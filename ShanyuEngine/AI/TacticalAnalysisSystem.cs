// ============================================================================
//  ShanyuEngine.AI.TacticalAnalysisSystem.cs
//  山雨引擎 -- 战术分析系统
//  对齐 FEAR AI / Killzone AI / Full Spectrum Warrior
// ----------------------------------------------------------------------------
//  高层战术分析，不是底层路径规划，而是"战场局势判断"：
//    1. TacticalRole        - 战术角色(突击/狙击/支援/侦察)
//    2. ThreatAssessment    - 威胁评估
//    3. BattleLine          - 战线
//    4. TacticalObjective   - 战术目标(歼灭/防守/侧翼)
//    5. EngagementDecision  - 交战决策
//    6. TeamCoordination    - 小队协同
//    7. TacticalAnalyzer    - 战术分析器(综合评估)
//    8. TacticalReport      - 战术报告(给决策系统用)
// ----------------------------------------------------------------------------
//  注意：战术分析频率应该低(0.5~2 秒一次)，不要每帧跑。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Numerics;

namespace ShanyuEngine.AI
{
    // ========================================================================
    //  一、战术角色
    // ========================================================================

    /// <summary>战术角色。</summary>
    public enum TacticalRole
    {
        Unassigned,
        Assault,    // 突击手
        Sniper,     // 狙击手
        Support,    // 支援
        Scout,      // 侦察
        Medic,      // 医疗
        Engineer,   // 工程
        Defender,   // 防守
        Flanker,    // 侧翼
        Leader      // 队长
    }

    /// <summary>角色特性。</summary>
    public static class TacticalRoleTraits
    {
        public static float PreferredEngagementRange(TacticalRole role) => role switch
        {
            TacticalRole.Assault => 10f,
            TacticalRole.Sniper => 40f,
            TacticalRole.Support => 15f,
            TacticalRole.Scout => 20f,
            TacticalRole.Medic => 5f,
            TacticalRole.Engineer => 8f,
            TacticalRole.Defender => 12f,
            TacticalRole.Flanker => 8f,
            TacticalRole.Leader => 15f,
            _ => 10f
        };

        public static float PreferredCoverUsage(TacticalRole role) => role switch
        {
            TacticalRole.Assault => 0.3f,
            TacticalRole.Sniper => 0.9f,
            TacticalRole.Support => 0.6f,
            TacticalRole.Scout => 0.5f,
            TacticalRole.Defender => 0.8f,
            _ => 0.5f
        };

        public static bool PrefersFlanking(TacticalRole role)
            => role == TacticalRole.Flanker || role == TacticalRole.Scout;
    }

    // ========================================================================
    //  二、威胁评估
    // ========================================================================

    /// <summary>威胁类型。</summary>
    public enum ThreatType
    {
        DirectFire,     // 直接火力
        IndirectFire,   // 间接火力(手雷/迫击炮)
        Melee,          // 近战
        Vehicle,        // 载具
        Sniper,         // 狙击手
        Group           // 群体
    }

    /// <summary>威胁等级。</summary>
    public enum ThreatLevel
    {
        None,
        Low,
        Medium,
        High,
        Critical
    }

    /// <summary>威胁评估结果。</summary>
    public struct ThreatAssessment
    {
        public int TargetId;
        public Vector3 TargetPosition;
        public ThreatType Type;
        public ThreatLevel Level;
        public float ThreatScore;       // 0~1
        public float Distance;
        public bool HasLineOfSight;
        public bool IsFlanking;          // 是否在侧翼
        public bool IsInCover;
        public float Health;             // 0~1
        public float DamagePotential;    // 该目标能造成的伤害潜力
        public int EstimatedAllies;      // 估计附近的盟友数
        public float LastUpdated;
    }

    // ========================================================================
    //  三、战线
    // ========================================================================

    /// <summary>
    /// 战线。表示敌我双方的接触线。
    /// </summary>
    public struct BattleLine
    {
        public Vector3 AllyCenter;
        public Vector3 EnemyCenter;
        public Vector3 Direction;    // 从盟友指向敌人
        public float Width;          // 战线宽度
        public List<Vector3> AllyPositions;
        public List<Vector3> EnemyPositions;
        public float Tension;        // 战线紧张度(0~1)
    }

