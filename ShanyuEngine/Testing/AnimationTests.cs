using ShanyuEngine.Testing;

namespace ShanyuEngine.Tests
{
    /// <summary>
    /// 动画测试：Skeleton 层级、SkinBinding、CPU Skinning、AnimationClip、Animator
    /// </summary>
    [TestClass]
    public class AnimationTests : EngineTestFixture
    {
        [TestMethod]
        public void SkeletonCreateBipedHierarchy()
        {
            var skeleton = ShanyuEngine.Skinning.Skeleton.CreateBiped("Biped");

            Assert.IsTrue(skeleton.Bones.Count >= 10);
            Assert.IsTrue(skeleton.Bones.Any(b => b.Name == "Hips"));
            Assert.IsTrue(skeleton.Bones.Any(b => b.Name == "Spine"));
            Assert.IsTrue(skeleton.Bones.Any(b => b.Name == "Head"));
            Assert.AreEqual(0, skeleton.Validate().Count);

            var hips = skeleton.Bones.First(b => b.Name == "Hips");
            Assert.AreEqual(-1, hips.ParentIndex);
            var spine = skeleton.Bones.First(b => b.Name == "Spine");
            Assert.AreEqual(skeleton.Bones.IndexOf(hips), spine.ParentIndex);
        }

        [TestMethod]
        public void SkeletonRoundtripWithMeta()
        {
            var projectPath = Path.Combine(TempDirectory, "SkeletonProject");
            var skeletonPath = Path.Combine(projectPath, "Assets", "Skeletons", "Biped.shskel");

            var skeleton = ShanyuEngine.Skinning.Skeleton.CreateBiped("Biped");
            skeleton.Write(skeletonPath);

            Assert.IsTrue(File.Exists(skeletonPath));
            Assert.IsTrue(File.Exists(skeletonPath + ".shmeta"));

            var loaded = ShanyuEngine.Skinning.Skeleton.Read(skeletonPath);
            Assert.AreEqual(skeleton.Name, loaded.Name);
            Assert.AreEqual(skeleton.Bones.Count, loaded.Bones.Count);
            Assert.AreEqual(0, loaded.Validate().Count);

            var meta = ShanyuEngine.Assets.AssetMeta.Read(skeletonPath + ".shmeta");
            Assert.AreEqual(ShanyuEngine.Assets.AssetType.Skeleton.ToString(), meta.Type);
        }

        [TestMethod]
        public void SkinBindingAutoWeightsAreNormalized()
        {
            var mesh = ShanyuEngine.Modeling.EditableMesh.CreateCube("SkinCube", 2f);
            var skeleton = ShanyuEngine.Skinning.Skeleton.CreateBiped("Biped");

            var binding = ShanyuEngine.Skinning.SkinBinding.AutoBind(mesh, skeleton);

            Assert.AreEqual(mesh.Vertices.Count, binding.Weights.Count);
            foreach (var weight in binding.Weights)
            {
                Assert.IsTrue(weight.Influences.Count > 0);
                var sum = weight.Influences.Sum(i => i.Weight);
                Assert.IsTrue(Math.Abs(sum - 1f) < 0.001f);
            }
        }

        [TestMethod]
        public void CpuSkinningDeformsMeshWithPose()
        {
            var mesh = ShanyuEngine.Modeling.EditableMesh.CreateCube("SkinCube", 2f);
            var skeleton = ShanyuEngine.Skinning.Skeleton.CreateBiped("Biped");
            var binding = ShanyuEngine.Skinning.SkinBinding.AutoBind(mesh, skeleton);

            var pose = skeleton.CreateBindPose();
            pose.Rotate("Hips", System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitY, MathF.PI / 2f));

            var deformed = ShanyuEngine.Skinning.CpuSkinning.Deform(mesh, skeleton, binding, pose);

            Assert.AreEqual(mesh.Vertices.Count, deformed.Count);
            Assert.IsTrue(deformed.Zip(mesh.Vertices, (a, b) => System.Numerics.Vector3.Distance(a, b)).Any(d => d > 0.01f));
        }

        [TestMethod]
        public void AnimationClipSamplesBoneKeyframes()
        {
            var skeleton = ShanyuEngine.Skinning.Skeleton.CreateBiped("AnimBiped");
            var clip = new ShanyuEngine.Animation.AnimationClip { Name = "HipLift", LengthSeconds = 1.0f, Looping = false };
            clip.AddTranslationKey("Hips", 0.0f, System.Numerics.Vector3.Zero);
            clip.AddTranslationKey("Hips", 1.0f, new System.Numerics.Vector3(0, 2, 0));
            clip.AddRotationKey("Hips", 0.0f, System.Numerics.Quaternion.Identity);
            clip.AddRotationKey("Hips", 1.0f, System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitY, MathF.PI));

            var pose = clip.Sample(skeleton, 0.5f);
            var hips = skeleton.FindBone("Hips");

            Assert.IsTrue(System.Numerics.Vector3.Distance(new System.Numerics.Vector3(0, 1, 0), pose.GetTranslationOffset(hips)) < 0.001f);
            Assert.IsTrue(MathF.Abs(pose.GetRotation(hips).Y) > 0.6f);
        }

        [TestMethod]
        public void AnimatorCrossFadesBetweenStates()
        {
            var skeleton = ShanyuEngine.Skinning.Skeleton.CreateBiped("AnimBiped");
            var idle = new ShanyuEngine.Animation.AnimationClip { Name = "Idle", LengthSeconds = 1.0f, Looping = true };
            idle.AddTranslationKey("Hips", 0.0f, System.Numerics.Vector3.Zero);
            idle.AddTranslationKey("Hips", 1.0f, System.Numerics.Vector3.Zero);
            var run = new ShanyuEngine.Animation.AnimationClip { Name = "Run", LengthSeconds = 1.0f, Looping = true };
            run.AddTranslationKey("Hips", 0.0f, System.Numerics.Vector3.Zero);
            run.AddTranslationKey("Hips", 1.0f, new System.Numerics.Vector3(0, 0, 2));

            var animator = new ShanyuEngine.Animation.Animator(skeleton);
            animator.AddState("Idle", idle);
            animator.AddState("Run", run);
            animator.Play("Idle");
            animator.Update(0.25f);
            animator.CrossFade("Run", 0.5f);
            animator.Update(0.25f);

            Assert.AreEqual("Run", animator.TargetStateName);

            animator.Update(0.25f);
            Assert.AreEqual("Run", animator.CurrentStateName);
        }
    }
}
