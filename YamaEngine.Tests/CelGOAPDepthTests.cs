using System;
using System.Collections.Generic;
using System.Linq;
using YamaEngine.AI;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    /// <summary>
    /// GOAP 规划器（A* 状态空间搜索 + 执行器 + 记忆 + Domain Builder）深度测试。
    /// 对齐 F.E.A.R. GOAP / Orkin GDC 2005 / UE5 Gameplay Ability System。
    /// </summary>
    public static class CelGOAPDepthTests
    {
        private static void Near(float want, float got, float tol, string msg)
        {
            if (MathF.Abs(want - got) > tol) throw new Exception($"{msg}: want {want}, got {got}");
        }
        private static void AssertFalse(bool cond, string msg) { if (cond) throw new Exception(msg); }

        // ── WorldState ──

        public static void TestWSDefaults()
        {
            var ws = new WorldState();
            AssertEqual(0, ws.Count, "empty count");
            AssertFalse(ws.GetBool("b"), "default bool false");
            AssertEqual(0, ws.GetInt("i"), "default int");
            Near(0f, ws.GetFloat("f"), 1e-5f, "default float");
            AssertEqual("", ws.GetString("s"), "default string");
        }

        public static void TestWSAccessors()
        {
            var ws = new WorldState();
            ws.SetBool("b", true); AssertTrue(ws.GetBool("b"), "bool");
            ws.SetInt("i", 42); AssertEqual(42, ws.GetInt("i"), "int");
            ws.SetFloat("f", 3.14f); Near(3.14f, ws.GetFloat("f"), 1e-4f, "float");
            ws.SetString("s", "hi"); AssertEqual("hi", ws.GetString("s"), "str");
            AssertEqual(4, ws.Count, "count all types");
        }

        public static void TestWSHasKey()
        {
            var ws = new WorldState();
            AssertFalse(ws.HasBool("b"), "no bool");
            ws.SetBool("b", false); // 设置为 false 仍算 Has
            AssertTrue(ws.HasBool("b"), "has bool even false");
            AssertFalse(ws.HasInt("i"), "no int");
            ws.SetInt("i", 0);
            AssertTrue(ws.HasInt("i"), "has int even 0");
        }

        public static void TestWSClone()
        {
            var ws = new WorldState();
            ws.SetInt("hp", 100);
            var c = ws.Clone();
            c.SetInt("hp", 50);
            c.SetBool("dead", true);
            AssertEqual(100, ws.GetInt("hp"), "original unchanged");
            AssertFalse(ws.HasBool("dead"), "original has no dead");
            AssertEqual(50, c.GetInt("hp"), "clone changed");
        }

        public static void TestWSDistanceTo()
        {
            var a = new WorldState();
            a.SetBool("x", true); a.SetBool("y", false); a.SetInt("hp", 100);
            var b = new WorldState();
            b.SetBool("x", true); b.SetBool("y", true); b.SetInt("hp", 50);
            // x 相同，y 不同(1)，hp 不同(1) → 距离 2
            AssertEqual(2, a.DistanceTo(b), "distance = 2");
        }

        public static void TestWSSatisfies()
        {
            var current = new WorldState();
            current.SetBool("has_weapon", true); current.SetInt("hp", 100);
            var desired = new WorldState();
            desired.SetBool("has_weapon", true);
            AssertTrue(current.Satisfies(desired), "current satisfies desired");
            var desired2 = new WorldState();
            desired2.SetInt("hp", 50); // current hp=100 != 50
            AssertFalse(current.Satisfies(desired2), "hp mismatch");
        }

        public static void TestWSApplyEffects()
        {
            var ws = new WorldState();
            ws.SetInt("ammo", 10); ws.SetBool("alive", true);
            var effects = new WorldState();
            effects.SetInt("ammo", 9); effects.SetBool("shot_fired", true);
            var result = ws.ApplyEffects(effects);
            AssertEqual(9, result.GetInt("ammo"), "ammo decremented");
            AssertTrue(result.GetBool("shot_fired"), "new key added");
            AssertTrue(result.GetBool("alive"), "old key preserved");
            AssertEqual(10, ws.GetInt("ammo"), "original unchanged");
        }

        public static void TestWSFingerprint()
        {
            var a = new WorldState();
            a.SetBool("x", true); a.SetInt("y", 5);
            var b = new WorldState();
            b.SetBool("x", true); b.SetInt("y", 5);
            AssertEqual(a.Fingerprint(), b.Fingerprint(), "identical states same fingerprint");
            var c = new WorldState();
            c.SetBool("x", false); c.SetInt("y", 5);
            AssertFalse(a.Fingerprint() == c.Fingerprint(), "different states different fingerprint");
        }

        // ── GOAPActionV2 ──

        public static void TestActionDefaults()
        {
            var a = new GOAPActionV2("Test");
            AssertEqual("Test", a.Name, "name");
            Near(1f, a.BaseCost, 1e-5f, "default cost 1");
            AssertTrue(a.Interruptible, "interruptible by default");
            Near(1f, a.EstimatedDuration, 1e-5f, "default duration 1s");
            AssertEqual(0, a.RequiredTags.Length, "no tags");
        }

        public static void TestActionPreconditionsMet()
        {
            var a = new GOAPActionV2("Attack");
            a.Preconditions.SetBool("near_target", true);
            var ws = new WorldState();
            AssertFalse(a.PreconditionsMet(ws), "precondition not met");
            ws.SetBool("near_target", true);
            AssertTrue(a.PreconditionsMet(ws), "precondition met");
        }

        public static void TestActionCanExecuteWithValidator()
        {
            var a = new GOAPActionV2("Shoot");
            a.Validator = ctx => ctx.WorldState.GetInt("ammo") > 0;
            var ctx = new GOAPContext(1, new WorldState());
            ctx.WorldState.SetInt("ammo", 0);
            AssertFalse(a.CanExecute(ctx), "no ammo, can't execute");
            ctx.WorldState.SetInt("ammo", 5);
            AssertTrue(a.CanExecute(ctx), "has ammo");
        }

        public static void TestActionGetCostWithMultiplier()
        {
            var a = new GOAPActionV2("Move");
            a.BaseCost = 2f;
            a.CostMultiplier = ctx => ctx.WorldState.GetFloat("urgency");
            var ctx = new GOAPContext(1, new WorldState());
            ctx.WorldState.SetFloat("urgency", 3f);
            Near(6f, a.GetCost(ctx), 1e-5f, "cost = base * mult");
        }

        public static void TestActionExecute()
        {
            bool executed = false;
            var a = new GOAPActionV2("DoThing");
            a.Executor = ctx => { executed = true; return true; };
            var ctx = new GOAPContext(1, new WorldState());
            AssertTrue(a.Execute(ctx), "execute returns true");
            AssertTrue(executed, "executor ran");
        }

        public static void TestActionNoExecutorDefaultsTrue()
        {
            var a = new GOAPActionV2("Passive");
            var ctx = new GOAPContext(1, new WorldState());
            AssertTrue(a.Execute(ctx), "null executor defaults true");
        }

        // ── GOAPGoalV2 ──

        public static void TestGoalDefaults()
        {
            var g = new GOAPGoalV2("KillEnemy");
            AssertEqual("KillEnemy", g.Name, "name");
            Near(1f, g.Priority, 1e-5f, "default priority");
            Near(0f, g.CoolDown, 1e-5f, "no cooldown");
        }

        public static void TestGoalDynamicPriority()
        {
            var g = new GOAPGoalV2("Flee");
            g.DynamicPriority = ctx => ctx.WorldState.GetFloat("danger") * 10f;
            var ctx = new GOAPContext(1, new WorldState());
            ctx.WorldState.SetFloat("danger", 0.8f);
            Near(8f, g.GetPriority(ctx), 1e-4f, "dynamic priority");
        }

        public static void TestGoalInvalidWhenAlreadyAchieved()
        {
            var g = new GOAPGoalV2("BeSafe");
            g.DesiredState.SetBool("safe", true);
            var ctx = new GOAPContext(1, new WorldState());
            // 已经安全了 → 目标无效
            ctx.WorldState.SetBool("safe", true);
            AssertFalse(g.IsValid(ctx, 0f), "goal achieved, invalid");
            ctx.WorldState.SetBool("safe", false);
            AssertTrue(g.IsValid(ctx, 0f), "not yet safe, valid");
        }

        public static void TestGoalCoolDown()
        {
            var g = new GOAPGoalV2("Attack");
            g.CoolDown = 2f;
            g.DesiredState.SetBool("enemy_dead", true); // 非空目标避免"已达成"短路
            var ctx = new GOAPContext(1, new WorldState());
            // 首次有效（_lastSelectedTime=-1e9）
            AssertTrue(g.IsValid(ctx, 0f), "first time valid");
            g.MarkSelected(0f);
            AssertFalse(g.IsValid(ctx, 1f), "in cooldown at t=1");
            AssertTrue(g.IsValid(ctx, 3f), "cooldown expired at t=3");
        }

        public static void TestGoalIsValidFunc()
        {
            var g = new GOAPGoalV2("Reload");
            g.IsValidFunc = ctx => ctx.WorldState.GetInt("ammo_reserve") > 0;
            g.DesiredState.SetBool("reloaded", true); // 非空目标
            var ctx = new GOAPContext(1, new WorldState());
            AssertFalse(g.IsValid(ctx, 0f), "no reserve, invalid");
            ctx.WorldState.SetInt("ammo_reserve", 30);
            AssertTrue(g.IsValid(ctx, 0f), "has reserve");
        }

        // ── GOAPContext ──

        public static void TestContextMemory()
        {
            var ctx = new GOAPContext(7, new WorldState());
            AssertEqual(7, ctx.AgentId, "agent id");
            AssertTrue(ctx.Memory.Count == 0, "empty memory");
            ctx.SetMemory("target", "player_01");
            var got = ctx.GetMemory<string>("target");
            AssertEqual("player_01", got!, "get memory string");
            AssertTrue(ctx.GetMemory<string>("missing") == null, "missing key null");
        }

        // ── GOAPPlannerV2 ──

        public static void TestPlannerDefaults()
        {
            var p = new GOAPPlannerV2();
            AssertEqual(12, p.MaxDepth, "max depth 12");
            AssertEqual(2000, p.MaxNodes, "max nodes 2000");
            Near(1f, p.HeuristicWeight, 1e-5f, "heuristic weight 1");
            AssertEqual(0, p.LastPlanNodesExpanded, "no nodes expanded yet");
        }

        public static void TestPlannerRegisterRemoveActions()
        {
            var p = new GOAPPlannerV2();
            p.RegisterAction(new GOAPActionV2("A"));
            p.RegisterAction(new GOAPActionV2("B"));
            p.RemoveAction("A");
            p.ClearActions();
        }

        public static void TestPlannerSimplePlan()
        {
            var p = new GOAPPlannerV2();
            // 动作：开门（前置：near_door + not locked → 效果：door_open）
            var openDoor = new GOAPActionV2("OpenDoor");
            openDoor.Preconditions.SetBool("near_door", true);
            openDoor.Preconditions.SetBool("door_locked", false);
            openDoor.Effects.SetBool("door_open", true);
            openDoor.BaseCost = 1f;
            p.RegisterAction(openDoor);

            var goal = new GOAPGoalV2("EnterRoom");
            goal.DesiredState.SetBool("door_open", true);

            var ctx = new GOAPContext(1, new WorldState());
            ctx.WorldState.SetBool("near_door", true);
            ctx.WorldState.SetBool("door_locked", false);

            var plan = p.Plan(goal, ctx);
            AssertTrue(plan != null, "plan found");
            AssertEqual(1, plan!.Count, "1 action plan");
            AssertEqual("OpenDoor", plan[0].Name, "plan is OpenDoor");
        }

        public static void TestPlannerMultiStepPlan()
        {
            var p = new GOAPPlannerV2();
            // 动作 1：移动到门（无前置 → near_door）
            var move = new GOAPActionV2("MoveToDoor");
            move.Effects.SetBool("near_door", true);
            move.BaseCost = 2f;
            p.RegisterAction(move);
            // 动作 2：开门
            var open = new GOAPActionV2("OpenDoor");
            open.Preconditions.SetBool("near_door", true);
            open.Effects.SetBool("door_open", true);
            open.BaseCost = 1f;
            p.RegisterAction(open);

            var goal = new GOAPGoalV2("Enter");
            goal.DesiredState.SetBool("door_open", true);

            var ctx = new GOAPContext(1, new WorldState()); // 起始状态：啥都没有
            var plan = p.Plan(goal, ctx);
            AssertTrue(plan != null, "plan found");
            AssertEqual(2, plan!.Count, "2-step plan");
            AssertEqual("MoveToDoor", plan[0].Name, "step 1 MoveToDoor");
            AssertEqual("OpenDoor", plan[1].Name, "step 2 OpenDoor");
        }

        public static void TestPlannerNoPlanWhenImpossible()
        {
            var p = new GOAPPlannerV2();
            var goal = new GOAPGoalV2("Impossible");
            goal.DesiredState.SetBool("flying", true);
            var ctx = new GOAPContext(1, new WorldState());
            var plan = p.Plan(goal, ctx);
            AssertTrue(plan == null, "no plan possible");
        }

        public static void TestPlannerPrefersLowCost()
        {
            var p = new GOAPPlannerV2();
            // 两条路都能达成目标，一条便宜一条贵
            var cheap = new GOAPActionV2("Cheap");
            cheap.Effects.SetBool("goal_done", true);
            cheap.BaseCost = 1f;
            var expensive = new GOAPActionV2("Expensive");
            expensive.Effects.SetBool("goal_done", true);
            expensive.BaseCost = 10f;
            p.RegisterAction(cheap);
            p.RegisterAction(expensive);

            var goal = new GOAPGoalV2("Do");
            goal.DesiredState.SetBool("goal_done", true);
            var ctx = new GOAPContext(1, new WorldState());
            var plan = p.Plan(goal, ctx);
            AssertTrue(plan != null && plan.Count > 0, "plan found");
            AssertEqual("Cheap", plan[0].Name, "prefers low cost");
        }

        public static void TestPlannerPickGoalHighestPriority()
        {
            var p = new GOAPPlannerV2();
            var low = new GOAPGoalV2("Low") { Priority = 1f };
            low.DesiredState.SetBool("low_done", true);
            var high = new GOAPGoalV2("High") { Priority = 10f };
            high.DesiredState.SetBool("high_done", true);
            var goals = new List<GOAPGoalV2> { low, high };
            var ctx = new GOAPContext(1, new WorldState());
            var picked = p.PickGoal(goals, ctx);
            AssertTrue(picked != null, "picked a goal");
            AssertEqual("High", picked!.Name, "picks highest priority");
        }

        public static void TestPlannerPickGoalSkipsInvalid()
        {
            var p = new GOAPPlannerV2();
            var g1 = new GOAPGoalV2("G1") { Priority = 100f };
            g1.DesiredState.SetBool("x", true);
            g1.IsValidFunc = ctx => false; // 永远无效
            var g2 = new GOAPGoalV2("G2") { Priority = 1f };
            g2.DesiredState.SetBool("y", true);
            var goals = new List<GOAPGoalV2> { g1, g2 };
            var ctx = new GOAPContext(1, new WorldState());
            var picked = p.PickGoal(goals, ctx);
            AssertTrue(picked != null, "picked fallback");
            AssertEqual("G2", picked!.Name, "skips invalid, picks G2");
        }

        public static void TestPlannerStats()
        {
            var p = new GOAPPlannerV2();
            var move = new GOAPActionV2("Move");
            move.Effects.SetBool("arrived", true);
            p.RegisterAction(move);
            var goal = new GOAPGoalV2("Go");
            goal.DesiredState.SetBool("arrived", true);
            var ctx = new GOAPContext(1, new WorldState());
            p.Plan(goal, ctx);
            AssertTrue(p.LastPlanNodesExpanded > 0, "nodes expanded tracked");
            AssertTrue(p.LastPlanNodesGenerated > 0, "nodes generated tracked");
        }

        // ── GOAPExecutor ──

        public static void TestExecutorDefaults()
        {
            var planner = new GOAPPlannerV2();
            var goals = new List<GOAPGoalV2>();
            var ex = new GOAPExecutor(planner, goals);
            AssertEqual(GOAPExecState.Idle, ex.State, "starts idle");
            AssertFalse(ex.HasPlan, "no plan");
            AssertTrue(ex.CurrentGoal == null, "no goal");
            Near(0.5f, ex.ReplanCooldown, 1e-5f, "default replan cooldown");
            AssertEqual(3, ex.MaxReplanAttempts, "max replan attempts");
            AssertTrue(ex.AutoReplan, "auto replan on");
        }

        public static void TestExecutorPlansAndExecutes()
        {
            var planner = new GOAPPlannerV2();
            var action = new GOAPActionV2("Act")
            {
                BaseCost = 1f,
                EstimatedDuration = 0.1f,
            };
            action.Effects.SetBool("done", true);
            planner.RegisterAction(action);
            var goal = new GOAPGoalV2("Do");
            goal.DesiredState.SetBool("done", true);
            var goals = new List<GOAPGoalV2> { goal };
            var ex = new GOAPExecutor(planner, goals);
            var ctx = new GOAPContext(1, new WorldState());
            ex.SetContext(ctx);

            var states = new List<GOAPExecState>();
            ex.OnStateChanged += s => states.Add(s);
            ex.OnActionStarted += a => { };

            // Update 足够多次让执行完成
            for (int i = 0; i < 10; i++) ex.Update(0.05f);
            AssertTrue(states.Contains(GOAPExecState.Executing), "entered executing");
            AssertTrue(ctx.WorldState.GetBool("done"), "effect applied");
        }

        public static void TestExecutorFailsWhenNoPlan()
        {
            var planner = new GOAPPlannerV2();
            var goal = new GOAPGoalV2("Impossible");
            goal.DesiredState.SetBool("impossible", true);
            var goals = new List<GOAPGoalV2> { goal };
            var ex = new GOAPExecutor(planner, goals);
            var ctx = new GOAPContext(1, new WorldState());
            ex.SetContext(ctx);
            ex.Update(0.016f);
            AssertEqual(GOAPExecState.Failed, ex.State, "no plan → Failed");
        }

        public static void TestExecutorPlanProgress()
        {
            var planner = new GOAPPlannerV2();
            var a1 = new GOAPActionV2("Step1") { EstimatedDuration = 0.1f };
            a1.Effects.SetBool("s1", true);
            var a2 = new GOAPActionV2("Step2") { EstimatedDuration = 0.1f };
            a2.Effects.SetBool("s2", true);
            planner.RegisterAction(a1); planner.RegisterAction(a2);
            var goal = new GOAPGoalV2("Both");
            goal.DesiredState.SetBool("s2", true);
            var ex = new GOAPExecutor(planner, new List<GOAPGoalV2> { goal });
            var ctx = new GOAPContext(1, new WorldState());
            ex.SetContext(ctx);
            ex.Update(0.016f); // plan + start first action
            AssertTrue(ex.HasPlan || ex.CurrentAction != null, "has work");
            AssertTrue(ex.PlanProgress >= 0f && ex.PlanProgress <= 1f, "progress in [0,1]");
        }

        public static void TestExecutorForceReplan()
        {
            var planner = new GOAPPlannerV2();
            var a = new GOAPActionV2("A") { EstimatedDuration = 10f };
            a.Effects.SetBool("done", true);
            planner.RegisterAction(a);
            var goal = new GOAPGoalV2("G");
            goal.DesiredState.SetBool("done", true);
            var ex = new GOAPExecutor(planner, new List<GOAPGoalV2> { goal });
            var ctx = new GOAPContext(1, new WorldState());
            ex.SetContext(ctx);
            ex.Update(0.016f);
            AssertEqual(GOAPExecState.Executing, ex.State, "executing");
            ex.ForceReplan();
            // 重规划后仍在执行（因为 plan 不变）
            AssertEqual(GOAPExecState.Executing, ex.State, "still executing after replan");
        }

        public static void TestExecutorAbort()
        {
            var planner = new GOAPPlannerV2();
            var a = new GOAPActionV2("Long") { EstimatedDuration = 100f };
            a.Effects.SetBool("done", true);
            planner.RegisterAction(a);
            var goal = new GOAPGoalV2("G");
            goal.DesiredState.SetBool("done", true);
            var ex = new GOAPExecutor(planner, new List<GOAPGoalV2> { goal });
            var ctx = new GOAPContext(1, new WorldState());
            ex.SetContext(ctx);
            ex.Update(0.016f);
            ex.Abort();
            AssertEqual(GOAPExecState.Idle, ex.State, "back to idle after abort");
            AssertFalse(ex.HasPlan, "plan cleared");
        }

        public static void TestExecutorOnActionEvents()
        {
            var planner = new GOAPPlannerV2();
            var a = new GOAPActionV2("Quick") { EstimatedDuration = 0.05f };
            a.Effects.SetBool("done", true);
            planner.RegisterAction(a);
            var goal = new GOAPGoalV2("G");
            goal.DesiredState.SetBool("done", true);
            var ex = new GOAPExecutor(planner, new List<GOAPGoalV2> { goal });
            var ctx = new GOAPContext(1, new WorldState());
            ex.SetContext(ctx);
            var started = new List<string>();
            var finished = new List<(string, bool)>();
            ex.OnActionStarted += a2 => started.Add(a2.Name);
            ex.OnActionFinished += (a2, success) => finished.Add((a2.Name, success));
            for (int i = 0; i < 5; i++) ex.Update(0.05f);
            AssertTrue(started.Contains("Quick"), "action started event");
            AssertTrue(finished.Any(f => f.Item1 == "Quick" && f.Item2), "action finished successfully");
        }

        // ── GOAPMemory ──

        public static void TestMemoryCachePlan()
        {
            var mem = new GOAPMemory();
            Near(2f, mem.PlanCacheTTL, 1e-5f, "default TTL 2s");
            var plan = new List<GOAPActionV2> { new("A"), new("B") };
            mem.CachePlan("goal1", plan, 0f);
            var cached = mem.TryGetCachedPlan("goal1", 1f);
            AssertTrue(cached != null, "cached plan retrieved");
            AssertEqual(2, cached!.Count, "2 actions");
            var expired = mem.TryGetCachedPlan("goal1", 3f);
            AssertTrue(expired == null, "plan expired after TTL");
        }

        public static void TestMemoryRecordSeen()
        {
            var mem = new GOAPMemory();
            mem.RecordSeen("player", 10f);
            Near(5f, mem.TimeSinceSeen("player", 15f), 1e-4f, "seen 5s ago");
            Near(float.MaxValue, mem.TimeSinceSeen("unknown", 15f), 0.01f, "never seen = max");
            mem.Clear();
            Near(float.MaxValue, mem.TimeSinceSeen("player", 15f), 0.01f, "cleared");
        }

        // ── GOAPDomainBuilder ──

        public static void TestBuilderActionAndGoal()
        {
            var (planner, goals) = new GOAPDomainBuilder()
                .Action("MoveTo", 2f, ws => ws.SetBool("has_target", true), ws => ws.SetBool("near_target", true))
                .Action("Attack", 1f, ws => ws.SetBool("near_target", true), ws => ws.SetBool("target_dead", true))
                .Goal("Kill", 5f, ws => ws.SetBool("target_dead", true))
                .Build();
            AssertTrue(planner != null, "planner built");
            AssertEqual(1, goals.Count, "1 goal");
            AssertEqual("Kill", goals[0].Name, "goal name");
            // 用 builder 构建的 domain 能规划
            var ctx = new GOAPContext(1, new WorldState());
            ctx.WorldState.SetBool("has_target", true);
            var plan = planner.Plan(goals[0], ctx);
            AssertTrue(plan != null, "plan found via builder");
            AssertEqual(2, plan!.Count, "2-step plan");
        }

        public static void TestBuilderDynamicGoal()
        {
            var (planner, goals) = new GOAPDomainBuilder()
                .GoalDynamic("Flee", ctx => ctx.WorldState.GetFloat("danger") * 100f, ws => ws.SetBool("safe", true))
                .Build();
            AssertEqual(1, goals.Count, "1 dynamic goal");
            AssertTrue(goals[0].DynamicPriority != null, "has dynamic priority fn");
        }

        // ── GOAPActionPresets ──

        public static void TestPresetMoveTo()
        {
            var a = GOAPActionPresets.MoveTo("player");
            AssertTrue(a.Name.Contains("MoveTo"), "name contains MoveTo");
            Near(2f, a.BaseCost, 1e-5f, "cost 2");
            AssertTrue(a.Effects.GetBool("near_player"), "effect near_player");
        }

        public static void TestPresetAttack()
        {
            var a = GOAPActionPresets.Attack("enemy");
            AssertTrue(a.Preconditions.GetBool("near_enemy"), "precond near_enemy");
            AssertTrue(a.Preconditions.GetBool("has_weapon"), "precond has_weapon");
            AssertTrue(a.Effects.GetBool("target_dead"), "effect target_dead");
        }

        public static void TestPresetReload()
        {
            var a = GOAPActionPresets.Reload();
            AssertTrue(a.Preconditions.GetBool("has_ammo_reserve"), "precond reserve");
            Near(30f, a.Effects.GetFloat("ammo"), 1e-4f, "effect ammo 30");
        }

        public static void TestPresetPickup()
        {
            var a = GOAPActionPresets.Pickup("key");
            AssertTrue(a.Preconditions.GetBool("near_key"), "precond near");
            AssertTrue(a.Effects.GetBool("has_key"), "effect has_key");
        }

        public static void TestPresetFlee()
        {
            var a = GOAPActionPresets.Flee("threat");
            AssertTrue(a.Preconditions.GetBool("threat_visible"), "precond threat");
            AssertTrue(a.Effects.GetBool("safe"), "effect safe");
        }

        public static void TestPresetOpenDoor()
        {
            var a = GOAPActionPresets.OpenDoor("door1");
            AssertTrue(a.Preconditions.GetBool("near_door1"), "precond near");
            AssertTrue(a.Preconditions.GetBool("door_locked") == false, "precond not locked");
            AssertTrue(a.Effects.GetBool("door_open"), "effect open");
        }

        // ── 集成测试：完整 GOAP 循环 ──

        public static void TestIntegrationFullCycle()
        {
            var (planner, goals) = new GOAPDomainBuilder()
                .Action("PickupWeapon", 1f,
                    ws => { },
                    ws => ws.SetBool("has_weapon", true))
                .Action("MoveToEnemy", 2f,
                    ws => ws.SetBool("has_weapon", true),
                    ws => ws.SetBool("near_enemy", true))
                .Action("Attack", 1f,
                    ws => { ws.SetBool("near_enemy", true); ws.SetBool("has_weapon", true); },
                    ws => ws.SetBool("enemy_dead", true))
                .Goal("Kill", 10f, ws => ws.SetBool("enemy_dead", true))
                .Build();

            var ex = new GOAPExecutor(planner, goals);
            var ctx = new GOAPContext(1, new WorldState());
            // 起始：啥都没有
            ex.SetContext(ctx);
            var finishedOk = false;
            ex.OnActionFinished += (_, success) => { if (success) finishedOk = true; };
            // 跑足够多 tick
            for (int i = 0; i < 50 && !ctx.WorldState.GetBool("enemy_dead"); i++)
                ex.Update(0.5f);
            AssertTrue(ctx.WorldState.GetBool("enemy_dead"), "enemy killed");
            AssertTrue(finishedOk, "actions finished successfully");
            // 验证 action 顺序：武器→移动→攻击
            AssertTrue(ctx.WorldState.GetBool("has_weapon"), "weapon acquired");
            AssertTrue(ctx.WorldState.GetBool("near_enemy"), "moved to enemy");
        }
    }
}
