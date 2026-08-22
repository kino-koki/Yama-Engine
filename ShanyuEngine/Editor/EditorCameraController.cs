// ==================================================================================================
//  EditorCameraController.cs — 编辑器相机控制器 (全功能实现)
//
//  命名空间: ShanyuEngine.Editor
//  依赖:     System.Numerics
//
//  功能:
//    类似 Unity / Unreal Engine 视口相机的完整编辑器相机系统:
//    - CameraMode 枚举: Orbit / Fly / Focus / Zoom
//    - EditorCamera 完整类: 位置/朝向/欧拉角/FOV/裁剪面/正交参数
//    - 交互方法: OrbitRotate / Pan / Zoom / FlyForward / FlyStrafe / FlyVertical / FocusOn / LookAt
//    - 矩阵计算: ViewMatrix / ProjectionMatrix (透视 + 正交, 右手坐标系)
//    - 平滑阻尼 Update: 位置和旋转的指数衰减平滑
//    - 轨道目标 (OrbitTarget) / 轨道距离 (OrbitDistance)
//    - 屏幕坐标 <-> 世界坐标射线互转
//    - 快捷预设视角 (顶/底/前/后/左/右)
//
//  技术要点:
//    - 使用右手坐标系 (D3D12 标准, Z 朝屏幕外为正)
//    - 平滑阻尼使用指数衰减: lerp(current, target, 1 - exp(-factor * dt))
//    - Pitch 限制在 (-PI/2 + epsilon, PI/2 - epsilon) 防止万向锁
//    - 正交模式下 Zoom 调整 OrthographicWidth 而非 FOV
//    - 支持从外部直接设置目标位置/旋转, 内部平滑跟踪
// ==================================================================================================

using System;
using System.Numerics;

namespace ShanyuEngine.Editor
{
    // ════════════════════════════════════════════════════════════
    //  CameraMode — 编辑器相机控制模式枚举
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 编辑器相机控制模式。
    /// </summary>
    public enum CameraMode
    {
        /// <summary>轨道模式: 围绕焦点旋转, 滚轮缩放距离 (默认, 类似 Blender 中键)。</summary>
        Orbit,
        /// <summary>飞行模式: WASD 移动 + 鼠标转向 (类似 FPS)。</summary>
        Fly,
        /// <summary>聚焦模式: 平滑过渡到目标对象 (过渡完成后自动切回 Orbit)。</summary>
        Focus,
        /// <summary>缩放模式: 仅调整视口缩放 (FOV / OrthoWidth), 不改变位置。</summary>
        Zoom
    }

    // ════════════════════════════════════════════════════════════
    //  EditorCamera — 编辑器相机 (核心数据)
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 编辑器相机核心数据。
    /// 封装位置、朝向、欧拉角、投影参数, 是 EditorCameraController 的数据载体。
    /// 所有属性均为 public 字段以便编辑器 Inspector 直接修改。
    /// </summary>
    public sealed class EditorCamera
    {
        // ── 位置与朝向 ──

        /// <summary>相机世界位置。</summary>
        public Vector3 Position = new Vector3(0.0f, 5.0f, 10.0f);

        /// <summary>相机前向向量 (看向的方向, 单位向量)。</summary>
        public Vector3 Forward = new Vector3(0.0f, -0.45f, -0.89f);

        /// <summary>相机上向量 (单位向量)。</summary>
        public Vector3 Up = Vector3.UnitY;

        /// <summary>相机右向量 (单位向量)。</summary>
        public Vector3 Right = Vector3.UnitX;

        // ── 欧拉角 (弧度) ──

        /// <summary>偏航角 Yaw (绕 Y 轴旋转, 弧度)。</summary>
        public float Yaw = -0.45f;

        /// <summary>俯仰角 Pitch (绕 X 轴旋转, 弧度)。</summary>
        public float Pitch = -0.45f;

        /// <summary>翻滚角 Roll (绕 Z 轴旋转, 弧度, 编辑器通常为 0)。</summary>
        public float Roll = 0.0f;

        // ── 投影参数 ──

        /// <summary>视场角 (度, 默认 60)。</summary>
        public float FieldOfView = 60.0f;

        /// <summary>近裁剪面 (默认 0.1)。</summary>
        public float NearClip = 0.1f;

        /// <summary>远裁剪面 (默认 10000)。</summary>
        public float FarClip = 10000.0f;

        /// <summary>正交模式宽度 (仅 IsOrthographic=true 时生效)。</summary>
        public float OrthographicWidth = 20.0f;

        /// <summary>是否为正交投影。</summary>
        public bool IsOrthographic = false;

        // ── 运动参数 ──

