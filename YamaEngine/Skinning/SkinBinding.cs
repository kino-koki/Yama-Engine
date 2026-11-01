using System.Numerics;
using System.Text.Json;
using YamaEngine.Modeling;

namespace YamaEngine.Skinning
{
    public readonly record struct BoneInfluence(int BoneIndex, float Weight);

    public sealed class VertexWeight
    {
        public List<BoneInfluence> Influences { get; set; } = new();
    }

    public sealed class SkinBinding
    {
        public string SkeletonGuid { get; set; } = string.Empty;
        public List<VertexWeight> Weights { get; set; } = new();
        public List<Matrix4x4> InverseBindPoses { get; set; } = new();

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            IncludeFields = true
        };

        public const int MaxInfluencesPerVertex = 4;

        public static SkinBinding AutoBind(EditableMesh mesh, Skeleton skeleton, int maxInfluences = MaxInfluencesPerVertex)
        {
            if (mesh.Vertices.Count == 0) throw new InvalidOperationException("Mesh has no vertices to bind.");
            if (skeleton.Bones.Count == 0) throw new InvalidOperationException("Skeleton has no bones.");

            var bindWorld = skeleton.ComputeWorldMatrices(null);
            var binding = new SkinBinding
            {
                SkeletonGuid = skeleton.Guid
            };
            foreach (var m in bindWorld)
            {
                Matrix4x4.Invert(m, out var inv);
                binding.InverseBindPoses.Add(inv);
            }

            for (var v = 0; v < mesh.Vertices.Count; v++)
            {
                var vertex = mesh.Vertices[v];
                var distances = new (int bone, float dist)[skeleton.Bones.Count];
                for (var b = 0; b < skeleton.Bones.Count; b++)
                {
                    var bonePos = new Vector3(bindWorld[b].M41, bindWorld[b].M42, bindWorld[b].M43);
                    distances[b] = (b, Vector3.Distance(vertex, bonePos));
                }
                Array.Sort(distances, (a, b) => a.dist.CompareTo(b.dist));

                var picks = Math.Min(maxInfluences, distances.Length);
                var weights = new List<BoneInfluence>(picks);
                float weightSum = 0f;
                for (var k = 0; k < picks; k++)
                {
                    var w = 1f / MathF.Max(distances[k].dist, 0.0001f);
                    weights.Add(new BoneInfluence(distances[k].bone, w));
                    weightSum += w;
                }
                if (weightSum > 0f)
                {
                    for (var k = 0; k < weights.Count; k++)
                    {
                        weights[k] = new BoneInfluence(weights[k].BoneIndex, weights[k].Weight / weightSum);
                    }
                }
                else
                {
                    weights.Clear();
                    weights.Add(new BoneInfluence(0, 1f));
                }

                binding.Weights.Add(new VertexWeight { Influences = weights });
            }

            return binding;
        }

        public void Write(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }

        public static SkinBinding Read(string path)
        {
            return JsonSerializer.Deserialize<SkinBinding>(File.ReadAllText(path), JsonOptions)
                   ?? throw new InvalidOperationException($"Invalid skin binding: {path}");
        }
    }
}
