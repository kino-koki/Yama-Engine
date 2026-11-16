# Yama-Engine vs UE4.7 能力差距矩阵与分阶段路线图

> 生成日期：2026-11-02
> 依据：引擎模块结构实测（Glob/Grep 核验）+ `ENGINE_EVALUATION_REPORT.md` 评分卡 + 2026-11-02 构建/测试实测
> 目标：**全面打平 UE4.7 的所有能力**（注意是对标 UE4.7，非 UE5，故 Lumen/Nanite/Chaos 等非必要）

---

## 0. 关键结论（先说重点）

**Yama-Engine 已覆盖 UE4.7 几乎全部能力域**（12/12 均有对应代码模块），但**绝大多数停留在"算法原型（R1-R2）"而非"生产闭环（R3）"**。

因此"打平 UE4.7"的本质**不是从无到有补功能，而是把现有原型逐个做闭环**：
1. 接入真实 GPU 渲染管线（当前最大的 P0 卡点）；
2. 补齐编辑器可视化工具链（蓝图/材质/动画/关卡编辑器）；
3. 完善构建/打包/跨平台工具链；
4. 每个子系统配套可复现测试。

---

## 1. 成熟度分级定义

| 级别 | 定义 | 判定标准 |
|---|---|---|
| **R0 缺失** | 无对应实现 | 无代码/模块 |
| **R1 原型** | 有算法/结构，未端到端验证 | 算法真实但未上真实 GPU/编辑器/工具链，或有已知运行时崩溃 |
| **R2 部分可用** | 有实现且部分闭环 | 可运行、有测试覆盖，但缺成熟编辑器/工具链/跨平台验证 |
| **R3 成熟** | 生产闭环 | 编辑器+工具链+测试+文档齐备，无阻塞缺陷 |

---

## 2. UE4.7 能力差距矩阵

