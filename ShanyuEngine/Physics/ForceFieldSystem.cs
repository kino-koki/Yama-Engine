using System;
using System.Collections.Generic;
using System.Numerics;
using ShanyuEngine.ECS;

namespace ShanyuEngine.Physics
{
    // ============================================================================================
    //  ForceFieldSystem.cs
    //
    //  物理力场系统。为物理世界提供基于空间体积的力场施加框架，支持径向力、方向力、
    //  漩涡力、湍流力、阻力、浮力等多种力场类型，并配备 BVH 加速的范围查询和编辑器
    //  可视化（Gizmo）。
    //
    //  与已有的 PhysicsField.cs（IPhysicsField 接口体系）互补：
    //    - PhysicsField.cs 侧重于物理场本身的数据采样（Sample 方法）
    //    - ForceFieldSystem.cs 侧重于 ECS 集成、批量施加力、范围查询与可视化
    //
    //  本系统包含以下核心模块：
    //    - ForceFieldType       ：力场类型枚举（径向/方向/漩涡/湍流/阻力/浮力）
    //    - ForceFieldFalloffType：距离衰减类型枚举（无/线性/指数）
    //    - ForceFieldShapeType  ：力场形状类型枚举（球/盒/胶囊/圆柱）
    //    - ForceFieldShape      ：力场形状（含 Contains/DistanceToFalloff 计算）
    //    - ForceFieldComponent  ：ECS 组件（类型/强度/衰减/范围/方向等参数）
    //    - FieldBVH             ：BVH 层次包围盒，加速力场范围查询
    //    - FieldQuery           ：范围查询器，使用 BVH 快速查找力场内的刚体
    //    - WindField            ：全局风场（方向风 + Curl Noise 湍流）
    //    - ForceFieldSystem     ：ECS 系统，每帧遍历力场组件并对范围内刚体施力
    //    - ForceFieldRenderer   ：编辑器可视化（Gizmo 绘制力场范围和力方向）
    //
    //  与引擎其他系统的对接：
    //    - PhysicsWorld：通过 GetPosition/AddForce/AddImpulse 等方法施加力到刚体
    //    - ECS：通过 ForceFieldComponent 查询力场实体，通过 TransformComponent 获取位置
    //    - Falloff/CurlNoise（PhysicsField.cs）：复用已有的衰减函数和噪声生成器
    // ============================================================================================

    // ──────────────────────────────────────────────────────────────
    //  枚举定义
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    ///   力场类型。决定力场如何计算施加到刚体上的力向量。
    /// </summary>
    public enum ForceFieldType : byte
    {
        /// <summary>径向力：从力场中心向外推（或向内吸），如爆炸冲击波、引力井。</summary>
        Radial = 0,
        /// <summary>方向力：沿固定方向施加恒定力，如风、重力方向。</summary>
        Directional = 1,
        /// <summary>漩涡力：绕力场轴线施加切向力，如龙卷风、涡旋。</summary>
        Vortex = 2,
        /// <summary>湍流力：基于 Curl Noise 的随机扰动力，如空气湍流。</summary>
        Turbulence = 3,
        /// <summary>阻力：与刚体速度方向相反的减速力，模拟空气/流体阻力。</summary>
        Drag = 4,
        /// <summary>浮力：向上的恒定力，模拟浮力效应（如水中物体上浮）。</summary>
        Buoyancy = 5,
    }

    /// <summary>
    ///   力场距离衰减类型。控制力场强度随到中心距离的衰减方式。
    /// </summary>
    public enum ForceFieldFalloffType : byte
    {
        /// <summary>无衰减：力场范围内强度恒定。</summary>
        None = 0,
        /// <summary>线性衰减：从中心到边缘线性递减到零。</summary>
        Linear = 1,
        /// <summary>指数衰减：e^(-d/λ)，平滑过渡。</summary>
        Exponential = 2,
    }

    /// <summary>
    ///   力场形状类型。限定力场在空间中的作用体积。
    /// </summary>
    public enum ForceFieldShapeType : byte
    {
        /// <summary>球形：以中心为圆心、Radius 为半径的球体。</summary>
        Sphere = 0,
        /// <summary>盒体：轴对齐包围盒，由 Center 和 HalfExtents 定义。</summary>
        Box = 1,
        /// <summary>胶囊体：由线段（P1-P2）和半径定义的胶囊。</summary>
        Capsule = 2,
        /// <summary>圆柱体：以中心为原点、Axis 为轴方向、Height 和 Radius 定义。</summary>
        Cylinder = 3,
    }

    // ──────────────────────────────────────────────────────────────
    //  力场形状
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    ///   力场形状。定义力场在空间中的作用范围，并提供点包含检测和归一化距离计算。
    ///   归一化距离 [0,1] 用于衰减计算：0=中心，1=边缘。
    /// </summary>
    public struct ForceFieldShape
    {
        /// <summary>形状类型。</summary>
        public ForceFieldShapeType Type;

        /// <summary>形状中心位置（世界空间）。</summary>
        public Vector3 Center;

        /// <summary>球/胶囊/圆柱的半径。</summary>
        public float Radius;

        /// <summary>盒体的半尺寸（Box 类型有效）。</summary>
        public Vector3 BoxHalfExtents;

        /// <summary>圆柱体的高度（Cylinder 类型有效）。</summary>
        public float Height;

        /// <summary>圆柱/胶囊的轴线方向（需归一化，Cylinder/Capsule 类型有效）。</summary>
        public Vector3 Axis;

        /// <summary>胶囊体的端点 A（Capsule 类型有效，与 Center 无关）。</summary>
        public Vector3 CapsuleP1;

        /// <summary>胶囊体的端点 B（Capsule 类型有效，与 Center 无关）。</summary>
        public Vector3 CapsuleP2;

        /// <summary>创建球形力场。</summary>
        public static ForceFieldShape Sphere(Vector3 center, float radius)
        {
            return new ForceFieldShape
            {
                Type = ForceFieldShapeType.Sphere,
                Center = center,
                Radius = radius,
            };
        }

        /// <summary>创建盒体力场。</summary>
        public static ForceFieldShape Box(Vector3 center, Vector3 halfExtents)
        {
            return new ForceFieldShape
            {
                Type = ForceFieldShapeType.Box,
                Center = center,
                BoxHalfExtents = halfExtents,
            };
        }

