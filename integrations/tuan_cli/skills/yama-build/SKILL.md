---
name: Yama 引擎构建
description: 用 -warnaserror 构建 Yama-Engine 三个托管项目并核对 0 警告门禁，Windows/.NET 10 环境
activation: auto
triggers: yama 构建, yama build, 编译引擎, -warnaserror
provides: yama-build
suggests: yama-physics-gate, yama-sota-audit
priority: 80
updated_at: 0
use_count: 0
---
# Yama 引擎构建

按仓库真实门禁跑，不要用「能编译就行」的宽松命令。

1. 确认工作目录是 Yama-Engine 仓库根（有 `docs/capability_matrix.csv`）。
2. 依次 restore 三个工程：`YamaEngine/YamaEngine.csproj`、
   `YamaEngine.Tests/YamaEngine.Tests.csproj`、`YamaCLI/YamaCLI.csproj`。
3. 用 `dotnet build <proj> -c Debug -p:Platform=x64 --no-restore -warnaserror`
   逐个构建；任何未抑制警告都必须当失败处理，不得靠加 NoWarn 掩盖。
4. 读 `.github/workflows/build.yml` 核对门禁口径未漂移；若 CI 红，
   先回读失败注解定位真实用例，不删断言、不放宽容差。
5. 收尾报告写明：变更文件、CI run id、Build/Test/Smoke 各自 conclusion。

约束：不改 `Directory.Build.props` 的 NoWarn 清单来制造假绿；
不把 `YamaEditor`（WinUI 3）/ C++ 原生桥硬塞进无头门禁。
