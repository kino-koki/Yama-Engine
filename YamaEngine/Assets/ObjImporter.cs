using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;

namespace YamaEngine.Assets
{
    // ====================================================================
    // OBJ Importer — Wavefront OBJ 文本格式解析
    // ====================================================================
    // 解析 .obj 文件中的顶点(v)、纹理坐标(vt)、法线(vn)、面(f) 数据。
    // 支持简单 .mtl 材质文件解析（漫反射颜色/纹理、金属度、粗糙度）。
    // 自动将多边形面三角化，展开为非索引顶点缓冲（适应 OBJ 引用模式）。
    // ====================================================================

    // ──────────────────────────────────────────────────────────────
    // OBJ 面索引（顶点/UV/法线，1-based 索引）
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// OBJ 面顶点索引。OBJ 使用 1-based 索引，支持 v, v/vt, v/vt/vn, v//vn 格式。
    /// </summary>
    public readonly struct ObjFaceVertex : IEquatable<ObjFaceVertex>
    {
        public readonly int PositionIndex;   // 1-based
        public readonly int TexCoordIndex;   // 1-based, 0 = 未指定
        public readonly int NormalIndex;     // 1-based, 0 = 未指定

        public ObjFaceVertex(int position, int texCoord, int normal)
        {
            PositionIndex = position;
            TexCoordIndex = texCoord;
            NormalIndex = normal;
        }

        public bool Equals(ObjFaceVertex other)
            => PositionIndex == other.PositionIndex
            && TexCoordIndex == other.TexCoordIndex
            && NormalIndex == other.NormalIndex;

        public override bool Equals(object obj) => obj is ObjFaceVertex o && Equals(o);
        public override int GetHashCode() => HashCode.Combine(PositionIndex, TexCoordIndex, NormalIndex);
        public static bool operator ==(ObjFaceVertex a, ObjFaceVertex b) => a.Equals(b);
        public static bool operator !=(ObjFaceVertex a, ObjFaceVertex b) => !a.Equals(b);
    }

    // ──────────────────────────────────────────────────────────────
    // OBJ 对象/组（一个 obj 文件可含多个 mesh）
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// OBJ 文件中的单个对象或组（对应一个子网格）。
    /// </summary>
    public sealed class ObjObject
    {
        /// <summary>对象/组名称。</summary>
        public string Name { get; set; } = "Default";

        /// <summary>使用的材质名称。</summary>
        public string MaterialName { get; set; } = string.Empty;

        /// <summary>展开后的顶点位置数组（每3个float为一个顶点）。</summary>
        public float[] Positions { get; set; } = Array.Empty<float>();

        /// <summary>展开后的法线数组（每3个float为一个法线，可能为空）。</summary>
        public float[] Normals { get; set; } = Array.Empty<float>();

        /// <summary>展开后的纹理坐标数组（每2个float为一个UV，可能为空）。</summary>
        public float[] TexCoords { get; set; } = Array.Empty<float>();

        /// <summary>展开后的索引数组（所有面三角化后的顺序索引）。</summary>
        public uint[] Indices { get; set; } = Array.Empty<uint>();

        /// <summary>唯一顶点数量。</summary>
        public int VertexCount => Positions.Length / 3;

        /// <summary>三角形数量。</summary>
        public int TriangleCount => Indices.Length / 3;

        /// <summary>索引数量。</summary>
        public int IndexCount => Indices.Length;

        /// <summary>关联材质数据。</summary>
        public ObjMaterial Material { get; set; } = new();
    }

    // ──────────────────────────────────────────────────────────────
    // OBJ 材质 (.mtl)
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// OBJ 材质数据（从 .mtl 文件解析）。
    /// </summary>
    public sealed class ObjMaterial
    {
        /// <summary>材质名称。</summary>
        public string Name { get; set; } = "Default";

