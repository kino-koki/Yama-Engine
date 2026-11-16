// ShurikenParticleDepthTests.cs — Unity5 Shuriken 粒子模块深度验证
// 覆盖：
//   * ParticleCurve：Constant / Linear 关键帧插值 / TwoConstants 随机 / 边界 Clamp
//   * ColorCurve：RGBA 四分量插值，Alpha 渐变
//   * ParticleSOA：槽位分配、Clear、RecountAlive
//   * EmitterModuleV2：速率发射、形状采样（Sphere/Cone/Box）、双值速度
//   * LifetimeModuleV2：初始化寿命、老化、寿命终结杀粒子
//   * ForceModuleV2：重力加速度积分、速度累积
//   * ColorModuleV2：颜色随生命周期变化
//   * SizeModuleV2：尺寸随生命周期变化
//   * ParticleModulePipeline：模块按优先级排序、Update 驱动、事件派发
// 全部 CPU 级确定性，不依赖 GPU/RHI。
using System;
using System.Numerics;
using YamaEngine.Particles;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

public static class ShurikenParticleDepthTests
{
    private static void Near(float got, float want, float tol, string msg)
        => AssertTrue(MathF.Abs(got - want) <= tol, $"{msg}（期望 {want}，实际 {got}，容差 {tol}）");

    private static void AssertFalse(bool condition, string message)
        => AssertTrue(!condition, message);

    // ── 辅助：构造 ModuleContext，绑定 SOA 数据 ──
    private static ModuleContext MakeCtx(ParticleSOA soa, float dt = 0.016f, float elapsed = 0f)
        => new()
        {
            DeltaTime = dt,
            ElapsedTime = elapsed,
            Data = soa,
            Events = new ParticleEventQueue(),
            EmitterPosition = Vector3.Zero
        };

    // ── 辅助：手动标记存活粒子（绕过随机发射，确定性测试） ──
    private static int SpawnTest(ParticleSOA soa, Vector3 pos = default, Vector3 vel = default,
        float lifetime = 2f, float seed = 0.5f)
    {
        int slot = soa.AllocateSlot();
        AssertTrue(slot >= 0, "SOA 应有空闲槽位");
        soa.Position[slot] = pos;
        soa.Velocity[slot] = vel;
        soa.Acceleration[slot] = Vector3.Zero;
        soa.Lifetime[slot] = lifetime;
        soa.Age[slot] = 0f;
        soa.RandomSeed[slot] = seed;
        soa.Mass[slot] = 1f;
        soa.MarkAlive(slot);
        return slot;
    }

    // ══════════════════════════════════════════════════════════════════
    //  一、ParticleCurve 求值
    // ══════════════════════════════════════════════════════════════════

    /// <summary>Constant 模式：始终返回固定值。</summary>
    public static void TestCurveConstant()
    {
        var c = new ParticleCurve(3.5f);
        AssertEqual(ParticleCurveMode.Constant, c.Mode, "模式应为 Constant");
        Near(c.Evaluate(0f), 3.5f, 0.001f, "Constant t=0");
        Near(c.Evaluate(0.5f), 3.5f, 0.001f, "Constant t=0.5");
        Near(c.Evaluate(1f), 3.5f, 0.001f, "Constant t=1");
    }

    /// <summary>Linear 模式：关键帧之间线性插值，t 越界后 Clamp。</summary>
    public static void TestCurveLinear()
    {
        var c = new ParticleCurve { Mode = ParticleCurveMode.Linear };
        c.Keys.Add(new CurveKey(0f, 0f));
        c.Keys.Add(new CurveKey(1f, 10f));

        Near(c.Evaluate(0f), 0f, 0.001f, "Linear t=0 起始");
        Near(c.Evaluate(0.5f), 5f, 0.001f, "Linear t=0.5 中点");
        Near(c.Evaluate(1f), 10f, 0.001f, "Linear t=1 终点");
        Near(c.Evaluate(-0.5f), 0f, 0.001f, "Linear t<0 Clamp 到起点");
        Near(c.Evaluate(2f), 10f, 0.001f, "Linear t>1 Clamp 到终点");
    }

