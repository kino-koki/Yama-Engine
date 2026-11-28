using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.AI;
using YamaEngine.GameFramework;
using static YamaEngine.Tests.TestHelpers;
using AIController = YamaEngine.AI.AIController;

namespace YamaEngine.Tests
{
    /// <summary>
    /// AIController 深度测试。对齐 UE5 AIController + Utility AI 决策。
    /// 覆盖：AIStateType/AIState/AIStateTransition/AIAction/AIDecisionMaker/
    ///       AIMemory/AICommunication/8 种内置行动。
    /// </summary>
    public static class AIControllerDepthTests
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

        private static AIController MakeController(Vector3 pos = default)
        {
            var pawn = new Pawn { Position = pos };
            var ctrl = new AIController();
            ctrl.Possess(pawn);
            return ctrl;
        }

        /// <summary>Register OnMessageReceived callback BEFORE broadcast so queued messages get delivered.</summary>
        private static int DeliverBroadcast(AICommunication c, AITeamId team, AICommunication.AIMessage msg,
            Func<AIController, AICommunication.AIMessage, bool>? filter = null)
        {
            int received = 0;
            c.OnMessageReceived = (member, m) =>
            {
                if (filter == null || filter(member, m)) received++;
            };
            c.Broadcast(team, msg);
            c.ProcessMessages(0f);
            return received;
        }

        // ── 枚举 ──

        public static void TestAIStateTypeValues()
        {
            AssertEqual(0, (int)AIStateType.Idle, "Idle=0");
            AssertEqual(1, (int)AIStateType.Alert, "Alert=1");
            AssertEqual(2, (int)AIStateType.Chase, "Chase=2");
            AssertEqual(3, (int)AIStateType.Attack, "Attack=3");
            AssertEqual(4, (int)AIStateType.Flee, "Flee=4");
            AssertEqual(5, (int)AIStateType.Patrol, "Patrol=5");
            AssertEqual(6, (int)AIStateType.Search, "Search=6");
            AssertEqual(7, (int)AIStateType.Dead, "Dead=7");
            AssertEqual(8, (int)AIStateType.Interact, "Interact=8");
            AssertEqual(9, (int)AIStateType.HoldPosition, "HoldPosition=9");
            AssertEqual(10, (int)AIStateType.Follow, "Follow=10");
            AssertEqual(11, (int)AIStateType.Custom, "Custom=11");
        }

        public static void TestMessageTypeValues()
        {
            AssertEqual(0, (int)AICommunication.MessageType.None, "None=0");
            AssertEqual(1, (int)AICommunication.MessageType.EnemySpotted, "EnemySpotted=1");
            AssertEqual(2, (int)AICommunication.MessageType.UnderAttack, "UnderAttack=2");
            AssertEqual(3, (int)AICommunication.MessageType.NeedBackup, "NeedBackup=3");
            AssertEqual(4, (int)AICommunication.MessageType.TargetDown, "TargetDown=4");
            AssertEqual(5, (int)AICommunication.MessageType.RallyPoint, "RallyPoint=5");
            AssertEqual(6, (int)AICommunication.MessageType.FlankLeft, "FlankLeft=6");
            AssertEqual(7, (int)AICommunication.MessageType.FlankRight, "FlankRight=7");
            AssertEqual(8, (int)AICommunication.MessageType.TakeCover, "TakeCover=8");
            AssertEqual(9, (int)AICommunication.MessageType.Regroup, "Regroup=9");
            AssertEqual(10, (int)AICommunication.MessageType.Custom, "Custom=10");
        }

        // ── AIState 生命周期 ──

        public static void TestAIStateDefaults()
        {
            var s = new AIState();
            AssertEqual(AIStateType.Idle, s.StateType, "default type");
            AssertEqual("Idle", s.StateName, "default name");
            Near(0f, s.MinDuration, 1e-5f, "min duration");
            Near(0f, s.MaxDuration, 1e-5f, "max duration");
            Near(0f, s.ElapsedTime, 1e-5f, "elapsed 0");
            AssertTrue(s.OnEnter == null, "no enter");
            AssertTrue(s.OnUpdate == null, "no update");
            AssertTrue(s.OnExit == null, "no exit");
        }

