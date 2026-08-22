using System.Numerics;
using System.Text.Json;
using ShanyuEngine.Assets;
using ShanyuEngine.Skinning;

namespace ShanyuEngine.Animation
{
    /// <summary>
    /// Determines how an <see cref="AnimationClip"/> handles time that falls outside
    /// of the [0, LengthSeconds] range while sampling.
    /// </summary>
    public enum WrapMode
    {
        /// <summary>Play once and hold the first/last frame. Sampling past the end clamps to the last key.</summary>
        Once,
        /// <summary>Wrap around to the beginning indefinitely.</summary>
        Loop,
        /// <summary>Ping-pong between the start and end of the clip.</summary>
        PingPong,
        /// <summary>Keep sampling the last frame forever (useful for one-shot poses).</summary>
        ClampForever
    }

    /// <summary>A keyframe holding a vector value (used for translation and scale tracks).</summary>
    public sealed record VectorKeyframe(float TimeSeconds, Vector3 Value) : IVectorKey
    {
        float IVectorKey.Time => TimeSeconds;
        Vector3 IVectorKey.Value => Value;
    }

    /// <summary>A keyframe holding a quaternion rotation.</summary>
    public sealed record RotationKeyframe(float TimeSeconds, Quaternion Value);

    /// <summary>A keyframe holding a per-axis scale value.</summary>
    public sealed record ScaleKeyframe(float TimeSeconds, Vector3 Value) : IVectorKey
    {
        float IVectorKey.Time => TimeSeconds;
        Vector3 IVectorKey.Value => Value;
    }

    /// <summary>Allows translation/scale keyframe lists to share a single vector sampler.</summary>
    internal interface IVectorKey
    {
        float Time { get; }
        Vector3 Value { get; }
    }

    /// <summary>
    /// An event fired by an <see cref="AnimationClip"/> when playback crosses a specific
    /// time. Events carry an optional typed payload and are surfaced through the
    /// animator/state-machine update loop.
    /// </summary>
    public sealed class AnimationEvent
    {
        public string Name { get; set; } = string.Empty;
        public float TimeSeconds { get; set; }
        public float FloatParameter { get; set; }
        public int IntParameter { get; set; }
        public string StringParameter { get; set; } = string.Empty;
        public object? ObjectParameter { get; set; }

        internal bool Fired { get; set; }

        public AnimationEvent() { }

        public AnimationEvent(string name, float timeSeconds)
        {
            Name = name;
            TimeSeconds = timeSeconds;
        }

        public AnimationEvent(string name, float timeSeconds, float floatParameter)
            : this(name, timeSeconds)
        {
            FloatParameter = floatParameter;
        }

        public AnimationEvent(string name, float timeSeconds, int intParameter)
            : this(name, timeSeconds)
        {
            IntParameter = intParameter;
        }

        public AnimationEvent Clone() => new()
        {
            Name = Name,
            TimeSeconds = TimeSeconds,
            FloatParameter = FloatParameter,
            IntParameter = IntParameter,
            StringParameter = StringParameter,
            ObjectParameter = ObjectParameter,
            Fired = Fired
        };
    }

    /// <summary>
    /// A per-bone animation track containing optional translation, rotation and scale
    /// keyframe curves. Keyframes are kept sorted ascending by time.
    /// </summary>
    public sealed class BoneAnimationTrack
    {
        public string BoneName { get; set; } = string.Empty;
        public List<VectorKeyframe> TranslationKeys { get; set; } = new();
        public List<RotationKeyframe> RotationKeys { get; set; } = new();
        public List<ScaleKeyframe> ScaleKeys { get; set; } = new();

        public bool HasData =>
            TranslationKeys.Count > 0 || RotationKeys.Count > 0 || ScaleKeys.Count > 0;

        public float LastKeyTime
        {
            get
            {
                float t = 0f;
                if (TranslationKeys.Count > 0) t = MathF.Max(t, TranslationKeys[^1].TimeSeconds);
                if (RotationKeys.Count > 0) t = MathF.Max(t, RotationKeys[^1].TimeSeconds);
                if (ScaleKeys.Count > 0) t = MathF.Max(t, ScaleKeys[^1].TimeSeconds);
                return t;
            }
        }
    }

