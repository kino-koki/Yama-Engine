using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.AI;
using YamaEngine.GameFramework;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    /// <summary>
    /// 增强感知系统（Enhanced Perception System）深度测试。对齐 UE5 AI Perception。
    /// 覆盖：感知通道/目标/刺激、AABB 遮挡、感知过滤、四通道感官、
    ///       短期/长期记忆、遗忘曲线、注意力机制、主组件生命周期。
    /// </summary>
    public static class EnhancedPerceptionDepthTests
    {
        private static void Near(float want, float got, float tol, string msg)
        {
            if (Math.Abs(want - got) > tol) throw new Exception($"{msg}: want {want}, got {got}");
        }
        private static void AssertFalse(bool cond, string msg) { if (cond) throw new Exception(msg); }

        private static Actor MakeActor(Vector3 pos, string name = "A")
        {
            return new Actor { Position = pos, Name = name };
        }

        // ── 枚举 / 目标 / 刺激 ──

        public static void TestSenseChannelFlags()
        {
            AssertEqual(0, (int)EnhancedSenseChannel.None, "None=0");
            AssertEqual(1, (int)EnhancedSenseChannel.Sight, "Sight=1");
            AssertEqual(2, (int)EnhancedSenseChannel.Hearing, "Hearing=2");
            AssertEqual(4, (int)EnhancedSenseChannel.Smell, "Smell=4");
            AssertEqual(8, (int)EnhancedSenseChannel.Touch, "Touch=8");
            AssertEqual(15, (int)EnhancedSenseChannel.All, "All=15");
        }

        public static void TestPerceptionEventValues()
        {
            AssertEqual(0, (int)EnhancedPerceptionEvent.Perceived, "Perceived=0");
            AssertEqual(1, (int)EnhancedPerceptionEvent.Updated, "Updated=1");
            AssertEqual(2, (int)EnhancedPerceptionEvent.Lost, "Lost=2");
            AssertEqual(3, (int)EnhancedPerceptionEvent.Heard, "Heard=3");
            AssertEqual(4, (int)EnhancedPerceptionEvent.Smelled, "Smelled=4");
            AssertEqual(5, (int)EnhancedPerceptionEvent.Touched, "Touched=5");
        }

        public static void TestTargetDefaults()
        {
            var t = new EnhancedPerceptionTarget(42);
            AssertEqual(42, t.Id, "id");
            AssertTrue(t.Actor == null, "no actor");
            AssertEqual(Vector3.Zero, t.Position, "pos zero");
            AssertEqual(Vector3.Zero, t.Velocity, "vel zero");
            Near(0f, t.Threat, 1e-5f, "threat 0");
            Near(1f, t.Camouflage, 1e-5f, "camouflage 1");
            Near(1f, t.Size, 1e-5f, "size 1");
            AssertTrue(t.Synced == false, "not synced");
        }

        public static void TestTargetSyncFromActor()
        {
            var a = MakeActor(new Vector3(3, 4, 5), "Enemy");
            var t = new EnhancedPerceptionTarget(1) { Actor = a };
            t.SyncFromActor();
            AssertEqual(new Vector3(3, 4, 5), t.Position, "pos synced");
            AssertEqual("Enemy", t.TypeTag, "typeTag from name");
        }

        public static void TestStimulusDefaults()
        {
            var s = new EnhancedStimulus();
            AssertEqual(EnhancedSenseChannel.None, s.Channel, "channel none");
            AssertEqual(Vector3.Zero, s.Position, "pos zero");
            Near(0f, s.Strength, 1e-5f, "strength 0");
            Near(0f, s.Timestamp, 1e-5f, "timestamp 0");
            AssertTrue(s.Target == null, "no target");
            AssertTrue(s.IsNew == false, "not new");
        }

        // ── AABB 遮挡检测 ──

        public static void TestAABBOcclusionAdd()
        {
            var c = new AABBOcclusionChecker();
            AssertEqual(0, c.Obstacles.Count, "empty");
            c.AddBox(Vector3.Zero, new Vector3(1, 1, 1));
            AssertEqual(1, c.Obstacles.Count, "1 box");
            Near(-1f, c.Obstacles[0].Min.X, 1e-5f, "min X");
            Near(1f, c.Obstacles[0].Max.X, 1e-5f, "max X");
        }

        public static void TestAABBOcclusionBlocked()
        {
            var c = new AABBOcclusionChecker();
            c.AddBox(new Vector3(5, 0, 0), new Vector3(1, 1, 1));
            bool hit = c.IsOccluded(new Vector3(0, 0, 0), new Vector3(10, 0, 0));
            AssertTrue(hit, "line through box center is occluded");
        }

        public static void TestAABBOcclusionClear()
        {
            var c = new AABBOcclusionChecker();
            c.AddBox(new Vector3(5, 0, 0), new Vector3(1, 1, 1));
            bool hit = c.IsOccluded(new Vector3(0, 0, 0), new Vector3(10, 0, 0));
            AssertTrue(hit, "blocked before clear");
            c.Obstacles.Clear();
            AssertFalse(c.IsOccluded(new Vector3(0, 0, 0), new Vector3(10, 0, 0)), "clear -> not blocked");
        }

        public static void TestAABBOcclusionMiss()
        {
            var c = new AABBOcclusionChecker();
            c.AddBox(new Vector3(0, 10, 0), new Vector3(1, 1, 1));
            bool hit = c.IsOccluded(new Vector3(0, 0, 0), new Vector3(10, 0, 0));
            AssertFalse(hit, "line doesn't pass through box above");
        }

        public static void TestAABBOcclusionBehind()
        {
            var c = new AABBOcclusionChecker();
            c.AddBox(new Vector3(-5, 0, 0), new Vector3(1, 1, 1));
            bool hit = c.IsOccluded(new Vector3(0, 0, 0), new Vector3(10, 0, 0));
            AssertFalse(hit, "box behind ray is not occluding");
        }

        public static void TestRayAABBStatic()
        {
            bool hit = AABBOcclusionChecker.RayAABB(
                new Vector3(0, 0, 0), new Vector3(1, 0, 0), 20f,
                new Vector3(5, -1, -1), new Vector3(8, 1, 1), out float t);
            AssertTrue(hit, "hit");
            Near(5f, t, 0.01f, "t=5 at min.X");
        }

        // ── PerceptionFilter ──

        public static void TestFilterSightInRange()
        {
            var f = new PerceptionFilter { EnableOcclusion = false };
            var target = new EnhancedPerceptionTarget(1) { Position = new Vector3(0, 0, 100) };
            // owner at origin looking +Z, target directly ahead -> in FOV
            float fov = 90f * 0.5f * MathF.PI / 180f;
            float strength = f.SightStrength(Vector3.Zero, Vector3.UnitZ, fov, 500f,
                target, false, 0f);
            AssertTrue(strength > 0f, "target in FOV -> strength > 0");
        }

        public static void TestFilterSightOutOfRange()
        {
            var f = new PerceptionFilter { EnableOcclusion = false };
            var target = new EnhancedPerceptionTarget(1) { Position = new Vector3(0, 0, 600) };
            float fov = MathF.PI / 2f;
            float strength = f.SightStrength(Vector3.Zero, Vector3.UnitZ, fov, 500f,
                target, false, 0f);
            Near(0f, strength, 1e-5f, "out of range -> 0");
        }

        public static void TestFilterSightFOVMiss()
        {
            var f = new PerceptionFilter { EnableOcclusion = false };
            var target = new EnhancedPerceptionTarget(1) { Position = new Vector3(0, 0, -100) };
            float fov = MathF.PI / 2f; // 90 deg half-angle
            float strength = f.SightStrength(Vector3.Zero, Vector3.UnitZ, fov, 500f,
                target, false, 0f);
            Near(0f, strength, 1e-5f, "behind -> 0");
        }

        public static void TestFilterSightCamouflageReduces()
        {
            var f = new PerceptionFilter { EnableOcclusion = false };
            var normal = new EnhancedPerceptionTarget(1) { Position = new Vector3(0, 0, 100), Camouflage = 1f };
            var camo = new EnhancedPerceptionTarget(2) { Position = new Vector3(0, 0, 100), Camouflage = 2f };
            float fov = MathF.PI / 2f;
            float s1 = f.SightStrength(Vector3.Zero, Vector3.UnitZ, fov, 500f, normal, false, 0f);
            float s2 = f.SightStrength(Vector3.Zero, Vector3.UnitZ, fov, 500f, camo, false, 0f);
            AssertTrue(s2 < s1, "higher camouflage -> lower strength");
        }

        public static void TestFilterSightOcclusion()
        {
            var f = new PerceptionFilter { EnableOcclusion = true };
            f.Occlusion.AddBox(new Vector3(0, 0, 50), new Vector3(1, 1, 1));
            var target = new EnhancedPerceptionTarget(1) { Position = new Vector3(0, 0, 100) };
            float fov = MathF.PI / 2f;
            float strength = f.SightStrength(Vector3.Zero, Vector3.UnitZ, fov, 500f,
                target, false, 0f);
            Near(0f, strength, 1e-5f, "occluded -> 0");
        }

        public static void TestFilterHearing()
        {
            var f = new PerceptionFilter { AmbientNoise = 0.0f };
            float s1 = f.HearingStrength(Vector3.Zero, 300f, new Vector3(50, 0, 0), 1.0f);
            AssertTrue(s1 > 0f, "close loud sound -> positive");
            float s2 = f.HearingStrength(Vector3.Zero, 300f, new Vector3(250, 0, 0), 1.0f);
            AssertTrue(s2 < s1, "farther -> weaker");
            float s3 = f.HearingStrength(Vector3.Zero, 300f, new Vector3(500, 0, 0), 1.0f);
            Near(0f, s3, 1e-5f, "out of range -> 0");
        }

        // ── 短期记忆 ──

        public static void TestSTMRecordAndLastKnown()
        {
            var m = new ShortTermMemory { Duration = 5f };
            m.Record(1, new Vector3(1, 0, 0), 0f, 1f);
            m.Record(1, new Vector3(2, 0, 0), 1f, 1f);
            var last = m.LastKnownPosition(1);
            AssertTrue(last.HasValue, "has last known");
            AssertEqual(new Vector3(2, 0, 0), last!.Value, "last pos");
        }

        public static void TestSTMExpire()
        {
            var m = new ShortTermMemory { Duration = 3f };
            m.Record(1, new Vector3(1, 0, 0), 0f, 1f);
            m.Update(2f); // within duration
            AssertTrue(m.LastKnownPosition(1).HasValue, "still valid");
            m.Update(4f); // expired
            AssertFalse(m.LastKnownPosition(1).HasValue, "expired");
        }

        public static void TestSTMForget()
        {
            var m = new ShortTermMemory();
            m.Record(1, Vector3.Zero, 0f, 1f);
            AssertTrue(m.LastKnownPosition(1).HasValue, "exists");
            m.Forget(1);
            AssertFalse(m.LastKnownPosition(1).HasValue, "forgotten");
        }

        // ── 遗忘曲线 / 长期记忆 ──

        public static void TestForgettingCurveRetention()
        {
            float r0 = ForgettingCurve.Retention(0f, 1f);
            Near(1f, r0, 0.01f, "at t=0 retention=1");
            float r1 = ForgettingCurve.Retention(1f, 1f);
            AssertTrue(r1 > 0.3f && r1 < 0.4f, "at t=S retention ~ e^-1");
            float r2 = ForgettingCurve.Retention(5f, 1f);
            AssertTrue(r2 < r1, "longer elapsed -> less retention");
        }

        public static void TestForgettingCurveReinforce()
        {
            float s0 = 1f;
            float s1 = ForgettingCurve.Reinforce(s0, 1);
            AssertTrue(s1 > s0, "reinforce increases strength");
            float s2 = ForgettingCurve.Reinforce(s0, 10);
            AssertTrue(s2 > s0 && s2 < s1, "more reviews -> smaller boost (diminishing)");
        }

        public static void TestLTMEncounterAndConfidence()
        {
            var m = new LongTermMemory();
            m.Encounter("Enemy", 0.8f, 0f);
            float conf = m.Confidence("Enemy", 0f);
            AssertTrue(conf > 0f, "just seen -> high confidence");
            float conf2 = m.Confidence("Enemy", 100f);
            AssertTrue(conf2 < conf, "long time -> lower confidence");
            Near(0f, m.Confidence("Unknown", 0f), 1e-5f, "unknown type -> 0");
        }

        public static void TestLTMGetEntry()
        {
            var m = new LongTermMemory();
            m.Encounter("Sniper", 1.0f, 10f);
            var e = m.GetEntry("Sniper");
            AssertTrue(e != null, "entry exists");
            AssertEqual(1, e!.EncounterCount, "1 encounter");
            AssertEqual("Sniper", e.TypeTag, "type tag");
            Near(1.0f, e.Threat, 1e-5f, "threat set");
        }

        public static void TestLTMMultipleEncounters()
        {
            var m = new LongTermMemory();
            m.Encounter("Grunt", 0.3f, 0f);
            m.Encounter("Grunt", 0.6f, 1f);
            m.Encounter("Grunt", 0.9f, 2f);
            var e = m.GetEntry("Grunt");
            AssertEqual(3, e!.EncounterCount, "3 encounters");
            AssertTrue(e.Threat > 0.5f, "threat converges upward");
            AssertTrue(e.MemoryStrength > 1f, "memory strengthened");
        }

        // ── PerceptionMemorySystem ──

        public static void TestMemorySystemRecord()
        {
            var ms = new PerceptionMemorySystem();
            var t = new EnhancedPerceptionTarget(1) { TypeTag = "Soldier", Threat = 0.5f };
            ms.Record(t, new Vector3(1, 2, 3), 0f, 1f);
            var last = ms.LastKnownPosition(1);
            AssertTrue(last.HasValue, "STM recorded");
            AssertEqual(new Vector3(1, 2, 3), last!.Value, "pos");
            AssertTrue(ms.TypeConfidence("Soldier", 0f) > 0f, "LTM confidence > 0");
        }

        // ── 注意力系统 ──

        public static void TestAttentionEmpty()
        {
            var at = new AttentionSystem();
            var ms = new PerceptionMemorySystem();
            at.Reallocate(new Dictionary<int, EnhancedPerceptionTarget>(), 0f, ms);
            AssertEqual(0, at.Entries.Count, "no entries");
            AssertTrue(at.FocusTarget == null, "no focus");
        }

        public static void TestAttentionSingleTarget()
        {
            var at = new AttentionSystem();
            var ms = new PerceptionMemorySystem();
            var t = new EnhancedPerceptionTarget(1) { Threat = 0.9f, TypeTag = "Boss" };
            ms.Record(t, Vector3.Zero, 0f, 1f);
            at.Reallocate(new Dictionary<int, EnhancedPerceptionTarget> { { 1, t } }, 0f, ms);
            AssertEqual(1, at.Entries.Count, "1 entry");
            AssertTrue(at.FocusTarget != null, "single target is focus");
            AssertEqual(1, at.FocusTarget!.Id, "focus is target 1");
        }

        public static void TestAttentionBudgetSums()
        {
            var at = new AttentionSystem { MaxTracked = 3 };
            var ms = new PerceptionMemorySystem();
            var perceived = new Dictionary<int, EnhancedPerceptionTarget>();
            for (int i = 0; i < 5; i++)
            {
                var t = new EnhancedPerceptionTarget(i) { Threat = 0.3f + i * 0.1f, TypeTag = "T" };
                ms.Record(t, Vector3.Zero, 0f, 1f);
                perceived[i] = t;
            }
            at.Reallocate(perceived, 0f, ms);
            AssertEqual(3, at.Entries.Count, "only MaxTracked entries");
            float sum = 0;
            foreach (var e in at.Entries) sum += e.Attention;
            Near(1f, sum, 0.01f, "attention budget sums to ~1");
        }

        public static void TestAttentionHighestThreatIsFocus()
        {
            var at = new AttentionSystem();
            var ms = new PerceptionMemorySystem();
            var low = new EnhancedPerceptionTarget(1) { Threat = 0.2f, TypeTag = "Grunt" };
            var high = new EnhancedPerceptionTarget(2) { Threat = 0.95f, TypeTag = "Boss" };
            ms.Record(low, Vector3.Zero, 0f, 1f);
            ms.Record(high, Vector3.Zero, 0f, 1f);
            at.Reallocate(new Dictionary<int, EnhancedPerceptionTarget>
            {
                { 1, low }, { 2, high }
            }, 0f, ms);
            AssertTrue(at.FocusTarget != null, "has focus");
            AssertEqual(2, at.FocusTarget!.Id, "highest threat is focus");
        }

        public static void TestAttentionIsTracked()
        {
            var at = new AttentionSystem { MaxTracked = 2 };
            var ms = new PerceptionMemorySystem();
            var perceived = new Dictionary<int, EnhancedPerceptionTarget>();
            for (int i = 0; i < 3; i++)
            {
                var t = new EnhancedPerceptionTarget(i) { Threat = 0.5f, TypeTag = "T" };
                ms.Record(t, Vector3.Zero, 0f, 1f);
                perceived[i] = t;
            }
            at.Reallocate(perceived, 0f, ms);
            int trackedCount = 0;
            for (int i = 0; i < 3; i++) if (at.IsTracked(i)) trackedCount++;
            AssertEqual(2, trackedCount, "only MaxTracked are tracked");
        }

        // ── 主组件 ──

        public static void TestComponentDefaults()
        {
            var c = new EnhancedPerceptionComponent();
            AssertTrue(c.Owner == null, "no owner");
            AssertEqual(0, c.Senses.Count, "no senses");
            AssertEqual(0, c.Targets.Count, "no targets");
            AssertEqual(0, c.PerceivedTargets.Count, "nothing perceived");
            AssertTrue(c.GetFocusTarget() == null, "no focus");
        }

        public static void TestComponentWithOwner()
        {
            var a = MakeActor(new Vector3(1, 2, 3));
            var c = new EnhancedPerceptionComponent(a);
            AssertEqual(new Vector3(1, 2, 3), c.GetOwnerPosition(), "pos from owner");
        }

        public static void TestComponentSetTransformProviders()
        {
            var c = new EnhancedPerceptionComponent();
            c.SetTransformProviders(
                () => new Vector3(10, 0, 0),
                () => Vector3.UnitX);
            AssertEqual(new Vector3(10, 0, 0), c.GetOwnerPosition(), "custom pos");
            AssertEqual(Vector3.UnitX, c.GetOwnerForward(), "custom fwd");
        }

        public static void TestRegisterDefaultSenses()
        {
            var c = new EnhancedPerceptionComponent();
            c.RegisterDefaultSenses();
            AssertEqual(4, c.Senses.Count, "4 senses (sight/hearing/smell/touch)");
        }

        public static void TestRegisterTargetFromActor()
        {
            var c = new EnhancedPerceptionComponent();
            var a = MakeActor(new Vector3(5, 0, 0), "Enemy");
            var t = c.RegisterTarget(a, 1, 0.7f);
            AssertEqual(1, c.Targets.Count, "1 target");
            AssertEqual(1, t.Id, "id");
            Near(0.7f, t.Threat, 1e-5f, "threat");
            AssertTrue(t.Synced, "synced from actor");
        }

        public static void TestUnregisterTarget()
        {
            var c = new EnhancedPerceptionComponent();
            c.RegisterTarget(MakeActor(Vector3.Zero), 1);
            c.RegisterTarget(MakeActor(Vector3.One), 2);
            AssertEqual(2, c.Targets.Count, "2 targets");
            c.UnregisterTarget(1);
            AssertEqual(1, c.Targets.Count, "1 remaining");
        }

        public static void TestSightSenseDetectsTarget()
        {
            var c = new EnhancedPerceptionComponent();
            c.SetTransformProviders(() => Vector3.Zero, () => Vector3.UnitZ);
            var sight = new EnhancedSightSense { Range = 200f, FieldOfView = 90f, UseSphere = false };
            c.Senses.Add(sight);

            var target = new EnhancedPerceptionTarget(1)
            {
                Position = new Vector3(0, 0, 100), // directly ahead
                TypeTag = "Enemy", Threat = 0.5f
            };
            c.RegisterTarget(target);

            int perceivedCount = 0;
            c.OnPerceived += t => perceivedCount++;
            c.Update(0.016f);
            AssertTrue(perceivedCount > 0, "target perceived on first tick");
            AssertTrue(c.PerceivedTargets.ContainsKey(1), "target in perceived map");
        }

        public static void TestSightSenseMissesBehind()
        {
            var c = new EnhancedPerceptionComponent();
            c.SetTransformProviders(() => Vector3.Zero, () => Vector3.UnitZ);
            var sight = new EnhancedSightSense { Range = 200f, FieldOfView = 60f, UseSphere = false };
            c.Senses.Add(sight);
            var target = new EnhancedPerceptionTarget(1) { Position = new Vector3(0, 0, -100) };
            c.RegisterTarget(target);
            c.Update(0.016f);
            AssertFalse(c.PerceivedTargets.ContainsKey(1), "behind target not perceived");
        }

        public static void TestOnLostEvent()
        {
            var c = new EnhancedPerceptionComponent();
            c.SetTransformProviders(() => Vector3.Zero, () => Vector3.UnitZ);
            c.Senses.Add(new EnhancedSightSense { Range = 200f, FieldOfView = 90f, UseSphere = false });
            var target = new EnhancedPerceptionTarget(1) { Position = new Vector3(0, 0, 100) };
            c.RegisterTarget(target);

            bool lost = false;
            c.OnLost += t => lost = true;
            c.Update(0.016f);
            AssertTrue(c.PerceivedTargets.ContainsKey(1), "perceived");
            // Move target far away and tick until STM expires
            target.Position = new Vector3(0, 0, -1000);
            for (int i = 0; i < 100; i++) c.Update(0.1f);
            AssertTrue(lost, "target eventually lost");
        }

        public static void TestHearingSenseReportNoise()
        {
            var c = new EnhancedPerceptionComponent();
            c.SetTransformProviders(() => Vector3.Zero, () => Vector3.UnitZ);
            var hearing = new EnhancedHearingSense { Range = 300f, MaxAge = 5f };
            c.Senses.Add(hearing);

            Vector3? heardPos = null;
            c.OnHeard += pos => heardPos = pos;
            hearing.ReportNoise(new Vector3(100, 0, 0), 1.0f, 0f);
            c.Update(0.016f);
            AssertTrue(heardPos.HasValue, "heard event fired");
            AssertEqual(new Vector3(100, 0, 0), heardPos!.Value, "heard pos");
        }

        public static void TestHearingMaxAgeExpire()
        {
            var c = new EnhancedPerceptionComponent();
            c.SetTransformProviders(() => Vector3.Zero, () => Vector3.UnitZ);
            var hearing = new EnhancedHearingSense { Range = 300f, MaxAge = 1f };
            c.Senses.Add(hearing);
            int heardCount = 0;
            c.OnHeard += _ => heardCount++;
            hearing.ReportNoise(new Vector3(50, 0, 0), 1.0f, 0f);
            c.Update(0.1f);
            AssertTrue(heardCount > 0, "heard first time");
            int before = heardCount;
            c.Update(2f); // noise expired
            AssertEqual(before, heardCount, "no new hear after expiry");
        }

        public static void TestSmellSenseDropScent()
        {
            var c = new EnhancedPerceptionComponent();
            c.SetTransformProviders(() => Vector3.Zero, () => Vector3.UnitZ);
            var smell = new EnhancedSmellSense { Range = 150f, MaxTrailAge = 8f };
            c.Senses.Add(smell);

            Vector3? smelled = null;
            c.OnSmelled += pos => smelled = pos;
            smell.DropScent(1, new Vector3(50, 0, 0), 0f);
            c.Update(0.1f);
            AssertTrue(smelled.HasValue, "smelled event fired");
        }

        public static void TestTouchSenseReportTouch()
        {
            var c = new EnhancedPerceptionComponent();
            c.SetTransformProviders(() => Vector3.Zero, () => Vector3.UnitZ);
            var touch = new EnhancedTouchSense { Radius = 2f, MaxAge = 0.5f };
            c.Senses.Add(touch);

            int touchedCount = 0;
            c.OnPerceptionEvent += (evt, t, pos) =>
            {
                if (evt == EnhancedPerceptionEvent.Touched) touchedCount++;
            };
            var target = new EnhancedPerceptionTarget(1);
            touch.ReportTouch(target, new Vector3(1, 0, 0), 0f);
            c.Update(0.016f);
            AssertTrue(touchedCount > 0, "touch event fired");
        }

        public static void TestClear()
        {
            var c = new EnhancedPerceptionComponent();
            c.SetTransformProviders(() => Vector3.Zero, () => Vector3.UnitZ);
            c.Senses.Add(new EnhancedSightSense { Range = 200f, FieldOfView = 90f, UseSphere = false });
            c.RegisterTarget(new EnhancedPerceptionTarget(1) { Position = new Vector3(0, 0, 100) });
            c.Update(0.016f);
            AssertTrue(c.PerceivedTargets.Count > 0, "perceived before clear");
            c.Clear();
            AssertEqual(0, c.PerceivedTargets.Count, "cleared perceived");
            AssertEqual(0, c.Targets.Count, "cleared targets");
        }

        public static void TestMemoryIntegratesWithComponent()
        {
            var c = new EnhancedPerceptionComponent();
            c.SetTransformProviders(() => Vector3.Zero, () => Vector3.UnitZ);
            c.Senses.Add(new EnhancedSightSense { Range = 200f, FieldOfView = 90f, UseSphere = false });
            var t = new EnhancedPerceptionTarget(1) { Position = new Vector3(0, 0, 100), TypeTag = "Enemy", Threat = 0.8f };
            c.RegisterTarget(t);
            c.Update(0.016f);
            var lastKnown = c.GetLastKnownPosition(1);
            AssertTrue(lastKnown.HasValue, "memory has last known pos");
        }
    }
}
