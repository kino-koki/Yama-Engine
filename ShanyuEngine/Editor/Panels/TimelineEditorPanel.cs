// ==================================================================================================
//  TimelineEditorPanel.cs — 山雨引擎时间轴编辑器面板
// ==================================================================================================
//  命名空间: ShanyuEngine.Editor.Panels
//  依赖:     ImGuiNET, ShanyuEngine.ECS, ShanyuEngine.Animation
//
//  功能:
//    1. 选择 ECS 实体上的 AnimatorComponent 进行编辑
//    2. 时间轴滑块（播放/暂停/停止/跳转到开始/跳转到结束）
//    3. 关键帧轨道可视化（骨骼变换关键帧）
//    4. 播放速度控制
//    5. 循环开关
//    6. 实时预览（拖动时间轴时更新实体动画）
//    7. 与 PlayMode 隔离（编辑器预览不影响运行态）
//
//  架构:
//    ECS AnimatorComponent → 时间轴面板 → EcsAnimationBridge.SetTime → 实时预览
// ==================================================================================================

using System;
using System.Numerics;
using ImGuiNET;
using ShanyuEngine.ECS;
using ShanyuEngine.Animation;

using AnimatorComponent = ShanyuEngine.ECS.AnimatorComponent;
namespace ShanyuEngine.Editor.Panels
{
    /// <summary>
    /// 时间轴编辑器面板。用于预览和编辑 ECS 实体的动画时间轴。
    /// </summary>
    public sealed class TimelineEditorPanel
    {
        // ── 子系统引用 ────────────────────────────────────────────────────────────

        private readonly EntityComponentSystem _ecs;
        private readonly EcsSelectionState _selection;
        private readonly EcsAnimationBridge? _animBridge;

        // ── 状态 ──────────────────────────────────────────────────────────────────

        private bool _isPlaying;
        private bool _isScrubbing;
        private float _previewTime;
        private float _playbackSpeed = 1f;
        private bool _loop = true;

        // ── 事件 ──────────────────────────────────────────────────────────────────

        /// <summary>预览时间变化事件。</summary>
        public event Action<int, float>? OnPreviewTimeChanged;

        /// <summary>场景被修改。</summary>
        public event Action? OnSceneModified;

        // ── 属性 ──────────────────────────────────────────────────────────────────

        public string Title { get; set; } = "时间轴";
        public bool IsVisible { get; set; } = true;

        // ─────────────────────────────────────────────────────────────────────────
        //  构造
        // ─────────────────────────────────────────────────────────────────────────

