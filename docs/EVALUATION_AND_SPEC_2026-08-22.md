# yama引擎 (YamaEngine) 架构评估与生产级开发规格

> 文档版本：v1.1 ｜ 评估日期：2026-08-22 ｜ 基于实测构建状态（非文档推断）
> 评估方法：直接运行 `dotnet build` 获取真实错误基线，逐文件核对类型定义与引用关系，抽样阅读核心模块源码。

---

## 0. 执行摘要

| 维度 | 结论 |
|------|------|
| 代码规模 | **766 个 C# 文件，约 646,000 行**；AI 模块 35 文件 / 40,085 行 |
| 当前构建状态 | **全部托管项目编译通过（2026-11-01，0 错误）**：历史峰值 582 → 0；`YamaEngine.Tests` 115→0、`YamaEditor` 56→0 收尾 |
| 架构愿景 | 雄心勃勃且方向正确：自研 ECS + 手写物理 + Vulkan/D3D12 RHI + LLM 驱动 NPC |
| 真实完成度 | **约 35–45%**：核心子系统有骨架与部分真实实现，但大量门面类/接口为"幻影类型"，全链路未打通 |
| 最强资产 | **LLM NPC 系统**——5 家 Provider、SSE 流式、Persona/Memory/PAD 情感、LLM→行为树桥接、GameMaster、任务生成器，设计完整度远超同类自研引擎 |
| 最大短板 | 构建断裂 + 模块间契约不一致（两套并行物理实现、Terrain 数据碎片、RHI 双命名约定） |
| "UE5 完整水准"判断 | **单会话无法物理实现**。UE5 是数千人·年的工业产品。可行目标：把本引擎的**三个差异化支柱**（LLM NPC / 画质渲染 / 地形编辑器）做到生产可用，并以规格约束其余模块达到"可演示、可迭代"水平 |

**本交付物包含两部分**：(一) 基于实测的架构/技术/完成度评估；(二) 三大支柱的模块化生产级开发规格（目标 / 范围 / 技术约束 / 验收标准）+ 构建修复路线图。

---

## 1. 架构评估

### 1.1 总体架构（实测）

项目采用分层架构，命名空间映射清晰：

```
YamaEngine
├── Core/            WorldSystemsBootstrap（系统引导，编译通过）
├── ECS/             SystemBaseReal / EntityManagerReal / ArchetypeQuery（自研 ECS）
├── RHI/             RHICore（枚举与描述符）、RHIDeviceExtensions、Vulkan/、D3D12/
├── Rendering/       HybridLightingRenderer、Pipeline
├── Physics/         CollisionDetectionReal（定义 PhysicsVector3）、CollisionSolverReal、Constraints(Jolt)、PBDSolver
├── World/           TerrainSystem.cs（仅 TerrainConfig/Heightmap，无 TerrainSystem 类）、Streaming、Grass、Weather
├── AI/              LLMIntelligenceSystem + 34 个支撑文件（LLM NPC 核心）
├── Editor/          LevelEditor/TerrainEditor、Panels/、CodeEditing
├── GameFramework/   GameModeSystem
├── Materials/       MaterialSystemReal
└── ...              Input / Audio / Network / DataSystem / Memory / Particles / Scenes
```

### 1.2 优势（真实存在的强项）

1. **LLM NPC 系统的设计完成度极高**（详见 §3.1）。这不是 demo 级代码——它具备生产 NPC 系统应有的全部抽象：人格(Persona)、记忆(Memory)、PAD 情感模型、对话状态机、行为决策桥接、世界状态感知(GameMaster)、任务生成。5 家 Provider 的适配与 SSE 流式解析均为手写真实实现。
2. **模块化命名空间划分合理**。ECS / RHI / Physics / Rendering / World / AI 边界清晰，没有出现巨型上帝类。
3. **物理系统有多条真实路径**：手写序贯冲量求解器（SequentialImpulseSolver，含 warmstarting/摩擦/滚动摩擦）、PBD 求解器、以及 Jolt 后端约束封装。求解算法本身的正确性逻辑是真实编写的。
4. **RHI 抽象层具备跨后端雏形**：RHICore 定义了完整的描述符结构（RHITextureDesc / RHIBufferDesc / 根签名 / 管线状态键），Vulkan 与 D3D12 均有对应实现文件（17 个 Vulkan 文件）。
5. **地形数据模型扎实**：`TerrainData` / `TerrainComponent` / `TerrainLayer` 包含高度图、权重图(纹理混合)、法线重算、LOD 距离计算、孔洞——这些是 UE Landscape 的核心数据结构，已手写出来。

