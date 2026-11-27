# 真实物理内核 · 第十轮：精确 CA 与多面接触流形（2026-11-26 继续）

延续口径：**不新增语言、不动渲染管线、不改 sln/CMake/NuGet/csproj、不宣称 GTA6 级**。
音频看板已在第九轮清零（3→0 条），本轮零改动。

## 1. 迭代式 Conservative Advancement（替换整帧角速度膨胀）

第七轮 `AngularInflation(|ω|,R,dt)=R·min(1,|ω|·dt·√2)` 把**整帧 dt** 的旋转上界
一次性加到 15 轴投影半径上 → 帧越长越早报、越保守。第十轮换用行业 CCD 做法：

```
① dist(t) = max over 15 axes ( |(d0+v_rel·t)·u| − r_a(u) − r_b(u) − spin·t )
   spin = |ω_a|·R_a + |ω_b|·R_b   （旋转的线性上界）
② motion = |v_rel| + spin
③ Δt = (dist − ε)/motion,  t += Δt,  回到 ①
④ dist ≤ ε → 返回 t；t > dt → -1
```

因为膨胀量用**局部 t** 而不是整帧 dt，CA 在未饱和区**更精确**；
对纯线性相对运动收敛到解析 TOI。

**更重要的一个真发现**：第七轮 `AngularInflation` 带
`min(1.0, |ω|·dt·√2)` 饱和，把角速度膨胀截断在 `R`。
对本夹具 `|ω|=12, dt=0.65`：真实旋转上界 `R·|ω|·dt ≈ 13.5`，
饱和后只有 `R ≈ 1.732`（低估约 7.8 倍）→ **高角速下会漏报**。
CA 的 `spin = |ω|·R` 无饱和、用完整 `spin·t` 上界，因此在该区间
**更早报**（更保守、不漏报），这不是退化而是修复。云端回归分两段断言：

- 零角速度 `|CA − ComputeToi| < 2e-3` 且 `CA ≤ analytic + 1e-3`（不漏报）
- 低角速（未饱和）`CA ≥ ComputeToiWithAngular − 1e-5`（不早报、更精确）
- 高角速（饱和）`CA ≤ ComputeToiWithAngular + 1e-5`（不漏报），并断言
  `R·|ω|·dt > 4·AngularInflation(...)` 作为饱和低估的数值证据
- 24 组布局最大迭代数 ≤ 12、结果逐位确定、静止/背向体返回 -1

**诚实边界**：这是**线性化 CA** —— 姿态轴仍取 t=0 的 15 根轴，旋转以
`spin·t` 的有界线性项并入投影半径，**不是**每步重算姿态 + 重枚举轴的完整旋转
CA。它保留「不漏报」保证，但仍是保守近似；轴集合不随 t 更新会略高估间隙。
完整 CA 需要四元数积分，留真机侧。

## 2. 多面接触流形（清掉第八轮留下的 Id 局限）

第八轮文档写：「逐点 Id 只在单对参考/入射面内稳定，多面同时接触时不同面
可能复用同一 Id」。本轮 `YrFaceClipping.BuildMultiFace`：

- 枚举 A 的 6 个面 `(axis, sign)`，按 `|n·faceNormal| ≥ 0.4` 筛选，取对齐度最高的至多 3 个
- 每个参考面独立做 Sutherland-Hodgman 裁剪，点的稳定 Id =
  `faceId*4 + localPointId`（0..23），**不同面的 Id 段不重叠**
- `BuildMultiFaceReduced` 调 `YrContactReduction` 把多面点集缩到 4 点
- 原 `BuildTagged` / `Build` 一字未改，单面语义逐点不变

`YrContactReduction` 用 farthest-point 贪心：先保留最深点，再反复选「到已选
集合最小距离最大」的点；所有比较带 Id tie-break，输出按 Id 排序 → 逐位确定。
诚实边界：这是**几何覆盖**启发式，不是按接触 Jacobian 线性独立性的最优 4 点，
也不考虑摩擦锥。

## 3. 三个新开关，全部默认 false（零回归）

| 开关 | 作用 | 默认 |
|---|---|---|
| `UseExactCa` | ToiAdvance 用迭代 CA 替代整帧膨胀/线性扫掠（优先于 `UseAngularSweep`） | false |
| `UseMultiFace` | 窄相用多面流形 + 缩减，点 Id 按 `(faceId,pointId)` 分段 | false |
| （已有）`UsePointWarmCache` | 按稳定 Id 缓存 λ_n | false |

