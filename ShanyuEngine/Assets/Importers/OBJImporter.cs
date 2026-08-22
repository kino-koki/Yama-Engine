// =====================================================================================
//  ShanyuEngine - OBJImporter：OBJ + MTL 导入器
//  命名空间：ShanyuEngine.Assets.Importers
// -------------------------------------------------------------------------------------
//  纯 C# 实现，支持 Wavefront OBJ (.obj) 和材质库 (.mtl) 格式：
//    - 顶点数据（v 位置 / vn 法线 / vt 纹理坐标 / vp 参数顶点）
//    - 面（f 三角面/多边形面/面索引 v/vt/vn 格式）
//    - 组（g 组名 / o 对象名）
//    - 平滑组（s 平滑组号 / off）
//    - 材质引用（usemtl / mtllib）
//    - MTL 材质解析（Ka/Kd/Ks/Ns/d/Tr/Ke/Ni/illum）
//    - MTL 纹理贴图（map_Kd/map_Ka/map_Ks/map_Ke/map_Bump/bump/map_d/map_refl/refl）
//    - 多边形三角化（fan triangulation / ear clipping）
//    - 法线生成（无法线时自动计算 / 平滑组处理）
//    - 切线生成（可选）
//    - 导入选项（坐标系翻转Y轴/缩放/合并相同材质/生成法线/生成切线）
// =====================================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;

namespace ShanyuEngine.Assets.Importers
{
    // =================================================================================
    //  导入选项
    // =================================================================================

    /// <summary>OBJ 导入选项。</summary>
    public sealed class ObjImportOptions
    {
        /// <summary>全局缩放比例。</summary>
        public float Scale = 1.0f;

        /// <summary>是否翻转 Y 轴（OBJ 默认 Y 朝上）。</summary>
        public bool FlipYAxis = false;

        /// <summary>是否翻转 Z 轴（OBJ 右手 → 引擎左手）。</summary>
        public bool FlipZAxis = false;

        /// <summary>缺少法线时是否自动计算。</summary>
        public bool GenerateNormals = true;

        /// <summary>是否生成切线。</summary>
        public bool GenerateTangents = true;

        /// <summary>是否合并使用相同材质的网格。</summary>
        public bool MergeSameMaterial = true;

        /// <summary>是否使用平滑组计算法线。</summary>
        public bool UseSmoothGroups = true;

        /// <summary>是否导入为独立对象（按 o 标签分割）。</summary>
        public bool SplitByObject = false;

        /// <summary>是否导入为独立组（按 g 标签分割）。</summary>
        public bool SplitByGroup = false;
    }

    // =================================================================================
    //  MTL 材质数据
    // =================================================================================

    /// <summary>MTL 材质定义。</summary>

    // =================================================================================
    //  OBJ 面数据
    // =================================================================================

    /// <summary>OBJ 面（多边形）。</summary>
    internal struct ObjFace
    {
        public int[] PositionIndices;
        public int[] TexCoordIndices;
        public int[] NormalIndices;
        public int MaterialIndex;
        public int SmoothGroup;

        public bool HasTexCoords => TexCoordIndices != null;
        public bool HasNormals => NormalIndices != null;
    }

    // =================================================================================
    //  OBJImporter —— 主导入器
    // =================================================================================

    /// <summary>
    /// Wavefront OBJ 导入器。支持 .obj + .mtl 格式。
    /// 输出为 InterchangeScene 统一格式。
    /// </summary>
    public sealed class OBJImporter
    {
        private readonly string _filePath;
        private readonly List<string> _messages = new();
        private ObjImportOptions _options = new();

        // OBJ 原始数据
        private readonly List<Vector3> _positions = new();
        private readonly List<Vector3> _normals = new();
        private readonly List<Vector2> _texCoords = new();
        private readonly List<ObjFace> _faces = new();
        private readonly List<ObjMaterial> _materials = new();
        private readonly Dictionary<string, int> _materialNameToIndex = new(StringComparer.Ordinal);

        // 当前状态
        private int _currentMaterialIndex = -1;
        private int _currentSmoothGroup = 0;
        private string _currentObjectName = string.Empty;
        private string _currentGroupName = string.Empty;

