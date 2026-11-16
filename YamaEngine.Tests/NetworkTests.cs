// NetworkTests.cs — 网络复制/预测/序列化子系统验证
// 覆盖：
//   * NetworkValueSerializer 按类型往返 (基元/Vector/Quaternion/String)
//   * ReplicatedPropertyReal<T> 脏标记 / 频率 / 条件 / 序列化往返
//   * ClientPredictorReal<TInput,TState> 预测 + 服务端纠错 (回滚重放)
//   * VarInt (无符号/有符号 ZigZag) 往返
//   * FloatQuantization (浮点/Vector3/Quaternion SmallestThree) 往返
//   * CRC32 校验 + BitWriter/BitReader 位打包往返
// 全部 CPU 级确定性，不依赖 GPU/真实网络。
using System;
using System.Numerics;
using YamaEngine.Network;
using static YamaEngine.Tests.TestHelpers;

// 可空性警告抑制: 泛型 T 序列化 / ShouldReplicate(null) 连接路径为测试有意为之。
#pragma warning disable CS8604, CS8625

namespace YamaEngine.Tests;

/// <summary>网络子系统验证套件。</summary>
public sealed class NetworkTests
{
    // ────────────────────────────────────────────────────────────
    // 1. NetworkValueSerializer 按类型往返
    // ────────────────────────────────────────────────────────────

    private static T RoundTripValue<T>(T value)
    {
        var writer = new NetworkWriterReal(256);
        NetworkValueSerializer.SerializeValue(writer, value, typeof(T));
        var reader = new NetworkReaderReal(writer.ToArray());
        return (T)NetworkValueSerializer.DeserializeValue(reader, typeof(T));
    }

    /// <summary>基元类型应可往返序列化。</summary>
    public static void TestNetworkValueSerializerPrimitiveRoundTrip()
    {
        AssertEqual(42, RoundTripValue(42), "int 往返失败。");
        AssertEqual(3.14f, RoundTripValue(3.14f), "float 往返失败。");
        AssertEqual(2.718281828, RoundTripValue(2.718281828), "double 往返失败。");
        AssertEqual(true, RoundTripValue(true), "bool 往返失败。");
        AssertEqual((byte)200, RoundTripValue((byte)200), "byte 往返失败。");
        AssertEqual((short)-12345, RoundTripValue((short)-12345), "short 往返失败。");
        AssertEqual(123456789L, RoundTripValue(123456789L), "long 往返失败。");
        AssertEqual("yama-network", RoundTripValue("yama-network"), "string 往返失败。");
    }

    /// <summary>数学类型应可往返序列化。</summary>
    public static void TestNetworkValueSerializerMathRoundTrip()
    {
        var v3 = new Vector3(1.5f, -2.25f, 3.75f);
        var v3b = RoundTripValue(v3);
        AssertTrue(Vector3.Distance(v3, v3b) < 1e-5f, $"Vector3 往返偏差过大: {v3b}");

        var q = Quaternion.Normalize(new Quaternion(0.1f, 0.2f, 0.3f, 0.9f));
        var qb = RoundTripValue(q);
        AssertTrue(Math.Abs(Quaternion.Dot(q, qb)) >= 0.9999f, $"Quaternion 往返偏差过大: {qb}");

        var m = Matrix4x4.CreateTranslation(1f, 2f, 3f) * Matrix4x4.CreateScale(2f);
        var mb = RoundTripValue(m);
        AssertTrue(Vector3.Distance(new Vector3(m.M41, m.M42, m.M43), new Vector3(mb.M41, mb.M42, mb.M43)) < 1e-4f,
            $"Matrix4x4 往返平移偏差过大: {mb}");
    }

    // ────────────────────────────────────────────────────────────
    // 2. ReplicatedPropertyReal<T> 脏标记 / 序列化
    // ────────────────────────────────────────────────────────────

