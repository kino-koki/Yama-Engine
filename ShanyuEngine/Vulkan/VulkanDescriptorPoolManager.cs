// =============================================================================
// VulkanDescriptorPoolManager.cs - 山雨引擎 Vulkan 描述符池管理器
// =============================================================================
// Vulkan 的描述符池管理是个蛋疼的事，因为你得预估每种类型要多少个，
// 然后池子满了还不能自动扩容 (VK_ERROR_OUT_OF_POOL_MEMORY)。
//
// 这套实现参考了 Granite 和 Sascha Willems 的写法，做了几个改进：
//   1. 支持多池子自动扩容 (一个池满了再建一个)
//   2. bindless 模式：一个大池子里塞一个 descriptor set，里面 N 个 binding
//   3. 每帧回收：渲染帧结束把临时 set 还回去复用
//   4. 线程安全：每个线程一个 pool (TLS)
//
// FIXME: bindless 还没考虑部分更新，目前每次都重写整个 set
// TODO: 加个 descriptor set 的缓存，相同 layout 不重复分配
//
// 作者: 山雨引擎团队
// =============================================================================

using Vulkan = Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Silk.NET.Vulkan;

using Buffer = Silk.NET.Vulkan.Buffer;
using Semaphore = Silk.NET.Vulkan.Semaphore;
namespace ShanyuEngine.Vulkan
{
    // ════════════════════════════════════════════════════════════════════════════
    // 区域 1: 公共类型
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>描述符池创建标志。</summary>
    [Flags]
    public enum DescriptorPoolCreateFlagsExt : uint
    {
        None = 0,
        /// <summary>允许 free 单个 set (默认只能 reset 整个池)。</summary>
        FreeDescriptorSet = 0x1,
        /// <summary>更新后不重置 set (VK_DESCRIPTOR_POOL_CREATE_UPDATE_AFTER_BIND_BIT)。</summary>
        UpdateAfterBind = 0x2,
        /// <summary>支持 HOST_ONLY (VK_EXT_descriptor_pool)。</summary>
        HostOnly = 0x4,
    }

    /// <summary>描述符类型分类，用于配额管理。</summary>
    public enum DescriptorCategory
    {
        Sampler,
        SampledImage,
        StorageImage,
        UniformTexelBuffer,
        StorageTexelBuffer,
        UniformBuffer,
        StorageBuffer,
        UniformBufferDynamic,
        StorageBufferDynamic,
        InputAttachment,
        InlineUniformBlock,
        AccelerationStructure,
    }

    /// <summary>描述符池配额。</summary>
    public struct DescriptorPoolQuota
    {
        /// <summary>每种类型的最大数量。</summary>
        public Dictionary<DescriptorType, uint> MaxPerType;
        /// <summary>set 总数。</summary>
        public uint MaxSets;
        /// <summary>每个池子里 set 数量。</summary>
        public uint SetsPerPool;

        public static DescriptorPoolQuota Default => new()
        {
            MaxPerType = new Dictionary<DescriptorType, uint>
            {
                [DescriptorType.Sampler] = 1000,
                [DescriptorType.CombinedImageSampler] = 10000,
                [DescriptorType.SampledImage] = 10000,
                [DescriptorType.StorageImage] = 1000,
                [DescriptorType.UniformTexelBuffer] = 1000,
                [DescriptorType.StorageTexelBuffer] = 1000,
                [DescriptorType.UniformBuffer] = 10000,
                [DescriptorType.StorageBuffer] = 10000,
                [DescriptorType.UniformBufferDynamic] = 1000,
                [DescriptorType.StorageBufferDynamic] = 1000,
                [DescriptorType.InputAttachment] = 1000,
            },
            MaxSets = 8192,
            SetsPerPool = 1024,
        };

        public static DescriptorPoolQuota Bindless => new()
        {
            MaxPerType = new Dictionary<DescriptorType, uint>
            {
                [DescriptorType.CombinedImageSampler] = 500000,  // bindless 贴图要够多
                [DescriptorType.StorageImage] = 5000,
                [DescriptorType.UniformBuffer] = 5000,
                [DescriptorType.StorageBuffer] = 50000,          // bindless buffer
            },
            MaxSets = 512,
            SetsPerPool = 64,
        };

        public static DescriptorPoolQuota Conservative => new()
        {
            MaxPerType = new Dictionary<DescriptorType, uint>
            {
                [DescriptorType.Sampler] = 256,
                [DescriptorType.CombinedImageSampler] = 2048,
                [DescriptorType.SampledImage] = 2048,
                [DescriptorType.StorageImage] = 256,
                [DescriptorType.UniformTexelBuffer] = 256,
                [DescriptorType.StorageTexelBuffer] = 256,
                [DescriptorType.UniformBuffer] = 2048,
                [DescriptorType.StorageBuffer] = 2048,
                [DescriptorType.UniformBufferDynamic] = 256,
                [DescriptorType.StorageBufferDynamic] = 256,
                [DescriptorType.InputAttachment] = 256,
            },
            MaxSets = 2048,
            SetsPerPool = 512,
        };
    }

    /// <summary>描述符池统计。</summary>
    public struct DescriptorPoolStats
    {
        public int PoolCount;
        public int TotalSetsAllocated;
        public int TotalSetsFreed;
        public int CurrentSetsInUse;
        public Dictionary<DescriptorType, uint> UsedPerType;
        public Dictionary<DescriptorType, uint> CapacityPerType;

        public override string ToString()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Pools: {PoolCount}, Sets in use: {CurrentSetsInUse}");
            if (UsedPerType != null)
            {
                foreach (var kv in UsedPerType)
                {
                    var cap = CapacityPerType != null && CapacityPerType.TryGetValue(kv.Key, out var c) ? c : 0;
                    sb.AppendLine($"  {kv.Key}: {kv.Value}/{cap}");
                }
            }
            return sb.ToString();
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 2: 单个描述符池封装
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 一个 VkDescriptorPool 的包装。负责分配/释放 VkDescriptorSet。
    /// 满了返回 false 让上层建新池。
    /// </summary>
    internal sealed unsafe class DescriptorPool : IDisposable
    {
        private readonly Vk _vk;
        private readonly Device _device;
        private readonly DescriptorPool _handle;
        private readonly DescriptorPoolCreateFlagsExt _flags;
        private readonly Dictionary<DescriptorType, uint> _capacityPerType;
        private readonly Dictionary<DescriptorType, uint> _usedPerType;
        private readonly uint _maxSets;
        private uint _setsInUse;
        private readonly object _lock = new();

        public DescriptorPoolHandle Handle => new() { Native = _handle.Handle };
        public uint MaxSets => _maxSets;
        public uint SetsInUse => _setsInUse;
        public bool IsFull => _setsInUse >= _maxSets;
        public bool SupportsFree => (_flags & DescriptorPoolCreateFlagsExt.FreeDescriptorSet) != 0;

        internal DescriptorPool(Vk vk, Device device, DescriptorPool handle,
                                DescriptorPoolCreateFlagsExt flags,
                                Dictionary<DescriptorType, uint> capacity,
                                uint maxSets)
        {
            _vk = vk;
            _device = device;
            _handle = handle;
            _flags = flags;
            _capacityPerType = new Dictionary<DescriptorType, uint>(capacity);
            _usedPerType = new Dictionary<DescriptorType, uint>();
            foreach (var kv in capacity) _usedPerType[kv.Key] = 0;
            _maxSets = maxSets;
        }

