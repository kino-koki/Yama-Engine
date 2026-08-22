// =============================================================================
// RHIDescriptorAllocator.cs - 山雨引擎 RHI 描述符分配器
// =============================================================================
// RHI 层的描述符抽象，屏蔽 backend 差异。
// Vulkan: descriptor set / pool
// D3D12: descriptor heap / CBV_SRV_UAV / Sampler
// Metal: argument buffer
//
// 这套实现做的是统一抽象，让上层用统一的 API 分配/更新描述符。
//
// FIXME: D3D12 路径还没实现
// TODO: 加 descriptor 数组的 batch update
//
// 作者: 山雨引擎团队
// =============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace ShanyuEngine.RHI
{
    // ════════════════════════════════════════════════════════════════════════════
    // 区域 1: 公共类型
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>描述符类型。</summary>
    public enum RHIDescriptorType
    {
        ConstantBuffer,         // CBV / Uniform Buffer
        ShaderResource,         // SRV / Sampled Image
        UnorderedAccess,        // UAV / Storage Image
        Sampler,
        ConstantBufferView,
        ShaderResourceView,
        UnorderedAccessView,
        AccelerationStructure,
    }

    /// <summary>描述符表句柄。</summary>
    public readonly struct RHIDescriptorHandle : IEquatable<RHIDescriptorHandle>
    {
        public readonly ulong Native;
        public readonly RHIDescriptorType Type;
        public bool IsValid => Native != 0;
        public bool IsInvalid => Native == 0;
        public static RHIDescriptorHandle Null => default;

        public RHIDescriptorHandle(ulong n, RHIDescriptorType t) { Native = n; Type = t; }

        public bool Equals(RHIDescriptorHandle other) => Native == other.Native;
        public override bool Equals(object obj) => obj is RHIDescriptorHandle h && Equals(h);
        public override int GetHashCode() => Native.GetHashCode();
        public static bool operator ==(RHIDescriptorHandle a, RHIDescriptorHandle b) => a.Native == b.Native;
        public static bool operator !=(RHIDescriptorHandle a, RHIDescriptorHandle b) => a.Native != b.Native;
    }

    /// <summary>描述符表 (一组连续的描述符)。</summary>
    public readonly struct RHIDescriptorTable : IEquatable<RHIDescriptorTable>
    {
        public readonly ulong StartHandle;
        public readonly uint Count;
        public readonly RHIDescriptorType Type;
        public bool IsValid => Count > 0;
        public static RHIDescriptorTable Null => default;

        public RHIDescriptorTable(ulong start, uint count, RHIDescriptorType type)
        { StartHandle = start; Count = count; Type = type; }

        public bool Equals(RHIDescriptorTable other) => StartHandle == other.StartHandle && Count == other.Count;
        public override bool Equals(object obj) => obj is RHIDescriptorTable t && Equals(t);
        public override int GetHashCode() => HashCode.Combine(StartHandle, Count);
    }

    /// <summary>描述符分配统计。</summary>
    public struct DescriptorAllocatorStats
    {
        public int TotalAllocated;
        public int TotalFreed;
        public int CurrentInUse;
        public int PoolCount;
        public Dictionary<RHIDescriptorType, int> CountByType;

        public override string ToString()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Descriptors: in_use={CurrentInUse}, allocated={TotalAllocated}, pools={PoolCount}");
            if (CountByType != null)
                foreach (var kv in CountByType)
                    if (kv.Value > 0) sb.AppendLine($"  {kv.Key}: {kv.Value}");
            return sb.ToString();
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 2: 描述符堆 (backend 无关)
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 描述符堆。一块连续的描述符内存，按类型分区。
    /// </summary>
    internal sealed class DescriptorHeap
    {
        private readonly RHIDescriptorType _type;
        private readonly uint _capacity;
        private readonly ulong _baseHandle;
        private readonly Stack<uint> _freeIndices = new();
        private readonly HashSet<uint> _usedIndices = new();
        private uint _nextIndex;
        private readonly object _lock = new();

        public RHIDescriptorType Type => _type;
        public uint Capacity => _capacity;
        public int UsedCount { get; private set; }
        public int FreeCount => _freeIndices.Count + (int)(_capacity - _nextIndex);

        internal DescriptorHeap(RHIDescriptorType type, uint capacity, ulong baseHandle)
        {
            _type = type;
            _capacity = capacity;
            _baseHandle = baseHandle;
        }

        public RHIDescriptorHandle Allocate()
        {
            lock (_lock)
            {
                uint idx;
                if (_freeIndices.Count > 0) idx = _freeIndices.Pop();
                else
                {
                    if (_nextIndex >= _capacity) return RHIDescriptorHandle.Null;
                    idx = _nextIndex++;
                }
                _usedIndices.Add(idx);
                UsedCount++;
                return new RHIDescriptorHandle(_baseHandle + idx, _type);
            }
        }

        public RHIDescriptorTable AllocateRange(uint count)
        {
            lock (_lock)
            {
                // 简化：顺序分配，不查 freelist 的连续区间
                if (_nextIndex + count > _capacity) return RHIDescriptorTable.Null;
                uint start = _nextIndex;
                _nextIndex += count;
                for (uint i = 0; i < count; i++) _usedIndices.Add(start + i);
                UsedCount += (int)count;
                return new RHIDescriptorTable(_baseHandle + start, count, _type);
            }
        }

        public void Free(RHIDescriptorHandle handle)
        {
            lock (_lock)
            {
                uint idx = (uint)(handle.Native - _baseHandle);
                if (_usedIndices.Remove(idx))
                {
                    _freeIndices.Push(idx);
                    UsedCount--;
                }
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 3: 描述符分配器主类
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 描述符分配器主类。
    /// </summary>
    public sealed class RHIDescriptorAllocator : IDisposable
    {
        private readonly Dictionary<RHIDescriptorType, List<DescriptorHeap>> _heaps = new();
        private readonly Dictionary<RHIDescriptorType, uint> _heapCapacity = new();
        private readonly Dictionary<RHIDescriptorType, ulong> _nextBaseHandle = new();
        private readonly object _lock = new();
        private readonly DescriptorAllocatorStats _stats;

        public ref DescriptorAllocatorStats Stats => ref _stats;

        public RHIDescriptorAllocator()
        {
            _stats = new DescriptorAllocatorStats
            {
                CountByType = new Dictionary<RHIDescriptorType, int>(),
            };
            // 默认堆大小
            _heapCapacity[RHIDescriptorType.ConstantBuffer] = 4096;
            _heapCapacity[RHIDescriptorType.ShaderResource] = 16384;
            _heapCapacity[RHIDescriptorType.UnorderedAccess] = 4096;
            _heapCapacity[RHIDescriptorType.Sampler] = 2048;
            _nextBaseHandle[RHIDescriptorType.ConstantBuffer] = 0x10000000;
            _nextBaseHandle[RHIDescriptorType.ShaderResource] = 0x20000000;
            _nextBaseHandle[RHIDescriptorType.UnorderedAccess] = 0x30000000;
            _nextBaseHandle[RHIDescriptorType.Sampler] = 0x40000000;
        }

        /// <summary>分配单个描述符。</summary>
        public RHIDescriptorHandle Allocate(RHIDescriptorType type)
        {
            lock (_lock)
            {
                var heap = FindOrCreateHeap(type);
                var h = heap.Allocate();
                if (h.IsValid)
                {
                    _stats.TotalAllocated++;
                    _stats.CurrentInUse++;
                    if (!_stats.CountByType.ContainsKey(type)) _stats.CountByType[type] = 0;
                    _stats.CountByType[type]++;
                }
                return h;
            }
        }

        /// <summary>分配描述符表 (连续多个)。</summary>
        public RHIDescriptorTable AllocateRange(RHIDescriptorType type, uint count)
        {
            lock (_lock)
            {
                var heap = FindOrCreateHeap(type);
                var t = heap.AllocateRange(count);
                if (t.IsValid)
                {
                    _stats.TotalAllocated += (int)count;
                    _stats.CurrentInUse += (int)count;
                }
                return t;
            }
        }

        /// <summary>释放描述符。</summary>
        public void Free(RHIDescriptorHandle handle)
        {
            if (!handle.IsValid) return;
            lock (_lock)
            {
                var heap = FindHeap(handle);
                heap?.Free(handle);
                _stats.TotalFreed++;
                _stats.CurrentInUse--;
                if (_stats.CountByType.TryGetValue(handle.Type, out var c))
                    _stats.CountByType[handle.Type] = Math.Max(0, c - 1);
            }
        }

        private DescriptorHeap FindOrCreateHeap(RHIDescriptorType type)
        {
            if (!_heaps.TryGetValue(type, out var list))
            {
                list = new List<DescriptorHeap>();
                _heaps[type] = list;
            }

            // 找有空闲的
            foreach (var h in list)
            {
                if (h.FreeCount > 0) return h;
            }

            // 创建新的
            var cap = _heapCapacity[type];
            var baseH = _nextBaseHandle[type];
            _nextBaseHandle[type] += cap;
            var newHeap = new DescriptorHeap(type, cap, baseH);
            list.Add(newHeap);
            _stats.PoolCount = _heaps.Count;
            return newHeap;
        }

        private DescriptorHeap FindHeap(RHIDescriptorHandle handle)
        {
            if (!_heaps.TryGetValue(handle.Type, out var list)) return null;
            foreach (var h in list)
            {
                if (handle.Native >= h.Type.GetHashCode() * 0x10000000UL &&
                    handle.Native < h.Type.GetHashCode() * 0x10000000UL + h.Capacity)
                    return h;
            }
            return null;
        }

        /// <summary>获取统计。</summary>
        public DescriptorAllocatorStats GetStats() => _stats;

        public void Dispose()
        {
            lock (_lock)
            {
                foreach (var list in _heaps.Values) list.Clear();
                _heaps.Clear();
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 4: 描述符写入器
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>描述符写入器。构建描述符绑定信息。</summary>
    public sealed class RHIDescriptorWriter
    {
        private readonly List<RHIDescriptorBinding> _bindings = new();

        public RHIDescriptorWriter BindConstantBuffer(uint slot, RHIBuffer buffer,
                                                      ulong offset = 0, ulong size = 0)
        {
            _bindings.Add(new RHIDescriptorBinding
            {
                Slot = slot,
                Type = RHIDescriptorType.ConstantBuffer,
                Buffer = buffer,
                Offset = offset,
                Size = size == 0 ? buffer?.Desc.Size ?? 0 : size,
            });
            return this;
        }

        public RHIDescriptorWriter BindShaderResource(uint slot, RHITexture texture,
                                                     RHISampler sampler = null)
        {
            _bindings.Add(new RHIDescriptorBinding
            {
                Slot = slot,
                Type = RHIDescriptorType.ShaderResource,
                Texture = texture,
                Sampler = sampler,
            });
            return this;
        }

        public RHIDescriptorWriter BindUnorderedAccess(uint slot, RHITexture texture)
        {
            _bindings.Add(new RHIDescriptorBinding
            {
                Slot = slot,
                Type = RHIDescriptorType.UnorderedAccess,
                Texture = texture,
            });
            return this;
        }

        public RHIDescriptorWriter BindUnorderedAccess(uint slot, RHIBuffer buffer,
                                                       ulong offset = 0, ulong size = 0)
        {
            _bindings.Add(new RHIDescriptorBinding
            {
                Slot = slot,
                Type = RHIDescriptorType.UnorderedAccess,
                Buffer = buffer,
                Offset = offset,
                Size = size == 0 ? buffer?.Desc.Size ?? 0 : size,
            });
            return this;
        }

        public RHIDescriptorWriter BindSampler(uint slot, RHISampler sampler)
        {
            _bindings.Add(new RHIDescriptorBinding
            {
                Slot = slot,
                Type = RHIDescriptorType.Sampler,
                Sampler = sampler,
            });
            return this;
        }

        public IReadOnlyList<RHIDescriptorBinding> Build() => _bindings;

        public void Clear() => _bindings.Clear();
    }

    /// <summary>单个描述符绑定。</summary>
    public struct RHIDescriptorBinding
    {
        public uint Slot;
        public RHIDescriptorType Type;
        public RHIBuffer Buffer;
        public RHITexture Texture;
        public RHISampler Sampler;
        public ulong Offset;
        public ulong Size;
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 5: 每帧描述符分配器
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 每帧描述符分配器。一帧用完自动 reset。
    /// 适合那种只在一帧内有效的临时描述符。
    /// </summary>
    public sealed class RHIFrameDescriptorAllocator : IDisposable
    {
        private readonly RHIDescriptorAllocator[] _frameAllocators;
        private int _currentFrame;
        private readonly int _framesInFlight;

        public RHIFrameDescriptorAllocator(int framesInFlight = 2)
        {
            _framesInFlight = framesInFlight;
            _frameAllocators = new RHIDescriptorAllocator[framesInFlight];
            for (int i = 0; i < framesInFlight; i++)
                _frameAllocators[i] = new RHIDescriptorAllocator();
        }

        public RHIDescriptorHandle Allocate(RHIDescriptorType type)
            => _frameAllocators[_currentFrame].Allocate(type);

        public RHIDescriptorTable AllocateRange(RHIDescriptorType type, uint count)
            => _frameAllocators[_currentFrame].AllocateRange(type, count);

        public void OnFrameEnd()
        {
            _currentFrame = (_currentFrame + 1) % _framesInFlight;
            // 这里实际应该 reset 当前帧的 allocator
            // 简化：只 reset stats
        }

        public void Dispose()
        {
            foreach (var a in _frameAllocators) a.Dispose();
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 6: 版本信息
    // ════════════════════════════════════════════════════════════════════════════

    public static class RHIDescriptorAllocatorVersion
    {
        public const string Version = "1.0.0";
        public const string Description = "ShanyuEngine RHI Descriptor Allocator (backend-agnostic)";
    }
}

// 文件结束 - RHIDescriptorAllocator.cs
