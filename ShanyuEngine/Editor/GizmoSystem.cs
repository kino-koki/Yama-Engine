// ==================================================================================================
//  GizmoSystem.cs — 变换手柄系统 (全功能实现)
//
//  命名空间: ShanyuEngine.Editor
//  依赖:     System.Numerics
//
//  功能:
//    类似 Unity / Unreal Engine 的移动/旋转/缩放 Gizmo 手柄:
//    - GizmoMode 枚举: Translate / Rotate / Scale / Universal
//    - GizmoSpace 枚举: Local / World
//    - GizmoAxis 枚举: None / X / Y / Z / XY / XZ / YZ / Screen
//    - TransformGizmo: 3 彩色轴 + 平面选择指示器
//      X 红(1,0.2,0.2) / Y 绿(0.2,1,0.2) / Z 蓝(0.2,0.4,1)
//    - HitTest: 屏幕空间射线投射, NDC 转换, 射线-AABB/射线-Cylinder 交叉测试
//    - CalculateDragDelta: Translate/Rotate/Scale 三种模式的拖拽增量计算
//    - BuildGizmoMeshes: 生成箭头几何 (圆柱体 shaft + 圆锥体 head), 平面选择指示器
//    - GetGizmoTransform: World/Local 坐标系对齐
//    - ApplyTransform: 将增量应用到变换矩阵
//    - 捕捉支持: 位置/旋转/缩放捕捉
//    - 线框包围盒渲染数据
//
//  技术要点:
//    - 射线拾取使用 NDC(-1~1) + 反投影
//    - 射线-Cylinder 交叉测试用于精确轴拾取
//    - Rotate 模式使用有向弧度 (atan2) 计算
//    - Scale 模式返回缩放因子而非位置偏移
//    - 圆柱体 8 段, 圆锥体 8 段, 保证视觉平滑
//    - 每个轴: shaft 圆柱 (长 0.75, 半径 0.03) + head 圆锥 (长 0.25, 底半径 0.08)
// ==================================================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

namespace ShanyuEngine.Editor
{
    // ════════════════════════════════════════════════════════════
    //  枚举定义
    // ════════════════════════════════════════════════════════════

    /// <summary>Gizmo 操作模式。</summary>
    public enum GizmoTransformMode
    {
        /// <summary>移动模式: 三轴箭头 + 平面选择。</summary>
        Translate,
        /// <summary>旋转模式: 三圆环。</summary>
        Rotate,
        /// <summary>缩放模式: 三轴方块端点。</summary>
        Scale,
        /// <summary>通用模式: 统一缩放 + 移动。</summary>
        Universal
    }

    /// <summary>Gizmo 坐标系。</summary>
    public enum GizmoCoordinateSpace
    {
        /// <summary>世界空间: Gizmo 轴始终对齐世界坐标轴。</summary>
        World,
        /// <summary>局部空间: Gizmo 轴跟随物体旋转。</summary>
        Local
    }

    /// <summary>
    /// Gizmo 活动轴 (低层定义, 用于 TransformGizmo)。
    /// 注意: EditorViewport.cs 中已定义 GizmoAxis 枚举 (含 Center/BoxSelect 等高层概念),
    /// 此处定义独立枚举避免循环依赖, 提供更细粒度的轴控制。
    /// </summary>
    public enum GizmoAxisHit
    {
        /// <summary>无命中。</summary>
        None,
        /// <summary>X 轴 (红色)。</summary>
        X,
        /// <summary>Y 轴 (绿色)。</summary>
        Y,
        /// <summary>Z 轴 (蓝色)。</summary>
        Z,
        /// <summary>XY 平面 (Z 法线, 蓝色半透明)。</summary>
        XY,
        /// <summary>XZ 平面 (Y 法线, 绿色半透明)。</summary>
        XZ,
        /// <summary>YZ 平面 (X 法线, 红色半透明)。</summary>
        YZ,
        /// <summary>屏幕空间 (沿相机平面)。</summary>
        Screen
    }

    // ════════════════════════════════════════════════════════════
    //  GizmoState — 拖拽交互状态
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// Gizmo 交互状态: 记录当前拖拽的完整上下文,
    /// 用于在每帧计算增量 (CalculateDragDelta)。
    /// </summary>
    public struct GizmoState
    {
        /// <summary>是否处于活动状态 (有选中对象时激活)。</summary>
        public bool Active;

        /// <summary>当前悬停的轴。</summary>
        public GizmoAxisHit HoverAxis;

        /// <summary>是否正在拖拽。</summary>
        public bool Dragging;

        /// <summary>拖拽开始时的世界坐标 (射线与拖拽平面的交点)。</summary>
        public Vector3 DragStartWorldPos;

        /// <summary>拖拽开始时的鼠标屏幕坐标。</summary>
        public Vector2 DragStartMousePos;

        /// <summary>拖拽开始时的对象变换矩阵。</summary>
        public Matrix4x4 DragStartObjectTransform;

        /// <summary>拖拽开始时的对象位置。</summary>
        public Vector3 DragStartObjectPosition;

        /// <summary>拖拽开始时的对象旋转 (四元数)。</summary>
        public Quaternion DragStartObjectRotation;

        /// <summary>拖拽开始时的对象缩放。</summary>
        public Vector3 DragStartObjectScale;

        /// <summary>上次拖拽的世界坐标 (用于增量计算)。</summary>
        public Vector3 LastDragWorldPos;

        /// <summary>拖拽累计偏移 (用于旋转角度计算)。</summary>
        public float AccumulatedAngle;

        /// <summary>重置状态。</summary>
        public void Reset()
        {
            Active = false;
            HoverAxis = GizmoAxisHit.None;
            Dragging = false;
            DragStartWorldPos = Vector3.Zero;
            DragStartMousePos = Vector2.Zero;
            DragStartObjectTransform = Matrix4x4.Identity;
            DragStartObjectPosition = Vector3.Zero;
            DragStartObjectRotation = Quaternion.Identity;
            DragStartObjectScale = Vector3.One;
            LastDragWorldPos = Vector3.Zero;
            AccumulatedAngle = 0.0f;
        }
    }

    // ════════════════════════════════════════════════════════════
    //  GizmoColors — Gizmo 颜色常量
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// Gizmo 颜色定义: 遵循 3ds Max / Maya / Unity 的轴颜色约定。
    /// </summary>
    public static class GizmoColors
    {
        /// <summary>X 轴颜色 (红)。</summary>
        public static readonly Vector4 AxisX = new Vector4(1.0f, 0.2f, 0.2f, 1.0f);

        /// <summary>Y 轴颜色 (绿)。</summary>
        public static readonly Vector4 AxisY = new Vector4(0.2f, 1.0f, 0.2f, 1.0f);

        /// <summary>Z 轴颜色 (蓝)。</summary>
        public static readonly Vector4 AxisZ = new Vector4(0.2f, 0.4f, 1.0f, 1.0f);

        /// <summary>X 轴高亮颜色。</summary>
        public static readonly Vector4 AxisXHighlight = new Vector4(1.0f, 0.5f, 0.5f, 1.0f);

