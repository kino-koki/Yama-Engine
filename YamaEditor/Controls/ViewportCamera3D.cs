// =============================================================================
// ViewportCamera3D.cs - 编辑器视口 3D 相机
// 提供透视/正交投影、轨道/平移/缩放、WASD+QE 飞行控制。
// 计算 View 和 Projection 矩阵供渲染器使用。
// =============================================================================

using System;
using System.Numerics;

namespace YamaEditor.Controls
{
    /// <summary>
    /// 编辑器视口 3D 相机 —— 管理相机位置、朝向、投影方式，
    /// 并计算 View/Projection 矩阵供渲染管线使用。
    /// </summary>
    public sealed class ViewportCamera3D
    {
        // ── 相机参数 ──
        private Vector3 _position = new(0, 5, -10);
        private float _yaw = MathF.PI * 0.5f;   // 绕 Y 轴
        private float _pitch = -0.35f;           // 绕 X 轴
        private float _fov = 60.0f * MathF.PI / 180.0f;
        private float _aspectRatio = 16.0f / 9.0f;
        private float _nearPlane = 0.1f;
        private float _farPlane = 2000.0f;
        private float _orthoSize = 10.0f;
        private bool _isOrtho;

        // ── 轨道参数 ──
        private Vector3 _target = Vector3.Zero;
        private float _orbitDistance = 12.0f;

        // ── 缓存矩阵 ──
        private Matrix4x4 _viewMatrix;
        private Matrix4x4 _projMatrix;
        private Matrix4x4 _viewProjMatrix;
        private bool _dirty = true;

        // ── 常量 ──
        private const float MoveSpeed = 5.0f;
        private const float BoostMultiplier = 3.0f;
        private const float OrbitSensitivity = 0.005f;
        private const float PanSensitivity = 0.02f;
        private const float ZoomSensitivity = 0.005f;
        private const float MinDistance = 0.5f;
        private const float MaxDistance = 500.0f;

        /// <summary>相机世界位置。</summary>
        public Vector3 Position
        {
            get => _position;
            set { _position = value; _dirty = true; }
        }

        /// <summary>目标点（轨道中心）。</summary>
        public Vector3 Target
        {
            get => _target;
            set { _target = value; _dirty = true; }
        }

        /// <summary>偏航角（弧度，绕 Y 轴）。</summary>
        public float Yaw
        {
            get => _yaw;
            set { _yaw = value; _dirty = true; }
        }

        /// <summary>俯仰角（弧度，绕 X 轴）。</summary>
        public float Pitch
        {
            get => _pitch;
            set { _pitch = Math.Clamp(value, -MathF.PI * 0.49f, MathF.PI * 0.49f); _dirty = true; }
        }

        /// <summary>视场角（弧度）。</summary>
        public float FOV
        {
            get => _fov;
            set { _fov = value; _dirty = true; }
        }

        /// <summary>宽高比。</summary>
        public float AspectRatio
        {
            get => _aspectRatio;
            set { _aspectRatio = value; _dirty = true; }
        }

        /// <summary>近裁剪面。</summary>
        public float NearPlane
        {
            get => _nearPlane;
            set { _nearPlane = value; _dirty = true; }
        }

        /// <summary>远裁剪面。</summary>
        public float FarPlane
        {
            get => _farPlane;
            set { _farPlane = value; _dirty = true; }
        }

        /// <summary>正交模式下的半高尺寸。</summary>
        public float OrthoSize
        {
            get => _orthoSize;
            set { _orthoSize = value; _dirty = true; }
        }

        /// <summary>是否使用正交投影。</summary>
        public bool IsOrtho
        {
            get => _isOrtho;
            set { _isOrtho = value; _dirty = true; }
        }

        /// <summary>轨道距离。</summary>
        public float OrbitDistance
        {
            get => _orbitDistance;
            set { _orbitDistance = Math.Clamp(value, MinDistance, MaxDistance); _dirty = true; }
        }

