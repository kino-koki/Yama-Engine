// =====================================================================================
//  YamaEngine - MeshImportPipeline：模型导入→GPU渲染 完整管线连接器
//  命名空间：YamaEngine.Rendering
// -------------------------------------------------------------------------------------
//  职责:
//    1. 接收导入的文件路径和格式
//    2. 调用正确的导入器 (OBJImporter / FbxImporter / GlTFImporter)
//    3. 将导入结果 (InterchangeScene → EditableMesh) 转换
//    4. 上传到 GPU (通过 SceneRendererReal.UploadMesh)
//    5. 提取材质纹理路径列表并可选加载纹理
//
//  解决的断裂点:
//    - AssetDatabase.ImportAsset() 产出 DCCMeshData 但不转换/不上传
//    - DCCMeshData 与 EditableMesh 之间无转换代码
//    - InterchangeScene (导入器输出) 与 EditableMesh (GPU输入) 之间无转换代码
//    - SceneRendererReal._meshCache 从未被填充
//
//  对接:
//    输入: 文件路径 (obj/fbx/gltf/glb)
//    导入器: YamaEngine.Assets.Importers.{OBJImporter,FbxImporter,GlTFImporter}
//    中间格式: YamaEngine.Assets.Importers.InterchangeScene / YamaEngine.DCC.DCCSceneData
//    输出: YamaEngine.Modeling.EditableMesh → SceneRendererReal.UploadMesh()
//    纹理: YamaEngine.Rendering.TextureManager.LoadFromFile()
// =====================================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using YamaEngine.Assets.Importers;
using YamaEngine.DCC;
using YamaEngine.Modeling;
using YamaEngine.RHI;

namespace YamaEngine.Rendering
{
    // =================================================================================
    //  ImportedMeshResult —— 导入结果
    // =================================================================================

    /// <summary>
    /// 模型导入结果。包含转换后的 EditableMesh、材质列表、纹理路径列表。
    /// </summary>
    public struct ImportedMeshResult
    {
        /// <summary>转换后的可编辑网格（主网格，多子网格场景时为第一个）。</summary>
        public EditableMesh? Mesh;

        /// <summary>场景中所有子网格（多子网格时按顺序排列）。</summary>
        public List<EditableMesh> SubMeshes;

        /// <summary>导入是否成功。</summary>
        public bool Success;

        /// <summary>错误信息（失败时）。</summary>
        public string? Error;

        /// <summary>所有纹理路径（从材质中提取）。</summary>
        public List<string> TexturePaths;

        /// <summary>转换后的材质列表。</summary>
        public List<ImportedMaterial> Materials;

        /// <summary>源场景数据（保留完整场景引用，供后续使用）。</summary>
        public InterchangeScene? SourceScene;
    }

    // =================================================================================
    //  ImportedMaterial —— 导入后的材质数据
    // =================================================================================

    /// <summary>
    /// 从导入结果中提取的材质信息。桥接 InterchangeMaterial 与引擎渲染材质。
    /// </summary>
    public struct ImportedMaterial
    {
        /// <summary>材质名称。</summary>
        public string Name;

        /// <summary>漫反射颜色 (线性空间)。</summary>
        public Vector4 BaseColor;

        /// <summary>金属度 [0,1]。</summary>
        public float Metallic;

        /// <summary>粗糙度 [0,1]。</summary>
        public float Roughness;

        /// <summary>不透明度 [0,1]。</summary>
        public float Opacity;

        /// <summary>漫反射纹理路径。</summary>
        public string? AlbedoTexturePath;

        /// <summary>法线纹理路径。</summary>
        public string? NormalTexturePath;

        /// <summary>粗糙度纹理路径。</summary>
        public string? RoughnessTexturePath;

        /// <summary>金属度纹理路径。</summary>
        public string? MetallicTexturePath;

        /// <summary>自发光纹理路径。</summary>
        public string? EmissiveTexturePath;

        /// <summary>对应的 InterchangeMaterial 索引。</summary>
        public int SourceMaterialIndex;
    }

    // =================================================================================
    //  MeshImportPipeline —— 管线主类
    // =================================================================================

    /// <summary>
    /// 模型导入管线：打通 文件导入 → 数据转换 → GPU上传 的完整流程。
    /// 
    /// 使用方式:
    ///   var pipeline = new MeshImportPipeline(device, sceneRenderer, textureManager);
    ///   var result = pipeline.ImportAndUpload("Assets/Models/character.fbx");
    ///   if (result.Success) { /* 网格已上传GPU，可直接渲染 */ }
    /// </summary>
    public class MeshImportPipeline
    {
        private readonly RHIDevice _device;
        private readonly SceneRendererReal _sceneRenderer;
        private readonly TextureManager? _textureManager;

        // 已加载纹理缓存 (路径 → GPU纹理)
        private readonly Dictionary<string, RHITexture> _loadedTextures = new(StringComparer.OrdinalIgnoreCase);

        // 已上传的网格路径集合 (避免重复上传)
        private readonly HashSet<string> _uploadedMeshPaths = new(StringComparer.OrdinalIgnoreCase);

        // 导入日志
        private readonly List<string> _logMessages = new();

