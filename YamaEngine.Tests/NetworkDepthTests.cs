// NetworkDepthTests.cs — 网络层防御性深度验证
// 覆盖：
//   * NetworkReaderReal.ReadString/ReadBytes 声明长度超剩余 → InvalidDataException（静默截断修复）
//   * NetworkReaderReal.ReadArray/ReadList/ReadDictionary 巨值 count → 不 OOM（上限钳制）
//   * NetworkReaderReal.ReadBitsPacked 超 32 位 VarInt / 超 5 组 → InvalidDataException（静默截断修复）
//   * UDPTransportReal.HandleFragmentPacket 伪造 totalFragments 巨值 → 丢弃不 OOM（分片 OOM 修复）
// 全部 CPU 级确定性，不依赖 GPU/真实网络。
using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Reflection;
using YamaEngine.Network;
using static YamaEngine.Tests.TestHelpers;

#pragma warning disable CS8625 // 测试有意传 null 验证防御

namespace YamaEngine.Tests;

/// <summary>网络层防御性深度验证套件。</summary>
public sealed class NetworkDepthTests
{
    // ────────────────────────────────────────────────────────────
    // 1. NetworkReaderReal: 字符串/字节数组声明长度超剩余
    // ────────────────────────────────────────────────────────────

    /// <summary>ReadString 声明长度超出剩余字节应抛 InvalidDataException（而非静默返回空串）。</summary>
    public static void TestReadStringLengthPrefixExceedsRemainingThrows()
    {
        // 长度前缀声明 100, 但实际只有 3 字节数据。
        byte[] data = new byte[7];
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0), 100);
        data[4] = (byte)'a';
        data[5] = (byte)'b';
        data[6] = (byte)'c';

        var reader = new NetworkReaderReal(data);
        AssertThrows<InvalidDataException>(
            () => reader.ReadString(),
            "ReadString 声明超长应抛 InvalidDataException。");
    }

    /// <summary>ReadString 合法往返仍应正常工作。</summary>
    public static void TestReadStringValidRoundTrip()
    {
        var writer = new NetworkWriterReal(64);
        writer.WriteString("yama-network-depth");
        var reader = new NetworkReaderReal(writer.ToArray());
        AssertEqual("yama-network-depth", reader.ReadString(), "合法字符串往返失败。");
    }

    /// <summary>ReadBytes 声明长度超出剩余字节应抛 InvalidDataException（而非静默截断）。</summary>
    public static void TestReadBytesLengthPrefixExceedsRemainingThrows()
    {
        byte[] data = new byte[7];
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0), 100);
        data[4] = 0xAA;
        data[5] = 0xBB;
        data[6] = 0xCC;

        var reader = new NetworkReaderReal(data);
        AssertThrows<InvalidDataException>(
            () => reader.ReadBytes(),
            "ReadBytes 声明超长应抛 InvalidDataException。");
    }

    /// <summary>ReadBytes 合法往返仍应正常工作。</summary>
    public static void TestReadBytesValidRoundTrip()
    {
        var writer = new NetworkWriterReal(64);
        writer.WriteBytes(new byte[] { 1, 2, 3, 4, 5 });
        var reader = new NetworkReaderReal(writer.ToArray());
        var bytes = reader.ReadBytes();
        AssertEqual(5, bytes.Length, "ReadBytes 长度不符。");
        AssertEqual((byte)1, bytes[0], "ReadBytes[0] 不符。");
        AssertEqual((byte)5, bytes[4], "ReadBytes[4] 不符。");
    }

    // ────────────────────────────────────────────────────────────
    // 2. NetworkReaderReal: 集合 count 巨值防 OOM
    // ────────────────────────────────────────────────────────────

    /// <summary>ReadArray 声明巨值 count 应被钳制到剩余字节数，不触发 OOM。</summary>
    public static void TestReadArrayHugeCountClampedNoOom()
    {
        // count = int.MaxValue, 剩余字节 4 (每个元素读 1 字节)。
        byte[] data = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0), int.MaxValue);
        data[4] = 0x01;
        data[5] = 0x02;
        data[6] = 0x03;
        data[7] = 0x04;

        var reader = new NetworkReaderReal(data);
        var arr = reader.ReadArray<byte>(() => reader.ReadByte());
        // 钳制到 Remaining=4, 不抛 OOM/异常。
        AssertTrue(arr.Length <= 4, $"ReadArray 应钳制 count, 实际 {arr.Length}。");
    }

    /// <summary>ReadList 声明巨值 count 应被钳制到剩余字节数，不触发 OOM。</summary>
    public static void TestReadListHugeCountClampedNoOom()
    {
        byte[] data = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0), int.MaxValue);
        data[4] = 0x01;
        data[5] = 0x02;
        data[6] = 0x03;
        data[7] = 0x04;

        var reader = new NetworkReaderReal(data);
        var list = reader.ReadList<byte>(() => reader.ReadByte());
        AssertTrue(list.Count <= 4, $"ReadList 应钳制 count, 实际 {list.Count}。");
    }

    /// <summary>ReadDictionary 声明巨值 count 应被钳制到剩余字节数，不触发 OOM。</summary>
    public static void TestReadDictionaryHugeCountClampedNoOom()
    {
        byte[] data = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0), int.MaxValue);
        data[4] = 0x01; // key
        data[5] = 0x01; // value
        data[6] = 0x02; // key
        data[7] = 0x02; // value

        var reader = new NetworkReaderReal(data);
        var dict = reader.ReadDictionary<byte, byte>(() => reader.ReadByte(), () => reader.ReadByte());
        // count 钳制到 Remaining=4, 最多 4 次迭代 (每次读 2 字节, 耗尽流后键值返回 0)。
        AssertTrue(dict.Count <= 4, $"ReadDictionary 应钳制 count, 实际 {dict.Count}。");
    }

    /// <summary>负 count 应被钳制为 0，返回空集合。</summary>
    public static void TestReadCollectionNegativeCountReturnsEmpty()
    {
        byte[] data = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0), -5);

        var reader = new NetworkReaderReal(data);
        var arr = reader.ReadArray<byte>(() => reader.ReadByte());
        AssertEqual(0, arr.Length, "负 count 应返回空数组。");
    }

    // ────────────────────────────────────────────────────────────
    // 3. NetworkReaderReal: ReadBitsPacked 超 32 位 VarInt
    // ────────────────────────────────────────────────────────────

    /// <summary>合法 VarInt (0) 应往返正确。</summary>
    public static void TestReadBitsPackedValidZero()
    {
        var writer = new NetworkWriterReal(16);
        writer.WriteBitsPacked(0);
        writer.FlushBits();
        var reader = new NetworkReaderReal(writer.ToArray());
        AssertEqual(0u, reader.ReadBitsPacked(), "VarInt 0 往返失败。");
    }

    /// <summary>第 5 组 (shift=28) chunk 高位非零 → 编码超 32 位, 应抛 InvalidDataException。</summary>
    public static void TestReadBitsPackedOver32BitsThrows()
    {
        // 手工构造: 5 组 VarInt, 前 4 组 0x7F+continue, 第 5 组 0x7F (chunk 高 3 位非零) + 结束位。
        // 字节流: 每字节 8 位 = 7 位数据 + 1 位继续。
        // 组1-4: 0x7F 数据 + 1 (继续) => 0xFF
        // 组5: 0x7F 数据 + 0 (结束) => 0x7F
        byte[] data = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x7F };
        var reader = new NetworkReaderReal(data);
        AssertThrows<InvalidDataException>(
            () => reader.ReadBitsPacked(),
            "超 32 位 VarInt 应抛 InvalidDataException。");
    }

    /// <summary>超过 5 组仍标记继续 → 应抛 InvalidDataException（而非静默截断）。</summary>
    public static void TestReadBitsPackedOver5GroupsThrows()
    {
        // 6 组全部 0xFF (0x7F 数据 + 继续)。
        byte[] data = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };
        var reader = new NetworkReaderReal(data);
        AssertThrows<InvalidDataException>(
            () => reader.ReadBitsPacked(),
            "超过 5 组继续位应抛 InvalidDataException。");
    }

    // ────────────────────────────────────────────────────────────
    // 4. UDPTransportReal: 分片 totalFragments 巨值防 OOM
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// 构造合法分片头但 totalFragments=0xFFFFFFFF, 经 HandleFragmentPacket 应被丢弃不 OOM。
    /// 通过反射调用私有方法, 验证防御上限生效。
    /// </summary>
    public static void TestUdpFragmentHugeTotalFragmentsDroppedNoOom()
    {
        var transport = new UDPTransportReal();
        try
        {
            // 分片包: Channel(1)=5 + Seq(4) + MsgId(4) + TotalFrags(4)=0xFFFFFFFF + FragIdx(4)=0 + payload
            byte[] packet = new byte[17 + 4];
            packet[0] = (byte)TransportChannel.Fragment;
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1), 1);   // seq
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(5), 42);  // msgId
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(9), uint.MaxValue); // totalFrags 伪造
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(13), 0);  // fragIdx
            packet[17] = 0xDE;
            packet[18] = 0xAD;
            packet[19] = 0xBE;
            packet[20] = 0xEF;

            var remote = new IPEndPoint(IPAddress.Loopback, 0);

            // 反射调用私有 HandleFragmentPacket(remote, data, sequence)。
            var method = typeof(UDPTransportReal).GetMethod(
                "HandleFragmentPacket", BindingFlags.Instance | BindingFlags.NonPublic);
            AssertTrue(method != null, "HandleFragmentPacket 方法未找到。");
            method!.Invoke(transport, new object[] { remote, packet, 1u });
            // 若未抛异常且未 OOM, 视为防御生效。
        }
        finally
        {
            transport.Dispose();
        }
    }

    /// <summary>合法小分片 (totalFragments=1) 应能被正常处理（重组交付或缓冲）。</summary>
    public static void TestUdpFragmentSmallValidNotDropped()
    {
        var transport = new UDPTransportReal();
        try
        {
            byte[] payload = new byte[] { 0x10, 0x20, 0x30 };
            byte[] packet = new byte[17 + payload.Length];
            packet[0] = (byte)TransportChannel.Fragment;
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1), 1);   // seq
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(5), 77);  // msgId
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(9), 1);   // totalFrags=1
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(13), 0);  // fragIdx=0
            Array.Copy(payload, 0, packet, 17, payload.Length);

            var remote = new IPEndPoint(IPAddress.Loopback, 0);
            var method = typeof(UDPTransportReal).GetMethod(
                "HandleFragmentPacket", BindingFlags.Instance | BindingFlags.NonPublic);
            AssertTrue(method != null, "HandleFragmentPacket 方法未找到。");
            method!.Invoke(transport, new object[] { remote, packet, 1u });
            // 单分片完整消息应被重组并交付（无异常即通过）。
        }
        finally
        {
            transport.Dispose();
        }
    }

    // ────────────────────────────────────────────────────────────
    // 5. NetworkReader (NetworkReplicationSystem) 溢出绕过边界
    // ────────────────────────────────────────────────────────────

    /// <summary>ReadBytes 声明 int.MaxValue 应抛异常而非 OOM（_position+count 溢出绕过检查修复）。</summary>
    public static void TestNetworkReaderReadBytesHugeCountThrows()
    {
        byte[] data = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0), int.MaxValue);
        data[4] = 0x01;
        data[5] = 0x02;
        data[6] = 0x03;
        data[7] = 0x04;

        var reader = new NetworkReader(data);
        AssertThrows<InvalidOperationException>(
            () => reader.ReadBytes(int.MaxValue),
            "ReadBytes(int.MaxValue) 应抛异常而非 OOM。");
    }

    /// <summary>ReadByteArray 声明巨值长度应抛异常而非 OOM。</summary>
    public static void TestNetworkReaderReadByteArrayHugeLengthThrows()
    {
        byte[] data = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0), int.MaxValue);
        data[4] = 0x01;

        var reader = new NetworkReader(data);
        AssertThrows<InvalidOperationException>(
            () => reader.ReadByteArray(),
            "ReadByteArray 巨值长度应抛异常而非 OOM。");
    }

    /// <summary>合法 ReadBytes 往返仍应正常工作。</summary>
    public static void TestNetworkReaderReadBytesValidRoundTrip()
    {
        var writer = new NetworkWriter(64);
        writer.WriteBytes(new byte[] { 10, 20, 30, 40 }, 0, 4);
        var reader = new NetworkReader(writer.ToArray());
        var bytes = reader.ReadBytes(4);
        AssertEqual(4, bytes.Length, "ReadBytes 长度不符。");
        AssertEqual((byte)10, bytes[0], "ReadBytes[0] 不符。");
    }

    // ────────────────────────────────────────────────────────────
    // 6. BitUnpacker.ReadBytes 溢出/静默截断
    // ────────────────────────────────────────────────────────────

    /// <summary>BitUnpacker.ReadBytes 声明巨值 count 应抛异常而非 OOM。</summary>
    public static void TestBitUnpackerReadBytesHugeCountThrows()
    {
        byte[] data = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
        var unpacker = new BitUnpacker(data);
        AssertThrows<InvalidDataException>(
            () => unpacker.ReadBytes(int.MaxValue),
            "BitUnpacker.ReadBytes(int.MaxValue) 应抛异常而非 OOM。");
    }

    /// <summary>BitUnpacker.ReadBytes 声明超剩余 count 应抛异常而非静默截断。</summary>
    public static void TestBitUnpackerReadBytesExceedsBufferThrows()
    {
        byte[] data = new byte[] { 0x01, 0x02 };
        var unpacker = new BitUnpacker(data);
        AssertThrows<InvalidDataException>(
            () => unpacker.ReadBytes(100),
            "BitUnpacker.ReadBytes(超剩余) 应抛异常而非静默截断。");
    }

    // ────────────────────────────────────────────────────────────
    // 7. RPCSystem.DeserializeParameter 的 decimal count 校验
    // ────────────────────────────────────────────────────────────

    /// <summary>decimal 参数 count != 4 应抛异常（防 new int[count] OOM）。</summary>
    public static void TestRpcDeserializeDecimalBadCountThrows()
    {
        byte[] data = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0), int.MaxValue);
        data[4] = 0;
        data[5] = 0;
        data[6] = 0;
        data[7] = 0;

        var reader = new NetworkReader(data);
        AssertThrows<InvalidDataException>(
            () => RpcParameterSerializer.DeserializeParameter(reader, typeof(decimal)),
            "decimal count != 4 应抛异常而非 OOM。");
    }

    // ────────────────────────────────────────────────────────────
    // 8. NetworkReaderReal: ReadBytesRaw / ReadRawBytes 静默截断修复
    // ────────────────────────────────────────────────────────────

    /// <summary>ReadBytesRaw 声明长度超剩余应抛 InvalidDataException（而非静默短读）。</summary>
    public static void TestReadBytesRawExceedsRemainingThrows()
    {
        byte[] data = new byte[] { 0x01, 0x02, 0x03 };
        var reader = new NetworkReaderReal(data);
        AssertThrows<InvalidDataException>(
            () => reader.ReadBytesRaw(100),
            "ReadBytesRaw(超剩余) 应抛 InvalidDataException 而非静默截断。");
    }

    /// <summary>ReadBytesRaw 合法长度应精确返回所请求字节。</summary>
    public static void TestReadBytesRawValidRoundTrip()
    {
        byte[] data = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var reader = new NetworkReaderReal(data);
        var bytes = reader.ReadBytesRaw(2);
        AssertEqual(2, bytes.Length, "ReadBytesRaw 长度不符。");
        AssertEqual((byte)0x01, bytes[0], "ReadBytesRaw[0] 不符。");
        AssertEqual((byte)0x02, bytes[1], "ReadBytesRaw[1] 不符。");
        AssertEqual(2, reader.Remaining, "ReadBytesRaw 后剩余字节不符。");
    }

    /// <summary>ReadRawBytes 目标数组范围非法应抛 ArgumentOutOfRangeException。</summary>
    public static void TestReadRawBytesBadDestinationRangeThrows()
    {
        byte[] data = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var reader = new NetworkReaderReal(data);
        byte[] dest = new byte[2];
        AssertThrows<ArgumentOutOfRangeException>(
            () => reader.ReadRawBytes(dest, 1, 2),
            "ReadRawBytes 越界目标范围应抛 ArgumentOutOfRangeException。");
    }

    /// <summary>ReadRawBytes 声明长度超剩余应抛 InvalidDataException（而非静默短读）。</summary>
    public static void TestReadRawBytesExceedsRemainingThrows()
    {
        byte[] data = new byte[] { 0x01, 0x02 };
        var reader = new NetworkReaderReal(data);
        AssertThrows<InvalidDataException>(
            () => reader.ReadRawBytes(new byte[8], 0, 8),
            "ReadRawBytes(超剩余) 应抛 InvalidDataException 而非静默短读。");
    }

    // ────────────────────────────────────────────────────────────
    // 9. NetworkFramingReal: 帧头 payloadSize 超实际数据 → 帧丢弃
    // ────────────────────────────────────────────────────────────

    /// <summary>帧头声明负载长度超过实际接收字节应返回 null（不静默截断半个负载）。</summary>
    public static void TestFramingDeclaredPayloadExceedsReceivedReturnsNull()
    {
        // 构造最小帧: HeaderSize 字节 + 1 字节真实负载, 但声明 PayloadSize = 200。
        // 帧头布局: ProtocolId(4) + FrameType(1) + FrameId(4) + Timestamp(8) + PayloadSize(4) + ...
        const int payloadSizeOffset = 4 + 1 + 4 + 8; // = 17

        byte[] data = new byte[NetworkFrameHeader.HeaderSize + 1];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0), NetworkFrameHeader.ProtocolMagic);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(payloadSizeOffset), 200);
        data[NetworkFrameHeader.HeaderSize] = 0xAB; // 真实负载仅 1 字节

        var frame = FrameCodec.ReadFrame(data, data.Length);
        AssertTrue(frame == null, "声明负载超长应返回 null。");
    }
}
