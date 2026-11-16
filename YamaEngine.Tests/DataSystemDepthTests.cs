using System;
using System.IO;
using YamaEngine.DataSystem;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    // ══════════════════════════════════════════════════════════════
    // DataSystemDepthTests — DataSystem 序列化工业级深度验证
    // 覆盖：BinaryFormatReader 损坏流防护（OOM 向量）、round-trip 完整性
    // ══════════════════════════════════════════════════════════════
    public static class DataSystemDepthTests
    {
        // ── BinaryFormatReader：伪造超大字符串长度必须抛 InvalidDataException（防 OOM/截断） ──
        public static void TestBinaryFormatReaderCorruptStringLengthThrows()
        {
            // 构造：完整 20 字节头（魔数+version+typeHash+fieldCount）+ 伪造 int.MaxValue 长度前缀
            var w = new BinaryFormatWriter();
            w.WriteHeader(1, 12345, 1);
            w.WriteInt(int.MaxValue); // 伪造字符串长度（无实际字符串数据）

            var reader = new BinaryFormatReader(w.ToArray());
            bool threw = false;
            try
            {
                reader.ReadHeader(out _, out _, out _);
                reader.ReadString();
            }
            catch (InvalidDataException)
            {
                threw = true;
            }
            AssertTrue(threw, "Corrupt string length prefix must throw InvalidDataException, not OOM.");
        }

        // ── BinaryFormatReader：伪造超大 byte[] 长度必须抛 InvalidDataException ──
        public static void TestBinaryFormatReaderCorruptBytesLengthThrows()
        {
            var w = new BinaryFormatWriter();
            w.WriteHeader(1, 12345, 1);
            w.WriteInt(int.MaxValue); // 伪造 byte[] 长度

            var reader = new BinaryFormatReader(w.ToArray());
            bool threw = false;
            try
            {
                reader.ReadHeader(out _, out _, out _);
                reader.ReadBytes();
            }
            catch (InvalidDataException)
            {
                threw = true;
            }
            AssertTrue(threw, "Corrupt bytes length prefix must throw InvalidDataException.");
        }

        // ── BinaryFormatReader：正常 round-trip 仍正确 ──
        public static void TestBinaryFormatReaderRoundTripStillWorks()
        {
            var w = new BinaryFormatWriter();
            w.WriteHeader(1, 12345, 3);
            w.WriteString("data");
            w.WriteInt(42);
            w.WriteFloat(3.14f);

            var reader = new BinaryFormatReader(w.ToArray());
            AssertTrue(reader.ReadHeader(out int version, out long hash, out int fields), "Header valid");
            AssertEqual(1, version, "version");
            AssertEqual(12345L, hash, "typeHash");
            AssertEqual(3, fields, "fieldCount");
            AssertEqual("data", reader.ReadString(), "string");
            AssertEqual(42, reader.ReadInt(), "int");
            AssertTrue(MathF.Abs(reader.ReadFloat() - 3.14f) < 1e-5f, "float");
        }

        // ── BinaryFormatReader：读越界（数据截断）必须抛而非静默 ──
        public static void TestBinaryFormatReaderTruncatedStreamThrows()
        {
            // 头 + 声明 100 字节字符串长度，但数据只有 5 字节（截断）
            var w = new BinaryFormatWriter();
            w.WriteHeader(1, 12345, 1);
            w.WriteInt(100); // 声明 100 字节
            w.WriteString("hello"); // 实际只有 5 字节字符串（但长度前缀已写 100 → 数据不足）

            var reader = new BinaryFormatReader(w.ToArray());
            bool threw = false;
            try
            {
                reader.ReadHeader(out _, out _, out _);
                reader.ReadString(); // 声明 100 但剩余 < 100 → 抛
            }
            catch (InvalidDataException)
            {
                threw = true;
            }
            AssertTrue(threw, "Truncated string must throw InvalidDataException, not silently truncate.");
        }

        // ── RuntimeSerializer：伪造超大属性计数必须抛（防 Dictionary 预分配 OOM） ──
        public static void TestRuntimeSerializerFakePropertyCountThrows()
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8))
            {
                w.Write("SHY_BIN");
                w.Write(1);          // format version
                w.Write(int.MaxValue); // 属性计数 = int.MaxValue
            }

            AssertThrows<InvalidDataException>(() =>
                BinarySerializer.DeserializeProperties(ms.ToArray()),
                "Fake property count must throw, not allocate huge dictionary");
        }

        // ── RuntimeSerializer：伪造超大字节数组长度必须抛（防 BinaryReader 先分配后读 OOM） ──
        public static void TestRuntimeSerializerFakeBytesLengthThrows()
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8))
            {
                w.Write("SHY_BIN");
                w.Write(1);                 // format version
                w.Write(1);                 // 1 个属性
                w.Write("v");               // 属性名
                w.Write((byte)23);          // MarkerBytes
                w.Write(int.MaxValue);      // 声明 2^31-1 字节
                w.Write(new byte[] { 1 }); // 实际仅 1 字节
            }

            AssertThrows<InvalidDataException>(() =>
                BinarySerializer.DeserializeProperties(ms.ToArray()),
                "Fake bytes length must throw, not pre-allocate giant array");
        }

        // ── RuntimeSerializer：合法 round-trip 仍正常（不回归） ──
        public static void TestRuntimeSerializerBytesRoundTrip()
        {
            var props = new PropertySerializer();
            props.GetProperties()["v"] = new byte[] { 1, 2, 3 };
            byte[] data = BinarySerializer.SerializeProperties(props);

            var restored = BinarySerializer.DeserializeProperties(data);
            var value = restored.GetProperties()["v"];
            AssertTrue(value is byte[] bytes && bytes.Length == 3 && bytes[2] == 3, "Bytes round-trip");
        }
    }
}
