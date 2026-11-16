# YamaEngine × Kimodo × MOSS-Live

AI NPC 正式接入层：**Kimodo 负责骨骼动作生成，MOSS-Live 负责全双工语音/文本互动**，二者在引擎侧由同一个状态机融合。

```
玩家语音/文本
   │
   ▼
MossLiveClient ── WebSocket ──► MOSS-Live（或本地 gateway）
   │  text / emotion / viseme / action
   ▼
AiNpcAgent（状态机：Listening → Thinking → Speaking ⇄ Acting）
   │  action(prompt + constraints)
   ▼
KimodoMotionClient ── HTTP ──► kimodo_service ──► Kimodo (NV diffusion model)
   │  rotations / root_positions / skeleton
   ▼
KimodoMotionConverter ──► AnimationClip + Skeleton
   │
   ▼
AnimationRuntime.PlayAnimation + PosePostProcess（viseme 下颌覆盖）
   │
   └─► action_result 回传 MOSS-Live（对话知道"动作做完了"）
```

## 目录

| 路径 | 说明 |
|---|---|
| `YamaEngine/NPC/AiNpc/` | 引擎侧接入层（配置/协议/客户端/转换器/Agent/运行时） |
| `integrations/kimodo_service/` | Kimodo HTTP sidecar（BVH/NPZ → 引擎 JSON 协议，含 mock 后端） |
| `integrations/moss_live_gateway/` | MOSS-Live WebSocket 网关（mock / passthrough / http） |
| `ai_npc.json` | 默认配置（引擎从工作目录读取） |
| `YamaEngine.Tests/AiNpcIntegrationTests.cs` | 6 个融合测试（协议/转换/HTTP/WebSocket/状态机/打断） |

## 快速启动

```powershell
# 1) 动作服务（无 GPU 时自动使用 mock 后端）
cd integrations/kimodo_service
pip install -r requirements.txt
uvicorn server:app --host 127.0.0.1 --port 8088

# 2) 互动网关（联调用 mock）
cd integrations/moss_live_gateway
pip install -r requirements.txt
uvicorn server:app --host 127.0.0.1 --port 8091

# 3) 引擎（工作目录含 ai_npc.json 即自动加载）
```

引擎侧创建 NPC：

```csharp
var runtime = new AiNpcRuntime(AiNpcConfig.Load("ai_npc.json"), animationRuntime);
var agent = runtime.CreateAgent(npcCharacter, sceneObject); // 自动连接 MOSS-Live
runtime.Update(dt); // 每帧，在 AnimationRuntime.Update 之后调用
```

或直接给 Agent 下发动作：

```csharp
agent.RequestAction("wave", "NPC 面向玩家挥手，脚部稳定", 2.5f, reportToMoss: true);
```

## Kimodo 接入

`kimodo_service` 调用官方 CLI（占位符可配）：

```
KIMODO_CLI_TEMPLATE='kimodo generate --prompt "{prompt}" --duration {duration} --fps {fps} --seed {seed} --model {model} --output {output} --bvh'
```

- 支持 `root2d / fullbody / end-effector` 约束（与官方 constraints JSON 一致，Y-up、米制）；
- 优先读 NPZ 的 `root_positions` / `foot_contacts`，骨架与局部旋转从 BVH 解析（厘米自动转米）；
- 无 CLI / 无权重 / 生成失败时自动降级 mock，并返回 `warning` 字段；
- 动作在引擎侧按 `bone_map` 重定向到既有角色骨架，根位移可选择写入 Hips 轨道或直接驱动角色 Transform。

> Kimodo 依赖 NVIDIA GPU 与模型权重，使用前请确认其许可证与显存要求（官方仓库 `nv-tlabs/kimodo`）。

## MOSS-Live 接入

引擎协议见 `YamaEngine/NPC/AiNpc/MossLiveProtocol.cs`：

- 上行：`session_start` / `audio_in`(PCM16 base64) / `text_in` / `interrupt` / `action_result` / `context`
- 下行：`ready` / `state` / `speech_started|ended` / `text_delta|done` / `audio` / `viseme` / `emotion` / `action` / `interrupt` / `error`

网关模式：

| `MOSS_BACKEND` | 用途 |
|---|---|
| `mock` | 本地完整闭环：文本 → 回复 → viseme → action，用于联调/CI |
| `passthrough` | 与官方/自建 MOSS-Live WebSocket 双向透传（要求对端使用同一协议） |
| `http` | 过渡：OpenAI 兼容 `/chat/completions` 生成文本，再由网关合成 viseme/action |

⚠️ **重要**：MOSS-Live（Mosi Intelligence 的全双工实时交互系统）目前尚未公开提供可直接调用的官方端点/权重；已开源的是 MOSS-VL-Realtime（11B 流式视频理解）。因此本接入采用"**协议先行 + 网关可替换**"策略：引擎侧协议已固定并测试通过，官方端点就绪后只需在 `passthrough` 模式补字段映射，无需改引擎代码。

## 验证

```powershell
dotnet build YamaEngine.Tests/YamaEngine.Tests.csproj
dotnet artifacts/bin/YamaEngine.Tests/net10.0-windows10.0.19041.0/YamaEngine.Tests.dll
```

AI NPC 用例已放在测试入口最前（历史物理用例在本机仍会先失败，属既有问题）。
