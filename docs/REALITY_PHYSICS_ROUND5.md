# 真实物理内核 · 第五轮：引擎级统一编排（2026-11-25 继续）

前四轮把能力一个个做对了，但它们是**散落的组件**：SAT 算出命中后没人消费，
接触求解器要手工喂 `YrContact[]`，岛/时钟/回滚各自独立。
这一轮不再加孤立算法，而是把它们接成一个 **Godot PhysicsServer 式的单一入口**。

## 管线全貌

```
真实帧 dt
  → YrFixedClock.Advance（double 累加 / 插值 α / 死亡螺旋保护）
  → 每个固定步：
       SyncObbs（刚体位置+四元数 → OBB）
       → YrBroadPhase.SweepAndPrune（AABB / X 轴剪枝 / 确定性候选对）
       → YrNarrowPhase（CCD 包围球预筛 + TOI 排序 → SAT → 接触流形 → YrContact[]）
       → YrIslandSolver（接触图 + 关节图并查集，较小根定序）
       → YrContactSolver（warm start / 序列冲量 / 二维库仑锥 / 位置投影）
       → YrJointSolver（Ball / Slider / Hinge）
       → RigidBody3D.Integrate（休眠岛跳过）
       → 岛休眠状态机
       → Snapshot / SaveFrame（FNV-1a 位级校验和）
```

`YrPhysicsPipeline.Step(realDt)` 是唯一入口；`StepFixed(dt)` 公开以便逐 tick 回放。
`Snapshot/Restore/RewindTo` 让「逐位确定」不只是断言，而是可回滚重放的能力。

## 三个新组件

| 组件 | 作用 | 关键正确性 |
|---|---|---|
| `YrBroadPhase` | OBB→AABB 解析包络、8 顶点枚举、**Sweep and Prune** | 插入排序 + 索引升序 tie-break；X 轴剪枝 `b.minX > a.maxX` 即 break；输出按 `(A,B)` 字典序，**与输入顺序无关** |
| `YrContactManifold` | 把 SAT 单近似点扩成最多 4 点 | 入射体 8 顶点按侵入深度排序（depth 降序 / index 升序）；投影半径 `ra = Σ\|n·u_i\|h_i`；无侵入顶点时回退单点 |
| `YrNarrowPhase` | CCD 预筛 + TOI 排序 + SAT + 流形 + `YrContact[]` + 跨帧 warm 缓存 | TOI 升序→`(A,B)` 升序定序；`CacheWarm` 按对缓存 λ_n；**不遍历 Dictionary**，避免哈希顺序破坏确定性 |

## 诚实边界（本轮特意不假装完成的部分）

1. **接触流形是顶点侵入近似，不是 Sutherland-Hodgman 面裁剪**。
   边-边接触、共面面接触的精确流形仍未做，SAT 的 `ContactPoint` 也仍是近似点。
2. **CCD 只做预筛 / TOI 观测 / 接触对排序，尚未实现 TOI 子步回退**。
   即没有把固定步拆成 `[0,toi]` 与 `[toi,dt]` 两段分别求解；
   「高速物体恰好本帧穿过薄板」仍可能漏检。这一环明确留给真机侧。
3. **包围球半径用 `halfExtents.Length()`**，是保守上界，不是最小包围球；
   宽相因此偏保守（多报候选对），不会漏报。
4. **warm start 缓存按 `(A,B)` 对取 `max(λ_n)`**，是简化策略，不是逐接触点
   持久化的完整缓存；多点流形下只有首个点吃到缓存。缓存本身已纳入位级快照，
   因此回滚重放逐位一致。
5. **Rust 本轮一行未动、不新增第四个导出**。宽相/流形/编排全是控制流密集，
   搬进纯数值内核没有收益反增 ABI 面。`sln` / `CMake` / `NuGet` /
   `YamaEngine.csproj` 同样一行未改（`Reality\**\*.cs` 通配自动纳入）。
   Rust 在 CI 仍不编译（主工程不依赖、沙盒无 cargo）。


## 静态审查抓到的三个真缺陷（都在第一次 CI 之前修掉）

| 缺陷 | 后果 | 修法 |
|---|---|---|
| `YrContactManifold` 插入排序用了 `(d, i, verts[i])` 无名元组却访问 `item.Depth` | 编译 CS1061 | 显式声明 `(float Depth, int Index, RVec3 Point) item` |
| **warm start 缓存没进快照** | `RewindTo` 后 `Restore` 清缓存，重放第一帧的 warm 冲量与原始序列不同 → **逐位分叉** | `ExportWarm`（按 `(A,B)` 排序，避开 Dictionary 遍历顺序）/ `ImportWarm` 纳入 `Snapshot` / `Restore` |
| **位置投影破坏轨道角动量守恒** | `CorrectPositions` 瞬移改变 `r`，`r×(mv)` 随之变化，总角动量不再严格守恒 | `PositionCorrectionBeta` 做成可配置；守恒测试设 `beta=0` 严格验，**不放宽容差**；文档明确这是非物理修正 |

第三点值得展开：成对接触冲量本身严格守恒总线动量与「自旋+轨道」总角动量，
但**位置投影不是冲量**——它改位置不改速度。只要 mv 有横向分量，
`Δr × mv ≠ 0`，总角动量就变了。这不是数值误差，是工程近似的固有代价。
所以没有靠调容差把它「验过去」，而是把开关暴露出来，并在真机路线里
把「TOI 子步 + 速度级约束替代位置投影」列为第一优先级。

## 回归门禁

五轮累计 **65 项**真实物理断言（9 + 14 + 14 + 14 + 14），
全部进入现有 `build.yml` 的 Test step 真实执行：无 `[Skip]`、无吞异常、无阈值放宽。
新增覆盖：AABB 解析包络 / 旋转 OBB 顶点被包住 / SAP 找全重叠对 /
分离无误报 / 排序确定性 / 流形点数上界 / 流形点贴接触面 / 流形逐位确定 /
窄相生成接触 / 无命中零接触 / CCD 报告接近 / 管线固定步 /
**管线总线动量+总角动量守恒** / 双管线逐位一致 + 回滚重放校验和复现。

## 不能宣称的（不变且不缩小）

- **不宣称 GTA6 / RAGE 级最真实**：专有实现未公开。
- 序列冲量是迭代近似非全局 LCP 精确解；PBF 非 N–S 完整解；XPBD 非有限元；
  断裂为工程判据；Tait 是弱可压缩近似；涡量约束是数值补偿；
  形状匹配是线性/刚性近似；化学反应为集总宏观模型；材料参数仍是工程量级近似。
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
```

## 下一步（真机侧，按优先级）

1. **TOI 子步回退**：把 CCD 命中但 SAT miss 的对按 `[0,toi]`/`[toi,dt]` 拆步求解，
   补上高速穿透这最后一环。
2. 接 GPU 帧循环做 1000+ 刚体 / 粒子压测，量测 SAP + 岛休眠的实际收益。
3. 把 SAT 升级为 Sutherland-Hodgman 面裁剪，得到完整接触流形。
4. 编译并加载 Rust 原生库，跑三个 parity 测试的原生路径。
5. 专项攻 MP3 的 M4/M5，把音频看板清到 0。
