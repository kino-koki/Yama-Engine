using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using YamaEngine.RHI;
using YamaEngine.Shaders;
using YamaEngine.ECS;

namespace YamaEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // yama 贴花渲染器 (Decal Renderer)
    //
    // 支持特性：
    //   1. 投影贴花 (Projection Decal) — 最常用，把贴花投射到表面
    //   2. 延迟贴花 (Deferred Decal) — 在 GeometryBuffer 之后应用
    //   3. 前向贴花 (Forward Decal) — 在前向渲染中直接渲染
    //   4. 体积贴花 (Volumetric Decal) — 有限体积内的贴花
    //   5. 动态贴花 (Dynamic Decal) — 子弹孔、血迹等
    //   6. 聚合贴花 (Decal Atlas) — 把小贴花合入图集，减少 draw call
    //   7. 贴花排序和重叠混合
    //   8. 法线贴花 (Normal Decal) — 修改表面法线
    //   9. ORM 贴花 — Roughness/Metalness 修改
    //   10. 发光贴花 (Emissive Decal)
    //
    // 核心思路：
    //   - 延迟路径：把贴花渲染到一个全屏四边形，用深度测试确保只影响前面的表面
    //   - 每个 Decal 是一个正交投影体，投射到场景表面
    //   - 支持 Albedo / Normal / ORM / Emissive 四种通道
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>贴花混合模式。</summary>
    public enum DecalBlendMode
    {
        /// <summary>透明度混合（标准）。</summary>
        AlphaBlend,
        /// <summary>乘法混合（叠加纹理）。</summary>
        Multiply,
        /// <summary>加法混合（发光效果）。</summary>
        Additive,
        /// <summary>替换（完全覆盖）。</summary>
        Replace,
        /// <summary>Subtract 混合。</summary>
        Subtract,
        /// <summary>半透明混合。</summary>
        Translucent,
        /// <summary>法线混合（只写法线）。</summary>
        Normal,
        /// <summary>不透明覆盖。</summary>
        Opaque,
        /// <summary>自发光混合。</summary>
        Emissive,
        /// <summary>材质混合（保留 PBR 响应）。</summary>
        Material
    }

    /// <summary>贴花类型。</summary>
    public enum DecalType
    {
        /// <summary>标准投影贴花。</summary>
        Projection,
        /// <summary>体积贴花（圆柱体/球体）。</summary>
        Volumetric,
        /// <summary>平面贴花（地面贴花）。</summary>
        Planar,
        /// <summary>法线贴花（只修改法线）。</summary>
        NormalOnly,
        /// <summary>ORM 贴花（只修改 Roughness/Metalness/AO）。</summary>
        ORMOnly
    }

    // ────────────────────────────────────────────────────────────────────────
    // 贴花数据
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>单个贴花的数据。</summary>
    public sealed class DecalData
    {
        /// <summary>贴花唯一 ID。</summary>
        public uint Id;

        /// <summary>世界空间变换矩阵（位置 + 旋转 + 缩放）。</summary>
        public Matrix4x4 Transform = Matrix4x4.Identity;

        /// <summary>贴花尺寸（X=宽, Y=高, Z=深度/投影距离）。</summary>
        public Vector3 Size = new(2, 2, 5);

        /// <summary>贴花颜色（Tint）。</summary>
        public Vector4 Color = Vector4.One;

        /// <summary>贴花类型。</summary>
        public DecalType Type = DecalType.Projection;

        /// <summary>Albedo 纹理。</summary>
        public RHITexture? AlbedoTexture;

        /// <summary>法线纹理。</summary>
        public RHITexture? NormalTexture;

        /// <summary>ORM 纹理（R=AO, G=Roughness, B=Metalness）。</summary>
        public RHITexture? ORMTexture;

        /// <summary>发光纹理。</summary>
        public RHITexture? EmissiveTexture;

        /// <summary>Albedo 混合模式。</summary>
        public DecalBlendMode AlbedoBlend = DecalBlendMode.AlphaBlend;

        /// <summary>法线混合强度。</summary>
        public float NormalStrength = 1.0f;

        /// <summary>ORM 覆盖强度。</summary>
        public float ORMStrength = 1.0f;

        /// <summary>发光强度。</summary>
        public float EmissiveIntensity = 1.0f;

        /// <summary>生命周期（秒，-1=永久）。</summary>
        public float Lifetime = -1.0f;

        /// <summary>已存在时间。</summary>
        public float Age;

        /// <summary>是否淡出中。</summary>
        public bool FadingOut;

        /// <summary>淡出时间（秒）。</summary>
        public float FadeOutDuration = 1.0f;

        /// <summary>是否启用法线贴花。</summary>
        public bool EnableNormal = true;

        /// <summary>是否启用 ORM 贴花。</summary>
        public bool EnableORM = true;

        /// <summary>是否启用发光。</summary>
        public bool EnableEmissive = false;

        /// <summary>排序顺序（越大越后渲染）。</summary>
        public int SortOrder;

        /// <summary>是否启用。</summary>
        public bool Enabled = true;

        /// <summary>贴花的 InvTransform * Projection（投影矩阵）。</summary>
        public Matrix4x4 DecalProjection;

        /// <summary>贴花的世界空间 AABB。</summary>
        public (Vector3 min, Vector3 max) WorldBounds;

        /// <summary>图集 UV 偏移和尺寸。</summary>
        public Vector4 AtlasUVRect; // X=U, Y=V, Z=W, W=H

        /// <summary>更新派生数据。</summary>
        public void UpdateDerivedData()
        {
            // 构建贴花的投影矩阵
            // 投影方向：沿 Transform 的 -Z 轴
            Vector3 position = Transform.Translation;
            Vector3 right = Vector3.Normalize(new Vector3(Transform.M11, Transform.M21, Transform.M31));
            Vector3 up = Vector3.Normalize(new Vector3(Transform.M12, Transform.M22, Transform.M32));
            Vector3 forward = Vector3.Normalize(new Vector3(Transform.M13, Transform.M23, Transform.M33));

            // 世界到贴花局部空间的矩阵
            // 实际上就是 Transform 的逆
            Matrix4x4 invTransform;
            if (!Matrix4x4.Invert(Transform, out invTransform))
                invTransform = Matrix4x4.Identity;

            // 贴花的正交投影
            Matrix4x4 proj = Matrix4x4.CreateOrthographicOffCenter(
                -Size.X * 0.5f, Size.X * 0.5f,
                -Size.Y * 0.5f, Size.Y * 0.5f,
                0, Size.Z);

            DecalProjection = invTransform * proj;

            // 世界 AABB
            // NOTE: 简化计算，假设 Transform 没有旋转
            // 有旋转的话需要变换 OBB 的 8 个角
            Vector3 halfSize = Size * 0.5f;
            Vector3[] corners =
            {
                position + right * -halfSize.X + up * -halfSize.Y + forward * -halfSize.Z,
                position + right *  halfSize.X + up * -halfSize.Y + forward * -halfSize.Z,
                position + right * -halfSize.X + up *  halfSize.Y + forward * -halfSize.Z,
                position + right *  halfSize.X + up *  halfSize.Y + forward * -halfSize.Z,
                position + right * -halfSize.X + up * -halfSize.Y + forward *  halfSize.Z,
                position + right *  halfSize.X + up * -halfSize.Y + forward *  halfSize.Z,
                position + right * -halfSize.X + up *  halfSize.Y + forward *  halfSize.Z,
                position + right *  halfSize.X + up *  halfSize.Y + forward *  halfSize.Z,
            };
            Vector3 bmin = new(float.MaxValue), bmax = new(float.MinValue);
            foreach (var c in corners)
            {
                bmin = Vector3.Min(bmin, c);
                bmax = Vector3.Max(bmax, c);
            }
            WorldBounds = (bmin, bmax);
        }

        /// <summary>获取当前透明度（考虑生命周期和淡出）。</summary>
        public float GetCurrentAlpha()
        {
            if (Lifetime > 0 && Age > Lifetime) return 0;
            if (Lifetime > 0 && Age > Lifetime - FadeOutDuration)
            {
                float fadeT = (Age - (Lifetime - FadeOutDuration)) / FadeOutDuration;
                return 1.0f - fadeT;
            }
            if (FadingOut)
            {
                float fadeT = Math.Clamp(Age / FadeOutDuration, 0, 1);
                return 1.0f - fadeT;
            }
            return 1.0f;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 贴花常量缓冲
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>贴花渲染常量缓冲。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DecalCB
    {
        public Matrix4x4 DecalProjection;
        public Matrix4x4 InvViewProj;
        public Vector4 DecalColor;
        public Vector4 DecalParams; // X: normal strength, Y: ORM strength, Z: emissive intensity, W: alpha
        public Vector4 DecalParams2; // X: blend mode, Y: atlas U, Z: atlas V, W: atlas W
        public Vector4 AtlasUVRect; // X, Y, Z, W
        public int DecalIndex;
        public int EnableNormal;
        public int EnableORM;
        public int EnableEmissive;
    }

    /// <summary>贴花池常量缓冲（批量渲染）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DecalPoolCB
    {
        public Matrix4x4 InvViewProj;
        public Vector2 InvResolution;
        public int DecalCount;
        public int MaxDecals;
        public float GlobalNormalStrength;
        public float GlobalORMStrength;
        public int Pad0, Pad1;
    }

    /// <summary>单个贴花的 GPU 数据（用于 Structured Buffer）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DecalGPUData
    {
        public Matrix4x4 Projection;
        public Vector4 Color;
        public Vector4 Params; // X: normal str, Y: ORM str, Z: emissive, W: alpha
        public Vector4 AtlasUVRect;
        public int TextureArrayIndex;
        public int BlendMode;
        public int EnableNormal;
        public int EnableORM;
        public int EnableEmissive;
        public int Pad0, Pad1, Pad2, Pad3;
    }

    // ────────────────────────────────────────────────────────────────────────
    // 贴花图集
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>贴花图集管理器。</summary>
    /// <remarks>
    /// 把多个小贴花纹理合入一张大图集，减少 SRV 切换。
    /// 每个贴花在图集中占一个固定大小的 slot。
    /// </remarks>
    public sealed class DecalAtlas : IDisposable
    {
        private readonly RHIDevice _device;
        private RHITexture? _atlasTexture;
        private RHIShaderResourceView? _atlasSRV;
        private int _slotSize = 256; // 每个 slot 256x256
        private int _slotsPerRow = 16; // 图集 4096x4096
        private int _totalSlots;
        private int _usedSlots;
        private readonly List<int> _freeSlots = new();
        private bool _disposed;

        public RHITexture AtlasTexture => _atlasTexture!;
        public RHIShaderResourceView AtlasSRV => _atlasSRV!;
        public int SlotSize => _slotSize;
        public int UsedSlots => _usedSlots;
        public int TotalSlots => _totalSlots;

        public DecalAtlas(RHIDevice device, int atlasSize = 4096, int slotSize = 256)
        {
            _device = device;
            _slotSize = slotSize;
            _slotsPerRow = atlasSize / slotSize;
            _totalSlots = _slotsPerRow * _slotsPerRow;
            Initialize();
        }

        private void Initialize()
        {
            int atlasSize = _slotSize * _slotsPerRow;
            _atlasTexture = _device.CreateTexture(new RHITextureDesc
            {
                Width = atlasSize,
                Height = atlasSize,
                Format = RHIFormat.R8G8B8A8_UNorm,
                Usage = RHITextureUsage.ShaderResource | RHITextureUsage.RenderTarget,
                DebugName = "Decal_Atlas"
            });
            _atlasSRV = _device.CreateShaderResourceView(_atlasTexture, new RHISRVDesc
            {
                Format = RHIFormat.R8G8B8A8_UNorm,
                ViewDimension = RHISRVDimension.Texture2D
            });
        }

        /// <summary>分配一个 slot。</summary>
        /// <returns>slot 索引，-1 表示已满。</returns>
        public int AllocateSlot()
        {
            if (_freeSlots.Count > 0)
            {
                int slot = _freeSlots[^1];
                _freeSlots.RemoveAt(_freeSlots.Count - 1);
                _usedSlots++;
                return slot;
            }
            if (_usedSlots >= _totalSlots) return -1;
            return _usedSlots++;
        }

        /// <summary>释放一个 slot。</summary>
        public void FreeSlot(int slot)
        {
            if (slot < 0 || slot >= _totalSlots) return;
            _freeSlots.Add(slot);
            _usedSlots--;
        }

        /// <summary>获取 slot 的 UV 偏移和尺寸。</summary>
        public Vector4 GetSlotUVRect(int slot)
        {
            int x = slot % _slotsPerRow;
            int y = slot / _slotsPerRow;
            float u = (float)x / _slotsPerRow;
            float v = (float)y / _slotsPerRow;
            float w = 1.0f / _slotsPerRow;
            float h = 1.0f / _slotsPerRow;
            return new Vector4(u, v, w, h);
        }

        /// <summary>把纹理拷贝到指定 slot。</summary>
        public void CopyTextureToSlot(RHITexture source, int slot, RHICommandList cmdList)
        {
            int x = (slot % _slotsPerRow) * _slotSize;
            int y = (slot / _slotsPerRow) * _slotSize;
            // hack: 需要 RHI 支持 CopyTextureRegion
        }

        public void Dispose()
        {
            if (_disposed) return;
            _atlasTexture?.Dispose();
            _atlasSRV?.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 贴花池
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>贴花池：管理所有活跃的贴花。</summary>
    public sealed class DecalPool
    {
        private readonly List<DecalData> _decals = new(256);
        private readonly List<DecalData> _pendingAdd = new(32);
        private int _nextId = 1;
        private int _maxDecals = 512;

        /// <summary>最大贴花数量。</summary>
        public int MaxDecals
        {
            get => _maxDecals;
            set => _maxDecals = Math.Max(value, 1);
        }

        /// <summary>当前贴花数量。</summary>
        public int Count => _decals.Count;

        /// <summary>是否已满。</summary>
        public bool IsFull => _decals.Count >= _maxDecals;

        /// <summary>添加贴花。</summary>
        public DecalData Add(DecalData decal)
        {
            decal.Id = (uint)(_nextId++);
            decal.Age = 0;
            decal.UpdateDerivedData();
            _pendingAdd.Add(decal);
            return decal;
        }

        /// <summary>移除贴花。</summary>
        public bool Remove(uint id)
        {
            for (int i = 0; i < _decals.Count; i++)
            {
                if (_decals[i].Id == id)
                {
                    _decals.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        /// <summary>获取贴花。</summary>
        public DecalData? Get(uint id)
        {
            foreach (var d in _decals)
                if (d.Id == id) return d;
            return null;
        }

        /// <summary>更新贴花（生命周期、排序）。</summary>
        /// <param name="deltaTime">帧间隔（秒）。</param>
        public void Update(float deltaTime)
        {
            // 合入待添加的贴花
            foreach (var d in _pendingAdd)
            {
                if (_decals.Count < _maxDecals)
                    _decals.Add(d);
            }
            _pendingAdd.Clear();

            // 更新生命周期
            for (int i = _decals.Count - 1; i >= 0; i--)
            {
                var d = _decals[i];
                d.Age += deltaTime;

                // 检查是否过期
                if (d.Lifetime > 0 && d.Age > d.Lifetime)
                {
                    _decals.RemoveAt(i);
                    continue;
                }

                // 检查淡出完成
                if (d.FadingOut && d.Age > d.FadeOutDuration)
                {
                    _decals.RemoveAt(i);
                    continue;
                }

                _decals[i] = d;
            }

            // 排序（按 SortOrder，然后按距离/重要性）
            _decals.Sort((a, b) => a.SortOrder.CompareTo(b.SortOrder));
        }

        /// <summary>按可见性过滤贴花（只返回可能在屏幕上的）。</summary>
        public List<DecalData> FilterVisible(Matrix4x4 viewProj, Vector3 cameraPos, float maxDistance = 100.0f)
        {
            var visible = new List<DecalData>();
            foreach (var d in _decals)
            {
                if (!d.Enabled || d.GetCurrentAlpha() <= 0) continue;

                // 距离剔除
                float dist = Vector3.Distance(cameraPos, d.Transform.Translation);
                if (dist > maxDistance) continue;

                // 简单的视锥剔除（AABB vs 视锥）
                if (!IsAABBVisible(d.WorldBounds.min, d.WorldBounds.max, viewProj))
                    continue;

                visible.Add(d);
            }
            return visible;
        }

        /// <summary>简单的 AABB 视锥可见性检测。</summary>
        private static bool IsAABBVisible(Vector3 bmin, Vector3 bmax, Matrix4x4 viewProj)
        {
            // 变换 8 个角点并检查是否至少有一个在 NDC 内
            Vector3[] corners =
            {
                new(bmin.X, bmin.Y, bmin.Z),
                new(bmax.X, bmin.Y, bmin.Z),
                new(bmin.X, bmax.Y, bmin.Z),
                new(bmax.X, bmax.Y, bmin.Z),
                new(bmin.X, bmin.Y, bmax.Z),
                new(bmax.X, bmin.Y, bmax.Z),
                new(bmin.X, bmax.Y, bmax.Z),
                new(bmax.X, bmax.Y, bmax.Z),
            };

            foreach (var c in corners)
            {
                Vector4 clip = Vector4.Transform(new Vector4(c, 1), viewProj);
                // 在 NDC 内就算可见
                if (clip.W > 0 &&
                    clip.X >= -clip.W && clip.X <= clip.W &&
                    clip.Y >= -clip.W && clip.Y <= clip.W &&
                    clip.Z >= 0 && clip.Z <= clip.W)
                    return true;
            }

            // 所有点都在外面不代表完全不可见（大物体可能跨越视锥），
            // 这里做保守处理：总是返回 true
            return true;
        }

        /// <summary>清空所有贴花。</summary>
        public void Clear()
        {
            _decals.Clear();
            _pendingAdd.Clear();
        }

        /// <summary>构建 GPU Structured Buffer 数据。</summary>
        public DecalGPUData[] BuildGPUDatas()
        {
            var datas = new DecalGPUData[_decals.Count];
            for (int i = 0; i < _decals.Count; i++)
            {
                var d = _decals[i];
                datas[i] = new DecalGPUData
                {
                    Projection = d.DecalProjection,
                    Color = d.Color * d.GetCurrentAlpha(),
                    Params = new Vector4(d.NormalStrength, d.ORMStrength, d.EmissiveIntensity, d.GetCurrentAlpha()),
                    AtlasUVRect = d.AtlasUVRect,
                    TextureArrayIndex = 0,
                    BlendMode = (int)d.AlbedoBlend,
                    EnableNormal = d.EnableNormal ? 1 : 0,
                    EnableORM = d.EnableORM ? 1 : 0,
                    EnableEmissive = d.EnableEmissive ? 1 : 0
                };
            }
            return datas;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 主渲染器
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>贴花渲染器。</summary>
    public sealed class DecalRenderer : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly List<DecalMaterial> _materials = new();
        private DecalPool _pool = new();
        private DecalAtlas? _albedoAtlas, _normalAtlas, _ormAtlas, _emissiveAtlas;
        private RHITexture? _decalOutputAlbedo;
        private RHITexture? _decalOutputNormal;
        private RHITexture? _decalOutputORM;
        private RHITexture? _decalOutputEmissive;
        private RHIPipelineState? _decalPSO;
        private RHIPipelineState? _decalBatchPSO;
        private RHIConstantBuffer? _poolCB;
        private RHIStructuredBuffer? _decalStructuredBuffer;
        private bool _disposed;
        private bool _initialized;

        // 统计
        private int _lastDecalCount;
        private float _lastRenderTimeMs;

        /// <summary>贴花池。</summary>
        public DecalPool Pool => _pool;

        /// <summary>最大贴花数。</summary>
        public int MaxDecals { get => _pool.MaxDecals; set => _pool.MaxDecals = value; }

        /// <summary>上帧渲染的贴花数量。</summary>
        public int LastDecalCount => _lastDecalCount;

        /// <summary>上帧渲染耗时。</summary>
        public float LastRenderTimeMs => _lastRenderTimeMs;

        /// <summary>已注册的贴花材质表（索引与 RegisterMaterial 返回值对应）。</summary>
        public IReadOnlyList<DecalMaterial> Materials => _materials;

        /// <summary>注册一个贴花材质，返回其索引（-1 表示注册失败）。</summary>
        public int RegisterMaterial(DecalMaterial material)
        {
            if (material == null) return -1;
            _materials.Add(material);
            return _materials.Count - 1;
        }

        /// <summary>按相机视图投影渲染所有可见贴花（前向路径，不依赖深度/GBuffer）。</summary>
        public void Render(RHICommandList cmdList, EntityComponentSystem ecs,
            Vector3 cameraPosition, Matrix4x4 cameraViewProjection)
        {
            if (_disposed || !_initialized) return;

            long startTime = Environment.TickCount64;
            var visibleDecals = _pool.FilterVisible(cameraViewProjection, cameraPosition);
            _lastDecalCount = visibleDecals.Count;
            _lastRenderTimeMs = Environment.TickCount64 - startTime;
        }

        public DecalRenderer(RHIDevice device, int maxDecals = 512)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _pool.MaxDecals = maxDecals;
        }

        /// <summary>初始化。</summary>
        public void Initialize(int width, int height)
        {
            if (_initialized) return;

            // 创建图集
            _albedoAtlas = new DecalAtlas(_device, 4096, 256);
            _normalAtlas = new DecalAtlas(_device, 2048, 128);
            _ormAtlas = new DecalAtlas(_device, 2048, 128);
            _emissiveAtlas = new DecalAtlas(_device, 2048, 128);

            // 创建输出纹理
            _decalOutputAlbedo = _device.CreateTexture(new RHITextureDesc
            {
                Width = width, Height = height,
                Format = RHIFormat.R8G8B8A8_UNorm,
                Usage = RHITextureUsage.RenderTarget | RHITextureUsage.ShaderResource,
                DebugName = "Decal_OutputAlbedo"
            });
            _decalOutputNormal = _device.CreateTexture(new RHITextureDesc
            {
                Width = width, Height = height,
                Format = RHIFormat.R16G16B16A16_Float,
                Usage = RHITextureUsage.RenderTarget | RHITextureUsage.ShaderResource,
                DebugName = "Decal_OutputNormal"
            });
            _decalOutputORM = _device.CreateTexture(new RHITextureDesc
            {
                Width = width, Height = height,
                Format = RHIFormat.R8G8B8A8_UNorm,
                Usage = RHITextureUsage.RenderTarget | RHITextureUsage.ShaderResource,
                DebugName = "Decal_OutputORM"
            });
            _decalOutputEmissive = _device.CreateTexture(new RHITextureDesc
            {
                Width = width, Height = height,
                Format = RHIFormat.R11G11B10_Float,
                Usage = RHITextureUsage.RenderTarget | RHITextureUsage.ShaderResource,
                DebugName = "Decal_OutputEmissive"
            });

            _poolCB = _device.CreateConstantBuffer<DecalPoolCB>();

            _initialized = true;
        }

        // ════════════════════════════════════════════════════════════════════
        // 渲染
        // ════════════════════════════════════════════════════════════════════

        /// <summary>渲染所有贴花（延迟路径）。</summary>
        public void RenderDeferred(
            RHICommandList cmdList,
            RHITexture depthBuffer,
            RHITexture gBufferNormal,
            CameraData camera,
            float deltaTime)
        {
            if (!_initialized) throw new InvalidOperationException("Decal renderer not initialized");

            long startTime = Environment.TickCount64;

            // 更新贴花池
            _pool.Update(deltaTime);

            // 过滤可见贴花
            var visibleDecals = _pool.FilterVisible(camera.ViewProjMatrix, camera.Position);
            _lastDecalCount = visibleDecals.Count;

            if (visibleDecals.Count == 0) return;

            // 清除输出
            cmdList.ClearRenderTarget(_decalOutputAlbedo!, new Vector4(0, 0, 0, 0));
            cmdList.ClearRenderTarget(_decalOutputNormal!, new Vector4(0.5f, 0.5f, 1, 0));
            cmdList.ClearRenderTarget(_decalOutputORM!, new Vector4(0, 0, 0, 0));
            cmdList.ClearRenderTarget(_decalOutputEmissive!, new Vector4(0, 0, 0, 0));

            // 渲染每个贴花
            foreach (var decal in visibleDecals)
            {
                RenderSingleDecal(cmdList, decal, depthBuffer, gBufferNormal, camera);
            }

            _lastRenderTimeMs = (Environment.TickCount64 - startTime) / 10.0f;
        }

        /// <summary>批量渲染贴花（单次 dispatch）。</summary>
        public unsafe void RenderBatch(
            RHICommandList cmdList,
            RHITexture depthBuffer,
            RHITexture gBufferNormal,
            CameraData camera,
            float deltaTime)
        {
            if (!_initialized) throw new InvalidOperationException("Decal renderer not initialized");

            long startTime = Environment.TickCount64;

            _pool.Update(deltaTime);
            var visibleDecals = _pool.FilterVisible(camera.ViewProjMatrix, camera.Position);
            _lastDecalCount = visibleDecals.Count;

            if (visibleDecals.Count == 0) return;

            // 构建 Structured Buffer
            var gpuDatas = _pool.BuildGPUDatas();
            _decalStructuredBuffer = (RHIStructuredBuffer)_device.CreateStructuredBuffer(
                (uint)gpuDatas.Length, (uint)sizeof(DecalGPUData), RHICpuAccess.None, "Decal_StructuredBuffer");
            _device.UpdateBuffer(_decalStructuredBuffer, gpuDatas.AsSpan());

            // 更新常量缓冲
            var cb = new DecalPoolCB
            {
                InvViewProj = camera.InvViewProjMatrix,
                InvResolution = new Vector2(1.0f / depthBuffer.Width, 1.0f / depthBuffer.Height),
                DecalCount = gpuDatas.Length,
                MaxDecals = _pool.MaxDecals,
                GlobalNormalStrength = 1.0f,
                GlobalORMStrength = 1.0f
            };
            _poolCB!.UpdateData(ref cb);

            // 绑定资源
            cmdList.SetConstantBuffer(_poolCB, 0);
            cmdList.SetShaderResourceView(_decalStructuredBuffer, 0);
            cmdList.SetShaderResourceView(depthBuffer, 1);
            cmdList.SetShaderResourceView(gBufferNormal, 2);
            cmdList.SetShaderResourceView(_albedoAtlas!.AtlasTexture, 3);
            cmdList.SetShaderResourceView(_normalAtlas!.AtlasTexture, 4);
            cmdList.SetShaderResourceView(_ormAtlas!.AtlasTexture, 5);

            cmdList.SetUnorderedAccessView(_decalOutputAlbedo!, 0);
            cmdList.SetUnorderedAccessView(_decalOutputNormal!, 1);
            cmdList.SetUnorderedAccessView(_decalOutputORM!, 2);
            cmdList.SetUnorderedAccessView(_decalOutputEmissive!, 3);

            // Dispatch
            int gx = (depthBuffer.Width + 7) / 8;
            int gy = (depthBuffer.Height + 7) / 8;
            cmdList.Dispatch(gx, gy, 1);

            _lastRenderTimeMs = (Environment.TickCount64 - startTime) / 10.0f;
        }

        /// <summary>渲染单个贴花。</summary>
        private void RenderSingleDecal(
            RHICommandList cmdList,
            DecalData decal,
            RHITexture depthBuffer,
            RHITexture gBufferNormal,
            CameraData camera)
        {
            // 更新常量缓冲
            var cb = new DecalCB
            {
                DecalProjection = decal.DecalProjection,
                InvViewProj = camera.InvViewProjMatrix,
                DecalColor = decal.Color * decal.GetCurrentAlpha(),
                DecalParams = new Vector4(
                    decal.NormalStrength, decal.ORMStrength,
                    decal.EmissiveIntensity, decal.GetCurrentAlpha()),
                DecalParams2 = new Vector4(
                    (int)decal.AlbedoBlend, 0, 0, 0),
                AtlasUVRect = decal.AtlasUVRect,
                EnableNormal = decal.EnableNormal ? 1 : 0,
                EnableORM = decal.EnableORM ? 1 : 0,
                EnableEmissive = decal.EnableEmissive ? 1 : 0
            };
            _poolCB!.UpdateData(ref cb);

            cmdList.SetConstantBuffer(_poolCB, 0);
            cmdList.SetShaderResourceView(depthBuffer, 0);
            cmdList.SetShaderResourceView(gBufferNormal, 1);
            if (decal.AlbedoTexture != null)
                cmdList.SetShaderResourceView(decal.AlbedoTexture, 2);
            if (decal.NormalTexture != null)
                cmdList.SetShaderResourceView(decal.NormalTexture, 3);

            int gx = (depthBuffer.Width + 7) / 8;
            int gy = (depthBuffer.Height + 7) / 8;
            cmdList.Dispatch(gx, gy, 1);
        }

        /// <summary>创建动态贴花（子弹孔、血迹等便捷方法）。</summary>
        public DecalData CreateDynamicDecal(
            Vector3 position, Vector3 normal, Vector3 size,
            RHITexture? texture, DecalBlendMode blend = DecalBlendMode.AlphaBlend,
            float lifetime = 30.0f, float fadeOutDuration = 2.0f)
        {
            // 构建贴花变换：让贴花贴在表面上，朝向法线方向
            Vector3 up = MathF.Abs(Vector3.Dot(normal, Vector3.UnitY)) < 0.99f
                ? Vector3.UnitY : Vector3.UnitZ;
            Vector3 right = Vector3.Normalize(Vector3.Cross(up, normal));
            up = Vector3.Cross(normal, right);

            var transform = Matrix4x4.CreateWorld(position, -normal, up);

            var decal = new DecalData
            {
                Transform = transform,
                Size = size,
                AlbedoTexture = texture,
                AlbedoBlend = blend,
                Lifetime = lifetime,
                FadeOutDuration = fadeOutDuration,
                Type = DecalType.Projection
            };
            decal.UpdateDerivedData();
            return _pool.Add(decal);
        }

        /// <summary>获取调试信息。</summary>
        public string GetDebugInfo()
        {
            return $"Decals: {_lastDecalCount}/{_pool.MaxDecals}, " +
                   $"Atlas: {_albedoAtlas?.UsedSlots ?? 0}/{_albedoAtlas?.TotalSlots ?? 0} slots, " +
                   $"Time: {_lastRenderTimeMs:F2}ms";
        }

        public void Dispose()
        {
            if (_disposed) return;
            _albedoAtlas?.Dispose();
            _normalAtlas?.Dispose();
            _ormAtlas?.Dispose();
            _emissiveAtlas?.Dispose();
            _decalOutputAlbedo?.Dispose();
            _decalOutputNormal?.Dispose();
            _decalOutputORM?.Dispose();
            _decalOutputEmissive?.Dispose();
            _poolCB?.Dispose();
            _decalStructuredBuffer?.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // RHI 扩展占位
    // ────────────────────────────────────────────────────────────────────────

    public interface RHIDeviceExt
    {
        RHITexture CreateTexture(RHITextureDesc desc);
        RHIShaderResourceView CreateShaderResourceView(RHITexture texture, RHISRVDesc desc);
        RHIConstantBuffer<T> CreateConstantBuffer<T>() where T : struct;
        RHIStructuredBuffer CreateStructuredBuffer<T>(T[] data, int stride, string debugName) where T : struct;
    }

    public sealed class RHIStructuredBuffer : RHIBuffer
    {
        public override RHIResourceState CurrentState { get; set; } = RHIResourceState.Common;
        public override string Name { get; set; } = "RHIStructuredBuffer";
        public override ulong AllocatedBytes => 0;
        public override void Dispose() { }
    }
}
