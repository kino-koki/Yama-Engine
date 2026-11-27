# 真实物理内核 · 第十二轮：角动量 CA / 参考面候选修正 / 18D 接触 Jacobian（2026-11-26 继续）

延续口径：**不新增语言、不动渲染管线、不改 sln/CMake/NuGet/csproj、不宣称行业 SOTA**。
音频看板 0 条；上一轮 136 项断言已全绿。本轮继续深化**算法层可判定差距**。

> **顺带更正笔误**：第十一轮提交信息写「十一轮累计 135 项（9+14×9）」，
> 实际 `RealityPhysicsEleventhTests.cs` 含 **15** 个 `Test` 方法，前十轮 121 项，
> 合计 **136 = 9 + 14×8 + 15**。`docs/REALITY_PHYSICS_ROUND11.md` 与
> `docs/ROUND11_DELIVERY.md` 已一并更正，不重写历史 commit message。

## 1. 角动量版 CA（清掉第十一轮「常量 ω 外推」的局限）

自由刚体在各向异性惯量下，即使无力矩、**世界系角动量 L 严格守恒**，
角速度 ω 也会随姿态进动 —— `RigidBody3D` 把 L 而非 ω 当状态量正因如此。
第十一轮 `YrFullRotationalCa` 用常量 ω 积分 `q(t)=exp(ωt/2)⊗q0`，
长时间大角速外推会姿态漂移，从而误判接触时刻。

`YrAngularMomentumCa` 把 L 作为守恒状态量：

```
ω(q) = R(q) · diag(invI_local) · Rᵀ(q) · L
dq/dt = ½ (ω,0) ⊗ q       （与 RigidBody3D.Integrate 同一半隐式口径）
姿态增量积分到 t → 重枚举 15 轴 → 精确 SAT 分离 dist(t)
motion = |v_rel| + maxInv·|L_a|·R_a + maxInv·|L_b|·R_b
```

CA 的 t 单调递增，积分器用**增量缓存**（只从上次 t 推进到新 t，不重复积分
历史），`Substeps` 可观测。运动上界用 `max(invI)·|L| ≥ |ω|` 保证不漏报；
float32 停滞同样不返回 −1。

云端回归断言：

- 各向异性惯量下 `OrientationAt` 与 `RigidBody3D` 4000 小步积分 `|q·q_ref| > 0.999`
- ω 随姿态进动（`|ω(T)−ω(0)| > 1e-3`），常量 ω 外推姿态明显偏离
- 零角动量与第十一轮完整旋转 CA parity；6 组带 L 场景不漏报；
  逐位确定、静止分离/背向体返回 −1、全对与候选对一致

**诚实边界**：姿态是**一阶半隐式数值积分**（子步 ≤ 2.5e-4），不是自由刚体
解析解（Jacobi 椭圆函数），有截断误差；L 不修改（严格守恒）、ω 随姿态更新。
接触判定仍是瞬时姿态的 15 轴 SAT，不是连续时间旋转扫掠根的精确解。

## 2. 多面参考面候选修正（清掉第十轮留下的启发式）

第十/十一轮 `BuildMultiFace` 对 A 的 6 个面按 `|n·faceNormal| ≥ 0.4` 筛选，
于是**同轴的正反两面会同时入候选**，背面也被裁一遍。第十二轮改成：

- 每轴**只取与 n 同向的那一侧**（`hit.Normal` 从 A 指向 B，A 上朝向 B 的
  接触面法向应与 n 同向）→ 每轴至多 1 面，最多 3 面
- 粗筛：`B 中心沿该面法向到参考面的分离 > B 在该方向的投影半径` 时剔除该面
- 保底：粗筛把面全剔时取 `|dot|` 最大轴的同向面，**不回退 `BuildTagged`**
  —— 后者的 Id 段 `0..3` 与多面 `(faceId,pointId)` 编码冲突，会让「点落在
  自己 Id 对应的参考面内」的断言失效