        /// <summary>获取导入日志消息列表。</summary>
        public IReadOnlyList<string> LogMessages => _logMessages;

        /// <summary>
        /// 创建模型导入管线。
        /// </summary>
        /// <param name="device">RHI 设备。</param>
        /// <param name="renderer">真实场景渲染器 (用于 UploadMesh)。</param>
        /// <param name="textureManager">纹理管理器 (可选，用于自动加载材质纹理)。</param>
        public MeshImportPipeline(RHIDevice device, SceneRendererReal renderer, TextureManager? textureManager = null)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _sceneRenderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
            _textureManager = textureManager;
        }

        // =================================================================================
        //  主入口：导入模型文件并上传GPU
        // =================================================================================

        /// <summary>
        /// 导入模型文件并上传到 GPU。
        /// 流程: 根据扩展名选择导入器 → 解析文件 → 转换为 EditableMesh → UploadMesh。
        /// </summary>
        /// <param name="filePath">模型文件路径 (.obj/.fbx/.gltf/.glb)。</param>
        /// <param name="autoLoadTextures">是否自动加载材质纹理到 GPU。</param>
        /// <returns>导入结果，包含 EditableMesh 和材质/纹理信息。</returns>
        public ImportedMeshResult ImportAndUpload(string filePath, bool autoLoadTextures = true)
        {
            if (string.IsNullOrEmpty(filePath))
                return Fail("文件路径不能为空");

            if (!File.Exists(filePath))
                return Fail($"文件不存在: {filePath}");

            _logMessages.Clear();
            Log($"[MeshImportPipeline] 开始导入: {Path.GetFileName(filePath)}");

            // 1. 根据扩展名选择导入器并解析
            InterchangeScene? scene = null;
            string ext = Path.GetExtension(filePath).ToLowerInvariant();

            try
            {
                switch (ext)
                {
                    case ".obj":
                        scene = ImportViaImporter(filePath, "OBJ");
                        break;
                    case ".fbx":
                        scene = ImportViaImporter(filePath, "FBX");
                        break;
                    case ".gltf":
                    case ".glb":
                        scene = ImportViaImporter(filePath, "glTF");
                        break;
                    default:
                        // 未知格式: 尝试 DCCBridge 回退
                        Log($"[MeshImportPipeline] 不支持的格式 '{ext}'，尝试 DCCBridge 回退");
                        scene = ImportViaDCCBridge(filePath);
                        break;
                }
            }
            catch (Exception ex)
            {
                Log($"[MeshImportPipeline] 导入器失败: {ex.Message}，尝试内置OBJ后备解析器");
                // 回退到内置解析器 (仅支持OBJ)
                scene = ImportFallback(filePath);
            }

            if (scene == null || scene.Meshes.Count == 0)
                return Fail("导入结果为空或没有网格数据");

            // 2. 将 InterchangeScene 转换为 EditableMesh 列表
            var subMeshes = new List<EditableMesh>();
            var materials = new List<ImportedMaterial>();
            var texturePaths = new List<string>();

            for (int i = 0; i < scene.Meshes.Count; i++)
            {
                var interchangeMesh = scene.Meshes[i];
                string meshCacheKey = scene.Meshes.Count == 1
                    ? filePath
                    : $"{filePath}?submesh={i}";

                var editableMesh = InterchangeMeshToEditableMesh(interchangeMesh, scene, i);
                subMeshes.Add(editableMesh);

                // 3. 上传到 GPU
                if (!_uploadedMeshPaths.Contains(meshCacheKey))
                {
                    _sceneRenderer.UploadMesh(meshCacheKey, editableMesh);
                    _uploadedMeshPaths.Add(meshCacheKey);
                    Log($"[MeshImportPipeline] 已上传网格 '{editableMesh.Name}' 到 GPU ({editableMesh.Vertices.Count} 顶点, {editableMesh.Faces.Count} 面)");
                }
            }

            // 4. 提取材质信息
            materials = ExtractMaterials(scene);
            texturePaths = ExtractTexturePaths(scene);

            // 5. 可选: 自动加载纹理
            if (autoLoadTextures && _textureManager != null)
            {
                LoadTextures(texturePaths);
            }

            // 合并所有子网格的日志消息
            if (scene.ImportMessages.Count > 0)
            {
                foreach (var msg in scene.ImportMessages)
                    Log($"[导入器] {msg}");
            }

            Log($"[MeshImportPipeline] 导入完成: {subMeshes.Count} 个子网格, {materials.Count} 个材质, {texturePaths.Count} 个纹理");

            return new ImportedMeshResult
            {
                Mesh = subMeshes.Count > 0 ? subMeshes[0] : null,
                SubMeshes = subMeshes,
                Success = true,
                TexturePaths = texturePaths,
                Materials = materials,
                SourceScene = scene
            };
        }

        // =================================================================================
        //  导入器调用
        // =================================================================================

        /// <summary>
        /// 通过引擎标准导入器导入文件，返回 InterchangeScene。
        /// </summary>
        private InterchangeScene? ImportViaImporter(string filePath, string format)
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();