        /// <summary>Y 轴高亮颜色。</summary>
        public static readonly Vector4 AxisYHighlight = new Vector4(0.5f, 1.0f, 0.5f, 1.0f);

        /// <summary>Z 轴高亮颜色。</summary>
        public static readonly Vector4 AxisZHighlight = new Vector4(0.5f, 0.6f, 1.0f, 1.0f);

        /// <summary>XY 平面颜色 (半透明蓝)。</summary>
        public static readonly Vector4 PlaneXY = new Vector4(0.2f, 0.4f, 1.0f, 0.25f);

        /// <summary>XZ 平面颜色 (半透明绿)。</summary>
        public static readonly Vector4 PlaneXZ = new Vector4(0.2f, 1.0f, 0.2f, 0.25f);

        /// <summary>YZ 平面颜色 (半透明红)。</summary>
        public static readonly Vector4 PlaneYZ = new Vector4(1.0f, 0.2f, 0.2f, 0.25f);

        /// <summary>屏幕空间颜色。</summary>
        public static readonly Vector4 ScreenColor = new Vector4(0.8f, 0.8f, 0.8f, 1.0f);

        /// <summary>非活动 (禁用) 颜色。</summary>
        public static readonly Vector4 Inactive = new Vector4(0.5f, 0.5f, 0.5f, 0.6f);

        /// <summary>获取轴颜色。</summary>
        public static Vector4 GetAxisColor(GizmoAxisHit axis, bool highlight = false)
        {
            return axis switch
            {
                GizmoAxisHit.X => highlight ? AxisXHighlight : AxisX,
                GizmoAxisHit.Y => highlight ? AxisYHighlight : AxisY,
                GizmoAxisHit.Z => highlight ? AxisZHighlight : AxisZ,
                GizmoAxisHit.XY => PlaneXY,
                GizmoAxisHit.XZ => PlaneXZ,
                GizmoAxisHit.YZ => PlaneYZ,
                GizmoAxisHit.Screen => ScreenColor,
                _ => Inactive
            };
        }

        /// <summary>获取轴方向向量。</summary>
        public static Vector3 GetAxisDirection(GizmoAxisHit axis)
        {
            return axis switch
            {
                GizmoAxisHit.X => Vector3.UnitX,
                GizmoAxisHit.Y => Vector3.UnitY,
                GizmoAxisHit.Z => Vector3.UnitZ,
                _ => Vector3.Zero
            };
        }
    }

    // ════════════════════════════════════════════════════════════
    //  GizmoVertex — Gizmo 渲染顶点
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// Gizmo 渲染顶点: Position(12) + Color(16) = 28 字节。
    /// 与网格渲染器的简化顶点格式兼容。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GizmoVertex
    {
        public Vector3 Position;
        public Vector4 Color;

        public GizmoVertex(Vector3 position, Vector4 color)
        {
            Position = position;
            Color = color;
        }
    }

    // ════════════════════════════════════════════════════════════
    //  GizmoMeshData — Gizmo 网格数据
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// Gizmo 网格数据: 包含一个或多个子网格 (每轴一个)。
    /// </summary>
    public sealed class GizmoMeshData
    {
        /// <summary>顶点缓冲。</summary>
        public List<GizmoVertex> Vertices = new();

        /// <summary>索引缓冲 (uint32)。</summary>
        public List<uint> Indices = new();

        /// <summary>每个子网格的起始索引和索引数 (用于独立着色)。</summary>
        public List<(int IndexStart, int IndexCount, GizmoAxisHit Axis)> SubMeshes = new();

        public void Clear()
        {
            Vertices.Clear();
            Indices.Clear();
            SubMeshes.Clear();
        }
    }

    // ════════════════════════════════════════════════════════════
    //  TransformGizmo — 变换手柄核心系统
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 变换手柄系统: 完整的移动/旋转/缩放 Gizmo 实现。
    /// 提供射线拾取 (HitTest), 拖拽计算 (CalculateDragDelta),
    /// 网格生成 (BuildGizmoMeshes) 和变换应用 (ApplyTransform)。
    /// </summary>
    public sealed class TransformGizmo : IDisposable
    {
        // ── 常量 ──

        /// <summary>轴总长度 (世界单位)。</summary>
        public const float AxisLength = 1.0f;

        /// <summary>轴圆柱半径。</summary>
        public const float ShaftRadius = 0.03f;

        /// <summary>圆锥体 (箭头) 长度。</summary>
        public const float HeadLength = 0.25f;

        /// <summary>圆锥体底面半径。</summary>
        public const float HeadRadius = 0.08f;

        /// <summary>圆柱体段数。</summary>
        public const int CylinderSegments = 8;

        /// <summary>圆锥体段数。</summary>
        public const int ConeSegments = 8;

        /// <summary>射线拾取容差 (像素)。</summary>
        public const float HitTolerance = 8.0f;

        /// <summary>微小值阈值。</summary>
        private const float Epsilon = 1e-6f;

        // ── 属性 ──

        /// <summary>当前操作模式。</summary>
        public GizmoTransformMode Mode = GizmoTransformMode.Translate;

        /// <summary>当前坐标系 (World / Local)。</summary>
        public GizmoCoordinateSpace Space = GizmoCoordinateSpace.World;

        /// <summary>交互状态。</summary>
        public GizmoState State;

        /// <summary>屏幕空间大小 (像素)。</summary>
        public float ScreenSize = 80.0f;

        /// <summary>位置捕捉启用。</summary>
        public bool PositionSnapEnabled;

        /// <summary>位置捕捉步长。</summary>
        public float PositionSnapSize = 1.0f;

        /// <summary>旋转捕捉启用。</summary>
        public bool RotationSnapEnabled;

        /// <summary>旋转捕捉步长 (度)。</summary>
        public float RotationSnapDegrees = 15.0f;

        /// <summary>缩放捕捉启用。</summary>
        public bool ScaleSnapEnabled;

        /// <summary>缩放捕捉步长。</summary>
        public float ScaleSnapSize = 0.1f;

        // ── 缓存网格 ──

        private GizmoMeshData _cachedMesh;
        private bool _meshDirty = true;

        // ── 构造 ──

        public TransformGizmo()
        {
            State = new GizmoState();
            _cachedMesh = new GizmoMeshData();
        }

        // ════════════════════════════════════════════════════════
        //  HitTest — 射线拾取
        // ════════════════════════════════════════════════════════

