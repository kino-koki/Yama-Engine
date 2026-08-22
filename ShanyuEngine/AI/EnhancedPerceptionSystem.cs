// ============================================================================
//  ShanyuEngine.AI.EnhancedPerceptionSystem.cs
//  山雨引擎 -- 增强感知系统 (Enhanced Perception System)
//  对齐 UE5 AI Perception / AISenses / AI Memory / Attention
// ----------------------------------------------------------------------------
//  本文件实现 (与现有 AIPerceptionSystem 并存，使用 Enhanced 前缀)：
//    1. 多感知通道        - 视觉(锥形/球形)/听觉(范围衰减)/嗅觉(轨迹追踪)/触觉(碰撞)
//    2. 感知过滤          - 距离/角度/遮挡(AABB raycast)/伪装等级
//    3. 记忆系统          - 短期记忆(最近N秒)/长期记忆(类型/威胁/频率)/遗忘曲线(Ebbinghaus)
//    4. 注意力机制        - 多目标追踪，按威胁等级分配注意力预算
//    5. 感知事件通知      - OnPerceived/OnLost/OnHeard/OnSmelled
//    6. 感知调试          - 可视化感知锥/范围/已感知目标
// ----------------------------------------------------------------------------
//  使用 System.Numerics，兼容 ShanyuEngine.GameFramework.Actor。零异常空壳。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Numerics;
using ShanyuEngine.GameFramework;

namespace ShanyuEngine.AI
{
    // ========================================================================
    //  一、感知通道与刺激
    // ========================================================================

    /// <summary>感知通道。</summary>
    [Flags]
    public enum EnhancedSenseChannel
    {
        None = 0,
        Sight = 1,
        Hearing = 2,
        Smell = 4,
        Touch = 8,
        All = Sight | Hearing | Smell | Touch
    }

    /// <summary>感知事件类型。</summary>
    public enum EnhancedPerceptionEvent
    {
        Perceived,
        Updated,
        Lost,
        Heard,
        Smelled,
        Touched
    }

    /// <summary>
    /// 感知目标：缓存目标的位置/速度/威胁/伪装等信息，与 Actor 兼容。
    /// </summary>
    public sealed class EnhancedPerceptionTarget
    {
        public Actor? Actor;
        public int Id;
        public Vector3 Position;
        public Vector3 Velocity;
        public float Threat;            // 威胁等级 0~1
        public float Camouflage = 1f;   // 伪装等级: 1=正常可见, <1=更难发现, >1=更易发现
        public float Size = 1f;         // 体型 (影响可探测性)
        public string TypeTag = "";
        public object? UserData;
        /// <summary>位置缓存是否已同步 (由感官/外部刷新)。</summary>
        public bool Synced;

        public EnhancedPerceptionTarget(int id) { Id = id; }

        /// <summary>从 Actor 同步缓存数据。</summary>
        public void SyncFromActor()
        {
            if (Actor == null) return;
            Position = Actor.Position;
            TypeTag = Actor.Name;
        }
    }

    /// <summary>增强感知刺激。</summary>
    public sealed class EnhancedStimulus
    {
        public EnhancedSenseChannel Channel;
        public Vector3 Position;
        public Vector3 Direction;
        public float Strength;       // 0~1
        public float Timestamp;
        public EnhancedPerceptionTarget? Target;
        public bool IsNew;
    }

    // ========================================================================
    //  二、遮挡检测 (AABB Raycast)
    // ========================================================================

    /// <summary>轴对齐包围盒障碍。</summary>
    public struct AABBObstacle
    {
        public Vector3 Min;
        public Vector3 Max;
        public AABBObstacle(Vector3 min, Vector3 max) { Min = min; Max = max; }
    }

    /// <summary>
    /// AABB 遮挡检测器：用 slab 法测试线段是否穿过任意 AABB。
    /// </summary>
    public sealed class AABBOcclusionChecker
    {
        public List<AABBObstacle> Obstacles = new();

