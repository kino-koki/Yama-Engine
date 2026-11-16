using System;
using System.Numerics;
using YamaEngine.Physics.Real;

namespace PhysDiag
{
    /// 动摩擦诊断：盒 μ=0.5，settle 90 步，冲量 5 m/s 滑行 30 步。
    /// 期望减速度 μg=4.905，实测 10.443≈2×。追踪每步法向/切向冲量与速度。
    public static class FrictionKineticDiag
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

            for (int i = 0; i < 90; i++) engine.Step(DT);
            Console.WriteLine($"=== settle: pos=({block.Position.X:F4},{block.Position.Y:F4}) contacts={engine.Contacts.Count}");
            block.ApplyImpulse(new Vector3(5f, 0, 0));
            Console.WriteLine("step | nCt | sumN | sumT | vX | angW | Y");
            for (int i = 0; i < 30; i++)
            {
                engine.Step(DT);
                float sumN = 0f, sumT = 0f;
                foreach (var c in engine.Contacts)
                {
                    sumN += Vector3.Dot(c.NormalImpulse, c.Normal);
                    sumT += c.TangentImpulse.Length();
                }
                if (i < 12 || i % 6 == 0)
                    Console.WriteLine($"{i,4} | {engine.Contacts.Count,3} | {sumN,6:F4} | {sumT,6:F4} | {block.Velocity.X,6:F4} | {block.AngularVelocity.Z,6:F4} | {block.Position.Y:F4}");
            }
            engine.Dispose();
        }
    }
}
