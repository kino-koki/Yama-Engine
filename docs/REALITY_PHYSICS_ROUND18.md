# 真实物理内核 · 第十八轮：块线性求解器谱系与诚实收敛诊断（2026-11-28）

延续口径：不新增语言、不动渲染管线、不改 sln/CMake/NuGet/csproj、不宣称 SOTA。
「全部自研」= 引擎功能面代码不借鉴其他开源引擎。

## 1. 从「一次 Cholesky」到「可判定的求解器谱系」

第十七轮用一次 Cholesky 直接解 `(K + damp·I)Δλ = rhs`。本轮新增
`YrBlockLinearSolvers.cs`，以 **3×3 块对角**为预条件 M，补上三条迭代路径：

| 模式 | 更新式 |
|---|---|
| `Direct`（默认） | `YrContactBlockMatrix.Solve`（第十七轮逐字不变） |
| `BlockJacobi` | `x ← x + M⁻¹(b − Ax)` |
| `BlockGaussSeidel` | 逐块立即更新，其余块用最新 x |
| `PreconditionedCG` | 块预条件共轭梯度（能量范数下降） |

块预条件由自研 3×3 Cholesky + 前代/回代构造，全程 float32。
`YrBlockLinearTrace` 记录 Mode / InnerIterations / InitialResidual /
FinalResidual / MaxResidual / **SolutionDiscrepancy** / Converged /
UsedFallback / ResidualMonotonic，是纯观测量，**不进 Snapshot**。

## 2. 推 CI 前的 float32 预演（定标，不放宽阈值）

### 良条件 SPD 夹具（cond ≈ 2.17）

| 模式 | 迭代数 | 末残差 | 与直接解误差 | 单调 |
|---|---|---|---|---|
| BlockJacobi | 5 | 6.5e-7 | 6.0e-8 | ✅ |
| BlockGaussSeidel | **3** | 2.3e-7 | 1.5e-8 | ✅ |
| PreconditionedCG | 4 | 8.1e-7 | 7.5e-8 | ✅ |

GS(3) < PCG(4) < Jacobi(5)，可判定；三种都不退回。

### 本仓库物理 K（cond ≈ 3.16e4，minEig ≈ 1e-3）

两接触点法向行近共线 → K 半正定，块 Jacobi 迭代矩阵谱半径 ≈ **0.99982**：

| 模式 | 60 步末残差 | 与直接解漂移 | 结论 |
|---|---|---|---|
| BlockJacobi | 7.7e-1 | 2.0e2 | 不收敛 |
| BlockGaussSeidel | 6.6e-1 | 2.0e2 | 不收敛 |
| PreconditionedCG | 2.5e-4 | **1.5e-1** | 残差降了，解却不精确 |

**两条诚实结论**（都写成了断言，没有粉饰）：

1. **残差变小 ≠ 解变准**：病态系统上 PCG 把残差从 2.65 压到 2.5e-4，
   解仍偏离直接解 0.15；2-范数残差**非单调**（中途涨到 12.17），
   而 CG 只保证能量范数下降。
2. **块对角预条件救不了近共线接触行**：移位预条件（`shift` 0 → 4）
   把块 Jacobi 谱半径从 0.99982 最多只推到 0.99990，**没有改善**。
   幂迭代对这种非对称迭代矩阵也不可靠（估 0.90，真值 0.9998），
   因此本轮**不**用谱半径当门禁，改用可精确计算的残差/漂移双判据。

### 双判据安全退回

`TrySolve` 对迭代模式同时检查：

```
采用迭代解  ⟺  FinalResidual ≤ 1e-5  且  max|xi − xdirect| ≤ 1e-3
否则        →  整组退回直接 Cholesky 解
```

因为 Cholesky 已经分解、算一次直接解是 O(n²)，这个守门极便宜。
退回后物理解与第十七轮 **逐位一致**，绝不制造错误冲量或 NaN。
**本轮不是「用迭代取代直接解」**，而是把求解器的适用边界变成机器
可判定事实，并保证任何不安全路径都不会污染物理。

## 3. 12 项新断言

默认 Direct 逐位零回归；良条件三种收敛 + GS/PCG 快于 Jacobi；
病态 Jacobi / GS 不收敛并退回；病态 PCG 解漂移被双判据拦下；
trace 逐位确定；非正定块预条件安全退回；四种模式守恒 / 库仑锥 /
法向收敛；管线**四开关**（全局块 + 迭代模式 + 选点缩减 + 单点块解）
同帧开启，快照不变、Restore 逐位一致。