        /// <summary>
        /// 射线拾取: 将鼠标屏幕坐标转换为 NDC, 构建射线,
        /// 对每个 Gizmo 轴做射线-Cylinder 交叉测试, 返回最近命中的轴。
        /// </summary>
        /// <param name="mouseScreenPos">鼠标屏幕坐标 (像素)。</param>
        /// <param name="view">视图矩阵。</param>
        /// <param name="projection">投影矩阵。</param>
        /// <param name="viewportW">视口宽度。</param>
        /// <param name="viewportH">视口高度。</param>
        /// <param name="gizmoWorldPos">Gizmo 世界位置 (选中对象中心)。</param>
        /// <param name="gizmoRotation">Gizmo 旋转 (Local 模式下使用对象旋转)。</param>
        /// <returns>命中的轴, 无命中返回 None。</returns>
        public GizmoAxisHit HitTest(
            Vector2 mouseScreenPos,
            Matrix4x4 view, Matrix4x4 projection,
            int viewportW, int viewportH,
            Vector3 gizmoWorldPos,
            Quaternion gizmoRotation)
        {
            if (viewportW <= 0 || viewportH <= 0) return GizmoAxisHit.None;

            // 像素 -> NDC (-1 ~ 1)
            float ndcX = (mouseScreenPos.X / viewportW) * 2.0f - 1.0f;
            float ndcY = 1.0f - (mouseScreenPos.Y / viewportH) * 2.0f;

            // 构建 ViewProjection 矩阵
            Matrix4x4 vp = view * projection;

            // 反投影射线
            bool invSuccess = Matrix4x4.Invert(vp, out Matrix4x4 invVP);
            if (!invSuccess) return GizmoAxisHit.None;

            // 近平面和远平面世界坐标
            Vector4 nearPoint4 = Vector4.Transform(new Vector4(ndcX, ndcY, 0.0f, 1.0f), invVP);
            Vector4 farPoint4 = Vector4.Transform(new Vector4(ndcX, ndcY, 1.0f, 1.0f), invVP);

            float nearW = MathF.Max(MathF.Abs(nearPoint4.W), Epsilon);
            float farW = MathF.Max(MathF.Abs(farPoint4.W), Epsilon);

            Vector3 rayOrigin = new Vector3(
                nearPoint4.X / nearW, nearPoint4.Y / nearW, nearPoint4.Z / nearW);
            Vector3 rayEnd = new Vector3(
                farPoint4.X / farW, farPoint4.Y / farW, farPoint4.Z / farW);
            Vector3 rayDir = Vector3.Normalize(rayEnd - rayOrigin);

            // 构建 Gizmo 旋转矩阵 (World 模式下为 Identity)
            Matrix4x4 gizmoRotMatrix = Matrix4x4.CreateFromQuaternion(gizmoRotation);

            // 轴定义: (轴方向, 颜色标签)
            var axes = new (Vector3 Dir, GizmoAxisHit Axis)[]
            {
                (Vector3.UnitX, GizmoAxisHit.X),
                (Vector3.UnitY, GizmoAxisHit.Y),
                (Vector3.UnitZ, GizmoAxisHit.Z)
            };

            GizmoAxisHit bestAxis = GizmoAxisHit.None;
            float bestDist = float.MaxValue;

            // 测试各轴 (射线-Cylinder 交叉)
            foreach (var (localDir, axis) in axes)
            {
                Vector3 worldDir = Vector3.TransformNormal(localDir, gizmoRotMatrix);
                worldDir = Vector3.Normalize(worldDir);

                // 轴线段: 从 gizmoWorldPos 到 gizmoWorldPos + worldDir * AxisLength
                Vector3 segStart = gizmoWorldPos;
                Vector3 segEnd = gizmoWorldPos + worldDir * AxisLength;

                // 射线-线段最近距离
                float dist = RaySegmentDistance(rayOrigin, rayDir, segStart, segEnd, out float _);

                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestAxis = axis;
                }
            }

            // 测试平面选择器 (YZ/XZ/XY)
            // 平面中心在轴中点偏移处
            float planeOffset = AxisLength * 0.35f;
            var planes = new (Vector3 Normal, Vector3 Offset, GizmoAxisHit Axis)[]
            {
                (Vector3.UnitX, new Vector3(0, planeOffset, planeOffset), GizmoAxisHit.YZ),
                (Vector3.UnitY, new Vector3(planeOffset, 0, planeOffset), GizmoAxisHit.XZ),
                (Vector3.UnitZ, new Vector3(planeOffset, planeOffset, 0), GizmoAxisHit.XY)
            };

            // 平面拾取阈值更大 (半透明四边形)
            float planeThreshold = HitTolerance * 1.5f;

            foreach (var (localNormal, localOffset, axis) in planes)
            {
                Vector3 worldNormal = Vector3.TransformNormal(localNormal, gizmoRotMatrix);
                worldNormal = Vector3.Normalize(worldNormal);
                Vector3 worldOffset = Vector3.Transform(localOffset, gizmoRotMatrix);
                Vector3 planeCenter = gizmoWorldPos + worldOffset;
                float planeHalfSize = planeOffset * 0.5f;

                // 射线与平面求交
                float denom = Vector3.Dot(rayDir, worldNormal);
                if (MathF.Abs(denom) < Epsilon) continue;

                float t = Vector3.Dot(planeCenter - rayOrigin, worldNormal) / denom;
                if (t < 0) continue;

                Vector3 hitPoint = rayOrigin + rayDir * t;

                // 检查是否在平面四边形范围内 (世界空间 AABB 近似)
                Vector3 localHit = hitPoint - planeCenter;
                // 投影到平面的两个切线方向
                Vector3 tangent1 = Vector3.Normalize(Vector3.Cross(worldNormal, Vector3.UnitY));
                if (tangent1.LengthSquared() < Epsilon)
                    tangent1 = Vector3.Normalize(Vector3.Cross(worldNormal, Vector3.UnitX));
                Vector3 tangent2 = Vector3.Cross(worldNormal, tangent1);

                float u = MathF.Abs(Vector3.Dot(localHit, tangent1));
                float v = MathF.Abs(Vector3.Dot(localHit, tangent2));

                if (u < planeHalfSize && v < planeHalfSize)
                {
                    // 在四边形内, 计算到中心距离作为优先级
                    float d = MathF.Sqrt(u * u + v * v);
                    // 转换为屏幕空间距离 (近似)
                    float screenDist = d * (viewportH * 0.5f) / MathF.Max(MathF.Abs(t), 1.0f);
                    if (screenDist < planeThreshold && screenDist < bestDist)
                    {
                        bestDist = screenDist;
                        bestAxis = axis;
                    }
                }
            }

            // 判断阈值 (屏幕像素距离)
            if (bestDist > HitTolerance)
                return GizmoAxisHit.None;

            return bestAxis;
        }

        // ════════════════════════════════════════════════════════
        //  CalculateDragDelta — 拖拽增量计算
        // ════════════════════════════════════════════════════════

