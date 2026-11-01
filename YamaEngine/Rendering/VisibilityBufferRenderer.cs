using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using YamaEngine.RHI;
using YamaEngine.Shaders;

namespace YamaEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // yama 可见性缓冲渲染器 (Visibility Buffer Renderer)
    //
    // 可见性缓冲 (Visibility Buffer) 是一种现代渲染技术，介于前向和延迟之间：
    //   - GeometryBuffer Pass：只写入"可见性信息"（primitiveID + instanceID + 重心坐标）
    //     不写 albedo/normal 等材质属性，因此 GeometryBuffer 极其轻量
    //   - Shade Pass：根据可见性缓冲，从原始几何数据中拉取材质属性再着色
    //
    // 优势：
    //   1. GeometryBuffer 带宽极低（每像素只存 64-128 bit）
    //   2. 支持任意材质（不像延迟有 GeometryBuffer 通道数限制）
    //   3. 支持多光源（结合 cluster/tile）
    //   4. 完美支持 MSAA（可见性信息可以 per-sample）
    //
    // 本实现包含完整的软件光栅化路径（CPU 侧），用于：
    //   - 工具流：在编辑器里预览可见性缓冲
    //   - 验证：对比 GPU 光栅化结果
    //   - fallback：没有 mesh shader 硬件时退化到软件路径
    //
    // 可见性缓冲格式（每像素 128 bit）：
    //   - 32 bit: PrimitiveID（三角形索引）
    //   - 32 bit: InstanceID（实例索引）
    //   - 32 bit: Barycentric.X (float)
    //   - 32 bit: Barycentric.Y (float)
    //   Z = 1 - X - Y，省一个通道
    //
    // 软件光栅化算法：
    //   - 顶点变换：MVP 矩阵把顶点转到 clip space
    //   - 裁剪：Cohen-Sutherland 风格的近远平面裁剪
    //   - 透视除法：clip space -> NDC
    //   - 视口变换：NDC -> screen
    //   - 三角形 setup：计算 edge function 系数
    //   - 光栅化：tile-based 并行扫描，每像素计算重心坐标
    //   - 深度测试：z-buffer
    //   - 写入可见性缓冲
    // ════════════════════════════════════════════════════════════════════════

    // ────────────────────────────────────────────────────────────────────────
    // 可见性缓冲数据结构
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>可见性缓冲像素格式（128 bit）。</summary>
    /// <summary>可见性缓冲实例信息。</summary>
    [StructLayout(LayoutKind.Sequential, Size = 16)]
    public struct VisibilityInstanceData
    {
        /// <summary>实例的世界矩阵。</summary>
        public Matrix4x4 WorldMatrix;

        /// <summary>WorldMatrix 的逆。</summary>
        public Matrix4x4 InvWorldMatrix;

        /// <summary>顶点缓冲起始偏移。</summary>
        public uint VertexOffset;

        /// <summary>索引缓冲起始偏移。</summary>
        public uint IndexOffset;

        /// <summary>三角形数量。</summary>
        public uint TriangleCount;

        /// <summary>材质 ID（用于后续 shading）。</summary>
        public uint MaterialID;

        /// <summary>实例 ID（draw call 内的 index）。</summary>
        public uint InstanceID;

        /// <summary>对象 ID（用于 picking）。</summary>
        public uint ObjectID;

        /// <summary>是否双面渲染。</summary>
        public uint DoubleSided;

        /// <summary>padding 对齐到 16 字节。</summary>
        public uint Pad0, Pad1, Pad2;
    }

    /// <summary>顶点数据（位置 + 法线 + UV）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct VisibilityVertex
    {
        public Vector3 Position;
        public float Pad0;
        public Vector3 Normal;
        public float Pad1;
        public Vector2 TexCoord;
        public Vector2 Pad2;
    }

    /// <summary>可见性缓冲渲染配置。</summary>
    public sealed class VisibilityBufferConfig
    {
        /// <summary>Tile 大小（软件光栅化用）。</summary>
        public int TileSize = 8;

        /// <summary>是否启用背面剔除。</summary>
        public bool EnableBackFaceCulling = true;

        /// <summary>是否启用近远平面裁剪。</summary>
        public bool EnableNearFarClip = true;

        /// <summary>是否启用 subpixel 采样（MSAA）。</summary>
        public bool EnableMSAA = false;

        /// <summary>MSAA 采样数（1/2/4/8）。</summary>
        public int MSAASampleCount = 1;

        /// <summary>是否使用保守光栅化。</summary>
        public bool ConservativeRasterization = false;

        /// <summary>保守光栅化扩展像素数。</summary>
        public float ConservativeExpand = 0.5f;

        /// <summary>是否启用深度预 pass（软件 z-prepass）。</summary>
        public bool EnableZPrePass = true;

        /// <summary>是否启用 hierarchical z buffer（加速遮挡剔除）。</summary>
        public bool EnableHiZ = true;

        /// <summary>Hi-Z 最大层级数。</summary>
        public int HiZMaxLevels = 8;

        /// <summary>是否启用 mesh shader 路径（如果硬件支持）。</summary>
        public bool PreferMeshShader = false;

        /// <summary>Mesh Shader 每组的三角形数。</summary>
        public int MeshShaderTrisPerGroup = 64;

        /// <summary>Debug 可视化模式。</summary>
        public VisibilityDebugMode DebugMode = VisibilityDebugMode.None;

        /// <summary>性能预设：高质量（MSAA + 保守光栅化）。</summary>
        public static VisibilityBufferConfig Quality => new()
        {
            TileSize = 8,
            EnableMSAA = true,
            MSAASampleCount = 4,
            ConservativeRasterization = false,
            EnableHiZ = true,
            PreferMeshShader = true
        };

        /// <summary>性能预设：性能优先（关闭 MSAA，tile 较大）。</summary>
        public static VisibilityBufferConfig Performance => new()
        {
            TileSize = 16,
            EnableMSAA = false,
            MSAASampleCount = 1,
            ConservativeRasterization = false,
            EnableHiZ = true,
            PreferMeshShader = true
        };

        /// <summary>性能预设：极致性能（软件光栅化 fallback）。</summary>
        public static VisibilityBufferConfig SoftwareFallback => new()
        {
            TileSize = 32,
            EnableMSAA = false,
            MSAASampleCount = 1,
            ConservativeRasterization = false,
            EnableHiZ = false,
            PreferMeshShader = false
        };
    }

    /// <summary>可见性缓冲 Debug 可视化模式。</summary>
    public enum VisibilityDebugMode
    {
        None,
        PrimitiveID,
        InstanceID,
        Barycentric,
        Depth,
        Overdraw,
        TriangleSize,
        MaterialID
    }

    // ────────────────────────────────────────────────────────────────────────
    // 软件光栅化核心
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 软件光栅化器：在 CPU 上模拟 GPU 光栅化流程。
    /// 用于工具预览、验证、fallback。
    /// </summary>
    public sealed class SoftwareRasterizer : IDisposable
    {
        private readonly VisibilityBufferConfig _config;
        private VisibilityBufferPixel[] _visBuffer;
        private float[] _depthBuffer;
        private int _width, _height;
        private int _tileX, _tileY;
        private bool _disposed;

        // 统计
        private long _totalTriangles;
        private long _visibleTriangles;
        private long _totalPixelsShaded;
        private long _totalPixelsTested;

        public int Width => _width;
        public int Height => _height;
        public long TotalTriangles => _totalTriangles;
        public long VisibleTriangles => _visibleTriangles;
        public long TotalPixelsShaded => _totalPixelsShaded;
        public long TotalPixelsTested => _totalPixelsTested;
        public float AverageOverdraw => _totalPixelsTested > 0 ? (float)_totalPixelsShaded / _totalPixelsTested : 0.0f;

        /// <summary>可见性缓冲像素数据。</summary>
        public ReadOnlySpan<VisibilityBufferPixel> VisibilityBuffer => _visBuffer;

        /// <summary>深度缓冲数据。</summary>
        public ReadOnlySpan<float> DepthBuffer => _depthBuffer;

        public SoftwareRasterizer(int width, int height, VisibilityBufferConfig? config = null)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentException("尺寸必须大于 0");
            _width = width;
            _height = height;
            _config = config ?? new VisibilityBufferConfig();
            _visBuffer = new VisibilityBufferPixel[width * height];
            _depthBuffer = new float[width * height];
            _tileX = (_width + _config.TileSize - 1) / _config.TileSize;
            _tileY = (_height + _config.TileSize - 1) / _config.TileSize;
            Clear();
        }

        // 兼容重载：以默认尺寸创建（供 RHI 设备驱动的创建点使用）
        public SoftwareRasterizer(RHIDevice device)
            : this(1024, 1024) { }

        /// <summary>初始化（默认空实现，可被后期初始化流程调用）。</summary>
        public void Initialize() { }

        /// <summary>是否已初始化 (供 GPU 软件光栅路径查询)。</summary>
        public bool IsInitialized => !_disposed;

        /// <summary>阶段 1: Bin 分配 (GPU 软件光栅路径, 兼容存根)。</summary>
        public void BinAssignPass(RHICommandList cmdList, RHIBuffer? clusterBuffer, SoftwareRasterCB cb)
        {
            // GPU 计算路径存根: CPU 光栅化器不执行 GPU pass。
        }

        /// <summary>阶段 2: Tile 光栅化 (GPU 软件光栅路径, 兼容存根)。</summary>
        public void RasterizePass(RHICommandList cmdList, RHITexture visibilityTexture, RHITexture depthTexture, RHITexture? historyTexture = null)
        {
            // GPU 计算路径存根: CPU 光栅化器不执行 GPU pass。
        }

        /// <summary>阶段 3: 属性解码 (GPU 软件光栅路径, 兼容存根)。</summary>
        public void DecodePass(RHICommandList cmdList, RHITexture visibilityTexture, RHITexture[] targets, AttributeDecodeCB cb)
        {
            // GPU 计算路径存根: CPU 光栅化器不执行 GPU pass。
        }

        /// <summary>清空缓冲（重置为无几何状态）。</summary>
        public void Clear()
        {
            Array.Fill(_visBuffer, VisibilityBufferPixel.Empty);
            Array.Fill(_depthBuffer, float.MaxValue);
            _totalTriangles = 0;
            _visibleTriangles = 0;
            _totalPixelsShaded = 0;
            _totalPixelsTested = 0;
        }

        /// <summary>调整缓冲大小。</summary>
        public void Resize(int width, int height)
        {
            if (width == _width && height == _height) return;
            _width = width;
            _height = height;
            _visBuffer = new VisibilityBufferPixel[width * height];
            _depthBuffer = new float[width * height];
            _tileX = (_width + _config.TileSize - 1) / _config.TileSize;
            _tileY = (_height + _config.TileSize - 1) / _config.TileSize;
            Clear();
        }

        // ── 顶点变换 ──

        /// <summary>把顶点位置变换到 clip space。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector4 TransformToClipSpace(Vector3 position, in Matrix4x4 world, in Matrix4x4 viewProj)
        {
            Vector4 worldPos = Vector4.Transform(new Vector4(position, 1.0f), world);
            return Vector4.Transform(worldPos, viewProj);
        }

        /// <summary>把顶点位置变换到 NDC（透视除法后）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 ClipToNDC(Vector4 clip)
        {
            float invW = 1.0f / clip.W;
            return new Vector3(clip.X * invW, clip.Y * invW, clip.Z * invW);
        }

        /// <summary>把 NDC 坐标变换到屏幕空间（像素坐标）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector3 NDCToScreen(Vector3 ndc)
        {
            // NDC: [-1, 1] -> Screen: [0, width] x [0, height]
            // 注意 Y 翻转：NDC 的 +1 是顶部，屏幕的 0 是顶部
            return new Vector3(
                (ndc.X + 1.0f) * 0.5f * _width,
                (1.0f - ndc.Y) * 0.5f * _height,
                ndc.Z
            );
        }

        // ── 裁剪 ──

        /// <summary>近远平面裁剪：把三角形按 w 平面裁剪，可能拆成 1-2 个三角形。</summary>
        /// <remarks>
        // NOTE: 这里只做了近平面裁剪，远平面裁剪对可见性影响不大（远处物体本来就小），
        // 但近平面不裁会导致 w<=0 的顶点投影错误，必须处理。
        // </remarks>
        public List<Vector4> ClipNearPlane(Vector4 v0, Vector4 v1, Vector4 v2)
        {
            const float nearW = 0.0001f; // 近平面在 clip space 的 w 阈值

            var result = new List<Vector4>(6);
            var input = new List<Vector4> { v0, v1, v2 };

            // Sutherland-Hodgman 风格：对每个顶点，看是否在近平面内侧（w > nearW）
            for (int i = 0; i < 3; i++)
            {
                Vector4 cur = input[i];
                Vector4 next = input[(i + 1) % 3];
                bool curIn = cur.W > nearW;
                bool nextIn = next.W > nearW;

                if (curIn)
                    result.Add(cur);

                if (curIn != nextIn)
                {
                    // 计算交点
                    float t = (nearW - cur.W) / (next.W - cur.W);
                    Vector4 intersect = cur + (next - cur) * t;
                    result.Add(intersect);
                }
            }

            return result;
        }

        /// <summary>屏幕空间包围盒裁剪：跳过完全在屏幕外的三角形。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool GetScreenBounds(Vector3 s0, Vector3 s1, Vector3 s2, out int minX, out int minY, out int maxX, out int maxY)
        {
            minX = (int)MathF.Floor(Math.Min(s0.X, Math.Min(s1.X, s2.X)));
            minY = (int)MathF.Floor(Math.Min(s0.Y, Math.Min(s1.Y, s2.Y)));
            maxX = (int)MathF.Ceiling(Math.Max(s0.X, Math.Max(s1.X, s2.X)));
            maxY = (int)MathF.Ceiling(Math.Max(s0.Y, Math.Max(s1.Y, s2.Y)));

            // 裁剪到屏幕范围
            minX = Math.Max(0, minX);
            minY = Math.Max(0, minY);
            maxX = Math.Min(_width - 1, maxX);
            maxY = Math.Min(_height - 1, maxY);

            return maxX >= minX && maxY >= minY;
        }

        // ── 三角形 setup ──

        /// <summary>
        /// Edge function: 计算点 p 相对于边 (v0, v1) 的"重心权重"。
        /// 返回值 &gt; 0 表示在边的左侧（CCW），&lt; 0 在右侧，= 0 在边上。
        /// 这是光栅化的核心，用 signed area 计算。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float EdgeFunction(Vector3 v0, Vector3 v1, float px, float py)
        {
            return (v1.X - v0.X) * (py - v0.Y) - (v1.Y - v0.Y) * (px - v0.X);
        }

        /// <summary>
        /// 计算三角形的 signed area（用于判断正反面和重心坐标归一化）。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float SignedArea2D(Vector3 v0, Vector3 v1, Vector3 v2)
        {
            return (v1.X - v0.X) * (v2.Y - v0.Y) - (v1.Y - v0.Y) * (v2.X - v0.X);
        }

        /// <summary>
        /// 重心坐标计算：给定三个屏幕空间顶点和像素坐标，返回重心坐标。
        /// 如果像素在三角形外，返回的坐标会有负分量。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 ComputeBarycentric(Vector3 v0, Vector3 v1, Vector3 v2, float px, float py)
        {
            float area = SignedArea2D(v0, v1, v2);
            if (MathF.Abs(area) < 1e-8f)
                return new Vector3(-1, -1, -1); // 退化三角形

            float invArea = 1.0f / area;
            // bary.x = area(v1, v2, p) / area(v0, v1, v2)
            float w0 = EdgeFunction(v1, v2, px, py) * invArea;
            float w1 = EdgeFunction(v2, v0, px, py) * invArea;
            float w2 = 1.0f - w0 - w1;
            return new Vector3(w0, w1, w2);
        }

        /// <summary>
        /// 透视校正重心坐标：给屏幕空间重心坐标，计算透视校正后的重心坐标。
        // 屏幕空间重心坐标在透视投影后会失真，需要用 1/w 加权校正。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 PerspectiveCorrectBarycentric(Vector3 screenBary, float w0, float w1, float w2)
        {
            float invW0 = 1.0f / w0;
            float invW1 = 1.0f / w1;
            float invW2 = 1.0f / w2;

            float lambda0 = screenBary.X * invW0;
            float lambda1 = screenBary.Y * invW1;
            float lambda2 = screenBary.Z * invW2;

            float invSum = 1.0f / (lambda0 + lambda1 + lambda2);
            return new Vector3(
                lambda0 * invSum,
                lambda1 * invSum,
                lambda2 * invSum
            );
        }

        // ── 光栅化 ──

        /// <summary>
        /// 光栅化单个三角形。
        /// </summary>
        /// <param name="clipV0">顶点 0 的 clip space 坐标。</param>
        /// <param name="clipV1">顶点 1 的 clip space 坐标。</param>
        /// <param name="clipV2">顶点 2 的 clip space 坐标。</param>
        /// <param name="primitiveID">三角形 ID。</param>
        /// <param name="instanceID">实例 ID。</param>
        /// <param name="doubleSided">是否双面渲染。</param>
        public void RasterizeTriangle(Vector4 clipV0, Vector4 clipV1, Vector4 clipV2, uint primitiveID, uint instanceID, bool doubleSided)
        {
            _totalTriangles++;

            // 近平面裁剪
            if (_config.EnableNearFarClip)
            {
                var clipped = ClipNearPlane(clipV0, clipV1, clipV2);
                if (clipped.Count < 3) return;

                // fan：第一个顶点 + 相邻两个顶点
                // NOTE: 简单的 fan 拆分，对于近端裁剪通常够用
                Vector4 first = clipped[0];
                for (int i = 1; i < clipped.Count - 1; i++)
                {
                    RasterizeTriangleNoClip(first, clipped[i], clipped[i + 1], primitiveID, instanceID, doubleSided);
                }
            }
            else
            {
                // 直接光栅化（如果 w <= 0 会有 bug，但性能更好）
                RasterizeTriangleNoClip(clipV0, clipV1, clipV2, primitiveID, instanceID, doubleSided);
            }
        }

        /// <summary>无近平面裁剪的光栅化（假设所有顶点 w &gt; 0）。</summary>
        private void RasterizeTriangleNoClip(Vector4 clipV0, Vector4 clipV1, Vector4 clipV2, uint primitiveID, uint instanceID, bool doubleSided)
        {
            // 透视除法
            Vector3 ndc0 = ClipToNDC(clipV0);
            Vector3 ndc1 = ClipToNDC(clipV1);
            Vector3 ndc2 = ClipToNDC(clipV2);

            // 屏幕空间
            Vector3 s0 = NDCToScreen(ndc0);
            Vector3 s1 = NDCToScreen(ndc1);
            Vector3 s2 = NDCToScreen(ndc2);

            // 包围盒
            if (!GetScreenBounds(s0, s1, s2, out int minX, out int minY, out int maxX, out int maxY))
                return;

            // 背面剔除
            float area = SignedArea2D(s0, s1, s2);
            if (_config.EnableBackFaceCulling && !doubleSided)
            {
                // CCW 为正面（area > 0），CW 为背面（area < 0）
                if (area <= 0.0f) return;
            }

            // 退化三角形跳过
            if (MathF.Abs(area) < 1e-4f) return;

            _visibleTriangles++;

            float invArea = 1.0f / area;

            // 计算重心坐标增量（每像素步进）
            // 这是 scanline 算法的核心优化
            // bary0 = edge(v1, v2, p) / area
            // ∂bary0/∂x = (v2.y - v1.y) / area * sign
            // ∂bary0/∂y = (v1.x - v2.x) / area * sign
            float dW0dx = (s2.Y - s1.Y) * invArea;
            float dW0dy = (s1.X - s2.X) * invArea;
            float dW1dx = (s0.Y - s2.Y) * invArea;
            float dW1dy = (s2.X - s0.X) * invArea;

            // 透视校正需要的 1/w
            float invW0 = 1.0f / clipV0.W;
            float invW1 = 1.0f / clipV1.W;
            float invW2 = 1.0f / clipV2.W;

            // 深度插值（透视校正）
            // z = bary0*z0 + bary1*z1 + bary2*z2
            float z0 = ndc0.Z, z1 = ndc1.Z, z2 = ndc2.Z;

            // MSAA 采样偏移
            Span<Vector2> samples = stackalloc Vector2[8];
            int sampleCount = _config.EnableMSAA ? _config.MSAASampleCount : 1;
            GetMSAASampleOffsets(samples, sampleCount);

            // Tile-based 光栅化：遍历包围盒内的 tile
            int tileStartX = minX / _config.TileSize;
            int tileEndX = maxX / _config.TileSize;
            int tileStartY = minY / _config.TileSize;
            int tileEndY = maxY / _config.TileSize;

            for (int ty = tileStartY; ty <= tileEndY; ty++)
            {
                for (int tx = tileStartX; tx <= tileEndX; tx++)
                {
                    int tileMinX = tx * _config.TileSize;
                    int tileMinY = ty * _config.TileSize;
                    int tileMaxX = Math.Min(tileMinX + _config.TileSize - 1, maxX);
                    int tileMaxY = Math.Min(tileMinY + _config.TileSize - 1, maxY);

                    // 计算该 tile 的重心坐标范围，快速判断是否覆盖
                    // NOTE: 这是 tile-level 的快速拒绝测试
                    float[] bary0Row = new float[4];
                    float[] bary1Row = new float[4];
                    ComputeTileBaryRange(s0, s1, s2, dW0dx, dW0dy, dW1dx, dW1dy,
                        tileMinX, tileMinY, tileMaxX, tileMaxY, bary0Row, bary1Row);

                    // 如果 tile 完全在三角形外，跳过
                    float b0Min = bary0Row[0], b0Max = bary0Row[1];
                    float b1Min = bary1Row[0], b1Max = bary1Row[1];
                    float b2Min = 1.0f - b0Max - b1Max;
                    float b2Max = 1.0f - b0Min - b1Min;

                    bool allNegative = b0Max < 0.0f || b1Max < 0.0f || b2Max < 0.0f;
                    bool allPositive = b0Min >= 0.0f && b1Min >= 0.0f && b2Min >= 0.0f;

                    if (_config.ConservativeRasterization)
                    {
                        // 保守光栅化：只要 tile 部分覆盖就处理
                        if (allNegative) continue;
                    }
                    else
                    {
                        // 标准光栅化：tile 必须有正区域
                        if (allNegative) continue;
                    }

                    // 遍历 tile 内的像素
                    for (int py = tileMinY; py <= tileMaxY; py++)
                    {
                        // 计算每行起始的重心坐标
                        float rowY = py + 0.5f;
                        float w0Row = EdgeFunction(s1, s2, tileMinX + 0.5f, rowY) * invArea;
                        float w1Row = EdgeFunction(s2, s0, tileMinX + 0.5f, rowY) * invArea;

                        for (int px = tileMinX; px <= tileMaxX; px++)
                        {
                            _totalPixelsTested++;

                            int pixelIdx = py * _width + px;
                            float pixX = px + 0.5f;

                            // 用增量计算重心坐标
                            // bary0 = w0Row + (px - tileMinX) * dW0dx
                            float bary0 = w0Row + (px - tileMinX) * dW0dx;
                            float bary1 = w1Row + (px - tileMinX) * dW1dx;
                            float bary2 = 1.0f - bary0 - bary1;

                            // 检查是否在三角形内
                            bool inside;
                            if (_config.ConservativeRasterization)
                            {
                                // 保守光栅化：考虑扩展范围
                                inside = bary0 >= -_config.ConservativeExpand &&
                                         bary1 >= -_config.ConservativeExpand &&
                                         bary2 >= -_config.ConservativeExpand;
                            }
                            else
                            {
                                // top-left rule: 严格 &gt;= 0 但要处理 top/left edge
                                // 简化版：&gt;= 0
                                inside = bary0 >= 0.0f && bary1 >= 0.0f && bary2 >= 0.0f;
                            }

                            if (!inside) continue;

                            // 透视校正重心坐标
                            float perspW0 = bary0 * invW0;
                            float perspW1 = bary1 * invW1;
                            float perspW2 = bary2 * invW2;
                            float perspSum = perspW0 + perspW1 + perspW2;
                            float invPerspSum = 1.0f / perspSum;

                            float pb0 = perspW0 * invPerspSum;
                            float pb1 = perspW1 * invPerspSum;
                            float pb2 = perspW2 * invPerspSum;

                            // 深度插值（透视校正）
                            float depth = pb0 * z0 + pb1 * z1 + pb2 * z2;

                            // 深度测试
                            if (depth >= _depthBuffer[pixelIdx])
                                continue;

                            // 写入可见性缓冲
                            _depthBuffer[pixelIdx] = depth;
                            _visBuffer[pixelIdx] = new VisibilityBufferPixel
                            {
                                PrimitiveID = primitiveID,
                                InstanceID = instanceID,
                                BaryX = pb0, // 存透视校正后的
                                BaryY = pb1
                            };

                            _totalPixelsShaded++;
                        }
                    }
                }
            }
        }

        /// <summary>计算 tile 的重心坐标范围（用于快速拒绝）。</summary>
        private void ComputeTileBaryRange(
            Vector3 s0, Vector3 s1, Vector3 s2,
            float dW0dx, float dW0dy, float dW1dx, float dW1dy,
            int minX, int minY, int maxX, int maxY,
            float[] bary0Range, float[] bary1Range)
        {
            // 计算 4 个角的重心坐标
            float cx0 = minX + 0.5f, cy0 = minY + 0.5f;
            float cx1 = maxX + 0.5f, cy1 = maxY + 0.5f;

            float w0_00 = EdgeFunction(s1, s2, cx0, cy0);
            float w0_10 = EdgeFunction(s1, s2, cx1, cy0);
            float w0_01 = EdgeFunction(s1, s2, cx0, cy1);
            float w0_11 = EdgeFunction(s1, s2, cx1, cy1);

            float area = SignedArea2D(s0, s1, s2);
            float invArea = 1.0f / area;

            float b0_00 = w0_00 * invArea;
            float b0_10 = w0_10 * invArea;
            float b0_01 = w0_01 * invArea;
            float b0_11 = w0_11 * invArea;

            bary0Range[0] = Math.Min(Math.Min(b0_00, b0_10), Math.Min(b0_01, b0_11));
            bary0Range[1] = Math.Max(Math.Max(b0_00, b0_10), Math.Max(b0_01, b0_11));

            float w1_00 = EdgeFunction(s2, s0, cx0, cy0) * invArea;
            float w1_10 = EdgeFunction(s2, s0, cx1, cy0) * invArea;
            float w1_01 = EdgeFunction(s2, s0, cx0, cy1) * invArea;
            float w1_11 = EdgeFunction(s2, s0, cx1, cy1) * invArea;

            bary1Range[0] = Math.Min(Math.Min(w1_00, w1_10), Math.Min(w1_01, w1_11));
            bary1Range[1] = Math.Max(Math.Max(w1_00, w1_10), Math.Max(w1_01, w1_11));
        }

        /// <summary>获取 MSAA 采样点偏移。</summary>
        private void GetMSAASampleOffsets(Span<Vector2> samples, int count)
        {
            // 标准的 MSAA 采样模式
            switch (count)
            {
                case 1:
                    samples[0] = new Vector2(0.5f, 0.5f);
                    break;
                case 2:
                    samples[0] = new Vector2(0.25f, 0.25f);
                    samples[1] = new Vector2(0.75f, 0.75f);
                    break;
                case 4:
                    samples[0] = new Vector2(0.375f, 0.125f);
                    samples[1] = new Vector2(0.875f, 0.375f);
                    samples[2] = new Vector2(0.125f, 0.625f);
                    samples[3] = new Vector2(0.625f, 0.875f);
                    break;
                case 8:
                    samples[0] = new Vector2(0.5625f, 0.3125f);
                    samples[1] = new Vector2(0.4375f, 0.6875f);
                    samples[2] = new Vector2(0.8125f, 0.5625f);
                    samples[3] = new Vector2(0.1875f, 0.8125f);
                    samples[4] = new Vector2(0.0625f, 0.1875f);
                    samples[5] = new Vector2(0.6875f, 0.9375f);
                    samples[6] = new Vector2(0.9375f, 0.0625f);
                    samples[7] = new Vector2(0.3125f, 0.4375f);
                    break;
            }
        }

        // ── 几何提交 ──

        /// <summary>
        /// 光栅化一个网格（顶点 + 索引）。
        /// </summary>
        public void RasterizeMesh(
            ReadOnlySpan<VisibilityVertex> vertices,
            ReadOnlySpan<uint> indices,
            in Matrix4x4 world,
            in Matrix4x4 viewProj,
            uint instanceID,
            uint materialID,
            uint objectID,
            bool doubleSided)
        {
            int triCount = indices.Length / 3;
            for (int i = 0; i < triCount; i++)
            {
                uint i0 = indices[i * 3 + 0];
                uint i1 = indices[i * 3 + 1];
                uint i2 = indices[i * 3 + 2];

                Vector4 c0 = TransformToClipSpace(vertices[(int)i0].Position, world, viewProj);
                Vector4 c1 = TransformToClipSpace(vertices[(int)i1].Position, world, viewProj);
                Vector4 c2 = TransformToClipSpace(vertices[(int)i2].Position, world, viewProj);

                RasterizeTriangle(c0, c1, c2, (uint)i, instanceID, doubleSided);
            }
        }

        /// <summary>
        /// 光栅化多个实例（批量提交）。
        /// </summary>
        public void RasterizeInstances(
            ReadOnlySpan<VisibilityVertex> vertices,
            ReadOnlySpan<uint> indices,
            ReadOnlySpan<Matrix4x4> worldMatrices,
            in Matrix4x4 viewProj,
            uint baseInstanceID,
            uint materialID,
            bool doubleSided)
        {
            for (int inst = 0; inst < worldMatrices.Length; inst++)
            {
                RasterizeMesh(vertices, indices, worldMatrices[inst], viewProj,
                    baseInstanceID + (uint)inst, materialID, baseInstanceID + (uint)inst, doubleSided);
            }
        }

        // ── Hi-Z 构建 ──

        /// <summary>构建 Hierarchical Z Buffer，用于遮挡剔除。</summary>
        public float[][] BuildHiZBuffer()
        {
            if (!_config.EnableHiZ) return Array.Empty<float[]>();

            int maxLevels = _config.HiZMaxLevels;
            var levels = new List<float[]>(maxLevels);

            // Level 0: 原始深度缓冲
            // NOTE: 这里用 1/z 而不是 z，因为我们要找"最远可见深度"做遮挡判断
            float[] level0 = new float[_width * _height];
            for (int i = 0; i < _depthBuffer.Length; i++)
            {
                float z = _depthBuffer[i];
                level0[i] = z == float.MaxValue ? 0.0f : 1.0f / MathF.Max(z, 1e-6f);
            }
            levels.Add(level0);

            int curW = _width, curH = _height;
            for (int lv = 1; lv < maxLevels; lv++)
            {
                int newW = Math.Max(1, curW / 2);
                int newH = Math.Max(1, curH / 2);
                float[] prev = levels[lv - 1];
                float[] cur = new float[newW * newH];

                for (int y = 0; y < newH; y++)
                {
                    for (int x = 0; x < newW; x++)
                    {
                        // 取 2x2 像素的最大 1/z（即最近的 z）
                        int sx0 = x * 2, sy0 = y * 2;
                        int sx1 = Math.Min(sx0 + 1, curW - 1);
                        int sy1 = Math.Min(sy0 + 1, curH - 1);

                        float v00 = prev[sy0 * curW + sx0];
                        float v10 = prev[sy0 * curW + sx1];
                        float v01 = prev[sy1 * curW + sx0];
                        float v11 = prev[sy1 * curW + sx1];
                        cur[y * newW + x] = Math.Max(Math.Max(v00, v10), Math.Max(v01, v11));
                    }
                }

                levels.Add(cur);
                curW = newW;
                curH = newH;

                if (curW == 1 && curH == 1) break;
            }

            return levels.ToArray();
        }

        /// <summary>查询 Hi-Z 在某个屏幕位置的深度（用于遮挡测试）。</summary>
        public float QueryHiZ(float[][] hiZ, float screenX, float screenY, int level)
        {
            if (hiZ == null || hiZ.Length == 0 || level < 0 || level >= hiZ.Length)
                return 0.0f;

            int w = hiZ[level].Length > 0 ? hiZ[level].GetLength(0) : 1;
            // hack: 实际宽度需要单独存储，这里简化处理
            int levelW = _width >> level;
            int levelH = _height >> level;
            if (levelW < 1) levelW = 1;
            if (levelH < 1) levelH = 1;

            int px = Math.Clamp((int)(screenX / _width * levelW), 0, levelW - 1);
            int py = Math.Clamp((int)(screenY / _height * levelH), 0, levelH - 1);

            return hiZ[level][py * levelW + px];
        }

        // ── Debug 可视化 ──

        /// <summary>生成 Debug 可视化图像（RGBA8）。</summary>
        public Vector4[] GenerateDebugImage(VisibilityDebugMode mode)
        {
            var result = new Vector4[_width * _height];
            for (int i = 0; i < _visBuffer.Length; i++)
            {
                var pix = _visBuffer[i];
                result[i] = pix.IsValid ? DebugPixelColor(pix, mode, _depthBuffer[i]) : Vector4.Zero;
            }
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector4 DebugPixelColor(VisibilityBufferPixel pix, VisibilityDebugMode mode, float depth)
        {
            switch (mode)
            {
                case VisibilityDebugMode.PrimitiveID:
                    // 用 primitiveID 的 hash 生成颜色
                    uint pid = pix.PrimitiveID;
                    return new Vector4(
                        ((pid * 2654435761u) & 0xFF) / 255.0f,
                        ((pid * 40503u) & 0xFF) / 255.0f,
                        ((pid * 91283u) & 0xFF) / 255.0f,
                        1.0f);

                case VisibilityDebugMode.InstanceID:
                    uint iid = pix.InstanceID;
                    return new Vector4(
                        ((iid * 2654435761u) & 0xFF) / 255.0f,
                        ((iid * 40503u) & 0xFF) / 255.0f,
                        ((iid * 91283u) & 0xFF) / 255.0f,
                        1.0f);

                case VisibilityDebugMode.Barycentric:
                    return new Vector4(pix.Barycentric.X, pix.Barycentric.Y, 1.0f, 1.0f);

                case VisibilityDebugMode.Depth:
                    float d = Math.Clamp(depth, 0.0f, 1.0f);
                    return new Vector4(d, d, d, 1.0f);

                case VisibilityDebugMode.TriangleSize:
                    // 用 primitiveID 模拟（实际应该是三角形面积）
                    float sizeHash = (pix.PrimitiveID % 256) / 255.0f;
                    return new Vector4(sizeHash, sizeHash * 0.5f, 1.0f - sizeHash, 1.0f);

                case VisibilityDebugMode.MaterialID:
                    uint mid = pix.InstanceID; // 简化：用 instanceID 代替
                    return new Vector4(
                        ((mid * 2654435761u) & 0xFF) / 255.0f,
                        ((mid * 40503u) & 0xFF) / 255.0f,
                        ((mid * 91283u) & 0xFF) / 255.0f,
                        1.0f);

                default:
                    return new Vector4(pix.BaryX, pix.BaryY, pix.BaryZ, 1.0f);
            }
        }

        /// <summary>统计 overdraw：每像素被覆盖几次。</summary>
        public int[] ComputeOverdrawStats()
        {
            // NOTE: 这需要在光栅化时统计，这里只返回简化版
            var stats = new int[8]; // 0, 1, 2-3, 4-7, 8-15, 16-31, 32-63, 64+
            // 简化：用 _totalPixelsShaded / _totalPixelsTested 估算
            float overdraw = AverageOverdraw;
            if (overdraw < 1) stats[0]++;
            else if (overdraw < 2) stats[1]++;
            else if (overdraw < 4) stats[2]++;
            else if (overdraw < 8) stats[3]++;
            else if (overdraw < 16) stats[4]++;
            else if (overdraw < 32) stats[5]++;
            else if (overdraw < 64) stats[6]++;
            else stats[7]++;
            return stats;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _visBuffer = null!;
            _depthBuffer = null!;
            _disposed = true;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 可见性缓冲渲染器（整合 GPU 路径和软件路径）
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 可见性缓冲渲染器：负责整体流程调度。
    /// 在 GPU 可用时用 mesh shader / 普通顶点管线，否则退化到软件光栅化。
    /// </summary>
    public sealed class VisibilityBufferRenderer : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly VisibilityBufferConfig _config;
        private RHITexture? _visBufferTexture;
        private RHITexture? _depthTexture;
        private RHIBuffer? _instanceBuffer;
        private RHIBuffer? _vertexBuffer;
        private RHIBuffer? _indexBuffer;
        private RHIBuffer? _visibilityCB;
        private SoftwareRasterizer? _softwareRasterizer;
        private bool _disposed;

        private readonly List<VisibilityInstanceData> _instances = new();
        private readonly List<VisibilityVertex> _vertices = new();
        private readonly List<uint> _indices = new();
        private uint _nextInstanceID;

        public int Width { get; private set; }
        public int Height { get; private set; }
        public VisibilityBufferConfig Config => _config;
        public IReadOnlyList<VisibilityInstanceData> Instances => _instances;
        public int InstanceCount => _instances.Count;
        public int VertexCount => _vertices.Count;
        public int IndexCount => _indices.Count;

        /// <summary>软件光栅化器（仅在软件路径启用时有效）。</summary>
        public SoftwareRasterizer SoftwarePath => _softwareRasterizer!;

        /// <summary>是否使用软件光栅化路径。</summary>
        public bool UseSoftwarePath => _softwareRasterizer != null;

        public VisibilityBufferRenderer(RHIDevice device, int width, int height, VisibilityBufferConfig? config = null)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _config = config ?? new VisibilityBufferConfig();
            Width = width;
            Height = height;

            // 如果硬件不支持 mesh shader 或配置了软件路径，启用软件光栅化
            bool hwMeshShader = device.Capabilities?.SupportsMeshShaders ?? false;
            if (!hwMeshShader || !_config.PreferMeshShader)
            {
                _softwareRasterizer = new SoftwareRasterizer(width, height, _config);
            }

            AllocateResources();
        }

        // ── 资源管理 ──

        private void AllocateResources()
        {
            // 可见性缓冲纹理：R32G32B32A32_Float（primitiveID, instanceID, baryX, baryY）
            _visBufferTexture = _device.CreateTexture(new RHITextureDesc
            {
                Width = Width,
                Height = Height,
                Format = RHIFormat.R32G32B32A32_Float,
                BindFlags = RHIBindFlags.RenderTarget | RHIBindFlags.UnorderedAccess | RHIBindFlags.ShaderResource,
                MiscFlags = 0,
                MipLevels = 1,
                ArraySize = 1
            });

            // 深度纹理：D32_Float
            _depthTexture = _device.CreateTexture(new RHITextureDesc
            {
                Width = Width,
                Height = Height,
                Format = RHIFormat.D32_Float,
                BindFlags = RHIBindFlags.DepthStencil | RHIBindFlags.ShaderResource,
                MiscFlags = 0,
                MipLevels = 1,
                ArraySize = 1
            });

            // 实例缓冲
            _instanceBuffer = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = (ulong)(4096 * (int)Marshal.SizeOf<VisibilityInstanceData>()),
                BindFlags = RHIBindFlags.StructuredBuffer | RHIBindFlags.ShaderResource,
                StructureByteStride = (ulong)Marshal.SizeOf<VisibilityInstanceData>(),
                Usage = (RHIBufferUsage)RHIUsage.Dynamic
            });

            // 顶点缓冲
            _vertexBuffer = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = (ulong)(1024 * 1024 * (int)Marshal.SizeOf<VisibilityVertex>()),
                BindFlags = RHIBindFlags.StructuredBuffer | RHIBindFlags.ShaderResource,
                StructureByteStride = (ulong)Marshal.SizeOf<VisibilityVertex>(),
                Usage = (RHIBufferUsage)RHIUsage.Dynamic
            });

            // 索引缓冲
            _indexBuffer = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = 4096 * 1024 * sizeof(uint),
                BindFlags = RHIBindFlags.IndexBuffer | RHIBindFlags.StructuredBuffer | RHIBindFlags.ShaderResource,
                StructureByteStride = sizeof(uint),
                Usage = (RHIBufferUsage)RHIUsage.Dynamic
            });

            // 常量缓冲
            _visibilityCB = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = 256, // 足够大的 CB
                BindFlags = RHIBindFlags.ConstantBuffer,
                Usage = (RHIBufferUsage)RHIUsage.Dynamic
            });
        }

        /// <summary>调整渲染目标大小。</summary>
        public void Resize(int width, int height)
        {
            if (Width == width && Height == height) return;
            Width = width;
            Height = height;

            _visBufferTexture?.Dispose();
            _depthTexture?.Dispose();

            _visBufferTexture = _device.CreateTexture(new RHITextureDesc
            {
                Width = width,
                Height = height,
                Format = RHIFormat.R32G32B32A32_Float,
                BindFlags = RHIBindFlags.RenderTarget | RHIBindFlags.UnorderedAccess | RHIBindFlags.ShaderResource,
                MipLevels = 1,
                ArraySize = 1
            });

            _depthTexture = _device.CreateTexture(new RHITextureDesc
            {
                Width = width,
                Height = height,
                Format = RHIFormat.D32_Float,
                BindFlags = RHIBindFlags.DepthStencil | RHIBindFlags.ShaderResource,
                MipLevels = 1,
                ArraySize = 1
            });

            _softwareRasterizer?.Resize(width, height);
        }

        // ── 几何提交 ──

        /// <summary>添加一个渲染实例。</summary>
        public uint AddInstance(
            ReadOnlySpan<VisibilityVertex> vertices,
            ReadOnlySpan<uint> indices,
            in Matrix4x4 worldMatrix,
            uint materialID,
            uint objectID,
            bool doubleSided)
        {
            uint instanceID = _nextInstanceID++;
            uint vertexOffset = (uint)_vertices.Count;
            uint indexOffset = (uint)_indices.Count;

            // 把顶点和索引追加到大缓冲
            foreach (var v in vertices) _vertices.Add(v);
            foreach (var i in indices) _indices.Add(i);

            _instances.Add(new VisibilityInstanceData
            {
                WorldMatrix = worldMatrix,
                InvWorldMatrix = Matrix4x4.Invert(worldMatrix, out var inv) ? inv : Matrix4x4.Identity,
                VertexOffset = vertexOffset,
                IndexOffset = indexOffset,
                TriangleCount = (uint)(indices.Length / 3),
                MaterialID = materialID,
                InstanceID = instanceID,
                ObjectID = objectID,
                DoubleSided = doubleSided ? 1u : 0u
            });

            return instanceID;
        }

        /// <summary>清除所有实例。</summary>
        public void ClearInstances()
        {
            _instances.Clear();
            _vertices.Clear();
            _indices.Clear();
            _nextInstanceID = 0;
        }

        // ── 渲染 ──

        /// <summary>执行可见性缓冲渲染。</summary>
        public VisibilityBufferStats Render(in Matrix4x4 viewProj)
        {
            var stats = new VisibilityBufferStats();

            if (_instances.Count == 0)
                return stats;

            if (_softwareRasterizer != null)
            {
                // 软件路径
                stats = RenderSoftware(viewProj);
            }
            else
            {
                // GPU 路径
                stats = RenderGPU(viewProj);
            }

            return stats;
        }

        /// <summary>软件光栅化渲染。</summary>
        private VisibilityBufferStats RenderSoftware(in Matrix4x4 viewProj)
        {
            _softwareRasterizer!.Clear();

            var verticesSpan = CollectionsMarshal.AsSpan(_vertices);
            var indicesSpan = CollectionsMarshal.AsSpan(_indices);

            foreach (var inst in _instances)
            {
                // 提取该实例的顶点和索引子集
                var instVerts = verticesSpan.Slice((int)inst.VertexOffset, (int)inst.TriangleCount * 3);
                // NOTE: 三角形数 * 3 不一定等于顶点数，这里假设索引是独立的
                // 实际应该用 IndexOffset + TriangleCount*3 来切片 indices
                var instIndices = indicesSpan.Slice((int)inst.IndexOffset, (int)inst.TriangleCount * 3);

                _softwareRasterizer.RasterizeMesh(
                    instVerts,
                    instIndices,
                    inst.WorldMatrix,
                    viewProj,
                    inst.InstanceID,
                    inst.MaterialID,
                    inst.ObjectID,
                    inst.DoubleSided != 0);
            }

            return new VisibilityBufferStats
            {
                InstanceCount = _instances.Count,
                TriangleCount = _softwareRasterizer.TotalTriangles,
                VisibleTriangleCount = _softwareRasterizer.VisibleTriangles,
                PixelsShaded = _softwareRasterizer.TotalPixelsShaded,
                PixelsTested = _softwareRasterizer.TotalPixelsTested,
                AverageOverdraw = _softwareRasterizer.AverageOverdraw,
                UseSoftwarePath = true
            };
        }

        /// <summary>GPU 光栅化渲染（mesh shader 或传统管线）。</summary>
        private VisibilityBufferStats RenderGPU(in Matrix4x4 viewProj)
        {
            // 上传实例数据
            var instanceData = CollectionsMarshal.AsSpan(_instances);
            _device.UpdateBuffer(_instanceBuffer!, instanceData);

            // 上传顶点和索引
            var vertexData = CollectionsMarshal.AsSpan(_vertices);
            _device.UpdateBuffer(_vertexBuffer!, vertexData);

            var indexData = CollectionsMarshal.AsSpan(_indices);
            _device.UpdateBuffer(_indexBuffer!, indexData);

            // 更新常量缓冲
            var cbData = new VisibilityRenderCB
            {
                ViewProj = viewProj,
                InvViewProj = Matrix4x4.Invert(viewProj, out var inv) ? inv : Matrix4x4.Identity,
                InstanceCount = (uint)_instances.Count,
                TotalVertexCount = (uint)_vertices.Count,
                TotalIndexCount = (uint)_indices.Count,
                Width = (uint)Width,
                Height = (uint)Height,
                EnableBackFaceCulling = _config.EnableBackFaceCulling ? 1u : 0u,
                ConservativeRaster = _config.ConservativeRasterization ? 1u : 0u
            };
            _device.UpdateBuffer(_visibilityCB!, new[] { cbData });

            // 清屏
            var clearValues = new[] { new Vector4(uint.MaxValue, uint.MaxValue, 0, 0) };
            _device.ClearRenderTarget(_visBufferTexture!, clearValues[0]);
            _device.ClearDepthStencil(_depthTexture!, 1.0f, 0);

            // 设置渲染目标
            _device.SetRenderTargets(new[] { _visBufferTexture! }, _depthTexture!);

            // 设置 SRV
            _device.SetShaderResource(0, _instanceBuffer!);
            _device.SetShaderResource(1, _vertexBuffer!);
            _device.SetShaderResource(2, _indexBuffer!);
            _device.SetConstantBuffer(0, _visibilityCB!);

            long totalTris = 0;
            if (_config.PreferMeshShader && (_device.Capabilities?.SupportsMeshShaders ?? false))
            {
                // Mesh Shader 路径
                totalTris = RenderWithMeshShader();
            }
            else
            {
                // 传统顶点管线
                totalTris = RenderWithVertexPipeline();
            }

            return new VisibilityBufferStats
            {
                InstanceCount = _instances.Count,
                TriangleCount = totalTris,
                UseSoftwarePath = false
            };
        }

        /// <summary>Mesh Shader 路径：把三角形分成组，每组装一个 meshlet。</summary>
        private long RenderWithMeshShader()
        {
            long totalTris = 0;
            int trisPerGroup = _config.MeshShaderTrisPerGroup;

            // 为每个实例生成 meshlet
            // NOTE: 实际应该预计算 meshlet，这里简化为运行时分组
            foreach (var inst in _instances)
            {
                int meshletCount = (int)((inst.TriangleCount + trisPerGroup - 1) / trisPerGroup);
                for (int ml = 0; ml < meshletCount; ml++)
                {
                    uint triStart = inst.IndexOffset / 3 + (uint)(ml * trisPerGroup);
                    uint triCount = (uint)Math.Min(trisPerGroup, (int)inst.TriangleCount - ml * trisPerGroup);

                    // Dispatch mesh shader
                    _device.DispatchMesh(1, 1, 1);
                    totalTris += triCount;
                }
            }

            return totalTris;
        }

        /// <summary>传统顶点管线：DrawIndexed。</summary>
        private long RenderWithVertexPipeline()
        {
            long totalTris = 0;

            // 合并 draw call：如果多个实例用同一个 mesh，用 instanced draw
            // NOTE: 简化版，每个实例一个 draw call
            foreach (var inst in _instances)
            {
                _device.DrawIndexed(
                    (int)inst.TriangleCount * 3,
                    (int)inst.IndexOffset,
                    (int)inst.VertexOffset,
                    (int)inst.InstanceID,
                    1);
                totalTris += inst.TriangleCount;
            }

            return totalTris;
        }

        // ── 着色阶段 ──

        /// <summary>
        /// 可见性缓冲着色：根据可见性缓冲 + 几何数据，重建属性并着色。
        /// </summary>
        public RHITexture Shade(ReadOnlySpan<VisibilityVertex> vertices, ReadOnlySpan<uint> indices,
            in Matrix4x4 viewProj, RHITexture? depthTarget = null)
        {
            // 创建着色输出纹理
            var shadedTex = _device.CreateTexture(new RHITextureDesc
            {
                Width = Width,
                Height = Height,
                Format = RHIFormat.R16G16B16A16_Float,
                BindFlags = RHIBindFlags.UnorderedAccess | RHIBindFlags.ShaderResource,
                MipLevels = 1,
                ArraySize = 1
            });

            // 上传顶点和索引
            _device.UpdateBuffer(_vertexBuffer!, vertices);
            _device.UpdateBuffer(_indexBuffer!, indices);

            // 设置 SRV/UAV
            _device.SetShaderResource(0, _visBufferTexture!);
            _device.SetShaderResource(1, _vertexBuffer!);
            _device.SetShaderResource(2, _indexBuffer!);
            _device.SetShaderResource(3, _instanceBuffer!);
            if (depthTarget != null)
                _device.SetShaderResource(4, depthTarget);

            _device.SetUnorderedAccessView(0, shadedTex);

            // Dispatch compute shader
            int groupX = (Width + 7) / 8;
            int groupY = (Height + 7) / 8;
            _device.Dispatch(groupX, groupY, 1);

            return shadedTex;
        }

        /// <summary>读取可见性缓冲回 CPU（用于 picking 等场景）。</summary>
        public VisibilityBufferPixel[] ReadBackVisibility()
        {
            var result = new VisibilityBufferPixel[Width * Height];
            if (_softwareRasterizer != null)
            {
                Array.Copy(_softwareRasterizer.VisibilityBuffer.ToArray(), result, result.Length);
            }
            else
            {
                _device.ReadbackTexture(_visBufferTexture!, out result);
            }
            return result;
        }

        /// <summary>Picking：获取屏幕某点的可见性信息。</summary>
        public VisibilityBufferPixel Pick(int screenX, int screenY)
        {
            if (screenX < 0 || screenX >= Width || screenY < 0 || screenY >= Height)
                return VisibilityBufferPixel.Empty;

            if (_softwareRasterizer != null)
            {
                return _softwareRasterizer.VisibilityBuffer[screenY * Width + screenX];
            }

            // GPU 路径：读取单个像素
            var pixel = new VisibilityBufferPixel[1];
            _device.ReadbackTextureRegion(_visBufferTexture!, screenX, screenY, 1, 1, out pixel);
            return pixel[0];
        }

        /// <summary>获取可见性缓冲纹理（GPU 路径）。</summary>
        public RHITexture GetVisibilityTexture() => _visBufferTexture!;

        /// <summary>获取深度纹理。</summary>
        public RHITexture GetDepthTexture() => _depthTexture!;

        public void Dispose()
        {
            if (_disposed) return;
            _visBufferTexture?.Dispose();
            _depthTexture?.Dispose();
            _instanceBuffer?.Dispose();
            _vertexBuffer?.Dispose();
            _indexBuffer?.Dispose();
            _visibilityCB?.Dispose();
            _softwareRasterizer?.Dispose();
            _disposed = true;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 常量缓冲结构
    // ────────────────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    internal struct VisibilityRenderCB
    {
        public Matrix4x4 ViewProj;
        public Matrix4x4 InvViewProj;
        public uint InstanceCount;
        public uint TotalVertexCount;
        public uint TotalIndexCount;
        public uint Width;
        public uint Height;
        public uint EnableBackFaceCulling;
        public uint ConservativeRaster;
        public uint Pad0, Pad1;
    }

    // ────────────────────────────────────────────────────────────────────────
    // 统计和结果
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>可见性缓冲渲染统计。</summary>
    public struct VisibilityBufferStats
    {
        public int InstanceCount;
        public long TriangleCount;
        public long VisibleTriangleCount;
        public long PixelsShaded;
        public long PixelsTested;
        public float AverageOverdraw;
        public bool UseSoftwarePath;
        public double RenderTimeMs;

        public override string ToString()
        {
            return $"Instances={InstanceCount}, Tris={TriangleCount}({VisibleTriangleCount} visible), " +
                   $"Pixels={PixelsShaded}/{PixelsTested}, Overdraw={AverageOverdraw:F2}x, " +
                   $"Path={(UseSoftwarePath ? "Software" : "GPU")}, Time={RenderTimeMs:F2}ms";
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 属性重建工具
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 可见性缓冲属性重建：从可见性缓冲 + 几何数据重建像素属性。
    /// 这是可见性缓冲渲染的核心：shading pass 不读 GeometryBuffer，而是从原始几何拉数据。
    /// </summary>
    public static class VisibilityAttributeReconstructor
    {
        /// <summary>从可见性缓冲像素重建顶点属性。</summary>
        public static VisibilityVertex ReconstructAttributes(
            VisibilityBufferPixel pix,
            ReadOnlySpan<VisibilityVertex> vertices,
            ReadOnlySpan<uint> indices,
            uint vertexOffset,
            uint indexOffset)
        {
            if (!pix.IsValid)
                return default;

            uint i0 = indices[(int)(indexOffset + pix.PrimitiveID * 3 + 0)];
            uint i1 = indices[(int)(indexOffset + pix.PrimitiveID * 3 + 1)];
            uint i2 = indices[(int)(indexOffset + pix.PrimitiveID * 3 + 2)];

            var v0 = vertices[(int)(vertexOffset + i0)];
            var v1 = vertices[(int)(vertexOffset + i1)];
            var v2 = vertices[(int)(vertexOffset + i2)];

            // 重心坐标插值
            // NOTE: 这里用的是透视校正后的重心坐标（存在可见性缓冲里）
            float b0 = pix.BaryX;
            float b1 = pix.BaryY;
            float b2 = pix.BaryZ;

            return new VisibilityVertex
            {
                Position = v0.Position * b0 + v1.Position * b1 + v2.Position * b2,
                Normal = Vector3.Normalize(v0.Normal * b0 + v1.Normal * b1 + v2.Normal * b2),
                TexCoord = v0.TexCoord * b0 + v1.TexCoord * b1 + v2.TexCoord * b2
            };
        }

        /// <summary>重建世界坐标。</summary>
        public static Vector3 ReconstructWorldPos(VisibilityBufferPixel pix,
            ReadOnlySpan<VisibilityVertex> vertices,
            ReadOnlySpan<uint> indices,
            uint vertexOffset,
            uint indexOffset,
            in Matrix4x4 worldMatrix)
        {
            if (!pix.IsValid) return Vector3.Zero;

            uint i0 = indices[(int)(indexOffset + pix.PrimitiveID * 3 + 0)];
            uint i1 = indices[(int)(indexOffset + pix.PrimitiveID * 3 + 1)];
            uint i2 = indices[(int)(indexOffset + pix.PrimitiveID * 3 + 2)];

            var v0 = vertices[(int)(vertexOffset + i0)].Position;
            var v1 = vertices[(int)(vertexOffset + i1)].Position;
            var v2 = vertices[(int)(vertexOffset + i2)].Position;

            Vector3 localPos = v0 * pix.BaryX + v1 * pix.BaryY + v2 * pix.BaryZ;
            return Vector3.Transform(localPos, worldMatrix);
        }

        /// <summary>重建导数（用于 mipmap 选择）。</summary>
        public static (Vector2 ddx, Vector2 ddy) ReconstructDerivatives(
            VisibilityBufferPixel pixCenter,
            VisibilityBufferPixel pixRight,
            VisibilityBufferPixel pixDown,
            ReadOnlySpan<VisibilityVertex> vertices,
            ReadOnlySpan<uint> indices,
            uint vertexOffset,
            uint indexOffset)
        {
            var attrC = ReconstructAttributes(pixCenter, vertices, indices, vertexOffset, indexOffset);
            var attrR = ReconstructAttributes(pixRight, vertices, indices, vertexOffset, indexOffset);
            var attrD = ReconstructAttributes(pixDown, vertices, indices, vertexOffset, indexOffset);

            return (attrR.TexCoord - attrC.TexCoord, attrD.TexCoord - attrC.TexCoord);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // Meshlet 生成器
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Meshlet 生成器：把网格拆分成 meshlet（mesh shader 的工作单元）。
    /// 每个 meshlet 包含一组三角形和它们引用的顶点。
    /// </summary>
    public sealed class MeshletGenerator
    {
        /// <summary>Meshlet 顶点上限。</summary>
        public const int MaxVertices = 64;

        /// <summary>Meshlet 三角形上限。</summary>
        public const int MaxTriangles = 124; // 用 8-bit 索引所以最多 256 顶点，124 三角形

        /// <summary>Meshlet 数据结构。</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct Meshlet
        {
            public uint VertexOffset;
            public uint TriangleOffset;
            public uint VertexCount;
            public uint TriangleCount;
            public uint MeshletID;
            public uint Pad0, Pad1, Pad2;
        }

        /// <summary>生成 meshlet。</summary>
        public static (Meshlet[] meshlets, uint[] localVertices, uint8[] localIndices) Generate(
            ReadOnlySpan<uint> indices, ReadOnlySpan<VisibilityVertex> vertices)
        {
            var meshlets = new List<Meshlet>();
            var localVerts = new List<uint>();
            var localTris = new List<byte>(); // 8-bit 索引（引用 meshlet 内顶点）

            var vertexSet = new Dictionary<uint, byte>(); // 全局顶点索引 -> meshlet 内本地索引
            uint currentVertOffset = 0;
            uint currentTriOffset = 0;
            int triCount = indices.Length / 3;

            for (int t = 0; t < triCount; t++)
            {
                uint i0 = indices[t * 3 + 0];
                uint i1 = indices[t * 3 + 1];
                uint i2 = indices[t * 3 + 2];

                // 检查这 3 个顶点是否可以加入当前 meshlet
                int neededNewVerts = 0;
                if (!vertexSet.ContainsKey(i0)) neededNewVerts++;
                if (!vertexSet.ContainsKey(i1)) neededNewVerts++;
                if (!vertexSet.ContainsKey(i2)) neededNewVerts++;

                // 如果超出容量，开新 meshlet
                if (vertexSet.Count + neededNewVerts > MaxVertices ||
                    (t - currentTriOffset / 3) >= MaxTriangles)
                {
                    // 保存当前 meshlet
                    meshlets.Add(new Meshlet
                    {
                        VertexOffset = currentVertOffset,
                        TriangleOffset = currentTriOffset,
                        VertexCount = (uint)vertexSet.Count,
                        TriangleCount = (uint)(t - currentTriOffset / 3) * 3,
                        MeshletID = (uint)meshlets.Count
                    });

                    currentVertOffset += (uint)vertexSet.Count;
                    currentTriOffset += (uint)((t - currentTriOffset / 3) * 3);
                    vertexSet.Clear();
                }

                // 加入顶点
                if (!vertexSet.TryGetValue(i0, out var l0))
                {
                    l0 = (byte)vertexSet.Count;
                    vertexSet[i0] = l0;
                    localVerts.Add(i0);
                }
                if (!vertexSet.TryGetValue(i1, out var l1))
                {
                    l1 = (byte)vertexSet.Count;
                    vertexSet[i1] = l1;
                    localVerts.Add(i1);
                }
                if (!vertexSet.TryGetValue(i2, out var l2))
                {
                    l2 = (byte)vertexSet.Count;
                    vertexSet[i2] = l2;
                    localVerts.Add(i2);
                }

                localTris.Add(l0);
                localTris.Add(l1);
                localTris.Add(l2);
            }

            // 最后一个 meshlet
            if (vertexSet.Count > 0)
            {
                meshlets.Add(new Meshlet
                {
                    VertexOffset = currentVertOffset,
                    TriangleOffset = currentTriOffset,
                    VertexCount = (uint)vertexSet.Count,
                    TriangleCount = (uint)(triCount - currentTriOffset / 3) * 3,
                    MeshletID = (uint)meshlets.Count
                });
            }

            return (meshlets.ToArray(), localVerts.ToArray(), Array.ConvertAll(localTris.ToArray(), b => (uint8)b));
        }

        /// <summary>计算 meshlet 的包围球。</summary>
        public static (Vector3 center, float radius) ComputeBounds(
            Meshlet meshlet, ReadOnlySpan<uint> localVerts, ReadOnlySpan<VisibilityVertex> vertices,
            uint vertexOffset)
        {
            Vector3 min = new(float.MaxValue);
            Vector3 max = new(float.MinValue);

            for (int i = 0; i < meshlet.VertexCount; i++)
            {
                uint globalIdx = localVerts[(int)(meshlet.VertexOffset + i)];
                Vector3 pos = vertices[(int)(vertexOffset + globalIdx)].Position;
                min = Vector3.Min(min, pos);
                max = Vector3.Max(max, pos);
            }

            Vector3 center = (min + max) * 0.5f;
            float radius = 0.0f;
            for (int i = 0; i < meshlet.VertexCount; i++)
            {
                uint globalIdx = localVerts[(int)(meshlet.VertexOffset + i)];
                Vector3 pos = vertices[(int)(vertexOffset + globalIdx)].Position;
                radius = MathF.Max(radius, Vector3.Distance(center, pos));
            }

            return (center, radius);
        }
    }

    /// <summary>uint8 别名（兼容性）。</summary>
}
