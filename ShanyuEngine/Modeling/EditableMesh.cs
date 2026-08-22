using System.Numerics;
using System.Text.Json;
using ShanyuEngine.Assets;

namespace ShanyuEngine.Modeling
{
    public sealed class EditableMesh
    {
        public int Version { get; set; } = 1;
        public string Guid { get; set; } = System.Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "EditableMesh";
        public List<Vector3> Vertices { get; set; } = new();
        public List<Vector3> Normals { get; set; } = new();
        public List<Vector2> UV0 { get; set; } = new();
        public List<MeshFace> Faces { get; set; } = new();
        public List<MeshBoneWeight> BoneWeights { get; set; } = new();

        /// <summary>选中的顶点索引集合。</summary>
        public HashSet<int> SelectedVertices { get; } = new();
        /// <summary>选中的面索引集合。</summary>
        public HashSet<int> SelectedFaces { get; } = new();
        /// <summary>选中的边索引集合 (基于 BuildEdges 计算的边列表)。</summary>
        public HashSet<int> SelectedEdges { get; } = new();

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            IncludeFields = true,
            PropertyNameCaseInsensitive = true
        };

        public static EditableMesh CreateCube(string name = "EditableCube", float size = 1f)
        {
            var h = size * 0.5f;
            var mesh = new EditableMesh { Name = name };
            mesh.Vertices.AddRange(new[]
            {
                new Vector3(-h, -h, -h),
                new Vector3( h, -h, -h),
                new Vector3( h,  h, -h),
                new Vector3(-h,  h, -h),
                new Vector3(-h, -h,  h),
                new Vector3( h, -h,  h),
                new Vector3( h,  h,  h),
                new Vector3(-h,  h,  h)
            });

            mesh.UV0.AddRange(Enumerable.Repeat(Vector2.Zero, mesh.Vertices.Count));
            mesh.Faces.AddRange(new[]
            {
                new MeshFace(0, 2, 1), new MeshFace(0, 3, 2),
                new MeshFace(4, 5, 6), new MeshFace(4, 6, 7),
                new MeshFace(0, 1, 5), new MeshFace(0, 5, 4),
                new MeshFace(3, 7, 6), new MeshFace(3, 6, 2),
                new MeshFace(1, 2, 6), new MeshFace(1, 6, 5),
                new MeshFace(0, 4, 7), new MeshFace(0, 7, 3)
            });
            mesh.RecalculateNormals();
            return mesh;
        }

        public void RecalculateNormals()
        {
            Normals = Enumerable.Repeat(Vector3.Zero, Vertices.Count).ToList();
            foreach (var face in Faces)
            {
                if (!face.IsValidFor(Vertices.Count)) continue;
                var a = Vertices[face.A];
                var b = Vertices[face.B];
                var c = Vertices[face.C];
                var normal = Vector3.Cross(b - a, c - a);
                if (normal.LengthSquared() > 0.000001f) normal = Vector3.Normalize(normal);
                Normals[face.A] += normal;
                Normals[face.B] += normal;
                Normals[face.C] += normal;
            }

            for (var i = 0; i < Normals.Count; i++)
            {
                Normals[i] = Normals[i].LengthSquared() > 0.000001f ? Vector3.Normalize(Normals[i]) : Vector3.UnitY;
            }
        }

        public List<string> Validate()
        {
            var errors = new List<string>();
            if (Vertices.Count == 0) errors.Add("Mesh has no vertices.");
            for (var i = 0; i < Faces.Count; i++)
            {
                if (!Faces[i].IsValidFor(Vertices.Count)) errors.Add($"Face {i} has an invalid vertex index.");
            }
            if (Normals.Count != 0 && Normals.Count != Vertices.Count) errors.Add("Normal count must match vertex count.");
            if (UV0.Count != 0 && UV0.Count != Vertices.Count) errors.Add("UV0 count must match vertex count.");
            return errors;
        }

