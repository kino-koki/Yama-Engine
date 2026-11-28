using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.AI;
using YamaEngine.Physics;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    /// <summary>
    /// Mass Crowd 群体行为系统深度测试。对齐 UE5 Mass Entity + Mass Crowd。
    /// 覆盖：SOA Chunk / EntityManager / 行为处理器 / RVO 避障 / 编队 / 生成器。
    /// </summary>
    public static class MassCrowdDepthTests
    {
        private static void Near(float want, float got, float tol, string msg)
        {
            if (Math.Abs(want - got) > tol) throw new Exception($"{msg}: want {want}, got {got}");
        }
        private static void AssertFalse(bool cond, string msg) { if (cond) throw new Exception(msg); }
        private static void NearV3(Vector3 want, Vector3 got, float tol, string msg)
        {
            if (Vector3.Distance(want, got) > tol) throw new Exception($"{msg}: want {want}, got {got}");
        }

        // ── 枚举 ──

        public static void TestAgentStateValues()
        {
            AssertEqual(0, (int)MassAgentState.None, "None=0");
            AssertEqual(1, (int)MassAgentState.Active, "Active=1");
            AssertEqual(2, (int)MassAgentState.Inactive, "Inactive=2");
            AssertEqual(3, (int)MassAgentState.Dying, "Dying=3");
        }

        public static void TestBehaviorTypeValues()
        {
            AssertEqual(0, (int)MassBehaviorType.Idle, "Idle=0");
            AssertEqual(1, (int)MassBehaviorType.Patrol, "Patrol=1");
            AssertEqual(2, (int)MassBehaviorType.Wander, "Wander=2");
            AssertEqual(3, (int)MassBehaviorType.Flee, "Flee=3");
            AssertEqual(4, (int)MassBehaviorType.Seek, "Seek=4");
            AssertEqual(5, (int)MassBehaviorType.Queue, "Queue=5");
            AssertEqual(6, (int)MassBehaviorType.Formation, "Formation=6");
        }

        public static void TestLodValues()
        {
            AssertEqual(0, (int)MassAgentLod.Near, "Near=0");
            AssertEqual(1, (int)MassAgentLod.Mid, "Mid=1");
            AssertEqual(2, (int)MassAgentLod.Far, "Far=2");
        }

        public static void TestFormationTypeValues()
        {
            AssertEqual(0, (int)MassFormationType.None, "None=0");
            AssertEqual(1, (int)MassFormationType.Circle, "Circle=1");
            AssertEqual(2, (int)MassFormationType.Line, "Line=2");
            AssertEqual(3, (int)MassFormationType.Wedge, "Wedge=3");
            AssertEqual(4, (int)MassFormationType.Column, "Column=4");
            AssertEqual(5, (int)MassFormationType.Grid, "Grid=5");
        }

        // ── MassAgentChunk ──

        public static void TestChunkCapacity()
        {
            AssertEqual(256, MassAgentChunk.ChunkCapacity, "capacity=256");
        }

        public static void TestChunkAddRemove()
        {
            var c = new MassAgentChunk { ChunkIndex = 0 };
            AssertEqual(0, c.ActiveCount, "initially empty");
            int slot = c.Add();
            AssertTrue(slot >= 0, "got slot");
            AssertEqual(1, c.ActiveCount, "1 active");
            AssertTrue(c.Get(slot).IsActive, "slot is active");
            c.Remove(slot);
            AssertEqual(0, c.ActiveCount, "removed");
            AssertFalse(c.Get(slot).IsActive, "slot inactive after remove");
        }

        public static void TestChunkFill()
        {
            var c = new MassAgentChunk { ChunkIndex = 0 };
            for (int i = 0; i < MassAgentChunk.ChunkCapacity; i++)
                AssertTrue(c.Add() >= 0, $"add slot {i}");
            AssertEqual(-1, c.Add(), "full -> -1");
            AssertEqual(MassAgentChunk.ChunkCapacity, c.ActiveCount, "full count");
        }

        public static void TestChunkAgentView()
        {
            var c = new MassAgentChunk { ChunkIndex = 0 };
            int slot = c.Add();
            var a = c.Get(slot);
            a.Position = new Vector3(1, 2, 3);
            a.Radius = 0.5f;
            a.MaxSpeed = 3f;
            a.State = MassAgentState.Active;
            a.Behavior = MassBehaviorType.Seek;
            NearV3(new Vector3(1, 2, 3), a.Position, 1e-5f, "position RW");
            Near(0.5f, a.Radius, 1e-5f, "radius RW");
            Near(3f, a.MaxSpeed, 1e-5f, "maxSpeed RW");
            AssertEqual(MassAgentState.Active, a.State, "state RW");
            AssertEqual(MassBehaviorType.Seek, a.Behavior, "behavior RW");
        }

        // ── MassEntityManager ──

        public static void TestManagerAllocateDefaults()
        {
            var m = new MassEntityManager();
            int id = m.Allocate(new Vector3(5, 0, 5));
            AssertTrue(id > 0, "id > 0");
            AssertEqual(1, m.ActiveCount, "1 active");
            AssertEqual(1, m.ChunkCount, "1 chunk auto-created");
            var a = m.GetAgent(id);
            AssertTrue(a.IsValid, "valid agent");
            NearV3(new Vector3(5, 0, 5), a.Position, 1e-5f, "pos");
            Near(0.4f, a.Radius, 1e-5f, "default radius");
            Near(1.4f, a.MaxSpeed, 1e-5f, "default maxSpeed");
            AssertEqual(MassAgentState.Active, a.State, "default state");
            AssertEqual(MassBehaviorType.Idle, a.Behavior, "default behavior");
            AssertEqual(MassAgentLod.Near, a.Lod, "default lod");
        }

        public static void TestManagerAllocateCustom()
        {
            var m = new MassEntityManager();
            int id = m.Allocate(Vector3.Zero, 0.8f, 2.5f);
            var a = m.GetAgent(id);
            Near(0.8f, a.Radius, 1e-5f, "custom radius");
            Near(2.5f, a.MaxSpeed, 1e-5f, "custom maxSpeed");
        }

        public static void TestManagerBatchAllocate()
        {
            var m = new MassEntityManager();
            var ids = m.Allocate(10, Vector3.Zero, 2f);
            AssertEqual(10, ids.Count, "10 ids");
            AssertEqual(10, m.ActiveCount, "10 active");
        }

        public static void TestManagerFree()
        {
            var m = new MassEntityManager();
            int id = m.Allocate(Vector3.Zero);
            AssertTrue(m.HasAgent(id), "exists");
            m.Free(id);
            AssertFalse(m.HasAgent(id), "freed");
            AssertEqual(0, m.ActiveCount, "0 active");
        }

        public static void TestManagerHasAgent()
        {
            var m = new MassEntityManager();
            AssertFalse(m.HasAgent(999), "unknown id");
            int id = m.Allocate(Vector3.Zero);
            AssertTrue(m.HasAgent(id), "known id");
        }

        public static void TestManagerGetInvalidAgent()
        {
            var m = new MassEntityManager();
            var a = m.GetAgent(999);
            AssertFalse(a.IsValid, "invalid agent view for unknown id");
        }

        public static void TestManagerReserve()
        {
            var m = new MassEntityManager();
            AssertEqual(0, m.Capacity, "0 capacity initially");
            m.Reserve(300);
            AssertTrue(m.Capacity >= 300, "capacity >= 300 after reserve");
            AssertEqual(0, m.ActiveCount, "no agents allocated by reserve");
        }

        public static void TestManagerSetPath()
        {
            var m = new MassEntityManager();
            int id = m.Allocate(Vector3.Zero);
            var path = new List<Vector3> { new Vector3(1, 0, 0), new Vector3(2, 0, 0) };
            m.SetPath(id, path);
            var got = m.GetPath(id);
            AssertTrue(got != null, "path set");
            AssertEqual(2, got!.Count, "2 waypoints");
            m.SetPath(id, null);
            AssertTrue(m.GetPath(id) == null, "path cleared");
        }

        public static void TestManagerPatrolLoop()
        {
            var m = new MassEntityManager();
            int id = m.Allocate(Vector3.Zero);
            var loop = new List<Vector3> { Vector3.Zero, new Vector3(10, 0, 0) };
            m.SetPatrolLoop(id, loop);
            var got = m.GetPatrolLoop(id);
            AssertTrue(got != null, "loop set");
            AssertEqual(2, got!.Count, "2 points");
        }

        public static void TestManagerForEachChunk()
        {
            var m = new MassEntityManager();
            m.Allocate(Vector3.Zero);
            m.Allocate(new Vector3(5, 0, 0));
            int chunksVisited = 0;
            m.ForEachChunk(c => chunksVisited++);
            AssertEqual(1, chunksVisited, "1 chunk visited");
        }

        public static void TestManagerGetAgentIdByLocation()
        {
            var m = new MassEntityManager();
            int id = m.Allocate(Vector3.Zero);
            var chunk = m.Chunks[0];
            int slot = -1;
            for (int i = 0; i < MassAgentChunk.ChunkCapacity; i++)
                if (chunk.Active[i]) { slot = i; break; }
            int recovered = m.GetAgentIdByLocation(chunk, slot);
            AssertEqual(id, recovered, "recovered id from location");
        }

        public static void TestManagerClear()
        {
            var m = new MassEntityManager();
            m.Allocate(Vector3.Zero);
            m.Allocate(new Vector3(1, 0, 0));
            m.Clear();
            AssertEqual(0, m.ActiveCount, "0 after clear");
            AssertEqual(0, m.ChunkCount, "0 chunks after clear");
        }

        // ── MassBehaviorBase SteerTowards ──

        public static void TestSteerTowardsAligned()
        {
            var fwd = new Vector3(0, 0, -1);
            var desired = new Vector3(0, 0, -1);
            var result = MassBehaviorBase.SteerTowardsStatic(fwd, desired, 10f, 0.016f);
            NearV3(desired, result, 0.01f, "already aligned");
        }

        public static void TestSteerTowards90Deg()
        {
            var fwd = new Vector3(0, 0, -1);
            var desired = new Vector3(1, 0, 0);
            var result = MassBehaviorBase.SteerTowardsStatic(fwd, desired, 100f, 1f);
            NearV3(desired, result, 0.01f, "enough turn rate -> reaches target");
        }

        public static void TestSteerTowardsZeroDir()
        {
            var fwd = new Vector3(0, 0, -1);
            var result = MassBehaviorBase.SteerTowardsStatic(fwd, Vector3.Zero, 10f, 0.016f);
            NearV3(fwd, result, 1e-5f, "zero desired -> keep current");
        }

        // ── MassSeekBehavior ──

        public static void TestSeekBehaviorMovesTowardTarget()
        {
            var m = new MassEntityManager();
            var spatial = new SpatialHashGrid(2f);
            var config = new MassCrowdConfig();
            int id = m.Allocate(new Vector3(0, 0, 0));
            var a = m.GetAgent(id);
            a.Behavior = MassBehaviorType.Seek;
            a.TargetPosition = new Vector3(0, 0, -10);
            a.MaxSpeed = 5f;
            a.Forward = new Vector3(0, 0, -1);

            var ctx = new MassBehaviorContext
            {
                Manager = m, SpatialHash = spatial, Config = config,
                FocalPoint = Vector3.Zero, Dt = 0.016f
            };
            var behavior = new MassSeekBehavior();
            behavior.ProcessChunk(ctx, m.Chunks[0], 0.016f);
            // Desired velocity should point toward -Z
            var dv = m.GetAgent(id).DesiredVelocity;
            AssertTrue(dv.Z < 0, "desired velocity toward target (-Z)");
            AssertTrue(dv.Length() > 0, "non-zero desired velocity");
        }

        // ── FleeBehavior ──

        public static void TestFleeBehaviorMovesAway()
        {
            var m = new MassEntityManager();
            var spatial = new SpatialHashGrid(2f);
            var config = new MassCrowdConfig();
            int id = m.Allocate(new Vector3(0, 0, 0));
            var a = m.GetAgent(id);
            a.Behavior = MassBehaviorType.Flee;
            a.ThreatPosition = new Vector3(0, 0, 5); // threat at +Z
            a.Forward = new Vector3(0, 0, -1);       // already facing away

            var ctx = new MassBehaviorContext
            {
                Manager = m, SpatialHash = spatial, Config = config,
                FocalPoint = Vector3.Zero, Dt = 0.016f
            };
            new FleeBehavior().ProcessChunk(ctx, m.Chunks[0], 0.016f);
            var dv = m.GetAgent(id).DesiredVelocity;
            AssertTrue(dv.Z < 0, "fleeing away from threat (toward -Z)");
        }

        // ── MassWanderBehavior ──

        public static void TestWanderBehaviorProducesVelocity()
        {
            var m = new MassEntityManager();
            var spatial = new SpatialHashGrid(2f);
            var config = new MassCrowdConfig();
            int id = m.Allocate(new Vector3(0, 0, 0));
            var a = m.GetAgent(id);
            a.Behavior = MassBehaviorType.Wander;
            a.MaxSpeed = 2f;

            var ctx = new MassBehaviorContext
            {
                Manager = m, SpatialHash = spatial, Config = config,
                FocalPoint = Vector3.Zero, Dt = 0.016f
            };
            new MassWanderBehavior().ProcessChunk(ctx, m.Chunks[0], 0.016f);
            var dv = m.GetAgent(id).DesiredVelocity;
            Near(2f, dv.Length(), 0.1f, "wander velocity ~= maxSpeed");
        }

        // ── PatrolBehavior ──

        public static void TestPatrolBehaviorNoLoop()
        {
            var m = new MassEntityManager();
            var spatial = new SpatialHashGrid(2f);
            var config = new MassCrowdConfig();
            int id = m.Allocate(new Vector3(0, 0, 0));
            var a = m.GetAgent(id);
            a.Behavior = MassBehaviorType.Patrol;
            // No patrol loop set

            var ctx = new MassBehaviorContext
            {
                Manager = m, SpatialHash = spatial, Config = config,
                FocalPoint = Vector3.Zero, Dt = 0.016f
            };
            new PatrolBehavior().ProcessChunk(ctx, m.Chunks[0], 0.016f);
            var dv = m.GetAgent(id).DesiredVelocity;
            Near(0f, dv.Length(), 1e-5f, "no loop -> zero velocity");
        }

        public static void TestPatrolBehaviorWithLoop()
        {
            var m = new MassEntityManager();
            var spatial = new SpatialHashGrid(2f);
            var config = new MassCrowdConfig();
            int id = m.Allocate(new Vector3(0, 0, 0));
            var a = m.GetAgent(id);
            a.Behavior = MassBehaviorType.Patrol;
            a.MaxSpeed = 3f;
            m.SetPatrolLoop(id, new List<Vector3>
            {
                new Vector3(10, 0, 0),
                new Vector3(10, 0, -10),
                new Vector3(0, 0, -10),
            });

            var ctx = new MassBehaviorContext
            {
                Manager = m, SpatialHash = spatial, Config = config,
                FocalPoint = Vector3.Zero, Dt = 0.016f
            };
            new PatrolBehavior().ProcessChunk(ctx, m.Chunks[0], 0.016f);
            var dv = m.GetAgent(id).DesiredVelocity;
            AssertTrue(dv.Length() > 0, "patrol with loop -> moving");
        }

        // ── RVOAvoidance ──

        public static void TestRVO_NoNeighbors()
        {
            var neighbors = new List<NeighborInfo>();
            var v = RVOAvoidance.ComputeAvoidanceVelocity(
                new Vector3(0, 0, 0), new Vector3(0, 0, -1), 0.4f, 2f,
                neighbors, 2f, 0.1f);
            Near(0f, v.X, 1e-5f, "no neighbors -> keep pref X");
            Near(-1f, v.Y, 1e-5f, "no neighbors -> keep pref Z");
        }

        public static void TestRVO_HeadOnAvoidance()
        {
            // A at origin heading -Z, B directly ahead heading +Z (head-on)
            var neighbors = new List<NeighborInfo>
            {
                new NeighborInfo(new Vector3(0, 0, -3), new Vector3(0, 0, 1), 0.4f)
            };
            var v = RVOAvoidance.ComputeAvoidanceVelocity(
                new Vector3(0, 0, 0), new Vector3(0, 0, -2), 0.4f, 3f,
                neighbors, 2f, 0.1f);
            AssertTrue(v.Length() > 0, "produces non-zero velocity");
            // Speed should be clamped to maxSpeed
            AssertTrue(v.Length() <= 3f + 0.1f, "within max speed");
        }

        public static void TestRVO_ClampToMaxSpeed()
        {
            var neighbors = new List<NeighborInfo>();
            var v = RVOAvoidance.ComputeAvoidanceVelocity(
                Vector3.Zero, new Vector3(0, 0, -10), 0.4f, 2f,
                neighbors, 2f, 0.1f);
            Near(2f, v.Length(), 0.01f, "clamped to maxSpeed=2");
        }

        public static void TestRVO_TwoNeighbors()
        {
            var neighbors = new List<NeighborInfo>
            {
                new NeighborInfo(new Vector3(1, 0, -2), new Vector3(0, 0, -1), 0.4f),
                new NeighborInfo(new Vector3(-1, 0, -2), new Vector3(0, 0, -1), 0.4f),
            };
            var v = RVOAvoidance.ComputeAvoidanceVelocity(
                Vector3.Zero, new Vector3(0, 0, -2), 0.4f, 3f,
                neighbors, 2f, 0.1f);
            AssertTrue(v.Length() > 0, "non-zero velocity");
        }

        // ── MassFormationManager ──

        public static void TestFormationCreateGroup()
        {
            var m = new MassEntityManager();
            var fm = new MassFormationManager(m);
            var g = fm.CreateGroup(MassBehaviorType.Formation, MassFormationType.Circle);
            AssertEqual(1, g.GroupId, "first group id=1");
            AssertEqual(MassFormationType.Circle, g.Formation, "circle formation");
            AssertTrue(fm.GetGroup(1) != null, "group retrievable");
        }

        public static void TestFormationAddMember()
        {
            var m = new MassEntityManager();
            var fm = new MassFormationManager(m);
            int aid = m.Allocate(Vector3.Zero);
            var g = fm.CreateGroup(MassBehaviorType.Formation, MassFormationType.Line);
            fm.AddMember(g, aid);
            AssertEqual(1, g.Members.Count, "1 member");
            AssertEqual(g.GroupId, m.GetAgent(aid).GroupId, "agent group id set");
            AssertEqual(MassBehaviorType.Formation, m.GetAgent(aid).Behavior, "agent behavior set");
        }

        public static void TestFormationOffsetCircle()
        {
            var m = new MassEntityManager();
            var fm = new MassFormationManager(m);
            // leader index=0 -> zero offset
            NearV3(Vector3.Zero, fm.ComputeFormationOffset(MassFormationType.Circle, 0, 3, 1.5f), 1e-5f, "leader offset 0");
            // member 1 at radius = 1.5 * 1.5 = 2.25
            var off1 = fm.ComputeFormationOffset(MassFormationType.Circle, 1, 3, 1.5f);
            Near(2.25f, off1.Length(), 0.1f, "circle member at radius");
        }

        public static void TestFormationOffsetColumn()
        {
            var m = new MassEntityManager();
            var fm = new MassFormationManager(m);
            var off1 = fm.ComputeFormationOffset(MassFormationType.Column, 1, 3, 2f);
            NearV3(new Vector3(0, 0, -2), off1, 1e-5f, "column member 1 behind");
            var off2 = fm.ComputeFormationOffset(MassFormationType.Column, 2, 3, 2f);
            NearV3(new Vector3(0, 0, -4), off2, 1e-5f, "column member 2 behind");
        }

        public static void TestFormationOffsetLine()
        {
            var m = new MassEntityManager();
            var fm = new MassFormationManager(m);
            // 3 members: index 0=leader, 1,2. half=(3-1)/2=1
            var off1 = fm.ComputeFormationOffset(MassFormationType.Line, 1, 3, 1f);
            Near(-1f, off1.X, 1e-5f, "line member 1 left of leader");
            var off2 = fm.ComputeFormationOffset(MassFormationType.Line, 2, 3, 1f);
            Near(0f, off2.X, 1e-5f, "line member 2 center");
        }

        public static void TestFormationOffsetNone()
        {
            var m = new MassEntityManager();
            var fm = new MassFormationManager(m);
            var off = fm.ComputeFormationOffset(MassFormationType.None, 1, 3, 1f);
            NearV3(Vector3.Zero, off, 1e-5f, "no formation -> zero offset");
        }

        public static void TestFormationUpdateGroups()
        {
            var m = new MassEntityManager();
            var fm = new MassFormationManager(m);
            int leader = m.Allocate(new Vector3(0, 0, 0));
            int member = m.Allocate(new Vector3(0, 0, 0));
            var g = fm.CreateGroup(MassBehaviorType.Formation, MassFormationType.Column);
            g.Spacing = 2f;
            // 先把成员加入列表，再 SetLeader（SetLeader 会插到 Members[0]），使成员 index=1
            fm.AddMember(g, member);
            fm.SetLeader(g, leader);
            // 验证：leader 在 Members[0]，member 在 Members[1]
            AssertEqual(leader, g.Members[0], "leader at index 0");
            AssertEqual(member, g.Members[1], "member at index 1");
            // Move leader
            var la = m.GetAgent(leader);
            la.Position = new Vector3(0, 0, -5);
            la.Forward = new Vector3(0, 0, -1);
            fm.UpdateGroups(0.016f);
            var ma = m.GetAgent(member);
            // Member index in list = 1, Column offset = (0,0,-1*2) = (0,0,-2) local
            // world = lpos + lfwd*offZ = (0,0,-5) + (0,0,-1)*(-2) = (0,0,-3)（列队偏移向 forward 反方向）
            NearV3(new Vector3(0, 0, -3), ma.TargetPosition, 0.1f, "member target relative to leader");
        }

        // ── MassCrowdSpawner ──

        public static void TestSpawnerSpawnAt()
        {
            var m = new MassEntityManager();
            m.Reserve(10);
            var spatial = new SpatialHashGrid(2f);
            var spawner = new MassCrowdSpawner(m, spatial);
            var ids = spawner.SpawnAt(new Vector3(0, 0, 0), 5);
            AssertEqual(5, ids.Count, "spawned 5");
            AssertEqual(5, m.ActiveCount, "5 active");
        }

        public static void TestSpawnerDespawn()
        {
            var m = new MassEntityManager();
            m.Reserve(5);
            var spatial = new SpatialHashGrid(2f);
            var spawner = new MassCrowdSpawner(m, spatial);
            var ids = spawner.SpawnAt(Vector3.Zero, 3);
            spawner.Despawn(ids[0]);
            var a = m.GetAgent(ids[0]);
            AssertEqual(MassAgentState.Dying, a.State, "despawned -> Dying");
        }

        public static void TestSpawnerDespawnUnknown()
        {
            var m = new MassEntityManager();
            var spatial = new SpatialHashGrid(2f);
            var spawner = new MassCrowdSpawner(m, spatial);
            // Should not throw
            spawner.Despawn(999);
        }

        // ── MassGroup ──

        public static void TestGroupDefaults()
        {
            var g = new MassGroup(5);
            AssertEqual(5, g.GroupId, "group id");
            AssertEqual(0, g.LeaderAgentId, "no leader");
            AssertEqual(MassFormationType.None, g.Formation, "no formation");
            Near(1.5f, g.Spacing, 1e-5f, "default spacing");
            AssertEqual(0, g.Members.Count, "no members");
        }

        // ── MassCrowdConfig ──

        public static void TestConfigDefaults()
        {
            var c = new MassCrowdConfig();
            Near(8f, c.MaxTurnRate, 1e-5f, "default turn rate");
            Near(20f, c.FleeSafeDistance, 1e-5f, "default flee distance");
            Near(1.2f, c.QueueStopDistance, 1e-5f, "default queue stop");
        }

        // ── 集成：多代理寻路移动 ──

        public static void TestIntegrationMultipleSeekAgents()
        {
            var m = new MassEntityManager();
            var spatial = new SpatialHashGrid(2f);
            var config = new MassCrowdConfig();
            var ctx = new MassBehaviorContext
            {
                Manager = m, SpatialHash = spatial, Config = config,
                FocalPoint = Vector3.Zero, Dt = 0.016f
            };
            var ids = m.Allocate(3, Vector3.Zero, 1f);
            var behavior = new MassSeekBehavior();
            foreach (int id in ids)
            {
                var a = m.GetAgent(id);
                a.Behavior = MassBehaviorType.Seek;
                a.TargetPosition = new Vector3(0, 0, -20);
                a.Forward = new Vector3(0, 0, -1);
            }
            behavior.ProcessChunk(ctx, m.Chunks[0], 0.016f);
            foreach (int id in ids)
            {
                var a = m.GetAgent(id);
                AssertTrue(a.DesiredVelocity.Z < 0, $"agent {id} seeks toward -Z");
            }
        }
    }
}
