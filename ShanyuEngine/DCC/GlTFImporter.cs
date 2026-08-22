// =====================================================================
//  ShanyuEngine - GlTFImporter: glTF 2.0 pure C# importer
//  Namespace: ShanyuEngine.DCC
// =====================================================================
//  Supported: .gltf (JSON) and .glb (binary) formats
//  Core glTF 2.0 features: nodes, meshes, materials, textures,
//  animations, skins, cameras, lights, morph targets
// =====================================================================

using System.Text.Json;
using System.Text.Json.Serialization;
using ShanyuEngine.Assets;
using ShanyuEngine.Assets.Importers;

namespace ShanyuEngine.DCC
{
    #region Import Configuration

    public sealed class GlTFImportConfig
    {
        public bool ImportGeometry { get; set; } = true;
        public bool ImportMaterials { get; set; } = true;
        public bool ImportTextures { get; set; } = true;
        public bool ImportAnimations { get; set; } = true;
        public bool ImportSkins { get; set; } = true;
        public bool ImportCameras { get; set; } = true;
        public bool ImportLights { get; set; } = true;
        public bool ImportMorphTargets { get; set; } = true;
        public bool GenerateTangents { get; set; } = true;
        public bool FlipUVs { get; set; } = false;
        public float ScaleFactor { get; set; } = 1.0f;
    }

    #endregion

    #region glTF JSON Schema Types