    // ========================================================================
    //  四、战术目标
    // ========================================================================

    /// <summary>战术目标类型。</summary>
    public enum TacticalObjectiveType
    {
        EliminateTarget,   // 歼灭目标
        DefendPosition,    // 防守位置
        Advance,           // 推进
        FlankLeft,         // 左翼迂回
        FlankRight,        // 右翼迂回
        Retreat,           // 撤退
        Regroup,           // 集结
        Suppress,          // 压制
        HoldFire,          // 停火
        Search              // 搜索
    }

    /// <summary>战术目标。</summary>
    public struct TacticalObjective
    {
        public TacticalObjectiveType Type;
        public Vector3 TargetPosition;
        public int? TargetId;
        public float Priority;
        public float Deadline;      // 完成期限
        public string Description;
        public bool IsCompleted;
        public bool IsAborted;
    }

    // ========================================================================
    //  五、交战决策
    // ========================================================================

    /// <summary>交战策略。</summary>
    public enum EngagementStrategy
    {
        Aggressive,    // 激进(主动进攻)
        Defensive,     // 防御(占掩体等)
        Cautious,      // 谨慎(交替掩护)
        Ambush,        // 伏击
        Suppressive,   // 压制
        Disengage,     // 脱战
        HoldPosition   // 原地坚守
    }

    /// <summary>交战决策结果。</summary>
    public struct EngagementDecision
    {
        public EngagementStrategy Strategy;
        public int? PrimaryTarget;
        public Vector3? MoveTarget;
        public Vector3? CoverTarget;
        public bool ShouldFlank;
        public FlankingSide FlankSide;
        public bool ShouldSuppress;
        public bool ShouldRetreat;
        public float Urgency;       // 0~1, 紧急程度
        public string Reason;
    }

    public enum FlankingSide { None, Left, Right }

    // ========================================================================
    //  六、小队协同
    // ========================================================================

    /// <summary>小队成员信息。</summary>
    public struct SquadMember
    {
        public int AgentId;
        public TacticalRole Role;
        public Vector3 Position;
        public Vector3 Velocity;
        public float Health;
        public int FactionId;
        public bool IsAlive;
        public int? CurrentTarget;
        public TacticalObjective? CurrentObjective;
    }

    /// <summary>小队状态。</summary>
    public struct SquadStatus
    {
        public List<SquadMember> Members;
        public Vector3 Center;
        public int AliveCount;
        public float AverageHealth;
        public float Cohesion;     // 凝聚度 0~1
        public float Morale;       // 士气 0~1
        public bool IsInCombat;
    }

    // ========================================================================
    //  七、战术分析器
    // ========================================================================

    /// <summary>
    /// 战术分析器。综合分析战场局势，给出战术建议。
    /// </summary>
    public sealed class TacticalAnalyzer
    {
        private readonly ISpatialQuery _spatial;
        private readonly CoverSystem _coverSystem;
        private readonly ThreatField? _threatField;
        private float _time;
        private float _analysisInterval = 0.5f;
        private float _timer;

        /// <summary>分析频率(秒)。</summary>
        public float AnalysisInterval
        {
            get => _analysisInterval;
            set => _analysisInterval = value;
        }

        public TacticalAnalyzer(ISpatialQuery spatial, CoverSystem cover, ThreatField? threat = null)
        {
            _spatial = spatial;
            _coverSystem = cover;
            _threatField = threat;
        }

