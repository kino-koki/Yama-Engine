// =============================================================================
//  CPUParticles2D.cs
//  yama引擎 - 2D 粒子（对应 Godot 的 CPUParticles2D）
//
//  对应 Godot 3 的 2D 粒子语义：
//    * CPUParticles2D：CPU 驱动的 2D 粒子发射器
//    * emitting：是否发射（false 时已有粒子继续走完生命周期）
//    * amount / lifetime / one_shot / explosiveness
//    * direction + spread + initial_velocity：发射方向与初速
//    * gravity：粒子重力（每步 velocity += gravity * dt）
//    * color / color_ramp：颜色随生命周期渐变
//    * 连续模式：rate = amount / lifetime，按 rate*dt 发射
//    * one_shot + explosiveness=1：一次性全发射后停止
// =============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.UI
{
    /// <summary>单个 2D 粒子。</summary>
    public struct Particle2D
    {
        /// <summary>世界坐标位置。</summary>
        public Vector2 Position;

        /// <summary>速度。</summary>
        public Vector2 Velocity;

        /// <summary>已存活时间。</summary>
        public float Life;

        /// <summary>总寿命（秒）。</summary>
        public float MaxLife;

        /// <summary>当前颜色（随生命周期渐变）。</summary>
        public Vector4 Color;

        /// <summary>是否存活。</summary>
        public bool Alive;
    }

    /// <summary>颜色渐变点（生命周期归一化 t → RGBA）。</summary>
    public sealed class ColorRamp
    {
        private readonly List<(float T, Vector4 Color)> _stops = new();

        /// <summary>添加渐变点（t 归一化 0..1）。</summary>
        public void AddStop(float t, Vector4 color)
        {
            _stops.Add((Math.Clamp(t, 0f, 1f), color));
            _stops.Sort((a, b) => a.T.CompareTo(b.T));
        }

        /// <summary>采样颜色（线性插值）。</summary>
        public Vector4 Sample(float t)
        {
            if (_stops.Count == 0) return new Vector4(1f, 1f, 1f, 1f);
            if (_stops.Count == 1) return _stops[0].Color;
            t = Math.Clamp(t, 0f, 1f);

            if (t <= _stops[0].T) return _stops[0].Color;
            if (t >= _stops[^1].T) return _stops[^1].Color;

            for (int i = 0; i < _stops.Count - 1; i++)
            {
                var a = _stops[i];
                var b = _stops[i + 1];
                if (t >= a.T && t <= b.T)
                {
                    float local = (b.T - a.T) < 1e-6f ? 0f : (t - a.T) / (b.T - a.T);
                    return new Vector4(
                        a.Color.X + (b.Color.X - a.Color.X) * local,
                        a.Color.Y + (b.Color.Y - a.Color.Y) * local,
                        a.Color.Z + (b.Color.Z - a.Color.Z) * local,
                        a.Color.W + (b.Color.W - a.Color.W) * local);
                }
            }
            return _stops[^1].Color;
        }

        /// <summary>渐变点数。</summary>
        public int StopCount => _stops.Count;
    }

    /// <summary>
    /// 2D 粒子发射器（对应 Godot CPUParticles2D）。发射、积分、寿命管理。
    /// </summary>
    public sealed class CPUParticles2D : Node2D
    {
        /// <summary>是否发射中（false 时已有粒子继续走完生命周期，不再新增）。</summary>
        public bool Emitting { get; set; } = true;

        /// <summary>目标最大粒子数。</summary>
        public int Amount { get; set; } = 8;

        /// <summary>粒子寿命（秒）。</summary>
        public float Lifetime { get; set; } = 1f;

        /// <summary>一次性发射模式（对应 Godot one_shot）。</summary>
        public bool OneShot { get; set; }

        /// <summary>爆发程度（0=连续，1=一次性全发射，对应 Godot explosiveness）。</summary>
        public float Explosiveness { get; set; }

        /// <summary>发射方向（单位向量，对应 Godot direction）。</summary>
        public Vector2 Direction { get; set; } = new(1f, 0f);

        /// <summary>发射锥角（度，0=直线，对应 Godot spread）。</summary>
        public float Spread { get; set; }

        /// <summary>初速度范围（对应 Godot initial_velocity_min/max）。</summary>
        public float InitialVelocityMin { get; set; } = 50f;
        public float InitialVelocityMax { get; set; } = 50f;

        /// <summary>粒子重力（对应 Godot gravity）。</summary>
        public Vector2 Gravity { get; set; } = new(0f, 980f);

        /// <summary>粒子基色（对应 Godot color）。</summary>
        public Vector4 Color { get; set; } = new(1f, 1f, 1f, 1f);

        /// <summary>颜色渐变（对应 Godot color_ramp），null 时用固定 Color。</summary>
        public ColorRamp? ColorRamp { get; set; }

        /// <summary>存活粒子数。</summary>
        public int AliveCount { get; private set; }

        /// <summary>累计发射数（测试用）。</summary>
        public int TotalEmitted { get; private set; }

        /// <summary>是否所有 one-shot 粒子已发射完。</summary>
        public bool OneShotEmitted { get; private set; }

        private readonly Particle2D[] _particles;
        private float _emissionAccumulator;
        private bool _oneShotStarted;

        public CPUParticles2D(string name = "CPUParticles2D") : base(name)
        {
            _particles = new Particle2D[1024]; // 上限 1024，够测试用
        }

        /// <summary>取存活粒子列表（测试/渲染用）。</summary>
        public IReadOnlyList<Particle2D> GetParticles()
        {
            var list = new List<Particle2D>();
            for (int i = 0; i < _particles.Length; i++)
            {
                if (_particles[i].Alive) list.Add(_particles[i]);
            }
            return list;
        }

        /// <summary>发射单个粒子（内部，从发射器位置）。</summary>
        private void EmitOne()
        {
            for (int i = 0; i < _particles.Length; i++)
            {
                if (_particles[i].Alive) continue;

                // 方向 + spread 简化：spread=0 时直线方向，否则加随机扰动（本简化用确定性扰动）
                Vector2 dir = Direction;
                if (Spread > 0f)
                {
                    // 用 TotalEmitted 做确定性扰动（-spread/2 .. +spread/2，度）
                    float angleOffset = ((TotalEmitted % 7) - 3f) * (Spread / 6f) * MathF.PI / 180f;
                    float cos = MathF.Cos(angleOffset);
                    float sin = MathF.Sin(angleOffset);
                    dir = new Vector2(dir.X * cos - dir.Y * sin, dir.X * sin + dir.Y * cos);
                }

                // 初速度（min==max 时固定，否则用 TotalEmitted 确定性插值）
                float speed = InitialVelocityMin;
                if (InitialVelocityMax > InitialVelocityMin)
                {
                    float t = (TotalEmitted % 5) / 4f; // 0, 0.25, 0.5, 0.75, 1
                    speed = InitialVelocityMin + (InitialVelocityMax - InitialVelocityMin) * t;
                }

                _particles[i] = new Particle2D
                {
                    Position = GlobalPosition,
                    Velocity = dir * speed,
                    Life = 0f,
                    MaxLife = Lifetime,
                    Color = Color,
                    Alive = true,
                };
                TotalEmitted++;
                AliveCount++;
                return;
            }
            // 池满，丢弃
        }

        /// <summary>发射 N 个（one-shot burst 或连续积累）。</summary>
        private void EmitBatch(int count)
        {
            for (int i = 0; i < count; i++)
                EmitOne();
        }

        /// <summary>推进粒子系统（发射 + 积分 + 寿命回收）。</summary>
        public void Update(float dt)
        {
            // 1. 发射逻辑
            if (Emitting)
            {
                if (OneShot)
                {
                    if (!_oneShotStarted)
                    {
                        // 一次性发射：explosiveness=1 全发，<1 按比例
                        int burst = Math.Max(1, (int)(Amount * Math.Clamp(Explosiveness, 0f, 1f)));
                        EmitBatch(burst);
                        _oneShotStarted = true;
                        OneShotEmitted = true;
                    }
                }
                else
                {
                    // 连续模式：rate = amount / lifetime，accum += rate*dt
                    float rate = Lifetime > 0f ? Amount / Lifetime : Amount;
                    _emissionAccumulator += rate * dt;
                    int emit = (int)_emissionAccumulator;
                    if (emit > 0)
                    {
                        EmitBatch(emit);
                        _emissionAccumulator -= emit;
                    }
                }
            }

            // 2. 积分 + 寿命
            for (int i = 0; i < _particles.Length; i++)
            {
                ref var p = ref _particles[i];
                if (!p.Alive) continue;

                p.Life += dt;
                if (p.Life >= p.MaxLife)
                {
                    p.Alive = false;
                    AliveCount--;
                    continue;
                }

                // 速度 += 重力 * dt
                p.Velocity += Gravity * dt;
                // 位置 += 速度 * dt
                p.Position += p.Velocity * dt;

                // 颜色渐变
                float t = p.MaxLife > 0f ? p.Life / p.MaxLife : 1f;
                p.Color = ColorRamp != null ? ColorRamp.Sample(t) : Color;
            }
        }

        /// <summary>重置发射器（清空所有粒子，one-shot 状态复位）。</summary>
        public void Restart()
        {
            for (int i = 0; i < _particles.Length; i++)
                _particles[i].Alive = false;
            AliveCount = 0;
            TotalEmitted = 0;
            _emissionAccumulator = 0f;
            _oneShotStarted = false;
            OneShotEmitted = false;
        }
    }
}
