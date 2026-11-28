# 真实物理内核 · 第七轮：可规模化 CCD 与完整接触流形（2026-11-25 继续）

延续口径：**不新增语言、不动渲染管线、不宣称 GTA6 级**。
这轮专攻第六轮「下一步」里前两条：**O(n²) 扫掠** 与 **顶点侵入近似流形**，
并顺手补上**角速度保守外推**。

## 1. 扫掠 AABB BVH：从 O(n²) 到 O(n log n + k)

`YrSweptBroadphase` 给每个刚体构造「整帧轨迹」AABB：

```
swept = Fat( Inflate( 当前AABB ∪ (当前AABB + v·dt), 角速度膨胀量 ) )
```

第六轮已证明「宽相基于当前 AABB 会漏掉高速穿透」——因为高速物体**当前分离，
整帧轨迹相交**。用轨迹 AABB 才是正面解法，而不是绕开宽相去做全对扫掠。

BVH 用**最长轴中位分割、叶容量 4、每帧重建**：

- 按 `(中心坐标, 索引)` 升序排序，索引 tie-break 保证逐位确定
- DFS 只对重叠节点对下探，叶层两两做 AABB 检测，输出按 `(A,B)` 字典序去重
- 每帧重建是显式选择（非增量动态树）：实现可验证、对 1000+ 体足够

**不漏报 parity 测试**：`TestR7SweptPairsIsSupersetOfAllToiHits` 构造 12 个确定性
布局的刚体，枚举全部 66 对，凡 `ComputeToi >= 0` 的对**必须**出现在 BVH 候选超集里。
另用 200 体均匀散布证明候选对 < 400，而全对基数是 19900。

## 2. Sutherland-Hodgman 面裁剪（真流形）

第五轮的 `YrContactManifold.Build` 是「8 顶点里取侵入最深的至多 4 个」，
对边-边接触、共面面接触会给出偏斜点集。新增 `YrFaceClipping.Build`：

```
① 参考面 = A 上法向与 n 最对齐的面（3 轴取 |dot| 最大）
② 入射面 = B 上 n·axis 最小的面，取 4 顶点
③ 顶点投影到参考面局部 2D (u,v)，用矩形 4 条侧边做 Sutherland-Hodgman 裁剪
   （线性插值时同步插值 U/V/深度 D/3D 点 P）
④ 按侵入深度 D 降序、索引升序保留至多 4 点，输出参考面上的 3D 点
```

`Clip` 的四条边 `f(u,v)` 与 `-Eps` 内侧判定、交点插值都是标准实现；
`InsideReference` 暴露给测试，断言每个输出点都在参考面矩形内。

**诚实边界**：这仍是**单对参考/入射面**近似，不处理多面同时接触、
不生成带持久 id 的逐点 warm start 缓存；incident 面选择是启发式。
但它已经从「顶点侵入近似」升级为真正的面裁剪流形。

## 3. 角速度保守膨胀 CCD

`YrSweptObb.AngularInflation(|ω|, R, dt) = R · min(1, |ω|·dt·√2)`，
把膨胀量加到 15 轴扫掠的每根轴投影半径 `r` 上，得到 `ComputeToiWithAngular`。

- **保证不漏报**：`tEnter` 只会更早或相等；测试断言 `fast ≤ slow ≤ plain` 且零角速度不膨胀。
- **可能早报**：这是保守上界近似，**不是**精确旋转扫掠（conservative advancement），
  且随 dt 线性增长、dt 越大越保守。真机侧可换成真正的 CA 迭代。

## 4. 三个开关，全部默认 false（零回归）

| 开关 | 作用 | 默认 |
|---|---|---|
| `UseBvhCcdPairs` | 用 BVH 轨迹候选对替代 O(n²) 全对 CCD；`LastCcdPairs` 可观测 | false |
| `UseAngularSweep` | 切到 `EarliestToiAllAngular` | false |
| `UseFaceClipping` | 窄相切到 `YrFaceClipping.Build` | false |

`YrToiAdvance.Advance` 新增可选 `ccdPairs` 参数（`null` = 原全对路径），
候选对由**整帧**扫掠算出，子步 `remaining` 变小后仍是保守超集。
`YrPhysicsPipeline` 只做透传，第六轮 `StepFixed` 核心一行未改——
新能力正确与旧路径不回归同时被证明。

## 5. 语言与构建边界（继续收窄）

- **不新增语言、不新增 Rust 导出**。BVH、面裁剪、扫掠全是控制流密集，
  纯数值内核无收益。`sln` / `CMake` / `NuGet` / `YamaEngine.csproj` 一行未改
  （`Reality\**\*.cs` 通配自动纳入）。
- Rust 内核在 CI **仍不编译**（主工程不依赖、沙盒无 cargo）。
- **音频 MP3 看板仍 1 条**（解码器固有延迟 + 末帧 bit reservoir），本轮不碰。

## 回归门禁

七轮累计 **93 项**真实物理断言（9+14+14+14+14+14+14），
全部进入现有 `build.yml` 的 Test step 真实执行：无 `[Skip]`、无吞异常、无阈值放宽。

## 不能宣称的（不变且不缩小）

- **不宣称 GTA6 / RAGE 级最真实**：专有实现未公开。
- BVH 是每帧重建的静态中位分割树，非增量动态树；
  角速度是保守上界近似、非精确旋转扫掠；扫掠 TOI 仍是线性匀速外推；
  面裁剪是单对参考/入射面近似；序列冲量非全局 LCP 精确解；
  PBF 非 N–S 完整解；XPBD 非有限元；断裂为工程判据；Tait 为弱可压缩近似；
  化学反应为集总宏观模型；材料参数仍是工程量级近似。
- **GPU 端到端、`0xC0000005`、百万粒子/刚体堆叠真机压测、多平台导出、
  Rust 原生内核编译**都未在沙盒验证。
- **渲染管线一律未动**。

能确认的仍是：**守恒可核算、角动量严格守恒、逐位确定、云端真实编译并执行**。

## 下一步（真机侧）

1. 把静态 BVH 换成增量动态 AABB 树（减少每帧重建开销），接 GPU 帧循环做 1000+ 刚体压测。
2. 用真正的 conservative advancement 替换角速度保守上界，补旋转扫掠。
3. 编译并加载 Rust 原生库，跑三个 parity 测试的原生路径。
4. 专项攻 MP3 的 M4/M5，把音频看板清到 0。