        /// <summary>尝试分配一个 set。</summary>
        public bool TryAllocate(DescriptorSetLayout layout, out DescriptorSet set)
        {
            set = default;
            lock (_lock)
            {
                if (_setsInUse >= _maxSets) return false;

                var allocInfo = new DescriptorSetAllocateInfo
                {
                    SType = StructureType.DescriptorSetAllocateInfo,
                    DescriptorPool = _handle,
                    DescriptorSetCount = 1,
                    PSetLayouts = &layout,
                };

                DescriptorSet outSet;
                var r = _vk.AllocateDescriptorSets(_device, allocInfo, &outSet);
                if (r == Result.ErrorOutOfPoolMemory || r == Result.ErrorOutOfHostMemory)
                    return false;
                if (r != Result.Success)
                {
                    Console.Error.WriteLine($"[DescPool] AllocateDescriptorSets 失败: {r}");
                    return false;
                }

                set = outSet;
                _setsInUse++;
                return true;
            }
        }

        /// <summary>尝试分配多个 set。</summary>
        public bool TryAllocateMany(DescriptorSetLayout layout, uint count, DescriptorSet[] outSets)
        {
            lock (_lock)
            {
                if (_setsInUse + count > _maxSets) return false;

                var allocInfo = new DescriptorSetAllocateInfo
                {
                    SType = StructureType.DescriptorSetAllocateInfo,
                    DescriptorPool = _handle,
                    DescriptorSetCount = count,
                    PSetLayouts = &layout,
                };

                fixed (DescriptorSet* pSets = outSets)
                {
                    var r = _vk.AllocateDescriptorSets(_device, allocInfo, pSets);
                    if (r != Result.Success)
                    {
                        if (r != Result.ErrorOutOfPoolMemory)
                            Console.Error.WriteLine($"[DescPool] AllocateMany 失败: {r}");
                        return false;
                    }
                }

                _setsInUse += count;
                return true;
            }
        }

        /// <summary>释放单个 set (只有 FreeDescriptorSet 模式才能用)。</summary>
        public bool FreeSet(DescriptorSet set)
        {
            if (!SupportsFree) return false;
            lock (_lock)
            {
                var sets = stackalloc DescriptorSet[1];
                sets[0] = set;
                var r = _vk.FreeDescriptorSets(_device, _handle, 1, sets);
                if (r != Result.Success)
                {
                    Console.Error.WriteLine($"[DescPool] FreeDescriptorSets 失败: {r}");
                    return false;
                }
                _setsInUse--;
                return true;
            }
        }

        /// <summary>重置整个池 (释放所有 set)。</summary>
        public void Reset()
        {
            lock (_lock)
            {
                var r = _vk.ResetDescriptorPool(_device, _handle, 0);
                if (r != Result.Success)
                    throw new VulkanException(r, "ResetDescriptorPool 失败");
                _setsInUse = 0;
                foreach (var key in _usedPerType.Keys.ToList()) _usedPerType[key] = 0;
            }
        }

        public void Dispose()
        {
            _vk.DestroyDescriptorPool(_device, _handle, null);
        }
    }

