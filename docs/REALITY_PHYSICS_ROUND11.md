# 真实物理内核 · 第十一轮：完整旋转 CA 与 Jacobian 独立性缩减（2026-11-26 继续）

延续口径：**不新增语言、不动渲染管线、不改 sln/CMake/NuGet/csproj、不宣称行业 SOTA**。
音频看板第九轮已清零，第十轮 121 项断言全绿；本轮继续深化**算法层可判定差距**。

## 1. 完整旋转 Conservative Advancement（清掉第十轮「线性化 CA」的局限）

第十轮 `YrConservativeAdvancement` 的姿态轴永远取 t=0 的 15 根轴，旋转以
`spin·t` **从 0 累积**的线性上界并入投影半径。保证不漏报，但把整段历史的
旋转误差压在 t 上，帧越长越保守。

`YrFullRotationalCa` 换成行业 CCD 的标准形态：

```
① q(t) = exp(ω·t/2) ⊗ q0    （RQuat.FromAxisAngle 左乘，世界系角速度）
   中心 = c0 + v·t
② 用积分姿态**重枚举 15 轴**，算该时刻精确 SAT 分离 dist(t)
   —— 不再加 spin·t，因为姿态已真实推进
③ motion = |v_rel| + |ω_a|R_a + |ω_b|R_b （表面点相对接近速率上界）
④ Δt = (dist − ε)/motion，迭代至 dist ≤ ε 或 t > dt
```

**不漏报论证**：Δt 内间隙减小量 ≤ motion·Δt = dist−ε，不会越过接触。
相对第十轮，旋转不再按累积时间膨胀而是按真实姿态积分 → **更精确**，且仍不漏报。

云端回归断言：

- 零角速度 `|full − analytic| < 2e-3`、与第十轮线性化 CA 一致（parity）
- 6 组带角速度场景 `full ≥ linear − 1e-4`（完整版不早报 = 更精确）
- `IntegrateOrientation` 与 `RigidBody3D` 4000 小步积分 `|q·q_ref| > 0.999`
- 真实固定步 1/60 的 24 组布局最大 34 次迭代、报接触的组全部收敛、
  零角速子集 2 次内收敛；逐位确定、静止分离/背向体返回 −1、全对与候选对一致

**已知代价（必须读）**：`motion` 含旋转的最坏点速度，带旋转时实际间隙下降
远慢于该上界，CA 呈几何级数收敛。真实固定步 1/60 实测最大 **34 次**迭代；
人为放大到 dt=1.2 + 大角速可达 **126 次**，故 `MaxIterations = 128`。
这是标准 CA 的性能代价，不是精度阈值放宽 —— 迭代上界是性能界，
`ε = 1e-4` 的接触容差与第十轮一致、未动。真机侧可用割线/根细化加速。

另外修掉第十轮停滞判定里的一个潜在漏报：`nextT <= t` 时旧逻辑会在
`dist > 4ε` 时返回 −1（把「float32 推不动」误判成「本帧不接触」）。
这一轮改为返回最后一次确认分离的时刻并按 `dist` 标注 `Converged`，
`Residual` 原样保留可观测 —— **绝不因数值停滞而漏报**。

**诚实边界**：仍是「线性化步长 + 精确瞬时姿态」的 CA，不是连续时间上求精确
旋转扫掠根；ω 取常量外推（自由刚体在各向异性惯量下会进动，与 `RigidBody3D`
的 L 状态量积分不同），长时间大角速会有姿态漂移。用 L 版 CA 替换留真机侧。

## 2. 多面接触流形：每个参考面各自重选入射面

第十轮 `BuildMultiFace` 整对共用一次 `IncidentFace(b, 全局n)`。而 `IncidentFace`
是「取 `n·ax[k]` 最小者」的启发式 —— 对轴对齐场景会落到**侧面**而非朝向 A 的面。

新增 `IncidentFaceFor(b, faceNormal)`：取 `|faceNormal·ax[k]|` 最大的轴，
`sign = −sign(dot)`，给出真正与参考面法向反向的入射面。`BuildMultiFace` 改为
每个参考面各自调用，侵入深度沿各自 `rf.Normal` 测量；`BuildTagged` / `Build`
**一字未改**，单面路径零回归。

诚实边界：参考面候选仍按全局法向 `|dot| ≥ 0.4` 筛选，同轴的正反两面会同时入
候选（其中一面通常因深度为负被自然淘汰）；斜角多面接触仍是启发式。

## 3. 接触缩减：从几何覆盖 → 6D wrench Jacobian 线性独立性

第十轮 `Reduce` 是 farthest-point 几何覆盖，会丢掉「力臂方向不同但点很近」的
接触 —— 而这些点对约束求解最有用。新增 `ReduceByWrench`：

- 每点构造 6D wrench 行 `j = [n, (p−center)×n]`，归一化
- 严格 modified Gram-Schmidt：基里存**正交归一化后的残差方向**（不是原始
  wrench 行），先保留侵入最深点，之后每次选「在该正交基下残差最大」的点
  = 挑一组线性最独立的接触约束