        /// <summary>漫反射颜色 (Kd)。</summary>
        public Vector3 DiffuseColor { get; set; } = new Vector3(0.8f, 0.8f, 0.8f);

        /// <summary>环境光颜色 (Ka)。</summary>
        public Vector3 AmbientColor { get; set; } = new Vector3(0f, 0f, 0f);

        /// <summary>高光颜色 (Ks)。</summary>
        public Vector3 SpecularColor { get; set; } = new Vector3(0.5f, 0.5f, 0.5f);

        /// <summary>发射颜色 (Ke)。</summary>
        public Vector3 EmissiveColor { get; set; } = Vector3.Zero;

        /// <summary>透射颜色 (Tf)。</summary>
        public Vector3 TransmissionFilter { get; set; } = new Vector3(1f, 1f, 1f);

        /// <summary>高光指数 (Ns)。</summary>
        public float SpecularExponent { get; set; } = 32f;

        /// <summary>透明度 (d) 或溶解度 (Tr = 1 - d)。</summary>
        public float Dissolve { get; set; } = 1f;

        /// <summary>光学密度 / 折射率 (Ni)。</summary>
        public float OpticalDensity { get; set; } = 1f;

        /// <summary>粗糙度 (PBR 扩展: Pr)。</summary>
        public float Roughness { get; set; } = 0.5f;

        /// <summary>金属度 (PBR 扩展: Pm)。</summary>
        public float Metallic { get; set; } = 0f;

        /// <summary>漫反射贴图路径 (map_Kd)。</summary>
        public string? DiffuseMap { get; set; }

        /// <summary>法线贴图路径 (map_bump / bump)。</summary>
        public string? NormalMap { get; set; }

        /// <summary>高光贴图路径 (map_Ks)。</summary>
        public string? SpecularMap { get; set; }

        /// <summary>粗糙度贴图路径 (map_Pr / map_roughness)。</summary>
        public string? RoughnessMap { get; set; }

        /// <summary>金属度贴图路径 (map_Pm / map_metallic)。</summary>
        public string? MetallicMap { get; set; }

        /// <summary>Alpha 贴图路径 (map_d)。</summary>
        public string? AlphaMap { get; set; }

        /// <summary>是否双面渲染。</summary>
        public bool DoubleSided { get; set; }

        /// <summary>光照模型类型（1=Constant, 2=Lambert, 3=Blinn-Phong, 4=PBR）。</summary>
        public int IlluminationModel { get; set; } = 2;

        // 以下成员用于各导入器（OBJ/MTL/FBX 文本/glTF）统一读取材质属性：
        // 颜色位置均用 Vector3，透明度/高光指数等用 float，贴图均为路径字符串。

        /// <summary>漫反射颜色 (Kd)。</summary>
        public Vector3 Diffuse { get; set; } = new Vector3(0.8f, 0.8f, 0.8f);
        /// <summary>环境光颜色 (Ka)。</summary>
        public Vector3 Ambient { get; set; } = new Vector3(0f, 0f, 0f);
        /// <summary>高光颜色 (Ks)。</summary>
        public Vector3 Specular { get; set; } = new Vector3(0.5f, 0.5f, 0.5f);
        /// <summary>发射颜色 (Ke)。</summary>
        public Vector3 Emissive { get; set; } = Vector3.Zero;

        /// <summary>透明度 [0,1]，1 为不透明。</summary>
        public float Opacity { get; set; } = 1f;
        /// <summary>高光指数 (Ns)。</summary>
        public float Shininess { get; set; } = 32f;
        /// <summary>折射率 (Ni)。</summary>
        public float RefractionIndex { get; set; } = 1f;

