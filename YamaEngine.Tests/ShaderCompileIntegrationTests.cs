// ShaderCompileIntegrationTests.cs — 着色器编译集成验证套件
// 覆盖:
//   * MaterialGraph → ShaderGraphCompiler.Compile → 真实 HLSL 源码（VS/PS 入口）【CPU 确定性】
//   * BuildPipelineReal.CookShader → 格式良好、可解析的着色器 blob（magic + 变体 + 字节码）
//   * 变体/关键字 → 不同 define/关键字产生不同内容哈希/字节码
//   * ShaderCache 二次编译命中缓存
//   * 空变体列表 → 仅头部 blob
// 全部 CPU 级确定性，不依赖 GPU/真实设备（Compile 只生成 HLSL 源码，不生成 DXBC/SPIRV）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using YamaEngine.Build;
using YamaEngine.Materials;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

/// <summary>着色器编译集成验证套件。</summary>
public sealed class ShaderCompileIntegrationTests
{
    private const uint ShaderMagic = 0x53534844u; // 'SSHD'

    private static string SimpleHlsl =>
        "float4 main(float4 pos : POSITION) : SV_POSITION { return pos; }";

    /// <summary>材质图 → ShaderGraphCompiler 应生成含入口点的真实 HLSL 源码。</summary>
    public static void TestMaterialGraphCompilesToHlsl()
    {
        var graph = MaterialGraph.CreateDefaultPbr("ShaderBP");
        AssertTrue(graph.Validate().Count == 0, "默认 PBR 图应有效。");

        var compiler = new ShaderGraphCompiler();
        var result = compiler.Compile(graph, new ShaderCompileOptions { UseCache = false });

        AssertTrue(result.Success, $"编译应成功: {result.ErrorMessage}");
        AssertTrue(result.VertexShaderSource != null && result.VertexShaderSource.Contains("VSMain"),
            "VS 源码应含 VSMain 入口。");
        AssertTrue(result.PixelShaderSource != null && result.PixelShaderSource.Contains("PSMain"),
            "PS 源码应含 PSMain 入口。");
        AssertTrue(result.VertexShaderSource!.Contains("SV_POSITION") || result.VertexShaderSource.Contains("Position"),
            "VS 源码应输出位置语义。");
        AssertTrue(result.ContentHash.Length > 0, "应生成内容哈希。");
    }

    /// <summary>CookShader 应产出格式良好、可完整解析的着色器 blob。</summary>
    public static void TestCookShaderProducesWellFormedBlob()
    {
        var cooker = new AssetCookerReal(new BuildConfig());
        var variants = new Dictionary<string, string>[]
        {
            new() { ["QUALITY"] = "HIGH" },
            new() { ["QUALITY"] = "LOW" }
        };

        var blob = cooker.CookShader(SimpleHlsl, "main", new[] { "SM_6_6" }, variants);

        using var ms = new MemoryStream(blob);
        using var r = new BinaryReader(ms);
        AssertEqual(ShaderMagic, r.ReadUInt32(), "着色器 blob 应以 'SSHD' magic 开头。");
        var count = r.ReadUInt32();
        AssertEqual(2u, count, "应含 2 个变体。");
        for (int i = 0; i < count; i++)
        {
            var bytecodeLen = r.ReadUInt32();
            var keyLen = r.ReadUInt16();
            var key = Encoding.UTF8.GetString(r.ReadBytes(keyLen));
            var bytecode = r.ReadBytes((int)bytecodeLen);
            AssertTrue(bytecode.Length == (int)bytecodeLen, "变体字节码长度应匹配。");
            AssertTrue(key.Length > 0, $"变体 {i} 应含非空关键字串。");
        }
        AssertTrue(ms.Position == ms.Length, "blob 应被完整解析（无多余/缺失字节）。");
    }

    /// <summary>不同 define 变体应产生不同的编译字节码。</summary>
    public static void TestCookShaderVariantsDistinct()
    {
        var cooker = new AssetCookerReal(new BuildConfig());
        var v1 = cooker.CookShader(SimpleHlsl, "main", new[] { "SM_6_6" },
            new[] { new Dictionary<string, string> { ["X"] = "1" } });
        var v2 = cooker.CookShader(SimpleHlsl, "main", new[] { "SM_6_6" },
            new[] { new Dictionary<string, string> { ["X"] = "2" } });

        AssertTrue(!v1.SequenceEqual(v2), "不同 define 应产生不同字节码。");
    }

    /// <summary>不同启用关键字应产生不同的内容哈希（Permutation 变体）。</summary>
    public static void TestShaderCompileKeywordVariantDistinct()
    {
        var graph = MaterialGraph.CreateDefaultPbr("KeywordBP");
        var compiler = new ShaderGraphCompiler();

        var a = compiler.Compile(graph, new ShaderCompileOptions
        { Keywords = new HashSet<string> { "QUALITY_HIGH" }, UseCache = false });
        var b = compiler.Compile(graph, new ShaderCompileOptions
        { Keywords = new HashSet<string> { "QUALITY_LOW" }, UseCache = false });

        AssertTrue(a.Success && b.Success, "两次编译都应成功。");
        AssertTrue(a.ContentHash != b.ContentHash, "不同关键字应产生不同内容哈希。");
    }

    /// <summary>同图同选项二次编译应命中 ShaderCache。</summary>
    public static void TestShaderCacheHitOnSecondCompile()
    {
        var graph = MaterialGraph.CreateDefaultPbr("CacheBP");
        var compiler = new ShaderGraphCompiler();

        var first = compiler.Compile(graph, new ShaderCompileOptions { UseCache = true });
        var second = compiler.Compile(graph, new ShaderCompileOptions { UseCache = true });

        AssertTrue(first.Success && second.Success, "两次编译都应成功。");
        AssertTrue(second.CacheHit, "第二次编译应命中缓存。");
        AssertEqual(first.ContentHash, second.ContentHash, "缓存命中应返回相同内容哈希。");
    }

    /// <summary>空变体列表 → 仅头部（magic + 0 变体）。</summary>
    public static void TestCookShaderEmptyVariantListProducesHeaderOnly()
    {
        var cooker = new AssetCookerReal(new BuildConfig());
        var blob = cooker.CookShader(SimpleHlsl, "main", new[] { "SM_6_6" },
            Array.Empty<Dictionary<string, string>>());

        using var ms = new MemoryStream(blob);
        using var r = new BinaryReader(ms);
        AssertEqual(ShaderMagic, r.ReadUInt32(), "magic 应正确。");
        AssertEqual(0u, r.ReadUInt32(), "应为 0 个变体。");
        AssertTrue(ms.Position == ms.Length, "空变体列表应仅含头部。");
    }
}
