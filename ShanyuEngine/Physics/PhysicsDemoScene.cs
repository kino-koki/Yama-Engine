// ============================================================================================
//  PhysicsDemoScene.cs
//
//  山雨引擎 (ShanyuEngine) 物理演示场景
//
//  职责：
//    创建一组物理驱动的可见物体（地面、立方体金字塔、下落球体），
//    配置 PhysicsWorld + PhysicsRenderBridge + PhysicsRenderTarget 的完整管线，
//    证明物理→渲染数据通路已打通。
//
//  使用方式：
//    var demo = PhysicsDemoScene.CreatePhysicsDemo();
//    // 每帧：
//    demo.World.Step(dt);
//    demo.Bridge.SyncTransforms();
//    // 然后遍历 demo.RenderTargets 渲染每个 target
//
//  命名空间：ShanyuEngine.Physics
//  目标框架：net8.0 / C# 8.0+
//  外部依赖：System.Numerics
// ============================================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

namespace ShanyuEngine.Physics
{
    /// <summary>
    /// 物理演示场景工厂。
    /// 创建一堆物体在重力下下落/碰撞/堆叠，用于验证物理→渲染管线。
    /// </summary>
    public static class PhysicsDemoScene
    {
        /// <summary>
        /// 创建演示场景：地面 + 立方体金字塔 + 下落球体。
        /// 返回 PhysicsDemoSetup，包含 World、Bridge 和 RenderTargets。
        /// </summary>
        public static PhysicsDemoSetup CreatePhysicsDemo()
        {
            var world = new PhysicsWorldReal(PhysicsWorldConfig.Default);
            var targets = new List<PhysicsRenderTarget>();
            var bridge = new PhysicsRenderBridge(world);

            // ==========================================
            // 1. 地面（静态刚体，不参与动力学模拟）
            // ==========================================
            int groundIndex = CreateBoxBody(
                world,
                new Vector3(0, -0.5f, 0),
                new Vector3(20, 0.5f, 20),
                isStatic: true);

            var groundTarget = new PhysicsRenderTarget
            {
                Name = "Ground",
                Scale = new Vector3(20, 0.5f, 20),
                BaseColor = new Vector4(0.3f, 0.5f, 0.3f, 1.0f),
                MeshType = DemoMeshType.Cube,
            };
            bridge.Register(groundIndex, groundTarget);
            targets.Add(groundTarget);

            // ==========================================
            // 2. 立方体金字塔 (5层)
            //    从底部到顶部逐渐缩小，展示堆叠稳定性
            // ==========================================
            for (int level = 0; level < 5; level++)
            {
                for (int x = -level; x <= level; x++)
                {
                    for (int z = -level; z <= level; z++)
                    {
                        float y = 0.5f + level * 1.05f;
                        var pos = new Vector3(x * 1.1f, y, z * 1.1f);

                        int bodyIndex = CreateBoxBody(
                            world,
                            pos,
                            new Vector3(0.5f, 0.5f, 0.5f),
                            isStatic: false,
                            mass: 1.0f);

                        var target = new PhysicsRenderTarget
                        {
                            Name = $"Box_L{level}_{x}_{z}",
                            Scale = new Vector3(1.0f, 1.0f, 1.0f),
                            BaseColor = new Vector4(
                                0.2f + level * 0.15f,
                                0.3f,
                                0.8f - level * 0.1f,
                                1.0f),
                            MeshType = DemoMeshType.Cube,
                        };
                        bridge.Register(bodyIndex, target);
                        targets.Add(target);
                    }
                }
            }

            // ==========================================
            // 3. 从高处落下的球体 (10个)
            //    排列成圆形，从不同高度落下
            // ==========================================
            for (int i = 0; i < 10; i++)
            {
                var pos = new Vector3(
                    MathF.Sin(i * 1.3f) * 5.0f,
                    8.0f + i * 0.5f,
                    MathF.Cos(i * 1.7f) * 5.0f);

                int bodyIndex = CreateSphereBody(
                    world,
                    pos,
                    radius: 0.5f,
                    mass: 0.5f);

                var target = new PhysicsRenderTarget
                {
                    Name = $"Sphere_{i}",
                    Scale = new Vector3(1.0f, 1.0f, 1.0f),
                    BaseColor = new Vector4(
                        0.8f,
                        0.2f + i * 0.06f,
                        0.2f + i * 0.04f,
                        1.0f),
                    MeshType = DemoMeshType.Sphere,
                };
                bridge.Register(bodyIndex, target);
                targets.Add(target);
            }

            // ==========================================
            // 4. 斜坡（倾斜的静态长方体）
            // ==========================================
            int rampIndex = CreateBoxBody(
                world,
                new Vector3(6, 0.5f, 0),
                new Vector3(3, 0.3f, 5),
                isStatic: true);

            var rampBody = world.GetBody(rampIndex);
            if (rampBody != null)
            {
                // 将斜坡绕 Z 轴倾斜约 20 度
                float angle = MathF.PI / 9.0f; // 20 degrees
                rampBody.Rotation = new PhysicsQuaternion(
                    0, 0, MathF.Sin(angle * 0.5f), MathF.Cos(angle * 0.5f));
                rampBody.UpdateInertiaWorld();
            }

            var rampTarget = new PhysicsRenderTarget
            {
                Name = "Ramp",
                Scale = new Vector3(6, 0.6f, 10),
                BaseColor = new Vector4(0.6f, 0.4f, 0.2f, 1.0f),
                MeshType = DemoMeshType.Cube,
            };
            bridge.Register(rampIndex, rampTarget);
            targets.Add(rampTarget);

            // 首次同步：使渲染目标的初始变换与刚体一致
            bridge.SyncTransforms();

            return new PhysicsDemoSetup
            {
                World = world,
                Bridge = bridge,
                RenderTargets = targets,
            };
        }

