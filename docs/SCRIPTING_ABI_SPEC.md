# 脚本能力规格：GDScript 等价物与绑定 ABI

> 状态：**规格文档，不是实现**。Godot 3.5 有 GDScript（类 Python、可选静态类型）、C#、VisualScript、
> C++ GDNative，以及社区 Rust/Nim/Python/JS 绑定。Yama 目前有 Roslyn C# 脚本 + 双轨 VisualScript +
> C++ vcxproj 桥接，**缺 GDScript 等价物**。本文定义它的边界与可落地路径，避免直接堆未编译 VM。

## 1. 为什么本轮不写 VM

沙盒无 `dotnet` / `csc` / `mono`，任何新增 `.cs` 都**无法编译验证**；而远端 `main` 是
「0 错误 0 警告 + 1559 用例」的基线。往 `Scripting/` 里塞一个未经编译的解释器，
等于把整个 CI 门禁重新变成不确定状态。所以本轮只交付：

- 能力差距的**精确定义**（矩阵 G3-053）
- 绑定 ABI 的**规格**（本文）
- 验收方式（S7 / S8）

实现留到有真机编译环境时，按本文分阶段落地。

## 2. 目标分层（对齐 Godot 3）

| 层 | Godot 3 | Yama 现状 | 本轮动作 |
|---|---|---|---|
| L1 语言 | GDScript / C# / VisualScript / C++ | C#(Roslyn) ✅、VisualScript 部分、C++ vcxproj 部分、GDScript ❌ | 定义 L1 规格 |
| L2 绑定 ABI | Object/Node/Resource/信号/属性 | `ScriptComponent` / `ScriptEventBus` / `ScriptRegistry` ✅ | 写清映射表 |
| L3 生命周期 | `_ready/_process/_physics_process` | `ScriptEventBus` 需核验 | 规格 |
| L4 热重载 | 脚本热重载 + 状态保持 | `PluginSystem.ScriptHotReloader` ✅（双轨待收敛） | `BUILD_DECISIONS` D1 |
| L5 调试 | GDScript 调试器 | `CodeEditing.CodeDebugger` ✅（接线待核验） | `debug-protocol.schema.json` |

## 3. 最小 ABI（若后续实现 GDScript 等价物）

```
YamaScript (语言层, 可选)
  └── ScriptBinding (ABI 层, 必须)
        ├── NodeRef        : 引用场景节点，不持有原生指针
        ├── PropertyBag    : Get/Set(string) → variant
        ├── SignalBus      : Connect/Disconnect/Emit(string, args)
        ├── LifecycleHooks : OnReady/OnProcess(dt)/OnPhysics(dt)/OnDestroy
        └── ScriptRegistry : 类型名 → 工厂，供序列化还原
```

**硬约束**

1. ABI 只暴露 `variant`（bool/int/float/string/Vector2/3/4/NodeRef），不暴露引擎内部类型。
2. 脚本不能持有非托管内存；所有跨边界对象由 `NodeRef` + 句柄表管理。
3. 热重载前后 `PropertyBag` 的 `[SerializeField]` 键必须逐项迁移（`StateMigrator` 已有实现）。
4. 编译失败保留旧域，不中断运行（Godot 3 语义）。
5. 调试消息遵循 `schemas/debug-protocol.schema.json`，复用现有 `CodeDebugger` 事件。

## 4. 为什么不直接用 C# 脚本充当 GDScript

可以，但要明确代价：Roslyn 每次变更都要增量编译 + ALC 卸载，冷启动比 GDScript 解释器慢；
且 C# 对非程序作者不够友好。若目标是「Godot 3 级完整能力」，
建议路径是 **先把 L2 绑定 ABI 做实（零风险，纯 C# 数据类）→ 再接 Roslyn → 最后才考虑轻量 DSL**。

## 5. 验收

- S7：挂脚本 → 立方体旋转（L1+L2+L3）
- S8：运行中改常量 → 行为变化 + 字段保持（L4）
- S9：断点 / 单步 / 变量 / 表达式求值（L5）

三条全过，才算拿到 Godot 3 的「脚本」能力票。本轮交付的是这三张票的**判定标准**。
