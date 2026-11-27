using System;
using YamaEngine.Reality;

namespace YamaEngine.Tests
{
    /// <summary>
    /// 第八轮回归（14 项）：增量动态 AABB 树、与静态 BVH 的 parity、
    /// 逐点持久 warm start、快照向前兼容与回滚重放。
    /// 全部进入现有 build.yml 的 Test step 真实执行。
    /// </summary>
    public static class RealityPhysicsEighthTests
    {
        private static void Require(bool ok, string msg)
        {
            if (!ok) throw new InvalidOperationException("REALITY_R8_FAIL: " + msg);
        }

        private static RigidBody3D Make(RVec3 pos, float mass)
        {
            var b = new RigidBody3D { Position = pos, Mass = mass };
            b.SetInertia(RigidBody3D.SphereInertia(mass, 0.5f));
            return b;
        }
        private static RVec3 H(float v) => new RVec3(v, v, v);

        private static YrAabb[] Layout(int n)
        {
            var boxes = new YrAabb[n];
            for (int i = 0; i < n; i++)
            {
                float x = ((i * 37) % 61) * 1.7f;
                float z = ((i * 19) % 23) * 1.3f;
                boxes[i] = new YrAabb(new RVec3(x, 0f, z), new RVec3(x + 2f, 2f, z + 2f));
            }
            return boxes;
        }

        public static void TestR8DynamicTreeFindsOverlappingPairs()
        {
            var boxes = new YrAabb[] {
                new YrAabb(new RVec3(0f,0f,0f), new RVec3(2f,2f,2f)),
                new YrAabb(new RVec3(1f,0f,0f), new RVec3(3f,2f,2f)),
                new YrAabb(new RVec3(1.5f,1f,0f), new RVec3(2.5f,3f,2f))
            };
            var t = YrDynamicAabbTree.RebuildFrom(boxes);
            var pairs = t.QueryPairs();
            Require(pairs.Length == 3, "三盒两两重叠应有 3 对，实际 " + pairs.Length);
            for (int k = 0; k < pairs.Length; k++)
                Require(pairs[k].Item1 < pairs[k].Item2, "候选对必须 A<B");
        }

        public static void TestR8DynamicTreeSeparatedYieldsNoPairs()
        {
            var boxes = new YrAabb[] {
                new YrAabb(new RVec3(0f,0f,0f), new RVec3(1f,1f,1f)),
                new YrAabb(new RVec3(10f,0f,0f), new RVec3(11f,1f,1f)),
                new YrAabb(new RVec3(0f,10f,0f), new RVec3(1f,11f,1f))
            };
            var t = YrDynamicAabbTree.RebuildFrom(boxes);
            Require(t.QueryPairs().Length == 0, "完全分离不应有候选对");
        }

        public static void TestR8DynamicTreeHeightIsLogarithmic()
        {
            var boxes = new YrAabb[200];
            for (int i = 0; i < boxes.Length; i++)
            {
                float x = i * 3f;
                boxes[i] = new YrAabb(new RVec3(x, 0f, 0f), new RVec3(x + 1f, 1f, 1f));
            }
            var t = YrDynamicAabbTree.RebuildFrom(boxes);
            Require(t.Height <= 24, "动态树高度退化: " + t.Height);
            Require(t.NodeCount <= 2 * boxes.Length + 1, "节点数超上界: " + t.NodeCount);
            Require(t.QueryPairs().Length == 0, "均匀分离不应有候选对");
        }

        public static void TestR8DynamicTreeMatchesStaticBvhParity()
        {
            var boxes = Layout(48);
            var dyn = YrDynamicAabbTree.RebuildFrom(boxes);
            var stat = new YrSweptBroadphase();
            stat.Build(boxes);
            var a = dyn.QueryPairs();
            var b = stat.QueryPairs();
            Require(a.Length == b.Length,
                    "动态树/静态 BVH 候选对数量不一致: " + a.Length + " vs " + b.Length);
            for (int k = 0; k < a.Length; k++)
                Require(a[k].Item1 == b[k].Item1 && a[k].Item2 == b[k].Item2,
                        "动态树/静态 BVH 候选对逐项不一致 @" + k);
        }