        /// <summary>移动速度 (单位/秒, 默认 10)。</summary>
        public float MoveSpeed = 10.0f;

        /// <summary>旋转灵敏度 (默认 2)。</summary>
        public float RotateSpeed = 2.0f;

        /// <summary>缩放灵敏度 (默认 5)。</summary>
        public float ZoomSpeed = 5.0f;

        /// <summary>平滑因子 (指数衰减系数, 默认 8, 越大越灵敏)。</summary>
        public float SmoothFactor = 8.0f;

        // ── 轨道参数 ──

        /// <summary>轨道中心点 (Orbit 模式下相机围绕此点旋转)。</summary>
        public Vector3 OrbitTarget = Vector3.Zero;

        /// <summary>到 OrbitTarget 的距离 (默认 5)。</summary>
        public float OrbitDistance = 12.0f;

        // ── 宽高比 ──

        /// <summary>视口宽高比 (由控制器在 Resize 时更新)。</summary>
        public float Aspect = 1.777f; // 16:9

        // ── 方法 ──

        /// <summary>根据当前欧拉角重新计算 Forward / Right / Up。</summary>
        public void RecalculateVectors()
        {
            float cosPitch = MathF.Cos(Pitch);
            float sinPitch = MathF.Sin(Pitch);
            float cosYaw = MathF.Cos(Yaw);
            float sinYaw = MathF.Sin(Yaw);
            float cosRoll = MathF.Cos(Roll);
            float sinRoll = MathF.Sin(Roll);

            // 基础前向 (无 Roll): Yaw 绕 Y 轴, Pitch 绕 X 轴
            Forward = new Vector3(
                cosPitch * sinYaw,
                sinPitch,
                cosPitch * cosYaw
            );
            Forward = Vector3.Normalize(Forward);

            // 右向量: Forward x Up(0,1,0)
            Right = Vector3.Normalize(Vector3.Cross(Forward, Vector3.UnitY));

            // 应用 Roll 后的上向量
            Vector3 baseUp = Vector3.Normalize(Vector3.Cross(Right, Forward));
            // Roll 旋转: 绕 Forward 轴旋转 Up
            Up = baseUp * cosRoll + Vector3.Cross(Forward, baseUp) * sinRoll + Forward * Vector3.Dot(Forward, baseUp) * (1 - cosRoll);
            Up = Vector3.Normalize(Up);

            // 重新计算 Right (应用 Roll 后)
            Right = Vector3.Normalize(Vector3.Cross(Forward, Up));
        }

        /// <summary>从 Forward 向量提取 Yaw 和 Pitch (重写欧拉角)。</summary>
        public void ExtractEulerAnglesFromForward()
        {
            Forward = Vector3.Normalize(Forward);
            Pitch = MathF.Asin(Math.Clamp(Forward.Y, -1.0f, 1.0f));
            float horizontalLength = MathF.Sqrt(Forward.X * Forward.X + Forward.Z * Forward.Z);
            if (horizontalLength > 1e-6f)
                Yaw = MathF.Atan2(Forward.X, Forward.Z);
            else
                Yaw = 0.0f;
        }

        /// <summary>从欧拉角提取 Forward 向量 (不改变 Position)。</summary>
        public void ExtractForwardFromEuler()
        {
            float cosPitch = MathF.Cos(Pitch);
            float sinPitch = MathF.Sin(Pitch);
            float cosYaw = MathF.Cos(Yaw);
            float sinYaw = MathF.Sin(Yaw);
            Forward = Vector3.Normalize(new Vector3(
                cosPitch * sinYaw,
                sinPitch,
                cosPitch * cosYaw
            ));
        }
    }

    // ════════════════════════════════════════════════════════════
    //  EditorCameraController — 编辑器相机控制器 (交互 + 更新)
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 编辑器相机控制器: 接收编辑器 UI 的鼠标/键盘事件,
    /// 驱动 EditorCamera 的位置/旋转/投影更新。
    /// 所有交互方法均为无状态调用 (由外部事件驱动), Update() 在每帧执行平滑插值。
    /// </summary>
    public sealed class EditorCameraController : IDisposable
    {
        // ── 常量 ──

        /// <summary>Pitch 限制 (防止万向锁)。</summary>
        private const float PitchLimit = MathF.PI / 2.0f - 0.01f;

        /// <summary>OrbitDistance 最小值。</summary>
        private const float MinOrbitDistance = 0.1f;

        /// <summary>OrbitDistance 最大值。</summary>
        private const float MaxOrbitDistance = 10000.0f;

        /// <summary>FOV 范围 (度)。</summary>
        private const float MinFOV = 1.0f;
        private const float MaxFOV = 179.0f;

