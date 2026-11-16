// AssemblyAttributes.cs —— 程序集级属性
// 授予 YamaEngine.Tests 程序集访问 internal 成员的权限（白盒引擎测试），
// 使测试工程能驱动场景生命周期（Initialize/Start/Update/Destroy 等内部方法）。

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("YamaEngine.Tests")]
