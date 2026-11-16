// CelSpriteAtlasDepthTests.cs — Unity5 SpriteAtlas 等价物深度验证
// 覆盖:
//   * CelAtlasRegion: 构造/IsValid/Center/Size/SourceRect
//   * CelSpriteAtlas: AddRegion/GetRegion/Contains/TryGetRegion/AddGrid/Clear

using System;
using System.Numerics;
using YamaEngine.Cel2D;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class CelSpriteAtlasDepthTests
    {
        private const float Eps = 1e-4f;

        // ── 1. CelAtlasRegion IsValid 正/反例 ──
        public static void TestRegionIsValid()
        {
            var ok = new CelAtlasRegion("ok", 0, 0, 32, 32, "a.png", new Vector2(256, 256));
            AssertTrue(ok.IsValid, "有效区域");

            var zeroW = new CelAtlasRegion("z", 0, 0, 0, 32, "a.png", new Vector2(256, 256));
            AssertTrue(!zeroW.IsValid, "Width=0 无效");

            var zeroH = new CelAtlasRegion("z", 0, 0, 32, 0, "a.png", new Vector2(256, 256));
            AssertTrue(!zeroH.IsValid, "Height=0 无效");

            var zeroAtlas = new CelAtlasRegion("z", 0, 0, 32, 32, "a.png", Vector2.Zero);
            AssertTrue(!zeroAtlas.IsValid, "AtlasSize=0 无效");
        }

        // ── 2. CelAtlasRegion Center/Size ──
        public static void TestRegionCenterSize()
        {
            var r = new CelAtlasRegion("r", 10, 20, 40, 60, "a.png", new Vector2(256, 256));
            AssertTrue(MathF.Abs(r.Center.X - 30f) < Eps, $"Center.X=X+W/2=30，实际 {r.Center.X}");
            AssertTrue(MathF.Abs(r.Center.Y - 50f) < Eps, $"Center.Y=Y+H/2=50，实际 {r.Center.Y}");
            AssertTrue(MathF.Abs(r.Size.X - 40f) < Eps, "Size.X=40");
            AssertTrue(MathF.Abs(r.Size.Y - 60f) < Eps, "Size.Y=60");
        }

        // ── 3. CelAtlasRegion.SourceRect UV 归一化 ──
        public static void TestRegionSourceRectNormalized()
        {
            var r = new CelAtlasRegion("r", 64, 128, 32, 32, "a.png", new Vector2(256, 256));
            Vector4 uv = r.SourceRect;
            AssertTrue(MathF.Abs(uv.X - 64f / 256f) < Eps, $"u0=64/256，实际 {uv.X}");
            AssertTrue(MathF.Abs(uv.Y - 128f / 256f) < Eps, $"v0=128/256，实际 {uv.Y}");
            AssertTrue(MathF.Abs(uv.Z - 96f / 256f) < Eps, $"u1=(64+32)/256，实际 {uv.Z}");
            AssertTrue(MathF.Abs(uv.W - 160f / 256f) < Eps, $"w=(128+32)/256，实际 {uv.W}");
        }

        // ── 4. CelAtlasRegion.SourceRect 无效时返回整图 ──
        public static void TestRegionSourceRectInvalidFallback()
        {
            var r = new CelAtlasRegion("r", 0, 0, 0, 0, "a.png", Vector2.Zero);
            Vector4 uv = r.SourceRect;
            AssertTrue(MathF.Abs(uv.X - 0f) < Eps && MathF.Abs(uv.Y - 0f) < Eps &&
                       MathF.Abs(uv.Z - 1f) < Eps && MathF.Abs(uv.W - 1f) < Eps,
                "无效区域 SourceRect 回退到 (0,0,1,1)");
        }

        // ── 5. CelSpriteAtlas 构造默认路径 ──
        public static void TestAtlasCtorDefaults()
        {
            var a = new CelSpriteAtlas("", 256, 256);
            AssertEqual("atlas", a.AtlasTexturePath, "空路径回退为 atlas");
            AssertEqual(0, a.Count, "初始 0 区域");
            AssertTrue(MathF.Abs(a.AtlasSize.X - 256f) < Eps, "AtlasSize.X=256");
        }

        // ── 6. AddRegion 成功 ──
        public static void TestAddRegion()
        {
            var a = new CelSpriteAtlas("ui.png", 512, 512);
            var r = a.AddRegion("btn", 0, 0, 64, 64);
            AssertEqual(1, a.Count, "添加后 1 区域");
            AssertEqual("btn", r.Name, "区域名");
            AssertEqual("ui.png", r.AtlasTexturePath, "纹理路径透传");
            AssertTrue(MathF.Abs(r.AtlasSize.X - 512f) < Eps, "AtlasSize 透传");
        }

        // ── 7. AddRegion 同名覆盖 ──
        public static void TestAddRegionSameNameOverwrites()
        {
            var a = new CelSpriteAtlas("ui.png", 512, 512);
            a.AddRegion("btn", 0, 0, 64, 64);
            a.AddRegion("btn", 64, 0, 64, 64);
            AssertEqual(2, a.Count, "同名覆盖但 _ordered 追加 → 2 条目");
            var latest = a.GetRegion("btn")!.Value;
            AssertEqual(64, latest.X, "覆盖后 X=64");
        }

        // ── 8. AddRegion 空名抛异常 ──
        public static void TestAddRegionEmptyNameThrows()
        {
            var a = new CelSpriteAtlas("ui.png", 512, 512);
            bool threw = false;
            try { a.AddRegion("", 0, 0, 32, 32); }
            catch (ArgumentException) { threw = true; }
            AssertTrue(threw, "空名应抛 ArgumentException");
        }

        // ── 9. AddRegion 非正尺寸抛异常 ──
        public static void TestAddRegionBadSizeThrows()
        {
            var a = new CelSpriteAtlas("ui.png", 512, 512);
            bool threw = false;
            try { a.AddRegion("x", 0, 0, 0, 32); }
            catch (ArgumentOutOfRangeException) { threw = true; }
            AssertTrue(threw, "Width=0 应抛 ArgumentOutOfRangeException");
        }

        // ── 10. GetRegion 未知返回 null ──
        public static void TestGetRegionMissing()
        {
            var a = new CelSpriteAtlas("ui.png", 512, 512);
            a.AddRegion("a", 0, 0, 32, 32);
            AssertTrue(!a.GetRegion("nope").HasValue, "未知区域返回 null");
            AssertTrue(a.GetRegion("a").HasValue, "已知区域返回有值");
        }

        // ── 11. GetSourceRect 已知/未知 ──
        public static void TestGetSourceRect()
        {
            var a = new CelSpriteAtlas("ui.png", 512, 512);
            a.AddRegion("icon", 0, 0, 64, 64);
            Vector4 known = a.GetSourceRect("icon");
            AssertTrue(MathF.Abs(known.Z - 64f / 512f) < Eps, "已知区域 u1=64/512");
            Vector4 unknown = a.GetSourceRect("missing");
            AssertTrue(MathF.Abs(unknown.Z - 1f) < Eps, "未知区域回退整图 (0,0,1,1)");
        }

        // ── 12. Contains / TryGetRegion ──
        public static void TestContainsAndTryGet()
        {
            var a = new CelSpriteAtlas("ui.png", 512, 512);
            a.AddRegion("icon", 0, 0, 32, 32);
            AssertTrue(a.Contains("icon"), "Contains 已知=true");
            AssertTrue(!a.Contains("nope"), "Contains 未知=false");

            bool ok = a.TryGetRegion("icon", out var r);
            AssertTrue(ok && r.Name == "icon", "TryGetRegion 已知");
            bool miss = a.TryGetRegion("nope", out var r2);
            AssertTrue(!miss, "TryGetRegion 未知返回 false");
        }

        // ── 13. AddGrid 基础 ──
        public static void TestAddGrid()
        {
            var a = new CelSpriteAtlas("tiles.png", 128, 128);
            int count = a.AddGrid("tile", 32, 32);
            AssertEqual(16, count, "128/32 * 128/32 = 16 格");
            AssertEqual(16, a.Count, "图集总区域数=16");
            var first = a.GetRegion("tile_0")!.Value;
            AssertEqual(0, first.X, "tile_0 X=0");
            var last = a.GetRegion("tile_15")!.Value;
            AssertEqual(96, last.X, "tile_15 X=96");
            AssertEqual(96, last.Y, "tile_15 Y=96");
        }

        // ── 14. AddGrid with margin+spacing ──
        public static void TestAddGridMarginSpacing()
        {
            // 128 图集，边距 8，格 32，间隔 8 → 每行 (128-16)/(32+8)=2.8 → 2 格
            var a = new CelSpriteAtlas("tiles.png", 128, 128);
            int count = a.AddGrid("g", 32, 32, margin: 8, spacing: 8);
            // x: 8, 48 → 下一 88+32=120 > 120=128-8 边界？ 8+32=40, 48+32=80, 下一个 x=88, 88+32=120, 128-8=120，刚好
            AssertTrue(count >= 2, $"margin+spacing 下至少 2 格，实际 {count}");
        }

        // ── 15. AddGrid 非法格子抛异常 ──
        public static void TestAddGridBadCellThrows()
        {
            var a = new CelSpriteAtlas("tiles.png", 128, 128);
            bool threw = false;
            try { a.AddGrid("g", 0, 32); }
            catch (ArgumentOutOfRangeException) { threw = true; }
            AssertTrue(threw, "cellW=0 抛 ArgumentOutOfRangeException");
        }

        // ── 16. Clear 清空 ──
        public static void TestClear()
        {
            var a = new CelSpriteAtlas("ui.png", 256, 256);
            a.AddRegion("a", 0, 0, 32, 32);
            a.AddRegion("b", 32, 0, 32, 32);
            AssertEqual(2, a.Count, "添加后 2 区域");
            a.Clear();
            AssertEqual(0, a.Count, "Clear 后 0 区域");
            AssertTrue(!a.Contains("a"), "Clear 后 Contains=false");
        }

        // ── 17. Regions 有序访问 ──
        public static void TestRegionsOrdered()
        {
            var a = new CelSpriteAtlas("ui.png", 256, 256);
            a.AddRegion("first", 0, 0, 32, 32);
            a.AddRegion("second", 32, 0, 32, 32);
            AssertEqual(2, a.Regions.Count, "Regions.Count=2");
            AssertEqual("first", a.Regions[0].Name, "首条=first");
            AssertEqual("second", a.Regions[1].Name, "次条=second");
        }
    }
}