        /// <summary>前方向（相机看向的方向）。</summary>
        public Vector3 Forward => Vector3.Normalize(new Vector3(
            MathF.Cos(_pitch) * MathF.Cos(_yaw),
            MathF.Sin(_pitch),
            MathF.Cos(_pitch) * MathF.Sin(_yaw)));

        /// <summary>右方向。</summary>
        public Vector3 Right => Vector3.Normalize(Vector3.Cross(Forward, Vector3.UnitY));

        /// <summary>上方向。</summary>
        public Vector3 Up => Vector3.Normalize(Vector3.Cross(Right, Forward));

        /// <summary>View 矩阵（世界 → 相机空间）。</summary>
        public Matrix4x4 ViewMatrix
        {
            get
            {
                if (_dirty) UpdateMatrices();
                return _viewMatrix;
            }
        }

        /// <summary>Projection 矩阵（相机 → 裁剪空间）。</summary>
        public Matrix4x4 ProjectionMatrix
        {
            get
            {
                if (_dirty) UpdateMatrices();
                return _projMatrix;
            }
        }

        /// <summary>View * Projection 组合矩阵。</summary>
        public Matrix4x4 ViewProjectionMatrix
        {
            get
            {
                if (_dirty) UpdateMatrices();
                return _viewProjMatrix;
            }
        }

        /// <summary>构造函数。</summary>
        public ViewportCamera3D()
        {
            UpdateMatrices();
        }

        // ══════════════════════════════════════════════════════════
        //  矩阵更新
        // ══════════════════════════════════════════════════════════

        private void UpdateMatrices()
        {
            // 从轨道参数推导位置
            Vector3 dir = new(
                _orbitDistance * MathF.Cos(_pitch) * MathF.Cos(_yaw),
                _orbitDistance * MathF.Sin(_pitch),
                _orbitDistance * MathF.Cos(_pitch) * MathF.Sin(_yaw));
            _position = _target + dir;

            // View 矩阵
            _viewMatrix = Matrix4x4.CreateLookAt(_position, _target, Vector3.UnitY);

            // Projection 矩阵
            if (_isOrtho)
            {
                float halfH = _orthoSize;
                float halfW = halfH * _aspectRatio;
                _projMatrix = Matrix4x4.CreateOrthographicOffCenter(
                    -halfW, halfW, -halfH, halfH, _nearPlane, _farPlane);
            }
            else
            {
                _projMatrix = Matrix4x4.CreatePerspectiveFieldOfView(
                    _fov, _aspectRatio, _nearPlane, _farPlane);
            }

            _viewProjMatrix = _viewMatrix * _projMatrix;
            _dirty = false;
        }

        // ══════════════════════════════════════════════════════════
        //  相机控制
        // ══════════════════════════════════════════════════════════

        /// <summary>轨道旋转（右键拖拽）。</summary>
        public void Orbit(float deltaX, float deltaY)
        {
            _yaw += deltaX * OrbitSensitivity;
            _pitch -= deltaY * OrbitSensitivity;
            _pitch = Math.Clamp(_pitch, -MathF.PI * 0.49f, MathF.PI * 0.49f);
            _dirty = true;
        }

        /// <summary>平移（中键拖拽）。</summary>
        public void Pan(float deltaX, float deltaY)
        {
            float scale = PanSensitivity * _orbitDistance;
            Vector3 pan = (Right * -deltaX + Up * deltaY) * scale;
            _target += pan;
            _dirty = true;
        }

        /// <summary>缩放（滚轮）。</summary>
        public void Zoom(float delta)
        {
            _orbitDistance *= 1.0f + delta * ZoomSensitivity;
            _orbitDistance = Math.Clamp(_orbitDistance, MinDistance, MaxDistance);
            if (_isOrtho)
            {
                _orthoSize *= 1.0f + delta * ZoomSensitivity;
                _orthoSize = Math.Max(0.1f, _orthoSize);
            }
            _dirty = true;
        }

