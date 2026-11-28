# 脚本能力规格：GDScript 等价物与绑定 ABI

> 状态（第二十二轮更正）：**L2 已实现**，L3 已实现；L1 的 GDScript /
> YamaScript 等价物**仍未实现**，本轮也不实现 VM。
>
> 更正说明：本文前一版把状态误写成「规格、未实现」，并以「本地缺少编译器」
> 作为不落地的理由，还声称只交付判定标准。这与仓库现状不符——
> `YamaEngine/Scripting/Abi/*.cs` 早已落地，`YamaEngine.Tests/ScriptAbiTests.cs`
> 的 7 项断言由 `build.yml` 的 Test step（.NET 10 / Windows）每次真实执行。
> 属于与第二十轮 `fetch_codeoss.py` 同类的**文档口径回漂**，本轮如实更正，
> 不把已经做到的事说成没做，也不把没做到的事说成做到。
> **L2 已实现**，CI 有 .NET 10 编译器，不存在「等真机环境」这回事。

## 1. 分层现状（对齐 Godot 3）

| 层 | Godot 3 | Yama 现状 | 状态 |
|---|---|---|---|
| L1 语言 | GDScript / C# / VisualScript / C++ | C#(Roslyn) ✅、VisualScript 部分、C++ vcxproj 部分、**GDScript/YamaScript 等价物 ❌** | 缺 DSL/VM，未实现 |
| **L2 绑定 ABI** | Object/Node/Resource/信号/属性 | `ScriptBinding` / `NodeRef`+`NodeHandleTable` / `PropertyBag` / `SignalBus` / `LifecycleDispatcher` / `ScriptRegistry` / `ScriptVariant` | ✅ **已实现，全在 `YamaEngine/Scripting/Abi/`** |
| L3 生命周期 | `_ready/_process/_physics_process` | `IScriptLifecycle` + `LifecycleDispatcher`（单脚本抛错隔离） | ✅ 已实现 |
| L4 热重载 | 脚本热重载 + 状态保持 | `PropertyBag.Migrate` / `ScriptBinding.CaptureState`+`RestoreState`，`ScriptHotReloader`（双轨待收敛） | ABI 侧已实现 |
| L5 调试 | GDScript 调试器 | `CodeEditing.CodeDebugger` 接线待核验 | 未闭合 |

L2 的确定性自检：`YamaEngine.Tests/ScriptAbiTests.cs`（7 项）——
variant 往返、句柄表生命周期、PropertyBag 迁移与反射捕获、
SignalBus 抛错隔离、LifecycleDispatcher 抛错隔离、Registry+Binding 还原。

## 2. 第二十二轮补的是 L1↔L2 的声明接线，不是 VM

扫描发现：`.yama` 场景的 `Components` 只有 `CelPhysics2D` / `TileMap` /
`Camera` / `Light` 等内置组件，`yscene.schema.json` 的 `Components.items`
是裸 `{"type":"object"}`，**没有任何脚本绑定声明**。也就是说
「用 YAMA 开发游戏」在此之前只能摆场景，不能声明行为。

本轮的做法（**不动 `SceneSerializer` 的强类型组件路径，零回归**）：

- 独立 `schemas/yama-script.schema.json` 定义声明格式；
- 独立声明文件 `Templates/*/Content/Scripts/*.yama`，不塞进场景 `Components`；
- `YamaEngine/Scripting/Abi/YamaScriptDeclaration.cs` 把声明**真实还原**成
  `ScriptBinding`：类型名未注册即 fail-closed、节点走 `NodeHandleTable`、
  属性逐项灌入 `PropertyBag`、生命周期只对真正实现 `IScriptLifecycle`
  的实例注册、信号只声明连接点不谎报已连接；
- 项目契约 `yama.project.json` 新增 `scriptBinding: "Content/Scripts"`，
  由 `scripts/check_yama_project.py` 离线校验。

**明确边界**：这是**声明式绑定**，不是 VM、不是 DSL 解释器、不执行字节码；
`language-profile.json` 的 `scriptBinding.vmImplemented=false` 与
`contract.json` 的 `script_binding.vm_implemented=false` 都是硬声明。
不谎报「YAMA 脚本语言已实现」。

## 3. 最小 ABI（已实现）

```
YamaScript (语言层, 仍未实现 VM/DSL)
  └── 声明层 YamaScriptDeclaration (第二十二轮)
        └── ScriptBinding (ABI 层, 已实现)
              ├── NodeRef        : 引用场景节点，不持有原生指针
              ├── PropertyBag    : Get/Set(string) → variant
              ├── SignalBus      : Connect/Disconnect/Emit(string, args)
              ├── LifecycleHooks : OnReady/OnProcess(dt)/OnPhysics(dt)/OnDestroy
              └── ScriptRegistry : 类型名 → 工厂，供反序列化还原
```

**硬约束（声明层逐条锁成断言）**

1. ABI 只暴露 `variant` 子集：bool / int32 / int64 / single / double /
   string / Vector2 / Vector3 / Vector4。`Object` / `Array` / `Dictionary` /
   `ResourceRef` / `NodeRef` 一律拒绝（`object`、`array` 等非法 kind 由
   `TestR22IllegalKindIsRejected` 红灯）。
2. 脚本不持有非托管内存；所有跨边界对象由 `NodeRef` + 句柄表管理。
3. 热重载前后 `PropertyBag` 的 `[ScriptSerializeField]` 键逐项迁移
   （`PropertyBag.Migrate` 已实现，`ScriptAbiTests` 已验）。
4. 单脚本抛错被隔离，不中断整帧循环（`LifecycleDispatcher` 已实现）。
5. 调试消息遵循 `schemas/debug-protocol.schema.json`（接线待核验，未闭合）。

## 4. 为什么不直接用 C# 脚本充当 GDScript

Roslyn 每次变更都要增量编译 + ALC 卸载，冷启动比解释器慢，且对非程序作者
不友好。若目标是「Godot 3 级完整能力」，路径仍是
**L2 做实（已完成）→ 接 Roslyn（部分已有）→ 最后才考虑轻量 DSL**。
本轮没有跨过最后一步，也不假装跨过。

## 5. 验收

- S7：挂脚本 → 立方体旋转（L1+L2+L3）：**L2/L3 已具备，L1 未闭合**
- S8：运行中改常量 → 行为变化 + 字段保持（L4）：**ABI 侧已具备**
- S9：断点 / 单步 / 变量 / 表达式求值（L5）：**未闭合**

本轮新增的 `YamaScriptBindingTests.cs` 12 项 + 1 个程序集级 `[Fact]`，
验的是「声明 → ABI 还原」这条**已经真实存在的链路**，不是给未实现的 VM 开票。