    /// <summary>SetValue 仅在值变化时置脏。</summary>
    public static void TestReplicatedPropertyDirtyTracking()
    {
        var prop = new ReplicatedPropertyReal<int>(0);
        AssertTrue(!prop.IsDirty, "初始应干净。");

        prop.SetValue(5);
        AssertTrue(prop.IsDirty, "值变化应置脏。");
        AssertEqual(5, prop.Value, "值应更新为 5。");

        prop.SetValue(5); // 相同值
        AssertTrue(prop.IsDirty, "相同值不应清除脏标记。");

        prop.ClearDirty();
        AssertTrue(!prop.IsDirty, "ClearDirty 应清除。");
    }

    /// <summary>ShouldReplicate: 脏 + 条件函数共同决定。</summary>
    public static void TestReplicatedPropertyShouldReplicate()
    {
        var prop = new ReplicatedPropertyReal<int>(0);
        AssertTrue(!prop.ShouldReplicate(null), "干净属性不应复制。");

        prop.SetValue(1);
        AssertTrue(prop.ShouldReplicate(null), "脏属性应复制。");

        prop.SetCondition(_ => false);
        AssertTrue(!prop.ShouldReplicate(null), "条件返回 false 时应阻止复制。");

        prop.SetCondition(_ => true);
        AssertTrue(prop.ShouldReplicate(null), "条件返回 true 时应允许复制。");
    }

    /// <summary>Serialize 后应清脏、更新 LastSyncedValue 并可往返读取。</summary>
    public static void TestReplicatedPropertySerializeRoundTrip()
    {
        var prop = new ReplicatedPropertyReal<float>(1.5f);
        prop.SetValue(9.75f);

        var writer = new NetworkWriterReal(256);
        prop.Serialize(writer);
        AssertTrue(!prop.IsDirty, "Serialize 后应清脏。");
        AssertEqual(9.75f, prop.LastSyncedValue, "LastSyncedValue 应更新为当前值。");

        var reader = new NetworkReaderReal(writer.ToArray());
        var remote = new ReplicatedPropertyReal<float>();
        remote.Deserialize(reader);
        AssertEqual(9.75f, remote.Value, "远端反序列化值应一致。");
    }

    // ────────────────────────────────────────────────────────────
    // 3. ClientPredictorReal<TInput,TState> 预测 + 纠错
    // ────────────────────────────────────────────────────────────

    // 模拟: 状态 = 位置(单轴), 输入 = 速度
    // 注意: SimInput 必须为 struct — 未记录输入的 tick 时 Predict 用 default,
    // 引用类型会得到 null 导致模拟函数解引用失败。
    private sealed class SimState
    {
        public float Pos;
    }

    private struct SimInput
    {
        public float Vel;
    }

    private static ClientPredictorReal<SimInput, SimState> CreatePredictor(out System.Collections.Generic.List<float> applied)
    {
        var appliedList = new System.Collections.Generic.List<float>();
        applied = appliedList;
        var predictor = new ClientPredictorReal<SimInput, SimState>(
            simulateFunc: (state, input, dt) => new SimState { Pos = state.Pos + input.Vel * dt },
            stateComparer: (a, b) => Math.Abs(a.Pos - b.Pos) < 0.001f,
            cloneFunc: s => new SimState { Pos = s.Pos });
        predictor.SetApplyStateCallback(s => appliedList.Add(s.Pos));
        return predictor;
    }

    /// <summary>Predict 应按输入逐步推进状态并应用回调。</summary>
    public static void TestClientPredictorAdvancesState()
    {
        var predictor = CreatePredictor(out var applied);
        predictor.SetFixedStep(1f / 60f);

        // 每帧速度 60 => 每帧位移 1.0
        predictor.RecordInput(1, new SimInput { Vel = 60f });
        predictor.RecordInput(2, new SimInput { Vel = 60f });
        var s0 = new SimState { Pos = 0f };
        var s1 = predictor.Predict(1, 1f / 60f, s0);
        AssertTrue(Math.Abs(s1.Pos - 1.0f) < 1e-4f, $"预测位置应为 1.0, 实际 {s1.Pos}");

        var s2 = predictor.Predict(2, 1f / 60f, s1);
        AssertTrue(Math.Abs(s2.Pos - 2.0f) < 1e-4f, $"第二次预测位置应为 2.0, 实际 {s2.Pos}");

        AssertTrue(applied.Count == 2, $"应用回调应触发 2 次, 实际 {applied.Count}");
        AssertTrue(Math.Abs(applied[1] - 2.0f) < 1e-4f, $"最终应用状态应为 2.0, 实际 {applied[1]}");
    }

