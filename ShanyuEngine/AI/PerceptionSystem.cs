// ============================================================================
//  ShanyuEngine.AI.PerceptionSystem.cs
//  山雨引擎 -- 感知系统
//  对齐 UE5 AI Perception / Unity Perception / Splinter Cell AI
// ----------------------------------------------------------------------------
//  实现完整的 AI 感知：
//    1. PerceptionSense       - 感官基类(视觉/听觉/嗅觉/触觉)
//    2. VisualSense           - 视觉(视野锥 + 遮挡检测)
//    3. HearingSense          - 听觉(声音传播 + 衰减)
//    4. SmellSense            - 嗅觉(气味扩散)
//    5. DamageSense           - 伤害感知
//    6. TouchSense            - 触觉(碰撞)
//    7. PerceptionStimulus    - 刺激源
//    8. PerceptionTarget      - 被感知的目标
//    9. PerceptionComponent   - 单个 agent 的感知组件
//   10. PerceptionSystem      - 全局感知系统(管理刺激传播)
//   11. PerceptionMemory      - 感知记忆(最近看到的/历史记录)
// ----------------------------------------------------------------------------
//  注意：视觉感知的遮挡检测是性能热点，得用空间分区加速。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Numerics;
using ShanyuEngine.Physics;

namespace ShanyuEngine.AI
{
    // ========================================================================
    //  一、感知类型枚举
    // ========================================================================

    /// <summary>感知类型(可组合)。</summary>
    [Flags]
    public enum PerceptionSenseFlags
    {
        None = 0,
        Sight = 1,
        Hearing = 2,
        Smell = 4,
        Damage = 8,
        Touch = 16,
        All = 0xFF
    }

    /// <summary>感知状态。</summary>
    public enum PerceptionState
    {
        /// <summary>从未感知。</summary>
        Unknown,
        /// <summary>刚感知到。</summary>
        Detected,
        /// <summary>持续感知。</summary>
        Tracked,
        /// <summary>失去感知(还在记忆里)。</summary>
        Lost,
        /// <summary>记忆也过期了。</summary>
        Forgotten
    }

    // ========================================================================
    //  二、刺激源
    // ========================================================================

    /// <summary>
    /// 感知刺激源。可以是任何能被感知的实体(玩家/物体/声音/气味)。
    /// </summary>
    public sealed class PerceptionStimulus
    {
        public int Id;
        public int OwnerId; // 拥有者 agent id
        public Vector3 Position;
        public Vector3 Velocity;
        /// <summary>该刺激源会触发的感官类型。</summary>
        public PerceptionSenseFlags SenseFlags;
        /// <summary>视觉可见性(0=不可见,1=完全可见)。</summary>
        public float Visibility = 1f;
        /// <summary>声音强度(db 或自定义)。</summary>
        public float Loudness = 0f;
        /// <summary>气味强度。</summary>
        public float SmellIntensity = 0f;
        /// <summary>团队/阵营(用于敌我识别)。</summary>
        public int FactionId = -1;
        /// <summary>类型标签(玩家/NPC/物品)。</summary>
        public string Tag = "";
        /// <summary>刺激源是否激活。</summary>
        public bool IsActive = true;
        /// <summary>半径(碰撞感知用)。</summary>
        public float Radius = 0.3f;

        public PerceptionStimulus(int id) { Id = id; }
    }

    /// <summary>瞬时刺激(声音事件)。</summary>
    public struct PerceptionEvent
    {
        public Vector3 Position;
        public float Loudness;
        public float Radius;
        public int SourceId;
        public float Timestamp;
        public string Tag;
    }

    // ========================================================================
    //  三、感知目标(每个 agent 对每个目标的跟踪数据)
    // ========================================================================

