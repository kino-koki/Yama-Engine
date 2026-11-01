// =============================================================================
//  Yama Engine - ParticleDemo.cs
//  yama引擎 - 演示粒子效果工厂
//
//  提供火焰、烟雾、爆炸、魔法四种预配置 FlowVFX 粒子系统，
//  可直接加入场景用于测试 GPU 粒子渲染管线。
// =============================================================================
using System;
using System.Numerics;
using YamaEngine.Particles;

namespace YamaEngine.VFX
{
    /// <summary>
    /// 演示粒子效果工厂。
    /// </summary>
    public static class ParticleDemo
    {
        /// <summary>
        /// 创建火焰粒子效果（橙色/红色，向上飘动，尺寸随生命周期缩小）。
        /// </summary>
        public static FlowVFXParticleSimulationReal CreateFireEffect(Vector3 position, int maxParticles = 2048)
        {
            var sim = new FlowVFXParticleSimulationReal
            {
                MaxParticles = maxParticles,
                FixedDt = 1f / 60f,
                MaxSubSteps = 4
            };

            var emitter = new FlowVFXEmitterReal
            {
                Position = position,
                SpawnMode = RealSpawnMode.Rate,
                SpawnRate = 120f,
                LoopDuration = 10f,
                MaxLoops = -1,
                SimulationSpace = RealSimSpace.World,
                Shape = new PointShape(),
                DirectionMode = SpawnDirectionMode.Cone,
                BaseDirection = Vector3.UnitY,
                DirectionConeHalfAngle = MathF.PI / 6f
            };
            emitter.Reset();
            sim.Emitter = emitter;

            // Spawn 模块
            sim.SpawnModules.Add(new InitializeLifeModule { LifeMin = 0.8f, LifeMax = 1.5f });
            sim.SpawnModules.Add(new InitializeColorModule
            {
                ColorMin = new Vector4(1.0f, 0.4f, 0.05f, 1.0f),
                ColorMax = new Vector4(1.0f, 0.7f, 0.1f, 1.0f)
            });
            sim.SpawnModules.Add(new InitializeSizeModule
            {
                SizeMin = new Vector2(0.3f, 0.3f),
                SizeMax = new Vector2(0.6f, 0.6f)
            });
            sim.SpawnModules.Add(new InitializeVelocityModule { SpeedMin = 1.5f, SpeedMax = 3.5f });

            // Update 模块
            sim.UpdateModules.Add(new YamaEngine.Particles.GravityModule { Direction = new Vector3(0, 1, 0), Strength = 2.5f });
            sim.UpdateModules.Add(new YamaEngine.Particles.DragModule { Drag = 1.2f });
            sim.UpdateModules.Add(new YamaEngine.Particles.TurbulenceModule { Strength = 1.5f, Frequency = 0.4f, TimeScale = 0.3f });
            sim.UpdateModules.Add(new ColorUpdateModule
            {
                StartColor = new Vector4(1.0f, 0.5f, 0.1f, 1.0f),
                EndColor = new Vector4(0.3f, 0.05f, 0.0f, 0.0f)
            });
            sim.UpdateModules.Add(new SizeUpdateModule
            {
                StartSize = new Vector2(0.5f, 0.5f),
                EndSize = new Vector2(0.05f, 0.05f)
            });
            sim.UpdateModules.Add(new LifetimeUpdateModule());

            return sim;
        }