        /// <summary>创建胶囊体力场。</summary>
        public static ForceFieldShape Capsule(Vector3 p1, Vector3 p2, float radius)
        {
            return new ForceFieldShape
            {
                Type = ForceFieldShapeType.Capsule,
                CapsuleP1 = p1,
                CapsuleP2 = p2,
                Center = (p1 + p2) * 0.5f,
                Radius = radius,
            };
        }

        /// <summary>创建圆柱体力场。</summary>
        public static ForceFieldShape Cylinder(Vector3 center, Vector3 axis, float radius, float height)
        {
            Vector3 normAxis = axis.LengthSquared() > 1e-8f ? Vector3.Normalize(axis) : Vector3.UnitY;
            return new ForceFieldShape
            {
                Type = ForceFieldShapeType.Cylinder,
                Center = center,
                Axis = normAxis,
                Radius = radius,
                Height = height,
            };
        }

        /// <summary>计算某点是否在形状内。</summary>
        public readonly bool Contains(Vector3 position)
        {
            return Type switch
            {
                ForceFieldShapeType.Sphere => Vector3.DistanceSquared(position, Center) <= Radius * Radius,
                ForceFieldShapeType.Box => IsInBox(position),
                ForceFieldShapeType.Capsule => IsInCapsule(position),
                ForceFieldShapeType.Cylinder => IsInCylinder(position),
                _ => false,
            };
        }

        /// <summary>计算归一化距离 [0,1]（0=中心，1=边缘，>1=外部）。</summary>
        public readonly float NormalizedDistance(Vector3 position)
        {
            return Type switch
            {
                ForceFieldShapeType.Sphere => Radius > 0f ? Vector3.Distance(position, Center) / Radius : 1f,
                ForceFieldShapeType.Box => NormalizedDistanceBox(position),
                ForceFieldShapeType.Capsule => NormalizedDistanceCapsule(position),
                ForceFieldShapeType.Cylinder => NormalizedDistanceCylinder(position),
                _ => 1f,
            };
        }

        /// <summary>计算衰减系数 [0,1]（1=全力，0=无力）。</summary>
        public readonly float EvaluateFalloff(Vector3 position, ForceFieldFalloffType falloffType, float lambda = 0.3f)
        {
            if (!Contains(position)) return 0f;
            float nd = NormalizedDistance(position);
            return falloffType switch
            {
                ForceFieldFalloffType.None => 1f,
                ForceFieldFalloffType.Linear => 1f - Math.Clamp(nd, 0f, 1f),
                ForceFieldFalloffType.Exponential => MathF.Exp(-nd / MathF.Max(0.0001f, lambda)),
                _ => 1f,
            };
        }

        /// <summary>计算形状的轴对齐包围盒（AABB）。</summary>
        public readonly (Vector3 min, Vector3 max) GetAABB()
        {
            return Type switch
            {
                ForceFieldShapeType.Sphere => (Center - new Vector3(Radius), Center + new Vector3(Radius)),
                ForceFieldShapeType.Box => (Center - BoxHalfExtents, Center + BoxHalfExtents),
                ForceFieldShapeType.Capsule => GetCapsuleAABB(),
                ForceFieldShapeType.Cylinder => GetCylinderAABB(),
                _ => (Center - Vector3.One, Center + Vector3.One),
            };
        }

        private readonly bool IsInBox(Vector3 pos)
        {
            Vector3 d = pos - Center;
            return MathF.Abs(d.X) <= BoxHalfExtents.X &&
                   MathF.Abs(d.Y) <= BoxHalfExtents.Y &&
                   MathF.Abs(d.Z) <= BoxHalfExtents.Z;
        }

        private readonly bool IsInCapsule(Vector3 pos)
        {
            float distToSegment = PointToSegmentDistance(pos, CapsuleP1, CapsuleP2);
            return distToSegment <= Radius;
        }

        private readonly bool IsInCylinder(Vector3 pos)
        {
            Vector3 d = pos - Center;
            // 沿轴投影
            float axialDist = Vector3.Dot(d, Axis);
            if (MathF.Abs(axialDist) > Height * 0.5f) return false;
            // 径向距离
            Vector3 radial = d - Axis * axialDist;
            return radial.LengthSquared() <= Radius * Radius;
        }

        private readonly float NormalizedDistanceBox(Vector3 pos)
        {
            Vector3 d = pos - Center;
            float nx = BoxHalfExtents.X > 0f ? MathF.Abs(d.X) / BoxHalfExtents.X : 0f;
            float ny = BoxHalfExtents.Y > 0f ? MathF.Abs(d.Y) / BoxHalfExtents.Y : 0f;
            float nz = BoxHalfExtents.Z > 0f ? MathF.Abs(d.Z) / BoxHalfExtents.Z : 0f;
            return Math.Max(nx, Math.Max(ny, nz));
        }

        private readonly float NormalizedDistanceCapsule(Vector3 pos)
        {
            float distToSeg = PointToSegmentDistance(pos, CapsuleP1, CapsuleP2);
            return Radius > 0f ? distToSeg / Radius : 1f;
        }

        private readonly float NormalizedDistanceCylinder(Vector3 pos)
        {
            Vector3 d = pos - Center;
            float axialDist = MathF.Abs(Vector3.Dot(d, Axis));
            float axialNorm = Height > 0f ? axialDist / (Height * 0.5f) : 0f;
            Vector3 radial = d - Axis * Vector3.Dot(d, Axis);
            float radialNorm = Radius > 0f ? radial.Length() / Radius : 0f;
            return Math.Max(axialNorm, radialNorm);
        }

        private readonly (Vector3 min, Vector3 max) GetCapsuleAABB()
        {
            Vector3 r = new(Radius);
            Vector3 min = Vector3.Min(CapsuleP1, CapsuleP2) - r;
            Vector3 max = Vector3.Max(CapsuleP1, CapsuleP2) + r;
            return (min, max);
        }

        private readonly (Vector3 min, Vector3 max) GetCylinderAABB()
        {
            // 保守 AABB：以圆柱半径为各方向半径
            Vector3 half = new Vector3(Radius) + Axis * (Height * 0.5f);
            Vector3 absHalf = new(MathF.Abs(half.X), MathF.Abs(half.Y), MathF.Abs(half.Z));
            return (Center - absHalf, Center + absHalf);
        }

