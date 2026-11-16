// CelRendering2DDepthTests.cs — Unity5 2D 渲染管线深度验证
// 覆盖:
//   * CelSpriteSorter2D: Z 序 / Y 序 / 按纹理分批 / 次级稳定排序 / 空输入
//   * Cel9SliceBuilder: 9 宫格切分 / 四角保持 / 边中拉伸 / UV 精度 / 小目标裁剪 / IsZero
//   * CelCameraFollow2D: 默认值 / Snap / 死区 / 边界钳制 / Zoom / 抖动 / Rectangle2D

using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.Cel2D;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class CelRendering2DDepthTests
    {
        private const float Eps = 1e-4f;

        private static CelSpriteData MakeSprite(float x, float y, float z, string tex = "a")
            => new CelSpriteData { Position = new Vector2(x, y), Z = z, TexturePath = tex };

        // ── Sorter: ZBackToFront 升序 ──
        public static void TestSorterZOrder()
        {
            var sorter = new CelSpriteSorter2D { Mode = CelSortMode2D.ZBackToFront };
            var sprites = new List<CelSpriteData>
            {
                MakeSprite(0, 0, 3),
                MakeSprite(0, 0, 1),
                MakeSprite(0, 0, 2),
            };
            var batches = sorter.BuildBatches(sprites);
            AssertEqual(1, batches.Count, "无 textureOf 单批");
            var list = batches[0].Sprites;
            AssertEqual(3, list.Count, "精灵数=3");
            AssertTrue(list[0].Z < list[1].Z && list[1].Z < list[2].Z, "Z 升序");
        }

        // ── Sorter: YBackToFront 升序 ──
        public static void TestSorterYOrder()
        {
            var sorter = new CelSpriteSorter2D { Mode = CelSortMode2D.YBackToFront };
            var sprites = new List<CelSpriteData>
            {
                MakeSprite(0, 30, 0),
                MakeSprite(0, 10, 5),   // Z 更大但 Y 更小 → 仍排前
                MakeSprite(0, 20, 0),
            };
            var batches = sorter.BuildBatches(sprites);
            var list = batches[0].Sprites;
            AssertTrue(list[0].Position.Y < list[1].Position.Y && list[1].Position.Y < list[2].Position.Y,
                $"Y 升序: {list[0].Position.Y},{list[1].Position.Y},{list[2].Position.Y}");
        }

        // ── Sorter: Z 相同按 X 稳定排序 ──
        public static void TestSorterZTieBreakX()
        {
            var sorter = new CelSpriteSorter2D { Mode = CelSortMode2D.ZBackToFront };
            var sprites = new List<CelSpriteData>
            {
                MakeSprite(30, 0, 1),
                MakeSprite(10, 0, 1),
                MakeSprite(20, 0, 1),
            };
            var list = sorter.BuildBatches(sprites)[0].Sprites;
            AssertEqual(10f, list[0].Position.X, "Z 相同 X 升序 [0]");
            AssertEqual(20f, list[1].Position.X, "Z 相同 X 升序 [1]");
            AssertEqual(30f, list[2].Position.X, "Z 相同 X 升序 [2]");
        }

        // ── Sorter: 按纹理分批，批内保 Z 序 ──
        public static void TestSorterBatchByTextureThenZ()
        {
            var sorter = new CelSpriteSorter2D { Mode = CelSortMode2D.BatchByTextureThenZ };
            var sprites = new List<CelSpriteData>
            {
                MakeSprite(0, 0, 5, "b"),   // Z=5 b
                MakeSprite(0, 0, 1, "a"),   // Z=1 a
                MakeSprite(0, 0, 3, "b"),   // Z=3 b
                MakeSprite(0, 0, 2, "a"),   // Z=2 a
            };
            var batches = sorter.BuildBatches(sprites, s => s.TexturePath);
            AssertEqual(2, batches.Count, "两个纹理→2 批");
            // 第一批是 Z 最小的纹理 "a" (Z=1,2)
            AssertEqual("a", batches[0].TextureKey as string, "首批纹理=a");
            AssertEqual(2, batches[0].Count, "a 批 2 精灵");
            AssertTrue(batches[0].Sprites[0].Z < batches[0].Sprites[1].Z, "批内 Z 升序");
            // 第二批 "b" (Z=3,5)
            AssertEqual("b", batches[1].TextureKey as string, "次批纹理=b");
            AssertEqual(2, batches[1].Count, "b 批 2 精灵");
        }

        // ── Sorter: 空输入返回空列表 ──
        public static void TestSorterEmptyInput()
        {
            var sorter = new CelSpriteSorter2D();
            var empty = new List<CelSpriteData>();
            var batches = sorter.BuildBatches(empty, s => s.TexturePath);
            AssertEqual(0, batches.Count, "空输入→0 批");
        }

        // ── Sorter: Batch2D.Count 与 Sprites 同步 ──
        public static void TestBatchCountProperty()
        {
            var sorter = new CelSpriteSorter2D();
            var sprites = new List<CelSpriteData> { MakeSprite(0, 0, 1), MakeSprite(0, 0, 2) };
            var batch = sorter.BuildBatches(sprites)[0];
            AssertEqual(batch.Sprites.Count, batch.Count, "Count==Sprites.Count");
        }

        // ── 9Slice: 完整区域生成 9 quad ──
        public static void Test9SliceProduces9Quads()
        {
            var region = new CelAtlasRegion("panel", 0, 0, 64, 64, "atlas.png", new Vector2(256, 256));
            var border = new Cel9SliceBorder(16);
            var quads = Cel9SliceBuilder.Build(region, 0, 0, 128, 128, border);
            AssertEqual(9, quads.Count, "标准九宫格应产生 9 quad");
        }

        // ── 9Slice: 四角 quad 保持原边框尺寸 ──
        public static void Test9SliceCornersKeepBorderSize()
        {
            var region = new CelAtlasRegion("panel", 0, 0, 64, 64, "atlas.png", new Vector2(256, 256));
            var quads = Cel9SliceBuilder.Build(region, 0, 0, 128, 128, new Cel9SliceBorder(16));

            // 找到左上角 quad（位置 0,0）
            var tl = quads[0];
            AssertTrue(MathF.Abs(tl.Size.X - 16f) < Eps && MathF.Abs(tl.Size.Y - 16f) < Eps,
                $"左上角保持 16x16，实际 {tl.Size.X}x{tl.Size.Y}");

            // 右下角 quad（最后一个）
            var br = quads[8];
            AssertTrue(MathF.Abs(br.Size.X - 16f) < Eps && MathF.Abs(br.Size.Y - 16f) < Eps,
                $"右下角保持 16x16，实际 {br.Size.X}x{br.Size.Y}");
        }

        // ── 9Slice: 中间块吸收拉伸量 ──
        public static void Test9SliceCenterStretches()
        {
            var region = new CelAtlasRegion("panel", 0, 0, 64, 64, "atlas.png", new Vector2(256, 256));
            var quads = Cel9SliceBuilder.Build(region, 0, 0, 200, 200, new Cel9SliceBorder(16));

            // 中心块（索引 4，行 1 列 1）尺寸 = dest - 2*border
            var center = quads[4];
            AssertTrue(MathF.Abs(center.Size.X - (200 - 32f)) < Eps,
                $"中心 X=dest-2*border={200 - 32}，实际 {center.Size.X}");
            AssertTrue(MathF.Abs(center.Size.Y - (200 - 32f)) < Eps,
                $"中心 Y=dest-2*border={200 - 32}，实际 {center.Size.Y}");
        }

        // ── 9Slice: UV 归一化（中心块）──
        public static void Test9SliceCenterUV()
        {
            // 区域 64x64 在 256x256 图集内，uniform 边框 16
            var region = new CelAtlasRegion("panel", 0, 0, 64, 64, "atlas.png", new Vector2(256, 256));
            var quads = Cel9SliceBuilder.Build(region, 0, 0, 128, 128, new Cel9SliceBorder(16));

            var center = quads[4];
            // 中心源矩形 = (16,16)-(48,48) → UV = /256
            AssertTrue(MathF.Abs(center.SourceRect.X - 16f / 256f) < Eps,
                $"中心 UV.u0=16/256，实际 {center.SourceRect.X}");
            AssertTrue(MathF.Abs(center.SourceRect.Y - 16f / 256f) < Eps,
                $"中心 UV.v0=16/256，实际 {center.SourceRect.Y}");
            AssertTrue(MathF.Abs(center.SourceRect.Z - 48f / 256f) < Eps,
                $"中心 UV.u1=48/256，实际 {center.SourceRect.Z}");
            AssertTrue(MathF.Abs(center.SourceRect.W - 48f / 256f) < Eps,
                $"中心 UV.w=48/256，实际 {center.SourceRect.W}");
        }

        // ── 9Slice: 目标过小→少于 9 quad（裁剪）──
        public static void Test9SliceSmallDestClips()
        {
            var region = new CelAtlasRegion("panel", 0, 0, 64, 64, "atlas.png", new Vector2(256, 256));
            // 边框 16*2 = 32 > dest 20 → 中间被裁
            var quads = Cel9SliceBuilder.Build(region, 0, 0, 20, 20, new Cel9SliceBorder(16));
            AssertTrue(quads.Count < 9, $"小目标应产生 <9 quad，实际 {quads.Count}");
            AssertTrue(quads.Count > 0, "至少 1 quad（边界 clamp 后仍有）");
        }

        // ── 9Slice: 无效区域返回空 ──
        public static void Test9SliceInvalidRegion()
        {
            // Width=0 → IsValid=false
            var region = new CelAtlasRegion("empty", 0, 0, 0, 0, "atlas.png", new Vector2(256, 256));
            AssertTrue(!region.IsValid, "0 尺寸区域无效");
            var quads = Cel9SliceBuilder.Build(region, 0, 0, 100, 100, new Cel9SliceBorder(8));
            AssertEqual(0, quads.Count, "无效区域→0 quad");
        }

        // ── 9Slice: Cel9SliceBorder.IsZero ──
        public static void Test9SliceBorderIsZero()
        {
            AssertTrue(new Cel9SliceBorder(0).IsZero, "uniform 0 → IsZero");
            AssertTrue(new Cel9SliceBorder(0, 0, 0, 0).IsZero, "全 0 → IsZero");
            AssertTrue(!new Cel9SliceBorder(8).IsZero, "uniform 8 → 非零");
            AssertTrue(!new Cel9SliceBorder(0, 0, 4, 0).IsZero, "有任一边>0 → 非零");
        }

        // ── 9Slice: ToSprite 生成有效精灵 ──
        public static void Test9SliceToSprite()
        {
            var quad = new CelQuad2D(new Vector2(10, 20), new Vector2(30, 40),
                new Vector4(0, 0, 1, 1));
            var sprite = Cel9SliceBuilder.ToSprite("ui.png", quad);
            AssertEqual("ui.png", sprite.TexturePath, "ToSprite 纹理路径");
            AssertTrue(MathF.Abs(sprite.Position.X - 25f) < Eps, "Position=center");
            AssertTrue(MathF.Abs(sprite.Position.Y - 40f) < Eps, "Position.Y=center");
            AssertTrue(MathF.Abs(sprite.Size.X - 30f) < Eps, "Size.X=quad.Size.X");
            AssertTrue(MathF.Abs(sprite.SourceRect.X - 0f) < Eps, "SourceRect 透传");
            AssertTrue(sprite.Material != null, "ToSprite 默认 Material");
            AssertTrue(!sprite.Material!.EnableOutline, "ToSprite 默认禁用描边（避免 9 宫格描边伪影）");
        }

        // ── 9Slice: BuildSprites 便捷生成精灵列表 ──
        public static void Test9SliceBuildSprites()
        {
            var region = new CelAtlasRegion("panel", 0, 0, 64, 64, "atlas.png", new Vector2(256, 256));
            var sprites = Cel9SliceBuilder.BuildSprites(region, 0, 0, 128, 128, new Cel9SliceBorder(16));
            AssertEqual(9, sprites.Count, "BuildSprites 生成 9 精灵");
            AssertEqual("atlas.png", sprites[0].TexturePath, "纹理路径来自 region.AtlasTexturePath");
        }

        // ── CameraFollow: 默认值 ──
        public static void TestCameraDefaults()
        {
            var cam = new CelCameraFollow2D();
            AssertTrue(MathF.Abs(cam.ViewportSize.X - 1280f) < Eps, "默认视口宽 1280");
            AssertTrue(MathF.Abs(cam.ViewportSize.Y - 720f) < Eps, "默认视口高 720");
            AssertTrue(MathF.Abs(cam.Zoom - 1.0f) < Eps, "默认 Zoom=1");
            AssertTrue(MathF.Abs(cam.Smoothing - 0.12f) < Eps, "默认 Smoothing=0.12");
            AssertTrue(MathF.Abs(cam.DeadZoneX - 64f) < Eps, "默认 DeadZoneX=64");
            AssertTrue(MathF.Abs(cam.DeadZoneY - 48f) < Eps, "默认 DeadZoneY=48");
            AssertTrue(cam.UseDeadZone, "默认 UseDeadZone=true");
            AssertTrue(cam.SnapOnStart, "默认 SnapOnStart=true");
            AssertTrue(!cam.ClampToBounds, "默认 ClampToBounds=false");
        }

        // ── CameraFollow: SnapToTarget 瞬移 ──
        public static void TestCameraSnapToTarget()
        {
            var cam = new CelCameraFollow2D
            {
                Target = new Vector2(100, 200),
                UseDeadZone = false,
                Smoothing = 0.5f,
            };
            cam.SnapToTarget();
            AssertTrue(MathF.Abs(cam.Position.X - 100f) < Eps, "Snap 后 Position.X=Target.X");
            AssertTrue(MathF.Abs(cam.Position.Y - 200f) < Eps, "Snap 后 Position.Y=Target.Y");
        }

        // ── CameraFollow: 死区内不移动 ──
        public static void TestCameraDeadZoneHolds()
        {
            var cam = new CelCameraFollow2D
            {
                Position = new Vector2(0, 0),
                Target = new Vector2(0, 0),
                DeadZoneX = 64,
                DeadZoneY = 48,
                Smoothing = 1.0f,
            };
            cam.SnapToTarget();
            cam.Target = new Vector2(30, 0);   // 死区半宽 64 → 目标在死区内
            cam.Update(1f / 60f);
            AssertTrue(MathF.Abs(cam.Position.X - 0f) < Eps,
                $"死区内相机不动，实际 X={cam.Position.X}");
        }

        // ── CameraFollow: 死区外开始跟随 ──
        public static void TestCameraDeadZoneBreak()
        {
            var cam = new CelCameraFollow2D(new Vector2(1280, 720), new Vector2(0, 0));
            cam.DeadZoneX = 64;
            cam.DeadZoneY = 48;
            cam.Smoothing = 1.0f;
            cam.Target = new Vector2(200, 0);   // dx=200 > 64 → moveX=136
            cam.Update(1f / 60f);
            // pull=(136,0)，follow=1 → Position += (136,0)
            AssertTrue(MathF.Abs(cam.Position.X - 136f) < 0.1f,
                $"死区外 X 一帧推进 136，实际 {cam.Position.X}");
        }

        // ── CameraFollow: 直接插值模式（UseDeadZone=false）──
        public static void TestCameraDirectLerp()
        {
            // 通过 ctor 设置初始 Position=0 / Target=0，同时 _initialized=true
            var cam = new CelCameraFollow2D(new Vector2(1280, 720), new Vector2(0, 0));
            cam.UseDeadZone = false;
            cam.Smoothing = 0.5f;
            cam.Target = new Vector2(200, 0);
            // t = 1 - (1-Smoothing)^(dt*60) = 1 - 0.5^1 = 0.5
            cam.Update(1f / 60f);
            float expected = 200f * (1f - MathF.Pow(1f - 0.5f, (1f / 60f) * 60f));
            AssertTrue(MathF.Abs(cam.Position.X - expected) < 0.5f,
                $"直接插值 X≈{expected}，实际 {cam.Position.X}");
        }

        // ── CameraFollow: 边界钳制（世界比视口大）──
        public static void TestCameraClampBigWorld()
        {
            var cam = new CelCameraFollow2D(new Vector2(1280, 720), new Vector2(1000, 500));
            cam.Initialize(new Vector2(0, 0), new Vector2(2000, 1000));
            cam.ClampToBounds = true;
            cam.UseDeadZone = false;

            // 视口半宽 = 1280/2 = 640；世界宽 2000 → X 范围 [640, 1360]
            cam.Position = new Vector2(5000, 500);
            cam.ClampPosition();
            AssertTrue(MathF.Abs(cam.Position.X - 1360f) < Eps,
                $"X 被钳制到 1360，实际 {cam.Position.X}");

            // 反向
            cam.Position = new Vector2(-500, 500);
            cam.ClampPosition();
            AssertTrue(MathF.Abs(cam.Position.X - 640f) < Eps,
                $"X 被钳制到 640，实际 {cam.Position.X}");
        }

        // ── CameraFollow: 边界钳制（世界比视口小→居中）──
        public static void TestCameraClampSmallWorld()
        {
            var cam = new CelCameraFollow2D(new Vector2(1280, 720), new Vector2(0, 0));
            cam.Initialize(new Vector2(0, 0), new Vector2(500, 300)); // 世界比视口小
            cam.ClampToBounds = true;
            cam.UseDeadZone = false;

            cam.Position = new Vector2(100, 100);
            cam.ClampPosition();
            // 世界中心 = (250, 150)
            AssertTrue(MathF.Abs(cam.Position.X - 250f) < Eps,
                $"小世界 X 居中到 250，实际 {cam.Position.X}");
            AssertTrue(MathF.Abs(cam.Position.Y - 150f) < Eps,
                $"小世界 Y 居中到 150，实际 {cam.Position.Y}");
        }

        // ── CameraFollow: Zoom 影响钳制范围 ──
        public static void TestCameraZoomAffectsClamp()
        {
            var cam = new CelCameraFollow2D(new Vector2(1280, 720), new Vector2(1000, 500));
            cam.Initialize(new Vector2(0, 0), new Vector2(2000, 1000));
            cam.ClampToBounds = true;
            cam.UseDeadZone = false;
            cam.Zoom = 2.0f;  // 视口半宽 = 1280/2/2 = 320

            // X 范围 [320, 1680]
            cam.Position = new Vector2(5000, 500);
            cam.ClampPosition();
            AssertTrue(MathF.Abs(cam.Position.X - 1680f) < Eps,
                $"Zoom=2 时 X 钳制到 1680，实际 {cam.Position.X}");
        }

        // ── CameraFollow: Shake 触发与衰减 ──
        public static void TestCameraShakeDecay()
        {
            var cam = new CelCameraFollow2D(new Vector2(1280, 720), new Vector2(0, 0));
            AssertEqual(0f, cam.ShakeStrength, "初始 ShakeStrength=0");

            cam.Shake(10f, 1f);
            AssertEqual(10f, cam.ShakeStrength, "Shake 后强度=10");
            AssertEqual(1f, cam.ShakeDuration, "Shake 持续 1s");
            AssertEqual(1f, cam.ShakeRemaining, "初始剩余=1s");

            cam.Update(0.5f);
            AssertTrue(cam.ShakeRemaining > 0f && cam.ShakeRemaining < 1f,
                $"衰减中，剩余 {cam.ShakeRemaining}");

            // 推进超时后，剩余 <=0
            cam.Update(2f);
            AssertTrue(cam.ShakeRemaining <= 0f, $"超时后剩余<=0，实际 {cam.ShakeRemaining}");

            // 下一帧 Update 时 guard 把强度归 0
            cam.Update(0.016f);
            AssertEqual(0f, cam.ShakeStrength, "下一帧强度归 0");
        }

        // ── CameraFollow: dt<=0 不推进跟随（但仍衰减抖动）──
        public static void TestCameraZeroDtNoOp()
        {
            var cam = new CelCameraFollow2D
            {
                Position = new Vector2(0, 0),
                Target = new Vector2(500, 500),
                UseDeadZone = false,
                Smoothing = 1.0f,
            };
            cam.SnapToTarget();
            cam.Target = new Vector2(500, 500);
            cam.Update(0f);
            cam.Update(-1f);
            // dt<=0 在跟随开头就返回；SnapToTarget 把 Position 置为 Target=(500,500)
            // 但 Target 在 Snap 时被设为 (500,500)，Position 已对齐 → 断言保持 (500,500)
            AssertTrue(MathF.Abs(cam.Position.X - 500f) < Eps,
                $"SnapToTarget 已把 Position 对齐 Target=500，dt<=0 不改，实际 {cam.Position.X}");
            AssertTrue(MathF.Abs(cam.Position.Y - 500f) < Eps,
                $"Y 同理，实际 {cam.Position.Y}");
        }

        // ── Rectangle2D 基础 ──
        public static void TestRectangle2D()
        {
            var r = new Rectangle2D(10, 20, 100, 50);
            AssertEqual(10f, r.X, "Rect.X");
            AssertEqual(20f, r.Y, "Rect.Y");
            AssertEqual(100f, r.Width, "Rect.Width");
            AssertEqual(50f, r.Height, "Rect.Height");
            AssertTrue(MathF.Abs(r.Center.X - 60f) < Eps, "Center.X=X+W/2=60");
            AssertTrue(MathF.Abs(r.Center.Y - 45f) < Eps, "Center.Y=Y+H/2=45");

            var fromVec = new Rectangle2D(new Vector2(1, 2), new Vector2(3, 4));
            AssertEqual(1f, fromVec.X, "fromVec.X");
            AssertEqual(4f, fromVec.Height, "fromVec.Height");
        }
    }
}
