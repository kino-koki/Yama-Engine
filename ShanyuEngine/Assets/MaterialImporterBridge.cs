// ==================================================================================================
//  MaterialImporterBridge.cs — 材质导入桥梁
//
//  命名空间: ShanyuEngine.Assets
//  功能:     将 InterchangeMaterialNode 转换为引擎材质资产（MaterialAsset），
//            解析 PBR 属性、纹理引用，并支持创建默认材质。
// ==================================================================================================

using System;
using System.IO;
using System.Numerics;

namespace ShanyuEngine.Assets
{
    // ─────────────────────────────────────────────────────────────────────────────
    //  MaterialImporterBridge — 材质导入桥梁
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 材质导入桥梁。负责从 <see cref="InterchangeMaterialNode"/> 提取 PBR 属性与纹理引用，
    /// 生成引擎可用的 <see cref="MaterialAsset"/>，并支持创建默认白色漫反射材质。
    /// </summary>
    public sealed class MaterialImporterBridge
    {
        private readonly AssetDatabase _assetDatabase;

        /// <summary>构造桥梁实例。</summary>
        /// <param name="assetDatabase">资产数据库（用于纹理导入与路径解析）。</param>
        public MaterialImporterBridge(AssetDatabase assetDatabase)
        {
            _assetDatabase = assetDatabase ?? throw new ArgumentNullException(nameof(assetDatabase));
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  公开导入接口
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 从 <see cref="InterchangeMaterialNode"/> 导入材质，提取 PBR 属性与纹理引用。
        /// </summary>
        /// <param name="node">Interchange 材质节点。</param>
        /// <param name="basePath">用于解析相对纹理路径的基准目录（通常为模型文件所在目录）。</param>
        /// <returns>导入后的引擎材质资产。</returns>
        public MaterialAsset ImportMaterialFromInterchange(InterchangeMaterialNode node, string basePath)
        {
            if (node == null)
                throw new ArgumentNullException(nameof(node));
            if (string.IsNullOrWhiteSpace(basePath))
                basePath = Directory.GetCurrentDirectory();

            var material = new MaterialAsset
            {
                Name = node.DisplayLabel ?? "ImportedMaterial",
                BaseColor = node.BaseColor,
                Metallic = node.Metallic,
                Roughness = node.Roughness,
                Emissive = node.Emissive,
                EmissiveIntensity = node.EmissiveIntensity,
                Alpha = node.Alpha,
                AlphaCutoff = node.AlphaCutoff,
                DoubleSided = node.DoubleSided,
                NormalScale = node.NormalScale
            };

            // 提取并解析纹理引用
            if (node.TextureSlots != null)
            {
                foreach (var slot in node.TextureSlots)
                {
                    string slotName = slot.Key;
                    string textureRef = slot.Value;

                    if (string.IsNullOrEmpty(textureRef))
                        continue;

                    string resolvedPath = ResolveTexturePath(textureRef, basePath);
                    if (!string.IsNullOrEmpty(resolvedPath) && File.Exists(resolvedPath))
                    {
                        // 通过 AssetDatabase 导入纹理，获取库内路径
                        var meta = _assetDatabase.ImportAsset(resolvedPath, AssetType.Texture2D);
                        string assetPath = meta?.AssetPath ?? resolvedPath;

                        BindTextureSlot(material, slotName, assetPath);
                    }
                }
            }

            return material;
        }

        /// <summary>
        /// 创建默认材质：白色漫反射，金属度 0，粗糙度 0.5。
        /// </summary>
        public MaterialAsset CreateDefaultMaterial()
        {
            return new MaterialAsset
            {
                Name = "DefaultMaterial",
                BaseColor = Vector3.One,
                Metallic = 0f,
                Roughness = 0.5f,
                Emissive = Vector3.Zero,
                EmissiveIntensity = 0f,
                Alpha = 1f,
                AlphaCutoff = 0.5f,
                DoubleSided = false,
                NormalScale = 1f
            };
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  内部辅助
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>将纹理引用绑定到 MaterialAsset 的标准槽位。</summary>
        private static void BindTextureSlot(MaterialAsset material, string slotName, string assetPath)
        {
            switch (slotName)
            {
                case InterchangeMaterialNode.Slots.BaseColor:
                    material.TextureSlots["Albedo"] = assetPath;
                    break;

                case InterchangeMaterialNode.Slots.MetallicRoughness:
                    // glTF 格式：R 通道 = Metallic，G 通道 = Roughness
                    material.TextureSlots["MetallicRoughness"] = assetPath;
                    break;

                case InterchangeMaterialNode.Slots.Normal:
                    material.TextureSlots["Normal"] = assetPath;
                    break;

                case InterchangeMaterialNode.Slots.Emissive:
                    material.TextureSlots["Emissive"] = assetPath;
                    break;

                case InterchangeMaterialNode.Slots.Occlusion:
                    material.TextureSlots["Occlusion"] = assetPath;
                    break;

                case InterchangeMaterialNode.Slots.ORM:
                    // Occlusion/Roughness/Metallic 三通道打包贴图
                    material.TextureSlots["ORM"] = assetPath;
                    break;

                default:
                    // 非标准槽位：按原名称透传
                    material.TextureSlots[slotName] = assetPath;
                    break;
            }
        }

        /// <summary>解析纹理的绝对路径（支持绝对路径、相对路径、仅文件名回退）。</summary>
        private static string ResolveTexturePath(string textureRef, string basePath)
        {
            if (string.IsNullOrWhiteSpace(textureRef))
                return string.Empty;

            // 1. 若已是绝对路径且存在
            if (Path.IsPathRooted(textureRef) && File.Exists(textureRef))
                return textureRef;

            // 2. 尝试作为相对路径解析
            string relativePath = Path.Combine(basePath, textureRef);
            if (File.Exists(relativePath))
                return relativePath;

            // 3. 仅取文件名再尝试（某些导出器使用扁平化路径）
            string fileName = Path.GetFileName(textureRef);
            string fileNamePath = Path.Combine(basePath, fileName);
            if (File.Exists(fileNamePath))
                return fileNamePath;

            return string.Empty;
        }
    }
}
