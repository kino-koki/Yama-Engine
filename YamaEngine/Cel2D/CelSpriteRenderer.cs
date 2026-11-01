// ============================================================================================
//  CelSpriteRenderer.cs
//
//  yama引擎 (YamaEngine) 赛璐璐 / 二次元 2D 渲染器 —— GPU 层
//
//  绘制路径（镜像现有 GPUParticleSystem / PostProcessSystem 的 RHI 用法）：
//    根签名: [0] CBV b0 帧常量 · [1] 描述符表 SRV t0 实例缓冲 · [2] 描述符表 SRV t1 精灵纹理
//    内联 HLSL 着色器: VSMain(SV_VertexID 展开四边形) + PSMain(赛璐璐量化+描边+边缘光+网点)
//
//  命名空间：YamaEngine.Cel2D  ·  目标框架：net10.0
// ============================================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using YamaEngine.RHI;

namespace YamaEngine.Cel2D
{
    /// <summary>单次绘制的精灵实例（渲染层输入）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CelDrawItem
    {
        public Vector2 Position;
        public Vector2 Size;
        public float Rotation;
        public float Z;
        public Vector4 UvRect;      // u0,v0,u1,v1
        public Vector4 Color;       // 色调 rgba
        public float BandCount;     // 赛璐璐量化色阶
        public float OutlineWidth;  // 描边宽度(纹素)
        public float RimIntensity;  // 边缘光强度
        public float Halftone;      // 网点密度(0=关)
    }

    /// <summary>GPU 实例布局（与 HLSL CelInstance 严格对齐，64 字节）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct CelInstance
    {
        public Vector4 PosSize;   // x,y=中心; z,w=尺寸
        public Vector4 UvRect;    // u0,v0,u1,v1
        public Vector4 Color;     // 色调 rgba
        public Vector4 Shading;   // x=色阶, y=描边宽, z=边缘光, w=网点
    }

    /// <summary>帧常量（与 HLSL FrameCB 严格对齐，96 字节）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct CelFrameCB
    {
        public Matrix4x4 Proj;        // 64 字节
        public Vector4 Viewport;      // 16 字节: x=屏宽, y=屏高, z=时间, w=保留
        public Vector4 Texel;         // 16 字节: x=1/texW, y=1/texH, z=保留, w=保留
    }

    /// <summary>
    /// 赛璐璐 2D 精灵渲染器。
    /// 按纹理分批：一次 Draw 绑定一个纹理 SRV，绘制该纹理下所有实例。
    /// </summary>
    public sealed class CelSpriteRenderer : IDisposable
    {
        private const int MaxInstances = 2048;
        private const uint InstanceSize = 64;      // 字节
        private const uint FrameCBSize = 96;       // 字节

        private readonly RHIDevice _device;
        private readonly RHIFormat _rtvFormat;

        private RHIPipelineState? _celPSO;
        private RHIRootSignature? _rootSig;
        private RHIBuffer? _frameCB;
        private RHIBuffer? _instanceBuffer;
        private RHIDescriptorHandle _instanceSRV;

        private readonly CelInstance[] _instances = new CelInstance[MaxInstances];

        public bool IsInitialized => _celPSO != null;

        public CelSpriteRenderer(RHIDevice device, RHIFormat rtvFormat = RHIFormat.R16G16B16A16_Float)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _rtvFormat = rtvFormat;
        }

        /// <summary>创建管线、根签名与缓冲。</summary>
        public void Initialize()
        {
            CreateRootSignature();
            CreatePipeline();
            CreateBuffers();
        }

        private void CreateRootSignature()
        {
            _rootSig = _device.CreateRootSignature(new RHIRootSignatureDesc
            {
                Parameters = new[]
                {
                    RootCBV(0, RHIShaderVisibility.All),
                    RootSRVTable(0, 1),   // t0 实例缓冲
                    RootSRVTable(1, 1)    // t1 精灵纹理
                },
                StaticSamplers = new[] { RHISamplerDesc.LinearClamp }
            });
        }

