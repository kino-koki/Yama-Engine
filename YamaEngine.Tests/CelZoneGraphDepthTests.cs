// CelZoneGraphDepthTests.cs — AI 区域图导航（ZoneGraph）深度验证
using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.AI;
using YamaEngine.ECS;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

public sealed class CelZoneGraphDepthTests
{
    // 辅助：创建一条直线车道（两点）
    private static ZoneLane MakeLane(int id, Vector3 a, Vector3 b,
        float width = 3f, ZoneLaneType type = ZoneLaneType.Walk, ZoneTag tags = ZoneTag.Sidewalk)
        => new(id, new List<Vector3> { a, b }, width, type, tags);

    // ════════════════════════════════════════════════════════════════════
    // ZoneLane 基础
    // ════════════════════════════════════════════════════════════════════

    /// <summary>车道默认属性：Direction=TwoWay, ConnectedLanes 为空, LaneId 正确。</summary>
    public static void TestLaneDefaults()
    {
        var lane = MakeLane(5, new Vector3(0, 0, 0), new Vector3(10, 0, 0));
        AssertEqual(5, lane.LaneId, "LaneId 应为 5。");
        AssertEqual(ZoneLaneDirection.TwoWay, lane.Direction, "默认方向应为双向。");
        AssertEqual(0, lane.ConnectedLanes.Count, "新建车道无下游连接。");
        AssertEqual(ZoneLaneType.Walk, lane.LaneType, "车道类型应为 Walk。");
    }

    /// <summary>车道长度 = 两端点直线距离。</summary>
    public static void TestLaneLength()
    {
        var lane = MakeLane(1, new Vector3(0, 0, 0), new Vector3(3, 0, 4));
        // 距离 = sqrt(9+16) = 5
        AssertTrue(MathF.Abs(lane.Length - 5f) < 0.01f,
            $"车道长度应≈5，实际 {lane.Length}");
    }

    /// <summary>RightVec 垂直于 +X 方向车道（叉乘 +X×+Y=+Z）。</summary>
    public static void TestLaneRightVec()
    {
        var lane = MakeLane(1, new Vector3(0, 0, 0), new Vector3(10, 0, 0));
        // Cross((1,0,0), (0,1,0)) = (0,0,1) → 归一化仍 (0,0,1)
        AssertTrue(MathF.Abs(lane.RightVec.X) < 0.01f, "RightVec.X 应≈0。");
        AssertTrue(MathF.Abs(lane.RightVec.Y) < 0.01f, "RightVec.Y 应≈0。");
        AssertTrue(MathF.Abs(lane.RightVec.Z - 1f) < 0.01f, $"RightVec.Z 应≈1，实际 {lane.RightVec.Z}");
    }

    /// <summary>GetPositionAtT(0)=Start，GetPositionAtT(1)=End，中点 t=0.5。</summary>
    public static void TestLaneGetPositionAtT()
    {
        var lane = MakeLane(1, new Vector3(0, 0, 0), new Vector3(10, 0, 0));
        var at0 = lane.GetPositionAtT(0f);
        AssertTrue(MathF.Abs(at0.X) < 0.01f, "t=0 应在起点。");
        var at1 = lane.GetPositionAtT(1f);
        AssertTrue(MathF.Abs(at1.X - 10f) < 0.01f, "t=1 应在终点。");
        var atMid = lane.GetPositionAtT(0.5f);
        AssertTrue(MathF.Abs(atMid.X - 5f) < 0.01f, $"t=0.5 应在 (5,0,0)，实际 X={atMid.X}");
    }

    /// <summary>GetPositionAtDistance 按弧长插值（折线两段）。</summary>
    public static void TestLaneGetPositionAtDistancePolyline()
    {
        // 折线：(0,0,0) → (5,0,0) → (5,0,5)，总长=10
        var lane = new ZoneLane(1, new List<Vector3>
        {
            new(0, 0, 0), new(5, 0, 0), new(5, 0, 5)
        }, 3f, ZoneLaneType.Walk, ZoneTag.Sidewalk);
        AssertTrue(MathF.Abs(lane.Length - 10f) < 0.01f, $"长度应=10，实际 {lane.Length}");
        // 弧长=7.5 → 第二段走 2.5（从 (5,0,0) 沿 +Z），结果 (5,0,2.5)
        var p = lane.GetPositionAtDistance(7.5f);
        AssertTrue(MathF.Abs(p.X - 5f) < 0.01f, $"X 应=5，实际 {p.X}");
        AssertTrue(MathF.Abs(p.Z - 2.5f) < 0.01f, $"Z 应=2.5，实际 {p.Z}");
    }

