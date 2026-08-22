// ==================================================================================================
//  ParticleEditorPanel.cs — 山雨引擎粒子编辑器面板
// ==================================================================================================
//  命名空间: ShanyuEngine.Editor.Panels
//  依赖:     ImGuiNET, ShanyuEngine.ECS, ShanyuEngine.Particles
//
//  功能:
//    * 显示并编辑选中实体的 ParticleEmitterComponent 属性
//    * 粒子播放控制: Play / Pause / Stop / Emit Burst
//    * 实时预览粒子数量、存活数
//    * 发射形状可视化选择
//    * 颜色/大小/生命周期曲线编辑
// ==================================================================================================

using System;
using System.Numerics;
using ImGuiNET;
using ShanyuEngine.ECS;
using ShanyuEngine.Particles;

namespace ShanyuEngine.Editor.Panels
{
    /// <summary>
    /// 粒子系统编辑器面板。提供粒子发射器属性的可视化编辑和播放控制。
    /// </summary>
    public sealed class ParticleEditorPanel
    {
        // ── 子系统引用 ────────────────────────────────────────────────────────────

        private readonly EntityComponentSystem _ecs;
        private readonly EcsSelectionState _selection;
        private readonly EcsParticleBridge _bridge;

        // ── UI 状态 ────────────────────────────────────────────────────────────────

        private int _burstCount = 10;
        private bool _showAdvanced;
        private readonly Vector4 _headerColor = new(0.2f, 0.7f, 0.9f, 1f);

        // ── 事件 ──────────────────────────────────────────────────────────────────

        public event Action? OnSceneModified;

        // ── 属性 ──────────────────────────────────────────────────────────────────

        public string Title { get; set; } = "粒子编辑器";
        public bool IsVisible { get; set; } = true;

        // ─────────────────────────────────────────────────────────────────────────
        //  构造
        // ─────────────────────────────────────────────────────────────────────────