        /// <summary>正交宽度范围。</summary>
        private const float MinOrthoWidth = 0.01f;
        private const float MaxOrthoWidth = 100000.0f;

        /// <summary>微小值阈值。</summary>
        private const float Epsilon = 1e-6f;

        /// <summary>PI 常量。</summary>
        private const float PI = MathF.PI;

        // ── 相机实例 ──

        /// <summary>内部相机数据。</summary>
        private readonly EditorCamera _camera;

        /// <summary>公开相机数据引用。</summary>
        public EditorCamera Camera => _camera;

        // ── 平滑阻尼目标 ──

        private Vector3 _targetPosition;
        private float _targetYaw;
        private float _targetPitch;
        private float _targetRoll;
        private float _targetOrbitDistance;
        private Vector3 _targetOrbitTarget;

        // ── 控制模式 ──

        private CameraMode _mode = CameraMode.Orbit;

        /// <summary>当前控制模式。</summary>
        public CameraMode Mode
        {
            get => _mode;
            set
            {
                if (_mode == CameraMode.Focus && value != CameraMode.Focus)
                {
                    // 聚焦完成, 切换到目标模式
                }
                _mode = value;
            }
        }

        // ── 聚焦过渡状态 ──

        private bool _isFocusing;
        private float _focusProgress;
        private float _focusDuration = 0.5f;
        private Vector3 _focusStartPos;
        private Vector3 _focusEndPos;
        private Vector3 _focusStartTarget;
        private Vector3 _focusEndTarget;
        private float _focusStartDistance;
        private float _focusEndDistance;

        // ── 缩放状态 ──

        private float _targetFOV;
        private float _targetOrthoWidth;

        // ── 构造 ──

        /// <summary>创建编辑器相机控制器, 使用默认参数。</summary>
        public EditorCameraController()
        {
            _camera = new EditorCamera();
            _camera.RecalculateVectors();

            // 初始化目标值
            _targetPosition = _camera.Position;
            _targetYaw = _camera.Yaw;
            _targetPitch = _camera.Pitch;
            _targetRoll = _camera.Roll;
            _targetOrbitDistance = _camera.OrbitDistance;
            _targetOrbitTarget = _camera.OrbitTarget;
            _targetFOV = _camera.FieldOfView;
            _targetOrthoWidth = _camera.OrthographicWidth;
        }

        /// <summary>创建编辑器相机控制器, 使用指定相机实例。</summary>
        public EditorCameraController(EditorCamera camera)
        {
            _camera = camera ?? throw new ArgumentNullException(nameof(camera));
            _camera.RecalculateVectors();

            _targetPosition = _camera.Position;
            _targetYaw = _camera.Yaw;
            _targetPitch = _camera.Pitch;
            _targetRoll = _camera.Roll;
            _targetOrbitDistance = _camera.OrbitDistance;
            _targetOrbitTarget = _camera.OrbitTarget;
            _targetFOV = _camera.FieldOfView;
            _targetOrthoWidth = _camera.OrthographicWidth;
        }

        // ════════════════════════════════════════════════════════
        //  交互方法 (由编辑器 UI 鼠标/键盘事件调用)
        // ════════════════════════════════════════════════════════

        /// <summary>
        /// 轨道旋转: 鼠标拖拽产生的 delta 旋转相机。
        /// 在 Orbit 模式下围绕 OrbitTarget 旋转, 在 Fly 模式下原地旋转。
        /// </summary>
        /// <param name="deltaX">鼠标水平偏移 (像素)。</param>
        /// <param name="deltaY">鼠标垂直偏移 (像素)。</param>
        public void OrbitRotate(float deltaX, float deltaY)
        {
            if (_isFocusing) return;

            float sens = _camera.RotateSpeed * 0.01f;

            // Yaw: 水平拖拽改变偏航
            _targetYaw += deltaX * sens;

            // Pitch: 垂直拖拽改变俯仰, 限制范围
            _targetPitch += deltaY * sens;
            _targetPitch = Math.Clamp(_targetPitch, -PitchLimit, PitchLimit);
        }

        /// <summary>
        /// 平移: 沿相机 Right 和 Up 方向移动相机及轨道目标。
        /// 偏移量受距离影响 (远处拖拽移动更快)。
        /// </summary>
        /// <param name="deltaX">鼠标水平偏移 (像素)。</param>
        /// <param name="deltaY">鼠标垂直偏移 (像素)。</param>
        public void Pan(float deltaX, float deltaY)
        {
            if (_isFocusing) return;

            // 计算相机当前右向量和上向量
            Vector3 right = _camera.Right;
            Vector3 up = _camera.Up;

            // 偏移量受当前轨道距离影响
            float distanceFactor = _camera.OrbitDistance * 0.002f;
            float panSens = _camera.MoveSpeed * distanceFactor;

            Vector3 offset = right * deltaX * panSens + up * deltaY * panSens;

            // 同时移动 Position 和 OrbitTarget
            _targetPosition += offset;
            _targetOrbitTarget += offset;
        }

