# 编辑器 9 项能力接线核验表（Godot 3.5 官方编辑器特性逐项）

> 对照 Godot 3.5 官方《特性列表》的编辑器小节。远端 Yama 的编辑器文件不是「没有」，
> 而是**缺一份「已有文件 → 公共 API → 接线点 → 验收」的核验**。本表补的就是这一层。

| # | Godot 3.5 能力 | Yama 已有文件（远端实测） | 已有公共 API / 行数 | 接线点（待核验） | 验收 |
|---|---|---|---|---|---|
| E1 | 场景树编辑器 | `Editor/Panels/OutlinerPanel.cs`、`Editor/EditorSceneController.cs`、`Editor/EditorWorld.cs` | SceneController 488 行 | `MainEditorWindow` → `OutlinerPanel` → `EditorWorld` | S3 |
| E2 | 内置脚本编辑器 | `CodeEditing/CodeEditorView.cs`(2031)、`Editor/Panels/CodeEditorPanel.cs`(1577) | `CodeEditorViewModel` / `IntelliSenseEngine` / `CodeFormatter` | 双击 `.cs` → 面板加载 → 保存回写 | S7 |
| E3 | 脚本调试器 | `CodeEditing/CodeDebugger.cs`(463) | `Breakpoint` / `StepOver` / `GetCallStack` / `GetLocalVariables` / `EvaluateExpression` / `WatchWindow` | **断点 UI 是否订阅 `BreakpointHit` 事件**（代码有事件，UI 未核验） | S9 |
| E4 | 性能监控 | `Editor/Panels/StatsPanel.cs`(373)、`Diagnostics/{CPU,GPU,Memory,Network}Profiler.cs`、`FrameDebugger.cs`(142) | 36 个 Diagnostics 文件 | StatsPanel 是否实时读 profiler | S3 附 |
| E5 | 场景热编辑 | `Editor/EditorSceneController.cs` | 需核验关闭后持久化 | `EditorPlayModeController`(549) 停止时是否写回场景 | S4 |
| E6 | 脚本热重载 | `PluginSystem/ScriptHotReloadSystem.cs`(1102) **主**；`Scripting/ScriptHotReload.cs`(876, Remove) **归档** | `ScriptHotReloader` / `StateMigrator` / `FieldMapping` / `Before/AfterReloadEventArgs` | 文件监听 → Roslyn 增量编译 → collectible ALC 卸载 → 状态迁移 → 事件通知编辑器 | S8 |
| E7 | 实时摄像机复制 | `Editor/EditorCameraController.cs`、`YamaEditor/Controls/ViewportCamera3D.cs` | 需核验同步链路 | 编辑器相机 transform → 运行时相机（单向或双向） | S10 |
| E8 | 远程检查器 | `Editor/EditorEngineBridge.cs`(1412) | 需核验 IPC 通道（命名管道 / TCP / LiteNetLib） | 运行进程暴露属性读取端点；**改动不持久化** | S10 |
| E9 | 插件 / 资产库 | `PluginSystem/PluginMarketSystem.cs`(1073)、`PluginCoreSystem.cs`、`integrations/README.md` | 市场 / 安装 / 事件系统 | 编辑器菜单 → 市场 → 安装 → 重启生效 | S3 附 |
| E10 | 内部脚本编辑器外接 VSCode/VIM | `CodeEditing/CodeEditorCore.cs` | 需核验外部编辑器协议 | `EditorShortcuts` 里加 "Open in External Editor" | — |
| E11 | 内置离线类参考 | `ARCHITECTURE.md` / `USER_GUIDE.md` / 40 篇 `docs/` | 有手册，**无 API 类参考** | `docfx` + XML 注释 → `docs/api/index.html` | `scripts/gen_api_docs.ps1` |
| E12 | 多语言编辑器 | `Localization/` 8 文件（`StringTableSystem` 1046 / `RTLTextSystem` / `PluralRulesSystem`） | 表驱动 + RTL + 复数 | 先把编辑器 UI 字符串全量抽到 StringTable，补中/英 | S3 附 |

## 三个最关键的「未核验」点

1. **E3 调试器**：`CodeDebugger` 的 API 完整度已接近 Godot 3 的 GDScript 调试器，
   但 `BreakpointHit` 事件是否有 UI 订阅、断点是否真的能中断主循环，静态看不出来 → S9 真机判。
2. **E6 热重载双轨**：见 `BUILD_DECISIONS.md` D1。命名空间已核验不同（不是 CS0101），
   但要**指定唯一入口** `YamaEngine.PluginSystem.ScriptHotReloader`，否则编辑器不知道订阅谁的事件。
3. **E7/E8 编辑器↔运行时 IPC**：`EditorEngineBridge.cs` 1412 行，但是否真有跨进程通道未核验。
   Godot 3 的「远程检查器 + 实时摄像机复制」都依赖这条通道，它是 S10 的前置。

## 本轮补了什么

- 上表（把 9 项官方特性逐项落到文件 + API + 验收 ID）
- `schemas/debug-protocol.schema.json`：把 E3/E8 的消息格式定死，便于后续接线
- `scripts/gen_api_docs.ps1` 规格（E11）
- `E2E_ACCEPTANCE.md` 的 S3/S4/S9/S10 四条验收场景

**没做**：不改任何编辑器 `.cs`——接线必须编译验证，沙盒没有 SDK。
