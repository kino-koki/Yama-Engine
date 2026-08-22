using System;
using System.Collections.Generic;
using System.Numerics;
using ShanyuEngine.VFX;

namespace ShanyuEngine.Particles
{
    public enum ParticleSimulationSpace
    {
        World,
        Local
    }

    public enum ParticleSortMode
    {
        None,
        Distance,
        Age
    }

    public struct MinMaxCurve
    {
        public float Constant;
        public float Min;
        public float Max;
        public bool IsTwoValues;
        public bool IsCurve;

        public MinMaxCurve(float value)
        {
            Constant = value;
            Min = value;
            Max = value;
            IsTwoValues = false;
            IsCurve = false;
        }

        public MinMaxCurve(float min, float max)
        {
            Min = min;
            Max = max;
            Constant = (min + max) * 0.5f;
            IsTwoValues = true;
            IsCurve = false;
        }

        public float Evaluate(float t, float random)
        {
            if (!IsTwoValues) return Constant;
            return Min + (Max - Min) * random;
        }

        public static implicit operator MinMaxCurve(float v) => new(v);
    }

    public struct Particle
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public Vector3 Acceleration;
        public Quaternion Rotation;
        public float AngularVelocity;
        public Vector2 Size;
        public Vector4 Color;
        public float Age;
        public float Lifetime;
        public float RandomSeed;
        public bool IsAlive;
        public float CameraDistance;
    }

    public class EmissionModule
    {
        public bool Enabled = true;
        public float RateOverTime = 10f;
        public float RateOverDistance = 0f;
        public int BurstCount;
        public List<ParticleBurst> Bursts = new();
        public float TimeAccumulator;
        public float DistanceAccumulator;
    }

    public class ParticleBurst
    {
        public float Time;
        public int MinCount = 10;
        public int MaxCount = 30;
        public int Cycles = 1;
        public float Interval = 1f;
        public float Probability = 1f;
        private bool _fired;

        public bool ShouldFire(float systemAge)
        {
            if (_fired) return false;
            if (systemAge >= Time)
            {
                _fired = true;
                return Random.Shared.NextDouble() < Probability;
            }
            return false;
        }

        public int GetCount()
        {
            return Random.Shared.Next(MinCount, MaxCount + 1);
        }
    }

    public class ShapeModule
    {
        public enum ShapeType { Sphere, Hemisphere, Cone, Box, Circle, Edge, Donut, Rectangle }
        public ShapeType Shape = ShapeType.Cone;
        public float Radius = 1f;
        public float Angle = 25f;
        public Vector3 Position = Vector3.Zero;
        public Quaternion Rotation = Quaternion.Identity;
        public Vector3 Scale = Vector3.One;
        public bool RandomDirection;
        public float SpherifyDirection;
        public float RandomPositionAmount;
        public float BoxThickness = 0f;

        public void Emit(out Vector3 pos, out Vector3 dir)
        {
            float r1 = (float)Random.Shared.NextDouble();
            float r2 = (float)Random.Shared.NextDouble();
            switch (Shape)
            {
                case ShapeType.Sphere:
                    float theta = r1 * MathF.PI * 2f;
                    float phi = MathF.Acos(2f * r2 - 1f);
                    dir = new Vector3(MathF.Sin(phi) * MathF.Cos(theta), MathF.Cos(phi), MathF.Sin(phi) * MathF.Sin(theta));
                    pos = dir * Radius * (float)Random.Shared.NextDouble();
                    break;
                case ShapeType.Hemisphere:
                    theta = r1 * MathF.PI * 2f;
                    phi = MathF.Acos(r2);
                    dir = new Vector3(MathF.Sin(phi) * MathF.Cos(theta), MathF.Cos(phi), MathF.Sin(phi) * MathF.Sin(theta));
                    pos = dir * Radius * (float)Random.Shared.NextDouble();
                    break;
                case ShapeType.Cone:
                    float angleRad = Angle * MathF.PI / 180f;
                    float coneRadius = MathF.Tan(angleRad) * 1f;
                    dir = new Vector3(MathF.Cos(r1 * MathF.PI * 2f) * coneRadius, 1f, MathF.Sin(r1 * MathF.PI * 2f) * coneRadius);
                    dir = Vector3.Normalize(dir);
                    pos = new Vector3((r1 - 0.5f) * Radius * 2f * (float)Random.Shared.NextDouble(), 0, (r2 - 0.5f) * Radius * 2f * (float)Random.Shared.NextDouble());
                    break;
                case ShapeType.Box:
                    pos = new Vector3((r1 - 0.5f) * Scale.X, (r2 - 0.5f) * Scale.Y, ((float)Random.Shared.NextDouble() - 0.5f) * Scale.Z);
                    dir = Vector3.UnitY;
                    break;
                default:
                    pos = Vector3.Zero;
                    dir = Vector3.UnitY;
                    break;
            }

            if (RandomDirection)
            {
                float rx = (float)Random.Shared.NextDouble() * 2f - 1f;
                float ry = (float)Random.Shared.NextDouble() * 2f - 1f;
                float rz = (float)Random.Shared.NextDouble() * 2f - 1f;
                dir = Vector3.Normalize(new Vector3(rx, ry, rz));
            }

            pos = Vector3.Transform(pos, Rotation) + Position;
            dir = Vector3.Transform(dir, Rotation);
        }
    }


    public class ForceModule
    {
        public bool Enabled;
        public MinMaxCurve X = new(0);
        public MinMaxCurve Y = new(-9.81f);
        public MinMaxCurve Z = new(0);
        public float Randomness;
    }




    public class LifetimeModule
    {
        public MinMaxCurve Lifetime = new(5f);
    }


    public class RendererModule
    {
        public string MaterialName = "DefaultParticle";
        public Vector4 TintColor = new(1, 1, 1, 1);
        public ParticleSortMode SortMode = ParticleSortMode.Distance;
        public float SoftParticlesDistance = 1f;
        public bool CastsShadows;
    }

    public class ParticleSystemCore
    {
        public string Name = "ParticleSystem";
        public Guid Id = Guid.NewGuid();
        public bool Enabled = true;
        public bool IsPlaying;
        public bool Looping = true;
        public float Duration = 5f;
        public float Age;
        public int MaxParticles = 1000;
        public ParticleSimulationSpace SimulationSpace = ParticleSimulationSpace.World;
        public bool UseUnscaledTime;
        public bool AutoDestroy;

        public readonly EmissionModule Emission = new();
        public readonly ShapeModule Shape = new();
        public readonly VelocityModule VelocityOverLifetime = new();
        public readonly ForceModule ForceModule = new();
        public readonly SizeModule SizeModule = new();
        public readonly ColorModule ColorModule = new();
        public readonly RotationModule RotationModule = new();
        public readonly LifetimeModule LifetimeModule = new();
        public readonly CollisionModule Collision = new();
        public readonly RendererModule Renderer = new();

        private Particle[] _particles;
        private int _aliveCount;
        private Vector3 _systemPosition;

        public int AliveCount => _aliveCount;
        public int ParticleCapacity => _particles?.Length ?? 0;
        public bool IsEmitting => Enabled && (Age < Duration || Looping);

        public ParticleSystemCore()
        {
            _particles = new Particle[MaxParticles];
            ForceModule.Enabled = true;
            VelocityOverLifetime.Enabled = true;
        }

        public void Play()
        {
            IsPlaying = true;
            Age = 0f;
        }

        public void Stop(bool clear = true)
        {
            IsPlaying = false;
            if (clear) Clear();
        }

        public void Pause() => IsPlaying = false;
        public void Resume() => IsPlaying = true;

        public void Clear()
        {
            for (int i = 0; i < _particles.Length; i++)
                _particles[i].IsAlive = false;
            _aliveCount = 0;
            Age = 0f;
        }

        public void Emit(int count, Vector3? position = null)
        {
            Vector3 pos = position ?? _systemPosition;
            for (int i = 0; i < count; i++)
                SpawnParticle(pos);
        }

        private void SpawnParticle(Vector3 position)
        {
            if (_aliveCount >= _particles.Length) return;

            int slot = -1;
            for (int i = 0; i < _particles.Length; i++)
            {
                if (!_particles[i].IsAlive) { slot = i; break; }
            }
            if (slot < 0) return;

            Shape.Emit(out var shapePos, out var dir);
            var p = new Particle
            {
                Position = position + shapePos,
                Velocity = dir * VelocityOverLifetime.Y.Evaluate(0, (float)Random.Shared.NextDouble()),
                Acceleration = Vector3.Zero,
                Rotation = Quaternion.Identity,
                AngularVelocity = RotationModule.AngularVelocity.Evaluate(0, (float)Random.Shared.NextDouble()),
                Size = new Vector2(SizeModule.SizeX.Evaluate(0, (float)Random.Shared.NextDouble()), SizeModule.SizeY.Evaluate(0, (float)Random.Shared.NextDouble())),
                Color = ColorModule.StartColor,
                Age = 0f,
                Lifetime = MathF.Max(0.01f, LifetimeModule.Lifetime.Evaluate(0, (float)Random.Shared.NextDouble())),
                RandomSeed = (float)Random.Shared.NextDouble(),
                IsAlive = true
            };
            _particles[slot] = p;
            _aliveCount++;
        }

        public void Update(float dt, Vector3 cameraPos)
        {
            if (!Enabled || !IsPlaying) return;
            Age += dt;

            // Emission
            Emission.TimeAccumulator += dt;
            float emitInterval = 1f / MathF.Max(0.01f, Emission.RateOverTime);
            while (Emission.TimeAccumulator >= emitInterval && _aliveCount < _particles.Length)
            {
                Emission.TimeAccumulator -= emitInterval;
                if (Age < Duration || Looping)
                    SpawnParticle(_systemPosition);
            }

            // Bursts
            foreach (var burst in Emission.Bursts)
            {
                if (burst.ShouldFire(Age))
                    Emit(burst.GetCount(), _systemPosition);
            }

            // Particle update
            for (int i = 0; i < _particles.Length; i++)
            {
                ref var p = ref _particles[i];
                if (!p.IsAlive) continue;

                p.Age += dt;
                float t = p.Age / p.Lifetime;
                if (t >= 1f) { p.IsAlive = false; _aliveCount--; continue; }

                if (ForceModule.Enabled)
                {
                    p.Acceleration = new Vector3(
                        ForceModule.X.Evaluate(t, p.RandomSeed),
                        ForceModule.Y.Evaluate(t, p.RandomSeed),
                        ForceModule.Z.Evaluate(t, p.RandomSeed));
                    p.Velocity += p.Acceleration * dt;
                }

                p.Position += p.Velocity * dt;

                if (RotationModule.Enabled)
                {
                    float ang = p.AngularVelocity * dt;
                    p.Rotation = Quaternion.Normalize(p.Rotation * Quaternion.CreateFromYawPitchRoll(0, 0, ang));
                }

                if (SizeModule.Enabled)
                {
                    float sizeT = MathF.Sin(t * MathF.PI) * 0.5f + 0.5f;
                    p.Size = new Vector2(
                        SizeModule.SizeX.Evaluate(t, p.RandomSeed) * sizeT,
                        SizeModule.SizeY.Evaluate(t, p.RandomSeed) * sizeT);
                }

                if (ColorModule.Enabled)
                {
                    float alpha = 1f - t;
                    p.Color = new Vector4(ColorModule.StartColor.X, ColorModule.StartColor.Y, ColorModule.StartColor.Z, alpha);
                }

                p.CameraDistance = Vector3.DistanceSquared(p.Position, cameraPos);
            }

            // Sort
            if (Renderer.SortMode == ParticleSortMode.Distance)
            {
                Array.Sort(_particles, (a, b) =>
                {
                    if (!a.IsAlive && !b.IsAlive) return 0;
                    if (!a.IsAlive) return 1;
                    if (!b.IsAlive) return -1;
                    return b.CameraDistance.CompareTo(a.CameraDistance);
                });
            }
        }

        public Particle[] GetParticles() => _particles;

        public int GetAliveParticleIndices(List<int> indices)
        {
            indices.Clear();
            for (int i = 0; i < _particles.Length; i++)
                if (_particles[i].IsAlive) indices.Add(i);
            return indices.Count;
        }

        public void SetPosition(Vector3 position) => _systemPosition = position;
    }

    public class ParticleEffect
    {
        public string Name = "ParticleEffect";
        public Guid Id = Guid.NewGuid();
        public Vector3 Position;
        public Quaternion Rotation = Quaternion.Identity;
        public Vector3 Scale = Vector3.One;
        public readonly List<ParticleSystemCore> Systems = new();
        public float Duration = 5f;
        public float Age;
        public bool IsPlaying;
        public bool Looping = true;

        public void Play()
        {
            IsPlaying = true;
            Age = 0f;
            foreach (var sys in Systems) sys.Play();
        }

        public void Stop()
        {
            IsPlaying = false;
            foreach (var sys in Systems) sys.Stop();
        }

        public void Update(float dt, Vector3 cameraPos)
        {
            if (!IsPlaying) return;
            Age += dt;
            foreach (var sys in Systems)
            {
                sys.SetPosition(Position);
                sys.Update(dt, cameraPos);
            }
        }

        public int TotalAlive
        {
            get
            {
                int c = 0;
                foreach (var s in Systems) c += s.AliveCount;
                return c;
            }
        }
    }
}