        /// <summary>
        /// 缩放: 根据当前投影模式调整缩放。
        /// - Orbit 模式: 调整 OrbitDistance (距离)
        /// - 透视模式: 调整 FOV (视场角)
        /// - 正交模式: 调整 OrthographicWidth (正交宽度)
        /// </summary>
        /// <param name="delta">滚轮增量 (正值 = 放大/拉近, 负值 = 缩小/拉远)。</param>
        public void Zoom(float delta)
        {
            if (_isFocusing) return;

            float zoomDelta = delta * _camera.ZoomSpeed * 0.001f;

            if (Mode == CameraMode.Orbit)
            {
                // Orbit 模式: 调整距离
                _targetOrbitDistance *= (1.0f - zoomDelta);
                _targetOrbitDistance = Math.Clamp(_targetOrbitDistance, MinOrbitDistance, MaxOrbitDistance);
            }
            else if (_camera.IsOrthographic)
            {
                // 正交模式: 调整正交宽度
                _targetOrthoWidth *= (1.0f - zoomDelta);
                _targetOrthoWidth = Math.Clamp(_targetOrthoWidth, MinOrthoWidth, MaxOrthoWidth);
            }
            else
            {
                // 透视模式: 调整 FOV
                _targetFOV *= (1.0f - zoomDelta * 0.5f);
                _targetFOV = Math.Clamp(_targetFOV, MinFOV, MaxFOV);
            }
        }

        /// <summary>
        /// 飞行模式前进: 沿相机 Forward 方向移动。
        /// </summary>
        /// <param name="amount">移动量 (正值 = 前进, 负值 = 后退)。</param>
        public void FlyForward(float amount)
        {
            if (_isFocusing) return;
            _targetPosition += _camera.Forward * amount * _camera.MoveSpeed;
        }

        /// <summary>
        /// 飞行模式横移: 沿相机 Right 方向移动。
        /// </summary>
        /// <param name="amount">移动量 (正值 = 右移, 负值 = 左移)。</param>
        public void FlyStrafe(float amount)
        {
            if (_isFocusing) return;
            _targetPosition += _camera.Right * amount * _camera.MoveSpeed;
        }

        /// <summary>
        /// 飞行模式升降: 沿世界 Y 轴移动。
        /// </summary>
        /// <param name="amount">移动量 (正值 = 上升, 负值 = 下降)。</param>
        public void FlyVertical(float amount)
        {
            if (_isFocusing) return;
            _targetPosition += Vector3.UnitY * amount * _camera.MoveSpeed;
        }

        /// <summary>
        /// 聚焦到目标点: 平滑移动相机使目标点位于视口中央。
        /// 自动计算最佳距离和朝向。
        /// </summary>
        /// <param name="target">目标世界坐标。</param>
        public void FocusOn(Vector3 target)
        {
            // 计算当前到目标的距离
            Vector3 toTarget = target - _camera.Position;
            float distance = toTarget.Length();
            if (distance < Epsilon)
                distance = _camera.OrbitDistance;

            // 设置聚焦过渡参数
            _focusStartPos = _camera.Position;
            _focusEndPos = target - _camera.Forward * distance;
            _focusStartTarget = _camera.OrbitTarget;
            _focusEndTarget = target;
            _focusStartDistance = _camera.OrbitDistance;
            _focusEndDistance = distance;
            _focusProgress = 0.0f;
            _isFocusing = true;

            // 临时切换到聚焦模式
            Mode = CameraMode.Focus;
        }

        /// <summary>
        /// 聚焦到包围盒: 自动计算包围盒中心并聚焦, 距离为包围盒对角线 * 2。
        /// </summary>
        /// <param name="center">包围盒中心。</param>
        /// <param name="size">包围盒大小 (对角线长度)。</param>
        public void FocusOnBounds(Vector3 center, float size)
        {
            float distance = MathF.Max(size * 1.5f, MinOrbitDistance);

            _focusStartPos = _camera.Position;
            _focusEndPos = center - _camera.Forward * distance;
            _focusStartTarget = _camera.OrbitTarget;
            _focusEndTarget = center;
            _focusStartDistance = _camera.OrbitDistance;
            _focusEndDistance = distance;
            _focusProgress = 0.0f;
            _isFocusing = true;

            Mode = CameraMode.Focus;
        }

