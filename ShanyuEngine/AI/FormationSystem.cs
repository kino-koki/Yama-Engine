// ============================================================================
//  ShanyuEngine.AI.FormationSystem.cs
//  山雨引擎 -- 阵型系统
//  对齐 UE5 Mass Formation / Unity Formation / Dragon Quest Party
// ----------------------------------------------------------------------------
//  实现多种阵型：
//    1. FormationType      - 阵型类型枚举(横排/纵队/V字/圆形/网格/自定义)
//    2. FormationSlot      - 阵型槽位(局部偏移/旋转)
//    3. FormationTemplate  - 阵型模板(预定义布局)
//    4. Formation          - 阵型实例(动态跟随 leader)
//    5. FormationSystem    - 阵型系统(管理多阵型 + 槽位分配)
//    6. FormationModifier  - 阵型修饰器(动态变形/呼吸效果)
//    7. SlotAssignment     - 槽位分配策略(就近/角色/固定)
// ----------------------------------------------------------------------------
//  注意：阵型槽位的"局部坐标"需要在 leader 移动时跟着旋转和缩放。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Numerics;

namespace ShanyuEngine.AI
{
    // ========================================================================
    //  一、阵型类型
    // ========================================================================

    /// <summary>预定义阵型类型。</summary>

    /// <summary>阵型槽位(局部空间)。</summary>

    /// <summary>
    /// 阵型模板。预定义的槽位布局。
    /// </summary>
    public sealed class FormationTemplate
    {
        public string Name;
        public FormationType Type;
        public List<FormationSlot> Slots = new();
        /// <summary>槽位间距(米)。</summary>
        public float Spacing = 1.5f;
        /// <summary>阵型是否跟随 leader 朝向旋转。</summary>
        public bool LockToLeaderRotation = true;
        /// <summary>阵型缩放(可以根据人数缩放)。</summary>
        public float Scale = 1f;
        /// <summary>阵型中心相对 leader 的偏移。</summary>
        public Vector3 CenterOffset = new(0, 0, -1f);

        public FormationTemplate(string name, FormationType type) { Name = name; Type = type; }

        /// <summary>根据类型自动生成槽位。</summary>
        public void GenerateSlots(int count, float spacing = 1.5f)
        {
            Spacing = spacing;
            Slots.Clear();
            switch (Type)
            {
                case FormationType.Line:
                    GenerateLine(count); break;
                case FormationType.Column:
                    GenerateColumn(count); break;
                case FormationType.V:
                    GenerateV(count); break;
                case FormationType.InvertedV:
                    GenerateInvertedV(count); break;
                case FormationType.Circle:
                    GenerateCircle(count); break;
                case FormationType.Square:
                    GenerateSquare(count); break;
                case FormationType.Triangle:
                    GenerateTriangle(count); break;
                case FormationType.Wedge:
                    GenerateWedge(count); break;
                case FormationType.Diamond:
                    GenerateDiamond(count); break;
                case FormationType.Scattered:
                    GenerateScattered(count); break;
            }
        }

        private void GenerateLine(int n)
        {
            float half = (n - 1) * 0.5f;
            for (int i = 0; i < n; i++)
            {
                Slots.Add(new FormationSlot
                {
                    Index = i,
                    LocalPosition = new Vector3((i - half) * Spacing, 0, 0),
                    LocalRotation = 0
                });
            }
        }

        private void GenerateColumn(int n)
        {
            for (int i = 0; i < n; i++)
            {
                Slots.Add(new FormationSlot
                {
                    Index = i,
                    LocalPosition = new Vector3(0, 0, -i * Spacing),
                    LocalRotation = 0
                });
            }
        }

        private void GenerateV(int n)
        {
            for (int i = 0; i < n; i++)
            {
                int half = i / 2;
                int side = (i % 2 == 0) ? -1 : 1;
                if (i == 0) side = 0;
                Slots.Add(new FormationSlot
                {
                    Index = i,
                    LocalPosition = new Vector3(side * half * Spacing, 0, -half * Spacing),
                    LocalRotation = 0
                });
            }
        }