        public static void TestAIStateEnterResetElapsedTime()
        {
            var s = new AIState { StateType = AIStateType.Alert, StateName = "Alert", MaxDuration = 5f };
            s.Update(null!, 3f);
            Near(3f, s.ElapsedTime, 1e-5f, "elapsed 3");
            s.Enter(null!);
            Near(0f, s.ElapsedTime, 1e-5f, "reset after Enter");
        }

        public static void TestAIStateUpdateAccumulates()
        {
            var s = new AIState { StateType = AIStateType.Chase };
            s.Enter(null!);
            s.Update(null!, 0.1f);
            s.Update(null!, 0.2f);
            Near(0.3f, s.ElapsedTime, 0.01f, "elapsed 0.3");
        }

        public static void TestAIStateDurationExpiredNoLimit()
        {
            var s = new AIState { StateType = AIStateType.Idle, MaxDuration = 0f };
            s.Enter(null!);
            s.Update(null!, 1000f);
            AssertFalse(s.IsDurationExpired(), "MaxDuration=0 -> never expires");
        }

        public static void TestAIStateDurationExpiredWithLimit()
        {
            var s = new AIState { StateType = AIStateType.Alert, MinDuration = 2f, MaxDuration = 4f };
            s.Enter(null!);
            s.Update(null!, 1f);
            AssertFalse(s.IsDurationExpired(), "below min -> not expired");
            s.Update(null!, 5f);
            AssertTrue(s.IsDurationExpired(), "above max -> expired");
        }

        public static void TestAIStateCallbacks()
        {
            bool entered = false, exited = false, updated = false;
            var s = new AIState
            {
                StateType = AIStateType.Attack,
                OnEnter = _ => entered = true,
                OnUpdate = (_, _) => { updated = true; return false; },
                OnExit = _ => exited = true,
            };
            s.Enter(null!);
            AssertTrue(entered, "enter callback");
            s.Update(null!, 0.1f);
            AssertTrue(updated, "update callback");
            s.Exit(null!);
            AssertTrue(exited, "exit callback");
        }

        // ── AIStateTransition ──

        public static void TestTransitionDefaults()
        {
            var t = new AIStateTransition();
            AssertEqual(AIStateType.Idle, t.FromState, "default from");
            AssertEqual(AIStateType.Idle, t.ToState, "default to");
            AssertTrue(t.Condition == null, "no condition");
            AssertEqual(0, t.Priority, "priority 0");
            AssertFalse(t.OneShot, "not one-shot");
        }

        public static void TestTransitionCtor()
        {
            var t = new AIStateTransition(AIStateType.Idle, AIStateType.Chase, _ => true, 5);
            AssertEqual(AIStateType.Idle, t.FromState, "from");
            AssertEqual(AIStateType.Chase, t.ToState, "to");
            AssertEqual(5, t.Priority, "priority");
        }

        public static void TestTransitionEvaluateTrue()
        {
            var t = new AIStateTransition(AIStateType.Idle, AIStateType.Alert, _ => true);
            AssertTrue(t.Evaluate(null!), "condition true -> evaluate true");
        }

        public static void TestTransitionEvaluateFalse()
        {
            var t = new AIStateTransition(AIStateType.Idle, AIStateType.Alert, _ => false);
            AssertFalse(t.Evaluate(null!), "condition false -> evaluate false");
        }

        public static void TestTransitionNoCondition()
        {
            var t = new AIStateTransition(AIStateType.Idle, AIStateType.Alert, null);
            // Engine behavior: Condition==null -> Evaluate returns false (never auto-triggers)
            AssertFalse(t.Evaluate(null!), "no condition -> Evaluate returns false");
        }

