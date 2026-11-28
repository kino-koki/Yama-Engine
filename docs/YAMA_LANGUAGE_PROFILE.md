# YAMA 主语言面：为什么「裁剪 Code-OSS」要分层做

> 第二十一轮设计说明｜基线 `df9c9beeea0b`｜状态：**能力面收口已落地，物理裁剪未做**

## 1. 先回答「会不会太大」：会，但要分清大在哪一层

| 层 | 量级 | 本仓库是否引入 |
|---|---|---|
| `microsoft/vscode` 源码仓库 | 约 **1.3 GB**，~2.5M LOC / 40,127 文件，TS 占 72%，1,000+ 依赖，6 个平台构建目标，2,000+ 注册命令，200+ 扩展 API 命名空间 | ❌ 只以 gitlink 锁定 commit，不 vendoring |
| 未压缩 web build 包 | 约 **528 MB** | ❌ |
| 从源码构建所需机器 | 4 核 / **9 GB** 内存起，Node 24 + 网络 | ❌ 本环境不具备 |
| 默认语言/扩展面 | 官方称支持「数百种语言」，内置若干 + Marketplace 自动补齐 | ⚠️ **这才是该裁的** |
| 本仓库实际 Code-OSS 侧资产 | 9 个轻量配置文件 + 14 个命令 + 自研 LSP/DAP/扩展清单/命令路由（第二十二轮后） | ✅ 全部在仓库里 |

结论：**「大」是上游源码与默认发行版的属性，不是本仓库引入的属性。**
第十九轮起本仓库硬声明 `compiledInThisRepo=false`，从来没把 1.8 GB
workbench 拉进构建；`product.json` 里 `extensionsGallery={}`、
`builtInExtensions=[]`、无遥测、无品牌。所以继续往「剥离源码」方向使劲
收益很小，而**默认全量语言面**才是真会让 IDE 变重、也让「YAMA 优先」
变成一句空话的地方。

## 2. 三层策略（本轮只做第 2 层，第 3 层如实标未做）

| 层 | 做法 | 状态 |
|---|---|---|
| L1 内核 | 不动 Monaco / workbench / Electron | 不做，也没必要 |
| **L2 能力面** | 在本仓库声明「内置哪些语言 / 扩展」，把主语言收口到 YAMA，其他语言做成**默认不安装、可拆卸模块** | ✅ **本轮落地** |
| L3 物理裁剪 | fork 上游，删掉不需要的 grammar / 内置扩展 / 命令注册，重编译 web-only 产物 | ❌ 需 Node 24 + 9GB + 网络；本环境写 workflow 实测 404、无 submodule 内容，**不谎报已做** |

L2 的价值：即便将来 L3 由带编译环境的机器完成，它照着
`language-profile.json` 删就行，不会一边删一边重新讨论「到底留哪些语言」。

## 3. YAMA 主语言不是新造的，仓库里早就有

| 资产 | 位置 |
|---|---|
| 场景/主语言文件 `.yama` | `Templates/Basic2D/Content/Scenes/Main.yama` 等 3 个模板 |
| 项目契约 | `Templates/*/yama.project.json`（本轮补 `primaryLanguage=yama`） |
| 场景 schema | `schemas/yscene.schema.json` |
| 脚本层规格 | `docs/SCRIPTING_ABI_SPEC.md`（C# Roslyn + VisualScript 双轨，明确「缺 GDScript 等价物」是规格、不是实现） |
| 编辑器桥接 | `YamaEngine/Editor/CodeOss/*.cs`（自研 LSP 3.17 / DAP 1.x 帧） |

所以本轮**不发明语言、不写 VM、不动 `Scripting/`**，只把 Code-OSS 侧
原来硬编码的 `yama-scene`（.yscene/.yprefab）扩成真正的 `yama` 主语言，
并把已有资产串成一条可判定契约。

## 4. 契约长什么样

`ide/codeoss/language-profile.json`：

- `primary.id = yama`，`enforced = true`，绑定 `.yama` / `.yscene` / `.yprefab`，
  项目契约 `yama.project.json`，场景 schema 复用 `schemas/yscene.schema.json`。
- `required_system_languages`：`csharp`（引擎宿主 / Roslyn 脚本层）、
  `json`（项目与场景数据）。二者 `userGameAuthoring=false`——**保留支持，
  但不作为「写游戏」的语言**，避免「保留部分语言」被解读成可以随便用 C# 写玩法。
- `optional_modules`：typescript / lua / gdscript / glsl / wgsl / python / cpp，
  全部 `installed=false` + `removable=true`。需要哪个挂哪个，不需要就不存在。