        private void GenerateInvertedV(int n)
        {
            for (int i = 0; i < n; i++)
            {
                int half = i / 2;
                int side = (i % 2 == 0) ? -1 : 1;
                if (i == 0) side = 0;
                Slots.Add(new FormationSlot
                {
                    Index = i,
                    LocalPosition = new Vector3(side * half * Spacing, 0, half * Spacing),
                    LocalRotation = 0
                });
            }
        }

        private void GenerateCircle(int n)
        {
            float radius = MathF.Max(1f, n * Spacing / (2 * MathF.PI));
            for (int i = 0; i < n; i++)
            {
                float angle = i * (2 * MathF.PI / n);
                Slots.Add(new FormationSlot
                {
                    Index = i,
                    LocalPosition = new Vector3(MathF.Cos(angle) * radius, 0, MathF.Sin(angle) * radius),
                    LocalRotation = angle
                });
            }
        }

        private void GenerateSquare(int n)
        {
            int side = (int)MathF.Ceiling(MathF.Sqrt(n));
            float half = (side - 1) * 0.5f;
            for (int i = 0; i < n; i++)
            {
                int x = i % side;
                int z = i / side;
                Slots.Add(new FormationSlot
                {
                    Index = i,
                    LocalPosition = new Vector3((x - half) * Spacing, 0, (z - half) * Spacing),
                    LocalRotation = 0
                });
            }
        }

        private void GenerateTriangle(int n)
        {
            int row = 0, inRow = 0, rowSize = 1;
            float halfRow;
            for (int i = 0; i < n; i++)
            {
                halfRow = (rowSize - 1) * 0.5f;
                Slots.Add(new FormationSlot
                {
                    Index = i,
                    LocalPosition = new Vector3((inRow - halfRow) * Spacing, 0, -row * Spacing * 0.866f),
                    LocalRotation = 0
                });
                inRow++;
                if (inRow >= rowSize) { row++; rowSize++; inRow = 0; }
            }
        }

        private void GenerateWedge(int n)
        {
            // 楔形：两列向后展开
            for (int i = 0; i < n; i++)
            {
                int row = i / 2;
                int side = (i % 2 == 0) ? -1 : 1;
                if (i == 0)
                {
                    Slots.Add(new FormationSlot { Index = 0, LocalPosition = Vector3.Zero });
                }
                else
                {
                    Slots.Add(new FormationSlot
                    {
                        Index = i,
                        LocalPosition = new Vector3(side * (row + 1) * Spacing * 0.5f, 0, -(row + 1) * Spacing)
                    });
                }
            }
        }

        private void GenerateDiamond(int n)
        {
            // 菱形：前/后/左/右 + 内部
            for (int i = 0; i < n; i++)
            {
                int layer = (int)MathF.Ceiling((i - 1) * 0.25f);
                if (i == 0)
                {
                    Slots.Add(new FormationSlot { Index = 0, LocalPosition = Vector3.Zero });
                    continue;
                }
                int inLayer = i - 1 - (layer - 1) * 4;
                float angle = inLayer * (MathF.PI / 2);
                float r = layer * Spacing;
                Slots.Add(new FormationSlot
                {
                    Index = i,
                    LocalPosition = new Vector3(MathF.Cos(angle) * r, 0, MathF.Sin(angle) * r)
                });
            }
        }

        private void GenerateScattered(int n)
        {
            var rng = new Random(42);
            for (int i = 0; i < n; i++)
            {
                float r = (float)rng.NextDouble() * 3f;
                float a = (float)(rng.NextDouble() * Math.PI * 2);
                Slots.Add(new FormationSlot
                {
                    Index = i,
                    LocalPosition = new Vector3(MathF.Cos(a) * r, 0, MathF.Sin(a) * r - 1f)
                });
            }
        }
    }

    // ========================================================================
    //  二、阵型实例
    // ========================================================================

    /// <summary>
    /// 阵型实例。跟随一个 leader，每帧更新所有槽位的世界坐标。
    /// </summary>