        public static void TestTransitionOneShot()
        {
            var t = new AIStateTransition(AIStateType.Idle, AIStateType.Alert, _ => true) { OneShot = true };
            AssertTrue(t.Evaluate(null!), "first evaluate true");
            AssertFalse(t.Evaluate(null!), "one-shot consumed -> false");
        }

        public static void TestTransitionOnTransitionCallback()
        {
            bool called = false;
            var t = new AIStateTransition(AIStateType.Idle, AIStateType.Chase, _ => true)
            {
                OnTransition = _ => called = true
            };
            // OnTransition is invoked by AIController.UpdateStateMachine after Evaluate returns true,
            // not by Evaluate itself. Verify the delegate is invokable.
            t.OnTransition?.Invoke(null!);
            AssertTrue(called, "OnTransition callback invocable");
        }

        // ── AIAction 基类 ──

        public static void TestActionDefaults()
        {
            var a = new AIAction_Patrol();
            AssertEqual("Patrol", a.ActionName, "name");
            Near(0.2f, a.Cost, 1e-5f, "cost");
            AssertEqual(10, a.Priority, "priority");
            AssertTrue(a.IsAvailable, "available default");
            AssertFalse(a.IsExecuting, "not executing");
        }

        public static void TestActionEvaluateScore()
        {
            var a = new AIAction_Patrol(); // Priority=10, Cost=0.2
            var ctrl = MakeController();
            Near(9.8f, a.EvaluateScore(ctrl), 0.01f, "score = priority - cost");
        }

        public static void TestActionEvaluateScoreUnavailable()
        {
            var a = new AIAction_Patrol { IsAvailable = false };
            var ctrl = MakeController();
            Near(float.MinValue, a.EvaluateScore(ctrl), 1f, "unavailable -> MinValue");
        }

        public static void TestActionExecuteAbort()
        {
            // Use a minimal test action whose Execute/Abort only toggles IsExecuting
            // (base AIAction.Execute/Abort don't touch controller fields)
            var a = new TestActionStub();
            AssertFalse(a.IsExecuting, "not executing before");
            a.Execute(null!);
            AssertTrue(a.IsExecuting, "executing after Execute");
            a.Abort(null!);
            AssertFalse(a.IsExecuting, "not executing after Abort");
        }

        private sealed class TestActionStub : AIAction
        {
            public TestActionStub() { ActionName = "TestStub"; }
            public override bool IsComplete(AIController controller) => false;
        }

        public static void TestActionCustomEvaluateCost()
        {
            var a = new AIAction_Search { SearchDuration = 15f };
            var ctrl = MakeController();
            // Default EvaluateCost returns Cost (0.4)
            Near(0.4f, a.EvaluateCost(ctrl), 1e-5f, "default cost");
        }

        // ── 内置行动 ──

        public static void TestPatrolActionDefaults()
        {
            var a = new AIAction_Patrol();
            AssertEqual("Patrol", a.ActionName, "name");
            AssertEqual(0, a.PatrolPoints.Count, "no points");
        }

        public static void TestChaseActionDefaults()
        {
            var a = new AIAction_Chase();
            AssertEqual("Chase", a.ActionName, "name");
            Near(500f, a.ChaseRadius, 1e-5f, "chase radius");
            Near(1f, a.RecalcInterval, 1e-5f, "recalc interval");
        }

        public static void TestAttackActionDefaults()
        {
            var a = new AIAction_Attack();
            AssertEqual("Attack", a.ActionName, "name");
            Near(5f, a.AttackRange, 1e-5f, "attack range");
            Near(1f, a.AttackCooldown, 1e-5f, "cooldown");
        }

        public static void TestFleeActionDefaults()
        {
            var a = new AIAction_Flee();
            AssertEqual("Flee", a.ActionName, "name");
            Near(50f, a.FleeDistance, 1e-5f, "flee distance");
            Near(0.3f, a.LowHealthThreshold, 1e-5f, "health threshold");
        }