        public void AddBox(Vector3 center, Vector3 halfExtents)
        {
            Obstacles.Add(new AABBObstacle(center - halfExtents, center + halfExtents));
        }

        public void AddBox(Vector3 min, Vector3 max) { Obstacles.Add(new AABBObstacle(min, max)); }

        /// <summary>线段 from->to 是否被任意 AABB 遮挡 (返回首次命中距离 < 1 视为遮挡)。</summary>
        public bool IsOccluded(Vector3 from, Vector3 to)
        {
            Vector3 dir = to - from;
            float length = dir.Length();
            if (length < 1e-4f) return false;
            dir /= length;
            foreach (var box in Obstacles)
            {
                if (RayAABB(from, dir, length, box.Min, box.Max, out float t))
                {
                    if (t >= 0f && t < length) return true;
                }
            }
            return false;
        }

        /// <summary>slab 法射线-AABB 相交。返回 true 且 t 为命中参数。</summary>
        public static bool RayAABB(Vector3 origin, Vector3 dir, float maxDist,
            Vector3 min, Vector3 max, out float t)
        {
            t = 0f;
            float tmin = 0f, tmax = maxDist;
            // X
            if (MathF.Abs(dir.X) < 1e-8f)
            {
                if (origin.X < min.X || origin.X > max.X) return false;
            }
            else
            {
                float inv = 1f / dir.X;
                float t1 = (min.X - origin.X) * inv;
                float t2 = (max.X - origin.X) * inv;
                if (t1 > t2) (t1, t2) = (t2, t1);
                tmin = MathF.Max(tmin, t1);
                tmax = MathF.Min(tmax, t2);
                if (tmin > tmax) return false;
            }
            // Y
            if (MathF.Abs(dir.Y) < 1e-8f)
            {
                if (origin.Y < min.Y || origin.Y > max.Y) return false;
            }
            else
            {
                float inv = 1f / dir.Y;
                float t1 = (min.Y - origin.Y) * inv;
                float t2 = (max.Y - origin.Y) * inv;
                if (t1 > t2) (t1, t2) = (t2, t1);
                tmin = MathF.Max(tmin, t1);
                tmax = MathF.Min(tmax, t2);
                if (tmin > tmax) return false;
            }
            // Z
            if (MathF.Abs(dir.Z) < 1e-8f)
            {
                if (origin.Z < min.Z || origin.Z > max.Z) return false;
            }
            else
            {
                float inv = 1f / dir.Z;
                float t1 = (min.Z - origin.Z) * inv;
                float t2 = (max.Z - origin.Z) * inv;
                if (t1 > t2) (t1, t2) = (t2, t1);
                tmin = MathF.Max(tmin, t1);
                tmax = MathF.Min(tmax, t2);
                if (tmin > tmax) return false;
            }
            t = tmin;
            return tmin >= 0f && tmin <= maxDist;
        }
    }

    // ========================================================================
    //  三、感知过滤器
    // ========================================================================

    /// <summary>
    /// 感知过滤：综合距离/角度/遮挡/伪装判断目标是否可被探测及强度。
    /// </summary>
    public sealed class PerceptionFilter
    {
        public AABBOcclusionChecker Occlusion = new();
        public bool EnableOcclusion = true;
        public float AmbientNoise = 0.05f;

        /// <summary>计算视觉可探测强度 (0~1)，不可探测返回 0。</summary>
        public float SightStrength(Vector3 ownerPos, Vector3 ownerForward, float fovRad, float range,
            EnhancedPerceptionTarget target, bool useSphere, float sphereRange)
        {
            Vector3 to = target.Position - ownerPos;
            float dist = to.Length();
            if (dist > range) return 0f;
            // 伪装: 有效可见性 = 基础 / camouflage
            float visibility = target.Camouflage > 0 ? 1f / target.Camouflage : 1f;
            visibility *= Math.Clamp(target.Size, 0.1f, 5f);

            bool inCone = false;
            if (dist > 0.001f)
            {
                Vector3 dirNorm = to / dist;
                float dot = Vector3.Dot(ownerForward, dirNorm);
                float angle = MathF.Acos(Math.Clamp(dot, -1f, 1f));
                inCone = angle <= fovRad;
            }
            bool inSphere = useSphere && dist <= sphereRange;
            if (!inCone && !inSphere) return 0f;

            // 遮挡
            if (EnableOcclusion && Occlusion.IsOccluded(ownerPos, target.Position)) return 0f;

            // 距离衰减
            float strength = (1f - dist / range) * visibility;
            if (!inCone && inSphere) strength *= 0.4f; // 球形(余光)感知较弱
            return Math.Clamp(strength, 0f, 1f);
        }

