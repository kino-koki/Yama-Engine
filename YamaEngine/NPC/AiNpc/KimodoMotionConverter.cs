using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.Animation;
using Skeleton = YamaEngine.Animation.Skeleton;

namespace YamaEngine.NPC.AiNpc;

/// <summary>Kimodo 动作转换结果：可直接播放的引擎动画片段 + 根轨迹。</summary>
public sealed class KimodoConvertedMotion
{
    public required Skeleton Skeleton { get; init; }
    public required AnimationClip Clip { get; init; }
    public float Fps { get; init; } = 30f;
    public int FrameCount { get; init; }
    /// <summary>逐帧根位置（引擎单位，Y-up）。</summary>
    public Vector3[] RootPositions { get; init; } = Array.Empty<Vector3>();
    /// <summary>逐帧足部接触（左跟/左掌/右跟/右掌）。</summary>
    public float[][]? FootContacts { get; init; }
    public IReadOnlyDictionary<int, int> BoneMapping { get; init; } = new Dictionary<int, int>();

    public float DurationSeconds => Clip.LengthSeconds;
}

/// <summary>
/// Kimodo(somaskel77 / SMPL-X / G1) → 引擎 Skeleton + AnimationClip。
///
/// 坐标约定：Kimodo 为 Y-up、米制；引擎同为 Y-up，按 <see cref="KimodoConfig.ScaleToEngine"/> 缩放。
/// 旋转换算：delta = inverse(sourceRest) * sourceFrame，target = targetRest * delta。
/// 因此同一骨架直接对齐，不同骨架按静息姿态自动重定向。
/// </summary>
public sealed class KimodoMotionConverter
{
    private readonly KimodoConfig _config;
    private readonly IReadOnlyDictionary<string, string>? _externalBoneMap;

    public KimodoMotionConverter(KimodoConfig config, IReadOnlyDictionary<string, string>? boneMap = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _externalBoneMap = boneMap;
    }

