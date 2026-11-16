using System;
using System.IO;
using System.Text;
using YamaEngine.Assets.Importers;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    /// <summary>
    /// 未受信二进制导入器的深度防御测试。
    /// 验证损坏长度前缀/元素计数不会导致 OOM、IndexOutOfRange 或静默截断，
    /// 而是抛出可诊断的 InvalidDataException。
    /// </summary>
    public static class ImporterDepthTests
    {
        // ───────────────────────────────────────────────
        //  FBX 二进制
        // ───────────────────────────────────────────────

        /// <summary>构造最小合法 FBX 二进制头（27 字节）。</summary>
        private static byte[] BuildFbxHeader()
        {
            var ms = new MemoryStream();
            // magic 固定 21 字节："Kaydara FBX Binary" (18) + 3 个空格
            byte[] magic = new byte[21];
            var magicText = Encoding.ASCII.GetBytes("Kaydara FBX Binary");
            Array.Copy(magicText, magic, magicText.Length);
            ms.Write(magic, 0, 21);
            ms.WriteByte(0x1A); ms.WriteByte(0x00);
            ms.Write(BitConverter.GetBytes(7400), 0, 4); // 版本 7.4
            return ms.ToArray();
        }

        /// <summary>
        /// 构造一个带单数组属性的 FBX 节点。arrayLen 可指定以模拟损坏长度前缀。
        /// endOffset 为节点结束的绝对文件偏移（FBX 语义）。
        /// </summary>
        private static byte[] BuildFbxNodeWithArray(uint arrayLen, uint encoding, uint compressedLen, uint endOffset)
        {
            var node = new MemoryStream();
            // 属性内容: 'a' + elemType('i') + arrayLen + encoding + compressedLen
            int propBytes = 1 + 1 + 4 + 4 + 4;
            int nameLen = 0;
            int nodeHeader = 13; // endOffset + numProps + propListLen + nameLen(1)

            node.Write(BitConverter.GetBytes(endOffset), 0, 4);     // endOffset（绝对偏移）
            node.Write(BitConverter.GetBytes(1u), 0, 4);            // numProperties
            node.Write(BitConverter.GetBytes(propBytes), 0, 4);     // propertyListLen
            node.WriteByte((byte)nameLen);                          // nameLen
            node.WriteByte((byte)'a');                              // 数组属性标记
            node.WriteByte((byte)'i');                              // int32[] 元素类型
            node.Write(BitConverter.GetBytes(arrayLen), 0, 4);      // 元素数量
            node.Write(BitConverter.GetBytes(encoding), 0, 4);      // 编码
            node.Write(BitConverter.GetBytes(compressedLen), 0, 4); // 压缩长度
            return node.ToArray();
        }

        // FBX 7.4 header 长度（21 magic + 2 reserved + 4 version）
        private const int FbxHeaderLen = 27;

        /// <summary>追加足够 padding，使节点区不被 footer 边界跳过。</summary>
        private static void AppendFooterPadding(MemoryStream ms)
        {
            byte[] pad = new byte[40];
            ms.Write(pad, 0, pad.Length);
        }

        public static void TestFbxArrayLengthPrefixTooLargeThrows()
        {
            string path = Path.Combine(Path.GetTempPath(), "fbx_depth_badarray_" + Guid.NewGuid().ToString("N") + ".fbx");
            FbxBinaryParser? parser = null;
            try
            {
                var full = new MemoryStream();
                var header = BuildFbxHeader();
                full.Write(header, 0, header.Length);
                // 伪造一个巨大元素数量（0x7FFFFFFF），触发 arrayLen 溢出保护
                // endOffset 为绝对偏移 = header + node(27)
                var node = BuildFbxNodeWithArray(0x7FFFFFFFu, 0u, 0u, (uint)(FbxHeaderLen + 27));
                full.Write(node, 0, node.Length);
                AppendFooterPadding(full);
                File.WriteAllBytes(path, full.ToArray());

                parser = new FbxBinaryParser(path);
                AssertThrows<InvalidDataException>(() => parser.Parse(),
                    "FBX 数组长度前缀过大必须抛 InvalidDataException，不得 OOM/静默截断");
            }
            finally
            {
                parser?.Dispose();
                if (File.Exists(path)) File.Delete(path);
            }
        }

        public static void TestFbxRawLengthPrefixTooLargeThrows()
        {
            string path = Path.Combine(Path.GetTempPath(), "fbx_depth_badraw_" + Guid.NewGuid().ToString("N") + ".fbx");
            FbxBinaryParser? parser = null;
            try
            {
                var full = new MemoryStream();
                var header = BuildFbxHeader();
                full.Write(header, 0, header.Length);

                // 单节点：属性为 'R'（raw），rawLen 伪造为巨大值
                var node = new MemoryStream();
                int propBytes = 1 + 4; // 'R' + rawLen
                node.Write(BitConverter.GetBytes((uint)(FbxHeaderLen + 13 + propBytes)), 0, 4); // endOffset 绝对
                node.Write(BitConverter.GetBytes(1u), 0, 4);
                node.Write(BitConverter.GetBytes(propBytes), 0, 4);
                node.WriteByte(0);
                node.WriteByte((byte)'R');
                node.Write(BitConverter.GetBytes(0x7FFFFFFFu), 0, 4); // rawLen 巨大
                full.Write(node.ToArray(), 0, (int)node.Length);
                AppendFooterPadding(full);
                File.WriteAllBytes(path, full.ToArray());

                parser = new FbxBinaryParser(path);
                AssertThrows<InvalidDataException>(() => parser.Parse(),
                    "FBX raw 长度前缀过大必须抛 InvalidDataException，不得静默丢弃");
            }
            finally
            {
                parser?.Dispose();
                if (File.Exists(path)) File.Delete(path);
            }
        }

        // ───────────────────────────────────────────────
        //  PLY 二进制
        // ───────────────────────────────────────────────

        /// <summary>
        /// 构造一个二进制 PLY，其中 vertex 元素的 Count 声明为巨大值。
        /// </summary>
        private static byte[] BuildPlyBinaryWithHugeCount(int declaredCount, out string headerText)
        {
            var sb = new StringBuilder();
            sb.AppendLine("ply");
            sb.AppendLine("format binary_little_endian 1.0");
            sb.AppendLine("element vertex " + declaredCount);
            sb.AppendLine("property float x");
            sb.AppendLine("property float y");
            sb.AppendLine("property float z");
            sb.AppendLine("element face 0");
            sb.AppendLine("property list uchar int vertex_indices");
            sb.AppendLine("end_header");
            headerText = sb.ToString();

            // 实际二进制数据：仅 1 个顶点（远小于 declaredCount）
            var ms = new MemoryStream();
            ms.Write(BitConverter.GetBytes(0.5f), 0, 4);
            ms.Write(BitConverter.GetBytes(0.5f), 0, 4);
            ms.Write(BitConverter.GetBytes(0.5f), 0, 4);
            return ms.ToArray();
        }

        public static void TestPlyHugeElementCountThrows()
        {
            string path = Path.Combine(Path.GetTempPath(), "ply_depth_hugecount_" + Guid.NewGuid().ToString("N") + ".ply");
            try
            {
                byte[] bin = BuildPlyBinaryWithHugeCount(int.MaxValue / 4, out string header);
                var full = new MemoryStream();
                var hdrBytes = Encoding.ASCII.GetBytes(header);
                full.Write(hdrBytes, 0, hdrBytes.Length);
                full.Write(bin, 0, bin.Length);
                File.WriteAllBytes(path, full.ToArray());

                AssertThrows<InvalidDataException>(() =>
                {
                    new PLYImporter(path).Import();
                }, "PLY 声明元素数量巨大必须抛 InvalidDataException，不得 OOM");
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        public static void TestPlyHugeFaceIndexCountThrows()
        {
            string path = Path.Combine(Path.GetTempPath(), "ply_depth_hugeface_" + Guid.NewGuid().ToString("N") + ".ply");
            try
            {
                // vertex 0 个 + face 1 个，face 的列表 count 伪造为巨大值
                var sb = new StringBuilder();
                sb.AppendLine("ply");
                sb.AppendLine("format binary_little_endian 1.0");
                sb.AppendLine("element vertex 0");
                sb.AppendLine("property float x");
                sb.AppendLine("property float y");
                sb.AppendLine("property float z");
                sb.AppendLine("element face 1");
                sb.AppendLine("property list uchar int vertex_indices");
                sb.AppendLine("end_header");
                var full = new MemoryStream();
                var hdrBytes = Encoding.ASCII.GetBytes(sb.ToString());
                full.Write(hdrBytes, 0, hdrBytes.Length);
                full.WriteByte(0xFF); // count = 255（巨大相对数据）
                full.Write(BitConverter.GetBytes(0), 0, 4); // 一个索引
                File.WriteAllBytes(path, full.ToArray());

                AssertThrows<InvalidDataException>(() =>
                {
                    new PLYImporter(path).Import();
                }, "PLY 面索引数量超出数据必须抛 InvalidDataException，不得 OOB/OOM");
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        public static void TestPlyTruncatedBinaryThrows()
        {
            string path = Path.Combine(Path.GetTempPath(), "ply_depth_trunc_" + Guid.NewGuid().ToString("N") + ".ply");
            try
            {
                // 声明 3 个顶点但只给 1 个顶点的数据（截断）→ ReadBinaryValue 越界 → InvalidData
                byte[] bin = BuildPlyBinaryWithHugeCount(3, out string header);
                var full = new MemoryStream();
                var hdrBytes = Encoding.ASCII.GetBytes(header);
                full.Write(hdrBytes, 0, hdrBytes.Length);
                full.Write(bin, 0, bin.Length); // 3 顶点需 36B，只给 12B
                File.WriteAllBytes(path, full.ToArray());

                AssertThrows<InvalidDataException>(() =>
                {
                    new PLYImporter(path).Import();
                }, "PLY 截断二进制必须抛 InvalidDataException，不得 IndexOutOfRange/静默");
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        // ───────────────────────────────────────────────
        //  glTF
        // ───────────────────────────────────────────────

        /// <summary>
        /// 未受信 glTF 的 buffer.uri 含 ../../ 应被拒绝（防止读到资产目录之外的任意文件），
        /// 而不是静默成功读取。
        /// </summary>
        public static void TestGltfExternalBufferPathTraversalRejected()
        {
            string dir = Path.Combine(Path.GetTempPath(), "gltf_depth_traversal_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string gltfPath = Path.Combine(dir, "model.gltf");
            try
            {
                // 构造一个最小 glTF: 单个 buffer 引用 ../../ 逃逸路径。
                const string json = "{\"asset\":{\"version\":\"2.0\"}," +
                    "\"buffers\":[{\"uri\":\"../../secret.bin\",\"byteLength\":4}]}";
                File.WriteAllText(gltfPath, json);

                AssertThrows<UnauthorizedAccessException>(() =>
                {
                    YamaEngine.Assets.GlTFLoader.Load(gltfPath);
                }, "glTF 外部 buffer 路径穿越必须被拒绝，不得读取资产目录之外文件");
            }
            finally
            {
                DeleteTree(dir);
            }
        }

        /// <summary>合法的 data: URI 与同目录相对路径应正常工作。</summary>
        public static void TestGltfDataUriAccepted()
        {
            string dir = Path.Combine(Path.GetTempPath(), "gltf_depth_datauri_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string gltfPath = Path.Combine(dir, "model.gltf");
            try
            {
                // byteLength 4 的 base64 数据 URI (AAAA = 4 个 0x00 字节)
                const string json = "{\"asset\":{\"version\":\"2.0\"}," +
                    "\"buffers\":[{\"uri\":\"data:application/octet-stream;base64,AAAA\",\"byteLength\":4}]}";
                File.WriteAllText(gltfPath, json);

                var model = YamaEngine.Assets.GlTFLoader.Load(gltfPath);
                AssertTrue(model != null, "data URI glTF 应成功加载。");
            }
            finally
            {
                DeleteTree(dir);
            }
        }

        private static void DeleteTree(string dir)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch
            {
                // 临时目录清理失败不影响断言结果
            }
        }

        // ───────────────────────────────────────────────
        //  NavMesh 反序列化 (OOM 向量)
        // ───────────────────────────────────────────────

        /// <summary>
        /// 未受信 NavMesh 流的顶点计数 = 0x7FFFFFFF 必须抛 InvalidDataException，
        /// 而非 new Vector3[vcount] 直接 OOM。
        /// </summary>
        public static void TestNavMeshDeserializeHugeVertexCountThrows()
        {
            using var ms = new MemoryStream();
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(1);                        // version
                bw.Write(0f); bw.Write(0f); bw.Write(0f);  // bmin
                bw.Write(1f); bw.Write(1f); bw.Write(1f);  // bmax
                bw.Write(1f); bw.Write(1f);               // cell size/height
                bw.Write(int.MaxValue);                   // vcount 伪造
            }
            var gen = new YamaEngine.AI.NavMeshGenerator();
            AssertThrows<InvalidDataException>(() =>
                gen.Deserialize(ms.ToArray()),
                "NavMesh 伪造顶点计数必须抛 InvalidDataException，不得 OOM");
        }

        /// <summary>
        /// 未受信 NavMesh 流的顶点计数超过流剩余字节必须抛，而非静默读至流尽头。
        /// </summary>
        public static void TestNavMeshDeserializeVertexCountExceedsDataThrows()
        {
            using var ms = new MemoryStream();
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(1);
                bw.Write(0f); bw.Write(0f); bw.Write(0f);
                bw.Write(1f); bw.Write(1f); bw.Write(1f);
                bw.Write(1f); bw.Write(1f);
                bw.Write(100);  // 声明 100 个顶点, 但后续只有 1 个顶点的数据
                bw.Write(0f); bw.Write(0f); bw.Write(0f);
            }
            var gen = new YamaEngine.AI.NavMeshGenerator();
            AssertThrows<InvalidDataException>(() =>
                gen.Deserialize(ms.ToArray()),
                "NavMesh 顶点计数超出数据必须抛 InvalidDataException");
        }

        // ───────────────────────────────────────────────
        //  PNG 纹理 (chunk 长度 / 尺寸溢出 / 截断)
        // ───────────────────────────────────────────────

        /// <summary>
        /// PNG IDAT chunk 声明长度超过文件剩余字节必须抛 InvalidDataException，
        /// 而非回绕 (int)chunkLength 导致越界/巨量分配。
        /// </summary>
        public static void TestPngChunkLengthExceedsFileThrows()
        {
            string path = Path.Combine(Path.GetTempPath(), "png_depth_chunk_" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                using var ms = new MemoryStream();
                // 8 字节 PNG 签名
                ms.Write(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
                // 一个 IDAT chunk, 声明长度 = 0xFFFFFFF0 (回绕为负)
                ms.Write(new byte[] { 0xFF, 0xFF, 0xFF, 0xF0 }, 0, 4);
                ms.Write(Encoding.ASCII.GetBytes("IDAT"), 0, 4);
                ms.Write(new byte[] { 1, 2, 3, 4 }, 0, 4); // 实际只有 4 字节数据
                File.WriteAllBytes(path, ms.ToArray());

                AssertThrows<InvalidDataException>(() =>
                    new TextureImporter().Import(path),
                    "PNG chunk 长度超过文件必须抛 InvalidDataException，不得回绕/OOM");
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        /// <summary>
        /// PNG IHDR 宽高为 0xFFFFFFFF (回绕为 -1) 必须抛 InvalidDataException，而非分配负尺寸。
        /// </summary>
        public static void TestPngIhdrDimensionOverflowThrows()
        {
            string path = Path.Combine(Path.GetTempPath(), "png_depth_dim_" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                using var ms = new MemoryStream();
                ms.Write(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
                // IHDR chunk (13 字节数据)
                ms.Write(new byte[] { 0, 0, 0, 13 }, 0, 4);
                ms.Write(Encoding.ASCII.GetBytes("IHDR"), 0, 4);
                ms.Write(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, 0, 4); // width = uint.Max
                ms.Write(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, 0, 4); // height = uint.Max
                ms.Write(new byte[] { 8, 2, 0, 0, 0 }, 0, 5);          // bitDepth=8, colorType=2
                ms.Write(new byte[] { 0, 0, 0, 0 }, 0, 4);             // CRC
                File.WriteAllBytes(path, ms.ToArray());

                AssertThrows<InvalidDataException>(() =>
                    new TextureImporter().Import(path),
                    "PNG IHDR 尺寸回绕必须抛 InvalidDataException，不得分配负尺寸");
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }
}