        /// <summary>
        /// 看向目标点: 立即设置朝向 (不经过平滑) 使相机 Forward 指向 target。
        /// </summary>
        /// <param name="target">目标世界坐标。</param>
        public void LookAt(Vector3 target)
        {
            Vector3 toTarget = target - _camera.Position;
            if (toTarget.LengthSquared() < Epsilon) return;

            // 更新 Forward
            _camera.Forward = Vector3.Normalize(toTarget);

            // 从 Forward 提取欧拉角
            _camera.ExtractEulerAnglesFromForward();
            _camera.RecalculateVectors();

            // 同步目标值
            _targetYaw = _camera.Yaw;
            _targetPitch = _camera.Pitch;
            _targetRoll = _camera.Roll;
        }

        // ════════════════════════════════════════════════════════
        //  预设视角 (快捷切换)
        // ════════════════════════════════════════════════════════

        /// <summary>切换到顶视图 (Y 轴向下看)。</summary>
        public void SetViewTop()
        {
            _targetYaw = 0.0f;
            _targetPitch = PI / 2.0f - 0.05f;
            _targetRoll = 0.0f;
        }

        /// <summary>切换到底视图 (Y 轴向上看)。</summary>
        public void SetViewBottom()
        {
            _targetYaw = 0.0f;
            _targetPitch = -(PI / 2.0f - 0.05f);
            _targetRoll = 0.0f;
        }

        /// <summary>切换到前视图 (Z 轴负方向看, 即 -Z)。</summary>
        public void SetViewFront()
        {
            _targetYaw = 0.0f;
            _targetPitch = 0.0f;
            _targetRoll = 0.0f;
        }

        /// <summary>切换到后视图 (Z 轴正方向看, 即 +Z)。</summary>
        public void SetViewBack()
        {
            _targetYaw = PI;
            _targetPitch = 0.0f;
            _targetRoll = 0.0f;
        }

        /// <summary>切换到左视图 (X 轴正方向看)。</summary>
        public void SetViewLeft()
        {
            _targetYaw = PI / 2.0f;
            _targetPitch = 0.0f;
            _targetRoll = 0.0f;
        }

        /// <summary>切换到右视图 (X 轴负方向看)。</summary>
        public void SetViewRight()
        {
            _targetYaw = -PI / 2.0f;
            _targetPitch = 0.0f;
            _targetRoll = 0.0f;
        }

        /// <summary>切换到透视自由视角 (倾斜 30 度角)。</summary>
        public void SetViewPerspective()
        {
            _targetYaw = -0.78f;   // ~45 deg
            _targetPitch = -0.52f;  // ~-30 deg
            _targetRoll = 0.0f;
        }

        // ════════════════════════════════════════════════════════
        //  矩阵计算
        // ════════════════════════════════════════════════════════

        /// <summary>
        /// 视图矩阵 (View Matrix)。
        /// 使用右手坐标系: CreateLookTo(Position, Forward, Up)。
        /// </summary>
        public Matrix4x4 ViewMatrix
        {
            get
            {
                // 确保 Forward 和 Up 是单位向量
                Vector3 forward = Vector3.Normalize(_camera.Forward);
                Vector3 up = Vector3.Normalize(_camera.Up);
                return Matrix4x4.CreateLookTo(_camera.Position, forward, up);
            }
        }

        /// <summary>
        /// 投影矩阵 (Projection Matrix)。
        /// 透视模式使用 CreatePerspectiveFieldOfView, 正交模式使用 CreateOrthographic。
        /// </summary>
        public Matrix4x4 ProjectionMatrix
        {
            get
            {
                float aspect = MathF.Max(0.001f, _camera.Aspect);

                if (_camera.IsOrthographic)
                {
                    float halfWidth = _camera.OrthographicWidth * 0.5f;
                    float halfHeight = halfWidth / aspect;
                    return Matrix4x4.CreateOrthographic(
                        halfWidth * 2.0f, halfHeight * 2.0f,
                        _camera.NearClip, _camera.FarClip
                    );
                }
                else
                {
                    float fovRad = _camera.FieldOfView * (PI / 180.0f);
                    return Matrix4x4.CreatePerspectiveFieldOfView(
                        fovRad, aspect,
                        _camera.NearClip, _camera.FarClip
                    );
                }
            }
        }

        /// <summary>View * Projection 组合矩阵。</summary>
        public Matrix4x4 ViewProjectionMatrix => ViewMatrix * ProjectionMatrix;

        /// <summary>投影矩阵的逆矩阵。</summary>
        public Matrix4x4 InverseProjectionMatrix
        {
            get
            {
                var proj = ProjectionMatrix;
                if (Matrix4x4.Invert(proj, out var inv))
                    return inv;
                return Matrix4x4.Identity;
            }
        }

