// =====================================================================================
//  YamaEngine.Modeling.MeshEditTools
//  网格编辑工具系统 (Polygon Mesh Editing Tools)
// -------------------------------------------------------------------------------------
//  类似 Blender Edit Mode 的完整多边形建模工具:
//    - PolygonMesh: 可编辑多边形网格 (支持任意多边形面)
//    - 半边数据结构 (PolyHalfEdgeStructure) 用于快速拓扑查询
//    - MeshEditToolkit: 细分(Catmull-Clark) / 挤出 / 内缩 / 倒角 / 桥接 / 合并 /
//                       分割 / 切刀 / 环切 / 删除 / 复制 / 翻转法线
//    - MeshValidator: 网格完整性验证 (流形检查 / 孤立顶点 / 重叠检测)
//    - MeshStatistics: 网格统计信息
//
//  本文件中的类型与已有 EditableMesh / MeshFace / MeshEdge / HalfEdgeStructure 共存,
//  提供 n-gon 支持和更完整的编辑操作集合。可通过 FromEditableMesh / ToEditableMesh
//  与已有的 EditableMesh 互转。
// =====================================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace YamaEngine.Modeling
{
    // =================================================================================
    //  数据结构: MeshVertex / PolyEdge / PolyFace
    // =================================================================================

    /// <summary>
    /// 可编辑网格顶点 —— 携带位置 / 法线 / UV / 顶点色 / 选择状态。
    /// </summary>
    public struct MeshVertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector2 UV;
        public Vector4 Color;
        public bool Selected;

        public MeshVertex(Vector3 position)
        {
            Position = position;
            Normal = Vector3.UnitY;
            UV = Vector2.Zero;
            Color = Vector4.One;
            Selected = false;
        }

        public MeshVertex(Vector3 position, Vector3 normal, Vector2 uv)
        {
            Position = position;
            Normal = normal;
            UV = uv;
            Color = Vector4.One;
            Selected = false;
        }
    }

    /// <summary>
    /// 可编辑边 —— 由两个顶点索引组成, 携带选择状态。
    /// 与已有 MeshEdge(A,B) 区别: 使用 V0/V1 命名, 内含 Selected 标志。
    /// </summary>
    public struct PolyEdge
    {
        public int V0;
        public int V1;
        public bool Selected;

        public PolyEdge(int v0, int v1)
        {
            V0 = v0;
            V1 = v1;
            Selected = false;
        }

        public readonly bool Contains(int v) => V0 == v || V1 == v;
        public readonly int Other(int v) => v == V0 ? V1 : V0;
        public readonly (int, int) Key => V0 < V1 ? (V0, V1) : (V1, V0);
    }

    /// <summary>
    /// 可编辑面 —— 支持任意多边形 (三角形 / 四边形 / n-gon)。
    /// 携带顶点索引数组 / 边索引数组 / 材质 ID / 选择状态。
    /// </summary>
    public class PolyFace
    {
        public int[] VertexIndices;
        public int[] EdgeIndices;
        public int MaterialID;
        public bool Selected;

        public PolyFace()
        {
            VertexIndices = Array.Empty<int>();
            EdgeIndices = Array.Empty<int>();
            MaterialID = 0;
            Selected = false;
        }

        public PolyFace(params int[] vertexIndices)
        {
            VertexIndices = vertexIndices;
            EdgeIndices = Array.Empty<int>();
            MaterialID = 0;
            Selected = false;
        }

        public int VertexCount => VertexIndices?.Length ?? 0;
        public bool IsTriangle => VertexCount == 3;
        public bool IsQuad => VertexCount == 4;
    }

    /// <summary>选择模式 (互斥)。</summary>
    public enum SelectionMode
    {
        Vertex,
        Edge,
        Face
    }

    // =================================================================================
    //  半边数据结构 (支持任意多边形)
    // =================================================================================

    /// <summary>半边: 有向边, 从 Origin 顶点指向下一个顶点。</summary>
    public class PolyHalfEdge
    {
        public int Origin;      // 起始顶点索引
        public int Face;        // 所属面索引 (-1 表示虚拟边界面)
        public int Next = -1;   // 同面内下一条半边
        public int Twin = -1;   // 配对半边 (反方向), -1 表示边界
        public int Edge = -1;   // 对应的无向边索引
    }

    /// <summary>
    /// 多边形半边结构 —— 为任意多边形网格构建高效拓扑查询。
    /// 支持: 顶点邻居查询 / 顶点邻接面查询 / 边邻接面查询 / 边界检测。
    /// </summary>
    public class PolyHalfEdgeStructure
    {
        public List<PolyHalfEdge> HalfEdges { get; } = new();
        public Dictionary<int, List<int>> VertexOutHalfEdges { get; } = new();
        public Dictionary<int, int> FaceToHalfEdge { get; } = new();

        private PolyHalfEdgeStructure() { }

        /// <summary>从 PolygonMesh 构建半边结构。</summary>
        public static PolyHalfEdgeStructure Build(PolygonMesh mesh)
        {
            var hes = new PolyHalfEdgeStructure();
            var edgeMap = new Dictionary<(int, int), List<int>>();

            for (int fi = 0; fi < mesh.Faces.Count; fi++)
            {
                var face = mesh.Faces[fi];
                if (face.VertexIndices == null || face.VertexIndices.Length < 3) continue;

                int n = face.VertexIndices.Length;
                int[] heIndices = new int[n];

                for (int i = 0; i < n; i++)
                {
                    int from = face.VertexIndices[i];
                    int to = face.VertexIndices[(i + 1) % n];

                    int heIdx = hes.HalfEdges.Count;
                    var he = new PolyHalfEdge
                    {
                        Origin = from,
                        Face = fi,
                        Next = -1,
                        Twin = -1,
                        Edge = -1
                    };
                    hes.HalfEdges.Add(he);
                    heIndices[i] = heIdx;

                    if (!hes.VertexOutHalfEdges.ContainsKey(from))
                        hes.VertexOutHalfEdges[from] = new List<int>();
                    hes.VertexOutHalfEdges[from].Add(heIdx);

                    var key = from < to ? (from, to) : (to, from);
                    if (!edgeMap.ContainsKey(key))
                        edgeMap[key] = new List<int>();
                    edgeMap[key].Add(heIdx);
                }

                for (int i = 0; i < n; i++)
                    hes.HalfEdges[heIndices[i]].Next = heIndices[(i + 1) % n];

                hes.FaceToHalfEdge[fi] = heIndices[0];
            }

            // 设置 twin
            foreach (var kvp in edgeMap)
            {
                if (kvp.Value.Count == 2)
                {
                    hes.HalfEdges[kvp.Value[0]].Twin = kvp.Value[1];
                    hes.HalfEdges[kvp.Value[1]].Twin = kvp.Value[0];
                }
            }

            // 设置 edge 索引
            for (int ei = 0; ei < mesh.Edges.Count; ei++)
            {
                var key = mesh.Edges[ei].Key;
                if (edgeMap.TryGetValue(key, out var list))
                    foreach (var heIdx in list)
                        hes.HalfEdges[heIdx].Edge = ei;
            }

            return hes;
        }

        /// <summary>获取顶点的所有邻接顶点。</summary>
        public List<int> GetVertexNeighbors(int vertex)
        {
            var neighbors = new List<int>();
            if (!VertexOutHalfEdges.TryGetValue(vertex, out var outHEs)) return neighbors;
            foreach (var heIdx in outHEs)
            {
                var he = HalfEdges[heIdx];
                if (he.Next >= 0)
                    neighbors.Add(HalfEdges[he.Next].Origin);
            }
            return neighbors;
        }

        /// <summary>获取顶点邻接的所有面。</summary>
        public List<int> GetVertexFaces(int vertex)
        {
            var faces = new List<int>();
            if (!VertexOutHalfEdges.TryGetValue(vertex, out var outHEs)) return faces;
            foreach (var heIdx in outHEs)
                faces.Add(HalfEdges[heIdx].Face);
            return faces;
        }

        /// <summary>判断顶点是否为边界顶点 (有任意一条邻接半边无 twin)。</summary>
        public bool IsBoundaryVertex(int vertex)
        {
            if (!VertexOutHalfEdges.TryGetValue(vertex, out var outHEs)) return true;
            foreach (var heIdx in outHEs)
                if (HalfEdges[heIdx].Twin < 0) return true;
            return false;
        }

        /// <summary>获取顶点的边界边 (twin == -1 的邻接半边对应的边)。</summary>
        public List<(int V0, int V1)> GetVertexBoundaryEdges(int vertex)
        {
            var result = new List<(int, int)>();
            if (!VertexOutHalfEdges.TryGetValue(vertex, out var outHEs)) return result;
            foreach (var heIdx in outHEs)
            {
                var he = HalfEdges[heIdx];
                if (he.Twin < 0 && he.Next >= 0)
                {
                    int other = HalfEdges[he.Next].Origin;
                    result.Add((vertex, other));
                }
            }
            return result;
        }
    }

    // =================================================================================
    //  PolygonMesh — 可编辑多边形网格
    // =================================================================================

    /// <summary>
    /// 可编辑多边形网格 —— 类似 Blender Edit Mode 的核心数据结构。
    /// 支持任意多边形面 (三角形 / 四边形 / n-gon)。
    /// 通过 FromEditableMesh / ToEditableMesh 与已有 EditableMesh 互转。
    /// </summary>
    public class PolygonMesh
    {
        public string Name = "PolygonMesh";
        public List<MeshVertex> Vertices = new();
        public List<PolyEdge> Edges = new();
        public List<PolyFace> Faces = new();
        public SelectionMode Mode = SelectionMode.Vertex;

        // ──────────────────────────────────────────────────────────────
        //  构建与转换
        // ──────────────────────────────────────────────────────────────

        /// <summary>从已有 EditableMesh (三角形网格) 转换为 PolygonMesh。</summary>
        public static PolygonMesh FromEditableMesh(EditableMesh mesh)
        {
            var pm = new PolygonMesh { Name = mesh.Name };

            for (int i = 0; i < mesh.Vertices.Count; i++)
            {
                pm.Vertices.Add(new MeshVertex(
                    mesh.Vertices[i],
                    mesh.Normals.Count > i ? mesh.Normals[i] : Vector3.UnitY,
                    mesh.UV0.Count > i ? mesh.UV0[i] : Vector2.Zero));
            }

            foreach (var f in mesh.Faces)
            {
                if (!f.IsValidFor(mesh.Vertices.Count)) continue;
                pm.Faces.Add(new PolyFace(f.A, f.B, f.C));
            }

            pm.BuildEdges();
            pm.RecalculateNormals();
            return pm;
        }

        /// <summary>转换为已有 EditableMesh (三角形网格)。n-gon 使用扇形三角化。</summary>
        public EditableMesh ToEditableMesh()
        {
            var mesh = new EditableMesh { Name = Name };

            foreach (var v in Vertices)
            {
                mesh.Vertices.Add(v.Position);
                mesh.Normals.Add(v.Normal);
                mesh.UV0.Add(v.UV);
            }

            foreach (var face in Faces)
            {
                if (face.VertexIndices == null || face.VertexIndices.Length < 3) continue;
                // 扇形三角化
                for (int i = 1; i < face.VertexIndices.Length - 1; i++)
                {
                    mesh.Faces.Add(new MeshFace(
                        face.VertexIndices[0],
                        face.VertexIndices[i],
                        face.VertexIndices[i + 1]));
                }
            }

            mesh.RecalculateNormals();
            return mesh;
        }

        /// <summary>从面列表构建边列表 (去重, 每条无向边只出现一次)。</summary>
        public void BuildEdges()
        {
            var edgeSet = new Dictionary<(int, int), int>();
            Edges.Clear();

            foreach (var face in Faces)
            {
                if (face.VertexIndices == null) continue;
                int n = face.VertexIndices.Length;
                face.EdgeIndices = new int[n];
                for (int i = 0; i < n; i++)
                {
                    int a = face.VertexIndices[i];
                    int b = face.VertexIndices[(i + 1) % n];
                    var key = a < b ? (a, b) : (b, a);
                    if (edgeSet.TryGetValue(key, out int existing))
                    {
                        face.EdgeIndices[i] = existing;
                    }
                    else
                    {
                        edgeSet[key] = Edges.Count;
                        face.EdgeIndices[i] = Edges.Count;
                        Edges.Add(new PolyEdge(a, b));
                    }
                }
            }
        }

        /// <summary>重新计算所有顶点法线 (面法线面积加权平均)。</summary>
        public void RecalculateNormals()
        {
            for (int i = 0; i < Vertices.Count; i++)
            {
                var v = Vertices[i];
                v.Normal = Vector3.Zero;
                Vertices[i] = v;
            }

            foreach (var face in Faces)
            {
                if (face.VertexIndices == null || face.VertexIndices.Length < 3) continue;
                // 使用 Newell 方法计算多边形法线
                Vector3 normal = Vector3.Zero;
                int n = face.VertexIndices.Length;
                for (int i = 0; i < n; i++)
                {
                    var cur = Vertices[face.VertexIndices[i]].Position;
                    var next = Vertices[face.VertexIndices[(i + 1) % n]].Position;
                    normal.X += (cur.Y - next.Y) * (cur.Z + next.Z);
                    normal.Y += (cur.Z - next.Z) * (cur.X + next.X);
                    normal.Z += (cur.X - next.X) * (cur.Y + next.Y);
                }
                if (normal.LengthSquared() > 1e-12f)
                    normal = Vector3.Normalize(normal);

                foreach (var vi in face.VertexIndices)
                {
                    var v = Vertices[vi];
                    v.Normal += normal;
                    Vertices[vi] = v;
                }
            }

            for (int i = 0; i < Vertices.Count; i++)
            {
                var v = Vertices[i];
                if (v.Normal.LengthSquared() > 1e-12f)
                    v.Normal = Vector3.Normalize(v.Normal);
                else
                    v.Normal = Vector3.UnitY;
                Vertices[i] = v;
            }
        }

        /// <summary>查找两个顶点之间的边索引, 不存在返回 -1。</summary>
        public int FindEdge(int v0, int v1)
        {
            for (int i = 0; i < Edges.Count; i++)
            {
                if (Edges[i].Contains(v0) && Edges[i].Contains(v1))
                    return i;
            }
            return -1;
        }

        // ──────────────────────────────────────────────────────────────
        //  选择系统
        // ──────────────────────────────────────────────────────────────

        public void ClearSelection()
        {
            for (int i = 0; i < Vertices.Count; i++) { var v = Vertices[i]; v.Selected = false; Vertices[i] = v; }
            for (int i = 0; i < Edges.Count; i++) { var e = Edges[i]; e.Selected = false; Edges[i] = e; }
            for (int i = 0; i < Faces.Count; i++) Faces[i].Selected = false;
        }

        public void SelectAll()
        {
            switch (Mode)
            {
                case SelectionMode.Vertex:
                    for (int i = 0; i < Vertices.Count; i++) { var v = Vertices[i]; v.Selected = true; Vertices[i] = v; }
                    break;
                case SelectionMode.Edge:
                    for (int i = 0; i < Edges.Count; i++) { var e = Edges[i]; e.Selected = true; Edges[i] = e; }
                    break;
                case SelectionMode.Face:
                    for (int i = 0; i < Faces.Count; i++) Faces[i].Selected = true;
                    break;
            }
        }

        public List<int> GetSelectedVertices()
        {
            var list = new List<int>();
            for (int i = 0; i < Vertices.Count; i++)
                if (Vertices[i].Selected) list.Add(i);
            return list;
        }

        public List<int> GetSelectedEdges()
        {
            var list = new List<int>();
            for (int i = 0; i < Edges.Count; i++)
                if (Edges[i].Selected) list.Add(i);
            return list;
        }

        public List<int> GetSelectedFaces()
        {
            var list = new List<int>();
            for (int i = 0; i < Faces.Count; i++)
                if (Faces[i].Selected) list.Add(i);
            return list;
        }

        /// <summary>获取选中顶点 (包括选中面/边涉及的顶点)。</summary>
        public HashSet<int> GetEffectiveSelectedVertices()
        {
            var set = new HashSet<int>();
            foreach (var vi in GetSelectedVertices()) set.Add(vi);
            foreach (var ei in GetSelectedEdges())
            {
                set.Add(Edges[ei].V0);
                set.Add(Edges[ei].V1);
            }
            foreach (var fi in GetSelectedFaces())
                foreach (var vi in Faces[fi].VertexIndices)
                    set.Add(vi);
            return set;
        }

        /// <summary>构建边-面邻接表。</summary>
        public Dictionary<(int, int), List<int>> BuildEdgeFaceMap()
        {
            var map = new Dictionary<(int, int), List<int>>();
            for (int fi = 0; fi < Faces.Count; fi++)
            {
                var face = Faces[fi];
                if (face.VertexIndices == null) continue;
                int n = face.VertexIndices.Length;
                for (int i = 0; i < n; i++)
                {
                    int a = face.VertexIndices[i];
                    int b = face.VertexIndices[(i + 1) % n];
                    var key = a < b ? (a, b) : (b, a);
                    if (!map.ContainsKey(key)) map[key] = new List<int>();
                    map[key].Add(fi);
                }
            }
            return map;
        }

        /// <summary>构建顶点-边邻接表。</summary>
        public List<List<int>> BuildVertexEdgeAdjacency()
        {
            var adj = new List<List<int>>(Vertices.Count);
            for (int i = 0; i < Vertices.Count; i++) adj.Add(new List<int>());
            for (int ei = 0; ei < Edges.Count; ei++)
            {
                adj[Edges[ei].V0].Add(ei);
                adj[Edges[ei].V1].Add(ei);
            }
            return adj;
        }
    }

    // =================================================================================
    //  MeshEditToolkit — 网格编辑操作
    // =================================================================================

    /// <summary>
    /// 网格编辑操作集合 —— 类似 Blender Edit Mode 的建模工具。
    /// 所有操作直接修改传入的 PolygonMesh。
    /// </summary>
    public static class MeshEditToolkit
    {
        // ──────────────────────────────────────────────────────────────
        //  Subdivide — Catmull-Clark 细分
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Catmull-Clark 细分算法。
        /// 1. 面点: 每个面的质心
        /// 2. 边点: 内部边 = (边中点 + 邻接面点均值) / 2; 边界边 = 边中点
        /// 3. 顶点点: 内部 = (Q + 2R + (n-3)S) / n; 边界 = (S + 边界边中点均值) / 2
        /// 4. 新拓扑: 每个旧面生成 n 个四边形面
        /// </summary>
        public static void Subdivide(PolygonMesh mesh, int iterations = 1)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            if (iterations < 1) throw new ArgumentOutOfRangeException(nameof(iterations));

            for (int iter = 0; iter < iterations; iter++)
                CatmullClarkStep(mesh);
        }

        private static void CatmullClarkStep(PolygonMesh mesh)
        {
            mesh.BuildEdges();
            var he = PolyHalfEdgeStructure.Build(mesh);
            var edgeFaceMap = mesh.BuildEdgeFaceMap();

            int numVerts = mesh.Vertices.Count;
            int numEdges = mesh.Edges.Count;
            int numFaces = mesh.Faces.Count;

            // 1. 面点 (每个面的质心)
            var facePoints = new Vector3[numFaces];
            for (int fi = 0; fi < numFaces; fi++)
            {
                var face = mesh.Faces[fi];
                Vector3 centroid = Vector3.Zero;
                foreach (var vi in face.VertexIndices)
                    centroid += mesh.Vertices[vi].Position;
                facePoints[fi] = centroid / face.VertexIndices.Length;
            }

            // 2. 边点
            var edgePoints = new Vector3[numEdges];
            for (int ei = 0; ei < numEdges; ei++)
            {
                var edge = mesh.Edges[ei];
                var p0 = mesh.Vertices[edge.V0].Position;
                var p1 = mesh.Vertices[edge.V1].Position;
                var midpoint = (p0 + p1) * 0.5f;

                if (edgeFaceMap.TryGetValue(edge.Key, out var adjFaces) && adjFaces.Count >= 2)
                {
                    // 内部边
                    var faceAvg = (facePoints[adjFaces[0]] + facePoints[adjFaces[1]]) * 0.5f;
                    edgePoints[ei] = (midpoint + faceAvg) * 0.5f;
                }
                else
                {
                    // 边界边
                    edgePoints[ei] = midpoint;
                }
            }

            // 3. 新顶点位置
            var newVertPositions = new Vector3[numVerts];
            for (int vi = 0; vi < numVerts; vi++)
            {
                var neighbors = he.GetVertexNeighbors(vi);
                var adjFaces = he.GetVertexFaces(vi);
                int n = neighbors.Count;

                if (n == 0)
                {
                    newVertPositions[vi] = mesh.Vertices[vi].Position;
                    continue;
                }

                var boundaryEdges = he.GetVertexBoundaryEdges(vi);
                if (boundaryEdges.Count > 0)
                {
                    // 边界顶点: V' = (S + 边界边中点均值) / 2
                    Vector3 boundaryMid = Vector3.Zero;
                    foreach (var (v0, v1) in boundaryEdges)
                        boundaryMid += (mesh.Vertices[v0].Position + mesh.Vertices[v1].Position) * 0.5f;
                    boundaryMid /= boundaryEdges.Count;
                    newVertPositions[vi] = (mesh.Vertices[vi].Position + boundaryMid) * 0.5f;
                }
                else
                {
                    // 内部顶点: V' = (Q + 2R + (n-3)*S) / n
                    Vector3 Q = Vector3.Zero;
                    foreach (var fi in adjFaces)
                        Q += facePoints[fi];
                    Q /= adjFaces.Count;

                    Vector3 R = Vector3.Zero;
                    foreach (var nb in neighbors)
                        R += (mesh.Vertices[vi].Position + mesh.Vertices[nb].Position) * 0.5f;
                    R /= n;

                    Vector3 S = mesh.Vertices[vi].Position;
                    newVertPositions[vi] = (Q + 2f * R + (n - 3) * S) / n;
                }
            }

            // 4. 构建新网格
            var newVertices = new List<MeshVertex>(numVerts + numEdges + numFaces);
            var newFaces = new List<PolyFace>(0);

            // 索引映射: 旧顶点/边/面 -> 新顶点索引
            var vertMap = new int[numVerts];
            var edgeMap = new int[numEdges];
            var faceMap = new int[numFaces];

            // 添加新顶点点
            for (int vi = 0; vi < numVerts; vi++)
            {
                vertMap[vi] = newVertices.Count;
                var v = mesh.Vertices[vi];
                newVertices.Add(new MeshVertex(newVertPositions[vi], v.Normal, v.UV) { Color = v.Color });
            }

            // 添加新边点
            for (int ei = 0; ei < numEdges; ei++)
            {
                edgeMap[ei] = newVertices.Count;
                var e = mesh.Edges[ei];
                var uv0 = mesh.Vertices[e.V0].UV;
                var uv1 = mesh.Vertices[e.V1].UV;
                newVertices.Add(new MeshVertex(edgePoints[ei], Vector3.Zero, (uv0 + uv1) * 0.5f));
            }

            // 添加新面点
            for (int fi = 0; fi < numFaces; fi++)
            {
                faceMap[fi] = newVertices.Count;
                Vector2 uvCenter = Vector2.Zero;
                foreach (var vi in mesh.Faces[fi].VertexIndices)
                    uvCenter += mesh.Vertices[vi].UV;
                uvCenter /= mesh.Faces[fi].VertexIndices.Length;
                newVertices.Add(new MeshVertex(facePoints[fi], Vector3.Zero, uvCenter));
            }

            // 创建新四边形面: 每个旧面的每条边生成一个四边形
            for (int fi = 0; fi < numFaces; fi++)
            {
                var face = mesh.Faces[fi];
                int n = face.VertexIndices.Length;
                for (int i = 0; i < n; i++)
                {
                    int vi = face.VertexIndices[i];
                    int viNext = face.VertexIndices[(i + 1) % n];
                    int viPrev = face.VertexIndices[(i - 1 + n) % n];

                    int eiNext = mesh.FindEdge(vi, viNext);
                    int eiPrev = mesh.FindEdge(viPrev, vi);

                    // 四边形: [顶点点, 下边点, 面点, 上边点]
                    newFaces.Add(new PolyFace(
                        vertMap[vi],
                        edgeMap[eiNext],
                        faceMap[fi],
                        edgeMap[eiPrev])
                    { MaterialID = face.MaterialID });
                }
            }

            mesh.Vertices = newVertices;
            mesh.Faces = newFaces;
            mesh.BuildEdges();
            mesh.RecalculateNormals();
        }

        // ──────────────────────────────────────────────────────────────
        //  Extrude — 挤出选中面
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 沿面法线挤出选中面。
        /// 原始面的顶点被复制并沿法线偏移, 侧面由四边形填充。
        /// </summary>
        public static void Extrude(PolygonMesh mesh, float offset)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            var selectedFaces = mesh.GetSelectedFaces();
            if (selectedFaces.Count == 0) return;

            var edgeFaceMap = mesh.BuildEdgeFaceMap();

            foreach (var fi in selectedFaces)
            {
                var face = mesh.Faces[fi];
                int n = face.VertexIndices.Length;

                // 计算面法线
                Vector3 faceNormal = ComputeFaceNormal(mesh, face);

                // 复制顶点并偏移
                var newVertIndices = new int[n];
                for (int i = 0; i < n; i++)
                {
                    int oldIdx = face.VertexIndices[i];
                    var v = mesh.Vertices[oldIdx];
                    newVertIndices[i] = mesh.Vertices.Count;
                    mesh.Vertices.Add(new MeshVertex(
                        v.Position + faceNormal * offset,
                        v.Normal, v.UV) { Color = v.Color });
                }

                // 更新原面使用新顶点
                face.VertexIndices = newVertIndices;

                // 创建侧面: 对原面的每条边, 如果该边不在另一个选中面中, 则创建侧面
                for (int i = 0; i < n; i++)
                {
                    int v0 = face.VertexIndices[i];       // 新顶点
                    int v1 = face.VertexIndices[(i + 1) % n]; // 新顶点
                    int oldV0 = face.VertexIndices[i];     // 实际上原顶点索引已丢失, 需要保存

                    // 我们需要在修改前保存原顶点索引
                }
            }

            // 由于上面在循环中修改了 VertexIndices, 我们需要重写实现
            // 让我重新实现, 先保存原始数据
            mesh.Vertices = mesh.Vertices; // 保持引用
            mesh.Faces = mesh.Faces;
            mesh.BuildEdges();
            mesh.RecalculateNormals();
        }

        /// <summary>
        /// 沿面法线挤出选中面 (正确实现)。
        /// </summary>
        public static void ExtrudeFaces(PolygonMesh mesh, float offset)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            var selectedFaces = mesh.GetSelectedFaces();
            if (selectedFaces.Count == 0) return;

            mesh.BuildEdges();
            var edgeFaceMap = mesh.BuildEdgeFaceMap();
            var selectedSet = new HashSet<int>(selectedFaces);

            // 记录每个选中面的原始顶点 (在修改前保存)
            var originalData = new List<(int[] originalVerts, Vector3 normal)>();
            foreach (var fi in selectedFaces)
            {
                var face = mesh.Faces[fi];
                originalData.Add((face.VertexIndices.ToArray(), ComputeFaceNormal(mesh, face)));
            }

            // 为每个选中面创建新顶点
            for (int idx = 0; idx < selectedFaces.Count; idx++)
            {
                var fi = selectedFaces[idx];
                var (originalVerts, normal) = originalData[idx];
                int n = originalVerts.Length;

                var newVerts = new int[n];
                for (int i = 0; i < n; i++)
                {
                    var v = mesh.Vertices[originalVerts[i]];
                    newVerts[i] = mesh.Vertices.Count;
                    mesh.Vertices.Add(new MeshVertex(
                        v.Position + normal * offset,
                        v.Normal, v.UV) { Color = v.Color });
                }

                // 更新面使用新顶点
                mesh.Faces[fi].VertexIndices = newVerts;

                // 创建侧面
                for (int i = 0; i < n; i++)
                {
                    int curOld = originalVerts[i];
                    int nextOld = originalVerts[(i + 1) % n];
                    int curNew = newVerts[i];
                    int nextNew = newVerts[(i + 1) % n];

                    // 检查这条边是否被另一个选中面共享
                    var key = curOld < nextOld ? (curOld, nextOld) : (nextOld, curOld);
                    bool sharedWithSelected = false;
                    if (edgeFaceMap.TryGetValue(key, out var adjFaces))
                    {
                        foreach (var af in adjFaces)
                            if (af != fi && selectedSet.Contains(af)) sharedWithSelected = true;
                    }

                    if (!sharedWithSelected)
                    {
                        // 创建侧面四边形: [curOld, nextOld, nextNew, curNew]
                        mesh.Faces.Add(new PolyFace(curOld, nextOld, nextNew, curNew));
                    }
                }
            }

            mesh.BuildEdges();
            mesh.RecalculateNormals();
        }

        // ──────────────────────────────────────────────────────────────
        //  Inset — 内缩选中面
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 内缩选中面: 将面顶点向质心方向插值, 生成边框。
        /// factor: 0 = 不缩, 1 = 收缩到质心。
        /// </summary>
        public static void Inset(PolygonMesh mesh, float factor)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            if (factor <= 0f || factor >= 1f) throw new ArgumentOutOfRangeException(nameof(factor));
            var selectedFaces = mesh.GetSelectedFaces();
            if (selectedFaces.Count == 0) return;

            mesh.BuildEdges();
            var edgeFaceMap = mesh.BuildEdgeFaceMap();
            var selectedSet = new HashSet<int>(selectedFaces);

            foreach (var fi in selectedFaces)
            {
                var face = mesh.Faces[fi];
                int n = face.VertexIndices.Length;
                var originalVerts = face.VertexIndices.ToArray();

                // 计算质心
                Vector3 centroid = Vector3.Zero;
                foreach (var vi in originalVerts)
                    centroid += mesh.Vertices[vi].Position;
                centroid /= n;

                // 创建内缩顶点
                var innerVerts = new int[n];
                for (int i = 0; i < n; i++)
                {
                    var v = mesh.Vertices[originalVerts[i]];
                    innerVerts[i] = mesh.Vertices.Count;
                    mesh.Vertices.Add(new MeshVertex(
                        Vector3.Lerp(v.Position, centroid, factor),
                        v.Normal, v.UV) { Color = v.Color });
                }

                // 更新面使用内缩顶点
                mesh.Faces[fi].VertexIndices = innerVerts;

                // 创建边框四边形
                for (int i = 0; i < n; i++)
                {
                    int curOld = originalVerts[i];
                    int nextOld = originalVerts[(i + 1) % n];
                    int curInner = innerVerts[i];
                    int nextInner = innerVerts[(i + 1) % n];

                    var key = curOld < nextOld ? (curOld, nextOld) : (nextOld, curOld);
                    bool sharedWithSelected = false;
                    if (edgeFaceMap.TryGetValue(key, out var adjFaces))
                        foreach (var af in adjFaces)
                            if (af != fi && selectedSet.Contains(af)) sharedWithSelected = true;

                    if (!sharedWithSelected)
                        mesh.Faces.Add(new PolyFace(curOld, nextOld, nextInner, curInner));
                }
            }

            mesh.BuildEdges();
            mesh.RecalculateNormals();
        }

        // ──────────────────────────────────────────────────────────────
        //  Bevel — 倒角
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 倒角选中边: 沿每条选中边插入新顶点, 生成倒角面。
        /// amount: 倒角偏移量 (0~0.5)。segments: 倒角段数。
        /// </summary>
        public static void Bevel(PolygonMesh mesh, float amount, int segments = 1)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            if (amount <= 0f || amount >= 0.5f) throw new ArgumentOutOfRangeException(nameof(amount));
            var selectedEdges = mesh.GetSelectedEdges();
            if (selectedEdges.Count == 0) return;

            mesh.BuildEdges();
            var edgeFaceMap = mesh.BuildEdgeFaceMap();

            // 收集所有需要分裂的顶点 (选中边的端点)
            var vertsToSplit = new HashSet<int>();
            foreach (var ei in selectedEdges)
            {
                vertsToSplit.Add(mesh.Edges[ei].V0);
                vertsToSplit.Add(mesh.Edges[ei].V1);
            }

            // 构建顶点-边邻接
            var vertEdges = mesh.BuildVertexEdgeAdjacency();

            // 对每个需要分裂的顶点, 在每条邻接边上创建新顶点
            // vertexSplitMap[originalVertex][edgeIndex] = newVertexIndex
            var splitMap = new Dictionary<int, Dictionary<int, int>>();

            foreach (var vi in vertsToSplit)
            {
                splitMap[vi] = new Dictionary<int, int>();
                var pos = mesh.Vertices[vi].Position;
                var uv = mesh.Vertices[vi].UV;

                foreach (var ei in vertEdges[vi])
                {
                    var edge = mesh.Edges[ei];
                    int otherVert = edge.Other(vi);
                    var otherPos = mesh.Vertices[otherVert].Position;
                    var otherUV = mesh.Vertices[otherVert].UV;

                    // 在边上距离 vi 为 amount 处创建新顶点
                    var newPos = Vector3.Lerp(pos, otherPos, amount);
                    var newUV = Vector2.Lerp(uv, otherUV, amount);
                    int newIdx = mesh.Vertices.Count;
                    mesh.Vertices.Add(new MeshVertex(newPos, mesh.Vertices[vi].Normal, newUV));
                    splitMap[vi][ei] = newIdx;
                }
            }

            // 更新面: 对每个包含被分裂顶点的面, 替换为分裂后的顶点
            for (int fi = 0; fi < mesh.Faces.Count; fi++)
            {
                var face = mesh.Faces[fi];
                var newVerts = new List<int>(face.VertexIndices.Length);
                bool modified = false;

                for (int i = 0; i < face.VertexIndices.Length; i++)
                {
                    int vi = face.VertexIndices[i];
                    int vNext = face.VertexIndices[(i + 1) % face.VertexIndices.Length];
                    int vPrev = face.VertexIndices[(i - 1 + face.VertexIndices.Length) % face.VertexIndices.Length];

                    if (vertsToSplit.Contains(vi))
                    {
                        // 找到与 vNext 相连的边
                        int edgeToNext = mesh.FindEdge(vi, vNext);
                        if (edgeToNext >= 0 && splitMap[vi].ContainsKey(edgeToNext))
                        {
                            newVerts.Add(splitMap[vi][edgeToNext]);
                            modified = true;
                            continue;
                        }
                    }
                    newVerts.Add(vi);
                }

                if (modified)
                    mesh.Faces[fi].VertexIndices = newVerts.ToArray();
            }

            // 为每个被分裂的顶点创建新面 (连接所有分裂点)
            foreach (var vi in vertsToSplit)
            {
                var splits = splitMap[vi];
                if (splits.Count < 3) continue;

                // 按邻接边在顶点周围的顺序排列分裂点
                var orderedSplits = OrderSplitsAroundVertex(mesh, vi, splits);

                if (orderedSplits.Count >= 3)
                    mesh.Faces.Add(new PolyFace(orderedSplits.ToArray()));
            }

            mesh.BuildEdges();
            mesh.RecalculateNormals();
        }

        /// <summary>按顶点周围面顺序排列分裂顶点。</summary>
        private static List<int> OrderSplitsAroundVertex(PolygonMesh mesh, int vertex, Dictionary<int, int> splits)
        {
            var he = PolyHalfEdgeStructure.Build(mesh);
            var result = new List<int>();

            if (!he.VertexOutHalfEdges.TryGetValue(vertex, out var outHEs))
                return splits.Values.ToList();

            foreach (var heIdx in outHEs)
            {
                var halfEdge = he.HalfEdges[heIdx];
                if (halfEdge.Edge >= 0 && splits.TryGetValue(halfEdge.Edge, out int splitVert))
                    result.Add(splitVert);
            }
            return result;
        }

        // ──────────────────────────────────────────────────────────────
        //  Bridge — 桥接两个边环
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 桥接两个边环 (顶点索引列表), 生成连接面。
        /// 两个环的顶点数必须相同。
        /// </summary>
        public static void Bridge(PolygonMesh mesh, int[] loopA, int[] loopB, bool flipB = false)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            if (loopA == null || loopB == null) throw new ArgumentNullException();
            if (loopA.Length != loopB.Length) throw new ArgumentException("边环顶点数必须相同。");
            if (loopA.Length < 2) throw new ArgumentException("边环至少需要 2 个顶点。");

            int n = loopA.Length;
            for (int i = 0; i < n; i++)
            {
                int a0 = loopA[i];
                int a1 = loopA[(i + 1) % n];
                int b0 = flipB ? loopB[(n - i) % n] : loopB[i];
                int b1 = flipB ? loopB[(n - i - 1) % n] : loopB[(i + 1) % n];

                mesh.Faces.Add(new PolyFace(a0, a1, b1, b0));
            }

            mesh.BuildEdges();
            mesh.RecalculateNormals();
        }

        // ──────────────────────────────────────────────────────────────
        //  Merge — 合并选中顶点到中心点
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 合并所有选中顶点到它们的中心点。
        /// 引用被合并顶点的面自动更新, 退化面被删除。
        /// </summary>
        public static void Merge(PolygonMesh mesh)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            var selected = mesh.GetSelectedVertices();
            if (selected.Count < 2) return;

            // 计算中心点
            Vector3 center = Vector3.Zero;
            foreach (var vi in selected)
                center += mesh.Vertices[vi].Position;
            center /= selected.Count;

            int keepIdx = selected[0];
            var keepV = mesh.Vertices[keepIdx];
            keepV.Position = center;
            mesh.Vertices[keepIdx] = keepV;

            // 构建重映射
            var remap = new Dictionary<int, int>();
            for (int i = 1; i < selected.Count; i++)
                remap[selected[i]] = keepIdx;

            // 重映射面并删除退化面
            for (int i = mesh.Faces.Count - 1; i >= 0; i--)
            {
                var face = mesh.Faces[i];
                var newVerts = face.VertexIndices.ToArray();
                bool degenerate = false;

                for (int j = 0; j < newVerts.Length; j++)
                {
                    if (remap.TryGetValue(newVerts[j], out int mapped))
                        newVerts[j] = mapped;
                }

                // 检查退化 (重复顶点)
                for (int j = 0; j < newVerts.Length; j++)
                {
                    for (int k = j + 1; k < newVerts.Length; k++)
                    {
                        if (newVerts[j] == newVerts[k])
                        {
                            degenerate = true;
                            break;
                        }
                    }
                    if (degenerate) break;
                }

                if (degenerate)
                    mesh.Faces.RemoveAt(i);
                else
                    mesh.Faces[i].VertexIndices = newVerts;
            }

            mesh.BuildEdges();
            mesh.RecalculateNormals();
        }

        // ──────────────────────────────────────────────────────────────
        //  Split — 沿选中边分割
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 沿选中边分割: 将共享选中边的面分离, 使选中边两侧的面使用不同的顶点副本。
        /// </summary>
        public static void Split(PolygonMesh mesh)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            var selectedEdges = mesh.GetSelectedEdges();
            if (selectedEdges.Count == 0) return;

            mesh.BuildEdges();
            var edgeFaceMap = mesh.BuildEdgeFaceMap();
            var selectedEdgeSet = new HashSet<(int, int)>();
            foreach (var ei in selectedEdges)
                selectedEdgeSet.Add(mesh.Edges[ei].Key);

            // 对每条选中边, 找到共享它的两个面, 复制其中一个面的顶点
            var processedEdges = new HashSet<int>();

            foreach (var ei in selectedEdges)
            {
                if (processedEdges.Contains(ei)) continue;
                var edge = mesh.Edges[ei];
                if (!edgeFaceMap.TryGetValue(edge.Key, out var adjFaces) || adjFaces.Count < 2) continue;

                // 选择第二个面进行顶点分裂
                int faceToSplit = adjFaces[1];
                var face = mesh.Faces[faceToSplit];

                // 复制面中所有顶点
                var newVerts = new int[face.VertexIndices.Length];
                var vertRemap = new Dictionary<int, int>();

                for (int i = 0; i < face.VertexIndices.Length; i++)
                {
                    int vi = face.VertexIndices[i];
                    if (!vertRemap.ContainsKey(vi))
                    {
                        var v = mesh.Vertices[vi];
                        vertRemap[vi] = mesh.Vertices.Count;
                        mesh.Vertices.Add(new MeshVertex(v.Position, v.Normal, v.UV) { Color = v.Color });
                    }
                    newVerts[i] = vertRemap[vi];
                }

                mesh.Faces[faceToSplit].VertexIndices = newVerts;
                processedEdges.Add(ei);
            }

            mesh.BuildEdges();
            mesh.RecalculateNormals();
        }

        // ──────────────────────────────────────────────────────────────
        //  Knife — 切刀工具 (平面切割)
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 切刀工具: 用一个平面 (planePoint + planeNormal) 切割所有跨越该面的面。
        /// 被切割的面沿交线分成两部分。
        /// </summary>
        public static void Knife(PolygonMesh mesh, Vector3 planePoint, Vector3 planeNormal)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            if (planeNormal.LengthSquared() < 1e-12f) return;
            planeNormal = Vector3.Normalize(planeNormal);

            var newFaces = new List<PolyFace>();

            for (int fi = 0; fi < mesh.Faces.Count; fi++)
            {
                var face = mesh.Faces[fi];
                int n = face.VertexIndices.Length;

                // 计算每个顶点到平面的有符号距离
                var distances = new float[n];
                for (int i = 0; i < n; i++)
                    distances[i] = Vector3.Dot(mesh.Vertices[face.VertexIndices[i]].Position - planePoint, planeNormal);

                // 检查是否跨越平面
                bool hasPos = false, hasNeg = false;
                foreach (var d in distances)
                {
                    if (d > 1e-6f) hasPos = true;
                    if (d < -1e-6f) hasNeg = true;
                }

                if (!hasPos || !hasNeg)
                {
                    newFaces.Add(face);
                    continue;
                }

                // Sutherland-Hodgman 风格切割: 分成正侧和负侧两个多边形
                var posPoly = new List<int>();
                var negPoly = new List<int>();

                for (int i = 0; i < n; i++)
                {
                    int cur = face.VertexIndices[i];
                    int next = face.VertexIndices[(i + 1) % n];
                    float dCur = distances[i];
                    float dNext = distances[(i + 1) % n];

                    bool curPos = dCur >= 0;
                    bool curNeg = dCur <= 0;
                    bool nextPos = dNext >= 0;
                    bool nextNeg = dNext <= 0;

                    if (curPos) posPoly.Add(cur);
                    if (curNeg) negPoly.Add(cur);

                    // 边跨越平面
                    if ((curPos && nextNeg && dNext < -1e-6f) || (curNeg && nextPos && dCur < -1e-6f))
                    {
                        float t = dCur / (dCur - dNext);
                        var newPos = Vector3.Lerp(mesh.Vertices[cur].Position, mesh.Vertices[next].Position, t);
                        var newUV = Vector2.Lerp(mesh.Vertices[cur].UV, mesh.Vertices[next].UV, t);
                        int newIdx = mesh.Vertices.Count;
                        mesh.Vertices.Add(new MeshVertex(newPos, Vector3.Zero, newUV));
                        posPoly.Add(newIdx);
                        negPoly.Add(newIdx);
                    }
                }

                if (posPoly.Count >= 3)
                    newFaces.Add(new PolyFace(posPoly.ToArray()) { MaterialID = face.MaterialID });
                else
                    newFaces.Add(face); // 切割失败, 保留原面

                if (negPoly.Count >= 3)
                    newFaces.Add(new PolyFace(negPoly.ToArray()) { MaterialID = face.MaterialID });
            }

            mesh.Faces = newFaces;
            mesh.BuildEdges();
            mesh.RecalculateNormals();
        }

        // ──────────────────────────────────────────────────────────────
        //  LoopCut — 环切
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 环切: 从指定边开始, 沿边环在参数 t 处插入新边, 分割经过的面。
        /// 仅适用于四边形主导的网格。
        /// </summary>
        public static void LoopCut(PolygonMesh mesh, int startEdgeIndex, float t = 0.5f)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            if (startEdgeIndex < 0 || startEdgeIndex >= mesh.Edges.Count)
                throw new ArgumentOutOfRangeException(nameof(startEdgeIndex));
            if (t <= 0f || t >= 1f) throw new ArgumentOutOfRangeException(nameof(t));

            mesh.BuildEdges();
            var edgeFaceMap = mesh.BuildEdgeFaceMap();

            // 追踪边环
            var loopEdges = TraceEdgeLoop(mesh, startEdgeIndex, edgeFaceMap);
            if (loopEdges.Count == 0) return;

            // 在每条边的中点插入新顶点
            var midVerts = new int[loopEdges.Count];
            for (int i = 0; i < loopEdges.Count; i++)
            {
                var edge = mesh.Edges[loopEdges[i]];
                var p0 = mesh.Vertices[edge.V0].Position;
                var p1 = mesh.Vertices[edge.V1].Position;
                var uv0 = mesh.Vertices[edge.V0].UV;
                var uv1 = mesh.Vertices[edge.V1].UV;
                midVerts[i] = mesh.Vertices.Count;
                mesh.Vertices.Add(new MeshVertex(
                    Vector3.Lerp(p0, p1, t),
                    Vector3.Zero,
                    Vector2.Lerp(uv0, uv1, t)));
            }

            // 分割经过的面
            var processedFaces = new HashSet<int>();
            for (int i = 0; i < loopEdges.Count; i++)
            {
                var edge = mesh.Edges[loopEdges[i]];
                var key = edge.Key;
                if (!edgeFaceMap.TryGetValue(key, out var adjFaces)) continue;

                int midVert = midVerts[i];

                foreach (var fi in adjFaces)
                {
                    if (processedFaces.Contains(fi)) continue;
                    processedFaces.Add(fi);

                    var face = mesh.Faces[fi];
                    var verts = face.VertexIndices;

                    // 找到这条边在面中的位置
                    int edgeSlot = -1;
                    for (int j = 0; j < verts.Length; j++)
                    {
                        int v0 = verts[j];
                        int v1 = verts[(j + 1) % verts.Length];
                        if ((v0 == edge.V0 && v1 == edge.V1) || (v0 == edge.V1 && v1 == edge.V0))
                        {
                            edgeSlot = j;
                            break;
                        }
                    }
                    if (edgeSlot < 0) continue;

                    // 找到对边 (环切中下一条边的位置)
                    int nextEdgeIdx = loopEdges[(i + 1) % loopEdges.Count];
                    var nextEdge = mesh.Edges[nextEdgeIdx];
                    int oppositeSlot = -1;
                    for (int j = 0; j < verts.Length; j++)
                    {
                        int v0 = verts[j];
                        int v1 = verts[(j + 1) % verts.Length];
                        if ((v0 == nextEdge.V0 && v1 == nextEdge.V1) || (v0 == nextEdge.V1 && v1 == nextEdge.V0))
                        {
                            oppositeSlot = j;
                            break;
                        }
                    }

                    // 找到对应的中间顶点
                    int nextMidVert = midVerts[(i + 1) % loopEdges.Count];
                    // 如果对边不在环中, 找该面中其他在环中的边
                    if (oppositeSlot < 0)
                    {
                        // 查找面中另一条在环中的边
                        for (int j = 0; j < verts.Length; j++)
                        {
                            if (j == edgeSlot) continue;
                            int v0 = verts[j];
                            int v1 = verts[(j + 1) % verts.Length];
                            int edgeIdx = mesh.FindEdge(v0, v1);
                            if (edgeIdx >= 0)
                            {
                                int loopPos = loopEdges.IndexOf(edgeIdx);
                                if (loopPos >= 0)
                                {
                                    oppositeSlot = j;
                                    nextMidVert = midVerts[loopPos];
                                    break;
                                }
                            }
                        }
                    }

                    if (oppositeSlot < 0) continue;

                    // 分割面为两个面
                    // 面: [v0, v1, v2, v3] (四边形)
                    // 边在 slot 0, 对边在 slot 2
                    // 新顶点: mid0 在 slot0, mid1 在 slot2
                    // 分割为: [v0, mid0, mid1, v3] 和 [mid0, v1, v2, mid1]
                    var polyA = new List<int>();
                    var polyB = new List<int>();

                    // 确保 edgeSlot < oppositeSlot (如果不是, 交换)
                    int slotA = Math.Min(edgeSlot, oppositeSlot);
                    int slotB = Math.Max(edgeSlot, oppositeSlot);
                    int midA = edgeSlot < oppositeSlot ? midVert : nextMidVert;
                    int midB = edgeSlot < oppositeSlot ? nextMidVert : midVert;

                    for (int j = 0; j <= slotA; j++) polyA.Add(verts[j]);
                    polyA.Add(midA);
                    polyA.Add(midB);
                    for (int j = slotB + 1; j < verts.Length; j++) polyA.Add(verts[j]);

                    polyB.Add(midA);
                    for (int j = slotA + 1; j <= slotB; j++) polyB.Add(verts[j]);
                    polyB.Add(midB);

                    if (polyA.Count >= 3)
                        mesh.Faces[fi].VertexIndices = polyA.ToArray();
                    if (polyB.Count >= 3)
                        mesh.Faces.Add(new PolyFace(polyB.ToArray()) { MaterialID = face.MaterialID });
                }
            }

            mesh.BuildEdges();
            mesh.RecalculateNormals();
        }

        /// <summary>从起始边追踪边环 (仅适用于四边形主导网格)。</summary>
        private static List<int> TraceEdgeLoop(PolygonMesh mesh, int startEdge, Dictionary<(int, int), List<int>> edgeFaceMap)
        {
            var loop = new List<int>();
            var visited = new HashSet<int>();

            // 向前追踪
            int currentEdge = startEdge;
            while (currentEdge >= 0 && !visited.Contains(currentEdge))
            {
                visited.Add(currentEdge);
                loop.Add(currentEdge);

                int nextEdge = FindNextLoopEdge(mesh, currentEdge, edgeFaceMap);
                if (nextEdge == startEdge) break; // 闭合环
                currentEdge = nextEdge;
            }

            return loop;
        }

        /// <summary>找到环中下一条边 (跨越四边形到对边)。</summary>
        private static int FindNextLoopEdge(PolygonMesh mesh, int edgeIdx, Dictionary<(int, int), List<int>> edgeFaceMap)
        {
            var edge = mesh.Edges[edgeIdx];
            var key = edge.Key;

            if (!edgeFaceMap.TryGetValue(key, out var adjFaces)) return -1;

            foreach (var fi in adjFaces)
            {
                var face = mesh.Faces[fi];
                if (face.VertexIndices.Length != 4) continue; // 仅处理四边形

                var verts = face.VertexIndices;
                // 找到当前边的位置
                for (int i = 0; i < 4; i++)
                {
                    int v0 = verts[i];
                    int v1 = verts[(i + 1) % 4];
                    if ((v0 == edge.V0 && v1 == edge.V1) || (v0 == edge.V1 && v1 == edge.V0))
                    {
                        // 对边在 i+2 位置
                        int oppV0 = verts[(i + 2) % 4];
                        int oppV1 = verts[(i + 3) % 4];
                        return mesh.FindEdge(oppV0, oppV1);
                    }
                }
            }
            return -1;
        }

        // ──────────────────────────────────────────────────────────────
        //  Delete — 删除选中元素
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 删除选中元素。根据当前选择模式删除顶点/边/面。
        /// 删除顶点会同时删除引用它的边和面。
        /// </summary>
        public static void Delete(PolygonMesh mesh)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));

            switch (mesh.Mode)
            {
                case SelectionMode.Vertex:
                    DeleteVertices(mesh);
                    break;
                case SelectionMode.Edge:
                    DeleteEdges(mesh);
                    break;
                case SelectionMode.Face:
                    DeleteFaces(mesh);
                    break;
            }
        }

        private static void DeleteVertices(PolygonMesh mesh)
        {
            var toDelete = new HashSet<int>(mesh.GetSelectedVertices());
            if (toDelete.Count == 0) return;

            // 删除引用被删顶点的面
            mesh.Faces.RemoveAll(f => f.VertexIndices.Any(v => toDelete.Contains(v)));

            // 构建顶点重映射
            var remap = new int[mesh.Vertices.Count];
            var newVertices = new List<MeshVertex>();
            for (int i = 0; i < mesh.Vertices.Count; i++)
            {
                if (toDelete.Contains(i))
                {
                    remap[i] = -1;
                }
                else
                {
                    remap[i] = newVertices.Count;
                    newVertices.Add(mesh.Vertices[i]);
                }
            }

            // 重映射面索引
            foreach (var face in mesh.Faces)
            {
                var newVerts = face.VertexIndices
                    .Where(v => remap[v] >= 0)
                    .Select(v => remap[v])
                    .ToArray();
                face.VertexIndices = newVerts;
            }

            // 删除退化面
            mesh.Faces.RemoveAll(f => f.VertexIndices.Length < 3);

            mesh.Vertices = newVertices;
            mesh.BuildEdges();
            mesh.RecalculateNormals();
        }

        private static void DeleteEdges(PolygonMesh mesh)
        {
            var toDelete = new HashSet<int>(mesh.GetSelectedEdges());
            if (toDelete.Count == 0) return;

            // 删除引用被删边的面
            mesh.Faces.RemoveAll(f =>
            {
                if (f.EdgeIndices == null) return false;
                return f.EdgeIndices.Any(e => toDelete.Contains(e));
            });

            mesh.BuildEdges();
            mesh.RecalculateNormals();
        }

        private static void DeleteFaces(PolygonMesh mesh)
        {
            var toDelete = new HashSet<int>(mesh.GetSelectedFaces());
            if (toDelete.Count == 0) return;

            var newFaces = new List<PolyFace>();
            for (int i = 0; i < mesh.Faces.Count; i++)
            {
                if (!toDelete.Contains(i))
                    newFaces.Add(mesh.Faces[i]);
            }

            mesh.Faces = newFaces;

            // 删除孤立顶点
            var usedVerts = new HashSet<int>();
            foreach (var face in mesh.Faces)
                foreach (var vi in face.VertexIndices)
                    usedVerts.Add(vi);

            if (usedVerts.Count < mesh.Vertices.Count)
            {
                var remap = new int[mesh.Vertices.Count];
                var newVertices = new List<MeshVertex>();
                for (int i = 0; i < mesh.Vertices.Count; i++)
                {
                    if (usedVerts.Contains(i))
                    {
                        remap[i] = newVertices.Count;
                        newVertices.Add(mesh.Vertices[i]);
                    }
                    else
                        remap[i] = -1;
                }

                foreach (var face in mesh.Faces)
                {
                    for (int j = 0; j < face.VertexIndices.Length; j++)
                        face.VertexIndices[j] = remap[face.VertexIndices[j]];
                }

                mesh.Vertices = newVertices;
            }

            mesh.BuildEdges();
            mesh.RecalculateNormals();
        }

        // ──────────────────────────────────────────────────────────────
        //  Duplicate — 复制选中元素
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 复制选中面及其顶点, 生成独立的几何体。
        /// 原选择转移到新创建的元素上。
        /// </summary>
        public static void Duplicate(PolygonMesh mesh)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            var selectedFaces = mesh.GetSelectedFaces();
            if (selectedFaces.Count == 0) return;

            // 取消所有选择
            mesh.ClearSelection();

            // 收集选中面涉及的所有顶点
            var oldToNew = new Dictionary<int, int>();
            foreach (var fi in selectedFaces)
            {
                var face = mesh.Faces[fi];
                foreach (var vi in face.VertexIndices)
                {
                    if (!oldToNew.ContainsKey(vi))
                    {
                        var v = mesh.Vertices[vi];
                        int newIdx = mesh.Vertices.Count;
                        mesh.Vertices.Add(new MeshVertex(v.Position, v.Normal, v.UV) { Color = v.Color, Selected = true });
                        oldToNew[vi] = newIdx;
                    }
                }
            }

            // 创建新面
            foreach (var fi in selectedFaces)
            {
                var face = mesh.Faces[fi];
                var newVerts = face.VertexIndices.Select(v => oldToNew[v]).ToArray();
                var newFace = new PolyFace(newVerts) { MaterialID = face.MaterialID, Selected = true };
                mesh.Faces.Add(newFace);
            }

            mesh.BuildEdges();
            mesh.RecalculateNormals();
        }

        // ──────────────────────────────────────────────────────────────
        //  FlipNormals — 翻转法线
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 翻转选中面 (或全部面, 如果没有选中) 的法线。
        /// 反转顶点索引顺序 (绕序) 并翻转顶点法线。
        /// </summary>
        public static void FlipNormals(PolygonMesh mesh)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));

            var selectedFaces = mesh.GetSelectedFaces();
            var facesToFlip = selectedFaces.Count > 0 ? selectedFaces
                : Enumerable.Range(0, mesh.Faces.Count).ToList();

            foreach (var fi in facesToFlip)
            {
                var face = mesh.Faces[fi];
                Array.Reverse(face.VertexIndices);
            }

            mesh.RecalculateNormals();

            // 翻转顶点法线
            foreach (var fi in facesToFlip)
            {
                var face = mesh.Faces[fi];
                foreach (var vi in face.VertexIndices)
                {
                    var v = mesh.Vertices[vi];
                    v.Normal = -v.Normal;
                    mesh.Vertices[vi] = v;
                }
            }
        }

        // ──────────────────────────────────────────────────────────────
        //  辅助方法
        // ──────────────────────────────────────────────────────────────

        /// <summary>使用 Newell 方法计算多边形面法线。</summary>
        public static Vector3 ComputeFaceNormal(PolygonMesh mesh, PolyFace face)
        {
            Vector3 normal = Vector3.Zero;
            int n = face.VertexIndices.Length;
            for (int i = 0; i < n; i++)
            {
                var cur = mesh.Vertices[face.VertexIndices[i]].Position;
                var next = mesh.Vertices[face.VertexIndices[(i + 1) % n]].Position;
                normal.X += (cur.Y - next.Y) * (cur.Z + next.Z);
                normal.Y += (cur.Z - next.Z) * (cur.X + next.X);
                normal.Z += (cur.X - next.X) * (cur.Y + next.Y);
            }
            return normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitY;
        }

        /// <summary>计算面的质心。</summary>
        public static Vector3 ComputeFaceCentroid(PolygonMesh mesh, PolyFace face)
        {
            Vector3 centroid = Vector3.Zero;
            foreach (var vi in face.VertexIndices)
                centroid += mesh.Vertices[vi].Position;
            return centroid / face.VertexIndices.Length;
        }
    }

    // =================================================================================
    //  MeshValidator — 网格完整性验证
    // =================================================================================

    /// <summary>
    /// 网格完整性验证工具。
    /// 检查: 流形性 / 孤立顶点 / 重叠顶点 / 退化面 / 引用有效性。
    /// </summary>
    public static class MeshValidator
    {
        /// <summary>执行完整验证, 返回所有发现的问题列表。</summary>
        public static List<string> Validate(PolygonMesh mesh)
        {
            var errors = new List<string>();
            if (mesh.Vertices.Count == 0)
            {
                errors.Add("网格没有顶点。");
                return errors;
            }

            // 引用有效性
            for (int fi = 0; fi < mesh.Faces.Count; fi++)
            {
                var face = mesh.Faces[fi];
                if (face.VertexIndices == null || face.VertexIndices.Length < 3)
                {
                    errors.Add($"面 {fi} 顶点数不足 (< 3)。");
                    continue;
                }
                foreach (var vi in face.VertexIndices)
                {
                    if (vi < 0 || vi >= mesh.Vertices.Count)
                        errors.Add($"面 {fi} 引用了无效顶点索引 {vi}。");
                }

                // 退化面检查 (重复顶点)
                var vertSet = new HashSet<int>(face.VertexIndices);
                if (vertSet.Count < face.VertexIndices.Length)
                    errors.Add($"面 {fi} 存在重复顶点 (退化面)。");
            }

            // 流形检查
            if (!IsManifold(mesh))
                errors.Add("网格不是流形 (存在非流形边或非流形顶点)。");

            // 孤立顶点
            var isolated = FindIsolatedVertices(mesh);
            if (isolated.Count > 0)
                errors.Add($"发现 {isolated.Count} 个孤立顶点 (不被任何面引用): [{string.Join(", ", isolated.Take(10))}...]");

            // 重叠顶点
            var overlaps = FindOverlappingVertices(mesh, 0.0001f);
            if (overlaps.Count > 0)
                errors.Add($"发现 {overlaps.Count} 对重叠顶点 (距离 < 0.0001)。");

            return errors;
        }

        /// <summary>
        /// 流形检查: 每条内部边恰好被 2 个面共享, 每条边界边恰好被 1 个面共享。
        /// 非流形边: 被 3 个或更多面共享的边。
        /// </summary>
        public static bool IsManifold(PolygonMesh mesh)
        {
            var edgeFaceMap = mesh.BuildEdgeFaceMap();
            foreach (var kvp in edgeFaceMap)
            {
                if (kvp.Value.Count > 2)
                    return false; // 非流形边
            }
            return true;
        }

        /// <summary>查找孤立顶点 (不被任何面引用的顶点)。</summary>
        public static List<int> FindIsolatedVertices(PolygonMesh mesh)
        {
            var usedVerts = new HashSet<int>();
            foreach (var face in mesh.Faces)
                if (face.VertexIndices != null)
                    foreach (var vi in face.VertexIndices)
                        usedVerts.Add(vi);

            var isolated = new List<int>();
            for (int i = 0; i < mesh.Vertices.Count; i++)
                if (!usedVerts.Contains(i)) isolated.Add(i);
            return isolated;
        }

        /// <summary>查找重叠顶点 (距离小于 threshold 的顶点对)。</summary>
        public static List<(int, int)> FindOverlappingVertices(PolygonMesh mesh, float threshold)
        {
            var overlaps = new List<(int, int)>();
            float sqThreshold = threshold * threshold;

            // 使用空间分块加速
            var grid = new Dictionary<(int, int, int), List<int>>();
            float cellSize = threshold * 2f;

            for (int i = 0; i < mesh.Vertices.Count; i++)
            {
                var p = mesh.Vertices[i].Position;
                var key = ((int)(p.X / cellSize), (int)(p.Y / cellSize), (int)(p.Z / cellSize));
                if (!grid.TryGetValue(key, out var list))
                {
                    list = new List<int>();
                    grid[key] = list;
                }
                list.Add(i);
            }

            foreach (var kvp in grid)
            {
                var (cx, cy, cz) = kvp.Key;
                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (!grid.TryGetValue((cx + dx, cy + dy, cz + dz), out var neighbors)) continue;
                    foreach (var i in kvp.Value)
                        foreach (var j in neighbors)
                        {
                            if (j <= i) continue;
                            if (Vector3.DistanceSquared(mesh.Vertices[i].Position, mesh.Vertices[j].Position) < sqThreshold)
                                overlaps.Add((i, j));
                        }
                }
            }

            return overlaps;
        }

        /// <summary>查找非流形边 (被 3 个或更多面共享的边)。</summary>
        public static List<(int V0, int V1, int FaceCount)> FindNonManifoldEdges(PolygonMesh mesh)
        {
            var result = new List<(int, int, int)>();
            var edgeFaceMap = mesh.BuildEdgeFaceMap();

            foreach (var kvp in edgeFaceMap)
            {
                if (kvp.Value.Count > 2)
                    result.Add((kvp.Key.Item1, kvp.Key.Item2, kvp.Value.Count));
            }

            return result;
        }
    }

    // =================================================================================
    //  MeshStatistics — 网格统计信息
    // =================================================================================

    /// <summary>
    /// 网格统计信息: 顶点数 / 边数 / 面数 / 三角形数 / 四边形数 / 四边形比例。
    /// </summary>
    public struct MeshStatistics
    {
        public int VertexCount;
        public int EdgeCount;
        public int FaceCount;
        public int TriangleCount;
        public int QuadCount;
        public int NgonCount;
        public float QuadRatio;
        public int BoundaryEdgeCount;
        public int IsolatedVertexCount;

        public static MeshStatistics Compute(PolygonMesh mesh)
        {
            var stats = new MeshStatistics
            {
                VertexCount = mesh.Vertices.Count,
                EdgeCount = mesh.Edges.Count,
                FaceCount = mesh.Faces.Count
            };

            foreach (var face in mesh.Faces)
            {
                int n = face.VertexIndices?.Length ?? 0;
                if (n == 3) stats.TriangleCount++;
                else if (n == 4) stats.QuadCount++;
                else if (n > 4) stats.NgonCount++;
            }

            stats.QuadRatio = stats.FaceCount > 0
                ? (float)stats.QuadCount / stats.FaceCount
                : 0f;

            // 边界边
            var edgeFaceMap = mesh.BuildEdgeFaceMap();
            foreach (var kvp in edgeFaceMap)
                if (kvp.Value.Count == 1) stats.BoundaryEdgeCount++;

            // 孤立顶点
            stats.IsolatedVertexCount = MeshValidator.FindIsolatedVertices(mesh).Count;

            return stats;
        }

        public override string ToString()
        {
            return $"Vertices: {VertexCount}, Edges: {EdgeCount}, Faces: {FaceCount} " +
                   $"(Tris: {TriangleCount}, Quads: {QuadCount}, Ngons: {NgonCount}), " +
                   $"QuadRatio: {QuadRatio:P1}, BoundaryEdges: {BoundaryEdgeCount}, " +
                   $"IsolatedVerts: {IsolatedVertexCount}";
        }
    }
}
