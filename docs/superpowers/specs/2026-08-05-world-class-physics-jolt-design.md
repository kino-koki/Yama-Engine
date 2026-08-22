# 世界级物理接入设计 — Jolt + 自研创新层

> 日期：2026-08-05
> 目标：将山雨引擎物理拉到世界顶级水平（对标 Jolt/PhysX/Chaos），并在接入基础上做自研创新解决社区争议。

## 1. 背景与现状

- `JoltPhysicsBridge.cs`（1092 行 P/Invoke 绑定）已存在，但 native `ShanyuJolt.dll` **缺失**（无 C++ 源码），桥接是死代码。
- 管理端 `PhysicsWorld` 类被 RigidbodyComponent / RaycastSystem / ForceFieldSystem / CharacterControllerReal 等多处引用但**不存在**，构建断链。
- vcpkg 含 `joltphysics` port；`ShanyuEngine.Native`（CMake, C++20, AVX2）是现成 native 宿主。
- 自研 C# 物理（PhysicsWorldV2 / RealPhysicsEngine / GJK+EPA+SAT）保留为参考/回退。

## 2. 架构

```
C# managed (ShanyuEngine.Physics)        Native (ShanyuJolt.dll, 新写)
┌────────────────────────────┐          ┌──────────────────────────┐
│ PhysicsWorld (缺, 写)      │─P/Invoke─│ SJ_* C ABI 实现          │
│  ├ AdaptiveSubstepper      │          │  ├ SmartEdgeFilter       │
│  ├ OriginShiftCoordinator  │          │  │  (ContactListener)    │
│  └ StackStabilizer         │          │  └ EdgeAdjacency 构建    │
└──────────┬─────────────────┘          └────────────┬─────────────┘
           │                                         │ links
   JoltPhysicsBridge.cs (已有)                       ▼
                                        JoltPhysics (vcpkg joltphysics)
```

## 3. 第一层：接入 Jolt（基础）

### 3.1 Native ShanyuJolt.dll
实现 `JoltPhysicsBridge.cs` 声明的全部 `SJ_*` C ABI：
- 库生命周期：`SJ_Init / SJ_Shutdown / SJ_GetVersion`
- PhysicsSystem：Create/Destroy/Step/SetGravity/GetGravity/GetActiveBodyCount/GetBodyCount/SetContactListener
- Shape：Box/Sphere/Capsule/Cylinder/Plane/Mesh/Compound 创建 + Finalize + Destroy
- Body：CreateAndAdd/RemoveAndDestroy/IsValid/SetPosition/GetPosition/GetRotation/Set+GetLinearVelocity/Set+GetAngularVelocity/AddForce/AddImpulse/AddImpulseAtPosition/AddTorque/Set+GetRestitution/Set+GetFriction/Set+GetMotionType/SetMotionQuality/Activate/Deactivate/IsActive/SetGravityFactor/SetIsSensor/SetObjectLayer/GetWorldBounds/Get+SetUserData
- Query：Raycast/RaycastMulti/OverlapSphere/OverlapBox/OverlapCapsule/OverlapShape/CollideShape
- Constraint：CreateFixed/Point/Hinge/Slider/Distance + Activate/Destroy + Hinge/Slider Limits + SetFrequency/Damping/MotorState/TargetVelocity/MaxMotorForce
- Character：Create/Destroy/Update/SetPosition/Get+SetPosition/Get+SetRotation/Get+SetLinearVelocity/GetGroundState/GetGroundNormal/GetGroundPosition/GetGroundBodyId/SetEnhancedInternalEdgeRemoval
- Debug：GetBodyStats

### 3.2 Managed PhysicsWorld
封装 `PhysicsSystemHandle`，暴露 RigidbodyComponent 等消费者所需的成员：
- `NativeHandle` 属性
- body 操作：Get/Set LinearVelocity/AngularVelocity/Position/Rotation/GravityFactor/Restitution/Friction/MotionType；IsBodyActive/ActivateBody
- shape 创建：Box/Sphere/Capsule/Mesh 等
- body 创建/销毁
- 约束创建
- 查询：Raycast 等
- 事件：OnCollisionEnter/Exit（供 CollisionEventSystem 挂接）
- Step(float dt, int collisionSteps)

