using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace ShanyuEngine.Assets
{
    // ====================================================================
    // glTF 2.0 Importer — 纯C# glTF/GLB 解析
    // ====================================================================
    // 支持 .gltf（JSON + 外部资源）和 .glb（二进制容器）两种格式。
    // 解析场景节点层级、网格顶点/索引数据、材质参数（PBR），
    // 组装为引擎内部 LoadedModel 供渲染管线使用。
    // ====================================================================

    // ──────────────────────────────────────────────────────────────
    // GLB 二进制头
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// GLB 文件头结构（12字节）。
    /// </summary>
    public struct GlbHeader
    {
        /// <summary>GLB 魔数 "glTF"（0x46546C67）。</summary>
        public uint Magic;

        /// <summary>GLB 文件总字节长度（含 header）。</summary>
        public uint DataLength;

        /// <summary>glTF 格式版本（2）。</summary>
        public uint FormatVersion;

        public static readonly uint GlbMagic = 0x46546C67; // "glTF" little-endian

        /// <summary>从字节流读取 GLB header。</summary>
        public static GlbHeader Read(byte[] data)
        {
            if (data.Length < 12)
                throw new InvalidDataException("GLB data too short for header (min 12 bytes).");

            return new GlbHeader
            {
                Magic = BitConverter.ToUInt32(data, 0),
                DataLength = BitConverter.ToUInt32(data, 4),
                FormatVersion = BitConverter.ToUInt32(data, 8)
            };
        }
    }

    /// <summary>
    /// GLB chunk 结构。glB 格式由多个 chunk 组成（JSON chunk + BIN chunk）。
    /// </summary>
    public struct GlbChunk
    {
        /// <summary>chunk 数据长度（字节数，不含本字段）。</summary>
        public uint ChunkLength;

        /// <summary>chunk 类型：0x4E4F534A（JSON）或 0x004E4942（BIN）。</summary>
        public uint ChunkType;

        /// <summary>chunk 起始偏移（数据段，不含 8 字节 chunk header）。</summary>
        public int DataOffset;

        public static readonly uint JsonChunkType = 0x4E4F534A; // "JSON"
        public static readonly uint BinChunkType = 0x004E4942;  // "BIN\0"
    }

    // ──────────────────────────────────────────────────────────────
    // glTF 数据模型（对应 glTF 2.0 规范）
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// glTF 文档根对象。
    /// </summary>
    public sealed class GlTFModel
    {
        public GlTFAsset? Asset { get; set; }
        public List<GlTFScene>? Scenes { get; set; }
        public int? DefaultScene { get; set; }
        public List<GlTFNode>? Nodes { get; set; }
        public List<GlTFMesh>? Meshes { get; set; }
        public List<GlTFAccessor>? Accessors { get; set; }
        public List<GlTFBufferView>? BufferViews { get; set; }
        public List<GlTFBuffer>? Buffers { get; set; }
        public List<GlTFMaterial>? Materials { get; set; }
        public List<GlTFTexture>? Textures { get; set; }
        public List<GlTFImage>? Images { get; set; }
        public List<GlTFTextureInfo>? TextureSamplers { get; set; }
    }

    public sealed class GlTFAsset
    {
        public string? Version { get; set; }
        public string? Generator { get; set; }
        public string? Copyright { get; set; }
    }

    public sealed class GlTFScene
    {
        public List<int>? Nodes { get; set; }
        public string? Name { get; set; }
    }

    public sealed class GlTFNode
    {
        public List<int>? Children { get; set; }
        public float[]? Translation { get; set; }
        public float[]? Rotation { get; set; }
        public float[]? Scale { get; set; }
        public float[]? Matrix { get; set; }
        public int? Mesh { get; set; }
        public int? Skin { get; set; }
        public int? Camera { get; set; }
        public string? Name { get; set; }
    }

    public sealed class GlTFMesh
    {
        public List<GlTFPrimitive>? Primitives { get; set; }
        public List<float[]>? Weights { get; set; }
        public string? Name { get; set; }
    }

    public sealed class GlTFPrimitive
    {
        public Dictionary<string, int>? Attributes { get; set; }
        public int? Indices { get; set; }
        public int? Material { get; set; }
        public string? Mode { get; set; }
    }

    public sealed class GlTFAccessor
    {
        public int BufferView { get; set; }
        public int ByteOffset { get; set; }
        public int ComponentType { get; set; } // 5120=BYTE, 5121=UBYTE, 5122=SHORT, 5123=USHORT, 5125=UINT, 5126=FLOAT
        public bool Normalized { get; set; }
        public int Count { get; set; }
        public string? Type { get; set; } // "SCALAR", "VEC2", "VEC3", "VEC4", "MAT2", "MAT3", "MAT4"
        public float[]? Max { get; set; }
        public float[]? Min { get; set; }
        public Dictionary<string, object>? Sparse { get; set; }

        /// <summary>每个完整元素的组件数量（标量=1, VEC2=2, VEC3=3, VEC4=4, ...）。</summary>
        public int GetNumComponents()
        {
            return Type switch
            {
                "SCALAR" => 1,
                "VEC2" => 2,
                "VEC3" => 3,
                "VEC4" => 4,
                "MAT2" => 4,
                "MAT3" => 9,
                "MAT4" => 16,
                _ => 1
            };
        }

        /// <summary>单个组件的字节大小。</summary>
        public int GetComponentSize()
        {
            return ComponentType switch
            {
                5120 or 5121 => 1, // BYTE / UNSIGNED_BYTE
                5122 or 5123 => 2, // SHORT / UNSIGNED_SHORT
                5125 => 4,        // UNSIGNED_INT
                5126 => 4,        // FLOAT
                _ => 4
            };
        }

        /// <summary>组件类型是否为无符号整型（用于索引解析）。</summary>
        public bool IsUnsignedInt => ComponentType == 5125;
        public bool IsUnsignedShort => ComponentType == 5123;
        public bool IsUnsignedByte => ComponentType == 5121;
        public bool IsFloat => ComponentType == 5126;
    }

    public sealed class GlTFBufferView
    {
        public int Buffer { get; set; }
        public int ByteOffset { get; set; }
        public int ByteStride { get; set; }
        public int ByteLength { get; set; }
        public int? Target { get; set; } // 34962=ARRAY_BUFFER, 34963=ELEMENT_ARRAY_BUFFER
    }

    public sealed class GlTFBuffer
    {
        public int ByteLength { get; set; }
        public string? Uri { get; set; }
    }

    // ──────────────────────────────────────────────────────────────
    // glTF 材质（PBR）
    // ──────────────────────────────────────────────────────────────

    public sealed class GlTFMaterial
    {
        public string? Name { get; set; }
        public GlTFPBRMetallicRoughness? PbrMetallicRoughness { get; set; }
        public GlTFTextureInfo? NormalTexture { get; set; }
        public GlTFTextureInfo? OcclusionTexture { get; set; }
        public GlTFTextureInfo? EmissiveTexture { get; set; }
        public float[]? EmissiveFactor { get; set; }
        public double? AlphaMode { get; set; }
        public double? AlphaCutoff { get; set; }
        public bool? DoubleSided { get; set; }
    }

    public sealed class GlTFPBRMetallicRoughness
    {
        public float[]? BaseColorFactor { get; set; }  // [R, G, B, A]
        public double? MetallicFactor { get; set; }
        public double? RoughnessFactor { get; set; }
        public GlTFTextureInfo? BaseColorTexture { get; set; }
        public GlTFTextureInfo? MetallicRoughnessTexture { get; set; }
    }

    public sealed class GlTFTextureInfo
    {
        public int Index { get; set; }
        public int? TexCoord { get; set; }
    }

    public sealed class GlTFTexture
    {
        public int? Source { get; set; }
        public int? Sampler { get; set; }
        public string? Name { get; set; }
    }

    public sealed class GlTFImage
    {
        public string? Uri { get; set; }
        public string? MimeType { get; set; }
        public int? BufferView { get; set; }
        public string? Name { get; set; }
    }

    // ──────────────────────────────────────────────────────────────
    // 引擎加载结果数据结构
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 加载后的子网格数据（单个 primitive）。
    /// </summary>
    public sealed class ModelSubMesh
    {
        /// <summary>子网格名称。</summary>
        public string Name { get; set; } = "SubMesh";

        /// <summary>顶点位置数据（每3个float为一个顶点）。</summary>
        public float[] Positions { get; set; } = Array.Empty<float>();

        /// <summary>法线数据（每3个float为一个法线，可选）。</summary>
        public float[]? Normals { get; set; }

        /// <summary>纹理坐标数据（每2个float为一个UV，可选）。</summary>
        public float[]? TexCoords0 { get; set; }

        /// <summary>索引数据（uint 或 ushort，取决于 glTF accessor 组件类型）。</summary>
        public uint[] Indices { get; set; } = Array.Empty<uint>();

        /// <summary>顶点数量。</summary>
        public int VertexCount => Positions.Length / 3;

        /// <summary>索引数量。</summary>
        public int IndexCount => Indices.Length;

        /// <summary>三角形数量。</summary>
        public int TriangleCount => Indices.Length / 3;

        /// <summary>索引是否为32位（uint）。</summary>
        public bool Use32BitIndices { get; set; }

        /// <summary>关联材质索引（在原始 glTF 中）。</summary>
        public int MaterialIndex { get; set; } = -1;

        /// <summary>材质参数。</summary>
        public LoadedMaterial Material { get; set; } = new();
    }

    /// <summary>
    /// 加载后的材质数据（PBR）。
    /// </summary>
    public sealed class LoadedMaterial
    {
        public string Name { get; set; } = "Default";

        /// <summary>基础颜色因子 [R, G, B, A]，默认白色不透明。</summary>
        public Vector4 BaseColorFactor { get; set; } = new Vector4(1f, 1f, 1f, 1f);

        /// <summary>金属度因子 (0.0 = 非金属, 1.0 = 金属)。</summary>
        public float MetallicFactor { get; set; } = 1f;

        /// <summary>粗糙度因子 (0.0 = 光滑, 1.0 = 粗糙)。</summary>
        public float RoughnessFactor { get; set; } = 1f;

        /// <summary>自发光颜色。</summary>
        public Vector3 EmissiveFactor { get; set; } = Vector3.Zero;

        /// <summary>AlphaMode (OPAQUE/MASK/BLEND)。</summary>
        public string AlphaMode { get; set; } = "OPAQUE";

        /// <summary>Alpha 裁剪阈值。</summary>
        public float AlphaCutoff { get; set; } = 0.5f;

        /// <summary>是否双面渲染。</summary>
        public bool DoubleSided { get; set; }

        /// <summary>基础颜色贴图路径（可选）。</summary>
        public string? BaseColorTexturePath { get; set; }

        /// <summary>法线贴图路径（可选）。</summary>
        public string? NormalTexturePath { get; set; }

        /// <summary>金属度/粗糙度贴图路径（可选）。</summary>
        public string? MetallicRoughnessTexturePath { get; set; }
    }

    /// <summary>
    /// 加载后的完整模型。
    /// </summary>

    /// <summary>
    /// 加载后的节点（含世界变换矩阵）。
    /// </summary>
    public sealed class LoadedNode
    {
        public string Name { get; set; } = "Node";
        public Matrix4x4 LocalTransform { get; set; } = Matrix4x4.Identity;
        public Matrix4x4 WorldTransform { get; set; } = Matrix4x4.Identity;
        public int MeshIndex { get; set; } = -1;
        public List<int> ChildIndices { get; set; } = new();
    }

    // ──────────────────────────────────────────────────────────────
    // GlTFLoader — 核心加载器
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// glTF 2.0 加载器。支持 .gltf（JSON + 外部 .bin/.texture）和 .glb（二进制容器）。
    /// </summary>
    public static class GlTFLoader
    {
        private const int GlbHeaderSize = 12;
        private const int GlbChunkHeaderSize = 8;

        // ── 公开 API ──

        /// <summary>
        /// 从文件加载 glTF 模型。根据扩展名自动判断 .gltf 或 .glb 格式。
        /// </summary>
        public static LoadedModel Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path is required.", nameof(path));
            if (!File.Exists(path))
                throw new FileNotFoundException($"glTF file not found: {path}", path);

            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".gltf" => LoadGltf(path),
                ".glb" => LoadGlb(path),
                _ => throw new NotSupportedException($"Unsupported glTF file extension: '{ext}'. Expected .gltf or .glb.")
            };
        }

        // ── GLB 加载 ──

        /// <summary>加载 .glb 二进制格式文件。</summary>
        private static LoadedModel LoadGlb(string path)
        {
            byte[] fileData = File.ReadAllBytes(path);

            // 解析 GLB header
            var header = GlbHeader.Read(fileData);
            if (header.Magic != GlbHeader.GlbMagic)
                throw new InvalidDataException($"Invalid GLB magic: 0x{header.Magic:X8}. Expected 0x{GlbHeader.GlbMagic:X8}.");
            if (header.FormatVersion != 2)
                throw new NotSupportedException($"Unsupported GLB format version: {header.FormatVersion}. Only version 2 is supported.");
            if (header.DataLength != fileData.Length)
                throw new InvalidDataException($"GLB DataLength ({header.DataLength}) does not match file size ({fileData.Length}).");

            // 解析 chunks
            string jsonChunk = string.Empty;
            byte[]? binChunkData = null;
            int offset = GlbHeaderSize;

            while (offset + GlbChunkHeaderSize <= fileData.Length)
            {
                uint chunkLength = BitConverter.ToUInt32(fileData, offset);
                uint chunkType = BitConverter.ToUInt32(fileData, offset + 4);
                int dataStart = offset + GlbChunkHeaderSize;

                if (dataStart + chunkLength > fileData.Length)
                    throw new InvalidDataException($"GLB chunk extends beyond file (offset={dataStart}, length={chunkLength}, fileSize={fileData.Length}).");

                if (chunkType == GlbChunk.JsonChunkType)
                {
                    jsonChunk = Encoding.UTF8.GetString(fileData, dataStart, (int)chunkLength);
                }
                else if (chunkType == GlbChunk.BinChunkType)
                {
                    binChunkData = new byte[chunkLength];
                    Array.Copy(fileData, dataStart, binChunkData, 0, chunkLength);
                }

                // 对齐到 4 字节边界
                offset = dataStart + (int)chunkLength;
                offset = (offset + 3) & ~3;
            }

            if (string.IsNullOrWhiteSpace(jsonChunk))
                throw new InvalidDataException("GLB file contains no JSON chunk.");

            // 解析 JSON
            var model = JsonSerializer.Deserialize<GlTFModel>(jsonChunk, GetJsonOptions())
                        ?? throw new InvalidDataException("Failed to parse glTF JSON from GLB.");

            // 构建 buffer 数据：glb 的 buffer[0] 指向 BIN chunk
            var buffers = new List<byte[]>(model.Buffers?.Count ?? 0);
            if (model.Buffers != null)
            {
                for (int i = 0; i < model.Buffers.Count; i++)
                {
                    // GLB 的第一个 buffer（无 URI 或 URI 为空）使用内嵌的 BIN chunk
                    if (i == 0 && (string.IsNullOrEmpty(model.Buffers[i].Uri)))
                    {
                        buffers.Add(binChunkData ?? Array.Empty<byte>());
                    }
                    else
                    {
                        buffers.Add(LoadExternalBuffer(model.Buffers[i].Uri, path));
                    }
                }
            }

            return BuildLoadedModel(model, buffers, path);
        }

        // ── glTF 加载 ──

        /// <summary>加载 .gltf 文本格式文件（JSON + 外部 .bin 文件）。</summary>
        private static LoadedModel LoadGltf(string path)
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            var model = JsonSerializer.Deserialize<GlTFModel>(json, GetJsonOptions())
                        ?? throw new InvalidDataException("Failed to parse glTF JSON.");

            // 加载外部 buffers
            var buffers = new List<byte[]>(model.Buffers?.Count ?? 0);
            if (model.Buffers != null)
            {
                foreach (var buf in model.Buffers)
                {
                    buffers.Add(LoadExternalBuffer(buf.Uri, path));
                }
            }

            return BuildLoadedModel(model, buffers, path);
        }

        /// <summary>加载外部 .bin buffer 文件（支持 data URI 和相对路径）。</summary>
        private static byte[] LoadExternalBuffer(string? uri, string gltfPath)
        {
            if (string.IsNullOrWhiteSpace(uri))
                return Array.Empty<byte>();

            // data URI (base64 编码)
            if (uri.StartsWith("data:"))
            {
                int commaIdx = uri.IndexOf(',');
                if (commaIdx < 0)
                    throw new InvalidDataException("Invalid data URI in glTF buffer.");
                string base64 = uri.Substring(commaIdx + 1);
                return Convert.FromBase64String(base64);
            }

            // 相对/绝对文件路径
            string basePath = Path.GetDirectoryName(Path.GetFullPath(gltfPath)) ?? string.Empty;
            string fullPath = Path.Combine(basePath, uri);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException($"External glTF buffer not found: {fullPath}", fullPath);
            return File.ReadAllBytes(fullPath);
        }

        // ── 模型组装 ──

        /// <summary>从解析后的 GlTFModel + buffer 数据组装 LoadedModel。</summary>
        private static LoadedModel BuildLoadedModel(GlTFModel model, List<byte[]> buffers, string sourcePath)
        {
            var result = new LoadedModel
            {
                Name = Path.GetFileNameWithoutExtension(sourcePath)
            };

            model.Buffers ??= new List<GlTFBuffer>();
            model.BufferViews ??= new List<GlTFBufferView>();
            model.Accessors ??= new List<GlTFAccessor>();
            model.Meshes ??= new List<GlTFMesh>();
            model.Materials ??= new List<GlTFMaterial>();
            model.Nodes ??= new List<GlTFNode>();
            model.Scenes ??= new List<GlTFScene>();

            // 解析材质
            foreach (var mat in model.Materials)
            {
                result.Materials.Add(ParseMaterial(mat, model, sourcePath));
            }

            // 确定默认场景的节点列表
            List<int> sceneNodes = new();
            if (model.DefaultScene.HasValue && model.DefaultScene.Value >= 0 && model.DefaultScene.Value < model.Scenes.Count)
            {
                sceneNodes = model.Scenes[model.DefaultScene.Value].Nodes ?? new List<int>();
            }
            else if (model.Scenes.Count > 0)
            {
                sceneNodes = model.Scenes[0].Nodes ?? new List<int>();
            }
            else
            {
                // 没有场景定义时，尝试加载所有节点
                for (int i = 0; i < model.Nodes.Count; i++)
                    sceneNodes.Add(i);
            }

            // 计算节点层级世界矩阵
            var worldMatrices = ComputeWorldMatrices(model);

            // 加载节点
            foreach (int nodeIdx in sceneNodes)
            {
                LoadNodeRecursive(model, nodeIdx, Matrix4x4.Identity, buffers, result, worldMatrices);
            }

            // 如果场景没有节点但存在网格，直接加载所有网格
            if (result.SubMeshes.Count == 0 && model.Meshes.Count > 0)
            {
                for (int meshIdx = 0; meshIdx < model.Meshes.Count; meshIdx++)
                {
                    var mesh = model.Meshes[meshIdx];
                    string meshName = mesh.Name ?? $"Mesh_{meshIdx}";
                    if (mesh.Primitives != null)
                    {
                        for (int primIdx = 0; primIdx < mesh.Primitives.Count; primIdx++)
                        {
                            result.SubMeshes.Add(ParsePrimitive(
                                mesh.Primitives[primIdx], meshName, primIdx,
                                model, buffers, result.Materials));
                        }
                    }
                }
            }

            // 加载节点层级信息
            for (int i = 0; i < model.Nodes.Count; i++)
            {
                var node = model.Nodes[i];
                var loadedNode = new LoadedNode
                {
                    Name = node.Name ?? $"Node_{i}",
                    LocalTransform = ComputeLocalMatrix(node),
                    WorldTransform = worldMatrices.TryGetValue(i, out var wm) ? wm : Matrix4x4.Identity,
                    MeshIndex = node.Mesh ?? -1,
                    ChildIndices = node.Children?.ToList() ?? new List<int>()
                };
                result.Nodes.Add(loadedNode);
            }

            return result;
        }

        /// <summary>递归加载节点及其子节点中的网格。</summary>
        private static void LoadNodeRecursive(GlTFModel model, int nodeIdx, Matrix4x4 parentMatrix,
            List<byte[]> buffers, LoadedModel result, Dictionary<int, Matrix4x4> worldMatrices)
        {
            if (nodeIdx < 0 || nodeIdx >= model.Nodes.Count) return;

            var node = model.Nodes[nodeIdx];
            Matrix4x4 localMatrix = ComputeLocalMatrix(node);
            Matrix4x4 worldMatrix = localMatrix * parentMatrix;

            if (node.Mesh.HasValue && node.Mesh.Value >= 0 && node.Mesh.Value < model.Meshes.Count)
            {
                var mesh = model.Meshes[node.Mesh.Value];
                string meshName = mesh.Name ?? node.Name ?? $"Mesh_{node.Mesh.Value}";

                if (mesh.Primitives != null)
                {
                    for (int primIdx = 0; primIdx < mesh.Primitives.Count; primIdx++)
                    {
                        var subMesh = ParsePrimitive(
                            mesh.Primitives[primIdx], meshName, primIdx,
                            model, buffers, result.Materials);

                        // 应用世界变换到顶点位置
                        ApplyTransformToPositions(subMesh, worldMatrix);

                        result.SubMeshes.Add(subMesh);
                    }
                }
            }

            // 递归处理子节点
            if (node.Children != null)
            {
                foreach (int childIdx in node.Children)
                {
                    LoadNodeRecursive(model, childIdx, worldMatrix, buffers, result, worldMatrices);
                }
            }
        }

        /// <summary>解析单个 primitive 为 ModelSubMesh。</summary>
        private static ModelSubMesh ParsePrimitive(GlTFPrimitive prim, string meshName, int primIdx,
            GlTFModel model, List<byte[]> buffers, List<LoadedMaterial> materials)
        {
            var subMesh = new ModelSubMesh
            {
                Name = $"{meshName}_Prim{primIdx}",
                MaterialIndex = prim.Material ?? -1,
                Use32BitIndices = false
            };

            // 材质
            if (prim.Material.HasValue && prim.Material.Value >= 0 && prim.Material.Value < materials.Count)
            {
                subMesh.Material = materials[prim.Material.Value];
            }

            // 顶点属性
            prim.Attributes ??= new Dictionary<string, int>();

            // POSITION
            if (prim.Attributes.TryGetValue("POSITION", out int posAccessorIdx)
                && posAccessorIdx >= 0 && posAccessorIdx < model.Accessors.Count)
            {
                var accessor = model.Accessors[posAccessorIdx];
                var positions = ReadAccessorFloats(accessor, model.BufferViews, buffers);
                subMesh.Positions = positions;
            }

            // NORMAL
            if (prim.Attributes.TryGetValue("NORMAL", out int normAccessorIdx)
                && normAccessorIdx >= 0 && normAccessorIdx < model.Accessors.Count)
            {
                var accessor = model.Accessors[normAccessorIdx];
                subMesh.Normals = ReadAccessorFloats(accessor, model.BufferViews, buffers);

                // 变换法线（仅使用矩阵的旋转部分）
                if (subMesh.Normals != null)
                {
                    // 法线将在 LoadNodeRecursive 中统一变换
                }
            }

            // TEXCOORD_0
            if (prim.Attributes.TryGetValue("TEXCOORD_0", out int uvAccessorIdx)
                && uvAccessorIdx >= 0 && uvAccessorIdx < model.Accessors.Count)
            {
                var accessor = model.Accessors[uvAccessorIdx];
                subMesh.TexCoords0 = ReadAccessorFloats(accessor, model.BufferViews, buffers);
            }

            // 索引
            if (prim.Indices.HasValue && prim.Indices.Value >= 0 && prim.Indices.Value < model.Accessors.Count)
            {
                var accessor = model.Accessors[prim.Indices.Value];
                subMesh.Indices = ReadAccessorIndices(accessor, model.BufferViews, buffers);
                subMesh.Use32BitIndices = accessor.IsUnsignedInt;
            }
            else
            {
                // 无索引：生成顺序索引
                int vertCount = subMesh.VertexCount;
                subMesh.Indices = new uint[vertCount];
                for (int i = 0; i < vertCount; i++)
                    subMesh.Indices[i] = (uint)i;
                subMesh.Use32BitIndices = vertCount > 65535;
            }

            return subMesh;
        }

        /// <summary>从 accessor 读取浮点数据（顶点/法线/UV）。</summary>
        private static float[] ReadAccessorFloats(GlTFAccessor accessor, List<GlTFBufferView> bufferViews, List<byte[]> buffers)
        {
            if (accessor.BufferView < 0 || accessor.BufferView >= bufferViews.Count)
                throw new InvalidDataException($"Accessor references invalid bufferView index: {accessor.BufferView}");

            var bv = bufferViews[accessor.BufferView];
            int bufIdx = bv.Buffer;
            if (bufIdx < 0 || bufIdx >= buffers.Count)
                throw new InvalidDataException($"BufferView references invalid buffer index: {bufIdx}");

            byte[] bufData = buffers[bufIdx];
            int numComponents = accessor.GetNumComponents();
            int totalFloats = accessor.Count * numComponents;
            var result = new float[totalFloats];

            int srcOffset = bv.ByteOffset + accessor.ByteOffset;
            int componentSize = accessor.GetComponentSize();

            if (accessor.IsFloat)
            {
                // 直接读取 float
                Buffer.BlockCopy(bufData, srcOffset, result, 0, totalFloats * sizeof(float));
            }
            else
            {
                // 整型读取后转换为 float
                for (int i = 0; i < accessor.Count; i++)
                {
                    int elemOffset = srcOffset + i * numComponents * componentSize;
                    for (int c = 0; c < numComponents; c++)
                    {
                        int off = elemOffset + c * componentSize;
                        result[i * numComponents + c] = ReadScalarFloat(bufData, off, accessor.ComponentType);
                    }
                }
            }

            return result;
        }

        /// <summary>从 accessor 读取索引数据（统一转为 uint[]）。</summary>
        private static uint[] ReadAccessorIndices(GlTFAccessor accessor, List<GlTFBufferView> bufferViews, List<byte[]> buffers)
        {
            if (accessor.BufferView < 0 || accessor.BufferView >= bufferViews.Count)
                throw new InvalidDataException($"Index accessor references invalid bufferView: {accessor.BufferView}");

            var bv = bufferViews[accessor.BufferView];
            int bufIdx = bv.Buffer;
            if (bufIdx < 0 || bufIdx >= buffers.Count)
                throw new InvalidDataException($"BufferView references invalid buffer index: {bufIdx}");

            byte[] bufData = buffers[bufIdx];
            int srcOffset = bv.ByteOffset + accessor.ByteOffset;
            var result = new uint[accessor.Count];

            if (accessor.IsUnsignedByte)
            {
                for (int i = 0; i < accessor.Count; i++)
                    result[i] = bufData[srcOffset + i];
            }
            else if (accessor.IsUnsignedShort)
            {
                for (int i = 0; i < accessor.Count; i++)
                    result[i] = BitConverter.ToUInt16(bufData, srcOffset + i * 2);
            }
            else if (accessor.IsUnsignedInt)
            {
                for (int i = 0; i < accessor.Count; i++)
                    result[i] = BitConverter.ToUInt32(bufData, srcOffset + i * 4);
            }
            else
            {
                // 有符号整型作为索引
                for (int i = 0; i < accessor.Count; i++)
                {
                    switch (accessor.ComponentType)
                    {
                        case 5120: result[i] = (uint)bufData[srcOffset + i]; break;
                        case 5122: result[i] = (uint)BitConverter.ToInt16(bufData, srcOffset + i * 2); break;
                        default: result[i] = (uint)i; break;
                    }
                }
            }

            return result;
        }

        /// <summary>按组件类型读取单个标量值转为 float。</summary>
        private static float ReadScalarFloat(byte[] data, int offset, int componentType)
        {
            return componentType switch
            {
                5120 => (sbyte)data[offset],     // BYTE (signed)
                5121 => data[offset],              // UNSIGNED_BYTE
                5122 => BitConverter.ToInt16(data, offset),   // SHORT
                5123 => BitConverter.ToUInt16(data, offset),  // UNSIGNED_SHORT
                5125 => BitConverter.ToUInt32(data, offset),  // UNSIGNED_INT
                5126 => BitConverter.ToSingle(data, offset),   // FLOAT
                _ => 0f
            };
        }

        // ── 节点变换矩阵计算 ──

        /// <summary>计算所有节点的世界变换矩阵。</summary>
        private static Dictionary<int, Matrix4x4> ComputeWorldMatrices(GlTFModel model)
        {
            var worldMatrices = new Dictionary<int, Matrix4x4>();

            // 确定场景根节点
            HashSet<int> childNodes = new();
            if (model.Nodes != null)
            {
                foreach (var node in model.Nodes)
                {
                    if (node.Children != null)
                    {
                        foreach (int c in node.Children)
                            childNodes.Add(c);
                    }
                }
            }

            // 从根节点开始递归计算
            List<int> sceneRoots = new();
            if (model.DefaultScene.HasValue && model.DefaultScene.Value >= 0 && model.DefaultScene.Value < model.Scenes?.Count)
            {
                sceneRoots = model.Scenes[model.DefaultScene.Value].Nodes ?? new List<int>();
            }
            else if (model.Scenes?.Count > 0)
            {
                sceneRoots = model.Scenes[0].Nodes ?? new List<int>();
            }
            else
            {
                // 没有场景定义：从非子节点开始
                for (int i = 0; i < model.Nodes.Count; i++)
                    if (!childNodes.Contains(i)) sceneRoots.Add(i);
            }

            foreach (int root in sceneRoots)
            {
                ComputeWorldMatrixRecursive(model, root, Matrix4x4.Identity, worldMatrices);
            }

            return worldMatrices;
        }

        private static void ComputeWorldMatrixRecursive(GlTFModel model, int nodeIdx, Matrix4x4 parentWorld,
            Dictionary<int, Matrix4x4> worldMatrices)
        {
            if (nodeIdx < 0 || nodeIdx >= model.Nodes.Count) return;

            var node = model.Nodes[nodeIdx];
            Matrix4x4 local = ComputeLocalMatrix(node);
            Matrix4x4 world = local * parentWorld;
            worldMatrices[nodeIdx] = world;

            if (node.Children != null)
            {
                foreach (int child in node.Children)
                    ComputeWorldMatrixRecursive(model, child, world, worldMatrices);
            }
        }

        /// <summary>计算节点的局部变换矩阵（TRS 或 4x4 矩阵）。</summary>
        private static Matrix4x4 ComputeLocalMatrix(GlTFNode node)
        {
            // 如果指定了 matrix，直接使用（列主序）
            if (node.Matrix != null && node.Matrix.Length == 16)
            {
                return new Matrix4x4(
                    node.Matrix[0], node.Matrix[1], node.Matrix[2], node.Matrix[3],
                    node.Matrix[4], node.Matrix[5], node.Matrix[6], node.Matrix[7],
                    node.Matrix[8], node.Matrix[9], node.Matrix[10], node.Matrix[11],
                    node.Matrix[12], node.Matrix[13], node.Matrix[14], node.Matrix[15]);
            }

            // TRS 分解
            Vector3 translation = node.Translation != null && node.Translation.Length >= 3
                ? new Vector3(node.Translation[0], node.Translation[1], node.Translation[2])
                : Vector3.Zero;

            Quaternion rotation = node.Rotation != null && node.Rotation.Length >= 4
                ? new Quaternion(node.Rotation[0], node.Rotation[1], node.Rotation[2], node.Rotation[3])
                : Quaternion.Identity;

            Vector3 scale = node.Scale != null && node.Scale.Length >= 3
                ? new Vector3(node.Scale[0], node.Scale[1], node.Scale[2])
                : Vector3.One;

            return Matrix4x4.CreateScale(scale) *
                   Matrix4x4.CreateFromQuaternion(rotation) *
                   Matrix4x4.CreateTranslation(translation);
        }

        // ── 顶点变换 ──

        /// <summary>将世界变换矩阵应用到顶点位置。</summary>
        private static void ApplyTransformToPositions(ModelSubMesh subMesh, Matrix4x4 transform)
        {
            if (subMesh.Positions == null || subMesh.Positions.Length == 0) return;

            for (int i = 0; i < subMesh.VertexCount; i++)
            {
                int idx = i * 3;
                Vector3 pos = new Vector3(subMesh.Positions[idx], subMesh.Positions[idx + 1], subMesh.Positions[idx + 2]);
                Vector3 transformed = Vector3.Transform(pos, transform);
                subMesh.Positions[idx] = transformed.X;
                subMesh.Positions[idx + 1] = transformed.Y;
                subMesh.Positions[idx + 2] = transformed.Z;
            }

            // 变换法线（使用逆转置矩阵）
            if (subMesh.Normals != null && subMesh.Normals.Length >= subMesh.VertexCount * 3)
            {
                Matrix4x4 inverseTranspose;
                if (Matrix4x4.Invert(transform, out inverseTranspose))
                {
                    // 转置
                    inverseTranspose = Matrix4x4.Transpose(inverseTranspose);
                    // 只取旋转部分（3x3）
                    for (int i = 0; i < subMesh.VertexCount; i++)
                    {
                        int idx = i * 3;
                        Vector3 normal = new Vector3(subMesh.Normals[idx], subMesh.Normals[idx + 1], subMesh.Normals[idx + 2]);
                        Vector3 transformedNormal = Vector3.TransformNormal(normal, inverseTranspose);
                        // 重新归一化
                        transformedNormal = Vector3.Normalize(transformedNormal);
                        subMesh.Normals[idx] = transformedNormal.X;
                        subMesh.Normals[idx + 1] = transformedNormal.Y;
                        subMesh.Normals[idx + 2] = transformedNormal.Z;
                    }
                }
            }
        }

        // ── 材质解析 ──

        /// <summary>解析 glTF 材质为引擎 LoadedMaterial。</summary>
        private static LoadedMaterial ParseMaterial(GlTFMaterial mat, GlTFModel model, string sourcePath)
        {
            var result = new LoadedMaterial
            {
                Name = mat.Name ?? "Material",
                DoubleSided = mat.DoubleSided ?? false,
                AlphaMode = mat.AlphaMode switch
                {
                    "MASK" => "MASK",
                    "BLEND" => "BLEND",
                    _ => "OPAQUE"
                },
                AlphaCutoff = (float)(mat.AlphaCutoff ?? 0.5)
            };

            // 自发光
            if (mat.EmissiveFactor != null && mat.EmissiveFactor.Length >= 3)
                result.EmissiveFactor = new Vector3(mat.EmissiveFactor[0], mat.EmissiveFactor[1], mat.EmissiveFactor[2]);

            // PBR 金属度/粗糙度
            if (mat.PbrMetallicRoughness != null)
            {
                var pbr = mat.PbrMetallicRoughness;

                if (pbr.BaseColorFactor != null && pbr.BaseColorFactor.Length >= 4)
                    result.BaseColorFactor = new Vector4(
                        pbr.BaseColorFactor[0], pbr.BaseColorFactor[1],
                        pbr.BaseColorFactor[2], pbr.BaseColorFactor[3]);

                result.MetallicFactor = (float)(pbr.MetallicFactor ?? 1.0);
                result.RoughnessFactor = (float)(pbr.RoughnessFactor ?? 1.0);

                // 纹理路径
                if (pbr.BaseColorTexture != null)
                    result.BaseColorTexturePath = ResolveImagePath(pbr.BaseColorTexture.Index, model, sourcePath);

                if (pbr.MetallicRoughnessTexture != null)
                    result.MetallicRoughnessTexturePath = ResolveImagePath(pbr.MetallicRoughnessTexture.Index, model, sourcePath);
            }

            if (mat.NormalTexture != null)
                result.NormalTexturePath = ResolveImagePath(mat.NormalTexture.Index, model, sourcePath);

            return result;
        }

        /// <summary>解析 glTF image 索引为文件路径。</summary>
        private static string? ResolveImagePath(int textureIdx, GlTFModel model, string sourcePath)
        {
            if (textureIdx < 0 || textureIdx >= model.Textures?.Count) return null;

            var texture = model.Textures[textureIdx];
            if (!texture.Source.HasValue) return null;

            int imageIdx = texture.Source.Value;
            if (imageIdx < 0 || imageIdx >= model.Images?.Count) return null;

            var image = model.Images[imageIdx];
            if (string.IsNullOrWhiteSpace(image.Uri)) return null;

            // data URI 不返回路径
            if (image.Uri.StartsWith("data:")) return null;

            // 相对路径
            string basePath = Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? string.Empty;
            return Path.Combine(basePath, image.Uri);
        }

        // ── JSON 序列化配置 ──

        private static JsonSerializerOptions GetJsonOptions()
        {
            return new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };
        }
    }
}
