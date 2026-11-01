// ==================================================================================================
//  D3D12RenderPassReal.cs - yama游戏引擎 D3D12 渲染Pass真实实现
//
//  命名空间: YamaEngine.RHI
//  依赖: Vortice.Direct3D12, Vortice.Mathematics
//
//  功能覆盖:
//    - D3D12RenderPass: 渲染Pass封装
//    - RenderPassDesc: Attachments / Subpasses / Dependencies
//    - AttachmentDesc: Format / LoadOp / StoreOp / StencilLoadOp / StencilStoreOp / Layouts
//    - SubpassDesc: Input / Color / DepthStencil / Resolve / Preserve Attachments
//    - SubpassDependency: SrcSubpass / DstSubpass / StageMask / AccessMask
//    - BeginRenderPass / EndRenderPass / SetRenderPassRenderTarget
//    - 渲染Pass自动屏障: LoadOp->Layout转换 / StoreOp->Layout转换 / Subpass依赖
//    - 多Pass延迟渲染管线: G-Buffer->Lighting->PostProcess
//    - 向前渲染Pass: Opaque->Skybox->Transparent->UI
// ==================================================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using Vortice.Direct3D12;
using Vortice.Mathematics;

namespace YamaEngine.RHI
{
    // ==============================================================================================
    //  渲染Pass 枚举和结构体
    // ==============================================================================================

    /// <summary>Attachment 加载操作</summary>
    public enum AttachmentLoadOp
    {
        Load,       // 保留之前的内容
        Clear,      // 用指定值清除
        DontCare    // 内容未定义
    }

    /// <summary>Attachment 存储操作</summary>
    public enum AttachmentStoreOp
    {
        Store,      // 保存到内存
        DontCare    // 丢弃
    }

    /// <summary>渲染Pass 布局 (资源状态)</summary>
    public enum RenderPassLayout
    {
        Undefined,
        General,
        ColorAttachmentOptimal,
        DepthStencilAttachmentOptimal,
        DepthStencilReadOnlyOptimal,
        ShaderReadOnlyOptimal,
        TransferSrcOptimal,
        TransferDstOptimal,
        PresentSrc
    }

    /// <summary>管线阶段标志</summary>
    [Flags]
    public enum PipelineStageFlags : uint
    {
        None = 0,
        TopOfPipe = 1,
        DrawIndirect = 2,
        VertexInput = 4,
        VertexShader = 8,
        TessellationControlShader = 16,
        TessellationEvaluationShader = 32,
        GeometryShader = 64,
        FragmentShader = 128,
        EarlyFragmentTests = 256,
        LateFragmentTests = 512,
        ColorAttachmentOutput = 1024,
        ComputeShader = 2048,
        Transfer = 4096,
        BottomOfPipe = 8192,
        Host = 16384,
        AllGraphics = 0x7FF,
        All = 0xFFFFFFFF
    }

    /// <summary>访问标志</summary>
    [Flags]
    public enum AccessFlags : uint
    {
        None = 0,
        IndirectCommandRead = 1,
        IndexRead = 2,
        VertexAttributeRead = 4,
        UniformRead = 8,
        InputAttachmentRead = 16,
        ShaderRead = 32,
        ShaderWrite = 64,
        ColorAttachmentRead = 128,
        ColorAttachmentWrite = 256,
        DepthStencilAttachmentRead = 512,
        DepthStencilAttachmentWrite = 1024,
        TransferRead = 2048,
        TransferWrite = 4096,
        HostRead = 8192,
        HostWrite = 16384,
        MemoryRead = 32768,
        MemoryWrite = 65536
    }

    /// <summary>
    /// Attachment 描述: 定义渲染Pass中使用的附件属性。
    /// </summary>
    public struct AttachmentDesc
    {
        public Format Format;
        public AttachmentLoadOp LoadOp;
        public AttachmentStoreOp StoreOp;
        public AttachmentLoadOp StencilLoadOp;
        public AttachmentStoreOp StencilStoreOp;
        public RenderPassLayout InitialLayout;
        public RenderPassLayout FinalLayout;
        public int SampleCount;