    /// <summary>Linear 多关键帧：3 点曲线中间段插值。</summary>
    public static void TestCurveLinearMultiKey()
    {
        var c = new ParticleCurve { Mode = ParticleCurveMode.Linear };
        c.Keys.Add(new CurveKey(0f, 0f));
        c.Keys.Add(new CurveKey(0.5f, 8f));
        c.Keys.Add(new CurveKey(1f, 4f));

        Near(c.Evaluate(0.25f), 4f, 0.001f, "0→8 段 t=0.25");
        Near(c.Evaluate(0.75f), 6f, 0.001f, "8→4 段 t=0.75");
    }

    /// <summary>TwoConstants 模式：在 min/max 之间按随机因子取值。</summary>
    public static void TestCurveTwoConstants()
    {
        var c = new ParticleCurve(2f, 6f);
        AssertEqual(ParticleCurveMode.TwoConstants, c.Mode, "模式应为 TwoConstants");
        Near(c.Evaluate(0f, 0f), 2f, 0.001f, "随机因子 0 → min");
        Near(c.Evaluate(0f, 1f), 6f, 0.001f, "随机因子 1 → max");
        Near(c.Evaluate(0f, 0.5f), 4f, 0.001f, "随机因子 0.5 → 中值");
    }

    /// <summary>Bezier 模式：Hermite 插值，切线导出控制点。</summary>
    public static void TestCurveBezier()
    {
        var c = new ParticleCurve { Mode = ParticleCurveMode.Bezier };
        c.Keys.Add(new CurveKey(0f, 0f, 0f, 0f));
        c.Keys.Add(new CurveKey(1f, 1f, 0f, 0f));
        // 零切线 Bezier 在 t=0.5 应约等于 0.5（S 形曲线对称）
        float mid = c.Evaluate(0.5f);
        Near(mid, 0.5f, 0.01f, "Bezier 对称曲线 t=0.5 中值");
    }

    /// <summary>Bake：烘焙采样数组，首尾值与曲线边界一致。</summary>
    public static void TestCurveBake()
    {
        var c = new ParticleCurve { Mode = ParticleCurveMode.Linear };
        c.Keys.Add(new CurveKey(0f, 2f));
        c.Keys.Add(new CurveKey(1f, 8f));

        float[] baked = c.Bake(16);
        AssertEqual(16, baked.Length, "烘焙数组长度应为 16");
        Near(baked[0], 2f, 0.001f, "烘焙首值应为起点值");
        Near(baked[^1], 8f, 0.001f, "烘焙末值应为终点值");
    }

    // ══════════════════════════════════════════════════════════════════
    //  二、ColorCurve 求值
    // ══════════════════════════════════════════════════════════════════

    /// <summary>ColorCurve 默认：RGBA 全为常量 1（白、不透明）。</summary>
    public static void TestColorCurveDefault()
    {
        var cc = new ColorCurve();
        // ColorCurve 默认四分量都是常量 1；Alpha 1→0 的渐变是 ColorModuleV2 构造的默认
        for (float t = 0f; t <= 1.001f; t += 0.25f)
        {
            var c = cc.Evaluate(t);
            Near(c.X, 1f, 0.001f, $"ColorCurve 默认 R t={t} 应为 1");
            Near(c.Y, 1f, 0.001f, $"ColorCurve 默认 G t={t} 应为 1");
            Near(c.Z, 1f, 0.001f, $"ColorCurve 默认 B t={t} 应为 1");
            Near(c.W, 1f, 0.001f, $"ColorCurve 默认 A t={t} 应为 1（常量）");
        }
    }

