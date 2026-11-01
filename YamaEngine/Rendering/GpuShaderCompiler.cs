// ════════════════════════════════════════════════════════════════════
// GpuShaderCompiler.cs — GPU shader 编译钩子
//
// 把 GLSL 源码编译为 SPIR-V（真实 GPU 消费格式）。
//   1. 优先复用 VulkanShaderManager 的 shaderc P/Invoke（shaderc_shared.dll）；
//   2. 不可用时回退到 glslangValidator（若在 PATH）；
//   3. 均不可用时进入 source-only 模式（headless/CI 无本地工具链）。
//
// 确定性：无外部工具链时稳定返回 source-only，不抛异常。
// ════════════════════════════════════════════════════════════════════

using System;
using System.Diagnostics;
using YamaEngine.Vulkan;

namespace YamaEngine.Rendering;

/// <summary>GPU shader 编译结果。</summary>
public readonly struct GpuShaderCompileResult
{
    public readonly bool Success;
    public readonly byte[]? Spirv;
    public readonly string? Error;
    public readonly long CompileTimeMs;
    public readonly bool IsSourceOnly; // true = 无工具链，仅保留源码

    public GpuShaderCompileResult(bool success, byte[]? spirv, string? error,
        long compileTimeMs, bool isSourceOnly)
    {
        Success = success; Spirv = spirv; Error = error;
        CompileTimeMs = compileTimeMs; IsSourceOnly = isSourceOnly;
    }

    public override string ToString() => Success
        ? $"OK ({Spirv?.Length ?? 0} bytes, {CompileTimeMs}ms)"
        : $"{(IsSourceOnly ? "SOURCE_ONLY" : "FAIL")}: {Error}";
}

/// <summary>GPU shader 编译器：GLSL → SPIR-V（shaderc / glslang / source-only）。</summary>
public static class GpuShaderCompiler
{
    /// <summary>无工具链时的说明（headless 环境）。</summary>
    public const string SourceOnlyReason = "shader compiler unavailable (source-only mode)";

    private static bool? _shadercAvailable;
    private static readonly object _lock = new();

    /// <summary>shaderc_shared.dll 是否可用（惰性检测，缓存）。</summary>
    public static bool IsShadercAvailable
    {
        get
        {
            if (_shadercAvailable.HasValue) return _shadercAvailable.Value;
            lock (_lock)
            {
                if (_shadercAvailable.HasValue) return _shadercAvailable.Value;
                _shadercAvailable = ProbeShaderc();
                return _shadercAvailable.Value;
            }
        }
    }

    private static bool ProbeShaderc()
    {
        try
        {
            using var c = new ShadercCompiler(ShaderOptimizationLevel.None);
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or InvalidOperationException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// 编译 GLSL 源码为 SPIR-V。无工具链时返回 Success=false 且 IsSourceOnly=true（不抛异常）。
    /// </summary>
    public static GpuShaderCompileResult TryCompileToSpirv(string glsl, ShaderStageExt stage,
        string entryPoint = "main", string fileName = "shader.glsl")
    {
        if (string.IsNullOrWhiteSpace(glsl))
            return new GpuShaderCompileResult(false, null, "empty shader source", 0, false);

        if (IsShadercAvailable)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var compiler = new ShadercCompiler(ShaderOptimizationLevel.Performance);
                var r = compiler.Compile(glsl, stage, fileName, entryPoint);
                sw.Stop();
                if (r.Success && r.Spirv != null && r.Spirv.Length > 0)
                    return new GpuShaderCompileResult(true, r.Spirv, null, r.CompileTimeMs, false);
                return new GpuShaderCompileResult(false, null,
                    r.ErrorMsg ?? "shaderc compile failed", r.CompileTimeMs, false);
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new GpuShaderCompileResult(false, null,
                    $"shaderc exception: {ex.Message}", sw.ElapsedMilliseconds, false);
            }
        }

        // shaderc 不可用 → glslangValidator 探测
        if (TryCompileWithGlslang(glsl, stage, entryPoint, out var spirv, out var err))
            return new GpuShaderCompileResult(true, spirv, null, 0, false);

        return new GpuShaderCompileResult(false, null, SourceOnlyReason, 0, true);
    }

    private static bool TryCompileWithGlslang(string glsl, ShaderStageExt stage,
        string entryPoint, out byte[]? spirv, out string? error)
    {
        spirv = null; error = null;
        try
        {
            string stageFlag = stage == ShaderStageExt.Vertex ? "vert" : "frag";
            var psi = new ProcessStartInfo("glslangValidator")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add($"-S{stageFlag}");
            psi.ArgumentList.Add("--stdin");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("--stdout");

            using var proc = Process.Start(psi);
            if (proc == null) return false;
            proc.StandardInput.Write(glsl);
            proc.StandardInput.Close();
            var stdout = proc.StandardOutput.BaseStream;
            using var ms = new MemoryStream();
            stdout.CopyTo(ms);
            proc.WaitForExit();
            string errOut = proc.StandardError.ReadToEnd();

            if (proc.ExitCode == 0 && ms.Length > 0)
            {
                spirv = ms.ToArray();
                return true;
            }
            error = errOut.Length > 0 ? errOut.Trim() : "glslangValidator failed";
            return false;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            error = "glslangValidator not found";
            return false;
        }
    }
}