- `WrenchIndependence` 把该指标暴露给测试；`BuildMultiFaceReduced` 改用
  **物理侵入深度** `ra − (p−ca)·n` 替代第十轮的「到中心距离」伪深度

`TestR11WrenchReductionDiffersFromGeometricOnCollinearSet` 用「4 个共线 + 1 个
正交」的点集给出可判定差异：几何版挑最远共线点 Id=3，wrench 版挑力臂正交点
Id=4，且 wrench 版独立性指标不低于几何版。

诚实边界：用单法向与单参考中心构造 6D 行，未含两体惯量与摩擦锥切向；
是 Jacobian 独立性的一阶近似，不是完整 LCP 选点。

## 4. 开关与零回归

`YrToiAdvance.UseFullRotationalCa`（默认 false）优先级高于
`UseExactCa` > `UseAngularInflation` > 线性扫掠；`YrPhysicsPipeline` 只做属性透传。
`BuildMultiFace` 的改进作用于第十轮那个默认关闭的多面开关之下，不触碰默认路径。

测试断言：新开关默认关、开启后常规 `StepFixed` 逐 tick 位级校验和相同、
CCD 子步路径开启后正常推进且 tick 增长。

## 5. 语言与构建边界（不变）

不新增语言、不新增 Rust 导出。`sln` / `CMake` / `NuGet` / `YamaEngine.csproj`
一行未改（`Reality\**\*.cs` 通配自动纳入）。Rust 内核在 CI **仍不编译**。
**渲染管线一律未动**。


## 诊断轮抓到的一个真 bug（CI `46005756839` 红灯 → 全绿）

失败落盘回读（`46006257966`）到 3 个精确失败，都是同一根因：
`TestR10MultiFaceIdsAreUniqueAndBounded` / `TestR10MultiFaceReducedIsBounded` /
`TestR11PerFaceIncidentPointsStayInsideOwnFace` —— **「多面 Id 冲突: 0」**。

根因：`YrFaceClipping.Lerp` 让裁剪交点**继承起点 `A.Id`**（为了让同一接触
特征跨帧保持标识）。但第十轮 `BuildMultiFace` 直接写
`fid*MaxPoints + cp.Id`，于是同一参考面内多个交点若继承自同一个 incident
顶点，就会拿到**同一个全局 Id**。第十轮的布局恰好没触发；这一轮把入射面
改成各自重选后，几何形态变化把它暴露出来了。这是第十轮埋下的真缺陷，
不是本轮改动的回归。

**修法**：每个参考面维护 `used[4]` 槽位表 —— 交点优先保留原顶点 Id（跨帧
稳定性不丢），若该槽位已占用或越界，就分配面内最小空闲槽位。结果：
面内 Id 唯一、`fid*4 + local ∈ [0,23]` 段不重叠、输出按 Id 升序。
没有删断言、没有放宽容差；诊断通道用完即删。

## 回归门禁

十一轮累计 **136 项**真实物理断言（9 + 14×8 + 15）+ 5 个音频参考一致性用例，
全部进入 `build.yml` 的 Test step 真实执行：无 Skip、无吞异常、无阈值放宽。

## 不能宣称的（不变且不缩小）

- **不宣称行业 SOTA / GTA6 / RAGE / Chaos 级最真实**：`docs/SOTA_GAP_ANALYSIS.md`
  的 G4–G8（真机 GPU 帧、规模压测、多平台导出、生态、外部验证）本环境物理上
  不可达，本轮没有缩小它们。
- 完整旋转 CA 仍是常量 ω 外推的 CA；多面参考面候选仍按全局法向；
  wrench 缩减是一阶近似；序列冲量非全局 LCP 精确解；PBF 非 N–S 完整解；
  XPBD 非有限元；断裂为工程判据；Tait 弱可压缩近似；材料参数为工程量级近似。
- GPU 端到端、`0xC0000005`、百万粒子真机压测、多平台导出、
  Rust 原生内核编译都未在沙盒验证。

> **数字更正**：第十一轮提交信息里写的「135 项（9+14×9）」是笔误 ——
> `RealityPhysicsEleventhTests.cs` 实际含 **15** 个 `Test` 方法，前十轮 121 项，
> 合计 **136** 项。此处与 `docs/ROUND11_DELIVERY.md` 一并更正，不改历史 commit message。

## 下一步

1. 用 `RigidBody3D` 的角动量 L（而非常量 ω）做 CA 姿态积分，消除各向异性进动漂移。
2. 多面参考面候选改为「每轴只取最对齐的一侧」，并按实际穿透深度筛面。
3. wrench 行加入两体惯量与摩擦锥切向，逼近完整接触 Jacobian。
4. 动态树接 GPU 帧循环压测；编译加载 Rust 原生内核跑 parity；扩 MP3 夹具。