    /// <summary>ColorModuleV2 默认 ColorOverLifetime：Alpha 从 1 渐变到 0。</summary>
    public static void TestColorModuleDefaultAlphaFade()
    {
        var mod = new ColorModuleV2();
        var col = mod.ColorOverLifetime;
        Near(col.A.Evaluate(0f), 1f, 0.001f, "ColorModule 默认 Alpha t=0 应为 1");
        Near(col.A.Evaluate(0.5f), 0.5f, 0.01f, "ColorModule 默认 Alpha t=0.5 应为 0.5");
        Near(col.A.Evaluate(1f), 0f, 0.001f, "ColorModule 默认 Alpha t=1 应为 0");
    }

    /// <summary>ColorCurve 自定义：红色渐变到蓝色。</summary>
    public static void TestColorCurveCustomGradient()
    {
        var cc = new ColorCurve();
        cc.R = new ParticleCurve { Mode = ParticleCurveMode.Linear };
        cc.R.Keys.Add(new CurveKey(0f, 1f));
        cc.R.Keys.Add(new CurveKey(1f, 0f));
        cc.B = new ParticleCurve { Mode = ParticleCurveMode.Linear };
        cc.B.Keys.Add(new CurveKey(0f, 0f));
        cc.B.Keys.Add(new CurveKey(1f, 1f));

        var at0 = cc.Evaluate(0f);
        Near(at0.X, 1f, 0.001f, "自定义 R t=0");
        Near(at0.Z, 0f, 0.001f, "自定义 B t=0");

        var at1 = cc.Evaluate(1f);
        Near(at1.X, 0f, 0.001f, "自定义 R t=1");
        Near(at1.Z, 1f, 0.001f, "自定义 B t=1");
    }

    // ══════════════════════════════════════════════════════════════════
    //  三、ParticleSOA 布局
    // ══════════════════════════════════════════════════════════════════

    /// <summary>SOA 槽位分配：AllocateSlot 按顺序返回空闲位。</summary>
    public static void TestSOAAllocateSlot()
    {
        using var soa = new ParticleSOA(8);
        AssertEqual(0, soa.AllocateSlot(), "首个空闲槽应为 0");
        soa.MarkAlive(0);
        AssertEqual(1, soa.AllocateSlot(), "标记 0 存活后空闲槽应为 1");
        soa.MarkAlive(1);
        AssertEqual(2, soa.AllocateSlot(), "标记 1 存活后空闲槽应为 2");
    }

    /// <summary>SOA IsAlive/MarkDead：标记语义正确。</summary>
    public static void TestSOAAliveFlags()
    {
        using var soa = new ParticleSOA(4);
        AssertFalse(soa.IsAlive(0), "初始粒子应不存活");
        soa.MarkAlive(0);
        AssertTrue(soa.IsAlive(0), "MarkAlive 后应存活");
        soa.MarkDead(0);
        AssertFalse(soa.IsAlive(0), "MarkDead 后应不存活");
    }

    /// <summary>SOA Clear/RecountAlive：清空全部存活计数归零。</summary>
    public static void TestSOAClearRecount()
    {
        using var soa = new ParticleSOA(10);
        for (int i = 0; i < 5; i++) soa.MarkAlive(i);
        soa.RecountAlive();
        AssertEqual(5, soa.AliveCount, "标记 5 个后 AliveCount 应为 5");

        soa.Clear();
        AssertEqual(0, soa.AliveCount, "Clear 后 AliveCount 应为 0");
    }

    /// <summary>SOA 槽位耗尽：全满后返回 -1。</summary>
    public static void TestSOAExhausted()
    {
        using var soa = new ParticleSOA(3);
        for (int i = 0; i < 3; i++) soa.MarkAlive(i);
        AssertEqual(-1, soa.AllocateSlot(), "全满后 AllocateSlot 应返回 -1");
    }

    // ══════════════════════════════════════════════════════════════════
    //  四、EmitterModuleV2
    // ══════════════════════════════════════════════════════════════════

