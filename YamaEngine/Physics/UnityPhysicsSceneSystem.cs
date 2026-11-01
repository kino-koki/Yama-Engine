using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.Physics
{
    // ============================================================================================
    //  UnityPhysicsSceneSystem.cs
    //
    //  物理场景系统 — 类似 Unity PhysicsScene，支持多场景物理。
    //
    //  每个场景拥有独立的物理世界（PhysicsWorld 实例），可独立配置重力、子步数、容差等参数。
    //  系统每帧遍历所有场景并步进模拟。支持场景间碰撞过滤和场景暂停/恢复/速率控制。
    // ============================================================================================

    /// <summary>
    ///   物理场景配置。定义每个场景的物理参数。
    /// </summary>
    public struct PhysicsSceneConfig
    {
        /// <summary>重力向量 (m/s^2)。默认 (0, -9.81, 0)。</summary>
        public Vector3 Gravity;

        /// <summary>固定时间步长 (秒)。默认 1/60。</summary>
        public float FixedTimestep;

        /// <summary>最大子步数（防止死亡螺旋）。默认 8。</summary>
        public int MaxSubSteps;

        /// <summary>碰撞检测子步数。默认 2。</summary>
        public int CollisionSubSteps;

        /// <summary>Baumgarte 位置矫正系数 (0..1)。默认 0.2。</summary>
        public float Baumgarte;

        /// <summary>穿透容差 (米)。默认 0.02。</summary>
        public float PenetrationSlop;

        /// <summary>最大刚体数。默认 16384。</summary>
        public int MaxBodies;

        /// <summary>是否启用确定性模拟。默认 true。</summary>
        public bool Deterministic;

        /// <summary>模拟速率倍率 (0=暂停, 0.5=半速, 1=正常, 2=双倍速度)。默认 1.0。</summary>
        public float SimulateSpeed;

        /// <summary>默认配置。</summary>
        public static PhysicsSceneConfig Default => new()
        {
            Gravity = new Vector3(0, -9.81f, 0),
            FixedTimestep = 1f / 60f,
            MaxSubSteps = 8,
            CollisionSubSteps = 2,
            Baumgarte = 0.2f,
            PenetrationSlop = 0.02f,
            MaxBodies = 16384,
            Deterministic = true,
            SimulateSpeed = 1.0f,
        };
    }

    /// <summary>
    ///   物理场景句柄。标识一个独立物理场景。
    /// </summary>
    public readonly struct PhysicsSceneHandle : IEquatable<PhysicsSceneHandle>
    {
        public readonly int Value;

        public PhysicsSceneHandle(int value) { Value = value; }
        public bool Equals(PhysicsSceneHandle o) => Value == o.Value;
        public override bool Equals(object? obj) => obj is PhysicsSceneHandle h && Equals(h);
        public override int GetHashCode() => Value.GetHashCode();
        public static bool operator ==(PhysicsSceneHandle a, PhysicsSceneHandle b) => a.Value == b.Value;
        public static bool operator !=(PhysicsSceneHandle a, PhysicsSceneHandle b) => a.Value != b.Value;
        public bool IsValid => Value >= 0;
    }

    /// <summary>
    ///   物理场景实例。封装一个 PhysicsWorld 及其配置。
    /// </summary>
    public sealed class PhysicsSceneInstance : IDisposable
    {
        private readonly PhysicsWorldReal _world;
        private PhysicsSceneConfig _config;
        private bool _isPaused;
        private float _accumulator;
        private bool _disposed;

        /// <summary>场景句柄。</summary>
        public PhysicsSceneHandle Handle { get; }

        /// <summary>底层 PhysicsWorld 实例。</summary>
        public PhysicsWorldReal World => _world;

        /// <summary>当前配置（只读副本）。</summary>
        public PhysicsSceneConfig Config => _config;

        /// <summary>场景是否暂停。</summary>
        public bool IsPaused
        {
            get => _isPaused;
            set
            {
                _isPaused = value;
                _world.IsPaused = value;
                if (value) _accumulator = 0f;
            }
        }

        /// <summary>模拟速率倍率。</summary>
        public float SimulateSpeed
        {
            get => _config.SimulateSpeed;
            set => _config.SimulateSpeed = Math.Max(0f, value);
        }

        /// <summary>重力向量。</summary>
        public Vector3 Gravity
        {
            get => _config.Gravity;
            set
            {
                _config.Gravity = value;
                _world.Gravity = value;
            }
        }

        /// <summary>场景中刚体总数。</summary>
        public int BodyCount => _world.BodyCount;

        /// <summary>场景中活跃（未休眠）刚体数。</summary>
        public int ActiveBodyCount => _world.ActiveBodyCount;

        internal PhysicsSceneInstance(PhysicsSceneHandle handle, PhysicsWorldReal world, PhysicsSceneConfig config)
        {
            Handle = handle;
            _world = world;
            _config = config;
            _isPaused = false;
            _accumulator = 0f;
        }

        /// <summary>
        ///   步进物理模拟。使用配置的固定时间步长和速率。
        /// </summary>
        /// <param name="deltaTime">帧间隔（秒）。</param>
        /// <returns>实际执行的固定步数。</returns>
        public int Step(float deltaTime)
        {
            if (_disposed || _isPaused || deltaTime <= 0f)
                return 0;

            float scaledDt = deltaTime * _config.SimulateSpeed;
            _accumulator += scaledDt;
            int stepsTaken = 0;

            while (_accumulator >= _config.FixedTimestep && stepsTaken < _config.MaxSubSteps)
            {
                // 直接步进原生系统
                JoltNative.SJ_PhysicsSystem_Step(
                    _world.NativeHandle,
                    _config.FixedTimestep,
                    _config.CollisionSubSteps);

                _accumulator -= _config.FixedTimestep;
                stepsTaken++;
            }

            // 防止死亡螺旋
            if (_accumulator > _config.FixedTimestep * _config.MaxSubSteps)
                _accumulator = 0f;

            return stepsTaken;
        }

        /// <summary>
        ///   更新配置（运行时修改物理参数）。
        /// </summary>
        public void ApplyConfig(in PhysicsSceneConfig config)
        {
            _config = config;
            _world.Gravity = config.Gravity;
        }

        /// <summary>
        ///   重置场景（移除所有刚体和约束）。
        /// </summary>
        public void Clear()
        {
            _world.Clear();
            _accumulator = 0f;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _world.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    ///   场景间碰撞过滤配置。管理多场景之间的碰撞关系。
    ///   默认场景间互不碰撞，可配置为允许特定场景对之间的碰撞。
    /// </summary>
    public sealed class SceneCollisionFilter
    {
        // 场景间碰撞掩码矩阵：filter[sceneA, sceneB] = true 表示允许碰撞
        private readonly HashSet<ulong> _allowedPairs = new();

        /// <summary>
        ///   允许两个场景之间的碰撞。
        /// </summary>
        public void AllowCollision(PhysicsSceneHandle a, PhysicsSceneHandle b)
        {
            ulong key = MakePairKey(a, b);
            _allowedPairs.Add(key);
        }

        /// <summary>
        ///   禁止两个场景之间的碰撞。
        /// </summary>
        public void DisallowCollision(PhysicsSceneHandle a, PhysicsSceneHandle b)
        {
            ulong key = MakePairKey(a, b);
            _allowedPairs.Remove(key);
        }

        /// <summary>
        ///   检查两个场景之间是否允许碰撞。
        /// </summary>
        public bool CanCollide(PhysicsSceneHandle a, PhysicsSceneHandle b)
        {
            if (a.Value == b.Value) return true; // 同一场景内正常碰撞
            ulong key = MakePairKey(a, b);
            return _allowedPairs.Contains(key);
        }

        /// <summary>清空所有场景间碰撞配置。</summary>
        public void Clear() => _allowedPairs.Clear();

        private static ulong MakePairKey(PhysicsSceneHandle a, PhysicsSceneHandle b)
        {
            int lo = Math.Min(a.Value, b.Value);
            int hi = Math.Max(a.Value, b.Value);
            return ((ulong)hi << 32) | (uint)lo;
        }
    }

    /// <summary>
    ///   多物理场景管理器。管理所有 PhysicsSceneInstance，每帧统一步进。
    ///   类似 Unity 中同时加载多个 PhysicsScene 的能力。
    /// </summary>
    public sealed class PhysicsSceneManager : IDisposable
    {
        private readonly List<PhysicsSceneInstance> _scenes = new();
        private int _nextHandleId;
        private bool _disposed;

        /// <summary>场景间碰撞过滤器。</summary>
        public SceneCollisionFilter SceneFilter { get; } = new();

        /// <summary>当前管理的场景数量。</summary>
        public int SceneCount => _scenes.Count;

        /// <summary>
        ///   创建一个新的物理场景。
        /// </summary>
        /// <param name="config">场景配置。默认使用 PhysicsSceneConfig.Default。</param>
        /// <returns>新场景的句柄。</returns>
        public PhysicsSceneHandle CreateScene(PhysicsSceneConfig? config = null)
        {
            var cfg = config ?? PhysicsSceneConfig.Default;

            var world = new PhysicsWorldReal(PhysicsWorldConfig.Default);
            var handle = new PhysicsSceneHandle(_nextHandleId++);
            var instance = new PhysicsSceneInstance(handle, world, cfg);
            _scenes.Add(instance);
            return handle;
        }

        /// <summary>
        ///   获取场景实例。
        /// </summary>
        public PhysicsSceneInstance? GetScene(PhysicsSceneHandle handle)
        {
            for (int i = 0; i < _scenes.Count; i++)
            {
                if (_scenes[i].Handle == handle)
                    return _scenes[i];
            }
            return null;
        }

        /// <summary>
        ///   销毁场景。
        /// </summary>
        public void DestroyScene(PhysicsSceneHandle handle)
        {
            for (int i = _scenes.Count - 1; i >= 0; i--)
            {
                if (_scenes[i].Handle == handle)
                {
                    _scenes[i].Dispose();
                    _scenes.RemoveAt(i);
                    return;
                }
            }
        }

        /// <summary>
        ///   步进所有场景。
        /// </summary>
        /// <param name="deltaTime">帧间隔（秒）。</param>
        public void StepAll(float deltaTime)
        {
            for (int i = 0; i < _scenes.Count; i++)
            {
                _scenes[i].Step(deltaTime);
            }
        }

        /// <summary>
        ///   步进指定场景。
        /// </summary>
        public void StepScene(PhysicsSceneHandle handle, float deltaTime)
        {
            var scene = GetScene(handle);
            scene?.Step(deltaTime);
        }

        /// <summary>
        ///   暂停指定场景。
        /// </summary>
        public void PauseScene(PhysicsSceneHandle handle)
        {
            var scene = GetScene(handle);
            if (scene != null) scene.IsPaused = true;
        }

        /// <summary>
        ///   恢复指定场景。
        /// </summary>
        public void ResumeScene(PhysicsSceneHandle handle)
        {
            var scene = GetScene(handle);
            if (scene != null) scene.IsPaused = false;
        }

        /// <summary>
        ///   设置场景模拟速率。
        /// </summary>
        public void SetSceneSpeed(PhysicsSceneHandle handle, float speed)
        {
            var scene = GetScene(handle);
            if (scene != null) scene.SimulateSpeed = speed;
        }

        /// <summary>
        ///   清空所有场景。
        /// </summary>
        public void ClearAll()
        {
            foreach (var scene in _scenes)
                scene.Clear();
        }

        /// <summary>
        ///   销毁所有场景。
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var scene in _scenes)
                scene.Dispose();
            _scenes.Clear();
            GC.SuppressFinalize(this);
        }
    }
}
