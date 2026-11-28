using System;
using YamaEngine.Reality;

namespace YamaEngine.Tests
{
    /// <summary>
    /// 第七轮回归：分布式（逐格点）反应–扩散燃烧。
    /// 覆盖：总包反应配平、元素守恒（反应+扩散下不变）、限流反应物（当量比）、
    /// 完全/不完全燃烧、火焰锋面从点火源逐渐推进（非瞬时全域点燃）、
    /// 逐位确定性、ConservationLedger 平衡。
    /// 均为 public static void Test* —— 由 XunitAdapter 反射自动发现进门禁。
    /// </summary>
    public static class RealityPhysicsSeventhTests
    {
        private static void Require(bool ok, string msg)
        {
            if (!ok) throw new InvalidOperationException("REALITY_R7_FAIL: " + msg);
        }
        private static bool Near(float a, float b, float tol) => MathF.Abs(a - b) <= tol;

        // 标准参数：CH4 + 2 O2 -> CO2 + 2 H2O，元素 [C,H,O]。
        private const float Ambient = 293.15f;
        private const float PreExp = 1e6f;
        private const float Ea = 50000f;
        private const float HeatRelease = 800000f;
        private const float HeatCapacity = 1000f;
        private const float Spacing = 0.01f;
        private const float SpeciesDiff = 1e-4f;
        private const float ThermalDiff = 2e-3f;
        private const float MaxBurnFraction = 0.2f;
        private const float Dt = 0.01f;
        // Ea=50000 下 Arrhenius 在 293K 几乎不反应（kf~1e-3），需 ~900K+ 才显著。
        // 故「全燃」类测试用高温环境 HotAmbient=1200K（kf~6650，vf*dt 远超燃耗上限）；
        // 「传播」类测试用冷环境 Ambient=293K + 燃点门控，靠点火源局部起燃、热扩散推进。
        private const float HotAmbient = 1200f;

        private static YrDistributedCombustion Build(int cells, float fuel, float ox,
                                                     float ignitionTemp,
                                                     float ambient = Ambient,
                                                     float[] fuelField = null,
                                                     float[] oxField = null)
        {
            var lf = fuelField ?? Fill(cells, fuel);
            var lo = oxField ?? Fill(cells, ox);
            return new YrDistributedCombustion(cells, lf, lo, ambient,
                PreExp, Ea, HeatRelease, ignitionTemp, HeatCapacity,
                Spacing, SpeciesDiff, ThermalDiff, MaxBurnFraction,
                new ConservationLedger());
        }

        private static float[] Fill(int n, float v)
        {
            var a = new float[n];
            for (int i = 0; i < n; i++) a[i] = v;
            return a;
        }

        /// <summary>元素守恒：点火充分燃烧前后 C/H/O 总量不变（反应配平 + 扩散守恒）。</summary>
        public static void TestR7ElementInventoryConservedUnderBurn()
        {
            var c = Build(10, 1f, 3f, ignitionTemp: 200f, ambient: HotAmbient); // 燃点低于环境 -> 全域点燃，充分反应
            double c0 = c.ElementInventory(YrDistributedCombustion.ElemC);
            double h0 = c.ElementInventory(YrDistributedCombustion.ElemH);
            double o0 = c.ElementInventory(YrDistributedCombustion.ElemO);
            Require(c0 > 0 && h0 > 0 && o0 > 0, "初始元素库存应为正");

            for (int k = 0; k < 60; k++) c.Step(Dt);

            double tol = 1e-3;
            Require(Math.Abs(c.ElementInventory(YrDistributedCombustion.ElemC) - c0) < tol,
                "C 守恒被破坏: " + c0 + " -> " + c.ElementInventory(YrDistributedCombustion.ElemC));
            Require(Math.Abs(c.ElementInventory(YrDistributedCombustion.ElemH) - h0) < tol,
                "H 守恒被破坏: " + h0 + " -> " + c.ElementInventory(YrDistributedCombustion.ElemH));
            Require(Math.Abs(c.ElementInventory(YrDistributedCombustion.ElemO) - o0) < tol,
                "O 守恒被破坏: " + o0 + " -> " + c.ElementInventory(YrDistributedCombustion.ElemO));
        }

        /// <summary>氧化剂过量 -> 完全燃烧：燃料趋尽，产物按计量比生成，账本平衡。</summary>
        public static void TestR7CompleteCombustionWithExcessOxidizer()
        {
            int n = 8;
            var c = Build(n, 1f, 3f, ignitionTemp: 200f, ambient: HotAmbient);
            for (int k = 0; k < 80; k++) c.Step(Dt);

            float fuelLeft = 0f, co2 = 0f, h2o = 0f;
            for (int i = 0; i < n; i++)
            {
                fuelLeft += c.Conc(YrDistributedCombustion.Fuel, i);
                co2 += c.Conc(YrDistributedCombustion.ProductA, i);
                h2o += c.Conc(YrDistributedCombustion.ProductB, i);
            }
            Require(fuelLeft < 0.05f, "氧化剂过量时应几乎完全燃烧，剩余燃料 " + fuelLeft);
            Require(co2 > 0.9f * n, "CO2 生成不足: " + co2);
            Require(Near(h2o, 2f * co2, 0.05f * n), "H2O/CO2 应接近计量比 2: " + h2o + " vs " + 2 * co2);
            Require(c.Ledger.IsBalanced(1e-6), "燃烧后账本失衡");
            Require(c.TotalHeatReleased > 0f, "放热燃烧应释放热量");
        }