        public static void TestSearchActionDefaults()
        {
            var a = new AIAction_Search();
            AssertEqual("Search", a.ActionName, "name");
            Near(10f, a.SearchDuration, 1e-5f, "search duration");
            Near(30f, a.SearchRadius, 1e-5f, "search radius");
        }

        public static void TestUseCoverActionDefaults()
        {
            var a = new AIAction_UseCover();
            AssertEqual("UseCover", a.ActionName, "name");
            AssertEqual(Vector3.Zero, a.CoverPosition, "default cover pos");
        }

        public static void TestHealActionDefaults()
        {
            var a = new AIAction_Heal();
            AssertEqual("Heal", a.ActionName, "name");
            Near(20f, a.HealAmount, 1e-5f, "heal amount");
            Near(2f, a.HealInterval, 1e-5f, "heal interval");
        }

        public static void TestRallyActionDefaults()
        {
            var a = new AIAction_Rally();
            AssertEqual("Rally", a.ActionName, "name");
            AssertEqual(Vector3.Zero, a.RallyPoint, "default rally point");
        }

        // ── AIDecisionMaker ──

        public static void TestDecisionMakerDefaults()
        {
            var dm = new AIDecisionMaker();
            AssertTrue(dm.CurrentAction == null, "no current action");
            AssertEqual(0, dm.GetAvailableActions().Count, "no actions");
        }

        public static void TestDecisionMakerRegisterUnregister()
        {
            var dm = new AIDecisionMaker();
            var a = new AIAction_Patrol();
            dm.RegisterAction(a);
            AssertEqual(1, dm.GetAvailableActions().Count, "1 action");
            dm.RegisterAction(a);
            AssertEqual(1, dm.GetAvailableActions().Count, "no duplicate");
            dm.UnregisterAction(a);
            AssertEqual(0, dm.GetAvailableActions().Count, "unregistered");
        }

        public static void TestDecisionMakerDecideBestAction()
        {
            var dm = new AIDecisionMaker();
            var patrol = new AIAction_Patrol(); // Priority=10, Cost=0.2 -> score=9.8
            var chase = new AIAction_Chase();   // Priority=30, Cost=0.5 -> score=29.5
            dm.RegisterAction(patrol);
            dm.RegisterAction(chase);
            var ctrl = MakeController();
            // Chase requires a known target for EvaluateAvailability
            ctrl.Memory.UpdateTarget(new Actor { Position = new Vector3(20, 0, 0) }, new Vector3(20, 0, 0), 0.7f);
            var best = dm.DecideBestAction(ctrl);
            AssertTrue(best != null, "best action found");
            AssertEqual("Chase", best!.ActionName, "chase has higher score");
        }

        public static void TestDecisionMakerNoAvailableAction()
        {
            var dm = new AIDecisionMaker();
            var flee = new AIAction_Flee(); // needs HealthPercent <= 0.3
            dm.RegisterAction(flee);
            var ctrl = MakeController();
            ctrl.HealthPercent = 1f;
            var best = dm.DecideBestAction(ctrl);
            AssertTrue(best == null, "no available action");
        }

        public static void TestDecisionMakerExecuteBestAction()
        {
            var dm = new AIDecisionMaker();
            dm.RegisterAction(new AIAction_Patrol());
            var ctrl = MakeController();
            dm.ExecuteBestAction(ctrl);
            AssertTrue(dm.CurrentAction != null, "current action set");
            AssertTrue(dm.CurrentAction!.IsExecuting, "action is executing");
        }

        public static void TestDecisionMakerOnActionChanged()
        {
            var dm = new AIDecisionMaker();
            var a = new AIAction_Patrol();
            var b = new AIAction_Chase();
            dm.RegisterAction(a);
            dm.RegisterAction(b);

            int changeCount = 0;
            dm.OnActionChanged = (_, _) => changeCount++;

            var ctrl = MakeController();
            // Register a fake closest target so chase is available
            ctrl.Memory.UpdateTarget(new Actor { Position = new Vector3(10, 0, 0) }, new Vector3(10, 0, 0), 0.8f);

            dm.ExecuteBestAction(ctrl);
            AssertTrue(dm.CurrentAction != null, "action selected");
            AssertTrue(changeCount >= 1, "OnActionChanged fired");
        }

