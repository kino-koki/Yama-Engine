using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace ShanyuEngine.Assets
{
    // ════════════════════════════════════════════════════════════════════════════
    //  ObjectAnimationClip — 对象级关键帧动画剪辑（简化版，无骨骼）
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 对象级动画关键帧：存储某个时间点的位置、旋转、缩放。
    /// </summary>
    public sealed class ObjectKeyframe
    {
        public float TimeSeconds { get; set; }
        public Vector3 Position { get; set; }
        public Quaternion Rotation { get; set; }
        public Vector3 Scale { get; set; }

        public ObjectKeyframe() { }

        public ObjectKeyframe(float time, Vector3 position, Quaternion rotation, Vector3 scale)
        {
            TimeSeconds = time;
            Position = position;
            Rotation = rotation;
            Scale = scale;
        }
    }

    /// <summary>
    /// 对象级动画剪辑：一组关键帧，描述 SceneObject 随时间变化的变换。
    /// 简化版，直接操作 SceneObject 的 Transform。
    /// </summary>
    public sealed class ObjectAnimationClip
    {
        public string Name { get; set; } = "ObjectAnimation";
        public float LengthSeconds { get; set; } = 1f;
        public bool Looping { get; set; } = true;
        public List<ObjectKeyframe> Keyframes { get; set; } = new();

        /// <summary>添加关键帧并自动按时间排序。</summary>
        public void AddKeyframe(float time, Vector3 position, Quaternion rotation, Vector3 scale)
        {
            Keyframes.Add(new ObjectKeyframe(time, position, rotation, scale));
            Keyframes.Sort((a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));
            LengthSeconds = MathF.Max(LengthSeconds, time);
        }

        /// <summary>在指定时间采样，返回插值后的变换。</summary>
        public (Vector3 position, Quaternion rotation, Vector3 scale) Sample(float time)
        {
            float t = Looping ? NormalizeTime(time, LengthSeconds) : Math.Clamp(time, 0f, LengthSeconds);

            if (Keyframes.Count == 0)
                return (Vector3.Zero, Quaternion.Identity, Vector3.One);

            if (Keyframes.Count == 1 || t <= Keyframes[0].TimeSeconds)
                return (Keyframes[0].Position, Keyframes[0].Rotation, Keyframes[0].Scale);

            if (t >= Keyframes[^1].TimeSeconds)
                return (Keyframes[^1].Position, Keyframes[^1].Rotation, Keyframes[^1].Scale);

            for (int i = 0; i < Keyframes.Count - 1; i++)
            {
                var a = Keyframes[i];
                var b = Keyframes[i + 1];
                if (t >= a.TimeSeconds && t <= b.TimeSeconds)
                {
                    float span = MathF.Max(0.0001f, b.TimeSeconds - a.TimeSeconds);
                    float alpha = (t - a.TimeSeconds) / span;

                    var pos = Vector3.Lerp(a.Position, b.Position, alpha);
                    var rot = Quaternion.Normalize(Quaternion.Slerp(a.Rotation, b.Rotation, alpha));
                    var scl = Vector3.Lerp(a.Scale, b.Scale, alpha);
                    return (pos, rot, scl);
                }
            }

            return (Keyframes[^1].Position, Keyframes[^1].Rotation, Keyframes[^1].Scale);
        }

        private static float NormalizeTime(float time, float length)
        {
            if (length <= 0f) return 0f;
            var wrapped = time % length;
            return wrapped < 0f ? wrapped + length : wrapped;
        }
    }
}
