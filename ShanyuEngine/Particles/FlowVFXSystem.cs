using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using ShanyuEngine.RHI;

namespace ShanyuEngine.Particles
{
    // ════════════════════════════════════════════════════════════════════
    // 山雨引擎 FlowVFX 级粒子系统 — 运行时与扩展层
    // --------------------------------------------------------------------
    // 本文件 complement 已有的 FlowVFXParticleSystem.cs，补充以下核心能力：
    //   - FlowVFXParameterStore：分层参数存储与绑定（系统→发射器→模块）
    //   - FlowVFXAttributeInterface：粒子属性数据接口（位置/速度/颜色/生命周期/大小）
    //   - FlowVFXEmitterMode：发射器模式（连续/脉冲/单次）
    //   - FlowVFXEventDispatcher：增强事件系统（碰撞/死亡/生成子粒子）
    //   - FlowVFXSubEmitterHandler：子发射器（死亡时生成/碰撞时生成）
    //   - FlowVFXEmitterLifecycle：生命周期管理（延迟启动/循环/持续时间）
    //   - FlowVFXCPUSimulator：CPU 模拟路径（调试 fallback）
    //   - FlowVFXGPUComputeDispatcher：GPU Compute 调度路径
    //   - FlowVFXSystemRuntime：运行时控制器（CPU/GPU 双路径切换）
    //
    // 与已有文件的关系：
    //   - FlowVFXParticleSystem.cs 定义了 FlowVFXEmitter / FlowVFXModule /
    //     FlowVFXDataInterface / FlowVFXSystem / FlowVFXInstance 等基础类型
    //   - 本文件扩展这些类型，提供更完整的运行时管线
    // ════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────────
    // 第一部分：发射器模式（连续/脉冲/单次）
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// FlowVFX 发射器运行模式。决定发射器如何在整个生命周期内生成粒子。
    /// 与已有的 <see cref="FlowVFXEmissionMode"/>（描述发射形状语义）不同，
    /// 此枚举描述发射器的时间行为模式。
    /// </summary>
    public enum FlowVFXEmitterMode
    {
        /// <summary>连续发射：按固定速率持续生成粒子，直到持续时间结束或被停止。</summary>
        Continuous,
        /// <summary>脉冲发射：按固定间隔一次性爆发指定数量粒子。</summary>
        Burst,
        /// <summary>单次发射：仅在启动时发射一批粒子，之后不再发射（适合爆炸/冲击波）。</summary>
        OneShot
    }

    // ──────────────────────────────────────────────────────────────────
    // 第二部分：参数存储与绑定系统
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 参数绑定描述。将一个命名参数绑定到特定层级（系统/发射器/模块）和寄存器。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FlowVFXParameterBinding
    {
        /// <summary>参数名。</summary>
        public string Name;
        /// <summary>参数类型。</summary>
        public FlowVFXParameterType Type;
        /// <summary>绑定层级：0=系统级, 1=发射器级, 2=模块级。</summary>
        public int HierarchyLevel;
        /// <summary>所属模块名（模块级参数用）。</summary>
        public string? ModuleName;
        /// <summary>HLSL 常量缓冲寄存器（b0/b1/...）。</summary>
        public uint ConstantBufferRegister;
        /// <summary>在常量缓冲中的字节偏移。</summary>
        public uint ByteOffset;
        /// <summary>数据字节大小。</summary>
        public uint ByteSize;
        /// <summary>是否已脏（需要重新上传 GPU）。</summary>
        public bool IsDirty;
    }

    /// <summary>
    /// FlowVFX 参数存储与绑定系统。提供分层参数管理（系统→发射器→模块），
    /// 支持 GPU 常量缓冲自动布局、热更新、参数覆盖。
    /// </summary>
    public class FlowVFXParameterStore : IDisposable
    {
        // ── 分层参数存储 ──
        private readonly Dictionary<string, FlowVFXParameter> _systemParams = new();
        private readonly Dictionary<string, Dictionary<string, FlowVFXParameter>> _emitterParams = new();
        private readonly Dictionary<string, Dictionary<string, FlowVFXParameter>> _moduleParams = new();

        // ── 绑定表 ──
        private readonly List<FlowVFXParameterBinding> _bindings = new();
        private readonly Dictionary<string, int> _bindingIndex = new();

        // ── GPU 常量缓冲布局 ──
        private readonly Dictionary<uint, uint> _cbSizes = new();       // register → size
        private readonly Dictionary<uint, RHIBuffer?> _cbBuffers = new(); // register → GPU buffer
        private RHIDevice? _device;
        private bool _disposed;

        /// <summary>系统级参数数量。</summary>
        public int SystemParameterCount => _systemParams.Count;
        /// <summary>总绑定数。</summary>
        public int BindingCount => _bindings.Count;
        /// <summary>是否存在脏参数（需重新上传）。</summary>
        public bool HasDirtyBindings
        {
            get
            {
                for (int i = 0; i < _bindings.Count; i++)
                    if (_bindings[i].IsDirty) return true;
                return false;
            }
        }

        // ──────────────────────────────────────────────────────────
        // 参数注册
        // ──────────────────────────────────────────────────────────

        /// <summary>注册系统级参数。</summary>
        public FlowVFXParameter RegisterSystemParameter(string name, FlowVFXParameterType type)
        {
            var p = new FlowVFXParameter { Name = name, Type = type };
            _systemParams[name] = p;
            return p;
        }

        /// <summary>注册发射器级参数。</summary>
        public FlowVFXParameter RegisterEmitterParameter(string emitterName, string name, FlowVFXParameterType type)
        {
            if (!_emitterParams.TryGetValue(emitterName, out var dict))
            {
                dict = new Dictionary<string, FlowVFXParameter>();
                _emitterParams[emitterName] = dict;
            }
            var p = new FlowVFXParameter { Name = name, Type = type };
            dict[name] = p;
            return p;
        }

        /// <summary>注册模块级参数。</summary>
        public FlowVFXParameter RegisterModuleParameter(string emitterName, string moduleName, string name, FlowVFXParameterType type)
        {
            string key = $"{emitterName}.{moduleName}";
            if (!_moduleParams.TryGetValue(key, out var dict))
            {
                dict = new Dictionary<string, FlowVFXParameter>();
                _moduleParams[key] = dict;
            }
            var p = new FlowVFXParameter { Name = name, Type = type };
            dict[name] = p;
            return p;
        }

        // ──────────────────────────────────────────────────────────
        // 参数访问（分层查找：模块→发射器→系统）
        // ──────────────────────────────────────────────────────────

        /// <summary>按层级查找参数（先查模块级，再查发射器级，最后查系统级）。</summary>
        public FlowVFXParameter? Resolve(string emitterName, string? moduleName, string name)
        {
            if (moduleName != null)
            {
                string key = $"{emitterName}.{moduleName}";
                if (_moduleParams.TryGetValue(key, out var modDict) && modDict.TryGetValue(name, out var mp))
                    return mp;
            }
            if (_emitterParams.TryGetValue(emitterName, out var emDict) && emDict.TryGetValue(name, out var ep))
                return ep;
            if (_systemParams.TryGetValue(name, out var sp))
                return sp;
            return null;
        }

        /// <summary>获取系统级参数。</summary>
        public FlowVFXParameter? GetSystemParameter(string name)
        {
            return _systemParams.TryGetValue(name, out var p) ? p : null;
        }

        /// <summary>设置系统级浮点参数。</summary>
        public void SetFloat(string name, float value)
        {
            if (_systemParams.TryGetValue(name, out var p))
            {
                p.FloatValue = value;
                MarkDirty(name);
            }
            else
            {
                _systemParams[name] = FlowVFXParameter.CreateFloat(name, value);
            }
        }

        /// <summary>设置系统级 Vec3 参数。</summary>
        public void SetVector3(string name, Vector3 value)
        {
            if (_systemParams.TryGetValue(name, out var p))
            {
                p.Vector3Value = value;
                MarkDirty(name);
            }
            else
            {
                _systemParams[name] = FlowVFXParameter.CreateVec3(name, value);
            }
        }

        /// <summary>设置系统级颜色参数。</summary>
        public void SetColor(string name, Vector4 value)
        {
            if (_systemParams.TryGetValue(name, out var p))
            {
                p.ColorValue = value;
                MarkDirty(name);
            }
            else
            {
                _systemParams[name] = FlowVFXParameter.CreateColor(name, value);
            }
        }

        /// <summary>标记参数为脏。</summary>
        public void MarkDirty(string name)
        {
            if (_bindingIndex.TryGetValue(name, out int idx))
            {
                var b = _bindings[idx];
                b.IsDirty = true;
                _bindings[idx] = b;
            }
        }

        /// <summary>标记所有参数为脏。</summary>
        public void MarkAllDirty()
        {
            for (int i = 0; i < _bindings.Count; i++)
            {
                var b = _bindings[i];
                b.IsDirty = true;
                _bindings[i] = b;
            }
        }

        // ──────────────────────────────────────────────────────────
        // GPU 绑定布局
        // ──────────────────────────────────────────────────────────

