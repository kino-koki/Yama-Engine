using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using YamaEngine.RHI;

namespace YamaEngine.MicroCluster
{
    /// <summary>
    /// MicroCluster聚类器 — 将网格三角形分组成簇，用于LOD层级构建。
    /// 基于相似三角形法线和面积的分组算法。
    /// </summary>
    public class MicroClusterClusterizer : IDisposable
    {
        public int TargetTrianglesPerCluster { get; set; } = 128;
        public float NormalThreshold { get; set; } = 0.95f;
        public float MaxEdgeLengthRatio { get; set; } = 2.0f;

        /// <summary>每个聚类的最大三角形数。</summary>
        public int MaxTrianglesPerCluster
        {
            get => TargetTrianglesPerCluster;
            set => TargetTrianglesPerCluster = value;
        }

        private readonly RHIDevice? _device;

        public MicroClusterClusterizer() { }

        public MicroClusterClusterizer(RHIDevice device)
        {
            _device = device;
        }

        public void Dispose() { }

        public List<MicroClusterCluster> Clusterize(Mesh mesh)
        {
            var triangles = ExtractTriangles(mesh);
            var clusters = new List<MicroClusterCluster>();

            // 贪心分簇
            var remaining = new HashSet<int>(Enumerable.Range(0, triangles.Count));
            while (remaining.Count > 0)
            {
                var cluster = new MicroClusterCluster { LodLevel = 0 };
                var seed = remaining.First();
                var seedTri = triangles[seed];
                cluster.Triangles.Add(seedTri);
                remaining.Remove(seed);

                foreach (var idx in remaining.ToList())
                {
                    if (cluster.Triangles.Count >= TargetTrianglesPerCluster) break;
                    var tri = triangles[idx];
                    if (CanMerge(seedTri, tri))
                    {
                        cluster.Triangles.Add(tri);
                        remaining.Remove(idx);
                    }
                }

                cluster.CalculateBounds();
                cluster.CalculateParentError();
                clusters.Add(cluster);
            }

            return clusters;
        }

        private List<Triangle> ExtractTriangles(Mesh mesh)
        {
            var triangles = new List<Triangle>();
            for (int i = 0; i < mesh.Indices.Count; i += 3)
            {
                var v0 = mesh.Vertices[mesh.Indices[i]];
                var v1 = mesh.Vertices[mesh.Indices[i + 1]];
                var v2 = mesh.Vertices[mesh.Indices[i + 2]];
                triangles.Add(new Triangle(v0, v1, v2, mesh.Indices[i] / 3));
            }
            return triangles;
        }

        private bool CanMerge(Triangle a, Triangle b)
        {
            float dot = Vector3.Dot(a.Normal, b.Normal);
            if (dot < NormalThreshold) return false;

            float areaRatio = Math.Max(a.Area, b.Area) / Math.Max(Math.Min(a.Area, b.Area), 0.0001f);
            if (areaRatio > MaxEdgeLengthRatio) return false;

            // 边界邻接检查
            if (!a.SharesEdge(b)) return false;

            return true;
        }
    }


    public class Triangle
    {
        public Vector3 V0, V1, V2;
        public Vector3 Normal;
        public Vector3 Center;
        public float Area;
        public int Index;

        public Triangle(Vector3 v0, Vector3 v1, Vector3 v2, int index)
        {
            V0 = v0; V1 = v1; V2 = v2; Index = index;
            Normal = Vector3.Normalize(Vector3.Cross(v1 - v0, v2 - v0));
            Center = (v0 + v1 + v2) / 3f;
            var e0 = v1 - v0;
            var e1 = v2 - v0;
            Area = Vector3.Cross(e0, e1).Length() * 0.5f;
        }

        public bool SharesEdge(Triangle other)
        {
            int shared = 0;
            if (IsSameVertex(V0, other.V0) || IsSameVertex(V0, other.V1) || IsSameVertex(V0, other.V2)) shared++;
            if (IsSameVertex(V1, other.V0) || IsSameVertex(V1, other.V1) || IsSameVertex(V1, other.V2)) shared++;
            if (IsSameVertex(V2, other.V0) || IsSameVertex(V2, other.V1) || IsSameVertex(V2, other.V2)) shared++;
            return shared >= 2;
        }

        private static bool IsSameVertex(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 0.001f;
    }


}
