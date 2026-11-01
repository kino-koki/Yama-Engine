// =====================================================================================
//  YamaEngine - glTFImporter：glTF 2.0 完整导入器
//  命名空间：YamaEngine.Assets.Importers
// -------------------------------------------------------------------------------------
//  纯 C# 实现，支持 Khronos Group glTF 2.0 规范：
//    - JSON 格式 (.gltf) 与二进制容器 (.glb) 解析
//    - 完整数据模型：Asset/Scene/Node/Mesh/Primitive/Accessor/BufferView/Buffer
//      /Material/Texture/Image/Sampler/Skin/Animation/Camera
//    - 扩展支持：KHR_materials_pbrSpecularGlossiness, KHR_materials_unlit,
//      KHR_materials_clearcoat, KHR_materials_transmission, KHR_materials_volume,
//      KHR_materials_ior, KHR_materials_sheen, KHR_materials_specular,
//      KHR_materials_emissive_strength, KHR_lights_punctual,
//      KHR_draco_mesh_compression, KHR_mesh_quantization,
//      KHR_texture_basisu, KHR_texture_transform
//    - Draco 解压占位（接口 + 回调）
//    - 坐标系转换（右手→左手）、单位转换
//    - 蒙皮绑定、动画导入、Morph Target 导入
//    - 导入选项（缩放/坐标系/生成切线/计算法线/合并网格/优化/LOD）
//    - 导入进度回调
// =====================================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace YamaEngine.Assets.Importers
{
    // =================================================================================
    //  导入选项
    // =================================================================================

    /// <summary>glTF 导入选项。</summary>
    public sealed class GltfImportOptions
    {
        /// <summary>全局缩放比例。</summary>
        public float Scale = 1.0f;

        /// <summary>是否将右手坐标系转换为左手坐标系（glTF 默认右手）。</summary>
        public bool ConvertToLeftHanded = true;

        /// <summary>缺少法线时是否自动计算。</summary>
        public bool GenerateNormals = true;

        /// <summary>缺少切线时是否自动生成。</summary>
        public bool GenerateTangents = true;

        /// <summary>是否合并同节点的多个网格 primitives。</summary>
        public bool MergePrimitives = false;

        /// <summary>是否优化网格（去重顶点）。</summary>
        public bool OptimizeMesh = true;

        /// <summary>是否导入动画。</summary>
        public bool ImportAnimations = true;

        /// <summary>是否导入蒙皮。</summary>
        public bool ImportSkins = true;

        /// <summary>是否导入 Morph Targets。</summary>
        public bool ImportMorphTargets = true;

        /// <summary>是否导入相机。</summary>
        public bool ImportCameras = true;

        /// <summary>是否导入灯光（KHR_lights_punctual）。</summary>
        public bool ImportLights = true;

        /// <summary>是否生成 LOD。</summary>
        public bool GenerateLOD = false;

        /// <summary>Draco 解压回调（若为 null 则跳过 Draco 压缩的网格）。</summary>
        public Func<byte[], int, int, byte[]>? DracoDecompressCallback;

        /// <summary>单纹理最大尺寸（0=不限制）。</summary>
        public int MaxTextureSize = 0;
    }

    // =================================================================================
    //  进度回调
    // =================================================================================

    /// <summary>导入进度报告委托。</summary>
    /// <param name="progress">进度 [0..1]。</param>
    /// <param name="message">当前阶段描述。</param>
    public delegate void GltfImportProgressCallback(float progress, string message);

    // =================================================================================
    //  glTF 2.0 数据模型（JSON 反序列化）
    // =================================================================================

    /// <summary>glTF 根文档。</summary>
    internal sealed class GltfDocument
    {
        public GltfAsset Asset = new();
        public List<GltfScene> Scenes = new();
        public List<GltfNode> Nodes = new();
        public List<GltfMesh> Meshes = new();
        public List<GltfMaterial> Materials = new();
        public List<GltfTexture> Textures = new();
        public List<GltfImage> Images = new();
        public List<GltfSampler> Samplers = new();
        public List<GltfBuffer> Buffers = new();
        public List<GltfBufferView> BufferViews = new();
        public List<GltfAccessor> Accessors = new();
        public List<GltfSkin> Skins = new();
        public List<GltfAnimation> Animations = new();
        public List<GltfCamera> Cameras = new();
        public int? Scene = 0;
        public List<string> ExtensionsUsed = new();
        public List<string> ExtensionsRequired = new();
        public JsonElement? Extensions = null;
        public JsonElement? Extras = null;
    }

    internal sealed class GltfAsset
    {
        public string Version = "2.0";
        public string Generator = string.Empty;
        public string Copyright = string.Empty;
        public string MinVersion = string.Empty;
    }

    internal sealed class GltfScene
    {
        public List<int> Nodes = new();
        public string Name = string.Empty;
    }

    internal sealed class GltfNode
    {
        public string Name = string.Empty;
        public int? Mesh;
        public int? Skin;
        public int? Camera;
        public List<int> Children = new();
        public float[]? Translation;
        public float[]? Rotation;
        public float[]? Scale;
        public float[]? Weights;
        public string Matrix;
        public int? Light; // KHR_lights_punctual
    }

    internal sealed class GltfMesh
    {
        public string Name = string.Empty;
        public List<GltfPrimitive> Primitives = new();
        public float[]? Weights;
    }

    internal sealed class GltfPrimitive
    {
        public Dictionary<string, int> Attributes = new();
        public int? Indices;
        public int? Material;
        public int Mode = 4; // TRIANGLES
        public List<GltfMorphTarget> Targets = new();
        public JsonElement? Extensions = null;
    }

    internal sealed class GltfMorphTarget
    {
        public Dictionary<string, int> Attributes = new();
    }

    internal sealed class GltfMaterial
    {
        public string Name = string.Empty;
        public GltfPbrMetallicRoughness? PbrMetallicRoughness;
        public GltfPbrSpecularGlossiness? PbrSpecularGlossiness; // KHR_materials_pbrSpecularGlossiness
        public float[]? EmissiveFactor;
        public int? NormalTexture;
        public int? OcclusionTexture;
        public int? EmissiveTexture;
        public string AlphaMode = "OPAQUE";
        public float AlphaCutoff = 0.5f;
        public bool DoubleSided = false;
        public bool Unlit = false; // KHR_materials_unlit
        public JsonElement? Extensions = null;
    }

    internal sealed class GltfPbrMetallicRoughness
    {
        public float[]? BaseColorFactor;
        public int? BaseColorTexture;
        public float? MetallicFactor;
        public float? RoughnessFactor;
        public int? MetallicRoughnessTexture;
    }

    internal sealed class GltfPbrSpecularGlossiness
    {
        public float[]? DiffuseFactor;
        public int? DiffuseTexture;
        public float[]? SpecularFactor;
        public float? GlossinessFactor;
        public int? SpecularGlossinessTexture;
    }

    internal sealed class GltfTextureInfo
    {
        public int Index;
        public int TexCoord;
        public float Scale = 1.0f; // KHR_texture_transform
        public float[]? Offset;
        public float[]? Rotation;
    }

    internal sealed class GltfTexture
    {
        public int? Source;
        public int? Sampler;
        public string Name = string.Empty;
        public int? BasisuSource; // KHR_texture_basisu
    }

    internal sealed class GltfImage
    {
        public string Uri = string.Empty;
        public int? BufferView;
        public string MimeType = string.Empty;
        public string Name = string.Empty;
    }

    internal sealed class GltfSampler
    {
        public int MagFilter = 9729; // LINEAR
        public int MinFilter = 9987; // LINEAR_MIPMAP_LINEAR
        public int WrapS = 10497;    // REPEAT
        public int WrapT = 10497;    // REPEAT
        public string Name = string.Empty;
    }

    internal sealed class GltfBuffer
    {
        public string Uri = string.Empty;
        public int ByteLength;
        public string Name = string.Empty;
        public byte[]? Data; // 加载后的原始数据
    }

    internal sealed class GltfBufferView
    {
        public int Buffer;
        public int ByteOffset;
        public int ByteLength;
        public int? ByteStride;
        public int? Target;
        public string Name = string.Empty;
    }

    internal sealed class GltfAccessor
    {
        public int? BufferView;
        public int ByteOffset;
        public int ComponentType;  // 5120=BYTE,5121=UBYTE,5122=SHORT,5123=USHORT,5125=UINT,5126=FLOAT
        public int Count;
        public string Type = "SCALAR"; // SCALAR/VEC2/VEC3/VEC4/MAT2/MAT3/MAT4
        public float[]? Max;
        public float[]? Min;
        public bool Normalized = false;
        public string Name = string.Empty;
    }

    internal sealed class GltfSkin
    {
        public int? InverseBindMatrices;
        public int? Skeleton;
        public List<int> Joints = new();
        public string Name = string.Empty;
    }

    internal sealed class GltfAnimation
    {
        public List<GltfAnimationChannel> Channels = new();
        public List<GltfAnimationSampler> Samplers = new();
        public string Name = string.Empty;
    }

    internal sealed class GltfAnimationChannel
    {
        public int Sampler;
        public GltfAnimationTarget Target = new();
    }

    internal sealed class GltfAnimationTarget
    {
        public int Node;
        public string Path = ""; // translation/rotation/scale/weights
    }

    internal sealed class GltfAnimationSampler
    {
        public int Input;
        public int Output;
        public string Interpolation = "LINEAR"; // LINEAR/STEP/CUBICSPLINE
    }

    internal sealed class GltfCamera
    {
        public string Name = string.Empty;
        public string Type = "perspective"; // perspective/orthographic
        public GltfCameraPerspective? Perspective;
        public GltfCameraOrthographic? Orthographic;
    }

    internal sealed class GltfCameraPerspective
    {
        public float AspectRatio = 0f;
        public float YFov;
        public float ZFar = 0f;
        public float ZNear;
    }

    internal sealed class GltfCameraOrthographic
    {
        public float XMag;
        public float YMag;
        public float ZFar;
        public float ZNear;
    }

    // =================================================================================
    //  glTF 常量
    // =================================================================================

    internal static class GltfConstants
    {
        public const int GL_BYTE = 5120;
        public const int GL_UNSIGNED_BYTE = 5121;
        public const int GL_SHORT = 5122;
        public const int GL_UNSIGNED_SHORT = 5123;
        public const int GL_UNSIGNED_INT = 5125;
        public const int GL_FLOAT = 5126;

        public const int GL_ARRAY_BUFFER = 34962;
        public const int GL_ELEMENT_ARRAY_BUFFER = 34963;

        public const int GL_POINTS = 0;
        public const int GL_LINES = 1;
        public const int GL_LINE_LOOP = 2;
        public const int GL_LINE_STRIP = 3;
        public const int GL_TRIANGLES = 4;
        public const int GL_TRIANGLE_STRIP = 5;
        public const int GL_TRIANGLE_FAN = 6;

        // 过滤器
        public const int GL_NEAREST = 9728;
        public const int GL_LINEAR = 9729;
        public const int GL_NEAREST_MIPMAP_NEAREST = 9984;
        public const int GL_LINEAR_MIPMAP_NEAREST = 9985;
        public const int GL_NEAREST_MIPMAP_LINEAR = 9986;
        public const int GL_LINEAR_MIPMAP_LINEAR = 9987;

        // 包装
        public const int GL_CLAMP_TO_EDGE = 33071;
        public const int GL_MIRRORED_REPEAT = 33648;
        public const int GL_REPEAT = 10497;

        /// <summary>获取组件类型的大小（字节）。</summary>
        public static int GetComponentTypeSize(int componentType)
        {
            return componentType switch
            {
                GL_BYTE => 1,
                GL_UNSIGNED_BYTE => 1,
                GL_SHORT => 2,
                GL_UNSIGNED_SHORT => 2,
                GL_UNSIGNED_INT => 4,
                GL_FLOAT => 4,
                _ => 0
            };
        }

        /// <summary>获取 accessor 类型对应的组件数。</summary>
        public static int GetComponentsCount(string type)
        {
            return type switch
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
    }

    // =================================================================================
    //  GLB 二进制格式
    // =================================================================================

    /// <summary>GLB 文件头。</summary>
    internal struct GlbHeader
    {
        public uint Magic;       // 0x46546C67 = "glTF"
        public uint Version;     // 2
        public uint Length;      // 文件总长度
    }

    /// <summary>GLB 数据块。</summary>
    internal struct GlbChunk
    {
        public uint ChunkLength;
        public uint ChunkType;   // 0x4E4F534A = JSON, 0x004E4942 = BIN
        public byte[] Data;
    }

    // =================================================================================
    //  glTFImporter —— 主导入器
    // =================================================================================

    /// <summary>
    /// glTF 2.0 导入器。支持 .gltf (JSON) 和 .glb (二进制) 格式。
    /// 输出为 InterchangeScene 统一格式。
    /// </summary>
    public sealed class GlTFImporter
    {
        private readonly string _filePath;
        private readonly List<string> _messages = new();
        private GltfDocument _doc = new();
        private GltfImportOptions _options = new();
        private GltfImportProgressCallback? _progressCallback;

        /// <summary>解析产出的统一场景格式。</summary>
        public InterchangeScene? Scene { get; private set; }

        /// <summary>构造 glTF 导入器。</summary>
        public GlTFImporter(string filePath)
        {
            _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        }

        /// <summary>使用选项执行导入。</summary>
        public InterchangeScene Import(GltfImportOptions? options = null,
            GltfImportProgressCallback? progressCallback = null)
        {
            if (!File.Exists(_filePath))
                throw new FileNotFoundException($"glTF 源文件不存在: {_filePath}");

            _options = options ?? new GltfImportOptions();
            _progressCallback = progressCallback;

            Scene = new InterchangeScene
            {
                Name = Path.GetFileNameWithoutExtension(_filePath),
                SourceFilePath = _filePath,
                SourceFormat = "glTF"
            };

            try
            {
                ReportProgress(0.05f, "检测文件格式...");

                string ext = Path.GetExtension(_filePath).ToLowerInvariant();
                string jsonText;

                if (ext == ".glb")
                {
                    jsonText = ParseGlb();
                }
                else
                {
                    jsonText = File.ReadAllText(_filePath, Encoding.UTF8);
                }

                ReportProgress(0.15f, "解析 JSON 文档...");
                ParseJson(jsonText);

                ReportProgress(0.25f, "加载缓冲区数据...");
                LoadBuffers();

                ReportProgress(0.35f, "转换材质...");
                ConvertMaterials();

                ReportProgress(0.45f, "转换网格...");
                ConvertMeshes();

                ReportProgress(0.60f, "构建场景层级...");
                BuildSceneHierarchy();

                if (_options.ImportSkins)
                {
                    ReportProgress(0.70f, "转换蒙皮...");
                    ConvertSkins();
                }

                if (_options.ImportAnimations)
                {
                    ReportProgress(0.80f, "转换动画...");
                    ConvertAnimations();
                }

                ReportProgress(0.90f, "后处理...");
                PostProcessScene();

                ReportProgress(1.0f, "导入完成");
            }
            catch (Exception ex)
            {
                Scene.ImportMessages.Add($"导入 glTF 失败: {ex.Message}");
                throw;
            }

            Scene.ImportMessages.AddRange(_messages);
            return Scene;
        }

        // =================================================================================
        //  GLB 二进制解析
        // =================================================================================

        /// <summary>解析 .glb 二进制文件，返回 JSON 文本并加载 BIN 数据。</summary>
        private string ParseGlb()
        {
            byte[] fileData = File.ReadAllBytes(_filePath);

            if (fileData.Length < 12)
                throw new InvalidDataException("GLB 文件过小");

            // 读取文件头（小端）
            uint magic = BitConverter.ToUInt32(fileData, 0);
            uint version = BitConverter.ToUInt32(fileData, 4);
            uint length = BitConverter.ToUInt32(fileData, 8);

            if (magic != 0x46546C67)
                throw new InvalidDataException($"无效的 GLB Magic: 0x{magic:X8}");

            if (version != 2)
                _messages.Add($"警告：GLB 版本 {version}，预期为 2");

            if (length != fileData.Length)
                _messages.Add($"警告：GLB 头部长度 {length} 与实际文件大小 {fileData.Length} 不一致");

            int offset = 12;
            string jsonText = string.Empty;
            byte[]? binData = null;

            // 解析数据块
            while (offset + 8 <= fileData.Length)
            {
                uint chunkLength = BitConverter.ToUInt32(fileData, offset);
                uint chunkType = BitConverter.ToUInt32(fileData, offset + 4);
                offset += 8;

                if (offset + chunkLength > fileData.Length)
                    throw new InvalidDataException($"GLB 数据块超出文件范围: offset={offset}, length={chunkLength}");

                byte[] chunkData = new byte[chunkLength];
                Array.Copy(fileData, offset, chunkData, 0, (int)chunkLength);
                offset += (int)chunkLength;

                if (chunkType == 0x4E4F534A) // "JSON"
                {
                    jsonText = Encoding.UTF8.GetString(chunkData).TrimEnd('\0', ' ', '\n', '\r', '\t');
                }
                else if (chunkType == 0x004E4942) // "BIN\0"
                {
                    binData = chunkData;
                }
                else
                {
                    _messages.Add($"未知 GLB 数据块类型: 0x{chunkType:X8}");
                }
            }

            // 如果有 BIN 数据，将其作为第一个 buffer
            if (binData != null)
            {
                _glbBinData = binData;
            }

            return jsonText;
        }

        private byte[]? _glbBinData;

        // =================================================================================
        //  JSON 解析
        // =================================================================================

        /// <summary>解析 glTF JSON 文档为 GltfDocument。</summary>
        private void ParseJson(string jsonText)
        {
            using var doc = JsonDocument.Parse(jsonText);
            var root = doc.RootElement;

            // Asset
            if (root.TryGetProperty("asset", out var assetEl))
            {
                _doc.Asset = new GltfAsset();
                if (assetEl.TryGetProperty("version", out var v))
                    _doc.Asset.Version = v.GetString() ?? "2.0";
                if (assetEl.TryGetProperty("generator", out var g))
                    _doc.Asset.Generator = g.GetString() ?? string.Empty;
                if (assetEl.TryGetProperty("copyright", out var c))
                    _doc.Asset.Copyright = c.GetString() ?? string.Empty;
                if (assetEl.TryGetProperty("minVersion", out var m))
                    _doc.Asset.MinVersion = m.GetString() ?? string.Empty;
            }

            if (_doc.Asset.Version != "2.0")
                _messages.Add($"glTF 版本 {_doc.Asset.Version}，导入器针对 2.0 设计，可能不完全兼容");

            // Scenes
            if (root.TryGetProperty("scenes", out var scenesEl))
            {
                foreach (var s in scenesEl.EnumerateArray())
                {
                    var scene = new GltfScene();
                    if (s.TryGetProperty("name", out var n))
                        scene.Name = n.GetString() ?? string.Empty;
                    if (s.TryGetProperty("nodes", out var nodesEl))
                        foreach (var ni in nodesEl.EnumerateArray())
                            scene.Nodes.Add(ni.GetInt32());
                    _doc.Scenes.Add(scene);
                }
            }

            if (root.TryGetProperty("scene", out var sceneIdx))
                _doc.Scene = sceneIdx.GetInt32();

            // Nodes
            if (root.TryGetProperty("nodes", out var nodesArr))
            {
                foreach (var n in nodesArr.EnumerateArray())
                    _doc.Nodes.Add(ParseNode(n));
            }

            // Meshes
            if (root.TryGetProperty("meshes", out var meshesArr))
            {
                foreach (var m in meshesArr.EnumerateArray())
                    _doc.Meshes.Add(ParseMesh(m));
            }

            // Materials
            if (root.TryGetProperty("materials", out var matsArr))
            {
                foreach (var m in matsArr.EnumerateArray())
                    _doc.Materials.Add(ParseMaterial(m));
            }

            // Textures
            if (root.TryGetProperty("textures", out var texArr))
            {
                foreach (var t in texArr.EnumerateArray())
                    _doc.Textures.Add(ParseTexture(t));
            }

            // Images
            if (root.TryGetProperty("images", out var imgArr))
            {
                foreach (var i in imgArr.EnumerateArray())
                    _doc.Images.Add(ParseImage(i));
            }

            // Samplers
            if (root.TryGetProperty("samplers", out var sampArr))
            {
                foreach (var s in sampArr.EnumerateArray())
                    _doc.Samplers.Add(ParseSampler(s));
            }

            // Buffers
            if (root.TryGetProperty("buffers", out var bufArr))
            {
                foreach (var b in bufArr.EnumerateArray())
                    _doc.Buffers.Add(ParseBuffer(b));
            }

            // BufferViews
            if (root.TryGetProperty("bufferViews", out var bvArr))
            {
                foreach (var bv in bvArr.EnumerateArray())
                    _doc.BufferViews.Add(ParseBufferView(bv));
            }

            // Accessors
            if (root.TryGetProperty("accessors", out var accArr))
            {
                foreach (var a in accArr.EnumerateArray())
                    _doc.Accessors.Add(ParseAccessor(a));
            }

            // Skins
            if (root.TryGetProperty("skins", out var skinArr))
            {
                foreach (var s in skinArr.EnumerateArray())
                    _doc.Skins.Add(ParseSkin(s));
            }

            // Animations
            if (root.TryGetProperty("animations", out var animArr))
            {
                foreach (var a in animArr.EnumerateArray())
                    _doc.Animations.Add(ParseAnimation(a));
            }

            // Cameras
            if (root.TryGetProperty("cameras", out var camArr))
            {
                foreach (var c in camArr.EnumerateArray())
                    _doc.Cameras.Add(ParseCamera(c));
            }

            // Extensions
            if (root.TryGetProperty("extensionsUsed", out var extUsed))
                foreach (var e in extUsed.EnumerateArray())
                    _doc.ExtensionsUsed.Add(e.GetString() ?? string.Empty);

            if (root.TryGetProperty("extensionsRequired", out var extReq))
                foreach (var e in extReq.EnumerateArray())
                    _doc.ExtensionsRequired.Add(e.GetString() ?? string.Empty);

            if (root.TryGetProperty("extensions", out var extEl))
                _doc.Extensions = extEl.Clone();

            _messages.Add($"glTF 解析完成: {_doc.Nodes.Count} 节点, {_doc.Meshes.Count} 网格, " +
                          $"{_doc.Materials.Count} 材质, {_doc.Textures.Count} 纹理, " +
                          $"{_doc.Accessors.Count} 访问器, {_doc.Animations.Count} 动画");
        }

        private GltfNode ParseNode(JsonElement el)
        {
            var node = new GltfNode();
            if (el.TryGetProperty("name", out var n))
                node.Name = n.GetString() ?? string.Empty;
            if (el.TryGetProperty("mesh", out var mesh))
                node.Mesh = mesh.GetInt32();
            if (el.TryGetProperty("skin", out var skin))
                node.Skin = skin.GetInt32();
            if (el.TryGetProperty("camera", out var cam))
                node.Camera = cam.GetInt32();
            if (el.TryGetProperty("children", out var children))
                foreach (var c in children.EnumerateArray())
                    node.Children.Add(c.GetInt32());
            if (el.TryGetProperty("translation", out var t))
                node.Translation = ParseFloatArray(t);
            if (el.TryGetProperty("rotation", out var r))
                node.Rotation = ParseFloatArray(r);
            if (el.TryGetProperty("scale", out var s))
                node.Scale = ParseFloatArray(s);
            if (el.TryGetProperty("weights", out var w))
                node.Weights = ParseFloatArray(w);
            if (el.TryGetProperty("matrix", out var m))
                node.Matrix = m.GetSingle().ToString(CultureInfo.InvariantCulture);

            // KHR_lights_punctual
            if (el.TryGetProperty("extensions", out var ext) &&
                ext.TryGetProperty("KHR_lights_punctual", out var light) &&
                light.TryGetProperty("light", out var lightIdx))
                node.Light = lightIdx.GetInt32();

            return node;
        }

        private GltfMesh ParseMesh(JsonElement el)
        {
            var mesh = new GltfMesh();
            if (el.TryGetProperty("name", out var n))
                mesh.Name = n.GetString() ?? string.Empty;

            if (el.TryGetProperty("primitives", out var prims))
            {
                foreach (var p in prims.EnumerateArray())
                {
                    var prim = new GltfPrimitive();
                    if (p.TryGetProperty("attributes", out var attrs))
                    {
                        foreach (var attr in attrs.EnumerateObject())
                            prim.Attributes[attr.Name] = attr.Value.GetInt32();
                    }
                    if (p.TryGetProperty("indices", out var idx))
                        prim.Indices = idx.GetInt32();
                    if (p.TryGetProperty("material", out var mat))
                        prim.Material = mat.GetInt32();
                    if (p.TryGetProperty("mode", out var mode))
                        prim.Mode = mode.GetInt32();
                    if (p.TryGetProperty("targets", out var targets))
                    {
                        foreach (var target in targets.EnumerateArray())
                        {
                            var mt = new GltfMorphTarget();
                            foreach (var attr in target.EnumerateObject())
                                mt.Attributes[attr.Name] = attr.Value.GetInt32();
                            prim.Targets.Add(mt);
                        }
                    }
                    if (p.TryGetProperty("extensions", out var ext))
                        prim.Extensions = ext.Clone();
                    mesh.Primitives.Add(prim);
                }
            }

            if (el.TryGetProperty("weights", out var w))
                mesh.Weights = ParseFloatArray(w);

            return mesh;
        }

        private GltfMaterial ParseMaterial(JsonElement el)
        {
            var mat = new GltfMaterial();
            if (el.TryGetProperty("name", out var n))
                mat.Name = n.GetString() ?? string.Empty;

            if (el.TryGetProperty("pbrMetallicRoughness", out var pbr))
            {
                var p = new GltfPbrMetallicRoughness();
                if (pbr.TryGetProperty("baseColorFactor", out var bcf))
                    p.BaseColorFactor = ParseFloatArray(bcf);
                if (pbr.TryGetProperty("baseColorTexture", out var bct))
                    p.BaseColorTexture = ParseTextureInfoIndex(bct);
                if (pbr.TryGetProperty("metallicFactor", out var mf))
                    p.MetallicFactor = mf.GetSingle();
                if (pbr.TryGetProperty("roughnessFactor", out var rf))
                    p.RoughnessFactor = rf.GetSingle();
                if (pbr.TryGetProperty("metallicRoughnessTexture", out var mrt))
                    p.MetallicRoughnessTexture = ParseTextureInfoIndex(mrt);
                mat.PbrMetallicRoughness = p;
            }

            if (el.TryGetProperty("emissiveFactor", out var ef))
                mat.EmissiveFactor = ParseFloatArray(ef);
            if (el.TryGetProperty("normalTexture", out var nt))
                mat.NormalTexture = ParseTextureInfoIndex(nt);
            if (el.TryGetProperty("occlusionTexture", out var ot))
                mat.OcclusionTexture = ParseTextureInfoIndex(ot);
            if (el.TryGetProperty("emissiveTexture", out var et))
                mat.EmissiveTexture = ParseTextureInfoIndex(et);
            if (el.TryGetProperty("alphaMode", out var am))
                mat.AlphaMode = am.GetString() ?? "OPAQUE";
            if (el.TryGetProperty("alphaCutoff", out var ac))
                mat.AlphaCutoff = ac.GetSingle();
            if (el.TryGetProperty("doubleSided", out var ds))
                mat.DoubleSided = ds.GetBoolean();

            // 扩展
            if (el.TryGetProperty("extensions", out var ext))
            {
                mat.Extensions = ext.Clone();

                if (ext.TryGetProperty("KHR_materials_unlit", out _))
                    mat.Unlit = true;

                if (ext.TryGetProperty("KHR_materials_pbrSpecularGlossiness", out var sg))
                {
                    var specGloss = new GltfPbrSpecularGlossiness();
                    if (sg.TryGetProperty("diffuseFactor", out var df))
                        specGloss.DiffuseFactor = ParseFloatArray(df);
                    if (sg.TryGetProperty("diffuseTexture", out var dt))
                        specGloss.DiffuseTexture = ParseTextureInfoIndex(dt);
                    if (sg.TryGetProperty("specularFactor", out var sf))
                        specGloss.SpecularFactor = ParseFloatArray(sf);
                    if (sg.TryGetProperty("glossinessFactor", out var gf))
                        specGloss.GlossinessFactor = gf.GetSingle();
                    if (sg.TryGetProperty("specularGlossinessTexture", out var sgt))
                        specGloss.SpecularGlossinessTexture = ParseTextureInfoIndex(sgt);
                    mat.PbrSpecularGlossiness = specGloss;
                }
            }

            return mat;
        }

        private int ParseTextureInfoIndex(JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("index", out var idx))
                return idx.GetInt32();
            return -1;
        }

        private GltfTexture ParseTexture(JsonElement el)
        {
            var tex = new GltfTexture();
            if (el.TryGetProperty("name", out var n))
                tex.Name = n.GetString() ?? string.Empty;
            if (el.TryGetProperty("source", out var s))
                tex.Source = s.GetInt32();
            if (el.TryGetProperty("sampler", out var samp))
                tex.Sampler = samp.GetInt32();

            // KHR_texture_basisu
            if (el.TryGetProperty("extensions", out var ext) &&
                ext.TryGetProperty("KHR_texture_basisu", out var bu) &&
                bu.TryGetProperty("source", out var buSource))
                tex.BasisuSource = buSource.GetInt32();

            return tex;
        }

        private GltfImage ParseImage(JsonElement el)
        {
            var img = new GltfImage();
            if (el.TryGetProperty("name", out var n))
                img.Name = n.GetString() ?? string.Empty;
            if (el.TryGetProperty("uri", out var uri))
                img.Uri = uri.GetString() ?? string.Empty;
            if (el.TryGetProperty("bufferView", out var bv))
                img.BufferView = bv.GetInt32();
            if (el.TryGetProperty("mimeType", out var mt))
                img.MimeType = mt.GetString() ?? string.Empty;
            return img;
        }

        private GltfSampler ParseSampler(JsonElement el)
        {
            var samp = new GltfSampler();
            if (el.TryGetProperty("name", out var n))
                samp.Name = n.GetString() ?? string.Empty;
            if (el.TryGetProperty("magFilter", out var mf))
                samp.MagFilter = mf.GetInt32();
            if (el.TryGetProperty("minFilter", out var minf))
                samp.MinFilter = minf.GetInt32();
            if (el.TryGetProperty("wrapS", out var ws))
                samp.WrapS = ws.GetInt32();
            if (el.TryGetProperty("wrapT", out var wt))
                samp.WrapT = wt.GetInt32();
            return samp;
        }

        private GltfBuffer ParseBuffer(JsonElement el)
        {
            var buf = new GltfBuffer();
            if (el.TryGetProperty("name", out var n))
                buf.Name = n.GetString() ?? string.Empty;
            if (el.TryGetProperty("uri", out var uri))
                buf.Uri = uri.GetString() ?? string.Empty;
            if (el.TryGetProperty("byteLength", out var bl))
                buf.ByteLength = bl.GetInt32();
            return buf;
        }

        private GltfBufferView ParseBufferView(JsonElement el)
        {
            var bv = new GltfBufferView();
            if (el.TryGetProperty("name", out var n))
                bv.Name = n.GetString() ?? string.Empty;
            if (el.TryGetProperty("buffer", out var b))
                bv.Buffer = b.GetInt32();
            if (el.TryGetProperty("byteOffset", out var bo))
                bv.ByteOffset = bo.GetInt32();
            if (el.TryGetProperty("byteLength", out var bl))
                bv.ByteLength = bl.GetInt32();
            if (el.TryGetProperty("byteStride", out var bs))
                bv.ByteStride = bs.GetInt32();
            if (el.TryGetProperty("target", out var t))
                bv.Target = t.GetInt32();
            return bv;
        }

        private GltfAccessor ParseAccessor(JsonElement el)
        {
            var acc = new GltfAccessor();
            if (el.TryGetProperty("name", out var n))
                acc.Name = n.GetString() ?? string.Empty;
            if (el.TryGetProperty("bufferView", out var bv))
                acc.BufferView = bv.GetInt32();
            if (el.TryGetProperty("byteOffset", out var bo))
                acc.ByteOffset = bo.GetInt32();
            if (el.TryGetProperty("componentType", out var ct))
                acc.ComponentType = ct.GetInt32();
            if (el.TryGetProperty("count", out var c))
                acc.Count = c.GetInt32();
            if (el.TryGetProperty("type", out var t))
                acc.Type = t.GetString() ?? "SCALAR";
            if (el.TryGetProperty("normalized", out var norm))
                acc.Normalized = norm.GetBoolean();
            if (el.TryGetProperty("max", out var max))
                acc.Max = ParseFloatArray(max);
            if (el.TryGetProperty("min", out var min))
                acc.Min = ParseFloatArray(min);
            return acc;
        }

        private GltfSkin ParseSkin(JsonElement el)
        {
            var skin = new GltfSkin();
            if (el.TryGetProperty("name", out var n))
                skin.Name = n.GetString() ?? string.Empty;
            if (el.TryGetProperty("inverseBindMatrices", out var ibm))
                skin.InverseBindMatrices = ibm.GetInt32();
            if (el.TryGetProperty("skeleton", out var sk))
                skin.Skeleton = sk.GetInt32();
            if (el.TryGetProperty("joints", out var joints))
                foreach (var j in joints.EnumerateArray())
                    skin.Joints.Add(j.GetInt32());
            return skin;
        }

        private GltfAnimation ParseAnimation(JsonElement el)
        {
            var anim = new GltfAnimation();
            if (el.TryGetProperty("name", out var n))
                anim.Name = n.GetString() ?? string.Empty;

            if (el.TryGetProperty("samplers", out var samplers))
            {
                foreach (var s in samplers.EnumerateArray())
                {
                    var samp = new GltfAnimationSampler();
                    if (s.TryGetProperty("input", out var inp))
                        samp.Input = inp.GetInt32();
                    if (s.TryGetProperty("output", out var outp))
                        samp.Output = outp.GetInt32();
                    if (s.TryGetProperty("interpolation", out var interp))
                        samp.Interpolation = interp.GetString() ?? "LINEAR";
                    anim.Samplers.Add(samp);
                }
            }

            if (el.TryGetProperty("channels", out var channels))
            {
                foreach (var c in channels.EnumerateArray())
                {
                    var ch = new GltfAnimationChannel();
                    if (c.TryGetProperty("sampler", out var samp))
                        ch.Sampler = samp.GetInt32();
                    if (c.TryGetProperty("target", out var target))
                    {
                        if (target.TryGetProperty("node", out var node))
                            ch.Target.Node = node.GetInt32();
                        if (target.TryGetProperty("path", out var path))
                            ch.Target.Path = path.GetString() ?? "";
                    }
                    anim.Channels.Add(ch);
                }
            }

            return anim;
        }

        private GltfCamera ParseCamera(JsonElement el)
        {
            var cam = new GltfCamera();
            if (el.TryGetProperty("name", out var n))
                cam.Name = n.GetString() ?? string.Empty;
            if (el.TryGetProperty("type", out var t))
                cam.Type = t.GetString() ?? "perspective";

            if (el.TryGetProperty("perspective", out var persp))
            {
                var p = new GltfCameraPerspective();
                if (persp.TryGetProperty("aspectRatio", out var ar))
                    p.AspectRatio = ar.GetSingle();
                if (persp.TryGetProperty("yfov", out var fov))
                    p.YFov = fov.GetSingle();
                if (persp.TryGetProperty("zfar", out var zf))
                    p.ZFar = zf.GetSingle();
                if (persp.TryGetProperty("znear", out var zn))
                    p.ZNear = zn.GetSingle();
                cam.Perspective = p;
            }

            if (el.TryGetProperty("orthographic", out var ortho))
            {
                var o = new GltfCameraOrthographic();
                if (ortho.TryGetProperty("xmag", out var xm))
                    o.XMag = xm.GetSingle();
                if (ortho.TryGetProperty("ymag", out var ym))
                    o.YMag = ym.GetSingle();
                if (ortho.TryGetProperty("zfar", out var zf))
                    o.ZFar = zf.GetSingle();
                if (ortho.TryGetProperty("znear", out var zn))
                    o.ZNear = zn.GetSingle();
                cam.Orthographic = o;
            }

            return cam;
        }

        private static float[] ParseFloatArray(JsonElement el)
        {
            var list = new List<float>();
            if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var v in el.EnumerateArray())
                    list.Add(v.GetSingle());
            }
            return list.ToArray();
        }

        // =================================================================================
        //  缓冲区数据加载
        // =================================================================================

        /// <summary>加载所有 buffer 的数据（从 URI 或 GLB BIN）。</summary>
        private void LoadBuffers()
        {
            string baseDir = Path.GetDirectoryName(_filePath) ?? string.Empty;

            for (int i = 0; i < _doc.Buffers.Count; i++)
            {
                var buf = _doc.Buffers[i];

                // GLB 嵌入的 BIN 数据（URI 为空时使用 GLB BIN）
                if (i == 0 && string.IsNullOrEmpty(buf.Uri) && _glbBinData != null)
                {
                    buf.Data = _glbBinData;
                    _messages.Add($"Buffer {i}: 使用 GLB 内嵌数据 ({buf.Data.Length} 字节)");
                    _doc.Buffers[i] = buf;
                    continue;
                }

                if (string.IsNullOrEmpty(buf.Uri))
                {
                    _messages.Add($"Buffer {i}: 无 URI 且无 GLB 数据，跳过");
                    continue;
                }

                // Data URI (base64)
                if (buf.Uri.StartsWith("data:", StringComparison.Ordinal))
                {
                    int commaIdx = buf.Uri.IndexOf(',');
                    if (commaIdx > 0)
                    {
                        string base64 = buf.Uri.Substring(commaIdx + 1);
                        buf.Data = Convert.FromBase64String(base64);
                        _messages.Add($"Buffer {i}: 从 Data URI 加载 ({buf.Data.Length} 字节)");
                    }
                }
                else
                {
                    // 外部文件
                    string bufPath = Path.Combine(baseDir, buf.Uri);
                    if (File.Exists(bufPath))
                    {
                        buf.Data = File.ReadAllBytes(bufPath);
                        _messages.Add($"Buffer {i}: 从 {buf.Uri} 加载 ({buf.Data.Length} 字节)");
                    }
                    else
                    {
                        _messages.Add($"Buffer {i}: 文件不存在 {bufPath}");
                    }
                }

                _doc.Buffers[i] = buf;
            }
        }

        // =================================================================================
        //  Accessor 数据读取
        // =================================================================================

        /// <summary>从 accessor 读取 float 数组。</summary>
        private float[] ReadAccessorFloat(int accessorIndex)
        {
            if (accessorIndex < 0 || accessorIndex >= _doc.Accessors.Count)
                return Array.Empty<float>();

            var acc = _doc.Accessors[accessorIndex];
            if (!acc.BufferView.HasValue)
                return new float[acc.Count * GltfConstants.GetComponentsCount(acc.Type)];

            var bv = _doc.BufferViews[acc.BufferView.Value];
            var buffer = _doc.Buffers[bv.Buffer];
            if (buffer.Data == null)
                return Array.Empty<float>();

            int components = GltfConstants.GetComponentsCount(acc.Type);
            int componentSize = GltfConstants.GetComponentTypeSize(acc.ComponentType);
            int stride = bv.ByteStride > 0 ? bv.ByteStride.Value : components * componentSize;

            var result = new float[acc.Count * components];
            int baseOffset = bv.ByteOffset + acc.ByteOffset;

            for (int i = 0; i < acc.Count; i++)
            {
                int elementOffset = baseOffset + i * stride;

                for (int c = 0; c < components; c++)
                {
                    int offset = elementOffset + c * componentSize;

                    if (acc.ComponentType == GltfConstants.GL_FLOAT)
                    {
                        result[i * components + c] = BitConverter.ToSingle(buffer.Data, offset);
                    }
                    else if (acc.ComponentType == GltfConstants.GL_UNSIGNED_BYTE)
                    {
                        byte val = buffer.Data[offset];
                        result[i * components + c] = acc.Normalized ? val / 255f : val;
                    }
                    else if (acc.ComponentType == GltfConstants.GL_BYTE)
                    {
                        sbyte val = (sbyte)buffer.Data[offset];
                        result[i * components + c] = acc.Normalized ? Math.Max(val / 127f, -1f) : val;
                    }
                    else if (acc.ComponentType == GltfConstants.GL_UNSIGNED_SHORT)
                    {
                        ushort val = BitConverter.ToUInt16(buffer.Data, offset);
                        result[i * components + c] = acc.Normalized ? val / 65535f : val;
                    }
                    else if (acc.ComponentType == GltfConstants.GL_SHORT)
                    {
                        short val = BitConverter.ToInt16(buffer.Data, offset);
                        result[i * components + c] = acc.Normalized ? Math.Max(val / 32767f, -1f) : val;
                    }
                    else if (acc.ComponentType == GltfConstants.GL_UNSIGNED_INT)
                    {
                        uint val = BitConverter.ToUInt32(buffer.Data, offset);
                        result[i * components + c] = val;
                    }
                }
            }

            return result;
        }

        /// <summary>从 accessor 读取 int 数组（索引数据）。</summary>
        private int[] ReadAccessorInt(int accessorIndex)
        {
            if (accessorIndex < 0 || accessorIndex >= _doc.Accessors.Count)
                return Array.Empty<int>();

            var acc = _doc.Accessors[accessorIndex];
            if (!acc.BufferView.HasValue)
                return new int[acc.Count];

            var bv = _doc.BufferViews[acc.BufferView.Value];
            var buffer = _doc.Buffers[bv.Buffer];
            if (buffer.Data == null)
                return Array.Empty<int>();

            int componentSize = GltfConstants.GetComponentTypeSize(acc.ComponentType);
            int stride = bv.ByteStride > 0 ? bv.ByteStride.Value : componentSize;

            var result = new int[acc.Count];
            int baseOffset = bv.ByteOffset + acc.ByteOffset;

            for (int i = 0; i < acc.Count; i++)
            {
                int offset = baseOffset + i * stride;

                switch (acc.ComponentType)
                {
                    case GltfConstants.GL_UNSIGNED_BYTE:
                        result[i] = buffer.Data[offset];
                        break;
                    case GltfConstants.GL_BYTE:
                        result[i] = (sbyte)buffer.Data[offset];
                        break;
                    case GltfConstants.GL_UNSIGNED_SHORT:
                        result[i] = BitConverter.ToUInt16(buffer.Data, offset);
                        break;
                    case GltfConstants.GL_SHORT:
                        result[i] = BitConverter.ToInt16(buffer.Data, offset);
                        break;
                    case GltfConstants.GL_UNSIGNED_INT:
                        result[i] = (int)BitConverter.ToUInt32(buffer.Data, offset);
                        break;
                    case GltfConstants.GL_FLOAT:
                        result[i] = (int)BitConverter.ToSingle(buffer.Data, offset);
                        break;
                }
            }

            return result;
        }

        /// <summary>读取 BufferView 的原始字节数据（用于嵌入图像）。</summary>
        private byte[] ReadBufferViewData(int bufferViewIndex)
        {
            if (bufferViewIndex < 0 || bufferViewIndex >= _doc.BufferViews.Count)
                return Array.Empty<byte>();

            var bv = _doc.BufferViews[bufferViewIndex];
            var buffer = _doc.Buffers[bv.Buffer];
            if (buffer.Data == null)
                return Array.Empty<byte>();

            int offset = bv.ByteOffset;
            int length = Math.Min(bv.ByteLength, buffer.Data.Length - offset);

            var result = new byte[length];
            Array.Copy(buffer.Data, offset, result, 0, length);
            return result;
        }

        // =================================================================================
        //  材质转换
        // =================================================================================

        private void ConvertMaterials()
        {
            for (int i = 0; i < _doc.Materials.Count; i++)
            {
                var gltfMat = _doc.Materials[i];
                var mat = new InterchangeMaterial
                {
                    Name = string.IsNullOrEmpty(gltfMat.Name) ? $"Material_{i}" : gltfMat.Name,
                    DoubleSided = gltfMat.DoubleSided,
                    ShadingModel = gltfMat.Unlit ? "Unlit" : "PBR"
                };

                // Alpha 模式
                if (gltfMat.AlphaMode == "BLEND")
                    mat.Opacity = 0.5f; // 标记为半透明
                else if (gltfMat.AlphaMode == "MASK")
                {
                    // Alpha 蒙版：无对应属性，此处为标记位（原为无效自赋值）
                }

                // PBR 金属粗糙度
                if (gltfMat.PbrMetallicRoughness != null)
                {
                    var pbr = gltfMat.PbrMetallicRoughness;

                    if (pbr.BaseColorFactor != null && pbr.BaseColorFactor.Length >= 3)
                    {
                        mat.DiffuseColor = new Vector3(pbr.BaseColorFactor[0], pbr.BaseColorFactor[1], pbr.BaseColorFactor[2]);
                        if (pbr.BaseColorFactor.Length >= 4)
                            mat.Opacity = pbr.BaseColorFactor[3];
                    }

                    mat.Metallic = pbr.MetallicFactor ?? 1f;
                    mat.Roughness = pbr.RoughnessFactor ?? 1f;

                    // 纹理引用
                    if (pbr.BaseColorTexture.HasValue)
                        mat.DiffuseTexturePath = ResolveTexturePath(pbr.BaseColorTexture.Value);
                    if (pbr.MetallicRoughnessTexture.HasValue)
                        mat.RoughnessTexturePath = ResolveTexturePath(pbr.MetallicRoughnessTexture.Value);
                }

                // KHR_materials_pbrSpecularGlossiness
                if (gltfMat.PbrSpecularGlossiness != null)
                {
                    var sg = gltfMat.PbrSpecularGlossiness;
                    if (sg.DiffuseFactor != null && sg.DiffuseFactor.Length >= 3)
                    {
                        mat.DiffuseColor = new Vector3(sg.DiffuseFactor[0], sg.DiffuseFactor[1], sg.DiffuseFactor[2]);
                        if (sg.DiffuseFactor.Length >= 4)
                            mat.Opacity = sg.DiffuseFactor[3];
                    }
                    // Glossiness → Roughness 转换
                    mat.Roughness = 1f - (sg.GlossinessFactor ?? 1f);
                    mat.Metallic = 0f; // Specular-Glossiness 默认非金属
                    mat.ShadingModel = "SpecularGlossiness";

                    if (sg.DiffuseTexture.HasValue)
                        mat.DiffuseTexturePath = ResolveTexturePath(sg.DiffuseTexture.Value);
                }

                // 自发光
                if (gltfMat.EmissiveFactor != null && gltfMat.EmissiveFactor.Length >= 3)
                {
                    mat.EmissiveColor = new Vector3(gltfMat.EmissiveFactor[0], gltfMat.EmissiveFactor[1], gltfMat.EmissiveFactor[2]);
                }
                if (gltfMat.EmissiveTexture.HasValue)
                    mat.EmissiveTexturePath = ResolveTexturePath(gltfMat.EmissiveTexture.Value);

                // 法线贴图
                if (gltfMat.NormalTexture.HasValue)
                    mat.NormalTexturePath = ResolveTexturePath(gltfMat.NormalTexture.Value);

                // AO 贴图
                if (gltfMat.OcclusionTexture.HasValue)
                    mat.OcclusionTexturePath = ResolveTexturePath(gltfMat.OcclusionTexture.Value);

                // 扩展属性提取
                if (gltfMat.Extensions.HasValue)
                {
                    var ext = gltfMat.Extensions.Value;

                    // KHR_materials_emissive_strength
                    if (ext.TryGetProperty("KHR_materials_emissive_strength", out var es) &&
                        es.TryGetProperty("emissiveStrength", out var strength))
                        mat.EmissiveIntensity = strength.GetSingle();

                    // KHR_materials_clearcoat
                    if (ext.TryGetProperty("KHR_materials_clearcoat", out var cc))
                    {
                        if (cc.TryGetProperty("clearcoatFactor", out var cf))
                            mat.ExtraTextureSlots["clearcoatFactor"] = cf.GetSingle().ToString(CultureInfo.InvariantCulture);
                        if (cc.TryGetProperty("clearcoatTexture", out var ctex) && ctex.TryGetProperty("index", out var cidx))
                            mat.ExtraTextureSlots["clearcoatTexture"] = ResolveTexturePath(cidx.GetInt32());
                    }

                    // KHR_materials_transmission
                    if (ext.TryGetProperty("KHR_materials_transmission", out var tr))
                    {
                        if (tr.TryGetProperty("transmissionFactor", out var tf))
                            mat.ExtraTextureSlots["transmissionFactor"] = tf.GetSingle().ToString(CultureInfo.InvariantCulture);
                    }

                    // KHR_materials_volume
                    if (ext.TryGetProperty("KHR_materials_volume", out var vol))
                    {
                        if (vol.TryGetProperty("thicknessFactor", out var thf))
                            mat.ExtraTextureSlots["thicknessFactor"] = thf.GetSingle().ToString(CultureInfo.InvariantCulture);
                        if (vol.TryGetProperty("ior", out var iorVal))
                            mat.ExtraTextureSlots["ior"] = iorVal.GetSingle().ToString(CultureInfo.InvariantCulture);
                    }

                    // KHR_materials_ior
                    if (ext.TryGetProperty("KHR_materials_ior", out var iorEl) &&
                        iorEl.TryGetProperty("ior", out var iorVal2))
                        mat.ExtraTextureSlots["ior"] = iorVal2.GetSingle().ToString(CultureInfo.InvariantCulture);

                    // KHR_materials_sheen
                    if (ext.TryGetProperty("KHR_materials_sheen", out var sheen))
                    {
                        if (sheen.TryGetProperty("sheenColorFactor", out var scf))
                            mat.ExtraTextureSlots["sheenColorFactor"] = string.Join(",", ParseFloatArray(scf));
                    }

                    // KHR_materials_specular
                    if (ext.TryGetProperty("KHR_materials_specular", out var spec))
                    {
                        if (spec.TryGetProperty("specularFactor", out var sf))
                            mat.ExtraTextureSlots["specularFactor"] = sf.GetSingle().ToString(CultureInfo.InvariantCulture);
                    }
                }

                Scene.Materials.Add(mat);
            }
        }

        /// <summary>解析纹理索引为图像路径。</summary>
        private string ResolveTexturePath(int textureIndex)
        {
            if (textureIndex < 0 || textureIndex >= _doc.Textures.Count)
                return string.Empty;

            var tex = _doc.Textures[textureIndex];
            int sourceIndex = tex.BasisuSource ?? tex.Source ?? -1;

            if (sourceIndex < 0 || sourceIndex >= _doc.Images.Count)
                return string.Empty;

            var img = _doc.Images[sourceIndex];
            string baseDir = Path.GetDirectoryName(_filePath) ?? string.Empty;

            if (!string.IsNullOrEmpty(img.Uri))
            {
                // Data URI 或文件路径
                if (img.Uri.StartsWith("data:"))
                    return img.Uri;
                return Path.Combine(baseDir, img.Uri);
            }

            if (img.BufferView.HasValue)
            {
                // 嵌入 BufferView 的图像
                return $"gltf://bufferView/{img.BufferView.Value}";
            }

            return string.Empty;
        }

        // =================================================================================
        //  网格转换
        // =================================================================================

        private void ConvertMeshes()
        {
            for (int i = 0; i < _doc.Meshes.Count; i++)
            {
                var gltfMesh = _doc.Meshes[i];
                var mesh = ConvertGltfMesh(gltfMesh, i);
                Scene.Meshes.Add(mesh);
            }
        }

        private InterchangeMesh ConvertGltfMesh(GltfMesh gltfMesh, int meshIndex)
        {
            var mesh = new InterchangeMesh
            {
                Name = string.IsNullOrEmpty(gltfMesh.Name) ? $"Mesh_{meshIndex}" : gltfMesh.Name
            };

            if (_options.MergePrimitives && gltfMesh.Primitives.Count > 1)
            {
                // 合并所有 primitives 到一个网格
                foreach (var prim in gltfMesh.Primitives)
                {
                    MergePrimitive(mesh, prim, gltfMesh);
                }
            }
            else
            {
                // 每个 primitive 作为独立子网格（这里简化为只取第一个 primitive 的数据，
                // 实际多 primitive 应拆分为多个 InterchangeMesh 或使用子网格索引）
                if (gltfMesh.Primitives.Count > 0)
                {
                    var prim = gltfMesh.Primitives[0];
                    ConvertPrimitive(mesh, prim, gltfMesh);
                    mesh.Name = gltfMesh.Name;
                }

                // 多 primitive 时记录材质索引
                foreach (var prim in gltfMesh.Primitives)
                {
                    if (prim.Material.HasValue)
                        mesh.MaterialIndices.Add(prim.Material.Value);
                }
            }

            return mesh;
        }

        private void ConvertPrimitive(InterchangeMesh mesh, GltfPrimitive prim, GltfMesh gltfMesh)
        {
            // 检查 Draco 压缩
            if (prim.Extensions.HasValue &&
                prim.Extensions.Value.TryGetProperty("KHR_draco_mesh_compression", out _))
            {
                if (_options.DracoDecompressCallback != null)
                {
                    _messages.Add($"网格 {gltfMesh.Name}: Draco 压缩，尝试解压...");
                    // Draco 解压需要完整实现，此处依赖回调
                }
                else
                {
                    _messages.Add($"网格 {gltfMesh.Name}: 跳过 Draco 压缩的 primitive（未提供解压回调）");
                    return;
                }
            }

            // 读取顶点位置
            if (!prim.Attributes.TryGetValue("POSITION", out int posAccessorIdx))
            {
                _messages.Add($"网格 {gltfMesh.Name}: 缺少 POSITION 属性，跳过");
                return;
            }

            float[] positions = ReadAccessorFloat(posAccessorIdx);
            int vertexCount = positions.Length / 3;

            int vertexOffset = mesh.Vertices.Count;
            for (int i = 0; i < vertexCount; i++)
            {
                var pos = new Vector3(
                    positions[i * 3],
                    positions[i * 3 + 1],
                    positions[i * 3 + 2]
                );

                // 坐标系转换：glTF 右手 → 引擎左手（翻转 Z）
                if (_options.ConvertToLeftHanded)
                    pos.Z = -pos.Z;

                pos *= _options.Scale;
                mesh.Vertices.Add(pos);
            }

            // 法线
            if (prim.Attributes.TryGetValue("NORMAL", out int normalIdx))
            {
                float[] normals = ReadAccessorFloat(normalIdx);
                for (int i = 0; i < normals.Length / 3; i++)
                {
                    var n = new Vector3(normals[i * 3], normals[i * 3 + 1], normals[i * 3 + 2]);
                    if (_options.ConvertToLeftHanded)
                        n.Z = -n.Z;
                    mesh.Normals.Add(n);
                }
            }
            else if (_options.GenerateNormals)
            {
                // 稍后在后处理中计算
            }

            // 切线
            if (prim.Attributes.TryGetValue("TANGENT", out int tangentIdx))
            {
                float[] tangents = ReadAccessorFloat(tangentIdx);
                for (int i = 0; i < tangents.Length / 4; i++)
                {
                    var t = new Vector4(
                        tangents[i * 4],
                        tangents[i * 4 + 1],
                        tangents[i * 4 + 2],
                        tangents[i * 4 + 3]
                    );
                    if (_options.ConvertToLeftHanded)
                        t.Z = -t.Z;
                    mesh.Tangents.Add(t);
                }
            }

            // UV 通道 0
            if (prim.Attributes.TryGetValue("TEXCOORD_0", out int uv0Idx))
            {
                float[] uvs = ReadAccessorFloat(uv0Idx);
                for (int i = 0; i < uvs.Length / 2; i++)
                {
                    mesh.UV0.Add(new Vector2(uvs[i * 2], uvs[i * 2 + 1]));
                }
            }

            // 顶点颜色
            if (prim.Attributes.TryGetValue("COLOR_0", out int colorIdx))
            {
                float[] colors = ReadAccessorFloat(colorIdx);
                int comp = 3;
                // 判断是 VEC3 还是 VEC4
                if (_doc.Accessors[colorIdx].Type == "VEC4")
                    comp = 4;

                for (int i = 0; i < colors.Length / comp; i++)
                {
                    if (comp == 4)
                        mesh.Colors.Add(new Vector4(colors[i * 4], colors[i * 4 + 1], colors[i * 4 + 2], colors[i * 4 + 3]));
                    else
                        mesh.Colors.Add(new Vector4(colors[i * 3], colors[i * 3 + 1], colors[i * 3 + 2], 1f));
                }
            }

            // 索引
            int indexOffset = mesh.Indices.Count;
            if (prim.Indices.HasValue)
            {
                int[] indices = ReadAccessorInt(prim.Indices.Value);
                for (int i = 0; i < indices.Length; i++)
                    mesh.Indices.Add(indices[i] + vertexOffset);
            }
            else
            {
                // 无索引：生成顺序索引
                for (int i = 0; i < vertexCount; i++)
                    mesh.Indices.Add(i + vertexOffset);
            }

            // 处理不同绘制模式
            if (prim.Mode != GltfConstants.GL_TRIANGLES)
            {
                ConvertPrimitiveMode(mesh, indexOffset, prim.Mode, vertexCount, vertexOffset);
            }
        }

        /// <summary>将非三角形模式转换为三角形。</summary>
        private void ConvertPrimitiveMode(InterchangeMesh mesh, int indexOffset, int mode, int vertexCount, int vertexOffset)
        {
            var oldIndices = new List<int>();
            for (int i = indexOffset; i < mesh.Indices.Count; i++)
                oldIndices.Add(mesh.Indices[i]);

            // 移除旧索引
            mesh.Indices.RemoveRange(indexOffset, mesh.Indices.Count - indexOffset);

            switch (mode)
            {
                case GltfConstants.GL_TRIANGLE_STRIP:
                    // (i, i+1, i+2), (i+1, i+2, i+3), ...
                    for (int i = 0; i + 2 < oldIndices.Count; i++)
                    {
                        if (i % 2 == 0)
                        {
                            mesh.Indices.Add(oldIndices[i]);
                            mesh.Indices.Add(oldIndices[i + 1]);
                            mesh.Indices.Add(oldIndices[i + 2]);
                        }
                        else
                        {
                            // 翻转绕序
                            mesh.Indices.Add(oldIndices[i]);
                            mesh.Indices.Add(oldIndices[i + 2]);
                            mesh.Indices.Add(oldIndices[i + 1]);
                        }
                    }
                    break;

                case GltfConstants.GL_TRIANGLE_FAN:
                    // (0, 1, 2), (0, 2, 3), (0, 3, 4), ...
                    for (int i = 1; i + 1 < oldIndices.Count; i++)
                    {
                        mesh.Indices.Add(oldIndices[0]);
                        mesh.Indices.Add(oldIndices[i]);
                        mesh.Indices.Add(oldIndices[i + 1]);
                    }
                    break;

                case GltfConstants.GL_POINTS:
                case GltfConstants.GL_LINES:
                case GltfConstants.GL_LINE_LOOP:
                case GltfConstants.GL_LINE_STRIP:
                    // 非三角形模式，不生成三角形
                    _messages.Add($"非三角形绘制模式 {mode}，跳过三角化");
                    break;
            }
        }

        private void MergePrimitive(InterchangeMesh mesh, GltfPrimitive prim, GltfMesh gltfMesh)
        {
            ConvertPrimitive(mesh, prim, gltfMesh);
        }

        // =================================================================================
        //  场景层级构建
        // =================================================================================

        private void BuildSceneHierarchy()
        {
            // 确定默认场景
            int sceneIdx = _doc.Scene ?? 0;
            if (sceneIdx < 0 || sceneIdx >= _doc.Scenes.Count)
            {
                // 无场景定义时，将所有无父节点的节点作为根
                var parentSet = new HashSet<int>();
                foreach (var node in _doc.Nodes)
                    foreach (var child in node.Children)
                        parentSet.Add(child);

                for (int i = 0; i < _doc.Nodes.Count; i++)
                {
                    if (!parentSet.Contains(i))
                        Scene.RootNodes.Add(ConvertNode(i));
                }
                return;
            }

            var scene = _doc.Scenes[sceneIdx];
            foreach (var nodeIdx in scene.Nodes)
            {
                Scene.RootNodes.Add(ConvertNode(nodeIdx));
            }
        }

        private InterchangeNode ConvertNode(int nodeIndex)
        {
            var gltfNode = _doc.Nodes[nodeIndex];
            var node = new InterchangeNode
            {
                Name = string.IsNullOrEmpty(gltfNode.Name) ? $"Node_{nodeIndex}" : gltfNode.Name
            };
            node.Metadata["glTFNodeIndex"] = nodeIndex.ToString();

            // 变换矩阵
            Matrix4x4 localMatrix;

            if (gltfNode.Translation != null || gltfNode.Rotation != null || gltfNode.Scale != null)
            {
                Vector3 translation = gltfNode.Translation != null && gltfNode.Translation.Length >= 3
                    ? new Vector3(gltfNode.Translation[0], gltfNode.Translation[1], gltfNode.Translation[2])
                    : Vector3.Zero;

                Quaternion rotation = Quaternion.Identity;
                if (gltfNode.Rotation != null && gltfNode.Rotation.Length >= 4)
                {
                    rotation = new Quaternion(
                        gltfNode.Rotation[0], gltfNode.Rotation[1],
                        gltfNode.Rotation[2], gltfNode.Rotation[3]
                    );
                }

                Vector3 scale = gltfNode.Scale != null && gltfNode.Scale.Length >= 3
                    ? new Vector3(gltfNode.Scale[0], gltfNode.Scale[1], gltfNode.Scale[2])
                    : Vector3.One;

                // 坐标系转换
                if (_options.ConvertToLeftHanded)
                {
                    translation.Z = -translation.Z;
                    rotation = new Quaternion(-rotation.X, -rotation.Y, rotation.Z, rotation.W);
                }

                translation *= _options.Scale;

                localMatrix = Matrix4x4.CreateScale(scale) *
                              Matrix4x4.CreateFromQuaternion(rotation) *
                              Matrix4x4.CreateTranslation(translation);
            }
            else
            {
                localMatrix = Matrix4x4.Identity;
            }

            node.LocalTransform = localMatrix;

            // 网格引用
            if (gltfNode.Mesh.HasValue)
                node.MeshIndex = gltfNode.Mesh.Value;

            // 相机引用（元数据）
            if (gltfNode.Camera.HasValue && _options.ImportCameras)
            {
                node.Metadata["CameraIndex"] = gltfNode.Camera.Value.ToString();
            }

            // 灯光引用（KHR_lights_punctual）
            if (gltfNode.Light.HasValue && _options.ImportLights)
            {
                node.Metadata["LightIndex"] = gltfNode.Light.Value.ToString();
            }

            // 蒙皮引用
            if (gltfNode.Skin.HasValue)
            {
                node.Metadata["SkinIndex"] = gltfNode.Skin.Value.ToString();
            }

            // Morph Target 权重
            if (gltfNode.Weights != null)
            {
                node.Metadata["MorphWeights"] = string.Join(",", gltfNode.Weights);
            }

            // 递归子节点
            foreach (var childIdx in gltfNode.Children)
            {
                node.Children.Add(ConvertNode(childIdx));
            }

            return node;
        }

        // =================================================================================
        //  蒙皮转换
        // =================================================================================

        private void ConvertSkins()
        {
            for (int i = 0; i < _doc.Skins.Count; i++)
            {
                var gltfSkin = _doc.Skins[i];

                // 读取逆绑定矩阵
                Matrix4x4[] inverseBindMatrices = Array.Empty<Matrix4x4>();
                if (gltfSkin.InverseBindMatrices.HasValue)
                {
                    float[] ibmData = ReadAccessorFloat(gltfSkin.InverseBindMatrices.Value);
                    int matCount = ibmData.Length / 16;
                    inverseBindMatrices = new Matrix4x4[matCount];

                    for (int j = 0; j < matCount; j++)
                    {
                        inverseBindMatrices[j] = new Matrix4x4(
                            ibmData[j * 16 + 0], ibmData[j * 16 + 1], ibmData[j * 16 + 2], ibmData[j * 16 + 3],
                            ibmData[j * 16 + 4], ibmData[j * 16 + 5], ibmData[j * 16 + 6], ibmData[j * 16 + 7],
                            ibmData[j * 16 + 8], ibmData[j * 16 + 9], ibmData[j * 16 + 10], ibmData[j * 16 + 11],
                            ibmData[j * 16 + 12], ibmData[j * 16 + 13], ibmData[j * 16 + 14], ibmData[j * 16 + 15]
                        );
                    }
                }

                // 创建骨骼
                for (int j = 0; j < gltfSkin.Joints.Count; j++)
                {
                    int jointNodeIdx = gltfSkin.Joints[j];
                    var jointNode = _doc.Nodes[jointNodeIdx];

                    var bone = new InterchangeBone
                    {
                        Name = string.IsNullOrEmpty(jointNode.Name) ? $"Bone_{j}" : jointNode.Name,
                        ParentIndex = -1,
                        InverseBindPose = j < inverseBindMatrices.Length ? inverseBindMatrices[j] : Matrix4x4.Identity
                    };

                    // 从节点变换提取绑定姿势
                    if (jointNode.Translation != null || jointNode.Rotation != null || jointNode.Scale != null)
                    {
                        Vector3 t = jointNode.Translation != null && jointNode.Translation.Length >= 3
                            ? new Vector3(jointNode.Translation[0], jointNode.Translation[1], jointNode.Translation[2])
                            : Vector3.Zero;

                        Quaternion r = Quaternion.Identity;
                        if (jointNode.Rotation != null && jointNode.Rotation.Length >= 4)
                            r = new Quaternion(jointNode.Rotation[0], jointNode.Rotation[1],
                                               jointNode.Rotation[2], jointNode.Rotation[3]);

                        Vector3 s = jointNode.Scale != null && jointNode.Scale.Length >= 3
                            ? new Vector3(jointNode.Scale[0], jointNode.Scale[1], jointNode.Scale[2])
                            : Vector3.One;

                        bone.LocalPosition = t;
                        bone.LocalRotation = r;
                        bone.LocalScale = s;
                        bone.BindPoseLocal = Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(r) * Matrix4x4.CreateTranslation(t);
                    }

                    Scene.Bones.Add(bone);
                }

                // 设置骨骼父级关系
                for (int j = 0; j < gltfSkin.Joints.Count; j++)
                {
                    int jointNodeIdx = gltfSkin.Joints[j];
                    var jointNode = _doc.Nodes[jointNodeIdx];

                    // 查找父关节
                    foreach (var childIdx in jointNode.Children)
                    {
                        int childJointIdx = gltfSkin.Joints.IndexOf(childIdx);
                        if (childJointIdx >= 0)
                        {
                            int boneIdx = GetBoneBaseIndex(i) + j;
                            int childBoneIdx = GetBoneBaseIndex(i) + childJointIdx;
                            Scene.Bones[childBoneIdx].ParentIndex = boneIdx;
                            Scene.Bones[childBoneIdx].ParentName = Scene.Bones[boneIdx].Name;
                        }
                    }
                }
            }
        }

        private int GetBoneBaseIndex(int skinIndex)
        {
            int baseIdx = 0;
            for (int i = 0; i < skinIndex; i++)
                baseIdx += _doc.Skins[i].Joints.Count;
            return baseIdx;
        }

        // =================================================================================
        //  动画转换
        // =================================================================================

        private void ConvertAnimations()
        {
            foreach (var gltfAnim in _doc.Animations)
            {
                var clip = new InterchangeAnimationClip
                {
                    Name = string.IsNullOrEmpty(gltfAnim.Name) ? "Animation" : gltfAnim.Name,
                    FrameRate = 30f
                };

                // 按 Node 分组通道
                var tracksByNode = new Dictionary<int, InterchangeBoneTrack>();

                foreach (var channel in gltfAnim.Channels)
                {
                    if (channel.Sampler < 0 || channel.Sampler >= gltfAnim.Samplers.Count)
                        continue;

                    var sampler = gltfAnim.Samplers[channel.Sampler];
                    int targetNode = channel.Target.Node;

                    if (!tracksByNode.TryGetValue(targetNode, out var track))
                    {
                        var nodeName = _doc.Nodes[targetNode].Name;
                        track = new InterchangeBoneTrack { BoneName = string.IsNullOrEmpty(nodeName) ? $"Node_{targetNode}" : nodeName };
                        tracksByNode[targetNode] = track;
                    }

                    // 读取时间键
                    float[] times = ReadAccessorFloat(sampler.Input);
                    float[] values = ReadAccessorFloat(sampler.Output);

                    // 更新动画时长
                    if (times.Length > 0 && times[^1] > clip.Duration)
                        clip.Duration = times[^1];

                    string path = channel.Target.Path;
                    string interpolation = sampler.Interpolation;

                    if (path == "translation")
                    {
                        int compCount = interpolation == "CUBICSPLINE" ? 9 : 3;
                        for (int i = 0; i < times.Length; i++)
                        {
                            int offset = interpolation == "CUBICSPLINE" ? i * compCount + 3 : i * compCount;
                            if (offset + 2 < values.Length)
                            {
                                var pos = new Vector3(values[offset], values[offset + 1], values[offset + 2]);
                                if (_options.ConvertToLeftHanded)
                                    pos.Z = -pos.Z;
                                pos *= _options.Scale;
                                track.PositionKeys.Add((times[i], pos));
                            }
                        }
                    }
                    else if (path == "rotation")
                    {
                        int compCount = interpolation == "CUBICSPLINE" ? 12 : 4;
                        for (int i = 0; i < times.Length; i++)
                        {
                            int offset = interpolation == "CUBICSPLINE" ? i * compCount + 4 : i * compCount;
                            if (offset + 3 < values.Length)
                            {
                                var rot = new Quaternion(
                                    values[offset], values[offset + 1],
                                    values[offset + 2], values[offset + 3]
                                );
                                if (_options.ConvertToLeftHanded)
                                    rot = new Quaternion(-rot.X, -rot.Y, rot.Z, rot.W);
                                track.RotationKeys.Add((times[i], rot));
                            }
                        }
                    }
                    else if (path == "scale")
                    {
                        int compCount = interpolation == "CUBICSPLINE" ? 9 : 3;
                        for (int i = 0; i < times.Length; i++)
                        {
                            int offset = interpolation == "CUBICSPLINE" ? i * compCount + 3 : i * compCount;
                            if (offset + 2 < values.Length)
                            {
                                track.ScaleKeys.Add((times[i], new Vector3(
                                    values[offset], values[offset + 1], values[offset + 2])));
                            }
                        }
                    }
                    else if (path == "weights" && _options.ImportMorphTargets)
                    {
                        // Morph Target 权重动画（记录在元数据中）
                        _messages.Add($"动画 {clip.Name}: Morph Target 权重动画（Node {targetNode}）");
                    }
                }

                foreach (var track in tracksByNode.Values)
                    clip.BoneTracks.Add(track);

                // 帧率估算
                if (clip.Duration > 0)
                {
                    int maxKeys = 0;
                    foreach (var track in clip.BoneTracks)
                        maxKeys = Math.Max(maxKeys, Math.Max(track.PositionKeys.Count,
                            Math.Max(track.RotationKeys.Count, track.ScaleKeys.Count)));
                    if (maxKeys > 1)
                        clip.FrameRate = (maxKeys - 1) / clip.Duration;
                }

                Scene.AnimationClips.Add(clip);
            }
        }

        // =================================================================================
        //  后处理
        // =================================================================================

        private void PostProcessScene()
        {
            // 为缺少法线的网格生成法线
            if (_options.GenerateNormals)
            {
                foreach (var mesh in Scene.Meshes)
                {
                    if (mesh.Normals.Count == 0 && mesh.Vertices.Count > 0 && mesh.Indices.Count > 0)
                    {
                        GenerateNormalsForMesh(mesh);
                    }
                }
            }

            // 为缺少切线的网格生成切线
            if (_options.GenerateTangents)
            {
                foreach (var mesh in Scene.Meshes)
                {
                    if (mesh.Tangents.Count == 0 && mesh.UV0.Count > 0 && mesh.Normals.Count > 0)
                    {
                        GenerateTangentsForMesh(mesh);
                    }
                }
            }

            // 没有材质的网格添加默认材质
            if (Scene.Materials.Count == 0 && Scene.Meshes.Count > 0)
            {
                var defaultMat = new InterchangeMaterial
                {
                    Name = "DefaultMaterial",
                    DiffuseColor = new Vector3(0.8f, 0.8f, 0.8f)
                };
                Scene.Materials.Add(defaultMat);

                foreach (var mesh in Scene.Meshes)
                    mesh.MaterialIndices.Add(0);
            }
        }

        /// <summary>为网格计算法线（面法线平均）。</summary>
        private void GenerateNormalsForMesh(InterchangeMesh mesh)
        {
            mesh.Normals.Clear();
            for (int i = 0; i < mesh.Vertices.Count; i++)
                mesh.Normals.Add(Vector3.Zero);

            for (int i = 0; i + 2 < mesh.Indices.Count; i += 3)
            {
                int i0 = mesh.Indices[i];
                int i1 = mesh.Indices[i + 1];
                int i2 = mesh.Indices[i + 2];

                if (i0 >= mesh.Vertices.Count || i1 >= mesh.Vertices.Count || i2 >= mesh.Vertices.Count)
                    continue;

                Vector3 v0 = mesh.Vertices[i0];
                Vector3 v1 = mesh.Vertices[i1];
                Vector3 v2 = mesh.Vertices[i2];

                Vector3 normal = Vector3.Normalize(Vector3.Cross(v1 - v0, v2 - v0));

                mesh.Normals[i0] += normal;
                mesh.Normals[i1] += normal;
                mesh.Normals[i2] += normal;
            }

            for (int i = 0; i < mesh.Normals.Count; i++)
            {
                if (mesh.Normals[i] != Vector3.Zero)
                    mesh.Normals[i] = Vector3.Normalize(mesh.Normals[i]);
                else
                    mesh.Normals[i] = Vector3.UnitY;
            }
        }

        /// <summary>为网格生成切线（基于 UV 坐标）。</summary>
        private void GenerateTangentsForMesh(InterchangeMesh mesh)
        {
            mesh.Tangents.Clear();
            for (int i = 0; i < mesh.Vertices.Count; i++)
                mesh.Tangents.Add(Vector4.Zero);

            for (int i = 0; i + 2 < mesh.Indices.Count; i += 3)
            {
                int i0 = mesh.Indices[i];
                int i1 = mesh.Indices[i + 1];
                int i2 = mesh.Indices[i + 2];

                if (i0 >= mesh.Vertices.Count || i1 >= mesh.Vertices.Count || i2 >= mesh.Vertices.Count)
                    continue;
                if (i0 >= mesh.UV0.Count || i1 >= mesh.UV0.Count || i2 >= mesh.UV0.Count)
                    continue;

                Vector3 v0 = mesh.Vertices[i0];
                Vector3 v1 = mesh.Vertices[i1];
                Vector3 v2 = mesh.Vertices[i2];

                Vector2 uv0 = mesh.UV0[i0];
                Vector2 uv1 = mesh.UV0[i1];
                Vector2 uv2 = mesh.UV0[i2];

                Vector3 edge1 = v1 - v0;
                Vector3 edge2 = v2 - v0;
                Vector2 deltaUV1 = uv1 - uv0;
                Vector2 deltaUV2 = uv2 - uv0;

                float denom = deltaUV1.X * deltaUV2.Y - deltaUV1.Y * deltaUV2.X;
                float r = Math.Abs(denom) > 1e-6f ? 1f / denom : 1f;

                Vector3 tangent = Vector3.Normalize((edge1 * deltaUV2.Y - edge2 * deltaUV1.Y) * r);
                float tangentW = denom < 0 ? -1f : 1f;

                mesh.Tangents[i0] += new Vector4(tangent, tangentW);
                mesh.Tangents[i1] += new Vector4(tangent, tangentW);
                mesh.Tangents[i2] += new Vector4(tangent, tangentW);
            }

            for (int i = 0; i < mesh.Tangents.Count; i++)
            {
                var t = mesh.Tangents[i];
                var t3 = new Vector3(t.X, t.Y, t.Z);
                if (t3 != Vector3.Zero)
                {
                    t3 = Vector3.Normalize(t3);
                    mesh.Tangents[i] = new Vector4(t3, t.W);
                }
                else
                    mesh.Tangents[i] = new Vector4(1, 0, 0, 1);
            }
        }

        // =================================================================================
        //  进度报告
        // =================================================================================

        private void ReportProgress(float progress, string message)
        {
            _progressCallback?.Invoke(progress, message);
        }
    }

    // =================================================================================
    //  glTFImporter 工厂
    // =================================================================================

    /// <summary>glTF 导入器适配器（用于 AssetImportManager 注册）。</summary>
    public sealed class GlTFImporterAdapter : IAssetImporter
    {
        /// <summary>支持的扩展名。</summary>
        public IEnumerable<string> SupportedExtensions => new[] { "gltf", "glb" };

        /// <summary>导入 glTF 文件。</summary>
        public InterchangeScene Import(string filePath)
        {
            var importer = new GlTFImporter(filePath);
            return importer.Import();
        }
    }
}
