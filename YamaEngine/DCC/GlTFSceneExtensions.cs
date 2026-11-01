using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.Assets.Importers;

namespace YamaEngine.DCC
{
    /// <summary>
    /// InterchangeScene 的 glTF 导入扩展方法。
    /// 将 GlTFImport* 中间数据转换并填充到 InterchangeScene 的
    /// Interchange* 强类型集合中（DCC 层 → Assets 层转换）。
    /// </summary>
    internal static class GlTFSceneExtensions
    {
        public static void SetImportMeshes(this InterchangeScene scene, GlTFImportMesh[] meshes)
        {
            if (meshes == null) return;
            foreach (var m in meshes)
            {
                var im = new InterchangeMesh { Name = m.Name ?? $"Mesh_{scene.Meshes.Count}" };
                im.Vertices.AddRange(ToVectors(m.Positions));
                if (m.Indices != null) im.Indices.AddRange(m.Indices);
                im.Normals.AddRange(ToVectors(m.Normals));
                im.UV0.AddRange(ToVector2s(m.UV0));
                im.Tangents.AddRange(ToVector4s(m.Tangents));
                im.Colors.AddRange(ToVector4s(m.Colors));
                if (m.MaterialIndex >= 0) im.MaterialIndices.Add(m.MaterialIndex);
                scene.Meshes.Add(im);
            }
        }

        public static void SetImportMaterials(this InterchangeScene scene, GlTFImportMaterial[] materials)
        {
            if (materials == null) return;
            foreach (var m in materials)
            {
                var im = new InterchangeMaterial
                {
                    Name = m.Name ?? $"Material_{scene.Materials.Count}",
                    DoubleSided = m.DoubleSided,
                    Metallic = m.MetallicFactor,
                    Roughness = m.RoughnessFactor,
                    ShadingModel = string.Equals(m.AlphaMode, "Unlit", StringComparison.OrdinalIgnoreCase) ? "Unlit" : "PBR"
                };
                if (m.BaseColorFactor != null && m.BaseColorFactor.Length >= 3)
                {
                    im.DiffuseColor = new Vector3(m.BaseColorFactor[0], m.BaseColorFactor[1], m.BaseColorFactor[2]);
                    if (m.BaseColorFactor.Length >= 4) im.Opacity = m.BaseColorFactor[3];
                }
                if (m.EmissiveFactor != null && m.EmissiveFactor.Length >= 3)
                    im.EmissiveColor = new Vector3(m.EmissiveFactor[0], m.EmissiveFactor[1], m.EmissiveFactor[2]);
                scene.Materials.Add(im);
            }
        }

        public static void SetImportAnimations(this InterchangeScene scene, GlTFImportAnimation[] animations)
        {
            if (animations == null) return;
            foreach (var a in animations)
            {
                var clip = new InterchangeAnimationClip { Name = a.Name ?? $"Anim_{scene.AnimationClips.Count}" };
                var tracks = new Dictionary<int, InterchangeBoneTrack>();
                if (a.Channels != null)
                {
                    foreach (var ch in a.Channels)
                    {
                        if (!tracks.TryGetValue(ch.NodeIndex, out var track))
                        {
                            track = new InterchangeBoneTrack { BoneName = ch.NodeIndex.ToString() };
                            tracks[ch.NodeIndex] = track;
                        }
                        AddKeys(track, ch);
                    }
                }
                float maxT = 0f;
                foreach (var t in tracks.Values)
                {
                    foreach (var (time, _) in t.PositionKeys) if (time > maxT) maxT = time;
                    foreach (var (time, _) in t.RotationKeys) if (time > maxT) maxT = time;
                    foreach (var (time, _) in t.ScaleKeys) if (time > maxT) maxT = time;
                    clip.BoneTracks.Add(t);
                }
                clip.Duration = maxT;
                scene.AnimationClips.Add(clip);
            }
        }

        public static void SetImportSkins(this InterchangeScene scene, GlTFImportSkin[] skins)
        {
            if (skins == null) return;
            foreach (var s in skins)
            {
                if (s.JointIndices == null) continue;
                for (int i = 0; i < s.JointIndices.Length; i++)
                {
                    scene.Bones.Add(new InterchangeBone
                    {
                        Name = s.Name != null ? $"{s.Name}_{i}" : $"Bone_{scene.Bones.Count}",
                        ParentIndex = -1
                    });
                }
            }
        }