        private void CreatePipeline()
        {
            const string celHlsl = @"
                struct CelInstance {
                    float4 posSize;
                    float4 uvRect;
                    float4 color;
                    float4 shading;
                };
                StructuredBuffer<CelInstance> Instances : register(t0);
                Texture2D SpriteTex : register(t1);
                SamplerState TexSampler : register(s0);
                cbuffer FrameCB : register(b0) {
                    float4x4 gProj;
                    float4 gViewport;
                    float4 gTexel;
                };
                struct VSOut {
                    float4 Pos : SV_Position;
                    float2 UV : TEXCOORD0;
                    float4 Color : COLOR0;
                    float4 Shading : COLOR1;
                };
                VSOut VSMain(uint vid : SV_VertexID, uint iid : SV_InstanceID) {
                    CelInstance inst = Instances[iid];
                    float2 corner = float2((vid & 1) ? 1 : -1, (vid & 2) ? 1 : -1);
                    float2 center = inst.posSize.xy;
                    float2 size = inst.posSize.zw;
                    float2 world = center + corner * size * 0.5;
                    VSOut o;
                    o.Pos = mul(gProj, float4(world, 0, 1));
                    o.UV = lerp(inst.uvRect.xy, inst.uvRect.zw, corner * 0.5 + 0.5);
                    o.Color = inst.color;
                    o.Shading = inst.shading;
                    return o;
                }
                float4 PSMain(VSOut i) : SV_Target {
                    float4 tex = SpriteTex.Sample(TexSampler, i.UV);
                    if (tex.a < 0.004) discard;
                    float bands = max(1.0, i.Shading.x);
                    float outlineW = max(0.0, i.Shading.y);
                    float rim = max(0.0, i.Shading.z);
                    float halftone = i.Shading.w;
                    // 赛璐璐量化色阶：按亮度切 N 段
                    float lum = dot(tex.rgb, float3(0.299, 0.587, 0.114));
                    float banded = floor(lum * bands + 0.5) / bands;
                    float toon = lerp(0.62, 1.06, banded);
                    float3 col = tex.rgb * toon * i.Color.rgb;
                    // 阴影区叠加半调网点
                    if (halftone > 0.001 && banded < 0.5) {
                        float2 sp = i.UV * halftone;
                        float dotv = frac(sp.x) * frac(sp.y);
                        if (dotv < 0.12) col *= 0.85;
                    }
                    // 边缘光 rim：靠近精灵边界提亮
                    float2 d = abs(i.UV - 0.5) * 2.0;
                    float edge = saturate(max(d.x, d.y));
                    col += pow(edge, 3.0) * rim * float3(1.0, 0.97, 0.9);
                    // 描边：4 向 alpha 采样，透明邻域 => 线稿色
                    if (outlineW > 0.05) {
                        float2 off = gTexel.xy * outlineW;
                        float ea = SpriteTex.Sample(TexSampler, i.UV + float2(off.x, 0)).a;
                        float eb = SpriteTex.Sample(TexSampler, i.UV - float2(off.x, 0)).a;
                        float ec = SpriteTex.Sample(TexSampler, i.UV + float2(0, off.y)).a;
                        float ed = SpriteTex.Sample(TexSampler, i.UV - float2(0, off.y)).a;
                        float inside = min(min(ea, eb), min(ec, ed));
                        if (inside < 0.1 && tex.a > 0.1) col = float3(0.10, 0.08, 0.13);
                    }
                    return float4(col, tex.a * i.Color.a);
                }
            ";

            _celPSO = _device.CreateGraphicsPipelineState(new RHIGraphicsPipelineDesc
            {
                Name = "CelSpritePSO",
                VS = _device.CompileShader(celHlsl, "VSMain", RHIShaderStage.Vertex, _device.MaxShaderModel),
                PS = _device.CompileShader(celHlsl, "PSMain", RHIShaderStage.Pixel, _device.MaxShaderModel),
                PrimitiveTopology = RHIPrimitiveTopology.TriangleStrip,
                BlendState = RHIBlendDesc.AlphaBlend,
                DepthStencilState = RHIDepthDesc.None,
                RasterizerState = RHIRasterizerDesc.NoCull,
                RTVFormats = new[] { _rtvFormat },
                DSVFormat = RHIFormat.Unknown
            });
        }