    /// <summary>GetClosestPoint 把外部点投影到车道中心线。</summary>
    public static void TestLaneGetClosestPoint()
    {
        var lane = MakeLane(1, new Vector3(0, 0, 0), new Vector3(10, 0, 0));
        var pos = new Vector3(5, 0, 3); // 在车道侧面
        var closest = lane.GetClosestPoint(pos);
        AssertTrue(MathF.Abs(closest.X - 5f) < 0.01f, $"最近点 X 应=5，实际 {closest.X}");
        AssertTrue(MathF.Abs(closest.Z) < 0.01f, "最近点 Z 应≈0（在中心线上）。");
    }

    /// <summary>GetTAtClosestPoint：起点侧→0，终点侧→1，中点→0.5。</summary>
    public static void TestLaneGetTAtClosestPoint()
    {
        var lane = MakeLane(1, new Vector3(0, 0, 0), new Vector3(10, 0, 0));
        AssertTrue(lane.GetTAtClosestPoint(new Vector3(1, 0, 5)) < 0.2f, "靠起点侧 t 应接近 0。");
        AssertTrue(lane.GetTAtClosestPoint(new Vector3(9, 0, 5)) > 0.8f, "靠终点侧 t 应接近 1。");
        float tMid = lane.GetTAtClosestPoint(new Vector3(5, 0, 5));
        AssertTrue(MathF.Abs(tMid - 0.5f) < 0.05f, $"中点 t 应≈0.5，实际 {tMid}");
    }

    /// <summary>ContainsPointXZ：点在半宽内=true，超出=false。</summary>
    public static void TestLaneContainsPointXZ()
    {
        var lane = MakeLane(1, new Vector3(0, 0, 0), new Vector3(10, 0, 0), width: 2f);
        AssertTrue(lane.ContainsPointXZ(new Vector3(5, 0, 0.9f)), "半宽内应=true（半宽=1）。");
        AssertTrue(!lane.ContainsPointXZ(new Vector3(5, 0, 2f)), "超出半宽应=false。");
    }

    /// <summary>HasTag/HasAllTags/HasAnyTag 位掩码查询。</summary>
    public static void TestLaneTagQueries()
    {
        var lane = MakeLane(1, new Vector3(0, 0, 0), new Vector3(1, 0, 0),
            tags: ZoneTag.Sidewalk | ZoneTag.Outdoor);
        AssertTrue(lane.HasTag(ZoneTag.Sidewalk), "应拥有 Sidewalk。");
        AssertTrue(!lane.HasTag(ZoneTag.Road), "不应拥有 Road。");
        AssertTrue(lane.HasAllTags(ZoneTag.Sidewalk | ZoneTag.Outdoor), "应拥有全部指定标签。");
        AssertTrue(!lane.HasAllTags(ZoneTag.Sidewalk | ZoneTag.Road), "缺少 Road，HasAllTags 应=false。");
        AssertTrue(lane.HasAnyTag(ZoneTag.Road | ZoneTag.Sidewalk), "HasAnyTag 应=true。");
    }

    // ════════════════════════════════════════════════════════════════════
    // ZoneShape
    // ════════════════════════════════════════════════════════════════════

