// ============================================================================================
//  CelSpriteAtlas.cs
//
//  yama引擎 (YamaEngine) 赛璐璐 / 二次元 2D 子系统 —— 精灵图集
//
//  把多张小图打包进一张图集纹理，减少 draw call / 状态切换。
//  纯数据/逻辑层：只负责区域管理与 UV 计算，不含 GPU 上传；
//  图集纹理本身由渲染层按 AtlasTexturePath 解析（与 CelShading2DSystem 的 resolveTextureRef 一致）。
//
//  典型用途: 动画帧、UI 图标、角色部件都放在一张图集里，用区域名查询 UV。
//  命名空间：YamaEngine.Cel2D  ·  目标框架：net10.0
// ============================================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.Cel2D
{
    /// <summary>图集中的一个区域（子图）。</summary>
    public readonly struct CelAtlasRegion
    {
        /// <summary>区域名。</summary>
        public readonly string Name;

        /// <summary>区域像素矩形（图集内）。</summary>
        public readonly int X;
        public readonly int Y;
        public readonly int Width;
        public readonly int Height;

        /// <summary>所属图集纹理路径。</summary>
        public readonly string AtlasTexturePath;

        /// <summary>图集总尺寸（用于归一化 UV）。</summary>
        public readonly Vector2 AtlasSize;

        public CelAtlasRegion(string name, int x, int y, int width, int height,
            string atlasTexturePath, Vector2 atlasSize)
        {
            Name = name;
            X = x;
            Y = y;
            Width = width;
            Height = height;
            AtlasTexturePath = atlasTexturePath;
            AtlasSize = atlasSize;
        }

        /// <summary>区域是否有效（落在图集内）。</summary>
        public readonly bool IsValid => Width > 0 && Height > 0 && AtlasSize.X > 0 && AtlasSize.Y > 0;

        /// <summary>区域中心（像素）。</summary>
        public readonly Vector2 Center => new(X + Width * 0.5f, Y + Height * 0.5f);

        /// <summary>区域尺寸（像素）。</summary>
        public readonly Vector2 Size => new(Width, Height);

        /// <summary>归一化 UV 矩形（x=u0,y=v0,z=u1,w=v1），供 CelSpriteData.SourceRect 使用。</summary>
        public readonly Vector4 SourceRect
        {
            get
            {
                if (!IsValid) return new Vector4(0, 0, 1, 1);
                float invW = 1f / AtlasSize.X;
                float invH = 1f / AtlasSize.Y;
                return new Vector4(X * invW, Y * invH, (X + Width) * invW, (Y + Height) * invH);
            }
        }
    }

    /// <summary>
    /// 赛璐璐 2D 精灵图集：管理区域、提供按名查询与网格生成。
    /// </summary>
    public sealed class CelSpriteAtlas
    {
        private readonly Dictionary<string, CelAtlasRegion> _regions = new(StringComparer.Ordinal);
        private readonly List<CelAtlasRegion> _ordered = new();

        /// <summary>图集纹理路径。</summary>
        public string AtlasTexturePath { get; }

        /// <summary>图集总尺寸。</summary>
        public Vector2 AtlasSize { get; }

        public IReadOnlyList<CelAtlasRegion> Regions => _ordered;
        public int Count => _ordered.Count;

        public CelSpriteAtlas(string atlasTexturePath, Vector2 atlasSize)
        {
            AtlasTexturePath = string.IsNullOrEmpty(atlasTexturePath) ? "atlas" : atlasTexturePath;
            AtlasSize = atlasSize;
        }

        public CelSpriteAtlas(string atlasTexturePath, int width, int height)
            : this(atlasTexturePath, new Vector2(width, height)) { }

        /// <summary>添加一个区域。</summary>
        public CelAtlasRegion AddRegion(string name, int x, int y, int width, int height)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("区域名不能为空", nameof(name));
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), "区域宽高必须为正");

            var region = new CelAtlasRegion(name, x, y, width, height, AtlasTexturePath, AtlasSize);
            _regions[name] = region;
            _ordered.Add(region);
            return region;
        }

        /// <summary>按名查询区域；不存在返回 null。</summary>
        public CelAtlasRegion? GetRegion(string name)
            => name != null && _regions.TryGetValue(name, out var r) ? r : null;

        /// <summary>按名查询区域 UV；不存在返回整图。</summary>
        public Vector4 GetSourceRect(string name)
            => GetRegion(name)?.SourceRect ?? new Vector4(0, 0, 1, 1);

        public bool Contains(string name) => name != null && _regions.ContainsKey(name);
        public bool TryGetRegion(string name, out CelAtlasRegion region)
            => (region = GetRegion(name) ?? default).IsValid;

        /// <summary>把图集按网格切分（每格 cellW×cellH），自动命名 name_0, name_1, ...。</summary>
        public int AddGrid(string namePrefix, int cellW, int cellH, int margin = 0, int spacing = 0)
        {
            if (cellW <= 0 || cellH <= 0)
                throw new ArgumentOutOfRangeException(nameof(cellW), "格子尺寸必须为正");

            int count = 0;
            int totalW = (int)AtlasSize.X;
            int totalH = (int)AtlasSize.Y;
            for (int y = margin; y + cellH <= totalH - margin; y += cellH + spacing)
            {
                for (int x = margin; x + cellW <= totalW - margin; x += cellW + spacing)
                {
                    AddRegion($"{namePrefix}_{count}", x, y, cellW, cellH);
                    count++;
                }
            }
            return count;
        }

        /// <summary>删除全部区域。</summary>
        public void Clear()
        {
            _regions.Clear();
            _ordered.Clear();
        }
    }
}