## 4. 第二层：四项自研创新

### 4.1 SmartEdgeFilter（C++，解决三角网格内部边鬼影碰撞）
- 痛点：Jolt `EnhancedInternalEdgeRemoval` 仅开/关；地板三角面化后物体"磕边"。
- 机制：
  1. `SJ_Shape_Mesh_Create` 时构建平行边邻接表（Jolt MeshShape 不暴露邻接）。
  2. ContactListener 收到接触后，按 SubShapeId 定位三角面，查共享边的邻接三角面。
  3. 算两三角面法线二面角；若 < 阈值（默认 5°，可按 body 调）视为近共面边。
  4. 压掉接触法线的边分量，只保留共面法线。
  5. 每 body-pair 维护小法线历史环防瞬时尖刺。
- 比 Jolt 二值开关更细粒度。

### 4.2 AdaptiveSubstepper（C#，解决快速小物体穿透）
- 痛点：`collisionSteps` 手填，填错 tunnel。
- 机制：`StepAdaptive(world, dt)` 步进前扫描活动体 vMax 与最小 collider 半径 rMin；`n = ceil(vMax·dt / rMin)` clamp [1,16]；带滞后防帧间震荡。

### 4.3 OriginShiftCoordinator（C#，解决大世界浮点精度）
- 痛点：Jolt 内部 float32，世界坐标 >~10km 抖。
- 机制：跟踪流动原点；焦点偏离当前原点超阈值（2km）时整体重基址——批量 `SJ_Body_SetPosition` 减 delta，速度不变。对玩法代码透明。

### 4.4 StackStabilizer（C#，解决堆叠微震）
- 痛点：迭代求解器 resting stack 微震。
- 机制：Step 后扫描"近静止接触岛"（两侧 |vel| < 阈值 + 法向力 > 阈值 + 持续 N 帧）；对这些施加速度阻尼（×0.5）+ 沿接触法线小幅位置投影。"持续 N 帧"门限防与活跃运动对抗。

## 5. 文件清单

### Native（新建）
- `ShanyuEngine.Native/include/shanyu/jolt/ShanyuJolt.h` — C ABI 头
- `ShanyuEngine.Native/src/jolt/ShanyuJolt.cpp` — SJ_* 全实现
- `ShanyuEngine.Native/src/jolt/EdgeAdjacency.{h,cpp}` — 网格边邻接
- `ShanyuEngine.Native/src/jolt/SmartEdgeFilter.{h,cpp}` — ContactListener
- 改 `ShanyuEngine.Native/CMakeLists.txt` — 链接 vcpkg joltphysics，输出 ShanyuJolt.dll

### Managed（新建）
- `ShanyuEngine/Physics/PhysicsWorld.cs` — 管理封装
- `ShanyuEngine/Physics/Innovations/AdaptiveSubstepper.cs`
- `ShanyuEngine/Physics/Innovations/OriginShiftCoordinator.cs`
- `ShanyuEngine/Physics/Innovations/StackStabilizer.cs`
- `ShanyuEngine/Physics/Innovations/SmartEdgeFilterSettings.cs`

### 测试与构建
- `ShanyuEngine.Tests/` 加物理烟测
- `scripts/build-jolt-native.ps1` — vcpkg + cmake 构建脚本

## 6. 验证（M2，用户本机）
1. vcpkg install joltphysics
2. cmake 构建 ShanyuEngine.Native → ShanyuJolt.dll
3. dotnet build ShanyuEditor.sln
4. 跑 5 箱子堆叠 demo + 边过滤 demo + 射线 demo

## 7. 诚实约束
本会话产出全部代码，**native 构建验证（vcpkg/cmake/MSVC）需用户在本机 PowerShell 跑**。代码不留桩，但"能编译运行"由用户验证后定论。

## 8. 不做（YAGNI）
- 不修改 Jolt 源码（全部创新在 wrapper/managed 层）
- 不重写自研 C# 物理（保留为参考）
- 不做 soft body / cloth 扩展（Jolt 已有基础，留后续）
- 不做跨平台 native 构建（先 Windows x64）