    /// <summary>ZoneShape 包围盒与中心计算。</summary>
    public static void TestShapeBoundsAndCenter()
    {
        var shape = new ZoneShape(1, new List<Vector3>
        {
            new(0, 0, 0), new(4, 0, 0), new(4, 0, 6), new(0, 0, 6)
        }, ZoneTag.Plaza);
        AssertTrue(MathF.Abs(shape.BoundsMin.X - 0f) < 0.01f, "BoundsMin.X 应=0。");
        AssertTrue(MathF.Abs(shape.BoundsMax.X - 4f) < 0.01f, "BoundsMax.X 应=4。");
        AssertTrue(MathF.Abs(shape.BoundsMax.Z - 6f) < 0.01f, "BoundsMax.Z 应=6。");
        var c = shape.CenterXZ;
        AssertTrue(MathF.Abs(c.X - 2f) < 0.01f && MathF.Abs(c.Y - 3f) < 0.01f,
            $"CenterXZ 应=(2,3)，实际 ({c.X},{c.Y})");
    }

    /// <summary>ZoneShape.ContainsPoint 射线法：内部=true，外部=false。</summary>
    public static void TestShapeContainsPoint()
    {
        var shape = new ZoneShape(1, new List<Vector3>
        {
            new(0, 0, 0), new(10, 0, 0), new(10, 0, 10), new(0, 0, 10)
        }, ZoneTag.Plaza);
        AssertTrue(shape.ContainsPoint(new Vector3(5, 0, 5)), "正方形内部应=true。");
        AssertTrue(!shape.ContainsPoint(new Vector3(15, 0, 5)), "正方形外部应=false。");
        AssertTrue(!shape.ContainsPoint(new Vector3(-1, 0, 5)), "包围盒外应=false（快速剔除）。");
    }

    // ════════════════════════════════════════════════════════════════════
    // ZoneSpatialHash
    // ════════════════════════════════════════════════════════════════════

    /// <summary>空间哈希 CellSize 钳制与 WorldToCell。</summary>
    public static void TestSpatialHashCellSize()
    {
        var hash = new ZoneSpatialHash(cellSize: 0.5f); // 强制钳到最小 1
        AssertEqual(1f, hash.CellSize, "CellSize 应钳到最小 1。");
        var hash2 = new ZoneSpatialHash(cellSize: 10f);
        var cell = hash2.WorldToCell(15f, 25f);
        AssertEqual(1, cell.cx, "cellX 应=1（15/10）。");
        AssertEqual(2, cell.cz, "cellZ 应=2（25/10）。");
    }

    /// <summary>AddLane + GetLanesNear 能找到附近车道。</summary>
    public static void TestSpatialHashAddLaneNear()
    {
        var hash = new ZoneSpatialHash(cellSize: 16f);
        var lane = MakeLane(1, new Vector3(0, 0, 0), new Vector3(10, 0, 0));
        hash.AddLane(lane);
        var near = hash.GetLanesNear(new Vector3(5, 0, 0), 5f);
        AssertTrue(near.Contains(1), "附近查询应包含车道 1。");
        var far = hash.GetLanesNear(new Vector3(100, 0, 100), 5f);
        AssertTrue(!far.Contains(1), "远处查询不应包含车道 1。");
    }

    // ════════════════════════════════════════════════════════════════════
    // ZoneGraphData
    // ════════════════════════════════════════════════════════════════════

    /// <summary>ZoneGraphData 增删查 + LaneCount/ShapeCount。</summary>
    public static void TestGraphDataCRUD()
    {
        var graph = new ZoneGraphData();
        AssertEqual(0, graph.LaneCount, "初始车道数=0。");
        var lane = MakeLane(1, new Vector3(0, 0, 0), new Vector3(10, 0, 0));
        graph.AddLane(lane);
        AssertEqual(1, graph.LaneCount, "添加后车道数=1。");
        AssertTrue(graph.GetLane(1) != null, "GetLane(1) 应返回车道。");
        AssertTrue(graph.GetLane(99) == null, "GetLane(99) 应返回 null。");

        var shape = new ZoneShape(1, new List<Vector3>
        {
            new(0, 0, 0), new(5, 0, 0), new(5, 0, 5), new(0, 0, 5)
        }, ZoneTag.Plaza);
        graph.AddShape(shape);
        AssertEqual(1, graph.ShapeCount, "添加后形状数=1。");

        graph.RemoveLane(1);
        AssertEqual(0, graph.LaneCount, "移除后车道数=0。");
        graph.RemoveShape(1);
        AssertEqual(0, graph.ShapeCount, "移除后形状数=0。");
    }

