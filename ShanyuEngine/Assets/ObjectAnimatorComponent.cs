using System;
using System.Numerics;

namespace ShanyuEngine.Assets
{
    // ════════════════════════════════════════════════════════════════════════════
    //  ObjectAnimatorComponent — 附加到 SceneObject 的对象级动画组件
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 附加到 <see cref="SceneObject"/> 的简化动画组件。管理一个 <see cref="ObjectAnimationClip"/> 的播放状态，
    /// 并在 Tick 中直接更新所属 SceneObject 的 Transform。
    /// </summary>
    public sealed class ObjectAnimatorComponent : IAssetComponent
    {
        public string Name => "Animator";

        /// <summary>当前播放的动画剪辑。</summary>
        public ObjectAnimationClip? Clip { get; set; }

        /// <summary>动画资产路径（可选，用于从文件加载）。</summary>
        public string ClipAssetPath { get; set; } = string.Empty;

        /// <summary>是否启用自动播放。</summary>
        public bool PlayOnStart { get; set; } = true;

        /// <summary>动画播放速度倍率。</summary>
        public float Speed { get; set; } = 1f;

        /// <summary>是否正在播放。</summary>
        public bool IsPlaying { get; private set; }

        /// <summary>当前播放时间（秒）。</summary>
        public float CurrentTime { get; private set; }

        /// <summary>是否已启动过。</summary>
        private bool _hasStarted;

        /// <summary>开始播放。</summary>
        public void Play()
        {
            IsPlaying = true;
            if (Clip != null && !Clip.Looping && CurrentTime >= Clip.LengthSeconds)
                CurrentTime = 0f;
        }

        /// <summary>暂停播放。</summary>
        public void Pause()
        {
            IsPlaying = false;
        }

        /// <summary>停止播放并回到起始位置。</summary>
        public void Stop()
        {
            IsPlaying = false;
            CurrentTime = 0f;
        }

        /// <summary>
        /// 每帧调用。如果启用 PlayOnStart，首次调用时自动开始播放。
        /// 根据 deltaTime 推进动画时间，并采样应用到 SceneObject。
        /// </summary>
        public void Update(float deltaTime, SceneObject owner)
        {
            if (Clip == null) return;

            if (!_hasStarted)
            {
                _hasStarted = true;
                if (PlayOnStart) Play();
            }

            if (!IsPlaying) return;

            // 推进时间
            CurrentTime += deltaTime * Speed;

            // 非循环动画到达末尾自动停止
            if (!Clip.Looping && CurrentTime >= Clip.LengthSeconds)
            {
                CurrentTime = Clip.LengthSeconds;
                IsPlaying = false;
            }

            // 采样并应用变换
            var (pos, rot, scl) = Clip.Sample(CurrentTime);
            owner.Position = pos;
            owner.Rotation = rot;
            owner.Scale = scl;
        }
    }
}
