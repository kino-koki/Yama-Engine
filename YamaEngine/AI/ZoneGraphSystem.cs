using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.ECS;

namespace YamaEngine.AI
{
    // ════════════════════════════════════════════════════════════════════════
    //  ZoneGraphSystem.cs — yama引擎 空间区域图系统（ZoneGraph）
    //
    //  对齐 Unreal Engine 5 的 ZoneGraph（空间区域图）子系统。在 UE5 中，ZoneGraph
    //  用于对游戏世界进行"语义化空间标注"——将世界划分为带语义标签的通行区域
    //  （人行道、车道、广场、走廊、楼梯、坡道、桥梁、隧道、斑马线、路口等），
    //  以"车道（ZoneLane）"和"形状（ZoneShape）"两种基本图元构建高层道路网络，
    //  支持群体导航（Crowd Navigation）与交通流（Traffic Flow）模拟。
    //
    //  ZoneGraph 与 NavMesh（精细可行走网格）互补：
    //    - NavMesh：底层精细网格，描述"哪里能走"，用于单体精确寻路与避障；
    //    - ZoneGraph：高层语义道路网络，描述"走哪条路/什么类型的路"，用于群体
    //      路径规划、交通流调度、区域级行为决策。
    //  典型工作流：ZoneGraph 做高层路径规划（车道序列） -> 每段车道下采样到
    //  NavMesh 做底层精细寻路（避开障碍物与其他个体）。
    //
    //  本文件实现以下内容（全部位于 YamaEngine.AI 命名空间）：
    //    1.  ZoneLaneType        —— 车道类型枚举（人行/车行/自行/公交/混合/禁行）
    //    2.  ZoneLaneDirection   —— 车道方向枚举（单向/双向）
    //    3.  ZoneTag             —— 区域标签位掩码（[Flags]，对齐 UE5 ZoneGraphTag）
    //    4.  TrafficLightPhase   —— 信号灯相位（绿/黄/红）
    //    5.  ZoneLane            —— 车道（带状通行区域，图中的节点，带下游连接）
    //    6.  ZoneShape           —— 区域形状（多边形边界，广场/庭院等开放区域）
    //    7.  ZoneSpatialHash     —— XZ 空间哈希（加速车道/形状的邻近查询）
    //    8.  ZoneGraphData       —— 区域图数据（车道/形状集合 + 空间索引 + 查询）
    //    9.  ZonePath            —— 区域路径（车道序列，沿路径取位置）
    //   10.  ZonePathfinder      —— 区域寻路器（车道图上 A* 寻路，标签成本/偏好）
    //   11.  ZoneGraphComponent  —— ECS 组件（struct : IComponent），标记实体在区域图中的位置
    //   12.  ZoneGraphSystem     —— ECS 系统（ISystem），每帧更新实体所在车道
    //   13.  ZoneLaneFollowing   —— 车道跟随行为（沿车道移动，到终点切换下游车道）
    //   14.  ZoneTrafficSystem   —— 交通流系统（车道占用 + 信号灯周期）
    //   15.  ZoneGraphBuilder    —— 区域图构建器（从样条/多边形生成车道与形状）
    //   16.  ZoneGraphDebugger   —— 调试（返回车道线段与形状边界供编辑器绘制）
    //
    //  与引擎系统对接：
    //    - ECS（YamaEngine.ECS.EntityComponentSystem）：通过 IComponent / ISystem 集成，
    //      使用 CreateEntity / GetComponent / SetComponent / HasComponent /
    //      QueryWithComponent<T>() 等 API。
    //    - 导航（YamaEngine.AI.NavigationSystem）：FindPath(Vector3, Vector3) 用于
    //      将高层 ZoneGraph 路径下采样到底层 NavMesh 精细寻路。
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────────────
    // 车道类型枚举（ZoneLaneType）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 车道类型。对齐 UE5 ZoneGraph 中 ZoneLaneType 的语义，描述一条车道的通行方式。
    /// 不同车道类型决定了哪些代理（行人/车辆/自行车等）可以使用该车道，
    /// 也影响寻路时的通行成本与速度限制。
    /// </summary>
    public enum ZoneLaneType
    {
        /// <summary>人行道：供行人步行通行，速度低、密度高。</summary>
        Walk = 0,
        /// <summary>车行道：供机动车行驶，速度高、需遵守交通规则。</summary>
        Drive = 1,
        /// <summary>自行车道：供自行车/电动车通行。</summary>
        Bike = 2,
        /// <summary>公交道：供公共交通工具（公交/有轨电车）通行。</summary>
        Transit = 3,
        /// <summary>混合道：行人/车辆/自行车等混合通行（如共享街道）。</summary>
        Mixed = 4,
        /// <summary>禁行道：禁止通行（施工区/障碍区等），寻路时不可经过。</summary>
        Restricted = 5,
    }

    // ──────────────────────────────────────────────────────────────────────
    // 车道方向枚举（ZoneLaneDirection）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 车道通行方向。对齐 UE5 ZoneGraph 中车道的单向/双向属性。
    /// 单向车道仅允许从 Start 流向 End；双向车道两个方向均可通行。
    /// </summary>
    public enum ZoneLaneDirection
    {
        /// <summary>单向：仅允许从 Start 到 End 方向通行。</summary>
        OneWay = 0,
        /// <summary>双向：两个方向均可通行。</summary>
        TwoWay = 1,
    }

    // ──────────────────────────────────────────────────────────────────────
    // 区域标签位掩码（ZoneTag）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 区域标签位掩码。使用 [Flags] 以支持位运算组合，对齐 UE5 ZoneGraph 中
    /// 通过 ZoneGraphTag 对车道/区域进行语义分类的做法。一条车道或形状可同时
    /// 拥有多个标签（例如一条斑马线同时是 Crosswalk | Road | Outdoor）。
    /// 查询/寻路时通过 RequiredTags（必须拥有全部指定标签）与 PreferenceTags
    /// （偏好标签降低通行成本）进行筛选与优化。
    /// </summary>
    [Flags]
    public enum ZoneTag : ulong
    {
        /// <summary>无标签。</summary>
        None       = 0,

        /// <summary>道路：一般道路表面。</summary>
        Road       = 1 << 0,
        /// <summary>人行道：专供行人步行的道路边缘区域。</summary>
        Sidewalk   = 1 << 1,
        /// <summary>广场：开阔的公共空间（集市/广场/庭院等）。</summary>
        Plaza      = 1 << 2,
        /// <summary>走廊：建筑物内的通道。</summary>
        Corridor   = 1 << 3,
        /// <summary>楼梯：需上下台阶的通道。</summary>
        Stair      = 1 << 4,
        /// <summary>坡道：倾斜的通行面（无障碍坡道/车道坡道等）。</summary>
        Ramp       = 1 << 5,
        /// <summary>桥梁：跨越地形障碍的高架通道。</summary>
        Bridge     = 1 << 6,
        /// <summary>隧道：穿越地形障碍的地下通道。</summary>
        Tunnel     = 1 << 7,
        /// <summary>斑马线：路口处的人行横道。</summary>
        Crosswalk  = 1 << 8,
        /// <summary>路口：多条道路交汇的区域，通常设有信号灯。</summary>
        Junction   = 1 << 9,
        /// <summary>停车区：供车辆停泊的区域。</summary>
        Parking    = 1 << 10,
        /// <summary>绿地：公园/草坪等绿化区域（可步行但成本较高）。</summary>
        Green      = 1 << 11,
        /// <summary>滨水：河流/湖泊/海岸边的区域。</summary>
        Waterfront = 1 << 12,
        /// <summary>室内：建筑物内部空间。</summary>
        Indoor     = 1 << 13,
        /// <summary>室外：露天空间。</summary>
        Outdoor    = 1 << 14,

        /// <summary>全部标签（用于掩码运算与"任意标签"查询）。</summary>
        All        = ~0UL,
    }

    // ──────────────────────────────────────────────────────────────────────
    // 信号灯相位（TrafficLightPhase）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 信号灯相位。对齐 UE5 交通流系统中路口信号灯的简化模型。
    /// 绿灯允许通行，黄灯警示即将变红，红灯禁止通行。
    /// </summary>
    public enum TrafficLightPhase
    {
        /// <summary>绿灯：允许通行。</summary>
        Green = 0,
        /// <summary>黄灯：即将变红，减速准备停车。</summary>
        Yellow = 1,
        /// <summary>红灯：禁止通行。</summary>
        Red = 2,
    }

    // ──────────────────────────────────────────────────────────────────────
    // 车道（ZoneLane）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 车道：一条带状通行区域，是 ZoneGraph 图中的基本节点。对齐 UE5 中 FZoneLaneData。
    /// 一条车道由一条样条（SplinePoints，至少 2 个点）定义中心线，配合宽度 Width
    /// 描述通行带状区域。车道通过 ConnectedLanes 建立下游连接，形成有向图。
    /// </summary>
    public class ZoneLane
    {
        /// <summary>车道唯一标识符（LaneId）。</summary>
        public int LaneId;
        /// <summary>车道中心线样条点（至少 2 个点：起点与终点，中间点构成曲线）。</summary>
        public List<Vector3> SplinePoints;
        /// <summary>车道起点世界坐标（= SplinePoints[0]）。</summary>
        public Vector3 Start;
        /// <summary>车道终点世界坐标（= SplinePoints[最后一个]）。</summary>
        public Vector3 End;
        /// <summary>车道右侧法向（归一化，垂直于车道前进方向，在 XZ 平面内）。</summary>
        public Vector3 RightVec;
        /// <summary>车道宽度（世界单位）。</summary>
        public float Width;
        /// <summary>车道类型。</summary>
        public ZoneLaneType LaneType;
        /// <summary>区域标签。</summary>
        public ZoneTag Tags;
        /// <summary>限速（单位：米/秒）。0 表示无限制。</summary>
        public float SpeedLimit;
        /// <summary>通行方向（单向/双向）。</summary>
        public ZoneLaneDirection Direction;
        /// <summary>下游连接的车道 ID 列表（形成有向图的邻接表）。</summary>
        public List<int> ConnectedLanes;
        /// <summary>所属区域 ID（ZoneId，用于将多条车道归组到一个逻辑区域）。</summary>
        public int ZoneId;
        /// <summary>车道长度（沿样条累积长度，自动计算）。</summary>
        public float Length;