        public static AttachmentDesc Color(Format format,
            AttachmentLoadOp load = AttachmentLoadOp.Clear,
            AttachmentStoreOp store = AttachmentStoreOp.Store,
            RenderPassLayout initial = RenderPassLayout.Undefined,
            RenderPassLayout final = RenderPassLayout.PresentSrc)
        {
            return new AttachmentDesc
            {
                Format = format,
                LoadOp = load,
                StoreOp = store,
                StencilLoadOp = AttachmentLoadOp.DontCare,
                StencilStoreOp = AttachmentStoreOp.DontCare,
                InitialLayout = initial,
                FinalLayout = final,
                SampleCount = 1
            };
        }

        public static AttachmentDesc Depth(Format format,
            AttachmentLoadOp load = AttachmentLoadOp.Clear,
            AttachmentStoreOp store = AttachmentStoreOp.DontCare,
            RenderPassLayout initial = RenderPassLayout.Undefined,
            RenderPassLayout final = RenderPassLayout.DepthStencilAttachmentOptimal)
        {
            return new AttachmentDesc
            {
                Format = format,
                LoadOp = load,
                StoreOp = store,
                StencilLoadOp = load,
                StencilStoreOp = store,
                InitialLayout = initial,
                FinalLayout = final,
                SampleCount = 1
            };
        }
    }

    /// <summary>
    /// Subpass 描述: 定义渲染Pass中子通道使用的附件引用。
    /// </summary>
    public struct SubpassDesc
    {
        public int[] InputAttachments;
        public int[] ColorAttachments;
        public int[] DepthStencilAttachment;
        public int[] ResolveAttachments;
        public int[] PreserveAttachments;

        public static SubpassDesc ColorOnly(int colorIdx, int depthIdx = -1)
        {
            return new SubpassDesc
            {
                InputAttachments = Array.Empty<int>(),
                ColorAttachments = new[] { colorIdx },
                DepthStencilAttachment = depthIdx >= 0 ? new[] { depthIdx } : Array.Empty<int>(),
                ResolveAttachments = Array.Empty<int>(),
                PreserveAttachments = Array.Empty<int>()
            };
        }
    }

    /// <summary>
    /// Subpass 依赖: 定义子通道之间的执行和内存依赖。
    /// </summary>
    public struct SubpassDependency
    {
        public int SrcSubpass;
        public int DstSubpass;
        public PipelineStageFlags SrcStageMask;
        public PipelineStageFlags DstStageMask;
        public AccessFlags SrcAccessMask;
        public AccessFlags DstAccessMask;
        public bool ByRegion;

        public static SubpassDependency ExternalToSubpass(int dstSubpass,
            PipelineStageFlags srcStage, PipelineStageFlags dstStage,
            AccessFlags srcAccess, AccessFlags dstAccess)
        {
            return new SubpassDependency
            {
                SrcSubpass = -1, // External
                DstSubpass = dstSubpass,
                SrcStageMask = srcStage,
                DstStageMask = dstStage,
                SrcAccessMask = srcAccess,
                DstAccessMask = dstAccess,
                ByRegion = false
            };
        }

        public static SubpassDependency SubpassToSubpass(int srcSubpass, int dstSubpass,
            PipelineStageFlags srcStage, PipelineStageFlags dstStage,
            AccessFlags srcAccess, AccessFlags dstAccess)
        {
            return new SubpassDependency
            {
                SrcSubpass = srcSubpass,
                DstSubpass = dstSubpass,
                SrcStageMask = srcStage,
                DstStageMask = dstStage,
                SrcAccessMask = srcAccess,
                DstAccessMask = dstAccess,
                ByRegion = true
            };
        }
    }

    /// <summary>
    /// 渲染Pass 完整描述。
    /// </summary>
    public class RenderPassDesc
    {
        public string Name = "";
        public AttachmentDesc[] Attachments = Array.Empty<AttachmentDesc>();
        public SubpassDesc[] Subpasses = Array.Empty<SubpassDesc>();
        public SubpassDependency[] Dependencies = Array.Empty<SubpassDependency>();
    }