    /// <summary>槽位更新模式。</summary>
    public enum SlotUpdateMode
    {
        /// <summary>每帧更新(精确但慢)。</summary>
        Continuous,
        /// <summary>每 N 帧更新一次。</summary>
        Periodic,
        /// <summary>leader 移动超过阈值才更新。</summary>
        Threshold
    }

    // ========================================================================
    //  三、阵型修饰器
    // ========================================================================

    /// <summary>阵型修饰器接口(动态改变槽位)。</summary>
    public interface IFormationModifier
    {
        Vector3 Apply(FormationSlot slot, Vector3 localPos, float phase);
    }

    /// <summary>呼吸效果(整体缩放波动)。</summary>
    public sealed class BreathingModifier : IFormationModifier
    {
        public float Frequency = 1f;
        public float Amplitude = 0.1f;
        public Vector3 Apply(FormationSlot slot, Vector3 localPos, float phase)
        {
            float scale = 1f + MathF.Sin(phase * Frequency) * Amplitude;
            return localPos * scale;
        }
    }

    /// <summary>波动效果(行波传递)。</summary>
    public sealed class WaveModifier : IFormationModifier
    {
        public float Frequency = 2f;
        public float Amplitude = 0.3f;
        public float WaveLength = 3f;
        public Vector3 Axis = new(0, 1, 0);
        public Vector3 Apply(FormationSlot slot, Vector3 localPos, float phase)
        {
            float wave = MathF.Sin(localPos.Z / WaveLength + phase * Frequency) * Amplitude;
            return localPos + Axis * wave;
        }
    }

    /// <summary>动态间距(战斗时拉开，平时紧凑)。</summary>
    public sealed class DynamicSpacingModifier : IFormationModifier
    {
        public float CombatMultiplier = 1.5f;
        public Func<bool> IsInCombat;
        public DynamicSpacingModifier(Func<bool> isCombat) { IsInCombat = isCombat; }
        public Vector3 Apply(FormationSlot slot, Vector3 localPos, float phase)
        {
            return IsInCombat() ? localPos * CombatMultiplier : localPos;
        }
    }

    // ========================================================================
    //  四、槽位分配策略
    // ========================================================================

    /// <summary>槽位分配策略。</summary>
    public enum SlotAssignmentStrategy
    {
        /// <summary>就近原则(谁离槽位近谁占)。</summary>
        Nearest,
        /// <summary>按角色(坦克占前排，远程占后排)。</summary>
        RoleBased,
        /// <summary>固定(先来后到)。</summary>
        FixedOrder,
        /// <summary>优先级(高的先选)。</summary>
        Priority
    }

    /// <summary>槽位分配器。</summary>
    public static class SlotAssigner
    {
        /// <summary>根据策略分配槽位。</summary>
        public static void Assign(Formation formation, Dictionary<int, Vector3> agentPositions,
            Dictionary<int, string>? agentRoles, SlotAssignmentStrategy strategy)
        {
            // 先清空
            for (int i = 0; i < formation.Template.Slots.Count; i++)
            {
                var s = formation.Template.Slots[i];
                s.AssignedAgentId = 0;
                formation.Template.Slots[i] = s;
            }
            formation.AgentToSlot.Clear();

            switch (strategy)
            {
                case SlotAssignmentStrategy.Nearest:
                    AssignNearest(formation, agentPositions);
                    break;
                case SlotAssignmentStrategy.RoleBased:
                    AssignRoleBased(formation, agentPositions, agentRoles);
                    break;
                case SlotAssignmentStrategy.FixedOrder:
                    AssignFixed(formation, agentPositions);
                    break;
                case SlotAssignmentStrategy.Priority:
                    AssignPriority(formation, agentPositions);
                    break;
            }
        }

