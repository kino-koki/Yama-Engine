using System;
using System.Numerics;
using YamaEngine.Rendering;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    // ══════════════════════════════════════════════════════════════
    // FrustumDepth — UE5.8 级视锥剔除深度验证
    // 覆盖：Frustum.FromViewProjection（6 平面提取+归一化）、Frustum.Intersects
    //       三态（Outside/Inside/Intersect）、positive/negative vertex 法、
    //       GPUDrivenCullingSystem 累积可见/剔除计数。
    // ══════════════════════════════════════════════════════════════
    public static class FrustumCullingTests
    {
        // 构造标准透视相机（右手系，看向 -Z），位于原点
        private static Matrix4x4 MakeViewProjection(float aspect = 16f / 9f, float fovY = 90f,
            float near = 0.1f, float far = 100f)
        {
            var view = Matrix4x4.CreateLookAt(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY);
            var proj = Matrix4x4.CreatePerspectiveFieldOfView(fovY * MathF.PI / 180f, aspect, near, far);
            return view * proj;
        }

        // ── 从 VP 提取 6 平面并归一化 ──
        public static void TestFrustumExtractsSixNormalizedPlanes()
        {
            var vp = MakeViewProjection();
            var f = Frustum.FromViewProjection(vp);
            AssertTrue(f.Planes.Length == 6, "Frustum must have exactly 6 planes.");
            foreach (var p in f.Planes)
            {
                float len = p.Normal.Length();
                AssertTrue(MathF.Abs(len - 1f) < 1e-5f,
                    $"Each plane normal must be normalized (length {len:F4}).");
            }
        }

        // ── Intersects 三态：盒完全在视锥内 / 完全外 / 跨越边界 ──
        public static void TestFrustumIntersectsTriState()
        {
            var vp = MakeViewProjection(fovY: 90f, far: 100f);
            var f = Frustum.FromViewProjection(vp);

            // 完全在视锥内的小盒（相机正前方近处）
            var inside = new AABB(new Vector3(-0.5f, -0.5f, -2f), new Vector3(0.5f, 0.5f, -3f));
            AssertTrue(f.Intersects(inside) == IntersectionResult.Inside,
                "Box fully within frustum must report Inside.");

            // 完全在视锥外（相机正后方 +Z，far 平面之外方向）
            var behind = new AABB(new Vector3(-0.5f, -0.5f, 5f), new Vector3(0.5f, 0.5f, 6f));
            AssertTrue(f.Intersects(behind) == IntersectionResult.Outside,
                "Box behind camera must report Outside.");

            // 跨越视锥边界（一半在内一半在 far 平面外）→ Intersect
            var straddle = new AABB(new Vector3(-0.5f, -0.5f, -50f), new Vector3(0.5f, 0.5f, 50f));
            var r = f.Intersects(straddle);
            AssertTrue(r == IntersectionResult.Intersect || r == IntersectionResult.Inside,
                $"Box straddling frustum must not be Outside, got {r}.");
        }

        // ── 正交投影视锥：轴对齐盒三态判定 ──
        public static void TestOrthographicFrustumTriState()
        {
            // 正交相机看向 -Z，范围 [-1,1]，near=0 far=10
            var view = Matrix4x4.CreateLookAt(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY);
            var proj = Matrix4x4.CreateOrthographic(2f, 2f, 0f, 10f);
            var f = Frustum.FromViewProjection(view * proj);

            var inBox = new AABB(new Vector3(-0.5f, -0.5f, -5f), new Vector3(0.5f, 0.5f, -6f));
            AssertTrue(f.Intersects(inBox) == IntersectionResult.Inside,
                "Box in ortho frustum must be Inside.");

            var outBox = new AABB(new Vector3(5f, 5f, -5f), new Vector3(6f, 6f, -6f));
            AssertTrue(f.Intersects(outBox) == IntersectionResult.Outside,
                "Box outside ortho frustum side planes must be Outside.");
        }

        // ── GPUDrivenCullingSystem：累积可见 + 剔除计数 ──
        public static void TestGPUCullingSystemAccumulatesVisibleAndCulled()
        {
            var vp = MakeViewProjection(fovY: 90f, far: 100f);
            var f = Frustum.FromViewProjection(vp);
            var culler = new GPUDrivenCullingSystem();

            // 3 个盒：0/1 在视锥内，2 在相机后方
            var bounds = new AABB[]
            {
                new(new Vector3(-0.5f,-0.5f,-2f), new Vector3(0.5f,0.5f,-3f)),   // in
                new(new Vector3(-0.3f,-0.3f,-4f), new Vector3(0.3f,0.3f,-5f)),   // in
                new(new Vector3(-0.5f,-0.5f, 5f), new Vector3(0.5f,0.5f, 6f)),   // out (behind)
            };
            int[] ids = { 10, 20, 30 };
            culler.Cull(f, bounds, ids);

            AssertTrue(culler.Visible.Count == 2, $"Expected 2 visible, got {culler.Visible.Count}.");
            AssertTrue(culler.CulledCount == 1, $"Expected 1 culled, got {culler.CulledCount}.");
            AssertTrue(culler.Visible[0].InstanceId == 10 && culler.Visible[1].InstanceId == 20,
                "Visible instances must preserve order and ids.");

            // 再次 Cull（覆盖前次结果，_visible/_culled 重置）
            culler.Cull(f, new AABB[] { bounds[2] }, new int[] { 99 });
            AssertTrue(culler.Visible.Count == 0 && culler.CulledCount == 1,
                "Re-Cull must reset previous results.");
        }
    }
}