    /// <summary>服务端状态与预测一致时应确认(不纠正)。</summary>
    public static void TestClientPredictorNoCorrectionOnMatch()
    {
        var predictor = CreatePredictor(out _);
        predictor.SetFixedStep(1f / 60f);
        predictor.RecordInput(1, new SimInput { Vel = 60f });
        predictor.Predict(1, 1f / 60f, new SimState { Pos = 0f });

        bool corrected = predictor.OnServerState(1, new SimState { Pos = 1.0f });
        AssertTrue(!corrected, "一致状态不应纠正。");
    }

    /// <summary>服务端状态与预测不一致时应回滚重放并纠正。</summary>
    public static void TestClientPredictorCorrectionOnMismatch()
    {
        var predictor = CreatePredictor(out var applied);
        predictor.SetFixedStep(1f / 60f);

        // 客户端预测: tick1 → 1.0, tick2 → 2.0
        predictor.RecordInput(1, new SimInput { Vel = 60f });
        predictor.RecordInput(2, new SimInput { Vel = 60f });
        var s1 = predictor.Predict(1, 1f / 60f, new SimState { Pos = 0f });
        predictor.Predict(2, 1f / 60f, s1);

        // 服务端权威: tick1 实际为 1.5 (服务器更快/有校正)
        bool corrected = predictor.OnServerState(1, new SimState { Pos = 1.5f });
        AssertTrue(corrected, "不一致状态应纠正。");

        // 纠错后应用的状态应等于重放结果: 1.5 + 1.0 = 2.5
        float final = applied[applied.Count - 1];
        AssertTrue(Math.Abs(final - 2.5f) < 1e-3f, $"重放后最终状态应为 2.5, 实际 {final}");
    }

    // ────────────────────────────────────────────────────────────
    // 4. VarInt 往返
    // ────────────────────────────────────────────────────────────

    /// <summary>无符号/有符号 VarInt 应可往返 (NetworkWriterReal 位打包)。</summary>
    public static void TestVarIntRoundTrip()
    {
        var writer = new NetworkWriterReal(64);
        writer.WriteBitsPacked(300u);
        writer.WriteBitsPackedSigned(-123456);
        writer.WriteBitsPacked(0u);
        writer.WriteBitsPackedSigned(7);
        writer.FlushBits();

        var reader = new NetworkReaderReal(writer.ToArray());
        AssertEqual(300u, reader.ReadBitsPacked(), "VarInt 300 往返失败。");
        AssertEqual(-123456, reader.ReadBitsPackedSigned(), "有符号 VarInt -123456 往返失败。");
        AssertEqual(0u, reader.ReadBitsPacked(), "VarInt 0 往返失败。");
        AssertEqual(7, reader.ReadBitsPackedSigned(), "有符号 VarInt 7 往返失败。");
    }

    // ────────────────────────────────────────────────────────────
    // 5. FloatQuantization 往返
    // ────────────────────────────────────────────────────────────

