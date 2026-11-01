// ============================================================================================
//  Cel9Slice.cs
//
//  yama引擎 (YamaEngine) 赛璐璐 / 二次元 2D 子系统 —— 九宫格拉伸
//
//  用于 UI 面板 / 按钮 / 对话框边框：四角保持原始尺寸，四边单轴拉伸，中心双轴拉伸。
//  避免直接拉伸整张图导致的角部模糊 / 变形，是二次元 UI 的常见做法。
//
//  纯数据/逻辑层：输入图集区域 + 目标矩形 + 边框宽度，输出 9 个 quad。
//  命名空间：YamaEngine.Cel2D  ·  目标框架：net10.0
// ============================================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.Cel2D
{
    /// <summary>九宫格边框宽度（像素，图集内）。</summary>
    public readonly struct Cel9SliceBorder
    {
        public readonly float Left;
        public readonly float Top;
        public readonly float Right;
        public readonly float Bottom;

        public Cel9SliceBorder(float left, float top, float right, float bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

        public Cel9SliceBorder(float uniform)
            : this(uniform, uniform, uniform, uniform) { }

        public readonly bool IsZero => Left <= 0 && Top <= 0 && Right <= 0 && Bottom <= 0;
    }

    /// <summary>九宫格输出的一块（位置矩形 + UV 矩形）。</summary>
    public readonly struct CelQuad2D
    {
        /// <summary>目标位置矩形（世界像素，左上原点）。</summary>
        public readonly Vector2 Position;
        public readonly Vector2 Size;

        /// <summary>UV 矩形（x=u0,y=v0,z=u1,w=v1）。</summary>
        public readonly Vector4 SourceRect;

        public CelQuad2D(Vector2 position, Vector2 size, Vector4 sourceRect)
        {
            Position = position;
            Size = size;
            SourceRect = sourceRect;
        }

        /// <summary>中心坐标（世界像素）。</summary>
        public readonly Vector2 Center => Position + Size * 0.5f;
    }

    /// <summary>
    /// 九宫格构建器：把图集区域按边框拆成 9 块，映射到目标矩形。
    /// </summary>
    public static class Cel9SliceBuilder
    {
        /// <summary>
        /// 构建 9 宫格 quad 列表。
        /// </summary>
        /// <param name="region">图集区域（源图）。</param>
        /// <param name="destX">目标矩形左上角 X（世界像素）。</param>
        /// <param name="destY">目标矩形左上角 Y。</param>
        /// <param name="destW">目标宽度。</param>
        /// <param name="destH">目标高度。</param>
        /// <param name="border">边框宽度。</param>
        /// <returns>9 个 quad（可能少于 9 个，若目标过小则裁剪）。</returns>
        public static List<CelQuad2D> Build(CelAtlasRegion region, float destX, float destY,
            float destW, float destH, Cel9SliceBorder border)
        {
            var quads = new List<CelQuad2D>(9);
            if (!region.IsValid || destW <= 0 || destH <= 0) return quads;

            // 源矩形（像素，图集内）
            float sx0 = region.X, sy0 = region.Y;
            float sx1 = region.X + region.Width, sy1 = region.Y + region.Height;

            // 边框像素（源）
            float bl = Math.Min(border.Left, region.Width * 0.5f);
            float bt = Math.Min(border.Top, region.Height * 0.5f);
            float br = Math.Min(border.Right, region.Width * 0.5f);
            float bb = Math.Min(border.Bottom, region.Height * 0.5f);

            float invW = 1f / region.AtlasSize.X;
            float invH = 1f / region.AtlasSize.Y;

            // 目标水平切分点
            float destInnerW = Math.Max(destW - bl - br, 0);
            float destInnerH = Math.Max(destH - bt - bb, 0);

            // 水平：左 | 中 | 右
            var cols = new (float src, float dest, float w)[3]
            {
                (bl, destX, bl),                                     // 左
                (region.Width - bl - br, destX + bl, destInnerW),    // 中
                (br, destX + bl + destInnerW, br)                    // 右
            };

            // 垂直：上 | 中 | 下
            var rows = new (float src, float dest, float h)[3]
            {
                (bt, destY, bt),
                (region.Height - bt - bb, destY + bt, destInnerH),
                (bb, destY + bt + destInnerH, bb)
            };

            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 3; c++)
                {
                    var (srcW, destXc, w) = cols[c];
                    var (srcH, destYc, h) = rows[r];
                    if (w <= 0.001f || h <= 0.001f) continue; // 目标过小，跳过

                    // 源 UV（该块）
                    float usrc0 = sx0 + (c == 0 ? 0 : (c == 1 ? bl : region.Width - br));
                    float vsrc0 = sy0 + (r == 0 ? 0 : (r == 1 ? bt : region.Height - bb));
                    float usrc1 = usrc0 + srcW;
                    float vsrc1 = vsrc0 + srcH;

                    var quad = new CelQuad2D(
                        new Vector2(destXc, destYc),
                        new Vector2(w, h),
                        new Vector4(usrc0 * invW, vsrc0 * invH, usrc1 * invW, vsrc1 * invH));
                    quads.Add(quad);
                }
            }

            return quads;
        }

        /// <summary>便捷：把 quad 转成 CelSpriteData（默认材质，用于 CelShading2DSystem 渲染）。</summary>
        public static CelSpriteData ToSprite(string texturePath, in CelQuad2D quad, CelToonMaterial? material = null)
        {
            return new CelSpriteData
            {
                TexturePath = texturePath,
                Position = quad.Center,
                Size = quad.Size,
                SourceRect = quad.SourceRect,
                Tint = Vector4.One,
                Material = material ?? new CelToonMaterial { EnableOutline = false }
            };
        }

        /// <summary>便捷：一次构建并转成精灵列表。</summary>
        public static List<CelSpriteData> BuildSprites(CelAtlasRegion region,
            float destX, float destY, float destW, float destH,
            Cel9SliceBorder border, CelToonMaterial? material = null)
        {
            var result = new List<CelSpriteData>();
            foreach (var q in Build(region, destX, destY, destW, destH, border))
                result.Add(ToSprite(region.AtlasTexturePath, q, material));
            return result;
        }
    }
}
