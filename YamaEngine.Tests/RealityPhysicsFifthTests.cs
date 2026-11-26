using System;
using YamaEngine.Reality;

namespace YamaEngine.Tests
{
    /// <summary>
    /// 第五轮回归（14 项）：宽相 SAP、接触流形、窄相链、统一管线、
    /// 守恒与回滚重放。全部进入现有 build.yml 的 Test step 真实执行。
    /// </summary>
    public static class RealityPhysicsFifthTests
    {
        private static void Require(bool ok, string msg)
        {
            if (!ok) throw new InvalidOperationException("REALITY_R5_FAIL: " + msg);
        }
        private static bool Near(float a, float b, float tol) => MathF.Abs(a - b) <= tol;

        private static RigidBody3D Make(RVec3 pos, float mass)
        {
            var b = new RigidBody3D { Position = pos, Mass = mass };
            b.SetInertia(RigidBody3D.SphereInertia(mass, 0.5f));
            return b;
        }
        private static RVec3 H(float v) => new RVec3(v, v, v);

        public static void TestR5AabbFromAxisAlignedObb()
        {
            var o = YrObb.AxisAligned(new RVec3(1f, 2f, 3f), new RVec3(0.5f, 1f, 1.5f));
            var box = YrBroadPhase.FromObb(o);
            Require(Near(box.Min.X, 0.5f, 1e-6f) && Near(box.Max.X, 1.5f, 1e-6f), "AABB minX/maxX 错误");
            Require(Near(box.Min.Y, 1f, 1e-6f) && Near(box.Max.Y, 3f, 1e-6f), "AABB minY/maxY 错误");
            Require(Near(box.Min.Z, 1.5f, 1e-6f) && Near(box.Max.Z, 4.5f, 1e-6f), "AABB minZ/maxZ 错误");
        }

        public static void TestR5AabbEnclosesRotatedObbVertices()
        {
            var q = RQuat.FromAxisAngle(new RVec3(1f, 1f, 1f), 0.8f);
            var o = new YrObb(new RVec3(0.5f, -1f, 2f), q, new RVec3(1f, 0.4f, 0.7f));
            var box = YrBroadPhase.FromObb(o);
            RVec3[] verts = YrBroadPhase.ObbVertices(o);
            Require(verts.Length == 8, "OBB 顶点数应为 8");
            for (int i = 0; i < verts.Length; i++)
                Require(box.Contains(verts[i]), "旋转 OBB 顶点未被 AABB 包住 @" + i);
        }

        public static void TestR5SapFindsOverlappingPairs()
        {
            var obbs = new YrObb[] {
                YrObb.AxisAligned(RVec3.Zero, H(1f)),
                YrObb.AxisAligned(new RVec3(1.5f, 0f, 0f), H(1f)),
                YrObb.AxisAligned(new RVec3(0.75f, 1.5f, 0f), H(1f))
            };
            var pairs = YrBroadPhase.SweepAndPrune(obbs);
            Require(pairs.Length == 3, "三盒两两重叠应有 3 对，实际 " + pairs.Length);
            for (int k = 0; k < pairs.Length; k++)
                Require(pairs[k].Item1 < pairs[k].Item2, "候选对必须 A<B");
        }

        public static void TestR5SapNoFalsePositiveWhenSeparated()
        {
            var obbs = new YrObb[] {
                YrObb.AxisAligned(RVec3.Zero, H(1f)),
                YrObb.AxisAligned(new RVec3(10f, 0f, 0f), H(1f)),
                YrObb.AxisAligned(new RVec3(0f, 10f, 0f), H(1f))
            };
            var pairs = YrBroadPhase.SweepAndPrune(obbs);
            Require(pairs.Length == 0, "完全分离不应产生候选对，实际 " + pairs.Length);
        }

        public static void TestR5SapOrderIsDeterministic()
        {
            var boxes = new YrAabb[] {
                new YrAabb(new RVec3(0f, 0f, 0f), new RVec3(1f, 1f, 1f)),
                new YrAabb(new RVec3(0f, 0f, 0f), new RVec3(1f, 1f, 1f)),
                new YrAabb(new RVec3(-1f, 0f, 0f), new RVec3(0.5f, 1f, 1f))
            };
            int[] a = YrBroadPhase.SortByMinX(boxes);
            int[] b = YrBroadPhase.SortByMinX(boxes);
            Require(a.Length == b.Length, "排序长度不一致");
            for (int i = 0; i < a.Length; i++)
                Require(a[i] == b[i], "相同 minX 时未按索引升序稳定排序 @" + i);
            Require(a[0] == 2, "最小 minX 的索引应先出现");
        }

        public static void TestR5ManifoldPointCountIsBounded()
        {
            var a = YrObb.AxisAligned(RVec3.Zero, H(1f));
            var b = YrObb.AxisAligned(new RVec3(1.8f, 0f, 0f), H(1f));
            var hit = YrConvex.SatObbObb(a, b);
            Require(hit.Hit, "重叠盒应命中");
            RVec3[] pts = YrContactManifold.Build(a, b, hit);
            Require(pts.Length >= 1 && pts.Length <= YrContactManifold.MaxPoints,
                    "接触流形点数越界: " + pts.Length);
            var miss = YrContactManifold.Build(a, b, YrSatHit.Miss);
            Require(miss.Length == 0, "无命中不应生成流形点");
        }

