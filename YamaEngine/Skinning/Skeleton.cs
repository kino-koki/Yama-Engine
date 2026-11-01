using System.Numerics;
using System.Text.Json;
using YamaEngine.Assets;

namespace YamaEngine.Skinning
{
    public sealed class Bone
    {
        public string Name { get; set; } = "Bone";
        public int ParentIndex { get; set; } = -1;
        public Vector3 LocalPosition { get; set; } = Vector3.Zero;
        public Quaternion LocalRotation { get; set; } = Quaternion.Identity;
        public Vector3 LocalScale { get; set; } = Vector3.One;
    }

    public sealed class Skeleton
    {
        public int Version { get; set; } = 1;
        public string Guid { get; set; } = System.Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "Skeleton";
        public List<Bone> Bones { get; set; } = new();

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            IncludeFields = true
        };

        public int AddBone(string name, int parentIndex, Vector3 localPosition)
        {
            Bones.Add(new Bone
            {
                Name = name,
                ParentIndex = parentIndex,
                LocalPosition = localPosition,
                LocalRotation = Quaternion.Identity,
                LocalScale = Vector3.One
            });
            return Bones.Count - 1;
        }

        public int FindBone(string name)
        {
            for (var i = 0; i < Bones.Count; i++)
            {
                if (Bones[i].Name == name) return i;
            }
            return -1;
        }

        public List<string> Validate()
        {
            var errors = new List<string>();
            if (Bones.Count == 0) errors.Add("Skeleton must contain at least one bone.");
            for (var i = 0; i < Bones.Count; i++)
            {
                var b = Bones[i];
                if (b.ParentIndex >= i) errors.Add($"Bone '{b.Name}' parent index must precede the bone itself.");
                if (b.ParentIndex < -1 || b.ParentIndex >= Bones.Count) errors.Add($"Bone '{b.Name}' has invalid parent index {b.ParentIndex}.");
                if (string.IsNullOrWhiteSpace(b.Name)) errors.Add($"Bone {i} has an empty name.");
            }
            return errors;
        }

        public Matrix4x4[] ComputeWorldMatrices(SkeletonPose? pose = null)
        {
            var world = new Matrix4x4[Bones.Count];
            for (var i = 0; i < Bones.Count; i++)
            {
                var b = Bones[i];
                // Pose scale offset defaults to One, so legacy behaviour is preserved.
                var scale = b.LocalScale * (pose?.GetScaleOffset(i) ?? Vector3.One);
                var local = Matrix4x4.CreateScale(scale)
                            * Matrix4x4.CreateFromQuaternion(pose?.GetRotation(i) ?? b.LocalRotation)
                            * Matrix4x4.CreateTranslation(b.LocalPosition + (pose?.GetTranslationOffset(i) ?? Vector3.Zero));
                world[i] = b.ParentIndex >= 0 ? local * world[b.ParentIndex] : local;
            }
            return world;
        }

        public SkeletonPose CreateBindPose()
        {
            return new SkeletonPose(this);
        }

        public static Skeleton CreateBiped(string name = "Biped")
        {
            var s = new Skeleton { Name = name };
            int hips = s.AddBone("Hips", -1, new Vector3(0, 1.0f, 0));
            int spine = s.AddBone("Spine", hips, new Vector3(0, 0.2f, 0));
            int chest = s.AddBone("Chest", spine, new Vector3(0, 0.2f, 0));
            int neck = s.AddBone("Neck", chest, new Vector3(0, 0.2f, 0));
            s.AddBone("Head", neck, new Vector3(0, 0.15f, 0));

            int lShoulder = s.AddBone("LeftShoulder", chest, new Vector3(-0.15f, 0.15f, 0));
            int lUpperArm = s.AddBone("LeftUpperArm", lShoulder, new Vector3(-0.15f, 0, 0));
            int lLowerArm = s.AddBone("LeftLowerArm", lUpperArm, new Vector3(-0.25f, 0, 0));
            s.AddBone("LeftHand", lLowerArm, new Vector3(-0.25f, 0, 0));

            int rShoulder = s.AddBone("RightShoulder", chest, new Vector3(0.15f, 0.15f, 0));
            int rUpperArm = s.AddBone("RightUpperArm", rShoulder, new Vector3(0.15f, 0, 0));
            int rLowerArm = s.AddBone("RightLowerArm", rUpperArm, new Vector3(0.25f, 0, 0));
            s.AddBone("RightHand", rLowerArm, new Vector3(0.25f, 0, 0));

            int lUpperLeg = s.AddBone("LeftUpperLeg", hips, new Vector3(-0.1f, -0.1f, 0));
            int lLowerLeg = s.AddBone("LeftLowerLeg", lUpperLeg, new Vector3(0, -0.4f, 0));
            s.AddBone("LeftFoot", lLowerLeg, new Vector3(0, -0.4f, 0));

            int rUpperLeg = s.AddBone("RightUpperLeg", hips, new Vector3(0.1f, -0.1f, 0));
            int rLowerLeg = s.AddBone("RightLowerLeg", rUpperLeg, new Vector3(0, -0.4f, 0));
            s.AddBone("RightFoot", rLowerLeg, new Vector3(0, -0.4f, 0));
            return s;
        }

