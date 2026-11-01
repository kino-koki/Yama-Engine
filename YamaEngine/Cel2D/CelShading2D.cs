// ============================================================================================
//  CelShading2D.cs
//
//  yama引擎 (YamaEngine) 赛璐璐 / 二次元 2D 渲染子系统 —— 数据模型层
//
//  专为"赛璐璐动画风格"的 2D 游戏打造，提供：
//    - 赛璐璐材质模型 (CelToonMaterial): 量化色阶(N段)、描边、边缘光、半调网点、发光
//    - 精灵数据 (CelSpriteData): 源矩形、枢轴、翻转、色调、z 序、材质
//    - 帧动画剪辑 (CelAnimationClip) / 播放器 (CelAnimationPlayer): 逐帧二次元动画
//    - 动画状态 (CelAnimationState): 播放/暂停/停止/循环
//
//  本文件不依赖 RHI，是纯数据/逻辑层；GPU 绘制见 CelSpriteRenderer.cs。
//  命名空间：YamaEngine.Cel2D  ·  目标框架：net10.0
// ============================================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.Cel2D
{
    /// <summary>赛璐璐着色参数（量化色阶、描边、边缘光、网点）。</summary>
    public sealed class CelToonMaterial
    {
        /// <summary>基础色调乘子。</summary>
        public Vector4 BaseColor { get; set; } = Vector4.One;

        /// <summary>赛璐璐量化色阶数（2~8）。2 = 高对比硬阴影，8 = 柔和过渡。</summary>
        public int BandCount { get; set; } = 3;

        /// <summary>阴影色调（叠加在暗部，如冷色阴影）。</summary>
        public Vector4 ShadowTint { get; set; } = new(0.82f, 0.84f, 1.0f, 1.0f);

        /// <summary>描边颜色（赛璐璐线稿色）。</summary>
        public Vector4 OutlineColor { get; set; } = new(0.10f, 0.08f, 0.13f, 1.0f);

        /// <summary>描边宽度（纹素单位，0 = 关闭）。</summary>
        public float OutlineWidth { get; set; } = 2.0f;

        /// <summary>边缘光颜色。</summary>
        public Vector4 RimColor { get; set; } = new(1.0f, 0.96f, 0.85f, 1.0f);

        /// <summary>边缘光强度（0 = 关闭）。</summary>
        public float RimIntensity { get; set; } = 0.0f;

        /// <summary>半调网点密度（0 = 关闭，>0 在阴影区叠加网点）。</summary>
        public float HalftoneDensity { get; set; } = 0.0f;

        /// <summary>自发光（提亮暗部，如眼部高光）。</summary>
        public Vector4 Emissive { get; set; } = Vector4.Zero;

        /// <summary>是否启用赛璐璐色阶量化。</summary>
        public bool EnableBanding { get; set; } = true;

        /// <summary>是否启用描边。</summary>
        public bool EnableOutline { get; set; } = true;

        /// <summary>拷贝构造。</summary>
        public CelToonMaterial Clone()
        {
            return new CelToonMaterial
            {
                BaseColor = BaseColor,
                BandCount = BandCount,
                ShadowTint = ShadowTint,
                OutlineColor = OutlineColor,
                OutlineWidth = OutlineWidth,
                RimColor = RimColor,
                RimIntensity = RimIntensity,
                HalftoneDensity = HalftoneDensity,
                Emissive = Emissive,
                EnableBanding = EnableBanding,
                EnableOutline = EnableOutline
            };
        }
    }

    /// <summary>2D 精灵数据（纯数据，不含 GPU 纹理句柄；纹理由渲染层按资源名解析）。</summary>
    public sealed class CelSpriteData
    {
        /// <summary>精灵资源路径（纹理）。</summary>
        public string TexturePath { get; set; } = string.Empty;

        /// <summary>世界位置（像素坐标，精灵中心）。</summary>
        public Vector2 Position { get; set; }

        /// <summary>源矩形 UV（x=u0,y=v0,z=u1,w=v1；默认整图）。</summary>
        public Vector4 SourceRect { get; set; } = new(0, 0, 1, 1);

        /// <summary>枢轴（0~1，默认中心）。</summary>
        public Vector2 Pivot { get; set; } = new(0.5f, 0.5f);

        /// <summary>世界尺寸。</summary>
        public Vector2 Size { get; set; } = Vector2.One;

        /// <summary>水平/垂直翻转。</summary>
        public bool FlipX { get; set; }
        public bool FlipY { get; set; }

        /// <summary>色调乘子。</summary>
        public Vector4 Tint { get; set; } = Vector4.One;

        /// <summary>z 序（越大越靠前）。</summary>
        public float Z { get; set; }

        /// <summary>赛璐璐材质。</summary>
        public CelToonMaterial Material { get; set; } = new();

        /// <summary>动画播放器（可空）。</summary>
        public CelAnimationPlayer? Player { get; set; }
    }

    /// <summary>单帧动画数据。</summary>
    public sealed class CelAnimationFrame
    {
        /// <summary>本帧源矩形 UV。</summary>
        public Vector4 SourceRect { get; set; } = new(0, 0, 1, 1);

        /// <summary>本帧时长（秒）。</summary>
        public float Duration { get; set; } = 0.1f;

        public CelAnimationFrame() { }
        public CelAnimationFrame(Vector4 sourceRect, float duration)
        {
            SourceRect = sourceRect;
            Duration = duration;
        }
    }

    /// <summary>逐帧二次元动画剪辑。</summary>
    public sealed class CelAnimationClip
    {
        private readonly List<CelAnimationFrame> _frames = new();

        public string Name { get; set; } = "Clip";
        public bool Loop { get; set; } = true;
        public float PlaybackSpeed { get; set; } = 1.0f;
        public IReadOnlyList<CelAnimationFrame> Frames => _frames;

        /// <summary>总时长。</summary>
        public float Duration
        {
            get
            {
                float d = 0;
                foreach (var f in _frames) d += f.Duration;
                return d;
            }
        }

        public CelAnimationClip() { }
        public CelAnimationClip(string name) { Name = name; }

        public CelAnimationClip AddFrame(Vector4 sourceRect, float duration)
        {
            _frames.Add(new CelAnimationFrame(sourceRect, duration));
            return this;
        }

        public CelAnimationClip AddFrames(IEnumerable<CelAnimationFrame> frames)
        {
            _frames.AddRange(frames);
            return this;
        }
    }

    /// <summary>动画播放状态。</summary>
    public enum CelPlaybackState { Stopped, Playing, Paused }

    /// <summary>逐帧动画播放器。</summary>
    public sealed class CelAnimationPlayer
    {
        private CelAnimationClip? _clip;
        private float _time;
        private int _frameIndex;
        private CelPlaybackState _state = CelPlaybackState.Stopped;

        public CelAnimationClip? Clip => _clip;
        public CelPlaybackState State => _state;
        public int FrameIndex => _frameIndex;
        public float NormalizedTime => _clip == null || _clip.Duration <= 0 ? 0 : _time / _clip.Duration;

        public void Play(CelAnimationClip clip)
        {
            _clip = clip ?? throw new ArgumentNullException(nameof(clip));
            _time = 0;
            _frameIndex = 0;
            _state = CelPlaybackState.Playing;
        }

        public void Stop()
        {
            _state = CelPlaybackState.Stopped;
            _time = 0;
            _frameIndex = 0;
        }

        public void Pause() => _state = CelPlaybackState.Paused;
        public void Resume() { if (_clip != null) _state = CelPlaybackState.Playing; }

        /// <summary>推进动画，返回当前帧源矩形 UV。</summary>
        public Vector4 Update(float deltaTime)
        {
            if (_clip == null || _state != CelPlaybackState.Playing || _clip.Frames.Count == 0)
                return _clip != null && _clip.Frames.Count > 0 ? _clip.Frames[_frameIndex].SourceRect : new Vector4(0, 0, 1, 1);

            float dur = _clip.Duration;
            _time += deltaTime * _clip.PlaybackSpeed;

            if (dur <= 0) return _clip.Frames[_frameIndex].SourceRect;

            if (_time >= dur)
            {
                if (_clip.Loop) _time %= dur;
                else { _time = dur; _state = CelPlaybackState.Stopped; }
            }

            // 计算当前帧索引
            float acc = 0;
            int idx = 0;
            for (int i = 0; i < _clip.Frames.Count; i++)
            {
                acc += _clip.Frames[i].Duration;
                if (_time < acc) { idx = i; break; }
                idx = i;
            }
            _frameIndex = idx;
            return _clip.Frames[idx].SourceRect;
        }
    }
}