    /// <summary>FindNearestLane 返回最近的可通行车道。</summary>
    public static void TestFindNearestLane()
    {
        var graph = new ZoneGraphData(hashCellSize: 8f);
        var laneA = MakeLane(1, new Vector3(0, 0, 0), new Vector3(10, 0, 0));
        var laneB = MakeLane(2, new Vector3(0, 0, 20), new Vector3(10, 0, 20));
        graph.AddLane(laneA);
        graph.AddLane(laneB);
        graph.BuildSpatialHash();

        var nearest = graph.FindNearestLane(new Vector3(5, 0, 2));
        AssertTrue(nearest != null, "应找到最近车道。");
        AssertEqual(1, nearest!.LaneId, "最近车道应为 laneA（Z=0 那条）。");
    }

    /// <summary>FindNearestLane 过滤 Restricted 车道。</summary>
    public static void TestFindNearestLaneSkipsRestricted()
    {
        var graph = new ZoneGraphData(hashCellSize: 8f);
        // 只有 restricted 车道在附近
        var restricted = MakeLane(1, new Vector3(0, 0, 0), new Vector3(10, 0, 0),
            type: ZoneLaneType.Restricted);
        graph.AddLane(restricted);
        graph.BuildSpatialHash();
        var result = graph.FindNearestLane(new Vector3(5, 0, 0));
        AssertTrue(result == null, "所有车道均 Restricted 时应返回 null。");
    }

    /// <summary>FindLanesInRadius 返回半径内车道并按距离排序。</summary>
    public static void TestFindLanesInRadius()
    {
        var graph = new ZoneGraphData(hashCellSize: 8f);
        var near = MakeLane(1, new Vector3(0, 0, 0), new Vector3(10, 0, 0));
        var far = MakeLane(2, new Vector3(0, 0, 30), new Vector3(10, 0, 30));
        graph.AddLane(near);
        graph.AddLane(far);
        graph.BuildSpatialHash();

        var inRadius = graph.FindLanesInRadius(new Vector3(5, 0, 0), 5f);
        AssertEqual(1, inRadius.Count, "半径 5 内应只有 1 条车道（laneA）。");
        AssertEqual(1, inRadius[0].LaneId, "该车道应为 laneA。");
    }

    /// <summary>FindNearestShape 返回包含点的形状。</summary>
    public static void TestFindNearestShapeInside()
    {
        var graph = new ZoneGraphData(hashCellSize: 8f);
        var plaza = new ZoneShape(1, new List<Vector3>
        {
            new(0, 0, 0), new(10, 0, 0), new(10, 0, 10), new(0, 0, 10)
        }, ZoneTag.Plaza);
        graph.AddShape(plaza);
        graph.BuildSpatialHash();
        var found = graph.FindNearestShape(new Vector3(5, 0, 5));
        AssertTrue(found != null, "应找到包含该点的形状。");
        AssertEqual(1, found!.ShapeId, "ShapeId 应=1。");
    }

    /// <summary>GetConnectedLanes 返回下游连接的车道对象。</summary>
    public static void TestGetConnectedLanes()
    {
        var graph = new ZoneGraphData();
        var a = MakeLane(1, new Vector3(0, 0, 0), new Vector3(10, 0, 0));
        var b = MakeLane(2, new Vector3(10, 0, 0), new Vector3(20, 0, 0));
        a.ConnectedLanes.Add(2);
        graph.AddLane(a);
        graph.AddLane(b);
        var conns = graph.GetConnectedLanes(1);
        AssertEqual(1, conns.Count, "laneA 有 1 条下游连接。");
        AssertEqual(2, conns[0].LaneId, "下游车道 ID=2。");
    }

    // ════════════════════════════════════════════════════════════════════
    // ZonePath + ZonePathfinder
    // ════════════════════════════════════════════════════════════════════

