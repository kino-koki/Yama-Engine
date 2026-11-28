# 真实化学内核 · 第七轮：分布式反应–扩散燃烧（2026-11-26）

延续口径：**不新增语言、不动渲染管线、不宣称 GTA6 级**。
这一轮专攻化学体系里排第一的那件事：**反应是集总的（一个燃料池），没有空间**。
热场是分布的（n 格点），化学却是「全局一个池子」，两者不对称——火焰无法「传播」，
只能「瞬间全域点燃」。第七轮把化学升级为逐格点的反应–扩散场。

## 1. 从集总到分布：组分场逐格点存储

第六轮及之前，`YrCombustionScenario` 用 `YrReactionNetwork` 持有**一对**浓度
（Fuel / Ash），全场共享——这是文档里明确写出的显式近似。第七轮新增
`YrDistributedCombustion`，把组分升级为**逐格点场**：

```
_conc[species][cell]   # 4 物种 × n 格点：CH4 / O2 / CO2 / H2O
_temp[cell]            # 共享温度场（驱动反应、被反应加热、自身扩散）
```

反应模型（甲烷类，元素 [C, H, O]）：`CH4 + 2 O2 -> CO2 + 2 H2O`。
构造时即**断言配平**：对 C/H/O 各元素，Σ Stoich[s]·Elements[s][e] = 0
（C: -1+1=0，H: -4+4=0，O: -4+2+2=0）。`TestR7OverallReactionIsBalanced` 独立复核。

## 2. 反应–扩散：让火焰「传」而不是「闪」

每帧 `Step(dt)` 分两段：

```
1) 扩散：组分场与温度场各自做最近邻显式扩散，按 CFL 拆子步
   stable = dx^2 / (2·max(D_species, D_thermal))
   每子步系数 coef = D·h/dx^2 ≤ 0.5   # 子步保证，杜绝负浓度/数值爆炸
2) 反应（逐格点）：燃点门控 + Arrhenius + 限流反应物 + 有限火焰速度
   if T[i] < T_ign: skip                       # 燃点门控
   kf = A·exp(-Ea/(R·T))
   extent = min(vf·dt, min(fuel, ox/2), maxBurnFraction·fuel)
   按计量数转移组分（extent 受限 ⇒ 不产生负浓度）
   q = extent·ΔH  →  T[i] += q/cp  →  ledger.BoundaryWork(q)
```

- **燃点门控**（硬阈值阶跃）：未达燃点的格点即便有燃料+氧化剂也不反应。
  这是火焰传播的必要条件——否则全域同时反应，无所谓「锋面」。
  `TestR7NoIgnitionNoReactionBelowIgnitionPoint` 验证：燃点设 1e6，燃料不被消耗、不放热。
- **组分扩散**：让燃料/氧化剂/产物在空间上搬运。`TestR7ElementInventoryConservedUnderBurn`
  证明扩散是**守恒**的（最近邻 + 无通量边界 + 系数 ≤ 0.5 ⇒ 元素总量严格不变）。
- **点火源 `Ignite(cell, T)`**：理想化的火花塞/长明灯，把某格点温度抬到给定值。
  没有它，全域等于环境温度、无格点达燃点，反应永不启动。文档不隐藏这是外部边界条件理想化。

## 3. 限流反应物（当量比）与有限火焰速度

真实燃烧的反应进度受**稀缺反应物**约束，不是「有多少燃料烧多少」：

```
extentMax = min(fuel, oxidizer / 2)        # 化学计量：1 燃料配 2 氧化剂
extentMax = min(extentMax, maxBurnFraction · fuel)   # 有限火焰速度
```

- **缺氧 → 不完全燃烧**：氧化剂耗尽而燃料剩余。`TestR7OxidizerStarvedIncompleteCombustion`
  设 ox=0.5（仅够烧 0.25 燃料），终态氧化剂归零、燃料大量剩余——不会凭空多放热。
- **氧化剂过量 → 完全燃烧**：`TestR7CompleteCombustionWithExcessOxidizer` 设 ox=3，
  终态燃料趋尽、CO2 趋满、H2O ≈ 2·CO2（计量比），账本平衡。
