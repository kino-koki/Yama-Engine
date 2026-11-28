# 第二十二轮交付：YAMA 行为层接线 + ABI 规格文档口径更正

基线 `f2bdf7ef90eb`（第二十一轮，双绿）｜设计说明 `docs/YAMA_LANGUAGE_PROFILE.md`

## 为什么要这一轮

第二十一轮把「YAMA 主语言」收口到了项目契约层，但留了一句诚实话：
`SCRIPTING_ABI_SPEC.md` 里的 YamaScript 等价物仍是规格、不是实现。
本轮先扫描仓库现状，发现两处「说了没对齐」：

**A. 文档口径回漂（和第二十轮 fetch 脚本同类）**

`docs/SCRIPTING_ABI_SPEC.md` 还写着「规格文档，不是实现」「沙盒无
`dotnet` / `csc` / `mono`，任何新增 `.cs` 都无法编译验证」「实现留到有
真机编译环境时」「本轮交付的是这三张票的判定标准」。但实际上：

- `YamaEngine/Scripting/Abi/` 下 `ScriptBinding` / `NodeRef`+`NodeHandleTable` /
  `PropertyBag` / `SignalBus` / `LifecycleDispatcher` / `ScriptRegistry` /
  `ScriptVariant` **全部已实现**；
- `YamaEngine.Tests/ScriptAbiTests.cs` 7 项断言由 `build.yml` 的
  Test step（.NET 10 / Windows）**每次真实执行**；
- 仓库 CI 本来就有编译器，不存在「等真机环境」。

把已经做到的事写成没做，和把没做到的事写成做到一样不诚实。本轮更正：
L2 已实现、L3 已实现、L4 的 ABI 侧已实现，**L1 的 VM/DSL 仍未实现**，
S7 只拿到 L2/L3 那半张票。

**B. 真正的缺口：L1↔L2 没接上**

`.yama` 场景的 `Components` 只有 `CelPhysics2D` / `TileMap` / `Camera` /
`Light` 等内置组件；`yscene.schema.json` 的 `Components.items` 是裸
`{"type":"object"}`；**没有任何脚本绑定声明**。也就是说「用 YAMA 开发游戏」
此前只能摆场景，不能声明行为。

## 1. 本轮做了什么

不动 `SceneSerializer` 的强类型组件路径（它 `ComponentRecord.FromComponent`
只 switch Camera/Light，改它风险大且会牵动场景序列化），改走**独立声明文件**：

| 产物 | 作用 |
|---|---|
| `schemas/yama-script.schema.json` | 声明格式：`typeName` / 带 kind 的 `properties` / `signals` / `lifecycle`，`additionalProperties=false` |
| `Templates/*/Content/Scripts/*.yama` | 三个模板各一个最小声明（PlayerController / CameraRig / NpcBrain） |
| `YamaEngine/Scripting/Abi/YamaScriptDeclaration.cs` | **真实还原**：解析 → `ScriptBinding` + `PropertyBag` + `NodeRef` |
| `scripts/check_yama_project.py` | 项目契约扩到 `scriptBinding: "Content/Scripts"`，纯 stdlib / 离线 / fail-closed |
| `ide/codeoss/language-profile.json` | 新增 `scriptBinding` 段，`vmImplemented=false` 硬声明 |
| `YamaScriptBindingTests.cs` | 12 项反射断言 + 1 个程序集级 `[Fact]` |

`CreateBinding` 的真实行为：类型名未在 `ScriptRegistry` 注册就抛异常拒绝；
节点由 `NodeHandleTable` 生成 `NodeRef`（脚本不持有原生指针）；5 个属性
逐项灌入 `PropertyBag`，可由 `binding.Properties.Get("speed")` 读回；
`TryRegisterLifecycle` 只对真正实现 `IScriptLifecycle` 的实例返回 true；
`DeclaredSignals` 只是连接点声明，空 `SignalBus` 的 `Emit` 返回 0，
**不谎报已连接、不谎报已挂载**。

## 2. 明确没做的（不谎报）

- **不是 VM、不是 DSL 解释器、不执行字节码**。`language-profile.json` 的
  `vmImplemented=false`、`contract.json` 的 `vm_implemented=false`、
  `SCRIPTING_ABI_SPEC.md` 的状态行都是硬声明。
- 没改 `SceneSerializer`、没往场景 `Components` 塞脚本组件、没动
  `yscene.schema.json`。理由写在设计说明里：避免牵动强类型序列化路径。
