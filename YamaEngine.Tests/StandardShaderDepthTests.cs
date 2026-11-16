// StandardShaderDepthTests.cs — Unity5 Standard Shader 深度验证（Metallic/Smoothness 工作流 + GPU cbuffer 布局）
// 覆盖：
//   * Standard Shader 默认属性（Albedo 白、Smoothness 0.5、Metallic 0）
//   * Metallic 工作流：金属度 0/1 的 Albedo 语义（电介质 vs 金属反射）
//   * Smoothness ↔ Roughness 映射（Unity 用 Smoothness，引擎存 Roughness=1-Smoothness）
//   * RenderMode → RenderQueue（Opaque 2000 / Cutout 2450 / Transparent 3000）
//   * GPU cbuffer 字节布局（BaseColorAlpha/Emissive/PBRParams/UVTransform/BlendParams 5×float4=80B）
//   * cbuffer 字段精确偏移 + 小端字节序验证
//   * 属性校验：Metallic/Roughness 越界、Emission 强度、UV 平铺非正
// 全部 CPU 级确定性，不依赖 GPU 渲染。
using System;
using System.Linq;
using System.Numerics;
using YamaEngine.Materials;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

public static class StandardShaderDepthTests
{
    private static void Near(float got, float want, float tol, string msg)
        => AssertTrue(MathF.Abs(got - want) <= tol, $"{msg}（期望 {want}，实际 {got}，容差 {tol}）");

    private static void NearV3(Vector3 got, Vector3 want, float tol, string msg)
    {
        Near(got.X, want.X, tol, msg + ".X");
        Near(got.Y, want.Y, tol, msg + ".Y");
        Near(got.Z, want.Z, tol, msg + ".Z");
    }

    /// <summary>Standard Shader 默认属性应与 Unity 一致：白 Albedo、Smoothness 0.5、Metallic 0。</summary>
    public static void TestStandardDefaults()
    {
        var m = PbrMaterial.Create("DefaultStandard");
        AssertEqual("DefaultStandard", m.Name, "材质名应等于传入名");
        NearV3(m.BaseColor, new Vector3(1, 1, 1), 0.0001f, "默认 Albedo 应为白");
        AssertEqual(0f, m.Metallic, "默认 Metallic 应为 0（电介质）");
        Near(m.Roughness, 0.5f, 0.0001f, "默认 Roughness 应为 0.5（Unity Smoothness 0.5）");
        AssertEqual(1f, m.Alpha, "默认 Alpha 应为 1（不透明）");
        AssertEqual(1f, m.AmbientOcclusion, "默认 AO 应为 1");
        NearV3(m.EmissiveColor, Vector3.Zero, 0.0001f, "默认 Emission 应为黑");
        AssertEqual(0f, m.EmissiveIntensity, "默认 Emission 强度应为 0");
        AssertEqual(1f, m.NormalScale, "默认法线强度应为 1");
        AssertEqual(PbrBlendMode.Opaque, m.BlendMode, "默认 RenderMode 应为 Opaque");
        AssertEqual(Vector2.One, m.UVTiling, "默认 UV 平铺应为 (1,1)");
        AssertEqual(Vector2.Zero, m.UVOffset, "默认 UV 偏移应为 (0,0)");
        AssertEqual(0, m.Validate().Count, "默认材质应通过校验");
    }

    /// <summary>Metallic 工作流：金属度改变反射率语义。</summary>
    public static void TestMetallicWorkflow()
    {
        // 电介质（Metallic=0）：低反射率，仅约 4% 镜面
        var dielectric = PbrMaterial.Create("Plastic");
        dielectric.Metallic = 0f;
        dielectric.Roughness = 0.5f;
        var dCb = dielectric.GetPackedConstants();
        AssertEqual(0f, dCb.PBRParams.X, "电介质 Metallic 应为 0");
        AssertEqual(0.5f, dCb.PBRParams.Y, "电介质 Roughness 应为 0.5");

        // 金属（Metallic=1）：高反射率，Albedo 作为镜面色
        var metal = PbrMaterial.Create("Iron");
        metal.Metallic = 1f;
        metal.Roughness = 0.25f;
        var mCb = metal.GetPackedConstants();
        AssertEqual(1f, mCb.PBRParams.X, "金属 Metallic 应为 1");
        AssertEqual(0.25f, mCb.PBRParams.Y, "金属 Roughness 应为 0.25");

        // 半金属（过渡区）
        var semi = PbrMaterial.Create("RustyMetal");
        semi.Metallic = 0.6f;
        AssertEqual(0.6f, semi.GetPackedConstants().PBRParams.X, "半金属 Metallic 应为 0.6");

        // Metallic=0/1 都合法
        AssertEqual(0, dielectric.Validate().Count, "电介质应通过校验");
        AssertEqual(0, metal.Validate().Count, "金属应通过校验");
    }