        public void Write(string path)
        {
            if (Validate().Count > 0) throw new InvalidOperationException("Cannot write invalid mesh: " + string.Join("; ", Validate()));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
            AssetMeta.FromMetadata(new AssetMetadata
            {
                Guid = System.Guid.Parse(Guid),
                Name = Name,
                AssetPath = ToProjectAssetPath(path),
                SourceFile = path,
                Type = AssetType.Mesh,
                FileSize = new FileInfo(path).Length,
                ImportedAt = DateTime.Now,
                LastModified = File.GetLastWriteTime(path),
                Hash = Guid
            }).Write(path + ".shmeta");
        }

        public static EditableMesh Read(string path)
        {
            var mesh = JsonSerializer.Deserialize<EditableMesh>(File.ReadAllText(path), JsonOptions)
                       ?? throw new InvalidOperationException($"Invalid editable mesh: {path}");
            var errors = mesh.Validate();
            if (errors.Count > 0) throw new InvalidOperationException("Invalid editable mesh: " + string.Join("; ", errors));
            return mesh;
        }

        private static string ToProjectAssetPath(string path)
        {
            var normalized = Path.GetFullPath(path).Replace('\\', '/');
            var marker = "/Assets/";
            var index = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0) return normalized[(index + 1)..];
            return Path.GetFileName(path);
        }

        // ──────────────────────────────────────────────────────────────
        // 选择系统
        // ──────────────────────────────────────────────────────────────

        /// <summary>选中单个顶点。</summary>
        public void SelectVertex(int index, bool additive = false)
        {
            if (!additive) ClearSelection();
            if (index >= 0 && index < Vertices.Count)
                SelectedVertices.Add(index);
        }

        /// <summary>选中单个面。</summary>
        public void SelectFace(int index, bool additive = false)
        {
            if (!additive) ClearSelection();
            if (index >= 0 && index < Faces.Count)
                SelectedFaces.Add(index);
        }

        /// <summary>框选顶点 (在给定的世界空间 AABB 内)。</summary>
        public void SelectVerticesInBox(Vector3 min, Vector3 max, bool additive = false)
        {
            if (!additive) ClearSelection();
            for (int i = 0; i < Vertices.Count; i++)
            {
                var v = Vertices[i];
                if (v.X >= min.X && v.X <= max.X &&
                    v.Y >= min.Y && v.Y <= max.Y &&
                    v.Z >= min.Z && v.Z <= max.Z)
                    SelectedVertices.Add(i);
            }
        }

        /// <summary>取消选中某个顶点。</summary>
        public void DeselectVertex(int index) => SelectedVertices.Remove(index);

        /// <summary>取消选中某个面。</summary>
        public void DeselectFace(int index) => SelectedFaces.Remove(index);

        /// <summary>清空所有选择。</summary>
        public void ClearSelection()
        {
            SelectedVertices.Clear();
            SelectedFaces.Clear();
            SelectedEdges.Clear();
        }

        /// <summary>全选顶点。</summary>
        public void SelectAllVertices()
        {
            SelectedVertices.Clear();
            for (int i = 0; i < Vertices.Count; i++) SelectedVertices.Add(i);
        }

        /// <summary>反选顶点。</summary>
        public void InvertVertexSelection()
        {
            var newSel = new HashSet<int>();
            for (int i = 0; i < Vertices.Count; i++)
                if (!SelectedVertices.Contains(i)) newSel.Add(i);
            SelectedVertices.Clear();
            foreach (var v in newSel) SelectedVertices.Add(v);
        }

        /// <summary>获取选中顶点的包围盒。</summary>
        public (Vector3 Min, Vector3 Max) GetSelectionBounds()
        {
            if (SelectedVertices.Count == 0)
                return (Vector3.Zero, Vector3.Zero);
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            foreach (var idx in SelectedVertices)
            {
                var v = Vertices[idx];
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }
            return (min, max);
        }

        // ──────────────────────────────────────────────────────────────
        // 变换操作 (对选中顶点)
        // ──────────────────────────────────────────────────────────────

        /// <summary>平移选中顶点。</summary>
        public void TranslateSelection(Vector3 translation)
        {
            foreach (var idx in SelectedVertices)
                Vertices[idx] += translation;
            RecalculateNormals();
        }

        /// <summary>绕中心点缩放选中顶点。</summary>
        public void ScaleSelection(Vector3 scaleFactors, Vector3? pivot = null)
        {
            var (min, max) = GetSelectionBounds();
            var center = pivot ?? (min + max) * 0.5f;
            foreach (var idx in SelectedVertices)
                Vertices[idx] = center + (Vertices[idx] - center) * scaleFactors;
            RecalculateNormals();
        }

        /// <summary>绕轴旋转选中顶点。</summary>
        public void RotateSelection(Quaternion rotation, Vector3? pivot = null)
        {
            var (min, max) = GetSelectionBounds();
            var center = pivot ?? (min + max) * 0.5f;
            foreach (var idx in SelectedVertices)
                Vertices[idx] = center + Vector3.Transform(Vertices[idx] - center, rotation);
            RecalculateNormals();
        }

        /// <summary>变换全部顶点 (平移 + 旋转 + 缩放)。</summary>
        public void TransformAll(Vector3 translation, Quaternion rotation, Vector3 scale)
        {
            var matrix = Matrix4x4.CreateScale(scale) *
                         Matrix4x4.CreateFromQuaternion(rotation) *
                         Matrix4x4.CreateTranslation(translation);
            for (int i = 0; i < Vertices.Count; i++)
            {
                var transformed = Vector3.Transform(Vertices[i], matrix);
                Vertices[i] = transformed;
                if (i < Normals.Count)
                    Normals[i] = Vector3.Normalize(Vector3.TransformNormal(Normals[i], matrix));
            }
        }

        // ──────────────────────────────────────────────────────────────
        // 边与半边结构
        // ──────────────────────────────────────────────────────────────

        /// <summary>构建边列表 (去重, 每条无向边只出现一次)。</summary>
        public List<MeshEdge> BuildEdges()
        {
            var edgeSet = new Dictionary<(int, int), int>();
            var edges = new List<MeshEdge>();
            foreach (var face in Faces)
            {
                if (!face.IsValidFor(Vertices.Count)) continue;
                AddEdgeUnique(edgeSet, edges, face.A, face.B);
                AddEdgeUnique(edgeSet, edges, face.B, face.C);
                AddEdgeUnique(edgeSet, edges, face.C, face.A);
            }
            return edges;
        }

        private static void AddEdgeUnique(Dictionary<(int, int), int> edgeSet, List<MeshEdge> edges, int a, int b)
        {
            var key = a < b ? (a, b) : (b, a);
            if (edgeSet.ContainsKey(key)) return;
            edgeSet[key] = edges.Count;
            edges.Add(new MeshEdge(a, b));
        }

        /// <summary>构建顶点邻接表 (每个顶点连接的顶点集合)。</summary>
        public List<HashSet<int>> BuildVertexAdjacency()
        {
            var adjacency = new List<HashSet<int>>(Vertices.Count);
            for (int i = 0; i < Vertices.Count; i++) adjacency.Add(new HashSet<int>());
            foreach (var face in Faces)
            {
                if (!face.IsValidFor(Vertices.Count)) continue;
                adjacency[face.A].Add(face.B); adjacency[face.B].Add(face.A);
                adjacency[face.B].Add(face.C); adjacency[face.C].Add(face.B);
                adjacency[face.C].Add(face.A); adjacency[face.A].Add(face.C);
            }
            return adjacency;
        }

        /// <summary>构建半边数据结构, 用于高效拓扑查询。</summary>
        public HalfEdgeStructure BuildHalfEdgeStructure()
        {
            return HalfEdgeStructure.BuildFromMesh(this);
        }

        /// <summary>查找边界边 (只被一个面引用的边)。</summary>
        public List<MeshEdge> FindBoundaryEdges()
        {
            var edgeFaceCount = new Dictionary<(int, int), int>();
            foreach (var face in Faces)
            {
                if (!face.IsValidFor(Vertices.Count)) continue;
                IncrementEdgeCount(edgeFaceCount, face.A, face.B);
                IncrementEdgeCount(edgeFaceCount, face.B, face.C);
                IncrementEdgeCount(edgeFaceCount, face.C, face.A);
            }
            var boundary = new List<MeshEdge>();
            foreach (var kvp in edgeFaceCount)
            {
                if (kvp.Value == 1)
                    boundary.Add(new MeshEdge(kvp.Key.Item1, kvp.Key.Item2));
            }
            return boundary;
        }

        private static void IncrementEdgeCount(Dictionary<(int, int), int> map, int a, int b)
        {
            var key = a < b ? (a, b) : (b, a);
            map.TryGetValue(key, out var count);
            map[key] = count + 1;
        }

        /// <summary>合并重复顶点 (距离小于 threshold 的顶点合并为一个)。</summary>
        public int MergeDuplicateVertices(float threshold = 0.0001f)
        {
            var remap = new int[Vertices.Count];
            for (int i = 0; i < remap.Length; i++) remap[i] = i;
            float sqThreshold = threshold * threshold;

            for (int i = 0; i < Vertices.Count; i++)
            {
                if (remap[i] != i) continue;
                for (int j = i + 1; j < Vertices.Count; j++)
                {
                    if (remap[j] != j) continue;
                    if (Vector3.DistanceSquared(Vertices[i], Vertices[j]) < sqThreshold)
                        remap[j] = i;
                }
            }

            // 构建新顶点列表
            var newVertices = new List<Vector3>();
            var oldToNew = new int[Vertices.Count];
            for (int i = 0; i < Vertices.Count; i++)
            {
                if (remap[i] == i)
                {
                    oldToNew[i] = newVertices.Count;
                    newVertices.Add(Vertices[i]);
                }
                else
                {
                    oldToNew[i] = oldToNew[remap[i]];
                }
            }

            int removedCount = Vertices.Count - newVertices.Count;
            Vertices = newVertices;

            // 重映射法线和 UV
            if (Normals.Count == remap.Length)
            {
                var newNormals = new List<Vector3>(newVertices.Count);
                for (int i = 0; i < remap.Length; i++)
                    if (remap[i] == i) newNormals.Add(Normals[i]);
                Normals = newNormals;
            }
            if (UV0.Count == remap.Length)
            {
                var newUVs = new List<Vector2>(newVertices.Count);
                for (int i = 0; i < remap.Length; i++)
                    if (remap[i] == i) newUVs.Add(UV0[i]);
                UV0 = newUVs;
            }

            // 重映射面索引
            for (int i = 0; i < Faces.Count; i++)
            {
                var f = Faces[i];
                Faces[i] = new MeshFace(oldToNew[f.A], oldToNew[f.B], oldToNew[f.C]);
            }

            RecalculateNormals();
            return removedCount;
        }
    }

    public readonly record struct MeshFace(int A, int B, int C)
    {
        public bool IsValidFor(int vertexCount)
        {
            return A >= 0 && B >= 0 && C >= 0 && A < vertexCount && B < vertexCount && C < vertexCount && A != B && B != C && A != C;
        }
    }

    public readonly record struct MeshBoneWeight(int Bone0, int Bone1, int Bone2, int Bone3, float Weight0, float Weight1, float Weight2, float Weight3);

    /// <summary>无向边 (两个顶点索引)。</summary>
    public readonly record struct MeshEdge(int A, int B)
    {
        /// <summary>判断顶点是否属于此边。</summary>
        public bool Contains(int vertex) => A == vertex || B == vertex;
        /// <summary>获取边的另一端顶点。</summary>
        public int Other(int vertex) => vertex == A ? B : A;
        /// <summary>边长度。</summary>
        public float Length(IReadOnlyList<Vector3> vertices) =>
            A >= 0 && B >= 0 && A < vertices.Count && B < vertices.Count
                ? Vector3.Distance(vertices[A], vertices[B]) : 0f;
    }

    /// <summary>
    /// 半边数据结构: 为网格构建高效的拓扑查询。
    /// 每条有向半边记录起始顶点、所属面、配对半边和下一条半边。
    /// </summary>
    public sealed class HalfEdgeStructure
    {
        /// <summary>所有半边。</summary>
        public List<HalfEdge> HalfEdges { get; } = new();
        /// <summary>顶点到出发半边的映射。</summary>
        public Dictionary<int, int> VertexToHalfEdge { get; } = new();
        /// <summary>面到第一条半边的映射。</summary>
        public Dictionary<int, int> FaceToHalfEdge { get; } = new();
        /// <summary>边 (无向) 到半边对的映射。</summary>
        public Dictionary<(int, int), (int, int)> EdgeToHalfEdges { get; } = new();

        private HalfEdgeStructure() { }

        /// <summary>从 EditableMesh 构建半边结构。</summary>
        public static HalfEdgeStructure BuildFromMesh(EditableMesh mesh)
        {
            var hes = new HalfEdgeStructure();

            for (int faceIdx = 0; faceIdx < mesh.Faces.Count; faceIdx++)
            {
                var face = mesh.Faces[faceIdx];
                if (!face.IsValidFor(mesh.Vertices.Count)) continue;

                int[] verts = { face.A, face.B, face.C };
                int[] heIndices = new int[3];

                for (int i = 0; i < 3; i++)
                {
                    int from = verts[i];
                    int to = verts[(i + 1) % 3];
                    var key = from < to ? (from, to) : (to, from);

                    int heIdx = hes.HalfEdges.Count;
                    var he = new HalfEdge
                    {
                        Vertex = from,
                        Face = faceIdx,
                        Next = -1,
                        Twin = -1
                    };
                    hes.HalfEdges.Add(he);
                    heIndices[i] = heIdx;

                    // 设置顶点映射
                    if (!hes.VertexToHalfEdge.ContainsKey(from))
                        hes.VertexToHalfEdge[from] = heIdx;

                    // 记录边
                    if (!hes.EdgeToHalfEdges.ContainsKey(key))
                    {
                        hes.EdgeToHalfEdges[key] = (heIdx, -1);
                    }
                    else
                    {
                        var existing = hes.EdgeToHalfEdges[key];
                        hes.EdgeToHalfEdges[key] = (existing.Item1, heIdx);
                        // 设置 twin
                        hes.HalfEdges[existing.Item1].Twin = heIdx;
                        hes.HalfEdges[heIdx].Twin = existing.Item1;
                    }
                }

                // 设置 next (环内循环)
                hes.HalfEdges[heIndices[0]].Next = heIndices[1];
                hes.HalfEdges[heIndices[1]].Next = heIndices[2];
                hes.HalfEdges[heIndices[2]].Next = heIndices[0];

                hes.FaceToHalfEdge[faceIdx] = heIndices[0];
            }

            return hes;
        }

        /// <summary>获取顶点的所有邻接顶点。</summary>
        public List<int> GetVertexNeighbors(int vertex)
        {
            var neighbors = new List<int>();
            if (!VertexToHalfEdge.TryGetValue(vertex, out int startHe)) return neighbors;
            int current = startHe;
            do
            {
                var he = HalfEdges[current];
                if (he.Twin >= 0)
                {
                    var twinHe = HalfEdges[he.Twin];
                    neighbors.Add(twinHe.Vertex);
                    current = he.Twin;
                    current = HalfEdges[current].Next;
                }
                else break;
            } while (current != startHe && current >= 0 && neighbors.Count < HalfEdges.Count);
            return neighbors;
        }

        /// <summary>获取面周围的所有半边 (按顺序)。</summary>
        public List<int> GetFaceHalfEdges(int faceIndex)
        {
            var result = new List<int>();
            if (!FaceToHalfEdge.TryGetValue(faceIndex, out int startHe)) return result;
            int current = startHe;
            do
            {
                result.Add(current);
                current = HalfEdges[current].Next;
            } while (current != startHe && current >= 0 && result.Count < HalfEdges.Count);
            return result;
        }

        /// <summary>判断边是否为边界边 (没有 twin)。</summary>
        public bool IsBoundaryEdge(int halfEdgeIndex)
        {
            return halfEdgeIndex >= 0 && halfEdgeIndex < HalfEdges.Count &&
                   HalfEdges[halfEdgeIndex].Twin < 0;
        }
    }

    /// <summary>半边: 有向边, 从 Vertex 指向下一个顶点。</summary>
    public class HalfEdge
    {
        /// <summary>起始顶点索引。</summary>
        public int Vertex;
        /// <summary>所属面索引。</summary>
        public int Face;
        /// <summary>配对半边 (反方向) 索引, -1 表示边界。</summary>
        public int Twin = -1;
        /// <summary>同面内下一条半边索引。</summary>
        public int Next = -1;
    }
}