        /// <summary>计算听觉强度 (距离线性衰减 - 环境噪声)。</summary>
        public float HearingStrength(Vector3 ownerPos, float range, Vector3 soundPos, float noiseLoudness)
        {
            float dist = Vector3.Distance(ownerPos, soundPos);
            if (dist > range) return 0f;
            float strength = noiseLoudness * (1f - dist / range) - AmbientNoise;
            return Math.Clamp(strength, 0f, 1f);
        }
    }

    // ========================================================================
    //  四、感官基类与各通道实现
    // ========================================================================

    public abstract class EnhancedSense
    {
        public EnhancedSenseChannel Channel;
        public bool Enabled = true;
        public abstract void Update(EnhancedPerceptionComponent comp, float dt, float time);
        public virtual void DebugDraw(EnhancedPerceptionComponent comp, PerceptionDebugDraw draw) { }
    }

    /// <summary>视觉感官：锥形(FOV) + 球形(近距全向)。</summary>
    public sealed class EnhancedSightSense : EnhancedSense
    {
        public float Range = 500f;
        public float FieldOfView = 90f;    // 度
        public float SphereRange = 50f;    // 近距全向感知
        public bool UseSphere = true;

        public EnhancedSightSense() { Channel = EnhancedSenseChannel.Sight; }

        public override void Update(EnhancedPerceptionComponent comp, float dt, float time)
        {
            if (!Enabled) return;
            Vector3 pos = comp.GetOwnerPosition();
            Vector3 fwd = comp.GetOwnerForward();
            float fovRad = FieldOfView * 0.5f * MathF.PI / 180f;
            foreach (var t in comp.Targets)
            {
                if (!t.Synced) t.SyncFromActor();
                float strength = comp.Filter.SightStrength(pos, fwd, fovRad, Range, t, UseSphere, SphereRange);
                if (strength <= 0f) continue;
                comp.RegisterStimulus(new EnhancedStimulus
                {
                    Channel = EnhancedSenseChannel.Sight,
                    Position = t.Position,
                    Direction = t.Position == pos ? fwd : Vector3.Normalize(t.Position - pos),
                    Strength = strength,
                    Timestamp = time,
                    Target = t
                });
            }
        }

        public override void DebugDraw(EnhancedPerceptionComponent comp, PerceptionDebugDraw draw)
        {
            Vector3 pos = comp.GetOwnerPosition();
            Vector3 fwd = comp.GetOwnerForward();
            float fovRad = FieldOfView * 0.5f * MathF.PI / 180f;
            float r = Range;
            Vector3 left = PerceptionMath.RotateY(fwd, fovRad) * r;
            Vector3 right = PerceptionMath.RotateY(fwd, -fovRad) * r;
            draw.DrawLine(pos, pos + left, new Vector3(0.2f, 1f, 0.2f));
            draw.DrawLine(pos, pos + right, new Vector3(0.2f, 1f, 0.2f));
            draw.DrawLine(pos + left, pos + right, new Vector3(0.2f, 1f, 0.2f));
            if (UseSphere) draw.DrawSphere(pos, SphereRange, new Vector3(0.2f, 0.6f, 0.2f));
        }
    }

    /// <summary>听觉感官：范围衰减，外部报告声音事件。</summary>
    public sealed class EnhancedHearingSense : EnhancedSense
    {
        public float Range = 300f;
        public float MaxAge = 5f;
        private readonly List<(Vector3 pos, float loudness, float time)> _events = new();