        private void CreateBuffers()
        {
            _frameCB = _device.CreateBuffer(FrameCBSize, RHIBufferUsage.ConstantBuffer, "CelFrameCB");
            _instanceBuffer = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = (ulong)MaxInstances * InstanceSize,
                BindFlags = RHIBindFlags.StructuredBuffer | RHIBindFlags.ShaderResource,
                StructureByteStride = InstanceSize
            }, "CelInstanceBuffer");
            _instanceSRV = _device.CreateSRV(_instanceBuffer, RHIFormat.Unknown, true,
                0, MaxInstances, InstanceSize);
        }

        /// <summary>
        /// 绘制一批精灵。同一次调用绑定单一纹理。
        /// </summary>
        /// <param name="cmd">命令列表。</param>
        /// <param name="rtv">渲染目标句柄。</param>
        /// <param name="target">渲染目标纹理（用于获取尺寸）。</param>
        /// <param name="textureSRV">精灵纹理 SRV。</param>
        /// <param name="items">精灵实例。</param>
        /// <param name="proj">正交投影矩阵（像素空间到裁剪空间）。</param>
        /// <param name="texelSize">1/纹理宽, 1/纹理高。</param>
        /// <param name="time">累计时间（秒，用于未来动画效果）。</param>
        public void Draw(RHICommandList cmd, RHIDescriptorHandle rtv, RHITexture target,
            RHIDescriptorHandle textureSRV, IReadOnlyList<CelDrawItem> items,
            in Matrix4x4 proj, Vector2 texelSize, float time = 0)
        {
            if (_celPSO == null || _rootSig == null || _frameCB == null || _instanceBuffer == null) return;
            if (!textureSRV.IsValid || items.Count == 0) return;

            int count = Math.Min(items.Count, MaxInstances);
            for (int i = 0; i < count; i++)
                _instances[i] = ToInstance(items[i]);

            var cb = new CelFrameCB
            {
                Proj = proj,
                Viewport = new Vector4(target?.Width ?? 0, target?.Height ?? 0, time, 0),
                Texel = new Vector4(texelSize.X, texelSize.Y, 0, 0)
            };

            _device.UploadToBuffer(_frameCB, new[] { cb });
            _device.UploadToBuffer(_instanceBuffer, new Span<CelInstance>(_instances, 0, count));

            cmd.SetGraphicsRootSignature(_rootSig);
            cmd.SetPipelineState(_celPSO);
            cmd.SetGraphicsRootConstantBufferView(0, _frameCB);
            cmd.SetGraphicsRootDescriptorTable(1, _instanceSRV);
            cmd.SetGraphicsRootDescriptorTable(2, textureSRV);
            cmd.IASetPrimitiveTopology(RHIPrimitiveTopology.TriangleStrip);
            cmd.OMSetRenderTargets(new[] { rtv }, null);
            cmd.RSSetViewport(0, 0, target?.Width ?? 0, target?.Height ?? 0, 0, 1);
            cmd.DrawInstanced(4, (uint)count, 0, 0);
        }

        private static CelInstance ToInstance(in CelDrawItem item)
        {
            return new CelInstance
            {
                PosSize = new Vector4(item.Position.X, item.Position.Y, item.Size.X, item.Size.Y),
                UvRect = item.UvRect,
                Color = item.Color,
                Shading = new Vector4(
                    MathF.Max(1f, item.BandCount),
                    MathF.Max(0f, item.OutlineWidth),
                    MathF.Max(0f, item.RimIntensity),
                    MathF.Max(0f, item.Halftone))
            };
        }

        // ── 根签名辅助 ──
        private static RHIRootParameter RootCBV(uint reg, RHIShaderVisibility vis) => new()
        {
            ParameterType = RHIRootParameterType.CBV,
            ShaderVisibility = vis,
            Descriptor = new RHIRootDescriptor { ShaderRegister = reg }
        };

        private static RHIRootParameter RootSRVTable(uint baseRegister, uint numDescriptors) => new()
        {
            ParameterType = RHIRootParameterType.DescriptorTable,
            ShaderVisibility = RHIShaderVisibility.All,
            DescriptorTable = new RHIRootDescriptorTable
            {
                Ranges = new[]
                {
                    new RHIRootDescriptorRange
                    {
                        RangeType = RHIDescriptorRangeType.SRV,
                        NumDescriptors = (int)numDescriptors,
                        BaseShaderRegister = baseRegister
                    }
                }
            }
        };

        public void Dispose()
        {
            _instanceSRV = default;
            _instanceBuffer?.Dispose();
            _frameCB?.Dispose();
            _celPSO?.Dispose();
            _rootSig?.Dispose();
        }
    }
}
