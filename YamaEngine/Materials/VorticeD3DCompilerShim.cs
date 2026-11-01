// =============================================================================
//  Yama Engine - Vortice.D3DCompiler Compat Shim
//  yama引擎 - Vortice.D3DCompiler 兼容垫片
//
//  The real Vortice.D3DCompiler package is NOT referenced in this build
//  (offline / not installed). This minimal shim provides the symbols the
//  material compiler references so the project compiles. At runtime the
//  Compile() call returns a failure result; MaterialCompiler.CompileShader
//  already falls back to a SHA-256 bytecode identifier in its catch block.
//
//  NOTE: Replace this shim with the real Vortice.D3DCompiler package when a
//  D3D12/Vulkan shader compilation backend is wired up (see MaterialSystemReal).
// =============================================================================

using System;
using System.Collections.Generic;

namespace Vortice.D3DCompiler
{
    /// <summary>预处理器宏（兼容 D3D 宏定义）。</summary>
    public struct Macro
    {
        public Macro(string name, string? definition)
        {
            Name = name;
            Definition = definition;
        }
        public string? Name;
        public string? Definition;
    }

    /// <summary>编译标志（占位，映射真实 D3DCompile 标志的子集）。</summary>
    [Flags]
    public enum CompileFlags
    {
        None = 0,
        Debug = 1 << 0,
        SkipValidation = 1 << 1,
        PackMatrixColumnMajor = 1 << 2,
        OptimizationLevel1 = 1 << 3,
        OptimizationLevel3 = 1 << 4,
        WarningsAreErrors = 1 << 5
    }

    /// <summary>编译结果（占位实现）。</summary>
    public sealed class CompilationResult : IDisposable
    {
        public CompilationResult(byte[] bytecode, bool hasErrors, string? errorMessage)
        {
            Bytecode = new Blob(bytecode);
            HasErrors = hasErrors;
            ErrorMessage = errorMessage;
        }

        /// <summary>编译后的着色器字节码（占位：空）。</summary>
        public Blob Bytecode { get; }

        /// <summary>是否存在错误。</summary>
        public bool HasErrors { get; }

        /// <summary>错误信息（若有）。</summary>
        public string? ErrorMessage { get; }

        public void Dispose() { }
    }

    /// <summary>字节码 Blob（占位）。</summary>
    public sealed class Blob : IDisposable
    {
        private readonly byte[] _data;
        public Blob(byte[] data) => _data = data ?? Array.Empty<byte>();
        public byte[] ToArray() => _data;
        public void Dispose() { }
    }

    /// <summary>静态编译器入口（占位）。</summary>
    public static class Compiler
    {
        /// <summary>
        /// 占位编译：离线环境下无法调用真实 D3DCompiler。
        /// 返回 hasErrors=true，由调用方回退到 SHA-256 标识。
        /// </summary>
        public static CompilationResult Compile(
            string source,
            string entryPoint,
            string profile,
            CompileFlags flags,
            Macro[]? macros = null)
        {
            return new CompilationResult(
                Array.Empty<byte>(),
                true,
                "Vortice.D3DCompiler shim: offline build has no real D3D compiler. " +
                "Wire up the Vortice.D3DCompiler package for actual HLSL compilation.");
        }
    }
}