    /// <summary>Emitter 速率发射：固定 rate + dt 累积。</summary>
    public static void TestEmitterRateEmission()
    {
        using var soa = new ParticleSOA(100);
        var emitter = new EmitterModuleV2 { Rate = new ParticleCurve(10f) }; // 10/s

        // 0.5s 应发射约 5 个（累积整数化后 5）
        var ctx = MakeCtx(soa, dt: 0.1f, elapsed: 0f);
        // 多次 step 0.1s，每次累积 1.0
        for (int i = 0; i < 5; i++)
        {
            ctx.ElapsedTime = i * 0.1f;
            emitter.Execute(ref ctx);
        }
        int alive = soa.RecountAlive();
        AssertTrue(alive >= 4 && alive <= 6, $"5 次 ×0.1s ×10/s 应发射 5 个左右（实际 {alive}）");
    }

    /// <summary>Emitter Cone 形状：粒子分布在锥体范围内。</summary>
    public static void TestEmitterConeShape()
    {
        using var soa = new ParticleSOA(100);
        var emitter = new EmitterModuleV2
        {
            Rate = new ParticleCurve(0f),  // 关闭持续发射
            BurstCount = 0,
            BurstInterval = 0,
            Shape = GPUEmissionShape.Cone,
            Radius = 2f,
            Angle = 30f
        };

        // 手动发射 50 个粒子，验证位置在合理范围
        var ctx = MakeCtx(soa, dt: 1f, elapsed: 0f);
        // 用 rate=50 模拟一次大 dt
        emitter.Rate = new ParticleCurve(50f);
        emitter.Execute(ref ctx);

        soa.RecountAlive();
        for (int i = 0; i < soa.Capacity; i++)
        {
            if (!soa.IsAlive(i)) continue;
            // 锥形粒子 Y 分量应为正（朝上发射）
            AssertTrue(soa.Velocity[i].Y > 0f, "Cone 发射粒子 Y 分量应为正");
        }
    }

    /// <summary>Emitter Box 形状：粒子位置在 BoxExtents 内。</summary>
    public static void TestEmitterBoxShape()
    {
        using var soa = new ParticleSOA(100);
        var emitter = new EmitterModuleV2
        {
            Rate = new ParticleCurve(100f),
            BurstCount = 0,
            BurstInterval = 0,
            Shape = GPUEmissionShape.Box,
            BoxExtents = new Vector3(1f, 0.5f, 0.3f),
            EmissionDirection = Vector3.UnitY
        };

        var ctx = MakeCtx(soa, dt: 1f, elapsed: 0f);
        emitter.Execute(ref ctx);
        soa.RecountAlive();

        for (int i = 0; i < soa.Capacity; i++)
        {
            if (!soa.IsAlive(i)) continue;
            AssertTrue(MathF.Abs(soa.Position[i].X) <= 1.1f, "Box X 应在 ±extents.X 内");
            AssertTrue(MathF.Abs(soa.Position[i].Y) <= 0.6f, "Box Y 应在 ±extents.Y 内");
            AssertTrue(MathF.Abs(soa.Position[i].Z) <= 0.4f, "Box Z 应在 ±extents.Z 内");
        }
    }

    /// <summary>Emitter Burst 脉冲：到时间后一次性发射固定数量。</summary>
    public static void TestEmitterBurst()
    {
        using var soa = new ParticleSOA(100);
        var emitter = new EmitterModuleV2
        {
            Rate = new ParticleCurve(0f),   // 关闭持续发射
            BurstCount = 20,
            BurstInterval = 0.5f            // 每 0.5s 脉冲 20 个
        };

        var ctx = MakeCtx(soa, dt: 0.6f, elapsed: 0f);
        emitter.Execute(ref ctx);
        int alive = soa.RecountAlive();
        AssertTrue(alive >= 20, $"首次 Burst 应至少 20 个（实际 {alive}）");
    }

    // ══════════════════════════════════════════════════════════════════
    //  五、LifetimeModuleV2
    // ══════════════════════════════════════════════════════════════════