        public static void TestR5ManifoldPointsNearContactPlane()
        {
            var a = YrObb.AxisAligned(RVec3.Zero, H(1f));
            var b = YrObb.AxisAligned(new RVec3(1.8f, 0f, 0f), H(1f));
            var hit = YrConvex.SatObbObb(a, b);
            RVec3[] pts = YrContactManifold.Build(a, b, hit);
            RVec3 n = hit.Normal.Normalized();
            float ra = YrContactManifold.ProjectedRadius(a, n);
            for (int i = 0; i < pts.Length; i++)
            {
                float signed = (pts[i] - a.Center).Dot(n) - ra;
                // 侵入顶点应落在参考面附近（±0.5 的顶点深度范围内）
                Require(signed <= 1e-4f && signed >= -0.5f,
                        "流形点未落在接触面附近 @" + i + " : " + signed);
            }
        }

        public static void TestR5ManifoldIsDeterministic()
        {
            var a = YrObb.AxisAligned(RVec3.Zero, H(1f));
            var b = YrObb.AxisAligned(new RVec3(1.7f, 0.2f, 0f), H(1f));
            var hit = YrConvex.SatObbObb(a, b);
            RVec3[] p1 = YrContactManifold.Build(a, b, hit);
            RVec3[] p2 = YrContactManifold.Build(a, b, hit);
            Require(p1.Length == p2.Length, "流形点数不确定");
            for (int i = 0; i < p1.Length; i++)
                Require(p1[i].X == p2[i].X && p1[i].Y == p2[i].Y && p1[i].Z == p2[i].Z,
                        "流形点逐位不确定 @" + i);
        }

        public static void TestR5NarrowPhaseGeneratesContacts()
        {
            var bodies = new RigidBody3D[] {
                Make(RVec3.Zero, 1f), Make(new RVec3(1.8f, 0f, 0f), 1f)
            };
            var obbs = new YrObb[] {
                new YrObb(bodies[0].Position, bodies[0].Orientation, H(1f)),
                new YrObb(bodies[1].Position, bodies[1].Orientation, H(1f))
            };
            var narrow = new YrNarrowPhase();
            var pairs = YrBroadPhase.SweepAndPrune(obbs);
            YrContact[] cs = narrow.Build(pairs, obbs, bodies, 1f / 60f, YrSurface.Default);
            Require(cs.Length >= 1, "命中对应生成接触点");
            Require(narrow.LastHitPairs == 1, "命中对数应为 1");
            for (int k = 0; k < cs.Length; k++)
            {
                Require(cs[k].BodyA == 0 && cs[k].BodyB == 1, "接触体索引错误");
                Require(cs[k].Normal.X > 0.9f, "接触法向应从 A 指向 B");
                Require(cs[k].Depth > 0f, "接触深度应为正");
            }
        }

        public static void TestR5NarrowPhaseNoHitYieldsNoContacts()
        {
            var bodies = new RigidBody3D[] {
                Make(RVec3.Zero, 1f), Make(new RVec3(10f, 0f, 0f), 1f)
            };
            var obbs = new YrObb[] {
                new YrObb(bodies[0].Position, bodies[0].Orientation, H(1f)),
                new YrObb(bodies[1].Position, bodies[1].Orientation, H(1f))
            };
            var narrow = new YrNarrowPhase();
            var pairs = YrBroadPhase.SweepAndPrune(obbs);
            YrContact[] cs = narrow.Build(pairs, obbs, bodies, 1f / 60f, YrSurface.Default);
            Require(cs.Length == 0, "分离体不应生成接触");
            Require(narrow.LastHitPairs == 0, "命中对数应为 0");
        }

        public static void TestR5CcdSweepReportsApproach()
        {
            var bodies = new RigidBody3D[] {
                Make(RVec3.Zero, 1f), Make(new RVec3(8f, 0f, 0f), 1f)
            };
            bodies[0].LinearVelocity = new RVec3(10f, 0f, 0f);
            var obbs = new YrObb[] {
                new YrObb(bodies[0].Position, bodies[0].Orientation, new RVec3(0.5f, 0.5f, 0.5f)),
                new YrObb(bodies[1].Position, bodies[1].Orientation, new RVec3(0.5f, 0.5f, 0.5f))
            };
            float toi = YrNarrowPhase.EarliestToi(
                new (int, int)[] { (0, 1) }, obbs, bodies, 1f);
            Require(toi >= 0f && toi <= 1f, "接近运动应报告 TOI: " + toi);
            bodies[0].LinearVelocity = RVec3.Zero;
            float none = YrNarrowPhase.EarliestToi(
                new (int, int)[] { (0, 1) }, obbs, bodies, 1f);
            Require(none < 0f, "静止分离不应报告 TOI");
        }