    /// <summary>
    /// 感知目标。记录 agent 对某个 stimulus 的感知状态。
    /// </summary>
    public sealed class PerceptionTarget
    {
        public int TargetId;
        public PerceptionStimulus Stimulus;
        public PerceptionState State;
        public PerceptionSenseFlags SensedBy;
        /// <summary>最后感知到的位置。</summary>
        public Vector3 LastKnownPosition;
        /// <summary>最后感知到的时间。</summary>
        public float LastSensedTime;
        /// <summary>首次感知时间。</summary>
        public float FirstSensedTime;
        /// <summary>感知强度(0~1)。</summary>
        public float Strength;
        /// <summary>距离。</summary>
        public float Distance;
        /// <summary>方向(单位向量)。</summary>
        public Vector3 Direction;
        /// <summary>目标速度估计。</summary>
        public Vector3 EstimatedVelocity;

        public PerceptionTarget(int targetId, PerceptionStimulus stimulus)
        {
            TargetId = targetId; Stimulus = stimulus;
            State = PerceptionState.Unknown;
        }

        public bool IsCurrentlyVisible => State == PerceptionState.Detected || State == PerceptionState.Tracked;
        public float TimeSinceLastSensed(float currentTime) => currentTime - LastSensedTime;
    }

    // ========================================================================
    //  四、感官配置
    // ========================================================================

    /// <summary>视觉配置。</summary>
    public struct SightConfig
    {
        public float SightRadius;        // 视野距离
        public float LoseSightRadius;    // 失去视野距离(通常大于 SightRadius)
        public float FieldOfView;        // 视野角度(度)
        public bool UsePeripheralVision; // 是否使用周边视觉
        public float PeripheralVisionAngle; // 周边视野角度
        public float PeripheralVisionThreshold; // 周边视野识别阈值(0~1)
        public float AutoSuccessRange;   // 此距离内自动识别
        public bool DetectNearestFraction; // 只识别最近的 N 个

        public static SightConfig Default => new()
        {
            SightRadius = 20f,
            LoseSightRadius = 25f,
            FieldOfView = 90f,
            UsePeripheralVision = true,
            PeripheralVisionAngle = 150f,
            PeripheralVisionThreshold = 0.5f,
            AutoSuccessRange = 2f,
            DetectNearestFraction = false
        };
    }

    /// <summary>听觉配置。</summary>
    public struct HearingConfig
    {
        public float HearingRange;    // 听觉范围
        public float NoiseThreshold;  // 噪声阈值
        public float AttenuationFactor; // 衰减系数

        public static HearingConfig Default => new()
        {
            HearingRange = 15f,
            NoiseThreshold = 0.1f,
            AttenuationFactor = 1f
        };
    }

    /// <summary>嗅觉配置。</summary>
    public struct SmellConfig
    {
        public float SmellRange;     // 嗅觉范围
        public float SmellThreshold; // 阈值
        public float DecayRate;      // 气味残留衰减

        public static SmellConfig Default => new()
        {
            SmellRange = 10f,
            SmellThreshold = 0.1f,
            DecayRate = 0.1f
        };
    }

    /// <summary>伤害感知配置。</summary>
    public struct DamageConfig
    {
        public float DamageThreshold;

        public static DamageConfig Default => new() { DamageThreshold = 0f };
    }

    // ========================================================================
    //  五、感官基类
    // ========================================================================

    /// <summary>
    /// 感官基类。每个感官负责一种感知模式。
    /// </summary>
    public abstract class PerceptionSense
    {
        public PerceptionSenseFlags SenseType;
        public bool Enabled = true;

        public abstract void Update(PerceptionComponent owner, List<PerceptionStimulus> stimuli, float dt);

        /// <summary>更新已有目标的记忆状态。</summary>
        public virtual void UpdateMemory(PerceptionComponent owner, float dt) { }
    }

    // ========================================================================
    //  六、视觉
    // ========================================================================

    /// <summary>
    /// 视觉感知。视野锥 + 距离 + 遮挡检测。
    /// 这是最常用的感官，也是最耗性能的(遮挡检测)。
    /// </summary>
    public sealed class VisualSense : PerceptionSense
    {
        public SightConfig Config = SightConfig.Default;
        /// <summary>遮挡检测回调(返回 true 表示被挡住)。</summary>
        public Func<Vector3, Vector3, bool>? LineOfSightTest;
        /// <summary>空间查询回调(返回范围内的 stimulus)。</summary>
        public Func<Vector3, float, List<PerceptionStimulus>>? SpatialQuery;

