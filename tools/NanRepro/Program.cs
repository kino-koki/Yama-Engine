using System;
using System.Numerics;
using YamaEngine.Physics.Real;

public static class NanRepro
{
    public static void Main()
    {
        PhysDiag.ManifoldDiag.Run();
        return;

        const float G = 9.81f;
        var engine = new RealPhysicsEngine(new PhysicsSettings
        {
            Gravity = new Vector3(0, -G, 0),
            SolverIterations = 8,
            EnableSleep = true
        });
        var ground = engine.CreateRigidbody(new RigidbodyDescription
        {
            Name = "Ground", Type = RigidbodyType.Static, Position = Vector3.Zero,
            Material = new PhysicsMaterial { Name = "G", StaticFriction = 0.8f, DynamicFriction = 0.8f, Restitution = 0.1f }
        });
        engine.CreateCollider(new ColliderDescription { Type = ColliderType.Box, Size = new Vector3(30, 1, 30) }, ground);
        var block = engine.CreateRigidbody(new RigidbodyDescription
        {
            Name = "Block", Type = RigidbodyType.Dynamic, Position = new Vector3(0, 2f, 0), Mass = 1f,
            Material = new PhysicsMaterial { Name = "B", StaticFriction = 0.8f, DynamicFriction = 0.8f, Restitution = 0.1f }
        });
        engine.CreateCollider(new ColliderDescription { Type = ColliderType.Box, Size = new Vector3(1, 1, 1) }, block);

        // 期望：盒子从 y=2 落到地面 y=1（block 半高 0.5 + ground 半高 0.5）并静止。
        const float expectedRest = 1.0f;
        bool anyBad = false;
        float lastY = block.Position.Y;

        for (int i = 0; i < 120; i++)
        {
            engine.Step(0.016f);
            var p = block.Position;
            var v = block.Velocity;
            bool bad = !float.IsFinite(p.Y) || !float.IsFinite(v.Y);
            if (bad) { Console.WriteLine($"  FIRST BAD at step {i}"); anyBad = true; break; }

            if (i % 20 == 0 || (i >= 25 && i <= 30))
                Console.WriteLine($"step {i,3} posY={p.Y:G4} velY={v.Y:G4}");
            lastY = p.Y;
        }

        if (!anyBad)
        {
            Console.WriteLine($"Final posY={lastY:G4} (expected rest ~{expectedRest})");
            Console.WriteLine(MathF.Abs(lastY - expectedRest) < 0.15f
                ? "PASS: box settled on ground, no NaN"
                : "FAIL: box did not settle at ground height");
        }
        engine.Dispose();

        // ---- 静摩擦：F=5N < μmg≈7.85N，块应保持静止 ----
        Console.WriteLine("\n--- Static friction (F=5N < mu*m*g=7.85N) ---");
        var engine2 = new RealPhysicsEngine(new PhysicsSettings
        {
            Gravity = new Vector3(0, -G, 0),
            SolverIterations = 8,
            EnableSleep = true
        });
        var ground2 = engine2.CreateRigidbody(new RigidbodyDescription
        {
            Name = "Ground", Type = RigidbodyType.Static, Position = Vector3.Zero,
            Material = new PhysicsMaterial { Name = "G", StaticFriction = 0.8f, DynamicFriction = 0.8f, Restitution = 0.1f }
        });
        engine2.CreateCollider(new ColliderDescription { Type = ColliderType.Box, Size = new Vector3(30, 1, 30) }, ground2);
        var block2 = engine2.CreateRigidbody(new RigidbodyDescription
        {
            Name = "Block", Type = RigidbodyType.Dynamic, Position = new Vector3(0, 2f, 0), Mass = 1f,
            Material = new PhysicsMaterial { Name = "B", StaticFriction = 0.8f, DynamicFriction = 0.8f, Restitution = 0.1f }
        });
        engine2.CreateCollider(new ColliderDescription { Type = ColliderType.Box, Size = new Vector3(1, 1, 1) }, block2);

        for (int i = 0; i < 90; i++) engine2.Step(0.016f);
        Console.WriteLine($"After settle: posY={block2.Position.Y:G4} velX={block2.Velocity.X:G4}");

        for (int i = 0; i < 150; i++)
        {
            block2.ApplyForce(new Vector3(5f, 0, 0));
            engine2.Step(0.016f);
        }
        float dx = MathF.Abs(block2.Position.X);
        float vx = MathF.Abs(block2.Velocity.X);
        Console.WriteLine($"Final: dx={block2.Position.X:G4}m velX={block2.Velocity.X:G4} m/s");
        Console.WriteLine(dx < 0.1f && vx < 0.5f
            ? "PASS: static friction held block in place"
            : "FAIL: block slid despite F < mu*m*g");
        engine2.Dispose();
    }
}
