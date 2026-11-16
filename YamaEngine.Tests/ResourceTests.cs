// ResourceTests.cs —— 追平 Godot 3 资源缓存语义：ResourceCache LRU
// 覆盖：Add/Get 命中、LRU 驱逐最久未使用、容量上限、Contains/Remove/Dispose 释放、
//       TotalSizeBytes 统计、路径归一化（大小写/分隔符不敏感）。

using System;
using System.IO;
using YamaEngine.Assets;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class ResourceTests
    {
        // ── 不可释放的占位资源（无 IDisposable） ──
        private sealed class PlainResource { public int Id; }

        // ── 可释放占位资源（计 Dispose 调用） ──
        private sealed class DisposingResource : IDisposable
        {
            public int Id;
            public bool Disposed;
            public void Dispose() => Disposed = true;
        }

        // ── Add 后 Get 命中，同一实例 ──
        public static void TestCacheAddGetReturnsSameInstance()
        {
            using var cache = new ResourceCache();
            var res = new PlainResource { Id = 1 };
            cache.Add("res/a", res, 100);

            AssertTrue(cache.Contains("res/a"), "Cache must contain added key.");
            var got = cache.Get<PlainResource>("res/a");
            AssertTrue(ReferenceEquals(res, got), "Get must return the same instance added.");
            AssertEqual(1, cache.Count, "Cache count must be 1.");
        }

        // ── 未命中返回 null ──
        public static void TestCacheMissReturnsNull()
        {
            using var cache = new ResourceCache();
            var got = cache.Get<PlainResource>("res/missing");
            AssertTrue(got == null, "Missing key must return null.");
            AssertTrue(!cache.Contains("res/missing"), "Contains must be false for missing key.");
        }

        // ── 容量上限：超限驱逐最久未使用 ──
        public static void TestCacheEvictsLeastRecentlyUsed()
        {
            using var cache = new ResourceCache(maxItems: 3);
            var a = new PlainResource { Id = 1 };
            var b = new PlainResource { Id = 2 };
            var c = new PlainResource { Id = 3 };
            cache.Add("a", a);
            cache.Add("b", b);
            cache.Add("c", c);

            // 访问 a、b 使 c 成为最久未使用
            cache.Get("a");
            cache.Get("b");

            // 加入 d → 超容量 → 驱逐 c（最久未使用）
            var d = new PlainResource { Id = 4 };
            cache.Add("d", d);

            AssertTrue(!cache.Contains("c"), "LRU must evict least-recently-used 'c'.");
            AssertTrue(cache.Contains("a"), "Recently accessed 'a' must survive.");
            AssertTrue(cache.Contains("b"), "Recently accessed 'b' must survive.");
            AssertTrue(cache.Contains("d"), "Newly added 'd' must be present.");
            AssertEqual(3, cache.Count, "Cache must respect max capacity.");
        }

        // ── 重新 Add 已存在 key 更新而非新增 ──
        public static void TestCacheReAddUpdatesExisting()
        {
            using var cache = new ResourceCache();
            cache.Add("x", new PlainResource { Id = 1 });
            var updated = new PlainResource { Id = 2 };
            cache.Add("x", updated);

            AssertEqual(1, cache.Count, "Re-adding same key must not increase count.");
            var got = cache.Get<PlainResource>("x");
            AssertTrue(ReferenceEquals(updated, got), "Re-add must replace resource instance.");
        }

        // ── Remove 释放 IDisposable 资源并移除 ──
        public static void TestCacheRemoveDisposesResource()
        {
            using var cache = new ResourceCache();
            var res = new DisposingResource { Id = 1 };
            cache.Add("disp", res);
            AssertTrue(cache.Contains("disp"), "Must contain before Remove.");

            bool removed = cache.Remove("disp");
            AssertTrue(removed, "Remove existing key must return true.");
            AssertTrue(res.Disposed, "Remove must dispose IDisposable resource.");
            AssertTrue(!cache.Contains("disp"), "Key must be gone after Remove.");
            AssertEqual(0, cache.Count, "Count must drop to 0.");

            AssertTrue(!cache.Remove("disp"), "Remove missing key must return false.");
        }

        // ── LRU 驱逐也释放 IDisposable 资源 ──
        public static void TestCacheEvictionDisposesResource()
        {
            using var cache = new ResourceCache(maxItems: 1);
            var first = new DisposingResource { Id = 1 };
            cache.Add("first", first);

            cache.Add("second", new DisposingResource { Id = 2 }); // 驱逐 first

            AssertTrue(first.Disposed, "Evicted IDisposable resource must be disposed.");
            AssertTrue(!cache.Contains("first"), "Evicted key must be gone.");
        }

        // ── Clear 释放所有 IDisposable 资源 ──
        public static void TestCacheClearDisposesAll()
        {
            using var cache = new ResourceCache();
            var r1 = new DisposingResource { Id = 1 };
            var r2 = new DisposingResource { Id = 2 };
            cache.Add("r1", r1);
            cache.Add("r2", r2);

            cache.Clear();
            AssertEqual(0, cache.Count, "Clear must empty the cache.");
            AssertTrue(r1.Disposed && r2.Disposed, "Clear must dispose all IDisposable resources.");
        }

        // ── TotalSizeBytes 累加 sizeBytes ──
        public static void TestCacheTotalSizeBytesAccumulates()
        {
            using var cache = new ResourceCache();
            cache.Add("a", new PlainResource(), 100);
            cache.Add("b", new PlainResource(), 250);
            AssertEqual(350UL, cache.TotalSizeBytes, "TotalSizeBytes must sum added sizes.");

            cache.Remove("a");
            AssertEqual(250UL, cache.TotalSizeBytes, "Remove must subtract that resource's size.");
        }

        // ── 路径归一化：大小写/正反斜杠不敏感 ──
        public static void TestCachePathNormalization()
        {
            using var cache = new ResourceCache();
            cache.Add("Assets/Textures/A.png", new PlainResource());

            // 反斜杠 + 不同大小写应命中同一键
            AssertTrue(cache.Contains("assets\\textures\\a.PNG"),
                "Path normalization must be case/separator-insensitive.");
            AssertTrue(cache.Get("Assets\\Textures\\A.png") != null,
                "Get with alternate path form must hit.");
            AssertEqual(1, cache.Count, "Equivalent paths must map to one entry.");
        }

        // ── MaxItems=0 表示无上限（不驱逐） ──
        public static void TestCacheUnlimitedCapacity()
        {
            using var cache = new ResourceCache(maxItems: 0);
            for (int i = 0; i < 50; i++)
                cache.Add($"k{i}", new PlainResource { Id = i });

            AssertEqual(50, cache.Count, "Unlimited cache must retain all entries.");
            for (int i = 0; i < 50; i++)
                AssertTrue(cache.Contains($"k{i}"), $"Entry k{i} must survive in unlimited cache.");
        }

        // ── Get 非 IDisposable 普通对象不抛异常 ──
        public static void TestCachePlainObjectNoDisposeCrash()
        {
            using var cache = new ResourceCache();
            cache.Add("plain", new PlainResource());
            bool removed = cache.Remove("plain");
            AssertTrue(removed, "Remove plain object must succeed without dispose crash.");
        }
    }
}
