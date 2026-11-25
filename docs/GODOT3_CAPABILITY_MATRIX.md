# Godot 3.5 原子能力矩阵（Yama-Engine 逐项核验）

> 生成：2026-11-24　基准：`main@3411fcf2a5ab`（远端 tree 实测 1480 条目 / 1037 个 `.cs` / 40 篇 md）  
> 对照：Godot 3.5 官方《特性列表》 https://docs.godotengine.org/zh-cn/3.x/about/list_of_features.html  
> 方法：逐项落到**远端真实文件路径 + 符号证据**，由 `scripts/capability_audit.py` 机器复核。

## 状态码定义

| 码 | 含义 |
|---|---|
| `已实现` | 远端有对应实现且符号可定位 |
| `已验证` | 有真机 / 截图 / CI 输出佐证 |
| `已实现·未端到端` | 代码在，但没跑过完整工作流 |
| `部分` | 有骨架或局部，未达 Godot 3 完整度 |
| `未验证` | 算法原型，README 自述未在真实 GPU 验证 |
| `缺失` | 远端无对应实现 |
| `占位·NDA` | 有占位接口，实现受厂商授权限制 |
| `N/A` | Godot 3.5 本身也不支持，不算缺口 |

## 总览

| 状态 | 项数 |
|---|---:|
| `已实现` | 31 |
| `部分` | 21 |
| `缺失` | 9 |
| `未验证` | 9 |
| `部分·双轨` | 2 |
| `已实现·未端到端` | 1 |
| `占位·NDA` | 1 |
| `已实现·未接线` | 1 |
| `N/A` | 1 |
| `已实现·部分验证` | 1 |
| `已验证` | 1 |
| `阻塞·真机` | 1 |
| `未验证·分支B` | 1 |
| `部分·分支A` | 1 |

**结论**：Yama 的差距不是「没有模块」，而是 **① 接线未核验 ② 端到端未验证 ③ 少数真实缺失（GLES 双渲染器 / GDScript 等价 / 平台导出）**。
矩阵里 **71 / 81** 项能定位到远端真实文件；纯文本工作能闭环的是「规格 / schema / 模板 / 决策 / 审计门禁」，
其余必须真机、GPU 或第三方授权，见 `docs/E2E_ACCEPTANCE.md` 与 `docs/GAP_ANALYSIS_CORRECTED.md`。

## 逐项矩阵

