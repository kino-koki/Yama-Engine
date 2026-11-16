// CelRenderLayers2DDepthTests.cs — Unity5 Sorting Layers / Rendering Layers 等价物深度验证
// 覆盖:
//   * CelRenderLayer2D: 默认值 + 命名构造
//   * CelRenderLayers2D: AddLayer/GetOrCreateLayer/GetLayer/RemoveLayer/Clear
//                        Assign/FlattenVisibleSprites/FlattenVisibleParticles/VisibleUpTo
//                        Index 升序插入 / 可见性 / Opacity=0 过滤

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using YamaEngine.Cel2D;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class CelRenderLayers2DDepthTests
    {
        private const float Eps = 1e-4f;

        private static CelSpriteData MakeSprite(float z, string tex = "a")
            => new CelSpriteData { Z = z, TexturePath = tex };

        // ── 1. CelRenderLayer2D 默认值 ──
        public static void TestLayerDefaults()
        {
            var l = new CelRenderLayer2D();
            AssertEqual("Layer", l.Name, "默认名=Layer");
            AssertEqual(0, l.Index, "默认 Index=0");
            AssertTrue(l.Visible, "默认 Visible=true");
            AssertTrue(MathF.Abs(l.Opacity - 1.0f) < Eps, "默认 Opacity=1");
            AssertTrue(l.AffectedByLight, "默认 AffectedByLight=true");
            AssertTrue(l.EnableBanding, "默认 EnableBanding=true");
            AssertEqual(0, l.Sprites.Count, "默认 0 精灵");
            AssertEqual(0, l.Particles.Count, "默认 0 粒子");
        }

        // ── 2. 命名构造 ──
        public static void TestLayerNamedCtor()
        {
            var l = new CelRenderLayer2D("Background", 0);
            AssertEqual("Background", l.Name, "命名 Name");
            AssertEqual(0, l.Index, "命名 Index");
        }

        // ── 3. AddLayer 按 Index 升序插入 ──
        public static void TestAddLayerSortedByIndex()
        {
            var mgr = new CelRenderLayers2D();
            mgr.AddLayer("Front", 10);
            mgr.AddLayer("Back", 0);
            mgr.AddLayer("Mid", 5);

            AssertEqual(3, mgr.Layers.Count, "3 层");
            AssertEqual("Back", mgr.Layers[0].Name, "Index=0 首位");
            AssertEqual("Mid", mgr.Layers[1].Name, "Index=5 中位");
            AssertEqual("Front", mgr.Layers[2].Name, "Index=10 末位");
        }

        // ── 4. GetLayer / GetOrCreateLayer ──
        public static void TestGetOrCreateLayer()
        {
            var mgr = new CelRenderLayers2D();
            mgr.AddLayer("BG", 0);

            var found = mgr.GetLayer("BG");
            AssertTrue(found != null, "GetLayer 已知返非空");

            var missing = mgr.GetLayer("Nope");
            AssertTrue(missing == null, "GetLayer 未知返 null");

            var created = mgr.GetOrCreateLayer("New", 3);
            AssertTrue(created != null, "GetOrCreateLayer 新建");
            AssertEqual(3, created!.Index, "新层 Index=3");
            AssertEqual(2, mgr.Layers.Count, "新建后 2 层");

            var again = mgr.GetOrCreateLayer("New", 99); // 同名保留原 Index
            AssertEqual(3, again.Index, "同名 GetOrCreate 不改 Index");
            AssertEqual(2, mgr.Layers.Count, "同名不新建");
        }

        // ── 5. RemoveLayer / Clear ──
        public static void TestRemoveAndClear()
        {
            var mgr = new CelRenderLayers2D();
            mgr.AddLayer("A", 0);
            mgr.AddLayer("B", 1);

            AssertTrue(mgr.RemoveLayer("A"), "RemoveLayer 已知返 true");
            AssertTrue(!mgr.RemoveLayer("Nope"), "RemoveLayer 未知返 false");
            AssertEqual(1, mgr.Layers.Count, "移除后 1 层");

            mgr.Clear();
            AssertEqual(0, mgr.Layers.Count, "Clear 后 0 层");
        }

        // ── 6. Assign 自动建层 + 精灵归入 ──
        public static void TestAssignCreatesLayerAndAdds()
        {
            var mgr = new CelRenderLayers2D();
            mgr.Assign(MakeSprite(1), "FG", 5);
            mgr.Assign(MakeSprite(2), "FG", 5);
            mgr.Assign(MakeSprite(3), "BG", 0);

            var fg = mgr.GetLayer("FG")!;
            AssertEqual(2, fg.Sprites.Count, "FG 层 2 精灵");
            AssertEqual(5, fg.Index, "FG Index=5");
        }

        // ── 7. FlattenVisibleSprites 按层升序输出 ──
        public static void TestFlattenSpritesOrder()
        {
            var mgr = new CelRenderLayers2D();
            mgr.AddLayer("BG", 0);
            mgr.AddLayer("FG", 1);
            mgr.GetLayer("BG")!.Sprites.Add(MakeSprite(10));
            mgr.GetLayer("BG")!.Sprites.Add(MakeSprite(20));
            mgr.GetLayer("FG")!.Sprites.Add(MakeSprite(30));

            var list = mgr.FlattenVisibleSprites().ToList();
            AssertEqual(3, list.Count, "全部可见 → 3 精灵");
            AssertEqual(10f, list[0].Z, "BG[0].Z=10");
            AssertEqual(20f, list[1].Z, "BG[1].Z=20");
            AssertEqual(30f, list[2].Z, "FG[0].Z=30");
        }

        // ── 8. 隐藏层过滤 ──
        public static void TestHiddenLayerFiltered()
        {
            var mgr = new CelRenderLayers2D();
            var bg = mgr.AddLayer("BG", 0);
            var hidden = mgr.AddLayer("Hidden", 1, visible: false);
            var fg = mgr.AddLayer("FG", 2);
            bg.Sprites.Add(MakeSprite(1));
            hidden.Sprites.Add(MakeSprite(2));
            fg.Sprites.Add(MakeSprite(3));

            var list = mgr.FlattenVisibleSprites().ToList();
            AssertEqual(2, list.Count, "隐藏层被过滤");
            AssertTrue(list.All(s => s.Z != 2f), "隐藏层精灵不出现");
        }

        // ── 9. Opacity<=0 层被过滤 ──
        public static void TestZeroOpacityLayerFiltered()
        {
            var mgr = new CelRenderLayers2D();
            var bg = mgr.AddLayer("BG", 0);
            var faded = mgr.AddLayer("Faded", 1);
            faded.Opacity = 0f;
            bg.Sprites.Add(MakeSprite(1));
            faded.Sprites.Add(MakeSprite(2));

            var list = mgr.FlattenVisibleSprites().ToList();
            AssertEqual(1, list.Count, "Opacity=0 层被过滤");
        }

        // ── 10. VisibleUpTo 包含本层之前 ──
        public static void TestVisibleUpToInclusive()
        {
            var mgr = new CelRenderLayers2D();
            mgr.AddLayer("BG", 0).Sprites.Add(MakeSprite(1));
            mgr.AddLayer("Mid", 1).Sprites.Add(MakeSprite(2));
            mgr.AddLayer("FG", 2).Sprites.Add(MakeSprite(3));

            var list = mgr.VisibleUpTo("Mid").ToList();
            AssertEqual(2, list.Count, "VisibleUpTo 含本层 → BG+Mid=2");
            AssertTrue(!list.Any(s => s.Z == 3f), "FG 不包含");
        }

        // ── 11. Assign 精灵独立列表引用 ──
        public static void TestAssignDoesNotCopyList()
        {
            var mgr = new CelRenderLayers2D();
            var layer = mgr.AddLayer("L", 0);
            var sp = MakeSprite(0);
            mgr.Assign(sp, "L");
            AssertTrue(ReferenceEquals(layer.Sprites[0], sp), "Assign 保留同一实例");
        }

        // ── 12. FlattenVisibleParticles 按层升序 ──
        public static void TestFlattenParticles()
        {
            var mgr = new CelRenderLayers2D();
            var bg = mgr.AddLayer("BG", 0);
            var fg = mgr.AddLayer("FG", 1);

            bg.Particles.Add(new CelParticle2D { Position = new Vector2(1, 0) });
            fg.Particles.Add(new CelParticle2D { Position = new Vector2(2, 0) });

            var list = mgr.FlattenVisibleParticles().ToList();
            AssertEqual(2, list.Count, "2 粒子按层升序");
            AssertTrue(MathF.Abs(list[0].Position.X - 1f) < Eps, "BG 粒子先出");
            AssertTrue(MathF.Abs(list[1].Position.X - 2f) < Eps, "FG 粒子后出");
        }
    }
}
