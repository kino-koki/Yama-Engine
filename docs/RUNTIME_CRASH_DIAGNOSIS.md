# 运行时崩溃诊断：`VulkanPipelineState` 0xC0000005

> 诊断日期：2026-11-02 ｜ 方法：静态代码审查（Grep/Read，抽读 `Vulkan/` 管线实现）
> **状态：未确认根因（需真实 GPU 环境复现确认）；已定位 5 个高嫌疑点，§3 防御性补丁已落地并通过构建+测试验证（2026-11-02）**

---

## 1. 崩溃链路（代码路径）

```
VulkanPipelineState（PSO 创建/持有）
  └─ VulkanDevice.CreateGraphicsPipeline (VulkanDevice.cs:1538)
       └─ CreateGraphicsPipelines (VulkanPipeline.cs:333)
            → 失败则 VkHelper.Check 抛异常 (VulkanResources.cs:367)，VkPipeline.Handle 可能=0
VulkanCommandList.SetPipelineState (VulkanCommandList.cs:1154)
  └─ CmdBindPipeline(CmdBuffer, _currentPSO.BindPoint, _currentPSO.VkPipeline)  ← 1157
        → 若 VkPipeline.Handle==0 → vkCmdBindPipeline(VK_NULL_HANDLE) = UB → 0xC0000005
```

**崩溃点最可能在 `VulkanCommandList.cs:1157`**：`vkCmdBindPipeline` 收到 `VK_NULL_HANDLE`（或已销毁句柄）时，驱动解引用无效管线 → 访问违例。

---

## 2. 高嫌疑点排名（静态证据）

| # | 嫌疑 | 证据（文件:行） | 置信度 | 说明 |
|---|---|---|---|---|
| A | **绑定空/失效 VkPipeline** | `VulkanCommandList.cs:1157` 无任何句柄校验 | 高 | `SetPipelineState` 直接 `CmdBindPipeline(..., _currentPSO.VkPipeline)`，未判 `Handle==0`/null/已销毁 |
| B | 0 个着色器阶段 | `VulkanPipeline.cs:260,314` `StageCount=0` | 中 | 若 VS/PS/GS/HS/DS 全为 null → `stageCount=0`，违反 Vulkan 规范（graphics 需 ≥1），驱动可能崩溃 |
| C | `SampleCount==0` 移位下溢 | `VulkanPipeline.cs:250` `1u << (desc.SampleCount-1)` | 中 | `desc.SampleCount-1` 下溢为 0xFFFFFFFF → 非法 `rasterizationSamples` |
| D | 空描述符集合布局 | `VulkanPipeline.cs:41-44` | 低 | `DescriptorManager` 构造时初始化堆（`VulkanDescriptors.cs:421-425`），**若初始化顺序正常则排除**；若堆创建失败仍会传播 |
| E | 生命周期：管线布局/PSO 先销毁后引用 | `VulkanPipeline.cs:94-104,664-674`；`VulkanCommandList.cs` 缓存 `_currentPSO` | 中 | 命令列表跨帧缓存 `_currentPSO`，`Reset`/`Dispose` 未清空；PSO 销毁后仍可能被 `SetPipelineState` 绑定 |

---

## 3. 建议修复（最小改动，先治 A/C，再防 B/E）

### 3.1 绑定前校验（`VulkanCommandList.cs` `SetPipelineState`）

```csharp
public override void SetPipelineState(RHIPipelineState pso)
{
    if (pso is not VulkanPipelineState vk)
        throw new ArgumentNullException(nameof(pso));
    if (vk.VkPipeline.Handle == 0UL)
        throw new InvalidOperationException(
            $"Pipeline '{vk.Name}' has no valid VkPipeline (create failed or disposed).");

    _currentPSO = vk;
    Device.Vk.CmdBindPipeline(CmdBuffer, vk.BindPoint, vk.VkPipeline);
    _currentBindPoint = vk.BindPoint;
    _currentGraphicsRoot = vk.BindPoint == PipelineBindPoint.Graphics ? null : _currentGraphicsRoot;
    _currentComputeRoot = vk.BindPoint == PipelineBindPoint.Compute ? null : _currentComputeRoot;
}
```

### 3.2 创建失败不残留半成品（`VulkanPipeline.cs` `CreateGraphicsPipeline` / `CreateMeshShaderPipeline`）

```csharp
// 进入时清空句柄，避免异常后遗留垃圾
VkPipeline = default;

// 阶段数 ≥1 校验（B）
if (stages.Count == 0)
    throw new InvalidOperationException("Graphics pipeline requires >= 1 shader stage.");

// SampleCount 下限（C）
RasterizationSamples = (SampleCountFlags)(1u << (Math.Max(1, desc.SampleCount) - 1)),
```

### 3.3 生命周期：命令列表 Begin/Reset 清空缓存（`VulkanCommandList.cs`）

```csharp
// BeginCommandBuffer / ResetCommandBuffer 内：
_currentPSO = null;
_currentGraphicsRoot = null;
_currentComputeRoot = null;
```

> ✅ **补丁已写入引擎代码并验证（2026-11-02）**：`dotnet build` exit=0 / 0 错误；测试套件复跑全通过（`All YamaEngine tests passed.`）。
> 已落地：`VulkanPipeline.cs`（CreateGraphics/CreateMesh/CreateCompute 入口清句柄、stageCount≥1 校验、SampleCount 下限）与 `VulkanCommandList.cs`（`SetPipelineState` 空 PSO/空句柄防护，抛受控异常而非访问违例）。
> 未落地（需你决定）：§3.3 命令列表 `Begin/Reset` 清空缓存、描述符集合布局前置校验。

---

## 4. 确认根因的方法（真实 GPU 环境，按序执行）

1. **开启验证层**：加载 `VK_LAYER_KHRONOS_validation` + 安装调试回调。规范违反（如绑定空管线、stageCount=0）会在崩溃前打出 VUID 与调用栈——**这是最快定位途径**。
2. **复现 + 栈回溯**：以 Debug 配置运行，捕获崩溃线程栈，确认是否落在 `CmdBindPipeline` 调用帧。
3. **回归验证**：应用 §3 补丁后重跑，确认不再崩溃，且 `dotnet build` 仍 0 错误、`dotnet test` 全通过。

---

## 5. 结论

- 最可能根因：**`SetPipelineState` 绑定空/失效 `VkPipeline`（VulkanCommandList.cs:1157）**，源头是管线创建失败后 `VkPipeline.Handle==0` 未被拦截，或 PSO/管线布局被提前销毁仍被引用。
- 次要隐患：stageCount=0、SampleCount 下溢、命令列表跨帧缓存未清。
- **静态分析只能给出假设排名，无法替代硬件确认**；请按 §4 在真实环境验证后再修码。