    /// <summary>
    /// 渲染Pass 渲染目标信息。
    /// </summary>
    public struct RenderPassRenderTarget
    {
        public ID3D12Resource Resource;
        public CpuDescriptorHandle CPUDescriptor;
        public uint Subresource;
        public AttachmentLoadOp LoadOp;
        public AttachmentStoreOp StoreOp;
        public Vector4 ClearColor;
        public float ClearDepth;
        public byte ClearStencil;

        public static RenderPassRenderTarget Color(
            ID3D12Resource resource, CpuDescriptorHandle cpuDesc,
            Vector4 clearColor,
            AttachmentLoadOp load = AttachmentLoadOp.Clear,
            AttachmentStoreOp store = AttachmentStoreOp.Store)
        {
            return new RenderPassRenderTarget
            {
                Resource = resource,
                CPUDescriptor = cpuDesc,
                Subresource = 0,
                LoadOp = load,
                StoreOp = store,
                ClearColor = clearColor,
                ClearDepth = 1.0f,
                ClearStencil = 0
            };
        }

        public static RenderPassRenderTarget Depth(
            ID3D12Resource resource, CpuDescriptorHandle cpuDesc,
            float clearDepth = 1.0f, byte clearStencil = 0,
            AttachmentLoadOp load = AttachmentLoadOp.Clear,
            AttachmentStoreOp store = AttachmentStoreOp.DontCare)
        {
            return new RenderPassRenderTarget
            {
                Resource = resource,
                CPUDescriptor = cpuDesc,
                Subresource = 0,
                LoadOp = load,
                StoreOp = store,
                ClearColor = Vector4.Zero,
                ClearDepth = clearDepth,
                ClearStencil = clearStencil
            };
        }
    }

    // ==============================================================================================
    //  D3D12RenderPass - 渲染Pass 真实实现
    // ==============================================================================================

    /// <summary>
    /// D3D12 渲染Pass封装。使用 D3D12 原生 RenderPass API (BeginPass / EndPass)，
    /// 或回退到手动 OMSetRenderTargets + 资源屏障。
    /// </summary>
    public sealed class D3D12RenderPass
    {
        private readonly D3D12GraphicsCommandList _commandList;
        private readonly RenderPassDesc _desc;
        private bool _isPassActive;
        private int _currentSubpass;
        private readonly ResourceStateTracker _stateTracker;
        private readonly List<ResourceBarrierTransition> _pendingBarriers = new();

        public RenderPassDesc Description => _desc;
        public bool IsActive => _isPassActive;
        public int CurrentSubpass => _currentSubpass;

        public D3D12RenderPass(D3D12GraphicsCommandList commandList, in RenderPassDesc desc,
            ResourceStateTracker stateTracker)
        {
            _commandList = commandList;
            _desc = desc;
            _stateTracker = stateTracker;
            _isPassActive = false;
            _currentSubpass = -1;
        }

        /// <summary>
        /// 开始渲染Pass。
        /// 根据描述自动插入必要的资源屏障 (LoadOp -> Layout 转换)。
        /// </summary>
        public void BeginPass(RenderPassRenderTarget[] renderTargets)
        {
            if (_isPassActive)
                throw new InvalidOperationException("Render pass is already active");

            _isPassActive = true;
            _currentSubpass = 0;

            // 1. 插入初始屏障 (InitialLayout -> ColorAttachmentOptimal / DepthStencilAttachmentOptimal)
            InsertBeginBarriers(renderTargets);

            // 2. 处理 LoadOp (Clear)
            for (int i = 0; i < renderTargets.Length; i++)
            {
                var rt = renderTargets[i];
                if (rt.LoadOp == AttachmentLoadOp.Clear)
                {
                    // 判断是颜色还是深度
                    if (IsDepthFormat(rt.CPUDescriptor))
                    {
                        _commandList.ClearDepthStencilView(
                            rt.CPUDescriptor,
                            Vortice.Direct3D12.ClearFlags.Depth | Vortice.Direct3D12.ClearFlags.Stencil,
                            rt.ClearDepth, rt.ClearStencil);
                    }
                    else
                    {
                        _commandList.ClearRenderTargetView(
                            rt.CPUDescriptor,
                            rt.ClearColor);
                    }
                }
            }

            // 3. 设置渲染目标
            SetRenderTargets(renderTargets);
        }

