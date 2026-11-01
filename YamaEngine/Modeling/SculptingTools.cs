// =====================================================================================
//  YamaEngine.Modeling.SculptingTools
//  数字雕刻工具系统 (Digital Sculpting Tools)
// -------------------------------------------------------------------------------------
//  类似 ZBrush / Blender Sculpt Mode 的雕刻系统:
//    - BrushType: Draw / Flatten / Smooth / Pinch / Inflate / Grab / SnakeHook / Layer / Mask
//    - SculptBrush: 笔刷参数 (半径 / 强度 / 衰减 / 对称 / 自动平滑)
//    - BrushFalloff: 衰减曲线 (Smooth: cos^2 / Constant / Linear / Root: sqrt)
//    - SculptMesh: 雕刻网格 (高分辨率, 多分辨率, 动态拓扑)
//    - SculptBVH: BVH 加速笔刷查询 (球-AABB 相交)
//    - SculptOperations: 各笔刷类型的实际变形算法
//
//  类型命名与已有 SculptTool.cs 中的 SculptBrushType / SculptFalloff / SculptBrushSettings
//  区别开, 使用 BrushType / BrushFalloff / SculptBrush 等名称。
// =====================================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace YamaEngine.Modeling
{
    // =================================================================================
    //  枚举: BrushType / BrushFalloff / SymmetryMode
    // =================================================================================

    /// <summary>
    /// 雕刻笔刷类型 —— 对齐 ZBrush / Blender Sculpt Mode 的笔刷分类。
    /// </summary>
    public enum BrushType
    {
        /// <summary>绘制: 沿法线方向推拉顶点。</summary>
        Draw,
        /// <summary>平坦化: 将顶点投影到平均平面。</summary>
        Flatten,
        /// <summary>平滑: Laplacian 平滑 (邻居平均)。</summary>
        Smooth,
        /// <summary>捏合: 向笔刷中心收缩顶点。</summary>
        Pinch,
        /// <summary>膨胀: 沿法线膨胀。</summary>
        Inflate,
        /// <summary>抓取: 抓取顶点自由拖拽。</summary>
        Grab,
        /// <summary>蛇钩: 拉伸顶点形成突起。</summary>
        SnakeHook,
        /// <summary>层: 固定高度层 (不叠加)。</summary>
        Layer,
        /// <summary>遮罩: 设置遮罩区域。</summary>
        Mask
    }

    /// <summary>
    /// 笔刷衰减曲线类型。
    /// </summary>
    public enum BrushFalloff
    {
        /// <summary>平滑衰减: f(x) = cos(pi*x/2)^2。</summary>
        Smooth,
        /// <summary>常数衰减: f(x) = 1.0 (半径内全强度)。</summary>
        Constant,
        /// <summary>线性衰减: f(x) = 1 - x。</summary>
        Linear,
        /// <summary>根号衰减: f(x) = sqrt(1 - x^2)。</summary>
        Root
    }

    /// <summary>对称雕刻模式 (可组合为标志位)。</summary>
    [Flags]
    public enum SymmetryMode
    {
        None = 0,
        X = 1,
        Y = 2,
        Z = 4
    }

    // =================================================================================
    //  BrushFalloffCurve — 衰减曲线计算
    // =================================================================================

    /// <summary>
    /// 笔刷衰减曲线计算 —— 根据归一化距离 [0,1] 返回衰减权重 [0,1]。
    /// x=0 表示笔刷中心, x=1 表示笔刷边缘。
    /// </summary>
    public static class BrushFalloffCurve
    {
        /// <summary>
        /// 计算衰减权重。
        /// x: 归一化距离 [0,1], type: 衰减类型。
        /// </summary>
        public static float Evaluate(float x, BrushFalloff type)
        {
            x = Math.Clamp(x, 0f, 1f);
            return type switch
            {
                BrushFalloff.Smooth => SmoothFalloff(x),
                BrushFalloff.Constant => 1f,
                BrushFalloff.Linear => 1f - x,
                BrushFalloff.Root => RootFalloff(x),
                _ => 1f - x
            };
        }

        /// <summary>平滑衰减: cos(pi*x/2)^2。在中心为 1, 边缘平滑过渡到 0。</summary>
        private static float SmoothFalloff(float x)
        {
            float c = MathF.Cos(MathF.PI * x * 0.5f);
            return c * c;
        }

        /// <summary>根号衰减: sqrt(1 - x^2)。在中心为 1, 边缘快速下降。</summary>
        private static float RootFalloff(float x)
        {
            float val = 1f - x * x;
            return val > 0f ? MathF.Sqrt(val) : 0f;
        }
    }

    // =================================================================================
    //  SculptBrush — 笔刷参数
    // =================================================================================

    /// <summary>
    /// 雕刻笔刷参数 —— 控制笔刷的行为和效果。
    /// </summary>
    public class SculptBrush
    {
        /// <summary>笔刷类型。</summary>
        public BrushType Type = BrushType.Draw;

        /// <summary>笔刷中心 (世界空间)。</summary>
        public Vector3 Center = Vector3.Zero;

        /// <summary>笔刷半径 (世界空间)。</summary>
        public float Radius = 0.5f;

        /// <summary>笔刷强度 (0~1)。</summary>
        public float Strength = 0.3f;

        /// <summary>衰减曲线类型。</summary>
        public BrushFalloff Falloff = BrushFalloff.Smooth;

        /// <summary>对称模式 (X/Y/Z 轴镜像)。</summary>
        public SymmetryMode Symmetry = SymmetryMode.None;

        /// <summary>自动平滑: 每笔自动应用平滑 (0=关闭, 1=完全平滑)。</summary>
        public float AutoSmooth = 0f;

        /// <summary>拓扑梳理: 梳理顶点使其更均匀 (0~1)。</summary>
        public float TopologyRake = 0f;

        /// <summary>拖拽方向 (用于 Grab / SnakeHook)。</summary>
        public Vector3 DragDirection = Vector3.Zero;

        /// <summary>层高度 (用于 Layer 笔刷)。</summary>
        public float LayerHeight = 0.1f;

        /// <summary>笔刷影响的法线方向 (为 Zero 时使用顶点法线)。</summary>
        public Vector3 OverrideNormal = Vector3.Zero;

        public void Validate()
        {
            if (Radius <= 0f) throw new ArgumentOutOfRangeException(nameof(Radius), "笔刷半径必须为正数。");
            if (Strength < 0f) throw new ArgumentOutOfRangeException(nameof(Strength), "笔刷强度不能为负。");
        }

        /// <summary>获取对称中心列表 (包括原始中心和镜像中心)。</summary>
        public List<Vector3> GetSymmetryCenters()
        {
            var centers = new List<Vector3> { Center };
            if (Symmetry.HasFlag(SymmetryMode.X))
                centers.Add(new Vector3(-Center.X, Center.Y, Center.Z));
            if (Symmetry.HasFlag(SymmetryMode.Y))
                centers.Add(new Vector3(Center.X, -Center.Y, Center.Z));
            if (Symmetry.HasFlag(SymmetryMode.Z))
                centers.Add(new Vector3(Center.X, Center.Y, -Center.Z));
            if (Symmetry.HasFlag(SymmetryMode.X) && Symmetry.HasFlag(SymmetryMode.Y))
                centers.Add(new Vector3(-Center.X, -Center.Y, Center.Z));
            if (Symmetry.HasFlag(SymmetryMode.X) && Symmetry.HasFlag(SymmetryMode.Z))
                centers.Add(new Vector3(-Center.X, Center.Y, -Center.Z));
            if (Symmetry.HasFlag(SymmetryMode.Y) && Symmetry.HasFlag(SymmetryMode.Z))
                centers.Add(new Vector3(Center.X, -Center.Y, -Center.Z));
            if (Symmetry.HasFlag(SymmetryMode.X) && Symmetry.HasFlag(SymmetryMode.Y) && Symmetry.HasFlag(SymmetryMode.Z))
                centers.Add(new Vector3(-Center.X, -Center.Y, -Center.Z));
            return centers;
        }

        /// <summary>获取法线方向 (使用 OverrideNormal 或回退到给定法线)。</summary>
        public Vector3 GetEffectiveNormal(Vector3 fallback)
        {
            return OverrideNormal.LengthSquared() > 1e-8f ? Vector3.Normalize(OverrideNormal) : fallback;
        }
    }

    // =================================================================================
    //  SculptBVH — BVH 加速笔刷查询
    // =================================================================================

    /// <summary>BVH 节点。</summary>
    public class SculptBVHNode
    {
        public Vector3 Min;
        public Vector3 Max;
        public SculptBVHNode Left;
        public SculptBVHNode Right;
        public List<int> VertexIndices; // 仅叶节点
        public bool IsLeaf => Left == null && Right == null;
    }

    /// <summary>
    /// BVH 加速结构 —— 用于快速查找笔刷影响范围内的顶点。
    /// 使用球-AABB 相交检测。
    /// </summary>
    public class SculptBVH
    {
        public SculptBVHNode Root;

        /// <summary>从顶点位置列表构建 BVH。</summary>
        public static SculptBVH Build(List<Vector3> positions)
        {
            var bvh = new SculptBVH();
            if (positions == null || positions.Count == 0) return bvh;

            var indices = Enumerable.Range(0, positions.Count).ToList();
            bvh.Root = BuildNode(positions, indices, 0);
            return bvh;
        }

        private static SculptBVHNode BuildNode(List<Vector3> positions, List<int> indices, int depth)
        {
            var node = new SculptBVHNode();

            // 计算 AABB
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            foreach (var i in indices)
            {
                min = Vector3.Min(min, positions[i]);
                max = Vector3.Max(max, positions[i]);
            }
            node.Min = min;
            node.Max = max;

            // 叶节点条件: 顶点数 <= 16 或深度过大
            if (indices.Count <= 16 || depth > 20)
            {
                node.VertexIndices = indices;
                return node;
            }

            // 选择最长轴分割
            var size = max - min;
            int axis = 0;
            if (size.Y > size.X) axis = 1;
            if (size.Z > size[axis]) axis = 2;

            // 按最长轴排序, 在中位数处分割
            indices.Sort((a, b) =>
            {
                float pa = axis == 0 ? positions[a].X : (axis == 1 ? positions[a].Y : positions[a].Z);
                float pb = axis == 0 ? positions[b].X : (axis == 1 ? positions[b].Y : positions[b].Z);
                return pa.CompareTo(pb);
            });

            int mid = indices.Count / 2;
            var leftIndices = indices.GetRange(0, mid);
            var rightIndices = indices.GetRange(mid, indices.Count - mid);

            node.Left = BuildNode(positions, leftIndices, depth + 1);
            node.Right = BuildNode(positions, rightIndices, depth + 1);
            return node;
        }

        /// <summary>
        /// 查询球体内的所有顶点索引。
        /// 使用球-AABB 相交检测加速。
        /// </summary>
        public List<int> QuerySphere(Vector3 center, float radius)
        {
            var result = new List<int>();
            if (Root == null) return result;
            QuerySphereNode(Root, center, radius, result);
            return result;
        }

        private void QuerySphereNode(SculptBVHNode node, Vector3 center, float radius, List<int> result)
        {
            if (!SphereAABBIntersect(center, radius, node.Min, node.Max))
                return;

            if (node.IsLeaf)
            {
                if (node.VertexIndices != null)
                    result.AddRange(node.VertexIndices);
            }
            else
            {
                if (node.Left != null) QuerySphereNode(node.Left, center, radius, result);
                if (node.Right != null) QuerySphereNode(node.Right, center, radius, result);
            }
        }

        /// <summary>
        /// 球-AABB 相交检测。
        /// 找到 AABB 上离球心最近的点, 检查距离是否小于半径。
        /// </summary>
        public static bool SphereAABBIntersect(Vector3 center, float radius, Vector3 min, Vector3 max)
        {
            float sqDist = 0f;
            if (center.X < min.X) sqDist += (min.X - center.X) * (min.X - center.X);
            else if (center.X > max.X) sqDist += (center.X - max.X) * (center.X - max.X);
            if (center.Y < min.Y) sqDist += (min.Y - center.Y) * (min.Y - center.Y);
            else if (center.Y > max.Y) sqDist += (center.Y - max.Y) * (center.Y - max.Y);
            if (center.Z < min.Z) sqDist += (min.Z - center.Z) * (min.Z - center.Z);
            else if (center.Z > max.Z) sqDist += (center.Z - max.Z) * (center.Z - max.Z);
            return sqDist <= radius * radius;
        }
    }

    // =================================================================================
    //  SculptMesh — 雕刻网格
    // =================================================================================

    /// <summary>
    /// 雕刻网格 —— 高分辨率网格, 支持动态拓扑和多分辨率。
    /// 顶点位移缓冲: 仅修改位移, 不破坏原始拓扑。
    /// </summary>
    public class SculptMesh
    {
        /// <summary>顶点位置 (当前)。</summary>
        public List<Vector3> Positions = new();

        /// <summary>顶点法线 (当前)。</summary>
        public List<Vector3> Normals = new();

        /// <summary>原始顶点位置 (用于位移缓冲)。</summary>
        public List<Vector3> OriginalPositions = new();

        /// <summary>顶点位移 (相对于原始位置)。</summary>
        public List<Vector3> Displacements = new();

        /// <summary>UV 坐标。</summary>
        public List<Vector2> UVs = new();

        /// <summary>面 (三角形索引)。</summary>
        public List<(int A, int B, int C)> Triangles = new();

        /// <summary>顶点遮罩 (0=完全遮罩, 1=完全影响)。</summary>
        public List<float> Masks = new();

        /// <summary>顶点邻居列表 (用于平滑等操作)。</summary>
        public List<HashSet<int>> Adjacency = new();

        /// <summary>BVH 加速结构。</summary>
        public SculptBVH BVH;

        /// <summary>动态拓扑控制器。</summary>
        public DynamicTopology DynamicTopo;

        /// <summary>多分辨率控制器。</summary>
        public Multiresolution Multires;

        /// <summary>BVH 是否需要重建。</summary>
        public bool BVHDirty = true;

        // ──────────────────────────────────────────────────────────────
        //  初始化
        // ──────────────────────────────────────────────────────────────

        public SculptMesh()
        {
            DynamicTopo = new DynamicTopology(this);
            Multires = new Multiresolution(this);
        }

        /// <summary>从 EditableMesh 初始化雕刻网格。</summary>
        public static SculptMesh FromEditableMesh(EditableMesh mesh)
        {
            var sm = new SculptMesh();
            sm.Positions = mesh.Vertices.ToList();
            sm.OriginalPositions = mesh.Vertices.ToList();
            sm.Displacements = Enumerable.Repeat(Vector3.Zero, mesh.Vertices.Count).ToList();
            sm.Normals = (mesh.Normals.Count == mesh.Vertices.Count
                ? mesh.Normals.ToList()
                : Enumerable.Repeat(Vector3.UnitY, mesh.Vertices.Count).ToList());
            sm.UVs = (mesh.UV0.Count == mesh.Vertices.Count
                ? mesh.UV0.ToList()
                : Enumerable.Repeat(Vector2.Zero, mesh.Vertices.Count).ToList());
            sm.Masks = Enumerable.Repeat(1f, mesh.Vertices.Count).ToList();

            foreach (var f in mesh.Faces)
            {
                if (f.IsValidFor(mesh.Vertices.Count))
                    sm.Triangles.Add((f.A, f.B, f.C));
            }

            sm.BuildAdjacency();
            sm.RebuildBVH();
            return sm;
        }

        /// <summary>转换为 EditableMesh。</summary>
        public EditableMesh ToEditableMesh()
        {
            var mesh = new EditableMesh { Name = "SculptMesh" };
            mesh.Vertices.AddRange(Positions);
            mesh.Normals.AddRange(Normals);
            mesh.UV0.AddRange(UVs);
            foreach (var (a, b, c) in Triangles)
                mesh.Faces.Add(new MeshFace(a, b, c));
            return mesh;
        }

        /// <summary>构建顶点邻接表。</summary>
        public void BuildAdjacency()
        {
            Adjacency = new List<HashSet<int>>(Positions.Count);
            for (int i = 0; i < Positions.Count; i++)
                Adjacency.Add(new HashSet<int>());

            foreach (var (a, b, c) in Triangles)
            {
                Adjacency[a].Add(b); Adjacency[b].Add(a);
                Adjacency[b].Add(c); Adjacency[c].Add(b);
                Adjacency[c].Add(a); Adjacency[a].Add(c);
            }
        }

        /// <summary>重建 BVH。</summary>
        public void RebuildBVH()
        {
            BVH = SculptBVH.Build(Positions);
            BVHDirty = false;
        }

        /// <summary>重新计算法线 (面法线加权平均)。</summary>
        public void RecalculateNormals()
        {
            for (int i = 0; i < Normals.Count; i++)
                Normals[i] = Vector3.Zero;

            foreach (var (a, b, c) in Triangles)
            {
                var normal = Vector3.Cross(Positions[b] - Positions[a], Positions[c] - Positions[a]);
                if (normal.LengthSquared() > 1e-12f)
                    normal = Vector3.Normalize(normal);
                Normals[a] += normal;
                Normals[b] += normal;
                Normals[c] += normal;
            }

            for (int i = 0; i < Normals.Count; i++)
                Normals[i] = Normals[i].LengthSquared() > 1e-12f
                    ? Vector3.Normalize(Normals[i])
                    : Vector3.UnitY;
        }

        /// <summary>更新位移缓冲 (位移 = 当前位置 - 原始位置)。</summary>
        public void UpdateDisplacements()
        {
            for (int i = 0; i < Positions.Count; i++)
            {
                if (i < OriginalPositions.Count && i < Displacements.Count)
                    Displacements[i] = Positions[i] - OriginalPositions[i];
            }
        }

        /// <summary>获取安全法线。</summary>
        public Vector3 SafeNormal(int index)
        {
            if (index >= 0 && index < Normals.Count && Normals[index].LengthSquared() > 1e-8f)
                return Vector3.Normalize(Normals[index]);
            return Vector3.UnitY;
        }

        /// <summary>获取遮罩权重。</summary>
        public float GetMask(int index)
        {
            return index >= 0 && index < Masks.Count ? Masks[index] : 1f;
        }
    }

    // =================================================================================
    //  DynamicTopology — 动态拓扑
    // =================================================================================

    /// <summary>
    /// 动态拓扑 —— 笔刷接触时自动细分/合并网格。
    /// 当三角形边长 > maxEdgeLength 时细分, < minEdgeLength 时合并。
    /// </summary>
    public class DynamicTopology
    {
        private readonly SculptMesh _mesh;

        /// <summary>最大边长 (超过此长度时细分)。</summary>
        public float MaxEdgeLength = 0.05f;

        /// <summary>最小边长 (低于此长度时合并, 暂未实现)。</summary>
        public float MinEdgeLength = 0.01f;

        /// <summary>是否启用动态拓扑。</summary>
        public bool Enabled = false;

        public DynamicTopology(SculptMesh mesh) { _mesh = mesh; }

        /// <summary>
        /// 在笔刷影响区域内执行动态拓扑细分。
        /// 对边长超过 MaxEdgeLength 的三角形进行边分裂。
        /// </summary>
        public void SubdivideInBrushRange(Vector3 center, float radius)
        {
            if (!Enabled) return;

            float maxLenSq = MaxEdgeLength * MaxEdgeLength;
            float radiusSq = radius * radius;

            // 找到需要细分的三角形
            var trianglesToSplit = new List<int>();
            for (int i = 0; i < _mesh.Triangles.Count; i++)
            {
                var (a, b, c) = _mesh.Triangles[i];
                // 检查三角形是否在笔刷范围内
                var centroid = (_mesh.Positions[a] + _mesh.Positions[b] + _mesh.Positions[c]) / 3f;
                if (Vector3.DistanceSquared(centroid, center) > radiusSq) continue;

                // 检查是否有边过长
                float lenAB = Vector3.DistanceSquared(_mesh.Positions[a], _mesh.Positions[b]);
                float lenBC = Vector3.DistanceSquared(_mesh.Positions[b], _mesh.Positions[c]);
                float lenCA = Vector3.DistanceSquared(_mesh.Positions[c], _mesh.Positions[a]);

                if (lenAB > maxLenSq || lenBC > maxLenSq || lenCA > maxLenSq)
                    trianglesToSplit.Add(i);
            }

            // 从后往前分裂 (避免索引偏移)
            for (int idx = trianglesToSplit.Count - 1; idx >= 0; idx--)
            {
                int triIdx = trianglesToSplit[idx];
                var (a, b, c) = _mesh.Triangles[triIdx];

                // 找到最长的边
                float lenAB = Vector3.DistanceSquared(_mesh.Positions[a], _mesh.Positions[b]);
                float lenBC = Vector3.DistanceSquared(_mesh.Positions[b], _mesh.Positions[c]);
                float lenCA = Vector3.DistanceSquared(_mesh.Positions[c], _mesh.Positions[a]);

                if (lenAB >= lenBC && lenAB >= lenCA)
                {
                    SplitEdgeInTriangle(triIdx, 0); // 边 AB
                }
                else if (lenBC >= lenCA)
                {
                    SplitEdgeInTriangle(triIdx, 1); // 边 BC
                }
                else
                {
                    SplitEdgeInTriangle(triIdx, 2); // 边 CA
                }
            }

            if (trianglesToSplit.Count > 0)
            {
                _mesh.BuildAdjacency();
                _mesh.RecalculateNormals();
                _mesh.BVHDirty = true;
            }
        }

        /// <summary>在三角形的最长边上分裂, 生成 2 个子三角形。</summary>
        private void SplitEdgeInTriangle(int triIdx, int edgeSlot)
        {
            var (a, b, c) = _mesh.Triangles[triIdx];

            int v0, v1, v2; // v0-v1 是要分裂的边
            switch (edgeSlot)
            {
                case 0: v0 = a; v1 = b; v2 = c; break;
                case 1: v0 = b; v1 = c; v2 = a; break;
                default: v0 = c; v1 = a; v2 = b; break;
            }

            // 创建中点
            int mid = _mesh.Positions.Count;
            var midPos = (_mesh.Positions[v0] + _mesh.Positions[v1]) * 0.5f;
            var midNormal = (_mesh.Normals[v0] + _mesh.Normals[v1]) * 0.5f;
            var midUV = (_mesh.UVs[v0] + _mesh.UVs[v1]) * 0.5f;
            _mesh.Positions.Add(midPos);
            _mesh.OriginalPositions.Add(midPos);
            _mesh.Displacements.Add(Vector3.Zero);
            _mesh.Normals.Add(midNormal.LengthSquared() > 1e-8f ? Vector3.Normalize(midNormal) : Vector3.UnitY);
            _mesh.UVs.Add(midUV);
            _mesh.Masks.Add((_mesh.GetMask(v0) + _mesh.GetMask(v1)) * 0.5f);

            // 替换原三角形为 2 个子三角形
            _mesh.Triangles[triIdx] = (v0, mid, v2);
            _mesh.Triangles.Add((mid, v1, v2));
        }
    }

    // =================================================================================
    //  Multiresolution — 多分辨率
    // =================================================================================

    /// <summary>
    /// 多分辨率系统 —— 基础网格 + 多级细分 + 位移数据。
    /// 可以在不同细分级别之间切换, 每级保存独立的位移数据。
    /// </summary>
    public class Multiresolution
    {
        private readonly SculptMesh _mesh;

        /// <summary>各级别的原始位置 (细分后的基础位置, 不含位移)。</summary>
        public List<List<Vector3>> BasePositions = new();

        /// <summary>各级别的位移数据。</summary>
        public List<List<Vector3>> Displacements = new();

        /// <summary>各级别的面数据。</summary>
        public List<List<(int, int, int)>> LevelTriangles = new();

        /// <summary>当前活动级别 (0 = 基础网格)。</summary>
        public int CurrentLevel = 0;

        /// <summary>最高级别。</summary>
        public int MaxLevel => BasePositions.Count - 1;

        public Multiresolution(SculptMesh mesh) { _mesh = mesh; }

        /// <summary>初始化级别 0 (当前网格作为基础)。</summary>
        public void InitBaseLevel()
        {
            BasePositions.Clear();
            Displacements.Clear();
            LevelTriangles.Clear();

            BasePositions.Add(_mesh.Positions.ToList());
            Displacements.Add(Enumerable.Repeat(Vector3.Zero, _mesh.Positions.Count).ToList());
            LevelTriangles.Add(_mesh.Triangles.ToList());
            CurrentLevel = 0;
        }

        /// <summary>提升一级细分 (线性细分: 每个三角形分成 4 个)。</summary>
        public void SubdivideLevel()
        {
            int newLevel = BasePositions.Count;
            var basePos = BasePositions[CurrentLevel];
            var baseTris = LevelTriangles[CurrentLevel];

            var newPos = basePos.ToList();
            var newTris = new List<(int, int, int)>();
            var edgeMidpoints = new Dictionary<(int, int), int>();

            foreach (var (a, b, c) in baseTris)
            {
                int midAB = GetOrCreateMidpoint(newPos, edgeMidpoints, a, b);
                int midBC = GetOrCreateMidpoint(newPos, edgeMidpoints, b, c);
                int midCA = GetOrCreateMidpoint(newPos, edgeMidpoints, c, a);

                newTris.Add((a, midAB, midCA));
                newTris.Add((midAB, b, midBC));
                newTris.Add((midCA, midBC, c));
                newTris.Add((midAB, midBC, midCA));
            }

            BasePositions.Add(newPos);
            Displacements.Add(Enumerable.Repeat(Vector3.Zero, newPos.Count).ToList());
            LevelTriangles.Add(newTris);
            CurrentLevel = newLevel;

            ApplyLevelToMesh();
        }

        /// <summary>降低一级 (返回上一级)。</summary>
        public void ReduceLevel()
        {
            if (CurrentLevel <= 0) return;

            // 保存当前级别的位移
            for (int i = 0; i < _mesh.Positions.Count && i < Displacements[CurrentLevel].Count; i++)
                Displacements[CurrentLevel][i] = _mesh.Positions[i] - BasePositions[CurrentLevel][i];

            CurrentLevel--;
            ApplyLevelToMesh();
        }

        /// <summary>切换到指定级别。</summary>
        public void SetLevel(int level)
        {
            if (level < 0 || level > MaxLevel) return;

            // 保存当前级别的位移
            if (CurrentLevel >= 0 && CurrentLevel < Displacements.Count)
            {
                for (int i = 0; i < _mesh.Positions.Count && i < Displacements[CurrentLevel].Count; i++)
                    Displacements[CurrentLevel][i] = _mesh.Positions[i] - BasePositions[CurrentLevel][i];
            }

            CurrentLevel = level;
            ApplyLevelToMesh();
        }

        /// <summary>将当前级别的数据应用到雕刻网格。</summary>
        public void ApplyLevelToMesh()
        {
            if (CurrentLevel < 0 || CurrentLevel >= BasePositions.Count) return;

            var basePos = BasePositions[CurrentLevel];
            var disps = Displacements[CurrentLevel];
            var tris = LevelTriangles[CurrentLevel];

            _mesh.Positions = new List<Vector3>(basePos.Count);
            _mesh.OriginalPositions = new List<Vector3>(basePos.Count);
            _mesh.Displacements = new List<Vector3>(basePos.Count);

            for (int i = 0; i < basePos.Count; i++)
            {
                Vector3 disp = i < disps.Count ? disps[i] : Vector3.Zero;
                _mesh.Positions.Add(basePos[i] + disp);
                _mesh.OriginalPositions.Add(basePos[i]);
                _mesh.Displacements.Add(disp);
            }

            _mesh.Normals = Enumerable.Repeat(Vector3.UnitY, basePos.Count).ToList();
            _mesh.UVs = Enumerable.Repeat(Vector2.Zero, basePos.Count).ToList();
            _mesh.Masks = Enumerable.Repeat(1f, basePos.Count).ToList();
            _mesh.Triangles = tris.ToList();

            _mesh.BuildAdjacency();
            _mesh.RecalculateNormals();
            _mesh.BVHDirty = true;
        }

        private static int GetOrCreateMidpoint(List<Vector3> positions, Dictionary<(int, int), int> cache, int a, int b)
        {
            var key = a < b ? (a, b) : (b, a);
            if (cache.TryGetValue(key, out int cached)) return cached;

            var mid = (positions[a] + positions[b]) * 0.5f;
            int idx = positions.Count;
            positions.Add(mid);
            cache[key] = idx;
            return idx;
        }
    }

    // =================================================================================
    //  SculptOperations — 雕刻操作
    // =================================================================================

    /// <summary>
    /// 雕刻操作集合 —— 实现各种笔刷类型的顶点变形算法。
    /// 所有操作直接修改 SculptMesh 的顶点位置。
    /// </summary>
    public static class SculptOperations
    {
        /// <summary>
        /// 应用笔刷到雕刻网格。
        /// 根据笔刷类型分发到具体操作。
        /// </summary>
        public static SculptStrokeResult Apply(SculptMesh mesh, SculptBrush brush)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            brush.Validate();
            if (mesh.Positions.Count == 0) return SculptStrokeResult.Empty;

            // BVH 重建 (如果需要)
            if (mesh.BVHDirty || mesh.BVH == null)
                mesh.RebuildBVH();

            var result = new SculptStrokeResult();

            // 对每个对称中心应用笔刷
            foreach (var center in brush.GetSymmetryCenters())
            {
                var brushCenter = brush.Center == center ? brush.Center : center;
                var singleResult = ApplySingle(mesh, brush, brushCenter);
                result.AffectedVertices += singleResult.AffectedVertices;
                result.TotalDisplacement += singleResult.TotalDisplacement;
                result.MaxDisplacement = Math.Max(result.MaxDisplacement, singleResult.MaxDisplacement);
            }

            // 自动平滑
            if (brush.AutoSmooth > 0f && brush.Type != BrushType.Smooth && brush.Type != BrushType.Mask)
            {
                var smoothBrush = new SculptBrush
                {
                    Type = BrushType.Smooth,
                    Center = brush.Center,
                    Radius = brush.Radius,
                    Strength = brush.AutoSmooth,
                    Falloff = brush.Falloff,
                    Symmetry = brush.Symmetry
                };
                Apply(mesh, smoothBrush);
            }

            // 重新计算法线
            mesh.RecalculateNormals();
            mesh.UpdateDisplacements();
            mesh.BVHDirty = true;

            return result;
        }

        private static SculptStrokeResult ApplySingle(SculptMesh mesh, SculptBrush brush, Vector3 center)
        {
            // 使用 BVH 查找笔刷影响范围内的顶点
            var affectedIndices = mesh.BVH.QuerySphere(center, brush.Radius);
            if (affectedIndices.Count == 0) return SculptStrokeResult.Empty;

            // 缓存原始位置 (用于 Grab / SnakeHook)
            var originalPositions = mesh.Positions.ToArray();

            int affectedCount = 0;
            float totalDisp = 0f;
            float maxDisp = 0f;

            foreach (var i in affectedIndices)
            {
                var pos = mesh.Positions[i];
                float dist = Vector3.Distance(pos, center);
                if (dist > brush.Radius) continue;

                float normalizedDist = dist / brush.Radius;
                float falloff = BrushFalloffCurve.Evaluate(normalizedDist, brush.Falloff);
                float maskWeight = mesh.GetMask(i);
                float weight = falloff * brush.Strength * maskWeight;
                if (weight <= 1e-6f && brush.Type != BrushType.Mask) continue;

                Vector3 newPos;
                switch (brush.Type)
                {
                    case BrushType.Draw:
                        newPos = ApplyDraw(mesh, i, brush, weight);
                        break;

                    case BrushType.Inflate:
                        newPos = ApplyInflate(mesh, i, weight);
                        break;

                    case BrushType.Smooth:
                        newPos = ApplySmooth(mesh, originalPositions, i, weight);
                        break;

                    case BrushType.Flatten:
                        newPos = ApplyFlatten(mesh, i, center, brush, weight);
                        break;

                    case BrushType.Pinch:
                        newPos = ApplyPinch(mesh, i, center, weight);
                        break;

                    case BrushType.Grab:
                        newPos = ApplyGrab(mesh, i, originalPositions, center, brush, weight);
                        break;

                    case BrushType.SnakeHook:
                        newPos = ApplySnakeHook(mesh, i, originalPositions, center, brush, weight);
                        break;

                    case BrushType.Layer:
                        newPos = ApplyLayer(mesh, i, originalPositions, center, brush, weight);
                        break;

                    case BrushType.Mask:
                        mesh.Masks[i] = Math.Clamp(1f - weight * 2f, 0f, 1f);
                        continue; // 不计算位移

                    default:
                        continue;
                }

                var displacement = Vector3.Distance(mesh.Positions[i], newPos);
                mesh.Positions[i] = newPos;

                if (displacement > 1e-6f)
                {
                    affectedCount++;
                    totalDisp += displacement;
                    maxDisp = Math.Max(maxDisp, displacement);
                }
            }

            // 动态拓扑
            mesh.DynamicTopo.SubdivideInBrushRange(center, brush.Radius);

            return new SculptStrokeResult(affectedCount, totalDisp, maxDisp);
        }

        // ──────────────────────────────────────────────────────────────
        //  各笔刷类型的具体实现
        // ──────────────────────────────────────────────────────────────

        /// <summary>Draw: 沿法线方向推拉顶点。</summary>
        private static Vector3 ApplyDraw(SculptMesh mesh, int i, SculptBrush brush, float weight)
        {
            var normal = brush.GetEffectiveNormal(mesh.SafeNormal(i));
            return mesh.Positions[i] + normal * weight;
        }

        /// <summary>Inflate: 沿法线膨胀 (与 Draw 类似但总是向外)。</summary>
        private static Vector3 ApplyInflate(SculptMesh mesh, int i, float weight)
        {
            var normal = mesh.SafeNormal(i);
            return mesh.Positions[i] + normal * weight;
        }

        /// <summary>Smooth: Laplacian 平滑 (邻居平均)。</summary>
        private static Vector3 ApplySmooth(SculptMesh mesh, Vector3[] originalPositions, int i, float weight)
        {
            if (i >= mesh.Adjacency.Count || mesh.Adjacency[i].Count == 0)
                return originalPositions[i];

            Vector3 sum = Vector3.Zero;
            foreach (var nb in mesh.Adjacency[i])
                sum += originalPositions[nb];
            var avg = sum / mesh.Adjacency[i].Count;

            return Vector3.Lerp(originalPositions[i], avg, Math.Clamp(weight, 0f, 1f));
        }

        /// <summary>Flatten: 将顶点投影到平均平面。</summary>
        private static Vector3 ApplyFlatten(SculptMesh mesh, int i, Vector3 center, SculptBrush brush, float weight)
        {
            // 计算笔刷范围内顶点的平均平面
            var normal = brush.GetEffectiveNormal(mesh.SafeNormal(i));
            var planePoint = center;

            // 将顶点投影到平面
            var pos = mesh.Positions[i];
            float signedDist = Vector3.Dot(pos - planePoint, normal);
            var projected = pos - normal * signedDist;

            return Vector3.Lerp(pos, projected, Math.Clamp(weight, 0f, 1f));
        }

        /// <summary>Pinch: 向笔刷中心收缩顶点。</summary>
        private static Vector3 ApplyPinch(SculptMesh mesh, int i, Vector3 center, float weight)
        {
            var pos = mesh.Positions[i];
            return Vector3.Lerp(pos, center, Math.Clamp(weight, 0f, 1f));
        }

        /// <summary>Grab: 抓取顶点拖拽 (自由变形)。</summary>
        private static Vector3 ApplyGrab(SculptMesh mesh, int i, Vector3[] originalPositions, Vector3 center, SculptBrush brush, float weight)
        {
            var pos = originalPositions[i];
            var delta = brush.DragDirection * weight;
            return pos + delta;
        }

        /// <summary>SnakeHook: 蛇钩 (拉伸顶点形成突起, 沿拖拽方向)。</summary>
        private static Vector3 ApplySnakeHook(SculptMesh mesh, int i, Vector3[] originalPositions, Vector3 center, SculptBrush brush, float weight)
        {
            var pos = originalPositions[i];
            var normal = mesh.SafeNormal(i);
            var dragDir = brush.DragDirection.LengthSquared() > 1e-8f
                ? Vector3.Normalize(brush.DragDirection)
                : normal;

            // 沿拖拽方向拉伸, 同时沿法线方向略微提升
            var delta = dragDir * weight + normal * weight * 0.3f;
            return pos + delta;
        }

        /// <summary>Layer: 固定高度层 (不叠加, 超过目标高度不再推)。</summary>
        private static Vector3 ApplyLayer(SculptMesh mesh, int i, Vector3[] originalPositions, Vector3 center, SculptBrush brush, float weight)
        {
            var pos = mesh.Positions[i];
            var originalPos = originalPositions[i];
            var normal = brush.GetEffectiveNormal(mesh.SafeNormal(i));

            // 计算当前位移量 (相对于原始位置)
            float currentDisp = Vector3.Dot(pos - originalPos, normal);

            // 目标高度
            float targetHeight = brush.LayerHeight * weight;

            // 如果当前位移已超过目标高度, 不再推
            if (currentDisp >= targetHeight) return pos;

            // 推到目标高度
            float remaining = targetHeight - currentDisp;
            return pos + normal * remaining;
        }
    }

    /// <summary>雕刻笔触结果。</summary>
    public struct SculptStrokeResult
    {
        public int AffectedVertices;
        public float TotalDisplacement;
        public float MaxDisplacement;

        public SculptStrokeResult(int affected, float total, float max)
        {
            AffectedVertices = affected;
            TotalDisplacement = total;
            MaxDisplacement = max;
        }

        public static SculptStrokeResult Empty => new(0, 0f, 0f);
    }
}