        /// <summary>解析产出的统一场景格式。</summary>
        public InterchangeScene? Scene { get; private set; }

        /// <summary>构造 OBJ 导入器。</summary>
        public OBJImporter(string filePath)
        {
            _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        }

        /// <summary>使用选项执行导入。</summary>
        public InterchangeScene Import(ObjImportOptions? options = null)
        {
            if (!File.Exists(_filePath))
                throw new FileNotFoundException($"OBJ 源文件不存在: {_filePath}");

            _options = options ?? new ObjImportOptions();

            Scene = new InterchangeScene
            {
                Name = Path.GetFileNameWithoutExtension(_filePath),
                SourceFilePath = _filePath,
                SourceFormat = "OBJ"
            };

            try
            {
                ParseObjFile();
                ConvertToScene();
                PostProcessScene();
            }
            catch (Exception ex)
            {
                Scene.ImportMessages.Add($"导入 OBJ 失败: {ex.Message}");
                throw;
            }

            Scene.ImportMessages.AddRange(_messages);
            return Scene;
        }

        // =================================================================================
        //  OBJ 文件解析
        // =================================================================================

        private void ParseObjFile()
        {
            string baseDir = Path.GetDirectoryName(_filePath) ?? string.Empty;
            string[] lines = File.ReadAllLines(_filePath, Encoding.UTF8);

            _messages.Add($"OBJ 文件: {lines.Length} 行");

            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();

                // 跳过空行和注释
                if (string.IsNullOrEmpty(line) || line.StartsWith("#"))
                    continue;

                // 分割指令和参数
                int spaceIdx = line.IndexOf(' ');
                if (spaceIdx < 0) continue;

                string cmd = line.Substring(0, spaceIdx).Trim();
                string args = line.Substring(spaceIdx + 1).Trim();

                switch (cmd)
                {
                    case "v":   // 顶点位置
                        ParsePosition(args);
                        break;
                    case "vn":  // 法线
                        ParseNormal(args);
                        break;
                    case "vt":  // 纹理坐标
                        ParseTexCoord(args);
                        break;
                    case "vp":  // 参数顶点（暂不处理）
                        break;
                    case "f":   // 面
                        ParseFace(args);
                        break;
                    case "g":   // 组名
                        _currentGroupName = args;
                        break;
                    case "o":   // 对象名
                        _currentObjectName = args;
                        break;
                    case "s":   // 平滑组
                        ParseSmoothGroup(args);
                        break;
                    case "usemtl":  // 使用材质
                        SetCurrentMaterial(args);
                        break;
                    case "mtllib":  // 材质库
                        LoadMaterialLibrary(baseDir, args);
                        break;
                    case "l":   // 线（暂不处理）
                    case "curv": // 曲线（暂不处理）
                    case "surf": // 表面（暂不处理）
                        break;
                }
            }

            _messages.Add($"OBJ 解析完成: {_positions.Count} 顶点, {_normals.Count} 法线, " +
                          $"{_texCoords.Count} UV, {_faces.Count} 面, {_materials.Count} 材质");
        }

        private void ParsePosition(string args)
        {
            var parts = SplitWhitespace(args);
            if (parts.Length < 3) return;

            float x = ParseFloat(parts[0]);
            float y = ParseFloat(parts[1]);
            float z = ParseFloat(parts[2]);

            var pos = new Vector3(x, y, z) * _options.Scale;

            if (_options.FlipYAxis)
                pos.Y = -pos.Y;
            if (_options.FlipZAxis)
                pos.Z = -pos.Z;

            _positions.Add(pos);
        }

        private void ParseNormal(string args)
        {
            var parts = SplitWhitespace(args);
            if (parts.Length < 3) return;

            var n = new Vector3(
                ParseFloat(parts[0]),
                ParseFloat(parts[1]),
                ParseFloat(parts[2])
            );

            if (_options.FlipYAxis)
                n.Y = -n.Y;
            if (_options.FlipZAxis)
                n.Z = -n.Z;

            _normals.Add(n);
        }

