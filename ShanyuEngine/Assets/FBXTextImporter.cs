using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;

namespace ShanyuEngine.Assets
{
    // ====================================================================
    // FBXTextImporter — 简化 FBX / OBJ 导入器
    // ====================================================================
    // OBJImporter：纯 C# 解析 Wavefront .obj 文本格式（v/vt/vn/f）
    //   - 解析顶点 / 法线 / 纹理坐标
    //   - 解析面索引，自动三角化多边形（quad → 2 三角形）
    //   - 支持 .mtl 材质（Kd / Ks / Ns / map_Kd 等）
    //   - 组装为 LoadedModel，输出可直接上传 GPU 的 CPU 端缓冲
    //
    // FBXImporter：占位实现，FBX 二进制 / ASCII 需集成 Assimp，此处返回错误信息。
    // ====================================================================

    /// <summary>OBJ 材质解析结果（来自 .mtl 文件）。</summary>

    /// <summary>
    /// Wavefront OBJ 文本格式导入器。
    /// 解析 v / vt / vn / f，支持 .mtl 材质，自动三角化多边形面。
    /// </summary>
    public class OBJImporter
    {
        private const int VertexStride = 32; // Position(3) + Normal(3) + TexCoord(2) = 8 floats

        /// <summary>从 .obj 文件路径加载模型。</summary>
        public LoadedModel Load(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"OBJ file not found: {path}", path);

            var model = new LoadedModel(path);
            string baseDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;

            var positions = new List<Vector3>();
            var texcoords = new List<Vector2>();
            var normals = new List<Vector3>();

            // 面：每组为 (posIndex, texIndex, normIndex)，OBJ 索引从 1 开始
            var faces = new List<List<(int p, int t, int n)>>();
            // 子网格分组（按材质 / usemtl 切分）
            var faceGroups = new List<(string mtl, List<List<(int p, int t, int n)>> faces)>();
            List<List<(int p, int t, int n)>> currentGroup = new();
            string currentMtl = string.Empty;

            // 材质库
            var materials = new Dictionary<string, ObjMaterial>(StringComparer.Ordinal);

            string[] lines = File.ReadAllLines(path);
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;

                // 拆分关键字与参数
                int space = line.IndexOfAny(new[] { ' ', '\t' });
                string keyword = space < 0 ? line : line.Substring(0, space);
                string args = space < 0 ? string.Empty : line.Substring(space + 1).Trim();

                switch (keyword)
                {
                    case "v":
                        var pos = ParseVec3(args);
                        if (pos.HasValue) positions.Add(pos.Value);
                        break;
                    case "vt":
                        var uv = ParseVec2(args);
                        if (uv.HasValue) texcoords.Add(uv.Value);
                        break;
                    case "vn":
                        var nrm = ParseVec3(args);
                        if (nrm.HasValue) normals.Add(nrm.Value);
                        break;
                    case "f":
                        var faceVerts = ParseFace(args, positions.Count, texcoords.Count, normals.Count);
                        if (faceVerts.Count > 0)
                        {
                            if (currentGroup.Count == 0 && faceGroups.Count == 0)
                            {
                                // 第一个组
                                currentGroup = new List<List<(int, int, int)>>();
                                faceGroups.Add((currentMtl, currentGroup));
                            }
                            currentGroup.Add(faceVerts);
                            faces.Add(faceVerts);
                        }
                        break;
                    case "usemtl":
                        // 切换材质组：结束当前组，开启新组
                        currentMtl = args;
                        currentGroup = new List<List<(int, int, int)>>();
                        faceGroups.Add((currentMtl, currentGroup));
                        break;
                    case "mtllib":
                        LoadMtlLib(args, baseDir, materials);
                        break;
                    case "g":
                    case "o":
                        // 组 / 对象名：开启新面组（保持当前材质引用）
                        currentGroup = new List<List<(int, int, int)>>();
                        faceGroups.Add((currentMtl, currentGroup));
                        break;
                }
            }

            // 若没有任何面组，创建一个空组占位以避免异常
            if (faceGroups.Count == 0)
                faceGroups.Add((string.Empty, new List<List<(int, int, int)>>()));

            // 合并顶点并构建子网格
            BuildModel(model, positions, texcoords, normals, faceGroups, materials, baseDir);