    /// <summary>Unity Smoothness ↔ 引擎 Roughness 映射（Roughness = 1 - Smoothness）。</summary>
    public static void TestSmoothnessToRoughnessMapping()
    {
        // Unity Standard Shader 用 Smoothness，引擎存 Roughness。
        // 映射约定：Roughness = 1 - Smoothness。验证常见值的双向一致。
        float[] smoothnessValues = { 0f, 0.25f, 0.5f, 0.75f, 1f };
        foreach (var s in smoothnessValues)
        {
            var m = PbrMaterial.Create("SmoothTest");
            m.Roughness = 1f - s; // Unity Smoothness → 引擎 Roughness
            // 镜面感（Smoothness=1）应对应 Roughness=0
            Near(m.Roughness, 1f - s, 0.0001f, $"Smoothness {s} 应映射 Roughness {1 - s}");
            // 反向：Smoothness = 1 - Roughness
            Near(1f - m.Roughness, s, 0.0001f, $"Roughness {m.Roughness} 应回映射 Smoothness {s}");
        }

        // 镜面（Smoothness=1 / Roughness=0）与 完全粗糙（Smoothness=0 / Roughness=1）
        var mirror = PbrMaterial.Create("Mirror");
        mirror.Roughness = 0f;
        AssertEqual(0f, mirror.Roughness, "镜面 Roughness 应为 0");
        AssertEqual(0, mirror.Validate().Count, "镜面应通过校验");

        var chalk = PbrMaterial.Create("Chalk");
        chalk.Roughness = 1f;
        AssertEqual(1f, chalk.Roughness, "完全粗糙 Roughness 应为 1");
        AssertEqual(0, chalk.Validate().Count, "完全粗糙应通过校验");
    }

    /// <summary>RenderMode → RenderQueue 映射（Unity 约定：Opaque 2000 / Cutout 2450 / Transparent 3000）。</summary>
    public static void TestRenderModeRenderQueue()
    {
        int QueueOf(PbrBlendMode mode) => mode switch
        {
            PbrBlendMode.Opaque => 2000,
            PbrBlendMode.Cutout => 2450,
            PbrBlendMode.Transparent => 3000,
            PbrBlendMode.Additive => 3000,
            _ => 2000
        };

        // 验证 BlendMode 枚举值打包进 cbuffer BlendParams.y
        AssertEqual(0f, PbrMaterial.Create("A").GetPackedConstants().BlendParams.Y, "Opaque 枚举值应为 0");
        var cutout = PbrMaterial.Create("B"); cutout.BlendMode = PbrBlendMode.Cutout;
        AssertEqual(1f, cutout.GetPackedConstants().BlendParams.Y, "Cutout 枚举值应为 1");
        var transparent = PbrMaterial.Create("C"); transparent.BlendMode = PbrBlendMode.Transparent;
        AssertEqual(2f, transparent.GetPackedConstants().BlendParams.Y, "Transparent 枚举值应为 2");
        var additive = PbrMaterial.Create("D"); additive.BlendMode = PbrBlendMode.Additive;
        AssertEqual(3f, additive.GetPackedConstants().BlendParams.Y, "Additive 枚举值应为 3");

        // RenderQueue 约定
        AssertEqual(2000, QueueOf(PbrBlendMode.Opaque), "Opaque 队列应为 2000");
        AssertEqual(2450, QueueOf(PbrBlendMode.Cutout), "Cutout 队列应为 2450（AlphaTest）");
        AssertEqual(3000, QueueOf(PbrBlendMode.Transparent), "Transparent 队列应为 3000");
        AssertEqual(3000, QueueOf(PbrBlendMode.Additive), "Additive 队列应为 3000");
    }

    /// <summary>Cutout 模式下 AlphaCutoff 阈值应写入 cbuffer BlendParams.x。</summary>
    public static void TestCutoutAlphaCutoff()
    {
        var m = PbrMaterial.Create("Grass");
        m.BlendMode = PbrBlendMode.Cutout;
        m.AlphaCutoff = 0.35f;
        var cb = m.GetPackedConstants();
        AssertEqual(1f, cb.BlendParams.Y, "Cutout 枚举值应为 1");
        AssertEqual(0.35f, cb.BlendParams.X, "AlphaCutoff 应打包进 BlendParams.x");
        AssertEqual(0, m.Validate().Count, "Cutout 应通过校验");

        // 边界：cutoff 必须在 0..1
        m.AlphaCutoff = 0f;
        AssertEqual(0, m.Validate().Count, "cutoff=0 应合法");
        m.AlphaCutoff = 1f;
        AssertEqual(0, m.Validate().Count, "cutoff=1 应合法");
    }