    /// <summary>
    /// A playable motion. Concrete implementations are <see cref="AnimationClip"/> and
    /// <see cref="BlendTree"/>. The interface allows the state machine to treat both
    /// uniformly.
    /// </summary>
    public interface IMotion
    {
        string Name { get; }
        float Length { get; }
        bool IsLooping { get; }

        /// <summary>Sample the motion at an absolute time and return a local-space pose.</summary>
        SkeletonPose Sample(Skeleton skeleton, float timeSeconds);

        /// <summary>Reset transient playback state (event flags, etc.).</summary>
        void Reset();
    }

    /// <summary>
    /// A complete animation clip: a collection of per-bone keyframe tracks plus metadata
    /// and events. Clips can be sampled at arbitrary times and support looping, ping-pong
    /// and clamped wrap modes.
    /// </summary>
    public sealed class AnimationClip : IMotion
    {
        public int Version { get; set; } = 1;
        public string Guid { get; set; } = System.Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "AnimationClip";
        public float LengthSeconds { get; set; } = 1f;

        /// <summary>
        /// Backward-compatible looping flag. Maps to <see cref="WrapMode"/>: setting to
        /// <c>true</c> selects <see cref="WrapMode.Loop"/>, <c>false</c> selects
        /// <see cref="WrapMode.Once"/>.
        /// </summary>
        public bool Looping
        {
            get => WrapMode == WrapMode.Loop;
            set => WrapMode = value ? WrapMode.Loop : WrapMode.Once;
        }

        public WrapMode WrapMode { get; set; } = WrapMode.Once;
        public List<BoneAnimationTrack> Tracks { get; set; } = new();
        public List<AnimationEvent> Events { get; set; } = new();
        public float Speed { get; set; } = 1f;

        string IMotion.Name => Name;
        float IMotion.Length => LengthSeconds;
        bool IMotion.IsLooping => Looping || WrapMode == WrapMode.PingPong;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            IncludeFields = true
        };

        public AnimationClip() { }

        /// <summary>Create a clip wrapping a single bone track for convenience.</summary>
        public AnimationClip(string name, float lengthSeconds, bool looping = false)
        {
            Name = name;
            LengthSeconds = MathF.Max(0f, lengthSeconds);
            Looping = looping;
        }

        // ---------------------------------------------------------------------
        // Keyframe authoring
        // ---------------------------------------------------------------------

        public void AddTranslationKey(string boneName, float timeSeconds, Vector3 value)
        {
            var track = GetOrCreateTrack(boneName);
            track.TranslationKeys.Add(new VectorKeyframe(MathF.Max(0f, timeSeconds), value));
            track.TranslationKeys.Sort((a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));
            LengthSeconds = MathF.Max(LengthSeconds, timeSeconds);
        }

        public void AddRotationKey(string boneName, float timeSeconds, Quaternion value)
        {
            var track = GetOrCreateTrack(boneName);
            track.RotationKeys.Add(new RotationKeyframe(MathF.Max(0f, timeSeconds), Quaternion.Normalize(value)));
            track.RotationKeys.Sort((a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));
            LengthSeconds = MathF.Max(LengthSeconds, timeSeconds);
        }

        public void AddScaleKey(string boneName, float timeSeconds, Vector3 value)
        {
            var track = GetOrCreateTrack(boneName);
            track.ScaleKeys.Add(new ScaleKeyframe(MathF.Max(0f, timeSeconds), value));
            track.ScaleKeys.Sort((a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));
            LengthSeconds = MathF.Max(LengthSeconds, timeSeconds);
        }

        /// <summary>Recompute <see cref="LengthSeconds"/> from the latest keyframe across all tracks.</summary>
        public void RecalculateLength()
        {
            float max = 0f;
            foreach (var track in Tracks)
                max = MathF.Max(max, track.LastKeyTime);
            LengthSeconds = MathF.Max(0.0001f, max);
        }

