# 山雨游戏引擎 Shanyu GamesEngine

> 使用 C# (.NET 8) 从零构建的游戏/建模引擎原型，目标对标 UE5 级渲染管线，同时保持轻量级一体化工作流。

![License](https://img.shields.io/badge/license-MIT-blue.svg)
![.NET](https://img.shields.io/badge/.NET-8.0-512bd4.svg)
![Vulkan](https://img.shields.io/badge/Vulkan-1.4-b71b1b.svg)
![Platform](https://img.shields.io/badge/platform-Windows%20x64-0078d4.svg)
![Build](https://img.shields.io/badge/build-FAILING-red.svg)

---

## ⚠️ 当前真实状态（2026-08-07 实测）

**本项目目前无法编译，也没有发布过任何可运行的版本。** 以下是经过 `dotnet build`（.NET SDK 8.0.422）实测的事实，不是自述：

| 指标 | 文档旧称 | 实测（2026-08-07） |
|---|---|---|
| 编译状态 | "✅ 全部编译通过 / 生产级" | **失败**：引擎库 1414 个编译错误，覆盖 177/766 个文件（约 23%）；编辑器与测试项目同样无法构建 |
| C# 源文件 | 759 / 798 | **817 个**（全仓库，含编辑器/测试/桥接） |
| 代码行数 | 568,888+ / 716,673 | **319,170 行**（全仓库含注释）；引擎库 766 文件 / 268,158 行 |
| NotImplementedException | 0 | **2**（可忽略，非核心） |

> 文档中"568K / 716K 行""生产级""全部编译通过"等表述**与真实编译结果不符**，本仓库的多份"评估报告"亦存在相互矛盾与夸大。本 README 及其同级文档已于 2026-08-07 依据实测结果重写。

**已实测确认的真实情况：**
- 引擎库（`ShanyuEngine.csproj`）单独 `dotnet build` → **1414 错误，177/766 文件受影响**。
- 编辑器（`ShanyuEditor.csproj`）→ 因依赖未编译的引擎库，**无法构建（实测 707 错误）**，故"编辑器可启动、视口已接通"目前**不成立**。
- 测试项目（`ShanyuEngine.Tests`，19 个测试文件、真实断言逻辑）→ 因引擎库未编译，**无法编译/运行**，此前"83 个用例全部通过"的说法无法验证且当前为假。

---

## 项目是什么

一个以 C#/.NET 8 为主干、目标对标 UE5 渲染管线的**桌面端游戏/建模引擎原型**。架构意图清晰：RHI 抽象层 + 单一 Vulkan 1.4 后端、数据驱动的项目/资产/场景管线、一体化编辑器工作流。

**真正的亮点（已抽读代码确认，是真写的）：**
- **渲染底层**：Vulkan 交换链/管线真实实现；`TriangleRenderer.cs` 是内嵌 SPIR-V、含顶点缓冲/PSO/`DrawInstanced` 闭环的"第一个三角形"真实代码。
- **物理**：`CollisionDetectionReal.cs` 中 GJK/EPA/SAT、自定义向量/四元数、形状分发器均为真实手写实现。
- **AI**：NavMesh 烘焙/查询、GOAP、行为树等算法层真实存在。
- **音频 / ECS / 资产管线**：音频 DSP、Archetype ECS 原型、ProjectSystem/AssetDatabase 的逻辑与测试代码均为认真编写。

**真实的短板（同样已实测确认）：**
- 整体**无法编译**，根因是"逐文件批量生成、从未整体编译对齐"留下的结构性问题（详见下方"为何编译失败"）。
- 大文件重复行率高（抽查 41%–47%），存在明显的 AI 批量生成注水痕迹。
- 仓库**不是 git 仓库**；根目录堆积大量分析脚本（`_analyze*.py`、`fix_dups*.py`）、失真错误日志与 `_dup_backup/`（157 个 `.bak`），工程卫生较弱。

---

## 为何编译失败（根因，实测归类）

错误码分布（引擎库 1414 错）：

| 错误码 | 数量 | 含义 | 真实根因 |
|---|---|---|---|
| CS0246 | 562 | 找不到类型/命名空间 | 两类：(a) **跨命名空间引用断裂**——`GameplayTag`、`BoundingBox`、`Camera`、`Mesh`、`TerrainSystem`、`RHIDevice`、`MicroClusterCluster` 等类型**存在但引用处缺 `using`**；(b) **约 10 个幻影类型**被大量引用却从未定义：`BodyDescriptor`、`PhysicsScene`、`PhysicsRenderTarget`、`RenderResource`、`MaterialLibrary`、`ProbeFieldIntegration`、`ColoredSegment`、`CPUHotSpot`、`LeakSuspect`、`RHITimestampQueryHeap` |
| CS0104 | 148 | 引用歧义 | **重复/竞争的类型定义**横跨命名空间——Vulkan 后端自身就有 11 个文件因同名类型在 `RHI` 与 `Vulkan` 命名空间撞车而报歧义（如 `VulkanSyncManager` 单文件 60 处） |
| CS0115 / CS0534 / CS0509 | 约 260 | 重写/抽象/密封类错误 | 类层次结构断裂（如 `VolumetricFogSystem` 继承自密封的 `FogVolume`） |
| CS0111 / CS0102 / CS0579 | 约 104 | 重复成员/特性 | 复制粘贴产物（如 `SceneRenderer.GetRenderItems` 重复定义、`MotionBlurConfig.Quality` 重复） |
| CS9244 / CS0051 / CS8340 | 约 146 | unsafe 上下文 / ref struct | 缺少 `unsafe` 上下文或 ref 结构用法不一致 |
| CS1073 | 若干 | 解析错误 | ECS 部分文件存在语法级断裂（缺括号） |

一句话：**核心算法类是各自能写的，但整个代码库是"分文件生成、从未统一编译"的产物——同名类型被生成了两套、跨命名空间引用大量缺 using、还有少量根本没写的幻影类型。** 这正是它无法编译的根本原因，也是"编译通过/生产级"说法不成立的原因。

---

## 技术栈（设计意图，非全部已验证）

| 领域 | 技术 |
|------|------|
| 图形后端 | Vulkan 1.4 (Silk.NET) + RHI 抽象层（**注意**：D3D12 后端已废弃并从 csproj 排除，仅有 `D3D12Device.cs` 1 个文件残留） |
| 渲染管线 | PBR · 级联阴影 · 路径追踪（原型）· MicroCluster（原型）· TSR · 体积雾 —— 算法层存在，但多数高级特性未在真实 GPU 上验证 |
| 物理 | 纯 C# 实现（GJK/EPA/SAT/9 种关节/流体/布料）——核心算法真实，但 22/82 物理文件含编译错误 |
| AI | GOAP · NavMesh · 行为树 · LLM 集成（开发中） |
| 动画 | Motion Matching · IK · 动画图表 · 重定向 |
| 音频 | WASAPI · 3D 空间 · DSP · 卷积混响 |
| 脚本 | Roslyn C# Scripting · 可视化脚本 |
| 编辑器 | ImGui.NET + WinUI 3 外壳（**当前无法编译运行**） |
| 网络 | LiteNetLib · 状态复制 · 客户端预测 |
| DCC 桥接 | Blender · Maya · 3ds Max LiveLink（骨架级） |

---

## 项目结构（实测）

```
ShanyuEngine/              引擎核心库 (C# .NET 8, 766 文件, ~26.8 万行；当前无法编译)
  ├── Rendering/           渲染管线 (113 文件)
  ├── Physics/             物理引擎 (82 文件)
  ├── AI/                  人工智能 (35 文件)
  ├── Audio/               音频系统 (35 文件)
  ├── Animation/           动画系统 (34 文件)
  ├── RHI/                 渲染硬件抽象 (21 文件)
  ├── Vulkan/              Vulkan 后端 (17 文件，自身 11 文件含编译错误)
  ├── ECS/                 Archetype ECS (19 文件，11 文件含编译错误)
  └── ...                  其余子系统
ShanyuEditor/              WinUI 3 编辑器外壳（依赖引擎库，当前无法构建）
ShanyuEngine.Tests/        单元测试（19 文件，真实断言逻辑，但无法编译运行）
ShanyuEngineBridge/        C++ native 桥接 (Assimp)
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
dotnet restore ShanyuEditor.sln
# 构建引擎核心库（当前会失败，见"为何编译失败"）
dotnet build ShanyuEngine\ShanyuEngine.csproj -c Debug
# 构建编辑器（依赖引擎库，当前同样失败）
dotnet build ShanyuEditor\ShanyuEditor.csproj -c Debug
# 运行测试（当前因引擎库未编译而无法运行）
dotnet test ShanyuEngine.Tests\ShanyuEngine.Tests.csproj
```

---

## 诚实的下一步

要让这个引擎"名副其实"，优先级是：

1. **跑通一次真实 build**，把 1414 个错误作为唯一事实来源，按上述根因分类修复（先解决 CS0104 命名空间撞车与 CS0246 缺 using，再补幻影类型）。
2. **把文档数字与状态改回真实**（本仓库各报告已在 2026-08-07 重写）。
3. **清理根目录**：删除 `_analyze*.py`、`fix_dups*.py`、失真错误日志与 `_dup_backup/`；建立 git 跟踪。
4. 在能编译的基础上，再逐步把高级渲染算法（MicroCluster/ProbeField/ReSTIR/SVGF）接入真实 GPU 管线并实测。

---

## 许可证

本项目基于 [MIT License](./LICENSE) 开源。