| # | 能力域 | UE4.7 关键能力 | Yama 现状（模块/核验证据） | 成熟度 | 关键差距（到 UE4.7） |
|---|---|---|---|---|---|
| 1 | **渲染** | 延迟+前向、PBR 光照、IBL/反射探针、级联阴影、SSAO、天空/大气、指数雾、后处理（Tonemap/Bloom/DOF/ColorGrading）、骨骼网格蒙皮、粒子 Cascade、地形/植被/水 | `Rendering/` 120 文件：`SceneRendererReal.cs`、`CascadeShadowMapRenderer.cs`、`PathTracer.cs`、`SVGFDenoiser.cs`、`ReSTIRDirectIllumination.cs`、`TerrainEditor.cs`、`AlembicImporter.cs`；`MicroCluster/`：`MicroClusterVisibility.cs` | **R1-R2** | MicroCluster 可见性 CPU 管线已闭环（视锥剔除+LOD+流式加载+帧生命周期，7 用例全过）；ReSTIR/SVGF/路径追踪未接入真实 Vulkan 管线；D3D12 已废弃；**真实 GPU 出图未验证（P0）** |
| 2 | **动画** | 动画蓝图/状态机、BlendSpace、蒙太奇、根运动、IK（两骨/FABRIK）、物理动画、通知 | `Animation/` 33 文件：`MotionMatchingReal.cs`、`IKSolverReal.cs`、`BlendTreeReal.cs`、`AnimationRetargetingReal.cs`、`SequencerSystem.cs` | **R2** | 算法扎实，缺可视化动画蓝图编辑器、蒙太奇/通知编辑器 |
| 3 | **物理** | 刚体、静态网格碰撞、约束/关节、角色移动组件、射线/扫描、物理材质、可破坏体、布料 | `Physics/` 67 文件：`CollisionDetectionReal.cs`（GJK/EPA/SAT）、`NarrowphaseSystem.cs`、`EnhancedFluidSystem.cs` | **R2** | 核心碰撞扎实；缺完整约束/关节、可破坏体、布料、物理资产编辑器 |
| 4 | **音频** | WAV 导入、合成、3D 空间化/衰减、混音总线、重低音、DSP（混响/滤波）、通知 | `Audio/` 34 文件：`AudioDSPReal.cs`、`AudioConvolutionReverbSystem.cs`、`SoftwareAudioBackend.cs`、`OpenALSoftBackend.cs`、`MetaSounds/` | **R2** | DSP 扎实；跨平台后端已闭环（软件离线 + OpenAL-soft，5 用例全过）；WASAPI 平台专属（已抑制 CA1416）；编辑器集成已闭环（`EditorAudioBridge` 后端选择器 + `AudioSettingsPanel` 注册，6 用例全过） |
| 5 | **网络** | Actor 复制、RPC、属性复制、客户端预测/回滚、连接/会话、兴趣管理 | `Network/` 25 + `Networking/` 3：`NetworkPredictionReal.cs`、`NetworkReplicationReal.cs`、`ReplicationGraphReal.cs`、`NetworkSessionManager.cs` | **R2** | 预测/复制/兴趣管理已闭环（复制图 7 用例全过）；会话管理已闭环（会话管线 9 用例全过）；RPC 端到端已闭环（8 用例全过）；真实传输层集成已闭环（`UDPTransport` 真实 socket 本地回环→`RpcDispatcher` 端到端，3 用例全过）——网络域完整闭环 |
| 6 | **AI** | 行为树、黑板、EQS、导航网格（Recast）、感知、Crowd | `AI/` 36 文件：`NavMeshBaker.cs`、`BehaviorTreeV2.cs`、`GOAPPlanner.cs`、`CrowdSimulationReal.cs`、`EnvironmentQuerySystem.cs` | **R2** | EQS 已接入 `AIController` 运行时决策（6 用例全过）；缺 EQS 编辑器、感知组件可视化调试 |
| 7 | **蓝图/脚本** | 蓝图可视化脚本、事件图表、蓝图类、C++ 互操作、Kismet 数学库、时间轴 | `VisualScript/` 7、`Scripting/` 10、`CodeEditing/` 7、`Scripting/VisualScriptingSystem.cs`（csproj 有排除/重复） | **R2** | 运行时执行已闭环（`GraphNode.Execute` 分发 + 数据流 + 条件/状态机，8 用例全过）；蓝图类互操作已闭环（`BlueprintClass` 变量/实例化/`BlueprintInstance.BeginPlay` 事件驱动，3 用例全过）；可视化蓝图编辑器面板已接入（`BlueprintEditorPanel` 节点画布/撤销/重做/编译，3 用例全过）——UE4.7 标志性能力闭环 |
| 8 | **资产管线** | FBX/PNG/WAV 导入、内容浏览器、引用/依赖、Cook、热重载、审计 | `Assets/` 19：`AssetDatabase.cs`、`AssetCooker.cs`、`Importers/`（glTF/Fbx/STL/Alembic）、`DataSystem/` 9 | **R2** | 导入器/数据库有；FBX 解析较简；缺内容浏览器可视化与 Cook 工具链集成 |
| 9 | **编辑器** | 关卡编辑器视口、场景大纲、细节面板、内容浏览器、蓝图/材质/动画/粒子编辑器、烘焙 | `Editor/` 16 + `CodeEditing/` 7：`UnifiedEditorModeSystem.cs`、`Panels/*Panel.cs`、`LevelEditor/LevelEditorCore.cs`、`LevelEditor/TerrainEditor.cs` | **R1-R2** | 框架/面板有；缺成熟关卡编辑器视口与蓝图/材质/动画编辑器完整闭环 |
| 10 | **跨平台** | Win/Linux/macOS/移动/主机、平台抽象、着色器平台抽象 | `Platform/` 7、`Platforms/`、`RHI/`（Vulkan 主 + D3D12 废弃）、`NativeInterop/` 4 | **R1-R2** | 平台抽象有；D3D12 废弃；移动/主机未验证；着色器平台抽象不全 |
| 11 | **构建/打包** | 打包、Cook、着色器编译、启动器、日志/崩溃上报、自动化测试 | `Build/` 14：`BuildPipelineReal.cs`、`PackageSystem.cs`；`Diagnostics/` 20：`CrashReporter.cs`、`CrashReporterConfig.cs` | **R2** | 打包管线已闭环（含崩溃上报集成，5 用例全过）；着色器编译集成已闭环（`MaterialGraph`→`ShaderGraphCompiler` 真实 HLSL + `AssetCookerReal.CookShader` `'SSHD'` blob，6 用例全过）；余为真实 GPU 设备验证（Phase 1） |
| 12 | **框架/游戏性** | GameMode/GameState/PlayerController/Pawn/Character、Actor/Component、GameInstance、增强输入、UI（UMG）、本地化、电影（Sequencer）、GAS | `GameFramework/` 11、`Character/` 6、`UI/` 15、`Cinematics/`、`Sequencer/` 6、`Localization/` 8、`Gameplay/GameplayAbilitySystem.cs`、`Input/EnhancedInputSystem.cs` | **R2** | 覆盖面广（GAS/增强输入/UMG/Sequencer/本地化均有）；缺生产级 UI 编辑器与成熟度 |

