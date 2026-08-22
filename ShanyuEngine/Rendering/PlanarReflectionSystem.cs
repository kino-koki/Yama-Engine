using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using ShanyuEngine.ECS;
using ShanyuEngine.RHI;

namespace ShanyuEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════════
    // 山雨引擎 平面反射系统 (Shanyu Planar Reflection System)
    //
    // 设计目标: 对齐 Unreal Engine 5 的 Planar Reflection 平面反射渲染。
    // 用于镜面 / 地板 / 水面 / 抛光金属 等平面镜面反射。核心思路是:
    //   1. 沿反射平面对称翻转摄像机 (位置反射 + 上方向翻转)
    //   2. 用翻转后的摄像机把整个场景重新渲染到一张反射 RenderTarget
    //   3. 通过"斜裁剪投影 (Oblique Projection)"把近裁剪面对齐到反射平面,
    //      从而剔除平面背面的几何, 避免反射纹理"穿模"
    //   4. 下游材质采样这张反射纹理, 并按菲涅尔/距离淡出与场景混合
    //
    // 与已有反射系统的关系:
    //   - RayTracingReflections.cs : 基于硬件光追 (DXR) 的精确反射, 代价高
    //   - WaterRenderer.cs (含 SSR): 屏幕空间反射, 只能反射屏幕内可见内容
    //   - 本系统 (PlanarReflection) : 传统平面反射, 介于两者之间 —— 精确且能
    //     反射屏幕外内容, 但每张反射纹理需额外渲染一次场景。三者可叠加/回退。
    //
    // 对接约定:
    //   - RHI: 使用 ShanyuEngine.RHI 的 RHIDevice / RHICommandList / RHITexture /
    //     RHIBuffer / RHIDescriptorHandle(SRV/UAV/RTV/DSV) / RHIRootSignature /
    //     RHIPipelineState 等已有抽象。
    //   - ECS: 使用 ShanyuEngine.ECS 的 IComponent / ISystem / EntityComponentSystem,
    //     TransformComponent / CameraComponent 等已有组件。
    //   - 渲染管线: 通过 SceneRenderCallback 回调复用 ForwardRenderer /
    //     HybridLightingRenderer 的场景渲染能力 (宿主负责把回调绑定到具体渲染器)。
    // ════════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────────────────
    // 全局设置
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 平面反射全局设置。对应 UE5 中 FPlanarReflectionScreenPass 的全局参数与
    /// r.PlanarReflection.* 控制台变量。可在运行期修改。
    /// </summary>
    public sealed class PlanarReflectionSettings
    {
        /// <summary>全局反射纹理分辨率倍率 (相对屏幕分辨率)。1.0 = 全分辨率, 0.5 = 半分辨率。</summary>
        public float GlobalResolutionScale = 0.5f;

        /// <summary>同时渲染的反射平面数量上限。多平面场景中按距离选最近的 N 个, 其余回退 SSR。</summary>
        public int MaxActiveReflections = 2;

        /// <summary>
        /// 隔帧更新频率。1 = 每帧重渲染; 2 = 隔帧 (每 2 帧渲染 1 次, 中间帧复用上次结果);
        /// 3 = 每 3 帧渲染 1 次, 以此类推。N 越大越省性能但反射延迟越高。
        /// </summary>
        public int UpdateRate = 1;

        /// <summary>是否强制半分辨率 (在 GlobalResolutionScale 之上再 /2)。</summary>
        public bool bEnableHalfResolution = false;

        /// <summary>是否启用远距屏幕空间回退 (SSR)。关闭后超距平面直接不反射。</summary>
        public bool bEnableScreenSpaceFallback = true;

        /// <summary>反射纹理最大边长 (像素), 防止 4K 屏幕下反射纹理过大。</summary>
        public int MaxReflectionTextureSize = 2048;

        /// <summary>反射纹理最小边长 (像素)。</summary>
        public int MinReflectionTextureSize = 128;

        /// <summary>反射颜色纹理格式 (HDR 场景使用 R16G16B16A16_Float, LDR 使用 R8G8B8A8_UNorm)。</summary>
        public RHIFormat DefaultColorFormat = RHIFormat.R16G16B16A16_Float;

        /// <summary>反射深度纹理格式。</summary>
        public RHIFormat DefaultDepthFormat = RHIFormat.D32_Float;

        /// <summary>
        /// 反射渲染时是否翻转背面剔除。镜像变换是镜像 (行列式为 -1), 会翻转三角形
        /// 绕序, 因此反射 Pass 必须翻转 CullMode (Front<->Back) 才不会误剔正面。
        /// </summary>
        public bool bFlipCulling = true;

        /// <summary>是否启用平面反射系统总开关。</summary>
        public bool bEnabled = true;

        /// <summary>是否绘制调试可视化 (平面边界 / 法线 / 裁剪范围)。</summary>
        public bool bDrawDebug = false;

        /// <summary>渲染池中每种 (尺寸,格式) 桶保留的空闲纹理上限, 超出则立即销毁。</summary>
        public int PoolBucketCapacity = 4;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 反射组件 (ECS)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 平面反射组件。挂载到任意实体上即可声明一个反射平面 (镜面/地板/水面)。
    /// 对应 UE5 的 UPlanarReflectionComponent。
    /// 必须为 struct 并实现 IComponent, 以便参与 ECS 查询与值语义拷贝。
    /// </summary>
    public struct PlanarReflectionComponent : IComponent
    {
        /// <summary>反射平面法线 (世界空间, 无需归一化, 内部会归一化)。通常为 (0,1,0) 表示地板。</summary>
        public Vector3 PlaneNormal;

        /// <summary>反射平面上一点 (世界空间)。配合 PlaneNormal 确定平面位置, 如水面高度点。</summary>
        public Vector3 PlanePoint;

        /// <summary>反射纹理分辨率倍率 (相对屏幕)。0 = 使用全局 GlobalResolutionScale。</summary>
        public float ResolutionScale;

        /// <summary>最大反射距离 (世界单位)。超出此距离的平面不参与渲染, 回退 SSR。</summary>
        public float MaxViewDistance;

        /// <summary>是否使用 HDR 反射纹理。HDR 反射能保留高光过曝信息。</summary>
        public bool bUseHDR;

        /// <summary>远距是否回退到屏幕空间反射 (SSR)。关闭则超距直接消失。</summary>
        public bool bScreenSpaceFallback;

        /// <summary>菲涅尔强度。控制掠射角下反射增强的程度 (水面尤其依赖)。</summary>
        public float FresnelStrength;

        /// <summary>距离淡出 (x=起始距离, y=结束距离)。在 [x,y] 区间内反射线性淡出到 0。</summary>
        public Vector2 DistanceFade;

        /// <summary>组件是否启用。</summary>
        public bool bEnabled;

        /// <summary>优先级 (相同距离时数值大的优先被选中渲染)。</summary>
        public int Priority;

        /// <summary>是否双面反射 (法线正反两侧都反射)。水面通常 false, 镜子 true。</summary>
        public bool bTwoSided;

        /// <summary>本帧是否已回退到 SSR (由系统填写, 供材质读取)。</summary>
        public bool bUsingScreenSpaceFallback;

        /// <summary>本帧反射强度 (0~1, 已结合距离淡出与菲涅尔, 由系统填写, 供材质读取)。</summary>
        public float CurrentStrength;

        /// <summary>构造一个典型地板/水面反射组件的便捷默认值。</summary>
        public static PlanarReflectionComponent CreateFloor(float planeY, float resolutionScale = 0.5f)
        {
            return new PlanarReflectionComponent
            {
                PlaneNormal = new Vector3(0f, 1f, 0f),
                PlanePoint = new Vector3(0f, planeY, 0f),
                ResolutionScale = resolutionScale,
                MaxViewDistance = 5000f,
                bUseHDR = true,
                bScreenSpaceFallback = true,
                FresnelStrength = 1.0f,
                DistanceFade = new Vector2(2000f, 4500f),
                bEnabled = true,
                Priority = 0,
                bTwoSided = false,
            };
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // GPU 常量缓冲
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 平面反射 GPU 常量缓冲。每个活跃反射平面一份, 供下游材质采样反射纹理时使用。
    /// 对应 UE5 PlanarReflection 的 shader 参数。布局按 16 字节对齐 (HLSL cbuffer)。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    public struct PlanarReflectionCB
    {
        /// <summary>反射摄像机 视图×投影 (含斜裁剪)。材质用它把世界坐标投影到反射纹理 UV。</summary>
        public Matrix4x4 ReflectionViewProj;

        /// <summary>ReflectionViewProj 的逆矩阵。用于从反射纹理 UV 反推世界坐标 (光线步进等)。</summary>
        public Matrix4x4 InvReflectionViewProj;

        /// <summary>反射平面方程 (法线.xyz, d), 世界空间: dot(normal, p) + d = 0。</summary>
        public Vector4 Plane;

        /// <summary>平面上一点 (世界空间)。</summary>
        public Vector3 PlanePoint;

        /// <summary>菲涅尔强度。</summary>
        public float FresnelStrength;

        /// <summary>距离淡出 (起始, 结束)。</summary>
        public Vector2 DistanceFade;

        /// <summary>最大反射距离。</summary>
        public float MaxViewDistance;

        /// <summary>分辨率倍率。</summary>
        public float ResolutionScale;

        /// <summary>反射纹理尺寸 (像素)。</summary>
        public Vector2 TextureSize;

        /// <summary>本帧反射强度 (已结合距离淡出)。材质按此 alpha 混合反射纹理。</summary>
        public float Strength;

        /// <summary>是否使用 HDR 纹理 (1=是, 0=否)。</summary>
        public int bUseHDR;

        /// <summary>是否回退到 SSR (1=是, 0=否)。</summary>
        public int bUsingSSR;

        /// <summary>对齐填充。</summary>
        public float _pad0;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 单个反射平面的渲染结果 (供材质/水面系统查询)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 一张反射纹理的运行期句柄。材质 (如水面/镜面材质) 通过实体 ID 查询本结果,
    /// 取 ReflectionSRV 采样反射颜色, 取 ConstantBuffer 绑定反射参数。
    /// </summary>
    public sealed class PlanarReflectionResult : IDisposable
    {
        /// <summary>所属实体 ID。</summary>
        public int EntityId;

        /// <summary>反射颜色纹理。</summary>
        public RHITexture? ReflectionColor;

        /// <summary>反射颜色 SRV (供像素着色器采样)。</summary>
        public RHIDescriptorHandle ReflectionSRV;

        /// <summary>反射参数常量缓冲。</summary>
        public RHIBuffer? ConstantBuffer;

        /// <summary>常量缓冲 GPU 虚拟地址。</summary>
        public ulong ConstantBufferGpuAddr;

        /// <summary>反射 (视图×投影), 含斜裁剪。</summary>
        public Matrix4x4 ReflectionViewProj;

        /// <summary>世界空间平面方程 (normal.xyz, d)。</summary>
        public Vector4 Plane;

        /// <summary>本帧反射强度 (0~1)。</summary>
        public float Strength;

        /// <summary>菲涅尔项 (0~1)。</summary>
        public float Fresnel;

        /// <summary>是否已回退到 SSR。</summary>
        public bool bUsingScreenSpaceFallback;

        /// <summary>反射纹理宽高。</summary>
        public int Width, Height;

        /// <summary>是否本帧已更新 (false 表示复用上帧结果)。</summary>
        public bool bUpdatedThisFrame;

        /// <summary>占用的池化 RT (由系统在释放时归还池)。</summary>
        internal PooledReflectionRT? OwnedRT;

        /// <summary>常量缓冲数据快照 (CPU 侧)。</summary>
        public PlanarReflectionCB CBData;

        private bool _disposed;

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            // 注意: ReflectionColor 与 OwnedRT 归 ReflectionRenderTargetPool 管理,
            // 此处只释放独占的常量缓冲。RT 由系统调用 pool.Release 归还。
            ConstantBuffer?.Dispose();
            ConstantBuffer = null;
            _disposed = true;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 数学工具: 反射 / 反射矩阵 / 斜裁剪投影 (Möbius 变换)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 平面反射数学工具集。所有矩阵采用 System.Numerics 行向量约定 (p' = p * M),
    /// 与引擎现有 TransformComponent.Matrix / ForwardRenderer 一致。
    /// </summary>
    public static class PlanarReflectionMath
    {
        /// <summary>
        /// 向量反射: r = v - 2*(v·n)*n。n 需归一化。
        /// 对应 HLSL 的 reflect() (注意 HLSL reflect 取入射方向反向)。
        /// </summary>
        public static Vector3 Reflect(Vector3 v, Vector3 n)
        {
            float d = Vector3.Dot(v, n);
            return v - 2.0f * d * n;
        }

        /// <summary>
        /// 反射摄像机位置: 沿平面 (planePoint, planeNormal) 对称翻转。
        /// reflectedPos = camPos - 2 * dot(camPos - planePoint, n) * n。
        /// </summary>
        public static Vector3 ReflectCameraPosition(Vector3 camPos, Vector3 planeNormal, Vector3 planePoint)
        {
            Vector3 n = Vector3.Normalize(planeNormal);
            float d = Vector3.Dot(camPos - planePoint, n);
            return camPos - 2.0f * d * n;
        }

        /// <summary>
        /// 构建反射变换矩阵 (行向量约定, p' = p * M)。沿平面 (planePoint, planeNormal)
        /// 做镜像翻转。线性部分 L = I - 2*n*n^T, 平移部分 t = 2*dot(planePoint, n)*n。
        /// 这是把"反射场景"与"反射摄像机"统一的对称变换。
        /// </summary>
        public static Matrix4x4 BuildReflectionMatrix(Vector3 planePoint, Vector3 planeNormal)
        {
            Vector3 n = Vector3.Normalize(planeNormal);
            float nx = n.X, ny = n.Y, nz = n.Z;
            float pd = Vector3.Dot(planePoint, n);
            float tx = 2.0f * pd * nx;
            float ty = 2.0f * pd * ny;
            float tz = 2.0f * pd * nz;

            // 线性部分 I - 2 n n^T (放在前 3 行), 平移放在第 4 行 (行向量约定)
            return new Matrix4x4(
                1f - 2f * nx * nx, -2f * nx * ny,      -2f * nx * nz,      0f,
                -2f * ny * nx,      1f - 2f * ny * ny, -2f * ny * nz,      0f,
                -2f * nz * nx,     -2f * nz * ny,      1f - 2f * nz * nz,  0f,
                tx,                 ty,                tz,                 1f);
        }

        /// <summary>
        /// 反射摄像机视图矩阵 = 反射矩阵 * 原视图 (行向量: p * (R * View))。
        /// 数学等价性: "反射场景 + 原摄像机" == "原场景 + 反射摄像机", 故
        /// 用反射摄像机渲染原场景即可得到镜像图像。
        /// </summary>
        public static Matrix4x4 BuildReflectedView(in Matrix4x4 view, Vector3 planePoint, Vector3 planeNormal)
            => BuildReflectionMatrix(planePoint, planeNormal) * view;

        /// <summary>
        /// 仅按法线翻转视图方向 (假定平面过原点; 通用情形请用 BuildReflectedView)。
        /// 对应 UE5 FReflectionVector / MirrorVectorByNormal 的矩阵形式。
        /// </summary>
        public static Matrix4x4 ReflectCameraView(Matrix4x4 view, Vector3 normal)
        {
            Vector3 n = Vector3.Normalize(normal);
            float nx = n.X, ny = n.Y, nz = n.Z;
            Matrix4x4 r = new Matrix4x4(
                1f - 2f * nx * nx, -2f * nx * ny,      -2f * nx * nz,      0f,
                -2f * ny * nx,      1f - 2f * ny * ny, -2f * ny * nz,      0f,
                -2f * nz * nx,     -2f * nz * ny,      1f - 2f * nz * nz,  0f,
                0f,                 0f,                0f,                 1f);
            return r * view;
        }

        /// <summary>
        /// 把世界空间平面方程变换到视图空间。平面是协变量, 需用 inverse(view)^T 变换:
        /// planeView = planeWorld * inverse(view)^T (行向量约定)。
        /// </summary>
        public static Vector4 TransformWorldPlaneToView(Vector4 worldPlane, in Matrix4x4 view)
        {
            if (!Matrix4x4.Invert(view, out Matrix4x4 invView))
                return worldPlane;
            Matrix4x4 invTranspose = Matrix4x4.Transpose(invView);
            // Vector4.Transform(v, M) == v * M (System.Numerics 行向量语义)
            Vector4 pv = Vector4.Transform(worldPlane, invTranspose);
            float len = MathF.Sqrt(pv.X * pv.X + pv.Y * pv.Y + pv.Z * pv.Z);
            if (len > 1e-6f) pv /= len;
            return pv;
        }

        /// <summary>
        /// 斜裁剪投影 (Oblique Projection / Möbius 变换)。对应 UE5 的
        /// FViewInfo::UpdateProjectionMatrix 与 Eric Lengyel 的经典算法。
        ///
        /// 作用: 修改投影矩阵的近裁剪面, 使其精确对齐给定的视图空间裁剪平面,
        /// 从而剔除该平面"摄像机一侧"的几何 (避免反射纹理穿模)。远裁剪面尽量
        /// 保持穿过视锥远侧对角顶点, 以最小化深度范围损失。
        ///
        /// 算法 (D3D [0,1] 深度约定, 近裁剪面 clip.z=0 由投影矩阵第 3 行决定):
        ///   1. clipPlane 为视图空间平面 (a,b,c,d), 确保摄像机位于其负侧 (d<0),
        ///      即被剔除侧; 若 d>0 则取反平面 (几何不变, 仅翻转保留侧约定)。
        ///   2. 取裁剪空间远侧对角顶点 q = (sgn(a), sgn(b), 1, 1)。
        ///   3. 经逆投影变换得到视图空间对角顶点 c = q * inverse(proj)。
        ///   4. 缩放平面 C' = C * (2 / dot(C, c)) (Möbius 变换)。
        ///   5. 用 C' 替换投影矩阵第 3 行 (M31..M34)。
        ///
        /// 注: 近裁剪面对齐反射平面是精确的 (与缩放系数无关, 因 k*C 与 C 同平面);
        /// 缩放系数仅影响远裁剪面位置 / 深度精度。此处采用与 Three.js / terathon
        /// 一致的 2/dot 标准实现。
        /// </summary>
        public static Matrix4x4 ObliqueProjection(Matrix4x4 proj, Vector4 clipPlane)
        {
            Vector4 C = clipPlane;

            // 确保摄像机 (视图空间原点) 位于裁剪平面负侧, 即保留远离摄像机的场景侧。
            // 原点处平面值 = C.w; 若 >0 表示摄像机在保留侧, 取反平面使其进入剔除侧。
            if (C.W > 0.0f) C = -C;

            // 退化保护: 投影矩阵不可逆时直接返回原矩阵。
            if (!Matrix4x4.Invert(proj, out Matrix4x4 invProj))
                return proj;

            // q = 裁剪平面远侧对角顶点 (D3D [0,1] 深度: 远平面 NDC z=1 => clip (±1,±1,1,1))
            Vector4 q = new Vector4(
                C.X < 0.0f ? -1f : 1f,
                C.Y < 0.0f ? -1f : 1f,
                1.0f,
                1.0f);

            // c = q 经逆投影到摄像机空间 (远侧对角顶点)。Vector4.Transform == q * invProj。
            Vector4 c = Vector4.Transform(q, invProj);

            float denom = Vector4.Dot(C, c);
            if (MathF.Abs(denom) < 1e-6f)
                return proj;

            // Möbius 缩放: 近裁剪面精确对齐反射平面, 远裁剪面尽量保持穿过对角顶点 c。
            C = C * (2.0f / denom);

            // 替换投影矩阵第 3 行 (D3D [0,1] 深度: 近裁剪面 clip.z=0 由第 3 行决定)。
            proj.M31 = C.X;
            proj.M32 = C.Y;
            proj.M33 = C.Z;
            proj.M34 = C.W;
            return proj;
        }

        /// <summary>
        /// 菲涅尔项 (Schlick 近似)。视线与法线夹角越大反射越强。
        /// 用于水面/地板在掠射角下反射增强的混合权重。
        /// </summary>
        public static float ComputeFresnel(Vector3 viewDir, Vector3 normal, float fresnel0, float strength)
        {
            Vector3 n = Vector3.Normalize(normal);
            Vector3 v = Vector3.Normalize(-viewDir);
            float cosTheta = MathF.Abs(Vector3.Dot(v, n));
            float cos5 = MathF.Pow(1.0f - cosTheta, 5.0f);
            float f = fresnel0 + (1.0f - fresnel0) * cos5;
            return Math.Clamp(f * strength, 0f, 1f);
        }

        /// <summary>计算点到平面的有符号距离 (法线需归一化)。</summary>
        public static float DistanceToPlane(Vector3 p, Vector3 planeNormal, Vector3 planePoint)
        {
            Vector3 n = Vector3.Normalize(planeNormal);
            return Vector3.Dot(p - planePoint, n);
        }

        /// <summary>把距离映射到 [fadeStart, fadeEnd] 区间的线性淡出 alpha (远端为 0)。</summary>
        public static float DistanceFadeAlpha(float distance, Vector2 fade)
        {
            if (distance <= fade.X) return 1.0f;
            if (distance >= fade.Y) return 0.0f;
            return 1.0f - (distance - fade.X) / (fade.Y - fade.X);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 反射纹理池 (按 尺寸/格式 分桶复用, 避免反复创建销毁)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>池化的一组反射 RT (颜色 + 深度, 含视图句柄)。</summary>
    public sealed class PooledReflectionRT
    {
        public RHITexture? ColorTexture;
        public RHIDescriptorHandle ColorRTV;
        public RHIDescriptorHandle ColorSRV;
        public RHITexture? DepthTexture;
        public RHIDescriptorHandle DepthDSV;
        public int Width;
        public int Height;
        public RHIFormat ColorFormat;
        public bool InUse;
    }

    /// <summary>
    /// 反射渲染目标池。按 (宽, 高, 颜色格式) 分桶, Acquire 优先复用空闲纹理,
    /// 否则新建; Release 归还桶供下次复用。对应 UE5 的 RenderTargetPool 机制。
    /// </summary>
    public sealed class ReflectionRenderTargetPool : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly Dictionary<(int w, int h, RHIFormat fmt), Stack<PooledReflectionRT>> _buckets = new();
        private readonly List<PooledReflectionRT> _all = new();
        private readonly PlanarReflectionSettings _settings;
        private readonly object _lock = new();
        private bool _disposed;

        /// <summary>构造。</summary>
        public ReflectionRenderTargetPool(RHIDevice device, PlanarReflectionSettings settings)
        {
            _device = device;
            _settings = settings;
        }

        /// <summary>
        /// 获取一组反射 RT。优先从桶中复用, 否则新建。返回的 RT 标记为 InUse。
        /// </summary>
        public PooledReflectionRT Acquire(int width, int height, RHIFormat colorFormat,
            RHIFormat depthFormat, bool needDepth = true)
        {
            // 钳制尺寸到合法范围
            width = Math.Clamp(width, _settings.MinReflectionTextureSize, _settings.MaxReflectionTextureSize);
            height = Math.Clamp(height, _settings.MinReflectionTextureSize, _settings.MaxReflectionTextureSize);

            var key = (width, height, colorFormat);
            lock (_lock)
            {
                if (_buckets.TryGetValue(key, out var stack))
                {
                    while (stack.Count > 0)
                    {
                        var rt = stack.Pop();
                        if (!rt.InUse)
                        {
                            rt.InUse = true;
                            return rt;
                        }
                    }
                }
            }

            // 新建
            var pooled = new PooledReflectionRT
            {
                Width = width,
                Height = height,
                ColorFormat = colorFormat,
                InUse = true
            };

            // 颜色 RT (RenderTarget | ShaderResource)
            var colorDesc = new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = colorFormat,
                Width = width,
                Height = height,
                DepthOrArraySize = 1,
                MipLevels = 1,
                SampleCount = 1,
                Flags = RHITextureFlags.RenderTarget | RHITextureFlags.ShaderResource,
                OptimizedClearValue = RHIClearValueHelper.Color(colorFormat, Vector4.Zero)
            };
            // 先存入局部变量, 再赋给字段 —— 避免 CreateRTV/CreateSRV 收到可能为 null 的字段 (Nullable 警告)
            RHITexture colorTex = _device.CreateTexture(colorDesc, "PlanarReflection.Color");
            pooled.ColorTexture = colorTex;
            pooled.ColorRTV = _device.CreateRTV(colorTex, 0, 0);
            pooled.ColorSRV = _device.CreateSRV(colorTex, new RHISRVDesc
            {
                Format = colorFormat,
                Dimension = RHIResourceDimension.Texture2D,
                MostDetailedMip = 0,
                MipLevels = 1,
                First2DArraySlice = 0,
                ArraySize = 1
            });

            // 深度 DS (DepthStencil | ShaderResource)
            if (needDepth)
            {
                var depthDesc = new RHITextureDesc
                {
                    Dimension = RHIResourceDimension.Texture2D,
                    Format = depthFormat,
                    Width = width,
                    Height = height,
                    DepthOrArraySize = 1,
                    MipLevels = 1,
                    SampleCount = 1,
                    Flags = RHITextureFlags.DepthStencil | RHITextureFlags.ShaderResource,
                    OptimizedClearValue = RHIClearValueHelper.Depth(1.0f, 0, depthFormat)
                };
                pooled.DepthTexture = _device.CreateTexture(depthDesc, "PlanarReflection.Depth");
                pooled.DepthDSV = _device.CreateDSV(pooled.DepthTexture, 0, 0);
            }

            lock (_lock)
            {
                _all.Add(pooled);
            }
            return pooled;
        }

        /// <summary>归还 RT 到桶, 供下次复用。</summary>
        public void Release(PooledReflectionRT rt)
        {
            if (rt == null) return;
            rt.InUse = false;
            var key = (rt.Width, rt.Height, rt.ColorFormat);
            lock (_lock)
            {
                if (!_buckets.TryGetValue(key, out var stack))
                {
                    stack = new Stack<PooledReflectionRT>();
                    _buckets[key] = stack;
                }
                // 限制桶容量, 超出则直接销毁以回收显存
                if (stack.Count >= _settings.PoolBucketCapacity)
                {
                    DisposeRT(rt);
                    _all.Remove(rt);
                }
                else
                {
                    stack.Push(rt);
                }
            }
        }

        /// <summary>清理所有空闲 (未使用) 的池化纹理, 回收显存。</summary>
        public void Trim()
        {
            lock (_lock)
            {
                var toRemove = new List<PooledReflectionRT>();
                foreach (var kvp in _buckets)
                {
                    while (kvp.Value.Count > 0)
                    {
                        var rt = kvp.Value.Pop();
                        DisposeRT(rt);
                        toRemove.Add(rt);
                    }
                }
                _buckets.Clear();
                foreach (var rt in toRemove) _all.Remove(rt);
            }
        }

        private static void DisposeRT(PooledReflectionRT rt)
        {
            rt.ColorTexture?.Dispose();
            rt.DepthTexture?.Dispose();
            rt.ColorTexture = null;
            rt.DepthTexture = null;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            lock (_lock)
            {
                foreach (var rt in _all) DisposeRT(rt);
                _all.Clear();
                _buckets.Clear();
            }
            _disposed = true;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 平面反射渲染器
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 场景渲染回调。宿主把此回调绑定到 ForwardRenderer/HybridLightingRenderer 的场景渲染入口,
    /// 使其用翻转后的 (view, proj) 把场景渲染到指定的反射 RT 中。
    /// 注意: 镜像变换会翻转三角形绕序, 回调内部应按 flipCulling 翻转 CullMode。
    /// </summary>
    public delegate void PlanarReflectionSceneRender(
        RHICommandList cmd,
        RHITexture colorRT, RHIDescriptorHandle colorRTV,
        RHITexture depthRT, RHIDescriptorHandle depthDSV,
        in Matrix4x4 reflectedView, in Matrix4x4 obliqueProj,
        Vector3 reflectedCamPos, Vector3 sunDir,
        int width, int height, bool flipCulling, float dt);

    /// <summary>
    /// 平面反射渲染器。负责单个反射平面的: 反射摄像机计算、斜裁剪投影、
    /// 反射 RT 设置/清屏、调用场景渲染回调、上传常量缓冲、产出反射结果。
    /// </summary>
    public sealed class PlanarReflectionRenderer : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly PlanarReflectionSettings _settings;
        private readonly ReflectionRenderTargetPool _pool;
        private bool _disposed;

        /// <summary>场景渲染回调。未绑定时反射纹理仅被清屏 (便于先跑通管线)。</summary>
        public PlanarReflectionSceneRender? SceneRenderCallback { get; set; }

        /// <summary>主光源方向 (世界空间, 指向光源), 用于反射 Pass 的场景着色。</summary>
        public Vector3 SunDirection { get; set; } = new Vector3(0.3f, 0.8f, 0.2f);

        /// <summary>构造。</summary>
        public PlanarReflectionRenderer(RHIDevice device, PlanarReflectionSettings settings,
            ReflectionRenderTargetPool pool)
        {
            _device = device;
            _settings = settings;
            _pool = pool;
        }

        /// <summary>
        /// 渲染一个反射平面, 写入 result。若 result.ConstantBuffer 为空则按需创建。
        /// </summary>
        /// <param name="cmd">已打开的命令列表, 本方法只记录不提交。</param>
        /// <param name="comp">反射组件。</param>
        /// <param name="baseView">主摄像机视图矩阵 (世界->视图)。</param>
        /// <param name="baseProj">主摄像机投影矩阵 (用于派生斜裁剪投影)。</param>
        /// <param name="camPos">主摄像机世界位置。</param>
        /// <param name="screenW">屏幕宽 (用于推算反射纹理尺寸)。</param>
        /// <param name="screenH">屏幕高。</param>
        /// <param name="dt">帧时间。</param>
        /// <param name="result">接收渲染结果 (会被原地更新)。</param>
        public void RenderReflection(RHICommandList cmd, in PlanarReflectionComponent comp,
            in Matrix4x4 baseView, in Matrix4x4 baseProj, Vector3 camPos,
            int screenW, int screenH, float dt, PlanarReflectionResult result)
        {
            Vector3 planeNormal = Vector3.Normalize(comp.PlaneNormal);
            Vector3 planePoint = comp.PlanePoint;

            // ── 1. 计算反射纹理分辨率 (屏幕 × 倍率, 可选半分辨率) ──
            float scale = comp.ResolutionScale > 0f ? comp.ResolutionScale : _settings.GlobalResolutionScale;
            if (_settings.bEnableHalfResolution) scale *= 0.5f;
            int w = Math.Max(1, (int)(screenW * scale));
            int h = Math.Max(1, (int)(screenH * scale));

            RHIFormat colorFmt = comp.bUseHDR ? _settings.DefaultColorFormat : RHIFormat.R8G8B8A8_UNorm;

            // ── 2. 从池获取反射 RT (颜色 + 深度) ──
            // 释放上一帧的 RT (尺寸可能变化), 重新获取
            if (result.OwnedRT != null)
            {
                _pool.Release(result.OwnedRT);
                result.OwnedRT = null;
            }
            var rt = _pool.Acquire(w, h, colorFmt, _settings.DefaultDepthFormat, true);
            result.OwnedRT = rt;

            // ── 3. 反射摄像机: 反射位置 + 反射视图矩阵 ──
            Vector3 reflectedCamPos = PlanarReflectionMath.ReflectCameraPosition(camPos, planeNormal, planePoint);
            // 反射视图 = 反射矩阵 * 原视图 (等价于反射摄像机看原场景)
            Matrix4x4 reflectedView = PlanarReflectionMath.BuildReflectedView(baseView, planePoint, planeNormal);

            // ── 4. 斜裁剪投影: 把近裁剪面对齐到反射平面 ──
            // 世界空间平面方程 (normal, d): dot(n,p) + d = 0 => d = -dot(n, planePoint)
            Vector4 worldPlane = new Vector4(planeNormal, -Vector3.Dot(planeNormal, planePoint));
            // 变换到反射摄像机的视图空间
            Vector4 viewPlane = PlanarReflectionMath.TransformWorldPlaneToView(worldPlane, reflectedView);
            // 双面反射时翻转法线使两侧都能保留 (取绝对值会破坏平面定义, 这里改为根据摄像机
            // 位于平面哪一侧决定是否翻转: 双面情况下始终保证摄像机在剔除侧)
            if (comp.bTwoSided && viewPlane.W > 0f) viewPlane = -viewPlane;
            Matrix4x4 obliqueProj = PlanarReflectionMath.ObliqueProjection(baseProj, viewPlane);

            Matrix4x4 reflectedVP = reflectedView * obliqueProj;

            // ── 5. 设置 RT / 视口 / 裁剪矩形, 清屏 ──
            cmd.TransitionTo(rt.ColorTexture!, RHIResourceState.RenderTarget);
            cmd.TransitionTo(rt.DepthTexture!, RHIResourceState.DepthWrite);
            cmd.OMSetRenderTargets(new[] { rt.ColorRTV }, rt.DepthDSV);
            cmd.SetViewport(0f, 0f, rt.Width, rt.Height, 0f, 1f);
            cmd.SetScissorRect(0, 0, rt.Width, rt.Height);
            cmd.ClearRenderTargetView(rt.ColorRTV, Vector4.Zero);
            cmd.ClearDepthStencilView(rt.DepthDSV, 1.0f, 0);

            // ── 6. 调用场景渲染回调 (用翻转摄像机 + 斜裁剪投影渲染场景到反射 RT) ──
            // 镜像变换翻转绕序, 回调需按 bFlipCulling 翻转 CullMode。
            if (SceneRenderCallback != null)
            {
                SceneRenderCallback(cmd,
                    rt.ColorTexture!, rt.ColorRTV,
                    rt.DepthTexture!, rt.DepthDSV,
                    in reflectedView, in obliqueProj,
                    reflectedCamPos, SunDirection,
                    rt.Width, rt.Height, _settings.bFlipCulling, dt);
            }

            // ── 7. 反射纹理转为像素着色器资源, 供材质采样 ──
            cmd.TransitionTo(rt.ColorTexture!, RHIResourceState.PixelShaderResource);

            // ── 8. 上传常量缓冲 PlanarReflectionCB ──
            EnsureConstantBuffer(result);

            PlanarReflectionCB cb = new PlanarReflectionCB
            {
                ReflectionViewProj = reflectedVP,
                Plane = worldPlane,
                PlanePoint = planePoint,
                FresnelStrength = comp.FresnelStrength,
                DistanceFade = comp.DistanceFade,
                MaxViewDistance = comp.MaxViewDistance,
                ResolutionScale = scale,
                TextureSize = new Vector2(rt.Width, rt.Height),
                Strength = comp.CurrentStrength,
                bUseHDR = comp.bUseHDR ? 1 : 0,
                bUsingSSR = comp.bUsingScreenSpaceFallback ? 1 : 0
            };
            Matrix4x4.Invert(reflectedVP, out cb.InvReflectionViewProj);
            result.CBData = cb;
            _device.UploadToBuffer(result.ConstantBuffer!, new[] { cb });

            // ── 9. 回写结果元数据 ──
            result.ReflectionColor = rt.ColorTexture;
            result.ReflectionSRV = rt.ColorSRV;
            result.ReflectionViewProj = reflectedVP;
            result.Plane = worldPlane;
            result.Strength = comp.CurrentStrength;
            result.Fresnel = comp.FresnelStrength;
            result.bUsingScreenSpaceFallback = comp.bUsingScreenSpaceFallback;
            result.Width = rt.Width;
            result.Height = rt.Height;
            result.bUpdatedThisFrame = true;
        }

        /// <summary>为结果按需创建常量缓冲 (256 字节对齐, Upload 堆)。</summary>
        private void EnsureConstantBuffer(PlanarReflectionResult result)
        {
            if (result.ConstantBuffer != null) return;
            int size = Marshal.SizeOf<PlanarReflectionCB>();
            size = (size + 255) & ~255; // D3D12 常量缓冲需 256 字节对齐
            var desc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)size,
                Flags = RHIBufferFlags.ConstantBuffer,
                HeapType = RHIResourceHeapType.Upload,
                Format = RHIFormat.Unknown,
                Stride = 0
            };
            result.ConstantBuffer = _device.CreateBuffer(desc, "PlanarReflection.CB");
            result.ConstantBufferGpuAddr = result.ConstantBuffer.GpuVirtualAddress;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 调试可视化
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>调试线段绘制回调 (宿主绑定到引擎 DebugLineRenderer)。</summary>
    public delegate void PlanarReflectionDebugLine(Vector3 a, Vector3 b, Vector4 color);

    /// <summary>
    /// 平面反射调试器。绘制反射平面边界、法线、裁剪范围, 对应 UE5 的
    /// ShowFlag.PlanarReflections / PlanarReflectionDebug 视图模式。
    /// </summary>
    public sealed class PlanarReflectionDebugger
    {
        /// <summary>调试线绘制回调。</summary>
        public PlanarReflectionDebugLine? LineDraw { get; set; }

        /// <summary>绘制单个反射平面的边界 (size×size 的方形) + 法线 + 裁剪范围。</summary>
        public void DrawPlane(Vector3 planeNormal, Vector3 planePoint, float size,
            Vector4 boundaryColor, Vector4 normalColor)
        {
            if (LineDraw == null) return;
            Vector3 n = Vector3.Normalize(planeNormal);
            // 构造平面内两个正交基
            Vector3 u = MathF.Abs(n.Y) > 0.99f ? new Vector3(1, 0, 0) : new Vector3(0, 1, 0);
            Vector3 tangent = Vector3.Normalize(u - n * Vector3.Dot(u, n));
            Vector3 bitangent = Vector3.Cross(n, tangent);

            float h = size * 0.5f;
            Vector3 p1 = planePoint + tangent * h + bitangent * h;
            Vector3 p2 = planePoint - tangent * h + bitangent * h;
            Vector3 p3 = planePoint - tangent * h - bitangent * h;
            Vector3 p4 = planePoint + tangent * h - bitangent * h;

            LineDraw(p1, p2, boundaryColor);
            LineDraw(p2, p3, boundaryColor);
            LineDraw(p3, p4, boundaryColor);
            LineDraw(p4, p1, boundaryColor);

            // 法线箭头
            Vector3 tip = planePoint + n * size * 0.5f;
            LineDraw(planePoint, tip, normalColor);
            LineDraw(tip, tip - n * size * 0.1f + tangent * size * 0.05f, normalColor);
            LineDraw(tip, tip - n * size * 0.1f - tangent * size * 0.05f, normalColor);
        }

        /// <summary>绘制距离淡出范围 (两个同心方形)。</summary>
        public void DrawClipRange(Vector3 planeNormal, Vector3 planePoint,
            float innerDist, float outerDist, Vector4 color)
        {
            if (LineDraw == null) return;
            DrawPlane(planeNormal, planePoint, innerDist * 2f, color, color);
            DrawPlane(planeNormal, planePoint, outerDist * 2f, color * 0.5f, color * 0.5f);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // ECS 系统: PlanarReflectionSystem
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 平面反射 ECS 系统。每帧收集所有 PlanarReflectionComponent, 按到主摄像机距离排序,
    /// 选择前 MaxActiveReflections 个进行渲染, 管理 RT 池与结果字典, 实现隔帧更新与
    /// 远距 SSR 回退。对应 UE5 的 FPlanarReflectionSceneProxy 编排逻辑。
    /// </summary>
    public sealed class PlanarReflectionSystem : ISystem, IDisposable
    {
        /// <inheritdoc/>
        public string Name => "PlanarReflectionSystem";

        private readonly PlanarReflectionSettings _settings;
        private RHIDevice? _device;
        private ReflectionRenderTargetPool? _pool;
        private PlanarReflectionRenderer? _renderer;
        private PlanarReflectionDebugger? _debugger;

        // 活跃反射结果, 按实体 ID 索引 (供材质/水面系统查询)
        private readonly Dictionary<int, PlanarReflectionResult> _results = new();
        // 本帧被选中渲染/复用的实体 ID (用于剔除已不活跃的结果)
        private readonly HashSet<int> _activeSet = new();

        private int _frameIndex;

        // 屏幕尺寸 (宿主每帧更新)
        /// <summary>屏幕宽度 (像素), 宿主每帧设置。</summary>
        public int ScreenWidth { get; set; } = 1920;
        /// <summary>屏幕高度 (像素), 宿主每帧设置。</summary>
        public int ScreenHeight { get; set; } = 1080;

        // 主摄像机覆盖 (宿主可直接提供, 否则由 ECS CameraComponent 计算)
        /// <summary>主摄像机视图矩阵覆盖 (非零则使用, 否则从 CameraComponent 计算)。</summary>
        public Matrix4x4 MainView { get; set; }
        /// <summary>主摄像机投影矩阵覆盖。</summary>
        public Matrix4x4 MainProj { get; set; }
        /// <summary>主摄像机世界位置覆盖。</summary>
        public Vector3 MainCameraPosition { get; set; }
        /// <summary>是否使用覆盖的主摄像机矩阵。</summary>
        public bool bUseCameraOverride { get; set; }

        /// <summary>设置 (运行期可改)。</summary>
        public PlanarReflectionSettings Settings => _settings;
        /// <summary>渲染器 (宿主可绑定其 SceneRenderCallback)。</summary>
        public PlanarReflectionRenderer? Renderer => _renderer;
        /// <summary>调试器。</summary>
        public PlanarReflectionDebugger? Debugger => _debugger;

        /// <summary>无参构造 (延迟到首次 Update 时按需初始化, 需先设置 Device)。</summary>
        public PlanarReflectionSystem() : this(new PlanarReflectionSettings()) { }

        /// <summary>带设置的构造。</summary>
        public PlanarReflectionSystem(PlanarReflectionSettings settings)
        {
            _settings = settings;
        }

        /// <summary>指定设备构造并立即初始化子系统。</summary>
        public PlanarReflectionSystem(RHIDevice device, PlanarReflectionSettings? settings = null)
            : this(settings ?? new PlanarReflectionSettings())
        {
            Initialize(device);
        }

        /// <summary>初始化池 / 渲染器 / 调试器。可在设置 Device 后调用。</summary>
        public void Initialize(RHIDevice device)
        {
            _device = device;
            _pool ??= new ReflectionRenderTargetPool(device, _settings);
            _renderer ??= new PlanarReflectionRenderer(device, _settings, _pool);
            _debugger ??= new PlanarReflectionDebugger();
        }

        /// <summary>设置设备 (用于无参构造后注入)。</summary>
        public RHIDevice? Device
        {
            get => _device;
            set
            {
                if (_device != value)
                {
                    _device = value;
                    if (value != null) Initialize(value);
                }
            }
        }

        /// <summary>查询某实体的反射结果 (供材质/水面系统采样)。</summary>
        public PlanarReflectionResult? GetResult(int entityId)
        {
            return _results.TryGetValue(entityId, out var r) ? r : null;
        }

        /// <summary>当前所有活跃反射结果。</summary>
        public IReadOnlyDictionary<int, PlanarReflectionResult> ActiveResults => _results;

        /// <inheritdoc/>
        public void Update(float dt, EntityComponentSystem ecs)
        {
            if (!_settings.bEnabled) return;
            if (_device == null || _renderer == null || _pool == null) return;

            _activeSet.Clear();
            _frameIndex++;

            // ── 1. 定位主摄像机 ──
            var cameraEntities = ecs.QueryWithComponent<CameraComponent>();
            int mainCamEntity = -1;
            CameraComponent mainCam = default;
            TransformComponent mainCamTf = default;
            bool hasCam = false;
            foreach (var e in cameraEntities)
            {
                var c = ecs.GetComponent<CameraComponent>(e);
                if (c.IsMain)
                {
                    mainCamEntity = e;
                    mainCam = c;
                    mainCamTf = ecs.GetComponent<TransformComponent>(e);
                    hasCam = true;
                    break;
                }
            }
            if (!hasCam && cameraEntities.Count > 0)
            {
                mainCamEntity = cameraEntities[0];
                mainCam = ecs.GetComponent<CameraComponent>(mainCamEntity);
                mainCamTf = ecs.GetComponent<TransformComponent>(mainCamEntity);
                hasCam = true;
            }
            if (!hasCam) return;

            // ── 2. 计算主摄像机 view/proj/pos ──
            Matrix4x4 view, proj;
            Vector3 camPos;
            if (bUseCameraOverride)
            {
                view = MainView;
                proj = MainProj;
                camPos = MainCameraPosition;
            }
            else
            {
                camPos = mainCamTf.Position;
                Vector3 target = camPos + mainCamTf.Forward;
                view = Matrix4x4.CreateLookAt(camPos, target, mainCamTf.Up);
                proj = mainCam.IsOrthographic
                    ? Matrix4x4.CreateOrthographic(mainCam.OrthographicSize * mainCam.AspectRatio,
                                                  mainCam.OrthographicSize, mainCam.NearClip, mainCam.FarClip)
                    : Matrix4x4.CreatePerspectiveFieldOfView(mainCam.FieldOfView, mainCam.AspectRatio,
                                                             mainCam.NearClip, mainCam.FarClip);
            }

            // ── 3. 收集所有 PlanarReflectionComponent ──
            var entities = ecs.QueryWithComponent<PlanarReflectionComponent>();
            if (entities.Count == 0) return;

            // 计算每个平面到摄像机的距离, 用于排序与剔除
            var candidates = new List<(int id, PlanarReflectionComponent comp, float dist)>();
            foreach (var e in entities)
            {
                var comp = ecs.GetComponent<PlanarReflectionComponent>(e);
                if (!comp.bEnabled) continue;

                // 若法线为零, 尝试用实体 Transform 的 Up 作为法线, Position 作为平面点
                if (comp.PlaneNormal == Vector3.Zero)
                {
                    var tf = ecs.GetComponent<TransformComponent>(e);
                    comp.PlaneNormal = tf.Up;
                    if (comp.PlanePoint == Vector3.Zero) comp.PlanePoint = tf.Position;
                }

                float dist = MathF.Abs(PlanarReflectionMath.DistanceToPlane(camPos, comp.PlaneNormal, comp.PlanePoint));
                candidates.Add((e, comp, dist));
            }
            if (candidates.Count == 0) return;

            // ── 4. 按距离升序排序 (距离近的优先), 同距离按 Priority 降序 ──
            candidates.Sort((a, b) =>
            {
                int c = a.dist.CompareTo(b.dist);
                if (c != 0) return c;
                return b.comp.Priority.CompareTo(a.comp.Priority);
            });

            // ── 5. 选择前 MaxActiveReflections 个 ──
            int maxActive = Math.Max(1, _settings.MaxActiveReflections);
            int selected = 0;

            // 创建/复用命令列表 (由系统拥有, 记录所有反射 Pass 后统一提交)
            RHICommandList? cmd = null;
            bool cmdOpened = false;

            try
            {
                foreach (var (id, comp, dist) in candidates)
                {
                    if (selected >= maxActive) break;
                    _activeSet.Add(id);

                    PlanarReflectionComponent c = comp;

                    // ── 5a. 距离淡出与 SSR 回退 ──
                    float fadeAlpha = PlanarReflectionMath.DistanceFadeAlpha(dist, c.DistanceFade);
                    bool outOfRange = dist > c.MaxViewDistance;

                    if (outOfRange)
                    {
                        // 超出最大反射距离: 回退 SSR 或直接不反射
                        c.bUsingScreenSpaceFallback = _settings.bEnableScreenSpaceFallback && c.bScreenSpaceFallback;
                        c.CurrentStrength = 0f;
                        ecs.SetComponent(id, c);

                        // 释放该实体的旧 RT (若有)
                        if (_results.TryGetValue(id, out var oldRes))
                        {
                            if (oldRes.OwnedRT != null) { _pool.Release(oldRes.OwnedRT); oldRes.OwnedRT = null; }
                            oldRes.bUpdatedThisFrame = false;
                            oldRes.bUsingScreenSpaceFallback = c.bUsingScreenSpaceFallback;
                            oldRes.Strength = 0f;
                        }
                        selected++;
                        continue;
                    }

                    c.bUsingScreenSpaceFallback = false;
                    c.CurrentStrength = Math.Clamp(fadeAlpha, 0f, 1f);

                    // ── 5b. 隔帧更新: (frameIndex + 选中序号) % UpdateRate == 0 才重渲染 ──
                    // 用 selected 偏移使多个平面错峰更新, 平摊开销
                    int rate = Math.Max(1, _settings.UpdateRate);
                    bool shouldRender = ((uint)(_frameIndex + selected) % (uint)rate) == 0;

                    // 获取/创建结果
                    if (!_results.TryGetValue(id, out var result))
                    {
                        result = new PlanarReflectionResult { EntityId = id };
                        _results[id] = result;
                    }

                    if (shouldRender)
                    {
                        // 懒打开命令列表
                        if (!cmdOpened)
                        {
                            cmd = _device.CreateCommandList(RHIQueueType.Graphics, "PlanarReflection");
                            cmd.Reset();
                            cmdOpened = true;
                        }

                        _renderer.RenderReflection(cmd, in c, in view, in proj, camPos,
                            ScreenWidth, ScreenHeight, dt, result);
                    }
                    else
                    {
                        // 复用上帧结果, 仅更新强度/淡出
                        result.bUpdatedThisFrame = false;
                        result.Strength = c.CurrentStrength;
                        result.bUsingScreenSpaceFallback = false;
                        result.CBData.Strength = c.CurrentStrength;
                        result.CBData.bUsingSSR = 0;
                        // 重新上传 CB 以反映强度变化
                        if (result.ConstantBuffer != null)
                            _device.UploadToBuffer(result.ConstantBuffer, new[] { result.CBData });
                    }

                    ecs.SetComponent(id, c);
                    selected++;

                    // ── 5c. 调试可视化 ──
                    if (_settings.bDrawDebug && _debugger != null)
                    {
                        float dbgSize = Math.Min(c.DistanceFade.Y, 500f);
                        _debugger.DrawPlane(c.PlaneNormal, c.PlanePoint, dbgSize,
                            new Vector4(0f, 1f, 1f, 1f), new Vector4(1f, 1f, 0f, 1f));
                        _debugger.DrawClipRange(c.PlaneNormal, c.PlanePoint,
                            c.DistanceFade.X, c.DistanceFade.Y, new Vector4(0.5f, 0.5f, 1f, 1f));
                    }
                }

                // ── 6. 提交命令列表 ──
                if (cmdOpened && cmd != null)
                {
                    cmd.Close();
                    _device.GetCommandQueue(RHIQueueType.Graphics).ExecuteCommandLists(cmd);
                    _device.GetCommandQueue(RHIQueueType.Graphics).Flush();
                }
            }
            finally
            {
                // ── 7. 清理不再活跃的结果 (实体被删除或被挤出 MaxActiveReflections) ──
                var toRemove = new List<int>();
                foreach (var kvp in _results)
                {
                    if (!_activeSet.Contains(kvp.Key))
                    {
                        if (kvp.Value.OwnedRT != null) _pool.Release(kvp.Value.OwnedRT);
                        kvp.Value.Dispose();
                        toRemove.Add(kvp.Key);
                    }
                }
                foreach (var id in toRemove) _results.Remove(id);
            }
        }

        /// <summary>清空所有反射结果并释放 RT (场景切换时调用)。</summary>
        public void ClearResults()
        {
            if (_pool != null)
            {
                foreach (var kvp in _results)
                {
                    if (kvp.Value.OwnedRT != null) _pool.Release(kvp.Value.OwnedRT);
                    kvp.Value.Dispose();
                }
            }
            _results.Clear();
            _activeSet.Clear();
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            ClearResults();
            _renderer?.Dispose();
            _pool?.Dispose();
        }
    }
}
