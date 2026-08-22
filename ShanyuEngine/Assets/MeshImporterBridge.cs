// ==================================================================================================
//  MeshImporterBridge.cs — 网格导入桥梁
//
//  命名空间: ShanyuEngine.Assets
//  功能:     将 InterchangeSystem 解析结果转换为 GPU 可用的 MeshGpuResource，
//            支持 glTF/OBJ/FBX 格式，提供同步/异步导入、法线/切线自动计算。
// ==================================================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using ShanyuEngine.Rendering;
using ShanyuEngine.RHI;

namespace ShanyuEngine.Assets
{
    // ─────────────────────────────────────────────────────────────────────────────
    //  AABB — 轴对齐包围盒
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>轴对齐包围盒（Axis-Aligned Bounding Box）。</summary>
    public struct AABB
    {
        /// <summary>最小角点。</summary>
        public Vector3 Min;
        /// <summary>最大角点。</summary>
        public Vector3 Max;

        /// <summary>中心点。</summary>
        public Vector3 Center => (Min + Max) * 0.5f;

        /// <summary>尺寸（Max - Min）。</summary>
        public Vector3 Extents => Max - Min;

        public AABB(Vector3 min, Vector3 max)
        {
            Min = min;
            Max = max;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  SubMeshRange — 子网格索引范围
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>子网格在全局索引缓冲中的范围。</summary>
    public readonly struct SubMeshRange
    {
        /// <summary>起始索引（在索引数组中的偏移）。</summary>
        public readonly int StartIndex;
        /// <summary>索引数量。</summary>
        public readonly int IndexCount;
        /// <summary>材质索引（在材质数组中的位置，-1 表示无材质）。</summary>
        public readonly int MaterialIndex;

        public SubMeshRange(int startIndex, int indexCount, int materialIndex)
        {
            StartIndex = startIndex;
            IndexCount = indexCount;
            MaterialIndex = materialIndex;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  ImportedMeshData — 从 InterchangeSystem 转换的中间数据结构
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>从 InterchangeMeshNode 提取并转换后的中间网格数据。</summary>
    public sealed class ImportedMeshData
    {
        /// <summary>顶点数组（与 RenderVertex 一一对应）。</summary>
        public List<RenderVertex> Vertices = new();

        /// <summary>索引数组（每 3 个一组构成三角形）。</summary>
        public List<uint> Indices = new();

        /// <summary>子网格范围列表（按材质分组）。</summary>
        public List<SubMeshRange> SubMeshes = new();

        /// <summary>网格名称。</summary>
        public string Name = string.Empty;

        /// <summary>局部空间包围盒。</summary>
        public AABB Bounds;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  MeshImporterBridge — 网格导入桥梁
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 网格导入桥梁。负责将外部模型文件（glTF/OBJ/FBX）通过 InterchangeSystem 解析，
    /// 提取顶点/索引/法线/UV，自动计算缺失的几何属性，最终生成 <see cref="MeshGpuResource"/>。
    /// </summary>
    public sealed class MeshImporterBridge
    {
        private readonly AssetDatabase _assetDatabase;

        /// <summary>构造桥梁实例。</summary>
        /// <param name="assetDatabase">资产数据库（用于 InterchangeJobManager）。</param>
        public MeshImporterBridge(AssetDatabase assetDatabase)
        {
            _assetDatabase = assetDatabase ?? throw new ArgumentNullException(nameof(assetDatabase));
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  公开导入接口
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 从指定文件路径导入模型，返回 <see cref="MeshGpuResource"/> 列表。
        /// 支持的格式：.gltf / .glb / .obj / .fbx。
        /// </summary>
        /// <param name="filePath">模型文件的绝对路径。</param>
        /// <param name="device">RHI 设备（用于创建 GPU 缓冲）。</param>
        /// <returns>导入成功的 GPU 网格资源列表。</returns>
        /// <exception cref="ArgumentException">文件路径为空。</exception>
        /// <exception cref="FileNotFoundException">文件不存在。</exception>
        /// <exception cref="NotSupportedException">格式不受支持。</exception>
        public List<MeshGpuResource> ImportFromInterchange(string filePath, RHIDevice device)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("文件路径不能为空", nameof(filePath));
            if (!File.Exists(filePath))
                throw new FileNotFoundException("找不到模型文件", filePath);
            if (device == null)
                throw new ArgumentNullException(nameof(device));

            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext != ".gltf" && ext != ".glb" && ext != ".obj" && ext != ".fbx")
                throw new NotSupportedException($"不支持的模型格式: {ext}");

            // 创建 Interchange 任务管理器并执行解析
            var manager = new InterchangeJobManager(_assetDatabase);
            var parser = manager.SelectParser(filePath);
            if (parser == null)
                throw new InvalidOperationException($"无法找到适用于 {ext} 的解析器");

            var container = parser.Parse(filePath);
            if (container == null)
                throw new InvalidOperationException("解析结果为空");

            // 提取所有 MeshNode
            var meshNodes = container.GetNodes<InterchangeMeshNode>();
            var result = new List<MeshGpuResource>();

            foreach (var node in meshNodes)
            {
                var importedData = ExtractMeshFromNode(node);
                if (importedData.Vertices.Count == 0 || importedData.Indices.Count == 0)
                    continue;

                var vertices = importedData.Vertices.ToArray();
                var indices = importedData.Indices.ToArray();
                var gpuMesh = new MeshGpuResource(device, vertices, indices, importedData.Name);
                result.Add(gpuMesh);
            }

            return result;
        }

        /// <summary>
        /// 异步导入模型文件。
        /// </summary>
        public async Task<List<MeshGpuResource>> ImportFromInterchangeAsync(string filePath, RHIDevice device)
        {
            return await Task.Run(() => ImportFromInterchange(filePath, device));
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  从 InterchangeMeshNode 提取数据
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 从单个 <see cref="InterchangeMeshNode"/> 提取顶点、索引、子网格信息，
        /// 并在缺失时自动计算法线与切线。
        /// </summary>
        public ImportedMeshData ExtractMeshFromNode(InterchangeMeshNode node)
        {
            if (node == null)
                throw new ArgumentNullException(nameof(node));

            var data = new ImportedMeshData
            {
                Name = node.DisplayLabel ?? "UnnamedMesh"
            };

            int vertexCount = node.Vertices?.Count ?? 0;
            if (vertexCount == 0)
                return data;

            bool hasNormals = node.Normals != null && node.Normals.Count >= vertexCount;
            bool hasUVs = node.UV0 != null && node.UV0.Count >= vertexCount;

            // ── 提取顶点属性 ──
            for (int i = 0; i < vertexCount; i++)
            {
                var pos = node.Vertices![i];
                var normal = hasNormals ? node.Normals![i] : Vector3.UnitY;
                var uv = hasUVs ? node.UV0![i] : Vector2.Zero;
                var color = Vector4.One;

                data.Vertices.Add(new RenderVertex(pos, normal, uv, color));
            }

            // ── 提取索引 ──
            if (node.Indices != null && node.Indices.Count > 0)
            {
                data.Indices.AddRange(node.Indices.Select(idx => (uint)idx));
            }

            // ── 缺失法线时计算面法线 ──
            if (!hasNormals && data.Indices.Count > 0)
            {
                ComputeFaceNormals(data);
            }

            // ── 缺失切线且存在 UV 时计算切线（简化版 Mikktspace）──
            if ((!node.Tangents?.Any() ?? true) && hasUVs && data.Indices.Count > 0)
            {
                ComputeTangents(data);
            }

            // ── 构建子网格范围 ──
            // InterchangeMeshNode 当前按 primitive / group 拆分，每个 node 即一个子网格
            if (node.MaterialUids != null && node.MaterialUids.Count > 0)
            {
                data.SubMeshes.Add(new SubMeshRange(0, data.Indices.Count, 0));
            }
            else
            {
                data.SubMeshes.Add(new SubMeshRange(0, data.Indices.Count, -1));
            }

            // ── 计算包围盒 ──
            data.Bounds = ComputeBounds(data.Vertices);

            return data;
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  几何计算辅助
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>基于面法线计算顶点法线（平均相邻面法线并归一化）。</summary>
        private static void ComputeFaceNormals(ImportedMeshData data)
        {
            var verts = data.Vertices;
            var indices = data.Indices;

            // 归零法线
            for (int i = 0; i < verts.Count; i++)
            {
                verts[i] = new RenderVertex(verts[i].Position, Vector3.Zero, verts[i].TexCoord, verts[i].Color);
            }

            // 累积面法线到顶点
            for (int i = 0; i < indices.Count; i += 3)
            {
                if (i + 2 >= indices.Count) break;

                int i0 = (int)indices[i];
                int i1 = (int)indices[i + 1];
                int i2 = (int)indices[i + 2];

                if (i0 >= verts.Count || i1 >= verts.Count || i2 >= verts.Count)
                    continue;

                var v0 = verts[i0].Position;
                var v1 = verts[i1].Position;
                var v2 = verts[i2].Position;

                var edge1 = v1 - v0;
                var edge2 = v2 - v0;
                var faceNormal = Vector3.Cross(edge1, edge2);

                if (faceNormal.LengthSquared() > 1e-12f)
                {
                    faceNormal = Vector3.Normalize(faceNormal);
                    verts[i0] = new RenderVertex(verts[i0].Position, verts[i0].Normal + faceNormal, verts[i0].TexCoord, verts[i0].Color);
                    verts[i1] = new RenderVertex(verts[i1].Position, verts[i1].Normal + faceNormal, verts[i1].TexCoord, verts[i1].Color);
                    verts[i2] = new RenderVertex(verts[i2].Position, verts[i2].Normal + faceNormal, verts[i2].TexCoord, verts[i2].Color);
                }
            }

            // 归一化
            for (int i = 0; i < verts.Count; i++)
            {
                var n = verts[i].Normal;
                if (n.LengthSquared() > 1e-12f)
                    n = Vector3.Normalize(n);
                else
                    n = Vector3.UnitY;
                verts[i] = new RenderVertex(verts[i].Position, n, verts[i].TexCoord, verts[i].Color);
            }
        }

        /// <summary>
        /// 基于 UV 差分计算顶点切线（Mikktspace 简化版）。
        /// 结果不直接写入 RenderVertex（当前顶点格式无切线槽位），
        /// 但保留计算逻辑供后续扩展。
        /// </summary>
        private static void ComputeTangents(ImportedMeshData data)
        {
            var verts = data.Vertices;
            var indices = data.Indices;
            var tan1 = new Vector3[verts.Count];
            var tan2 = new Vector3[verts.Count];

            for (int i = 0; i < indices.Count; i += 3)
            {
                if (i + 2 >= indices.Count) break;

                int i0 = (int)indices[i];
                int i1 = (int)indices[i + 1];
                int i2 = (int)indices[i + 2];

                if (i0 >= verts.Count || i1 >= verts.Count || i2 >= verts.Count)
                    continue;

                var v0 = verts[i0].Position;
                var v1 = verts[i1].Position;
                var v2 = verts[i2].Position;

                var w0 = verts[i0].TexCoord;
                var w1 = verts[i1].TexCoord;
                var w2 = verts[i2].TexCoord;

                float x1 = v1.X - v0.X;
                float x2 = v2.X - v0.X;
                float y1 = v1.Y - v0.Y;
                float y2 = v2.Y - v0.Y;
                float z1 = v1.Z - v0.Z;
                float z2 = v2.Z - v0.Z;

                float s1 = w1.X - w0.X;
                float s2 = w2.X - w0.X;
                float t1 = w1.Y - w0.Y;
                float t2 = w2.Y - w0.Y;

                float det = s1 * t2 - s2 * t1;
                if (MathF.Abs(det) < 1e-6f)
                    continue;

                float r = 1.0f / det;
                var sdir = new Vector3((t2 * x1 - t1 * x2) * r, (t2 * y1 - t1 * y2) * r, (t2 * z1 - t1 * z2) * r);
                var tdir = new Vector3((s1 * x2 - s2 * x1) * r, (s1 * y2 - s2 * y1) * r, (s1 * z2 - s2 * z1) * r);

                tan1[i0] += sdir;
                tan1[i1] += sdir;
                tan1[i2] += sdir;

                tan2[i0] += tdir;
                tan2[i1] += tdir;
                tan2[i2] += tdir;
            }

            // Gram-Schmidt 正交化（结果目前仅作保留，未写入 RenderVertex）
            for (int i = 0; i < verts.Count; i++)
            {
                var n = verts[i].Normal;
                var t = tan1[i];
                var tangent = t - n * Vector3.Dot(n, t);

                if (tangent.LengthSquared() > 1e-12f)
                    tangent = Vector3.Normalize(tangent);
                else
                    tangent = Vector3.UnitX;

                // 手性判断（未存储，供后续扩展）
                // float w = Vector3.Dot(Vector3.Cross(n, tangent), tan2[i]) < 0.0f ? -1.0f : 1.0f;

                // 当前 RenderVertex 无切线槽位，保持原顶点不变
                _ = tangent;
            }
        }

        /// <summary>根据顶点位置计算轴对齐包围盒。</summary>
        private static AABB ComputeBounds(List<RenderVertex> vertices)
        {
            if (vertices == null || vertices.Count == 0)
                return new AABB(Vector3.Zero, Vector3.Zero);

            var min = vertices[0].Position;
            var max = vertices[0].Position;

            for (int i = 1; i < vertices.Count; i++)
            {
                var p = vertices[i].Position;
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }

            return new AABB(min, max);
        }
    }
}
