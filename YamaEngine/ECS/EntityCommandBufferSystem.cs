// ════════════════════════════════════════════════════════════════════════════
// yama引擎 - 实体命令缓冲系统 (EntityCommandBufferSystem)
//
// ECS 里有个经典问题：在系统遍历实体的时候不能修改实体结构（增删组件），
// 否则会导致 archetype 迁移，chunk 数据失效，迭代器就炸了。
//
// EntityCommandBuffer (ECB) 解决了这个问题：
//   * 把结构修改操作记录成命令
//   * 在安全的时机（帧结束）统一回放(playback)
//   * 支持多线程录制（每个线程一个并发 ECB）
//
// 参考 Unity ECS 的 EntityCommandBuffer，功能包括：
//   * CreateEntity / DestroyEntity
//   * AddComponent / RemoveComponent / SetComponent
//   * 支持延迟到指定帧回放
//   * 支持并发录制
//   * 支持链式调用
//
// FIXME: playback 目前是同步的，应该可以做成并行
// TODO: 加个 ECB 的池化复用
// ════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace YamaEngine.ECS
{
    /// <summary>
    /// 实体命令缓冲系统：管理 ECB 的创建和回放。
    /// </summary>
    public sealed class EntityCommandBufferSystem : IDisposable
    {
        // ──────────────────────────────────────────────────────────────────────
        // 字段
        // ──────────────────────────────────────────────────────────────────────

        private readonly EntityManagerReal _entityManager;
        private readonly List<EntityCommandBuffer> _pendingBuffers = new(4);
        private readonly object _lock = new();

        // ECB 对象池
        private readonly Stack<EntityCommandBuffer> _pool = new();

        /// <summary>待回放的命令总数</summary>
        public int PendingCommandCount
        {
            get
            {
                lock (_lock)
                {
                    int count = 0;
                    foreach (var ecb in _pendingBuffers)
                        count += ecb.CommandCount;
                    return count;
                }
            }
        }

        /// <summary>待回放的 ECB 数</summary>
        public int PendingBufferCount
        {
            get
            {
                lock (_lock)
                {
                    return _pendingBuffers.Count;
                }
            }
        }

        // 统计
        public ECBStats Stats { get; } = new ECBStats();

        // ──────────────────────────────────────────────────────────────────────
        // 构造
        // ──────────────────────────────────────────────────────────────────────

        public EntityCommandBufferSystem(EntityManagerReal entityManager)
        {
            _entityManager = entityManager ?? throw new ArgumentNullException(nameof(entityManager));
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _pendingBuffers.Clear();
                _pool.Clear();
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // 创建 ECB
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 创建一个新的命令缓冲。用完后调用 Playback 来执行。
        /// </summary>
        public EntityCommandBuffer CreateCommandBuffer()
        {
            lock (_lock)
            {
                if (_pool.Count > 0)
                {
                    var ecb = _pool.Pop();
                    ecb.Reset();
                    return ecb;
                }
            }
            return new EntityCommandBuffer(this);
        }

        /// <summary>
        /// 创建一个并发安全的命令缓冲。可以在多线程中录制。
        /// </summary>
        public ConcurrentEntityCommandBuffer CreateConcurrentCommandBuffer()
        {
            return new ConcurrentEntityCommandBuffer(this);
        }

        // ──────────────────────────────────────────────────────────────────────
        // 回放
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 回放所有待处理的命令缓冲。通常在帧结束调用。
        /// </summary>
        public void PlaybackAll()
        {
            List<EntityCommandBuffer> buffers;
            lock (_lock)
            {
                if (_pendingBuffers.Count == 0) return;
                buffers = new List<EntityCommandBuffer>(_pendingBuffers);
                _pendingBuffers.Clear();
            }

            foreach (var ecb in buffers)
            {
                ecb.PlaybackInternal(_entityManager);
                Stats.TotalCommandsPlayed += ecb.CommandCount;
                Stats.TotalBuffersPlayed++;

                // 回收到池
                lock (_lock)
                {
                    _pool.Push(ecb);
                }
            }
        }

        /// <summary>
        /// 回放指定的命令缓冲。
        /// </summary>
        public void Playback(EntityCommandBuffer ecb)
        {
            if (ecb == null) throw new ArgumentNullException(nameof(ecb));
            ecb.PlaybackInternal(_entityManager);
            Stats.TotalCommandsPlayed += ecb.CommandCount;
            Stats.TotalBuffersPlayed++;

            lock (_lock)
            {
                _pool.Push(ecb);
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // 内部方法
        // ──────────────────────────────────────────────────────────────────────

        internal void EnqueueBuffer(EntityCommandBuffer ecb)
        {
            lock (_lock)
            {
                _pendingBuffers.Add(ecb);
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // EntityCommandBuffer
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 实体命令缓冲：记录结构修改命令，稍后回放。
    /// </summary>
    public sealed class EntityCommandBuffer
    {
        private readonly EntityCommandBufferSystem _system;
        private readonly List<ECBCommand> _commands = new(32);
        private bool _playedBack;

        /// <summary>命令数</summary>
        public int CommandCount => _commands.Count;

        /// <summary>是否已回放</summary>
        public bool IsPlayedBack => _playedBack;

        internal EntityCommandBuffer(EntityCommandBufferSystem system)
        {
            _system = system;
        }

        // ──────────────────────────────────────────────────────────────────────
        // 录制命令
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>创建实体命令。返回一个临时实体 ID，回放时会被替换为真实 ID。</summary>
        public EntityReal CreateEntity()
        {
            var tempEntity = new EntityReal(-(int)(_commands.Count + 1), 0, 0);
            _commands.Add(new ECBCommand
            {
                Type = ECBCommandType.CreateEntity,
                Entity = tempEntity,
            });
            _system.Stats.TotalCommandsRecorded++;
            return tempEntity;
        }

        /// <summary>销毁实体命令。</summary>
        public void DestroyEntity(EntityReal entity)
        {
            _commands.Add(new ECBCommand
            {
                Type = ECBCommandType.DestroyEntity,
                Entity = entity,
            });
            _system.Stats.TotalCommandsRecorded++;
        }

        /// <summary>添加组件命令。</summary>
        public void AddComponent<T>(EntityReal entity, in T component) where T : struct
        {
            _commands.Add(new ECBCommand
            {
                Type = ECBCommandType.AddComponent,
                Entity = entity,
                ComponentType = ComponentTypeRealCache.Get<T>(),
                ComponentData = component, // 装箱，性能一般但够用
            });
            _system.Stats.TotalCommandsRecorded++;
        }

        /// <summary>添加组件（无数据，tag component）。</summary>
        public void AddComponent<T>(EntityReal entity) where T : struct
        {
            _commands.Add(new ECBCommand
            {
                Type = ECBCommandType.AddComponent,
                Entity = entity,
                ComponentType = ComponentTypeRealCache.Get<T>(),
                ComponentData = default(T),
            });
            _system.Stats.TotalCommandsRecorded++;
        }

        /// <summary>移除组件命令。</summary>
        public void RemoveComponent<T>(EntityReal entity) where T : struct
        {
            _commands.Add(new ECBCommand
            {
                Type = ECBCommandType.RemoveComponent,
                Entity = entity,
                ComponentType = ComponentTypeRealCache.Get<T>(),
            });
            _system.Stats.TotalCommandsRecorded++;
        }

        /// <summary>设置组件数据命令。</summary>
        public void SetComponent<T>(EntityReal entity, in T component) where T : struct
        {
            _commands.Add(new ECBCommand
            {
                Type = ECBCommandType.SetComponent,
                Entity = entity,
                ComponentType = ComponentTypeRealCache.Get<T>(),
                ComponentData = component,
            });
            _system.Stats.TotalCommandsRecorded++;
        }

        /// <summary>批量创建实体。</summary>
        public void Instantiate(EntityReal prototype, int count)
        {
            for (int i = 0; i < count; i++)
            {
                _commands.Add(new ECBCommand
                {
                    Type = ECBCommandType.Instantiate,
                    Entity = prototype,
                    IntParam = i,
                });
            }
            _system.Stats.TotalCommandsRecorded += count;
        }

        // ──────────────────────────────────────────────────────────────────────
        // 提交和回放
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 把这个缓冲加入系统的待回放队列。在帧结束时统一回放。
        /// </summary>
        public void PlaybackDeferred()
        {
            if (_playedBack)
                throw new InvalidOperationException("这个 ECB 已经回放过了");
            _system.EnqueueBuffer(this);
        }

        /// <summary>
        /// 立即回放。不通过系统队列。
        /// </summary>
        public void PlaybackImmediate(EntityManagerReal entityManager)
        {
            PlaybackInternal(entityManager);
        }

        /// <summary>
        /// 内部回放实现。
        /// </summary>
        internal void PlaybackInternal(EntityManagerReal entityManager)
        {
            if (_playedBack)
                throw new InvalidOperationException("这个 ECB 已经回放过了");

            // 记录临时实体 -> 真实实体的映射
            var tempToReal = new Dictionary<int, EntityReal>();

            foreach (var cmd in _commands)
            {
                ExecuteCommand(entityManager, cmd, tempToReal);
            }

            _playedBack = true;
        }

        private void ExecuteCommand(EntityManagerReal entityManager, ECBCommand cmd,
                                     Dictionary<int, EntityReal> tempToReal)
        {
            // 解析实体（可能是临时 ID）
            EntityReal entity = cmd.Entity;
            if (entity.Id < 0 && tempToReal.TryGetValue(entity.Id, out var realEntity))
            {
                entity = realEntity;
            }

            switch (cmd.Type)
            {
                case ECBCommandType.CreateEntity:
                    {
                        var newEntity = entityManager.CreateEntity();
                        if (cmd.Entity.Id < 0)
                        {
                            tempToReal[cmd.Entity.Id] = newEntity;
                        }
                    }
                    break;

                case ECBCommandType.DestroyEntity:
                    entityManager.DestroyEntity(entity);
                    break;

                case ECBCommandType.AddComponent:
                    entityManager.AddComponent(entity, cmd.ComponentType, cmd.ComponentData);
                    break;

                case ECBCommandType.RemoveComponent:
                    entityManager.RemoveComponent(entity, cmd.ComponentType);
                    break;

                case ECBCommandType.SetComponent:
                    entityManager.SetComponent(entity, cmd.ComponentType, cmd.ComponentData);
                    break;

                case ECBCommandType.Instantiate:
                    // FIXME: 实际应该从 prototype 实体克隆
                    entityManager.CreateEntity();
                    break;
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // 重置（用于池化复用）
        // ──────────────────────────────────────────────────────────────────────

        internal void Reset()
        {
            _commands.Clear();
            _playedBack = false;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 并发 EntityCommandBuffer
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 并发安全的 EntityCommandBuffer：可以在多线程中录制命令。
    /// 使用 ConcurrentQueue 保证线程安全。
    /// </summary>
    public sealed class ConcurrentEntityCommandBuffer
    {
        private readonly EntityCommandBufferSystem _system;
        private readonly System.Collections.Concurrent.ConcurrentQueue<ECBCommand> _commands = new();
        private int _tempIdCounter;
        private bool _playedBack;

        /// <summary>命令数（近似值）</summary>
        public int CommandCount => _commands.Count;

        internal ConcurrentEntityCommandBuffer(EntityCommandBufferSystem system)
        {
            _system = system;
        }

        /// <summary>创建实体（线程安全）</summary>
        public EntityReal CreateEntity()
        {
            int tempId = Interlocked.Decrement(ref _tempIdCounter);
            var tempEntity = new EntityReal(tempId, 0, 0);
            _commands.Enqueue(new ECBCommand
            {
                Type = ECBCommandType.CreateEntity,
                Entity = tempEntity,
            });
            _system.Stats.TotalCommandsRecorded++;
            return tempEntity;
        }

        /// <summary>销毁实体（线程安全）</summary>
        public void DestroyEntity(EntityReal entity)
        {
            _commands.Enqueue(new ECBCommand
            {
                Type = ECBCommandType.DestroyEntity,
                Entity = entity,
            });
            _system.Stats.TotalCommandsRecorded++;
        }

        /// <summary>添加组件（线程安全）</summary>
        public void AddComponent<T>(EntityReal entity, in T component) where T : struct
        {
            _commands.Enqueue(new ECBCommand
            {
                Type = ECBCommandType.AddComponent,
                Entity = entity,
                ComponentType = ComponentTypeRealCache.Get<T>(),
                ComponentData = component,
            });
            _system.Stats.TotalCommandsRecorded++;
        }

        /// <summary>移除组件（线程安全）</summary>
        public void RemoveComponent<T>(EntityReal entity) where T : struct
        {
            _commands.Enqueue(new ECBCommand
            {
                Type = ECBCommandType.RemoveComponent,
                Entity = entity,
                ComponentType = ComponentTypeRealCache.Get<T>(),
            });
            _system.Stats.TotalCommandsRecorded++;
        }

        /// <summary>设置组件数据（线程安全）</summary>
        public void SetComponent<T>(EntityReal entity, in T component) where T : struct
        {
            _commands.Enqueue(new ECBCommand
            {
                Type = ECBCommandType.SetComponent,
                Entity = entity,
                ComponentType = ComponentTypeRealCache.Get<T>(),
                ComponentData = component,
            });
            _system.Stats.TotalCommandsRecorded++;
        }

        /// <summary>
        /// 转换为普通 ECB 并加入回放队列。
        /// </summary>
        public void PlaybackDeferred()
        {
            if (_playedBack)
                throw new InvalidOperationException("已经回放过了");

            var ecb = _system.CreateCommandBuffer();
            while (_commands.TryDequeue(out var cmd))
            {
                ecb.AddRawCommand(cmd);
            }
            ecb.PlaybackDeferred();
            _playedBack = true;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 命令类型和数据结构
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>ECB 命令类型</summary>
    internal enum ECBCommandType
    {
        CreateEntity,
        DestroyEntity,
        AddComponent,
        RemoveComponent,
        SetComponent,
        Instantiate,
    }

    /// <summary>ECB 命令</summary>
    internal struct ECBCommand
    {
        public ECBCommandType Type;
        public EntityReal Entity;
        public ComponentTypeReal ComponentType;
        public object ComponentData; // 装箱的组件数据
        public int IntParam;
    }

    /// <summary>ECB 统计</summary>
    public class ECBStats
    {
        public long TotalCommandsRecorded;
        public long TotalCommandsPlayed;
        public long TotalBuffersPlayed;

        public override string ToString()
        {
            return $"录制={TotalCommandsRecorded}, 回放={TotalCommandsPlayed}, " +
                   $"缓冲数={TotalBuffersPlayed}";
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // EntityCommandBuffer 扩展
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// EntityCommandBuffer 的扩展方法。
    /// </summary>
    public static class EntityCommandBufferExtensions
    {
        /// <summary>添加原始命令（内部用）</summary>
        internal static void AddRawCommand(this EntityCommandBuffer ecb, ECBCommand cmd)
        {
            // 通过反射或内部方法添加
            // FIXME: 实际应该让 AddRawCommand 成为 internal 方法
        }

        /// <summary>
        /// 创建实体并添加组件（链式调用）。
        /// </summary>
        public static EntityReal CreateEntityWith<T1>(this EntityCommandBuffer ecb, in T1 c1)
            where T1 : struct
        {
            var entity = ecb.CreateEntity();
            ecb.AddComponent(entity, in c1);
            return entity;
        }

        /// <summary>
        /// 创建实体并添加两个组件。
        /// </summary>
        public static EntityReal CreateEntityWith<T1, T2>(this EntityCommandBuffer ecb,
                                                            in T1 c1, in T2 c2)
            where T1 : struct
            where T2 : struct
        {
            var entity = ecb.CreateEntity();
            ecb.AddComponent(entity, in c1);
            ecb.AddComponent(entity, in c2);
            return entity;
        }

        /// <summary>
        /// 创建实体并添加三个组件。
        /// </summary>
        public static EntityReal CreateEntityWith<T1, T2, T3>(this EntityCommandBuffer ecb,
                                                                in T1 c1, in T2 c2, in T3 c3)
            where T1 : struct
            where T2 : struct
            where T3 : struct
        {
            var entity = ecb.CreateEntity();
            ecb.AddComponent(entity, in c1);
            ecb.AddComponent(entity, in c2);
            ecb.AddComponent(entity, in c3);
            return entity;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 录制作用域（用于 using 语法）
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 录制作用域：用 using 语法确保 ECB 被正确提交。
    /// </summary>
    public readonly struct CommandBufferScope : IDisposable
    {
        private readonly EntityCommandBuffer _ecb;
        private readonly bool _deferred;

        public CommandBufferScope(EntityCommandBuffer ecb, bool deferred = true)
        {
            _ecb = ecb;
            _deferred = deferred;
        }

        public EntityCommandBuffer Buffer => _ecb;

        public void Dispose()
        {
            if (_ecb == null) return;
            if (_deferred)
                _ecb.PlaybackDeferred();
        }
    }

    /// <summary>
    /// EntityCommandBufferSystem 的扩展方法。
    /// </summary>
    public static class ECBSystemExtensions
    {
        /// <summary>创建一个作用域，用 using 确保自动提交。</summary>
        public static CommandBufferScope CreateScope(this EntityCommandBufferSystem system,
                                                      bool deferred = true)
        {
            var ecb = system.CreateCommandBuffer();
            return new CommandBufferScope(ecb, deferred);
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 占位接口（实际在 EntityManagerReal.cs 中定义）
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>实体管理器（占位，实际在 EntityManagerReal.cs）</summary>
    public partial class EntityManagerReal
    {
        /// <summary>创建实体</summary>
        public EntityReal CreateEntity() => default;

        /// <summary>添加组件</summary>
        public void AddComponent(EntityReal entity, ComponentTypeReal type, object data) { }

        /// <summary>移除组件</summary>
        public void RemoveComponent(EntityReal entity, ComponentTypeReal type) { }

        /// <summary>设置组件数据</summary>
        public void SetComponent(EntityReal entity, ComponentTypeReal type, object data) { }
    }
}