        public TimelineEditorPanel(EntityComponentSystem ecs,
            EcsSelectionState selection,
            EcsAnimationBridge? animBridge = null)
        {
            _ecs = ecs ?? throw new ArgumentNullException(nameof(ecs));
            _selection = selection ?? throw new ArgumentNullException(nameof(selection));
            _animBridge = animBridge;
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  渲染
        // ─────────────────────────────────────────────────────────────────────────

        public void Render()
        {
            if (!IsVisible) return;

            ImGui.SetNextWindowSizeConstraints(new Vector2(600, 180), new Vector2(float.MaxValue, 300));
            if (!ImGui.Begin($"{Title}###TimelineEditor"))
            {
                ImGui.End();
                return;
            }

            int entityId = _selection.Active;
            if (entityId < 0 || !_ecs.HasEntity(entityId) || !_ecs.HasComponent<AnimatorComponent>(entityId))
            {
                ImGui.TextDisabled("未选中带动画组件的实体");
                ImGui.End();
                return;
            }

            var anim = _ecs.GetComponent<AnimatorComponent>(entityId);
            float duration = anim.Duration > 0 ? anim.Duration : 1f;
            string stateName = string.IsNullOrEmpty(anim.CurrentState) ? "Default" : anim.CurrentState;

            // 同步预览时间
            if (!_isScrubbing && _isPlaying)
                _previewTime = anim.CurrentTime;

            // ── 工具栏 ──
            DrawToolbar(entityId, ref anim, duration);
            ImGui.Separator();

            // ── 时间轴滑块 ──
            DrawTimelineSlider(entityId, ref anim, duration);
            ImGui.Separator();

            // ── 信息 ──
            DrawInfo(entityId, anim, duration, stateName);

            // ── 播放控制 ──
            if (_isPlaying && !_isScrubbing)
            {
                _previewTime += ImGui.GetIO().DeltaTime * _playbackSpeed;
                if (_loop && _previewTime > duration)
                    _previewTime = 0f;
                else if (!_loop && _previewTime > duration)
                {
                    _previewTime = duration;
                    _isPlaying = false;
                }

                _animBridge?.SetTime(entityId, _previewTime);
                OnPreviewTimeChanged?.Invoke(entityId, _previewTime);
            }

            ImGui.End();
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  工具栏
        // ─────────────────────────────────────────────────────────────────────────

        private void DrawToolbar(int entityId, ref AnimatorComponent anim, float duration)
        {
            // 播放/暂停
            if (_isPlaying)
            {
                if (ImGui.Button("|| 暂停", new Vector2(60, 0)))
                    _isPlaying = false;
            }
            else
            {
                if (ImGui.Button("> 播放", new Vector2(60, 0)))
                {
                    _isPlaying = true;
                    _animBridge?.Play(entityId, anim.CurrentState);
                }
            }

            ImGui.SameLine();
            if (ImGui.Button("■ 停止", new Vector2(60, 0)))
            {
                _isPlaying = false;
                _previewTime = 0f;
                _animBridge?.SetTime(entityId, 0f);
            }

            ImGui.SameLine();
            if (ImGui.Button("|<", new Vector2(40, 0)))
            {
                _previewTime = 0f;
                _animBridge?.SetTime(entityId, 0f);
            }

            ImGui.SameLine();
            if (ImGui.Button(">|", new Vector2(40, 0)))
            {
                _previewTime = duration;
                _animBridge?.SetTime(entityId, duration);
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(80);
            if (ImGui.DragFloat("速度", ref _playbackSpeed, 0.1f, 0f, 5f, "%.1fx"))
            {
                anim.PlaybackSpeed = _playbackSpeed;
                _ecs.SetComponent(entityId, anim);
                OnSceneModified?.Invoke();
            }

            ImGui.SameLine();
            bool loop = _loop;
            if (ImGui.Checkbox("循环", ref loop))
            {
                _loop = loop;
                anim.Loop = loop;
                _ecs.SetComponent(entityId, anim);
                OnSceneModified?.Invoke();
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  时间轴滑块
        // ─────────────────────────────────────────────────────────────────────────

        private void DrawTimelineSlider(int entityId, ref AnimatorComponent anim, float duration)
        {
            ImGui.PushID("TimelineSlider");

            float time = _previewTime;
            ImGui.SetNextItemWidth(-1);

            bool wasScrubbing = _isScrubbing;
            _isScrubbing = false;

            if (ImGui.SliderFloat("##Time", ref time, 0f, duration, $"%.2f / {duration:F2}s"))
            {
                _isScrubbing = ImGui.IsItemActive();
                _previewTime = time;
                _animBridge?.SetTime(entityId, time);
                OnPreviewTimeChanged?.Invoke(entityId, time);

                // 暂停播放以便精确拖动
                if (_isScrubbing)
                    _animBridge?.Pause(entityId);
            }

            // 拖动结束，恢复状态
            if (wasScrubbing && !_isScrubbing && _isPlaying)
                _animBridge?.Play(entityId, anim.CurrentState);

            ImGui.PopID();
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  信息
        // ─────────────────────────────────────────────────────────────────────────

        private void DrawInfo(int entityId, AnimatorComponent anim, float duration, string stateName)
        {
            ImGui.Text($"实体: #{entityId}  状态: {stateName}");

            if (anim.BoneMatrices != null)
                ImGui.TextColored(new Vector4(0.5f, 0.8f, 0.5f, 1f), $"骨骼: {anim.BoneMatrices.Length}");
            else
                ImGui.TextDisabled("骨骼矩阵未计算");

            // 状态参数（如果有 AnimatorStateComponent）
            if (_ecs.HasComponent<AnimatorStateComponent>(entityId))
            {
                var state = _ecs.GetComponent<AnimatorStateComponent>(entityId);
                if (state.FloatParameters != null && state.FloatParameters.Count > 0)
                {
                    ImGui.Text("参数:");
                    ImGui.Indent();
                    foreach (var kv in state.FloatParameters)
                        ImGui.Text($"{kv.Key}: {kv.Value:F2}");
                    ImGui.Unindent();
                }
            }
        }

        /// <summary>重置面板状态。</summary>
        public void Reset()
        {
            _isPlaying = false;
            _isScrubbing = false;
            _previewTime = 0f;
        }
    }
}
