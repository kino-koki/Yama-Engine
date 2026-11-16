// VulkanBackendTests.cs — Vulkan 图形后端深度验证（无需 GPU 的纯逻辑层）
// 覆盖 ResourceStateTracker：Yama Vulkan 后端（唯一图形后端）的 barrier 消除核心。
//   它跟踪 image/buffer 的 (layout, accessMask, stageMask) 状态，
//   在每次资源访问前判断是否真的需要 vkCmdPipelineBarrier，
//   消除冗余 barrier（Godot3/UE 渲染后端的关键性能优化）。
// Image/Buffer 为 Silk.NET 不透明句柄（包 ulong Handle），可直接构造，无需 GPU 设备。

using System;
using System.Collections.Generic;
using YamaEngine.Vulkan;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class VulkanBackendTests
    {
        // ── 未记录的 image：首次访问从 Undefined 布局转换（需要 barrier） ──
        public static void TestUntrackedImageNeedsUndefinedTransition()
        {
            var tracker = new ResourceStateTracker();
            var img = new Image { Handle = 0xC0FFEE };
            AssertTrue(!tracker.TryGetImageState(img, out _), "Untracked image must have no state.");

            bool need = tracker.NeedsImageBarrier(img, ImageLayout.ShaderReadOnlyOptimal,
                AccessFlags.ShaderReadBit, out var req);
            AssertTrue(need, "First access to an untracked image must require a barrier.");
            AssertTrue(req.OldLayout == ImageLayout.Undefined,
                "Untracked image barrier must originate from Undefined layout.");
            AssertTrue(req.NewLayout == ImageLayout.ShaderReadOnlyOptimal,
                "Barrier new layout must match requested layout.");
        }

        // ── 已在目标状态且 access mask 已含：冗余 barrier 被消除 ──
        public static void TestRedundantImageBarrierEliminated()
        {
            var tracker = new ResourceStateTracker();
            var img = new Image { Handle = 0x42 };
            // 记录：已是 ShaderReadOnlyOptimal，access 已含 ShaderRead
            tracker.SetImageState(img, ImageLayout.ShaderReadOnlyOptimal,
                AccessFlags.ShaderReadBit, PipelineStageFlags.FragmentShaderBit);

            bool need = tracker.NeedsImageBarrier(img, ImageLayout.ShaderReadOnlyOptimal,
                AccessFlags.ShaderReadBit, out _);
            AssertTrue(!need,
                "Access to an image already in target layout+access must NOT need a barrier (redundant).");
        }

        // ── 布局变化：即使 access 覆盖，layout 不同仍需 barrier ──
        public static void TestLayoutChangeNeedsBarrier()
        {
            var tracker = new ResourceStateTracker();
            var img = new Image { Handle = 0x43 };
            tracker.SetImageState(img, ImageLayout.ColorAttachmentOptimal,
                AccessFlags.ColorAttachmentWriteBit, PipelineStageFlags.ColorAttachmentOutputBit);

            bool need = tracker.NeedsImageBarrier(img, ImageLayout.ShaderReadOnlyOptimal,
                AccessFlags.ShaderReadBit, out var req);
            AssertTrue(need, "Layout change must require a barrier even if access overlaps.");
            AssertTrue(req.OldLayout == ImageLayout.ColorAttachmentOptimal,
                "Barrier old layout must be the recorded layout.");
            AssertTrue(req.NewLayout == ImageLayout.ShaderReadOnlyOptimal,
                "Barrier new layout must be the target layout.");
        }

        // ── Buffer barrier：access mask 覆盖则消除、不覆盖则插入 ──
        public static void TestBufferBarrierAccessOverlapLogic()
        {
            var tracker = new ResourceStateTracker();
            var buf = new VkBuffer { Handle = 0xBEEF };
            // 未记录的 buffer：不插 barrier（保守跳过）
            bool untracked = tracker.NeedsBufferBarrier(buf, AccessFlags.VertexAttributeReadBit, out _);
            AssertTrue(!untracked, "Untracked buffer must skip barrier (no state recorded).");

            // 记录写访问
            tracker.SetBufferState(buf, AccessFlags.TransferWriteBit,
                PipelineStageFlags.TransferBit);
            // 读访问与写 access 不重叠 → 需要 barrier
            bool needRead = tracker.NeedsBufferBarrier(buf, AccessFlags.VertexAttributeReadBit, out var req);
            AssertTrue(needRead, "Write→Read (WAW→RAW) transition must need a buffer barrier.");
            AssertTrue((req.SrcAccess & AccessFlags.TransferWriteBit) != 0,
                "Barrier src access must carry recorded TransferWrite.");

            // 相同 access（仍想 TransferWrite）→ 冗余，消除
            bool redundant = tracker.NeedsBufferBarrier(buf, AccessFlags.TransferWriteBit, out _);
            AssertTrue(!redundant, "Same access mask on buffer must be redundant (no barrier).");
        }

        // ── Clear 后状态归零，再访问回到 Undefined 起点 ──
        public static void TestClearResetsState()
        {
            var tracker = new ResourceStateTracker();
            var img = new Image { Handle = 0x55 };
            tracker.SetImageState(img, ImageLayout.ShaderReadOnlyOptimal,
                AccessFlags.ShaderReadBit, PipelineStageFlags.FragmentShaderBit);
            var buf = new VkBuffer { Handle = 0x56 };
            tracker.SetBufferState(buf, AccessFlags.TransferWriteBit, PipelineStageFlags.TransferBit);

            AssertTrue(tracker.ImageStateCount == 1, "Precondition: 1 image state.");
            AssertTrue(tracker.BufferStateCount == 1, "Precondition: 1 buffer state.");

            tracker.Clear();
            AssertTrue(tracker.ImageStateCount == 0, "After Clear, image state count must be 0.");
            AssertTrue(tracker.BufferStateCount == 0, "After Clear, buffer state count must be 0.");

            // 再访问回到未记录起点
            bool need = tracker.NeedsImageBarrier(img, ImageLayout.ShaderReadOnlyOptimal,
                AccessFlags.ShaderReadBit, out var req);
            AssertTrue(need, "After Clear, image must need a fresh barrier from Undefined.");
            AssertTrue(req.OldLayout == ImageLayout.Undefined,
                "After Clear, barrier must originate from Undefined layout.");
        }

        // ════════════════════════════════════════════════════════════════════════
        // 描述符池 / 内存类型 / 查询池 —— 纯逻辑桩函数
        // ════════════════════════════════════════════════════════════════════════

        // ── 描述符布局哈希：同 bindings 稳定一致；binding 顺序不同 → 哈希不同 ──
        public static void TestDescriptorLayoutHashDeterminism()
        {
            var a = new[]
            {
                new DescriptorSetLayoutBinding { Binding = 0 },
                new DescriptorSetLayoutBinding { Binding = 1 },
            };
            var b = new[]
            {
                new DescriptorSetLayoutBinding { Binding = 0 },
                new DescriptorSetLayoutBinding { Binding = 1 },
            };
            var swapped = new[]
            {
                new DescriptorSetLayoutBinding { Binding = 1 },
                new DescriptorSetLayoutBinding { Binding = 0 },
            };
            ulong ha = DescriptorPoolTestStub.TestLayoutHash(a);
            ulong hb = DescriptorPoolTestStub.TestLayoutHash(b);
            ulong hs = DescriptorPoolTestStub.TestLayoutHash(swapped);
            AssertTrue(ha == hb, "Identical binding sets must produce identical layout hash.");
            AssertTrue(ha != hs, "Different binding order must produce a different layout hash.");
        }

        // ── 描述符配额：缺类型返回 false；配额足够返回 true；超出返回 false ──
        public static void TestDescriptorQuotaCheck()
        {
            var quota = DescriptorPoolQuota.Default;
            // 存在的类型：10000 容量，要 5000 → 足够
            AssertTrue(DescriptorPoolTestStub.TestQuotaEnough(quota, DescriptorType.UniformBuffer, 5000),
                "Default quota must cover 5000 uniform buffers.");
            // 超出容量 10000 → 不足
            AssertTrue(!DescriptorPoolTestStub.TestQuotaEnough(quota, DescriptorType.UniformBuffer, 20000),
                "Default quota must reject requests exceeding 10000 uniform buffers.");
            // 不存在的类型 → false
            var empty = new DescriptorPoolQuota { MaxPerType = new Dictionary<DescriptorType, uint>() };
            AssertTrue(!DescriptorPoolTestStub.TestQuotaEnough(empty, DescriptorType.Sampler, 1),
                "Empty quota must reject any descriptor type request.");
        }

        // ── 内存类型选择：选中最小满足位+属性的内存类型；无匹配返回 -1 ──
        public static void TestMemoryTypeSelection()
        {
            // 3 个内存类型：0=DeviceLocal，1=HostVisible|Coherent，2=DeviceLocal|HostVisible
            var flags = new[]
            {
                MemoryPropertyFlags.DeviceLocalBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                MemoryPropertyFlags.DeviceLocalBit | MemoryPropertyFlags.HostVisibleBit,
            };

            // 要 DeviceLocal，全部允许 → 选中 0（最小索引）
            int t0 = AllocatorTestStub.TestMemoryTypeSelection(0b111, MemoryPropertyFlags.DeviceLocalBit, flags);
            AssertTrue(t0 == 0, $"MemoryTypeSelection(DeviceLocal, all) must select 0, got {t0}.");

            // 只要 type 1（位 0b010）的 HostVisible|Coherent → 选中 1
            int t1 = AllocatorTestStub.TestMemoryTypeSelection(0b010,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, flags);
            AssertTrue(t1 == 1, $"MemoryTypeSelection(type1 filter) must select 1, got {t1}.");

            // 要 LazyAllocated（没有任何类型满足）→ 返回 -1
            int none = AllocatorTestStub.TestMemoryTypeSelection(0b111, MemoryPropertyFlags.LazilyAllocatedBit, flags);
            AssertTrue(none == -1, $"MemoryTypeSelection(LazyAllocated) must return -1, got {none}.");

            // typeFilter 全 0 → 无可用类型 → -1
            int nofilter = AllocatorTestStub.TestMemoryTypeSelection(0, MemoryPropertyFlags.DeviceLocalBit, flags);
            AssertTrue(nofilter == -1, "MemoryTypeSelection with empty typeFilter must return -1.");
        }

        // ── Timestamp → ms 转换（查询池计时） ──
        public static void TestTimestampToMs()
        {
            // period 单位 ns/tick。1e6 ticks × 1ns/tick = 1ms
            double ms = QueryPoolTestStub.TestTimestampToMs(1_000_000UL, 1.0f);
            AssertTrue(MathF.Abs((float)ms - 1.0f) < 1e-3f,
                $"TimestampToMs(1e6 ticks, 1ns period) must be 1ms, got {ms:F4}.");
            // period=0.5ns/tick, 2e6 ticks → 1ms
            double ms2 = QueryPoolTestStub.TestTimestampToMs(2_000_000UL, 0.5f);
            AssertTrue(MathF.Abs((float)ms2 - 1.0f) < 1e-3f,
                $"TimestampToMs(2e6 ticks, 0.5ns) must be 1ms, got {ms2:F4}.");
        }

        // ── 内存对齐：向上取整到 alignment 倍数 ──
        public static void TestAllocatorAlignment()
        {
            AssertTrue(AllocatorTestStub.TestAlignment(0UL, 256UL, 0UL),
                "AlignUp(0, 256) must be 0.");
            AssertTrue(AllocatorTestStub.TestAlignment(1UL, 256UL, 256UL),
                "AlignUp(1, 256) must be 256.");
            AssertTrue(AllocatorTestStub.TestAlignment(256UL, 256UL, 256UL),
                "AlignUp(256, 256) must stay 256 (already aligned).");
            AssertTrue(AllocatorTestStub.TestAlignment(257UL, 256UL, 512UL),
                "AlignUp(257, 256) must be 512.");
            // 非二次幂对齐 (Vulkan 常用 64/128/256/512)
            AssertTrue(AllocatorTestStub.TestAlignment(100UL, 64UL, 128UL),
                "AlignUp(100, 64) must be 128.");
        }

        // ── 碎片率：最大空闲块 / 总空闲块 → 1 - maxFree/totalFree ──
        public static void TestAllocatorFragmentation()
        {
            // 全部空闲在一块 → 碎片率 0
            float perfect = AllocatorTestStub.TestFragmentation(new ulong[] { 1024 }, 1024);
            AssertTrue(MathF.Abs(perfect) < 1e-4f,
                $"Single free block must have fragmentation 0, got {perfect:F4}.");

            // 空闲分散成两等块 → maxFree=512, total=1024 → 1 - 0.5 = 0.5
            float half = AllocatorTestStub.TestFragmentation(new ulong[] { 512, 512 }, 512);
            AssertTrue(MathF.Abs(half - 0.5f) < 1e-4f,
                $"Two equal free blocks must have fragmentation 0.5, got {half:F4}.");

            // 高度碎片：4×256 → maxFree=256, total=1024 → 1 - 0.25 = 0.75
            float high = AllocatorTestStub.TestFragmentation(new ulong[] { 256, 256, 256, 256 }, 256);
            AssertTrue(MathF.Abs(high - 0.75f) < 1e-4f,
                $"Four equal free blocks must have fragmentation 0.75, got {high:F4}.");

            // 无空闲 → 0
            float empty = AllocatorTestStub.TestFragmentation(System.Array.Empty<ulong>(), 0);
            AssertTrue(MathF.Abs(empty) < 1e-4f,
                $"Empty free list must have fragmentation 0, got {empty:F4}.");
        }

        // ── 管线状态哈希：FNV-1a 三键，确定性 + 顺序敏感 ──
        public static void TestPipelineStateHash()
        {
            ulong h1 = PipelineCacheTestStub.TestHash(1, 2, 3);
            ulong h1again = PipelineCacheTestStub.TestHash(1, 2, 3);
            ulong h2 = PipelineCacheTestStub.TestHash(3, 2, 1);
            AssertTrue(h1 == h1again, "Pipeline state hash must be deterministic (same keys → same hash).");
            AssertTrue(h1 != h2, "Pipeline state hash must be order-sensitive (1,2,3) vs (3,2,1).");
            // 已知 FNV-1a 空偏移；非零且区分度足够
            AssertTrue(h1 != 0, "Pipeline state hash must not be zero for non-zero keys.");
        }

        // ── 管线缓存命中率：hits/(hits+misses)，空缓存返回 0 ──
        public static void TestPipelineCacheHitRate()
        {
            AssertTrue(PipelineCacheTestStub.TestHitRate(8, 2, 0.8f),
                "Hit rate of 8/10 must be ~0.8.");
            AssertTrue(!PipelineCacheTestStub.TestHitRate(8, 2, 0.5f),
                "Hit rate of 8/10 must NOT be ~0.5.");
            // 空缓存（0 total）→ expected==0 才算成立
            AssertTrue(PipelineCacheTestStub.TestHitRate(0, 0, 0f),
                "Empty cache must report hit rate 0.");
            AssertTrue(!PipelineCacheTestStub.TestHitRate(0, 0, 0.5f),
                "Empty cache must reject non-zero expected hit rate.");
            // 冷缓存全 miss → 0
            AssertTrue(PipelineCacheTestStub.TestHitRate(0, 10, 0f),
                "All-miss cache must report hit rate 0.");
        }

        // ── 同步超时：ms/ns、sec/ns 单位转换 ──
        public static void TestSyncTimeoutConversion()
        {
            AssertTrue(SyncManagerTestStub.TestTimeoutMsToNs(100UL) == 100_000_000UL,
                "100ms must be 1e8 ns.");
            AssertTrue(SyncManagerTestStub.TestTimeoutSecToNs(1UL) == 1_000_000_000UL,
                "1s must be 1e9 ns.");
            AssertTrue(SyncManagerTestStub.TestTimeoutMsToNs(0UL) == 0UL,
                "0ms timeout must be 0 ns.");
            AssertTrue(SyncManagerTestStub.TestTimeoutSecToNs(0UL) == 0UL,
                "0s timeout must be 0 ns.");
        }

        // ── Fence 帧索引：frame-in-flight 环形推进 ──
        public static void TestFenceFrameIndex()
        {
            // 三重缓冲（FIF=3）：current=0 → next=1；current=1 → 2；current=2 → 0（回绕）
            AssertTrue(SyncManagerTestStub.TestFenceFrameIndex(0, 3, 1), "FIF=3: frame 0 must advance to 1.");
            AssertTrue(SyncManagerTestStub.TestFenceFrameIndex(1, 3, 2), "FIF=3: frame 1 must advance to 2.");
            AssertTrue(SyncManagerTestStub.TestFenceFrameIndex(2, 3, 0), "FIF=3: frame 2 must wrap to 0.");
        }

        // ── Swapchain 图像数选择：夹在 [min, max] 之间取 desired ──
        public static void TestSwapchainImageCountSelection()
        {
            // desired 在 [min, max] 内
            AssertTrue(SwapchainTestStub.TestSelectImageCount(2, 8, 3) == 3,
                "ImageCount(2,8,desired=3) must select 3.");
            // desired 小于 min → 取 min
            AssertTrue(SwapchainTestStub.TestSelectImageCount(4, 8, 2) == 4,
                "ImageCount(4,8,desired=2) must clamp up to min=4.");
            // desired 超过 max → 取 max
            AssertTrue(SwapchainTestStub.TestSelectImageCount(2, 4, 8) == 4,
                "ImageCount(2,4,desired=8) must clamp down to max=4.");
            // max==0 表示无上限 → 用 desired（不低于 min）
            AssertTrue(SwapchainTestStub.TestSelectImageCount(2, 0, 6) == 6,
                "ImageCount(2,0,desired=6) with max=0 must select 6.");
        }
    }
}