        /// <summary>漫反射贴图路径 (map_Kd)。</summary>
        public string? DiffuseTexture { get; set; }
        /// <summary>漫反射贴图路径（同 DiffuseTexture 的别名，供旧调用方使用）。</summary>
        public string? DiffuseTexturePath { get; set; }
        /// <summary>环境光/遮挡贴图路径 (map_Ka)。</summary>
        public string? AmbientTexture { get; set; }
        /// <summary>高光贴图路径 (map_Ks)。</summary>
        public string? SpecularTexture { get; set; }
        /// <summary>高光贴图路径（同 SpecularTexture 别名）。</summary>
        public string? SpecularTexturePath { get; set; }
        /// <summary>发射贴图路径 (map_Ke)。</summary>
        public string? EmissiveTexture { get; set; }
        /// <summary>法线贴图路径 (map_Bump)。</summary>
        public string? NormalTexture { get; set; }
        /// <summary>法线贴图路径（同 NormalTexture 别名）。</summary>
        public string? NormalTexturePath { get; set; }
        /// <summary>凹凸贴图路径 (bump)。</summary>
        public string? BumpTexture { get; set; }
        /// <summary>透明贴图路径 (map_d)。</summary>
        public string? OpacityTexture { get; set; }
        /// <summary>反射贴图路径 (map_Ka/reflect)。</summary>
        public string? ReflectionTexture { get; set; }
        /// <summary>粗糙度贴图路径 (map_Pr)。</summary>
        public string? RoughnessTexture { get; set; }
        /// <summary>金属度贴图路径 (map_Pm)。</summary>
        public string? MetallicTexture { get; set; }
        /// <summary>置换贴图路径 (disp)。</summary>
        public string? DisplacementTexture { get; set; }

        // 将材质数据转为引擎 LoadedMaterial（便于与 glTF 导入器统一使用）
        public LoadedMaterial ToLoadedMaterial()
        {
            return new LoadedMaterial
            {
                Name = Name,
                BaseColorFactor = new Vector4(DiffuseColor.X, DiffuseColor.Y, DiffuseColor.Z, Dissolve),
                MetallicFactor = Metallic,
                RoughnessFactor = Roughness,
                EmissiveFactor = EmissiveColor,
                DoubleSided = DoubleSided,
                AlphaMode = Dissolve < 1f ? "BLEND" : "OPAQUE",
                AlphaCutoff = 0.5f,
                BaseColorTexturePath = DiffuseMap,
                NormalTexturePath = NormalMap,
                MetallicRoughnessTexturePath = null // OBJ 不支持合并的 ORM 纹理
            };
        }
    }

    // ──────────────────────────────────────────────────────────────
    // OBJ 加载结果
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// OBJ 加载后的完整模型数据。
    /// </summary>
    public sealed class LoadedObjModel
    {
        /// <summary>模型名称。</summary>
        public string Name { get; set; } = "ObjModel";

        /// <summary>子对象/组列表（每个 object/group 对应一个子网格）。</summary>
        public List<ObjObject> Objects { get; set; } = new();