        public EnhancedHearingSense() { Channel = EnhancedSenseChannel.Hearing; }

        public void ReportNoise(Vector3 pos, float loudness, float time) => _events.Add((pos, loudness, time));

        public override void Update(EnhancedPerceptionComponent comp, float dt, float time)
        {
            if (!Enabled) return;
            Vector3 pos = comp.GetOwnerPosition();
            for (int i = _events.Count - 1; i >= 0; i--)
            {
                var e = _events[i];
                float age = time - e.time;
                if (age > MaxAge) { _events.RemoveAt(i); continue; }
                float strength = comp.Filter.HearingStrength(pos, Range, e.pos, e.loudness);
                if (strength <= 0f) continue;
                Vector3 dir = e.pos == pos ? Vector3.Zero : Vector3.Normalize(e.pos - pos);
                comp.RegisterStimulus(new EnhancedStimulus
                {
                    Channel = EnhancedSenseChannel.Hearing,
                    Position = e.pos,
                    Direction = dir,
                    Strength = strength,
                    Timestamp = time,
                    Target = null
                });
                comp.RaiseEvent(EnhancedPerceptionEvent.Heard, null, e.pos);
            }
        }

        public override void DebugDraw(EnhancedPerceptionComponent comp, PerceptionDebugDraw draw)
            => draw.DrawSphere(comp.GetOwnerPosition(), Range, new Vector3(0.5f, 0.3f, 1f));
    }

    /// <summary>嗅觉感官：追踪目标留下的气味轨迹。</summary>
    public sealed class EnhancedSmellSense : EnhancedSense
    {
        public float Range = 150f;
        public float MaxTrailAge = 8f;
        /// <summary>每个目标的气味轨迹 (目标Id -> 轨迹点)。</summary>
        private readonly Dictionary<int, List<(Vector3 pos, float time)>> _trails = new();

        public EnhancedSmellSense() { Channel = EnhancedSenseChannel.Smell; }

        /// <summary>由外部周期性投放气味点 (目标移动时调用)。</summary>
        public void DropScent(int targetId, Vector3 pos, float time)
        {
            if (!_trails.TryGetValue(targetId, out var list)) _trails[targetId] = list = new List<(Vector3, float)>();
            list.Add((pos, time));
        }

        public override void Update(EnhancedPerceptionComponent comp, float dt, float time)
        {
            if (!Enabled) return;
            Vector3 pos = comp.GetOwnerPosition();
            foreach (var kv in _trails)
            {
                var trail = kv.Value;
                // 找到范围内最新的可嗅轨迹点
                EnhancedStimulus? best = null;
                for (int i = trail.Count - 1; i >= 0; i--)
                {
                    var p = trail[i];
                    float age = time - p.time;
                    if (age > MaxTrailAge) { trail.RemoveAt(i); continue; }
                    float dist = Vector3.Distance(pos, p.pos);
                    if (dist > Range) continue;
                    // 越新越近越强
                    float strength = (1f - dist / Range) * (1f - age / MaxTrailAge);
                    if (strength <= 0f) continue;
                    if (best == null || strength > best.Strength)
                    {
                        best = new EnhancedStimulus
                        {
                            Channel = EnhancedSenseChannel.Smell,
                            Position = p.pos,
                            Direction = p.pos == pos ? Vector3.Zero : Vector3.Normalize(p.pos - pos),
                            Strength = Math.Clamp(strength, 0f, 1f),
                            Timestamp = time
                        };
                    }
                }
                if (best != null)
                {
                    comp.RegisterStimulus(best);
                    comp.RaiseEvent(EnhancedPerceptionEvent.Smelled, null, best.Position);
                }
            }
        }

        public override void DebugDraw(EnhancedPerceptionComponent comp, PerceptionDebugDraw draw)
            => draw.DrawSphere(comp.GetOwnerPosition(), Range, new Vector3(0.8f, 0.6f, 0.2f));
    }