    /// <summary>ZonePath 基础属性：IsValid/SegmentCount/TotalLength。</summary>
    public static void TestZonePathBasics()
    {
        var graph = new ZoneGraphData();
        graph.AddLane(MakeLane(1, new Vector3(0, 0, 0), new Vector3(10, 0, 0)));
        graph.AddLane(MakeLane(2, new Vector3(10, 0, 0), new Vector3(20, 0, 0)));
        var path = new ZonePath(new List<int> { 1, 2 }, graph);
        AssertTrue(path.IsValid, "包含 2 条车道，IsValid=true。");
        AssertEqual(2, path.SegmentCount, "SegmentCount=2。");
        AssertTrue(MathF.Abs(path.TotalLength - 20f) < 0.01f,
            $"TotalLength 应=20，实际 {path.TotalLength}");
    }

    /// <summary>ZonePath.GetPositionAtDistance 沿多车道路径取位置。</summary>
    public static void TestZonePathGetPositionAtDistance()
    {
        var graph = new ZoneGraphData();
        graph.AddLane(MakeLane(1, new Vector3(0, 0, 0), new Vector3(10, 0, 0)));
        graph.AddLane(MakeLane(2, new Vector3(10, 0, 0), new Vector3(20, 0, 0)));
        var path = new ZonePath(new List<int> { 1, 2 }, graph);

        var p0 = path.GetPositionAtDistance(0f);
        AssertTrue(MathF.Abs(p0.X) < 0.01f, "距离 0 应在路径起点 (0,0,0)。");
        var p15 = path.GetPositionAtDistance(15f);
        AssertTrue(MathF.Abs(p15.X - 15f) < 0.01f, $"距离 15 应在 (15,0,0)，实际 X={p15.X}");
        var pEnd = path.GetPositionAtDistance(100f); // 超出→终点
        AssertTrue(MathF.Abs(pEnd.X - 20f) < 0.01f, "超出范围应返回终点 (20,0,0)。");
    }

    /// <summary>ZonePath.GetPositionAtT(0)=起点，GetPositionAtT(1)=终点。</summary>
    public static void TestZonePathGetPositionAtT()
    {
        var graph = new ZoneGraphData();
        graph.AddLane(MakeLane(1, new Vector3(0, 0, 0), new Vector3(10, 0, 0)));
        var path = new ZonePath(new List<int> { 1 }, graph);
        var p0 = path.GetPositionAtT(0f);
        AssertTrue(MathF.Abs(p0.X) < 0.01f, "t=0 应在起点。");
        var p1 = path.GetPositionAtT(1f);
        AssertTrue(MathF.Abs(p1.X - 10f) < 0.01f, "t=1 应在终点。");
    }

    /// <summary>ZonePathfinder.FindPath 直连车道返回单节点路径。</summary>
    public static void TestPathfinderSameLane()
    {
        var graph = new ZoneGraphData();
        graph.AddLane(MakeLane(1, new Vector3(0, 0, 0), new Vector3(10, 0, 0)));
        var finder = new ZonePathfinder(graph);
        var path = finder.FindPath(1, 1);
        AssertTrue(path != null, "同车道寻路应成功。");
        AssertEqual(1, path!.LaneIds.Count, "路径仅含起点车道。");
    }

    /// <summary>ZonePathfinder.FindPath 两车道串联寻路。</summary>
    public static void TestPathfinderTwoLaneChain()
    {
        var graph = new ZoneGraphData();
        var a = MakeLane(1, new Vector3(0, 0, 0), new Vector3(10, 0, 0));
        var b = MakeLane(2, new Vector3(10, 0, 0), new Vector3(20, 0, 0));
        a.ConnectedLanes.Add(2);
        graph.AddLane(a);
        graph.AddLane(b);
        var finder = new ZonePathfinder(graph);
        var path = finder.FindPath(1, 2);
        AssertTrue(path != null, "应找到路径。");
        AssertEqual(2, path!.LaneIds.Count, "路径应含 2 条车道。");
        AssertEqual(1, path.LaneIds[0], "路径起点=车道1。");
        AssertEqual(2, path.LaneIds[1], "路径终点=车道2。");
    }

