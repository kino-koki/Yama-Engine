// =============================================================================
// RHIPipelineStateManager.cs - yama引擎 RHI 管线状态管理
// =============================================================================
// 管线状态对象 (PSO) 的缓存和管理。VkPipeline / ID3D12PipelineState 统一抽象。
//
// 主要功能：
//   1. PSO 缓存 (hash key 去重)
//   2. PSO 状态构建器 (流式 API)
//   3. 热重载 (shader 改了重建)
//   4. 延迟创建 (首次用才创建)
//
// FIXME: hash 计算还没包含所有字段
// TODO: 加 PSO 池化，LRU 淘汰
//
// 作者: yama引擎团队
// =============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace YamaEngine.RHI
{
    // ════════════════════════════════════════════════════════════════════════════
    // 区域 1: 管线状态 key
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>管线状态 key。</summary>
    public readonly struct RHIPipelineStateKey : IEquatable<RHIPipelineStateKey>
    {
        public readonly ulong Hash;
        public RHIPipelineStateKey(ulong hash) => Hash = hash;
        public bool Equals(RHIPipelineStateKey other) => Hash == other.Hash;
        public override bool Equals(object obj) => obj is RHIPipelineStateKey k && Equals(k);
        public override int GetHashCode() => (int)Hash;
        public static bool operator ==(RHIPipelineStateKey a, RHIPipelineStateKey b) => a.Hash == b.Hash;
        public static bool operator !=(RHIPipelineStateKey a, RHIPipelineStateKey b) => a.Hash != b.Hash;
        public override string ToString() => $"PSO@0x{Hash:X16}";
    }

    /// <summary>管线状态统计。</summary>
    public struct PipelineStateManagerStats
    {
        public int TotalCreated;
        public int CacheHits;
        public int CacheMisses;
        public int CurrentlyCached;
        public int EvictedCount;

        public float HitRate
            => (CacheHits + CacheMisses) == 0 ? 0 : (float)CacheHits / (CacheHits + CacheMisses);

        public override string ToString()
            => $"PSO: created={TotalCreated}, cached={CurrentlyCached}, " +
               $"hits={CacheHits} ({HitRate * 100:F1}%), evicted={EvictedCount}";
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 2: 管线状态描述 (完整版)
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>完整的 graphics pipeline 状态描述。</summary>
    public class RHIPipelineStateDesc
    {
        // shaders
        public RHIShader VertexShader;
        public RHIShader PixelShader;
        public RHIShader GeometryShader;
        public RHIShader HullShader;
        public RHIShader DomainShader;
        public RHIShader MeshShader;
        public RHIShader AmplificationShader;

        // input layout
        public RHIInputElement[] InputElements;

        // primitive topology
        public RHIPrimitiveTopology Topology = RHIPrimitiveTopology.TriangleList;

        // rasterizer
        public RHIFillMode FillMode = RHIFillMode.Solid;
        public RHICullMode CullMode = RHICullMode.Back;
        public bool FrontCounterClockwise = true;
        public int DepthBias = 0;
        public float DepthBiasClamp = 0;
        public float SlopeScaledDepthBias = 0;
        public bool DepthClipEnable = true;
        public bool MultisampleEnable = false;
        public bool AntialiasedLineEnable = false;
        public bool ConservativeRaster = false;

        // blend
        public bool AlphaToCoverageEnable = false;
        public bool IndependentBlendEnable = false;
        public RHIRenderTargetBlend[] RenderTargetBlends;

        // depth stencil
        public bool DepthEnable = true;
        public bool DepthWriteEnable = true;
        public RHICompareFunction DepthFunc = RHICompareFunction.Less;
        public bool StencilEnable = false;
        public RHIStencilOpDesc FrontFace;
        public RHIStencilOpDesc BackFace;

        // render targets
        public RHIFormat[] RTVFormats;
        public RHIFormat DSVFormat = RHIFormat.Unknown;
        public uint SampleCount = 1;
        public uint SampleMask = 0xFFFFFFFF;

        // misc
        public RHIRootSignature RootSignature;
        public string Name;

        // ── 高层兼容属性（渲染层以这些命名/类型构造描述；与上方字段语义一致）──
        /// <summary>光栅化状态（高层 RHIRasterizerState 兼容）。</summary>
        public RHIRasterizerState RasterizerState;
        /// <summary>深度模板状态（高层 RHIDepthStencilState 兼容）。</summary>
        public RHIDepthStencilState DepthStencilState;
        /// <summary>输入布局（高层 RHIInputLayoutDesc 兼容）。</summary>
        public RHIInputLayoutDesc[] InputLayout;
        /// <summary>渲染目标格式（高层 RHIPixelFormat 兼容）。</summary>
        public RHIPixelFormat[] RenderTargetFormats;
        /// <summary>深度模板格式（高层 RHIPixelFormat 兼容）。</summary>
        public RHIPixelFormat DepthStencilFormat;
        /// <summary>图元拓扑（底层命名兼容别名）。</summary>
        public RHIPrimitiveTopology PrimitiveTopology
        {
            get => Topology;
            set => Topology = value;
        }

        /// <summary>计算 hash key。</summary>
        public RHIPipelineStateKey ComputeKey()
        {
            ulong hash = 14695981039346656037UL;
            unchecked
            {
                hash = Mix(hash, VertexShader?.Id ?? 0);
                hash = Mix(hash, PixelShader?.Id ?? 0);
                hash = Mix(hash, GeometryShader?.Id ?? 0);
                hash = Mix(hash, HullShader?.Id ?? 0);
                hash = Mix(hash, DomainShader?.Id ?? 0);
                hash = Mix(hash, MeshShader?.Id ?? 0);
                hash = Mix(hash, AmplificationShader?.Id ?? 0);
                hash = Mix(hash, (uint)Topology);
                hash = Mix(hash, (uint)FillMode);
                hash = Mix(hash, (uint)CullMode);
                hash = Mix(hash, FrontCounterClockwise ? 1u : 0u);
                hash = Mix(hash, (uint)DepthBias);
                hash = Mix(hash, DepthEnable ? 1u : 0u);
                hash = Mix(hash, DepthWriteEnable ? 1u : 0u);
                hash = Mix(hash, (uint)DepthFunc);
                hash = Mix(hash, StencilEnable ? 1u : 0u);
                hash = Mix(hash, SampleCount);
                hash = Mix(hash, SampleMask);
                hash = Mix(hash, (uint)DSVFormat.GetHashCode());
                if (RTVFormats != null)
                {
                    foreach (var f in RTVFormats)
                        hash = Mix(hash, (uint)f.GetHashCode());
                }
                if (RenderTargetBlends != null)
                {
                    foreach (var b in RenderTargetBlends)
                    {
                        hash = Mix(hash, b.BlendEnable ? 1u : 0u);
                        hash = Mix(hash, (uint)b.SrcBlend);
                        hash = Mix(hash, (uint)b.DestBlend);
                        hash = Mix(hash, (uint)b.BlendOp);
                        hash = Mix(hash, (uint)b.SrcBlendAlpha);
                        hash = Mix(hash, (uint)b.DestBlendAlpha);
                        hash = Mix(hash, (uint)b.BlendOpAlpha);
                        hash = Mix(hash, (uint)b.RenderTargetWriteMask);
                    }
                }
            }
            return new RHIPipelineStateKey(hash);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong Mix(ulong hash, ulong v)
        {
            unchecked { hash ^= v; hash *= 1099511628211UL; return hash; }
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong Mix(ulong hash, uint v) => Mix(hash, (ulong)v);

        /// <summary>隐式转换为底层 <see cref="RHIGraphicsPipelineDesc"/>（供 RHIDevice.CreatePipelineState 使用）。</summary>
        public static implicit operator RHIGraphicsPipelineDesc(RHIPipelineStateDesc d) => new()
        {
            Name = d.Name ?? "",
            PrimitiveTopology = d.Topology,
            RTVFormats = d.RTVFormats ?? Array.Empty<RHIFormat>(),
            DSVFormat = d.DSVFormat,
            SampleCount = (int)d.SampleCount,
            DepthStencilState = new RHIDepthDesc
            {
                DepthEnable = d.DepthEnable,
                DepthWriteEnable = d.DepthWriteEnable,
                DepthFunc = (RHIComparison)d.DepthFunc,
                StencilEnable = d.StencilEnable,
                StencilReadMask = 0xFF,
                StencilWriteMask = 0xFF,
                FrontFace = RHIDepthStencilOp.Default,
                BackFace = RHIDepthStencilOp.Default,
            },
            RasterizerState = new RHIRasterizerDesc
            {
                FillMode = d.FillMode,
                CullMode = d.CullMode,
                FrontCounterClockwise = d.FrontCounterClockwise,
                DepthBias = d.DepthBias,
                DepthBiasClamp = d.DepthBiasClamp,
                SlopeScaledDepthBias = d.SlopeScaledDepthBias,
                DepthClipEnable = d.DepthClipEnable,
                MultisampleEnable = d.MultisampleEnable,
                AntialiasedLineEnable = d.AntialiasedLineEnable,
                ConservativeRaster = d.ConservativeRaster,
            },
        };
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 3: 管线状态缓存条目
    // ════════════════════════════════════════════════════════════════════════════

    internal sealed class PipelineStateEntry
    {
        public RHIPipelineStateKey Key;
        public RHIPipeline Pipeline;
        public RHIPipelineStateDesc Desc;
        public DateTime CreatedAt;
        public long LastUsedFrame;
        public int UseCount;
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 4: 管线状态管理器主类
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 管线状态管理器主类。
    /// </summary>
    public sealed class RHIPipelineStateManager : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly Dictionary<RHIPipelineStateKey, PipelineStateEntry> _cache = new();
        private readonly object _lock = new();
        private PipelineStateManagerStats _stats;
        private long _currentFrame;
        private const long DefaultEvictFrames = 600;  // 10 秒 (60fps)

        public ref PipelineStateManagerStats Stats => ref _stats;

        public RHIPipelineStateManager(RHIDevice device)
        {
            _device = device;
            _stats = new PipelineStateManagerStats();
        }

        /// <summary>获取或创建 PSO。</summary>
        public RHIPipeline GetOrCreate(RHIPipelineStateDesc desc)
        {
            var key = desc.ComputeKey();
            lock (_lock)
            {
                if (_cache.TryGetValue(key, out var entry))
                {
                    _stats.CacheHits++;
                    entry.UseCount++;
                    entry.LastUsedFrame = _currentFrame;
                    return entry.Pipeline;
                }
            }

            // 创建
            _stats.CacheMisses++;
            var pipeline = CreatePipeline(desc);

            var newEntry = new PipelineStateEntry
            {
                Key = key,
                Pipeline = pipeline,
                Desc = desc,
                CreatedAt = DateTime.UtcNow,
                LastUsedFrame = _currentFrame,
            };
            lock (_lock)
            {
                _cache[key] = newEntry;
                _stats.TotalCreated++;
                _stats.CurrentlyCached = _cache.Count;
            }
            return pipeline;
        }

        /// <summary>尝试获取已缓存的 PSO。</summary>
        public bool TryGet(RHIPipelineStateKey key, out RHIPipeline pipeline)
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(key, out var entry))
                {
                    _stats.CacheHits++;
                    entry.UseCount++;
                    entry.LastUsedFrame = _currentFrame;
                    pipeline = entry.Pipeline;
                    return true;
                }
            }
            pipeline = null;
            return false;
        }

        /// <summary>使某个 PSO 失效 (shader 改了之后调用)。</summary>
        public void Invalidate(RHIPipelineStateKey key)
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(key, out var entry))
                {
                    entry.Pipeline?.Dispose();
                    _cache.Remove(key);
                    _stats.CurrentlyCached = _cache.Count;
                }
            }
        }

        /// <summary>按 shader 失效所有相关 PSO。</summary>
        public void InvalidateByShader(RHIShader shader)
        {
            if (shader == null) return;
            lock (_lock)
            {
                var toRemove = new List<RHIPipelineStateKey>();
                foreach (var kv in _cache)
                {
                    var d = kv.Value.Desc;
                    if (ReferenceEquals(d.VertexShader, shader) ||
                        ReferenceEquals(d.PixelShader, shader) ||
                        ReferenceEquals(d.GeometryShader, shader) ||
                        ReferenceEquals(d.HullShader, shader) ||
                        ReferenceEquals(d.DomainShader, shader) ||
                        ReferenceEquals(d.MeshShader, shader) ||
                        ReferenceEquals(d.AmplificationShader, shader))
                    {
                        toRemove.Add(kv.Key);
                    }
                }
                foreach (var k in toRemove)
                {
                    if (_cache.TryGetValue(k, out var e))
                        e.Pipeline?.Dispose();
                    _cache.Remove(k);
                }
                _stats.CurrentlyCached = _cache.Count;
            }
        }

        /// <summary>每帧调用，淘汰长时间不用的。</summary>
        public void TickFrame(long frameIndex)
        {
            _currentFrame = frameIndex;
            EvictUnused(DefaultEvictFrames);
        }

        /// <summary>淘汰长时间不用的。</summary>
        public void EvictUnused(long maxIdleFrames)
        {
            lock (_lock)
            {
                var toRemove = new List<RHIPipelineStateKey>();
                foreach (var kv in _cache)
                {
                    if (_currentFrame - kv.Value.LastUsedFrame > maxIdleFrames)
                        toRemove.Add(kv.Key);
                }
                foreach (var k in toRemove)
                {
                    if (_cache.TryGetValue(k, out var e))
                        e.Pipeline?.Dispose();
                    _cache.Remove(k);
                }
                _stats.EvictedCount += toRemove.Count;
                _stats.CurrentlyCached = _cache.Count;
            }
        }

        /// <summary>清空所有缓存。</summary>
        public void ClearAll()
        {
            lock (_lock)
            {
                foreach (var kv in _cache)
                    kv.Value.Pipeline?.Dispose();
                _cache.Clear();
                _stats.CurrentlyCached = 0;
            }
        }

        private RHIPipeline CreatePipeline(RHIPipelineStateDesc desc)
        {
            var gDesc = new RHIGraphicsPipelineDesc
            {
                VertexShader = desc.VertexShader,
                PixelShader = desc.PixelShader,
                GeometryShader = desc.GeometryShader,
                HullShader = desc.HullShader,
                DomainShader = desc.DomainShader,
                MeshShader = desc.MeshShader,
                AmplificationShader = desc.AmplificationShader,
                InputElements = desc.InputElements,
                Topology = desc.Topology,
                FillMode = desc.FillMode,
                CullMode = desc.CullMode,
                FrontCounterClockwise = desc.FrontCounterClockwise,
                DepthBias = desc.DepthBias,
                DepthBiasClamp = desc.DepthBiasClamp,
                SlopeScaledDepthBias = desc.SlopeScaledDepthBias,
                DepthClipEnable = desc.DepthClipEnable,
                ConservativeRaster = desc.ConservativeRaster,
                AlphaToCoverageEnable = desc.AlphaToCoverageEnable,
                IndependentBlendEnable = desc.IndependentBlendEnable,
                RenderTargetBlends = desc.RenderTargetBlends,
                DepthEnable = desc.DepthEnable,
                DepthWriteEnable = desc.DepthWriteEnable,
                DepthFunc = desc.DepthFunc,
                StencilEnable = desc.StencilEnable,
                FrontFace = desc.FrontFace,
                BackFace = desc.BackFace,
                RTVFormats = desc.RTVFormats,
                DSVFormat = desc.DSVFormat,
                SampleCount = (int)desc.SampleCount,
                SampleMask = (int)desc.SampleMask,
                RootSignature = desc.RootSignature,
                Name = desc.Name ?? "GraphicsPipeline",
            };
            return _device.CreateGraphicsPipeline(gDesc);
        }

        public void Dispose()
        {
            ClearAll();
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 5: PSO 构建器 (流式 API)
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>PSO 构建器。</summary>
    public sealed class RHIPipelineStateBuilder
    {
        private readonly RHIPipelineStateDesc _desc = new();

        public RHIPipelineStateBuilder SetVertexShader(RHIShader shader)
        { _desc.VertexShader = shader; return this; }

        public RHIPipelineStateBuilder SetPixelShader(RHIShader shader)
        { _desc.PixelShader = shader; return this; }

        public RHIPipelineStateBuilder SetGeometryShader(RHIShader shader)
        { _desc.GeometryShader = shader; return this; }

        public RHIPipelineStateBuilder SetHullShader(RHIShader shader)
        { _desc.HullShader = shader; return this; }

        public RHIPipelineStateBuilder SetDomainShader(RHIShader shader)
        { _desc.DomainShader = shader; return this; }

        public RHIPipelineStateBuilder SetMeshShader(RHIShader shader)
        { _desc.MeshShader = shader; return this; }

        public RHIPipelineStateBuilder SetAmplificationShader(RHIShader shader)
        { _desc.AmplificationShader = shader; return this; }

        public RHIPipelineStateBuilder SetInputLayout(RHIInputElement[] elements)
        { _desc.InputElements = elements; return this; }

        public RHIPipelineStateBuilder SetTopology(RHIPrimitiveTopology topo)
        { _desc.Topology = topo; return this; }

        public RHIPipelineStateBuilder SetFillMode(RHIFillMode mode)
        { _desc.FillMode = mode; return this; }

        public RHIPipelineStateBuilder SetCullMode(RHICullMode mode)
        { _desc.CullMode = mode; return this; }

        public RHIPipelineStateBuilder SetDepthTest(bool enable, bool write, RHICompareFunction func = RHICompareFunction.Less)
        {
            _desc.DepthEnable = enable;
            _desc.DepthWriteEnable = write;
            _desc.DepthFunc = func;
            return this;
        }

        public RHIPipelineStateBuilder SetStencilTest(bool enable, RHIStencilOpDesc front, RHIStencilOpDesc back)
        {
            _desc.StencilEnable = enable;
            _desc.FrontFace = front;
            _desc.BackFace = back;
            return this;
        }

        public RHIPipelineStateBuilder SetRenderTargetFormats(RHIFormat[] formats, RHIFormat dsvFormat = RHIFormat.Unknown)
        {
            _desc.RTVFormats = formats;
            _desc.DSVFormat = dsvFormat;
            return this;
        }

        public RHIPipelineStateBuilder SetSampleCount(uint count)
        { _desc.SampleCount = count; return this; }

        public RHIPipelineStateBuilder AddBlendState(uint index, RHIRenderTargetBlend blend)
        {
            if (_desc.RenderTargetBlends == null)
                _desc.RenderTargetBlends = new RHIRenderTargetBlend[8];
            _desc.RenderTargetBlends[index] = blend;
            return this;
        }

        public RHIPipelineStateBuilder SetNoBlend()
        {
            _desc.RenderTargetBlends = null;
            return this;
        }

        public RHIPipelineStateBuilder SetDefaultBlend()
        {
            _desc.RenderTargetBlends = new RHIRenderTargetBlend[1];
            _desc.RenderTargetBlends[0] = new RHIRenderTargetBlend
            {
                BlendEnable = false,
                RenderTargetWriteMask = (byte)RHIColorWriteMask.All,
            };
            return this;
        }

        public RHIPipelineStateBuilder SetAlphaBlend()
        {
            _desc.RenderTargetBlends = new RHIRenderTargetBlend[1];
            _desc.RenderTargetBlends[0] = new RHIRenderTargetBlend
            {
                BlendEnable = true,
                SrcBlend = RHIBlend.SrcAlpha,
                DestBlend = RHIBlend.InvSrcAlpha,
                BlendOp = RHIBlendOp.Add,
                SrcBlendAlpha = RHIBlend.One,
                DestBlendAlpha = RHIBlend.InvSrcAlpha,
                BlendOpAlpha = RHIBlendOp.Add,
                RenderTargetWriteMask = (byte)RHIColorWriteMask.All,
            };
            return this;
        }

        public RHIPipelineStateBuilder SetAdditiveBlend()
        {
            _desc.RenderTargetBlends = new RHIRenderTargetBlend[1];
            _desc.RenderTargetBlends[0] = new RHIRenderTargetBlend
            {
                BlendEnable = true,
                SrcBlend = RHIBlend.One,
                DestBlend = RHIBlend.One,
                BlendOp = RHIBlendOp.Add,
                SrcBlendAlpha = RHIBlend.One,
                DestBlendAlpha = RHIBlend.One,
                BlendOpAlpha = RHIBlendOp.Add,
                RenderTargetWriteMask = (byte)RHIColorWriteMask.All,
            };
            return this;
        }

        public RHIPipelineStateBuilder SetName(string name)
        { _desc.Name = name; return this; }

        public RHIPipelineStateDesc Build() => _desc;
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 6: 版本信息
    // ════════════════════════════════════════════════════════════════════════════

    public static class RHIPipelineStateManagerVersion
    {
        public const string Version = "1.0.0";
        public const string Description = "YamaEngine RHI Pipeline State Manager (PSO cache + builder)";
    }
}

// 文件结束 - RHIPipelineStateManager.cs
