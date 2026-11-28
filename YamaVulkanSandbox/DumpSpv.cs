using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using YamaVulkanSandbox;

// Dump actual bytecode from the loaded assembly in a fresh process
var asm = typeof(VulkanTriangleRenderer).Assembly;
Console.WriteLine($"Assembly: {asm.Location}");

var f = typeof(VulkanTriangleRenderer).GetField("VertexShaderSpv",
    BindingFlags.NonPublic | BindingFlags.Static);
var v = (uint[])f!.GetValue(null)!;
Console.WriteLine($"VertexShaderSpv length = {v.Length}");
Console.WriteLine($"Words 115..135:");
for (int i = 115; i < Math.Min(135, v.Length); i++)
    Console.WriteLine($"  w{i}: 0x{v[i]:X8}");

var ff = typeof(VulkanTriangleRenderer).GetField("FragmentShaderSpv",
    BindingFlags.NonPublic | BindingFlags.Static);
var fv = (uint[])ff!.GetValue(null)!;
Console.WriteLine($"FragmentShaderSpv length = {fv.Length}");
Console.WriteLine($"Words 72..85:");
for (int i = 72; i < Math.Min(85, fv.Length); i++)
    Console.WriteLine($"  w{i}: 0x{fv[i]:X8}");