        /// <summary>点到线段的距离。</summary>
        private static float PointToSegmentDistance(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float t = Vector3.Dot(p - a, ab);
            float denom = ab.LengthSquared();
            if (denom < 1e-8f) return Vector3.Distance(p, a);
            t = Math.Clamp(t / denom, 0f, 1f);
            Vector3 closest = a + ab * t;
            return Vector3.Distance(p, closest);
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  力场 ECS 组件
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    ///   力场 ECS 组件。附加到实体上使其成为力场源，对范围内的刚体施加力。
    ///   组件存储力场类型、强度、衰减、形状、方向等全部参数。
    ///   每帧由 ForceFieldSystem 遍历并对范围内刚体施加力。
    /// </summary>
    public struct ForceFieldComponent : IComponent
    {
        /// <summary>力场类型。</summary>
        public ForceFieldType FieldType;

        /// <summary>力场基础强度（力的量纲取决于类型）。</summary>
        public float Strength;

        /// <summary>距离衰减类型。</summary>
        public ForceFieldFalloffType Falloff;

        /// <summary>指数衰减波长（仅 Exponential 衰减使用）。</summary>
        public float FalloffLambda;

        /// <summary>力场形状。</summary>
        public ForceFieldShape Shape;

        /// <summary>方向力/漩涡力/浮力的方向或轴线（需归一化）。</summary>
        public Vector3 Direction;

        /// <summary>径向力是否向外推（true=向外推/爆炸，false=向内吸/引力）。</summary>
        public bool RadialOutward;

        /// <summary>阻力系数（Drag 类型有效，控制减速强度）。</summary>
        public float DragCoefficient;

        /// <summary>湍流力的噪声频率。</summary>
        public float TurbulenceFrequency;

        /// <summary>湍流力的噪声演化速度。</summary>
        public float TurbulenceEvolutionSpeed;

        /// <summary>是否启用。</summary>
        public bool Enabled;

        /// <summary>力场是否跟随实体位置（true=跟随，false=固定世界空间）。</summary>
        public bool FollowEntity;

        /// <summary>是否只施加力到特定标签的刚体（0=所有刚体）。</summary>
        public uint TargetBodyMask;

        /// <summary>湍流噪声内部时间状态。</summary>
        public float NoiseTime;

        /// <summary>初始化默认值。</summary>
        public static ForceFieldComponent CreateDefault()
        {
            return new ForceFieldComponent
            {
                FieldType = ForceFieldType.Radial,
                Strength = 10f,
                Falloff = ForceFieldFalloffType.Linear,
                FalloffLambda = 0.3f,
                Shape = ForceFieldShape.Sphere(Vector3.Zero, 5f),
                Direction = new Vector3(1, 0, 0),
                RadialOutward = true,
                DragCoefficient = 0.5f,
                TurbulenceFrequency = 0.5f,
                TurbulenceEvolutionSpeed = 0.2f,
                Enabled = true,
                FollowEntity = true,
                TargetBodyMask = 0,
                NoiseTime = 0f,
            };
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  BVH 加速结构
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    ///   BVH（Bounding Volume Hierarchy）层次包围盒节点。
    ///   用于加速力场范围查询：将场景中的刚体位置组织为树结构，
    ///   使范围查询只需检查与查询 AABB 相交的子树。
    /// </summary>
    internal sealed class FieldBVHNode
    {
        /// <summary>AABB 最小角。</summary>
        public Vector3 Min;
        /// <summary>AABB 最大角。</summary>
        public Vector3 Max;
        /// <summary>叶子节点时存储的刚体 ID（内部节点为 -1）。</summary>
        public int BodyIndex = -1;
        /// <summary>左子节点。</summary>
        public FieldBVHNode Left;
        /// <summary>右子节点。</summary>
        public FieldBVHNode Right;
        /// <summary>是否为叶子节点。</summary>
        public bool IsLeaf => Left == null && Right == null;
    }

    /// <summary>
    ///   BVH 加速的空间查询器。维护场景中所有刚体位置的 BVH 树，
    ///   提供 AABB 范围查询和球形范围查询，用于快速找到力场范围内的刚体。
    ///
    ///   BVH 在每帧物理步进前重建（增量更新开销较大，简单场景下全量重建即可）。
    /// </summary>
    public sealed class FieldQuery
    {
        private FieldBVHNode _root;
        private readonly List<(uint bodyId, Vector3 position)> _bodies = new();

        /// <summary>当前 BVH 中的刚体数量。</summary>
        public int Count => _bodies.Count;

        /// <summary>清空并重新填充刚体位置数据。</summary>
        public void Rebuild(IEnumerable<(uint bodyId, Vector3 position)> bodies)
        {
            _bodies.Clear();
            foreach (var b in bodies)
                _bodies.Add(b);

            if (_bodies.Count == 0) { _root = null; return; }

            var indices = new int[_bodies.Count];
            for (int i = 0; i < indices.Length; i++) indices[i] = i;
            _root = BuildNode(indices, 0, indices.Length, 0);
        }

        private FieldBVHNode BuildNode(int[] indices, int start, int end, int depth)
        {
            if (start >= end) return null;
            var node = new FieldBVHNode();

            if (end - start <= 4)
            {
                // 叶子节点
                var (min, max) = ComputeAABB(indices, start, end);
                node.Min = min;
                node.Max = max;
                node.BodyIndex = start; // 存储起始索引，遍历时读取连续范围
                // 对于多体叶子，用 BodyIndex 存储 -end 表示范围 [start, end)
                // 简化处理：每个叶子只存一个体
                if (end - start == 1)
                {
                    node.BodyIndex = indices[start];
                    return node;
                }
            }

            // 选择最长轴作为分割轴
            var (aabbMin, aabbMax) = ComputeAABB(indices, start, end);
            node.Min = aabbMin;
            node.Max = aabbMax;
            Vector3 extent = aabbMax - aabbMin;
            int axis = 0;
            if (extent.Y > extent.X && extent.Y > extent.Z) axis = 1;
            else if (extent.Z > extent.X) axis = 2;

            // 按选定轴排序
            int mid = start + (end - start) / 2;
            PartialSortByAxis(indices, start, end, mid, axis);

            node.Left = BuildNode(indices, start, mid, depth + 1);
            node.Right = BuildNode(indices, mid, end, depth + 1);
            node.BodyIndex = -1;
            return node;
        }

        private (Vector3 min, Vector3 max) ComputeAABB(int[] indices, int start, int end)
        {
            Vector3 min = new(float.MaxValue);
            Vector3 max = new(float.MinValue);
            for (int i = start; i < end; i++)
            {
                var pos = _bodies[indices[i]].position;
                min = Vector3.Min(min, pos);
                max = Vector3.Max(max, pos);
            }
            return (min, max);
        }

        private void PartialSortByAxis(int[] indices, int start, int end, int mid, int axis)
        {
            while (start < end)
            {
                int pivot = PartitionAxis(indices, start, end - 1, axis);
                if (pivot == mid) return;
                if (pivot < mid) start = pivot + 1;
                else end = pivot;
            }
        }

        private int PartitionAxis(int[] indices, int lo, int hi, int axis)
        {
            float pivotVal = GetAxis(_bodies[indices[hi]].position, axis);
            int i = lo;
            for (int j = lo; j < hi; j++)
            {
                if (GetAxis(_bodies[indices[j]].position, axis) <= pivotVal)
                {
                    (indices[i], indices[j]) = (indices[j], indices[i]);
                    i++;
                }
            }
            (indices[i], indices[hi]) = (indices[hi], indices[i]);
            return i;
        }

        private static float GetAxis(Vector3 v, int axis) => axis switch
        {
            0 => v.X,
            1 => v.Y,
            _ => v.Z,
        };

        /// <summary>查询 AABB 范围内的所有刚体 ID。</summary>
        public List<uint> QueryAABB(Vector3 min, Vector3 max)
        {
            var result = new List<uint>();
            if (_root == null) return result;
            QueryAABBRecursive(_root, min, max, result);
            return result;
        }

        private void QueryAABBRecursive(FieldBVHNode node, Vector3 min, Vector3 max, List<uint> result)
        {
            if (node == null) return;
            // AABB 重叠检测
            if (node.Max.X < min.X || node.Min.X > max.X ||
                node.Max.Y < min.Y || node.Min.Y > max.Y ||
                node.Max.Z < min.Z || node.Min.Z > max.Z)
                return;

            if (node.IsLeaf && node.BodyIndex >= 0)
            {
                result.Add(_bodies[node.BodyIndex].bodyId);
            }
            else
            {
                QueryAABBRecursive(node.Left, min, max, result);
                QueryAABBRecursive(node.Right, min, max, result);
            }
        }

        /// <summary>查询球形范围内的所有刚体 ID。</summary>
        public List<uint> QuerySphere(Vector3 center, float radius)
        {
            Vector3 r = new(radius);
            var candidates = QueryAABB(center - r, center + r);
            var result = new List<uint>();
            float rSq = radius * radius;
            foreach (var bodyId in candidates)
            {
                // 重新查找位置（简化实现）
                foreach (var b in _bodies)
                {
                    if (b.bodyId == bodyId)
                    {
                        if (Vector3.DistanceSquared(b.position, center) <= rSq)
                            result.Add(bodyId);
                        break;
                    }
                }
            }
            return result;
        }

        /// <summary>查询力场形状范围内的所有刚体 ID。</summary>
        public List<uint> QueryShape(ForceFieldShape shape)
        {
            var (min, max) = shape.GetAABB();
            var candidates = QueryAABB(min, max);
            var result = new List<uint>();
            foreach (var bodyId in candidates)
            {
                foreach (var b in _bodies)
                {
                    if (b.bodyId == bodyId)
                    {
                        if (shape.Contains(b.position))
                            result.Add(bodyId);
                        break;
                    }
                }
            }
            return result;
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  全局风场
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    ///   全局风场。在整个场景中施加方向风 + Curl Noise 湍流。
    ///   风场不受力场形状限制，对所有刚体生效（或通过 TargetBodyMask 过滤）。
    ///
    ///   风场 = 方向风 * Strength + CurlNoise(pos * freq, time) * TurbulenceStrength
    ///   方向风提供恒定的推力方向，湍流噪声添加随机扰动使风力看起来更自然。
    /// </summary>
    public sealed class WindField
    {
        /// <summary>方向风方向（需归一化）。</summary>
        public Vector3 WindDirection = new(1, 0, 0);

        /// <summary>方向风强度。</summary>
        public float Strength = 5f;

        /// <summary>湍流强度（0=纯方向风，1=强湍流）。</summary>
        public float TurbulenceStrength = 0.3f;

        /// <summary>湍流噪声频率。</summary>
        public float TurbulenceFrequency = 0.3f;

        /// <summary>湍流噪声时间演化速度。</summary>
        public float EvolutionSpeed = 0.15f;

        /// <summary>是否启用。</summary>
        public bool Enabled = true;

        /// <summary>风场作用的高度上限（高于此高度无风，0=无限高度）。</summary>
        public float MaxHeight = 0f;

        /// <summary>风场作用的高度下限（低于此高度无风，0=无限高度）。</summary>
        public float MinHeight = 0f;

        private float _time;

        /// <summary>
        ///   在指定位置采样风力。
        /// </summary>
        public Vector3 Sample(Vector3 position, float deltaTime)
        {
            if (!Enabled) return Vector3.Zero;

            // 高度限制
            if (MaxHeight > 0f && position.Y > MaxHeight) return Vector3.Zero;
            if (MinHeight > 0f && position.Y < MinHeight) return Vector3.Zero;

            _time += deltaTime * EvolutionSpeed;

            // 方向风
            Vector3 dir = WindDirection;
            if (dir.LengthSquared() > 1e-8f)
                dir = Vector3.Normalize(dir);
            Vector3 wind = dir * Strength;

            // 湍流扰动
            if (TurbulenceStrength > 0f)
            {
                Vector3 curl = CurlNoise.Sample(
                    position * TurbulenceFrequency,
                    new Vector3(_time, _time * 0.7f, _time * 1.3f));
                wind += curl * TurbulenceStrength * Strength;
            }

            return wind;
        }

        /// <summary>
        ///   将风场力施加到 PhysicsWorld 中的所有刚体。
        /// </summary>
        /// <param name="world">物理世界。</param>
        /// <param name="bodyIds">需要施加风的刚体 ID 列表。</param>
        /// <param name="deltaTime">帧间隔。</param>
        public void ApplyTo(PhysicsWorld world, IEnumerable<uint> bodyIds, float deltaTime)
        {
            if (!Enabled || world == null) return;
            foreach (var bodyId in bodyIds)
            {
                Vector3 pos = world.GetPosition(bodyId);
                Vector3 force = Sample(pos, deltaTime);
                if (force != Vector3.Zero)
                    world.AddForce(bodyId, force);
            }
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  力场 ECS 系统
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    ///   力场 ECS 系统。每帧遍历所有 ForceFieldComponent，对力场形状范围内的
    ///   RigidBody 施加力。使用 FieldQuery（BVH）加速范围查询。
    ///
    ///   系统执行流程（每帧）：
    ///     1. 更新力场形状中心（如果 FollowEntity）
    ///     2. 用 FieldQuery 查询力场形状范围内的刚体
    ///     3. 对每个刚体计算力向量（根据力场类型）
    ///     4. 应用衰减系数
    ///     5. 调用 PhysicsWorld.AddForce 施加力
    ///     6. 更新 WindField 并施加全局风
    /// </summary>
    public sealed class ForceFieldSystem : ISystem
    {
        private readonly PhysicsWorld _world;
        private readonly FieldQuery _query = new();

        /// <summary>全局风场。</summary>
        public WindField Wind { get; } = new();

        /// <summary>系统名称。</summary>
        public string Name => "ForceFieldSystem";

        /// <summary>是否启用全局风场。</summary>
        public bool EnableWind { get; set; } = true;

        /// <summary>每帧重建 BVH 时收集的刚体 ID 列表（用于风场施加）。</summary>
        private readonly List<uint> _allBodies = new();

        public ForceFieldSystem(PhysicsWorld world)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
        }

        /// <summary>每帧更新所有力场组件。</summary>
        public void Update(float dt, EntityComponentSystem ecs)
        {
            if (ecs == null || _world == null) return;

            // 1. 收集物理世界中所有刚体并重建 BVH
            _allBodies.Clear();
            var bodyPositions = new List<(uint bodyId, Vector3 position)>();
            foreach (var bodyId in _world.GetAllBodyIds())
            {
                Vector3 pos = _world.GetPosition(bodyId);
                bodyPositions.Add((bodyId, pos));
                _allBodies.Add(bodyId);
            }
            _query.Rebuild(bodyPositions);

            // 2. 遍历力场组件
            var fieldEntities = ecs.QueryWithComponent<ForceFieldComponent>();
            foreach (var entityId in fieldEntities)
            {
                var fc = ecs.GetComponent<ForceFieldComponent>(entityId);
                if (!fc.Enabled) continue;

                // 同步力场中心到实体位置
                if (fc.FollowEntity && ecs.HasComponent<TransformComponent>(entityId))
                {
                    var transform = ecs.GetComponent<TransformComponent>(entityId);
                    var shape = fc.Shape;
                    shape.Center = transform.Position;
                    fc.Shape = shape;
                }

                // 查询力场范围内的刚体
                var bodyIds = _query.QueryShape(fc.Shape);

                // 对每个刚体施加力
                foreach (var bodyId in bodyIds)
                {
                    ApplyForceToBody(bodyId, ref fc, dt);
                }

                // 更新湍流噪声时间
                if (fc.FieldType == ForceFieldType.Turbulence)
                {
                    fc.NoiseTime += dt * fc.TurbulenceEvolutionSpeed;
                }

                ecs.SetComponent(entityId, fc);
            }

            // 3. 施加全局风场
            if (EnableWind && Wind.Enabled)
            {
                Wind.ApplyTo(_world, _allBodies, dt);
            }
        }

        /// <summary>对单个刚体计算并施加力场力。</summary>
        private void ApplyForceToBody(uint bodyId, ref ForceFieldComponent fc, float dt)
        {
            Vector3 bodyPos = _world.GetPosition(bodyId);
            if (!fc.Shape.Contains(bodyPos)) return;

            // 计算衰减
            float falloff = fc.Shape.EvaluateFalloff(bodyPos, fc.Falloff, fc.FalloffLambda);
            if (falloff <= 0f) return;

            // 根据力场类型计算力向量
            Vector3 force = fc.FieldType switch
            {
                ForceFieldType.Radial => ComputeRadialForce(bodyPos, ref fc),
                ForceFieldType.Directional => ComputeDirectionalForce(ref fc),
                ForceFieldType.Vortex => ComputeVortexForce(bodyPos, ref fc),
                ForceFieldType.Turbulence => ComputeTurbulenceForce(bodyPos, ref fc),
                ForceFieldType.Drag => ComputeDragForce(bodyId, ref fc),
                ForceFieldType.Buoyancy => ComputeBuoyancyForce(ref fc),
                _ => Vector3.Zero,
            };

            force *= fc.Strength * falloff;

            if (force != Vector3.Zero)
                _world.AddForce(bodyId, force);
        }

        /// <summary>径向力：从中心向外推或向内吸。</summary>
        private Vector3 ComputeRadialForce(Vector3 bodyPos, ref ForceFieldComponent fc)
        {
            Vector3 toBody = bodyPos - fc.Shape.Center;
            float lenSq = toBody.LengthSquared();
            if (lenSq < 1e-8f) return Vector3.Zero;
            Vector3 dir = toBody / MathF.Sqrt(lenSq);
            return fc.RadialOutward ? dir : -dir;
        }

        /// <summary>方向力：沿固定方向施加恒定力。</summary>
        private Vector3 ComputeDirectionalForce(ref ForceFieldComponent fc)
        {
            Vector3 dir = fc.Direction;
            if (dir.LengthSquared() > 1e-8f)
                dir = Vector3.Normalize(dir);
            return dir;
        }

        /// <summary>漩涡力：绕轴线施加切向力。</summary>
        private Vector3 ComputeVortexForce(Vector3 bodyPos, ref ForceFieldComponent fc)
        {
            Vector3 axis = fc.Direction;
            if (axis.LengthSquared() > 1e-8f)
                axis = Vector3.Normalize(axis);
            else
                axis = Vector3.UnitY;

            Vector3 r = bodyPos - fc.Shape.Center;
            float proj = Vector3.Dot(r, axis);
            Vector3 planar = r - axis * proj;
            if (planar.LengthSquared() < 1e-8f) return Vector3.Zero;

            Vector3 tangent = Vector3.Cross(axis, planar);
            if (tangent.LengthSquared() > 1e-8f)
                tangent = Vector3.Normalize(tangent);
            return tangent;
        }

        /// <summary>湍流力：基于 Curl Noise 的无散度随机力。</summary>
        private Vector3 ComputeTurbulenceForce(Vector3 bodyPos, ref ForceFieldComponent fc)
        {
            return CurlNoise.Sample(
                bodyPos * fc.TurbulenceFrequency,
                new Vector3(fc.NoiseTime, fc.NoiseTime * 0.7f, fc.NoiseTime * 1.3f));
        }

        /// <summary>阻力：与刚体速度方向相反的减速力。F = -v * drag。</summary>
        private Vector3 ComputeDragForce(uint bodyId, ref ForceFieldComponent fc)
        {
            Vector3 vel = _world.GetLinearVelocity(bodyId);
            return -vel * fc.DragCoefficient;
        }

        /// <summary>浮力：向上的恒定力。</summary>
        private Vector3 ComputeBuoyancyForce(ref ForceFieldComponent fc)
        {
            return Vector3.UnitY;
        }

        /// <summary>获取 FieldQuery 实例（用于外部调试或自定义查询）。</summary>
        public FieldQuery GetQuery() => _query;
    }

    // ──────────────────────────────────────────────────────────────
    //  力场编辑器可视化（Gizmo）
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    ///   力场编辑器可视化器。在编辑器模式下绘制力场的范围和力方向 Gizmo，
    ///   帮助开发者直观了解力场的空间分布和作用方向。
    ///
    ///   Gizmo 绘制内容包括：
    ///     - 力场形状的线框轮廓（球/盒/胶囊/圆柱）
    ///     - 力方向箭头（方向力/漩涡力/浮力）
    ///     - 径向力的中心标记和向外箭头
    ///     - 衰减区域渐变色（从中心全力到边缘无力）
    ///
    ///   注：本类提供线框数据生成接口，实际渲染由引擎的 DebugDraw/Gizmo 系统执行。
    /// </summary>
    public sealed class ForceFieldRenderer
    {
        /// <summary>Gizmo 线段。</summary>
        public struct GizmoLine
        {
            /// <summary>线段起点。</summary>
            public Vector3 Start;
            /// <summary>线段终点。</summary>
            public Vector3 End;
            /// <summary>线段颜色（RGBA）。</summary>
            public Vector4 Color;
        }

        /// <summary>Gizmo 箭头。</summary>
        public struct GizmoArrow
        {
            /// <summary>起点。</summary>
            public Vector3 Origin;
            /// <summary>方向（需归一化）。</summary>
            public Vector3 Direction;
            /// <summary>长度。</summary>
            public float Length;
            /// <summary>颜色。</summary>
            public Vector4 Color;
        }

        /// <summary>力场范围的线框颜色（半透明青色）。</summary>
        public Vector4 RangeColor = new(0.2f, 0.8f, 1.0f, 0.6f);

        /// <summary>力方向箭头颜色（亮黄色）。</summary>
        public Vector4 DirectionColor = new(1.0f, 0.9f, 0.2f, 1.0f);

        /// <summary>径向力箭头颜色（红色=向外，蓝色=向内）。</summary>
        public Vector4 OutwardColor = new(1.0f, 0.3f, 0.3f, 1.0f);
        public Vector4 InwardColor = new(0.3f, 0.5f, 1.0f, 1.0f);

        /// <summary>力场线框的经线/纬线分段数。</summary>
        public int WireSegments { get; set; } = 24;

        /// <summary>箭头长度系数（相对于力场半径）。</summary>
        public float ArrowLengthScale { get; set; } = 0.4f;

        /// <summary>
        ///   为一个力场组件生成 Gizmo 线段和箭头数据。
        /// </summary>
        public void BuildGizmo(ForceFieldComponent fc, List<GizmoLine> lines, List<GizmoArrow> arrows)
        {
            if (!fc.Enabled) return;

            // 1. 生成力场形状线框
            BuildShapeWireframe(fc.Shape, lines);

            // 2. 生成力方向箭头
            BuildForceArrows(fc, arrows);
        }

        /// <summary>生成力场形状的线框轮廓。</summary>
        private void BuildShapeWireframe(ForceFieldShape shape, List<GizmoLine> lines)
        {
            switch (shape.Type)
            {
                case ForceFieldShapeType.Sphere:
                    BuildSphereWireframe(shape.Center, shape.Radius, lines);
                    break;
                case ForceFieldShapeType.Box:
                    BuildBoxWireframe(shape.Center, shape.BoxHalfExtents, lines);
                    break;
                case ForceFieldShapeType.Capsule:
                    BuildCapsuleWireframe(shape.CapsuleP1, shape.CapsuleP2, shape.Radius, lines);
                    break;
                case ForceFieldShapeType.Cylinder:
                    BuildCylinderWireframe(shape.Center, shape.Axis, shape.Radius, shape.Height, lines);
                    break;
            }
        }

        private void BuildSphereWireframe(Vector3 center, float radius, List<GizmoLine> lines)
        {
            int seg = WireSegments;
            // 经线
            for (int i = 0; i < 4; i++)
            {
                float phi = MathF.PI * i / 4f;
                Vector3 prev = Vector3.Zero;
                for (int j = 0; j <= seg; j++)
                {
                    float theta = 2f * MathF.PI * j / seg;
                    Vector3 p = center + new Vector3(
                        radius * MathF.Sin(phi) * MathF.Cos(theta),
                        radius * MathF.Cos(phi),
                        radius * MathF.Sin(phi) * MathF.Sin(theta));
                    if (j > 0)
                        lines.Add(new GizmoLine { Start = prev, End = p, Color = RangeColor });
                    prev = p;
                }
            }
            // 纬线
            for (int i = 1; i < 4; i++)
            {
                float phi = MathF.PI * i / 4f;
                Vector3 prev = Vector3.Zero;
                for (int j = 0; j <= seg; j++)
                {
                    float theta = 2f * MathF.PI * j / seg;
                    Vector3 p = center + new Vector3(
                        radius * MathF.Sin(phi) * MathF.Cos(theta),
                        radius * MathF.Cos(phi),
                        radius * MathF.Sin(phi) * MathF.Sin(theta));
                    if (j > 0)
                        lines.Add(new GizmoLine { Start = prev, End = p, Color = RangeColor });
                    prev = p;
                }
            }
        }

        private void BuildBoxWireframe(Vector3 center, Vector3 half, List<GizmoLine> lines)
        {
            Vector3[] c = new Vector3[8];
            int idx = 0;
            for (int x = -1; x <= 1; x += 2)
                for (int y = -1; y <= 1; y += 2)
                    for (int z = -1; z <= 1; z += 2)
                        c[idx++] = center + new Vector3(x * half.X, y * half.Y, z * half.Z);

            int[][] edges = new int[][]
            {
                new int[] {0,1},new int[] {0,2},new int[] {0,4},new int[] {1,3},new int[] {1,5},new int[] {2,3},
                new int[] {2,6},new int[] {3,7},new int[] {4,5},new int[] {4,6},new int[] {5,7},new int[] {6,7}
            };
            foreach (var e in edges)
                lines.Add(new GizmoLine { Start = c[e[0]], End = c[e[1]], Color = RangeColor });
        }

        private void BuildCapsuleWireframe(Vector3 p1, Vector3 p2, float radius, List<GizmoLine> lines)
        {
            // 两端的球面线框（简化）
            BuildSphereWireframe(p1, radius, lines);
            BuildSphereWireframe(p2, radius, lines);
            // 连接线
            Vector3 axis = p2 - p1;
            if (axis.LengthSquared() < 1e-8f) return;
            axis = Vector3.Normalize(axis);
            Vector3 perp = MathF.Abs(axis.Y) < 0.99f ? Vector3.Cross(axis, Vector3.UnitY) : Vector3.Cross(axis, Vector3.UnitX);
            perp = Vector3.Normalize(perp) * radius;
            Vector3 perp2 = Vector3.Cross(axis, perp) * radius;
            lines.Add(new GizmoLine { Start = p1 + perp, End = p2 + perp, Color = RangeColor });
            lines.Add(new GizmoLine { Start = p1 - perp, End = p2 - perp, Color = RangeColor });
            lines.Add(new GizmoLine { Start = p1 + perp2, End = p2 + perp2, Color = RangeColor });
            lines.Add(new GizmoLine { Start = p1 - perp2, End = p2 - perp2, Color = RangeColor });
        }

        private void BuildCylinderWireframe(Vector3 center, Vector3 axis, float radius, float height, List<GizmoLine> lines)
        {
            Vector3 normAxis = axis.LengthSquared() > 1e-8f ? Vector3.Normalize(axis) : Vector3.UnitY;
            Vector3 perp = MathF.Abs(normAxis.Y) < 0.99f
                ? Vector3.Cross(normAxis, Vector3.UnitY)
                : Vector3.Cross(normAxis, Vector3.UnitX);
            perp = Vector3.Normalize(perp);
            Vector3 perp2 = Vector3.Cross(normAxis, perp);

            Vector3 top = center + normAxis * (height * 0.5f);
            Vector3 bot = center - normAxis * (height * 0.5f);
            int seg = WireSegments;

            Vector3 prevTop = Vector3.Zero, prevBot = Vector3.Zero;
            for (int i = 0; i <= seg; i++)
            {
                float angle = 2f * MathF.PI * i / seg;
                Vector3 offset = (perp * MathF.Cos(angle) + perp2 * MathF.Sin(angle)) * radius;
                Vector3 topP = top + offset;
                Vector3 botP = bot + offset;
                if (i > 0)
                {
                    lines.Add(new GizmoLine { Start = prevTop, End = topP, Color = RangeColor });
                    lines.Add(new GizmoLine { Start = prevBot, End = botP, Color = RangeColor });
                    lines.Add(new GizmoLine { Start = topP, End = botP, Color = RangeColor });
                }
                prevTop = topP;
                prevBot = botP;
            }
        }

        /// <summary>生成力方向箭头。</summary>
        private void BuildForceArrows(ForceFieldComponent fc, List<GizmoArrow> arrows)
        {
            float arrowLen = GetArrowLength(fc.Shape);

            switch (fc.FieldType)
            {
                case ForceFieldType.Directional:
                case ForceFieldType.Buoyancy:
                    {
                        Vector3 dir = fc.FieldType == ForceFieldType.Buoyancy
                            ? Vector3.UnitY
                            : (fc.Direction.LengthSquared() > 1e-8f ? Vector3.Normalize(fc.Direction) : Vector3.UnitX);
                        arrows.Add(new GizmoArrow
                        {
                            Origin = fc.Shape.Center,
                            Direction = dir,
                            Length = arrowLen,
                            Color = DirectionColor,
                        });
                    }
                    break;

                case ForceFieldType.Radial:
                    {
                        Vector4 color = fc.RadialOutward ? OutwardColor : InwardColor;
                        int arrowCount = 6;
                        for (int i = 0; i < arrowCount; i++)
                        {
                            float angle = 2f * MathF.PI * i / arrowCount;
                            Vector3 dir = new(MathF.Cos(angle), 0, MathF.Sin(angle));
                            Vector3 origin = fc.Shape.Center + dir * arrowLen * 0.5f;
                            arrows.Add(new GizmoArrow
                            {
                                Origin = origin,
                                Direction = fc.RadialOutward ? dir : -dir,
                                Length = arrowLen * 0.5f,
                                Color = color,
                            });
                        }
                    }
                    break;

                case ForceFieldType.Vortex:
                    {
                        Vector3 axis = fc.Direction.LengthSquared() > 1e-8f
                            ? Vector3.Normalize(fc.Direction) : Vector3.UnitY;
                        int arrowCount = 4;
                        for (int i = 0; i < arrowCount; i++)
                        {
                            float angle = 2f * MathF.PI * i / arrowCount;
                            Vector3 radial = new(MathF.Cos(angle), 0, MathF.Sin(angle));
                            Vector3 tangent = Vector3.Cross(axis, radial);
                            if (tangent.LengthSquared() > 1e-8f) tangent = Vector3.Normalize(tangent);
                            arrows.Add(new GizmoArrow
                            {
                                Origin = fc.Shape.Center + radial * arrowLen * 0.5f,
                                Direction = tangent,
                                Length = arrowLen * 0.4f,
                                Color = DirectionColor,
                            });
                        }
                    }
                    break;

                case ForceFieldType.Turbulence:
                    {
                        // 湍流力方向随机，绘制几个随机方向箭头示意
                        var rand = new Random(42);
                        for (int i = 0; i < 4; i++)
                        {
                            Vector3 dir = new(
                                (float)(rand.NextDouble() * 2 - 1),
                                (float)(rand.NextDouble() * 2 - 1),
                                (float)(rand.NextDouble() * 2 - 1));
                            if (dir.LengthSquared() > 1e-8f) dir = Vector3.Normalize(dir);
                            arrows.Add(new GizmoArrow
                            {
                                Origin = fc.Shape.Center + dir * arrowLen * 0.3f,
                                Direction = dir,
                                Length = arrowLen * 0.3f,
                                Color = DirectionColor,
                            });
                        }
                    }
                    break;

                case ForceFieldType.Drag:
                    {
                        // 阻力方向与速度相反，绘制一个向中心的箭头
                        arrows.Add(new GizmoArrow
                        {
                            Origin = fc.Shape.Center + Vector3.UnitX * arrowLen * 0.5f,
                            Direction = -Vector3.UnitX,
                            Length = arrowLen * 0.4f,
                            Color = InwardColor,
                        });
                    }
                    break;
            }
        }

        private static float GetArrowLength(ForceFieldShape shape)
        {
            return shape.Type switch
            {
                ForceFieldShapeType.Sphere => shape.Radius,
                ForceFieldShapeType.Capsule => shape.Radius * 2f,
                ForceFieldShapeType.Cylinder => shape.Radius * 2f,
                ForceFieldShapeType.Box => Math.Max(shape.BoxHalfExtents.X, Math.Max(shape.BoxHalfExtents.Y, shape.BoxHalfExtents.Z)),
                _ => 1f,
            };
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  力场系统管理器
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    ///   力场系统管理器。提供力场创建的便捷工厂方法，统一管理力场系统资源。
    ///   封装 ForceFieldSystem 的初始化和 WindField 配置。
    /// </summary>
    public sealed class ForceFieldSystemManager
    {
        private readonly ForceFieldSystem _system;
        private readonly ForceFieldRenderer _renderer = new();

        /// <summary>获取力场系统实例。</summary>
        public ForceFieldSystem System => _system;

        /// <summary>获取力场可视化渲染器。</summary>
        public ForceFieldRenderer Renderer => _renderer;

        /// <summary>获取全局风场。</summary>
        public WindField Wind => _system.Wind;

        public ForceFieldSystemManager(PhysicsWorld world)
        {
            _system = new ForceFieldSystem(world);
        }

        /// <summary>在 ECS 中创建一个径向力场实体。</summary>
        public int CreateRadialField(EntityComponentSystem ecs, Vector3 center, float radius,
            float strength, bool outward = true,
            ForceFieldFalloffType falloff = ForceFieldFalloffType.Linear)
        {
            var id = ecs.CreateEntity();
            ecs.AddComponent(id, new ForceFieldComponent
            {
                FieldType = ForceFieldType.Radial,
                Strength = strength,
                Falloff = falloff,
                Shape = ForceFieldShape.Sphere(center, radius),
                RadialOutward = outward,
                Enabled = true,
                FollowEntity = false,
            });
            return id;
        }

        /// <summary>在 ECS 中创建一个方向力场实体。</summary>
        public int CreateDirectionalField(EntityComponentSystem ecs, Vector3 center, Vector3 halfExtents,
            Vector3 direction, float strength,
            ForceFieldFalloffType falloff = ForceFieldFalloffType.None)
        {
            var id = ecs.CreateEntity();
            ecs.AddComponent(id, new ForceFieldComponent
            {
                FieldType = ForceFieldType.Directional,
                Strength = strength,
                Falloff = falloff,
                Shape = ForceFieldShape.Box(center, halfExtents),
                Direction = direction,
                Enabled = true,
                FollowEntity = false,
            });
            return id;
        }

        /// <summary>在 ECS 中创建一个漩涡力场实体。</summary>
        public int CreateVortexField(EntityComponentSystem ecs, Vector3 center, float radius,
            Vector3 axis, float strength,
            ForceFieldFalloffType falloff = ForceFieldFalloffType.Linear)
        {
            var id = ecs.CreateEntity();
            ecs.AddComponent(id, new ForceFieldComponent
            {
                FieldType = ForceFieldType.Vortex,
                Strength = strength,
                Falloff = falloff,
                Shape = ForceFieldShape.Sphere(center, radius),
                Direction = axis,
                Enabled = true,
                FollowEntity = false,
            });
            return id;
        }

        /// <summary>在 ECS 中创建一个湍流力场实体。</summary>
        public int CreateTurbulenceField(EntityComponentSystem ecs, Vector3 center, float radius,
            float frequency, float strength,
            ForceFieldFalloffType falloff = ForceFieldFalloffType.None)
        {
            var id = ecs.CreateEntity();
            ecs.AddComponent(id, new ForceFieldComponent
            {
                FieldType = ForceFieldType.Turbulence,
                Strength = strength,
                Falloff = falloff,
                Shape = ForceFieldShape.Sphere(center, radius),
                TurbulenceFrequency = frequency,
                Enabled = true,
                FollowEntity = false,
            });
            return id;
        }

        /// <summary>在 ECS 中创建一个阻力场实体。</summary>
        public int CreateDragField(EntityComponentSystem ecs, Vector3 center, float radius,
            float dragCoefficient, float strength,
            ForceFieldFalloffType falloff = ForceFieldFalloffType.None)
        {
            var id = ecs.CreateEntity();
            ecs.AddComponent(id, new ForceFieldComponent
            {
                FieldType = ForceFieldType.Drag,
                Strength = strength,
                Falloff = falloff,
                Shape = ForceFieldShape.Sphere(center, radius),
                DragCoefficient = dragCoefficient,
                Enabled = true,
                FollowEntity = false,
            });
            return id;
        }

        /// <summary>在 ECS 中创建一个浮力场实体。</summary>
        public int CreateBuoyancyField(EntityComponentSystem ecs, Vector3 center, Vector3 halfExtents,
            float strength,
            ForceFieldFalloffType falloff = ForceFieldFalloffType.None)
        {
            var id = ecs.CreateEntity();
            ecs.AddComponent(id, new ForceFieldComponent
            {
                FieldType = ForceFieldType.Buoyancy,
                Strength = strength,
                Falloff = falloff,
                Shape = ForceFieldShape.Box(center, halfExtents),
                Enabled = true,
                FollowEntity = false,
            });
            return id;
        }

        /// <summary>配置全局风场。</summary>
        public void ConfigureWind(Vector3 direction, float strength, float turbulence = 0.3f)
        {
            Wind.WindDirection = direction;
            Wind.Strength = strength;
            Wind.TurbulenceStrength = turbulence;
        }
    }
}
