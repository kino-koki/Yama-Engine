# 第二十轮交付（完善轮）：把第十九轮的三个真实缺口补齐

基线 `e33e98f5dcf7`（第十九轮，main/develop 双绿）｜详细计划见
`docs/CODEOSS_MINIMAL_TUAN_PLAN.md` 第 7 节（M5）

## 为什么要这一轮

第十九轮把 Code - OSS 1.148.0 做成了「最轻量 web-only 干净 MIT 构建面」，
Tuan 侧也做成了真实被调用能力，但有**三处说了没真落地**的地方。这轮不
开新方向，专修这三处，而且修的是根因、不是改文档措辞糊过去。

## 1. 口径漂移（真实 bug）

`scripts/fetch_codeoss.py` 的 `steps` 仍输出：

```
cd ide/codeoss/upstream && npm ci && npm run compile
产物为干净 MIT workbench
```

这和第十九轮刚建立的「最轻量 web-only、本仓库不编译」直接矛盾。

**修复**：steps 改成指向 `build_codeoss_minimal.py --check` / `--plan`，
明确写 "本仓库不编译 1.8GB 上游 workbench，不宣称产物存在"。
更关键的是在 `build_codeoss_minimal.py --check` 里加**反回漂断言**：
fetch 脚本只要再出现 `npm ci && npm run compile` 或 `npm run compile`、
或丢掉「本仓库不编译」声明，`--check` 立刻返回非 0。以后谁把口径改回去，
CI 会拦下来。

顺带修一处小的不诚实：`--plan` 原来写死 `npm run compile-web`，但 1.148.0
的实际 web 构建脚本名本环境**未核实**。现在改成「按该 tag 上游构建文档执行、
本仓库未核实脚本名、不写死」。

## 2. Python 门禁：实测做不到写 workflow，改成真正可执行的 C# 双实现

第十九轮的 `--check` 和 unittest 只在本地合并视图跑过；`ci/codeoss-gate.yml`
是 `ci/` 下的模板，注释写着"请在网页端移到 `.github/workflows/` 启用"。
也就是说，那套 Python 门禁**从未在 CI 里真跑**。

**先做实测，不猜权限**：`GET /user` 返回 `X-OAuth-Scopes: repo`，没有
`workflow`。用 `POST /git/trees` 做对照实验：

| tree 内容 | 结果 |
|---|---|
| 仅 `.github/workflows/build.yml` | **404 Not Found** |
| 仅 `scripts/fetch_codeoss.py` | 201 Created |
| 其余 22 个文件（不含 workflow 路径） | 201 Created |

结论：**本环境确实写不进 workflow**，第十九轮文档里的判断成立。
（本轮草稿一度写成"实测 repo scope 足够"，发现 404 后立即全部更正，
没有把没做到的事写成交付。）

**因此改走真正能落地的路径**：不谎报"已启用"，而是把 Python 侧校验的
**语义完整双实现进 C# xUnit**——`build.yml` 的 Test step 每次真实执行：

- `CodeOssMinimalTests.cs`（第十九轮 12 项）：6 个配置文件 SHA-256、
  8 项排除、web-only 未编译声明、离线脚本 fail-closed、CI 模板不含完整编译。
- `CodeOssMinimalBridgeTests.cs`（本轮 12 项）：fetch 脚本口径不回漂、
  反回漂断言存在、模板 4 条步骤完整、`ci_gate_enabled=false` 如实标记、
  C# 等价门禁存在、MCP/子命令/CLI/dispatcher 真委派行为。
- `RepoContractTests` 新增 1 个程序集级 `[Fact]`。

契约新增 `ci_gate`（标注 `NOT enabled`）、`ci_gate_enabled=false`、
`csharp_equivalent_gate` 指向上面两个 C# 文件；`ci/codeoss-gate.yml`
头部注释补上 404 实测记录与"仍需网页端人工启用"。**模板仍保留**，
用户在网页端移入 `.github/workflows/` 后即可获得 Linux/Python 3.12 的
独立校验 job；在那之前，等价语义已由每次 CI 的 Test step 覆盖。

