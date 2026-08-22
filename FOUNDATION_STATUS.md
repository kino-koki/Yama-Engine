# 山雨引擎 — 工程地基状态清单（FOUNDATION STATUS）

> 生成日期：2026-08-07
> 目的：提升工程成熟度（对标 UE5.8 的第一阶段：先把地基打牢）
> **验证声明**：本清单基于静态代码扫描 + 源码阅读生成。当前工作环境**未安装 .NET SDK**，
> 因此所有编译/运行相关结论**未经实际编译验证**。任何"已修复"仅指静态层面的正确性修复。

---

## 0. 为什么先做地基，而不是直接加 UE5.8 特性

UE5.8 的完整功能集（Nanite / Lumen / Niagara / Chaos / Control Rig / MetaSound /
World Partition / Mass / GAS / Iris 等）是大团队多年累积的成果。本项目当前是
~50 万行 C#、算法骨架广但成熟度偏低的原型（渲染管线从未在真实 GPU 跑过完整 PBR 帧、
单体巨库、历史损坏文件）。在"能否稳定构建/运行"都没解决前堆特性，只会放大已有的
"看似生产级、实则未验证"问题。故第一阶段只做四件事：

1. 可复现构建（CI + 本地脚本）
2. 消除机器相关的硬编码路径（可移植性）
3. 真实状态清单（本文档）
4. 规划程序集拆分（提案，暂不执行，见 §4）

---

## 1. 构建前置条件（在能编译的机器上）

| 依赖 | 版本 | 说明 |
|---|---|---|
| .NET SDK | 8.0.422（global.json 锁定） | 必须，否则无法还原/构建 |
| Visual Studio 2022 | 17.8+ | 含"桌面开发"与 C++ 工作负载（桥接工程需要） |
| Windows App SDK | 1.8+ | ShanyuEditor (WinUI 3) 依赖 |
| Vulkan 驱动 | 1.3+（推荐 1.4） | 运行时渲染需要 |
| 平台 | Windows 10/11 x64 | 当前唯一目标平台 |
| Assimp（C++ 桥接） | 源码放 `third_party\assimp` 或设 `SHANYU_THIRD_PARTY` 环境变量 | ShanyuEngineBridge 依赖 |

---

## 2. 已核实：当前是干净的（相比早期报告已大幅改善）