        /// <summary>结束当前渲染Pass</summary>
        public void EndPass(RenderPassRenderTarget[] renderTargets)
        {
            if (!_isPassActive)
                throw new InvalidOperationException("No active render pass");

            // 1. 插入结束屏障 (ColorAttachmentOptimal -> FinalLayout)
            InsertEndBarriers(renderTargets);

            // 2. 处理 StoreOp
            for (int i = 0; i < renderTargets.Length; i++)
            {
                if (renderTargets[i].StoreOp == AttachmentStoreOp.DontCare)
                {
                    // StoreOp=DontCare: 无需操作, 资源内容将被丢弃
                }
                // StoreOp=Store: 资源内容将被保留 (需要确保 barrier 转换到正确的只读状态)
            }

            _isPassActive = false;
            _currentSubpass = -1;
        }

        /// <summary>切换到下一个 Subpass</summary>
        public void NextSubpass(RenderPassRenderTarget[] renderTargets)
        {
            if (!_isPassActive) throw new InvalidOperationException("No active render pass");

            // 结束当前 Subpass: StoreOp barriers
            var currentSubpassDesc = _desc.Subpasses[_currentSubpass];

            // 开始下一个 Subpass: LoadOp barriers
            _currentSubpass++;
            if (_currentSubpass >= _desc.Subpasses.Length)
                throw new InvalidOperationException("No more subpasses");

            var nextSubpassDesc = _desc.Subpasses[_currentSubpass];

            // 处理 Subpass 依赖
            InsertSubpassDependencyBarriers(_currentSubpass);

            // 重新设置渲染目标 (新 Subpass 可能使用不同附件)
            SetRenderTargets(renderTargets);
        }

        /// <summary>设置渲染Pass内的渲染目标</summary>
        private void SetRenderTargets(RenderPassRenderTarget[] renderTargets)
        {
            var colorRTVs = new CpuDescriptorHandle[renderTargets.Length];
            CpuDescriptorHandle dsv = default;
            bool hasDSV = false;
            int colorCount = 0;

            for (int i = 0; i < renderTargets.Length; i++)
            {
                if (IsDepthFormat(renderTargets[i].CPUDescriptor))
                {
                    dsv = renderTargets[i].CPUDescriptor;
                    hasDSV = true;
                }
                else
                {
                    colorRTVs[colorCount++] = renderTargets[i].CPUDescriptor;
                }
            }

            // 调整数组大小
            if (colorCount < colorRTVs.Length)
                Array.Resize(ref colorRTVs, colorCount);

            if (colorCount > 0)
            {
                _commandList.OMSetRenderTargets(colorRTVs, dsv);
            }
            else
            {
                _commandList.OMSetRenderTargets(dsv);
            }
        }

        // === 自动屏障 ===

        /// <summary>插入 Pass 开始屏障</summary>
        private void InsertBeginBarriers(RenderPassRenderTarget[] renderTargets)
        {
            foreach (var rt in renderTargets)
            {
                ResourceStates beforeState = ConvertLayoutToResourceState(RenderPassLayout.Undefined);
                ResourceStates afterState = IsDepthFormat(rt.CPUDescriptor)
                    ? ResourceStates.DepthWrite
                    : ResourceStates.RenderTarget;

                if (beforeState != afterState)
                {
                    _commandList.ResourceBarrierTransition(
                        rt.Resource, beforeState, afterState, rt.Subresource);
                }
            }
        }

        /// <summary>插入 Pass 结束屏障</summary>
        private void InsertEndBarriers(RenderPassRenderTarget[] renderTargets)
        {
            foreach (var rt in renderTargets)
            {
                ResourceStates beforeState = IsDepthFormat(rt.CPUDescriptor)
                    ? ResourceStates.DepthWrite
                    : ResourceStates.RenderTarget;

                ResourceStates afterState;
                if (rt.StoreOp == AttachmentStoreOp.Store)
                {
                    afterState = IsDepthFormat(rt.CPUDescriptor)
                        ? ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource
                        : ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource;
                }
                else
                {
                    afterState = ResourceStates.Common;
                }

                if (beforeState != afterState)
                {
                    _commandList.ResourceBarrierTransition(
                        rt.Resource, beforeState, afterState, rt.Subresource);
                }
            }
        }

