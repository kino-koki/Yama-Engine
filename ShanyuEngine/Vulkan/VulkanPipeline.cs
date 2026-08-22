using Vulkan = Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace ShanyuEngine.RHI.Vulkan
{
    // ════════════════════════════════════════════════════════════════
    // Vulkan Root Signature
    // Maps D3D12-style root signature to VkPipelineLayout + VkDescriptorSetLayout
    // ════════════════════════════════════════════════════════════════

    public sealed class VulkanRootSignature : RHIRootSignature
    {
        internal readonly VulkanDevice Device;
        internal PipelineLayout VkPipelineLayout;
        internal DescriptorSetLayout[] SetLayouts;
        internal PushConstantRange[] PushConstantRanges;
        internal RHIRootParameter[] Parameters;
        internal uint PushConstantSize;
        private string _name = "";
        private bool _disposed;

        public VulkanRootSignature(VulkanDevice device, RHIRootSignatureDesc desc)
        {
            Device = device;
            Parameters = desc.Parameters ?? Array.Empty<RHIRootParameter>();
            BuildPipelineLayout();
        }

        private unsafe void BuildPipelineLayout()
        {
            var setLayouts = new List<DescriptorSetLayout>();
            var pushConstantRanges = new List<PushConstantRange>();

            // Set 0: Bindless CBV/SRV/UAV (from descriptor manager)
            setLayouts.Add(Device.DescriptorManager.BindlessSetLayout);

            // Set 1: Sampler heap
            setLayouts.Add(Device.DescriptorManager.SamplerSetLayout);

            // Process root parameters for push constants
            uint pcOffset = 0;

            foreach (var param in Parameters)
            {
                if (param.ParameterType == RHIRootParameterType.Constants)
                {
                    var constants = param.Constants;
                    if (constants.HasValue)
                    {
                        pushConstantRanges.Add(new PushConstantRange
                        {
                            StageFlags = VkFormatMapper.ToVkShaderVisibility(param.ShaderVisibility),
                            Offset = pcOffset,
                            Size = (uint)(constants.Value.Num32BitValues * 4)
                        });
                        pcOffset += (uint)(constants.Value.Num32BitValues * 4);
                    }
                }
            }

            PushConstantSize = pcOffset;
            PushConstantRanges = pushConstantRanges.ToArray();

            DescriptorSetLayout[] layouts = setLayouts.ToArray();
            PushConstantRange[] pcRanges = PushConstantRanges;

            fixed (DescriptorSetLayout* pSetLayouts = layouts)
            fixed (PushConstantRange* pPCRanges = pcRanges)
            {
                PipelineLayoutCreateInfo layoutInfo = new()
                {
                    SType = StructureType.PipelineLayoutCreateInfo,
                    SetLayoutCount = (uint)setLayouts.Count,
                    PSetLayouts = pSetLayouts,
                    PushConstantRangeCount = (uint)PushConstantRanges.Length,
                    PPushConstantRanges = pPCRanges
                };

                VkHelper.Check(Device.Vk.CreatePipelineLayout(Device.VkDevice, &layoutInfo, null, out VkPipelineLayout),
                    "Failed to create pipeline layout");
            }

            SetLayouts = setLayouts.ToArray();
        }

        public override string Name { get => _name; set => _name = value; }

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (VkPipelineLayout.Handle != 0)
            {
                Device.Vk.DestroyPipelineLayout(Device.VkDevice, VkPipelineLayout, null);
                VkPipelineLayout = default;
            }
        }
    }

    // ════════════════════════════════════════════════════════════════
    // Vulkan Pipeline State
    // Creates VkPipeline for graphics, compute, and mesh shader pipelines
    // ════════════════════════════════════════════════════════════════

    public sealed unsafe class VulkanPipelineState : RHIPipelineState
    {
        internal readonly VulkanDevice Device;
        internal Pipeline VkPipeline;
        internal PipelineLayout VkPipelineLayout;
        internal PipelineBindPoint BindPoint;
        internal RHIPipelineType _pipelineType;
        private string _name = "";
        private bool _disposed;

        public VulkanPipelineState(VulkanDevice device, RHIGraphicsPipelineDesc desc, VulkanRootSignature rootSig)
        {
            Device = device;
            VkPipelineLayout = rootSig.VkPipelineLayout;
            _pipelineType = RHIPipelineType.Graphics;
            BindPoint = PipelineBindPoint.Graphics;
            _name = desc.Name;

            CreateGraphicsPipeline(desc, rootSig);
        }

        public VulkanPipelineState(VulkanDevice device, RHIComputePipelineDesc desc, VulkanRootSignature rootSig)
        {
            Device = device;
            VkPipelineLayout = rootSig.VkPipelineLayout;
            _pipelineType = RHIPipelineType.Compute;
            BindPoint = PipelineBindPoint.Compute;
            _name = desc.Name;

            CreateComputePipeline(desc, rootSig);
        }

        public VulkanPipelineState(VulkanDevice device, RHIMeshPipelineDesc desc, VulkanRootSignature rootSig)
        {
            Device = device;
            VkPipelineLayout = rootSig.VkPipelineLayout;
            _pipelineType = RHIPipelineType.Mesh;
            BindPoint = PipelineBindPoint.Graphics;
            _name = desc.Name;

            CreateMeshShaderPipeline(desc, rootSig);
        }

        // ── Graphics Pipeline ──
        private unsafe void CreateGraphicsPipeline(RHIGraphicsPipelineDesc desc, VulkanRootSignature rootSig)
        {
            var stages = new List<PipelineShaderStageCreateInfo>();
            var shaderModules = new List<ShaderModule>();
            var pinnedStrings = new List<GCHandle>();

            try
            {
                AddShaderStage(stages, shaderModules, pinnedStrings, desc.VS, ShaderStageFlags.VertexBit);
                AddShaderStage(stages, shaderModules, pinnedStrings, desc.PS, ShaderStageFlags.FragmentBit);
                AddShaderStage(stages, shaderModules, pinnedStrings, desc.GS, ShaderStageFlags.GeometryBit);
                AddShaderStage(stages, shaderModules, pinnedStrings, desc.HS, ShaderStageFlags.TessellationControlBit);
                AddShaderStage(stages, shaderModules, pinnedStrings, desc.DS, ShaderStageFlags.TessellationEvaluationBit);

                // Vertex input state
                var vertexBindings = new List<VertexInputBindingDescription>();
                var vertexAttrs = new List<VertexInputAttributeDescription>();

                if (desc.InputLayout != null)
                {
                    // Group by input slot
                    var slotStrides = new Dictionary<int, uint>();
                    var slotRates = new Dictionary<int, VertexInputRate>();

                    foreach (var elem in desc.InputLayout)
                    {
                        if (!slotStrides.ContainsKey(elem.InputSlot))
                        {
                            slotStrides[elem.InputSlot] = 0;
                            slotRates[elem.InputSlot] = elem.IsPerVertex ? VertexInputRate.Vertex : VertexInputRate.Instance;
                        }
                        uint elemSize = (uint)GetFormatSize(elem.Format);
                        uint offset = elem.AlignedByteOffset >= 0 ? (uint)elem.AlignedByteOffset : slotStrides[elem.InputSlot];
                        vertexAttrs.Add(new VertexInputAttributeDescription
                        {
                            Location = (uint)elem.SemanticIndex,
                            Binding = (uint)elem.InputSlot,
                            Format = VkFormatMapper.ToVkFormat(elem.Format),
                            Offset = offset
                        });
                        if (elem.AlignedByteOffset < 0)
                            slotStrides[elem.InputSlot] += elemSize;
                    }

                    foreach (var kvp in slotStrides)
                    {
                        vertexBindings.Add(new VertexInputBindingDescription
                        {
                            Binding = (uint)kvp.Key,
                            Stride = kvp.Value,
                            InputRate = slotRates[kvp.Key]
                        });
                    }
                }

                VertexInputBindingDescription[] vbArr = vertexBindings.ToArray();
                VertexInputAttributeDescription[] vaArr = vertexAttrs.ToArray();

                fixed (VertexInputBindingDescription* pVB = vbArr)
                fixed (VertexInputAttributeDescription* pVA = vaArr)
                {
                    PipelineVertexInputStateCreateInfo vertexInputState = new()
                    {
                        SType = StructureType.PipelineVertexInputStateCreateInfo,
                        VertexBindingDescriptionCount = (uint)vbArr.Length,
                        PVertexBindingDescriptions = pVB,
                        VertexAttributeDescriptionCount = (uint)vaArr.Length,
                        PVertexAttributeDescriptions = pVA
                    };

                    PipelineInputAssemblyStateCreateInfo inputAssembly = new()
                    {
                        SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                        Topology = VkFormatMapper.ToVkTopology(desc.PrimitiveTopology),
                        PrimitiveRestartEnable = false
                    };

                    Viewport viewport = new(0, 0, 1, 1, 0, 1);
                    Rect2D scissor = new(new Offset2D(0, 0), new Extent2D(1, 1));

                    PipelineViewportStateCreateInfo viewportState = new()
                    {
                        SType = StructureType.PipelineViewportStateCreateInfo,
                        ViewportCount = 1,
                        PViewports = &viewport,
                        ScissorCount = 1,
                        PScissors = &scissor
                    };

                    PipelineRasterizationStateCreateInfo rasterizer = BuildRasterizer(desc.RasterizerState);

                    PipelineMultisampleStateCreateInfo multisampling = new()
                    {
                        SType = StructureType.PipelineMultisampleStateCreateInfo,
                        RasterizationSamples = (SampleCountFlags)(1u << (desc.SampleCount - 1)),
                        MinSampleShading = 1.0f
                    };

                    PipelineDepthStencilStateCreateInfo depthStencil = BuildDepthStencil(desc.DepthStencilState);

                    // Color blend state
                    var colorAttachments = BuildColorBlendAttachments(desc.BlendState);
                    float[] blendConstants = { 0, 0, 0, 0 };

                    PipelineShaderStageCreateInfo[] stageArr = stages.ToArray();
                    PipelineColorBlendAttachmentState[] caArr = colorAttachments.ToArray();

                    fixed (PipelineShaderStageCreateInfo* pStages = stageArr)
                    fixed (PipelineColorBlendAttachmentState* pCA = caArr)
                    fixed (float* pBC = blendConstants)
                    {
                        PipelineColorBlendStateCreateInfo colorBlending = new()
                        {
                            SType = StructureType.PipelineColorBlendStateCreateInfo,
                            LogicOpEnable = false,
                            AttachmentCount = (uint)caArr.Length,
                            PAttachments = pCA,
                            BlendConstants = pBC
                        };

                        DynamicState[] dynamicStates =
                        {
                            DynamicState.Viewport, DynamicState.Scissor,
                            DynamicState.BlendConstants, DynamicState.StencilReference,
                            DynamicState.PrimitiveTopology
                        };

                        fixed (DynamicState* pDS = dynamicStates)
                        {
                            PipelineDynamicStateCreateInfo dynamicState = new()
                            {
                                SType = StructureType.PipelineDynamicStateCreateInfo,
                                DynamicStateCount = (uint)dynamicStates.Length,
                                PDynamicStates = pDS
                            };

                            // Convert RTV formats
                            Format[] colorFormats = new Format[desc.RTVFormats?.Length ?? 0];
                            for (int i = 0; i < colorFormats.Length; i++)
                                colorFormats[i] = VkFormatMapper.ToVkFormat(desc.RTVFormats[i]);
                            Format depthFormat = desc.DSVFormat != RHIFormat.Unknown
                                ? VkFormatMapper.ToVkFormat(desc.DSVFormat) : Format.Undefined;

                            fixed (Format* pColorFormats = colorFormats)
                            {
                                PipelineRenderingCreateInfo renderingInfo = new()
                                {
                                    SType = StructureType.PipelineRenderingCreateInfo,
                                    ColorAttachmentCount = (uint)colorFormats.Length,
                                    PColorAttachmentFormats = pColorFormats,
                                    DepthAttachmentFormat = depthFormat,
                                    StencilAttachmentFormat = Format.Undefined
                                };

                                GraphicsPipelineCreateInfo pipelineInfo = new()
                                {
                                    SType = StructureType.GraphicsPipelineCreateInfo,
                                    StageCount = (uint)stageArr.Length,
                                    PStages = pStages,
                                    PVertexInputState = &vertexInputState,
                                    PInputAssemblyState = &inputAssembly,
                                    PViewportState = &viewportState,
                                    PRasterizationState = &rasterizer,
                                    PMultisampleState = &multisampling,
                                    PDepthStencilState = &depthStencil,
                                    PColorBlendState = &colorBlending,
                                    PDynamicState = &dynamicState,
                                    Layout = VkPipelineLayout,
                                    RenderPass = default,
                                    Subpass = 0,
                                    BasePipelineHandle = default,
                                    BasePipelineIndex = -1,
                                    PNext = &renderingInfo
                                };

                                VkHelper.Check(
                                    Device.Vk.CreateGraphicsPipelines(Device.VkDevice, default, 1, &pipelineInfo, null, out VkPipeline),
                                    "Failed to create graphics pipeline");
                            }
                        }
                    }
                }
            }
            finally
            {
                foreach (var module in shaderModules)
                    Device.Vk.DestroyShaderModule(Device.VkDevice, module, null);
                foreach (var handle in pinnedStrings)
                    handle.Free();
            }
        }

        // ── Compute Pipeline ──
        private unsafe void CreateComputePipeline(RHIComputePipelineDesc desc, VulkanRootSignature rootSig)
        {
            if (!desc.CS.HasValue)
                throw new ArgumentException("Compute shader is required for compute pipeline");

            ShaderModule module = CreateShaderModule(desc.CS.Value);
            byte[] entryBytes = VkHelper.StringToUtf8(desc.CS.Value.EntryPoint ?? "main");
            GCHandle entryHandle = GCHandle.Alloc(entryBytes, GCHandleType.Pinned);

            try
            {
                PipelineShaderStageCreateInfo shaderStage = new()
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = module,
                    PName = (byte*)entryHandle.AddrOfPinnedObject()
                };

                ComputePipelineCreateInfo pipelineInfo = new()
                {
                    SType = StructureType.ComputePipelineCreateInfo,
                    Stage = shaderStage,
                    Layout = VkPipelineLayout
                };

                VkHelper.Check(
                    Device.Vk.CreateComputePipelines(Device.VkDevice, default, 1, &pipelineInfo, null, out VkPipeline),
                    "Failed to create compute pipeline");
            }
            finally
            {
                entryHandle.Free();
                Device.Vk.DestroyShaderModule(Device.VkDevice, module, null);
            }
        }

        // ── Mesh Shader Pipeline ──
        private unsafe void CreateMeshShaderPipeline(RHIMeshPipelineDesc desc, VulkanRootSignature rootSig)
        {
            var stages = new List<PipelineShaderStageCreateInfo>();
            var shaderModules = new List<ShaderModule>();
            var pinnedStrings = new List<GCHandle>();

            try
            {
                AddShaderStage(stages, shaderModules, pinnedStrings, desc.MS, ShaderStageFlags.MeshBitExt);
                AddShaderStage(stages, shaderModules, pinnedStrings, desc.AS, ShaderStageFlags.TaskBitExt);
                AddShaderStage(stages, shaderModules, pinnedStrings, desc.PS, ShaderStageFlags.FragmentBit);

                Viewport viewport = new(0, 0, 1, 1, 0, 1);
                Rect2D scissor = new(new Offset2D(0, 0), new Extent2D(1, 1));

                PipelineViewportStateCreateInfo viewportState = new()
                {
                    SType = StructureType.PipelineViewportStateCreateInfo,
                    ViewportCount = 1,
                    PViewports = &viewport,
                    ScissorCount = 1,
                    PScissors = &scissor
                };

                PipelineRasterizationStateCreateInfo rasterizer = BuildRasterizer(desc.RasterizerState);

                PipelineMultisampleStateCreateInfo multisampling = new()
                {
                    SType = StructureType.PipelineMultisampleStateCreateInfo,
                    RasterizationSamples = SampleCountFlags.Count1Bit,
                    MinSampleShading = 1.0f
                };

                PipelineDepthStencilStateCreateInfo depthStencil = BuildDepthStencil(desc.DepthStencilState);

                var colorAttachments = BuildColorBlendAttachments(desc.BlendState);

                PipelineShaderStageCreateInfo[] stageArr = stages.ToArray();
                PipelineColorBlendAttachmentState[] caArr = colorAttachments.ToArray();
                float[] blendConstants = { 0, 0, 0, 0 };

                fixed (PipelineShaderStageCreateInfo* pStages = stageArr)
                fixed (PipelineColorBlendAttachmentState* pCA = caArr)
                fixed (float* pBC = blendConstants)
                {
                    PipelineColorBlendStateCreateInfo colorBlending = new()
                    {
                        SType = StructureType.PipelineColorBlendStateCreateInfo,
                        AttachmentCount = (uint)caArr.Length,
                        PAttachments = pCA,
                        BlendConstants = pBC
                    };

                    DynamicState[] dynamicStates = { DynamicState.Viewport, DynamicState.Scissor, DynamicState.BlendConstants };
                    fixed (DynamicState* pDS = dynamicStates)
                    {
                        PipelineDynamicStateCreateInfo dynamicState = new()
                        {
                            SType = StructureType.PipelineDynamicStateCreateInfo,
                            DynamicStateCount = (uint)dynamicStates.Length,
                            PDynamicStates = pDS
                        };

                        Format[] colorFormats = new Format[desc.RTVFormats?.Length ?? 0];
                        for (int i = 0; i < colorFormats.Length; i++)
                            colorFormats[i] = VkFormatMapper.ToVkFormat(desc.RTVFormats[i]);
                        Format depthFormat = desc.DSVFormat != RHIFormat.Unknown
                            ? VkFormatMapper.ToVkFormat(desc.DSVFormat) : Format.Undefined;

                        fixed (Format* pColorFormats = colorFormats)
                        {
                            PipelineRenderingCreateInfo renderingInfo = new()
                            {
                                SType = StructureType.PipelineRenderingCreateInfo,
                                ColorAttachmentCount = (uint)colorFormats.Length,
                                PColorAttachmentFormats = pColorFormats,
                                DepthAttachmentFormat = depthFormat
                            };

                            GraphicsPipelineCreateInfo pipelineInfo = new()
                            {
                                SType = StructureType.GraphicsPipelineCreateInfo,
                                StageCount = (uint)stageArr.Length,
                                PStages = pStages,
                                PVertexInputState = null,
                                PInputAssemblyState = null,
                                PViewportState = &viewportState,
                                PRasterizationState = &rasterizer,
                                PMultisampleState = &multisampling,
                                PDepthStencilState = &depthStencil,
                                PColorBlendState = &colorBlending,
                                PDynamicState = &dynamicState,
                                Layout = VkPipelineLayout,
                                RenderPass = default,
                                Subpass = 0,
                                BasePipelineIndex = -1,
                                PNext = &renderingInfo
                            };

                            VkHelper.Check(
                                Device.Vk.CreateGraphicsPipelines(Device.VkDevice, default, 1, &pipelineInfo, null, out VkPipeline),
                                "Failed to create mesh shader pipeline");
                        }
                    }
                }
            }
            finally
            {
                foreach (var module in shaderModules)
                    Device.Vk.DestroyShaderModule(Device.VkDevice, module, null);
                foreach (var handle in pinnedStrings)
                    handle.Free();
            }
        }

        private unsafe void AddShaderStage(List<PipelineShaderStageCreateInfo> stages,
            List<ShaderModule> modules, List<GCHandle> pinnedStrings,
            RHIShaderBytecode? shader, ShaderStageFlags stage)
        {
            if (!shader.HasValue) return;
            ShaderModule module = CreateShaderModule(shader.Value);
            modules.Add(module);

            byte[] entryBytes = VkHelper.StringToUtf8(shader.Value.EntryPoint ?? "main");
            GCHandle handle = GCHandle.Alloc(entryBytes, GCHandleType.Pinned);
            pinnedStrings.Add(handle);

            stages.Add(new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = stage,
                Module = module,
                PName = (byte*)handle.AddrOfPinnedObject()
            });
        }

        private unsafe ShaderModule CreateShaderModule(RHIShaderBytecode shader)
        {
            if (shader.Bytecode == null || shader.Bytecode.Length == 0)
                throw new ArgumentException("Shader bytecode is empty");

            // Verify SPIR-V magic number
            if (shader.Bytecode.Length >= 4)
            {
                uint magic = BitConverter.ToUInt32(shader.Bytecode, 0);
                if (magic != 0x07230203)
                    throw new ArgumentException(
                        $"Shader '{shader.Name}' is not valid SPIR-V. Compile GLSL/HLSL to SPIR-V before creating pipeline.");
            }

            fixed (byte* pCode = shader.Bytecode)
            {
                ShaderModuleCreateInfo createInfo = new()
                {
                    SType = StructureType.ShaderModuleCreateInfo,
                    CodeSize = (nuint)shader.Bytecode.Length,
                    PCode = (uint*)pCode
                };

                VkHelper.Check(Device.Vk.CreateShaderModule(Device.VkDevice, &createInfo, null, out ShaderModule module),
                    "Failed to create shader module");
                return module;
            }
        }

        private PipelineRasterizationStateCreateInfo BuildRasterizer(RHIRasterizerDesc r)
        {
            return new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                DepthClampEnable = !r.DepthClipEnable,
                RasterizerDiscardEnable = false,
                PolygonMode = VkFormatMapper.ToVkPolygonMode(r.FillMode),
                CullMode = VkFormatMapper.ToVkCullMode(r.CullMode),
                FrontFace = r.FrontCounterClockwise ? FrontFace.CounterClockwise : FrontFace.Clockwise,
                DepthBiasEnable = r.DepthBias != 0,
                DepthBiasConstantFactor = r.DepthBias,
                DepthBiasClamp = r.DepthBiasClamp,
                DepthBiasSlopeFactor = r.SlopeScaledDepthBias,
                LineWidth = 1.0f
            };
        }

        private PipelineDepthStencilStateCreateInfo BuildDepthStencil(RHIDepthDesc ds)
        {
            StencilOpState front = new()
            {
                FailOp = VkFormatMapper.ToVkStencilOp(ds.FrontFace.StencilFailOp),
                PassOp = VkFormatMapper.ToVkStencilOp(ds.FrontFace.StencilPassOp),
                DepthFailOp = VkFormatMapper.ToVkStencilOp(ds.FrontFace.StencilDepthFailOp),
                CompareOp = VkFormatMapper.ToVkCompareOp(ds.FrontFace.StencilFunc),
                CompareMask = ds.StencilReadMask,
                WriteMask = ds.StencilWriteMask,
                Reference = 0
            };
            StencilOpState back = new()
            {
                FailOp = VkFormatMapper.ToVkStencilOp(ds.BackFace.StencilFailOp),
                PassOp = VkFormatMapper.ToVkStencilOp(ds.BackFace.StencilPassOp),
                DepthFailOp = VkFormatMapper.ToVkStencilOp(ds.BackFace.StencilDepthFailOp),
                CompareOp = VkFormatMapper.ToVkCompareOp(ds.BackFace.StencilFunc),
                CompareMask = ds.StencilReadMask,
                WriteMask = ds.StencilWriteMask,
                Reference = 0
            };

            return new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = ds.DepthEnable,
                DepthWriteEnable = ds.DepthWriteEnable,
                DepthCompareOp = VkFormatMapper.ToVkCompareOp(ds.DepthFunc),
                DepthBoundsTestEnable = false,
                StencilTestEnable = ds.StencilEnable,
                Front = front,
                Back = back,
                MinDepthBounds = 0.0f,
                MaxDepthBounds = 1.0f
            };
        }

        private List<PipelineColorBlendAttachmentState> BuildColorBlendAttachments(RHIBlendDesc blend)
        {
            var result = new List<PipelineColorBlendAttachmentState>();
            if (blend.RenderTargets != null)
            {
                foreach (var rt in blend.RenderTargets)
                {
                    result.Add(new PipelineColorBlendAttachmentState
                    {
                        BlendEnable = rt.BlendEnable,
                        SrcColorBlendFactor = VkFormatMapper.ToVkBlendFactor(rt.SrcBlend),
                        DstColorBlendFactor = VkFormatMapper.ToVkBlendFactor(rt.DestBlend),
                        ColorBlendOp = VkFormatMapper.ToVkBlendOp(rt.BlendOp),
                        SrcAlphaBlendFactor = VkFormatMapper.ToVkBlendFactor(rt.SrcBlendAlpha),
                        DstAlphaBlendFactor = VkFormatMapper.ToVkBlendFactor(rt.DestBlendAlpha),
                        AlphaBlendOp = VkFormatMapper.ToVkBlendOp(rt.BlendOpAlpha),
                        ColorWriteMask = (ColorComponentFlags)rt.RenderTargetWriteMask
                    });
                }
            }
            if (result.Count == 0)
            {
                result.Add(new PipelineColorBlendAttachmentState
                {
                    ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                                     ColorComponentFlags.BBit | ColorComponentFlags.ABit
                });
            }
            return result;
        }

        private static int GetFormatSize(RHIFormat fmt)
        {
            return fmt switch
            {
                RHIFormat.R8_UNorm or RHIFormat.R8_SNorm or RHIFormat.R8_UInt or RHIFormat.R8_SInt => 1,
                RHIFormat.R8G8_UNorm or RHIFormat.R8G8_SNorm or RHIFormat.R8G8_UInt or RHIFormat.R8G8_SInt => 2,
                RHIFormat.R8G8B8A8_UNorm or RHIFormat.R8G8B8A8_SNorm or RHIFormat.R8G8B8A8_UInt or RHIFormat.R8G8B8A8_SInt
                    or RHIFormat.B8G8R8A8_UNorm or RHIFormat.B8G8R8A8_UNorm_SRGB => 4,
                RHIFormat.R16_Float or RHIFormat.R16_UNorm or RHIFormat.R16_SNorm or RHIFormat.R16_UInt or RHIFormat.R16_SInt => 2,
                RHIFormat.R16G16_Float or RHIFormat.R16G16_UNorm or RHIFormat.R16G16_SNorm or RHIFormat.R16G16_UInt or RHIFormat.R16G16_SInt => 4,
                RHIFormat.R16G16B16A16_Float or RHIFormat.R16G16B16A16_UNorm or RHIFormat.R16G16B16A16_SNorm
                    or RHIFormat.R16G16B16A16_UInt or RHIFormat.R16G16B16A16_SInt => 8,
                RHIFormat.R32_Float or RHIFormat.R32_UInt or RHIFormat.R32_SInt => 4,
                RHIFormat.R32G32_Float or RHIFormat.R32G32_UInt or RHIFormat.R32G32_SInt => 8,
                RHIFormat.R32G32B32A32_Float or RHIFormat.R32G32B32A32_UInt or RHIFormat.R32G32B32A32_SInt => 16,
                RHIFormat.R10G10B10A2_UNorm or RHIFormat.R10G10B10A2_UInt or RHIFormat.R11G11B10_Float => 4,
                _ => 4
            };
        }

        public override string Name { get => _name; set => _name = value; }
        public override RHIPipelineType PipelineType => _pipelineType;

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (VkPipeline.Handle != 0)
            {
                Device.Vk.DestroyPipeline(Device.VkDevice, VkPipeline, null);
                VkPipeline = default;
            }
        }
    }
}