        /// <summary>飞行移动（WASD + QE）。</summary>
        public void Fly(Vector3 direction, float deltaTime, bool boost)
        {
            if (direction.LengthSquared() < 0.001f) return;

            float speed = MoveSpeed * deltaTime * (boost ? BoostMultiplier : 1f);
            direction = Vector3.Normalize(direction);

            // 移动目标和位置（保持轨道距离）
            Vector3 move = (Right * direction.X + Up * direction.Y + Forward * -direction.Z) * speed;
            _target += move;
            _dirty = true;
        }

        /// <summary>设置预设视角。</summary>
        public void LookFrom(string view)
        {
            switch (view.ToUpperInvariant())
            {
                case "TOP":
                    _pitch = -MathF.PI * 0.49f;
                    _yaw = 0;
                    break;
                case "FRONT":
                    _pitch = 0;
                    _yaw = MathF.PI * 0.5f;
                    break;
                case "LEFT":
                    _pitch = 0;
                    _yaw = 0;
                    break;
                case "PERSPECTIVE":
                    _pitch = -0.35f;
                    _yaw = MathF.PI * 0.5f;
                    break;
            }
            _dirty = true;
        }

        /// <summary>重置到默认位置。</summary>
        public void Reset()
        {
            _target = Vector3.Zero;
            _yaw = MathF.PI * 0.5f;
            _pitch = -0.35f;
            _orbitDistance = 12.0f;
            _isOrtho = false;
            _dirty = true;
        }

        /// <summary>更新宽高比。</summary>
        public void SetViewportSize(int width, int height)
        {
            _aspectRatio = height > 0 ? (float)width / height : 1.0f;
            _dirty = true;
        }

        // ══════════════════════════════════════════════════════════
        //  坐标变换工具
        // ══════════════════════════════════════════════════════════

        /// <summary>世界坐标 → NDC 坐标。</summary>
        public Vector3 WorldToNDC(Vector3 worldPos)
        {
            Vector4 clip = Vector4.Transform(new Vector4(worldPos, 1.0f), ViewProjectionMatrix);
            if (Math.Abs(clip.W) < 1e-6f) return Vector3.Zero;
            return new Vector3(clip.X / clip.W, clip.Y / clip.W, clip.Z / clip.W);
        }

        /// <summary>世界坐标 → 屏幕像素坐标。</summary>
        public Vector2 WorldToScreen(Vector3 worldPos, int screenW, int screenH)
        {
            Vector3 ndc = WorldToNDC(worldPos);
            return new Vector2(
                (ndc.X + 1) * 0.5f * screenW,
                (1 - ndc.Y) * 0.5f * screenH);
        }

        /// <summary>屏幕像素 → 世界射线方向。</summary>
        public (Vector3 origin, Vector3 dir) ScreenToWorldRay(float screenX, float screenY, int screenW, int screenH)
        {
            float x = 2.0f * screenX / screenW - 1.0f;
            float y = 1.0f - 2.0f * screenY / screenH;

            Matrix4x4 invViewProj;
            if (!Matrix4x4.Invert(ViewProjectionMatrix, out invViewProj))
                return (_position, Forward);

            Vector4 nearPoint = Vector4.Transform(new Vector4(x, y, 0, 1), invViewProj);
            Vector4 farPoint = Vector4.Transform(new Vector4(x, y, 1, 1), invViewProj);

            if (Math.Abs(nearPoint.W) < 1e-6f || Math.Abs(farPoint.W) < 1e-6f)
                return (_position, Forward);

            Vector3 near = new(nearPoint.X / nearPoint.W, nearPoint.Y / nearPoint.W, nearPoint.Z / nearPoint.W);
            Vector3 far = new(farPoint.X / farPoint.W, farPoint.Y / farPoint.W, farPoint.Z / farPoint.W);

            return (_position, Vector3.Normalize(far - near));
        }
    }
}