        /// <summary>视图矩阵的逆矩阵 (相机世界矩阵)。</summary>
        public Matrix4x4 InverseViewMatrix
        {
            get
            {
                var view = ViewMatrix;
                if (Matrix4x4.Invert(view, out var inv))
                    return inv;
                return Matrix4x4.Identity;
            }
        }

        // ════════════════════════════════════════════════════════
        //  坐标转换工具
        // ════════════════════════════════════════════════════════

        /// <summary>
        /// 世界坐标 -> 屏幕坐标 (NDC: -1 ~ 1)。
        /// </summary>
        /// <param name="worldPos">世界坐标。</param>
        /// <returns>NDC 坐标 (X: -1~1 左到右, Y: -1~1 下到上)。</returns>
        public Vector3 WorldToNDC(Vector3 worldPos)
        {
            Vector4 clip = Vector4.Transform(new Vector4(worldPos, 1.0f), ViewProjectionMatrix);
            float w = MathF.Max(MathF.Abs(clip.W), Epsilon);
            return new Vector3(clip.X / w, clip.Y / w, clip.Z / w);
        }

        /// <summary>
        /// 屏幕坐标 (像素) -> 世界射线。
        /// </summary>
        /// <param name="screenX">屏幕 X (像素, 左上角为原点)。</param>
        /// <param name="screenY">屏幕 Y (像素)。</param>
        /// <param name="viewportWidth">视口宽度。</param>
        /// <param name="viewportHeight">视口高度。</param>
        /// <returns>从相机出发的世界射线 (Origin + Direction)。</returns>
        public (Vector3 Origin, Vector3 Direction) ScreenToWorldRay(
            float screenX, float screenY, float viewportWidth, float viewportHeight)
        {
            // 像素 -> NDC
            float ndcX = (screenX / viewportWidth) * 2.0f - 1.0f;
            float ndcY = 1.0f - (screenY / viewportHeight) * 2.0f; // Y 翻转

            var invVP = Matrix4x4.Invert(ViewProjectionMatrix, out var inv)
                ? inv : Matrix4x4.Identity;

            // 近平面和远平面世界坐标
            Vector4 nearPoint4 = Vector4.Transform(new Vector4(ndcX, ndcY, 0.0f, 1.0f), invVP);
            Vector4 farPoint4 = Vector4.Transform(new Vector4(ndcX, ndcY, 1.0f, 1.0f), invVP);

            // 透视除法
            float nearW = MathF.Max(MathF.Abs(nearPoint4.W), Epsilon);
            float farW = MathF.Max(MathF.Abs(farPoint4.W), Epsilon);

            Vector3 nearPoint = new Vector3(
                nearPoint4.X / nearW, nearPoint4.Y / nearW, nearPoint4.Z / nearW);
            Vector3 farPoint = new Vector3(
                farPoint4.X / farW, farPoint4.Y / farW, farPoint4.Z / farW);

            Vector3 dir = Vector3.Normalize(farPoint - nearPoint);
            return (nearPoint, dir);
        }

        /// <summary>
        /// 屏幕像素坐标 -> 世界射线 (Vector2 重载)。
        /// </summary>
        public (Vector3 Origin, Vector3 Direction) ScreenToWorldRay(
            Vector2 screenPos, Vector2 viewportSize)
        {
            return ScreenToWorldRay(screenPos.X, screenPos.Y, viewportSize.X, viewportSize.Y);
        }

        /// <summary>
        /// 调整视口尺寸 (在窗口 Resize 时调用)。
        /// </summary>
        /// <param name="width">视口宽度。</param>
        /// <param name="height">视口高度。</param>
        public void Resize(int width, int height)
        {
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            _camera.Aspect = (float)width / (float)height;
        }

        // ════════════════════════════════════════════════════════
        //  Update — 每帧更新 (平滑阻尼 + 向量刷新)
        // ════════════════════════════════════════════════════════

