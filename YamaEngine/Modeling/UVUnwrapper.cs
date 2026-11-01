using System.Numerics;

namespace YamaEngine.Modeling
{
    public enum UvProjectionAxis
    {
        X,
        Y,
        Z
    }

    public static class UVUnwrapper
    {
        public static void ProjectPlanar(EditableMesh mesh, UvProjectionAxis axis = UvProjectionAxis.Y)
        {
            EnsureUvCount(mesh);
            if (mesh.Vertices.Count == 0) return;

            var projected = new List<Vector2>(mesh.Vertices.Count);
            foreach (var vertex in mesh.Vertices)
            {
                projected.Add(axis switch
                {
                    UvProjectionAxis.X => new Vector2(vertex.Z, vertex.Y),
                    UvProjectionAxis.Y => new Vector2(vertex.X, vertex.Z),
                    _ => new Vector2(vertex.X, vertex.Y)
                });
            }

            NormalizeToUnitSquare(mesh, projected);
        }

        public static void ProjectBox(EditableMesh mesh)
        {
            EnsureUvCount(mesh);
            if (mesh.Vertices.Count == 0) return;

            var uvSum = Enumerable.Repeat(Vector2.Zero, mesh.Vertices.Count).ToArray();
            var uvWeight = new int[mesh.Vertices.Count];

            foreach (var face in mesh.Faces)
            {
                if (!face.IsValidFor(mesh.Vertices.Count)) continue;
                var normal = FaceNormal(mesh, face);
                var axis = DominantAxis(normal);
                foreach (var index in new[] { face.A, face.B, face.C })
                {
                    uvSum[index] += ProjectVertex(mesh.Vertices[index], axis);
                    uvWeight[index]++;
                }
            }

            var projected = new List<Vector2>(mesh.Vertices.Count);
            for (var i = 0; i < mesh.Vertices.Count; i++)
            {
                projected.Add(uvWeight[i] > 0 ? uvSum[i] / uvWeight[i] : Vector2.Zero);
            }

            NormalizeToUnitSquare(mesh, projected);
        }

        public static void ProjectSpherical(EditableMesh mesh)
        {
            EnsureUvCount(mesh);
            if (mesh.Vertices.Count == 0) return;

            var center = Vector3.Zero;
            foreach (var vertex in mesh.Vertices) center += vertex;
            center /= mesh.Vertices.Count;

            for (var i = 0; i < mesh.Vertices.Count; i++)
            {
                var direction = mesh.Vertices[i] - center;
                if (direction.LengthSquared() < 0.000001f)
                {
                    mesh.UV0[i] = new Vector2(0.5f, 0.5f);
                    continue;
                }

                direction = Vector3.Normalize(direction);
                var u = 0.5f + MathF.Atan2(direction.Z, direction.X) / (2f * MathF.PI);
                var v = 0.5f - MathF.Asin(Math.Clamp(direction.Y, -1f, 1f)) / MathF.PI;
                mesh.UV0[i] = new Vector2(Clamp01(u), Clamp01(v));
            }
        }

        public static IReadOnlyList<int> MarkSeamBySharpAngle(EditableMesh mesh, float angleDegrees)
        {
            var edgeNormals = new Dictionary<(int, int), List<Vector3>>();
            foreach (var face in mesh.Faces)
            {
                if (!face.IsValidFor(mesh.Vertices.Count)) continue;
                var normal = FaceNormal(mesh, face);
                AddEdgeNormal(edgeNormals, face.A, face.B, normal);
                AddEdgeNormal(edgeNormals, face.B, face.C, normal);
                AddEdgeNormal(edgeNormals, face.C, face.A, normal);
            }

            var seamVertices = new HashSet<int>();
            var threshold = MathF.Cos(MathF.PI * angleDegrees / 180f);
            foreach (var pair in edgeNormals)
            {
                if (pair.Value.Count < 2) continue;
                var dot = Vector3.Dot(Vector3.Normalize(pair.Value[0]), Vector3.Normalize(pair.Value[1]));
                if (dot < threshold)
                {
                    seamVertices.Add(pair.Key.Item1);
                    seamVertices.Add(pair.Key.Item2);
                }
            }

            return seamVertices.OrderBy(v => v).ToArray();
        }

