using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.Cel2D
{
    /// <summary>
    /// 2D 游戏场景：持有相机、灯光、层、物理、精灵与动画，并负责更新。
    /// 纯逻辑层：Update 推进动画 / 粒子 / 物理 / 相机跟随；
    /// Render 由 <see cref="CelShading2DSystem"/> 消费 <see cref="CelRenderFrame2D"/>。
    /// </summary>
    public sealed class CelScene2D
    {
        public string Name { get; set; } = "Scene";

        /// <summary>场景背景色。</summary>
        public Vector4 BackgroundColor { get; set; } = new Vector4(0.96f, 0.96f, 1.0f, 1.0f);

        /// <summary>分层渲染（背景→前景）。</summary>
        public CelRenderLayers2D Layers { get; } = new();

        /// <summary>相机（世界→屏幕投影）。字段而非属性：结构体成员需可写回。</summary>
        public Cel2DCamera Camera = new();

        /// <summary>灯光组。</summary>
        public CelLightRig2D LightRig { get; } = new();

        /// <summary>物理世界（可选，默认有）。</summary>
        public CelPhysicsWorld2D Physics { get; set; } = new();

        /// <summary>相机跟随控制器（可选）。</summary>
        public CelCameraFollow2D? CameraFollow { get; set; }

        /// <summary>精灵排序器。</summary>
        public CelSpriteSorter2D Sorter { get; set; } = new();

        /// <summary>顶层精灵（未分层时使用）。</summary>
        public List<CelSpriteData> Sprites { get; } = new();

        /// <summary>动画播放器。</summary>
        public List<CelAnimationPlayer> Animators { get; } = new();

        /// <summary>粒子系统（默认有）。</summary>
        public CelParticleSystem2D Particles { get; set; } = new();

        /// <summary>粒子纹理路径（空则不绘制粒子）。</summary>
        public string ParticleTexturePath { get; set; } = string.Empty;

        /// <summary>粒子材质（默认关闭描边的赛璐璐量化材质）。</summary>
        public CelToonMaterial? ParticleMaterial { get; set; }

        /// <summary>当前时间（秒）。</summary>
        public float Time { get; private set; }

        /// <summary>是否暂停更新。</summary>
        public bool Paused { get; set; }

        public CelScene2D() { }

        public CelScene2D(string name) { Name = name; }

        /// <summary>
        /// 添加精灵到场景（自动放入默认层或顶层列表）。
        /// </summary>
        public CelSpriteData AddSprite(string texture, Vector2 position, Vector2 size, string? layer = null, float z = 0.0f)
        {
            var sprite = new CelSpriteData
            {
                TexturePath = texture,
                Position = position,
                Size = size,
                Z = z,
            };
            if (layer != null)
                Layers.Assign(sprite, layer);
            else
                Sprites.Add(sprite);
            return sprite;
        }

        /// <summary>
        /// 添加动画精灵（带播放器）。纹理路径与尺寸由调用方指定，
        /// 播放器每帧写入精灵的 SourceRect 以推进帧。
        /// </summary>
        public CelSpriteData AddAnimatedSprite(CelAnimationClip clip, Vector2 position, string texturePath = "", Vector2? size = null, string? layer = null, float z = 0.0f)
        {
            var sprite = new CelSpriteData
            {
                TexturePath = texturePath,
                Position = position,
                Size = size ?? Vector2.One,
                Z = z,
            };
            var player = new CelAnimationPlayer();
            player.Play(clip);
            sprite.Player = player;      // 播放器驱动精灵 SourceRect（见 CelShading2DSystem.Update）
            Animators.Add(player);
            if (layer != null)
                Layers.Assign(sprite, layer);
            else
                Sprites.Add(sprite);
            return sprite;
        }

        /// <summary>更新场景逻辑。</summary>
        public void Update(float deltaTime)
        {
            if (Paused || deltaTime <= 0.0f)
                return;

            Time += deltaTime;

            // 动画。
            foreach (var anim in Animators)
                anim.Update(deltaTime);

            // 粒子。
            Particles.Update(deltaTime);

            // 物理。
            Physics.Update(deltaTime);

            // 相机跟随。
            CameraFollow?.Update(deltaTime);
            if (CameraFollow != null)
                Camera.Position = CameraFollow.Position;
        }

        /// <summary>
        /// 生成一帧渲染快照（供渲染器消费）。按层+排序批处理。
        /// </summary>
        public CelRenderFrame2D BuildFrame()
        {
            var frame = new CelRenderFrame2D
            {
                Camera = Camera,
                BackgroundColor = BackgroundColor,
                LightRig = LightRig,
                Time = Time,
            };

            // 收集精灵：分层优先，否则顶层。
            var allSprites = new List<CelSpriteData>();
            allSprites.AddRange(Layers.FlattenVisibleSprites());
            allSprites.AddRange(Sprites);

            foreach (var batch in Sorter.BuildBatches(allSprites, s => string.IsNullOrEmpty(s.TexturePath) ? null : s.TexturePath))
                frame.Batches.Add(batch);

            // 粒子转精灵（使用场景粒子材质）。
            foreach (var p in Particles.ToSpriteData(ParticleTexturePath, ParticleMaterial))
                frame.ParticleSprites.Add(p);

            // 相机矩阵：供渲染器构建投影。
            frame.Projection = Camera.BuildProjection();
            frame.Viewport = Camera.ViewportSize;
            frame.Zoom = Camera.Zoom;

            return frame;
        }
    }

    /// <summary>
    /// 一帧渲染快照（相机 + 批次 + 粒子 + 灯光），由渲染层消费。
    /// </summary>
    public sealed class CelRenderFrame2D
    {
        public Cel2DCamera Camera { get; set; } = new();
        public Vector4 BackgroundColor { get; set; } = new(0.96f, 0.96f, 1.0f, 1.0f);
        public CelLightRig2D? LightRig { get; set; }
        public float Time { get; set; }
        public float Zoom { get; set; } = 1.0f;
        public Vector2 Viewport { get; set; }
        public Matrix4x4 Projection { get; set; } = Matrix4x4.Identity;

        public List<CelSpriteBatch2D> Batches { get; } = new();
        public List<CelSpriteData> ParticleSprites { get; } = new();
    }

    /// <summary>
    /// 2D 游戏管理器：持有当前场景，驱动更新循环，提供场景切换与创建辅助。
    /// 纯逻辑层。
    /// </summary>
    public sealed class CelGame2D
    {
        public CelScene2D CurrentScene { get; private set; } = new("Main");
        public string GameName { get; set; } = "Cel Game";

        /// <summary>每帧最大 deltaTime（防止卡顿跳帧，秒）。</summary>
        public float MaxDeltaTime { get; set; } = 0.1f;

        public event Action<CelScene2D>? OnSceneChanged;

        public void SetScene(CelScene2D scene)
        {
            CurrentScene = scene;
            OnSceneChanged?.Invoke(scene);
        }

        public void Update(float deltaTime)
        {
            float dt = MathF.Min(deltaTime, MaxDeltaTime);
            CurrentScene.Update(dt);
        }

        public CelRenderFrame2D BuildFrame() => CurrentScene.BuildFrame();

        /// <summary>默认相机（视口尺寸）。</summary>
        public static CelScene2D CreateDefaultScene(int width = 1280, int height = 720)
        {
            var scene = new CelScene2D("Main")
            {
                Camera = new Cel2DCamera
                {
                    ViewportSize = new Vector2(width, height),
                    Position = new Vector2(width * 0.5f, height * 0.5f),
                },
            };
            scene.CameraFollow = new CelCameraFollow2D(new Vector2(width, height), scene.Camera.Position);
            return scene;
        }
    }

}
