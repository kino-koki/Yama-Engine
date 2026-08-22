using System.Numerics;

namespace ShanyuEngine.Modeling
{
    public static class SculptTool
    {
        public static SculptResult Apply(EditableMesh mesh, SculptBrushSettings settings)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            settings.Validate();
            if (mesh.Vertices.Count == 0) return SculptResult.Empty;

            mesh.RecalculateNormals();
            var original = mesh.Vertices.ToArray();
            var adjacency = BuildAdjacency(mesh);
            var affectedVertices = 0;
            var totalDisplacement = 0f;
            var maxDisplacement = 0f;

            for (var i = 0; i < mesh.Vertices.Count; i++)
            {
                var distance = BrushPlaneDistance(original[i], settings.Center);
                if (distance > settings.Radius) continue;

                var falloff = EvaluateFalloff(distance / settings.Radius, settings.Falloff);
                var mask = settings.Mask?.GetWeight(i) ?? 1f;
                var weight = falloff * settings.Strength * mask;
                if (weight <= 0.000001f) continue;

                var before = mesh.Vertices[i];
                var after = settings.Type switch
                {
                    SculptBrushType.Inflate => before + SafeNormal(mesh.Normals, i) * weight,
                    SculptBrushType.Draw => before + settings.DirectionOrNormal(SafeNormal(mesh.Normals, i)) * weight,
                    SculptBrushType.Grab => before + settings.DirectionOrDefault(Vector3.UnitY) * weight,
                    SculptBrushType.Smooth => Vector3.Lerp(before, AverageNeighborPosition(original, adjacency, i), Math.Clamp(weight, 0f, 1f)),
                    SculptBrushType.Flatten => Flatten(before, settings.Center, settings.DirectionOrDefault(Vector3.UnitZ), weight),
                    SculptBrushType.Pinch => Pinch(before, settings.Center, weight),
                    _ => before
                };

                mesh.Vertices[i] = after;
                var displacement = Vector3.Distance(before, after);
                if (displacement > 0.000001f)
                {
                    affectedVertices++;
                    totalDisplacement += displacement;
                    maxDisplacement = MathF.Max(maxDisplacement, displacement);
                }
            }

            mesh.RecalculateNormals();
            return new SculptResult(affectedVertices, totalDisplacement, maxDisplacement);
        }

        private static float EvaluateFalloff(float normalizedDistance, SculptFalloff falloff)
        {
            var t = Math.Clamp(normalizedDistance, 0f, 1f);
            return falloff switch
            {
                SculptFalloff.Linear => 1f - t,
                SculptFalloff.Smooth => 1f - (t * t * (3f - 2f * t)),
                SculptFalloff.Sharp => MathF.Pow(1f - t, 2f),
                SculptFalloff.Constant => 1f,
                _ => 1f - t
            };
        }

        private static float BrushPlaneDistance(Vector3 position, Vector3 center)
        {
            var dx = position.X - center.X;
            var dy = position.Y - center.Y;
            return MathF.Sqrt(dx * dx + dy * dy);
        }

        private static Vector3 Flatten(Vector3 position, Vector3 planePoint, Vector3 planeNormal, float weight)
        {
            var normal = Vector3.Normalize(planeNormal.LengthSquared() < 0.000001f ? Vector3.UnitZ : planeNormal);
            var signedDistance = Vector3.Dot(position - planePoint, normal);
            var projected = position - normal * signedDistance;
            return Vector3.Lerp(position, projected, Math.Clamp(weight, 0f, 1f));
        }

        /// <summary>Pinch 笔刷: 将顶点向笔刷中心轴 (XY 平面投影中心) 拉拢, 产生收缩捏合效果。</summary>
        private static Vector3 Pinch(Vector3 position, Vector3 center, float weight)
        {
            // 在 XY 平面内将顶点向中心拉拢, 保持 Z 不变
            var target = new Vector3(center.X, center.Y, position.Z);
            return Vector3.Lerp(position, target, Math.Clamp(weight, 0f, 1f));
        }

        private static Vector3 AverageNeighborPosition(IReadOnlyList<Vector3> vertices, IReadOnlyList<HashSet<int>> adjacency, int index)
        {
            if (index < 0 || index >= adjacency.Count || adjacency[index].Count == 0) return vertices[index];
            var sum = Vector3.Zero;
            foreach (var neighbor in adjacency[index]) sum += vertices[neighbor];
            return sum / adjacency[index].Count;
        }

        private static List<HashSet<int>> BuildAdjacency(EditableMesh mesh)
        {
            var adjacency = Enumerable.Range(0, mesh.Vertices.Count).Select(_ => new HashSet<int>()).ToList();
            foreach (var face in mesh.Faces)
            {
                if (!face.IsValidFor(mesh.Vertices.Count)) continue;
                AddNeighbor(adjacency, face.A, face.B);
                AddNeighbor(adjacency, face.B, face.C);
                AddNeighbor(adjacency, face.C, face.A);
            }
            return adjacency;
        }

        private static void AddNeighbor(List<HashSet<int>> adjacency, int a, int b)
        {
            adjacency[a].Add(b);
            adjacency[b].Add(a);
        }

        private static Vector3 SafeNormal(IReadOnlyList<Vector3> normals, int index)
        {
            if (index >= 0 && index < normals.Count && normals[index].LengthSquared() > 0.000001f)
            {
                return Vector3.Normalize(normals[index]);
            }
            return Vector3.UnitZ;
        }
    }

    public sealed class SculptBrushSettings
    {
        public SculptBrushType Type { get; set; } = SculptBrushType.Draw;
        public Vector3 Center { get; set; } = Vector3.Zero;
        public Vector3 Direction { get; set; } = Vector3.Zero;
        public float Radius { get; set; } = 1f;
        public float Strength { get; set; } = 0.25f;
        public SculptFalloff Falloff { get; set; } = SculptFalloff.Smooth;
        public SculptMask? Mask { get; set; }

        public void Validate()
        {
            if (Radius <= 0f) throw new ArgumentOutOfRangeException(nameof(Radius), "Sculpt radius must be positive.");
            if (Strength < 0f) throw new ArgumentOutOfRangeException(nameof(Strength), "Sculpt strength must be >= 0.");
        }

        public Vector3 DirectionOrDefault(Vector3 fallback)
        {
            return Direction.LengthSquared() > 0.000001f ? Vector3.Normalize(Direction) : fallback;
        }

        public Vector3 DirectionOrNormal(Vector3 normal)
        {
            return Direction.LengthSquared() > 0.000001f ? Vector3.Normalize(Direction) : normal;
        }
    }

    public sealed class SculptMask
    {
        private readonly Dictionary<int, float> _weights = new();

        public void SetWeight(int vertexIndex, float weight)
        {
            _weights[vertexIndex] = Math.Clamp(weight, 0f, 1f);
        }

        public float GetWeight(int vertexIndex)
        {
            return _weights.TryGetValue(vertexIndex, out var weight) ? weight : 1f;
        }

        public void Clear()
        {
            _weights.Clear();
        }
    }

    public enum SculptBrushType
    {
        Draw,
        Inflate,
        Smooth,
        Grab,
        Flatten,
        Pinch
    }

    public enum SculptFalloff
    {
        Smooth,
        Linear,
        Sharp,
        Constant
    }

    public record struct SculptResult(int AffectedVertices, float TotalDisplacement, float MaxDisplacement)
    {
        public static SculptResult Empty => new(0, 0f, 0f);
    }
}
