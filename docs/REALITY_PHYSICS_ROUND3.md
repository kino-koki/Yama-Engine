# 真实物理内核 · 第三轮深化（2026-11-25 继续）

延续前两轮口径：**不新增语言、不动渲染管线、不宣称 GTA6 级**，
只把「可核算、可回放、可验证」的物理能力继续往真实推。

## 上轮留下的两个待办，本轮各推进一半

| 待办 | 本轮状态 |
|---|---|
| Rust 内核在 CI 不编译（沙盒无 cargo） | 仍是边界，但把 C# 同构校验扩到**三个导出**，并修了「一个导出缺失拖垮全部」的探测健壮性 |
| MP3 音频看板 1 条（解码器固有延迟 + 末帧 reservoir） | **本轮不碰**，留作已精确定义的专项；看板仍 1 条，不假装清掉 |

## 第三轮新增能力

| 能力 | 文件 | 物理意义 |
|---|---|---|
| 3×3 矩阵 + 极分解 | `YrMat3.cs` | 形变梯度、逆、行列式、Higham 迭代提取旋转因子 |
| 形状匹配约束 | `YrShapeMatch.cs` | `A = (Σpqᵀ)(Σqqᵀ)⁻¹` 生成整体目标位置，软体/布料不塌陷；可切「仅刚性」模式 |
| 凸体窄相 SAT | `YrConvex.cs` | 15 轴（3+3+9 叉积）OBB-OBB，最小穿透轴 + 深度 + 法向，旋转薄板也稳定 |
| 流固双向耦合 | `YrFluidRigid.cs` | `v+ω×r` 表面速度边界 + EOS 压力 + 二次阻力，显式过账；此前 PBF 与刚体各解各的 |
| 约束岛 + 休眠 | `YrIsland.cs` | 并查集连通分量（较小根保证确定性）、按岛速度休眠/唤醒，避免远处物体唤醒全世界 |
| Arrhenius 反应网络 | `YrReactionNet.cs` | 可逆反应 + van't Hoff 平衡 + 显式子步 + 非负裁剪 + 配平与元素守恒校验 + 反应热过账 |
| 固定步时钟 | `YrFixedClock.cs` | Godot 3 `_physics_process` 语义：double 累加、插值 alpha、死亡螺旋保护 |
| 编排接入 | `RealityPhysicsWorld.cs` | `FixedStepAdvance(realDt)` + `AttachIslands`，旧 `Step` / `StepWithBuoyancy` 签名不变 |
| Rust 第三热点 | `yr_covariance_accum` | 形状匹配协方差 9 路乘加，纯连续内存，C# 同构逐位校验 |

### 三个「比画面更真实」的点

1. **形状匹配不是逐对距离约束**。距离约束只能保边长，整体剪切/体积仍会塌；
   形变梯度 + 极分解给出整体旋转目标，`RigidOnly` 时甚至能把剪切分量剥离。
   `TestR3ShapeMatchRecoversRotation` 断言旋转 90° 的梯度矩阵逐项正确。

2. **固定步是物理正确性的前提，不只是性能 trick**。可变 dt 直接积分会让
   XPBD 的 `α̃ = compliance/Δt²` 失去意义、让休眠判据随帧率漂移。
   `YrFixedClock` 用 double 累加 + clamp，测试断言 `0.055s → 3 步 + α=0.3`。

3. **化学反应必须同时过两道门**：配平（元素矩阵零和）和守恒（总元素量不变），
   而且反应热要显式进账本。`TestR3ArrheniusMonotonicAndConservation` 一次验完
   单调性 / 守恒 / 非负 / 账本平衡 / 平衡态净速率趋零。

### 多语言边界（第三轮再收窄）

- 三个 Rust 导出 `yr_density_pass` / `yr_density_pass_csr` / `yr_covariance_accum`
  **各自独立探测**：任一缺失不再让其他加速路径全部退化。
- 邻域构建、CSR 打包、矩阵求逆、极分解、SAT 轴枚举、岛标签、时钟累加
  **全部留在 C#**——它们决定确定性；Rust 只做三种纯连续内存乘加。
