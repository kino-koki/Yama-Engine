// =============================================================================
//  Yama Engine - Sprite2D.cs
//  yama引擎 - 2D 精灵与相机节点（对应 Godot 的 Sprite2D / AnimatedSprite2D / Camera2D）
//
//  建立在 Node2D 层级树之上，提供 Godot 2D 的渲染与相机语义：
//    * Sprite2D：纹理 + 帧区域(region) + 翻转 + 偏移，变换来自 Node2D。
//    * AnimatedSprite2D：SpriteFrames（命名动画→帧列表）+ 播放/暂停/切帧。
//    * Camera2D：世界→屏幕投影 + 缩放 + 世界可见边界 + 当前相机切换。
// =============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace YamaEngine.UI
{
    /// <summary>
    /// 2D 精灵（对应 Godot Sprite2D）：纹理区域 + 翻转 + 偏移。
    /// 位置/旋转/缩放由继承的 Node2D 提供。
    /// </summary>
    public sealed class Sprite2D : Node2D
    {
        /// <summary>纹理路径（标识用，测试可空字符串）。</summary>
        public string TexturePath { get; set; } = string.Empty;

        /// <summary>纹理区域（源矩形，UV 或像素；默认整张）。</summary>
        public Rect2 RegionRect { get; set; } = new(0, 0, 1, 1);

        /// <summary>是否使用区域（false 时忽略 RegionRect）。</summary>
        public bool RegionEnabled { get; set; }

        /// <summary>水平翻转。</summary>
        public bool FlipH { get; set; }

        /// <summary>垂直翻转。</summary>
        public bool FlipV { get; set; }

        /// <summary>绘制偏移（相对节点原点，受 Node2D 变换影响）。</summary>
        public Vector2 Offset { get; set; } = Vector2.Zero;

        /// <summary>调制色（CanvasItem2D 的 Modulate 别名，精灵着色）。</summary>
        public Vector4 Tint { get => Modulate; set => Modulate = value; }

        public Sprite2D(string name = "Sprite2D") : base(name) { }

        /// <summary>精灵中心的世界坐标（含 Offset，经全局变换）。</summary>
        public Vector2 GetCenterGlobal()
            => ToGlobal(Offset);
    }

    /// <summary>2D 矩形（RegionRect 用）。</summary>
    public readonly struct Rect2
    {
        public float X { get; }
        public float Y { get; }
        public float Width { get; }
        public float Height { get; }

        public Rect2(float x, float y, float width, float height)
        {
            X = x; Y = y; Width = width; Height = height;
        }

        /// <summary>宽高为零视为空矩形。</summary>
        public bool IsEmpty => Width <= 0f || Height <= 0f;

        /// <summary>左上角（X,Y）。</summary>
        public Vector2 Position => new(X, Y);

        /// <summary>右下角（X+Width, Y+Height）。</summary>
        public Vector2 End => new(X + Width, Y + Height);

        /// <summary>中心点。</summary>
        public Vector2 Center => new(X + Width * 0.5f, Y + Height * 0.5f);
    }

    /// <summary>
    /// 单个动画帧（对应 Godot AtlasTexture / SpriteFrames 帧）。
    /// </summary>
    public sealed class SpriteFrame
    {
        /// <summary>源区域。</summary>
        public Rect2 Region { get; set; }

        /// <summary>帧时长（秒）。</summary>
        public float Duration { get; set; } = 0.1f;

        public SpriteFrame() { }
        public SpriteFrame(Rect2 region, float duration)
        {
            Region = region;
            Duration = duration;
        }
    }

    /// <summary>
    /// 精灵帧库（对应 Godot SpriteFrames）：命名动画 → 帧列表 + FPS。
    /// </summary>
    public sealed class SpriteFrames
    {
        private sealed class AnimDef
        {
            public List<SpriteFrame> Frames { get; } = new();
            public float Fps { get; set; } = 10f;
            public bool Loop { get; set; } = true;
        }

        private readonly Dictionary<string, AnimDef> _anims = new();

        /// <summary>动画数量。</summary>
        public int AnimationCount => _anims.Count;

        /// <summary>是否含指定动画。</summary>
        public bool HasAnimation(string name) => !string.IsNullOrEmpty(name) && _anims.ContainsKey(name);

        /// <summary>添加动画（重名覆盖）。</summary>
        public SpriteFrames AddAnimation(string name, float fps = 10f, bool loop = true)
        {
            _anims[name] = new AnimDef { Fps = MathF.Max(1e-3f, fps), Loop = loop };
            return this;
        }

        /// <summary>向动画追加一帧。</summary>
        public SpriteFrames AddFrame(string name, Rect2 region, float duration = 0.1f)
        {
            if (!_anims.TryGetValue(name, out var a))
                throw new KeyNotFoundException($"Animation '{name}' not in SpriteFrames.");
            a.Frames.Add(new SpriteFrame(region, duration));
            return this;
        }

        /// <summary>取动画帧列表（不存在返回 null）。</summary>
        internal List<SpriteFrame>? GetFrames(string name)
            => _anims.TryGetValue(name, out var a) ? a.Frames : null;

        /// <summary>取动画 FPS。</summary>
        internal float GetFps(string name)
            => _anims.TryGetValue(name, out var a) ? a.Fps : 0f;

        /// <summary>取动画是否循环。</summary>
        internal bool GetLoop(string name)
            => _anims.TryGetValue(name, out var a) && a.Loop;

        // ── 编辑器工作流扩展 ──

        /// <summary>列出所有动画名（稳定顺序）。</summary>
        public IReadOnlyList<string> GetAnimationNames() => _anims.Keys.ToList();

        /// <summary>移除动画（返回是否成功删除）。</summary>
        public bool RemoveAnimation(string name) => _anims.Remove(name);

        /// <summary>设置动画是否循环。</summary>
        public void SetLoop(string name, bool loop)
        {
            if (!_anims.TryGetValue(name, out var a))
                throw new KeyNotFoundException($"Animation '{name}' not in SpriteFrames.");
            a.Loop = loop;
        }

        /// <summary>设置动画 FPS。</summary>
        public void SetAnimationFps(string name, float fps)
        {
            if (!_anims.TryGetValue(name, out var a))
                throw new KeyNotFoundException($"Animation '{name}' not in SpriteFrames.");
            a.Fps = MathF.Max(1e-3f, fps);
        }

        /// <summary>取动画 FPS（不存在返回 0）。</summary>
        public float GetAnimationFps(string name) => GetFps(name);

        /// <summary>取动画是否循环（不存在返回 false）。</summary>
        public bool GetAnimationLoop(string name) => GetLoop(name);

        /// <summary>取动画帧数（不存在返回 0）。</summary>
        public int GetFrameCount(string name) => GetFrames(name)?.Count ?? 0;

        /// <summary>在指定位置插入一帧（对应 Godot add_frame 的 atPosition）。</summary>
        public SpriteFrames InsertFrame(string name, int index, Rect2 region, float duration = 0.1f)
        {
            if (!_anims.TryGetValue(name, out var a))
                throw new KeyNotFoundException($"Animation '{name}' not in SpriteFrames.");
            index = Math.Clamp(index, 0, a.Frames.Count);
            a.Frames.Insert(index, new SpriteFrame(region, duration));
            return this;
        }

        /// <summary>移除指定索引的帧。</summary>
        public void RemoveFrame(string name, int index)
        {
            if (!_anims.TryGetValue(name, out var a))
                throw new KeyNotFoundException($"Animation '{name}' not in SpriteFrames.");
            if (index < 0 || index >= a.Frames.Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            a.Frames.RemoveAt(index);
        }

        /// <summary>设置指定索引帧的区域。</summary>
        public void SetFrameRegion(string name, int index, Rect2 region)
        {
            var frames = GetFrames(name) ?? throw new KeyNotFoundException($"Animation '{name}' not in SpriteFrames.");
            if (index < 0 || index >= frames.Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            frames[index].Region = region;
        }

        /// <summary>取指定索引帧的区域。</summary>
        public Rect2 GetFrameRegion(string name, int index)
        {
            var frames = GetFrames(name) ?? throw new KeyNotFoundException($"Animation '{name}' not in SpriteFrames.");
            if (index < 0 || index >= frames.Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return frames[index].Region;
        }

        /// <summary>清空所有动画。</summary>
        public void Clear() => _anims.Clear();
    }

    /// <summary>精灵动画播放状态。</summary>
    public enum SpriteAnimState { Stopped, Playing, Paused }

    /// <summary>
    /// 动画精灵（对应 Godot AnimatedSprite2D）：播放 SpriteFrames 中的命名动画。
    /// </summary>
    public sealed class AnimatedSprite2D : Node2D
    {
        private readonly SpriteFrames _frames;
        private string _currentAnim = string.Empty;
        private SpriteAnimState _state = SpriteAnimState.Stopped;
        private float _time;
        private int _frameIndex;
        private int _finishedCount;

        /// <summary>当前动画名。</summary>
        public string Animation => _currentAnim;

        /// <summary>播放状态。</summary>
        public SpriteAnimState State => _state;

        /// <summary>当前帧索引。</summary>
        public int FrameIndex => _frameIndex;

        /// <summary>当前帧数（无动画为 0）。</summary>
        public int FrameCount => _frames.GetFrames(_currentAnim)?.Count ?? 0;

        /// <summary>播放速度倍率。</summary>
        public float SpeedScale { get; set; } = 1f;

        /// <summary>非循环动画播完次数（frame_changed 完成回调计数）。</summary>
        public int FinishedCount => _finishedCount;

        /// <summary>当前帧区域（无帧返回空 Rect2）。</summary>
        public Rect2 CurrentRegion
        {
            get
            {
                var frames = _frames.GetFrames(_currentAnim);
                if (frames == null || frames.Count == 0) return default;
                return frames[Math.Clamp(_frameIndex, 0, frames.Count - 1)].Region;
            }
        }

        public AnimatedSprite2D(SpriteFrames frames, string name = "AnimatedSprite2D") : base(name)
        {
            _frames = frames ?? throw new ArgumentNullException(nameof(frames));
        }

        /// <summary>播放指定动画（从头开始）。</summary>
        public void Play(string anim)
        {
            if (!_frames.HasAnimation(anim))
                throw new KeyNotFoundException($"Cannot play '{anim}': not in SpriteFrames.");
            _currentAnim = anim;
            _frameIndex = 0;
            _time = 0f;
            _state = SpriteAnimState.Playing;
        }

        /// <summary>暂停（保持帧）。</summary>
        public void Pause()
        {
            if (_state == SpriteAnimState.Playing) _state = SpriteAnimState.Paused;
        }

        /// <summary>停止（复位到 0 帧）。</summary>
        public void Stop()
        {
            _state = SpriteAnimState.Stopped;
            _frameIndex = 0;
            _time = 0f;
        }

        /// <summary>恢复播放。</summary>
        public void Resume()
        {
            if (_state == SpriteAnimState.Paused) _state = SpriteAnimState.Playing;
        }

        /// <summary>
        /// 每帧推进：按 FPS * SpeedScale 累积时间切帧。
        /// 循环动画回绕；非循环停在末帧并计一次 FinishedCount。
        /// </summary>
        public override void Update(float dt)
        {
            base.Update(dt);
            if (_state != SpriteAnimState.Playing) return;

            var frames = _frames.GetFrames(_currentAnim);
            if (frames == null || frames.Count == 0) return;

            float fps = _frames.GetFps(_currentAnim);
            float frameDur = 1f / fps;
            _time += dt * SpeedScale;

            while (_time >= frameDur)
            {
                _time -= frameDur;
                _frameIndex++;

                bool last = _frameIndex >= frames.Count;
                if (last)
                {
                    if (_frames.GetLoop(_currentAnim))
                    {
                        _frameIndex = 0;
                    }
                    else
                    {
                        _frameIndex = frames.Count - 1;
                        _state = SpriteAnimState.Stopped;
                        _finishedCount++;
                        return;
                    }
                }
            }
        }
    }

    /// <summary>
    /// 2D 相机（对应 Godot Camera2D）：世界→屏幕投影 + 缩放 + 世界边界 + 当前相机。
    /// 相机作为 Node2D 挂在场景树中，其全局变换决定观察中心。
    /// </summary>
    public sealed class Camera2D : Node2D
    {
        /// <summary>缩放倍率（>1 放大，视野变小）。</summary>
        public float Zoom { get; set; } = 1f;

        /// <summary>视口尺寸（像素，用于投影与边界计算）。</summary>
        public Vector2 ViewportSize { get; set; } = new(1920, 1080);

        /// <summary>是否启用位置边界限制。</summary>
        public bool LimitEnabled { get; set; }

        /// <summary>世界左边界。</summary>
        public float LimitLeft { get; set; }

        /// <summary>世界上边界。</summary>
        public float LimitTop { get; set; }

        /// <summary>世界右边界。</summary>
        public float LimitRight { get; set; }

        /// <summary>世界下边界。</summary>
        public float LimitBottom { get; set; }

        private static Camera2D? _current;

        // ── 平滑跟随（Godot position_smoothing_enabled + speed）──
        /// <summary>是否启用平滑跟随（对应 Godot position_smoothing_enabled）。</summary>
        public bool PositionSmoothingEnabled { get; set; }

        /// <summary>平滑跟随速度（对应 Godot position_smoothing_speed，越大跟随越快）。</summary>
        public float PositionSmoothingSpeed { get; set; } = 5f;

        private Vector2 _smoothTarget;

        // ── 拖拽边距（Godot drag_margin_* + drag_margin_enabled）──
        /// <summary>是否启用拖拽边距（对应 Godot drag_margin_enabled）。</summary>
        public bool DragMarginEnabled { get; set; }

        /// <summary>水平拖拽边距（相对视野半宽的比例，对应 Godot drag_margin_h）。</summary>
        public float DragMarginH { get; set; } = 0.2f;

        /// <summary>垂直拖拽边距（相对视野半高的比例，对应 Godot drag_margin_v）。</summary>
        public float DragMarginV { get; set; } = 0.2f;

        /// <summary>设置相机要平滑跟随到的世界坐标目标（对应 Godot set_target_position）。</summary>
        public void SetTargetPosition(Vector2 target) => _smoothTarget = target;

        /// <summary>获取当前平滑跟随目标。</summary>
        public Vector2 GetTargetPosition() => _smoothTarget;

        /// <summary>
        /// 计算考虑拖拽边距后的实际跟随目标。
        /// 目标落在中心±margin 区域内时返回当前中心（相机不动）；超出时把目标夹到 margin 边界。
        /// margin_h 半宽 = HalfWidth * DragMarginH，margin_v 半高 = HalfHeight * DragMarginV。
        /// </summary>
        public Vector2 ComputeDragTarget()
        {
            if (!DragMarginEnabled) return _smoothTarget;
            float mx = HalfWidth * DragMarginH;
            float my = HalfHeight * DragMarginV;
            var pos = GlobalPosition;
            float dx = _smoothTarget.X - pos.X;
            float dy = _smoothTarget.Y - pos.Y;
            // 拖拽边距死区语义：|offset| <= margin 时不动（有效 offset=0）；
            // 超出时只追到 margin 边界（有效 offset = offset - clamp(offset,-margin,margin)）
            dx -= Math.Clamp(dx, -mx, mx);
            dy -= Math.Clamp(dy, -my, my);
            return pos + new Vector2(dx, dy);
        }

        /// <summary>
        /// 每帧更新：平滑跟随（lerp 当前位置朝目标推进）。
        /// 公式：pos = lerp(pos, target, 1 - exp(-speed * dt))（帧率无关指数衰减）。
        /// drag_margin 启用时 target 先经 ComputeDragTarget 夹紧。
        /// 仅 PositionSmoothingEnabled=true 时生效。
        /// </summary>
        public void Update(float deltaTime)
        {
            if (!PositionSmoothingEnabled) return;
            var effectiveTarget = ComputeDragTarget();
            float t = 1f - MathF.Exp(-PositionSmoothingSpeed * deltaTime);
            var pos = GlobalPosition;
            GlobalPosition = Vector2.Lerp(pos, effectiveTarget, t);
        }

        public Camera2D(string name = "Camera2D") : base(name) { }

        /// <summary>设为当前相机。</summary>
        public void MakeCurrent() => _current = this;

        /// <summary>当前活动相机（未设置时为 null）。</summary>
        public static Camera2D? Current => _current;

        /// <summary>清空当前相机。</summary>
        public static void ClearCurrent() => _current = null;

        /// <summary>观察中心的世界坐标（相机全局位置）。</summary>
        public Vector2 GetCenter() => GlobalPosition;

        /// <summary>
        /// 世界→屏幕投影（对应 Godot 的 get_screen_center_position 与 viewport 变换，含相机旋转）。
        /// 屏幕 = rot(-rotation) * (world - center) * zoom + viewportCenter。
        /// </summary>
        public Vector2 WorldToScreen(Vector2 world)
        {
            var center = GetCenter();
            var d = world - center;
            d = Rotate(d, -Rotation); // 逆旋转相机旋转
            return d * Zoom + ViewportSize * 0.5f;
        }

        /// <summary>屏幕→世界反投影（含相机旋转）。</summary>
        public Vector2 ScreenToWorld(Vector2 screen)
        {
            var center = GetCenter();
            var d = (screen - ViewportSize * 0.5f) / Zoom;
            d = Rotate(d, Rotation); // 正旋转回世界
            return d + center;
        }

        private static Vector2 Rotate(Vector2 v, float radians)
        {
            float c = MathF.Cos(radians), s = MathF.Sin(radians);
            return new Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c);
        }

        /// <summary>视野半宽（世界单位）。</summary>
        public float HalfWidth => ViewportSize.X * 0.5f / Zoom;

        /// <summary>视野半高（世界单位）。</summary>
        public float HalfHeight => ViewportSize.Y * 0.5f / Zoom;

        /// <summary>
        /// 世界可见边界（考虑 LimitEnabled 时夹紧相机中心）。
        /// 返回 (minX, minY, maxX, maxY)。
        /// </summary>
        public (float minX, float minY, float maxX, float maxY) GetVisibleBounds()
        {
            var center = GetCenter();
            float cx = center.X, cy = center.Y;
            float hw = HalfWidth, hh = HalfHeight;

            if (LimitEnabled)
            {
                // 夹紧中心使视野不越界（仅在边界范围大于视野时有效）
                float minX = LimitLeft + hw, maxX = LimitRight - hw;
                if (maxX >= minX) cx = Math.Clamp(cx, minX, maxX);
                float minY = LimitTop + hh, maxY = LimitBottom - hh;
                if (maxY >= minY) cy = Math.Clamp(cy, minY, maxY);
            }

            return (cx - hw, cy - hh, cx + hw, cy + hh);
        }
    }
}
