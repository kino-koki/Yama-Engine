# Code - OSS 最轻量新版本集成 + Tuan-CLI 被调用能力优化 · 详细开发计划

日期：2026-11-28　基线：`df15cd5cb275`（第十八轮，main/develop 双绿）
口径：不停工、不逐轮问继续，但绝不谎报达标；「全部自研」= 引擎功能面不借鉴其他开源引擎。

## 0. 先查证，再定口径（避免「升级版本号」式假交付）

联网查证（2026-11-28）：VS Code / Code - OSS 当前最新稳定 tag 是
**1.148.0，2026-11-24 发布**，对应 Electron 43.8.10 / Node 24.22.1 /
Chromium 150.0.7871.250；1.147.0 是 11-17、1.146.0 是 11-10。
本仓库 `ide/codeoss/UPSTREAM.lock.json` 已精确锁定 `1.148.0` @
`661626d0e72f97806550790a98fb87116c9db825`。

**因此本轮没有「升到更新版本」可做**。用户要的「最轻量级的新版本的开源版本」
= 把**已经是最新稳定版**的 1.148.0，从此前「完整 Electron workbench 编译」
口径，切换为**最轻量 web-only 干净 MIT 构建面**，并让这个口径在 lock、
product.json、CI 模板、文档、C# 断言、Tuan 动作里**处处可判定**。

「最轻量」的明确定义（全部写进 `MINIMAL.lock.json`）：

| 去掉的重量 | 判定字段 |
|---|---|
| Electron 桌面壳 / 打包 | `noElectron=true`，`build_target="web"` |
| 原生模块重编译 | `noNativeModules=true` |
| Marketplace gallery / 扩展下载 | `noMarketplace=true` / `noBuiltinExtensionDownload=true` |
| 遥测 / 更新服务 | `noTelemetry=true` / `noUpdateService=true` |
| 微软品牌 | `noBranding=true` |

**诚实边界（本轮最重要的约束）**：本环境不编译 1.8 GB 上游源码，
`compile_in_this_repo=false` 写进 product.json 与 lock，CI 模板只做离线
校验 + 轻量产物清单，**不宣称**「完整 workbench 已在 CI 编译验收」。
G4 仍未闭合。

## 1. 目标拆解

- **A. Code-OSS 最轻量集成**：新增 `MINIMAL.lock.json`（含 6 个轻量配置文件的
  SHA-256 摘要）+ `scripts/build_codeoss_minimal.py`（`--check` / `--plan` /
  `--emit-manifest`，纯 stdlib、离线、fail-closed）；改造 product.json、
  UPSTREAM.lock、.gitmodules、ci/codeoss-gate.yml、README、SPDX 清单。
- **B. Tuan 被调用能力**：现状 `mcp_server.py` 的 `tools/call` 只返回
  **意图字符串**（INTENTS 字典），不是真调用。本轮新增 `yama_bridge.py`
  （7 个真实只读动作：真跑校验脚本、真读契约、真跑 hook、真读差距文档）、
  `yama_actions.json`（静态动作注册表）、`yama_cli.py`（独立 CLI 入口）；
  MCP / 插件子命令 / 独立 CLI **三入口统一委派**到同一个 dispatcher。
- **C. 双实现门禁**：Python 侧（unittest + `build_codeoss_minimal.py --check`）
  与 C# 侧（12 项新反射断言 + 1 个新 `[Fact]`（第二十轮再加 12 项 + 1 个 `[Fact]`））用同一套 SHA-256 口径，
  任何一侧漂移都红灯。累计 **282 项**反射断言 + **15** 个 `[Fact]`（第二十一轮后）。

## 2. 阶段与里程碑（每阶段都有可判定验收，不靠口头汇报）

