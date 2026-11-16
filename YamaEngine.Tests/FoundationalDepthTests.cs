using System;
using System.Numerics;
using YamaEngine.Core;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    // ══════════════════════════════════════════════════════════════
    // FoundationalDepthTests — 基础模块工业级深度验证
    // 覆盖：BinarySerializer 边界/损坏数据、NativeContainers 非法参数、
    //       MathUtils 退化输入、YamaRandom 零种子、Memory 分配器防御
    // ══════════════════════════════════════════════════════════════
    public static class FoundationalDepthTests
    {
        // ── BinarySerializer：写越界必须抛明确异常而非 IndexOutOfRange ──
        public static void TestBinarySerializerWriteOverflowThrows()
        {
            var w = new BinarySerializer();
            // 写入超出 64MB 容量会触发溢出保护（直接写一个超长字符串）
            bool threw = false;
            try
            {
                w.WriteString(new string('x', 70 * 1024 * 1024));
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }
            AssertTrue(threw, "WriteString beyond capacity must throw InvalidOperationException.");
        }

        // ── BinarySerializer：读越界必须抛明确异常（损坏/截断流） ──
        public static void TestBinarySerializerReadUnderflowThrows()
        {
            var w = new BinarySerializer();
            bool threw = false;
            try
            {
                // WriteSpan 写 4 字节长度前缀 + data。写 Capacity-8 字节数据后，
                // offset 落在 Capacity-4；随后 ReadLong（需 8 字节）越过 64MB 边界。
                w.WriteSpan(new byte[w.Capacity - 8]);
                w.ReadLong();
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }
            AssertTrue(threw, "ReadLong past buffer boundary must throw InvalidOperationException.");
        }

        // ── BinarySerializer：伪造超大长度前缀不得导致 OOM ──
        public static void TestBinarySerializerCorruptLengthPrefixThrows()
        {
            var w = new BinarySerializer();
            w.WriteInt(int.MaxValue); // 伪造超大字符串长度
            w.Reset();
            bool threw = false;
            try
            {
                w.ReadString();
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }
            AssertTrue(threw, "Corrupt length prefix must throw InvalidOperationException, not OOM.");
        }

        // ── BinarySerializer：未知类型标签必须抛异常（而非静默 null） ──
        public static void TestBinarySerializerUnknownTypeTagThrows()
        {
            var w = new BinarySerializer();
            w.WriteType((SerializedType)250); // 非法标签
            w.Reset();
            bool threw = false;
            try
            {
                w.ReadObject();
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }
            AssertTrue(threw, "Unknown type tag must throw InvalidOperationException.");
        }

        // ── BinarySerializer：正常 round-trip 仍正确 ──
        public static void TestBinarySerializerRoundTripStillWorks()
        {
            var w = new BinarySerializer();
            w.WriteInt(-123);
            w.WriteFloat(3.14159f);
            w.WriteString("hello");
            w.WriteBool(true);
            w.WriteLong(1L << 40);

            w.Reset();
            AssertEqual(-123, w.ReadInt(), "int");
            AssertTrue(MathF.Abs(w.ReadFloat() - 3.14159f) < 1e-5f, "float");
            AssertEqual("hello", w.ReadString(), "string");
            AssertTrue(w.ReadBool(), "bool");
            AssertEqual(1L << 40, w.ReadLong(), "long");
        }

        // ── RingBuffer：零容量必须拒绝 ──
        public static void TestRingBufferZeroCapacityRejected()
        {
            bool threw = false;
            try { _ = new RingBuffer<int>(0); }
            catch (ArgumentOutOfRangeException) { threw = true; }
            AssertTrue(threw, "RingBuffer capacity 0 must throw ArgumentOutOfRangeException.");
        }

        // ── RingBuffer：空队列出队必须抛异常 ──
        public static void TestRingBufferDequeueEmptyThrows()
        {
            var rb = new RingBuffer<int>(4);
            bool threw = false;
            try { rb.Dequeue(); }
            catch (InvalidOperationException) { threw = true; }
            AssertTrue(threw, "Dequeue on empty RingBuffer must throw.");
        }

        // ── SparseSet：负 ID 必须拒绝 ──
        public static void TestSparseSetNegativeIdRejected()
        {
            var ss = new SparseSet<int>();
            bool threw = false;
            try { ss.Add(-1, 42); }
            catch (ArgumentOutOfRangeException) { threw = true; }
            AssertTrue(threw, "SparseSet.Add(-1) must throw ArgumentOutOfRangeException.");
        }

        // ── TokenList：generation 复用后旧 token 失效（ABA 防护） ──
        public static void TestTokenListGenerationInvalidatesStaleToken()
        {
            var tl = new TokenList<string>();
            var t1 = tl.Add("first");
            AssertTrue(tl.Remove(t1), "Remove t1");
            // 复用同一槽位
            var t2 = tl.Add("second");
            AssertTrue(t1.Id == t2.Id, "Slot must be reused.");
            AssertTrue(t1.Generation != t2.Generation, "Generation must increment on reuse.");
            AssertTrue(tl.Get(t1) == null, "Stale token must be invalidated.");
            AssertEqual("second", tl.Get(t2), "Fresh token must resolve.");
        }

        // ── MathUtils：AngleBetween 对零向量安全 ──
        public static void TestAngleBetweenZeroVectorSafe()
        {
            float a = MathUtils.AngleBetween(Vector3.Zero, Vector3.UnitX);
            AssertTrue(a == 0, "AngleBetween with zero vector must return 0.");
        }

        // ── MathUtils：DistanceToSegment 退化线段退化为点距 ──
        public static void TestDistanceToSegmentDegenerateSegment()
        {
            var p = new Vector3(3, 4, 0);
            float d = MathUtils.DistanceToSegment(p, Vector3.Zero, Vector3.Zero);
            AssertTrue(MathF.Abs(d - 5) < 1e-4f, "Degenerate segment must equal point-to-point distance.");
        }

        // ── GeometryUtils：空数组 AABB 必须抛异常 ──
        public static void TestComputeAABBEmptyThrows()
        {
            bool threw = false;
            try { GeometryUtils.ComputeAABB(Array.Empty<Vector3>(), out _, out _); }
            catch (ArgumentException) { threw = true; }
            AssertTrue(threw, "ComputeAABB on empty array must throw.");
        }

        // ── MathUtils：PointInTriangle 退化三角形返回 false ──
        public static void TestPointInTriangleDegenerate()
        {
            var a = new Vector2(0, 0);
            var b = new Vector2(1, 1);
            var c = new Vector2(2, 2); // 共线 → 退化
            AssertTrue(!MathUtils.PointInTriangle(new Vector2(0.5f, 0.5f), a, b, c),
                "Degenerate triangle must return false.");
        }

        // ── YamaRandom：零种子不得产生全零序列 ──
        public static void TestYamaRandomZeroSeedNonDegenerate()
        {
            var rng = new YamaRandom(0);
            bool anyNonZero = false;
            for (int i = 0; i < 10; i++)
                if (rng.Next() != 0) { anyNonZero = true; break; }
            AssertTrue(anyNonZero, "Seed 0 must not produce constant-zero output.");
        }

        // ── HalfPrecision：极端下溢不产生未定义行为 ──
        public static void TestHalfPrecisionUnderflowSafe()
        {
            ushort h = HalfPrecision.FloatToHalf(float.Epsilon);
            AssertTrue(h == 0, "Denormal-min float must underflow to half zero (no UB).");
        }

        // ── TimeManager：SetTimeout 负延迟拒绝 ──
        public static void TestTimeManagerNegativeTimeoutRejected()
        {
            var tm = TimeManager.Instance;
            bool threw = false;
            try { tm.SetTimeout(() => { }, -1); }
            catch (ArgumentOutOfRangeException) { threw = true; }
            AssertTrue(threw, "SetTimeout(-1) must throw ArgumentOutOfRangeException.");
        }

        // ── TimeManager：SetInterval 非正间隔拒绝 ──
        public static void TestTimeManagerNonPositiveIntervalRejected()
        {
            var tm = TimeManager.Instance;
            bool threw = false;
            try { tm.SetInterval(() => { }, 0); }
            catch (ArgumentOutOfRangeException) { threw = true; }
            AssertTrue(threw, "SetInterval(0) must throw ArgumentOutOfRangeException.");
        }

        // ── TimeManager：帧计数不重复递增（修复回归） ──
        public static void TestTimeManagerFrameCountNoDoubleIncrement()
        {
            var tm = TimeManager.Instance;
            tm.Reset();
            tm.Update(); // 第一帧：_lastFrameTime <= 0 → 仅一次递增
            AssertEqual(1UL, tm.FrameCount, "FrameCount after first Update must be 1.");
        }

        // ── YamaRandom.Next(max) 非正拒绝 ──
        public static void TestYamaRandomNextNonPositiveRejected()
        {
            var rng = new YamaRandom(1);
            bool threw = false;
            try { rng.Next(0); }
            catch (ArgumentOutOfRangeException) { threw = true; }
            AssertTrue(threw, "Next(0) must throw ArgumentOutOfRangeException.");
        }
    }
}
