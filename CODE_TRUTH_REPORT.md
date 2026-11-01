# yama引擎 (Yama Engine) — 代码真实状态报告（修订版）

> **最后更新**：2026-08-07（基于 `dotnet build` 真实构建结果 + 逐文件抽读）
> **修订说明**：本文件旧版（"CODE_TRUTH_REPORT"）声称各子系统"✅ 编译 / 生产级 / 零 NotImplementedException"，**与真实编译结果严重不符**，已于 2026-08-07 依据实测重写。本版以编译器输出为唯一事实来源。
> **审查方法**：.NET SDK 8.0.422 真实 `dotnet build` + 错误归类脚本 + 逐子系统抽读源代码。

---

## §0. 最关键的事实（先看这段）

**yama引擎当前无法编译，也没有发布过任何可运行的版本。**

- `YamaEngine.csproj` 单独构建：**1414 个编译错误**，覆盖 **177 / 766 个文件（约 23%）**。
- `YamaEditor.csproj`：**无法构建**（依赖未编译的引擎库，实测 707 错误），因此"编辑器可启动、视口已接通"不成立。
- `YamaEngine.Tests`：**无法编译/运行**（19 个测试文件逻辑真实，但因引擎库未编译而失败）。

旧版报告中"✅ 编译 生产级"的逐行标注**全部作废**——它们是基于"逐文件抽读后贴标签"，而非真实编译器结果。

---

## §1. 真实规模（实测 vs 旧称）

| 指标 | 旧版声称 | 实测（2026-08-07） |
|---|---|---|
| C# 文件总数 | 798（主引擎 756） | **817 个**（全仓库，含编辑器/测试/桥接） |
| 引擎库 C# 文件 | 756 | **766 个** |
| 代码总行数（含注释/空行） | 503,016（去注释）/ 716,673（含） | **319,170 行**（全仓库）；引擎库 268,158 行 |
| HLSL 着色器 | 37 | 37（属实） |
| NotImplementedException | 0 | **2**（可忽略，非核心空壳） |

> 旧版行数约为实测的 **1.8 倍**，属虚高。请以实测为准。

---

## §2. 构建错误根因归类（引擎库 1414 错）

| 错误码 | 数量 | 含义 | 真实根因 |
|---|---|---|---|
| CS0246 | 562 | 找不到类型/命名空间 | **两类**：(a) 跨命名空间引用断裂——`GameplayTag`、`BoundingBox`、`Camera`、`Mesh`、`TerrainSystem`、`RHIDevice`、`MicroClusterCluster` 等类型**存在但引用处缺 `using`**；(b) **约 10 个幻影类型**被大量引用却从未定义：`BodyDescriptor`、`PhysicsScene`、`PhysicsRenderTarget`、`RenderResource`、`MaterialLibrary`、`ProbeFieldIntegration`、`ColoredSegment`、`CPUHotSpot`、`LeakSuspect`、`RHITimestampQueryHeap` |
| CS0115 | 182 | 找不到可 override 的方法 | 类层次结构断裂 |
| CS0104 | 148 | 引用歧义 | **重复/竞争的类型定义**横跨命名空间（Vulkan 后端 11 文件因此报歧义，如 `VulkanSyncManager` 单文件 60 处） |
| CS9244 | 50 | 需 Unsafe 上下文 | 缺 `unsafe` / `AllowUnsafeBlocks` |
| CS0051 | 50 | 数组初始值设定项/Hazard | ref 结构/数组用法问题 |
| CS8340 | 46 | ref 结构约束 | ref struct 跨边界传递 |
| CS0111 | 44 | 类型已定义同名成员 | 复制粘贴重复成员 |
| CS0534 | 40 | 未实现继承的抽象成员 | 抽象类未实现 |
| CS0101 | 38 | 命名空间已包含定义 | 同名类型在同一命名空间重复 |
| CS0509 | 38 | 无法从密封类型派生 | 如 `LocalFogVolume` 继承自密封 `FogVolume` |
| CS0102 | 30 | 类型已包含成员 | 重复字段（如 `MotionBlurConfig.Quality`） |
| CS0234 / CS0260 / CS0103 | 若干 | 缺少/partial/未声明 | 分散的语法与引用问题 |
| CS1073 | 若干 | 解析错误 | ECS 部分文件缺括号等语法断裂 |