| 里程碑 | 输入 | 实际动作 | 产物 | 验收条件 |
|---|---|---|---|---|
| M0 现状冻结 | 第十八轮绿基线 | 读全 20 个 Tuan 文件 / 6 个 CodeOss 桥接文件 / 契约门禁 / CI | 本计划文档 | 基线 CI 双绿记录可回读 |
| M1 轻量锁 | 1.148.0 查证结论 | 建 MINIMAL.lock + 离线校验脚本 + 6 文件 SHA-256 | `ide/codeoss/MINIMAL.lock.json`, `scripts/build_codeoss_minimal.py` | `--check` 返回 `clean_mit_build=true` 且 `errors=[]`；digest 与 C# 逐位一致 |
| M2 配置面 | M1 | product.json 加 `yamaMinimal`（含 `compiledInThisRepo=false`）；UPSTREAM.lock / .gitmodules / ci gate / README / SPDX 同步 | 6 个改动文件 | ci gate 不再含 `npm ci` / `npm run compile`；C# 断言 |
| M3 被调用能力 | 现有 MCP/子命令 | 新增 bridge / actions 注册表 / CLI 入口；三入口真委派；契约 13/13；hook 26 项；skill 更新 | 10 个 Tuan 文件 | `python -m unittest discover` 全绿；`tools/call` 返回真实 `clean_mit_build` 而非意图字符串；`yama_cli.py capabilities` 可执行 |
| M4 CI 收口 | M1–M3 | 推 develop → 验双绿 → 快进 main/feat → 验 main CI | CI run id | Build/Test/Smoke 全 success，失败级注解 0，诊断残留 0 |

### 顺序与依赖

M0 → M1（`config_sha256` 必须在 M2 改完 6 个文件后重算，故实际落地顺序是
「先定稿 6 个文件 → 算摘要 → 写 MINIMAL.lock」）→ M2 → M3 → M4。
M3 的 `verify_codeoss_minimal` 动作依赖 M1 的脚本，故 M3 单测放在 M1 之后跑。

## 3. 文件级变更清单

### 新增（8）

| 文件 | 作用 |
|---|---|
| `ide/codeoss/MINIMAL.lock.json` | 最轻量 web-only 构建声明 + 6 文件 SHA-256 + 排除项 |
| `scripts/build_codeoss_minimal.py` | 离线校验 / 构建计划 / 轻量清单，纯 stdlib |
| `YamaEngine.Tests/CodeOssMinimalTests.cs` | 12 项 C# 反射断言（含 SHA-256 双实现比对） |
| `integrations/tuan_cli/yama_bridge.py` | 7 个真实只读动作的 dispatcher，fail-closed |
| `integrations/tuan_cli/yama_actions.json` | 静态动作注册表（Tuan 无需 import 即可发现） |
| `integrations/tuan_cli/yama_cli.py` | 独立 CLI 入口，Tuan 可 subprocess 直接调用 |
| `docs/CODEOSS_MINIMAL_TUAN_PLAN.md` | 本计划 |
| `docs/ROUND19_DELIVERY.md` | 交付与诚实边界 |

### 修改（14）

`ide/codeoss/product.json`（加 yamaMinimal）、`ide/codeoss/UPSTREAM.lock.json`、
`ide/codeoss/README.md`、`.gitmodules`、`ci/codeoss-gate.yml`、
`docs/THIRD_PARTY_SPX.md`、`docs/SOTA_GAP_ANALYSIS.md`（246→258）、
`docs/ROUND18_DELIVERY.md`（下一步诚实更新）、
`YamaEngine.Tests/RepoContractTests.cs`（10/10→13/13 + 新 `[Fact]`）、
`integrations/tuan_cli/contract.json`、`mcp_server.py`、`subcommand.py`、
`hooks/yama_gate.py`、`tests/test_tuan_integration.py`、`README.md`、
`skills/yama-build/SKILL.md`、`skills/yama-coupled-mass/SKILL.md`。

## 4. 验收矩阵

| 层 | 命令 / 断言 | 期望 |
|---|---|---|
| 离线轻量校验 | `python scripts/build_codeoss_minimal.py --check` | `clean_mit_build=true`, `errors=[]`, 6 个 digest 全匹配 |
| 构建计划 | `python scripts/build_codeoss_minimal.py --plan` | 输出 web-only 步骤，明确标注「本仓库未编译」 |
| Python 适配层 | `python -m unittest discover -s integrations/tuan_cli/tests -v` | 全绿，含 MCP round-trip / CLI / bridge / hook 26 项 |
| Hook | `python integrations/tuan_cli/hooks/yama_gate.py --event Stop </dev/null` | `decision=allow`, `checked=22`, `missing=[]` |
| MCP 真调用 | `tools/call yama_codeoss_minimal` | `delegated=true` 且 `result.clean_mit_build=true`（不是意图字符串） |
| CLI 真调用 | `python integrations/tuan_cli/yama_cli.py capabilities` | 返回真实 13/13 工具与子命令清单 |
| C# 门禁 | `RepoContractTests` + `CodeOssMinimalTests` | 13 个 `[Fact]` + 12 项反射断言全过 |
| CI | develop + main | Build / Test / Smoke 全 success |