            switch (ext)
            {
                case ".obj":
                {
                    var importer = new OBJImporter(filePath);
                    return importer.Import();
                }
                case ".fbx":
                {
                    var importer = new FbxImporter(filePath);
                    return importer.Import();
                }
                case ".gltf":
                case ".glb":
                {
                    // GlTFImporter 构造函数接受 filePath
                    // GlTFImporter 在 YamaEngine.Assets.Importers 命名空间
                    var importerType = Type.GetType("YamaEngine.Assets.Importers.GlTFImporter, YamaEngine");
                    if (importerType != null)
                    {
                        var ctor = importerType.GetConstructor(new[] { typeof(string) });
                        if (ctor != null)
                        {
                            var importerInstance = ctor.Invoke(new object[] { filePath });
                            // 调用 Import() 无参数版本
                            var importMethod = importerType.GetMethod("Import", new Type[] { typeof(GltfImportOptions) });
                            if (importMethod == null)
                                importMethod = importerType.GetMethod("Import", Type.EmptyTypes);
                            if (importMethod != null)
                                return importMethod.Invoke(importerInstance, null) as InterchangeScene;
                        }
                    }
                    Log($"[MeshImportPipeline] GlTFImporter 类未找到，跳过 glTF 导入");
                    return null;
                }
                default:
                    return null;
            }
        }

        /// <summary>
        /// 通过 DCCBridge 导入文件作为回退方案。
        /// 将 DCCSceneData 转换为 InterchangeScene。
        /// </summary>
        private InterchangeScene? ImportViaDCCBridge(string filePath)
        {
            try
            {
                var bridge = new DCCBridge();
                var dccScene = bridge.ImportScene(filePath);

                if (dccScene == null || dccScene.Meshes.Count == 0)
                {
                    Log("[MeshImportPipeline] DCCBridge 未返回任何网格");
                    return null;
                }

                // 将 DCCSceneData 转换为 InterchangeScene
                return DCCSceneDataToInterchangeScene(dccScene, filePath);
            }
            catch (Exception ex)
            {
                Log($"[MeshImportPipeline] DCCBridge 回退失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 内置后备解析器 (简易OBJ解析器)。
        /// 当标准导入器不可用或失败时使用。
        /// </summary>
        private InterchangeScene? ImportFallback(string filePath)
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext != ".obj")
            {
                Log($"[MeshImportPipeline] 内置后备解析器仅支持 OBJ 格式，当前格式: {ext}");
                return null;
            }

            try
            {
                return ParseObjBuiltIn(filePath);
            }
            catch (Exception ex)
            {
                Log($"[MeshImportPipeline] 内置OBJ解析器失败: {ex.Message}");
                return null;
            }
        }

        // =================================================================================
        //  InterchangeScene → EditableMesh 转换器
        // =================================================================================

        /// <summary>
        /// 将 InterchangeMesh 转换为 EditableMesh。
        /// 这是引擎标准导入器 (OBJImporter/FbxImporter/GlTFImporter) 输出到 GPU 输入的核心转换。
        /// </summary>
        /// <param name="src">InterchangeMesh (导入器输出)。</param>
        /// <param name="scene">所属 InterchangeScene (用于提取材质信息)。</param>
        /// <param name="meshIndex">网格索引 (用于命名)。</param>
        /// <returns>EditableMesh (可直接上传GPU)。</returns>
        public static EditableMesh InterchangeMeshToEditableMesh(
            InterchangeMesh src, InterchangeScene? scene = null, int meshIndex = 0)
        {
            if (src == null)
                throw new ArgumentNullException(nameof(src));

            var mesh = new EditableMesh
            {
                Name = string.IsNullOrEmpty(src.Name) ? $"ImportedMesh_{meshIndex}" : src.Name
            };

            // 1. 复制顶点位置
            mesh.Vertices = new List<Vector3>(src.Vertices);

            // 2. 复制法线
            if (src.Normals != null && src.Normals.Count > 0)
            {
                mesh.Normals = new List<Vector3>(src.Normals);
            }

            // 3. 复制 UV0
            if (src.UV0 != null && src.UV0.Count > 0)
            {
                mesh.UV0 = new List<Vector2>(src.UV0);
            }

            // 4. 转换索引 → MeshFace
            //    InterchangeMesh.Indices 是扁平的 int 数组 (每3个一组 = 一个三角形)
            //    EditableMesh.Faces 是 MeshFace(A, B, C) 列表
            if (src.Indices != null && src.Indices.Count >= 3)
            {
                mesh.Faces = new List<MeshFace>(src.Indices.Count / 3);
                for (int i = 0; i + 2 < src.Indices.Count; i += 3)
                {
                    int a = src.Indices[i];
                    int b = src.Indices[i + 1];
                    int c = src.Indices[i + 2];

                    // 验证索引范围
                    if (a < 0 || b < 0 || c < 0 ||
                        a >= mesh.Vertices.Count || b >= mesh.Vertices.Count || c >= mesh.Vertices.Count)
                        continue;

                    mesh.Faces.Add(new MeshFace(a, b, c));
                }
            }

            // 5. 如果没有法线，自动计算
            if (mesh.Normals.Count == 0 && mesh.Vertices.Count > 0 && mesh.Faces.Count > 0)
            {
                mesh.RecalculateNormals();
            }

            // 6. 转换骨骼权重 (如果有)
            if (src.BoneWeights != null && src.BoneWeights.Count > 0)
            {
                mesh.BoneWeights = new List<MeshBoneWeight>(src.BoneWeights.Count);
                for (int i = 0; i < src.BoneWeights.Count; i++)
                {
                    var bw = src.BoneWeights[i];
                    mesh.BoneWeights.Add(new MeshBoneWeight(
                        bw.BoneIndex0, bw.BoneIndex1, bw.BoneIndex2, bw.BoneIndex3,
                        bw.Weight0, bw.Weight1, bw.Weight2, bw.Weight3));
                }
            }

            return mesh;
        }

        // =================================================================================
        //  DCCMeshData → EditableMesh 转换器
        // =================================================================================

        /// <summary>
        /// 将 DCCMeshData 转换为 EditableMesh。
        /// 这是 DCCBridge 导出格式到 GPU 输入的转换。
        /// DCCMeshData 使用 DCCVertex (含 Position/Normal/UV0/Color/MaterialIndex)，
        /// EditableMesh 使用分离的 Vertices/Normals/UV0/Faces。
        /// </summary>
        /// <param name="src">DCCMeshData (DCCBridge输出)。</param>
        /// <returns>EditableMesh (可直接上传GPU)。</returns>
        public static EditableMesh DCCMeshDataToEditableMesh(DCCMeshData src)
        {
            if (src == null)
                throw new ArgumentNullException(nameof(src));

            if (src.Vertices == null || src.Vertices.Count == 0)
                return new EditableMesh { Name = "EmptyMesh" };

            var mesh = new EditableMesh
            {
                Name = string.IsNullOrEmpty(src.Name) ? "DCCMesh" : src.Name
            };

            int vertexCount = src.Vertices.Count;

            // 1. 填充顶点位置
            mesh.Vertices = new List<Vector3>(vertexCount);
            for (int i = 0; i < vertexCount; i++)
            {
                mesh.Vertices.Add(src.Vertices[i].Position);
            }

            // 2. 填充法线 (如果有)
            if (src.HasNormals)
            {
                mesh.Normals = new List<Vector3>(vertexCount);
                for (int i = 0; i < vertexCount; i++)
                {
                    mesh.Normals.Add(src.Vertices[i].Normal);
                }
            }

            // 3. 填充 UV0 (如果有)
            if (src.HasUVs)
            {
                mesh.UV0 = new List<Vector2>(vertexCount);
                for (int i = 0; i < vertexCount; i++)
                {
                    mesh.UV0.Add(src.Vertices[i].UV0);
                }
            }

            // 4. 转换索引 → MeshFace
            //    DCCMeshData.Indices 是扁平的 int 数组 (每3个一组 = 一个三角形)
            if (src.Indices != null && src.Indices.Count >= 3)
            {
                mesh.Faces = new List<MeshFace>(src.Indices.Count / 3);
                for (int i = 0; i + 2 < src.Indices.Count; i += 3)
                {
                    int a = src.Indices[i];
                    int b = src.Indices[i + 1];
                    int c = src.Indices[i + 2];

                    // 验证索引范围
                    if (a < 0 || b < 0 || c < 0 ||
                        a >= vertexCount || b >= vertexCount || c >= vertexCount)
                        continue;

                    mesh.Faces.Add(new MeshFace(a, b, c));
                }
            }

            // 5. 如果没有法线，自动计算
            if (mesh.Normals.Count == 0 && mesh.Faces.Count > 0)
            {
                mesh.RecalculateNormals();
            }

            return mesh;
        }

        // =================================================================================
        //  DCCSceneData → InterchangeScene 转换器
        // =================================================================================

        /// <summary>
        /// 将 DCCSceneData (DCCBridge 输出) 转换为 InterchangeScene (管线标准格式)。
        /// 用于将 DCCBridge 回退导入的结果统一为 InterchangeScene。
        /// </summary>
        private static InterchangeScene DCCSceneDataToInterchangeScene(DCCSceneData dccScene, string filePath)
        {
            var scene = new InterchangeScene
            {
                Name = string.IsNullOrEmpty(dccScene.Name) ? Path.GetFileNameWithoutExtension(filePath) : dccScene.Name,
                SourceFilePath = filePath,
                SourceFormat = "DCC"
            };

            // 转换网格
            foreach (var dccMesh in dccScene.Meshes)
            {
                var mesh = new InterchangeMesh
                {
                    Name = dccMesh.Name
                };

                int vertexCount = dccMesh.Vertices.Count;

                // 顶点位置
                mesh.Vertices = new List<Vector3>(vertexCount);
                for (int i = 0; i < vertexCount; i++)
                    mesh.Vertices.Add(dccMesh.Vertices[i].Position);

                // 法线
                if (dccMesh.HasNormals)
                {
                    mesh.Normals = new List<Vector3>(vertexCount);
                    for (int i = 0; i < vertexCount; i++)
                        mesh.Normals.Add(dccMesh.Vertices[i].Normal);
                }

                // UV
                if (dccMesh.HasUVs)
                {
                    mesh.UV0 = new List<Vector2>(vertexCount);
                    for (int i = 0; i < vertexCount; i++)
                        mesh.UV0.Add(dccMesh.Vertices[i].UV0);
                }

                // 顶点颜色
                if (dccMesh.HasVertexColors)
                {
                    mesh.Colors = new List<Vector4>(vertexCount);
                    for (int i = 0; i < vertexCount; i++)
                        mesh.Colors.Add(dccMesh.Vertices[i].Color);
                }

                // 索引
                mesh.Indices = new List<int>(dccMesh.Indices);

                // 材质索引
                if (dccMesh.MaterialNames != null)
                {
                    for (int m = 0; m < dccMesh.MaterialNames.Count; m++)
                        mesh.MaterialIndices.Add(m);
                }

                scene.Meshes.Add(mesh);
            }

            // 转换材质
            if (dccScene.Materials != null)
            {
                for (int i = 0; i < dccScene.Materials.Count; i++)
                {
                    var dccMat = dccScene.Materials[i];
                    var mat = new InterchangeMaterial
                    {
                        Name = dccMat.Name ?? $"DCCMaterial_{i}"
                    };
                    scene.Materials.Add(mat);
                }
            }

            // 创建场景根节点
            if (scene.Meshes.Count > 0)
            {
                var rootNode = new InterchangeNode
                {
                    Name = scene.Name,
                    MeshIndex = 0
                };
                scene.RootNodes.Add(rootNode);
            }

            return scene;
        }

        // =================================================================================
        //  内置 OBJ 解析器 (后备)
        // =================================================================================

        /// <summary>
        /// 内置 OBJ 解析器。当标准导入器不可用时作为后备。
        /// 支持完整的 Wavefront OBJ 格式: v/vn/vt/f/g/o/s/usemtl/mtllib。
        /// </summary>
        private InterchangeScene ParseObjBuiltIn(string filePath)
        {
            var scene = new InterchangeScene
            {
                Name = Path.GetFileNameWithoutExtension(filePath),
                SourceFilePath = filePath,
                SourceFormat = "OBJ_BuiltIn"
            };

            // 原始OBJ数据
            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var texCoords = new List<Vector2>();
            var materials = new List<ObjMaterialInternal>();
            var materialNameToIndex = new Dictionary<string, int>(StringComparer.Ordinal);

            // 当前状态
            int currentMaterialIndex = -1;
            string currentGroupName = string.Empty;

            // 按材质分组的面 (用于后续三角化)
            var allFaces = new List<ObjFaceInternal>();

            string baseDir = Path.GetDirectoryName(filePath) ?? string.Empty;
            string[] lines = File.ReadAllLines(filePath, Encoding.UTF8);

            Log($"[内置OBJ解析器] 解析 {lines.Length} 行");

            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;

                int spaceIdx = line.IndexOf(' ');
                if (spaceIdx < 0) continue;

                string cmd = line.Substring(0, spaceIdx).Trim();
                string args = line.Substring(spaceIdx + 1).Trim();

                switch (cmd)
                {
                    case "v":
                    {
                        var parts = SplitWs(args);
                        if (parts.Length >= 3)
                        {
                            positions.Add(new Vector3(
                                ParseFloat(parts[0]),
                                ParseFloat(parts[1]),
                                ParseFloat(parts[2])));
                        }
                        break;
                    }
                    case "vn":
                    {
                        var parts = SplitWs(args);
                        if (parts.Length >= 3)
                        {
                            normals.Add(new Vector3(
                                ParseFloat(parts[0]),
                                ParseFloat(parts[1]),
                                ParseFloat(parts[2])));
                        }
                        break;
                    }
                    case "vt":
                    {
                        var parts = SplitWs(args);
                        if (parts.Length >= 2)
                        {
                            texCoords.Add(new Vector2(ParseFloat(parts[0]), 1f - ParseFloat(parts[1])));
                        }
                        break;
                    }
                    case "f":
                    {
                        var parts = SplitWs(args);
                        if (parts.Length < 3) break;

                        var posIndices = new List<int>();
                        var texIndices = new List<int>();
                        var normIndices = new List<int>();
                        bool hasTex = false, hasNorm = false;

                        foreach (var part in parts)
                        {
                            var indices = part.Split('/');
                            int pi = ParseObjIndex(indices[0], positions.Count);
                            if (pi >= 0) posIndices.Add(pi);

                            if (indices.Length > 1 && !string.IsNullOrEmpty(indices[1]))
                            {
                                int ti = ParseObjIndex(indices[1], texCoords.Count);
                                if (ti >= 0) { texIndices.Add(ti); hasTex = true; }
                            }

                            if (indices.Length > 2 && !string.IsNullOrEmpty(indices[2]))
                            {
                                int ni = ParseObjIndex(indices[2], normals.Count);
                                if (ni >= 0) { normIndices.Add(ni); hasNorm = true; }
                            }
                        }

                        if (posIndices.Count < 3) break;

                        allFaces.Add(new ObjFaceInternal
                        {
                            PositionIndices = posIndices.ToArray(),
                            TexCoordIndices = hasTex ? texIndices.ToArray() : null,
                            NormalIndices = hasNorm ? normIndices.ToArray() : null,
                            MaterialIndex = currentMaterialIndex >= 0 ? currentMaterialIndex : 0,
                            GroupName = currentGroupName
                        });
                        break;
                    }
                    case "g":
                        currentGroupName = args;
                        break;
                    case "o":
                        currentGroupName = args;
                        break;
                    case "s":
                        // 平滑组 (暂不处理)
                        break;
                    case "usemtl":
                        if (materialNameToIndex.TryGetValue(args, out int matIdx))
                            currentMaterialIndex = matIdx;
                        else
                            currentMaterialIndex = -1;
                        break;
                    case "mtllib":
                        ParseMtlBuiltIn(Path.Combine(baseDir, args), materials, materialNameToIndex);
                        break;
                }
            }

            Log($"[内置OBJ解析器] {positions.Count} 顶点, {normals.Count} 法线, {texCoords.Count} UV, {allFaces.Count} 面, {materials.Count} 材质");

            // 没有材质时创建默认材质
            if (materials.Count == 0)
            {
                materials.Add(new ObjMaterialInternal { Name = "DefaultMaterial" });
                materialNameToIndex["DefaultMaterial"] = 0;
            }

            // 按材质分组面 → 每组材质生成一个 InterchangeMesh
            var facesByMaterial = new Dictionary<int, List<ObjFaceInternal>>();
            foreach (var face in allFaces)
            {
                int matIdx = face.MaterialIndex >= 0 ? face.MaterialIndex : 0;
                if (!facesByMaterial.TryGetValue(matIdx, out var list))
                {
                    list = new List<ObjFaceInternal>();
                    facesByMaterial[matIdx] = list;
                }
                list.Add(face);
            }

            bool hasUVs = texCoords.Count > 0;
            bool hasNormals = normals.Count > 0;

            foreach (var kvp in facesByMaterial)
            {
                var mesh = new InterchangeMesh
                {
                    Name = $"Mesh_{kvp.Key}"
                };
                mesh.MaterialIndices.Add(kvp.Key);

                // 顶点去重映射
                var vertexMap = new Dictionary<long, int>();

                foreach (var face in kvp.Value)
                {
                    // 三角化 (扇形)
                    int[] triIndices = TriangulateBuiltIn(face.PositionIndices.Length);

                    for (int i = 0; i < triIndices.Length; i += 3)
                    {
                        for (int j = 0; j < 3; j++)
                        {
                            int vi = triIndices[i + j];
                            int posIdx = face.PositionIndices[vi];
                            int texIdx = face.HasTexCoords && vi < face.TexCoordIndices.Length ? face.TexCoordIndices[vi] : -1;
                            int normIdx = face.HasNormals && vi < face.NormalIndices.Length ? face.NormalIndices[vi] : -1;

                            long key = MakeVertexKeyBuiltIn(posIdx, texIdx, normIdx);

                            if (!vertexMap.TryGetValue(key, out int newIdx))
                            {
                                newIdx = mesh.Vertices.Count;
                                vertexMap[key] = newIdx;

                                // 位置
                                mesh.Vertices.Add(posIdx >= 0 && posIdx < positions.Count
                                    ? positions[posIdx]
                                    : Vector3.Zero);

                                // UV
                                if (hasUVs)
                                {
                                    mesh.UV0.Add(texIdx >= 0 && texIdx < texCoords.Count
                                        ? texCoords[texIdx]
                                        : Vector2.Zero);
                                }

                                // 法线
                                if (hasNormals)
                                {
                                    mesh.Normals.Add(normIdx >= 0 && normIdx < normals.Count
                                        ? normals[normIdx]
                                        : Vector3.UnitY);
                                }
                            }

                            mesh.Indices.Add(newIdx);
                        }
                    }
                }

                scene.Meshes.Add(mesh);
            }

            // 为缺少法线的网格生成法线
            foreach (var mesh in scene.Meshes)
            {
                if (mesh.Normals.Count == 0 && mesh.Vertices.Count > 0 && mesh.Indices.Count >= 3)
                {
                    GenerateNormalsForInterchangeMesh(mesh);
                }
            }

            // 转换材质到 InterchangeMaterial
            foreach (var objMat in materials)
            {
                var mat = new InterchangeMaterial
                {
                    Name = objMat.Name,
                    DiffuseColor = objMat.Diffuse,
                    Roughness = objMat.Shininess > 0 ? MathF.Sqrt(2f / (objMat.Shininess + 2f)) : 0.5f,
                    Opacity = objMat.Opacity,
                    DiffuseTexturePath = objMat.DiffuseTexture,
                    NormalTexturePath = string.IsNullOrEmpty(objMat.NormalTexture) ? objMat.BumpTexture : objMat.NormalTexture
                };
                scene.Materials.Add(mat);
            }

            // 创建根节点
            if (scene.Meshes.Count > 0)
            {
                scene.RootNodes.Add(new InterchangeNode
                {
                    Name = scene.Name,
                    MeshIndex = 0
                });
            }

            return scene;
        }

        /// <summary>
        /// 内置 MTL 解析器。
        /// </summary>
        private static void ParseMtlBuiltIn(string mtlPath, List<ObjMaterialInternal> materials,
            Dictionary<string, int> materialNameToIndex)
        {
            if (!File.Exists(mtlPath)) return;

            string baseDir = Path.GetDirectoryName(mtlPath) ?? string.Empty;
            string[] lines = File.ReadAllLines(mtlPath, Encoding.UTF8);
            ObjMaterialInternal? currentMat = null;

            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;

                int spaceIdx = line.IndexOf(' ');
                if (spaceIdx < 0) continue;

                string cmd = line.Substring(0, spaceIdx).Trim();
                string args = line.Substring(spaceIdx + 1).Trim();

                switch (cmd)
                {
                    case "newmtl":
                        if (currentMat != null)
                        {
                            materialNameToIndex[currentMat.Name] = materials.Count;
                            materials.Add(currentMat);
                        }
                        currentMat = new ObjMaterialInternal { Name = args };
                        break;
                    case "Kd":
                        if (currentMat != null) currentMat.Diffuse = ParseColor3(args);
                        break;
                    case "Ks":
                        if (currentMat != null) currentMat.Specular = ParseColor3(args);
                        break;
                    case "Ka":
                        if (currentMat != null) currentMat.Ambient = ParseColor3(args);
                        break;
                    case "Ke":
                        if (currentMat != null) currentMat.Emissive = ParseColor3(args);
                        break;
                    case "Ns":
                        if (currentMat != null) currentMat.Shininess = ParseFloat(args);
                        break;
                    case "d":
                        if (currentMat != null) currentMat.Opacity = ParseFloat(args);
                        break;
                    case "Tr":
                        if (currentMat != null) currentMat.Opacity = 1f - ParseFloat(args);
                        break;
                    case "map_Kd":
                        if (currentMat != null) currentMat.DiffuseTexture = ResolveTexPath(args, baseDir);
                        break;
                    case "map_Bump":
                    case "bump":
                        if (currentMat != null) currentMat.BumpTexture = ResolveTexPath(args, baseDir);
                        break;
                    case "norm":
                        if (currentMat != null) currentMat.NormalTexture = ResolveTexPath(args, baseDir);
                        break;
                    case "map_Ks":
                        if (currentMat != null) currentMat.SpecularTexture = ResolveTexPath(args, baseDir);
                        break;
                    case "map_Ke":
                        if (currentMat != null) currentMat.EmissiveTexture = ResolveTexPath(args, baseDir);
                        break;
                    case "map_d":
                        if (currentMat != null) currentMat.OpacityTexture = ResolveTexPath(args, baseDir);
                        break;
                }
            }

            if (currentMat != null)
            {
                materialNameToIndex[currentMat.Name] = materials.Count;
                materials.Add(currentMat);
            }
        }

        // =================================================================================
        //  材质提取与纹理加载
        // =================================================================================

        /// <summary>
        /// 从 InterchangeScene 提取材质信息为 ImportedMaterial 列表。
        /// </summary>
        private static List<ImportedMaterial> ExtractMaterials(InterchangeScene scene)
        {
            var result = new List<ImportedMaterial>();
            if (scene?.Materials == null) return result;

            for (int i = 0; i < scene.Materials.Count; i++)
            {
                var src = scene.Materials[i];
                result.Add(new ImportedMaterial
                {
                    Name = src.Name,
                    BaseColor = new Vector4(src.DiffuseColor, src.Opacity),
                    Metallic = src.Metallic,
                    Roughness = src.Roughness,
                    Opacity = src.Opacity,
                    AlbedoTexturePath = string.IsNullOrEmpty(src.DiffuseTexturePath) ? null : src.DiffuseTexturePath,
                    NormalTexturePath = string.IsNullOrEmpty(src.NormalTexturePath) ? null : src.NormalTexturePath,
                    RoughnessTexturePath = string.IsNullOrEmpty(src.RoughnessTexturePath) ? null : src.RoughnessTexturePath,
                    MetallicTexturePath = string.IsNullOrEmpty(src.MetallicTexturePath) ? null : src.MetallicTexturePath,
                    EmissiveTexturePath = string.IsNullOrEmpty(src.EmissiveTexturePath) ? null : src.EmissiveTexturePath,
                    SourceMaterialIndex = i
                });
            }

            return result;
        }

        /// <summary>
        /// 从 InterchangeScene 提取所有不重复的纹理路径。
        /// </summary>
        private static List<string> ExtractTexturePaths(InterchangeScene scene)
        {
            var pathSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (scene?.Materials == null) return new List<string>();

            foreach (var mat in scene.Materials)
            {
                AddIfValid(mat.DiffuseTexturePath, pathSet);
                AddIfValid(mat.NormalTexturePath, pathSet);
                AddIfValid(mat.RoughnessTexturePath, pathSet);
                AddIfValid(mat.MetallicTexturePath, pathSet);
                AddIfValid(mat.EmissiveTexturePath, pathSet);
                AddIfValid(mat.OcclusionTexturePath, pathSet);

                if (mat.ExtraTextureSlots != null)
                {
                    foreach (var kvp in mat.ExtraTextureSlots)
                        AddIfValid(kvp.Value, pathSet);
                }
            }

            var result = new List<string>(pathSet);
            return result;
        }

        private static void AddIfValid(string? path, HashSet<string> set)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                set.Add(path);
        }

        /// <summary>
        /// 加载纹理到 GPU。使用 TextureManager.LoadFromFile 并缓存结果。
        /// </summary>
        private void LoadTextures(List<string> texturePaths)
        {
            if (_textureManager == null) return;

            foreach (var texPath in texturePaths)
            {
                if (_loadedTextures.ContainsKey(texPath)) continue;

                try
                {
                    var texture = _textureManager.LoadFromFile(texPath);
                    _loadedTextures[texPath] = texture;
                    Log($"[MeshImportPipeline] 已加载纹理: {Path.GetFileName(texPath)}");
                }
                catch (Exception ex)
                {
                    Log($"[MeshImportPipeline] 纹理加载失败 '{Path.GetFileName(texPath)}': {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 获取已加载的 GPU 纹理。
        /// </summary>
        public RHITexture? GetLoadedTexture(string texturePath)
        {
            if (string.IsNullOrEmpty(texturePath)) return null;
            _loadedTextures.TryGetValue(texturePath, out var tex);
            return tex;
        }

        // =================================================================================
        //  辅助工具
        // =================================================================================

        private ImportedMeshResult Fail(string error)
        {
            Log($"[MeshImportPipeline] 错误: {error}");
            return new ImportedMeshResult
            {
                Success = false,
                Error = error,
                SubMeshes = new List<EditableMesh>(),
                TexturePaths = new List<string>(),
                Materials = new List<ImportedMaterial>()
            };
        }

        private void Log(string message)
        {
            _logMessages.Add(message);
            Console.WriteLine(message);
        }

        // --- 内置OBJ解析器内部类型 ---

        private sealed class ObjMaterialInternal
        {
            public string Name = string.Empty;
            public Vector3 Ambient = new(0.2f, 0.2f, 0.2f);
            public Vector3 Diffuse = new(0.8f, 0.8f, 0.8f);
            public Vector3 Specular = Vector3.Zero;
            public Vector3 Emissive = Vector3.Zero;
            public float Shininess;
            public float Opacity = 1f;
            public string DiffuseTexture = string.Empty;
            public string NormalTexture = string.Empty;
            public string BumpTexture = string.Empty;
            public string SpecularTexture = string.Empty;
            public string EmissiveTexture = string.Empty;
            public string OpacityTexture = string.Empty;
        }

        private struct ObjFaceInternal
        {
            public int[] PositionIndices;
            public int[] TexCoordIndices;
            public int[] NormalIndices;
            public int MaterialIndex;
            public string GroupName;
            public bool HasTexCoords => TexCoordIndices != null;
            public bool HasNormals => NormalIndices != null;
        }

        // --- 内置OBJ解析器工具方法 ---

        private static string[] SplitWs(string s)
            => s.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

        private static float ParseFloat(string s)
            => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;

        private static Vector3 ParseColor3(string args)
        {
            var parts = SplitWs(args);
            return parts.Length >= 3
                ? new Vector3(ParseFloat(parts[0]), ParseFloat(parts[1]), ParseFloat(parts[2]))
                : Vector3.Zero;
        }

        private static int ParseObjIndex(string s, int count)
        {
            if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx))
                return -1;
            return idx < 0 ? count + idx + 1 : idx - 1;
        }

        private static int[] TriangulateBuiltIn(int vertexCount)
        {
            if (vertexCount == 3) return new[] { 0, 1, 2 };
            if (vertexCount == 4) return new[] { 0, 1, 2, 0, 2, 3 };

            var result = new int[(vertexCount - 2) * 3];
            int idx = 0;
            for (int i = 1; i < vertexCount - 1; i++)
            {
                result[idx++] = 0;
                result[idx++] = i;
                result[idx++] = i + 1;
            }
            return result;
        }

        private static long MakeVertexKeyBuiltIn(int posIdx, int texIdx, int normIdx)
        {
            return ((long)(posIdx & 0x1FFFFF) << 42) |
                   ((long)(texIdx & 0x1FFFFF) << 21) |
                   (long)(normIdx & 0x1FFFFF);
        }

        private static string ResolveTexPath(string args, string baseDir)
        {
            var parts = SplitWs(args);
            var pathParts = new List<string>();
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].StartsWith("-"))
                {
                    if (parts[i] == "-s" || parts[i] == "-o" || parts[i] == "-t")
                        i += 3;
                    continue;
                }
                pathParts.Add(parts[i]);
            }
            if (pathParts.Count == 0) return string.Empty;
            return Path.Combine(baseDir, string.Join(" ", pathParts));
        }

        /// <summary>
        /// 为 InterchangeMesh 生成法线 (面法线平均)。
        /// </summary>
        private static void GenerateNormalsForInterchangeMesh(InterchangeMesh mesh)
        {
            mesh.Normals = new List<Vector3>(mesh.Vertices.Count);
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

                Vector3 normal = Vector3.Cross(v1 - v0, v2 - v0);
                if (normal.LengthSquared() > 0.000001f)
                    normal = Vector3.Normalize(normal);

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
    }
}