        public static void TestR8DynamicTreeIsDeterministic()
        {
            var boxes = Layout(64);
            var t1 = YrDynamicAabbTree.RebuildFrom(boxes);
            var t2 = YrDynamicAabbTree.RebuildFrom(boxes);
            var a = t1.QueryPairs(); var b = t2.QueryPairs();
            Require(a.Length == b.Length, "两次构建候选对数量不确定");
            for (int i = 0; i < a.Length; i++)
                Require(a[i].Item1 == b[i].Item1 && a[i].Item2 == b[i].Item2,
                        "动态树候选对逐位不确定 @" + i);
            Require(t1.Height == t2.Height, "树高不确定");
        }

        public static void TestR8DynamicTreeUpdateReusesLeafWhenFatContains()
        {
            var t = new YrDynamicAabbTree();
            var box = new YrAabb(new RVec3(0f, 0f, 0f), new RVec3(2f, 2f, 2f));
            int leaf = t.Insert(0, box);
            var tiny = new YrAabb(new RVec3(0.01f, 0.01f, 0.01f),
                                  new RVec3(1.99f, 1.99f, 1.99f));
            int after = t.Update(0, tiny);
            Require(after == leaf, "fat 盒包住微小移动时应复用同一 leaf");
            Require(t.NodeCount == 1 && t.Root == leaf, "不应产生额外节点");
            var far = new YrAabb(new RVec3(50f, 0f, 0f), new RVec3(52f, 2f, 2f));
            int moved = t.Update(0, far);
            // 「leaf id 必须改变」是错的断言：自由链表会复用刚释放的节点索引，
            // 单节点树删后重插拿到同一个 id 恰恰是设计特性。
            // 真正该验的是「盒确实被换了、树确实被重建」。
            Require(t.LeafOfBody(0) == moved, "重插后 leaf 映射应指向新节点");
            Require(t.Root == moved, "单节点树重插后 root 应指向该 leaf");
            Require(Math.Abs(t.Nodes[moved].Box.Min.X - 50f) < 1e-6f,
                    "超出原盒后叶盒未更新，实际 Min.X=" + t.Nodes[moved].Box.Min.X);
            Require(t.NodeCount == 1, "单节点树删后重插不应残留多余节点");
        }

        public static void TestR8DynamicTreeRemoveAndReinsert()
        {
            var boxes = Layout(32);
            var t = YrDynamicAabbTree.RebuildFrom(boxes);
            var before = t.QueryPairs();
            for (int i = 0; i < 8; i++) t.Remove(i);
            Require(t.LeafOfBody(0) < 0, "Remove 后 leaf 映射未清空");
            for (int i = 0; i < 8; i++) t.Insert(i, boxes[i]);
            var after = t.QueryPairs();
            Require(before.Length == after.Length, "删后重插候选对数应复原");
            for (int k = 0; k < before.Length; k++)
                Require(before[k].Item1 == after[k].Item1
                     && before[k].Item2 == after[k].Item2,
                        "删后重插候选对逐项不一致 @" + k);
        }

        public static void TestR8FaceClippingIdsAreStableAndBounded()
        {
            var a = YrObb.AxisAligned(RVec3.Zero, H(1f));
            var b = YrObb.AxisAligned(new RVec3(1.8f, 0.2f, 0.1f), H(1f));
            var hit = YrConvex.SatObbObb(a, b);
            Require(hit.Hit, "重叠盒应命中");
            var t1 = YrFaceClipping.BuildTagged(a, b, hit);
            var t2 = YrFaceClipping.BuildTagged(a, b, hit);
            Require(t1.Length >= 1 && t1.Length <= 4, "带 Id 流形点数越界");
            Require(t1.Length == t2.Length, "带 Id 流形点数不确定");
            for (int i = 0; i < t1.Length; i++)
            {
                Require(t1[i].Id >= 0 && t1[i].Id <= 3,
                        "pointId 应落在 incident 顶点 0..3: " + t1[i].Id);
                Require(t1[i].Id == t2[i].Id, "pointId 逐位不确定 @" + i);
                Require(t1[i].Point.X == t2[i].Point.X
                     && t1[i].Point.Y == t2[i].Point.Y
                     && t1[i].Point.Z == t2[i].Point.Z, "点逐位不确定 @" + i);
            }
            // Build 兼容层必须与 BuildTagged 给出相同 3D 点
            var legacy = YrFaceClipping.Build(a, b, hit);
            Require(legacy.Length == t1.Length, "兼容 Build 点数不一致");
            for (int i = 0; i < legacy.Length; i++)
                Require(legacy[i].X == t1[i].Point.X
                     && legacy[i].Y == t1[i].Point.Y
                     && legacy[i].Z == t1[i].Point.Z, "兼容 Build 点不一致 @" + i);
        }

