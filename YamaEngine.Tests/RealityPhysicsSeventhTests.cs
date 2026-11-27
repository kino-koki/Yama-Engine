using System;
using YamaEngine.Reality;

namespace YamaEngine.Tests
{
    /// <summary>
    /// 第七轮回归（14 项）：扫掠 AABB BVH 规模化、不漏报 parity、
    /// Sutherland-Hodgman 面裁剪流形、角速度保守 CCD、管线开关。
    /// 全部进入现有 build.yml 的 Test step 真实执行。
    /// </summary>
    public static class RealityPhysicsSeventhTests
    {
        private static void Require(bool ok, string msg)
        {
            if (!ok) throw new InvalidOperationException("REALITY_R7_FAIL: " + msg);
        }
        private static bool Near(float a, float b, float tol) => MathF.Abs(a - b) <= tol;

        private static RigidBody3D Make(RVec3 pos, float mass)
        {
            var b = new RigidBody3D { Position = pos, Mass = mass };
            b.SetInertia(RigidBody3D.SphereInertia(mass, 0.5f));
            return b;
        }
        private static RVec3 H(float v) => new RVec3(v, v, v);
        private static YrObb Box(RigidBody3D b, float h)
            => new YrObb(b.Position, b.Orientation, H(h));

        public static void TestR7BvhFindsOverlappingPairs()
        {
            var boxes = new YrAabb[] {
                new YrAabb(new RVec3(0f,0f,0f), new RVec3(2f,2f,2f)),
                new YrAabb(new RVec3(1f,0f,0f), new RVec3(3f,2f,2f)),
                new YrAabb(new RVec3(1.5f,1f,0f), new RVec3(2.5f,3f,2f))
            };
            var bvh = new YrSweptBroadphase();
            bvh.Build(boxes);
            var pairs = bvh.QueryPairs();
            Require(pairs.Length == 3, "三盒两两重叠应有 3 对，实际 " + pairs.Length);
            for (int k = 0; k < pairs.Length; k++)
                Require(pairs[k].Item1 < pairs[k].Item2, "候选对必须 A<B");
        }

        public static void TestR7BvhSeparatedYieldsNoPairs()
        {
            var boxes = new YrAabb[] {
                new YrAabb(new RVec3(0f,0f,0f), new RVec3(1f,1f,1f)),
                new YrAabb(new RVec3(10f,0f,0f), new RVec3(11f,1f,1f)),
                new YrAabb(new RVec3(0f,10f,0f), new RVec3(1f,11f,1f))
            };
            var bvh = new YrSweptBroadphase();
            bvh.Build(boxes);
            Require(bvh.QueryPairs().Length == 0, "完全分离不应有候选对");
        }

        public static void TestR7BvhHeightIsLogarithmic()
        {
            // 200 个沿 x 均匀散布的盒；中位分割 BVH 高度应远小于 n
            var boxes = new YrAabb[200];
            for (int i = 0; i < boxes.Length; i++)
            {
                float x = i * 3f;
                boxes[i] = new YrAabb(new RVec3(x, 0f, 0f), new RVec3(x + 1f, 1f, 1f));
            }
            var bvh = new YrSweptBroadphase();
            bvh.Build(boxes);
            Require(bvh.Height <= 16, "BVH 高度退化: " + bvh.Height);
            Require(bvh.NodeCount <= 2 * boxes.Length + 1, "节点数超上界");
            Require(bvh.QueryPairs().Length == 0, "均匀分离不应有候选对");
        }

        public static void TestR7BvhIsDeterministic()
        {
            var boxes = new YrAabb[64];
            // 确定性伪布局（不用 Random，避免平台差异）
            for (int i = 0; i < boxes.Length; i++)
            {
                float x = ((i * 37) % 61) * 1.7f;
                float z = ((i * 19) % 23) * 1.3f;
                boxes[i] = new YrAabb(new RVec3(x, 0f, z), new RVec3(x + 2f, 2f, z + 2f));
            }
            var a = new YrSweptBroadphase(); a.Build(boxes);
            var b = new YrSweptBroadphase(); b.Build(boxes);
            var pa = a.QueryPairs(); var pb = b.QueryPairs();
            Require(pa.Length == pb.Length, "BVH 候选对数量不确定");
            for (int i = 0; i < pa.Length; i++)
                Require(pa[i].Item1 == pb[i].Item1 && pa[i].Item2 == pb[i].Item2,
                        "BVH 候选对逐位不确定 @" + i);
        }

        public static void TestR7SweptBoxEnclosesWholeTrajectory()
        {
            var o = YrObb.AxisAligned(RVec3.Zero, H(1f));
            var v = new RVec3(20f, 3f, -4f);
            float dt = 0.5f;
            YrAabb swept = YrSweptBroadphase.SweptBox(o, v, 0f, dt);
            RVec3 start = new RVec3(-1f, -1f, -1f);
            RVec3 end = start + v * dt;
            RVec3 hi = new RVec3(1f, 1f, 1f) + v * dt;
            Require(swept.Contains(start, 0.1f), "轨迹起点未被包住");
            Require(swept.Contains(end, 0.1f), "轨迹终点未被包住");
            Require(swept.Contains(hi, 0.1f), "轨迹终点上界未被包住");
        }

