# 第十七轮交付（连续推进第四轮）

## 实质增量

1. **跨接触点 3m×3m 全局块耦合求解**接入 `YrContactSolver` /
   `YrPhysicsPipeline`（默认关闭 → 222 项旧断言与位级快照逐位零回归）。
   新文件 `YrContactBlockMatrix.cs`：全局组装 / 对称化 / 阻尼 Cholesky /
   前代回代 / 跨块观测量。
2. **12 项新物理断言**：默认关闭零回归、全局 K 对称且有跨点耦合、
   对角块与单点 `BuildK` 逐位 parity、Cholesky 往返、奇异矩阵退回、
   on/off 可判定差异、线动量/总角动量守恒、库仑锥、法向速度收敛、
   trace 逐位确定、奇异上下文安全退回、三开关联动。
3. **三开关联动验证**闭合第十六轮待办：同时开启不冲突、不崩、快照不变。
4. **Tuan 侧**：5 skills / 9 MCP 工具 / 9 子命令 / hook 契约 / unittest 同步，
   仍未写 Tuan-CLI 仓库。

## CI 红灯真修留痕

首次推 develop：Build 全绿、Test 红灯。定位为 `RepoContractTests.cs` 的
`Assert.Equal(8, tools)` 未随 contract.json 的 9 个 MCP 工具同步。真修是
同步数字 + 补 `subcommands` 数量断言（门禁从 2 维增强到 3 维），
**没有**删断言、放宽或改扫描范围。

## 真修过的数值坑（写进文档，不藏）

半正定 K 对 float32 构造顺序的 ~1ulp 不对称极敏感，会让 Cholesky 主元假性
变负。修法是组装后对称化 + 可调 Tikhonov 阻尼（只加主元、不写回 K）。
没有删断言、没有放宽容差、没有改扫描范围。

## 不能翻转的

- **不宣称已达 SOTA**：G4–G8 未闭合；Code - OSS Electron 完整构建未在现有 CI 编译。
- 不宣称零第三方；不宣称精确 LCP/SOCP；不宣称任意 MP3 位级一致。
- 不改 sln/CMake/NuGet/csproj、不动渲染管线、不删断言、不放宽容差。

## 下一步（继续推进，不停）

1. 跨接触点的块 Gauss-Seidel / 预条件共轭梯度（与 Jacobi 收敛性对比）。
2. 摩擦锥从二维圆锥投影升级为精确锥互补（仍是自研、默认关闭、零回归）。
3. 带 Node 20 的专用 job 编译 Code - OSS 1.148.0 干净 MIT workbench 并验收。
4. 按 `TUAN.md` 逐次授权后把适配层合并进 Tuan-CLI 仓库。
5. 扩音频夹具、Rust 原生内核编译加载、真机 GPU 帧循环（需硬件/SDK）。