        public static void TestR8PointWarmCachePersistsAcrossFrames()
        {
            var bodies = new RigidBody3D[] {
                Make(RVec3.Zero, 1f), Make(new RVec3(1.8f, 0f, 0f), 1f)
            };
            var obbs = new YrObb[] {
                new YrObb(bodies[0].Position, bodies[0].Orientation, H(1f)),
                new YrObb(bodies[1].Position, bodies[1].Orientation, H(1f))
            };
            var narrow = new YrNarrowPhase { UsePointWarmCache = true };
            var pairs = YrBroadPhase.SweepAndPrune(obbs);
            var cs1 = narrow.Build(pairs, obbs, bodies, 1f / 60f, YrSurface.Default);
            Require(cs1.Length >= 1, "首帧应生成接触点");
            // 人为注入不同的 λ，模拟求解器结果
            for (int k = 0; k < cs1.Length; k++) cs1[k].NormalLambda = 3f + k;
            narrow.CacheWarm(cs1);
            Require(narrow.PointWarmCacheCount >= 1, "逐点缓存未写入");

            var cs2 = narrow.Build(pairs, obbs, bodies, 1f / 60f, YrSurface.Default);
            Require(cs2.Length == cs1.Length, "次帧点数应一致");
            for (int k = 0; k < cs2.Length; k++)
                Require(cs2[k].NormalLambda > 0f,
                        "次帧未吃到逐点 warm 缓存 @" + k);
        }

        public static void TestR8PointWarmExportImportIsDeterministic()
        {
            var narrow = new YrNarrowPhase { UsePointWarmCache = true };
            narrow.ImportWarmPoints(new (int, int, int, float)[] {
                (3, 7, 2, 1.5f), (1, 2, 0, 0.25f), (1, 2, 3, 0.75f)
            });
            var a = narrow.ExportWarmPoints();
            var b = narrow.ExportWarmPoints();
            Require(a.Length == 3 && b.Length == 3, "导出条目数错误");
            for (int i = 0; i < a.Length; i++)
                Require(a[i].Item1 == b[i].Item1 && a[i].Item2 == b[i].Item2
                     && a[i].Item3 == b[i].Item3 && a[i].Item4 == b[i].Item4,
                        "导出顺序/值不确定 @" + i);
            Require(a[0].Item1 == 1 && a[0].Item2 == 2 && a[0].Item3 == 0,
                    "未按 (A,B,Id) 字典序导出");
            narrow.ClearWarmCache();
            Require(narrow.PointWarmCacheCount == 0, "ClearWarmCache 未清逐点缓存");
        }

        public static void TestR8LegacySnapshotStillRestores()
        {
            var bodies = new RigidBody3D[] {
                Make(new RVec3(-1.2f, 0f, 0f), 1f), Make(new RVec3(1.2f, 0f, 0f), 1f)
            };
            var pipe = new YrPhysicsPipeline(bodies, new RVec3[] { H(1f), H(1f) });
            pipe.SleepEnabled = false;
            var snap = pipe.Snapshot();
            // 手工截掉末尾逐点段，构造第七轮及以前的旧格式快照
            int warmCount = (int)snap[2 + 14 * bodies.Length];
            int legacyLen = 2 + 14 * bodies.Length + 1 + 3 * warmCount;
            var legacy = new float[legacyLen];
            Array.Copy(snap, legacy, legacyLen);
            bodies[0].LinearVelocity = new RVec3(9f, 0f, 0f);
            pipe.Restore(legacy);
            Require(Math.Abs(bodies[0].LinearVelocity.X) < 1e-6f,
                    "旧格式快照应能恢复（向前兼容）");
        }

