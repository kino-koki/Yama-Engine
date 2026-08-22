// ============================================================================================
//  EngineCompatTypes.cs
//
//  山雨引擎 (ShanyuEngine) 兼容类型补全层。
//
//  目的：修复构建期 CS0246（被引用但原先未定义的结构/枚举类型）。
//  本文件统一补齐以下真实可用类型：
//    - DemoMeshType              ：物理演示网格类型枚举
//    - PhysicsRenderTarget       ：物理调试渲染目标（名称/缩放/基础色/网格类型）
//    - BodyDescriptor            ：刚体创建描述符（含 fluent With* 方法）
//    - ColoredSegment            ：代码编辑器语法着色段（TextSegment + Color）
//    - CameraManager             ：相机管理器（多相机注册/切换的最小实现）
//    - BuiltinMaterials          ：内置材质静态库（占位注册点）
//
//  设计原则：仅包含被调用方实际使用的成员；语义与现有代码对齐；非空壳。
//  命名空间：ShanyuEngine（或对应子命名空间，见各类型声明）。
// ============================================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Numerics;

namespace ShanyuEngine
{
    /// <summary>相机投影模式。</summary>
    public enum CameraMode
    {
        Perspective = 0,
        Orthographic,
    }

    /// <summary>
    ///  相机：提供视图/投影矩阵、视锥射线与基础变换。
    ///  设计为值类型（struct），支持 Nullable&lt;Camera&gt; 的 HasValue/Value 用法。
    /// </summary>
    public struct Camera
    {
        public Vector3 Position;
        public Vector3 Forward;
        public Vector3 Up;
        public float FieldOfView;     // 透视：垂直 FOV（弧度）
        public float NearClip;
        public float FarClip;
        public float Aspect;
        public CameraMode Mode;
        public bool OrthoViewMode;    // 正交视图标志
        public float OrthoSize;       // 正交半高

        public Matrix4x4 ViewMatrix => Matrix4x4.CreateLookAt(Position, Position + Forward, Up);
        public Matrix4x4 ProjectionMatrix => Mode == CameraMode.Orthographic
            ? Matrix4x4.CreateOrthographic(OrthoSize * 2 * Aspect, OrthoSize * 2, NearClip, FarClip)
            : Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView, Aspect, NearClip, FarClip);

        public void SetAspect(float aspect) => Aspect = aspect;
        public void Update() { /* 每帧变换刷新钩子，当前无缓存状态需更新 */ }
        public void FrameBounds(Vector3 center, float radius)
        {
            Position = center - Forward * (radius / MathF.Max(1e-3f, MathF.Sin(FieldOfView * 0.5f)));
            NearClip = MathF.Max(0.01f, radius * 0.01f);
            FarClip = radius * 10f;
        }

        /// <summary>将屏幕坐标（归一化设备坐标 [-1,1]）转换为世界射线。</summary>
        public (Vector3 Origin, Vector3 Direction) ScreenToWorldRay(float ndcX, float ndcY)
        {
            var invView = Matrix4x4.Invert(ViewMatrix, out var iv) ? iv : Matrix4x4.Identity;
            var invProj = Matrix4x4.Invert(ProjectionMatrix, out var ip) ? ip : Matrix4x4.Identity;
            var nearPoint = Vector3.Transform(new Vector3(ndcX, ndcY, 0f), invProj);
            var worldNear = Vector3.Transform(nearPoint, invView);
            var dir = Vector3.Normalize(worldNear - Position);
            return (Position, dir);
        }
    }

    /// <summary>物理演示网格类型。</summary>
    public enum DemoMeshType
    {
        Cube = 0,
        Box,
        Sphere,
        Capsule,
        Cylinder,
        Mesh,
    }

    /// <summary>物理调试渲染目标：将一个刚体索引映射到可绘制的调试网格。</summary>
    public sealed class PhysicsRenderTarget
    {
        public string Name = string.Empty;
        public Vector3 Scale = Vector3.One;
        public Vector4 BaseColor = new Vector4(0.8f, 0.8f, 0.8f, 1f);
        public DemoMeshType MeshType = DemoMeshType.Cube;
        public int BodyIndex = -1;
    }

    /// <summary>刚体创建描述符（在创建物理刚体前填充）。</summary>
    public sealed class BodyDescriptor
    {
        public int EntityId;
        public Vector3 Position = Vector3.Zero;
        public float Mass = 1f;
        public int ObjectLayer;
        public uint CollisionMask = 0xFFFFFFFF;

        // 形状参数（由 With* 方法填充）
        public Vector3 HalfExtents;
        public float SphereRadius;
        public float CapsuleRadius;
        public float CapsuleHalfHeight;
        public Vector3[]? ConvexVertices;
        public uint[]? ConvexIndices;

        public BodyDescriptor WithBox(Vector3 halfExtents) { HalfExtents = halfExtents; return this; }
        public BodyDescriptor WithSphere(float radius) { SphereRadius = radius; return this; }
        public BodyDescriptor WithCapsule(float radius, float halfHeight) { CapsuleRadius = radius; CapsuleHalfHeight = halfHeight; return this; }
        public BodyDescriptor WithMesh(Vector3[] vertices, uint[] indices) { ConvexVertices = vertices; ConvexIndices = indices; return this; }
    }
}

namespace ShanyuEngine.CodeEditing
{
    /// <summary>语法着色段：一段文本区间及其显示颜色。</summary>
    public sealed class ColoredSegment
    {
        public TextSegment Segment { get; init; }
        public Color Color { get; init; }

        public ColoredSegment(TextSegment segment, Color color)
        {
            Segment = segment;
            Color = color;
        }
    }
}

namespace ShanyuEngine.Rendering
{
    /// <summary>相机管理器：维护活动相机与已注册相机列表的最小实现。</summary>
    public sealed class CameraManager
    {
        private readonly List<Camera> _cameras = new();
        public Camera? ActiveCamera { get; private set; }

        public void Register(Camera camera)
        {
            if (!_cameras.Contains(camera))
            {
                _cameras.Add(camera);
                ActiveCamera ??= camera;
            }
        }

        public void SetActive(Camera camera) => ActiveCamera = camera;
        public IReadOnlyList<Camera> Cameras => _cameras;
    }

    /// <summary>内置材质静态库（提供默认材质注册点；具体材质实例由 MaterialSystem 填充）。</summary>
    public static class BuiltinMaterials
    {
        public const string DefaultLit = "DefaultLit";
        public const string DefaultUnlit = "DefaultUnlit";
        public const string Skybox = "Skybox";

        private static readonly HashSet<string> _registered = new();
        public static void Register(string name) => _registered.Add(name);
        public static bool IsRegistered(string name) => _registered.Contains(name);
    }
}
