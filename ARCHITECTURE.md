# 架构文档

> **重要**：本文件中的规模与状态数据已于 2026-11-01 依据真实 `dotnet build` 结果修订。
> 先前版本声称的"568K 行 / 759 文件 / 52 模块 / 全部编译通过 / 生产级"与真实编译结果不符，已更正；当前全部托管项目 **0 错误**。

## 系统总览

yama引擎采用分层架构，从底层 RHI 到上层编辑器。**全部托管项目当前以 0 错误通过编译（2026-11-01 实测）**。

```
┌─────────────────────────────────────────────────────────────┐
│                    YamaEditor (WinUI 3)                    │
│         编辑器外壳 · XAML 页面 · Vulkan Surface 嵌入          │
│         ✅ 编译通过（0 错误）                                 │
├─────────────────────────────────────────────────────────────┤
│                     Editor Layer (ImGui)                     │
│  Outliner · Inspector · Viewport · Console · CodeEditor      │
│  TerrainEditor · ModelingPanel · UnifiedEditorModeSystem     │
├─────────────────────────────────────────────────────────────┤
│                    Gameplay & Scripting                       │
│  GameplayAbilities · VisualScript · Roslyn Scripting         │
├─────────────────────────────────────────────────────────────┤
│                       Engine Systems                          │
│  Rendering · Physics · AI · Animation · Audio · Network      │
│  ECS · Assets · Build · Input · Particles · VFX             │
├─────────────────────────────────────────────────────────────┤
│                         RHI Layer                              │
│            RHI Abstraction (描述符/命令/同步)                   │
│  ✅ RHI↔Vulkan 命名空间撞车已消歧（原 CS0104 歧义归 0）        │
├──────────────┬──────────────────────────────────────────────┤
│   Vulkan     │              (D3D12 已废弃并排除)               │
│  Silk.NET    │                                                │
├──────────────┴──────────────────────────────────────────────┤
│                    Core & Platform                            │
│  EngineCore · MathUtils · Serialization · Threading          │
└─────────────────────────────────────────────────────────────┘
```

## 构建状态（实测，2026-11-01）

| 工程 | 构建结果 |
|---|---|
| `YamaEngine` (引擎库) | **通过**：0 错误（历史峰值 1414 已清零） |
| `YamaEditor` (编辑器) | **通过**：0 错误（历史峰值 707 / 收尾 56→0，commit `b900c87`） |
| `YamaEngine.Tests` (测试) | **可编译运行**：0 错误（115→0，commit `2f4f8b8`） |
| `YamaRuntime` / `BlenderBridge` / `YamaCel2DSandbox` / `YamaVulkanSandbox` | **通过**：0 错误 |
| `YamaEngineBridge` (C++ vcxproj) | 需 Visual Studio MSBuild（`dotnet CLI` 无法导入 `Microsoft.Cpp.Default.props`，环境限制） |

> 编译层面已闭环；"渲染闭环已完整接通""编辑器可启动"仍需真实 GPU 运行验证——编译通过 ≠ GPU 已验证。

## 真实规模（实测）

| 指标 | 旧文档声称 | 实测 |
|---|---|---|
| C# 源文件总数 | 759 / 798 | **817 个**（全仓库，含编辑器/测试/桥接） |
| 引擎库 C# 文件 | 759 | **766 个** |
| 代码总行数（含注释/空行） | 568,888+ / 716,673 | **319,170 行**（全仓库）；引擎库 268,158 行 |
| 模块数 | 52 | 约 40+ 个目录级子系统（非 52 个独立模块） |

> 旧文档的行数约为实测的 **1.8 倍**，属虚高。请以实测为准。

## 模块清单（实测文件数 / 行数）

### 渲染管线 (Rendering · 113 文件)

| 子系统 | 真实状态 |
|--------|------|
| SceneRendererReal | 真实 DrawIndexedInstanced + PBR + 阴影 + 骨骼蒙皮；Rendering 目录 **0 文件含编译错误** |
| MicroCluster | 虚拟几何（簇化 + LOD + 量化），算法原型 |
| PathTracer | BVH/NEE/MIS/ReSTIR/SVGF，算法原型（HLSL 存在） |
| ShadowSystem | 级联阴影 |
| TSR | 时序超分辨率 |
| FoliageRenderingSystem | 植被渲染 |

> **注意**：Rendering 编译通过（0 错误）；高级特性（MicroCluster/ProbeField/ReSTIR/SVGF）多为"算法原型"，未接入真实 GPU 管线实测。

### 物理系统 (Physics · 82 文件)

| 子系统 | 真实状态 |
|--------|------|
| EnhancedContactSolver | 双切线摩擦 + 堆叠稳定性（算法真实） |
| NarrowphaseSystem | GJK/EPA/SAT 碰撞检测（`CollisionDetectionReal.cs` 已抽读，真实实现） |
| BroadphaseReal | 层级包围盒加速 |
| EnhancedFluidSystem | 流体模拟 |
| ClothSystem | 布料模拟 |
| 9 种关节约束 | 铰链/滑动/固定/弹簧/P6DOF |

> 核心碰撞算法（GJK/EPA/SAT）经抽读确认是真实手写实现；Physics 目录编译通过（0 错误）。