        /// <summary>评估威胁。</summary>
        public ThreatAssessment AssessThreat(SquadMember self, SquadMember enemy, float currentTime)
        {
            var assessment = new ThreatAssessment
            {
                TargetId = enemy.AgentId,
                TargetPosition = enemy.Position,
                Distance = Vector3.Distance(self.Position, enemy.Position),
                HasLineOfSight = !_spatial.IsBlocked(self.Position, enemy.Position),
                Health = enemy.Health,
                LastUpdated = currentTime
            };

            // 威胁类型
            if (enemy.Role == TacticalRole.Sniper) assessment.Type = ThreatType.Sniper;
            else if (assessment.Distance < 3f) assessment.Type = ThreatType.Melee;
            else assessment.Type = ThreatType.DirectFire;

            // 威胁分数
            float score = 0;
            // 距离越近威胁越大
            score += Math.Clamp(1f - assessment.Distance / 30f, 0, 1) * 0.3f;
            // 血量高的威胁大(还能打很久)
            score += enemy.Health * 0.2f;
            // 有视线威胁大
            score += assessment.HasLineOfSight ? 0.3f : 0f;
            // 侧翼威胁大
            assessment.IsFlanking = IsFlanking(self, enemy);
            score += assessment.IsFlanking ? 0.2f : 0f;
            // 在掩体里威胁小
            assessment.IsInCover = _coverSystem.QueryNearby(enemy.Position, 1f).GetEnumerator().MoveNext();
            score += assessment.IsInCover ? -0.1f : 0.1f;

            assessment.ThreatScore = Math.Clamp(score, 0, 1);
            assessment.Level = assessment.ThreatScore switch
            {
                > 0.8f => ThreatLevel.Critical,
                > 0.6f => ThreatLevel.High,
                > 0.4f => ThreatLevel.Medium,
                > 0.2f => ThreatLevel.Low,
                _ => ThreatLevel.None
            };
            // 伤害潜力(简化)
            assessment.DamagePotential = enemy.Health * 10f * (assessment.HasLineOfSight ? 1f : 0.3f);
            return assessment;
        }

        /// <summary>判断是否被侧翼包抄。</summary>
        private bool IsFlanking(SquadMember self, SquadMember enemy)
        {
            if (self.CurrentTarget == null) return false;
            // 简化：如果敌人在自己的侧面(跟朝向目标方向夹角大)
            return false; // TODO: 需要更多上下文
        }

        /// <summary>分析小队状态。</summary>
        public SquadStatus AnalyzeSquad(List<SquadMember> members, float currentTime)
        {
            var status = new SquadStatus
            {
                Members = members,
                Center = Vector3.Zero,
                AliveCount = 0,
                AverageHealth = 0,
                Cohesion = 0,
                Morale = 1f,
                IsInCombat = false
            };
            if (members.Count == 0) return status;
            Vector3 center = Vector3.Zero;
            float totalHealth = 0;
            int alive = 0;
            foreach (var m in members)
            {
                if (!m.IsAlive) continue;
                center += m.Position;
                totalHealth += m.Health;
                alive++;
                if (m.CurrentTarget != null) status.IsInCombat = true;
            }
            if (alive > 0)
            {
                status.Center = center / alive;
                status.AverageHealth = totalHealth / alive;
            }
            status.AliveCount = alive;
            // 凝聚度：基于成员间的平均距离
            if (alive > 1)
            {
                float totalDist = 0;
                int pairs = 0;
                for (int i = 0; i < members.Count; i++)
                {
                    if (!members[i].IsAlive) continue;
                    for (int j = i + 1; j < members.Count; j++)
                    {
                        if (!members[j].IsAlive) continue;
                        totalDist += Vector3.Distance(members[i].Position, members[j].Position);
                        pairs++;
                    }
                }
                if (pairs > 0)
                {
                    float avgDist = totalDist / pairs;
                    status.Cohesion = Math.Clamp(1f - avgDist / 20f, 0, 1);
                }
            }
            // 士气：基于血量和凝聚力
            status.Morale = status.AverageHealth * 0.5f + status.Cohesion * 0.3f + (status.IsInCombat ? 0.2f : 0f);
            status.Morale = Math.Clamp(status.Morale, 0, 1);
            return status;
        }