        public static void TestDecisionMakerUpdate()
        {
            var dm = new AIDecisionMaker();
            var search = new AIAction_Search { SearchDuration = 0.5f };
            var patrol = new AIAction_Patrol();
            dm.RegisterAction(search);
            dm.RegisterAction(patrol);
            var ctrl = MakeController();
            dm.ExecuteBestAction(ctrl);
            // Search (Priority=20, Cost=0.4 -> 19.6) beats Patrol (Priority=10, Cost=0.2 -> 9.8)
            AssertTrue(dm.CurrentAction == search, "search selected first");

            // Update until search completes (SearchDuration=0.5, 5 x 0.1 = 0.5)
            for (int i = 0; i < 6; i++)
                dm.Update(ctrl, 0.1f);

            // After search's timer elapses, IsComplete returns true. DM re-evaluates and
            // picks the highest scorer. Search still scores higher than patrol, so search
            // is re-selected with a fresh Execute (its _elapsed resets in Execute).
            AssertEqual("Search", dm.CurrentAction?.ActionName, "search re-selected after completion (higher score)");
        }

        public static void TestDecisionMakerForceAction()
        {
            var dm = new AIDecisionMaker();
            var a = new AIAction_Patrol();
            dm.RegisterAction(a);
            var ctrl = MakeController();
            dm.ForceAction(a, ctrl);
            AssertTrue(dm.CurrentAction == a, "forced action");
            AssertTrue(a.IsExecuting, "forced action executing");
        }

        // ── AIMemory ──

        public static void TestMemoryDefaults()
        {
            var m = new AIMemory();
            Near(30f, m.DefaultForgetTime, 1e-5f, "default forget time");
            Near(1f, m.ThreatUpdateInterval, 1e-5f, "threat update interval");
            AssertEqual(0, m.GetKnownTargets().Count, "no known targets");
        }

        public static void TestMemoryUpdateTarget()
        {
            var m = new AIMemory();
            var target = new Actor { Position = new Vector3(5, 0, 0) };
            m.UpdateTarget(target, new Vector3(5, 0, 0), 0.8f);
            AssertEqual(1, m.GetKnownTargets().Count, "1 known target");
        }

        public static void TestMemoryGetLastKnownPosition()
        {
            var m = new AIMemory();
            var target = new Actor { Position = new Vector3(1, 2, 3) };
            m.UpdateTarget(target, new Vector3(1, 2, 3), 0.5f);
            var pos = m.GetLastKnownPosition(target);
            AssertTrue(pos.HasValue, "has position");
            AssertEqual(new Vector3(1, 2, 3), pos!.Value, "position");
        }

        public static void TestMemoryGetLastKnownPositionUnknown()
        {
            var m = new AIMemory();
            var pos = m.GetLastKnownPosition(new Actor());
            AssertTrue(pos == null, "unknown target -> null");
        }

        public static void TestMemoryGetLastKnownPositionAny()
        {
            var m = new AIMemory();
            m.UpdateTarget(new Actor(), new Vector3(1, 0, 0));
            var pos = m.GetLastKnownPosition();
            AssertTrue(pos.HasValue, "any valid target -> has position");
        }

        public static void TestMemoryGetKnownTargets()
        {
            var m = new AIMemory();
            m.UpdateTarget(new Actor(), Vector3.Zero);
            m.UpdateTarget(new Actor(), Vector3.One);
            AssertEqual(2, m.GetKnownTargets().Count, "2 targets");
        }