        public VisualSense()
        {
            SenseType = PerceptionSenseFlags.Sight;
        }

        public override void Update(PerceptionComponent owner, List<PerceptionStimulus> stimuli, float dt)
        {
            if (!Enabled || !owner.IsEnabled) return;

            var forward = owner.GetForward();
            float cosHalfFov = MathF.Cos(Config.FieldOfView * 0.5f * MathF.PI / 180f);
            float cosHalfPeripheral = MathF.Cos(Config.PeripheralVisionAngle * 0.5f * MathF.PI / 180f);

            foreach (var stim in stimuli)
            {
                if (!stim.IsActive || (stim.SenseFlags & PerceptionSenseFlags.Sight) == 0) continue;

                var toTarget = stim.Position - owner.Position;
                float dist = toTarget.Length();
                if (dist > Config.LoseSightRadius) continue;
                if (dist < 0.01f) continue;

                var dir = toTarget / dist;
                float dot = Vector3.Dot(forward, dir);

                // 判断是否在视野内
                bool inMainFov = dot >= cosHalfFov;
                bool inPeripheral = Config.UsePeripheralVision && dot >= cosHalfPeripheral
                    && stim.Visibility >= Config.PeripheralVisionThreshold;
                bool autoSuccess = dist <= Config.AutoSuccessRange;

                if (!inMainFov && !inPeripheral && !autoSuccess) continue;

                // 遮挡检测
                if (LineOfSightTest != null && !autoSuccess)
                {
                    if (LineOfSightTest(owner.Position, stim.Position)) continue;
                }

                // 计算感知强度
                float strength = 1f;
                if (dist > Config.SightRadius)
                    strength = 1f - (dist - Config.SightRadius) / (Config.LoseSightRadius - Config.SightRadius);
                strength *= stim.Visibility;
                if (inPeripheral && !inMainFov)
                    strength *= 0.6f;

                owner.RegisterSensing(stim, PerceptionSenseFlags.Sight, strength, dist, dir);
            }
        }
    }

    // ========================================================================
    //  七、听觉
    // ========================================================================

    /// <summary>
    /// 听觉感知。处理声音事件 + 持续噪声源。
    /// </summary>
    public sealed class HearingSense : PerceptionSense
    {
        public HearingConfig Config = HearingConfig.Default;
        /// <summary>声音事件队列(由外部系统推送)。</summary>
        public List<PerceptionEvent> PendingEvents = new();

        public HearingSense() { SenseType = PerceptionSenseFlags.Hearing; }

        public override void Update(PerceptionComponent owner, List<PerceptionStimulus> stimuli, float dt)
        {
            if (!Enabled || !owner.IsEnabled) return;

            // 处理瞬时声音事件
            for (int i = PendingEvents.Count - 1; i >= 0; i--)
            {
                var ev = PendingEvents[i];
                var toOwner = owner.Position - ev.Position;
                float dist = toOwner.Length();
                if (dist > ev.Radius) { PendingEvents.RemoveAt(i); continue; }
                // 衰减
                float strength = ev.Loudness * (1f - dist / ev.Radius) * Config.AttenuationFactor;
                if (strength < Config.NoiseThreshold) { PendingEvents.RemoveAt(i); continue; }
                // 找对应的 stimulus
                var stim = stimuli.Find(s => s.Id == ev.SourceId);
                if (stim != null)
                {
                    var dir = dist > 0.01f ? toOwner / dist : Vector3.Zero;
                    owner.RegisterSensing(stim, PerceptionSenseFlags.Hearing, strength, dist, dir);
                }
                PendingEvents.RemoveAt(i);
            }

            // 处理持续噪声源
            foreach (var stim in stimuli)
            {
                if (!stim.IsActive || stim.Loudness <= 0) continue;
                if ((stim.SenseFlags & PerceptionSenseFlags.Hearing) == 0) continue;
                var toTarget = stim.Position - owner.Position;
                float dist = toTarget.Length();
                if (dist > Config.HearingRange) continue;
                float strength = stim.Loudness * (1f - dist / Config.HearingRange);
                if (strength < Config.NoiseThreshold) continue;
                var dir = dist > 0.01f ? toTarget / dist : Vector3.Zero;
                owner.RegisterSensing(stim, PerceptionSenseFlags.Hearing, strength, dist, dir);
            }
        }

