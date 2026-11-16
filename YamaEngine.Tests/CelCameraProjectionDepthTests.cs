// CelCameraProjectionDepthTests.cs — Cel2DCamera 像素空间正交投影/坐标转换深度验证
using System;
using System.Numerics;
using YamaEngine.Cel2D;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

public sealed class CelCameraProjectionDepthTests
{
    /// <summary>构造默认值：Zoom=1，ClearColor alpha=1。</summary>
    public static void TestCameraDefaults()
    {
        var cam = new Cel2DCamera(new Vector2(800, 600));
        AssertEqual(new Vector2(800, 600), cam.ViewportSize, "视口应=800x600。");
        AssertEqual(1f, cam.Zoom, "默认 Zoom 应=1。");
        AssertTrue(cam.ClearColor.W > 0f, "ClearColor alpha 应为正。");
    }

    /// <summary>Zoom<=0 钳制为 1（防除零）。</summary>
    public static void TestZoomClampedToOne()
    {
        var cam = new Cel2DCamera(new Vector2(800, 600), Vector2.Zero, 0f);
        AssertEqual(1f, cam.Zoom, "Zoom=0 应被钳制为 1。");
        var cam2 = new Cel2DCamera(new Vector2(800, 600), Vector2.Zero, -5f);
        AssertEqual(1f, cam2.Zoom, "Zoom=-5 应被钳制为 1。");
    }

    /// <summary>Position 别名读写 Center。</summary>
    public static void TestPositionAlias()
    {
        var cam = new Cel2DCamera(new Vector2(800, 600));
        cam.Position = new Vector2(10, 20);
        AssertEqual(new Vector2(10, 20), cam.Center, "Position 写应同步 Center。");
        cam.Center = new Vector2(30, 40);
        AssertEqual(new Vector2(30, 40), cam.Position, "Position 读应=Center。");
    }

    /// <summary>WorldToScreen：相机中心点 → 视口中心。</summary>
    public static void TestWorldToScreenCenter()
    {
        var cam = new Cel2DCamera(new Vector2(800, 600), new Vector2(100, 100));
        var s = cam.WorldToScreen(new Vector2(100, 100));
        AssertEqual(new Vector2(400, 300), s, "相机中心应映射到视口中心 (400,300)。");
    }

    /// <summary>WorldToScreen：+X 世界点 → 屏幕右侧（Y 向下体系）。</summary>
    public static void TestWorldToScreenRight()
    {
        var cam = new Cel2DCamera(new Vector2(800, 600), new Vector2(0, 0));
        var s = cam.WorldToScreen(new Vector2(100, 0));
        AssertEqual(500f, s.X, "世界 +X=100 应到屏幕 X=500。");
        AssertEqual(300f, s.Y, "世界 Y=0 应到屏幕 Y=300。");
    }

    /// <summary>ScreenToWorld：视口中心 → 相机中心世界点。</summary>
    public static void TestScreenToWorldCenter()
    {
        var cam = new Cel2DCamera(new Vector2(800, 600), new Vector2(50, 50));
        var w = cam.ScreenToWorld(new Vector2(400, 300));
        AssertEqual(new Vector2(50, 50), w, "视口中心应逆变换到相机中心世界点。");
    }

    /// <summary>WorldToScreen/ScreenToWorld 往返恒等（Zoom=1）。</summary>
    public static void TestRoundTripIdentity()
    {
        var cam = new Cel2DCamera(new Vector2(1280, 720), new Vector2(640, 360));
        var world = new Vector2(813, 245);
        var round = cam.ScreenToWorld(cam.WorldToScreen(world));
        AssertTrue(Vector2.Distance(world, round) < 0.01f,
            $"往返应恒等：({round.X:F3},{round.Y:F3}) vs ({world.X},{world.Y})。");
    }

