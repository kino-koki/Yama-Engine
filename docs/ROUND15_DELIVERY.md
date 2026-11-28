# 第十五轮交付（已推 main）

## 用户口径

「全部自研」= 引擎功能面代码不借鉴其他开源引擎；「持续推进直到 SOTA」=
不停工、不逐轮问「继续」，但**不谎报达标**。本轮为连续推进的第二轮实质增量。

## 四件实质工作

1. **3×3 耦合块求解真实接入管线**：`YrContactSolver.UseCoupledMassMatrix`
   + `SolveCoupled` + `YrPhysicsPipeline` 转发 + `YrCoupledSolveTrace` 可观测。
   默认关闭 → 190 项旧断言与位级快照逐位零回归；开启后保留 K 非对角耦合。
2. **可判定退化与守恒**：对角 K 逐位 parity、奇异上下文安全 fallback、
   线动量/总角动量逐位守恒、库仑锥、法向速度收敛、trace 逐位确定、
   `LastK` 与独立构造逐位一致（14 项物理断言）。
3. **Code - OSS 自研协议面扩展**：LSP didChange/completion/definition、
   DAP 异常断点/求值、第 7 个扩展命令与路由（6 项桥接断言）。
4. **Tuan-CLI 融合加深**：5 skills / 8 MCP 工具 / 8 子命令 / hook 契约项，
   全部 Yama 侧，未写 Tuan-CLI 仓库（遵循 `TUAN.md`）。

## 修复与稳定性

- 第十四轮 main 偶发红已通过多锚点仓库根定位加固，develop + main 双 CI 全绿。
- 第十五轮继续默认关闭策略，**不改任何既有断言**、不改 sln/CMake/NuGet/csproj。

## 不能翻转的

- **不宣称已达 SOTA**：G4–G8 未闭合；Code - OSS Electron 完整构建未在现有 CI 编译。
- 不宣称零第三方；不宣称跨接触全局 LCP；不宣称任意 MP3 位级一致。

## 下一步（继续推进，不停）

1. 把 `ReduceByCoupledMass` 接进 `YrNarrowPhase` 的流形缩减路径。
2. 跨接触点的块 Jacobi / Gauss-Seidel 全局求解（仍自研、默认关闭、零回归）。
3. 带 Node 20 的专用 job 编译 Code - OSS 1.148.0 干净 MIT workbench 并验收。
4. 按 `TUAN.md` 逐次授权后把适配层合并进 Tuan-CLI 仓库。
5. 扩音频夹具、Rust 原生内核编译加载、真机 GPU 帧循环（需硬件/SDK）。
