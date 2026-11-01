using System;
using System.Numerics;

namespace YamaEngine.Cel2D
{
    /// <summary>
    /// 2D 相机跟随控制器：平滑跟随目标、死区、边界钳制、缩放与震动。
    /// 纯数据层：输出偏移给 <see cref="Cel2DCamera"/> 使用。
    /// </summary>
    public sealed class CelCameraFollow2D
    {
        /// <summary>跟随目标世界位置。</summary>
        public Vector2 Target { get; set; }

        /// <summary>当前相机中心（世界坐标）。字段：类内直接写回成员。</summary>
        public Vector2 Position;

        /// <summary>视口尺寸（像素）。</summary>
        public Vector2 ViewportSize { get; set; } = new Vector2(1280.0f, 720.0f);

        /// <summary>缩放（1 = 1:1 像素）。</summary>
        public float Zoom { get; set; } = 1.0f;

        /// <summary>跟随平滑（0..1，越大跟随越快；0 = 瞬移）。</summary>
        public float Smoothing { get; set; } = 0.12f;

        /// <summary>死区半宽（像素）。目标在死区内不移动相机。</summary>
        public float DeadZoneX { get; set; } = 64.0f;

        /// <summary>死区半高（像素）。</summary>
        public float DeadZoneY { get; set; } = 48.0f;

        /// <summary>是否启用死区。</summary>
        public bool UseDeadZone { get; set; } = true;

        /// <summary>是否启用边界钳制。</summary>
        public bool ClampToBounds { get; set; }

        /// <summary>世界边界（左 / 上 / 右 / 下）。仅在 ClampToBounds 时有效。</summary>
        public Rectangle2D WorldBounds { get; set; }

        /// <summary>是否在初始化时瞬移到目标。</summary>
        public bool SnapOnStart { get; set; } = true;

        /// <summary>当前震动强度（像素）。</summary>
        public float ShakeStrength { get; private set; }

        /// <summary>震动剩余时间（秒）。</summary>
        public float ShakeRemaining { get; private set; }

        /// <summary>震动持续时间（秒）。</summary>
        public float ShakeDuration { get; private set; }

        private float _shakeSeed;
        private bool _initialized;

        public CelCameraFollow2D() { }

        public CelCameraFollow2D(Vector2 viewportSize, Vector2 startPosition)
        {
            ViewportSize = viewportSize;
            Position = startPosition;
            Target = startPosition;
            _initialized = true;
        }

        /// <summary>初始化并（可选）瞬移到目标。</summary>
        public void Initialize(Vector2 worldBoundsMin, Vector2 worldBoundsMax)
        {
            WorldBounds = new Rectangle2D(worldBoundsMin, worldBoundsMax - worldBoundsMin);
            if (SnapOnStart)
            {
                Position = Target;
                _initialized = true;
            }
        }

        /// <summary>每帧推进相机逻辑。</summary>
        /// <param name="deltaTime">帧时间（秒）。</param>
        public void Update(float deltaTime)
        {
            if (deltaTime <= 0.0f)
                return;

            Vector2 desired = Target;

            if (UseDeadZone && !_initialized)
            {
                // 第一次启用死区：直接对齐。
                _initialized = true;
            }

            if (UseDeadZone)
            {
                // 死区：仅当目标越过死区边缘时才移动。
                float dx = desired.X - Position.X;
                float dy = desired.Y - Position.Y;
                float moveX = 0.0f;
                float moveY = 0.0f;

                if (dx > DeadZoneX)
                    moveX = dx - DeadZoneX;
                else if (dx < -DeadZoneX)
                    moveX = dx + DeadZoneX;

                if (dy > DeadZoneY)
                    moveY = dy - DeadZoneY;
                else if (dy < -DeadZoneY)
                    moveY = dy + DeadZoneY;

                Vector2 pull = new Vector2(moveX, moveY);
                float follow = MathF.Min(1.0f, Smoothing * deltaTime * 60.0f);
                Position += pull * follow;
            }
            else
            {
                // 直接平滑插值。
                float t = 1.0f - MathF.Pow(1.0f - Smoothing, deltaTime * 60.0f);
                Position = Vector2.Lerp(Position, desired, Math.Clamp(t, 0.0f, 1.0f));
            }

            if (ClampToBounds)
                ClampPosition();

            UpdateShake(deltaTime);
        }

        /// <summary>将相机钳制在世界边界内。</summary>
        public void ClampPosition()
        {
            if (!ClampToBounds || WorldBounds.Width <= 0.0f)
                return;

            float halfW = ViewportSize.X * 0.5f / Zoom;
            float halfH = ViewportSize.Y * 0.5f / Zoom;

            // 世界比视口大：钳制中心；否则居中。
            if (WorldBounds.Width >= halfW * 2.0f)
                Position.X = MathF.Max(WorldBounds.X + halfW, MathF.Min(WorldBounds.X + WorldBounds.Width - halfW, Position.X));
            else
                Position.X = WorldBounds.X + WorldBounds.Width * 0.5f;

            if (WorldBounds.Height >= halfH * 2.0f)
                Position.Y = MathF.Max(WorldBounds.Y + halfH, MathF.Min(WorldBounds.Y + WorldBounds.Height - halfH, Position.Y));
            else
                Position.Y = WorldBounds.Y + WorldBounds.Height * 0.5f;
        }

        /// <summary>触发一次相机震动。</summary>
        public void Shake(float strength, float duration)
        {
            ShakeStrength = strength;
            ShakeRemaining = duration;
            ShakeDuration = MathF.Max(duration, 0.0001f);
            _shakeSeed = (float)(new Random().NextDouble() * 1000.0);
        }

        private void UpdateShake(float deltaTime)
        {
            if (ShakeRemaining <= 0.0f)
            {
                ShakeStrength = 0.0f;
                return;
            }

            ShakeRemaining -= deltaTime;
            float t = MathF.Max(ShakeRemaining / MathF.Max(ShakeDuration, 0.0001f), 0.0f);
            float amp = ShakeStrength * t * t; // 二次衰减
            float time = (float)Environment.TickCount * 0.001f + _shakeSeed;
            Position += new Vector2(
                MathF.Sin(time * 31.7f) * amp,
                MathF.Cos(time * 27.3f) * amp);
        }

        /// <summary>重置相机到目标（瞬移）。</summary>
        public void SnapToTarget()
        {
            Position = Target;
            _initialized = true;
        }
    }

    /// <summary>
    /// 2D 矩形（X / Y 为左上角，W / H 为宽高）。
    /// </summary>
    public readonly struct Rectangle2D
    {
        public readonly float X;
        public readonly float Y;
        public readonly float Width;
        public readonly float Height;

        public Rectangle2D(float x, float y, float width, float height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        public Rectangle2D(Vector2 position, Vector2 size)
        {
            X = position.X;
            Y = position.Y;
            Width = size.X;
            Height = size.Y;
        }

        public Vector2 Position => new Vector2(X, Y);
        public Vector2 Size => new Vector2(Width, Height);
        public Vector2 Center => new Vector2(X + Width * 0.5f, Y + Height * 0.5f);
    }
}