        /// <summary>
        /// 每帧更新: 应用平滑阻尼, 刷新相机向量和投影。
        /// </summary>
        /// <param name="deltaTime">帧间隔 (秒)。</param>
        public void Update(float deltaTime)
        {
            if (deltaTime <= 0.0f) deltaTime = 0.016f;

            // 处理聚焦过渡
            if (_isFocusing)
            {
                UpdateFocusTransition(deltaTime);
                return;
            }

            // 平滑阻尼: 指数衰减插值
            float smoothT = 1.0f - MathF.Exp(-_camera.SmoothFactor * deltaTime);

            // 位置平滑
            _camera.Position = Vector3.Lerp(_camera.Position, _targetPosition, smoothT);

            // 旋转平滑
            _camera.Yaw = LerpAngle(_camera.Yaw, _targetYaw, smoothT);
            _camera.Pitch = LerpAngle(_camera.Pitch, _targetPitch, smoothT);
            _camera.Roll = LerpAngle(_camera.Roll, _targetRoll, smoothT);

            // 距离平滑
            _camera.OrbitDistance = MathF.Lerp(_camera.OrbitDistance, _targetOrbitDistance, smoothT);

            // 轨道目标平滑
            _camera.OrbitTarget = Vector3.Lerp(_camera.OrbitTarget, _targetOrbitTarget, smoothT);

            // FOV 平滑
            _camera.FieldOfView = MathF.Lerp(_camera.FieldOfView, _targetFOV, smoothT);

            // 正交宽度平滑
            _camera.OrthographicWidth = MathF.Lerp(_camera.OrthographicWidth, _targetOrthoWidth, smoothT);

            // 根据模式更新 Position (Orbit 模式下位置由焦点和距离反推)
            if (Mode == CameraMode.Orbit)
            {
                _camera.ExtractForwardFromEuler();
                _targetPosition = _camera.OrbitTarget - _camera.Forward * _camera.OrbitDistance;
            }

            // 重新计算方向向量
            _camera.RecalculateVectors();
        }

        /// <summary>
        /// 更新聚焦过渡动画 (使用 Smoothstep 缓动)。
        /// </summary>
        private void UpdateFocusTransition(float deltaTime)
        {
            _focusProgress += deltaTime / MathF.Max(0.01f, _focusDuration);
            if (_focusProgress >= 1.0f)
            {
                _focusProgress = 1.0f;
                _isFocusing = false;
                Mode = CameraMode.Orbit; // 聚焦完成后切换到轨道模式
            }

            // Smoothstep 缓动
            float t = _focusProgress;
            t = t * t * (3.0f - 2.0f * t);

            // 插值位置
            _camera.Position = Vector3.Lerp(_focusStartPos, _focusEndPos, t);
            _camera.OrbitTarget = Vector3.Lerp(_focusStartTarget, _focusEndTarget, t);
            _camera.OrbitDistance = MathF.Lerp(_focusStartDistance, _focusEndDistance, t);

            // 同步目标值
            _targetPosition = _camera.Position;
            _targetOrbitTarget = _camera.OrbitTarget;
            _targetOrbitDistance = _camera.OrbitDistance;

            // 重新计算方向
            Vector3 toTarget = _camera.OrbitTarget - _camera.Position;
            if (toTarget.LengthSquared() > Epsilon)
            {
                _camera.Forward = Vector3.Normalize(toTarget);
                _camera.ExtractEulerAnglesFromForward();
                _targetYaw = _camera.Yaw;
                _targetPitch = _camera.Pitch;
            }

            _camera.RecalculateVectors();
        }

        // ════════════════════════════════════════════════════════
        //  直接设置方法 (跳过平滑, 立即生效)
        // ════════════════════════════════════════════════════════

        /// <summary>立即设置位置 (无平滑)。</summary>
        public void SetPositionImmediate(Vector3 position)
        {
            _camera.Position = position;
            _targetPosition = position;
        }

        /// <summary>立即设置欧拉角 (无平滑)。</summary>
        public void SetEulerImmediate(float yaw, float pitch, float roll)
        {
            _camera.Yaw = yaw;
            _camera.Pitch = Math.Clamp(pitch, -PitchLimit, PitchLimit);
            _camera.Roll = roll;
            _camera.RecalculateVectors();
            _targetYaw = _camera.Yaw;
            _targetPitch = _camera.Pitch;
            _targetRoll = _camera.Roll;
        }

        /// <summary>立即设置轨道目标 (无平滑)。</summary>
        public void SetOrbitTargetImmediate(Vector3 target)
        {
            _camera.OrbitTarget = target;
            _targetOrbitTarget = target;
        }

        /// <summary>立即设置轨道距离 (无平滑)。</summary>
        public void SetOrbitDistanceImmediate(float distance)
        {
            _camera.OrbitDistance = Math.Clamp(distance, MinOrbitDistance, MaxOrbitDistance);
            _targetOrbitDistance = _camera.OrbitDistance;
        }

        /// <summary>立即设置 FOV (无平滑)。</summary>
        public void SetFieldOfViewImmediate(float fovDegrees)
        {
            _camera.FieldOfView = Math.Clamp(fovDegrees, MinFOV, MaxFOV);
            _targetFOV = _camera.FieldOfView;
        }

        /// <summary>立即设置正交模式 (无平滑)。</summary>
        public void SetOrthographicImmediate(bool orthographic)
        {
            _camera.IsOrthographic = orthographic;
        }

