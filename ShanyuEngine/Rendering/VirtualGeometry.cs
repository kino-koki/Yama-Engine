using System.Numerics;
using ShanyuEngine.Modeling;

namespace ShanyuEngine.Rendering
{
    public readonly record struct BoundingSphere(Vector3 Center, float Radius);

    public sealed class VirtualGeometryAsset
    {
        public string MeshAssetPath { get; init; } = string.Empty;
        public IReadOnlyList<VirtualGeometryCluster> Clusters { get; init; } = Array.Empty<VirtualGeometryCluster>();
    }

    public sealed class VirtualGeometryCluster
    {
        public int Id { get; init; }
        public int LodLevel { get; init; }
        public int TriangleCount { get; init; }
        public int FirstTriangle { get; init; }
        public BoundingSphere Bounds { get; init; }
        public float GeometricError { get; init; }
        public Vector2 ScreenMin { get; init; }
        public Vector2 ScreenMax { get; init; }
        public float Depth { get; init; }
    }

    public static class VirtualGeometryBuilder
    {
        public static VirtualGeometryAsset Build(EditableMesh mesh, string meshAssetPath, int maxTrianglesPerCluster = 64)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            if (maxTrianglesPerCluster <= 0) throw new ArgumentOutOfRangeException(nameof(maxTrianglesPerCluster));

            var clusters = new List<VirtualGeometryCluster>();
            var clusterId = 0;
            for (var first = 0; first < mesh.Faces.Count; first += maxTrianglesPerCluster)
            {
                var count = Math.Min(maxTrianglesPerCluster, mesh.Faces.Count - first);
                clusters.Add(CreateCluster(mesh, clusterId++, 0, first, count));
            }

            if (clusters.Count > 1)
            {
                var parent = CreateParentCluster(mesh, clusterId, clusters);
                clusters.Add(parent);
            }

            return new VirtualGeometryAsset
            {
                MeshAssetPath = meshAssetPath ?? string.Empty,
                Clusters = clusters
            };
        }

        private static VirtualGeometryCluster CreateCluster(EditableMesh mesh, int id, int lodLevel, int firstTriangle, int triangleCount)
        {
            var used = new List<Vector3>();
            for (var i = firstTriangle; i < firstTriangle + triangleCount && i < mesh.Faces.Count; i++)
            {
                var face = mesh.Faces[i];
                if (face.IsValidFor(mesh.Vertices.Count))
                {
                    used.Add(mesh.Vertices[face.A]);
                    used.Add(mesh.Vertices[face.B]);
                    used.Add(mesh.Vertices[face.C]);
                }
            }

            var sphere = ComputeSphere(used);
            var depth = Math.Clamp(0.1f + id * 0.35f, 0.05f, 0.95f);
            return new VirtualGeometryCluster
            {
                Id = id,
                LodLevel = lodLevel,
                FirstTriangle = firstTriangle,
                TriangleCount = triangleCount,
                Bounds = sphere,
                GeometricError = Math.Max(0.01f, sphere.Radius / (lodLevel + 1)),
                ScreenMin = new Vector2(0.25f, 0.25f),
                ScreenMax = new Vector2(0.75f, 0.75f),
                Depth = depth
            };
        }

        private static VirtualGeometryCluster CreateParentCluster(EditableMesh mesh, int id, IReadOnlyList<VirtualGeometryCluster> children)
        {
            var center = Vector3.Zero;
            foreach (var c in children) center += c.Bounds.Center;
            center /= Math.Max(1, children.Count);

            var radius = 0.01f;
            foreach (var c in children)
            {
                radius = Math.Max(radius, Vector3.Distance(center, c.Bounds.Center) + c.Bounds.Radius);
            }

            return new VirtualGeometryCluster
            {
                Id = id,
                LodLevel = 1,
                FirstTriangle = 0,
                TriangleCount = Math.Max(1, mesh.Faces.Count / 2),
                Bounds = new BoundingSphere(center, radius),
                GeometricError = radius * 0.25f,
                ScreenMin = new Vector2(0.25f, 0.25f),
                ScreenMax = new Vector2(0.75f, 0.75f),
                Depth = 0.10f
            };
        }