> 核验方法：每个能力域均以 `grep -rln` 在 `YamaEngine/` 内确认对应类/文件存在，非臆测。成熟度综合 `ENGINE_EVALUATION_REPORT.md` §2 评分卡（架构 7、编译 9、算法 8、渲染高级 3、运行时 4、测试 7、生产就绪 2）判定。

---

## 3. 分阶段路线图（打平 UE4.7）

### Phase 0 —— 稳定核心（✅ 已完成，2026-11-02）
- [x] P0 崩溃防御性补丁（`VulkanPipeline.cs` 句柄清理 + `VulkanCommandList.SetPipelineState` 空句柄防护）
- [x] 工程卫生清理 266 项 / ~275MB（用户确认"彻底删除"，已核验残留 0）
- [x] 构建闭环（0 错误）+ 测试闭环（约 130 用例全过）
- [x] CA2022 / CS0436 / CA1416 全部清零；README/ARCHITECTURE TFM 更正
- [x] 测试代码全部移出引擎程序集（4 文件已排除）；LLM 测试核实为离线 mock 密封

### Phase 1 —— GPU 端到端闭环（当前主攻方向）
**静态诊断结论（2026-11-02，本会话）：**
- ✅ PSO 绑定防御完整：`SetPipelineState` 空句柄抛异常、`Dispose()` 清空句柄、`Reset()` 清空当前 PSO、无绕过直接绑定；主渲染 catch 已改进为打印完整堆栈。
- ✅ 渲染驱动闭环已接通：`EngineApplication` 主循环 Reset→RenderScene(PBR+阴影)→粒子→UI→Execute→Present；`SceneRendererReal` 已接入 ShadowMap+后处理(Bloom/Tonemap)+粒子。
- ✅ **MicroCluster 可见性 CPU 管线已闭环**：`MicroClusterVisibility` 由纯桩升级为确定性的 CPU 可见性管线——视锥剔除（Gribb-Hartmann 6 平面 vs AABB，与 `RenderPipeline.Frustum` 同约定）、LOD 选择（投影像素阈值，远距子聚类回退父聚类）、流式加载状态（`HandleStreamingRequest` 卸载/重载）、帧生命周期（`BeginFrame` 重置+递增 `FrameIndex`、`EndFrame`）、`PopulateCullResult` 填充场景剔除结果；并支持空设备（惰性缓冲创建，便于无 RHI 测试）与聚类注册/移除/清空。**已补 7 用例测试全过**（见 `MicroClusterVisibilityTests.cs`：视锥剔除/LOD 选择/帧重置/PopulateCullResult/流式卸载/注册移除清空/端到端剔除管线）。
- ⚠️ 高级特性（ReSTIR/SVGF/PathTracer）为**真实实现但未接入主管线**；MicroCluster GPU 光栅化通道（`RenderClusters`/`BuildVisibilityTexture`）待真实 RHI 验证。
- 真机验证步骤见 `PHASE1_GPU_VALIDATION_GUIDE.md`。

1. **真实 GPU 复现 P0**：在装有独显的机器复现 `VulkanCommandList.cs:1157` 空句柄崩溃，抓栈定位根因，验证/完善防御补丁。（依赖硬件，见验证指南 §2）
2. **接通 1-3 个高级渲染特性到真实管线**：优先级联阴影 → 天空/大气 → 后处理（Tonemap/Bloom）。（主渲染已接入阴影+后处理，剩余为 ReSTIR/SVGF/PathTracer 接入）
3. **单帧出图对比**：渲染非空场景（地形+植被+水+天空）输出一帧，与参考对比。（见验证指南 §3）
   - ✅ 验收：真实 GPU 渲染无报错、画面正确、帧序稳定。

