// ════════════════════════════════════════════════════════════════════════
// DeferredGpuRenderer.cs — Deferred GPU 执行器
//
// 把 GpuDeferredPipeline.BuildFramePlan 的帧计划落到真实 RHI:
//   1. 分配全部渲染目标 (GBuffer MRT 8 + 深度 + SSAO/HDR/后处理链)
//   2. GpuShaderCompiler 将 GLSL 编译为 SPIR-V (无工具链时保留源码, 与既有行为一致)
//   3. 创建图形管线 (GBuffer 带 InputLayout, 全屏 pass 空布局)
//   4. Execute: 依 plan 顺序执行 — GBuffer 绘制注册网格 (MRT8+深度),
//      其余 pass 以 3 顶点全屏三角形运行
//
// 帧常量通过 CBV 根描述符注入 (与 VulkanRenderDevice 的 FrameUBO 模式一致)。
// ════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using YamaEngine.RHI;
using YamaEngine.Vulkan;

namespace YamaEngine.Rendering;

/// <summary>Deferred 光照 UBO (与 GLSL LightingUBO 一致, 8×16 = 128 字节)。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct DeferredLightingUBO
{
    /// <summary>xyz = 相机位置。</summary>
    public Vector4 CameraPos;
    /// <summary>xyz = 方向光方向, w = 强度。</summary>
    public Vector4 DirLightDir;
    /// <summary>rgb = 方向光颜色。</summary>
    public Vector4 DirLightColor;
    /// <summary>xyz = 点光位置。</summary>
    public Vector4 PointLightPos;
    /// <summary>rgb = 点光颜色, w = 范围。</summary>
    public Vector4 PointLightColor;
    /// <summary>xyz = 聚光位置, w = 内角 (弧度)。</summary>
    public Vector4 SpotLightPos;
    /// <summary>xyz = 聚光方向, w = 外角 (弧度)。</summary>
    public Vector4 SpotLightDir;
    /// <summary>rgb = 聚光颜色, w = 强度。</summary>
    public Vector4 SpotLightColor;
}

/// <summary>GBuffer 网格材质 UBO (与 GBuffer FS MaterialUBO 一致, 48 字节)。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct DeferredMaterialUBO
{
    /// <summary>rgb = albedo, a = alpha。</summary>
    public Vector4 BaseColor;
    /// <summary>rgb = 自发光, a = 强度。</summary>
    public Vector4 Emissive;
    /// <summary>r = metallic, g = roughness, b = AO, a = opacity。</summary>
    public Vector4 PbrParams;
}

/// <summary>
/// Deferred GPU 执行器：渲染目标分配 / GLSL→SPIR-V 编译 / 管线创建 / 依序执行 pass。
/// </summary>
public sealed class DeferredGpuRenderer : IDisposable
{
    private readonly RHIDevice _device;
    private readonly int _width;
    private readonly int _height;
    private readonly RenderPipelineSettings _settings;

    private GpuFramePlan? _plan;
    private readonly Dictionary<string, RHITexture> _targets = new();
    private readonly Dictionary<string, RHIDescriptorHandle> _rtvs = new();
    private RHIDescriptorHandle _dsv;
    private readonly List<RHIPipelineState> _pipelines = new();
    private RHIRootSignature? _rootSignature;

    // 常量缓冲 (b0 光照 / b1 材质)
    private RHIBuffer? _lightingCB;
    private RHIBuffer? _materialCB;
    private DeferredMaterialUBO _material = new()
    {
        BaseColor = new Vector4(0.7f, 0.7f, 0.7f, 1f),
        Emissive = new Vector4(0f, 0f, 0f, 0f),
        PbrParams = new Vector4(0f, 0.6f, 1f, 1f)
    };

    // GBuffer pass 绘制的网格 (未注册时仅清屏)
    private RHIBuffer? _meshVB;
    private RHIBuffer? _meshIB;
    private uint _meshIndexCount;
    private uint _meshVertexCount;
    private bool _hasMesh;

    private bool _initialized;

