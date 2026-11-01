using System;
using System.Reflection;

class InspectTypes
{
    static Assembly LoadFromOutput(string dllName)
    {
        // 从程序输出目录加载（DLL 由 NuGet 还原后随构建复制到此），不再写死 E:\nuget 本地路径
        var path = Path.Combine(AppContext.BaseDirectory, dllName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"未在输出目录找到 {dllName}，请先还原 Silk.NET 2.22.0 (NuGet)。", path);
        return Assembly.LoadFrom(path);
    }

    static void Main()
    {
        // 加载 Silk.NET.Core
        var coreAsm = LoadFromOutput("Silk.NET.Core.dll");
        
        // 检查 APIVersion
        var apiVerType = coreAsm.GetType("Silk.NET.Core.APIVersion");
        if (apiVerType != null)
        {
            Console.WriteLine("=== APIVersion 构造函数 ===");
            foreach (var c in apiVerType.GetConstructors())
            {
                var ps = c.GetParameters();
                Console.WriteLine($"  APIVersion({string.Join(", ", Array.ConvertAll(ps, p => $"{p.ParameterType.Name} {p.Name}"))})");
            }
            Console.WriteLine("=== APIVersion 字段 ===");
            foreach (var f in apiVerType.GetFields())
                Console.WriteLine($"  {f.FieldType.Name} {f.Name}");
        }
        else
        {
            Console.WriteLine("APIVersion 不存在");
            // 检查 Version 类型
            var mathsAsm = LoadFromOutput("Silk.NET.Maths.dll");
            var verType = mathsAsm.GetType("Silk.NET.Maths.Version");
            if (verType != null)
            {
                Console.WriteLine("=== Silk.NET.Maths.Version 构造函数 ===");
                foreach (var c in verType.GetConstructors())
                {
                    var ps = c.GetParameters();
                    Console.WriteLine($"  Version({string.Join(", ", Array.ConvertAll(ps, p => $"{p.ParameterType.Name} {p.Name}"))})");
                }
            }
        }

        // 加载 Windowing.Common
        var winAsm = LoadFromOutput("Silk.NET.Windowing.Common.dll");
        var gaType = winAsm.GetType("Silk.NET.Windowing.GraphicsAPI");
        if (gaType != null)
        {
            Console.WriteLine("=== GraphicsAPI 构造函数 ===");
            foreach (var c in gaType.GetConstructors())
            {
                var ps = c.GetParameters();
                Console.WriteLine($"  GraphicsAPI({string.Join(", ", Array.ConvertAll(ps, p => $"{p.ParameterType.Name} {p.Name}"))})");
            }
        }
    }
}
