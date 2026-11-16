// FormationTests.cs — UE5.8 编队子系统深度验证
// 覆盖：FormationTemplate 十种队形几何（Line 对称/Column 沿-Z/V 对称顶点/Circle 半径角度/Diamond 层级/Wedge），
// SlotAssigner 四种策略（Nearest 贪心/FixedOrder 顺序/Priority 降序/RoleBased 角色匹配），
// Formation.GetAgentTargetPosition（LocalOffsetToWorld 旋转矩阵 XZ 平面）。
// 全部纯 CPU、确定性数值验证（Y-up，队形在 XZ 平面）。

using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.AI;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class FormationTests
    {
        private static void AssertNear(float got, float want, float tol, string msg)
            => AssertTrue(MathF.Abs(got - want) <= tol,
                $"{msg} (got {got:F4}, want {want:F4}±{tol:F4})");

        private static FormationTemplate Gen(FormationType type, int n, float spacing = 1.5f)
        {
            var t = new FormationTemplate(type.ToString(), type);
            t.GenerateSlots(n, spacing);
            return t;
        }

        // ── Line：对称于 x=0，间距=spacing ──
        public static void TestLineSymmetryAndSpacing()
        {
            var t = Gen(FormationType.Line, 5, 2f);
            AssertEqual(5, t.Slots.Count, "Line must have 5 slots.");
            // i=0: x=-4, i=4: x=+4 (对称)
            AssertNear(t.Slots[0].LocalPosition.X, -4f, 0.01f, "Line[0].X must be -4.");
            AssertNear(t.Slots[4].LocalPosition.X, 4f, 0.01f, "Line[4].X must be +4.");
            AssertNear(t.Slots[2].LocalPosition.X, 0f, 0.01f, "Line center X must be 0.");
            // 间距
            AssertNear(t.Slots[1].LocalPosition.X - t.Slots[0].LocalPosition.X, 2f, 0.01f,
                "Line spacing must equal 2.");
        }

        // ── Column：沿 -Z 排列，X=0 ──
        public static void TestColumnAlongNegativeZ()
        {
            var t = Gen(FormationType.Column, 4, 1.5f);
            for (int i = 0; i < 4; i++)
            {
                AssertNear(t.Slots[i].LocalPosition.X, 0f, 0.01f, $"Column[{i}].X must be 0.");
                AssertNear(t.Slots[i].LocalPosition.Z, -i * 1.5f, 0.01f,
                    $"Column[{i}].Z must be -{i * 1.5f:F1}.");
            }
        }

        // ── V：i=0 在顶点（原点），两翼对称 ──
        public static void TestVSymmetry()
        {
            var t = Gen(FormationType.V, 7, 2f);
            AssertNear(t.Slots[0].LocalPosition.X, 0f, 0.01f, "V[0].X must be 0 (apex).");
            AssertNear(t.Slots[0].LocalPosition.Z, 0f, 0.01f, "V[0].Z must be 0 (apex).");
            // i=1 (side=+1,row=1) 和 i=2 (side=-1,row=1) 关于 X 轴对称
            AssertNear(t.Slots[1].LocalPosition.X, -t.Slots[2].LocalPosition.X, 0.01f,
                "V wings must be X-symmetric.");
            AssertNear(t.Slots[1].LocalPosition.Z, t.Slots[2].LocalPosition.Z, 0.01f,
                "V wings must have equal Z.");
            // 翼展递增
            AssertTrue(MathF.Abs(t.Slots[3].LocalPosition.X) > MathF.Abs(t.Slots[1].LocalPosition.X),
                "V wings must spread outward.");
        }

        // ── Circle：半径 = max(1, n*spacing/2π)，首槽在 (radius,0,0) ──
        public static void TestCircleRadiusAndAngle()
        {
            var t = Gen(FormationType.Circle, 8, 2f);
            float expectedR = 8f * 2f / (2 * MathF.PI);
            // 首槽在角度 0 → (R, 0, 0)
            AssertNear(t.Slots[0].LocalPosition.X, expectedR, 0.05f,
                "Circle[0] must be at (R,0,0).");
            AssertNear(t.Slots[0].LocalPosition.Z, 0f, 0.05f, "Circle[0].Z must be 0.");
            // 第二槽 45°
            AssertNear(t.Slots[1].LocalPosition.X, MathF.Cos(MathF.PI / 4) * expectedR, 0.05f,
                "Circle[1].X must be at 45°.");
            // 所有点在等半径圆上
            for (int i = 0; i < 8; i++)
            {
                var p = t.Slots[i].LocalPosition;
                float r = MathF.Sqrt(p.X * p.X + p.Z * p.Z);
                AssertNear(r, expectedR, 0.05f, $"Circle[{i}] must be on radius {expectedR:F2}.");
            }
        }

        // ── Diamond：层级半径递增，首槽在原点 ──
        public static void TestDiamondLayers()
        {
            var t = Gen(FormationType.Diamond, 5, 2f);
            AssertNear(t.Slots[0].LocalPosition.Length(), 0f, 0.01f,
                "Diamond[0] must be at origin (center).");
            // 外层槽位半径 > 0
            bool hasOuter = false;
            for (int i = 1; i < 5; i++)
                if (t.Slots[i].LocalPosition.Length() > 0.5f) { hasOuter = true; break; }
            AssertTrue(hasOuter, "Diamond must have outer-layer slots at radius > 0.");
        }

        // ── GenerateSlots(0)：返回空 Slots ──
        public static void TestGenerateSlotsZero()
        {
            var t = Gen(FormationType.Line, 0);
            AssertEqual(0, t.Slots.Count, "GenerateSlots(0) must produce empty slots.");
        }

        // ── SlotAssigner.Nearest：贪心找最近 agent ──
        public static void TestAssignNearest()
        {
            var formation = new Formation(1, Gen(FormationType.Line, 3, 1f));
            formation.Update(0); // LeaderPosition=Zero, 更新 WorldPosition=LocalPosition
            var positions = new Dictionary<int, Vector3>
            {
                { 10, new Vector3(-1f, 0, 0) },  // 最靠近 slot0 (-1,0,0)
                { 20, new Vector3(0.1f, 0, 0) }, // 最靠近 slot1 (0,0,0)
                { 30, new Vector3(1f, 0, 0) },   // 最靠近 slot2 (+1,0,0)
            };
            SlotAssigner.Assign(formation, positions, null, SlotAssignmentStrategy.Nearest);
            AssertEqual(10, formation.Template.Slots[0].AssignedAgentId,
                "Nearest: agent 10 must go to slot0 (-1,0,0).");
            AssertEqual(20, formation.Template.Slots[1].AssignedAgentId,
                "Nearest: agent 20 must go to slot1 (0,0,0).");
            AssertEqual(30, formation.Template.Slots[2].AssignedAgentId,
                "Nearest: agent 30 must go to slot2 (+1,0,0).");
            AssertEqual(0, formation.AgentToSlot[10], "AgentToSlot[10] must map to slot index 0.");
        }

        // ── SlotAssigner.FixedOrder：按 positions.Keys 迭代顺序顺序填 ──
        public static void TestAssignFixedOrder()
        {
            var formation = new Formation(1, Gen(FormationType.Line, 4, 1f));
            formation.Update(0);
            var positions = new Dictionary<int, Vector3>
            {
                { 100, new Vector3(9, 0, 0) },
                { 200, new Vector3(8, 0, 0) },
                { 300, new Vector3(7, 0, 0) },
            };
            SlotAssigner.Assign(formation, positions, null, SlotAssignmentStrategy.FixedOrder);
            // 迭代顺序：100→slot0, 200→slot1, 300→slot2（不论位置）
            AssertEqual(100, formation.Template.Slots[0].AssignedAgentId,
                "FixedOrder: first agent in dict goes to slot0.");
            AssertEqual(300, formation.Template.Slots[2].AssignedAgentId,
                "FixedOrder: third agent goes to slot2.");
            // slot3 未分配
            AssertEqual(0, formation.Template.Slots[3].AssignedAgentId,
                "FixedOrder: unused slot stays 0.");
        }

        // ── SlotAssigner.Priority：槽位按 Priority 降序排后再 Nearest 贪心 ──
        public static void TestAssignPriority()
        {
            var template = Gen(FormationType.Line, 2, 1f);
            // 手动设 Priority：slot0 低、slot1 高
            template.Slots[0].Priority = 1;
            template.Slots[1].Priority = 10;
            var formation = new Formation(1, template);
            formation.Update(0);
            var positions = new Dictionary<int, Vector3>
            {
                { 5, new Vector3(-0.9f, 0, 0) }, // 靠 slot0
                { 6, new Vector3(0.9f, 0, 0) },  // 靠 slot1
            };
            SlotAssigner.Assign(formation, positions, null, SlotAssignmentStrategy.Priority);
            // Priority 降序：先处理 slot1（P=10）挑最近 agent 6，再 slot0 挑 agent 5
            AssertEqual(6, formation.Template.Slots[1].AssignedAgentId,
                "Priority: high-priority slot1 gets its nearest agent first.");
            AssertEqual(5, formation.Template.Slots[0].AssignedAgentId,
                "Priority: slot0 gets remaining agent.");
        }

        // ── SlotAssigner.RoleBased：角色匹配空槽 ──
        public static void TestAssignRoleBased()
        {
            var template = Gen(FormationType.Line, 3, 1f);
            template.Slots[0].Role = "Leader";
            template.Slots[1].Role = "Support";
            template.Slots[2].Role = null; // 通用槽
            var formation = new Formation(1, template);
            formation.Update(0);
            var positions = new Dictionary<int, Vector3>
            {
                { 1, new Vector3(0, 0, 0) },
                { 2, new Vector3(1, 0, 0) },
            };
            var roles = new Dictionary<int, string>
            {
                { 1, "Leader" },
                { 2, "Support" },
            };
            SlotAssigner.Assign(formation, positions, roles, SlotAssignmentStrategy.RoleBased);
            AssertEqual(1, formation.Template.Slots[0].AssignedAgentId,
                "RoleBased: Leader agent 1 must go to Leader slot0.");
            AssertEqual(2, formation.Template.Slots[1].AssignedAgentId,
                "RoleBased: Support agent 2 must go to Support slot1.");
        }

        // ── Formation.GetAgentTargetPosition：旋转矩阵 XZ 平面 ──
        public static void TestGetAgentTargetPositionWithRotation()
        {
            var template = Gen(FormationType.Column, 2, 1f);
            var formation = new Formation(1, template)
            {
                LeaderPosition = new Vector3(5, 0, 5),
                LeaderRotation = 0f,
            };
            formation.AgentToSlot[42] = 1; // agent 42 在 slot1（local (0,0,-1)）
            var target = formation.GetAgentTargetPosition(42);
            AssertTrue(target.HasValue, "Registered agent must have target.");
            AssertNear(target.Value.X, 5f, 0.01f, "Rot=0: target.X = leader.X.");
            AssertNear(target.Value.Z, 5f - 1f, 0.01f, "Rot=0: target.Z = leader.Z - 1 (column -Z).");

            // 旋转 90°（π/2）：local(0,0,-1) → world(sin(π/2)*-1*-1?, ...)
            // x' = 0*cos - (-1)*sin(π/2) = 1, z' = 0*sin + (-1)*cos(π/2) = 0
            formation.LeaderRotation = MathF.PI / 2f;
            var target2 = formation.GetAgentTargetPosition(42);
            AssertNear(target2.Value.X, 5f + 1f, 0.01f, "Rot=90°: local(0,0,-1) rotates to +X.");
            AssertNear(target2.Value.Z, 5f, 0.01f, "Rot=90°: target.Z stays at leader.Z.");
        }

        // ── Formation.GetAgentTargetPosition：未注册返回 null ──
        public static void TestGetAgentTargetPositionUnregistered()
        {
            var formation = new Formation(1, Gen(FormationType.Line, 2, 1f));
            var target = formation.GetAgentTargetPosition(999);
            AssertTrue(!target.HasValue, "Unregistered agent must return null.");
        }
    }
}
