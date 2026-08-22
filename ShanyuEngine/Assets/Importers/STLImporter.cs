// =====================================================================================
//  ShanyuEngine - STLImporter：STL 导入器
//  命名空间：ShanyuEngine.Assets.Importers
// -------------------------------------------------------------------------------------
//  纯 C# 实现，支持 STL 立体光刻格式（ASCII 和 Binary）：
//    - ASCII STL 解析（facet normal / vertex / outer loop / endloop / endfacet）
//    - Binary STL 解析（80字节头 / 三角形数 / 法线+3顶点+属性字节）
//    - 三角形→网格转换（顶点合并 / 去重 / 索引化）
//    - 法线处理（使用面法线或重新计算顶点法线）
//    - 导入选项（合并重复顶点 / 计算法线 / 生成UV / 单位转换）
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

    /// <summary>STL 导入选项。</summary>
    public sealed class StlImportOptions
    {
        /// <summary>全局缩放比例。</summary>
        public float Scale = 1.0f;

        /// <summary>是否合并重复顶点（位置相同的顶点合并为一个）。</summary>
        public bool MergeDuplicateVertices = true;

        /// <summary>是否从面法线计算顶点法线（而非使用面法线）。</summary>
        public bool RecalculateNormals = false;

        /// <summary>是否生成 UV 坐标（球面映射 / 盒形映射）。</summary>
        public bool GenerateUVs = false;

        /// <summary>UV 生成方式。</summary>
        public StlUVGenerationMode UVMode = StlUVGenerationMode.BoxProjection;

        /// <summary>单位转换。</summary>
        public StlUnitType SourceUnit = StlUnitType.Millimeter;

        /// <summary>目标单位。</summary>
        public StlUnitType TargetUnit = StlUnitType.Meter;
    }

    /// <summary>STL UV 生成模式。</summary>
    public enum StlUVGenerationMode
    {
        /// <summary>球面映射。</summary>
        SphericalProjection,
        /// <summary>盒形映射。</summary>
        BoxProjection,
        /// <summary>平面映射。</summary>
        PlanarProjection
    }

    /// <summary>STL 单位类型。</summary>
    public enum StlUnitType
    {
        Millimeter,
        Centimeter,
        Meter,
        Inch,
        Foot
    }

    // =================================================================================
    //  STL 三角形
    // =================================================================================

    internal struct StlTriangle
    {
        public Vector3 Normal;
        public Vector3 V0;
        public Vector3 V1;
        public Vector3 V2;
        public ushort Attribute;
    }

    // =================================================================================
    //  STLImporter —— 主导入器
    // =================================================================================

    /// <summary>
    /// STL 导入器。支持 ASCII (.stl) 和 Binary (.stl) 格式。
    /// 输出为 InterchangeScene 统一格式。
    /// </summary>
    public sealed class STLImporter
    {
        private readonly string _filePath;
        private readonly List<string> _messages = new();
        private StlImportOptions _options = new();

        /// <summary>解析产出的统一场景格式。</summary>
        public InterchangeScene? Scene { get; private set; }

        /// <summary>构造 STL 导入器。</summary>
        public STLImporter(string filePath)
        {
            _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        }

        /// <summary>使用选项执行导入。</summary>
        public InterchangeScene Import(StlImportOptions? options = null)
        {
            if (!File.Exists(_filePath))
                throw new FileNotFoundException($"STL 源文件不存在: {_filePath}");

            _options = options ?? new StlImportOptions();

            Scene = new InterchangeScene
            {
                Name = Path.GetFileNameWithoutExtension(_filePath),
                SourceFilePath = _filePath,
                SourceFormat = "STL"
            };

            try
            {
                // 检测格式：ASCII 还是 Binary
                bool isBinary = DetectBinaryStl(_filePath);

                List<StlTriangle> triangles;
                if (isBinary)
                {
                    _messages.Add("检测到 Binary STL 格式");
                    triangles = ParseBinaryStl(_filePath);
                }
                else
                {
                    _messages.Add("检测到 ASCII STL 格式");
                    triangles = ParseAsciiStl(_filePath);
                }

                _messages.Add($"STL 解析完成: {triangles.Count} 三角形");

                ConvertToMesh(triangles);
                PostProcessScene();
            }
            catch (Exception ex)
            {
                Scene.ImportMessages.Add($"导入 STL 失败: {ex.Message}");
                throw;
            }

            Scene.ImportMessages.AddRange(_messages);
            return Scene;
        }

        // =================================================================================
        //  格式检测
        // =================================================================================

        /// <summary>
        /// 检测 STL 文件是 ASCII 还是 Binary。
        /// Binary STL 文件头为 80 字节 + 4 字节三角形数 = 84 字节，
        /// 如果文件大小 == 84 + 三角形数 * 50，则为 Binary。
        /// ASCII STL 文件前几个字节为 "solid"。
        /// </summary>
        private bool DetectBinaryStl(string filePath)
        {
            // 先检查文件大小和 "solid" 关键字
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);

            byte[] header = new byte[5];
            int read = fs.Read(header, 0, 5);

            // 如果以 "solid" 开头，可能是 ASCII
            if (read >= 5 && Encoding.ASCII.GetString(header, 0, 5).Equals("solid", StringComparison.OrdinalIgnoreCase))
            {
                // 但有些 Binary STL 也以 "solid" 开头，需要进一步检查
                long fileSize = fs.Length;
                if (fileSize >= 84)
                {
                    fs.Position = 80;
                    byte[] countBytes = new byte[4];
                    fs.Read(countBytes, 0, 4);
                    uint triCount = BitConverter.ToUInt32(countBytes, 0);

                    long expectedSize = 84 + (long)triCount * 50;
                    if (expectedSize == fileSize)
                        return true; // 大小匹配，是 Binary
                }
                return false; // 否则认为是 ASCII
            }

            return true; // 不以 "solid" 开头，是 Binary
        }

        // =================================================================================
        //  ASCII STL 解析
        // =================================================================================

        private List<StlTriangle> ParseAsciiStl(string filePath)
        {
            var triangles = new List<StlTriangle>();
            string[] lines = File.ReadAllLines(filePath, Encoding.UTF8);

            StlTriangle tri = default;
            int vertexIndex = 0;
            bool inFacet = false;

            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();

                if (line.StartsWith("facet", StringComparison.OrdinalIgnoreCase))
                {
                    inFacet = true;
                    vertexIndex = 0;

                    // facet normal nx ny nz
                    var parts = line.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 5)
                    {
                        tri.Normal = new Vector3(
                            ParseFloat(parts[2]),
                            ParseFloat(parts[3]),
                            ParseFloat(parts[4])
                        );
                    }
                }
                else if (line.StartsWith("vertex", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 4)
                    {
                        var v = new Vector3(
                            ParseFloat(parts[1]),
                            ParseFloat(parts[2]),
                            ParseFloat(parts[3])
                        );

                        switch (vertexIndex)
                        {
                            case 0: tri.V0 = v; break;
                            case 1: tri.V1 = v; break;
                            case 2: tri.V2 = v; break;
                        }
                        vertexIndex++;
                    }
                }
                else if (line.StartsWith("endfacet", StringComparison.OrdinalIgnoreCase))
                {
                    if (inFacet && vertexIndex == 3)
                    {
                        triangles.Add(tri);
                    }
                    inFacet = false;
                }
            }

            return triangles;
        }

        // =================================================================================
        //  Binary STL 解析
        // =================================================================================

        private List<StlTriangle> ParseBinaryStl(string filePath)
        {
            var triangles = new List<StlTriangle>();
            byte[] data = File.ReadAllBytes(filePath);

            if (data.Length < 84)
                throw new InvalidDataException("Binary STL 文件过小");

            // 跳过 80 字节头，读取三角形数
            uint triCount = BitConverter.ToUInt32(data, 80);
            _messages.Add($"Binary STL: {triCount} 三角形");

            int offset = 84;
            for (int i = 0; i < triCount; i++)
            {
                if (offset + 50 > data.Length)
                {
                    _messages.Add($"警告: 文件在三角形 {i} 处截断");
                    break;
                }

                var tri = new StlTriangle
                {
                    // 法线（12 字节，小端 float）
                    Normal = new Vector3(
                        BitConverter.ToSingle(data, offset),
                        BitConverter.ToSingle(data, offset + 4),
                        BitConverter.ToSingle(data, offset + 8)
                    ),
                    // 顶点 0
                    V0 = new Vector3(
                        BitConverter.ToSingle(data, offset + 12),
                        BitConverter.ToSingle(data, offset + 16),
                        BitConverter.ToSingle(data, offset + 20)
                    ),
                    // 顶点 1
                    V1 = new Vector3(
                        BitConverter.ToSingle(data, offset + 24),
                        BitConverter.ToSingle(data, offset + 28),
                        BitConverter.ToSingle(data, offset + 32)
                    ),
                    // 顶点 2
                    V2 = new Vector3(
                        BitConverter.ToSingle(data, offset + 36),
                        BitConverter.ToSingle(data, offset + 40),
                        BitConverter.ToSingle(data, offset + 44)
                    ),
                    // 属性字节
                    Attribute = BitConverter.ToUInt16(data, offset + 48)
                };

                triangles.Add(tri);
                offset += 50;
            }

            return triangles;
        }

        // =================================================================================
        //  转换为网格
        // =================================================================================

        private void ConvertToMesh(List<StlTriangle> triangles)
        {
            var mesh = new InterchangeMesh
            {
                Name = Scene.Name
            };

            float unitScale = GetUnitScaleFactor(_options.SourceUnit, _options.TargetUnit);
            float totalScale = _options.Scale * unitScale;

            if (_options.MergeDuplicateVertices)
            {
                // 顶点合并模式：使用哈希表去重
                var vertexMap = new Dictionary<Vector3, int>(new Vector3EqualityComparer());

                foreach (var tri in triangles)
                {
                    // 应用单位转换
                    var v0 = tri.V0 * totalScale;
                    var v1 = tri.V1 * totalScale;
                    var v2 = tri.V2 * totalScale;

                    int i0 = GetOrAddVertex(mesh, vertexMap, v0);
                    int i1 = GetOrAddVertex(mesh, vertexMap, v1);
                    int i2 = GetOrAddVertex(mesh, vertexMap, v2);

                    mesh.Indices.Add(i0);
                    mesh.Indices.Add(i1);
                    mesh.Indices.Add(i2);
                }

                _messages.Add($"顶点合并: {triangles.Count * 3} → {mesh.Vertices.Count} 顶点");
            }
            else
            {
                // 不合并：每个三角形 3 个独立顶点
                foreach (var tri in triangles)
                {
                    int baseIdx = mesh.Vertices.Count;

                    mesh.Vertices.Add(tri.V0 * totalScale);
                    mesh.Vertices.Add(tri.V1 * totalScale);
                    mesh.Vertices.Add(tri.V2 * totalScale);

                    // 使用面法线
                    if (!_options.RecalculateNormals)
                    {
                        Vector3 normal = tri.Normal;
                        if (normal == Vector3.Zero)
                            normal = Vector3.Normalize(Vector3.Cross(tri.V1 - tri.V0, tri.V2 - tri.V0));

                        mesh.Normals.Add(normal);
                        mesh.Normals.Add(normal);
                        mesh.Normals.Add(normal);
                    }

                    mesh.Indices.Add(baseIdx);
                    mesh.Indices.Add(baseIdx + 1);
                    mesh.Indices.Add(baseIdx + 2);
                }
            }

            // 重新计算法线
            if (_options.RecalculateNormals || mesh.Normals.Count == 0)
            {
                CalculateVertexNormals(mesh);
            }

            // 生成 UV
            if (_options.GenerateUVs)
            {
                GenerateUVs(mesh);
            }

            Scene.Meshes.Add(mesh);

            // 添加默认材质
            Scene.Materials.Add(new InterchangeMaterial
            {
                Name = "DefaultMaterial",
                DiffuseColor = new Vector3(0.8f, 0.8f, 0.8f)
            });
            mesh.MaterialIndices.Add(0);

            // 创建场景节点
            Scene.RootNodes.Add(new InterchangeNode
            {
                Name = Scene.Name,
                MeshIndex = 0
            });
        }

        private int GetOrAddVertex(InterchangeMesh mesh, Dictionary<Vector3, int> map, Vector3 pos)
        {
            if (map.TryGetValue(pos, out int idx))
                return idx;

            idx = mesh.Vertices.Count;
            mesh.Vertices.Add(pos);
            map[pos] = idx;
            return idx;
        }

        // =================================================================================
        //  法线计算
        // =================================================================================

        private void CalculateVertexNormals(InterchangeMesh mesh)
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

        // =================================================================================
        //  UV 生成
        // =================================================================================

        private void GenerateUVs(InterchangeMesh mesh)
        {
            // 计算包围盒
            Vector3 min = new(float.MaxValue);
            Vector3 max = new(float.MinValue);

            foreach (var v in mesh.Vertices)
            {
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }

            Vector3 size = max - min;
            if (size.X < 1e-6f) size.X = 1f;
            if (size.Y < 1e-6f) size.Y = 1f;
            if (size.Z < 1e-6f) size.Z = 1f;

            mesh.UV0.Clear();

            switch (_options.UVMode)
            {
                case StlUVGenerationMode.BoxProjection:
                    foreach (var v in mesh.Vertices)
                    {
                        var rel = (v - min) / size;
                        // 简单盒形映射：取 XZ 平面
                        mesh.UV0.Add(new Vector2(rel.X, rel.Z));
                    }
                    break;

                case StlUVGenerationMode.SphericalProjection:
                    Vector3 center = (min + max) * 0.5f;
                    foreach (var v in mesh.Vertices)
                    {
                        var dir = Vector3.Normalize(v - center);
                        float u = 0.5f + MathF.Atan2(dir.Z, dir.X) / (2f * MathF.PI);
                        float vCoord = 0.5f - MathF.Asin(dir.Y) / MathF.PI;
                        mesh.UV0.Add(new Vector2(u, vCoord));
                    }
                    break;

                case StlUVGenerationMode.PlanarProjection:
                    foreach (var v in mesh.Vertices)
                    {
                        var rel = (v - min) / size;
                        mesh.UV0.Add(new Vector2(rel.X, rel.Y));
                    }
                    break;
            }
        }

        // =================================================================================
        //  后处理
        // =================================================================================

        private void PostProcessScene()
        {
            // STL 无额外后处理
        }

        // =================================================================================
        //  工具方法
        // =================================================================================

        private static float ParseFloat(string s)
        {
            if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float val))
                return val;
            return 0f;
        }

        /// <summary>获取单位转换缩放因子。</summary>
        private static float GetUnitScaleFactor(StlUnitType source, StlUnitType target)
        {
            float sourceInMeters = source switch
            {
                StlUnitType.Millimeter => 0.001f,
                StlUnitType.Centimeter => 0.01f,
                StlUnitType.Meter => 1f,
                StlUnitType.Inch => 0.0254f,
                StlUnitType.Foot => 0.3048f,
                _ => 1f
            };

            float targetInMeters = target switch
            {
                StlUnitType.Millimeter => 0.001f,
                StlUnitType.Centimeter => 0.01f,
                StlUnitType.Meter => 1f,
                StlUnitType.Inch => 0.0254f,
                StlUnitType.Foot => 0.3048f,
                _ => 1f
            };

            return sourceInMeters / targetInMeters;
        }
    }

    // =================================================================================
    //  Vector3 相等比较器（用于顶点去重）
    // =================================================================================

    /// <summary>Vector3 相等比较器（浮点容差）。</summary>
    internal sealed class Vector3EqualityComparer : IEqualityComparer<Vector3>
    {
        private const float Epsilon = 1e-6f;

        public bool Equals(Vector3 a, Vector3 b)
        {
            return MathF.Abs(a.X - b.X) < Epsilon &&
                   MathF.Abs(a.Y - b.Y) < Epsilon &&
                   MathF.Abs(a.Z - b.Z) < Epsilon;
        }

        public int GetHashCode(Vector3 v)
        {
            // 量化到 6 位小数后取哈希
            int x = (int)(v.X * 1e6f);
            int y = (int)(v.Y * 1e6f);
            int z = (int)(v.Z * 1e6f);
            return HashCode.Combine(x, y, z);
        }
    }

    // =================================================================================
    //  STLImporter 适配器
    // =================================================================================

    /// <summary>STL 导入器适配器（用于 AssetImportManager 注册）。</summary>
    public sealed class STLImporterAdapter : IAssetImporter
    {
        /// <summary>支持的扩展名。</summary>
        public IEnumerable<string> SupportedExtensions => new[] { "stl" };

        /// <summary>导入 STL 文件。</summary>
        public InterchangeScene Import(string filePath)
        {
            var importer = new STLImporter(filePath);
            return importer.Import();
        }
    }
}
