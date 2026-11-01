# Vulkan 最小渲染闭环设计

## 目标
让 YamaEngine 在真实 GPU 上渲染一个彩色三角形，并通过烟测验证。

## 现状
- 2918 个编译错误集中在 25 个文件（其余数百文件正常）
- Vulkan RHI 骨架已有 16 个文件（Silk.NET.Vulkan），但关键绘制路径未完成
- 所有现有渲染测试跑在 NullRenderDevice 上

## 阶段 1a：排除损坏文件
从 csproj 排除以下 25 个文件：
- Network\NetworkLobbySystem.cs (1502)
- Runtime\Scripting\ScriptEngine.cs (1026)
- DCC\BlenderPythonPlugin.cs (142)
- Assets\ResourceLoader.cs (60)
- ECS\ComponentDataAccessor.cs (22)
- Physics\CollisionDetectionReal.cs, ConstraintSystem.cs, BroadphaseSystem.cs, BuoyancySystem.cs, WindFieldSystem.cs, MagnetismSystem.cs, VehiclePhysicsSystem.cs (68)
- AI\PathSmoother.cs (20)
- Rendering\WorldRenderPipeline.cs, HairRenderingSystem.cs, VariableRateShading.cs, ClusteredForwardRenderer.cs, RayTracedShadows.cs (52)
- Gameplay\NPCDialogSystem.cs (18)
- Core\WorldSystemsBootstrap.cs (12)
- Localization\AdvancedLocalizationSystem.cs (10)
- UI\WidgetAnimation.cs (8)
- Platform\LinuxAdapter.cs (4)
- Input\InputSystemReal.cs (2)
- Animation\AnimMontageSystem.cs (2)

## 阶段 1b：完成 Vulkan RHI
- 检查 VulkanDevice/VulkanSwapChain/VulkanCommandList/VulkanPipeline 现有代码
- 补全缺失的绘制命令
- 实现 VulkanRenderDevice : IRenderDevice

## 阶段 1c：最小渲染闭环
- 顶点缓冲（彩色三角形）
- 最小 SPIR-V 着色器
- 完整帧流程：AcquireNextImage → RenderPass → Draw → Present

## 阶段 1d：真实 GPU 渲染测试
- 在 YamaEngine.Tests 中添加 VulkanRenderDevice 烟测
- 验证：设备创建、交换链创建、一帧渲染、帧统计正确

## 交付物
1. 可编译的 YamaEngine.csproj
2. VulkanRenderDevice 实现
3. 真实 GPU 渲染测试