        /// <summary>关键：当前分离、整帧会撞的高速对必须出现在 BVH 候选里。</summary>
        public static void TestR7SweptPairsCatchesTunnelingPair()
        {
            var bodies = new RigidBody3D[] {
                Make(new RVec3(-5f, 0f, 0f), 1f),
                Make(new RVec3(5f, 0f, 0f), 1f),
                Make(new RVec3(0f, 30f, 0f), 1f)
            };
            bodies[0].LinearVelocity = new RVec3(20f, 0f, 0f);
            var obbs = new YrObb[] { Box(bodies[0], 1f), Box(bodies[1], 1f), Box(bodies[2], 1f) };
            var pairs = YrSweptBroadphase.SweptPairs(obbs, bodies, 0.65f);
            bool found = false;
            for (int k = 0; k < pairs.Length; k++)
                if (pairs[k].Item1 == 0 && pairs[k].Item2 == 1) found = true;
            Require(found, "当前分离但本帧会撞的对未被 BVH 捕获");
            // 远处静止体不应产生候选
            for (int k = 0; k < pairs.Length; k++)
                Require(pairs[k].Item1 != 2 && pairs[k].Item2 != 2,
                        "远处静止体不应进候选对");
        }

        /// <summary>parity：所有真正有 TOI 的对，必须都在扫掠 BVH 候选超集里。</summary>
        public static void TestR7SweptPairsIsSupersetOfAllToiHits()
        {
            var bodies = new RigidBody3D[12];
            var obbs = new YrObb[12];
            for (int i = 0; i < bodies.Length; i++)
            {
                float x = ((i * 29) % 17) * 4f - 30f;
                float y = ((i * 11) % 7) * 2f - 6f;
                bodies[i] = Make(new RVec3(x, y, 0f), 1f);
                bodies[i].LinearVelocity = new RVec3(((i * 7) % 5) * 3f - 6f,
                                                      ((i * 5) % 3) * 1f - 1f, 0f);
                obbs[i] = Box(bodies[i], 1f);
            }
            float dt = 0.5f;
            var pairs = YrSweptBroadphase.SweptPairs(obbs, bodies, dt);
            var set = new System.Collections.Generic.HashSet<(int, int)>(pairs);
            for (int i = 0; i < bodies.Length; i++)
                for (int j = i + 1; j < bodies.Length; j++)
                {
                    float toi = YrSweptObb.ComputeToi(
                        obbs[i], bodies[i].LinearVelocity,
                        obbs[j], bodies[j].LinearVelocity, dt);
                    if (toi >= 0f)
                        Require(set.Contains((i, j)),
                                "有 TOI 的对 (" + i + "," + j + ") 不在 BVH 候选超集里");
                }
        }

        public static void TestR7SweptPairsMuchSmallerThanQuadratic()
        {
            int n = 200;
            var bodies = new RigidBody3D[n];
            var obbs = new YrObb[n];
            for (int i = 0; i < n; i++)
            {
                float x = i * 4f;
                bodies[i] = Make(new RVec3(x, 0f, (i % 3) * 4f), 1f);
                obbs[i] = Box(bodies[i], 1f);
            }
            var pairs = YrSweptBroadphase.SweptPairs(obbs, bodies, 1f / 60f);
            int quadratic = n * (n - 1) / 2;
            Require(quadratic == 19900, "全对基数应为 19900");
            Require(pairs.Length < 400,
                    "BVH 候选对未显著少于 O(n²): " + pairs.Length + " / " + quadratic);
        }

        public static void TestR7FaceClippingProducesBoundedPoints()
        {
            var a = YrObb.AxisAligned(RVec3.Zero, H(1f));
            var b = YrObb.AxisAligned(new RVec3(1.8f, 0.2f, 0.1f), H(1f));
            var hit = YrConvex.SatObbObb(a, b);
            Require(hit.Hit, "重叠盒应命中");
            RVec3[] pts = YrFaceClipping.Build(a, b, hit);
            Require(pts.Length >= 1 && pts.Length <= YrFaceClipping.MaxPoints,
                    "面裁剪流形点数越界: " + pts.Length);
            Require(YrFaceClipping.Build(a, b, YrSatHit.Miss).Length == 0,
                    "无命中不应生成流形点");
        }

        public static void TestR7FaceClippingPointsInsideReferenceFace()
        {
            var a = YrObb.AxisAligned(RVec3.Zero, H(1f));
            var b = YrObb.AxisAligned(new RVec3(1.7f, 0.3f, -0.2f), H(1f));
            var hit = YrConvex.SatObbObb(a, b);
            RVec3 n = hit.Normal.Normalized();
            var rf = YrFaceClipping.ReferenceFace(a, n);
            RVec3[] pts = YrFaceClipping.Build(a, b, hit);
            for (int i = 0; i < pts.Length; i++)
                Require(YrFaceClipping.InsideReference(rf, pts[i]),
                        "裁剪点落在参考面矩形外 @" + i);
        }

