# yama游戏引擎 Yama GamesEngine（仓库名：Yama-Engine）

> 使用 C# (.NET 8) 从零构建的游戏/建模引擎原型，目标对标 UE5 级渲染管线，同时保持轻量级一体化工作流。

![License](https://img.shields.io/badge/license-MIT-blue.svg)
![.NET](https://img.shields.io/badge/.NET-8.0-512bd4.svg)
![Vulkan](https://img.shields.io/badge/Vulkan-1.4-b71b1b.svg)
![Platform](https://img.shields.io/badge/platform-Windows%20x64-0078d4.svg)
![Build](https://img.shields.io/badge/build-PASSING-brightgreen.svg)

---

## ✅ 当前真实构建状态（2026-11-01 实测）

**全部托管 C# 项目均以 0 错误通过 `dotnet build`。** 以下是 2026-11-01 逐项目实测结果：

| 项目 | 类型 | 编译错误 |
|---|---|---|
| `YamaEngine`（引擎核心库） | C# net8.0 | **0** |
| `YamaEditor`（WinUI 3 编辑器） | C# net10.0-windows | **0** |
| `YamaEngine.Tests`（单元测试） | C# net8.0 | **0** |
| `YamaRuntime` | C# net8.0 | **0** |
| `BlenderBridge` | C# net8.0 | **0** |
| `YamaCel2DSandbox` / `YamaVulkanSandbox` | C# net8.0 | **0** |
| `YamaEngineBridge`（C++ 原生桥接） | C++ vcxproj | 需 Visual Studio MSBuild（`dotnet CLI` 无法导入 `Microsoft.Cpp.Default.props`，属构建环境限制，非代码错误） |

> **历史轨迹**：2026-08-07 引擎库实测 1414 个编译错误（177/766 文件受影响）→ 2026-08-22 降至 374 → 逐步清零。2026-11-01 完成 `YamaEngine.Tests`（115→0，commit `2f4f8b8`）与 `YamaEditor`（56→0，commit `b900c87`）收尾，实现**全部托管项目 0 错误**。
>
> 仓库路径：`G:\Yama-Engine`（原名 `G:\Shanyu GamesEngine and Modeling`，2026-11 更名）。

---

## 项目是什么

一个以 C#/.NET 8 为主干、目标对标 UE5 渲染管线的**桌面端游戏/建模引擎原型**。架构意图清晰：RHI 抽象层 + 单一 Vulkan 1.4 后端、数据驱动的项目/资产/场景管线、一体化编辑器工作流。

**真正的亮点（已抽读代码确认，是真写的）：**
- **渲染底层**：Vulkan 交换链/管线真实实现；`TriangleRenderer.cs` 是内嵌 SPIR-V、含顶点缓冲/PSO/`DrawInstanced` 闭环的"第一个三角形"真实代码。
- **物理**：`CollisionDetectionReal.cs` 中 GJK/EPA/SAT、自定义向量/四元数、形状分发器均为真实手写实现。
- **AI**：NavMesh 烘焙/查询、GOAP、行为树等算法层真实存在。
- **音频 / ECS / 资产管线**：音频 DSP、Archetype ECS 原型、ProjectSystem/AssetDatabase 的逻辑与测试代码均为认真编写。

**仍需补齐的真实短板：**
- 高级渲染算法（MicroCluster/ProbeField/ReSTIR/SVGF/路径追踪）多为**算法原型**，尚未在真实 GPU 上完成端到端验证。
- 引擎库与编辑器虽已编译通过，但**运行时**（如 `VulkanPipelineState` 原生崩溃 0xC0000005）仍需在真实硬件上排查。
- 仓库根目录仍残留历史分析脚本与失真错误日志，工程卫生待清理。

---

## 编译修复历程（根因，已解决）

历史上编译失败的根本原因是"逐文件批量生成、从未整体编译对齐"的结构性问题。已按根因分类修复：

| 错误码 | 根因 | 处理 |
|---|---|---|
| CS0246 | 缺 `using` / 幻影类型（如 `BodyDescriptor`、`PhysicsScene`、`MaterialLibrary`） | 补 using + 补真实类型定义 |
| CS0104 | 命名空间撞车（`RHI` 与 `Vulkan` 同名类型，如 `VulkanSyncManager`） | 消歧别名 / 统一命名空间 |
| CS0115/CS0534/CS0509 | 类层次断裂（如继承密封 `FogVolume`） | 修正继承关系 |
| CS0111/CS0102/CS0579 | 重复成员（复制粘贴产物） | 去重 |
| CS9244/CS0051/CS8340 | unsafe / ref struct 不一致 | 补 `unsafe` 上下文 |
| CS1073 | ECS 语法级断裂（缺括号） | 修复语法 |

> 各目录"X/Y 文件含编译错误"的旧计数（如 Rendering 23/113、Physics 22/82、ECS 11/19、Vulkan 11/17）在 2026-11-01 已全部归零。

---

## 技术栈（设计意图；算法层真实，GPU 端到端部分未验证）

| 领域 | 技术 |
|------|------|
| 图形后端 | Vulkan 1.4 (Silk.NET) + RHI 抽象层（D3D12 后端已废弃并从 csproj 排除，仅有 `D3D12Device.cs` 1 个文件残留） |
| 渲染管线 | PBR · 级联阴影 · 路径追踪（原型）· MicroCluster（原型）· TSR · 体积雾 —— 算法层存在，多数高级特性未在真实 GPU 上验证 |
| 物理 | 纯 C# 实现（GJK/EPA/SAT/9 种关节/流体/布料）——核心算法真实手写，编译通过 |
| AI | GOAP · NavMesh · 行为树 · LLM 集成（开发中） |
| 动画 | Motion Matching · IK · 动画图表 · 重定向 |
| 音频 | WASAPI · 3D 空间 · DSP · 卷积混响 |
| 脚本 | Roslyn C# Scripting · 可视化脚本 |
| 编辑器 | WinUI 3 外壳 + 视口/大纲/内容浏览器/检查器/序列编辑器（编译通过） |
| 网络 | LiteNetLib · 状态复制 · 客户端预测 |
| DCC 桥接 | Blender · Maya · 3ds Max LiveLink（骨架级） |

---

## 项目结构（实测）

```
YamaEngine/              引擎核心库 (C# .NET 8, 766 文件, ~26.8 万行；编译通过)
  ├── Rendering/           渲染管线 (113 文件)
  ├── Physics/             物理引擎 (82 文件)
  ├── AI/                  人工智能 (35 文件)
  ├── Audio/               音频系统 (35 文件)
  ├── Animation/           动画系统 (34 文件)
  ├── RHI/                 渲染硬件抽象 (21 文件)
  ├── Vulkan/              Vulkan 后端 (17 文件)
  ├── ECS/                 Archetype ECS (19 文件)
  └── ...                  其余子系统
YamaEditor/              WinUI 3 编辑器外壳（编译通过）
YamaEngine.Tests/        单元测试（19 文件，真实断言逻辑，编译通过）
YamaEngineBridge/        C++ native 桥接 (Assimp)
BlenderBridge / MayaBridge / MaxBridge   DCC 端插件
```

---

## 环境要求与构建

| 依赖 | 版本 |
|------|------|
| [.NET SDK](https://dotnet.microsoft.com/download) | 8.0.422（仓库 `global.json` 已锁定） |
| [Visual Studio 2022](https://visualstudio.microsoft.com/) | 17.8+（含桌面开发 & C++ 工作负载） |
| Windows App SDK | 1.8+ |
| Vulkan 驱动 | 1.3+（推荐 1.4） |
| 平台 | Windows 10/11 x64 |

```powershell
# 还原依赖
dotnet restore YamaEditor.sln
# 构建引擎核心库（0 错误）
dotnet build YamaEngine\YamaEngine.csproj -c Debug
# 构建编辑器（0 错误）
dotnet build YamaEditor\YamaEditor.csproj -c Debug
# 运行测试（引擎库已可编译，测试可执行）
dotnet test YamaEngine.Tests\YamaEngine.Tests.csproj
```

---

## 下一步（在"能编译"基础上继续）

1. **GPU 端到端验证**：把高级渲染算法（MicroCluster/ProbeField/ReSTIR/SVGF）接入真实 Vulkan 管线并实测。
2. **运行时排查**：解决 `VulkanPipelineState` 原生崩溃（0xC0000005）等运行时问题。
3. **工程卫生**：清理根目录历史脚本（`_analyze*.py`、`fix_dups*.py`、失真错误日志与 `_dup_backup/`）。
4. **测试跑通**：确认 `YamaEngine.Tests` 全用例在真实环境通过。

---

## 许可证

本项目基于 [MIT License](./LICENSE) 开源。