- `forbidden_defaults`：内置全量语言包、Marketplace 自动安装、`builtInExtensions`。
- `enforcement.strictYamaProject = true`。

## 5. 「强制使用 YAMA 开发游戏」到底强制在哪（诚实边界）

**不**改 workbench 内核、**不**锁死 UI、**不**谎称编辑器已经禁止打开 `.ts`。
本环境没编译上游，做不到也不假装做到。

真正强制的是**本仓库可控的三道**：

1. **项目契约**：每个游戏项目必须有 `yama.project.json`，且
   `primaryLanguage=yama`、`startupScene` 必须是 `.yama` 且文件真实存在。
   三个内置模板已全部补上声明。
2. **离线门禁**：`scripts/check_yama_project.py --check`（纯 stdlib、不联网、
   不执行外部进程、fail-closed），校验 profile 自身 + 三个模板；
   `--project DIR` 可校验任意项目目录。
3. **CI 断言**：`YamaLanguageProfileTests.cs` 12 项反射断言 + 1 个程序集级
   `[Fact]` 由 `build.yml` 的 Test step 每次真实执行；
   `build_codeoss_minimal.py --check` 与 `fetch_codeoss.py --check` 也把
   「主语言强制 / 可选模块零内置 / 严格门禁」锁成红灯条件。

把 `enforced` 改成 false、把任一 `installed` 改成 true、把
`strictYamaProject` 改成 false、把模板入口换成 `.tscn`——都会红灯。
**这是可判定事实，不是文档口号。**

## 6. 模块化拆卸怎么工作

- 扩展清单 `contributes.languages` 由 `YamaLanguageProfile.ManifestLanguagesJson()`
  生成，**只含** `yama` + `csharp` + `json`；7 个可选模块 id 一个都不进内置清单。
- `YamaExtensionManifest.IsWellFormed` 增加反向校验：内置语言里出现任何
  可选模块 id 直接判非法。
- 命令面新增 `yama.languageProfile`（读契约）与 `yama.newGameProject`
  （返回 YAMA 脚手架意图），共 **12** 个命令；配置项
  `yama.language.allowOptionalModules` 默认 false。
- 新增模块的正确姿势：在 `optional_modules` 里加一项并写明扩展名，
  保持 `installed=false`；门禁会验证它没被偷偷内置。

## 7. 不能翻转的口径

- 上游 1.8 GB web-only workbench **仍未编译**，`compiledInThisRepo=false`。
- **L3 物理裁剪未做**；需带 Node 24 / 9GB / 网络的机器，按
  `build_codeoss_minimal.py --plan` 的输出执行，本仓库不代做、不谎报。
- **Python 独立门禁模板仍未启用**：token 仅 `repo` scope，写
  `.github/workflows/` 实测 404；需网页端人工移入。等价语义由 C# xUnit 覆盖。
- G4–G8 未闭合，不宣称已达 SOTA；未向 `kino-koki/Tuan-CLI` 写入任何文件。
- 不删断言、不放宽容差、不吞异常。


## 8. 第二十二轮：行为层声明（不是 VM）

第二十一轮收口的是「项目契约 + 内置语言面」，但 `.yama` 场景的
`Components` 只有内置组件，`yscene.schema.json` 的 `Components.items`
是裸 `{"type":"object"}`——**没有任何脚本绑定声明**，所以「用 YAMA
开发游戏」当时只能摆场景。

本轮不改 `SceneSerializer` 的强类型组件路径（`ComponentRecord` 只 switch
Camera/Light，动它会牵动场景序列化、零回归不成立），改走独立声明文件：
`schemas/yama-script.schema.json` + `Templates/*/Content/Scripts/*.yama` +
`YamaEngine/Scripting/Abi/YamaScriptDeclaration.cs`。声明被**真实还原**
成 `ScriptBinding` / `PropertyBag` / `NodeRef`：类型名未注册即拒绝、
属性 kind 只放行 9 种 variant 子集、生命周期只认 `IScriptLifecycle`、
信号只是连接点声明。项目契约补 `scriptBinding: "Content/Scripts"`。

**边界**：不是 VM、不是 DSL 解释器、不执行字节码；
`vmImplemented=false` 是硬声明。第二十一轮说的「YamaScript 等价物
仍是规格」依然成立——本轮补的是 L1↔L2 的声明接线，不是跨过最后一步。
另更正 `docs/SCRIPTING_ABI_SPEC.md`：L2/L3 绑定 ABI 早已实现并在 CI
执行，原文「规格文档，不是实现 / 沙盒无 dotnet」是口径回漂。
