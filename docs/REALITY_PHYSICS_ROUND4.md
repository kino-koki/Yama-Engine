# 真实物理内核 · 第四轮深化（2026-11-25 继续）

延续口径：**不新增语言、不动渲染管线、不宣称 GTA6 级**。
这一轮的重点不是再堆一个孤立算法，而是把前三轮那些**各自正确但没接起来**
的能力接成链，并补上「可回滚的确定性」这个工程闭环。

## 1. 完整窄相链终于接通

前三轮已有 `YrConvex`（SAT 法向/深度）、`ContactFriction`（摩擦锥）、
`ContinuousCollision`（TOI），但彼此不相连——SAT 算出命中后没有求解器消费它。
本轮 `YrContactSolver` 用**序列冲量（Sequential Impulse）**把它们串起来：

```
SAT 命中 (法向, 深度, 接触点)
   → Prepare：记录初始法向速度、设恢复系数目标、施加 warm start 冲量
     （跨帧缓存 λ；单接触一次迭代必精确，测试只验「冲量被施加 + λ 不被重置」，
      不编造「减少迭代次数」这种在线性约束下不成立的结论）
   → Solve：法向冲量（λ_n ≥ 0）+ 二维库仑圆锥切向冲量，多次迭代
   → CorrectPositions：纯位置投影消除穿透（不改速度，不注入动能）
```

三个容易被写错、本轮特意处理对的地方：

1. **角冲量就是 `L += r × j`**。因为 `RigidBody3D` 以世界系角动量 L 为状态量，
   不需要再乘 I⁻¹；这比「把 ω 当状态量再换算」结构上更不容易出错。
2. **恢复系数只作用一次**。`TargetNormalVelocity` 由初始 `vn0` 决定并固定，
   后续迭代目标为 0，避免每轮迭代重复注入弹性能量。
3. **位置修正与速度求解分离**。Baumgarte 偏置会把人为能量灌进系统，
   这里改用独立的位置投影，`TestR4PositionCorrectionDoesNotInjectEnergy`
   直接断言动能前后不变。

还有一处**守恒断言本身的纠错**：只看 `L_A + L_B` 并不守恒，因为内力作用在
不同刚体质心上，必须把轨道项 `r × (m v)` 一起算进总角动量。
`TotalAngularMomentum` 给出的才是关于世界原点的守恒量，
`TestR4ContactConservesTotalMomenta` 同时验总线动量与总角动量。

## 2. 双刚体关节（约束图不再只有质点）

`YrJointSolver` 与接触求解器共用「成对冲量 + 广义有效质量」框架：

| 类型 | 锁定 | 保留 |
|---|---|---|
| `Ball` | 三向相对平移 | 任意相对旋转 |
| `Slider` | 垂直轴的两向平移 | 沿轴滑动 |
| `Hinge` | 三向平移 + 垂直轴的两向相对角速度 | 绕轴自转 |

局部锚点经四元数旋转到世界系，随姿态正确移动；位置投影同样不改变速度。

## 3. 四面体体积约束

距离约束只能保边长，整体仍可能压扁到零体积。`YrVolumeConstraint` 用
signed volume 的解析梯度做 XPBD 修正：

```
V = (p1-p0)·((p2-p0)×(p3-p0))/6
∇V(p1)=((p2-p0)×(p3-p0))/6, ∇V(p0) = -Σ∇V(其余)  →  Σ∇V = 0
```

`Σ∇V = 0` 是平移不变性的解析保证：约束自身不产生净外力。
测试既验证梯度逐项相加为零，也验证「整体放大 2 倍（体积 ×8）后能收敛回 V0」。

## 4. 燃烧端到端场景（第一次把化学/热/结构/断裂跑成一个场景）

`YrCombustionScenario`：化学反应 → 反应热进账本 → 按初始燃料权重分布到温度场
→ 热扩散/相变/热应力 → 强度退化 → 疲劳累积 → 断裂释放能进账本。

**单一责任划分，避免双重记账**：反应网络独占化学，`ThermoStructuralCoupler`
传入 `fuel` 全 0（内建燃烧分支不触发）只管输运与结构响应，场景把
「账本里已登记的反应热」做空间分布但**不再二次记账**。
测试同时断言：燃烧推进、峰值温度上升、**账本平衡**、无燃料时温度不自发上升
（杜绝隐式产生能量）、两次同初态快照逐位一致。

> 诚实边界：化学是**集总模型**（一个燃料池），热场是**分布模型**（n 格点）。
> 这是显式近似，不是 CFD 级的反应流。

## 5. 确定性从断言升级为能力