    /// <summary>帧计划。</summary>
    public GpuFramePlan? Plan => _plan;
    /// <summary>已分配渲染目标数。</summary>
    public int TargetCount => _targets.Count;
    /// <summary>已创建管线数。</summary>
    public int PipelineCount => _pipelines.Count;
    /// <summary>是否已注册网格。</summary>
    public bool HasMesh => _hasMesh;

    /// <summary>构造。</summary>
    public DeferredGpuRenderer(RHIDevice device, int width, int height, RenderPipelineSettings? settings = null)
    {
        _device = device;
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        _settings = settings ?? new RenderPipelineSettings();
    }

    /// <summary>初始化：构建帧计划 → 分配渲染目标 → 编译着色器/创建管线 → 常量缓冲。</summary>
    public void Initialize()
    {
        _plan = GpuDeferredPipeline.BuildFramePlan(_width, _height, _settings);
        AllocateTargets();
        CreateRootSignatureAndPipelines();
        CreateConstantBuffers();
        _initialized = true;
    }

    /// <summary>分配帧计划中的全部渲染目标 (GBuffer MRT / 深度 / 中间链)。</summary>
    private void AllocateTargets()
    {
        foreach (var t in _plan!.Targets.Values)
        {
            bool isDepth = t.IsDepth;
            var desc = new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = t.Format,
                Width = Math.Max(1, (int)(_width * t.WidthScale)),
                Height = Math.Max(1, (int)(_height * t.HeightScale)),
                DepthOrArraySize = 1,
                MipLevels = 1,
                SampleCount = 1,
                Flags = isDepth
                    ? RHITextureFlags.DepthStencil | RHITextureFlags.ShaderResource
                    : RHITextureFlags.RenderTarget | RHITextureFlags.ShaderResource
            };
            var tex = _device.CreateTexture(desc, t.Name);
            _targets[t.Name] = tex;
            if (isDepth)
                _dsv = _device.CreateDSV(tex);
            else
                _rtvs[t.Name] = _device.CreateRTV(tex);
        }
    }

    /// <summary>创建共享根签名 + 逐 pass 图形管线 (GLSL 经 GpuShaderCompiler 编译为 SPIR-V)。</summary>
    private void CreateRootSignatureAndPipelines()
    {
        // 共享根签名: b0 帧常量 (LightingUBO) + b1 材质 + t0-t7 纹理
        var rootDesc = new RHIRootSignatureDesc
        {
            Parameters = new RHIRootParameter[]
            {
                new() { ParameterType = RHIRootParameterType.CBV, ShaderVisibility = RHIShaderVisibility.All,
                        Descriptor = new RHIRootDescriptor { ShaderRegister = 0 } },  // 0: b0 Frame/Lighting UBO
                new() { ParameterType = RHIRootParameterType.CBV, ShaderVisibility = RHIShaderVisibility.All,
                        Descriptor = new RHIRootDescriptor { ShaderRegister = 1 } },  // 1: b1 Material UBO
                new() { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                        DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] {
                            new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.SRV, NumDescriptors = 1, BaseShaderRegister = 0 } } } }, // 2: t0
                new() { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                        DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] {
                            new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.SRV, NumDescriptors = 1, BaseShaderRegister = 1 } } } }, // 3: t1
                new() { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                        DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] {
                            new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.SRV, NumDescriptors = 1, BaseShaderRegister = 2 } } } }, // 4: t2
                new() { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                        DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] {
                            new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.SRV, NumDescriptors = 1, BaseShaderRegister = 3 } } } }, // 5: t3
                new() { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                        DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] {
                            new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.SRV, NumDescriptors = 1, BaseShaderRegister = 4 } } } }, // 6: t4
                new() { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                        DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] {
                            new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.SRV, NumDescriptors = 1, BaseShaderRegister = 5 } } } }, // 7: t5
                new() { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                        DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] {
                            new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.SRV, NumDescriptors = 1, BaseShaderRegister = 6 } } } }, // 8: t6
                new() { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                        DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] {
                            new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.SRV, NumDescriptors = 1, BaseShaderRegister = 7 } } } }, // 9: t7
            },
            StaticSamplers = new[] { RHISamplerDesc.LinearWrap, RHISamplerDesc.PointClamp },
            AllowInputAssembler = true
        };
        _rootSignature = _device.CreateRootSignature(rootDesc, "DeferredGpu.RootSig");

        // GBuffer 网格顶点布局: pos(12) + normal(12) + uv(8) + tangent(12) = 44 字节
        var gbufferLayout = new RHIInputElement[]
        {
            new("POSITION", RHIFormat.R32G32B32_Float, 0, 0),
            new("NORMAL", RHIFormat.R32G32B32_Float, 0, 12),
            new("TEXCOORD", RHIFormat.R32G32_Float, 0, 24),
            new("TANGENT", RHIFormat.R32G32B32_Float, 0, 32),
        };

        foreach (var pd in _plan!.BuildPipelineDescs())
        {
            var desc = pd.Desc;
            desc.RootSignature = _rootSignature;
            desc.VS = CompileOrPassthrough(desc.VS, pd.Name + ".VS");
            desc.PS = CompileOrPassthrough(desc.PS, pd.Name + ".PS");
            desc.InputLayout = pd.Kind == GpuPassKind.GBuffer
                ? gbufferLayout
                : Array.Empty<RHIInputElement>();
            _pipelines.Add(_device.CreateGraphicsPipelineState(desc, pd.Name));
        }
    }

    /// <summary>
    /// GLSL 源码字节码 → SPIR-V (GpuShaderCompiler)。
    /// 无工具链 (headless/CI) 时保留源码字节码原样返回。
    /// </summary>
    private RHIShaderBytecode CompileOrPassthrough(RHIShaderBytecode? source, string name)
    {
        if (!source.HasValue) return default;
        var bc = source.Value;
        if (bc.Bytecode == null || bc.Bytecode.Length == 0)
            return bc;
        var stage = bc.Stage == RHIShaderStage.Vertex ? ShaderStageExt.Vertex : ShaderStageExt.Fragment;
        var glsl = Encoding.UTF8.GetString(bc.Bytecode);
        var r = GpuShaderCompiler.TryCompileToSpirv(glsl, stage, bc.EntryPoint, name);
        if (r.Success && r.Spirv != null)
            return new RHIShaderBytecode(r.Spirv, bc.EntryPoint, bc.Stage, name);
        return bc;
    }

    /// <summary>创建帧常量缓冲 (b0 光照 UBO / b1 材质 UBO)。</summary>
    private void CreateConstantBuffers()
    {
        _lightingCB = _device.CreateBuffer(new RHIBufferDesc
        {
            SizeInBytes = 128,
            Flags = RHIBufferFlags.ConstantBuffer,
            HeapType = RHIResourceHeapType.Upload
        }, "DeferredGpu.LightingCB");
        _materialCB = _device.CreateBuffer(new RHIBufferDesc
        {
            SizeInBytes = 48,
            Flags = RHIBufferFlags.ConstantBuffer,
            HeapType = RHIResourceHeapType.Upload
        }, "DeferredGpu.MaterialCB");
        var mat = _material;
        _device.UploadToBuffer(_materialCB, MemoryMarshal.CreateReadOnlySpan(ref mat, 1));
    }

    /// <summary>
    /// 注册 GBuffer pass 绘制的网格 (CPU 顶点数据, 布局 pos+normal+uv+tangent = 44 字节/顶点)。
    /// </summary>
    public void SetMesh(Vector3[] positions, Vector3[] normals, Vector2[]? uvs, Vector3[]? tangents, uint[] indices)
    {
        int count = Math.Min(positions.Length, normals.Length);
        var verts = new byte[count * 44];
        for (int i = 0; i < count; i++)
        {
            int o = i * 44;
            WriteVec3(verts, o, positions[i]);
            WriteVec3(verts, o + 12, normals[i]);
            WriteVec2(verts, o + 24, uvs != null && i < uvs.Length ? uvs[i] : Vector2.Zero);
            WriteVec3(verts, o + 32, tangents != null && i < tangents.Length ? tangents[i] : new Vector3(1, 0, 0));
        }
        _meshVB?.Dispose();
        _meshVB = _device.CreateBuffer(new RHIBufferDesc
        {
            SizeInBytes = (ulong)verts.Length,
            Flags = RHIBufferFlags.VertexBuffer,
            HeapType = RHIResourceHeapType.Upload
        }, "DeferredGpu.MeshVB");
        _device.UploadToBuffer(_meshVB, verts);

        _meshIB?.Dispose();
        _meshIB = _device.CreateBuffer(new RHIBufferDesc
        {
            SizeInBytes = (ulong)(indices.Length * 4),
            Flags = RHIBufferFlags.IndexBuffer,
            HeapType = RHIResourceHeapType.Upload
        }, "DeferredGpu.MeshIB");
        _device.UploadToBuffer(_meshIB, indices);

        _meshIndexCount = (uint)indices.Length;
        _meshVertexCount = (uint)count;
        _hasMesh = _meshIndexCount > 0;
    }

    /// <summary>设置 GBuffer 材质参数 (albedo/自发光/PBR)。</summary>
    public void SetMaterial(in DeferredMaterialUBO material)
    {
        _material = material;
        var mat = _material;
        _device.UploadToBuffer(_materialCB!, MemoryMarshal.CreateReadOnlySpan(ref mat, 1));
    }

    /// <summary>
    /// 执行一帧 Deferred 渲染: 依帧计划顺序运行全部 pass。
    /// </summary>
    /// <param name="cmdList">命令列表。</param>
    /// <param name="lighting">光照 UBO (相机 + 三类光源)。</param>
    public void Execute(RHICommandList cmdList, in DeferredLightingUBO lighting)
    {
        if (!_initialized || _plan == null) return;

        // 上传光照 UBO (b0)
        var ubo = lighting;
        _device.UploadToBuffer(_lightingCB!, MemoryMarshal.CreateReadOnlySpan(ref ubo, 1));
        cmdList.SetGraphicsRootSignature(_rootSignature!);

        int pipelineIdx = 0;
        foreach (var pass in _plan.Passes)
        {
            var pipeline = _pipelines[pipelineIdx++];
            cmdList.SetPipelineState(pipeline);

            if (pass.Kind == GpuPassKind.GBuffer)
            {
                ExecuteGBuffer(cmdList, pass);
                continue;
            }

            // ── 全屏 pass: 单颜色目标 + 3 顶点三角形 ──
            if (pass.ColorTargets.Count > 0 && _rtvs.TryGetValue(pass.ColorTargets[0], out var rtv))
            {
                cmdList.TransitionTo(_targets[pass.ColorTargets[0]], RHIResourceState.RenderTarget);
                cmdList.OMSetRenderTargets(new[] { rtv }, default);
                cmdList.SetViewport(new RHIViewport(_width, _height));
            }

            if (pass.Kind == GpuPassKind.DeferredLighting)
            {
                // 绑定 GBuffer 纹理 (t0-t5) + 光照 UBO (b0)
                BindTexture(cmdList, 2, "GBuffer.WorldPos");
                BindTexture(cmdList, 3, "GBuffer.Albedo");
                BindTexture(cmdList, 4, "GBuffer.Normal");
                BindTexture(cmdList, 5, "GBuffer.PBR");
                BindTexture(cmdList, 6, "GBuffer.Emissive");
                if (_targets.TryGetValue("Scene.SSAO", out var ssao))
                {
                    cmdList.TransitionTo(ssao, RHIResourceState.NonPixelShaderResource);
                    cmdList.SetGraphicsRootDescriptorTable(7, _device.CreateSRV(ssao));
                }
                cmdList.SetGraphicsRootConstantBufferView(0, _lightingCB!);
            }

            cmdList.DrawInstanced(3, 1, 0, 0);
        }
    }

    /// <summary>GBuffer pass: MRT 8 + 深度清屏, 绘制注册网格。</summary>
    private void ExecuteGBuffer(RHICommandList cmdList, GpuPassRecord pass)
    {
        var rtvs = new RHIDescriptorHandle[pass.ColorTargets.Count];
        for (int i = 0; i < pass.ColorTargets.Count; i++)
        {
            rtvs[i] = _rtvs[pass.ColorTargets[i]];
            cmdList.TransitionTo(_targets[pass.ColorTargets[i]], RHIResourceState.RenderTarget);
        }
        RHITexture? depthTex = null;
        bool hasDepth = !string.IsNullOrEmpty(pass.DepthTarget) && _targets.TryGetValue(pass.DepthTarget!, out depthTex);
        if (hasDepth)
            cmdList.TransitionTo(depthTex!, RHIResourceState.DepthWrite);

        cmdList.OMSetRenderTargets(rtvs, _dsv);
        // SSAO 在计划中位于 Lighting 之后执行; 先清为 1 使 Lighting 阶段 AO 中性
        foreach (var name in pass.ColorTargets)
        {
            float c = name == "Scene.SSAO" ? 1f : 0f;
            cmdList.ClearRenderTargetView(_rtvs[name], new Vector4(c, c, c, 1f));
        }
        if (hasDepth)
            cmdList.ClearDepthStencilView(_dsv, 1.0f, 0);
        cmdList.SetViewport(new RHIViewport(_width, _height));

        if (_hasMesh && _meshVB != null && _meshIB != null)
        {
            cmdList.SetGraphicsRootConstantBufferView(0, _lightingCB!);   // FrameUBO (b0)
            cmdList.SetGraphicsRootConstantBufferView(1, _materialCB!);   // MaterialUBO (b1)
            cmdList.IASetPrimitiveTopology(RHIPrimitiveTopology.TriangleList);
            cmdList.IASetVertexBuffers(0, _meshVB, 0, 44, 44 * (int)_meshVertexCount);
            cmdList.IASetIndexBuffer(_meshIB, 0, _meshIndexCount * 4, RHIFormat.R32_UInt);
            cmdList.DrawIndexedInstanced(_meshIndexCount, 1, 0, 0, 0);
        }

        // 转入后续 pass 可采样状态
        foreach (var name in pass.ColorTargets)
            cmdList.TransitionTo(_targets[name], RHIResourceState.NonPixelShaderResource);
        if (hasDepth)
            cmdList.TransitionTo(depthTex!, RHIResourceState.DepthRead);
    }

    /// <summary>绑定纹理到指定根参数 (自动状态转换 + SRV 创建)。</summary>
    private void BindTexture(RHICommandList cmdList, int param, string targetName)
    {
        if (_targets.TryGetValue(targetName, out var tex))
        {
            cmdList.TransitionTo(tex, RHIResourceState.NonPixelShaderResource);
            cmdList.SetGraphicsRootDescriptorTable(param, _device.CreateSRV(tex));
        }
    }

    private static void WriteVec3(byte[] dst, int offset, Vector3 v)
    {
        BitConverter.TryWriteBytes(dst.AsSpan(offset), v.X);
        BitConverter.TryWriteBytes(dst.AsSpan(offset + 4), v.Y);
        BitConverter.TryWriteBytes(dst.AsSpan(offset + 8), v.Z);
    }

    private static void WriteVec2(byte[] dst, int offset, Vector2 v)
    {
        BitConverter.TryWriteBytes(dst.AsSpan(offset), v.X);
        BitConverter.TryWriteBytes(dst.AsSpan(offset + 4), v.Y);
    }

    /// <summary>释放全部 GPU 资源。</summary>
    public void Dispose()
    {
        foreach (var p in _pipelines) p.Dispose();
        _pipelines.Clear();
        _rootSignature?.Dispose();
        _rootSignature = null;
        foreach (var t in _targets.Values) t.Dispose();
        _targets.Clear();
        _rtvs.Clear();
        _lightingCB?.Dispose();
        _materialCB?.Dispose();
        _meshVB?.Dispose();
        _meshIB?.Dispose();
        _lightingCB = null;
        _materialCB = null;
        _meshVB = null;
        _meshIB = null;
        _initialized = false;
        GC.SuppressFinalize(this);
    }
}