    /// <summary>触觉感官：碰撞接触触发。</summary>
    public sealed class EnhancedTouchSense : EnhancedSense
    {
        public float Radius = 2f;
        public float MaxAge = 0.5f;
        private readonly List<(EnhancedPerceptionTarget? target, Vector3 point, float time)> _touches = new();

        public EnhancedTouchSense() { Channel = EnhancedSenseChannel.Touch; }

        public void ReportTouch(EnhancedPerceptionTarget? target, Vector3 point, float time) => _touches.Add((target, point, time));

        public override void Update(EnhancedPerceptionComponent comp, float dt, float time)
        {
            if (!Enabled) return;
            for (int i = _touches.Count - 1; i >= 0; i--)
            {
                var t = _touches[i];
                float age = time - t.time;
                if (age > MaxAge) { _touches.RemoveAt(i); continue; }
                comp.RegisterStimulus(new EnhancedStimulus
                {
                    Channel = EnhancedSenseChannel.Touch,
                    Position = t.point,
                    Direction = Vector3.Zero,
                    Strength = 1f,
                    Timestamp = time,
                    Target = t.target
                });
                comp.RaiseEvent(EnhancedPerceptionEvent.Touched, t.target, t.point);
            }
        }

        public override void DebugDraw(EnhancedPerceptionComponent comp, PerceptionDebugDraw draw)
            => draw.DrawSphere(comp.GetOwnerPosition(), Radius, new Vector3(1f, 0.4f, 0.4f));
    }

    // ========================================================================
    //  五、记忆系统 (短期 / 长期 / 遗忘曲线)
    // ========================================================================

    /// <summary>短期记忆条目：目标在某时刻的位置。</summary>
    public struct ShortTermMemoryEntry
    {
        public Vector3 Position;
        public float Time;
        public float Strength;
    }

    /// <summary>短期记忆：保留最近 N 秒的目标位置序列。</summary>
    public sealed class ShortTermMemory
    {
        public float Duration = 5f;
        public int MaxEntries = 30;
        private readonly Dictionary<int, LinkedList<ShortTermMemoryEntry>> _data = new();

        public void Record(int targetId, Vector3 pos, float time, float strength)
        {
            if (!_data.TryGetValue(targetId, out var list)) _data[targetId] = list = new LinkedList<ShortTermMemoryEntry>();
            list.AddLast(new ShortTermMemoryEntry { Position = pos, Time = time, Strength = strength });
            while (list.Count > MaxEntries) list.RemoveFirst();
        }

        /// <summary>清理过期条目。</summary>
        public void Update(float currentTime)
        {
            foreach (var list in _data.Values)
            {
                var node = list.First;
                while (node != null)
                {
                    var next = node.Next;
                    if (currentTime - node.Value.Time > Duration) list.Remove(node);
                    node = next;
                }
            }
        }

        public Vector3? LastKnownPosition(int targetId)
        {
            if (_data.TryGetValue(targetId, out var list) && list.Count > 0) return list.Last.Value.Position;
            return null;
        }

        public void Forget(int targetId) => _data.Remove(targetId);
        public void Clear() => _data.Clear();
    }

    /// <summary>长期记忆条目：实体类型的威胁/频率/记忆强度。</summary>
    public sealed class LongTermMemoryEntry
    {
        public string TypeTag = "";
        public float Threat;
        public int EncounterCount;
        public float LastSeenTime;
        public float MemoryStrength = 1f;   // 遗忘曲线的 S 参数
        public float Salience = 1f;          // 初始显著度
    }

    /// <summary>
    /// 遗忘曲线 (Ebbinghaus 模型): R(t) = e^(-t/S)。
    /// S (记忆强度) 随重复次数增长，复现时刷新时间戳并增强 S。
    /// </summary>
    public static class ForgettingCurve
    {
        /// <summary>计算保留率 (0~1)。</summary>
        public static float Retention(float elapsed, float strength)
            => MathF.Exp(-elapsed / MathF.Max(strength, 0.01f));

