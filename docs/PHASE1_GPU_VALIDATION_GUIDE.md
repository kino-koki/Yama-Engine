# Phase 1 — GPU 端到端真机验证指南

> 生成日期：2026-11-02
> 目的：让**真实 GPU** 环境完成 P0 崩溃复现 + 非空场景出图对比，是当前 Phase 1 的核心硬件依赖项。
> 前置结论（本会话静态诊断，可复用）：P0 防御已完整、渲染驱动闭环已接通（见 §4）。

---

## 1. 前置条件

| 项 | 要求 |
|---|---|
| 硬件 | 装有独立 GPU 的 Windows 机器（NVIDIA/AMD，Vulkan 支持） |
| 驱动 | 最新 Vulkan 驱动（NVIDIA 建议 550+，AMD 建议 Adrenalin 24.x） |
| 工具链 | .NET SDK 10.0.203（`global.json` 锁定） |
| 验证工具 | `vulkaninfo`（确认设备枚举）、RenderDoc（可选，帧捕获） |
| 场景素材 | 一个含地形/网格/光照/天空的示例关卡（`Samples/` 或测试场景） |

---

## 2. P0 崩溃复现步骤（0xC0000005）

1. **先确认环境**：运行 `vulkaninfo` 应列出你的独显设备；若无，检查驱动/权限。
2. **启动引擎/编辑器**：
   ```bash
   dotnet run --project YamaEngine.Tests -c Debug   # 或运行 Editor 可执行
   ```
3. **触发渲染**：打开一个含网格+光源的场景，观察崩溃或错误输出。
4. **抓取诊断日志**：
   - 本会话已改进 `EngineApplication` 主渲染 catch：**首次渲染错误会打印完整堆栈**，格式
     `[EngineApplication] 主渲染失败 @帧N: <完整异常>`
   - 若为**原生崩溃（0xC0000005，无托管异常）**：改用 RenderDoc/VS 附加调试抓取，定位原生栈顶。
5. **记录并反馈**：将 `@帧N` 后的完整异常堆栈（或原生栈）发给分析，用于精确定位根因。

> **关键**：本会话已落地防御补丁 —— `VulkanCommandList.SetPipelineState` 对 `VkPipeline.Handle==0` 抛
> `InvalidOperationException`（而非向 `CmdBindPipeline` 传空句柄）。若真机复现为**托管异常**，堆栈将直接
> 指向创建失败/已销毁的 PSO；若仍为原生 0xC0000005，说明根因在防御补丁未覆盖的路径（如描述符/同步）。

---

## 3. 非空场景出图对比步骤

1. **确保场景非空**：加载含地形 + 网格 + 点光/平行光 + 天空的关卡（不要默认空场景）。
2. **单帧出图**：编辑器视口或引擎运行到稳定帧，用 RenderDoc 捕获一帧，或走现有截图接口导出 PNG。
3. **检查项**：
   | 检查项 | 期望 | 若失败 |
   |---|---|---|
   | 无报错/无验证层警告 | 干净退出 | 记录 warning，逐条分析 |
   | 网格可见且无闪烁/黑面 | PBR 材质正确显示 | 检查法线/UV/深度 |
   | 级联阴影投射 | 阴影随光源/相机移动 | 检查 `CascadeShadowMapRenderer` |
   | 后处理（Bloom+Tonemap） | 高亮溢出、色调映射正确 | 检查 `PostProcessStack.RenderPostProcess` |
   | 粒子可见 | GPU 粒子渲染 | 检查 `GPUParticleSystem.Render` |
4. **对比参考**：与 UE4.7 同场景参考图对比，记录差异清单（亮度/阴影/后处理/材质）。

---

## 4. 本会话静态诊断结论（复用）

| 检查项 | 结论 | 证据 |
|---|---|---|
| PSO 绑定防御 | ✅ 完整 | `SetPipelineState` 空句柄抛异常；`Dispose()` 清空句柄；`Reset()` 清空当前 PSO；无绕过 `CmdBindPipeline` 的直接调用 |
| 渲染驱动闭环 | ✅ 已接通 | `EngineApplication` 主循环：Reset→RenderScene(PBR+阴影)→粒子→UI→Execute→Present；`SceneRendererReal` 已接入 ShadowMap+PostProcess(Bloom/Tonemap)+粒子 |
| 后处理注入 | ✅ 已注入 | `EditorViewport.SetPostProcessSystem`（822 行） |
| 高级特性（ReSTIR/SVGF/PathTracer） | ⚠️ 真实实现**未接入主管线** | `ReSTIRPipeline`/`SVGFPipeline`/`PathTracer` 完整，但 `SceneRendererReal` 未调用 |
| MicroCluster 可见性 | ❌ 桩实现 | `MicroClusterVisibility.BeginFrame/EndFrame` 空方法；已实例化+订阅但无功能 |

---

## 5. 反馈路径

复现/出图结果请反馈以下信息，用于下一步精确修复：

- P0：崩溃时的 `@帧N` + 完整异常堆栈 / 原生栈顶；是否被 catch 捕获为托管异常。
- 出图：截图 + 检查项通过/失败清单 + 验证层 warning 全文。
- 环境：GPU 型号、驱动版本、`vulkaninfo` 摘要。
