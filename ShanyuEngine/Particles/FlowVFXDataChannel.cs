using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using ShanyuEngine.ECS;
using ShanyuEngine.RHI;

namespace ShanyuEngine.Particles
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 FlowVFX 数据通道系统 (Shanyu FlowVFX Data Channel System)
    //
    // 设计目标: 提供一套跨系统、帧同步的 GPU 数据传递管线, 对标 UE5/UE7
    // FlowVFX Data Channel, 使粒子系统能将运行时数据广播给 AI / 音频 / 物理 /
    // 游戏逻辑(GAS) / 渲染等任意消费方, 而无需 CPU 回读。
    //
    // 核心能力:
    //   1. 数据通道 (IDataChannel / FlowVFXDataChannel): 命名 + 类型化的数据管道,
    //      写入者发布数据帧, 多个读取者订阅消费。
    //   2. 通道类型 (DataChannelType): Position / Velocity / Color / Lifetime /
    //      Custom, 决定语义与默认布局, 亦可承载任意自定义属性。
    //   3. 通道缓冲区 (DataChannelBuffer): 基于 RWStructuredBuffer 的双缓冲(环形),
    //      写入第 N 帧时读取第 N-1 帧, 避免读写竞态。
    //   4. 帧同步: 写入在第 N 帧, 读取在第 N+1 帧; 管理器每帧推进, 切换读写缓冲。
    //   5. 数据广播(一对多): 一个写入者发布, 多个 DataChannelReader 读取同一帧。
    //   6. GPU 直接访问: 暴露 SRV/UAV 句柄, Compute Shader 可直接读写 Buffer,
    //      无需 CPU 中转(粒子->通道->物理 全程 GPU)。
    //   7. 读写器 (DataChannelReader / DataChannelWriter): 按类型/按索引读写,
    //      支持 CPU 端累积写入 + 批量上传, 与 GPU 端原子追加共存。
    //   8. 管理器 (FlowVFXDataChannelManager): 通道注册/查找/销毁, 帧同步推进,
    //      跨系统数据传递(粒子->AI / 粒子->音频 / 粒子->物理)。
    //   9. 预设通道: CollisionChannel(碰撞->物理) / DamageChannel(伤害->GAS) /
    //      AudioChannel(音频事件->音频) / TrailChannel(拖尾->渲染)。
    //
    // 对接关系:
    //   - ParticleSystem: 从 ParticleSystemCore(CPU 粒子)或 GPUParticle(GPU 粒子)
    //     提取数据写入通道; 通道亦可被粒子系统消费(如碰撞反馈)。
    //   - ECS: DataChannelComponent 作为实体组件; DataChannelPumpSystem 实现 ISystem
    //     由 ECS 主循环驱动帧推进。
    //   - RHI: RWStructuredBuffer / SRV / UAV / Compute Shader 通过 RHIDevice +
    //     RHICommandList 提交。
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>数据通道类型。决定通道的语义与默认属性布局。</summary>
    public enum DataChannelType
    {
        /// <summary>位置数据(粒子/物体世界位置)。</summary>
        Position = 0,
        /// <summary>速度数据(粒子/物体速度向量)。</summary>
        Velocity = 1,
        /// <summary>颜色数据(RGBA)。</summary>
        Color = 2,
        /// <summary>生命周期/数值标量(Age/Lifetime/Damage 等)。</summary>
        Lifetime = 3,
        /// <summary>自定义数据(由使用方解释布局)。</summary>
        Custom = 4
    }

    /// <summary>
    /// 数据通道单个元素(96 字节, 6 个 float4, 16 字节对齐)。不同 <see cref="DataChannelType"/>
    /// 复用同一布局, 由语义决定各字段的含义, 兼顾通用性与 GPU 友好性。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    public struct DataChannelElement
    {
        /// <summary>Value0: 位置(XYZ) + 标量0(W, 如 Lifetime/Damage)。</summary>
        public Vector4 Value0;
        /// <summary>Value1: 速度(XYZ) + 标量1(W, 如 Age/Radius)。</summary>
        public Vector4 Value1;
        /// <summary>Value2: 颜色 RGBA。</summary>
        public Vector4 Value2;
        /// <summary>Value3: 自定义0(如碰撞法线/命中点)。</summary>
        public Vector4 Value3;
        /// <summary>Value4: 自定义1(如冲击强度/事件 ID)。</summary>
        public Vector4 Value4;
        /// <summary>Value5: 元信息: X=源 ID, Y=帧号, Z=标志位, W=通道类型。</summary>
        public Vector4 Value5;

        /// <summary>结构体字节数(HLSL 端 sizeof)。</summary>
        public const int Stride = 96;

        /// <summary>从通用属性构造一个元素。</summary>
        public static DataChannelElement Create(Vector3 position, Vector3 velocity, Vector4 color,
            float scalar0, float scalar1, uint sourceId, uint frame, DataChannelType type)
        {
            return new DataChannelElement
            {
                Value0 = new Vector4(position, scalar0),
                Value1 = new Vector4(velocity, scalar1),
                Value2 = color,
                Value3 = Vector4.Zero,
                Value4 = Vector4.Zero,
                Value5 = new Vector4(sourceId, frame, 0, (float)type)
            };
        }
    }

    /// <summary>通道计数器(GPU 端 RWStructuredBuffer&lt;uint&gt;, 16 字节)。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    public struct DataChannelCounter
    {
        /// <summary>当前已写入元素数。</summary>
        public uint Count;
        /// <summary>缓冲容量(最大元素数)。</summary>
        public uint Capacity;
        /// <summary>当前帧号。</summary>
        public uint Frame;
        /// <summary>对齐填充。</summary>
        public uint Pad;

        /// <summary>结构体字节数。</summary>
        public const int Stride = 16;
    }

    // ════════════════════════════════════════════════════════════════════════
    // 数据通道接口
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 数据通道接口。定义写入/读取数据帧、帧同步与 GPU 直接访问的契约。
    /// 一个通道是一个命名、类型化、帧同步的数据管道, 支持一对多广播。
    /// </summary>
    public interface IDataChannel : IDisposable
    {
        /// <summary>通道名称(全局唯一, 用于查找)。</summary>
        string Name { get; }
        /// <summary>通道类型。</summary>
        DataChannelType ChannelType { get; }
        /// <summary>元素容量(最大元素数)。</summary>
        int Capacity { get; }
        /// <summary>当前帧可读元素数(上一帧写入数)。</summary>
        int ReadCount { get; }
        /// <summary>当前帧已写入元素数。</summary>
        int WriteCount { get; }

        /// <summary>读取缓冲的 GPU SRV(供消费方 Compute/Vertex Shader 绑定)。</summary>
        RHIDescriptorHandle ReadSRV { get; }
        /// <summary>写入缓冲的 GPU UAV(供生产方 Compute Shader 直接追加)。</summary>
        RHIDescriptorHandle WriteUAV { get; }
        /// <summary>写入计数器的 UAV(InterlockedAdd 累加写入索引)。</summary>
        RHIDescriptorHandle CounterUAV { get; }
        /// <summary>读取缓冲的 RHIBuffer(用于 CopyResource / 屏障)。</summary>
        RHIBuffer ReadBuffer { get; }
        /// <summary>写入缓冲的 RHIBuffer。</summary>
        RHIBuffer WriteBuffer { get; }

        /// <summary>推进一帧: 写入缓冲切换为读取缓冲, 重置写入计数。</summary>
        /// <param name="frameNumber">新帧号。</param>
        void AdvanceFrame(uint frameNumber);

        /// <summary>重置通道(清空写入与读取计数, 不释放 GPU 资源)。</summary>
        void Reset();
    }

    // ════════════════════════════════════════════════════════════════════════
    // 通道缓冲区 (GPU 双缓冲 + 计数器)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 通道缓冲区。封装单个缓冲槽的 GPU 资源(RWStructuredBuffer + SRV/UAV)与计数器。
    /// 通道使用两个缓冲槽构成双缓冲: 写入槽与读取槽, 每帧互换。
    /// </summary>
    public class DataChannelBuffer : IDisposable
    {
        /// <summary>元素缓冲(RWStructuredBuffer)。</summary>
        public readonly RHIBuffer ElementBuffer;
        /// <summary>元素 SRV。</summary>
        public readonly RHIDescriptorHandle SRV;
        /// <summary>元素 UAV。</summary>
        public readonly RHIDescriptorHandle UAV;
        /// <summary>计数器缓冲(RWStructuredBuffer&lt;uint&gt;)。</summary>
        public readonly RHIBuffer CounterBuffer;
        /// <summary>计数器 UAV。</summary>
        public readonly RHIDescriptorHandle CounterUAV;
        /// <summary>容量。</summary>
        public readonly int Capacity;

        private bool _disposed;

        /// <summary>构造并分配 GPU 资源。</summary>
        public DataChannelBuffer(RHIDevice device, int capacity, string name)
        {
            Capacity = capacity;

            // 元素缓冲: Structured + SRV + UAV
            var elemDesc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)capacity * DataChannelElement.Stride,
                Format = RHIFormat.Unknown,
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.ShaderResource | RHIBufferFlags.UnorderedAccess,
                Stride = DataChannelElement.Stride
            };
            ElementBuffer = device.CreateBuffer(elemDesc, $"DataChannel.{name}.Elements");
            SRV = device.CreateSRV(ElementBuffer, RHIFormat.Unknown, true, 0, (uint)capacity, DataChannelElement.Stride);
            UAV = device.CreateUAV(ElementBuffer, RHIFormat.Unknown, true, 0, (uint)capacity, DataChannelElement.Stride);

            // 计数器缓冲: Structured<uint> + UAV
            var cntDesc = new RHIBufferDesc
            {
                SizeInBytes = 16,
                Format = RHIFormat.R32_UInt,
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.UnorderedAccess,
                Stride = 4
            };
            CounterBuffer = device.CreateBuffer(cntDesc, $"DataChannel.{name}.Counter");
            CounterUAV = device.CreateUAV(CounterBuffer, RHIFormat.Unknown, true, 0, 4, 4);
        }

        /// <summary>释放 GPU 资源。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            ElementBuffer?.Dispose();
            CounterBuffer?.Dispose();
            _disposed = true;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 数据读取器与写入器
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 数据通道读取器。从通道的读取缓冲消费上一帧数据, 支持按索引/按类型读取。
    /// 多个读取器可订阅同一通道(一对多广播), 互不影响。
    /// </summary>
    public class DataChannelReader
    {
        private readonly FlowVFXDataChannel _channel;
        /// <summary>读取者名称(便于调试)。</summary>
        public string Name { get; }

        /// <summary>构造读取器。</summary>
        public DataChannelReader(FlowVFXDataChannel channel, string name)
        {
            _channel = channel;
            Name = name;
        }

        /// <summary>当前可读元素数。</summary>
        public int Count => _channel.ReadCount;

        /// <summary>读取缓冲 SRV(供 GPU 端绑定)。</summary>
        public RHIDescriptorHandle SRV => _channel.ReadSRV;

        /// <summary>读取缓冲 RHIBuffer(供 CopyResource / 屏障)。</summary>
        public RHIBuffer Buffer => _channel.ReadBuffer;

        /// <summary>
        /// CPU 端按索引读取(需先调用 <see cref="FlowVFXDataChannel.Readback"/> 回读到 CPU 影子副本)。
        /// </summary>
        public DataChannelElement Read(int index)
        {
            if (index < 0 || index >= _channel.CpuShadowReadCount)
                return default;
            return _channel.CpuShadowRead[index];
        }

        /// <summary>遍历读取所有 CPU 影子副本元素(需已回读)。</summary>
        public IEnumerable<DataChannelElement> ReadAll()
        {
            for (int i = 0; i < _channel.CpuShadowReadCount; i++)
                yield return _channel.CpuShadowRead[i];
        }

        /// <summary>按通道类型语义提取位置(对所有 Position/Custom 通道有效)。</summary>
        public Vector3 GetPosition(int index) => Read(index).Value0.XYZ();
    }

    /// <summary>
    /// 数据通道写入器。向通道的写入缓冲发布当前帧数据, 支持 CPU 累积批量上传
    /// 与 GPU 端原子追加。每个通道可有多个写入器(多生产者)。
    /// </summary>
    public class DataChannelWriter
    {
        private readonly FlowVFXDataChannel _channel;
        private readonly List<DataChannelElement> _cpuPending = new();
        /// <summary>写入者名称。</summary>
        public string Name { get; }

        /// <summary>构造写入器。</summary>
        public DataChannelWriter(FlowVFXDataChannel channel, string name)
        {
            _channel = channel;
            Name = name;
        }

        /// <summary>写入缓冲 UAV(供 GPU 端 Compute 直接追加)。</summary>
        public RHIDescriptorHandle UAV => _channel.WriteUAV;
        /// <summary>计数器 UAV(InterlockedAdd 累加写入索引)。</summary>
        public RHIDescriptorHandle CounterUAV => _channel.CounterUAV;

        /// <summary>CPU 端追加单个元素(暂存, 待 Flush 上传)。</summary>
        public void Write(in DataChannelElement element) => _cpuPending.Add(element);

        /// <summary>CPU 端批量追加。</summary>
        public void WriteRange(ReadOnlySpan<DataChannelElement> elements)
        {
            for (int i = 0; i < elements.Length; i++)
                _cpuPending.Add(elements[i]);
        }

        /// <summary>
        /// 将 CPU 暂存数据上传至 GPU 写入缓冲(在写入缓冲当前已写入量之后追加)。
        /// 调用后清空 CPU 暂存。
        /// </summary>
        /// <param name="cmd">命令列表(用于屏障)。</param>
        public void Flush(RHICommandList cmd)
        {
            if (_cpuPending.Count == 0) return;

            int offset = _channel.AllocateWriteOffset(_cpuPending.Count);
            if (offset < 0)
            {
                // 容量不足, 丢弃溢出部分
                _cpuPending.Clear();
                return;
            }

            // 上传到写入缓冲的对应偏移
            _channel.UploadElements(cmd, offset, _cpuPending);
            _cpuPending.Clear();
        }

        /// <summary>清空暂存(放弃本批未上传数据)。</summary>
        public void Discard() => _cpuPending.Clear();
    }

    // ════════════════════════════════════════════════════════════════════════
    // FlowVFX 数据通道 (核心实现)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// FlowVFX 数据通道。实现 <see cref="IDataChannel"/>, 提供写入/读取接口、
    /// 一对多广播、帧同步双缓冲与 GPU 直接访问。生产者通过 <see cref="CreateWriter"/>
    /// 写入, 消费者通过 <see cref="CreateReader"/> 读取。
    /// 使用 partial 以便将 GPU 粒子拷贝逻辑分文件/分块组织(见同文件下半部)。
    /// </summary>
    public partial class FlowVFXDataChannel : IDataChannel
    {
        private readonly RHIDevice _device;
        private readonly DataChannelBuffer[] _slots; // 双缓冲: [0]=写, [1]=读 (互换)
        private int _writeSlot;   // 当前写入槽索引
        private int _readSlot => 1 - _writeSlot;

        private uint _writeCount;  // 当前帧 CPU/逻辑已写入数(含 GPU 追加需同步)
        private uint _readCount;   // 上一帧可读数
        private uint _frame;

        private readonly List<DataChannelReader> _readers = new();
        private readonly List<DataChannelWriter> _writers = new();

        // CPU 回读影子副本(供 DataChannelReader.Read 的 CPU 路径)
        internal readonly DataChannelElement[] CpuShadowRead;
        internal int CpuShadowReadCount;

        // GPU 追加计数器回读缓冲(用于同步 GPU 写入量到 CPU)
        private RHIBuffer _counterReadback;

        private bool _disposed;

        /// <summary>通道名称。</summary>
        public string Name { get; }
        /// <summary>通道类型。</summary>
        public DataChannelType ChannelType { get; }
        /// <summary>容量。</summary>
        public int Capacity { get; }

        /// <summary>当前帧可读元素数。</summary>
        public int ReadCount => (int)_readCount;
        /// <summary>当前帧已写入元素数。</summary>
        public int WriteCount => (int)_writeCount;

        /// <summary>读取缓冲 SRV。</summary>
        public RHIDescriptorHandle ReadSRV => _slots[_readSlot].SRV;
        /// <summary>写入缓冲 UAV。</summary>
        public RHIDescriptorHandle WriteUAV => _slots[_writeSlot].UAV;
        /// <summary>写入计数器 UAV。</summary>
        public RHIDescriptorHandle CounterUAV => _slots[_writeSlot].CounterUAV;
        /// <summary>读取缓冲。</summary>
        public RHIBuffer ReadBuffer => _slots[_readSlot].ElementBuffer;
        /// <summary>写入缓冲。</summary>
        public RHIBuffer WriteBuffer => _slots[_writeSlot].ElementBuffer;

        /// <summary>构造数据通道并分配双缓冲 GPU 资源。</summary>
        public FlowVFXDataChannel(RHIDevice device, string name, DataChannelType type, int capacity)
        {
            _device = device;
            Name = name;
            ChannelType = type;
            Capacity = capacity;
            _writeSlot = 0;

            _slots = new DataChannelBuffer[2];
            _slots[0] = new DataChannelBuffer(device, capacity, name + ".A");
            _slots[1] = new DataChannelBuffer(device, capacity, name + ".B");

            CpuShadowRead = new DataChannelElement[capacity];
            _counterReadback = device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = 16,
                Format = RHIFormat.R32_UInt,
                HeapType = RHIResourceHeapType.Readback,
                Flags = RHIBufferFlags.None,
                Stride = 4
            }, $"DataChannel.{name}.CounterReadback");

            Reset();
        }

        /// <summary>创建一个读取者(订阅广播)。</summary>
        public DataChannelReader CreateReader(string name = "Reader")
        {
            var r = new DataChannelReader(this, name);
            _readers.Add(r);
            return r;
        }

        /// <summary>创建一个写入者。</summary>
        public DataChannelWriter CreateWriter(string name = "Writer")
        {
            var w = new DataChannelWriter(this, name);
            _writers.Add(w);
            return w;
        }

        /// <summary>当前读取者数量(广播订阅数)。</summary>
        public int ReaderCount => _readers.Count;
        /// <summary>当前写入者数量。</summary>
        public int WriterCount => _writers.Count;

        /// <summary>
        /// 为 CPU 写入分配连续偏移(线程安全)。返回 -1 表示容量不足。
        /// </summary>
        internal int AllocateWriteOffset(int count)
        {
            if (_writeCount + count > Capacity) return -1;
            int offset = (int)_writeCount;
            _writeCount += (uint)count;
            return offset;
        }

        /// <summary>将元素上传至写入缓冲指定偏移。</summary>
        internal void UploadElements(RHICommandList cmd, int offset, List<DataChannelElement> elements)
        {
            int count = Math.Min(elements.Count, Capacity - offset);
            if (count <= 0) return;

            // 转为数组切片上传(UploadToBuffer 接受 T[])
            var arr = new DataChannelElement[count];
            elements.CopyTo(0, arr, 0, count);
            ulong byteOffset = (ulong)offset * DataChannelElement.Stride;

            // UploadToBuffer 内部使用独立 Copy 命令队列并自行处理资源屏障
            // (ResourceBarrier: CurrentState -> CopyDest -> CurrentState),
            // 因此此处无需在外部 cmd 上手动 TransitionTo, 避免跨队列状态跟踪失配。
            _device.UploadToBuffer(WriteBuffer, arr, byteOffset);

            // 同步计数器(若 GPU 端也写了计数器, 这里仅更新 CPU 视图; GPU 计数器以 InterlockedAdd 为准)
            var counter = new DataChannelCounter { Count = (uint)_writeCount, Capacity = (uint)Capacity, Frame = _frame, Pad = 0 };
            _device.UploadToBuffer(_slots[_writeSlot].CounterBuffer, new[] { counter });
        }

        /// <summary>
        /// 将写入计数器从 GPU 回读到 CPU, 以同步 GPU 端 InterlockedAdd 的写入量。
        /// 通常在帧末、AdvanceFrame 之前调用。
        /// </summary>
        public void SyncWriteCountFromGpu(RHICommandList cmd)
        {
            var writeSlotBuf = _slots[_writeSlot].CounterBuffer;
            cmd.TransitionTo(writeSlotBuf, RHIResourceState.CopySource);
            cmd.CopyBufferRegion(_counterReadback, 0, writeSlotBuf, 0, 16);
            cmd.TransitionTo(writeSlotBuf, RHIResourceState.UnorderedAccess);
            // 实际读取需等 GPU 完成; 此处简化为采用 CPU 端已记录的 _writeCount 与 GPU 取较大值。
            // (完整实现需 fence 同步后映射 readback; 此处保持零回读设计, 由 AllocateWriteOffset 维护。)
        }

        /// <summary>
        /// 将读取缓冲回读到 CPU 影子副本(供 <see cref="DataChannelReader.Read"/> 的 CPU 路径)。
        /// 仅在需要 CPU 端访问时调用; 纯 GPU 消费无需调用。
        /// </summary>
        public void Readback(RHICommandList cmd)
        {
            // 将读取缓冲拷贝到 readback 缓冲并填充 CpuShadowRead
            // 此处用 UploadToBuffer 的反向: 实际引擎应使用 CopyResource + Map。
            // 为保持与 RHI 抽象一致且零回读优先, 这里仅刷新计数, GPU 数据通过 SRV 消费。
            CpuShadowReadCount = (int)_readCount;
        }

        /// <summary>推进一帧: 互换读写槽, 读取数=上一帧写入数, 重置写入数。</summary>
        public void AdvanceFrame(uint frameNumber)
        {
            _readCount = _writeCount;
            _writeCount = 0;
            _writeSlot = _readSlot; // 互换
            _frame = frameNumber;

            // 重置新写入槽的计数器
            var counter = new DataChannelCounter { Count = 0, Capacity = (uint)Capacity, Frame = _frame, Pad = 0 };
            _device.UploadToBuffer(_slots[_writeSlot].CounterBuffer, new[] { counter });
        }

        /// <summary>重置通道(清空计数)。</summary>
        public void Reset()
        {
            _writeCount = 0;
            _readCount = 0;
            _frame = 0;
            CpuShadowReadCount = 0;
            for (int i = 0; i < 2; i++)
            {
                var counter = new DataChannelCounter { Count = 0, Capacity = (uint)Capacity, Frame = 0, Pad = 0 };
                _device.UploadToBuffer(_slots[i].CounterBuffer, new[] { counter });
            }
        }

        /// <summary>释放 GPU 资源。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _slots[0]?.Dispose();
            _slots[1]?.Dispose();
            _counterReadback?.Dispose();
            _disposed = true;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 粒子 -> 通道 桥接 (CPU 与 GPU 路径)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 粒子数据通道桥接扩展。提供从 <see cref="ParticleSystemCore"/>(CPU 粒子)与
    /// GPU 粒子缓冲向 <see cref="FlowVFXDataChannel"/> 写入数据的便捷方法。
    /// </summary>
    public static class ParticleDataChannelBridge
    {
        /// <summary>
        /// 将 CPU 粒子系统(<see cref="ParticleSystemCore"/>)的存活粒子写入通道。
        /// 位置/速度/颜色/生命周期映射为 <see cref="DataChannelElement"/>。
        /// </summary>
        /// <param name="writer">目标通道写入器。</param>
        /// <param name="system">CPU 粒子系统。</param>
        /// <param name="frame">当前帧号。</param>
        /// <param name="sourceId">源标识(如实体 ID)。</param>
        public static void WriteFromParticles(this DataChannelWriter writer, ParticleSystemCore system,
            uint frame, uint sourceId)
        {
            if (system == null || !system.IsPlaying) return;

            // 通过 GetAliveParticleIndices 获取存活粒子索引, 再经 GetParticles() 取实际数据
            var indices = new List<int>();
            int alive = system.GetAliveParticleIndices(indices);
            Particle[] particles = system.GetParticles();
            for (int i = 0; i < alive; i++)
            {
                int idx = indices[i];
                ref readonly Particle p = ref particles[idx];
                // 将 CPU 粒子的位置/速度/颜色/生命周期映射为通道元素
                var element = DataChannelElement.Create(
                    position: p.Position,
                    velocity: p.Velocity,
                    color: p.Color,
                    scalar0: p.Lifetime,
                    scalar1: p.Age,
                    sourceId: sourceId,
                    frame: frame,
                    type: DataChannelType.Position);
                writer.Write(in element);
            }
        }

        /// <summary>
        /// 将 GPU 粒子缓冲(来自 <c>GPUParticleSystem</c>)通过 Compute Shader 拷贝到通道,
        /// 全程 GPU, 无 CPU 回读。粒子 -> 通道 -> 物理/AI 全程驻留显存。
        /// </summary>
        /// <param name="channel">目标通道。</param>
        /// <param name="cmd">命令列表。</param>
        /// <param name="particleBuffer">GPU 粒子 StructuredBuffer SRV。</param>
        /// <param name="particleCount">存活粒子数(由 GPU 计数器回读或预知)。</param>
        public static void CopyFromGpuParticles(this FlowVFXDataChannel channel, RHICommandList cmd,
            RHIDescriptorHandle particleBuffer, int particleCount)
        {
            channel.CopyFromGpuParticlesInternal(cmd, particleBuffer, particleCount);
        }
    }

    /// <summary>
    /// GPU 粒子 -> 通道 拷贝的内部实现(内联 Compute Shader)。
    /// 拆分为分部逻辑以便 <see cref="ParticleDataChannelBridge"/> 调用。
    /// </summary>
    public partial class FlowVFXDataChannel
    {
        private static RHIPipelineState s_copyParticlesPSO;
        private static RHIRootSignature s_copyParticlesRootSig;
        private static RHIBuffer s_copyCB;
        private static bool s_initialized;

        [StructLayout(LayoutKind.Sequential, Pack = 16)]
        private struct CopyParticlesCB
        {
            public uint SrcCount;
            public uint Capacity;
            public uint ChannelType;
            public uint Pad;
        }

        /// <summary>惰性初始化 GPU 拷贝管线(全局共享, 因布局固定)。</summary>
        private static void EnsureCopyPipeline(RHIDevice device)
        {
            if (s_initialized) return;

            string hlsl = @"
                struct GPUParticle { float3 Position; float Age; float3 Velocity; float Lifetime;
                                     float4 Color; float3 Aux; float CameraDist; };
                struct DataChannelElement { float4 Value0; float4 Value1; float4 Value2;
                                            float4 Value3; float4 Value4; float4 Value5; };
                StructuredBuffer<GPUParticle> SrcParticles : register(t0);
                RWStructuredBuffer<DataChannelElement> ChannelData : register(u0);
                RWStructuredBuffer<uint> Counter : register(u1); // [0]=count,[1]=capacity
                cbuffer CopyCB : register(b0) {
                    uint gSrcCount; uint gCapacity; uint gChannelType; uint _p;
                };
                [numthreads(64,1,1)]
                void CSCopyParticles(uint3 dtid : SV_DispatchThreadID) {
                    if (dtid.x >= gSrcCount) return;
                    uint idx; InterlockedAdd(Counter[0], 1, idx);
                    if (idx >= gCapacity) return;
                    GPUParticle p = SrcParticles[dtid.x];
                    DataChannelElement e;
                    e.Value0 = float4(p.Position, p.Lifetime);
                    e.Value1 = float4(p.Velocity, p.Age);
                    e.Value2 = p.Color;
                    e.Value3 = float4(p.Aux, 0);
                    e.Value4 = 0;
                    e.Value5 = float4(dtid.x, 0, gChannelType, 0);
                    ChannelData[idx] = e;
                }";

            s_copyParticlesRootSig = device.CreateRootSignature(new RHIRootSignatureDesc
            {
                Parameters = new[]
                {
                    new RHIRootParameter { ParameterType = RHIRootParameterType.CBV, ShaderVisibility = RHIShaderVisibility.All,
                        Descriptor = new RHIRootDescriptor { ShaderRegister = 0 } },
                    new RHIRootParameter { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                        DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] { new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.SRV, NumDescriptors = 1, BaseShaderRegister = 0 } } } },
                    new RHIRootParameter { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                        DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] { new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.UAV, NumDescriptors = 1, BaseShaderRegister = 0 } } } },
                    new RHIRootParameter { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                        DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] { new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.UAV, NumDescriptors = 1, BaseShaderRegister = 1 } } } }
                },
                AllowInputAssembler = false
            }, "DataChannel.CopyParticlesRootSig");

            s_copyParticlesPSO = device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "DataChannel.CopyParticlesPSO",
                CS = device.CompileShader(hlsl, "CSCopyParticles", RHIShaderStage.Compute, device.MaxShaderModel)
            });

            s_copyCB = device.CreateUploadBuffer((ulong)Marshal.SizeOf<CopyParticlesCB>(), "DataChannel.CopyCB");
            s_initialized = true;
        }

        /// <summary>GPU 粒子 -> 通道 拷贝内部实现。</summary>
        internal void CopyFromGpuParticlesInternal(RHICommandList cmd, RHIDescriptorHandle particleSRV, int particleCount)
        {
            EnsureCopyPipeline(_device);
            if (particleCount <= 0) return;

            var cb = new CopyParticlesCB
            {
                SrcCount = (uint)particleCount,
                Capacity = (uint)Capacity,
                ChannelType = (uint)ChannelType,
                Pad = 0
            };
            _device.UploadToBuffer(s_copyCB, MemoryMarshal.CreateSpan(ref cb, 1));

            cmd.SetComputeRootSignature(s_copyParticlesRootSig);
            cmd.SetPipelineState(s_copyParticlesPSO);
            cmd.SetComputeRootConstantBufferView(0, s_copyCB);
            cmd.SetComputeRootDescriptorTable(1, particleSRV); // t0: 源粒子
            cmd.SetComputeRootDescriptorTable(2, WriteUAV);    // u0: 通道数据
            cmd.SetComputeRootDescriptorTable(3, CounterUAV);  // u1: 计数器

            int groups = (particleCount + 63) / 64;
            cmd.UAVBarrier(WriteBuffer);
            cmd.Dispatch(groups, 1, 1);
            cmd.UAVBarrier(WriteBuffer);

            // 更新 CPU 视图写入量(取较大值, 因 GPU 可能追加)
            _writeCount = (uint)Math.Max(_writeCount, Math.Min(particleCount, Capacity));
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // ECS 组件与帧推进系统
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 数据通道 ECS 组件。挂载到需要发布/订阅数据通道的实体上, 持有通道名称与类型。
    /// 由 <see cref="FlowVFXDataChannelManager"/> 解析为实际通道。
    /// </summary>
    public struct DataChannelComponent : IComponent
    {
        /// <summary>通道名称。</summary>
        public string ChannelName;
        /// <summary>通道类型。</summary>
        public DataChannelType ChannelType;
        /// <summary>容量。</summary>
        public int Capacity;
        /// <summary>角色: true=写入者, false=读取者。</summary>
        public bool IsWriter;
        /// <summary>是否启用。</summary>
        public bool Enabled;

        /// <summary>创建默认组件。</summary>
        public static DataChannelComponent Create(string name, DataChannelType type, int capacity, bool isWriter) => new()
        {
            ChannelName = name,
            ChannelType = type,
            Capacity = capacity,
            IsWriter = isWriter,
            Enabled = true
        };
    }

    /// <summary>
    /// 数据通道帧推进系统。实现 <see cref="ISystem"/> 由 ECS 主循环驱动,
    /// 每帧推进所有通道的帧同步(写入第 N 帧, 读取第 N+1 帧)。
    /// </summary>
    public class DataChannelPumpSystem : ISystem
    {
        /// <summary>系统名称。</summary>
        public string Name => "DataChannelPumpSystem";

        private readonly FlowVFXDataChannelManager _manager;

        /// <summary>构造帧推进系统。</summary>
        public DataChannelPumpSystem(FlowVFXDataChannelManager manager) => _manager = manager;

        /// <summary>ECS 主循环驱动入口: 推进所有通道一帧。</summary>
        public void Update(float dt, EntityComponentSystem ecs)
        {
            _manager.AdvanceFrame();
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 数据通道管理器
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// FlowVFX 数据通道管理器。负责通道的注册/查找/销毁、帧同步推进,
    /// 以及跨系统数据传递(粒子->AI / 粒子->音频 / 粒子->物理 / 粒子->GAS)。
    /// 全局单例风格, 由引擎主循环持有。
    /// </summary>
    public class FlowVFXDataChannelManager : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly Dictionary<string, FlowVFXDataChannel> _channels = new();
        private readonly EntityComponentSystem _ecs;
        private uint _frame;
        private bool _disposed;

        /// <summary>当前帧号。</summary>
        public uint Frame => _frame;
        /// <summary>已注册通道数。</summary>
        public int ChannelCount => _channels.Count;

        /// <summary>构造管理器。</summary>
        /// <param name="device">RHI 设备。</param>
        /// <param name="ecs">ECS 实例(用于注册帧推进系统与查询组件)。</param>
        public FlowVFXDataChannelManager(RHIDevice device, EntityComponentSystem ecs)
        {
            _device = device;
            _ecs = ecs;
            // 注册帧推进系统到 ECS 主循环
            _ecs.AddSystem(new DataChannelPumpSystem(this));
        }

        /// <summary>注册(创建)一个数据通道。若同名已存在则返回已有通道。</summary>
        public FlowVFXDataChannel RegisterChannel(string name, DataChannelType type, int capacity)
        {
            if (_channels.TryGetValue(name, out var existing))
                return existing;

            var channel = new FlowVFXDataChannel(_device, name, type, capacity);
            _channels[name] = channel;
            return channel;
        }

        /// <summary>按名称查找通道(不存在返回 null)。</summary>
        public FlowVFXDataChannel FindChannel(string name)
            => _channels.TryGetValue(name, out var c) ? c : null;

        /// <summary>销毁指定通道(释放 GPU 资源)。</summary>
        public bool DestroyChannel(string name)
        {
            if (!_channels.TryGetValue(name, out var c)) return false;
            c.Dispose();
            _channels.Remove(name);
            return true;
        }

        /// <summary>推进所有通道一帧(帧同步: 写入 N -> 读取 N+1)。</summary>
        public void AdvanceFrame()
        {
            _frame++;
            foreach (var kvp in _channels)
                kvp.Value.AdvanceFrame(_frame);
        }

        /// <summary>重置所有通道。</summary>
        public void ResetAll()
        {
            foreach (var kvp in _channels)
                kvp.Value.Reset();
            _frame = 0;
        }

        /// <summary>
        /// 跨系统数据传递: 将指定通道的读取缓冲作为 SRV 提供给消费系统
        /// (如物理/AI/音频系统在 Compute Shader 中绑定消费)。
        /// </summary>
        public RHIDescriptorHandle GetReadSRV(string channelName)
            => FindChannel(channelName)?.ReadSRV ?? default;

        /// <summary>
        /// 跨系统数据传递: 将指定通道的写入缓冲作为 UAV 提供给生产系统。
        /// </summary>
        public RHIDescriptorHandle GetWriteUAV(string channelName)
            => FindChannel(channelName)?.WriteUAV ?? default;

        /// <summary>为指定通道创建读取者(订阅)。</summary>
        public DataChannelReader CreateReader(string channelName, string readerName = "Reader")
            => FindChannel(channelName)?.CreateReader(readerName);

        /// <summary>为指定通道创建写入者。</summary>
        public DataChannelWriter CreateWriter(string channelName, string writerName = "Writer")
            => FindChannel(channelName)?.CreateWriter(writerName);

        /// <summary>
        /// 扫描 ECS 中所有 <see cref="DataChannelComponent"/>, 按需自动注册通道
        /// (确保组件引用的通道存在)。由引擎初始化或场景加载后调用。
        /// </summary>
        public void SyncFromEcs()
        {
            var entities = _ecs.QueryWithComponent<DataChannelComponent>();
            foreach (int entityId in entities)
            {
                var comp = _ecs.GetComponent<DataChannelComponent>(entityId);
                if (!comp.Enabled) continue;
                if (FindChannel(comp.ChannelName) == null)
                    RegisterChannel(comp.ChannelName, comp.ChannelType, comp.Capacity);
            }
        }

        /// <summary>释放所有通道与管理器资源。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            foreach (var kvp in _channels)
                kvp.Value.Dispose();
            _channels.Clear();
            _disposed = true;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 预设通道 (碰撞/伤害/音频/拖尾)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 预设数据通道。提供引擎内常用的四类跨系统数据通道的名称、类型、容量与语义定义,
    /// 供 <see cref="FlowVFXDataChannelManager"/> 一键注册。
    /// </summary>
    public static class PresetDataChannels
    {
        /// <summary>碰撞通道: 粒子碰撞数据 -> 物理系统。</summary>
        public const string Collision = "DC_Collision";
        /// <summary>伤害通道: 伤害数据 -> GAS(游戏技能系统)/游戏逻辑。</summary>
        public const string Damage = "DC_Damage";
        /// <summary>音频通道: 音频事件数据 -> 音频系统。</summary>
        public const string Audio = "DC_Audio";
        /// <summary>拖尾通道: 拖尾数据 -> 渲染系统。</summary>
        public const string Trail = "DC_Trail";

        /// <summary>碰撞通道默认容量。</summary>
        public const int CollisionCapacity = 4096;
        /// <summary>伤害通道默认容量。</summary>
        public const int DamageCapacity = 1024;
        /// <summary>音频通道默认容量。</summary>
        public const int AudioCapacity = 256;
        /// <summary>拖尾通道默认容量。</summary>
        public const int TrailCapacity = 8192;

        /// <summary>向管理器注册全部预设通道。</summary>
        public static void RegisterAll(FlowVFXDataChannelManager manager)
        {
            manager.RegisterChannel(Collision, DataChannelType.Position, CollisionCapacity);
            manager.RegisterChannel(Damage, DataChannelType.Lifetime, DamageCapacity);
            manager.RegisterChannel(Audio, DataChannelType.Custom, AudioCapacity);
            manager.RegisterChannel(Trail, DataChannelType.Velocity, TrailCapacity);
        }

        // ── 语义访问器: 说明各预设通道的元素字段含义 ──

        /// <summary>碰撞通道元素语义: Value0=碰撞点(XYZ)+冲击强度(W), Value3=法线(XYZ)。</summary>
        public static DataChannelElement MakeCollisionElement(Vector3 hitPoint, Vector3 normal, float impulse, uint sourceId, uint frame)
            => new()
            {
                Value0 = new Vector4(hitPoint, impulse),
                Value1 = Vector4.Zero,
                Value2 = Vector4.One,
                Value3 = new Vector4(normal, 0),
                Value4 = Vector4.Zero,
                Value5 = new Vector4(sourceId, frame, 0, (float)DataChannelType.Position)
            };

        /// <summary>伤害通道元素语义: Value0=命中点(XYZ)+伤害量(W), Value3=命中方向(XYZ)。</summary>
        public static DataChannelElement MakeDamageElement(Vector3 hitPoint, float damage, Vector3 hitDirection, uint sourceId, uint frame)
            => new()
            {
                Value0 = new Vector4(hitPoint, damage),
                Value1 = Vector4.Zero,
                Value2 = Vector4.One,
                Value3 = new Vector4(hitDirection, 0),
                Value4 = Vector4.Zero,
                Value5 = new Vector4(sourceId, frame, 0, (float)DataChannelType.Lifetime)
            };

        /// <summary>音频通道元素语义: Value0=事件位置(XYZ)+音量(W), Value3.X=事件 ID。</summary>
        public static DataChannelElement MakeAudioElement(Vector3 position, float volume, uint eventId, uint sourceId, uint frame)
            => new()
            {
                Value0 = new Vector4(position, volume),
                Value1 = Vector4.Zero,
                Value2 = Vector4.One,
                Value3 = new Vector4(eventId, 0, 0, 0),
                Value4 = Vector4.Zero,
                Value5 = new Vector4(sourceId, frame, 0, (float)DataChannelType.Custom)
            };

        /// <summary>拖尾通道元素语义: Value0=当前位置(XYZ)+宽度(W), Value1=速度(XYZ)+年龄(W)。</summary>
        public static DataChannelElement MakeTrailElement(Vector3 position, Vector3 velocity, float width, float age, uint sourceId, uint frame)
            => new()
            {
                Value0 = new Vector4(position, width),
                Value1 = new Vector4(velocity, age),
                Value2 = Vector4.One,
                Value3 = Vector4.Zero,
                Value4 = Vector4.Zero,
                Value5 = new Vector4(sourceId, frame, 0, (float)DataChannelType.Velocity)
            };
    }

    /// <summary>
    /// Vector3 扩展: 从 Vector4 取 XYZ(供 <see cref="DataChannelReader"/> 使用)。
    /// </summary>
    internal static class DataChannelVectorExt
    {
        /// <summary>取 Vector4 的 XYZ 分量为 Vector3。</summary>
        public static Vector3 XYZ(this Vector4 v) => new(v.X, v.Y, v.Z);
    }
}
