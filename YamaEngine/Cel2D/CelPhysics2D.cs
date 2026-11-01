using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.Cel2D
{
    // =========================================================================
    //  2D 形状
    // =========================================================================

    /// <summary>2D 碰撞形状类型。</summary>
    public enum CelShapeType2D
    {
        Circle = 0,
        Box = 1,
        Capsule = 2,
        Polygon = 3,
    }

    /// <summary>2D 形状基类（纯数据）。</summary>
    public abstract class CelShape2D
    {
        public abstract CelShapeType2D ShapeType { get; }

        /// <summary>形状局部质心（用于绕质心旋转）。</summary>
        public virtual Vector2 LocalCenter => Vector2.Zero;

        /// <summary>形状包围盒（局部坐标）。</summary>
        public abstract Rectangle2D LocalBounds { get; }

        /// <summary>质量（密度 1 时的质量）。</summary>
        public abstract float ComputeMass(float density);

        /// <summary>绕质心的转动惯量。</summary>
        public abstract float ComputeInertia(float mass);

        /// <summary>形状是否凸。</summary>
        public virtual bool IsConvex => true;

        /// <summary>计算局部坐标系下的顶点（仅凸多边形类）。</summary>
        public virtual IReadOnlyList<Vector2> GetVertices() => Array.Empty<Vector2>();
    }

    /// <summary>圆形。</summary>
    public sealed class CelCircleShape2D : CelShape2D
    {
        public float Radius { get; set; } = 0.5f;

        public override CelShapeType2D ShapeType => CelShapeType2D.Circle;

        public override Rectangle2D LocalBounds =>
            new Rectangle2D(-Radius, -Radius, Radius * 2.0f, Radius * 2.0f);

        public override float ComputeMass(float density) =>
            MathF.PI * Radius * Radius * density;

        public override float ComputeInertia(float mass) =>
            0.5f * mass * Radius * Radius;
    }

    /// <summary>轴对齐盒（局部空间，质心在中心）。</summary>
    public sealed class CelBoxShape2D : CelShape2D
    {
        public float HalfWidth { get; set; } = 0.5f;
        public float HalfHeight { get; set; } = 0.5f;

        public override CelShapeType2D ShapeType => CelShapeType2D.Box;

        public override Rectangle2D LocalBounds =>
            new Rectangle2D(-HalfWidth, -HalfHeight, HalfWidth * 2.0f, HalfHeight * 2.0f);

        public override float ComputeMass(float density) =>
            4.0f * HalfWidth * HalfHeight * density;

        public override float ComputeInertia(float mass) =>
            mass * (HalfWidth * HalfWidth + HalfHeight * HalfHeight) / 3.0f;

        public override IReadOnlyList<Vector2> GetVertices() => new[]
        {
            new Vector2(-HalfWidth, -HalfHeight),
            new Vector2( HalfWidth, -HalfHeight),
            new Vector2( HalfWidth,  HalfHeight),
            new Vector2(-HalfWidth,  HalfHeight),
        };
    }

    /// <summary>胶囊体（竖直方向，由半径 + 半长定义）。</summary>
    public sealed class CelCapsuleShape2D : CelShape2D
    {
        public float Radius { get; set; } = 0.5f;
        public float HalfHeight { get; set; } = 1.0f;

        public override CelShapeType2D ShapeType => CelShapeType2D.Capsule;

        public override Rectangle2D LocalBounds =>
            new Rectangle2D(-Radius, -HalfHeight, Radius * 2.0f, HalfHeight * 2.0f);

        public override float ComputeMass(float density)
        {
            // 圆柱段 + 两个半球。
            float cyl = 2.0f * Radius * (HalfHeight - Radius) * density;
            float sphere = MathF.PI * Radius * Radius * density;
            return cyl + sphere;
        }

        public override float ComputeInertia(float mass)
        {
            float len = MathF.Max(HalfHeight - Radius, 0.0f);
            float r2 = Radius * Radius;
            return mass * (r2 * 0.5f + len * len / 12.0f);
        }

        /// <summary>胶囊体中轴端点（局部坐标）。</summary>
        public (Vector2 A, Vector2 B) Segment(float halfHeightOverride = float.NaN)
        {
            float h = float.IsNaN(halfHeightOverride) ? HalfHeight : halfHeightOverride;
            float halfLen = MathF.Max(h - Radius, 0.0f);
            return (new Vector2(0, -halfLen), new Vector2(0, halfLen));
        }
    }

    /// <summary>凸多边形（局部坐标顶点，顺时针）。</summary>
    public sealed class CelPolygonShape2D : CelShape2D
    {
        private readonly List<Vector2> _vertices = new();

        public IReadOnlyList<Vector2> Vertices => _vertices;

        public void SetVertices(IEnumerable<Vector2> vertices)
        {
            _vertices.Clear();
            foreach (var v in vertices)
                _vertices.Add(v);
        }

        public override CelShapeType2D ShapeType => CelShapeType2D.Polygon;

        public override bool IsConvex => true;

        public override IReadOnlyList<Vector2> GetVertices() => _vertices;

        public override Rectangle2D LocalBounds
        {
            get
            {
                if (_vertices.Count == 0)
                    return new Rectangle2D();
                float minX = float.MaxValue, minY = float.MaxValue;
                float maxX = float.MinValue, maxY = float.MinValue;
                foreach (var v in _vertices)
                {
                    minX = MathF.Min(minX, v.X); maxX = MathF.Max(maxX, v.X);
                    minY = MathF.Min(minY, v.Y); maxY = MathF.Max(maxY, v.Y);
                }
                return new Rectangle2D(minX, minY, maxX - minX, maxY - minY);
            }
        }

        public override float ComputeMass(float density)
        {
            // 多边形面积（鞋带公式）。
            float area = 0.0f;
            for (int i = 0; i < _vertices.Count; i++)
            {
                Vector2 a = _vertices[i];
                Vector2 b = _vertices[(i + 1) % _vertices.Count];
                area += a.X * b.Y - b.X * a.Y;
            }
            return MathF.Abs(area) * 0.5f * density;
        }

        public override float ComputeInertia(float mass)
        {
            float num = 0.0f, den = 0.0f;
            for (int i = 0; i < _vertices.Count; i++)
            {
                Vector2 a = _vertices[i];
                Vector2 b = _vertices[(i + 1) % _vertices.Count];
                float cross = a.X * b.Y - b.X * a.Y;
                num += cross * (Vector2.Dot(a, a) + Vector2.Dot(a, b) + Vector2.Dot(b, b));
                den += cross;
            }
            if (MathF.Abs(den) < 1e-8f)
                return 0.0f;
            return mass * num / (6.0f * den);
        }
    }

    // =========================================================================
    //  2D 刚体
    // =========================================================================

    /// <summary>2D 刚体。</summary>
    public sealed class CelBody2D
    {
        public string Name { get; set; } = "Body";
        public CelShape2D Shape { get; set; }

        public Vector2 Position;
        public float Rotation;
        public Vector2 LinearVelocity;
        public float AngularVelocity;

        public float Mass { get; private set; } = 1.0f;
        public float InverseMass { get; private set; } = 1.0f;
        public float Inertia { get; private set; } = 1.0f;
        public float InverseInertia { get; private set; } = 1.0f;

        public float Density { get; set; } = 1.0f;
        public float Friction { get; set; } = 0.4f;
        public float Restitution { get; set; } = 0.1f;

        /// <summary>静态体（不可移动）。</summary>
        public bool IsStatic { get; set; }

        /// <summary>是否为传感器（只触发回调，不产生物理响应）。</summary>
        public bool IsSensor { get; set; }

        /// <summary>线性阻尼（每秒衰减比例）。</summary>
        public float LinearDamping { get; set; }
        public float AngularDamping { get; set; }

        public Vector2 Force { get; private set; }
        public float Torque { get; private set; }

        public object? Tag { get; set; }

        public CelBody2D(CelShape2D shape, Vector2 position, float density = 1.0f)
        {
            Shape = shape;
            Position = position;
            Density = density;
            RecomputeMass();
        }

        public void RecomputeMass()
        {
            if (IsStatic || Shape == null)
            {
                Mass = 0.0f;
                InverseMass = 0.0f;
                Inertia = 0.0f;
                InverseInertia = 0.0f;
                return;
            }

            float m = Shape.ComputeMass(Density);
            Mass = m;
            InverseMass = m > 1e-9f ? 1.0f / m : 0.0f;
            Inertia = Shape.ComputeInertia(m);
            InverseInertia = Inertia > 1e-9f ? 1.0f / Inertia : 0.0f;
        }

        public void ApplyForce(Vector2 force) => Force += force;
        public void ApplyForceAtPoint(Vector2 force, Vector2 worldPoint)
        {
            Force += force;
            Vector2 r = worldPoint - Position;
            Torque += r.X * force.Y - r.Y * force.X;
        }

        public void ApplyImpulse(Vector2 impulse, Vector2 contactPoint)
        {
            if (InverseMass == 0.0f)
                return;
            LinearVelocity += impulse * InverseMass;
            Vector2 r = contactPoint - Position;
            AngularVelocity += InverseInertia * (r.X * impulse.Y - r.Y * impulse.X);
        }

        public void ApplyAngularImpulse(float impulse)
        {
            if (InverseInertia == 0.0f)
                return;
            AngularVelocity += InverseInertia * impulse;
        }

        public void SetStatic(bool isStatic)
        {
            IsStatic = isStatic;
            if (isStatic)
            {
                LinearVelocity = Vector2.Zero;
                AngularVelocity = 0.0f;
            }
            RecomputeMass();
        }

        /// <summary>世界空间 AABB。</summary>
        public Rectangle2D ComputeWorldBounds()
        {
            var local = Shape.LocalBounds;
            Vector2 center = Position + Rotate(new Vector2(local.X + local.Width * 0.5f, local.Y + local.Height * 0.5f) - Shape.LocalCenter, Rotation);
            float halfW = local.Width * 0.5f;
            float halfH = local.Height * 0.5f;
            float cos = MathF.Abs(MathF.Cos(Rotation));
            float sin = MathF.Abs(MathF.Sin(Rotation));
            float extX = halfW * cos + halfH * sin;
            float extY = halfW * sin + halfH * cos;
            return new Rectangle2D(center.X - extX, center.Y - extY, extX * 2.0f, extY * 2.0f);
        }

        /// <summary>世界空间顶点（凸多边形）。</summary>
        public IReadOnlyList<Vector2> ComputeWorldVertices()
        {
            var local = Shape.GetVertices();
            if (local.Count == 0)
                return Array.Empty<Vector2>();
            var world = new List<Vector2>(local.Count);
            foreach (var v in local)
                world.Add(Position + Rotate(v - Shape.LocalCenter, Rotation));
            return world;
        }

        internal static Vector2 Rotate(Vector2 v, float angle)
        {
            float c = MathF.Cos(angle);
            float s = MathF.Sin(angle);
            return new Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c);
        }
    }

    // =========================================================================
    //  碰撞流形与检测
    // =========================================================================

    /// <summary>碰撞接触点信息。</summary>
    public readonly struct CelContact2D
    {
        public readonly Vector2 Point;
        public readonly float Separation;

        public CelContact2D(Vector2 point, float separation)
        {
            Point = point;
            Separation = separation;
        }
    }

    /// <summary>碰撞流形（法线从 A 指向 B）。</summary>
    public sealed class CelManifold2D
    {
        public CelBody2D A { get; internal set; }
        public CelBody2D B { get; internal set; }
        public Vector2 Normal { get; internal set; }
        public float Depth { get; internal set; }
        public CelContact2D[] Contacts { get; internal set; } = Array.Empty<CelContact2D>();
        public bool IsColliding { get; internal set; }
    }

    /// <summary>2D 碰撞检测器（纯几何）。</summary>
    public static class CelCollision2D
    {
        // ---- 圆 - 圆 ----
        public static bool CircleCircle(Vector2 ca, float ra, Vector2 cb, float rb, out Vector2 normal, out float depth, out Vector2 point)
        {
            Vector2 d = cb - ca;
            float dist = d.Length();
            float sum = ra + rb;
            if (dist >= sum || dist <= 1e-9f)
            {
                normal = Vector2.Zero;
                depth = 0.0f;
                point = Vector2.Zero;
                return false;
            }
            normal = d / dist;
            depth = sum - dist;
            point = ca + normal * (ra - depth * 0.5f);
            return true;
        }

        // ---- 圆 - 盒 (OBB) ----
        public static bool CircleBox(CelBody2D circle, CelBody2D box, out Vector2 normal, out float depth, out Vector2 point)
        {
            var circleShape = circle.Shape as CelCircleShape2D;
            var boxShape = box.Shape as CelBoxShape2D;
            if (circleShape == null || boxShape == null)
            {
                normal = Vector2.Zero; depth = 0.0f; point = Vector2.Zero;
                return false;
            }

            // 把圆心变换到盒局部空间。
            Vector2 local = CelBody2D.Rotate(circle.Position - box.Position, -box.Rotation);
            Vector2 clamped = new Vector2(
                MathF.Clamp(local.X, -boxShape.HalfWidth, boxShape.HalfWidth),
                MathF.Clamp(local.Y, -boxShape.HalfHeight, boxShape.HalfHeight));
            Vector2 diff = local - clamped;
            float dist = diff.Length();

            bool inside = dist <= 1e-9f;
            if (!inside && dist >= circleShape.Radius)
            {
                normal = Vector2.Zero; depth = 0.0f; point = Vector2.Zero;
                return false;
            }

            Vector2 localNormal;
            if (inside)
            {
                // 圆心在盒内：沿最近的面推出。
                float dx = boxShape.HalfWidth - MathF.Abs(local.X);
                float dy = boxShape.HalfHeight - MathF.Abs(local.Y);
                if (dx < dy)
                    localNormal = new Vector2(MathF.Sign(local.X) * dx, 0.0f);
                else
                    localNormal = new Vector2(0.0f, MathF.Sign(local.Y) * dy);
                depth = circleShape.Radius + (dx < dy ? dx : dy);
            }
            else
            {
                localNormal = diff / dist;
                depth = circleShape.Radius - dist;
            }

            normal = CelBody2D.Rotate(localNormal, box.Rotation);
            point = circle.Position + normal * (depth * 0.5f);
            return true;
        }

        // ---- 盒 - 盒 (SAT) ----
        public static bool BoxBox(CelBody2D a, CelBody2D b, out Vector2 normal, out float depth, out Vector2 point)
        {
            var sa = a.Shape as CelBoxShape2D;
            var sb = b.Shape as CelBoxShape2D;
            if (sa == null || sb == null)
            {
                normal = Vector2.Zero; depth = 0.0f; point = Vector2.Zero;
                return false;
            }

            var va = TransformBox(a, sa);
            var vb = TransformBox(b, sb);
            if (SAT(va, vb, out normal, out depth, out point))
                return true;

            normal = Vector2.Zero; depth = 0.0f; point = Vector2.Zero;
            return false;
        }

        // ---- 凸多边形 - 凸多边形 (SAT) ----
        public static bool PolygonPolygon(CelBody2D a, CelBody2D b, out Vector2 normal, out float depth, out Vector2 point)
        {
            var va = a.ComputeWorldVertices();
            var vb = b.ComputeWorldVertices();
            if (va.Count < 3 || vb.Count < 3)
            {
                normal = Vector2.Zero; depth = 0.0f; point = Vector2.Zero;
                return false;
            }
            if (SAT(va, vb, out normal, out depth, out point))
                return true;
            normal = Vector2.Zero; depth = 0.0f; point = Vector2.Zero;
            return false;
        }

        // ---- 胶囊 - 胶囊 (segment-segment) ----
        public static bool CapsuleCapsule(CelBody2D a, CelBody2D b, out Vector2 normal, out float depth, out Vector2 point)
        {
            var sa = a.Shape as CelCapsuleShape2D;
            var sb = b.Shape as CelCapsuleShape2D;
            if (sa == null || sb == null)
            {
                normal = Vector2.Zero; depth = 0.0f; point = Vector2.Zero;
                return false;
            }

            var (a0, a1) = TransformSegment(a, sa);
            var (b0, b1) = TransformSegment(b, sb);
            ClosestSegmentSegment(a0, a1, b0, b1, out Vector2 pa, out Vector2 pb, out float t);

            Vector2 d = pb - pa;
            float dist = d.Length();
            float sum = sa.Radius + sb.Radius;
            if (dist >= sum || dist <= 1e-9f)
            {
                normal = Vector2.Zero; depth = 0.0f; point = Vector2.Zero;
                return false;
            }
            normal = d / dist;
            depth = sum - dist;
            point = pa + normal * (sa.Radius - depth * 0.5f);
            return true;
        }

        // ---- 胶囊 - 盒 ----
        public static bool CapsuleBox(CelBody2D cap, CelBody2D box, out Vector2 normal, out float depth, out Vector2 point)
        {
            var sc = cap.Shape as CelCapsuleShape2D;
            var sb = box.Shape as CelBoxShape2D;
            if (sc == null || sb == null)
            {
                normal = Vector2.Zero; depth = 0.0f; point = Vector2.Zero;
                return false;
            }

            var (a0, a1) = TransformSegment(cap, sc);
            var bv = TransformBox(box, sb);

            float bestDepth = -float.MaxValue;
            Vector2 bestNormal = Vector2.Zero;
            Vector2 bestPoint = Vector2.Zero;

            // 胶囊两端各做圆-多边形最近点，取最深。
            for (int i = 0; i < 2; i++)
            {
                Vector2 c = i == 0 ? a0 : a1;
                ClosestPointOnPolygon(c, bv, out Vector2 cp, out Vector2 n, out float d, out bool inside);
                float pen = inside ? sc.Radius + d : sc.Radius - d;
                if (pen > bestDepth)
                {
                    bestDepth = pen;
                    bestNormal = n;
                    bestPoint = cp + n * (sc.Radius * 0.5f);
                }
            }

            if (bestDepth <= 0.0f)
            {
                normal = Vector2.Zero; depth = 0.0f; point = Vector2.Zero;
                return false;
            }
            normal = bestNormal;
            depth = bestDepth;
            point = bestPoint;
            return true;
        }

        // ---- 通用派发 ----
        public static bool Test(CelBody2D a, CelBody2D b, out CelManifold2D manifold)
        {
            manifold = new CelManifold2D { A = a, B = b };

            var ta = a.Shape.ShapeType;
            var tb = b.Shape.ShapeType;

            bool hit;
            Vector2 normal = Vector2.Zero, point = Vector2.Zero;
            float depth = 0.0f;

            // 约定：流形法线恒从 A 指向 B（供求解器沿 -normal 推 A、+normal 推 B）。
            // 圆/胶囊 vs 盒/多边形 的辅助函数返回 盒/多边形→圆/胶囊，
            // 因此当 A 是圆/胶囊、B 是盒/多边形时需取反。
            if (ta == CelShapeType2D.Circle && tb == CelShapeType2D.Circle)
                hit = CircleCircle(a.Position, ((CelCircleShape2D)a.Shape).Radius, b.Position, ((CelCircleShape2D)b.Shape).Radius, out normal, out depth, out point);
            else if (ta == CelShapeType2D.Circle && tb == CelShapeType2D.Box)
            {
                hit = CircleBox(a, b, out normal, out depth, out point);
                if (hit) normal = -normal;
            }
            else if (ta == CelShapeType2D.Box && tb == CelShapeType2D.Circle)
                hit = CircleBox(b, a, out normal, out depth, out point);
            else if (ta == CelShapeType2D.Box && tb == CelShapeType2D.Box)
                hit = BoxBox(a, b, out normal, out depth, out point);
            else if (ta == CelShapeType2D.Capsule && tb == CelShapeType2D.Capsule)
                hit = CapsuleCapsule(a, b, out normal, out depth, out point);
            else if (ta == CelShapeType2D.Capsule && tb == CelShapeType2D.Box)
            {
                hit = CapsuleBox(a, b, out normal, out depth, out point);
                if (hit) normal = -normal;
            }
            else if (ta == CelShapeType2D.Box && tb == CelShapeType2D.Capsule)
                hit = CapsuleBox(b, a, out normal, out depth, out point);
            else if (ta == CelShapeType2D.Polygon && tb == CelShapeType2D.Polygon)
                hit = PolygonPolygon(a, b, out normal, out depth, out point);
            else if (ta == CelShapeType2D.Circle && tb == CelShapeType2D.Polygon)
            {
                hit = CirclePolygon(a, b, out normal, out depth, out point);
                if (hit) normal = -normal;
            }
            else if (ta == CelShapeType2D.Polygon && tb == CelShapeType2D.Circle)
                hit = CirclePolygon(b, a, out normal, out depth, out point);
            else if (ta == CelShapeType2D.Capsule && tb == CelShapeType2D.Polygon)
            {
                hit = CapsulePolygon(a, b, out normal, out depth, out point);
                if (hit) normal = -normal;
            }
            else if (ta == CelShapeType2D.Polygon && tb == CelShapeType2D.Capsule)
                hit = CapsulePolygon(b, a, out normal, out depth, out point);
            else
                hit = false;

            manifold.IsColliding = hit;
            manifold.Normal = normal;
            manifold.Depth = depth;
            if (hit)
                manifold.Contacts = new[] { new CelContact2D(point, depth) };
            else
                manifold.Contacts = Array.Empty<CelContact2D>();

            return hit;
        }

        // ---- 圆 - 凸多边形 ----
        private static bool CirclePolygon(CelBody2D circle, CelBody2D poly, out Vector2 normal, out float depth, out Vector2 point)
        {
            var cs = circle.Shape as CelCircleShape2D;
            var vs = poly.ComputeWorldVertices();
            if (cs == null || vs.Count < 3)
            {
                normal = Vector2.Zero; depth = 0.0f; point = Vector2.Zero;
                return false;
            }
            ClosestPointOnPolygon(circle.Position, vs, out Vector2 cp, out Vector2 n, out float d, out bool inside);
            if (inside || d < cs.Radius)
            {
                normal = n;
                depth = inside ? cs.Radius + d : cs.Radius - d;
                point = cp;
                return true;
            }
            normal = Vector2.Zero; depth = 0.0f; point = Vector2.Zero;
            return false;
        }

        // ---- 胶囊 - 凸多边形 ----
        private static bool CapsulePolygon(CelBody2D cap, CelBody2D poly, out Vector2 normal, out float depth, out Vector2 point)
        {
            var cs = cap.Shape as CelCapsuleShape2D;
            var vs = poly.ComputeWorldVertices();
            if (cs == null || vs.Count < 3)
            {
                normal = Vector2.Zero; depth = 0.0f; point = Vector2.Zero;
                return false;
            }
            var (a0, a1) = TransformSegment(cap, cs);

            float best = -float.MaxValue;
            Vector2 bestN = Vector2.Zero, bestP = Vector2.Zero;
            for (int i = 0; i < 2; i++)
            {
                Vector2 c = i == 0 ? a0 : a1;
                ClosestPointOnPolygon(c, vs, out Vector2 cp, out Vector2 n, out float d, out bool inside);
                float pen = inside ? cs.Radius + d : cs.Radius - d;
                if (pen > best) { best = pen; bestN = n; bestP = cp; }
            }
            if (best <= 0.0f)
            {
                normal = Vector2.Zero; depth = 0.0f; point = Vector2.Zero;
                return false;
            }
            normal = bestN;
            depth = best;
            point = bestP;
            return true;
        }

        private static Vector2[] TransformBox(CelBody2D body, CelBoxShape2D shape)
        {
            var vs = shape.GetVertices();
            var result = new Vector2[4];
            for (int i = 0; i < 4; i++)
                result[i] = body.Position + CelBody2D.Rotate(vs[i] - shape.LocalCenter, body.Rotation);
            return result;
        }

        private static (Vector2 A, Vector2 B) TransformSegment(CelBody2D body, CelCapsuleShape2D shape)
        {
            var (a, b) = shape.Segment();
            return (body.Position + CelBody2D.Rotate(a - shape.LocalCenter, body.Rotation),
                    body.Position + CelBody2D.Rotate(b - shape.LocalCenter, body.Rotation));
        }

        // ---- SAT (凸多边形 vs 凸多边形，顶点世界坐标) ----
        private static bool SAT(IReadOnlyList<Vector2> va, IReadOnlyList<Vector2> vb, out Vector2 normal, out float depth, out Vector2 point)
        {
            normal = Vector2.Zero;
            depth = float.MaxValue;
            point = Vector2.Zero;

            // 分离轴：A 的面法线 + B 的面法线。
            for (int i = 0; i < va.Count; i++)
            {
                Vector2 edge = va[(i + 1) % va.Count] - va[i];
                Vector2 axis = new Vector2(-edge.Y, edge.X);
                if (axis.LengthSquared() < 1e-9f) continue;
                axis = Vector2.Normalize(axis);

                if (SeparatedOnAxis(va, vb, axis, out float d))
                    return false;
                if (d < depth) { depth = d; normal = axis; }
            }

            for (int i = 0; i < vb.Count; i++)
            {
                Vector2 edge = vb[(i + 1) % vb.Count] - vb[i];
                Vector2 axis = new Vector2(-edge.Y, edge.X);
                if (axis.LengthSquared() < 1e-9f) continue;
                axis = Vector2.Normalize(axis);

                if (SeparatedOnAxis(va, vb, axis, out float d))
                    return false;
                if (d < depth) { depth = d; normal = axis; }
            }

            if (depth <= 0.0f)
                return false;

            // 保证法线从 A 指向 B。
            Vector2 ca = Centroid(va);
            Vector2 cb = Centroid(vb);
            if (Vector2.Dot(normal, cb - ca) < 0.0f)
                normal = -normal;

            point = (ca + cb) * 0.5f;
            return true;
        }

        private static bool SeparatedOnAxis(IReadOnlyList<Vector2> va, IReadOnlyList<Vector2> vb, Vector2 axis, out float overlap)
        {
            float minA = float.MaxValue, maxA = float.MinValue;
            float minB = float.MaxValue, maxB = float.MinValue;
            foreach (var v in va)
            {
                float p = Vector2.Dot(v, axis);
                minA = MathF.Min(minA, p); maxA = MathF.Max(maxA, p);
            }
            foreach (var v in vb)
            {
                float p = Vector2.Dot(v, axis);
                minB = MathF.Min(minB, p); maxB = MathF.Max(maxB, p);
            }
            overlap = MathF.Min(maxA, maxB) - MathF.Max(minA, minB);
            return overlap < 0.0f;
        }

        private static Vector2 Centroid(IReadOnlyList<Vector2> vs)
        {
            Vector2 s = Vector2.Zero;
            foreach (var v in vs) s += v;
            return s / vs.Count;
        }

        /// <summary>
        /// 求点 p 到凸多边形边界最近点。
        /// 始终返回最近边界点、该点处外法线（远离多边形内部）与距离，
        /// 并通过 <paramref name="centerInside"/> 指示 p 是否在多边形内部。
        /// </summary>
        private static bool ClosestPointOnPolygon(Vector2 p, IReadOnlyList<Vector2> vs, out Vector2 closest, out Vector2 normal, out float dist, out bool centerInside)
        {
            closest = Vector2.Zero;
            normal = Vector2.Zero;
            dist = float.MaxValue;
            centerInside = false;

            for (int i = 0; i < vs.Count; i++)
            {
                Vector2 a = vs[i];
                Vector2 b = vs[(i + 1) % vs.Count];
                Vector2 ab = b - a;
                Vector2 ap = p - a;
                float len2 = ab.LengthSquared();
                float t = len2 > 1e-9f ? MathF.Clamp(Vector2.Dot(ap, ab) / len2, 0.0f, 1.0f) : 0.0f;
                Vector2 proj = a + ab * t;
                float d = Vector2.Distance(p, proj);
                if (d < dist)
                {
                    dist = d;
                    closest = proj;
                    // 外法线：边的垂直方向，指向远离多边形内部。
                    Vector2 edgeN = new Vector2(ab.Y, -ab.X);
                    if (edgeN.LengthSquared() > 1e-9f)
                    {
                        edgeN = Vector2.Normalize(edgeN);
                        // 外法线应指向远离多边形质心。
                        Vector2 edgeMid = (a + b) * 0.5f;
                        Vector2 centroid = Centroid(vs);
                        if (Vector2.Dot(edgeN, centroid - edgeMid) > 0.0f)
                            edgeN = -edgeN;
                        normal = edgeN;
                    }
                }
            }

            // 点在多边形内部判定（叉积一致，顶点假设为一致绕向）。
            int sign = 0;
            centerInside = true;
            for (int i = 0; i < vs.Count; i++)
            {
                Vector2 a = vs[i];
                Vector2 b = vs[(i + 1) % vs.Count];
                float cross = (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
                int s = cross > 1e-9f ? 1 : (cross < -1e-9f ? -1 : 0);
                if (s != 0)
                {
                    if (sign == 0) sign = s;
                    else if (s != sign) { centerInside = false; break; }
                }
            }

            return true;
        }

        private static void ClosestSegmentSegment(Vector2 a0, Vector2 a1, Vector2 b0, Vector2 b1, out Vector2 pa, out Vector2 pb, out float t)
        {
            Vector2 d1 = a1 - a0;
            Vector2 d2 = b1 - b0;
            Vector2 r = a0 - b0;
            float a = Vector2.Dot(d1, d1);
            float e = Vector2.Dot(d2, d2);
            float f = Vector2.Dot(d2, r);

            if (a <= 1e-9f && e <= 1e-9f)
            {
                pa = a0; pb = b0; t = 0.0f; return;
            }
            if (a <= 1e-9f)
            {
                t = 0.0f;
                float s = MathF.Clamp(f / e, 0.0f, 1.0f);
                pa = a0; pb = b0 + d2 * s;
                return;
            }

            float c = Vector2.Dot(d1, r);
            float b = Vector2.Dot(d1, d2);
            float denom = a * e - b * b;
            float sVal = denom > 1e-9f ? MathF.Clamp((b * f - c * e) / denom, 0.0f, 1.0f) : 0.0f;
            t = (b * sVal + f) / e;
            if (t < 0.0f) { t = 0.0f; sVal = MathF.Clamp(-c / a, 0.0f, 1.0f); }
            else if (t > 1.0f) { t = 1.0f; sVal = MathF.Clamp((b - c) / a, 0.0f, 1.0f); }

            pa = a0 + d1 * sVal;
            pb = b0 + d2 * t;
        }
    }

    // =========================================================================
    //  2D 物理世界
    // =========================================================================

    /// <summary>2D 物理世界：积分 + 宽相位 + 窄相位 + 冲量求解。</summary>
    public sealed class CelPhysicsWorld2D
    {
        private readonly List<CelBody2D> _bodies = new();
        private readonly List<(CelBody2D A, CelBody2D B)> _contacts = new();

        public Vector2 Gravity { get; set; } = new Vector2(0.0f, 9.81f * 100.0f); // 像素/秒^2

        /// <summary>每帧子步数（稳定性）。</summary>
        public int SubSteps { get; set; } = 4;

        /// <summary>位置校正系数（Baumgarte，0..1）。</summary>
        public float PositionCorrection { get; set; } = 0.2f;

        /// <summary>允许穿透深度（像素，用于位置校正阈值）。</summary>
        public float Slop { get; set; } = 0.5f;

        public IReadOnlyList<CelBody2D> Bodies => _bodies;

        public event Action<CelManifold2D>? OnContact;
        public event Action<CelManifold2D>? OnContactBegin;
        public event Action<CelManifold2D>? OnContactEnd;

        private readonly Dictionary<CelBody2D, HashSet<CelBody2D>> _touching = new();

        public CelBody2D CreateBody(CelShape2D shape, Vector2 position, float density = 1.0f, string? name = null)
        {
            var body = new CelBody2D(shape, position, density);
            if (name != null) body.Name = name;
            _bodies.Add(body);
            return body;
        }

        public void AddBody(CelBody2D body) => _bodies.Add(body);

        public void RemoveBody(CelBody2D body)
        {
            _bodies.Remove(body);
            _touching.Remove(body);
        }

        public void Clear()
        {
            _bodies.Clear();
            _contacts.Clear();
            _touching.Clear();
        }

        /// <summary>推进一帧。</summary>
        public void Update(float deltaTime)
        {
            if (deltaTime <= 0.0f)
                return;

            float dt = deltaTime / SubSteps;
            for (int i = 0; i < SubSteps; i++)
                Step(dt);
        }

        private void Step(float dt)
        {
            // 1. 积分力 → 速度。
            foreach (var body in _bodies)
            {
                if (body.InverseMass == 0.0f)
                    continue;
                body.LinearVelocity += (body.Force * body.InverseMass + Gravity) * dt;
                body.AngularVelocity += body.Torque * body.InverseInertia * dt;

                body.LinearVelocity *= MathF.Max(0.0f, 1.0f - body.LinearDamping * dt);
                body.AngularVelocity *= MathF.Max(0.0f, 1.0f - body.AngularDamping * dt);

                body.Force = Vector2.Zero;
                body.Torque = 0.0f;
            }

            // 2. 宽相位：AABB 扫掠。
            _contacts.Clear();
            for (int i = 0; i < _bodies.Count; i++)
            {
                var a = _bodies[i];
                for (int j = i + 1; j < _bodies.Count; j++)
                {
                    var b = _bodies[j];
                    if (a.InverseMass == 0.0f && b.InverseMass == 0.0f)
                        continue;
                    if (!AabbOverlap(a.ComputeWorldBounds(), b.ComputeWorldBounds()))
                        continue;
                    if (CelCollision2D.Test(a, b, out var manifold))
                    {
                        if (!manifold.IsColliding)
                            continue;
                        _contacts.Add((a, b));
                        TrackContacts(a, b, manifold);
                        if (!a.IsSensor && !b.IsSensor)
                            ResolveContact(manifold, dt);
                        OnContact?.Invoke(manifold);
                    }
                }
            }

            // 3. 积分速度 → 位置。
            foreach (var body in _bodies)
            {
                if (body.InverseMass == 0.0f)
                    continue;
                body.Position += body.LinearVelocity * dt;
                body.Rotation += body.AngularVelocity * dt;
            }
        }

        private void TrackContacts(CelBody2D a, CelBody2D b, CelManifold2D m)
        {
            if (!_touching.TryGetValue(a, out var setA))
                _touching[a] = setA = new HashSet<CelBody2D>();
            bool isNew = setA.Add(b);
            if (isNew)
                OnContactBegin?.Invoke(m);
        }

        private void ResolveContact(CelManifold2D m, float dt)
        {
            CelBody2D a = m.A, b = m.B;
            Vector2 normal = m.Normal;

            // 位置校正（投影）。
            float invMassSum = a.InverseMass + b.InverseMass;
            if (invMassSum > 0.0f)
            {
                float correction = MathF.Max(m.Depth - Slop, 0.0f) / invMassSum * PositionCorrection;
                a.Position -= normal * correction * a.InverseMass;
                b.Position += normal * correction * b.InverseMass;
            }

            // 相对速度沿法线。
            Vector2 rv = b.LinearVelocity - a.LinearVelocity;
            float velAlongNormal = Vector2.Dot(rv, normal);
            if (velAlongNormal > 0.0f)
                return;

            // 冲量。
            float restitution = MathF.Min(a.Restitution, b.Restitution);
            float j = -(1.0f + restitution) * velAlongNormal / invMassSum;
            Vector2 impulse = normal * j;
            a.LinearVelocity -= impulse * a.InverseMass;
            b.LinearVelocity += impulse * b.InverseMass;

            // 摩擦（切向）。
            Vector2 tangent = rv - normal * velAlongNormal;
            if (tangent.LengthSquared() > 1e-9f)
            {
                tangent = Vector2.Normalize(tangent);
                float jt = -Vector2.Dot(rv, tangent) / invMassSum;
                float friction = MathF.Sqrt(a.Friction * b.Friction);
                float maxFriction = j * friction;
                jt = MathF.Clamp(jt, -maxFriction, maxFriction);
                Vector2 frictionImpulse = tangent * jt;
                a.LinearVelocity -= frictionImpulse * a.InverseMass;
                b.LinearVelocity += frictionImpulse * b.InverseMass;
            }
        }

        private static bool AabbOverlap(Rectangle2D a, Rectangle2D b)
        {
            return a.X < b.X + b.Width &&
                   a.X + a.Width > b.X &&
                   a.Y < b.Y + b.Height &&
                   a.Y + a.Height > b.Y;
        }

        /// <summary>射线检测（圆 / 盒 / 胶囊 / 多边形）。</summary>
        public CelBody2D? Raycast(Vector2 origin, Vector2 direction, float maxDistance, out Vector2 hitPoint, out float hitDistance)
        {
            hitPoint = Vector2.Zero;
            hitDistance = float.MaxValue;
            CelBody2D? hit = null;
            float min = float.MaxValue;

            foreach (var body in _bodies)
            {
                var bounds = body.ComputeWorldBounds();
                if (!RayIntersectsAabb(origin, direction, maxDistance, bounds))
                    continue;

                if (RaycastShape(body, origin, direction, maxDistance, out float d))
                {
                    if (d < min)
                    {
                        min = d;
                        hit = body;
                        hitDistance = d;
                        hitPoint = origin + direction * d;
                    }
                }
            }

            if (hit == null)
                hitDistance = 0.0f;
            return hit;
        }

        private static bool RayIntersectsAabb(Vector2 o, Vector2 d, float maxDist, Rectangle2D box)
        {
            float tmin = 0.0f, tmax = maxDist;
            Vector2 dir = d.LengthSquared() > 1e-9f ? d / d.Length() : Vector2.UnitX;
            foreach (var axis in new[] { 0, 1 })
            {
                float oa = axis == 0 ? o.X : o.Y;
                float da = axis == 0 ? dir.X : dir.Y;
                float min = axis == 0 ? box.X : box.Y;
                float max = axis == 0 ? box.X + box.Width : box.Y + box.Height;
                if (MathF.Abs(da) < 1e-9f)
                {
                    if (oa < min || oa > max) return false;
                }
                else
                {
                    float t1 = (min - oa) / da;
                    float t2 = (max - oa) / da;
                    if (t1 > t2) (t1, t2) = (t2, t1);
                    tmin = MathF.Max(tmin, t1);
                    tmax = MathF.Min(tmax, t2);
                    if (tmin > tmax) return false;
                }
            }
            return true;
        }

        private static bool RaycastShape(CelBody2D body, Vector2 o, Vector2 d, float maxDist, out float hitDist)
        {
            hitDist = float.MaxValue;
            Vector2 dir = d.LengthSquared() > 1e-9f ? d / d.Length() : Vector2.UnitX;
            bool found = false;

            switch (body.Shape.ShapeType)
            {
                case CelShapeType2D.Circle:
                {
                    var c = (CelCircleShape2D)body.Shape;
                    Vector2 oc = o - body.Position;
                    float b = Vector2.Dot(oc, dir);
                    float c2 = Vector2.Dot(oc, oc) - c.Radius * c.Radius;
                    float disc = b * b - c2;
                    if (disc >= 0.0f)
                    {
                        float t = -b - MathF.Sqrt(disc);
                        if (t >= 0.0f && t <= maxDist) { hitDist = t; found = true; }
                    }
                    break;
                }
                case CelShapeType2D.Box:
                case CelShapeType2D.Polygon:
                {
                    var vs = body.ComputeWorldVertices();
                    if (vs.Count < 3) break;
                    for (int i = 0; i < vs.Count; i++)
                    {
                        Vector2 a = vs[i];
                        Vector2 b2 = vs[(i + 1) % vs.Count];
                        if (RaySegment(o, dir, a, b2, out float t) && t >= 0.0f && t <= maxDist && t < hitDist)
                        {
                            hitDist = t;
                            found = true;
                        }
                    }
                    break;
                }
                case CelShapeType2D.Capsule:
                {
                    var cap = (CelCapsuleShape2D)body.Shape;
                    var (a0, a1) = TransformSegmentWorld(body, cap);
                    if (RaySegment(o, dir, a0, a1, out float ts) && ts >= 0.0f && ts <= maxDist && ts < hitDist)
                    {
                        hitDist = ts;
                        found = true;
                    }
                    break;
                }
            }

            return found;
        }

        private static (Vector2, Vector2) TransformSegmentWorld(CelBody2D body, CelCapsuleShape2D shape)
        {
            var (a, b) = shape.Segment();
            return (body.Position + CelBody2D.Rotate(a - shape.LocalCenter, body.Rotation),
                    body.Position + CelBody2D.Rotate(b - shape.LocalCenter, body.Rotation));
        }

        private static bool RaySegment(Vector2 o, Vector2 d, Vector2 a, Vector2 b, out float t)
        {
            t = -1.0f;
            Vector2 ab = b - a;
            Vector2 ao = o - a;
            float denom = Cross(d, ab);
            if (MathF.Abs(denom) < 1e-9f)
                return false;
            t = Cross(ao, ab) / denom;
            float u = Cross(ao, d) / denom;
            return t >= 0.0f && u >= 0.0f && u <= 1.0f;
        }

        private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
    }
}
