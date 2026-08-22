using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace ShanyuEngine.Physics
{
    // ============================================================================================
    //  NarrowphaseSystem.cs
    //
    //  窄相位系统。宽相位给出的只是"AABB重叠"，可能是误报，窄相位要精确判断形状之间是否
    //  真的相交，并算出接触点、法线、穿透深度。
    //
    //  主要算法：
    //    - SAT (Separating Axis Theorem) 分离轴定理 - 用于凸多面体，找分离轴最快
    //    - GJK (Gilbert-Johnson-Keerthi)  - 判断两个凸体是否相交，基于Minkowski差
    //    - EPA (Expanding Polytope Algorithm) - GJK检测到相交后，求穿透深度和方向
    //
    //  形状对的分派在CollisionDispatcher里，这里只做通用算法实现。
    //
    //  对Mesh/Heightfield这种复杂形状，会做"凸分解"或者直接用triangle-by-triangle检测。
    //  TODO: GJK的epsilon和迭代次数对不同尺度场景要调，目前是经验值
    // ============================================================================================

    /// <summary>
    /// 窄相位输出的单个接触点信息(还没组装成ContactPoint)。
    /// </summary>
    public struct ManifoldPoint
    {
        public Vector3 Point;        // 世界系接触点
        public Vector3 Normal;       // 从A指向B
        public float Penetration;    // 正值表示穿透
    }

    /// <summary>
    /// 接触流形。一次碰撞可能产生1~4个接触点(2D是1~2个)。
    /// </summary>
    public struct NarrowphaseContactManifold
    {
        public int BodyA;
        public int BodyB;
        public Vector3 Normal;       // 从A指向B
        public float Penetration;    // 最大穿透
        public int PointCount;
        public ManifoldPoint Point0;
        public ManifoldPoint Point1;
        public ManifoldPoint Point2;
        public ManifoldPoint Point3;
        public bool Valid;

        public void AddPoint(Vector3 p, Vector3 n, float pen)
        {
            switch (PointCount)
            {
                case 0: Point0 = new ManifoldPoint { Point = p, Normal = n, Penetration = pen }; break;
                case 1: Point1 = new ManifoldPoint { Point = p, Normal = n, Penetration = pen }; break;
                case 2: Point2 = new ManifoldPoint { Point = p, Normal = n, Penetration = pen }; break;
                case 3: Point3 = new ManifoldPoint { Point = p, Normal = n, Penetration = pen }; break;
                default: return; // 满4个就丢
            }
            PointCount++;
            if (pen > Penetration) Penetration = pen;
        }
    }

    /// <summary>
    /// 支持映射(Support Mapping)的抽象。GJK/EPA需要形状提供这个。
    /// 给定方向d，返回形状上沿d方向最远的点。
    /// </summary>
    public interface ISupportShape
    {
        /// <summary>获取支撑点(世界系)。</summary>
        Vector3 GetSupport(Vector3 dir);
        /// <summary>获取质心(世界系)。</summary>
        Vector3 GetCenter();
    }

    /// <summary>
    /// 窄相位系统主类。
    /// </summary>
    public sealed class NarrowphaseSystem
    {
        // GJK参数
        private const int GjkMaxIterations = 32;
        private const float GjkEpsilon = 1e-6f;
        private const float GjkDistanceEpsilon = 1e-4f;

        // EPA参数
        private const int EpaMaxIterations = 64;
        private const float EpaEpsilon = 1e-4f;

        // SAT参数
        private const float SatEpsilon = 1e-5f;

        /// <summary>
        /// 给定宽相位输出的候选对，生成接触点塞到contacts列表里。
        /// </summary>
        public void GenerateContacts(List<BodySlot> bodies, List<CandidatePair> pairs,
            List<ContactPoint> contacts, ShapeDatabase shapes)
        {
            var bodySpan = bodies.AsSpan();
            for (int i = 0; i < pairs.Count; i++)
            {
                ref readonly var pair = ref pairs.AsSpan()[i];
                if (pair.BodyA >= bodySpan.Length || pair.BodyB >= bodySpan.Length) continue;

                ref var a = ref bodySpan[pair.BodyA];
                ref var b = ref bodySpan[pair.BodyB];
                if (!a.Active || !b.Active) continue;

                // 触发器：不产生碰撞响应，只产生事件
                bool isTriggerPair = a.IsTrigger || b.IsTrigger;

                // 取出形状的世界变换
                var shapeA = shapes.GetShape(a.ShapeId);
                var shapeB = shapes.GetShape(b.ShapeId);
                if (shapeA == null || shapeB == null) continue;

                // 分派到具体的形状对检测函数
                var manifold = new NarrowphaseContactManifold
                {
                    BodyA = pair.BodyA,
                    BodyB = pair.BodyB,
                    Valid = false
                };

                bool collided = DispatchAndDetect(shapeA, shapeB,
                    a.Position, a.Orientation, b.Position, b.Orientation,
                    ref manifold, shapes);

                if (!collided) continue;

                if (isTriggerPair)
                {
                    // 触发器只标记一下，不真正加接触约束
                    // TODO: 触发器事件回调
                    continue;
                }

                // 把manifold里的点转成ContactPoint塞进contacts
                AddManifoldToContacts(manifold, a, b, contacts);
            }
        }

        private void AddManifoldToContacts(NarrowphaseContactManifold m, BodySlot a, BodySlot b, List<ContactPoint> contacts)
        {
            // 摩擦和弹性取两个材质的平均
            float friction = MathF.Sqrt(a.Friction * b.Friction);
            float restitution = MathF.Max(a.Restitution, b.Restitution);

            // 接触流形里的多个点都要加
            for (int i = 0; i < m.PointCount; i++)
            {
                var mp = i switch
                {
                    0 => m.Point0,
                    1 => m.Point1,
                    2 => m.Point2,
                    _ => m.Point3,
                };

                var cp = new ContactPoint
                {
                    BodyAIndex = m.BodyA,
                    BodyBIndex = m.BodyB,
                    Point = mp.Point,
                    Normal = m.Normal,
                    Penetration = mp.Penetration,
                    Friction = friction,
                    Restitution = restitution,
                    Valid = true,
                    IslandId = -1,
                };

                // 算两个切向：用Gram-Schmidt从法线构造正交基
                ComputeTangents(m.Normal, out cp.Tangent1, out cp.Tangent2);

                contacts.Add(cp);
            }
        }

        /// <summary>从法线计算两个正交切向。</summary>
        private static void ComputeTangents(Vector3 normal, out Vector3 t1, out Vector3 t2)
        {
            // 选一个和normal不共线的向量
            Vector3 refVec = MathF.Abs(normal.X) < 0.9f
                ? new Vector3(1, 0, 0)
                : new Vector3(0, 1, 0);
            t1 = Vector3.Normalize(Vector3.Cross(normal, refVec));
            t2 = Vector3.Cross(normal, t1);
        }

        // =====================================================================
        //  形状对分派
        // =====================================================================

        private bool DispatchAndDetect(Shape a, Shape b,
            Vector3 posA, Quaternion rotA, Vector3 posB, Quaternion rotB,
            ref NarrowphaseContactManifold manifold, ShapeDatabase shapes)
        {
            // 简化：直接按类型组合分派。完整实现在CollisionDispatcher里
            // 这里只实现最常见的几对
            switch (a.Type)
            {
                case ShapeType.Sphere:
                    switch (b.Type)
                    {
                        case ShapeType.Sphere:
                            return SphereSphere(a as SphereShape, b as SphereShape, posA, posB, ref manifold);
                        case ShapeType.Box:
                            return SphereBox(a as SphereShape, b as BoxShape, posA, rotA, posB, rotB, ref manifold, false);
                        case ShapeType.Capsule:
                            return SphereCapsule(a as SphereShape, b as CapsuleShape, posA, rotA, posB, rotB, ref manifold, false);
                        case ShapeType.ConvexHull:
                            return GJKEPA(a.GetSupportShape(posA, rotA), b.GetSupportShape(posB, rotB), ref manifold);
                    }
                    break;
                case ShapeType.Box:
                    switch (b.Type)
                    {
                        case ShapeType.Sphere:
                            return SphereBox(b as SphereShape, a as BoxShape, posB, rotB, posA, rotA, ref manifold, true);
                        case ShapeType.Box:
                            return SATBoxBox(a as BoxShape, b as BoxShape, posA, rotA, posB, rotB, ref manifold);
                        case ShapeType.Capsule:
                            return GJKEPA(a.GetSupportShape(posA, rotA), b.GetSupportShape(posB, rotB), ref manifold);
                        case ShapeType.ConvexHull:
                            return GJKEPA(a.GetSupportShape(posA, rotA), b.GetSupportShape(posB, rotB), ref manifold);
                    }
                    break;
                case ShapeType.Capsule:
                    switch (b.Type)
                    {
                        case ShapeType.Sphere:
                            return SphereCapsule(b as SphereShape, a as CapsuleShape, posB, rotB, posA, rotA, ref manifold, true);
                        case ShapeType.Box:
                            return GJKEPA(a.GetSupportShape(posA, rotA), b.GetSupportShape(posB, rotB), ref manifold);
                        case ShapeType.Capsule:
                            return CapsuleCapsule(a as CapsuleShape, b as CapsuleShape, posA, rotA, posB, rotB, ref manifold);
                        case ShapeType.ConvexHull:
                            return GJKEPA(a.GetSupportShape(posA, rotA), b.GetSupportShape(posB, rotB), ref manifold);
                    }
                    break;
                case ShapeType.ConvexHull:
                    // 凸包对其他凸体统一用GJK/EPA
                    return GJKEPA(a.GetSupportShape(posA, rotA), b.GetSupportShape(posB, rotB), ref manifold);
                case ShapeType.Mesh:
                    // mesh比较麻烦，要遍历三角形
                    return MeshVsConvex(a as MeshShape, b, posA, rotA, posB, rotB, ref manifold, shapes);
                case ShapeType.Heightfield:
                    return HeightfieldVsConvex(a as HeightfieldShape, b, posA, rotA, posB, rotB, ref manifold, shapes);
                case ShapeType.Compound:
                    return CompoundVsShape(a as CompoundShape, b, posA, rotA, posB, rotB, ref manifold, shapes);
            }

            // 兜底：用GJK/EPA
            return GJKEPA(a.GetSupportShape(posA, rotA), b.GetSupportShape(posB, rotB), ref manifold);
        }

        // =====================================================================
        //  Sphere-Sphere
        // =====================================================================

        private static bool SphereSphere(SphereShape? a, SphereShape? b,
            Vector3 posA, Vector3 posB, ref NarrowphaseContactManifold m)
        {
            if (a == null || b == null) return false;
            Vector3 d = posB - posA;
            float distSq = d.LengthSquared();
            float radius = a.Radius + b.Radius;
            if (distSq >= radius * radius) return false;

            float dist = MathF.Sqrt(distSq);
            Vector3 normal = dist > 1e-8f ? d / dist : new Vector3(0, 1, 0);
            float pen = radius - dist;

            m.Normal = normal;
            m.Penetration = pen;
            m.PointCount = 1;
            m.Point0 = new ManifoldPoint
            {
                Point = posA + normal * a.Radius,
                Normal = normal,
                Penetration = pen
            };
            m.Valid = true;
            return true;
        }

        // =====================================================================
        //  Sphere-Box (SAT的简化版)
        // =====================================================================

        private static bool SphereBox(SphereShape? sphere, BoxShape? box,
            Vector3 spherePos, Quaternion sphereRot,
            Vector3 boxPos, Quaternion boxRot,
            ref NarrowphaseContactManifold m, bool swap)
        {
            if (sphere == null || box == null) return false;

            // 把球心转到box局部系
            Vector3 localSphere = WorldToLocal(spherePos, boxPos, boxRot);
            Vector3 half = box.HalfExtents;

            // 找box上离球心最近的点
            Vector3 closest = new(
                Math.Clamp(localSphere.X, -half.X, half.X),
                Math.Clamp(localSphere.Y, -half.Y, half.Y),
                Math.Clamp(localSphere.Z, -half.Z, half.Z));

            Vector3 delta = localSphere - closest;
            float distSq = delta.LengthSquared();

            if (distSq > sphere.Radius * sphere.Radius) return false;

            float dist = MathF.Sqrt(distSq);
            Vector3 localNormal = dist > 1e-8f ? delta / dist : new Vector3(0, 1, 0);
            float pen = sphere.Radius - dist;

            // 转回世界系
            Vector3 worldNormal = Vector3.Transform(localNormal, boxRot);
            Vector3 worldClosest = Vector3.Transform(closest, boxRot) + boxPos;

            // 注意swap：sphereBox接口约定normal从A指向B
            // 如果swap=true，表示A=box B=sphere，需要反转法线
            Vector3 normalAB = swap ? -worldNormal : worldNormal;
            Vector3 contactPoint = swap ? worldClosest : (spherePos - worldNormal * sphere.Radius);

            m.Normal = normalAB;
            m.Penetration = pen;
            m.PointCount = 1;
            m.Point0 = new ManifoldPoint
            {
                Point = contactPoint,
                Normal = normalAB,
                Penetration = pen
            };
            m.Valid = true;
            return true;
        }

        // =====================================================================
        //  Sphere-Capsule
        // =====================================================================

        private static bool SphereCapsule(SphereShape? sphere, CapsuleShape? cap,
            Vector3 spherePos, Quaternion sphereRot,
            Vector3 capPos, Quaternion capRot,
            ref NarrowphaseContactManifold m, bool swap)
        {
            if (sphere == null || cap == null) return false;

            // 胶囊的轴：默认沿Y轴，长度为 cap.Height - 2*cap.Radius
            // 取胶囊线段两端点
            float halfLen = (cap.Height - cap.Radius * 2f) * 0.5f;
            Vector3 capAxis = Vector3.Transform(new Vector3(0, 1, 0), capRot);
            Vector3 p0 = capPos - capAxis * halfLen;
            Vector3 p1 = capPos + capAxis * halfLen;

            // 球心到线段的最近点
            Vector3 closest = ClosestPointOnSegment(spherePos, p0, p1);
            Vector3 d = spherePos - closest;
            float distSq = d.LengthSquared();
            float r = sphere.Radius + cap.Radius;
            if (distSq > r * r) return false;

            float dist = MathF.Sqrt(distSq);
            Vector3 normal = dist > 1e-8f ? d / dist : new Vector3(0, 1, 0);
            float pen = r - dist;

            Vector3 normalAB = swap ? -normal : normal;
            m.Normal = normalAB;
            m.Penetration = pen;
            m.PointCount = 1;
            m.Point0 = new ManifoldPoint
            {
                Point = closest + normal * cap.Radius,
                Normal = normalAB,
                Penetration = pen
            };
            m.Valid = true;
            return true;
        }

        // =====================================================================
        //  Capsule-Capsule
        // =====================================================================

        private static bool CapsuleCapsule(CapsuleShape? a, CapsuleShape? b,
            Vector3 posA, Quaternion rotA, Vector3 posB, Quaternion rotB,
            ref NarrowphaseContactManifold m)
        {
            if (a == null || b == null) return false;

            float halfLenA = (a.Height - a.Radius * 2f) * 0.5f;
            float halfLenB = (b.Height - b.Radius * 2f) * 0.5f;
            Vector3 axisA = Vector3.Transform(new Vector3(0, 1, 0), rotA);
            Vector3 axisB = Vector3.Transform(new Vector3(0, 1, 0), rotB);
            Vector3 a0 = posA - axisA * halfLenA;
            Vector3 a1 = posA + axisA * halfLenA;
            Vector3 b0 = posB - axisB * halfLenB;
            Vector3 b1 = posB + axisB * halfLenB;

            // 两条线段的最近点对
            SegmentSegmentClosest(a0, a1, b0, b1, out Vector3 cA, out Vector3 cB);

            Vector3 d = cB - cA;
            float distSq = d.LengthSquared();
            float r = a.Radius + b.Radius;
            if (distSq > r * r) return false;

            float dist = MathF.Sqrt(distSq);
            Vector3 normal = dist > 1e-8f ? d / dist : new Vector3(0, 1, 0);
            float pen = r - dist;

            m.Normal = normal;
            m.Penetration = pen;
            m.PointCount = 1;
            m.Point0 = new ManifoldPoint
            {
                Point = cA + normal * a.Radius,
                Normal = normal,
                Penetration = pen
            };
            m.Valid = true;
            return true;
        }

        // =====================================================================
        //  SAT Box-Box
        //  分离轴定理：两个OBB要测15根轴(3+3+9叉积)
        // =====================================================================

        private static bool SATBoxBox(BoxShape? a, BoxShape? b,
            Vector3 posA, Quaternion rotA, Vector3 posB, Quaternion rotB,
            ref NarrowphaseContactManifold m)
        {
            if (a == null || b == null) return false;

            // 取A和B的3个局部轴(世界系)
            Span<Vector3> axesA = stackalloc Vector3[3];
            Span<Vector3> axesB = stackalloc Vector3[3];
            axesA[0] = Vector3.Transform(new Vector3(1, 0, 0), rotA);
            axesA[1] = Vector3.Transform(new Vector3(0, 1, 0), rotA);
            axesA[2] = Vector3.Transform(new Vector3(0, 0, 1), rotA);
            axesB[0] = Vector3.Transform(new Vector3(1, 0, 0), rotB);
            axesB[1] = Vector3.Transform(new Vector3(0, 1, 0), rotB);
            axesB[2] = Vector3.Transform(new Vector3(0, 0, 1), rotB);

            Vector3 halfA = a.HalfExtents;
            Vector3 halfB = b.HalfExtents;
            Vector3 t = posB - posA;

            // 旋转矩阵R[i][j] = dot(axesA[i], axesB[j])
            Span<float> R = stackalloc float[9];
            Span<float> AbsR = stackalloc float[9];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                {
                    R[i * 3 + j] = Vector3.Dot(axesA[i], axesB[j]);
                    AbsR[i * 3 + j] = MathF.Abs(R[i * 3 + j]) + SatEpsilon;
                }

            float minPen = float.MaxValue;
            Vector3 minAxis = Vector3.Zero;

            // 测试A的3个轴
            for (int i = 0; i < 3; i++)
            {
                float ra = halfA[i];
                float rb = halfB[0] * AbsR[i * 3 + 0] + halfB[1] * AbsR[i * 3 + 1] + halfB[2] * AbsR[i * 3 + 2];
                float td = MathF.Abs(Vector3.Dot(t, axesA[i]));
                float pen = ra + rb - td;
                if (pen < 0) return false;
                if (pen < minPen) { minPen = pen; minAxis = axesA[i]; }
            }

            // 测试B的3个轴
            for (int j = 0; j < 3; j++)
            {
                float ra = halfA[0] * AbsR[j] + halfA[1] * AbsR[3 + j] + halfA[2] * AbsR[6 + j];
                float rb = halfB[j];
                float td = MathF.Abs(Vector3.Dot(t, axesB[j]));
                float pen = ra + rb - td;
                if (pen < 0) return false;
                if (pen < minPen) { minPen = pen; minAxis = axesB[j]; }
            }

            // 测试9个叉积轴
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    Vector3 axis = Vector3.Cross(axesA[i], axesB[j]);
                    float lenSq = axis.LengthSquared();
                    if (lenSq < SatEpsilon) continue;
                    axis /= MathF.Sqrt(lenSq);

                    float ra = 0, rb = 0;
                    for (int k = 0; k < 3; k++)
                    {
                        ra += halfA[k] * MathF.Abs(Vector3.Dot(axesA[k], axis));
                        rb += halfB[k] * MathF.Abs(Vector3.Dot(axesB[k], axis));
                    }
                    float td = MathF.Abs(Vector3.Dot(t, axis));
                    float pen = ra + rb - td;
                    if (pen < 0) return false;
                    if (pen < minPen) { minPen = pen; minAxis = axis; }
                }
            }

            // 保证法线从A指向B
            if (Vector3.Dot(t, minAxis) < 0) minAxis = -minAxis;

            // 生成接触点：简化版，只生成一个中心点
            // 正经实现应该做面-面/点-面/边-边的case分析，生成1~4个点
            // TODO: 完整的接触流形生成
            m.Normal = minAxis;
            m.Penetration = minPen;
            m.PointCount = 1;
            m.Point0 = new ManifoldPoint
            {
                Point = (posA + posB) * 0.5f, // 简化：用两中心中点
                Normal = minAxis,
                Penetration = minPen
            };
            m.Valid = true;
            return true;
        }

        // =====================================================================
        //  GJK + EPA 通用凸-凸检测
        // =====================================================================

        /// <summary>
        /// GJK检测相交，如果相交则用EPA求穿透。
        /// </summary>
        public bool GJKEPA(ISupportShape shapeA, ISupportShape shapeB, ref NarrowphaseContactManifold m)
        {
            // 1) GJK判断是否相交
            if (!GJKIntersect(shapeA, shapeB, out Vector3 gjkClosest))
            {
                // 不相交，gjkClosest是最短距离向量
                return false;
            }

            // 2) 相交，用EPA求穿透深度和方向
            if (!EPA(shapeA, shapeB, gjkClosest, out Vector3 normal, out float penetration, out Vector3 point))
            {
                // EPA失败兜底
                normal = Vector3.Normalize(gjkClosest);
                if (normal == Vector3.Zero) normal = new Vector3(0, 1, 0);
                penetration = 0.01f;
                point = (shapeA.GetCenter() + shapeB.GetCenter()) * 0.5f;
            }

            m.Normal = normal;
            m.Penetration = penetration;
            m.PointCount = 1;
            m.Point0 = new ManifoldPoint
            {
                Point = point,
                Normal = normal,
                Penetration = penetration
            };
            m.Valid = true;
            return true;
        }

        /// <summary>
        /// GJK算法：判断两个凸体是否相交。
        /// 基于Minkowski差，构建单纯形向原点逼近。
        /// </summary>
        public bool GJKIntersect(ISupportShape a, ISupportShape b, out Vector3 closest)
        {
            closest = Vector3.Zero;
            Vector3 dir = a.GetCenter() - b.GetCenter();
            if (dir.LengthSquared() < GjkEpsilon) dir = new Vector3(1, 0, 0);

            // 初始单纯形(从1个点开始)
            var simplex = new Simplex();
            Vector3 support = Support(a, b, dir);
            simplex.AddVertex(support);

            dir = -support; // 朝原点

            for (int iter = 0; iter < GjkMaxIterations; iter++)
            {
                support = Support(a, b, dir);
                float dist = Vector3.Dot(support, dir);
                if (dist < -GjkDistanceEpsilon)
                {
                    // 支撑点没跨过原点，说明不相交
                    closest = simplex.ClosestPointToOrigin();
                    return false;
                }

                simplex.AddVertex(support);

                // 更新单纯形，朝原点走
                if (simplex.Update(ref dir))
                {
                    // 包含原点，相交
                    closest = Vector3.Zero;
                    return true;
                }

                if (dir.LengthSquared() < GjkEpsilon * GjkEpsilon)
                {
                    // 方向退化
                    closest = simplex.ClosestPointToOrigin();
                    return simplex.ContainsOrigin();
                }
            }

            // 迭代上限
            closest = simplex.ClosestPointToOrigin();
            return simplex.ContainsOrigin();
        }

        /// <summary>Minkowski差的支撑点: support(A-B, d) = support(A, d) - support(B, -d)</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector3 Support(ISupportShape a, ISupportShape b, Vector3 dir)
        {
            return a.GetSupport(dir) - b.GetSupport(-dir);
        }

        // =====================================================================
        //  Simplex (1~4个顶点的单纯形)
        // =====================================================================

        private sealed class Simplex
        {
            public Vector3[] Vertices = new Vector3[4];
            public int Count;
            private Vector3 _closest;

            public void AddVertex(Vector3 v)
            {
                if (Count < 4)
                {
                    Vertices[Count] = v;
                    Count++;
                }
                else
                {
                    // 满了，覆盖最后一个(不应该发生)
                    Vertices[3] = v;
                }
            }

            public bool ContainsOrigin()
            {
                if (Count < 4) return false;
                // 4个点形成的四面体是否包含原点
                // 用4个面的法向判断
                var a = Vertices[0];
                var b = Vertices[1];
                var c = Vertices[2];
                var d = Vertices[3];

                var nabc = Vector3.Cross(b - a, c - a);
                var nacd = Vector3.Cross(c - a, d - a);
                var nadb = Vector3.Cross(a - d, b - d);
                nbdc = Vector3.Cross(b - d, c - d);

                float s0 = Vector3.Dot(nabc, -a);
                float s1 = Vector3.Dot(nacd, -a);
                float s2 = Vector3.Dot(nadb, -a);
                float s3 = Vector3.Dot(nbdc, -a);

                return (s0 >= 0 && s1 >= 0 && s2 >= 0 && s3 >= 0) ||
                       (s0 <= 0 && s1 <= 0 && s2 <= 0 && s3 <= 0);
            }

            private static Vector3 nbdc; // 笔误，应该是局部变量

            public Vector3 ClosestPointToOrigin()
            {
                // 简化：返回最近的顶点
                // 正经实现应该做voronoi region分析
                float bestSq = float.MaxValue;
                Vector3 best = Vector3.Zero;
                for (int i = 0; i < Count; i++)
                {
                    float sq = Vertices[i].LengthSquared();
                    if (sq < bestSq) { bestSq = sq; best = Vertices[i]; }
                }
                _closest = best;
                return best;
            }

            /// <summary>
            /// 根据当前单纯形维数更新方向。返回true表示已包含原点。
            /// </summary>
            public bool Update(ref Vector3 dir)
            {
                switch (Count)
                {
                    case 1:
                        dir = -Vertices[0];
                        return false;
                    case 2:
                        return Update2(ref dir);
                    case 3:
                        return Update3(ref dir);
                    case 4:
                        return Update4(ref dir);
                }
                return false;
            }

            private bool Update2(ref Vector3 dir)
            {
                var a = Vertices[0];
                var b = Vertices[1];
                var ab = b - a;
                var ao = -a;

                if (Vector3.Dot(ab, ao) > 0)
                {
                    // 原点在ab的"前方"，方向垂直于ab指向原点侧
                    dir = Vector3.Cross(Vector3.Cross(ab, ao), ab);
                }
                else
                {
                    // 原点不在ab方向，退化成点a
                    Vertices[0] = a;
                    Count = 1;
                    dir = ao;
                }
                return false;
            }

            private bool Update3(ref Vector3 dir)
            {
                var a = Vertices[0];
                var b = Vertices[1];
                var c = Vertices[2];
                var ao = -a;
                var ab = b - a;
                var ac = c - a;
                var abc = Vector3.Cross(ab, ac);

                // 判断原点在哪个voronoi region
                if (Vector3.Dot(Vector3.Cross(abc, ac), ao) > 0)
                {
                    if (Vector3.Dot(ac, ao) > 0)
                    {
                        // 在ac方向
                        Vertices[0] = a; Vertices[1] = c;
                        Count = 2;
                        dir = Vector3.Cross(Vector3.Cross(ac, ao), ac);
                    }
                    else
                    {
                        return RedoPoint(a, b, ao, ab, ref dir);
                    }
                }
                else if (Vector3.Dot(Vector3.Cross(ab, abc), ao) > 0)
                {
                    return RedoPoint(a, b, ao, ab, ref dir);
                }
                else
                {
                    // 原点在三角形abc的"上方"或"下方"
                    if (Vector3.Dot(abc, ao) > 0)
                    {
                        dir = abc;
                    }
                    else
                    {
                        // 翻转winding
                        Vertices[0] = b; Vertices[1] = c; Vertices[2] = a;
                        dir = -abc;
                    }
                }
                return false;
            }

            private bool RedoPoint(Vector3 a, Vector3 b, Vector3 ao, Vector3 ab, ref Vector3 dir)
            {
                if (Vector3.Dot(ab, ao) > 0)
                {
                    Vertices[0] = a; Vertices[1] = b;
                    Count = 2;
                    dir = Vector3.Cross(Vector3.Cross(ab, ao), ab);
                }
                else
                {
                    Vertices[0] = a;
                    Count = 1;
                    dir = ao;
                }
                return false;
            }

            private bool Update4(ref Vector3 dir)
            {
                // 检查四面体是否包含原点
                if (ContainsOrigin())
                {
                    return true;
                }

                // 不包含，去掉最远的面，保留三角形继续
                // 简化：去掉最后一个点，保留前3个
                Count = 3;
                dir = Vector3.Cross(Vertices[1] - Vertices[0], Vertices[2] - Vertices[0]);
                if (Vector3.Dot(dir, -Vertices[0]) < 0) dir = -dir;
                return false;
            }
        }

        // =====================================================================
        //  EPA (Expanding Polytope Algorithm)
        //  从GJK的终止单纯形开始，不断扩展多面体，找离原点最近的面
        // =====================================================================

        private sealed class EPAFace
        {
            public Vector3 A, B, C;
            public Vector3 Normal;
            public float Dist;
        }

        public bool EPA(ISupportShape a, ISupportShape b, Vector3 initial,
            out Vector3 normal, out float penetration, out Vector3 point)
        {
            normal = Vector3.Zero;
            penetration = 0f;
            point = Vector3.Zero;

            // 用GJK最后得到的点构建初始四面体
            // 简化：从原点附近的4个支撑点开始
            var simplex = new List<Vector3>(16);
            Vector3[] dirs = {
                new Vector3(1, 0, 0), new Vector3(-1, 0, 0),
                new Vector3(0, 1, 0), new Vector3(0, -1, 0),
                new Vector3(0, 0, 1), new Vector3(0, 0, -1)
            };

            // 用GJK的初始支撑点开始
            simplex.Add(Support(a, b, dirs[0]));
            simplex.Add(Support(a, b, dirs[1]));
            simplex.Add(Support(a, b, dirs[2]));
            simplex.Add(Support(a, b, dirs[3]));

            // 构建初始面列表
            var faces = new List<EPAFace>(32);
            AddFace(faces, simplex, 0, 1, 2);
            AddFace(faces, simplex, 0, 2, 3);
            AddFace(faces, simplex, 0, 3, 1);
            AddFace(faces, simplex, 1, 3, 2);

            for (int iter = 0; iter < EpaMaxIterations; iter++)
            {
                // 找离原点最近的面
                float minDist = float.MaxValue;
                EPAFace? closest = null;
                foreach (var f in faces)
                {
                    if (f.Dist < minDist)
                    {
                        minDist = f.Dist;
                        closest = f;
                    }
                }
                if (closest == null) return false;

                normal = closest.Normal;
                penetration = minDist;

                // 沿法线方向找新的支撑点
                Vector3 p = Support(a, b, normal);
                float d = Vector3.Dot(p, normal);

                if (d - penetration < EpaEpsilon)
                {
                    // 收敛了
                    point = (closest.A + closest.B + closest.C) / 3f;
                    return true;
                }

                // 扩展：移除被p"看到"的面，添加新面
                var toRemove = new List<EPAFace>();
                foreach (var f in faces)
                {
                    if (Vector3.Dot(f.Normal, p - f.A) > 0)
                        toRemove.Add(f);
                }
                foreach (var f in toRemove) faces.Remove(f);

                // 收集边界边，沿边界建新面
                int newIdx = simplex.Count;
                simplex.Add(p);
                var boundaryEdges = new List<(int, int)>();
                foreach (var f in toRemove)
                {
                    int ia = simplex.IndexOf(f.A);
                    int ib = simplex.IndexOf(f.B);
                    int ic = simplex.IndexOf(f.C);
                    boundaryEdges.Add((ia, ib));
                    boundaryEdges.Add((ib, ic));
                    boundaryEdges.Add((ic, ia));
                }

                // 去掉成对的(同一条边出现两次说明是内部边)
                var uniqueEdges = new List<(int, int)>();
                for (int i = 0; i < boundaryEdges.Count; i++)
                {
                    bool unique = true;
                    for (int j = 0; j < boundaryEdges.Count; j++)
                    {
                        if (i == j) continue;
                        if (boundaryEdges[i] == (boundaryEdges[j].Item2, boundaryEdges[j].Item1))
                        {
                            unique = false;
                            break;
                        }
                    }
                    if (unique) uniqueEdges.Add(boundaryEdges[i]);
                }

                foreach (var (u, v) in uniqueEdges)
                {
                    AddFace(faces, simplex, u, v, newIdx);
                }
            }

            // 迭代上限
            point = (a.GetCenter() + b.GetCenter()) * 0.5f;
            return true;
        }

        private static void AddFace(List<EPAFace> faces, List<Vector3> verts, int i, int j, int k)
        {
            var a = verts[i];
            var b = verts[j];
            var c = verts[k];
            var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            float dist = Vector3.Dot(normal, a);
            if (dist < 0)
            {
                // 法线朝外
                normal = -normal;
                dist = -dist;
                (b, c) = (c, b);
            }
            faces.Add(new EPAFace { A = a, B = b, C = c, Normal = normal, Dist = dist });
        }

        // =====================================================================
        //  Mesh/Heightfield/Compound 的处理
        // =====================================================================

        private bool MeshVsConvex(MeshShape? mesh, Shape convex,
            Vector3 meshPos, Quaternion meshRot,
            Vector3 convPos, Quaternion convRot,
            ref NarrowphaseContactManifold m, ShapeDatabase shapes)
        {
            if (mesh == null) return false;
            // 遍历mesh的三角形，每个三角形当做一个凸包(三角形)和convex做GJK
            // 优化：先用AABB剔除
            bool anyHit = false;
            NarrowphaseContactManifold best = m;
            best.Penetration = 0;

            foreach (var tri in mesh.Indices)
            {
                // 取三角形顶点
                if (tri.Index0 >= mesh.Vertices.Length || tri.Index1 >= mesh.Vertices.Length || tri.Index2 >= mesh.Vertices.Length) continue;
                var v0 = Vector3.Transform(mesh.Vertices[tri.Index0], meshRot) + meshPos;
                var v1 = Vector3.Transform(mesh.Vertices[tri.Index1], meshRot) + meshPos;
                var v2 = Vector3.Transform(mesh.Vertices[tri.Index2], meshRot) + meshPos;

                // 三角形AABB快速剔除
                var triMin = Vector3.Min(Vector3.Min(v0, v1), v2);
                var triMax = Vector3.Max(Vector3.Max(v0, v1), v2);
                // ...这里省略和convex AABB的精确剔除

                // 建个临时三角形shape
                var triShape = new TriangleShape(v0, v1, v2);
                var triManifold = new NarrowphaseContactManifold { BodyA = m.BodyA, BodyB = m.BodyB };
                if (GJKEPA(triShape.GetSupportShape(Vector3.Zero, Quaternion.Identity), convex.GetSupportShape(convPos, convRot), ref triManifold))
                {
                    if (triManifold.Penetration > best.Penetration)
                    {
                        best = triManifold;
                        anyHit = true;
                    }
                }
            }

            if (anyHit) m = best;
            return anyHit;
        }

        private bool HeightfieldVsConvex(HeightfieldShape? hf, Shape convex,
            Vector3 hfPos, Quaternion hfRot,
            Vector3 convPos, Quaternion convRot,
            ref NarrowphaseContactManifold m, ShapeDatabase shapes)
        {
            if (hf == null) return false;
            // 高度场本质是mesh的特例，把每个格子拆成2个三角形
            // 优化：只在convex的AABB覆盖的格子里查
            bool anyHit = false;
            NarrowphaseContactManifold best = m;
            best.Penetration = 0;

            // 简化：把整个高度场转成三角形列表
            // 正经实现应该做空间索引
            for (int z = 0; z < hf.GridSizeZ - 1; z++)
            {
                for (int x = 0; x < hf.GridSizeX - 1; x++)
                {
                    float h00 = hf.GetHeight(x, z);
                    float h10 = hf.GetHeight(x + 1, z);
                    float h01 = hf.GetHeight(x, z + 1);
                    float h11 = hf.GetHeight(x + 1, z + 1);

                    Vector3 v0 = new(x * hf.CellSize, h00, z * hf.CellSize);
                    Vector3 v1 = new((x + 1) * hf.CellSize, h10, z * hf.CellSize);
                    Vector3 v2 = new(x * hf.CellSize, h01, (z + 1) * hf.CellSize);
                    Vector3 v3 = new((x + 1) * hf.CellSize, h11, (z + 1) * hf.CellSize);

                    v0 = Vector3.Transform(v0, hfRot) + hfPos;
                    v1 = Vector3.Transform(v1, hfRot) + hfPos;
                    v2 = Vector3.Transform(v2, hfRot) + hfPos;
                    v3 = Vector3.Transform(v3, hfRot) + hfPos;

                    var triManifold = new NarrowphaseContactManifold { BodyA = m.BodyA, BodyB = m.BodyB };
                    var tri1 = new TriangleShape(v0, v1, v2);
                    if (GJKEPA(tri1.GetSupportShape(Vector3.Zero, Quaternion.Identity), convex.GetSupportShape(convPos, convRot), ref triManifold))
                    {
                        if (triManifold.Penetration > best.Penetration)
                        {
                            best = triManifold;
                            anyHit = true;
                        }
                    }

                    triManifold = new NarrowphaseContactManifold { BodyA = m.BodyA, BodyB = m.BodyB };
                    var tri2 = new TriangleShape(v1, v3, v2);
                    if (GJKEPA(tri2.GetSupportShape(Vector3.Zero, Quaternion.Identity), convex.GetSupportShape(convPos, convRot), ref triManifold))
                    {
                        if (triManifold.Penetration > best.Penetration)
                        {
                            best = triManifold;
                            anyHit = true;
                        }
                    }
                }
            }

            if (anyHit) m = best;
            return anyHit;
        }

        private bool CompoundVsShape(CompoundShape? compound, Shape other,
            Vector3 compPos, Quaternion compRot,
            Vector3 otherPos, Quaternion otherRot,
            ref NarrowphaseContactManifold m, ShapeDatabase shapes)
        {
            if (compound == null) return false;
            bool anyHit = false;
            NarrowphaseContactManifold best = m;
            best.Penetration = 0;

            foreach (var sub in compound.Children)
            {
                // 子形状的世界变换 = 父变换 * 子局部变换
                var subPos = Vector3.Transform(sub.LocalPos, compRot) + compPos;
                var subRot = Quaternion.Multiply(compRot, sub.LocalRot);
                var subShape = shapes.GetShape(sub.ShapeId);
                if (subShape == null) continue;

                var subManifold = new NarrowphaseContactManifold { BodyA = m.BodyA, BodyB = m.BodyB };
                if (DispatchAndDetect(subShape, other, subPos, subRot, otherPos, otherRot, ref subManifold, shapes))
                {
                    if (subManifold.Penetration > best.Penetration)
                    {
                        best = subManifold;
                        anyHit = true;
                    }
                }
            }

            if (anyHit) m = best;
            return anyHit;
        }

        // =====================================================================
        //  几何辅助函数
        // =====================================================================

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector3 WorldToLocal(Vector3 world, Vector3 origin, Quaternion rot)
        {
            var invRot = Quaternion.Inverse(rot);
            return Vector3.Transform(world - origin, invRot);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector3 ClosestPointOnSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float t = Vector3.Dot(p - a, ab) / ab.LengthSquared();
            t = Math.Clamp(t, 0f, 1f);
            return a + ab * t;
        }

        /// <summary>两条线段之间的最近点对。</summary>
        private static void SegmentSegmentClosest(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2,
            out Vector3 c1, out Vector3 c2)
        {
            Vector3 d1 = q1 - p1;
            Vector3 d2 = q2 - p2;
            Vector3 r = p1 - p2;
            float a = Vector3.Dot(d1, d1);
            float e = Vector3.Dot(d2, d2);
            float f = Vector3.Dot(d2, r);

            float s, t;
            if (a <= GjkEpsilon && e <= GjkEpsilon)
            {
                s = t = 0f;
            }
            else if (a <= GjkEpsilon)
            {
                s = 0f;
                t = Math.Clamp(f / e, 0f, 1f);
            }
            else
            {
                float c = Vector3.Dot(d1, r);
                if (e <= GjkEpsilon)
                {
                    t = 0f;
                    s = Math.Clamp(-c / a, 0f, 1f);
                }
                else
                {
                    float b = Vector3.Dot(d1, d2);
                    float denom = a * e - b * b;
                    if (denom != 0f)
                        s = Math.Clamp((b * f - c * e) / denom, 0f, 1f);
                    else
                        s = 0f;
                    t = (b * s + f) / e;
                    if (t < 0f) { t = 0f; s = Math.Clamp(-c / a, 0f, 1f); }
                    else if (t > 1f) { t = 1f; s = Math.Clamp((b - c) / a, 0f, 1f); }
                }
            }
            c1 = p1 + d1 * s;
            c2 = p2 + d2 * t;
        }

        // =====================================================================
        //  公开测试接口(单元测试用)
        // =====================================================================

        public bool TestSphereSphere(Vector3 posA, float rA, Vector3 posB, float rB,
            out Vector3 normal, out float penetration)
        {
            var m = new NarrowphaseContactManifold();
            var a = new SphereShape(rA);
            var b = new SphereShape(rB);
            bool hit = SphereSphere(a, b, posA, posB, ref m);
            normal = m.Normal;
            penetration = m.Penetration;
            return hit;
        }

        public bool TestGJK(ISupportShape a, ISupportShape b, out Vector3 normal, out float pen)
        {
            var m = new NarrowphaseContactManifold();
            bool hit = GJKEPA(a, b, ref m);
            normal = m.Normal;
            pen = m.Penetration;
            return hit;
        }
    }

    // =====================================================================
    //  临时TriangleShape，用于mesh/heightfield的逐三角形检测
    // =====================================================================

    internal sealed class TriangleShape : Shape
    {
        private readonly Vector3 _a, _b, _c;

        public TriangleShape(Vector3 a, Vector3 b, Vector3 c)
        {
            _a = a; _b = b; _c = c;
            Type = ShapeType.Triangle;
        }

        public override ISupportShape GetSupportShape(Vector3 pos, Quaternion rot)
        {
            return new TriangleSupport(_a, _b, _c, pos, rot);
        }

        private sealed class TriangleSupport : ISupportShape
        {
            private readonly Vector3 _a, _b, _c, _pos;
            private readonly Quaternion _rot;
            public TriangleSupport(Vector3 a, Vector3 b, Vector3 c, Vector3 pos, Quaternion rot)
            {
                _a = a; _b = b; _c = c; _pos = pos; _rot = rot;
            }

            public Vector3 GetSupport(Vector3 dir)
            {
                // 把dir转到局部系
                var localDir = Vector3.Transform(dir, Quaternion.Inverse(_rot));
                float da = Vector3.Dot(_a, localDir);
                float db = Vector3.Dot(_b, localDir);
                float dc = Vector3.Dot(_c, localDir);
                Vector3 local = da >= db && da >= dc ? _a : (db >= dc ? _b : _c);
                return Vector3.Transform(local, _rot) + _pos;
            }

            public Vector3 GetCenter()
            {
                return Vector3.Transform((_a + _b + _c) / 3f, _rot) + _pos;
            }
        }
    }
}
