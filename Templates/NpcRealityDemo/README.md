# NpcRealityDemo 模板

把分支 `feat/npc-reality-core` 的 24 个 `.cs` 暴露成一个可演示场景（矩阵 G3-071）。

**前置**：分支 B 的 `npc-reality-ci.yml` 转绿后才可用；本模板本身只是数据，不参与编译。

组件约定（待分支 B 接线时映射到真实类型）：

- `RealityWorld` → `YamaEngine.Physics/Reality/RealityWorld.cs`（能量/质量/元素三重守恒）
- `NpcRuntimeAgent` → `YamaEngine.NPC/Runtime/NpcRuntimeCore.cs`

**验收**：S2 静态审计 → 分支 B CI 绿 → 用本模板跑 60 帧，检查守恒门禁无告警、
NPC 对物理事件（燃烧/爆裂）产生感知信念。
