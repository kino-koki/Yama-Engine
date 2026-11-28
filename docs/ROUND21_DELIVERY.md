# 第二十一轮交付：YAMA 主语言面收口（回答「Code-OSS 会不会太大」）

基线 `df9c9beeea0b`（第二十轮，双绿）｜设计说明见 `docs/YAMA_LANGUAGE_PROFILE.md`

## 结论：大的是上游，不是本仓库

联网查证的量级：`microsoft/vscode` 仓库约 **1.3 GB**、~2.5M LOC /
40,127 文件、1,000+ 依赖、6 个平台构建目标、2,000+ 命令；未压缩 web build
包约 **528 MB**；从源码构建要 4 核 / **9 GB** 内存 + Node 24；官方称支持
「数百种语言」。而本仓库从未 vendoring 这些——只有 gitlink 锁定 commit、
7 个轻量配置文件、12 个命令、自研 LSP/DAP，`compiledInThisRepo=false`
是硬声明。

所以本轮**没有**去 fork 上游做物理裁剪（本环境无 Node 24、无 submodule
内容、写 workflow 实测 404，做不到；且第十九轮已承诺不编译上游，不翻案）。
真正收口的是**默认全量语言/扩展面**——这才是会让 IDE 变重、也让
「YAMA 优先」变成空话的地方。

## 1. 不新造语言，复用已有资产

仓库里早就有 YAMA 主语言资产：三个模板的 `Content/Scenes/Main.yama`、
`yama.project.json`、`schemas/yscene.schema.json`、
`docs/SCRIPTING_ABI_SPEC.md`（C# Roslyn + VisualScript 双轨）。
本轮**不写 VM、不动 `Scripting/`**，只把 Code-OSS 侧原来硬编码的
`yama-scene`（.yscene/.yprefab）扩成真正的 `yama` 主语言，
并把这些资产串成可判定契约。

## 2. 契约与强制机制

`ide/codeoss/language-profile.json`：

- 主语言 `yama`：`enforced=true`，绑定 `.yama` / `.yscene` / `.yprefab`，
  项目契约 `yama.project.json`。
- 系统级保留 `csharp` / `json`，标记 `userGameAuthoring=false`——
  保留支持，但不作为写游戏的语言。
- 7 个可插拔模块（typescript / lua / gdscript / glsl / wgsl / python / cpp）
  全部 `installed=false` + `removable=true`；禁止内置全量语言包、
  Marketplace 自动安装、`builtInExtensions`。
- `strictYamaProject=true`。

**强制边界（如实）**：不改 workbench 内核、不锁死 UI、不谎称编辑器已禁止
打开 `.ts`。本环境没编译上游，做不到也不假装做到。强制落在本仓库三道：
项目契约（3 个模板补 `primaryLanguage=yama`）、
`scripts/check_yama_project.py`（纯 stdlib / 离线 / fail-closed）、
C# xUnit + 两个 `--check` 脚本的 CI 断言。把 `enforced` 改 false、
任一 `installed` 改 true、`strictYamaProject` 改 false、模板入口换成 `.tscn`
都会红灯。

## 3. 引擎侧真的接上了

- 新增 `YamaEngine/Editor/CodeOss/YamaLanguageProfile.cs`：解析契约、
  `IsYamaFirst`（fail-closed）、`ManifestLanguagesJson()`（只生成
  yama/csharp/json 三种内置语言）。
- `YamaExtensionManifest`：命令 **10 → 12**（`yama.languageProfile` /
  `yama.newGameProject`）；`contributes.languages` 改为按 profile 生成；
  新增两个语言配置项；`IsWellFormed` 反向拒绝任何可插拔模块 id 出现在内置清单。
- `YamaCodeOssBridge`：持有 `Profile`、路由 12 个命令。
- `YamaCodeOssProduct`：解析 `product.json` 的 `yamaLanguageProfile` 段，
  新增 `IsYamaFirstLanguage`；第十四轮 6 项干净 MIT、第二十轮
  `IsMinimalWebBuild` 均不回退。

## 4. 数字同步（第十七 / 二十轮踩过的坑，逐条对齐）

| 项 | 前 | 后 |
|---|---|---|
| 命令数 | 10 | **12**（R15 测试 / R20 测试 ×2 / RepoContract / contract / 单测 / 文档全同步） |
| 摘要配置文件 | 6 | **7**（Python `CONFIG_FILES` / C# `ConfigFiles` / `MINIMAL.lock.json` / 说明文档） |
| hook 静态核查 | 28 | **30** |
| 反射断言 | 270 | **282**（新增 12 项） |
| `[Fact]` | 14 | **15** |
| 可选语言模块默认内置数 | — | **0** |

6→7 文件 SHA-256 全部重新定标，Python `hashlib` 与 C#
`Convert.ToHexString(SHA256.HashData(...))` 同口径，LF / CRLF 双视图通过。

## 5. 仍然不能翻转的

- 上游 1.8 GB web-only workbench **仍未编译**；`compiledInThisRepo=false`。
- **L3 物理裁剪未做**（需 Node 24 / 9GB / 网络），本轮不谎报。
- **Python 独立门禁模板仍未启用**（token 仅 `repo` scope，写 workflow 实测 404）。
- G4–G8 未闭合，不宣称已达 SOTA；不宣称零第三方；不宣称精确 LCP/SOCP。
- **未向 `kino-koki/Tuan-CLI` 写入任何文件**，仍按 `TUAN.md` 逐次确认。
- 不删断言、不塞隔离清单、不放宽容差、不吞异常。

## 6. 下一步

带 Node 24 / 9GB / 网络的机器上按 `--plan` 真编译 web-only 产物，并照
`language-profile.json` 做 L3 物理裁剪（G4 才可能闭合）；网页端启用独立
Python 门禁；按 `TUAN.md` 逐次授权后合并适配层；`docs/SCRIPTING_ABI_SPEC.md`
里的 YamaScript 等价物仍是规格、不是实现；零空间投影预条件、精确锥互补、
真机 GPU 帧循环。
