---
name: Yama 耦合接触质量门禁
description: 核查 3×3 接触有效质量矩阵是否真的接进逐接触求解，验证默认关闭零回归、对角退化 parity 与守恒 trace
activation: auto
triggers: 耦合质量, coupled mass, 3x3 K, 块求解, UseCoupledMassMatrix, LastCoupledTrace, 全局块, UseGlobalBlockSolve, LastGlobalBlockTrace, 求解器谱系, GlobalBlockSolveMode, YrBlockLinearTrace
provides: yama-coupled-mass
suggests: yama-physics-gate, yama-test, yama-sota-audit
priority: 85
updated_at: 0
use_count: 0
---
# Yama 耦合接触质量门禁

第十五轮把第十四轮的算法原型接进了真实求解路径，本 skill 用来防止「只写了矩阵、没接线」的假交付。

1. 确认 `YrPhysicsPipeline.UseCoupledMassMatrix` 默认 **false**：190 项旧断言与位级快照必须逐位零回归；`LastCoupledTrace` 是纯观测量，不进 `Snapshot`。
2. 跑 `RealityPhysicsFifteenthTests.cs` 全套 14 项：对角 K 退化逐位 parity、各向异性场景 `CoupledSteps>0`、on/off 可判定差异、线动量/总角动量逐位守恒、库仑锥、收敛、奇异 fallback、trace 逐位确定。
3. 用 `yama_coupled_mass` MCP 工具或 `tuan yama coupled` 查询 trace：必须给出 `TotalSteps / CoupledSteps / FallbackSteps / MaxOffDiagonal / LastK / LastUsedCoupling`，不要只回「已启用」。
4. 核对 `YrContactMassMatrix3x3.BuildK` 的显式基重载：求解器用自己的 `Basis(n)` 传 `(n,t1,t2)`，不能与 `TangentBasis` 的基错位。
5. 核对第十六轮：`UseCoupledMassReduction` 是否接进 `YrNarrowPhase.Build`，默认必须关闭；开启后 `LastReducedPoints` 有界（≤4）、逐位深度按 `ra - (p-centerA)·n` 构造、不进 Snapshot。
6. 核对第十七轮：`UseGlobalBlockSolve` 是否接进 `YrContactSolver.Solve`，默认必须关闭；开启后 `LastGlobalBlockTrace` 给出 `Groups / MaxPointsPerGroup / TotalIterations / SolvedGroups / FallbackGroups / MaxCrossBlock / LastUsedGlobal`，用 `yama_global_blocks` 工具或 `tuan yama globalblocks` 查询，不要只回「已启用」。
7. 核对三开关同帧联动：全局求解 > 单点块解 > 默认 SI 的优先级明确，同时开启不崩、快照格式不变、默认路径三个 trace 全为 0。
8. 核对第十八轮：`GlobalBlockSolveMode` 默认必须是 `Direct`；用 `yama_solver_spectrum` 工具或 `tuan yama solver-spectrum` 查询 `YrBlockLinearTrace`，必须给出 `InnerIterations / InitialResidual / FinalResidual / MaxResidual / SolutionDiscrepancy / Converged / UsedFallback / ResidualMonotonic`，不要只回「已启用」。
9. 核对双判据：残差达标 **且** 解漂移 ≤ 1e-3 才采用迭代解，否则整组退回 Direct；病态 K（cond≈3.16e4）上块 Jacobi / GS / PCG 都应如实记录为不收敛或漂移超标并退回，**不要**包装成「迭代更快更好」。
10. 报告里写明诚实边界：全局块是投影 Jacobi + 圆锥投影，不是精确 LCP/SOCP；阻尼是求解正则化不是物理；K 帧内固定、不随姿态更新；不同 (A,B) 对按组独立求解；残差变小 ≠ 解变准；迭代谱系不替代生产路径的直接 Cholesky 解。

约束：不把「开关存在」当作「耦合生效」；`CoupledSteps==0` 的帧必须如实标注无接触或 K 对角，不虚报触发。不删断言、不放宽容差、不宣称已达 SOTA。