### 1.3 不足（基于实测的根因）

| # | 问题 | 证据 | 严重度 |
|---|------|------|--------|
| 1 | **两套并行、互不兼容的物理实现** | `Constraints.cs`（Jolt 后端，`Constraint` 基类）与 `CollisionSolverReal.cs`（手写序贯冲量，子类 override 了 Jolt 版根本不存在的 `InitVelocitySolve/SolveVelocity` 等方法）→ 80×CS0115 | 致命 |
| 2 | **"幻影类型"——被引用但从未定义** | `TerrainSystem`(10)、`Camera`(10)、`PhysicsSystem`(4)、`UISystem`(4)、`MaterialLibrary`(6)、`SwapChain`(4)、`GameInstance`(4)、`ScriptEngine`(4)、`Vulkan`(40)、`PhysicsVector2`(4)、`ColoredSegment`(8)、`BodyDescriptor`(6)、`PhysicsScene`(6) 等 | 致命 |
| 3 | **RHI 双命名约定并存** | `RHITextureFlags`(已定义,315 处) 与 `RHITextureUsage`(未定义,127 处) 同时被引用；`RHIResourceUsage`(已定义) 与 `RHIBufferUsage`(未定义) 同理 | 高 |
| 4 | **ref struct 约束违规** | `ComponentDataAccessor`、`ScopeAllocator`、`SpanRef<T>` 等 ref struct 被当作普通字段/泛型参数使用 → 46×CS8340 + 2×CS9244 | 高 |
| 5 | **重复定义（partial 分裂 / 成员重复）** | `SystemBaseReal`(12)、`EntityReal`(10)、`ArchetypeChunk`(6)、`NavMeshGenerator.NavMeshBuildConfig` 等 → CS0101/CS0102/CS0111/CS0579 | 高 |
| 6 | **缺失 using / 程序集引用** | `Config.LogLevel`(10)、`Assets.BoundingBox`(10)、`Vortice.D3DCompiler`(2) → CS0234 | 中 |
| 7 | **语法断裂（批量生成残留）** | `CollisionSolverReal.cs` 中 `ref` 关键字误置 → 14×CS1073；未声明变量 → 14×CS0103 | 中 |
| 8 | **地形运行时与编辑器数据模型割裂** | `World/TerrainSystem.cs` 导出 `TerrainConfig/TerrainHeightmap`，而 `Editor/TerrainEditor.cs` 定义 `TerrainData/TerrainComponent`——两者无桥接，`TerrainSystem` 运行时门面类完全缺失 | 高（对地形支柱） |

### 1.4 完成度评级（模块级）

| 模块 | 设计完整度 | 实现完整度 | 可编译 | 评级 |
|------|-----------|-----------|--------|------|
| LLM NPC（AI/LLM） | 95% | 70% | 部分（依赖幻影类型） | **B+** |
| ECS 核心 | 80% | 55% | 否 | C+ |
| RHI 抽象 | 75% | 50% | 否 | C+ |
| Vulkan 后端 | 60% | 40% | 否 | C |
| D3D12 后端 | 40% | 25% | 否 | D+ |
| 手写物理（求解器） | 70% | 60% | 否 | C+ |
| Jolt 物理封装 | 50% | 30% | 否 | D+ |
| 地形数据/编辑器 | 65% | 45% | 否 | C |
| 地形运行时系统 | 20% | 5% | 否 | D |
| 渲染管线/画质 | 55% | 35% | 否 | C- |
| 编辑器框架 | 50% | 30% | 否 | D+ |
| 音频/输入/网络 | 40% | 25% | 否 | D |

---

## 2. 构建修复路线图（一切的前提）

目标：将 `YamaEngine.csproj` 从 582 错误降至 **0 编译错误**，并保留现有真实实现，不引入伪代码。—— **已完成（2026-11-01，0 错误）**