            return model;
        }

        // ──────────────────────────────────────────────────────────────
        // 文本解析辅助
        // ──────────────────────────────────────────────────────────────

        private static Vector3? ParseVec3(string args)
        {
            var p = SplitFloats(args);
            if (p.Length < 3) return null;
            return new Vector3(p[0], p[1], p[2]);
        }

        private static Vector2? ParseVec2(string args)
        {
            var p = SplitFloats(args);
            if (p.Length < 1) return null;
            float u = p[0];
            float v = p.Length > 1 ? p[1] : 0f;
            return new Vector2(u, v);
        }

        private static float[] SplitFloats(string args)
        {
            var tokens = args.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var result = new float[tokens.Length];
            for (int i = 0; i < tokens.Length; i++)
            {
                float.TryParse(tokens[i], NumberStyles.Float, CultureInfo.InvariantCulture, out result[i]);
            }
            return result;
        }

        /// <summary>解析面行，返回面的顶点索引列表（posIndex, texIndex, normIndex）。</summary>
        private static List<(int p, int t, int n)> ParseFace(string args, int posCount, int texCount, int normCount)
        {
            var result = new List<(int p, int t, int n)>();
            var tokens = args.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (string tok in tokens)
            {
                // 格式：v / v/vt / v//vn / v/vt/vn
                var parts = tok.Split('/');
                int p = ParseIndex(parts[0], posCount);
                int t = parts.Length > 1 && parts[1].Length > 0 ? ParseIndex(parts[1], texCount) : -1;
                int n = parts.Length > 2 && parts[2].Length > 0 ? ParseIndex(parts[2], normCount) : -1;
                result.Add((p, t, n));
            }
            return result;
        }

        /// <summary>OBJ 索引从 1 开始；负数表示从末尾倒数。转换为 0 基索引。</summary>
        private static int ParseIndex(string s, int count)
        {
            if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx)) return -1;
            if (idx > 0) return idx - 1;            // 正数：1-based
            if (idx < 0) return count + idx;          // 负数：相对末尾
            return -1;
        }

        // ──────────────────────────────────────────────────────────────
        // .mtl 材质解析
        // ──────────────────────────────────────────────────────────────

        private static void LoadMtlLib(string libName, string baseDir, Dictionary<string, ObjMaterial> materials)
        {
            string mtlPath = Path.Combine(baseDir, libName);
            if (!File.Exists(mtlPath)) return;

            ObjMaterial? current = null;
            string[] lines = File.ReadAllLines(mtlPath);

            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;

                int space = line.IndexOfAny(new[] { ' ', '\t' });
                string keyword = space < 0 ? line : line.Substring(0, space);
                string args = space < 0 ? string.Empty : line.Substring(space + 1).Trim();

                switch (keyword)
                {
                    case "newmtl":
                        current = new ObjMaterial { Name = args };
                        materials[args] = current;
                        break;
                    case "Kd" when current != null:
                        var kd = ParseVec3(args);
                        if (kd.HasValue) current.DiffuseColor = kd.Value;
                        break;
                    case "Ks" when current != null:
                        var ks = ParseVec3(args);
                        if (ks.HasValue) current.SpecularColor = ks.Value;
                        break;
                    case "Ka" when current != null:
                        var ka = ParseVec3(args);
                        if (ka.HasValue) current.AmbientColor = ka.Value;
                        break;
                    case "Ns" when current != null:
                        float.TryParse(args, NumberStyles.Float, CultureInfo.InvariantCulture, out float ns);
                        current.Shininess = ns;
                        break;
                    case "d" when current != null:
                        float.TryParse(args, NumberStyles.Float, CultureInfo.InvariantCulture, out float d);
                        current.Opacity = d;
                        break;
                    case "Tr" when current != null:
                        float.TryParse(args, NumberStyles.Float, CultureInfo.InvariantCulture, out float tr);
                        current.Opacity = 1f - tr;
                        break;
                    case "map_Kd" when current != null:
                        current.DiffuseTexturePath = ResolveTexturePath(args, baseDir);
                        break;
                    case "map_Bump":
                    case "bump" when current != null:
                        current.NormalTexturePath = ResolveTexturePath(args, baseDir);
                        break;
                    case "map_Ks" when current != null:
                        current.SpecularTexturePath = ResolveTexturePath(args, baseDir);
                        break;
                }
            }
        }

        private static string ResolveTexturePath(string args, string baseDir)
        {
            // 可能含选项（如 -s 1 1 1），取最后一个非选项 token 作为路径
            var tokens = args.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = tokens.Length - 1; i >= 0; i--)
            {
                if (!tokens[i].StartsWith("-"))
                {
                    string p = tokens[i];
                    return Path.IsPathRooted(p) ? p : Path.Combine(baseDir, p);
                }
            }
            return args;
        }

        // ──────────────────────────────────────────────────────────────
        // 模型组装
        // ──────────────────────────────────────────────────────────────

        private static void BuildModel(
            LoadedModel model,
            List<Vector3> positions, List<Vector2> texcoords, List<Vector3> normals,
            List<(string mtl, List<List<(int p, int t, int n)>> faces)> faceGroups,
            Dictionary<string, ObjMaterial> materials, string baseDir)
        {
            var vertexList = new List<float>();
            var indexList = new List<uint>();
            var vertexCache = new Dictionary<long, int>();
            int vertexOffset = 0;
            bool use32 = false;

            // 转换 OBJ 材质为 LoadedMaterial
            int mtlIdx = 0;
            var mtlNameToIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var kvp in materials)
            {
                var lm = new LoadedMaterial
                {
                    Name = kvp.Value.Name,
                    BaseColorFactor = new Vector4(kvp.Value.DiffuseColor, kvp.Value.Opacity),
                    MetallicFactor = 0f,
                    RoughnessFactor = kvp.Value.Shininess > 0 ? 1f / kvp.Value.Shininess * 32f : 1f,
                    BaseColorTexturePath = kvp.Value.DiffuseTexturePath,
                    NormalTexturePath = kvp.Value.NormalTexturePath,
                    MetallicRoughnessTexturePath = kvp.Value.SpecularTexturePath,
                    EmissiveFactor = kvp.Value.AmbientColor,
                    AlphaMode = kvp.Value.Opacity < 1f ? "BLEND" : "OPAQUE"
                };
                model.Materials.Add(lm);
                mtlNameToIndex[kvp.Key] = mtlIdx++;
            }
            if (model.Materials.Count == 0)
            {
                model.Materials.Add(new LoadedMaterial { Name = "Default" });
                mtlNameToIndex[string.Empty] = 0;
            }

            bool hasNormals = normals.Count > 0;
            bool hasTexcoords = texcoords.Count > 0;

            foreach (var group in faceGroups)
            {
                if (group.faces.Count == 0) continue;

                int matIndex = 0;
                if (!string.IsNullOrEmpty(group.mtl) && mtlNameToIndex.TryGetValue(group.mtl, out int mi))
                    matIndex = mi;

                var subMesh = new ModelSubMesh
                {
                    Name = $"SubMesh_{group.mtl}",
                    MaterialIndex = matIndex,
                    IndexStart = (int)indexList.Count
                };

                foreach (var face in group.faces)
                {
                    // 三角化：n 边形 → (n-2) 个三角形
                    var triangles = TriangulateFace(face);

                    foreach (var fv in triangles)
                    {
                        long key = ((long)fv.p << 42) ^ ((long)(fv.t + 2) << 21) ^ (long)(fv.n + 2);
                        if (!vertexCache.TryGetValue(key, out int vIdx))
                        {
                            Vector3 pos = (fv.p >= 0 && fv.p < positions.Count) ? positions[fv.p] : Vector3.Zero;
                            Vector3 nrm;
                            if (hasNormals && fv.n >= 0 && fv.n < normals.Count)
                                nrm = normals[fv.n];
                            else
                                nrm = new Vector3(0f, 1f, 0f);

                            Vector2 uv;
                            if (hasTexcoords && fv.t >= 0 && fv.t < texcoords.Count)
                                uv = texcoords[fv.t];
                            else
                                uv = Vector2.Zero;

                            vertexList.Add(pos.X); vertexList.Add(pos.Y); vertexList.Add(pos.Z);
                            vertexList.Add(nrm.X); vertexList.Add(nrm.Y); vertexList.Add(nrm.Z);
                            vertexList.Add(uv.X); vertexList.Add(uv.Y);

                            vIdx = vertexOffset++;
                            vertexCache[key] = vIdx;
                        }
                        indexList.Add((uint)vIdx);
                    }
                }

                subMesh.IndexCount = (int)indexList.Count - subMesh.IndexStart;
                if (subMesh.IndexCount > 0) model.SubMeshes.Add(subMesh);
                vertexCache.Clear();
                if (vertexOffset > 65535) use32 = true;
            }

            // 若没有有效子网格，生成空缓冲避免上层 NRE
            model.VertexCount = vertexOffset;
            model.IndexCount = (int)indexList.Count;
            model.VertexStride = VertexStride;
            model.Use32BitIndices = use32 || vertexOffset > 65535;

            model.RawVertexData = PackFloats(vertexList);
            model.RawIndexData = PackIndices(indexList, model.Use32BitIndices);
        }

        /// <summary>将多边形面（3+ 顶点）三角化为三角形序列（扇形剖分）。</summary>
        private static List<(int p, int t, int n)> TriangulateFace(List<(int p, int t, int n)> face)
        {
            var result = new List<(int p, int t, int n)>(face.Count);
            if (face.Count < 3) return result;

            if (face.Count == 3)
            {
                result.AddRange(face);
            }
            else
            {
                // 扇形三角化：以第一个顶点为中心
                for (int i = 1; i + 1 < face.Count; i++)
                {
                    result.Add(face[0]);
                    result.Add(face[i]);
                    result.Add(face[i + 1]);
                }
            }
            return result;
        }

        private static byte[] PackFloats(List<float> data)
        {
            byte[] bytes = new byte[data.Count * sizeof(float)];
            Buffer.BlockCopy(data.ToArray(), 0, bytes, 0, bytes.Length);
            return bytes;
        }

        private static byte[] PackIndices(List<uint> indices, bool use32)
        {
            if (use32)
            {
                byte[] bytes = new byte[indices.Count * sizeof(uint)];
                Buffer.BlockCopy(indices.ToArray(), 0, bytes, 0, bytes.Length);
                return bytes;
            }
            else
            {
                byte[] bytes = new byte[indices.Count * sizeof(ushort)];
                for (int i = 0; i < indices.Count; i++)
                {
                    ushort v = (ushort)indices[i];
                    bytes[i * 2] = (byte)(v & 0xFF);
                    bytes[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
                }
                return bytes;
            }
        }
    }

    // ====================================================================
    // FbxStubImporter — FBX 导入器占位实现（旧版兼容别名）
    // ====================================================================
    // 原名 FBXImporter，已重命名为 FbxStubImporter 以避免与
    // ShanyuEngine.Assets.Importers.FbxImporter（完整解析器）同名冲突。
    // ====================================================================

    /// <summary>
    /// FBX 导入器占位（旧版）。请使用 ShanyuEngine.Assets.Importers.FbxImporter
    /// 获取完整 FBX 解析功能。此类仅保留向后兼容。
    /// </summary>
    [Obsolete("Use ShanyuEngine.Assets.Importers.FbxImporter for full FBX parsing. This stub is retained for backward compatibility only.")]
    public class FbxStubImporter
    {
        /// <summary>FBX 导入结果，包含模型数据或错误信息。</summary>
        public sealed class FbxImportResult
        {
            public LoadedModel? Model { get; set; }
            public bool Success => Model != null;
            public string Error { get; set; } = string.Empty;
        }

        /// <summary>
        /// 尝试加载 FBX 文件。当前为占位实现，返回需要 Assimp 集成的错误信息。
        /// </summary>
        public FbxImportResult Load(string path)
        {
            if (!File.Exists(path))
            {
                return new FbxImportResult
                {
                    Error = $"FBX file not found: {path}"
                };
            }

            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext != ".fbx")
            {
                return new FbxImportResult
                {
                    Error = $"Not an FBX file (extension '{ext}'): {path}"
                };
            }

            // FBX 二进制 / ASCII 解析需要 Assimp（Open Asset Import Library）集成。
            // 推荐方案：
            //   1. 通过 P/Invoke 调用 Assimp native 库（assimp.dll / libassimp.so）
            //   2. 或使用 AssimpNet 托管包装
            // 集成后此处应：
            //   - 创建 Assimp Importer
            //   - 读取场景（含层级、网格、材质、骨骼、动画）
            //   - 转换为 LoadedModel（顶点 / 索引 / ModelSubMesh[]）
            return new FbxImportResult
            {
                Error = "FBX parsing requires Assimp integration. " +
                        "Please add AssimpNet (NuGet: AssimpNet) or P/Invoke assimp native library, " +
                        "then implement the conversion from Assimp.Scene to LoadedModel in FBXImporter.Load(). " +
                        $"Target file: {path}"
            };
        }

        /// <summary>
        /// 异步加载 FBX 文件（占位）。
        /// </summary>
        public System.Threading.Tasks.Task<FbxImportResult> LoadAsync(string path)
        {
            return System.Threading.Tasks.Task.FromResult(Load(path));
        }
    }
}