        /// <summary>推送一个声音事件。</summary>
        public void ReportNoise(Vector3 pos, float loudness, float radius, int sourceId, float time, string tag = "")
        {
            PendingEvents.Add(new PerceptionEvent
            {
                Position = pos, Loudness = loudness, Radius = radius,
                SourceId = sourceId, Timestamp = time, Tag = tag
            });
        }
    }

    // ========================================================================
    //  八、嗅觉
    // ========================================================================

    /// <summary>
    /// 嗅觉感知。气味会扩散并残留，可以追踪气味轨迹。
    /// </summary>
    public sealed class SmellSense : PerceptionSense
    {
        public SmellConfig Config = SmellConfig.Default;
        /// <summary>气味残留网格(给气味追踪用)。</summary>
        public Dictionary<long, (float intensity, float timestamp)> SmellGrid = new();
        public float GridCellSize = 2f;

        public SmellSense() { SenseType = PerceptionSenseFlags.Smell; }

        public override void Update(PerceptionComponent owner, List<PerceptionStimulus> stimuli, float dt)
        {
            if (!Enabled || !owner.IsEnabled) return;

            // 检测当前气味源
            foreach (var stim in stimuli)
            {
                if (!stim.IsActive || stim.SmellIntensity <= 0) continue;
                if ((stim.SenseFlags & PerceptionSenseFlags.Smell) == 0) continue;
                var toTarget = stim.Position - owner.Position;
                float dist = toTarget.Length();
                if (dist > Config.SmellRange) continue;
                float strength = stim.SmellIntensity * (1f - dist / Config.SmellRange);
                if (strength < Config.SmellThreshold) continue;
                var dir = dist > 0.01f ? toTarget / dist : Vector3.Zero;
                owner.RegisterSensing(stim, PerceptionSenseFlags.Smell, strength, dist, dir);
                // 在残留网格里标记
                MarkSmell(stim.Position, stim.SmellIntensity * dt, owner.CurrentTime);
            }
        }

        public override void UpdateMemory(PerceptionComponent owner, float dt)
        {
            // 衰减气味残留
            var keys = new List<long>(SmellGrid.Keys);
            foreach (var k in keys)
            {
                var v = SmellGrid[k];
                v.intensity -= Config.DecayRate * dt;
                if (v.intensity <= 0) SmellGrid.Remove(k);
                else SmellGrid[k] = v;
            }
        }

        private void MarkSmell(Vector3 pos, float intensity, float time)
        {
            long key = GridKey(pos);
            if (SmellGrid.TryGetValue(key, out var existing))
                SmellGrid[key] = (existing.intensity + intensity, time);
            else
                SmellGrid[key] = (intensity, time);
        }

        private long GridKey(Vector3 pos)
        {
            int x = (int)MathF.Floor(pos.X / GridCellSize);
            int z = (int)MathF.Floor(pos.Z / GridCellSize);
            return ((long)x << 32) | (uint)z;
        }

        /// <summary>获取某位置的气味强度。</summary>
        public float GetSmellAt(Vector3 pos)
            => SmellGrid.TryGetValue(GridKey(pos), out var v) ? v.intensity : 0f;
    }

    // ========================================================================
    //  九、伤害感知
    // ========================================================================

    public sealed class DamageSense : PerceptionSense
    {
        public DamageConfig Config = DamageConfig.Default;
        public List<(int sourceId, float damage, Vector3 position, float time)> PendingDamage = new();