        // ──────────────────────────────────────────────────────────────
        // LSCM (Least Squares Conformal Maps) 自动 UV 展开
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 使用 LSCM 算法自动展开 UV。
        /// 1. 沿尖锐边切缝生成 chart
        /// 2. 每个 chart 内 pin 两个顶点, 求解最小二乘共形映射
        /// 3. 将各 chart UV 打包到 [0,1]x[0,1] 空间
        /// </summary>
        public static void UnwrapLSCM(EditableMesh mesh, float seamAngleDegrees = 40f)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            if (mesh.Faces.Count == 0 || mesh.Vertices.Count == 0) return;

            EnsureUvCount(mesh);

            // 1. 计算缝边 (锐角边 + 边界边)
            var seamEdges = FindSeamEdges(mesh, seamAngleDegrees);

            // 2. 构建 chart (面级别 flood fill)
            var charts = BuildCharts(mesh, seamEdges);

            // 3. 对每个 chart 求解 LSCM 并打包
            float packOffsetX = 0f;
            float packOffsetY = 0f;
            float packRowHeight = 0f;
            float packMaxX = 0.95f;
            float chartScale = (float)(1.0 / Math.Sqrt(Math.Max(1, charts.Count)));

            foreach (var chart in charts)
            {
                if (chart.Faces.Count == 0) continue;
                var (uvMin, uvMax) = SolveLSCMChart(mesh, chart);

                float sx = uvMax.X - uvMin.X > 0.0001f ? 1f / (uvMax.X - uvMin.X) : 1f;
                float sy = uvMax.Y - uvMin.Y > 0.0001f ? 1f / (uvMax.Y - uvMin.Y) : 1f;
                float scale = Math.Min(sx, sy) * chartScale;
                float w = (uvMax.X - uvMin.X) * scale;
                float h = (uvMax.Y - uvMin.Y) * scale;

                if (packOffsetX + w > packMaxX)
                {
                    packOffsetX = 0f;
                    packOffsetY += packRowHeight + 0.02f;
                    packRowHeight = 0f;
                }

                foreach (var vi in chart.VertexSet)
                {
                    var uv = mesh.UV0[vi];
                    mesh.UV0[vi] = new Vector2(
                        (uv.X - uvMin.X) * scale + packOffsetX,
                        (uv.Y - uvMin.Y) * scale + packOffsetY);
                }

                packOffsetX += w + 0.02f;
                packRowHeight = Math.Max(packRowHeight, h);
            }
        }

        /// <summary>查找缝边: 锐角边 + 边界边, 返回无向边集合。</summary>
        private static HashSet<(int, int)> FindSeamEdges(EditableMesh mesh, float angleDegrees)
        {
            var edgeNormals = new Dictionary<(int, int), List<Vector3>>();
            var edgeFaceCount = new Dictionary<(int, int), int>();

            foreach (var face in mesh.Faces)
            {
                if (!face.IsValidFor(mesh.Vertices.Count)) continue;
                var normal = FaceNormal(mesh, face);
                AddEdgeNormal(edgeNormals, face.A, face.B, normal);
                AddEdgeNormal(edgeNormals, face.B, face.C, normal);
                AddEdgeNormal(edgeNormals, face.C, face.A, normal);
                IncEdgeCount(edgeFaceCount, face.A, face.B);
                IncEdgeCount(edgeFaceCount, face.B, face.C);
                IncEdgeCount(edgeFaceCount, face.C, face.A);
            }

            var seams = new HashSet<(int, int)>();
            var threshold = MathF.Cos(MathF.PI * angleDegrees / 180f);

            foreach (var pair in edgeNormals)
            {
                // 边界边 (只被一个面引用) 作为缝
                edgeFaceCount.TryGetValue(pair.Key, out int fc);
                if (fc < 2) { seams.Add(pair.Key); continue; }

                // 锐角边作为缝
                if (pair.Value.Count >= 2)
                {
                    var dot = Vector3.Dot(
                        Vector3.Normalize(pair.Value[0]),
                        Vector3.Normalize(pair.Value[1]));
                    if (dot < threshold) seams.Add(pair.Key);
                }
            }
            return seams;
        }

        private static void IncEdgeCount(Dictionary<(int, int), int> map, int a, int b)
        {
            var key = a < b ? (a, b) : (b, a);
            map.TryGetValue(key, out var c);
            map[key] = c + 1;
        }