| 检查项 | 结果 | 说明 |
|---|---|---|
| 主引擎 `ShanyuEngine.csproj` 含 `E:\` 硬编码 | ✅ 无 | 之前的目录重定向（artifacts/）修复已生效 |
| `UseD3D12` 残留 | ✅ 仅旧报告文档提及，代码已无 | `ShanyuRuntime/Program.cs` 已用 `UseVulkan` |
| `csproj <Compile Remove>` 数量 | ✅ 从 25 降到 **4** | 见 §3 |
| 之前损坏的 `NetworkLobbySystem.cs` | ✅ 已重写为 909 行干净实现 | 重新纳入构建 |
| 之前损坏的 `ScriptEngine.cs` | ✅ 已重写为 980 行干净 Roslyn 引擎 | 重新纳入构建 |
| `NotImplementedException` | ✅ 0 | 全量扫描结果（见 CODE_TRUTH_REPORT.md） |
| `artifacts/` 目录 | ✅ 已被 .gitignore | 其中的 `E:\nuget` 路径是**生成的构建缓存**，非源码，下次 restore 自动重生 |

---

## 3. 仍被 `<Compile Remove>` 排除的文件（技术债清单）

`ShanyuEngine.csproj` 中当前 4 项：

| 文件 / 模式 | 排除原因 | 建议 |
|---|---|---|
| `RHI\D3D12\**\*.cs` | D3D12 后端已废弃，统一用 Vulkan | 保留（有意） |
| `Rendering\**\*SparseShadowAtlases*.cs` | 开发中 | 完成后移除排除 |
| `Scripting\VisualScriptingSystem.cs` | 与 `NodeScriptSystem.cs` 类型重复 | 二选一并移除排除 |
| `Build\BuildPipeline.cs` | 是 `BuildPipelineReal.cs` 的早期简版 | 删除旧文件或移除排除 |

---

## 4. 地基阶段已落地 / 待办

### 4.1 本次已完成的静态修复（未经编译验证）

| 文件 | 改动 | 风险 |
|---|---|---|
| `ShanyuEngineBridge/ShanyuEngineBridge.vcxproj` | Assimp 路径由写死 `E:\shanyu_third_party` 改为 `$(SHANYU_THIRD_PARTY)` 环境变量优先、回退 `$(SolutionDir)..\third_party\assimp` | 低（标准 MSBuild 属性间接引用） |
| `ShanyuVulkanSandbox/ShanyuVulkanSandbox.csproj` | 删除写死 `E:\nuget` 的 `HintPath` 引用，改为 `PackageReference` 还原 Silk.NET 2.22.0（与主引擎一致） | 低（独立工程，不在主 .sln） |
| `ShanyuVulkanSandbox/InspectTypes.cs` | 三个 `Assembly.LoadFrom(@"E:\nuget\...")` 改为从输出目录 `AppContext.BaseDirectory` 解析 | 低（独立诊断工具） |
| `.github/workflows/build.yml`（新增） | CI：还原→构建→测试→上传 artifacts | 新增文件，零风险 |
| `scripts/build_verify.ps1`（新增） | 本地可复现构建+测试+真实报告+技术债汇总 | 新增文件，零风险 |

### 4.2 仍需人工/真机验证

- [ ] 在装有 .NET 8 SDK 的机器跑一次 `scripts/build_verify.ps1`，把**真实错误清单**固化。
- [ ] 推送后让 `build.yml` 跑一次，确认 WinUI 3 / Windows App SDK 目标可用。
- [ ] 在真机（有 Vulkan 1.4 显卡）启动编辑器，确认视口能渲出完整 PBR 场景（目前仅验证过三角形）。
- [ ] `ShanyuEngineBridge` 用新路径指示成功编译（需 Assimp 就位）。

### 4.3 已知但本次未改（低优先级 / 需决策）

- `ShanyuEngine/DCC/DCCBridge.cs` 第 565-598 行：写死 `C:\Program Files\...\blender.exe` 等。
  这是 Windows 上探测 DCC 安装位置的常见做法，但应改为可配置（注册表/环境变量/设置项）。
- `ConsoleSDKAbstraction.cs` 含 56 处 TODO（主机 SDK 抽象，本就为占位接口，无 NDA 风险）。
- 早期失真错误日志（`all_errors.txt` / `build_errors.txt` 等 5 个文件）仍留在仓库根目录，
  引用的错误在当前源码中已不存在，**建议确认后删除**（会严重误导后续维护者）。

---

## 5. 提案：程序集拆分（暂不执行）

当前 `ShanyuEngine` 是单一巨型库（756 文件编译进一个 `Library`），无程序集边界，
任意子系统可互相直接耦合，难以独立测试/演进。建议按内聚性拆分为：

| 目标程序集 | 内容 |
|---|---|
| `ShanyuEngine.Core` | Math / Core / ECS / RHI 抽象 / Platform |
| `ShanyuEngine.Rendering` | Rendering / Vulkan / MicroCluster / Shaders |
| `ShanyuEngine.Physics` | Physics |
| `ShanyuEngine.Animation` | Animation / Skinning |
| `ShanyuEngine.AI` | AI |
| `ShanyuEngine.Audio` | Audio |
| `ShanyuEngine.Gameplay` | Gameplay / GameFramework / Scripting / VisualScript |
| `ShanyuEngine.World` | World / Particles / VFX / Modeling |
| `ShanyuEngine.Network` | Network / Networking |
| `ShanyuEngine.Editor` | UI / ImGui 面板 |
| `ShanyuEngine`（组合根） | 引用以上，提供 `EngineApplication` 入口 |

**为何现在不执行**：拆分 756 个文件需要编译验证（本环境无 SDK），且可能破坏内部引用。
应待 §4.2 的 CI 跑通、有了"真实错误清单"这一单一事实来源后，再分批迁移，由 CI 捕获回归。

---

## 6. 下一步建议（地基阶段收尾后，再进入特性阶段）

1. 跑通 `build_verify.ps1` 与 CI，拿到真实错误清单。
2. 清理 §3 的 4 个 `<Compile Remove>`（优先 SparseShadowAtlases、VisualScriptingSystem、BuildPipeline）。
3. 执行 §5 程序集拆分（分批 + CI 守护）。
4. 真机验证渲染闭环（完整 PBR 场景）。
5. 之后才按 UE5.8 能力地图，逐个模块做"真能跑、真能测"的实现。