        /// <summary>生成战术报告。</summary>
        public TacticalReport GenerateReport(SquadStatus ownStatus, List<ThreatAssessment> threats, float currentTime)
        {
            var report = new TacticalReport
            {
                GeneratedTime = currentTime,
                OwnStatus = ownStatus,
                Threats = threats,
                EnemyCount = threats.Count
            };
            // 找最大威胁
            if (threats.Count > 0)
            {
                float maxThreat = 0;
                foreach (var t in threats)
                {
                    if (t.ThreatScore > maxThreat)
                    {
                        maxThreat = t.ThreatScore;
                        report.BiggestThreat = t;
                    }
                }
                report.AverageThreat = 0;
                foreach (var t in threats) report.AverageThreat += t.ThreatScore;
                report.AverageThreat /= threats.Count;
            }
            // 局势评估
            report.ShouldEngage = ownStatus.AverageHealth > 0.4f && report.AverageThreat < 0.7f;
            report.ShouldRetreat = ownStatus.AverageHealth < 0.3f || report.AverageThreat > 0.8f;
            report.ShouldFlank = ownStatus.Cohesion > 0.6f && threats.Count > 0 && threats.Count < 4;
            report.ShouldDefend = ownStatus.AverageHealth < 0.5f && threats.Count > 2;
            return report;
        }

        /// <summary>做交战决策。</summary>
        public EngagementDecision MakeEngagementDecision(SquadMember self, TacticalReport report, List<SquadMember> allies)
        {
            var decision = new EngagementDecision();
            if (report.BiggestThreat.TargetId != 0)
            {
                var threat = report.BiggestThreat;
                decision.PrimaryTarget = threat.TargetId;

                // 决策逻辑：基于角色和威胁
                if (report.ShouldRetreat || self.Health < 0.2f)
                {
                    decision.Strategy = EngagementStrategy.Disengage;
                    decision.ShouldRetreat = true;
                    decision.Urgency = 1f;
                    decision.Reason = "血量过低，撤退";
                }
                else if (report.ShouldDefend && TacticalRoleTraits.PreferredCoverUsage(self.Role) > 0.5f)
                {
                    decision.Strategy = EngagementStrategy.Defensive;
                    var cover = _coverSystem.FindBestCover(self.Position, threat.TargetPosition);
                    if (cover.HasValue)
                    {
                        decision.CoverTarget = cover.Value.GetStandPosition();
                        decision.MoveTarget = decision.CoverTarget;
                    }
                    decision.Reason = "防守，占掩体";
                }
                else if (report.ShouldFlank && TacticalRoleTraits.PrefersFlanking(self.Role))
                {
                    decision.Strategy = EngagementStrategy.Aggressive;
                    decision.ShouldFlank = true;
                    decision.FlankSide = PickFlankSide(self, threat.TargetPosition, allies);
                    decision.Reason = "侧翼迂回";
                }
                else if (threat.Distance > TacticalRoleTraits.PreferredEngagementRange(self.Role) * 1.5f)
                {
                    decision.Strategy = EngagementStrategy.Aggressive;
                    decision.MoveTarget = Vector3.Lerp(self.Position, threat.TargetPosition, 0.5f);
                    decision.Reason = "距离太远，接近";
                }
                else if (threat.Distance < TacticalRoleTraits.PreferredEngagementRange(self.Role) * 0.5f)
                {
                    decision.Strategy = EngagementStrategy.Suppressive;
                    decision.ShouldSuppress = true;
                    decision.Reason = "距离太近，压制";
                }
                else
                {
                    decision.Strategy = EngagementStrategy.Aggressive;
                    decision.Reason = "正常交战";
                }
                decision.Urgency = threat.ThreatScore;
            }
            else
            {
                decision.Strategy = EngagementStrategy.HoldPosition;
                decision.Reason = "没有威胁";
            }
            return decision;
        }

        private FlankingSide PickFlankSide(SquadMember self, Vector3 enemyPos, List<SquadMember> allies)
        {
            // 看哪侧盟友少(避免重复)
            var toEnemy = enemyPos - self.Position;
            toEnemy.Y = 0;
            var forward = toEnemy.LengthSquared() > 0.01f ? Vector3.Normalize(toEnemy) : Vector3.UnitZ;
            var right = new Vector3(-forward.Z, 0, forward.X);
            int rightCount = 0, leftCount = 0;
            foreach (var a in allies)
            {
                if (a.AgentId == self.AgentId) continue;
                var toAlly = a.Position - self.Position;
                float dot = Vector3.Dot(toAlly, right);
                if (dot > 1f) rightCount++;
                else if (dot < -1f) leftCount++;
            }
            return rightCount <= leftCount ? FlankingSide.Right : FlankingSide.Left;
        }

