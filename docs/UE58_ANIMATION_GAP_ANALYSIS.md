# Yama-Engine vs UE5.8 深度级动画能力差距矩阵

> 生成日期：2026-11-02（本会话，A-F 全部闭环）
> 依据：`YamaEngine/Animation/` + 新增 5 个能力文件实测（Grep 公开 API 全量核验）
> 目标：**把动画能力域提升至 UE5.8 深度级**（在已打平 UE4.7 动画 R2 基础上，补 UE5.8 标志性深度能力并逐个闭环）

---

## 0. 关键结论（先说重点）

Yama-Engine 动画域现有 **34 文件 / 约 15 个子系统**，已具备 UE5.8 的**全部深度级骨架**：
- 状态机（参数/条件/过渡/并行/混合曲线/Conduit/历史/惰性混合）
- BlendSpace 1D/2D/3D（三角剖分/重心坐标/网格采样/Delaunay 四面体化）
- Motion Matching（轨迹/特征/KDTree/数据库/求解器/混合）
- IK（两骨/FABRIK/CCD/LookAt/Limb/Spring/脚部/全身体 FBIK/脚部锁定/脚部放置）
- 蒙太奇（Section/Slot/Segment/BranchPoint/实例/混合进出/通知系统/多 Slot 分层）
- 重定向（BoneMapping/RetargetClip/Pose/RootMotion/IKTarget）
- Sequencer（轨道体系/播放器/录制器/电影渲染/动画/相机/音频/材质/粒子/子序列）
- 动画蓝图（数据驱动资产 + 运行时工厂 + 编辑器面板）
- ControlRig（完整约束求值器：层次/空间切换/Parent/Aim/IK/Transform 约束）
- PoseAsset（姿态资产 + Blend Poses by weight 混合节点）

**6 个 UE5.8 深度级关键缺口已全部闭环（A-F ✅）：**

| # | 缺口 | 现状 | UE5.8 深度级要求 | 优先级 | 状态 |
|---|---|---|---|---|---|
| A | **ControlRig 完整约束求值器** | 仅数据结构 → 已建 `RigHierarchy`/约束系统/空间切换/求值管线 | `RigHierarchy`（骨骼/控制/空/曲线层次）+ 约束系统（Parent/Aim/IK/Transform Constraint）+ 空间切换（Local/Global/Parent/World）+ 前向求值管线 | **P0** | ✅ |
| B | **Full Body IK（FBIK）+ 脚部锁定/放置** | 单链 IK 齐全 → 已建多链 FBIK + FootLock + FootPlacement | 多链全身体 IK 求解 + 脚部锁定（Foot Locking 曲线）+ 脚部放置（Foot Placement） | **P0** | ✅ |
| C | **BlendSpace 3D** | 1D/2D 齐备 → 已建 `BlendSpace3D`（Delaunay 四面体化） | 三维混合空间（四面体/格点采样） | P1 | ✅ |
| D | **蒙太奇通知系统 + 分层** | 蒙太奇有 Section/Slot/BranchPoint → 已建 `AnimNotify`/`AnimNotifyState`/`MontageNotifyTrack`/`MontageLayerStack` | AnimNotify（瞬时通知）+ NotifyState（持续状态）+ 蒙太奇分层（多 Slot 叠加权重） | P1 | ✅ |
| E | **动画蓝图编辑器面板** | 有数据+工厂 → 已建 `AnimGraphEditorPanel`（状态图/参数/预览） | AnimGraph 编辑器：状态图/混合图/事件图可视化 + 参数面板 + 预览视口 | P1 | ✅ |
| F | **PoseAsset（姿态资产/姿态混合）** | 无 → 已建 `PoseAsset`/`BlendPosesNode` | PoseAsset 资产 + Pose 混合节点（Blend Poses） | P2 | ✅ |

**测试套件：300 项全部通过**（会话开始 251 → 300，本次动画域闭环 +49）。

---

## 1. 现有动画子系统盘点（核验证据）