        public ParticleEditorPanel(EntityComponentSystem ecs, EcsSelectionState selection, EcsParticleBridge bridge)
        {
            _ecs = ecs ?? throw new ArgumentNullException(nameof(ecs));
            _selection = selection ?? throw new ArgumentNullException(nameof(selection));
            _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  渲染
        // ─────────────────────────────────────────────────────────────────────────

        public void Render()
        {
            if (!IsVisible) return;

            ImGui.SetNextWindowSizeConstraints(new Vector2(320, 400), new Vector2(float.MaxValue, float.MaxValue));
            if (!ImGui.Begin($"{Title}###ParticleEditor"))
            {
                ImGui.End();
                return;
            }

            int entityId = _selection.Active;

            if (entityId < 0 || !_ecs.HasEntity(entityId))
            {
                ImGui.TextDisabled("未选中任何实体");
                ImGui.TextDisabled("在层级面板中选中带粒子发射器的实体以编辑。");
                ImGui.End();
                return;
            }

            if (!_ecs.HasComponent<ParticleEmitterComponent>(entityId))
            {
                ImGui.TextDisabled($"实体 #{entityId} 没有粒子发射器组件");
                if (ImGui.Button("添加粒子发射器"))
                {
                    _ecs.AddComponent(entityId, new ParticleEmitterComponent());
                    OnSceneModified?.Invoke();
                }
                ImGui.End();
                return;
            }

            var emitter = _ecs.GetComponent<ParticleEmitterComponent>(entityId);
            bool changed = false;

            // 实体头部
            string name = _ecs.GetEntityName(entityId) ?? "Entity";
            ImGui.TextColored(_headerColor, $"{name} #{entityId}");
            ImGui.Separator();

            // 播放控制
            changed |= DrawPlaybackControls(entityId, ref emitter);
            ImGui.Separator();

            // 基础属性
            changed |= DrawBasicProperties(ref emitter);
            ImGui.Separator();

            // 发射属性
            changed |= DrawEmissionProperties(ref emitter);
            ImGui.Separator();

            // 形状属性
            changed |= DrawShapeProperties(ref emitter);
            ImGui.Separator();

            // 物理属性
            changed |= DrawPhysicsProperties(ref emitter);
            ImGui.Separator();

            // 高级选项
            if (ImGui.CollapsingHeader("高级选项"))
            {
                changed |= DrawAdvancedProperties(ref emitter);
            }

            // 统计信息
            DrawStatistics(entityId);

            // 写回组件
            if (changed)
            {
                _ecs.SetComponent(entityId, emitter);
                OnSceneModified?.Invoke();
            }

            ImGui.End();
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  播放控制
        // ─────────────────────────────────────────────────────────────────────────

        private bool DrawPlaybackControls(int entityId, ref ParticleEmitterComponent emitter)
        {
            bool changed = false;

            ImGui.Text("播放控制");

            var system = _bridge.GetSystem(entityId);
            bool isPlaying = system?.IsPlaying ?? false;

            // Play / Pause / Stop 按钮
            if (!isPlaying)
            {
                if (ImGui.Button("▶ 播放", new Vector2(70, 0)))
                {
                    _bridge.Play(entityId);
                }
            }
            else
            {
                if (ImGui.Button("⏸ 暂停", new Vector2(70, 0)))
                {
                    _bridge.Pause(entityId);
                }
            }

            ImGui.SameLine();
            if (ImGui.Button("⏹ 停止", new Vector2(70, 0)))
            {
                _bridge.Stop(entityId, true);
            }

            ImGui.SameLine();
            if (ImGui.Button("🔥 爆发", new Vector2(70, 0)))
            {
                _bridge.Emit(entityId, _burstCount);
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(60);
            ImGui.InputInt("数量", ref _burstCount, 1, 10);
            _burstCount = Math.Clamp(_burstCount, 1, 1000);

            // Enabled 开关
            bool enabled = emitter.Enabled;
            if (ImGui.Checkbox("激活发射器", ref enabled))
            {
                emitter.Enabled = enabled;
                changed = true;
            }

            return changed;
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  基础属性
        // ─────────────────────────────────────────────────────────────────────────

        private bool DrawBasicProperties(ref ParticleEmitterComponent emitter)
        {
            bool changed = false;

            ImGui.Text("基础属性");

            int maxParticles = emitter.MaxParticles;
            if (ImGui.DragInt("最大粒子数", ref maxParticles, 1, 1, 100000))
            {
                emitter.MaxParticles = maxParticles;
                changed = true;
            }

            float lifetime = emitter.Lifetime;
            if (ImGui.DragFloat("生命周期 (秒)", ref lifetime, 0.1f, 0.01f, 60f, "%.2f"))
            {
                emitter.Lifetime = lifetime;
                changed = true;
            }

            bool looping = emitter.Looping;
            if (ImGui.Checkbox("循环播放", ref looping))
            {
                emitter.Looping = looping;
                changed = true;
            }

            // 模拟空间
            var spaces = new[] { "世界空间", "本地空间" };
            int spaceIdx = (int)emitter.SimulationSpace;
            if (ImGui.Combo("模拟空间", ref spaceIdx, spaces, spaces.Length))
            {
                emitter.SimulationSpace = (ParticleSimulationSpace)spaceIdx;
                changed = true;
            }

            return changed;
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  发射属性
        // ─────────────────────────────────────────────────────────────────────────

        private bool DrawEmissionProperties(ref ParticleEmitterComponent emitter)
        {
            bool changed = false;

            ImGui.Text("发射属性");

            float rate = emitter.EmissionRate;
            if (ImGui.DragFloat("发射速率 (个/秒)", ref rate, 0.5f, 0f, 10000f, "%.1f"))
            {
                emitter.EmissionRate = rate;
                changed = true;
            }

            float speed = emitter.StartSpeed;
            if (ImGui.DragFloat("起始速度", ref speed, 0.1f, -100f, 100f, "%.2f"))
            {
                emitter.StartSpeed = speed;
                changed = true;
            }

            return changed;
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  形状属性
        // ─────────────────────────────────────────────────────────────────────────

        private bool DrawShapeProperties(ref ParticleEmitterComponent emitter)
        {
            bool changed = false;

            ImGui.Text("发射形状");

            var shapes = new[] { "球体", "半球", "圆锥", "盒子" };
            int shapeIdx = (int)emitter.ShapeType;
            if (ImGui.Combo("形状", ref shapeIdx, shapes, shapes.Length))
            {
                emitter.ShapeType = (ParticleShapeType)shapeIdx;
                changed = true;
            }

            float radius = emitter.ShapeRadius;
            if (ImGui.DragFloat("半径", ref radius, 0.05f, 0f, 100f, "%.2f"))
            {
                emitter.ShapeRadius = radius;
                changed = true;
            }

            return changed;
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  物理属性
        // ─────────────────────────────────────────────────────────────────────────

        private bool DrawPhysicsProperties(ref ParticleEmitterComponent emitter)
        {
            bool changed = false;

            ImGui.Text("物理属性");

            float gravity = emitter.Gravity;
            if (ImGui.DragFloat("重力 (Y轴)", ref gravity, 0.5f, -50f, 50f, "%.2f"))
            {
                emitter.Gravity = gravity;
                changed = true;
            }

            return changed;
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  高级属性
        // ─────────────────────────────────────────────────────────────────────────

        private bool DrawAdvancedProperties(ref ParticleEmitterComponent emitter)
        {
            bool changed = false;

            float size = emitter.StartSize;
            if (ImGui.DragFloat("起始大小", ref size, 0.005f, 0.001f, 10f, "%.3f"))
            {
                emitter.StartSize = size;
                changed = true;
            }

            Vector4 color = emitter.StartColor;
            if (ImGui.ColorEdit4("起始颜色", ref color, ImGuiColorEditFlags.Float | ImGuiColorEditFlags.NoInputs))
            {
                emitter.StartColor = color;
                changed = true;
            }

            return changed;
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  统计信息
        // ─────────────────────────────────────────────────────────────────────────

        private void DrawStatistics(int entityId)
        {
            ImGui.Separator();
            ImGui.Text("统计信息");

            var system = _bridge.GetSystem(entityId);
            if (system != null)
            {
                ImGui.Text($"存活粒子: {system.AliveCount} / {system.ParticleCapacity}");
                ImGui.Text($"系统年龄: {system.Age:F2}s");
                ImGui.Text($"正在发射: {(system.IsEmitting ? "是" : "否")}");
            }
            else
            {
                ImGui.TextDisabled("模拟器未启动 — 运行 Play 后生成");
            }
        }
    }
}