- 上游 1.8 GB web-only workbench **仍未编译**；**L3 物理裁剪仍未做**；
  Python 独立门禁模板**仍未启用**（token 仅 `repo` scope，写 workflow 实测 404）。
- G4–G8 未闭合，不宣称已达 SOTA；不宣称零第三方；不宣称精确 LCP/SOCP；
  **未向 `kino-koki/Tuan-CLI` 写入任何文件**（`TUAN.md` 逐次确认）。
- 不删断言、不塞隔离清单、不放宽容差、不吞异常。

## 3. 数字同步

| 项 | 前 | 后 |
|---|---|---|
| 引擎侧命令 | 12 | **14**（`yama.scriptBinding` / `yama.newScript`） |
| 受校验配置 | 7 | **9**（加入 `yama-script.schema.json`、`check_yama_project.py`） |
| hook 静态核查 | 30 | **32** |
| 反射断言 | 282 | **295** |
| `[Fact]` | 15 | **16** |
| 属性 kind 白名单 | — | **9**（拒绝 object/array/dictionary/resourceRef/nodeRef） |

9 个 SHA-256 重新定标，Python `hashlib` 与 C# `Convert.ToHexString`
同口径，LF / CRLF 双视图通过。

## 4. 中途红灯（如实记录，逐条修根因）

本轮 develop 连续四次红灯，全部逐条定位并修复，没有删断言、没有塞
隔离清单、没有放宽容差、没有吞异常：

1. **CS1012 / 11 条编译注解**：新测试里误用了 Python 式单引号多字符
   面量（`'"value": 6.25'`）与 `string.Replace(old,new,1)` 三参数写法。
   C# 没有这个重载。改为 `ReplaceFirst` 辅助方法 + 双引号转义字符串；
   本地验收加 [18] 粗检专防此类误用。
2. **既有 [Fact] 被新增数据文件破坏（真正的确定性红灯）**：用二分法
   （临时移除全部 13 项新测试 + 1 个新 [Fact]）确认红灯与新增测试无关。
   根因是 `RepoContractTests.cs` 里两个文件系统假设：
   - `TestAllJsonSchemasAreValid` 断言 `schemas/*.json` 恰好 4 个，
     本轮新增 `yama-script.schema.json` 使实际为 5；
   - `TestTemplatesAndExportContractsAreValid` 把 `Templates/**` 下
     所有 `.yama` 一律当场景，强取 `Version` / `Objects`；
     新增的 `Content/Scripts/*.yama` 是脚本绑定声明，没有这两个键，
     `GetProperty` 直接抛 `KeyNotFoundException`。
   修法：前者精确同步为 5 并**追加**「新 schema 必须声明 TypedValue
   与 kind 白名单」的更强校验；后者按 `schema` 字段分流——声明文件
   必须含 `typeName` / `properties(Object)` / `lifecycle`，场景文件
   仍按原契约校验 `Version=1` + `Objects(Array)`。**两类都真实校验，
   不跳过任何一个文件**，不是放宽。
3. **诊断轮引入的 xUnit1013**：为定位红灯曾临时给 `XunitAdapter`
   加 `public static WriteSummary`，触发 xUnit analyzer「test class
   内 public 非测试方法」警告。诊断通道（原始 stdout / Step Summary）
   在 vstest 子进程里均不可达，遂完全移除该文件，commit tree 基于
   main 时自动恢复原版。诊断代码不留在交付里。

另记一个设计待办：脚本绑定声明目前仍用 `.yama` 扩展名，与场景
`Main.yama` 同名。本轮靠 schema 字段分流，语义可用但存在格式歧义；
后续若分离为 `.yscript` 之类扩展名，需同步契约、门禁与 9 个摘要。
本轮未做，如实标注。

最终以推送后 develop / main 两条 run 的实际 conclusion 为准，
此处不预写 CI 结果。

## 5. 下一步

带 Node 24 / 9GB / 网络的机器上按 `--plan` 真编译并按
`language-profile.json` 做 L3 物理裁剪；网页端启用独立 Python 门禁；
按 `TUAN.md` 逐次授权后合并适配层；L1 的 YamaScript 等价物仍是规格
（需要真 DSL/VM 设计，不是再补一层声明）；S9 调试器接线；
零空间投影预条件、精确锥互补、真机 GPU 帧循环。