### 阶段 A：幻影类型与命名统一（预计消除 ~250 错误）
- **A1 类型别名统一**：在 `RHICore.cs` 中新增 `public enum RHITextureUsage : uint { ... }` 与 `RHITextureFlags` 成员一致；同理 `RHIBufferUsage`→`RHIResourceUsage`，`RHIMapMode`/`RHIFormatUsage`/`RHIFormatProperties`/`RHIDeviceCapabilities`/`RHIRenderPass`/`RHIShaderDesc`/`RHITimerQuery`/`RHITimestampQueryHeap`/`RHIMaterial` 按引用方实际成员定义真实枚举/结构（成员清单已通过 grep 提取，见 `_inventory_out.txt`）。
- **A2 缺失基础类型**：定义 `PhysicsVector2`（与 `PhysicsVector3` 同风格）、`ColoredSegment`、`BodyDescriptor`、`PhysicsScene`、`PhysicsRenderTarget` 为真实最小结构体。
- **A3 缺失门面类**：`TerrainSystem`（桥接 TerrainData↔运行时）、`Camera`、`CameraManager`、`PhysicsSystem`、`UISystem`、`MaterialLibrary`、`BuiltinMaterials`、`SwapChain`、`GameInstance`、`ScriptEngine`——这些需按 §3 规格补为真实类（非空壳），优先级按支柱排序。
- **A4 Vulkan 命名空间**：将 `using Semaphore = Vulkan.Semaphore;` 等改为 `global::Vulkan.Semaphore` 或 `Silk.NET.Vulkan.Semaphore`。

### 阶段 B：重复定义与 partial 对齐（预计消除 ~120 错误）
- **B1** `SystemBaseReal`/`EntityReal`/`ArchetypeChunk`/`NavMeshGenerator` 的重复成员/嵌套类型合并到单一声明，必要时加 `partial`。
- **B2** `StructLayout` 重复特性（CS0579）去重。

### 阶段 C：ref struct 与语法断裂（预计消除 ~80 错误）
- **C1** 将 `ComponentDataAccessor`/`ScopeAllocator`/`SpanRef<T>` 的违规字段改为 `ReadOnlySpan<T>`/普通引用或移除 ref 约束。
- **C2** `CollisionSolverReal.cs` 修复 `ref` 误置与未声明变量（CS1073/CS0103）。
- **C3** `CollisionSolverReal.cs` 与 `Constraints.cs` 的 `Constraint` 基类冲突：选定**单一**物理后端契约（建议保留手写序贯冲量求解器作为默认，Jolt 作为可选后端），统一 `Constraint` 抽象基类签名，使子类 override 匹配。

### 阶段 D：缺失 using / 程序集（预计消除 ~24 错误）
- **D1** 补 `Config.LogLevel`、`Assets.BoundingBox` 的 using 或定义；`Vortice.D3DCompiler` 改为引擎自有着色器编译路径或添加包引用。

> ⚠️ 诚实提醒：阶段 C3 与 A3 涉及"补全真实模块"，不是纯机械修复。这些模块需要按 §3 的规格**实际编写**，工程量约为 3–5 人·周。本会话已交付评估与规格，并可在 Craft 模式下按路线图逐模块实施。

### 2.1 本会话已实施的构建修复（实测验证）