    /// <summary>描述符池句柄。</summary>
    public readonly struct DescriptorPoolHandle : IEquatable<DescriptorPoolHandle>
    {
        public readonly ulong Native;
        public bool IsValid => Native != 0;
        public bool Equals(DescriptorPoolHandle other) => Native == other.Native;
        public override bool Equals(object obj) => obj is DescriptorPoolHandle h && Equals(h);
        public override int GetHashCode() => Native.GetHashCode();
        public static bool operator ==(DescriptorPoolHandle a, DescriptorPoolHandle b) => a.Native == b.Native;
        public static bool operator !=(DescriptorPoolHandle a, DescriptorPoolHandle b) => a.Native != b.Native;
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 3: 描述符 set 回收站 - 用于帧内复用
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 描述符 set 回收站。一帧用完的 set 不立刻 free，先收着，
    /// 下一帧同一个 layout 要 set 的时候直接发出去。
    /// 这个套路是从 Granite 学的，能大幅减少 AllocateDescriptorSets 调用。
    /// </summary>
    internal sealed class DescriptorSetRecycler
    {
        private readonly Dictionary<ulong, Stack<DescriptorSet>> _freeSets = new();
        private readonly Dictionary<ulong, int> _layoutRefCounts = new();
        private readonly object _lock = new();
        private const int MaxFreePerLayout = 256;

        /// <summary>回收一个 set。</summary>
        public void Recycle(ulong layoutHash, DescriptorSet set)
        {
            lock (_lock)
            {
                if (!_freeSets.TryGetValue(layoutHash, out var stack))
                {
                    stack = new Stack<DescriptorSet>();
                    _freeSets[layoutHash] = stack;
                }
                if (stack.Count < MaxFreePerLayout)
                    stack.Push(set);
            }
        }

        /// <summary>尝试拿一个回收的 set。</summary>
        public bool TryGet(ulong layoutHash, out DescriptorSet set)
        {
            set = default;
            lock (_lock)
            {
                if (!_freeSets.TryGetValue(layoutHash, out var stack)) return false;
                if (stack.Count == 0) return false;
                set = stack.Pop();
                return true;
            }
        }

        /// <summary>清空所有回收的 set (池子被 reset 了)。</summary>
        public void Clear()
        {
            lock (_lock) _freeSets.Clear();
        }

        /// <summary>当前回收站大小。</summary>
        public int Count
        {
            get
            {
                lock (_lock)
                {
                    int n = 0;
                    foreach (var s in _freeSets.Values) n += s.Count;
                    return n;
                }
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 4: 描述符池池组 - 多个池子组合，自动扩容
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 描述符池组。负责管理多个 VkDescriptorPool，自动扩容。
    /// 上层只管要 set，不用关心具体哪个池。
    /// </summary>
    internal sealed unsafe class DescriptorPoolGroup : IDisposable
    {
        private readonly Vk _vk;
        private readonly Device _device;
        private readonly DescriptorPoolCreateFlagsExt _flags;
        private readonly DescriptorPoolQuota _quota;
        private readonly List<DescriptorPool> _pools = new();
        private readonly DescriptorSetRecycler _recycler = new();
        private readonly object _lock = new();
        private int _currentPoolIdx = 0;
        private int _totalSetsAllocated;
        private int _totalSetsFreed;

        public int PoolCount => _pools.Count;
        public int CurrentSetsInUse => _totalSetsAllocated - _totalSetsFreed;

        public DescriptorPoolGroup(Vk vk, Device device, DescriptorPoolCreateFlagsExt flags,
                                   in DescriptorPoolQuota quota)
        {
            _vk = vk;
            _device = device;
            _flags = flags;
            _quota = quota;
            // 初始建一个池
            CreateNewPool();
        }

        /// <summary>分配一个 set。</summary>
        public bool TryAllocate(DescriptorSetLayout layout, ulong layoutHash, out DescriptorSet set)
        {
            set = default;
            lock (_lock)
            {
                // 先看回收站
                if (_recycler.TryGet(layoutHash, out set))
                    return true;

                // 当前池试
                if (_pools[_currentPoolIdx].TryAllocate(layout, out set))
                {
                    _totalSetsAllocated++;
                    return true;
                }

                // 当前池满了，建新池
                if (!CreateNewPool()) return false;

                if (_pools[_currentPoolIdx].TryAllocate(layout, out set))
                {
                    _totalSetsAllocated++;
                    return true;
                }
                return false;
            }
        }

        /// <summary>批量分配同 layout 的 set。</summary>
        public bool TryAllocateMany(DescriptorSetLayout layout, ulong layoutHash,
                                    int count, DescriptorSet[] outSets)
        {
            lock (_lock)
            {
                int filled = 0;
                // 先从回收站拿
                while (filled < count && _recycler.TryGet(layoutHash, out outSets[filled]))
                    filled++;

                // 剩下的从池子拿
                while (filled < count)
                {
                    int need = count - filled;
                    if (_pools[_currentPoolIdx].TryAllocateMany(layout, (uint)need, outSets.AsSpan(filled, need).ToArray()))
                    {
                        _totalSetsAllocated += need;
                        filled = count;
                    }
                    else
                    {
                        // 当前池装不下这多，那就一个一个来，或者建新池
                        if (!CreateNewPool()) return false;
                        // 再试一次单个
                        if (_pools[_currentPoolIdx].TryAllocate(layout, out outSets[filled]))
                        {
                            _totalSetsAllocated++;
                            filled++;
                        }
                        else return false;
                    }
                }
                return filled == count;
            }
        }

        /// <summary>回收一个 set (给 FreeDescriptorSet 模式)。</summary>
        public void Recycle(ulong layoutHash, DescriptorSet set)
        {
            lock (_lock)
            {
                _recycler.Recycle(layoutHash, set);
                _totalSetsFreed++;
            }
        }

        /// <summary>重置所有池。</summary>
        public void ResetAll()
        {
            lock (_lock)
            {
                foreach (var p in _pools) p.Reset();
                _recycler.Clear();
                _totalSetsAllocated = 0;
                _totalSetsFreed = 0;
                _currentPoolIdx = 0;
            }
        }

        /// <summary>重置当前池。</summary>
        public void ResetCurrent()
        {
            lock (_lock)
            {
                _pools[_currentPoolIdx].Reset();
            }
        }

        private bool CreateNewPool()
        {
            // 构建 pool size 数组
            int typeCount = _quota.MaxPerType.Count;
            var poolSizes = new DescriptorPoolSize[typeCount];
            int i = 0;
            foreach (var kv in _quota.MaxPerType)
            {
                poolSizes[i++] = new DescriptorPoolSize
                {
                    Type = kv.Key,
                    DescriptorCount = kv.Value,
                };
            }

            DescriptorPoolCreateFlags vkFlags = 0;
            if ((_flags & DescriptorPoolCreateFlagsExt.FreeDescriptorSet) != 0)
                vkFlags |= DescriptorPoolCreateFlags.FreeDescriptorSetBit;
            if ((_flags & DescriptorPoolCreateFlagsExt.UpdateAfterBind) != 0)
                vkFlags |= DescriptorPoolCreateFlags.UpdateAfterBindBit;

            var poolInfo = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                Flags = vkFlags,
                MaxSets = _quota.SetsPerPool,
                PoolSizeCount = (uint)typeCount,
            };

            fixed (DescriptorPoolSize* pSizes = poolSizes)
            {
                poolInfo.PPoolSizes = pSizes;

                DescriptorPool handle;
                var r = _vk.CreateDescriptorPool(_device, poolInfo, null, &handle);
                if (r != Result.Success)
                {
                    Console.Error.WriteLine($"[DescPool] CreateDescriptorPool 失败: {r}");
                    return false;
                }

                var pool = new DescriptorPool(_vk, _device, handle, _flags,
                                              _quota.MaxPerType, _quota.SetsPerPool);
                _pools.Add(pool);
                _currentPoolIdx = _pools.Count - 1;
                return true;
            }
        }

        public DescriptorPoolStats GetStats()
        {
            lock (_lock)
            {
                return new DescriptorPoolStats
                {
                    PoolCount = _pools.Count,
                    TotalSetsAllocated = _totalSetsAllocated,
                    TotalSetsFreed = _totalSetsFreed,
                    CurrentSetsInUse = CurrentSetsInUse,
                    UsedPerType = new Dictionary<DescriptorType, uint>(_quota.MaxPerType),
                    CapacityPerType = new Dictionary<DescriptorType, uint>(_quota.MaxPerType),
                };
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                foreach (var p in _pools) p.Dispose();
                _pools.Clear();
                _recycler.Clear();
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 5: 描述符写入辅助
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>描述符写入构建器。</summary>
    public sealed unsafe class DescriptorWriter
    {
        private readonly Vk _vk;
        private readonly Device _device;
        private readonly List<WriteDescriptorSet> _writes = new();
        private readonly List<DescriptorBufferInfo> _bufferInfos = new();
        private readonly List<DescriptorImageInfo> _imageInfos = new();
        private readonly List<Buffer> _bufferHandles = new();
        private readonly List<Sampler> _samplerHandles = new();
        private readonly List<ImageView> _viewHandles = new();

        public DescriptorWriter(Vk vk, Device device)
        {
            _vk = vk;
            _device = device;
        }

        /// <summary>写 uniform buffer。</summary>
        public DescriptorWriter WriteUniformBuffer(uint binding, Buffer buffer, ulong offset, ulong range)
        {
            _bufferHandles.Add(buffer);
            _bufferInfos.Add(new DescriptorBufferInfo
            {
                Buffer = buffer,
                Offset = offset,
                Range = range,
            });
            _writes.Add(new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DescriptorType = DescriptorType.UniformBuffer,
                DescriptorCount = 1,
                DstBinding = binding,
                PBufferInfo = (DescriptorBufferInfo*)0, // 之后再修正
            });
            return this;
        }

        /// <summary>写 storage buffer。</summary>
        public DescriptorWriter WriteStorageBuffer(uint binding, Buffer buffer, ulong offset, ulong range)
        {
            _bufferHandles.Add(buffer);
            _bufferInfos.Add(new DescriptorBufferInfo
            {
                Buffer = buffer,
                Offset = offset,
                Range = range,
            });
            _writes.Add(new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1,
                DstBinding = binding,
            });
            return this;
        }

        /// <summary>写 combined image sampler。</summary>
        public DescriptorWriter WriteCombinedImageSampler(uint binding, Sampler sampler,
                                                         ImageView view, ImageLayout layout)
        {
            _samplerHandles.Add(sampler);
            _viewHandles.Add(view);
            _imageInfos.Add(new DescriptorImageInfo
            {
                Sampler = sampler,
                ImageView = view,
                ImageLayout = layout,
            });
            _writes.Add(new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                DstBinding = binding,
            });
            return this;
        }

        /// <summary>写 storage image。</summary>
        public DescriptorWriter WriteStorageImage(uint binding, ImageView view, ImageLayout layout)
        {
            _viewHandles.Add(view);
            _imageInfos.Add(new DescriptorImageInfo
            {
                ImageView = view,
                ImageLayout = layout,
            });
            _writes.Add(new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DescriptorType = DescriptorType.StorageImage,
                DescriptorCount = 1,
                DstBinding = binding,
            });
            return this;
        }

        /// <summary>写 sampled image。</summary>
        public DescriptorWriter WriteSampledImage(uint binding, ImageView view, ImageLayout layout)
        {
            _viewHandles.Add(view);
            _imageInfos.Add(new DescriptorImageInfo
            {
                ImageView = view,
                ImageLayout = layout,
            });
            _writes.Add(new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DescriptorType = DescriptorType.SampledImage,
                DescriptorCount = 1,
                DstBinding = binding,
            });
            return this;
        }

        /// <summary>写 sampler。</summary>
        public DescriptorWriter WriteSampler(uint binding, Sampler sampler)
        {
            _samplerHandles.Add(sampler);
            _imageInfos.Add(new DescriptorImageInfo
            {
                Sampler = sampler,
            });
            _writes.Add(new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DescriptorType = DescriptorType.Sampler,
                DescriptorCount = 1,
                DstBinding = binding,
            });
            return this;
        }

        /// <summary>写 input attachment。</summary>
        public DescriptorWriter WriteInputAttachment(uint binding, ImageView view, ImageLayout layout)
        {
            _viewHandles.Add(view);
            _imageInfos.Add(new DescriptorImageInfo
            {
                ImageView = view,
                ImageLayout = layout,
            });
            _writes.Add(new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DescriptorType = DescriptorType.InputAttachment,
                DescriptorCount = 1,
                DstBinding = binding,
            });
            return this;
        }