        public static void TestR8DefaultSwitchesKeepLegacyBehavior()
        {
            var bodies = new RigidBody3D[] {
                Make(RVec3.Zero, 1f), Make(new RVec3(1.8f, 0f, 0f), 1f)
            };
            var pipe = new YrPhysicsPipeline(bodies, new RVec3[] { H(1f), H(1f) });
            Require(!pipe.UsePointWarmCache && !pipe.UseDynamicTree,
                    "两个新开关默认必须关闭");
            pipe.SleepEnabled = false;
            pipe.StepFixed(1f / 60f);
            Require(pipe.PointWarmCacheCount == 0, "默认路径不应写逐点缓存");
            Require(pipe.LastDynamicTreeHeight == 0, "默认路径不应建动态树");
            var snap = pipe.Snapshot();
            int warmCount = (int)snap[2 + 14 * bodies.Length];
            int wpIndex = 2 + 14 * bodies.Length + 1 + 3 * warmCount;
            Require(wpIndex < snap.Length && snap[wpIndex] == 0f,
                    "默认路径逐点段长度应为 0");
        }

        public static void TestR8DynamicTreePipelineMatchesSap()
        {
            YrPhysicsPipeline MakePipe(bool dyn)
            {
                var b = new RigidBody3D[] {
                    Make(RVec3.Zero, 1f), Make(new RVec3(1.8f, 0f, 0f), 1f),
                    Make(new RVec3(9f, 9f, 0f), 1f)
                };
                var p = new YrPhysicsPipeline(b, new RVec3[] { H(1f), H(1f), H(1f) });
                p.SleepEnabled = false; p.UseDynamicTree = dyn;
                return p;
            }
            var a = MakePipe(false); var b = MakePipe(true);
            for (int k = 0; k < 6; k++)
            {
                a.StepFixed(1f / 60f); b.StepFixed(1f / 60f);
                ulong ha = YrRollbackBuffer.Checksum(a.Snapshot(), a.Tick);
                ulong hb = YrRollbackBuffer.Checksum(b.Snapshot(), b.Tick);
                Require(ha == hb, "动态树路径与 SAP 路径逐位分叉 @" + (k + 1));
            }
            Require(b.LastDynamicTreeHeight > 0, "动态树应已构建");
        }

        public static void TestR8PointWarmRollbackReplay()
        {
            var bodies = new RigidBody3D[] {
                Make(RVec3.Zero, 1f), Make(new RVec3(1.8f, 0f, 0f), 1f)
            };
            var pipe = new YrPhysicsPipeline(bodies, new RVec3[] { H(1f), H(1f) });
            pipe.SleepEnabled = false;
            pipe.UsePointWarmCache = true;
            var hashes = new ulong[11];
            hashes[0] = YrRollbackBuffer.Checksum(pipe.Snapshot(), 0);
            pipe.SaveFrame();
            for (int k = 0; k < 10; k++)
            {
                pipe.StepFixed(1f / 60f);
                pipe.SaveFrame();
                hashes[k + 1] = YrRollbackBuffer.Checksum(pipe.Snapshot(), pipe.Tick);
            }
            Require(pipe.PointWarmCacheCount > 0, "逐点缓存应在多帧后非空");
            Require(pipe.RewindTo(5), "未找到 tick 5 快照");
            Require(pipe.Tick == 5, "回滚后 tick 错误");
            for (int k = 5; k < 10; k++)
            {
                pipe.StepFixed(1f / 60f);
                ulong h = YrRollbackBuffer.Checksum(pipe.Snapshot(), pipe.Tick);
                Require(h == hashes[k + 1], "逐点 warm 回滚重放分叉 @" + (k + 1));
            }
        }
    }
}