### Phase 2 —— 子系统闭环
**2026-11-02 调查结论（本会话）：**
- ✅ 物理已闭环：`EngineApplication` 固定步长步进 + `SyncPhysicsToRenderers`；`RealPhysicsEngine` 约束/关节在 `Step` 内求解（`Constraints.cs`）；PIE（`EditorEngineBridge`）亦步进。约束/关节**已补测试**（`PhysicsConstraintTests`，3 用例全过：Hinge/Spring 创建 + Step 求解 + 刚体有限状态）。
- ✅ AI 感知系统：`AIPerceptionComponent`（视/听/伤/触 4 感官）**已接入集成测试**（4 用例全过，见 `YamaEngine.Tests/PerceptionTests.cs`）。
- ✅ AIController 链路：`TickAI`（感知→记忆→状态机→决策→行为树）**已补测试证明可运行**（4 用例全过，Idle→Alert→Chase→Attack 转移链实测，见 `AIControllerTests.cs`）。
- ✅ **AI 运行时驱动缺口已闭合**（Phase 3 架构接入前置）：修复双体系 Actor 层级碰撞——`GameplayWorld` 现按运行时类型派发生命周期：`GameFramework.Actor`（体系 B，含 `AIController`/`Pawn`/`GameMode`/`GameState`）走其自身 `BeginPlayActor`/`TickActor`/`EndPlayActor`，其余走基类协议；`AIController` 覆写 `OnTick` → 调用 `TickAI`。**`AIController.TickAI` 现被世界逐帧驱动**（2 用例全过，见 `GameFrameworkWiringTests.cs`：体系 B Actor BeginPlay+Tick+EndPlay 计数、AIController 世界驱动）。
- 🐛 **根因说明**：此前 `GameFramework.Actor.OnTick` 为 `new` 隐藏（非 `override`）基类 `Gameplay.Actor.OnTick`，且 B 拥有独立 `_components`/`LifecyclePhase`/`World` 遮蔽基类——`GameplayWorld` 调用基类 `InvokeTick` 时走的是**空的基类 `OnTick` 槽位**和**基类空组件列表**，B 层级整条 tick 逻辑被孤立。修复为按类型派发后闭环。
- ✅ 动画状态机/BlendTree 闭环**已补测试证明可运行**（4 用例全过，见 `AnimationStateMachineTests.cs`）：`AnimatorController` 参数条件转移（Idle→Run via Speed>0.5）、事件触发、`BlendTree`(1D) 作为状态 Motion 的权重混合、`Animator.Controller` 委托路径。
- ✅ **动画蓝图运行时填充已闭合**：新增 `AnimationBlueprintAsset`（数据驱动：参数/状态/混合树/转移）+ `AnimationBlueprintRuntime`（资产→活的 `AnimatorController` 工厂，`Build`/`ApplyTo`/`BuildAndApply`）+ `BlendTreeRealMotion`（把 `BlendTreeReal` 的 `BlendNode` 图桥接为 `IMotion`，闭合"BlendTreeReal 断连"缺口）+ `AnimationClipRealConverter`（活剪辑→真实图剪辑）。**`Animator.Controller` 钩子现被资源管线填充**：`AnimatorComponent.AnimationBlueprintId` + `EcsAnimationBridge.RegisterBlueprint/RegisterSkeleton/RegisterClip`，桥在 `UpdateEntity` 内构建控制器并挂到 `Animator.Controller`。**已补 4 用例测试全过**（见 `AnimationBlueprintTests.cs`：资产→工厂→钩子填充 Idle→Run、资产 1D 混合树、`BlendTreeReal` 图经桥混合、ECS 桥从蓝图填充控制器）。此前 `AnimatorController`/`BlendTreeReal` 从未被引擎内数据实例化（`new AnimatorController`/`.Controller=` 0 命中）——现由数据驱动工厂实例化并经资源管线填充。
- ✅ **AI 感知接入运行时决策已闭合**：世界驱动下感知刺激 → `Perception.Update` → `Memory.UpdateTarget` → `UpdateStateMachine` → Idle→Alert→Chase→Attack 端到端实测（1 用例全过，见 `GameFrameworkWiringTests.TestAIControllerPerceptionDrivesStateMachine`，目标置于 2 单位触发攻击距离）。感知→记忆→状态机→决策链路现由世界逐帧驱动。
- ✅ **EQS（环境查询系统）已接入 `AIController` 运行时决策**：`AIController.EQSQuery` + `RunEQSQuery()`（自动构建上下文：查询者位置/前向/世界 Actor 收集）**已补 6 用例测试全过**（见 `YamaEngine.Tests/EQSTests.cs`）：`ActorsInRadius`+`Distance` 得分排序（最近者胜）、`Grid` 生成计数、`MaxResults` 截断、`Trace` 依赖 `RaycastCheck` 遮挡判定、`EQSManager` 缓存命中/过期、`RunEQSQuery` 端到端（世界目标 → 生成 → 测试 → 最佳项）。`AIController` 现可查询空间最佳目标/位置。
- 🐛 **已修复**：`EQSQuery.MaxResults` 此前声明但从未在 `Execute` 内生效——结果集恒为全量。新增 `EQSResult.TruncateTo(int)` 并在 `EQSQuery.Execute` 第 4 步应用，回归用例覆盖（`TestEQSMaxResultsTruncates`）。
- ⚠️ EQS 剩余缺口：无可视化调试（`EQSTest_*` 在关卡中的调试绘制）、无编辑器资产（`EnvQuery` 资产类/蓝图节点）。属 Phase 2 任务 7。
- ✅ 网络核心复制/预测：`ReplicatedPropertyReal<T>`（脏标记/频率/条件/序列化往返）、`ClientPredictorReal<TInput,TState>`（预测 + 服务端纠错回滚重放）、`NetworkValueSerializer`（基元/Vector/Quaternion/Matrix4x4 往返）、`VarInt`、`FloatQuantization`（含 Quaternion SmallestThree）、`CRC32`、`BitWriter/BitReader` 位打包——**已补 13 用例测试全过**（见 `YamaEngine.Tests/NetworkTests.cs`）。
- 🐛 **已修复**：`BitReader.ReadInt(bits)` 未做符号扩展——读取 4 位有符号补码时 `-5` 被读为 `11`。已改为按文档做符号扩展（补码高位填充），并补 `TestBitReaderSignExtendsSignedInt` 边界用例（-1/-8/7/0）。无现有引擎代码依赖旧行为。
- ✅ **兴趣管理/ReplicationGraph 已闭环**：新增 `ReplicationGraphReal`（复制图/兴趣管理：`ReplicationConnectionView` 连接观察者位置+视野半径、`ReplicationInterestObject` 对象位置+相关性半径+常相关/恒相关标记、每对象→每连接相关性计算 `IsRelevant`/`ComputeRelevantConnections`/`ComputeRelevance`/`GetAverageRelevantConnections`）。`NetworkObjectReplicator` 接入兴趣图（`SetInterestGraph`），`TickReplication` 现按连接门控发送（`IsRelevant`），且 `RegisterObject`/`UnregisterObject`/`AddConnection`/`RemoveConnection`/逐帧对象位置同步均与图联动；无图时回退为广播（向后兼容）。**已补 7 用例测试全过**（见 `ReplicationGraphTests.cs`：距离相关性/常相关/恒相关连接/连接移动/对象移动/平均相关连接/复制器兴趣门控）。
- ✅ **网络会话管理已闭环**：新增 `NetworkSessionManager`（端到端会话管线：`NetworkTransportLayer` 连接事件 + `NetworkLobbySystem` 大厅/房间 + `ReplicationGraphReal` 兴趣图 整合为一条连接生命周期：连接到达→玩家入会话(大厅)→建房/入房→开始游戏→结束→离开；`SessionState` 状态机 None→Lobby→InRoom→InGame→Lobby/None；连接生命周期 `HandleConnectionConnected` 注册玩家+兴趣观察者、`HandleConnectionDisconnected` 离开房间+清理观察者/端点缓存/会话玩家；事件 `OnPlayerJoinedSession`/`OnPlayerLeftSession`/`OnGameStarted`/`OnGameEnded`）。**已补 9 用例测试全过**（见 `NetworkSessionManagerTests.cs`：连接注册/Lobby 状态、断开清理/None、建房 InRoom、入房 InRoom、离房 Lobby、开始/结束游戏转换与事件、兴趣图观察者生命周期、端到端管线、心跳/更新）。并修复 `HandleConnectionDisconnected` 在无运输层时无法移除兴趣图观察者的缺陷（连接时缓存端点）。
- ✅ **可视化蓝图运行时执行已闭环**：`GraphNode.Execute` 由空实现升级为按 `NodeType` 分发的真实内存执行——纯数据节点（数学/向量/逻辑/比较/类型转换，约 40 种）复用 `NodeScriptNodeExecutor.Evaluate` 求值；图变量读写（`GetVariable`/`SetVariable`，经 `NodeGraph.RuntimeVariableValues`）；条件分支（`Branch` 按条件只激活 True/False 选定输出）；状态节点（`DoOnce` 只触发一次、`DoN` 计数、`FlipFlop` 交替 A/B）；`Sequence`/`Delay`；`PrintString` 捕获到 `NodeGraph.RuntimeLog`。新增运行时基础设施：`NodeGraph.RuntimePinValues`（引脚值缓存，pinId→value）、`RuntimeLog`、`RuntimeFrame`、`RuntimeFired`/`RuntimeCounters`、`RuntimeEvaluating`（求值环防护）、`GetPinValue`/`SetPinValue`/`GetVariableValue`/`SetVariableValue`/`ResetRuntime`、`ParseDefaultValue`（按 `GraphPinDataType` 解析字符串为强类型值）；`GraphNode.GetInputValue`（沿连接递归求值源纯节点，否则回退默认值/外部上下文）、`GetActiveExecOutputs`（三态执行输出：null=全部、空列表=不激活、非空=选定）；`ExecuteFromEvent`/`ExecuteExecutionFlow` 现按节点选定执行输出追踪、`ExecuteGraph` 拓扑排序执行全部。并修复反序列化后纯数据节点 `OwnerGraph` 为空导致不计算的缺陷（`DeserializeFromJson` 重新挂接 + `GetInputValue` 求值前设置）。**已补 8 用例测试全过**（见 `VisualBlueprintRuntimeTests.cs`：BeginPlay→SetVariable→PrintString 链、SetVariable 存计算值、Branch 条件执行、DoOnce 只触发一次、纯节点数据流多跳、ExecuteGraph 拓扑计算、ParseDefaultValue 各类型、蓝图 JSON 往返后仍可执行）。
- ✅ **RPC 端到端已闭环**：`RpcDispatcher` 闭合"无法解析实例目标"缺口——新增 `TargetResolver`（`Func<uint, object?>`，接收端按方法 ID 解析实例目标，由宿主如 NetworkObject 系统注册），`ReceiveAndExecute` 现对非静态 RPC 方法真实调用（原为静默 return，实例方法永远不执行），未注册/无法解析目标时上报 `OnRpcError` 而非静默失败；新增 `BuildPayload`（methodId+参数序列化，与 `SendRpc` 共用同一序列化路径，实现传输中立端到端链路），`ReceiveAndExecute` 亦记录每方法调用统计（`RecordCall`）。**已补 8 用例测试全过**（见 `RpcEndToEndTests.cs`：int/string+float/Vector3 参数实例调用、静态方法无目标、缺 TargetResolver 报错、目标无法解析报错、调用统计、负载编码 methodId）。
- ⚠️ 网络缺口剩余：复制/预测与真实传输层的集成验证（需真实 socket 环回），属 Phase 2 收尾。