| 子系统 | 文件 | 公开 API 摘要（Grep 核验） | 状态 |
|---|---|---|---|
| 骨架/蒙皮 | `AnimationCore.cs` `PoseEvaluationReal.cs` | `Skeleton`/`SkeletonPose`/`AnimationClipReal`/CPU 蒙皮 | ✅ R2 |
| 动画图（真实） | `AnimGraphEvaluatorReal.cs` | `AnimGraphNodeReal` 派生：`Clip`/`BlendSpace1D`/`BlendSpace2D`/`StateMachine`/`Layered`/`Additive`/`IK`/`TransformBone`/`Spline`/`Ragdoll`/`PoseSnapshot`/`Output`；`AnimGraphInstance`（AddNode/Connect/SetParameter/Tick） | ✅ R2 |
| 节点脚本/蓝图 | `AnimNodeScriptSystem.cs` | `AnimNodeBase` 蓝图式 + `AnimInstance`（NativeInitialize/Update/Evaluate、Montage_Play/Stop、Get/Set 参数）+ `SkeletalMeshComponent` | ✅ R2 |
| 状态机（真实） | `AnimStateMachineReal.cs` | `AnimParameter`(Bool/Float/Int/Trigger)、`TransitionCondition`、`AnimTransitionReal`、`AnimConduit`、`AnimStateReal`、`ParallelStateEntry`、`AnimStateMachineReal`(AddState/AddTransition/ForceTransition/Tick/GetCurrentPose) | ✅ R2 |
| 状态机（节点） | `AnimStateMachineNode.cs` | `AnimState`/`AnimTransition`/`StateHistory`/`InertialBlend`/`AnimStateMachineNode` | ✅ R2 |
| 混合树 | `BlendTreeReal.cs` `BlendTree.cs` | `BlendTreeReal`/`BlendNode` 图 | ✅ R2 |
| BlendSpace | `BlendSpaceSystem.cs` | `BlendSpace1D`(AddSample/Rebuild/GetSampleWeights/Sample)、`BlendSpace2D`(AddSample/Triangulate/GetSampleWeights/NearestThreeWeights/Sample)、`BlendSpaceTriangle`/`Barycentric`、`BlendSpaceGrid`(Build/Rebuild) | ✅ R2 |
| 蒙太奇 | `AnimMontageSystem.cs` | `AnimMontage`(AddSection/AddSlot/AddSegment/AddBranchPoint)、`MontageInstance`(StartBlendIn/Out/Update/JumpToSection/SetNormalizedTime/SampleCurrentPose/UsesSlot/GetSlotWeight)、`MontageInstanceManager` | ✅ R2 |
| IK | `IKSolverReal.cs` `FootIKSystem.cs` | `TwoBoneIKSolverReal`/`LookAtIKSolver`/`FABRIKSolverReal`/`CCDIKSolverReal`/`LimbIKSolver`/`SpringIKSolver`/`IKSolverUtilities`、`FootIKSystem` | ✅ R2（单链） |
| Motion Matching | `MotionMatchingReal.cs` `MotionMatchingSystem.cs` | `MotionMatchingTrajectory`(BuildFromRootMotion/BuildDesired)、`MotionMatchingFeature`(ComputeFromClip/WeightedDistance)、`MotionMatchingFrame`、`KDTree`(Build/FindNearest/FindKNearest)、`MotionMatchingDatabase`(Build/GetFrame)、`MotionMatchingSolver`(BuildDatabase/FindBestMatch/Search/Update/Blending) | ✅ R2 |
| ControlRig | `ControlRig\ControlRigCore.cs` `ControlRig\ControlRigConstraints.cs` | `RigValue`/`RigTransform`/`RigElementType`/`RigElement`/`ControlRigControl`/`RigCurve`/`RigPortSpec`/`RigNode`/`ControlRigAsset`(Save/Load/FindControl/FindCurve)、`RigHierarchy`(AddElement/ComputeGlobals/Propagate/GetParentIndex)、`RigSpace`/`RigSpaceUtil`(ToWorld/ToLocal)、`RigConstraint`(Parent/Aim/IK/Transform)、`ControlRigEvaluator`(Evaluate) | ✅ R3（完整约束求值器） |
| 重定向 | `AnimationRetargetingReal.cs` | `BoneMappingReal`/`RetargetingSettings`/`AnimationRetargeter`(Initialize/RetargetClip/RetargetPose/RetargetRootMotion/RetargetIKTarget/GetBoneMapping/GetUnmapped*)、静态 `Retarget`/`BuildBoneMapping`/`ComputeBoneLengthRatio` | ✅ R2 |
| Sequencer | `SequencerSystem.cs` | `LevelSequence`/`SequencerTrack` 派生（Transform/Property/Event/Camera/Audio/Animation/Material/Particle/Scene/SubSequence）、`SequencerPlayer`(Play/Pause/Seek/Update)、`SequenceRecorder`、`MovieRenderer`、`FbxAnimationExporter` | ✅ R2 |
| 动画蓝图 | `AnimationBlueprintAsset.cs` | `AnimationBlueprintAsset`(AddFloat/Bool/Trigger、AddClipState/AddBlendTreeState1D/AddTransition)、`AnimationBlueprintRuntime`(Build/ApplyTo/BuildAndApply)、`BlendTreeRealMotion`、`AnimationClipRealConverter` | ✅ R2（数据+工厂） |
| Full Body IK | `FullBodyIK.cs` | `FullBodyIKSolver`（多链 FABRIK 层次求解 + 逐链权重/优先级）、`FootLockSystem`（锁定曲线 + 保持世界位置）、`FootPlacementSystem`（目标地面点 + 朝向对齐 + 预测）、`IKMath`/`TwoBoneIK` 共享工具 | ✅ R3（UE5.8 深度） |
| BlendSpace 3D | `BlendSpace3DSystem.cs` | `BlendSpace3D`（三轴样本 + 真正 3D Delaunay 四面体化 + 四面体重心插值 + 凸包外最近 4 反距离回退）、`BlendSpaceSample3D`/`BlendSpaceTetrahedron` | ✅ R3（UE5.8 深度） |
| 蒙太奇通知+分层 | `MontageNotifySystem.cs` | `AnimNotify`（瞬时 Fire）/`AnimNotifyState`（区间 Begin/End）/`MontageNotifyTrack`（命名轨道）/`MontageNotifyRuntime`（逐帧评估）/`AnimNotifyLog`（事件日志）、`MontageSlotLayer`/`MontageLayerStack`（多 Slot 权重叠加 + 骨骼蒙版） | ✅ R3（UE5.8 深度） |
| 动画蓝图编辑器 | `Editor\Panels\AnimGraphEditorPanel.cs` | `AnimGraphEditorPanel`（状态图/转移/参数面板 + BuildPreview + SetFloat/Bool/Int/Trigger + TickPreview/PreviewState），注册 `MainEditorWindow` | ✅ R3（UE5.8 深度） |
| PoseAsset | `PoseAssetSystem.cs` | `PoseEntry`（命名姿态）/`PoseAsset`（AddPose/SetBone/CapturePose/Evaluate/ApplyToPose）/`BlendPosesNode`（多姿态按权重混合） | ✅ R3（UE5.8 深度） |
| 渲染桥 | `AnimationRenderBridge.cs` `SkinnedMeshAnimatorComponent.cs` `EcsAnimationBridge.cs` | 渲染桥/ECS 桥/蒙皮动画组件 | ✅ R2 |
| 时间线面板 | `Editor\Panels\TimelineEditorPanel.cs` | ECS 实体动画时间轴预览（非完整动画序列编辑器） | ⚠️ R1 |

