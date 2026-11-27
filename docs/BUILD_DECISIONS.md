# 构建决策：6 项非测试 `<Compile Remove>` 逐项结论

> 基准 `main@3411fcf2a5ab` 的 `YamaEngine/YamaEngine.csproj`（54 条 Include / 24 条 Remove）。
> 24 条 Remove 中 18 条是引擎源码树里的 `*Tests.cs`（与 `YamaEngine.Tests/` 同名类冲突，应搬走而非长期压着），
> 剩下 **6 条**才是真正需要架构决策的。本文逐项给结论。

## D0 先看一个容易误判的点

`Scripting/ScriptHotReload.cs`（被 Remove，876 行）与
`PluginSystem/ScriptHotReloadSystem.cs`（**在编译**，1102 行）看起来像「同名类型冲突」，
但实测命名空间不同：

| 文件 | namespace | 关键类型 |
|---|---|---|
| `Scripting/ScriptHotReload.cs` | `YamaEngine.Scripting` | `HotReloadOptions`:125、`CompilationResult`:163、`ScriptHotReload`:183 |
| `PluginSystem/ScriptHotReloadSystem.cs` | `YamaEngine.PluginSystem` | `HotReloadOptions`:65、`CompileResult`:107、`ScriptHotReloader`:321 |

`HotReloadOptions` 虽然同名，但**分别位于 `YamaEngine.Scripting` 与 `YamaEngine.PluginSystem`，不构成 `CS0101`**。
所以「恢复它就会编译报错」是错的——真正的问题是**功能双轨**（两套热重载、两套状态迁移、两套事件参数），
不是编译冲突。这条结论已用远端源码逐行核验，见 `scripts/capability_audit.py` 的 `dual_track` 检查。

> 这也是上一轮《完整待办清单》里「10 组 CS0101 阻断」被撤回的同类原因：静态扫描只看到同名，
> 没看嵌套类型 / 命名空间 / `Compile Remove` 的实际生效范围。**任何编译结论以 `dotnet build` 为准。**

## D1 `Scripting/ScriptHotReload.cs` — 决策：**保留 Remove，归档为参考实现**

- 现状：`PluginSystem/ScriptHotReloadSystem.cs`（1102 行）已在编译，含 `StateMigrator` / `FieldMapping` /
  `BeforeReloadEventArgs` / `AfterReloadEventArgs`，功能更完整；`Scripting/` 版依赖注释里写的是 `net8.0`，
  而 `global.json` 已锁 `10.0.203`。
- 动作：
  1. 不恢复进编译（避免双轨 + 过时 TFM 注释）。
  2. 移到 `docs/archive/ScriptHotReload.legacy.cs.md`（代码存档，不参与编译），或直接在下一次清理提交删除。
  3. 把 `Scripting/ScriptAssemblyLoader.cs`（collectible ALC）作为 `PluginSystem` 版的底层依赖显式接线，
     在 `docs/EDITOR_WIRING.md` 的 E6 写清唯一入口是 `YamaEngine.PluginSystem.ScriptHotReloader`。
- 验收：全仓库 `grep -rn "class HotReloadOptions"` 只剩 1 个参与编译的定义；运行中改速度常量 → 行为变化且 `SerializeField` 字段值保持。

## D2 `Scripting/VisualScriptingSystem.cs` — 决策：**保留 Remove，收敛到 `NodeScriptSystem`**

- 现状：2662 行，与 `Scripting/NodeScriptSystem.cs` 有 20+ 组同名类型
  （`NodeScriptGraph` / `NodeScriptNode` / `NodeScriptPin` / `NodeScriptDataType` / `SequenceNode` …）。
  另有 `VisualScript/NodeScriptNodeLibrary.cs`(2501)、`NodeGraphSystem.cs`(2454)、`VisualScriptEditorSystem.cs`(1994)
  是一套独立的图系统。