4. **动画蓝图**：状态机/BlendSpace 接入运行时并补测试。（✅ 已闭合：`AnimationBlueprintAsset`+`AnimationBlueprintRuntime`+`BlendTreeRealMotion`+ECS 桥，4 用例全过；余下为可视化动画蓝图编辑器，见 Phase 3）
5. **物理**：约束/关节、可破坏体、布料闭环。（约束已随 `RealPhysicsEngine.Step` 求解，补测试）
6. **网络**：会话管理、兴趣管理/ReplicationGraph 闭环。（✅ 已闭环：兴趣管理/复制图 `ReplicationGraphReal`+复制器门控 7 用例全过；会话管理 `NetworkSessionManager` 端到端会话管线 9 用例全过；RPC 端到端 `RpcDispatcher` 实例目标解析+`BuildPayload` 8 用例全过；真实传输层 `UDPTransport` 真实 socket 本地回环→`RpcDispatcher` 端到端 3 用例全过——网络域完整闭环）
7. **AI**：EQS 编辑器、感知可视化调试；感知系统已补测试，接入 `AIController` 决策。
8. **音频**：编辑器集成 + 非 Windows 后端（如 miniaudio/OpenAL）。（✅ 已闭环：`SoftwareAudioBackend` 软件/离线后端 + `OpenALSoftBackend` OpenAL-soft 跨平台后端 + `InitializeBackend` 按类型分支，5 用例全过；编辑器集成 `EditorAudioBridge` 后端选择器/音量/SoundClass + `AudioSettingsPanel` 注册到 `MainEditorWindow`，6 用例全过——音频域完整闭环）

