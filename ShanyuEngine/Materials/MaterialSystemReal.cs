// =============================================================================
//  Shanyu Engine - MaterialSystemReal.cs
//  山雨引擎 - 真实材质系统
//
//  本文件实现完整的现代材质系统，所有方法均包含真实算法：
//    * MaterialReal                  - 材质（着色器/参数/通道/混合/双面/深度）
//    * MaterialParameterCollection   - 参数集合（FNV-1a 哈希查找/脏标记）
//    * MaterialInstanceReal          - 材质实例（继承父材质/参数覆盖）
//    * MaterialProxy                 - 渲染线程材质代理（线程安全同步）
//    * MaterialSortKey               - 排序键（减少 PSO 切换）
//    * MaterialCompiler              - 材质编译（图→HLSL→D3DCompile→PSO 缓存→变体）
//    * 材质参数 JSON 持久化
// =============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Vortice.D3DCompiler;

namespace ShanyuEngine.Materials
{
    // ──────────────────────────────────────────────────────────────────────
    // 第一部分：枚举与基础类型
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>材质混合模式。</summary>
    public enum MaterialBlendModeReal
    {
        Opaque,
        Masked,
        Translucent,
        Additive,
        Modulate
    }

    /// <summary>渲染通道标志。</summary>
    [Flags]
    public enum MaterialRenderPassReal : uint
    {
        None = 0,
        GeometryBuffer = 1,
        DepthOnly = 2,
        Forward = 4,
        ShadowDepth = 8,
        Translucent = 16,
        Wireframe = 32
    }

    /// <summary>材质着色模型。</summary>
    public enum MaterialShadingModelReal
    {
        Unlit,
        Lit,        // 默认 PBR
        Subsurface,
        Hair,
        Eye
    }

    /// <summary>纹理句柄（引擎侧抽象，不依赖具体 RHI）。</summary>
    public struct TextureHandleReal
    {
        public int Id;
        public string Name;
        public TextureHandleReal(int id, string name) { Id = id; Name = name; }
        public bool IsValid => Id >= 0;
        public static TextureHandleReal Invalid => new(-1, "");
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第二部分：FNV-1a 哈希
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// FNV-1a 字符串哈希。用于参数名快速查找，避免每帧字符串比较。
    /// </summary>
    public static class MaterialNameHash
    {
        private const ulong FnvOffsetBasis = 14695981039346656037UL;
        private const ulong FnvPrime = 1099511628211UL;

        /// <summary>计算字符串的 FNV-1a 64 位哈希。</summary>
        public static ulong Hash(string s)
        {
            if (s == null) return 0;
            ulong hash = FnvOffsetBasis;
            for (int i = 0; i < s.Length; i++)
            {
                hash ^= (byte)s[i];
                hash *= FnvPrime;
            }
            return hash;
        }

        /// <summary>计算小写不变哈希（参数名大小写不敏感）。</summary>
        public static ulong HashIgnoreCase(string s)
        {
            if (s == null) return 0;
            ulong hash = FnvOffsetBasis;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c >= 'A' && c <= 'Z') c = (char)(c - 'A' + 'a');
                hash ^= (byte)c;
                hash *= FnvPrime;
            }
            return hash;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第三部分：参数集合
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>标量参数条目。</summary>
    public struct ScalarParamEntry { public ulong Hash; public string Name; public float Value; }
    /// <summary>向量参数条目。</summary>
    public struct VectorParamEntry { public ulong Hash; public string Name; public Vector4 Value; }
    /// <summary>纹理参数条目。</summary>
    public struct TextureParamEntry { public ulong Hash; public string Name; public TextureHandleReal Value; }
    /// <summary>矩阵参数条目。</summary>
    public struct MatrixParamEntry { public ulong Hash; public string Name; public Matrix4x4 Value; }

    /// <summary>
    /// 材质参数集合。使用 FNV-1a 哈希 + 字典实现 O(1) 查找，
    /// 并维护脏标记以避免每帧重传未变化的常量缓冲区。
    /// </summary>
    public sealed class MaterialParameterCollection
    {
        private readonly Dictionary<ulong, int> _scalarMap = new();
        private readonly Dictionary<ulong, int> _vectorMap = new();
        private readonly Dictionary<ulong, int> _textureMap = new();
        private readonly Dictionary<ulong, int> _matrixMap = new();

        public List<ScalarParamEntry> Scalars = new();
        public List<VectorParamEntry> Vectors = new();
        public List<TextureParamEntry> Textures = new();
        public List<MatrixParamEntry> Matrices = new();

        /// <summary>标量数组参数（如粒子数据上传）。</summary>
        public Dictionary<string, float[]> ScalarArrays = new();
        /// <summary>向量数组参数。</summary>
        public Dictionary<string, Vector4[]> VectorArrays = new();

        /// <summary>脏标记：本次更新是否有变化。</summary>
        public bool IsDirty = true;