- **有限火焰速度 `maxBurnFraction`**：每帧最多烧掉本地燃料的这一比例。
  没有它，一个格点的燃料会在一帧内燃尽、能量以尖峰释放，随后热扩散把峰值稀释到
  燃点以下——**火焰猝熄**而非传播。这是把「爆燃有有限传播速度」这一物理事实显式建模。

## 4. 火焰传播：从一点到全域是「逐渐」的

`TestR7FlameFrontPropagatesFromIgnitionSource`：只点燃 0 号格点，跑 120 帧。

- **非瞬时**：第 1 帧后 `IgnitedCount() ≤ 3`（不是全域同时点燃）。
- **确实传播**：总燃耗 `> 2.0`，而点火格自身存量只有 1.0——多烧的必然来自邻近格点，
  证明锋面越过了点火格。同时 `IgnitedCount() > 1`、账本平衡。
- **逐位确定**：`TestR7DeterministicReplay` 两次运行快照逐位相同（`TestR7*` 系列全程无随机、无并行）。

## 5. 语言与构建边界（不变）

- **不新增语言、不新增 Rust 导出**。`sln` / `CMake` / `NuGet` / `YamaEngine.csproj`
  一行未改（`Reality\**\*.cs` 通配自动纳入新文件）。
- 全部纯 C# 数值内核，无 GPU、无原生依赖。
- **音频 MP3 看板仍 1 条**，本轮不碰。渲染管线一律未动。

## 回归门禁

第七轮新增 **9 项**化学断言，由 `XunitAdapter` 反射自动发现（`public static void Test*`），
进入现有 `build.yml` 的 Test step 真实执行：无 `[Skip]`、无吞异常、无阈值放宽。

| 用例 | 断言 |
|---|---|
| `TestR7OverallReactionIsBalanced` | CH4+2O2→CO2+2H2O 对 C/H/O 净计量和为 0 |
| `TestR7ElementInventoryConservedUnderBurn` | 充分燃烧前后 C/H/O 总量不变 |
| `TestR7CompleteCombustionWithExcessOxidizer` | 氧化剂过量 → 燃料趋尽、H2O≈2·CO2、账本平衡 |
| `TestR7OxidizerStarvedIncompleteCombustion` | 缺氧 → 氧化剂耗尽、燃料剩余 |
| `TestR7NoFuelNoHeatReleased` | 无燃料 → 零放热 |
| `TestR7NoOxidizerNoHeatReleased` | 无氧化剂 → 零放热 |
| `TestR7FlameFrontPropagatesFromIgnitionSource` | 非瞬时 + 锋面越过点火格 |
| `TestR7DeterministicReplay` | 两次运行快照逐位相同 |
| `TestR7NoIgnitionNoReactionBelowIgnitionPoint` | 未达燃点不反应 |

## 不能宣称的（不变且不缩小）

- **不宣称 GTA6 / RAGE 级最真实**：专有实现未公开。
- 化学从「集总」升级为「一维分布」，但**仍是一维格点链**、最近邻显式扩散；
  真实三维火焰需要多维网格 + 更高阶格式。
- **单步总包反应**，不含自由基链式分支（真实烃燃烧是 GRI-Mech 等数十至上百步机理）。
- 燃点门控是**硬阈值阶跃**，非平滑 Sigmoid；点火源是理想化边界条件。
- 浓度为**集总摩尔量**，非严格摩尔体积分数；Arrhenius 参数是工程量级近似，非实验标定。
- 无辐射换热、无对流边界、无多相（液滴/颗粒）燃烧、无湍流-化学相互作用。
- **渲染管线一律未动**；本模块不含 GPU 端到端。

能确认的仍是：**反应配平、元素守恒可核算、限流反应物正确、火焰逐渐传播、逐位确定**。

## 下一步（化学侧）

1. 一维 → 三维网格，接真实流体速度场（`ThermoFluidCoupler` 的半拉格朗日对流输运组分）。
2. 总包反应 → 多步简化机理（至少加入 CO 中间体与 CO→CO2 延迟氧化）。
3. 把 `YrCombustionScenario` 的集总燃料池替换/桥接到分布式版本，消除双模型并存。
4. Arrhenius 参数对标实验（层流火焰速度、绝热火焰温度），而非工程量级估算。