        /// <summary>更新(每 AnalysisInterval 秒分析一次)。</summary>
        public void Update(float dt, Action<TacticalReport>? onReport = null)
        {
            _time += dt;
            _timer += dt;
            if (_timer < _analysisInterval) return;
            _timer = 0;
            // 实际分析由外部调用对应方法
        }
    }

    // ========================================================================
    //  八、战术报告
    // ========================================================================

    /// <summary>战术报告。</summary>
    public struct TacticalReport
    {
        public float GeneratedTime;
        public SquadStatus OwnStatus;
        public List<ThreatAssessment> Threats;
        public int EnemyCount;
        public ThreatAssessment BiggestThreat;
        public float AverageThreat;
        public bool ShouldEngage;
        public bool ShouldRetreat;
        public bool ShouldFlank;
        public bool ShouldDefend;
        public BattleLine? BattleLine;
        public List<TacticalObjective> SuggestedObjectives;

        public override string ToString()
            => $"[TacticalReport enemies={EnemyCount} avgThreat={AverageThreat:F2} engage={ShouldEngage} retreat={ShouldRetreat}]";
    }

    // ========================================================================
    //  九、目标分配器
    // ========================================================================

    /// <summary>
    /// 目标分配器。给小队成员分配敌人目标，避免重复攻击。
    /// </summary>
    public sealed class TargetAssigner
    {
        /// <summary>分配目标(贪心：每个盟友找最近的未分配敌人)。</summary>
        public Dictionary<int, int> AssignTargets(List<SquadMember> allies, List<int> enemies, Dictionary<int, Vector3> positions)
        {
            var result = new Dictionary<int, int>();
            var available = new List<int>(enemies);
            foreach (var ally in allies)
            {
                if (!ally.IsAlive) continue;
                if (available.Count == 0) break;
                int best = -1;
                float bestDist = float.MaxValue;
                foreach (var e in available)
                {
                    if (!positions.TryGetValue(e, out var ep)) continue;
                    float d = Vector3.DistanceSquared(ally.Position, ep);
                    if (d < bestDist) { bestDist = d; best = e; }
                }
                if (best >= 0)
                {
                    result[ally.AgentId] = best;
                    available.Remove(best);
                }
            }
            return result;
        }

        /// <summary>分配目标(按角色优先级)。</summary>
        public Dictionary<int, int> AssignByRole(List<SquadMember> allies, List<ThreatAssessment> threats)
        {
            var result = new Dictionary<int, int>();
            var available = new List<ThreatAssessment>(threats);
            // 按角色优先级排序(狙击手先选)
            var ordered = new List<SquadMember>(allies);
            ordered.Sort((a, b) => b.Role == TacticalRole.Sniper ? 1 : -1);
            foreach (var ally in ordered)
            {
                if (!ally.IsAlive) continue;
                if (available.Count == 0) break;
                // 找最匹配的目标
                int bestIdx = -1;
                float bestScore = 0;
                for (int i = 0; i < available.Count; i++)
                {
                    var t = available[i];
                    float range = TacticalRoleTraits.PreferredEngagementRange(ally.Role);
                    float score = t.ThreatScore * 0.5f + (1f - MathF.Abs(t.Distance - range) / range) * 0.5f;
                    if (score > bestScore) { bestScore = score; bestIdx = i; }
                }
                if (bestIdx >= 0)
                {
                    result[ally.AgentId] = available[bestIdx].TargetId;
                    available.RemoveAt(bestIdx);
                }
            }
            return result;
        }
    }

    // ========================================================================
    //  十、战术指挥官
    // ========================================================================

    /// <summary>
    /// 战术指挥官。综合管理小队的战术决策。
    /// </summary>
    public sealed class TacticalCommander
    {
        private readonly TacticalAnalyzer _analyzer;
        private readonly TargetAssigner _assigner = new();
        private readonly Dictionary<int, TacticalRole> _roleAssignments = new();
        private TacticalReport? _lastReport;
        private Dictionary<int, int> _targetAssignments = new();

