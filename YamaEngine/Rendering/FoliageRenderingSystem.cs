using System;
using YamaEngine.Core;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using YamaEngine.RHI;
using YamaEngine.Shaders;
using YamaEngine.World;

namespace YamaEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎 植被渲染系统 (Yama Foliage Rendering System)
    //
    // 这块先这样，回头再优化
    // 整体设计参考了 UE5 MicroCluster Foliage + Unity HDRP SpeedTree 的思路，
    // 但全部从零实现，不依赖任何第三方。
    //
    // 核心模块:
    //   1. Imposter 烘焙系统 - 八面体八方向公告牌，离屏渲染 3D 树木到 atlas
    //   2. 草地 GPU 实例化 - 百万级草地三角面，Compute Shader 剔除 + Indirect Draw
    //   3. 树木 LOD 链 - LOD0(MicroCluster/完整mesh) -> LOD1(简化) -> LOD2(impbillboard) -> LOD3(impostor)
    //   4. 风力系统 - 顶点级风力场，支持阵风/摆动/物理碰撞
    //   5. 季节系统 - 春夏秋冬颜色变化，落叶，积雪
    //   6. 植被交互 - 角色踩踏草地压扁，车辆碾压树木
    //   7. WPO (World Position Offset) - 草叶弯曲、树干摇晃
    //   8. Subsurface 散射 - 次表面散射模拟树叶透光
    //   9. Alpha 测试优化 - Mip 级 alpha 锐化防止远处出现透明洞
    //  10. Two-Sided GI - 双面植被的全局光照
    //
    // NOTE: 大量使用 StructuredBuffer 和 indirect draw，CPU 端基本不参与逐实例循环
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>植被渲染系统全局常量。</summary>
    public static class FoliageConstants
    {
        /// <summary>最大植被类型数。</summary>
        public const int MaxFoliageTypes = 128;

        /// <summary>单类型最大实例数。</summary>
        public const int MaxInstancesPerType = 1 << 20; // 100 万

        /// <summary>草地实例上限。</summary>
        public const int MaxGrassInstances = 1 << 22; // 400 万

        /// <summary>Imposter atlas 最大分辨率。</summary>
        public const int MaxImposterAtlasSize = 8192;

        /// <summary>八面体 impostor 采样方向数（每轴）。</summary>
        public const int OctahedralSlices = 16;

        /// <summary>Imposter 每帧烘焙的最大视角数。</summary>
        public const int MaxImposterFrames = 256;

        /// <summary>LOD 层数。</summary>
        public const int LODCount = 4;

        /// <summary>风力场分辨率。</summary>
        public const int WindFieldResolution = 128;

        /// <summary>交互影响图分辨率。</summary>
        public const int InteractionMapSize = 256;

        /// <summary>草地近距 mesh 切换距离（米）。</summary>
        public const float GrassMeshDistance = 50.0f;

        /// <summary>草地 billboard 切换距离（米）。</summary>
        public const float GrassBillboardDistance = 200.0f;

        /// <summary>草地最大渲染距离（米）。</summary>
        public const float GrassMaxDistance = 500.0f;

        /// <summary>树木最大渲染距离（米）。</summary>
        public const float TreeMaxDistance = 2000.0f;

        /// <summary>Imposter 烘焙的视锥层数（八面体参数化）。</summary>
        public const int ImposterOctahedralLayers = 8;

        // hack: 临时方案，后面把 GI 集成进 ProbeField
        /// <summary>是否启用植被双面 GI。</summary>
        public const bool EnableTwoSidedGI = true;
    }

    // ────────────────────────────────────────────────────────────────────────
    // 枚举
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>植被类型。</summary>
    public enum FoliageType
    {
        /// <summary>阔叶树（如橡树、枫树）。</summary>
        BroadleafTree = 0,
        /// <summary>针叶树（如松树、杉树）。</summary>
        ConiferTree = 1,
        /// <summary>灌木丛。</summary>
        Shrub = 2,
        /// <summary>草。</summary>
        Grass = 3,
        /// <summary>蕨类。</summary>
        Fern = 4,
        /// <summary>花。</summary>
        Flower = 5,
        /// <summary>仙人掌。</summary>
        Cactus = 6,
        /// <summary>竹子。</summary>
        Bamboo = 7,
        /// <summary>自定义。</summary>
        Custom = 0xFF
    }

    /// <summary>植被 LOD 层级。</summary>
    public enum FoliageLODLevel : byte
    {
        /// <summary>LOD0：完整 mesh（可启用 MicroCluster）。</summary>
        LOD0_FullMesh = 0,
        /// <summary>LOD1：简化 mesh。</summary>
        LOD1_Simplified = 1,
        /// <summary>LOD2：billboard（单一朝向公告牌）。</summary>
        LOD2_Billboard = 2,
        /// <summary>LOD3：impostor（八面体多视角公告牌）。</summary>
        LOD3_Impostor = 3,
        /// <summary>已剔除。</summary>
        Culled = 0xFF
    }

    /// <summary>风力动画质量。</summary>
    public enum FoliageWindQuality
    {
        Off = 0,
        /// <summary>简单正弦摆动。</summary>
        Low = 1,
        /// <summary>噪声 + 顶点权重。</summary>
        Medium = 2,
        /// <summary>方向噪声场 + 阵风。</summary>
        High = 3,
        /// <summary>物理摆动 + 二次谐波。</summary>
        Cinematic = 4
    }

    /// <summary>季节。</summary>
    public enum Season
    {
        Spring = 0,
        Summer = 1,
        Autumn = 2,
        Winter = 3
    }

    /// <summary>草地渲染模式。</summary>
    public enum GrassRenderMode
    {
        /// <summary>仅 mesh 草。</summary>
        MeshOnly = 0,
        /// <summary>仅公告牌草。</summary>
        BillboardOnly = 1,
        /// <summary>混合（按距离切换）。</summary>
        Hybrid = 2
    }

    /// <summary>Imposter 烘焙模式。</summary>
    public enum ImposterBakeMode
    {
        /// <summary>八面体八方向。</summary>
        Octahedral = 0,
        /// <summary>球面均匀采样。</summary>
        Spherical = 1,
        /// <summary>半圆切片（只烘焙上半球，地面方向用预计算）。</summary>
        HemisphereSlices = 2
    }

    /// <summary>Imposter 重建模式。</summary>
    public enum ImposterReconstructionMode
    {
        /// <summary>最近邻（最快）。</summary>
        Nearest = 0,
        /// <summary>三线性（八方向插值）。</summary>
        Trilinear = 1,
        /// <summary>八面体重心插值（最平滑）。</summary>
        Barycentric = 2
    }

    /// <summary>植被 Alpha 测试模式。</summary>
    public enum FoliageAlphaMode
    {
        /// <summary>标准 alpha test。</summary>
        AlphaTest = 0,
        /// <summary>to coverage（MSAA）。</summary>
        AlphaToCoverage = 1,
        /// <summary>alpha hash（ stochastic）。</summary>
        AlphaHash = 2
    }

    // ────────────────────────────────────────────────────────────────────────
    // GPU 数据结构（与 HLSL 严格对齐）
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 植被实例数据（驻 GPU）。每个实例 32 字节。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    public struct FoliageInstance
    {
        /// <summary>世界位置 XYZ + 随机种子 W。</summary>
        public Vector4 PositionSeed;
        /// <summary>缩放 XYZ + 旋转(弧度, 绕 Y) W。</summary>
        public Vector4 ScaleRotation;
        /// <summary>颜色乘数 RGB + 高度偏移 A。</summary>
        public Vector4 ColorHeight;
        /// <summary>风力相位偏移 XYZ + LOD 权重 W。</summary>
        public Vector4 WindPhaseLOD;

        /// <summary>世界位置。</summary>
        public readonly Vector3 WorldPosition => new(PositionSeed.X, PositionSeed.Y, PositionSeed.Z);
        /// <summary>缩放。</summary>
        public readonly Vector3 Scale => new(ScaleRotation.X, ScaleRotation.Y, ScaleRotation.Z);
        /// <summary>绕 Y 轴旋转（弧度）。</summary>
        public readonly float RotationY => ScaleRotation.W;
        /// <summary>随机种子。</summary>
        public readonly uint Seed => (uint)PositionSeed.W;
    }

    /// <summary>Indirect draw 参数（D3D12 DrawIndexedInstancedIndirect 兼容）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct IndirectDrawArgs
    {
        public uint IndexCountPerInstance;
        public uint InstanceCount;
        public uint StartIndexLocation;
        public int BaseVertexLocation;
        public uint StartInstanceLocation;
    }

    /// <summary>草地 billboard 实例（精简，16 字节）。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    public struct GrassBillboardInstance
    {
        /// <summary>世界位置 XYZ + 缩放 W。</summary>
        public Vector4 PositionScale;
        /// <summary>颜色 RGB + 随机旋转 W。</summary>
        public Vector4 ColorRotation;
    }

    /// <summary>Imposter 顶点（用于 impostor mesh 的顶点缓冲区）。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    public struct ImposterVertex
    {
        /// <summary>位置（impostor quad 的本地坐标，-0.5~0.5）。</summary>
        public Vector3 Position;
        /// <summary>UV。</summary>
        public Vector2 UV;
        /// <summary>法线（用于光照）。</summary>
        public Vector3 Normal;
        /// <summary>切片索引（八面体方向）。</summary>
        public float SliceIndex;
    }

    /// <summary>植被类型参数（GPU 常量缓冲区）。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    public struct FoliageTypeParams
    {
        /// <summary>LOD 切换距离（LOD0->1, 1->2, 2->3, 3->cull）。</summary>
        public Vector4 LODDistances; // x=LOD0->1, y=LOD1->2, z=LOD2->3, w=LOD3->cull
        /// <summary>风力参数（振幅 X, 频率 Y, 阵风强度 Z, 阵风频率 W）。</summary>
        public Vector4 WindParams;
        /// <summary>季节颜色乘数（春 X, 夏 Y, 秋 Z, 冬 W）。</summary>
        public Vector4 SeasonTint;
        /// <summary>SSS 颜色 + 强度。</summary>
        public Vector4 SSSColor;
        /// <summary>Alpha 阈值 + 杂项。</summary>
        public Vector4 AlphaParams; // x=alpha_cutoff, y=alpha_hash_scale, z=wpo_strength, w=double_sided
        /// <summary>Imposter 尺寸（atlas 中的单个切片像素大小）。</summary>
        public Vector4 ImposterParams; // x=atlas_size, y=slices_per_row, z=slice_size, w=frames_count
    }

    // ────────────────────────────────────────────────────────────────────────
    // 配置
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>植被渲染系统配置。</summary>
    public sealed class FoliageRenderConfig
    {
        /// <summary>风力质量。</summary>
        public FoliageWindQuality WindQuality = FoliageWindQuality.High;

        /// <summary>草地渲染模式。</summary>
        public GrassRenderMode GrassMode = GrassRenderMode.Hybrid;

        /// <summary>Imposter 烘焙模式。</summary>
        public ImposterBakeMode ImposterBake = ImposterBakeMode.Octahedral;

        /// <summary>Imposter 重建模式。</summary>
        public ImposterReconstructionMode ImposterReconstruct = ImposterReconstructionMode.Barycentric;

        /// <summary>Alpha 测试模式。</summary>
        public FoliageAlphaMode AlphaMode = FoliageAlphaMode.AlphaToCoverage;

        /// <summary>当前季节。</summary>
        public Season CurrentSeason = Season.Summer;

        /// <summary>季节过渡进度（0~1，用于平滑季节切换）。</summary>
        public float SeasonTransition = 1.0f;

        /// <summary>风力强度（0~1）。</summary>
        public float WindStrength = 0.6f;

        /// <summary>风力方向（世界空间，XZ 平面）。</summary>
        public Vector3 WindDirection = new(1, 0, 0.3f);

        /// <summary>SSS 强度。</summary>
        public float SSSIntensity = 0.8f;

        /// <summary>Imposter atlas 分辨率。</summary>
        public int ImposterAtlasSize = 4096;

        /// <summary>每帧最大 impostor 烘焙切片数。</summary>
        public int MaxImposterBakePerFrame = 8;

        /// <summary>启用交互（踩踏）。</summary>
        public bool EnableInteraction = true;

        /// <summary>交互半径（米）。</summary>
        public float InteractionRadius = 2.0f;

        /// <summary>交互强度。</summary>
        public float InteractionStrength = 1.0f;

        /// <summary>启用 HZB 遮挡剔除。</summary>
        public bool EnableHZBOcclusion = true;

        /// <summary>启用 MicroCluster 集成（LOD0 使用 MicroCluster）。</summary>
        public bool EnableMicroClusterIntegration = true;

        /// <summary>草地阴影投掷（是否让草地投射阴影）。</summary>
        public bool EnableGrassShadow = true;

        /// <summary>植被 GI 模式（0=关闭, 1=静态探针, 2=动态 SSGI）。</summary>
        public int GIMode = 1;

        /// <summary>最大可见植被实例数（per frame）。</summary>
        public int MaxVisibleInstances = 500000;

        /// <summary>应用到配置档位。</summary>
        public void ApplyPreset(string preset)
        {
            switch (preset.ToLowerInvariant())
            {
                case "low":
                    WindQuality = FoliageWindQuality.Low;
                    GrassMode = GrassRenderMode.BillboardOnly;
                    ImposterReconstruct = ImposterReconstructionMode.Nearest;
                    AlphaMode = FoliageAlphaMode.AlphaTest;
                    EnableInteraction = false;
                    EnableHZBOcclusion = false;
                    EnableMicroClusterIntegration = false;
                    EnableGrassShadow = false;
                    GIMode = 0;
                    ImposterAtlasSize = 2048;
                    break;
                case "medium":
                    WindQuality = FoliageWindQuality.Medium;
                    GrassMode = GrassRenderMode.Hybrid;
                    ImposterReconstruct = ImposterReconstructionMode.Trilinear;
                    AlphaMode = FoliageAlphaMode.AlphaTest;
                    EnableInteraction = true;
                    EnableHZBOcclusion = true;
                    EnableMicroClusterIntegration = false;
                    EnableGrassShadow = true;
                    GIMode = 1;
                    ImposterAtlasSize = 4096;
                    break;
                case "high":
                    WindQuality = FoliageWindQuality.High;
                    GrassMode = GrassRenderMode.Hybrid;
                    ImposterReconstruct = ImposterReconstructionMode.Barycentric;
                    AlphaMode = FoliageAlphaMode.AlphaToCoverage;
                    EnableInteraction = true;
                    EnableHZBOcclusion = true;
                    EnableMicroClusterIntegration = true;
                    EnableGrassShadow = true;
                    GIMode = 1;
                    ImposterAtlasSize = 4096;
                    break;
                case "ultra":
                case "cinematic":
                    WindQuality = FoliageWindQuality.Cinematic;
                    GrassMode = GrassRenderMode.MeshOnly;
                    ImposterReconstruct = ImposterReconstructionMode.Barycentric;
                    AlphaMode = FoliageAlphaMode.AlphaToCoverage;
                    EnableInteraction = true;
                    EnableHZBOcclusion = true;
                    EnableMicroClusterIntegration = true;
                    EnableGrassShadow = true;
                    GIMode = 2;
                    ImposterAtlasSize = 8192;
                    MaxVisibleInstances = 1000000;
                    break;
            }
        }

        public static FoliageRenderConfig Default => new();
    }

    // ────────────────────────────────────────────────────────────────────────
    // 八面体参数化（Imposter 方向编码）
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 八面体参数化：把球面方向编码到 2D 纹理上。
    /// 这是 impostor 系统的核心数学工具。
    /// 参考: "Octahedral Imposition Mapping" (Pranckevičius, 2017)
    /// </summary>
    public static class OctahedralMapping
    {
        /// <summary>球面方向 -> 八面体 UV（[-1,1]^2）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector2 Encode(Vector3 dir)
        {
            dir = Vector3.Normalize(dir);
            float l1norm = Math.Abs(dir.X) + Math.Abs(dir.Y) + Math.Abs(dir.Z);
            Vector2 uv;
            if (dir.Z < 0)
            {
                // 折叠下半球
                uv.X = (1.0f - Math.Abs(dir.Y)) * (dir.X >= 0 ? 1 : -1);
                uv.Y = (1.0f - Math.Abs(dir.X)) * (dir.Y >= 0 ? 1 : -1);
            }
            else
            {
                uv.X = dir.X;
                uv.Y = dir.Y;
            }
            // 归一化到 [0,1]
            return new Vector2(uv.X * 0.5f + 0.5f, uv.Y * 0.5f + 0.5f);
        }

        /// <summary>八面体 UV([0,1]^2) -> 球面方向。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 Decode(Vector2 uv)
        {
            Vector2 p = uv * 2.0f - Vector2.One; // [-1,1]
            float z = 1.0f - Math.Abs(p.X) - Math.Abs(p.Y);
            Vector3 dir;
            if (z < 0)
            {
                // 下半球展开
                float x = (1.0f - Math.Abs(p.Y)) * (p.X >= 0 ? 1 : -1);
                float y = (1.0f - Math.Abs(p.X)) * (p.Y >= 0 ? 1 : -1);
                dir = new Vector3(x, y, z);
            }
            else
            {
                dir = new Vector3(p.X, p.Y, z);
            }
            return Vector3.Normalize(dir);
        }

        /// <summary>生成 N 个均匀分布的八面体采样方向。</summary>
        public static Vector3[] GenerateSampleDirections(int n)
        {
            var dirs = new Vector3[n];
            // Fibonacci 球面分布
            float goldenRatio = (1.0f + MathF.Sqrt(5.0f)) * 0.5f;
            float angleIncrement = MathF.PI * 2.0f * goldenRatio;

            for (int i = 0; i < n; i++)
            {
                float t = (float)(i + 0.5) / n;
                float phi = MathF.Acos(1.0f - 2.0f * t); // 极角
                float theta = angleIncrement * i;          // 方位角
                dirs[i] = new Vector3(
                    MathF.Sin(phi) * MathF.Cos(theta),
                    MathF.Cos(phi),
                    MathF.Sin(phi) * MathF.Sin(theta));
            }
            return dirs;
        }

        /// <summary>计算八面体 UV 的 3 个最近邻切片索引（用于重心插值）。</summary>
        public static (int i0, int i1, int i2, Vector3 bary) FindNearestSlices(Vector2 uv, int slicesPerRow)
        {
            // UV -> 网格坐标
            float gx = uv.X * slicesPerRow;
            float gy = uv.Y * slicesPerRow;
            int x0 = (int)MathF.Floor(gx);
            int y0 = (int)MathF.Floor(gy);
            float fx = gx - x0;
            float fy = gy - y0;

            // 两个三角形，选择包含当前点的那个
            int i0, i1, i2;
            Vector3 bary;
            if (fx + fy < 1.0f)
            {
                // 下三角
                i0 = y0 * slicesPerRow + x0;
                i1 = y0 * slicesPerRow + ((x0 + 1) % slicesPerRow);
                i2 = ((y0 + 1) % slicesPerRow) * slicesPerRow + x0;
                bary = new Vector3(1.0f - fx - fy, fx, fy);
            }
            else
            {
                // 上三角
                i0 = ((y0 + 1) % slicesPerRow) * slicesPerRow + ((x0 + 1) % slicesPerRow);
                i1 = ((y0 + 1) % slicesPerRow) * slicesPerRow + x0;
                i2 = y0 * slicesPerRow + ((x0 + 1) % slicesPerRow);
                bary = new Vector3(fx + fy - 1.0f, 1.0f - fx, 1.0f - fy);
            }
            return (i0, i1, i2, bary);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 风力系统
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>风力场参数。</summary>
    public sealed class WindFieldParams
    {
        /// <summary>风力方向（归一化）。</summary>
        public Vector3 Direction = new(1, 0, 0.3f);
        /// <summary>风力强度。</summary>
        public float Strength = 0.6f;
        /// <summary>主频（摆动频率）。</summary>
        public float Frequency = 0.8f;
        /// <summary>阵风频率。</summary>
        public float GustFrequency = 0.15f;
        /// <summary>阵风强度。</summary>
        public float GustStrength = 0.4f;
        /// <summary>阵风半径（米）。</summary>
        public float GustRadius = 15.0f;
        /// <summary>时间（秒）。</summary>
        public float Time;
        /// <summary>噪声纹理。</summary>
        public RHITexture? NoiseTexture;
        /// <summary>风力场纹理（运行时更新）。</summary>
        public RHITexture? WindFieldTexture;
    }

    /// <summary>风力场计算器（CPU 端参考实现，实际用 GPU）。</summary>
    public static class WindFieldCalculator
    {
        /// <summary>简单 2D 噪声（用于风力场）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Noise2D(float x, float y)
        {
            // 简化的值噪声
            int ix = (int)MathF.Floor(x);
            int iy = (int)MathF.Floor(y);
            float fx = x - ix;
            float fy = y - iy;

            // 平滑插值
            float sx = fx * fx * (3 - 2 * fx);
            float sy = fy * fy * (3 - 2 * fy);

            float n00 = Hash(ix, iy);
            float n10 = Hash(ix + 1, iy);
            float n01 = Hash(ix, iy + 1);
            float n11 = Hash(ix + 1, iy + 1);

            float n0 = n00 * (1 - sx) + n10 * sx;
            float n1 = n01 * (1 - sx) + n11 * sx;
            return n0 * (1 - sy) + n1 * sy;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float Hash(int x, int y)
        {
            uint h = (uint)(x * 374761393 + y * 668265263);
            h = (h ^ (h >> 13)) * 1274126177u;
            return (h & 0xFFFFFF) / (float)0xFFFFFF;
        }

        /// <summary>计算某点的风力偏移（WPO, World Position Offset）。</summary>
        public static Vector3 ComputeWPO(
            Vector3 worldPos, Vector3 vertexNormal, float vertexHeight,
            WindFieldParams wind, FoliageWindQuality quality)
        {
            if (quality == FoliageWindQuality.Off) return Vector3.Zero;

            Vector3 windDir = Vector3.Normalize(wind.Direction);
            float t = wind.Time;

            Vector3 offset = Vector3.Zero;

            switch (quality)
            {
                case FoliageWindQuality.Low:
                    {
                        // 简单正弦摆动，根部不动，顶部摆
                        float sway = MathF.Sin(t * wind.Frequency * 2.0f) * wind.Strength * vertexHeight * 0.3f;
                        offset = windDir * sway;
                        break;
                    }
                case FoliageWindQuality.Medium:
                    {
                        // 噪声 + 顶点权重
                        float n = Noise2D(worldPos.X * 0.1f + t * 0.3f, worldPos.Z * 0.1f);
                        float sway = (MathF.Sin(t * wind.Frequency * 2.0f) + n * 0.5f) * wind.Strength * vertexHeight * 0.4f;
                        offset = windDir * sway;
                        break;
                    }
                case FoliageWindQuality.High:
                    {
                        // 方向噪声场 + 阵风
                        float n1 = Noise2D(worldPos.X * 0.05f + t * 0.2f, worldPos.Z * 0.05f);
                        float n2 = Noise2D(worldPos.X * 0.15f + t * 0.5f, worldPos.Z * 0.15f);

                        // 主摆动
                        float mainSway = MathF.Sin(t * wind.Frequency * 2.0f + n1 * 6.0f) * wind.Strength * vertexHeight * 0.5f;
                        // 高频抖动（叶子级别）
                        float leafFlutter = n2 * wind.Strength * vertexHeight * 0.15f;

                        // 阵风
                        float gust = ComputeGust(worldPos, t, wind);
                        float gustEffect = gust * wind.GustStrength * vertexHeight * 0.8f;

                        offset = windDir * (mainSway + leafFlutter + gustEffect);
                        break;
                    }
                case FoliageWindQuality.Cinematic:
                    {
                        // 物理摆动 + 二次谐波
                        float n1 = Noise2D(worldPos.X * 0.05f + t * 0.2f, worldPos.Z * 0.05f);
                        float n2 = Noise2D(worldPos.X * 0.15f + t * 0.5f, worldPos.Z * 0.15f);
                        float n3 = Noise2D(worldPos.X * 0.3f + t * 1.0f, worldPos.Z * 0.3f);

                        // 基波
                        float f1 = MathF.Sin(t * wind.Frequency * 2.0f + n1 * 6.0f);
                        // 二次谐波
                        float f2 = MathF.Sin(t * wind.Frequency * 4.0f + n2 * 8.0f) * 0.5f;
                        // 三次谐波
                        float f3 = MathF.Sin(t * wind.Frequency * 8.0f + n3 * 10.0f) * 0.25f;

                        float sway = (f1 + f2 + f3) * wind.Strength * vertexHeight * 0.5f;

                        // 叶子级高频
                        float leafFlutter = n3 * wind.Strength * vertexHeight * 0.1f;

                        // 阵风
                        float gust = ComputeGust(worldPos, t, wind);
                        float gustEffect = gust * wind.GustStrength * vertexHeight;

                        // 物理阻尼（让运动看起来更自然）
                        float damping = 1.0f - MathF.Exp(-vertexHeight * 0.5f);

                        offset = windDir * ((sway + leafFlutter + gustEffect) * damping);
                        break;
                    }
            }

            return offset;
        }

        /// <summary>计算阵风强度（在某点某时刻）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float ComputeGust(Vector3 worldPos, float t, WindFieldParams wind)
        {
            // 阵风以波的形式传播
            float gustPhase = Vector3.Dot(worldPos, wind.Direction) * 0.1f - t * wind.GustFrequency * 10.0f;
            float gust = MathF.Max(0, MathF.Sin(gustPhase)); // 只取正半周
            // 加噪声让阵风不规则
            gust *= Noise2D(worldPos.X * 0.02f + t * 0.1f, worldPos.Z * 0.02f);
            return gust;
        }

        /// <summary>更新风力场纹理（CPU 端生成数据，上传到 GPU）。</summary>
        public static float[] GenerateWindFieldData(int resolution, WindFieldParams wind)
        {
            var data = new float[resolution * resolution * 4]; // RGBA float
            for (int y = 0; y < resolution; y++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    int idx = (y * resolution + x) * 4;
                    float u = (float)x / resolution;
                    float v = (float)y / resolution;

                    // 主风力方向
                    data[idx + 0] = wind.Direction.X * wind.Strength;
                    data[idx + 1] = wind.Direction.Z * wind.Strength;
                    // 阵风强度
                    data[idx + 2] = ComputeGust(new Vector3(u * 100, 0, v * 100), wind.Time, wind);
                    // 时间相位
                    data[idx + 3] = wind.Time;
                }
            }
            return data;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // Imposter 烘焙系统
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Imposter 烘焙请求。</summary>
    public sealed class ImposterBakeRequest
    {
        /// <summary>源 mesh。</summary>
        public RHIMesh? SourceMesh;
        /// <summary>植被类型。</summary>
        public FoliageType Type;
        /// <summary>烘焙模式。</summary>
        public ImposterBakeMode Mode = ImposterBakeMode.Octahedral;
        /// <summary>切片数（八面体 = OctahedralSlices^2）。</summary>
        public int SliceCount = FoliageConstants.OctahedralSlices * FoliageConstants.OctahedralSlices;
        /// <summary>每切片分辨率。</summary>
        public int SliceResolution = 256;
        /// <summary>烘焙帧数（动画 impostor）。</summary>
        public int FrameCount = 1;
        /// <summary>是否烘焙法线。</summary>
        public bool BakeNormal = true;
        /// <summary>是否烘焙深度。</summary>
        public bool BakeDepth = true;
        /// <summary>是否烘焙颜色。</summary>
        public bool BakeColor = true;
        /// <summary>是否烘焙 ORM。</summary>
        public bool BakeORM = true;
        /// <summary>是否烘焙发光。</summary>
        public bool BakeEmissive = false;
    }

    /// <summary>Imposter 烘焙结果。</summary>
    public sealed class ImposterBakeResult
    {
        /// <summary>颜色 atlas。</summary>
        public RHITexture? ColorAtlas;
        /// <summary>法线 atlas。</summary>
        public RHITexture? NormalAtlas;
        /// <summary>深度 atlas。</summary>
        public RHITexture? DepthAtlas;
        /// <summary>ORM atlas。</summary>
        public RHITexture? ORMAtlas;
        /// <summary>切片数。</summary>
        public int SliceCount;
        /// <summary>每切片分辨率。</summary>
        public int SliceResolution;
        /// <summary>切片方向（每个切片对应的视角方向）。</summary>
        public Vector3[] SliceDirections = Array.Empty<Vector3>();
        /// <summary>Imposter mesh（单 quad，UV 指向 atlas）。</summary>
        public RHIMesh? ImposterMesh;
        /// <summary>是否完成。</summary>
        public bool IsComplete;
        /// <summary>已烘焙切片数。</summary>
        public int BakedSlices;
    }

    /// <summary>Imposter 烘焙器。</summary>
    public sealed class ImposterBaker : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly FoliageRenderConfig _config;
        private RHITexture? _bakeColorTarget;
        private RHITexture? _bakeNormalTarget;
        private RHITexture? _bakeDepthTarget;
        private RHICommandList? _bakeCmdList;
        private bool _disposed;

        /// <summary>当前烘焙任务。</summary>
        public ImposterBakeRequest? CurrentRequest { get; private set; }

        /// <summary>烘焙结果。</summary>
        public ImposterBakeResult Result { get; } = new();

        public ImposterBaker(RHIDevice device, FoliageRenderConfig config)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        /// <summary>开始烘焙。</summary>
        public void BeginBake(ImposterBakeRequest request)
        {
            CurrentRequest = request ?? throw new ArgumentNullException(nameof(request));

            // 创建渲染目标
            int sliceRes = request.SliceResolution;
            _bakeColorTarget = _device.CreateTexture(new RHITextureDesc
            {
                Width = sliceRes, Height = sliceRes,
                Format = RHIFormat.R8G8B8A8_UNorm,
                Usage = RHITextureUsage.RenderTarget | RHITextureUsage.ShaderResource,
                DebugName = "ImposterBake_Color"
            });
            _bakeNormalTarget = _device.CreateTexture(new RHITextureDesc
            {
                Width = sliceRes, Height = sliceRes,
                Format = RHIFormat.R10G10B10A2_UNorm,
                Usage = RHITextureUsage.RenderTarget | RHITextureUsage.ShaderResource,
                DebugName = "ImposterBake_Normal"
            });
            _bakeDepthTarget = _device.CreateTexture(new RHITextureDesc
            {
                Width = sliceRes, Height = sliceRes,
                Format = RHIFormat.D32_Float,
                Usage = RHITextureUsage.DepthStencil | RHITextureUsage.ShaderResource,
                DebugName = "ImposterBake_Depth"
            });

            // 创建 atlas
            int atlasSize = _config.ImposterAtlasSize;
            Result.ColorAtlas = _device.CreateTexture(new RHITextureDesc
            {
                Width = atlasSize, Height = atlasSize,
                Format = RHIFormat.R8G8B8A8_UNorm,
                Usage = RHITextureUsage.RenderTarget | RHITextureUsage.ShaderResource | RHITextureUsage.UnorderedAccess,
                DebugName = "Imposter_ColorAtlas"
            });
            Result.NormalAtlas = _device.CreateTexture(new RHITextureDesc
            {
                Width = atlasSize, Height = atlasSize,
                Format = RHIFormat.R10G10B10A2_UNorm,
                Usage = RHITextureUsage.RenderTarget | RHITextureUsage.ShaderResource | RHITextureUsage.UnorderedAccess,
                DebugName = "Imposter_NormalAtlas"
            });
            Result.DepthAtlas = _device.CreateTexture(new RHITextureDesc
            {
                Width = atlasSize, Height = atlasSize,
                Format = RHIFormat.R32_Float,
                Usage = RHITextureUsage.RenderTarget | RHITextureUsage.ShaderResource | RHITextureUsage.UnorderedAccess,
                DebugName = "Imposter_DepthAtlas"
            });
            if (request.BakeORM)
            {
                Result.ORMAtlas = _device.CreateTexture(new RHITextureDesc
                {
                    Width = atlasSize, Height = atlasSize,
                    Format = RHIFormat.R8G8B8A8_UNorm,
                    Usage = RHITextureUsage.RenderTarget | RHITextureUsage.ShaderResource | RHITextureUsage.UnorderedAccess,
                    DebugName = "Imposter_ORMAtlas"
                });
            }

            // 生成切片方向
            Result.SliceDirections = OctahedralMapping.GenerateSampleDirections(request.SliceCount);
            Result.SliceCount = request.SliceCount;
            Result.SliceResolution = sliceRes;
            Result.BakedSlices = 0;
            Result.IsComplete = false;

            _bakeCmdList = _device.CreateCommandList(RHIQueueType.Graphics, "ImposterBakeCmd");
        }

        /// <summary>烘焙一个切片（每帧调用，分摊到多帧）。</summary>
        /// <returns>是否完成所有切片。</returns>
        public bool BakeSlice()
        {
            if (CurrentRequest == null || Result.IsComplete) return true;
            if (Result.BakedSlices >= Result.SliceCount)
            {
                Result.IsComplete = true;
                // 生成 impostor mesh（单 quad）
                Result.ImposterMesh = GenerateImposterQuad();
                return true;
            }

            int sliceIdx = Result.BakedSlices;
            Vector3 dir = Result.SliceDirections[sliceIdx];

            // NOTE: 这里简化了实际的 GPU 渲染流程
            // 实际上需要：
            // 1. 设置正交相机看向 -dir 方向
            // 2. 渲染源 mesh 到 _bakeColorTarget/NormalTarget/DepthTarget
            // 3. 把渲染结果 copy 到 atlas 的对应位置

            // 计算切片在 atlas 中的位置
            int slicesPerRow = (int)MathF.Ceiling(MathF.Sqrt(Result.SliceCount));
            int sliceX = sliceIdx % slicesPerRow;
            int sliceY = sliceIdx / slicesPerRow;
            int sliceSize = Result.SliceResolution;

            // 渲染源 mesh 从 dir 方向
            RenderMeshToSlice(dir, sliceIdx);

            // Copy 到 atlas
            CopySliceToAtlas(sliceX * sliceSize, sliceY * sliceSize, sliceSize);

            Result.BakedSlices++;
            return false;
        }

        /// <summary>逐帧烘焙（分摊到多帧避免卡顿）。</summary>
        public void BakeProgressive(int maxSlicesPerFrame)
        {
            for (int i = 0; i < maxSlicesPerFrame; i++)
            {
                if (BakeSlice()) break;
            }
        }

        /// <summary>立即烘焙所有切片（阻塞）。</summary>
        public void BakeAll()
        {
            while (!BakeSlice()) { }
        }

        private void RenderMeshToSlice(Vector3 viewDir, int sliceIdx)
        {
            if (_bakeCmdList == null || CurrentRequest?.SourceMesh == null) return;

            // 设置渲染目标
            _bakeCmdList.SetRenderTargets(
                new[] { _bakeColorTarget!, _bakeNormalTarget! },
                _bakeDepthTarget!);
            _bakeCmdList.ClearRenderTarget(_bakeColorTarget!, new Vector4(0, 0, 0, 0));
            _bakeCmdList.ClearRenderTarget(_bakeNormalTarget!, new Vector4(0.5f, 0.5f, 1.0f, 1.0f));
            _bakeCmdList.ClearDepthStencil(_bakeDepthTarget!, 1.0f, 0);

            _bakeCmdList.SetViewport(0, 0, Result.SliceResolution, Result.SliceResolution);

            // 计算视角矩阵（从 viewDir 方向看向原点）
            Vector3 eye = -viewDir * 10.0f;
            Matrix4x4 view = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY);
            // 正交投影
            float orthoSize = 3.0f; // 根据 mesh 包围盒调整
            Matrix4x4 proj = Matrix4x4.CreateOrthographic(orthoSize * 2, orthoSize * 2, 0.1f, 100.0f);
            Matrix4x4 viewProj = view * proj;

            // 设置常量并绘制
            // _bakeCmdList.SetConstantBuffer(0, ...);
            // _bakeCmdList.DrawIndexed(CurrentRequest.SourceMesh);

            _bakeCmdList.Execute();
        }

        private void CopySliceToAtlas(int dstX, int dstY, int sliceSize)
        {
            if (_bakeCmdList == null) return;
            _bakeCmdList.CopyTextureRegion(
                Result.ColorAtlas!, _bakeColorTarget!,
                dstX, dstY, 0, 0, sliceSize, sliceSize);
            _bakeCmdList.CopyTextureRegion(
                Result.NormalAtlas!, _bakeNormalTarget!,
                dstX, dstY, 0, 0, sliceSize, sliceSize);
            _bakeCmdList.CopyTextureRegion(
                Result.DepthAtlas!, _bakeDepthTarget!,
                dstX, dstY, 0, 0, sliceSize, sliceSize);
            _bakeCmdList.Execute();
        }

        /// <summary>生成 impostor 用的 quad mesh。</summary>
        private RHIMesh GenerateImposterQuad()
        {
            // 一个 1x1 quad，UV 覆盖整张 atlas
            var vertices = new ImposterVertex[4];
            vertices[0] = new ImposterVertex
            {
                Position = new Vector3(-0.5f, -0.5f, 0),
                UV = new Vector2(0, 1),
                Normal = new Vector3(0, 0, 1),
                SliceIndex = 0
            };
            vertices[1] = new ImposterVertex
            {
                Position = new Vector3(0.5f, -0.5f, 0),
                UV = new Vector2(1, 1),
                Normal = new Vector3(0, 0, 1),
                SliceIndex = 0
            };
            vertices[2] = new ImposterVertex
            {
                Position = new Vector3(0.5f, 0.5f, 0),
                UV = new Vector2(1, 0),
                Normal = new Vector3(0, 0, 1),
                SliceIndex = 0
            };
            vertices[3] = new ImposterVertex
            {
                Position = new Vector3(-0.5f, 0.5f, 0),
                UV = new Vector2(0, 0),
                Normal = new Vector3(0, 0, 1),
                SliceIndex = 0
            };

            int[] indices = { 0, 1, 2, 0, 2, 3 };

            // 创建 RHI mesh（渲染命名空间视图句柄；顶点/索引数据供后端上传）
            return new RHIMesh
            {
                VertexCount = vertices.Length,
                IndexCount = indices.Length,
                VertexStride = Marshal.SizeOf<ImposterVertex>()
            };
        }

        /// <summary>计算 impostor 采样方向（从相机到实例中心）。</summary>
        public static Vector3 ComputeImposterSampleDir(Vector3 cameraPos, Vector3 instancePos)
        {
            return Vector3.Normalize(cameraPos - instancePos);
        }

        /// <summary>计算 impostor atlas 上的 UV 偏移和缩放（找到最匹配的切片）。</summary>
        public static Vector4 ComputeSliceUV(int sliceIndex, int slicesPerRow, int totalSlices)
        {
            int sx = sliceIndex % slicesPerRow;
            int sy = sliceIndex / slicesPerRow;
            float invSlices = 1.0f / slicesPerRow;
            return new Vector4(sx * invSlices, sy * invSlices, invSlices, invSlices);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _bakeColorTarget?.Dispose();
            _bakeNormalTarget?.Dispose();
            _bakeDepthTarget?.Dispose();
            _bakeCmdList?.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 植被类型描述
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>单个植被类型描述。</summary>
    public sealed class FoliageTypeDesc
    {
        /// <summary>类型 ID。</summary>
        public int Id;
        /// <summary>类型。</summary>
        public FoliageType Type = FoliageType.BroadleafTree;
        /// <summary>名称。</summary>
        public string Name = "Tree";
        /// <summary>LOD0 mesh（最高细节）。</summary>
        public RHIMesh? LOD0Mesh;
        /// <summary>LOD1 mesh（简化）。</summary>
        public RHIMesh? LOD1Mesh;
        /// <summary>LOD2 mesh（billboard）。</summary>
        public RHIMesh? LOD2Mesh;
        /// <summary>Imposter mesh + atlas。</summary>
        public ImposterBakeResult? Imposter;
        /// <summary>材质。</summary>
        public RHIMaterial? Material;
        /// <summary>LOD 距离。</summary>
        public Vector4 LODDistances = new(50, 150, 400, 1500);
        /// <summary>风力参数。</summary>
        public Vector4 WindParams = new(0.3f, 0.8f, 0.4f, 0.15f);
        /// <summary>SSS 颜色。</summary>
        public Vector3 SSSColor = new(0.8f, 0.4f, 0.2f);
        /// <summary>SSS 强度。</summary>
        public float SSSStrength = 0.5f;
        /// <summary>Alpha 阈值。</summary>
        public float AlphaCutoff = 0.5f;
        /// <summary>Alpha hash 缩放。</summary>
        public float AlphaHashScale = 1.0f;
        /// <summary>WPO 强度。</summary>
        public float WPOStrength = 1.0f;
        /// <summary>是否双面。</summary>
        public bool DoubleSided = true;
        /// <summary>季节颜色（春/夏/秋/冬）。</summary>
        public Vector4[] SeasonColors = new Vector4[4];
        /// <summary>季节过渡颜色（秋冬之间的落叶色等）。</summary>
        public Vector4[] SeasonTransitionColors = new Vector4[4];
        /// <summary>实例数。</summary>
        public int InstanceCount;
        /// <summary>实例缓冲区。</summary>
        public RHIBuffer? InstanceBuffer;
        /// <summary>Indirect draw args 缓冲区（每个 LOD 一个）。</summary>
        public RHIBuffer?[] IndirectArgsBuffers = new RHIBuffer?[FoliageConstants.LODCount];
        /// <summary>是否已初始化。</summary>
        public bool IsInitialized;

        /// <summary>初始化默认季节颜色。</summary>
        public void InitDefaultSeasonColors()
        {
            // 春：嫩绿
            SeasonColors[0] = new Vector4(0.5f, 0.8f, 0.3f, 1.0f);
            // 夏：深绿
            SeasonColors[1] = new Vector4(0.2f, 0.6f, 0.2f, 1.0f);
            // 秋：橙红
            SeasonColors[2] = new Vector4(0.8f, 0.4f, 0.1f, 1.0f);
            // 冬：灰白（带积雪）
            SeasonColors[3] = new Vector4(0.7f, 0.7f, 0.75f, 1.0f);

            // 过渡色
            SeasonTransitionColors[0] = new Vector4(0.4f, 0.7f, 0.25f, 1.0f); // 春夏
            SeasonTransitionColors[1] = new Vector4(0.5f, 0.5f, 0.15f, 1.0f);  // 夏秋
            SeasonTransitionColors[2] = new Vector4(0.75f, 0.55f, 0.4f, 1.0f); // 秋冬
            SeasonTransitionColors[3] = new Vector4(0.6f, 0.75f, 0.28f, 1.0f); // 冬春
        }

        /// <summary>获取当前季节颜色。</summary>
        public Vector4 GetSeasonColor(Season season, float transition)
        {
            int curIdx = (int)season;
            int nextIdx = (curIdx + 1) % 4;
            Vector4 cur = SeasonColors[curIdx];
            Vector4 next = SeasonTransitionColors[curIdx];
            return Vector4.Lerp(cur, next, transition);
        }

        /// <summary>转换为 GPU 参数。</summary>
        public FoliageTypeParams ToGPUParams()
        {
            return new FoliageTypeParams
            {
                LODDistances = LODDistances,
                WindParams = WindParams,
                SeasonTint = SeasonColors[(int)Type % 4],
                SSSColor = new Vector4(SSSColor, SSSStrength),
                AlphaParams = new Vector4(AlphaCutoff, AlphaHashScale, WPOStrength, DoubleSided ? 1 : 0),
                ImposterParams = Imposter != null
                    ? new Vector4(_config_imposterAtlasSize, _config_slicesPerRow, _config_sliceSize, Imposter.SliceCount)
                    : Vector4.Zero
            };
        }

        // hack: 临时把配置存在静态变量里，后面改成从 config 读
        private static float _config_imposterAtlasSize = 4096;
        private static float _config_slicesPerRow = 16;
        private static float _config_sliceSize = 256;
    }

    // ────────────────────────────────────────────────────────────────────────
    // 实例剔除系统
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>植被实例剔除器（CPU 端参考实现，实际用 GPU Compute）。</summary>
    public sealed class FoliageCuller
    {
        private readonly FoliageRenderConfig _config;

        public FoliageCuller(FoliageRenderConfig config)
        {
            _config = config;
        }

        /// <summary>剔除不可见实例，输出每个 LOD 的可见实例列表。</summary>
        public void Cull(
            FoliageInstance[] allInstances,
            int instanceCount,
            Matrix4x4 viewProj,
            Vector3 cameraPos,
            Vector4 lodDistances,
            RHITexture? hzbTexture,
            out List<int>[] visiblePerLOD)
        {
            visiblePerLOD = new List<int>[FoliageConstants.LODCount];
            for (int i = 0; i < FoliageConstants.LODCount; i++)
                visiblePerLOD[i] = new List<int>();

            float maxDist = lodDistances.W;
            if (maxDist <= 0) maxDist = FoliageConstants.TreeMaxDistance;

            for (int i = 0; i < instanceCount; i++)
            {
                ref var inst = ref allInstances[i];
                Vector3 worldPos = inst.WorldPosition;

                // 距离剔除
                float dist = Vector3.Distance(cameraPos, worldPos);
                if (dist > maxDist) continue;

                // 视锥剔除
                Vector4 clipPos = Vector4.Transform(new Vector4(worldPos, 1), viewProj);
                if (clipPos.W <= 0) continue;
                Vector3 ndc = new Vector3(
                    clipPos.X / clipPos.W,
                    clipPos.Y / clipPos.W,
                    clipPos.Z / clipPos.W);
                if (Math.Abs(ndc.X) > 1.2f || Math.Abs(ndc.Y) > 1.2f ||
                    ndc.Z < -1.2f || ndc.Z > 1.2f)
                    continue;

                // HZB 遮挡剔除（可选）
                // NOTE: 实际实现需要采样 HZB mip chain
                // if (_config.EnableHZBOcclusion && hzbTexture != null)
                // {
                //     if (IsOccluded(worldPos, dist, hzbTexture)) continue;
                // }

                // 确定 LOD
                FoliageLODLevel lod = DetermineLOD(dist, lodDistances);
                if (lod == FoliageLODLevel.Culled) continue;

                visiblePerLOD[(int)lod].Add(i);
            }

            // 限制最大可见数
            int totalVisible = 0;
            foreach (var list in visiblePerLOD) totalVisible += list.Count;
            if (totalVisible > _config.MaxVisibleInstances)
            {
                // 按距离排序，保留近的
                float ratio = (float)_config.MaxVisibleInstances / totalVisible;
                for (int i = 0; i < FoliageConstants.LODCount; i++)
                {
                    int keep = (int)(visiblePerLOD[i].Count * ratio);
                    if (keep < visiblePerLOD[i].Count)
                        visiblePerLOD[i].RemoveRange(keep, visiblePerLOD[i].Count - keep);
                }
            }
        }

        /// <summary>根据距离确定 LOD。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FoliageLODLevel DetermineLOD(float dist, Vector4 lodDistances)
        {
            if (dist < lodDistances.X) return FoliageLODLevel.LOD0_FullMesh;
            if (dist < lodDistances.Y) return FoliageLODLevel.LOD1_Simplified;
            if (dist < lodDistances.Z) return FoliageLODLevel.LOD2_Billboard;
            if (dist < lodDistances.W) return FoliageLODLevel.LOD3_Impostor;
            return FoliageLODLevel.Culled;
        }

        /// <summary>计算 indirect draw 参数。</summary>
        public static IndirectDrawArgs ComputeDrawArgs(RHIMesh mesh, int instanceCount)
        {
            return new IndirectDrawArgs
            {
                IndexCountPerInstance = (uint)mesh.IndexCount,
                InstanceCount = (uint)instanceCount,
                StartIndexLocation = 0,
                BaseVertexLocation = 0,
                StartInstanceLocation = 0
            };
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 季节系统
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>季节系统（颜色变化、落叶、积雪）。</summary>
    public sealed class SeasonSystem
    {
        private Season _currentSeason = Season.Summer;
        private Season _targetSeason = Season.Summer;
        private float _transitionProgress = 1.0f;
        private float _transitionSpeed = 0.1f; // 每秒过渡比例

        /// <summary>当前季节。</summary>
        public Season CurrentSeason => _currentSeason;

        /// <summary>目标季节。</summary>
        public Season TargetSeason => _targetSeason;

        /// <summary>过渡进度（0~1）。</summary>
        public float TransitionProgress => _transitionProgress;

        /// <summary>设置季节（带过渡动画）。</summary>
        public void SetSeason(Season season, bool instant = false)
        {
            if (season == _targetSeason && !instant) return;
            _targetSeason = season;
            if (instant)
            {
                _currentSeason = season;
                _transitionProgress = 1.0f;
            }
            else
            {
                _transitionProgress = 0.0f;
            }
        }

        /// <summary>更新（每帧调用）。</summary>
        public void Update(float dt)
        {
            if (_transitionProgress < 1.0f)
            {
                _transitionProgress = MathF.Min(1.0f, _transitionProgress + dt * _transitionSpeed);
                if (_transitionProgress >= 1.0f)
                {
                    _currentSeason = _targetSeason;
                }
            }
        }

        /// <summary>获取季节颜色乘数。</summary>
        public Vector4 GetSeasonTint(FoliageTypeDesc typeDesc)
        {
            if (_transitionProgress >= 1.0f)
            {
                return typeDesc.SeasonColors[(int)_currentSeason];
            }
            // 过渡中
            Vector4 cur = typeDesc.SeasonColors[(int)_currentSeason];
            Vector4 next = typeDesc.SeasonColors[(int)_targetSeason];
            return Vector4.Lerp(cur, next, _transitionProgress);
        }

        /// <summary>获取落叶强度（秋天最强）。</summary>
        public float GetLeafFallIntensity()
        {
            return _currentSeason switch
            {
                Season.Autumn => 1.0f,
                Season.Winter => 0.3f,
                Season.Spring => 0.1f,
                Season.Summer => 0.0f,
                _ => 0.0f
            };
        }

        /// <summary>获取积雪强度（冬天最强）。</summary>
        public float GetSnowIntensity()
        {
            return _currentSeason switch
            {
                Season.Winter => 1.0f,
                Season.Autumn => _transitionProgress * 0.3f,
                Season.Spring => (1.0f - _transitionProgress) * 0.3f,
                Season.Summer => 0.0f,
                _ => 0.0f
            };
        }

        /// <summary>获取积雪颜色。</summary>
        public Vector3 GetSnowColor()
        {
            // 带一点蓝调
            return new Vector3(0.85f, 0.88f, 0.95f);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 植被交互系统
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>植被交互影响点（角色/车辆位置）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FoliageInteractionPoint
    {
        /// <summary>世界位置。</summary>
        public Vector3 Position;
        /// <summary>影响半径。</summary>
        public float Radius;
        /// <summary>影响强度（0~1，1=完全压扁）。</summary>
        public float Strength;
        /// <summary>速度（用于动态弯曲方向）。</summary>
        public Vector3 Velocity;
        /// <summary>生命周期（秒，-1=永久）。</summary>
        public float Lifetime;
        /// <summary>已存在时间。</summary>
        public float Age;
        /// <summary>是否激活。</summary>
        public int Active;
    }

    /// <summary>植被交互系统。</summary>
    public sealed class FoliageInteractionSystem : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly FoliageRenderConfig _config;
        private readonly List<FoliageInteractionPoint> _points = new();
        private RHIBuffer? _interactionBuffer;
        private RHITexture? _interactionMap;
        private bool _disposed;

        /// <summary>最大交互点数。</summary>
        public int MaxPoints { get; } = 64;

        public FoliageInteractionSystem(RHIDevice device, FoliageRenderConfig config)
        {
            _device = device;
            _config = config;
        }

        /// <summary>初始化。</summary>
        public void Initialize()
        {
            _interactionBuffer = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = (uint)(MaxPoints * Marshal.SizeOf<FoliageInteractionPoint>()),
                Flags = RHIBufferFlags.StructuredBuffer | RHIBufferFlags.UnorderedAccess,
                Stride = (uint)Marshal.SizeOf<FoliageInteractionPoint>(),
                DebugName = "Foliage_InteractionBuffer"
            });

            _interactionMap = _device.CreateTexture(new RHITextureDesc
            {
                Width = FoliageConstants.InteractionMapSize,
                Height = FoliageConstants.InteractionMapSize,
                Format = RHIFormat.R16G16B16A16_Float,
                Usage = RHITextureUsage.UnorderedAccess | RHITextureUsage.ShaderResource,
                DebugName = "Foliage_InteractionMap"
            });
        }

        /// <summary>添加交互点。</summary>
        public void AddPoint(Vector3 position, float radius, float strength, Vector3 velocity, float lifetime = -1)
        {
            // 找一个未激活的槽位
            for (int i = 0; i < _points.Count; i++)
            {
                if (_points[i].Active == 0)
                {
                    _points[i] = new FoliageInteractionPoint
                    {
                        Position = position,
                        Radius = radius,
                        Strength = strength,
                        Velocity = velocity,
                        Lifetime = lifetime,
                        Age = 0,
                        Active = 1
                    };
                    return;
                }
            }

            if (_points.Count < MaxPoints)
            {
                _points.Add(new FoliageInteractionPoint
                {
                    Position = position,
                    Radius = radius,
                    Strength = strength,
                    Velocity = velocity,
                    Lifetime = lifetime,
                    Age = 0,
                    Active = 1
                });
            }
        }

        /// <summary>更新（每帧调用）。</summary>
        public void Update(float dt)
        {
            for (int i = 0; i < _points.Count; i++)
            {
                if (_points[i].Active == 0) continue;
                _points[i] = UpdatePoint(_points[i], dt);
            }

            // 上传到 GPU
            if (_interactionBuffer != null && _points.Count > 0)
            {
                var data = _points.ToArray();
                _device.UpdateBuffer(_interactionBuffer, data);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static FoliageInteractionPoint UpdatePoint(FoliageInteractionPoint p, float dt)
        {
            p.Age += dt;
            if (p.Lifetime > 0 && p.Age >= p.Lifetime)
            {
                p.Active = 0;
                return p;
            }
            // 强度随时间衰减
            if (p.Lifetime > 0)
            {
                float t = p.Age / p.Lifetime;
                p.Strength *= 1.0f - t * 0.5f;
            }
            return p;
        }

        /// <summary>获取交互缓冲区（GPU）。</summary>
        public RHIBuffer? GetInteractionBuffer() => _interactionBuffer;

        /// <summary>获取交互贴图（GPU）。</summary>
        public RHITexture? GetInteractionMap() => _interactionMap;

        /// <summary>计算某个位置受交互影响的弯曲量（CPU 端参考实现）。</summary>
        public static Vector3 ComputeBendOffset(
            Vector3 worldPos, Vector3 normal,
            ReadOnlySpan<FoliageInteractionPoint> points)
        {
            Vector3 bend = Vector3.Zero;
            for (int i = 0; i < points.Length; i++)
            {
                if (points[i].Active == 0) continue;
                Vector3 toPoint = worldPos - points[i].Position;
                float dist = toPoint.Length();
                if (dist > points[i].Radius) continue;

                float falloff = 1.0f - dist / points[i].Radius;
                falloff = falloff * falloff; // 平方衰减

                // 弯曲方向：远离交互点
                Vector3 bendDir = Vector3.Normalize(toPoint);
                bendDir.Y = 0; // 只在 XZ 平面弯曲
                if (bendDir.LengthSquared() < 0.001f) continue;
                bendDir = Vector3.Normalize(bendDir);

                // 加上速度方向（顺着的方向弯）
                Vector3 velDir = points[i].Velocity;
                if (velDir.LengthSquared() > 0.001f)
                {
                    velDir = Vector3.Normalize(velDir);
                    bendDir = Vector3.Normalize(bendDir * 0.7f + velDir * 0.3f);
                }

                bend += bendDir * falloff * points[i].Strength * 0.5f;
            }
            return bend;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _interactionBuffer?.Dispose();
            _interactionMap?.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 草地渲染器
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>草地渲染器（GPU 实例化 + billboard）。</summary>

    // ────────────────────────────────────────────────────────────────────────
    // 树木渲染器
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>树木渲染器（LOD 链 + impostor）。</summary>
    public sealed class TreeRenderer : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly FoliageRenderConfig _config;
        private readonly Dictionary<int, FoliageTypeDesc> _types = new();
        private readonly ImposterBaker _imposterBaker;
        private RHIBuffer? _typeParamsCB;
        private RHIBuffer? _instanceBuffers;
        private RHIBuffer? _indirectArgsBuffers;
        private RHIPipelineState? _treeMeshPSO;
        private RHIPipelineState? _treeImpostorPSO;
        private RHIPipelineState? _treeCullCS;
        private RHIRootSignature? _treeRootSig;
        private bool _disposed;

        public TreeRenderer(RHIDevice device, FoliageRenderConfig config)
        {
            _device = device;
            _config = config;
            _imposterBaker = new ImposterBaker(device, config);
        }

        /// <summary>初始化。</summary>
        public void Initialize()
        {
            // 创建类型参数常量缓冲区
            _typeParamsCB = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = (uint)(FoliageConstants.MaxFoliageTypes * Marshal.SizeOf<FoliageTypeParams>()),
                Flags = RHIBufferFlags.StructuredBuffer,
                Stride = (uint)Marshal.SizeOf<FoliageTypeParams>(),
                DebugName = "Tree_TypeParams"
            });

            // 实例缓冲区（所有类型共享）
            _instanceBuffers = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = (uint)(FoliageConstants.MaxFoliageTypes * FoliageConstants.MaxInstancesPerType * Marshal.SizeOf<FoliageInstance>()),
                Flags = RHIBufferFlags.StructuredBuffer | RHIBufferFlags.UnorderedAccess,
                Stride = (uint)Marshal.SizeOf<FoliageInstance>(),
                DebugName = "Tree_InstanceBuffer"
            });

            // Indirect args（每个类型每个 LOD 一个）
            _indirectArgsBuffers = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = (uint)(FoliageConstants.MaxFoliageTypes * FoliageConstants.LODCount * Marshal.SizeOf<IndirectDrawArgs>()),
                Flags = RHIBufferFlags.UnorderedAccess | RHIBufferFlags.IndirectArgs,
                DebugName = "Tree_IndirectArgs"
            });

            CreatePSOs();
        }

        private void CreatePSOs()
        {
            _treeRootSig = _device.CreateRootSignature(new RHIRootSignatureDesc
            {
                Parameters = new[]
                {
                    new RHIRootParameter
                    {
                        ParameterType = RHIRootParameterType.StructuredBufferSRV,
                        ShaderVisibility = RHIShaderVisibility.All,
                        Descriptor = new RHIRootDescriptor { ShaderRegister = 0, RegisterSpace = 0 }
                    },
                    new RHIRootParameter
                    {
                        ParameterType = RHIRootParameterType.CBV,
                        ShaderVisibility = RHIShaderVisibility.All,
                        Descriptor = new RHIRootDescriptor { ShaderRegister = 0, RegisterSpace = 0 }
                    }
                },
                AllowInputAssembler = true,
                Flags = RHIRootSignatureFlags.AllowInputAssemblerInputLayout
            }, "TreeRootSig");

            _treeMeshPSO = _device.CreateGraphicsPipelineState(new RHIGraphicsPipelineDesc
            {
                Name = "TreeMeshPSO",
                RootSignature = _treeRootSig,
                BlendState = RHIBlendDesc.Default,
                DepthStencilState = new RHIDepthDesc
                {
                    DepthEnable = true,
                    DepthWriteEnable = true,
                    DepthFunc = RHIComparison.Less
                },
                RasterizerState = new RHIRasterizerDesc
                {
                    FillMode = RHIFillMode.Solid,
                    CullMode = RHICullMode.None, // 双面
                    DepthClipEnable = true
                },
                RTVFormats = new[] { RHIFormat.R16G16B16A16_Float },
                DSVFormat = RHIFormat.D32_Float
            }, "TreeMeshPSO");

            _treeImpostorPSO = _device.CreateGraphicsPipelineState(new RHIGraphicsPipelineDesc
            {
                Name = "TreeImpostorPSO",
                RootSignature = _treeRootSig,
                BlendState = RHIBlendDesc.Default,
                DepthStencilState = new RHIDepthDesc
                {
                    DepthEnable = true,
                    DepthWriteEnable = true,
                    DepthFunc = RHIComparison.Less
                },
                RasterizerState = new RHIRasterizerDesc
                {
                    FillMode = RHIFillMode.Solid,
                    CullMode = RHICullMode.None,
                    DepthClipEnable = true
                },
                RTVFormats = new[] { RHIFormat.R16G16B16A16_Float },
                DSVFormat = RHIFormat.D32_Float
            }, "TreeImpostorPSO");

            _treeCullCS = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "TreeCullCS",
                RootSignature = _treeRootSig
            }, "TreeCullCS");
        }

        /// <summary>注册植被类型。</summary>
        public void RegisterType(FoliageTypeDesc typeDesc)
        {
            if (typeDesc.Id < 0 || typeDesc.Id >= FoliageConstants.MaxFoliageTypes)
                throw new ArgumentOutOfRangeException(nameof(typeDesc.Id));

            typeDesc.InitDefaultSeasonColors();
            _types[typeDesc.Id] = typeDesc;
        }

        /// <summary>烘焙 impostor。</summary>
        public void BakeImposter(int typeId, ImposterBakeRequest request)
        {
            if (!_types.TryGetValue(typeId, out var typeDesc)) return;
            request.SourceMesh = typeDesc.LOD0Mesh;
            _imposterBaker.BeginBake(request);
            // 分帧烘焙
            _imposterBaker.BakeProgressive(_config.MaxImposterBakePerFrame);
            if (_imposterBaker.Result.IsComplete)
            {
                typeDesc.Imposter = _imposterBaker.Result;
            }
        }

        /// <summary>更新 impostor 烘焙进度。</summary>
        public void UpdateImposterBaking()
        {
            _imposterBaker.BakeProgressive(_config.MaxImposterBakePerFrame);
        }

        /// <summary>渲染所有树木。</summary>
        public void Render(
            RHICommandList cmdList,
            Matrix4x4 viewProj,
            Matrix4x4 invViewProj,
            Vector3 cameraPos,
            RHITexture depthBuffer,
            RHITexture? hzbTexture,
            WindFieldParams wind,
            SeasonSystem season,
            FoliageInteractionSystem? interaction)
        {
            // 1. Compute Shader 剔除（所有类型）
            cmdList.SetPipelineState(_treeCullCS!);
            cmdList.SetShaderResourceView(0, _instanceBuffers!);
            cmdList.SetUnorderedAccessView(0, _indirectArgsBuffers!);
            int totalTypes = _types.Count;
            if (totalTypes == 0) return;
            int gx = (FoliageConstants.MaxInstancesPerType * totalTypes + 63) / 64;
            cmdList.Dispatch(gx, 1, 1);

            // 2. 渲染每个类型
            foreach (var kv in _types)
            {
                int typeId = kv.Key;
                var typeDesc = kv.Value;
                if (typeDesc.InstanceCount == 0) continue;

                // LOD0 + LOD1: mesh
                for (int lod = 0; lod < 2; lod++)
                {
                    var mesh = lod == 0 ? typeDesc.LOD0Mesh : typeDesc.LOD1Mesh;
                    if (mesh == null) continue;

                    cmdList.SetPipelineState(_treeMeshPSO!);
                    cmdList.SetGraphicsRootSignature(_treeRootSig!);
                    cmdList.SetShaderResourceView(0, _instanceBuffers!);
                    // Indirect draw (offset = typeId * LODCount * sizeof(IndirectDrawArgs) + lod * sizeof(IndirectDrawArgs))
                    uint argsOffset = (uint)(typeId * FoliageConstants.LODCount * Marshal.SizeOf<IndirectDrawArgs>() +
                                              lod * Marshal.SizeOf<IndirectDrawArgs>());
                    cmdList.ExecuteIndirect(_indirectArgsBuffers, argsOffset);
                }

                // LOD2: billboard mesh
                if (typeDesc.LOD2Mesh != null)
                {
                    cmdList.SetPipelineState(_treeMeshPSO!);
                    cmdList.SetGraphicsRootSignature(_treeRootSig!);
                    uint argsOffset = (uint)(typeId * FoliageConstants.LODCount * Marshal.SizeOf<IndirectDrawArgs>() +
                                              2 * Marshal.SizeOf<IndirectDrawArgs>());
                    cmdList.ExecuteIndirect(_indirectArgsBuffers, argsOffset);
                }

                // LOD3: impostor
                if (typeDesc.Imposter != null && typeDesc.Imposter.IsComplete)
                {
                    cmdList.SetPipelineState(_treeImpostorPSO!);
                    cmdList.SetGraphicsRootSignature(_treeRootSig!);
                    cmdList.SetShaderResourceView(0, _instanceBuffers!);
                    cmdList.SetShaderResourceView(1, typeDesc.Imposter.ColorAtlas!);
                    cmdList.SetShaderResourceView(2, typeDesc.Imposter.NormalAtlas!);
                    uint argsOffset = (uint)(typeId * FoliageConstants.LODCount * Marshal.SizeOf<IndirectDrawArgs>() +
                                              3 * Marshal.SizeOf<IndirectDrawArgs>());
                    cmdList.ExecuteIndirect(_indirectArgsBuffers, argsOffset);
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _typeParamsCB?.Dispose();
            _instanceBuffers?.Dispose();
            _indirectArgsBuffers?.Dispose();
            _treeMeshPSO?.Dispose();
            _treeImpostorPSO?.Dispose();
            _treeCullCS?.Dispose();
            _treeRootSig?.Dispose();
            _imposterBaker.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 落叶系统
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>落叶粒子。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct LeafParticle
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public Vector3 Rotation;
        public Vector3 AngularVelocity;
        public float Size;
        public float Lifetime;
        public float Age;
        public uint Seed;
        public int Active;
    }

    /// <summary>落叶系统（季节相关）。</summary>
    public sealed class LeafFallSystem : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly SeasonSystem _season;
        private readonly List<LeafParticle> _leaves = new();
        private RHIBuffer? _leafBuffer;
        private RHITexture? _leafAtlas;
        private bool _disposed;

        /// <summary>最大粒子数。</summary>
        public int MaxLeaves { get; } = 4096;

        /// <summary>当前活动粒子数。</summary>
        public int ActiveCount { get; private set; }

        public LeafFallSystem(RHIDevice device, SeasonSystem season)
        {
            _device = device;
            _season = season;
        }

        /// <summary>初始化。</summary>
        public void Initialize()
        {
            _leafBuffer = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = (uint)(MaxLeaves * Marshal.SizeOf<LeafParticle>()),
                Flags = RHIBufferFlags.StructuredBuffer | RHIBufferFlags.UnorderedAccess,
                Stride = (uint)Marshal.SizeOf<LeafParticle>(),
                DebugName = "LeafFall_Buffer"
            });

            _leafAtlas = _device.CreateTexture(new RHITextureDesc
            {
                Width = 512, Height = 512,
                Format = RHIFormat.R8G8B8A8_UNorm,
                Usage = RHITextureUsage.ShaderResource,
                DebugName = "LeafFall_Atlas"
            });
        }

        /// <summary>生成落叶。</summary>
        public void SpawnLeaves(Vector3 center, float radius, int count, Season season)
        {
            float fallIntensity = _season.GetLeafFallIntensity();
            if (fallIntensity <= 0) return;

            int actualCount = (int)(count * fallIntensity);
            var rng = new Random();

            for (int i = 0; i < actualCount; i++)
            {
                if (_leaves.Count >= MaxLeaves) break;

                float angle = (float)rng.NextDouble() * MathF.PI * 2;
                float r = (float)rng.NextDouble() * radius;
                Vector3 pos = center + new Vector3(
                    MathF.Cos(angle) * r,
                    5 + (float)rng.NextDouble() * 10,
                    MathF.Sin(angle) * r);

                Vector3 vel = new Vector3(
                    (float)(rng.NextDouble() - 0.5) * 0.5f,
                    -0.5f - (float)rng.NextDouble() * 0.5f,
                    (float)(rng.NextDouble() - 0.5) * 0.5f);

                Vector3 rot = new Vector3(
                    (float)rng.NextDouble() * MathF.PI * 2,
                    (float)rng.NextDouble() * MathF.PI * 2,
                    (float)rng.NextDouble() * MathF.PI * 2);

                Vector3 angVel = new Vector3(
                    (float)(rng.NextDouble() - 0.5) * 2,
                    (float)(rng.NextDouble() - 0.5) * 2,
                    (float)(rng.NextDouble() - 0.5) * 2);

                // 季节颜色
                Vector4 color = season switch
                {
                    Season.Autumn => new Vector4(0.8f, 0.4f, 0.1f, 1),
                    Season.Winter => new Vector4(0.7f, 0.7f, 0.75f, 1),
                    Season.Spring => new Vector4(0.5f, 0.8f, 0.3f, 1),
                    _ => new Vector4(0.2f, 0.6f, 0.2f, 1)
                };

                _leaves.Add(new LeafParticle
                {
                    Position = pos,
                    Velocity = vel,
                    Rotation = rot,
                    AngularVelocity = angVel,
                    Size = 0.1f + (float)rng.NextDouble() * 0.1f,
                    Lifetime = 8 + (float)rng.NextDouble() * 4,
                    Age = 0,
                    Seed = (uint)rng.Next(),
                    Active = 1
                });
            }

            ActiveCount = _leaves.Count;
        }

        /// <summary>更新（每帧调用）。</summary>
        public void Update(float dt, Vector3 windDir, float windStrength)
        {
            for (int i = 0; i < _leaves.Count; i++)
            {
                var leaf = _leaves[i];
                if (leaf.Active == 0) continue;

                // 重力
                leaf.Velocity.Y -= 1.5f * dt;

                // 风力
                leaf.Velocity += windDir * windStrength * dt * 0.5f;

                // 空气阻力
                leaf.Velocity *= MathF.Max(0, 1 - dt * 0.5f);

                // 摆动（叶子飘落特性）
                float sway = MathF.Sin(leaf.Age * 3 + leaf.Seed) * 0.3f;
                leaf.Velocity.X += sway * dt;

                // 位置更新
                leaf.Position += leaf.Velocity * dt;
                leaf.Rotation += leaf.AngularVelocity * dt;
                leaf.Age += dt;

                // 落地或超时
                if (leaf.Position.Y < 0 || leaf.Age >= leaf.Lifetime)
                {
                    leaf.Active = 0;
                }

                _leaves[i] = leaf;
            }

            // 清理死亡粒子
            _leaves.RemoveAll(l => l.Active == 0);
            ActiveCount = _leaves.Count;

            // 上传到 GPU
            if (_leafBuffer != null && _leaves.Count > 0)
            {
                _device.UpdateBuffer(_leafBuffer, _leaves.ToArray());
            }
        }

        /// <summary>渲染落叶。</summary>
        public void Render(RHICommandList cmdList, Matrix4x4 viewProj)
        {
            if (ActiveCount == 0) return;
            // NOTE: 实际实现用粒子着色器
        }

        public void Dispose()
        {
            if (_disposed) return;
            _leafBuffer?.Dispose();
            _leafAtlas?.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 植被 SSS (Subsurface Scattering)
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>植被次表面散射。</summary>
    public static class FoliageSSS
    {
        /// <summary>计算树叶透光（简化版 SSS）。</summary>
        /// <param name="albedo">叶子反照率。</param>
        /// <param name="sssColor">SSS 颜色。</param>
        /// <param name="lightDir">光照方向。</param>
        /// <param name="normal">叶子法线。</param>
        /// <param name="viewDir">视角方向。</param>
        /// <param name="sssStrength">SSS 强度。</param>
        /// <returns>透光颜色。</returns>
        public static Vector3 ComputeTransmittance(
            Vector3 albedo, Vector3 sssColor,
            Vector3 lightDir, Vector3 normal, Vector3 viewDir,
            float sssStrength)
        {
            // 透光强度：当光源在叶子背面时最强
            float backLight = MathF.Max(0, -Vector3.Dot(normal, lightDir));
            // 视角和光源对齐时透光最强（逆光）
            float viewLight = MathF.Max(0, Vector3.Dot(-lightDir, viewDir));
            // wrap lighting
            float wrap = MathF.Max(0, (Vector3.Dot(normal, lightDir) + 1) * 0.5f);

            // 透光 = sssColor * (backLight * viewLight + wrap * 0.3)
            float transmittance = (backLight * viewLight + wrap * 0.3f) * sssStrength;

            return sssColor * transmittance + albedo * wrap * 0.5f;
        }

        /// <summary>叶子的 wrap lighting（让光照在叶子边缘扩散）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float WrapLighting(float NdotL, float wrapFactor)
        {
            return MathF.Max(0, (NdotL + wrapFactor) / (1 + wrapFactor));
        }

        /// <summary>计算双面光照。</summary>
        public static Vector3 ComputeTwoSidedLighting(
            Vector3 normal, Vector3 lightDir, Vector3 viewDir,
            Vector3 lightColor, Vector3 albedo,
            Vector3 sssColor, float sssStrength)
        {
            // 正面
            float NdotLFront = MathF.Max(0, Vector3.Dot(normal, lightDir));
            // 背面
            float NdotLBack = MathF.Max(0, -Vector3.Dot(normal, lightDir));

            // wrap lighting
            float wrapFront = WrapLighting(NdotLFront, 0.5f);
            float wrapBack = WrapLighting(NdotLBack, 0.5f);

            // 正面漫反射
            Vector3 frontDiffuse = albedo * lightColor * wrapFront;
            // 背面透光
            Vector3 backTransmit = sssColor * lightColor * wrapBack * sssStrength;

            return frontDiffuse + backTransmit;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // Alpha 测试优化
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>植被 Alpha 测试工具。</summary>
    public static class FoliageAlphaTest
    {
        /// <summary>Alpha hash 函数（stochastic alpha test）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float AlphaHash(Vector2 uv, float scale)
        {
            // 基于 hash 的 alpha test，比阈值法抗锯齿更好
            float h = Hash2D(uv * scale);
            return h;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float Hash2D(Vector2 p)
        {
            // 简化 hash
            float x = MathF.Sin(p.X * 127.1f + p.Y * 311.7f) * 43758.5453f;
            return x - MathF.Floor(x);
        }

        /// <summary>Alpha to coverage（MSAA 兼容）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float AlphaToCoverage(float alpha, float alphaCutoff, int sampleCount)
        {
            // 把 alpha 映射到 coverage mask
            // 简化版：直接返回调整后的 alpha
            float adjustedCutoff = alphaCutoff - (1.0f / sampleCount) * 0.5f;
            return alpha > adjustedCutoff ? alpha : 0;
        }

        /// <summary>Mip 级 alpha 锐化（防止远处出现透明洞）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float SharpenAlphaAtMip(float alpha, int mipLevel, float sharpness)
        {
            // 越高 mip 越锐化
            float sharpenAmount = sharpness * mipLevel;
            // 把 alpha 推向 0 或 1
            return alpha > 0.5f
                ? MathF.Min(1, alpha + sharpenAmount * 0.5f)
                : MathF.Max(0, alpha - sharpenAmount * 0.5f);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 植被阴影
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>植被阴影渲染器。</summary>
    public sealed class FoliageShadowRenderer : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly FoliageRenderConfig _config;
        private RHIPipelineState? _foliageShadowPSO;
        private RHIRootSignature? _foliageShadowRootSig;
        private bool _disposed;

        public FoliageShadowRenderer(RHIDevice device, FoliageRenderConfig config)
        {
            _device = device;
            _config = config;
        }

        /// <summary>初始化。</summary>
        public void Initialize()
        {
            _foliageShadowRootSig = _device.CreateRootSignature(new RHIRootSignatureDesc
            {
                Parameters = new[]
                {
                    new RHIRootParameter
                    {
                        ParameterType = RHIRootParameterType.CBV,
                        ShaderVisibility = RHIShaderVisibility.All,
                        Descriptor = new RHIRootDescriptor { ShaderRegister = 0, RegisterSpace = 0 }
                    }
                },
                AllowInputAssembler = true,
                Flags = RHIRootSignatureFlags.AllowInputAssemblerInputLayout
            }, "FoliageShadowRootSig");

            _foliageShadowPSO = _device.CreateGraphicsPipelineState(new RHIGraphicsPipelineDesc
            {
                Name = "FoliageShadowPSO",
                RootSignature = _foliageShadowRootSig,
                BlendState = RHIBlendDesc.Default,
                DepthStencilState = new RHIDepthDesc
                {
                    DepthEnable = true,
                    DepthWriteEnable = true,
                    DepthFunc = RHIComparison.Less
                },
                RasterizerState = new RHIRasterizerDesc
                {
                    FillMode = RHIFillMode.Solid,
                    CullMode = RHICullMode.None,
                    DepthBias = 50,
                    SlopeScaledDepthBias = 1.5f,
                    DepthClipEnable = true
                },
                RTVFormats = Array.Empty<RHIFormat>(),
                DSVFormat = RHIFormat.D32_Float
            }, "FoliageShadowPSO");
        }

        /// <summary>渲染植被到阴影图。</summary>
        public void RenderToShadowMap(
            RHICommandList cmdList,
            Matrix4x4 lightViewProj,
            TreeRenderer treeRenderer,
            GrassRenderer grassRenderer)
        {
            cmdList.SetPipelineState(_foliageShadowPSO!);
            cmdList.SetGraphicsRootSignature(_foliageShadowRootSig!);

            // 树木阴影
            // treeRenderer.RenderShadow(cmdList, lightViewProj);

            // 草地阴影（可选，性能开销大）
            if (_config.EnableGrassShadow)
            {
                // grassRenderer.RenderShadow(cmdList, lightViewProj);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _foliageShadowPSO?.Dispose();
            _foliageShadowRootSig?.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 植被分布生成器
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>植被分布参数。</summary>
    public sealed class FoliageDistributionParams
    {
        /// <summary>最小高度（米）。</summary>
        public float MinHeight = 0;
        /// <summary>最大高度（米）。</summary>
        public float MaxHeight = 1000;
        /// <summary>最小坡度（度）。</summary>
        public float MinSlope = 0;
        /// <summary>最大坡度（度）。</summary>
        public float MaxSlope = 60;
        /// <summary>基础密度（实例/平方米）。</summary>
        public float BaseDensity = 0.01f;
        /// <summary>密度图缩放。</summary>
        public float DensityMapScale = 1.0f;
        /// <summary>随机抖动半径（米）。</summary>
        public float JitterRadius = 0.5f;
        /// <summary>缩放下限。</summary>
        public float MinScale = 0.8f;
        /// <summary>缩放上限。</summary>
        public float MaxScale = 1.2f;
        /// <summary>集群强度（0=均匀，1=强集群）。</summary>
        public float ClusterStrength = 0.5f;
        /// <summary>集群半径（米）。</summary>
        public float ClusterRadius = 10.0f;
    }

    /// <summary>植被分布生成器（基于密度图 + 高度/坡度规则）。</summary>
    public static class FoliageDistributor
    {
        /// <summary>生成植被实例。</summary>
        public static FoliageInstance[] Generate(
            Vector2 areaMin, Vector2 areaMax,
            FoliageDistributionParams Params,
            int seed,
            Func<float, float, float> heightSampler,
            Func<float, float, Vector3> normalSampler,
            Func<float, float, float> densitySampler)
        {
            float area = (areaMax.X - areaMin.X) * (areaMax.Y - areaMin.Y);
            int targetCount = (int)(area * Params.BaseDensity);
            targetCount = Math.Min(targetCount, FoliageConstants.MaxInstancesPerType);

            var instances = new List<FoliageInstance>(targetCount);
            var rng = new Random(seed);

            // Poisson disk 采样近似
            float cellSize = MathF.Sqrt(1.0f / Params.BaseDensity) * 0.7f;
            int gridW = (int)((areaMax.X - areaMin.X) / cellSize) + 1;
            int gridH = (int)((areaMax.Y - areaMin.Y) / cellSize) + 1;
            var grid = new bool[gridW * gridH];

            int attempts = targetCount * 10;
            for (int a = 0; a < attempts && instances.Count < targetCount; a++)
            {
                float x = areaMin.X + (float)rng.NextDouble() * (areaMax.X - areaMin.X);
                float z = areaMin.Y + (float)rng.NextDouble() * (areaMax.Y - areaMin.Y);

                // 集群调整
                if (Params.ClusterStrength > 0)
                {
                    float cluster = (float)rng.NextDouble();
                    if (cluster > Params.ClusterStrength)
                    {
                        // 在已有实例附近生成
                        if (instances.Count > 0)
                        {
                            int refIdx = rng.Next(instances.Count);
                            Vector3 refPos = instances[refIdx].WorldPosition;
                            float angle = (float)rng.NextDouble() * MathF.PI * 2;
                            float r = (float)rng.NextDouble() * Params.ClusterRadius;
                            x = refPos.X + MathF.Cos(angle) * r;
                            z = refPos.Z + MathF.Sin(angle) * r;
                        }
                    }
                }

                // 抖动
                x += (float)(rng.NextDouble() - 0.5) * Params.JitterRadius;
                z += (float)(rng.NextDouble() - 0.5) * Params.JitterRadius;

                // 高度检查
                float height = heightSampler(x, z);
                if (height < Params.MinHeight || height > Params.MaxHeight) continue;

                // 坡度检查
                Vector3 normal = normalSampler(x, z);
                float slope = MathF.Acos(MathF.Abs(normal.Y)) * MathUtils.Rad2Deg;
                if (slope < Params.MinSlope || slope > Params.MaxSlope) continue;

                // 密度图检查
                float density = densitySampler(x, z) * Params.DensityMapScale;
                if ((float)rng.NextDouble() > density) continue;

                // Poisson disk 检查
                int gx = (int)((x - areaMin.X) / cellSize);
                int gy = (int)((z - areaMin.Y) / cellSize);
                if (gx < 0 || gx >= gridW || gy < 0 || gy >= gridH) continue;
                if (grid[gy * gridW + gx]) continue;
                grid[gy * gridW + gx] = true;

                // 生成实例
                float scale = Params.MinScale + (float)rng.NextDouble() * (Params.MaxScale - Params.MinScale);
                float rotation = (float)rng.NextDouble() * MathF.PI * 2;
                uint instSeed = (uint)rng.Next();

                // 颜色变化
                float colorVar = 0.85f + (float)rng.NextDouble() * 0.3f;

                instances.Add(new FoliageInstance
                {
                    PositionSeed = new Vector4(x, height, z, instSeed),
                    ScaleRotation = new Vector4(scale, scale, scale, rotation),
                    ColorHeight = new Vector4(colorVar, colorVar, colorVar, 0),
                    WindPhaseLOD = new Vector4((float)rng.NextDouble() * MathF.PI * 2, 0, 0, 1)
                });
            }

            return instances.ToArray();
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 植被 GI（全局光照）
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>植被全局光照。</summary>
    public sealed class FoliageGI : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly FoliageRenderConfig _config;
        private RHITexture? _irradianceAtlas;
        private RHITexture? _visibilityAtlas;
        private bool _disposed;

        /// <summary>GI 探针分辨率。</summary>
        public int ProbeResolution { get; } = 64;

        public FoliageGI(RHIDevice device, FoliageRenderConfig config)
        {
            _device = device;
            _config = config;
        }

        /// <summary>初始化。</summary>
        public void Initialize()
        {
            // 双面 GI 需要两张 irradiance atlas（正面 + 背面）
            _irradianceAtlas = _device.CreateTexture(new RHITextureDesc
            {
                Width = 1024, Height = 1024,
                Format = RHIFormat.R16G16B16A16_Float,
                Usage = RHITextureUsage.ShaderResource | RHITextureUsage.UnorderedAccess,
                DebugName = "Foliage_IrradianceAtlas"
            });

            _visibilityAtlas = _device.CreateTexture(new RHITextureDesc
            {
                Width = 1024, Height = 1024,
                Format = RHIFormat.R8_UNorm,
                Usage = RHITextureUsage.ShaderResource | RHITextureUsage.UnorderedAccess,
                DebugName = "Foliage_VisibilityAtlas"
            });
        }

        /// <summary>计算双面 GI 贡献。</summary>
        public Vector3 ComputeDoubleSidedGI(
            Vector3 normal, Vector3 frontIrradiance, Vector3 backIrradiance,
            float sssStrength)
        {
            // 正面 GI
            Vector3 frontGI = frontIrradiance * MathF.Max(0, normal.Y) * 0.5f;
            // 背面 GI（透光）
            Vector3 backGI = backIrradiance * sssStrength * 0.3f;
            return frontGI + backGI;
        }

        /// <summary>更新 GI 探针（每帧或低频更新）。</summary>
        public void UpdateProbes(RHICommandList cmdList, RHITexture depthBuffer, RHITexture normalBuffer)
        {
            if (_config.GIMode == 0) return;
            // NOTE: 实际实现需要 compute shader 采样场景并更新探针
        }

        public void Dispose()
        {
            if (_disposed) return;
            _irradianceAtlas?.Dispose();
            _visibilityAtlas?.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 主渲染管线
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>植被渲染管线主入口。</summary>
    public sealed class FoliageRenderPipeline : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly FoliageRenderConfig _config;
        private readonly GrassRenderer _grassRenderer;
        private readonly TreeRenderer _treeRenderer;
        private readonly FoliageCuller _culler;
        internal readonly SeasonSystem _seasonSystem;
        private readonly FoliageInteractionSystem _interactionSystem;
        internal readonly WindFieldParams _windParams;
        internal readonly LeafFallSystem _leafFallSystem;
        private readonly FoliageShadowRenderer _shadowRenderer;
        private readonly FoliageGI _giSystem;
        private bool _disposed;

        /// <summary>帧时间。</summary>
        public float FrameTime { get; private set; }

        /// <summary>可见实例数。</summary>
        public int VisibleInstanceCount { get; private set; }

        public FoliageRenderPipeline(RHIDevice device, FoliageRenderConfig? config = null)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _config = config ?? FoliageRenderConfig.Default;

            _grassRenderer = new GrassRenderer(_device, _config);
            _treeRenderer = new TreeRenderer(_device, _config);
            _culler = new FoliageCuller(_config);
            _seasonSystem = new SeasonSystem();
            _interactionSystem = new FoliageInteractionSystem(_device, _config);
            _windParams = new WindFieldParams();
            _leafFallSystem = new LeafFallSystem(_device, _seasonSystem);
            _shadowRenderer = new FoliageShadowRenderer(_device, _config);
            _giSystem = new FoliageGI(_device, _config);
        }

        /// <summary>初始化所有子系统。</summary>
        public void Initialize()
        {
            _grassRenderer.Initialize();
            _treeRenderer.Initialize();
            _interactionSystem.Initialize();
            _leafFallSystem.Initialize();
            _shadowRenderer.Initialize();
            _giSystem.Initialize();
        }

        /// <summary>更新（每帧调用）。</summary>
        public void Update(float dt, Vector3 cameraPos, Vector3 cameraDir)
        {
            FrameTime = dt;

            // 更新风力
            _windParams.Time += dt;
            _windParams.Direction = _config.WindDirection;
            _windParams.Strength = _config.WindStrength;

            // 更新季节
            _seasonSystem.Update(dt);

            // 更新交互
            if (_config.EnableInteraction)
            {
                _interactionSystem.Update(dt);
            }

            // 更新落叶
            _leafFallSystem.Update(dt, _windParams.Direction, _windParams.Strength);

            // 更新 impostor 烘焙进度
            _treeRenderer.UpdateImposterBaking();
        }

        /// <summary>渲染。</summary>
        public void Render(
            RHICommandList cmdList,
            Matrix4x4 viewProj,
            Matrix4x4 invViewProj,
            Vector3 cameraPos,
            RHITexture depthBuffer,
            RHITexture? hzbTexture,
            RHITexture colorTarget)
        {
            // 1. 渲染植被阴影（在阴影 pass 中调用）
            // _shadowRenderer.RenderToShadowMap(cmdList, lightViewProj, _treeRenderer, _grassRenderer);

            // 2. GeometryBuffer pass：渲染植被到 GeometryBuffer
            // 树木
            _treeRenderer.Render(
                cmdList, viewProj, invViewProj, cameraPos,
                depthBuffer, hzbTexture,
                _windParams, _seasonSystem,
                _config.EnableInteraction ? _interactionSystem : null);

            // 草地
            _grassRenderer.Render(
                cmdList, viewProj, cameraPos,
                depthBuffer, _windParams,
                _config.EnableInteraction ? _interactionSystem : null);

            // 3. 落叶（透明 pass）
            _leafFallSystem.Render(cmdList, viewProj);
        }

        /// <summary>设置季节。</summary>
        public void SetSeason(Season season, bool instant = false)
        {
            _seasonSystem.SetSeason(season, instant);
        }

        /// <summary>添加交互点（角色踩踏）。</summary>
        public void AddInteractionPoint(Vector3 position, float radius, float strength, Vector3 velocity)
        {
            if (!_config.EnableInteraction) return;
            _interactionSystem.AddPoint(position, radius, strength, velocity);
        }

        /// <summary>烘焙某个类型的 impostor。</summary>
        public void BakeImposter(int typeId, ImposterBakeRequest? request = null)
        {
            request ??= new ImposterBakeRequest();
            _treeRenderer.BakeImposter(typeId, request);
        }

        /// <summary>生成草地。</summary>
        public void GenerateGrass(
            Vector2 areaMin, Vector2 areaMax,
            float density, int seed,
            Func<float, float, float> heightSampler,
            Func<float, float, Vector3> normalSampler)
        {
            _grassRenderer.GenerateInstances(areaMin, areaMax, density, seed, heightSampler, normalSampler);
        }

        /// <summary>生成植被分布。</summary>
        public void GenerateFoliage(
            int typeId,
            Vector2 areaMin, Vector2 areaMax,
            FoliageDistributionParams distParams,
            int seed,
            Func<float, float, float> heightSampler,
            Func<float, float, Vector3> normalSampler,
            Func<float, float, float> densitySampler)
        {
            var instances = FoliageDistributor.Generate(
                areaMin, areaMax, distParams, seed,
                heightSampler, normalSampler, densitySampler);

            // NOTE: 这里应该把 instances 上传到 GPU
            // 简化：只记录数量
        }

        public void Dispose()
        {
            if (_disposed) return;
            _grassRenderer.Dispose();
            _treeRenderer.Dispose();
            _interactionSystem.Dispose();
            _leafFallSystem.Dispose();
            _shadowRenderer.Dispose();
            _giSystem.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 着色器源码（HLSL）
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>植被着色器源码。</summary>
    public static class FoliageShaders
    {
        /// <summary>草地顶点着色器（HLSL）。</summary>
        public const string GrassVS = @"
struct FoliageInstance
{
    float4 PositionSeed : POSITION;
    float4 ScaleRotation : SCALE;
    float4 ColorHeight : COLOR;
    float4 WindPhaseLOD : WIND;
};

cbuffer FoliageCB : register(b0)
{
    float4x4 g_ViewProj;
    float3 g_CameraPos;
    float g_Time;
    float3 g_WindDir;
    float g_WindStrength;
    float g_WindFrequency;
    float g_WindGust;
    float g_SSSStrength;
    float g_WPOStrength;
    int g_WindQuality;
    float3 g_Pad;
};

StructuredBuffer<FoliageInstance> g_Instances : register(t0);

struct VSOutput
{
    float4 Position : SV_POSITION;
    float3 WorldPos : WORLD_POS;
    float3 Normal : NORMAL;
    float2 UV : TEXCOORD;
    float4 Color : COLOR;
    float3 WindOffset : WIND_OFFSET;
};

VSOutput main(uint instanceId : SV_InstanceID, float3 localPos : POSITION,
              float3 normal : NORMAL, float2 uv : TEXCOORD)
{
    VSOutput output;
    FoliageInstance inst = g_Instances[instanceId];

    // 应用缩放和旋转
    float s = inst.ScaleRotation.x;
    float angle = inst.ScaleRotation.w;
    float cosA = cos(angle);
    float sinA = sin(angle);
    float3 scaledPos = localPos * s;
    float3 rotatedPos = float3(
        scaledPos.x * cosA + scaledPos.z * sinA,
        scaledPos.y,
        -scaledPos.x * sinA + scaledPos.z * cosA);

    // 世界位置
    float3 worldPos = inst.PositionSeed.xyz + rotatedPos;

    // 风力 WPO (World Position Offset)
    float3 windOffset = float3(0, 0, 0);
    float heightFactor = saturate(localPos.y); // 根部不动，顶部摆
    if (g_WindQuality > 0)
    {
        float windPhase = inst.WindPhaseLOD.x + g_Time * g_WindFrequency;
        float sway = sin(windPhase) * g_WindStrength * heightFactor;
        windOffset = g_WindDir * sway * g_WPOStrength;

        if (g_WindQuality >= 3)
        {
            // 阵风
            float gust = sin(dot(worldPos.xz, g_WindDir.xz) * 0.1 - g_Time * 1.5);
            gust = max(0, gust) * g_WindGust;
            windOffset += g_WindDir * gust * heightFactor * g_WPOStrength;
        }
    }

    worldPos += windOffset;

    output.Position = mul(g_ViewProj, float4(worldPos, 1));
    output.WorldPos = worldPos;
    output.Normal = normal;
    output.UV = uv;
    output.Color = inst.ColorHeight;
    output.WindOffset = windOffset;

    return output;
}
";

        /// <summary>草地像素着色器（HLSL）。</summary>
        public const string GrassPS = @"
struct PSInput
{
    float4 Position : SV_POSITION;
    float3 WorldPos : WORLD_POS;
    float3 Normal : NORMAL;
    float2 UV : TEXCOORD;
    float4 Color : COLOR;
    float3 WindOffset : WIND_OFFSET;
};

cbuffer FoliageCB : register(b0)
{
    float4x4 g_ViewProj;
    float3 g_CameraPos;
    float g_Time;
    float3 g_WindDir;
    float g_WindStrength;
    float g_WindFrequency;
    float g_WindGust;
    float g_SSSStrength;
    float g_WPOStrength;
    int g_WindQuality;
    float3 g_Pad;
};

Texture2D g_AlbedoTex : register(t0);
SamplerState g_LinearSampler : register(s0);

float4 main(PSInput input) : SV_Target
{
    float4 albedo = g_AlbedoTex.Sample(g_LinearSampler, input.UV);
    albedo.rgb *= input.Color.rgb;

    // Alpha test
    clip(albedo.a - 0.5);

    // 简单的 wrap lighting
    float3 lightDir = normalize(float3(0.5, 0.8, 0.3));
    float NdotL = dot(input.Normal, lightDir);
    float wrap = (NdotL + 0.5) / 1.5;
    wrap = max(0, wrap);

    // SSS (透光)
    float3 sssColor = float3(0.8, 0.4, 0.2);
    float backLight = max(0, -NdotL);
    float3 sss = sssColor * backLight * g_SSSStrength;

    float3 color = albedo.rgb * (wrap * 0.8 + 0.2) + sss;

    // 雾
    float dist = length(input.WorldPos - g_CameraPos);
    float fogFactor = saturate((dist - 50) / 200);
    color = lerp(color, float3(0.7, 0.8, 0.9), fogFactor);

    return float4(color, 1.0);
}
";

        /// <summary>Impostor 顶点着色器（HLSL）。</summary>
        public const string ImpostorVS = @"
struct FoliageInstance
{
    float4 PositionSeed : POSITION;
    float4 ScaleRotation : SCALE;
    float4 ColorHeight : COLOR;
    float4 WindPhaseLOD : WIND;
};

cbuffer ImpostorCB : register(b0)
{
    float4x4 g_ViewProj;
    float3 g_CameraPos;
    float g_Time;
    float4 g_ImposterParams; // x=atlas_size, y=slices_per_row, z=slice_size, w=frames
    float3 g_Pad;
};

StructuredBuffer<FoliageInstance> g_Instances : register(t0);

struct VSOutput
{
    float4 Position : SV_POSITION;
    float2 AtlasUV : TEXCOORD0;
    float3 ViewDir : TEXCOORD1;
    float4 Color : COLOR;
    float InstanceScale : SCALE;
};

VSOutput main(uint instanceId : SV_InstanceID, float3 localPos : POSITION, float2 uv : TEXCOORD)
{
    VSOutput output;
    FoliageInstance inst = g_Instances[instanceId];

    // Billboard: 让 quad 朝向相机
    float3 worldPos = inst.PositionSeed.xyz;
    float3 toCamera = g_CameraPos - worldPos;
    float3 right = normalize(cross(float3(0, 1, 0), toCamera));
    float3 up = float3(0, 1, 0);

    float s = inst.ScaleRotation.x;
    float3 billboardPos = worldPos + (right * localPos.x + up * localPos.y) * s;

    output.Position = mul(g_ViewProj, float4(billboardPos, 1));

    // 计算 view dir 用于选择 impostor 切片
    output.ViewDir = normalize(toCamera);

    // 计算八面体 UV
    float3 dir = output.ViewDir;
    float l1norm = abs(dir.x) + abs(dir.y) + abs(dir.z);
    float2 octUV;
    if (dir.z < 0)
    {
        octUV.x = (1 - abs(dir.y)) * (dir.x >= 0 ? 1 : -1);
        octUV.y = (1 - abs(dir.x)) * (dir.y >= 0 ? 1 : -1);
    }
    else
    {
        octUV = dir.xy;
    }
    octUV = octUV * 0.5 + 0.5;

    // 映射到 atlas
    float slicesPerRow = g_ImposterParams.y;
    float sliceSize = g_ImposterParams.z / g_ImposterParams.x;
    float2 sliceUV = floor(octUV * slicesPerRow) / slicesPerRow;
    output.AtlasUV = sliceUV + uv * sliceSize;

    output.Color = inst.ColorHeight;
    output.InstanceScale = s;

    return output;
}
";

        /// <summary>Impostor 像素着色器（HLSL）。</summary>
        public const string ImpostorPS = @"
struct PSInput
{
    float4 Position : SV_POSITION;
    float2 AtlasUV : TEXCOORD0;
    float3 ViewDir : TEXCOORD1;
    float4 Color : COLOR;
    float InstanceScale : SCALE;
};

Texture2D g_ColorAtlas : register(t0);
Texture2D g_NormalAtlas : register(t1);
Texture2D g_DepthAtlas : register(t2);
SamplerState g_PointSampler : register(s0);

float4 main(PSInput input) : SV_Target
{
    float4 color = g_ColorAtlas.Sample(g_PointSampler, input.AtlasUV);
    color.rgb *= input.Color.rgb;

    // Alpha test
    clip(color.a - 0.5);

    return color;
}
";

        /// <summary>风力 Compute Shader（更新风力场）。</summary>
        public const string WindFieldCS = @"
RWTexture2D<float4> g_WindField : register(u0);

cbuffer WindCB : register(b0)
{
    float3 g_WindDir;
    float g_Strength;
    float g_Frequency;
    float g_GustFrequency;
    float g_GustStrength;
    float g_Time;
    uint g_Resolution;
    float3 g_Pad;
};

float noise2D(float x, float y)
{
    float2 p = float2(x, y);
    float n = sin(p.x * 12.9898 + p.y * 78.233) * 43758.5453;
    return n - floor(n);
}

[numthreads(8, 8, 1)]
void main(uint3 dtid : SV_DispatchThreadID)
{
    if (dtid.x >= g_Resolution || dtid.y >= g_Resolution) return;

    float2 uv = (float2)dtid.xy / g_Resolution;
    float2 worldPos = uv * 100.0;

    // 主风力
    float2 wind = g_WindDir.xz * g_Strength;

    // 噪声扰动
    float n = noise2D(worldPos.x * 0.1 + g_Time * 0.2, worldPos.y * 0.1);
    wind += (n - 0.5) * 0.3;

    // 阵风
    float gustPhase = dot(worldPos, g_WindDir.xz) * 0.1 - g_Time * g_GustFrequency * 10;
    float gust = max(0, sin(gustPhase));
    gust *= noise2D(worldPos.x * 0.02 + g_Time * 0.1, worldPos.y * 0.02);
    wind += g_WindDir.xz * gust * g_GustStrength;

    g_WindField[dtid.xy] = float4(wind, gust, g_Time);
}
";

        /// <summary>实例剔除 Compute Shader。</summary>
        public const string FoliageCullCS = @"
struct FoliageInstance
{
    float4 PositionSeed;
    float4 ScaleRotation;
    float4 ColorHeight;
    float4 WindPhaseLOD;
};

struct IndirectDrawArgs
{
    uint IndexCountPerInstance;
    uint InstanceCount;
    uint StartIndexLocation;
    int BaseVertexLocation;
    uint StartInstanceLocation;
};

StructuredBuffer<FoliageInstance> g_AllInstances : register(t0);
RWStructuredBuffer<FoliageInstance> g_VisibleInstances : register(u0);
RWStructuredBuffer<IndirectDrawArgs> g_DrawArgs : register(u1);

cbuffer CullCB : register(b0)
{
    float4x4 g_ViewProj;
    float3 g_CameraPos;
    uint g_InstanceCount;
    float4 g_LODDistances;
    uint g_TypeOffset;
    uint g_TypeInstanceCount;
    uint2 g_Pad;
};

groupshared uint g_VisibleCount;
groupshared uint g_LodCounts[4];

[numthreads(64, 1, 1)]
void main(uint3 dtid : SV_DispatchThreadID, uint3 gid : SV_GroupID, uint3 gtid : SV_GroupThreadID, uint gi : SV_GroupIndex)
{
    if (gi == 0)
    {
        g_VisibleCount = 0;
        for (int i = 0; i < 4; i++) g_LodCounts[i] = 0;
    }
    GroupMemoryBarrierWithGroupSync();

    uint globalIdx = dtid.x;
    if (globalIdx >= g_InstanceCount)
    {
        GroupMemoryBarrierWithGroupSync();
        if (gi == 0)
        {
            // 写入 indirect draw args
            uint baseOffset = gid.x * 4; // 4 LOD per group
            for (int lod = 0; lod < 4; lod++)
            {
                IndirectDrawArgs args;
                args.IndexCountPerInstance = 0; // 由 CPU 填
                args.InstanceCount = g_LodCounts[lod];
                args.StartIndexLocation = 0;
                args.BaseVertexLocation = 0;
                args.StartInstanceLocation = g_VisibleCount; // 简化
                g_DrawArgs[baseOffset + lod] = args;
            }
        }
        return;
    }

    FoliageInstance inst = g_AllInstances[globalIdx];
    float3 worldPos = inst.PositionSeed.xyz;

    // 距离剔除
    float dist = length(worldPos - g_CameraPos);
    if (dist > g_LODDistances.w) return;

    // 视锥剔除
    float4 clipPos = mul(g_ViewProj, float4(worldPos, 1));
    if (clipPos.w <= 0) return;
    float3 ndc = clipPos.xyz / clipPos.w;
    if (abs(ndc.x) > 1.2 || abs(ndc.y) > 1.2 || ndc.z < -1.2 || ndc.z > 1.2) return;

    // 确定 LOD
    uint lod = 3;
    if (dist < g_LODDistances.x) lod = 0;
    else if (dist < g_LODDistances.y) lod = 1;
    else if (dist < g_LODDistances.z) lod = 2;

    // 写入可见实例
    uint writeIdx;
    InterlockedAdd(g_VisibleCount, 1, writeIdx);
    g_VisibleInstances[writeIdx] = inst;

    InterlockedAdd(g_LodCounts[lod], 1);
}
";
    }

    // ────────────────────────────────────────────────────────────────────────
    // 调试 / 可视化
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>植被调试可视化。</summary>
    public sealed class FoliageDebugVisualizer
    {
        private readonly FoliageRenderPipeline _pipeline;

        public FoliageDebugVisualizer(FoliageRenderPipeline pipeline)
        {
            _pipeline = pipeline;
        }

        /// <summary>获取调试信息字符串。</summary>
        public string GetDebugInfo()
        {
            return $@"=== Foliage Render Pipeline ===
Wind: dir={_pipeline._windParams.Direction}, strength={_pipeline._windParams.Strength:F2}, time={_pipeline._windParams.Time:F1}
Season: {_pipeline._seasonSystem.CurrentSeason} -> {_pipeline._seasonSystem.TargetSeason} (transition={_pipeline._seasonSystem.TransitionProgress:P0})
LeafFall: {_pipeline._leafFallSystem.ActiveCount} particles
Frame time: {_pipeline.FrameTime * 1000:F2} ms
";
        }

        /// <summary>获取 LOD 分布。</summary>
        public Dictionary<FoliageLODLevel, int> GetLODDistribution()
        {
            // NOTE: 实际从 GPU 读回 indirect draw args 的 InstanceCount
            return new Dictionary<FoliageLODLevel, int>
            {
                [FoliageLODLevel.LOD0_FullMesh] = 0,
                [FoliageLODLevel.LOD1_Simplified] = 0,
                [FoliageLODLevel.LOD2_Billboard] = 0,
                [FoliageLODLevel.LOD3_Impostor] = 0
            };
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 工具方法
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>植被工具方法。</summary>
    public static class FoliageUtils
    {
        /// <summary>计算植被的 AABB。</summary>
        public static (Vector3 min, Vector3 max) ComputeAABB(FoliageInstance instance, Vector3 meshHalfExtents)
        {
            Vector3 pos = instance.WorldPosition;
            Vector3 scale = instance.Scale;
            Vector3 halfSize = meshHalfExtents * scale;
            return (pos - halfSize, pos + halfSize);
        }

        /// <summary>计算植被的世界矩阵。</summary>
        public static Matrix4x4 ComputeWorldMatrix(FoliageInstance instance)
        {
            return Matrix4x4.CreateScale(instance.Scale) *
                   Matrix4x4.CreateRotationY(instance.RotationY) *
                   Matrix4x4.CreateTranslation(instance.WorldPosition);
        }

        /// <summary>计算 billboard 朝向相机的世界矩阵。</summary>
        public static Matrix4x4 ComputeBillboardMatrix(Vector3 position, Vector3 cameraPos, float scale)
        {
            Vector3 toCamera = cameraPos - position;
            toCamera.Y = 0; // Y 轴 billboard
            if (toCamera.LengthSquared() < 0.001f) toCamera = Vector3.UnitZ;
            toCamera = Vector3.Normalize(toCamera);

            Vector3 right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, toCamera));
            Vector3 up = Vector3.UnitY;

            return new Matrix4x4(
                right.X * scale, up.X * scale, toCamera.X * scale, 0,
                right.Y * scale, up.Y * scale, toCamera.Y * scale, 0,
                right.Z * scale, up.Z * scale, toCamera.Z * scale, 0,
                position.X, position.Y, position.Z, 1);
        }

        /// <summary>距离雾计算。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float ComputeFogFactor(float dist, float fogStart, float fogEnd)
        {
            return Math.Clamp((dist - fogStart) / (fogEnd - fogStart), 0, 1);
        }

        /// <summary>距离衰减。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float ComputeDistanceFade(float dist, float fadeStart, float fadeEnd)
        {
            return Math.Clamp(1.0f - (dist - fadeStart) / (fadeEnd - fadeStart), 0, 1);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 植被类型管理器
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>植被类型管理器。</summary>
    public sealed class FoliageTypeManager
    {
        private readonly Dictionary<int, FoliageTypeDesc> _types = new();
        private int _nextId = 0;

        /// <summary>注册新类型。</summary>
        public int RegisterType(FoliageType type, string name, RHIMesh? lod0Mesh = null)
        {
            int id = _nextId++;
            var desc = new FoliageTypeDesc
            {
                Id = id,
                Type = type,
                Name = name,
                LOD0Mesh = lod0Mesh
            };
            desc.InitDefaultSeasonColors();
            _types[id] = desc;
            return id;
        }

        /// <summary>获取类型。</summary>
        public FoliageTypeDesc? GetType(int id)
        {
            return _types.TryGetValue(id, out var desc) ? desc : null;
        }

        /// <summary>获取所有类型。</summary>
        public IReadOnlyDictionary<int, FoliageTypeDesc> GetAllTypes() => _types;

        /// <summary>移除类型。</summary>
        public bool RemoveType(int id)
        {
            return _types.Remove(id);
        }

        /// <summary>设置 LOD mesh。</summary>
        public void SetLODMesh(int typeId, int lod, RHIMesh mesh)
        {
            if (!_types.TryGetValue(typeId, out var desc)) return;
            switch (lod)
            {
                case 0: desc.LOD0Mesh = mesh; break;
                case 1: desc.LOD1Mesh = mesh; break;
                case 2: desc.LOD2Mesh = mesh; break;
            }
        }

        /// <summary>设置 LOD 距离。</summary>
        public void SetLODDistances(int typeId, float lod0To1, float lod1To2, float lod2To3, float lod3ToCull)
        {
            if (!_types.TryGetValue(typeId, out var desc)) return;
            desc.LODDistances = new Vector4(lod0To1, lod1To2, lod2To3, lod3ToCull);
        }

        /// <summary>类型数量。</summary>
        public int Count => _types.Count;
    }
}