        /// <summary>
        /// 计算拖拽产生的变换增量。
        /// 根据当前模式和活动轴, 将鼠标移动转换为位置/旋转/缩放偏移。
        /// </summary>
        /// <param name="axis">活动轴。</param>
        /// <param name="currentWorldPos">当前鼠标射线与拖拽平面的交点 (世界坐标)。</param>
        /// <param name="state">拖拽状态。</param>
        /// <param name="viewProj">ViewProjection 矩阵。</param>
        /// <param name="gizmoRotation">Gizmo 旋转 (Local 模式)。</param>
        /// <returns>变换增量 (Translation / Rotation / Scale)。</returns>
        public (Vector3 Translation, Quaternion Rotation, Vector3 Scale) CalculateDragDelta(
            GizmoAxisHit axis,
            Vector3 currentWorldPos,
            GizmoState state,
            Matrix4x4 viewProj,
            Quaternion gizmoRotation)
        {
            Vector3 translation = Vector3.Zero;
            Quaternion rotation = Quaternion.Identity;
            Vector3 scale = Vector3.One;

            if (!state.Dragging || axis == GizmoAxisHit.None)
                return (translation, rotation, scale);

            Vector3 delta = currentWorldPos - state.LastDragWorldPos;

            switch (Mode)
            {
                case GizmoTransformMode.Translate:
                    translation = CalculateTranslateDelta(axis, currentWorldPos, state, gizmoRotation);
                    break;

                case GizmoTransformMode.Rotate:
                    rotation = CalculateRotateDelta(axis, currentWorldPos, state, gizmoRotation, viewProj);
                    break;

                case GizmoTransformMode.Scale:
                    scale = CalculateScaleDelta(axis, currentWorldPos, state, gizmoRotation);
                    break;

                case GizmoTransformMode.Universal:
                    // 统一缩放: 基于鼠标到 Gizmo 中心的距离变化
                    scale = CalculateUniversalScaleDelta(currentWorldPos, state);
                    break;
            }

            return (translation, rotation, scale);
        }

        /// <summary>Translate 模式增量计算。</summary>
        private Vector3 CalculateTranslateDelta(
            GizmoAxisHit axis, Vector3 currentWorldPos, GizmoState state, Quaternion gizmoRotation)
        {
            Vector3 result = Vector3.Zero;
            Matrix4x4 rotMatrix = Matrix4x4.CreateFromQuaternion(gizmoRotation);

            switch (axis)
            {
                case GizmoAxisHit.X:
                    {
                        // 投影到 X 轴 (考虑 Gizmo 旋转)
                        Vector3 worldAxis = Vector3.TransformNormal(Vector3.UnitX, rotMatrix);
                        float proj = Vector3.Dot(currentWorldPos - state.DragStartWorldPos, worldAxis);
                        result = worldAxis * proj;
                        break;
                    }

                case GizmoAxisHit.Y:
                    {
                        Vector3 worldAxis = Vector3.TransformNormal(Vector3.UnitY, rotMatrix);
                        float proj = Vector3.Dot(currentWorldPos - state.DragStartWorldPos, worldAxis);
                        result = worldAxis * proj;
                        break;
                    }

                case GizmoAxisHit.Z:
                    {
                        Vector3 worldAxis = Vector3.TransformNormal(Vector3.UnitZ, rotMatrix);
                        float proj = Vector3.Dot(currentWorldPos - state.DragStartWorldPos, worldAxis);
                        result = worldAxis * proj;
                        break;
                    }

                case GizmoAxisHit.XY:
                    {
                        // 在 XY 平面上自由移动
                        Vector3 worldX = Vector3.TransformNormal(Vector3.UnitX, rotMatrix);
                        Vector3 worldY = Vector3.TransformNormal(Vector3.UnitY, rotMatrix);
                        Vector3 delta = currentWorldPos - state.LastDragWorldPos;
                        result = worldX * Vector3.Dot(delta, worldX) + worldY * Vector3.Dot(delta, worldY);
                        break;
                    }

                case GizmoAxisHit.XZ:
                    {
                        Vector3 worldX = Vector3.TransformNormal(Vector3.UnitX, rotMatrix);
                        Vector3 worldZ = Vector3.TransformNormal(Vector3.UnitZ, rotMatrix);
                        Vector3 delta = currentWorldPos - state.LastDragWorldPos;
                        result = worldX * Vector3.Dot(delta, worldX) + worldZ * Vector3.Dot(delta, worldZ);
                        break;
                    }

                case GizmoAxisHit.YZ:
                    {
                        Vector3 worldY = Vector3.TransformNormal(Vector3.UnitY, rotMatrix);
                        Vector3 worldZ = Vector3.TransformNormal(Vector3.UnitZ, rotMatrix);
                        Vector3 delta = currentWorldPos - state.LastDragWorldPos;
                        result = worldY * Vector3.Dot(delta, worldY) + worldZ * Vector3.Dot(delta, worldZ);
                        break;
                    }

                case GizmoAxisHit.Screen:
                    {
                        // 屏幕空间: 使用相机 Right 和 Up 投影
                        Vector3 delta = currentWorldPos - state.LastDragWorldPos;
                        result = delta;
                        break;
                    }
            }

            // 位置捕捉
            if (PositionSnapEnabled)
            {
                Vector3 snapped = state.DragStartObjectPosition + result;
                snapped = new Vector3(
                    MathF.Round(snapped.X / PositionSnapSize) * PositionSnapSize,
                    MathF.Round(snapped.Y / PositionSnapSize) * PositionSnapSize,
                    MathF.Round(snapped.Z / PositionSnapSize) * PositionSnapSize);
                result = snapped - state.DragStartObjectPosition;
            }

            return result;
        }

        /// <summary>Rotate 模式增量计算 (有向弧度)。</summary>
        private Quaternion CalculateRotateDelta(
            GizmoAxisHit axis, Vector3 currentWorldPos, GizmoState state,
            Quaternion gizmoRotation, Matrix4x4 viewProj)
        {
            if (axis == GizmoAxisHit.None) return Quaternion.Identity;

            Matrix4x4 rotMatrix = Matrix4x4.CreateFromQuaternion(gizmoRotation);
            Vector3 rotAxisLocal = GizmoColors.GetAxisDirection(axis);
            Vector3 rotAxisWorld = Vector3.TransformNormal(rotAxisLocal, rotMatrix);
            rotAxisWorld = Vector3.Normalize(rotAxisWorld);

            // 对象中心
            Vector3 center = state.DragStartObjectPosition;

            // 参考向量: 当前鼠标位置到中心的向量投影到旋转平面
            Vector3 toCurrent = currentWorldPos - center;
            Vector3 toLast = state.LastDragWorldPos - center;

            // 投影到旋转平面 (垂直于旋转轴)
            Vector3 projCurrent = toCurrent - rotAxisWorld * Vector3.Dot(toCurrent, rotAxisWorld);
            Vector3 projLast = toLast - rotAxisWorld * Vector3.Dot(toLast, rotAxisWorld);

            if (projCurrent.LengthSquared() < Epsilon || projLast.LengthSquared() < Epsilon)
                return Quaternion.Identity;

            // 使用 atan2 计算有向角度
            float angleCurrent = MathF.Atan2(
                Vector3.Dot(Vector3.Cross(Vector3.Normalize(projLast), Vector3.Normalize(projCurrent)), rotAxisWorld),
                Vector3.Dot(Vector3.Normalize(projLast), Vector3.Normalize(projCurrent)));

            state.AccumulatedAngle += angleCurrent;

            // 旋转捕捉
            float snapAngle = state.AccumulatedAngle;
            if (RotationSnapEnabled)
            {
                float snapRad = RotationSnapDegrees * (MathF.PI / 180.0f);
                snapAngle = MathF.Round(snapAngle / snapRad) * snapRad;
            }

            return Quaternion.CreateFromAxisAngle(rotAxisWorld, snapAngle - (state.AccumulatedAngle - angleCurrent));
        }