**一句话根因**：核心算法类是各自能写的，但整个代码库是"分文件批量生成、从未整体编译对齐"的产物——同名类型被生成了两套（RHI 与 Vulkan 命名空间撞车）、跨命名空间引用大量缺 `using`、还有少量根本没写的幻影类型。这正是它无法编译的根本原因。

---

## §3. 各子系统真实状态（实测含错文件数）

| 子系统 | 文件数 | 含编译错误文件数 | 真实状态 |
|---|---|---|---|
| Rendering | 113 | 23 | 算法层存在，但报错最密集；高级特性多为原型未实测 |
| Physics | 82 | 22 | 核心 GJK/EPA/SAT 真实（已抽读），但 22 文件跨命名空间断引用 |
| AI | 35 | 12 | NavMesh/GOAP/行为树算法真实，部分文件断引用 |
| ECS | 19 | 11 | Archetype 概念真实，但 11 文件含解析错误/重复成员/抽象未实现 |
| Vulkan | 17 | 11 | 交换链/管线真实，但 11 文件因 RHI↔Vulkan 命名空间撞车报 CS0104 歧义 |
| UI | 15 | 8 | — |
| Editor | 41 | 8 | 编辑器工程整体无法编译 |
| RHI | 21 | 7 | — |
| Network | 24 | 7 | — |
| World | 8 | 6 | 含 TerrainSystem/TerrainHeightmap（存在但被跨命名空间引用缺失） |
| Audio | 35 | 5 | 算法真实 |
| Assets | 34 | 4 | 资产管线逻辑真实 |
| 其余（GameFramework/Runtime/Gameplay/Core/Materials/ShaderGraph/Animation/Input/...） | — | 1–4 不等 | 分散断引用 |

> 约 77% 的引擎文件单独看无错误，但整个库因上述结构性问题无法作为整体编译通过。

---

## §4. 真实存在的亮点（抽读确认，是真写的）

- **Vulkan 底层**：交换链（`VulkanSwapchain`）、图形/计算管线真实实现；`YamaEditor/Controls/TriangleRenderer.cs` 是内嵌 SPIR-V、含顶点缓冲/PSO/`DrawInstanced` 闭环的"第一个三角形"真实代码。
- **物理碰撞**：`CollisionDetectionReal.cs` 的 GJK/EPA/SAT、自定义数学类型、形状分发器为真实手写实现。
- **AI**：NavMesh 烘焙/查询、GOAP、行为树等算法真实存在。
- **音频 DSP / ECS 原型 / 资产管线**：逻辑均为认真编写。
- **测试**：`YamaEngine.Tests` 中 ProjectSystem/AssetDatabase 等测试用自研断言（`AssertEqual`/`AssertTrue`）做了真实的工程创建/资源导入/roundtrip 验证——只是当前无法编译运行。

---

## §5. 诚实声明

1. 本报告基于 **.NET 8.0.422 真实 `dotnet build` 输出**（非逐文件自读贴标签）。
2. "生产级""编译通过""568K 行"等旧表述**已被实测推翻**，本版为更正。
3. 渲染管线中的高级算法（MicroCluster/ProbeField/ReSTIR/SVGF）虽有代码与 HLSL，但**未在真实 GPU 上验证运行**，且依赖于当前无法编译的引擎。
4. 本仓库另有多份"评估报告"（COMPLETION_REPORT、EVALUATION_REPORT、DEEP_REVIEW_REPORT 等），其"编译通过/生产级/视口已接通"类表述同样与实测不符，均已/应同步修订。
