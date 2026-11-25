# Yama-Engine 差距分析（修正版，以远端 `main` 为基准）

> 生成：2026-11-24　基准提交：`3411fcf2a5ab`（`vorbis: 修复 MDCT 逆变换…`）
> **本文取代此前的《Yama 引擎完整待办清单》**。那份清单基于**本地残缺工作区副本**，
> 其中多项「缺口」在远端 `main` 上并不成立，现予撤回。

---

## 1. 为什么会误判

此前分析用的是沙盒里的 `/data/workspace/Yama-Engine`：它是仓库的**不完整快照**
（1029 个 `.cs`、0 个 `.md`、`YamaEditor.sln` 声明的 7 个工程中 5 个工程文件不存在）。
远端 `main` 的实际内容要完整得多。以本地副本推出的结论，不能代表仓库真实状态。

## 2. 远端 `main` 的真实台账（GitHub API 实测，2026-11-24）

| 指标 | 远端实测 | 此前误判 |
|---|---:|---|
| 仓库 tree 条目 | **1480** | — |
| `.cs` 文件 | **1037** | 1029（本地副本） |
| `.md` 文档 | **40** | **0** ❌ |
| `.csproj` | **9** | 2 ❌ |
| `.vcxproj` | **1**（`YamaEngineBridge`） | 0 ❌ |
| `YamaEditor.sln` 工程 | **7 个，全部存在** | 缺 5 个 ❌ |
| `README` / `LICENSE` / `.gitignore` / `.editorconfig` / `CONTRIBUTING.md` | **全部存在** | 全部缺失 ❌ |
| CI | `build.yml`：`-warnaserror` + xUnit **1559 用例** + `YamaCLI smoke 60` | 口径矛盾、徽章复用（部分成立） |
| `Templates/` `third_party/` `cmake/` `scripts/` `integrations/` `research/` | **全部存在** | 0 资产 / 0 shader ❌ |
| 主工程构建状态 | 作者自述 **0 错误 / 0 警告**（2026-11-15 实测，见 README 与 docs/） | 断言「10 组 CS0101 阻断」 ❌ |

**撤回的结论**：「sln 5 个工程缺失」「`dotnet restore YamaEditor.sln` 必然失败」
「仓库 0 个文档 / 0 个 LICENSE」「0 个 shader、模型、场景资产」
「10 组 `CS0101` 是硬编译阻断」「没有可执行宿主 / 没有 `Main()`」。

> `YamaEditor/`、`YamaRuntime/`、`YamaCLI/`、`BlenderBridge/`、`MayaBridge/`、`MaxBridge/`、
> `YamaEngine.Native/`、`YamaEngine.Rust/`、`YamaVulkanSandbox/`、`YamaCel2DSandbox/`
> 在远端都是真实工程目录。`build.yml` 里 `dotnet restore` / `build` / `test` / `smoke`
> 四步齐全，`YamaCLI smoke 60` 说明**存在可执行入口并已进 CI**。

### 关于「10 组类型重名」

静态扫描确实在 `BuiltinShaders.cs`、`BuiltinShadersGLSL.cs`、`ShadowMapSystem.cs` 等
文件里看到同名 `FrameConstants` / `ShadowConstants` / `ObjectConstants` 等。但
`ShadowMapSystem.cs:51` 的 `ShadowConstants` 是**类内嵌套类型**（不参与命名空间级冲突），
而远端有真实 `dotnet build -warnaserror` 0 错误的记录。因此这些更可能是嵌套、
条件编译或已消解的历史残留，**不能以静态推断宣称「必然 CS0101」**。列为「待真机确认」，
不作为阻断项。

## 3. 作者自己承认的真实短板（README / docs/ 原文口径）

1. **高级渲染算法未在真实 GPU 端到端验证**：MicroCluster / ProbeField / ReSTIR / SVGF /
   路径追踪多为算法原型；`TriangleRenderer.cs` 的「第一个三角形」已验证。
2. **运行时原生崩溃**：`VulkanPipelineState` 的 `0xC0000005`，需真实硬件排查。
3. **4 项 `<Compile Remove>` 技术债**：`RHI\D3D12\**`、`*SparseShadowAtlases*`、
   `Scripting\VisualScriptingSystem.cs`（与 `NodeScriptSystem.cs` 类型重复）、
   `Build\BuildPipeline.cs`（`BuildPipelineReal.cs` 的早期简版）。
