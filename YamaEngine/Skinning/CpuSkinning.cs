using System.Numerics;
using YamaEngine.Modeling;

namespace YamaEngine.Skinning
{
    public static class CpuSkinning
    {
        public static List<Vector3> Deform(EditableMesh mesh, Skeleton skeleton, SkinBinding binding, SkeletonPose pose)
        {
            if (mesh.Vertices.Count != binding.Weights.Count)
            {
                throw new InvalidOperationException("Mesh vertex count must match skin binding weight count.");
            }
            if (binding.InverseBindPoses.Count != skeleton.Bones.Count)
            {
                throw new InvalidOperationException("Skin binding bind-pose count must match skeleton bone count.");
            }

            var animated = skeleton.ComputeWorldMatrices(pose);
            var output = new List<Vector3>(mesh.Vertices.Count);

            for (var v = 0; v < mesh.Vertices.Count; v++)
            {
                var src = mesh.Vertices[v];
                var sum = Vector3.Zero;
                var weights = binding.Weights[v].Influences;
                if (weights.Count == 0)
                {
                    output.Add(src);
                    continue;
                }
                foreach (var w in weights)
                {
                    if (w.BoneIndex < 0 || w.BoneIndex >= skeleton.Bones.Count) continue;
                    var skin = binding.InverseBindPoses[w.BoneIndex] * animated[w.BoneIndex];
                    sum += Vector3.Transform(src, skin) * w.Weight;
                }
                output.Add(sum);
            }
            return output;
        }
    }
}
