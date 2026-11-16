// ResourceSaverTests.cs —— 追平 Godot 3 的 ResourceSaver（自定义资源序列化 + 持久化 round-trip）

using System;
using System.Collections.Generic;
using System.Linq;
using YamaEngine.UI;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class ResourceSaverTests
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

        private static void AssertThrows<TEx>(Action a, string msg) where TEx : Exception
        {
            try { a(); }
            catch (TEx) { return; }
            catch (Exception ex) { throw new Exception($"{msg} (wrong exception {ex.GetType().Name})."); }
            throw new Exception($"{msg} (no exception).");
        }

        // ── 可写存储后端：实现 Write + Exists + Load（Load 用 TextResourceSerializer 反序列化）──
        private sealed class RoundTripBackend : IResourceStorageBackend
        {
            public readonly Dictionary<string, string> Files = new(StringComparer.OrdinalIgnoreCase);
            public void Write(string path, string data) => Files[path] = data;
            public bool Exists(string path) => Files.ContainsKey(path);
            public Resource Load(string path, GodotResourceLoader loader)
            {
                var res = TextResourceSerializer.Deserialize(Files[path]);
                return res ?? throw new InvalidOperationException("Bad data.");
            }
        }

        // ── 内存只读后端（用于验证 Save 写入独立于加载）──
        private sealed class WriteOnlyBackend : IResourceStorageBackend
        {
            public readonly Dictionary<string, string> Files = new(StringComparer.OrdinalIgnoreCase);
            public void Write(string path, string data) => Files[path] = data;
            public bool Exists(string path) => Files.ContainsKey(path);
            public Resource Load(string path, GodotResourceLoader loader) =>
                TextResourceSerializer.Deserialize(Files[path])!;
        }

        // ── TextResource 序列化器 round-trip（直接 serialize→deserialize）──
        public static void TestTextSerializerRoundTrip()
        {
            var res = new TextResource { Text = "hello world" };
            var serializer = new TextResourceSerializer();
            string data = serializer.Serialize(res);
            var back = TextResourceSerializer.Deserialize(data)!;
            AssertEqual("hello world", back.Text, "Round-trip text preserved.");
            AssertEqual("TextResource", back.ResourceType, "Type preserved.");
        }

        // ── 序列化器转义换行 ──
        public static void TestTextSerializerEscaping()
        {
            var res = new TextResource { Text = "line1\nline2\r\nline3" };
            string data = new TextResourceSerializer().Serialize(res);
            AssertFalse(data.Contains('\n'), "Serialized single line (no raw LF).");
            var back = TextResourceSerializer.Deserialize(data)!;
            AssertEqual("line1\nline2\r\nline3", back.Text, "Newlines round-trip.");
        }

        // ── 空文本 round-trip ──
        public static void TestTextSerializerEmpty()
        {
            var res = new TextResource { Text = "" };
            string data = new TextResourceSerializer().Serialize(res);
            var back = TextResourceSerializer.Deserialize(data)!;
            AssertEqual("", back.Text, "Empty text preserved.");
        }

        // ── 注册序列化器 ──
        public static void TestRegisterSerializer()
        {
            var saver = new ResourceSaver();
            AssertEqual(0, saver.SerializerCount, "No serializer initially.");
            saver.RegisterSerializer(new TextResourceSerializer());
            AssertEqual(1, saver.SerializerCount, "One registered.");
            AssertTrue(saver.HasSerializer("TextResource"), "Has TextResource.");
            AssertFalse(saver.HasSerializer("CompositeResource"), "Not has CompositeResource.");
        }

        // ── Save 写入后端 + 设置 ResourcePath ──
        public static void TestSaveWritesAndSetsPath()
        {
            var saver = new ResourceSaver();
            saver.RegisterSerializer(new TextResourceSerializer());
            var backend = new WriteOnlyBackend();
            var res = new TextResource { Text = "data" };

            saver.Save(res, "res/a", backend);
            AssertTrue(backend.Files.ContainsKey("res/a"), "Written to backend.");
            AssertTrue(backend.Files["res/a"].StartsWith("TextResource|"), "Serialized prefix.");
            AssertEqual("res/a", res.ResourcePath, "ResourcePath set by save.");
        }

        // ── Save round-trip：Save → GodotResourceLoader.Load 得回等价资源 ──
        public static void TestSaveRoundTripViaLoader()
        {
            var saver = new ResourceSaver();
            saver.RegisterSerializer(new TextResourceSerializer());
            var backend = new RoundTripBackend();
            var loader = new GodotResourceLoader(backend);

            var original = new TextResource { Text = "persisted" };
            saver.Save(original, "res/x", backend);

            var loaded = loader.Load<TextResource>("res/x");
            AssertTrue(loaded != null, "Loaded after save.");
            AssertEqual("persisted", loaded!.Text, "Content round-trip via loader.");
        }

        // ── Save 未注册类型抛异常 ──
        public static void TestSaveUnregisteredTypeThrows()
        {
            var saver = new ResourceSaver();
            var backend = new WriteOnlyBackend();
            var res = new TextResource { Text = "x" }; // TextResource 未注册
            AssertThrows<InvalidOperationException>(
                () => saver.Save(res, "a", backend),
                "Unregistered type throws.");
        }

        // ── Save null 参数抛异常 ──
        public static void TestSaveNullArguments()
        {
            var saver = new ResourceSaver();
            saver.RegisterSerializer(new TextResourceSerializer());
            var backend = new WriteOnlyBackend();
            AssertThrows<ArgumentNullException>(
                () => saver.Save(null!, "a", backend), "Null resource throws.");
            AssertThrows<ArgumentNullException>(
                () => saver.Save(new TextResource(), null!, backend), "Null path throws.");
            AssertThrows<ArgumentNullException>(
                () => saver.Save(new TextResource(), "a", null!), "Null backend throws.");
        }

        // ── 序列化器类型不符抛异常 ──
        public static void TestSerializerWrongTypeThrows()
        {
            var serializer = new TextResourceSerializer();
            var comp = new CompositeResource();
            AssertThrows<ArgumentException>(
                () => serializer.Serialize(comp), "Wrong resource type throws.");
        }

        // ── 多次 Save 同路径覆盖 ──
        public static void TestMultipleSavesOverwrite()
        {
            var saver = new ResourceSaver();
            saver.RegisterSerializer(new TextResourceSerializer());
            var backend = new WriteOnlyBackend();

            saver.Save(new TextResource { Text = "v1" }, "res/a", backend);
            saver.Save(new TextResource { Text = "v2" }, "res/a", backend);
            AssertTrue(backend.Files["res/a"].EndsWith("v2"), "Overwritten with v2.");
        }

        // ── 完整 round-trip 多轮（save→load→save）──
        public static void TestRoundTripStable()
        {
            var saver = new ResourceSaver();
            saver.RegisterSerializer(new TextResourceSerializer());
            var backend = new RoundTripBackend();
            var loader = new GodotResourceLoader(backend);

            saver.Save(new TextResource { Text = "stable\ntext" }, "res/s", backend);
            var l1 = loader.Load<TextResource>("res/s")!;
            saver.Save(l1, "res/s", backend); // 再存
            var l2 = loader.Load<TextResource>("res/s")!;
            AssertEqual("stable\ntext", l2.Text, "Stable over multiple round-trips.");
        }

        // ── 多个资源分别保存到不同路径 ──
        public static void TestSaveMultipleResources()
        {
            var saver = new ResourceSaver();
            saver.RegisterSerializer(new TextResourceSerializer());
            var backend = new RoundTripBackend();
            var loader = new GodotResourceLoader(backend);

            saver.Save(new TextResource { Text = "A" }, "res/a", backend);
            saver.Save(new TextResource { Text = "B" }, "res/b", backend);
            saver.Save(new TextResource { Text = "C" }, "res/c", backend);
            AssertEqual("A", loader.Load<TextResource>("res/a")!.Text, "A loaded.");
            AssertEqual("B", loader.Load<TextResource>("res/b")!.Text, "B loaded.");
            AssertEqual("C", loader.Load<TextResource>("res/c")!.Text, "C loaded.");
            AssertEqual(3, backend.Files.Count, "Three files stored.");
        }
    }
}