    /// <summary>量化/反量化应保持误差在量化步长内。</summary>
    public static void TestFloatQuantizationRoundTrip()
    {
        float min = -100f, max = 100f;
        int bits = 16;
        float step = (max - min) / ((1 << bits) - 1);

        float value = 37.5f;
        uint q = FloatQuantizationUtility.QuantizeFloat(value, min, max, bits);
        float dq = FloatQuantizationUtility.DequantizeFloat(q, min, max, bits);
        AssertTrue(Math.Abs(dq - value) <= step + 1e-4f, $"量化误差应 ≤ {step}, 实际 {Math.Abs(dq - value)}");

        // Vector3 独立分量
        var v = new Vector3(10f, -20f, 30f);
        var (qx, qy, qz) = FloatQuantizationUtility.QuantizeVector3(v, min, max, bits);
        var dqV = FloatQuantizationUtility.DequantizeVector3(qx, qy, qz, min, max, bits);
        AssertTrue(Vector3.Distance(v, dqV) <= step * 1.8f + 1e-4f, $"Vector3 量化误差过大: {dqV}");

        // Quaternion SmallestThree
        var qq = Quaternion.Normalize(new Quaternion(0.3f, -0.5f, 0.1f, 0.8f));
        var (axis, neg, a, b, c) = FloatQuantizationUtility.QuantizeQuaternion(qq, 12);
        var dqQ = FloatQuantizationUtility.DequantizeQuaternion(axis, neg, a, b, c, 12);
        AssertTrue(Math.Abs(Quaternion.Dot(qq, dqQ)) >= 0.999f, $"Quaternion 量化误差过大: {dqQ}");
    }

    // ────────────────────────────────────────────────────────────
    // 6. CRC32 校验
    // ────────────────────────────────────────────────────────────

    /// <summary>CRC32 已知向量校验 (IEEE 802.3)。</summary>
    public static void TestCrc32KnownVector()
    {
        // "123456789" 的 CRC32 = 0xCBF43926 (IEEE 802.3 标准测试向量)
        byte[] data = System.Text.Encoding.ASCII.GetBytes("123456789");
        uint crc = CRC32Utility.ComputeCRC32(data);
        AssertEqual(0xCBF43926u, crc, $"CRC32(\"123456789\") 应为 0xCBF43926, 实际 0x{crc:X8}");
        AssertTrue(CRC32Utility.VerifyCRC32(data, crc), "VerifyCRC32 应通过。");
    }

    // ────────────────────────────────────────────────────────────
    // 7. BitWriter / BitReader 位打包往返
    // ────────────────────────────────────────────────────────────

    /// <summary>位级打包应可往返。</summary>
    public static void TestBitPackRoundTrip()
    {
        using var bitWriter = new BitWriter(32);
        bitWriter.WriteBool(true);
        bitWriter.WriteBool(false);
        bitWriter.WriteUInt(0x2A, 6);   // 42
        bitWriter.WriteInt(-5, 4);      // 有符号
        bitWriter.WriteQuantizedFloat(0.5f, 0f, 1f, 8);
        byte[] bytes = bitWriter.ToArray();

        var bitReader = new BitReader(bytes);
        AssertEqual(true, bitReader.ReadBool(), "bit bool#1 应 true。");
        AssertEqual(false, bitReader.ReadBool(), "bit bool#2 应 false。");
        AssertEqual(0x2Au, bitReader.ReadUInt(6), "bit uint 应 42。");
        AssertEqual(-5, bitReader.ReadInt(4), "bit int 应 -5。");
        float q = bitReader.ReadQuantizedFloat(0f, 1f, 8);
        AssertTrue(Math.Abs(q - 0.5f) < 0.01f, $"bit 量化浮点应为 0.5, 实际 {q}");
    }

    /// <summary>位级有符号整数读应做符号扩展 (补码)。</summary>
    public static void TestBitReaderSignExtendsSignedInt()
    {
        // 4 位有符号范围 [-8, 7]
        using var bitWriter = new BitWriter(16);
        bitWriter.WriteInt(-1, 4);   // 0b1111
        bitWriter.WriteInt(-8, 4);   // 0b1000
        bitWriter.WriteInt(7, 4);    // 0b0111
        bitWriter.WriteInt(0, 4);    // 0b0000
        byte[] bytes = bitWriter.ToArray();

        var bitReader = new BitReader(bytes);
        AssertEqual(-1, bitReader.ReadInt(4), "-1 应符号扩展为 -1。");
        AssertEqual(-8, bitReader.ReadInt(4), "-8 应符号扩展为 -8。");
        AssertEqual(7, bitReader.ReadInt(4), "7 应保持 7。");
        AssertEqual(0, bitReader.ReadInt(4), "0 应保持 0。");
    }
}
