// Light2DDepthTests.cs — Unity5 URP Light2D 等价验证
// 覆盖:
//   * CelLight2D.Evaluate: Directional 朗伯/无法线 + Point 距离衰减 + Area AABB 衰减 + 旋转
//   * Disabled / Intensity<=0 / 法线背光 边界
//   * Clone 深拷贝隔离
//   * CelLightRig2D: Add/Remove/Clear + EvaluateShading/EvaluateRim 路由 + EvaluateColor 主导色合成

using System;
using System.Numerics;
using YamaEngine.Cel2D;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class Light2DDepthTests
    {
        private const float Eps = 1e-4f;

        // ── 1. Directional 无法线：恒返回 Intensity ──
        public static void TestDirectionalNoNormal()
        {
            var l = new CelLight2D
            {
                Type = CelLightType2D.Directional,
                Direction = new Vector2(0, -1), // 向下
                Intensity = 1.0f
            };

            // 任意点：无法线 → 满强度
            AssertTrue(MathF.Abs(l.Evaluate(new Vector2(0, 0)) - 1.0f) < Eps, "Directional 无法线应=Intensity");
            AssertTrue(MathF.Abs(l.Evaluate(new Vector2(1000, -500)) - 1.0f) < Eps, "Directional 远点无法线仍=Intensity");

            // Intensity=2.5 → 2.5
            l.Intensity = 2.5f;
            AssertTrue(MathF.Abs(l.Evaluate(Vector2.Zero) - 2.5f) < Eps, "Directional 强度=2.5");
        }

        // ── 2. Directional 有法线：朗伯余弦项 dot(N, -Dir) ──
        public static void TestDirectionalLambert()
        {
            var l = new CelLight2D
            {
                Type = CelLightType2D.Directional,
                Direction = new Vector2(0, -1), // 光向下传播
                Intensity = 1.0f
            };

            // 法线朝上 (0,-1)：dot((0,-1), -(0,-1)) = dot((0,-1),(0,1)) = -1... 
            // 等等：Direction 是传播方向，-Direction 是"朝光源方向"
            // dot(normal, -Direction) = dot((0,-1), (0,1)) = -1 → 负数 → 返回 0
            // 法线应该朝 -Y（即 (0,-1)），-Direction=(0,1)，dot=(0,-1)·(0,1)=-1
            // 实际上光源在上方（Direction=(0,-1) 表示光向下），法线朝上=(0,-1)，
            // -Direction=(0,1)，dot((0,-1),(0,1))=-1 → 背光
            // 让我重算：法线朝向光源 = -Direction = (0,1)，即法线=(0,1) 时正对
            var l2 = new CelLight2D
            {
                Type = CelLightType2D.Directional,
                Direction = new Vector2(0, -1),
                Intensity = 1.0f
            };

            // 法线朝下 (0,1)：dot((0,1), -（0,-1)) = dot((0,1),(0,1)) = 1 → 满强度
            AssertTrue(MathF.Abs(l2.Evaluate(Vector2.Zero, new Vector2(0, 1)) - 1.0f) < Eps,
                "法线与 -Direction 同向应满强度");

            // 法线垂直 (1,0)：dot((1,0),(0,1))=0 → 边界 → 返回 0
            AssertTrue(l2.Evaluate(Vector2.Zero, new Vector2(1, 0)) < Eps,
                "法线垂直应=0（ndl<=0）");

            // 法线背光 (0,-1)：dot((0,-1),(0,1))=-1 → 0
            AssertTrue(l2.Evaluate(Vector2.Zero, new Vector2(0, -1)) < Eps,
                "法线背光应=0");

            // 法线斜 45°：dot((sin45,cos45),(0,1))=cos45≈0.707
            float cos45 = MathF.Cos(MathF.PI / 4);
            AssertTrue(MathF.Abs(l2.Evaluate(Vector2.Zero, new Vector2(cos45, cos45)) - cos45) < Eps,
                "法线 45° 应=cos45");
        }

        // ── 3. Point 距离衰减：t=dist/R, fall=1-t^Falloff ──
        public static void TestPointDistanceFalloff()
        {
            var l = new CelLight2D
            {
                Type = CelLightType2D.Point,
                Position = Vector2.Zero,
                Radius = 10.0f,
                Falloff = 2.0f, // 平方衰减
                Intensity = 1.0f
            };

            // 近中心（dist=0.01, t=0.001, fall≈1）：几乎满强度
            AssertTrue(MathF.Abs(l.Evaluate(new Vector2(0.01f, 0)) - 1.0f) < 1e-3f,
                "点光近中心应≈Intensity");
            // 恰在光源处 dist=0 → 引擎防护返回 0
            AssertTrue(l.Evaluate(Vector2.Zero) < Eps, "点光 dist=0 应=0（防护）");

            // 半径一半：dist=5, t=0.5, fall=1-0.25=0.75
            AssertTrue(MathF.Abs(l.Evaluate(new Vector2(5, 0)) - 0.75f) < Eps,
                "点光 0.5R 处应=0.75I");

            // 半径外：dist=10 → 返回 0
            AssertTrue(l.Evaluate(new Vector2(10, 0)) < Eps, "点光半径处应=0");
            AssertTrue(l.Evaluate(new Vector2(15, 0)) < Eps, "点光半径外应=0");

            // 线性衰减 Falloff=1：dist=5, t=0.5, fall=1-0.5=0.5
            l.Falloff = 1.0f;
            AssertTrue(MathF.Abs(l.Evaluate(new Vector2(5, 0)) - 0.5f) < Eps,
                "线性衰减 0.5R 处应=0.5I");

            // Intensity=2：半强度点 → 2*0.75=1.5
            l.Falloff = 2.0f;
            l.Intensity = 2.0f;
            AssertTrue(MathF.Abs(l.Evaluate(new Vector2(5, 0)) - 1.5f) < Eps,
                "强度2的 0.5R 处应=1.5");
        }

        // ── 4. Point 极近距离（dist<=0.0001）返回 0 防除零 ──
        public static void TestPointNearZeroDistance()
        {
            var l = new CelLight2D
            {
                Type = CelLightType2D.Point,
                Position = Vector2.Zero,
                Radius = 10.0f,
                Intensity = 1.0f
            };

            // 恰好在光源位置 → dist=0 → 返回 0（边界防护）
            AssertTrue(l.Evaluate(Vector2.Zero) < Eps, "点光 dist=0 应=0（防护）");

            // 稍微偏移 → 正常衰减
            AssertTrue(l.Evaluate(new Vector2(0.001f, 0)) > 0.99f, "点光极近应接近满强度");
        }

        // ── 5. Area AABB 衰减：矩形内部满强度，外部按距离衰减 ──
        public static void TestAreaAABBFalloff()
        {
            var l = new CelLight2D
            {
                Type = CelLightType2D.Area,
                Position = Vector2.Zero,
                AreaSize = new Vector2(10, 10), // halfW=5, halfH=5
                Radius = 5.0f,
                Falloff = 2.0f,
                Intensity = 1.0f
            };

            // 中心：dx=dy=0, d=0, fall=1 → 强度=1
            AssertTrue(MathF.Abs(l.Evaluate(Vector2.Zero) - 1.0f) < Eps, "区域光中心应=Intensity");

            // 内部点 (3,3)：dx=max(|3|-5,0)=0, dy=0 → d=0 → fall=1
            AssertTrue(MathF.Abs(l.Evaluate(new Vector2(3, 3)) - 1.0f) < Eps, "区域光内部点应=Intensity");

            // 边界 (5,5)：dx=0,dy=0 → 仍在边界上 → fall=1
            AssertTrue(MathF.Abs(l.Evaluate(new Vector2(5, 5)) - 1.0f) < Eps, "区域光边界应=Intensity");

            // 外部 (8,0)：dx=|8|-5=3, dy=0 → d=3, t=3/5=0.6, fall=1-0.36=0.64
            AssertTrue(MathF.Abs(l.Evaluate(new Vector2(8, 0)) - 0.64f) < 1e-3f,
                "区域光外部 3px 处应=0.64");

            // 远处 (15,0)：dx=10, d=10, t=min(10/5,1)=1, fall=1-1=0
            AssertTrue(l.Evaluate(new Vector2(15, 0)) < Eps, "区域光远处应=0");
        }

        // ── 6. Area 旋转：AreaRotation 旋转局部采样方向 ──
        public static void TestAreaRotation()
        {
            var l = new CelLight2D
            {
                Type = CelLightType2D.Area,
                Position = Vector2.Zero,
                AreaSize = new Vector2(10, 2), // halfW=5, halfH=1
                Radius = 5.0f,
                Falloff = 1.0f,
                AreaRotation = 0.0f,
                Intensity = 1.0f
            };

            // 无旋转：(8,0) 在 X 方向外 3px (d=3, t=0.6, val=0.4)
            //         (0,8) 在 Y 方向外 7px (d=7, t=1, val=0)
            float valXNoRot = l.Evaluate(new Vector2(8, 0));
            float valYNoRot = l.Evaluate(new Vector2(0, 8));
            AssertTrue(MathF.Abs(valXNoRot - 0.4f) < 1e-3f,
                $"无旋转 (8,0) 应=0.4，实际={valXNoRot}");
            AssertTrue(valYNoRot < Eps, "无旋转 (0,8) 远在 Y 外应=0");

            // 旋转 90°：X/Y 轴互换
            l.AreaRotation = MathF.PI / 2;
            float valXRot = l.Evaluate(new Vector2(8, 0));
            float valYRot = l.Evaluate(new Vector2(0, 8));
            // 旋转后 (8,0)→local(0,-8) 在 Y 外，(0,8)→local(8,0) 在 X 外 d=3
            AssertTrue(valXRot < Eps, "旋转 90° 后 (8,0) 应在 Y 外=0");
            AssertTrue(MathF.Abs(valYRot - 0.4f) < 1e-3f,
                $"旋转 90° 后 (0,8) 应=0.4，实际={valYRot}");
        }

        // ── 7. Disabled 灯光 Evaluate 恒=0 ──
        public static void TestDisabledLight()
        {
            var l = new CelLight2D
            {
                Type = CelLightType2D.Point,
                Position = Vector2.Zero,
                Radius = 100.0f,
                Enabled = false
            };
            AssertTrue(l.Evaluate(Vector2.Zero) < Eps, "禁用灯光应=0");

            l.Enabled = true;
            AssertTrue(l.Evaluate(new Vector2(0.01f, 0)) > 0.99f, "启用后近中心应≈满强度");
        }

        // ── 8. Intensity<=0 恒=0 ──
        public static void TestZeroIntensity()
        {
            var l = new CelLight2D
            {
                Type = CelLightType2D.Point,
                Position = Vector2.Zero,
                Radius = 100.0f,
                Intensity = 0.0f
            };
            AssertTrue(l.Evaluate(Vector2.Zero) < Eps, "Intensity=0 应=0");

            l.Intensity = -1.0f;
            AssertTrue(l.Evaluate(Vector2.Zero) < Eps, "Intensity<0 应=0");
        }

        // ── 9. Clone 深拷贝：修改克隆不影响原对象 ──
        public static void TestCloneIsolation()
        {
            var orig = new CelLight2D
            {
                Type = CelLightType2D.Point,
                Position = new Vector2(1, 2),
                Radius = 50.0f,
                Intensity = 0.8f,
                Color = new Vector3(1, 0.5f, 0.2f),
                Falloff = 1.5f,
                AffectsShading = true,
                AffectsRim = false
            };

            var clone = orig.Clone();

            // 值相等
            AssertEqual(CelLightType2D.Point, clone.Type, "Clone Type");
            AssertTrue(MathF.Abs(clone.Position.X - 1.0f) < Eps, "Clone Position.X");
            AssertTrue(MathF.Abs(clone.Radius - 50.0f) < Eps, "Clone Radius");
            AssertTrue(MathF.Abs(clone.Intensity - 0.8f) < Eps, "Clone Intensity");
            AssertTrue(MathF.Abs(clone.Color.Y - 0.5f) < Eps, "Clone Color.Y");
            AssertTrue(clone.AffectsRim == false, "Clone AffectsRim");

            // 修改克隆不影响原对象
            clone.Intensity = 5.0f;
            clone.Radius = 999.0f;
            AssertTrue(MathF.Abs(orig.Intensity - 0.8f) < Eps, "修改克隆不应影响原 Intensity");
            AssertTrue(MathF.Abs(orig.Radius - 50.0f) < Eps, "修改克隆不应影响原 Radius");
        }

        // ── 10. Rig Add/Remove/Clear + Lights 只读列表 ──
        public static void TestRigAddRemoveClear()
        {
            var rig = new CelLightRig2D();
            AssertEqual(0, rig.Lights.Count, "初始空");

            var l1 = new CelLight2D { Intensity = 1.0f };
            var l2 = new CelLight2D { Intensity = 2.0f };

            rig.Add(l1);
            rig.Add(l2);
            AssertEqual(2, rig.Lights.Count, "添加后=2");

            rig.Remove(l1);
            AssertEqual(1, rig.Lights.Count, "移除后=1");
            AssertTrue(rig.Lights[0] == l2, "剩余是 l2");

            rig.Clear();
            AssertEqual(0, rig.Lights.Count, "Clear 后=0");
        }

        // ── 11. Rig EvaluateShading：仅累加 AffectsShading 的灯光 ──
        public static void TestRigEvaluateShading()
        {
            var rig = new CelLightRig2D();

            var shade = new CelLight2D
            {
                Type = CelLightType2D.Point,
                Position = Vector2.Zero,
                Radius = 10.0f,
                Intensity = 1.0f,
                AffectsShading = true,
                AffectsRim = false
            };

            var rim = new CelLight2D
            {
                Type = CelLightType2D.Point,
                Position = Vector2.Zero,
                Radius = 10.0f,
                Intensity = 1.0f,
                AffectsShading = false,
                AffectsRim = true
            };

            rig.Add(shade);
            rig.Add(rim);

            // EvaluateShading 只算 shade（近中心≈1.0，rim 不参与）
            float shading = rig.EvaluateShading(new Vector2(0.01f, 0));
            AssertTrue(MathF.Abs(shading - 1.0f) < 1e-3f,
                $"EvaluateShading 应仅 shade 灯≈1.0，实际={shading}");

            // EvaluateRim 只算 rim（近中心≈1.0）
            float rimVal = rig.EvaluateRim(new Vector2(0.01f, 0));
            AssertTrue(MathF.Abs(rimVal - 1.0f) < 1e-3f,
                $"EvaluateRim 近中心应≈1.0，实际={rimVal}");
        }

        // ── 12. Rig EvaluateShading 多灯叠加（可超 1） ──
        public static void TestRigShadingStack()
        {
            var rig = new CelLightRig2D();

            // 两个定向光无法线，各贡献 Intensity
            rig.Add(new CelLight2D { Type = CelLightType2D.Directional, Intensity = 0.6f, AffectsShading = true });
            rig.Add(new CelLight2D { Type = CelLightType2D.Directional, Intensity = 0.7f, AffectsShading = true });

            float total = rig.EvaluateShading(Vector2.Zero);
            AssertTrue(MathF.Abs(total - 1.3f) < Eps,
                $"两灯叠加应=1.3（可超 1），实际={total}");
        }

        // ── 13. Rig EvaluateColor：单灯主导色 ──
        public static void TestRigColorSingleDominant()
        {
            var rig = new CelLightRig2D();

            var red = new CelLight2D
            {
                Type = CelLightType2D.Directional,
                Intensity = 1.0f,
                Color = new Vector3(1, 0, 0),
                AffectsShading = true
            };
            rig.Add(red);

            Vector3 baseColor = new(0.2f, 0.2f, 0.2f);
            Vector3 result = rig.EvaluateColor(Vector2.Zero, baseColor);

            // dominant=red, maxI=1.0, acc=red*1=(1,0,0)
            // result = red*1 + acc*0.35 = (1,0,0)+(0.35,0,0)=(1.35,0,0) → clamp=(1,0,0)
            AssertTrue(MathF.Abs(result.X - 1.0f) < Eps, "EvaluateColor R 应 clamp 到 1");
            AssertTrue(result.Y < Eps, "EvaluateColor G 应=0");
            AssertTrue(result.Z < Eps, "EvaluateColor B 应=0");
        }

        // ── 14. Rig EvaluateColor：无有效灯光 → 返回 baseColor ──
        public static void TestRigColorNoLight()
        {
            var rig = new CelLightRig2D();
            // 空场景

            Vector3 baseColor = new(0.3f, 0.5f, 0.7f);
            Vector3 result = rig.EvaluateColor(Vector2.Zero, baseColor);
            AssertTrue(MathF.Abs(result.X - 0.3f) < Eps, "无灯光应返回 baseColor R");
            AssertTrue(MathF.Abs(result.Y - 0.5f) < Eps, "无灯光应返回 baseColor Y");
            AssertTrue(MathF.Abs(result.Z - 0.7f) < Eps, "无灯光应返回 baseColor Z");

            // 有灯但强度=0
            rig.Add(new CelLight2D { Intensity = 0.0f });
            result = rig.EvaluateColor(Vector2.Zero, baseColor);
            AssertTrue(MathF.Abs(result.X - 0.3f) < Eps, "强度0应返回 baseColor");
        }

        // ── 15. Rig EvaluateColor：主灯决定色调，辅灯叠加 ──
        public static void TestRigColorMultiLight()
        {
            var rig = new CelLightRig2D();

            // 主灯：红色，强度 1.0
            rig.Add(new CelLight2D
            {
                Type = CelLightType2D.Directional,
                Intensity = 1.0f,
                Color = new Vector3(1, 0, 0),
                AffectsShading = true
            });

            // 辅灯：蓝色，强度 0.5
            rig.Add(new CelLight2D
            {
                Type = CelLightType2D.Directional,
                Intensity = 0.5f,
                Color = new Vector3(0, 0, 1),
                AffectsShading = true
            });

            Vector3 baseColor = Vector3.Zero;
            Vector3 result = rig.EvaluateColor(Vector2.Zero, baseColor);

            // maxI=1.0, dominant=red, acc=red*1+blue*0.5=(1,0,0.5)
            // result=red*1+acc*0.35=(1,0,0)+(0.35,0,0.175)=(1.35,0,0.175)→clamp=(1,0,0.175)
            AssertTrue(MathF.Abs(result.X - 1.0f) < Eps, "R clamp 到 1");
            AssertTrue(result.Y < Eps, "G=0");
            AssertTrue(MathF.Abs(result.Z - 0.175f) < 1e-3f,
                $"B 应=0.175，实际={result.Z}");
        }
    }
}
