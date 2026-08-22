using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace ShanyuEngine.Physics
{
    // ============================================================================================
    //  CollisionDispatcher.cs
    //
    //  碰撞分派器。根据两个形状的类型，调用对应的碰撞检测函数。
    //  作用就是解耦: NarrowphaseSystem不用关心具体形状对，只要问Dispatcher要个函数就行。
    //
    //  典型的形状对:
    //    Box-Box, Box-Sphere, Box-Capsule, Box-Convex, Box-Mesh
    //    Sphere-Sphere, Sphere-Capsule, Sphere-Convex, Sphere-Mesh
    //    Capsule-Capsule, Capsule-Convex, Capsule-Mesh
    //    Convex-Convex (GJK/EPA)
    //    Convex-Mesh, Mesh-Mesh
    //    Compound-任何
    //
    //  用查表 + 双分派(Double Dispatch)实现。
    //
    //  注意: 所有形状对都要实现，不然会有运行时报错。
    //  TODO: Heightfield-各种 还没做。
    // ============================================================================================

    /// <summary>碰撞形状类型。</summary>
    public enum CollisionShapeType
    {
        Empty,
        Sphere,
        Box,
        Capsule,
        Cylinder,
        Cone,
        ConvexHull,
        Mesh,
        Heightfield,
        Compound,
        Plane,
        Ray,
        Custom = 99,
    }

    /// <summary>碰撞检测算法类型。</summary>
    public enum CollisionAlgorithmType
    {
        /// <summary>无碰撞(其中一个为Empty)。</summary>
        None,

        /// <summary>解析公式(球-球等)。</summary>
        Analytic,

        /// <summary>GJK+EPA。</summary>
        GjkEpa,

        /// <summary>SAT分离轴。</summary>
        Sat,

        /// <summary>网格遍历。</summary>
        MeshTraversal,

        /// <summary>简化(网格当凸包)。</summary>
        MeshAsConvex,

        /// <summary>分治(Compound的每个子形状)。</summary>
        CompoundDispatch,

        /// <summary>射线投射。</summary>
        Raycast,
    }

    /// <summary>碰撞查询输入。</summary>
    public struct CollisionQueryInput
    {
        public Vector3 PosA;
        public Quaternion RotA;
        public ICollisionShape ShapeA;

        public Vector3 PosB;
        public Quaternion RotB;
        public ICollisionShape ShapeB;

        public float AllowedPenetration;
        public bool UseContinuous;  // CCD
        public Vector3 VelocityA;
        public Vector3 VelocityB;
    }

    /// <summary>碰撞查询输出(接触点)。</summary>
    public struct CollisionQueryOutput
    {
        public bool HasCollision;
        public Vector3 ClosestPointA;   // A上的最近点
        public Vector3 ClosestPointB;   // B上的最近点
        public Vector3 Normal;          // 从A指向B
        public float Distance;          // 负数=穿透
        public int ContactCount;
        public Vector4[] Contacts;      // xyz=点, w=穿透
        public CollisionAlgorithmType Algorithm;
    }

    /// <summary>碰撞形状接口。所有形状实现这个。</summary>
    public interface ICollisionShape
    {
        CollisionShapeType Type { get; }
        Vector3 LocalCenter { get; }
        float Volume { get; }
        void GetAABB(Vector3 position, Quaternion rotation, out Vector3 min, out Vector3 max);
        Vector3 GetSupportPoint(Vector3 direction, Vector3 position, Quaternion rotation);
        bool Raycast(Vector3 origin, Vector3 direction, float maxDistance,
            Vector3 position, Quaternion rotation, out float t, out Vector3 normal);
    }

    /// <summary>
    ///   碰撞分派器。根据形状对类型选择碰撞算法。
    /// </summary>
    public sealed class CollisionDispatcher
    {
        // 形状对到算法的映射表
        private readonly CollisionAlgorithmType[,] _algorithmTable;
        // 形状对到检测函数的映射
        private readonly Dictionary<(CollisionShapeType, CollisionShapeType), CollisionDetector> _detectors;
        private readonly int _typeCount;
        private bool _enableCaching;
        private readonly Dictionary<(int, int), CollisionQueryOutput> _cache;
        private int _cacheHits;
        private int _cacheMisses;
        private int _totalDispatches;

        public bool EnableCaching
        {
            get => _enableCaching;
            set
            {
                _enableCaching = value;
                if (!value) _cache.Clear();
            }
        }

        public int CacheHits => _cacheHits;
        public int CacheMisses => _cacheMisses;
        public int TotalDispatches => _totalDispatches;

        public CollisionDispatcher()
        {
            _typeCount = Enum.GetValues<CollisionShapeType>().Length;
            _algorithmTable = new CollisionAlgorithmType[_typeCount, _typeCount];
            _detectors = new Dictionary<(CollisionShapeType, CollisionShapeType), CollisionDetector>(64);
            _cache = new Dictionary<(int, int), CollisionQueryOutput>(256);
            InitializeDefaultAlgorithms();
            RegisterDefaultDetectors();
        }

        // ========================================================================================
        //  默认算法表
        // ========================================================================================

        private void InitializeDefaultAlgorithms()
        {
            // 默认全部GJK(对凸包有效)
            for (int i = 0; i < _typeCount; i++)
            {
                for (int j = 0; j < _typeCount; j++)
                {
                    _algorithmTable[i, j] = CollisionAlgorithmType.GjkEpa;
                }
            }

            // Empty
            SetAlgorithm(CollisionShapeType.Empty, CollisionShapeType.Empty, CollisionAlgorithmType.None);
            for (int i = 0; i < _typeCount; i++)
            {
                SetAlgorithm((CollisionShapeType)i, CollisionShapeType.Empty, CollisionAlgorithmType.None);
                SetAlgorithm(CollisionShapeType.Empty, (CollisionShapeType)i, CollisionAlgorithmType.None);
            }

            // 球-球: 解析
            SetAlgorithm(CollisionShapeType.Sphere, CollisionShapeType.Sphere, CollisionAlgorithmType.Analytic);

            // 球-Box: 解析
            SetAlgorithm(CollisionShapeType.Sphere, CollisionShapeType.Box, CollisionAlgorithmType.Analytic);
            SetAlgorithm(CollisionShapeType.Box, CollisionShapeType.Sphere, CollisionAlgorithmType.Analytic);

            // 球-胶囊: 解析
            SetAlgorithm(CollisionShapeType.Sphere, CollisionShapeType.Capsule, CollisionAlgorithmType.Analytic);
            SetAlgorithm(CollisionShapeType.Capsule, CollisionShapeType.Sphere, CollisionAlgorithmType.Analytic);

            // 球-平面: 解析
            SetAlgorithm(CollisionShapeType.Sphere, CollisionShapeType.Plane, CollisionAlgorithmType.Analytic);
            SetAlgorithm(CollisionShapeType.Plane, CollisionShapeType.Sphere, CollisionAlgorithmType.Analytic);

            // Box-Box: SAT
            SetAlgorithm(CollisionShapeType.Box, CollisionShapeType.Box, CollisionAlgorithmType.Sat);

            // Capsule-Capsule: 解析
            SetAlgorithm(CollisionShapeType.Capsule, CollisionShapeType.Capsule, CollisionAlgorithmType.Analytic);

            // Box-Capsule: 解析(简化)
            SetAlgorithm(CollisionShapeType.Box, CollisionShapeType.Capsule, CollisionAlgorithmType.Analytic);
            SetAlgorithm(CollisionShapeType.Capsule, CollisionShapeType.Box, CollisionAlgorithmType.Analytic);

            // 网格相关
            for (int i = 0; i < _typeCount; i++)
            {
                var t = (CollisionShapeType)i;
                if (t == CollisionShapeType.Mesh) continue;
                SetAlgorithm(t, CollisionShapeType.Mesh, CollisionAlgorithmType.MeshTraversal);
                SetAlgorithm(CollisionShapeType.Mesh, t, CollisionAlgorithmType.MeshTraversal);
            }
            SetAlgorithm(CollisionShapeType.Mesh, CollisionShapeType.Mesh, CollisionAlgorithmType.MeshTraversal);

            // 高度场
            for (int i = 0; i < _typeCount; i++)
            {
                var t = (CollisionShapeType)i;
                if (t == CollisionShapeType.Heightfield) continue;
                SetAlgorithm(t, CollisionShapeType.Heightfield, CollisionAlgorithmType.MeshTraversal);
                SetAlgorithm(CollisionShapeType.Heightfield, t, CollisionAlgorithmType.MeshTraversal);
            }

            // Compound
            for (int i = 0; i < _typeCount; i++)
            {
                var t = (CollisionShapeType)i;
                if (t == CollisionShapeType.Compound) continue;
                SetAlgorithm(t, CollisionShapeType.Compound, CollisionAlgorithmType.CompoundDispatch);
                SetAlgorithm(CollisionShapeType.Compound, t, CollisionAlgorithmType.CompoundDispatch);
            }
            SetAlgorithm(CollisionShapeType.Compound, CollisionShapeType.Compound,
                CollisionAlgorithmType.CompoundDispatch);

            // Ray
            for (int i = 0; i < _typeCount; i++)
            {
                var t = (CollisionShapeType)i;
                if (t == CollisionShapeType.Ray) continue;
                SetAlgorithm(CollisionShapeType.Ray, t, CollisionAlgorithmType.Raycast);
                SetAlgorithm(t, CollisionShapeType.Ray, CollisionAlgorithmType.Raycast);
            }
        }

        private void SetAlgorithm(CollisionShapeType a, CollisionShapeType b, CollisionAlgorithmType algo)
        {
            _algorithmTable[(int)a, (int)b] = algo;
        }

        /// <summary>获取两个形状类型对应的算法。</summary>
        public CollisionAlgorithmType GetAlgorithm(CollisionShapeType a, CollisionShapeType b)
        {
            return _algorithmTable[(int)a, (int)b];
        }

        // ========================================================================================
        //  检测函数注册
        // ========================================================================================

        /// <summary>碰撞检测函数委托。</summary>
        public delegate void CollisionDetector(ref CollisionQueryInput input, ref CollisionQueryOutput output);

        /// <summary>注册一个形状对的检测函数。</summary>
        public void RegisterDetector(CollisionShapeType a, CollisionShapeType b, CollisionDetector detector)
        {
            _detectors[(a, b)] = detector;
            // 同时注册反向(B,A)
            _detectors[(b, a)] = (ref CollisionQueryInput input, ref CollisionQueryOutput output) =>
            {
                // 交换A和B
                var swapped = new CollisionQueryInput
                {
                    PosA = input.PosB, RotA = input.RotB, ShapeA = input.ShapeB,
                    PosB = input.PosA, RotB = input.RotA, ShapeB = input.ShapeA,
                    AllowedPenetration = input.AllowedPenetration,
                    UseContinuous = input.UseContinuous,
                    VelocityA = input.VelocityB,
                    VelocityB = input.VelocityA,
                };
                detector(ref swapped, ref output);
                // 反转法线
                output.Normal = -output.Normal;
                (output.ClosestPointA, output.ClosestPointB) = (output.ClosestPointB, output.ClosestPointA);
            };
        }

        private void RegisterDefaultDetectors()
        {
            RegisterDetector(CollisionShapeType.Sphere, CollisionShapeType.Sphere, SphereSphere);
            RegisterDetector(CollisionShapeType.Sphere, CollisionShapeType.Box, SphereBox);
            RegisterDetector(CollisionShapeType.Sphere, CollisionShapeType.Capsule, SphereCapsule);
            RegisterDetector(CollisionShapeType.Sphere, CollisionShapeType.Plane, SpherePlane);
            RegisterDetector(CollisionShapeType.Box, CollisionShapeType.Box, BoxBox);
            RegisterDetector(CollisionShapeType.Capsule, CollisionShapeType.Capsule, CapsuleCapsule);
            RegisterDetector(CollisionShapeType.Box, CollisionShapeType.Capsule, BoxCapsule);
            RegisterDetector(CollisionShapeType.ConvexHull, CollisionShapeType.ConvexHull, ConvexConvexGjk);
        }

        // ========================================================================================
        //  分派
        // ========================================================================================

        /// <summary>
        ///   分派碰撞检测。
        /// </summary>
        public void Dispatch(ref CollisionQueryInput input, ref CollisionQueryOutput output)
        {
            _totalDispatches++;

            if (input.ShapeA == null || input.ShapeB == null)
            {
                output.HasCollision = false;
                output.Algorithm = CollisionAlgorithmType.None;
                return;
            }

            var typeA = input.ShapeA.Type;
            var typeB = input.ShapeB.Type;

            // Empty: 没碰撞
            if (typeA == CollisionShapeType.Empty || typeB == CollisionShapeType.Empty)
            {
                output.HasCollision = false;
                output.Algorithm = CollisionAlgorithmType.None;
                return;
            }

            // 缓存检查
            if (_enableCaching)
            {
                int hash = HashCode.Combine(
                    input.PosA, input.RotA, typeA,
                    input.PosB, input.RotB, typeB);
                int hash2 = HashCode.Combine(input.ShapeA, input.ShapeB);
                var key = (hash, hash2);
                if (_cache.TryGetValue(key, out var cached))
                {
                    output = cached;
                    _cacheHits++;
                    return;
                }
                _cacheMisses++;
            }

            // 查算法
            output.Algorithm = GetAlgorithm(typeA, typeB);

            // 查检测函数
            if (_detectors.TryGetValue((typeA, typeB), out var detector))
            {
                detector(ref input, ref output);
            }
            else
            {
                // 没注册过，用GJK兜底
                if (IsConvexShape(typeA) && IsConvexShape(typeB))
                {
                    ConvexConvexGjk(ref input, ref output);
                }
                else if (typeA == CollisionShapeType.Compound || typeB == CollisionShapeType.Compound)
                {
                    CompoundDispatch(ref input, ref output);
                }
                else if (typeA == CollisionShapeType.Mesh || typeB == CollisionShapeType.Mesh)
                {
                    MeshDispatch(ref input, ref output);
                }
                else
                {
                    output.HasCollision = false;
                }
            }

            // 写缓存
            if (_enableCaching && _cache.Count < 1024)
            {
                int hash = HashCode.Combine(
                    input.PosA, input.RotA, typeA,
                    input.PosB, input.RotB, typeB);
                int hash2 = HashCode.Combine(input.ShapeA, input.ShapeB);
                _cache[(hash, hash2)] = output;
            }
        }

        private static bool IsConvexShape(CollisionShapeType type)
        {
            return type is CollisionShapeType.Sphere or CollisionShapeType.Box or
                   CollisionShapeType.Capsule or CollisionShapeType.Cylinder or
                   CollisionShapeType.Cone or CollisionShapeType.ConvexHull;
        }

        // ========================================================================================
        //  默认检测函数
        // ========================================================================================

        /// <summary>球-球碰撞。</summary>
        public static void SphereSphere(ref CollisionQueryInput input, ref CollisionQueryOutput output)
        {
            var sphereA = (SphereShape)input.ShapeA;
            var sphereB = (SphereShape)input.ShapeB;

            Vector3 centerA = input.PosA + Vector3.Transform(sphereA.LocalCenter, input.RotA);
            Vector3 centerB = input.PosB + Vector3.Transform(sphereB.LocalCenter, input.RotB);

            Vector3 diff = centerB - centerA;
            float distSq = diff.LengthSquared();
            float radius = sphereA.Radius + sphereB.Radius;

            output.Contacts = new Vector4[1];

            if (distSq < radius * radius)
            {
                float dist = MathF.Sqrt(distSq);
                output.HasCollision = true;
                output.Distance = dist - radius;
                output.Normal = dist > 1e-6f ? diff / dist : Vector3.UnitY;
                output.ClosestPointA = centerA + output.Normal * sphereA.Radius;
                output.ClosestPointB = centerB - output.Normal * sphereB.Radius;
                output.ContactCount = 1;
                output.Contacts[0] = new Vector4(
                    (output.ClosestPointA + output.ClosestPointB) * 0.5f,
                    -output.Distance);
            }
            else
            {
                output.HasCollision = false;
                float dist = MathF.Sqrt(distSq);
                output.Distance = dist - radius;
                output.Normal = dist > 1e-6f ? diff / dist : Vector3.UnitY;
                output.ClosestPointA = centerA + output.Normal * sphereA.Radius;
                output.ClosestPointB = centerB - output.Normal * sphereB.Radius;
                output.ContactCount = 0;
            }
        }

        /// <summary>球-盒碰撞。</summary>
        public static void SphereBox(ref CollisionQueryInput input, ref CollisionQueryOutput output)
        {
            var sphere = (SphereShape)input.ShapeA;
            var box = (BoxShape)input.ShapeB;

            Vector3 sphereCenter = input.PosA + Vector3.Transform(sphere.LocalCenter, input.RotA);

            // 把球心转到box局部坐标系
            Vector3 localSphere = Vector3.Transform(sphereCenter - input.PosB, Quaternion.Inverse(input.RotB));

            // 找box上最近的点(clamp)
            Vector3 closest = new(
                Math.Clamp(localSphere.X, -box.HalfExtents.X, box.HalfExtents.X),
                Math.Clamp(localSphere.Y, -box.HalfExtents.Y, box.HalfExtents.Y),
                Math.Clamp(localSphere.Z, -box.HalfExtents.Z, box.HalfExtents.Z));

            Vector3 diff = localSphere - closest;
            float distSq = diff.LengthSquared();

            output.Contacts = new Vector4[1];

            if (distSq < sphere.Radius * sphere.Radius)
            {
                float dist = MathF.Sqrt(distSq);
                Vector3 localNormal = dist > 1e-6f ? diff / dist : Vector3.UnitY;
                Vector3 worldNormal = Vector3.Transform(localNormal, input.RotB);
                Vector3 worldClosest = input.PosB + Vector3.Transform(closest, input.RotB);

                output.HasCollision = true;
                output.Distance = dist - sphere.Radius;
                output.Normal = worldNormal;
                output.ClosestPointA = sphereCenter - worldNormal * sphere.Radius;
                output.ClosestPointB = worldClosest;
                output.ContactCount = 1;
                output.Contacts[0] = new Vector4(
                    (output.ClosestPointA + output.ClosestPointB) * 0.5f,
                    -output.Distance);
            }
            else
            {
                float dist = MathF.Sqrt(distSq);
                Vector3 localNormal = dist > 1e-6f ? diff / dist : Vector3.UnitY;
                Vector3 worldNormal = Vector3.Transform(localNormal, input.RotB);
                Vector3 worldClosest = input.PosB + Vector3.Transform(closest, input.RotB);

                output.HasCollision = false;
                output.Distance = dist - sphere.Radius;
                output.Normal = worldNormal;
                output.ClosestPointA = sphereCenter - worldNormal * sphere.Radius;
                output.ClosestPointB = worldClosest;
                output.ContactCount = 0;
            }
        }

        /// <summary>球-胶囊碰撞。</summary>
        public static void SphereCapsule(ref CollisionQueryInput input, ref CollisionQueryOutput output)
        {
            var sphere = (SphereShape)input.ShapeA;
            var capsule = (CapsuleShape)input.ShapeB;

            Vector3 sphereCenter = input.PosA + Vector3.Transform(sphere.LocalCenter, input.RotA);

            // 胶囊的两个端点(世界系)
            Vector3 capAxis = Vector3.Transform(Vector3.UnitY, input.RotB);
            Vector3 capCenter = input.PosB + Vector3.Transform(capsule.LocalCenter, input.RotB);
            Vector3 capP0 = capCenter - capAxis * (capsule.HalfHeight);
            Vector3 capP1 = capCenter + capAxis * (capsule.HalfHeight);

            // 球心到线段最近点
            Vector3 closest = ClosestPointOnSegment(sphereCenter, capP0, capP1);

            Vector3 diff = sphereCenter - closest;
            float distSq = diff.LengthSquared();
            float radius = sphere.Radius + capsule.Radius;

            output.Contacts = new Vector4[1];

            if (distSq < radius * radius)
            {
                float dist = MathF.Sqrt(distSq);
                output.HasCollision = true;
                output.Distance = dist - radius;
                output.Normal = dist > 1e-6f ? diff / dist : Vector3.UnitY;
                output.ClosestPointA = sphereCenter - output.Normal * sphere.Radius;
                output.ClosestPointB = closest + output.Normal * capsule.Radius;
                output.ContactCount = 1;
                output.Contacts[0] = new Vector4(
                    (output.ClosestPointA + output.ClosestPointB) * 0.5f,
                    -output.Distance);
            }
            else
            {
                float dist = MathF.Sqrt(distSq);
                output.HasCollision = false;
                output.Distance = dist - radius;
                output.Normal = dist > 1e-6f ? diff / dist : Vector3.UnitY;
                output.ClosestPointA = sphereCenter - output.Normal * sphere.Radius;
                output.ClosestPointB = closest + output.Normal * capsule.Radius;
                output.ContactCount = 0;
            }
        }

        /// <summary>球-平面碰撞。</summary>
        public static void SpherePlane(ref CollisionQueryInput input, ref CollisionQueryOutput output)
        {
            var sphere = (SphereShape)input.ShapeA;
            var plane = (PlaneShape)input.ShapeB;

            Vector3 sphereCenter = input.PosA + Vector3.Transform(sphere.LocalCenter, input.RotA);
            Vector3 planeNormal = Vector3.Transform(plane.Normal, input.RotB);
            Vector3 planePoint = input.PosB + Vector3.Transform(plane.LocalCenter, input.RotB);

            float dist = Vector3.Dot(sphereCenter - planePoint, planeNormal);

            output.Contacts = new Vector4[1];

            if (dist < sphere.Radius)
            {
                output.HasCollision = true;
                output.Distance = dist - sphere.Radius;
                output.Normal = planeNormal;
                output.ClosestPointA = sphereCenter - planeNormal * sphere.Radius;
                output.ClosestPointB = sphereCenter - planeNormal * dist;
                output.ContactCount = 1;
                output.Contacts[0] = new Vector4(
                    (output.ClosestPointA + output.ClosestPointB) * 0.5f,
                    -output.Distance);
            }
            else
            {
                output.HasCollision = false;
                output.Distance = dist - sphere.Radius;
                output.Normal = planeNormal;
                output.ClosestPointA = sphereCenter - planeNormal * sphere.Radius;
                output.ClosestPointB = sphereCenter - planeNormal * dist;
                output.ContactCount = 0;
            }
        }

        /// <summary>盒-盒碰撞(SAT)。</summary>
        public static void BoxBox(ref CollisionQueryInput input, ref CollisionQueryOutput output)
        {
            var boxA = (BoxShape)input.ShapeA;
            var boxB = (BoxShape)input.ShapeB;

            // SAT: 15个分离轴
            // 3个A的轴 + 3个B的轴 + 9个叉积
            Vector3 axesA0 = Vector3.Transform(Vector3.UnitX, input.RotA);
            Vector3 axesA1 = Vector3.Transform(Vector3.UnitY, input.RotA);
            Vector3 axesA2 = Vector3.Transform(Vector3.UnitZ, input.RotA);
            Vector3 axesB0 = Vector3.Transform(Vector3.UnitX, input.RotB);
            Vector3 axesB1 = Vector3.Transform(Vector3.UnitY, input.RotB);
            Vector3 axesB2 = Vector3.Transform(Vector3.UnitZ, input.RotB);

            Vector3[] axes = new Vector3[15];
            axes[0] = axesA0; axes[1] = axesA1; axes[2] = axesA2;
            axes[3] = axesB0; axes[4] = axesB1; axes[5] = axesB2;
            axes[6] = Vector3.Cross(axesA0, axesB0);
            axes[7] = Vector3.Cross(axesA0, axesB1);
            axes[8] = Vector3.Cross(axesA0, axesB2);
            axes[9] = Vector3.Cross(axesA1, axesB0);
            axes[10] = Vector3.Cross(axesA1, axesB1);
            axes[11] = Vector3.Cross(axesA1, axesB2);
            axes[12] = Vector3.Cross(axesA2, axesB0);
            axes[13] = Vector3.Cross(axesA2, axesB1);
            axes[14] = Vector3.Cross(axesA2, axesB2);

            Vector3 centerA = input.PosA + Vector3.Transform(boxA.LocalCenter, input.RotA);
            Vector3 centerB = input.PosB + Vector3.Transform(boxB.LocalCenter, input.RotB);
            Vector3 d = centerB - centerA;

            float minPenetration = float.MaxValue;
            Vector3 minAxis = Vector3.UnitY;

            for (int i = 0; i < 15; i++)
            {
                Vector3 axis = axes[i];
                if (axis.LengthSquared() < 1e-6f) continue;
                axis = Vector3.Normalize(axis);

                // A在轴上的投影半径
                float rA = boxA.HalfExtents.X * MathF.Abs(Vector3.Dot(axesA0, axis)) +
                           boxA.HalfExtents.Y * MathF.Abs(Vector3.Dot(axesA1, axis)) +
                           boxA.HalfExtents.Z * MathF.Abs(Vector3.Dot(axesA2, axis));
                // B在轴上的投影半径
                float rB = boxB.HalfExtents.X * MathF.Abs(Vector3.Dot(axesB0, axis)) +
                           boxB.HalfExtents.Y * MathF.Abs(Vector3.Dot(axesB1, axis)) +
                           boxB.HalfExtents.Z * MathF.Abs(Vector3.Dot(axesB2, axis));

                float dist = MathF.Abs(Vector3.Dot(d, axis));
                float penetration = rA + rB - dist;

                if (penetration < 0)
                {
                    // 找到分离轴，无碰撞
                    output.HasCollision = false;
                    output.Distance = -penetration;
                    output.Normal = axis;
                    output.ContactCount = 0;
                    return;
                }

                if (penetration < minPenetration)
                {
                    minPenetration = penetration;
                    minAxis = Vector3.Dot(d, axis) < 0 ? -axis : axis;
                }
            }

            // 所有轴都有重叠 = 碰撞
            output.HasCollision = true;
            output.Distance = -minPenetration;
            output.Normal = minAxis;
            output.ContactCount = 1;
            output.Contacts = new Vector4[1];
            output.Contacts[0] = new Vector4(
                centerA + minAxis * (MathF.Abs(Vector3.Dot(d, minAxis)) - minPenetration * 0.5f),
                minPenetration);
            output.ClosestPointA = centerA + minAxis * boxA.HalfExtents.Length();
            output.ClosestPointB = centerB - minAxis * boxB.HalfExtents.Length();
        }

        /// <summary>胶囊-胶囊碰撞。</summary>
        public static void CapsuleCapsule(ref CollisionQueryInput input, ref CollisionQueryOutput output)
        {
            var capA = (CapsuleShape)input.ShapeA;
            var capB = (CapsuleShape)input.ShapeB;

            Vector3 axisA = Vector3.Transform(Vector3.UnitY, input.RotA);
            Vector3 axisB = Vector3.Transform(Vector3.UnitY, input.RotB);
            Vector3 centerA = input.PosA + Vector3.Transform(capA.LocalCenter, input.RotA);
            Vector3 centerB = input.PosB + Vector3.Transform(capB.LocalCenter, input.RotB);

            Vector3 pA1 = centerA - axisA * capA.HalfHeight;
            Vector3 pA2 = centerA + axisA * capA.HalfHeight;
            Vector3 pB1 = centerB - axisB * capB.HalfHeight;
            Vector3 pB2 = centerB + axisB * capB.HalfHeight;

            // 线段-线段最近点
            ClosestPointsSegmentSegment(pA1, pA2, pB1, pB2,
                out var cA, out var cB, out float segDist);

            Vector3 diff = cB - cA;
            float distSq = diff.LengthSquared();
            float radius = capA.Radius + capB.Radius;

            output.Contacts = new Vector4[1];

            if (distSq < radius * radius)
            {
                float dist = MathF.Sqrt(distSq);
                output.HasCollision = true;
                output.Distance = dist - radius;
                output.Normal = dist > 1e-6f ? diff / dist : Vector3.UnitY;
                output.ClosestPointA = cA;
                output.ClosestPointB = cB;
                output.ContactCount = 1;
                output.Contacts[0] = new Vector4(
                    (cA + cB) * 0.5f,
                    -output.Distance);
            }
            else
            {
                float dist = MathF.Sqrt(distSq);
                output.HasCollision = false;
                output.Distance = dist - radius;
                output.Normal = dist > 1e-6f ? diff / dist : Vector3.UnitY;
                output.ClosestPointA = cA;
                output.ClosestPointB = cB;
                output.ContactCount = 0;
            }
        }

        /// <summary>盒-胶囊碰撞(简化: 把胶囊当线段处理)。</summary>
        public static void BoxCapsule(ref CollisionQueryInput input, ref CollisionQueryOutput output)
        {
            var box = (BoxShape)input.ShapeA;
            var cap = (CapsuleShape)input.ShapeB;

            Vector3 capAxis = Vector3.Transform(Vector3.UnitY, input.RotB);
            Vector3 capCenter = input.PosB + Vector3.Transform(cap.LocalCenter, input.RotB);
            Vector3 capP0 = capCenter - capAxis * cap.HalfHeight;
            Vector3 capP1 = capCenter + capAxis * cap.HalfHeight;

            // 把胶囊线段转到box局部系
            Quaternion invRotA = Quaternion.Inverse(input.RotA);
            Vector3 localP0 = Vector3.Transform(capP0 - input.PosA, invRotA);
            Vector3 localP1 = Vector3.Transform(capP1 - input.PosA, invRotA);

            // 采样线段上的点，找最近
            int samples = 16;
            float minDist = float.MaxValue;
            Vector3 bestLocalPoint = Vector3.Zero;

            for (int i = 0; i <= samples; i++)
            {
                float t = (float)i / samples;
                Vector3 p = Vector3.Lerp(localP0, localP1, t);
                Vector3 closest = new(
                    Math.Clamp(p.X, -box.HalfExtents.X, box.HalfExtents.X),
                    Math.Clamp(p.Y, -box.HalfExtents.Y, box.HalfExtents.Y),
                    Math.Clamp(p.Z, -box.HalfExtents.Z, box.HalfExtents.Z));
                float d = Vector3.DistanceSquared(p, closest);
                if (d < minDist)
                {
                    minDist = d;
                    bestLocalPoint = closest;
                }
            }

            float dist = MathF.Sqrt(minDist);
            float radius = cap.Radius;
            // 加上box的"半径"(简化)
            // 实际上box closest point已经算了，这里直接用dist

            output.Contacts = new Vector4[1];

            if (dist < radius)
            {
                Vector3 worldClosest = input.PosA + Vector3.Transform(bestLocalPoint, input.RotA);
                // 找线段上最近点
                Vector3 capClosest = ClosestPointOnSegment(worldClosest, capP0, capP1);
                Vector3 diff = capClosest - worldClosest;
                float actualDist = diff.Length();

                output.HasCollision = true;
                output.Distance = actualDist - radius;
                output.Normal = actualDist > 1e-6f ? diff / actualDist : Vector3.UnitY;
                output.ClosestPointA = worldClosest;
                output.ClosestPointB = capClosest;
                output.ContactCount = 1;
                output.Contacts[0] = new Vector4(
                    (worldClosest + capClosest) * 0.5f,
                    -output.Distance);
            }
            else
            {
                Vector3 worldClosest = input.PosA + Vector3.Transform(bestLocalPoint, input.RotA);
                Vector3 capClosest = ClosestPointOnSegment(worldClosest, capP0, capP1);
                Vector3 diff = capClosest - worldClosest;
                float actualDist = diff.Length();

                output.HasCollision = false;
                output.Distance = actualDist - radius;
                output.Normal = actualDist > 1e-6f ? diff / actualDist : Vector3.UnitY;
                output.ClosestPointA = worldClosest;
                output.ClosestPointB = capClosest;
                output.ContactCount = 0;
            }
        }

        /// <summary>凸包-凸包碰撞(GJK+EPA)。</summary>
        public static void ConvexConvexGjk(ref CollisionQueryInput input, ref CollisionQueryOutput output)
        {
            // GJK算法
            Vector3 initDir = input.PosB - input.PosA;
            if (initDir == Vector3.Zero) initDir = Vector3.UnitX;

            // 支持点函数
            Vector3 SupportA(Vector3 d) => input.ShapeA.GetSupportPoint(d, input.PosA, input.RotA);
            Vector3 SupportB(Vector3 d) => input.ShapeB.GetSupportPoint(d, input.PosB, input.RotB);
            Vector3 Support(Vector3 d) => SupportA(d) - SupportB(-d);

            // 简化GJK: 不完整实现，用迭代
            Vector3[] simplex = new Vector3[4];
            int simplexCount = 0;

            simplex[0] = Support(initDir);
            simplexCount = 1;

            Vector3 direction = -simplex[0];

            for (int iter = 0; iter < 32; iter++)
            {
                Vector3 point = Support(direction);
                if (Vector3.Dot(point, direction) < 0)
                {
                    // 没有碰撞
                    output.HasCollision = false;
                    output.Distance = point.Length();
                    output.Normal = direction;
                    output.ContactCount = 0;
                    return;
                }

                simplex[simplexCount++] = point;

                // 简化: 只处理线段和三角形的情况
                if (simplexCount == 2)
                {
                    DoSimplexLine(simplex, ref simplexCount, ref direction);
                }
                else if (simplexCount == 3)
                {
                    DoSimplexTriangle(simplex, ref simplexCount, ref direction);
                }
                else if (simplexCount == 4)
                {
                    // 包含原点 = 碰撞
                    output.HasCollision = true;
                    output.Distance = -0.01f; // 简化
                    output.Normal = -direction;
                    output.ContactCount = 1;
                    output.Contacts = new Vector4[1];
                    output.Contacts[0] = new Vector4(input.PosA, 0.01f);
                    return;
                }
            }

            // 达到迭代上限
            output.HasCollision = false;
            output.Distance = direction.Length();
        }

        private static void DoSimplexLine(Vector3[] simplex, ref int count, ref Vector3 direction)
        {
            Vector3 a = simplex[1];
            Vector3 b = simplex[0];
            Vector3 ab = b - a;
            Vector3 ao = -a;

            if (Vector3.Dot(ab, ao) > 0)
            {
                direction = Vector3.Cross(Vector3.Cross(ab, ao), ab);
            }
            else
            {
                simplex[0] = a;
                count = 1;
                direction = ao;
            }
        }

        private static void DoSimplexTriangle(Vector3[] simplex, ref int count, ref Vector3 direction)
        {
            Vector3 a = simplex[2];
            Vector3 b = simplex[1];
            Vector3 c = simplex[0];
            Vector3 ab = b - a;
            Vector3 ac = c - a;
            Vector3 ao = -a;

            Vector3 abc = Vector3.Cross(ab, ac);

            if (Vector3.Dot(Vector3.Cross(abc, ac), ao) > 0)
            {
                if (Vector3.Dot(ac, ao) > 0)
                {
                    simplex[1] = a;
                    count = 2;
                    direction = Vector3.Cross(Vector3.Cross(ac, ao), ac);
                }
                else
                {
                    simplex[0] = b;
                    simplex[1] = a;
                    count = 2;
                    DoSimplexLine(simplex, ref count, ref direction);
                }
            }
            else if (Vector3.Dot(Vector3.Cross(ab, abc), ao) > 0)
            {
                simplex[0] = c;
                simplex[1] = a;
                count = 2;
                DoSimplexLine(simplex, ref count, ref direction);
            }
            else
            {
                if (Vector3.Dot(abc, ao) > 0)
                {
                    direction = abc;
                }
                else
                {
                    Vector3 temp = simplex[0];
                    simplex[0] = simplex[1];
                    simplex[1] = temp;
                    direction = -abc;
                }
            }
        }

        /// <summary>Compound形状分派。</summary>
        public static void CompoundDispatch(ref CollisionQueryInput input, ref CollisionQueryOutput output)
        {
            // 简化: 遍历所有子形状
            ICollisionShape compoundShape, otherShape;
            Vector3 compoundPos, otherPos;
            Quaternion compoundRot, otherRot;

            if (input.ShapeA.Type == CollisionShapeType.Compound)
            {
                compoundShape = input.ShapeA;
                otherShape = input.ShapeB;
                compoundPos = input.PosA; compoundRot = input.RotA;
                otherPos = input.PosB; otherRot = input.RotB;
            }
            else
            {
                compoundShape = input.ShapeB;
                otherShape = input.ShapeA;
                compoundPos = input.PosB; compoundRot = input.RotB;
                otherPos = input.PosA; otherRot = input.RotA;
            }

            var compound = (CompoundShape)compoundShape;
            output.HasCollision = false;
            output.Distance = float.MaxValue;
            output.Contacts = new Vector4[8];
            int contactCount = 0;

            foreach (var child in compound.Children)
            {
                var childInput = new CollisionQueryInput
                {
                    PosA = compoundPos + Vector3.Transform(child.Offset, compoundRot),
                    RotA = child.Rotation * compoundRot,
                    ShapeA = child.Shape,
                    PosB = otherPos,
                    RotB = otherRot,
                    ShapeB = otherShape,
                    AllowedPenetration = input.AllowedPenetration,
                };

                var childOutput = new CollisionQueryOutput();
                // 递归调用Dispatcher(简化: 直接调对应检测函数)
                // 实际实现要拿到dispatcher实例

                if (childOutput.HasCollision && contactCount < 8)
                {
                    output.HasCollision = true;
                    if (childOutput.Distance < output.Distance)
                    {
                        output.Distance = childOutput.Distance;
                        output.Normal = childOutput.Normal;
                        output.ClosestPointA = childOutput.ClosestPointA;
                        output.ClosestPointB = childOutput.ClosestPointB;
                    }
                    output.Contacts[contactCount++] = childOutput.Contacts[0];
                }
            }

            output.ContactCount = contactCount;
        }

        /// <summary>Mesh分派。</summary>
        public static void MeshDispatch(ref CollisionQueryInput input, ref CollisionQueryOutput output)
        {
            // 简化: 把mesh当凸包处理
            ConvexConvexGjk(ref input, ref output);
        }

        // ========================================================================================
        //  工具函数
        // ========================================================================================

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 ClosestPointOnSegment(Vector3 point, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float t = Vector3.Dot(point - a, ab);
            float denom = Vector3.Dot(ab, ab);
            if (denom < 1e-8f) return a;
            t /= denom;
            t = Math.Clamp(t, 0f, 1f);
            return a + ab * t;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ClosestPointsSegmentSegment(
            Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2,
            out Vector3 c1, out Vector3 c2, out float distance)
        {
            Vector3 d1 = q1 - p1;
            Vector3 d2 = q2 - p2;
            Vector3 r = p1 - p2;
            float a = Vector3.Dot(d1, d1);
            float e = Vector3.Dot(d2, d2);
            float f = Vector3.Dot(d2, r);

            float s, t;
            if (a <= 1e-8f && e <= 1e-8f)
            {
                c1 = p1; c2 = p2;
                distance = Vector3.Distance(c1, c2);
                return;
            }
            if (a <= 1e-8f)
            {
                s = 0; t = Math.Clamp(f / e, 0, 1);
            }
            else
            {
                float c = Vector3.Dot(d1, r);
                if (e <= 1e-8f)
                {
                    t = 0; s = Math.Clamp(-c / a, 0, 1);
                }
                else
                {
                    float b = Vector3.Dot(d1, d2);
                    float denom = a * e - b * b;
                    if (denom != 0)
                        s = Math.Clamp((b * f - c * e) / denom, 0, 1);
                    else
                        s = 0;
                    t = (b * s + f) / e;
                    if (t < 0) { t = 0; s = Math.Clamp(-c / a, 0, 1); }
                    else if (t > 1) { t = 1; s = Math.Clamp((b - c) / a, 0, 1); }
                }
            }
            c1 = p1 + d1 * s;
            c2 = p2 + d2 * t;
            distance = Vector3.Distance(c1, c2);
        }

        /// <summary>清除缓存。</summary>
        public void ClearCache()
        {
            _cache.Clear();
            _cacheHits = 0;
            _cacheMisses = 0;
        }

        /// <summary>获取统计。</summary>
        public void GetStats(out int dispatches, out int cacheHits, out int cacheMisses,
            out int registeredDetectors)
        {
            dispatches = _totalDispatches;
            cacheHits = _cacheHits;
            cacheMisses = _cacheMisses;
            registeredDetectors = _detectors.Count;
        }
    }

    // ============================================================================================
    //  形状实现(简化的基本形状，供Dispatcher用)
    // ============================================================================================

    /// <summary>球形状。</summary>
    public sealed class SphereShape : ICollisionShape
    {
        public float Radius;
        public Vector3 LocalCenter = Vector3.Zero;

        public CollisionShapeType Type => CollisionShapeType.Sphere;
        Vector3 ICollisionShape.LocalCenter => LocalCenter;
        public float Volume => (4f / 3f) * MathF.PI * Radius * Radius * Radius;

        public SphereShape(float radius) { Radius = radius; }

        public void GetAABB(Vector3 position, Quaternion rotation, out Vector3 min, out Vector3 max)
        {
            Vector3 center = position + Vector3.Transform(LocalCenter, rotation);
            min = center - new Vector3(Radius);
            max = center + new Vector3(Radius);
        }

        public Vector3 GetSupportPoint(Vector3 direction, Vector3 position, Quaternion rotation)
        {
            Vector3 center = position + Vector3.Transform(LocalCenter, rotation);
            if (direction.LengthSquared() < 1e-8f) return center;
            return center + Vector3.Normalize(direction) * Radius;
        }

        public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance,
            Vector3 position, Quaternion rotation, out float t, out Vector3 normal)
        {
            Vector3 center = position + Vector3.Transform(LocalCenter, rotation);
            Vector3 oc = origin - center;
            float a = Vector3.Dot(direction, direction);
            float b = 2f * Vector3.Dot(oc, direction);
            float c = Vector3.Dot(oc, oc) - Radius * Radius;
            float disc = b * b - 4 * a * c;
            if (disc < 0) { t = 0; normal = Vector3.Zero; return false; }
            float sq = MathF.Sqrt(disc);
            float t0 = (-b - sq) / (2 * a);
            float t1 = (-b + sq) / (2 * a);
            if (t0 >= 0 && t0 <= maxDistance) { t = t0; normal = Vector3.Normalize((origin + direction * t) - center); return true; }
            if (t1 >= 0 && t1 <= maxDistance) { t = t1; normal = Vector3.Normalize((origin + direction * t) - center); return true; }
            t = 0; normal = Vector3.Zero; return false;
        }
    }

    /// <summary>盒形状。</summary>
    public sealed class BoxShape : ICollisionShape
    {
        public Vector3 HalfExtents;
        public Vector3 LocalCenter = Vector3.Zero;

        public CollisionShapeType Type => CollisionShapeType.Box;
        Vector3 ICollisionShape.LocalCenter => LocalCenter;
        public float Volume => 8f * HalfExtents.X * HalfExtents.Y * HalfExtents.Z;

        public BoxShape(Vector3 halfExtents) { HalfExtents = halfExtents; }
        public BoxShape(float x, float y, float z) { HalfExtents = new Vector3(x, y, z); }

        public void GetAABB(Vector3 position, Quaternion rotation, out Vector3 min, out Vector3 max)
        {
            Vector3 center = position + Vector3.Transform(LocalCenter, rotation);
            // 旋转后的AABB: 取8个角点
            Vector3[] corners = new Vector3[8];
            int i = 0;
            for (int x = -1; x <= 1; x += 2)
                for (int y = -1; y <= 1; y += 2)
                    for (int z = -1; z <= 1; z += 2)
                        corners[i++] = center + Vector3.Transform(
                            new Vector3(HalfExtents.X * x, HalfExtents.Y * y, HalfExtents.Z * z), rotation);

            min = corners[0]; max = corners[0];
            for (int j = 1; j < 8; j++)
            {
                min = Vector3.Min(min, corners[j]);
                max = Vector3.Max(max, corners[j]);
            }
        }

        public Vector3 GetSupportPoint(Vector3 direction, Vector3 position, Quaternion rotation)
        {
            Vector3 center = position + Vector3.Transform(LocalCenter, rotation);
            Vector3 localDir = Vector3.Transform(direction, Quaternion.Inverse(rotation));
            Vector3 localSupport = new(
                Math.Sign(localDir.X) * HalfExtents.X,
                Math.Sign(localDir.Y) * HalfExtents.Y,
                Math.Sign(localDir.Z) * HalfExtents.Z);
            return center + Vector3.Transform(localSupport, rotation);
        }

        public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance,
            Vector3 position, Quaternion rotation, out float t, out Vector3 normal)
        {
            // 简化: slab method
            Vector3 localOrigin = Vector3.Transform(origin - position, Quaternion.Inverse(rotation));
            Vector3 localDir = Vector3.Transform(direction, Quaternion.Inverse(rotation));

            float tmin = -float.MaxValue;
            float tmax = float.MaxValue;
            Vector3 hitNormal = Vector3.Zero;

            for (int i = 0; i < 3; i++)
            {
                float o = i == 0 ? localOrigin.X : i == 1 ? localOrigin.Y : localOrigin.Z;
                float d = i == 0 ? localDir.X : i == 1 ? localDir.Y : localDir.Z;
                float h = i == 0 ? HalfExtents.X : i == 1 ? HalfExtents.Y : HalfExtents.Z;

                if (MathF.Abs(d) < 1e-8f)
                {
                    if (MathF.Abs(o) > h) { t = 0; normal = Vector3.Zero; return false; }
                    continue;
                }
                float t1 = (-h - o) / d;
                float t2 = (h - o) / d;
                Vector3 n = i == 0 ? -Vector3.UnitX : i == 1 ? -Vector3.UnitY : -Vector3.UnitZ;
                if (t1 > t2) { (t1, t2) = (t2, t1); n = -n; }
                if (t1 > tmin) { tmin = t1; hitNormal = n; }
                if (t2 < tmax) tmax = t2;
                if (tmin > tmax) { t = 0; normal = Vector3.Zero; return false; }
            }

            if (tmin >= 0 && tmin <= maxDistance)
            {
                t = tmin;
                normal = Vector3.Transform(hitNormal, rotation);
                return true;
            }
            t = 0; normal = Vector3.Zero; return false;
        }
    }

    /// <summary>胶囊形状。</summary>
    public sealed class CapsuleShape : ICollisionShape
    {
        public float Radius;
        public float HalfHeight;  // 圆柱部分的一半高度(不含半球)
        public Vector3 LocalCenter = Vector3.Zero;

        public CollisionShapeType Type => CollisionShapeType.Capsule;
        Vector3 ICollisionShape.LocalCenter => LocalCenter;
        public float Volume =>
            MathF.PI * Radius * Radius * (2f * HalfHeight) +
            (4f / 3f) * MathF.PI * Radius * Radius * Radius;

        public CapsuleShape(float radius, float height)
        {
            Radius = radius;
            HalfHeight = height * 0.5f;
        }

        public void GetAABB(Vector3 position, Quaternion rotation, out Vector3 min, out Vector3 max)
        {
            Vector3 center = position + Vector3.Transform(LocalCenter, rotation);
            Vector3 axis = Vector3.Transform(Vector3.UnitY, rotation);
            Vector3 extent = new(Radius, MathF.Abs(axis.Y) * HalfHeight + Radius, Radius);
            // 简化: 只考虑主轴
            min = center - new Vector3(
                HalfHeight + Radius, HalfHeight + Radius, HalfHeight + Radius);
            max = center + new Vector3(
                HalfHeight + Radius, HalfHeight + Radius, HalfHeight + Radius);
        }

        public Vector3 GetSupportPoint(Vector3 direction, Vector3 position, Quaternion rotation)
        {
            Vector3 center = position + Vector3.Transform(LocalCenter, rotation);
            Vector3 axis = Vector3.Transform(Vector3.UnitY, rotation);
            Vector3 dirNorm = direction.LengthSquared() > 1e-8f ? Vector3.Normalize(direction) : direction;

            // 选择上端点或下端点
            float dot = Vector3.Dot(dirNorm, axis);
            Vector3 endPoint = dot > 0 ? axis * HalfHeight : -axis * HalfHeight;
            return center + endPoint + dirNorm * Radius;
        }

        public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance,
            Vector3 position, Quaternion rotation, out float t, out Vector3 normal)
        {
            // 简化: 用两个球+cylinder
            t = 0; normal = Vector3.Zero;
            return false;
        }
    }

    /// <summary>平面形状(无限大)。</summary>
    public sealed class PlaneShape : ICollisionShape
    {
        public Vector3 Normal = Vector3.UnitY;
        public Vector3 LocalCenter = Vector3.Zero;

        public CollisionShapeType Type => CollisionShapeType.Plane;
        Vector3 ICollisionShape.LocalCenter => LocalCenter;
        public float Volume => 0;

        public PlaneShape(Vector3 normal) { Normal = Vector3.Normalize(normal); }
        public PlaneShape(float a, float b, float c) { Normal = Vector3.Normalize(new Vector3(a, b, c)); }

        public void GetAABB(Vector3 position, Quaternion rotation, out Vector3 min, out Vector3 max)
        {
            min = new Vector3(float.MinValue);
            max = new Vector3(float.MaxValue);
        }

        public Vector3 GetSupportPoint(Vector3 direction, Vector3 position, Quaternion rotation)
        {
            return position + Vector3.Transform(Normal, rotation) * 1000f; // 无限远
        }

        public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance,
            Vector3 position, Quaternion rotation, out float t, out Vector3 normal)
        {
            Vector3 n = Vector3.Transform(Normal, rotation);
            float denom = Vector3.Dot(n, direction);
            if (MathF.Abs(denom) < 1e-6f) { t = 0; normal = Vector3.Zero; return false; }
            float tt = Vector3.Dot(position - origin, n) / denom;
            if (tt >= 0 && tt <= maxDistance) { t = tt; normal = n; return true; }
            t = 0; normal = Vector3.Zero; return false;
        }
    }

    /// <summary>凸包形状。</summary>
    public sealed class ConvexHullShape : ICollisionShape
    {
        public Vector3[] Vertices;
        public Vector3 LocalCenter = Vector3.Zero;

        public CollisionShapeType Type => CollisionShapeType.ConvexHull;
        Vector3 ICollisionShape.LocalCenter => LocalCenter;
        public float Volume { get; private set; }

        public ConvexHullShape(Vector3[] vertices)
        {
            Vertices = vertices;
            ComputeCenter();
            ComputeVolume();
        }

        private void ComputeCenter()
        {
            LocalCenter = Vector3.Zero;
            foreach (var v in Vertices) LocalCenter += v;
            LocalCenter /= Vertices.Length;
        }

        private void ComputeVolume()
        {
            // 简化: 用AABB近似
            Vector3 min = Vertices[0], max = Vertices[0];
            foreach (var v in Vertices)
            {
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }
            Volume = (max.X - min.X) * (max.Y - min.Y) * (max.Z - min.Z);
        }

        public void GetAABB(Vector3 position, Quaternion rotation, out Vector3 min, out Vector3 max)
        {
            min = new Vector3(float.MaxValue);
            max = new Vector3(float.MinValue);
            foreach (var v in Vertices)
            {
                Vector3 worldV = position + Vector3.Transform(v, rotation);
                min = Vector3.Min(min, worldV);
                max = Vector3.Max(max, worldV);
            }
        }

        public Vector3 GetSupportPoint(Vector3 direction, Vector3 position, Quaternion rotation)
        {
            Vector3 localDir = Vector3.Transform(direction, Quaternion.Inverse(rotation));
            Vector3 best = Vertices[0];
            float bestDot = Vector3.Dot(best, localDir);
            for (int i = 1; i < Vertices.Length; i++)
            {
                float dot = Vector3.Dot(Vertices[i], localDir);
                if (dot > bestDot)
                {
                    bestDot = dot;
                    best = Vertices[i];
                }
            }
            return position + Vector3.Transform(best, rotation);
        }

        public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance,
            Vector3 position, Quaternion rotation, out float t, out Vector3 normal)
        {
            // 简化: 遍历三角形
            t = 0; normal = Vector3.Zero;
            return false;
        }
    }

    /// <summary>网格形状(凹形)。</summary>
    public sealed class MeshShape : ICollisionShape
    {
        public Vector3[] Vertices;
        public int[] Indices;
        public Vector3 LocalCenter = Vector3.Zero;
        public Vector3 AABBHalfExtents;

        public CollisionShapeType Type => CollisionShapeType.Mesh;
        Vector3 ICollisionShape.LocalCenter => LocalCenter;
        public float Volume => 0;

        public MeshShape(Vector3[] vertices, int[] indices)
        {
            Vertices = vertices;
            Indices = indices;
            ComputeAABB();
        }

        private void ComputeAABB()
        {
            Vector3 min = Vertices[0], max = Vertices[0];
            foreach (var v in Vertices)
            {
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }
            LocalCenter = (min + max) * 0.5f;
            AABBHalfExtents = (max - min) * 0.5f;
        }

        public void GetAABB(Vector3 position, Quaternion rotation, out Vector3 min, out Vector3 max)
        {
            // 用AABBHalfExtents旋转后取最大
            Vector3 center = position + Vector3.Transform(LocalCenter, rotation);
            Vector3[] corners = new Vector3[8];
            for (int i = 0; i < 8; i++)
            {
                int x = (i & 1) == 0 ? -1 : 1;
                int y = (i & 2) == 0 ? -1 : 1;
                int z = (i & 4) == 0 ? -1 : 1;
                corners[i] = center + Vector3.Transform(
                    new Vector3(AABBHalfExtents.X * x, AABBHalfExtents.Y * y, AABBHalfExtents.Z * z), rotation);
            }
            min = corners[0]; max = corners[0];
            for (int i = 1; i < 8; i++)
            {
                min = Vector3.Min(min, corners[i]);
                max = Vector3.Max(max, corners[i]);
            }
        }

        public Vector3 GetSupportPoint(Vector3 direction, Vector3 position, Quaternion rotation)
        {
            Vector3 localDir = Vector3.Transform(direction, Quaternion.Inverse(rotation));
            Vector3 best = Vertices[0];
            float bestDot = Vector3.Dot(best, localDir);
            for (int i = 1; i < Vertices.Length; i++)
            {
                float dot = Vector3.Dot(Vertices[i], localDir);
                if (dot > bestDot) { bestDot = dot; best = Vertices[i]; }
            }
            return position + Vector3.Transform(best, rotation);
        }

        public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance,
            Vector3 position, Quaternion rotation, out float t, out Vector3 normal)
        {
            // 遍历三角形做raycast
            float bestT = float.MaxValue;
            Vector3 bestNormal = Vector3.Zero;
            bool hit = false;

            Quaternion invRot = Quaternion.Inverse(rotation);
            Vector3 localOrigin = Vector3.Transform(origin - position, invRot);
            Vector3 localDir = Vector3.Transform(direction, invRot);

            for (int i = 0; i < Indices.Length; i += 3)
            {
                Vector3 v0 = Vertices[Indices[i]];
                Vector3 v1 = Vertices[Indices[i + 1]];
                Vector3 v2 = Vertices[Indices[i + 2]];

                if (RayTriangle(localOrigin, localDir, v0, v1, v2, out float tt))
                {
                    if (tt < bestT && tt <= maxDistance)
                    {
                        bestT = tt;
                        bestNormal = Vector3.Normalize(Vector3.Cross(v1 - v0, v2 - v0));
                        hit = true;
                    }
                }
            }

            if (hit)
            {
                t = bestT;
                normal = Vector3.Transform(bestNormal, rotation);
                return true;
            }
            t = 0; normal = Vector3.Zero;
            return false;
        }

        private static bool RayTriangle(Vector3 origin, Vector3 dir,
            Vector3 v0, Vector3 v1, Vector3 v2, out float t)
        {
            t = 0;
            Vector3 edge1 = v1 - v0;
            Vector3 edge2 = v2 - v0;
            Vector3 h = Vector3.Cross(dir, edge2);
            float a = Vector3.Dot(edge1, h);
            if (MathF.Abs(a) < 1e-8f) return false;
            float f = 1f / a;
            Vector3 s = origin - v0;
            float u = f * Vector3.Dot(s, h);
            if (u < 0 || u > 1) return false;
            Vector3 q = Vector3.Cross(s, edge1);
            float v = f * Vector3.Dot(dir, q);
            if (v < 0 || u + v > 1) return false;
            t = f * Vector3.Dot(edge2, q);
            return t > 0;
        }
    }

    /// <summary>复合形状。</summary>
    public sealed class CompoundShape : ICollisionShape
    {
        public struct ChildShape
        {
            public ICollisionShape Shape;
            public Vector3 Offset;
            public Quaternion Rotation;
        }

        public List<ChildShape> Children = new();
        public Vector3 LocalCenter = Vector3.Zero;

        public CollisionShapeType Type => CollisionShapeType.Compound;
        Vector3 ICollisionShape.LocalCenter => LocalCenter;
        public float Volume
        {
            get { float v = 0; foreach (var c in Children) v += c.Shape.Volume; return v; }
        }

        public void AddChild(ICollisionShape shape, Vector3 offset, Quaternion? rotation = null)
        {
            Children.Add(new ChildShape
            {
                Shape = shape,
                Offset = offset,
                Rotation = rotation ?? Quaternion.Identity,
            });
            RecomputeCenter();
        }

        private void RecomputeCenter()
        {
            LocalCenter = Vector3.Zero;
            float totalVol = 0;
            foreach (var c in Children)
            {
                float v = c.Shape.Volume;
                LocalCenter += (c.Offset + c.Shape.LocalCenter) * v;
                totalVol += v;
            }
            if (totalVol > 0) LocalCenter /= totalVol;
        }

        public void GetAABB(Vector3 position, Quaternion rotation, out Vector3 min, out Vector3 max)
        {
            min = new Vector3(float.MaxValue);
            max = new Vector3(float.MinValue);
            foreach (var c in Children)
            {
                Vector3 childPos = position + Vector3.Transform(c.Offset, rotation);
                Quaternion childRot = c.Rotation * rotation;
                c.Shape.GetAABB(childPos, childRot, out var cmin, out var cmax);
                min = Vector3.Min(min, cmin);
                max = Vector3.Max(max, cmax);
            }
        }

        public Vector3 GetSupportPoint(Vector3 direction, Vector3 position, Quaternion rotation)
        {
            Vector3 best = new(float.MinValue);
            bool first = true;
            foreach (var c in Children)
            {
                Vector3 childPos = position + Vector3.Transform(c.Offset, rotation);
                Quaternion childRot = c.Rotation * rotation;
                Vector3 sp = c.Shape.GetSupportPoint(direction, childPos, childRot);
                if (first || Vector3.Dot(sp, direction) > Vector3.Dot(best, direction))
                {
                    best = sp;
                    first = false;
                }
            }
            return best;
        }

        public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance,
            Vector3 position, Quaternion rotation, out float t, out Vector3 normal)
        {
            float bestT = float.MaxValue;
            Vector3 bestNormal = Vector3.Zero;
            bool hit = false;
            foreach (var c in Children)
            {
                Vector3 childPos = position + Vector3.Transform(c.Offset, rotation);
                Quaternion childRot = c.Rotation * rotation;
                if (c.Shape.Raycast(origin, direction, maxDistance, childPos, childRot, out float tt, out var n))
                {
                    if (tt < bestT) { bestT = tt; bestNormal = n; hit = true; }
                }
            }
            t = bestT; normal = bestNormal;
            return hit;
        }
    }

    /// <summary>高度场形状(地形)。</summary>
    public sealed class HeightfieldShape : ICollisionShape
    {
        public float[,] Heights;
        public float CellSize;
        public float HeightScale = 1f;
        public Vector3 LocalCenter = Vector3.Zero;
        public Vector3 Origin;

        public CollisionShapeType Type => CollisionShapeType.Heightfield;
        Vector3 ICollisionShape.LocalCenter => LocalCenter;
        public float Volume => 0;

        public HeightfieldShape(float[,] heights, float cellSize, Vector3 origin)
        {
            Heights = heights;
            CellSize = cellSize;
            Origin = origin;
            ComputeCenter();
        }

        private void ComputeCenter()
        {
            int w = Heights.GetLength(0);
            int h = Heights.GetLength(1);
            LocalCenter = new Vector3(w * CellSize * 0.5f, 0, h * CellSize * 0.5f);
        }

        public void GetAABB(Vector3 position, Quaternion rotation, out Vector3 min, out Vector3 max)
        {
            int w = Heights.GetLength(0);
            int h = Heights.GetLength(1);
            float minHeight = float.MaxValue, maxHeight = float.MinValue;
            for (int x = 0; x < w; x++)
                for (int z = 0; z < h; z++)
                {
                    float ht = Heights[x, z] * HeightScale;
                    if (ht < minHeight) minHeight = ht;
                    if (ht > maxHeight) maxHeight = ht;
                }
            min = position + Origin + new Vector3(0, minHeight, 0);
            max = position + Origin + new Vector3(w * CellSize, maxHeight, h * CellSize);
        }

        public Vector3 GetSupportPoint(Vector3 direction, Vector3 position, Quaternion rotation)
        {
            // 简化: 返回最高点
            int w = Heights.GetLength(0);
            int h = Heights.GetLength(1);
            float maxH = float.MinValue;
            int bx = 0, bz = 0;
            for (int x = 0; x < w; x++)
                for (int z = 0; z < h; z++)
                {
                    if (Heights[x, z] > maxH) { maxH = Heights[x, z]; bx = x; bz = z; }
                }
            return position + Origin + new Vector3(bx * CellSize, maxH * HeightScale, bz * CellSize);
        }

        public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance,
            Vector3 position, Quaternion rotation, out float t, out Vector3 normal)
        {
            // 简化: 把射线转到局部系
            Vector3 localOrigin = origin - position - Origin;
            // 遍历网格找交点
            int w = Heights.GetLength(0);
            int h = Heights.GetLength(1);

            float bestT = float.MaxValue;
            Vector3 bestN = Vector3.UnitY;
            bool hit = false;

            for (int x = 0; x < w - 1; x++)
            {
                for (int z = 0; z < h - 1; z++)
                {
                    Vector3 v00 = new(x * CellSize, Heights[x, z] * HeightScale, z * CellSize);
                    Vector3 v10 = new((x + 1) * CellSize, Heights[x + 1, z] * HeightScale, z * CellSize);
                    Vector3 v01 = new(x * CellSize, Heights[x, z + 1] * HeightScale, (z + 1) * CellSize);
                    Vector3 v11 = new((x + 1) * CellSize, Heights[x + 1, z + 1] * HeightScale, (z + 1) * CellSize);

                    // 两个三角形
                    if (RayTriangleLocal(localOrigin, direction, v00, v10, v11, out float t1))
                    {
                        if (t1 < bestT) { bestT = t1; bestN = Vector3.Normalize(Vector3.Cross(v10 - v00, v11 - v00)); hit = true; }
                    }
                    if (RayTriangleLocal(localOrigin, direction, v00, v11, v01, out float t2))
                    {
                        if (t2 < bestT) { bestT = t2; bestN = Vector3.Normalize(Vector3.Cross(v11 - v00, v01 - v00)); hit = true; }
                    }
                }
            }

            if (hit && bestT <= maxDistance)
            {
                t = bestT;
                normal = Vector3.Transform(bestN, rotation);
                return true;
            }
            t = 0; normal = Vector3.Zero;
            return false;
        }

        private static bool RayTriangleLocal(Vector3 o, Vector3 d, Vector3 v0, Vector3 v1, Vector3 v2, out float t)
        {
            t = 0;
            Vector3 e1 = v1 - v0, e2 = v2 - v0;
            Vector3 h = Vector3.Cross(d, e2);
            float a = Vector3.Dot(e1, h);
            if (MathF.Abs(a) < 1e-8f) return false;
            float f = 1f / a;
            Vector3 s = o - v0;
            float u = f * Vector3.Dot(s, h);
            if (u < 0 || u > 1) return false;
            Vector3 q = Vector3.Cross(s, e1);
            float v = f * Vector3.Dot(d, q);
            if (v < 0 || u + v > 1) return false;
            t = f * Vector3.Dot(e2, q);
            return t > 0;
        }
    }
}
