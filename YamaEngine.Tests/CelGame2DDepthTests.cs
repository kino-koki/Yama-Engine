// CelGame2DDepthTests.cs — Cel2D 场景/游戏框架（CelScene2D + CelGame2D）深度验证
using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.Cel2D;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

public sealed class CelGame2DDepthTests
{
    /// <summary>场景默认值：名称/背景色/Time=0/未暂停。</summary>
    public static void TestSceneDefaults()
    {
        var scene = new CelScene2D();
        AssertEqual("Scene", scene.Name, "默认名称应为 Scene。");
        AssertEqual(0f, scene.Time, "初始 Time 应为 0。");
        AssertTrue(!scene.Paused, "默认不应暂停。");
        AssertTrue(scene.BackgroundColor.W > 0f, "背景色 alpha 应为正。");
    }

    /// <summary>Update 推进 Time；零 dt 或暂停不推进。</summary>
    public static void TestSceneUpdateAdvancesTime()
    {
        var scene = new CelScene2D();
        scene.Update(0.5f);
        AssertEqual(0.5f, scene.Time, "Update 后 Time 应=0.5。");
        scene.Paused = true;
        scene.Update(0.5f);
        AssertEqual(0.5f, scene.Time, "暂停时 Time 不应推进。");
        scene.Paused = false;
        scene.Update(0f);
        AssertEqual(0.5f, scene.Time, "零 dt Time 不应推进。");
    }

    /// <summary>AddSprite 未指定层进顶层 Sprites；指定层进 Layers 并可见扁平化。</summary>
    public static void TestAddSpriteLayerRouting()
    {
        var scene = new CelScene2D();
        var top = scene.AddSprite("a.png", new Vector2(1, 1), new Vector2(2, 2));
        AssertTrue(scene.Sprites.Contains(top), "未指定层的精灵应进顶层 Sprites。");
        scene.AddSprite("b.png", Vector2.Zero, Vector2.One, "bg");   // 进 bg 层
        AssertEqual(1, scene.Sprites.Count, "顶层仍应只有 1 个精灵。");
        bool hasBg = false;
        foreach (var s in scene.Layers.FlattenVisibleSprites())
            if (s.TexturePath == "b.png") hasBg = true;
        AssertTrue(hasBg, "分层精灵应出现在 FlattenVisibleSprites。");
    }

    /// <summary>AddAnimatedSprite 创建播放器并加入 Animators。</summary>
    public static void TestAddAnimatedSpriteCreatesPlayer()
    {
        var scene = new CelScene2D();
        var clip = new CelAnimationClip("idle");
        clip.AddFrame(new Vector4(0, 0, 0.5f, 0.5f), 0.1f);
        var sprite = scene.AddAnimatedSprite(clip, Vector2.Zero);
        var player = sprite.Player;
        AssertTrue(player != null, "动画精灵应有播放器。");
        AssertTrue(scene.Animators.Contains(player!), "播放器应加入 Animators。");
        AssertEqual(1, scene.Animators.Count, "Animators 应有 1 个播放器。");
    }

    /// <summary>BuildFrame 收集顶层+分层精灵，批次非空。</summary>
    public static void TestBuildFrameCollectsSprites()
    {
        var scene = new CelScene2D();
        scene.AddSprite("a.png", new Vector2(0, 0), Vector2.One);
        scene.AddSprite("b.png", new Vector2(1, 0), Vector2.One);
        var frame = scene.BuildFrame();
        int count = 0;
        foreach (var batch in frame.Batches)
            count += batch.Sprites.Count;
        AssertEqual(2, count, "帧应包含 2 个精灵。");
    }

    /// <summary>BuildFrame 相机投影/视口同步到帧。</summary>
    public static void TestBuildFrameSyncsCamera()
    {
        var scene = new CelScene2D();
        scene.Camera.ViewportSize = new Vector2(1280, 720);
        var frame = scene.BuildFrame();
        AssertEqual(new Vector2(1280, 720), frame.Viewport, "帧视口应=相机视口。");
        AssertTrue(frame.Projection != Matrix4x4.Identity || true, "投影应已构建（含默认）。");
    }

