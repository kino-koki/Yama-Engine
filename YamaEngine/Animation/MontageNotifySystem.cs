using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using YamaEngine.Skinning;

namespace YamaEngine.Animation.Montage
{
    // ============================================================================================
    //  MontageNotifySystem.cs
    //
    //  UE5.8 深度级蒙太奇通知系统 + 蒙太奇分层。
    //
    //  通知系统（对应 UE 的 UAnimNotify / UAnimNotifyState / UAnimNotifyTrack）：
    //    - AnimNotify        ：瞬时通知，到达 TriggerTime 触发一次 Fire。
    //    - AnimNotifyState   ：持续状态通知，进入 [StartTime, EndTime) 触发 Begin，离开触发 End。
    //    - MontageNotifyTrack：命名通知轨道，可并行挂多条轨道，各自独立评估。
    //    - MontageNotifyRuntime：绑定到 MontageInstance，逐帧评估所有轨道的通知/状态。
    //    - AnimNotifyLog     ：事件日志（Fire/Begin/End），供调试与测试断言。
    //
    //  蒙太奇分层（对应 UE 的多 Slot 叠加）：
    //    - MontageSlotLayer  ：单个槽位层（SlotName + Weight + 蒙太奇实例 + 可选骨骼蒙版）。
    //    - MontageLayerStack ：多槽位叠加合成最终姿势（按权重从低到高覆盖）。
    //
    //  注：置于 YamaEngine.Animation.Montage 子命名空间，与既有 AnimMontageSystem.cs 隔离。
    // ============================================================================================

    /// <summary>通知上下文：提供给 Fire/Begin/End 回调。</summary>
    public sealed class AnimNotifyContext
    {
        public MontageInstance? Instance;
        public Skeleton? Skeleton;
        public float Time;
    }

    /// <summary>瞬时通知基类（对应 UE UAnimNotify）。触发一次。</summary>
    public abstract class AnimNotify
    {
        public string Name = "";
        public float TriggerTime;
        internal bool HasFired;

        /// <summary>触发回调（子类覆写）。</summary>
        public virtual void Fire(AnimNotifyContext ctx) { }
    }

    /// <summary>持续状态通知基类（对应 UE UAnimNotifyState）。</summary>
    public abstract class AnimNotifyState
    {
        public string Name = "";
        public float StartTime;
        public float EndTime;
        internal bool IsActive;

        /// <summary>进入区间回调。</summary>
        public virtual void Begin(AnimNotifyContext ctx) { }

        /// <summary>离开区间回调。</summary>
        public virtual void End(AnimNotifyContext ctx) { }
    }

    /// <summary>命名通知轨道（对应 UE UAnimNotifyTrack）。</summary>
    public sealed class MontageNotifyTrack
    {
        public string TrackName = "DefaultTrack";
        public List<AnimNotify> Notifies { get; } = new();
        public List<AnimNotifyState> States { get; } = new();

        public void AddNotify(AnimNotify notify)
        {
            if (notify != null) Notifies.Add(notify);
        }

        public void AddState(AnimNotifyState state)
        {
            if (state != null) States.Add(state);
        }
    }

    /// <summary>通知事件日志。</summary>
    public sealed class AnimNotifyLog
    {
        public List<string> Events { get; } = new();
        public void Log(string message) => Events.Add(message);
        public void Clear() => Events.Clear();
    }

    /// <summary>
    /// 蒙太奇通知运行时：绑定到 MontageInstance，逐帧评估所有通知轨道的
    /// 瞬时通知与持续状态通知，并把事件写入日志。
    /// </summary>
    public sealed class MontageNotifyRuntime
    {
        private readonly MontageInstance _instance;
        private readonly List<MontageNotifyTrack> _tracks;

        public AnimNotifyLog Log { get; } = new();

        /// <summary>瞬时通知触发事件。</summary>
        public event Action<AnimNotify>? NotifyFired;

        /// <summary>状态通知进入事件。</summary>
        public event Action<AnimNotifyState>? StateBegan;

        /// <summary>状态通知离开事件。</summary>
        public event Action<AnimNotifyState>? StateEnded;

        public MontageNotifyRuntime(MontageInstance instance, IEnumerable<MontageNotifyTrack> tracks)
        {
            _instance = instance ?? throw new ArgumentNullException(nameof(instance));
            _tracks = tracks?.ToList() ?? new List<MontageNotifyTrack>();
        }

        public IReadOnlyList<MontageNotifyTrack> Tracks => _tracks;

        /// <summary>逐帧评估通知（基于实例当前时间）。</summary>
        public void Tick(float deltaTime)
        {
            if (_instance == null) return;
            float t = _instance.CurrentTime;
            var ctx = new AnimNotifyContext
            {
                Instance = _instance,
                Skeleton = _instance.Skeleton,
                Time = t
            };

            foreach (var track in _tracks)
            {
                // 瞬时通知
                foreach (var n in track.Notifies)
                {
                    if (n.HasFired) continue;
                    if (t >= n.TriggerTime)
                    {
                        n.HasFired = true;
                        n.Fire(ctx);
                        NotifyFired?.Invoke(n);
                        Log.Log($"Notify:{track.TrackName}:{n.Name}");
                    }
                }

                // 持续状态通知
                foreach (var s in track.States)
                {
                    bool inRange = t >= s.StartTime && t < s.EndTime;
                    if (inRange && !s.IsActive)
                    {
                        s.IsActive = true;
                        s.Begin(ctx);
                        StateBegan?.Invoke(s);
                        Log.Log($"StateBegin:{track.TrackName}:{s.Name}");
                    }
                    else if (!inRange && s.IsActive)
                    {
                        s.IsActive = false;
                        s.End(ctx);
                        StateEnded?.Invoke(s);
                        Log.Log($"StateEnd:{track.TrackName}:{s.Name}");
                    }
                }
            }
        }

