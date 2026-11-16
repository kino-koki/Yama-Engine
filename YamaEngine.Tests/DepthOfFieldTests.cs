// DepthOfFieldTests.cs — UE5.8 景深 (DoF) 子系统深度验证
// 覆盖：ComputeCoC 薄镜头散焦圆（对焦面=0、近/远场 smoothstep 单调增长、饱和上限、深度<=0 守卫）、
// BokehKernel 圆形/六边形/多边形样本（数量、归一化权重、中心存在）。

using System;
using System.Numerics;
using YamaEngine.Rendering;
using static YamaEngine.Rendering.BokehKernel;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class DepthOfFieldTests
    {
        private static void AssertNear(float got, float want, float tol, string msg)
            => AssertTrue(MathF.Abs(got - want) <= tol,
                $"{msg} (got {got:F4}, want {want:F4}±{tol:F4})");

        // ── 对焦面上 CoC=0；深度<=0 守卫返回 0 ──
        public static void TestCoCFocusPlaneZero()
        {
            float coc = ComputeCoC(depth: 10f, focusDistance: 10f, nearFocusStart: 5f, farFocusStart: 5f, nearBlurSize: 8f, farBlurSize: 8f, focalLength: 2.5f);
            AssertNear(coc, 0f, 1e-4f, "CoC at focus plane must be 0.");
            AssertNear(ComputeCoC(0f, 10f, 5f, 5f, 8f, 8f, 2.5f), 0f, 1e-4f, "CoC at depth<=0 must be 0.");
            AssertNear(ComputeCoC(-1f, 10f, 5f, 5f, 8f, 8f, 2.5f), 0f, 1e-4f, "CoC at negative depth must be 0.");
        }

        // ── 近场：深度朝相机减小，CoC 单调增长并饱和到 nearBlurSize ──
        public static void TestCoCNearFieldMonotonicSaturates()
        {
            const float nearBlur = 10f;
            float close = ComputeCoC(1f, 10f, 5f, 5f, nearBlur, 10f, 2.5f);
            float mid = ComputeCoC(5f, 10f, 5f, 5f, nearBlur, 10f, 2.5f);
            float atFocus = ComputeCoC(9.9f, 10f, 5f, 5f, nearBlur, 10f, 2.5f);
            AssertTrue(atFocus < mid, "CoC must increase as depth moves away from focus.");
            AssertTrue(mid <= close, "Near CoC must be monotonic non-decreasing toward camera.");
            // 距离超过 nearStart → t=1 → 饱和到 nearBlurSize
            AssertNear(close, nearBlur, 0.01f, "Near CoC must saturate to nearBlurSize.");
        }

        // ── 远场：深度增大，CoC 单调增长并饱和到 farBlurSize ──
        public static void TestCoCFarFieldMonotonicSaturates()
        {
            const float farBlur = 6f;
            float near = ComputeCoC(10.1f, 10f, 5f, 5f, 10f, farBlur, 2.5f);
            float mid = ComputeCoC(12f, 10f, 5f, 5f, 10f, farBlur, 2.5f);
            float far = ComputeCoC(100f, 10f, 5f, 5f, 10f, farBlur, 2.5f);
            AssertTrue(near < mid && mid <= far, "Far CoC must be monotonic increasing with depth.");
            AssertNear(far, farBlur, 0.01f, "Far CoC must saturate to farBlurSize.");
        }

        // ── 圆形 Bokeh 样本：数量、权重和≈1、含中心点 ──
        public static void TestBokehCircularSamples()
        {
            var samples = BokehKernel.GenerateSamples(BokehShape.Circular, bladeCount: 0, maxRadius: 5f, sampleCount: 64);
            AssertEqual(64, samples.Length, "Circle must produce sampleCount samples.");
            float totalW = 0;
            bool hasCenter = false;
            foreach (var (off, w) in samples)
            {
                totalW += w;
                if (off.Length() < 1e-3f) hasCenter = true;
            }
            AssertNear(totalW, 1f, 0.02f, "Bokeh sample weights must sum to ~1.");
            AssertTrue(hasCenter, "Circle bokeh must include a center sample.");
        }

        // ── 六边形样本：数量正确、权重归一化 ──
        public static void TestBokehHexagonalSamples()
        {
            var samples = BokehKernel.GenerateSamples(BokehShape.Hexagonal, 0, 5f, 61);
            AssertTrue(samples.Length > 0, "Hexagonal must produce samples.");
            float totalW = 0;
            foreach (var (_, w) in samples) totalW += w;
            AssertNear(totalW, 1f, 0.02f, "Hexagonal weights must sum to ~1.");
            // 所有样本都在 maxRadius 内
            foreach (var (off, _) in samples)
                AssertTrue(off.Length() <= 5f + 1e-2f, "Hex samples must lie within maxRadius.");
        }

        // ── 叶片（多边形）样本：数量正确、权重归一化、落在多边形内 ──
        public static void TestBokehBladeSamples()
        {
            var samples = BokehKernel.GenerateSamples(BokehShape.Blade, bladeCount: 6, maxRadius: 4f, sampleCount: 128);
            AssertTrue(samples.Length > 0, "Blade must produce samples.");
            float totalW = 0;
            foreach (var (_, w) in samples) totalW += w;
            AssertNear(totalW, 1f, 0.02f, "Blade weights must sum to ~1.");
            foreach (var (off, _) in samples)
                AssertTrue(off.Length() <= 4f + 1e-2f, "Blade samples must lie within maxRadius.");
        }
    }
}