        private static BoundingSphere ComputeSphere(IReadOnlyList<Vector3> points)
        {
            if (points.Count == 0) return new BoundingSphere(Vector3.Zero, 0.01f);

            var center = Vector3.Zero;
            foreach (var p in points) center += p;
            center /= points.Count;

            var radius = 0.01f;
            foreach (var p in points) radius = Math.Max(radius, Vector3.Distance(center, p));
            return new BoundingSphere(center, radius);
        }
    }

    public sealed class VirtualGeometrySelection
    {
        public IReadOnlyList<VirtualGeometryCluster> SelectedClusters { get; init; } = Array.Empty<VirtualGeometryCluster>();
        public int EstimatedTriangleCount { get; init; }
        public int LodLevel { get; init; }
    }

    public static class VirtualGeometryLodSelector
    {
        public static VirtualGeometrySelection Select(VirtualGeometryAsset asset, float cameraDistance, float errorThresholdPixels, int viewportHeight)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            cameraDistance = Math.Max(0.001f, cameraDistance);
            viewportHeight = Math.Max(1, viewportHeight);

            var lod0 = asset.Clusters.Where(c => c.LodLevel == 0).ToList();
            var lower = asset.Clusters.Where(c => c.LodLevel > 0).OrderBy(c => c.LodLevel).ToList();
            var maxError = lod0.Count == 0 ? 0f : lod0.Max(c => ProjectError(c.GeometricError, cameraDistance, viewportHeight));

            IReadOnlyList<VirtualGeometryCluster> selected;
            var lodLevel = 0;
            if (lower.Count > 0 && maxError <= errorThresholdPixels)
            {
                selected = lower.Where(c => c.LodLevel == lower[0].LodLevel).ToList();
                lodLevel = lower[0].LodLevel;
            }
            else
            {
                selected = lod0;
            }

            return new VirtualGeometrySelection
            {
                SelectedClusters = selected,
                EstimatedTriangleCount = selected.Sum(c => c.TriangleCount),
                LodLevel = lodLevel
            };
        }

        private static float ProjectError(float geometricError, float distance, int viewportHeight)
        {
            return geometricError / distance * viewportHeight;
        }
    }

    public sealed class VirtualGeometryDrawList
    {
        public IReadOnlyList<GpuDrawCommand> Commands { get; init; } = Array.Empty<GpuDrawCommand>();
        public int InputClusterCount { get; init; }
        public int OccludedClusterCount { get; init; }
        public int VisibleClusterCount => Commands.Count;
    }

    public static class VirtualGeometryDrawListBuilder
    {
        public static VirtualGeometryDrawList Build(VirtualGeometrySelection selection, HzbOcclusionBuffer? hzb, string materialAssetPath)
        {
            if (selection == null) throw new ArgumentNullException(nameof(selection));

            var input = 0;
            var occluded = 0;
            var commands = new List<GpuDrawCommand>();
            foreach (var cluster in selection.SelectedClusters.OrderBy(c => c.Id))
            {
                input++;
                if (hzb != null && hzb.IsOccluded(cluster.ScreenMin, cluster.ScreenMax, cluster.Depth))
                {
                    occluded++;
                    continue;
                }

                commands.Add(new GpuDrawCommand
                {
                    Name = $"Cluster:{cluster.Id}:LOD{cluster.LodLevel}",
                    MeshAssetPath = $"cluster://{cluster.Id}",
                    MaterialAssetPath = materialAssetPath ?? string.Empty,
                    InstanceCount = 1,
                    SortKey = ((ulong)cluster.LodLevel << 32) | (uint)cluster.Id
                });
            }

            return new VirtualGeometryDrawList
            {
                Commands = commands,
                InputClusterCount = input,
                OccludedClusterCount = occluded
            };
        }
    }
}