        public DamageSense() { SenseType = PerceptionSenseFlags.Damage; }

        public override void Update(PerceptionComponent owner, List<PerceptionStimulus> stimuli, float dt)
        {
            if (!Enabled || !owner.IsEnabled) return;
            for (int i = PendingDamage.Count - 1; i >= 0; i--)
            {
                var dmg = PendingDamage[i];
                if (dmg.damage < Config.DamageThreshold) { PendingDamage.RemoveAt(i); continue; }
                var stim = stimuli.Find(s => s.Id == dmg.sourceId);
                if (stim != null)
                {
                    var toTarget = dmg.position - owner.Position;
                    float dist = toTarget.Length();
                    var dir = dist > 0.01f ? toTarget / dist : Vector3.Zero;
                    owner.RegisterSensing(stim, PerceptionSenseFlags.Damage, 1f, dist, dir);
                }
                PendingDamage.RemoveAt(i);
            }
        }

        public void ReportDamage(int sourceId, float damage, Vector3 sourcePos, float time)
        {
            PendingDamage.Add((sourceId, damage, sourcePos, time));
        }
    }

    // ========================================================================
    //  十、触觉感知
    // ========================================================================

    public sealed class TouchSense : PerceptionSense
    {
        public float TouchRange = 0.5f;

        public TouchSense() { SenseType = PerceptionSenseFlags.Touch; }

        public override void Update(PerceptionComponent owner, List<PerceptionStimulus> stimuli, float dt)
        {
            if (!Enabled || !owner.IsEnabled) return;
            foreach (var stim in stimuli)
            {
                if (!stim.IsActive) continue;
                if ((stim.SenseFlags & PerceptionSenseFlags.Touch) == 0) continue;
                float dist = Vector3.Distance(owner.Position, stim.Position);
                if (dist > TouchRange + stim.Radius) continue;
                var toTarget = stim.Position - owner.Position;
                var dir = dist > 0.01f ? toTarget / dist : Vector3.Zero;
                owner.RegisterSensing(stim, PerceptionSenseFlags.Touch, 1f, dist, dir);
            }
        }
    }

    // ========================================================================
    //  十一、感知组件
    // ========================================================================

    /// <summary>
    /// 感知组件。每个 agent 一个，管理它的所有感官和目标。
    /// </summary>
    public sealed class PerceptionComponent
    {
        public int AgentId;
        public Vector3 Position;
        public Vector3 Facing;
        public bool IsEnabled = true;
        public float CurrentTime;
        public int FactionId = -1;
        public float Radius = 0.3f;

        private readonly List<PerceptionSense> _senses = new();
        private readonly Dictionary<int, PerceptionTarget> _targets = new();
        /// <summary>记忆配置。</summary>
        public float MemoryDuration = 5f;
        public float ForgetDuration = 10f;
        /// <summary>每帧最多处理的目标数(性能限制)。</summary>
        public int MaxTargetsPerUpdate = 20;

        /// <summary>感知事件(给外部系统订阅)。</summary>
        public event Action<PerceptionTarget>? OnTargetPerceived;
        public event Action<PerceptionTarget>? OnTargetLost;
        public event Action<PerceptionTarget>? OnTargetForgotten;

        public PerceptionComponent(int agentId) { AgentId = agentId; }

        public void AddSense(PerceptionSense sense) => _senses.Add(sense);
        public T? GetSense<T>() where T : PerceptionSense
        {
            foreach (var s in _senses) if (s is T t) return t;
            return null;
        }
        public IEnumerable<PerceptionTarget> GetAllTargets() => _targets.Values;
        public PerceptionTarget? GetTarget(int targetId)
            => _targets.TryGetValue(targetId, out var t) ? t : null;

        public Vector3 GetForward() => Facing == Vector3.Zero ? Vector3.UnitZ : Vector3.Normalize(Facing);