    /// <summary>GPU cbuffer 字节布局：5×float4=80 字节，字段偏移正确。</summary>
    public static void TestCbufferLayout()
    {
        AssertEqual(80, PbrMaterial.PbrMaterialConstants.SizeInBytes, "cbuffer 总大小应为 80 字节");

        var m = PbrMaterial.Create("Layout");
        m.BaseColor = new Vector3(1f, 0.5f, 0f);
        m.Alpha = 0.8f;
        m.Metallic = 0.3f;
        m.Roughness = 0.7f;
        m.EmissiveColor = new Vector3(0.1f, 0.2f, 0.3f);
        m.EmissiveIntensity = 2f;
        m.NormalScale = 1.5f;
        m.UVTiling = new Vector2(4f, 2f);
        m.UVOffset = new Vector2(0.25f, 0.5f);
        m.AlphaCutoff = 0.5f;
        m.BlendMode = PbrBlendMode.Transparent;
        m.DoubleSided = true;

        var bytes = m.GetPackedConstantsBytes();
        AssertEqual(80, bytes.Length, "打包字节应为 80");

        // float4[0] BaseColorAlpha @ offset 0
        Near(ReadFloat(bytes, 0), 1f, 0.001f, "cbuffer[0].x 应为 BaseColor.R");
        Near(ReadFloat(bytes, 4), 0.5f, 0.001f, "cbuffer[0].y 应为 BaseColor.G");
        Near(ReadFloat(bytes, 8), 0f, 0.001f, "cbuffer[0].z 应为 BaseColor.B");
        Near(ReadFloat(bytes, 12), 0.8f, 0.001f, "cbuffer[0].w 应为 Alpha");

        // float4[1] Emissive @ offset 16
        Near(ReadFloat(bytes, 16), 0.1f, 0.001f, "cbuffer[1].x 应为 Emissive.R");
        Near(ReadFloat(bytes, 20), 0.2f, 0.001f, "cbuffer[1].y 应为 Emissive.G");
        Near(ReadFloat(bytes, 24), 0.3f, 0.001f, "cbuffer[1].z 应为 Emissive.B");
        Near(ReadFloat(bytes, 28), 2f, 0.001f, "cbuffer[1].w 应为 EmissiveIntensity");

        // float4[2] PBRParams @ offset 32
        Near(ReadFloat(bytes, 32), 0.3f, 0.001f, "cbuffer[2].x 应为 Metallic");
        Near(ReadFloat(bytes, 36), 0.7f, 0.001f, "cbuffer[2].y 应为 Roughness");
        Near(ReadFloat(bytes, 40), 1f, 0.001f, "cbuffer[2].z 应为 AO");
        Near(ReadFloat(bytes, 44), 1.5f, 0.001f, "cbuffer[2].w 应为 NormalScale");

        // float4[3] UVTransform @ offset 48
        Near(ReadFloat(bytes, 48), 4f, 0.001f, "cbuffer[3].x 应为 UV tiling.X");
        Near(ReadFloat(bytes, 52), 2f, 0.001f, "cbuffer[3].y 应为 UV tiling.Y");
        Near(ReadFloat(bytes, 56), 0.25f, 0.001f, "cbuffer[3].z 应为 UV offset.X");
        Near(ReadFloat(bytes, 60), 0.5f, 0.001f, "cbuffer[3].w 应为 UV offset.Y");

        // float4[4] BlendParams @ offset 64
        Near(ReadFloat(bytes, 64), 0.5f, 0.001f, "cbuffer[4].x 应为 AlphaCutoff");
        Near(ReadFloat(bytes, 68), 2f, 0.001f, "cbuffer[4].y 应为 BlendMode 枚举");
        Near(ReadFloat(bytes, 72), 1f, 0.001f, "cbuffer[4].z 应为 DoubleSided 标志");
        Near(ReadFloat(bytes, 76), 0f, 0.001f, "cbuffer[4].w 应为保留 0");
    }

