// ==================================================================================================
//  ParticleComponents.cs — yama引擎 ECS 粒子系统组件
// ==================================================================================================
//  命名空间: YamaEngine.ECS
//  依赖:     YamaEngine.Particles
//
//  功能:
//    ECS 版本的粒子系统组件，由 EcsParticleBridge 驱动实际的 ParticleSystemCore 模拟。
// ==================================================================================================

using System;
using System.Numerics;
using YamaEngine.Particles;

namespace YamaEngine.ECS
{
    /// <summary>
    /// ECS 粒子发射器组件。附加到实体后，由 <see cref="EcsParticleBridge"/> 驱动模拟。
    /// </summary>
    public struct ParticleEmitterComponent : IComponent
    {
        /// <summary>发射器是否激活。</summary>
        public bool Enabled { get; set; }

        /// <summary>粒子系统核心（运行时创建，不序列化）。</summary>
        public ParticleSystemCore? RuntimeSystem { get; set; }

        /// <summary>最大粒子数。</summary>
        public int MaxParticles { get; set; }

        /// <summary>发射速率（个/秒）。</summary>
        public float EmissionRate { get; set; }

        /// <summary>生命周期（秒）。</summary>
        public float Lifetime { get; set; }

        /// <summary>起始颜色。</summary>
        public Vector4 StartColor { get; set; }

        /// <summary>起始大小。</summary>
        public float StartSize { get; set; }

        /// <summary>发射形状类型。</summary>
        public ParticleShapeType ShapeType { get; set; }

        /// <summary>发射形状半径。</summary>
        public float ShapeRadius { get; set; }

        /// <summary>重力/力场 (Y 轴)。</summary>
        public float Gravity { get; set; }

        /// <summary>起始速度。</summary>
        public float StartSpeed { get; set; }

        /// <summary>循环播放。</summary>
        public bool Looping { get; set; }

        /// <summary>模拟空间。</summary>
        public ParticleSimulationSpace SimulationSpace { get; set; }

        public ParticleEmitterComponent()
        {
            Enabled = true;
            MaxParticles = 100;
            EmissionRate = 10f;
            Lifetime = 3f;
            StartColor = new Vector4(1, 1, 1, 1);
            StartSize = 0.1f;
            ShapeType = ParticleShapeType.Cone;
            ShapeRadius = 1f;
            Gravity = -9.81f;
            StartSpeed = 2f;
            Looping = true;
            SimulationSpace = ParticleSimulationSpace.World;
        }
    }

    /// <summary>粒子发射形状类型。</summary>
    public enum ParticleShapeType
    {
        Sphere,
        Hemisphere,
        Cone,
        Box,
    }
}