        /// <summary>
        /// 创建烟雾粒子效果（灰色，缓慢扩散，向上飘动）。
        /// </summary>
        public static FlowVFXParticleSimulationReal CreateSmokeEffect(Vector3 position, int maxParticles = 1536)
        {
            var sim = new FlowVFXParticleSimulationReal
            {
                MaxParticles = maxParticles,
                FixedDt = 1f / 60f,
                MaxSubSteps = 4
            };

            var emitter = new FlowVFXEmitterReal
            {
                Position = position,
                SpawnMode = RealSpawnMode.Rate,
                SpawnRate = 40f,
                LoopDuration = 10f,
                MaxLoops = -1,
                SimulationSpace = RealSimSpace.World,
                Shape = new DiscShape { Radius = 0.5f, Normal = Vector3.UnitY },
                DirectionMode = SpawnDirectionMode.Normal
            };
            emitter.Reset();
            sim.Emitter = emitter;

            // Spawn 模块
            sim.SpawnModules.Add(new InitializeLifeModule { LifeMin = 2.0f, LifeMax = 4.0f });
            sim.SpawnModules.Add(new InitializeColorModule
            {
                ColorMin = new Vector4(0.6f, 0.6f, 0.6f, 0.5f),
                ColorMax = new Vector4(0.8f, 0.8f, 0.8f, 0.7f)
            });
            sim.SpawnModules.Add(new InitializeSizeModule
            {
                SizeMin = new Vector2(0.5f, 0.5f),
                SizeMax = new Vector2(1.0f, 1.0f)
            });
            sim.SpawnModules.Add(new InitializeVelocityModule { SpeedMin = 0.5f, SpeedMax = 1.5f });

            // Update 模块
            sim.UpdateModules.Add(new YamaEngine.Particles.GravityModule { Direction = new Vector3(0, 1, 0), Strength = 1.0f });
            sim.UpdateModules.Add(new YamaEngine.Particles.DragModule { Drag = 0.8f });
            sim.UpdateModules.Add(new WindForceModule { WindDirection = new Vector3(1, 0, 0.3f), WindStrength = 0.5f });
            sim.UpdateModules.Add(new YamaEngine.Particles.TurbulenceModule { Strength = 1.0f, Frequency = 0.2f, TimeScale = 0.2f });
            sim.UpdateModules.Add(new ColorUpdateModule
            {
                StartColor = new Vector4(0.7f, 0.7f, 0.7f, 0.6f),
                EndColor = new Vector4(0.5f, 0.5f, 0.5f, 0.0f),
                FadeAlphaOnly = false
            });
            sim.UpdateModules.Add(new SizeUpdateModule
            {
                StartSize = new Vector2(0.8f, 0.8f),
                EndSize = new Vector2(2.5f, 2.5f)
            });
            sim.UpdateModules.Add(new LifetimeUpdateModule());

            return sim;
        }

        /// <summary>
        /// 创建爆炸粒子效果（一次性爆发，粒子向四周飞散）。
        /// </summary>
        public static FlowVFXParticleSimulationReal CreateExplosionEffect(Vector3 position, int maxParticles = 4096)
        {
            var sim = new FlowVFXParticleSimulationReal
            {
                MaxParticles = maxParticles,
                FixedDt = 1f / 60f,
                MaxSubSteps = 4
            };

            var emitter = new FlowVFXEmitterReal
            {
                Position = position,
                SpawnMode = RealSpawnMode.Burst,
                LoopDuration = 0.5f,
                MaxLoops = 1,
                SimulationSpace = RealSimSpace.World,
                Shape = new SphereShape { Radius = 0.2f, SurfaceOnly = true },
                DirectionMode = SpawnDirectionMode.Random
            };
            emitter.Bursts.Add(new SpawnBurst(0.0f, 800));
            emitter.Reset();
            sim.Emitter = emitter;

            // Spawn 模块
            sim.SpawnModules.Add(new InitializeLifeModule { LifeMin = 0.5f, LifeMax = 1.2f });
            sim.SpawnModules.Add(new InitializeColorModule
            {
                ColorMin = new Vector4(1.0f, 0.8f, 0.1f, 1.0f),
                ColorMax = new Vector4(1.0f, 0.3f, 0.05f, 1.0f)
            });
            sim.SpawnModules.Add(new InitializeSizeModule
            {
                SizeMin = new Vector2(0.2f, 0.2f),
                SizeMax = new Vector2(0.5f, 0.5f)
            });
            sim.SpawnModules.Add(new InitializeVelocityModule { SpeedMin = 5.0f, SpeedMax = 12.0f });
            sim.SpawnModules.Add(new InitializeRotationModule { RotationMin = 0f, RotationMax = MathF.PI * 2f });

            // Update 模块
            sim.UpdateModules.Add(new YamaEngine.Particles.GravityModule { Direction = new Vector3(0, -1, 0), Strength = 9.81f });
            sim.UpdateModules.Add(new YamaEngine.Particles.DragModule { Drag = 1.5f, Quadratic = true });
            sim.UpdateModules.Add(new ColorUpdateModule
            {
                StartColor = new Vector4(1.0f, 0.9f, 0.2f, 1.0f),
                EndColor = new Vector4(0.3f, 0.05f, 0.0f, 0.0f)
            });
            sim.UpdateModules.Add(new SizeUpdateModule
            {
                StartSize = new Vector2(0.4f, 0.4f),
                EndSize = new Vector2(0.02f, 0.02f)
            });
            sim.UpdateModules.Add(new LifetimeUpdateModule());

            return sim;
        }

