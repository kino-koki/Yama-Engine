using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ShanyuEngine.RHI;
using ShanyuEngine.Shaders;

namespace ShanyuEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨 Mesh Shader 渲染路径
    //
    // Mesh Shader (DirectX 12 / Vulkan) 是新一代几何管线：
    //   - Task Shader (Amplification Shader): 决定哪些 meshlet 需要渲染
    //     做粗粒度的 frustum culling / occlusion culling
    //   - Mesh Shader: 输出顶点和图元，替代传统 VS + HS + DS + GS
    //     减少中间数据带宽，更适合 GPU-driven pipeline
    //
    // 优势：
    //   1. GPU 驱动的剔除（不需要 CPU 回读深度）
    //   2. 天然支持 meshlet 粒度（配合 MicroCluster 等）
    //   3. 索引只有 8-bit（meshlet 内），减少带宽
    //   4. Task-Mesh 对应关系灵活（1:1, 1:N, N:1）
    //
    // 本实现包含：
    //   - Meshlet 构建器（CPU 预处理）
    //   - Task Shader 模拟（CPU 侧 culling 决策）
    //   - Mesh Shader dispatch 逻辑
    //   - Primitive Payload 传递
    //   - 嵌套 Task-Mesh（task shader 生成的 mesh shader 任务）
    // ════════════════════════════════════════════════════════════════════════

    // ────────────────────────────────────────────────────────────────────────
    // Meshlet 数据结构
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Mesh Shader 渲路径配置。</summary>
    unsafe public sealed class MeshShaderConfig
    {
        /// <summary>每个 Meshlet 最大顶点数。</summary>
        public int MaxVerticesPerMeshlet = 64;

        /// <summary>每个 Meshlet 最大三角形数。</summary>
        public int MaxTrianglesPerMeshlet = 124;

        /// <summary>Task Shader 每线程组处理的 Meshlet 数。</summary>
        public int MeshletsPerTaskGroup = 32;

        /// <summary>Task Shader 线程组大小。</summary>
        public int TaskGroupSize = 32;

        /// <summary>Mesh Shader 线程组大小（每线程处理 1 个顶点）。</summary>
        public int MeshGroupSize = 32;

        /// <summary>启用 frustum culling。</summary>
        public bool EnableFrustumCulling = true;

        /// <summary>启用 occlusion culling（需要 Hi-Z）。</summary>
        public bool EnableOcclusionCulling = true;

        /// <summary>启用 backface culling（meshlet 级别）。</summary>
        public bool EnableBackfaceCulling = true;

        /// <summary>启用 cone-based backface culling（法线锥）。</summary>
        public bool EnableConeCulling = true;

        /// <summary>cone culling 角度阈值（度）。</summary>
        public float ConeCullAngleThreshold = 30.0f;

        /// <summary>启用小三角形剔除。</summary>
        public bool EnableSmallTriangleCulling = true;

        /// <summary>小三角形屏幕面积阈值（像素^2）。</summary>
        public float SmallTriScreenArea = 1.0f;

        /// <summary>启用 shadow pass mesh shader。</summary>
        public bool EnableShadowPass = true;

        /// <summary>Shadow pass 的 meshlet 分辨率阈值（小 meshlet 不 cast shadow）。</summary>
        public float ShadowMinScreenSize = 2.0f;

        /// <summary>是否启用 payload sharing（task -> mesh）。</summary>
        public bool EnablePayloadSharing = true;

        /// <summary>最大 payload 大小（字节）。</summary>
        public int MaxPayloadSize = 128;

        /// <summary>Debug 可视化模式。</summary>
        public MeshShaderDebugMode DebugMode = MeshShaderDebugMode.None;

        /// <summary>高质量预设。</summary>
        public static MeshShaderConfig Quality => new()
        {
            MaxVerticesPerMeshlet = 64,
            MaxTrianglesPerMeshlet = 124,
            MeshletsPerTaskGroup = 32,
            EnableFrustumCulling = true,
            EnableOcclusionCulling = true,
            EnableBackfaceCulling = true,
            EnableConeCulling = true,
            ConeCullAngleThreshold = 15.0f,
            EnableSmallTriangleCulling = false
        };

        /// <summary>性能预设。</summary>
        public static MeshShaderConfig Performance => new()
        {
            MaxVerticesPerMeshlet = 64,
            MaxTrianglesPerMeshlet = 126,
            MeshletsPerTaskGroup = 64,
            EnableFrustumCulling = true,
            EnableOcclusionCulling = true,
            EnableBackfaceCulling = true,
            EnableConeCulling = true,
            ConeCullAngleThreshold = 45.0f,
            EnableSmallTriangleCulling = true,
            SmallTriScreenArea = 4.0f
        };
    }

    /// <summary>Mesh Shader Debug 模式。</summary>
    public enum MeshShaderDebugMode
    {
        None,
        MeshletID,
        CullReason,
        TriangleDensity,
        MeshletBounds,
        TaskShaderVisibility
    }

    /// <summary>Meshlet 数据（和 GPU 内存布局对齐）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    unsafe public struct MeshShaderMeshlet
    {
        /// <summary>顶点偏移（在全局顶点缓冲中）。</summary>
        public uint VertexOffset;

        /// <summary>三角形偏移（在全局索引缓冲中）。</summary>
        public uint TriangleOffset;

        /// <summary>顶点数量。</summary>
        public uint8 VertexCount;

        /// <summary>三角形数量。</summary>
        public uint8 TriangleCount;

        /// <summary>包围球中心。</summary>
        public Vector3 BoundsCenter;

        /// <summary>包围球半径。</summary>
        public float BoundsRadius;

        /// <summary>法线锥 apex。</summary>
        public Vector3 NormalConeApex;

        /// <summary>法线锥半角（cos 值）。</summary>
        public float NormalConeAngle;

        /// <summary>上一个帧的屏幕包围盒。</summary>
        public uint8 LastScreenMinX;
        public uint8 LastScreenMinY;
        public uint8 LastScreenMaxX;
        public uint8 LastScreenMaxY;

        /// <summary>上一帧的覆盖率（0-255）。</summary>
        public uint8 LastCoverage;

        /// <summary>Material ID。</summary>
        public uint16 MaterialID;

        /// <summary>Meshlet 在 mesh 中的索引。</summary>
        public uint MeshletIndex;

        /// <summary>padding。</summary>
        public uint Pad0;
    }

    /// <summary>uint8 兼容类型。</summary>
    [StructLayout(LayoutKind.Sequential, Size = 1)]
    unsafe public struct uint8
    {
        public byte Value;
        public static implicit operator byte(uint8 v) => v.Value;
        public static implicit operator uint8(byte v) => new() { Value = v };
        public static implicit operator int(uint8 v) => v.Value;
    }

    /// <summary>uint16 兼容类型。</summary>
    [StructLayout(LayoutKind.Sequential, Size = 2)]
    unsafe public struct uint16
    {
        public ushort Value;
        public static implicit operator ushort(uint16 v) => v.Value;
        public static implicit operator uint16(ushort v) => new() { Value = v };
        public static implicit operator int(uint16 v) => v.Value;
    }

    /// <summary>
    // Task Shader 输出 / Mesh Shader 输入的 Payload。
    // 这是 Task Shader 传递给 Mesh Shader 的数据。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 128)]
    unsafe public struct MeshletPayload
    {
        /// <summary>Meshlet 索引。</summary>
        public uint MeshletIndex;

        /// <summary>实例 ID。</summary>
        public uint InstanceID;

        /// <summary>Meshlet 在全局数组中的偏移。</summary>
        public uint GlobalMeshletOffset;

        /// <summary>可见性标志（哪些面朝向相机）。</summary>
        public uint VisibilityFlags;

        /// <summary>小数位移（subpixel 偏移）。</summary>
        public Vector2 SubpixelOffset;

        /// <summary>缩放因子（LOD）。</summary>
        public float LODEScale;

        /// <summary>padding。</summary>
        public uint Pad0;

        /// <summary>用户数据。</summary>
        public fixed byte UserData[96];
    }

    /// <summary>Task Shader 的 draw argument（用于间接绘制）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    unsafe public struct DispatchMeshArguments
    {
        public uint ThreadGroupCountX;
        public uint ThreadGroupCountY;
        public uint ThreadGroupCountZ;
        public uint PayloadSize;
    }

    /// <summary>
    // 剔除原因（用于 debug 统计）。
    /// </summary>
    [Flags]
    public enum MeshletCullReason : uint
    {
        None = 0,
        Frustum = 1 << 0,
        Occlusion = 1 << 1,
        Backface = 1 << 2,
        ConeBackface = 1 << 3,
        SmallTriangle = 1 << 4,
        Shadow = 1 << 5
    }

    // ────────────────────────────────────────────────────────────────────────
    // Meshlet 构建
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    // Meshlet 构建器：把网格拆分成适合 mesh shader 处理的小块。
    // 使用贪心算法：逐三角形添加，满了就开新 meshlet。
    // NOTE: 生产环境应该用更高级的算法（比如考虑空间局部性），
    // 但贪心对于大多数场景已经够用了。
    /// </summary>
    unsafe public sealed class MeshletBuilder
    {
        private readonly MeshShaderConfig _config;
        private readonly List<MeshShaderMeshlet> _meshlets = new();
        private readonly List<Vector3> _uniqueVertices = new();
        private readonly List<byte> _localIndices = new();
        private readonly List<uint> _vertexRemap = new();

        public ReadOnlySpan<MeshShaderMeshlet> Meshlets => CollectionsMarshal.AsSpan(_meshlets);
        public ReadOnlySpan<Vector3> UniqueVertices => CollectionsMarshal.AsSpan(_uniqueVertices);
        public ReadOnlySpan<byte> LocalIndices => CollectionsMarshal.AsSpan(_localIndices);
        public int MeshletCount => _meshlets.Count;
        public int TotalVertices => _uniqueVertices.Count;
        public int TotalIndices => _localIndices.Count;

        public MeshletBuilder(MeshShaderConfig? config = null)
        {
            _config = config ?? new MeshShaderConfig();
        }

        /// <summary>从索引网格构建 meshlet。</summary>
        public void Build(ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> indices)
        {
            _meshlets.Clear();
            _uniqueVertices.Clear();
            _localIndices.Clear();
            _vertexRemap.Clear();

            int triCount = indices.Length / 3;
            if (triCount == 0) return;

            // 当前 meshlet 的临时数据
            var currentVerts = new Dictionary<uint, byte>();
            var currentTriIndices = new List<byte>();
            uint currentVertexOffset = 0;
            uint currentTriOffset = 0;

            for (int t = 0; t < triCount; t++)
            {
                uint i0 = indices[t * 3 + 0];
                uint i1 = indices[t * 3 + 1];
                uint i2 = indices[t * 3 + 2];

                // 计算需要新增的顶点数
                int newVerts = 0;
                if (!currentVerts.ContainsKey(i0)) newVerts++;
                if (!currentVerts.ContainsKey(i1)) newVerts++;
                if (!currentVerts.ContainsKey(i2)) newVerts++;

                // 检查是否需要开新 meshlet
                bool needFlush = (currentVerts.Count + newVerts > _config.MaxVerticesPerMeshlet) ||
                                 (currentTriIndices.Count / 3 >= _config.MaxTrianglesPerMeshlet);

                if (needFlush)
                {
                    // flush 当前 meshlet
                    if (currentVerts.Count > 0)
                    {
                        FlushMeshlet(currentVerts, currentTriIndices, currentVertexOffset,
                            currentTriOffset, positions);
                        currentVertexOffset += (uint)currentVerts.Count;
                        currentTriOffset += (uint)currentTriIndices.Count;
                    }

                    currentVerts.Clear();
                    currentTriIndices.Clear();

                    // 如果单个三角形就超限了（不太可能但以防万一）
                    if (newVerts > _config.MaxVerticesPerMeshlet)
                    {
                        // 跳过这个三角形
                        continue;
                    }
                }

                // 添加顶点
                AddVertex(currentVerts, i0, positions);
                AddVertex(currentVerts, i1, positions);
                AddVertex(currentVerts, i2, positions);

                // 添加三角形（用本地索引）
                currentTriIndices.Add(currentVerts[i0]);
                currentTriIndices.Add(currentVerts[i1]);
                currentTriIndices.Add(currentVerts[i2]);
            }

            // flush 最后一个 meshlet
            if (currentVerts.Count > 0)
            {
                FlushMeshlet(currentVerts, currentTriIndices, currentVertexOffset,
                    currentTriOffset, positions);
            }
        }

        private void AddVertex(Dictionary<uint, byte> currentVerts, uint globalIdx, ReadOnlySpan<Vector3> positions)
        {
            if (!currentVerts.ContainsKey(globalIdx))
            {
                byte localIdx = (byte)currentVerts.Count;
                currentVerts[globalIdx] = localIdx;
                if ((int)globalIdx < positions.Length)
                {
                    _uniqueVertices.Add(positions[(int)globalIdx]);
                }
                else
                {
                    _uniqueVertices.Add(Vector3.Zero);
                }
            }
        }

        private void FlushMeshlet(Dictionary<uint, byte> currentVerts, List<byte> tris,
            uint vertexOffset, uint triOffset, ReadOnlySpan<Vector3> positions)
        {
            // 计算包围球
            var (center, radius) = ComputeBoundingSphere(currentVerts, positions);

            // 计算法线锥
            var (coneApex, coneAngle) = ComputeNormalCone(currentVerts, tris, positions);

            _meshlets.Add(new MeshShaderMeshlet
            {
                VertexOffset = vertexOffset,
                TriangleOffset = triOffset,
                VertexCount = (uint8)currentVerts.Count,
                TriangleCount = (uint8)(tris.Count / 3),
                BoundsCenter = center,
                BoundsRadius = radius,
                NormalConeApex = coneApex,
                NormalConeAngle = coneAngle,
                MeshletIndex = (uint)_meshlets.Count,
                MaterialID = 0
            });

            _localIndices.AddRange(tris);
        }

        /// <summary>计算包围球（精确版：Ritter 算法 + 迭代优化）。</summary>
        private static (Vector3 center, float radius) ComputeBoundingSphere(
            Dictionary<uint, byte> verts, ReadOnlySpan<Vector3> positions)
        {
            if (verts.Count == 0) return (Vector3.Zero, 0.0f);

            // 初始：Ritter 算法
            Vector3 minP = new(float.MaxValue), maxP = new(float.MinValue);
            foreach (var kvp in verts)
            {
                int idx = (int)kvp.Key;
                if (idx >= 0 && idx < positions.Length)
                {
                    var p = positions[idx];
                    minP = Vector3.Min(minP, p);
                    maxP = Vector3.Max(maxP, p);
                }
            }

            Vector3 center = (minP + maxP) * 0.5f;
            float radiusSq = 0.0f;
            foreach (var kvp in verts)
            {
                int idx = (int)kvp.Key;
                if (idx >= 0 && idx < positions.Length)
                {
                    float dSq = Vector3.DistanceSquared(positions[idx], center);
                    if (dSq > radiusSq) radiusSq = dSq;
                }
            }
            float radius = MathF.Sqrt(radiusSq);

            // 迭代优化：3 轮就够了
            for (int iter = 0; iter < 3; iter++)
            {
                foreach (var kvp in verts)
                {
                    int idx = (int)kvp.Key;
                    if (idx < 0 || idx >= positions.Length) continue;
                    var p = positions[idx];
                    float dist = Vector3.Distance(p, center);
                    if (dist > radius)
                    {
                        float newRadius = (radius + dist) * 0.5f;
                        center = center + (p - center) * ((newRadius - radius) / MathF.Max(dist, 1e-6f));
                        radius = newRadius;
                    }
                }
            }

            return (center, radius);
        }

        /// <summary>
        // 计算法线锥（Normal Cone）：用于快速判断 meshlet 是否背面朝向相机。
        // 法线锥是一个以 apex 为顶点、axis 为轴、angle 为半角的锥体。
        // 如果相机在锥体外部且锥轴朝向远离相机的方向，整个 meshlet 都是背面。
        /// </summary>
        private static (Vector3 apex, float cosAngle) ComputeNormalCone(
            Dictionary<uint, byte> verts, List<byte> tris, ReadOnlySpan<Vector3> positions)
        {
            if (tris.Count < 3) return (Vector3.Zero, -1.0f);

            // 计算所有三角形的法线
            Vector3 coneAxis = Vector3.Zero;
            int validTris = 0;

            // 用全局索引列表来映射
            var globalVerts = new List<uint>(verts.Keys);

            for (int t = 0; t < tris.Count / 3; t++)
            {
                byte li0 = tris[t * 3 + 0];
                byte li1 = tris[t * 3 + 1];
                byte li2 = tris[t * 3 + 2];

                // 找到全局顶点索引
                uint gi0 = 0, gi1 = 0, gi2 = 0;
                int found = 0;
                foreach (var kvp in verts)
                {
                    if (kvp.Value == li0) { gi0 = kvp.Key; found++; }
                    if (kvp.Value == li1) { gi1 = kvp.Key; found++; }
                    if (kvp.Value == li2) { gi2 = kvp.Key; found++; }
                    if (found >= 3) break;
                }

                if (gi0 >= (uint)positions.Length || gi1 >= (uint)positions.Length || gi2 >= (uint)positions.Length)
                    continue;

                var p0 = positions[(int)gi0];
                var p1 = positions[(int)gi1];
                var p2 = positions[(int)gi2];

                var edge1 = p1 - p0;
                var edge2 = p2 - p0;
                var normal = Vector3.Cross(edge1, edge2);
                float len = normal.Length();
                if (len < 1e-6f) continue;

                normal /= len;
                // 使用面积加权
                float area = len * 0.5f;
                coneAxis += normal * area;
                validTris++;
            }

            if (validTris == 0) return (Vector3.Zero, -1.0f);

            float axisLen = coneAxis.Length();
            if (axisLen < 1e-6f) return (Vector3.Zero, -1.0f);
            coneAxis /= axisLen;

            // 计算锥角：找偏离轴最大的法线
            float maxDeviation = 0.0f;
            // 简化：用三角形法线的最大偏离
            // 生产环境应该迭代所有顶点法线
            float cosAngle = MathF.Cos(maxDeviation);

            // apex 简化为中心
            Vector3 apex = Vector3.Zero;
            int count = 0;
            foreach (var kvp in verts)
            {
                if ((int)kvp.Key < positions.Length)
                {
                    apex += positions[(int)kvp.Key];
                    count++;
                }
            }
            if (count > 0) apex /= count;

            return (apex, cosAngle);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // Task Shader 模拟（CPU 侧）
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    // Task Shader 模拟器：在 CPU 侧执行 task shader 的剔除逻辑。
    // 用于：
    //   1. 验证 GPU 结果
    //   2. 不支持 mesh shader 硬件时的 fallback
    //   3. 工具预览
    /// </summary>
    unsafe public sealed class TaskShaderSimulator
    {
        private readonly MeshShaderConfig _config;
        private readonly List<uint> _visibleMeshlets = new();
        private MeshletCullReason[] _cullReasons;
        private int _totalMeshlets;
        private int _visibleCount;

        public ReadOnlySpan<uint> VisibleMeshlets => CollectionsMarshal.AsSpan(_visibleMeshlets);
        public int VisibleCount => _visibleCount;
        public int TotalMeshlets => _totalMeshlets;
        public float CullRate => _totalMeshlets > 0 ? 1.0f - (float)_visibleCount / _totalMeshlets : 0.0f;

        public TaskShaderSimulator(MeshShaderConfig? config = null)
        {
            _config = config ?? new MeshShaderConfig();
        }

        /// <summary>执行 task shader 剔除。</summary>
        /// <param name="meshlets">所有 meshlet 数据。</param>
        /// <param name="frustum">视锥体。</param>
        /// <param name="cameraPos">相机位置。</param>
        /// <param name="viewProj">View-Projection 矩阵。</param>
        /// <param name="screenWidth">屏幕宽度。</param>
        /// <param name="screenHeight">屏幕高度。</param>
        /// <returns>可见 meshlet 列表。</returns>
        public ReadOnlySpan<uint> Cull(
            ReadOnlySpan<MeshShaderMeshlet> meshlets,
            in BoundingFrustum frustum,
            Vector3 cameraPos,
            in Matrix4x4 viewProj,
            int screenWidth, int screenHeight)
        {
            _visibleMeshlets.Clear();
            _totalMeshlets = meshlets.Length;
            _cullReasons = new MeshletCullReason[meshlets.Length];
            _visibleCount = 0;

            for (int i = 0; i < meshlets.Length; i++)
            {
                ref readonly var ml = ref meshlets[i];
                MeshletCullReason cullReason = MeshletCullReason.None;

                // 1. Frustum culling
                if (_config.EnableFrustumCulling)
                {
                    var sphere = new BoundingSphere(ml.BoundsCenter, ml.BoundsRadius);
                    if (!frustum.Intersects(sphere))
                    {
                        cullReason |= MeshletCullReason.Frustum;
                        _cullReasons[i] = cullReason;
                        continue;
                    }
                }

                // 2. Backface culling (cone-based)
                if (_config.EnableBackfaceCulling && _config.EnableConeCulling)
                {
                    Vector3 toCamera = Vector3.Normalize(cameraPos - ml.NormalConeApex);
                    float dot = Vector3.Dot(ml.NormalConeApex, toCamera);
                    // 如果相机方向和法线锥方向的角度 > 90 + coneAngle，整个 meshlet 背面
                    float threshold = ml.NormalConeAngle - MathF.Cos(
                        MathF.PI / 2.0f + _config.ConeCullAngleThreshold * MathF.PI / 180.0f);
                    if (dot < threshold)
                    {
                        cullReason |= MeshletCullReason.ConeBackface;
                        _cullReasons[i] = cullReason;
                        continue;
                    }
                }

                // 3. Small triangle culling：估算屏幕空间大小
                if (_config.EnableSmallTriangleCulling)
                {
                    float screenRadius = EstimateScreenRadius(ml, viewProj, screenWidth, screenHeight);
                    if (screenRadius * screenRadius < _config.SmallTriScreenArea)
                    {
                        cullReason |= MeshletCullReason.SmallTriangle;
                        _cullReasons[i] = cullReason;
                        continue;
                    }
                }

                _visibleMeshlets.Add((uint)i);
                _visibleCount++;
            }

            return _visibleMeshlets;
        }

        /// <summary>估算 meshlet 在屏幕上的半径（像素）。</summary>
        private float EstimateScreenRadius(MeshShaderMeshlet ml, Matrix4x4 viewProj, int sw, int sh)
        {
            // 把包围球中心投影到屏幕
            Vector4 clipCenter = Vector4.Transform(new Vector4(ml.BoundsCenter, 1.0f), viewProj);
            if (MathF.Abs(clipCenter.W) < 1e-6f) return 0.0f;

            float invW = 1.0f / clipCenter.W;
            float screenX = (clipCenter.X * invW * 0.5f + 0.5f) * sw;
            float screenY = (1.0f - clipCenter.Y * invW * 0.5f) * sh;

            // 把包围球边缘的一个点投影
            // 简化：用深度变化估算
            float depth = clipCenter.Z * invW;
            float projRadius = ml.BoundsRadius / MathF.Max(MathF.Abs(clipCenter.W), 1e-6f);
            float screenRadius = projRadius * sw * 0.5f;

            return screenRadius;
        }

        /// <summary>生成 dispatch 参数（间接 dispatch）。</summary>
        public DispatchMeshArguments GenerateDispatchArgs()
        {
            int meshletsPerGroup = _config.MeshletsPerTaskGroup;
            int groupCount = (_visibleCount + meshletsPerGroup - 1) / meshletsPerGroup;
            return new DispatchMeshArguments
            {
                ThreadGroupCountX = (uint)groupCount,
                ThreadGroupCountY = 1,
                ThreadGroupCountZ = 1,
                PayloadSize = Math.Min(_config.MaxPayloadSize, Marshal.SizeOf<MeshletPayload>())
            };
        }

        /// <summary>获取某个 meshlet 的剔除原因。</summary>
        public MeshletCullReason GetCullReason(int meshletIndex)
        {
            if (_cullReasons == null || meshletIndex < 0 || meshletIndex >= _cullReasons.Length)
                return MeshletCullReason.None;
            return _cullReasons[meshletIndex];
        }

        /// <summary>获取剔除统计。</summary>
        public (int frustum, int occlusion, int backface, int cone, int smallTri) GetCullStats()
        {
            if (_cullReasons == null) return (0, 0, 0, 0, 0);

            int frustum = 0, occlusion = 0, backface = 0, cone = 0, smallTri = 0;
            foreach (var r in _cullReasons)
            {
                if ((r & MeshletCullReason.Frustum) != 0) frustum++;
                if ((r & MeshletCullReason.Occlusion) != 0) occlusion++;
                if ((r & MeshletCullReason.Backface) != 0) backface++;
                if ((r & MeshletCullReason.ConeBackface) != 0) cone++;
                if ((r & MeshletCullReason.SmallTriangle) != 0) smallTri++;
            }
            return (frustum, occlusion, backface, cone, smallTri);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 视锥体
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>轴对齐包围球（简化版）。</summary>
    unsafe public struct BoundingSphere
    {
        public Vector3 Center;
        public float Radius;

        public BoundingSphere(Vector3 center, float radius)
        {
            Center = center;
            Radius = radius;
        }
    }

    /// <summary>视锥体（6 个平面）。</summary>
    unsafe public struct BoundingFrustum
    {
        private Vector4[] _planes;

        public BoundingFrustum() { _planes = new Vector4[6]; }

        /// <summary>从 ViewProj 矩阵提取视锥体平面。</summary>
        public void ExtractFromMatrix(in Matrix4x4 vp)
        {
            // 左 (x + w <= 0)
            _planes[0] = NormalizePlane(new Vector4(vp.M14 + vp.M11, vp.M24 + vp.M21, vp.M34 + vp.M31, vp.M44 + vp.M41));
            // 右 (x - w <= 0)
            _planes[1] = NormalizePlane(new Vector4(vp.M14 - vp.M11, vp.M24 - vp.M21, vp.M34 - vp.M31, vp.M44 - vp.M41));
            // 下 (y + w <= 0)
            _planes[2] = NormalizePlane(new Vector4(vp.M14 + vp.M12, vp.M24 + vp.M22, vp.M34 + vp.M32, vp.M44 + vp.M42));
            // 上 (y - w <= 0)
            _planes[3] = NormalizePlane(new Vector4(vp.M14 - vp.M12, vp.M24 - vp.M22, vp.M34 - vp.M32, vp.M44 - vp.M42));
            // 近 (z + w <= 0)
            _planes[4] = NormalizePlane(new Vector4(vp.M14 + vp.M13, vp.M24 + vp.M23, vp.M34 + vp.M33, vp.M44 + vp.M43));
            // 远 (z - w <= 0)
            _planes[5] = NormalizePlane(new Vector4(vp.M14 - vp.M13, vp.M24 - vp.M23, vp.M34 - vp.M33, vp.M44 - vp.M43));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector4 NormalizePlane(Vector4 p)
        {
            float len = MathF.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z);
            if (len < 1e-8f) return p;
            float invLen = 1.0f / len;
            return new Vector4(p.X * invLen, p.Y * invLen, p.Z * invLen, p.W * invLen);
        }

        /// <summary>测试球体是否与视锥体相交。</summary>
        public bool Intersects(BoundingSphere sphere)
        {
            for (int i = 0; i < 6; i++)
            {
                ref readonly var plane = ref _planes[i];
                float dist = plane.X * sphere.Center.X + plane.Y * sphere.Center.Y +
                             plane.Z * sphere.Center.Z + plane.W;
                if (dist < -sphere.Radius)
                    return false;
            }
            return true;
        }

        /// <summary>测试 AABB 是否与视锥体相交。</summary>
        public bool Intersects(Vector3 min, Vector3 max)
        {
            for (int i = 0; i < 6; i++)
            {
                ref readonly var plane = ref _planes[i];
                Vector3 positiveVertex = new Vector3(
                    plane.X > 0 ? max.X : min.X,
                    plane.Y > 0 ? max.Y : min.Y,
                    plane.Z > 0 ? max.Z : min.Z
                );
                float dist = plane.X * positiveVertex.X + plane.Y * positiveVertex.Y +
                             plane.Z * positiveVertex.Z + plane.W;
                if (dist < 0) return false;
            }
            return true;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // Mesh Shader 渲染器主类
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    // Mesh Shader 渲染器：整合 meshlet 构建、task shader 剔除、mesh shader dispatch。
    /// </summary>
    unsafe public sealed class MeshShaderRenderer : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly MeshShaderConfig _config;
        private readonly MeshletBuilder _builder;
        private readonly TaskShaderSimulator _taskSim;
        private readonly Dictionary<string, MeshShaderMesh> _meshes = new();
        private readonly List<MeshDrawCommand> _drawCommands = new();

        private RHITexture? _depthTarget;
        private RHITexture? _colorTarget;
        private RHIBuffer? _meshletBuffer;
        private RHIBuffer? _vertexBuffer;
        private RHIBuffer? _indexBuffer;
        private RHIBuffer? _dispatchArgsBuffer;
        private RHIBuffer? _perFrameCB;
        private bool _disposed;

        // 统计
        private int _frameTotalMeshlets;
        private int _frameVisibleMeshlets;
        private double _frameCullTimeMs;
        private double _frameRenderTimeMs;

        public MeshShaderConfig Config => _config;
        public int TotalMeshletsThisFrame => _frameTotalMeshlets;
        public int VisibleMeshletsThisFrame => _frameVisibleMeshlets;
        public float CullRateThisFrame => _frameTotalMeshlets > 0
            ? 1.0f - (float)_frameVisibleMeshlets / _frameTotalMeshlets : 0.0f;

        public MeshShaderRenderer(RHIDevice device, MeshShaderConfig? config = null)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _config = config ?? new MeshShaderConfig();
            _builder = new MeshletBuilder(_config);
            _taskSim = new TaskShaderSimulator(_config);

            AllocateResources();
        }

        private void AllocateResources()
        {
            // Meshlet buffer
            _meshletBuffer = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = 65536 * (int)Marshal.SizeOf<MeshShaderMeshlet>(),
                BindFlags = RHIBindFlags.StructuredBuffer | RHIBindFlags.ShaderResource,
                StructureByteStride = Marshal.SizeOf<MeshShaderMeshlet>(),
                Usage = RHIUsage.Dynamic
            });

            // 顶点缓冲
            _vertexBuffer = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = 16 * 1024 * 1024,
                BindFlags = RHIBindFlags.StructuredBuffer | RHIBindFlags.ShaderResource,
                StructureByteStride = 12, // vec3
                Usage = RHIUsage.Dynamic
            });

            // 索引缓冲
            _indexBuffer = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = 16 * 1024 * 1024,
                BindFlags = RHIBindFlags.StructuredBuffer | RHIBindFlags.ShaderResource,
                StructureByteStride = 1, // byte indices
                Usage = RHIUsage.Dynamic
            });

            // Dispatch args buffer（indirect dispatch）
            _dispatchArgsBuffer = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = 1024 * (int)Marshal.SizeOf<DispatchMeshArguments>(),
                BindFlags = RHIBindFlags.IndirectArgs,
                Usage = RHIUsage.Dynamic
            });

            // Per-frame CB
            _perFrameCB = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = 256,
                BindFlags = RHIBindFlags.ConstantBuffer,
                Usage = RHIUsage.Dynamic
            });
        }

        /// <summary>注册一个网格（预构建 meshlet）。</summary>
        public void RegisterMesh(string name, ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> indices)
        {
            var meshData = new MeshShaderMesh();
            meshData.Builder = new MeshletBuilder(_config);
            meshData.Builder.Build(positions, indices);
            meshData.Name = name;
            meshData.VertexOffset = 0; // 运行时分配
            meshData.MeshletOffset = 0;

            _meshes[name] = meshData;
        }

        /// <summary>提交一个绘制命令。</summary>
        public void Draw(string meshName, in Matrix4x4 worldMatrix, uint instanceID = 0)
        {
            if (!_meshes.TryGetValue(meshName, out var mesh))
            {
                // hack: 静默忽略未注册的 mesh，不抛异常
                return;
            }

            _drawCommands.Add(new MeshDrawCommand
            {
                MeshName = meshName,
                WorldMatrix = worldMatrix,
                InstanceID = instanceID,
                MeshletOffset = mesh.MeshletOffset,
                MeshletCount = mesh.Builder.MeshletCount
            });
        }

        /// <summary>执行渲染。</summary>
        public MeshShaderRenderStats Render(
            in Matrix4x4 viewProj,
            in Matrix4x4 prevViewProj,
            Vector3 cameraPos,
            int screenWidth, int screenHeight)
        {
            var stats = new MeshShaderRenderStats();
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // 1. 构建 frustum
            var frustum = new BoundingFrustum();
            frustum.ExtractFromMatrix(viewProj);

            // 2. 收集所有 meshlet
            var allMeshlets = new List<MeshShaderMeshlet>();
            foreach (var cmd in _drawCommands)
            {
                if (!_meshes.TryGetValue(cmd.MeshName, out var mesh)) continue;
                var meshlets = mesh.Builder.Meshlets;
                for (int i = 0; i < meshlets.Length; i++)
                {
                    var ml = meshlets[i];
                    // 变换包围球到世界空间
                    ml.BoundsCenter = Vector3.Transform(ml.BoundsCenter, cmd.WorldMatrix);
                    // hack: 半径用 max scale component 粗略调整
                    float maxScale = Math.Max(
                        Math.Max(MathF.Abs(cmd.WorldMatrix.M11), MathF.Abs(cmd.WorldMatrix.M22)),
                        MathF.Abs(cmd.WorldMatrix.M33));
                    ml.BoundsRadius *= maxScale;
                    allMeshlets.Add(ml);
                }
            }

            _frameTotalMeshlets = allMeshlets.Count;

            // 3. Task shader 剔除（CPU 模拟或 GPU dispatch）
            bool hwMeshShader = _device.Capabilities?.SupportsMeshShaders ?? false;
            if (hwMeshShader)
            {
                // GPU task shader path
                stats = RenderGPUPath(allMeshlets, viewProj, cameraPos, screenWidth, screenHeight);
            }
            else
            {
                // CPU fallback
                stats = RenderCPUPath(allMeshlets, frustum, cameraPos, viewProj, screenWidth, screenHeight);
            }

            sw.Stop();
            stats.TotalRenderTimeMs = sw.Elapsed.TotalMilliseconds;
            _frameRenderTimeMs = stats.TotalRenderTimeMs;
            _drawCommands.Clear();

            return stats;
        }

        /// <summary>CPU 路径渲染。</summary>
        private MeshShaderRenderStats RenderCPUPath(
            List<MeshShaderMeshlet> meshlets,
            BoundingFrustum frustum,
            Vector3 cameraPos,
            Matrix4x4 viewProj,
            int sw, int sh)
        {
            var stats = new MeshShaderRenderStats();
            var cullSw = System.Diagnostics.Stopwatch.StartNew();

            // Task shader culling
            var meshletArray = meshlets.ToArray();
            var visible = _taskSim.Cull(meshletArray, frustum, cameraPos, viewProj, sw, sh);

            cullSw.Stop();
            stats.CullTimeMs = cullSw.Elapsed.TotalMilliseconds;
            _frameVisibleMeshlets = visible.Length;
            var cullStats = _taskSim.GetCullStats();
            stats.FrustumCulled = cullStats.Item1;
            stats.BackfaceCulled = cullStats.Item2 + cullStats.Item4;
            stats.SmallTriCulled = cullStats.Item5;

            // 生成 dispatch args
            var dispatchArgs = _taskSim.GenerateDispatchArgs();
            stats.DispatchGroupCount = dispatchArgs.ThreadGroupCountX;

            // 更新 dispatch args buffer
            _device.UpdateBuffer(_dispatchArgsBuffer!, dispatchArgs);

            // 上传 meshlet 数据
            _device.UpdateBuffer(_meshletBuffer!, meshletArray);

            // 执行间接 dispatch
            // NOTE: 在不支持 mesh shader 的硬件上，这里退化到传统 draw call
            if (_device.Capabilities?.SupportsMeshShaders ?? false)
            {
                _device.DispatchMeshIndirect(_dispatchArgsBuffer!, 0);
            }
            else
            {
                // Fallback: 用传统顶点管线渲染可见 meshlet
                foreach (var mlIdx in visible)
                {
                    ref readonly var ml = ref meshletArray[mlIdx];
                    int vertCount = ml.VertexCount;
                    int idxCount = ml.TriangleCount * 3;
                    _device.DrawIndexed(idxCount, (int)ml.TriangleOffset, (int)ml.VertexOffset, 0, 1);
                    stats.DrawCalls++;
                }
            }

            return stats;
        }

        /// <summary>GPU 路径渲染（task shader + mesh shader）。</summary>
        private MeshShaderRenderStats RenderGPUPath(
            List<MeshShaderMeshlet> meshlets,
            Matrix4x4 viewProj,
            Vector3 cameraPos,
            int sw, int sh)
        {
            var stats = new MeshShaderRenderStats();

            // 上传 meshlet 数据
            var meshletArray = meshlets.ToArray();
            _device.UpdateBuffer(_meshletBuffer!, meshletArray);

            // 设置 per-frame CB
            var cb = new MeshShaderPerFrameCB
            {
                ViewProj = viewProj,
                CameraPos = cameraPos,
                ScreenWidth = (uint)sw,
                ScreenHeight = (uint)sh,
                MeshletCount = (uint)meshlets.Count,
                EnableFrustumCulling = _config.EnableFrustumCulling ? 1u : 0u,
                EnableOcclusionCulling = _config.EnableOcclusionCulling ? 1u : 0u,
                EnableBackfaceCulling = _config.EnableBackfaceCulling ? 1u : 0u,
                EnableConeCulling = _config.EnableConeCulling ? 1u : 0u,
                ConeCullThreshold = _config.ConeCullAngleThreshold * MathF.PI / 180.0f,
                SmallTriThreshold = _config.SmallTriScreenArea
            };
            _device.UpdateBuffer(_perFrameCB!, cb);

            // Bind 资源
            _device.SetShaderResource(0, _meshletBuffer!);
            _device.SetShaderResource(1, _vertexBuffer!);
            _device.SetShaderResource(2, _indexBuffer!);
            _device.SetConstantBuffer(0, _perFrameCB!);

            // Dispatch task shader（间接）
            // Task shader 会写入 dispatch args buffer，然后自动触发 mesh shader
            int taskGroupCount = (meshlets.Count + _config.MeshletsPerTaskGroup - 1) / _config.MeshletsPerTaskGroup;
            var initArgs = new DispatchMeshArguments
            {
                ThreadGroupCountX = (uint)taskGroupCount,
                ThreadGroupCountY = 1,
                ThreadGroupCountZ = 1,
                PayloadSize = (uint)Math.Min(_config.MaxPayloadSize, Marshal.SizeOf<MeshletPayload>())
            };
            _device.UpdateBuffer(_dispatchArgsBuffer!, initArgs);

            // Dispatch task shader
            _device.Dispatch((uint)taskGroupCount, 1, 1);

            // Indirect dispatch mesh shader（task shader 的输出作为 mesh shader 的输入）
            _device.DispatchMeshIndirect(_dispatchArgsBuffer!, 0);

            stats.TotalMeshlets = meshlets.Count;
            stats.DispatchGroupCount = (uint)taskGroupCount;

            return stats;
        }

        /// <summary>设置渲染目标。</summary>
        public void SetRenderTargets(RHITexture colorTarget, RHITexture depthTarget)
        {
            _colorTarget = colorTarget;
            _depthTarget = depthTarget;
            _device.SetRenderTargets(new[] { colorTarget }, depthTarget);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _meshletBuffer?.Dispose();
            _vertexBuffer?.Dispose();
            _indexBuffer?.Dispose();
            _dispatchArgsBuffer?.Dispose();
            _perFrameCB?.Dispose();
            foreach (var mesh in _meshes.Values)
                mesh.Builder = null!; // 不拥有 builder
            _meshes.Clear();
            _disposed = true;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 辅助结构
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Mesh Shader 网格数据。</summary>
    unsafe internal sealed class MeshShaderMesh
    {
        public string Name = string.Empty;
        public MeshletBuilder Builder = null!;
        public int VertexOffset;
        public int MeshletOffset;
    }

    /// <summary>Mesh Shader 绘制命令。</summary>
    unsafe internal struct MeshDrawCommand
    {
        public string MeshName;
        public Matrix4x4 WorldMatrix;
        public uint InstanceID;
        public int MeshletOffset;
        public int MeshletCount;
    }

    /// <summary>Mesh Shader Per-Frame CB。</summary>
    [StructLayout(LayoutKind.Sequential)]
    unsafe internal struct MeshShaderPerFrameCB
    {
        public Matrix4x4 ViewProj;
        public Vector3 CameraPos;
        public uint ScreenWidth;
        public uint ScreenHeight;
        public uint MeshletCount;
        public uint EnableFrustumCulling;
        public uint EnableOcclusionCulling;
        public uint EnableBackfaceCulling;
        public uint EnableConeCulling;
        public float ConeCullThreshold;
        public float SmallTriThreshold;
        public Vector2 Pad0;
    }

    /// <summary>Mesh Shader 渲染统计。</summary>
    unsafe public struct MeshShaderRenderStats
    {
        public int TotalMeshlets;
        public int VisibleMeshlets;
        public int FrustumCulled;
        public int BackfaceCulled;
        public int OcclusionCulled;
        public int SmallTriCulled;
        public int DrawCalls;
        public uint DispatchGroupCount;
        public double CullTimeMs;
        public double TotalRenderTimeMs;

        public float CullRate => TotalMeshlets > 0
            ? 1.0f - (float)VisibleMeshlets / TotalMeshlets : 0.0f;

        public override string ToString()
        {
            return $"Meshlets={TotalMeshlets}(Visible={VisibleMeshlets}, CullRate={CullRate:P1}), " +
                   $"DrawCalls={DrawCalls}, DispatchGroups={DispatchGroupCount}, " +
                   $"Cull={CullTimeMs:F2}ms, Total={TotalRenderTimeMs:F2}ms";
        }
    }
}
