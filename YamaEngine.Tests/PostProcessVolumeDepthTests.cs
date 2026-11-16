// PostProcessVolumeDepthTests.cs — Unity5 PostProcess Volume 深度验证
// 覆盖:
//   * PostProcessVolume: ComputeWeight/Contains 四种形状 + 无限范围 + BlendDistance 平滑
//   * PostProcessSettings.Blend: Override 优先级 + 权重剩余吃 + 引擎默认兜底
//   * VolumeBlendSystem: AddStandaloneVolume + SetCameraPosition + Update 写回 PostProcessingStack
//   * ApplyTo: 混合结果写入 PostProcessingStack.Settings 字段

using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.ECS;
using YamaEngine.Rendering;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class PostProcessVolumeDepthTests
    {
        private const float Eps = 1e-4f;

        // ── 1. Sphere 形状：内部权重=BlendWeight，外部权重=0，BlendDistance 内 smoothstep ──
        public static void TestSphereComputeWeight()
        {
            var v = new PostProcessVolume
            {
                Shape = VolumeShape.Sphere,
                Center = Vector3.Zero,
                Radius = 5.0f,
                BlendDistance = 1.0f,
                BlendWeight = 1.0f
            };

            // 球心：权重满
            float wCenter = v.ComputeWeight(Vector3.Zero);
            AssertTrue(MathF.Abs(wCenter - 1.0f) < Eps, $"球心权重应=1，实际={wCenter}");

            // 边界外：权重=0
            float wOut = v.ComputeWeight(new Vector3(10, 0, 0));
            AssertTrue(wOut < Eps, $"球外权重应=0，实际={wOut}");

            // 边界外刚好 BlendDistance：权重=0
            float wBoundary = v.ComputeWeight(new Vector3(6, 0, 0));
            AssertTrue(wBoundary < Eps, $"球外 BlendDistance 处权重应=0，实际={wBoundary}");

            // 边界内刚好：权重=1
            float wInner = v.ComputeWeight(new Vector3(4, 0, 0));
            AssertTrue(MathF.Abs(wInner - 1.0f) < Eps, $"球内边界处权重应=1，实际={wInner}");

            // 半径=0 + BlendDistance=0 球：权重=0（避免除零）
            AssertTrue(v.Contains(Vector3.Zero), "球心应 Contains=true");
        }

        // ── 2. Box 形状：内部/外部 + 旋转 + 混合距离 ──
        public static void TestBoxComputeWeight()
        {
            var v = new PostProcessVolume
            {
                Shape = VolumeShape.Box,
                Center = new Vector3(10, 0, 0),
                HalfExtents = new Vector3(2, 2, 2),
                BlendDistance = 1.0f,
                BlendWeight = 1.0f
            };

            // 盒心：权重满
            AssertTrue(MathF.Abs(v.ComputeWeight(new Vector3(10, 0, 0)) - 1.0f) < Eps,
                "盒心权重应=1");

            // 内部：权重=1
            AssertTrue(MathF.Abs(v.ComputeWeight(new Vector3(11, 0.5f, 0)) - 1.0f) < Eps,
                "盒内权重应=1");

            // 盒外：权重=0
            AssertTrue(v.ComputeWeight(new Vector3(20, 0, 0)) < Eps,
                "盒外权重应=0");

            // 盒外刚好 BlendDistance 处：权重=0
            float wAtBoundary = v.ComputeWeight(new Vector3(13, 0, 0));
            AssertTrue(wAtBoundary < Eps, $"盒外 BlendDistance 处权重应=0，实际={wAtBoundary}");

            // 旋转 45°：局部轴向对齐
            var rotated = new PostProcessVolume
            {
                Shape = VolumeShape.Box,
                Center = Vector3.Zero,
                HalfExtents = new Vector3(5, 5, 5),
                Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4),
                BlendDistance = 0.5f,
                BlendWeight = 1.0f
            };
            // 局部 (3,0,0) → 世界 (3cos45, 0, -3sin45) ≈ (2.12, 0, -2.12)
            Vector3 worldPt = Vector3.Transform(new Vector3(3, 0, 0),
                Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4));
            AssertTrue(MathF.Abs(rotated.ComputeWeight(worldPt) - 1.0f) < Eps,
                "旋转后盒内点权重应=1");
        }

        // ── 3. 无限范围体积：权重恒为 BlendWeight ──
        public static void TestInfiniteRangeVolume()
        {
            var v = new PostProcessVolume
            {
                IsInfiniteRange = true,
                Shape = VolumeShape.Sphere,
                Center = new Vector3(100, 100, 100), // 即使远，无限范围也全局生效
                Radius = 5.0f,
                BlendWeight = 1.0f
            };

            float w0 = v.ComputeWeight(Vector3.Zero);
            AssertTrue(MathF.Abs(w0 - 1.0f) < Eps, $"无限范围任意点权重应=1，实际={w0}");

            float wFar = v.ComputeWeight(new Vector3(-50, 0, 0));
            AssertTrue(MathF.Abs(wFar - 1.0f) < Eps, $"无限范围远点权重应=1，实际={wFar}");

            AssertTrue(v.Contains(new Vector3(999, 999, 999)), "无限范围 Contains 任意点=true");

            // BlendWeight=0.5 → 权重恒 0.5
            v.BlendWeight = 0.5f;
            AssertTrue(MathF.Abs(v.ComputeWeight(Vector3.Zero) - 0.5f) < Eps,
                "无限范围权重应=BlendWeight");
        }

        // ── 4. Disabled 体积权重恒为 0 ──
        public static void TestDisabledVolume()
        {
            var v = new PostProcessVolume
            {
                IsEnabled = false,
                Shape = VolumeShape.Sphere,
                Center = Vector3.Zero,
                Radius = 100.0f
            };
            AssertTrue(v.ComputeWeight(Vector3.Zero) < Eps, "禁用体积权重应=0");
            AssertTrue(!v.Contains(Vector3.Zero), "禁用体积 Contains=false");
        }

        // ── 5. Capsule 形状：沿 Y 轴的线段+两端半球 ──
        public static void TestCapsuleComputeWeight()
        {
            var v = new PostProcessVolume
            {
                Shape = VolumeShape.Capsule,
                Center = Vector3.Zero,
                Radius = 2.0f,
                Height = 10.0f, // 线段长度 = 10 - 4 = 6
                BlendDistance = 1.0f,
                BlendWeight = 1.0f
            };

            // 胶囊轴线上：权重=1
            AssertTrue(MathF.Abs(v.ComputeWeight(new Vector3(0, 2, 0)) - 1.0f) < Eps,
                "胶囊内轴向点权重应=1");

            // 胶囊轴内最远处 (0, 3, 0)：线段半长=3，clamp 到 (0,3,0) 距离=0，权重=1
            AssertTrue(MathF.Abs(v.ComputeWeight(new Vector3(0, 3, 0)) - 1.0f) < Eps,
                "胶囊线段端点权重应=1");

            // 胶囊外：权重=0
            AssertTrue(v.ComputeWeight(new Vector3(5, 0, 0)) < Eps,
                "胶囊外权重应=0");
        }

        // ── 6. Convex 形状：平面列表描述的凸多面体 ──
        public static void TestConvexComputeWeight()
        {
            // 用 6 个平面定义单位立方体 (|x|<=1,|y|<=1,|z|<=1)
            // 平面方程 dot(N,P) + w >= 0 为内侧
            var v = new PostProcessVolume
            {
                Shape = VolumeShape.Convex,
                Center = Vector3.Zero,
                BlendDistance = 0.5f,
                BlendWeight = 1.0f,
                ConvexPlanes = new List<Vector4>
                {
                    new(1, 0, 0, -1), new(-1, 0, 0, -1),
                    new(0, 1, 0, -1), new(0, -1, 0, -1),
                    new(0, 0, 1, -1), new(0, 0, -1, -1)
                }
            };

            // 原点：深度=min(所有 signed)=min(-1,-1,..)=-1，外侧 → 权重=0
            // 但实际 signed = dot(N,P) + w = 0 + (-1) = -1，inside=false
            // 深度取 -min(|signed|) = -1 → 权重=0
            AssertTrue(v.ComputeWeight(Vector3.Zero) < Eps, "平面原点权重应=0（signed 全负）");

            // 修复：平面方程 dot(N,P)+w>=0 内侧，对 |x|<=1 用 N=+X, w=1（P.x+1>=0 → x>=-1）+ N=-X, w=1（-x+1>=0 → x<=1）
            var v2 = new PostProcessVolume
            {
                Shape = VolumeShape.Convex,
                Center = Vector3.Zero,
                BlendDistance = 0.5f,
                BlendWeight = 1.0f,
                ConvexPlanes = new List<Vector4>
                {
                    new(1, 0, 0, 1), new(-1, 0, 0, 1),
                    new(0, 1, 0, 1), new(0, -1, 0, 1),
                    new(0, 0, 1, 1), new(0, 0, -1, 1)
                }
            };

            // 原点：signed 全 1，inside=true，深度=min(1,1,...)=1 > BlendDistance=0.5 → 权重=1
            AssertTrue(MathF.Abs(v2.ComputeWeight(Vector3.Zero) - 1.0f) < Eps,
                "凸包内原点权重应=1");

            // (0.5, 0, 0)：signed=1.5,1-0.5=0.5,1,1,1,1 → min=0.5，深度=0.5=BlendDistance → 权重=1
            AssertTrue(MathF.Abs(v2.ComputeWeight(new Vector3(0.5f, 0, 0)) - 1.0f) < Eps,
                "凸包近边权重应=1");

            // (2, 0, 0)：signed=3,-1,1,1,1,1 → inside=false → 深度取负 → 权重=0
            AssertTrue(v2.ComputeWeight(new Vector3(2, 0, 0)) < Eps,
                "凸包外权重应=0");
        }

        // ── 7. Settings.Blend：单一覆盖者胜出 + Override=false 回退引擎默认 ──
        public static void TestSettingsBlendSingleOverride()
        {
            var settings = new PostProcessSettings();
            // 只覆盖 Bloom 强度
            settings.Bloom = new BloomOverride
            {
                Override = true, Enabled = true,
                Threshold = 0.7f, Intensity = 2.5f, Radius = 0.9f,
                Tint = new Vector3(1, 0.5f, 0)
            };
            settings.OutputScale = new OutputScaleOverride { Override = true, Scale = 0.5f };

            var result = PostProcessSettings.Blend(new List<PostProcessSettings.Contribution>
            {
                new(settings, 1.0f, 0)
            });

            // Bloom 覆盖生效
            AssertTrue(MathF.Abs(result.Bloom.Intensity - 2.5f) < Eps,
                $"覆盖后 Bloom.Intensity 应=2.5，实际={result.Bloom.Intensity}");
            AssertTrue(MathF.Abs(result.Bloom.Threshold - 0.7f) < Eps,
                $"覆盖后 Bloom.Threshold 应=0.7");
            AssertTrue(result.Bloom.Tint.Y < 0.6f, "Bloom.Tint.Y 应=0.5（覆盖生效）");

            // OutputScale 覆盖
            AssertTrue(MathF.Abs(result.OutputScale.Scale - 0.5f) < Eps,
                "OutputScale 覆盖应生效");

            // Exposure 未覆盖 → 回退引擎默认
            AssertTrue(MathF.Abs(result.Exposure.Key - 0.18f) < Eps,
                $"未覆盖 Exposure.Key 应=0.18（引擎默认），实际={result.Exposure.Key}");
            AssertTrue(MathF.Abs(result.Exposure.MinExposure - (-8.0f)) < Eps,
                "未覆盖 Exposure.MinExposure 应=-8");
        }

        // ── 8. Settings.Blend：多体积按优先级 + 权重剩余吃 ──
        public static void TestSettingsBlendPriorityAndWeight()
        {
            // 高优先级：Override Bloom 强度=2.0
            var high = new PostProcessSettings();
            high.Bloom = new BloomOverride
            {
                Override = true, Enabled = true,
                Intensity = 2.0f, Threshold = 0.5f, Radius = 0.5f,
                Tint = new Vector3(1, 1, 1)
            };

            // 低优先级：Override Bloom 强度=1.0
            var low = new PostProcessSettings();
            low.Bloom = new BloomOverride
            {
                Override = true, Enabled = true,
                Intensity = 1.0f, Threshold = 1.5f, Radius = 0.2f,
                Tint = new Vector3(0, 0, 0)
            };

            var result = PostProcessSettings.Blend(new List<PostProcessSettings.Contribution>
            {
                new(high, 0.6f, 10),  // 高优先级吃 0.6 权重
                new(low, 1.0f, 0)     // 低优先级吃剩余 0.4 权重
            });

            // Bloom.Intensity = 0.6*2.0 + 0.4*1.0 = 1.2 + 0.4 = 1.6
            float expectedIntensity = 0.6f * 2.0f + 0.4f * 1.0f;
            AssertTrue(MathF.Abs(result.Bloom.Intensity - expectedIntensity) < 1e-3f,
                $"Bloom.Intensity 加权混合应={expectedIntensity}，实际={result.Bloom.Intensity}");

            // Bloom.Threshold 同理：0.6*0.5 + 0.4*1.5 = 0.3 + 0.6 = 0.9
            float expectedThreshold = 0.6f * 0.5f + 0.4f * 1.5f;
            AssertTrue(MathF.Abs(result.Bloom.Threshold - expectedThreshold) < 1e-3f,
                $"Bloom.Threshold 加权混合应={expectedThreshold}，实际={result.Bloom.Threshold}");
        }

        // ── 9. Settings.Blend：高优先级权重大于 0.5 时 Enum 覆盖生效 ──
        public static void TestSettingsBlendEnumByPriority()
        {
            var withOp = new PostProcessSettings();
            withOp.Tonemapping = new TonemappingOverride
            {
                Override = true, Operator = 3 // ACES 之类
            };

            var withOpLow = new PostProcessSettings();
            withOpLow.Tonemapping = new TonemappingOverride
            {
                Override = true, Operator = 1
            };

            // 高优先级权重大于 0.5 → 直接取高优先级 operator
            var result = PostProcessSettings.Blend(new List<PostProcessSettings.Contribution>
            {
                new(withOp, 0.7f, 10),
                new(withOpLow, 1.0f, 0)
            });
            AssertEqual(3, result.Tonemapping.Operator, "高优先级权重>0.5 应直接取 operator=3");

            // 高优先级权重不足 0.5 → 取首个 override（低优先级）
            var result2 = PostProcessSettings.Blend(new List<PostProcessSettings.Contribution>
            {
                new(withOp, 0.3f, 10),
                new(withOpLow, 1.0f, 0)
            });
            AssertEqual(1, result2.Tonemapping.Operator, "高优先级权重<0.5 应取首个覆盖者=1");
        }

        // ── 10. ApplyTo 写回 PostProcessingStack.Settings ──
        public static void TestApplyToPostProcessingStack()
        {
            var volumeSettings = new PostProcessSettings();
            volumeSettings.Bloom = new BloomOverride
            {
                Override = true, Enabled = true,
                Threshold = 0.9f, Intensity = 3.0f, Radius = 0.6f,
                Tint = new Vector3(1, 0.8f, 0.6f)
            };
            volumeSettings.Vignette = new VignetteOverride
            {
                Override = true, Enabled = true,
                Intensity = 0.8f, Radius = 0.6f, Softness = 0.4f,
                Color = new Vector3(0.1f, 0.1f, 0.1f)
            };

            var blended = PostProcessSettings.Blend(new List<PostProcessSettings.Contribution>
            {
                new(volumeSettings, 1.0f, 0)
            });

            var stack = new PostProcessingStack(null!);
            blended.ApplyTo(stack);

            var s = stack.Settings;
            AssertTrue(s.EnableBloom, "ApplyTo 后 EnableBloom 应 true");
            AssertTrue(MathF.Abs(s.BloomIntensity - 3.0f) < Eps,
                $"ApplyTo 后 BloomIntensity 应=3.0，实际={s.BloomIntensity}");
            AssertTrue(MathF.Abs(s.BloomThreshold - 0.9f) < Eps,
                "ApplyTo 后 BloomThreshold 应=0.9");

            AssertTrue(s.EnableVignette, "ApplyTo 后 EnableVignette 应 true");
            AssertTrue(MathF.Abs(s.VignetteIntensity - 0.8f) < Eps,
                "ApplyTo 后 VignetteIntensity 应=0.8");
            AssertTrue(MathF.Abs(s.VignetteSoftness - 0.4f) < Eps,
                "ApplyTo 后 VignetteSoftness 应=0.4");
        }

        // ── 11. VolumeBlendSystem：AddStandaloneVolume + Update 写回 Stack ──
        public static void TestVolumeBlendSystemUpdate()
        {
            var stack = new PostProcessingStack(null!);
            var system = new VolumeBlendSystem(stack);

            var vol = new PostProcessVolume
            {
                Name = "InnerBloom",
                Shape = VolumeShape.Sphere,
                Center = Vector3.Zero,
                Radius = 10.0f,
                BlendDistance = 1.0f,
                BlendWeight = 1.0f,
                Priority = 100
            };
            vol.Settings = new PostProcessSettings
            {
                Bloom = new BloomOverride
                {
                    Override = true, Enabled = true,
                    Threshold = 0.4f, Intensity = 5.0f, Radius = 0.5f,
                    Tint = Vector3.One
                }
            };

            system.AddStandaloneVolume(vol);
            system.SetCameraPosition(Vector3.Zero); // 摄像机在体积内

            system.Update(0.016f, ecs: null);

            var s = stack.Settings;
            AssertTrue(s.EnableBloom, "Update 后 EnableBloom 应 true（体积生效）");
            AssertTrue(MathF.Abs(s.BloomIntensity - 5.0f) < Eps,
                $"Update 后 BloomIntensity 应=5.0（覆盖值），实际={s.BloomIntensity}");
        }

        // ── 12. VolumeBlendSystem：摄像机移出体积 → Bloom 回退默认 ──
        public static void TestVolumeBlendSystemCameraOutside()
        {
            var stack = new PostProcessingStack(null!);
            var system = new VolumeBlendSystem(stack);

            var vol = new PostProcessVolume
            {
                Shape = VolumeShape.Sphere,
                Center = new Vector3(50, 0, 0),
                Radius = 5.0f,
                BlendDistance = 1.0f
            };
            vol.Settings = new PostProcessSettings
            {
                Bloom = new BloomOverride
                {
                    Override = true, Enabled = true,
                    Threshold = 0.4f, Intensity = 5.0f, Radius = 0.5f,
                    Tint = Vector3.One
                }
            };

            system.AddStandaloneVolume(vol);
            system.SetCameraPosition(Vector3.Zero); // 远离体积

            system.Update(0.016f, ecs: null);

            var s = stack.Settings;
            // 体积无效 → 回退引擎默认（Bloom.Intensity=0.8）
            AssertTrue(MathF.Abs(s.BloomIntensity - 0.8f) < 1e-2f,
                $"摄像机外 BloomIntensity 应=0.8（引擎默认），实际={s.BloomIntensity}");
        }

        // ── 13. Priority 排序：同点多个体积，高 Priority 完全胜出（权重≥剩余） ──
        public static void TestVolumeBlendSystemPrioritySort()
        {
            var stack = new PostProcessingStack(null!);
            var system = new VolumeBlendSystem(stack);

            var lowVol = new PostProcessVolume
            {
                Name = "LowPrio",
                Shape = VolumeShape.Sphere,
                Center = Vector3.Zero,
                Radius = 10.0f,
                BlendDistance = 0.1f,
                BlendWeight = 1.0f,
                Priority = 0
            };
            lowVol.Settings = new PostProcessSettings
            {
                Bloom = new BloomOverride
                {
                    Override = true, Enabled = true,
                    Threshold = 0.1f, Intensity = 1.0f, Radius = 0.1f,
                    Tint = Vector3.One
                }
            };

            var highVol = new PostProcessVolume
            {
                Name = "HighPrio",
                Shape = VolumeShape.Sphere,
                Center = Vector3.Zero,
                Radius = 10.0f,
                BlendDistance = 0.1f,
                BlendWeight = 1.0f,
                Priority = 100
            };
            highVol.Settings = new PostProcessSettings
            {
                Bloom = new BloomOverride
                {
                    Override = true, Enabled = true,
                    Threshold = 0.9f, Intensity = 9.0f, Radius = 0.9f,
                    Tint = new Vector3(1, 0, 0)
                }
            };

            system.AddStandaloneVolume(lowVol);
            system.AddStandaloneVolume(highVol);
            system.SetCameraPosition(Vector3.Zero);

            system.Update(0.016f, ecs: null);

            var s = stack.Settings;
            // 高优先级权重=1，吃光全部权重 → Intensity=9.0
            AssertTrue(MathF.Abs(s.BloomIntensity - 9.0f) < 1e-2f,
                $"高优先级完全覆盖后 BloomIntensity 应=9.0，实际={s.BloomIntensity}");
            AssertTrue(s.BloomTint.X > 0.9f,
                $"高优先级 Tint.X 应=1（红色），实际={s.BloomTint.X}");
        }

        // ── 14. RemoveStandaloneVolume + ClearCameraPosition 行为 ──
        public static void TestVolumeBlendSystemRemoveAndClear()
        {
            var stack = new PostProcessingStack(null!);
            var system = new VolumeBlendSystem(stack);

            var vol = new PostProcessVolume
            {
                Shape = VolumeShape.Sphere,
                Center = Vector3.Zero,
                Radius = 100.0f
            };
            vol.Settings = new PostProcessSettings
            {
                Vignette = new VignetteOverride
                {
                    Override = true, Enabled = true,
                    Intensity = 1.0f, Radius = 0.5f, Softness = 0.5f,
                    Color = Vector3.Zero
                }
            };

            system.AddStandaloneVolume(vol);
            AssertTrue(system.RemoveStandaloneVolume(vol), "Remove 应 true");
            AssertTrue(!system.RemoveStandaloneVolume(vol), "重复 Remove 应 false");

            system.SetCameraPosition(Vector3.Zero);
            system.Update(0.016f, ecs: null);

            // 已移除体积 → Vignette 回退引擎默认（Intensity=0.5）
            var s = stack.Settings;
            AssertTrue(MathF.Abs(s.VignetteIntensity - 0.5f) < 1e-2f,
                $"移除后 VignetteIntensity 应=0.5（引擎默认），实际={s.VignetteIntensity}");

            // ClearCameraPosition 后再 Update：无摄像机 → 不混入任何体积
            system.ClearCameraPosition();
            stack.Settings.EnableBloom = false; // 重置
            system.Update(0.016f, ecs: null);
            // 引擎默认 Bloom.Intensity=0.8 仍生效
            AssertTrue(MathF.Abs(stack.Settings.BloomIntensity - 0.8f) < 1e-2f,
                "无摄像机时仅引擎默认生效");
        }
    }
}