        public BoneAnimationTrack? FindTrack(string boneName)
        {
            foreach (var t in Tracks)
                if (t.BoneName == boneName) return t;
            return null;
        }

        // ---------------------------------------------------------------------
        // Sampling
        // ---------------------------------------------------------------------

        /// <summary>
        /// Sample the clip at an arbitrary time and produce a local-space pose for the
        /// given skeleton. Bones without tracks retain their bind pose.
        /// </summary>
        public SkeletonPose Sample(Skeleton skeleton, float timeSeconds)
        {
            if (skeleton == null) throw new ArgumentNullException(nameof(skeleton));

            var t = NormalizeTime(timeSeconds, LengthSeconds, WrapMode);
            var pose = skeleton.CreateBindPose();
            foreach (var track in Tracks)
            {
                var idx = skeleton.FindBone(track.BoneName);
                if (idx < 0) continue;

                if (track.TranslationKeys.Count > 0)
                    pose.SetTranslationOffset(idx, SampleVector(track.TranslationKeys, t));
                if (track.RotationKeys.Count > 0)
                    pose.SetRotation(idx, SampleRotation(track.RotationKeys, t));
                if (track.ScaleKeys.Count > 0)
                    pose.SetScaleOffset(idx, SampleVector(track.ScaleKeys, t));
            }
            return pose;
        }

        /// <summary>
        /// Sample a single track's channels into a provided pose without touching other
        /// bones. Useful for additive/blended sampling pipelines.
        /// </summary>
        public void SampleTrack(Skeleton skeleton, SkeletonPose pose, BoneAnimationTrack track, float timeSeconds)
        {
            if (skeleton == null) throw new ArgumentNullException(nameof(skeleton));
            if (pose == null) throw new ArgumentNullException(nameof(pose));
            var idx = skeleton.FindBone(track.BoneName);
            if (idx < 0) return;

            var t = NormalizeTime(timeSeconds, LengthSeconds, WrapMode);
            if (track.TranslationKeys.Count > 0)
                pose.SetTranslationOffset(idx, SampleVector(track.TranslationKeys, t));
            if (track.RotationKeys.Count > 0)
                pose.SetRotation(idx, SampleRotation(track.RotationKeys, t));
            if (track.ScaleKeys.Count > 0)
                pose.SetScaleOffset(idx, SampleVector(track.ScaleKeys, t));
        }

        // ---------------------------------------------------------------------
        // Animation events
        // ---------------------------------------------------------------------

        public void AddEvent(AnimationEvent evt)
        {
            Events.Add(evt ?? throw new ArgumentNullException(nameof(evt)));
            Events.Sort((a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));
        }

        public void AddEvent(string name, float timeSeconds) => AddEvent(new AnimationEvent(name, timeSeconds));

        /// <summary>Clear the fired flag on all events so they can fire again on the next pass.</summary>
        public void ResetEvents()
        {
            foreach (var e in Events) e.Fired = false;
        }

        void IMotion.Reset() => ResetEvents();

        /// <summary>
        /// Return events whose time is crossed while advancing from
        /// <paramref name="previousTime"/> to <paramref name="currentTime"/>. Handles
        /// looping wrap-around so events fire every loop iteration.
        /// </summary>
        public List<AnimationEvent> PollEvents(float previousTime, float currentTime)
        {
            var result = new List<AnimationEvent>();
            if (Events.Count == 0) return result;

            var len = LengthSeconds;
            if (len <= 0f) return result;

            var looping = WrapMode == WrapMode.Loop || WrapMode == WrapMode.PingPong;
            float p = looping ? NormalizeTime(previousTime, len, WrapMode) : Math.Clamp(previousTime, 0f, len);
            float c = looping ? NormalizeTime(currentTime, len, WrapMode) : Math.Clamp(currentTime, 0f, len);

            foreach (var evt in Events)
            {
                var et = Math.Clamp(evt.TimeSeconds, 0f, len);
                if (c >= p)
                {
                    if (et > p && et <= c) result.Add(evt);
                }
                else
                {
                    // Time wrapped across the clip boundary.
                    if (et > p || et <= c) result.Add(evt);
                }
            }
            return result;
        }