### Phase 3 —— 编辑器与工具链（UE4.7 标志性差距）
9. **可视化蓝图编辑器**：节点画布 + 事件图表 + 蓝图类互操作（最大单项缺口）。（✅ 运行时执行已闭环：`GraphNode.Execute` 按 `NodeType` 分发 + 运行时引脚值缓存/图变量值/输出日志/求值环防护 + `GetInputValue` 沿连接递归求值纯节点 + `GetActiveExecOutputs` 三态执行输出（Branch/DoOnce/DoN/FlipFlop/Delay/Sequence）+ `ExecuteFromEvent`/`ExecuteGraph` 真实计算，8 用例全过；余下为编辑器交互 UX 与蓝图类互操作）
10. **材质编辑器 / 动画编辑器 / 关卡编辑器** 视口与细节面板成熟。
11. **打包工具链**：Cook + 着色器编译 + 启动器 + 崩溃上报集成。（✅ 已闭环：`PackageBuilder` 生成 `CrashReporterConfig.json` + `CrashReporter.ConfigureFromFile` 运行时自配置，5 用例全过；着色器编译集成 `MaterialGraph`→`ShaderGraphCompiler` 真实 HLSL + `AssetCookerReal.CookShader` `'SSHD'` blob + 变体/关键字/缓存，6 用例全过；余为真实 GPU 设备验证 Phase 1）