        /// <summary>每次复现后增强记忆强度 (对数增长，避免无限增大)。</summary>
        public static float Reinforce(float currentStrength, int reviewCount)
            => currentStrength * (1f + 0.5f / (1f + reviewCount * 0.1f));
    }

    /// <summary>长期记忆：按实体类型记录威胁/频率/遗忘。</summary>
    public sealed class LongTermMemory
    {
        private readonly Dictionary<string, LongTermMemoryEntry> _entries = new();

        /// <summary>记录一次遭遇。</summary>
        public void Encounter(string typeTag, float threat, float time)
        {
            if (!_entries.TryGetValue(typeTag, out var e))
            {
                e = new LongTermMemoryEntry { TypeTag = typeTag, Threat = threat, Salience = Math.Clamp(threat, 0.1f, 1f) };
                _entries[typeTag] = e;
            }
            e.EncounterCount++;
            e.LastSeenTime = time;
            e.Threat = Math.Clamp(e.Threat * 0.7f + threat * 0.3f, 0f, 1f);
            e.MemoryStrength = ForgettingCurve.Reinforce(e.MemoryStrength, e.EncounterCount);
        }

        /// <summary>查询某类型的当前记忆置信度 (保留率 * 显著度)。</summary>
        public float Confidence(string typeTag, float currentTime)
        {
            if (!_entries.TryGetValue(typeTag, out var e)) return 0f;
            return ForgettingCurve.Retention(currentTime - e.LastSeenTime, e.MemoryStrength) * e.Salience;
        }

        public LongTermMemoryEntry? GetEntry(string typeTag)
            => _entries.TryGetValue(typeTag, out var e) ? e : null;

        /// <summary>更新：对长期记忆做整体衰减 (可选)。</summary>
        public void Update(float currentTime) { /* 保留率通过 Confidence 实时计算，无需显式衰减 */ }

        public void Clear() => _entries.Clear();
    }

    /// <summary>记忆系统：整合短期 + 长期记忆。</summary>
    public sealed class PerceptionMemorySystem
    {
        public ShortTermMemory ShortTerm = new();
        public LongTermMemory LongTerm = new();

        public void Record(EnhancedPerceptionTarget target, Vector3 pos, float time, float strength)
        {
            ShortTerm.Record(target.Id, pos, time, strength);
            LongTerm.Encounter(target.TypeTag, target.Threat, time);
        }

        public void Update(float currentTime)
        {
            ShortTerm.Update(currentTime);
            LongTerm.Update(currentTime);
        }

        public Vector3? LastKnownPosition(int targetId) => ShortTerm.LastKnownPosition(targetId);
        public float TypeConfidence(string typeTag, float time) => LongTerm.Confidence(typeTag, time);
    }

    // ========================================================================
    //  六、注意力机制
    // ========================================================================

    /// <summary>注意力分配条目。</summary>
    public sealed class AttentionEntry
    {
        public EnhancedPerceptionTarget Target;
        public float Threat;
        public float Attention;     // 分配的注意力 (0~1)
        public bool IsFocus;        // 是否为当前焦点
    }

    /// <summary>
    /// 注意力系统：基于威胁等级在多个目标间分配有限注意力预算。
    /// 高威胁目标获得更多注意力；超过最大追踪数的目标被忽略。
    /// </summary>
    public sealed class AttentionSystem
    {
        public int MaxTracked = 5;
        public float TotalBudget = 1f;
        public float FocusThreshold = 0.3f;
        public readonly List<AttentionEntry> Entries = new();
        public EnhancedPerceptionTarget? FocusTarget;