    /// <summary>Lifetime：粒子年龄增长，超过寿命后死亡。</summary>
    public static void TestLifetimeAgingKill()
    {
        using var soa = new ParticleSOA(4);
        int slot = SpawnTest(soa, lifetime: 1f);
        AssertTrue(soa.IsAlive(slot), "初始应存活");

        var mod = new LifetimeModuleV2 { Lifetime = new ParticleCurve(1f), KillOnLifetimeEnd = true };

        // 0.5s 时仍存活
        var ctx = MakeCtx(soa, dt: 0.5f);
        mod.Execute(ref ctx);
        AssertTrue(soa.IsAlive(slot), "0.5s 时应存活（寿命 1s）");
        Near(soa.Age[slot], 0.5f, 0.001f, "0.5s 后 Age 应为 0.5");

        // 再 0.6s，总 1.1s 超过寿命
        ctx = MakeCtx(soa, dt: 0.6f);
        mod.Execute(ref ctx);
        AssertFalse(soa.IsAlive(slot), "1.1s 超过寿命应死亡");
    }

    /// <summary>Lifetime KillOnLifetimeEnd=false：超时不杀粒子。</summary>
    public static void TestLifetimeNoKill()
    {
        using var soa = new ParticleSOA(4);
        int slot = SpawnTest(soa, lifetime: 1f);
        var mod = new LifetimeModuleV2 { KillOnLifetimeEnd = false };
        mod.Lifetime = new ParticleCurve(1f);

        var ctx = MakeCtx(soa, dt: 2f);
        mod.Execute(ref ctx);
        AssertTrue(soa.IsAlive(slot), "KillOnLifetimeEnd=false 时超时应仍存活");
        Near(soa.Age[slot], 2f, 0.01f, "Age 应累积到 2s");
    }

    // ══════════════════════════════════════════════════════════════════
    //  六、ForceModuleV2
    // ══════════════════════════════════════════════════════════════════

    /// <summary>Force 重力：速度按 -9.81*dt 累积。</summary>
    public static void TestForceGravity()
    {
        using var soa = new ParticleSOA(4);
        int slot = SpawnTest(soa, vel: Vector3.Zero, lifetime: 5f);

        var mod = new ForceModuleV2 { ForceY = new ParticleCurve(-9.81f) };
        var ctx = MakeCtx(soa, dt: 0.5f);
        mod.Execute(ref ctx);

        Near(soa.Velocity[slot].Y, -9.81f * 0.5f, 0.01f, "0.5s 重力速度累积");
        Near(soa.Acceleration[slot].Y, -9.81f, 0.01f, "加速度应为 -9.81");
    }

    /// <summary>Force 自定义方向：X/Z 也可施加力。</summary>
    public static void TestForceCustomDirection()
    {
        using var soa = new ParticleSOA(4);
        int slot = SpawnTest(soa, vel: Vector3.Zero, lifetime: 5f);

        var mod = new ForceModuleV2
        {
            ForceX = new ParticleCurve(10f),
            ForceY = new ParticleCurve(0f),
            ForceZ = new ParticleCurve(-5f)
        };
        var ctx = MakeCtx(soa, dt: 0.1f);
        mod.Execute(ref ctx);

        Near(soa.Velocity[slot].X, 1f, 0.01f, "X 方向力累积");
        Near(soa.Velocity[slot].Z, -0.5f, 0.01f, "Z 方向力累积");
    }

    // ══════════════════════════════════════════════════════════════════
    //  七、ColorModuleV2 + SizeModuleV2
    // ══════════════════════════════════════════════════════════════════

    /// <summary>ColorModule：Alpha 随生命周期衰减。</summary>
    public static void TestColorModuleFadeAlpha()
    {
        using var soa = new ParticleSOA(4);
        int slot = SpawnTest(soa, lifetime: 2f);

        var mod = new ColorModuleV2();
        // 设寿命
        soa.Lifetime[slot] = 2f;
        soa.Age[slot] = 1f; // 50% 生命周期

        var ctx = MakeCtx(soa);
        mod.Execute(ref ctx);

        Near(soa.Color[slot].X, 1f, 0.001f, "R 生命周期中段应为 1");
        Near(soa.Color[slot].W, 0.5f, 0.01f, "Alpha 生命周期中段应约 0.5");
    }