- 动作：以 `NodeScriptSystem.cs` + `VisualScript/` 为唯一真源；`VisualScriptingSystem.cs` 归档到 `docs/archive/`；
  长期目标是让两处图节点库共用同一注册表。
- 验收：仓库里只有一套参与编译的 `NodeScript*`，能画 `OnTick → Move` 并运行。

## D3 `Build/BuildPipeline.cs` — 决策：**保留 Remove，删除旧文件**

- 现状：`FOUNDATION_STATUS.md` 自述它是 `BuildPipelineReal.cs`(3275 行) 的早期简版；
  且全仓库唯一的 `static Main()` 在这里，被 Remove 后引擎库彻底没有入口——
  但这**不是问题**：`YamaCLI/YamaCLI.csproj` 是真正的可执行入口，`build.yml` 的 `smoke 60` 已验证。
- 动作：删除文件（或移入 `docs/archive/`），并从 csproj 移除这条 Remove，让 Include 列表更干净。
- 验收：`grep -rn "static .* Main(" YamaEngine/` 为 0；`dotnet run --project YamaCLI -- smoke 60` 通过。

## D4 `Rendering/**/*SparseShadowAtlases*.cs` — 决策：**暂留 Remove，挂到 GPU 验证 Gate**

- 现状：1819 行完整实现，注释标「开发中」。它和 `CascadeShadowMapRenderer.cs`(1855 行) 的阴影路径关系未核验。
- 动作：不擅自恢复（可能引入未验证的渲染分支，碰你明天要发的新版管线）。
  在 `docs/RENDER_MATRIX.md` 登记为「待真机决定恢复/合并」，由 GPU Gate 的 R3 验收来定。
- 验收：R3 阴影对比截图通过后，再决定恢复；恢复时同步跑 `-warnaserror`。

## D5 `DCC/BlenderPythonPlugin.cs` / `MayaPythonPlugin.cs` — 决策：**搬出 C# 源码树**

- 现状：`.cs` 文件里装 Python 插件代码，`DCC/` 目录还有 `DCCBridge.cs`(1965 行)。
  远端已有 `BlenderBridge/`、`MayaBridge/`、`MaxBridge/` 三个独立工程与 `integrations/`。
- 动作：把 Python 本体移到 `tools/dcc/blender/`、`tools/dcc/maya/` 下的 `.py`；C# 侧只保留 IPC 客户端。
- 验收：`YamaEngine/DCC/` 下无 `*.py` 内容混装；Blender 能装插件并导出测试网格。

## D6 18 条 `*Tests.cs` Remove — 决策：**统一搬进 `YamaEngine.Tests/`**

- 清单：`Testing/{Scene,Material,Animation,Build,Diagnostics,Render,Editor,Runtime,Gameplay,Ecs,Math,Physics,Modeling,Asset}Tests.cs`
  + `Audio/MetaSounds/MetaSoundTests.cs` + `Physics/Advanced/Tests.cs` + `Physics/Real/Tests.cs` + `Rendering/Water/Real/Tests.cs`。
- 原因：与 `YamaEngine.Tests/` 下的同名类冲突，靠 Remove 长期压着会让这些测试**永远不跑**。
- 动作：逐个人工比对，把有效断言合并进 `YamaEngine.Tests/`，删除引擎源码树里的副本，清空对应 Remove。
- 验收：`build.yml` 的 `dotnet test` 用例数从 1559 上升且全绿；`YamaEngine/Testing/` 不再含 `*Tests.cs`。

---

## 合并顺序（避免撞你明天的新版）

1. 本决策文档 + 能力矩阵（纯文档，零风险）
2. D1/D2/D3/D5/D6 的机械清理（每步单独提交，每步跑 `-warnaserror` + `dotnet test`）
3. D4 等 GPU Gate 结果
4. 任何涉及 `Rendering/` / `Vulkan/` 的改动放到新版 GPU 验证分支，不要混进这批

> **本轮不改 csproj、不删源文件**：以上是需要你在真机确认后执行的决策，不是本分支已执行的改动。
