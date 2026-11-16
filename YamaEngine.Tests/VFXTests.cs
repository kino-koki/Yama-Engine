using System;
using System.Numerics;
using YamaEngine.VFX;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    // ══════════════════════════════════════════════════════════════
    // VFXDepth — UE5.8 级 CPU 粒子模拟深度验证
    // 覆盖：辛欧拉重力积分、寿命到期回收、确定性 burst 发射计数、
    //       Pool 复用、显式欧拉位移
    // ══════════════════════════════════════════════════════════════
    public static class VFXTests
    {
        private static void AssertNear(float got, float want, float tol, string msg)
        {
            AssertTrue(MathF.Abs(got - want) <= tol,
                $"{msg} (got {got:F4}, want {want:F4}±{tol:F4})");
        }

        // 强制 LOD Level 0（RateMultiplier=1）避免计数被 LOD 缩放
        private static ParticleSystem MakeDeterministicSystem(int max = 64)
        {
            var sys = new ParticleSystem(max);
            sys.LODSystem.ForceLOD = true;
            sys.LODSystem.ForcedLODLevel = 0;
            return sys;
        }

        // ── 重力积分：辛欧拉（先 vel+=a*dt，再 pos+=v*dt）──
        public static void TestGravitySemiImplicitEuler()
        {
            var sys = MakeDeterministicSystem(8);
            sys.Pool.TrySpawn(out int idx, out var p);
            p.Initialize(maxLife: 1f, seed: 0f);
            // 初速度 0，重力 (0,-10,0)
            sys.SetParticle(idx, p);
            sys.AddModule(new GravityModule(-10f));

            var vp = Matrix4x4.Identity;
            sys.Update(0.1f, Vector3.Zero, ref vp);

            ref var q = ref sys.GetParticleRef(idx);
            // 第一帧：vel = 0 + (-10)*0.1 = -1；pos = 0 + (-1)*0.1 = -0.1
            AssertNear(q.Velocity.Y, -1f, 1e-4f, "Semi-implicit Euler: velocity should integrate gravity first.");
            AssertNear(q.Position.Y, -0.1f, 1e-4f, "Position should use updated velocity (pos += v*dt).");

            // 第二帧：vel = -1 + (-10)*0.1 = -2；pos = -0.1 + (-2)*0.1 = -0.3
            sys.Update(0.1f, Vector3.Zero, ref vp);
            q = ref sys.GetParticleRef(idx);
            AssertNear(q.Velocity.Y, -2f, 1e-4f, "Second frame velocity should accumulate.");
            AssertNear(q.Position.Y, -0.3f, 1e-4f, "Second frame position should use second-frame velocity.");
        }

        // ── 寿命到期回收：Life<=0 后粒子被 Kill 并标记死亡 ──
        public static void TestLifetimeExpiryRecycles()
        {
            var sys = MakeDeterministicSystem(8);
            sys.Pool.TrySpawn(out int idx, out var p);
            p.Initialize(maxLife: 0.5f, seed: 0f);
            sys.SetParticle(idx, p);

            var vp = Matrix4x4.Identity;
            // 0.3s < 0.5s → 仍存活
            sys.Update(0.3f, Vector3.Zero, ref vp);
            AssertTrue(sys.TotalActiveParticles == 1, "Particle alive at 0.3s < 0.5s max life.");
            AssertTrue(sys.GetParticleRef(idx).IsAlive, "Particle IsAlive should be true.");

            // 再 0.3s 累计 0.6s > 0.5s → 死亡
            sys.Update(0.3f, Vector3.Zero, ref vp);
            AssertTrue(sys.TotalActiveParticles == 0, "Particle should be recycled after lifetime expiry.");
            AssertTrue(sys.TotalKilledThisFrame == 1, "TotalKilledThisFrame should count the expired particle.");
            AssertTrue(!sys.GetParticleRef(idx).IsAlive, "Expired particle IsAlive should be false.");
        }

        // ── Burst 发射：确定性计数（Point Shape + 零 variance）──
        public static void TestBurstEmissionDeterministicCount()
        {
            var sys = MakeDeterministicSystem(64);
            var emitter = new ParticleEmitter { MaxParticles = 32 };
            emitter.Shape = EmitterShape.Point;
            sys.AddEmitter(emitter);

            var burst = new SpawnBurstModule();
            burst.AddBurst(time: 0f, count: 10, countVariance: 0); // t=0 触发，零 variance
            sys.AddModule(burst);

            var vp = Matrix4x4.Identity;
            // emitter.Tick(0.016) → SystemTime≈0.016 >= 0 → burst 触发 10 个
            sys.Update(0.016f, Vector3.Zero, ref vp);

            AssertTrue(sys.TotalSpawnedThisFrame == 10,
                $"Burst count=10 with zero variance should spawn exactly 10 (got {sys.TotalSpawnedThisFrame}).");
            AssertTrue(sys.TotalActiveParticles == 10, "All spawned burst particles should be alive.");
            AssertTrue(burst.TotalBurstParticlesEmitted == 10, "Burst module should record total emitted.");
            // burst 不重复触发（HasFired=true）
            sys.Update(0.016f, Vector3.Zero, ref vp);
            AssertTrue(sys.TotalSpawnedThisFrame == 0,
                "Burst with HasFired=true should not re-trigger on second frame.");
        }

        // ── Rate 发射：Accumulator 累积语义（Rate=100, dt=0.1 → 10 个）──
        public static void TestRateEmissionAccumulator()
        {
            var sys = MakeDeterministicSystem(64);
            var emitter = new ParticleEmitter { MaxParticles = 64 };
            emitter.Shape = EmitterShape.Point;
            sys.AddEmitter(emitter);

            sys.AddModule(new SpawnRateModule(rate: 100f));

            var vp = Matrix4x4.Identity;
            // dt=0.1, Rate=100 → Accumulator += 10 → spawn 10
            sys.Update(0.1f, Vector3.Zero, ref vp);
            AssertTrue(sys.TotalSpawnedThisFrame == 10,
                $"Rate=100 over dt=0.1 should spawn 10 (got {sys.TotalSpawnedThisFrame}).");

            // dt=0.05 → Accumulator += 5 → spawn 5
            sys.Update(0.05f, Vector3.Zero, ref vp);
            AssertTrue(sys.TotalSpawnedThisFrame == 5,
                $"Rate=100 over dt=0.05 should spawn 5 (got {sys.TotalSpawnedThisFrame}).");
        }

        // ── Pool 复用：死亡粒子槽被新粒子复用（容量不增长）──
        public static void TestPoolSlotReuseAfterExpiry()
        {
            var sys = MakeDeterministicSystem(4); // 小容量
            sys.Pool.TrySpawn(out int first, out var p);
            p.Initialize(maxLife: 0.1f, seed: 0f);
            sys.SetParticle(first, p);

            var vp = Matrix4x4.Identity;
            // 粒子寿命到期
            sys.Update(0.2f, Vector3.Zero, ref vp);
            AssertTrue(sys.TotalActiveParticles == 0, "First particle should expire.");

            // 新粒子应复用某个已释放的槽（容量 4 不增长）
            bool spawned = sys.Pool.TrySpawn(out int second, out _);
            AssertTrue(spawned, "Pool should have free slot for new particle after expiry.");
            AssertTrue(second >= 0 && second < 4, "Recycled slot should be within pool capacity (no growth).");
            AssertTrue(sys.Pool.AliveCount == 1, "AliveCount should be 1 after reuse.");
        }

        // ── 初速度 + 重力：辛欧拉下位置积分正确性（非零初速度）──
        public static void TestInitialVelocityWithGravity()
        {
            var sys = MakeDeterministicSystem(8);
            sys.Pool.TrySpawn(out int idx, out var p);
            p.Initialize(maxLife: 2f, seed: 0f);
            p.Velocity = new Vector3(10f, 0f, 0f); // 水平初速 10
            sys.SetParticle(idx, p);
            sys.AddModule(new GravityModule(-10f));

            var vp = Matrix4x4.Identity;
            sys.Update(0.1f, Vector3.Zero, ref vp);

            ref var q = ref sys.GetParticleRef(idx);
            // vel.y = -10*0.1 = -1, vel.x 保持 10（重力只作用 Y）
            AssertNear(q.Velocity.X, 10f, 1e-4f, "Gravity should not affect X velocity.");
            // pos.x = 0 + 10*0.1 = 1
            AssertNear(q.Position.X, 1f, 1e-4f, "X position should integrate initial velocity.");
            // pos.y = 0 + (-1)*0.1 = -0.1
            AssertNear(q.Position.Y, -0.1f, 1e-4f, "Y position should integrate gravity-driven velocity.");
        }
    }
}