---

## 2. UE5.8 深度级闭环计划

### 任务 A —— ControlRig 完整约束求值器（P0）✅ 已完成
**目标**：把 ControlRig 从"数据结构原型"提升为"可求值的完整约束系统"。
- [x] A1 `RigHierarchy`：元素层次（骨骼/控制/空/曲线），父子关系、全局/局部变换求值
- [x] A2 空间切换：`RigSpace`（Local/Global/Parent/World），`RigSpaceUtil.ToWorld`/`ToLocal` 转换
- [x] A3 约束系统：`ParentConstraint`（维持父级偏移）、`AimConstraint`（朝向目标）、`IKConstraint`（两骨 IK 到目标）、`TransformConstraint`（匹配源变换）
- [x] A4 求值管线：`ControlRigEvaluator.Evaluate`（控制输入→前向求值→约束→骨骼写回）
- [x] A5 测试：`ControlRigEvaluationTests.cs`（7 用例全过：层次全局/父约束/瞄准/IK/变换/空间往返/端到端写回骨骼；测试总数 263→270）

### 任务 B —— Full Body IK + 脚部锁定/放置（P0）✅
**目标**：单链 IK 扩展为全身体 + 脚部深度能力。
- [x] B1 `FullBodyIKSolver`（`FullBodyIK.cs`）：多链（脊柱/手臂/腿）层次求解，FABRIK 端到端 + 逐链权重；共享 `IKMath`/`TwoBoneIK` 工具
- [x] B2 `FootLockSystem`（`FullBodyIK.cs`）：脚部锁定曲线（锁定/解锁时间戳）+ 脚部锁定求解（经 `FullBodyIKSolver` 保持世界位置）
- [x] B3 `FootPlacementSystem`（`FullBodyIK.cs`）：脚部放置（目标地面点 + 朝向对齐 + 预测）
- [x] B4 测试：`FullBodyIKTests.cs`（6 用例全过：FBIK 链解算/多链层次/FootLock 锁定/FootLock 解锁/FootPlacement 对齐/两骨 IK 几何；测试总数 270→276）

### 任务 C —— BlendSpace 3D（P1）✅
**目标**：BlendSpace 从 1D/2D 提升为三轴深度级（对应 UE5.8 UBlendSpace）。
- [x] C1 `BlendSpace3D`（`BlendSpace3DSystem.cs`）：样本（剪辑+三维坐标）、三轴参数（X/Y/Z）、真正 3D Delaunay 四面体化（Bowyer–Watson 推广，数值外接球判定）、四面体重心插值、凸包外最近 4 样本反距离回退
- [x] C2 测试：`BlendSpace3DTests.cs`（6 用例全过：样本点权重=1 / 正四面体质心 0.25 / 权重和恒 1 且非负 / 线段插值姿势 / 凸包外回退 / 四面体化合法；测试总数 276→282）