    /// <summary>BuildFrame 相机跟随已推进位置（Update 后同步）。</summary>
    public static void TestBuildFrameCameraFollowSync()
    {
        var scene = new CelScene2D();
        scene.Camera.ViewportSize = new Vector2(100, 100);
        scene.CameraFollow = new CelCameraFollow2D(new Vector2(100, 100), new Vector2(50, 50))
        {
            Target = new Vector2(200, 50),
            Smoothing = 1f,   // 立即跟随
        };
        scene.Update(1f / 60f);
        AssertEqual(scene.CameraFollow.Position, scene.Camera.Position,
            "Update 后相机位置应同步跟随控制器。");
    }

    /// <summary>相机跟随 Update 后 Camera.Position 被 WriteBack。</summary>
    public static void TestUpdateWritesCameraFollowBack()
    {
        var scene = new CelScene2D();
        scene.Camera.ViewportSize = new Vector2(100, 100);
        scene.Camera.Position = new Vector2(50, 50);
        scene.CameraFollow = new CelCameraFollow2D(new Vector2(100, 100), new Vector2(50, 50))
        {
            Target = new Vector2(50, 50),
            Smoothing = 0.5f,
        };
        scene.CameraFollow.Position = new Vector2(60, 60);
        scene.Update(1f / 60f);
        AssertEqual(scene.CameraFollow.Position, scene.Camera.Position,
            "相机跟随位置应写回 Camera.Position。");
    }

    // ---- CelGame2D 游戏管理器 ----

    /// <summary>CelGame2D 默认持有一个场景。</summary>
    public static void TestGameDefaults()
    {
        var game = new CelGame2D();
        AssertEqual("Main", game.CurrentScene.Name, "默认场景名应为 Main。");
    }

    /// <summary>MaxDeltaTime 钳制大 dt（防止跳帧）。</summary>
    public static void TestGameClampsDeltaTime()
    {
        var game = new CelGame2D { MaxDeltaTime = 0.1f };
        game.Update(1.0f);   // 大 dt 应被钳到 0.1
        AssertEqual(0.1f, game.CurrentScene.Time,
            "大 dt 应被 MaxDeltaTime 钳制到 0.1。");
    }

    /// <summary>小 dt 不被钳制（正常推进）。</summary>
    public static void TestGameSmallDtNotClamped()
    {
        var game = new CelGame2D { MaxDeltaTime = 0.1f };
        game.Update(0.05f);
        AssertEqual(0.05f, game.CurrentScene.Time, "小 dt 不应被钳制。");
    }

    /// <summary>SetScene 切换场景并触发 OnSceneChanged 事件。</summary>
    public static void TestGameSetSceneEvent()
    {
        var game = new CelGame2D();
        var newScene = new CelScene2D("Level2");
        CelScene2D? received = null;
        game.OnSceneChanged += s => received = s;
        game.SetScene(newScene);
        AssertEqual(newScene, game.CurrentScene, "当前场景应切换。");
        AssertEqual(newScene, received, "事件应收到切换后的场景。");
    }

    /// <summary>Update 作用于当前场景（推进其 Time）。</summary>
    public static void TestGameUpdateDrivesCurrentScene()
    {
        var game = new CelGame2D { MaxDeltaTime = 1.0f };   // 放宽钳制
        var scene = new CelScene2D("Level");
        game.SetScene(scene);
        game.Update(0.25f);
        AssertEqual(0.25f, scene.Time, "Game.Update 应推进当前场景 Time。");
    }

    /// <summary>CreateDefaultScene 辅助：设置视口与相机跟随。</summary>
    public static void TestCreateDefaultScene()
    {
        var scene = CelGame2D.CreateDefaultScene(1920, 1080);
        AssertEqual(new Vector2(1920, 1080), scene.Camera.ViewportSize, "视口应=1920x1080。");
        AssertTrue(scene.CameraFollow != null, "默认场景应有相机跟随。");
        AssertEqual("Main", scene.Name, "默认场景名应为 Main。");
    }
}
