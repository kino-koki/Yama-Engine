using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YamaEngine.Gameplay
{
    // ════════════════════════════════════════════════════════════════════════
    //  NPCDialogSystem.cs — NPC 分支对话系统
    //
    //  生产级分支对话系统, 提供完整的条件/后果/脚本化对话能力:
    //    1. DialogNode           —— 对话节点 (多行文本/分支选项/条件/后果/脚本)
    //    2. DialogChoice         —— 分支选项 (显示文本/跳转目标/条件/禁用)
    //    3. IDialogCondition     —— 对话条件接口 (物品/任务/Flag/属性/组合)
    //    4. IDialogConsequence   —— 对话后果接口 (物品/Flag/属性/任务/脚本)
    //    5. DialogGraph          —— 对话图 (节点字典/验证/序列化)
    //    6. DialogEngine         —— 对话运行时引擎 (推进/条件评估/后果执行/事件)
    //
    //  与基础 DialogueSystem 的关系:
    //    基础版 (DialogueSystem.cs) 侧重剧情线性对话, 简单变量和条件。
    //    本文件侧重 NPC 交互的分支对话, 完整的条件/后果链和脚本化扩展。
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────
    // 1. DialogNode — 对话节点
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 对话节点。包含说话者、多行文本、分支选项、显示条件、选择后果等信息。
    /// 是对话图中的基本单元。
    /// </summary>
    public sealed class DialogNode
    {
        /// <summary>节点唯一 ID。</summary>
        public string ID { get; set; } = string.Empty;

        /// <summary>说话者名称 (NPC 或玩家名称)。</summary>
        public string SpeakerName { get; set; } = string.Empty;

        /// <summary>说话者头像纹理路径 (可为本地化键)。</summary>
        public string SpeakerPortrait { get; set; } = string.Empty;

        /// <summary>多行对话文本 (按顺序逐行显示)。</summary>
        public string[] Lines { get; set; } = Array.Empty<string>();

        /// <summary>分支选项 (玩家可做的选择)。无选项时自动跳转到 NextNodeID。</summary>
        public DialogChoice[] Choices { get; set; } = Array.Empty<DialogChoice>();

        /// <summary>显示此节点的条件 (不满足则跳过此节点)。</summary>
        public IDialogCondition[] Conditions { get; set; } = Array.Empty<IDialogCondition>();

        /// <summary>进入此节点时执行的后果 (无条件触发)。</summary>
        public IDialogConsequence[] Consequences { get; set; } = Array.Empty<IDialogConsequence>();

        /// <summary>
        /// 无选项时的自动跳转目标节点 ID。
        /// 空字符串表示对话结束。
        /// </summary>
        public string NextNodeID { get; set; } = string.Empty;

        /// <summary>
        /// C# 脚本片段 (高级条件/后果)。
        /// 可包含内联的条件评估或后果执行逻辑。
        /// </summary>
        public string Script { get; set; } = string.Empty;

        /// <summary>节点是否可重复访问 (false = 只能访问一次)。</summary>
        public bool CanRevisit { get; set; } = true;

        /// <summary>节点优先级 (同层节点选择时, 优先级高的先评估)。</summary>
        public int Priority { get; set; } = 0;

        /// <summary>附加自定义数据。</summary>
        public Dictionary<string, object> UserData { get; set; } = new();
    }

    // ──────────────────────────────────────────────────────────────
    // 2. DialogChoice — 分支选项
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 对话分支选项。玩家可选择后跳转到指定节点。
    /// 可设置显示条件和禁用状态。
    /// </summary>
    public sealed class DialogChoice
    {
        /// <summary>选项显示文本 (玩家看到的内容)。</summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>选择后跳转的目标节点 ID。</summary>
        public string TargetNodeID { get; set; } = string.Empty;

        /// <summary>显示此选项所需的条件 (不满足则隐藏)。</summary>
        public IDialogCondition[] Conditions { get; set; } = Array.Empty<IDialogCondition>();

        /// <summary>选择此选项后执行的后果。</summary>
        public IDialogConsequence[] Consequences { get; set; } = Array.Empty<IDialogConsequence>();

        /// <summary>是否禁用。</summary>
        public bool IsDisabled { get; set; }

        /// <summary>禁用原因 (IsDisabled 为 true 时向玩家显示)。</summary>
        public string DisabledReason { get; set; } = string.Empty;

        /// <summary>C# 脚本片段。</summary>
        public string Script { get; set; } = string.Empty;
    }

    // ──────────────────────────────────────────────────────────────
    // 3. IDialogCondition — 对话条件接口
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 对话条件接口。实现此接口以创建自定义的对话显示条件。
    /// </summary>
    public interface IDialogCondition
    {
        /// <summary>条件类型标识 (用于序列化)。</summary>
        string ConditionType { get; }

        /// <summary>
        /// 评估条件是否满足。
        /// </summary>
        /// <param name="context">对话运行时上下文, 提供对游戏状态 (背包/任务/Flag/属性) 的访问。</param>
        /// <returns>条件是否满足。</returns>
        bool Evaluate(IDialogContext context);

        /// <summary>序列化为字典 (用于 JSON)。</summary>
        Dictionary<string, object> Serialize();

        /// <summary>从字典反序列化。</summary>
        void Deserialize(Dictionary<string, object> data);
    }

    /// <summary>
    /// 对话运行时上下文接口。提供对游戏状态的访问, 用于条件评估和后果执行。
    /// </summary>
    public interface IDialogContext
    {
        /// <summary>检查玩家是否拥有指定物品及数量。</summary>
        bool HasItem(string itemId, int count = 1);

        /// <summary>获取指定物品的数量。</summary>
        int GetItemCount(string itemId);

        /// <summary>检查任务状态。</summary>
        QuestState GetQuestState(string questId);

        /// <summary>检查全局 Flag。</summary>
        bool GetFlag(string flagName);

        /// <summary>设置全局 Flag。</summary>
        void SetFlag(string flagName, bool value);

        /// <summary>获取玩家属性值。</summary>
        float GetStat(string statName);

        /// <summary>设置玩家属性值。</summary>
        void SetStat(string statName, float value);

        /// <summary>修改玩家属性值 (增量)。</summary>
        void ModifyStat(string statName, float delta);

        /// <summary>执行 C# 脚本片段。</summary>
        object? ExecuteScript(string script);

        /// <summary>获取对话本地变量。</summary>
        object? GetVariable(string name);

        /// <summary>设置对话本地变量。</summary>
        void SetVariable(string name, object? value);
    }

    // ──────────────────────────────────────────────────────────────
    // 3a. 具体条件实现
    // ──────────────────────────────────────────────────────────────

    /// <summary>检查玩家是否拥有某物品。</summary>
    public sealed class DialogCondition_HasItem : IDialogCondition
    {
        public string ConditionType => "HasItem";

        /// <summary>物品 ID。</summary>
        public string ItemID { get; set; } = string.Empty;

        /// <summary>所需数量 (默认 1)。</summary>
        public int RequiredCount { get; set; } = 1;

        public bool Evaluate(IDialogContext context)
        {
            return context.HasItem(ItemID, RequiredCount);
        }

        public Dictionary<string, object> Serialize()
        {
            return new Dictionary<string, object>
            {
                { "itemID", ItemID },
                { "requiredCount", RequiredCount }
            };
        }

        public void Deserialize(Dictionary<string, object> data)
        {
            ItemID = data.GetValueOrDefault("itemID", string.Empty)?.ToString() ?? string.Empty;
            RequiredCount = Convert.ToInt32(data.GetValueOrDefault("requiredCount", 1));
        }
    }

    /// <summary>检查任务状态。</summary>
    public sealed class DialogCondition_QuestState : IDialogCondition
    {
        public string ConditionType => "QuestState";

        /// <summary>任务 ID。</summary>
        public string QuestID { get; set; } = string.Empty;

        /// <summary>期望的任务状态。</summary>
        public QuestState ExpectedState { get; set; } = QuestState.Active;

        public bool Evaluate(IDialogContext context)
        {
            return context.GetQuestState(QuestID) == ExpectedState;
        }

        public Dictionary<string, object> Serialize()
        {
            return new Dictionary<string, object>
            {
                { "questID", QuestID },
                { "expectedState", ExpectedState.ToString() }
            };
        }

        public void Deserialize(Dictionary<string, object> data)
        {
            QuestID = data.GetValueOrDefault("questID", string.Empty)?.ToString() ?? string.Empty;
            var stateStr = data.GetValueOrDefault("expectedState", "Active")?.ToString() ?? "Active";
            ExpectedState = Enum.TryParse<QuestState>(stateStr, out var s) ? s : QuestState.Active;
        }
    }

    /// <summary>检查全局 Flag。</summary>
    public sealed class DialogCondition_Flag : IDialogCondition
    {
        public string ConditionType => "Flag";

        /// <summary>Flag 名称。</summary>
        public string FlagName { get; set; } = string.Empty;

        /// <summary>期望值 (默认 true)。</summary>
        public bool ExpectedValue { get; set; } = true;

        public bool Evaluate(IDialogContext context)
        {
            return context.GetFlag(FlagName) == ExpectedValue;
        }

        public Dictionary<string, object> Serialize()
        {
            return new Dictionary<string, object>
            {
                { "flagName", FlagName },
                { "expectedValue", ExpectedValue }
            };
        }

        public void Deserialize(Dictionary<string, object> data)
        {
            FlagName = data.GetValueOrDefault("flagName", string.Empty)?.ToString() ?? string.Empty;
            ExpectedValue = Convert.ToBoolean(data.GetValueOrDefault("expectedValue", true));
        }
    }

    /// <summary>属性值检查 (如力量 >= 10)。</summary>
    public sealed class DialogCondition_StatCheck : IDialogCondition
    {
        public string ConditionType => "StatCheck";

        /// <summary>属性名称。</summary>
        public string StatName { get; set; } = string.Empty;

        /// <summary>比较运算符。</summary>
        public string Operator { get; set; } = ">=";

        /// <summary>比较值。</summary>
        public float TargetValue { get; set; }

        public bool Evaluate(IDialogContext context)
        {
            var stat = context.GetStat(StatName);
            return Operator switch
            {
                "==" or "=" => Math.Abs(stat - TargetValue) < 0.001f,
                "!=" or "<>" => Math.Abs(stat - TargetValue) >= 0.001f,
                ">" => stat > TargetValue,
                ">=" => stat >= TargetValue,
                "<" => stat < TargetValue,
                "<=" => stat <= TargetValue,
                _ => false
            };
        }

        public Dictionary<string, object> Serialize()
        {
            return new Dictionary<string, object>
            {
                { "statName", StatName },
                { "operator", Operator },
                { "targetValue", TargetValue }
            };
        }

        public void Deserialize(Dictionary<string, object> data)
        {
            StatName = data.GetValueOrDefault("statName", string.Empty)?.ToString() ?? string.Empty;
            Operator = data.GetValueOrDefault("operator", ">=")?.ToString() ?? ">=";
            TargetValue = Convert.ToSingle(data.GetValueOrDefault("targetValue", 0f));
        }
    }

    /// <summary>AND/OR/NOT 组合条件。</summary>
    public sealed class DialogCondition_Composite : IDialogCondition
    {
        public string ConditionType => "Composite";

        /// <summary>逻辑运算符。</summary>
        public CompositeLogicOperator LogicOp { get; set; } = CompositeLogicOperator.And;

        /// <summary>子条件列表。</summary>
        public IDialogCondition[] SubConditions { get; set; } = Array.Empty<IDialogCondition>();

        public bool Evaluate(IDialogContext context)
        {
            return LogicOp switch
            {
                CompositeLogicOperator.And => SubConditions.All(c => c.Evaluate(context)),
                CompositeLogicOperator.Or => SubConditions.Any(c => c.Evaluate(context)),
                CompositeLogicOperator.Not => SubConditions.Length > 0 && !SubConditions[0].Evaluate(context),
                CompositeLogicOperator.Nand => !SubConditions.All(c => c.Evaluate(context)),
                CompositeLogicOperator.Nor => !SubConditions.Any(c => c.Evaluate(context)),
                _ => false
            };
        }

        public Dictionary<string, object> Serialize()
        {
            var subData = SubConditions.Select(c => new Dictionary<string, object>
            {
                { "type", c.ConditionType },
                { "data", c.Serialize() }
            }).ToList();

            return new Dictionary<string, object>
            {
                { "logicOp", LogicOp.ToString() },
                { "subConditions", subData }
            };
        }

        public void Deserialize(Dictionary<string, object> data)
        {
            var opStr = data.GetValueOrDefault("logicOp", "And")?.ToString() ?? "And";
            LogicOp = Enum.TryParse<CompositeLogicOperator>(opStr, out var op) ? op : CompositeLogicOperator.And;

            if (data.TryGetValue("subConditions", out var subObj) && subObj is System.Collections.IList subList)
            {
                var conditions = new List<IDialogCondition>();
                foreach (var item in subList)
                {
                    if (item is Dictionary<string, object> dict)
                    {
                        var type = dict.GetValueOrDefault("type", "")?.ToString() ?? "";
                        var conditionData = dict.GetValueOrDefault("data") as Dictionary<string, object>
                            ?? new Dictionary<string, object>();
                        var condition = ConditionFactory.Create(type);
                        condition?.Deserialize(conditionData);
                        if (condition != null) conditions.Add(condition);
                    }
                }
                SubConditions = conditions.ToArray();
            }
        }
    }

    /// <summary>组合逻辑运算符。</summary>
    public enum CompositeLogicOperator
    {
        And,
        Or,
        Not,
        Nand,
        Nor
    }

    /// <summary>条件工厂: 根据类型字符串创建条件实例。</summary>
    public static class ConditionFactory
    {
        private static readonly Dictionary<string, Func<IDialogCondition>> _creators = new()
        {
            { "HasItem", () => new DialogCondition_HasItem() },
            { "QuestState", () => new DialogCondition_QuestState() },
            { "Flag", () => new DialogCondition_Flag() },
            { "StatCheck", () => new DialogCondition_StatCheck() },
            { "Composite", () => new DialogCondition_Composite() }
        };

        /// <summary>注册自定义条件类型。</summary>
        public static void Register(string type, Func<IDialogCondition> creator)
        {
            _creators[type] = creator;
        }

        /// <summary>根据类型创建条件实例。</summary>
        public static IDialogCondition Create(string type)
        {
            return _creators.TryGetValue(type, out var creator)
                ? creator()
                : new DialogCondition_Flag(); // 默认返回 Flag 条件
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 4. IDialogConsequence — 对话后果接口
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 对话后果接口。实现此接口以创建自定义的对话选择后果。
    /// </summary>
    public interface IDialogConsequence
    {
        /// <summary>后果类型标识 (用于序列化)。</summary>
        string ConsequenceType { get; }

        /// <summary>执行后果。</summary>
        /// <param name="context">对话运行时上下文。</param>
        void Execute(IDialogContext context);

        /// <summary>序列化为字典。</summary>
        Dictionary<string, object> Serialize();

        /// <summary>从字典反序列化。</summary>
        void Deserialize(Dictionary<string, object> data);
    }

    // ──────────────────────────────────────────────────────────────
    // 4a. 具体后果实现
    // ──────────────────────────────────────────────────────────────

    /// <summary>给予物品。</summary>
    public sealed class DialogConsequence_GiveItem : IDialogConsequence
    {
        public string ConsequenceType => "GiveItem";

        /// <summary>物品 ID。</summary>
        public string ItemID { get; set; } = string.Empty;

        /// <summary>数量。</summary>
        public int Count { get; set; } = 1;

        public void Execute(IDialogContext context)
        {
            // 通过脚本执行: 调用背包系统的 AddItem 方法
            context.ExecuteScript($"Inventory.AddItem(\"{ItemID}\", {Count})");
        }

        public Dictionary<string, object> Serialize()
        {
            return new Dictionary<string, object> { { "itemID", ItemID }, { "count", Count } };
        }

        public void Deserialize(Dictionary<string, object> data)
        {
            ItemID = data.GetValueOrDefault("itemID", string.Empty)?.ToString() ?? string.Empty;
            Count = Convert.ToInt32(data.GetValueOrDefault("count", 1));
        }
    }

    /// <summary>移除物品。</summary>
    public sealed class DialogConsequence_RemoveItem : IDialogConsequence
    {
        public string ConsequenceType => "RemoveItem";

        public string ItemID { get; set; } = string.Empty;
        public int Count { get; set; } = 1;

        public void Execute(IDialogContext context)
        {
            context.ExecuteScript($"Inventory.RemoveItem(\"{ItemID}\", {Count})");
        }

        public Dictionary<string, object> Serialize()
        {
            return new Dictionary<string, object> { { "itemID", ItemID }, { "count", Count } };
        }

        public void Deserialize(Dictionary<string, object> data)
        {
            ItemID = data.GetValueOrDefault("itemID", string.Empty)?.ToString() ?? string.Empty;
            Count = Convert.ToInt32(data.GetValueOrDefault("count", 1));
        }
    }

    /// <summary>设置全局 Flag。</summary>
    public sealed class DialogConsequence_SetFlag : IDialogConsequence
    {
        public string ConsequenceType => "SetFlag";

        public string FlagName { get; set; } = string.Empty;
        public bool Value { get; set; } = true;

        public void Execute(IDialogContext context)
        {
            context.SetFlag(FlagName, Value);
        }

        public Dictionary<string, object> Serialize()
        {
            return new Dictionary<string, object> { { "flagName", FlagName }, { "value", Value } };
        }

        public void Deserialize(Dictionary<string, object> data)
        {
            FlagName = data.GetValueOrDefault("flagName", string.Empty)?.ToString() ?? string.Empty;
            Value = Convert.ToBoolean(data.GetValueOrDefault("value", true));
        }
    }

    /// <summary>修改属性值。</summary>
    public sealed class DialogConsequence_ModifyStat : IDialogConsequence
    {
        public string ConsequenceType => "ModifyStat";

        public string StatName { get; set; } = string.Empty;
        public float Delta { get; set; }

        public void Execute(IDialogContext context)
        {
            context.ModifyStat(StatName, Delta);
        }

        public Dictionary<string, object> Serialize()
        {
            return new Dictionary<string, object> { { "statName", StatName }, { "delta", Delta } };
        }

        public void Deserialize(Dictionary<string, object> data)
        {
            StatName = data.GetValueOrDefault("statName", string.Empty)?.ToString() ?? string.Empty;
            Delta = Convert.ToSingle(data.GetValueOrDefault("delta", 0f));
        }
    }

    /// <summary>开始任务。</summary>
    public sealed class DialogConsequence_StartQuest : IDialogConsequence
    {
        public string ConsequenceType => "StartQuest";

        public string QuestID { get; set; } = string.Empty;

        public void Execute(IDialogContext context)
        {
            context.ExecuteScript($"QuestLog.AcceptQuest(\"{QuestID}\")");
        }

        public Dictionary<string, object> Serialize()
        {
            return new Dictionary<string, object> { { "questID", QuestID } };
        }

        public void Deserialize(Dictionary<string, object> data)
        {
            QuestID = data.GetValueOrDefault("questID", string.Empty)?.ToString() ?? string.Empty;
        }
    }

    /// <summary>完成任务。</summary>
    public sealed class DialogConsequence_CompleteQuest : IDialogConsequence
    {
        public string ConsequenceType => "CompleteQuest";

        public string QuestID { get; set; } = string.Empty;

        public void Execute(IDialogContext context)
        {
            context.ExecuteScript($"QuestLog.CompleteQuest(\"{QuestID}\")");
        }

        public Dictionary<string, object> Serialize()
        {
            return new Dictionary<string, object> { { "questID", QuestID } };
        }

        public void Deserialize(Dictionary<string, object> data)
        {
            QuestID = data.GetValueOrDefault("questID", string.Empty)?.ToString() ?? string.Empty;
        }
    }

    /// <summary>执行 C# 脚本片段。</summary>
    public sealed class DialogConsequence_Script : IDialogConsequence
    {
        public string ConsequenceType => "Script";

        public string ScriptCode { get; set; } = string.Empty;

        public void Execute(IDialogContext context)
        {
            context.ExecuteScript(ScriptCode);
        }

        public Dictionary<string, object> Serialize()
        {
            return new Dictionary<string, object> { { "scriptCode", ScriptCode } };
        }

        public void Deserialize(Dictionary<string, object> data)
        {
            ScriptCode = data.GetValueOrDefault("scriptCode", string.Empty)?.ToString() ?? string.Empty;
        }
    }

    /// <summary>后果工厂。</summary>
    public static class ConsequenceFactory
    {
        private static readonly Dictionary<string, Func<IDialogConsequence>> _creators = new()
        {
            { "GiveItem", () => new DialogConsequence_GiveItem() },
            { "RemoveItem", () => new DialogConsequence_RemoveItem() },
            { "SetFlag", () => new DialogConsequence_SetFlag() },
            { "ModifyStat", () => new DialogConsequence_ModifyStat() },
            { "StartQuest", () => new DialogConsequence_StartQuest() },
            { "CompleteQuest", () => new DialogConsequence_CompleteQuest() },
            { "Script", () => new DialogConsequence_Script() }
        };

        public static void Register(string type, Func<IDialogConsequence> creator)
        {
            _creators[type] = creator;
        }

        public static IDialogConsequence Create(string type)
        {
            return _creators.TryGetValue(type, out var creator)
                ? creator()
                : new DialogConsequence_Script();
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 5. DialogGraph — 对话图
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 对话图。由节点和边 (通过 NextNodeID 和 Choices.TargetNodeID) 构成的有向图。
    /// 包含验证、序列化/反序列化功能。
    /// </summary>
    public sealed class DialogGraph
    {
        /// <summary>节点字典 (Key = NodeID)。</summary>
        public Dictionary<string, DialogNode> Nodes { get; set; } = new();

        /// <summary>起始节点 ID。</summary>
        public string StartNodeID { get; set; } = string.Empty;

        /// <summary>对话图 ID (用于数据库索引)。</summary>
        public string GraphID { get; set; } = string.Empty;

        /// <summary>对话图显示名称。</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>关联的 NPC ID。</summary>
        public string NPCID { get; set; } = string.Empty;

        /// <summary>对话图版本 (用于热更新)。</summary>
        public int Version { get; set; } = 1;

        /// <summary>附加元数据。</summary>
        public Dictionary<string, object> Meta { get; set; } = new();

        /// <summary>
        /// 验证对话图完整性。
        /// 检查: 悬空引用、起始节点存在、死循环检测。
        /// </summary>
        /// <returns>验证结果 (errors + warnings)。</returns>
        public DialogGraphValidationResult Validate()
        {
            var result = new DialogGraphValidationResult();

            // 检查起始节点
            if (string.IsNullOrEmpty(StartNodeID))
            {
                result.AddError("对话图缺少起始节点 (StartNodeID)。");
            }
            else if (!Nodes.ContainsKey(StartNodeID))
            {
                result.AddError($"起始节点 '{StartNodeID}' 不存在于节点字典中。");
            }

            // 检查节点引用完整性
            foreach (var kvp in Nodes)
            {
                var nodeId = kvp.Key;
                var node = kvp.Value;

                // 检查 NextNodeID 引用
                if (!string.IsNullOrEmpty(node.NextNodeID))
                {
                    if (!Nodes.ContainsKey(node.NextNodeID))
                    {
                        result.AddError(
                            $"节点 '{nodeId}' 的 NextNodeID '{node.NextNodeID}' 不存在 (悬空引用)。");
                    }
                }

                // 检查选项引用
                foreach (var choice in node.Choices)
                {
                    if (string.IsNullOrEmpty(choice.TargetNodeID))
                    {
                        result.AddWarning(
                            $"节点 '{nodeId}' 的选项 '{choice.Text}' 缺少 TargetNodeID (将结束对话)。");
                    }
                    else if (!Nodes.ContainsKey(choice.TargetNodeID))
                    {
                        result.AddError(
                            $"节点 '{nodeId}' 的选项 '{choice.Text}' 的 TargetNodeID '{choice.TargetNodeID}' 不存在。");
                    }
                }

                // 检查空节点
                if (node.Lines.Length == 0 && node.Choices.Length == 0 &&
                    string.IsNullOrEmpty(node.NextNodeID) && node.Consequences.Length == 0)
                {
                    result.AddWarning($"节点 '{nodeId}' 既无文本、无选项、也无跳转, 可能是空节点。");
                }
            }

            // 死循环检测
            var visited = new HashSet<string>();
            var path = new List<string>();
            if (!string.IsNullOrEmpty(StartNodeID))
            {
                DetectCycle(StartNodeID, visited, path, result);
            }

            // 检查孤立节点 (没有其他节点引用到它)
            var reachableNodes = new HashSet<string>();
            CollectReachable(StartNodeID, reachableNodes);
            foreach (var nodeId in Nodes.Keys)
            {
                if (!reachableNodes.Contains(nodeId) && nodeId != StartNodeID)
                {
                    result.AddWarning($"节点 '{nodeId}' 是孤立的, 没有任何路径能到达它。");
                }
            }

            return result;
        }

        private void DetectCycle(string nodeId, HashSet<string> visited, List<string> path, DialogGraphValidationResult result)
        {
            if (path.Contains(nodeId))
            {
                var cycleStart = path.IndexOf(nodeId);
                var cycleNodes = path.Skip(cycleStart).Concat(new[] { nodeId });
                result.AddError($"检测到死循环: {string.Join(" -> ", cycleNodes)}");
                return;
            }

            if (visited.Contains(nodeId)) return;

            if (!Nodes.TryGetValue(nodeId, out var node)) return;

            visited.Add(nodeId);
            path.Add(nodeId);

            // 遍历所有可能的出边
            if (!string.IsNullOrEmpty(node.NextNodeID))
                DetectCycle(node.NextNodeID, visited, path, result);

            foreach (var choice in node.Choices)
            {
                if (!string.IsNullOrEmpty(choice.TargetNodeID))
                    DetectCycle(choice.TargetNodeID, visited, path, result);
            }

            path.RemoveAt(path.Count - 1);
        }

        private void CollectReachable(string nodeId, HashSet<string> reachable)
        {
            if (string.IsNullOrEmpty(nodeId) || reachable.Contains(nodeId)) return;
            if (!Nodes.TryGetValue(nodeId, out var node)) return;

            reachable.Add(nodeId);

            if (!string.IsNullOrEmpty(node.NextNodeID))
                CollectReachable(node.NextNodeID, reachable);

            foreach (var choice in node.Choices)
            {
                if (!string.IsNullOrEmpty(choice.TargetNodeID))
                    CollectReachable(choice.TargetNodeID, reachable);
            }
        }

        /// <summary>序列化为 JSON 字符串。</summary>
        public string Serialize()
        {
            return JsonSerializer.Serialize(this, DialogGraphSerializerContext.Default.DialogGraph);
        }

        /// <summary>从 JSON 字符串反序列化。</summary>
        public static DialogGraph? Deserialize(string json)
        {
            try
            {
                var graph = JsonSerializer.Deserialize(json, DialogGraphSerializerContext.Default.DialogGraph);
                return graph;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DialogGraph.Deserialize 失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>从 JSON 文件加载对话图。</summary>
        public static DialogGraph? LoadFromFile(string filePath)
        {
            if (!System.IO.File.Exists(filePath)) return null;
            var json = System.IO.File.ReadAllText(filePath);
            return Deserialize(json);
        }

        /// <summary>保存到 JSON 文件。</summary>
        public bool SaveToFile(string filePath)
        {
            try
            {
                var json = Serialize();
                var dir = System.IO.Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllText(filePath, json);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DialogGraph.SaveToFile 失败: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>对话图验证结果。</summary>
    public sealed class DialogGraphValidationResult
    {
        public List<string> Errors { get; } = new();
        public List<string> Warnings { get; } = new();

        public bool IsValid => Errors.Count == 0;

        public void AddError(string error) => Errors.Add(error);
        public void AddWarning(string warning) => Warnings.Add(warning);

        public override string ToString()
        {
            var sb = new StringBuilder();
            if (IsValid) sb.AppendLine("DialogGraph validation passed.");
            foreach (var e in Errors) sb.AppendLine($"  ERROR: {e}");
            foreach (var w in Warnings) sb.AppendLine($"  WARNING: {w}");
            return sb.ToString();
        }
    }

    /// <summary>对话图 JSON 序列化上下文。</summary>
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
    [JsonSerializable(typeof(DialogGraph))]
    [JsonSerializable(typeof(DialogNode))]
    [JsonSerializable(typeof(DialogChoice))]
    [JsonSerializable(typeof(Dictionary<string, object>))]
    public partial class DialogGraphSerializerContext : JsonSerializerContext { }

    // ──────────────────────────────────────────────────────────────
    // 6. DialogEngine — 对话运行时引擎
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 对话运行时引擎。驱动对话图的执行:
    /// 节点推进、条件评估、后果执行、事件通知、暂停/恢复/中断。
    /// </summary>
    public sealed class DialogEngine : IDisposable
    {
        // ── 状态 ──
        private DialogGraph? _graph;
        private DialogNode? _currentNode;
        private DialogChoice[] _currentChoices = Array.Empty<DialogChoice>();
        private readonly Dictionary<string, object> _localVariables = new();
        private readonly HashSet<string> _visitedNodes = new();
        private int _currentLineIndex;
        private bool _isRunning;
        private bool _isPaused;
        private bool _disposed;

        /// <summary>对话运行时上下文 (由外部注入)。</summary>
        public IDialogContext? Context { get; set; }

        // ── 只读状态 ──
        /// <summary>当前节点。</summary>
        public DialogNode? CurrentNode => _currentNode;

        /// <summary>当前可用的选择列表 (已过滤条件和禁用状态)。</summary>
        public IReadOnlyList<DialogChoice> CurrentChoices => _currentChoices;

        /// <summary>当前文本行索引。</summary>
        public int CurrentLineIndex => _currentLineIndex;

        /// <summary>当前对话文本行 (所有行)。</summary>
        public string[] CurrentLines => _currentNode?.Lines ?? Array.Empty<string>();

        /// <summary>对话是否正在运行。</summary>
        public bool IsRunning => _isRunning;

        /// <summary>对话是否暂停。</summary>
        public bool IsPaused => _isPaused;

        /// <summary>当前对话图。</summary>
        public DialogGraph? CurrentGraph => _graph;

        // ── 事件 ──
        /// <summary>对话开始事件。</summary>
        public event Action<DialogGraph>? OnDialogStart;

        /// <summary>进入节点事件。</summary>
        public event Action<DialogNode>? OnNodeEnter;

        /// <summary>显示文本行事件。</summary>
        public event Action<string, string, string>? OnShowLine; // (speakerName, line, portrait)

        /// <summary>选项列表就绪事件。</summary>
        public event Action<DialogChoice[]>? OnChoicesReady;

        /// <summary>选择已做出事件。</summary>
        public event Action<DialogChoice, int>? OnChoiceMade;

        /// <summary>对话结束事件。</summary>
        public event Action? OnDialogEnd;

        // ══════════════════════════════════════════════════════════
        // 对话控制
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// 开始一段对话。
        /// </summary>
        /// <param name="graph">对话图。</param>
        /// <param name="context">运行时上下文。</param>
        /// <param name="startNodeId">起始节点 ID (null 则使用图的默认起始节点)。</param>
        public bool Start(DialogGraph graph, IDialogContext? context = null, string? startNodeId = null)
        {
            if (graph == null) throw new ArgumentNullException(nameof(graph));
            if (_isRunning) return false;

            // 验证对话图
            var validation = graph.Validate();
            if (!validation.IsValid)
            {
                System.Diagnostics.Debug.WriteLine($"DialogEngine.Start: 对话图验证失败:\n{validation}");
                // 不阻止运行, 但打印警告
            }

            _graph = graph;
            Context = context;
            _localVariables.Clear();
            _visitedNodes.Clear();
            _isRunning = true;
            _isPaused = false;

            var startId = startNodeId ?? graph.StartNodeID;
            if (string.IsNullOrEmpty(startId) || !graph.Nodes.ContainsKey(startId))
            {
                System.Diagnostics.Debug.WriteLine("DialogEngine.Start: 无效的起始节点, 对话无法开始。");
                _isRunning = false;
                return false;
            }

            OnDialogStart?.Invoke(graph);
            EnterNode(startId);
            return true;
        }

        /// <summary>
        /// 推进对话: 显示下一行文本。
        /// 返回 true 表示成功推进, false 表示当前节点文本已全部显示。
        /// </summary>
        public bool AdvanceText()
        {
            if (!_isRunning || _isPaused || _currentNode == null) return false;

            _currentLineIndex++;
            if (_currentLineIndex >= _currentNode.Lines.Length)
            {
                // 文本显示完毕
                return false;
            }

            // 触发显示事件
            OnShowLine?.Invoke(
                _currentNode.SpeakerName,
                _currentNode.Lines[_currentLineIndex],
                _currentNode.SpeakerPortrait);
            return true;
        }

        /// <summary>
        /// 推进对话: 选择一个选项。
        /// </summary>
        /// <param name="choiceIndex">选项索引。</param>
        public bool Advance(int choiceIndex)
        {
            if (!_isRunning || _isPaused || _currentNode == null) return false;

            if (_currentChoices.Length == 0)
            {
                // 无选项, 自动跳转到下一节点
                if (string.IsNullOrEmpty(_currentNode.NextNodeID))
                {
                    EndDialog();
                    return false;
                }
                EnterNode(_currentNode.NextNodeID);
                return true;
            }

            if (choiceIndex < 0 || choiceIndex >= _currentChoices.Length) return false;

            var choice = _currentChoices[choiceIndex];

            // 执行选择后果
            if (choice.Consequences.Length > 0)
            {
                ExecuteConsequences(choice.Consequences);
            }

            // 执行选项脚本
            if (!string.IsNullOrEmpty(choice.Script) && Context != null)
            {
                Context.ExecuteScript(choice.Script);
            }

            OnChoiceMade?.Invoke(choice, choiceIndex);

            // 跳转到目标节点
            if (string.IsNullOrEmpty(choice.TargetNodeID))
            {
                EndDialog();
                return false;
            }

            EnterNode(choice.TargetNodeID);
            return true;
        }

        /// <summary>
        /// 推进对话: 在无选项时继续到下一节点。
        /// </summary>
        public bool Continue()
        {
            if (!_isRunning || _isPaused || _currentNode == null) return false;

            if (string.IsNullOrEmpty(_currentNode.NextNodeID))
            {
                EndDialog();
                return false;
            }

            EnterNode(_currentNode.NextNodeID);
            return true;
        }

        /// <summary>暂停对话。</summary>
        public void Pause()
        {
            if (!_isRunning) return;
            _isPaused = true;
        }

        /// <summary>恢复对话。</summary>
        public void Resume()
        {
            if (!_isRunning || !_isPaused) return;
            _isPaused = false;

            // 重新触发当前节点的显示
            if (_currentNode != null)
            {
                ShowCurrentNodeContent();
            }
        }

        /// <summary>中断对话 (不执行后续后果)。</summary>
        public void Interrupt()
        {
            _isRunning = false;
            _isPaused = false;
            _currentNode = null;
            _currentChoices = Array.Empty<DialogChoice>();
            OnDialogEnd?.Invoke();
        }

        /// <summary>结束对话。</summary>
        private void EndDialog()
        {
            _isRunning = false;
            _isPaused = false;
            _currentNode = null;
            _currentChoices = Array.Empty<DialogChoice>();
            _currentLineIndex = 0;
            OnDialogEnd?.Invoke();
        }

        // ══════════════════════════════════════════════════════════
        // 内部方法
        // ══════════════════════════════════════════════════════════

        private void EnterNode(string nodeId)
        {
            if (_graph == null || Context == null) return;

            // 查找下一个有效节点 (跳过不满足条件的节点)
            string? validNodeId = FindNextValidNode(nodeId);
            if (validNodeId == null)
            {
                EndDialog();
                return;
            }

            if (!_graph.Nodes.TryGetValue(validNodeId, out var node))
            {
                EndDialog();
                return;
            }

            // 不可重复访问检查
            if (!node.CanRevisit && _visitedNodes.Contains(node.ID))
            {
                // 跳到下一节点
                if (string.IsNullOrEmpty(node.NextNodeID))
                {
                    EndDialog();
                    return;
                }
                EnterNode(node.NextNodeID);
                return;
            }

            _currentNode = node;
            _currentLineIndex = 0;
            _visitedNodes.Add(node.ID);

            // 执行节点后果 (无条件触发)
            if (node.Consequences.Length > 0)
            {
                ExecuteConsequences(node.Consequences);
            }

            // 执行节点脚本
            if (!string.IsNullOrEmpty(node.Script))
            {
                Context.ExecuteScript(node.Script);
            }

            OnNodeEnter?.Invoke(node);

            // 过滤可用选项
            _currentChoices = FilterChoices(node.Choices);

            // 显示内容
            ShowCurrentNodeContent();
        }

        private string? FindNextValidNode(string nodeId)
        {
            var visited = new HashSet<string>();
            var current = nodeId;

            while (current != null && !visited.Contains(current))
            {
                visited.Add(current);

                if (!_graph!.Nodes.TryGetValue(current, out var node))
                    return null;

                // 检查节点条件
                if (EvaluateConditions(node.Conditions))
                    return current;

                // 条件不满足, 尝试跳到下一节点
                current = node.NextNodeID;
            }

            return null;
        }

        private void ShowCurrentNodeContent()
        {
            if (_currentNode == null) return;

            // 显示第一行文本
            if (_currentNode.Lines.Length > 0)
            {
                _currentLineIndex = 0;
                OnShowLine?.Invoke(
                    _currentNode.SpeakerName,
                    _currentNode.Lines[0],
                    _currentNode.SpeakerPortrait);
            }
            else if (_currentChoices.Length > 0)
            {
                // 无文本但有选项, 直接显示选项
                OnChoicesReady?.Invoke(_currentChoices);
            }
            else
            {
                // 无文本无选项, 自动继续
                Continue();
            }
        }

        /// <summary>评估条件数组 (所有条件必须满足)。</summary>
        private bool EvaluateConditions(IDialogCondition[] conditions)
        {
            if (conditions.Length == 0) return true;

            foreach (var condition in conditions)
            {
                if (Context != null && !condition.Evaluate(Context))
                    return false;
            }
            return true;
        }

        /// <summary>过滤可用选项 (隐藏不满足条件的选项)。</summary>
        private DialogChoice[] FilterChoices(DialogChoice[] choices)
        {
            if (choices.Length == 0) return Array.Empty<DialogChoice>();

            var filtered = new List<DialogChoice>();
            foreach (var choice in choices)
            {
                if (EvaluateConditions(choice.Conditions))
                {
                    filtered.Add(choice);
                }
            }
            return filtered.ToArray();
        }

        /// <summary>执行后果数组。</summary>
        private void ExecuteConsequences(IDialogConsequence[] consequences)
        {
            if (Context == null || consequences.Length == 0) return;

            foreach (var consequence in consequences)
            {
                try
                {
                    consequence.Execute(Context);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"DialogEngine: 执行后果 {consequence.ConsequenceType} 失败: {ex.Message}");
                }
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _isRunning = false;
            _isPaused = false;
            _currentNode = null;
            _currentChoices = Array.Empty<DialogChoice>();
            _localVariables.Clear();
            _visitedNodes.Clear();
            OnDialogStart = null;
            OnNodeEnter = null;
            OnShowLine = null;
            OnChoicesReady = null;
            OnChoiceMade = null;
            OnDialogEnd = null;
        }
    }
}
