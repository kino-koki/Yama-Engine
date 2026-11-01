// ============================================================================
//  YamaEngine.AI.PathSmoother.cs
//  yama引擎 -- 路径平滑器
//  对齐 Detour's dtPathCorridor / Recast funnel algorithm
// ----------------------------------------------------------------------------
//  实现两套主流的路径平滑算法：
//    1. Funnel Algorithm (String Pulling) - 沿多边形走廊拉绳子，最短路径
//    2. Iterative Smoothing - 迭代式平滑，B样条/Catmull-Rom/简单窗口平均
//
//  funnel algorithm 这块得仔细推导 不然很容易写出 bug，特别是左右通道切换。
//  参考：http://digestingduck.blogspot.com/2010/03/simple-stupid-funnel-algorithm.html
// ============================================================================
using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.AI
{
    // ========================================================================
    //  一、Funnel 算法
    // ========================================================================

    /// <summary>
    /// 漏斗算法(拉绳子)。
    /// 输入是 navmesh 多边形序列(走廊)和左右边界，
    /// 输出是最短的贴墙路径。
    /// </summary>
    public sealed class FunnelSmoother
    {
        /// <summary>漏斗顶点(左/右通道边)。</summary>
        public struct FunnelPortal
        {
            public Vector3 Left;
            public Vector3 Right;
            public int PolyIndex; // 该 portal 属于哪个多边形
        }

        private readonly List<FunnelPortal> _portals = new();
        private readonly List<Vector3> _smoothPath = new();

        /// <summary>是否启用顶点夹紧(防止穿出走廊)。</summary>
        public bool ClampToCorridor { get; set; } = true;
        /// <summary>路径点最小间距(米)，太近的点合并掉。</summary>
        public float MinWaypointDist { get; set; } = 0.5f;
        /// <summary>是否迭代优化(多次拉绳子收敛)。</summary>
        public bool IterativeOptimize { get; set; } = true;
        public int MaxIterations { get; set; } = 3;

        public FunnelSmoother() { }

        /// <summary>添加一个 portal(两个顶点定义通道)。</summary>
        public void AddPortal(Vector3 left, Vector3 right, int polyIdx = -1)
        {
            _portals.Add(new FunnelPortal { Left = left, Right = right, PolyIndex = polyIdx });
        }

        /// <summary>批量添加 portal。</summary>
        public void AddPortals(IEnumerable<FunnelPortal> portals)
        {
            _portals.AddRange(portals);
        }

        /// <summary>清空。</summary>
        public void Clear()
        {
            _portals.Clear();
            _smoothPath.Clear();
        }

        /// <summary>
        /// 执行漏斗算法。返回平滑后的路径点(世界坐标)。
        /// 这是 "Simple Stupid Funnel Algorithm" 的实现。
        /// </summary>
        public List<Vector3> Smooth(Vector3 start, Vector3 end)
        {
            _smoothPath.Clear();
            if (_portals.Count == 0)
            {
                _smoothPath.Add(start);
                _smoothPath.Add(end);
                return _smoothPath;
            }

            // 迭代优化(可选)
            var result = RunFunnel(start, end);
            if (IterativeOptimize)
            {
                for (int i = 1; i < MaxIterations; i++)
                {
                    var refined = RunFunnel(start, end);
                    if (refined.Count == result.Count) break;
                    result = refined;
                }
            }

            // 合并过近的点
            if (MinWaypointDist > 0)
                result = MergeClosePoints(result, MinWaypointDist);

            _smoothPath.AddRange(result);
            return result;
        }

        /// <summary>核心：一次漏斗算法。</summary>
        private List<Vector3> RunFunnel(Vector3 start, Vector3 end)
        {
            var path = new List<Vector3> { start };

            // 漏斗的三个关键点：apex(尖端), left, right
            Vector3 apex = start;
            Vector3 left = _portals[0].Left;
            // 右边界点（与 left 对称声明）
            Vector3 right = _portals[0].Right;
            int apexIdx = 0, leftIdx = 0, rightIdx = 0;

            // 注意：XZ 平面做判断，Y 取插值
            for (int i = 1; i < _portals.Count; i++)
            {
                var portal = _portals[i];
                var newLeft = portal.Left;
                var newRight = portal.Right;

                // 更新右边界
                if (TriArea2D(apex, right, newRight) <= 0)
                {
                    if (apex == right || TriArea2D(apex, left, newRight) > 0)
                    {
                        // 右边界往内收
                        right = newRight;
                        rightIdx = i;
                    }
                    else
                    {
                        // 漏斗收紧到 left，apex 跳到 left
                        apex = left;
                        apexIdx = leftIdx;
                        path.Add(apex);
                        // 重置
                        left = apex; right = apex;
                        leftIdx = apexIdx; rightIdx = apexIdx;
                        i = apexIdx; // 从 apex 后面重新开始
                        continue;
                    }
                }

                // 更新左边界
                if (TriArea2D(apex, left, newLeft) >= 0)
                {
                    if (apex == left || TriArea2D(apex, right, newLeft) < 0)
                    {
                        left = newLeft;
                        leftIdx = i;
                    }
                    else
                    {
                        apex = right;
                        apexIdx = rightIdx;
                        path.Add(apex);
                        left = apex; right = apex;
                        leftIdx = apexIdx; rightIdx = apexIdx;
                        i = apexIdx;
                        continue;
                    }
                }
            }

            path.Add(end);
            return path;
        }

        /// <summary>
        /// 计算 a->b->c 在 XZ 平面的有向三角形面积(2倍)。
        /// 正值=CCW(逆时针)，负值=CW(顺时针)，0=共线。
        /// 这是 funnel 的核心判断函数。
        /// </summary>
        private static float TriArea2D(Vector3 a, Vector3 b, Vector3 c)
        {
            float abx = b.X - a.X, abz = b.Z - a.Z;
            float acx = c.X - a.X, acz = c.Z - a.Z;
            return acx * abz - abx * acz;
        }

        /// <summary>合并距离过近的相邻点。</summary>
        private static List<Vector3> MergeClosePoints(List<Vector3> path, float minDist)
        {
            if (path.Count < 3) return path;
            var result = new List<Vector3> { path[0] };
            float minDistSq = minDist * minDist;
            for (int i = 1; i < path.Count; i++)
            {
                if (Vector3.DistanceSquared(result[result.Count - 1], path[i]) >= minDistSq)
                    result.Add(path[i]);
            }
            return result;
        }
    }

    // ========================================================================
    //  二、走廊路径管理
    // ========================================================================

    /// <summary>
    /// 路径走廊。维护一条 navmesh 路径 + 当前位置 + 漏斗平滑结果。
    /// 跟 Detour 的 dtPathCorridor 对齐。
    /// </summary>
    public sealed class PathCorridor
    {
        private readonly List<int> _path = new();       // 多边形 id 序列
        private readonly List<Vector3> _waypoints = new(); // 漏斗平滑后的世界坐标
        private Vector3 _currentPos;
        private int _currentCorner; // 当前目标 waypoint 索引
        private readonly FunnelSmoother _funnel = new();

        public float CornerRadius { get; set; } = 0.3f;
        public bool AutoRepath { get; set; } = true;

        public Vector3 Position => _currentPos;
        public bool HasPath => _path.Count > 0;
        public int PathPolyCount => _path.Count;
        public int WaypointCount => _waypoints.Count;
        public bool ReachedEnd => _currentCorner >= _waypoints.Count;

        public PathCorridor() { }

        /// <summary>重置走廊。</summary>
        public void Reset(Vector3 pos)
        {
            _currentPos = pos;
            _path.Clear();
            _waypoints.Clear();
            _currentCorner = 0;
        }

        /// <summary>设置新路径(多边形序列)。</summary>
        public void SetPath(List<int> polys, GeneratedNavMesh nav, Vector3 start, Vector3 end)
        {
            _path.Clear();
            _path.AddRange(polys);
            _funnel.Clear();

            // 从多边形序列提取 portal
            for (int i = 0; i < polys.Count - 1; i++)
            {
                var portal = ExtractPortal(polys[i], polys[i + 1], nav);
                _funnel.AddPortal(portal.left, portal.right, i);
            }

            // 漏斗算法
            _waypoints.Clear();
            _waypoints.AddRange(_funnel.Smooth(start, end));
            _currentCorner = 0;
        }

        /// <summary>提取两个相邻多边形之间的共享边(作为 portal)。</summary>
        private (Vector3 left, Vector3 right) ExtractPortal(int polyA, int polyB, GeneratedNavMesh nav)
        {
            var a = nav.Polygons[polyA];
            var b = nav.Polygons[polyB];
            // 找共享顶点对
            for (int i = 0; i < a.VertexIndices.Length; i++)
            {
                int va = a.VertexIndices[i];
                int va2 = a.VertexIndices[(i + 1) % a.VertexIndices.Length];
                for (int j = 0; j < b.VertexIndices.Length; j++)
                {
                    int vb = b.VertexIndices[j];
                    int vb2 = b.VertexIndices[(j + 1) % b.VertexIndices.Length];
                    if (va == vb2 && va2 == vb)
                    {
                        var p1 = nav.Vertices[va];
                        var p2 = nav.Vertices[va2];
                        // 保证 left/right 是相对移动方向的左右
                        if (Cross2D(p1 - p2, _currentPos - p2) > 0)
                            return (p2, p1);
                        return (p1, p2);
                    }
                }
            }
            // 找不到共享边，用质心连线
            var ca = nav.PolyCenter(polyA);
            var cb = nav.PolyCenter(polyB);
            return (ca, cb);
        }

        private static float Cross2D(Vector3 a, Vector3 b) => a.X * b.Z - a.Z * b.X;

        /// <summary>移动代理。返回是否到达终点。</summary>
        public bool Move(Vector3 target, float maxMove)
        {
            if (_currentCorner >= _waypoints.Count) return true;
            var wp = _waypoints[_currentCorner];
            var toWp = wp - _currentPos;
            toWp.Y = 0;
            float dist = toWp.Length();
            if (dist < CornerRadius)
            {
                _currentCorner++;
                if (_currentCorner >= _waypoints.Count) return true;
                wp = _waypoints[_currentCorner];
                toWp = wp - _currentPos;
                toWp.Y = 0;
                dist = toWp.Length();
            }
            if (dist <= maxMove)
            {
                _currentPos = wp;
            }
            else
            {
                _currentPos += toWp / dist * maxMove;
            }
            return false;
        }

        /// <summary>获取当前目标点。</summary>
        public Vector3 GetCorner() =>
            _currentCorner < _waypoints.Count ? _waypoints[_currentCorner] : _currentPos;

        /// <summary>获取剩余路径。</summary>
        public List<Vector3> GetRemainingPath()
        {
            var result = new List<Vector3> { _currentPos };
            for (int i = _currentCorner; i < _waypoints.Count; i++)
                result.Add(_waypoints[i]);
            return result;
        }

        /// <summary>修正位置(防止穿墙)。</summary>
        public void AdjustPosition(Vector3 newPos) => _currentPos = newPos;
    }

    // ========================================================================
    //  三、迭代式平滑器
    // ========================================================================

    /// <summary>
    /// 迭代式路径平滑。比 funnel 简单但效果不如。
    /// 适合不能用 navmesh corridor 的场景(比如自由空间路径)。
    /// </summary>
    public sealed class IterativePathSmoother
    {
        /// <summary>平滑方法枚举。</summary>
        public enum SmoothMethod
        {
            /// <summary>简单窗口平均，最快。</summary>
            MovingAverage,
            /// <summary>Catmull-Rom 样条，平滑度好。</summary>
            CatmullRom,
            /// <summary>三次 B 样条，C2 连续。</summary>
            BSpline,
            /// <summary>梯度下降(带碰撞约束)。</summary>
            GradientDescent
        }

        public SmoothMethod Method { get; set; } = SmoothMethod.CatmullRom;
        /// <summary>平滑强度 0~1。</summary>
        public float Strength { get; set; } = 0.5f;
        /// <summary>迭代次数。</summary>
        public int Iterations { get; set; } = 3;
        /// <summary>窗口大小(moving average 用)。</summary>
        public int WindowSize { get; set; } = 3;
        /// <summary>碰撞检查回调，返回 false 表示该位置不可走。</summary>
        public Func<Vector3, bool>? CollisionCheck { get; set; }
        /// <summary>采样密度(每米多少个点)。</summary>
        public float SampleDensity { get; set; } = 2f;

        public IterativePathSmoother() { }

        /// <summary>执行平滑。</summary>
        public List<Vector3> Smooth(List<Vector3> path)
        {
            if (path.Count < 3) return new List<Vector3>(path);

            // 先按密度重采样
            var sampled = Resample(path, SampleDensity);

            for (int iter = 0; iter < Iterations; iter++)
            {
                sampled = Method switch
                {
                    SmoothMethod.MovingAverage => SmoothMovingAverage(sampled),
                    SmoothMethod.CatmullRom => SmoothCatmullRom(sampled),
                    SmoothMethod.BSpline => SmoothBSpline(sampled),
                    SmoothMethod.GradientDescent => SmoothGradientDescent(sampled),
                    _ => sampled
                };

                // 碰撞约束
                if (CollisionCheck != null)
                    sampled = EnforceCollision(sampled);
            }

            return sampled;
        }

        // --------------------------------------------------------------------
        //  重采样
        // --------------------------------------------------------------------

        private List<Vector3> Resample(List<Vector3> path, float density)
        {
            if (density <= 0) return new List<Vector3>(path);
            var result = new List<Vector3> { path[0] };
            float stepLen = 1f / density;
            float acc = 0;
            for (int i = 1; i < path.Count; i++)
            {
                var seg = path[i] - path[i - 1];
                float segLen = seg.Length();
                while (acc + segLen >= stepLen)
                {
                    float t = (stepLen - acc) / segLen;
                    result.Add(path[i - 1] + seg * t);
                    seg = path[i] - result[result.Count - 1];
                    segLen = seg.Length();
                    acc = 0;
                }
                acc += segLen;
            }
            if (Vector3.Distance(result[result.Count - 1], path[path.Count - 1]) > 0.01f)
                result.Add(path[path.Count - 1]);
            return result;
        }

        // --------------------------------------------------------------------
        //  Moving Average
        // --------------------------------------------------------------------

        private List<Vector3> SmoothMovingAverage(List<Vector3> path)
        {
            if (path.Count < 3) return path;
            var result = new List<Vector3>(path.Count);
            int half = WindowSize / 2;
            for (int i = 0; i < path.Count; i++)
            {
                if (i == 0 || i == path.Count - 1)
                {
                    result.Add(path[i]);
                    continue;
                }
                Vector3 sum = Vector3.Zero;
                int count = 0;
                for (int j = -half; j <= half; j++)
                {
                    int idx = i + j;
                    if (idx < 0 || idx >= path.Count) continue;
                    sum += path[idx];
                    count++;
                }
                var smoothed = sum / count;
                result.Add(Vector3.Lerp(path[i], smoothed, Strength));
            }
            return result;
        }

        // --------------------------------------------------------------------
        //  Catmull-Rom
        // --------------------------------------------------------------------

        private List<Vector3> SmoothCatmullRom(List<Vector3> path)
        {
            if (path.Count < 3) return path;
            var result = new List<Vector3>(path.Count);
            for (int i = 0; i < path.Count; i++)
            {
                if (i == 0 || i == path.Count - 1)
                {
                    result.Add(path[i]);
                    continue;
                }
                var p0 = path[i - 1];
                var p1 = path[i];
                var p2 = path[i + 1];
                var p3 = i + 2 < path.Count ? path[i + 2] : p2;

                // Catmull-Rom 在 t=0.5 处的值
                var smoothed = 0.5f * (
                    2f * p1 +
                    -p0 + p2 +
                    2f * p0 - 5f * p1 + 4f * p2 - p3 +
                    -p0 + 3f * p1 - 3f * p2 + p3
                ) * 0.25f;

                // 上面公式有点问题，直接用标准 Catmull-Rom 基函数
                // P(t) = 0.5 * ((2*P1) + (-P0 + P2)*t + (2*P0 - 5*P1 + 4*P2 - P3)*t^2 + (-P0 + 3*P1 - 3*P2 + P3)*t^3)
                // 在 t=0 时就是 P1，所以我们想要的是用相邻点的"影响"来调整 P1
                float t = 0.5f;
                var target = 0.5f * (
                    (2 * p1) +
                    (-p0 + p2) * t +
                    (2 * p0 - 5 * p1 + 4 * p2 - p3) * t * t +
                    (-p0 + 3 * p1 - 3 * p2 + p3) * t * t * t
                );
                result.Add(Vector3.Lerp(path[i], target, Strength));
            }
            return result;
        }

        // --------------------------------------------------------------------
        //  B-Spline
        // --------------------------------------------------------------------

        private List<Vector3> SmoothBSpline(List<Vector3> path)
        {
            if (path.Count < 4) return path;
            var result = new List<Vector3>(path.Count);
            // 三次均匀 B 样条基函数
            // B0 = (1-t)^3 / 6
            // B1 = (3t^3 - 6t^2 + 4) / 6
            // B2 = (-3t^3 + 3t^2 + 3t + 1) / 6
            // B3 = t^3 / 6
            // t=0.5 时简化
            for (int i = 0; i < path.Count; i++)
            {
                if (i == 0 || i == path.Count - 1)
                {
                    result.Add(path[i]);
                    continue;
                }
                var p0 = i - 1 >= 0 ? path[i - 1] : path[i];
                var p1 = path[i];
                var p2 = i + 1 < path.Count ? path[i + 1] : path[i];
                var p3 = i + 2 < path.Count ? path[i + 2] : p2;

                // 在 t=0.5 求值
                float t = 0.5f;
                float b0 = MathF.Pow(1 - t, 3) / 6f;
                float b1 = (3 * t * t * t - 6 * t * t + 4) / 6f;
                float b2 = (-3 * t * t * t + 3 * t * t + 3 * t + 1) / 6f;
                float b3 = t * t * t / 6f;
                var target = p0 * b0 + p1 * b1 + p2 * b2 + p3 * b3;
                result.Add(Vector3.Lerp(path[i], target, Strength));
            }
            return result;
        }

        // --------------------------------------------------------------------
        //  Gradient Descent (带约束的能量最小化)
        // --------------------------------------------------------------------

        private List<Vector3> SmoothGradientDescent(List<Vector3> path)
        {
            // 最小化 E = sum |p_i - p_{i-1}|^2 (路径长度) + lambda * sum |p_i - 0.5*(p_{i-1}+p_{i+1})|^2 (平滑)
            // 梯度下降
            var result = new List<Vector3>(path);
            float lambda = Strength * 0.5f;
            for (int iter = 0; iter < 3; iter++)
            {
                var next = new List<Vector3>(result.Count);
                for (int i = 0; i < result.Count; i++)
                {
                    if (i == 0 || i == result.Count - 1)
                    {
                        next.Add(result[i]);
                        continue;
                    }
                    var p = result[i];
                    var prev = result[i - 1];
                    var succ = result[i + 1];
                    // 平滑项梯度：p - 0.5*(prev+succ)
                    var grad = lambda * (p - (prev + succ) * 0.5f);
                    next.Add(p - grad);
                }
                result = next;
            }
            return result;
        }

        // --------------------------------------------------------------------
        //  碰撞约束
        // --------------------------------------------------------------------

        private List<Vector3> EnforceCollision(List<Vector3> path)
        {
            if (CollisionCheck == null) return path;
            var result = new List<Vector3>(path.Count);
            foreach (var p in path)
            {
                if (CollisionCheck(p))
                    result.Add(p);
                else
                {
                    // 找附近可走点
                    var nearest = FindNearestValid(p);
                    result.Add(nearest);
                }
            }
            return result;
        }

        private Vector3 FindNearestValid(Vector3 p)
        {
            // 螺旋搜索
            for (float r = 0.1f; r < 5f; r += 0.1f)
            {
                for (int a = 0; a < 8; a++)
                {
                    float ang = a * MathF.PI / 4f;
                    var test = p + new Vector3(MathF.Cos(ang) * r, 0, MathF.Sin(ang) * r);
                    if (CollisionCheck!(test)) return test;
                }
            }
            return p;
        }
    }

    // ========================================================================
    //  四、路径平滑器统一入口
    // ========================================================================

    /// <summary>
    /// 路径平滑系统。根据路径类型自动选择算法。
    /// </summary>

    // ========================================================================
    //  五、路径优化工具
    // ========================================================================

    /// <summary>路径优化相关工具方法。</summary>
    public static class PathOptimizationUtils
    {
        /// <summary>检测路径中是否有锐角(需要减速的拐弯)。</summary>
        public static List<int> FindSharpCorners(List<Vector3> path, float thresholdDeg = 90f)
        {
            var corners = new List<int>();
            float cosThreshold = MathF.Cos(thresholdDeg * MathF.PI / 180f);
            for (int i = 1; i < path.Count - 1; i++)
            {
                var d1 = path[i] - path[i - 1];
                var d2 = path[i + 1] - path[i];
                d1 = Vector3.Normalize(d1);
                d2 = Vector3.Normalize(d2);
                if (Vector3.Dot(d1, d2) < cosThreshold)
                    corners.Add(i);
            }
            return corners;
        }

        /// <summary>计算路径的"曲折度"(实际长度/直线距离)。1=直线，越大越曲折。</summary>
        public static float Tortuosity(List<Vector3> path)
        {
            if (path.Count < 2) return 1f;
            float straight = Vector3.Distance(path[0], path[path.Count - 1]);
            if (straight < 1e-4f) return float.MaxValue;
            float total = 0;
            for (int i = 1; i < path.Count; i++)
                total += Vector3.Distance(path[i - 1], path[i]);
            return total / straight;
        }

        /// <summary>把路径切分成若干段(按距离)。</summary>
        public static List<List<Vector3>> SplitByDistance(List<Vector3> path, float segmentLen)
        {
            var result = new List<List<Vector3>>();
            if (path.Count < 2) return result;
            var current = new List<Vector3> { path[0] };
            float acc = 0;
            for (int i = 1; i < path.Count; i++)
            {
                var seg = path[i] - path[i - 1];
                float segLen = seg.Length();
                while (acc + segLen >= segmentLen)
                {
                    float t = (segmentLen - acc) / segLen;
                    var split = path[i - 1] + seg * t;
                    current.Add(split);
                    result.Add(current);
                    current = new List<Vector3> { split };
                    seg = path[i] - split;
                    segLen = seg.Length();
                    acc = 0;
                }
                acc += segLen;
                current.Add(path[i]);
            }
            if (current.Count > 1) result.Add(current);
            return result;
        }

        /// <summary>路径偏移(给跟随者用，避免完全重叠)。</summary>
        public static List<Vector3> OffsetPath(List<Vector3> path, float offset, Vector3 up)
        {
            if (path.Count < 2) return new List<Vector3>(path);
            var result = new List<Vector3>(path.Count);
            for (int i = 0; i < path.Count; i++)
            {
                Vector3 dir;
                if (i == 0) dir = path[1] - path[0];
                else if (i == path.Count - 1) dir = path[path.Count - 1] - path[path.Count - 2];
                else dir = path[i + 1] - path[i - 1];
                dir = Vector3.Normalize(dir);
                var side = Vector3.Cross(up, dir);
                result.Add(path[i] + side * offset);
            }
            return result;
        }

        /// <summary>路径反向。</summary>
        public static List<Vector3> Reverse(List<Vector3> path)
        {
            var result = new List<Vector3>(path);
            result.Reverse();
            return result;
        }
    }
}