## 5. 风险与处置（不掩盖）

1. **上游 1.8 GB 未编译**：`compile_in_this_repo=false` 是硬声明，CI 模板
   只做离线校验；不上传不存在的 workbench 产物。要真编译需带 Node 24 的机器
   与网络，属 G4，未闭合。
2. **token scope 仅 `repo`**：`.github/workflows/` 写入会被拒，故轻量门禁
   仍放 `ci/codeoss-gate.yml` 模板，网页端人工移动后才启用。本轮不谎报已启用。
3. **SHA-256 自引用**：`MINIMAL.lock.json` 自身不进摘要集合，避免鸡生蛋；
   其余 6 个文件的摘要在全部定稿后一次性算出。
4. **Tuan-CLI 仓库零写入**：沿用其 `TUAN.md`「push 必须每次明确确认 /
   历史同意不代表授权」。本轮只改 Yama 侧，三入口都是 Yama 仓库内可独立运行的
   产物，Tuan 侧按需接入。
5. **旧契约数字硬编码**：`RepoContractTests.cs` 的 `Assert.Equal(10, tools)`
   与单测的 `len(tools)==10` 必须同一次提交改到 13/13——第十七轮就是漏了
   这一处而红灯，本轮列为 M3 的显式检查项。

## 6. 完成后的下一步（继续推进，不停）

1. 在带 Node 24 / 网络的机器上按 `--plan` 真编译 web-only workbench，
   把 `compiledInThisRepo` 翻成 true 并上传真实产物（G4 才可能闭合）。
2. 网页端把 `ci/codeoss-gate.yml` 移入 `.github/workflows/` 启用轻量门禁。
3. 按 `TUAN.md` 逐次授权后把适配层合并进 Tuan-CLI 仓库，并接上真实
   `tools/call` 回读（本轮已把 Yama 侧入口全部备好）。
4. 迭代求解器：零空间投影 / 冗余接触剔除预条件（降 K 条件数）、精确锥互补。
5. 真机 GPU / 规模 / 平台 SDK / 外部验证（G4–G8，需硬件与团队）。


## 7. M5 完善轮（第二十轮，2026-11-28）——把三个真实缺口补齐

第十九轮交付后有三处「说了但没真正落地」的地方，本轮逐条修掉，
**不靠放宽断言、不靠改文档措辞糊过去**：

| 缺口 | 第十九轮状态 | 第二十轮真实修复 | 验收 |
|---|---|---|---|
| 口径漂移 | `scripts/fetch_codeoss.py` 的 steps 仍写 `npm ci && npm run compile` / "产物为干净 MIT workbench"，与最轻量 web-only 矛盾 | 改成 web-only 步骤 + "本仓库不编译"；`build_codeoss_minimal.py --check` 新增**反回漂断言**（fetch 再出现编译宣称就红灯） | `TestR20FetchScriptNoElectronCompileClaim` / `TestR20MinimalCheckGuardsAgainstFetchDrift` |
| Python 门禁没真跑 | `--check` 与 unittest 只在本地合并视图验证；`ci/codeoss-gate.yml` 是 `ci/` 下模板 | **实测**：token 仅 `repo` scope，写 `.github/workflows/` 返回 404（对照实验见交付文档），做不到。改为把 Python 校验语义**完整双实现进 C# xUnit**（`build.yml` 的 Test step 每次真实执行），并把「模板未启用」锁成断言；模板保留待网页端人工启用 | `TestR20GateTemplateIsHonestAndCsharpEquivalentExists` / `TestR20CsharpGateCoversPythonBehaviour` / 契约 `ci_gate_enabled=false` |
| 引擎侧没接线 | 只加了 C# 测试去读磁盘上的 Python 脚本；`YamaCodeOssProduct` 不解析 `yamaMinimal`，manifest 与 bridge 仍只有 7 个命令 | Product 解析 9 项 minimal 声明并给出 `IsMinimalWebBuild`；manifest 7→**10** 命令；bridge 路由 10 命令；R15 硬编码 7→10 同步 | 新增 `CodeOssMinimalBridgeTests.cs` 10 项 + 1 个程序集级 `[Fact]` |

