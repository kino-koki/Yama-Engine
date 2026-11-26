# 真实物理内核 · 第二轮深化（2026-11-25 继续）

## 先纠正上一轮的一处交付缺陷

上一轮 `feat/reality-physics-architecture` 分支**无法独立编译**：它带了引用
`KnownLimitations.IsSkipped` 的测试适配器，却没有带 `KnownLimitations.cs` 本身
（CI 是在含完整历史的 develop 上验证的，误把「develop 绿」等同「干净分支绿」）。

本轮改为：**干净分支 = main + 全部自洽差异**，并且**用与已验证 develop 完全相同的
tree sha 重建**，逐字节一致。分支现在独立可编译、可跑 CI，不再依赖 develop 历史。

## 第二轮新增的物理能力

| 能力 | 文件 | 相对第一轮的升级 |
|---|---|---|
| 刚体角动量 | `RigidBody3D.cs` | 以 **世界系角动量 L 为状态量**，无力矩时逐位守恒；各向异性惯性张量下 ω 随姿态自由进动（把 ω 当常量是错误简化） |
| 四元数姿态 | `RQuat` | 半隐式积分 + 每步归一化，避免漂移 |
| 接触摩擦 | `ContactFriction.cs` | 恢复系数法向冲量 + **库仑摩擦锥投影**（静摩擦全额止滑 / 动摩擦 μ·|j_n| 截断） |
| 连续碰撞 | `ContinuousCollision.cs` | 扫掠球-平面 / 球-球 **TOI 解析解**，防高速穿透 |
| 弱可压缩 EOS | `EquationOfState.cs` | **Tait 状态方程** `p=B[(ρ/ρ0)^γ−1]` + 局部声速 + Akinci 风格 cohesion 表面张力 |
| 热↔流体闭环 | `ThermoFluidCoupler.cs` | **Boussinesq 浮力**、半拉格朗日对流、**涡量约束**（补 PBF 数值耗散丢失的涡） |
| 邻域加速 | `NativeKernel.cs` + Rust | CSR 邻域版密度遍历，**O(N·k)**；Rust 只做连续内存累加，易向量化 |
| 编排闭环 | `RealityPhysicsWorld.cs` | `StepWithBuoyancy` 把温度场 → 浮力 → PBF 预测步 → 结构损伤接通 |

### 最关键的物理正确性修正：角动量而非角速度

第一轮的 XPBD 只处理质点，没有转动。这一轮补刚体时没有采用常见的
「ω 当常量、q += ½ω⊗q」写法——那对各向异性刚体是错的（自由刚体会进动）。
改为把 **L（世界系角动量）作为守恒状态量**：

```
ω_world = R · (I_local⁻¹ · (Rᵀ · L))
q̇ = ½ · Quat(ω_world, 0) ⊗ q , 归一化
L̇ = τ_external
```

于是「无力矩 → L 逐位不变」是结构性保证，而不是数值近似；测试
`TestAdvAngularMomentumConservedExactly` 跑 2000 步断言逐位相等，
`TestAdvAnisotropicPrecession` 同时断言 ω 确实发生了变化。

### 关于「多语言互补」的进一步收窄

仍然**不新增语言**，且把 Rust 的接触面压得更窄：

- 邻域构建（空间哈希、27 邻桶、索引升序、CSR 打包）**全部留在 C#**，
  因为它决定确定性，必须走同一份已验证代码；
- Rust 新增 `yr_density_pass_csr`，只接收 `positions + offsets + indices`，
  做**纯数值连续累加**——没有分配、没有全局状态、没有哈希、没有排序；
- 这样「启用 Rust」与「纯 C# fallback」的公式与遍历顺序完全同构，
  `TestAdvCsrDensityMatchesFullPairwise` 断言 CSR 结果与全对 O(N²) **逐位一致**；
- 依然不用静态平台调用特性，`NativeLibrary.TryLoad` 探测，缺失即回退；
  主 `sln` / `CMake` / `NuGet` 一行未改，Rust 仍是独立可选 crate。

> 诚实边界：Rust 内核在 CI 里**不编译**（主工程不依赖它），本轮只做了
> 静态审查 + C# 同构实现的逐位校验。真机启用前请自行 `cargo build --release`
> 并跑一次 `TestAdvCsrDensityMatchesFullPairwise` 的原生路径。

## 回归门禁

新增 `RealityPhysicsAdvancedTests` 共 **14 项**，加上第一轮 9 项，
真实物理内核合计 **23 项断言**进入 `build.yml` 的 Test step：

角动量守恒 / 各向异性进动 / 力矩积分 / 四元数归一化 /
静摩擦止滑 / 动摩擦截断 / 球-平面 TOI / 球-球 TOI /
Tait EOS 与表面张力 / Boussinesq 浮力 / 零速度对流恒等 /
均匀场零涡量力 / CSR 与全对逐位一致 / 热浮力闭环有限性。

## 仍未宣称的部分（不变且不缩小）

- **不宣称 GTA6 / RAGE 级最真实**：专有实现未公开。
- PBF 不是 N–S 完整解；XPBD 不是有限元；断裂是工程判据；
  Tait EOS 是弱可压缩近似；涡量约束是数值补偿而非真实湍流模型；
  材料参数仍是工程量级近似。
- **GPU 端到端、`0xC0000005`、百万粒子 / 刚体堆叠真机压测、多平台导出**
  都未在沙盒验证（沙盒无 GPU、无 .NET 运行时、无 Rust 工具链）。
- **渲染管线一律未动**（用户明确要求）。

能确认的仍是：**守恒可核算、角动量结构性守恒、逐位确定、云端真实编译并执行**。

## 验收

```bash
dotnet build YamaEngine/YamaEngine.csproj -c Debug -warnaserror
dotnet test YamaEngine.Tests/YamaEngine.Tests.csproj \
  --filter "FullyQualifiedName~RealityPhysics"
cargo build --release --manifest-path native/reality_physics/Cargo.toml
```