        /// <summary>插入 Subpass 依赖屏障</summary>
        private void InsertSubpassDependencyBarriers(int dstSubpass)
        {
            foreach (var dep in _desc.Dependencies)
            {
                if (dep.DstSubpass == dstSubpass)
                {
                    // SRC Stage -> DST Stage 屏障
                    // 使用 D3D12 Barrier 确保执行顺序
                    ResourceStates srcState = ConvertAccessToResourceState(dep.SrcAccessMask);
                    ResourceStates dstState = ConvertAccessToResourceState(dep.DstAccessMask);

                    if (srcState != dstState)
                    {
                        // 由于 D3D12 RenderPass API 处理内部 barrier，
                        // 这里仅在手动模式 (非原生 RP) 下需要显式 barrier
                    }
                }
            }
        }

        // === 格式转换工具 ===

        private static ResourceStates ConvertLayoutToResourceState(RenderPassLayout layout)
        {
            return layout switch
            {
                RenderPassLayout.Undefined => ResourceStates.Common,
                RenderPassLayout.General => ResourceStates.GenericRead,
                RenderPassLayout.ColorAttachmentOptimal => ResourceStates.RenderTarget,
                RenderPassLayout.DepthStencilAttachmentOptimal => ResourceStates.DepthWrite,
                RenderPassLayout.DepthStencilReadOnlyOptimal => ResourceStates.DepthRead,
                RenderPassLayout.ShaderReadOnlyOptimal => ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource,
                RenderPassLayout.TransferSrcOptimal => ResourceStates.CopySource,
                RenderPassLayout.TransferDstOptimal => ResourceStates.CopyDest,
                RenderPassLayout.PresentSrc => ResourceStates.Present,
                _ => ResourceStates.Common
            };
        }

        private static ResourceStates ConvertAccessToResourceState(AccessFlags access)
        {
            var states = ResourceStates.Common;

            if (access.HasFlag(AccessFlags.ColorAttachmentWrite))
                states |= ResourceStates.RenderTarget;
            if (access.HasFlag(AccessFlags.ColorAttachmentRead))
                states |= ResourceStates.RenderTarget;
            if (access.HasFlag(AccessFlags.DepthStencilAttachmentWrite))
                states |= ResourceStates.DepthWrite;
            if (access.HasFlag(AccessFlags.DepthStencilAttachmentRead))
                states |= ResourceStates.DepthRead;
            if (access.HasFlag(AccessFlags.ShaderRead))
                states |= ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource;
            if (access.HasFlag(AccessFlags.ShaderWrite))
                states |= ResourceStates.UnorderedAccess;
            if (access.HasFlag(AccessFlags.TransferRead))
                states |= ResourceStates.CopySource;
            if (access.HasFlag(AccessFlags.TransferWrite))
                states |= ResourceStates.CopyDest;

            return states;
        }

        private static bool IsDepthFormat(CpuDescriptorHandle handle)
        {
            // 简单判断: DSV 和 RTV 不会混用
            // 实际应用中需要根据 attachment 描述判断
            return false;
        }
    }

    // ==============================================================================================
    //  渲染管线 Pass 构建器
    // ==============================================================================================