- 不用静态平台调用特性；主 `sln` / `CMake` / `NuGet` 一行未改；
  `YamaEngine.csproj` 仍是上一轮那 **1 行** `Reality\**\*.cs` Include，本轮连它都没动。
- Rust 在 CI **仍不编译**（主工程不依赖、沙盒无 cargo）。真机启用前请自行
  `cargo build --release --manifest-path native/reality_physics/Cargo.toml`。


## 诊断轮抓到并修掉的两个真缺陷（不是文档润色）

第一次 CI 编译红：`cov` 局部变量漏声明（补齐）。第二次 CI Test 红，
临时失败落盘回读到 3 个精确失败，全是真问题：

| 失败 | 根因 | 修法 |
|---|---|---|
| `TestR3ShapeMatchPureTranslation` 形变梯度 ≠ I（差 1） | 测试用共面 4 点，`Σqqᵀ` 的 z 方向为 0 → 奇异 → 求逆失败退回 Identity，A 退成 `Apq` | Tikhonov 正则化 `cov+λI` 保住非退化方向；**退化方向假设无变形**（取单位行）。这是信息不足时的最小假设，不是硬凑 |
| `TestR3FixedClockStepsAndAlpha` 一个固定 dt 返回 0 步 | `1f/60f` 比 `1.0/60.0` 大一个 ulp，用 float 存固定 dt 后 `realDt/fixedDt < 1` → floor 0 | 构造参数改 `double fixedDt = 1.0/60.0`，World 与测试同步；累计器本来就是 double |
| `TestR3WorldFixedStepAndCovarianceParity` 期望 3 步实际 2 步 | 同上：`0.05f` 被略大的 float 固定 dt 除成 2.9999998 | 同一修复覆盖 |

三处都不是降阈值或跳过用例：形状匹配现在能正确处理退化点云，固定步在
不同帧率下不再丢步——后者对 XPBD 的 `α̃=compliance/Δt²` 和休眠判据是正确性问题。

## 回归门禁

三轮累计 **37 项**真实物理断言（`RealityPhysicsTests` 9 +
`RealityPhysicsAdvancedTests` 14 + `RealityPhysicsThirdTests` 14），
全部进入现有 `build.yml` 的 Test step 真实执行，无 `[Skip]`、无异常吞掉、无阈值放宽。

## 不能宣称的（不变且不缩小）

- **不宣称 GTA6 / RAGE 级最真实**：专有实现未公开。
- PBF 非 N–S 完整解；XPBD 非有限元；断裂为工程判据；Tait 是弱可压缩近似；
  涡量约束是数值补偿；SAT 接触点为近似点、非完整接触流形；
  形状匹配是线性/刚性近似、非线性超弹性材料未建模；
  反应网络是宏观动力学、非量子/微观反应；材料参数仍是工程量级近似。
- **GPU 端到端、`0xC0000005`、百万粒子 / 刚体堆叠真机压测、多平台导出、
  Rust 原生内核编译**都未在沙盒验证。
- **渲染管线一律未动**。
- 音频看板仍 1 条（MP3），已精确到「解码器固有延迟 + 末帧 bit reservoir」，
  没有为了数字好看而降阈值或藏用例。

能确认的仍是：**守恒可核算、角动量结构性守恒、逐位确定、云端真实编译并执行**。

## 验收

```bash
git fetch && git checkout feat/reality-physics-architecture
dotnet build YamaEngine/YamaEngine.csproj -c Debug -warnaserror
dotnet test YamaEngine.Tests/YamaEngine.Tests.csproj \
  --filter "FullyQualifiedName~RealityPhysics"
cargo build --release --manifest-path native/reality_physics/Cargo.toml
```

## 下一步（真机侧）

1. 接 GPU 帧循环做 1000+ 粒子 + 刚体堆叠压测，验证岛休眠的实际收益。
2. 把 SAT 命中结果喂给 `ContactFriction` + `ContinuousCollision`，形成完整窄相链。
3. 用 `YrReactionNetwork` 驱动燃烧放热 → 热应力 → 断裂的端到端场景。
4. 编译并加载 Rust `.so/.dll/.dylib`，跑三个 parity 测试的原生路径。
5. 专项攻 MP3 的 M4/M5（解码器延迟补偿 + 末帧 reservoir），把看板清到 0。