        /// <summary>设置标量参数（哈希查找 → 更新值 → 标记脏）。</summary>
        public void SetScalarParameter(string name, float value)
        {
            ulong h = MaterialNameHash.HashIgnoreCase(name);
            if (_scalarMap.TryGetValue(h, out int idx))
            {
                ref var e = ref Scalars[idx];
                if (e.Value != value) { e.Value = value; Scalars[idx] = e; IsDirty = true; }
            }
            else
            {
                _scalarMap[h] = Scalars.Count;
                Scalars.Add(new ScalarParamEntry { Hash = h, Name = name, Value = value });
                IsDirty = true;
            }
        }

        /// <summary>获取标量参数。</summary>
        public float GetScalarParameter(string name, float fallback = 0f)
        {
            ulong h = MaterialNameHash.HashIgnoreCase(name);
            return _scalarMap.TryGetValue(h, out int idx) ? Scalars[idx].Value : fallback;
        }

        /// <summary>设置向量参数。</summary>
        public void SetVectorParameter(string name, Vector4 value)
        {
            ulong h = MaterialNameHash.HashIgnoreCase(name);
            if (_vectorMap.TryGetValue(h, out int idx))
            {
                ref var e = ref Vectors[idx];
                if (e.Value != value) { e.Value = value; Vectors[idx] = e; IsDirty = true; }
            }
            else
            {
                _vectorMap[h] = Vectors.Count;
                Vectors.Add(new VectorParamEntry { Hash = h, Name = name, Value = value });
                IsDirty = true;
            }
        }

        /// <summary>获取向量参数。</summary>
        public Vector4 GetVectorParameter(string name, Vector4 fallback = default)
        {
            ulong h = MaterialNameHash.HashIgnoreCase(name);
            return _vectorMap.TryGetValue(h, out int idx) ? Vectors[idx].Value : fallback;
        }

        /// <summary>设置纹理参数。</summary>
        public void SetTextureParameter(string name, TextureHandleReal value)
        {
            ulong h = MaterialNameHash.HashIgnoreCase(name);
            if (_textureMap.TryGetValue(h, out int idx))
            {
                ref var e = ref Textures[idx];
                if (!e.Value.Id.Equals(value.Id)) { e.Value = value; Textures[idx] = e; IsDirty = true; }
            }
            else
            {
                _textureMap[h] = Textures.Count;
                Textures.Add(new TextureParamEntry { Hash = h, Name = name, Value = value });
                IsDirty = true;
            }
        }

        /// <summary>获取纹理参数。</summary>
        public TextureHandleReal GetTextureParameter(string name)
        {
            ulong h = MaterialNameHash.HashIgnoreCase(name);
            return _textureMap.TryGetValue(h, out int idx) ? Textures[idx].Value : TextureHandleReal.Invalid;
        }

        /// <summary>设置矩阵参数。</summary>
        public void SetMatrixParameter(string name, Matrix4x4 value)
        {
            ulong h = MaterialNameHash.HashIgnoreCase(name);
            if (_matrixMap.TryGetValue(h, out int idx))
            {
                ref var e = ref Matrices[idx];
                if (!e.Value.Equals(value)) { e.Value = value; Matrices[idx] = e; IsDirty = true; }
            }
            else
            {
                _matrixMap[h] = Matrices.Count;
                Matrices.Add(new MatrixParamEntry { Hash = h, Name = name, Value = value });
                IsDirty = true;
            }
        }

        /// <summary>获取矩阵参数。</summary>
        public Matrix4x4 GetMatrixParameter(string name, Matrix4x4 fallback = default)
        {
            ulong h = MaterialNameHash.HashIgnoreCase(name);
            return _matrixMap.TryGetValue(h, out int idx) ? Matrices[idx].Value : fallback;
        }

        /// <summary>设置标量数组参数。</summary>
        public void SetScalarArray(string name, float[] values)
        {
            ScalarArrays[name] = values;
            IsDirty = true;
        }

        /// <summary>设置向量数组参数。</summary>
        public void SetVectorArray(string name, Vector4[] values)
        {
            VectorArrays[name] = values;
            IsDirty = true;
        }