        /// <summary>按威胁重新分配注意力 (softmax 归一化)。</summary>
        public void Reallocate(Dictionary<int, EnhancedPerceptionTarget> perceived, float time, PerceptionMemorySystem memory)
        {
            Entries.Clear();
            // 收集候选并按威胁排序
            var candidates = new List<(EnhancedPerceptionTarget t, float score)>();
            foreach (var t in perceived.Values)
            {
                float conf = memory.TypeConfidence(t.TypeTag, time);
                float score = t.Threat * 0.7f + conf * 0.3f;
                candidates.Add((t, score));
            }
            candidates.Sort((a, b) => b.score.CompareTo(a.score));

            // 取前 MaxTracked 个
            int count = Math.Min(MaxTracked, candidates.Count);
            float sum = 0;
            for (int i = 0; i < count; i++) sum += MathF.Exp(candidates[i].score * 2f);
            if (sum <= 0) sum = 1;

            FocusTarget = null;
            float maxAttn = 0;
            for (int i = 0; i < count; i++)
            {
                float attn = MathF.Exp(candidates[i].score * 2f) / sum;
                var entry = new AttentionEntry { Target = candidates[i].t, Threat = candidates[i].t.Threat, Attention = attn };
                if (attn > FocusThreshold && attn > maxAttn) { maxAttn = attn; }
                Entries.Add(entry);
            }
            // 标记焦点
            foreach (var e in Entries)
            {
                e.IsFocus = (e.Attention >= maxAttn && e.Attention > FocusThreshold);
                if (e.IsFocus) FocusTarget = e.Target;
            }
        }

        /// <summary>是否在追踪某目标。</summary>
        public bool IsTracked(int targetId)
        {
            foreach (var e in Entries) if (e.Target.Id == targetId) return true;
            return false;
        }
    }

    // ========================================================================
    //  七、增强感知组件 (主类)
    // ========================================================================

    /// <summary>调试绘制回调集。</summary>
    public sealed class PerceptionDebugDraw
    {
        public Action<Vector3, Vector3, Vector3>? DrawLine;     // from, to, color
        public Action<Vector3, float, Vector3>? DrawSphere;     // center, radius, color
        public Action<Vector3, string, Vector3>? DrawText;      // pos, text, color
    }

    /// <summary>
    /// 增强感知组件：管理多通道感官、记忆、注意力与事件。
    /// 兼容 ShanyuEngine.GameFramework.Actor 作为 owner/target。
    /// </summary>
    public sealed class EnhancedPerceptionComponent
    {
        public Actor? Owner;
        private Func<Vector3>? _getPos;
        private Func<Vector3>? _getFwd;
        public PerceptionFilter Filter = new();
        public PerceptionMemorySystem Memory = new();
        public AttentionSystem Attention = new();
        public List<EnhancedSense> Senses = new();
        public List<EnhancedPerceptionTarget> Targets = new();

        private readonly Dictionary<int, EnhancedPerceptionTarget> _perceived = new();
        private float _time;

        /// <summary>感知事件。</summary>
        public event Action<EnhancedPerceptionEvent, EnhancedPerceptionTarget?, Vector3>? OnPerceptionEvent;
        public event Action<EnhancedPerceptionTarget>? OnPerceived;
        public event Action<EnhancedPerceptionTarget>? OnLost;
        public event Action<Vector3>? OnHeard;
        public event Action<Vector3>? OnSmelled;

        public bool DebugEnabled;
        public PerceptionDebugDraw? DebugDraw;

        public EnhancedPerceptionComponent() { }
        public EnhancedPerceptionComponent(Actor owner) { Owner = owner; }

        /// <summary>设置位置/朝向提供者 (无 Actor 时用)。</summary>
        public void SetTransformProviders(Func<Vector3> getPos, Func<Vector3> getFwd)
        { _getPos = getPos; _getFwd = getFwd; }

        public void RegisterDefaultSenses()
        {
            if (Senses.Count == 0)
            {
                Senses.Add(new EnhancedSightSense());
                Senses.Add(new EnhancedHearingSense());
                Senses.Add(new EnhancedSmellSense());
                Senses.Add(new EnhancedTouchSense());
            }
        }

        public Vector3 GetOwnerPosition()
            => _getPos?.Invoke() ?? Owner?.Position ?? Vector3.Zero;
        public Vector3 GetOwnerForward()
        {
            if (_getFwd != null) return _getFwd();
            if (Owner != null) return Vector3.Transform(Vector3.UnitZ, Owner.Rotation);
            return Vector3.UnitZ;
        }