        /// <summary>每帧更新。stimuli 是系统提供的所有可能刺激源。</summary>
        public void Update(List<PerceptionStimulus> stimuli, float dt)
        {
            if (!IsEnabled) return;
            CurrentTime += dt;

            // 先标记所有目标为 "可能丢失"
            var lostCandidates = new List<int>();
            foreach (var (id, target) in _targets)
            {
                if (target.State == PerceptionState.Detected || target.State == PerceptionState.Tracked)
                    target.State = PerceptionState.Lost; // 临时标记，若再被感知到会改回
            }

            // 各感官更新
            foreach (var sense in _senses)
            {
                sense.Update(this, stimuli, dt);
                sense.UpdateMemory(this, dt);
            }

            // 检查目标状态转换
            var toRemove = new List<int>();
            foreach (var (id, target) in _targets)
            {
                float timeSince = target.TimeSinceLastSensed(CurrentTime);
                if (target.State == PerceptionState.Lost)
                {
                    if (timeSince > MemoryDuration)
                    {
                        target.State = PerceptionState.Forgotten;
                        OnTargetLost?.Invoke(target);
                    }
                    else
                    {
                        // 真的 lost 了(本帧没被感知到)
                        if (target.SensedBy == 0)
                        {
                            // 仍然保持 Lost
                        }
                    }
                }
                if (target.State == PerceptionState.Forgotten && timeSince > ForgetDuration)
                {
                    toRemove.Add(id);
                    OnTargetForgotten?.Invoke(target);
                }
            }
            foreach (var id in toRemove) _targets.Remove(id);
        }

        /// <summary>注册一次感知(由 sense 调用)。</summary>
        public void RegisterSensing(PerceptionStimulus stim, PerceptionSenseFlags sense, float strength, float dist, Vector3 dir)
        {
            if (!_targets.TryGetValue(stim.Id, out var target))
            {
                target = new PerceptionTarget(stim.Id, stim)
                {
                    FirstSensedTime = CurrentTime
                };
                _targets[stim.Id] = target;
            }
            bool wasLost = target.State == PerceptionState.Lost || target.State == PerceptionState.Unknown;
            target.Stimulus = stim;
            target.SensedBy |= sense;
            target.LastKnownPosition = stim.Position;
            target.LastSensedTime = CurrentTime;
            target.Strength = MathF.Max(target.Strength, strength);
            target.Distance = dist;
            target.Direction = dir;
            target.EstimatedVelocity = stim.Velocity;
            if (wasLost)
            {
                target.State = PerceptionState.Detected;
                OnTargetPerceived?.Invoke(target);
            }
            else if (target.State == PerceptionState.Detected)
            {
                target.State = PerceptionState.Tracked;
            }
        }

        /// <summary>获取当前可见的目标。</summary>
        public List<PerceptionTarget> GetVisibleTargets()
        {
            var result = new List<PerceptionTarget>();
            foreach (var t in _targets.Values)
                if (t.IsCurrentlyVisible) result.Add(t);
            return result;
        }

        /// <summary>获取最近的敌人(按距离)。</summary>
        public PerceptionTarget? GetClosestHostile(int myFaction)
        {
            PerceptionTarget? best = null;
            float bestDist = float.MaxValue;
            foreach (var t in _targets.Values)
            {
                if (!t.IsCurrentlyVisible) continue;
                if (t.Stimulus.FactionId == myFaction) continue; // 友方跳过
                if (t.Distance < bestDist) { bestDist = t.Distance; best = t; }
            }
            return best;
        }

        /// <summary>获取最后已知位置。</summary>
        public Vector3? GetLastKnownPosition(int targetId)
            => _targets.TryGetValue(targetId, out var t) ? t.LastKnownPosition : null;

        /// <summary>清除所有目标。</summary>
        public void ClearTargets() => _targets.Clear();
    }

    // ========================================================================
    //  十二、感知系统主类
    // ========================================================================