顺带修掉一处小的不诚实：`--plan` 原来写死了 `npm run compile-web`，
但 1.148.0 的实际脚本名本环境**未核实**，现改为"按该 tag 上游构建文档执行、
本仓库未核实脚本名、不写死"。

### M5 验收矩阵（追加）

| 层 | 命令 / 断言 | 期望 |
|---|---|---|
| 真实 CI | `build.yml` 的 Test step（Windows .NET） | C# 等价门禁 24 项 R19/R20 断言 + 14 个 `[Fact]` 全过；Python 模板**未启用**如实标记 |
| 反回漂 | `python scripts/build_codeoss_minimal.py --check` | `errors=[]`；fetch 脚本若写回 `npm run compile` 即返回非 0 |
| C# 桥接 | `CodeOssMinimalBridgeTests` 10 项 + `RepoContractTests` 新 `[Fact]` | 全过；`Commands.Length==10`、`IsMinimalWebBuild=true` |
| 累计 | 反射断言 / `[Fact]` | **270 / 14**；hook 核查 **28** 项 |

### M5 之后仍未闭合的（不谎报）

- 上游 1.8 GB web-only workbench **仍未编译**，G4 仍未闭合；
  `compiledInThisRepo=false` 依旧是硬声明。
- **Python 门禁模板仍未在 CI 启用**：本环境 token 无 `workflow` scope，
  实测写入被 404 拒绝；需网页端人工移入 `.github/workflows/`。
  本轮不谎报已启用，只保证等价语义由 C# xUnit 每次真实执行。
- **未向 `kino-koki/Tuan-CLI` 仓库写入任何文件**，仍按 `TUAN.md` 逐次确认。
- 真机 GPU / 规模 / 平台 SDK / 生态 / 外部验证（G4–G8）本环境物理上不具备。


## 8. M6（第二十一轮，2026-11-28）——YAMA 主语言面收口

起因：用户质疑「Code - OSS 会不会太大」，提出裁剪为 YAMA 主语言、
强制用其开发游戏、但保留部分语言且可模块化拆卸。

**结论**：大的是上游（1.3 GB 仓库 / 528 MB 未压缩 web 包 / 4核9GB 构建机 /
数百种语言 / 2000+ 命令），本仓库从未 vendoring；真正该裁的是默认全量
语言面。L3 物理裁剪需 Node 24 + 9GB + 网络，本环境写 workflow 实测 404、
无 submodule 内容，**做不到且不谎报**。本轮只做 L2 能力面收口。

| 项 | 落地 | 验收 |
|---|---|---|
| 语言面契约 | `ide/codeoss/language-profile.json`：yama 强制、csharp/json 系统级保留（不作写游戏语言）、7 个可插拔模块默认零内置、禁止内置全量语言包 | `YamaLanguageProfileTests` 12 项 + 1 个 `[Fact]` |
| 强制使用 YAMA | 3 个模板补 `primaryLanguage=yama`；`scripts/check_yama_project.py` 离线 fail-closed；两个 `--check` 脚本与 CI 断言 | 篡改 enforced / installed / strict / `.tscn` 入口均红灯 |
| 模块化拆卸 | `contributes.languages` 只生成 yama/csharp/json；`IsWellFormed` 反向拒绝可选模块 id；`allowOptionalModules` 默认 false | `TestR21ManifestLanguagesExcludeOptionalModules` |
| 引擎侧接线 | 新增 `YamaLanguageProfile.cs`；命令 10→12；Product 解析 `yamaLanguageProfile` 段 | `TestR21ExtensionManifestHasTwelveCommandsAndYamaLanguage` 等 |

同步：摘要 6→7 文件、hook 28→30、反射断言 270→282、`[Fact]` 14→15。
详细设计与诚实边界见 `docs/YAMA_LANGUAGE_PROFILE.md` 与
`docs/ROUND21_DELIVERY.md`。
