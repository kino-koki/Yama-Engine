# 第三方基础设施 SPDX 署名清单（2026-11-27）

> 口径见 `docs/SELF_RESEARCH_BOUNDARY.md`：这些是**基础设施依赖**，
> 不是引擎功能面实现，未拷贝其源码充当自研。
> 每一项都由 `RepoContractTests` 强制覆盖 `YamaEngine.csproj` 的 PackageReference。
> 许可标注以各发行包内 LICENSE 为准；不确定处已明确标注。

## A. YamaEngine.csproj 的 PackageReference（逐项）

| Package | 版本 | 角色（基础设施） | SPDX |
|---|---|---|---|
| Silk.NET.Vulkan | 2.22.0 | Vulkan 1.4 P/Invoke 绑定 | MIT |
| Silk.NET.Vulkan.Extensions.EXT | 2.22.0 | Vulkan EXT 扩展绑定 | MIT |
| Silk.NET.Vulkan.Extensions.KHR | 2.22.0 | Vulkan KHR 扩展绑定 | MIT |
| Silk.NET.Core | 2.22.0 | 绑定公共运行时 | MIT |
| Silk.NET.Windowing | 2.22.0 | 窗口/输入平台层 | MIT |
| Vortice.Mathematics | 1.9.3 | 数学类型辅助（引擎另有自研 `RVec3`/`YrMat3`） | MIT |
| StbImageSharp | 2.22.5 | 图像解码绑定（音频解码器为自研） | MIT |
| LiteNetLib | 1.3.1 | UDP 可靠传输库 | MIT |
| Microsoft.CodeAnalysis.CSharp | 4.9.2 | Roslyn C# 编译器（脚本编译基础设施） | Apache-2.0 |
| Microsoft.CodeAnalysis.CSharp.Scripting | 4.9.2 | Roslyn 脚本宿主 | Apache-2.0 |
| Microsoft.CodeAnalysis | 4.9.2 | Roslyn 公共 API | Apache-2.0 |
| Microsoft.CodeAnalysis.Scripting | 4.9.2 | Roslyn 脚本 API | Apache-2.0 |
| ImGui.NET | 1.90.8.1 | 调试 UI 绑定 | MIT |

## B. 测试工程（非运行时功能面）

| Package | 版本 | SPDX |
|---|---|---|
| xunit | 2.9.3 | Apache-2.0 |
| xunit.runner.visualstudio | 2.8.2 | Apache-2.0 |
| Microsoft.NET.Test.Sdk | 17.11.1 | MIT |

## C. 仓库内二进制 / 原生 / 构建工具

| 项 | 路径 | 角色 | SPDX |
|---|---|---|---|
| 微软 DXC shader 编译器 | `third_party/dxc/x64/` | HLSL→DXIL 工具链 | Apache-2.0（以发行包 LICENSE 为准） |
| C++ 原生桥 | `YamaEngine.Native/CMakeLists.txt` | 引擎自研原生层构建脚本 | 项目自研 |
| Rust 数值热点（可选） | `YamaEngine.Rust/Cargo.toml`、`native/reality_physics/Cargo.toml` | 纯数值加速，独立可选 crate，CI 不编译 | 项目自研 |

## D. 内置 Code - OSS（第十四轮新增，第十九轮切换最轻量构建面）

| 项 | 来源 | 版本 / commit | SPDX |
|---|---|---|---|
| Code - OSS 源码 | https://github.com/microsoft/vscode | tag `1.148.0` @ `661626d0e72f97806550790a98fb87116c9db825` | MIT |
| Electron / Node.js（随 Code - OSS 构建） | upstream 依赖树 | 由 tag 的 `package.json` 锁定 | MIT（以各自 LICENSE 为准） |
| VSCodium 干净构建做法 | https://vscodium.com/ | 仅参考「去品牌/去遥测」流程，未拷贝其脚本 | MIT |

**最轻量构建面（第十九轮）**：`ide/codeoss/MINIMAL.lock.json` 把 1.148.0
（2026-11-24，当前最新稳定版）定义为 `web-only-minimal`：排除 Electron /
原生模块 / Marketplace / 遥测 / 品牌 / 内置扩展下载 / 更新服务；
`scripts/build_codeoss_minimal.py` 纯 stdlib 离线校验 6 个配置文件的 SHA-256；
`compiledInThisRepo=false` 是硬声明——本仓库**不编译** 1.8 GB 上游，
不分发未存在的 workbench 产物，不宣称 G4 闭合。

**许可边界**：GitHub 上的 `microsoft/vscode` 仓库是 MIT（Code - OSS）；
微软官方发行的 Visual Studio Code 构建时替换 `product.json`（品牌 / 遥测 /
扩展市场）并适用微软产品许可。本仓库内置的是**干净 MIT 构建**：
`ide/codeoss/product.json` 去品牌、去遥测端点、无 Marketplace gallery。
不得把官方 VS Code 二进制或商标随仓库再分发。

## E. Tuan-CLI（跨项目引用，非依赖）

| 项 | 来源 | SPDX |
|---|---|---|
| Tuan-CLI skill / hook / MCP 协议格式参考 | https://github.com/kino-koki/Tuan-CLI | MIT |

`integrations/tuan_cli/` 下的适配层代码为本项目自研；
**本轮未向 Tuan-CLI 仓库写入任何文件**（遵循其 `TUAN.md` 的逐次确认规则）。