        private static void AssignNearest(Formation f, Dictionary<int, Vector3> positions)
        {
            // 贪心：每个槽位找最近的未分配 agent
            var available = new List<int>(positions.Keys);
            for (int i = 0; i < f.Template.Slots.Count && available.Count > 0; i++)
            {
                var slot = f.Template.Slots[i];
                int best = -1;
                float bestDist = float.MaxValue;
                foreach (var id in available)
                {
                    float d = Vector3.DistanceSquared(positions[id], slot.WorldPosition);
                    if (d < bestDist) { bestDist = d; best = id; }
                }
                if (best >= 0)
                {
                    slot.AssignedAgentId = best;
                    f.Template.Slots[i] = slot;
                    f.AgentToSlot[best] = i;
                    available.Remove(best);
                }
            }
        }

        private static void AssignRoleBased(Formation f, Dictionary<int, Vector3> positions,
            Dictionary<int, string>? roles)
        {
            if (roles == null) { AssignNearest(f, positions); return; }
            // 按 slot.Role 匹配
            foreach (var (agentId, role) in roles)
            {
                for (int i = 0; i < f.Template.Slots.Count; i++)
                {
                    var slot = f.Template.Slots[i];
                    if (slot.AssignedAgentId != 0) continue;
                    if (string.IsNullOrEmpty(slot.Role) || slot.Role == role)
                    {
                        slot.AssignedAgentId = agentId;
                        f.Template.Slots[i] = slot;
                        f.AgentToSlot[agentId] = i;
                        break;
                    }
                }
            }
            // 剩下的按 Nearest
            foreach (var agentId in positions.Keys)
            {
                if (f.AgentToSlot.ContainsKey(agentId)) continue;
                f.AddAgent(agentId);
            }
        }

        private static void AssignFixed(Formation f, Dictionary<int, Vector3> positions)
        {
            int slotIdx = 0;
            foreach (var id in positions.Keys)
            {
                if (slotIdx >= f.Template.Slots.Count) break;
                var slot = f.Template.Slots[slotIdx];
                slot.AssignedAgentId = id;
                f.Template.Slots[slotIdx] = slot;
                f.AgentToSlot[id] = slotIdx;
                slotIdx++;
            }
        }

        private static void AssignPriority(Formation f, Dictionary<int, Vector3> positions)
        {
            // 按 slot.Priority 排序
            var sortedSlots = new List<int>();
            for (int i = 0; i < f.Template.Slots.Count; i++) sortedSlots.Add(i);
            sortedSlots.Sort((a, b) => f.Template.Slots[b].Priority.CompareTo(f.Template.Slots[a].Priority));
            var available = new List<int>(positions.Keys);
            foreach (var i in sortedSlots)
            {
                if (available.Count == 0) break;
                var slot = f.Template.Slots[i];
                // 在 available 里找最近的
                int best = -1;
                float bestDist = float.MaxValue;
                foreach (var id in available)
                {
                    float d = Vector3.DistanceSquared(positions[id], slot.WorldPosition);
                    if (d < bestDist) { bestDist = d; best = id; }
                }
                if (best >= 0)
                {
                    slot.AssignedAgentId = best;
                    f.Template.Slots[i] = slot;
                    f.AgentToSlot[best] = i;
                    available.Remove(best);
                }
            }
        }
    }

    // ========================================================================
    //  五、阵型系统主类
    // ========================================================================

    /// <summary>
    /// 阵型系统。管理多个阵型，每帧更新。
    /// </summary>
    public sealed class FormationSystem
    {
        private readonly Dictionary<int, Formation> _formations = new();
        private readonly Dictionary<int, int> _agentToFormation = new(); // agentId -> formationId
        private int _nextId = 1;
        private float _updateTimer;
        private int _updateFrameSkip = 0; // 0=每帧, N=每 N+1 帧

        public int FormationCount => _formations.Count;
        public float UpdateInterval { get; set; } = 0f;
        public bool AutoUpdateLeaderRotation { get; set; } = true;

        public FormationSystem() { }

        // --------------------------------------------------------------------
        //  阵型管理
        // --------------------------------------------------------------------

        public Formation CreateFormation(FormationType type, int slotCount, float spacing = 1.5f, string name = "")
        {
            var template = new FormationTemplate(name, type);
            template.GenerateSlots(slotCount, spacing);
            var formation = new Formation(_nextId++, template) { Name = name };
            _formations[formation.Id] = formation;
            return formation;
        }