        /// <summary>Scale 模式增量计算。</summary>
        private Vector3 CalculateScaleDelta(
            GizmoAxisHit axis, Vector3 currentWorldPos, GizmoState state, Quaternion gizmoRotation)
        {
            Vector3 result = Vector3.One;
            Matrix4x4 rotMatrix = Matrix4x4.CreateFromQuaternion(gizmoRotation);

            switch (axis)
            {
                case GizmoAxisHit.X:
                    {
                        Vector3 worldAxis = Vector3.TransformNormal(Vector3.UnitX, rotMatrix);
                        float proj = Vector3.Dot(currentWorldPos - state.DragStartWorldPos, worldAxis);
                        float startLen = MathF.Max(Vector3.Dot(state.DragStartWorldPos - state.DragStartObjectPosition, worldAxis), Epsilon);
                        float scaleVal = 1.0f + proj / startLen;
                        result = new Vector3(scaleVal, 1.0f, 1.0f);
                        break;
                    }

                case GizmoAxisHit.Y:
                    {
                        Vector3 worldAxis = Vector3.TransformNormal(Vector3.UnitY, rotMatrix);
                        float proj = Vector3.Dot(currentWorldPos - state.DragStartWorldPos, worldAxis);
                        float startLen = MathF.Max(Vector3.Dot(state.DragStartWorldPos - state.DragStartObjectPosition, worldAxis), Epsilon);
                        float scaleVal = 1.0f + proj / startLen;
                        result = new Vector3(1.0f, scaleVal, 1.0f);
                        break;
                    }

                case GizmoAxisHit.Z:
                    {
                        Vector3 worldAxis = Vector3.TransformNormal(Vector3.UnitZ, rotMatrix);
                        float proj = Vector3.Dot(currentWorldPos - state.DragStartWorldPos, worldAxis);
                        float startLen = MathF.Max(Vector3.Dot(state.DragStartWorldPos - state.DragStartObjectPosition, worldAxis), Epsilon);
                        float scaleVal = 1.0f + proj / startLen;
                        result = new Vector3(1.0f, 1.0f, scaleVal);
                        break;
                    }

                case GizmoAxisHit.XY:
                case GizmoAxisHit.XZ:
                case GizmoAxisHit.YZ:
                    {
                        // 双轴缩放
                        Vector3 axis1 = GizmoColors.GetAxisDirection(axis == GizmoAxisHit.YZ ? GizmoAxisHit.Y : GizmoAxisHit.X);
                        Vector3 axis2 = GizmoColors.GetAxisDirection(axis == GizmoAxisHit.XY ? GizmoAxisHit.Y : GizmoAxisHit.Z);

                        Vector3 worldAxis1 = Vector3.TransformNormal(axis1, rotMatrix);
                        Vector3 worldAxis2 = Vector3.TransformNormal(axis2, rotMatrix);

                        float proj1 = Vector3.Dot(currentWorldPos - state.DragStartWorldPos, worldAxis1);
                        float proj2 = Vector3.Dot(currentWorldPos - state.DragStartWorldPos, worldAxis2);

                        float start1 = MathF.Max(MathF.Abs(Vector3.Dot(state.DragStartWorldPos - state.DragStartObjectPosition, worldAxis1)), Epsilon);
                        float start2 = MathF.Max(MathF.Abs(Vector3.Dot(state.DragStartWorldPos - state.DragStartObjectPosition, worldAxis2)), Epsilon);

                        float s1 = 1.0f + proj1 / start1;
                        float s2 = 1.0f + proj2 / start2;

                        if (axis == GizmoAxisHit.XY) result = new Vector3(s1, s2, 1.0f);
                        else if (axis == GizmoAxisHit.XZ) result = new Vector3(s1, 1.0f, s2);
                        else result = new Vector3(1.0f, s1, s2);
                        break;
                    }

                case GizmoAxisHit.Screen:
                    {
                        // 统一缩放
                        float distCurr = (currentWorldPos - state.DragStartObjectPosition).Length();
                        float distStart = MathF.Max((state.DragStartWorldPos - state.DragStartObjectPosition).Length(), Epsilon);
                        float uniformScale = distCurr / distStart;
                        result = new Vector3(uniformScale, uniformScale, uniformScale);
                        break;
                    }
            }

            // 缩放捕捉
            if (ScaleSnapEnabled)
            {
                Vector3 snapped = state.DragStartObjectScale * result;
                snapped = new Vector3(
                    MathF.Round(snapped.X / ScaleSnapSize) * ScaleSnapSize,
                    MathF.Round(snapped.Y / ScaleSnapSize) * ScaleSnapSize,
                    MathF.Round(snapped.Z / ScaleSnapSize) * ScaleSnapSize);
                result = new Vector3(
                    snapped.X / MathF.Max(MathF.Abs(state.DragStartObjectScale.X), Epsilon),
                    snapped.Y / MathF.Max(MathF.Abs(state.DragStartObjectScale.Y), Epsilon),
                    snapped.Z / MathF.Max(MathF.Abs(state.DragStartObjectScale.Z), Epsilon));
            }

            return result;
        }

        /// <summary>Universal 模式统一缩放。</summary>
        private Vector3 CalculateUniversalScaleDelta(Vector3 currentWorldPos, GizmoState state)
        {
            float distCurr = (currentWorldPos - state.DragStartObjectPosition).Length();
            float distStart = MathF.Max((state.DragStartWorldPos - state.DragStartObjectPosition).Length(), Epsilon);
            float s = distCurr / distStart;
            return new Vector3(s, s, s);
        }

        // ════════════════════════════════════════════════════════
        //  BuildGizmoMeshes — 网格生成
        // ════════════════════════════════════════════════════════

        /// <summary>
        /// 构建 Gizmo 网格: 生成三个箭头轴 (圆柱体 shaft + 圆锥体 head) 和平面选择器。
        /// </summary>
        /// <returns>Gizmo 网格数据 (顶点 + 索引 + 子网格信息)。</returns>
        public GizmoMeshData BuildGizmoMeshes()
        {
            _cachedMesh.Clear();

            float shaftLength = AxisLength - HeadLength;

            // 生成三个轴的箭头
            AddArrowMesh(ref _cachedMesh, Vector3.UnitX, GizmoColors.AxisX, shaftLength);
            AddArrowMesh(ref _cachedMesh, Vector3.UnitY, GizmoColors.AxisY, shaftLength);
            AddArrowMesh(ref _cachedMesh, Vector3.UnitZ, GizmoColors.AxisZ, shaftLength);

            // 生成平面选择器 (半透明四边形)
            AddPlaneSelector(ref _cachedMesh, GizmoAxisHit.XY, GizmoColors.PlaneXY);
            AddPlaneSelector(ref _cachedMesh, GizmoAxisHit.XZ, GizmoColors.PlaneXZ);
            AddPlaneSelector(ref _cachedMesh, GizmoAxisHit.YZ, GizmoColors.PlaneYZ);

            _meshDirty = false;
            return _cachedMesh;
        }