    /// <summary>
    /// 感知系统。管理所有 agent 的感知组件 + 刺激源 + 空间查询。
    /// </summary>
    public sealed class PerceptionSystem
    {
        private readonly Dictionary<int, PerceptionComponent> _components = new();
        private readonly Dictionary<int, PerceptionStimulus> _stimuli = new();
        private readonly SpatialHashGrid _stimuliGrid;
        private readonly SpatialHashGrid _componentGrid;
        private float _time;
        private readonly List<PerceptionEvent> _globalNoiseEvents = new();

        /// <summary>全局视觉遮挡检测(共享，性能优化)。</summary>
        public Func<Vector3, Vector3, bool>? GlobalLineOfSight;
        /// <summary>更新间隔(0=每帧)。</summary>
        public float UpdateInterval = 0f;
        private float _timer;

        public int ComponentCount => _components.Count;
        public int StimulusCount => _stimuli.Count;

        public PerceptionSystem()
        {
            _stimuliGrid = new SpatialHashGrid(5f);
            _componentGrid = new SpatialHashGrid(5f);
        }

        // --------------------------------------------------------------------
        //  组件管理
        // --------------------------------------------------------------------

        public PerceptionComponent RegisterAgent(int agentId)
        {
            var comp = new PerceptionComponent(agentId);
            _components[agentId] = comp;
            return comp;
        }

        public void UnregisterAgent(int agentId) => _components.Remove(agentId);

        public PerceptionComponent? GetComponent(int agentId)
            => _components.TryGetValue(agentId, out var c) ? c : null;

        /// <summary>更新 agent 位置/朝向(由移动系统调用)。</summary>
        public void UpdateAgentTransform(int agentId, Vector3 pos, Vector3 facing)
        {
            if (_components.TryGetValue(agentId, out var c))
            {
                c.Position = pos;
                c.Facing = facing;
            }
        }

        // --------------------------------------------------------------------
        //  刺激源管理
        // --------------------------------------------------------------------

        public PerceptionStimulus RegisterStimulus(int id)
        {
            var s = new PerceptionStimulus(id);
            _stimuli[id] = s;
            return s;
        }

        public void UnregisterStimulus(int id) => _stimuli.Remove(id);

        public PerceptionStimulus? GetStimulus(int id)
            => _stimuli.TryGetValue(id, out var s) ? s : null;

        /// <summary>更新刺激源位置(由外部系统调用)。</summary>
        public void UpdateStimulusPosition(int id, Vector3 pos, Vector3 vel)
        {
            if (_stimuli.TryGetValue(id, out var s))
            {
                s.Position = pos;
                s.Velocity = vel;
            }
        }

        /// <summary>报告一个噪声事件(全局传播)。</summary>
        public void ReportNoise(Vector3 pos, float loudness, float radius, int sourceId, string tag = "")
        {
            _globalNoiseEvents.Add(new PerceptionEvent
            {
                Position = pos, Loudness = loudness, Radius = radius,
                SourceId = sourceId, Timestamp = _time, Tag = tag
            });
        }

        /// <summary>报告伤害事件。</summary>
        public void ReportDamage(int targetAgentId, int sourceId, float damage, Vector3 sourcePos)
        {
            if (_components.TryGetValue(targetAgentId, out var c))
            {
                var dmg = c.GetSense<DamageSense>();
                dmg?.ReportDamage(sourceId, damage, sourcePos, _time);
            }
        }

        // --------------------------------------------------------------------
        //  主更新
        // --------------------------------------------------------------------