        public static void SetImportNodes(this InterchangeScene scene, GlTFImportNode[] nodes, int[] rootNodes)
        {
            if (nodes == null) return;
            var created = new InterchangeNode[nodes.Length];
            for (int i = 0; i < nodes.Length; i++)
                created[i] = ConvertNode(nodes[i]);

            for (int i = 0; i < nodes.Length; i++)
            {
                if (nodes[i].Children == null) continue;
                foreach (var c in nodes[i].Children)
                    if (c >= 0 && c < created.Length)
                        created[i].Children.Add(created[c]);
            }

            if (rootNodes != null && rootNodes.Length > 0)
            {
                foreach (var r in rootNodes)
                    if (r >= 0 && r < created.Length)
                        scene.RootNodes.Add(created[r]);
            }
            else
            {
                scene.RootNodes.AddRange(created);
            }
        }

        private static InterchangeNode ConvertNode(in GlTFImportNode n)
        {
            var node = new InterchangeNode { Name = n.Name ?? "Node" };
            if (n.MeshIndex.HasValue) node.MeshIndex = n.MeshIndex.Value;
            if (n.SkinIndex.HasValue) node.BoneIndex = n.SkinIndex.Value;
            if (n.LocalMatrix != null && n.LocalMatrix.Length >= 16)
                node.LocalTransform = ToMatrix4x4(n.LocalMatrix);
            return node;
        }

        private static Matrix4x4 ToMatrix4x4(float[] m)
        {
            // glTF 矩阵为列主序 [m00,m10,m20,m30, m01,m11,...]，System.Numerics 为行主序。
            return new Matrix4x4(
                m[0], m[4], m[8], m[12],
                m[1], m[5], m[9], m[13],
                m[2], m[6], m[10], m[14],
                m[3], m[7], m[11], m[15]);
        }

        private static void AddKeys(InterchangeBoneTrack track, in GlTFImportChannel ch)
        {
            if (ch.KeyframeTimes == null || ch.KeyframeValues == null) return;
            var times = ch.KeyframeTimes;
            var vals = ch.KeyframeValues;
            string path = ch.TargetPath ?? "";
            if (path == "translation" || path == "position")
            {
                for (int i = 0; i < times.Length && i * 3 + 2 < vals.Length; i++)
                    track.PositionKeys.Add((times[i], new Vector3(vals[i * 3], vals[i * 3 + 1], vals[i * 3 + 2])));
            }
            else if (path == "rotation")
            {
                for (int i = 0; i < times.Length && i * 4 + 3 < vals.Length; i++)
                    track.RotationKeys.Add((times[i], new Quaternion(vals[i * 4], vals[i * 4 + 1], vals[i * 4 + 2], vals[i * 4 + 3])));
            }
            else if (path == "scale")
            {
                for (int i = 0; i < times.Length && i * 3 + 2 < vals.Length; i++)
                    track.ScaleKeys.Add((times[i], new Vector3(vals[i * 3], vals[i * 3 + 1], vals[i * 3 + 2])));
            }
        }

        private static List<Vector3> ToVectors(float[] arr)
        {
            var list = new List<Vector3>();
            if (arr == null) return list;
            for (int i = 0; i + 2 < arr.Length; i += 3)
                list.Add(new Vector3(arr[i], arr[i + 1], arr[i + 2]));
            return list;
        }

        private static List<Vector2> ToVector2s(float[] arr)
        {
            var list = new List<Vector2>();
            if (arr == null) return list;
            for (int i = 0; i + 1 < arr.Length; i += 2)
                list.Add(new Vector2(arr[i], arr[i + 1]));
            return list;
        }

        private static List<Vector4> ToVector4s(float[] arr)
        {
            var list = new List<Vector4>();
            if (arr == null) return list;
            for (int i = 0; i + 3 < arr.Length; i += 4)
                list.Add(new Vector4(arr[i], arr[i + 1], arr[i + 2], arr[i + 3]));
            return list;
        }
    }
}