        /// <summary>注册目标 (可来自 Actor)。</summary>
        public EnhancedPerceptionTarget RegisterTarget(Actor actor, int id, float threat = 0.5f)
        {
            var t = new EnhancedPerceptionTarget(id) { Actor = actor, Threat = threat, Synced = true };
            t.SyncFromActor();
            Targets.Add(t);
            return t;
        }
        public void RegisterTarget(EnhancedPerceptionTarget t) => Targets.Add(t);
        public void UnregisterTarget(int id)
        {
            Targets.RemoveAll(t => t.Id == id);
            _perceived.Remove(id);
            Memory.ShortTerm.Forget(id);
        }

        /// <summary>注册刺激 (内部由感官调用)。</summary>
        public void RegisterStimulus(EnhancedStimulus stim)
        {
            if (stim.Target != null)
            {
                bool isNew = !_perceived.ContainsKey(stim.Target.Id);
                stim.IsNew = isNew;
                _perceived[stim.Target.Id] = stim.Target;
                Memory.Record(stim.Target, stim.Position, stim.Timestamp, stim.Strength);
                if (isNew)
                {
                    OnPerceived?.Invoke(stim.Target);
                    RaiseEvent(EnhancedPerceptionEvent.Perceived, stim.Target, stim.Position);
                }
                else
                {
                    RaiseEvent(EnhancedPerceptionEvent.Updated, stim.Target, stim.Position);
                }
            }
        }

        public void RaiseEvent(EnhancedPerceptionEvent evt, EnhancedPerceptionTarget? target, Vector3 pos)
        {
            OnPerceptionEvent?.Invoke(evt, target, pos);
            switch (evt)
            {
                case EnhancedPerceptionEvent.Heard: OnHeard?.Invoke(pos); break;
                case EnhancedPerceptionEvent.Smelled: OnSmelled?.Invoke(pos); break;
            }
        }

        /// <summary>每帧更新：感官 -> 记忆 -> 注意力 -> 丢失检测。</summary>
        public void Update(float dt)
        {
            _time += dt;
            // 同步目标缓存
            foreach (var t in Targets) if (t.Actor != null) t.SyncFromActor();

            foreach (var s in Senses) s.Update(this, dt, _time);

            Memory.Update(_time);
            Attention.Reallocate(_perceived, _time, Memory);

            // 丢失检测：短期记忆中已无条目(超龄清理)的目标视为丢失
            var toRemove = new List<int>();
            foreach (var id in _perceived.Keys)
                if (!Memory.LastKnownPosition(id).HasValue) toRemove.Add(id);
            foreach (var id in toRemove)
            {
                if (_perceived.TryGetValue(id, out var t)) OnLost?.Invoke(t);
                _perceived.Remove(id);
                RaiseEvent(EnhancedPerceptionEvent.Lost, null, Vector3.Zero);
            }

            if (DebugEnabled && DebugDraw != null)
                foreach (var s in Senses) s.DebugDraw(this, DebugDraw);
        }

        /// <summary>获取当前感知到的目标。</summary>
        public IReadOnlyDictionary<int, EnhancedPerceptionTarget> PerceivedTargets => _perceived;
        public EnhancedPerceptionTarget? GetFocusTarget() => Attention.FocusTarget;
        public Vector3? GetLastKnownPosition(int targetId) => Memory.LastKnownPosition(targetId);

        public void Clear()
        {
            _perceived.Clear();
            Targets.Clear();
            Memory.ShortTerm.Clear();
            Memory.LongTerm.Clear();
            Attention.Entries.Clear();
        }
    }

    // ========================================================================
    //  八、辅助工具
    // ========================================================================

    internal static class PerceptionMath
    {
        public static Vector3 RotateY(Vector3 v, float angle)
        {
            float cos = MathF.Cos(angle), sin = MathF.Sin(angle);
            return new Vector3(v.X * cos + v.Z * sin, v.Y, -v.X * sin + v.Z * cos);
        }
    }
}
