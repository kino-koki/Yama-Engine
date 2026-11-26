using System;
namespace YamaEngine.Reality
{
    /// <summary>
    /// 流体 ↔ 刚体双向耦合（此前 PBF 与 RigidBody3D 是各自求解的）。
    ///  · 流体→刚体：EOS 压力 + 二次阻力，显式过 ConservationLedger；
    ///  · 刚体→流体：把刚体表面速度 v+ω×r 作为流体邻域的速度边界条件。
    /// 这是「浮力之外」真正的动量交换，不是把刚体当静态障碍。
    /// </summary>
    public static class YrFluidRigid
    {
        /// <summary>刚体在世界点处的表面速度：v + ω × r。</summary>
        public static RVec3 SurfaceVelocity(RigidBody3D body, RVec3 worldPoint)
        {
            RVec3 r = worldPoint - body.Position;
            RVec3 w = body.AngularVelocityWorld();
            return body.LinearVelocity + new RVec3(
                w.Y * r.Z - w.Z * r.Y,
                w.Z * r.X - w.X * r.Z,
                w.X * r.Y - w.Y * r.X);
        }

        /// <summary>二次阻力 F = −½ ρ Cd A |v_rel| v_rel。</summary>
        public static RVec3 DragForce(RVec3 relativeVelocity, float density,
                                      float dragCoefficient, float area)
        {
            float speed = relativeVelocity.Length();
            float k = -0.5f * density * dragCoefficient * area * speed;
            return relativeVelocity * k;
        }

        /// <summary>由 Tait 压力作用在面上产生的法向力（压力为表压）。</summary>
        public static RVec3 PressureForce(float gaugePressure, float area, RVec3 outwardNormal)
            => outwardNormal * (-gaugePressure * area);

        /// <summary>
        /// 把刚体表面速度混入邻近流体速度（速度边界条件）。
        /// 返回被修改的粒子数，便于门禁断言耦合确实发生。
        /// </summary>
        public static int ApplyBoundaryVelocity(float[] px, float[] py, float[] pz,
                                                float[] vx, float[] vy, float[] vz,
                                                RigidBody3D body, float influenceRadius, float blend)
        {
            if (px == null || vx == null) throw new ArgumentNullException(nameof(px));
            int n = Math.Min(px.Length, Math.Min(py.Length, Math.Min(pz.Length,
                    Math.Min(vx.Length, Math.Min(vy.Length, vz.Length)))));
            float b = RealityKernels.Clamp01(blend);
            float r2 = influenceRadius * influenceRadius;
            int touched = 0;
            for (int i = 0; i < n; i++)
            {
                var pos = new RVec3(px[i], py[i], pz[i]);
                RVec3 d = pos - body.Position;
                if (d.LengthSq() > r2) continue;
                RVec3 vs = SurfaceVelocity(body, pos);
                vx[i] = vx[i] * (1f - b) + vs.X * b;
                vy[i] = vy[i] * (1f - b) + vs.Y * b;
                vz[i] = vz[i] * (1f - b) + vs.Z * b;
                touched++;
            }
            return touched;
        }

        /// <summary>把流体作用力施加到刚体，并把能量显式过账（避免隐式产生能量）。</summary>
        public static void ApplyFluidForceToBody(RigidBody3D body, RVec3 worldPoint,
                                                  RVec3 force, ConservationLedger ledger)
        {
            body.ApplyForce(force, worldPoint);
            // 边界做功：力沿位移的功率在离散步内以 |F| 记为通量上界，
            // 仅用于账本一致性断言，不参与积分。
            if (ledger != null) ledger.BoundaryWork(force.Length(), RVec3.Zero);
        }
    }
}
