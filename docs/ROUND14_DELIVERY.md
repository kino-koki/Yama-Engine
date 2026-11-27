# 第十四轮交付（已推 main）

## 用户口径澄清

「全部自研」= **引擎功能面代码不借鉴其他开源引擎**，不是零第三方依赖。
第三方基础设施允许，但必须锁定版本 + 逐项 SPDX 署名 + 不得拷实现冒充自研。

## 四件实质工作

1. **自研边界机器可判定化**：`docs/SELF_RESEARCH_BOUNDARY.md`、
   `docs/THIRD_PARTY_SPX.md`（13 个 PackageReference 全覆盖）、
   `scripts/audit_no_engine_copy.py`、CI 4 个新 `[Fact]` 门禁。
2. **内置 Code - OSS 1.148.0（最新 · 最轻量 · 完整 clean-MIT）**：
   submodule gitlink 精确锁定 `661626d0...`、去品牌/去遥测/无 Marketplace
   `product.json`、`UPSTREAM.lock.json`、stdlib `fetch_codeoss.py`、
   `ci/codeoss-gate.yml` 模板；**自研** LSP 3.17 / DAP 1.x / JSON-RPC 帧、
   扩展清单、命令桥接（`YamaEngine/Editor/CodeOss/` 6 个 .cs）。
3. **Tuan-CLI 深度融合（Yama 侧）**：4 个 skill、`PostToolUse`/`Stop` hooks、
   自研零依赖 MCP server（7 工具）、子命令适配、contract.json、
   stdlib unittest。**未向 Tuan-CLI 仓库写入**（遵循其 `TUAN.md` 逐次确认规则）。
4. **完整 3×3 接触有效质量矩阵**：自研 Cholesky / Jacobi / K^{-1/2} / 耦合 18D 行，
   对角退化 parity 与 `C·K·C=I` 白化恒等式可判定。

## CI 稳定性事件（诚实记录）

- develop `46021464790`（commit `e2719b361180`）**全绿** → 推 main。
- main 首次 run `46022019428` **Test step 偶发红**（无具体失败注解，仅 exit 1）。
- 同一 commit 的 `rerun-failed-jobs` attempt 2 **全绿**；注入失败落盘诊断的
  run `46022443962` 记录 `count=0`（零失败）。三者共同表明是 runner 偶发，
  非确定性回归。
- 加固：`RepoContractTests.FindRepoRoot` / `CodeOssBridgeTests.FindRoot`
  改为多锚点（`capability_matrix.csv` 或 `UPSTREAM.lock.json`）+ 双起点
  （`AppContext.BaseDirectory` / cwd）+ `YAMA_REPO_ROOT` 兜底，消除 testhost
  工作目录偶发差异这一可能性。加固后重新走 develop → main 双 CI 验证。
- **没有删断言、没有放宽容差、没有隔离用例。**

## 不能翻转的

- **不宣称已达 SOTA**：G4–G8 未闭合，Code - OSS Electron 完整构建未在现有 CI 编译。
- 不宣称零第三方；不宣称任意 MP3 位级一致；不宣称 Godot 全能力。
- 渲染管线未动、sln/CMake/NuGet/csproj 未改、不新增语言。

## 下一步（持续推进）

1. 把 `ReduceByCoupledMass` 接进 `YrContactSolver` 逐接触求解。
2. 在带 Node 20 的专用 job 编译 Code - OSS clean workbench 并截图验收。
3. 按 `TUAN.md` 逐次授权后把适配层合并进 Tuan-CLI 仓库。
4. 扩 MP3 夹具、Rust 原生内核编译加载、真机 GPU 帧循环。