    /// <summary>ZonePathfinder 避开 Restricted 车道（走更长的可行路径）。</summary>
    public static void TestPathfinderAvoidsRestricted()
    {
        var graph = new ZoneGraphData();
        // 直连路径：lane1→lane2(restricted)
        var a = MakeLane(1, new Vector3(0, 0, 0), new Vector3(10, 0, 0));
        var bad = MakeLane(2, new Vector3(10, 0, 0), new Vector3(20, 0, 0),
            type: ZoneLaneType.Restricted);
        a.ConnectedLanes.Add(2);
        graph.AddLane(a);
        graph.AddLane(bad);

        var finder = new ZonePathfinder(graph);
        var path = finder.FindPath(1, 2); // 目标是 restricted 车道
        AssertTrue(path == null, "目标为 Restricted 车道时寻路应失败（不可到达）。");
    }

    /// <summary>LaneCost：Restricted 返回 MaxValue，偏好标签降低代价。</summary>
    public static void TestPathfinderLaneCost()
    {
        var graph = new ZoneGraphData();
        var normal = MakeLane(1, new Vector3(0, 0, 0), new Vector3(10, 0, 0),
            type: ZoneLaneType.Drive, tags: ZoneTag.Road);
        var restricted = MakeLane(2, new Vector3(0, 0, 0), new Vector3(10, 0, 0),
            type: ZoneLaneType.Restricted, tags: ZoneTag.Road);
        graph.AddLane(normal);
        graph.AddLane(restricted);

        var finder = new ZonePathfinder(graph);
        // Restricted 车道代价无穷大
        AssertTrue(finder.LaneCost(restricted) == float.MaxValue,
            "Restricted 车道代价应为 MaxValue。");
        // 普通车道代价 = Length = 10
        float normalCost = finder.LaneCost(normal);
        AssertTrue(MathF.Abs(normalCost - 10f) < 0.01f,
            $"无偏好时代价=Length=10，实际 {normalCost}");

        // 设置偏好 Sidewalk（该车道是 Road，无偏好折扣）
        finder.PreferenceTags = ZoneTag.Sidewalk;
        float noDiscount = finder.LaneCost(normal);
        AssertTrue(MathF.Abs(noDiscount - 10f) < 0.01f,
            $"车道无偏好标签时代价不变，实际 {noDiscount}");

        // 车道拥有偏好标签 → 代价打折
        var sidewalk = MakeLane(3, new Vector3(0, 0, 0), new Vector3(10, 0, 0),
            type: ZoneLaneType.Walk, tags: ZoneTag.Sidewalk);
        graph.AddLane(sidewalk);
        float discounted = finder.LaneCost(sidewalk);
        AssertTrue(discounted < 10f && discounted > 4f,
            $"偏好折扣代价应在 (4,10)，实际 {discounted}");
    }

    /// <summary>Heuristic = 两车道终点 XZ 距离。</summary>
    public static void TestPathfinderHeuristic()
    {
        var graph = new ZoneGraphData();
        var a = MakeLane(1, new Vector3(0, 0, 0), new Vector3(3, 0, 4));
        var b = MakeLane(2, new Vector3(3, 0, 4), new Vector3(6, 0, 8));
        graph.AddLane(a);
        graph.AddLane(b);
        var finder = new ZonePathfinder(graph);
        float h = finder.Heuristic(a, b);
        // dist((3,4),(6,8)) = sqrt(9+16) = 5
        AssertTrue(MathF.Abs(h - 5f) < 0.01f, $"启发值应=5，实际 {h}");
    }

    // ════════════════════════════════════════════════════════════════════
    // ZoneGraphSystem (ECS 集成)
    // ════════════════════════════════════════════════════════════════════

    /// <summary>ZoneGraphSystem 构造后 Graph/Pathfinder 非空，Active 已设置。</summary>
    public static void TestSystemDefaults()
    {
        var sys = new ZoneGraphSystem(hashCellSize: 8f);
        AssertTrue(sys.Graph != null, "构造后 Graph 不应为 null。");
        AssertTrue(sys.Pathfinder != null, "构造后 Pathfinder 不应为 null。");
        AssertEqual("ZoneGraphSystem", sys.Name, "系统名称正确。");
        AssertTrue(ZoneGraphSystem.Active == sys, "构造后 Active 应指向当前实例。");
    }

