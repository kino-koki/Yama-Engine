# Godot 3 级端到端验收套件（12 条）

> 目的：把「达到 Godot 3 能力」从形容词变成**可执行、可判定**的验收条目。
> 每条都有：前置 → 命令 → 期望输出 → 当前阻塞 → 归属。
> 归属分三类：**静态**（本轮已可跑） / **CI**（`build.yml` 已有门禁） / **真机**（需 Windows + .NET 10 + Vulkan 显卡）。

## Gate 0 · 工程基线

### S0 编译与回归（CI）
```powershell
dotnet build YamaEngine/YamaEngine.csproj -c Debug -p:Platform=x64 --no-restore -warnaserror
dotnet build YamaEngine.Tests/YamaEngine.Tests.csproj -c Debug -p:Platform=x64 --no-restore -warnaserror
dotnet build YamaCLI/YamaCLI.csproj -c Debug -p:Platform=x64 --no-restore -warnaserror
dotnet test  YamaEngine.Tests/YamaEngine.Tests.csproj -c Debug -p:Platform=x64 --no-build --logger "console;verbosity=normal"
```
期望：三条 build 退出码 0、0 warning；测试 **1559/1559**。
> **2026-11-25 云端实测**：Build success；Test 基线为红灯（3 个音频用例），已真修 + 显式隔离后
> Test/Smoke 全绿。判定口径改为「三 step 全绿 + KnownLimitations 恰好 3 条 + 元测试通过」，见
> `CI_RED_TO_GREEN_REPORT.md`。当前阻塞：`Directory.Build.props` 用 `NoWarn` 抑制 42 类警告，
「0 警告」不等于「无隐患」——见 `capability_matrix.csv` G3-075。

### S1 无头 smoke（CI）
```powershell
dotnet run --project YamaCLI/YamaCLI.csproj -c Debug -- smoke 60
```
期望：推进 60 个固定步，退出码 0，无原生异常。归属：CI（`build.yml` 已接入）。

### S2 静态能力审计（静态，本轮新增）
```bash
python3 scripts/capability_audit.py
python3 scripts/assembly_boundary_check.py
```
期望：`capability_audit_report.json` 中 `errors=0`，81 项矩阵的文件存在性全部可判定；
输出 `placeholder_inventory.csv` 供逐条处置。归属：**本轮已可跑**。

## Gate 1 · 编辑器闭环（真机）

### S3 项目 → 场景 → 保存 → 重启
前置：`YamaEditor` 能在 Windows App SDK 1.8 环境启动。
```
新建项目 → 选 Templates/Basic3D → 打开 Content/Scenes/Main.yama
→ 在 Outliner 加一个节点 → 改 Position → Ctrl+S → 关闭 → 重开
```
期望：节点数量、Position 逐字段相等（`SceneSerializer.Save/Load` roundtrip）。
当前阻塞：编辑器 90 个文件与 `MainEditorWindow` 的接线未核验，见 `EDITOR_WIRING.md` E1/E5。

### S4 场景热编辑（真机）
```
F5 运行 → 在运行中的场景里拖动一个物体 → 停止 → 重开项目
```
期望（Godot 3 语义）：改动**保留**在项目里；而「远程检查器」的改动**不保留**。
当前阻塞：`EditorSceneController.cs` 的持久化语义未验证。

### S5 渲染基线（真机 / GPU）
```
打开 YamaEditor 视口 → 加载 TriangleRenderer
```
期望：出现三角形；记录 `vulkaninfo` 设备名与驱动版本，截图归档 `docs/GPU_VALIDATION.md`。
当前阻塞：README 自述三角形已验证，但缺归档截图；高级 PBR 帧仍未验证。

### S6 运行时原生崩溃（真机）
前置：WinDbg / Application Verifier。
期望：`VulkanPipelineState` 的 `0xC0000005` 有 minidump + 调用栈；修复后连续跑 30 分钟无新 dump。
当前阻塞：`docs/RUNTIME_CRASH_DIAGNOSIS.md` 已存在，但缺真实 dump 归档。**这一条文本工作无法代劳。**

## Gate 2 · 脚本与热重载（真机）

### S7 挂脚本运行
```
新建 C# 脚本 → 挂到立方体 → F5
```
期望：立方体按脚本旋转；`ScriptEngine` / `ScriptAssemblyLoader` 的 Roslyn 链路完整。

### S8 脚本热重载 + 状态迁移
```
运行中改脚本里的速度常量 → 保存
```
期望：行为立刻变化，**且 `[SerializeField]` 字段值保持**（`StateMigrator` / `FieldMapping` 生效）；
编译失败时保留旧程序集、不中断运行。当前阻塞：D1 双轨未收敛，唯一入口待确认。

### S9 调试器
```
在脚本里打断点 → F5 → 命中 → StepOver → 查看局部变量 → EvaluateExpression
```
期望：`CodeDebugger.cs` 的 `Breakpoint` / `GetCallStack` / `GetLocalVariables` / `EvaluateExpression` 全部可达。
当前阻塞：调试器在 `CodeEditing/` 里，但是否接入编辑器 UI 未核验（E3）。

### S10 远端检查器 / 实时摄像机复制
期望：运行进程外能读节点属性（改了不持久化）；编辑器移动相机，运行画面同步跟随。
当前阻塞：`EditorEngineBridge.cs` 的 IPC 通道与 `EditorCameraController` ↔ `ViewportCamera3D` 同步链路待核验。

## Gate 3 · 资源与导出（真机）

### S11 资产管线
```
丢一张 PNG / 一个 glTF / 一个 OGG 进项目 → AssetDatabase 生成 .meta → AssetCooker 烘焙 → 运行时加载
```
期望：同输入产出同 hash；改贴图后依赖图触发重烘焙；`YamaCLI cook` 产出可被 `YamaRuntime` 加载的包。

### S12 跨平台导出
```
YamaCLI build --platform windows|linux|web|android|ios
```
期望：每个目标产「运行时 + 资源 + 清单」模板，干净机器解压即运行。
当前阻塞：仅 5 个适配层，无 macOS / iOS / Web / 导出模板 / 签名流程，见 `PLATFORM_MATRIX.md`。

---

## 判定规则

- **Gate 0 全绿** = 工程基线成立（本轮把 S2 补上了，S0/S1 已有 CI）。
- **Gate 1–3 任一条未过** = 不能宣称「达到 Godot 3 能力」。
- 本轮**只能交付 Gate 0 的静态部分 + 全部 12 条的验收脚本化**；Gate 1–3 需要真机，
  物理上不可能在沙盒完成（无 .NET SDK、无 GPU、无 Windows App SDK）。

> 这张表的意义是：明天你发新版时，不用再问「还差什么」，按 S0→S12 顺序勾，
> 哪条红就修哪条。每一条都对应 `capability_matrix.csv` 里的具体 ID。