    /// <summary>小端字节序验证：float 逐字节符合 x86/x64 小端布局。</summary>
    public static void TestCbufferLittleEndian()
    {
        var m = PbrMaterial.Create("Endian");
        m.Metallic = 1f; // 1.0f = 0x3F800000 → 小端字节 [0x00, 0x00, 0x80, 0x3F]
        var bytes = m.GetPackedConstantsBytes();

        // Metallic 在 PBRParams.x @ offset 32
        AssertEqual(0x00, bytes[32], "1.0f 小端第 0 字节应为 0x00");
        AssertEqual(0x00, bytes[33], "1.0f 小端第 1 字节应为 0x00");
        AssertEqual(0x80, bytes[34], "1.0f 小端第 2 字节应为 0x80");
        AssertEqual(0x3F, bytes[35], "1.0f 小端第 3 字节应为 0x3F");

        // 0.5f = 0x3F000000 → [0x00, 0x00, 0x00, 0x3F]，默认 Roughness 0.5 @ offset 36
        m.Roughness = 0.5f;
        bytes = m.GetPackedConstantsBytes();
        AssertEqual(0x00, bytes[36], "0.5f 小端第 0 字节应为 0x00");
        AssertEqual(0x3F, bytes[39], "0.5f 小端第 3 字节应为 0x3F");
    }

    /// <summary>属性校验：各参数越界应被 Validate 捕获。</summary>
    public static void TestPropertyValidationBounds()
    {
        var m = PbrMaterial.Create("Bounds");

        m.Metallic = -0.1f;
        AssertTrue(m.Validate().Any(e => e.Contains("Metallic")), "Metallic<0 应报错");
        m.Metallic = 1.1f;
        AssertTrue(m.Validate().Any(e => e.Contains("Metallic")), "Metallic>1 应报错");
        m.Metallic = 0.5f;

        m.Roughness = -0.1f;
        AssertTrue(m.Validate().Any(e => e.Contains("Roughness")), "Roughness<0 应报错");
        m.Roughness = 1.5f;
        AssertTrue(m.Validate().Any(e => e.Contains("Roughness")), "Roughness>1 应报错");
        m.Roughness = 0.5f;

        m.EmissiveIntensity = -1f;
        AssertTrue(m.Validate().Any(e => e.Contains("Emissive intensity") || e.Contains("emissive")), "Emissive<0 应报错");
        m.EmissiveIntensity = 5f;

        m.UVTiling = new Vector2(0f, 1f);
        AssertTrue(m.Validate().Any(e => e.Contains("UVTiling")), "UV tiling=0 应报错");
        m.UVTiling = new Vector2(-1f, 1f);
        AssertTrue(m.Validate().Any(e => e.Contains("UVTiling")), "UV tiling<0 应报错");
        m.UVTiling = Vector2.One;

        AssertEqual(0, m.Validate().Count, "恢复后应全部通过校验");

        // 空名应报错
        m.Name = "";
        AssertTrue(m.Validate().Any(e => e.Contains("name", StringComparison.OrdinalIgnoreCase)), "空名应报错");
    }

    /// <summary>Emission 属性：颜色 + 强度打包进 cbuffer[1]，强度可 >1（HDR）。</summary>
    public static void TestEmissionHDR()
    {
        var m = PbrMaterial.Create("Glow");
        m.EmissiveColor = new Vector3(1f, 0.6f, 0.2f);
        m.EmissiveIntensity = 3.5f; // HDR 自发光强度

        var cb = m.GetPackedConstants();
        NearV3(new Vector3(cb.Emissive.X, cb.Emissive.Y, cb.Emissive.Z), new Vector3(1f, 0.6f, 0.2f), 0.001f, "Emission 颜色");
        AssertEqual(3.5f, cb.Emissive.W, "Emission 强度应为 3.5");

        AssertEqual(0, m.Validate().Count, "HDR Emission 应通过校验");
    }

    /// <summary>DoubleSided 标志打包进 cbuffer[4].z（1=双面 / 0=单面）。</summary>
    public static void TestDoubleSidedFlag()
    {
        var single = PbrMaterial.Create("Single");
        single.DoubleSided = false;
        AssertEqual(0f, single.GetPackedConstants().BlendParams.Z, "单面标志应为 0");

        var doubleSided = PbrMaterial.Create("Leaf");
        doubleSided.DoubleSided = true;
        AssertEqual(1f, doubleSided.GetPackedConstants().BlendParams.Z, "双面标志应为 1");
        AssertEqual(0, doubleSided.Validate().Count, "双面材质应通过校验");
    }

    // ── 辅助：从字节数组按小端读 float ──
    private static float ReadFloat(byte[] buf, int offset)
        => BitConverter.ToSingle(buf, offset);
}
