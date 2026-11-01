// ==================================================================================================
//  EcsParticleBridge.cs — yama引擎 ECS ↔ 粒子系统桥接器
// ==================================================================================================
//  命名空间: YamaEngine.Particles
//  依赖:     YamaEngine.ECS
//
//  功能:
//    1. 遍历所有带 ParticleEmitterComponent 的 ECS 实体
//    2. 为每个实体创建/同步 ParticleSystemCore
//    3. 将实体 Transform 同步到粒子系统位置
//    4. 每帧驱动粒子模拟
//    5. 提供 Play/Stop/Emit 快捷 API
// ==================================================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.ECS;

namespace YamaEngine.Particles
{
    /// <summary>
    /// ECS 粒子系统桥接器。将 ECS 实体上的 <see cref="ParticleEmitterComponent"/> 驱动为实际粒子模拟。
    /// </summary>
    public sealed class EcsParticleBridge : IDisposable
    {
        private readonly EntityComponentSystem _ecs;
        private readonly Dictionary<int, ParticleSystemCore> _systems = new();

        public event Action<int, string>? OnParticleError;

        public EcsParticleBridge(EntityComponentSystem ecs)
        {
            _ecs = ecs ?? throw new ArgumentNullException(nameof(ecs));
        }

        public void Dispose()
        {
            _systems.Clear();
        }

        /// <summary>更新所有 ECS 粒子实体。应在每帧渲染前调用。</summary>
        public void Update(float deltaTime, Vector3 cameraPosition)
        {
            foreach (int entityId in _ecs.AllEntities)
            {
                if (!_ecs.HasComponent<ParticleEmitterComponent>(entityId))
                    continue;

                try
                {
                    UpdateEntity(entityId, deltaTime, cameraPosition);
                }
                catch (Exception ex)
                {
                    OnParticleError?.Invoke(entityId, ex.Message);
                }
            }
        }

        private void UpdateEntity(int entityId, float deltaTime, Vector3 cameraPosition)
        {
            var emitter = _ecs.GetComponent<ParticleEmitterComponent>(entityId);
            if (!emitter.Enabled) return;

            // 获取或创建 ParticleSystemCore
            if (!_systems.TryGetValue(entityId, out var system))
            {
                system = CreateSystemFromComponent(emitter);
                _systems[entityId] = system;

                // 将引用写回组件
                emitter.RuntimeSystem = system;
                _ecs.SetComponent(entityId, emitter);
            }

            // 同步实体位置
            if (_ecs.HasComponent<TransformComponent>(entityId))
            {
                var transform = _ecs.GetComponent<TransformComponent>(entityId);
                system.SetPosition(transform.Position);
            }

            // 同步组件变更
            SyncComponentToSystem(ref emitter, system);

            // 驱动模拟
            system.Update(deltaTime, cameraPosition);
        }

        private static ParticleSystemCore CreateSystemFromComponent(ParticleEmitterComponent emitter)
        {
            var system = new ParticleSystemCore
            {
                MaxParticles = emitter.MaxParticles,
                Looping = emitter.Looping,
                SimulationSpace = emitter.SimulationSpace,
                Enabled = emitter.Enabled,
            };

            system.Emission.RateOverTime = emitter.EmissionRate;
            system.LifetimeModule.Lifetime = new MinMaxCurve(emitter.Lifetime);
            system.SizeModule.SizeX = new MinMaxCurve(emitter.StartSize);
            system.SizeModule.SizeY = new MinMaxCurve(emitter.StartSize);
            system.ColorModule.StartColor = emitter.StartColor;
            system.ForceModule.Y = new MinMaxCurve(emitter.Gravity);
            system.VelocityOverLifetime.Y = new MinMaxCurve(emitter.StartSpeed);
            system.Shape.Radius = emitter.ShapeRadius;
            system.Shape.Shape = (ShapeModule.ShapeType)emitter.ShapeType;

            system.Play();
            return system;
        }

        private static void SyncComponentToSystem(ref ParticleEmitterComponent emitter, ParticleSystemCore system)
        {
            system.Enabled = emitter.Enabled;
            system.Looping = emitter.Looping;
            system.SimulationSpace = emitter.SimulationSpace;

            if (system.Emission.RateOverTime != emitter.EmissionRate)
                system.Emission.RateOverTime = emitter.EmissionRate;

            // 注意：其他参数变更在重新 Play 时才生效，运行时变更需要特殊处理
        }

        /// <summary>获取指定实体的粒子系统（如果存在）。</summary>
        public ParticleSystemCore? GetSystem(int entityId)
        {
            _systems.TryGetValue(entityId, out var system);
            return system;
        }

        /// <summary>获取所有粒子的活跃索引（用于渲染）。</summary>
        public IReadOnlyList<Particle> GetParticles(int entityId)
        {
            if (_systems.TryGetValue(entityId, out var system))
                return system.GetParticles();
            return Array.Empty<Particle>();
        }

        /// <summary>播放指定实体的粒子系统。</summary>
        public void Play(int entityId)
        {
            if (_systems.TryGetValue(entityId, out var system))
                system.Play();
        }

        /// <summary>停止指定实体的粒子系统。</summary>
        public void Stop(int entityId, bool clear = false)
        {
            if (_systems.TryGetValue(entityId, out var system))
                system.Stop(clear);
        }

        /// <summary>暂停指定实体的粒子系统。</summary>
        public void Pause(int entityId)
        {
            if (_systems.TryGetValue(entityId, out var system))
                system.Pause();
        }

        /// <summary>清除指定实体的所有粒子。</summary>
        public void Clear(int entityId)
        {
            if (_systems.TryGetValue(entityId, out var system))
                system.Clear();
        }

        /// <summary>强制发射 N 个粒子。</summary>
        public void Emit(int entityId, int count)
        {
            if (_systems.TryGetValue(entityId, out var system))
                system.Emit(count);
        }

        /// <summary>清理已销毁实体的粒子系统。</summary>
        public void Cleanup()
        {
            var toRemove = new List<int>();
            foreach (var kv in _systems)
            {
                if (!_ecs.HasEntity(kv.Key))
                    toRemove.Add(kv.Key);
            }
            foreach (var id in toRemove)
                _systems.Remove(id);
        }
    }
}