        /// <summary>添加参数绑定到 GPU 常量缓冲。</summary>
        public void AddBinding(string name, FlowVFXParameterType type, uint cbRegister, uint byteOffset)
        {
            if (_bindingIndex.ContainsKey(name)) return;

            uint size = type switch
            {
                FlowVFXParameterType.Float => 4u,
                FlowVFXParameterType.Vector2 => 8u,
                FlowVFXParameterType.Vector3 => 16u, // float3 padded to float4
                FlowVFXParameterType.Color => 16u,
                FlowVFXParameterType.Matrix => 64u,
                FlowVFXParameterType.Bool => 4u,
                FlowVFXParameterType.Int => 4u,
                _ => 4u
            };

            var binding = new FlowVFXParameterBinding
            {
                Name = name,
                Type = type,
                ConstantBufferRegister = cbRegister,
                ByteOffset = byteOffset,
                ByteSize = size,
                IsDirty = true
            };

            _bindingIndex[name] = _bindings.Count;
            _bindings.Add(binding);

            // 更新常量缓冲大小
            uint endOffset = byteOffset + size;
            if (!_cbSizes.TryGetValue(cbRegister, out uint curSize) || endOffset > curSize)
            {
                _cbSizes[cbRegister] = endOffset;
                // 向上对齐到 256 字节（D3D12 CB 对齐要求）
                _cbSizes[cbRegister] = (_cbSizes[cbRegister] + 255u) & ~255u;
            }
        }

        /// <summary>初始化 GPU 常量缓冲。</summary>
        public void InitializeGPU(RHIDevice device)
        {
            _device = device;
            foreach (var (reg, size) in _cbSizes)
            {
                var desc = new RHIBufferDesc
                {
                    SizeInBytes = Math.Max(size, 256),
                    Format = RHIFormat.Unknown,
                    HeapType = RHIResourceHeapType.Upload,
                    Flags = RHIBufferFlags.ConstantBuffer,
                    Stride = Math.Max(size, 256)
                };
                _cbBuffers[reg] = device.CreateBuffer(desc, $"FlowVFXParamCB_b{reg}");
            }
        }

        /// <summary>将脏参数上传到 GPU 常量缓冲。</summary>
        public void UploadDirty(RHICommandList cmd)
        {
            if (_device == null) return;

            foreach (var (reg, size) in _cbSizes)
            {
                if (!_cbBuffers.TryGetValue(reg, out var buf) || buf == null) continue;

                // 检查该 CB 是否有脏绑定
                bool hasDirty = false;
                for (int i = 0; i < _bindings.Count; i++)
                {
                    if (_bindings[i].ConstantBufferRegister == reg && _bindings[i].IsDirty)
                    {
                        hasDirty = true;
                        break;
                    }
                }
                if (!hasDirty) continue;

                // 构建完整的 CB 数据
                int cbSize = (int)Math.Max(size, 256);
                byte[] cbData = new byte[cbSize];

                for (int i = 0; i < _bindings.Count; i++)
                {
                    var b = _bindings[i];
                    if (b.ConstantBufferRegister != reg) continue;

                    var param = _systemParams.TryGetValue(b.Name, out var sp) ? sp : null;
                    if (param == null) continue;

                    WriteParameterToBytes(cbData, b, param);

                    b.IsDirty = false;
                    _bindings[i] = b;
                }

                _device.UploadToBuffer(buf, cbData);
            }
        }

        /// <summary>将单个参数写入字节数组的指定偏移。</summary>
        private static void WriteParameterToBytes(byte[] data, FlowVFXParameterBinding binding, FlowVFXParameter param)
        {
            int offset = (int)binding.ByteOffset;
            switch (binding.Type)
            {
                case FlowVFXParameterType.Float:
                    BitConverter.TryWriteBytes(data.AsSpan(offset, 4), param.FloatValue);
                    break;
                case FlowVFXParameterType.Vector2:
                    BitConverter.TryWriteBytes(data.AsSpan(offset, 4), param.Vector2Value.X);
                    BitConverter.TryWriteBytes(data.AsSpan(offset + 4, 4), param.Vector2Value.Y);
                    break;
                case FlowVFXParameterType.Vector3:
                    BitConverter.TryWriteBytes(data.AsSpan(offset, 4), param.Vector3Value.X);
                    BitConverter.TryWriteBytes(data.AsSpan(offset + 4, 4), param.Vector3Value.Y);
                    BitConverter.TryWriteBytes(data.AsSpan(offset + 8, 4), param.Vector3Value.Z);
                    break;
                case FlowVFXParameterType.Color:
                    BitConverter.TryWriteBytes(data.AsSpan(offset, 4), param.ColorValue.X);
                    BitConverter.TryWriteBytes(data.AsSpan(offset + 4, 4), param.ColorValue.Y);
                    BitConverter.TryWriteBytes(data.AsSpan(offset + 8, 4), param.ColorValue.Z);
                    BitConverter.TryWriteBytes(data.AsSpan(offset + 12, 4), param.ColorValue.W);
                    break;
                case FlowVFXParameterType.Bool:
                    BitConverter.TryWriteBytes(data.AsSpan(offset, 4), param.BoolValue ? 1u : 0u);
                    break;
                case FlowVFXParameterType.Int:
                    BitConverter.TryWriteBytes(data.AsSpan(offset, 4), (uint)param.IntValue);
                    break;
            }
        }

        /// <summary>获取指定寄存器的常量缓冲。</summary>
        public RHIBuffer? GetConstantBuffer(uint register)
        {
            return _cbBuffers.TryGetValue(register, out var buf) ? buf : null;
        }

        /// <summary>获取所有已分配的常量缓冲寄存器。</summary>
        public IEnumerable<uint> GetConstantBufferRegisters() => _cbSizes.Keys;

