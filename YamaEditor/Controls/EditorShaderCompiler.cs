// =============================================================================
// EditorShaderCompiler.cs - 编辑器共享着色器编译助手
// 使用引擎的 DxcCompiler 将内联 HLSL 编译为 SPIR-V，替代旧的硬编码字节码方案。
// =============================================================================

using System;
using System.Diagnostics;
using System.IO;
using YamaEngine.RHI;
using YamaEngine.Shaders;

namespace YamaEditor.Controls
{
    /// <summary>
    /// 编辑器共享着色器编译器 —— DXC (HLSL→SPIR-V)。
    /// 所有编辑器渲染器共享同一个编译器实例与磁盘缓存。
    /// </summary>
    internal static class EditorShaderCompiler
    {
        private static DxcCompiler? _compiler;
        private static readonly object _lock = new();

        // 直通顶点着色器：NDC 位置 + 颜色直通（CPU 预变换管线用）
        public const string PassthroughVS = @"
struct VSInput
{
    [[vk::location(0)]] float2 inPos   : POSITION;
    [[vk::location(1)]] float3 inColor : COLOR;
};

struct VSOutput
{
    float4 pos   : SV_Position;
    float3 color : COLOR;
};

VSOutput VSMain(VSInput i)
{
    VSOutput o;
    o.pos   = float4(i.inPos, 0.0, 1.0);
    o.color = i.inColor;
    return o;
}
";

        // 直通片段着色器：插值颜色直接输出
        public const string PassthroughFS = @"
struct VSOutput
{
    float4 pos   : SV_Position;
    float3 color : COLOR;
};

float4 PSMain(VSOutput i) : SV_Target0
{
    return float4(i.color, 1.0);
}
";

        /// <summary>编译任意 HLSL 片段（SM 6.0 → SPIR-V）。</summary>
        public static RHIShaderBytecode Compile(string hlsl, string entryPoint, RHIShaderStage stage, string name)
        {
            if (_compiler == null)
            {
                lock (_lock)
                {
                    if (_compiler == null)
                    {
                        var baseDir = AppContext.BaseDirectory;
                        _compiler = new DxcCompiler(
                            Path.Combine(baseDir, "Shaders"),
                            Path.Combine(baseDir, "ShaderCache"),
                            useDebug: Debugger.IsAttached);
                    }
                }
            }
            return _compiler.Compile(hlsl, entryPoint, stage, RHIShaderModel.SM_6_0, name);
        }

        /// <summary>直通 VS（NDC pos + color）。</summary>
        public static RHIShaderBytecode PassthroughVertex() =>
            Compile(PassthroughVS, "VSMain", RHIShaderStage.Vertex, "EditorPassthroughVS");

        /// <summary>直通 FS（color → SV_Target0）。</summary>
        public static RHIShaderBytecode PassthroughPixel() =>
            Compile(PassthroughFS, "PSMain", RHIShaderStage.Pixel, "EditorPassthroughFS");
    }
}