        /// <summary>构建 chart: 沿缝边切开后, flood fill 找连通面组。</summary>
        private static List<MeshChart> BuildCharts(EditableMesh mesh, HashSet<(int, int)> seamEdges)
        {
            var charts = new List<MeshChart>();
            var visited = new bool[mesh.Faces.Count];

            var edgeToFaces = new Dictionary<(int, int), List<int>>();
            for (int fi = 0; fi < mesh.Faces.Count; fi++)
            {
                var f = mesh.Faces[fi];
                if (!f.IsValidFor(mesh.Vertices.Count)) continue;
                AddFaceEdge(edgeToFaces, f.A, f.B, fi);
                AddFaceEdge(edgeToFaces, f.B, f.C, fi);
                AddFaceEdge(edgeToFaces, f.C, f.A, fi);
            }

            for (int start = 0; start < mesh.Faces.Count; start++)
            {
                if (visited[start]) continue;
                visited[start] = true;

                var chart = new MeshChart();
                var queue = new Queue<int>();
                queue.Enqueue(start);

                while (queue.Count > 0)
                {
                    int fi = queue.Dequeue();
                    chart.Faces.Add(fi);
                    var f = mesh.Faces[fi];
                    if (!f.IsValidFor(mesh.Vertices.Count)) continue;

                    TryExpand(edgeToFaces, seamEdges, visited, queue, f.A, f.B);
                    TryExpand(edgeToFaces, seamEdges, visited, queue, f.B, f.C);
                    TryExpand(edgeToFaces, seamEdges, visited, queue, f.C, f.A);

                    chart.AddVertex(f.A);
                    chart.AddVertex(f.B);
                    chart.AddVertex(f.C);
                }
                charts.Add(chart);
            }
            return charts;
        }

        private static void AddFaceEdge(Dictionary<(int, int), List<int>> map, int a, int b, int faceIndex)
        {
            var key = a < b ? (a, b) : (b, a);
            if (!map.TryGetValue(key, out var list)) { list = new List<int>(); map[key] = list; }
            list.Add(faceIndex);
        }

        private static void TryExpand(Dictionary<(int, int), List<int>> edgeToFaces,
            HashSet<(int, int)> seamEdges, bool[] visited, Queue<int> queue, int a, int b)
        {
            var key = a < b ? (a, b) : (b, a);
            if (seamEdges.Contains(key)) return;
            if (!edgeToFaces.TryGetValue(key, out var faces)) return;
            foreach (var nf in faces)
                if (!visited[nf]) { visited[nf] = true; queue.Enqueue(nf); }
        }

        /// <summary>求解单个 chart 的 LSCM UV。</summary>
        private static (Vector2 Min, Vector2 Max) SolveLSCMChart(EditableMesh mesh, MeshChart chart)
        {
            var chartVerts = chart.VertexList;
            int n = chartVerts.Count;
            if (n < 3) return (Vector2.Zero, Vector2.One);

            var localIndex = new Dictionary<int, int>(n);
            for (int i = 0; i < n; i++)
                localIndex[chartVerts[i]] = i;

            int rows = 2 * chart.Faces.Count;
            int cols = 2 * n;
            var A = new float[rows, cols];

            for (int fi = 0; fi < chart.Faces.Count; fi++)
            {
                var face = mesh.Faces[chart.Faces[fi]];
                int i0 = localIndex[face.A];
                int i1 = localIndex[face.B];
                int i2 = localIndex[face.C];

                var (x1, y1, x2, y2) = ProjectTriangle2D(
                    mesh.Vertices[face.A], mesh.Vertices[face.B], mesh.Vertices[face.C]);

                int row = 2 * fi;
                // Eq1 (real)
                A[row, 2 * i0] += x1 - x2;     A[row, 2 * i0 + 1] += y2 - y1;
                A[row, 2 * i1] += x2;           A[row, 2 * i1 + 1] += -y2;
                A[row, 2 * i2] += -x1;          A[row, 2 * i2 + 1] += y1;
                // Eq2 (imag)
                A[row + 1, 2 * i0] += y1 - y2;  A[row + 1, 2 * i0 + 1] += x1 - x2;
                A[row + 1, 2 * i1] += y2;        A[row + 1, 2 * i1 + 1] += x2;
                A[row + 1, 2 * i2] += -y1;       A[row + 1, 2 * i2 + 1] += -x1;
            }

            // Pin 两个顶点: 第一个 pin 在 (0,0), 距离最远的 pin 在 (1,0)
            int pin0 = 0;
            int pin1 = FindFarthestVertex(mesh, chartVerts, pin0);
            var pinnedCols = new HashSet<int> { 2 * pin0, 2 * pin0 + 1, 2 * pin1, 2 * pin1 + 1 };

            var b = new float[rows];
            for (int r = 0; r < rows; r++)
            {
                b[r] = -(A[r, 2 * pin1] * 1f); // pin1.u = 1, 其余 pinned = 0
            }

            var freeCols = new List<int>();
            for (int c = 0; c < cols; c++)
                if (!pinnedCols.Contains(c)) freeCols.Add(c);

            int nf = freeCols.Count;
            var Af = new float[rows, nf];
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < nf; c++)
                    Af[r, c] = A[r, freeCols[c]];