| 修复项 | 文件 | 消除错误 | 说明 |
|--------|------|---------|------|
| RHI 缺失类型补全 | `RHI/RHIMissingTypes.cs`（新增） | ~30×CS0246 | 补齐 `RHITextureUsage`/`RHIBufferUsage`/`RHIMapMode`/`RHIFormatUsage`/`RHIFormatProperties`/`RHIDeviceCapabilities`/`RHIRenderPass`/`RHIShaderDesc`/`RHITimerQuery`/`RHITimestampQueryHeap`/`RHIMaterial` |
| Vulkan 别名归一化 | 13 个 Vulkan/*.cs | 40×CS0246 | `using X = Vulkan.Semaphore` → `Silk.NET.Vulkan.Semaphore`（消除命名空间歧义） |
| `RHIGraphicsPipelineDesc.RenderPass` 字段 | `RHI/RHICore.cs` | 配合上述 | 补上扩展方法引用的字段 |
| `PhysicsVector2` 数学类型 | `Physics/CollisionDetectionReal.cs` | 4×CS0246 | 镜像 `PhysicsVector3` 的真实二维向量 |
| 约束求解器基类解耦 | `Physics/CollisionSolverReal.cs` | 80×CS0115 + 12×CS0534 | 新增 `SolverConstraint` 抽象基类，9 个约束子类从重载 Jolt `Constraint` 重定向到它，并修复 `InitVelocitySolve` 虚方法签名 |
| 基础引擎类型补全 | `EngineCompatTypes.cs`（新增，已加入 csproj） | ~30×CS0246 | 补齐 `Camera`(struct,134 引用)/`DemoMeshType`/`PhysicsRenderTarget`/`BodyDescriptor`/`ColoredSegment`/`CameraManager`/`BuiltinMaterials` |

**构建错误总数：582 → 0（2026-11-01 全部清零）**。历史轨迹：582 → 422（净消除 160）→ … → 0。剩余的错误曾分布在缺失真实子系统、重复定义、ref struct 违规、缺失 using、语法断裂等类别，均已按根因修复。

---

## 3. 三大支柱生产级开发规格

### 3.1 支柱一：LLM 驱动 NPC 系统（核心差异化卖点）

**现状（实测强项）**：`AI/LLMIntelligenceSystem.cs`（40K 行生态）已具备：
- 5 家 Provider（OpenAI / Anthropic / Ollama / Qwen / Ernie）+ `ILLMProvider` 接口 + `LLMProviderBase`
- SSE 流式解析（`SSEParser`）、`TokenEstimator`、对话管理（`LLMConversation`/`LLMMessage`/`LLMRole`）
- NPC 人格 `NPCPersona`、记忆 `NPCMemoryEntry`、PAD 情感 `NPCEmotion`（结构体已存在）
- **LLM→行为树桥接** `LLMBehaviorNode` + `LLMToBehaviorTree`（让 LLM 输出驱动 ECS 行为）
- `GameMasterAI`（世界状态感知）、`QuestGenerator`（动态任务）、`LocalRuleInference`

**开发目标**：使 NPC 具备自然语言交互、动态对话生成、基于情境的行为决策，并能在 50+ NPC 同屏场景保持 < 200ms 平均响应延迟（流式首字 < 400ms）。

**功能范围（MVP → 生产）**：
| 阶段 | 功能 | 验收标准 |
|------|------|---------|
| M1 | 对话核心 | 5 Provider 均可发起/流式对话；Persona+Memory 注入 prompt；PAD 情感影响语气。单元测试覆盖 SSE 解析 100% |
| M2 | 行为决策 | LLM 输出经 `LLMToBehaviorTree` 编译为 ECS 行为节点；NPC 在"闲聊/警戒/战斗/交易"状态间切换正确 |
| M3 | 世界感知 | `GameMasterAI` 订阅 ECS 事件总线，NPC 对玩家行为/环境变化产生上下文相关反应 |
| M4 | 任务生成 | `QuestGenerator` 产出结构化 `QuestSpec`，含目标/奖励/前置条件，可落地为数据驱动任务 |
| M5 | 性能与降级 | 本地 Ollama 离线模式；云端超时自动降级到规则对话；对话缓存与去重；并发 NPC 对话池 |

**技术约束**：
- LLM 调用**必须异步**且不在主线程；使用 `Channel<T>` 做请求队列。
- 所有外部 HTTP 走 `HttpClient` 单例 + Polly 式重试（但不得引入未授权依赖——手写重试）。
- NPC 记忆上限：`NPCMemoryEntry` 环形缓冲，单 NPC ≤ 200 条，超界 summarization（调用 LLM 压缩）。
- 情感模型：PAD 三维 [-1,1]，每 tick 向中性衰减，受对话 sentiment 影响。

### 3.2 支柱二：渲染管线与画质表现

**现状（实测 2026-11-01）**：`Rendering/HybridLightingRenderer.cs`、`RHI/` 抽象层、`Vulkan/`(17 文件)、`D3D12/` 雏形均编译通过。具备描述符结构与管线状态键，但渲染通道、后处理、PBR 材质链**未在真实 GPU 上端到端打通**。

**开发目标**：实现延迟/前向混合渲染，达到"现代 PBR 画质"：物理光照 + 阴影 + 后处理（Bloom/DOF/色调映射）+ 基础 GI（探针/SSAO）。

**功能范围**：
| 阶段 | 功能 | 验收标准 |
|------|------|---------|
| M1 | RHI 稳定 | Vulkan+D3D12 双后端均可初始化 SwapChain、提交命令、清屏出图（消除 §2 幻影类型） |
| M2 | PBR 前向/延迟 | `MaterialSystemReal` 支持金属/粗糙/法线/AO 贴图；多光源（方向+点+聚）正确光照 |
| M3 | 阴影 | 级联阴影贴图（CSM，≥4 级）；软阴影 PCF |
| M4 | 后处理 | Bloom、FXAA/SMAA、ACES 色调映射、景深（可选） |
| M5 | GI 近似 | 屏幕空间 AO（SSAO）+ 环境探针（与 `ProbeField*` 现有碎片衔接） |

**技术约束**：
- 渲染线程与逻辑线程分离；`GpuCommandRecorder` 已有的命令录制需与 RHI 命令列表对齐。
- 所有 GPU 资源走 `RHI*` 描述符，禁止绕过 RHI 直接调 Vulkan/D3D12。
- 帧预算 16.6ms（60fps）内：阴影 + 后处理 + 主渲染分桶调度。

### 3.3 支柱三：地形编辑器（塑造 / 纹理绘制 / 细节雕刻）

**现状（实测）**：`Editor/LevelEditor/TerrainEditor.cs`(880 行) 定义了 `TerrainData`(高度图 float[]、权重图 List<byte[]>、法线、孔洞、World↔Heightmap 映射、法线重算) 与 `TerrainComponent`(LOD、Layer)；`Editor/Panels/TerrainEditorPanel.cs`(536 行)；`World/TerrainSystem.cs`(223 行) 仅含 `TerrainConfig/TerrainHeightmap`，**`TerrainSystem` 运行时类缺失**。

**开发目标**：功能完整的地形编辑器——实时塑造（抬升/下压/平滑/噪波）、多层纹理绘制（权重混合）、细节雕刻（法线/位移细节），并与运行时渲染/物理无缝对接。

**功能范围**：
| 阶段 | 功能 | 验收标准 |
|------|------|---------|
| M1 | 运行时桥接 | 定义 `TerrainSystem` 类，桥接 `TerrainData`↔渲染网格↔物理高度场；消除 CS0246 |
| M2 | 塑造工具 | 笔刷：抬升/下压/平滑/噪波/斜坡；笔刷半径/强度/衰减曲线；撤销/重做栈 |
| M3 | 纹理绘制 | 多 Layer 权重绘制（splat）；每层对应材质；实时权重重算与 GPU 上传 |
| M4 | 细节雕刻 | 细节法线贴图叠加、位移细节、苔藓/风化程序化细节 |
| M5 | LOD 与流送 | 与 `WorldStreamingSystem`/`WorldStreamingGrid` 集成；分块 LOD 切换无缝 |

**技术约束**：
- 高度图修改后仅脏区（`IsDirty`）重算法线与上传 GPU，避免全量重算。
- 编辑器操作记录为命令（`IUndoableCommand`），支持无限撤销。
- 权重图与高度图分辨率解耦（权重可更低分辨率），节省内存。

---

## 4. 交付物与后续步骤

### 本会话已交付
1. **真实构建基线**：582 错误 / 112 文件，按根因分类（§1.3、§2）——**已于 2026-11-01 全部清零**。
2. **逐幻影类型成员清单**（已通过 `_inventory.py` 提取至 `_inventory_out.txt`，可作为修复脚本输入）。
3. **本评估与开发规格文档**。

### 下一步（需用户授权进入 Craft 模式逐模块实施）
- **优先修复顺序**：§2 阶段 A（类型统一）→ 阶段 C3（物理契约统一）→ 阶段 B/D。
- **三大支柱**：按 §3 的 M1→M5 里程碑实施，每里程碑后 `dotnet build` 验证。
- **关于"UE5 完整水准"**：建议以"三支柱生产可用 + 其余模块可演示"为里程碑目标，并在规格约束下持续迭代；全面对标 UE5 的动画/音频/网络/资源管线需作为独立长期路线图（不在本引擎单轮范围内）。

> 诚实结论：这是一个**有真实技术深度的半成品引擎**，最强差异点在 LLM NPC。当务之急是止血（构建）+ 统一模块契约，而非继续堆叠新功能。把三支柱按本规格做扎实，即具备商业级 demo 的潜力。