        public void Write(string path)
        {
            if (Validate().Count > 0) throw new InvalidOperationException("Cannot write invalid skeleton: " + string.Join("; ", Validate()));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
            AssetMeta.FromMetadata(new AssetMetadata
            {
                Guid = System.Guid.Parse(Guid),
                Name = Name,
                AssetPath = ToProjectAssetPath(path),
                SourceFile = path,
                Type = AssetType.Skeleton,
                FileSize = new FileInfo(path).Length,
                ImportedAt = DateTime.Now,
                LastModified = File.GetLastWriteTime(path),
                Hash = Guid
            }).Write(path + ".shmeta");
        }

        public static Skeleton Read(string path)
        {
            var skel = JsonSerializer.Deserialize<Skeleton>(File.ReadAllText(path), JsonOptions)
                       ?? throw new InvalidOperationException($"Invalid skeleton: {path}");
            var errors = skel.Validate();
            if (errors.Count > 0) throw new InvalidOperationException("Invalid skeleton: " + string.Join("; ", errors));
            return skel;
        }

        private static string ToProjectAssetPath(string path)
        {
            var normalized = Path.GetFullPath(path).Replace('\\', '/');
            const string marker = "/Assets/";
            var idx = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) return normalized[(idx + 1)..];
            return Path.GetFileName(path);
        }
    }

    public sealed class SkeletonPose
    {
        private readonly Skeleton _skeleton;
        private readonly Quaternion[] _rotations;
        private readonly Vector3[] _translationOffsets;
        private readonly Vector3[] _scaleOffsets;

        public SkeletonPose(Skeleton skeleton)
        {
            _skeleton = skeleton;
            _rotations = new Quaternion[skeleton.Bones.Count];
            _translationOffsets = new Vector3[skeleton.Bones.Count];
            _scaleOffsets = new Vector3[skeleton.Bones.Count];
            for (var i = 0; i < skeleton.Bones.Count; i++)
            {
                _rotations[i] = skeleton.Bones[i].LocalRotation;
                _translationOffsets[i] = Vector3.Zero;
                _scaleOffsets[i] = Vector3.One;
            }
        }

        public int BoneCount => _rotations.Length;
        public Skeleton Skeleton => _skeleton;
        // 根运动（运动匹配等系统使用）
        public Vector3? RootMotion { get; set; }

        public Quaternion GetRotation(int boneIndex) => _rotations[boneIndex];
        public Vector3 GetTranslationOffset(int boneIndex) => _translationOffsets[boneIndex];
        public Vector3 GetScaleOffset(int boneIndex) => _scaleOffsets[boneIndex];

        public void SetRotation(int boneIndex, Quaternion rotation)
        {
            _rotations[boneIndex] = Quaternion.Normalize(rotation);
        }

        public void SetRotation(string boneName, Quaternion rotation)
        {
            var idx = _skeleton.FindBone(boneName);
            if (idx < 0) throw new ArgumentException($"Bone '{boneName}' not found.");
            SetRotation(idx, rotation);
        }

        public void SetTranslationOffset(int boneIndex, Vector3 offset)
        {
            _translationOffsets[boneIndex] = offset;
        }

        public void SetTranslationOffset(string boneName, Vector3 offset)
        {
            var idx = _skeleton.FindBone(boneName);
            if (idx < 0) throw new ArgumentException($"Bone '{boneName}' not found.");
            SetTranslationOffset(idx, offset);
        }

        public void SetScaleOffset(int boneIndex, Vector3 scale)
        {
            _scaleOffsets[boneIndex] = scale;
        }

        public void SetScaleOffset(string boneName, Vector3 scale)
        {
            var idx = _skeleton.FindBone(boneName);
            if (idx < 0) throw new ArgumentException($"Bone '{boneName}' not found.");
            SetScaleOffset(idx, scale);
        }

        public void Rotate(string boneName, Quaternion rotation)
        {
            var idx = _skeleton.FindBone(boneName);
            if (idx < 0) throw new ArgumentException($"Bone '{boneName}' not found.");
            _rotations[idx] = Quaternion.Normalize(rotation * _rotations[idx]);
        }

        public void Translate(string boneName, Vector3 offset)
        {
            var idx = _skeleton.FindBone(boneName);
            if (idx < 0) throw new ArgumentException($"Bone '{boneName}' not found.");
            _translationOffsets[idx] += offset;
        }

        public void Scale(string boneName, Vector3 scale)
        {
            var idx = _skeleton.FindBone(boneName);
            if (idx < 0) throw new ArgumentException($"Bone '{boneName}' not found.");
            _scaleOffsets[idx] *= scale;
        }

        /// <summary>
        /// Copies translation, rotation and scale channels from another pose that shares the
        /// same skeleton. Used by the animation system to layer and blend poses efficiently.
        /// </summary>
        public void CopyFrom(SkeletonPose other)
        {
            if (other == null) throw new ArgumentNullException(nameof(other));
            var count = Math.Min(_rotations.Length, other._rotations.Length);
            for (var i = 0; i < count; i++)
            {
                _rotations[i] = other._rotations[i];
                _translationOffsets[i] = other._translationOffsets[i];
                _scaleOffsets[i] = other._scaleOffsets[i];
            }
        }

        public SkeletonPose Clone()
        {
            var clone = new SkeletonPose(_skeleton);
            clone.CopyFrom(this);
            return clone;
        }
    }
}
