using System.Numerics;
using YamaEngine.Skinning;

namespace YamaEngine.Animation
{
    /// <summary>
    /// 动画→渲染桥接器。将 SkeletonPose / AnimationClip 转换为骨骼矩阵数组，供 GPU 蒙皮使用。
    /// </summary>
    public static class AnimationRenderBridge
    {
        /// <summary>
        /// 从当前 Pose 计算骨骼世界空间矩阵数组。
        /// </summary>
        public static Matrix4x4[] ComputeBoneWorldMatrices(SkeletonPose pose, Skeleton skeleton)
        {
            return skeleton.ComputeWorldMatrices(pose);
        }

        /// <summary>
        /// 将骨骼世界矩阵转换为 GPU 蒙皮矩阵（逆绑定姿势 * 世界矩阵）。
        /// 这是顶点着色器期望的格式：将顶点从绑定姿势变换到动画姿势。
        /// </summary>
        public static Matrix4x4[] ComputeSkinningMatrices(Matrix4x4[] boneWorldMatrices, SkinBinding skinBinding)
        {
            if (boneWorldMatrices == null || skinBinding?.InverseBindPoses == null)
                return boneWorldMatrices ?? Array.Empty<Matrix4x4>();

            int count = Math.Min(boneWorldMatrices.Length, skinBinding.InverseBindPoses.Count);
            var result = new Matrix4x4[count];
            for (int i = 0; i < count; i++)
            {
                result[i] = skinBinding.InverseBindPoses[i] * boneWorldMatrices[i];
            }
            return result;
        }

        /// <summary>
        /// 从动画片段采样并计算骨骼世界矩阵。
        /// </summary>
        public static Matrix4x4[] SampleAndComputeWorldMatrices(AnimationClip clip, Skeleton skeleton, float time)
        {
            if (clip == null || skeleton == null)
                return Array.Empty<Matrix4x4>();

            var pose = clip.Sample(skeleton, time);
            return skeleton.ComputeWorldMatrices(pose);
        }

        /// <summary>
        /// 从动画片段采样并计算完整的 GPU 蒙皮矩阵（包含逆绑定姿势）。
        /// </summary>
        public static Matrix4x4[] SampleAndComputeSkinningMatrices(AnimationClip clip, Skeleton skeleton, SkinBinding skinBinding, float time)
        {
            var worldMatrices = SampleAndComputeWorldMatrices(clip, skeleton, time);
            return ComputeSkinningMatrices(worldMatrices, skinBinding);
        }
    }
}
