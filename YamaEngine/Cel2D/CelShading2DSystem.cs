// ============================================================================================
//  CelShading2DSystem.cs
//
//  yama引擎 (YamaEngine) 赛璐璐 2D 系统 —— 把数据模型 + 渲染器 + 2D 相机拧成一条可用管线。
//
//  用法:
//    var cel = new CelShading2DSystem(device, resolveTextureRef);
//    cel.Initialize();
//    cel.Update(dt);                       // 推进所有精灵动画
//    cel.Draw(cmd, rtv, target, camera);   // 按纹理批量绘制
//
//  命名空间：YamaEngine.Cel2D  ·  目标框架：net10.0
// ============================================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.RHI;

namespace YamaEngine.Cel2D
{
    /// <summary>已解析的赛璐璐纹理引用（SRV 句柄 + 纹理尺寸，用于描边/网点 UV 计算）。</summary>
    public readonly struct CelTextureRef
    {
        public readonly RHIDescriptorHandle SRV;
        public readonly Vector2 Size;
        public bool IsValid => SRV.IsValid && Size.X > 0 && Size.Y > 0;

        public CelTextureRef(RHIDescriptorHandle srv, Vector2 size)
        {
            SRV = srv;
            Size = size;
        }
    }

    /// <summary>2D 正交相机（像素空间，原点左上，Y 向下）。</summary>
    public struct Cel2DCamera
    {
        /// <summary>视口尺寸（像素）。</summary>
        public Vector2 ViewportSize;

        /// <summary>相机中心（像素坐标）。</summary>
        public Vector2 Center;

        /// <summary>缩放（1 = 1:1）。</summary>
        public float Zoom;

        /// <summary>背景色。</summary>
        public Vector4 ClearColor;

        public Cel2DCamera(Vector2 viewportSize, Vector2 center = default, float zoom = 1f, Vector4? clearColor = null)
        {
            ViewportSize = viewportSize;
            Center = center;
            Zoom = zoom <= 0 ? 1f : zoom;
            ClearColor = clearColor ?? new Vector4(0.13f, 0.13f, 0.16f, 1f);
        }

        /// <summary>构建像素空间正交投影（Y 向下）。</summary>
        public Matrix4x4 BuildProjection()
        {
            float halfW = ViewportSize.X * 0.5f / Zoom;
            float halfH = ViewportSize.Y * 0.5f / Zoom;
            float left = Center.X - halfW;
            float right = Center.X + halfW;
            float top = Center.Y - halfH;
            float bottom = Center.Y + halfH;
            return Matrix4x4.CreateOrthographicOffCenter(left, right, bottom, top, -1f, 1f);
        }

        /// <summary>相机世界位置（Center 别名，方便跟随）。</summary>
        public Vector2 Position
        {
            get => Center;
            set => Center = value;
        }

        /// <summary>世界坐标 → 屏幕坐标（像素）。</summary>
        public Vector2 WorldToScreen(Vector2 world)
        {
            Vector2 rel = world - Center;
            return new Vector2(
                ViewportSize.X * 0.5f + rel.X * Zoom,
                ViewportSize.Y * 0.5f + rel.Y * Zoom);
        }

        /// <summary>屏幕坐标（像素） → 世界坐标。</summary>
        public Vector2 ScreenToWorld(Vector2 screen)
        {
            Vector2 rel = screen - ViewportSize * 0.5f;
            return new Vector2(
                Center.X + rel.X / Zoom,
                Center.Y + rel.Y / Zoom);
        }
    }

    /// <summary>
    /// 赛璐璐 2D 系统：管理精灵/动画，按纹理批量绘制。
    /// </summary>
    public sealed class CelShading2DSystem : IDisposable
    {
        private readonly CelSpriteRenderer _renderer;
        private readonly Func<string, CelTextureRef?> _resolveTextureRef;
        private readonly Dictionary<string, List<CelDrawItem>> _batch = new();

        public CelShading2DSystem(RHIDevice device,
            Func<string, CelTextureRef?> resolveTextureRef,
            RHIFormat rtvFormat = RHIFormat.R16G16B16A16_Float)
        {
            _resolveTextureRef = resolveTextureRef ?? throw new ArgumentNullException(nameof(resolveTextureRef));
            _renderer = new CelSpriteRenderer(device, rtvFormat);
        }

        public void Initialize() => _renderer.Initialize();

        /// <summary>推进所有精灵动画，更新其源矩形。</summary>
        public void Update(float deltaTime, IEnumerable<CelSpriteData> sprites)
        {
            if (sprites == null) return;
            foreach (var s in sprites)
            {
                if (s.Player != null)
                    s.SourceRect = s.Player.Update(deltaTime);
            }
        }

        /// <summary>
        /// 绘制精灵集合（按纹理自动分批）。
        /// </summary>
        public void Draw(RHICommandList cmd, RHIDescriptorHandle rtv, RHITexture target,
            in Cel2DCamera camera, IEnumerable<CelSpriteData> sprites, float time = 0)
        {
            if (sprites == null) return;
            _batch.Clear();

            foreach (var s in sprites)
            {
                if (s == null || string.IsNullOrEmpty(s.TexturePath)) continue;
                var texRef = _resolveTextureRef(s.TexturePath);
                if (!texRef.HasValue || !texRef.Value.IsValid) continue;

                // 翻转：调整源矩形 UV
                var rect = s.SourceRect;
                if (s.FlipX) (rect.Z, rect.X) = (rect.X, rect.Z);
                if (s.FlipY) (rect.W, rect.Y) = (rect.Y, rect.W);

                var item = new CelDrawItem
                {
                    Position = s.Position,
                    Size = s.Size,
                    Z = s.Z,
                    UvRect = rect,
                    Color = s.Tint * s.Material.BaseColor,
                    BandCount = s.Material.EnableBanding ? s.Material.BandCount : 1f,
                    OutlineWidth = s.Material.EnableOutline ? s.Material.OutlineWidth : 0f,
                    RimIntensity = s.Material.RimIntensity,
                    Halftone = s.Material.HalftoneDensity
                };

                if (!_batch.TryGetValue(s.TexturePath, out var list))
                    _batch[s.TexturePath] = list = new List<CelDrawItem>();
                list.Add(item);
            }

            var proj = camera.BuildProjection();
            foreach (var kvp in _batch)
            {
                if (kvp.Value.Count == 0) continue;
                var texRef = _resolveTextureRef(kvp.Key);
                if (!texRef.HasValue || !texRef.Value.IsValid) continue;
                var size = texRef.Value.Size;
                var texel = new Vector2(1f / size.X, 1f / size.Y);
                _renderer.Draw(cmd, rtv, target, texRef.Value.SRV, kvp.Value, in proj, texel, time);
            }
        }

        public void Dispose() => _renderer.Dispose();
    }
}