管线只做透传；`StepFixed` / `StepFixedToi` 核心一行未改。
`TestR10PipelineNewSwitchesDefaultOffAndZeroRegression` 断言默认关闭、开启后
常规路径逐 tick 位级校验和相同。

## 4. 语言与构建边界（继续收窄）

不新增语言、不新增 Rust 导出。`sln` / `CMake` / `NuGet` / `YamaEngine.csproj`
一行未改（`Reality\**\*.cs` 通配自动纳入）。Rust 内核在 CI **仍不编译**
（主工程不依赖、沙盒无 cargo）。**渲染管线一律未动**。


## 诊断轮抓到的两个真缺陷（CI `45994887145` 红灯 → 全绿）

`45995303207` 失败落盘回读到 3 个精确失败，全部真修，未删断言、未放宽 ε：

1. **CA 的 float32 精度停滞**（`TestR10CaZeroSpinApproachesAnalyticToi` /
   `TestR10CaIterationCountIsBounded`）：零角速主场景第 2 次迭代
   `dist=0.0001001358`，只比 `ε=1e-4` 大 1.36e-7，算出
   `delta≈6.8e-9` **大于** `1e-9` 阈值；但 `t≈0.399995` 的 float32 ulp
   约 3e-8，`t+delta` 被舍回原值 → 空转 32 次并标 `Converged=false`。
   本地用 numpy.float32 完整复现（24 组里 17 组 32 次、7 组 2~3 次）。
   **修法**：加 `nextT <= t` 停滞判定 —— 若 `dist <= 4ε` 视为数值接触并
   `Converged=true`（Residual 原样保留可观测），否则判本帧不接触。
   这是数值稳健性修复，不是放宽 ε；修后常见场景 2~3 次迭代收敛。

2. **管线测试的语义假设错了**（`TestR10PipelineNewSwitchesDefaultOffAndZeroRegression`）：
   我断言「CA + 多面同时开启后常规 `StepFixed` 与默认路径逐位一致」，
   但 `UseMultiFace` 直接改 `YrNarrowPhase.Build`，常规窄相的接触点集
   必然变化 → 分叉是**预期行为**，不是 bug。
   **修法**：拆成两段 —— `UseExactCa` 单独开（只作用于 `StepFixedToi`）
   必须与默认逐位一致；`UseMultiFace` 开则验它自身逐位确定 +
   快照回滚重放一致。诊断通道用完即删。

## 回归门禁

十轮累计 **121 项**真实物理断言（9 + 14×8），全部进入现有 `build.yml` 的
Test step 真实执行：无 `[Skip]`、无吞异常、无阈值放宽。
音频 5 个参考一致性用例全部在真实门禁里（第九轮清零）。

## 不能宣称的（不变且不缩小）

- **不宣称 GTA6 / RAGE 级最真实，不宣称行业 SOTA**：见 `docs/SOTA_GAP_ANALYSIS.md`。
- 线性化 CA 非完整旋转 CA；多面流形的入射面仍按全局法向选一次，斜角多面接触
  是启发式；接触缩减是几何启发式；序列冲量非全局 LCP 精确解；
  PBF 非 N–S 完整解；XPBD 非有限元；断裂为工程判据；Tait 弱可压缩近似；
  化学反应为集总宏观模型；材料参数为工程量级近似。
- **GPU 端到端、`0xC0000005`、百万粒子/刚体堆叠真机压测、多平台导出、
  Rust 原生内核编译**都未在沙盒验证。

能确认的仍是：**守恒可核算、角动量严格守恒、逐位确定、云端真实编译并执行**。

## 下一步

1. 完整旋转 CA：每步四元数积分 + 重枚举 15 轴（消除轴集合固定带来的保守偏差）。
2. 多面流形按每个参考面各自重选入射面；接触缩减改按 Jacobian 线性独立性。
3. 动态树接 GPU 帧循环做 1000+ 刚体压测；编译并加载 Rust 原生内核跑 parity。
4. 扩 MP3 夹具覆盖（立体声 / MPEG-1 44.1k / 短块混合块 / ID3v2 / CRC 帧）。