    /// <summary>SetGraph 替换内部图并重建寻路器。</summary>
    public static void TestSystemSetGraph()
    {
        var sys = new ZoneGraphSystem();
        var newGraph = new ZoneGraphData();
        newGraph.AddLane(MakeLane(1, new Vector3(0, 0, 0), new Vector3(5, 0, 0)));
        sys.SetGraph(newGraph);
        AssertTrue(sys.Graph == newGraph, "SetGraph 后 Graph 应指向新图。");
        AssertTrue(sys.Pathfinder != null, "SetGraph 后 Pathfinder 应已重建。");
    }

    /// <summary>RegisterAgent 挂载 ZoneGraphComponent。</summary>
    public static void TestSystemRegisterAgent()
    {
        var sys = new ZoneGraphSystem();
        var ecs = new EntityComponentSystem();
        int eid = ecs.CreateEntity("agent", new Vector3(5, 0, 0));
        sys.RegisterAgent(ecs, eid);
        AssertTrue(ecs.HasComponent<ZoneGraphComponent>(eid), "注册后应挂载 ZoneGraphComponent。");
        var comp = ecs.GetComponent<ZoneGraphComponent>(eid);
        AssertEqual(0, comp.CurrentLaneId, "初始 CurrentLaneId=0（未绑定）。");
    }

    /// <summary>RegisterAgent 重复调用幂等（不重复添加组件）。</summary>
    public static void TestSystemRegisterAgentIdempotent()
    {
        var sys = new ZoneGraphSystem();
        var ecs = new EntityComponentSystem();
        int eid = ecs.CreateEntity("a");
        sys.RegisterAgent(ecs, eid);
        sys.RegisterAgent(ecs, eid); // 第二次不应报错
        AssertTrue(ecs.HasComponent<ZoneGraphComponent>(eid), "重复注册后仍有组件。");
    }

    /// <summary>Update 将代理绑定到最近车道（CurrentLaneId 被更新）。</summary>
    public static void TestSystemUpdateBindsNearestLane()
    {
        var graph = new ZoneGraphData(hashCellSize: 8f);
        graph.AddLane(MakeLane(1, new Vector3(0, 0, 0), new Vector3(20, 0, 0)));
        graph.BuildSpatialHash();

        var sys = new ZoneGraphSystem();
        sys.SetGraph(graph);
        sys.RebindDistance = 100f; // 保证快速路径不干扰（无初始车道）

        var ecs = new EntityComponentSystem();
        int eid = ecs.CreateEntity("agent", new Vector3(5, 0, 0));
        sys.RegisterAgent(ecs, eid);

        sys.Update(0.016f, ecs);
        var comp = ecs.GetComponent<ZoneGraphComponent>(eid);
        AssertEqual(1, comp.CurrentLaneId, "Update 后应绑定到最近车道（ID=1）。");
        AssertTrue(comp.LaneT > 0f && comp.LaneT < 1f, $"LaneT 应在 (0,1)，实际 {comp.LaneT}");
    }

    /// <summary>FindPathToPosition 通过 ECS 规划跨车道路径。</summary>
    public static void TestSystemFindPathToPosition()
    {
        var graph = new ZoneGraphData(hashCellSize: 8f);
        var a = MakeLane(1, new Vector3(0, 0, 0), new Vector3(10, 0, 0));
        var b = MakeLane(2, new Vector3(10, 0, 0), new Vector3(20, 0, 0));
        a.ConnectedLanes.Add(2);
        graph.AddLane(a);
        graph.AddLane(b);
        graph.BuildSpatialHash();

        var sys = new ZoneGraphSystem();
        sys.SetGraph(graph);
        var ecs = new EntityComponentSystem();
        int eid = ecs.CreateEntity("agent", new Vector3(5, 0, 0));
        sys.RegisterAgent(ecs, eid);

        var path = sys.FindPathToPosition(ecs, eid, new Vector3(15, 0, 0));
        AssertTrue(path != null, "应找到到目标位置的路径。");
        AssertEqual(2, path!.LaneIds.Count, "跨两车道寻路应返回 2 段。");
    }
}