`YrRollbackBuffer`：每帧存 state 副本 + **FNV-1a 位级校验和**
（对 float 取 IEEE 位模式，能区分 `-0` 与 `+0`、捕捉 NaN），
`Rewind(tick)` 丢弃未来帧，`VerifyDeterministic` 逐 tick 比对。
配合第三轮的 `YrFixedClock`，这就是客户端预测 / 回滚重模拟的物理底座。

## 6. 多语言：这轮刻意不扩 Rust 接触面

本轮新增能力全是**控制流密集**（SAT 后处理、迭代冲量求解、约束图、场景编排），
搬进纯数值内核没有收益反而增加 ABI 面。所以：

- **不新增第四个 Rust 导出**，三个导出仍是 `yr_density_pass` /
  `yr_density_pass_csr` / `yr_covariance_accum`；
- 改为给 Rust crate 补 **3 个 `#[cfg(test)]` 自测**，真机 `cargo test`
  可独立验证三个 C ABI 导出与 C# `Scalar*` 同构实现逐位一致；
- 不用静态平台调用特性；`sln` / `CMake` / `NuGet` / `YamaEngine.csproj`
  **本轮一行未改**（`Reality\**\*.cs` 通配自动纳入新源码）。

> Rust 内核在 CI **仍不编译**（主工程不依赖、沙盒无 cargo）。这是已知边界，
> 不是「已验证」。真机启用前请跑 `cargo test --manifest-path ...` 与
> `cargo build --release --manifest-path ...`。


## 诊断轮抓到的两个「测试写错」而非「代码写错」

CI `45823438905` Test 红，失败落盘回读到 2 个精确失败，检查后都是**断言本身写错**：

| 失败 | 真相 | 修法 |
|---|---|---|
| `TestR4SliderKeepsAxialFreedom`：「滑块应压住横向速度: 1」 | 成对冲量消除的是**相对速度**。等质量两体各分一半冲量，B 的 x 速度从 2 变 1、A 从 0 变 1，两者同速，`vrel.X=0` 才是正确结果 | 断言改为相对速度的横向分量；轴向同理 |
| `TestR4HingeLocksTransverseAngularVelocity`：「应保留绕轴自转」 | 初始角动量给的是 `(1,0,0.5)`，**根本没有 Y 分量**，绕轴自转本来就是 0，断言无从成立 | 初始角动量改为 `(1,0.8,0.5)`，横向 x/z 交给铰链锁、轴向 Y 保留 |

代码没动。这两处如果靠「放宽阈值」也能变绿，但那会把错误物理固化下来——
所以选择改断言，并把「约束消相对速度、守恒量必须含轨道项」写进测试注释。

## 回归门禁

四轮累计 **51 项**真实物理断言（9 + 14 + 14 + 14），
全部进入现有 `build.yml` 的 Test step 真实执行：无 `[Skip]`、无吞异常、无阈值放宽。

## 不能宣称的（不变且不缩小）

- **不宣称 GTA6 / RAGE 级最真实**：专有实现未公开。
- PBF 非 N–S 完整解；XPBD 非有限元；断裂为工程判据；Tait 是弱可压缩近似；
  涡量约束是数值补偿；SAT 接触点为近似点、非完整接触流形；
  形状匹配是线性/刚性近似；序列冲量是迭代近似、非全局 LCP 精确解；
  化学反应是集总宏观动力学；材料参数仍是工程量级近似。
- **GPU 端到端、`0xC0000005`、百万粒子 / 刚体堆叠真机压测、多平台导出、
  Rust 原生内核编译**都未在沙盒验证。
- **渲染管线一律未动**。
- **音频看板仍 1 条 MP3**（解码器固有延迟 + 末帧 bit reservoir），
  已精确定义但本轮不碰，不降阈值、不藏用例。

能确认的仍是：**守恒可核算、角动量结构性守恒、逐位确定、云端真实编译并执行**。

## 验收

```bash
git fetch && git checkout feat/reality-physics-architecture
dotnet build YamaEngine/YamaEngine.csproj -c Debug -warnaserror
dotnet test YamaEngine.Tests/YamaEngine.Tests.csproj \
  --filter "FullyQualifiedName~RealityPhysics"
cargo test --manifest-path native/reality_physics/Cargo.toml
cargo build --release --manifest-path native/reality_physics/Cargo.toml
```

## 下一步（真机侧）

1. 把 SAT 命中数组直接喂给 `YrContactSolver`，形成 CCD→SAT→冲量→位置投影的全链。
2. 接 GPU 帧循环做 1000+ 粒子 + 刚体堆叠压测，量测约束岛休眠收益。
3. 编译并加载 Rust `.so/.dll/.dylib`，跑三个 parity 测试的原生路径。
4. 用 `YrRollbackBuffer` + `YrFixedClock` 做一次 60fps 回滚重模拟实测。
5. 专项攻 MP3 的 M4/M5，把看板清到 0。