    internal sealed class GlTFRoot
    {
        public GlTFAsset? Asset { get; set; }
        public int? Scene { get; set; }
        public GlTFScene[]? Scenes { get; set; }
        public GlTFNode[]? Nodes { get; set; }
        public GlTFMesh[]? Meshes { get; set; }
        public GlTFAccessor[]? Accessors { get; set; }
        public GlTFBufferView[]? BufferViews { get; set; }
        public GlTFBuffer[]? Buffers { get; set; }
        public GlTFMaterial[]? Materials { get; set; }
        public GlTFTexture[]? Textures { get; set; }
        public GlTFImage[]? Images { get; set; }
        public GlTFSampler[]? Samplers { get; set; }
        public GlTFSkin[]? Skins { get; set; }
        public GlTFAnimation[]? Animations { get; set; }
        public GlTFCamera[]? Cameras { get; set; }
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }
    }

    internal sealed class GlTFAsset
    {
        public string? Version { get; set; }
        public string? Generator { get; set; }
        public string? Copyright { get; set; }
        public string? MinVersion { get; set; }
    }

    internal sealed class GlTFScene
    {
        public string? Name { get; set; }
        public int[]? Nodes { get; set; }
    }

    internal sealed class GlTFNode
    {
        public string? Name { get; set; }
        public int? Mesh { get; set; }
        public int? Skin { get; set; }
        public int? Camera { get; set; }
        public int[]? Children { get; set; }
        public float[]? Matrix { get; set; }
        public float[]? Translation { get; set; }
        public float[]? Rotation { get; set; }
        public float[]? Scale { get; set; }
        public float[]? Weights { get; set; }
    }

    internal sealed class GlTFMesh
    {
        public string? Name { get; set; }
        public GlTFPrimitive[]? Primitives { get; set; }
        public float[]? Weights { get; set; }
    }

    internal sealed class GlTFPrimitive
    {
        public Dictionary<string, int>? Attributes { get; set; }
        public int? Indices { get; set; }
        public int? Material { get; set; }
        public int Mode { get; set; } = 4; // TRIANGLES
        public GlTFMorphTarget[]? Targets { get; set; }
    }

    internal sealed class GlTFMorphTarget
    {
        public Dictionary<string, int>? Attributes { get; set; }
    }

    internal enum GlTFComponentType
    {
        BYTE = 5120,
        UNSIGNED_BYTE = 5121,
        SHORT = 5122,
        UNSIGNED_SHORT = 5123,
        UNSIGNED_INT = 5125,
        FLOAT = 5126
    }

    internal enum GlTFAccessorType
    {
        SCALAR, VEC2, VEC3, VEC4, MAT2, MAT3, MAT4
    }

    internal sealed class GlTFAccessor
    {
        public int? BufferView { get; set; }
        public int ByteOffset { get; set; }
        public int ComponentType { get; set; }
        public int Count { get; set; }
        public string? Type { get; set; }
        public bool Normalized { get; set; }
        public float[]? Min { get; set; }
        public float[]? Max { get; set; }
        public GlTFSparse? Sparse { get; set; }
    }

    internal sealed class GlTFSparse
    {
        public int Count { get; set; }
        public GlTFSparseIndices? Indices { get; set; }
        public GlTFSparseValues? Values { get; set; }
    }

    internal sealed class GlTFSparseIndices
    {
        public int? BufferView { get; set; }
        public int ByteOffset { get; set; }
        public int ComponentType { get; set; }
    }

    internal sealed class GlTFSparseValues
    {
        public int? BufferView { get; set; }
        public int ByteOffset { get; set; }
    }

    internal sealed class GlTFBufferView
    {
        public int? Buffer { get; set; }
        public int ByteOffset { get; set; }
        public int ByteLength { get; set; }
        public int? ByteStride { get; set; }
        public int? Target { get; set; }
    }

    internal sealed class GlTFBuffer
    {
        public int ByteLength { get; set; }
        public string? Uri { get; set; }
    }

    internal sealed class GlTFMaterial
    {
        public string? Name { get; set; }
        public GlTFPbrMetallicRoughness? PbrMetallicRoughness { get; set; }
        public GlTFNormalTextureInfo? NormalTexture { get; set; }
        public GlTFOcclusionTextureInfo? OcclusionTexture { get; set; }
        public GlTFTextureInfo? EmissiveTexture { get; set; }
        public float[]? EmissiveFactor { get; set; }
        public string? AlphaMode { get; set; } = "OPAQUE";
        public float AlphaCutoff { get; set; } = 0.5f;
        public bool DoubleSided { get; set; }
    }

    internal sealed class GlTFPbrMetallicRoughness
    {
        public float[]? BaseColorFactor { get; set; }
        public GlTFTextureInfo? BaseColorTexture { get; set; }
        public float MetallicFactor { get; set; } = 1.0f;
        public float RoughnessFactor { get; set; } = 1.0f;
        public GlTFTextureInfo? MetallicRoughnessTexture { get; set; }
    }

    internal sealed class GlTFTextureInfo
    {
        public int Index { get; set; }
        public int TexCoord { get; set; }
    }

    internal sealed class GlTFNormalTextureInfo
    {
        public int Index { get; set; }
        public int TexCoord { get; set; }
        public float Scale { get; set; } = 1.0f;
    }

    internal sealed class GlTFOcclusionTextureInfo
    {
        public int Index { get; set; }
        public int TexCoord { get; set; }
        public float Strength { get; set; } = 1.0f;
    }

    internal sealed class GlTFTexture
    {
        public int? Source { get; set; }
        public int? Sampler { get; set; }
    }

    internal sealed class GlTFImage
    {
        public string? Uri { get; set; }
        public string? MimeType { get; set; }
        public int? BufferView { get; set; }
    }

    internal sealed class GlTFSampler
    {
        public int? MagFilter { get; set; }
        public int? MinFilter { get; set; }
        public int? WrapS { get; set; }
        public int? WrapT { get; set; }
    }

    internal sealed class GlTFSkin
    {
        public string? Name { get; set; }
        public int? InverseBindMatrices { get; set; }
        public int[]? Joints { get; set; }
        public int? Skeleton { get; set; }
    }

    internal sealed class GlTFAnimation
    {
        public string? Name { get; set; }
        public GlTFAnimationChannel[]? Channels { get; set; }
        public GlTFAnimationSampler[]? Samplers { get; set; }
    }

    internal sealed class GlTFAnimationChannel
    {
        public int Sampler { get; set; }
        public GlTFAnimationTarget? Target { get; set; }
    }

    internal sealed class GlTFAnimationTarget
    {
        public int? Node { get; set; }
        public string? Path { get; set; }
    }

    internal sealed class GlTFAnimationSampler
    {
        public int Input { get; set; }
        public int Output { get; set; }
        public string? Interpolation { get; set; } = "LINEAR";
    }

    internal sealed class GlTFCamera
    {
        public string? Name { get; set; }
        public string? Type { get; set; }
        public GlTFPerspectiveCamera? Perspective { get; set; }
        public GlTFOrthographicCamera? Orthographic { get; set; }
    }

    internal sealed class GlTFPerspectiveCamera
    {
        public float? AspectRatio { get; set; }
        public float YFov { get; set; }
        public float ZFar { get; set; }
        public float ZNear { get; set; }
    }

    internal sealed class GlTFOrthographicCamera
    {
        public float XMag { get; set; }
        public float YMag { get; set; }
        public float ZFar { get; set; }
        public float ZNear { get; set; }
    }

    #endregion

    #region Accessor Data Cache

    internal struct GlTFAccessorData
    {
        public float[] FloatData;
        public int[] IntData;
        public int ComponentsPerElement;
        public int Count;
    }

    #endregion

    #region Runtime Data

    public struct GlTFImportMesh
    {
        public string Name;
        public int[] Indices;
        public float[] Positions;
        public float[] Normals;
        public float[] Tangents;
        public float[] UV0;
        public float[] UV1;
        public float[] Colors;
        public float[] Joints;
        public float[] Weights;
        public int MaterialIndex;
        public int PrimitiveMode;
    }

    public struct GlTFImportMaterial
    {
        public string Name;
        public float[] BaseColorFactor;
        public int BaseColorTextureIndex;
        public float MetallicFactor;
        public float RoughnessFactor;
        public int MetallicRoughnessTextureIndex;
        public int NormalTextureIndex;
        public int OcclusionTextureIndex;
        public float[] EmissiveFactor;
        public int EmissiveTextureIndex;
        public string AlphaMode;
        public float AlphaCutoff;
        public bool DoubleSided;
    }

    public struct GlTFImportNode
    {
        public string Name;
        public int? MeshIndex;
        public int? SkinIndex;
        public int? CameraIndex;
        public int[] Children;
        public float[] LocalMatrix;
    }

    public struct GlTFImportAnimation
    {
        public string Name;
        public GlTFImportChannel[] Channels;
    }

    public struct GlTFImportChannel
    {
        public int NodeIndex;
        public string TargetPath;
        public float[] KeyframeTimes;
        public float[] KeyframeValues;
        public string Interpolation;
    }

    public struct GlTFImportSkin
    {
        public string Name;
        public int[] JointIndices;
        public float[] InverseBindMatrices;
    }

    #endregion

    #region Main Importer

    public sealed class GlTFImporter
    {
        private readonly string _filePath;
        private readonly GlTFImportConfig _config;
        private readonly List<string> _messages = new();
        private byte[]? _glbBinChunk;
        private GlTFRoot? _root;
        private string _assetBaseDir = string.Empty;

        private byte[][] _bufferData = Array.Empty<byte[]>();
        private GlTFAccessorData[] _accessorCache = Array.Empty<GlTFAccessorData>();

        public InterchangeScene? Scene { get; private set; }

        public GlTFImporter(string filePath, GlTFImportConfig? config = null)
        {
            _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
            _config = config ?? new GlTFImportConfig();
        }

        public bool Import()
        {
            try
            {
                _assetBaseDir = Path.GetDirectoryName(_filePath) ?? ".";
                ParseFile();
                if (_root == null) return false;

                LoadBufferData();
                CacheAccessors();
                BuildScene();
                return true;
            }
            catch (Exception ex)
            {
                _messages.Add($"Import failed: {ex.Message}");
                return false;
            }
        }

        public string[] GetMessages() => _messages.ToArray();

        #region File Parsing

        private void ParseFile()
        {
            string ext = Path.GetExtension(_filePath).ToLowerInvariant();
            if (ext == ".glb")
                ParseGLB();
            else
                ParseGLTF();
        }

        private void ParseGLTF()
        {
            string jsonStr = File.ReadAllText(_filePath);
            _root = JsonSerializer.Deserialize<GlTFRoot>(jsonStr, _jsonOptions);
        }

        private void ParseGLB()
        {
            byte[] fileBytes = File.ReadAllBytes(_filePath);
            if (fileBytes.Length < 12)
                throw new InvalidDataException("Invalid GLB file: too small");

            uint magic = BitConverter.ToUInt32(fileBytes, 0);
            if (magic != 0x46546C67)
                throw new InvalidDataException("Invalid GLB magic number");

            uint version = BitConverter.ToUInt32(fileBytes, 4);
            if (version != 2)
                throw new InvalidDataException($"Unsupported glTF version: {version}");

            int offset = 12;
            string? jsonStr = null;

            while (offset + 8 <= fileBytes.Length)
            {
                uint chunkLength = BitConverter.ToUInt32(fileBytes, offset);
                uint chunkType = BitConverter.ToUInt32(fileBytes, offset + 4);

                if (chunkType == 0x4E4F534A)
                {
                    jsonStr = System.Text.Encoding.UTF8.GetString(fileBytes, offset + 8, (int)chunkLength);
                }
                else if (chunkType == 0x004E4942)
                {
                    _glbBinChunk = new byte[chunkLength];
                    Array.Copy(fileBytes, offset + 8, _glbBinChunk, 0, (int)chunkLength);
                }

                offset += 8 + (int)chunkLength;
            }

            if (jsonStr == null)
                throw new InvalidDataException("GLB file has no JSON chunk");

            _root = JsonSerializer.Deserialize<GlTFRoot>(jsonStr, _jsonOptions);
        }

        #endregion

        #region Buffer Loading

        private void LoadBufferData()
        {
            if (_root?.Buffers == null || _root.Buffers.Length == 0)
                return;

            _bufferData = new byte[_root.Buffers.Length][];
            for (int i = 0; i < _root.Buffers.Length; i++)
            {
                var buffer = _root.Buffers[i];
                if (string.IsNullOrEmpty(buffer.Uri))
                {
                    if (_glbBinChunk != null && _glbBinChunk.Length >= buffer.ByteLength)
                    {
                        _bufferData[i] = _glbBinChunk;
                        continue;
                    }
                    throw new InvalidDataException($"GLB buffer {i} has no URI and no BIN chunk");
                }

                string uri = buffer.Uri;
                if (uri.StartsWith("data:"))
                    _bufferData[i] = DecodeDataUri(uri);
                else
                {
                    string bufferPath = Path.Combine(_assetBaseDir, Uri.UnescapeDataString(uri));
                    _bufferData[i] = File.ReadAllBytes(bufferPath);
                }
            }
        }

        private static byte[] DecodeDataUri(string uri)
        {
            int commaIndex = uri.IndexOf(',');
            if (commaIndex < 0)
                throw new InvalidDataException("Invalid data URI");

            string encoded = uri[(commaIndex + 1)..];
            string prefix = uri[..commaIndex];
            if (prefix.Contains("base64", StringComparison.OrdinalIgnoreCase))
                return Convert.FromBase64String(encoded);

            return System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(encoded));
        }

        #endregion

        #region Accessor Caching

        private void CacheAccessors()
        {
            if (_root?.Accessors == null) return;
            _accessorCache = new GlTFAccessorData[_root.Accessors.Length];

            for (int i = 0; i < _root.Accessors.Length; i++)
                _accessorCache[i] = ReadAccessor(_root.Accessors[i]);
        }

        private GlTFAccessorData ReadAccessor(GlTFAccessor accessor)
        {
            var result = new GlTFAccessorData();
            int compCount = GetComponentCount(accessor.Type);
            int compSize = GetComponentByteSize((GlTFComponentType)accessor.ComponentType);
            result.ComponentsPerElement = compCount;
            result.Count = accessor.Count;

            int elementSize = compCount * compSize;
            int totalSize = accessor.Count * elementSize;

            float[] floatData = new float[accessor.Count * compCount];
            int[] intData = Array.Empty<int>();
            bool isIntType = accessor.ComponentType != (int)GlTFComponentType.FLOAT;

            if (accessor.BufferView.HasValue)
            {
                var bufferView = _root?.BufferViews?[accessor.BufferView.Value];
                if (bufferView != null)
                {
                    int bufferIdx = bufferView.Buffer ?? 0;
                    int byteStride = bufferView.ByteStride ?? elementSize;
                    byte[] srcBuffer = _bufferData[bufferIdx];
                    int baseOffset = bufferView.ByteOffset + accessor.ByteOffset;

                    for (int j = 0; j < accessor.Count; j++)
                    {
                        int srcOffset = baseOffset + j * byteStride;
                        for (int c = 0; c < compCount; c++)
                        {
                            int offset = srcOffset + c * compSize;
                            if (isIntType)
                                floatData[j * compCount + c] = ReadIntComponent(srcBuffer, offset, (GlTFComponentType)accessor.ComponentType, accessor.Normalized);
                            else
                                floatData[j * compCount + c] = BitConverter.ToSingle(srcBuffer, offset);
                        }
                    }
                }
            }

            // Handle sparse accessor
            if (accessor.Sparse != null)
                ApplySparseAccessor(accessor.Sparse, floatData, compCount);

            result.FloatData = floatData;
            result.IntData = intData;
            return result;
        }

        private void ApplySparseAccessor(GlTFSparse sparse, float[] data, int compCount)
        {
            if (sparse.Indices == null || sparse.Values == null) return;
            if (sparse.Indices.BufferView == null || sparse.Values.BufferView == null) return;

            var indexView = _root?.BufferViews?[sparse.Indices.BufferView.Value];
            var valueView = _root?.BufferViews?[sparse.Values.BufferView.Value];
            if (indexView == null || valueView == null) return;

            byte[] srcBuffer = _bufferData[indexView.Buffer ?? 0];
            byte[] valBuffer = _bufferData[valueView.Buffer ?? 0];
            int idxCompSize = GetComponentByteSize((GlTFComponentType)sparse.Indices.ComponentType);

            for (int i = 0; i < sparse.Count; i++)
            {
                int idxOffset = indexView.ByteOffset + sparse.Indices.ByteOffset + i * idxCompSize;
                int elemIndex = (int)ReadIntComponent(srcBuffer, idxOffset, (GlTFComponentType)sparse.Indices.ComponentType, false);

                int valOffset = valueView.ByteOffset + sparse.Values.ByteOffset + i * compCount * 4;
                for (int c = 0; c < compCount; c++)
                    data[elemIndex * compCount + c] = BitConverter.ToSingle(valBuffer, valOffset + c * 4);
            }
        }

        private static float ReadIntComponent(byte[] buffer, int offset, GlTFComponentType type, bool normalized)
        {
            float value = type switch
            {
                GlTFComponentType.BYTE => (sbyte)buffer[offset],
                GlTFComponentType.UNSIGNED_BYTE => buffer[offset],
                GlTFComponentType.SHORT => BitConverter.ToInt16(buffer, offset),
                GlTFComponentType.UNSIGNED_SHORT => BitConverter.ToUInt16(buffer, offset),
                GlTFComponentType.UNSIGNED_INT => BitConverter.ToUInt32(buffer, offset),
                _ => 0
            };

            if (!normalized) return value;

            return type switch
            {
                GlTFComponentType.BYTE => Math.Max(value / 127.0f, -1.0f),
                GlTFComponentType.UNSIGNED_BYTE => value / 255.0f,
                GlTFComponentType.SHORT => Math.Max(value / 32767.0f, -1.0f),
                GlTFComponentType.UNSIGNED_SHORT => value / 65535.0f,
                _ => value
            };
        }

        #endregion

        #region Scene Building

        private void BuildScene()
        {
            var scene = new InterchangeScene();

            if (_config.ImportGeometry && _root?.Meshes != null)
                ImportMeshes(scene);

            if (_config.ImportMaterials && _root?.Materials != null)
                ImportMaterials(scene);

            if (_config.ImportAnimations && _root?.Animations != null)
                ImportAnimations(scene);

            if (_config.ImportSkins && _root?.Skins != null)
                ImportSkins(scene);

            ImportSceneHierarchy(scene);

            Scene = scene;
        }

        private void ImportMeshes(InterchangeScene scene)
        {
            List<GlTFImportMesh> meshes = new();
            for (int mi = 0; mi < _root!.Meshes!.Length; mi++)
            {
                var gltfMesh = _root.Meshes[mi];
                string meshName = gltfMesh.Name ?? $"mesh_{mi}";

                if (gltfMesh.Primitives == null) continue;

                foreach (var prim in gltfMesh.Primitives)
                {
                    var mesh = new GlTFImportMesh
                    {
                        Name = meshName,
                        MaterialIndex = prim.Material ?? -1,
                        PrimitiveMode = prim.Mode,
                        Indices = ReadIndices(prim.Indices),
                        Positions = ReadAttribute(prim.Attributes, "POSITION", 3),
                        Normals = ReadAttribute(prim.Attributes, "NORMAL", 3),
                        Tangents = ReadAttribute(prim.Attributes, "TANGENT", 4),
                        UV0 = ReadAttribute(prim.Attributes, "TEXCOORD_0", 2),
                        UV1 = ReadAttribute(prim.Attributes, "TEXCOORD_1", 2),
                        Colors = ReadAttribute(prim.Attributes, "COLOR_0", 4),
                        Joints = ReadAttribute(prim.Attributes, "JOINTS_0", 4),
                        Weights = ReadAttribute(prim.Attributes, "WEIGHTS_0", 4),
                    };

                    // Fix winding order if needed
                    if (_config.FlipUVs && mesh.UV0 != null)
                    {
                        for (int i = 1; i < mesh.UV0.Length; i += 2)
                            mesh.UV0[i] = 1.0f - mesh.UV0[i];
                    }

                    meshes.Add(mesh);
                }
            }
            scene.SetImportMeshes(meshes.ToArray());
        }

        private int[] ReadIndices(int? accessorIndex)
        {
            if (accessorIndex == null || _root?.Accessors == null || accessorIndex.Value >= _root.Accessors.Length)
                return Array.Empty<int>();

            var accessor = _root.Accessors[accessorIndex.Value];
            var data = _accessorCache[accessorIndex.Value];

            if (accessor.ComponentType == (int)GlTFComponentType.UNSIGNED_INT)
            {
                int[] result = new int[data.Count];
                for (int i = 0; i < data.Count; i++)
                    result[i] = (int)data.FloatData[i];
                return result;
            }

            return Array.ConvertAll(data.FloatData, f => (int)f);
        }

        private float[] ReadAttribute(Dictionary<string, int>? attributes, string name, int expectedComps)
        {
            if (attributes == null || !attributes.TryGetValue(name, out int accessorIdx))
                return Array.Empty<float>();

            var data = _accessorCache[accessorIdx];
            if (data.FloatData == null || data.FloatData.Length == 0)
                return Array.Empty<float>();

            int actualComps = data.ComponentsPerElement;
            if (actualComps < expectedComps)
            {
                float[] padded = new float[data.Count * expectedComps];
                for (int i = 0; i < data.Count; i++)
                {
                    for (int c = 0; c < actualComps; c++)
                        padded[i * expectedComps + c] = data.FloatData[i * actualComps + c];
                    for (int c = actualComps; c < expectedComps; c++)
                        padded[i * expectedComps + c] = c == 3 ? 1.0f : 0.0f;
                }
                return padded;
            }

            return data.FloatData;
        }

        private void ImportMaterials(InterchangeScene scene)
        {
            List<GlTFImportMaterial> materials = new();
            for (int i = 0; i < _root!.Materials!.Length; i++)
            {
                var gltfMat = _root.Materials[i];
                var pbr = gltfMat.PbrMetallicRoughness;

                materials.Add(new GlTFImportMaterial
                {
                    Name = gltfMat.Name ?? $"material_{i}",
                    BaseColorFactor = pbr?.BaseColorFactor ?? new float[] { 1, 1, 1, 1 },
                    BaseColorTextureIndex = pbr?.BaseColorTexture?.Index ?? -1,
                    MetallicFactor = pbr?.MetallicFactor ?? 1.0f,
                    RoughnessFactor = pbr?.RoughnessFactor ?? 1.0f,
                    MetallicRoughnessTextureIndex = pbr?.MetallicRoughnessTexture?.Index ?? -1,
                    NormalTextureIndex = gltfMat.NormalTexture?.Index ?? -1,
                    OcclusionTextureIndex = gltfMat.OcclusionTexture?.Index ?? -1,
                    EmissiveFactor = gltfMat.EmissiveFactor ?? new float[] { 0, 0, 0 },
                    EmissiveTextureIndex = gltfMat.EmissiveTexture?.Index ?? -1,
                    AlphaMode = gltfMat.AlphaMode ?? "OPAQUE",
                    AlphaCutoff = gltfMat.AlphaCutoff,
                    DoubleSided = gltfMat.DoubleSided,
                });
            }
            scene.SetImportMaterials(materials.ToArray());
        }

        private void ImportAnimations(InterchangeScene scene)
        {
            List<GlTFImportAnimation> animations = new();
            for (int ai = 0; ai < _root!.Animations!.Length; ai++)
            {
                var gltfAnim = _root.Animations[ai];
                List<GlTFImportChannel> channels = new();

                if (gltfAnim.Channels != null && gltfAnim.Samplers != null)
                {
                    foreach (var channel in gltfAnim.Channels)
                    {
                        if (channel.Target == null) continue;
                        if (channel.Sampler >= gltfAnim.Samplers.Length) continue;

                        var sampler = gltfAnim.Samplers[channel.Sampler];
                        var timeData = _accessorCache[sampler.Input];
                        var valueData = _accessorCache[sampler.Output];

                        channels.Add(new GlTFImportChannel
                        {
                            NodeIndex = channel.Target.Node ?? -1,
                            TargetPath = channel.Target.Path ?? "",
                            KeyframeTimes = timeData.FloatData?.ToArray() ?? Array.Empty<float>(),
                            KeyframeValues = valueData.FloatData?.ToArray() ?? Array.Empty<float>(),
                            Interpolation = sampler.Interpolation ?? "LINEAR",
                        });
                    }
                }

                animations.Add(new GlTFImportAnimation
                {
                    Name = gltfAnim.Name ?? $"animation_{ai}",
                    Channels = channels.ToArray(),
                });
            }
            scene.SetImportAnimations(animations.ToArray());
        }

        private void ImportSkins(InterchangeScene scene)
        {
            List<GlTFImportSkin> skins = new();
            for (int si = 0; si < _root!.Skins!.Length; si++)
            {
                var gltfSkin = _root.Skins[si];
                float[] ibm = Array.Empty<float>();
                if (gltfSkin.InverseBindMatrices.HasValue)
                {
                    var ibmData = _accessorCache[gltfSkin.InverseBindMatrices.Value];
                    ibm = ibmData.FloatData?.ToArray() ?? Array.Empty<float>();
                }

                skins.Add(new GlTFImportSkin
                {
                    Name = gltfSkin.Name ?? $"skin_{si}",
                    JointIndices = gltfSkin.Joints ?? Array.Empty<int>(),
                    InverseBindMatrices = ibm,
                });
            }
            scene.SetImportSkins(skins.ToArray());
        }

        private void ImportSceneHierarchy(InterchangeScene scene)
        {
            if (_root?.Nodes == null) return;

            int[]? rootNodes = null;
            if (_root.Scene.HasValue && _root.Scenes != null && _root.Scene.Value < _root.Scenes.Length)
                rootNodes = _root.Scenes[_root.Scene.Value].Nodes;

            List<GlTFImportNode> nodes = new();
            for (int i = 0; i < _root.Nodes.Length; i++)
            {
                var node = _root.Nodes[i];
                float[] matrix = BuildNodeMatrix(node);
                nodes.Add(new GlTFImportNode
                {
                    Name = node.Name ?? $"node_{i}",
                    MeshIndex = node.Mesh,
                    SkinIndex = node.Skin,
                    CameraIndex = node.Camera,
                    Children = node.Children ?? Array.Empty<int>(),
                    LocalMatrix = matrix,
                });
            }
            scene.SetImportNodes(nodes.ToArray(), rootNodes ?? Array.Empty<int>());
        }

        private static float[] BuildNodeMatrix(GlTFNode node)
        {
            if (node.Matrix != null && node.Matrix.Length == 16)
                return node.Matrix;

            float[] tx = node.Translation ?? new float[] { 0, 0, 0 };
            float[] rot = node.Rotation ?? new float[] { 0, 0, 0, 1 };
            float[] sc = node.Scale ?? new float[] { 1, 1, 1 };

            float qx = rot[0], qy = rot[1], qz = rot[2], qw = rot[3];
            float[,] m = new float[4, 4];
            m[0, 0] = (1 - 2 * (qy * qy + qz * qz)) * sc[0];
            m[0, 1] = (2 * (qx * qy - qz * qw)) * sc[0];
            m[0, 2] = (2 * (qx * qz + qy * qw)) * sc[0];
            m[0, 3] = 0;
            m[1, 0] = (2 * (qx * qy + qz * qw)) * sc[1];
            m[1, 1] = (1 - 2 * (qx * qx + qz * qz)) * sc[1];
            m[1, 2] = (2 * (qy * qz - qx * qw)) * sc[1];
            m[1, 3] = 0;
            m[2, 0] = (2 * (qx * qz - qy * qw)) * sc[2];
            m[2, 1] = (2 * (qy * qz + qx * qw)) * sc[2];
            m[2, 2] = (1 - 2 * (qx * qx + qy * qy)) * sc[2];
            m[2, 3] = 0;
            m[3, 0] = tx[0]; m[3, 1] = tx[1]; m[3, 2] = tx[2]; m[3, 3] = 1;

            float[] flat = new float[16];
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                    flat[r * 4 + c] = m[r, c];
            return flat;
        }

        #endregion

        #region Utilities

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            IncludeFields = false,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        private static int GetComponentCount(string? type) => type switch
        {
            "SCALAR" => 1,
            "VEC2" => 2,
            "VEC3" => 3,
            "VEC4" => 4,
            "MAT2" => 4,
            "MAT3" => 9,
            "MAT4" => 16,
            _ => 3,
        };

        private static int GetComponentByteSize(GlTFComponentType type) => type switch
        {
            GlTFComponentType.BYTE => 1,
            GlTFComponentType.UNSIGNED_BYTE => 1,
            GlTFComponentType.SHORT => 2,
            GlTFComponentType.UNSIGNED_SHORT => 2,
            GlTFComponentType.UNSIGNED_INT => 4,
            GlTFComponentType.FLOAT => 4,
            _ => 4,
        };

        #endregion
    }

    #endregion
}
