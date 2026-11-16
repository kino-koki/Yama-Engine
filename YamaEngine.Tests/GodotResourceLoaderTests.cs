// GodotResourceLoaderTests.cs —— 追平 Godot 3 的 ResourceLoader（引用计数/缓存/依赖/分帧线程加载）

using System;
using System.Collections.Generic;
using YamaEngine.UI;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class GodotResourceLoaderTests
    {
        private static void AssertEqual<T>(T got, T want, string msg)
        {
            if (!Equals(got, want)) throw new Exception($"{msg} (got {got}, want {want}).");
        }

        private static void AssertTrue(bool cond, string msg)
        {
            if (!cond) throw new Exception($"{msg}");
        }

        private static void AssertFalse(bool cond, string msg)
        {
            if (cond) throw new Exception($"{msg}");
        }

        // ── 内存后端：按路径返回预置文本资源 ──
        private sealed class MemoryBackend : IResourceBackend
        {
            public readonly Dictionary<string, string> Files = new(StringComparer.OrdinalIgnoreCase);
            public bool Exists(string path) => Files.ContainsKey(path);
            public Resource Load(string path, GodotResourceLoader loader)
            {
                return new TextResource { Text = Files[path] };
            }
        }

        // ── 复合后端：加载时创建依赖子资源 ──
        private sealed class CompositeBackend : IResourceBackend
        {
            public bool Exists(string path) => path == "root" || path == "child";
            public Resource Load(string path, GodotResourceLoader loader)
            {
                if (path == "child") return new TextResource { Text = "child" };
                var comp = new CompositeResource();
                var child = loader.Load<TextResource>("child")!; // 依赖
                comp.Children.Add(child);
                return comp;
            }
        }

        // ── Resource 引用计数：reference/unreference ──
        public static void TestResourceRefCount()
        {
            var r = new TextResource { Text = "x" };
            AssertEqual(0, r.ReferenceCount, "Initial 0.");
            r.Reference();
            AssertEqual(1, r.ReferenceCount, "After reference 1.");
            r.Reference();
            AssertEqual(2, r.ReferenceCount, "After second reference 2.");
            AssertFalse(r.Unreference(), "Not zero after one unref.");
            AssertTrue(r.Unreference(), "Zero after second unref.");
        }

        // ── Resource 类型名 ──
        public static void TestResourceType()
        {
            var r = new TextResource();
            AssertEqual("TextResource", r.ResourceType, "Type name.");
        }

        // ── Load 同步：创建 + 缓存 + 首次引用 ──
        public static void TestLoadCreatesAndCaches()
        {
            var backend = new MemoryBackend();
            backend.Files["a"] = "hello";
            var loader = new GodotResourceLoader(backend);

            var res = loader.Load<TextResource>("a");
            AssertTrue(res != null, "Loaded.");
            AssertEqual("hello", res!.Text, "Text content.");
            AssertEqual("a", res.ResourcePath, "Path set.");
            AssertTrue(loader.HasCached("a"), "Cached.");
        }

        // ── Load 缓存命中：同一实例 + 引用计数增加 ──
        public static void TestLoadCacheHit()
        {
            var backend = new MemoryBackend();
            backend.Files["a"] = "hello";
            var loader = new GodotResourceLoader(backend);

            var r1 = loader.Load<TextResource>("a");
            var r2 = loader.Load<TextResource>("a");
            AssertTrue(ReferenceEquals(r1, r2), "Same instance from cache.");
            AssertTrue(r1!.ReferenceCount >= 2, "RefCount >= 2 (two loads).");
        }

        // ── Load 不存在路径返回 null ──
        public static void TestLoadMissing()
        {
            var backend = new MemoryBackend();
            var loader = new GodotResourceLoader(backend);
            AssertTrue(loader.Load<TextResource>("missing") == null, "Missing → null.");
        }

        // ── Unload 释放 loader 持有引用 ──
        public static void TestUnload()
        {
            var backend = new MemoryBackend();
            backend.Files["a"] = "x";
            var loader = new GodotResourceLoader(backend);

            var r = loader.Load<TextResource>("a");
            int before = r!.ReferenceCount;
            AssertTrue(loader.Unload("a"), "Unload succeeded.");
            AssertFalse(loader.HasCached("a"), "Removed from cache.");
            AssertEqual(before - 1, r.ReferenceCount, "RefCount dropped by 1.");
        }

        // ── Unload 未缓存返回 false ──
        public static void TestUnloadNotCached()
        {
            var backend = new MemoryBackend();
            var loader = new GodotResourceLoader(backend);
            AssertFalse(loader.Unload("nope"), "Not cached → false.");
        }

        // ── ClearCache 清空 ──
        public static void TestClearCache()
        {
            var backend = new MemoryBackend();
            backend.Files["a"] = "1";
            backend.Files["b"] = "2";
            var loader = new GodotResourceLoader(backend);
            loader.Load<TextResource>("a");
            loader.Load<TextResource>("b");
            AssertEqual(2, loader.CacheCount, "Two cached.");
            loader.ClearCache();
            AssertEqual(0, loader.CacheCount, "Cleared.");
        }

        // ── IgnoreCache 绕过缓存重新加载 ──
        public static void TestIgnoreCache()
        {
            var backend = new MemoryBackend();
            backend.Files["a"] = "v1";
            var loader = new GodotResourceLoader(backend);

            var r1 = loader.Load<TextResource>("a");
            backend.Files["a"] = "v2"; // 后端内容变了
            var r2 = loader.Load<TextResource>("a", CacheMode.IgnoreCache);
            AssertFalse(ReferenceEquals(r1, r2), "IgnoreCache loads fresh instance.");
            AssertEqual("v2", r2!.Text, "Fresh content.");
        }

        // ── 依赖加载：Composite 后端加载子依赖 ──
        public static void TestDependencyLoad()
        {
            var loader = new GodotResourceLoader(new CompositeBackend());
            var comp = loader.Load<CompositeResource>("root");
            AssertTrue(comp != null, "Root loaded.");
            AssertEqual(1, comp!.Children.Count, "One child.");
            var child = comp.Children[0] as TextResource;
            AssertEqual("child", child!.Text, "Child content.");
            AssertTrue(loader.HasCached("child"), "Child cached too.");
        }

        // ── 线程加载：request → tick → status → retrieve ──
        public static void TestThreadedLoadFlow()
        {
            var backend = new MemoryBackend();
            backend.Files["x"] = "data";
            var loader = new GodotResourceLoader(backend);

            AssertEqual(LoadThreadedStatus.Invalid, loader.LoadThreadedGetStatus("x"), "Before request: Invalid.");
            loader.LoadThreadedRequest("x");
            AssertEqual(LoadThreadedStatus.InProgress, loader.LoadThreadedGetStatus("x"), "After request: InProgress.");

            loader.LoadThreadedTick();
            AssertEqual(LoadThreadedStatus.Loaded, loader.LoadThreadedGetStatus("x"), "After tick: Loaded.");

            var res = loader.LoadThreadedRetrieve<TextResource>("x");
            AssertTrue(res != null, "Retrieved.");
            AssertEqual("data", res!.Text, "Content.");
            AssertEqual(LoadThreadedStatus.Invalid, loader.LoadThreadedGetStatus("x"), "Task cleared after retrieve.");
        }

        // ── 线程加载失败：后端不存在 ──
        public static void TestThreadedLoadFailed()
        {
            var backend = new MemoryBackend();
            var loader = new GodotResourceLoader(backend);
            loader.LoadThreadedRequest("ghost");
            loader.LoadThreadedTick();
            AssertEqual(LoadThreadedStatus.Failed, loader.LoadThreadedGetStatus("ghost"), "Failed.");
            AssertTrue(loader.LoadThreadedGetError("ghost") != null, "Error message set.");
            // Retrieve 失败任务返回 null
            AssertTrue(loader.LoadThreadedRetrieve<TextResource>("ghost") == null, "Retrieve failed → null.");
        }

        // ── 线程加载缓存：完成后进缓存 ──
        public static void TestThreadedLoadCaches()
        {
            var backend = new MemoryBackend();
            backend.Files["x"] = "d";
            var loader = new GodotResourceLoader(backend);
            loader.LoadThreadedRequest("x");
            loader.LoadThreadedTick();
            AssertTrue(loader.HasCached("x"), "Threaded load cached.");
            // 同步 Load 命中缓存
            var r = loader.Load<TextResource>("x");
            AssertEqual("d", r!.Text, "Sync load hits cache.");
        }

        // ── 重复 request 同路径无副作用 ──
        public static void TestThreadedRequestDedup()
        {
            var backend = new MemoryBackend();
            backend.Files["x"] = "d";
            var loader = new GodotResourceLoader(backend);
            loader.LoadThreadedRequest("x");
            loader.LoadThreadedRequest("x");
            AssertEqual(1, loader.ThreadedTaskCount, "Dedup to one task.");
        }

        // ── LoadThreadedGetStatus 未知路径 Invalid ──
        public static void TestThreadedGetStatusUnknown()
        {
            var loader = new GodotResourceLoader(new MemoryBackend());
            AssertEqual(LoadThreadedStatus.Invalid, loader.LoadThreadedGetStatus("never"), "Unknown → Invalid.");
        }

        // ── ClearThreadedTasks ──
        public static void TestClearThreadedTasks()
        {
            var backend = new MemoryBackend();
            backend.Files["x"] = "d";
            var loader = new GodotResourceLoader(backend);
            loader.LoadThreadedRequest("x");
            loader.LoadThreadedTick(); // 完成
            loader.ClearThreadedTasks();
            AssertEqual(0, loader.ThreadedTaskCount, "Tasks cleared.");
            AssertEqual(LoadThreadedStatus.Invalid, loader.LoadThreadedGetStatus("x"), "Status Invalid after clear.");
        }

        // ── 多资源缓存计数 ──
        public static void TestCacheCount()
        {
            var backend = new MemoryBackend();
            backend.Files["a"] = "1";
            backend.Files["b"] = "2";
            backend.Files["c"] = "3";
            var loader = new GodotResourceLoader(backend);
            loader.Load<TextResource>("a");
            loader.Load<TextResource>("b");
            loader.Load<TextResource>("c");
            AssertEqual(3, loader.CacheCount, "Three cached.");
            loader.Unload("b");
            AssertEqual(2, loader.CacheCount, "Two after unload.");
        }
    }
}
