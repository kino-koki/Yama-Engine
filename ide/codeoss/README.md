# 内置 Code - OSS 1.148.0（最新稳定版 · 最轻量 web-only · 干净 MIT）

## 这是什么

`microsoft/vscode` 仓库即 **Code - OSS**，MIT 许可。联网查证（2026-11-28）：
当前最新稳定 tag 为 **1.148.0**（2026-11-24 发布），对应 commit
`661626d0e72f97806550790a98fb87116c9db825`。**没有更新的稳定 tag 可升**，
所以本轮把「最轻量」做成可判定事实，而不是改个版本号说升级了。

本仓库以 **submodule gitlink** 锁定该精确 commit，并提供：

| 文件 | 作用 |
|---|---|
| `language-profile.json` | **YAMA 主语言面契约**：主语言强制 / 系统级保留语言 / 7 个可插拔模块默认零内置 |
| `UPSTREAM.lock.json` | 上游 tag / commit / MIT 许可 / 最新稳定版查证日 |
| `MINIMAL.lock.json` | **最轻量 web-only 构建声明**：排除项 + 9 个轻量配置文件的 SHA-256 + `compile_in_this_repo=false` |
| `product.json` | 去品牌 / 去遥测 / 无 Marketplace / 无内置扩展的干净 MIT 覆盖，含 `yamaMinimal` 段 |
| `scripts/fetch_codeoss.py` | stdlib 一键拉取、校验 commit、应用 product.json |
| `scripts/build_codeoss_minimal.py` | **纯 stdlib 离线校验 / 构建计划 / 轻量清单**，零 Node、零 Electron、零网络、fail-closed |
| `ci/codeoss-gate.yml` | 轻量门禁模板（只做离线校验，不编译上游） |
| `YamaEngine/Editor/CodeOss/*.cs` | **自研** LSP/DAP 客户端、扩展清单、命令桥接 |

## 「最轻量」怎么理解（机器可判定）

`MINIMAL.lock.json` 明确排除 8 项重量：Electron 桌面壳、原生模块重编译、
Marketplace gallery、遥测、微软品牌、内置扩展下载、更新服务、扩展市场端点。
构建目标 `build_target="web"`。

**诚实边界（最重要）**：本环境不编译 1.8 GB 上游源码。
`compile_in_this_repo=false` 同时写进 `product.json` 的 `yamaMinimal` 与
`MINIMAL.lock.json`；CI 模板只做离线校验并上传**轻量构建清单**（不是
workbench 产物）。**不宣称**完整 Electron workbench 已在 CI 编译验收，
G4 仍未闭合。C# 的 `CodeOssMinimalTests` 会把这条声明锁成断言。

## 许可边界（必须遵守）

- GitHub `microsoft/vscode` 仓库 = **MIT**（Code - OSS）。
- 微软官方下载的 Visual Studio Code = 构建时替换 `product.json`
  （品牌 / 遥测 / 扩展市场）+ **微软产品许可**，**不是 MIT**。
- 本仓库只做干净 MIT 构建，不得分发官方 VS Code 二进制、图标或商标。
- 逐项 SPDX 署名见 `docs/THIRD_PARTY_SPX.md`。

## 与「全部自研」的关系

Code - OSS 是 **IDE 外壳 / 基础设施**，不是游戏引擎。引擎相连的所有桥接
代码 100% 自研（LSP 帧、DAP 帧、扩展清单、命令路由、轻量校验脚本、
Tuan 适配层均为本项目编写），未拷贝 VSCodium 脚本或任何编辑器插件实现。

## 本地使用（无需 Node / 无需 .NET / 无需网络）

```bash
python scripts/fetch_codeoss.py --check                  # 上游锁 + 干净 MIT + 语言面
python scripts/check_yama_project.py --check            # YAMA 主语言强制 + 3 个模板项目契约
python scripts/build_codeoss_minimal.py --check         # 最轻量锁 + 9 文件 SHA-256
python scripts/build_codeoss_minimal.py --plan          # web-only 最小构建步骤（标注未编译）
python scripts/build_codeoss_minimal.py --emit-manifest # 轻量构建清单 JSON
```

真要编译 web-only workbench，需要带 Node 24 与网络的机器，按 `--plan`
输出的步骤手动执行；本仓库不代做、不谎报。

## CI 说明

**第二十轮实测**：本环境 token 仅 `repo` scope，写 `.github/workflows/`
返回 404，因此 `ci/codeoss-gate.yml` **仍是模板、仍需网页端人工移入**
`.github/workflows/` 才启用，不谎报已启用。同一套 Python 校验语义
（`fetch_codeoss.py --check` / `build_codeoss_minimal.py --check` /
`--emit-manifest` / Tuan unittest / 独立 CLI / 自研边界审计）已由
`CodeOssMinimalTests.cs` 与 `CodeOssMinimalBridgeTests.cs` 双实现进 C# xUnit，
由现有 `build.yml` 的 Test step **每次真实执行**。契约
`ci_gate_enabled=false` 如实标记模板状态。


## YAMA 主语言面（第二十一轮）

回答「会不会太大」：**大的是上游**（1.3 GB 仓库 / 528 MB 未压缩 web 包 /
4 核 9 GB 构建机 / 数百种语言），本仓库从未 vendoring。本轮只做
**L2 能力面收口**：`language-profile.json` 声明 YAMA 为主语言且强制，
C# / JSON 作为系统级保留（不作写游戏语言），7 个可选语言模块默认不安装、
可拆卸；扩展清单只内置 yama/csharp/json。

「强制」只落在本仓库项目契约、离线脚本与 CI 断言三层，**不谎称已改
workbench 内核或锁死 UI**。L3 物理裁剪需带 Node 24 / 9 GB / 网络的机器，
本环境做不到，如实标注未做。详见 `docs/YAMA_LANGUAGE_PROFILE.md`。


## YAMA 行为层（第二十二轮）

第二十一轮之后，「用 YAMA 开发游戏」还只能摆场景。本轮补上声明式脚本绑定：
`schemas/yama-script.schema.json` 定义声明格式，
`Templates/*/Content/Scripts/*.yama` 是示例，
`YamaEngine/Scripting/Abi/YamaScriptDeclaration.cs` 把声明真实还原成
已实现的 L2 绑定 ABI（`ScriptBinding` + `PropertyBag` + `NodeRef`）。
项目契约新增 `scriptBinding` 字段，由 `check_yama_project.py` 离线校验。

**不是 VM、不是 DSL 解释器**；`vmImplemented=false` 是硬声明。
同时更正了 `docs/SCRIPTING_ABI_SPEC.md` 的陈旧口径——L2 绑定 ABI
早已实现并在 CI 执行，缺的是 L1 的语言/VM。详见
`docs/ROUND22_DELIVERY.md`。
