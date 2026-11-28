---
name: Yama 回归门禁
description: 运行 Yama 的 xUnit 反射套件与无头 smoke，核对物理断言数并回读 CI 失败注解
activation: auto
triggers: yama 测试, yama test, 回归门禁, 物理断言
provides: yama-test
suggests: yama-physics-gate, yama-sota-audit
priority: 82
updated_at: 0
use_count: 0
---
# Yama 回归门禁

1. `dotnet test YamaEngine.Tests/YamaEngine.Tests.csproj -c Debug -p:Platform=x64 --no-build`。
2. `dotnet run --project YamaCLI/YamaCLI.csproj -c Debug -- smoke 60`。
3. 核对反射发现的是 `public static void Test*` 方法（`XunitAdapter.cs`），
   任何异常都原样重抛；`KnownLimitations.cs` 必须是空清单，否则视为看板未清零。
4. 若失败，从 check-run annotations 回读精确用例名与错误，不改测试构造去凑绿。
5. 报告物理断言总数（第十三/十四轮后为 210 项）与音频用例数（5 个）。

约束：禁止用 Skip、吞异常、KnownLimitations 隔离来制造绿灯。
