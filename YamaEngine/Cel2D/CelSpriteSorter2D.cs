using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.Cel2D
{
    /// <summary>
    /// 2D 精灵排序策略。
    /// </summary>
    public enum CelSortMode2D
    {
        /// <summary>按 Z 值（越小越后）。</summary>
        ZBackToFront = 0,

        /// <summary>按 Y 值（越小越后）——用于俯视/类 Isometric 遮挡。</summary>
        YBackToFront = 1,

        /// <summary>按纹理批次聚合后按 Z。</summary>
        BatchByTextureThenZ = 2,
    }

    /// <summary>
    /// 2D 精灵排序 + 批处理器。
    /// 将精灵列表排序并分组为可一次绘制的批次，供 <see cref="CelShading2DSystem"/> 消费。
    /// 纯数据层：只做排序 / 分组，不触碰 RHI。
    /// </summary>
    public sealed class CelSpriteSorter2D
    {
        /// <summary>当前排序策略。</summary>
        public CelSortMode2D Mode { get; set; } = CelSortMode2D.BatchByTextureThenZ;

        /// <summary>
        /// 排序并返回按纹理分组的批次（每批共享同一纹理，可一次 DrawInstanced）。
        /// </summary>
        /// <param name="sprites">待排序精灵。</param>
        /// <param name="textureOf">获取精灵纹理的引用键（用于分组；null 则单批）。</param>
        public List<CelSpriteBatch2D> BuildBatches(
            IReadOnlyList<CelSpriteData> sprites,
            Func<CelSpriteData, object?>? textureOf = null)
        {
            var result = new List<CelSpriteBatch2D>();
            if (sprites == null || sprites.Count == 0)
                return result;

            // 1. 排序副本。
            var sorted = new List<CelSpriteData>(sprites);
            switch (Mode)
            {
                case CelSortMode2D.YBackToFront:
                    sorted.Sort(CompareByY);
                    break;
                case CelSortMode2D.BatchByTextureThenZ:
                case CelSortMode2D.ZBackToFront:
                default:
                    sorted.Sort(CompareByZ);
                    break;
            }

            // 2. 按纹理分组（保持 Z 序）。
            if (textureOf == null)
            {
                var batch = new CelSpriteBatch2D { TextureKey = null };
                batch.Sprites.AddRange(sorted);
                result.Add(batch);
                return result;
            }

            var groups = new Dictionary<object?, List<CelSpriteData>>();
            var order = new List<object?>();
            foreach (var s in sorted)
            {
                object? key = textureOf(s);
                if (!groups.TryGetValue(key, out var list))
                {
                    list = new List<CelSpriteData>();
                    groups[key] = list;
                    order.Add(key);
                }
                list.Add(s);
            }

            foreach (var key in order)
            {
                var batch = new CelSpriteBatch2D { TextureKey = key };
                batch.Sprites.AddRange(groups[key]);
                result.Add(batch);
            }

            return result;
        }

        private static int CompareByZ(CelSpriteData a, CelSpriteData b)
        {
            int cmp = a.Z.CompareTo(b.Z);
            if (cmp != 0) return cmp;
            // Z 相同则按 Y 二次排序，保证上下遮挡稳定。
            cmp = a.Position.Y.CompareTo(b.Position.Y);
            if (cmp != 0) return cmp;
            return a.Position.X.CompareTo(b.Position.X);
        }

        private static int CompareByY(CelSpriteData a, CelSpriteData b)
        {
            int cmp = a.Position.Y.CompareTo(b.Position.Y);
            if (cmp != 0) return cmp;
            return a.Z.CompareTo(b.Z);
        }
    }

    /// <summary>
    /// 一个可一次绘制的精灵批次（共享纹理）。
    /// </summary>
    public sealed class CelSpriteBatch2D
    {
        /// <summary>纹理引用键（可为 null 表示无纹理 / 纯色）。</summary>
        public object? TextureKey { get; set; }

        /// <summary>批次内精灵（已排序）。</summary>
        public List<CelSpriteData> Sprites { get; } = new();

        /// <summary>批次数量。</summary>
        public int Count => Sprites.Count;
    }
}
