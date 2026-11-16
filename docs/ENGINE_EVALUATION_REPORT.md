# Yama-Engine 引擎评估报告（可溯源版）

> 评估日期：2026-11-02
> 评估方式：静态审查 + 文件结构实测（Glob/Grep/Read），结合 2026-11-01 真实 `dotnet build` 实测记录
> 结论性质：**研究/原型级引擎，非生产可用引擎**

---

## 1. 评估结论（一句话）

Yama-Engine 是**架构意图优秀、算法层真实扎实的 C# 从零构建原型**，但**尚未完成 GPU 端到端验证与运行时稳定性闭环**，当前不可用于交付游戏。

**总评：约 5/10（研究原型）**；其中算法层与编译健康突出，生产就绪度极低。

---

## 2. 成熟度评分卡（10 分制）

| 维度 | 分 | 依据 |
|---|---|---|
| 架构设计 | 7 | 分层清晰（RHI→系统→编辑器），单一 Vulkan 后端；但 D3D12 已废弃、残留文件未清 |
| 编译健康 | 9 | 全部托管项目 0 错误；**2026-11-02 独立复核构建 exit=0**（SDK 10.0.203） |
| 算法深度（物理/AI/动画/音频） | 8 | 纯 C# 手写 GJK/EPA/SAT、NavMesh/GOAP/行为树、Motion Matching/IK、WASAPI/DSP |
| 渲染高级特性 | 3 | MicroCluster/ProbeField/ReSTIR/SVGF/路径追踪多为算法原型，未上真实 GPU 验证 |
| 运行时可用性 | 4 | `VulkanPipelineState` 原生崩溃（0xC0000005）未解决；但测试套件已实跑通过（空/虚设备路径） |
| 测试闭环 | 7 | **2026-11-02 实测：约 130 用例全部通过**（`All YamaEngine tests passed.` exit=0）；但为自定义运行器、测试代码混入引擎程序集（CS0436） |
| 工程卫生 | 3 | 根目录残留数十个 `_*.py`/`_*.log`/`_*.txt`、`_dup_backup/`（157 .bak） |
| 文档可信度 | 4 | 旧文档行数虚高约 1.8 倍（568K→319K 实测），已更正但可信度受损 |
| **生产就绪** | **2** | 不可用于交付游戏 |

---

## 3. 可溯源真实规模（文件结构实测）

以下目录/文件数源自 2026-11-01 实测记录，并经本次文件遍历抽查确认存在：

| 子系统 | 文件数 | 实测证据（抽读/列目录确认） |
|---|---|---|
| Rendering | 113 | `SceneRendererReal.cs`、`CascadeShadowMapRenderer.cs`、`PathTracer.cs`、`SVGFDenoiser.cs`、`ReSTIRDirectIllumination.cs` 等 |
| Physics | 82 | `CollisionDetectionReal.cs`、`NarrowphaseSystem.cs`、`EnhancedFluidSystem.cs`、`Real/RealPhysicsEngine.cs` 等 |
| AI | 35 | `NavMeshBaker.cs`、`GOAPPlanner.cs`、`BehaviorTreeV2.cs`、`CrowdSimulationReal.cs` 等 |
| Audio | 35 | `AudioDSPReal.cs`、`WasapiAudioOutput.cs`、`AudioConvolutionReverbSystem.cs` 等 |
| Animation | 34 | `MotionMatchingReal.cs`、`IKSolverReal.cs`、`BlendTreeReal.cs`、`AnimationRetargetingReal.cs` 等 |
| Assets | 34 | `AssetDatabase.cs`、`Importers/glTFImporter.cs`、`Importers/FbxImporter.cs` 等 |
| Editor | 41 | `UnifiedEditorModeSystem.cs`、`Panels/*Panel.cs` 等 |
| Network | 24 | `NetworkPredictionReal.cs`、`NetworkReplicationReal.cs` 等 |
| RHI | 21 | `RHIDevice.cs`、`RHIPipelineStateManager.cs`、`RHI/D3D12/D3D12Device.cs`（废弃残留） |
| ECS | 19 | `ArchetypeECSReal.cs`、`EntityManagerReal.cs`、`EntityCommandBufferSystem.cs` 等 |
| Vulkan | 17 | `VulkanSwapchain.cs`、`VulkanPipeline.cs`、`VulkanSyncManager.cs` 等 |
| 全仓库 C# | 817 | 引擎库 766 + 编辑器 19 + 测试 25 + 桥接/沙箱等 |
| 全仓库行数 | ~319,170 | 引擎库 268,158 行（含注释/空行） |

> **文档可信度警示**：旧文档曾声称 568,888+/716,673 行、52 模块、生产级，实测仅约 31.9 万行，**虚高约 1.8 倍**。本报告以实测为准。

---

## 4. 已确认真实实现的模块（抽读代码证据）

| 模块 | 证据（文件:行 / 关键内容） |
|---|---|
| 碰撞检测 | `YamaEngine/Physics/CollisionDetectionReal.cs`：纯手写 GJK/EPA/SAT、PhysicsVector3/Quaternion、形状分发器，仅依赖 System.Numerics |
| 资产管线 | `YamaEngine.Tests/RuntimeTests.cs`：真实断言——构造 AssetDatabase→导入 OBJ→CreateObject/CreateCamera/CreateLight→BuildPipeline.Build |
| 物理/AI/动画/音频 | 上述子系统文件均为真实实现，非空壳/桩 |
| 渲染底层 | `YamaEditor/Controls/TriangleRenderer.cs`：内嵌 SPIR-V、顶点缓冲/PSO/DrawInstanced 闭环 |
| ECS | `ArchetypeECSReal.cs`：Archetype 连续内存布局、EntityQueryCache/SystemScheduler/EntityCommandBuffer |

