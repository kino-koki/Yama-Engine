// ============================================================================================
//  CelParticleSystem2D.cs
//
//  yama引擎 (YamaEngine) 赛璐璐 / 二次元 2D 子系统 —— 2D 粒子系统
//
//  赛璐璐风格的 2D 粒子（樱花、魔法光点、粉尘、雨滴等）：
//    · 发射器按速率/一次爆发发射，粒子池复用，避免 GC 抖动
//    · 颜色/尺寸/速度随生命周期插值
//    · 可附加赛璐璐材质（色阶量化/网点），使粒子与角色风格统一
//
//  纯数据/逻辑层：只产出 CelSpriteData 供 CelShading2DSystem 渲染，不含 GPU 逻辑。
//  命名空间：YamaEngine.Cel2D  ·  目标框架：net10.0
// ============================================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.Cel2D
{
    /// <summary>单个 2D 粒子。</summary>
    public struct CelParticle2D
    {
        public Vector2 Position;
        public Vector2 Velocity;
        public float Life;          // 已存活时间
        public float MaxLife;
        public float Size;          // 当前尺寸
        public float StartSize;
        public float EndSize;
        public float Rotation;
        public float RotationSpeed;
        public Vector4 Color;       // 当前颜色
        public Vector4 StartColor;
        public Vector4 EndColor;
        public Vector2 Gravity;
        public float Drag;
        public bool Active => Life < MaxLife;
    }

    /// <summary>发射形状。</summary>
    public enum CelEmitShape2D { Point, Circle, Box, Line }

    /// <summary>粒子发射器配置。</summary>
    public sealed class CelParticleEmitter2D
    {
        /// <summary>每秒发射数。</summary>
        public float EmissionRate = 60f;
        public float Lifetime = 1.5f;
        public float StartSpeed = 60f;
        public float EndSpeed = 0f;
        public float StartSize = 8f;
        public float EndSize = 2f;
        public Vector4 StartColor = Vector4.One;
        public Vector4 EndColor = new(1f, 1f, 1f, 0f);
        /// <summary>发射方向（角度，弧度）；null 表示全向。</summary>
        public float? Direction;
        /// <summary>扩散角（弧度）。</summary>
        public float Spread = 0f;
        public Vector2 Gravity;
        public float Drag = 0f;
        public float RotationSpeed = 0f;
        public CelEmitShape2D Shape = CelEmitShape2D.Point;
        public Vector2 ShapeRadius = new(0f, 0f);   // Circle=半径, Box=半宽高, Line=半长
        public bool Loop = true;

        // 运行时累计（内部）
        internal float _accum;
    }

    /// <summary>
    /// 2D 赛璐璐粒子系统：发射 + 更新 + 转为渲染精灵。
    /// </summary>
    public sealed class CelParticleSystem2D
    {
        private readonly List<CelParticle2D> _particles = new();
        private readonly List<CelSpriteData> _scratch = new();

        /// <summary>粒子数量上限（防止池爆炸）。</summary>
        public int MaxParticles { get; set; } = 4096;

        /// <summary>全局色阶量化（0 = 禁用，>0 叠加到粒子材质）。</summary>
        public int QuantizeBands { get; set; } = 0;

        public IReadOnlyList<CelParticle2D> Particles => _particles;
        public int ActiveCount
        {
            get
            {
                int n = 0;
                foreach (var p in _particles) if (p.Active) n++;
                return n;
            }
        }

        /// <summary>持续发射（按速率）。</summary>
        public void Emit(in CelParticleEmitter2D emitter, float deltaTime)
        {
            if (!emitter.Loop && emitter._accum >= emitter.Lifetime) return;
            emitter._accum += deltaTime;
            float interval = emitter.EmissionRate > 0 ? 1f / emitter.EmissionRate : float.MaxValue;
            while (emitter._accum >= interval)
            {
                emitter._accum -= interval;
                SpawnOne(emitter);
            }
        }

        /// <summary>一次爆发 N 个。</summary>
        public void EmitBurst(in CelParticleEmitter2D emitter, int count)
        {
            for (int i = 0; i < count; i++) SpawnOne(emitter);
        }

        private void SpawnOne(in CelParticleEmitter2D e)
        {
            if (_particles.Count >= MaxParticles) return;

            var p = new CelParticle2D
            {
                Position = SamplePosition(e),
                Life = 0,
                MaxLife = MathF.Max(0.01f, e.Lifetime),
                StartSize = e.StartSize,
                EndSize = e.EndSize,
                Size = e.StartSize,
                StartColor = e.StartColor,
                EndColor = e.EndColor,
                Color = e.StartColor,
                Gravity = e.Gravity,
                Drag = e.Drag,
                RotationSpeed = e.RotationSpeed,
                Rotation = 0
            };

            // 方向 + 扩散
            float ang;
            if (e.Direction.HasValue)
                ang = e.Direction.Value + (RandFloat() * 2f - 1f) * e.Spread * 0.5f;
            else
                ang = RandFloat() * MathF.Tau;
            float speed = Lerp(e.StartSpeed, e.EndSpeed, 0f);
            p.Velocity = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * speed;

            _particles.Add(p);
        }

        /// <summary>推进所有粒子。</summary>
        public void Update(float deltaTime)
        {
            for (int i = _particles.Count - 1; i >= 0; i--)
            {
                var p = _particles[i];
                if (!p.Active)
                {
                    _particles.RemoveAt(i);
                    continue;
                }

                p.Life += deltaTime;
                if (p.Life >= p.MaxLife) { _particles.RemoveAt(i); continue; }

                // 速度积分 + 阻力 + 重力
                p.Velocity += p.Gravity * deltaTime;
                if (p.Drag > 0)
                    p.Velocity *= MathF.Max(0f, 1f - p.Drag * deltaTime);
                p.Position += p.Velocity * deltaTime;

                // 生命周期插值
                float t = Clamp01(p.Life / p.MaxLife);
                p.Size = Lerp(p.StartSize, p.EndSize, t);
                p.Color = Lerp(p.StartColor, p.EndColor, t);
                p.Rotation += p.RotationSpeed * deltaTime;

                _particles[i] = p;
            }
        }

        /// <summary>转为精灵渲染列表（供 CelShading2DSystem.Draw）。</summary>
        public List<CelSpriteData> ToSpriteData(string texturePath, CelToonMaterial? baseMaterial = null)
        {
            _scratch.Clear();
            if (string.IsNullOrEmpty(texturePath)) return _scratch;

            var mat = baseMaterial ?? new CelToonMaterial { EnableOutline = false };
            if (QuantizeBands > 0)
            {
                mat.BandCount = QuantizeBands;
                mat.EnableBanding = true;
            }

            foreach (var p in _particles)
            {
                if (!p.Active) continue;
                _scratch.Add(new CelSpriteData
                {
                    TexturePath = texturePath,
                    Position = p.Position,
                    Size = new Vector2(p.Size, p.Size),
                    SourceRect = new Vector4(0, 0, 1, 1),
                    Tint = p.Color,
                    Z = 5f,
                    Material = mat
                });
            }
            return _scratch;
        }

        public void Clear() => _particles.Clear();

        // ── 发射位置采样 ──
        private Vector2 SamplePosition(in CelParticleEmitter2D e)
        {
            switch (e.Shape)
            {
                case CelEmitShape2D.Circle:
                    float a = RandFloat() * MathF.Tau;
                    float r = MathF.Sqrt(RandFloat()) * MathF.Max(0, e.ShapeRadius.X);
                    return new Vector2(MathF.Cos(a) * r, MathF.Sin(a) * r);
                case CelEmitShape2D.Box:
                    return new Vector2(
                        (RandFloat() * 2f - 1f) * e.ShapeRadius.X,
                        (RandFloat() * 2f - 1f) * e.ShapeRadius.Y);
                case CelEmitShape2D.Line:
                    return new Vector2((RandFloat() * 2f - 1f) * e.ShapeRadius.X, 0);
                default:
                    return Vector2.Zero;
            }
        }

        // ── 工具 ──
        private static readonly Random _rng = new();
        private static float RandFloat() => (float)_rng.NextDouble();
        private static float Clamp01(float v) => v < 0 ? 0 : (v > 1 ? 1 : v);
        private static float Lerp(float a, float b, float t) => a + (b - a) * t;
        private static Vector4 Lerp(Vector4 a, Vector4 b, float t) => new(
            a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t,
            a.Z + (b.Z - a.Z) * t, a.W + (b.W - a.W) * t);
    }
}