        public static void TestMemoryGetClosestTarget()
        {
            var m = new AIMemory();
            m.UpdateTarget(new Actor(), new Vector3(100, 0, 0));
            m.UpdateTarget(new Actor(), new Vector3(10, 0, 0));
            var closest = m.GetClosestTarget(Vector3.Zero);
            AssertTrue(closest != null, "closest found");
            NearV3(new Vector3(10, 0, 0), m.GetLastKnownPosition(closest!)!.Value, 1e-5f, "closest at (10,0,0)");
        }

        public static void TestMemoryGetHighestThreatTarget()
        {
            var m = new AIMemory();
            m.UpdateTarget(new Actor(), Vector3.Zero, 0.3f);
            m.UpdateTarget(new Actor(), Vector3.One, 0.9f);
            var threat = m.GetHighestThreatTarget();
            AssertTrue(threat != null, "threat found");
        }

        public static void TestMemoryForgetAfterTime()
        {
            var m = new AIMemory { DefaultForgetTime = 1f };
            m.UpdateTarget(new Actor(), Vector3.Zero);
            m.Update(0.5f, Vector3.Zero);
            AssertEqual(1, m.GetKnownTargets().Count, "still valid");
            m.Update(0.6f, Vector3.Zero);
            AssertEqual(0, m.GetKnownTargets().Count, "forgotten after 1.1s > 1s");
        }

        public static void TestMemoryClearAll()
        {
            var m = new AIMemory();
            m.UpdateTarget(new Actor(), Vector3.Zero);
            m.UpdateTarget(new Actor(), Vector3.One);
            m.ClearAll();
            AssertEqual(0, m.GetKnownTargets().Count, "cleared");
        }

        public static void TestMemoryOnTargetForgotten()
        {
            var m = new AIMemory { DefaultForgetTime = 0.5f };
            bool forgotten = false;
            m.OnTargetForgotten = _ => forgotten = true;
            m.UpdateTarget(new Actor(), Vector3.Zero);
            m.Update(1f, Vector3.Zero);
            AssertTrue(forgotten, "OnTargetForgotten fired");
        }

        public static void TestMemoryOnThreatChanged()
        {
            var m = new AIMemory();
            float lastThreat = 0f;
            m.OnThreatChanged = (_, threat) => lastThreat = threat;
            m.UpdateTarget(new Actor(), Vector3.Zero, 0.7f);
            Near(0.7f, lastThreat, 1e-5f, "threat callback");
        }

        // ── AICommunication ──

        public static void TestCommunicationDefaults()
        {
            var c = new AICommunication();
            Near(5f, c.MessageLifespan, 1e-5f, "lifespan");
            AssertTrue(c.OnMessageReceived == null, "no callback");
        }

        public static void TestCommunicationRegisterMember()
        {
            var c = new AICommunication();
            var ctrl = MakeController();
            c.RegisterMember(AITeamId.TeamA, ctrl);
            var msg = new AICommunication.AIMessage { Type = AICommunication.MessageType.EnemySpotted };
            int received = DeliverBroadcast(c, AITeamId.TeamA, msg);
            AssertTrue(received > 0, "member received broadcast");
        }

        public static void TestCommunicationUnregisterMember()
        {
            var c = new AICommunication();
            var ctrl = MakeController();
            c.RegisterMember(AITeamId.TeamA, ctrl);
            c.UnregisterMember(AITeamId.TeamA, ctrl);
            var msg = new AICommunication.AIMessage { Type = AICommunication.MessageType.EnemySpotted };
            int received = DeliverBroadcast(c, AITeamId.TeamA, msg);
            AssertEqual(0, received, "no member to receive");
        }

        public static void TestCommunicationBroadcastToTeam()
        {
            var c = new AICommunication();
            var ctrl1 = MakeController();
            var ctrl2 = MakeController();
            c.RegisterMember(AITeamId.TeamA, ctrl1);
            c.RegisterMember(AITeamId.TeamA, ctrl2);

            int ctrl1Received = 0, ctrl2Received = 0;
            c.OnMessageReceived = (member, _) =>
            {
                if (member == ctrl1) ctrl1Received++;
                if (member == ctrl2) ctrl2Received++;
            };
            var msg = new AICommunication.AIMessage { Sender = ctrl1 };
            c.Broadcast(AITeamId.TeamA, msg);
            c.ProcessMessages(0f);
            AssertEqual(0, ctrl1Received, "sender doesn't receive own");
            AssertEqual(1, ctrl2Received, "ctrl2 received");
        }