        /// <summary>构造车道并自动计算派生属性（Start/End/RightVec/Length）。</summary>
        /// <param name="laneId">车道唯一 ID。</param>
        /// <param name="splinePoints">中心线样条点（至少 2 个）。</param>
        /// <param name="width">车道宽度。</param>
        /// <param name="laneType">车道类型。</param>
        /// <param name="tags">区域标签。</param>
        public ZoneLane(int laneId, List<Vector3> splinePoints, float width,
                        ZoneLaneType laneType, ZoneTag tags)
        {
            LaneId = laneId;
            SplinePoints = splinePoints ?? new List<Vector3> { Vector3.Zero, Vector3.Zero };
            if (SplinePoints.Count < 2)
            {
                // 保证至少 2 个点，避免后续插值异常
                while (SplinePoints.Count < 2)
                    SplinePoints.Add(Vector3.Zero);
            }
            Width = Math.Max(0.1f, width);
            LaneType = laneType;
            Tags = tags;
            SpeedLimit = 0f;
            Direction = ZoneLaneDirection.TwoWay;
            ConnectedLanes = new List<int>();
            ZoneId = 0;

            Start = SplinePoints[0];
            End = SplinePoints[SplinePoints.Count - 1];
            Length = ComputeLength();
            RightVec = ComputeRightVec();
        }

        /// <summary>沿样条累积计算车道总长度。</summary>
        private float ComputeLength()
        {
            float len = 0f;
            for (int i = 0; i < SplinePoints.Count - 1; i++)
                len += Vector3.Distance(SplinePoints[i], SplinePoints[i + 1]);
            return len;
        }

        /// <summary>
        /// 计算车道右侧法向。取车道整体前进方向（Start->End），与世界上方向叉乘得到右侧向量。
        /// 叉乘公式：Right = Cross(dir, Up)，在右手坐标系中得到前进方向的右侧。
        /// </summary>
        private Vector3 ComputeRightVec()
        {
            Vector3 dir = End - Start;
            if (dir.LengthSquared() < 1e-8f)
                return new Vector3(1, 0, 0); // 退化情况：返回默认 +X
            dir = Vector3.Normalize(dir);
            // Right = Cross(dir, Up) = Cross(dir, (0,1,0))
            Vector3 right = Vector3.Cross(dir, Vector3.UnitY);
            if (right.LengthSquared() < 1e-8f)
                return new Vector3(1, 0, 0); // 车道垂直向上时退化
            return Vector3.Normalize(right);
        }

        /// <summary>
        /// 沿车道在参数 t（0=起点, 1=终点）处插值得到世界坐标。
        /// 对齐 UE5 中 FZoneLaneData::GetPositionAtT 的语义。
        /// 内部将 t 转换为沿样条的弧长距离，再沿分段线性插值。
        /// </summary>
        public Vector3 GetPositionAtT(float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            float targetDist = t * Length;
            return GetPositionAtDistance(targetDist);
        }

        /// <summary>
        /// 沿车道在指定弧长距离处取世界坐标。沿样条分段累积距离并线性插值。
        /// </summary>
        public Vector3 GetPositionAtDistance(float distance)
        {
            if (Length <= 0f) return Start;
            distance = Math.Clamp(distance, 0f, Length);
            float accum = 0f;
            for (int i = 0; i < SplinePoints.Count - 1; i++)
            {
                Vector3 a = SplinePoints[i];
                Vector3 b = SplinePoints[i + 1];
                float segLen = Vector3.Distance(a, b);
                if (accum + segLen >= distance || i == SplinePoints.Count - 2)
                {
                    float localT = segLen > 1e-8f ? (distance - accum) / segLen : 0f;
                    localT = Math.Clamp(localT, 0f, 1f);
                    return Vector3.Lerp(a, b, localT);
                }
                accum += segLen;
            }
            return End;
        }

        /// <summary>
        /// 查询给定点在车道上的最近点。遍历样条各段，将查询点投影到每段线段上，
        /// 取距离最近的投影点。对齐 UE5 中车道最近点查询的语义。
        /// </summary>
        public Vector3 GetClosestPoint(Vector3 pos)
        {
            if (SplinePoints.Count < 2) return Start;
            float bestDistSq = float.MaxValue;
            Vector3 bestPoint = Start;
            float bestDistAlong = 0f;

            float accum = 0f;
            for (int i = 0; i < SplinePoints.Count - 1; i++)
            {
                Vector3 a = SplinePoints[i];
                Vector3 b = SplinePoints[i + 1];
                Vector3 ab = b - a;
                float segLenSq = ab.LengthSquared();
                float segLen = (float)Math.Sqrt(segLenSq);

                float localT = 0f;
                if (segLenSq > 1e-8f)
                {
                    localT = Vector3.Dot(pos - a, ab) / segLenSq;
                    localT = Math.Clamp(localT, 0f, 1f);
                }
                Vector3 proj = Vector3.Lerp(a, b, localT);
                float distSq = Vector3.DistanceSquared(pos, proj);
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    bestPoint = proj;
                    bestDistAlong = accum + localT * segLen;
                }
                accum += segLen;
            }
            return bestPoint;
        }