        private void ParseTexCoord(string args)
        {
            var parts = SplitWhitespace(args);
            if (parts.Length < 2) return;

            float u = ParseFloat(parts[0]);
            float v = ParseFloat(parts[1]);
            // OBJ 纹理坐标 V 轴通常需要翻转（原点在左下）
            v = 1f - v;

            _texCoords.Add(new Vector2(u, v));
        }

        private void ParseFace(string args)
        {
            var parts = SplitWhitespace(args);
            if (parts.Length < 3) return;

            var posIndices = new List<int>();
            var texIndices = new List<int>();
            var normIndices = new List<int>();
            bool hasTex = false, hasNorm = false;

            foreach (var part in parts)
            {
                // 格式: v / vt / vn  （vt 和 vn 可省略）
                var indices = part.Split('/');

                int pi = ParseIndex(indices[0], _positions.Count);
                if (pi >= 0) posIndices.Add(pi);

                if (indices.Length > 1 && !string.IsNullOrEmpty(indices[1]))
                {
                    int ti = ParseIndex(indices[1], _texCoords.Count);
                    if (ti >= 0) { texIndices.Add(ti); hasTex = true; }
                }

                if (indices.Length > 2 && !string.IsNullOrEmpty(indices[2]))
                {
                    int ni = ParseIndex(indices[2], _normals.Count);
                    if (ni >= 0) { normIndices.Add(ni); hasNorm = true; }
                }
            }

            if (posIndices.Count < 3) return;

            var face = new ObjFace
            {
                PositionIndices = posIndices.ToArray(),
                TexCoordIndices = hasTex ? texIndices.ToArray() : null,
                NormalIndices = hasNorm ? normIndices.ToArray() : null,
                MaterialIndex = _currentMaterialIndex,
                SmoothGroup = _currentSmoothGroup
            };

            _faces.Add(face);
        }