### ECS 架构 (ECS · 19 文件)

- **Archetype 模型**：原型架构，组件按内存布局连续存储（概念真实）
- **EntityQueryCache / SystemScheduler / EntityCommandBuffer**：编译通过（0 错误；原 CS1073/CS0111/CS0534/CS9244 已修复）

### 编辑器 (Editor · 41 文件)

#### 一体化工作流 (UnifiedEditorModeSystem)

5 种模式按 `1-5` 键切换（场景/地形/建模/代码/测试）—— 代码存在，编辑器工程编译通过；运行验证待真实 GPU 环境。

### AI 系统 (AI · 35 文件)

- NavMesh 烘焙 + 寻路（A*/D*Lite/JPS+/Theta*）
- 行为树 / GOAP / 群体模拟（ORCA）
- LLM 集成（开发中）

### 动画系统 (Animation · 34 文件)

- Motion Matching / IK（FABRIK+CCD+Two-Bone）/ 动画图表 / 重定向

### 音频系统 (Audio · 35 文件)

- WASAPI + Miniaudio；FFT/DSP/卷积混响/合成器（算法真实）

### DCC 桥接 (DCC · 骨架级)

| 软件 | 状态 |
|------|------|
| Blender | LiveLink TCP 插件（骨架级） |
| Maya / 3ds Max | Python 插件（骨架级） |

## 项目依赖关系

```
YamaEditor.sln
├── YamaEngine (Library, net8.0)         ← 编译通过（0 错误）
│   ├── Silk.NET.Vulkan (2.22.0)
│   ├── ImGui.NET (1.90.8.1)
│   ├── Vortice.Mathematics (1.9.3)
│   ├── Microsoft.CodeAnalysis.CSharp (4.9.2)
│   ├── SixLabors.ImageSharp (3.1.5)
│   └── LiteNetLib (1.3.1)
├── YamaEditor (WinUI 3, net8.0-windows10)  ← 编译通过（0 错误）
├── YamaEngine.Tests (net8.0)               ← 编译通过（0 错误）
├── YamaEngineBridge (C++/vcxproj)          ← Assimp 模型导入（需 VS MSBuild）
├── BlenderBridge (net8.0)
└── YamaEngine.Rust (Cargo)
```

## 关键设计决策（设计意图；算法层真实，GPU 端到端未全部验证）

### 1. 单一图形后端 (Vulkan)

原 D3D12 后端已废弃，统一使用 Vulkan 1.4 (Silk.NET)。**csproj 仅排除了 `RHI/D3D12/` 目录下的 `D3D12Device.cs` 一个文件（3205 行）残留**；渲染代码缺 `using YamaEngine.RHI` 导致的 CS0246 已修复。

### 2. ImGui 编辑器 UI

编辑器 UI 基于 ImGui.NET。WinUI 3 用于外壳和 Vulkan Surface 嵌入。编辑器编译通过；UI 运行验证待真实环境。

### 3. 纯 C# 物理

物理系统从零实现（GJK/EPA/SAT/关节），不依赖 PhysX/Havok。核心算法经抽读确认真实；物理目录编译通过。

### 4. Archetype ECS

选择 Archetype 模式而非 Sparse Set。概念真实；ECS 目录编译通过。

## 目录结构（实测）

```
YamaEngine/
├── AI/                 人工智能 (35 文件)
├── Animation/          动画系统 (34 文件)
├── Assets/             资产管理 + 导入器 (34 文件)
├── Audio/              音频系统 (35 文件)
├── Build/              构建管线 (14 文件)
├── CodeEditing/        代码编辑器核心（原引用幻影类型 ColoredSegment，已补定义）
├── Core/               引擎核心 (20 文件)
├── DCC/                DCC 桥接
├── Diagnostics/        诊断/Profiler (19 文件)
├── ECS/                实体组件系统 (19 文件)
├── Editor/             编辑器 (41 文件)
├── GameFramework/      游戏框架
├── Gameplay/           游戏玩法 (含 GameplayAbilitySystemV2，原引用幻影类型 GameplayTag/GameplayAttribute，已补定义)
├── Input/              输入系统 (14 文件)
├── Materials/          材质系统
├── Modeling/           建模工具 (10 文件)
├── Network/            网络复制 (24 文件)
├── Networking/         网络 (3 文件)
├── Particles/          粒子系统 (16 文件)
├── Physics/            物理引擎 (82 文件)
├── Platform/           平台抽象
├── RHI/                渲染硬件抽象 (21 文件)
├── Rendering/          渲染管线 (113 文件)
├── Scripting/          脚本系统
├── ShaderGraph/        着色器图表
├── Shaders/            HLSL 着色器 (37 个 .hlsl)
├── UI/                 UI 系统 (15 文件)
├── VFX/                视觉效果 (11 文件)
├── Vulkan/             Vulkan 后端 (17 文件，0 错误；原 11 文件 CS0104 命名空间歧义已消歧)
├── World/              世界/地形 (含 TerrainSystem、TerrainHeightmap)
└── ...                其余子系统
```

> 以上结构基于实测文件树，全部目录 0 编译错误。