        public Formation CreateFormationFromTemplate(FormationTemplate template, string name = "")
        {
            var formation = new Formation(_nextId++, template) { Name = name };
            _formations[formation.Id] = formation;
            return formation;
        }

        public bool RemoveFormation(int id)
        {
            if (!_formations.TryGetValue(id, out var f)) return false;
            // 解除所有 agent 的绑定
            foreach (var agentId in f.AgentToSlot.Keys)
                _agentToFormation.Remove(agentId);
            _formations.Remove(id);
            return true;
        }

        public Formation? GetFormation(int id) => _formations.TryGetValue(id, out var f) ? f : null;
        public IEnumerable<Formation> GetAllFormations() => _formations.Values;

        /// <summary>添加 agent 到阵型。</summary>
        public bool AddAgentToFormation(int formationId, int agentId, string? role = null)
        {
            if (!_formations.TryGetValue(formationId, out var f)) return false;
            if (!f.AddAgent(agentId, role)) return false;
            _agentToFormation[agentId] = formationId;
            return true;
        }

        /// <summary>从阵型移除 agent。</summary>
        public void RemoveAgentFromFormation(int agentId)
        {
            if (!_agentToFormation.TryGetValue(agentId, out var fid)) return;
            if (_formations.TryGetValue(fid, out var f))
                f.RemoveAgent(agentId);
            _agentToFormation.Remove(agentId);
        }

        /// <summary>获取 agent 所在阵型。</summary>
        public Formation? GetAgentFormation(int agentId)
            => _agentToFormation.TryGetValue(agentId, out var fid) && _formations.TryGetValue(fid, out var f) ? f : null;

        /// <summary>获取 agent 在阵型中的目标位置。</summary>
        public Vector3? GetAgentTargetPosition(int agentId)
        {
            var f = GetAgentFormation(agentId);
            return f?.GetAgentTargetPosition(agentId);
        }

        /// <summary>设置阵型 leader。</summary>
        public void SetLeader(int formationId, int leaderAgentId, Vector3 leaderPos, float leaderRot)
        {
            if (!_formations.TryGetValue(formationId, out var f)) return;
            f.LeaderAgentId = leaderAgentId;
            f.LeaderPosition = leaderPos;
            f.LeaderRotation = leaderRot;
        }

        /// <summary>更新 leader 位置(由外部移动系统调用)。</summary>
        public void UpdateLeader(int formationId, Vector3 pos, Vector3 vel)
        {
            if (!_formations.TryGetValue(formationId, out var f)) return;
            f.LeaderPosition = pos;
            f.LeaderVelocity = vel;
            if (AutoUpdateLeaderRotation && vel.LengthSquared() > 0.1f)
            {
                float targetRot = MathF.Atan2(vel.X, vel.Z);
                // 平滑旋转
                float diff = targetRot - f.LeaderRotation;
                while (diff > MathF.PI) diff -= 2 * MathF.PI;
                while (diff < -MathF.PI) diff += 2 * MathF.PI;
                f.LeaderRotation += diff * 0.1f;
            }
        }

        // --------------------------------------------------------------------
        //  主更新
        // --------------------------------------------------------------------

        /// <summary>每帧更新所有阵型。</summary>
        public void Update(float dt)
        {
            _updateTimer += dt;
            if (UpdateInterval > 0 && _updateTimer < UpdateInterval)
                return;
            float effectiveDt = _updateTimer > 0 ? _updateTimer : dt;
            _updateTimer = 0;

            foreach (var f in _formations.Values)
            {
                if (!f.IsActive) continue;
                f.Update(effectiveDt);
            }
        }

        // --------------------------------------------------------------------
        //  预设阵型
        // --------------------------------------------------------------------

