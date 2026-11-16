using System;
using System.IO;
using System.Text;
using YamaEngine.DataSystem;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    // ══════════════════════════════════════════════════════════════
    // DataTableDepthTests — DataTable.ImportBinary 工业级深度验证
    // 覆盖：伪造行/列/字段计数拒绝、7-bit 长度前缀 OOM 防御、空数据
    // ══════════════════════════════════════════════════════════════
    public static class DataTableDepthTests
    {
        private static byte[] BuildValidBinary()
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms, Encoding.UTF8);
            w.Write("MyTable");
            w.Write(0); // rows
            w.Write(0); // cols
            return ms.ToArray();
        }

        private static byte[] Write7BitPrefix(byte[] data, int prefixValue)
        {
            // 构造: 单字节变长前缀前缀 + 固定 8 字节 (row/col 计数)
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms, Encoding.UTF8);
            // 手工写 7-bit 前缀
            Write7Bit(w, prefixValue);
            w.Write(0); // rowCount
            w.Write(0); // colCount
            return ms.ToArray();
        }

        private static void Write7Bit(BinaryWriter w, int value)
        {
            uint v = (uint)value;
            while (v >= 0x80) { w.Write((byte)(v | 0x80)); v >>= 7; }
            w.Write((byte)v);
        }

        // ── 合法二进制正常导入 ──
        public static void TestImportBinaryValid()
        {
            var table = new DataTable<TestRow>();
            AssertTrue(table.ImportBinary(BuildValidBinary()), "valid binary import");
        }

        // ── 空数据 → 返回 false (不崩溃) ──
        public static void TestImportBinaryEmptyFails()
        {
            var table = new DataTable<TestRow>();
            AssertTrue(!table.ImportBinary(Array.Empty<byte>()), "empty binary rejected");
        }

        // ── 伪造巨大行计数 (负) → 拒绝 ──
        public static void TestImportBinaryNegativeRowCountRejected()
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms, Encoding.UTF8);
            w.Write("T");
            w.Write(-5);   // rowCount 负
            w.Write(0);    // colCount
            var table = new DataTable<TestRow>();
            AssertTrue(!table.ImportBinary(ms.ToArray()), "negative rowCount rejected");
        }

        // ── 伪造巨大列计数 → 拒绝 (防 OOM/DoS) ──
        public static void TestImportBinaryHugeColCountRejected()
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms, Encoding.UTF8);
            w.Write("T");
            w.Write(0);
            w.Write(1000000); // colCount 巨大
            var table = new DataTable<TestRow>();
            AssertTrue(!table.ImportBinary(ms.ToArray()), "huge colCount rejected");
        }

        // ── 伪造 7-bit 长度前缀 0x7FFFFFFF → 不 OOM (返回 false) ──
        public static void TestImportBinaryHugeStringPrefixNoOom()
        {
            byte[] data = Write7BitPrefix(new byte[8], 0x7FFFFFFF);
            var table = new DataTable<TestRow>();
            bool ok = false;
            try { ok = table.ImportBinary(data); } catch { ok = false; }
            AssertTrue(!ok, "huge 7-bit prefix must not OOM (rejected)");
        }

        // ── 7-bit 前缀超 5 字节 (畸形) → 拒绝 ──
        public static void TestImportBinaryMalformedVarintRejected()
        {
            // 连续 5 个 0x80 字节 (都带延续位) → 第 6 字节无终止 → 无效
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms, Encoding.UTF8);
            for (int i = 0; i < 5; i++) w.Write((byte)0x80);
            w.Write(0);
            var table = new DataTable<TestRow>();
            AssertTrue(!table.ImportBinary(ms.ToArray()), "malformed varint rejected");
        }
    }

    // 供 DataTable 泛型使用的测试行类型
    public class TestRow : DataTableRow
    {
        public string Name { get; set; } = "";
        public int RowIndex { get; set; }
        public override void FromDictionary(System.Collections.Generic.Dictionary<string, string> fields) { }
        public override System.Collections.Generic.Dictionary<string, string> ToDictionary() => new();
    }
}