        public void Dispose()
        {
            if (_disposed) return;
            foreach (var (_, buf) in _cbBuffers)
                buf?.Dispose();
            _cbBuffers.Clear();
            _disposed = true;
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // 第三部分：粒子属性数据接口
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 粒子属性类型。定义可通过属性接口访问的粒子数据通道。
    /// 与已有的 <see cref="FlowVFXDataInterface"/>（外部资源数据接口：曲线/噪声/网格/纹理）
    /// 不同，此接口提供对粒子自身属性（位置/速度/颜色等）的类型化读写访问。
    /// </summary>
    public enum FlowVFXAttributeType
    {
        /// <summary>粒子世界位置 (float3)。</summary>
        Position,
        /// <summary>粒子速度向量 (float3)。</summary>
        Velocity,
        /// <summary>粒子加速度 (float3)。</summary>
        Acceleration,
        /// <summary>粒子颜色 RGBA (float4)。</summary>
        Color,
        /// <summary>粒子生命周期 (float)。</summary>
        Lifetime,
        /// <summary>粒子当前年龄 (float)。</summary>
        Age,
        /// <summary>粒子尺寸 XY (float2)。</summary>
        Size,
        /// <summary>粒子旋转角度 (float)。</summary>
        Rotation,
        /// <summary>粒子质量 (float)。</summary>
        Mass,
        /// <summary>粒子随机种子 (float)。</summary>
        Seed,
        /// <summary>归一化年龄 [0,1] (float, 只读)。</summary>
        NormalizedAge,
        /// <summary>粒子是否存活 (bool, 只读)。</summary>
        Alive
    }

    /// <summary>
    /// 粒子属性数据接口。提供对 SOA 粒子数据的类型化读写访问，
    /// 供模块通过统一接口读写粒子属性，同时支持 CPU/GPU 双路径。
    /// </summary>
    public class FlowVFXAttributeInterface : IDisposable
    {
        /// <summary>接口名称。</summary>
        public string Name = "AttributeInterface";
        /// <summary>绑定的粒子数据容量。</summary>
        public int Capacity;
        /// <summary>是否使用 GPU 路径。</summary>
        public bool UseGPU;

        // ── CPU 端数据引用（SOA 数组）──
        private Vector3[]? _positions;
        private Vector3[]? _velocities;
        private Vector3[]? _accelerations;
        private Vector4[]? _colors;
        private float[]? _ages;
        private float[]? _lifetimes;
        private Vector2[]? _sizes;
        private float[]? _rotations;
        private float[]? _masses;
        private float[]? _seeds;
        private uint[]? _flags;

        // ── GPU 端缓冲引用 ──
        private readonly Dictionary<FlowVFXAttributeType, RHIBuffer?> _gpuBuffers = new();
        private RHIDevice? _device;

        // ──────────────────────────────────────────────────────────
        // 绑定
        // ──────────────────────────────────────────────────────────

        /// <summary>绑定到 CPU SOA 数据。</summary>
        public void BindCPU(ParticleSOA data)
        {
            Capacity = data.Capacity;
            _positions = data.Position;
            _velocities = data.Velocity;
            _accelerations = data.Acceleration;
            _colors = data.Color;
            _ages = data.Age;
            _lifetimes = data.Lifetime;
            _sizes = data.Size;
            _rotations = data.Rotation;
            _masses = data.Mass;
            _seeds = data.RandomSeed;
            _flags = data.Flags;
            UseGPU = false;
        }

        /// <summary>绑定到 GPU 缓冲。</summary>
        public void BindGPU(RHIDevice device, ParticleSOA data)
        {
            _device = device;
            UseGPU = true;
            // 创建/获取各属性的 GPU 结构化缓冲
            _gpuBuffers[FlowVFXAttributeType.Position] = data.GetOrCreateGPUBuffer<Vector3>("Position");
            _gpuBuffers[FlowVFXAttributeType.Velocity] = data.GetOrCreateGPUBuffer<Vector3>("Velocity");
            _gpuBuffers[FlowVFXAttributeType.Color] = data.GetOrCreateGPUBuffer<Vector4>("Color");
            _gpuBuffers[FlowVFXAttributeType.Size] = data.GetOrCreateGPUBuffer<Vector2>("Size");
            _gpuBuffers[FlowVFXAttributeType.Age] = data.GetOrCreateGPUBuffer<float>("Age");
            _gpuBuffers[FlowVFXAttributeType.Lifetime] = data.GetOrCreateGPUBuffer<float>("Lifetime");
            _gpuBuffers[FlowVFXAttributeType.Mass] = data.GetOrCreateGPUBuffer<float>("Mass");
            _gpuBuffers[FlowVFXAttributeType.Seed] = data.GetOrCreateGPUBuffer<float>("Seed");
            _gpuBuffers[FlowVFXAttributeType.Rotation] = data.GetOrCreateGPUBuffer<float>("Rotation");
            _gpuBuffers[FlowVFXAttributeType.Alive] = data.GetOrCreateGPUBuffer<uint>("Flags");

            // 同时保留 CPU 引用用于 fallback
            BindCPU(data);
        }

        // ──────────────────────────────────────────────────────────
        // 读取
        // ──────────────────────────────────────────────────────────

        /// <summary>读取 float3 属性。</summary>
        public Vector3 GetVector3(FlowVFXAttributeType attr, int index)
        {
            if (index < 0 || index >= Capacity) return Vector3.Zero;
            return attr switch
            {
                FlowVFXAttributeType.Position => _positions?[index] ?? Vector3.Zero,
                FlowVFXAttributeType.Velocity => _velocities?[index] ?? Vector3.Zero,
                FlowVFXAttributeType.Acceleration => _accelerations?[index] ?? Vector3.Zero,
                _ => Vector3.Zero
            };
        }

        /// <summary>读取 float4 属性。</summary>
        public Vector4 GetVector4(FlowVFXAttributeType attr, int index)
        {
            if (index < 0 || index >= Capacity) return Vector4.Zero;
            return attr switch
            {
                FlowVFXAttributeType.Color => _colors?[index] ?? Vector4.One,
                _ => Vector4.Zero
            };
        }

        /// <summary>读取 float2 属性。</summary>
        public Vector2 GetVector2(FlowVFXAttributeType attr, int index)
        {
            if (index < 0 || index >= Capacity) return Vector2.Zero;
            return attr switch
            {
                FlowVFXAttributeType.Size => _sizes?[index] ?? Vector2.One,
                _ => Vector2.Zero
            };
        }

        /// <summary>读取 float 属性。</summary>
        public float GetFloat(FlowVFXAttributeType attr, int index)
        {
            if (index < 0 || index >= Capacity) return 0f;
            return attr switch
            {
                FlowVFXAttributeType.Lifetime => _lifetimes?[index] ?? 0f,
                FlowVFXAttributeType.Age => _ages?[index] ?? 0f,
                FlowVFXAttributeType.Rotation => _rotations?[index] ?? 0f,
                FlowVFXAttributeType.Mass => _masses?[index] ?? 1f,
                FlowVFXAttributeType.Seed => _seeds?[index] ?? 0f,
                FlowVFXAttributeType.NormalizedAge => GetNormalizedAge(index),
                _ => 0f
            };
        }

        /// <summary>读取 bool 属性。</summary>
        public bool GetBool(FlowVFXAttributeType attr, int index)
        {
            if (index < 0 || index >= Capacity) return false;
            return attr switch
            {
                FlowVFXAttributeType.Alive => (_flags?[index] & 1u) != 0,
                _ => false
            };
        }

        // ──────────────────────────────────────────────────────────
        // 写入
        // ──────────────────────────────────────────────────────────

        /// <summary>写入 float3 属性。</summary>
        public void SetVector3(FlowVFXAttributeType attr, int index, Vector3 value)
        {
            if (index < 0 || index >= Capacity) return;
            switch (attr)
            {
                case FlowVFXAttributeType.Position when _positions != null: _positions[index] = value; break;
                case FlowVFXAttributeType.Velocity when _velocities != null: _velocities[index] = value; break;
                case FlowVFXAttributeType.Acceleration when _accelerations != null: _accelerations[index] = value; break;
            }
        }

        /// <summary>写入 float4 属性。</summary>
        public void SetVector4(FlowVFXAttributeType attr, int index, Vector4 value)
        {
            if (index < 0 || index >= Capacity) return;
            if (attr == FlowVFXAttributeType.Color && _colors != null)
                _colors[index] = value;
        }

        /// <summary>写入 float2 属性。</summary>
        public void SetVector2(FlowVFXAttributeType attr, int index, Vector2 value)
        {
            if (index < 0 || index >= Capacity) return;
            if (attr == FlowVFXAttributeType.Size && _sizes != null)
                _sizes[index] = value;
        }

        /// <summary>写入 float 属性。</summary>
        public void SetFloat(FlowVFXAttributeType attr, int index, float value)
        {
            if (index < 0 || index >= Capacity) return;
            switch (attr)
            {
                case FlowVFXAttributeType.Lifetime when _lifetimes != null: _lifetimes[index] = value; break;
                case FlowVFXAttributeType.Age when _ages != null: _ages[index] = value; break;
                case FlowVFXAttributeType.Rotation when _rotations != null: _rotations[index] = value; break;
                case FlowVFXAttributeType.Mass when _masses != null: _masses[index] = value; break;
            }
        }

        /// <summary>累加 float3 属性（用于速度累加）。</summary>
        public void AddVector3(FlowVFXAttributeType attr, int index, Vector3 delta)
        {
            if (index < 0 || index >= Capacity) return;
            switch (attr)
            {
                case FlowVFXAttributeType.Velocity when _velocities != null: _velocities[index] += delta; break;
                case FlowVFXAttributeType.Position when _positions != null: _positions[index] += delta; break;
                case FlowVFXAttributeType.Acceleration when _accelerations != null: _accelerations[index] += delta; break;
            }
        }

        /// <summary>标记粒子存活/死亡。</summary>
        public void SetAlive(int index, bool alive)
        {
            if (index < 0 || index >= Capacity || _flags == null) return;
            if (alive) _flags[index] |= 1u;
            else _flags[index] &= ~1u;
        }

        // ──────────────────────────────────────────────────────────
        // 辅助
        // ──────────────────────────────────────────────────────────

        /// <summary>获取归一化年龄 [0,1]。</summary>
        public float GetNormalizedAge(int index)
        {
            if (_ages == null || _lifetimes == null || index < 0 || index >= Capacity) return 1f;
            float lt = _lifetimes[index];
            return lt > 1e-4f ? Math.Clamp(_ages[index] / lt, 0f, 1f) : 1f;
        }

        /// <summary>获取 GPU 缓冲（供 Compute Shader 绑定）。</summary>
        public RHIBuffer? GetGPUBuffer(FlowVFXAttributeType attr)
        {
            return _gpuBuffers.TryGetValue(attr, out var buf) ? buf : null;
        }

        /// <summary>同步 CPU 数据到 GPU 缓冲。</summary>
        public void SyncToGPU(RHICommandList cmd)
        {
            if (!UseGPU || _device == null) return;
            // 同步通过 ParticleSOA.SyncToGPU 完成
        }

        public void Dispose()
        {
            _gpuBuffers.Clear();
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // 第四部分：增强事件系统
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 增强粒子事件记录。包含完整的碰撞/死亡/生成信息，
    /// 支持子发射器派生和事件链。
    /// </summary>
    public struct FlowVFXEventRecord
    {
        /// <summary>事件类型。</summary>
        public ParticleEventType Type;
        /// <summary>事件来源发射器名。</summary>
        public string SourceEmitter;
        /// <summary>粒子索引。</summary>
        public int ParticleIndex;
        /// <summary>事件发生位置。</summary>
        public Vector3 Position;
        /// <summary>碰撞法线（碰撞事件用）。</summary>
        public Vector3 Normal;
        /// <summary>粒子速度。</summary>
        public Vector3 Velocity;
        /// <summary>粒子颜色（用于子粒子继承）。</summary>
        public Vector4 Color;
        /// <summary>粒子速度大小。</summary>
        public float Speed;
        /// <summary>粒子归一化年龄。</summary>
        public float NormalizedAge;
        /// <summary>用户自定义数据。</summary>
        public uint UserData;
        /// <summary>事件时间戳。</summary>
        public float Timestamp;
        /// <summary>是否已被处理。</summary>
        public bool Handled;
    }

    /// <summary>
    /// 事件处理器委托（带 ref 以允许修改事件记录）。</summary>
    public delegate void FlowVFXEventRecordHandler(ref FlowVFXEventRecord evt);

    /// <summary>
    /// FlowVFX 事件分发器。管理粒子事件队列，支持事件优先级、
    /// 过滤、子发射器触发、事件统计。
    /// 比 <see cref="ParticleEventQueue"/> 提供更完整的事件链与子发射器集成。
    /// </summary>
    public class FlowVFXEventDispatcher
    {
        private readonly List<FlowVFXEventRecord> _pendingEvents = new();
        private readonly List<FlowVFXEventRecord> _deferredEvents = new();
        private readonly Dictionary<ParticleEventType, List<FlowVFXEventRecordHandler>> _handlers = new();

        // ── 事件统计 ──
        private readonly Dictionary<ParticleEventType, int> _stats = new();
        private int _totalEventsDispatched;

        /// <summary>待处理事件数。</summary>
        public int PendingCount => _pendingEvents.Count;
        /// <summary>总派发事件数（自创建以来）。</summary>
        public int TotalDispatched => _totalEventsDispatched;

        // ──────────────────────────────────────────────────────────
        // 订阅
        // ──────────────────────────────────────────────────────────

        /// <summary>订阅指定类型的事件。</summary>
        public void Subscribe(ParticleEventType type, FlowVFXEventRecordHandler handler)
        {
            if (!_handlers.TryGetValue(type, out var list))
            {
                list = new List<FlowVFXEventRecordHandler>();
                _handlers[type] = list;
            }
            list.Add(handler);
        }

        /// <summary>取消订阅。</summary>
        public void Unsubscribe(ParticleEventType type, FlowVFXEventRecordHandler handler)
        {
            if (_handlers.TryGetValue(type, out var list))
                list.Remove(handler);
        }

        // ──────────────────────────────────────────────────────────
        // 入队
        // ──────────────────────────────────────────────────────────

        /// <summary>入队一个事件。</summary>
        public void Enqueue(in FlowVFXEventRecord evt)
        {
            _pendingEvents.Add(evt);
        }

        /// <summary>入队生成事件。</summary>
        public void EnqueueSpawn(string emitter, int index, Vector3 pos, float timestamp)
        {
            _pendingEvents.Add(new FlowVFXEventRecord
            {
                Type = ParticleEventType.Spawned,
                SourceEmitter = emitter,
                ParticleIndex = index,
                Position = pos,
                Timestamp = timestamp
            });
        }

        /// <summary>入队死亡事件。</summary>
        public void EnqueueDeath(string emitter, int index, Vector3 pos, Vector3 vel, Vector4 color, float normalizedAge, float timestamp)
        {
            _pendingEvents.Add(new FlowVFXEventRecord
            {
                Type = ParticleEventType.Died,
                SourceEmitter = emitter,
                ParticleIndex = index,
                Position = pos,
                Velocity = vel,
                Color = color,
                NormalizedAge = normalizedAge,
                Timestamp = timestamp
            });
        }

        /// <summary>入队碰撞事件。</summary>
        public void EnqueueCollision(string emitter, int index, Vector3 pos, Vector3 normal, Vector3 vel, float timestamp)
        {
            _pendingEvents.Add(new FlowVFXEventRecord
            {
                Type = ParticleEventType.Collided,
                SourceEmitter = emitter,
                ParticleIndex = index,
                Position = pos,
                Normal = normal,
                Velocity = vel,
                Speed = vel.Length(),
                Timestamp = timestamp
            });
        }

        /// <summary>入队延迟事件（下一帧派发）。</summary>
        public void EnqueueDeferred(in FlowVFXEventRecord evt)
        {
            _deferredEvents.Add(evt);
        }

        // ──────────────────────────────────────────────────────────
        // 派发
        // ──────────────────────────────────────────────────────────

        /// <summary>派发所有待处理事件给订阅者。</summary>
        public void Dispatch()
        {
            // 将上一帧的延迟事件移入待处理队列
            if (_deferredEvents.Count > 0)
            {
                _pendingEvents.AddRange(_deferredEvents);
                _deferredEvents.Clear();
            }

            foreach (var evt in _pendingEvents)
            {
                var e = evt;
                if (_handlers.TryGetValue(e.Type, out var list))
                {
                    for (int i = 0; i < list.Count; i++)
                        list[i](ref e);
                }

                // 统计
                _stats[e.Type] = _stats.TryGetValue(e.Type, out int c) ? c + 1 : 1;
                _totalEventsDispatched++;
            }
            _pendingEvents.Clear();
        }

        /// <summary>清空所有事件。</summary>
        public void Clear()
        {
            _pendingEvents.Clear();
            _deferredEvents.Clear();
        }

        /// <summary>获取指定类型的事件统计。</summary>
        public int GetEventCount(ParticleEventType type)
        {
            return _stats.TryGetValue(type, out int c) ? c : 0;
        }

        /// <summary>重置统计。</summary>
        public void ResetStats()
        {
            _stats.Clear();
            _totalEventsDispatched = 0;
        }

        /// <summary>获取本帧所有待处理事件（只读，供子发射器消费）。</summary>
        public IReadOnlyList<FlowVFXEventRecord> GetPendingEvents() => _pendingEvents;
    }

    // ──────────────────────────────────────────────────────────────────
    // 第五部分：子发射器系统
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 子发射器配置。描述在特定事件发生时如何派生子粒子。
    /// 比 <see cref="SubEmitterDesc"/> 提供更完整的继承与深度控制。
    /// </summary>
    public class FlowVFXSubEmitterConfig
    {
        /// <summary>配置名称。</summary>
        public string Name = "SubEmitter";
        /// <summary>触发时机。</summary>
        public SubEmitterTrigger Trigger = SubEmitterTrigger.OnDeath;
        /// <summary>每次触发生成的子粒子数。</summary>
        public int SpawnCount = 5;
        /// <summary>最大生成次数（-1=无限）。</summary>
        public int MaxSpawnCount = -1;
        /// <summary>子发射器深度（防止无限递归）。</summary>
        public int MaxDepth = 3;

        // ── 继承 ──
        public bool InheritPosition = true;
        public bool InheritVelocity = true;
        public float InheritedVelocityScale = 0.5f;
        public bool InheritColor = false;
        public bool InheritRotation = false;

        // ── 子粒子初始参数 ──
        public GPUEmissionShape Shape = GPUEmissionShape.Sphere;
        public float ShapeRadius = 0.2f;
        public ParticleCurve Speed = new(1f, 3f);
        public ParticleCurve Lifetime = new(1f, 2f);
        public ParticleCurve Size = new(0.05f, 0.1f);
        public Vector4 Color = new(1, 1, 1, 1);
        public Vector3 EmissionDirection = Vector3.UnitY;

        // ── 速率限制 ──
        public float RateLimit = 0f; // 每秒最大触发次数（0=不限）
        private float _rateTimer;

        /// <summary>是否可以触发（速率限制检查）。</summary>
        public bool CanTrigger(float dt)
        {
            if (RateLimit <= 0f) return true;
            _rateTimer += dt;
            if (_rateTimer >= 1f / RateLimit)
            {
                _rateTimer = 0f;
                return true;
            }
            return false;
        }

        /// <summary>重置速率计时器。</summary>
        public void Reset() => _rateTimer = 0f;
    }

    /// <summary>
    /// 子发射器运行时处理器。监听事件分发器，
    /// 在配置的触发时机从父粒子状态派生子粒子。
    /// </summary>
    public class FlowVFXSubEmitterHandler
    {
        private readonly List<FlowVFXSubEmitterConfig> _configs = new();
        private readonly Dictionary<string, int> _spawnCounters = new();
        private readonly Random _rng = new();
        private int _currentDepth;

        /// <summary>子发射器配置列表。</summary>
        public IReadOnlyList<FlowVFXSubEmitterConfig> Configs => _configs;

        /// <summary>当前递归深度。</summary>
        public int CurrentDepth => _currentDepth;

        /// <summary>添加子发射器配置。</summary>
        public void AddConfig(FlowVFXSubEmitterConfig config)
        {
            _configs.Add(config);
            _spawnCounters[config.Name] = 0;
        }

        /// <summary>处理事件，在匹配时生成子粒子。</summary>
        /// <param name="evt">事件记录</param>
        /// <param name="data">目标粒子 SOA 数据（子粒子写入此数据）</param>
        /// <param name="dt">帧步长（用于速率限制）</param>
        /// <returns>本次生成的子粒子数</returns>
        public int ProcessEvent(in FlowVFXEventRecord evt, ParticleSOA data, float dt)
        {
            if (_currentDepth >= 3) return 0; // 深度限制

            int spawned = 0;
            foreach (var config in _configs)
            {
                bool match = config.Trigger switch
                {
                    SubEmitterTrigger.OnSpawn => evt.Type == ParticleEventType.Spawned,
                    SubEmitterTrigger.OnCollide => evt.Type == ParticleEventType.Collided,
                    SubEmitterTrigger.OnDeath => evt.Type == ParticleEventType.Died,
                    _ => false
                };
                if (!match) continue;
                if (!config.CanTrigger(dt)) continue;

                // 检查最大生成次数
                if (config.MaxSpawnCount >= 0)
                {
                    int count = _spawnCounters.GetValueOrDefault(config.Name, 0);
                    if (count >= config.MaxSpawnCount) continue;
                }

                // 继承父粒子状态
                Vector3 basePos = config.InheritPosition ? evt.Position : Vector3.Zero;
                Vector3 baseVel = config.InheritVelocity ? evt.Velocity * config.InheritedVelocityScale : Vector3.Zero;
                Vector4 baseCol = config.InheritColor ? evt.Color : config.Color;

                // 生成子粒子
                for (int s = 0; s < config.SpawnCount; s++)
                {
                    int slot = AllocateSlot(data);
                    if (slot < 0) break;

                    SpawnChild(data, config, slot, basePos, baseVel, baseCol);
                    spawned++;
                }

                _spawnCounters[config.Name] = _spawnCounters.GetValueOrDefault(config.Name, 0) + 1;
            }
            return spawned;
        }

        /// <summary>分配空闲槽位。</summary>
        private static int AllocateSlot(ParticleSOA data)
        {
            for (int i = 0; i < data.Capacity; i++)
                if ((data.Flags[i] & 1u) == 0) return i;
            return -1;
        }

        /// <summary>生成单个子粒子。</summary>
        private void SpawnChild(ParticleSOA data, FlowVFXSubEmitterConfig config, int slot, Vector3 basePos, Vector3 baseVel, Vector4 baseCol)
        {
            float r1 = (float)_rng.NextDouble();
            float r2 = (float)_rng.NextDouble();
            float r3 = (float)_rng.NextDouble();

            // 形状采样
            Vector3 offset = config.Shape switch
            {
                GPUEmissionShape.Sphere => RandomInSphere(r1, r2, r3) * config.ShapeRadius,
                GPUEmissionShape.Hemisphere => RandomInHemisphere(r1, r2, r3) * config.ShapeRadius,
                GPUEmissionShape.Box => new Vector3(
                    (r1 - 0.5f) * config.ShapeRadius * 2f,
                    (r2 - 0.5f) * config.ShapeRadius * 2f,
                    (r3 - 0.5f) * config.ShapeRadius * 2f),
                _ => Vector3.Zero
            };

            // 方向采样
            Vector3 dir = config.Shape switch
            {
                GPUEmissionShape.Sphere => RandomInSphere(r1, r2, r3),
                GPUEmissionShape.Hemisphere => RandomInHemisphere(r1, r2, r3),
                _ => config.EmissionDirection
            };
            if (dir.LengthSquared() < 1e-6f) dir = config.EmissionDirection;
            dir = Vector3.Normalize(dir);

            float speed = config.Speed.Evaluate(0f, r1);
            float lifetime = config.Lifetime.Evaluate(0f, r2);
            float size = config.Size.Evaluate(0f, r3);

            data.Position[slot] = basePos + offset;
            data.Velocity[slot] = baseVel + dir * speed;
            data.Color[slot] = baseCol;
            data.BaseColor[slot] = baseCol;
            data.Size[slot] = new Vector2(size, size);
            data.Age[slot] = 0f;
            data.Lifetime[slot] = lifetime;
            data.Mass[slot] = 1f;
            data.RandomSeed[slot] = r1;
            data.Flags[slot] = 1u | 4u; // alive + child
        }

        private static Vector3 RandomInSphere(float r1, float r2, float r3)
        {
            float theta = r1 * MathF.PI * 2f;
            float phi = MathF.Acos(2f * r2 - 1f);
            return new Vector3(
                MathF.Sin(phi) * MathF.Cos(theta),
                MathF.Cos(phi),
                MathF.Sin(phi) * MathF.Sin(theta));
        }

        private static Vector3 RandomInHemisphere(float r1, float r2, float r3)
        {
            float theta = r1 * MathF.PI * 2f;
            float phi = MathF.Acos(r2);
            return new Vector3(
                MathF.Sin(phi) * MathF.Cos(theta),
                MathF.Cos(phi),
                MathF.Sin(phi) * MathF.Sin(theta));
        }

        /// <summary>进入子发射器深度。</summary>
        public void PushDepth() => _currentDepth++;
        /// <summary>退出子发射器深度。</summary>
        public void PopDepth() => _currentDepth = Math.Max(0, _currentDepth - 1);

        /// <summary>重置生成计数器。</summary>
        public void ResetCounters()
        {
            foreach (var key in _spawnCounters.Keys)
                _spawnCounters[key] = 0;
            foreach (var config in _configs)
                config.Reset();
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // 第六部分：发射器生命周期管理
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 发射器生命周期状态。
    /// </summary>
    public enum FlowVFXEmitterLifecycleState
    {
        /// <summary>等待延迟启动。</summary>
        WaitingDelay,
        /// <summary>正在发射/模拟。</summary>
        Active,
        /// <summary>已停止发射，等待残留粒子消亡。</summary>
        Finishing,
        /// <summary>完全结束，可被回收。</summary>
        Finished
    }

    /// <summary>
    /// 发射器生命周期管理。控制延迟启动、循环次数、持续时间、自动停止。
    /// </summary>
    public class FlowVFXEmitterLifecycle
    {
        /// <summary>延迟启动时间（秒）。系统启动后等待此时间再开始发射。</summary>
        public float DelayStart = 0f;
        /// <summary>单次循环持续时间（秒）。</summary>
        public float Duration = 5f;
        /// <summary>循环次数（-1=无限循环, 0=不循环, N=循环 N 次）。</summary>
        public int LoopCount = -1;
        /// <summary>循环间隔（两次循环之间的暂停时间）。</summary>
        public float LoopInterval = 0f;
        /// <summary>发射器模式。</summary>
        public FlowVFXEmitterMode Mode = FlowVFXEmitterMode.Continuous;
        /// <summary>单次模式发射数量。</summary>
        public int OneShotCount = 100;
        /// <summary>脉冲模式间隔。</summary>
        public float BurstInterval = 1f;
        /// <summary>脉冲模式每次爆发数量。</summary>
        public int BurstCount = 50;

        // ── 运行时状态 ──
        private float _elapsedTime;
        private float _loopTimer;
        private float _intervalTimer;
        private float _burstTimer;
        private int _completedLoops;
        private bool _oneShotFired;
        private FlowVFXEmitterLifecycleState _state = FlowVFXEmitterLifecycleState.WaitingDelay;

        /// <summary>当前生命周期状态。</summary>
        public FlowVFXEmitterLifecycleState State => _state;
        /// <summary>已运行时间（不含延迟）。</summary>
        public float ElapsedTime => _elapsedTime;
        /// <summary>已完成循环次数。</summary>
        public int CompletedLoops => _completedLoops;
        /// <summary>是否正在发射。</summary>
        public bool IsEmitting => _state == FlowVFXEmitterLifecycleState.Active;
        /// <summary>是否已结束。</summary>
        public bool IsFinished => _state == FlowVFXEmitterLifecycleState.Finished;
        /// <summary>当前循环进度 [0,1]。</summary>
        public float LoopProgress => Duration > 0f ? Math.Clamp(_loopTimer / Duration, 0f, 1f) : 1f;

        /// <summary>重置生命周期。</summary>
        public void Reset()
        {
            _elapsedTime = 0f;
            _loopTimer = 0f;
            _intervalTimer = 0f;
            _burstTimer = 0f;
            _completedLoops = 0;
            _oneShotFired = false;
            _state = DelayStart > 0f ? FlowVFXEmitterLifecycleState.WaitingDelay : FlowVFXEmitterLifecycleState.Active;
        }

        /// <summary>
        /// 更新生命周期，返回本帧应发射的粒子数。
        /// </summary>
        /// <param name="dt">帧步长</param>
        /// <param name="rateOverTime">连续模式每秒发射率（仅 Continuous 模式用）</param>
        /// <returns>本帧应发射粒子数</returns>
        public int Update(float dt, float rateOverTime)
        {
            if (_state == FlowVFXEmitterLifecycleState.Finished) return 0;

            // 延迟启动
            if (_state == FlowVFXEmitterLifecycleState.WaitingDelay)
            {
                _elapsedTime += dt;
                if (_elapsedTime >= DelayStart)
                {
                    _state = FlowVFXEmitterLifecycleState.Active;
                    _elapsedTime = 0f;
                }
                else return 0;
            }

            // 循环间隔
            if (_state == FlowVFXEmitterLifecycleState.Active && _intervalTimer > 0f)
            {
                _intervalTimer -= dt;
                if (_intervalTimer > 0f) return 0;
            }

            int toEmit = 0;

            switch (Mode)
            {
                case FlowVFXEmitterMode.Continuous:
                    toEmit = UpdateContinuous(dt, rateOverTime);
                    break;
                case FlowVFXEmitterMode.Burst:
                    toEmit = UpdateBurst(dt);
                    break;
                case FlowVFXEmitterMode.OneShot:
                    toEmit = UpdateOneShot();
                    break;
            }

            // 更新循环计时
            _loopTimer += dt;
            _elapsedTime += dt;

            if (_loopTimer >= Duration)
            {
                _completedLoops++;
                _loopTimer = 0f;

                if (LoopCount >= 0 && _completedLoops >= LoopCount)
                {
                    // 达到循环次数上限
                    _state = FlowVFXEmitterLifecycleState.Finishing;
                }
                else
                {
                    // 进入循环间隔
                    _intervalTimer = LoopInterval;
                }
            }

            return toEmit;
        }

        /// <summary>标记进入 Finishing 状态（停止发射）。</summary>
        public void StopEmitting()
        {
            if (_state == FlowVFXEmitterLifecycleState.Active)
                _state = FlowVFXEmitterLifecycleState.Finishing;
        }

        /// <summary>检查是否可以标记为完成（无存活粒子时调用）。</summary>
        public void CheckFinished(int aliveCount)
        {
            if (_state == FlowVFXEmitterLifecycleState.Finishing && aliveCount == 0)
                _state = FlowVFXEmitterLifecycleState.Finished;
        }

        private float _continuousAccum;

        private int UpdateContinuous(float dt, float rate)
        {
            _continuousAccum += dt * rate;
            int count = (int)_continuousAccum;
            _continuousAccum -= count;
            return count;
        }

        private int UpdateBurst(float dt)
        {
            _burstTimer += dt;
            if (_burstTimer >= BurstInterval)
            {
                _burstTimer -= BurstInterval;
                return BurstCount;
            }
            return 0;
        }

        private int UpdateOneShot()
        {
            if (!_oneShotFired)
            {
                _oneShotFired = true;
                return OneShotCount;
            }
            return 0;
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // 第七部分：CPU 模拟器（调试 fallback）
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// FlowVFX CPU 模拟器。提供完整的 CPU 粒子模拟路径，
    /// 作为 GPU Compute 的调试 fallback。支持子步进、碰撞、力场、
    /// 生命周期管理、事件派发、子发射器。
    /// </summary>
    public class FlowVFXCPUSimulator : IDisposable
    {
        private readonly FlowVFXSystem _system;
        private readonly List<EmitterRuntimeState> _emitterStates = new();
        private readonly FlowVFXEventDispatcher _eventDispatcher = new();
        private readonly FlowVFXSubEmitterHandler _subEmitterHandler = new();
        private readonly FlowVFXParameterStore _parameterStore;

        /// <summary>系统世界位置。</summary>
        public Vector3 Position;
        /// <summary>系统世界旋转。</summary>
        public Quaternion Rotation = Quaternion.Identity;
        /// <summary>是否正在播放。</summary>
        public bool IsPlaying { get; private set; }
        /// <summary>累计模拟时间。</summary>
        public float ElapsedTime { get; private set; }
        /// <summary>子步数（提高碰撞精度）。</summary>
        public int SubSteps = 2;
        /// <summary>事件分发器。</summary>
        public FlowVFXEventDispatcher EventDispatcher => _eventDispatcher;
        /// <summary>子发射器处理器。</summary>
        public FlowVFXSubEmitterHandler SubEmitterHandler => _subEmitterHandler;
        /// <summary>参数存储。</summary>
        public FlowVFXParameterStore ParameterStore => _parameterStore;
        /// <summary>总存活粒子数。</summary>
        public int TotalAlive
        {
            get
            {
                int c = 0;
                foreach (var s in _emitterStates) c += s.Data.AliveCount;
                return c;
            }
        }

        public FlowVFXCPUSimulator(FlowVFXSystem system, FlowVFXParameterStore? parameterStore = null)
        {
            _system = system;
            _parameterStore = parameterStore ?? new FlowVFXParameterStore();
            foreach (var emitter in system.Emitters)
                _emitterStates.Add(new EmitterRuntimeState(emitter));
        }

        /// <summary>开始播放。</summary>
        public void Play()
        {
            IsPlaying = true;
            ElapsedTime = 0f;
            foreach (var state in _emitterStates)
            {
                state.Reset();
                state.Lifecycle.Reset();
            }
            _subEmitterHandler.ResetCounters();
        }

        /// <summary>停止播放。</summary>
        public void Stop()
        {
            IsPlaying = false;
            foreach (var state in _emitterStates)
                state.Lifecycle.StopEmitting();
        }

        /// <summary>暂停。</summary>
        public void Pause() => IsPlaying = false;

        /// <summary>恢复。</summary>
        public void Resume() => IsPlaying = true;

        /// <summary>模拟一帧。</summary>
        /// <param name="dt">帧步长</param>
        /// <param name="cameraPos">相机位置（排序用）</param>
        public void Tick(float dt, Vector3 cameraPos)
        {
            if (!IsPlaying || !_system.Enabled) return;

            ElapsedTime += dt;
            var sortedEmitters = _system.GetSortedEmitters();

            float subDt = dt / SubSteps;
            for (int step = 0; step < SubSteps; step++)
            {
                for (int ei = 0; ei < sortedEmitters.Count; ei++)
                {
                    var emitter = sortedEmitters[ei];
                    if (!emitter.Enabled) continue;
                    var state = _emitterStates.Find(s => s.Emitter == emitter);
                    if (state == null || state.Lifecycle.IsFinished) continue;

                    TickEmitterCPU(state, subDt, cameraPos);
                }

                // 派发事件并处理子发射器
                ProcessEvents(subDt);
            }

            // 检查发射器是否完成
            foreach (var state in _emitterStates)
                state.Lifecycle.CheckFinished(state.Data.AliveCount);
        }

        /// <summary>CPU 模拟单个发射器。</summary>
        private void TickEmitterCPU(EmitterRuntimeState state, float dt, Vector3 cameraPos)
        {
            var emitter = state.Emitter;

            // 更新生命周期
            var emitModule = emitter.GetOrCreateModule<FlowVFXEmitModule>();
            float rate = emitModule.SpawnRate.Evaluate(state.ElapsedTime);
            int toEmit = state.Lifecycle.Update(dt, rate);

            // 发射粒子
            for (int i = 0; i < toEmit; i++)
                EmitParticleCPU(state, cameraPos);

            // 执行更新模块
            ExecuteModules(state, FlowVFXModuleStage.Update, dt);

            // 欧拉积分
            IntegrateCPU(state, dt);

            // 执行事件模块（碰撞等）
            ExecuteModules(state, FlowVFXModuleStage.Event, dt);

            // 更新生命周期/年龄
            UpdateLifetimesCPU(state, dt);

            state.ElapsedTime += dt;
        }

        /// <summary>CPU 发射单个粒子。</summary>
        private void EmitParticleCPU(EmitterRuntimeState state, Vector3 cameraPos)
        {
            int slot = AllocateSlot(state.Data);
            if (slot < 0) return;

            var emitModule = state.Emitter.GetOrCreateModule<FlowVFXEmitModule>();
            float rand = (float)Random.Shared.NextDouble();

            // 形状采样
            SampleEmitterShape(state.Emitter, out var pos, out var dir);

            state.Data.Position[slot] = Position + pos;
            state.Data.Velocity[slot] = dir * emitModule.InitialSpeed.Evaluate(0f, rand);
            state.Data.Acceleration[slot] = emitModule.InitialAcceleration;
            state.Data.Color[slot] = emitModule.InitialColor;
            state.Data.BaseColor[slot] = emitModule.InitialColor;
            float sz = emitModule.InitialSize.Evaluate(0f, rand);
            state.Data.Size[slot] = new Vector2(sz, sz);
            state.Data.Age[slot] = 0f;
            state.Data.Lifetime[slot] = emitModule.Lifetime.Evaluate(0f, rand);
            state.Data.Mass[slot] = 1f;
            state.Data.RandomSeed[slot] = rand;
            state.Data.Flags[slot] = 1u;
            state.Data.CameraDistSq[slot] = 0f;

            // 入队生成事件
            _eventDispatcher.EnqueueSpawn(state.Emitter.Name, slot, state.Data.Position[slot], state.ElapsedTime);
        }

        /// <summary>分配空闲槽位。</summary>
        private static int AllocateSlot(ParticleSOA data)
        {
            for (int i = 0; i < data.Capacity; i++)
                if ((data.Flags[i] & 1u) == 0) return i;
            return -1;
        }

        /// <summary>发射器形状采样。</summary>
        private static void SampleEmitterShape(FlowVFXEmitter emitter, out Vector3 pos, out Vector3 dir)
        {
            float r1 = (float)Random.Shared.NextDouble();
            float r2 = (float)Random.Shared.NextDouble();
            float r3 = (float)Random.Shared.NextDouble();

            switch (emitter.Shape)
            {
                case GPUEmissionShape.Sphere:
                    {
                        float theta = r1 * MathF.PI * 2f;
                        float phi = MathF.Acos(2f * r2 - 1f);
                        dir = new Vector3(MathF.Sin(phi) * MathF.Cos(theta), MathF.Cos(phi), MathF.Sin(phi) * MathF.Sin(theta));
                        pos = dir * emitter.ShapeRadius * r3;
                        break;
                    }
                case GPUEmissionShape.Cone:
                    {
                        float angleRad = emitter.ShapeAngle * MathF.PI / 180f;
                        float coneR = MathF.Tan(angleRad);
                        dir = new Vector3(
                            MathF.Cos(r1 * MathF.PI * 2f) * coneR,
                            1f,
                            MathF.Sin(r1 * MathF.PI * 2f) * coneR);
                        dir = Vector3.Normalize(dir);
                        pos = new Vector3(
                            (r2 - 0.5f) * emitter.ShapeRadius * 2f * r3,
                            0,
                            (r3 - 0.5f) * emitter.ShapeRadius * 2f * r2);
                        break;
                    }
                case GPUEmissionShape.Box:
                    pos = new Vector3(
                        (r1 - 0.5f) * emitter.BoxExtents.X,
                        (r2 - 0.5f) * emitter.BoxExtents.Y,
                        (r3 - 0.5f) * emitter.BoxExtents.Z);
                    dir = Vector3.UnitY;
                    break;
                case GPUEmissionShape.Hemisphere:
                    {
                        float theta = r1 * MathF.PI * 2f;
                        float phi = MathF.Acos(r2);
                        dir = new Vector3(MathF.Sin(phi) * MathF.Cos(theta), MathF.Cos(phi), MathF.Sin(phi) * MathF.Sin(theta));
                        pos = dir * emitter.ShapeRadius * r3;
                        break;
                    }
                default:
                    pos = Vector3.Zero;
                    dir = Vector3.UnitY;
                    break;
            }
        }

        /// <summary>CPU 欧拉积分。</summary>
        private static void IntegrateCPU(EmitterRuntimeState state, float dt)
        {
            for (int i = 0; i < state.Data.Capacity; i++)
            {
                if ((state.Data.Flags[i] & 1u) == 0) continue;
                // 速度 += 加速度 * dt
                state.Data.Velocity[i] += state.Data.Acceleration[i] * dt;
                // 位置 += 速度 * dt
                state.Data.Position[i] += state.Data.Velocity[i] * dt;
                // 清零加速度（每帧由力模块重新累加）
                state.Data.Acceleration[i] = Vector3.Zero;
            }
        }

        /// <summary>执行指定阶段的模块。</summary>
        private void ExecuteModules(EmitterRuntimeState state, FlowVFXModuleStage stage, float dt)
        {
            var modules = state.Emitter.GetModulesByStage(stage);
            foreach (var module in modules)
            {
                if (!module.Enabled) continue;
                var ctx = new FlowVFXSimulationContext
                {
                    DeltaTime = dt,
                    ElapsedTime = state.ElapsedTime,
                    Random = (float)Random.Shared.NextDouble(),
                    EmitterPosition = Position,
                    CameraPosition = Vector3.Zero,
                    ParticleCount = state.Data.Capacity,
                    Positions = state.Data.Position,
                    Velocities = state.Data.Velocity,
                    Colors = state.Data.Color,
                    Sizes = state.Data.Size,
                    Ages = state.Data.Age,
                    Lifetimes = state.Data.Lifetime,
                    Rotations = state.Data.Rotation,
                    Masses = state.Data.Mass,
                    Seeds = state.Data.RandomSeed,
                    Flags = state.Data.Flags
                };
                module.Execute(ctx);
            }
        }

        /// <summary>更新生命周期，杀死过期粒子。</summary>
        private void UpdateLifetimesCPU(EmitterRuntimeState state, float dt)
        {
            for (int i = 0; i < state.Data.Capacity; i++)
            {
                if ((state.Data.Flags[i] & 1u) == 0) continue;
                state.Data.Age[i] += dt;
                if (state.Data.Age[i] >= state.Data.Lifetimes[i])
                {
                    state.Data.Flags[i] &= ~1u;
                    // 入队死亡事件（携带父粒子状态供子发射器使用）
                    float normAge = state.Data.Lifetimes[i] > 1e-4f
                        ? state.Data.Age[i] / state.Data.Lifetimes[i]
                        : 1f;
                    _eventDispatcher.EnqueueDeath(
                        state.Emitter.Name, i,
                        state.Data.Position[i],
                        state.Data.Velocity[i],
                        state.Data.Color[i],
                        normAge,
                        state.ElapsedTime);
                }
            }
            state.Data.RecountAlive();
        }

        /// <summary>处理事件并触发子发射器。</summary>
        private void ProcessEvents(float dt)
        {
            var events = _eventDispatcher.GetPendingEvents();
            if (events.Count > 0)
            {
                _subEmitterHandler.PushDepth();
                foreach (var evt in events)
                {
                    // 为每个发射器的数据尝试生成子粒子
                    foreach (var state in _emitterStates)
                    {
                        _subEmitterHandler.ProcessEvent(evt, state.Data, dt);
                    }
                }
                _subEmitterHandler.PopDepth();
            }
            _eventDispatcher.Dispatch();
        }

        /// <summary>获取发射器运行时状态（供渲染使用）。</summary>
        public EmitterRuntimeState? GetEmitterState(string emitterName)
        {
            return _emitterStates.Find(s => s.Emitter.Name == emitterName);
        }

        /// <summary>获取全部发射器状态。</summary>
        public IReadOnlyList<EmitterRuntimeState> EmitterStates => _emitterStates;

        public void Dispose()
        {
            foreach (var s in _emitterStates) s.Dispose();
            _emitterStates.Clear();
            _parameterStore.Dispose();
        }
    }

    /// <summary>
    /// 发射器运行时状态（扩展版本）。持有粒子 SOA 数据、生命周期、事件队列。
    /// </summary>
    public sealed class EmitterRuntimeState : IDisposable
    {
        /// <summary>关联的发射器配置。</summary>
        public readonly FlowVFXEmitter Emitter;
        /// <summary>粒子 SOA 数据。</summary>
        public ParticleSOA Data;
        /// <summary>生命周期管理器。</summary>
        public readonly FlowVFXEmitterLifecycle Lifecycle = new();
        /// <summary>已运行时间。</summary>
        public float ElapsedTime;
        /// <summary>属性数据接口。</summary>
        public readonly FlowVFXAttributeInterface AttributeInterface = new();

        /// <summary>构造。</summary>
        public EmitterRuntimeState(FlowVFXEmitter emitter)
        {
            Emitter = emitter;
            Data = new ParticleSOA(emitter.MaxParticles);
            AttributeInterface.BindCPU(Data);
            // 从发射器配置同步生命周期参数
            Lifecycle.Duration = emitter.Duration;
            Lifecycle.LoopCount = emitter.Loops ? -1 : 1;
        }

        /// <summary>重置。</summary>
        public void Reset()
        {
            Data.Clear();
            ElapsedTime = 0f;
            Lifecycle.Reset();
            AttributeInterface.BindCPU(Data);
        }

        public void Dispose()
        {
            Data.Dispose();
            AttributeInterface.Dispose();
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // 第八部分：GPU Compute 调度器
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// GPU Compute 调度描述。描述如何将一个发射器的模拟调度到 GPU。
    /// </summary>
    public struct FlowVFXGPUDispatchDesc
    {
        /// <summary>线程组大小（通常 64）。</summary>
        public int ThreadGroupSize;
        /// <summary>线程组数量。</summary>
        public int GroupCount;
        /// <summary>模拟着色器 PSO。</summary>
        public RHIPipelineState? SimulatePSO;
        /// <summary>发射着色器 PSO。</summary>
        public RHIPipelineState? EmitPSO;
        /// <summary>剔除着色器 PSO。</summary>
        public RHIPipelineState? CullPSO;
        /// <summary>根签名。</summary>
        public RHIRootSignature? RootSignature;
        /// <summary>粒子数据 UAV 缓冲。</summary>
        public RHIBuffer? ParticleBuffer;
        /// <summary>存活索引 UAV 缓冲。</summary>
        public RHIBuffer? AliveListBuffer;
        /// <summary>死亡索引 UAV 缓冲。</summary>
        public RHIBuffer? DeadListBuffer;
        /// <summary>计数器 UAV 缓冲。</summary>
        public RHIBuffer? CounterBuffer;
        /// <summary>间接绘制参数 UAV 缓冲。</summary>
        public RHIBuffer? IndirectArgsBuffer;

        /// <summary>计算总线程数。</summary>
        public int TotalThreads => ThreadGroupSize * GroupCount;
    }

    /// <summary>
    /// FlowVFX GPU Compute 调度器。管理 GPU 粒子模拟的着色器调度、
    /// 资源绑定和 CPU/GPU 同步。作为 <see cref="FlowVFXCPUSimulator"/> 的 GPU 对应路径。
    /// </summary>
    public class FlowVFXGPUComputeDispatcher : IDisposable
    {
        private readonly RHIDevice _device;
        private RHICommandQueue? _computeQueue;
        private RHICommandList? _computeCmd;
        private readonly Dictionary<string, FlowVFXGPUDispatchDesc> _dispatchDescs = new();
        private readonly FlowVFXParameterStore _parameterStore;
        private bool _disposed;

        /// <summary>是否已初始化。</summary>
        public bool IsInitialized { get; private set; }
        /// <summary>参数存储。</summary>
        public FlowVFXParameterStore ParameterStore => _parameterStore;

        /// <summary>构造。</summary>
        public FlowVFXGPUComputeDispatcher(RHIDevice device, FlowVFXParameterStore? parameterStore = null)
        {
            _device = device;
            _parameterStore = parameterStore ?? new FlowVFXParameterStore();
        }

        /// <summary>初始化 GPU 调度器。</summary>
        public void Initialize()
        {
            _computeQueue = _device.GetCommandQueue(RHIQueueType.Compute);
            _computeCmd = _device.CreateCommandList(RHIQueueType.Compute, "FlowVFXGPUComputeCmd");
            _parameterStore.InitializeGPU(_device);
            IsInitialized = true;
        }

        /// <summary>注册发射器的 GPU 调度描述。</summary>
        public void RegisterEmitter(string emitterName, FlowVFXGPUDispatchDesc desc)
        {
            _dispatchDescs[emitterName] = desc;
        }

        /// <summary>GPU 模拟一帧。</summary>
        /// <param name="dt">帧步长</param>
        /// <param name="cameraPos">相机位置</param>
        /// <param name="view">视图矩阵</param>
        /// <param name="proj">投影矩阵</param>
        public void Tick(float dt, Vector3 cameraPos, Matrix4x4 view, Matrix4x4 proj)
        {
            if (!IsInitialized || _computeCmd == null) return;

            _computeCmd.Open();

            // 上传参数
            _parameterStore.UploadDirty(_computeCmd);

            // 为每个发射器调度 GPU 模拟
            foreach (var (name, desc) in _dispatchDescs)
            {
                if (desc.SimulatePSO == null || desc.RootSignature == null) continue;

                // 资源屏障：粒子缓冲 → UAV
                if (desc.ParticleBuffer != null)
                    _computeCmd.TransitionTo(desc.ParticleBuffer, RHIResourceState.UnorderedAccess);
                if (desc.AliveListBuffer != null)
                    _computeCmd.TransitionTo(desc.AliveListBuffer, RHIResourceState.UnorderedAccess);
                if (desc.DeadListBuffer != null)
                    _computeCmd.TransitionTo(desc.DeadListBuffer, RHIResourceState.UnorderedAccess);
                if (desc.CounterBuffer != null)
                    _computeCmd.TransitionTo(desc.CounterBuffer, RHIResourceState.UnorderedAccess);
                if (desc.IndirectArgsBuffer != null)
                    _computeCmd.TransitionTo(desc.IndirectArgsBuffer, RHIResourceState.UnorderedAccess);

                // 1. 发射阶段
                if (desc.EmitPSO != null && desc.ParticleBuffer != null && desc.DeadListBuffer != null)
                {
                    _computeCmd.SetComputeRootSignature(desc.RootSignature);
                    _computeCmd.SetPipelineState(desc.EmitPSO);
                    BindComputeResources(_computeCmd, desc);
                    int emitGroups = Math.Max(1, (64 + desc.ThreadGroupSize - 1) / desc.ThreadGroupSize);
                    _computeCmd.Dispatch(emitGroups, 1, 1);
                }

                // 2. 模拟阶段
                _computeCmd.SetComputeRootSignature(desc.RootSignature);
                _computeCmd.SetPipelineState(desc.SimulatePSO);
                BindComputeResources(_computeCmd, desc);
                _computeCmd.Dispatch(desc.GroupCount, 1, 1);

                // 3. 剔除/紧致阶段
                if (desc.CullPSO != null)
                {
                    _computeCmd.SetPipelineState(desc.CullPSO);
                    _computeCmd.Dispatch(desc.GroupCount, 1, 1);
                }

                // 屏障转回通用读（确保模拟完成后渲染可读）
                if (desc.ParticleBuffer != null)
                    _computeCmd.TransitionTo(desc.ParticleBuffer, RHIResourceState.Common);
                if (desc.AliveListBuffer != null)
                    _computeCmd.TransitionTo(desc.AliveListBuffer, RHIResourceState.Common);
                if (desc.CounterBuffer != null)
                    _computeCmd.TransitionTo(desc.CounterBuffer, RHIResourceState.Common);
                if (desc.IndirectArgsBuffer != null)
                    _computeCmd.TransitionTo(desc.IndirectArgsBuffer, RHIResourceState.Common);
            }

            _computeCmd.Close();
            _computeQueue?.ExecuteCommandLists(new[] { _computeCmd });
            _computeQueue?.Flush();
        }

        /// <summary>绑定计算着色器资源。</summary>
        private static void BindComputeResources(RHICommandList cmd, FlowVFXGPUDispatchDesc desc)
        {
            if (desc.ParticleBuffer != null)
                cmd.SetComputeRootUnorderedAccessView(0, desc.ParticleBuffer.GpuVirtualAddress);
            if (desc.AliveListBuffer != null)
                cmd.SetComputeRootUnorderedAccessView(1, desc.AliveListBuffer.GpuVirtualAddress);
            if (desc.DeadListBuffer != null)
                cmd.SetComputeRootUnorderedAccessView(2, desc.DeadListBuffer.GpuVirtualAddress);
            if (desc.CounterBuffer != null)
                cmd.SetComputeRootUnorderedAccessView(3, desc.CounterBuffer.GpuVirtualAddress);
            if (desc.IndirectArgsBuffer != null)
                cmd.SetComputeRootUnorderedAccessView(4, desc.IndirectArgsBuffer.GpuVirtualAddress);
        }

        /// <summary>获取调度描述。</summary>
        public FlowVFXGPUDispatchDesc? GetDispatchDesc(string emitterName)
        {
            return _dispatchDescs.TryGetValue(emitterName, out var desc) ? desc : null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _parameterStore.Dispose();
            _computeCmd?.Dispose();
            _disposed = true;
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // 第九部分：系统运行时控制器
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 模拟路径选择。
    /// </summary>
    public enum FlowVFXSimulationPath
    {
        /// <summary>CPU 模拟（调试 fallback）。</summary>
        CPU,
        /// <summary>GPU Compute 模拟。</summary>
        GPUCompute,
        /// <summary>自动选择（有 GPU 时用 GPU，否则 CPU）。</summary>
        Auto
    }

    /// <summary>
    /// FlowVFX 系统运行时控制器。统一管理 CPU/GPU 双路径切换、
    /// 生命周期、事件、子发射器、参数存储。
    /// 这是 FlowVFX 粒子系统的最高层运行时入口。
    /// </summary>
    public class FlowVFXSystemRuntime : IDisposable
    {
        private readonly FlowVFXSystem _system;
        private readonly FlowVFXParameterStore _parameterStore;
        private FlowVFXCPUSimulator? _cpuSim;
        private FlowVFXGPUComputeDispatcher? _gpuDispatcher;
        private FlowVFXSimulationPath _path = FlowVFXSimulationPath.Auto;
        private RHIDevice? _device;
        private bool _disposed;

        /// <summary>系统名称。</summary>
        public string Name => _system.Name;
        /// <summary>模拟路径。</summary>
        public FlowVFXSimulationPath SimulationPath
        {
            get => _path;
            set
            {
                _path = value;
                UpdateActivePath();
            }
        }
        /// <summary>实际使用的模拟路径（Auto 解析后）。</summary>
        public FlowVFXSimulationPath ActivePath { get; private set; }
        /// <summary>参数存储。</summary>
        public FlowVFXParameterStore ParameterStore => _parameterStore;
        /// <summary>CPU 模拟器（可能为 null）。</summary>
        public FlowVFXCPUSimulator? CPUSimulator => _cpuSim;
        /// <summary>GPU 调度器（可能为 null）。</summary>
        public FlowVFXGPUComputeDispatcher? GPUDispatcher => _gpuDispatcher;
        /// <summary>是否正在播放。</summary>
        public bool IsPlaying => _cpuSim?.IsPlaying ?? false;
        /// <summary>系统世界位置。</summary>
        public Vector3 Position
        {
            get => _cpuSim?.Position ?? Vector3.Zero;
            set { if (_cpuSim != null) _cpuSim.Position = value; }
        }
        /// <summary>总存活粒子数。</summary>
        public int TotalAlive => _cpuSim?.TotalAlive ?? 0;

        /// <summary>构造。</summary>
        /// <param name="system">FlowVFX 系统配置</param>
        /// <param name="device">RHI 设备（null 时强制 CPU 路径）</param>
        public FlowVFXSystemRuntime(FlowVFXSystem system, RHIDevice? device = null)
        {
            _system = system;
            _device = device;
            _parameterStore = new FlowVFXParameterStore();

            // 始终创建 CPU 模拟器（作为 fallback 和调试工具）
            _cpuSim = new FlowVFXCPUSimulator(system, _parameterStore);

            // 有设备时创建 GPU 调度器
            if (device != null)
            {
                _gpuDispatcher = new FlowVFXGPUComputeDispatcher(device, _parameterStore);
            }

            UpdateActivePath();
        }

        /// <summary>更新活跃模拟路径。</summary>
        private void UpdateActivePath()
        {
            ActivePath = _path switch
            {
                FlowVFXSimulationPath.CPU => FlowVFXSimulationPath.CPU,
                FlowVFXSimulationPath.GPUCompute => _gpuDispatcher != null
                    ? FlowVFXSimulationPath.GPUCompute
                    : FlowVFXSimulationPath.CPU,
                FlowVFXSimulationPath.Auto => _gpuDispatcher != null
                    ? FlowVFXSimulationPath.GPUCompute
                    : FlowVFXSimulationPath.CPU,
                _ => FlowVFXSimulationPath.CPU
            };
        }

        /// <summary>初始化 GPU 资源（如果使用 GPU 路径）。</summary>
        public void InitializeGPU()
        {
            if (_gpuDispatcher != null && !_gpuDispatcher.IsInitialized)
                _gpuDispatcher.Initialize();
        }

        /// <summary>开始播放。</summary>
        public void Play()
        {
            _cpuSim?.Play();
        }

        /// <summary>停止播放。</summary>
        public void Stop()
        {
            _cpuSim?.Stop();
        }

        /// <summary>暂停。</summary>
        public void Pause() => _cpuSim?.Pause();

        /// <summary>恢复。</summary>
        public void Resume() => _cpuSim?.Resume();

        /// <summary>模拟一帧。</summary>
        /// <param name="dt">帧步长</param>
        /// <param name="cameraPos">相机位置</param>
        /// <param name="view">视图矩阵（GPU 路径用）</param>
        /// <param name="proj">投影矩阵（GPU 路径用）</param>
        public void Tick(float dt, Vector3 cameraPos, Matrix4x4 view = default, Matrix4x4 proj = default)
        {
            if (ActivePath == FlowVFXSimulationPath.GPUCompute && _gpuDispatcher != null)
            {
                // GPU 路径：同时运行 CPU 模拟作为调试镜像（可选）
                _gpuDispatcher.Tick(dt, cameraPos, view, proj);
                // CPU 也可同步运行（用于调试对比），但默认不运行
                // _cpuSim?.Tick(dt, cameraPos);
            }
            else
            {
                // CPU 路径
                _cpuSim?.Tick(dt, cameraPos);
            }
        }

        /// <summary>切换到 CPU 路径（调试用）。</summary>
        public void SwitchToCPU()
        {
            _path = FlowVFXSimulationPath.CPU;
            UpdateActivePath();
        }

        /// <summary>切换到 GPU 路径。</summary>
        public void SwitchToGPU()
        {
            _path = FlowVFXSimulationPath.GPUCompute;
            UpdateActivePath();
        }

        /// <summary>获取发射器运行时状态。</summary>
        public EmitterRuntimeState? GetEmitterState(string name)
        {
            return _cpuSim?.GetEmitterState(name);
        }

        /// <summary>获取全部发射器状态。</summary>
        public IReadOnlyList<EmitterRuntimeState> EmitterStates
        {
            get
            {
                if (_cpuSim != null) return _cpuSim.EmitterStates;
                return Array.Empty<EmitterRuntimeState>();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _cpuSim?.Dispose();
            _gpuDispatcher?.Dispose();
            _parameterStore.Dispose();
            _disposed = true;
        }
    }
}