### 任务 D —— 蒙太奇通知系统 + 分层（P1）✅
**目标**：蒙太奇从段/分支点原型提升为通知系统 + 多 Slot 分层（UE5.8 深度级）。
- [x] D1 `MontageNotifySystem.cs`：`AnimNotify`（瞬时 Fire 一次）/`AnimNotifyState`（区间 Begin/End）/`MontageNotifyTrack`（命名轨道，多轨道并行）/`MontageNotifyRuntime`（绑定实例逐帧评估）/`AnimNotifyLog`（事件日志）
- [x] D2 `MontageLayerStack`（`MontageNotifySystem.cs`）：`MontageSlotLayer`（SlotName+Weight+实例+骨骼蒙版），多 Slot 按权重从低到高叠加合成最终姿势（蒙版控制上层覆盖）
- [x] D3 测试：`MontageNotifyTests.cs`（6 用例全过：通知触发一次 / 状态 Begin/End / 多轨道并行 / 分层蒙版覆盖 / 层添加移除权重 / 事件日志；测试总数 282→288）

### 任务 E —— 动画蓝图编辑器面板（P1）✅
**目标**：提供 UE5.8 动画蓝图编辑器交互层（状态图/参数/预览）。
- [x] E1 `AnimGraphEditorPanel`（`Editor/Panels/AnimGraphEditorPanel.cs`）：状态图可视化（状态/转移/条件列表）、参数面板（Float/Bool/Int/Trigger 编辑）、运行时预览（BuildPreview→Animator+AnimatorController，SetFloat/SetBool/SetTrigger/TickPreview）
- [x] E2 注册到 `MainEditorWindow`（`AnimGraphEditor` 属性 + `_allPanels.Add`）
- [x] E3 测试：`AnimGraphEditorTests.cs`（6 用例全过：新建资产/构建预览/参数驱动状态/Trigger 转移/Tick 推进/面板注册；测试总数 288→294）

### 任务 F —— PoseAsset（P2）✅
**目标**：姿态资产 + 按权重混合节点（UE5.8 UPoseAsset / Blend Poses by weight）。
- [x] F1 `PoseAssetSystem.cs`：`PoseEntry`（命名姿态：每骨骼旋转/平移/缩放）+ `PoseAsset`（AddPose/SetBone/CapturePose/Evaluate/ApplyToPose）+ `BlendPosesNode`（多姿态按权重混合：旋转顺序 Slerp + 平移/缩放加权）
- [x] F2 测试：`PoseAssetTests.cs`（6 用例全过：姿态读写/捕获/写回/等权混合/单姿态全权重/旋转混合；测试总数 294→300）

---

## 3. 验收标准

每个任务闭环 = **代码可编译（0 错误）+ 新增测试全部通过 + 差距矩阵对应条目标记 ✅**。
最终目标：动画域从 UE4.7 R2 提升至 **UE5.8 深度级**，全部子能力可 CPU 端到端验证。

### 测试套件汇总（300 项全过）

| 任务 | 测试文件 | 用例数 | 覆盖点 | 状态 |
|---|---|---|---|---|
| A ControlRig | `ControlRigEvaluationTests.cs` | 7 | 层次全局/父约束/瞄准/IK/变换约束/空间往返/端到端写回骨骼 | ✅ |
| B FBIK+脚部 | `FullBodyIKTests.cs` | 6 | FBIK 链解算/多链层次/FootLock 锁定/解锁/FootPlacement 对齐/两骨 IK 几何 | ✅ |
| C BlendSpace3D | `BlendSpace3DTests.cs` | 6 | 样本点权重/正四面体质心/权重和恒 1/线段插值/凸包外回退/四面体化合法 | ✅ |
| D 蒙太奇通知+分层 | `MontageNotifyTests.cs` | 6 | 通知触发一次/状态 Begin-End/多轨道并行/分层蒙版覆盖/层增删权重/事件日志 | ✅ |
| E 动画蓝图编辑器 | `AnimGraphEditorTests.cs` | 6 | 新建资产/构建预览/参数驱动状态/Trigger 转移/Tick 推进/面板注册 | ✅ |
| F PoseAsset | `PoseAssetTests.cs` | 6 | 姿态读写/捕获/写回/等权混合/单姿态全权重/旋转混合 | ✅ |
| **合计** | **6 文件** | **37** | 本次动画域深度闭环 | ✅ |

> 会话起始动画相关测试 251 项 → 现 **300 项全过**（+49，含本次 A-F 闭环 37 项 + 前置动画域 12 项）。