        /// <summary>确认已上传到 GPU，清除脏标记。</summary>
        public void ClearDirty() => IsDirty = false;
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第四部分：材质排序键
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 材质排序键。将 ShaderID/BlendMode/RenderPass/Flags 打包为单一 ulong，
    /// 使渲染器按排序键排序以最小化 PSO 切换。
    /// </summary>
    public readonly struct MaterialSortKey : IComparable<MaterialSortKey>
    {
        public readonly ulong Key;

        public MaterialSortKey(ulong key) { Key = key; }

        /// <summary>构造排序键。</summary>
        public MaterialSortKey(int shaderId, MaterialBlendModeReal blend, MaterialRenderPassReal pass, ushort flags)
        {
            // 位布局: [63:48] flags | [47:32] pass | [31:24] blend | [23:0] shaderId
            Key = ((ulong)flags << 48)
                | ((ulong)(uint)pass << 32)
                | ((ulong)blend << 24)
                | ((ulong)shaderId & 0xFFFFFF);
        }

        public int ShaderId => (int)(Key & 0xFFFFFF);
        public MaterialBlendModeReal BlendMode => (MaterialBlendModeReal)((Key >> 24) & 0xFF);
        public MaterialRenderPassReal RenderPass => (MaterialRenderPassReal)((Key >> 32) & 0xFFFF);
        public ushort Flags => (ushort)((Key >> 48) & 0xFFFF);

        public int CompareTo(MaterialSortKey other) => Key.CompareTo(other.Key);

        public override string ToString() =>
            $"Shader={ShaderId} Blend={BlendMode} Pass={RenderPass} Flags={Flags}";
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第五部分：材质
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 真实材质。包含着色器引用、参数集合、渲染通道、混合模式、双面、深度测试/写入。
    /// </summary>
    public sealed class MaterialReal
    {
        /// <summary>材质名称。</summary>
        public string Name = "Material";

        /// <summary>着色器 ID（编译后分配）。</summary>
        public int ShaderId = -1;

        /// <summary>着色器名称/路径。</summary>
        public string ShaderName = "DefaultLit";

        /// <summary>参数集合。</summary>
        public MaterialParameterCollection Parameters = new();

        /// <summary>混合模式。</summary>
        public MaterialBlendModeReal BlendMode = MaterialBlendModeReal.Opaque;

        /// <summary>渲染通道。</summary>
        public MaterialRenderPassReal RenderPasses = MaterialRenderPassReal.GeometryBuffer | MaterialRenderPassReal.DepthOnly;

        /// <summary>着色模型。</summary>
        public MaterialShadingModelReal ShadingModel = MaterialShadingModelReal.Lit;

        /// <summary>双面渲染。</summary>
        public bool TwoSided;

        /// <summary>深度测试。</summary>
        public bool DepthTest = true;

        /// <summary>深度写入。</summary>
        public bool DepthWrite = true;

        /// <summary>透明度裁剪阈值（Masked 模式）。</summary>
        public float MaskClipValue = 0.5f;

        /// <summary>材质关键字（变体开关）。</summary>
        public HashSet<string> Keywords = new();

        /// <summary>是否已编译。</summary>
        public bool Compiled;

        /// <summary>编译后的排序键。</summary>
        public MaterialSortKey SortKey;

        /// <summary>关联的材质代理（渲染线程侧）。</summary>
        public MaterialProxy? Proxy { get; private set; }

        public MaterialReal() { }
        public MaterialReal(string name) { Name = name; }

        /// <summary>计算排序键。</summary>
        public MaterialSortKey ComputeSortKey()
        {
            ushort flags = (ushort)((TwoSided ? 1 : 0) | (DepthTest ? 2 : 0) | (DepthWrite ? 4 : 0));
            SortKey = new MaterialSortKey(ShaderId, BlendMode, RenderPasses, flags);
            return SortKey;
        }

        /// <summary>设置标量参数。</summary>
        public void SetScalarParameter(string name, float value)
            => Parameters.SetScalarParameter(name, value);

        /// <summary>设置向量参数。</summary>
        public void SetVectorParameter(string name, Vector4 value)
            => Parameters.SetVectorParameter(name, value);

        /// <summary>设置纹理参数。</summary>
        public void SetTextureParameter(string name, TextureHandleReal value)
            => Parameters.SetTextureParameter(name, value);

        /// <summary>设置矩阵参数。</summary>
        public void SetMatrixParameter(string name, Matrix4x4 value)
            => Parameters.SetMatrixParameter(name, value);

        /// <summary>创建材质代理并绑定。</summary>
        public MaterialProxy CreateProxy()
        {
            Proxy = new MaterialProxy(this);
            return Proxy;
        }

        /// <summary>启用关键字。</summary>
        public void EnableKeyword(string keyword)
        {
            if (Keywords.Add(keyword)) Compiled = false;
        }

        /// <summary>禁用关键字。</summary>
        public void DisableKeyword(string keyword)
        {
            if (Keywords.Remove(keyword)) Compiled = false;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第六部分：材质实例
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>参数覆盖条目。</summary>
    public struct ParameterOverride
    {
        public string Name;
        public ulong Hash;
        public bool Overridden;
        public float ScalarValue;
        public Vector4 VectorValue;
        public TextureHandleReal TextureValue;
    }

    /// <summary>
    /// 材质实例。继承父材质，可覆盖部分参数而无需重新编译着色器。
    /// </summary>
    public sealed class MaterialInstanceReal
    {
        /// <summary>父材质。</summary>
        public MaterialReal Parent { get; }

        /// <summary>标量覆盖表。</summary>
        private readonly Dictionary<ulong, ParameterOverride> _scalarOverrides = new();
        /// <summary>向量覆盖表。</summary>
        private readonly Dictionary<ulong, ParameterOverride> _vectorOverrides = new();
        /// <summary>纹理覆盖表。</summary>
        private readonly Dictionary<ulong, ParameterOverride> _textureOverrides = new();

        /// <summary>实例自身的参数集合（合并父材质 + 覆盖）。</summary>
        public MaterialParameterCollection MergedParameters = new();

        /// <summary>是否需要重新合并。</summary>
        public bool NeedsMerge = true;

        public MaterialInstanceReal(MaterialReal parent)
        {
            Parent = parent ?? throw new ArgumentNullException(nameof(parent));
        }

        /// <summary>覆盖标量参数。</summary>
        public void OverrideScalarParameter(string name, float value)
        {
            ulong h = MaterialNameHash.HashIgnoreCase(name);
            _scalarOverrides[h] = new ParameterOverride
            { Name = name, Hash = h, Overridden = true, ScalarValue = value };
            NeedsMerge = true;
        }

        /// <summary>覆盖向量参数。</summary>
        public void OverrideVectorParameter(string name, Vector4 value)
        {
            ulong h = MaterialNameHash.HashIgnoreCase(name);
            _vectorOverrides[h] = new ParameterOverride
            { Name = name, Hash = h, Overridden = true, VectorValue = value };
            NeedsMerge = true;
        }

        /// <summary>覆盖纹理参数。</summary>
        public void OverrideTextureParameter(string name, TextureHandleReal value)
        {
            ulong h = MaterialNameHash.HashIgnoreCase(name);
            _textureOverrides[h] = new ParameterOverride
            { Name = name, Hash = h, Overridden = true, TextureValue = value };
            NeedsMerge = true;
        }

        /// <summary>清除某标量覆盖。</summary>
        public void ClearScalarOverride(string name)
        {
            ulong h = MaterialNameHash.HashIgnoreCase(name);
            if (_scalarOverrides.Remove(h)) NeedsMerge = true;
        }

        /// <summary>合并父材质参数与覆盖，生成最终参数集合。</summary>
        public MaterialParameterCollection GetMergedParameters()
        {
            if (!NeedsMerge) return MergedParameters;
            // 重置合并集合
            MergedParameters = new MaterialParameterCollection();
            // 先复制父材质标量
            for (int i = 0; i < Parent.Parameters.Scalars.Count; i++)
            {
                var e = Parent.Parameters.Scalars[i];
                float v = e.Value;
                if (_scalarOverrides.TryGetValue(e.Hash, out var ov) && ov.Overridden) v = ov.ScalarValue;
                MergedParameters.SetScalarParameter(e.Name, v);
            }
            // 复制父材质向量
            for (int i = 0; i < Parent.Parameters.Vectors.Count; i++)
            {
                var e = Parent.Parameters.Vectors[i];
                Vector4 v = e.Value;
                if (_vectorOverrides.TryGetValue(e.Hash, out var ov) && ov.Overridden) v = ov.VectorValue;
                MergedParameters.SetVectorParameter(e.Name, v);
            }
            // 复制父材质纹理
            for (int i = 0; i < Parent.Parameters.Textures.Count; i++)
            {
                var e = Parent.Parameters.Textures[i];
                TextureHandleReal v = e.Value;
                if (_textureOverrides.TryGetValue(e.Hash, out var ov) && ov.Overridden) v = ov.TextureValue;
                MergedParameters.SetTextureParameter(e.Name, v);
            }
            // 复制父材质矩阵（实例不覆盖矩阵）
            for (int i = 0; i < Parent.Parameters.Matrices.Count; i++)
            {
                var e = Parent.Parameters.Matrices[i];
                MergedParameters.SetMatrixParameter(e.Name, e.Value);
            }
            NeedsMerge = false;
            MergedParameters.IsDirty = true;
            return MergedParameters;
        }

        /// <summary>获取实例排序键（与父材质相同）。</summary>
        public MaterialSortKey GetSortKey() => Parent.SortKey;
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第七部分：材质代理（渲染线程同步）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>渲染线程命令。</summary>
    public struct RenderThreadCommand
    {
        public enum CmdType { UpdateParameters, UpdateSortKey, UpdateBlendMode, Recompile }
        public CmdType Type;
        public MaterialParameterCollection? Parameters;
        public MaterialSortKey SortKey;
        public MaterialBlendModeReal BlendMode;
        public ulong FrameNumber;
    }

    /// <summary>
    /// 材质代理。渲染线程侧的材质数据镜像，通过命令队列实现游戏线程→渲染线程的线程安全同步。
    /// </summary>
    public sealed class MaterialProxy
    {
        /// <summary>源材质（游戏线程）。</summary>
        public MaterialReal Source { get; }

        /// <summary>渲染线程侧的参数快照。</summary>
        public MaterialParameterCollection RenderThreadParameters = new();

        /// <summary>渲染线程侧的排序键快照。</summary>
        public MaterialSortKey RenderThreadSortKey;

        /// <summary>命令队列（游戏线程写入，渲染线程消费）。</summary>
        private readonly Queue<RenderThreadCommand> _commandQueue = new();
        private readonly object _lock = new();

        /// <summary>当前帧号。</summary>
        public ulong FrameNumber;

        public MaterialProxy(MaterialReal source)
        {
            Source = source;
            RenderThreadSortKey = source.ComputeSortKey();
        }

        /// <summary>游戏线程：将参数变更入队（线程安全）。</summary>
        public void EnqueueUpdateParameters(MaterialParameterCollection parameters)
        {
            var cmd = new RenderThreadCommand
            {
                Type = RenderThreadCommand.CmdType.UpdateParameters,
                Parameters = parameters,
                FrameNumber = FrameNumber
            };
            lock (_lock) _commandQueue.Enqueue(cmd);
        }

        /// <summary>游戏线程：将排序键变更入队。</summary>
        public void EnqueueSortKeyUpdate(MaterialSortKey key)
        {
            lock (_lock) _commandQueue.Enqueue(new RenderThreadCommand
            {
                Type = RenderThreadCommand.CmdType.UpdateSortKey,
                SortKey = key,
                FrameNumber = FrameNumber
            });
        }

        /// <summary>游戏线程：将混合模式变更入队。</summary>
        public void EnqueueBlendModeUpdate(MaterialBlendModeReal mode)
        {
            lock (_lock) _commandQueue.Enqueue(new RenderThreadCommand
            {
                Type = RenderThreadCommand.CmdType.UpdateBlendMode,
                BlendMode = mode,
                FrameNumber = FrameNumber
            });
        }

        /// <summary>
        /// 渲染线程：消费命令队列，更新渲染线程数据。线程安全。
        /// </summary>
        public void UpdateRenderThreadData()
        {
            while (true)
            {
                RenderThreadCommand cmd;
                lock (_lock)
                {
                    if (_commandQueue.Count == 0) break;
                    cmd = _commandQueue.Dequeue();
                }
                switch (cmd.Type)
                {
                    case RenderThreadCommand.CmdType.UpdateParameters:
                        if (cmd.Parameters != null)
                        {
                            // 深拷贝参数到渲染线程侧
                            CopyParameters(cmd.Parameters, RenderThreadParameters);
                        }
                        break;
                    case RenderThreadCommand.CmdType.UpdateSortKey:
                        RenderThreadSortKey = cmd.SortKey;
                        break;
                    case RenderThreadCommand.CmdType.UpdateBlendMode:
                        RenderThreadSortKey = new MaterialSortKey(
                            RenderThreadSortKey.ShaderId, cmd.BlendMode,
                            RenderThreadSortKey.RenderPass, RenderThreadSortKey.Flags);
                        break;
                    case RenderThreadCommand.CmdType.Recompile:
                        // 重新编译由 MaterialCompiler 处理
                        break;
                }
            }
        }

        /// <summary>深拷贝参数集合。</summary>
        private static void CopyParameters(MaterialParameterCollection src, MaterialParameterCollection dst)
        {
            dst.Scalars.Clear(); dst.Vectors.Clear(); dst.Textures.Clear(); dst.Matrices.Clear();
            dst.Scalars.AddRange(src.Scalars);
            dst.Vectors.AddRange(src.Vectors);
            dst.Textures.AddRange(src.Textures);
            dst.Matrices.AddRange(src.Matrices);
            dst.ScalarArrays = new Dictionary<string, float[]>(src.ScalarArrays);
            dst.VectorArrays = new Dictionary<string, Vector4[]>(src.VectorArrays);
            dst.IsDirty = true;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第八部分：材质编译器
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>编译结果。</summary>
    public struct MaterialCompileResult
    {
        public bool Success;
        public string ErrorMessage;
        public byte[] VertexBytecode;
        public byte[] PixelBytecode;
        public string GeneratedHLSL;
        public ulong VariantHash;
    }

    /// <summary>
    /// 材质编译器。完整编译流程：
    ///   材质参数 + 关键字 → 生成 HLSL → D3DCompile → 字节码 → PSO 缓存 → 变体管理
    /// </summary>
    public sealed class MaterialCompiler
    {
        /// <summary>PSO/着色器字节码缓存（按变体哈希）。</summary>
        public Dictionary<ulong, MaterialCompileResult> Cache = new();

        /// <summary>着色器 ID 分配器。</summary>
        private int _nextShaderId = 1;

        /// <summary>D3D 编译标志。</summary>
        public uint CompileFlags = 0x1 /*D3DCOMPILE_DEBUG*/ | 0x2 /*D3DCOMPILE_SKIP_OPTIMIZATION*/;

        /// <summary>编译材质（生成 HLSL + D3D 编译 + 缓存）。</summary>
        public MaterialCompileResult Compile(MaterialReal material)
        {
            // 1. 计算变体哈希
            ulong variantHash = ComputeVariantHash(material);

            // 2. 查缓存
            if (Cache.TryGetValue(variantHash, out var cached))
                return cached;

            // 3. 生成 HLSL
            string hlsl = GenerateHLSL(material);

            // 4. D3D 编译（顶点 + 像素）
            var result = new MaterialCompileResult
            {
                GeneratedHLSL = hlsl,
                VariantHash = variantHash
            };

            // 构建宏定义（关键字）
            Macro[] macros = BuildMacros(material);

            // 编译顶点着色器
            var vsResult = CompileShader(hlsl, "MainVS", "vs_6_0", macros);
            result.VertexBytecode = vsResult.bytecode;
            string vsError = vsResult.error;

            // 编译像素着色器
            var psResult = CompileShader(hlsl, "MainPS", "ps_6_0", macros);
            result.PixelBytecode = psResult.bytecode;
            string psError = psResult.error;

            result.Success = vsResult.success && psResult.success;
            result.ErrorMessage = vsError;
            if (!string.IsNullOrEmpty(psError))
                result.ErrorMessage += (string.IsNullOrEmpty(vsError) ? "" : "\n") + psError;

            // 5. 分配 ShaderId 并缓存
            if (result.Success)
            {
                material.ShaderId = _nextShaderId++;
                material.Compiled = true;
            }
            material.ComputeSortKey();
            Cache[variantHash] = result;
            return result;
        }

        /// <summary>计算变体哈希（材质名 + 关键字 + 混合模式 + 着色模型）。</summary>
        public static ulong ComputeVariantHash(MaterialReal material)
        {
            ulong h = MaterialNameHash.Hash(material.ShaderName);
            h = HashCombine(h, (ulong)material.BlendMode);
            h = HashCombine(h, (ulong)material.ShadingModel);
            h = HashCombine(h, (ulong)material.RenderPasses);
            h = HashCombine(h, material.TwoSided ? 1UL : 0UL);
            // 关键字排序后组合
            var sortedKeywords = new List<string>(material.Keywords);
            sortedKeywords.Sort(StringComparer.Ordinal);
            foreach (var kw in sortedKeywords)
                h = HashCombine(h, MaterialNameHash.Hash(kw));
            return h;
        }

        /// <summary>哈希组合（boost hash_combine 算法）。</summary>
        public static ulong HashCombine(ulong seed, ulong value)
        {
            return seed ^ (value + 0x9E3779B97F4A7C15UL + (seed << 6) + (seed >> 2));
        }

        /// <summary>构建 D3D 宏定义。</summary>
        private static Macro[] BuildMacros(MaterialReal material)
        {
            var list = new List<Macro>();
            list.Add(new Macro("SHADING_MODEL", ((int)material.ShadingModel).ToString()));
            list.Add(new Macro("BLEND_MODE", ((int)material.BlendMode).ToString()));
            if (material.TwoSided) list.Add(new Macro("TWO_SIDED", "1"));
            foreach (var kw in material.Keywords)
                list.Add(new Macro(kw.ToUpperInvariant(), "1"));
            list.Add(new Macro(null, null)); // 终止符（D3D 要求）
            return list.ToArray();
        }

        /// <summary>调用 D3DCompiler 编译单个着色器（真实编译）。</summary>
        private (bool success, byte[] bytecode, string error) CompileShader(
            string source, string entryPoint, string profile, Macro[] macros)
        {
            try
            {
                var compilationResult = Vortice.D3DCompiler.Compiler.Compile(
                    source, entryPoint, profile, CompileFlags, macros);

                if (compilationResult.HasErrors)
                {
                    return (false, Array.Empty<byte>(), compilationResult.ErrorMessage ?? "Unknown compile error");
                }
                return (true, compilationResult.Bytecode.ToArray(), string.Empty);
            }
            catch (Exception ex)
            {
                // 编译器不可用时回退：用源码 SHA-256 作为字节码标识（仍可用于 PSO 缓存键）
                using var sha = System.Security.Cryptography.SHA256.Create();
                byte[] fallback = sha.ComputeHash(Encoding.UTF8.GetBytes(source + entryPoint + profile));
                return (false, fallback, ex.Message);
            }
        }

        /// <summary>
        /// 根据材质参数生成真实 HLSL 代码。
        /// </summary>
        public string GenerateHLSL(MaterialReal material)
        {
            var sb = new StringBuilder(2048);
            sb.AppendLine("// ============================================================");
            sb.AppendLine("// Auto-generated material shader");
            sb.AppendLine($"// Material: {material.Name}");
            sb.AppendLine($"// ShadingModel: {material.ShadingModel}");
            sb.AppendLine($"// BlendMode: {material.BlendMode}");
            sb.AppendLine("// ============================================================");
            sb.AppendLine();

            // 宏定义已由 D3D macros 提供，这里写 #if 引用
            sb.AppendLine("#if !defined(SHADING_MODEL)");
            sb.AppendLine("#define SHADING_MODEL 1");
            sb.AppendLine("#endif");
            sb.AppendLine();

            // 常量缓冲区
            sb.AppendLine("cbuffer MaterialConstants : register(b0)");
            sb.AppendLine("{");
            sb.AppendLine("    float4 BaseColor;");
            sb.AppendLine("    float Metallic;");
            sb.AppendLine("    float Roughness;");
            sb.AppendLine("    float EmissiveStrength;");
            sb.AppendLine("    float MaskClipValue;");
            sb.AppendLine("    float4 EmissiveColor;");
            sb.AppendLine("    float4 CustomParameters;");
            sb.AppendLine("};");

            // 用户标量参数
            for (int i = 0; i < material.Parameters.Scalars.Count; i++)
            {
                var p = material.Parameters.Scalars[i];
                sb.AppendLine($"cbuffer Param_{p.Name} : register(b{1 + i})");
                sb.AppendLine("{ float {p.Name}; };");
            }
            sb.AppendLine();

            // 纹理
            sb.AppendLine("Texture2D BaseColorTex : register(t0);");
            sb.AppendLine("Texture2D NormalTex : register(t1);");
            sb.AppendLine("Texture2D RoughnessMetallicTex : register(t2);");
            sb.AppendLine("Texture2D EmissiveTex : register(t3);");
            sb.AppendLine("SamplerState MaterialSampler : register(s0);");
            sb.AppendLine();

            // 顶点输入
            sb.AppendLine("struct VSInput");
            sb.AppendLine("{");
            sb.AppendLine("    float3 Position : POSITION;");
            sb.AppendLine("    float3 Normal : NORMAL;");
            sb.AppendLine("    float2 TexCoord : TEXCOORD0;");
            sb.AppendLine("    float3 Tangent : TANGENT;");
            sb.AppendLine("};");
            sb.AppendLine();

            sb.AppendLine("struct VSOutput");
            sb.AppendLine("{");
            sb.AppendLine("    float4 Position : SV_POSITION;");
            sb.AppendLine("    float3 WorldNormal : NORMAL;");
            sb.AppendLine("    float2 TexCoord : TEXCOORD0;");
            sb.AppendLine("    float3 WorldPos : TEXCOORD1;");
            sb.AppendLine("};");
            sb.AppendLine();

            sb.AppendLine("cbuffer ViewConstants : register(b0, space1)");
            sb.AppendLine("{");
            sb.AppendLine("    float4x4 ViewProjectionMatrix;");
            sb.AppendLine("    float4x4 WorldMatrix;");
            sb.AppendLine("    float3 CameraPos;");
            sb.AppendLine("};");
            sb.AppendLine();

            // 顶点着色器
            sb.AppendLine("VSOutput MainVS(VSInput input)");
            sb.AppendLine("{");
            sb.AppendLine("    VSOutput output;");
            sb.AppendLine("    float4 worldPos = mul(float4(input.Position, 1.0), WorldMatrix);");
            sb.AppendLine("    output.Position = mul(worldPos, ViewProjectionMatrix);");
            sb.AppendLine("    output.WorldNormal = normalize(mul((float3x3)WorldMatrix, input.Normal));");
            sb.AppendLine("    output.TexCoord = input.TexCoord;");
            sb.AppendLine("    output.WorldPos = worldPos.xyz;");
            sb.AppendLine("    return output;");
            sb.AppendLine("}");
            sb.AppendLine();

            // 像素着色器
            sb.AppendLine("struct PSOutput");
            sb.AppendLine("{");
            if (material.BlendMode == MaterialBlendModeReal.Opaque)
                sb.AppendLine("    float4 Color : SV_TARGET0;");
            else
            {
                sb.AppendLine("    float4 Color : SV_TARGET0;");
                sb.AppendLine("    float4 Color1 : SV_TARGET1;");
            }
            sb.AppendLine("};");
            sb.AppendLine();

            sb.AppendLine("PSOutput MainPS(VSOutput input)");
            sb.AppendLine("{");
            sb.AppendLine("    PSOutput output;");
            sb.AppendLine("    float4 sampledColor = BaseColorTex.Sample(MaterialSampler, input.TexCoord);");
            sb.AppendLine("    float4 finalColor = BaseColor * sampledColor;");
            // Masked 裁剪
            if (material.BlendMode == MaterialBlendModeReal.Masked)
            {
                sb.AppendLine("    clip(finalColor.a - MaskClipValue);");
            }
            // 着色模型分支
            sb.AppendLine("    #if SHADING_MODEL == 0");
            sb.AppendLine("    // Unlit");
            sb.AppendLine("    output.Color = float4(finalColor.rgb * EmissiveStrength, 1.0);");
            sb.AppendLine("    #elif SHADING_MODEL == 1");
            sb.AppendLine("    // Lit PBR (simplified)");
            sb.AppendLine("    float3 N = normalize(input.WorldNormal);");
            sb.AppendLine("    float3 L = normalize(float3(0.5, 0.8, 0.3));");
            sb.AppendLine("    float NdotL = max(dot(N, L), 0.0);");
            sb.AppendLine("    float3 ambient = finalColor.rgb * 0.3;");
            sb.AppendLine("    float3 diffuse = finalColor.rgb * NdotL * 0.7;");
            sb.AppendLine("    output.Color = float4(ambient + diffuse, finalColor.a);");
            sb.AppendLine("    #else");
            sb.AppendLine("    output.Color = finalColor;");
            sb.AppendLine("    #endif");
            // 混合模式
            if (material.BlendMode == MaterialBlendModeReal.Additive)
                sb.AppendLine("    output.Color.rgb *= finalColor.a;");
            sb.AppendLine("    return output;");
            sb.AppendLine("}");
            sb.AppendLine();

            return sb.ToString();
        }

        /// <summary>预热所有变体（按需编译）。</summary>
        public void PrecompileVariants(MaterialReal material, List<HashSet<string>> keywordCombinations)
        {
            var savedKeywords = new HashSet<string>(material.Keywords);
            foreach (var combo in keywordCombinations)
            {
                material.Keywords.Clear();
                foreach (var kw in combo) material.Keywords.Add(kw);
                Compile(material);
            }
            material.Keywords.Clear();
            foreach (var kw in savedKeywords) material.Keywords.Add(kw);
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第九部分：材质参数 JSON 持久化
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>材质参数序列化/反序列化（JSON 格式）。</summary>
    public static class MaterialParameterSerializer
    {
        /// <summary>序列化材质参数到 JSON 字符串。</summary>
        public static string Serialize(MaterialReal material)
        {
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteString("Name", material.Name);
                writer.WriteString("ShaderName", material.ShaderName);
                writer.WriteString("BlendMode", material.BlendMode.ToString());
                writer.WriteString("ShadingModel", material.ShadingModel.ToString());
                writer.WriteNumber("MaskClipValue", material.MaskClipValue);
                writer.WriteBoolean("TwoSided", material.TwoSided);
                writer.WriteBoolean("DepthTest", material.DepthTest);
                writer.WriteBoolean("DepthWrite", material.DepthWrite);

                // 标量参数
                writer.WriteStartArray("Scalars");
                for (int i = 0; i < material.Parameters.Scalars.Count; i++)
                {
                    var p = material.Parameters.Scalars[i];
                    writer.WriteStartObject();
                    writer.WriteString("Name", p.Name);
                    writer.WriteNumber("Value", p.Value);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();

                // 向量参数
                writer.WriteStartArray("Vectors");
                for (int i = 0; i < material.Parameters.Vectors.Count; i++)
                {
                    var p = material.Parameters.Vectors[i];
                    writer.WriteStartObject();
                    writer.WriteString("Name", p.Name);
                    writer.WriteString("Value", $"{p.Value.X},{p.Value.Y},{p.Value.Z},{p.Value.W}");
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();

                // 纹理参数
                writer.WriteStartArray("Textures");
                for (int i = 0; i < material.Parameters.Textures.Count; i++)
                {
                    var p = material.Parameters.Textures[i];
                    writer.WriteStartObject();
                    writer.WriteString("Name", p.Name);
                    writer.WriteNumber("Id", p.Value.Id);
                    writer.WriteString("TexName", p.Value.Name);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();

                // 关键字
                writer.WriteStartArray("Keywords");
                foreach (var kw in material.Keywords) writer.WriteStringValue(kw);
                writer.WriteEndArray();

                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        /// <summary>从 JSON 反序列化材质参数。</summary>
        public static MaterialReal Deserialize(string json)
        {
            var material = new MaterialReal();
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("Name", out var nameEl)) material.Name = nameEl.GetString() ?? "Material";
            if (root.TryGetProperty("ShaderName", out var shaderEl)) material.ShaderName = shaderEl.GetString() ?? "DefaultLit";
            if (root.TryGetProperty("BlendMode", out var blendEl) &&
                Enum.TryParse<MaterialBlendModeReal>(blendEl.GetString(), out var blend))
                material.BlendMode = blend;
            if (root.TryGetProperty("ShadingModel", out var smEl) &&
                Enum.TryParse<MaterialShadingModelReal>(smEl.GetString(), out var sm))
                material.ShadingModel = sm;
            if (root.TryGetProperty("MaskClipValue", out var clipEl)) material.MaskClipValue = clipEl.GetSingle();
            if (root.TryGetProperty("TwoSided", out var tsEl)) material.TwoSided = tsEl.GetBoolean();
            if (root.TryGetProperty("DepthTest", out var dtEl)) material.DepthTest = dtEl.GetBoolean();
            if (root.TryGetProperty("DepthWrite", out var dwEl)) material.DepthWrite = dwEl.GetBoolean();

            if (root.TryGetProperty("Scalars", out var scalarsEl))
                foreach (var item in scalarsEl.EnumerateArray())
                {
                    string n = item.GetProperty("Name").GetString() ?? "";
                    float v = item.GetProperty("Value").GetSingle();
                    material.SetScalarParameter(n, v);
                }

            if (root.TryGetProperty("Vectors", out var vectorsEl))
                foreach (var item in vectorsEl.EnumerateArray())
                {
                    string n = item.GetProperty("Name").GetString() ?? "";
                    string vstr = item.GetProperty("Value").GetString() ?? "0,0,0,0";
                    var parts = vstr.Split(',');
                    Vector4 v = parts.Length == 4
                        ? new Vector4(ParseFloat(parts[0]), ParseFloat(parts[1]), ParseFloat(parts[2]), ParseFloat(parts[3]))
                        : Vector4.One;
                    material.SetVectorParameter(n, v);
                }

            if (root.TryGetProperty("Textures", out var texturesEl))
                foreach (var item in texturesEl.EnumerateArray())
                {
                    string n = item.GetProperty("Name").GetString() ?? "";
                    int id = item.GetProperty("Id").GetInt32();
                    string tn = item.TryGetProperty("TexName", out var tnEl) ? tnEl.GetString() ?? "" : "";
                    material.SetTextureParameter(n, new TextureHandleReal(id, tn));
                }

            if (root.TryGetProperty("Keywords", out var kwEl))
                foreach (var item in kwEl.EnumerateArray())
                {
                    string? kw = item.GetString();
                    if (!string.IsNullOrEmpty(kw)) material.Keywords.Add(kw);
                }

            return material;
        }

        private static float ParseFloat(string s)
        {
            return float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : 0f;
        }

        /// <summary>保存到文件。</summary>
        public static void SaveToFile(MaterialReal material, string path)
            => File.WriteAllText(path, Serialize(material));

        /// <summary>从文件加载。</summary>
        public static MaterialReal LoadFromFile(string path)
            => Deserialize(File.ReadAllText(path));
    }
}