| ID | 类别 | Godot 3.5 能力 | Yama 对应文件（远端实测） | 状态 | 本轮动作 |
|---|---|---|---|---|---|
| G3-001 | 平台 | Windows 7+ 编辑器与导出(32/64) | `YamaEngine/Platform/Win32Window.cs`；`YamaEditor/YamaEditor.csproj`；`YamaEditor/App.xaml.cs` | `已实现·未端到端` | 写入平台矩阵；补导出模板清单 |
| G3-002 | 平台 | macOS 10.12+ (x86/ARM) | `YamaEngine/Platform/SDL2Window.cs` | `缺失` | 标为 P1 缺口；不伪造支持 |
| G3-003 | 平台 | Linux (x86/ARM) | `YamaEngine/Platform/LinuxAdapter.cs`；`YamaEngine/Platform/SDL2Window.cs` | `部分` | 平台矩阵标注未验证 |
| G3-004 | 平台 | HTML5 / WebAssembly | `YamaEngine/Platform/PlatformAbstractionLayer.cs` | `缺失` | 明确列为需第三方/自研后端 |
| G3-005 | 平台 | Android 4.4+ | `YamaEngine/Platform/AndroidAdapter.cs` | `部分` | 标未出包验证 |
| G3-006 | 平台 | iOS 10+ | `YamaEngine/Platform/PlatformAbstractionLayer.cs` | `缺失` | 列为缺口 |
| G3-007 | 平台 | 主机(PS5/Xbox/Switch) | `YamaEngine/Platforms/ConsoleSDKAbstraction.cs` | `占位·NDA` | 决策文档：条件编译隔离，默认不编入 |
| G3-008 | 平台 | 无头 Linux/macOS 服务器 | `YamaCLI/YamaCLI.csproj`；`scripts/build_verify.ps1` | `已实现` | 纳入 E2E S1 |
| G3-009 | 平台 | 导出模板(每平台一份) | `Templates/Basic3D/yama.project.json` | `部分` | 本轮补 6 平台 template.json |
| G3-010 | 平台 | 签名 / 商店分发 | — | `缺失` | 列入生态路线图 |
| G3-011 | 编辑器 | 场景树编辑器 | `YamaEngine/Editor/Panels/OutlinerPanel.cs`；`YamaEngine/Editor/EditorSceneController.cs` | `已实现` | 接线核验 E1 |
| G3-012 | 编辑器 | 内置脚本编辑器 | `YamaEngine/CodeEditing/CodeEditorView.cs`；`YamaEngine/Editor/Panels/CodeEditorPanel.cs` | `已实现` | 接线核验 E2 |
| G3-013 | 编辑器 | 脚本调试器(断点/单步/栈/变量) | `YamaEngine/CodeEditing/CodeDebugger.cs` | `已实现·未接线` | 补调试协议 schema；接线核验 E3 |
| G3-014 | 编辑器 | 线程内调试 | — | `N/A` | 标注 N/A，不算缺口 |
| G3-015 | 编辑器 | 性能监控工具 | `YamaEngine/Editor/Panels/StatsPanel.cs`；`YamaEngine/Diagnostics/CPUProfiler.cs`；`YamaEngine/Diagnostics/GPUProfiler.cs`；`YamaEngine/Diagnostics/Fr… | `已实现` | 接线核验 E4 |
| G3-016 | 编辑器 | 脚本热重载 | `YamaEngine/PluginSystem/ScriptHotReloadSystem.cs`；`YamaEngine/Scripting/ScriptHotReload.cs` | `部分·双轨` | BUILD_DECISIONS D1；补接线规格 |
| G3-017 | 编辑器 | 场景热编辑(运行期改动保留) | `YamaEngine/Editor/EditorSceneController.cs` | `未验证` | 接线核验 E5 + E2E S4 |
| G3-018 | 编辑器 | 远程检查器 | `YamaEngine/Editor/EditorEngineBridge.cs` | `部分` | 补 remote-inspector 协议规格 |
| G3-019 | 编辑器 | 实时摄像机复制 | `YamaEngine/Editor/EditorCameraController.cs`；`YamaEditor/Controls/ViewportCamera3D.cs` | `部分` | 接线核验 E7 |
| G3-020 | 编辑器 | 内置离线类参考文档 | `ARCHITECTURE.md`；`USER_GUIDE.md` | `部分` | 本轮补 docfx 规格 + XML 注释生成脚本 |
| G3-021 | 编辑器 | 多语言编辑器(社区数十种) | `YamaEngine/Localization/StringTableSystem.cs`；`YamaEngine/Localization/RTLTextSystem.cs`；`YamaEngine/Localization/PluralRulesSystem.cs` | `部分` | 标中/英双语最小目标 |
| G3-022 | 编辑器 | 插件 / 资产库 | `YamaEngine/PluginSystem/PluginMarketSystem.cs`；`YamaEngine/PluginSystem/PluginCoreSystem.cs`；`integrations/README.md` | `已实现` | 接线核验 E9 |
| G3-023 | 渲染 | OpenGL ES 2.0 (移动/Web 推荐) | — | `缺失` | 平台矩阵标为阻断 Web/低端移动 |
| G3-024 | 渲染 | OpenGL 2.1 (桌面推荐) | — | `缺失` | 同上 |
| G3-025 | 渲染 | Vulkan 1.3/1.4 后端 | `YamaEngine/Vulkan/VulkanDevice.cs`；`YamaEngine/Vulkan/VulkanSwapchain.cs`；`YamaEditor/Controls/TriangleRenderer.cs` | `已实现·部分验证` | 渲染台账 R1 |
| G3-026 | 渲染 | 第一个三角形端到端 | `YamaEditor/Controls/TriangleRenderer.cs` | `已验证` | 纳入 E2E S5 作为基线 |
| G3-027 | 渲染 | PBR 材质 | `YamaEngine/Rendering/AdvancedPBRShading.cs` | `未验证` | 渲染台账 R2 |
| G3-028 | 渲染 | 级联阴影 | `YamaEngine/Rendering/CascadeShadowMapRenderer.cs` | `未验证` | 渲染台账 R3 |
| G3-029 | 渲染 | MicroCluster 虚拟几何 | `YamaEngine/Rendering/MicroClusterVirtualGeometry.cs` | `未验证` | 渲染台账 R4 |
| G3-030 | 渲染 | ReSTIR / SVGF / 路径追踪 | `YamaEngine/Rendering/RayTracedShadows.cs` | `未验证` | 渲染台账 R5 |
| G3-031 | 渲染 | 体积雾 / 大气 | `YamaEngine/Rendering/AtmosphereRenderer.cs` | `未验证` | 渲染台账 R6 |
| G3-032 | 渲染 | 后处理 / 色调映射 | `YamaEngine/Rendering/ColorGradingSystem.cs` | `未验证` | 渲染台账 R7 |
| G3-033 | 渲染 | RenderGraph | `YamaEngine/Rendering/RenderGraphV2.cs` | `未验证` | 渲染台账 R8 |
| G3-034 | 渲染 | 运行时原生崩溃 0xC0000005 | `YamaEngine/Diagnostics/CrashReporter.cs`；`docs/RUNTIME_CRASH_DIAGNOSIS.md` | `阻塞·真机` | E2E S6：抓 minidump |
| G3-035 | 2D | 专用 2D 像素坐标系 | `YamaEngine/Cel2D/CelGame2D.cs`；`YamaEngine/Cel2D/CelRenderLayers2D.cs` | `部分` | 2D 规格 + Basic2D 模板 |
| G3-036 | 2D | 精灵 / Polygon2D / Line2D | `YamaEngine/Cel2D/CelSpriteRenderer.cs`；`YamaEngine/Cel2D/Cel9Slice.cs` | `部分` | 2D 规格 |
| G3-037 | 2D | AnimatedSprite | `YamaEngine/Cel2D/CelAnimationStateMachine2D.cs`；`YamaEngine/Cel2D/CelSkeletalAnimation.cs` | `已实现` | 2D 模板内含动画帧 |
| G3-038 | 2D | 视差层 / 伪 3D | `YamaEngine/Cel2D/CelRenderLayers2D.cs` | `部分` | 2D 规格 |
| G3-039 | 2D | YSort | `YamaEngine/Cel2D/CelSpriteSorter2D.cs` | `已实现` | 2D 模板 |
| G3-040 | 2D | TileMap 编辑器 | `YamaEngine/Editor/LevelEditor/GridSnappingSystem.cs`；`YamaEngine/Editor/LevelEditor/TerrainEditor.cs` | `部分` | 本轮补 tileset.schema.json |
| G3-041 | 2D | TileSet 图块数据库 | — | `缺失` | 本轮补 schema + Basic2D 示例 |
| G3-042 | 2D | 2D 物理 | `YamaEngine/Cel2D/CelPhysics2D.cs`；`YamaEngine/Physics/CollisionDetectionReal.cs` | `已实现` | 2D 规格 |
| G3-043 | 2D | Camera2D 跟随 / 拖拽边界 | `YamaEngine/Cel2D/CelCameraFollow2D.cs` | `已实现` | 2D 模板 |
| G3-044 | 3D | 3D 节点与变换 | `YamaEngine/Scenes/SceneManager.cs`；`YamaEngine/GameFramework/GameFrameworkFull.cs` | `已实现` | 场景 schema |
| G3-045 | 3D | glTF / FBX / Blender 导入 | `YamaEngine/Assets/Importers/glTFImporter.cs`；`YamaEngine/Assets/Importers/FbxImporter.cs`；`YamaEngine/DCC/DCCBridge.cs` | `部分` | 接线核验 E10 |
| G3-046 | 3D | 骨骼 / 蒙皮 / 动画 | `YamaEngine/Animation/AnimStateMachineReal.cs`；`YamaEngine/Animation/AnimationRetargetingReal.cs` | `已实现` | — |
| G3-047 | 3D | PackedScene 实例化 | `YamaEngine/GameFramework/PackedScene.cs` | `部分` | 决策文档 D5 |
| G3-048 | 场景 | 场景序列化(.yama, JSON) | `YamaEngine/Scenes/SceneSerializer.cs`；`Templates/Basic3D/Content/Scenes/Main.yama` | `已实现` | 本轮补 yscene.schema.json |
| G3-049 | 资源 | 项目清单 yama.project.json | `Templates/Basic3D/yama.project.json` | `已实现` | 本轮补 yama-project.schema.json |
| G3-050 | 资源 | 自定义 Resource 系统 | `YamaEngine/Assets/AssetMeta.cs`；`YamaEngine/DataSystem/DataAssetSystem.cs` | `部分` | 资源规格 |
| G3-051 | 资源 | 资产数据库 / 依赖图 | `YamaEngine/Assets/AssetDatabase.cs`；`YamaEngine/Assets/AssetDependencyGraph.cs` | `已实现` | 接线核验 E11 |
| G3-052 | 资源 | 资产烘焙 / Cook | `YamaEngine/Assets/AssetCooker.cs`；`YamaEngine/Build/CookingSystem.cs` | `已实现` | E2E S7 |
| G3-053 | 资源 | 热更新 | `YamaEngine/Build/HotUpdateSystem.cs`；`YamaEngine/PluginSystem/ContentPatchSystem.cs` | `部分` | E2E S8 |
| G3-054 | 脚本 | GDScript 等价(类 Python) | — | `缺失` | 本轮补 SCRIPTING_ABI_SPEC.md(规格，不堆未编译 VM) |
| G3-055 | 脚本 | C# 脚本 (Roslyn) | `YamaEngine/Scripting/ScriptEngine.cs`；`YamaEngine/Scripting/ScriptAssemblyLoader.cs`；`YamaEngine/Scripting/ScriptComponent.cs` | `已实现` | 接线核验 E12 |
| G3-056 | 脚本 | VisualScript 节点图 | `YamaEngine/Scripting/NodeScriptSystem.cs`；`YamaEngine/VisualScript/NodeGraphSystem.cs` | `部分·双轨` | 决策文档 D2 |
| G3-057 | 脚本 | C++ 扩展 ABI | `YamaEngineBridge/YamaEngineBridge.vcxproj` | `部分` | 平台矩阵标注环境限制 |
| G3-058 | 脚本 | 信号 / 事件总线 | `YamaEngine/Scripting/ScriptEventBus.cs` | `已实现` | — |
| G3-059 | 脚本 | IntelliSense | `YamaEngine/CodeEditing/IntelliSenseEngine.cs` | `已实现` | — |
| G3-060 | 脚本 | 代码格式化 | `YamaEngine/CodeEditing/CodeFormatter.cs` | `已实现` | — |
| G3-061 | 脚本 | 状态迁移(热重载保字段) | `YamaEngine/PluginSystem/ScriptHotReloadSystem.cs` | `已实现` | 决策文档 D1 |
| G3-062 | GUI | 自适应布局 / 锚点 | `YamaEngine/UI/UmgWidgetSystem.cs` | `部分` | GUI 规格 |
| G3-063 | GUI | 主题系统 | `YamaEngine/UI/WidgetControls.cs` | `未验证` | GUI 规格 |
| G3-064 | GUI | 文本 / 字体 / RTL | `YamaEngine/Localization/RTLTextSystem.cs`；`YamaEngine/Localization/PluralRulesSystem.cs` | `已实现` | — |
| G3-065 | GUI | 控件库 | `YamaEngine/UI/WidgetControls.cs` | `部分` | 占位台账分类 |
| G3-066 | 音频 | 3D 空间音频 | `YamaEngine/Audio/Audio3DSpatialSystem.cs`；`YamaEngine/Audio/AudioSpatialReal.cs` | `已实现` | — |
| G3-067 | 音频 | DSP / 混音 / 卷积混响 | `YamaEngine/Audio/AdvancedAudioEngine.cs`；`YamaEngine/Audio/AudioDSPReal.cs`；`YamaEngine/Audio/AudioMixerReal.cs` | `已实现` | — |
| G3-068 | 音频 | Vorbis/FLAC/MP3 解码 | `YamaEngine/Audio/VorbisDecoder.cs`；`YamaEngine/Audio/AudioDecoderReal.cs` | `已实现` | 纳入 E2E S9 |
| G3-069 | 网络 | 状态复制 | `YamaEngine/Network/NetworkReplicationSystem.cs`；`YamaEngine/Networking/NetworkReplication.cs` | `已实现` | — |
| G3-070 | 网络 | 客户端预测 / 插值 | `YamaEngine/Network/NetworkInterpolator.cs`；`YamaEngine/Network/NetworkFramingReal.cs` | `部分` | — |
| G3-071 | 动画 | 动画状态机 / 混合树 | `YamaEngine/Animation/AnimStateMachineReal.cs`；`YamaEngine/Animation/BlendTreeReal.cs` | `已实现` | — |
| G3-072 | 物理 | GJK/EPA/SAT 碰撞 | `YamaEngine/Physics/CollisionDetectionReal.cs` | `已实现` | E2E S10 |
| G3-073 | 物理 | 物理化学守恒核心 | `YamaEngine/Physics/Reality/` | `未验证·分支B` | 等 CI 绿灯后合入 |
| G3-074 | 工程 | 0 错误 0 警告构建 | `YamaEngine/YamaEngine.csproj`；`.github/workflows/build.yml` | `已实现` | E2E S0 |
| G3-075 | 工程 | xUnit 回归门禁(1559) | `YamaEngine.Tests/XunitAdapter.cs`；`.github/workflows/build.yml` | `已实现` | E2E S0 |
| G3-076 | 工程 | 无头 smoke 60 帧 | `YamaCLI/YamaCLI.csproj` | `已实现` | E2E S1 |
| G3-077 | 工程 | 静态仓库守卫 | `scripts/path_guard.py`；`ci/repo-guards.yml` | `部分·分支A` | 本轮补 capability_audit.py |
| G3-078 | 工程 | 文档 / 手册 | `ARCHITECTURE.md`；`USER_GUIDE.md`；`TUTORIAL.md` | `已实现` | 本轮补矩阵与决策文档 |
| G3-079 | 工程 | 许可证 / 治理 | `LICENSE`；`SECURITY.md`；`CHANGELOG.md`；`NOTICE`；`CODE_OF_CONDUCT.md` | `部分` | 矩阵登记 |
| G3-080 | 工程 | 示例工程 | `Templates/Basic3D/` | `部分` | 本轮补 Basic2D / NpcRealityDemo |
| G3-081 | 生态 | 社区 / 发行 / 第三方验证 | — | `缺失` | ECOLOGY_ROADMAP.md |

### 证据与验收口径

每条的完整 `evidence`（符号/行数）与 `acceptance`（具体命令与期望输出）在 `docs/capability_matrix.csv`，
由 `scripts/capability_audit.py` 逐行复核文件存在性。

---

> 诚实声明：本矩阵为**静态核验**。标 `已实现` 不等于「跑通了 Godot 3 级完整工作流」；
> 编译状态（0 错误 / 0 警告 / 1559 用例）来自仓库 README 与 CI 配置，本次未在真机重新执行。
