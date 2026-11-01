// =============================================================================
//  CelSpriteGen.cs — 程序化生成赛璐璐风格角色精灵图（CPU 逐像素）
//
//  生成 256x256 RGBA 二次元少女头像：
//    · 头发/脸部带柔和垂直渐变 → GPU 的赛璐璐量化(BandCount)会把渐变切成 N 段硬边色阶
//    · 无内建描边 → 依赖 GPU 侧 4 向 alpha 描边(OutlineWidth)展示线稿效果
//    · 大眼高光、腮红、衣服色块 → 二次元特征
//
//  这样无需任何外部美术资源即可在真实 GPU 上验证 CelShading2D 的
//  量化 / 描边 / 边缘光 / 半调网点 四项赛璐璐效果。
// =============================================================================

namespace YamaCel2DSandbox;

internal static class CelSpriteGen
{
    public const int Size = 256;

    // ── 色板（二次元可爱风）──
    private static readonly (int r, int g, int b) SkinLight = (255, 231, 207); // 亮肤色
    private static readonly (int r, int g, int b) SkinShadow = (240, 196, 168); // 肤色阴影
    private static readonly (int r, int g, int b) HairLight = (164, 112, 232); // 头发亮(紫)
    private static readonly (int r, int g, int b) HairDark = (92, 52, 172);    // 头发暗(紫)
    private static readonly (int r, int g, int b) EyeIris = (88, 200, 250);    // 瞳色(蓝)
    private static readonly (int r, int g, int b) EyeIrisDark = (44, 118, 196);
    private static readonly (int r, int g, int b) EyeWhite = (255, 255, 255);
    private static readonly (int r, int g, int b) Cloth = (255, 143, 184);     // 衣服(粉)
    private static readonly (int r, int g, int b) Blush = (255, 174, 180);     // 腮红
    private static readonly (int r, int g, int b) Mouth = (214, 108, 128);     // 嘴

    /// <summary>生成 256x256 RGBA 角色精灵（行序自顶向下）。</summary>
    public static byte[] GenerateCharacter()
    {
        int n = Size;
        var px = new byte[n * n * 4];

        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                (int r, int g, int b) col = default;
                bool has = false;

                // 1) 衣服肩部（最底层）
                if (InEllipse(x, y, 128, 234, 86, 40))
                {
                    col = Cloth;
                    has = true;
                }

                // 2) 头发后层（大椭圆 + 垂直渐变）
                if (InEllipse(x, y, 128, 112, 102, 108))
                {
                    float t = Clamp01((y - 6f) / 214f);          // 上亮下暗
                    col = Lerp(HairLight, HairDark, t);
                    has = true;
                }

                // 3) 脸（椭圆 + 右下柔和阴影渐变）
                if (InEllipse(x, y, 128, 150, 63, 57))
                {
                    // 阴影: 偏向脸部右下, 距左上越远越暗
                    float sh = Clamp01(((x - 68f) + (y - 96f)) / 190f) * 0.55f;
                    col = Lerp(SkinLight, SkinShadow, sh);
                    has = true;
                }

                // 4) 刘海（覆盖额头上部: 3 片椭圆弧, 头发色渐变）
                if (InFringe(x, y))
                {
                    float t = Clamp01((y - 30f) / 130f);
                    col = Lerp(HairLight, HairDark, t);
                    has = true;
                }

                // 5) 眼睛（大眼 + 瞳孔 + 双高光）
                if (DrawEyes(x, y, ref col)) has = true;

                // 6) 腮红（两片淡粉椭圆）
                if (InEllipse(x, y, 82, 180, 15, 8) || InEllipse(x, y, 174, 180, 15, 8))
                {
                    col = Blush;
                    has = true;
                }

                // 7) 嘴（小椭圆弧）
                if (InEllipse(x, y, 128, 192, 11, 6))
                {
                    col = Mouth;
                    has = true;
                }

                if (has)
                {
                    int i = (y * n + x) * 4;
                    px[i + 0] = (byte)clamp(col.r);
                    px[i + 1] = (byte)clamp(col.g);
                    px[i + 2] = (byte)clamp(col.b);
                    px[i + 3] = 255;
                }
                // 否则保持透明 (a=0)
            }
        }

        return px;
    }

    // ── 几何判断 ──

    private static bool InEllipse(int x, int y, float cx, float cy, float rx, float ry)
    {
        float dx = (x - cx) / rx;
        float dy = (y - cy) / ry;
        return dx * dx + dy * dy <= 1f;
    }

    /// <summary>刘海：3 片椭圆弧覆盖额头上部（在脸/头发范围内）。</summary>
    private static bool InFringe(int x, int y)
    {
        if (y > 138) return false; // 不盖住眼睛
        // 左/中/右 三片
        if (InEllipse(x, y, 88, 96, 42, 62)) return true;
        if (InEllipse(x, y, 128, 90, 46, 66)) return true;
        if (InEllipse(x, y, 168, 96, 42, 62)) return true;
        return false;
    }

    /// <summary>绘制双眼；返回是否命中（用于覆盖层）。</summary>
    private static bool DrawEyes(int x, int y, ref (int r, int g, int b) col)
    {
        bool hit = false;

        // 左眼中心(100,162) 右眼中心(156,162)
        foreach (var (cx, cy) in new[] { (100f, 162f), (156f, 162f) })
        {
            // 眼白
            if (InEllipse(x, y, cx, cy, 23, 21))
            {
                col = EyeWhite;
                hit = true;
            }
            // 瞳孔（蓝, 上亮下暗）
            if (InEllipse(x, y, cx, cy + 2, 14, 17))
            {
                float t = Clamp01((y - (cy - 16f)) / 36f);
                col = Lerp(EyeIris, EyeIrisDark, t);
                hit = true;
            }
            // 大高光（左上）
            if (InEllipse(x, y, cx - 5, cy - 7, 5.5f, 5f))
            {
                col = EyeWhite;
                hit = true;
            }
            // 小高光（右下）
            if (InEllipse(x, y, cx + 6, cy + 6, 2.5f, 2.5f))
            {
                col = (255, 255, 255);
                hit = true;
            }
            // 上睫毛（深色条带）
            if (InEllipse(x, y, cx, cy - 18, 24, 6) && y < cy - 14)
            {
                col = (52, 40, 82);
                hit = true;
            }
        }

        return hit;
    }

    // ── 工具 ──

    private static float Clamp01(float v) => v < 0 ? 0 : (v > 1 ? 1 : v);
    private static int clamp(int v) => v < 0 ? 0 : (v > 255 ? 255 : v);

    private static (int, int, int) Lerp((int, int, int) a, (int, int, int) b, float t)
    {
        int r = (int)(a.Item1 + (b.Item1 - a.Item1) * t);
        int g = (int)(a.Item2 + (b.Item2 - a.Item2) * t);
        int bl = (int)(a.Item3 + (b.Item3 - a.Item3) * t);
        return (r, g, bl);
    }
}