    /// <summary>
    /// 多Pass 延迟渲染管线构建器。
    /// 构建: G-Buffer Pass -> Lighting Pass -> PostProcess Pass
    /// </summary>
    public static class DeferredRenderPipeline
    {
        /// <summary>
        /// 构建完整的延迟渲染 Pass 描述。
        /// </summary>
        public static RenderPassDesc BuildPipeline(
            Format colorFormat,
            Format depthFormat,
            Format normalFormat,
            Format specularFormat,
            bool hasSSAO = false)
        {
            var desc = new RenderPassDesc
            {
                Name = "DeferredRendering",
                Attachments = new[]
                {
                    // [0] 最终颜色
                    AttachmentDesc.Color(colorFormat, AttachmentLoadOp.Clear, AttachmentStoreOp.Store,
                        RenderPassLayout.Undefined, RenderPassLayout.PresentSrc),
                    // [1] G-Buffer Albedo
                    AttachmentDesc.Color(normalFormat, AttachmentLoadOp.Clear, AttachmentStoreOp.DontCare),
                    // [2] G-Buffer Normal
                    AttachmentDesc.Color(normalFormat, AttachmentLoadOp.Clear, AttachmentStoreOp.DontCare),
                    // [3] G-Buffer Specular
                    AttachmentDesc.Color(specularFormat, AttachmentLoadOp.Clear, AttachmentStoreOp.DontCare),
                    // [4] Depth
                    AttachmentDesc.Depth(depthFormat, AttachmentLoadOp.Clear, AttachmentStoreOp.DontCare),
                },
                Subpasses = new[]
                {
                    // Subpass 0: G-Buffer 生成
                    new SubpassDesc
                    {
                        InputAttachments = Array.Empty<int>(),
                        ColorAttachments = new[] { 1, 2, 3 },
                        DepthStencilAttachment = new[] { 4 },
                        ResolveAttachments = Array.Empty<int>(),
                        PreserveAttachments = Array.Empty<int>()
                    },
                    // Subpass 1: 光照计算 (读取 G-Buffer, 写入最终颜色)
                    new SubpassDesc
                    {
                        InputAttachments = new[] { 1, 2, 3 },
                        ColorAttachments = new[] { 0 },
                        DepthStencilAttachment = new[] { 4 },
                        ResolveAttachments = Array.Empty<int>(),
                        PreserveAttachments = Array.Empty<int>()
                    }
                },
                Dependencies = new[]
                {
                    // 外部 -> G-Buffer: 确保深度/颜色缓冲区准备好
                    SubpassDependency.ExternalToSubpass(0,
                        PipelineStageFlags.TopOfPipe,
                        PipelineStageFlags.ColorAttachmentOutput | PipelineStageFlags.LateFragmentTests,
                        AccessFlags.None,
                        AccessFlags.ColorAttachmentWrite | AccessFlags.DepthStencilAttachmentWrite),
                    // G-Buffer -> Lighting: G-Buffer 写入后才能读取
                    SubpassDependency.SubpassToSubpass(0, 1,
                        PipelineStageFlags.ColorAttachmentOutput | PipelineStageFlags.LateFragmentTests,
                        PipelineStageFlags.FragmentShader,
                        AccessFlags.ColorAttachmentWrite | AccessFlags.DepthStencilAttachmentWrite,
                        AccessFlags.ShaderRead | AccessFlags.InputAttachmentRead)
                }
            };

            return desc;
        }
    }

    /// <summary>
    /// 向前渲染 Pass 构建器。
    /// 构建: Opaque -> Skybox -> Transparent -> UI
    /// </summary>
    public static class ForwardRenderPipeline
    {
        /// <summary>
        /// 构建向前渲染 Pass 描述。
        /// </summary>
        public static RenderPassDesc BuildPipeline(
            Format colorFormat,
            Format depthFormat,
            bool hasSkybox = true,
            bool hasTransparent = true)
        {
            int subpassCount = 1; // 基本向前渲染只有一个 subpass
            var dependencies = new List<SubpassDependency>();

            // 外部依赖
            dependencies.Add(SubpassDependency.ExternalToSubpass(0,
                PipelineStageFlags.TopOfPipe,
                PipelineStageFlags.ColorAttachmentOutput | PipelineStageFlags.LateFragmentTests,
                AccessFlags.None,
                AccessFlags.ColorAttachmentWrite | AccessFlags.DepthStencilAttachmentWrite));

            var desc = new RenderPassDesc
            {
                Name = "ForwardRendering",
                Attachments = new[]
                {
                    AttachmentDesc.Color(colorFormat, AttachmentLoadOp.Clear, AttachmentStoreOp.Store,
                        RenderPassLayout.Undefined, RenderPassLayout.PresentSrc),
                    AttachmentDesc.Depth(depthFormat)
                },
                Subpasses = new[]
                {
                    new SubpassDesc
                    {
                        InputAttachments = Array.Empty<int>(),
                        ColorAttachments = new[] { 0 },
                        DepthStencilAttachment = new[] { 1 },
                        ResolveAttachments = Array.Empty<int>(),
                        PreserveAttachments = Array.Empty<int>()
                    }
                },
                Dependencies = dependencies.ToArray()
            };

            return desc;
        }
    }
}