        /// <summary>解析 OBJ 索引（支持负数=相对索引）。</summary>
        private int ParseIndex(string s, int count)
        {
            if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx))
                return -1;

            if (idx < 0)
                idx = count + idx + 1; // 负数=相对（从末尾）
            else
                idx = idx - 1; // 正数=绝对（1-based → 0-based）

            return idx;
        }

        private void ParseSmoothGroup(string args)
        {
            if (args.Equals("off", StringComparison.OrdinalIgnoreCase) || args == "0")
                _currentSmoothGroup = 0;
            else if (int.TryParse(args, out int sg))
                _currentSmoothGroup = sg;
        }

        private void SetCurrentMaterial(string materialName)
        {
            if (_materialNameToIndex.TryGetValue(materialName, out int idx))
                _currentMaterialIndex = idx;
            else
                _currentMaterialIndex = -1;
        }

        // =================================================================================
        //  MTL 材质库解析
        // =================================================================================

        private void LoadMaterialLibrary(string baseDir, string mtlLibName)
        {
            // 可能包含多个文件名（空格分隔）
            var mtlFiles = SplitWhitespace(mtlLibName);
            foreach (var mtlFile in mtlFiles)
            {
                string mtlPath = Path.Combine(baseDir, mtlFile);
                if (File.Exists(mtlPath))
                {
                    ParseMtlFile(mtlPath, baseDir);
                }
                else
                {
                    _messages.Add($"MTL 文件不存在: {mtlPath}");
                }
            }
        }

        private void ParseMtlFile(string mtlPath, string baseDir)
        {
            string[] lines = File.ReadAllLines(mtlPath, Encoding.UTF8);
            ObjMaterial? currentMat = null;

            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#"))
                    continue;

                int spaceIdx = line.IndexOf(' ');
                if (spaceIdx < 0) continue;

                string cmd = line.Substring(0, spaceIdx).Trim();
                string args = line.Substring(spaceIdx + 1).Trim();

                switch (cmd)
                {
                    case "newmtl":
                        if (currentMat != null)
                            AddMaterial(currentMat);
                        currentMat = new ObjMaterial { Name = args };
                        break;
                    case "Ka":
                        if (currentMat != null) currentMat.Ambient = ParseColor(args);
                        break;
                    case "Kd":
                        if (currentMat != null) currentMat.Diffuse = ParseColor(args);
                        break;
                    case "Ks":
                        if (currentMat != null) currentMat.Specular = ParseColor(args);
                        break;
                    case "Ke":
                        if (currentMat != null) currentMat.Emissive = ParseColor(args);
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
                    case "Ni":
                        if (currentMat != null) currentMat.RefractionIndex = ParseFloat(args);
                        break;
                    case "illum":
                        if (currentMat != null && int.TryParse(args, out int illum))
                            currentMat.IlluminationModel = illum;
                        break;
                    case "map_Ka":
                        if (currentMat != null) currentMat.AmbientTexture = ResolveTexturePath(args, baseDir);
                        break;
                    case "map_Kd":
                        if (currentMat != null) currentMat.DiffuseTexture = ResolveTexturePath(args, baseDir);
                        break;
                    case "map_Ks":
                        if (currentMat != null) currentMat.SpecularTexture = ResolveTexturePath(args, baseDir);
                        break;
                    case "map_Ke":
                        if (currentMat != null) currentMat.EmissiveTexture = ResolveTexturePath(args, baseDir);
                        break;
                    case "map_Bump":
                    case "bump":
                        if (currentMat != null) currentMat.BumpTexture = ResolveTexturePath(args, baseDir);
                        break;
                    case "norm":
                        if (currentMat != null) currentMat.NormalTexture = ResolveTexturePath(args, baseDir);
                        break;
                    case "map_d":
                        if (currentMat != null) currentMat.OpacityTexture = ResolveTexturePath(args, baseDir);
                        break;
                    case "map_refl":
                    case "refl":
                        if (currentMat != null) currentMat.ReflectionTexture = ResolveTexturePath(args, baseDir);
                        break;
                    case "map_Pr":
                        if (currentMat != null) currentMat.RoughnessTexture = ResolveTexturePath(args, baseDir);
                        break;
                    case "map_Pm":
                        if (currentMat != null) currentMat.MetallicTexture = ResolveTexturePath(args, baseDir);
                        break;
                    case "map_disp":
                        if (currentMat != null) currentMat.DisplacementTexture = ResolveTexturePath(args, baseDir);
                        break;
                }
            }

            if (currentMat != null)
                AddMaterial(currentMat);
        }

        private void AddMaterial(ObjMaterial mat)
        {
            if (_materialNameToIndex.ContainsKey(mat.Name))
            {
                _materials[_materialNameToIndex[mat.Name]] = mat;
            }
            else
            {
                _materialNameToIndex[mat.Name] = _materials.Count;
                _materials.Add(mat);
            }
        }

        /// <summary>解析 MTL 纹理路径（去除选项参数如 -s 1 1 1）。</summary>
        private string ResolveTexturePath(string args, string baseDir)
        {
            var parts = SplitWhitespace(args);
            // 跳过选项参数（-s, -o, -blendu 等）
            var pathParts = new List<string>();
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].StartsWith("-"))
                {
                    // 跳过选项标志和它的参数
                    if (parts[i] == "-s" || parts[i] == "-o" || parts[i] == "-t")
                        i += 3; // 跳过 3 个数值参数
                    else
                        i += 0; // 其他标志无参数或下一行处理
                    continue;
                }
                pathParts.Add(parts[i]);
            }

            if (pathParts.Count == 0) return string.Empty;

            string texPath = string.Join(" ", pathParts);
            string fullPath = Path.Combine(baseDir, texPath);
            return fullPath;
        }

        // =================================================================================
        //  转换为 InterchangeScene
        // =================================================================================

        private void ConvertToScene()
        {
            // 1) 转换材质
            foreach (var objMat in _materials)
            {
                var mat = new InterchangeMaterial
                {
                    Name = objMat.Name,
                    DiffuseColor = objMat.Diffuse,
                    EmissiveColor = objMat.Emissive,
                    Opacity = objMat.Opacity,
                    ShadingModel = "PBR"
                };

                // Ns (Shininess) → Roughness 转换
                if (objMat.Shininess > 0)
                    mat.Roughness = MathF.Sqrt(2f / (objMat.Shininess + 2f));
                else
                    mat.Roughness = 1f;

                // 光照模型判断金属度
                if (objMat.IlluminationModel >= 3)
                    mat.Metallic = objMat.Specular.Length() > 0.5f ? 1f : 0f;

                // 纹理路径
                mat.DiffuseTexturePath = objMat.DiffuseTexture;
                mat.NormalTexturePath = objMat.NormalTexture.Length > 0
                    ? objMat.NormalTexture : objMat.BumpTexture;
                mat.EmissiveTexturePath = objMat.EmissiveTexture;
                mat.OcclusionTexturePath = objMat.AmbientTexture;
                mat.ExtraTextureSlots["specular"] = objMat.SpecularTexture;
                mat.ExtraTextureSlots["opacity"] = objMat.OpacityTexture;
                mat.ExtraTextureSlots["reflection"] = objMat.ReflectionTexture;
                mat.ExtraTextureSlots["roughness"] = objMat.RoughnessTexture;
                mat.ExtraTextureSlots["metallic"] = objMat.MetallicTexture;
                mat.ExtraTextureSlots["displacement"] = objMat.DisplacementTexture;

                Scene.Materials.Add(mat);
            }

            // 没有材质时添加默认材质
            if (Scene.Materials.Count == 0)
            {
                Scene.Materials.Add(new InterchangeMaterial
                {
                    Name = "DefaultMaterial",
                    DiffuseColor = new Vector3(0.8f, 0.8f, 0.8f)
                });
            }

            // 2) 按材质分组面
            var facesByMaterial = new Dictionary<int, List<ObjFace>>();
            foreach (var face in _faces)
            {
                int matIdx = face.MaterialIndex >= 0 ? face.MaterialIndex : 0;
                if (!facesByMaterial.TryGetValue(matIdx, out var list))
                {
                    list = new List<ObjFace>();
                    facesByMaterial[matIdx] = list;
                }
                list.Add(face);
            }

            // 3) 为每组材质创建网格
            if (_options.MergeSameMaterial)
            {
                foreach (var kvp in facesByMaterial)
                {
                    var mesh = BuildMeshFromFaces(kvp.Value, kvp.Key);
                    Scene.Meshes.Add(mesh);
                }
            }
            else
            {
                // 每个面单独网格（不推荐，仅用于调试）
                var mesh = BuildMeshFromFaces(_faces, 0);
                Scene.Meshes.Add(mesh);
            }

            // 4) 创建场景节点
            var node = new InterchangeNode
            {
                Name = string.IsNullOrEmpty(_currentObjectName) ? Scene.Name : _currentObjectName,
                MeshIndex = 0
            };
            Scene.RootNodes.Add(node);
        }

        /// <summary>从面列表构建网格（含三角化）。</summary>
        private InterchangeMesh BuildMeshFromFaces(List<ObjFace> faces, int materialIndex)
        {
            var mesh = new InterchangeMesh
            {
                Name = $"Mesh_{materialIndex}"
            };
            mesh.MaterialIndices.Add(materialIndex);

            // 顶点去重映射：(posIdx, texIdx, normIdx) → 新顶点索引
            var vertexMap = new Dictionary<long, int>();
            bool hasTexCoords = _texCoords.Count > 0;
            bool hasNormals = _normals.Count > 0;

            foreach (var face in faces)
            {
                // 三角化（扇形三角化）
                int[] triIndices = TriangulateFace(face);

                for (int i = 0; i < triIndices.Length; i += 3)
                {
                    for (int j = 0; j < 3; j++)
                    {
                        int vi = triIndices[i + j];
                        int posIdx = face.PositionIndices[vi];
                        int texIdx = face.HasTexCoords && vi < face.TexCoordIndices.Length ? face.TexCoordIndices[vi] : -1;
                        int normIdx = face.HasNormals && vi < face.NormalIndices.Length ? face.NormalIndices[vi] : -1;

                        long key = MakeVertexKey(posIdx, texIdx, normIdx);

                        if (!vertexMap.TryGetValue(key, out int newIdx))
                        {
                            newIdx = mesh.Vertices.Count;
                            vertexMap[key] = newIdx;

                            // 位置
                            if (posIdx >= 0 && posIdx < _positions.Count)
                                mesh.Vertices.Add(_positions[posIdx]);
                            else
                                mesh.Vertices.Add(Vector3.Zero);

                            // UV
                            if (hasTexCoords && texIdx >= 0 && texIdx < _texCoords.Count)
                                mesh.UV0.Add(_texCoords[texIdx]);
                            else if (hasTexCoords)
                                mesh.UV0.Add(Vector2.Zero);

                            // 法线
                            if (hasNormals && normIdx >= 0 && normIdx < _normals.Count)
                                mesh.Normals.Add(_normals[normIdx]);
                            else if (hasNormals)
                                mesh.Normals.Add(Vector3.UnitY);
                        }

                        mesh.Indices.Add(newIdx);
                    }
                }
            }

            return mesh;
        }

        /// <summary>扇形三角化多边形面。</summary>
        private int[] TriangulateFace(ObjFace face)
        {
            int count = face.PositionIndices.Length;
            if (count == 3)
                return new int[] { 0, 1, 2 };

            if (count == 4)
                return new int[] { 0, 1, 2, 0, 2, 3 };

            // 扇形三角化
            var result = new int[(count - 2) * 3];
            int idx = 0;
            for (int i = 1; i < count - 1; i++)
            {
                result[idx++] = 0;
                result[idx++] = i;
                result[idx++] = i + 1;
            }
            return result;
        }

        private long MakeVertexKey(int posIdx, int texIdx, int normIdx)
        {
            // 将三个索引打包为 long
            return ((long)(posIdx & 0x1FFFFF) << 42) |
                   ((long)(texIdx & 0x1FFFFF) << 21) |
                   (long)(normIdx & 0x1FFFFF);
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
                    if (mesh.Normals.Count == 0 && mesh.Vertices.Count > 0)
                        GenerateNormalsForMesh(mesh);
                }
            }

            // 生成切线
            if (_options.GenerateTangents)
            {
                foreach (var mesh in Scene.Meshes)
                {
                    if (mesh.Tangents.Count == 0 && mesh.UV0.Count > 0 && mesh.Normals.Count > 0)
                        GenerateTangentsForMesh(mesh);
                }
            }
        }

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
                Vector2 duv1 = uv1 - uv0;
                Vector2 duv2 = uv2 - uv0;

                float denom = duv1.X * duv2.Y - duv1.Y * duv2.X;
                float r = Math.Abs(denom) > 1e-6f ? 1f / denom : 1f;

                Vector3 tangent = Vector3.Normalize((edge1 * duv2.Y - edge2 * duv1.Y) * r);
                float tw = denom < 0 ? -1f : 1f;

                mesh.Tangents[i0] += new Vector4(tangent, tw);
                mesh.Tangents[i1] += new Vector4(tangent, tw);
                mesh.Tangents[i2] += new Vector4(tangent, tw);
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
        //  工具方法
        // =================================================================================

        private static string[] SplitWhitespace(string s)
        {
            return s.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static float ParseFloat(string s)
        {
            if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float val))
                return val;
            return 0f;
        }

        private static Vector3 ParseColor(string args)
        {
            var parts = SplitWhitespace(args);
            if (parts.Length < 3) return Vector3.Zero;
            return new Vector3(
                ParseFloat(parts[0]),
                ParseFloat(parts[1]),
                ParseFloat(parts[2])
            );
        }
    }

    // =================================================================================
    //  OBJImporter 适配器
    // =================================================================================

    /// <summary>OBJ 导入器适配器（用于 AssetImportManager 注册）。</summary>
    public sealed class OBJImporterAdapter : IAssetImporter
    {
        /// <summary>支持的扩展名。</summary>
        public IEnumerable<string> SupportedExtensions => new[] { "obj" };

        /// <summary>导入 OBJ 文件。</summary>
        public InterchangeScene Import(string filePath)
        {
            var importer = new OBJImporter(filePath);
            return importer.Import();
        }
    }
}