4. **工程卫生**：根目录历史分析脚本与失真错误日志；`ConsoleSDKAbstraction.cs` 的主机 SDK 占位。
5. **程序集拆分提案**：当前 `YamaEngine` 是单一巨型库（766 文件编进一个 Library），
   无程序集边界，见 `docs/FOUNDATION_STATUS.md` §5。
6. **测试全用例跑通**：编译已清零，仍需全量 `dotnet test` 回归。
7. **文档口径不一致**：`docs/FOUNDATION_STATUS.md` 仍写 `.NET 8.0.422`，而
   `global.json` 与 README 已统一为 `10.0.203`；`docs/CODE_TRUTH_REPORT.md` 的
   817 文件 / 319,170 行是 2026-11-01 的快照，与当前 1037 文件不符。

## 4. 本次已落实到仓库的补齐（本分支）

| 类别 | 内容 |
|---|---|
| 治理文件 | 新增 `SECURITY.md`、`CHANGELOG.md`、`CODE_OF_CONDUCT.md`、`NOTICE`（远端原本缺这 4 个） |
| 可移植性 | `VorbisDecoder.cs` 删除写死到开发者本机用户目录的调试输出；`DCCBridge.cs` 支持 `YAMA_BLENDER_PATH` / `YAMA_MAYA_PATH` / `YAMA_3DSMAX_PATH` 环境变量覆盖，修正 Assimp 安装提示 |
| 口径 | README 的 Tests 徽章不再复用 Build 徽章；`docs/FOUNDATION_STATUS.md` 的 .NET 版本统一为 10.0.203 |
| CI | 新增 `repo-guards.yml` + `scripts/path_guard.py`：硬编码路径 / 孤儿 `.cs` / 治理文件 / 占位台账，纯静态、无需 SDK |
| 分析 | 本文件（修正版差距分析） |

## 5. 仍然存在的缺口（必须靠真机 / GPU，文本工作无法代劳）

| 缺口 | 为什么这里做不了 | 下一步 |
|---|---|---|
| 高级渲染 GPU 端到端验证 | 需要 Vulkan 1.4 显卡与真实驱动 | 按 `docs/PHASE1_GPU_VALIDATION_GUIDE.md` 在真机跑完整 PBR 帧并留截图 |
| `0xC0000005` 原生崩溃 | 需要 WinDbg / minidump / 真实调用栈 | 按 `docs/RUNTIME_CRASH_DIAGNOSIS.md` 抓 dump 定位 |
| 4 项 Compile Remove 决策 | 涉及「保留哪套实现」的架构判断 | 逐项决定恢复 / 删除 / 合并，并写进 `docs/BUILD_DECISIONS.md` |
| 巨型库拆分 | 需跨程序集编译验证与引用重构 | 先在 CI 里加程序集边界检查，再分阶段拆 |
| 主机 SDK 占位 | 受厂商 NDA 限制 | 用条件编译隔离，默认构建不编入，文档写明「需第三方授权」 |
| 全量测试回归 | 需 Windows + .NET 10.0.203 | CI `build.yml` 已有门禁，确认 1559/1559 |

## 6. 关于「达到 Godot 3 能力」

Godot 3.5 是多年、多人协作的成熟引擎，其能力包含**可分发编辑器二进制、双渲染器
（GLES2 / OpenGL）、GDScript 与调试器、跨平台导出模板、资产库与社区生态**。
这些不是靠在沙盒里继续生成 `.cs` 文件能达成的：沙盒内 `dotnet / csc / mcs / mono / msbuild`
全部不存在，任何新代码都**未经编译**。

因此本轮的原则是：**只补能静态验证、零风险、不碰渲染管线的缺口**，并把需要真机的部分
明确列出交给 `build.yml` / 真机验证。继续往仓库堆未编译代码，只会把「0 错误 0 警告」
的既有成果重新变成不确定状态。

## 7. 核验方式

```bash
# 远端真实结构
curl -s "https://api.github.com/repos/kino-koki/Yama-Engine/git/trees/main?recursive=1"
# 静态守卫（本分支）
python3 scripts/path_guard.py
# 真实编译（仍需 Windows + .NET 10.0.203）
dotnet build YamaEngine/YamaEngine.csproj -c Debug -p:Platform=x64 -warnaserror
```

---

> 诚实声明：本文的「远端实测」来自 GitHub API 的 tree / contents 读取；
> 「0 错误 0 警告 / 1559 用例」来自仓库 README 与 `docs/` 的作者自述与 CI 配置，
> **本次未在真机重新编译验证**。任何编译结论请以 CI 输出为准。