        /// <summary>执行写入。</summary>
        public void UpdateSet(DescriptorSet set)
        {
            if (_writes.Count == 0) return;

            // 修正指针 - 因为 List 可能 rearrange，所以最后一次性 pin
            var writes = _writes.ToArray();
            var bufInfos = _bufferInfos.ToArray();
            var imgInfos = _imageInfos.ToArray();

            int bufIdx = 0, imgIdx = 0;
            for (int i = 0; i < writes.Length; i++)
            {
                writes[i].DstSet = set;
                var t = writes[i].DescriptorType;
                if (t == DescriptorType.UniformBuffer || t == DescriptorType.StorageBuffer ||
                    t == DescriptorType.UniformBufferDynamic || t == DescriptorType.StorageBufferDynamic ||
                    t == DescriptorType.UniformTexelBuffer || t == DescriptorType.StorageTexelBuffer)
                {
                    fixed (DescriptorBufferInfo* p = &bufInfos[bufIdx])
                        writes[i].PBufferInfo = p;
                    bufIdx++;
                }
                else
                {
                    fixed (DescriptorImageInfo* p = &imgInfos[imgIdx])
                        writes[i].PImageInfo = p;
                    imgIdx++;
                }
            }

            fixed (WriteDescriptorSet* pWrites = writes)
            {
                _vk.UpdateDescriptorSets(_device, (uint)writes.Length, pWrites, 0, null);
            }

            // 清掉，准备复用
            _writes.Clear();
            _bufferInfos.Clear();
            _imageInfos.Clear();
            _bufferHandles.Clear();
            _samplerHandles.Clear();
            _viewHandles.Clear();
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 6: bindless 描述符管理
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// bindless 描述符管理。一个大 set 里塞所有贴图/buffer，
    /// shader 通过 NonUniform resource index 访问。
    /// 这是现代渲染的标准做法，比每次 draw 重新绑 descriptor 快几个数量级。
    /// </summary>
    public sealed unsafe class BindlessDescriptorManager : IDisposable
    {
        private readonly Vk _vk;
        private readonly Device _device;
        private DescriptorSetLayout _setLayout;
        private DescriptorSet _set;
        private DescriptorPool _pool;
        private readonly DescriptorType _descType;
        private readonly uint _maxCount;
        private readonly Dictionary<ulong, uint> _resourceToIndex = new();
        private readonly List<ulong> _indexToResource = new();
        private readonly Stack<uint> _freeIndices = new();
        private uint _nextIndex;
        private readonly object _lock = new();

        public DescriptorSet Set => _set;
        public DescriptorSetLayout Layout => _setLayout;
        public uint MaxCount => _maxCount;
        public int AllocatedCount => (int)_nextIndex - _freeIndices.Count;
        public int FreeCount => _freeIndices.Count;

        public BindlessDescriptorManager(Vk vk, Device device, DescriptorType descType,
                                         uint maxCount, uint binding,
                                         bool updateAfterBind = true)
        {
            _vk = vk;
            _device = device;
            _descType = descType;
            _maxCount = maxCount;

            CreateLayoutAndPool(binding, updateAfterBind);
        }

        private void CreateLayoutAndPool(uint binding, bool updateAfterBind)
        {
            // layout binding
            var bindingFlags = DescriptorBindingFlags.UpdateAfterBindBit;
            if (updateAfterBind)
                bindingFlags |= DescriptorBindingFlags.UpdateAfterBindBit;

            var bindingInfo = new DescriptorSetLayoutBinding
            {
                Binding = binding,
                DescriptorType = _descType,
                DescriptorCount = _maxCount,
                StageFlags = ShaderStageFlags.All,
            };

            var bindingFlagsInfo = new DescriptorSetLayoutBindingFlagsCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutBindingFlagsCreateInfo,
                BindingCount = 1,
                PBindingFlags = &bindingFlags,
            };

            var layoutInfo = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = 1,
                PBindings = &bindingInfo,
                Flags = updateAfterBind ? DescriptorSetLayoutCreateFlags.UpdateAfterBindPoolBit : 0,
                PNext = &bindingFlagsInfo,
            };

            fixed (DescriptorSetLayout* pLayout = &_setLayout)
            {
                var r = _vk.CreateDescriptorSetLayout(_device, layoutInfo, null, pLayout);
                if (r != Result.Success)
                    throw new VulkanException(r, "CreateDescriptorSetLayout bindless 失败");
            }

            // pool
            var poolSize = new DescriptorPoolSize
            {
                Type = _descType,
                DescriptorCount = _maxCount,
            };
            var poolInfo = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                Flags = DescriptorPoolCreateFlags.UpdateAfterBindBit,
                MaxSets = 1,
                PoolSizeCount = 1,
                PPoolSizes = &poolSize,
            };

            fixed (DescriptorPool* pPool = &_pool)
            {
                var r = _vk.CreateDescriptorPool(_device, poolInfo, null, pPool);
                if (r != Result.Success)
                    throw new VulkanException(r, "CreateDescriptorPool bindless 失败");
            }

            // 分配 set
            var allocInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _pool,
                DescriptorSetCount = 1,
                PSetLayouts = _setLayout,
            };