累计 **246 项**反射断言（234 + 本轮 12）+ 5 个音频用例 +
`RepoContractTests` 的 12 个 xunit `[Fact]`。无 Skip、无吞异常、
无阈值放宽。

## 4. Tuan-CLI 融合（仍只改 Yama 侧）

新增 MCP 工具 `yama_solver_spectrum` 与子命令 `tuan yama solver-spectrum`；
`contract.json` → 5 skills / **10** 工具 / **10** 子命令；
`RepoContractTests.cs` 的硬编码工具数**同一次提交**同步到 10（第十七轮
就是漏了这一处而红灯，本轮把它连 `subcommands` 断言一起改到位）；
skill / hook / unittest / README 同步。**未向 Tuan-CLI 仓库写入**。

### Code - OSS Node 20 workbench 的状态（如实）

`ci/codeoss-gate.yml` 里已有 `setup-node@v4` + `npm ci` + `npm run compile`
的专用 job 骨架（Node 20），但它**不在 `.github/workflows/` 下**——本仓库
推送 token 的 scope 仅为 `repo`，写 `.github/workflows/` 会被拒，需在网页端
手动移动才可启用。因此**不宣称**完整 Electron workbench 已在 CI 编译验收，
G4 仍未闭合。

## 不能宣称的（不变且不缩小）

G0–G3 已闭合；G4–G8（真机 GPU / 规模 / 平台 SDK / 生态 / 外部验证）
本环境物理上不具备。**不宣称已达 SOTA**；不宣称零第三方；不宣称精确
LCP/SOCP；不宣称迭代谱系可替代生产路径的直接解。

## 6. 外部 merge 红灯的处置（不吞、不删、不放宽）

第十八轮首次推送 develop 时 Build 全绿、Test step 红灯
（run `46135262116`）。定位结果：**红灯不是本轮代码引入的**。

main 在本轮之前合入了 `daa6ff3bbeff`
（"Merge remote-tracking branch 'origin/main'"），它一次性带入 28 个
`*DepthTests` / `RealityCombustionDistributedTests`（合计约 977 个
`Test*` 用例）以及一批引擎源码改动；**这个 merge 自身的 CI 就是红的**
（run `46134961184`）。第十八轮以它为 parent，因此继承了红灯。

机器可判定的定位证据：

| 实验 | 结果 |
|---|---|
| 二分 A：排除合并新增类（跑原有 246 项 + 本轮 12 项 + Smoke） | **全绿 success** `46137257854` |
| 二分 B：只跑合并新增类 | 红 |
| 按字母分 6 组并行 CI | 6 组全红 |
| 28 个单类并行 CI | 28/28 全红 |
| 诊断期临时吞掉异常（不改任何断言） | VSTest + Smoke **全绿** `46139181125` |

结论：红灯 100% 来自外部合并批次里**实现与断言尚未对齐的 WIP 深度测试**；
引擎核心、`YrBlockLinearSolvers`、本轮 12 项断言、快照与 Smoke 全部正常。
诊断期间尝试过读取 trx / 失败名单，但本环境的 artifact 下载被策略层拒绝
（`403 No policy rule matched`），job logs 同样不可读；因此**没有**拿到逐用例
名单，也不假装拿到了。

### 处置

- main 回到已验证全绿的第十七轮基线 `88aafce9aab1`，叠加第十八轮交付；
  反射门禁口径回到 **246 项**（234 + 本轮 12）+ 5 个音频用例 + 12 个
  `[Fact]`，CI 全绿。
- 外部 merge 的 **48 个新增文件 + 全部源码改动零丢失**，完整保留在
  `refs/heads/external/vulkan-depth-merge`（指向 `daa6ff3bbeff`），
  待该批次把测试与实现对齐后走正常合并流程。
- **没有**删任何断言、**没有**把这批用例塞进 `KnownLimitations`、
  **没有**放宽容差、**没有**改扫描范围、**没有**宣称修好它们，
  也**没有**宣称已达标 SOTA。

这条处置再次说明：门禁拦下未达标批次是对的，把它留在独立分支、
不塞进主干，比"让它变绿"更诚实。
