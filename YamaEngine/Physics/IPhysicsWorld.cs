// ============================================================================================
//  IPhysicsWorld.cs
//
//  yama引擎 (YamaEngine) 物理世界统一接口。
//  所有物理系统实现应遵循此接口，方便在 PhysicsWorldReal <> PhysicsWorldV2 之间切换。
//
//  用法：
//    var physics = new PhysicsWorldReal(PhysicsWorldConfig.Default); // 推荐
//    // 或
//    var physics = new PhysicsWorldV2(settings); // V2（标记为 Obsolete）
//
//  目标框架：net8.0 / C# 10.0+
// ============================================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.Physics
{
    /// <summary>
    /// 物理世界统一接口。封装物理模拟、碰撞检测、射线查询等核心能力。
    /// PhysicsWorldReal 和 PhysicsWorldV2 都实现了此接口，
    /// 引擎层通过此接口解耦具体的物理实现。
    ///
    /// 实现要求：
    ///   - Step() 内部支持固定步长累积 + 子步分解
    ///   - 内置广相位 + 窄相位碰撞检测
    ///   - 支持约束求解与积分
    ///   - 支持休眠机制（可选）
    /// </summary>
    public interface IPhysicsWorld : IDisposable
    {
        // ===== 配置 =====
        /// <summary>重力向量。</summary>
        PhysicsVector3 Gravity { get; set; }

        /// <summary>固定时间步长（秒）。</summary>
        float FixedTimestep { get; set; }

        // ===== 生命周期 =====
        /// <summary>执行一帧物理模拟。</summary>
        void Step(float deltaTime);

        /// <summary>重置物理世界，清除所有刚体和约束。</summary>
        void Reset();

        // ===== 刚体管理 =====
        /// <summary>当前活跃刚体数量。</summary>
        int BodyCount { get; }

        // ===== 查询 =====
        /// <summary>射线检测，返回最近的命中结果。</summary>
        bool Raycast(PhysicsVector3 origin, PhysicsVector3 direction, float maxDistance, out RaycastHitData hit);

        /// <summary>是否暂停（默认未暂停）。</summary>
        bool IsPaused { get => false; set { } }

        /// <summary>按索引获取刚体（默认无）。</summary>
        RigidBody? GetBody(int index) => null;
    }
}