    /// <summary>
    /// 转换动作。传入 <paramref name="targetSkeleton"/> 时按骨骼名重定向到既有角色骨架；
    /// 否则使用 Kimodo 返回的骨架定义新建一套。
    /// </summary>
    public KimodoConvertedMotion Convert(KimodoMotionResponse motion, Skeleton? targetSkeleton = null, string? clipName = null, bool? embedRootMotion = null)
    {
        if (motion == null) throw new ArgumentNullException(nameof(motion));
        if (!motion.IsUsable)
            throw new KimodoIntegrationException("Kimodo 动作不可用（缺少帧或骨架）");

        var map = BuildNameMap(motion);
        var skeleton = targetSkeleton ?? BuildSkeleton(motion.Skeleton, map);
        int frameCount = motion.FrameCount;
        float fps = motion.Fps > 0f ? motion.Fps : _config.Fps;
        float duration = frameCount / fps;

        var clip = new AnimationClip(clipName ?? $"Kimodo_{motion.Id}", duration, looping: false);
        clip.WrapMode = WrapMode.Once;

        var boneMap = new Dictionary<int, int>();      // kimodo joint → engine bone
        var restDeltas = new Quaternion[motion.Skeleton.Joints.Count];
        for (int j = 0; j < motion.Skeleton.Joints.Count; j++)
        {
            var joint = motion.Skeleton.Joints[j];
            restDeltas[j] = joint.RestRotation is { Length: 4 }
                ? Normalize(new Quaternion(joint.RestRotation[0], joint.RestRotation[1], joint.RestRotation[2], joint.RestRotation[3]))
                : Quaternion.Identity;

            var engineName = map.TryGetValue(joint.Name, out var mappedName) && !string.IsNullOrEmpty(mappedName)
                ? mappedName
                : joint.Name;
            int boneIndex = skeleton.FindBone(engineName);
            if (boneIndex < 0) boneIndex = skeleton.FindBone(joint.Name);
            if (boneIndex >= 0) boneMap[j] = boneIndex;
        }

        if (boneMap.Count == 0)
            throw new KimodoIntegrationException("Kimodo 骨架与目标骨架无同名骨骼，无法重定向");

        float scale = _config.ScaleToEngine;
        for (int f = 0; f < frameCount; f++)
        {
            float time = f / fps;
            var frame = motion.Rotations[f];
            for (int j = 0; j < frame.Length; j++)
            {
                if (!boneMap.TryGetValue(j, out int boneIndex)) continue;
                if (frame[j].Length < 4) continue;

                var sourceFrame = Normalize(new Quaternion(frame[j][0], frame[j][1], frame[j][2], frame[j][3]));
                var delta = Quaternion.Inverse(restDeltas[j]) * sourceFrame;
                var targetRest = skeleton.Bones[boneIndex].LocalRotation;
                var local = Normalize(targetRest * delta);
                clip.AddRotationKey(skeleton.Bones[boneIndex].Name, time, local);
            }
        }

        var rootPositions = new Vector3[frameCount];
        for (int f = 0; f < frameCount; f++)
        {
            var p = f < motion.RootPositions.Length ? motion.RootPositions[f] : new float[3];
            rootPositions[f] = new Vector3(
                (p.Length > 0 ? p[0] : 0f) * scale,
                (p.Length > 1 ? p[1] : 0f) * scale,
                (p.Length > 2 ? p[2] : 0f) * scale);
        }

        bool embedRoot = embedRootMotion ?? _config.UseRootMotion;
        if (embedRoot && rootPositions.Length > 0)
        {
            int rootBone = FindRootBone(skeleton, motion, map, boneMap);
            if (rootBone >= 0)
            {
                var restLocal = skeleton.Bones[rootBone].LocalPosition;
                var rootName = skeleton.Bones[rootBone].Name;
                for (int f = 0; f < frameCount; f++)
                    clip.AddTranslationKey(rootName, f / fps, rootPositions[f] - restLocal);
            }
        }

        clip.RecalculateLength();

        if (_config.SaveGeneratedClips)
        {
            try
            {
                var dir = System.IO.Path.IsPathRooted(_config.ClipOutputDirectory)
                    ? _config.ClipOutputDirectory
                    : System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), _config.ClipOutputDirectory);
                System.IO.Directory.CreateDirectory(dir);
                clip.Write(System.IO.Path.Combine(dir, Sanitize(clip.Name) + ".clip.json"));
            }
            catch
            {
                // 保存失败不应阻断动作播放。
            }
        }

        return new KimodoConvertedMotion
        {
            Skeleton = skeleton,
            Clip = clip,
            Fps = fps,
            FrameCount = frameCount,
            RootPositions = rootPositions,
            FootContacts = motion.FootContacts,
            BoneMapping = boneMap
        };
    }

    /// <summary>由 Kimodo 骨架定义构建引擎骨架（父索引已拓扑排序保证前驱）。</summary>
    public static Skeleton BuildSkeleton(KimodoSkeletonDef def, IReadOnlyDictionary<string, string>? nameMap = null)
    {
        var skeleton = new Skeleton();
        var pending = new List<(KimodoJointDef Joint, int SourceIndex)>();
        for (int i = 0; i < def.Joints.Count; i++) pending.Add((def.Joints[i], i));

        var built = new Dictionary<int, int>(); // source index → engine bone index
        int guard = 0;
        while (pending.Count > 0)
        {
            if (guard++ > def.Joints.Count * 2 + 8)
                throw new KimodoIntegrationException("Kimodo 骨架父级关系存在循环");

            bool progressed = false;
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var (joint, sourceIndex) = pending[i];
                int parentSource = joint.ParentIndex;
                if (parentSource >= 0 && !built.ContainsKey(parentSource)) continue;

                int parentBone = parentSource >= 0 ? built[parentSource] : -1;
                var restPos = joint.RestPosition is { Length: 3 }
                    ? new Vector3(joint.RestPosition[0], joint.RestPosition[1], joint.RestPosition[2])
                    : Vector3.Zero;
                var restRot = joint.RestRotation is { Length: 4 }
                    ? Normalize(new Quaternion(joint.RestRotation[0], joint.RestRotation[1], joint.RestRotation[2], joint.RestRotation[3]))
                    : Quaternion.Identity;

                var mapped = nameMap != null && nameMap.TryGetValue(joint.Name, out var mappedName) && !string.IsNullOrEmpty(mappedName)
                    ? mappedName
                    : joint.Name;
                int boneIndex = skeleton.AddBone(mapped, parentBone, restPos);
                skeleton.Bones[boneIndex].LocalRotation = restRot;
                built[sourceIndex] = boneIndex;
                pending.RemoveAt(i);
                progressed = true;
            }

            if (!progressed)
                throw new KimodoIntegrationException("Kimodo 骨架父级索引无效");
        }

        return skeleton;
    }

    private static Dictionary<string, string> BuildNameMap(KimodoMotionResponse motion)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (motion.BoneMap != null)
            foreach (var kv in motion.BoneMap) map[kv.Key] = kv.Value;
        return map;
    }

    private static int FindRootBone(Skeleton skeleton, KimodoMotionResponse motion,
        Dictionary<string, string> nameMap, Dictionary<int, int> boneMap)
    {
        for (int j = 0; j < motion.Skeleton.Joints.Count; j++)
        {
            if (motion.Skeleton.Joints[j].ParentIndex < 0 && boneMap.TryGetValue(j, out int bone)) return bone;
        }

        foreach (var preferred in new[] { "Hips", "Pelvis", "pelvis", "root", "Root" })
        {
            var idx = skeleton.FindBone(preferred);
            if (idx >= 0) return idx;
        }

        return skeleton.Bones.Count > 0 ? 0 : -1;
    }

    private static Quaternion Normalize(Quaternion q)
    {
        float len = q.Length();
        return len < 1e-8f ? Quaternion.Identity : new Quaternion(q.X / len, q.Y / len, q.Z / len, q.W / len);
    }

    private static string Sanitize(string name)
    {
        var chars = new char[name.Length];
        for (int i = 0; i < name.Length; i++)
            chars[i] = char.IsLetterOrDigit(name[i]) || name[i] is '_' or '-' ? name[i] : '_';
        return new string(chars);
    }
}
