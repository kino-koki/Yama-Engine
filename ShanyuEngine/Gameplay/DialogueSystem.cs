using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using ShanyuEngine.Audio;
using ShanyuEngine.Core;
using ShanyuEngine.ECS;
using ShanyuEngine.UI;

namespace ShanyuEngine.Gameplay
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - 角色对话系统 (Dialogue System)
    //
    // 本文件实现一套数据驱动的剧情对话框架, 涵盖:
    //   1. DialogueNode        —— 对话节点 (文本/选择/分支/动作/事件/条件/设标志)
    //   2. DialogueChoice      —— 选择项 (文本/条件/目标节点/结果标志)
    //   3. DialogueGraph       —— 对话图 (有向图, 入口/出口/跳转条件)
    //   4. DialogueVariable    —— 对话变量 (全局/本地, 布尔/整数/浮点/字符串)
    //   5. DialogueCondition   —— 对话条件 (变量/物品/任务/关系比较)
    //   6. DialogueSpeaker     —— 说话者 (角色信息/头像/声音/口型绑定)
    //   7. DialogueSystem      —— 对话系统核心 (开始/推进/选择/条件/变量/标志/事件)
    //   8. DialogueUIController —— 对话 UI 控制器 (打字机/选项/头像/语音)
    //   9. DialogueHistory     —— 对话历史 (记录/回顾/跳过)
    //   10. DialogueComponent  —— ECS 组件
    //
    // 对接系统:
    //   * 本地化: ShanyuEngine.Core.Localization (文本查找, 本地化键)
    //   * 音频  : ShanyuEngine.Audio.AudioSystem (语音播放)
    //   * ECS   : ShanyuEngine.ECS.IComponent / ISystem / EntityComponentSystem
    //   * UI    : ShanyuEngine.UI.Widget / PanelWidget / TextWidget (对话框渲染)
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────
    // 1. 枚举定义
    // ──────────────────────────────────────────────────────────────

    /// <summary>对话节点类型, 决定节点在对话流中的行为。</summary>
    public enum DialogueNodeType
    {
        /// <summary>文本节点: 显示一句台词, 推进到下一节点。</summary>
        Text,
        /// <summary>选择节点: 显示若干选项, 玩家选择后跳转对应分支。</summary>
        Choice,
        /// <summary>分支节点: 根据条件自动选择跳转目标 (无玩家交互)。</summary>
        Branch,
        /// <summary>动作节点: 触发一个游戏动作 (如给物品/播放动画)。</summary>
        Action,
        /// <summary>事件节点: 广播一个游戏事件。</summary>
        Event,
        /// <summary>条件节点: 评估条件, 满足则继续, 否则跳到失败分支。</summary>
        Condition,
        /// <summary>设标志节点: 设置剧情标志 / 变量。</summary>
        SetFlag
    }

    /// <summary>对话变量作用域。</summary>
    public enum DialogueVariableScope
    {
        /// <summary>本地变量: 仅在当前对话图内有效, 对话结束销毁。</summary>
        Local,
        /// <summary>全局变量: 跨对话持久存在 (存档保存)。</summary>
        Global
    }

    /// <summary>对话变量值类型。</summary>
    public enum DialogueVariableType { Boolean, Integer, Float, String }

    /// <summary>对话条件类型。</summary>
    public enum DialogueConditionType
    {
        /// <summary>变量比较 (==, !=, &gt;, &lt;, &gt;=, &lt;=)。</summary>
        VariableCompare,
        /// <summary>物品检查 (背包是否拥有指定物品及数量)。</summary>
        ItemCheck,
        /// <summary>任务状态检查。</summary>
        QuestCheck,
        /// <summary>角色关系/好感度检查。</summary>
        RelationshipCheck,
        /// <summary>剧情标志检查 (布尔变量是否为 true)。</summary>
        FlagCheck
    }

    /// <summary>对话摄像机镜头模式。</summary>
    public enum DialogueCameraMode
    {
        /// <summary>默认对话摄像机 (双人取景)。</summary>
        Default,
        /// <summary>特写镜头 (聚焦说话者面部)。</summary>
        CloseUp,
        /// <summary>全景镜头 (展示环境)。</summary>
        Wide,
        /// <summary>过肩镜头 (从另一角色肩后看向说话者)。</summary>
        OverShoulder,
        /// <summary>不切换镜头。</summary>
        None
    }

    // ──────────────────────────────────────────────────────────────
    // 2. 对话变量 (DialogueVariable)
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 对话变量。支持全局 / 本地作用域, 以及布尔 / 整数 / 浮点 / 字符串四种类型。
    /// 全局变量由 <see cref="DialogueSystem"/> 统一管理并持久化, 本地变量随对话图生命周期存在。
    /// </summary>
    public sealed class DialogueVariable
    {
        /// <summary>变量名 (键)。</summary>
        public string Name { get; set; } = "";
        /// <summary>作用域。</summary>
        public DialogueVariableScope Scope { get; set; } = DialogueVariableScope.Local;
        /// <summary>值类型。</summary>
        public DialogueVariableType Type { get; set; } = DialogueVariableType.Boolean;

        private object? _value;

        /// <summary>当前值 (运行时读写)。</summary>
        public object? Value
        {
            get => _value;
            set => _value = Coerce(value);
        }

        /// <summary>将传入值强制转换为变量声明类型。</summary>
        private object? Coerce(object? v)
        {
            if (v == null) return Type switch
            {
                DialogueVariableType.Boolean => false,
                DialogueVariableType.Integer => 0,
                DialogueVariableType.Float => 0f,
                DialogueVariableType.String => "",
                _ => null
            };
            return Type switch
            {
                DialogueVariableType.Boolean => Convert.ToBoolean(v),
                DialogueVariableType.Integer => Convert.ToInt32(v),
                DialogueVariableType.Float => Convert.ToSingle(v),
                DialogueVariableType.String => v.ToString() ?? "",
                _ => v
            };
        }

        public bool AsBoolean() => _value is bool b ? b : Convert.ToBoolean(_value);
        public int AsInteger() => _value is int i ? i : Convert.ToInt32(_value);
        public float AsFloat() => _value is float f ? f : Convert.ToSingle(_value);
        public string AsString() => _value?.ToString() ?? "";

        /// <summary>创建布尔变量。</summary>
        public static DialogueVariable Bool(string name, bool value, DialogueVariableScope scope = DialogueVariableScope.Local) =>
            new() { Name = name, Scope = scope, Type = DialogueVariableType.Boolean, Value = value };

        /// <summary>创建整数变量。</summary>
        public static DialogueVariable Int(string name, int value, DialogueVariableScope scope = DialogueVariableScope.Local) =>
            new() { Name = name, Scope = scope, Type = DialogueVariableType.Integer, Value = value };

        /// <summary>创建浮点变量。</summary>
        public static DialogueVariable Float(string name, float value, DialogueVariableScope scope = DialogueVariableScope.Local) =>
            new() { Name = name, Scope = scope, Type = DialogueVariableType.Float, Value = value };

        /// <summary>创建字符串变量。</summary>
        public static DialogueVariable String(string name, string value, DialogueVariableScope scope = DialogueVariableScope.Local) =>
            new() { Name = name, Scope = scope, Type = DialogueVariableType.String, Value = value };
    }

    // ──────────────────────────────────────────────────────────────
    // 3. 对话条件 (DialogueCondition)
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 对话条件。用于分支节点与选项的前置判断, 支持变量比较 / 物品 / 任务 / 关系 / 标志检查。
    /// 评估逻辑由 <see cref="DialogueSystem.EvaluateCondition"/> 执行, 可注入外部查询回调。
    /// </summary>
    public sealed class DialogueCondition
    {
        /// <summary>条件类型。</summary>
        public DialogueConditionType Type { get; set; } = DialogueConditionType.VariableCompare;

        /// <summary>变量名 / 物品ID / 任务ID / 角色ID (取决于 Type)。</summary>
        public string Key { get; set; } = "";
        /// <summary>比较运算符 (VariableCompare 时使用)。</summary>
        public string Operator { get; set; } = "==";
        /// <summary>比较目标值。</summary>
        public string TargetValue { get; set; } = "";

        /// <summary>关系/好感度阈值 (RelationshipCheck 时使用)。</summary>
        public float Threshold { get; set; }

        /// <summary>
        /// 默认评估回调。引擎外部可注入更复杂的查询 (如查询背包系统 / 任务系统)。
        /// 签名: (condition, system) => bool
        /// </summary>
        public static Func<DialogueCondition, DialogueSystem, bool>? ExternalEvaluator;

        /// <summary>评估此条件是否满足 (委托给外部评估器或默认实现)。</summary>
        public bool Evaluate(DialogueSystem system)
        {
            if (ExternalEvaluator != null) return ExternalEvaluator(this, system);
            return system?.EvaluateCondition(this) ?? false;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 4. 说话者 (DialogueSpeaker)
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 说话者。描述一个参与对话的角色, 包含显示名 / 头像 / 语音配置 / 口型动画绑定。
    /// 一个对话图可包含多个说话者。
    /// </summary>
    public sealed class DialogueSpeaker
    {
        /// <summary>角色唯一 ID。</summary>
        public string CharacterId { get; set; } = "";
        /// <summary>显示名 (可为本地化键, 由 Localization 查找)。</summary>
        public string DisplayName { get; set; } = "";
        /// <summary>头像纹理资源名。</summary>
        public string PortraitTexture { get; set; } = "";
        /// <summary>头像边框颜色 (UI 显示用)。</summary>
        public Vector4 PortraitTint = Vector4.One;

        /// <summary>默认语音音频片段名 (在 AudioSystem 中注册)。</summary>
        public string VoiceClipName { get; set; } = "";
        /// <summary>语音音量倍数。</summary>
        public float VoiceVolume { get; set; } = 1f;
        /// <summary>语音音调倍数。</summary>
        public float VoicePitch { get; set; } = 1f;

        /// <summary>口型动画绑定 (骨骼/BlendShape 名称)。</summary>
        public string LipSyncBoneName { get; set; } = "Jaw";
        /// <summary>口型动画最大张合度。</summary>
        public float LipSyncMaxOpen { get; set; } = 1f;

        /// <summary>说话者位置 (世界坐标, 用于 3D 语音定位)。</summary>
        public Vector3 WorldPosition;

        /// <summary>获取本地化后的显示名。</summary>
        public string GetLocalizedDisplayName()
        {
            if (string.IsNullOrEmpty(DisplayName)) return CharacterId;
            // 若 DisplayName 是本地化键 (含 '.'), 则查找本地化文本
            if (DisplayName.Contains('.') && Localization.Instance.TryGet(DisplayName, out var text))
                return text;
            return Localization.Instance.Get(DisplayName);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 5. 选择项 (DialogueChoice)
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 对话选择项。用于 Choice 类型节点, 玩家选择后跳转到目标节点,
    /// 并可设置结果标志 / 触发动作。
    /// </summary>
    public sealed class DialogueChoice
    {
        /// <summary>选项文本 (可为本地化键)。</summary>
        public string Text { get; set; } = "";
        /// <summary>显示此选项所需满足的前置条件 (null 表示无条件)。</summary>
        public DialogueCondition? Condition { get; set; }
        /// <summary>选择后跳转的目标节点 Id (空表示结束对话)。</summary>
        public string TargetNodeId { get; set; } = "";
        /// <summary>选择后设置的结果标志 (Key=Value 形式)。</summary>
        public List<KeyValuePair<string, string>> ResultFlags { get; set; } = new();
        /// <summary>选择后触发的动作事件名。</summary>
        public string ActionEvent { get; set; } = "";

        /// <summary>获取本地化后的选项文本。</summary>
        public string GetLocalizedText()
        {
            if (string.IsNullOrEmpty(Text)) return "";
            if (Text.Contains('.') && Localization.Instance.TryGet(Text, out var t)) return t;
            return Localization.Instance.Get(Text);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 6. 对话节点 (DialogueNode)
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 对话节点。对话图中的一个节点, 携带节点类型 / 说话者 / 文本 / 语音 /
    /// 动画表情触发 / 摄像机镜头 / 下一节点列表等数据。
    /// </summary>
    public sealed class DialogueNode
    {
        /// <summary>节点唯一 Id。</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        /// <summary>节点类型。</summary>
        public DialogueNodeType Type { get; set; } = DialogueNodeType.Text;

        /// <summary>说话者角色 Id (引用 DialogueGraph.Speakers 中的条目)。</summary>
        public string SpeakerId { get; set; } = "";
        /// <summary>文本内容 (可为本地化键或直接文本)。</summary>
        public string Text { get; set; } = "";
        /// <summary>语音音频片段名 (在 AudioSystem 中注册, 空表示无语音)。</summary>
        public string VoiceClip { get; set; } = "";

        /// <summary>动画触发 (如 "Talk"/"Wave", 由动画系统响应)。</summary>
        public string AnimationTrigger { get; set; } = "";
        /// <summary>表情触发 (如 "Happy"/"Angry", 由表情系统响应)。</summary>
        public string ExpressionTrigger { get; set; } = "";

        /// <summary>摄像机镜头模式。</summary>
        public DialogueCameraMode CameraMode { get; set; } = DialogueCameraMode.Default;

        /// <summary>下一节点列表 (Text 节点通常只有一个; Choice 节点的选项各自带目标)。</summary>
        public List<string> NextNodeIds { get; set; } = new();
        /// <summary>本节点的选择项 (仅 Choice 类型节点使用)。</summary>
        public List<DialogueChoice> Choices { get; set; } = new();

        /// <summary>分支条件 (Branch 节点: 满足条件跳 NextNodeIds[0], 否则 [1])。</summary>
        public DialogueCondition? BranchCondition { get; set; }
        /// <summary>Condition 节点的条件。</summary>
        public DialogueCondition? Condition { get; set; }
        /// <summary>SetFlag 节点要设置的标志列表。</summary>
        public List<KeyValuePair<string, string>> SetFlags { get; set; } = new();
        /// <summary>Action 节点要触发的动作事件名。</summary>
        public string ActionEvent { get; set; } = "";
        /// <summary>Event 节点要广播的事件标签。</summary>
        public string EventTag { get; set; } = "";

        /// <summary>获取本地化后的节点文本 (对接 LocalizationSystem)。</summary>
        public string GetLocalizedText()
        {
            if (string.IsNullOrEmpty(Text)) return "";
            // 若文本含 '.' 视为本地化键查找; 否则视为直接文本
            if (Text.Contains('.') && Localization.Instance.TryGet(Text, out var t)) return t;
            return Localization.Instance.Get(Text);
        }

        /// <summary>是否为出口节点 (无下一节点)。</summary>
        public bool IsExitNode => NextNodeIds.Count == 0 && Type != DialogueNodeType.Choice;
    }

    // ──────────────────────────────────────────────────────────────
    // 7. 对话图 (DialogueGraph)
    //    有向图: 节点 + 边, 含入口 / 出口 / 跳转条件。
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 对话图。由若干 <see cref="DialogueNode"/> 组成的有向图,
    /// 持有入口节点与出口节点, 以及参与对话的说话者集合。
    /// </summary>
    public sealed class DialogueGraph
    {
        /// <summary>图唯一 Id。</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        /// <summary>图名。</summary>
        public string Name { get; set; } = "Dialogue";

        /// <summary>节点字典 (Id -> Node)。</summary>
        public Dictionary<string, DialogueNode> Nodes { get; set; } = new();
        /// <summary>参与对话的说话者 (CharacterId -> Speaker)。</summary>
        public Dictionary<string, DialogueSpeaker> Speakers { get; set; } = new();

        /// <summary>入口节点 Id。</summary>
        public string EntryNodeId { get; set; } = "";
        /// <summary>出口节点 Id 列表 (到达即结束对话)。</summary>
        public List<string> ExitNodeIds { get; set; } = new();

        /// <summary>本地变量初始值 (对话开始时初始化)。</summary>
        public List<DialogueVariable> LocalVariables { get; set; } = new();

        /// <summary>添加节点。</summary>
        public void AddNode(DialogueNode node)
        {
            if (node == null || string.IsNullOrEmpty(node.Id)) return;
            Nodes[node.Id] = node;
            if (string.IsNullOrEmpty(EntryNodeId)) EntryNodeId = node.Id;
        }

        /// <summary>添加说话者。</summary>
        public void AddSpeaker(DialogueSpeaker speaker)
        {
            if (speaker != null && !string.IsNullOrEmpty(speaker.CharacterId))
                Speakers[speaker.CharacterId] = speaker;
        }

        /// <summary>获取入口节点。</summary>
        public DialogueNode? GetEntryNode() =>
            string.IsNullOrEmpty(EntryNodeId) ? null : GetNode(EntryNodeId);

        /// <summary>按 Id 获取节点。</summary>
        public DialogueNode? GetNode(string id) =>
            string.IsNullOrEmpty(id) ? null : Nodes.TryGetValue(id, out var n) ? n : null;

        /// <summary>获取说话者。</summary>
        public DialogueSpeaker? GetSpeaker(string characterId) =>
            string.IsNullOrEmpty(characterId) ? null :
            Speakers.TryGetValue(characterId, out var s) ? s : null;

        /// <summary>验证图完整性 (入口存在 / 无悬空跳转)。</summary>
        public List<string> Validate()
        {
            var errors = new List<string>();
            if (string.IsNullOrEmpty(EntryNodeId)) errors.Add("对话图缺少入口节点。");
            else if (!Nodes.ContainsKey(EntryNodeId)) errors.Add($"入口节点不存在: {EntryNodeId}");

            foreach (var node in Nodes.Values)
            {
                foreach (var nextId in node.NextNodeIds)
                {
                    if (!Nodes.ContainsKey(nextId))
                        errors.Add($"节点 {node.Id} 的下一节点 {nextId} 不存在。");
                }
                foreach (var choice in node.Choices)
                {
                    if (!string.IsNullOrEmpty(choice.TargetNodeId) && !Nodes.ContainsKey(choice.TargetNodeId))
                        errors.Add($"节点 {node.Id} 的选项目标 {choice.TargetNodeId} 不存在。");
                }
            }
            return errors;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 8. 对话历史 (DialogueHistory)
    // ──────────────────────────────────────────────────────────────

    /// <summary>对话历史条目, 记录一句已播放的台词。</summary>
    public sealed class DialogueHistoryEntry
    {
        /// <summary>所属对话图 Id。</summary>
        public string GraphId { get; set; } = "";
        /// <summary>节点 Id。</summary>
        public string NodeId { get; set; } = "";
        /// <summary>说话者名。</summary>
        public string SpeakerName { get; set; } = "";
        /// <summary>台词文本 (已本地化)。</summary>
        public string Text { get; set; } = "";
        /// <summary>播放时间戳。</summary>
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        /// <summary>玩家是否做出了选择 (Choice 节点时记录所选选项文本)。</summary>
        public string? SelectedChoiceText { get; set; }
    }

    /// <summary>
    /// 对话历史。记录已播放的对话条目, 用于回顾与跳过已读对话。
    /// </summary>
    public sealed class DialogueHistory
    {
        private readonly List<DialogueHistoryEntry> _entries = new();
        private readonly HashSet<string> _seenNodes = new();

        /// <summary>历史条目数。</summary>
        public int Count => _entries.Count;
        /// <summary>只读条目列表。</summary>
        public IReadOnlyList<DialogueHistoryEntry> Entries => _entries;

        /// <summary>记录一条对话历史。</summary>
        public void Add(DialogueHistoryEntry entry)
        {
            if (entry == null) return;
            _entries.Add(entry);
            _seenNodes.Add(entry.NodeId);
        }

        /// <summary>某节点是否已播放过 (用于跳过已读)。</summary>
        public bool HasSeenNode(string nodeId) => _seenNodes.Contains(nodeId);

        /// <summary>清空历史。</summary>
        public void Clear()
        {
            _entries.Clear();
            _seenNodes.Clear();
        }

        /// <summary>导出历史为纯文本 (用于日志 / 调试)。</summary>
        public string ExportText()
        {
            var sb = new StringBuilder();
            foreach (var e in _entries)
            {
                sb.Append('[').Append(e.SpeakerName).Append("] ");
                sb.AppendLine(e.Text);
            }
            return sb.ToString();
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 9. 对话 ECS 组件 (DialogueComponent)
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 对话 ECS 组件。挂在触发对话的实体上, 标记其正在参与对话,
    /// 并持有对 <see cref="DialogueSystem"/> 的引用。遵循引擎 ECS 约定: 组件为 struct。
    /// </summary>
    public struct DialogueComponent : IComponent
    {
        /// <summary>对话系统实例引用 (由世界持有)。</summary>
        public DialogueSystem? System;
        /// <summary>该实体参与对话的说话者 Id。</summary>
        public string SpeakerId;
        /// <summary>是否正在对话中。</summary>
        public bool InDialogue;
        /// <summary>当前对话图 Id。</summary>
        public string ActiveGraphId;
    }

    // ──────────────────────────────────────────────────────────────
    // 10. 对话系统核心 (DialogueSystem)
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 对话系统核心。管理对话图库 / 变量 / 标志, 驱动对话流程,
    /// 并与本地化 / 音频 / UI 系统对接。提供开始 / 推进 / 选择 / 条件评估等接口。
    /// </summary>
    public sealed class DialogueSystem
    {
        // ── 数据存储 ──
        private readonly Dictionary<string, DialogueGraph> _graphs = new();
        private readonly Dictionary<string, DialogueVariable> _globalVariables = new(StringComparer.Ordinal);
        private readonly Dictionary<string, DialogueVariable> _localVariables = new(StringComparer.Ordinal);
        private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

        // ── 运行时状态 ──
        private DialogueGraph? _activeGraph;
        private DialogueNode? _currentNode;
        private readonly List<DialogueSpeaker> _participants = new();
        private readonly DialogueHistory _history = new();

        // ── 外部系统引用 ──
        private readonly AudioSystem? _audio;

        /// <summary>对话历史。</summary>
        public DialogueHistory History => _history;
        /// <summary>当前对话图。</summary>
        public DialogueGraph? ActiveGraph => _activeGraph;
        /// <summary>当前节点。</summary>
        public DialogueNode? CurrentNode => _currentNode;
        /// <summary>是否正在对话中。</summary>
        public bool IsActive => _activeGraph != null && _currentNode != null;
        /// <summary>当前参与对话的说话者。</summary>
        public IReadOnlyList<DialogueSpeaker> Participants => _participants;

        // ── 事件 ──
        /// <summary>对话开始事件。参数: 对话图 Id。</summary>
        public event Action<string>? OnDialogueStart;
        /// <summary>进入节点事件。参数: 节点。</summary>
        public event Action<DialogueNode>? OnNodeEnter;
        /// <summary>玩家选择选项事件。参数: 选项索引。</summary>
        public Action<int>? OnChoiceSelected;
        /// <summary>对话结束事件。</summary>
        public event Action? OnDialogueEnd;
        /// <summary>节点动作触发事件。参数: 动作事件名。</summary>
        public event Action<string>? OnActionTriggered;
        /// <summary>节点事件广播。参数: 事件标签。</summary>
        public event Action<string>? OnEventBroadcast;

        /// <summary>创建对话系统。传入音频系统以播放语音 (可为 null)。</summary>
        public DialogueSystem(AudioSystem? audio = null)
        {
            _audio = audio;
        }

        // ── 对话图管理 ──

        /// <summary>注册一个对话图。</summary>
        public void RegisterGraph(DialogueGraph graph)
        {
            if (graph != null) _graphs[graph.Id] = graph;
        }

        /// <summary>按 Id 获取对话图。</summary>
        public DialogueGraph? GetGraph(string graphId) =>
            string.IsNullOrEmpty(graphId) ? null :
            _graphs.TryGetValue(graphId, out var g) ? g : null;

        // ── 变量与标志管理 ──

        /// <summary>设置变量 (自动按作用域分发到全局/本地存储)。</summary>
        public void SetVariable(DialogueVariable variable)
        {
            if (variable == null) return;
            var store = variable.Scope == DialogueVariableScope.Global ? _globalVariables : _localVariables;
            store[variable.Name] = variable;
        }

        /// <summary>获取变量值。</summary>
        public DialogueVariable? GetVariable(string name)
        {
            if (_localVariables.TryGetValue(name, out var v)) return v;
            if (_globalVariables.TryGetValue(name, out var g)) return g;
            return null;
        }

        /// <summary>获取变量值 (类型化便捷方法)。</summary>
        public T GetVariable<T>(string name, T defaultValue = default!)
        {
            var v = GetVariable(name);
            if (v == null) return defaultValue;
            try
            {
                return v.Type switch
                {
                    DialogueVariableType.Boolean => (T)(object)v.AsBoolean(),
                    DialogueVariableType.Integer => (T)(object)v.AsInteger(),
                    DialogueVariableType.Float => (T)(object)v.AsFloat(),
                    DialogueVariableType.String => (T)(object)v.AsString(),
                    _ => defaultValue
                };
            }
            catch { return defaultValue; }
        }

        /// <summary>设置剧情标志 (布尔全局变量)。</summary>
        public void SetFlag(string flagName, bool value = true)
        {
            if (value) _flags.Add(flagName);
            else _flags.Remove(flagName);
            SetVariable(DialogueVariable.Bool(flagName, value, DialogueVariableScope.Global));
        }

        /// <summary>检查剧情标志是否已设置。</summary>
        public bool HasFlag(string flagName) => _flags.Contains(flagName);

        /// <summary>清除所有剧情标志 (调试用)。</summary>
        public void ClearFlags() => _flags.Clear();

        // ── 条件评估 ──

        /// <summary>
        /// 评估对话条件。支持变量比较 / 标志检查, 物品/任务/关系检查
        /// 委托给 <see cref="DialogueCondition.ExternalEvaluator"/>。
        /// </summary>
        public bool EvaluateCondition(DialogueCondition condition)
        {
            if (condition == null) return true;
            switch (condition.Type)
            {
                case DialogueConditionType.FlagCheck:
                    return HasFlag(condition.Key);

                case DialogueConditionType.VariableCompare:
                    {
                        var v = GetVariable(condition.Key);
                        if (v == null) return false;
                        return CompareVariable(v, condition.Operator, condition.TargetValue);
                    }

                case DialogueConditionType.ItemCheck:
                case DialogueConditionType.QuestCheck:
                case DialogueConditionType.RelationshipCheck:
                    // 这些条件依赖外部系统, 由 ExternalEvaluator 处理
                    return DialogueCondition.ExternalEvaluator?.Invoke(condition, this) ?? false;

                default:
                    return false;
            }
        }

        /// <summary>按运算符比较变量值与目标值。</summary>
        private static bool CompareVariable(DialogueVariable v, string op, string target)
        {
            try
            {
                return v.Type switch
                {
                    DialogueVariableType.Boolean => op == "==" ? v.AsBoolean() == bool.Parse(target)
                                                          : v.AsBoolean() != bool.Parse(target),
                    DialogueVariableType.Integer => CompareNumbers(v.AsInteger(), int.Parse(target), op),
                    DialogueVariableType.Float => CompareNumbers(v.AsFloat(), float.Parse(target), op),
                    DialogueVariableType.String => op switch
                    {
                        "==" => v.AsString() == target,
                        "!=" => v.AsString() != target,
                        "contains" => v.AsString().Contains(target),
                        _ => false
                    },
                    _ => false
                };
            }
            catch { return false; }
        }

        /// <summary>通用数值比较。</summary>
        private static bool CompareNumbers<T>(T a, T b, string op) where T : IComparable<T>
        {
            int c = a.CompareTo(b);
            return op switch
            {
                "==" => c == 0,
                "!=" => c != 0,
                ">" => c > 0,
                "<" => c < 0,
                ">=" => c >= 0,
                "<=" => c <= 0,
                _ => false
            };
        }

        // ── 对话流程控制 ──

        /// <summary>
        /// 开始对话。初始化本地变量, 跳转到入口节点。
        /// </summary>
        /// <param name="graphId">对话图 Id。</param>
        /// <param name="participants">参与对话的说话者列表。</param>
        /// <returns>是否成功开始。</returns>
        public bool StartDialogue(string graphId, List<DialogueSpeaker>? participants = null)
        {
            var graph = GetGraph(graphId);
            if (graph == null) return false;

            var errors = graph.Validate();
            if (errors.Count > 0) return false;

            _activeGraph = graph;
            _participants.Clear();
            if (participants != null) _participants.AddRange(participants);
            // 将图内定义的说话者也加入参与者
            foreach (var sp in graph.Speakers.Values)
                if (!_participants.Contains(sp)) _participants.Add(sp);

            // 初始化本地变量
            _localVariables.Clear();
            foreach (var lv in graph.LocalVariables)
                _localVariables[lv.Name] = lv;

            OnDialogueStart?.Invoke(graphId);

            var entry = graph.GetEntryNode();
            if (entry == null) return false;
            EnterNode(entry);
            return true;
        }

        /// <summary>进入指定节点 (核心调度, 处理各类节点副作用)。</summary>
        private void EnterNode(DialogueNode node)
        {
            _currentNode = node;
            OnNodeEnter?.Invoke(node);

            // 记录历史
            var speaker = _activeGraph?.GetSpeaker(node.SpeakerId);
            _history.Add(new DialogueHistoryEntry
            {
                GraphId = _activeGraph?.Id ?? "",
                NodeId = node.Id,
                SpeakerName = speaker?.GetLocalizedDisplayName() ?? "",
                Text = node.GetLocalizedText()
            });

            // 播放语音 (对接 AudioSystem)
            PlayVoiceClip(node, speaker);

            // 触发动画 / 表情
            if (!string.IsNullOrEmpty(node.AnimationTrigger))
                OnActionTriggered?.Invoke($"Anim:{node.AnimationTrigger}");
            if (!string.IsNullOrEmpty(node.ExpressionTrigger))
                OnActionTriggered?.Invoke($"Expression:{node.ExpressionTrigger}");

            // 处理非交互节点 (自动推进)
            switch (node.Type)
            {
                case DialogueNodeType.Branch:
                    {
                        // 评估条件: 满足走 NextNodeIds[0], 否则 [1]
                        bool ok = node.BranchCondition?.Evaluate(this) ?? true;
                        string nextId = ok && node.NextNodeIds.Count > 0 ? node.NextNodeIds[0]
                                      : node.NextNodeIds.Count > 1 ? node.NextNodeIds[1] : "";
                        GotoNext(nextId);
                        break;
                    }
                case DialogueNodeType.Condition:
                    {
                        bool ok = node.Condition?.Evaluate(this) ?? true;
                        string nextId = ok && node.NextNodeIds.Count > 0 ? node.NextNodeIds[0]
                                      : node.NextNodeIds.Count > 1 ? node.NextNodeIds[1] : "";
                        GotoNext(nextId);
                        break;
                    }
                case DialogueNodeType.SetFlag:
                    {
                        foreach (var flag in node.SetFlags)
                            ApplyFlag(flag.Key, flag.Value);
                        GotoNext(node.NextNodeIds.Count > 0 ? node.NextNodeIds[0] : "");
                        break;
                    }
                case DialogueNodeType.Action:
                    {
                        if (!string.IsNullOrEmpty(node.ActionEvent))
                            OnActionTriggered?.Invoke(node.ActionEvent);
                        GotoNext(node.NextNodeIds.Count > 0 ? node.NextNodeIds[0] : "");
                        break;
                    }
                case DialogueNodeType.Event:
                    {
                        if (!string.IsNullOrEmpty(node.EventTag))
                            OnEventBroadcast?.Invoke(node.EventTag);
                        GotoNext(node.NextNodeIds.Count > 0 ? node.NextNodeIds[0] : "");
                        break;
                    }
                // Text / Choice 节点等待玩家交互, 不自动推进
            }
        }

        /// <summary>应用一个标志设置 (支持布尔/整数/浮点/字符串推断)。</summary>
        private void ApplyFlag(string key, string value)
        {
            // 优先尝试布尔
            if (bool.TryParse(value, out var b)) { SetFlag(key, b); return; }
            if (int.TryParse(value, out var i)) { SetVariable(DialogueVariable.Int(key, i, DialogueVariableScope.Global)); return; }
            if (float.TryParse(value, out var f)) { SetVariable(DialogueVariable.Float(key, f, DialogueVariableScope.Global)); return; }
            SetVariable(DialogueVariable.String(key, value, DialogueVariableScope.Global));
        }

        /// <summary>跳转到下一节点 (空 Id 表示结束对话)。</summary>
        private void GotoNext(string nextNodeId)
        {
            if (string.IsNullOrEmpty(nextNodeId))
            {
                EndDialogue();
                return;
            }
            var next = _activeGraph?.GetNode(nextNodeId);
            if (next == null) { EndDialogue(); return; }
            EnterNode(next);
        }

        /// <summary>
        /// 推进对话 (玩家点击继续)。仅对 Text 节点有效, 跳转到下一节点。
        /// </summary>
        public void Advance()
        {
            if (!IsActive || _currentNode == null) return;
            if (_currentNode.Type != DialogueNodeType.Text) return;
            GotoNext(_currentNode.NextNodeIds.Count > 0 ? _currentNode.NextNodeIds[0] : "");
        }

        /// <summary>
        /// 选择选项。仅对 Choice 节点有效。
        /// </summary>
        /// <param name="index">选项索引 (0 起)。</param>
        public void SelectChoice(int index)
        {
            if (!IsActive || _currentNode == null || _currentNode.Type != DialogueNodeType.Choice) return;
            if (index < 0 || index >= _currentNode.Choices.Count) return;

            var choice = _currentNode.Choices[index];
            OnChoiceSelected?.Invoke(index);

            // 记录选择到历史
            _history.Add(new DialogueHistoryEntry
            {
                GraphId = _activeGraph?.Id ?? "",
                NodeId = _currentNode.Id,
                Text = choice.GetLocalizedText(),
                SelectedChoiceText = choice.GetLocalizedText()
            });

            // 应用结果标志
            foreach (var flag in choice.ResultFlags)
                ApplyFlag(flag.Key, flag.Value);

            // 触发动作
            if (!string.IsNullOrEmpty(choice.ActionEvent))
                OnActionTriggered?.Invoke(choice.ActionEvent);

            // 跳转目标
            GotoNext(choice.TargetNodeId);
        }

        /// <summary>获取当前节点的可选选项 (过滤掉条件不满足的)。</summary>
        public List<DialogueChoice> GetAvailableChoices()
        {
            var result = new List<DialogueChoice>();
            if (_currentNode == null || _currentNode.Type != DialogueNodeType.Choice) return result;
            foreach (var c in _currentNode.Choices)
            {
                if (c.Condition == null || c.Condition.Evaluate(this))
                    result.Add(c);
            }
            return result;
        }

        /// <summary>结束当前对话。</summary>
        public void EndDialogue()
        {
            if (!IsActive) return;
            _activeGraph = null;
            _currentNode = null;
            _participants.Clear();
            OnDialogueEnd?.Invoke();
        }

        // ── 语音播放 (对接 AudioSystem) ──

        /// <summary>播放节点语音 (对接 AudioSystem, 使用 Voice 总线)。</summary>
        private void PlayVoiceClip(DialogueNode node, DialogueSpeaker? speaker)
        {
            if (_audio == null) return;
            string clipName = !string.IsNullOrEmpty(node.VoiceClip) ? node.VoiceClip
                            : speaker?.VoiceClipName ?? "";
            if (string.IsNullOrEmpty(clipName)) return;

            var clip = _audio.GetClip(clipName);
            if (clip == null) return;

            Vector3 pos = speaker?.WorldPosition ?? Vector3.Zero;
            float vol = speaker?.VoiceVolume ?? 1f;
            float pitch = speaker?.VoicePitch ?? 1f;
            // 对接 AudioSystem.PlayOneShot, 语音归入 Voice 总线
            _audio.PlayOneShot(clip, pos, vol, pitch);
        }

        /// <summary>停止所有正在播放的语音。</summary>
        public void StopVoice()
        {
            _audio?.StopAll(AudioBus.Voice);
        }

        // ── 每帧更新 (由 DialogueUpdateSystem 驱动) ──

        /// <summary>每帧更新 (主要用于 UI 打字机推进, 由 ECS 系统调用)。</summary>
        public void Update(float dt) { /* UI 推进由 DialogueUIController.Update 处理 */ }
    }

    // ──────────────────────────────────────────────────────────────
    // 11. 对话 UI 控制器 (DialogueUIController)
    //     驱动 UMG Widget 显示对话框 / 打字机效果 / 选项 / 头像 / 语音。
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 对话 UI 控制器。基于引擎 UMG Widget 系统, 实现对话框显示/隐藏、
    /// 打字机逐字效果、选项按钮动态生成、说话者头像与名称显示。
    /// 与 <see cref="DialogueSystem"/> 协作: 订阅系统事件刷新 UI。
    /// </summary>
    public sealed class DialogueUIController
    {
        private readonly DialogueSystem _system;
        private readonly AudioSystem? _audio;

        // ── UI 控件引用 (由外部绑定) ──
        private PanelWidget? _rootPanel;
        private TextWidget? _speakerNameText;
        private TextWidget _dialogueText = new() { Name = "DialogueText" };
        private PanelWidget? _choicePanel;
        private readonly List<TextWidget> _choiceButtons = new();

        // ── 打字机状态 ──
        private string _fullText = "";
        private StringBuilder _displayedText = new();
        private int _charIndex;
        private float _typewriterTimer;
        /// <summary>打字机每字间隔 (秒)。</summary>
        public float TypewriterSpeed { get; set; } = 0.03f;
        /// <summary>是否正在播放打字机效果。</summary>
        public bool IsTyping { get; private set; }
        /// <summary>对话框是否可见。</summary>
        public bool IsVisible { get; private set; }

        /// <summary>选项按钮生成回调 (由外部 UI 系统实现具体按钮创建)。</summary>
        public Func<string, int, TextWidget>? CreateChoiceButton;

        /// <summary>头像显示回调 (参数: 头像纹理名, 边框色)。</summary>
        public Action<string, Vector4>? OnShowPortrait;

        /// <summary>创建控制器。</summary>
        /// <param name="system">对话系统。</param>
        /// <param name="audio">音频系统 (语音, 可为 null)。</param>
        public DialogueUIController(DialogueSystem system, AudioSystem? audio = null)
        {
            _system = system ?? throw new ArgumentNullException(nameof(system));
            _audio = audio;

            // 订阅对话系统事件
            _system.OnDialogueStart += HandleDialogueStart;
            _system.OnNodeEnter += HandleNodeEnter;
            _system.OnDialogueEnd += HandleDialogueEnd;
        }

        /// <summary>绑定 UI 控件根面板与子控件。</summary>
        public void BindWidgets(PanelWidget rootPanel, TextWidget speakerNameText, PanelWidget choicePanel)
        {
            _rootPanel = rootPanel;
            _speakerNameText = speakerNameText;
            _choicePanel = choicePanel;
            if (_rootPanel != null && !_rootPanel.Children.Contains(_dialogueText))
                _rootPanel.AddChild(_dialogueText);
            Hide();
        }

        // ── 事件处理 ──

        private void HandleDialogueStart(string graphId)
        {
            Show();
        }

        private void HandleNodeEnter(DialogueNode node)
        {
            if (node == null) return;

            // 显示说话者名
            var speaker = _system.ActiveGraph?.GetSpeaker(node.SpeakerId);
            if (_speakerNameText != null)
                _speakerNameText.Text = speaker?.GetLocalizedDisplayName() ?? "";

            // 显示头像
            if (speaker != null && OnShowPortrait != null)
                OnShowPortrait(speaker.PortraitTexture, speaker.PortraitTint);

            // 启动打字机
            _fullText = node.GetLocalizedText();
            _displayedText.Clear();
            _charIndex = 0;
            _typewriterTimer = 0f;
            IsTyping = _fullText.Length > 0;
            UpdateDialogueTextWidget();

            // 清空并重建选项 (仅 Choice 节点)
            ClearChoices();
            if (node.Type == DialogueNodeType.Choice)
            {
                var choices = _system.GetAvailableChoices();
                BuildChoiceButtons(choices);
            }
        }

        private void HandleDialogueEnd()
        {
            Hide();
        }

        // ── 打字机更新 ──

        /// <summary>每帧更新打字机效果 (由游戏主循环调用)。</summary>
        public void Update(float dt)
        {
            if (!IsTyping || _rootPanel == null) return;
            _typewriterTimer += dt;
            while (_typewriterTimer >= TypewriterSpeed && _charIndex < _fullText.Length)
            {
                _typewriterTimer -= TypewriterSpeed;
                _displayedText.Append(_fullText[_charIndex]);
                _charIndex++;
            }
            UpdateDialogueTextWidget();
            if (_charIndex >= _fullText.Length)
                IsTyping = false;
        }

        /// <summary>立即显示全部文本 (跳过打字机)。</summary>
        public void SkipTypewriter()
        {
            if (!IsTyping) return;
            _displayedText.Clear();
            _displayedText.Append(_fullText);
            _charIndex = _fullText.Length;
            IsTyping = false;
            UpdateDialogueTextWidget();
        }

        /// <summary>更新对话框文本控件。</summary>
        private void UpdateDialogueTextWidget()
        {
            _dialogueText.Text = _displayedText.ToString();
            _dialogueText.MarkDirty();
        }

        // ── 选项按钮 ──

        /// <summary>构建选项按钮。</summary>
        private void BuildChoiceButtons(List<DialogueChoice> choices)
        {
            if (_choicePanel == null) return;
            for (int i = 0; i < choices.Count; i++)
            {
                var choice = choices[i];
                string text = choice.GetLocalizedText();
                TextWidget btn;
                if (CreateChoiceButton != null)
                {
                    btn = CreateChoiceButton(text, i);
                }
                else
                {
                    btn = new TextWidget
                    {
                        Name = $"Choice_{i}",
                        Text = $"{i + 1}. {text}",
                        Color = Color.White
                    };
                }
                int capturedIndex = i; // 闭包捕获
                _choiceButtons.Add(btn);
                _choicePanel.AddChild(btn);
            }
        }

        /// <summary>清空选项按钮。</summary>
        private void ClearChoices()
        {
            if (_choicePanel != null) _choicePanel.ClearChildren();
            _choiceButtons.Clear();
        }

        // ── 显示/隐藏 ──

        /// <summary>显示对话框。</summary>
        public void Show()
        {
            IsVisible = true;
            if (_rootPanel != null) _rootPanel.IsVisible = true;
        }

        /// <summary>隐藏对话框。</summary>
        public void Hide()
        {
            IsVisible = false;
            IsTyping = false;
            if (_rootPanel != null) _rootPanel.IsVisible = false;
        }

        /// <summary>处理玩家点击 (推进对话或完成打字机)。</summary>
        public void HandleClick()
        {
            if (!IsVisible) return;
            if (IsTyping)
            {
                SkipTypewriter();
                return;
            }
            // 非 Choice 节点点击则推进
            if (_system.CurrentNode != null && _system.CurrentNode.Type != DialogueNodeType.Choice)
            {
                _system.Advance();
            }
        }

        /// <summary>处理玩家选择选项。</summary>
        public void HandleChoiceSelect(int index)
        {
            _system.SelectChoice(index);
        }

        /// <summary>销毁控制器, 取消事件订阅。</summary>
        public void Dispose()
        {
            _system.OnDialogueStart -= HandleDialogueStart;
            _system.OnNodeEnter -= HandleNodeEnter;
            _system.OnDialogueEnd -= HandleDialogueEnd;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 12. 对话 ECS 更新系统 (DialogueUpdateSystem)
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 对话 ECS 更新系统。实现 <see cref="ISystem"/>, 每帧驱动
    /// <see cref="DialogueSystem.Update"/> 与 <see cref="DialogueUIController.Update"/>。
    /// </summary>
    public sealed class DialogueUpdateSystem : ISystem
    {
        /// <summary>系统名。</summary>
        public string Name => "DialogueSystem";

        private readonly DialogueSystem _system;
        private readonly DialogueUIController? _ui;

        /// <summary>创建系统。</summary>
        /// <param name="system">对话系统。</param>
        /// <param name="ui">UI 控制器 (可为 null)。</param>
        public DialogueUpdateSystem(DialogueSystem system, DialogueUIController? ui = null)
        {
            _system = system ?? throw new ArgumentNullException(nameof(system));
            _ui = ui;
        }

        /// <summary>每帧更新所有拥有 <see cref="DialogueComponent"/> 的实体。</summary>
        public void Update(float dt, EntityComponentSystem ecs)
        {
            if (ecs == null) return;
            // 更新对话系统核心
            _system.Update(dt);

            // 更新 UI 打字机
            _ui?.Update(dt);

            // 同步对话组件状态
            var entities = ecs.QueryWithComponent<DialogueComponent>();
            foreach (var entityId in entities)
            {
                var comp = ecs.GetComponent<DialogueComponent>(entityId);
                comp.InDialogue = _system.IsActive;
                comp.ActiveGraphId = _system.ActiveGraph?.Id ?? "";
                ecs.SetComponent(entityId, comp);
            }
        }
    }
}