        public static void TestCommunicationMessageExpiry()
        {
            var c = new AICommunication { MessageLifespan = 1f };
            var ctrl = MakeController();
            c.RegisterMember(AITeamId.TeamA, ctrl);
            var msg = new AICommunication.AIMessage { Timestamp = 10f };
            // ProcessMessages(100f) - well past expiry (10+1=11 < 100)
            int received = 0;
            c.OnMessageReceived = (_, _) => received++;
            c.Broadcast(AITeamId.TeamA, msg);
            c.ProcessMessages(100f);
            AssertEqual(0, received, "expired message not delivered");
        }

        public static void TestCommunicationAIMessageDefaults()
        {
            var msg = new AICommunication.AIMessage();
            AssertEqual(AICommunication.MessageType.None, msg.Type, "default type");
            AssertTrue(msg.Sender == null, "no sender");
            AssertTrue(msg.Target == null, "no target");
            AssertEqual(Vector3.Zero, msg.Position, "no position");
            Near(0f, msg.Timestamp, 1e-5f, "no timestamp");
            AssertEqual(0, msg.Data.Count, "no data");
        }

        // ── AIController ──

        public static void TestControllerDefaults()
        {
            var ctrl = MakeController(new Vector3(3, 4, 5));
            AssertEqual(AIStateType.Idle, ctrl.CurrentStateType, "default state");
            AssertTrue(ctrl.NavAgent == null, "no nav agent");
            AssertTrue(ctrl.CurrentBehaviorTree == null, "no behavior tree");
            Near(1f, ctrl.HealthPercent, 1e-5f, "full health");
            AssertEqual(AITeamId.None, ctrl.TeamId, "no team");
            NearV3(new Vector3(3, 4, 5), ctrl.Position, 1e-5f, "position from pawn");
        }

        public static void TestControllerDefaultStatesRegistered()
        {
            var ctrl = MakeController();
            // After Possess, default states are registered
            // Verify via forcing to Patrol (a default state)
            ctrl.ForceState(AIStateType.Patrol);
            AssertEqual(AIStateType.Patrol, ctrl.CurrentStateType, "patrol state registered");
        }

        public static void TestControllerDefaultActionsRegistered()
        {
            var ctrl = MakeController();
            // After Possess, default actions are registered in DecisionMaker
            AssertTrue(ctrl.DecisionMaker.GetAvailableActions().Count > 0, "default actions registered");
        }

        public static void TestControllerTeamRegistration()
        {
            var pawn = new Pawn { Position = Vector3.Zero };
            var ctrl = new AIController();
            var comm = new AICommunication();
            ctrl.TeamId = AITeamId.TeamA;
            ctrl.Communication = comm;
            ctrl.Possess(pawn);
            // ctrl should be registered in comm
            var msg = new AICommunication.AIMessage { Type = AICommunication.MessageType.EnemySpotted };
            int received = DeliverBroadcast(comm, AITeamId.TeamA, msg);
            AssertTrue(received > 0, "controller registered in team communication");
        }

        public static void TestControllerForceState()
        {
            var ctrl = MakeController();
            ctrl.ForceState(AIStateType.Chase);
            AssertEqual(AIStateType.Chase, ctrl.CurrentStateType, "chase state");
        }

        public static void TestControllerOnStateChanged()
        {
            var ctrl = MakeController();
            AIStateType lastState = AIStateType.Idle;
            ctrl.OnStateChanged = (from, to) => lastState = to;
            ctrl.ForceState(AIStateType.Attack);
            AssertEqual(AIStateType.Attack, lastState, "state changed callback");
        }
    }
}