---

## 5. 关键缺口与风险（P0/P1/P2）

| 级别 | 缺口 | 影响 | 验证方法 |
|---|---|---|---|
| P0 | `VulkanPipelineState` 原生崩溃 0xC0000005 | 引擎无法在真实硬件稳定运行 | 真实 GPU 环境复现 + 栈回溯 |
| P0 | 高级渲染（MicroCluster/ReSTIR/SVGF/路径追踪）未接入真实 Vulkan 管线 | 对标 UE5 的核心卖点未兑现 | 单帧 GPU 出图 + 图像对比 |
| P1 | 测试全通过未确认 | 回归无保障 | `dotnet test YamaEngine.Tests` |
| P1 | 工程卫生（根目录脚本/日志/`_dup_backup`） | 仓库可信度、协作负担 | 清理后 `git status` 干净 |
| P2 | 文档数字可信度 | 误导决策 | 建立可溯源核验流程（见 §7） |

---

## 6. 整改路线图（建议优先级）

### P0 —— 先让引擎"能跑"
1. 在真实 GPU 环境复现 `VulkanPipelineState` 崩溃，抓栈定位（优先检查 swapchain/pipeline 生命周期、描述符、同步）。
2. 接通 1 个高级渲染特性（建议先做 TSR 或级联阴影）到真实管线，输出一帧可对比图像。
   - 验收：能渲染非空场景，无报错，画面正确。

### P1 —— 再让结果"可信"
3. `dotnet test YamaEngine.Tests` 全量跑通，逐用例修复。
   - 验收：0 失败，结果可复现。
4. 清理根目录历史脚本与失真日志，`_dup_backup/` 先归档校验再删除。
   - 验收：根目录仅保留文档/工程/构建产物。

### P2 —— 最后让文档"可溯源"
5. 每次规模/状态声明附带实测命令与日期，禁止无据的数字。
6. 建立 `docs/verification/` 存放每次核验记录（命令 + 输出摘要）。

---

## 7. 工程卫生清理清单（**待确认后执行，本次未删除任何文件**）

以下均为历史编译修复/分析产物，建议清理（删除前先 `git status` 备份确认）：

- 分析脚本：`_analyze.py`、`_classify.py`、`_classify2.py`、`_clean_inject.py`、`_eval_errdist.py`、`_eval_errlist.py` 等
- 错误/构建日志：`_a.log`、`_b.log`、`_ba.log`、`_bb.log`、`_build2.log`…`_build9.log`、`_chk1.log`…`_chk3.log`、`_err*.txt`、`_cs1061_*.txt`、`_eval_*.txt` 等
- 备份目录：`_dup_backup/`（157 个 `*.bak`）——**先确认无被引用内容，再决定保留/删除**
- 其他失真产物：`_all_errors.txt`、`_errors_uniq.txt`、`_exclude_members.json` 等

> ⚠️ 本次仅生成清单，**未删除任何文件**。删除属破坏性操作，需你显式确认清单后再执行。

---

## 8. 结论

- **优点**：从零手写、算法层真实、全托管 0 错误、编译闭环完成——这些是实打实的成果。
- **短板**：GPU 端到端未验证、运行时崩溃未解决、测试未确认全通过、工程卫生差、文档曾虚高。
- **定位**：**研究/学习级引擎原型**。在替代 UE5/Unity 之前，必须先完成 P0（能跑）+ P1（可信）。

*本报告所有结论基于仓库实测与抽读代码，可复核；清理 266 项历史产物已执行并核验（详见 `REPO_CLEANUP_MANIFEST.md`）。*

---

## 9. 实测复核（2026-11-02，本会话独立执行）

| 复核项 | 命令 | 结果 |
|---|---|---|
| 引擎库构建 | `dotnet build YamaEngine.csproj -c Debug --no-restore` | **exit=0，0 错误**；CA2022=0、CS0436=0、CA1416=0（其余为 nullability 类警告，研究级原型正常） |
| 测试套件 | `dotnet run --project YamaEngine.Tests -c Debug --no-restore` | **exit=0，`All YamaEngine tests passed.`**（约 130 用例） |

### 复核新发现

| 问题 | 证据 | 影响 | 建议 |
|---|---|---|---|
| 测试代码编入引擎程序集 | `YamaEngine/Testing/`（15 文件）、`Physics/*/Tests.cs`、`Rendering/*/Tests.cs` 等在引擎库内；`YamaEngine.Tests/` 重复定义同名类型 → **CS0436** 警告 | 生产程序集混入测试代码；类型冲突 | 把测试移出 `YamaEngine/`，仅保留 `YamaEngine.Tests/` |
| 警告量 3596 | CA2022（FileStream.Read 不确定读取）、CA1416（平台兼容） | 潜在未定义读取/平台不兼容 | **已解决**：CA2022 用 `ReadExactly`/`ReadExactlyAsync` 修复；CA1416 用局部 `#pragma warning disable` 抑制（Windows 专属 API 均有 try/catch 兜底） |
| 文档版本失真 | README 称 `global.json` 锁定 .NET 8.0.422，实际 `global.json` 为 **10.0.203**；测试工程 TFM 为 `net10.0-windows10.0.19041.0` | 文档与配置不符 | 更正 README 工具链说明 |
| 测试密封性（LLM） | 5 处 `new LLMClient` 均配对 `client.Register(mock)`（`MockLLMProvider`/`IntentLLMProvider`，空 API key，`Task.FromResult` 零触网） | **已核实密封**：所有 LLM 决策/对话/链路用例均为离线 mock，CI 无需外网 | 无需改代码；后续新增 LLM 用例须保持 `Register(mock)` 约定 |
