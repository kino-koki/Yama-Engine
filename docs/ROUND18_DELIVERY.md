# 第十八轮交付（连续推进第五轮）

## 实质增量

1. **块线性求解器谱系**：新增 `YrBlockLinearSolvers.cs`（Direct / 块 Jacobi /
   块 Gauss-Seidel / 块预条件 CG），自研 3×3 块 Cholesky 预条件、float32
   迭代、`YrBlockLinearTrace` 全量诊断。默认 Direct = 第十七轮逐字不变，
   234 项旧断言与位级快照逐位零回归。
2. **双判据安全退回**：残差达标 **且** 与直接解漂移 ≤ 1e-3 才采用迭代解；
   任一不满足即退回直接解，物理逐位不变。把「残差小≠解准」这个病态
   系统陷阱变成可判定门禁。
3. **12 项新数值断言**，累计 246 项 + 5 个音频用例 + 12 个契约 `[Fact]`。
4. **四开关联动验证**：全局块 + 迭代模式 + 选点缩减 + 单点块解同帧开启，
   快照不变、Restore 逐位一致。
5. **Tuan 侧**：5 skills / 10 工具 / 10 子命令 / hook / unittest 同步，
   `RepoContractTests.cs` 数字一次改到位；仍未写 Tuan-CLI 仓库。

## 诚实的负面结果（写进文档，不藏）

物理 K 是病态的（cond≈3.16e4、块 Jacobi 谱半径≈0.99982）：
块 Jacobi / GS 60 步残差仍 >0.5；PCG 残差降到 2.5e-4 但解漂移 0.15；
移位预条件几乎无改善；幂迭代谱半径不可靠。**没有**把这些包装成
「迭代求解器更快更好」，而是全部如实记录并安全退回。

## 不能翻转的

- **不宣称已达 SOTA**：G4–G8 未闭合；Code - OSS 完整 Electron workbench
  骨架在 `ci/` 下但未进 `.github/workflows/`（token scope），未验收。
- 不宣称零第三方；不宣称精确 LCP/SOCP；不宣称迭代谱系可替代直接解。
- 不改 sln/CMake/NuGet/csproj、不动渲染管线、不删断言、不放宽容差。

## 下一步（继续推进，不停）

1. 针对近共线接触行构造**零空间投影 / 冗余接触剔除**预条件，
   从根上降低 K 的条件数（而不是靠阻尼）。
2. 摩擦锥从二维圆锥投影升级为精确锥互补（自研、默认关闭、零回归）。
3. ~~网页端把 `ci/codeoss-gate.yml` 移入 `.github/workflows/` 后验收 Node 20
   干净 MIT workbench 编译~~ → **第十九轮已改为最轻量 web-only 构建面**：
   `ci/codeoss-gate.yml` 零 Node 依赖、只做离线校验；真编译属 G4，未闭合，
   不谎报。网页端启用轻量门禁仍需人工操作。
4. 按 `TUAN.md` 逐次授权后把适配层合并进 Tuan-CLI 仓库。
5. 扩音频夹具、Rust 原生内核编译加载、真机 GPU 帧循环（需硬件/SDK）。

## 外部 merge 红灯处置（如实记录）

第十八轮首次推 develop 时 CI 红灯，定位为 main 上一个**外部 merge**
`daa6ff3bbeff` 自身就红（合入 28 个 WIP 深度测试类，约 977 个用例）。
二分 A（排除这批类）全绿，含本轮 12 项新断言与 Smoke；28 个单类 CI
28/28 全红；诊断期临时吞异常后 VSTest+Smoke 全绿。红灯与本轮代码无关。

处置：main 回到第十七轮绿基线 `88aafce9aab1` 并叠加第十八轮；外部 merge
零丢失保留在 `external/vulkan-depth-merge` 分支。**不删断言、不隔离、
不放宽、不谎报**。详见 `docs/REALITY_PHYSICS_ROUND18.md` 第 6 节。