        // ---------------------------------------------------------------------
        // Serialization
        // ---------------------------------------------------------------------

        public void Write(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
            AssetMeta.FromMetadata(new AssetMetadata
            {
                Guid = System.Guid.Parse(Guid),
                Name = Name,
                AssetPath = ToProjectAssetPath(path),
                SourceFile = path,
                Type = AssetType.Animation,
                FileSize = new FileInfo(path).Length,
                ImportedAt = DateTime.Now,
                LastModified = File.GetLastWriteTime(path),
                Hash = Guid
            }).Write(path + ".shmeta");
        }

        public static AnimationClip Read(string path)
        {
            return JsonSerializer.Deserialize<AnimationClip>(File.ReadAllText(path), JsonOptions)
                   ?? throw new InvalidOperationException($"Invalid animation clip: {path}");
        }

        // ---------------------------------------------------------------------
        // Internals
        // ---------------------------------------------------------------------

        private BoneAnimationTrack GetOrCreateTrack(string boneName)
        {
            var track = Tracks.FirstOrDefault(t => t.BoneName == boneName);
            if (track != null) return track;

            track = new BoneAnimationTrack { BoneName = boneName };
            Tracks.Add(track);
            return track;
        }

        /// <summary>
        /// Normalise a sample time according to a wrap mode. Exposed internally so blend
        /// trees and the state machine can share the exact same wrapping semantics.
        /// </summary>
        internal static float NormalizeTime(float time, float length, WrapMode mode)
        {
            if (length <= 0f) return 0f;
            switch (mode)
            {
                case WrapMode.Loop:
                {
                    var wrapped = time % length;
                    return wrapped < 0f ? wrapped + length : wrapped;
                }
                case WrapMode.PingPong:
                {
                    var period = 2f * length;
                    var p = time % period;
                    if (p < 0f) p += period;
                    return p < length ? p : period - p;
                }
                case WrapMode.Once:
                case WrapMode.ClampForever:
                default:
                    return Math.Clamp(time, 0f, length);
            }
        }

        private static Vector3 SampleVector<T>(IReadOnlyList<T> keys, float time) where T : IVectorKey
        {
            if (keys.Count == 1 || time <= keys[0].Time) return keys[0].Value;
            if (time >= keys[^1].Time) return keys[^1].Value;

            for (var i = 0; i < keys.Count - 1; i++)
            {
                var a = keys[i];
                var b = keys[i + 1];
                if (time >= a.Time && time <= b.Time)
                {
                    var span = MathF.Max(0.0001f, b.Time - a.Time);
                    return Vector3.Lerp(a.Value, b.Value, (time - a.Time) / span);
                }
            }
            return keys[^1].Value;
        }

        private static Quaternion SampleRotation(IReadOnlyList<RotationKeyframe> keys, float time)
        {
            if (keys.Count == 1 || time <= keys[0].TimeSeconds) return keys[0].Value;
            if (time >= keys[^1].TimeSeconds) return keys[^1].Value;

            for (var i = 0; i < keys.Count - 1; i++)
            {
                var a = keys[i];
                var b = keys[i + 1];
                if (time >= a.TimeSeconds && time <= b.TimeSeconds)
                {
                    var span = MathF.Max(0.0001f, b.TimeSeconds - a.TimeSeconds);
                    return Quaternion.Normalize(Quaternion.Slerp(a.Value, b.Value, (time - a.TimeSeconds) / span));
                }
            }
            return keys[^1].Value;
        }

        private static string ToProjectAssetPath(string path)
        {
            var normalized = Path.GetFullPath(path).Replace('\\', '/');
            const string marker = "/Assets/";
            var idx = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            return idx >= 0 ? normalized[(idx + 1)..] : Path.GetFileName(path);
        }
    }
}