    /// <summary>往返恒等（Zoom=2）。</summary>
    public static void TestRoundTripZoomed()
    {
        var cam = new Cel2DCamera(new Vector2(1280, 720), new Vector2(640, 360), 2f);
        var world = new Vector2(900, 100);
        var round = cam.ScreenToWorld(cam.WorldToScreen(world));
        AssertTrue(Vector2.Distance(world, round) < 0.01f,
            $"Zoom=2 往返应恒等：({round.X:F3},{round.Y:F3})。");
    }

    /// <summary>Zoom 放大：屏幕偏移 = 世界偏移 × Zoom。</summary>
    public static void TestZoomScalesScreenOffset()
    {
        var cam = new Cel2DCamera(new Vector2(800, 600), new Vector2(0, 0), 2f);
        var s = cam.WorldToScreen(new Vector2(100, 0));
        AssertEqual(600f, s.X, "Zoom=2 时世界 +X=100 应到屏幕 X=400+200=600。");
    }

    /// <summary>BuildProjection 返回有效矩阵（对角非全零，行列式非零）。</summary>
    public static void TestBuildProjectionValid()
    {
        var cam = new Cel2DCamera(new Vector2(800, 600), new Vector2(400, 300));
        var m = cam.BuildProjection();
        AssertTrue(m.M11 != 0f || m.M22 != 0f, "投影矩阵 M11/M22 应非零（正交缩放）。");
        float det = m.GetDeterminant();
        AssertTrue(MathF.Abs(det) > 0f, $"投影矩阵行列式应非零，实际 {det:F4}。");
    }

    /// <summary>BuildProjection：相机中心点投影到裁剪空间原点（NDC 中心）。</summary>
    public static void TestProjectionCenterToOrigin()
    {
        var cam = new Cel2DCamera(new Vector2(800, 600), new Vector2(400, 300));
        var m = cam.BuildProjection();
        var ndc = Vector4.Transform(new Vector4(400, 300, 0, 1), m);
        AssertTrue(MathF.Abs(ndc.X) < 1e-4f,
            $"相机中心应投影到 NDC X=0，实际 {ndc.X:F4}。");
        AssertTrue(MathF.Abs(ndc.Y) < 1e-4f,
            $"相机中心应投影到 NDC Y=0，实际 {ndc.Y:F4}。");
    }

    /// <summary>BuildProjection：世界 +X 边界投影到 NDC X=+1（Y 向下体系右为正）。</summary>
    public static void TestProjectionRightEdge()
    {
        var cam = new Cel2DCamera(new Vector2(800, 600), new Vector2(400, 300));
        var m = cam.BuildProjection();
        var ndc = Vector4.Transform(new Vector4(800, 300, 0, 1), m);
        AssertTrue(ndc.X > 0.99f && ndc.X < 1.01f,
            $"世界右边界应投影到 NDC X≈+1，实际 {ndc.X:F4}。");
    }

    /// <summary>BuildProjection：世界顶部（Y 小）投影到 NDC Y=+1（Y 向下翻转）。</summary>
    public static void TestProjectionTopFlipped()
    {
        var cam = new Cel2DCamera(new Vector2(800, 600), new Vector2(400, 300));
        var m = cam.BuildProjection();
        var ndc = Vector4.Transform(new Vector4(400, 0, 0, 1), m);
        AssertTrue(ndc.Y > 0.99f,
            $"Y 向下体系世界顶部应投影到 NDC Y≈+1，实际 {ndc.Y:F4}。");
    }

    /// <summary>Zoom 反映到投影：Zoom=2 时世界范围减半（视野更小）。</summary>
    public static void TestProjectionZoomNarrows()
    {
        var cam = new Cel2DCamera(new Vector2(800, 600), new Vector2(400, 300), 2f);
        var m = cam.BuildProjection();
        var right = Vector4.Transform(new Vector4(800, 300, 0, 1), m);
        AssertTrue(right.X > 0.99f,
            $"Zoom=2 时世界右边界仍应到 NDC X=+1（视野缩小一倍），实际 {right.X:F4}。");
    }
}
