using System;
using System.IO;
using System.Numerics;
using System.Linq;
using System.Collections.Generic;
using YamaEngine.Animation;
using YamaEngine.Assets;
using YamaEngine.Modeling;
using YamaEngine.Skinning;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class AnimationTests
    {
        public static void TestSkeletonCreateBipedHierarchy()
        {
            var skeleton = Skeleton.CreateBiped("Biped");

            AssertTrue(skeleton.Bones.Count >= 10, "Biped skeleton should contain a meaningful bone count.");
            AssertTrue(skeleton.Bones.Any(b => b.Name == "Hips"), "Biped skeleton should expose a Hips bone.");
            AssertTrue(skeleton.Bones.Any(b => b.Name == "Spine"), "Biped skeleton should expose a Spine bone.");
            AssertTrue(skeleton.Bones.Any(b => b.Name == "Head"), "Biped skeleton should expose a Head bone.");
            AssertEqual(0, skeleton.Validate().Count, "Biped skeleton should validate cleanly.");

            var hips = skeleton.Bones.First(b => b.Name == "Hips");
            AssertEqual(-1, hips.ParentIndex, "Hips should be the root bone.");
            var spine = skeleton.Bones.First(b => b.Name == "Spine");
            AssertEqual(skeleton.Bones.IndexOf(hips), spine.ParentIndex, "Spine should parent to Hips.");
        }

        public static void TestSkeletonRoundtripWithMeta(string tempRoot)
        {
            var projectPath = Path.Combine(tempRoot, "SkeletonProject");
            var skeletonPath = Path.Combine(projectPath, "Assets", "Skeletons", "Biped.shskel");

            var skeleton = Skeleton.CreateBiped("Biped");
            skeleton.Write(skeletonPath);

            AssertTrue(File.Exists(skeletonPath), "Skeleton file should be written.");
            AssertTrue(File.Exists(skeletonPath + ".shmeta"), "Skeleton meta should be written.");

            var loaded = Skeleton.Read(skeletonPath);
            AssertEqual(skeleton.Name, loaded.Name, "Skeleton name should roundtrip.");
            AssertEqual(skeleton.Bones.Count, loaded.Bones.Count, "Skeleton bone count should roundtrip.");
            AssertEqual(0, loaded.Validate().Count, "Loaded skeleton should validate.");

            var meta = AssetMeta.Read(skeletonPath + ".shmeta");
            AssertEqual(AssetType.Skeleton.ToString(), meta.Type, "Skeleton should register as Skeleton asset.");
            AssertEqual("Assets/Skeletons/Biped.shskel", meta.AssetPath, "Skeleton meta should store project relative path.");
        }

        public static void TestSkinBindingAutoWeightsAreNormalized()
        {
            var mesh = EditableMesh.CreateCube("SkinCube", 2f);
            var skeleton = Skeleton.CreateBiped("Biped");

            var binding = SkinBinding.AutoBind(mesh, skeleton);

            AssertEqual(mesh.Vertices.Count, binding.Weights.Count, "Skin binding should produce one weight set per vertex.");
            foreach (var weight in binding.Weights)
            {
                AssertTrue(weight.Influences.Count > 0, "Each vertex should have at least one bone influence.");
                var sum = weight.Influences.Sum(i => i.Weight);
                AssertTrue(Math.Abs(sum - 1f) < 0.001f, "Skin weights should be normalized per vertex.");
            }
        }

        public static void TestCpuSkinningDeformsMeshWithPose()
        {
            var mesh = EditableMesh.CreateCube("SkinCube", 2f);
            var skeleton = Skeleton.CreateBiped("Biped");
            var binding = SkinBinding.AutoBind(mesh, skeleton);

            var pose = skeleton.CreateBindPose();
            pose.Rotate("Hips", Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f));

            var deformed = CpuSkinning.Deform(mesh, skeleton, binding, pose);

            AssertEqual(mesh.Vertices.Count, deformed.Count, "Deformed mesh should preserve vertex count.");
            AssertTrue(deformed.Zip(mesh.Vertices, (a, b) => Vector3.Distance(a, b)).Any(d => d > 0.01f), "CPU skinning should displace mesh vertices when pose changes.");
        }

        public static void TestAnimationClipSamplesBoneKeyframes()
        {
            var skeleton = Skeleton.CreateBiped("AnimBiped");
            var clip = new AnimationClip { Name = "HipLift", LengthSeconds = 1.0f, Looping = false };
            clip.AddTranslationKey("Hips", 0.0f, Vector3.Zero);
            clip.AddTranslationKey("Hips", 1.0f, new Vector3(0, 2, 0));
            clip.AddRotationKey("Hips", 0.0f, Quaternion.Identity);
            clip.AddRotationKey("Hips", 1.0f, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI));

            var pose = clip.Sample(skeleton, 0.5f);
            var hips = skeleton.FindBone("Hips");

            AssertTrue(Vector3.Distance(new Vector3(0, 1, 0), pose.GetTranslationOffset(hips)) < 0.001f, "AnimationClip should interpolate translation keyframes.");
            AssertTrue(MathF.Abs(pose.GetRotation(hips).Y) > 0.6f, "AnimationClip should interpolate rotation keyframes.");
        }

        public static void TestAnimationClipLoopsAndRoundtrips(string tempRoot)
        {
            var clipPath = Path.Combine(tempRoot, "AnimationProject", "Assets", "Animations", "Walk.shanim");
            var clip = new AnimationClip { Name = "Walk", LengthSeconds = 1.0f, Looping = true };
            clip.AddTranslationKey("Hips", 0.0f, Vector3.Zero);
            clip.AddTranslationKey("Hips", 1.0f, new Vector3(0, 0, 1));
            clip.Write(clipPath);

            var loaded = AnimationClip.Read(clipPath);
            var pose = loaded.Sample(Skeleton.CreateBiped("AnimBiped"), 1.5f);
            var hips = Skeleton.CreateBiped("AnimBiped").FindBone("Hips");

            AssertEqual("Walk", loaded.Name, "AnimationClip name should roundtrip.");
            AssertTrue(loaded.Looping, "AnimationClip looping flag should roundtrip.");
            AssertTrue(File.Exists(clipPath), "AnimationClip file should be written.");
            AssertTrue(File.Exists(clipPath + ".shmeta"), "AnimationClip meta should be written.");
            AssertTrue(Vector3.Distance(new Vector3(0, 0, 0.5f), pose.GetTranslationOffset(hips)) < 0.001f, "Looping clip should wrap sample time.");
        }

        public static void TestAnimatorCrossFadesBetweenStates()
        {
            var skeleton = Skeleton.CreateBiped("AnimBiped");
            var idle = new AnimationClip { Name = "Idle", LengthSeconds = 1.0f, Looping = true };
            idle.AddTranslationKey("Hips", 0.0f, Vector3.Zero);
            idle.AddTranslationKey("Hips", 1.0f, Vector3.Zero);

            var run = new AnimationClip { Name = "Run", LengthSeconds = 1.0f, Looping = true };
            run.AddTranslationKey("Hips", 0.0f, Vector3.Zero);
            run.AddTranslationKey("Hips", 1.0f, new Vector3(0, 0, 2));

            var animator = new Animator(skeleton);
            animator.AddState("Idle", idle);
            animator.AddState("Run", run);
            animator.Play("Idle");
            animator.Update(0.25f);
            animator.CrossFade("Run", 0.5f);
            animator.Update(0.25f);

            AssertEqual("Run", animator.TargetStateName, "Animator should expose crossfade target.");
            AssertTrue(animator.BlendWeight > 0.4f && animator.BlendWeight < 0.6f, "Animator should advance blend weight during crossfade.");

            animator.Update(0.25f);
            var pose = animator.CurrentPose;
            var hips = skeleton.FindBone("Hips");

            AssertEqual("Run", animator.CurrentStateName, "Animator should commit target state after crossfade.");
            AssertTrue(pose.GetTranslationOffset(hips).Z > 0.0f, "Animator should output target animation pose after transition.");
        }
    }
}
