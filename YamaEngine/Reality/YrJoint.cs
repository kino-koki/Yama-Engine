using System;
namespace YamaEngine.Reality
{
    public enum YrJointKind
    {
        /// <summary>球窝：锁定三向相对平移，允许任意相对旋转。</summary>
        Ball,
        /// <summary>滑块：只锁定垂直于轴的两向平移，轴向可自由滑动。</summary>
        Slider,
        /// <summary>铰链：锁定三向平移 + 垂直于轴的两向相对角速度。</summary>
        Hinge
    }

    public struct YrJoint
    {
        public int A;
        public int B;
        public RVec3 LocalAnchorA;
        public RVec3 LocalAnchorB;
        public RVec3 Axis;          // 世界系轴，Slider/Hinge 使用
        public YrJointKind Kind;
        public float Beta;          // 速度级位置偏差，默认 0（改用位置投影）
    }

    /// <summary>
    /// 双刚体关节：与接触求解器共用「成对冲量 + 广义有效质量」框架。
    /// Ball 锁 3 轴平移，Slider 只锁 2 轴（保留轴向自由度），
    /// Hinge 额外锁 2 轴相对角速度（保留绕轴自转）。
    /// 局部锚点经四元数旋转到世界系，因此随刚体姿态正确移动。
    /// </summary>
    public sealed class YrJointSolver
    {
        private RigidBody3D[] _bodies = Array.Empty<RigidBody3D>();
        private YrJoint[] _joints = Array.Empty<YrJoint>();

        public YrJoint[] Joints => _joints;

        public void SetBodies(RigidBody3D[] bodies)
            => _bodies = bodies ?? Array.Empty<RigidBody3D>();
        public void SetJoints(YrJoint[] joints)
            => _joints = joints ?? Array.Empty<YrJoint>();

        private bool Valid(in YrJoint j)
            => j.A >= 0 && j.B >= 0 && j.A < _bodies.Length && j.B < _bodies.Length
            && _bodies[j.A] != null && _bodies[j.B] != null;

        private static float EffectiveMass(RigidBody3D a, RigidBody3D b,
                                           RVec3 ra, RVec3 rb, RVec3 dir)
        {
            float angular = a.WorldInvInertiaApply(ra.Cross(dir)).Cross(ra).Dot(dir)
                          + b.WorldInvInertiaApply(rb.Cross(dir)).Cross(rb).Dot(dir);
            return a.InvMass + b.InvMass + angular;
        }

        private static void ApplyPair(RigidBody3D a, RigidBody3D b,
                                      RVec3 ra, RVec3 rb, RVec3 jOnB)
        {
            a.ApplyLinearImpulse(-jOnB);
            b.ApplyLinearImpulse(jOnB);
            a.ApplyAngularImpulse(-(ra.Cross(jOnB)));
            b.ApplyAngularImpulse(rb.Cross(jOnB));
        }

        private static (RVec3 Pa, RVec3 Pb, RVec3 Ra, RVec3 Rb) Anchors(
            in YrJoint j, RigidBody3D a, RigidBody3D b)
        {
            RVec3 pa = a.Position + a.Orientation.Rotate(j.LocalAnchorA);
            RVec3 pb = b.Position + b.Orientation.Rotate(j.LocalAnchorB);
            return (pa, pb, pa - a.Position, pb - b.Position);
        }

        /// <summary>锚点世界位置误差（Ball/Slider 的约束值）。</summary>
        public float AnchorError(in YrJoint j)
        {
            if (!Valid(j)) return 0f;
            var an = Anchors(j, _bodies[j.A], _bodies[j.B]);
            return (an.Pa - an.Pb).Length();
        }

        public void Solve(float dt, int iterations)
        {
            if (dt <= 0f) throw new ArgumentOutOfRangeException(nameof(dt));
            var xyz = new RVec3[] {
                new RVec3(1f, 0f, 0f), new RVec3(0f, 1f, 0f), new RVec3(0f, 0f, 1f)
            };
            for (int it = 0; it < iterations; it++)
            {
                for (int k = 0; k < _joints.Length; k++)
                {
                    ref var j = ref _joints[k];
                    if (!Valid(j)) continue;
                    RigidBody3D a = _bodies[j.A], b = _bodies[j.B];
                    var an = Anchors(j, a, b);
                    RVec3 C = an.Pa - an.Pb;
                    RVec3 axis = j.Axis.LengthSq() > 1e-12f
                        ? j.Axis.Normalized() : new RVec3(0f, 1f, 0f);
                    RVec3[] dirs;
                    if (j.Kind == YrJointKind.Slider)
                    {
                        var basis = YrContactSolver.BasisFromAxis(axis);
                        dirs = new RVec3[] { basis.T1, basis.T2 };
                    }
                    else dirs = xyz;

                    for (int d = 0; d < dirs.Length; d++)
                    {
                        RVec3 dir = dirs[d];
                        RVec3 wa = a.AngularVelocityWorld(), wb = b.AngularVelocityWorld();
                        RVec3 vrel = (b.LinearVelocity + wb.Cross(an.Rb))
                                   - (a.LinearVelocity + wa.Cross(an.Ra));
                        float bias = (j.Beta / dt) * C.Dot(dir);
                        float eff = EffectiveMass(a, b, an.Ra, an.Rb, dir);
                        if (eff <= 1e-12f) continue;
                        float lambda = -(vrel.Dot(dir) + bias) / eff;
                        ApplyPair(a, b, an.Ra, an.Rb, dir * lambda);
                    }

                    if (j.Kind == YrJointKind.Hinge)
                    {
                        var basis = YrContactSolver.BasisFromAxis(axis);
                        RVec3 n1 = basis.T1, n2 = basis.T2;
                        RVec3[] angDirs = new RVec3[] { n1, n2 };
                        for (int d = 0; d < angDirs.Length; d++)
                        {
                            RVec3 n = angDirs[d];
                            float inertiaA = a.WorldInvInertiaApply(n).Dot(n);
                            float inertiaB = b.WorldInvInertiaApply(n).Dot(n);
                            float w = inertiaA + inertiaB;
                            if (w <= 1e-12f) continue;
                            RVec3 wrel = b.AngularVelocityWorld() - a.AngularVelocityWorld();
                            float lambda = -wrel.Dot(n) / w;
                            b.ApplyAngularImpulse(n * lambda);
                            a.ApplyAngularImpulse(-(n * lambda));
                        }
                    }
                }
            }
        }

        /// <summary>位置投影拉回锚点，同样不改速度，避免注入能量。</summary>
        public void CorrectPositions(float beta)
        {
            for (int k = 0; k < _joints.Length; k++)
            {
                ref var j = ref _joints[k];
                if (!Valid(j)) continue;
                RigidBody3D a = _bodies[j.A], b = _bodies[j.B];
                float w = a.InvMass + b.InvMass;
                if (w <= 0f) continue;
                var an = Anchors(j, a, b);
                RVec3 C = an.Pa - an.Pb;
                RVec3 corr = C * (beta / w);
                a.Position = a.Position - corr * a.InvMass;
                b.Position = b.Position + corr * b.InvMass;
            }
        }
    }
}