        /// <summary>
        /// 查询给定点在车道上的参数 t（0~1）。用于 ZoneGraphComponent 的 LaneT 更新。
        /// </summary>
        public float GetTAtClosestPoint(Vector3 pos)
        {
            if (SplinePoints.Count < 2 || Length <= 0f) return 0f;
            float bestDistSq = float.MaxValue;
            float bestDistAlong = 0f;

            float accum = 0f;
            for (int i = 0; i < SplinePoints.Count - 1; i++)
            {
                Vector3 a = SplinePoints[i];
                Vector3 b = SplinePoints[i + 1];
                Vector3 ab = b - a;
                float segLenSq = ab.LengthSquared();
                float segLen = (float)Math.Sqrt(segLenSq);

                float localT = 0f;
                if (segLenSq > 1e-8f)
                {
                    localT = Vector3.Dot(pos - a, ab) / segLenSq;
                    localT = Math.Clamp(localT, 0f, 1f);
                }
                Vector3 proj = Vector3.Lerp(a, b, localT);
                float distSq = Vector3.DistanceSquared(pos, proj);
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    bestDistAlong = accum + localT * segLen;
                }
                accum += segLen;
            }
            return Math.Clamp(bestDistAlong / Length, 0f, 1f);
        }

        /// <summary>判断车道是否拥有指定标签（位与运算）。</summary>
        public bool HasTag(ZoneTag tag) => (Tags & tag) != ZoneTag.None;

        /// <summary>判断车道是否拥有全部指定标签。</summary>
        public bool HasAllTags(ZoneTag required) => (Tags & required) == required;

        /// <summary>判断车道是否拥有任意指定标签。</summary>
        public bool HasAnyTag(ZoneTag any) => (Tags & any) != ZoneTag.None;

        /// <summary>
        /// 判断给定点（XZ 平面）是否在车道带状区域内。
        /// 通过计算点到车道中心线的距离是否小于半宽来判断。
        /// </summary>
        public bool ContainsPointXZ(Vector3 pos)
        {
            Vector3 closest = GetClosestPoint(pos);
            float distSq = (pos.X - closest.X) * (pos.X - closest.X) +
                           (pos.Z - closest.Z) * (pos.Z - closest.Z);
            float halfWidth = Width * 0.5f;
            return distSq <= halfWidth * halfWidth;
        }

        /// <summary>车道的 XZ 包围盒最小角（用于空间哈希）。</summary>
        public Vector2 BoundsMinXZ
        {
            get
            {
                float minX = float.MaxValue, minZ = float.MaxValue;
                foreach (var p in SplinePoints)
                {
                    if (p.X < minX) minX = p.X;
                    if (p.Z < minZ) minZ = p.Z;
                }
                return new Vector2(minX, minZ);
            }
        }

        /// <summary>车道的 XZ 包围盒最大角（用于空间哈希）。</summary>
        public Vector2 BoundsMaxXZ
        {
            get
            {
                float maxX = float.MinValue, maxZ = float.MinValue;
                foreach (var p in SplinePoints)
                {
                    if (p.X > maxX) maxX = p.X;
                    if (p.Z > maxZ) maxZ = p.Z;
                }
                return new Vector2(maxX, maxZ);
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 区域形状（ZoneShape）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 区域形状：由多边形边界描述的开放区域（广场、庭院、停车场等）。
    /// 对齐 UE5 ZoneGraph 中的 ZoneShape 概念。与车道（带状）不同，形状是面状区域，
    /// 用于描述二维开放空间。通过 ContainsPoint 判断点是否在区域内。
    /// </summary>
    public class ZoneShape
    {
        /// <summary>形状唯一标识符（ShapeId）。</summary>
        public int ShapeId;
        /// <summary>多边形顶点列表（按顺序连接形成闭合边界）。</summary>
        public List<Vector3> Points;
        /// <summary>区域标签。</summary>
        public ZoneTag Tags;
        /// <summary>XZ 包围盒最小角（自动计算，加速 ContainsPoint 的快速剔除）。</summary>
        public Vector3 BoundsMin;
        /// <summary>XZ 包围盒最大角（自动计算）。</summary>
        public Vector3 BoundsMax;

        /// <summary>构造形状并自动计算包围盒。</summary>
        public ZoneShape(int shapeId, List<Vector3> points, ZoneTag tags)
        {
            ShapeId = shapeId;
            Points = points ?? new List<Vector3>();
            Tags = tags;
            ComputeBounds();
        }

        /// <summary>计算 XZ 包围盒（Y 取所有点的范围）。</summary>
        private void ComputeBounds()
        {
            if (Points.Count == 0)
            {
                BoundsMin = Vector3.Zero;
                BoundsMax = Vector3.Zero;
                return;
            }
            Vector3 mn = Points[0];
            Vector3 mx = Points[0];
            for (int i = 1; i < Points.Count; i++)
            {
                Vector3 p = Points[i];
                if (p.X < mn.X) mn.X = p.X; else if (p.X > mx.X) mx.X = p.X;
                if (p.Y < mn.Y) mn.Y = p.Y; else if (p.Y > mx.Y) mx.Y = p.Y;
                if (p.Z < mn.Z) mn.Z = p.Z; else if (p.Z > mx.Z) mx.Z = p.Z;
            }
            BoundsMin = mn;
            BoundsMax = mx;
        }

        /// <summary>
        /// 判断点是否在多边形区域内（XZ 平面射线法）。
        /// 先用包围盒快速剔除，再执行射线交叉测试。
        /// 对齐 UE5 中 ZoneShape 的点包含查询。
        /// </summary>
        public bool ContainsPoint(Vector3 pos)
        {
            // 包围盒快速剔除
            if (pos.X < BoundsMin.X || pos.X > BoundsMax.X ||
                pos.Z < BoundsMin.Z || pos.Z > BoundsMax.Z)
                return false;

            int n = Points.Count;
            if (n < 3) return false;

            // 射线法：从测试点向 +X 方向发射水平射线，
            // 统计与多边形边界的交点数。奇数次=在内部，偶数次=在外部。
            bool inside = false;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                Vector3 pi = Points[i];
                Vector3 pj = Points[j];

                // 检查边 (pj -> pi) 是否与水平射线相交
                bool intersect = ((pi.Z > pos.Z) != (pj.Z > pos.Z)) &&
                                 (pos.X < (pj.X - pi.X) * (pos.Z - pi.Z) /
                                          (pj.Z - pi.Z) + pi.X);
                if (intersect) inside = !inside;
            }
            return inside;
        }

        /// <summary>判断形状是否拥有指定标签。</summary>
        public bool HasTag(ZoneTag tag) => (Tags & tag) != ZoneTag.None;

        /// <summary>判断形状是否拥有全部指定标签。</summary>
        public bool HasAllTags(ZoneTag required) => (Tags & required) == required;

        /// <summary>形状中心的 XZ 坐标（包围盒中心，用于空间哈希）。</summary>
        public Vector2 CenterXZ => new(
            (BoundsMin.X + BoundsMax.X) * 0.5f,
            (BoundsMin.Z + BoundsMax.Z) * 0.5f);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 空间哈希（ZoneSpatialHash）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// XZ 平面空间哈希索引。对齐 UE5 ZoneGraph 中用于加速邻近查询的空间分区结构。
    /// 将世界 XZ 平面划分为固定大小的格子（Cell），每个格子记录覆盖该格子的
    /// 车道 ID 和形状 ID。查询时只需检查查询位置附近少数格子，避免全量遍历。
    /// </summary>
    public class ZoneSpatialHash
    {
        /// <summary>格子大小（世界单位）。应大于最大车道宽度，保证查询精度。</summary>
        public float CellSize;
        /// <summary>车道格子索引：键=(cellX, cellZ)，值=覆盖该格子的车道 ID 列表。</summary>
        public Dictionary<(int cx, int cz), List<int>> LaneCells;
        /// <summary>形状格子索引：键=(cellX, cellZ)，值=覆盖该格子的形状 ID 列表。</summary>
        public Dictionary<(int cx, int cz), List<int>> ShapeCells;

        /// <summary>构造空间哈希并指定格子大小。</summary>
        public ZoneSpatialHash(float cellSize = 16f)
        {
            CellSize = Math.Max(1f, cellSize);
            LaneCells = new Dictionary<(int, int), List<int>>();
            ShapeCells = new Dictionary<(int, int), List<int>>();
        }

        /// <summary>将世界坐标转换为格子坐标。</summary>
        public (int cx, int cz) WorldToCell(float x, float z)
        {
            return ((int)Math.Floor(x / CellSize), (int)Math.Floor(z / CellSize));
        }

        /// <summary>清空所有索引数据。</summary>
        public void Clear()
        {
            LaneCells.Clear();
            ShapeCells.Clear();
        }

        /// <summary>
        /// 将一条车道加入空间哈希。遍历车道 XZ 包围盒覆盖的所有格子，
        /// 在每个格子中记录该车道 ID。
        /// </summary>
        public void AddLane(ZoneLane lane)
        {
            Vector2 mn = lane.BoundsMinXZ;
            Vector2 mx = lane.BoundsMaxXZ;
            (int x0, int z0) = WorldToCell(mn.X - lane.Width * 0.5f, mn.Y - lane.Width * 0.5f);
            (int x1, int z1) = WorldToCell(mx.X + lane.Width * 0.5f, mx.Y + lane.Width * 0.5f);
            for (int cx = x0; cx <= x1; cx++)
            {
                for (int cz = z0; cz <= z1; cz++)
                {
                    var key = (cx, cz);
                    if (!LaneCells.TryGetValue(key, out var list))
                    {
                        list = new List<int>();
                        LaneCells[key] = list;
                    }
                    if (!list.Contains(lane.LaneId))
                        list.Add(lane.LaneId);
                }
            }
        }

        /// <summary>
        /// 将一个形状加入空间哈希。遍历形状 XZ 包围盒覆盖的所有格子。
        /// </summary>
        public void AddShape(ZoneShape shape)
        {
            (int x0, int z0) = WorldToCell(shape.BoundsMin.X, shape.BoundsMin.Z);
            (int x1, int z1) = WorldToCell(shape.BoundsMax.X, shape.BoundsMax.Z);
            for (int cx = x0; cx <= x1; cx++)
            {
                for (int cz = z0; cz <= z1; cz++)
                {
                    var key = (cx, cz);
                    if (!ShapeCells.TryGetValue(key, out var list))
                    {
                        list = new List<int>();
                        ShapeCells[key] = list;
                    }
                    if (!list.Contains(shape.ShapeId))
                        list.Add(shape.ShapeId);
                }
            }
        }

        /// <summary>查询指定位置所在格子中的所有车道 ID（不含去重）。</summary>
        public List<int> GetLanesAtCell(Vector3 pos)
        {
            var key = WorldToCell(pos.X, pos.Z);
            if (LaneCells.TryGetValue(key, out var list))
                return new List<int>(list);
            return new List<int>();
        }

        /// <summary>
        /// 查询指定位置附近指定半径内的所有车道 ID（去重）。
        /// 遍历覆盖查询球 XZ 投影的所有格子，收集车道 ID。
        /// </summary>
        public List<int> GetLanesNear(Vector3 pos, float radius)
        {
            var result = new List<int>();
            var seen = new HashSet<int>();
            (int x0, int z0) = WorldToCell(pos.X - radius, pos.Z - radius);
            (int x1, int z1) = WorldToCell(pos.X + radius, pos.Z + radius);
            for (int cx = x0; cx <= x1; cx++)
            {
                for (int cz = z0; cz <= z1; cz++)
                {
                    var key = (cx, cz);
                    if (LaneCells.TryGetValue(key, out var list))
                    {
                        foreach (int id in list)
                        {
                            if (seen.Add(id))
                                result.Add(id);
                        }
                    }
                }
            }
            return result;
        }

        /// <summary>查询指定位置附近的所有形状 ID（去重）。</summary>
        public List<int> GetShapesNear(Vector3 pos)
        {
            var key = WorldToCell(pos.X, pos.Z);
            if (ShapeCells.TryGetValue(key, out var list))
                return new List<int>(list);
            return new List<int>();
        }

        /// <summary>查询指定位置附近指定半径内的所有形状 ID（去重）。</summary>
        public List<int> GetShapesNear(Vector3 pos, float radius)
        {
            var result = new List<int>();
            var seen = new HashSet<int>();
            (int x0, int z0) = WorldToCell(pos.X - radius, pos.Z - radius);
            (int x1, int z1) = WorldToCell(pos.X + radius, pos.Z + radius);
            for (int cx = x0; cx <= x1; cx++)
            {
                for (int cz = z0; cz <= z1; cz++)
                {
                    var key = (cx, cz);
                    if (ShapeCells.TryGetValue(key, out var list))
                    {
                        foreach (int id in list)
                        {
                            if (seen.Add(id))
                                result.Add(id);
                        }
                    }
                }
            }
            return result;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 区域图数据（ZoneGraphData）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 区域图数据。对齐 UE5 中 FZoneGraphData，是整个 ZoneGraph 系统的核心数据容器。
    /// 持有所有车道与形状的字典，以及用于加速查询的空间哈希索引。
    /// 提供最近车道/形状查询、半径范围查询、邻接遍历等接口。
    /// </summary>
    public class ZoneGraphData
    {
        /// <summary>车道字典，键为 LaneId。</summary>
        public Dictionary<int, ZoneLane> Lanes;
        /// <summary>形状字典，键为 ShapeId。</summary>
        public Dictionary<int, ZoneShape> Shapes;
        /// <summary>XZ 空间哈希索引（加速邻近查询）。</summary>
        public ZoneSpatialHash SpatialHash;
        /// <summary>空间哈希格子大小。</summary>
        public float HashCellSize;

        /// <summary>构造空区域图数据。</summary>
        /// <param name="hashCellSize">空间哈希格子大小（默认 16 米）。</param>
        public ZoneGraphData(float hashCellSize = 16f)
        {
            Lanes = new Dictionary<int, ZoneLane>();
            Shapes = new Dictionary<int, ZoneShape>();
            HashCellSize = hashCellSize;
            SpatialHash = new ZoneSpatialHash(hashCellSize);
        }

        /// <summary>车道总数。</summary>
        public int LaneCount => Lanes.Count;
        /// <summary>形状总数。</summary>
        public int ShapeCount => Shapes.Count;

        /// <summary>添加一条车道到区域图。</summary>
        public void AddLane(ZoneLane lane)
        {
            Lanes[lane.LaneId] = lane;
        }

        /// <summary>添加一个形状到区域图。</summary>
        public void AddShape(ZoneShape shape)
        {
            Shapes[shape.ShapeId] = shape;
        }

        /// <summary>按 LaneId 获取车道（不存在返回 null）。</summary>
        public ZoneLane? GetLane(int laneId)
        {
            return Lanes.TryGetValue(laneId, out var lane) ? lane : null;
        }

        /// <summary>按 ShapeId 获取形状（不存在返回 null）。</summary>
        public ZoneShape? GetShape(int shapeId)
        {
            return Shapes.TryGetValue(shapeId, out var shape) ? shape : null;
        }

        /// <summary>移除指定车道。</summary>
        public bool RemoveLane(int laneId) => Lanes.Remove(laneId);

        /// <summary>移除指定形状。</summary>
        public bool RemoveShape(int shapeId) => Shapes.Remove(shapeId);

        /// <summary>
        /// 构建空间哈希索引。在所有车道与形状添加完毕后调用一次，
        /// 将所有车道与形状注册到空间哈希中，加速后续邻近查询。
        /// 对齐 UE5 中 ZoneGraph 构建阶段的空间索引生成。
        /// </summary>
        public void BuildSpatialHash()
        {
            SpatialHash = new ZoneSpatialHash(HashCellSize);
            foreach (var kvp in Lanes)
                SpatialHash.AddLane(kvp.Value);
            foreach (var kvp in Shapes)
                SpatialHash.AddShape(kvp.Value);
        }

        /// <summary>
        /// 查找距离指定位置最近的车道。对齐 UE5 ZoneGraph 的 FindNearestLane。
        /// 利用空间哈希仅在附近格子中搜索候选车道，避免全量遍历。
        /// 若指定 requiredTags，仅考虑拥有全部所需标签的车道。
        /// </summary>
        /// <param name="pos">查询位置（世界坐标）。</param>
        /// <param name="requiredTags">可选的必需标签过滤（null 表示不过滤）。</param>
        /// <returns>最近车道，若无候选则返回 null。</returns>
        public ZoneLane? FindNearestLane(Vector3 pos, ZoneTag? requiredTags = null)
        {
            // 逐步扩大搜索半径，直到找到候选车道
            // 注意：不使用 float.MaxValue 作为半径，否则空间哈希会尝试遍历极大数量的格子
            float[] radii = { HashCellSize, HashCellSize * 2, HashCellSize * 4, HashCellSize * 8, HashCellSize * 16 };
            ZoneLane? best = null;
            float bestDistSq = float.MaxValue;
            var seen = new HashSet<int>();

            foreach (float radius in radii)
            {
                List<int> candidates = SpatialHash.GetLanesNear(pos, radius);
                bool foundAny = false;
                foreach (int id in candidates)
                {
                    if (!seen.Add(id)) continue;
                    if (!Lanes.TryGetValue(id, out var lane)) continue;
                    if (lane.LaneType == ZoneLaneType.Restricted) continue;
                    if (requiredTags.HasValue && !lane.HasAllTags(requiredTags.Value)) continue;
                    foundAny = true;
                    Vector3 closest = lane.GetClosestPoint(pos);
                    float distSq = Vector3.DistanceSquared(pos, closest);
                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        best = lane;
                    }
                }
                // 如果当前半径已找到候选，且搜索半径的平方已覆盖最近距离，则无需继续扩大
                if (foundAny && radius * radius >= bestDistSq)
                    break;
            }
            // 若空间哈希在所有半径内均未找到候选，退化为全量搜索
            if (best == null && Lanes.Count > 0)
            {
                foreach (var kvp in Lanes)
                {
                    var lane = kvp.Value;
                    if (lane.LaneType == ZoneLaneType.Restricted) continue;
                    if (requiredTags.HasValue && !lane.HasAllTags(requiredTags.Value)) continue;
                    Vector3 closest = lane.GetClosestPoint(pos);
                    float distSq = Vector3.DistanceSquared(pos, closest);
                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        best = lane;
                    }
                }
            }
            return best;
        }

        /// <summary>
        /// 查找指定位置指定半径内的所有车道。
        /// 返回的车道按距离从近到远排序。
        /// </summary>
        public List<ZoneLane> FindLanesInRadius(Vector3 pos, float radius)
        {
            var result = new List<ZoneLane>();
            List<int> candidates = SpatialHash.GetLanesNear(pos, radius);
            var seen = new HashSet<int>();
            float radiusSq = radius * radius;

            foreach (int id in candidates)
            {
                if (!seen.Add(id)) continue;
                if (!Lanes.TryGetValue(id, out var lane)) continue;
                Vector3 closest = lane.GetClosestPoint(pos);
                float distSq = Vector3.DistanceSquared(pos, closest);
                if (distSq <= radiusSq)
                    result.Add(lane);
            }
            // 退化为全量搜索（空间哈希为空时）
            if (result.Count == 0 && Lanes.Count > 0 && SpatialHash.LaneCells.Count == 0)
            {
                foreach (var kvp in Lanes)
                {
                    var lane = kvp.Value;
                    Vector3 closest = lane.GetClosestPoint(pos);
                    float distSq = Vector3.DistanceSquared(pos, closest);
                    if (distSq <= radiusSq)
                        result.Add(lane);
                }
            }
            // 按距离排序
            result.Sort((a, b) =>
            {
                float da = Vector3.DistanceSquared(pos, a.GetClosestPoint(pos));
                float db = Vector3.DistanceSquared(pos, b.GetClosestPoint(pos));
                return da.CompareTo(db);
            });
            return result;
        }

        /// <summary>
        /// 查找距离指定位置最近的形状（广场/庭院等开放区域）。
        /// 优先检查点是否在某个形状内部，否则取包围盒中心最近的形状。
        /// </summary>
        public ZoneShape? FindNearestShape(Vector3 pos)
        {
            // 1) 先检查是否在某个形状内部
            List<int> nearby = SpatialHash.GetShapesNear(pos);
            foreach (int id in nearby)
            {
                if (Shapes.TryGetValue(id, out var shape) && shape.ContainsPoint(pos))
                    return shape;
            }
            // 退化为全量搜索（空间哈希为空时）
            if (nearby.Count == 0 && Shapes.Count > 0 && SpatialHash.ShapeCells.Count == 0)
            {
                foreach (var kvp in Shapes)
                {
                    if (kvp.Value.ContainsPoint(pos))
                        return kvp.Value;
                }
            }

            // 2) 未在内部，取包围盒中心距离最近的形状
            ZoneShape? best = null;
            float bestDistSq = float.MaxValue;
            foreach (var kvp in Shapes)
            {
                var shape = kvp.Value;
                Vector2 center = shape.CenterXZ;
                float distSq = (pos.X - center.X) * (pos.X - center.X) +
                               (pos.Z - center.Y) * (pos.Z - center.Y);
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    best = shape;
                }
            }
            return best;
        }

        /// <summary>
        /// 获取指定车道的所有下游连接车道（图遍历邻接）。
        /// 对齐 UE5 ZoneGraph 中车道连接关系的遍历。
        /// </summary>
        public List<ZoneLane> GetConnectedLanes(int laneId)
        {
            var result = new List<ZoneLane>();
            if (Lanes.TryGetValue(laneId, out var lane))
            {
                foreach (int connId in lane.ConnectedLanes)
                {
                    if (Lanes.TryGetValue(connId, out var conn))
                        result.Add(conn);
                }
            }
            return result;
        }

        /// <summary>获取指定车道的所有下游连接车道 ID 列表。</summary>
        public List<int> GetConnectedLaneIds(int laneId)
        {
            if (Lanes.TryGetValue(laneId, out var lane))
                return new List<int>(lane.ConnectedLanes);
            return new List<int>();
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 区域路径（ZonePath）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 区域路径：由车道序列组成的路径。对齐 UE5 中 FZoneGraphPath。
    /// 由 ZonePathfinder 的 A* 搜索结果构建，记录途经的车道 ID 列表与总长度，
    /// 支持沿路径按弧长取位置（供代理沿路径移动）。
    /// </summary>
    public class ZonePath
    {
        /// <summary>途经的车道 ID 列表（按通行顺序）。</summary>
        public List<int> LaneIds;
        /// <summary>路径总长度（所有车道长度之和）。</summary>
        public float TotalLength;
        /// <summary>引用的区域图数据（用于解析车道位置）。</summary>
        public ZoneGraphData? Graph;

        /// <summary>构造空路径。</summary>
        public ZonePath()
        {
            LaneIds = new List<int>();
            TotalLength = 0f;
            Graph = null;
        }

        /// <summary>从车道 ID 列表与区域图构建路径并计算总长度。</summary>
        public ZonePath(List<int> laneIds, ZoneGraphData graph)
        {
            LaneIds = laneIds ?? new List<int>();
            Graph = graph;
            TotalLength = 0f;
            foreach (int id in LaneIds)
            {
                var lane = graph.GetLane(id);
                if (lane != null)
                    TotalLength += lane.Length;
            }
        }

        /// <summary>路径是否有效（至少包含一条车道）。</summary>
        public bool IsValid => LaneIds.Count > 0;

        /// <summary>路径中车道段数。</summary>
        public int SegmentCount => LaneIds.Count;

        /// <summary>
        /// 沿路径在指定弧长距离处取世界坐标。
        /// 遍历路径中的各车道段，在对应车道段内插值取位置。
        /// 对齐 UE5 中沿 ZoneGraph 路径取位置的功能。
        /// </summary>
        /// <param name="distance">沿路径的弧长距离（0=起点）。</param>
        /// <returns>该距离处的世界坐标。超出范围则返回路径终点。</returns>
        public Vector3 GetPositionAtDistance(float distance)
        {
            if (Graph == null || LaneIds.Count == 0)
                return Vector3.Zero;

            distance = Math.Clamp(distance, 0f, TotalLength);
            float accum = 0f;
            foreach (int id in LaneIds)
            {
                var lane = Graph.GetLane(id);
                if (lane == null) continue;
                if (accum + lane.Length >= distance)
                {
                    float localDist = distance - accum;
                    return lane.GetPositionAtDistance(localDist);
                }
                accum += lane.Length;
            }
            // 返回最后一条车道的终点
            var lastLane = Graph.GetLane(LaneIds[LaneIds.Count - 1]);
            return lastLane?.End ?? Vector3.Zero;
        }

        /// <summary>
        /// 沿路径在参数 t（0=起点, 1=终点）处取世界坐标。
        /// </summary>
        public Vector3 GetPositionAtT(float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            return GetPositionAtDistance(t * TotalLength);
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 区域寻路器（ZonePathfinder）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 区域寻路器：在车道图上执行 A* 寻路。对齐 UE5 ZoneGraph 的路径规划。
    /// 代价函数 = 车道长度 + 标签成本调整：
    ///   - 若设置 RequiredTags，仅遍历拥有全部所需标签的车道；
    ///   - PreferenceTags 中的标签会降低车道代价（如行人优先走 Sidewalk）；
    ///   - PenaltyTags 中的标签会增加车道代价（如避免走 Junction/Parking）。
    /// </summary>
    public class ZonePathfinder
    {
        /// <summary>引用的区域图数据。</summary>
        public ZoneGraphData Graph;
        /// <summary>偏好标签：拥有这些标签的车道代价降低（乘以折扣系数）。</summary>
        public ZoneTag PreferenceTags;
        /// <summary>偏好折扣系数（0~1，越小越偏好）。</summary>
        public float PreferenceDiscount = 0.5f;
        /// <summary>惩罚标签：拥有这些标签的车道代价增加（乘以惩罚系数）。</summary>
        public ZoneTag PenaltyTags;
        /// <summary>惩罚系数（>1，越大越避免）。</summary>
        public float PenaltyMultiplier = 2.0f;

        /// <summary>构造寻路器并绑定区域图。</summary>
        public ZonePathfinder(ZoneGraphData graph)
        {
            Graph = graph;
        }

        /// <summary>
        /// 在车道图上执行 A* 寻路，从起始车道到目标车道。
        /// 对齐 UE5 ZoneGraph 的 FindPath 语义。
        /// </summary>
        /// <param name="startLaneId">起始车道 ID。</param>
        /// <param name="endLaneId">目标车道 ID。</param>
        /// <param name="requiredTags">可选的必需标签（null 表示不过滤）。</param>
        /// <returns>找到的 ZonePath，若无路径则返回 null。</returns>
        public ZonePath? FindPath(int startLaneId, int endLaneId, ZoneTag? requiredTags = null)
        {
            if (!Graph.Lanes.ContainsKey(startLaneId) || !Graph.Lanes.ContainsKey(endLaneId))
                return null;
            if (startLaneId == endLaneId)
                return new ZonePath(new List<int> { startLaneId }, Graph);

            // A* 算法数据结构
            var openSet = new HashSet<int> { startLaneId };
            var cameFrom = new Dictionary<int, int>();
            var gScore = new Dictionary<int, float>();
            var fScore = new Dictionary<int, float>();

            gScore[startLaneId] = 0f;
            fScore[startLaneId] = Heuristic(Graph.GetLane(startLaneId)!, Graph.GetLane(endLaneId)!);

            while (openSet.Count > 0)
            {
                // 从 openSet 中选取 fScore 最小的节点
                int current = -1;
                float lowestF = float.MaxValue;
                foreach (int id in openSet)
                {
                    float f = fScore.TryGetValue(id, out var v) ? v : float.MaxValue;
                    if (f < lowestF)
                    {
                        lowestF = f;
                        current = id;
                    }
                }
                if (current == -1) break;

                // 到达目标，重建路径
                if (current == endLaneId)
                    return ReconstructPath(cameFrom, current);

                openSet.Remove(current);

                // 遍历邻接车道
                var currentLane = Graph.GetLane(current);
                if (currentLane == null) continue;

                foreach (int neighborId in currentLane.ConnectedLanes)
                {
                    var neighborLane = Graph.GetLane(neighborId);
                    if (neighborLane == null) continue;
                    // 禁行车道不可经过
                    if (neighborLane.LaneType == ZoneLaneType.Restricted) continue;
                    // 必需标签过滤
                    if (requiredTags.HasValue && !neighborLane.HasAllTags(requiredTags.Value))
                        continue;

                    float tentativeG = gScore[current] + LaneCost(neighborLane);

                    if (tentativeG < (gScore.TryGetValue(neighborId, out var ng) ? ng : float.MaxValue))
                    {
                        cameFrom[neighborId] = current;
                        gScore[neighborId] = tentativeG;
                        fScore[neighborId] = tentativeG + Heuristic(neighborLane, Graph.GetLane(endLaneId)!);
                        openSet.Add(neighborId);
                    }
                }
            }
            // 未找到路径
            return null;
        }

        /// <summary>
        /// 计算车道的通行代价。基础代价 = 车道长度，再根据标签调整：
        ///   - 拥有 PreferenceTags -> 乘以折扣（降低代价）；
        ///   - 拥有 PenaltyTags -> 乘以惩罚（增加代价）。
        /// </summary>
        public float LaneCost(ZoneLane lane)
        {
            float cost = lane.Length;
            if (lane.LaneType == ZoneLaneType.Restricted)
                return float.MaxValue;
            // 偏好标签折扣
            if (PreferenceTags != ZoneTag.None && lane.HasAnyTag(PreferenceTags))
                cost *= PreferenceDiscount;
            // 惩罚标签倍率
            if (PenaltyTags != ZoneTag.None && lane.HasAnyTag(PenaltyTags))
                cost *= PenaltyMultiplier;
            return cost;
        }

        /// <summary>
        /// A* 启发函数：估算从当前车道到目标车道的距离。
        /// 使用两车道终点之间的直线距离（XZ 平面），满足可容许性。
        /// </summary>
        public float Heuristic(ZoneLane a, ZoneLane b)
        {
            float dx = a.End.X - b.End.X;
            float dz = a.End.Z - b.End.Z;
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>从 cameFrom 字典重建路径（从终点回溯到起点，再反转）。</summary>
        private ZonePath ReconstructPath(Dictionary<int, int> cameFrom, int current)
        {
            var path = new List<int> { current };
            while (cameFrom.TryGetValue(current, out int prev))
            {
                path.Add(prev);
                current = prev;
            }
            path.Reverse();
            return new ZonePath(path, Graph);
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // ECS 组件（ZoneGraphComponent）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 区域图组件。挂载到需要在 ZoneGraph 中导航的实体上，记录实体当前所在的
    /// 车道 ID、沿车道的参数 t（0=起点, 1=终点）以及期望切换到的目标车道 ID。
    /// 对齐 UE5 中 AI 代理与 ZoneGraph 的运行时绑定关系。
    /// 注意：引擎约束要求组件为 struct : IComponent。
    /// </summary>
    public struct ZoneGraphComponent : IComponent
    {
        /// <summary>当前所在车道 ID（0 表示尚未绑定到任何车道）。</summary>
        public int CurrentLaneId;
        /// <summary>沿当前车道的参数 t（0=起点, 1=终点），由 ZoneGraphSystem 每帧更新。</summary>
        public float LaneT;
        /// <summary>期望切换到的目标车道 ID（0 表示无目标，由行为树/上层逻辑设置）。</summary>
        public int DesiredLaneId;

        /// <summary>是否已绑定到某条车道。</summary>
        public bool HasLane => CurrentLaneId > 0;
        /// <summary>是否有期望的目标车道。</summary>
        public bool HasDesiredLane => DesiredLaneId > 0;
    }

    // ──────────────────────────────────────────────────────────────────────
    // ECS 系统（ZoneGraphSystem）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 区域图系统。实现 ECS 的 ISystem 接口，对齐 UE5 中 ZoneGraph 运行时管理职责：
    ///   1. 每帧更新所有 ZoneGraphComponent 实体：根据 TransformComponent.Position
    ///      查找最近车道，更新 CurrentLaneId / LaneT；
    ///   2. 当实体跨出当前车道范围时，自动切换到下游最近的车道；
    ///   3. 可选：将高层 ZoneGraph 路径下采样到底层 NavMesh 精细寻路。
    /// 该系统拥有一个 ZoneGraphData 实例与一个 ZonePathfinder 实例。
    /// </summary>
    public sealed class ZoneGraphSystem : ISystem
    {
        /// <summary>系统名称。</summary>
        public string Name => "ZoneGraphSystem";

        /// <summary>该系统拥有并维护的区域图数据。</summary>
        public ZoneGraphData Graph { get; private set; }

        /// <summary>区域寻路器（基于 Graph 构建）。</summary>
        public ZonePathfinder Pathfinder { get; private set; }

        /// <summary>绑定的导航系统（用于将 ZoneGraph 路径下采样到 NavMesh）。</summary>
        public NavigationSystem? BoundNavigation { get; private set; }

        /// <summary>全局活跃实例（供行为树/游戏逻辑访问，与 SmartObjectsSystem 的模式一致）。</summary>
        public static ZoneGraphSystem? Active { get; private set; }

        /// <summary>每帧更新时重绑车道的距离阈值（超出此距离才重新查找最近车道）。</summary>
        public float RebindDistance = 4f;

        /// <summary>构造系统，创建空区域图与寻路器并设为 Active。</summary>
        public ZoneGraphSystem() : this(hashCellSize: 16f) { }

        /// <summary>构造系统并指定空间哈希格子大小。</summary>
        public ZoneGraphSystem(float hashCellSize)
        {
            Graph = new ZoneGraphData(hashCellSize);
            Pathfinder = new ZonePathfinder(Graph);
            Active = this;
        }

        /// <summary>设置区域图数据（替换内部图，并重建寻路器）。</summary>
        public void SetGraph(ZoneGraphData graph)
        {
            Graph = graph;
            Pathfinder = new ZonePathfinder(graph);
        }

        /// <summary>绑定导航系统（启用 ZoneGraph -> NavMesh 路径下采样）。</summary>
        public void BindNavigation(NavigationSystem navigation) => BoundNavigation = navigation;

        /// <summary>
        /// 将一个 ECS 实体注册为 ZoneGraph 代理：挂载 ZoneGraphComponent 并初始化。
        /// </summary>
        public void RegisterAgent(EntityComponentSystem ecs, int entityId)
        {
            if (!ecs.HasEntity(entityId)) return;
            if (ecs.HasComponent<ZoneGraphComponent>(entityId)) return;

            ecs.AddComponent(entityId, new ZoneGraphComponent
            {
                CurrentLaneId = 0,
                LaneT = 0f,
                DesiredLaneId = 0,
            });
        }

        /// <summary>
        /// 为实体规划到目标位置的高层路径（通过最近车道 A* 寻路）。
        /// 返回 ZonePath，调用方可进一步通过 BuildNavMeshWaypoints 下采样到 NavMesh。
        /// </summary>
        public ZonePath? FindPathToPosition(EntityComponentSystem ecs, int entityId, Vector3 targetPos)
        {
            if (!ecs.HasEntity(entityId) || !ecs.HasComponent<ZoneGraphComponent>(entityId))
                return null;

            var tf = ecs.GetComponent<TransformComponent>(entityId);
            var comp = ecs.GetComponent<ZoneGraphComponent>(entityId);

            var startLane = Graph.FindNearestLane(tf.Position);
            var endLane = Graph.FindNearestLane(targetPos);
            if (startLane == null || endLane == null) return null;

            return Pathfinder.FindPath(startLane.LaneId, endLane.LaneId);
        }

        /// <summary>
        /// 将高层 ZonePath 下采样为底层 NavMesh 路径点列表。
        /// 在每段车道上取若干采样点，通过 NavigationSystem.FindPath 连接成精细路径。
        /// 这体现了 ZoneGraph（高层语义道路网络）与 NavMesh（底层精细网格）的互补关系。
        /// </summary>
        public List<Vector3>? BuildNavMeshWaypoints(ZonePath path, Vector3 currentPos, int samplesPerLane = 3)
        {
            if (BoundNavigation == null || !path.IsValid) return null;

            var waypoints = new List<Vector3>();
            Vector3 cursor = currentPos;

            foreach (int laneId in path.LaneIds)
            {
                var lane = Graph.GetLane(laneId);
                if (lane == null) continue;

                // 在车道上均匀采样若干点，通过 NavMesh 逐段寻路连接
                for (int i = 1; i <= samplesPerLane; i++)
                {
                    float t = (float)i / samplesPerLane;
                    Vector3 sample = lane.GetPositionAtT(t);
                    var subPath = BoundNavigation.FindPath(cursor, sample);
                    if (subPath != null && subPath.Count > 0)
                    {
                        // 跳过子路径的第一个点（与 cursor 重合）
                        for (int j = 1; j < subPath.Count; j++)
                            waypoints.Add(subPath[j]);
                    }
                    cursor = sample;
                }
            }
            return waypoints;
        }

        /// <summary>
        /// 每帧更新：遍历所有 ZoneGraphComponent 实体，根据其 TransformComponent.Position
        /// 更新当前所在车道与沿车道的参数 t。
        /// 对齐 UE5 中 ZoneGraph 代理位置同步逻辑。
        /// </summary>
        public void Update(float dt, EntityComponentSystem ecs)
        {
            var agents = ecs.QueryWithComponent<ZoneGraphComponent>();
            foreach (int eid in agents)
            {
                if (!ecs.HasComponent<TransformComponent>(eid)) continue;

                var tf = ecs.GetComponent<TransformComponent>(eid);
                var comp = ecs.GetComponent<ZoneGraphComponent>(eid);

                // 尝试在当前车道上更新 t 值（快速路径：实体仍在当前车道附近）
                bool updated = false;
                if (comp.HasLane)
                {
                    var currentLane = Graph.GetLane(comp.CurrentLaneId);
                    if (currentLane != null)
                    {
                        Vector3 closest = currentLane.GetClosestPoint(tf.Position);
                        float distToLane = Vector3.Distance(tf.Position, closest);

                        // 若实体仍在当前车道附近（距离小于重绑阈值），仅更新 t
                        if (distToLane < RebindDistance)
                        {
                            float newT = currentLane.GetTAtClosestPoint(tf.Position);
                            comp.LaneT = newT;
                            updated = true;

                            // 若 t 接近 1（到达车道终点），尝试切换到下游车道
                            if (newT > 0.9f && currentLane.ConnectedLanes.Count > 0)
                            {
                                int bestNext = FindBestNextLane(currentLane, tf.Position);
                                if (bestNext > 0)
                                {
                                    comp.CurrentLaneId = bestNext;
                                    var nextLane = Graph.GetLane(bestNext);
                                    if (nextLane != null)
                                        comp.LaneT = nextLane.GetTAtClosestPoint(tf.Position);
                                }
                            }
                        }
                    }
                }

                // 快速路径失败：重新查找最近车道（慢速路径）
                if (!updated)
                {
                    var nearest = Graph.FindNearestLane(tf.Position);
                    if (nearest != null)
                    {
                        comp.CurrentLaneId = nearest.LaneId;
                        comp.LaneT = nearest.GetTAtClosestPoint(tf.Position);
                    }
                    else
                    {
                        comp.CurrentLaneId = 0;
                        comp.LaneT = 0f;
                    }
                }

                // 写回组件（struct 按值传递，必须 SetComponent）
                ecs.SetComponent(eid, comp);
            }
        }

        /// <summary>
        /// 在当前车道的下游连接中找到最合适的下一条车道。
        /// 选择标准：下游车道中距离实体当前位置最近的车道。
        /// </summary>
        private int FindBestNextLane(ZoneLane currentLane, Vector3 pos)
        {
            int bestId = 0;
            float bestDistSq = float.MaxValue;
            foreach (int connId in currentLane.ConnectedLanes)
            {
                var conn = Graph.GetLane(connId);
                if (conn == null) continue;
                Vector3 closest = conn.GetClosestPoint(pos);
                float distSq = Vector3.DistanceSquared(pos, closest);
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    bestId = connId;
                }
            }
            return bestId;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 车道跟随行为（ZoneLaneFollowing）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 车道跟随行为。对齐 UE5 中沿 ZoneGraph 车道移动的 Steering 行为。
    /// 代理沿当前车道前进方向移动，在车道上向前看一段距离（LookAheadDistance）
    /// 取目标位置，输出移动方向。当到达车道终点时，自动切换到下游车道继续跟随。
    /// </summary>
    public class ZoneLaneFollowing
    {
        /// <summary>引用的区域图数据。</summary>
        public ZoneGraphData Graph;
        /// <summary>前视距离：在当前车道上向前看多远取目标位置。</summary>
        public float LookAheadDistance = 3f;
        /// <summary>到达车道终点的阈值（t > 此值视为到达终点）。</summary>
        public float EndThreshold = 0.95f;

        /// <summary>构造车道跟随行为并绑定区域图。</summary>
        public ZoneLaneFollowing(ZoneGraphData graph)
        {
            Graph = graph;
        }

        /// <summary>
        /// 更新车道跟随行为，计算移动方向。
        /// </summary>
        /// <param name="entityId">实体 ID。</param>
        /// <param name="dt">帧时间。</param>
        /// <param name="ecs">ECS 系统。</param>
        /// <param name="desiredMoveDir">输出的期望移动方向（归一化）。</param>
        /// <returns>true=仍在跟随中，false=已到达路径终点（无下游车道可切换）。</returns>
        public bool Update(int entityId, float dt, EntityComponentSystem ecs, out Vector3 desiredMoveDir)
        {
            desiredMoveDir = Vector3.Zero;

            if (!ecs.HasEntity(entityId)) return false;
            if (!ecs.HasComponent<ZoneGraphComponent>(entityId)) return false;
            if (!ecs.HasComponent<TransformComponent>(entityId)) return false;

            var tf = ecs.GetComponent<TransformComponent>(entityId);
            var comp = ecs.GetComponent<ZoneGraphComponent>(entityId);

            if (!comp.HasLane)
            {
                // 未绑定车道，尝试找最近车道
                var nearest = Graph.FindNearestLane(tf.Position);
                if (nearest == null) return false;
                comp.CurrentLaneId = nearest.LaneId;
                comp.LaneT = nearest.GetTAtClosestPoint(tf.Position);
                ecs.SetComponent(entityId, comp);
            }

            var lane = Graph.GetLane(comp.CurrentLaneId);
            if (lane == null) return false;

            // 检查是否到达车道终点
            if (comp.LaneT >= EndThreshold)
            {
                // 尝试切换到下游车道
                int nextLaneId = SelectNextLane(lane, tf.Position);
                if (nextLaneId <= 0)
                    return false; // 无下游车道，跟随结束

                comp.CurrentLaneId = nextLaneId;
                var nextLane = Graph.GetLane(nextLaneId);
                if (nextLane != null)
                    comp.LaneT = nextLane.GetTAtClosestPoint(tf.Position);
                ecs.SetComponent(entityId, comp);

                lane = nextLane;
                if (lane == null) return false;
            }

            // 在当前车道上向前看 LookAheadDistance 取目标位置
            float currentDist = comp.LaneT * lane.Length;
            float targetDist = currentDist + LookAheadDistance;
            Vector3 targetPos = lane.GetPositionAtDistance(targetDist);

            // 计算移动方向（XZ 平面）
            Vector3 dir = targetPos - tf.Position;
            dir.Y = 0f; // 仅在水平面移动
            if (dir.LengthSquared() > 1e-6f)
                desiredMoveDir = Vector3.Normalize(dir);

            return true;
        }

        /// <summary>
        /// 选择下一条车道。优先选择 DesiredLaneId（若设置且在连接列表中），
        /// 否则选择下游连接中距离当前位置最近的车道。
        /// </summary>
        private int SelectNextLane(ZoneLane currentLane, Vector3 pos)
        {
            if (currentLane.ConnectedLanes.Count == 0) return 0;

            int bestId = 0;
            float bestDistSq = float.MaxValue;
            foreach (int connId in currentLane.ConnectedLanes)
            {
                var conn = Graph.GetLane(connId);
                if (conn == null) continue;
                if (conn.LaneType == ZoneLaneType.Restricted) continue;
                Vector3 closest = conn.GetClosestPoint(pos);
                float distSq = Vector3.DistanceSquared(pos, closest);
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    bestId = connId;
                }
            }
            return bestId;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 交通流系统（ZoneTrafficSystem）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 交通流系统。对齐 UE5 ZoneGraph 交通流模拟的简化版本。
    /// 功能：
    ///   1. 车道占用：记录每条车道上当前有哪些实体，用于碰撞避免与拥堵检测；
    ///   2. 信号灯：对带 Junction 标签的车道实现简化的时间周期红绿信号，
    ///      绿灯允许通行，红灯禁止通行，黄灯过渡。
    /// 该系统本身不直接移动实体，而是为上层逻辑（行为树/转向行为）提供
    /// 交通状态查询接口。
    /// </summary>
    public class ZoneTrafficSystem
    {
        /// <summary>引用的区域图数据。</summary>
        public ZoneGraphData Graph;

        /// <summary>车道占用表：LaneId -> 该车道上的实体 ID 列表。</summary>
        public Dictionary<int, List<int>> LaneOccupancy;

        /// <summary>信号灯状态表：LaneId -> 信号灯运行时状态。</summary>
        public Dictionary<int, TrafficLightRuntime> TrafficLights;

        /// <summary>绿灯持续时间（秒）。</summary>
        public float GreenDuration = 15f;
        /// <summary>黄灯持续时间（秒）。</summary>
        public float YellowDuration = 3f;
        /// <summary>红灯持续时间（秒）。</summary>
        public float RedDuration = 12f;

        /// <summary>每条车道最大占用数（超过则视为拥堵）。</summary>
        public int CongestionThreshold = 8;

        /// <summary>构造交通流系统并绑定区域图。</summary>
        public ZoneTrafficSystem(ZoneGraphData graph)
        {
            Graph = graph;
            LaneOccupancy = new Dictionary<int, List<int>>();
            TrafficLights = new Dictionary<int, TrafficLightRuntime>();
            InitializeTrafficLights();
        }

        /// <summary>
        /// 初始化信号灯：为所有带 Junction 标签的车道创建信号灯，
        /// 初始相位为绿灯，并为不同车道设置随机相位偏移以模拟交错放行。
        /// </summary>
        public void InitializeTrafficLights()
        {
            TrafficLights.Clear();
            var rng = new Random(42);
            foreach (var kvp in Graph.Lanes)
            {
                var lane = kvp.Value;
                if (lane.HasTag(ZoneTag.Junction))
                {
                    var light = new TrafficLightRuntime
                    {
                        Phase = TrafficLightPhase.Green,
                        Timer = 0f,
                        // 随机偏移初始相位，避免所有路口同时变灯
                        PhaseOffset = (float)(rng.NextDouble() * (GreenDuration + YellowDuration + RedDuration)),
                    };
                    // 应用偏移：快进到偏移时间点
                    AdvanceLight(light, light.PhaseOffset);
                    TrafficLights[lane.LaneId] = light;
                }
            }
        }

        /// <summary>
        /// 注册实体到车道占用表。当实体进入某条车道时调用。
        /// </summary>
        public void RegisterEntity(int entityId, int laneId)
        {
            if (!LaneOccupancy.TryGetValue(laneId, out var list))
            {
                list = new List<int>();
                LaneOccupancy[laneId] = list;
            }
            if (!list.Contains(entityId))
                list.Add(entityId);
        }

        /// <summary>
        /// 从车道占用表中移除实体。当实体离开车道或被销毁时调用。
        /// </summary>
        public void UnregisterEntity(int entityId, int laneId)
        {
            if (LaneOccupancy.TryGetValue(laneId, out var list))
            {
                list.Remove(entityId);
                if (list.Count == 0)
                    LaneOccupancy.Remove(laneId);
            }
        }

        /// <summary>从所有车道占用表中移除实体（实体被销毁时调用）。</summary>
        public void UnregisterEntityFromAll(int entityId)
        {
            var emptyKeys = new List<int>();
            foreach (var kvp in LaneOccupancy)
            {
                kvp.Value.Remove(entityId);
                if (kvp.Value.Count == 0)
                    emptyKeys.Add(kvp.Key);
            }
            foreach (int key in emptyKeys)
                LaneOccupancy.Remove(key);
        }

        /// <summary>获取指定车道上的实体数量。</summary>
        public int GetLaneOccupancyCount(int laneId)
        {
            return LaneOccupancy.TryGetValue(laneId, out var list) ? list.Count : 0;
        }

        /// <summary>获取指定车道上的实体 ID 列表（只读副本）。</summary>
        public List<int> GetLaneOccupants(int laneId)
        {
            return LaneOccupancy.TryGetValue(laneId, out var list)
                ? new List<int>(list)
                : new List<int>();
        }

        /// <summary>判断指定车道是否拥堵（占用数超过阈值）。</summary>
        public bool IsLaneCongested(int laneId)
        {
            return GetLaneOccupancyCount(laneId) >= CongestionThreshold;
        }

        /// <summary>获取指定车道的信号灯相位（无信号灯返回 Green）。</summary>
        public TrafficLightPhase GetLightPhase(int laneId)
        {
            return TrafficLights.TryGetValue(laneId, out var light)
                ? light.Phase
                : TrafficLightPhase.Green;
        }

        /// <summary>判断指定车道是否允许通行（绿灯或无信号灯）。</summary>
        public bool CanPass(int laneId)
        {
            return GetLightPhase(laneId) == TrafficLightPhase.Green;
        }

        /// <summary>
        /// 每帧更新：推进所有信号灯的计时器，并根据 ZoneGraphComponent
        /// 更新车道占用表。
        /// </summary>
        public void Update(float dt, EntityComponentSystem ecs)
        {
            // 1) 更新信号灯
            UpdateLights(dt);

            // 2) 根据实体的 ZoneGraphComponent 更新车道占用
            var agents = ecs.QueryWithComponent<ZoneGraphComponent>();
            var currentOccupancy = new Dictionary<int, List<int>>();

            foreach (int eid in agents)
            {
                var comp = ecs.GetComponent<ZoneGraphComponent>(eid);
                if (!comp.HasLane) continue;
                if (!ecs.HasEntity(eid)) continue;

                if (!currentOccupancy.TryGetValue(comp.CurrentLaneId, out var list))
                {
                    list = new List<int>();
                    currentOccupancy[comp.CurrentLaneId] = list;
                }
                list.Add(eid);
            }

            // 替换占用表（简化策略：每帧重建）
            LaneOccupancy = currentOccupancy;
        }

        /// <summary>推进所有信号灯的计时器并执行相位切换。</summary>
        public void UpdateLights(float dt)
        {
            foreach (var kvp in TrafficLights)
            {
                AdvanceLight(kvp.Value, dt);
            }
        }

        /// <summary>推进单个信号灯的计时器并执行相位切换。</summary>
        private void AdvanceLight(TrafficLightRuntime light, float dt)
        {
            light.Timer += dt;
            switch (light.Phase)
            {
                case TrafficLightPhase.Green:
                    if (light.Timer >= GreenDuration)
                    {
                        light.Phase = TrafficLightPhase.Yellow;
                        light.Timer = 0f;
                    }
                    break;
                case TrafficLightPhase.Yellow:
                    if (light.Timer >= YellowDuration)
                    {
                        light.Phase = TrafficLightPhase.Red;
                        light.Timer = 0f;
                    }
                    break;
                case TrafficLightPhase.Red:
                    if (light.Timer >= RedDuration)
                    {
                        light.Phase = TrafficLightPhase.Green;
                        light.Timer = 0f;
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// 信号灯运行时状态。记录当前相位与计时器。
    /// </summary>
    public class TrafficLightRuntime
    {
        /// <summary>当前信号灯相位。</summary>
        public TrafficLightPhase Phase;
        /// <summary>当前相位已持续时间（秒）。</summary>
        public float Timer;
        /// <summary>初始相位偏移（秒，用于交错放行）。</summary>
        public float PhaseOffset;
    }

    // ──────────────────────────────────────────────────────────────────────
    // 区域图构建器（ZoneGraphBuilder）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 区域图构建器。对齐 UE5 中 ZoneGraph 的构建流程。
    /// 提供流式 API：通过 AddLaneFromSpline / AddShapeFromPolygon 添加车道与形状，
    /// 通过 ConnectLanes 建立车道间的下游连接，最后调用 Build() 生成 ZoneGraphData
    /// （内部自动构建空间哈希索引）。
    /// </summary>
    public class ZoneGraphBuilder
    {
        /// <summary>已添加的车道列表（按添加顺序）。</summary>
        public List<ZoneLane> Lanes;
        /// <summary>已添加的形状列表（按添加顺序）。</summary>
        public List<ZoneShape> Shapes;
        /// <summary>下一个车道 ID（自增）。</summary>
        public int NextLaneId = 1;
        /// <summary>下一个形状 ID（自增）。</summary>
        public int NextShapeId = 1;
        /// <summary>空间哈希格子大小（传入 Build 的 ZoneGraphData）。</summary>
        public float HashCellSize = 16f;

        /// <summary>构造空构建器。</summary>
        public ZoneGraphBuilder()
        {
            Lanes = new List<ZoneLane>();
            Shapes = new List<ZoneShape>();
        }

        /// <summary>构造构建器并指定空间哈希格子大小。</summary>
        public ZoneGraphBuilder(float hashCellSize) : this()
        {
            HashCellSize = hashCellSize;
        }

        /// <summary>
        /// 从样条点列表生成一条车道并添加到构建器。
        /// 对齐 UE5 中从样条生成 ZoneLane 的流程。
        /// </summary>
        /// <param name="splinePoints">样条控制点（至少 2 个点，首尾为起终点）。</param>
        /// <param name="width">车道宽度。</param>
        /// <param name="laneType">车道类型。</param>
        /// <param name="tags">区域标签。</param>
        /// <returns>分配的车道 ID（LaneId）。</returns>
        public int AddLaneFromSpline(List<Vector3> splinePoints, float width,
                                     ZoneLaneType laneType, ZoneTag tags)
        {
            if (splinePoints == null || splinePoints.Count < 2)
                throw new ArgumentException("样条点列表至少需要 2 个点", nameof(splinePoints));

            int laneId = NextLaneId++;
            var lane = new ZoneLane(laneId, new List<Vector3>(splinePoints), width, laneType, tags);
            Lanes.Add(lane);
            return laneId;
        }

        /// <summary>
        /// 从多边形顶点列表生成一个形状并添加到构建器。
        /// 对齐 UE5 中从多边形生成 ZoneShape 的流程。
        /// </summary>
        /// <param name="poly">多边形顶点列表（按顺序连接形成闭合边界）。</param>
        /// <param name="tags">区域标签。</param>
        /// <returns>分配的形状 ID（ShapeId）。</returns>
        public int AddShapeFromPolygon(List<Vector3> poly, ZoneTag tags)
        {
            if (poly == null || poly.Count < 3)
                throw new ArgumentException("多边形至少需要 3 个顶点", nameof(poly));

            int shapeId = NextShapeId++;
            var shape = new ZoneShape(shapeId, new List<Vector3>(poly), tags);
            Shapes.Add(shape);
            return shapeId;
        }

        /// <summary>
        /// 建立两条车道之间的下游连接（fromId -> toId）。
        /// 对齐 UE5 ZoneGraph 中车道连接关系的构建。
        /// 连接是单向的：from 的 ConnectedLanes 中添加 to。
        /// </summary>
        /// <param name="fromId">上游车道 ID。</param>
        /// <param name="toId">下游车道 ID。</param>
        public void ConnectLanes(int fromId, int toId)
        {
            var fromLane = Lanes.Find(l => l.LaneId == fromId);
            if (fromLane == null) return;
            if (!fromLane.ConnectedLanes.Contains(toId))
                fromLane.ConnectedLanes.Add(toId);
        }

        /// <summary>
        /// 建立两条车道之间的双向连接（fromId <-> toId）。
        /// 适用于双向通行场景。
        /// </summary>
        public void ConnectLanesBidirectional(int laneA, int laneB)
        {
            ConnectLanes(laneA, laneB);
            ConnectLanes(laneB, laneA);
        }

        /// <summary>设置车道限速。</summary>
        public void SetSpeedLimit(int laneId, float speedLimit)
        {
            var lane = Lanes.Find(l => l.LaneId == laneId);
            if (lane != null) lane.SpeedLimit = speedLimit;
        }

        /// <summary>设置车道通行方向。</summary>
        public void SetDirection(int laneId, ZoneLaneDirection direction)
        {
            var lane = Lanes.Find(l => l.LaneId == laneId);
            if (lane != null) lane.Direction = direction;
        }

        /// <summary>设置车道所属区域 ID。</summary>
        public void SetZoneId(int laneId, int zoneId)
        {
            var lane = Lanes.Find(l => l.LaneId == laneId);
            if (lane != null) lane.ZoneId = zoneId;
        }

        /// <summary>
        /// 构建最终的 ZoneGraphData。将所有车道与形状写入数据容器，
        /// 并调用 BuildSpatialHash() 构建空间索引。
        /// </summary>
        /// <returns>构建完成的 ZoneGraphData。</returns>
        public ZoneGraphData Build()
        {
            var data = new ZoneGraphData(HashCellSize);
            foreach (var lane in Lanes)
                data.AddLane(lane);
            foreach (var shape in Shapes)
                data.AddShape(shape);
            data.BuildSpatialHash();
            return data;
        }

        /// <summary>清空构建器中的所有数据（重置为初始状态）。</summary>
        public void Clear()
        {
            Lanes.Clear();
            Shapes.Clear();
            NextLaneId = 1;
            NextShapeId = 1;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 调试（ZoneGraphDebugger）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 区域图调试器。对齐 UE5 编辑器中 ZoneGraph 的调试可视化功能。
    /// 提供接口返回所有车道线段与形状边界，供编辑器视口绘制。
    /// </summary>
    public class ZoneGraphDebugger
    {
        /// <summary>引用的区域图数据。</summary>
        public ZoneGraphData Graph;

        /// <summary>构造调试器并绑定区域图。</summary>
        public ZoneGraphDebugger(ZoneGraphData graph)
        {
            Graph = graph;
        }

        /// <summary>
        /// 获取所有车道的线段列表，用于编辑器绘制车道中心线。
        /// 每条车道返回其样条点构成的连续线段。
        /// </summary>
        /// <returns>线段列表，每项包含起点、终点、车道标签。</returns>
        public List<(Vector3 start, Vector3 end, ZoneTag tags)> GetLaneSegments()
        {
            var result = new List<(Vector3, Vector3, ZoneTag)>();
            foreach (var kvp in Graph.Lanes)
            {
                var lane = kvp.Value;
                for (int i = 0; i < lane.SplinePoints.Count - 1; i++)
                {
                    result.Add((lane.SplinePoints[i], lane.SplinePoints[i + 1], lane.Tags));
                }
            }
            return result;
        }

        /// <summary>
        /// 获取所有车道的边界线段列表（左右两侧边界），用于编辑器绘制车道宽度。
        /// 每条车道返回其左右两侧的边界线段。
        /// </summary>
        /// <returns>边界线段列表，每项包含起点、终点、车道标签。</returns>
        public List<(Vector3 start, Vector3 end, ZoneTag tags)> GetLaneBoundaries()
        {
            var result = new List<(Vector3, Vector3, ZoneTag)>();
            foreach (var kvp in Graph.Lanes)
            {
                var lane = kvp.Value;
                float halfWidth = lane.Width * 0.5f;
                for (int i = 0; i < lane.SplinePoints.Count - 1; i++)
                {
                    Vector3 a = lane.SplinePoints[i];
                    Vector3 b = lane.SplinePoints[i + 1];
                    Vector3 dir = b - a;
                    if (dir.LengthSquared() < 1e-8f) continue;
                    dir = Vector3.Normalize(dir);
                    Vector3 right = Vector3.Cross(dir, Vector3.UnitY);
                    if (right.LengthSquared() > 1e-8f)
                        right = Vector3.Normalize(right);

                    // 左边界段
                    result.Add((a + right * halfWidth, b + right * halfWidth, lane.Tags));
                    // 右边界段
                    result.Add((a - right * halfWidth, b - right * halfWidth, lane.Tags));
                }
            }
            return result;
        }

        /// <summary>
        /// 获取所有形状的多边形边界，用于编辑器绘制区域轮廓。
        /// </summary>
        /// <returns>多边形顶点列表的列表，每个内层列表是一个形状的闭合边界顶点。</returns>
        public List<List<Vector3>> GetShapeOutlines()
        {
            var result = new List<List<Vector3>>();
            foreach (var kvp in Graph.Shapes)
            {
                var shape = kvp.Value;
                var outline = new List<Vector3>(shape.Points);
                // 闭合：添加第一个点到末尾
                if (outline.Count > 0)
                    outline.Add(outline[0]);
                result.Add(outline);
            }
            return result;
        }

        /// <summary>
        /// 获取所有车道连接关系，用于编辑器绘制车道间的连接箭头。
        /// </summary>
        /// <returns>连接列表，每项包含起点（上游车道终点）、终点（下游车道起点）。</returns>
        public List<(Vector3 from, Vector3 to)> GetLaneConnections()
        {
            var result = new List<(Vector3, Vector3)>();
            foreach (var kvp in Graph.Lanes)
            {
                var lane = kvp.Value;
                foreach (int connId in lane.ConnectedLanes)
                {
                    var conn = Graph.GetLane(connId);
                    if (conn == null) continue;
                    result.Add((lane.End, conn.Start));
                }
            }
            return result;
        }

        /// <summary>
        /// 获取区域图的调试信息字符串（统计摘要），用于编辑器信息面板显示。
        /// </summary>
        public string GetDebugInfo()
        {
            int totalConnections = 0;
            int junctionCount = 0;
            int sidewalkCount = 0;
            int roadCount = 0;
            float totalLength = 0f;

            foreach (var kvp in Graph.Lanes)
            {
                var lane = kvp.Value;
                totalConnections += lane.ConnectedLanes.Count;
                totalLength += lane.Length;
                if (lane.HasTag(ZoneTag.Junction)) junctionCount++;
                if (lane.HasTag(ZoneTag.Sidewalk)) sidewalkCount++;
                if (lane.HasTag(ZoneTag.Road)) roadCount++;
            }

            return $"ZoneGraph 调试信息:\n" +
                   $"  车道总数: {Graph.LaneCount}\n" +
                   $"  形状总数: {Graph.ShapeCount}\n" +
                   $"  车道连接总数: {totalConnections}\n" +
                   $"  车道总长度: {totalLength:F1}m\n" +
                   $"  路口车道数: {junctionCount}\n" +
                   $"  人行道车道数: {sidewalkCount}\n" +
                   $"  道路车道数: {roadCount}\n" +
                   $"  空间哈希格子数: {Graph.SpatialHash.LaneCells.Count}";
        }

        /// <summary>
        /// 获取指定车道类型的统计信息。
        /// </summary>
        public Dictionary<ZoneLaneType, int> GetLaneTypeStats()
        {
            var stats = new Dictionary<ZoneLaneType, int>();
            foreach (var kvp in Graph.Lanes)
            {
                var type = kvp.Value.LaneType;
                if (!stats.ContainsKey(type))
                    stats[type] = 0;
                stats[type]++;
            }
            return stats;
        }
    }
}