        /// <summary>氧化剂匮乏 -> 不完全燃烧：氧气耗尽而燃料剩余（限流反应物）。</summary>
        public static void TestR7OxidizerStarvedIncompleteCombustion()
        {
            int n = 6;
            var c = Build(n, 1f, 0.5f, ignitionTemp: 200f, ambient: HotAmbient); // ox 仅 0.5，仅够烧 0.25 燃料
            for (int k = 0; k < 30; k++) c.Step(Dt);

            float fuelLeft = 0f, oxLeft = 0f;
            for (int i = 0; i < n; i++)
            {
                fuelLeft += c.Conc(YrDistributedCombustion.Fuel, i);
                oxLeft += c.Conc(YrDistributedCombustion.Oxidizer, i);
            }
            Require(oxLeft < 1e-3f, "匮乏氧化剂应被耗尽，剩余 " + oxLeft);
            Require(fuelLeft > 0.5f * n, "缺氧时燃料应大量剩余（不完全燃烧），剩余 " + fuelLeft);
            Require(c.TotalHeatReleased > 0f, "部分燃烧仍应放热");
            Require(c.Ledger.IsBalanced(1e-6), "不完全燃烧后账本失衡");
        }

        /// <summary>无燃料 -> 不产生任何热量（杜绝隐式能量）。</summary>
        public static void TestR7NoFuelNoHeatReleased()
        {
            var c = Build(6, 0f, 3f, ignitionTemp: 200f, ambient: HotAmbient);
            for (int k = 0; k < 30; k++) c.Step(Dt);
            Require(c.TotalHeatReleased == 0f, "无燃料不应放热: " + c.TotalHeatReleased);
            Require(c.Ledger.IsBalanced(1e-6), "无燃料场景账本失衡");
        }

        /// <summary>无氧化剂 -> 不产生任何热量。</summary>
        public static void TestR7NoOxidizerNoHeatReleased()
        {
            var c = Build(6, 1f, 0f, ignitionTemp: 200f, ambient: HotAmbient);
            for (int k = 0; k < 30; k++) c.Step(Dt);
            Require(c.TotalHeatReleased == 0f, "无氧化剂不应放热: " + c.TotalHeatReleased);
        }

        /// <summary>
        /// 火焰锋面从点火源逐渐推进：单点点燃后，点燃格点数随帧增长但非瞬时全域。
        /// 并以「总燃耗超过点火格自身存量」证明火焰确实传到了邻近格点。
        /// </summary>
        public static void TestR7FlameFrontPropagatesFromIgnitionSource()
        {
            int n = 12;
            var c = Build(n, 1f, 3f, ignitionTemp: 900f);
            c.Ignite(0, 2000f); // 仅点燃 0 号格点

            c.Step(Dt);
            int afterOne = c.IgnitedCount();
            Require(afterOne <= 3, "火焰不应在一帧内瞬时点燃全域，实际 " + afterOne);

            for (int k = 0; k < 120; k++) { c.Step(Dt); }

            float burnedTotal = 0f;
            for (int i = 0; i < n; i++)
                burnedTotal += 1f - c.Conc(YrDistributedCombustion.Fuel, i);

            Require(c.IgnitedCount() > 1, "火焰应传到点火格之外");
            Require(burnedTotal > 2.0f,
                "总燃耗应超过点火格自身存量(1.0)以证明传播，实际 " + burnedTotal);
            Require(c.Ledger.IsBalanced(1e-6), "传播场景账本失衡");
        }

        /// <summary>逐位确定性：两次相同初始条件的运行产生完全相同的快照。</summary>
        public static void TestR7DeterministicReplay()
        {
            float[] Run()
            {
                var c = Build(10, 1f, 3f, ignitionTemp: 900f);
                c.Ignite(0, 2000f);
                for (int k = 0; k < 40; k++) c.Step(Dt);
                return c.Snapshot();
            }
            var a = Run();
            var b = Run();
            Require(a.Length == b.Length, "快照长度不一致");
            for (int i = 0; i < a.Length; i++)
                Require(a[i] == b[i], "快照逐位不一致 @" + i);
        }

        /// <summary>燃点门控：未达燃点时即使有燃料+氧化剂也不反应。</summary>
        public static void TestR7NoIgnitionNoReactionBelowIgnitionPoint()
        {
            var c = Build(6, 1f, 3f, ignitionTemp: 1e6f);
            c.Ignite(0, 1000f); // 仍远低于 1e6
            for (int k = 0; k < 40; k++) c.Step(Dt);
            Require(c.TotalHeatReleased == 0f,
                "未达燃点不应反应放热: " + c.TotalHeatReleased);
            Require(Near(c.Conc(YrDistributedCombustion.Fuel, 0), 1f, 1e-4f),
                "未达燃点燃料不应被消耗");
        }

        /// <summary>构造即校验总包反应配平（CH4+2O2->CO2+2H2O 对 C/H/O 净计量和为 0）。</summary>
        public static void TestR7OverallReactionIsBalanced()
        {
            var stoich = YrDistributedCombustion.Stoich;
            var elems = YrDistributedCombustion.SpeciesElements;
            Require(stoich.Length == elems.Length, "计量数与物种数不一致");
            for (int e = 0; e < YrDistributedCombustion.ElementCount; e++)
            {
                double sum = 0.0;
                for (int s = 0; s < stoich.Length; s++) sum += stoich[s] * elems[s][e];
                Require(Math.Abs(sum) < 1e-9, "元素 " + e + " 净计量和非零: " + sum);
            }
            var c = Build(4, 1f, 3f, ignitionTemp: 200f, ambient: HotAmbient);
            double inv0 = c.ElementInventory(YrDistributedCombustion.ElemO);
            c.Step(Dt);
            Require(Math.Abs(c.ElementInventory(YrDistributedCombustion.ElemO) - inv0) < 1e-4,
                "单步后 O 库存改变，反应未严格配平");
        }
    }
}
