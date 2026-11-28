# Yama-Engine ↔ Tuan-CLI 深度融合（Yama 侧适配层）

> 目标：把 Yama 的构建 / 回归 / 物理守恒 / SOTA 审计挂进 Tuan-CLI 的
> **Agent 工作流**（skills / hooks / MCP / 子命令 / CI 状态回读）。
> 本轮**只改 Yama-Engine 仓库**，不向 Tuan-CLI 写入任何文件。

## 为什么先只改这一侧

`kino-koki/Tuan-CLI` 的 `TUAN.md`（用户自定项目法）规定：
「commit / push / reset / rebase 必须用户明确要求才做，且每次都要确认」
「历史同意不代表授权」。因此跨仓库写入需要逐次授权；本轮采用零授权风险的
**Yama 侧适配层**，后续若要合并进 Tuan-CLI 仓库，再按该规则逐次确认。

## 五层融合

| 层 | 文件 | 挂接方式 |
|---|---|---|
| Skills | `skills/*/SKILL.md` ×5 | Tuan 内置技能格式（YAML frontmatter + ≥3 步正文），auto 激活；`yama-build` / `yama-test` / `yama-physics-gate` / `yama-sota-audit` / `yama-coupled-mass` |
| Hooks | `hooks/yama_hooks.yaml` + `yama_gate.py` | `PostToolUse` / `Stop`（Tuan `CORE_HOOK_EVENTS` 实名），stdin/stdout JSON 决策，纯 stdlib、不依赖 .NET |
| MCP Server | `mcp_server.py` | 自研 JSON-RPC 2.0 over stdio（对齐 Tuan 零依赖客户端）：`initialize` / `tools/list` / `tools/call`，13 个工具（真实委派，只读/fail-closed） |
| 子命令 | `subcommand.py` | `tuan yama build\|...\|codeoss-minimal\|invoke\|capabilities`，三入口统一委派到 `yama_bridge.invoke()`，不执行写操作 |
| 被调用能力 | `yama_bridge.py` + `yama_actions.json` + `yama_cli.py` | 7 个真实只读动作（真跑离线校验 / 真读契约 / 真跑 hook / 真读差距文档）；MCP `tools/call`、插件 `handle()`、独立 CLI 三入口统一委派，fail-closed |
| CI 状态回读 | `contract.json` + `RepoContractTests` | Yama 每次 CI 都校验适配层文件完整、skill frontmatter 合法、Code - OSS lock 精确 |

## 协议来源（署名）

- Tuan-CLI skill / hook / MCP 格式参考：https://github.com/kino-koki/Tuan-CLI （MIT）
  仅参考**协议格式与事件名**，代码全部本项目自研重写，未拷贝其实现。
- LSP 3.17 / DAP 1.x / JSON-RPC 2.0 为公开协议规范。

## 真实 CI 验证（第二十轮）

本环境 token 无 `workflow` scope，写 `.github/workflows/` 实测 404，
故 `ci/codeoss-gate.yml` 仍是待网页端启用的模板（契约
`ci_gate_enabled=false` 如实标记）。但下面这些本地命令的**语义**已由
`YamaEngine.Tests/CodeOssMinimalTests.cs` 与 `CodeOssMinimalBridgeTests.cs`
双实现进 C# xUnit，由现有 `build.yml` 的 Test step 每次真实执行——
MCP 真委派、CLI 直调、SHA-256、口径不回漂都在真实门禁里被校验。

## 本地验证（无需 .NET / 无需 Tuan 安装）

```bash
python -m unittest discover -s integrations/tuan_cli/tests -v
python integrations/tuan_cli/yama_cli.py capabilities       # 独立 CLI 真调用
python integrations/tuan_cli/yama_cli.py codeoss-minimal    # 真实离线轻量校验
python integrations/tuan_cli/yama_cli.py invoke --action sota_status
python integrations/tuan_cli/hooks/yama_gate.py --event Stop </dev/null
echo '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}' \
  | python integrations/tuan_cli/mcp_server.py
echo '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"yama_codeoss_minimal","arguments":{}}}' \
  | python integrations/tuan_cli/mcp_server.py   # 返回真实 clean_mit_build，不是意图字符串
```

## 约束

- 不宣称「已达 SOTA」：所有 skill / hook 输出都带 G4–G8 未闭合提示。
- Hook 不在 Agent 进程里偷偷跑 `dotnet`（避免硬依赖），只做静态契约核查；
  真机 GPU / 规模 / 平台门禁仍由 CI 与实机复核。
- 自研边界见 `docs/SELF_RESEARCH_BOUNDARY.md`：适配层代码 100% 自研，
  不借鉴其他开源引擎实现。


## YAMA 语言面（第二十一轮）

契约 `language_profile` 字段指向 `ide/codeoss/language-profile.json`：
主语言 `yama` 强制、7 个可插拔语言模块默认零内置、项目契约门禁
`scripts/check_yama_project.py`。引擎侧命令表由 10 扩到 12，
新增 `yama.languageProfile` / `yama.newGameProject`。


## YAMA 行为层（第二十二轮）

契约新增 `script_binding` 字段：`schemas/yama-script.schema.json` +
`YamaEngine/Scripting/Abi/YamaScriptDeclaration.cs`，`vm_implemented=false`
如实标注**不是 VM**。引擎侧命令表由 12 扩到 14，新增
`yama.scriptBinding` / `yama.newScript`。