        /// <summary>
        /// 创建简化演示场景（仅地面 + 5个下落立方体）。
        /// 适合快速测试和调试。
        /// </summary>
        public static PhysicsDemoSetup CreateSimpleDemo()
        {
            var world = new PhysicsWorldReal(PhysicsWorldConfig.Default);
            var targets = new List<PhysicsRenderTarget>();
            var bridge = new PhysicsRenderBridge(world);

            // 地面
            int groundIndex = CreateBoxBody(world,
                new Vector3(0, -0.5f, 0),
                new Vector3(10, 0.5f, 10),
                isStatic: true);
            var groundTarget = new PhysicsRenderTarget
            {
                Name = "Ground",
                Scale = new Vector3(10, 0.5f, 10),
                BaseColor = new Vector4(0.3f, 0.5f, 0.3f, 1.0f),
                MeshType = DemoMeshType.Cube,
            };
            bridge.Register(groundIndex, groundTarget);
            targets.Add(groundTarget);

            // 5个不同高度的下落立方体
            for (int i = 0; i < 5; i++)
            {
                var pos = new Vector3(i * 2.0f - 4.0f, 3.0f + i * 1.5f, 0);
                int bodyIndex = CreateBoxBody(world, pos,
                    new Vector3(0.5f, 0.5f, 0.5f),
                    isStatic: false, mass: 1.0f);
                var target = new PhysicsRenderTarget
                {
                    Name = $"DropBox_{i}",
                    Scale = Vector3.One,
                    BaseColor = new Vector4(0.2f + i * 0.15f, 0.4f, 0.9f - i * 0.1f, 1.0f),
                    MeshType = DemoMeshType.Cube,
                };
                bridge.Register(bodyIndex, target);
                targets.Add(target);
            }

            bridge.SyncTransforms();

            return new PhysicsDemoSetup
            {
                World = world,
                Bridge = bridge,
                RenderTargets = targets,
            };
        }

        #region 刚体创建辅助方法

        /// <summary>
        /// 创建长方体刚体并添加到物理世界。
        /// </summary>
        /// <param name="world">目标物理世界。</param>
        /// <param name="pos">中心位置。</param>
        /// <param name="halfExtents">半尺寸（长宽高的一半）。</param>
        /// <param name="isStatic">是否静态体。</param>
        /// <param name="mass">质量（静态体忽略）。</param>
        /// <returns>刚体在 PhysicsWorld 中的索引。</returns>
        private static int CreateBoxBody(
            PhysicsWorldReal world, Vector3 pos, Vector3 halfExtents,
            bool isStatic, float mass = 1.0f)
        {
            var shape = new BoxShape(new PhysicsVector3(halfExtents.X, halfExtents.Y, halfExtents.Z));

            var body = new RigidBody(shape, isStatic ? 0f : mass)
            {
                Position = new PhysicsVector3(pos.X, pos.Y, pos.Z),
                BodyType = isStatic ? BodyType.Static : BodyType.Dynamic,
                Friction = 0.5f,
                Restitution = 0.1f,
            };

            return world.AddBody(body);
        }

        /// <summary>
        /// 创建球体刚体并添加到物理世界。
        /// </summary>
        /// <param name="world">目标物理世界。</param>
        /// <param name="pos">球心位置。</param>
        /// <param name="radius">球半径。</param>
        /// <param name="mass">质量。</param>
        /// <returns>刚体在 PhysicsWorld 中的索引。</returns>
        private static int CreateSphereBody(
            PhysicsWorldReal world, Vector3 pos, float radius, float mass = 1.0f)
        {
            var shape = new SphereShape(radius);

            var body = new RigidBody(shape, mass)
            {
                Position = new PhysicsVector3(pos.X, pos.Y, pos.Z),
                BodyType = BodyType.Dynamic,
                Friction = 0.4f,
                Restitution = 0.6f, // 球体弹跳性好
            };

            return world.AddBody(body);
        }

        #endregion
    }

    /// <summary>
    /// 物理演示场景的完整配置。
    /// 包含物理世界、桥接器和渲染目标列表。
    /// </summary>
    public class PhysicsDemoSetup
    {
        /// <summary>物理世界实例。</summary>
        public PhysicsWorldReal World;

        /// <summary>物理→渲染桥接器。</summary>
        public PhysicsRenderBridge Bridge;

        /// <summary>所有渲染目标列表（与 PhysicsWorld 中的刚体一一对应）。</summary>
        public List<PhysicsRenderTarget> RenderTargets;

        /// <summary>
        /// 执行一帧物理模拟并同步渲染变换。
        /// </summary>
        /// <param name="dt">帧时间增量（秒）。</param>
        public void StepAndSync(float dt)
        {
            World.Step(dt);
            Bridge.SyncTransforms();
        }

        /// <summary>
        /// 使用时间累积器步进（适配可变帧率）。
        /// </summary>
        /// <param name="frameDt">帧时间增量（秒）。</param>
        public void StepAndSyncWithAccumulator(float frameDt)
        {
            World.StepWithAccumulator(frameDt);
            Bridge.SyncTransforms();
        }
    }
}