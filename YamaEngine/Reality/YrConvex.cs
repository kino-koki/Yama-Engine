using System;
namespace YamaEngine.Reality
{
    /// <summary>有向包围盒凸体（复用 RigidBody3D 的四元数姿态）。</summary>
    public readonly struct YrObb
    {
        public readonly RVec3 Center;
        public readonly RQuat Orientation;
        public readonly RVec3 HalfExtents;
        public YrObb(RVec3 center, RQuat orientation, RVec3 halfExtents)
        { Center = center; Orientation = orientation; HalfExtents = halfExtents; }
        public static YrObb AxisAligned(RVec3 center, RVec3 halfExtents)
            => new YrObb(center, RQuat.Identity, halfExtents);
    }

    /// <summary>SAT 命中结果。ContactPoint 为近似点，非完整接触流形。</summary>
    public readonly struct YrSatHit
    {
        public readonly bool Hit;
        public readonly RVec3 Normal;   // 从 a 指向 b
        public readonly float Depth;
        public readonly RVec3 ContactPoint;
        public YrSatHit(bool hit, RVec3 normal, float depth, RVec3 point)
        { Hit = hit; Normal = normal; Depth = depth; ContactPoint = point; }
        public static YrSatHit Miss => new YrSatHit(false, RVec3.Zero, 0f, RVec3.Zero);
    }

    /// <summary>
    /// 凸体窄相：15 轴分离轴定理（3 + 3 + 9 叉积），返回最小穿透轴与深度。
    /// 相对「只做球-球」是本质升级：旋转盒、薄板、斜面都能给出稳定法向。
    /// </summary>
    public static class YrConvex
    {
        private static readonly RVec3 Ex = new RVec3(1f, 0f, 0f);
        private static readonly RVec3 Ey = new RVec3(0f, 1f, 0f);
        private static readonly RVec3 Ez = new RVec3(0f, 0f, 1f);

        private static RVec3[] AxesOf(in YrObb o)
        {
            return new RVec3[] {
                o.Orientation.Rotate(Ex), o.Orientation.Rotate(Ey), o.Orientation.Rotate(Ez)
            };
        }

        private static float ProjectedRadius(in YrObb o, RVec3 axis, RVec3[] axes)
        {
            return MathF.Abs(axis.Dot(axes[0])) * o.HalfExtents.X
                 + MathF.Abs(axis.Dot(axes[1])) * o.HalfExtents.Y
                 + MathF.Abs(axis.Dot(axes[2])) * o.HalfExtents.Z;
        }

        public static YrSatHit SatObbObb(in YrObb a, in YrObb b)
        {
            RVec3[] aa = AxesOf(a), ba = AxesOf(b);
            RVec3 delta = b.Center - a.Center;
            var candidates = new System.Collections.Generic.List<RVec3>(15);
            for (int i = 0; i < 3; i++) candidates.Add(aa[i]);
            for (int i = 0; i < 3; i++) candidates.Add(ba[i]);
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                {
                    RVec3 u = aa[i], v = ba[j];
                    var c = new RVec3(u.Y * v.Z - u.Z * v.Y,
                                      u.Z * v.X - u.X * v.Z,
                                      u.X * v.Y - u.Y * v.X);
                    float l = c.Length();
                    if (l > 1e-6f) candidates.Add(c * (1f / l));
                }

            float minOverlap = float.MaxValue;
            RVec3 bestNormal = RVec3.Zero;
            for (int k = 0; k < candidates.Count; k++)
            {
                RVec3 axis = candidates[k];
                float l2 = axis.LengthSq();
                if (l2 < 1e-12f) continue;
                if (MathF.Abs(l2 - 1f) > 1e-5f) axis = axis * (1f / MathF.Sqrt(l2));
                float d = delta.Dot(axis);
                float overlap = ProjectedRadius(a, axis, aa) + ProjectedRadius(b, axis, ba) - MathF.Abs(d);
                if (overlap <= 0f) return YrSatHit.Miss;
                if (overlap < minOverlap)
                {
                    minOverlap = overlap;
                    bestNormal = d < 0f ? -axis : axis;
                }
            }
            if (minOverlap == float.MaxValue) return YrSatHit.Miss;
            RVec3 point = a.Center + bestNormal * (ProjectedRadius(a, bestNormal, aa) - minOverlap * 0.5f);
            return new YrSatHit(true, bestNormal, minOverlap, point);
        }
    }
}
