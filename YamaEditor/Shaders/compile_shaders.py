#!/usr/bin/env python3
"""
Yama Engine - SPIR-V 着色器编译脚本
将 GLSL 源文件编译为 SPIR-V 字节码，并生成 C# 嵌入代码。

使用方法:
    python compile_shaders.py

依赖:
    - glslangValidator 或 glslc (Vulkan SDK 自带)

输出:
    - *.spv 文件 (SPIR-V 字节码)
    - ShaderSPIRV.cs (C# 嵌入代码，含 uint[] 数组)
"""

import os
import subprocess
import sys
import struct

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
SHADERS = [
    ("passthrough.vert", "vert"),
    ("passthrough.frag", "frag"),
    ("mesh.vert", "vert"),
    ("mesh.frag", "frag"),
]

def find_compiler():
    """查找 GLSL 编译器"""
    candidates = [
        "glslangValidator",
        "glslc",
        os.path.join(os.environ.get("VULKAN_SDK", ""), "Bin", "glslangValidator.exe"),
        os.path.join(os.environ.get("VULKAN_SDK", ""), "Bin", "glslc.exe"),
    ]
    for c in candidates:
        try:
            subprocess.run([c, "--version"], capture_output=True, check=True)
            return c
        except (FileNotFoundError, subprocess.CalledProcessError):
            continue
    return None

def compile_shader(compiler, source_path, stage):
    """编译单个着色器"""
    output_path = source_path.replace(".vert", ".spv").replace(".frag", ".spv")

    if os.path.basename(compiler) == "glslc":
        cmd = [compiler, "-fshader-stage=" + stage, source_path, "-o", output_path, "--target-env=vulkan1.3"]
    else:
        cmd = [compiler, "-V", source_path, "-o", output_path]

    print(f"  编译: {os.path.basename(source_path)} -> {os.path.basename(output_path)}")
    result = subprocess.run(cmd, capture_output=True, text=True)

    if result.returncode != 0:
        print(f"  错误: {result.stderr}")
        return None

    return output_path

def spv_to_csharp(spv_path, var_name):
    """将 SPIR-V 文件转为 C# uint[] 代码"""
    with open(spv_path, "rb") as f:
        data = f.read()

    words = struct.unpack(f"<{len(data)//4}I", data)

    lines = []
    lines.append(f"        // {os.path.basename(spv_path)} - {len(words)} words, {len(data)} bytes")
    lines.append(f"        internal static readonly uint[] {var_name} = {{")

    for i in range(0, len(words), 6):
        chunk = words[i:i+6]
        hex_vals = ", ".join(f"0x{w:08X}" for w in chunk)
        lines.append(f"            {hex_vals},")

    lines.append("        };")
    return "\n".join(lines)

def main():
    print("=" * 60)
    print("  Yama Engine - SPIR-V 着色器编译脚本")
    print("=" * 60)

    compiler = find_compiler()
    if not compiler:
        print("错误: 未找到 glslangValidator 或 glslc")
        print("请安装 Vulkan SDK: https://vulkan.lunarg.com/")
        sys.exit(1)

    print(f"使用编译器: {compiler}")
    print()

    csharp_blocks = []

    for source_name, stage in SHADERS:
        source_path = os.path.join(SCRIPT_DIR, source_name)
        if not os.path.exists(source_path):
            print(f"  跳过: {source_name} (文件不存在)")
            continue

        spv_path = compile_shader(compiler, source_path, stage)
        if spv_path is None:
            continue

        # 生成 C# 变量名
        base = os.path.splitext(source_name)[0]
        var_name = base.replace(".", "_") + "_spv"
        csharp_blocks.append(spv_to_csharp(spv_path, var_name))

    # 生成 C# 文件
    cs_path = os.path.join(SCRIPT_DIR, "..", "Controls", "BuiltinShaderSPIRV.cs")
    cs_content = """// =============================================================================
// BuiltinShaderSPIRV.cs - 自动生成的内置着色器 SPIR-V 字节码
// 此文件由 compile_shaders.py 自动生成，请勿手动编辑。
// =============================================================================

namespace YamaEditor.Controls
{
    internal static class BuiltinShaderSPIRV
    {
"""
    cs_content += "\n\n".join(csharp_blocks)
    cs_content += "    }\n}\n"

    with open(cs_path, "w", encoding="utf-8") as f:
        f.write(cs_content)

    print()
    print(f"C# 代码已生成: {cs_path}")
    print("=" * 60)
    print("  编译完成!")
    print("=" * 60)

if __name__ == "__main__":
    main()
