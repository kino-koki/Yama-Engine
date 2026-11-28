# 第十九轮交付：Code - OSS 最轻量新版本集成 + Tuan-CLI 真实被调用能力

基线 `df15cd5cb275`（第十八轮双绿）｜详细计划见 `docs/CODEOSS_MINIMAL_TUAN_PLAN.md`

## 1. 先说查证结论：没有更新的稳定版可升

联网查证（2026-11-28）：Code - OSS 当前最新稳定 tag 是 **1.148.0**
（2026-11-24 发布，Electron 43.8.10 / Node 24.22.1）；1.147.0 是 11-17。
本仓库第十四轮起已精确锁定 1.148.0 @
`661626d0e72f97806550790a98fb87116c9db825`。

所以本轮**没有改版本号假装升级**，而是把 1.148.0 从「完整 Electron
workbench 编译」口径切成**最轻量 web-only 干净 MIT 构建面**，并把这个口径
在 lock / product.json / CI 模板 / 文档 / C# 断言 / Tuan 动作里全部做成
可判定事实。

## 2. Code - OSS 最轻量构建面（真实增量）

- 新增 `ide/codeoss/MINIMAL.lock.json`：`profile=web-only-minimal`、
  `build_target=web`、8 项排除（Electron / 原生模块 / Marketplace / 遥测 /
  品牌 / 内置扩展下载 / 更新服务 / gallery）、**`compile_in_this_repo=false`**、
  以及 6 个轻量配置文件的 **SHA-256 摘要**。
- 新增 `scripts/build_codeoss_minimal.py`：纯 stdlib、零 Node / 零 Electron /
  零网络；`--check` 离线校验（fail-closed）、`--plan` 输出 web-only 手动步骤、
  `--emit-manifest` 产出轻量构建清单。
- `product.json` 加 `yamaMinimal` 段（含 `compiledInThisRepo=false`）；
  `UPSTREAM.lock.json` / `.gitmodules` / `ci/codeoss-gate.yml` /
  `ide/codeoss/README.md` / `docs/THIRD_PARTY_SPX.md` 同步。
- `ci/codeoss-gate.yml` 从「npm ci + npm run compile 完整 Electron」改成
  **零 Node 依赖的离线校验门禁**，只上传轻量清单，不上传不存在的
  workbench 产物。

**诚实边界**：本仓库**不编译** 1.8 GB 上游源码；不宣称完整 Electron
workbench 已在 CI 编译验收；不分发官方 VS Code 二进制或商标；G4 仍未闭合。
这条声明被写进 product.json、MINIMAL.lock、CI 模板注释和 C# 断言。

## 3. Tuan-CLI 协同 / 被调用能力（从「意图字符串」到「真调用」）

上一版 `mcp_server.py` 的 `tools/call` 只返回 `INTENTS` 字典里的字符串，
**不是真调用**。本轮：

- 新增 `yama_bridge.py`：7 个真实只读动作（真跑
  `build_codeoss_minimal.py --check`、真读 `contract.json`、真读
  `MINIMAL.lock.json + product.json`、真读 SOTA 差距文档、真跑
  `yama_gate.py`、命令意图表），统一 `invoke()` dispatcher，fail-closed、
  注册表一致性自检。
- 新增 `yama_actions.json`（静态动作注册表，Tuan 无需 import 即可发现）、
  `yama_cli.py`（独立 CLI 入口，Tuan 可 subprocess 直调）。
- **三入口统一委派**：MCP `tools/call`、插件 `subcommand.handle()`、
  独立 `yama_cli.py`；`tools/call` 返回 `delegated=true` +
  结构化 `result`（含真实 `clean_mit_build`），不再只有意图字符串。
- 契约 10/10 → **13 工具 / 13 子命令 / 7 动作**；hook 核查 18 → **22 项**；
  `RepoContractTests.cs` 的硬编码数字**同一次提交**同步（第十七轮踩过的坑）；
  5 个 skills 不变，2 个 skill 补被调用能力步骤。

**未向 `kino-koki/Tuan-CLI` 仓库写入任何文件**，沿用其 `TUAN.md`
「push 必须每次明确确认 / 历史同意不代表授权」规则。

## 4. 门禁

- Python：`build_codeoss_minimal.py --check` + `python -m unittest discover`
  全绿（含 MCP round-trip、CLI 直调、bridge 一致性、hook 26 项、
  6 文件 SHA-256 比对）。
- C#：新增 `CodeOssMinimalTests.cs` **12 项**反射断言 +
  `RepoContractTests` 新增 1 个 `[Fact]`；累计 **258 项**反射断言 +
  5 个音频用例 + **13** 个 `[Fact]`。
- 双实现同口径：Python 与 C# 都先做 `
 → 
` 行尾归一化，再算
  `sha256` / `Convert.ToHexString(SHA256.HashData(...))` 逐位一致。
  **这是一次真实红灯修复**：首次推 develop 时 Windows runner 的
  `core.autocrlf=true` 把 6 个文本文件 checkout 成 CRLF，两处 C# SHA-256
  断言全部失配（Build 成功、Test 红）。本地用 CRLF 视图复现后改成归一化口径，
  LF / CRLF 双视图均 `--check` 通过，不是删断言或放宽容差。

## 5. 不能翻转的

- 不宣称已达 SOTA：G0–G3 已闭合，G4–G8 需真机 GPU / 规模 / 平台 SDK /
  生态 / 外部验证，本环境物理上不具备。
- 不宣称零第三方；不宣称精确 LCP/SOCP；不宣称迭代谱系替代直接解。
- `ci/codeoss-gate.yml` 第十九轮时仍在 `ci/` 下（当时未启用，本轮如实记录）。
  **第二十轮实测确认该写入会被 404 拒绝（token 仅 `repo` scope），
  故仍保持模板状态**；但同一套 Python 校验语义已双实现进 C# xUnit，
  由现有 Test step 每次真实执行。见 `docs/ROUND20_DELIVERY.md`。
- 不删断言、不塞隔离清单、不放宽容差、不吞异常。

## 6. 下一步

带 Node 24 / 网络的机器上按 `--plan` 真编译 web-only 产物（G4 才可能闭合）、
网页端启用轻量门禁、按 `TUAN.md` 逐次授权后合并适配层进 Tuan-CLI、
零空间投影预条件、精确锥互补、真机 GPU 帧循环。
