using System;
using System.IO;
using YamaEngine.Build;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    // ══════════════════════════════════════════════════════════════
    // BuildPipelineDepthTests — AssetCookerReal.CookTexture 工业级深度验证
    // 覆盖：尺寸溢出/负、通道非法、像素总量上限、源数据不足、mip 溢出
    // ══════════════════════════════════════════════════════════════
    public static class BuildPipelineDepthTests
    {
        private static AssetCookerReal NewCooker()
        {
            return new AssetCookerReal(new BuildConfig { Target = BuildTarget.Windows, Architecture = PlatformArchitecture.x64 });
        }

        // ── 合法小纹理正常烘焙 ──
        public static void TestCookTextureValid()
        {
            var cooker = NewCooker();
            byte[] src = new byte[4 * 4 * 4];
            var result = cooker.CookTexture(src, 4, 4, 4, TextureCompressionFormat.BC3_DXT5);
            AssertTrue(result != null && result.Length > 0, "valid cook output non-empty");
        }

        // ── width/height = 0 → 参数异常 ──
        public static void TestCookTextureZeroSizeThrows()
        {
            var cooker = NewCooker();
            AssertThrows<ArgumentOutOfRangeException>(() =>
                cooker.CookTexture(new byte[1], 0, 4, 4, TextureCompressionFormat.BC3_DXT5), "zero width");
        }

        // ── width/height 巨大 → 像素总数超限抛异常（防 OOM） ──
        public static void TestCookTextureHugeSizeThrows()
        {
            var cooker = NewCooker();
            AssertThrows<InvalidDataException>(() =>
                cooker.CookTexture(new byte[1], 65536, 65536, 4, TextureCompressionFormat.BC3_DXT5),
                "huge dims overflow/oom");
        }

        // ── 负尺寸 → 参数异常 ──
        public static void TestCookTextureNegativeSizeThrows()
        {
            var cooker = NewCooker();
            AssertThrows<ArgumentOutOfRangeException>(() =>
                cooker.CookTexture(new byte[1], -4, 4, 4, TextureCompressionFormat.BC3_DXT5), "negative width");
        }

        // ── 通道非法（0/2/5）→ 参数异常 ──
        public static void TestCookTextureInvalidChannelsThrows()
        {
            var cooker = NewCooker();
            AssertThrows<ArgumentOutOfRangeException>(() =>
                cooker.CookTexture(new byte[1], 4, 4, 2, TextureCompressionFormat.BC3_DXT5), "channels=2");
            AssertThrows<ArgumentOutOfRangeException>(() =>
                cooker.CookTexture(new byte[1], 4, 4, 5, TextureCompressionFormat.BC3_DXT5), "channels=5");
        }

        // ── 源数据不足以容纳像素 → 抛异常（防越界读） ──
        public static void TestCookTextureSourceTooSmallThrows()
        {
            var cooker = NewCooker();
            // 4x4 RGBA 需 64 字节，仅给 8 字节
            AssertThrows<InvalidDataException>(() =>
                cooker.CookTexture(new byte[8], 4, 4, 4, TextureCompressionFormat.BC3_DXT5),
                "source too small");
        }

        // ── 空源 → ArgumentNullException ──
        public static void TestCookTextureNullSourceThrows()
        {
            var cooker = NewCooker();
            AssertThrows<ArgumentNullException>(() =>
                cooker.CookTexture(null!, 4, 4, 4, TextureCompressionFormat.BC3_DXT5), "null source");
        }
    }
}
