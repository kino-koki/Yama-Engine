using YamaEngine.Core;
using YamaEngine.MathUtils;
using YamaEngine.Physics.Real;

public static class Diag
{
    public static void Main()
    {
        var world = new RealPhysicsEngine();
        world.Settings.Gravity = new Vector3(0, -9.81f, 0);
        world.Settings.SolverIterations = 8;

        var ground = new RealRigidBody
        {
            Name = "Ground", Mass = 0, IsStatic = true,
            Material = new PhysicsMaterial { StaticFriction = 0.5f, DynamicFriction = 0.5f }
        };
        ground.AddShape(new BoxShape(new Vector3(10, 0.1f, 10)));
        ground.Position = new Vector3(0, -0.1f, 0);
        world.AddRigidBody(ground);

        var block = new RealRigidBody
        {
            Name = "Block", Mass = 1, InverseMass = 1,
            Material = new PhysicsMaterial { StaticFriction = 0.5f, DynamicFriction = 0.5f }
        };
        block.AddShape(new BoxShape(new Vector3(0.3f, 0.3f, 0.3f)));
        block.Position = new Vector3(0, 2, 0);
        world.AddRigidBody(block);

        // 沉降
        for (int i = 0; i < 90; i++) world.Step(0.016f);
        block.ApplyImpulse(new Vector3(4.905f, 0, 0));
        var v0 = block.Velocity;

        for (int k = 1; k <= 5; k++)
        {
            world.Step(0.016f);
            System.Console.WriteLine($"  step{k}: pos.y={block.Position.Y:F4} vy={block.Velocity.Y:F4} vx={block.Velocity.X:F4}");
        }
        var v1 = block.Velocity;
        System.Console.WriteLine($"v0=({v0.X:F3}) v1=({v1.X:F3}) decel={(v0.X - v1.X) / (5 * 0.016f):F3}");
    }
}