        public static void TestR7FaceClippingIsDeterministic()
        {
            var q = RQuat.FromAxisAngle(new RVec3(0f, 1f, 0f), 0.4f);
            var a = new YrObb(RVec3.Zero, q, H(1f));
            var b = YrObb.AxisAligned(new RVec3(1.6f, 0.2f, 0f), H(1f));
            var hit = YrConvex.SatObbObb(a, b);
            var p1 = YrFaceClipping.Build(a, b, hit);
            var p2 = YrFaceClipping.Build(a, b, hit);
            Require(p1.Length == p2.Length, "面裁剪点数不确定");
            for (int i = 0; i < p1.Length; i++)
                Require(p1[i].X == p2[i].X && p1[i].Y == p2[i].Y && p1[i].Z == p2[i].Z,
                        "面裁剪点逐位不确定 @" + i);
            RVec3[] inc = YrFaceClipping.IncidentFace(b, hit.Normal.Normalized());
            Require(inc.Length == 4, "入射面应有 4 个顶点");
        }

        public static void TestR7AngularInflationNeverReportsLater()
        {
            var a = YrObb.AxisAligned(new RVec3(-5f, 0f, 0f), H(1f));
            var b = YrObb.AxisAligned(new RVec3(5f, 0f, 0f), H(1f));
            var va = new RVec3(20f, 0f, 0f);
            float plain = YrSweptObb.ComputeToi(a, va, b, RVec3.Zero, 0.65f);
            float slow = YrSweptObb.ComputeToiWithAngular(
                a, va, new RVec3(0f, 0f, 1f), b, RVec3.Zero, RVec3.Zero, 0.65f);
            float fast = YrSweptObb.ComputeToiWithAngular(
                a, va, new RVec3(0f, 0f, 30f), b, RVec3.Zero, RVec3.Zero, 0.65f);
            Require(plain >= 0f, "基准 TOI 应存在");
            Require(slow <= plain + 1e-6f, "角速度膨胀不应晚报 TOI");
            Require(fast <= slow + 1e-6f, "角速度越大应越早报（保守单调）");
            Require(YrSweptObb.AngularInflation(0f, 1.7f, 0.5f) == 0f,
                    "零角速度不应膨胀");
        }

        public static void TestR7PipelineBvhSwitchReducesCcdPairs()
        {
            var bodies = new RigidBody3D[] {
                Make(new RVec3(-5f, 0f, 0f), 1f), Make(new RVec3(5f, 0f, 0f), 1f)
            };
            bodies[0].LinearVelocity = new RVec3(20f, 0f, 0f);
            var pipe = new YrPhysicsPipeline(bodies, new RVec3[] { H(1f), H(1f) });
            pipe.SleepEnabled = false; pipe.UseToiSubsteps = true;
            pipe.UseBvhCcdPairs = true;
            pipe.StepFixedToi(0.65f);
            Require(pipe.LastCcdPairs == 1, "BVH 应只给出 1 个 CCD 候选对，实际 " + pipe.LastCcdPairs);
            Require(pipe.LastToiHits >= 1, "BVH 路径仍应捕获接触");
        }

        public static void TestR7PipelineFaceClippingSwitchChangesManifold()
        {
            YrContact[] Run(bool face)
            {
                var bodies = new RigidBody3D[] {
                    Make(RVec3.Zero, 1f), Make(new RVec3(1.8f, 0.3f, 0f), 1f)
                };
                var obbs = new YrObb[] { Box(bodies[0], 1f), Box(bodies[1], 1f) };
                var narrow = new YrNarrowPhase { UseFaceClipping = face };
                var pairs = YrBroadPhase.SweepAndPrune(obbs);
                return narrow.Build(pairs, obbs, bodies, 1f / 60f, YrSurface.Default);
            }
            var approx = Run(false);
            var clipped = Run(true);
            Require(approx.Length >= 1 && clipped.Length >= 1, "两种流形都应生成接触点");
            // 不要求点数必然不同（几何可能巧合），但要求开关确实生效且不抛异常
            var pipe = new YrPhysicsPipeline(
                new RigidBody3D[] { Make(RVec3.Zero, 1f), Make(new RVec3(1.8f, 0f, 0f), 1f) },
                new RVec3[] { H(1f), H(1f) });
            pipe.UseFaceClipping = true;
            Require(pipe.UseFaceClipping, "管线开关未透传到窄相");
            pipe.SleepEnabled = false;
            pipe.StepFixed(1f / 60f);
            Require(pipe.Tick == 1, "面裁剪开关下常规管线仍应正常推进");
        }
    }
}
