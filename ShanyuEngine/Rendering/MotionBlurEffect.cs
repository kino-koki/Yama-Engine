using System;
using System.Numerics;
using System.Runtime.InteropServices;
using ShanyuEngine.RHI;
using ShanyuEngine.Shaders;

namespace ShanyuEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨 运动模糊效果 (Motion Blur Effect)
    //
    // 基于运动向量的 Per-Pixel 运动模糊，包含：
    //   1. TileMax：16x16 Tile 内最大速度提取
    //   2. NeighborMax：3x3 Tile 邻域最大速度扩展（消除边界断裂）
    //   3. Reconstruction：Catmull-Rom 插值重建，沿运动方向多次采样
    //   4. VelocityBuffer：运动向量生成（相机运动 + PerObject 运动）
    // ════════════════════════════════════════════════════════════════════════

    // ────────────────────────────────────────────────────────────────────────
    // MotionBlurSettings — 运动模糊配置
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 运动模糊效果设置（值类型，可序列化）。
    /// </summary>
    [Serializable]
    public struct MotionBlurSettings
    {
        /// <summary>是否启用运动模糊。</summary>
        public bool Enabled;

        /// <summary>
        /// 运动模糊强度系数 [0, 1]。
        /// 控制最终运动模糊效果的强度缩放。
        /// 0 = 无模糊，1 = 完全模糊。
        /// 默认 0.5。
        /// </summary>
        public float Intensity;

        /// <summary>
        /// 最大速度（像素）。限制单个像素的最大运动向量长度。
        /// 超过此速度的运动会截断到该值，防止极端情况下的伪影。
        /// 范围 [1, 200]，默认 64.0。
        /// </summary>
        public float MaxVelocity;

        /// <summary>
        /// 采样数量。沿运动方向收集的样本数。
        /// 更多采样 = 更高质量但更低性能。
        /// 范围 [4, 32]，默认 16。
        /// </summary>
        public int SampleCount;

        /// <summary>工厂方法：创建默认设置。</summary>
        public static MotionBlurSettings Default => new()
        {
            Enabled = true,
            Intensity = 0.5f,
            MaxVelocity = 64.0f,
            SampleCount = 16
        };

        /// <summary>工厂方法：高质量预设。</summary>
        public static MotionBlurSettings HighQuality => new()
        {
            Enabled = true,
            Intensity = 0.7f,
            MaxVelocity = 100.0f,
            SampleCount = 24
        };

        /// <summary>工厂方法：低性能预设。</summary>
        public static MotionBlurSettings LowQuality => new()
        {
            Enabled = true,
            Intensity = 0.3f,
            MaxVelocity = 32.0f,
            SampleCount = 8
        };

        /// <summary>工厂方法：禁用。</summary>
        public static MotionBlurSettings Disabled => new() { Enabled = false };
    }

    // ────────────────────────────────────────────────────────────────────────
    // 运动模糊常量缓冲
    // ────────────────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    internal struct MBTileMaxCB
    {
        public Vector2 InvResolution;
        public float MaxVelocity;
        public int Pad0, Pad1, Pad2;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MBNeighborMaxCB
    {
        public Vector2 InvTileResolution;
        public float MaxVelocity;
        public int Pad0, Pad1, Pad2;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MBReconstructCB
    {
        public Vector2 InvResolution;
        public Vector2 InvTileResolution;
        public float Intensity;
        public int SampleCount;
        public Matrix4x4 PrevViewProj;
        public Matrix4x4 InvViewProj;
    }

    // ────────────────────────────────────────────────────────────────────────
    // VelocityBuffer — 运动向量缓冲
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 运动向量缓冲管理器。
    /// 生成和管理 Per-Pixel 运动向量纹理，来源包括：
    ///   1. 相机运动：ViewProjection 矩阵差值
    ///   2. PerObject 运动：物体自身旋转/平移贡献
    /// </summary>
    public sealed class VelocityBuffer : IDisposable
    {
        /// <summary>运动向量纹理（RG16_Float: R=X速度, G=Y速度）。</summary>
        private RHITexture? _velocityTexture;

        /// <summary>当前帧 VP 矩阵。</summary>
        private Matrix4x4 _currentViewProj;
        /// <summary>上一帧 VP 矩阵。</summary>
        private Matrix4x4 _prevViewProj;

        /// <summary>设备引用。</summary>
        private readonly RHIDevice _device;

        /// <summary>运动向量纹理。</summary>
        public RHITexture? Texture => _velocityTexture;

        /// <summary>屏幕宽高。</summary>
        private int _width, _height;

        public VelocityBuffer(RHIDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
        }

        /// <summary>创建运动向量纹理资源。</summary>
        public void Create(int width, int height)
        {
            _width = width;
            _height = height;

            var desc = new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R16G16_Float,
                Width = width,
                Height = height,
                DepthOrArraySize = 1,
                MipLevels = 1,
                SampleCount = 1,
                Flags = RHITextureFlags.UnorderedAccess | RHITextureFlags.ShaderResource
                    | RHITextureFlags.RenderTarget
            };
            _velocityTexture = _device.CreateTexture(desc, "VelocityBuffer");
        }

        /// <summary>
        /// 每帧更新：存储当前 VP，供下一帧使用。
        /// </summary>
        public void UpdateViewProjection(in Matrix4x4 currentViewProj)
        {
            _prevViewProj = _currentViewProj;
            _currentViewProj = currentViewProj;
        }

        /// <summary>获取上一帧的 VP 矩阵。</summary>
        public Matrix4x4 PrevViewProj => _prevViewProj;

        /// <summary>
        /// CPU 侧计算单个像素的运动向量。
        /// 算法：将当前 NDC 坐标通过当前 VP 的逆矩阵变换到世界空间，
        /// 再通过上一帧 VP 变换回 NDC，取差值得到运动向量。
        /// </summary>
        /// <param name="ndcX">NDC X 坐标 [-1, 1]。</param>
        /// <param name="ndcY">NDC Y 坐标 [-1, 1]。</param>
        /// <param name="linearDepth">线性深度。</param>
        /// <returns>屏幕空间运动向量（像素）。</returns>
        public Vector2 ComputeCameraVelocity(float ndcX, float ndcY, float linearDepth)
        {
            // NDC -> Clip Space
            Vector4 clipPos = new Vector4(ndcX, ndcY, linearDepth, 1.0f);

            // 通过当前 VP 逆矩阵变换到世界空间
            if (!Matrix4x4.Invert(_currentViewProj, out Matrix4x4 invVP))
                return Vector2.Zero;

            Vector4 worldPos = Vector4.Transform(clipPos, invVP);
            if (Math.Abs(worldPos.W) < 0.0001f) return Vector2.Zero;
            worldPos /= worldPos.W;

            // 通过上一帧 VP 变换回 NDC
            Vector4 prevClip = Vector4.Transform(worldPos, _prevViewProj);
            if (Math.Abs(prevClip.W) < 0.0001f) return Vector2.Zero;
            prevClip /= prevClip.W;

            // 运动向量 = 当前 NDC - 上一帧 NDC（转换为像素空间）
            float vx = (ndcX - prevClip.X) * 0.5f * _width;
            float vy = (ndcY - prevClip.Y) * 0.5f * _height;

            return new Vector2(vx, vy);
        }

        /// <summary>释放资源。</summary>
        public void Dispose()
        {
            _velocityTexture?.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // MotionBlurPipeline — 运动模糊管线
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 运动模糊管线。
    /// 执行流程：TileMax → NeighborMax → Reconstruction
    /// </summary>
    public sealed class MotionBlurPipeline : IDisposable
    {
        private const int TileSize = 16;

        private readonly RHIDevice _device;
        private readonly MotionBlurSettings _settings;

        // ── 管线 ──
        private RHIPipelineState? _tileMaxPipeline;
        private RHIPipelineState? _neighborMaxPipeline;
        private RHIPipelineState? _reconstructPipeline;

        // ── 资源 ──
        private RHITexture? _tileMaxTexture;
        private RHITexture? _neighborMaxTexture;

        private int _width, _height;
        private int _tileCountX, _tileCountY;
        private bool _initialized;

        /// <summary>运动向量缓冲。</summary>
        public VelocityBuffer Velocity { get; }

        /// <summary>当前设置。</summary>
        public MotionBlurSettings Settings => _settings;

        /// <summary>是否已初始化。</summary>
        public bool IsInitialized => _initialized;

        public MotionBlurPipeline(RHIDevice device, MotionBlurSettings settings)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _settings = settings;
            Velocity = new VelocityBuffer(device);
        }

        /// <summary>初始化。</summary>
        public void Initialize(int width, int height)
        {
            _width = width;
            _height = height;
            _tileCountX = (width + TileSize - 1) / TileSize;
            _tileCountY = (height + TileSize - 1) / TileSize;

            Velocity.Create(width, height);
            AllocateResources();
            CreatePipelines();
            _initialized = true;
        }

        private void AllocateResources()
        {
            RHITextureDesc MakeRT(string name, int w, int h, RHIFormat fmt) => new()
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = fmt,
                Width = Math.Max(1, w), Height = Math.Max(1, h),
                DepthOrArraySize = 1, MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.UnorderedAccess | RHITextureFlags.ShaderResource
            };

            _tileMaxTexture = _device.CreateTexture(
                MakeRT("MB_TileMax", _tileCountX, _tileCountY, RHIFormat.R16G16_Float), "MB_TileMax");
            _neighborMaxTexture = _device.CreateTexture(
                MakeRT("MB_NeighborMax", _tileCountX, _tileCountY, RHIFormat.R16G16_Float), "MB_NeighborMax");
        }

        private void CreatePipelines()
        {
            _tileMaxPipeline = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "MB_TileMax",
                CS = ShaderManager.Instance.GetBytecode("PostProcess/MotionBlur", "MBTileMaxCS",
                    RHIShaderStage.Compute)
            });
            _neighborMaxPipeline = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "MB_NeighborMax",
                CS = ShaderManager.Instance.GetBytecode("PostProcess/MotionBlur", "MBNeighborMaxCS",
                    RHIShaderStage.Compute)
            });
            _reconstructPipeline = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "MB_Reconstruct",
                CS = ShaderManager.Instance.GetBytecode("PostProcess/MotionBlur", "MBReconstructCS",
                    RHIShaderStage.Compute)
            });
        }

        /// <summary>
        /// 执行运动模糊管线。
        /// </summary>
        public void Execute(RHICommandList cmdList, RHITexture sceneColor, RHITexture motionVectors,
            RHITexture output, in Matrix4x4 prevViewProj, in Matrix4x4 invViewProj)
        {
            if (!_initialized || !_settings.Enabled) return;

            // Phase 1: TileMax
            ExecuteTileMax(cmdList, motionVectors);

            // Phase 2: NeighborMax
            ExecuteNeighborMax(cmdList);

            // Phase 3: Reconstruction
            ExecuteReconstruction(cmdList, sceneColor, output, prevViewProj, invViewProj);
        }

        /// <summary>
        /// Phase 1: TileMax。
        /// 将屏幕分为 16x16 Tile，每个 Tile 内取最大速度。
        /// 算法：
        ///   - 读取 MotionVector 纹理
        ///   - 每个 Tile 分配一个线程组（16x16 线程）
        ///   - 每个线程读取对应像素的运动向量长度 = length(motionVector)
        ///   - 使用 GroupSharedMemory 进行线程组内 Reduce，取最大值
        ///   - 结果写入 TileMax 纹理的对应像素
        /// </summary>
        private void ExecuteTileMax(RHICommandList cmdList, RHITexture motionVectors)
        {
            if (_tileMaxPipeline == null) return;

            cmdList.SetPipelineState(_tileMaxPipeline);
            cmdList.TransitionTo(motionVectors, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(_tileMaxTexture!, RHIResourceState.UnorderedAccess);

            var cb = new MBTileMaxCB
            {
                InvResolution = new Vector2(1.0f / _width, 1.0f / _height),
                MaxVelocity = _settings.MaxVelocity,
                Pad0 = Pad1 = Pad2 = 0
            };

            cmdList.Dispatch(_tileCountX, _tileCountY, 1);
            cmdList.TransitionTo(_tileMaxTexture, RHIResourceState.NonPixelShaderResource);
        }

        /// <summary>
        /// Phase 2: NeighborMax。
        /// 每个 Tile 取自身和 8 个邻居（3x3 邻域）的最大速度。
        /// 确保快速运动物体不会在 Tile 边界产生断裂伪影。
        /// 算法：
        ///   - 对每个 Tile 采样 3x3 邻域
        ///   - 取 9 个 Tile 中的最大速度
        ///   - 结果写入 NeighborMax 纹理
        /// </summary>
        private void ExecuteNeighborMax(RHICommandList cmdList)
        {
            if (_neighborMaxPipeline == null) return;

            cmdList.SetPipelineState(_neighborMaxPipeline);
            cmdList.TransitionTo(_tileMaxTexture!, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(_neighborMaxTexture!, RHIResourceState.UnorderedAccess);

            var cb = new MBNeighborMaxCB
            {
                InvTileResolution = new Vector2(1.0f / _tileCountX, 1.0f / _tileCountY),
                MaxVelocity = _settings.MaxVelocity,
                Pad0 = Pad1 = Pad2 = 0
            };

            cmdList.Dispatch(_tileCountX, _tileCountY, 1);
            cmdList.TransitionTo(_neighborMaxTexture, RHIResourceState.NonPixelShaderResource);
        }

        /// <summary>
        /// Phase 3: Reconstruction。
        /// 根据像素速度确定采样方向和长度，沿运动方向收集多个样本。
        /// 算法：
        ///   1. 读取该像素所属 Tile 的最大速度（从 NeighborMax）
        ///   2. 如果速度 < 1 像素，跳过模糊
        ///   3. 沿运动方向收集 SampleCount 个样本
        ///   4. 使用 Catmull-Rom 插值提高采样质量
        ///   5. 速度权重：靠近原始位置的样本权重更高
        ///      weight(s) = 1.0 - |s| / totalLength
        ///   6. 加权平均所有样本得到最终颜色
        /// </summary>
        private void ExecuteReconstruction(RHICommandList cmdList, RHITexture source, RHITexture output,
            in Matrix4x4 prevViewProj, in Matrix4x4 invViewProj)
        {
            if (_reconstructPipeline == null) return;

            cmdList.SetPipelineState(_reconstructPipeline);
            cmdList.TransitionTo(source, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(_neighborMaxTexture!, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(output, RHIResourceState.UnorderedAccess);

            var cb = new MBReconstructCB
            {
                InvResolution = new Vector2(1.0f / _width, 1.0f / _height),
                InvTileResolution = new Vector2(1.0f / _tileCountX, 1.0f / _tileCountY),
                Intensity = _settings.Intensity,
                SampleCount = _settings.SampleCount,
                PrevViewProj = prevViewProj,
                InvViewProj = invViewProj
            };

            cmdList.Dispatch((_width + 7) / 8, (_height + 7) / 8, 1);
            cmdList.TransitionTo(output, RHIResourceState.NonPixelShaderResource);
        }

        // ── CPU 侧 Catmull-Rom 插值 ──

        /// <summary>
        /// Catmull-Rom 样条插值。
        /// 用于运动模糊重建中提高采样质量。
        /// </summary>
        /// <param name="p0">控制点0。</param>
        /// <param name="p1">控制点1。</param>
        /// <param name="p2">控制点2。</param>
        /// <param name="p3">控制点3。</param>
        /// <param name="t">插值参数 [0, 1]。</param>
        /// <returns>插值结果。</returns>
        public static Vector3 CatmullRomInterpolate(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t;
            float t3 = t2 * t;

            Vector3 result = 0.5f * (
                (2.0f * p1) +
                (-p0 + p2) * t +
                (2.0f * p0 - 5.0f * p1 + 4.0f * p2 - p3) * t2 +
                (-p0 + 3.0f * p1 - 3.0f * p2 + p3) * t3);

            return result;
        }

        /// <summary>
        /// 速度权重函数。
        /// 靠近原始位置（t=0）的样本权重更高。
        /// weight = (1 - |t|)^2
        /// </summary>
        public static float VelocityWeight(float t)
        {
            float absT = Math.Abs(t);
            float w = 1.0f - absT;
            return w * w;
        }

        /// <summary>更新设置。</summary>
        public void UpdateSettings(MotionBlurSettings settings) => _settings = settings;

        /// <summary>释放资源。</summary>
        public void Dispose()
        {
            _tileMaxPipeline?.Dispose();
            _neighborMaxPipeline?.Dispose();
            _reconstructPipeline?.Dispose();
            _tileMaxTexture?.Dispose();
            _neighborMaxTexture?.Dispose();
            Velocity.Dispose();
            _initialized = false;
            GC.SuppressFinalize(this);
        }
    }
}