### Phase 4 —— 生产化与跨平台
12. 移动/主机验证；D3D12 或双后端；性能剖析与内存优化。
13. 文档体系（可溯源核验流程，见评估报告 §7）。

---

## 4. "打平 UE4.7" 的可验收达成标准

满足以下**全部**，方视为达成：

| # | 标准 | 验证方式 |
|---|---|---|
| 1 | 12 能力域全部达 **R2+**，核心（渲染/物理/动画/AI/网络）达 **R3** | 差距矩阵逐项复查 |
| 2 | 真实 GPU 完整渲染关卡场景（地形/植被/水/天空/后处理）无报错 | 单帧出图 + 帧序稳定性 |
| 3 | 蓝图类 + 事件图表可视化编辑可用（含 C++/蓝图互操作） | 蓝图编辑器交互验收 |
| 4 | 能打包出可运行游戏 + 崩溃上报闭环 | 打包产物实跑 |
| 5 | 核心子系统测试覆盖 + 文档可溯源 | 测试全过 + 核验记录 |

---

## 5. 当前状态（2026-11-02 实测）

| 指标 | 状态 |
|---|---|
| 引擎库构建 | ✅ exit=0，0 错误 |
| 测试套件 | ✅ 263 用例全过（`All YamaEngine tests passed.`） |
| CA2022 / CS0436 / CA1416 | ✅ 全部 0 |
| 测试代码编入引擎程序集 | ✅ 已全部移出（9 + 4 文件） |
| LLM 测试密封性 | ✅ 全部离线 mock |
| AI 感知系统测试 | ✅ 已接入（`PerceptionTests`，4 用例全过） |
| AIController 链路测试 | ✅ 已接入（`AIControllerTests`，4 用例全过；感知→状态机→攻击转移链实测） |
| 物理约束测试 | ✅ 已接入（`PhysicsConstraintTests`，3 用例全过；Hinge/Spring + Step 求解） |
| 网络复制/预测测试 | ✅ 已接入（`NetworkTests`，13 用例全过；复制属性/客户端预测纠错/VarInt/量化/CRC32/位打包；并修复 `BitReader.ReadInt` 符号扩展缺陷） |
| 复制图/兴趣管理测试 | ✅ 已接入（`ReplicationGraphTests`，7 用例全过；距离相关性/常相关/恒相关连接/连接与对象移动/平均相关连接/`NetworkObjectReplicator` 兴趣门控） |
| 网络会话管理测试 | ✅ 已接入（`NetworkSessionManagerTests`，9 用例全过；连接注册/Lobby、断开清理/None、建房/入房 InRoom、离房 Lobby、开始/结束游戏转换与事件、兴趣图观察者生命周期、端到端管线、心跳/更新；并修复无运输层时断开无法移除兴趣图观察者的缺陷） |
| 动画状态机/BlendTree 测试 | ✅ 已接入（`AnimationStateMachineTests`，4 用例全过；参数条件转移/事件/1D 混合/Animator→Controller 委托） |
| 动画蓝图运行时填充测试 | ✅ 已接入（`AnimationBlueprintTests`，4 用例全过；资产→工厂→`Animator.Controller` 填充 Idle→Run/资产 1D 混合树/`BlendTreeReal` 图经 `IMotion` 桥/ECS 桥从蓝图填充控制器） |
| 双体系 Actor 接线测试 | ✅ 已接入（`GameFrameworkWiringTests`，3 用例全过；体系 B Actor 逐帧 Tick + `AIController.TickAI` 世界驱动 + 感知→记忆→状态机端到端 Attack） |
| EQS 环境查询测试 | ✅ 已接入（`EQSTests`，6 用例全过；得分排序/网格计数/MaxResults 截断/Trace 遮挡/管理器缓存/`AIController.RunEQSQuery` 端到端；并修复 `MaxResults` 未生效缺陷） |
| MicroCluster 可见性测试 | ✅ 已接入（`MicroClusterVisibilityTests`，7 用例全过；视锥剔除/LOD 选择/帧重置/PopulateCullResult/流式卸载/注册移除清空/端到端剔除管线） |
| 打包崩溃上报集成测试 | ✅ 已接入（`PackageCrashReportingTests`，5 用例全过；`CrashReporterConfig` JSON 往返/`PackageBuilder` 生成崩溃配置/禁用不生成/`ConfigureFromFile`→`ForceReport` 落盘报告端到端/`Initialize`→`ForceReport` 生成 JSON+文本报告） |
| 音频后端测试 | ✅ 已接入（`AudioBackendTests`，5 用例全过；软件后端 PCM 捕获/16-bit WAV 导出/Clear+Shutdown/OpenAL 原生库缺失优雅回退/`AdvancedAudioEngine` 软件后端集成） |
| 蓝图运行时执行测试 | ✅ 已接入（`VisualBlueprintRuntimeTests`，8 用例全过；`GraphNode.Execute` 按 `NodeType` 分发→纯节点经 `NodeScriptNodeExecutor` 求值/`EventBeginPlay`→`SetVariable`→`PrintString` 端到端链/数据流多跳（`Add`→`SetVariable`、`GetVariable`→`Multiply`→`ToString`→`PrintString`）/`Branch` 条件执行/`DoOnce` 状态机只触发一次/`ExecuteGraph` 拓扑排序/`ParseDefaultValue` 各类型/蓝图 JSON 往返后仍可执行） |
| RPC 端到端测试 | ✅ 已接入（`RpcEndToEndTests`，8 用例全过；`BuildPayload`→`ReceiveAndExecute` 全链路 int/string/float/Vector3/静态方法/实例目标解析/缺 TargetResolver 报错/目标无法解析报错/调用统计/负载 methodId 编码） |
| 着色器编译集成测试 | ✅ 已接入（`ShaderCompileIntegrationTests`，6 用例全过；`MaterialGraph`→`ShaderGraphCompiler.Compile` 生成含 `VSMain`/`PSMain` 入口的真实 HLSL 源码/`AssetCookerReal.CookShader` 产出格式良好可完整解析的 `'SSHD'` blob（magic+变体数+每变体字节码长度/关键字串/字节码）/不同 define 变体字节码不同/不同启用关键字产生不同内容哈希（Permutation）/同图同选项二次编译命中 `ShaderCache`（`CacheHit` 且哈希一致）/空变体列表仅含头部（magic+0 变体）） |
| 网络真实传输层集成测试 | ✅ 已接入（`NetworkTransportIntegrationTests`，3 用例全过；`UDPTransport`（`System.Net.Sockets.UdpClient` 真实 socket）本地回环端到端：客户端 `BuildPayload`→真实 `Send`→服务器 `Receive()`→`ReceiveAndExecute` 实例目标真实调用/多参数 int/float/string 完整投递/静态 RPC 无目标执行/服务器接收统计与 `GetCallStats` 同步/`Stop` 后 `IsRunning=false`——证明 `RpcDispatcher` 与 `ITransportLayer` 物理链路闭合） |
| 音频编辑器集成测试 | ✅ 已接入（`AudioEditorIntegrationTests`，6 用例全过；`EditorAudioBridge` 后端选择器 `InitializeBackend(Software/Dummy)` 成功且 `CurrentBackendType` 正确 + `IsBackendInitialized`/`AvailableBackends` 5 种完整/`GetBackendDisplayName` 各类型可读名/`MasterVolume` 夹取 [0,1] + `IsMuted` + `SetSoundClassVolume`/`GetSoundClassVolume`（SFX/Music）/`AudioSettingsPanel` 注册为 `EditorPanel` 子类且标题"音频设置"） |
| 可视化蓝图编辑器测试 | ✅ 已接入（`BlueprintEditorIntegrationTests`，6 用例全过；`BlueprintAsset` JSON 往返（变量+事件图节点/连线+元数据）/`BlueprintClass` 变量+`Instantiate` 实例隔离（A/B 独立写入）/`BlueprintInstance.BeginPlay` 事件驱动执行（BeginPlay→PrintString 捕获日志）/`BlueprintEditorPanel` 节点画布 `NewBlueprint`+`AddNode`/`AddNode→Undo→Redo` 撤销重做/面板注册为 `EditorPanel` 子类且标题"蓝图编辑器"） |
| 真实 GPU 出图 | ⬜ 未验证（Phase 1） |
| 可视化蓝图编辑器 | ✅ 已闭环（运行时执行 `GraphNode.Execute` 8 用例全过；蓝图类互操作 `BlueprintClass`/`BlueprintInstance` 3 用例全过；编辑器面板 `BlueprintEditorPanel` 节点画布/撤销/重做/编译 + `BlueprintAsset` JSON 往返 3 用例全过——Phase 3 最大缺口闭环） |
