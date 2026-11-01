using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.Cel2D
{
    /// <summary>
    /// 2D 渲染层：为赛璐璐二次元场景提供前后层分组。
    /// 层顺序：索引越小越靠后（背景），越大越靠前（前景/UI）。
    /// </summary>
    public sealed class CelRenderLayer2D
    {
        /// <summary>层名称（用于查找）。</summary>
        public string Name { get; set; } = "Layer";

        /// <summary>层索引（0 = 最底）。越大越靠前。</summary>
        public int Index { get; set; }

        /// <summary>是否启用（禁用层不参与绘制）。</summary>
        public bool Visible { get; set; } = true;

        /// <summary>该层整体透明度（0..1）。</summary>
        public float Opacity { get; set; } = 1.0f;

        /// <summary>该层是否受灯光影响（如 UI / 特效层可关）。</summary>
        public bool AffectedByLight { get; set; } = true;

        /// <summary>该层是否启用赛璐璐量化（如 UI 层可关）。</summary>
        public bool EnableBanding { get; set; } = true;

        /// <summary>该层内的精灵。</summary>
        public List<CelSpriteData> Sprites { get; } = new();

        /// <summary>该层内的 2D 粒子（可选）。</summary>
        public List<CelParticle2D> Particles { get; } = new();

        public CelRenderLayer2D() { }

        public CelRenderLayer2D(string name, int index)
        {
            Name = name;
            Index = index;
        }
    }

    /// <summary>
    /// 2D 渲染层管理器：按层分组精灵，支持层排序、可见性、透明与逐层过滤。
    /// 纯数据层；<see cref="CelShading2DSystem"/> 消费其分组结果绘制。
    /// </summary>
    public sealed class CelRenderLayers2D
    {
        private readonly List<CelRenderLayer2D> _layers = new();

        public IReadOnlyList<CelRenderLayer2D> Layers => _layers;

        /// <summary>
        /// 添加层（按 Index 排序插入）。
        /// </summary>
        public CelRenderLayer2D AddLayer(string name, int index, bool visible = true)
        {
            var layer = new CelRenderLayer2D(name, index) { Visible = visible };
            InsertSorted(layer);
            return layer;
        }

        /// <summary>获取或创建层。</summary>
        public CelRenderLayer2D GetOrCreateLayer(string name, int index)
        {
            foreach (var l in _layers)
            {
                if (string.Equals(l.Name, name, StringComparison.Ordinal))
                    return l;
            }
            return AddLayer(name, index);
        }

        public CelRenderLayer2D? GetLayer(string name)
        {
            foreach (var l in _layers)
            {
                if (string.Equals(l.Name, name, StringComparison.Ordinal))
                    return l;
            }
            return null;
        }

        public bool RemoveLayer(string name)
        {
            var layer = GetLayer(name);
            return layer != null && _layers.Remove(layer);
        }

        public void Clear()
        {
            _layers.Clear();
        }

        /// <summary>
        /// 把精灵放入指定层。若层不存在则创建。
        /// </summary>
        public void Assign(CelSpriteData sprite, string layerName, int layerIndex = 0)
        {
            var layer = GetOrCreateLayer(layerName, layerIndex);
            layer.Sprites.Add(sprite);
        }

        /// <summary>
        /// 按层顺序（后台→前台）返回所有可见层中开启的精灵。
        /// </summary>
        public IEnumerable<CelSpriteData> FlattenVisibleSprites()
        {
            foreach (var layer in _layers)
            {
                if (!layer.Visible || layer.Opacity <= 0.0f)
                    continue;
                foreach (var s in layer.Sprites)
                    yield return s;
            }
        }

        /// <summary>
        /// 按层顺序返回所有可见层的粒子。
        /// </summary>
        public IEnumerable<CelParticle2D> FlattenVisibleParticles()
        {
            foreach (var layer in _layers)
            {
                if (!layer.Visible || layer.Opacity <= 0.0f)
                    continue;
                foreach (var p in layer.Particles)
                    yield return p;
            }
        }

        /// <summary>
        /// 收集某层（含）之前的所有可见精灵，用于背景裁剪。
        /// </summary>
        public IEnumerable<CelSpriteData> VisibleUpTo(string layerName)
        {
            foreach (var layer in _layers)
            {
                if (layer.Visible && layer.Opacity > 0.0f)
                {
                    foreach (var s in layer.Sprites)
                        yield return s;
                }
                if (string.Equals(layer.Name, layerName, StringComparison.Ordinal))
                    yield break;
            }
        }

        private void InsertSorted(CelRenderLayer2D layer)
        {
            int i = 0;
            while (i < _layers.Count && _layers[i].Index <= layer.Index)
                i++;
            _layers.Insert(i, layer);
        }
    }
}
