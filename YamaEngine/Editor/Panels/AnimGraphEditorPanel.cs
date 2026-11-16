using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using YamaEngine.Animation;

namespace YamaEngine.Editor.Panels
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎 - 动画蓝图编辑器面板 (AnimGraphEditorPanel)
    //
    // 提供 UE5.8 深度级动画蓝图编辑器交互 UX:
    //   - 蓝图资产打开/新建（AnimationBlueprintAsset）
    //   - 状态图可视化（状态列表 + 转移列表 + 条件）
    //   - 参数面板（Float/Bool/Int/Trigger 参数编辑，驱动运行时）
    //   - 运行时预览（AnimationBlueprintRuntime.Build → Animator + AnimatorController，
    //                  SetFloat/SetBool/SetTrigger/TickPreview，显示当前状态）
    //   - 骨骼蒙皮预览状态
    //
    // 依赖 AnimationBlueprintAsset/AnimationBlueprintRuntime（Asset → Controller 工厂）。
    // 运行时求值（AnimatorController.Update → AnimationStateMachine 默认状态推进）已闭环，
    // 本面板提供编辑器交互层。
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>动画蓝图编辑器面板: 状态图 + 参数面板 + 运行时预览。</summary>
    public sealed class AnimGraphEditorPanel : EditorPanel
    {
        /// <summary>当前打开的动画蓝图资产（null = 未打开）。</summary>
        public AnimationBlueprintAsset? CurrentAsset { get; private set; }

        /// <summary>预览用 Animator（挂 Controller）。</summary>
        public Animator? PreviewAnimator { get; private set; }

        /// <summary>预览用 AnimatorController（由资产构建）。</summary>
        public AnimatorController? PreviewController { get; private set; }

        private readonly Dictionary<Guid, AnimationClip> _clips = new();
        private Func<Guid, AnimationClip> _resolveClip;
        private Skeleton? _skeleton;
        private string _newBlueprintName = "ABP_NewAnimBlueprint";
        private string _previewState = "";
        private float _previewDt = 1f / 60f;
        private bool _showParams = true;
        private bool _showStates = true;
        private bool _showTransitions = true;

        public AnimGraphEditorPanel(Skeleton? skeleton = null)
            : base("动画蓝图编辑器", "AnimGraphEditor")
        {
            CanClose = true;
            MinSize = new Vector2(380, 320);
            _skeleton = skeleton;
            _resolveClip = g => _clips.TryGetValue(g, out var c) ? c : null;
        }

        // ─────────────────────────── 资产操作 ───────────────────────────

        /// <summary>设置预览骨架。</summary>
        public void SetSkeleton(Skeleton skeleton) => _skeleton = skeleton;

        /// <summary>注册剪辑（供 Build 的 resolveClip 解析）。</summary>
        public void RegisterClip(Guid id, AnimationClip clip)
        {
            if (clip != null) _clips[id] = clip;
        }

        /// <summary>打开动画蓝图资产。</summary>
        public void OpenAsset(AnimationBlueprintAsset asset)
        {
            CurrentAsset = asset ?? throw new ArgumentNullException(nameof(asset));
            _previewState = "";
            PreviewController = null;
            PreviewAnimator = null;
        }

        /// <summary>新建动画蓝图资产并打开。</summary>
        public AnimationBlueprintAsset NewBlueprint(string name)
        {
            var asset = new AnimationBlueprintAsset { Name = name };
            OpenAsset(asset);
            return asset;
        }

        // ─────────────────────────── 运行时预览 ───────────────────────────

        /// <summary>从当前资产构建预览运行时（Animator + AnimatorController）。</summary>
        public void BuildPreview()
        {
            if (CurrentAsset == null || _skeleton == null)
            {
                PreviewController = null;
                PreviewAnimator = null;
                return;
            }
            PreviewController = AnimationBlueprintRuntime.Build(CurrentAsset, _skeleton, _resolveClip);
            PreviewAnimator = new Animator(_skeleton);
            PreviewAnimator.Controller = PreviewController;
        }

        public void SetFloat(string name, float value) => PreviewController?.SetFloat(name, value);
        public void SetBool(string name, bool value) => PreviewController?.SetBool(name, value);
        public void SetInt(string name, int value) => PreviewController?.SetInt(name, value);
        public void SetTrigger(string name) => PreviewController?.SetTrigger(name);

        /// <summary>推进预览一帧。</summary>
        public void TickPreview(float deltaTime)
        {
            if (PreviewAnimator == null) return;
            PreviewAnimator.Update(MathF.Max(0f, deltaTime));
            _previewState = PreviewAnimator.CurrentStateName;
        }

        /// <summary>当前预览状态名。</summary>
        public string PreviewState => PreviewAnimator?.CurrentStateName ?? "";

        /// <summary>状态数。</summary>
        public int StateCount => CurrentAsset?.States.Count ?? 0;

        /// <summary>转移数。</summary>
        public int TransitionCount => CurrentAsset?.Transitions.Count ?? 0;

        /// <summary>参数数。</summary>
        public int ParameterCount => CurrentAsset?.Parameters.Count ?? 0;

        // ─────────────────────────── 绘制 ───────────────────────────

        protected override void OnDraw(EditorContext ctx)
        {
            // 资产工具栏
            if (ImGui.Button("新建")) { NewBlueprint(_newBlueprintName); }
            ImGui.SameLine();
            ImGui.InputText("名称", ref _newBlueprintName, 128);
            ImGui.Separator();

            if (CurrentAsset == null)
            {
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), "未打开动画蓝图资产。");
                return;
            }

            ImGui.Text($"资产: {CurrentAsset.Name}");

            if (ImGui.Button("构建预览"))
            {
                BuildPreview();
            }

            // 参数面板
            if (ImGui.CollapsingHeader($"参数 ({ParameterCount})", ref _showParams))
            {
                DrawParameters();
            }

            // 状态图
            if (ImGui.CollapsingHeader($"状态 ({StateCount})", ref _showStates))
            {
                DrawStates();
            }

            // 转移
            if (ImGui.CollapsingHeader($"转移 ({TransitionCount})", ref _showTransitions))
            {
                DrawTransitions();
            }

            // 预览
            ImGui.Separator();
            if (PreviewAnimator != null)
            {
                ImGui.Text($"预览状态: {_previewState}");
                ImGui.InputFloat("步长", ref _previewDt, 0.001f, 0.05f);
                if (ImGui.Button("步进"))
                {
                    TickPreview(_previewDt);
                }
                ImGui.SameLine();
                if (ImGui.Button("播放"))
                {
                    TickPreview(_previewDt);
                }
            }
            else
            {
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), "尚未构建预览。");
            }
        }

        private void DrawParameters()
        {
            if (CurrentAsset == null) return;
            foreach (var p in CurrentAsset.Parameters)
            {
                switch (p.Type)
                {
                    case AnimatorParameterType.Float:
                        float f = PreviewController?.GetFloat(p.Name) ?? p.DefaultFloat;
                        if (ImGui.DragFloat(p.Name, ref f, 0.05f))
                        {
                            SetFloat(p.Name, f);
                        }
                        break;
                    case AnimatorParameterType.Bool:
                        bool b = PreviewController?.GetBool(p.Name) ?? p.DefaultBool;
                        if (ImGui.Checkbox(p.Name, ref b))
                        {
                            SetBool(p.Name, b);
                        }
                        break;
                    case AnimatorParameterType.Int:
                        int iv = PreviewController?.GetInt(p.Name) ?? p.DefaultInt;
                        if (ImGui.InputInt(p.Name, ref iv))
                        {
                            SetInt(p.Name, iv);
                        }
                        break;
                    case AnimatorParameterType.Trigger:
                        if (ImGui.Button($"触发 {p.Name}"))
                        {
                            SetTrigger(p.Name);
                        }
                        break;
                }
            }
        }

        private void DrawStates()
        {
            if (CurrentAsset == null) return;
            foreach (var s in CurrentAsset.States)
            {
                string kind = s.UseBlendTree ? "BlendTree" : "Clip";
                ImGui.Text($"[{kind}] {s.Name}  (speed={s.Speed:0.##})");
            }
        }

        private void DrawTransitions()
        {
            if (CurrentAsset == null) return;
            foreach (var t in CurrentAsset.Transitions)
            {
                string cond = "";
                if (t.Conditions.Count > 0)
                {
                    var c = t.Conditions[0];
                    cond = $"{c.Parameter} {ModeSymbol(c.Mode)} {c.Threshold}";
                }
                ImGui.Text($"{t.From} → {t.To}  ({cond})");
            }
        }

        private static string ModeSymbol(AnimatorConditionMode mode) => mode switch
        {
            AnimatorConditionMode.Greater => ">",
            AnimatorConditionMode.Less => "<",
            AnimatorConditionMode.Equals => "==",
            AnimatorConditionMode.NotEqual => "!=",
            AnimatorConditionMode.If => "if",
            AnimatorConditionMode.IfNot => "ifnot",
            _ => "?"
        };
    }
}
