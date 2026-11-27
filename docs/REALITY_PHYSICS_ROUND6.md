# 真实物理内核 · 第六轮：连续碰撞子步与严格守恒（2026-11-25）

延续口径：**不新增语言、不动渲染管线、不宣称 GTA6 级**。
这一轮专攻前五轮清单里排第一的两件事：**高速穿透** 和 **位置投影破坏角动量守恒**。

## 1. 真 CCD：15 轴扫掠 SAT（替换包围球粗筛）

第五轮的 `ContinuousCollision.SweptSphereSphere` 用包围球报 TOI，但 OBB 的
包围球半径 `|halfExtents|` 远大于其真实投影半径，会**过早**报 TOI——
子步走到那一刻 SAT 还没命中，等于白拆一步。

`YrSweptObb.ComputeToi` 改用与窄相 `YrConvex.SatObbObb` **完全相同的 15 根轴**
（3 + 3 + 9 叉积）做 slab 区间求交：

```
对轴 u：p=(cb-ca)·u, w=(vb-va)·u, r=ra(u)+rb(u)
|w|≈0 → 要求 |p| < r，否则永不相交
否则 t1=(-r-p)/w, t2=(r-p)/w → tEnter=max(t1), tExit=min(t2)
若 tEnter ≤ tExit 且 tEnter ∈ [0,dt] → 首次接触时刻
```

轴集合一致 ⇒ TOI 时刻的 SAT 判定不再错配。
`EarliestToiAll` 刻意做 **O(n²) 全对扫掠而不依赖宽相**：宽相基于「当前」AABB，
而高速物体**当前恰好是分离的**，用宽相候选对会直接漏掉穿透——这是对照测试
`TestR6ConventionalStepMissesTunneling` 所证伪的那个错误前提。

## 2. TOI 子步回退 + 纯速度级求解

`YrToiAdvance.Advance` 把固定步 `dt` 按最早 TOI 拆成子区间：

```
while remaining > 0 && guard < maxSubsteps:
    toi = contactResolved ? -1 : 全对扫掠 TOI
    span = (toi in (0,remaining)) ? min(remaining, toi + ToiSlop) : remaining
    Integrate(span) → SyncObbs → SAP → 窄相 → Prepare/Solve(纯冲量) → joints.Solve
    if 本段有接触 → contactResolved = true   # 剩余时间一次走完，杜绝零步长空转
    无 CorrectPositions
```

- **受控进入量 `ToiSlop = 1e-3f`**：`tEnter` 时刻 SAT 的 overlap 恰为 0 会被判
  miss，所以终点取 `tEnter + 极小量` 让约束有正的穿透深度。这是 CCD 标准做法，
  **不是放宽容差**。
- **完全不调用 `CorrectPositions`**。第五轮已证明位置瞬移改变 `r×(mv)` 会破坏
  轨道角动量；这里改成在穿透**发生之前**用速度级冲量弹开，从根上不需要位置修正。
  `TestR6ToiStepConservesTotalAngularMomentum` 甚至把 `PositionCorrectionBeta`
  设成 0.9，TOI 路径依然严格守恒——因为那个开关在这条路径里根本不生效。

## 3. 接入方式：零回归

`YrPhysicsPipeline` 新增 `UseToiSubsteps`（**默认 false**）与 `ToiMaxSubsteps`。
`Step(realDt)` 按开关分派到新的 `StepFixedToi`，原 `StepFixed` **一行未改**——
前五轮 65 项断言语义完全不变。这不是保守，是让「新路径正确」与「旧路径不回归」
可以同时被证明。

## 4. 关键对照（不是自说自话）

| 用例 | 结果 |
|---|---|
| `TestR6ConventionalStepMissesTunneling` | A 从 -5 以 20/s 走 0.65s 越过 B 到 x=8，初始宽相分离 → 常规单步 **0 接触点**，确实漏检 |
| `TestR6ToiStepCapturesTunneling` | 同初态 TOI 路径：≥2 子步、≥1 次命中、≥1 接触点 |
| `TestR6ToiStepConservesTotalAngularMomentum` | 含轨道项 `r×(mv)` 的总角动量差 < 1e-3，且 `beta=0.9` 无效 |

## 5. 语言与构建边界（继续收窄）

- **不新增语言、不新增 Rust 导出**。扫掠 TOI、子步编排全是控制流密集，
  纯数值内核无收益。`sln` / `CMake` / `NuGet` / `YamaEngine.csproj` 一行未改
  （`Reality\**\*.cs` 通配自动纳入）。
- Rust 内核在 CI **仍不编译**（主工程不依赖、沙盒无 cargo）。
- **音频 MP3 看板仍 1 条**（解码器固有延迟 + 末帧 bit reservoir），本轮不碰。


## 诊断轮抓到的一个「测试场景写错」

CI `45829197450` Test 红，失败落盘回读到唯一失败：
`TestR6ToiStepConservesTotalAngularMomentum :: 本用例必须真的发生了接触`。

根因不是 TOI 实现，而是**测试初态物理上就不会碰撞**：两体在 y 方向错开 1.9，
又被给了相反的横向相对速度（-0.8/s），15 轴 slab 求交得到 `tEnter > tExit`，
正确地判定「擦肩而过」。CCD 没错，是断言要求了一个不存在的接触。

修法：把两体的横向 (y/z) 速度设为**相同**，相对速度纯沿 x 保证碰撞；
同时保留 y 位置错开 + 非零横向速度，使轨道项 `r×(mv)` 非零，守恒断言才有意义。
**没有删掉「必须发生接触」这条断言，也没有放宽容差**——改的是初态，让它真的撞上。

## 回归门禁

六轮累计 **79 项**真实物理断言（9 + 14 + 14 + 14 + 14 + 14），
全部进入现有 `build.yml` 的 Test step 真实执行：无 `[Skip]`、无吞异常、无阈值放宽。

## 不能宣称的（不变且不缩小）

- **不宣称 GTA6 / RAGE 级最真实**：专有实现未公开。
- 扫掠 TOI 是**线性匀速**外推（子步内速度视为常量），不做旋转扫掠、
  不做加速度/角速度的外推，因此高速自旋+薄板仍可能漏；
  序列冲量是迭代近似非全局 LCP 精确解；接触流形仍是顶点侵入近似、非面裁剪；
  PBF 非 N–S 完整解；XPBD 非有限元；断裂为工程判据；Tait 为弱可压缩近似；
  化学反应为集总宏观模型；材料参数仍是工程量级近似。
- `EarliestToiAll` 是 O(n²)，大场景需要 BVH / 动态 AABB 树加速，尚未做。
- **GPU 端到端、`0xC0000005`、百万粒子/刚体堆叠真机压测、多平台导出、
  Rust 原生内核编译**都未在沙盒验证。
- **渲染管线一律未动**。

能确认的仍是：**守恒可核算、角动量严格守恒、逐位确定、云端真实编译并执行**。

## 下一步（真机侧）

1. 把 O(n²) 扫掠换成动态 AABB 树 / BVH，做 1000+ 刚体压测。
2. 补旋转扫掠（角速度外推）与 Sutherland-Hodgman 面裁剪。
3. 编译并加载 Rust 原生库，跑三个 parity 测试的原生路径。
4. 专项攻 MP3 的 M4/M5，把音频看板清到 0。