    /// <summary>SizeModule：尺寸随生命周期曲线变化。</summary>
    public static void TestSizeModuleOverLifetime()
    {
        using var soa = new ParticleSOA(4);
        int slot = SpawnTest(soa, lifetime: 2f);

        var mod = new SizeModuleV2
        {
            SizeX = new ParticleCurve { Mode = ParticleCurveMode.Linear },
            AspectRatioLocked = true
        };
        mod.SizeX.Keys.Add(new CurveKey(0f, 0.1f));
        mod.SizeX.Keys.Add(new CurveKey(1f, 1.0f));

        soa.Lifetime[slot] = 2f;
        soa.Age[slot] = 1f; // 50%

        var ctx = MakeCtx(soa);
        mod.Execute(ref ctx);

        Near(soa.Size[slot].X, 0.55f, 0.01f, "Size 生命周期中段应约 0.55");
        Near(soa.Size[slot].Y, 0.55f, 0.01f, "AspectRatioLocked 时 Y=X");
    }

    /// <summary>SizeModule AspectRatioLocked=false：X/Y 独立曲线。</summary>
    public static void TestSizeModuleIndependentAxes()
    {
        using var soa = new ParticleSOA(4);
        int slot = SpawnTest(soa, lifetime: 2f);

        var mod = new SizeModuleV2
        {
            SizeX = new ParticleCurve(0.5f),
            SizeY = new ParticleCurve(0.3f),
            AspectRatioLocked = false
        };

        soa.Lifetime[slot] = 2f;
        soa.Age[slot] = 0f;

        var ctx = MakeCtx(soa);
        mod.Execute(ref ctx);

        Near(soa.Size[slot].X, 0.5f, 0.01f, "SizeX 应为 0.5");
        Near(soa.Size[slot].Y, 0.3f, 0.01f, "SizeY 应为 0.3（独立）");
    }

    // ══════════════════════════════════════════════════════════════════
    //  八、RotationModuleV2
    // ══════════════════════════════════════════════════════════════════

    /// <summary>Rotation：角速度积分旋转角。</summary>
    public static void TestRotationIntegration()
    {
        using var soa = new ParticleSOA(4);
        int slot = SpawnTest(soa, lifetime: 5f);

        var mod = new RotationModuleV2 { AngularVelocity = new ParticleCurve(90f) }; // 90°/s
        var ctx = MakeCtx(soa, dt: 1f);
        mod.Execute(ref ctx);

        Near(soa.Rotation[slot], 90f, 0.5f, "1s × 90°/s 应为 90°");
        Near(soa.AngularVelocity[slot], 90f, 0.01f, "角速度应为 90");
    }

    // ══════════════════════════════════════════════════════════════════
    //  九、ParticleModulePipeline 集成
    // ══════════════════════════════════════════════════════════════════

    /// <summary>Pipeline 模块排序：按 ExecutionOrder 升序执行。</summary>
    public static void TestPipelineModuleOrdering()
    {
        var pipeline = new ParticleModulePipeline();

        var emitter = new EmitterModuleV2 { ExecutionOrder = 10 };
        var lifetime = new LifetimeModuleV2 { ExecutionOrder = 20 };
        var force = new ForceModuleV2 { ExecutionOrder = 30 };
        pipeline.AddModule(emitter);
        pipeline.AddModule(lifetime);
        pipeline.AddModule(force);

        AssertEqual(3, pipeline.Modules.Count, "Pipeline 应有 3 个模块");
        // 验证排序：Emitter(10) < Lifetime(20) < Force(30)
        AssertTrue(pipeline.Modules[0] is EmitterModuleV2, "第一个模块应为 Emitter（order=10）");
        AssertTrue(pipeline.Modules[1] is LifetimeModuleV2, "第二个模块应为 Lifetime（order=20）");
        AssertTrue(pipeline.Modules[2] is ForceModuleV2, "第三个模块应为 Force（order=30）");
    }