            // 正规方程 AtA * x = At * b
            var AtA = new float[nf, nf];
            var Atb = new float[nf];
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < nf; c++)
                {
                    Atb[c] += Af[r, c] * b[r];
                    for (int c2 = c; c2 < nf; c2++)
                        AtA[c, c2] += Af[r, c] * Af[r, c2];
                }
            }
            for (int c = 0; c < nf; c++)
                for (int r = c + 1; r < nf; r++)
                    AtA[r, c] = AtA[c, r];
            for (int c = 0; c < nf; c++)
                AtA[c, c] += 1e-6f;

            var xFree = SolveLinearSystem(AtA, Atb);

            var uv = new float[cols];
            uv[2 * pin1] = 1f; // pin1.u = 1, 其余 pinned = 0
            for (int c = 0; c < nf; c++)
                uv[freeCols[c]] = xFree[c];

            Vector2 min = new(float.MaxValue), max = new(float.MinValue);
            for (int i = 0; i < n; i++)
            {
                var uvCoord = new Vector2(uv[2 * i], uv[2 * i + 1]);
                mesh.UV0[chartVerts[i]] = uvCoord;
                min = Vector2.Min(min, uvCoord);
                max = Vector2.Max(max, uvCoord);
            }
            return (min, max);
        }

        /// <summary>将三角形投影到其所在平面的 2D 坐标, 返回 (x1,y1,x2,y2) 相对于 v0。</summary>
        private static (float x1, float y1, float x2, float y2) ProjectTriangle2D(Vector3 v0, Vector3 v1, Vector3 v2)
        {
            var e1 = v1 - v0;
            var e2 = v2 - v0;
            var normal = Vector3.Cross(e1, e2);
            float len = normal.Length();
            if (len < 1e-9f) return (0f, 0f, 0f, 0f);
            normal /= len;

            var absN = Vector3.Abs(normal);
            Vector3 xAxis, yAxis;
            if (absN.X >= absN.Y && absN.X >= absN.Z) { xAxis = Vector3.UnitY; yAxis = Vector3.UnitZ; }
            else if (absN.Y >= absN.Z) { xAxis = Vector3.UnitZ; yAxis = Vector3.UnitX; }
            else { xAxis = Vector3.UnitX; yAxis = Vector3.UnitY; }

            float x1 = Vector3.Dot(e1, xAxis);
            float y1 = Vector3.Dot(e1, yAxis);
            float x2 = Vector3.Dot(e2, xAxis);
            float y2 = Vector3.Dot(e2, yAxis);

            // 确保正面积 (CCW)
            if (x1 * y2 - x2 * y1 < 0) (y1, y2) = (y2, y1);
            return (x1, y1, x2, y2);
        }

        private static int FindFarthestVertex(EditableMesh mesh, List<int> chartVerts, int vertex0Index)
        {
            var p0 = mesh.Vertices[chartVerts[vertex0Index]];
            float maxDist = -1f;
            int farIdx = vertex0Index;
            for (int i = 0; i < chartVerts.Count; i++)
            {
                var d = Vector3.DistanceSquared(p0, mesh.Vertices[chartVerts[i]]);
                if (d > maxDist) { maxDist = d; farIdx = i; }
            }
            return farIdx;
        }

        /// <summary>高斯消元法求解 A*x = b (部分主元选取)。</summary>
        private static float[] SolveLinearSystem(float[,] A, float[] b)
        {
            int n = b.Length;
            var m = new float[n, n + 1];
            for (int r = 0; r < n; r++)
            {
                for (int c = 0; c < n; c++) m[r, c] = A[r, c];
                m[r, n] = b[r];
            }

            for (int col = 0; col < n; col++)
            {
                int pivot = col;
                float maxVal = Math.Abs(m[col, col]);
                for (int r = col + 1; r < n; r++)
                    if (Math.Abs(m[r, col]) > maxVal) { maxVal = Math.Abs(m[r, col]); pivot = r; }
                if (maxVal < 1e-12f) continue;

                if (pivot != col)
                    for (int c = 0; c <= n; c++)
                        (m[col, c], m[pivot, c]) = (m[pivot, c], m[col, c]);

                for (int r = col + 1; r < n; r++)
                {
                    float factor = m[r, col] / m[col, col];
                    for (int c = col; c <= n; c++)
                        m[r, c] -= factor * m[col, c];
                }
            }

            var x = new float[n];
            for (int i = n - 1; i >= 0; i--)
            {
                float sum = m[i, n];
                for (int j = i + 1; j < n; j++)
                    sum -= m[i, j] * x[j];
                x[i] = Math.Abs(m[i, i]) > 1e-12f ? sum / m[i, i] : 0f;
            }
            return x;
        }

        private static void EnsureUvCount(EditableMesh mesh)
        {
            while (mesh.UV0.Count < mesh.Vertices.Count) mesh.UV0.Add(Vector2.Zero);
            if (mesh.UV0.Count > mesh.Vertices.Count) mesh.UV0.RemoveRange(mesh.Vertices.Count, mesh.UV0.Count - mesh.Vertices.Count);
        }

        private static Vector2 ProjectVertex(Vector3 vertex, UvProjectionAxis axis)
        {
            return axis switch
            {
                UvProjectionAxis.X => new Vector2(vertex.Z, vertex.Y),
                UvProjectionAxis.Y => new Vector2(vertex.X, vertex.Z),
                _ => new Vector2(vertex.X, vertex.Y)
            };
        }

        private static void NormalizeToUnitSquare(EditableMesh mesh, IReadOnlyList<Vector2> projected)
        {
            var min = new Vector2(projected.Min(v => v.X), projected.Min(v => v.Y));
            var max = new Vector2(projected.Max(v => v.X), projected.Max(v => v.Y));
            var size = max - min;
            if (MathF.Abs(size.X) < 0.000001f) size.X = 1f;
            if (MathF.Abs(size.Y) < 0.000001f) size.Y = 1f;

            for (var i = 0; i < projected.Count; i++)
            {
                mesh.UV0[i] = new Vector2(
                    Clamp01((projected[i].X - min.X) / size.X),
                    Clamp01((projected[i].Y - min.Y) / size.Y));
            }
        }

        private static Vector3 FaceNormal(EditableMesh mesh, MeshFace face)
        {
            var a = mesh.Vertices[face.A];
            var b = mesh.Vertices[face.B];
            var c = mesh.Vertices[face.C];
            var normal = Vector3.Cross(b - a, c - a);
            return normal.LengthSquared() > 0.000001f ? Vector3.Normalize(normal) : Vector3.UnitY;
        }

        private static UvProjectionAxis DominantAxis(Vector3 normal)
        {
            var abs = Vector3.Abs(normal);
            if (abs.X >= abs.Y && abs.X >= abs.Z) return UvProjectionAxis.X;
            if (abs.Y >= abs.X && abs.Y >= abs.Z) return UvProjectionAxis.Y;
            return UvProjectionAxis.Z;
        }

        private static void AddEdgeNormal(Dictionary<(int, int), List<Vector3>> map, int a, int b, Vector3 normal)
        {
            var key = a < b ? (a, b) : (b, a);
            if (!map.TryGetValue(key, out var normals))
            {
                normals = new List<Vector3>();
                map[key] = normals;
            }
            normals.Add(normal);
        }

        private static float Clamp01(float value)
        {
            return Math.Clamp(value, 0f, 1f);
        }
    }

    /// <summary>UV 展开 chart: 一组连通的面及其顶点。</summary>
    public sealed class MeshChart
    {
        public List<int> Faces { get; } = new();
        private readonly HashSet<int> _vertexSet = new();
        private List<int>? _vertexList;

        public IReadOnlyCollection<int> VertexSet => _vertexSet;
        public List<int> VertexList => _vertexList ??= _vertexSet.ToList();

        public void AddVertex(int index) => _vertexSet.Add(index);
    }
}
