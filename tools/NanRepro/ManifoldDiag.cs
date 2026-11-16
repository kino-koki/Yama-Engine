using System;
using System.Numerics;
using YamaEngine.Physics.Real;

namespace PhysDiag
{
    /// 动摩擦 manifold 诊断：复现 TestKineticFrictionDeceleration 场景，
    /// 在 Step 中段（Cleanup 清空前）通过反射读私有 _contacts，看 manifold 点数/深度/法线。
    public static class ManifoldDiag
    {
        public static void Run()
        {
            const float G = 9.81f;
            const float DT = 0.016f;
            var engine = new RealPhysicsEngine(new PhysicsSettings
            {
                Gravity = new Vector3(0, -G, 0),
                SolverIterations = 8,
                EnableSleep = true
            });
            var ground = engine.CreateRigidbody(new RigidbodyDescription
            {
                Name = "Ground", Type = RigidbodyType.Static, Position = Vector3.Zero,
                Material = new PhysicsMaterial { Name = "G", StaticFriction = 0.5f, DynamicFriction = 0.5f, Restitution = 0.1f }
            });
            engine.CreateCollider(new ColliderDescription { Type = ColliderType.Box, Size = new Vector3(30, 1, 30) }, ground);
            var block = engine.CreateRigidbody(new RigidbodyDescription
            {
                Name = "Block", Type = RigidbodyType.Dynamic, Position = new Vector3(0, 2.0f, 0), Mass = 1f,
                Material = new PhysicsMaterial { Name = "B", StaticFriction = 0.5f, DynamicFriction = 0.5f, Restitution = 0.1f }
            });
            engine.CreateCollider(new ColliderDescription { Type = ColliderType.Box, Size = new Vector3(1, 1, 1) }, block);

            // 反射拿私有 _contacts（Step 后被 Cleanup 清空，需在内部读；这里用子类 hook 不可行，
            // 改为手动逐步：调用引擎内部阶段不可访问，故退而打印公开状态 + 用独立 NarrowPhase 验证）。
            // 直接用 NarrowPhase 单独验证 manifold 生成。
            var groundCollider = (BoxCollider)engine.ColliderFor(ground);
            var blockCollider = (BoxCollider)engine.ColliderFor(block);

            for (int i = 0; i < 90; i++) engine.Step(DT);
            Console.WriteLine($"settle: blockPos=({block.Position.X:F4},{block.Position.Y:F4},{block.Position.Z:F4})");
            var q = block.Orientation;
            Console.WriteLine($"  blockOrient=(x{q.X:F3},y{q.Y:F3},z{q.Z:F3},w{q.W:F3})");
            Console.WriteLine($"  groundPos=({ground.Position.X:F4},{ground.Position.Y:F4})");
            var np = new NarrowPhase();
            var cs = np.GenerateContacts(blockCollider, groundCollider);
            Console.WriteLine($"  NarrowPhase(boxBlock,boxGround) => {cs.Count} contacts");
            for (int k = 0; k < cs.Count; k++)
            {
                var c = cs[k];
                Console.WriteLine($"    [{k}] n=({c.Normal.X:F3},{c.Normal.Y:F3},{c.Normal.Z:F3}) depth={c.PenetrationDepth:F5} pt=({c.Point.X:F3},{c.Point.Y:F3},{c.Point.Z:F3})");
            }

            block.ApplyImpulse(new Vector3(5f, 0, 0));
            Console.WriteLine("slide (after impulse), contacts per frame:");
            for (int i = 0; i < 6; i++)
            {
                engine.Step(DT);
                q = block.Orientation;
                var cs2 = np.GenerateContacts(blockCollider, groundCollider);
                Console.WriteLine($"  f{i}: nCt={cs2.Count} Y={block.Position.Y:F4} angWz={block.AngularVelocity.Z:F3} vX={block.Velocity.X:F3}");
                for (int k = 0; k < Math.Min(cs2.Count, 5); k++)
                    Console.WriteLine($"      [{k}] n=({cs2[k].Normal.X:F2},{cs2[k].Normal.Y:F2}) d={cs2[k].PenetrationDepth:F4}");
            }
        }
    }

    /// 小 helper：从引擎拿 collider 列表（公开 API 若无则退化）。
    public static class EngineExt
    {
        public static Collider ColliderFor(this RealPhysicsEngine e, Rigidbody b)
        {
            // 用反射取私有 _colliders
            var f = typeof(RealPhysicsEngine).GetField("_colliders",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var list = (System.Collections.Generic.List<Collider>)f.GetValue(e);
            foreach (var c in list)
                if (c.Body == b) return c;
            return null;
        }
    }
}