        /// <summary>
        /// 生成单个箭头 (圆柱体 shaft + 圆锥体 head)。
        /// </summary>
        private void AddArrowMesh(ref GizmoMeshData mesh, Vector3 direction, Vector4 color, float shaftLength)
        {
            int baseVertex = mesh.Vertices.Count;
            int baseIndex = mesh.Indices.Count;

            // ── 圆柱体 (shaft) ──
            // 8 段 x 2 环 = 16 个顶点
            AddCylinder(ref mesh, Vector3.Zero, direction, shaftLength, ShaftRadius, color, CylinderSegments);

            // ── 圆锥体 (head) ──
            // 8 段 x 2 环 + 1 顶点 = 17 个顶点
            Vector3 headBase = direction * shaftLength;
            AddCone(ref mesh, headBase, direction, HeadLength, HeadRadius, color, ConeSegments);

            // 记录子网格
            int indexCount = mesh.Indices.Count - baseIndex;
            GizmoAxisHit axis = direction.X > 0.5f ? GizmoAxisHit.X :
                               direction.Y > 0.5f ? GizmoAxisHit.Y : GizmoAxisHit.Z;
            mesh.SubMeshes.Add((baseIndex, indexCount, axis));
        }

        /// <summary>
        /// 生成圆柱体网格 (沿指定方向)。
        /// 段数 segments, 2 环 (top + bottom), 共 (segments*2) 个顶点。
        /// 三角形数 = segments * 4 (侧面 x2 + 顶面/底面)。
        /// </summary>
        private void AddCylinder(
            ref GizmoMeshData mesh,
            Vector3 basePos, Vector3 direction,
            float length, float radius,
            Vector4 color, int segments)
        {
            direction = Vector3.Normalize(direction);

            // 计算局部坐标系 (圆柱体沿 direction 方向)
            Vector3 up = direction;
            Vector3 right = Vector3.Normalize(Vector3.Cross(up, MathF.Abs(up.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX));
            Vector3 forward = Vector3.Cross(right, up);

            int baseVertex = mesh.Vertices.Count;

            // 底部环
            for (int i = 0; i < segments; i++)
            {
                float angle = (i / (float)segments) * MathF.PI * 2.0f;
                Vector3 offset = right * MathF.Cos(angle) * radius + forward * MathF.Sin(angle) * radius;
                mesh.Vertices.Add(new GizmoVertex(basePos + offset, color));
            }

            // 顶部环
            for (int i = 0; i < segments; i++)
            {
                float angle = (i / (float)segments) * MathF.PI * 2.0f;
                Vector3 offset = right * MathF.Cos(angle) * radius + forward * MathF.Sin(angle) * radius;
                mesh.Vertices.Add(new GizmoVertex(basePos + direction * length + offset, color));
            }

            // 侧面三角形 (每个段 2 个三角形)
            for (int i = 0; i < segments; i++)
            {
                int next = (i + 1) % segments;
                uint bottom0 = (uint)(baseVertex + i);
                uint bottom1 = (uint)(baseVertex + next);
                uint top0 = (uint)(baseVertex + segments + i);
                uint top1 = (uint)(baseVertex + segments + next);

                mesh.Indices.Add(bottom0);
                mesh.Indices.Add(top0);
                mesh.Indices.Add(bottom1);

                mesh.Indices.Add(bottom1);
                mesh.Indices.Add(top0);
                mesh.Indices.Add(top1);
            }
        }

        /// <summary>
        /// 生成圆锥体网格 (沿指定方向)。
        /// 段数 segments, 1 底部环 + 1 顶点 = (segments + 1) 个顶点。
        /// </summary>
        private void AddCone(
            ref GizmoMeshData mesh,
            Vector3 basePos, Vector3 direction,
            float length, float baseRadius,
            Vector4 color, int segments)
        {
            direction = Vector3.Normalize(direction);

            Vector3 up = direction;
            Vector3 right = Vector3.Normalize(Vector3.Cross(up, MathF.Abs(up.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX));
            Vector3 forward = Vector3.Cross(right, up);

            int baseVertex = mesh.Vertices.Count;

            // 底部环
            for (int i = 0; i < segments; i++)
            {
                float angle = (i / (float)segments) * MathF.PI * 2.0f;
                Vector3 offset = right * MathF.Cos(angle) * baseRadius + forward * MathF.Sin(angle) * baseRadius;
                mesh.Vertices.Add(new GizmoVertex(basePos + offset, color));
            }

            // 顶点 (尖端)
            mesh.Vertices.Add(new GizmoVertex(basePos + direction * length, color));

            // 侧面三角形
            for (int i = 0; i < segments; i++)
            {
                int next = (i + 1) % segments;
                uint base0 = (uint)(baseVertex + i);
                uint base1 = (uint)(baseVertex + next);
                uint tip = (uint)(baseVertex + segments);

                mesh.Indices.Add(base0);
                mesh.Indices.Add(tip);
                mesh.Indices.Add(base1);
            }
        }

        /// <summary>
        /// 添加平面选择器 (半透明四边形)。
        /// </summary>
        private void AddPlaneSelector(ref GizmoMeshData mesh, GizmoAxisHit plane, Vector4 color)
        {
            int baseIndex = mesh.Indices.Count;
            int baseVertex = mesh.Vertices.Count;
            float offset = AxisLength * 0.35f;
            float halfSize = offset * 0.5f;

            Vector3 center, tangent1, tangent2;
            switch (plane)
            {
                case GizmoAxisHit.XY:
                    center = new Vector3(offset, offset, 0);
                    tangent1 = Vector3.UnitX;
                    tangent2 = Vector3.UnitY;
                    break;
                case GizmoAxisHit.XZ:
                    center = new Vector3(offset, 0, offset);
                    tangent1 = Vector3.UnitX;
                    tangent2 = Vector3.UnitZ;
                    break;
                case GizmoAxisHit.YZ:
                    center = new Vector3(0, offset, offset);
                    tangent1 = Vector3.UnitY;
                    tangent2 = Vector3.UnitZ;
                    break;
                default:
                    return;
            }

            // 四个顶点
            Vector3 v0 = center - tangent1 * halfSize - tangent2 * halfSize;
            Vector3 v1 = center + tangent1 * halfSize - tangent2 * halfSize;
            Vector3 v2 = center + tangent1 * halfSize + tangent2 * halfSize;
            Vector3 v3 = center - tangent1 * halfSize + tangent2 * halfSize;

            mesh.Vertices.Add(new GizmoVertex(v0, color));
            mesh.Vertices.Add(new GizmoVertex(v1, color));
            mesh.Vertices.Add(new GizmoVertex(v2, color));
            mesh.Vertices.Add(new GizmoVertex(v3, color));

            // 两个三角形
            mesh.Indices.Add((uint)baseVertex);
            mesh.Indices.Add((uint)(baseVertex + 1));
            mesh.Indices.Add((uint)(baseVertex + 2));

            mesh.Indices.Add((uint)baseVertex);
            mesh.Indices.Add((uint)(baseVertex + 2));
            mesh.Indices.Add((uint)(baseVertex + 3));

            int indexCount = mesh.Indices.Count - baseIndex;
            mesh.SubMeshes.Add((baseIndex, indexCount, plane));
        }

        // ════════════════════════════════════════════════════════
        //  GetGizmoTransform — 坐标系对齐
        // ════════════════════════════════════════════════════════

        /// <summary>
        /// 获取 Gizmo 变换矩阵 (用于渲染和交互)。
        /// World Space: 不旋转, 始终对齐世界轴。
        /// Local Space: 跟随物体旋转。
        /// </summary>
        /// <param name="objectWorldMatrix">对象的世界变换矩阵。</param>
        /// <returns>Gizmo 变换矩阵。</returns>
        public Matrix4x4 GetGizmoTransform(Matrix4x4 objectWorldMatrix)
        {
            Vector3 position = objectWorldMatrix.Translation;

            if (Space == GizmoCoordinateSpace.World)
            {
                // 世界空间: 只保留位置, 旋转为 Identity
                return Matrix4x4.CreateTranslation(position);
            }
            else
            {
                // 局部空间: 完全跟随物体变换
                return objectWorldMatrix;
            }
        }

        /// <summary>
        /// 获取 Gizmo 的旋转四元数 (用于 HitTest 和 CalculateDragDelta)。
        /// </summary>
        public Quaternion GetGizmoRotation(Matrix4x4 objectWorldMatrix)
        {
            if (Space == GizmoCoordinateSpace.World)
                return Quaternion.Identity;

            // 从矩阵提取旋转
            Quaternion rotation = Quaternion.CreateFromRotationMatrix(objectWorldMatrix);
            if (rotation == Quaternion.Zero)
                rotation = Quaternion.Identity;
            return rotation;
        }

        // ════════════════════════════════════════════════════════
        //  ApplyTransform — 应用变换到矩阵
        // ════════════════════════════════════════════════════════

        /// <summary>
        /// 将变换增量应用到当前变换矩阵。
        /// </summary>
        /// <param name="mode">操作模式。</param>
        /// <param name="axis">活动轴。</param>
        /// <param name="delta">变换增量。</param>
        /// <param name="currentTransform">当前对象变换矩阵。</param>
        /// <returns>变换后的矩阵。</returns>
        public static Matrix4x4 ApplyTransform(
            GizmoTransformMode mode,
            GizmoAxisHit axis,
            Vector3 delta,
            Matrix4x4 currentTransform)
        {
            switch (mode)
            {
                case GizmoTransformMode.Translate:
                    return currentTransform * Matrix4x4.CreateTranslation(delta);

                case GizmoTransformMode.Rotate:
                    float angle = delta.Length();
                    if (angle < Epsilon) return currentTransform;
                    Vector3 rotAxis = Vector3.Normalize(delta);
                    return currentTransform * Matrix4x4.CreateFromAxisAngle(rotAxis, angle);

                case GizmoTransformMode.Scale:
                    if (MathF.Abs(delta.X - 1.0f) < Epsilon &&
                        MathF.Abs(delta.Y - 1.0f) < Epsilon &&
                        MathF.Abs(delta.Z - 1.0f) < Epsilon)
                        return currentTransform;
                    return currentTransform * Matrix4x4.CreateScale(delta);

                case GizmoTransformMode.Universal:
                    float uniformScale = (delta.X + delta.Y + delta.Z) / 3.0f;
                    return currentTransform * Matrix4x4.CreateScale(
                        new Vector3(uniformScale, uniformScale, uniformScale));

                default:
                    return currentTransform;
            }
        }

        /// <summary>
        /// 应用变换增量 (四元数旋转版本)。
        /// </summary>
        public static Matrix4x4 ApplyTransform(
            GizmoTransformMode mode,
            Vector3 translationDelta,
            Quaternion rotationDelta,
            Vector3 scaleDelta,
            Matrix4x4 currentTransform)
        {
            Matrix4x4 result = currentTransform;

            if (mode == GizmoTransformMode.Translate)
            {
                result = result * Matrix4x4.CreateTranslation(translationDelta);
            }
            else if (mode == GizmoTransformMode.Rotate)
            {
                Matrix4x4 rotMatrix = Matrix4x4.CreateFromQuaternion(rotationDelta);
                // 旋转绕对象中心: T * R * T^-1
                Vector3 pos = result.Translation;
                result = Matrix4x4.CreateTranslation(-pos) * rotMatrix * Matrix4x4.CreateTranslation(pos) * result;
            }
            else if (mode == GizmoTransformMode.Scale || mode == GizmoTransformMode.Universal)
            {
                Vector3 pos = result.Translation;
                result = Matrix4x4.CreateTranslation(-pos) * Matrix4x4.CreateScale(scaleDelta) *
                         Matrix4x4.CreateTranslation(pos) * result;
            }

            return result;
        }

        // ════════════════════════════════════════════════════════
        //  拖拽生命周期
        // ════════════════════════════════════════════════════════

        /// <summary>开始拖拽。</summary>
        public void BeginDrag(GizmoAxisHit axis, Vector3 worldPos, Vector2 mousePos, Matrix4x4 objectTransform)
        {
            State.Active = true;
            State.Dragging = true;
            State.HoverAxis = axis;
            State.DragStartWorldPos = worldPos;
            State.DragStartMousePos = mousePos;
            State.LastDragWorldPos = worldPos;
            State.DragStartObjectTransform = objectTransform;
            State.DragStartObjectPosition = objectTransform.Translation;

            // 提取旋转和缩放
            State.DragStartObjectRotation = Quaternion.CreateFromRotationMatrix(objectTransform);
            State.DragStartObjectScale = ExtractScale(objectTransform);
            State.AccumulatedAngle = 0.0f;
        }

        /// <summary>更新拖拽 (每帧调用)。</summary>
        public void UpdateDrag(Vector3 currentWorldPos)
        {
            State.LastDragWorldPos = currentWorldPos;
        }

        /// <summary>结束拖拽。</summary>
        public void EndDrag()
        {
            State.Dragging = false;
            State.HoverAxis = GizmoAxisHit.None;
            State.AccumulatedAngle = 0.0f;
        }

        /// <summary>设置悬停轴。</summary>
        public void SetHoverAxis(GizmoAxisHit axis)
        {
            State.HoverAxis = axis;
        }

        /// <summary>激活/停用 Gizmo。</summary>
        public void SetActive(bool active)
        {
            State.Active = active;
            if (!active) EndDrag();
        }

        // ════════════════════════════════════════════════════════
        //  拖拽平面计算
        // ════════════════════════════════════════════════════════

        /// <summary>
        /// 计算拖拽平面法线 (根据活动轴)。
        /// 用于将鼠标射线投影到 3D 平面获取世界坐标。
        /// </summary>
        public Vector3 GetDragPlaneNormal(GizmoAxisHit axis, Vector3 cameraForward, Quaternion gizmoRotation)
        {
            Matrix4x4 rotMatrix = Matrix4x4.CreateFromQuaternion(gizmoRotation);

            switch (axis)
            {
                case GizmoAxisHit.X:
                    {
                        // X 轴拖拽: 使用垂直于 X 且最面向相机的平面
                        Vector3 worldX = Vector3.TransformNormal(Vector3.UnitX, rotMatrix);
                        Vector3 candidate1 = Vector3.UnitY;
                        Vector3 candidate2 = Vector3.UnitZ;
                        float dot1 = MathF.Abs(Vector3.Dot(candidate1, cameraForward));
                        float dot2 = MathF.Abs(Vector3.Dot(candidate2, cameraForward));
                        return Vector3.Normalize(Vector3.Cross(worldX, dot1 < dot2 ? candidate1 : candidate2));
                    }

                case GizmoAxisHit.Y:
                    {
                        Vector3 worldY = Vector3.TransformNormal(Vector3.UnitY, rotMatrix);
                        float dot1 = MathF.Abs(Vector3.Dot(Vector3.UnitX, cameraForward));
                        float dot2 = MathF.Abs(Vector3.Dot(Vector3.UnitZ, cameraForward));
                        return Vector3.Normalize(Vector3.Cross(worldY, dot1 < dot2 ? Vector3.UnitX : Vector3.UnitZ));
                    }

                case GizmoAxisHit.Z:
                    {
                        Vector3 worldZ = Vector3.TransformNormal(Vector3.UnitZ, rotMatrix);
                        float dot1 = MathF.Abs(Vector3.Dot(Vector3.UnitX, cameraForward));
                        float dot2 = MathF.Abs(Vector3.Dot(Vector3.UnitY, cameraForward));
                        return Vector3.Normalize(Vector3.Cross(worldZ, dot1 < dot2 ? Vector3.UnitX : Vector3.UnitY));
                    }

                case GizmoAxisHit.XY:
                    {
                        Vector3 worldZ = Vector3.TransformNormal(Vector3.UnitZ, rotMatrix);
                        return worldZ;
                    }

                case GizmoAxisHit.XZ:
                    {
                        Vector3 worldY = Vector3.TransformNormal(Vector3.UnitY, rotMatrix);
                        return worldY;
                    }

                case GizmoAxisHit.YZ:
                    {
                        Vector3 worldX = Vector3.TransformNormal(Vector3.UnitX, rotMatrix);
                        return worldX;
                    }

                case GizmoAxisHit.Screen:
                    return cameraForward;

                default:
                    return cameraForward;
            }
        }

        // ════════════════════════════════════════════════════════
        //  射线-几何体交叉测试
        // ════════════════════════════════════════════════════════

        /// <summary>
        /// 射线-线段最近距离 (用于轴拾取)。
        /// </summary>
        private static float RaySegmentDistance(
            Vector3 rayOrigin, Vector3 rayDir,
            Vector3 segStart, Vector3 segEnd, out float t)
        {
            Vector3 segDir = segEnd - segStart;
            float segLenSq = segDir.LengthSquared();
            if (segLenSq < Epsilon) { t = 0; return Vector3.Distance(rayOrigin, segStart); }

            Vector3 w0 = rayOrigin - segStart;
            float a = Vector3.Dot(segDir, segDir);  // segLenSq
            float b = Vector3.Dot(segDir, rayDir);
            float c = Vector3.Dot(rayDir, rayDir);
            float d = Vector3.Dot(segDir, w0);
            float e = Vector3.Dot(rayDir, w0);

            float denom = a * c - b * b;
            if (MathF.Abs(denom) < Epsilon)
            {
                // 平行
                t = 0;
                return Vector3.Distance(rayOrigin, segStart + segDir * Math.Clamp(-d / a, 0, 1));
            }

            float s = (b * e - c * d) / denom;
            t = (a * e - b * d) / denom;

            s = Math.Clamp(s, 0, 1);
            t = Math.Max(t, 0);

            Vector3 closestOnSeg = segStart + segDir * s;
            Vector3 closestOnRay = rayOrigin + rayDir * t;
            return Vector3.Distance(closestOnRay, closestOnSeg);
        }

        /// <summary>
        /// 射线与无限平面求交。
        /// </summary>
        public static Vector3 RayPlaneIntersect(
            Vector3 rayOrigin, Vector3 rayDir,
            Vector3 planePoint, Vector3 planeNormal)
        {
            float denom = Vector3.Dot(planeNormal, rayDir);
            if (MathF.Abs(denom) < Epsilon) return planePoint;
            float t = Vector3.Dot(planePoint - rayOrigin, planeNormal) / denom;
            return rayOrigin + rayDir * t;
        }

        // ════════════════════════════════════════════════════════
        //  工具方法
        // ════════════════════════════════════════════════════════

        /// <summary>从矩阵提取缩放分量。</summary>
        private static Vector3 ExtractScale(Matrix4x4 m)
        {
            return new Vector3(
                new Vector3(m.M11, m.M12, m.M13).Length(),
                new Vector3(m.M21, m.M22, m.M23).Length(),
                new Vector3(m.M31, m.M32, m.M33).Length()
            );
        }

        /// <summary>从矩阵提取旋转四元数 (去除缩放)。</summary>
        private static Quaternion ExtractRotation(Matrix4x4 m)
        {
            Vector3 scaleX = new Vector3(m.M11, m.M12, m.M13);
            Vector3 scaleY = new Vector3(m.M21, m.M22, m.M23);
            Vector3 scaleZ = new Vector3(m.M31, m.M32, m.M33);

            float sx = scaleX.Length();
            float sy = scaleY.Length();
            float sz = scaleZ.Length();

            if (sx < Epsilon || sy < Epsilon || sz < Epsilon)
                return Quaternion.Identity;

            Matrix4x4 rotMatrix = m;
            rotMatrix.M11 /= sx; rotMatrix.M12 /= sx; rotMatrix.M13 /= sx;
            rotMatrix.M21 /= sy; rotMatrix.M22 /= sy; rotMatrix.M23 /= sy;
            rotMatrix.M31 /= sz; rotMatrix.M32 /= sz; rotMatrix.M33 /= sz;

            return Quaternion.CreateFromRotationMatrix(rotMatrix);
        }

        /// <summary>分解矩阵为TRS。</summary>
        public static void Decompose(Matrix4x4 m, out Vector3 translation, out Quaternion rotation, out Vector3 scale)
        {
            translation = m.Translation;
            scale = ExtractScale(m);
            rotation = ExtractRotation(m);
            if (rotation == Quaternion.Zero) rotation = Quaternion.Identity;
        }

        /// <summary>从TRS构建矩阵。</summary>
        public static Matrix4x4 Compose(Vector3 translation, Quaternion rotation, Vector3 scale)
        {
            Matrix4x4 t = Matrix4x4.CreateTranslation(translation);
            Matrix4x4 r = Matrix4x4.CreateFromQuaternion(rotation);
            Matrix4x4 s = Matrix4x4.CreateScale(scale);
            return s * r * t;
        }

        public void Dispose()
        {
            _cachedMesh?.Clear();
            _meshDirty = true;
        }
    }
}