            fixed (DescriptorSet* pSet = &_set)
            {
                var r = _vk.AllocateDescriptorSets(_device, allocInfo, pSet);
                if (r != Result.Success)
                    throw new VulkanException(r, "AllocateDescriptorSets bindless 失败");
            }
        }

        /// <summary>绑定一个 image，返回它的 index。</summary>
        public uint BindImage(ulong imageId, ImageView view, ImageLayout layout, Sampler sampler = default,
                              uint binding = 0)
        {
            lock (_lock)
            {
                if (_resourceToIndex.TryGetValue(imageId, out var existing))
                    return existing;

                uint idx = AllocateIndex();
                var imgInfo = new DescriptorImageInfo
                {
                    Sampler = sampler,
                    ImageView = view,
                    ImageLayout = layout,
                };
                var write = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = _set,
                    DstBinding = binding,
                    DstArrayElement = idx,
                    DescriptorType = _descType,
                    DescriptorCount = 1,
                    PImageInfo = &imgInfo,
                };
                _vk.UpdateDescriptorSets(_device, 1, &write, 0, null);

                _resourceToIndex[imageId] = idx;
                while (_indexToResource.Count <= idx) _indexToResource.Add(0);
                _indexToResource[(int)idx] = imageId;
                return idx;
            }
        }

        /// <summary>绑定一个 buffer，返回 index。</summary>
        public uint BindBuffer(ulong bufferId, Buffer buffer, ulong offset, ulong range, uint binding = 0)
        {
            lock (_lock)
            {
                if (_resourceToIndex.TryGetValue(bufferId, out var existing))
                    return existing;

                uint idx = AllocateIndex();
                var bufInfo = new DescriptorBufferInfo
                {
                    Buffer = buffer,
                    Offset = offset,
                    Range = range,
                };
                var write = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = _set,
                    DstBinding = binding,
                    DstArrayElement = idx,
                    DescriptorType = _descType,
                    DescriptorCount = 1,
                    PBufferInfo = &bufInfo,
                };
                _vk.UpdateDescriptorSets(_device, 1, &write, 0, null);

                _resourceToIndex[bufferId] = idx;
                while (_indexToResource.Count <= idx) _indexToResource.Add(0);
                _indexToResource[(int)idx] = bufferId;
                return idx;
            }
        }

        /// <summary>解绑。</summary>
        public void Unbind(ulong resourceId)
        {
            lock (_lock)
            {
                if (!_resourceToIndex.TryGetValue(resourceId, out var idx)) return;
                _resourceToIndex.Remove(resourceId);
                _indexToResource[(int)idx] = 0;
                _freeIndices.Push(idx);
            }
        }

        /// <summary>查询某个资源是否已绑定。</summary>
        public bool IsBound(ulong resourceId)
        {
            lock (_lock) return _resourceToIndex.ContainsKey(resourceId);
        }

        /// <summary>查询资源的 index。</summary>
        public uint? GetIndex(ulong resourceId)
        {
            lock (_lock)
            {
                return _resourceToIndex.TryGetValue(resourceId, out var idx) ? idx : (uint?)null;
            }
        }

        private uint AllocateIndex()
        {
            if (_freeIndices.Count > 0) return _freeIndices.Pop();
            if (_nextIndex >= _maxCount)
                throw new InvalidOperationException("bindless 池满了，加 _maxCount");
            return _nextIndex++;
        }

        public void Dispose()
        {
            if (_set.Handle != 0)
                _vk.FreeDescriptorSets(_device, _pool, 1, _set);
            if (_pool.Handle != 0)
                _vk.DestroyDescriptorPool(_device, _pool, null);
            if (_setLayout.Handle != 0)
                _vk.DestroyDescriptorSetLayout(_device, _setLayout, null);
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 7: 描述符 set layout 缓存
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 描述符 set layout 缓存。相同的 binding 描述只创建一次 layout，
    /// 这是 Vulkan 性能优化的关键点之一。
    /// </summary>
    public sealed unsafe class DescriptorSetLayoutCache : IDisposable
    {
        private readonly Vk _vk;
        private readonly Device _device;
        private readonly Dictionary<ulong, DescriptorSetLayout> _cache = new();
        private readonly Dictionary<ulong, string> _debugNames = new();
        private readonly object _lock = new();

        public DescriptorSetLayoutCache(Vk vk, Device device)
        {
            _vk = vk;
            _device = device;
        }

        /// <summary>获取或创建 layout。</summary>
        public DescriptorSetLayout GetOrCreate(DescriptorSetLayoutBinding[] bindings,
                                               DescriptorSetLayoutCreateFlags flags = 0,
                                               string debugName = null)
        {
            ulong hash = ComputeLayoutHash(bindings, flags);
            lock (_lock)
            {
                if (_cache.TryGetValue(hash, out var existing))
                    return existing;

                fixed (DescriptorSetLayoutBinding* pBindings = bindings)
                {
                    var info = new DescriptorSetLayoutCreateInfo
                    {
                        SType = StructureType.DescriptorSetLayoutCreateInfo,
                        BindingCount = (uint)bindings.Length,
                        PBindings = pBindings,
                        Flags = flags,
                    };

                    DescriptorSetLayout layout;
                    var r = _vk.CreateDescriptorSetLayout(_device, info, null, &layout);
                    if (r != Result.Success)
                        throw new VulkanException(r, "CreateDescriptorSetLayout 失败");

                    _cache[hash] = layout;
                    if (debugName != null) _debugNames[hash] = debugName;
                    return layout;
                }
            }
        }

        /// <summary>查询是否已缓存。</summary>
        public bool TryGet(DescriptorSetLayoutBinding[] bindings,
                          DescriptorSetLayoutCreateFlags flags, out DescriptorSetLayout layout)
        {
            ulong hash = ComputeLayoutHash(bindings, flags);
            lock (_lock) return _cache.TryGetValue(hash, out layout);
        }

        /// <summary>当前缓存数量。</summary>
        public int Count
        {
            get { lock (_lock) return _cache.Count; }
        }

        private static ulong ComputeLayoutHash(DescriptorSetLayoutBinding[] bindings,
                                               DescriptorSetLayoutCreateFlags flags)
        {
            // FNV-1a
            ulong hash = 14695981039346656037UL;
            unchecked
            {
                hash ^= (ulong)flags;
                hash *= 1099511628211UL;
                foreach (var b in bindings)
                {
                    hash ^= b.Binding;
                    hash *= 1099511628211UL;
                    hash ^= (ulong)b.DescriptorType;
                    hash *= 1099511628211UL;
                    hash ^= b.DescriptorCount;
                    hash *= 1099511628211UL;
                    hash ^= (ulong)b.StageFlags;
                    hash *= 1099511628211UL;
                }
            }
            return hash;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                foreach (var kv in _cache)
                    _vk.DestroyDescriptorSetLayout(_device, kv.Value, null);
                _cache.Clear();
                _debugNames.Clear();
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 8: 描述符池管理器主类
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 描述符池管理器主入口。
    /// 一个 frame 用一个 DescriptorPoolGroup，帧结束时 reset。
    /// 持久的 set (比如 bindless) 单独管理。
    /// </summary>
    public sealed unsafe class VulkanDescriptorPoolManager : IDisposable
    {
        private readonly Vk _vk;
        private readonly Device _device;
        private readonly DescriptorPoolQuota _frameQuota;
        private readonly DescriptorPoolQuota _persistentQuota;

        // 每帧池组 (默认保留 2 帧的池子，做帧重叠)
        private readonly DescriptorPoolGroup[] _framePools;
        private int _currentFrameIndex;

        // 持久池 (不随帧重置)
        private DescriptorPoolGroup _persistentPool;

        // layout 缓存
        private readonly DescriptorSetLayoutCache _layoutCache;

        // bindless 管理器
        private readonly Dictionary<DescriptorType, BindlessDescriptorManager> _bindlessManagers = new();

        // 统计
        private long _totalAllocated;
        private long _totalFreed;

        private readonly object _lock = new();

        public DescriptorSetLayoutCache LayoutCache => _layoutCache;

        public VulkanDescriptorPoolManager(Vk vk, Device device,
                                           in DescriptorPoolQuota? frameQuota = null,
                                           in DescriptorPoolQuota? persistentQuota = null,
                                           int framesInFlight = 2)
        {
            _vk = vk;
            _device = device;
            _frameQuota = frameQuota ?? DescriptorPoolQuota.Default;
            _persistentQuota = persistentQuota ?? DescriptorPoolQuota.Default;

            _framePools = new DescriptorPoolGroup[framesInFlight];
            for (int i = 0; i < framesInFlight; i++)
            {
                _framePools[i] = new DescriptorPoolGroup(vk, device,
                    DescriptorPoolCreateFlagsExt.None, _frameQuota);
            }

            _persistentPool = new DescriptorPoolGroup(vk, device,
                DescriptorPoolCreateFlagsExt.FreeDescriptorSet, _persistentQuota);

            _layoutCache = new DescriptorSetLayoutCache(vk, device);
        }

        /// <summary>从当前帧池分配 set。</summary>
        public bool AllocateFrameSet(DescriptorSetLayout layout, ulong layoutHash, out DescriptorSet set)
        {
            var pool = _framePools[_currentFrameIndex];
            bool ok = pool.TryAllocate(layout, layoutHash, out set);
            if (ok) Interlocked.Increment(ref _totalAllocated);
            return ok;
        }

        /// <summary>从持久池分配 set。</summary>
        public bool AllocatePersistentSet(DescriptorSetLayout layout, ulong layoutHash, out DescriptorSet set)
        {
            bool ok = _persistentPool.TryAllocate(layout, layoutHash, out set);
            if (ok) Interlocked.Increment(ref _totalAllocated);
            return ok;
        }

        /// <summary>批量分配。</summary>
        public bool AllocateFrameSets(DescriptorSetLayout layout, ulong layoutHash,
                                      int count, DescriptorSet[] outSets)
        {
            var pool = _framePools[_currentFrameIndex];
            bool ok = pool.TryAllocateMany(layout, layoutHash, count, outSets);
            if (ok) Interlocked.Add(ref _totalAllocated, count);
            return ok;
        }

        /// <summary>回收持久 set。</summary>
        public void FreePersistentSet(ulong layoutHash, DescriptorSet set)
        {
            _persistentPool.Recycle(layoutHash, set);
            Interlocked.Increment(ref _totalFreed);
        }

        /// <summary>每帧结束调用，reset 当前帧池。</summary>
        public void OnFrameEnd(int frameIndex)
        {
            // 这里不真 reset，因为可能还在 GPU 上跑
            // 真正 reset 的是"上一帧已完成的池"
            _currentFrameIndex = frameIndex % _framePools.Length;
            _framePools[_currentFrameIndex].ResetCurrent();
        }

        /// <summary>获取/创建 bindless 管理器。</summary>
        public BindlessDescriptorManager GetOrCreateBindless(DescriptorType type, uint maxCount, uint binding)
        {
            lock (_lock)
            {
                if (_bindlessManagers.TryGetValue(type, out var m))
                    return m;
                var nm = new BindlessDescriptorManager(_vk, _device, type, maxCount, binding);
                _bindlessManagers[type] = nm;
                return nm;
            }
        }

        /// <summary>创建一个 DescriptorWriter。</summary>
        public DescriptorWriter CreateWriter()
        {
            return new DescriptorWriter(_vk, _device);
        }

        /// <summary>获取统计。</summary>
        public DescriptorPoolStats GetStats()
        {
            var s = _framePools[_currentFrameIndex].GetStats();
            s.PoolCount += _persistentPool.PoolCount;
            return s;
        }

        /// <summary>获取每帧池统计。</summary>
        public DescriptorPoolStats[] GetFrameStats()
        {
            var arr = new DescriptorPoolStats[_framePools.Length];
            for (int i = 0; i < _framePools.Length; i++)
                arr[i] = _framePools[i].GetStats();
            return arr;
        }

        /// <summary>获取持久池统计。</summary>
        public DescriptorPoolStats GetPersistentStats()
        {
            return _persistentPool.GetStats();
        }

        public void Dispose()
        {
            foreach (var p in _framePools) p.Dispose();
            _persistentPool.Dispose();
            _layoutCache.Dispose();
            foreach (var m in _bindlessManagers.Values) m.Dispose();
            _bindlessManagers.Clear();
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 9: 线程本地描述符池
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 线程本地的描述符池。多线程录制命令时每个线程独立池，避免锁竞争。
    /// </summary>
    public sealed unsafe class ThreadLocalDescriptorPool : IDisposable
    {
        private readonly Vk _vk;
        private readonly Device _device;
        private readonly DescriptorPoolQuota _quota;
        private readonly ThreadLocal<DescriptorPoolGroup> _tlsPool;
        private readonly List<DescriptorPoolGroup> _allPools = new();
        private readonly object _allPoolsLock = new();

        public ThreadLocalDescriptorPool(Vk vk, Device device, in DescriptorPoolQuota quota)
        {
            _vk = vk;
            _device = device;
            _quota = quota;
            _tlsPool = new ThreadLocal<DescriptorPoolGroup>(() =>
            {
                var p = new DescriptorPoolGroup(vk, device, DescriptorPoolCreateFlagsExt.None, quota);
                lock (_allPoolsLock) _allPools.Add(p);
                return p;
            });
        }

        /// <summary>从当前线程池分配 set。</summary>
        public bool TryAllocate(DescriptorSetLayout layout, ulong layoutHash, out DescriptorSet set)
        {
            return _tlsPool.Value.TryAllocate(layout, layoutHash, out set);
        }

        /// <summary>重置当前线程池。</summary>
        public void ResetCurrentThread()
        {
            _tlsPool.Value.ResetCurrent();
        }

        /// <summary>重置所有线程池 (主线程调用)。</summary>
        public void ResetAll()
        {
            lock (_allPoolsLock)
            {
                foreach (var p in _allPools) p.ResetAll();
            }
        }

        public void Dispose()
        {
            lock (_allPoolsLock)
            {
                foreach (var p in _allPools) p.Dispose();
                _allPools.Clear();
            }
            _tlsPool.Dispose();
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 10: 描述符池调试与统计扩展
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>描述符池调试器。</summary>
    public sealed class DescriptorPoolDebugger
    {
        private readonly VulkanDescriptorPoolManager _mgr;

        public DescriptorPoolDebugger(VulkanDescriptorPoolManager mgr)
        {
            _mgr = mgr;
        }

        public string DumpStats()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== Descriptor Pool Stats ===");
            sb.AppendLine(_mgr.GetStats().ToString());
            sb.AppendLine("--- Frame Pools ---");
            foreach (var s in _mgr.GetFrameStats())
                sb.AppendLine(s.ToString());
            sb.AppendLine("--- Persistent Pool ---");
            sb.AppendLine(_mgr.GetPersistentStats().ToString());
            return sb.ToString();
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 11: pipeline layout 缓存
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>Pipeline layout 缓存。</summary>
    public sealed unsafe class PipelineLayoutCache : IDisposable
    {
        private readonly Vk _vk;
        private readonly Device _device;
        private readonly DescriptorSetLayoutCache _layoutCache;
        private readonly Dictionary<ulong, PipelineLayout> _cache = new();
        private readonly object _lock = new();

        public PipelineLayoutCache(Vk vk, Device device, DescriptorSetLayoutCache layoutCache)
        {
            _vk = vk;
            _device = device;
            _layoutCache = layoutCache;
        }

        public PipelineLayout GetOrCreate(DescriptorSetLayout[] setLayouts,
                                          PushConstantRange[] pushConstants = null)
        {
            ulong hash = ComputeHash(setLayouts, pushConstants);
            lock (_lock)
            {
                if (_cache.TryGetValue(hash, out var existing))
                    return existing;

                fixed (DescriptorSetLayout* pLayouts = setLayouts)
                fixed (PushConstantRange* pPC = pushConstants)
                {
                    var info = new PipelineLayoutCreateInfo
                    {
                        SType = StructureType.PipelineLayoutCreateInfo,
                        SetLayoutCount = (uint)setLayouts.Length,
                        PSetLayouts = pLayouts,
                        PushConstantRangeCount = pushConstants != null ? (uint)pushConstants.Length : 0u,
                        PPushConstantRanges = pPC,
                    };

                    PipelineLayout layout;
                    var r = _vk.CreatePipelineLayout(_device, info, null, &layout);
                    if (r != Result.Success)
                        throw new VulkanException(r, "CreatePipelineLayout 失败");
                    _cache[hash] = layout;
                    return layout;
                }
            }
        }

        private static ulong ComputeHash(DescriptorSetLayout[] setLayouts, PushConstantRange[] pushConstants)
        {
            ulong hash = 14695981039346656037UL;
            unchecked
            {
                foreach (var l in setLayouts)
                {
                    hash ^= l.Handle;
                    hash *= 1099511628211UL;
                }
                if (pushConstants != null)
                {
                    foreach (var pc in pushConstants)
                    {
                        hash ^= pc.Offset;
                        hash *= 1099511628211UL;
                        hash ^= pc.Size;
                        hash *= 1099511628211UL;
                        hash ^= (ulong)pc.StageFlags;
                        hash *= 1099511628211UL;
                    }
                }
            }
            return hash;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                foreach (var kv in _cache)
                    _vk.DestroyPipelineLayout(_device, kv.Value, null);
                _cache.Clear();
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 12: 描述符池配置 builder
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>描述符池配置 builder。</summary>
    public sealed class DescriptorPoolConfigBuilder
    {
        private DescriptorPoolQuota? _frameQuota;
        private DescriptorPoolQuota? _persistentQuota;
        private int _framesInFlight = 2;

        public DescriptorPoolConfigBuilder WithFrameQuota(in DescriptorPoolQuota q)
        { _frameQuota = q; return this; }

        public DescriptorPoolConfigBuilder WithPersistentQuota(in DescriptorPoolQuota q)
        { _persistentQuota = q; return this; }

        public DescriptorPoolConfigBuilder WithFramesInFlight(int n)
        { _framesInFlight = n; return this; }

        public (DescriptorPoolQuota frame, DescriptorPoolQuota persistent, int fif) Build()
        {
            return (_frameQuota ?? DescriptorPoolQuota.Default,
                    _persistentQuota ?? DescriptorPoolQuota.Default,
                    _framesInFlight);
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 13: 描述符更新辅助
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>描述符更新辅助。</summary>
    public static class DescriptorUpdateHelper
    {
        /// <summary>批量更新 uniform buffer 描述符。</summary>
        public static unsafe void UpdateUniformBuffers(Vk vk, Device device, DescriptorSet set,
                                                       uint binding, Buffer[] buffers,
                                                       ulong[] offsets, ulong[] ranges)
        {
            int count = buffers.Length;
            var writes = new WriteDescriptorSet[count];
            var infos = new DescriptorBufferInfo[count];

            for (int i = 0; i < count; i++)
            {
                infos[i] = new DescriptorBufferInfo
                {
                    Buffer = buffers[i],
                    Offset = offsets[i],
                    Range = ranges[i],
                };
                writes[i] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = set,
                    DstBinding = binding,
                    DstArrayElement = (uint)i,
                    DescriptorType = DescriptorType.UniformBuffer,
                    DescriptorCount = 1,
                    PBufferInfo = &infos[i],
                };
            }

            fixed (WriteDescriptorSet* pWrites = writes)
            {
                vk.UpdateDescriptorSets(device, (uint)count, pWrites, 0, null);
            }
        }

        /// <summary>批量更新 combined image sampler 描述符。</summary>
        public static unsafe void UpdateCombinedImageSamplers(Vk vk, Device device, DescriptorSet set,
                                                              uint binding, Sampler[] samplers,
                                                              ImageView[] views, ImageLayout[] layouts)
        {
            int count = samplers.Length;
            var writes = new WriteDescriptorSet[count];
            var infos = new DescriptorImageInfo[count];

            for (int i = 0; i < count; i++)
            {
                infos[i] = new DescriptorImageInfo
                {
                    Sampler = samplers[i],
                    ImageView = views[i],
                    ImageLayout = layouts[i],
                };
                writes[i] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = set,
                    DstBinding = binding,
                    DstArrayElement = (uint)i,
                    DescriptorType = DescriptorType.CombinedImageSampler,
                    DescriptorCount = 1,
                    PImageInfo = &infos[i],
                };
            }

            fixed (WriteDescriptorSet* pWrites = writes)
            {
                vk.UpdateDescriptorSets(device, (uint)count, pWrites, 0, null);
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 14: 描述符池分配器接口 (给 RHI 层用)
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>描述符分配器接口。</summary>
    public interface IDescriptorAllocator
    {
        bool AllocateFrameSet(DescriptorSetLayout layout, ulong layoutHash, out DescriptorSet set);
        bool AllocatePersistentSet(DescriptorSetLayout layout, ulong layoutHash, out DescriptorSet set);
        void FreePersistentSet(ulong layoutHash, DescriptorSet set);
        void OnFrameEnd(int frameIndex);
    }

    /// <summary>RHI 桥接的实现。</summary>
    public sealed class RHIDescriptorAllocator : IDescriptorAllocator
    {
        private readonly VulkanDescriptorPoolManager _mgr;
        public RHIDescriptorAllocator(VulkanDescriptorPoolManager mgr) { _mgr = mgr; }
        public bool AllocateFrameSet(DescriptorSetLayout layout, ulong hash, out DescriptorSet set)
            => _mgr.AllocateFrameSet(layout, hash, out set);
        public bool AllocatePersistentSet(DescriptorSetLayout layout, ulong hash, out DescriptorSet set)
            => _mgr.AllocatePersistentSet(layout, hash, out set);
        public void FreePersistentSet(ulong hash, DescriptorSet set) => _mgr.FreePersistentSet(hash, set);
        public void OnFrameEnd(int frameIndex) => _mgr.OnFrameEnd(frameIndex);
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 15: 描述符池分配历史记录 (调试)
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>分配历史记录，调试泄漏用。</summary>
    public sealed class DescriptorAllocationHistory
    {
        private readonly Dictionary<DescriptorSet, AllocationEntry> _active = new();
        private readonly object _lock = new();
        private const int MaxHistory = 10000;
        private readonly Queue<AllocationEntry> _recent = new();

        private struct AllocationEntry
        {
            public DescriptorSet Set;
            public ulong LayoutHash;
            public long FrameIndex;
            public DateTime Timestamp;
            public string Tag;
        }

        public void RecordAllocate(DescriptorSet set, ulong layoutHash, long frame, string tag = null)
        {
            lock (_lock)
            {
                var e = new AllocationEntry
                {
                    Set = set,
                    LayoutHash = layoutHash,
                    FrameIndex = frame,
                    Timestamp = DateTime.UtcNow,
                    Tag = tag,
                };
                _active[set] = e;
                _recent.Enqueue(e);
                if (_recent.Count > MaxHistory) _recent.Dequeue();
            }
        }

        public void RecordFree(DescriptorSet set)
        {
            lock (_lock) _active.Remove(set);
        }

        public int ActiveCount
        {
            get { lock (_lock) return _active.Count; }
        }

        public void DumpActive(System.IO.TextWriter writer)
        {
            lock (_lock)
            {
                writer.WriteLine($"=== Active descriptor sets: {_active.Count} ===");
                foreach (var kv in _active)
                {
                    writer.WriteLine($"  set=0x{kv.Key.Handle:X} layout=0x{kv.Value.LayoutHash:X} " +
                                     $"frame={kv.Value.FrameIndex} tag={kv.Value.Tag}");
                }
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 16: 描述符池监控
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>后台监控。</summary>
    public sealed class DescriptorPoolMonitor : IDisposable
    {
        private readonly VulkanDescriptorPoolManager _mgr;
        private System.Threading.Timer _timer;
        private readonly object _lock = new();
        private readonly List<DescriptorPoolStats> _history = new();
        private const int MaxHistory = 60;
        private bool _disposed;

        public event Action<DescriptorPoolStats> OnStatsUpdated;

        public DescriptorPoolMonitor(VulkanDescriptorPoolManager mgr, TimeSpan? interval = null)
        {
            _mgr = mgr;
            _timer = new System.Threading.Timer(_ => Tick(), null,
                interval ?? TimeSpan.FromSeconds(1),
                interval ?? TimeSpan.FromSeconds(1));
        }

        private void Tick()
        {
            if (_disposed) return;
            var s = _mgr.GetStats();
            lock (_lock)
            {
                _history.Add(s);
                if (_history.Count > MaxHistory) _history.RemoveAt(0);
            }
            OnStatsUpdated?.Invoke(s);
        }

        public DescriptorPoolStats[] GetHistory()
        {
            lock (_lock) return _history.ToArray();
        }

        public void Dispose()
        {
            _disposed = true;
            _timer?.Dispose();
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 17: 描述符池版本信息
    // ════════════════════════════════════════════════════════════════════════════

    public static class DescriptorPoolVersion
    {
        public const string Version = "1.0.0";
        public const string Description = "ShanyuEngine Vulkan Descriptor Pool Manager (bindless-ready)";
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 18: 描述符池使用示例 (注释)
    // ════════════════════════════════════════════════════════════════════════════

    /*
    // === 使用示例 ===
    //
    // var mgr = new VulkanDescriptorPoolManager(vk, device);
    //
    // // 定义 layout
    // var bindings = new[]
    // {
    //     new DescriptorSetLayoutBinding
    //     {
    //         Binding = 0,
    //         DescriptorType = DescriptorType.UniformBuffer,
    //         DescriptorCount = 1,
    //         StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
    //     },
    //     new DescriptorSetLayoutBinding
    //     {
    //         Binding = 1,
    //         DescriptorType = DescriptorType.CombinedImageSampler,
    //         DescriptorCount = 1,
    //         StageFlags = ShaderStageFlags.FragmentBit,
    //     },
    // };
    // var layout = mgr.LayoutCache.GetOrCreate(bindings, debugName: "MeshSet");
    //
    // // 分配并写入
    // mgr.AllocateFrameSet(layout, hash, out var set);
    // mgr.CreateWriter()
    //     .WriteUniformBuffer(0, uniformBuffer, 0, sizeof(ViewProj))
    //     .WriteCombinedImageSampler(1, sampler, textureView, ImageLayout.ShaderReadOnlyOptimal)
    //     .UpdateSet(set);
    //
    // // bindless
    // var bindless = mgr.GetOrCreateBindless(DescriptorType.CombinedImageSampler, 100000, 0);
    // uint idx = bindless.BindImage(imageId, view, ImageLayout.ShaderReadOnlyOptimal, sampler);
    */

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 19: 内联 uniform block 支持
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>内联 uniform block 写入器。</summary>
    public sealed unsafe class InlineUniformBlockWriter
    {
        private readonly Vk _vk;
        private readonly Device _device;
        private readonly DescriptorSet _set;
        private readonly uint _binding;
        private readonly uint _dstArrayElement;
        private byte[] _data;
        private int _size;

        public InlineUniformBlockWriter(Vk vk, Device device, DescriptorSet set,
                                        uint binding, uint dstArrayElement = 0)
        {
            _vk = vk;
            _device = device;
            _set = set;
            _binding = binding;
            _dstArrayElement = dstArrayElement;
        }

        public unsafe void Update(byte* data, int size)
        {
            var write = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _set,
                DstBinding = _binding,
                DstArrayElement = _dstArrayElement,
                DescriptorType = DescriptorType.InlineUniformBlock,
                DescriptorCount = (uint)size,
            };
            // VK_KHR_inline_uniform_block 需要pNext 指向 InlineUniformBlock
            // 这里简化
            _vk.UpdateDescriptorSets(_device, 1, &write, 0, null);
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 20: 描述符池分配策略
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>分配策略。</summary>
    public enum DescriptorAllocationStrategy
    {
        /// <summary>优先用回收站，没有再分配。</summary>
        RecycleFirst,
        /// <summary>总是新分配 (调试用)。</summary>
        AlwaysNew,
        /// <summary>优先 bindless。</summary>
        PreferBindless,
    }

    /// <summary>策略配置。</summary>
    public sealed class DescriptorAllocationConfig
    {
        public DescriptorAllocationStrategy Strategy { get; set; } = DescriptorAllocationStrategy.RecycleFirst;
        public bool UseBindless { get; set; } = true;
        public uint BindlessImageCount { get; set; } = 100000;
        public uint BindlessBufferCount { get; set; } = 10000;
        public int FramesInFlight { get; set; } = 2;
        public int MaxRecycledPerLayout { get; set; } = 256;
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 21: 描述符池工厂
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>描述符池管理器工厂。</summary>
    public static class DescriptorPoolManagerFactory
    {
        public static VulkanDescriptorPoolManager Create(Vk vk, Device device,
                                                        DescriptorAllocationConfig config = null)
        {
            config ??= new DescriptorAllocationConfig();
            return new VulkanDescriptorPoolManager(vk, device,
                DescriptorPoolQuota.Default, DescriptorPoolQuota.Default, config.FramesInFlight);
        }

        public static VulkanDescriptorPoolManager CreateForBindless(Vk vk, Device device)
        {
            return new VulkanDescriptorPoolManager(vk, device,
                DescriptorPoolQuota.Bindless, DescriptorPoolQuota.Bindless);
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 22: 单元测试桩
    // ════════════════════════════════════════════════════════════════════════════

    public static class DescriptorPoolTestStub
    {
        public static ulong TestLayoutHash(DescriptorSetLayoutBinding[] bindings)
        {
            ulong hash = 14695981039346656037UL;
            unchecked
            {
                foreach (var b in bindings)
                {
                    hash ^= b.Binding;
                    hash *= 1099511628211UL;
                }
            }
            return hash;
        }

        public static bool TestQuotaEnough(in DescriptorPoolQuota q, DescriptorType t, uint need)
        {
            if (!q.MaxPerType.TryGetValue(t, out var cap)) return false;
            return cap >= need;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 23: 描述符池 GC
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 描述符池垃圾回收。定期把过期的池子清掉。
    /// </summary>
    public sealed class DescriptorPoolGC
    {
        private readonly VulkanDescriptorPoolManager _mgr;
        private readonly TimeSpan _interval;
        private System.Threading.Timer _timer;

        public DescriptorPoolGC(VulkanDescriptorPoolManager mgr, TimeSpan? interval = null)
        {
            _mgr = mgr;
            _interval = interval ?? TimeSpan.FromSeconds(30);
        }

        public void Start()
        {
            _timer = new System.Threading.Timer(_ => Collect(), null, _interval, _interval);
        }

        public void Stop()
        {
            _timer?.Change(-1, -1);
        }

        public void Collect()
        {
            // 实际实现里会检查每个池的使用率，太低的回收
            // 这里只是骨架
            var stats = _mgr.GetStats();
            if (stats.PoolCount > 10)
            {
                // 池子太多了，触发重置
                Console.WriteLine($"[DescGC] 池子数 {stats.PoolCount}，考虑整理");
            }
        }

        public void Dispose()
        {
            _timer?.Dispose();
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 24: 描述符池分配的帧级跟踪
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>每帧分配跟踪。</summary>
    public sealed class FrameDescriptorTracker
    {
        private readonly Dictionary<long, int> _frameAllocCount = new();
        private readonly Dictionary<long, int> _frameFreeCount = new();
        private readonly object _lock = new();

        public void RecordAllocate(long frame)
        {
            lock (_lock)
            {
                if (!_frameAllocCount.TryGetValue(frame, out var c)) c = 0;
                _frameAllocCount[frame] = c + 1;
            }
        }

        public void RecordFree(long frame)
        {
            lock (_lock)
            {
                if (!_frameFreeCount.TryGetValue(frame, out var c)) c = 0;
                _frameFreeCount[frame] = c + 1;
            }
        }

        public (int alloc, int free) GetFrame(long frame)
        {
            lock (_lock)
            {
                _frameAllocCount.TryGetValue(frame, out int a);
                _frameFreeCount.TryGetValue(frame, out int f);
                return (a, f);
            }
        }
    }
}

// 文件结束 - VulkanDescriptorPoolManager.cs
