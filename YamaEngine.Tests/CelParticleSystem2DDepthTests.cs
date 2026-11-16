// CelParticleSystem2DDepthTests.cs — Unity5 2D ParticleSystem 等价物深度验证
// 覆盖:
//   * CelParticle2D: 默认值 + Active 属性
//   * CelParticleEmitter2D: 默认值
//   * CelParticleSystem2D: EmitBurst/Emit/Update/ToSpriteData/Clear
//                         ActiveCount/MaxParticles/QuantizeBands
//                         Gravity/Drag/Color 插值/Size 插值

using System;
using System.Linq;
using System.Numerics;
using YamaEngine.Cel2D;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class CelParticleSystem2DDepthTests
    {
        private const float Eps = 1e-3f;

        private static CelParticleEmitter2D MakeEmitter() => new CelParticleEmitter2D
        {
            EmissionRate = 60f,
            Lifetime = 1.0f,
            StartSpeed = 100f,
            EndSpeed = 0f,
            StartSize = 8f,
            EndSize = 2f,
            StartColor = Vector4.One,
            EndColor = new Vector4(1, 1, 1, 0),
        };

        // ── 1. CelParticle2D 默认值 + Active ──
        public static void TestParticleDefaults()
        {
            var p = new CelParticle2D();
            AssertEqual(Vector2.Zero, p.Position, "默认 Position=Zero");
            AssertEqual(0f, p.Life, "默认 Life=0");
            AssertEqual(0f, p.MaxLife, "默认 MaxLife=0");
            AssertTrue(!p.Active, "Life=MaxLife=0 → 非活跃");

            p.MaxLife = 1f;
            AssertTrue(p.Active, "Life<MaxLife → 活跃");

            p.Life = 1f;
            AssertTrue(!p.Active, "Life>=MaxLife → 非活跃");
        }

        // ── 2. Emitter 默认值 ──
        public static void TestEmitterDefaults()
        {
            var e = new CelParticleEmitter2D();
            AssertEqual(60f, e.EmissionRate, "默认 EmissionRate=60");
            AssertEqual(1.5f, e.Lifetime, "默认 Lifetime=1.5");
            AssertEqual(8f, e.StartSize, "默认 StartSize=8");
            AssertEqual(2f, e.EndSize, "默认 EndSize=2");
            AssertEqual(CelEmitShape2D.Point, e.Shape, "默认 Shape=Point");
            AssertTrue(e.Loop, "默认 Loop=true");
            AssertTrue(!e.Direction.HasValue, "默认 Direction=null（全向）");
        }

        // ── 3. EmitBurst 产生 N 个粒子 ──
        public static void TestEmitBurst()
        {
            var ps = new CelParticleSystem2D();
            AssertEqual(0, ps.ActiveCount, "初始 0 粒子");

            ps.EmitBurst(MakeEmitter(), 10);
            AssertEqual(10, ps.Particles.Count, "Burst(10) 产生 10");
            AssertEqual(10, ps.ActiveCount, "新粒子全部活跃");
        }

        // ── 4. Emit 按速率产生 ──
        public static void TestEmitRate()
        {
            var ps = new CelParticleSystem2D();
            var e = MakeEmitter();
            e.EmissionRate = 100f; // 每秒 100 个
            e.StartSpeed = 0f; // 速度 0 避免飞出屏幕

            ps.Emit(e, 0.1f); // 期望产生 ≈10 个（可能有 ±1 抖动）
            int emitted = ps.Particles.Count;
            AssertTrue(emitted >= 8 && emitted <= 12,
                $"0.1s 内 EmissionRate=100 应发射≈10，实际 {emitted}");
        }

        // ── 5. Update 推进 Life 并移除过期 ──
        public static void TestUpdateRemovesExpired()
        {
            var ps = new CelParticleSystem2D();
            var e = MakeEmitter();
            e.Lifetime = 0.1f;
            e.StartSpeed = 0;

            ps.EmitBurst(e, 5);
            AssertEqual(5, ps.ActiveCount, "初始 5 活跃");

            ps.Update(0.05f);
            AssertEqual(5, ps.ActiveCount, "Life=0.05 < MaxLife=0.1 → 仍活跃");

            ps.Update(0.1f); // 累计 0.15 > 0.1 → 过期
            AssertEqual(0, ps.ActiveCount, "超过 Lifetime 后全部移除");
        }

        // ── 6. Size 生命周期插值 ──
        public static void TestSizeInterpolation()
        {
            var ps = new CelParticleSystem2D();
            var e = MakeEmitter();
            e.Lifetime = 1.0f;
            e.StartSize = 10f;
            e.EndSize = 2f;
            e.StartSpeed = 0;

            ps.EmitBurst(e, 1);
            AssertEqual(10f, ps.Particles[0].Size, "t=0 时 Size=StartSize");

            ps.Update(0.5f); // t=0.5
            float expected = 10f + (2f - 10f) * 0.5f; // Lerp=6
            AssertTrue(MathF.Abs(ps.Particles[0].Size - expected) < Eps,
                $"t=0.5 时 Size≈{expected}，实际 {ps.Particles[0].Size}");
        }

        // ── 7. Color 生命周期插值 ──
        public static void TestColorInterpolation()
        {
            var ps = new CelParticleSystem2D();
            var e = MakeEmitter();
            e.Lifetime = 1.0f;
            e.StartColor = new Vector4(1, 0, 0, 1); // 红
            e.EndColor = new Vector4(0, 0, 1, 0);   // 蓝+透明
            e.StartSpeed = 0;

            ps.EmitBurst(e, 1);
            AssertTrue(MathF.Abs(ps.Particles[0].Color.X - 1f) < Eps, "t=0 颜色=红");

            ps.Update(0.5f);
            var c = ps.Particles[0].Color;
            AssertTrue(MathF.Abs(c.X - 0.5f) < Eps, "t=0.5 R=0.5");
            AssertTrue(MathF.Abs(c.Z - 0.5f) < Eps, "t=0.5 B=0.5");
            AssertTrue(MathF.Abs(c.W - 0.5f) < Eps, "t=0.5 A=0.5");
        }

        // ── 8. Gravity 影响速度 ──
        public static void TestGravityAffectsVelocity()
        {
            var ps = new CelParticleSystem2D();
            var e = MakeEmitter();
            e.Lifetime = 2.0f;
            e.StartSpeed = 0;
            e.Direction = -MathF.PI / 2f; // 向上
            e.Gravity = new Vector2(0, 9.8f); // 向下重力

            ps.EmitBurst(e, 1);
            AssertEqual(0f, ps.Particles[0].Velocity.Y, "初始速度=0");

            ps.Update(1f);
            float vy = ps.Particles[0].Velocity.Y;
            AssertTrue(MathF.Abs(vy - 9.8f) < 0.1f,
                $"重力加速 1s 后 vy≈9.8，实际 {vy}");
        }

        // ── 9. Drag 减速 ──
        public static void TestDragDecelerates()
        {
            var ps = new CelParticleSystem2D();
            var e = MakeEmitter();
            e.Lifetime = 5f;
            e.StartSpeed = 100f;
            e.Direction = 0; // +X
            e.Drag = 2f;

            ps.EmitBurst(e, 1);
            float v0 = ps.Particles[0].Velocity.X;
            AssertEqual(100f, v0, "初始速度=100");

            ps.Update(0.5f);
            float v1 = ps.Particles[0].Velocity.X;
            AssertTrue(v1 < v0, $"Drag 后速度降低：{v0}→{v1}");
        }

        // ── 10. RotationSpeed 累积旋转 ──
        public static void TestRotationSpeed()
        {
            var ps = new CelParticleSystem2D();
            var e = MakeEmitter();
            e.Lifetime = 2f;
            e.RotationSpeed = MathF.PI; // 半圈/秒
            e.StartSpeed = 0;

            ps.EmitBurst(e, 1);
            AssertEqual(0f, ps.Particles[0].Rotation, "初始 Rotation=0");

            ps.Update(1f);
            AssertTrue(MathF.Abs(ps.Particles[0].Rotation - MathF.PI) < 0.01f,
                $"1s 后 Rotation≈π，实际 {ps.Particles[0].Rotation}");
        }

        // ── 11. MaxParticles 上限 ──
        public static void TestMaxParticlesCap()
        {
            var ps = new CelParticleSystem2D { MaxParticles = 5 };
            ps.EmitBurst(MakeEmitter(), 10);
            AssertEqual(5, ps.Particles.Count, "超上限时截断到 MaxParticles");
        }

        // ── 12. Clear 清空 ──
        public static void TestClear()
        {
            var ps = new CelParticleSystem2D();
            ps.EmitBurst(MakeEmitter(), 10);
            AssertEqual(10, ps.ActiveCount, "Burst 后 10");
            ps.Clear();
            AssertEqual(0, ps.ActiveCount, "Clear 后 0");
        }

        // ── 13. ToSpriteData 空 path 返回空 ──
        public static void TestToSpriteDataEmptyPath()
        {
            var ps = new CelParticleSystem2D();
            ps.EmitBurst(MakeEmitter(), 5);
            var sprites = ps.ToSpriteData("");
            AssertEqual(0, sprites.Count, "空 texturePath 返回空列表");
        }

        // ── 14. ToSpriteData 生成对应精灵 ──
        public static void TestToSpriteDataGenerates()
        {
            var ps = new CelParticleSystem2D();
            var e = MakeEmitter();
            e.Lifetime = 10f;
            e.StartSpeed = 0;

            ps.EmitBurst(e, 3);
            var sprites = ps.ToSpriteData("spark.png");
            AssertEqual(3, sprites.Count, "3 粒子 → 3 精灵");
            AssertEqual("spark.png", sprites[0].TexturePath, "纹理路径透传");
            AssertTrue(MathF.Abs(sprites[0].Size.X - 8f) < Eps, "Size=StartSize=8");
            AssertTrue(sprites[0].Material != null, "Material 默认非空");
            AssertTrue(!sprites[0].Material!.EnableOutline, "默认禁用描边");
        }

        // ── 15. QuantizeBands 影响材质 ──
        public static void TestQuantizeBands()
        {
            var ps = new CelParticleSystem2D { QuantizeBands = 4 };
            var e = MakeEmitter();
            e.Lifetime = 10f;
            e.StartSpeed = 0;

            ps.EmitBurst(e, 1);
            var sprites = ps.ToSpriteData("spark.png");
            AssertTrue(sprites[0].Material!.EnableBanding, "QuantizeBands>0 启用 Banding");
            AssertEqual(4, sprites[0].Material!.BandCount, "BandCount=QuantizeBands");
        }

        // ── 16. NonLoop 发射器超时后不再发射 ──
        public static void TestNonLoopEmitterTimeout()
        {
            var ps = new CelParticleSystem2D();
            var e = MakeEmitter();
            e.Loop = false;
            e.Lifetime = 0.5f;
            e.EmissionRate = 100f;

            // 累计 1s（远超 Lifetime=0.5），所有 Lifetime=0.5 粒子也已死亡
            for (int i = 0; i < 60; i++) { ps.Emit(e, 1f / 60f); ps.Update(1f / 60f); }
            AssertEqual(0, ps.ActiveCount,
                "Lifetime=0.5 粒子在 1s 后全部死亡，且 emitter 停止");

            // 继续发射 1s，仍无粒子（guard 永久阻塞）
            for (int i = 0; i < 60; i++) { ps.Emit(e, 1f / 60f); ps.Update(1f / 60f); }
            AssertEqual(0, ps.ActiveCount, "emitter 停止后无新粒子");
        }

        // ── 17. Direction+Spread 方向控制 ──
        public static void TestDirectionSpread()
        {
            var ps = new CelParticleSystem2D();
            var e = MakeEmitter();
            e.Lifetime = 2f;
            e.StartSpeed = 100f;
            e.Direction = 0f; // +X 方向
            e.Spread = 0f;    // 无扩散 → 全部 +X

            ps.EmitBurst(e, 5);
            foreach (var p in ps.Particles)
            {
                AssertTrue(p.Velocity.X > 90f,
                    $"Direction=0 Spread=0 → 全部 +X，实际 vx={p.Velocity.X}");
            }
        }

        // ── 18. Shape=Circle 采样在半径内 ──
        public static void TestShapeCircle()
        {
            var ps = new CelParticleSystem2D();
            var e = MakeEmitter();
            e.Lifetime = 2f;
            e.Shape = CelEmitShape2D.Circle;
            e.ShapeRadius = new Vector2(50, 0);
            e.StartSpeed = 0;

            ps.EmitBurst(e, 20);
            foreach (var p in ps.Particles)
            {
                float dist = p.Position.Length();
                AssertTrue(dist <= 50.1f,
                    $"Circle 采样应在半径 50 内，实际 {dist}");
            }
        }

        // ── 19. Shape=Box 采样在框内 ──
        public static void TestShapeBox()
        {
            var ps = new CelParticleSystem2D();
            var e = MakeEmitter();
            e.Lifetime = 2f;
            e.Shape = CelEmitShape2D.Box;
            e.ShapeRadius = new Vector2(30, 20);
            e.StartSpeed = 0;

            ps.EmitBurst(e, 20);
            foreach (var p in ps.Particles)
            {
                AssertTrue(MathF.Abs(p.Position.X) <= 30.1f,
                    $"Box X 在 [-30,30] 内，实际 {p.Position.X}");
                AssertTrue(MathF.Abs(p.Position.Y) <= 20.1f,
                    $"Box Y 在 [-20,20] 内，实际 {p.Position.Y}");
            }
        }

        // ── 20. NonLoop 累计 Lifetime 后永久停止 ──
        public static void TestNonLoopAccumTimeout()
        {
            var ps = new CelParticleSystem2D();
            var e = MakeEmitter();
            e.Loop = false;
            e.Lifetime = 1.0f;
            e.EmissionRate = 50f; // 1s 内 ≈50 个

            // Phase 1: 累计 1s，期间在发射
            for (int i = 0; i < 60; i++) { ps.Emit(e, 1f / 60f); ps.Update(1f / 60f); }
            AssertTrue(ps.ActiveCount > 0, "Lifetime 内应有活跃粒子");

            // Phase 2: 再跑 3s，足够 emitter 停止 + 全部粒子死亡（Lifetime=1s，边界粒子最多活到 t=2s）
            for (int i = 0; i < 180; i++) { ps.Emit(e, 1f / 60f); ps.Update(1f / 60f); }
            AssertEqual(0, ps.ActiveCount,
                "NonLoop 发射器停止后所有粒子最终死亡");
        }
    }
}