        /// <summary>创建一个标准的战斗小队(4 人)。</summary>
        public Formation CreateSquad(Vector3 leaderPos, float leaderRot)
        {
            var f = CreateFormation(FormationType.Diamond, 4, 2f, "Squad");
            f.LeaderPosition = leaderPos;
            f.LeaderRotation = leaderRot;
            // 设置角色：前排坦克，后排远程
            var s0 = f.Template.Slots[0]; s0.Role = "leader"; f.Template.Slots[0] = s0;
            var s1 = f.Template.Slots[1]; s1.Role = "tank"; f.Template.Slots[1] = s1;
            var s2 = f.Template.Slots[2]; s2.Role = "ranged"; f.Template.Slots[2] = s2;
            var s3 = f.Template.Slots[3]; s3.Role = "support"; f.Template.Slots[3] = s3;
            return f;
        }

        /// <summary>创建行军队列(纵队)。</summary>
        public Formation CreateMarchingColumn(Vector3 leaderPos, float leaderRot, int count)
        {
            var f = CreateFormation(FormationType.Column, count, 2f, "Column");
            f.LeaderPosition = leaderPos;
            f.LeaderRotation = leaderRot;
            return f;
        }

        /// <summary>创建战斗横排。</summary>
        public Formation CreateBattleLine(Vector3 leaderPos, float leaderRot, int count)
        {
            var f = CreateFormation(FormationType.Line, count, 1.5f, "BattleLine");
            f.LeaderPosition = leaderPos;
            f.LeaderRotation = leaderRot;
            return f;
        }

        // --------------------------------------------------------------------
        //  动态调整
        // --------------------------------------------------------------------

        /// <summary>动态调整阵型大小(战斗时拉大间距)。</summary>
        public void SetFormationScale(int formationId, float scale)
        {
            if (_formations.TryGetValue(formationId, out var f))
                f.Template.Scale = scale;
        }

        /// <summary>切换阵型类型(保留成员)。</summary>
        public void ChangeFormationType(int formationId, FormationType newType, float spacing)
        {
            if (!_formations.TryGetValue(formationId, out var f)) return;
            // 记录当前成员
            var members = new List<(int agentId, string role)>();
            foreach (var (slotIdx, agentId) in f.GetOccupiedSlots())
            {
                members.Add((agentId, f.Template.Slots[slotIdx].Role));
            }
            // 重新生成模板
            var newTemplate = new FormationTemplate(f.Name, newType) { Spacing = spacing };
            newTemplate.GenerateSlots(members.Count, spacing);
            newTemplate.CenterOffset = f.Template.CenterOffset;
            newTemplate.LockToLeaderRotation = f.Template.LockToLeaderRotation;
            f.Template = newTemplate;
            // 重新分配
            for (int i = 0; i < members.Count && i < newTemplate.Slots.Count; i++)
            {
                f.AddAgentToSlot(members[i].agentId, i);
                var slot = newTemplate.Slots[i];
                slot.Role = members[i].role;
                newTemplate.Slots[i] = slot;
            }
        }

        /// <summary>获取所有阵型的调试数据。</summary>
        public FormationDebugData GetDebugData()
        {
            var data = new FormationDebugData();
            foreach (var f in _formations.Values)
            {
                if (!f.IsActive) continue;
                data.FormationCenters.Add((f.LeaderPosition, f.Id));
                foreach (var (slotIdx, agentId) in f.GetOccupiedSlots())
                {
                    var slot = f.Template.Slots[slotIdx];
                    data.Slots.Add((slot.WorldPosition, agentId, f.Id));
                }
                // 槽位连线
                for (int i = 0; i < f.Template.Slots.Count; i++)
                {
                    var s = f.Template.Slots[i];
                    data.FormationOutlines.Add((f.LeaderPosition, s.WorldPosition));
                }
            }
            return data;
        }
    }

    /// <summary>阵型调试数据。</summary>
    public sealed class FormationDebugData
    {
        public List<(Vector3 pos, int formationId)> FormationCenters = new();
        public List<(Vector3 pos, int agentId, int formationId)> Slots = new();
        public List<(Vector3 from, Vector3 to)> FormationOutlines = new();
    }
}
