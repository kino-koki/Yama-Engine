using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using ShanyuEngine.Assets;
using ShanyuEngine.ECS;
using ShanyuEngine.Input;
using ShanyuEngine.Rendering;
using ShanyuEngine.RHI;
using ShanyuEngine.Particles;
using ShanyuEngine.VFX;

using ParticleRenderer = ShanyuEngine.VFX.ParticleRenderer;
namespace ShanyuEngine.Editor
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - 编辑器 3D 视口系统 (EditorViewport)
    //
    // 本文件实现编辑器的核心 3D 视口, 提供:
    //   * 离屏渲染目标管理 (将场景渲染到纹理供 ImGui 采样显示)
    //   * 视口摄像机控制 (飞行 / 轨道 / 正交三种模式)
    //   * Gizmo 系统 (移动 / 旋转 / 缩放 / 框选)
    //   * 视口叠加层 (无限网格 / 世界坐标轴 / 选择高亮 / 包围盒)
    //   * 后处理预览 (在编辑器内实时预览 PostProcessingStack 效果)
    //   * 多视口布局 (透视 + 三正交的四视图)
    //   * IMGUI 集成 (在视口内叠加 UI)
    //   * 实体射线拾取
    //   * 拖放资源到视口
    //   * 截屏与帧录制
    //
    // 对接系统: ECS (EntityComponentSystem) / Rendering (IRenderDevice) / RHI (RHIDevice)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>视口摄像机控制模式。</summary>
    public enum ViewportCameraMode
    {
        /// <summary>飞行模式: WASD 移动, 鼠标右键拖拽转向 (类似 FPS)。</summary>
        Fly,
        /// <summary>轨道模式: 围绕焦点旋转, 鼠标滚轮缩放距离 (类似 Blender)。</summary>
        Orbit,
        /// <summary>正交模式: 无透视的前/顶/侧视图, 用于精确对位。</summary>
        Orthographic
    }

    /// <summary>Gizmo 操作类型。</summary>
    public enum GizmoMode
    {
        /// <summary>无 Gizmo。</summary>
        None,
        /// <summary>移动 Gizmo (三轴箭头 + 中心方块平面)。</summary>
        Move,
        /// <summary>旋转 Gizmo (三圆环)。</summary>
        Rotate,
        /// <summary>缩放 Gizmo (三轴方块端点)。</summary>
        Scale,
        /// <summary>框选 Gizmo (屏幕空间矩形)。</summary>
        BoxSelect
    }

    /// <summary>Gizmo 当前拖拽的子部件 (用于决定约束轴)。</summary>
    public enum GizmoAxis
    {
        None,
        X, Y, Z,
        XY, XZ, YZ,
        Center,       // 中心 (自由移动 / 框选整体)
        Screen        // 屏幕空间 (沿相机平面)
    }

    /// <summary>视口布局类型。</summary>
    public enum ViewportLayout
    {
        /// <summary>单透视视口。</summary>
        SinglePerspective,
        /// <summary>四视图: 透视 + 顶 / 前 / 侧。</summary>
        Quad,
        /// <summary>左右分屏: 透视 + 正交。</summary>
        Split2,
        /// <summary>三视图。</summary>
        Triple
    }

    /// <summary>
    /// 视口离屏渲染目标。负责创建并管理供视口渲染的颜色 / 深度纹理,
    /// 以及对应的 RTV / DSV 描述符。当视口尺寸变化时自动重建。
    /// </summary>
    public sealed class ViewportRenderTarget : IDisposable
    {
        private readonly RHIDevice _device;
        private RHITexture _colorTexture;
        private RHITexture _depthTexture;
        private RHIDescriptorHandle _rtv;
        private RHIDescriptorHandle _dsv;
        private int _width;
        private int _height;
        private bool _disposed;

        /// <summary>颜色纹理 (供 ImGui 采样 / 后处理输入)。</summary>
        public RHITexture ColorTexture => _colorTexture;
        /// <summary>深度纹理 (Gizmo 深度测试 / 拾取)。</summary>
        public RHITexture DepthTexture => _depthTexture;
        public RHIDescriptorHandle RTV => _rtv;
        public RHIDescriptorHandle DSV => _dsv;
        public int Width => _width;
        public int Height => _height;

        /// <summary>颜色清空值 (引擎默认天蓝色)。</summary>
        public Vector4 ClearColor = new(0.12f, 0.14f, 0.18f, 1.0f);

        public ViewportRenderTarget(RHIDevice device, int width, int height)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _width = Math.Max(1, width);
            _height = Math.Max(1, height);
            Rebuild();
        }

        /// <summary>按新尺寸重建渲染目标 (尺寸相同时跳过)。</summary>
        public void Resize(int width, int height)
        {
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            if (width == _width && height == _height) return;
            _width = width;
            _height = height;
            Rebuild();
        }

        // 重建颜色 + 深度纹理及描述符
        private void Rebuild()
        {
            _rtv = default;
            _dsv = default;
            _colorTexture?.Dispose();
            _depthTexture?.Dispose();

            // 颜色: RGBA16F 以支持 HDR 后处理
            var colorDesc = new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R16G16B16A16_Float,
                Width = _width,
                Height = _height,
                DepthOrArraySize = 1,
                MipLevels = 1,
                SampleCount = 1,
                Flags = RHITextureFlags.RenderTarget | RHITextureFlags.ShaderResource,
                OptimizedClearValue = RHIClearValueHelper.Color(RHIFormat.R16G16B16A16_Float, ClearColor)
            };
            _colorTexture = _device.CreateTexture(colorDesc, "ViewportColor");
            _colorTexture.CurrentState = RHIResourceState.Common;
            _rtv = _device.CreateRTV(_colorTexture);

            // 深度: D32_Float
            var depthDesc = new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.D32_Float,
                Width = _width,
                Height = _height,
                DepthOrArraySize = 1,
                MipLevels = 1,
                SampleCount = 1,
                Flags = RHITextureFlags.DepthStencil,
                OptimizedClearValue = RHIClearValueHelper.Depth(1.0f, 0, RHIFormat.D32_Float)
            };
            _depthTexture = _device.CreateTexture(depthDesc, "ViewportDepth");
            _depthTexture.CurrentState = RHIResourceState.DepthWrite;
            _dsv = _device.CreateDSV(_depthTexture);
        }

        /// <summary>清空颜色与深度缓冲 (在命令录制开始时调用)。</summary>
        public void Clear(RHICommandList cmd)
        {
            if (cmd == null) return;
            cmd.ClearRenderTargetView(_rtv, ClearColor);
            cmd.ClearDepthStencilView(_dsv, 1.0f, 0);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _colorTexture?.Dispose();
            _depthTexture?.Dispose();
            _disposed = true;
        }
    }

    /// <summary>
    /// 视口摄像机控制器。封装位置 / 朝向 / 投影, 并按当前模式响应输入。
    /// 同时生成 view / projection 矩阵供渲染器与拾取使用。
    /// </summary>
    public sealed class ViewportCamera
    {
        /// <summary>相机世界位置。</summary>
        public Vector3 Position = new(0, 5, 12);
        /// <summary>欧拉角 (弧度): X=pitch, Y=yaw, Z=roll。</summary>
        public Vector3 YawPitchRoll = new(-0.4f, 0.6f, 0);
        /// <summary>轨道模式焦点。</summary>
        public Vector3 OrbitFocus = Vector3.Zero;
        /// <summary>轨道距离。</summary>
        public float OrbitDistance = 12.0f;
        /// <summary>视场角 (度)。</summary>
        public float FieldOfView = 60.0f;
        /// <summary>近裁剪面。</summary>
        public float NearClip = 0.1f;
        /// <summary>远裁剪面。</summary>
        public float FarClip = 2000.0f;
        /// <summary>正交视图尺寸 (半高)。</summary>
        public float OrthographicSize = 10.0f;
        /// <summary>当前控制模式。</summary>
        public ViewportCameraMode Mode = ViewportCameraMode.Orbit;
        /// <summary>正交视图朝向 (用于四视图)。</summary>
        public OrthoView OrthoViewMode = OrthoView.Perspective;

        /// <summary>飞行模式移动速度 (单位/秒)。</summary>
        public float MoveSpeed = 8.0f;
        /// <summary>飞行模式加速倍率 (按住 Shift)。</summary>
        public float BoostMultiplier = 3.0f;
        /// <summary>转向灵敏度。</summary>
        public float LookSensitivity = 0.25f;
        /// <summary>缩放灵敏度。</summary>
        public float ZoomSensitivity = 0.1f;

        private float _aspect = 1.0f;

        /// <summary>正交视图朝向枚举。</summary>
        public enum OrthoView { Perspective, Front, Top, Side }

        /// <summary>前向向量 (相机看向的方向)。</summary>
        public Vector3 Forward
        {
            get
            {
                float cp = MathF.Cos(YawPitchRoll.X), sp = MathF.Sin(YawPitchRoll.X);
                float cy = MathF.Cos(YawPitchRoll.Y), sy = MathF.Sin(YawPitchRoll.Y);
                return new Vector3(cp * sy, sp, cp * cy);
            }
        }

        public Vector3 Right => Vector3.Normalize(Vector3.Cross(Forward, Vector3.UnitY));
        public Vector3 Up => Vector3.Normalize(Vector3.Cross(Right, Forward));

        /// <summary>更新宽高比。</summary>
        public void SetAspect(float aspect) => _aspect = MathF.Max(0.0001f, aspect);

        /// <summary>视图矩阵。</summary>
        public Matrix4x4 ViewMatrix
        {
            get
            {
                if (Mode == ViewportCameraMode.Orbit)
                {
                    // 轨道模式: 位置由焦点 + 距离 + 朝向反推
                    Position = OrbitFocus - Forward * OrbitDistance;
                }
                return Matrix4x4.CreateLookAt(Position, Position + Forward, Vector3.UnitY);
            }
        }

        /// <summary>投影矩阵 (透视 / 正交)。</summary>
        public Matrix4x4 ProjectionMatrix
        {
            get
            {
                if (Mode == ViewportCameraMode.Orthographic || OrthoViewMode != OrthoView.Perspective)
                {
                    float h = OrthographicSize;
                    float w = h * _aspect;
                    return Matrix4x4.CreateOrthographic(w * 2, h * 2, NearClip, FarClip);
                }
                float fovRad = FieldOfView * (MathF.PI / 180f);
                return Matrix4x4.CreatePerspectiveFieldOfView(fovRad, _aspect, NearClip, FarClip);
            }
        }

        /// <summary>View * Projection。</summary>
        public Matrix4x4 ViewProjection => ViewMatrix * ProjectionMatrix;

        /// <summary>
        /// 按输入更新相机 (在编辑器主循环每帧调用)。
        /// </summary>
        /// <param name="input">输入系统。</param>
        /// <param name="dt">帧时间 (秒)。</param>
        /// <param name="viewportFocused">视口是否处于焦点 (失焦时忽略输入)。</param>
        public void Update(InputSystem input, float dt, bool viewportFocused)
        {
            if (input == null || !viewportFocused) return;

            // 鼠标右键拖拽 = 转向 (飞行) / 旋转 (轨道)
            // 注: 鼠标按键在 InputKey 中以 MouseLeft/Right/Middle 形式表示, 用 IsKeyDown 查询
            bool rightDrag = input.IsKeyDown(InputKey.MouseRight);
            bool middleDrag = input.IsKeyDown(InputKey.MouseMiddle);

            float dx = input.GetAxis(InputAxis.MouseDeltaX);
            float dy = input.GetAxis(InputAxis.MouseDeltaY);

            if (rightDrag)
            {
                YawPitchRoll.Y -= dx * LookSensitivity * 0.01f;
                YawPitchRoll.X -= dy * LookSensitivity * 0.01f;
                // 限制 pitch
                YawPitchRoll.X = Math.Clamp(YawPitchRoll.X, -1.55f, 1.55f);
            }

            // 滚轮缩放
            float wheel = input.GetAxis(InputAxis.MouseWheel);
            if (Mode == ViewportCameraMode.Orbit)
            {
                OrbitDistance *= 1.0f - wheel * ZoomSensitivity;
                OrbitDistance = Math.Clamp(OrbitDistance, 0.5f, 5000.0f);
            }
            else
            {
                // 透视/正交模式下滚轮前后移动
                Position += Forward * wheel * MoveSpeed * 0.5f;
            }

            // 中键平移
            if (middleDrag)
            {
                Vector3 pan = Right * (-dx * 0.01f * OrbitDistance * 0.1f) +
                              Up * (dy * 0.01f * OrbitDistance * 0.1f);
                if (Mode == ViewportCameraMode.Orbit)
                {
                    OrbitFocus += pan;
                }
                else
                {
                    Position += pan;
                }
            }

            // 飞行模式 WASD 移动
            if (Mode == ViewportCameraMode.Fly && rightDrag)
            {
                float speed = MoveSpeed * (input.IsKeyDown(InputKey.Shift) ? BoostMultiplier : 1.0f) * dt;
                Vector3 move = Vector3.Zero;
                if (input.IsKeyDown(InputKey.W)) move += Forward;
                if (input.IsKeyDown(InputKey.S)) move -= Forward;
                if (input.IsKeyDown(InputKey.D)) move += Right;
                if (input.IsKeyDown(InputKey.A)) move -= Right;
                if (input.IsKeyDown(InputKey.E)) move += Vector3.UnitY;
                if (input.IsKeyDown(InputKey.Q)) move -= Vector3.UnitY;
                if (move != Vector3.Zero) Position += Vector3.Normalize(move) * speed;
            }
        }

        /// <summary>将世界坐标点投影到屏幕 NDC (-1..1)。</summary>
        public Vector3 WorldToScreen(Vector3 world)
        {
            var p = Vector4.Transform(new Vector4(world, 1f), ViewProjection);
            float w = MathF.Max(p.W, 1e-8f);
            return new Vector3(p.X / w, p.Y / w, p.Z / w);
        }

        /// <summary>由屏幕坐标 (像素) + 深度反投影到世界射线。</summary>
        public Ray ScreenToWorldRay(Vector2 screenPx, Vector2 viewportSize)
        {
            float ndcX = (screenPx.X / viewportSize.X) * 2f - 1f;
            float ndcY = 1f - (screenPx.Y / viewportSize.Y) * 2f;
            var inv = Matrix4x4.Invert(ViewProjection, out var invM);
            if (!inv) return new Ray(Position, Forward);
            var nearPoint = Vector4.Transform(new Vector3(ndcX, ndcY, 0f), invM);
            var farPoint = Vector4.Transform(new Vector3(ndcX, ndcY, 1f), invM);
            // 透视除法 (Vector4.Transform 配 Matrix4x4 返回 Vector4, 含 W 分量)
            float nw = MathF.Max(nearPoint.W, 1e-8f);
            float fw = MathF.Max(farPoint.W, 1e-8f);
            Vector3 near = new(nearPoint.X / nw, nearPoint.Y / nw, nearPoint.Z / nw);
            Vector3 far = new(farPoint.X / fw, farPoint.Y / fw, farPoint.Z / fw);
            Vector3 dir = Vector3.Normalize(far - near);
            return new Ray(near, dir);
        }

        /// <summary>聚焦到指定包围盒 (帧到视野中央)。</summary>
        public void FrameBounds(BoundingBox bounds)
        {
            OrbitFocus = bounds.Center;
            float radius = MathF.Max(0.1f, (bounds.Max - bounds.Min).Length() * 0.5f);
            OrbitDistance = MathF.Max(radius * 2.2f, 1.0f);
            if (Mode == ViewportCameraMode.Orthographic)
                OrthographicSize = radius * 1.2f;
        }
    }

    /// <summary>3D 射线 (起点 + 方向)。</summary>
    public struct Ray
    {
        public Vector3 Origin;
        public Vector3 Direction;
        public Ray(Vector3 origin, Vector3 dir) { Origin = origin; Direction = dir; }
    }

    /// <summary>
    /// Gizmo 系统: 渲染并处理移动 / 旋转 / 缩放 / 框选操纵器。
    /// 通过屏幕空间测试确定悬停轴, 拖拽时产生增量并应用到选中对象。
    /// </summary>
    public sealed class GizmoSystem
    {
        /// <summary>当前 Gizmo 模式。</summary>
        public GizmoMode Mode = GizmoMode.Move;
        /// <summary>当前悬停 / 拖拽的轴。</summary>
        public GizmoAxis ActiveAxis = GizmoAxis.None;
        /// <summary>是否正在拖拽。</summary>
        public bool IsDragging;
        /// <summary>Gizmo 绘制尺寸 (屏幕像素)。</summary>
        public float ScreenSize = 80.0f;
        /// <summary>是否使用世界空间 (false = 相对选中对象局部)。</summary>
        public bool UseWorldSpace = false;

        private Vector3 _dragStart;
        private Vector3 _dragOrigin;
        private Vector2 _boxSelectStart;

        /// <summary>由当前相机与选中对象计算 Gizmo 世界位置。</summary>
        public Vector3 GetGizmoPosition(List<SceneObject> selection, ViewportCamera cam)
        {
            if (selection == null || selection.Count == 0) return Vector3.Zero;
            if (selection.Count == 1) return selection[0].Position;
            // 多选: 取包围中心
            Vector3 sum = Vector3.Zero;
            foreach (var o in selection) sum += o.Position;
            return sum / selection.Count;
        }

        /// <summary>开始拖拽 (按下鼠标时调用)。</summary>
        public void BeginDrag(Vector2 screenPos, ViewportCamera cam, Vector2 viewportSize, List<SceneObject> selection)
        {
            if (Mode == GizmoMode.BoxSelect)
            {
                _boxSelectStart = screenPos;
                IsDragging = true;
                return;
            }
            if (selection == null || selection.Count == 0) return;
            _dragOrigin = GetGizmoPosition(selection, cam);
            _dragStart = _dragOrigin;
            IsDragging = true;
        }

        /// <summary>拖拽中, 返回应用到选中对象的变换增量。</summary>
        public GizmoDelta UpdateDrag(Vector2 screenPos, ViewportCamera cam, Vector2 viewportSize)
        {
            var delta = new GizmoDelta();
            if (!IsDragging) return delta;

            if (Mode == GizmoMode.BoxSelect)
            {
                delta.BoxSelectMin = Vector2.Min(_boxSelectStart, screenPos);
                delta.BoxSelectMax = Vector2.Max(_boxSelectStart, screenPos);
                return delta;
            }

            // 将鼠标射线与活动轴平面相交, 得到世界增量
            var ray = cam.ScreenToWorldRay(screenPos, viewportSize);
            Vector3 axisVec = ActiveAxis switch
            {
                GizmoAxis.X => Vector3.UnitX,
                GizmoAxis.Y => Vector3.UnitY,
                GizmoAxis.Z => Vector3.UnitZ,
                _ => Vector3.Zero
            };

            switch (Mode)
            {
                case GizmoMode.Move:
                    {
                        // 射线与过原点法线 = 轴的平面求交, 再投影到轴
                        Vector3 hit = RayPlaneIntersect(ray, _dragOrigin, axisVec);
                        Vector3 along = Vector3.Dot(hit - _dragOrigin, axisVec) * axisVec;
                        delta.Translation = along;
                        _dragOrigin = _dragStart + along;
                        break;
                    }
                case GizmoMode.Scale:
                    {
                        Vector3 hit = RayPlaneIntersect(ray, _dragOrigin, axisVec);
                        float s = Vector3.Dot(hit - _dragOrigin, axisVec);
                        delta.Scale = axisVec * s * 0.5f;
                        break;
                    }
                case GizmoMode.Rotate:
                    {
                        // 旋转: 用鼠标在屏幕上的横向移动近似角度
                        float angle = (screenPos.X - viewportSize.X * 0.5f) * 0.01f;
                        delta.Rotation = Quaternion.CreateFromAxisAngle(axisVec, angle);
                        break;
                    }
            }
            return delta;
        }

        /// <summary>结束拖拽。</summary>
        public void EndDrag() { IsDragging = false; ActiveAxis = GizmoAxis.None; }

        // 射线与平面相交 (平面过 point, 法线 normal)
        private static Vector3 RayPlaneIntersect(Ray ray, Vector3 point, Vector3 normal)
        {
            float denom = Vector3.Dot(normal, ray.Direction);
            if (MathF.Abs(denom) < 1e-6f) return point;
            float t = Vector3.Dot(point - ray.Origin, normal) / denom;
            return ray.Origin + ray.Direction * MathF.Max(0, t);
        }

        /// <summary>测试鼠标在哪个 Gizmo 轴上 (简化: 根据屏幕投影距离)。</summary>
        public GizmoAxis HitTest(Vector2 screenPx, ViewportCamera cam, Vector2 viewportSize, List<SceneObject> selection)
        {
            if (selection == null || selection.Count == 0 || Mode == GizmoMode.None) return GizmoAxis.None;
            Vector3 origin = GetGizmoPosition(selection, cam);
            Vector2 originScreen = WorldToScreenXY(origin, cam, viewportSize);
            float threshold = ScreenSize * 0.15f;

            GizmoAxis best = GizmoAxis.Center;
            float bestDist = threshold;
            foreach (var (axis, vec) in new[] {
                (GizmoAxis.X, Vector3.UnitX), (GizmoAxis.Y, Vector3.UnitY), (GizmoAxis.Z, Vector3.UnitZ) })
            {
                Vector2 axisEnd = WorldToScreenXY(origin + vec * 1.0f, cam, viewportSize);
                float d = PointToSegmentDistance(screenPx, originScreen, axisEnd);
                if (d < bestDist) { bestDist = d; best = axis; }
            }
            return best;
        }

        private static Vector2 WorldToScreenXY(Vector3 world, ViewportCamera cam, Vector2 size)
        {
            var ndc = cam.WorldToScreen(world);
            return new Vector2((ndc.X + 1f) * 0.5f * size.X, (1f - ndc.Y) * 0.5f * size.Y);
        }

        private static float PointToSegmentDistance(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float t = Math.Clamp(Vector2.Dot(p - a, ab) / MathF.Max(1e-6f, ab.LengthSquared()), 0f, 1f);
            return Vector2.Distance(p, a + ab * t);
        }
    }

    /// <summary>Gizmo 拖拽产生的变换增量。</summary>
    public struct GizmoDelta
    {
        public Vector3 Translation;
        public Vector3 Scale;
        public Quaternion Rotation;
        public Vector2 BoxSelectMin;
        public Vector2 BoxSelectMax;
    }

    /// <summary>
    /// 视口叠加层: 绘制无限网格 / 世界坐标轴 / 选择高亮 / 对象包围盒。
    /// 以线段列表形式提供给 RHI 命令录制。
    /// </summary>
    public sealed class ViewportOverlay
    {
        /// <summary>是否显示网格。</summary>
        public bool ShowGrid = true;
        /// <summary>是否显示世界坐标轴。</summary>
        public bool ShowWorldAxes = true;
        /// <summary>是否显示选中对象包围盒。</summary>
        public bool ShowSelectionBounds = true;
        /// <summary>网格大小 (单元)。</summary>
        public float GridSize = 1.0f;
        /// <summary>网格单元数 (半边)。</summary>
        public int GridHalfExtent = 50;
        /// <summary>网格颜色。</summary>
        public Vector3 GridColor = new(0.3f, 0.3f, 0.32f);
        /// <summary>选中高亮颜色。</summary>
        public Vector3 SelectionColor = new(1.0f, 0.55f, 0.1f);

        /// <summary>构造网格线段 (在 XZ 平面)。</summary>
        public List<(Vector3 a, Vector3 b)> BuildGridLines(ViewportCamera cam)
        {
            var lines = new List<(Vector3, Vector3)>();
            if (!ShowGrid) return lines;
            Vector3 center = new(cam.OrbitFocus.X, 0, cam.OrbitFocus.Z);
            for (int i = -GridHalfExtent; i <= GridHalfExtent; i++)
            {
                float offset = i * GridSize;
                bool major = i % 10 == 0;
                Vector3 color = major ? GridColor * 1.6f : GridColor;
                lines.Add((center + new Vector3(-GridHalfExtent * GridSize, 0, offset),
                           center + new Vector3(GridHalfExtent * GridSize, 0, offset)));
                lines.Add((center + new Vector3(offset, 0, -GridHalfExtent * GridSize),
                           center + new Vector3(offset, 0, GridHalfExtent * GridSize)));
            }
            return lines;
        }

        /// <summary>构造世界坐标轴线段 (X 红 / Y 绿 / Z 蓝)。</summary>
        public List<(Vector3 a, Vector3 b, Vector3 color)> BuildWorldAxes()
        {
            var lines = new List<(Vector3, Vector3, Vector3)>();
            if (!ShowWorldAxes) return lines;
            float len = GridSize * GridHalfExtent;
            lines.Add((Vector3.Zero, new Vector3(len, 0, 0), new Vector3(1, 0.2f, 0.2f)));
            lines.Add((Vector3.Zero, new Vector3(0, len, 0), new Vector3(0.2f, 1, 0.2f)));
            lines.Add((Vector3.Zero, new Vector3(0, 0, len), new Vector3(0.2f, 0.4f, 1)));
            return lines;
        }

        /// <summary>构造选中对象包围盒线段。</summary>
        public List<(Vector3 a, Vector3 b)> BuildSelectionBounds(List<SceneObject> selection)
        {
            var lines = new List<(Vector3, Vector3)>();
            if (!ShowSelectionBounds || selection == null) return lines;
            foreach (var obj in selection)
            {
                if (obj == null) continue;
                var b = obj.Bounds.Transform(obj.LocalToWorldMatrix);
                AddBox(lines, b.Min, b.Max);
            }
            return lines;
        }

        // 添加一个立方体的 12 条边
        private static void AddBox(List<(Vector3, Vector3)> lines, Vector3 min, Vector3 max)
        {
            var c = new[]
            {
                new Vector3(min.X, min.Y, min.Z), new Vector3(max.X, min.Y, min.Z),
                new Vector3(min.X, max.Y, min.Z), new Vector3(max.X, max.Y, min.Z),
                new Vector3(min.X, min.Y, max.Z), new Vector3(max.X, min.Y, max.Z),
                new Vector3(min.X, max.Y, max.Z), new Vector3(max.X, max.Y, max.Z)
            };
            int[] edges = { 0,1, 2,3, 4,5, 6,7, 0,2, 1,3, 4,6, 5,7, 0,4, 1,5, 2,6, 3,7 };
            for (int i = 0; i < edges.Length; i += 2)
                lines.Add((c[edges[i]], c[edges[i + 1]]));
        }
    }

    /// <summary>
    /// 视口后处理预览: 在编辑器内对视口渲染结果应用 PostProcessingSettings,
    /// 便于美术在不进入运行时的情况下调参。
    /// </summary>
    public sealed class ViewportPostProcessPreview
    {
        /// <summary>是否启用后处理预览。</summary>
        public bool Enabled = true;
        /// <summary>预览用的后处理设置 (可与游戏内设置不同)。</summary>
        public PostProcessingSettings Settings = new();
        /// <summary>当前预览的色调映射算子。</summary>
        public TonemapOperator Tonemap = TonemapOperator.ACES;
        /// <summary>是否显示曝光直方图叠加。</summary>
        public bool ShowHistogram = false;

        /// <summary>将后处理应用到输入纹理, 输出到目标 (此处记录为命令, 实际由渲染器执行)。</summary>
        public void Apply(RHICommandList cmd, RHITexture input, RHITexture output)
        {
            if (cmd == null || input == null || output == null || !Enabled) return;
            // 真实实现会绑定计算管线执行各 Pass:
            //   自动曝光 -> 景深 -> Bloom -> 色调映射 -> 颜色分级
            // 此处通过资源屏障切换状态, 标记管线位置
            cmd.TransitionTo(input, RHIResourceState.NonPixelShaderResource);
            cmd.TransitionTo(output, RHIResourceState.UnorderedAccess);
            // Dispatch(...); 由渲染器填充
            cmd.TransitionTo(output, RHIResourceState.PixelShaderResource);
        }
    }

    /// <summary>
    /// 射线拾取: 将屏幕点击转换为世界射线, 与场景对象包围盒求交, 返回最近命中。
    /// </summary>
    public static class RayPicking
    {
        /// <summary>对场景中所有可见对象执行射线拾取。</summary>
        /// <param name="assets">资产数据库 (提供场景对象)。</param>
        /// <param name="ray">世界射线。</param>
        /// <param name="layerMask">层掩码 (0 = 全部)。</param>
        /// <returns>最近命中的对象, 或 null。</returns>
        public static SceneObject Pick(AssetDatabase assets, Ray ray, uint layerMask = 0)
        {
            if (assets == null) return null;
            SceneObject best = null;
            float bestT = float.MaxValue;
            foreach (var obj in assets.SceneObjects)
            {
                if (obj == null || !obj.IsVisible) continue;
                if (layerMask != 0 && (obj.Layer & layerMask) == 0) continue;
                var box = obj.Bounds.Transform(obj.LocalToWorldMatrix);
                if (RayBoxIntersect(ray, box.Min, box.Max, out float t) && t < bestT && t > 0)
                {
                    bestT = t;
                    best = obj;
                }
            }
            return best;
        }

        // 射线与 AABB 求交 (Slab 方法)
        private static bool RayBoxIntersect(Ray ray, Vector3 min, Vector3 max, out float t)
        {
            t = 0;
            float tmin = 0f, tmax = float.MaxValue;
            for (int i = 0; i < 3; i++)
            {
                float o = i == 0 ? ray.Origin.X : i == 1 ? ray.Origin.Y : ray.Origin.Z;
                float d = i == 0 ? ray.Direction.X : i == 1 ? ray.Direction.Y : ray.Direction.Z;
                float bmin = i == 0 ? min.X : i == 1 ? min.Y : min.Z;
                float bmax = i == 0 ? max.X : i == 1 ? max.Y : max.Z;
                if (MathF.Abs(d) < 1e-8f)
                {
                    if (o < bmin || o > bmax) return false;
                }
                else
                {
                    float t1 = (bmin - o) / d;
                    float t2 = (bmax - o) / d;
                    if (t1 > t2) (t1, t2) = (t2, t1);
                    tmin = MathF.Max(tmin, t1);
                    tmax = MathF.Min(tmax, t2);
                    if (tmin > tmax) return false;
                }
            }
            t = tmin;
            return true;
        }
    }

    /// <summary>
    /// 截屏与帧录制: 将视口颜色纹理读回 CPU 并保存为 PNG 序列。
    /// </summary>
    public sealed class ViewportCapture
    {
        private readonly RHIDevice _device;
        private readonly string _outputDir;
        private int _frameIndex;
        /// <summary>是否正在录制。</summary>
        public bool IsRecording;
        /// <summary>录制帧率上限。</summary>
        public int TargetFps = 30;

        public ViewportCapture(RHIDevice device, string outputDir)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _outputDir = outputDir ?? Path.Combine(Path.GetTempPath(), "ShanyuViewportCapture");
            Directory.CreateDirectory(_outputDir);
        }

        /// <summary>截取单帧到文件。</summary>
        public string CaptureFrame(RHITexture colorTexture)
        {
            if (colorTexture == null) return null;
            string path = Path.Combine(_outputDir, $"frame_{_frameIndex:D6}.png");
            // 真实实现: 创建 readback buffer, CopyTexture, Map, 编码 PNG
            // 此处仅记录路径与索引, 由宿主程序完成编码
            _frameIndex++;
            return path;
        }

        /// <summary>开始连续录制。</summary>
        public void StartRecording() { IsRecording = true; _frameIndex = 0; }

        /// <summary>停止录制并返回输出目录。</summary>
        public string StopRecording() { IsRecording = false; return _outputDir; }
    }

    /// <summary>
    /// 编辑器视口: 整合渲染目标 / 相机 / Gizmo / 叠加层 / 后处理预览 / 拾取 / 拖放 / 截屏,
    /// 并提供 IMGUI 叠加绘制回调。一个 EditorViewport 实例代表一个可见视口单元;
    /// 通过 <see cref="ViewportLayout"/> 可组合多个视口。
    /// </summary>
    public sealed class EditorViewport : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly IRenderDevice _renderDevice;
        private readonly AssetDatabase _assets;
        private readonly EntityComponentSystem _ecs;
        private SceneRendererReal _sceneRenderer;
        private TextureManager _textureManager;
        private PostProcessSystem _postProcess;

        /// <summary>渲染目标 (离屏颜色 + 深度)。</summary>
        public ViewportRenderTarget RenderTarget;
        /// <summary>视口摄像机。</summary>
        public ViewportCamera Camera = new();
        /// <summary>Gizmo 系统。</summary>
        public GizmoSystem Gizmo = new();
        /// <summary>叠加层。</summary>
        public ViewportOverlay Overlay = new();
        /// <summary>后处理预览。</summary>
        public ViewportPostProcessPreview PostProcess = new();
        /// <summary>截屏 / 录制。</summary>
        public ViewportCapture Capture;

        // ── GPU 粒子系统 ──
        private ParticleRenderer? _particleRenderer;
        private FlowVFXRenderBridge? _particleBridge;
        private FlowVFXParticleSimulationReal? _particleSimulation;

        /// <summary>当前布局。</summary>
        public ViewportLayout Layout = ViewportLayout.SinglePerspective;
        /// <summary>视口像素尺寸 (由 ImGui 传入)。</summary>
        public Vector2 Size = new(800, 600);
        /// <summary>视口是否被聚焦 (接收输入)。</summary>
        public bool IsFocused;
        /// <summary>IMGUI 叠加绘制回调 (在视口上方绘制 UI)。</summary>
        public Action<Vector2> OnImGuiOverlay;

        private readonly List<EditorViewport> _subViewports = new();
        private bool _disposed;

        public EditorViewport(RHIDevice device, IRenderDevice renderDevice,
            AssetDatabase assets, EntityComponentSystem ecs, int width, int height)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _renderDevice = renderDevice ?? throw new ArgumentNullException(nameof(renderDevice));
            _assets = assets ?? throw new ArgumentNullException(nameof(assets));
            _ecs = ecs ?? throw new ArgumentNullException(nameof(ecs));
            _sceneRenderer = new SceneRendererReal(_device);
            _textureManager = new TextureManager(_device);
            _sceneRenderer.SetTextureManager(_textureManager);
            _postProcess = new PostProcessSystem(_device, width, height);
            _postProcess.Initialize();
            _sceneRenderer.SetPostProcessSystem(_postProcess);
            RenderTarget = new ViewportRenderTarget(_device, width, height);
            Capture = new ViewportCapture(_device, null);
            Camera.SetAspect((float)width / height);

            // ── 初始化 GPU 粒子渲染器 ──
            _particleRenderer = new ParticleRenderer(_device);
            _particleRenderer.Initialize();
            _sceneRenderer.SetParticleRenderer(_particleRenderer);
            _particleBridge = new FlowVFXRenderBridge(_particleRenderer);
            _sceneRenderer.SetParticleBridge(_particleBridge);

            // ── 创建演示粒子系统（火焰）──
            _particleSimulation = ParticleDemo.CreateFireEffect(new Vector3(0, 0, 0), 2048);
            _sceneRenderer.ActiveParticleSimulation = _particleSimulation;

            // 四视图布局时创建三个正交子视口
            ApplyLayout(Layout);
        }

        /// <summary>应用视口布局, 创建/销毁子视口。</summary>
        public void ApplyLayout(ViewportLayout layout)
        {
            Layout = layout;
            foreach (var sub in _subViewports) sub.Dispose();
            _subViewports.Clear();

            if (layout == ViewportLayout.Quad)
            {
                foreach (var ov in new[] { ViewportCamera.OrthoView.Top,
                                           ViewportCamera.OrthoView.Front,
                                           ViewportCamera.OrthoView.Side })
                {
                    var sub = new EditorViewport(_device, _renderDevice, _assets, _ecs, 256, 256);
                    sub.Camera.Mode = ViewportCameraMode.Orbit;
                    sub.Camera.OrthoViewMode = ov;
                    _subViewports.Add(sub);
                }
            }
        }

        /// <summary>调整视口尺寸 (由 ImGui resize 回调驱动)。</summary>
        public void Resize(int width, int height)
        {
            Size = new Vector2(width, height);
            RenderTarget.Resize(width, height);
            _postProcess?.Resize(width, height);
            Camera.SetAspect((float)width / Math.Max(1, height));
        }

        /// <summary>每帧更新: 相机输入 + Gizmo + 子视口。</summary>
        public void Update(InputSystem input, float dt)
        {
            Camera.Update(input, dt, IsFocused);

            var selection = _assets.GetSelectedObjects();
            if (IsFocused && input.IsKeyDown(InputKey.MouseLeft))
            {
                if (!Gizmo.IsDragging)
                {
                    Gizmo.ActiveAxis = Gizmo.HitTest(input.MousePosition, Camera, Size, selection);
                    if (Gizmo.Mode != GizmoMode.None)
                        Gizmo.BeginDrag(input.MousePosition, Camera, Size, selection);
                }
                if (Gizmo.IsDragging)
                {
                    var delta = Gizmo.UpdateDrag(input.MousePosition, Camera, Size);
                    ApplyGizmoDelta(selection, delta);
                }
            }
            else if (Gizmo.IsDragging)
            {
                Gizmo.EndDrag();
            }

            // 更新粒子模拟
            _particleSimulation?.Simulate(dt);

            foreach (var sub in _subViewports) sub.Update(input, dt);
        }

        // 将 Gizmo 增量应用到选中对象
        private void ApplyGizmoDelta(List<SceneObject> selection, GizmoDelta delta)
        {
            if (selection == null) return;
            foreach (var obj in selection)
            {
                if (Gizmo.Mode == GizmoMode.Move) obj.Position += delta.Translation;
                else if (Gizmo.Mode == GizmoMode.Scale) obj.Scale += delta.Scale;
                else if (Gizmo.Mode == GizmoMode.Rotate) obj.Rotation = delta.Rotation * obj.Rotation;
            }
        }

        /// <summary>渲染视口: 清空 -> 渲染场景 -> 后处理 -> 叠加层 -> 截屏。</summary>
        public void Render(float dt)
        {
            var cmd = _device.CreateCommandList(RHIQueueType.Graphics, "ViewportCmd");
            cmd.Open();

            // 通过真实场景渲染器渲染场景
            var scene = BuildRenderScene();
            var cameraData = BuildCameraData();

            // 预上传骨骼蒙皮网格到 GPU，避免首帧上传延迟
            if (scene is RenderSceneSnapshot snapshot)
            {
                foreach (var skinned in snapshot.SkinnedMeshes)
                {
                    if (skinned.Mesh != null)
                        _sceneRenderer.UploadSkinnedMesh(skinned.MeshAssetPath, skinned.Mesh);
                }
            }

            if (_postProcess != null)
            {
                // 后处理管线: 场景渲染到中间 HDR 纹理 → Bloom + 色调映射 → 输出到 RenderTarget.RTV
                cmd.ClearRenderTargetView(_postProcess.GetSceneColorRTV(), RenderTarget.ClearColor);
                cmd.ClearDepthStencilView(RenderTarget.DSV, 1.0f, 0);
                cmd.SetViewport(new RHIViewport(0, 0, RenderTarget.Width, RenderTarget.Height, 0, 1));
                cmd.SetScissorRect(new RHIScissorRect(0, 0, RenderTarget.Width, RenderTarget.Height));
                cmd.OMSetRenderTargets(new[] { _postProcess.GetSceneColorRTV() }, RenderTarget.DSV);

                // 渲染场景并执行后处理输出到最终 RTV
                _sceneRenderer.RenderScene(cmd, scene, cameraData, dt, RenderTarget.RTV);

                // 叠加层与 Gizmo 直接渲染到最终 RTV (不参与后处理)
                cmd.OMSetRenderTargets(new[] { RenderTarget.RTV }, RenderTarget.DSV);
            }
            else
            {
                // 无后处理: 直接渲染到 RenderTarget
                RenderTarget.Clear(cmd);
                cmd.SetViewport(new RHIViewport(0, 0, RenderTarget.Width, RenderTarget.Height, 0, 1));
                cmd.SetScissorRect(new RHIScissorRect(0, 0, RenderTarget.Width, RenderTarget.Height));
                cmd.OMSetRenderTargets(new[] { RenderTarget.RTV }, RenderTarget.DSV);

                _sceneRenderer.RenderScene(cmd, scene, cameraData, dt);
            }

            // 叠加层与 Gizmo
            DrawOverlay(cmd);
            DrawGizmo(cmd);

            // 解绑渲染目标
            cmd.OMSetRenderTargets(Array.Empty<RHIDescriptorHandle>(), null);

            // 后处理预览 (ViewportPostProcessPreview 占位逻辑)
            PostProcess.Apply(cmd, RenderTarget.ColorTexture, RenderTarget.ColorTexture);

            cmd.Close();
            _device.GetCommandQueue(RHIQueueType.Graphics).ExecuteCommandLists(new[] { cmd });

            // 截屏 (读取已渲染的 ColorTexture)
            if (Capture.IsRecording) Capture.CaptureFrame(RenderTarget.ColorTexture);

            foreach (var sub in _subViewports) sub.Render(dt);
            cmd.Dispose();
        }

        // 构造渲染场景快照 (从 AssetDatabase + ECS)
        // 骨骼动画对象会在 RenderSceneBuilder 中被识别并加入 SkinnedMeshes 列表
        private IRenderScene BuildRenderScene()
        {
            return RenderSceneBuilder.FromAssetDatabase(_assets, "EditorViewport");
        }

        // 构造相机数据 (从 ViewportCamera 转换为渲染器使用的 CameraData)
        private CameraData BuildCameraData()
        {
            var view = Camera.ViewMatrix;
            var proj = Camera.ProjectionMatrix;

            // 从场景中提取主光源方向（用于 ShadowMap 计算）
            Vector3 lightDir = Vector3.Zero;
            var scene = BuildRenderScene();
            if (scene?.Lights != null && scene.Lights.Count > 0)
            {
                var light = scene.Lights[0];
                lightDir = new Vector3(
                    light.WorldMatrix.M31,
                    light.WorldMatrix.M32,
                    light.WorldMatrix.M33);
                if (lightDir.LengthSquared() < 1e-6f)
                    lightDir = new Vector3(-0.5f, -0.8f, -0.3f);
            }

            return new CameraData
            {
                Position = Camera.Position,
                Forward = Camera.Forward,
                Up = Vector3.UnitY,
                FieldOfView = Camera.FieldOfView,
                NearClip = Camera.NearClip,
                FarClip = Camera.FarClip,
                AspectRatio = Camera._aspect > 0 ? Camera._aspect : (Size.X > 0 ? Size.X / Size.Y : 1.777f),
                ViewMatrix = view,
                ProjectionMatrix = proj,
                ViewProjectionMatrix = view * proj,
                LightDirection = lightDir
            };
        }

        // 绘制叠加层 (网格 / 坐标轴 / 选择框) — 使用真实线段渲染器
        private void DrawOverlay(RHICommandList cmd)
        {
            var cameraData = BuildCameraData();

            // 渲染无限网格
            if (Overlay.ShowGrid)
                _sceneRenderer.RenderGrid(cmd, cameraData, Overlay.GridSize, Overlay.GridHalfExtent);

            // 渲染世界坐标轴 (X 红 / Y 绿 / Z 蓝)
            if (Overlay.ShowWorldAxes)
            {
                var axes = Overlay.BuildWorldAxes();
                if (axes.Count > 0)
                {
                    var positions = new Vector3[axes.Count * 2];
                    var colors = new Vector4[axes.Count * 2];
                    for (int i = 0; i < axes.Count; i++)
                    {
                        positions[i * 2] = axes[i].a;
                        positions[i * 2 + 1] = axes[i].b;
                        colors[i * 2] = new Vector4(axes[i].color, 1.0f);
                        colors[i * 2 + 1] = new Vector4(axes[i].color, 1.0f);
                    }
                    _sceneRenderer.RenderLines(cmd, positions, colors, cameraData);
                }
            }

            // 渲染选中对象包围盒
            if (Overlay.ShowSelectionBounds)
            {
                var bounds = Overlay.BuildSelectionBounds(_assets.GetSelectedObjects());
                if (bounds.Count > 0)
                {
                    var positions = new Vector3[bounds.Count * 2];
                    var colors = new Vector4[bounds.Count * 2];
                    var selColor = new Vector4(Overlay.SelectionColor, 1.0f);
                    for (int i = 0; i < bounds.Count; i++)
                    {
                        positions[i * 2] = bounds[i].a;
                        positions[i * 2 + 1] = bounds[i].b;
                        colors[i * 2] = selColor;
                        colors[i * 2 + 1] = selColor;
                    }
                    _sceneRenderer.RenderLines(cmd, positions, colors, cameraData);
                }
            }
        }

        // 绘制 Gizmo — 移动/旋转/缩放线框几何
        private void DrawGizmo(RHICommandList cmd)
        {
            if (Gizmo.Mode == GizmoMode.None) return;
            var selection = _assets.GetSelectedObjects();
            if (selection.Count == 0) return;

            var cameraData = BuildCameraData();
            var gizmoPos = Gizmo.GetGizmoPosition(selection, Camera);

            // Gizmo在世界空间的轴长 (随距离缩放)
            float axisLen = Gizmo.ScreenSize * 0.01f;
            float dist = Vector3.Distance(cameraData.Position, gizmoPos);
            if (dist > 0.1f) axisLen *= dist * 0.15f;
            axisLen = MathF.Max(axisLen, 0.1f);

            switch (Gizmo.Mode)
            {
                case GizmoMode.Move:
                    DrawMoveGizmo(cmd, cameraData, gizmoPos, axisLen);
                    break;
                case GizmoMode.Rotate:
                    DrawRotateGizmo(cmd, cameraData, gizmoPos, axisLen);
                    break;
                case GizmoMode.Scale:
                    DrawScaleGizmo(cmd, cameraData, gizmoPos, axisLen);
                    break;
            }
        }

        // 移动 Gizmo: 三轴箭头 (X 红 / Y 绿 / Z 蓝)
        private void DrawMoveGizmo(RHICommandList cmd, in CameraData camera, Vector3 center, float len)
        {
            var positions = new List<Vector3>();
            var colors = new List<Vector4>();

            // 三轴线段
            AddLine(positions, colors, center, center + new Vector3(len, 0, 0), new Vector4(1, 0.2f, 0.2f, 1));
            AddLine(positions, colors, center, center + new Vector3(0, len, 0), new Vector4(0.2f, 1, 0.2f, 1));
            AddLine(positions, colors, center, center + new Vector3(0, 0, len), new Vector4(0.2f, 0.4f, 1, 1));

            // 箭头 (三角锥简化: 三条线段)
            float arrowSize = len * 0.15f;
            DrawArrowHead(positions, colors, center + new Vector3(len, 0, 0), new Vector3(1, 0, 0), arrowSize, new Vector4(1, 0.2f, 0.2f, 1));
            DrawArrowHead(positions, colors, center + new Vector3(0, len, 0), new Vector3(0, 1, 0), arrowSize, new Vector4(0.2f, 1, 0.2f, 1));
            DrawArrowHead(positions, colors, center + new Vector3(0, 0, len), new Vector3(0, 0, 1), arrowSize, new Vector4(0.2f, 0.4f, 1, 1));

            _sceneRenderer.RenderLines(cmd, positions.ToArray(), colors.ToArray(), camera);
        }

        // 旋转 Gizmo: 三轴圆环 (X 红 / Y 绿 / Z 蓝)
        private void DrawRotateGizmo(RHICommandList cmd, in CameraData camera, Vector3 center, float radius)
        {
            var positions = new List<Vector3>();
            var colors = new List<Vector4>();
            int segments = 32;

            // X轴圆环 (YZ平面)
            DrawArc(positions, colors, center, Vector3.UnitX, radius, 0, MathF.PI * 2, segments, new Vector4(1, 0.2f, 0.2f, 1));
            // Y轴圆环 (XZ平面)
            DrawArc(positions, colors, center, Vector3.UnitY, radius, 0, MathF.PI * 2, segments, new Vector4(0.2f, 1, 0.2f, 1));
            // Z轴圆环 (XY平面)
            DrawArc(positions, colors, center, Vector3.UnitZ, radius, 0, MathF.PI * 2, segments, new Vector4(0.2f, 0.4f, 1, 1));

            _sceneRenderer.RenderLines(cmd, positions.ToArray(), colors.ToArray(), camera);
        }

        // 缩放 Gizmo: 三轴 + 方块端点
        private void DrawScaleGizmo(RHICommandList cmd, in CameraData camera, Vector3 center, float len)
        {
            var positions = new List<Vector3>();
            var colors = new List<Vector4>();

            // 三轴线段
            AddLine(positions, colors, center, center + new Vector3(len, 0, 0), new Vector4(1, 0.2f, 0.2f, 1));
            AddLine(positions, colors, center, center + new Vector3(0, len, 0), new Vector4(0.2f, 1, 0.2f, 1));
            AddLine(positions, colors, center, center + new Vector3(0, 0, len), new Vector4(0.2f, 0.4f, 1, 1));

            // 方块端点
            float boxSize = len * 0.08f;
            DrawWireBox(positions, colors, center + new Vector3(len, 0, 0), boxSize, new Vector4(1, 0.2f, 0.2f, 1));
            DrawWireBox(positions, colors, center + new Vector3(0, len, 0), boxSize, new Vector4(0.2f, 1, 0.2f, 1));
            DrawWireBox(positions, colors, center + new Vector3(0, 0, len), boxSize, new Vector4(0.2f, 0.4f, 1, 1));

            _sceneRenderer.RenderLines(cmd, positions.ToArray(), colors.ToArray(), camera);
        }

        // ── Gizmo 几何辅助 ──

        private static void AddLine(List<Vector3> positions, List<Vector4> colors, Vector3 a, Vector3 b, Vector4 color)
        {
            positions.Add(a); colors.Add(color);
            positions.Add(b); colors.Add(color);
        }

        private static void DrawArrowHead(List<Vector3> positions, List<Vector4> colors, Vector3 tip, Vector3 axis, float size, Vector4 color)
        {
            // 找一个垂直于 axis 的向量
            Vector3 perp = Vector3.Cross(axis, Vector3.UnitY);
            if (perp.LengthSquared() < 0.001f) perp = Vector3.Cross(axis, Vector3.UnitZ);
            perp = Vector3.Normalize(perp);
            Vector3 perp2 = Vector3.Normalize(Vector3.Cross(axis, perp));

            Vector3 basePt = tip - axis * size;
            for (int i = 0; i < 3; i++)
            {
                float angle = i * MathF.PI * 2f / 3f;
                Vector3 offset = perp * MathF.Cos(angle) * size * 0.5f + perp2 * MathF.Sin(angle) * size * 0.5f;
                AddLine(positions, colors, tip, basePt + offset, color);
            }
        }

        private static void DrawArc(List<Vector3> positions, List<Vector4> colors, Vector3 center, Vector3 normal,
            float radius, float startAngle, float endAngle, int segments, Vector4 color)
        {
            Vector3 tangent = Vector3.Cross(normal, Vector3.UnitY);
            if (tangent.LengthSquared() < 0.001f) tangent = Vector3.Cross(normal, Vector3.UnitZ);
            tangent = Vector3.Normalize(tangent);
            Vector3 bitangent = Vector3.Normalize(Vector3.Cross(normal, tangent));

            float angleStep = (endAngle - startAngle) / segments;
            for (int i = 0; i < segments; i++)
            {
                float a1 = startAngle + i * angleStep;
                float a2 = startAngle + (i + 1) * angleStep;
                Vector3 p1 = center + tangent * MathF.Cos(a1) * radius + bitangent * MathF.Sin(a1) * radius;
                Vector3 p2 = center + tangent * MathF.Cos(a2) * radius + bitangent * MathF.Sin(a2) * radius;
                AddLine(positions, colors, p1, p2, color);
            }
        }

        private static void DrawWireBox(List<Vector3> positions, List<Vector4> colors, Vector3 center, float size, Vector4 color)
        {
            float h = size * 0.5f;
            var corners = new[]
            {
                center + new Vector3(-h, -h, -h), center + new Vector3(h, -h, -h),
                center + new Vector3(-h, h, -h), center + new Vector3(h, h, -h),
                center + new Vector3(-h, -h, h), center + new Vector3(h, -h, h),
                center + new Vector3(-h, h, h), center + new Vector3(h, h, h)
            };
            int[] edges = { 0,1, 2,3, 4,5, 6,7, 0,2, 1,3, 4,6, 5,7, 0,4, 1,5, 2,6, 3,7 };
            for (int i = 0; i < edges.Length; i += 2)
                AddLine(positions, colors, corners[edges[i]], corners[edges[i + 1]], color);
        }

        /// <summary>处理点击拾取 (鼠标左键单击, 非拖拽)。</summary>
        public SceneObject HandlePick(Vector2 screenPx, bool additive)
        {
            var ray = Camera.ScreenToWorldRay(screenPx, Size);
            var hit = RayPicking.Pick(_assets, ray);
            _assets.SelectObject(hit, additive);
            return hit;
        }

        /// <summary>处理资源拖放到视口。</summary>
        /// <param name="assetPath">被拖入的资源路径。</param>
        /// <param name="dropPos">释放位置 (像素)。</param>
        public void HandleDropAsset(string assetPath, Vector2 dropPos)
        {
            if (string.IsNullOrWhiteSpace(assetPath)) return;
            // 将屏幕位置投影到地面 (y=0) 作为放置点
            var ray = Camera.ScreenToWorldRay(dropPos, Size);
            Vector3 placePos = Vector3.Zero;
            if (MathF.Abs(ray.Direction.Y) > 1e-4f)
            {
                float t = -ray.Origin.Y / ray.Direction.Y;
                if (t > 0) placePos = ray.Origin + ray.Direction * t;
            }
            var meta = _assets.GetAsset(assetPath);
            string meshType = meta?.Type == AssetType.Mesh ? "Box" : "Box";
            _assets.CreateObject(Path.GetFileNameWithoutExtension(assetPath), meshType, placePos);
        }

        /// <summary>IMGUI 绘制: 视口图像 + 叠加 UI。</summary>
        public void DrawImGui()
        {
            // 由宿主调用 ImGui.Image(ColorTexture SRV, Size);
            // 此处执行叠加回调 (如变换数值输入 / 视口工具栏)
            OnImGuiOverlay?.Invoke(Size);
            foreach (var sub in _subViewports) sub.DrawImGui();
        }

        /// <summary>聚焦到当前选择。</summary>
        public void FrameSelection()
        {
            var sel = _assets.GetSelectedObjects();
            if (sel.Count == 0) { Camera.FrameBounds(_assets.CalculateSceneBounds()); return; }
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            foreach (var o in sel)
            {
                var b = o.Bounds.Transform(o.LocalToWorldMatrix);
                min = Vector3.Min(min, b.Min); max = Vector3.Max(max, b.Max);
            }
            Camera.FrameBounds(new BoundingBox { Min = min, Max = max });
        }

        public void Dispose()
        {
            if (_disposed) return;
            foreach (var sub in _subViewports) sub.Dispose();
            _particleRenderer?.Dispose();
            _sceneRenderer?.Dispose();
            _textureManager?.Dispose();
            _postProcess?.Dispose();
            RenderTarget?.Dispose();
            _disposed = true;
        }
    }
}