        public void Update(float dt)
        {
            _time += dt;
            _timer += dt;
            if (UpdateInterval > 0 && _timer < UpdateInterval) return;
            float effectiveDt = _timer;
            _timer = 0;

            // 1. 更新空间索引
            _stimuliGrid.Clear();
            _componentGrid.Clear();
            foreach (var s in _stimuli.Values)
                if (s.IsActive) _stimuliGrid.Insert(s.Position, s.Id);
            foreach (var c in _components.Values)
                if (c.IsEnabled) _componentGrid.Insert(c.Position, c.AgentId);

            // 2. 推送噪声事件到听觉感官
            foreach (var ev in _globalNoiseEvents)
            {
                foreach (var id in _componentGrid.Query(ev.Position, ev.Radius))
                {
                    if (_components.TryGetValue(id, out var c))
                    {
                        var hearing = c.GetSense<HearingSense>();
                        hearing?.ReportNoise(ev.Position, ev.Loudness, ev.Radius, ev.SourceId, ev.Timestamp, ev.Tag);
                    }
                }
            }
            _globalNoiseEvents.Clear();

            // 3. 每个感知组件更新
            foreach (var c in _components.Values)
            {
                if (!c.IsEnabled) continue;
                c.CurrentTime = _time;
                // 只查询附近的刺激源
                var nearbyStimuli = new List<PerceptionStimulus>();
                foreach (var sid in _stimuliGrid.Query(c.Position, 30f)) // 30 = 最大感知范围
                    if (_stimuli.TryGetValue(sid, out var s) && s.IsActive)
                        nearbyStimuli.Add(s);
                c.Update(nearbyStimuli, effectiveDt);
            }
        }

        // --------------------------------------------------------------------
        //  查询
        // --------------------------------------------------------------------

        /// <summary>查询某 agent 能看到的所有目标。</summary>
        public List<int> GetVisibleTargets(int agentId)
        {
            var result = new List<int>();
            if (!_components.TryGetValue(agentId, out var c)) return result;
            foreach (var t in c.GetVisibleTargets())
                result.Add(t.TargetId);
            return result;
        }

        /// <summary>查询某 agent 的最近敌人。</summary>
        public int? GetClosestHostile(int agentId)
        {
            if (!_components.TryGetValue(agentId, out var c)) return null;
            var t = c.GetClosestHostile(c.FactionId);
            return t?.TargetId;
        }

        /// <summary>查询能看到某个目标的所有 agent(反向查询)。</summary>
        public List<int> GetAgentsSeeingTarget(int targetId)
        {
            var result = new List<int>();
            foreach (var c in _components.Values)
            {
                var t = c.GetTarget(targetId);
                if (t != null && t.IsCurrentlyVisible)
                    result.Add(c.AgentId);
            }
            return result;
        }
    }

    // ========================================================================
    //  十三、感知记忆系统
    // ========================================================================

    /// <summary>
    /// 长期感知记忆。比 PerceptionComponent 的短期记忆更长，用于决策。
    /// </summary>
    public sealed class PerceptionMemory
    {
        public struct MemoryEntry
        {
            public int TargetId;
            public Vector3 LastKnownPosition;
            public float LastSeenTime;
            public int SeenCount;
            public float TotalExposureTime;
            public string TargetTag;
            public int FactionId;
        }

        private readonly Dictionary<int, MemoryEntry> _entries = new();

        public void Record(PerceptionTarget target, float currentTime)
        {
            if (!_entries.TryGetValue(target.TargetId, out var e))
            {
                e = new MemoryEntry { TargetId = target.TargetId };
            }
            e.LastKnownPosition = target.LastKnownPosition;
            e.LastSeenTime = currentTime;
            e.SeenCount++;
            e.TargetTag = target.Stimulus.Tag;
            e.FactionId = target.Stimulus.FactionId;
            _entries[target.TargetId] = e;
        }

        public MemoryEntry? Get(int targetId)
            => _entries.TryGetValue(targetId, out var e) ? e : null;

        /// <summary>获取所有记忆中目标的最后已知位置。</summary>
        public Dictionary<int, Vector3> GetAllLastKnownPositions()
        {
            var result = new Dictionary<int, Vector3>();
            foreach (var (id, e) in _entries)
                result[id] = e.LastKnownPosition;
            return result;
        }

        /// <summary>清除过期记忆。</summary>
        public void PurgeOld(float currentTime, float maxAge)
        {
            var toRemove = new List<int>();
            foreach (var (id, e) in _entries)
                if (currentTime - e.LastSeenTime > maxAge) toRemove.Add(id);
            foreach (var id in toRemove) _entries.Remove(id);
        }
    }
}
