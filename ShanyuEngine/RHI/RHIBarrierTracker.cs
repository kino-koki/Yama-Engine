// ════════════════════════════════════════════════════════════════════════════
// 山雨引擎 - RHI 屏障跟踪器 (RHIBarrierTracker)
//
// 负责跟踪每个资源的当前状态，自动计算需要的屏障转换。
// 思路借鉴 D3D12 的隐式屏障提升 + decay 机制，以及 Granite 的 barrier builder。
//
// 关键点:
//   * 每个资源维护一个"当前状态"表（按 subresource 分割）
//   * Transition 时自动计算 src->dst 的屏障
//   * 支持"隐式提升"(implicit promotion) - Common 状态可以无屏障提升到只读状态
//   * 支持"自动衰减"(auto decay) - GPU 执行完后某些状态会衰减回 Common
//   * 支持批处理 - 把多个屏障合并成一次 ResourceBarrier 调用
//
// FIXME: 目前只做了 buffer 和 texture 的全局状态跟踪，subresource 粒度的还没做
// TODO: 加个看板统计屏障数量，方便调优
// ════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ShanyuEngine.RHI
{
    /// <summary>
    /// 屏障跟踪器：记录每个资源的当前 RHIResourceState，自动计算需要的转换屏障。
    /// </summary>
    public sealed class RHIBarrierTracker : IDisposable
    {
        // ──────────────────────────────────────────────────────────────────────
        // 数据结构
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 单个资源的状态跟踪条目。
        /// 用数组按 subresource 索引，如果资源没有 subresource 就只存一个。
        /// </summary>
        private sealed class ResourceStateEntry
        {
            public RHIResource Resource;
            public RHIResourceState[] SubresourceStates;
            public bool IsTexture;
            public int NumSubresources;
            public ulong LastUsedFrame;

            // 用于快速判断整个资源是否处于同一状态
            public bool UniformState;
            public RHIResourceState CommonState;

            public ResourceStateEntry(RHIResource resource, bool isTexture, int numSubresources)
            {
                Resource = resource;
                IsTexture = isTexture;
                NumSubresources = Math.Max(1, numSubresources);
                SubresourceStates = new RHIResourceState[NumSubresources];
                CommonState = RHIResourceState.Common;
                UniformState = true;
                LastUsedFrame = 0;

                // 默认全部 Common
                for (int i = 0; i < NumSubresources; i++)
                    SubresourceStates[i] = RHIResourceState.Common;
            }

            public void ResetTo(RHIResourceState state)
            {
                for (int i = 0; i < NumSubresources; i++)
                    SubresourceStates[i] = state;
                CommonState = state;
                UniformState = true;
            }
        }

        /// <summary>资源 -> 状态条目的映射</summary>
        private readonly Dictionary<RHIResource, ResourceStateEntry> _stateMap = new();

        /// <summary>待刷新的屏障列表（在 BeginBatch / EndBatch 之间收集）</summary>
        private readonly List<RHIBarrier> _pendingBarriers = new(64);

        /// <summary>当前帧索引</summary>
        private ulong _currentFrame;

        /// <summary>是否在批处理模式中</summary>
        private bool _inBatch;

        /// <summary>统计信息</summary>
        private BarrierStats _stats;
        private readonly BarrierStats _statsLastFrame;

        /// <summary>最大允许的待处理屏障数（防止无限增长）</summary>
        private const int MaxPendingBarriers = 4096;

        /// <summary>是否启用隐式提升优化</summary>
        public bool EnableImplicitPromotion { get; set; } = true;

        /// <summary>是否启用自动衰减优化</summary>
        public bool EnableAutoDecay { get; set; } = true;

        /// <summary>是否启用屏障合并</summary>
        public bool EnableBarrierMerging { get; set; } = true;

        private bool _disposed;

        // ──────────────────────────────────────────────────────────────────────
        // 公开统计
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>屏障统计信息</summary>
        public ref readonly BarrierStats Stats => ref _stats;

        /// <summary>上一帧的统计快照</summary>
        public ref readonly BarrierStats LastFrameStats => ref _statsLastFrame;

        /// <summary>当前跟踪的资源数量</summary>
        public int TrackedResourceCount => _stateMap.Count;

        /// <summary>当前待处理的屏障数量</summary>
        public int PendingBarrierCount => _pendingBarriers.Count;

        // ──────────────────────────────────────────────────────────────────────
        // 构造/析构
        // ──────────────────────────────────────────────────────────────────────

        public RHIBarrierTracker()
        {
            _statsLastFrame = new BarrierStats();
        }

        ~RHIBarrierTracker()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (_disposed) return;
            if (disposing)
            {
                _stateMap.Clear();
                _pendingBarriers.Clear();
            }
            _disposed = true;
        }

        // ──────────────────────────────────────────────────────────────────────
        // 帧管理
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>帧开始，更新帧索引。</summary>
        public void BeginFrame(ulong frameIndex)
        {
            _currentFrame = frameIndex;

            // 保存上一帧统计
            _statsLastFrame.TotalBarriers = _stats.TotalBarriers;
            _statsLastFrame.TransitionBarriers = _stats.TransitionBarriers;
            _statsLastFrame.UAVBarriers = _stats.UAVBarriers;
            _statsLastFrame.AliasingBarriers = _stats.AliasingBarriers;
            _statsLastFrame.SkippedByPromotion = _stats.SkippedByPromotion;
            _statsLastFrame.SkippedByDecay = _stats.SkippedByDecay;
            _statsLastFrame.MergedBarriers = _stats.MergedBarriers;
            _statsLastFrame.BatchCount = _stats.BatchCount;
            _statsLastFrame.PeakPendingBarriers = _stats.PeakPendingBarriers;

            // 重置本帧计数
            _stats.TotalBarriers = 0;
            _stats.TransitionBarriers = 0;
            _stats.UAVBarriers = 0;
            _stats.AliasingBarriers = 0;
            _stats.SkippedByPromotion = 0;
            _stats.SkippedByDecay = 0;
            _stats.MergedBarriers = 0;
            _stats.BatchCount = 0;
            _stats.PeakPendingBarriers = 0;

            // 如果开启自动衰减，在帧结束时把符合条件的资源衰减回 Common
            if (EnableAutoDecay)
            {
                DecayResources();
            }
        }

        /// <summary>帧结束，刷新所有待处理屏障。</summary>
        public void EndFrame(IRHICommandList cmdList)
        {
            if (_inBatch)
            {
                EndBatch(cmdList);
            }
            else if (_pendingBarriers.Count > 0)
            {
                FlushBarriers(cmdList);
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // 资源注册
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 注册一个资源到跟踪器。资源在创建后必须先注册才能使用屏障功能。
        /// </summary>
        /// <param name="resource">要注册的资源</param>
        /// <param name="initialState">初始状态，通常是 Common 或 CopyDest</param>
        /// <param name="numSubresources">subresource 数量（mip * array * plane），buffer 填 1</param>
        public void Register(RHIResource resource, RHIResourceState initialState, int numSubresources = 1)
        {
            if (resource == null)
                throw new ArgumentNullException(nameof(resource));
            if (_stateMap.ContainsKey(resource))
            {
                // 已注册，只更新状态
                SetState(resource, initialState, numSubresources);
                return;
            }

            bool isTexture = resource is RHITexture;
            var entry = new ResourceStateEntry(resource, isTexture, numSubresources);
            entry.ResetTo(initialState);
            entry.LastUsedFrame = _currentFrame;
            _stateMap[resource] = entry;
            _stats.TrackedResources = _stateMap.Count;
        }

        /// <summary>
        /// 注销资源。资源销毁前应该调用这个，避免悬垂引用。
        /// </summary>
        public void Unregister(RHIResource resource)
        {
            if (resource == null) return;
            _stateMap.Remove(resource);
            _stats.TrackedResources = _stateMap.Count;
        }

        /// <summary>
        /// 强制设置资源状态（不生成屏障）。用于外部已经做了屏障的情况。
        /// </summary>
        public void SetState(RHIResource resource, RHIResourceState state, int numSubresources = 1)
        {
            if (!_stateMap.TryGetValue(resource, out var entry))
            {
                Register(resource, state, numSubresources);
                return;
            }
            entry.ResetTo(state);
            entry.LastUsedFrame = _currentFrame;
        }

        /// <summary>
        /// 获取资源当前状态。如果资源是 uniform 状态返回 CommonState，否则返回混合标志。
        /// </summary>
        public RHIResourceState GetState(RHIResource resource)
        {
            if (!_stateMap.TryGetValue(resource, out var entry))
                return RHIResourceState.Common;
            return entry.UniformState ? entry.CommonState : RHIResourceState.Common; // 混合状态返回 Common 作为fallback
        }

        /// <summary>
        /// 获取指定 subresource 的状态。
        /// </summary>
        public RHIResourceState GetSubresourceState(RHIResource resource, int subresource)
        {
            if (!_stateMap.TryGetValue(resource, out var entry))
                return RHIResourceState.Common;
            if (subresource < 0 || subresource >= entry.NumSubresources)
                throw new ArgumentOutOfRangeException(nameof(subresource));
            return entry.SubresourceStates[subresource];
        }

        // ──────────────────────────────────────────────────────────────────────
        // 屏障生成 - 核心逻辑
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 把资源转换到目标状态，自动计算需要的屏障并加入待处理列表。
        /// </summary>
        /// <param name="resource">目标资源</param>
        /// <param name="targetState">要转换到的状态</param>
        public void TransitionTo(RHIResource resource, RHIResourceState targetState)
        {
            TransitionTo(resource, targetState, 0, AllSubresources);
        }

        /// <summary>
        /// 把资源的指定 subresource 转换到目标状态。
        /// </summary>
        /// <param name="resource">目标资源</param>
        /// <param name="targetState">要转换到的状态</param>
        /// <param name="subresource">subresource 索引，传 AllSubresources 表示全部</param>
        /// <param name="numSubresources">要转换的 subresource 数量</param>
        public void TransitionTo(RHIResource resource, RHIResourceState targetState,
                                   int subresource, int numSubresources)
        {
            if (resource == null) throw new ArgumentNullException(nameof(resource));
            if (!_stateMap.TryGetValue(resource, out var entry))
            {
                // 没注册过，自动注册并假设初始状态是 Common
                Register(resource, RHIResourceState.Common, numSubresources);
                entry = _stateMap[resource];
            }

            entry.LastUsedFrame = _currentFrame;

            // 如果目标状态和当前状态一样，不需要做屏障
            if (entry.UniformState && entry.CommonState == targetState)
            {
                _stats.SkippedByPromotion++; // 其实是 same-state skip，统计混在一起了
                return;
            }

            // 检查隐式提升：从 Common 到某些只读状态可以不做屏障
            if (EnableImplicitPromotion && entry.UniformState &&
                entry.CommonState == RHIResourceState.Common &&
                CanImplicitPromote(targetState))
            {
                // 不需要屏障，直接更新状态
                entry.CommonState = targetState;
                for (int i = 0; i < entry.NumSubresources; i++)
                    entry.SubresourceStates[i] = targetState;
                _stats.SkippedByPromotion++;
                return;
            }

            // 生成 transition barrier
            if (subresource == AllSubresources)
            {
                // 全部 subresource
                GenerateTransitionAll(entry, targetState);
            }
            else
            {
                // 指定 subresource 范围
                GenerateTransitionRange(entry, targetState, subresource, numSubresources);
            }
        }

        /// <summary>表示全部 subresource 的特殊值</summary>
        public const int AllSubresources = -1;

        /// <summary>
        /// 为整个资源生成转换屏障。
        /// </summary>
        private void GenerateTransitionAll(ResourceStateEntry entry, RHIResourceState targetState)
        {
            if (entry.UniformState)
            {
                // 简单情况：整个资源一个状态
                var srcState = entry.CommonState;
                if (srcState != targetState)
                {
                    AddBarrier(new RHIBarrier
                    {
                        Type = RHIBarrierType.Transition,
                        Resource = entry.Resource,
                        SourceState = srcState,
                        TargetState = targetState,
                        Subresource = AllSubresources,
                    });
                    _stats.TransitionBarriers++;
                }
                entry.CommonState = targetState;
                entry.UniformState = true;
                for (int i = 0; i < entry.NumSubresources; i++)
                    entry.SubresourceStates[i] = targetState;
            }
            else
            {
                // 复杂情况：不同 subresource 有不同状态
                // 把相邻的相同状态的 subresource 合并成一个屏障
                int startIdx = 0;
                RHIResourceState curState = entry.SubresourceStates[0];

                for (int i = 1; i <= entry.NumSubresources; i++)
                {
                    bool isLast = (i == entry.NumSubresources);
                    bool stateChanged = !isLast && entry.SubresourceStates[i] != curState;

                    if (isLast || stateChanged)
                    {
                        if (curState != targetState)
                        {
                            int endIdx = isLast ? entry.NumSubresources - 1 : i - 1;
                            int count = endIdx - startIdx + 1;

                            if (count == entry.NumSubresources)
                            {
                                // 整个资源一起转
                                AddBarrier(new RHIBarrier
                                {
                                    Type = RHIBarrierType.Transition,
                                    Resource = entry.Resource,
                                    SourceState = curState,
                                    TargetState = targetState,
                                    Subresource = AllSubresources,
                                });
                            }
                            else
                            {
                                // 部分转换 - 这里简化处理，每个 subresource 单独一个屏障
                                // FIXME: 应该支持 range，但目前 RHI 接口只支持单个或全部
                                for (int j = startIdx; j <= endIdx; j++)
                                {
                                    AddBarrier(new RHIBarrier
                                    {
                                        Type = RHIBarrierType.Transition,
                                        Resource = entry.Resource,
                                        SourceState = curState,
                                        TargetState = targetState,
                                        Subresource = j,
                                    });
                                }
                            }
                            _stats.TransitionBarriers += count;
                        }

                        if (!isLast)
                        {
                            startIdx = i;
                            curState = entry.SubresourceStates[i];
                        }
                    }
                }

                // 更新所有 subresource 到目标状态
                for (int i = 0; i < entry.NumSubresources; i++)
                    entry.SubresourceStates[i] = targetState;
                entry.CommonState = targetState;
                entry.UniformState = true;
            }
        }

        /// <summary>
        /// 为指定范围的 subresource 生成转换屏障。
        /// </summary>
        private void GenerateTransitionRange(ResourceStateEntry entry, RHIResourceState targetState,
                                              int startSubresource, int count)
        {
            int endSubresource = Math.Min(startSubresource + count - 1, entry.NumSubresources - 1);

            for (int i = startSubresource; i <= endSubresource; i++)
            {
                var srcState = entry.SubresourceStates[i];
                if (srcState != targetState)
                {
                    AddBarrier(new RHIBarrier
                    {
                        Type = RHIBarrierType.Transition,
                        Resource = entry.Resource,
                        SourceState = srcState,
                        TargetState = targetState,
                        Subresource = i,
                    });
                    _stats.TransitionBarriers++;
                }
                entry.SubresourceStates[i] = targetState;
            }

            // 检查是否所有 subresource 现在都一样
            CheckUniform(entry);
        }

        /// <summary>
        /// 检查 entry 的所有 subresource 是否处于同一状态，更新 UniformState 标志。
        /// </summary>
        private void CheckUniform(ResourceStateEntry entry)
        {
            if (entry.NumSubresources <= 1)
            {
                entry.UniformState = true;
                entry.CommonState = entry.SubresourceStates[0];
                return;
            }

            var first = entry.SubresourceStates[0];
            for (int i = 1; i < entry.NumSubresources; i++)
            {
                if (entry.SubresourceStates[i] != first)
                {
                    entry.UniformState = false;
                    return;
                }
            }
            entry.UniformState = true;
            entry.CommonState = first;
        }

        /// <summary>
        /// 添加 UAV 屏障。用于确保两个 UAV 操作之间的顺序。
        /// </summary>
        public void UAVBarrier(RHIResource resource)
        {
            if (resource == null) throw new ArgumentNullException(nameof(resource));
            AddBarrier(new RHIBarrier
            {
                Type = RHIBarrierType.UAV,
                Resource = resource,
                SourceState = RHIResourceState.UnorderedAccess,
                TargetState = RHIResourceState.UnorderedAccess,
                Subresource = AllSubresources,
            });
            _stats.UAVBarriers++;
        }

        /// <summary>
        /// 添加别名屏障。用于别名资源的切换。
        /// </summary>
        public void AliasingBarrier(RHIResource before, RHIResource after)
        {
            AddBarrier(new RHIBarrier
            {
                Type = RHIBarrierType.Aliasing,
                Resource = after,
                ResourceBefore = before,
                SourceState = RHIResourceState.Common,
                TargetState = RHIResourceState.Common,
                Subresource = AllSubresources,
            });
            _stats.AliasingBarriers++;
        }

        // ──────────────────────────────────────────────────────────────────────
        // 隐式提升 / 自动衰减
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 判断从 Common 状态是否可以隐式提升到目标状态。
        /// 参考 D3D12 文档：Common 状态可以隐式提升到任何只读状态 + 某些写状态。
        /// </summary>
        private static bool CanImplicitPromote(RHIResourceState targetState)
        {
            // 这些是可以隐式提升的状态
            const RHIResourceState promotable =
                RHIResourceState.VertexAndConstantBuffer |
                RHIResourceState.IndexBuffer |
                RHIResourceState.NonPixelShaderResource |
                RHIResourceState.PixelShaderResource |
                RHIResourceState.IndirectArgument |
                RHIResourceState.CopySource |
                RHIResourceState.DepthRead;

            return (targetState & ~promotable) == 0 && targetState != 0;
        }

        /// <summary>
        /// 判断状态是否可以自动衰减回 Common。
        /// GPU 执行完后，如果资源处于某些只读状态，且后续是写操作，可以衰减。
        /// </summary>
        private static bool CanDecay(RHIResourceState state)
        {
            // 这些状态在 GPU 完成后会自动衰减到 Common
            const RHIResourceState decayable =
                RHIResourceState.NonPixelShaderResource |
                RHIResourceState.PixelShaderResource |
                RHIResourceState.CopySource |
                RHIResourceState.IndirectArgument |
                RHIResourceState.VertexAndConstantBuffer |
                RHIResourceState.IndexBuffer;

            return (state & decayable) != 0 && (state & ~decayable) == 0;
        }

        /// <summary>
        /// 在帧结束时把符合条件的资源状态衰减回 Common。
        /// 这样下一帧就可以利用隐式提升。
        /// </summary>
        private void DecayResources()
        {
            foreach (var kv in _stateMap)
            {
                var entry = kv.Value;
                if (!entry.UniformState) continue;
                if (CanDecay(entry.CommonState))
                {
                    entry.CommonState = RHIResourceState.Common;
                    for (int i = 0; i < entry.NumSubresources; i++)
                        entry.SubresourceStates[i] = RHIResourceState.Common;
                    _stats.SkippedByDecay++;
                }
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // 批处理
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 开始屏障批处理模式。之后的 TransitionTo 调用只会收集屏障，不立即提交。
        /// </summary>
        public void BeginBatch()
        {
            if (_inBatch)
                throw new InvalidOperationException("已经在批处理模式了，不能重复 BeginBatch");
            _inBatch = true;
        }

        /// <summary>
        /// 结束批处理模式，把收集的屏障一次性提交。
        /// </summary>
        public void EndBatch(IRHICommandList cmdList)
        {
            if (!_inBatch)
                throw new InvalidOperationException("不在批处理模式，不能 EndBatch");
            _inBatch = false;

            if (_pendingBarriers.Count == 0)
                return;

            // 如果启用合并，先合并相同资源的屏障
            if (EnableBarrierMerging)
            {
                MergeBarriers();
            }

            FlushBarriers(cmdList);
            _stats.BatchCount++;
        }

        /// <summary>
        /// 立即刷新所有待处理屏障到命令列表。
        /// </summary>
        public void Flush(IRHICommandList cmdList)
        {
            if (_inBatch)
            {
                // 在 batch 模式下调用 Flush 会强制提交但不结束 batch
            }
            FlushBarriers(cmdList);
        }

        /// <summary>
        /// 实际执行屏障提交。
        /// </summary>
        private void FlushBarriers(IRHICommandList cmdList)
        {
            if (_pendingBarriers.Count == 0) return;
            if (cmdList == null) throw new ArgumentNullException(nameof(cmdList));

            // 调用命令列表的 ResourceBarrier 方法
            cmdList.ResourceBarrier(_pendingBarriers);

            _stats.TotalBarriers += _pendingBarriers.Count;
            _pendingBarriers.Clear();
        }

        /// <summary>
        /// 添加屏障到待处理列表。如果不在 batch 模式，会立即提交。
        /// </summary>
        private void AddBarrier(RHIBarrier barrier)
        {
            if (_pendingBarriers.Count >= MaxPendingBarriers)
            {
                // 达到上限，强制刷新
                // FIXME: 这里应该抛异常还是自动刷新？先自动刷新吧
                if (!_inBatch)
                {
                    // 不在 batch 模式，无法刷新（没有 cmdList），只能丢掉
                    // 这种情况不应该发生
                    Debug.Assert(false, "屏障数量超过上限且无法刷新");
                    return;
                }
            }

            _pendingBarriers.Add(barrier);

            if (_pendingBarriers.Count > _stats.PeakPendingBarriers)
                _stats.PeakPendingBarriers = _pendingBarriers.Count;

            // 如果不在 batch 模式，标记需要刷新
            // 实际刷新由调用者通过 Flush 或 EndFrame 触发
        }

        /// <summary>
        /// 合并相邻的相同资源、相同类型的屏障。
        /// </summary>
        private void MergeBarriers()
        {
            if (_pendingBarriers.Count <= 1) return;

            int originalCount = _pendingBarriers.Count;
            var merged = new List<RHIBarrier>(_pendingBarriers.Count);
            var seen = new Dictionary<RHIResource, int>(); // resource -> index in merged

            foreach (var barrier in _pendingBarriers)
            {
                if (barrier.Type == RHIBarrierType.Transition &&
                    seen.TryGetValue(barrier.Resource, out int idx))
                {
                    var existing = merged[idx];
                    // 如果目标状态一样，可以合并
                    // 实际上同一个资源在一个 batch 里不应该有两个不同的目标状态
                    // 如果发生了，后面的覆盖前面的
                    if (existing.SourceState == barrier.TargetState)
                    {
                        // A->B, B->C => A->C
                        existing.SourceState = barrier.SourceState;
                        _stats.MergedBarriers++;
                    }
                    else
                    {
                        // 冲突，都保留
                        merged.Add(barrier);
                        seen[barrier.Resource] = merged.Count - 1;
                    }
                }
                else
                {
                    merged.Add(barrier);
                    if (barrier.Type == RHIBarrierType.Transition)
                        seen[barrier.Resource] = merged.Count - 1;
                }
            }

            if (merged.Count < originalCount)
            {
                _pendingBarriers.Clear();
                _pendingBarriers.AddRange(merged);
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // 高级功能：自动屏障
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 在执行 Copy 操作前自动插入屏障。
        /// 把 src 转到 CopySource，dst 转到 CopyDest。
        /// </summary>
        public void PrepareForCopy(IRHICommandList cmdList, RHIResource src, RHIResource dst)
        {
            BeginBatch();
            TransitionTo(src, RHIResourceState.CopySource);
            TransitionTo(dst, RHIResourceState.CopyDest);
            EndBatch(cmdList);
        }

        /// <summary>
        /// 在执行渲染前自动插入屏障。把 RT 转到 RenderTarget 状态。
        /// </summary>
        public void PrepareForRender(IRHICommandList cmdList, params RHIResource[] renderTargets)
        {
            BeginBatch();
            foreach (var rt in renderTargets)
            {
                TransitionTo(rt, RHIResourceState.RenderTarget);
            }
            EndBatch(cmdList);
        }

        /// <summary>
        /// 在执行计算前自动插入屏障。把 UAV 资源转到 UnorderedAccess 状态。
        /// </summary>
        public void PrepareForCompute(IRHICommandList cmdList, params RHIResource[] uavs)
        {
            BeginBatch();
            foreach (var uav in uavs)
            {
                TransitionTo(uav, RHIResourceState.UnorderedAccess);
            }
            EndBatch(cmdList);
        }

        /// <summary>
        /// 渲染完成后把 RT 转回 Present 或 Common 状态。
        /// </summary>
        public void PostRender(IRHICommandList cmdList, RHIResource renderTarget, bool toPresent = false)
        {
            TransitionTo(renderTarget, toPresent ? RHIResourceState.Present : RHIResourceState.Common);
            Flush(cmdList);
        }

        /// <summary>
        /// 在读取纹理前自动插入屏障。转到 ShaderResource 状态。
        /// </summary>
        public void PrepareForRead(IRHICommandList cmdList, params RHIResource[] resources)
        {
            BeginBatch();
            foreach (var res in resources)
            {
                TransitionTo(res, RHIResourceState.PixelShaderResource |
                                   RHIResourceState.NonPixelShaderResource);
            }
            EndBatch(cmdList);
        }

        /// <summary>
        /// 在写入 UAV 前（第一个操作前）插入 UAV 屏障确保之前的写操作完成。
        /// </summary>
        public void PrepareUAVWrite(IRHICommandList cmdList, params RHIResource[] uavs)
        {
            BeginBatch();
            foreach (var uav in uavs)
            {
                UAVBarrier(uav);
            }
            EndBatch(cmdList);
        }

        // ──────────────────────────────────────────────────────────────────────
        // 调试 / 诊断
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 获取所有被跟踪资源的当前状态。用于调试和可视化。
        /// </summary>
        public List<ResourceStateInfo> GetAllResourceStates()
        {
            var result = new List<ResourceStateInfo>(_stateMap.Count);
            foreach (var kv in _stateMap)
            {
                var entry = kv.Value;
                result.Add(new ResourceStateInfo
                {
                    Resource = entry.Resource,
                    IsTexture = entry.IsTexture,
                    NumSubresources = entry.NumSubresources,
                    UniformState = entry.UniformState,
                    CurrentState = entry.CommonState,
                    SubresourceStates = (RHIResourceState[])entry.SubresourceStates.Clone(),
                    LastUsedFrame = entry.LastUsedFrame,
                });
            }
            return result;
        }

        /// <summary>
        /// 验证资源是否处于期望状态。如果不符，返回 false 并输出警告。
        /// </summary>
        public bool ValidateState(RHIResource resource, RHIResourceState expected, out string errorMsg)
        {
            errorMsg = null;
            if (!_stateMap.TryGetValue(resource, out var entry))
            {
                errorMsg = $"资源未注册到屏障跟踪器";
                return false;
            }
            if (!entry.UniformState)
            {
                errorMsg = $"资源处于混合状态，无法验证全局状态";
                return false;
            }
            if (entry.CommonState != expected)
            {
                errorMsg = $"状态不匹配: 期望 {expected}, 实际 {entry.CommonState}";
                return false;
            }
            return true;
        }

        /// <summary>
        /// 生成诊断报告字符串。
        /// </summary>
        public string GenerateReport()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("═══ RHIBarrierTracker 诊断报告 ═══");
            sb.AppendLine($"跟踪资源数: {_stateMap.Count}");
            sb.AppendLine($"待处理屏障: {_pendingBarriers.Count}");
            sb.AppendLine();
            sb.AppendLine("── 本帧统计 ──");
            sb.AppendLine($"  总屏障数: {_stats.TotalBarriers}");
            sb.AppendLine($"  Transition: {_stats.TransitionBarriers}");
            sb.AppendLine($"  UAV: {_stats.UAVBarriers}");
            sb.AppendLine($"  Aliasing: {_stats.AliasingBarriers}");
            sb.AppendLine($"  隐式提升跳过: {_stats.SkippedByPromotion}");
            sb.AppendLine($"  自动衰减跳过: {_stats.SkippedByDecay}");
            sb.AppendLine($"  合并屏障: {_stats.MergedBarriers}");
            sb.AppendLine($"  批次数: {_stats.BatchCount}");
            sb.AppendLine($"  峰值待处理: {_stats.PeakPendingBarriers}");
            sb.AppendLine();
            sb.AppendLine("── 上一帧统计 ──");
            sb.AppendLine($"  总屏障数: {_statsLastFrame.TotalBarriers}");
            sb.AppendLine($"  Transition: {_statsLastFrame.TransitionBarriers}");
            sb.AppendLine($"  UAV: {_statsLastFrame.UAVBarriers}");
            sb.AppendLine($"  合并屏障: {_statsLastFrame.MergedBarriers}");
            sb.AppendLine();
            sb.AppendLine("── 资源状态 ──");
            foreach (var kv in _stateMap)
            {
                var entry = kv.Value;
                var name = entry.Resource?.Name ?? "<null>";
                sb.AppendLine($"  {name}: {(entry.UniformState ? entry.CommonState.ToString() : "混合")} " +
                              $"(subs={entry.NumSubresources}, lastFrame={entry.LastUsedFrame})");
            }
            return sb.ToString();
        }

        /// <summary>
        /// 清除所有跟踪状态。慎用，只在重置设备时调用。
        /// </summary>
        public void ClearAll()
        {
            _stateMap.Clear();
            _pendingBarriers.Clear();
            _stats = new BarrierStats();
        }

        // ──────────────────────────────────────────────────────────────────────
        // 辅助类型
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>屏障类型</summary>
        public enum RHIBarrierType
        {
            Transition,
            UAV,
            Aliasing,
        }

        /// <summary>屏障描述</summary>
        public struct RHIBarrier
        {
            public RHIBarrierType Type;
            public RHIResource Resource;
            public RHIResource ResourceBefore; // 仅 Aliasing 用
            public RHIResourceState SourceState;
            public RHIResourceState TargetState;
            public int Subresource; // -1 = all

            public override string ToString()
            {
                return Type switch
                {
                    RHIBarrierType.Transition => $"Transition({Resource?.Name}: {SourceState}->{TargetState}, sub={Subresource})",
                    RHIBarrierType.UAV => $"UAV({Resource?.Name})",
                    RHIBarrierType.Aliasing => $"Aliasing({ResourceBefore?.Name}->{Resource?.Name})",
                    _ => $"Unknown({Type})",
                };
            }
        }

        /// <summary>统计信息</summary>
        public struct BarrierStats
        {
            public int TotalBarriers;
            public int TransitionBarriers;
            public int UAVBarriers;
            public int AliasingBarriers;
            public int SkippedByPromotion;
            public int SkippedByDecay;
            public int MergedBarriers;
            public int BatchCount;
            public int PeakPendingBarriers;
            public int TrackedResources;
        }

        /// <summary>资源状态信息（调试用）</summary>
        public struct ResourceStateInfo
        {
            public RHIResource Resource;
            public bool IsTexture;
            public int NumSubresources;
            public bool UniformState;
            public RHIResourceState CurrentState;
            public RHIResourceState[] SubresourceStates;
            public ulong LastUsedFrame;
        }

        // ──────────────────────────────────────────────────────────────────────
        // 扩展：split barrier 支持
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// split barrier 的前半部分。在命令录制早期发出，告诉 GPU "准备好转换"。
        /// 配合 EndSplitBarrier 完成转换。
        /// split barrier 可以让 GPU 更灵活地调度，减少空闲时间。
        /// </summary>
        public void BeginSplitBarrier(RHIResource resource, RHIResourceState targetState)
        {
            if (!_stateMap.TryGetValue(resource, out var entry))
            {
                Register(resource, RHIResourceState.Common);
                entry = _stateMap[resource];
            }

            var srcState = entry.UniformState ? entry.CommonState : RHIResourceState.Common;
            if (srcState == targetState) return;

            // 发出 begin split barrier
            AddBarrier(new RHIBarrier
            {
                Type = RHIBarrierType.Transition,
                Resource = resource,
                SourceState = srcState,
                TargetState = targetState,
                Subresource = AllSubresources,
            });

            // 注意：split barrier 在 begin 时不会立即更新跟踪状态
            // 要等 EndSplitBarrier 调用后才更新
            // FIXME: 目前没有真正实现 split，只是标记一下
        }

        /// <summary>
        /// split barrier 的后半部分。完成转换并更新跟踪状态。
        /// </summary>
        public void EndSplitBarrier(RHIResource resource, RHIResourceState targetState)
        {
            if (!_stateMap.TryGetValue(resource, out var entry))
                return;

            AddBarrier(new RHIBarrier
            {
                Type = RHIBarrierType.Transition,
                Resource = resource,
                SourceState = RHIResourceState.Common, // split end 用 Common 作为 src
                TargetState = targetState,
                Subresource = AllSubresources,
            });

            // 更新跟踪状态
            entry.ResetTo(targetState);
        }

        // ──────────────────────────────────────────────────────────────────────
        // 扩展：全局重置
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 在设备重置或 swapchain 重建时调用。
        /// 把所有资源状态重置为 Common，因为 GPU 状态已经丢失。
        /// </summary>
        public void OnDeviceReset()
        {
            foreach (var kv in _stateMap)
            {
                kv.Value.ResetTo(RHIResourceState.Common);
            }
            _pendingBarriers.Clear();
            Console.WriteLine($"[RHIBarrierTracker] 设备重置，已重置 {_stateMap.Count} 个资源状态");
        }

        /// <summary>
        /// 清理长时间未使用的资源条目。
        /// </summary>
        /// <param name="maxIdleFrames">最大空闲帧数</param>
        public int CleanupIdle(ulong maxIdleFrames = 300)
        {
            int removed = 0;
            var toRemove = new List<RHIResource>();

            foreach (var kv in _stateMap)
            {
                if (_currentFrame - kv.Value.LastUsedFrame > maxIdleFrames)
                {
                    toRemove.Add(kv.Key);
                }
            }

            foreach (var res in toRemove)
            {
                _stateMap.Remove(res);
                removed++;
            }

            _stats.TrackedResources = _stateMap.Count;
            return removed;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 扩展：屏障批处理器
    // 一个更高级的封装，自动管理 batch 生命周期
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 屏障批处理器：自动管理 BeginBatch/EndBatch 的 scope。
    /// 用 using 语句确保 EndBatch 一定会被调用。
    /// </summary>
    public readonly struct BarrierBatchScope : IDisposable
    {
        private readonly RHIBarrierTracker _tracker;
        private readonly IRHICommandList _cmdList;

        public BarrierBatchScope(RHIBarrierTracker tracker, IRHICommandList cmdList)
        {
            _tracker = tracker;
            _cmdList = cmdList;
            _tracker.BeginBatch();
        }

        public void Dispose()
        {
            _tracker.EndBatch(_cmdList);
        }
    }

    /// <summary>
    /// RHIBarrierTracker 的扩展方法。
    /// </summary>
    public static class BarrierTrackerExtensions
    {
        /// <summary>创建一个 batch scope，用 using 确保自动 EndBatch。</summary>
        public static BarrierBatchScope CreateBatch(this RHIBarrierTracker tracker, IRHICommandList cmdList)
        {
            return new BarrierBatchScope(tracker, cmdList);
        }

        /// <summary>批量转换多个资源到各自的目标状态。</summary>
        public static void TransitionMultiple(this RHIBarrierTracker tracker,
                                               IRHICommandList cmdList,
                                               params (RHIResource resource, RHIResourceState target)[] transitions)
        {
            using var scope = tracker.CreateBatch(cmdList);
            foreach (var (res, target) in transitions)
            {
                tracker.TransitionTo(res, target);
            }
        }

        /// <summary>设置渲染目标前的完整屏障序列。</summary>
        public static void SetupRenderTarget(this RHIBarrierTracker tracker,
                                              IRHICommandList cmdList,
                                              RHIResource[] colorTargets,
                                              RHIResource depthTarget,
                                              bool depthWrite)
        {
            using var scope = tracker.CreateBatch(cmdList);
            foreach (var ct in colorTargets)
            {
                if (ct != null)
                    tracker.TransitionTo(ct, RHIResourceState.RenderTarget);
            }
            if (depthTarget != null)
            {
                tracker.TransitionTo(depthTarget, depthWrite ? RHIResourceState.DepthWrite : RHIResourceState.DepthRead);
            }
        }

        /// <summary>完成渲染后把渲染目标转回可读状态。</summary>
        public static void FinishRenderTarget(this RHIBarrierTracker tracker,
                                               IRHICommandList cmdList,
                                               RHIResource[] colorTargets,
                                               RHIResource depthTarget,
                                               bool readBack)
        {
            using var scope = tracker.CreateBatch(cmdList);
            foreach (var ct in colorTargets)
            {
                if (ct != null)
                {
                    tracker.TransitionTo(ct, readBack ? RHIResourceState.PixelShaderResource : RHIResourceState.Common);
                }
            }
            if (depthTarget != null)
            {
                tracker.TransitionTo(depthTarget, readBack ? RHIResourceState.DepthRead : RHIResourceState.Common);
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 扩展：屏障验证器
    // 在 Debug 模式下验证屏障的正确性
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 屏障验证器：在 Debug 构建中验证资源状态转换的合法性。
    /// 检测常见的错误如：从 RenderTarget 直接转到 DepthWrite 等。
    /// </summary>
    public sealed class BarrierValidator
    {
        private readonly RHIBarrierTracker _tracker;
        private readonly List<string> _warnings = new();
        private readonly List<string> _errors = new();
        private bool _enabled = true;

        /// <summary>是否启用验证</summary>
        public bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        /// <summary>警告列表</summary>
        public IReadOnlyList<string> Warnings => _warnings;

        /// <summary>错误列表</summary>
        public IReadOnlyList<string> Errors => _errors;

        public BarrierValidator(RHIBarrierTracker tracker)
        {
            _tracker = tracker;
        }

        /// <summary>验证一个屏障是否合法。</summary>
        public bool Validate(in RHIBarrierTracker.RHIBarrier barrier)
        {
            if (!_enabled) return true;

            _warnings.Clear();
            _errors.Clear();

            // 检查 transition 的合法性
            if (barrier.Type == RHIBarrierTracker.RHIBarrierType.Transition)
            {
                ValidateTransition(barrier.Resource, barrier.SourceState, barrier.TargetState);
            }

            // 检查 UAV barrier
            if (barrier.Type == RHIBarrierTracker.RHIBarrierType.UAV)
            {
                // UAV barrier 的资源必须在 UAV 状态
                var curState = _tracker.GetState(barrier.Resource);
                if ((curState & RHIResourceState.UnorderedAccess) == 0)
                {
                    _warnings.Add($"UAV barrier 的资源 {barrier.Resource?.Name} 当前不在 UAV 状态 ({curState})");
                }
            }

            return _errors.Count == 0;
        }

        private void ValidateTransition(RHIResource resource, RHIResourceState src, RHIResourceState dst)
        {
            // 从 Present 转出只能转到 Common 或 CopyDest
            if (src == RHIResourceState.Present &&
                dst != RHIResourceState.Common &&
                dst != RHIResourceState.CopyDest &&
                dst != RHIResourceState.RenderTarget)
            {
                _errors.Add($"非法转换: Present -> {dst}。从 Present 只能转到 Common/CopyDest/RenderTarget");
            }

            // 从 CopyDest 转出应该先转到 Common 再转其他
            if (src == RHIResourceState.CopyDest && dst != RHIResourceState.Common)
            {
                _warnings.Add($"建议: 从 CopyDest 转到 {dst} 前先转到 Common，可以利用隐式提升");
            }

            // DepthWrite 和 RenderTarget 互斥
            if (src == RHIResourceState.DepthWrite && dst == RHIResourceState.RenderTarget)
            {
                _errors.Add($"非法转换: DepthWrite -> RenderTarget。深度缓冲不能当 RT 用");
            }
            if (src == RHIResourceState.RenderTarget && dst == RHIResourceState.DepthWrite)
            {
                _errors.Add($"非法转换: RenderTarget -> DepthWrite。RT 不能当深度缓冲用");
            }

            // 检查是否有冲突的读写标志
            bool srcWrite = (src & (RHIResourceState.RenderTarget | RHIResourceState.DepthWrite |
                                     RHIResourceState.UnorderedAccess | RHIResourceState.CopyDest)) != 0;
            bool dstWrite = (dst & (RHIResourceState.RenderTarget | RHIResourceState.DepthWrite |
                                     RHIResourceState.UnorderedAccess | RHIResourceState.CopyDest)) != 0;
            if (srcWrite && dstWrite)
            {
                _warnings.Add($"两个连续的写状态转换: {src} -> {dst}，可能需要 UAV barrier");
            }
        }

        /// <summary>清空所有验证记录。</summary>
        public void Clear()
        {
            _warnings.Clear();
            _errors.Clear();
        }

        /// <summary>获取验证报告。</summary>
        public string GetReport()
        {
            var sb = new System.Text.StringBuilder();
            if (_errors.Count > 0)
            {
                sb.AppendLine("── 屏障验证错误 ──");
                foreach (var e in _errors)
                    sb.AppendLine($"  [ERROR] {e}");
            }
            if (_warnings.Count > 0)
            {
                sb.AppendLine("── 屏障验证警告 ──");
                foreach (var w in _warnings)
                    sb.AppendLine($"  [WARN] {w}");
            }
            if (_errors.Count == 0 && _warnings.Count == 0)
            {
                sb.AppendLine("屏障验证通过，没有问题。");
            }
            return sb.ToString();
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 扩展：屏障历史记录器
    // 记录最近的屏障操作历史，用于性能分析和调试
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 屏障历史记录器：环形缓冲区记录最近的屏障操作。
    /// </summary>
    public sealed class BarrierHistoryLogger
    {
        private readonly RHIBarrierTracker.RHIBarrier[] _ringBuffer;
        private readonly ulong[] _frameNumbers;
        private readonly int _capacity;
        private int _head;
        private int _count;
        private readonly object _lock = new();

        /// <summary>记录容量</summary>
        public int Capacity => _capacity;

        /// <summary>当前记录数</summary>
        public int Count => _count;

        public BarrierHistoryLogger(int capacity = 1024)
        {
            _capacity = capacity;
            _ringBuffer = new RHIBarrierTracker.RHIBarrier[capacity];
            _frameNumbers = new ulong[capacity];
            _head = 0;
            _count = 0;
        }

        /// <summary>记录一个屏障。</summary>
        public void Log(in RHIBarrierTracker.RHIBarrier barrier, ulong frame)
        {
            lock (_lock)
            {
                _ringBuffer[_head] = barrier;
                _frameNumbers[_head] = frame;
                _head = (_head + 1) % _capacity;
                if (_count < _capacity) _count++;
            }
        }

        /// <summary>获取最近的 N 条记录。</summary>
        public List<(RHIBarrierTracker.RHIBarrier barrier, ulong frame)> GetRecent(int n)
        {
            var result = new List<(RHIBarrierTracker.RHIBarrier, ulong)>(n);
            lock (_lock)
            {
                int toTake = Math.Min(n, _count);
                int start = (_head - toTake + _capacity) % _capacity;
                for (int i = 0; i < toTake; i++)
                {
                    int idx = (start + i) % _capacity;
                    result.Add((_ringBuffer[idx], _frameNumbers[idx]));
                }
            }
            return result;
        }

        /// <summary>按资源过滤历史记录。</summary>
        public List<(RHIBarrierTracker.RHIBarrier barrier, ulong frame)> GetByResource(RHIResource resource, int maxResults = 100)
        {
            var result = new List<(RHIBarrierTracker.RHIBarrier, ulong)>();
            lock (_lock)
            {
                int start = (_head - _count + _capacity) % _capacity;
                for (int i = 0; i < _count; i++)
                {
                    int idx = (start + i) % _capacity;
                    if (_ringBuffer[idx].Resource == resource)
                    {
                        result.Add((_ringBuffer[idx], _frameNumbers[idx]));
                        if (result.Count >= maxResults) break;
                    }
                }
            }
            return result;
        }

        /// <summary>清空历史。</summary>
        public void Clear()
        {
            lock (_lock)
            {
                _head = 0;
                _count = 0;
            }
        }

        /// <summary>生成历史报告。</summary>
        public string GenerateReport(int maxEntries = 50)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"═══ 屏障历史记录 (共 {_count} 条，显示最近 {Math.Min(maxEntries, _count)} 条) ═══");

            var recent = GetRecent(maxEntries);
            // 从旧到新显示
            recent.Reverse();
            foreach (var (barrier, frame) in recent)
            {
                sb.AppendLine($"  [Frame {frame}] {barrier}");
            }
            return sb.ToString();
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 扩展：屏障优化分析器
    // 分析屏障模式，给出优化建议
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 屏障优化分析器：分析屏障使用模式，提供优化建议。
    /// </summary>
    public sealed class BarrierOptimizationAnalyzer
    {
        private readonly RHIBarrierTracker _tracker;
        private readonly BarrierHistoryLogger _history;
        private readonly List<OptimizationSuggestion> _suggestions = new();

        /// <summary>优化建议列表</summary>
        public IReadOnlyList<OptimizationSuggestion> Suggestions => _suggestions;

        public BarrierOptimizationAnalyzer(RHIBarrierTracker tracker, BarrierHistoryLogger history)
        {
            _tracker = tracker;
            _history = history;
        }

        /// <summary>执行分析。</summary>
        public void Analyze()
        {
            _suggestions.Clear();

            var stats = _tracker.LastFrameStats;

            // 检查 1: 屏障数量过多
            if (stats.TotalBarriers > 100)
            {
                _suggestions.Add(new OptimizationSuggestion
                {
                    Severity = SuggestionSeverity.Warning,
                    Category = "数量",
                    Message = $"上一帧总屏障数 {stats.TotalBarriers} 偏高。" +
                              "考虑使用批量屏障或减少状态切换。",
                });
            }

            // 检查 2: 隐式提升利用率低
            if (stats.TotalBarriers > 20 && stats.SkippedByPromotion < stats.TotalBarriers / 10)
            {
                _suggestions.Add(new OptimizationSuggestion
                {
                    Severity = SuggestionSeverity.Info,
                    Category = "隐式提升",
                    Message = $"隐式提升跳过率低 ({stats.SkippedByPromotion}/{stats.TotalBarriers})。" +
                              "确保资源在不使用时处于 Common 状态以利用隐式提升。",
                });
            }

            // 检查 3: 合并率低
            if (stats.TransitionBarriers > 30 && stats.MergedBarriers < stats.TransitionBarriers / 5)
            {
                _suggestions.Add(new OptimizationSuggestion
                {
                    Severity = SuggestionSeverity.Info,
                    Category = "合并",
                    Message = $"屏障合并率低 ({stats.MergedBarriers}/{stats.TransitionBarriers})。" +
                              "考虑把相关操作放在同一个 batch 中执行。",
                });
            }

            // 检查 4: UAV 屏障过多
            if (stats.UAVBarriers > 20)
            {
                _suggestions.Add(new OptimizationSuggestion
                {
                    Severity = SuggestionSeverity.Warning,
                    Category = "UAV",
                    Message = $"UAV 屏障数量较多 ({stats.UAVBarriers})。" +
                              "考虑重排 pass 顺序减少 UAV 同步需求。",
                });
            }

            // 检查 5: 分析历史中的重复模式
            AnalyzeHistoryPatterns();
        }

        private void AnalyzeHistoryPatterns()
        {
            var recent = _history.GetRecent(500);
            if (recent.Count < 10) return;

            // 统计每个资源的屏障频率
            var freq = new Dictionary<RHIResource, int>();
            foreach (var (barrier, _) in recent)
            {
                if (barrier.Resource == null) continue;
                if (!freq.ContainsKey(barrier.Resource))
                    freq[barrier.Resource] = 0;
                freq[barrier.Resource]++;
            }

            // 找出屏障频率最高的资源
            foreach (var kv in freq)
            {
                if (kv.Value > 20)
                {
                    _suggestions.Add(new OptimizationSuggestion
                    {
                        Severity = SuggestionSeverity.Warning,
                        Category = "热点资源",
                        Message = $"资源 '{kv.Key?.Name ?? "null"}' 在最近 500 条记录中出现 {kv.Value} 次屏障。" +
                                  "考虑重新设计渲染流程减少状态切换。",
                    });
                }
            }

            // 检查 ping-pong 模式 (A->B->A->B...)
            if (recent.Count > 4)
            {
                int pingPongCount = 0;
                for (int i = 2; i < recent.Count; i++)
                {
                    var b0 = recent[i - 2].barrier;
                    var b2 = recent[i].barrier;
                    if (b0.Resource == b2.Resource &&
                        b0.SourceState == b2.TargetState &&
                        b0.TargetState == b2.SourceState &&
                        b0.Type == RHIBarrierTracker.RHIBarrierType.Transition)
                    {
                        pingPongCount++;
                    }
                }
                if (pingPongCount > 10)
                {
                    _suggestions.Add(new OptimizationSuggestion
                    {
                        Severity = SuggestionSeverity.Warning,
                        Category = "Ping-Pong",
                        Message = $"检测到 {pingPongCount} 次 ping-pong 状态切换。" +
                                  "资源在两个状态间频繁切换，考虑重新组织渲染 pass。",
                    });
                }
            }
        }

        /// <summary>生成分析报告。</summary>
        public string GenerateReport()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("═══ 屏障优化分析报告 ═══");
            sb.AppendLine();

            if (_suggestions.Count == 0)
            {
                sb.AppendLine("没有发现明显的优化点，屏障使用看起来不错。");
                return sb.ToString();
            }

            foreach (var s in _suggestions)
            {
                string icon = s.Severity switch
                {
                    SuggestionSeverity.Error => "[!]",
                    SuggestionSeverity.Warning => "[~]",
                    SuggestionSeverity.Info => "[i]",
                    _ => "[?]",
                };
                sb.AppendLine($"{icon} [{s.Category}] {s.Message}");
            }
            return sb.ToString();
        }
    }

    /// <summary>优化建议</summary>
    public sealed class OptimizationSuggestion
    {
        public SuggestionSeverity Severity;
        public string Category;
        public string Message;

        public override string ToString() => $"[{Severity}] [{Category}] {Message}";
    }

    /// <summary>建议严重程度</summary>
    public enum SuggestionSeverity
    {
        Info,
        Warning,
        Error,
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 接口占位（实际实现在其他文件）
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>RHI 资源接口</summary>

    /// <summary>命令列表接口</summary>
    public partial interface IRHICommandList
    {
        void ResourceBarrier(IReadOnlyList<RHIBarrierTracker.RHIBarrier> barriers);
        void CopyResource(RHIResource src, RHIResource dst);
        void CopyBufferRegion(RHIResource src, ulong srcOffset, RHIResource dst, ulong dstOffset, ulong size);
        void CopyTextureRegion(RHIResource src, int srcSub, RHIResource dst, int dstSub);
        void Draw(uint vertexCount, uint instanceCount = 1);
        void DrawIndexed(uint indexCount, uint instanceCount = 1);
        void Dispatch(uint x, uint y, uint z);
    }
}