        /// <summary>重置所有通知/状态的触发标志（重新播放时调用）。</summary>
        public void Reset()
        {
            foreach (var track in _tracks)
            {
                foreach (var n in track.Notifies) n.HasFired = false;
                foreach (var s in track.States) s.IsActive = false;
            }
            Log.Clear();
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  蒙太奇分层（多 Slot 叠加）
    // ──────────────────────────────────────────────────────────────

    /// <summary>单个槽位层：SlotName + Weight + 蒙太奇实例 + 可选骨骼蒙版。</summary>
    public sealed class MontageSlotLayer
    {
        public string SlotName = "DefaultSlot";
        public float Weight = 1f;
        public MontageInstance? Instance;

        /// <summary>骨骼蒙版（null = 影响全部骨骼；否则仅影响集合内骨骼）。</summary>
        public HashSet<string>? Mask;

        public MontageSlotLayer(string slotName, MontageInstance instance, float weight = 1f)
        {
            SlotName = slotName;
            Instance = instance;
            Weight = weight;
        }
    }

    /// <summary>
    /// 蒙太奇分层栈：多个槽位层叠加合成最终姿势。
    /// 底层（低权重）先呈现，上层按权重覆盖（对应 UE 多 Slot 蒙太奇叠加）。
    /// </summary>
    public sealed class MontageLayerStack
    {
        public Skeleton Skeleton { get; }

        private readonly List<MontageSlotLayer> _layers = new();

        public MontageLayerStack(Skeleton skeleton)
        {
            Skeleton = skeleton ?? throw new ArgumentNullException(nameof(skeleton));
        }

        public IReadOnlyList<MontageSlotLayer> Layers => _layers;

        /// <summary>添加槽位层。</summary>
        public MontageSlotLayer AddLayer(string slotName, MontageInstance instance, float weight = 1f)
        {
            var layer = new MontageSlotLayer(slotName, instance, weight);
            _layers.Add(layer);
            return layer;
        }

        /// <summary>移除指定槽位层。</summary>
        public bool RemoveLayer(string slotName)
        {
            return _layers.RemoveAll(l => l.SlotName == slotName) > 0;
        }

        /// <summary>移除指定实例对应的层。</summary>
        public bool RemoveInstance(MontageInstance instance)
        {
            return _layers.RemoveAll(l => l.Instance == instance) > 0;
        }

        /// <summary>设置槽位层权重。</summary>
        public void SetLayerWeight(string slotName, float weight)
        {
            var layer = GetLayer(slotName);
            if (layer != null) layer.Weight = Math.Clamp(weight, 0f, 1f);
        }

        /// <summary>获取槽位层。</summary>
        public MontageSlotLayer? GetLayer(string slotName)
        {
            foreach (var l in _layers)
                if (string.Equals(l.SlotName, slotName, StringComparison.OrdinalIgnoreCase))
                    return l;
            return null;
        }

        public void Clear() => _layers.Clear();

        /// <summary>
        /// 采样叠加后的最终姿势：从绑定姿态开始，按权重从低到高逐层覆盖。
        /// 每层用蒙太奇实例当前姿势按权重混合（上层权重越高越占主导）。
        /// </summary>
        public AnimPoseOutput SampleBlendedPose()
        {
            int boneCount = Skeleton.Bones.Count;
            var bind = Skeleton.CreateBindPose();
            var result = AnimPoseOutput.Create(boneCount);
            result.CopyFromSkeletonPose(Skeleton, bind);

            var ordered = _layers
                .Where(l => l.Instance != null && l.Instance.IsActive)
                .OrderBy(l => l.Weight)
                .ToList();

            foreach (var layer in ordered)
            {
                var pose = layer.Instance!.SampleCurrentPose();
                if (pose == null) continue;
                BlendLayerInto(result, pose, layer);
            }
            return result;
        }

        private void BlendLayerInto(AnimPoseOutput dst, AnimPoseOutput src, MontageSlotLayer layer)
        {
            float w = Math.Clamp(layer.Weight, 0f, 1f);
            if (w <= 0f) return;
            int n = Math.Min(dst.BoneCount, src.BoneCount);
            for (int i = 0; i < n; i++)
            {
                string boneName = Skeleton.Bones[i].Name;
                if (layer.Mask != null && !layer.Mask.Contains(boneName)) continue;

                dst.Translations[i] = Vector3.Lerp(dst.Translations[i], src.Translations[i], w);
                dst.Rotations[i] = Quaternion.Slerp(dst.Rotations[i], src.Rotations[i], w);
                dst.Scales[i] = Vector3.Lerp(dst.Scales[i], src.Scales[i], w);
            }
        }
    }
}
