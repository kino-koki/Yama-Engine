// BinarySerializerDepthTests.cs — Unity5 序列化系统深度验证
// 覆盖:
//   * BinarySerializer: 基础类型 round-trip、字符串(含 null)、数组、Vector/Quaternion/Matrix、
//                       GZip Compress/Decompress、Save/Load 文件、WriteObject/ReadObject tagged union
//   * VersionedSerializer: WriteHeader/ReadHeader/SaveWithHeader/migration 触发
//   * JsonSerializerWrapper: camelCase 命名 + binary round-trip
//   * CSVSerializer: 表头 + 转义

using System;
using System.IO;
using System.Linq;
using System.Numerics;
using YamaEngine.Core;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public class CsvRow
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public float Score { get; set; }
    }

    public class JsonPayload
    {
        public int Health { get; set; } = 100;
        public string PlayerName { get; set; } = "Hero";
        public Vector3 Spawn { get; set; } = new(1, 2, 3);
    }

    public static class BinarySerializerDepthTests
    {
        private static void Near(float got, float want, float tol, string msg)
            => AssertTrue(MathF.Abs(got - want) <= tol, $"{msg} (got {got:F4}, want {want:F4}±{tol:F4})");

        // =========================================================
        // 1. 基础类型 round-trip
        // =========================================================

        /// <summary>Bool/Byte/SByte/Short/UShort/Int/UInt/Long/ULong/Float/Double/Decimal round-trip。</summary>
        public static void TestPrimitivesRoundTrip()
        {
            var w = new BinarySerializer();
            w.WriteBool(true); w.WriteBool(false);
            w.WriteByte(200); w.WriteSByte(-100);
            w.WriteShort(-30000); w.WriteUShort(60000);
            w.WriteInt(-1234567); w.WriteUInt(4000000000u);
            w.WriteLong(-9_000_000_000L); w.WriteULong(18_000_000_000UL);
            w.WriteFloat(3.14f); w.WriteDouble(2.718281828);
            w.WriteDecimal(123.456m);
            AssertTrue(w.Offset > 0, "写入后 Offset 应 > 0");

            w.Reset(); // Reset 只清 _offset，不清 _buffer，可立即读回
            AssertTrue(w.ReadBool(), "bool true");
            AssertTrue(!w.ReadBool(), "bool false");
            AssertEqual((byte)200, w.ReadByte(), "byte");
            AssertEqual((sbyte)-100, w.ReadSByte(), "sbyte");
            AssertEqual((short)-30000, w.ReadShort(), "short");
            AssertEqual((ushort)60000, w.ReadUShort(), "ushort");
            AssertEqual(-1234567, w.ReadInt(), "int");
            AssertEqual(4000000000u, w.ReadUInt(), "uint");
            AssertEqual(-9_000_000_000L, w.ReadLong(), "long");
            AssertEqual(18_000_000_000UL, w.ReadULong(), "ulong");
            Near(3.14f, w.ReadFloat(), 1e-5f, "float");
            AssertTrue(Math.Abs(w.ReadDouble() - 2.718281828) < 1e-9, "double");
            AssertEqual(123.456m, w.ReadDecimal(), "decimal");
        }

        /// <summary>字符串 round-trip，含 null。</summary>
        public static void TestStringRoundTrip()
        {
            var w = new BinarySerializer();
            w.WriteString("Hello, Yama!");
            w.WriteString("");
            w.WriteString(null);
            w.WriteString("Unicode: 游戏引擎");

            w.Reset();
            AssertEqual("Hello, Yama!", w.ReadString(), "ASCII 字符串");
            AssertEqual("", w.ReadString(), "空字符串");
            AssertTrue(w.ReadString() == null, "null 字符串读回应为 null");
            AssertEqual("Unicode: 游戏引擎", w.ReadString(), "Unicode 字符串");
        }

        /// <summary>WriteBytes/ReadBytes round-trip。</summary>
        public static void TestBytesRoundTrip()
        {
            var w = new BinarySerializer();
            byte[] data = { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0xFF };
            w.WriteBytes(data);

            w.Reset();
            var read = w.ReadBytes();
            AssertEqual(data.Length, read.Length, "字节数组长度");
            for (int i = 0; i < data.Length; i++)
                AssertEqual(data[i], read[i], $"字节[{i}]");
        }

        // =========================================================
        // 2. 数值数组
        // =========================================================

        /// <summary>WriteIntArray/FloatArray/StringArray round-trip。</summary>
        public static void TestTypedArrays()
        {
            var w = new BinarySerializer();
            w.WriteIntArray(new[] { 1, -2, 3, -4, 5 });
            w.WriteFloatArray(new[] { 1.5f, -2.5f, 3.14f });
            w.WriteStringArray(new[] { "a", "bb", "ccc" });

            w.Reset();
            var ints = w.ReadIntArray();
            AssertEqual(5, ints.Length, "int 数组长度");
            AssertEqual(-2, ints[1], "int[1]");

            var floats = w.ReadFloatArray();
            AssertEqual(3, floats.Length, "float 数组长度");
            Near(3.14f, floats[2], 1e-5f, "float[2]");

            var strings = w.ReadStringArray();
            AssertEqual(3, strings.Length, "string 数组长度");
            AssertEqual("ccc", strings[2], "string[2]");
        }

        // =========================================================
        // 3. Vector/Quaternion/Matrix
        // =========================================================

        /// <summary>Vector3/Vector4/Quaternion round-trip（用 ReadObject tagged union）。</summary>
        public static void TestVectorTypes()
        {
            var w = new BinarySerializer();
            var v3 = new Vector3(1.5f, -2.5f, 3.5f);
            var v4 = new Vector4(0.1f, 0.2f, 0.3f, 0.4f);
            var q = Quaternion.CreateFromYawPitchRoll(0.5f, 0.3f, 0.1f);
            w.WriteObject(v3); w.WriteObject(v4); w.WriteObject(q);

            w.Reset();
            // 用 ReadObject 读回（支持 Vector3/Vector4/Quaternion）
            var rv3 = (Vector3)w.ReadObject()!;
            Near(1.5f, rv3.X, 1e-5f, "V3.X"); Near(-2.5f, rv3.Y, 1e-5f, "V3.Y"); Near(3.5f, rv3.Z, 1e-5f, "V3.Z");

            var rv4 = (Vector4)w.ReadObject()!;
            Near(0.1f, rv4.X, 1e-5f, "V4.X"); Near(0.4f, rv4.W, 1e-5f, "V4.W");

            var rq = (Quaternion)w.ReadObject()!;
            Near(q.X, rq.X, 1e-5f, "Q.X"); Near(q.W, rq.W, 1e-5f, "Q.W");
        }

        /// <summary>Matrix4x4 round-trip（按 M11..M44 行主序，验证 translation M41/M42/M43）。</summary>
        public static void TestMatrix4x4()
        {
            var w = new BinarySerializer();
            var m = Matrix4x4.CreateTranslation(10, 20, 30) * Matrix4x4.CreateScale(2);
            w.WriteMatrix4x4(m);

            w.Reset();
            // 按 M11..M14, M21..M24, M31..M34, M41..M44 顺序读 16 个 float
            float m11 = w.ReadFloat(); w.ReadFloat(); w.ReadFloat(); w.ReadFloat();
            w.ReadFloat(); w.ReadFloat(); w.ReadFloat(); w.ReadFloat();
            w.ReadFloat(); w.ReadFloat(); w.ReadFloat(); w.ReadFloat();
            float m41 = w.ReadFloat(), m42 = w.ReadFloat(), m43 = w.ReadFloat(); w.ReadFloat();
            // CreateTranslation(10,20,30) * CreateScale(2) = 平移被缩放 → M41=20, M42=40, M43=60, M11=2
            Near(2f, m11, 1e-5f, "M11 scale X");
            Near(20f, m41, 1e-3f, "M41 translation X");
            Near(40f, m42, 1e-3f, "M42 translation Y");
            Near(60f, m43, 1e-3f, "M43 translation Z");
        }

        // =========================================================
        // 4. WriteObject/ReadObject tagged union
        // =========================================================

        /// <summary>WriteObject 支持基础类型 tagged union round-trip。</summary>
        public static void TestWriteReadObject()
        {
            var w = new BinarySerializer();
            w.WriteObject(42);
            w.WriteObject("tagged");
            w.WriteObject(3.14f);
            w.WriteObject(null);
            w.WriteObject(true);

            w.Reset();
            AssertEqual(42, (int)w.ReadObject()!, "int object");
            AssertEqual("tagged", (string)w.ReadObject()!, "string object");
            Near(3.14f, (float)w.ReadObject()!, 1e-5f, "float object");
            AssertTrue(w.ReadObject() == null, "null object");
            AssertTrue((bool)w.ReadObject()!, "bool object");
        }

        // =========================================================
        // 5. GZip Compress/Decompress + 文件持久化
        // =========================================================

        /// <summary>Compress 输出可 Decompress 回原数据；重复数据压缩后体积显著缩小。</summary>
        public static void TestCompressDecompress()
        {
            var w = new BinarySerializer();
            for (int i = 0; i < 1000; i++) w.WriteInt(42); // 重复数据可压缩
            int rawSize = w.Offset;

            byte[] compressed = w.Compress();
            AssertTrue(compressed.Length < rawSize, $"压缩后 ({compressed.Length}) 应小于原始 ({rawSize})");

            // Decompress 回同一实例（Decompress 设 _offset=0 并填 _buffer）
            w.Decompress(compressed);
            for (int i = 0; i < 1000; i++)
                AssertEqual(42, w.ReadInt(), $"压缩 round-trip int[{i}]");
        }

        /// <summary>SaveToFile → LoadFromFile 字段保持一致（同实例 LoadFromFile 重置 _offset）。</summary>
        public static void TestSaveLoadFile()
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "yama_binser_test");
            Directory.CreateDirectory(tmpDir);
            string filePath = Path.Combine(tmpDir, "data.bin");
            try
            {
                var w = new BinarySerializer();
                w.WriteInt(1337);
                w.WriteString("persisted");
                w.WriteFloat(2.5f);
                w.SaveToFile(filePath);
                AssertTrue(File.Exists(filePath), "文件应存在");

                w.LoadFromFile(filePath);
                AssertEqual(1337, w.ReadInt(), "int round-trip via file");
                AssertEqual("persisted", w.ReadString(), "string round-trip via file");
                Near(2.5f, w.ReadFloat(), 1e-5f, "float round-trip via file");
            }
            finally
            {
                if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
            }
        }

        // =========================================================
        // 6. Reset / Offset
        // =========================================================

        /// <summary>Reset 清空 Offset，可复用同一 writer。</summary>
        public static void TestReset()
        {
            var w = new BinarySerializer();
            w.WriteInt(1); w.WriteInt(2); w.WriteInt(3);
            AssertTrue(w.Offset > 0, "写入后 Offset > 0");
            w.Reset();
            AssertEqual(0, w.Offset, "Reset 后 Offset=0");
            w.WriteInt(99);
            w.Reset();
            AssertEqual(99, w.ReadInt(), "Reset 后重新写入的 int");
        }

        // =========================================================
        // 7. VersionedSerializer — Header + Migration
        // =========================================================

        /// <summary>WriteHeader/ReadHeader round-trip 版本号。</summary>
        public static void TestVersionedHeaderRoundTrip()
        {
            var vs = new VersionedSerializer();
            var w = new BinarySerializer();
            vs.WriteHeader(w);
            w.WriteInt(42);

            w.Reset();
            var (maj, min, pat) = vs.ReadHeader(w);
            AssertEqual(1, maj, "默认 Major=1");
            AssertEqual(0, min, "默认 Minor=0");
            AssertEqual(0, pat, "默认 Patch=0");
            AssertEqual(42, w.ReadInt(), "Header 后的数据");
        }

        /// <summary>SaveWithHeader → LoadWithVersionCheck round-trip（版本匹配，无 migration）。</summary>
        public static void TestSaveWithHeaderRoundTrip()
        {
            var vs = new VersionedSerializer();
            byte[] data = vs.SaveWithHeader(w =>
            {
                w.WriteInt(100);
                w.WriteString("v1data");
            });

            // LoadWithVersionCheck 返回 BinarySerializer（内部已 Decompress，_offset 已过 header）
            var result = vs.LoadWithVersionCheck(data, targetVersion: 1);
            AssertTrue(result is BinarySerializer, "版本匹配应返回 BinarySerializer");
            var r = (BinarySerializer)result!;
            AssertEqual(100, r.ReadInt(), "SaveWithHeader int");
            AssertEqual("v1data", r.ReadString(), "SaveWithHeader string");
        }

        /// <summary>Migration 触发：major < targetVersion 时应用注册的 migration。</summary>
        public static void TestMigrationTriggered()
        {
            var vs = new VersionedSerializer();
            vs.RegisterMigration(1, reader =>
            {
                // v1 → v2 migration：读旧字段，转换为新格式
                int oldValue = reader.ReadInt();
                return oldValue * 2; // 模拟迁移变换
            });

            // 用 v1 header 写数据（直接构造 header = 1.0.0）
            var w = new BinarySerializer();
            w.WriteInt(1); // major
            w.WriteInt(0); // minor
            w.WriteInt(0); // patch
            w.WriteString(DateTime.UtcNow.ToString("O"));
            w.WriteInt(21); // v1 数据
            byte[] data = w.Compress();

            object? result = vs.LoadWithVersionCheck(data, targetVersion: 2);
            AssertTrue(result != null, "migration 应返回非 null");
            AssertTrue(result is int, "migration 返回 int");
            AssertEqual(42, (int)result, "migration 应将 21 转换为 42");
        }

        // =========================================================
        // 8. JsonSerializerWrapper
        // =========================================================

        /// <summary>JSON 序列化使用 camelCase 命名；binary round-trip 字段保持。</summary>
        public static void TestJsonWrapper()
        {
            var wrapper = new JsonSerializerWrapper();
            var payload = new JsonPayload { Health = 85, PlayerName = "Knight" };

            string json = wrapper.Serialize(payload);
            AssertTrue(json.Contains("\"health\""), "JSON 应含 camelCase health");
            AssertTrue(json.Contains("\"playerName\""), "JSON 应含 camelCase playerName");

            var deserialized = wrapper.Deserialize<JsonPayload>(json);
            AssertTrue(deserialized != null, "反序列化应成功");
            AssertEqual(85, deserialized.Health, "Health 应保持");
            AssertEqual("Knight", deserialized.PlayerName, "PlayerName 应保持");

            // Binary round-trip
            byte[] binary = wrapper.SerializeBinary(payload);
            var fromBinary = wrapper.DeserializeBinary<JsonPayload>(binary);
            AssertTrue(fromBinary != null, "binary 反序列化应成功");
            AssertEqual(85, fromBinary.Health, "binary Health 应保持");
        }

        // =========================================================
        // 9. CSVSerializer
        // =========================================================

        /// <summary>CSV 输出表头 + 行数据 + 含逗号字段正确转义。</summary>
        public static void TestCsvSerializer()
        {
            var csv = new CSVSerializer();
            var rows = new[]
            {
                new CsvRow { Id = 1, Name = "Alice", Score = 95.5f },
                new CsvRow { Id = 2, Name = "Bob, Jr.", Score = 87.0f }, // 含逗号应转义
                new CsvRow { Id = 3, Name = "He said \"hi\"", Score = 72.5f }, // 含引号应双引号转义
            };
            string output = csv.Serialize(rows);
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();

            AssertEqual(4, lines.Count, "表头 + 3 行数据");
            AssertTrue(lines[0].Contains("Id"), "表头应含 Id");
            AssertTrue(lines[0].Contains("Name"), "表头应含 Name");
            AssertTrue(lines[0].Contains("Score"), "表头应含 Score");

            AssertTrue(lines[1].Contains("Alice"), "行 1 应含 Alice");
            // Bob, Jr. 应被引号包裹
            AssertTrue(lines[2].Contains("\"Bob, Jr.\""), "含逗号字段应被引号包裹");
            // He said "hi" 应转义为 "He said ""hi"""
            AssertTrue(lines[3].Contains("\"\"hi\"\""), "含引号应双引号转义");
        }

        // =========================================================
        // 10. SerializedType 枚举完整性
        // =========================================================

        /// <summary>SerializedType 关键枚举值存在。</summary>
        public static void TestSerializedTypeEnum()
        {
            AssertEqual(0, (int)SerializedType.Null, "Null=0");
            AssertTrue(Enum.IsDefined(typeof(SerializedType), (byte)SerializedType.Int), "Int 已定义");
            AssertTrue(Enum.IsDefined(typeof(SerializedType), (byte)SerializedType.Vector3), "Vector3 已定义");
            AssertTrue(Enum.IsDefined(typeof(SerializedType), (byte)SerializedType.String), "String 已定义");
            AssertTrue(Enum.IsDefined(typeof(SerializedType), (byte)SerializedType.AssetHandle), "AssetHandle 已定义");
        }
    }
}
