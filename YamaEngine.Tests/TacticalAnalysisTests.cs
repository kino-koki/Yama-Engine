// TacticalAnalysisTests.cs — UE5.8 战术分析子系统深度验证
// 覆盖：TacticalRoleTraits（角色特性：交战距离/掩体倾向/侧翼偏好）、
// TacticalAnalyzer.AnalyzeSquad（质心/血量/凝聚度/士气/交战态）、
// GenerateReport（交战/撤退/侧翼/防守局势判定）、AssignRoles（角色分配顺序）。

using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.AI;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class TacticalAnalysisTests
    {
        private static void AssertNear(float got, float want, float tol, string msg)
            => AssertTrue(MathF.Abs(got - want) <= tol,
                $"{msg} (got {got:F4}, want {want:F4}±{tol:F4})");

        private sealed class EmptySpatial : ISpatialQuery
        {
            public bool IsBlocked(Vector3 from, Vector3 to) => false;
            public bool IsWalkable(Vector3 pos) => true;
            public List<Vector3> QueryWalkablePoints(Vector3 center, float radius) => new();
            public float GetGroundHeight(Vector3 pos) => 0f;
            public Vector3 FindNearestWalkable(Vector3 pos, float maxDist = 10f) => pos;
        }

        private static TacticalAnalyzer MakeAnalyzer()
            => new TacticalAnalyzer(new EmptySpatial(), new CoverSystem(new EmptySpatial()));

        // ── 角色特性：狙击手远交战距离+高掩体，医疗兵近距，侧翼/侦察偏好迂回 ──
        public static void TestRoleTraits()
        {
            AssertNear(TacticalRoleTraits.PreferredEngagementRange(TacticalRole.Sniper), 40f, 0.01f,
                "Sniper engagement range must be 40.");
            AssertNear(TacticalRoleTraits.PreferredEngagementRange(TacticalRole.Medic), 5f, 0.01f,
                "Medic engagement range must be 5.");
            AssertNear(TacticalRoleTraits.PreferredCoverUsage(TacticalRole.Sniper), 0.9f, 0.01f,
                "Sniper cover usage must be 0.9.");
            AssertNear(TacticalRoleTraits.PreferredCoverUsage(TacticalRole.Assault), 0.3f, 0.01f,
                "Assault cover usage must be 0.3.");
            AssertTrue(TacticalRoleTraits.PrefersFlanking(TacticalRole.Flanker),
                "Flanker must prefer flanking.");
            AssertTrue(TacticalRoleTraits.PrefersFlanking(TacticalRole.Scout),
                "Scout must prefer flanking.");
            AssertTrue(!TacticalRoleTraits.PrefersFlanking(TacticalRole.Defender),
                "Defender must not prefer flanking.");
        }

        // ── 小队状态：质心、平均血量、存活数、凝聚度（距离越近凝聚越高） ──
        public static void TestSquadCenterHealthCohesion()
        {
            var analyzer = MakeAnalyzer();
            var members = new List<SquadMember>
            {
                new SquadMember { AgentId = 0, Position = new Vector3(0,0,0), Health = 1f, IsAlive = true },
                new SquadMember { AgentId = 1, Position = new Vector3(4,0,0), Health = 0.5f, IsAlive = true },
                new SquadMember { AgentId = 2, Position = new Vector3(2,0,0), Health = 0.8f, IsAlive = true },
            };
            var status = analyzer.AnalyzeSquad(members, 0f);
            AssertEqual(3, status.AliveCount, "AliveCount must be 3.");
            AssertNear(status.Center.X, 2f, 0.01f, "Squad center X must be 2.");
            AssertNear(status.AverageHealth, (1f + 0.5f + 0.8f) / 3f, 0.01f,
                "AverageHealth must be mean of healths.");
            // 凝聚度：成员间距最大 4，平均距离 = (4+2+2)/3 = 2.667；Cohesion=1-avgDist/20
            AssertTrue(status.Cohesion > 0.8f,
                $"Tight squad cohesion must be high, got {status.Cohesion:F3}.");
        }

        // ── 散开的小队凝聚度低于紧凑小队；阵亡不计入 ──
        public static void TestSquadCohesionDropsWhenSpread()
        {
            var analyzer = MakeAnalyzer();
            var tight = new List<SquadMember>
            {
                new SquadMember { AgentId = 0, Position = new Vector3(0,0,0), Health = 1f, IsAlive = true },
                new SquadMember { AgentId = 1, Position = new Vector3(1,0,0), Health = 1f, IsAlive = true },
            };
            var spread = new List<SquadMember>
            {
                new SquadMember { AgentId = 0, Position = new Vector3(0,0,0), Health = 1f, IsAlive = true },
                new SquadMember { AgentId = 1, Position = new Vector3(30,0,0), Health = 1f, IsAlive = true },
            };
            float tightCoh = analyzer.AnalyzeSquad(tight, 0f).Cohesion;
            float spreadCoh = analyzer.AnalyzeSquad(spread, 0f).Cohesion;
            AssertTrue(tightCoh > spreadCoh,
                $"Tight cohesion ({tightCoh:F3}) must exceed spread ({spreadCoh:F3}).");

            // 阵亡成员不计入质心/血量
            var dead = new List<SquadMember>
            {
                new SquadMember { AgentId = 0, Position = new Vector3(0,0,0), Health = 0f, IsAlive = false },
                new SquadMember { AgentId = 1, Position = new Vector3(4,0,0), Health = 1f, IsAlive = true },
            };
            var s2 = analyzer.AnalyzeSquad(dead, 0f);
            AssertEqual(1, s2.AliveCount, "Dead member must not count in AliveCount.");
            AssertNear(s2.AverageHealth, 1f, 0.01f, "AverageHealth must ignore dead members.");
        }

        // ── GenerateReport 局势判定：低血/高威胁撤退，健康/低威胁交战，凝聚力高+少量敌侧翼 ──
        public static void TestReportDecisions()
        {
            var analyzer = MakeAnalyzer();

            // 健康小队 + 低威胁 → 交战
            var healthy = new SquadStatus
            {
                Members = new List<SquadMember>(),
                Center = new Vector3(0,0,0),
                AliveCount = 4,
                AverageHealth = 0.9f,
                Cohesion = 0.9f,
                Morale = 1f,
                IsInCombat = false
            };
            var lightThreats = new List<ThreatAssessment>
            {
                new ThreatAssessment { TargetId = 10, ThreatScore = 0.3f },
            };
            var r1 = analyzer.GenerateReport(healthy, lightThreats, 0f);
            AssertTrue(r1.ShouldEngage, "Healthy+low-threat must engage.");
            AssertTrue(!r1.ShouldRetreat, "Healthy+low-threat must not retreat.");
            AssertNear(r1.AverageThreat, 0.3f, 0.01f, "AverageThreat must be mean of threat scores.");

            // 低血量 → 撤退
            var weak = healthy;
            weak.AverageHealth = 0.2f;
            var r2 = analyzer.GenerateReport(weak, lightThreats, 0f);
            AssertTrue(r2.ShouldRetreat, "Low-health must retreat.");

            // 高威胁 → 撤退
            var highThreats = new List<ThreatAssessment>
            {
                new ThreatAssessment { TargetId = 10, ThreatScore = 0.9f },
                new ThreatAssessment { TargetId = 11, ThreatScore = 0.95f },
            };
            var r3 = analyzer.GenerateReport(healthy, highThreats, 0f);
            AssertTrue(r3.ShouldRetreat, "High-average-threat (>0.8) must retreat.");

            // 凝聚度高 + 1~3 个敌人 → 侧翼
            var cohesive = healthy;
            cohesive.AverageHealth = 0.9f;
            cohesive.Cohesion = 0.8f;
            var fewThreats = new List<ThreatAssessment>
            {
                new ThreatAssessment { TargetId = 10, ThreatScore = 0.4f },
                new ThreatAssessment { TargetId = 11, ThreatScore = 0.4f },
            };
            var r4 = analyzer.GenerateReport(cohesive, fewThreats, 0f);
            AssertTrue(r4.ShouldFlank,
                "Cohesive squad with few enemies must flank.");
        }

        // ── AssignRoles：首个成员为 Leader，其后依次 Assault/Support/Flanker/Sniper ──
        public static void TestAssignRolesOrder()
        {
            var commander = new TacticalCommander(MakeAnalyzer());
            var members = new List<SquadMember>();
            for (int i = 0; i < 5; i++)
                members.Add(new SquadMember { AgentId = i, Position = new Vector3(i,0,0), Health = 1f, IsAlive = true });

            commander.AssignRoles(members);
            AssertTrue(members[0].Role == TacticalRole.Leader, "First member must be Leader.");
            AssertTrue(members[1].Role == TacticalRole.Assault, "Second member must be Assault.");
            AssertTrue(members[2].Role == TacticalRole.Support, "Third member must be Support.");
            AssertTrue(members[3].Role == TacticalRole.Flanker, "Fourth member must be Flanker.");
            AssertTrue(members[4].Role == TacticalRole.Sniper, "Fifth member must be Sniper.");
        }
    }
}