        public static void TestR5PipelineFixedStepsAdvanceTick()
        {
            var bodies = new RigidBody3D[] {
                Make(new RVec3(-2f, 0f, 0f), 1f), Make(new RVec3(2f, 0f, 0f), 1f)
            };
            var pipe = new YrPhysicsPipeline(bodies, new RVec3[] { H(0.5f), H(0.5f) });
            pipe.SleepEnabled = false;
            int steps = pipe.Step(0.05);
            Require(steps == 3, "0.05s 应执行 3 个固定步，实际 " + steps);
            Require(pipe.Tick == 3, "管线 tick 未同步: " + pipe.Tick);
            Require(pipe.LastStats.BroadPhasePairs == 0, "分离体不应有宽相对");
            Require(pipe.LastStats.IslandCount == 2, "两个独立刚体应成 2 个岛");
        }

        public static void TestR5PipelineConservesTotalMomenta()
        {
            var bodies = new RigidBody3D[] {
                Make(new RVec3(-1.2f, 0.1f, 0f), 2f),
                Make(new RVec3(1.2f, -0.1f, 0.2f), 3f)
            };
            bodies[0].LinearVelocity = new RVec3(3f, 0.2f, -0.1f);
            bodies[1].LinearVelocity = new RVec3(-2f, -0.1f, 0.3f);
            bodies[0].AngularMomentum = new RVec3(0.1f, -0.2f, 0.05f);
            bodies[1].AngularMomentum = new RVec3(-0.05f, 0.1f, 0.2f);
            RVec3 p0 = YrContactSolver.TotalLinearMomentum(bodies);
            RVec3 l0 = YrContactSolver.TotalAngularMomentum(bodies);
            var pipe = new YrPhysicsPipeline(bodies, new RVec3[] { H(1f), H(1f) });
            pipe.SleepEnabled = false;
            pipe.Surface = new YrSurface(0.5f, 0.6f, 0.4f);
            // 位置投影是非物理瞬移，会改变轨道项 r×(mv)；
            // 严格验总角动量守恒必须关闭它（beta=0），不是放宽容差。
            pipe.PositionCorrectionBeta = 0f;
            for (int k = 0; k < 12; k++) pipe.StepFixed(1f / 60f);
            RVec3 p1 = YrContactSolver.TotalLinearMomentum(bodies);
            RVec3 l1 = YrContactSolver.TotalAngularMomentum(bodies);
            Require((p1 - p0).Length() < 1e-4f,
                    "管线总线动量不守恒: " + (p1 - p0).Length());
            Require((l1 - l0).Length() < 1e-3f,
                    "管线总角动量(自旋+轨道)不守恒: " + (l1 - l0).Length());
            Require(pipe.LastStats.LedgerResidual < 1e-6, "账本失衡");
        }

        public static void TestR5PipelineDeterministicAndRollbackReplay()
        {
            YrPhysicsPipeline MakePipe()
            {
                var b = new RigidBody3D[] {
                    Make(new RVec3(-1.2f, 0f, 0f), 1f),
                    Make(new RVec3(1.2f, 0.1f, 0f), 1f)
                };
                b[0].LinearVelocity = new RVec3(2f, 0.3f, 0f);
                b[1].LinearVelocity = new RVec3(-1f, -0.2f, 0f);
                var p = new YrPhysicsPipeline(b, new RVec3[] { H(1f), H(1f) });
                p.SleepEnabled = false;
                return p;
            }
            var a = MakePipe();
            var b = MakePipe();
            var ha = new ulong[13];
            var hb = new ulong[13];
            ha[0] = YrRollbackBuffer.Checksum(a.Snapshot(), 0);
            hb[0] = YrRollbackBuffer.Checksum(b.Snapshot(), 0);
            a.SaveFrame(); b.SaveFrame();
            for (int k = 0; k < 12; k++)
            {
                a.StepFixed(1f / 60f); b.StepFixed(1f / 60f);
                a.SaveFrame(); b.SaveFrame();
                ha[k + 1] = YrRollbackBuffer.Checksum(a.Snapshot(), a.Tick);
                hb[k + 1] = YrRollbackBuffer.Checksum(b.Snapshot(), b.Tick);
                Require(ha[k + 1] == hb[k + 1], "两条同初态管线分叉 @" + (k + 1));
            }

            // 回滚到 tick 6 并重放 6 步，校验和必须逐项复现原始序列 7..12
            Require(a.RewindTo(6), "未找到 tick 6 快照");
            Require(a.Tick == 6, "回滚后 tick 未恢复: " + a.Tick);
            Require(a.Rollback.Count == 7, "Rewind 未丢弃未来帧");
            for (int k = 6; k < 12; k++)
            {
                a.StepFixed(1f / 60f);
                ulong rh = YrRollbackBuffer.Checksum(a.Snapshot(), a.Tick);
                Require(rh == ha[k + 1], "回滚重放校验和分叉 @" + (k + 1));
            }
        }
    }
}
