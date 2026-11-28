# 真实物理内核 · 第十七轮：跨接触点全局块耦合求解（2026-11-28）

延续口径：不新增语言、不动渲染管线、不改 sln/CMake/NuGet/csproj、不宣称 SOTA。
「全部自研」= 引擎功能面代码不借鉴其他开源引擎。

## 1. 从「单点 3×3」到「多点 3m×3m」

第十五轮把 3×3 K 接进**单个接触点**的求解；第十六轮把 K⁻¹/² 白化行接进窄相选点。
本轮把**同一 (A,B) 对的 m 个接触点**组装成一个 3m×3m 对称矩阵一次求解，
保留「不同接触点经由共享刚体产生的冲量耦合」：

```
K_kl[α,β] = (invM_a+invM_b)(d_k^α·d_l^β)
          + (r_a^k×d_k^α)·I_a⁻¹(r_a^l×d_l^β)
          + (r_b^k×d_k^α)·I_b⁻¹(r_b^l×d_l^β)
```

- I⁻¹ 对称 ⇒ `K_lk = K_klᵀ`，整体对称；**对角块 K_kk 逐位等于第十四轮
  `YrContactMassMatrix3x3.BuildK`**（已写成 parity 断言）。
- 不同 (A,B) 对无共享刚体 ⇒ 跨对块为零，按对分组独立求解与整体求解等价；
  分组按 `(minA, maxA, 原序)` 定序，逐位确定。
- 新文件 `YrContactBlockMatrix.cs`：全局组装 + 一次**对称化** + 自研 n×n
  Cholesky + 前代/回代 + `MaxCrossBlockNorm` 观测量。

### 求解语义：投影 Jacobi，不是 SI 的 Gauss-Seidel

每次迭代先基于**同一时刻**速度算全部点的 rhs，统一解 `Δλ = L⁻¹L⁻ᵀrhs`，
逐点做 `λ_n ≥ 0` 累加钳制与二维库仑圆锥投影，**再统一施加**全部合成冲量。
这与 sequential impulse「逐点解完立刻施加」的 GS 语义不同，是本轮可判定差异的来源。

### 两个真修过的数值事实（推 CI 前 float32 预演发现）

1. **不对称会让 Cholesky 假性失败**：float32 构造顺序使 `K_kl[α,β]` 与
   `K_lk[β,α]` 差 ~4.8e-7；而两体重叠共面时 K 是**半正定**（最小特征值
   ~1.2e-8），这点误差足以让某主元变负。修法：组装后做一次上/下三角对称化。
2. **半正定需要正则化**：加可调 Tikhonov 阻尼（默认 `1e-3`，**只加在
   Cholesky 主元上，不写回 K**，因此不改 K 的物理含义、不破坏对角块 parity）。
   阻尼设为 0 可让全零质量上下文强制走安全退回路径（已写成断言）。

### 安全退化与优先级

- `UseGlobalBlockSolve` 默认 **false**：`Solve` 循环体与第十六轮逐字相同，
  222 项旧断言与位级快照逐位零回归。
- 优先级：`UseGlobalBlockSolve` > `UseCoupledMassMatrix` > 默认 SI。
  三者同时开启时前者生效，后者被跳过（已在联动断言里验证「不冲突、不崩」）。
- Cholesky 非正定 ⇒ 整组退回原 SI 路径，绝不制造 NaN/Inf。
- `YrGlobalBlockTrace`（Groups / MaxPointsPerGroup / TotalIterations /
  SolvedGroups / FallbackGroups / MaxCrossBlock / LastUsedGlobal）是纯观测量，
  **不进 Snapshot**。

### 诚实边界

仍是投影 Jacobi + 互补钳制/圆锥投影，**不是精确 LCP/SOCP**；K 帧内固定、
不随姿态更新；摩擦锥为二维圆锥投影；阻尼是正则化不是物理；不同 (A,B) 对
之间不做跨对耦合（物理上块为零，但实现上按对分组）。

## 2. 三开关联动验证

`UseGlobalBlockSolve` + `UseCoupledMassReduction`（窄相选点）+ 
`UseCoupledMassMatrix`（单点块解）在同一帧同时开启：管线正常推进、
三个开关可回读、快照长度/内容不变、Restore 逐位一致、默认路径三个
trace 全为 0。第十六轮留下的「联动验证」待办本轮闭合。

## 3. Tuan-CLI 融合（仍只改 Yama 侧）

新增 MCP 工具 `yama_global_blocks` 与子命令 `tuan yama globalblocks`；
`contract.json` → 5 skills / **9** MCP 工具 / **9** 子命令；
skill 增补全局块求解核查步骤、hook 契约增加 `YrContactBlockMatrix.cs` 与
`UseGlobalBlockSolve` 检查、unittest 同步。**未向 Tuan-CLI 仓库写入**。

## 推 CI 前的 float32 预演（定标，不放宽阈值）

复刻两体各向异性惯量 + 两个同对接触点 `(1.3,0.7,0)` / `(0.4,-0.9,0)`：

| 量 | 实测 | 断言 |
|---|---|---|
| `MaxCrossBlockNorm` | 11.186 | > 1e-3 |
| 对角块 vs 单点 `BuildK` | 逐位一致 | `==` |
| 对称化后 Cholesky | 成功，minEig=0.001 | true |
| `(K+damp·I)x=b` 往返残差 | 2.8e-5 | < 1e-3 |
| 全局 on/off `MaxDiff` | 0.064 | > 1e-4 |
| 线动量 / 总角动量残差 | 4.8e-8 / 0 | < 1e-5 |
| 摩擦锥 `\|λ_t\| ≤ μ·λ_n` | 两点均在锥内 | true |
| 6 次迭代后 `\|vn\|` | ≈ 0 | < 1e-3 |

### 推 CI 后的红灯与真修（门禁没被放宽，反而被增强）

第十七轮首次推 develop 时 Build 全绿、Test step 红灯。本地按推送后状态
复现：`contract.json` 的 `mcp_tools` 从 8 增至 9（新增
`yama_global_blocks`），但
`YamaEngine.Tests/RepoContractTests.cs:204` 仍硬编码
`Assert.Equal(8, tools)`——契约门禁**正确**拦下了这次适配层数字漏同步。

真修三件事：

1. 硬编码同步到 `Assert.Equal(9, tools)`；
2. 顺手**增强**门禁：新增 `subcommands` 数量断言 `Assert.Equal(9, sub)`，
   让 skills / mcp_tools / subcommands 三个维度都在 CI 里可判定；
3. 把 `RepoContractTests.cs` 纳入交付 payload，并在推送脚本自洽断言里
   强制检查「9 工具 / 9 子命令」与断言文本同时存在。

没有删断言、没有放宽容差、没有改扫描范围。这条红灯再次说明：数字同步
漏一处，机器就会拦一次，比人工核对可靠。

## 回归门禁

十七轮累计 **234 项**反射式断言（222 + 本轮 12）+ 5 个音频用例，
另有 `RepoContractTests` 的 12 个 xunit `[Fact]` 契约门禁。
无 Skip、无吞异常、无阈值放宽；诊断/凭据残留 0。

## 不能宣称的（不变且不缩小）

G0–G3 已闭合；G4–G8（真机 GPU / 规模 / 平台 SDK / 生态 / 外部验证）
本环境物理上不具备。Code - OSS 完整 Electron workbench 未在现有 CI 编译。
**不宣称已达 SOTA。**