## 3. 引擎侧 C# 桥接真正接线（不是只有测试读脚本）

第十九轮新增的 C# 测试是「读磁盘上的 Python 脚本再做断言」，但
`YamaCodeOssProduct` / `YamaExtensionManifest` / `YamaCodeOssBridge`
三个真正的引擎侧类**仍不认识** `yamaMinimal`，命令表还是 7 个。

**修复**：

- `YamaCodeOssProduct`：解析 `yamaMinimal` 段（profile / buildTarget /
  compiledInThisRepo / 7 项排除），新增 `HasMinimalSection` 与
  `IsMinimalWebBuild`。**字段缺失不会被误判成绿**；把
  `compiledInThisRepo` 改成 true、或删掉任一排除项，都会让判定变 false。
  第十四轮的 6 项干净 MIT 校验保持独立、不回退。
- `YamaExtensionManifest.Commands` 7 → **10**：新增
  `yama.codeossMinimal` / `yama.invoke` / `yama.capabilities`。
- `YamaCodeOssBridge.Dispatch` 同步路由 10 个命令，新命令返回结构化
  只读意图（指向真实离线校验 / dispatcher / 契约）。
- `CodeOssBridgeFifteenthTests.cs` 里硬编码的 `Commands.Length == 7`
  **同一次提交**改成 10 并追加三条新命令断言——第十七轮就是漏了这种
  数字同步才红灯，本轮列为显式检查项。

## 4. 门禁与计数

- 新增 `YamaEngine.Tests/CodeOssMinimalBridgeTests.cs` **12 项**反射断言，
  `RepoContractTests.cs` 新增 **1 个程序集级 `[Fact]`**（直接实例化
  `YamaCodeOssProduct` / `YamaCodeOssBridge`，证明能力真编进被测程序集，
  不是只验证磁盘文本）。
- 累计 **270 项**反射断言 + 5 个音频用例 + **14** 个 `[Fact]`；
  hook 静态核查 26 → **28** 项；契约新增 `ci_gate` /
  `codeoss_bridge_commands=10`，单测数字同步。
- 6 个轻量配置文件的 SHA-256 在改动后**重新定标**（LF 归一化口径不变），
  Python `hashlib` 与 C# `Convert.ToHexString(SHA256.HashData(...))`
  逐位一致；LF / CRLF 双视图都通过。
- CI：`build.yml` 的 Build / Test / Smoke 全 success 才算收口；
  Python 模板**未启用**如实标记，不谎报第二个 job。

## 5. 不能翻转的（仍然不变）

- 上游 1.8 GB web-only workbench **仍未编译**；`compiledInThisRepo=false`
  依旧是写进 product.json / MINIMAL.lock / CI 注释 / C# 断言的硬声明。
- **Python 独立门禁模板仍未启用**：token 仅 `repo` scope，写 workflow
  实测 404；需网页端人工移入 `.github/workflows/`，本轮不谎报已启用。
- **G4–G8 未闭合，不宣称已达 SOTA**；不宣称零第三方；不宣称精确
  LCP/SOCP；不宣称迭代谱系替代直接解。
- **未向 `kino-koki/Tuan-CLI` 仓库写入任何文件**，仍按 `TUAN.md`
  「push 必须每次明确确认 / 历史同意不代表授权」执行。
- 不删断言、不塞隔离清单、不放宽容差、不吞异常、不放宽门禁。

## 6. 下一步

带 Node 24 / 网络的机器上按 `--plan` 真编译 web-only 产物（G4 才可能闭合）、
按 `TUAN.md` 逐次授权后把适配层合并进 Tuan-CLI、零空间投影预条件、
精确锥互补、真机 GPU 帧循环。