        /// <summary>
        /// 创建魔法粒子效果（蓝色/紫色，螺旋上升，带轨道运动）。
        /// </summary>
        public static FlowVFXParticleSimulationReal CreateMagicEffect(Vector3 position, int maxParticles = 1024)
        {
            var sim = new FlowVFXParticleSimulationReal
            {
                MaxParticles = maxParticles,
                FixedDt = 1f / 60f,
                MaxSubSteps = 4
            };

            var emitter = new FlowVFXEmitterReal
            {
                Position = position,
                SpawnMode = RealSpawnMode.Rate,
                SpawnRate = 80f,
                LoopDuration = 10f,
                MaxLoops = -1,
                SimulationSpace = RealSimSpace.World,
                Shape = new DiscShape { Radius = 0.3f, Normal = Vector3.UnitY },
                DirectionMode = SpawnDirectionMode.Normal
            };
            emitter.Reset();
            sim.Emitter = emitter;

            // Spawn 模块
            sim.SpawnModules.Add(new InitializeLifeModule { LifeMin = 1.5f, LifeMax = 2.5f });
            sim.SpawnModules.Add(new InitializeColorModule
            {
                ColorMin = new Vector4(0.2f, 0.4f, 1.0f, 0.8f),
                ColorMax = new Vector4(0.6f, 0.2f, 1.0f, 1.0f),
                RandomHue = true,
                Saturation = 0.8f,
                Value = 1.0f
            });
            sim.SpawnModules.Add(new InitializeSizeModule
            {
                SizeMin = new Vector2(0.15f, 0.15f),
                SizeMax = new Vector2(0.35f, 0.35f)
            });
            sim.SpawnModules.Add(new InitializeVelocityModule { SpeedMin = 1.0f, SpeedMax = 2.5f });

            // Update 模块
            sim.UpdateModules.Add(new YamaEngine.Particles.GravityModule { Direction = new Vector3(0, 1, 0), Strength = 1.5f });
            sim.UpdateModules.Add(new YamaEngine.Particles.DragModule { Drag = 0.6f });
            sim.UpdateModules.Add(new VortexModule
            {
                AxisOrigin = position,
                AxisDirection = Vector3.UnitY,
                AngularSpeed = 4.0f,
                AxialForce = 1.0f,
                RadialForce = -0.5f
            });
            sim.UpdateModules.Add(new YamaEngine.Particles.OrbitModule
            {
                Center = position,
                Axis = Vector3.UnitY,
                AngularSpeed = 3.0f,
                RadiusDecay = 0.0f
            });
            sim.UpdateModules.Add(new ColorUpdateModule
            {
                StartColor = new Vector4(0.3f, 0.5f, 1.0f, 0.9f),
                EndColor = new Vector4(0.8f, 0.2f, 0.9f, 0.0f)
            });
            sim.UpdateModules.Add(new SizeUpdateModule
            {
                StartSize = new Vector2(0.25f, 0.25f),
                EndSize = new Vector2(0.05f, 0.05f)
            });
            sim.UpdateModules.Add(new LifetimeUpdateModule());

            return sim;
        }
    }
}