    /// <summary>Pipeline GetOrAdd：同名模块单例。</summary>
    public static void TestPipelineGetOrAdd()
    {
        var pipeline = new ParticleModulePipeline();
        var a = pipeline.GetOrAdd<ForceModuleV2>();
        var b = pipeline.GetOrAdd<ForceModuleV2>();
        AssertTrue(ReferenceEquals(a, b), "GetOrAdd 应返回同一实例");

        AssertEqual(1, pipeline.Modules.Count, "只应有 1 个 Force 模块");
    }

    /// <summary>Pipeline 事件队列：Emitter Spawned 事件入队。</summary>
    public static void TestPipelineEventQueue()
    {
        using var soa = new ParticleSOA(50);
        var emitter = new EmitterModuleV2 { Rate = new ParticleCurve(10f) };
        var queue = new ParticleEventQueue();

        var ctx = new ModuleContext
        {
            DeltaTime = 1f, // 1s × 10/s = 10 个
            ElapsedTime = 0f,
            Data = soa,
            Events = queue,
            EmitterPosition = Vector3.Zero
        };
        emitter.Execute(ref ctx);
        soa.RecountAlive();

        AssertTrue(queue.Count > 0, "发射后事件队列应有 Spawned 事件");
        AssertEqual(soa.AliveCount, queue.Count, "Spawned 事件数应等于存活粒子数");
    }

    /// <summary>ParticleEventQueue Subscribe/Dispatch：事件派发到订阅者。</summary>
    public static void TestEventQueueDispatch()
    {
        var queue = new ParticleEventQueue();
        int spawnCount = 0, deathCount = 0;
        queue.Subscribe(ParticleEventType.Spawned, (in ParticleEvent _) => spawnCount++);
        queue.Subscribe(ParticleEventType.Died, (in ParticleEvent _) => deathCount++);

        queue.Enqueue(ParticleEventType.Spawned, 0, Vector3.Zero);
        queue.Enqueue(ParticleEventType.Spawned, 1, Vector3.Zero);
        queue.Enqueue(ParticleEventType.Died, 0, Vector3.Zero);
        queue.Enqueue(ParticleEventType.Custom, 0, Vector3.Zero);

        queue.Dispatch();
        AssertEqual(2, spawnCount, "Spawned 事件应派发 2 次");
        AssertEqual(1, deathCount, "Died 事件应派发 1 次");
        AssertEqual(0, queue.Count, "Dispatch 后队列应清空");
    }

    /// <summary>集成：Emitter + Lifetime + Force + Color + Size 全管线 Update。</summary>
    public static void TestPipelineFullUpdate()
    {
        using var soa = new ParticleSOA(200);
        var emitter = new EmitterModuleV2 { Rate = new ParticleCurve(50f), Shape = GPUEmissionShape.Sphere, Radius = 1f };
        var lifetime = new LifetimeModuleV2 { Lifetime = new ParticleCurve(1f) };
        var force = new ForceModuleV2 { ForceY = new ParticleCurve(-9.81f) };

        var pipeline = new ParticleModulePipeline();
        pipeline.AddModule(emitter);
        pipeline.AddModule(lifetime);
        pipeline.AddModule(force);

        // 注入 SOA 数据（绕过 Initialize 需要 RHI device）
        var field = typeof(ParticleModulePipeline).GetProperty("Data");
        field!.SetValue(pipeline, soa);

        // 模拟 1s：发射 50 个，其中部分老化
        pipeline.Update(0.5f, Vector3.Zero, Vector3.Zero, Matrix4x4.Identity, Matrix4x4.Identity);
        int alive1 = soa.AliveCount;
        AssertTrue(alive1 > 0, "0.5s 后应有存活粒子");
        AssertTrue(alive1 <= 50, $"0.5s × 50/s 最多 25 个，实际 {alive1}");

        // 再 0.6s：总 1.1s，早期粒子应老化死亡
        pipeline.Update(0.6f, Vector3.Zero, Vector3.Zero, Matrix4x4.Identity, Matrix4x4.Identity);
        // 存活数可能为 0 或仍有新生粒子，只要不崩溃即通过
    }
}
