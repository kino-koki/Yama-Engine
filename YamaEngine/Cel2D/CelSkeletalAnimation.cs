// ============================================================================================
//  CelSkeletalAnimation.cs
//
//  yama引擎 (YamaEngine) 赛璐璐 / 二次元 2D 子系统 —— 2D 骨骼动画
//
//  用于二次元角色肢体 / 部件动画：
//    · 骨骼层级（父-子），TRS 局部变换，世界矩阵由父链累积
//    · 线性蒙皮（顶点-骨骼权重），用于肢体弯曲
//    · 关键帧轨道 + 采样插值（位置/旋转/缩放）
//
//  纯数据/逻辑层：不依赖 RHI；产出世界矩阵/蒙皮顶点供渲染层消费。
//  命名空间：YamaEngine.Cel2D  ·  目标框架：net10.0
// ============================================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.Cel2D
{
    /// <summary>2D 骨骼（静态定义 + 局部变换）。</summary>
    public sealed class CelBone2D
    {
        public string Name = "Bone";
        public int ParentIndex = -1;
        public Vector2 LocalPosition;
        public float LocalRotation;      // 弧度
        public Vector2 LocalScale = Vector2.One;

        // 运行时世界变换（SetPose 后有效）
        public Matrix3x2 WorldMatrix = Matrix3x2.Identity;

        /// <summary>局部 TRS 矩阵。</summary>
        public Matrix3x2 LocalMatrix => CelMath2D.Compose(LocalPosition, LocalRotation, LocalScale);
    }

    /// <summary>单个骨骼的局部变换快照。</summary>
    public readonly struct CelBoneLocalTransform
    {
        public readonly Vector2 Position;
        public readonly float Rotation;
        public readonly Vector2 Scale;
        public CelBoneLocalTransform(Vector2 pos, float rot, Vector2 scale)
        {
            Position = pos;
            Rotation = rot;
            Scale = scale;
        }
    }

    /// <summary>一帧完整姿势（每根骨骼一个局部变换）。</summary>
    public sealed class CelPose2D
    {
        public readonly CelBoneLocalTransform[] Transforms;

        public CelPose2D(int boneCount)
        {
            Transforms = new CelBoneLocalTransform[Math.Max(0, boneCount)];
            for (int i = 0; i < Transforms.Length; i++)
                Transforms[i] = new CelBoneLocalTransform(Vector2.Zero, 0f, Vector2.One);
        }

        public int Count => Transforms.Length;

        public void SetTransform(int boneIndex, Vector2 pos, float rot, Vector2 scale)
        {
            if (boneIndex >= 0 && boneIndex < Transforms.Length)
                Transforms[boneIndex] = new CelBoneLocalTransform(pos, rot, scale);
        }
    }

    /// <summary>骨骼骨架：静态层级 + 应用姿势计算世界矩阵。</summary>
    public sealed class CelSkeleton2D
    {
        private readonly List<CelBone2D> _bones = new();

        public IReadOnlyList<CelBone2D> Bones => _bones;
        public int BoneCount => _bones.Count;

        /// <summary>添加骨骼（parentIndex 必须 < 当前 count，即父先于子）。</summary>
        public int AddBone(string name, int parentIndex,
            Vector2 localPos, float localRot = 0f, Vector2? localScale = null)
        {
            if (parentIndex >= _bones.Count)
                throw new ArgumentOutOfRangeException(nameof(parentIndex), "父骨骼必须先于子骨骼添加");
            _bones.Add(new CelBone2D
            {
                Name = name,
                ParentIndex = parentIndex,
                LocalPosition = localPos,
                LocalRotation = localRot,
                LocalScale = localScale ?? Vector2.One
            });
            return _bones.Count - 1;
        }

        public CelBone2D GetBone(int index) => _bones[index];

        /// <summary>应用姿势，计算所有骨骼的世界矩阵（父先于子）。</summary>
        public void SetPose(in CelPose2D pose)
        {
            for (int i = 0; i < _bones.Count; i++)
            {
                var bone = _bones[i];
                if (i < pose.Count)
                {
                    var t = pose.Transforms[i];
                    bone.LocalPosition = t.Position;
                    bone.LocalRotation = t.Rotation;
                    bone.LocalScale = t.Scale;
                }

                if (bone.ParentIndex >= 0 && bone.ParentIndex < i)
                {
                    // world = parentWorld * local
                    bone.WorldMatrix = Matrix3x2.Multiply(_bones[bone.ParentIndex].WorldMatrix, bone.LocalMatrix);
                }
                else
                {
                    bone.WorldMatrix = bone.LocalMatrix;
                }
            }
        }

        /// <summary>骨骼世界原点（用于渲染/挂点）。</summary>
        public Vector2 GetBoneWorldPosition(int index)
        {
            var m = _bones[index].WorldMatrix;
            return new Vector2(m.M31, m.M32);
        }

        public Vector2 TransformPoint(int boneIndex, Vector2 localPoint)
            => Vector2.Transform(localPoint, _bones[boneIndex].WorldMatrix);
    }

    /// <summary>蒙皮权重。</summary>
    public readonly struct CelSkinWeight2D
    {
        public readonly int BoneIndex;
        public readonly float Weight;
        public CelSkinWeight2D(int boneIndex, float weight)
        {
            BoneIndex = boneIndex;
            Weight = weight;
        }
    }

    /// <summary>蒙皮顶点（局部 + 权重，线性混合骨骼）。</summary>
    public sealed class CelSkinVertex2D
    {
        public Vector2 Position;
        public readonly List<CelSkinWeight2D> Weights = new();

        public CelSkinVertex2D AddWeight(int boneIndex, float weight)
        {
            Weights.Add(new CelSkinWeight2D(boneIndex, weight));
            return this;
        }
    }

    /// <summary>线性蒙皮：把绑定姿势顶点按骨骼世界矩阵变换并混合。</summary>
    public static class CelSkinning2D
    {
        public static Vector2 Apply(CelSkeleton2D skeleton, in CelSkinVertex2D v)
        {
            if (v.Weights.Count == 0) return v.Position;
            Vector2 result = Vector2.Zero;
            float total = 0f;
            foreach (var w in v.Weights)
            {
                if (w.BoneIndex < 0 || w.BoneIndex >= skeleton.BoneCount || w.Weight <= 0f) continue;
                result += Vector2.Transform(v.Position, skeleton.GetBone(w.BoneIndex).WorldMatrix) * w.Weight;
                total += w.Weight;
            }
            return total > 0f ? result / total : v.Position;
        }

        public static void Apply(CelSkeleton2D skeleton, IReadOnlyList<CelSkinVertex2D> vertices, Vector2[] output)
        {
            for (int i = 0; i < vertices.Count && i < output.Length; i++)
                output[i] = Apply(skeleton, vertices[i]);
        }
    }

    /// <summary>骨骼关键帧。</summary>
    public readonly struct CelBoneKeyframe2D
    {
        public readonly float Time;
        public readonly Vector2 Position;
        public readonly float Rotation;
        public readonly Vector2 Scale;
        public CelBoneKeyframe2D(float time, Vector2 pos, float rot, Vector2 scale)
        {
            Time = time;
            Position = pos;
            Rotation = rot;
            Scale = scale;
        }
    }

    /// <summary>单根骨骼的关键帧轨道（按时间升序）。</summary>
    public sealed class CelBoneTrack2D
    {
        public int BoneIndex;
        public readonly List<CelBoneKeyframe2D> Keyframes = new();
        public bool Loop;

        public CelBoneTrack2D(int boneIndex) { BoneIndex = boneIndex; }

        public CelBoneTrack2D Add(float time, Vector2 pos, float rot, Vector2 scale)
        {
            Keyframes.Add(new CelBoneKeyframe2D(time, pos, rot, scale));
            return this;
        }

        /// <summary>在 time 处采样（返回插值后的局部变换）。</summary>
        public CelBoneLocalTransform Sample(float time)
        {
            if (Keyframes.Count == 0) return new CelBoneLocalTransform(Vector2.Zero, 0f, Vector2.One);
            if (Keyframes.Count == 1 || time <= Keyframes[0].Time)
            {
                var k0 = Keyframes[0];
                return new CelBoneLocalTransform(k0.Position, k0.Rotation, k0.Scale);
            }
            var last = Keyframes[^1];
            if (time >= last.Time) return new CelBoneLocalTransform(last.Position, last.Rotation, last.Scale);

            // 二分/线性查找区间
            for (int i = 0; i < Keyframes.Count - 1; i++)
            {
                var a = Keyframes[i];
                var b = Keyframes[i + 1];
                if (time >= a.Time && time <= b.Time)
                {
                    float span = MathF.Max(b.Time - a.Time, 0.0001f);
                    float t = CelMath2D.Clamp01((time - a.Time) / span);
                    return new CelBoneLocalTransform(
                        Vector2.Lerp(a.Position, b.Position, t),
                        CelMath2D.LerpAngle(a.Rotation, b.Rotation, t),
                        Vector2.Lerp(a.Scale, b.Scale, t));
                }
            }
            return new CelBoneLocalTransform(last.Position, last.Rotation, last.Scale);
        }
    }

    /// <summary>骨骼动画剪辑。</summary>
    public sealed class CelSkeletalClip2D
    {
        public string Name = "Clip";
        public bool Loop = true;
        public float PlaybackSpeed = 1f;
        public readonly List<CelBoneTrack2D> Tracks = new();

        public CelSkeletalClip2D(string name = "Clip") { Name = name; }

        public CelSkeletalClip2D AddTrack(CelBoneTrack2D track)
        {
            Tracks.Add(track);
            return this;
        }

        public float Duration
        {
            get
            {
                float d = 0f;
                foreach (var t in Tracks)
                    if (t.Keyframes.Count > 0)
                        d = MathF.Max(d, t.Keyframes[^1].Time);
                return d;
            }
        }

        /// <summary>采样整个剪辑到姿势。</summary>
        public CelPose2D Sample(float time, int boneCount)
        {
            var pose = new CelPose2D(boneCount);
            foreach (var t in Tracks)
            {
                if (t.BoneIndex < 0 || t.BoneIndex >= boneCount) continue;
                pose.SetTransform(t.BoneIndex, t.Sample(time).Position,
                    t.Sample(time).Rotation, t.Sample(time).Scale);
            }
            return pose;
        }
    }

    /// <summary>骨骼动画播放器。</summary>
    public sealed class CelSkeletalAnimator2D
    {
        private CelSkeletalClip2D? _clip;
        private float _time;
        private bool _playing;

        public CelSkeletalClip2D? Clip => _clip;
        public bool IsPlaying => _playing;
        public float Time => _time;

        public void Play(CelSkeletalClip2D clip, bool restart = true)
        {
            _clip = clip;
            _playing = true;
            if (restart) _time = 0f;
        }

        public void Stop() { _playing = false; _time = 0f; }
        public void Pause() => _playing = false;
        public void Resume() => _playing = true;

        /// <summary>推进并返回当前姿势。</summary>
        public CelPose2D? Update(float deltaTime, int boneCount)
        {
            if (_clip == null || !_playing) return null;
            _time += deltaTime * _clip.PlaybackSpeed;

            float dur = _clip.Duration;
            if (dur > 0f)
            {
                if (_clip.Loop) _time %= dur;
                else if (_time > dur) { _time = dur; _playing = false; }
            }
            return _clip.Sample(_time, boneCount);
        }
    }

    // ── 工具 ──
    internal static class CelMath2D
    {
        public static float Clamp01(float v) => v < 0 ? 0 : (v > 1 ? 1 : v);

        /// <summary>角度插值（最短弧）。</summary>
        public static float LerpAngle(float a, float b, float t)
        {
            float diff = (b - a) % MathF.Tau;
            if (diff > MathF.PI) diff -= MathF.Tau;
            if (diff < -MathF.PI) diff += MathF.Tau;
            return a + diff * t;
        }

        public static Matrix3x2 Compose(Vector2 pos, float rot, Vector2 scale)
        {
            var m = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateRotation(rot);
            m.M31 += pos.X;
            m.M32 += pos.Y;
            return m;
        }
    }
}
