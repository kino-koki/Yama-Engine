// StreamingTests.cs — UE5 世界流送子系统深度验证
// 覆盖：StreamingPriority（强制加载/滞后/距离/方向/大小综合优先级）、
// PredictiveStreamingLoader（速度估计+前瞻预测+静止不预测+Reset）、
// WorldComposition（CreateLevel/GetLevelsNear按距离排序）、VisibilityManager（视锥+距离剔除）。
// 全部纯 CPU，确定性数值验证。

using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.World;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class StreamingTests
    {
        private static void AssertNear(float got, float want, float tol, string msg)
            => AssertTrue(MathF.Abs(got - want) <= tol,
                $"{msg} (got {got:F4}, want {want:F4}±{tol:F4})");

        // ── StreamingPriority：强制加载 → -1000（最高优先级）──
        public static void TestStreamingPriorityGameplayForced()
        {
            float p = StreamingPriority.Calculate(distance: 9999f, inView: false,
                isHysteresis: false, requiredByGameplay: true, size: 100f);
            AssertNear(p, -1000f, 0.01f, "Gameplay-forced level priority must be -1000.");
        }

        // ── StreamingPriority：滞后保护期 → -500 ──
        public static void TestStreamingPriorityHysteresis()
        {
            float p = StreamingPriority.Calculate(distance: 9999f, inView: false,
                isHysteresis: true, requiredByGameplay: false, size: 100f);
            AssertNear(p, -500f, 0.01f, "Hysteresis level priority must be -500.");
        }

        // ── StreamingPriority：距离越近越优先（近 > 远）──
        public static void TestStreamingPriorityDistanceOrdering()
        {
            float near = StreamingPriority.Calculate(10f, inView: false, isHysteresis: false,
                requiredByGameplay: false, size: 0f);
            float far = StreamingPriority.Calculate(100f, inView: false, isHysteresis: false,
                requiredByGameplay: false, size: 0f);
            AssertTrue(near < far, $"Near level ({near:F2}) must be more prioritized than far ({far:F2}).");
        }

        // ── StreamingPriority：在视野方向加成（inView 减 50）──
        public static void TestStreamingPriorityInViewBonus()
        {
            float inView = StreamingPriority.Calculate(50f, inView: true, isHysteresis: false,
                requiredByGameplay: false, size: 0f);
            float outOfView = StreamingPriority.Calculate(50f, inView: false, isHysteresis: false,
                requiredByGameplay: false, size: 0f);
            AssertNear(inView, outOfView - 50f, 0.01f,
                "InView bonus must reduce priority by 50.");
        }

        // ── StreamingPriority：优先级公式 distance - 50*inView - size*0.01 ──
        public static void TestStreamingPriorityFormula()
        {
            float p = StreamingPriority.Calculate(200f, inView: true, isHysteresis: false,
                requiredByGameplay: false, size: 500f);
            // 200 - 50 - 500*0.01 = 200 - 50 - 5 = 145
            AssertNear(p, 145f, 0.01f, "Priority formula: distance + viewFactor + sizeFactor.");
        }

        // ── IsInViewDirection：目标在相机前方 → true ──
        public static void TestIsInViewDirectionForward()
        {
            var camPos = new Vector3(0, 0, 0);
            var forward = new Vector3(0, 0, -1); // 看 -Z
            var target = new Vector3(0, 0, -100); // 正前方
            bool result = StreamingPriority.IsInViewDirection(camPos, forward, target, fovHalfAngle: 1.0f);
            AssertTrue(result, "Target directly in front must be in view direction.");
        }

        // ── IsInViewDirection：目标在正后方 → false ──
        public static void TestIsInViewDirectionBehind()
        {
            var camPos = new Vector3(0, 0, 0);
            var forward = new Vector3(0, 0, -1);
            var target = new Vector3(0, 0, 100); // 正后方
            bool result = StreamingPriority.IsInViewDirection(camPos, forward, target, fovHalfAngle: 1.0f);
            AssertTrue(!result, "Target directly behind must NOT be in view direction.");
        }

        // ── IsInViewDirection：目标偏离 FOV 边界 → false ──
        public static void TestIsInViewDirectionOutsideFOV()
        {
            var camPos = new Vector3(0, 0, 0);
            var forward = new Vector3(0, 0, -1);
            // fovHalfAngle=1.0 rad ≈ 57°，目标偏离 90°（+X 方向）→ cos(90°)=0 < cos(1.0)≈0.54
            var target = new Vector3(100, 0, 0);
            bool result = StreamingPriority.IsInViewDirection(camPos, forward, target, fovHalfAngle: 1.0f);
            AssertTrue(!result, "Target at 90° must be outside 57° half-FOV.");
        }

        // ── PredictiveStreamingLoader：匀速移动 → 预测位置沿速度方向延伸 ──
        public static void TestPredictiveLoaderVelocityPrediction()
        {
            var loader = new PredictiveStreamingLoader
            {
                LookAheadSeconds = 2f,
                MinSpeedThreshold = 1f,
            };
            // 模拟 30 帧沿 +X 匀速移动，速度 = 10/s
            Vector3 pos = new Vector3(0, 0, 0);
            float dt = 1f / 30f;
            for (int i = 0; i < 30; i++)
            {
                pos.X += 10f * dt; // 每帧移动 10*dt
                loader.Update(pos, dt);
            }
            AssertTrue(loader.HasPrediction, "PredictiveLoader should have prediction for constant +X motion.");
            // 预测位置应在当前位置前方（+X 方向）
            AssertTrue(loader.PredictedPosition.X > pos.X,
                $"Predicted position X ({loader.PredictedPosition.X:F2}) must be ahead of current ({pos.X:F2}).");
            // 速度应大致正确（10/s 沿 X）
            AssertTrue(loader.CurrentVelocity.X > 5f,
                $"Smoothed velocity X should be near 10 (got {loader.CurrentVelocity.X:F2}).");
        }

        // ── PredictiveStreamingLoader：静止 → 无预测 ──
        public static void TestPredictiveLoaderStationaryNoPrediction()
        {
            var loader = new PredictiveStreamingLoader
            {
                LookAheadSeconds = 2f,
                MinSpeedThreshold = 5f,
            };
            var pos = new Vector3(10, 5, -3);
            float dt = 1f / 30f;
            for (int i = 0; i < 30; i++)
            {
                loader.Update(pos, dt); // 位置不变
            }
            AssertTrue(!loader.HasPrediction, "Stationary viewer must not have prediction.");
            // 预测位置等于当前位置（速度≈0）
            AssertNear(loader.PredictedPosition.X, pos.X, 0.5f, "Stationary: predicted X stays near current.");
        }

        // ── PredictiveStreamingLoader：Reset 清空状态 ──
        public static void TestPredictiveLoaderReset()
        {
            var loader = new PredictiveStreamingLoader
            {
                LookAheadSeconds = 2f,
                MinSpeedThreshold = 1f,
            };
            Vector3 pos = new Vector3(0, 0, 0);
            float dt = 1f / 30f;
            for (int i = 0; i < 30; i++)
            {
                pos.X += 10f * dt;
                loader.Update(pos, dt);
            }
            loader.Reset();
            AssertNear(loader.CurrentVelocity.Length(), 0f, 0.001f, "Reset must zero velocity.");
            AssertNear(loader.PredictedPosition.Length(), 0f, 0.001f, "Reset must zero predicted position.");
        }

        // ── WorldComposition：CreateLevel + GetLevelsNear 按距离排序 ──
        public static void TestWorldCompositionGetLevelsNearSorted()
        {
            var comp = new WorldComposition();
            // Level A 在原点附近，Level B 在远处，Level C 在中等距离
            var a = comp.CreateLevel("NearLevel", WorldBounds.FromCenterSize(new Vector3(0, 0, 0), new Vector3(50, 50, 50)));
            var b = comp.CreateLevel("FarLevel", WorldBounds.FromCenterSize(new Vector3(1000, 0, 0), new Vector3(50, 50, 50)));
            var c = comp.CreateLevel("MidLevel", WorldBounds.FromCenterSize(new Vector3(200, 0, 0), new Vector3(50, 50, 50)));
            AssertTrue(comp.Count == 3, "WorldComposition must contain 3 levels.");

            // 从原点查询 maxDistance=300 的附近级别
            var near = comp.GetLevelsNear(new Vector3(0, 0, 0), 300f);
            // A 中心 0（距离 0），C 中心 200（距离 175），B 中心 1000（距离 975>300，排除）
            AssertTrue(near.Count == 2, $"GetLevelsNear(300) should return 2 levels (got {near.Count}).");
            // 按距离排序：A 在 C 前
            AssertTrue(near[0].Name == "NearLevel",
                $"First near level should be NearLevel (got {near[0].Name}).");
            AssertTrue(near[1].Name == "MidLevel",
                $"Second near level should be MidLevel (got {near[1].Name}).");
        }

        // ── WorldComposition：UpdatePriorities 计算优先级（BoundsCenter 驱动 ComputePriority）──
        public static void TestWorldCompositionUpdatePriorities()
        {
            var comp = new WorldComposition();
            var a = comp.CreateLevel("NearLevel", WorldBounds.FromCenterSize(new Vector3(0, 0, 0), new Vector3(50, 50, 50)));
            var b = comp.CreateLevel("FarLevel", WorldBounds.FromCenterSize(new Vector3(1000, 0, 0), new Vector3(50, 50, 50)));
            // CreateLevel 只设 Bounds，ComputePriority 用 BoundsCenter，需手动同步
            a.BoundsCenter = new Vector3(0, 0, 0);
            b.BoundsCenter = new Vector3(1000, 0, 0);
            AssertNear(b.Priority, 0f, 0.01f, "Priority must be 0 before UpdatePriorities.");
            comp.UpdatePriorities(new Vector3(0, 0, 0));
            // 近的优先级数值上更低（越低越优先）
            AssertNear(a.Priority, 0f, 0.01f, "NearLevel at origin: priority = dist 0.");
            AssertNear(b.Priority, 1000f, 0.01f, "FarLevel at 1000: priority = dist 1000.");
            AssertTrue(a.Priority < b.Priority,
                $"Near level priority ({a.Priority:F2}) must be lower than far ({b.Priority:F2}).");
        }

        // ── VisibilityManager：距离剔除（超出最大可见距离）──
        public static void TestVisibilityManagerDistanceCulling()
        {
            var vm = new VisibilityManager
            {
                HysteresisFrames = 1,
                MaxVisibilityDistance = 100f,
            };
            // 创建一个远处关卡
            var level = new StreamingLevel
            {
                LevelName = "FarLevel",
                BoundsCenter = new Vector3(500, 0, 0),
                LoadRadius = 200f,
            };
            level.Bounds = new YamaEngine.Rendering.BoundingBox(
                new Vector3(450, -50, -50), new Vector3(550, 50, 50));
            // 不设置视锥矩阵（保持 Identity）→ 距离剔除生效
            level.IsFrustumVisible = true; // 初始可见
            var result = vm.UpdateVisibility(level, new Vector3(0, 0, 0));
            AssertTrue(!level.IsFrustumVisible, "Level beyond MaxVisibilityDistance must be culled.");
        }

        // ── VisibilityManager：视锥内关卡保持可见 ──
        public static void TestVisibilityManagerInFrustum()
        {
            var vm = new VisibilityManager
            {
                HysteresisFrames = 3,
                MaxVisibilityDistance = 2000f,
            };
            var level = new StreamingLevel
            {
                LevelName = "FrontLevel",
                BoundsCenter = new Vector3(0, 0, -100), // 正前方
                LoadRadius = 500f,
            };
            level.Bounds = new YamaEngine.Rendering.BoundingBox(
                new Vector3(-50, -50, -150), new Vector3(50, 50, -50));
            // 设置一个看向 -Z 的视锥矩阵（identity 简化）
            vm.SetViewProjection(Matrix4x4.Identity);
            level.IsFrustumVisible = false;
            level.InvisibleFrames = 0;
            var result = vm.UpdateVisibility(level, new Vector3(0, 0, 0));
            AssertTrue(level.IsFrustumVisible, "Level in front with Identity VP must be visible.");
        }

        // ══════════════════════════════════════════════════════════════════
        // WorldStreamingGrid（网格流送）
        // ══════════════════════════════════════════════════════════════════

        // ── WorldStreamingGrid：WorldToGrid 网格坐标 floor 除法（纯函数）──
        public static void TestStreamingGridGetCellAt()
        {
            var grid = new WorldStreamingGridSystem();
            // CellSize=256，点 (300, 0, 100) → grid (1, 0, 0)
            var g = grid.WorldToGrid(new Vector3(300, 0, 100));
            AssertNear(g.x, 1, 0.01f, "Grid X must be floor(300/256)=1.");
            AssertNear(g.y, 0, 0.01f, "Grid Y must be floor(0/256)=0.");
            AssertNear(g.z, 0, 0.01f, "Grid Z must be floor(100/256)=0.");
            // 负坐标：(-300, 0, -100) → grid (-2, 0, -1)
            var gn = grid.WorldToGrid(new Vector3(-300, 0, -100));
            AssertNear(gn.x, -2, 0.01f, "Negative X must floor to -2.");
            AssertNear(gn.z, -1, 0.01f, "Negative Z must floor to -1.");
        }

        // ── WorldStreamingGrid：ForceLoadCell 状态转换 + ActiveCellCount ──
        public static void TestStreamingGridForceLoadCell()
        {
            var grid = new WorldStreamingGridSystem();
            int activeBefore = grid.ActiveCellCount;
            grid.ForceLoadCell(2, 0, 3);
            var cell = grid.GetOrCreateCell(2, 0, 3);
            AssertTrue(cell.State == CellState.Loaded, "ForceLoadCell must set State=Loaded.");
            AssertNear(cell.LoadProgress, 1f, 0.01f, "ForceLoadCell must set LoadProgress=1.");
            AssertTrue(grid.ActiveCellCount > activeBefore,
                "ForceLoadCell must add cell to active set.");
        }

        // ── WorldStreamingGrid：RegisterEntity 关联到网格单元 ──
        public static void TestStreamingGridRegisterEntity()
        {
            var grid = new WorldStreamingGridSystem();
            int entityId = 42;
            var pos = new Vector3(100, 0, 100); // → cell (0,0,0)
            grid.RegisterEntity(entityId, pos);
            var cell = grid.GetOrCreateCell(0, 0, 0);
            AssertTrue(cell.EntityIds.Contains(entityId),
                "RegisterEntity must add entityId to cell EntityIds.");
            // 目标 cell 先 ForceLoad 让它存在（GetCellAt 只查不建）
            grid.ForceLoadCell(2, 0, 0);
            // 移动到已存在的 cell → 应从旧 cell 移除、加入新 cell
            grid.UpdateEntityPosition(entityId, new Vector3(600, 0, 0)); // → cell (2,0,0)
            var oldCell = grid.GetOrCreateCell(0, 0, 0);
            var newCell = grid.GetOrCreateCell(2, 0, 0);
            AssertTrue(!oldCell.EntityIds.Contains(entityId),
                "UpdateEntityPosition must remove from old cell.");
            AssertTrue(newCell.EntityIds.Contains(entityId),
                "UpdateEntityPosition must add to new cell.");
        }

        // ── WorldStreamingGrid：GetCellsInRadius 返回半径内单元 ──
        public static void TestStreamingGridGetCellsInRadius()
        {
            var grid = new WorldStreamingGridSystem();
            grid.GetOrCreateCell(0, 0, 0);
            grid.GetOrCreateCell(1, 0, 0);
            grid.GetOrCreateCell(2, 0, 0);
            var cells = grid.GetCellsInRadius(new Vector3(0, 0, 0), radiusInCells: 1);
            AssertTrue(cells.Count >= 1, "GetCellsInRadius must return at least the center cell.");
            // 至少包含原点 cell (0,0,0)
            bool hasOrigin = false;
            foreach (var c in cells)
                if (c.GridX == 0 && c.GridY == 0 && c.GridZ == 0) hasOrigin = true;
            AssertTrue(hasOrigin, "GetCellsInRadius must include origin cell.");
        }

        // ══════════════════════════════════════════════════════════════════
        // StreamingLevel.SelectLod（LOD 距离选择）
        // ══════════════════════════════════════════════════════════════════

        // ── SelectLod：4 分支距离阈值（LoadRadius=100 → 35/70/100）──
        public static void TestSelectLodDistanceBranches()
        {
            var level = new StreamingLevel
            {
                BoundsCenter = new Vector3(0, 0, 0),
                LoadRadius = 100f,
            };
            // dist 20 < 100*0.35=35 → High
            AssertTrue(level.SelectLod(new Vector3(20, 0, 0)) == StreamingLodQuality.High,
                "dist 20 must select High LOD (<0.35*LoadRadius).");
            // dist 50, 35<=50<70 → Medium
            AssertTrue(level.SelectLod(new Vector3(50, 0, 0)) == StreamingLodQuality.Medium,
                "dist 50 must select Medium LOD (0.35~0.7*LoadRadius).");
            // dist 85, 70<=85<100 → Low
            AssertTrue(level.SelectLod(new Vector3(85, 0, 0)) == StreamingLodQuality.Low,
                "dist 85 must select Low LOD (0.7~1.0*LoadRadius).");
            // dist 500 >= 100 → Hlod
            AssertTrue(level.SelectLod(new Vector3(500, 0, 0)) == StreamingLodQuality.Hlod,
                "dist 500 must select Hlod LOD (>LoadRadius).");
        }

        // ── SelectLod：阈值边界精度（dist 恰在 0.35/0.7 转换点）──
        public static void TestSelectLodBoundaryPrecision()
        {
            var level = new StreamingLevel
            {
                BoundsCenter = new Vector3(0, 0, 0),
                LoadRadius = 100f,
            };
            // dist 恰为 35（0.35*100）→ 不满足 <35 → Medium
            AssertTrue(level.SelectLod(new Vector3(35, 0, 0)) == StreamingLodQuality.Medium,
                "dist exactly 0.35*LoadRadius must select Medium (not High).");
            // dist 恰为 70（0.7*100）→ 不满足 <70 → Low
            AssertTrue(level.SelectLod(new Vector3(70, 0, 0)) == StreamingLodQuality.Low,
                "dist exactly 0.7*LoadRadius must select Low (not Medium).");
            // dist 恰为 100 → 不满足 <100 → Hlod
            AssertTrue(level.SelectLod(new Vector3(100, 0, 0)) == StreamingLodQuality.Hlod,
                "dist exactly LoadRadius must select Hlod (not Low).");
        }

        // ── ShouldLoadByDistance：距离 <= LoadRadius 才加载 ──
        public static void TestShouldLoadByDistance()
        {
            var level = new StreamingLevel
            {
                BoundsCenter = new Vector3(0, 0, 0),
                LoadRadius = 100f,
            };
            AssertTrue(level.ShouldLoadByDistance(new Vector3(50, 0, 0)),
                "dist 50 must be within LoadRadius 100 → should load.");
            AssertTrue(!level.ShouldLoadByDistance(new Vector3(200, 0, 0)),
                "dist 200 must be beyond LoadRadius 100 → should not load.");
        }
    }
}