`TestR12FaceCandidatesNeverIncludeOppositeSide` 断言同一轴的正反面不同时出现、
最多 3 个轴；`TestR12FaceCandidatesStillInsideOwnReferenceFace` 在旋转 A 下
仍验 Id 唯一 + 点在面内 + 缩有界。

## 3. 18D 接触 Jacobian 缩减（含摩擦切向）

`ReduceByWrench` 只构造法向 6D 行 `[n, r×n]`，丢掉两个摩擦切向的力臂信息。
`ReduceByContactJacobian` 每点构造 **18D 行**：

```
[ n, r×n, t1, r×t1, t2, r×t2 ]   （t1,t2 为法向的确定性正交基）
```

做法向 + 两切向各自的 6D wrench，严格 modified Gram-Schmidt（基里存正交
归一化残差方向）挑最独立的点。`linearWeight / angularWeight` 允许按两体
有效质量给线性段与角段加权。`BuildMultiFaceReduced` 改用它。

`TestR12ContactJacobianReductionDiffersFromWrench` 用的点集先在本地用
float64 复刻 MGS 数值预演过：6D wrench 选 `[0,1,4]`、18D 选 `[0,3,4]`，
差异可判定且 18D 必含带切向力臂的 Id=4。`TestR12ContactJacobianWeightsChangeSelection`
同点集下线性重选 `[0,3,4]`、角段重选 `[0,1,4]`，断言权重确实改变选点。
（第一版点集本地预演发现两种缩减选点相同、权重也不改变选点 —— 已换成
真正有区分度的点集，不是放宽断言。）

**诚实边界**：权重是**外部标量**，不是从两体惯量张量算出的完整有效质量矩阵；
仍未含摩擦锥的非线性边界与切向耦合；是接触 Jacobian 独立性的一阶近似。

## 4. 开关与零回归

`YrToiAdvance.UseAngularMomentumCa`（默认 false）优先级高于
`UseFullRotationalCa` > `UseExactCa` > `UseAngularInflation` > 线性扫掠；
`YrPhysicsPipeline` 只做属性透传。面候选与缩减改进作用于第十轮默认关闭的
`UseMultiFace` 之下。断言：新开关默认关、常规 `StepFixed` 逐 tick 位级一致、
CCD 子步路径开启后正常推进。

## 5. 语言与构建边界（不变）

不新增语言、不新增 Rust 导出。`sln` / `CMake` / `NuGet` / `YamaEngine.csproj`
一行未改（`Reality\**\*.cs` 通配自动纳入）。Rust 内核在 CI **仍不编译**。
**渲染管线一律未动**。

## 回归门禁

十二轮累计 **150 项**真实物理断言（9 + 14×9 + 15）+ 5 个音频参考一致性用例，
全部进入 `build.yml` 的 Test step 真实执行：无 Skip、无吞异常、无阈值放宽。

## 不能宣称的（不变且不缩小）

- **不宣称行业 SOTA / GTA6 / RAGE / Chaos 级最真实**：`docs/SOTA_GAP_ANALYSIS.md`
  的 G4–G8（真机 GPU 帧、规模压测、多平台导出、生态、外部验证）本环境物理上
  不可达，本轮没有缩小它们。
- L 版 CA 是一阶数值姿态积分、非解析解；参考面粗筛是几何启发式；
  18D 缩减是含摩擦的一阶近似；序列冲量非全局 LCP 精确解；PBF 非 N–S 完整解；
  XPBD 非有限元；断裂为工程判据；Tait 弱可压缩近似；材料参数为工程量级近似。
- GPU 端到端、`0xC0000005`、百万粒子真机压测、多平台导出、
  Rust 原生内核编译都未在沙盒验证。

## 下一步

1. CA 用割线/根细化把带旋转时的几何级数收敛加速（当前真实固定步最大 34 次迭代）。
2. 接触 Jacobian 权重改从两体惯量张量算有效质量矩阵；引入摩擦锥非线性边界。
3. 动态树接 GPU 帧循环压测；编译加载 Rust 原生内核跑 parity；扩 MP3 夹具。