        public TacticalReport? LastReport => _lastReport;
        public Dictionary<int, int> TargetAssignments => _targetAssignments;

        public TacticalCommander(TacticalAnalyzer analyzer) { _analyzer = analyzer; }

        /// <summary>分配角色(基于小队组成)。</summary>
        public void AssignRoles(List<SquadMember> members)
        {
            _roleAssignments.Clear();
            if (members.Count == 0) return;
            // 队长
            _roleAssignments[members[0].AgentId] = TacticalRole.Leader;
            // 根据人数分配
            if (members.Count > 1)
                _roleAssignments[members[1].AgentId] = TacticalRole.Assault;
            if (members.Count > 2)
                _roleAssignments[members[2].AgentId] = TacticalRole.Support;
            if (members.Count > 3)
                _roleAssignments[members[3].AgentId] = TacticalRole.Flanker;
            if (members.Count > 4)
                _roleAssignments[members[4].AgentId] = TacticalRole.Sniper;
            // 应用到成员
            for (int i = 0; i < members.Count; i++)
            {
                if (_roleAssignments.TryGetValue(members[i].AgentId, out var role))
                {
                    var m = members[i];
                    m.Role = role;
                    members[i] = m;
                }
            }
        }

        /// <summary>执行一次完整分析。</summary>
        public TacticalReport RunAnalysis(List<SquadMember> ownMembers, List<SquadMember> enemies, float time)
        {
            // 分析小队
            var squadStatus = _analyzer.AnalyzeSquad(ownMembers, time);
            // 评估所有威胁
            var threats = new List<ThreatAssessment>();
            foreach (var enemy in enemies)
            {
                if (!enemy.IsAlive) continue;
                if (ownMembers.Count == 0) break;
                var threat = _analyzer.AssessThreat(ownMembers[0], enemy, time);
                threats.Add(threat);
            }
            // 生成报告
            _lastReport = _analyzer.GenerateReport(squadStatus, threats, time);
            // 分配目标
            var enemyIds = new List<int>();
            var enemyPositions = new Dictionary<int, Vector3>();
            foreach (var t in threats)
            {
                enemyIds.Add(t.TargetId);
                enemyPositions[t.TargetId] = t.TargetPosition;
            }
            _targetAssignments = _assigner.AssignTargets(ownMembers, enemyIds, enemyPositions);
            return _lastReport.Value;
        }

        /// <summary>获取某 agent 应该攻击的目标。</summary>
        public int? GetAssignedTarget(int agentId)
            => _targetAssignments.TryGetValue(agentId, out var t) ? t : null;

        /// <summary>生成战术命令(给行为树/GOAP 用)。</summary>
        public List<TacticalObjective> GenerateObjectives(TacticalReport report)
        {
            var objectives = new List<TacticalObjective>();
            if (report.ShouldRetreat)
            {
                objectives.Add(new TacticalObjective
                {
                    Type = TacticalObjectiveType.Retreat,
                    Priority = 1f,
                    Deadline = 5f,
                    Description = "撤退到安全区域"
                });
            }
            else if (report.ShouldDefend)
            {
                objectives.Add(new TacticalObjective
                {
                    Type = TacticalObjectiveType.DefendPosition,
                    TargetPosition = report.OwnStatus.Center,
                    Priority = 0.8f,
                    Description = "坚守阵地"
                });
            }
            else if (report.ShouldFlank)
            {
                objectives.Add(new TacticalObjective
                {
                    Type = TacticalObjectiveType.FlankRight,
                    TargetPosition = report.BiggestThreat.TargetPosition,
                    Priority = 0.7f,
                    Description = "侧翼迂回"
                });
            }
            else if (report.ShouldEngage && report.BiggestThreat.TargetId != 0)
            {
                objectives.Add(new TacticalObjective
                {
                    Type = TacticalObjectiveType.EliminateTarget,
                    TargetId = report.BiggestThreat.TargetId,
                    TargetPosition = report.BiggestThreat.TargetPosition,
                    Priority = 0.9f,
                    Description = "消灭主要威胁"
                });
            }
            return objectives;
        }
    }
}