        /// <summary>解析出的所有材质。</summary>
        public Dictionary<string, ObjMaterial> Materials { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>总顶点数。</summary>
        public int TotalVertexCount
        {
            get
            {
                int count = 0;
                foreach (var obj in Objects) count += obj.VertexCount;
                return count;
            }
        }

        /// <summary>总三角形数。</summary>
        public int TotalTriangleCount
        {
            get
            {
                int count = 0;
                foreach (var obj in Objects) count += obj.TriangleCount;
                return count;
            }
        }

        /// <summary>转换为引擎统一的 LoadedModel。</summary>
        public LoadedModel ToLoadedModel()
        {
            var model = new LoadedModel
            {
                Name = Name
            };

            foreach (var mat in Materials.Values)
                model.Materials.Add(mat.ToLoadedMaterial());

            foreach (var obj in Objects)
            {
                var subMesh = new ModelSubMesh
                {
                    Name = obj.Name,
                    Positions = obj.Positions,
                    Normals = obj.Normals.Length > 0 ? obj.Normals : null,
                    TexCoords0 = obj.TexCoords.Length > 0 ? obj.TexCoords : null,
                    Indices = obj.Indices,
                    Use32BitIndices = obj.VertexCount > 65535,
                    Material = obj.Material.ToLoadedMaterial()
                };
                model.SubMeshes.Add(subMesh);
            }

            return model;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // ObjLoader — 核心 OBJ 解析器
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Wavefront OBJ 文件加载器。支持 .obj/.mtl 解析、多边形三角化、
    /// 索引展开为非索引顶点（因 OBJ 的 v/vt/vn 引用组合不固定）。
    /// </summary>
    public static class ObjLoader
    {
        // ── 公开 API ──

        /// <summary>
        /// 从文件加载 OBJ 模型。
        /// </summary>
        public static LoadedObjModel Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path is required.", nameof(path));
            if (!File.Exists(path))
                throw new FileNotFoundException($"OBJ file not found: {path}", path);

            var model = new LoadedObjModel
            {
                Name = Path.GetFileNameWithoutExtension(path)
            };

            // 解析 OBJ
            var ctx = new ParseContext(model);
            ParseObjFile(path, ctx);

            return model;
        }

        /// <summary>仅解析 .mtl 材质文件。</summary>
        public static Dictionary<string, ObjMaterial> LoadMtl(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path is required.", nameof(path));
            if (!File.Exists(path))
                throw new FileNotFoundException($"MTL file not found: {path}", path);

            var materials = new Dictionary<string, ObjMaterial>(StringComparer.OrdinalIgnoreCase);
            ParseMtlFile(path, materials);
            return materials;
        }

        // ── OBJ 解析 ──

        private static void ParseObjFile(string path, ParseContext ctx)
        {
            string? basePath = Path.GetDirectoryName(Path.GetFullPath(path));

            using var reader = new StreamReader(path, Encoding.UTF8);
            string line;
            int lineNumber = 0;

            while ((line = reader.ReadLine()) != null)
            {
                lineNumber++;
                line = line.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#"))
                    continue;

                // 移除行内注释
                int commentIdx = line.IndexOf('#');
                if (commentIdx >= 0)
                    line = line.Substring(0, commentIdx).TrimEnd();

                if (line.Length == 0) continue;

                string[] parts = SplitObjLine(line);
                if (parts.Length == 0) continue;

                switch (parts[0].ToLowerInvariant())
                {
                    case "v":
                        ParseVertex(parts, ctx);
                        break;
                    case "vt":
                        ParseTexCoord(parts, ctx);
                        break;
                    case "vn":
                        ParseNormal(parts, ctx);
                        break;
                    case "f":
                    case "fo": // face with texture offset (treated as f)
                        ParseFace(parts, ctx);
                        break;
                    case "o":
                        FlushCurrentObject(ctx);
                        ctx.CurrentObjectName = parts.Length > 1 ? parts[1] : "Object";
                        break;
                    case "g":
                        FlushCurrentObject(ctx);
                        ctx.CurrentGroupName = parts.Length > 1 ? parts[1] : "Group";
                        break;
                    case "usemtl":
                        FlushCurrentObject(ctx);
                        ctx.CurrentMaterialName = parts.Length > 1 ? parts[1] : string.Empty;
                        break;
                    case "mtllib":
                        if (parts.Length > 1 && basePath != null)
                        {
                            string mtlPath = Path.Combine(basePath, parts[1]);
                            if (File.Exists(mtlPath))
                                ParseMtlFile(mtlPath, ctx.Model.Materials);
                        }
                        break;
                    case "s":
                        // smooth shading on/off -- 忽略（引擎自行决定法线生成方式）
                        break;
                }
            }

            // 刷新最后一个对象
            FlushCurrentObject(ctx);
        }

        /// <summary>
        /// 按空格分割 OBJ 行，保留引号内的空格不分割。
        /// </summary>
        private static string[] SplitObjLine(string line)
        {
            var result = new List<string>();
            int i = 0;
            while (i < line.Length)
            {
                while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
                if (i >= line.Length) break;

                int start = i;
                while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
                result.Add(line.Substring(start, i - start));
            }
            return result.ToArray();
        }

        private static void ParseVertex(string[] parts, ParseContext ctx)
        {
            if (parts.Length < 4) return;
            if (!TryParseFloat(parts[1], out float x)) return;
            if (!TryParseFloat(parts[2], out float y)) return;
            if (!TryParseFloat(parts[3], out float z)) return;
            float w = parts.Length >= 5 && TryParseFloat(parts[4], out float ww) ? ww : 1f;

            ctx.Positions.Add(new Vector3(x / w, y / w, z / w));
        }

        private static void ParseTexCoord(string[] parts, ParseContext ctx)
        {
            if (parts.Length < 3) return;
            if (!TryParseFloat(parts[1], out float u)) return;
            if (!TryParseFloat(parts[2], out float v)) return;
            float w = parts.Length >= 4 && TryParseFloat(parts[3], out float ww) ? ww : 0f;

            // OBJ 的 V 是从下到上（0=底,1=顶），OpenGL 风格
            ctx.TexCoords.Add(new Vector3(u, v, w));
        }

        private static void ParseNormal(string[] parts, ParseContext ctx)
        {
            if (parts.Length < 4) return;
            if (!TryParseFloat(parts[1], out float nx)) return;
            if (!TryParseFloat(parts[2], out float ny)) return;
            if (!TryParseFloat(parts[3], out float nz)) return;

            ctx.Normals.Add(new Vector3(nx, ny, nz));
        }

        private static void ParseFace(string[] parts, ParseContext ctx)
        {
            if (parts.Length < 4) return;

            // 解析面顶点
            var faceVertices = new List<ObjFaceVertex>();
            for (int i = 1; i < parts.Length; i++)
            {
                if (TryParseFaceVertex(parts[i], out var fv))
                    faceVertices.Add(fv);
            }

            if (faceVertices.Count < 3) return;

            // 三角化（Fan Triangulation）：将 N 边形分解为 (N-2) 个三角形
            for (int i = 1; i < faceVertices.Count - 1; i++)
            {
                ctx.FaceTriangles.Add(faceVertices[0]);
                ctx.FaceTriangles.Add(faceVertices[i]);
                ctx.FaceTriangles.Add(faceVertices[i + 1]);
            }
        }

        /// <summary>
        /// 解析 OBJ 面顶点引用。支持格式：
        ///   v              -> (v, 0, 0)
        ///   v/vt           -> (v, vt, 0)
        ///   v/vt/vn        -> (v, vt, vn)
        ///   v//vn          -> (v, 0, vn)
        /// 负数索引表示从末尾倒数。
        /// </summary>
        private static bool TryParseFaceVertex(string token, out ObjFaceVertex result)
        {
            result = default;

            if (string.IsNullOrWhiteSpace(token))
                return false;

            string[] components = token.Split('/');
            if (components.Length < 1 || components.Length > 3)
                return false;

            if (!TryParseObjIndex(components[0], 0, out int vIdx))
                return false;

            int vtIdx = 0;
            if (components.Length >= 2 && components[1].Length > 0)
                TryParseObjIndex(components[1], 0, out vtIdx);

            int vnIdx = 0;
            if (components.Length >= 3 && components[2].Length > 0)
                TryParseObjIndex(components[2], 0, out vnIdx);

            result = new ObjFaceVertex(vIdx, vtIdx, vnIdx);
            return true;
        }

        /// <summary>
        /// 解析 OBJ 索引（1-based，支持负数）。解析后转为 0-based 正索引。
        /// </summary>
        private static bool TryParseObjIndex(string s, int count, out int index)
        {
            index = 0;
            if (!TryParseInt(s, out int raw))
                return false;

            // 负数：从末尾倒数
            if (raw < 0)
                index = count + raw + 1; // +1 因为 raw 是 -1 表示最后一个
            else
                index = raw;

            return index >= 0;
        }

        // ── MTL 解析 ──

        private static void ParseMtlFile(string path, Dictionary<string, ObjMaterial> materials)
        {
            using var reader = new StreamReader(path, Encoding.UTF8);
            string line;
            ObjMaterial? current = null;
            string? basePath = Path.GetDirectoryName(Path.GetFullPath(path));

            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;

                int commentIdx = line.IndexOf('#');
                if (commentIdx >= 0)
                    line = line.Substring(0, commentIdx).TrimEnd();

                if (line.Length == 0) continue;

                string[] parts = SplitObjLine(line);
                if (parts.Length == 0) continue;

                switch (parts[0].ToLowerInvariant())
                {
                    case "newmtl":
                        current = new ObjMaterial { Name = parts.Length > 1 ? parts[1] : "Unnamed" };
                        materials[current.Name] = current;
                        break;

                    case "kd": // 漫反射颜色
                        if (current != null && parts.Length >= 4)
                        {
                            current.DiffuseColor = ParseVec3(parts[1], parts[2], parts[3], current.DiffuseColor);
                        }
                        break;

                    case "ka": // 环境光颜色
                        if (current != null && parts.Length >= 4)
                        {
                            current.AmbientColor = ParseVec3(parts[1], parts[2], parts[3], current.AmbientColor);
                        }
                        break;

                    case "ks": // 高光颜色
                        if (current != null && parts.Length >= 4)
                        {
                            current.SpecularColor = ParseVec3(parts[1], parts[2], parts[3], current.SpecularColor);
                        }
                        break;

                    case "ke": // 自发光颜色
                        if (current != null && parts.Length >= 4)
                        {
                            current.EmissiveColor = ParseVec3(parts[1], parts[2], parts[3], current.EmissiveColor);
                        }
                        break;

                    case "tf": // 透射颜色
                        if (current != null && parts.Length >= 4)
                        {
                            current.TransmissionFilter = ParseVec3(parts[1], parts[2], parts[3], current.TransmissionFilter);
                        }
                        break;

                    case "ns": // 高光指数
                        if (current != null && parts.Length > 1)
                        {
                            TryParseFloat(parts[1], out var specExp);
                            current.SpecularExponent = specExp;
                        }
                        break;

                    case "ni": // 折射率
                        if (current != null && parts.Length > 1)
                        {
                            TryParseFloat(parts[1], out var optDensity);
                            current.OpticalDensity = optDensity;
                        }
                        break;

                    case "d": // 透明度
                        if (current != null && parts.Length > 1)
                        {
                            TryParseFloat(parts[1], out var dissolve);
                            current.Dissolve = dissolve;
                        }
                        break;

                    case "tr": // 透明度（反向）
                        if (current != null && parts.Length > 1)
                        {
                            if (TryParseFloat(parts[1], out float tr))
                                current.Dissolve = 1f - tr;
                        }
                        break;

                    case "illum": // 光照模型
                        if (current != null && parts.Length > 1)
                        {
                            TryParseInt(parts[1], out var illum);
                            current.IlluminationModel = illum;
                        }
                        break;

                    case "pr": // PBR 粗糙度
                        if (current != null && parts.Length > 1)
                        {
                            TryParseFloat(parts[1], out var rough);
                            current.Roughness = rough;
                        }
                        break;

                    case "pm": // PBR 金属度
                        if (current != null && parts.Length > 1)
                        {
                            TryParseFloat(parts[1], out var metal);
                            current.Metallic = metal;
                        }
                        break;

                    case "map_kd": // 漫反射贴图
                        if (current != null && parts.Length > 1 && basePath != null)
                            current.DiffuseMap = ResolveTexturePath(parts, basePath);
                        break;

                    case "map_ks": // 高光贴图
                        if (current != null && parts.Length > 1 && basePath != null)
                            current.SpecularMap = ResolveTexturePath(parts, basePath);
                        break;

                    case "map_bump":
                    case "bump": // 法线贴图
                        if (current != null && parts.Length > 1 && basePath != null)
                            current.NormalMap = ResolveTexturePath(parts, basePath);
                        break;

                    case "map_d": // Alpha 贴图
                        if (current != null && parts.Length > 1 && basePath != null)
                            current.AlphaMap = ResolveTexturePath(parts, basePath);
                        break;

                    case "map_pr":
                    case "map_roughness": // 粗糙度贴图
                        if (current != null && parts.Length > 1 && basePath != null)
                            current.RoughnessMap = ResolveTexturePath(parts, basePath);
                        break;

                    case "map_pm":
                    case "map_metallic": // 金属度贴图
                        if (current != null && parts.Length > 1 && basePath != null)
                            current.MetallicMap = ResolveTexturePath(parts, basePath);
                        break;
                }
            }
        }

        /// <summary>
        /// 解析贴图路径。OBJ 贴图行可能包含 -bm（混合系数）等选项，
        /// 文件路径可能在中间（-o/-s 等选项之间）或行尾。
        /// </summary>
        private static string? ResolveTexturePath(string[] parts, string basePath)
        {
            // 简单策略：取最后一个非选项参数作为文件路径
            // 跳过 -bm, -o, -s, -mm, -t 等选项
            for (int i = parts.Length - 1; i >= 1; i--)
            {
                if (!parts[i].StartsWith("-"))
                    return Path.Combine(basePath, parts[i]);
            }
            return null;
        }

        private static Vector3 ParseVec3(string s1, string s2, string s3, Vector3 fallback)
        {
            if (TryParseFloat(s1, out float x) && TryParseFloat(s2, out float y) && TryParseFloat(s3, out float z))
                return new Vector3(x, y, z);
            return fallback;
        }

        // ── 对象组装（索引展开）──

        /// <summary>
        /// 将当前累积的面数据刷新为一个 ObjObject，添加到模型中。
        /// OBJ 的面引用使用 (v/vt/vn) 组合，必须展开为唯一顶点。
        /// </summary>
        private static void FlushCurrentObject(ParseContext ctx)
        {
            if (ctx.FaceTriangles.Count == 0) return;

            var obj = new ObjObject
            {
                Name = ctx.CurrentObjectName ?? ctx.CurrentGroupName ?? "Object",
                MaterialName = ctx.CurrentMaterialName
            };

            // 查找材质
            if (!string.IsNullOrEmpty(obj.MaterialName)
                && ctx.Model.Materials.TryGetValue(obj.MaterialName, out var material))
            {
                obj.Material = material;
            }

            // 展开索引：为每个唯一的 (v/vt/vn) 组合创建一个顶点
            var vertexMap = new Dictionary<ObjFaceVertex, uint>();
            var positions = new List<float>();
            var normals = new List<float>();
            var texCoords = new List<float>();
            var indices = new List<uint>();
            uint nextIndex = 0;

            foreach (var fv in ctx.FaceTriangles)
            {
                if (vertexMap.TryGetValue(fv, out uint idx))
                {
                    indices.Add(idx);
                }
                else
                {
                    idx = nextIndex++;
                    vertexMap[fv] = idx;
                    indices.Add(idx);

                    // 顶点位置（OBJ 1-based → 0-based）
                    int pi = fv.PositionIndex - 1;
                    if (pi >= 0 && pi < ctx.Positions.Count)
                    {
                        positions.Add(ctx.Positions[pi].X);
                        positions.Add(ctx.Positions[pi].Y);
                        positions.Add(ctx.Positions[pi].Z);
                    }
                    else
                    {
                        positions.Add(0f);
                        positions.Add(0f);
                        positions.Add(0f);
                    }

                    // 法线
                    int ni = fv.NormalIndex - 1;
                    if (ni >= 0 && ni < ctx.Normals.Count)
                    {
                        normals.Add(ctx.Normals[ni].X);
                        normals.Add(ctx.Normals[ni].Y);
                        normals.Add(ctx.Normals[ni].Z);
                    }
                    else
                    {
                        normals.Add(0f);
                        normals.Add(0f);
                        normals.Add(0f);
                    }

                    // 纹理坐标
                    int ti = fv.TexCoordIndex - 1;
                    if (ti >= 0 && ti < ctx.TexCoords.Count)
                    {
                        texCoords.Add(ctx.TexCoords[ti].X);
                        texCoords.Add(ctx.TexCoords[ti].Y);
                    }
                    else
                    {
                        texCoords.Add(0f);
                        texCoords.Add(0f);
                    }
                }
            }

            obj.Positions = positions.ToArray();
            obj.Normals = normals.ToArray();
            obj.TexCoords = texCoords.ToArray();
            obj.Indices = indices.ToArray();

            // 如果没有法线数据，尝试从面法线生成
            if (obj.Normals.All(n => n == 0f) && obj.TriangleCount > 0)
            {
                GenerateFaceNormals(obj);
            }

            ctx.Model.Objects.Add(obj);

            // 重置面数据（保留顶点/法线/UV 缓冲，后续对象可能引用相同的全局顶点）
            ctx.FaceTriangles.Clear();
        }

        /// <summary>
        /// 从面数据生成平坦法线（当面无 vn 引用时使用）。
        /// </summary>
        private static void GenerateFaceNormals(ObjObject obj)
        {
            var normals = new float[obj.VertexCount * 3];
            var faceNormals = new Vector3[obj.VertexCount];

            for (int i = 0; i < obj.TriangleCount; i++)
            {
                int i0 = (int)obj.Indices[i * 3 + 0];
                int i1 = (int)obj.Indices[i * 3 + 1];
                int i2 = (int)obj.Indices[i * 3 + 2];

                Vector3 v0 = GetVertex(obj.Positions, i0);
                Vector3 v1 = GetVertex(obj.Positions, i1);
                Vector3 v2 = GetVertex(obj.Positions, i2);

                Vector3 e1 = v1 - v0;
                Vector3 e2 = v2 - v0;
                Vector3 faceNormal = Vector3.Normalize(Vector3.Cross(e1, e2));

                faceNormals[i0] += faceNormal;
                faceNormals[i1] += faceNormal;
                faceNormals[i2] += faceNormal;
            }

            // 归一化并写入法线数组
            for (int i = 0; i < obj.VertexCount; i++)
            {
                Vector3 n = Vector3.Normalize(faceNormals[i]);
                normals[i * 3 + 0] = n.X;
                normals[i * 3 + 1] = n.Y;
                normals[i * 3 + 2] = n.Z;
            }

            obj.Normals = normals;
        }

        private static Vector3 GetVertex(float[] positions, int index)
        {
            int i = index * 3;
            return new Vector3(positions[i], positions[i + 1], positions[i + 2]);
        }

        // ── 数值解析辅助 ──

        private static readonly char[] FloatSeparators = { ' ', '\t' };
        private static readonly char[] IntSeparators = { ' ', '\t' };

        private static bool TryParseFloat(string s, out float result)
        {
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
        }

        private static bool TryParseInt(string s, out int result)
        {
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
        }

        // ── 内部解析上下文 ──

        private sealed class ParseContext
        {
            public LoadedObjModel Model;
            public List<Vector3> Positions = new();
            public List<Vector3> Normals = new();
            public List<Vector3> TexCoords = new();
            public List<ObjFaceVertex> FaceTriangles = new();
            public string? CurrentObjectName = null;
            public string? CurrentGroupName = null;
            public string CurrentMaterialName = string.Empty;

            public ParseContext(LoadedObjModel model)
            {
                Model = model;
            }
        }
    }
}
