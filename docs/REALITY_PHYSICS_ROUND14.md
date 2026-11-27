# 真实物理内核 · 第十四轮：完整 3×3 接触有效质量矩阵（2026-11-27）

延续口径：**不新增语言、不动渲染管线、不改 sln/CMake/NuGet/csproj、不宣称行业 SOTA**。
本轮同时完成三件跨领域工作，见 `docs/ROUND14_DELIVERY.md`。

## 1. 自研边界澄清（用户口径）

用户澄清「全部自研 = **引擎功能面代码不借鉴其他开源引擎**，不是零第三方依赖」。
`docs/SELF_RESEARCH_BOUNDARY.md` 把这条写成可审计规则：

- 允许把 Silk.NET / Roslyn / ImGui.NET / xunit / Code - OSS 当作**基础设施依赖**，
  但必须锁定版本、逐项署名 SPDX（`docs/THIRD_PARTY_SPX.md`），不得把其实现
  拷进 `YamaEngine/` 充当自研。
- 禁止复制/移植 Godot / Unreal / Unity / PhysX / Havok / Bullet / Box2D / Jolt /
  Bevy / O3DE 的源码、类结构、算法实现或注释。
- 允许参考公开**协议规范**（Vulkan spec / LSP 3.17 / DAP 1.x / JSON-RPC 2.0 /
  FLAC·MP3·Vorbis 格式规范 / RFC），落地代码自行编写。

`scripts/audit_no_engine_copy.py` + `RepoContractTests.TestNoForeignEngineCopyrightHeadersInSources`
在每次 CI 扫描全部 `.cs` 源文件头部，命中其他引擎版权/许可/路径特征即红灯。
`TestSelfResearchBoundarySpdxCoversEveryPackageReference` 强制 SPDX 清单覆盖
`YamaEngine.csproj` 的**全部 13 个** `PackageReference`。

## 2. 完整 3×3 接触有效质量矩阵（算法层增量）

第十三轮 `YrContactEffectiveMass` 只取 K 的**对角项** `K(d)=invM_a+invM_b+
(r×d)·I⁻¹(r×d)`，忽略法向/切向耦合。本轮 `YrContactMassMatrix3x3` 补上非对角：

```
在接触正交基 (n,t1,t2) 下：
K_ij = (invM_a+invM_b)(d_i·d_j)
     + (r_a×d_i)·I_a⁻¹(r_a×d_j) + (r_b×d_i)·I_b⁻¹(r_b×d_j)
```

平动项因基正交只留对角；**角项产生非对角耦合**。自研数值路径：

- `Cholesky` → `Solve(Kx=b)`
- 自研 cyclic Jacobi 对称特征分解（48 次扫描）→ `K^{-1/2}=Q·diag(1/√λ)·Qᵀ`
- `CoupledContactRow`：3 个原始 6D wrench 行按 **K^{-1/2} 混合**，再乘摩擦锥缩放，
  拼成 18D 行；`ReduceByCoupledMass` 走 `ReduceByRows` 严格 MGS

**可判定 parity**：当 K 严格对角（零角惯量 + 接触点在质心）时，
`C=diag(1/√K_kk)`，耦合行**逐位退化**为第十三轮 `WeightedContactRow`。
另有 `C·K·C=I` 白化恒等式、`L·Lᵀ=K`、`Kx=b`、特征方程、非对角场景与标量版
选点差异、奇异上下文无 NaN/Inf、Jacobi 逐位确定、默认管线零回归。

**诚实边界**：仍是单点接触的 3×3 近似（非逐点独立约束的完整 LCP K 矩阵）；
摩擦锥缩放沿用一阶近似；Jacobi 是固定扫描次数的数值特征分解；
**`ReduceByCoupledMass` 尚未接入 `YrContactSolver` 的逐接触求解**。

### 推 CI 前的本地数值预演（两处测试构造真修）

用 float32 复刻全套数值路径，提前发现两个会让云端红灯的**测试写法**问题：
① 力臂场景里 `n=+Z`、`r` 在 XY 平面时，K 的非对角只出现在 `(t1,t2)` 即
`At(1,2)`，`At(0,1)/(0,2)` 恒为 0 —— 断言按真实耦合位置改写；
② 第一版缩减点集下耦合版与标量版选点完全相同，换成数值预演确认有差异的
点集（耦合 `[0,3,4]` vs 标量 `[0,3,5]`）。**没有放宽任何阈值**，
`C·K·C=I` 白化误差实测 `~1e-7`、对角退化 parity 实测 `0`、
非对角行差异实测 `0.208`、Jacobi 特征方程误差 `~3e-7`。

## 3. 内置 Code - OSS 1.148.0 与 Tuan-CLI 融合

见 `ide/codeoss/README.md` 与 `integrations/tuan_cli/README.md`。
两块的 C# 契约（`CodeOssBridgeTests` 12 项 + `RepoContractTests` 4 个 [Fact]）
都进入现有 `build.yml` 的 Test step 真实执行。

## 回归门禁

十四轮累计 **190 项**反射式断言（前十三轮 164 + 本轮 14 物理 + 12 Code-OSS/Tuan 桥接）
+ 5 个音频用例，
另有 `RepoContractTests` 的 xunit `[Fact]` 契约门禁（原有 + 新增 4 个）。
全部真实执行：无 Skip、无吞异常、无阈值放宽。

## CI 稳定性（不粉饰）

main 首次 CI 出现过一次**同 commit 偶发 Test 红**：develop 全绿、
失败落盘诊断零失败、`rerun` attempt 2 全绿。已把两个测试类里的仓库根定位
改成多锚点 + 双起点 + 环境变量兜底，并重新跑 develop/main 双 CI。
不因此删断言或放宽阈值，详见 `docs/ROUND14_DELIVERY.md`。

## 不能宣称的（不变且不缩小）

`docs/SOTA_GAP_ANALYSIS.md` 的 G0–G3 已闭合；**G4–G8**（真机 GPU 帧 /
规模压测 / 多平台导出 SDK / 生态 / 外部验证）本环境物理上不具备。
Code - OSS 干净 MIT workbench 的完整 Electron 构建**未在现有 CI 编译**
（无 Node/Electron 链，模板在 `ci/codeoss-gate.yml`），
submodule 源码约 1.8 GB 未 blob 入库。**不宣称已达 SOTA。**