        // ════════════════════════════════════════════════════════
        //  工具方法
        // ════════════════════════════════════════════════════════

        /// <summary>
        /// 角度线性插值 (处理 -PI ~ PI 环绕)。
        /// </summary>
        private static float LerpAngle(float current, float target, float t)
        {
            float diff = target - current;
            // 将差值归一化到 -PI ~ PI
            while (diff > PI) diff -= 2.0f * PI;
            while (diff < -PI) diff += 2.0f * PI;
            return current + diff * t;
        }

        /// <summary>
        /// 平滑阻尼 (Spring 阻尼, 更自然的手感)。
        /// </summary>
        private static float SmoothDamp(float current, float target, ref float velocity, float smoothTime, float deltaTime)
        {
            float omega = 2.0f / MathF.Max(0.0001f, smoothTime);
            float x = omega * deltaTime;
            float exp = 1.0f / (1.0f + x + 0.48f * x * x + 0.235f * x * x * x);
            float change = current - target;
            float temp = (velocity + omega * change) * deltaTime;
            velocity = (velocity - omega * temp) * exp;
            return target + (change + temp) * exp;
        }

        /// <summary>
        /// 重置相机到默认位置。
        /// </summary>
        public void Reset()
        {
            _targetPosition = new Vector3(0.0f, 5.0f, 10.0f);
            _targetYaw = -0.45f;
            _targetPitch = -0.45f;
            _targetRoll = 0.0f;
            _targetOrbitDistance = 12.0f;
            _targetOrbitTarget = Vector3.Zero;
            _targetFOV = 60.0f;
            _targetOrthoWidth = 20.0f;

            _camera.FieldOfView = 60.0f;
            _camera.IsOrthographic = false;
            _camera.NearClip = 0.1f;
            _camera.FarClip = 10000.0f;

            Mode = CameraMode.Orbit;
            _isFocusing = false;
        }

        /// <summary>
        /// 获取相机当前速度 (用于调试显示)。
        /// </summary>
        public float GetCurrentSpeed()
        {
            Vector3 delta = _targetPosition - _camera.Position;
            return delta.Length();
        }

        /// <summary>
        /// 序列化相机状态 (用于场景保存/恢复)。
        /// </summary>
        public EditorCameraSnapshot CreateSnapshot()
        {
            return new EditorCameraSnapshot
            {
                Position = _camera.Position,
                Yaw = _camera.Yaw,
                Pitch = _camera.Pitch,
                Roll = _camera.Roll,
                FieldOfView = _camera.FieldOfView,
                NearClip = _camera.NearClip,
                FarClip = _camera.FarClip,
                IsOrthographic = _camera.IsOrthographic,
                OrthographicWidth = _camera.OrthographicWidth,
                OrbitTarget = _camera.OrbitTarget,
                OrbitDistance = _camera.OrbitDistance,
                Mode = Mode
            };
        }

        /// <summary>
        /// 从快照恢复相机状态。
        /// </summary>
        public void RestoreSnapshot(EditorCameraSnapshot snapshot)
        {
            if (snapshot == null) return;

            SetPositionImmediate(snapshot.Position);
            SetEulerImmediate(snapshot.Yaw, snapshot.Pitch, snapshot.Roll);
            SetFieldOfViewImmediate(snapshot.FieldOfView);
            SetOrbitTargetImmediate(snapshot.OrbitTarget);
            SetOrbitDistanceImmediate(snapshot.OrbitDistance);
            SetOrthographicImmediate(snapshot.IsOrthographic);

            _camera.NearClip = snapshot.NearClip;
            _camera.FarClip = snapshot.FarClip;
            _camera.OrthographicWidth = snapshot.OrthographicWidth;
            _targetOrthoWidth = snapshot.OrthographicWidth;
            _targetFOV = snapshot.FieldOfView;

            Mode = snapshot.Mode;
        }

        public void Dispose()
        {
            // 无需特殊释放
        }
    }

    // ════════════════════════════════════════════════════════════
    //  EditorCameraSnapshot — 相机状态快照 (序列化用)
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 相机状态快照: 用于序列化/反序列化相机位置和参数。
    /// 在保存场景或切换编辑器布局时使用。
    /// </summary>
    [Serializable]
    public sealed class EditorCameraSnapshot
    {
        public Vector3 Position;
        public float Yaw;
        public float Pitch;
        public float Roll;
        public float FieldOfView;
        public float NearClip;
        public float FarClip;
        public bool IsOrthographic;
        public float OrthographicWidth;
        public Vector3 OrbitTarget;
        public float OrbitDistance;
        public CameraMode Mode;
    }
}
