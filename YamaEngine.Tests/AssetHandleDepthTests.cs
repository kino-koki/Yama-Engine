// AssetHandleDepthTests.cs — Unity5 AssetHandle / AssetHandleRegistry / SoftAssetPtr 深度验证
// 覆盖:
//   * AssetHandle: Zero/IsValid/Parse/New/ToString/Equals/GetHashCode/==/!=
//   * AssetHandleRegistry: Register/Unregister/Resolve/IsLoaded/Clear/WeakReference 语义
//   * SoftAssetPtr<T>: Get() 懒加载/SetHandle 切换重置缓存/隐式转换

using System;
using YamaEngine.Core;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    // 测试用资产类型
    public class FakeTexture { public string Name { get; set; } = ""; public int Width { get; set; } = 256; }
    public class FakeMesh { public string Name { get; set; } = ""; public int VertexCount { get; set; } = 3; }

    public static class AssetHandleDepthTests
    {
        // =========================================================
        // 1. AssetHandle
        // =========================================================

        /// <summary>Zero handle：Guid=Empty/TypeName=空/IsValid=false。</summary>
        public static void TestHandleZero()
        {
            AssertEqual(Guid.Empty, AssetHandle.Zero.Guid, "Zero.Guid 应为 Empty");
            AssertEqual(string.Empty, AssetHandle.Zero.TypeName, "Zero.TypeName 应为空");
            AssertTrue(!AssetHandle.Zero.IsValid, "Zero 应 IsValid=false");
        }

        /// <summary>New 生成有效句柄，DebugName 可选。</summary>
        public static void TestHandleNew()
        {
            var h = AssetHandle.New("Texture2D", "Brick");
            AssertTrue(h.IsValid, "New 应 IsValid=true");
            AssertEqual("Texture2D", h.TypeName, "TypeName 应为 Texture2D");
            AssertEqual("Brick", h.DebugName, "DebugName 应为 Brick");
            AssertTrue(h.Guid != Guid.Empty, "Guid 应非空");

            var h2 = AssetHandle.New("Mesh");
            AssertTrue(h2.IsValid, "无 DebugName 也应有效");
            AssertTrue(h2.DebugName == null, "DebugName 默认 null");
            AssertTrue(h.Guid != h2.Guid, "两次 New Guid 应不同");
        }

        /// <summary>Parse 合法 Guid 字符串成功，非法字符串回退 Zero。</summary>
        public static void TestHandleParse()
        {
            var g = Guid.NewGuid();
            var h = AssetHandle.Parse(g.ToString("N"), "StaticMesh", "Cube");
            AssertTrue(h.IsValid, "合法字符串 Parse 应有效");
            AssertEqual(g, h.Guid, "Guid 应相等");
            AssertEqual("StaticMesh", h.TypeName, "TypeName 应保留");
            AssertEqual("Cube", h.DebugName, "DebugName 应保留");

            var bad = AssetHandle.Parse("not-a-guid", "Material");
            AssertTrue(!bad.IsValid, "非法字符串 Parse 应回退 Zero");
            AssertEqual(AssetHandle.Zero, bad, "Parse 失败应等于 Zero");
        }

        /// <summary>ToString 形如 "guid:N:TypeName"。</summary>
        public static void TestHandleToString()
        {
            var g = Guid.NewGuid();
            var h = new AssetHandle(g, "Material", "Gold");
            string s = h.ToString();
            AssertTrue(s.Contains(g.ToString("N")), "ToString 应含 N 格式 Guid");
            AssertTrue(s.Contains("Material"), "ToString 应含 TypeName");
            AssertTrue(s.EndsWith(":Material"), "ToString 应以 :TypeName 结尾");
        }

        /// <summary>Equals/==/!= 仅比较 Guid+TypeName，DebugName 不参与。</summary>
        public static void TestHandleEquality()
        {
            var g = Guid.NewGuid();
            var a = new AssetHandle(g, "Texture2D", "A");
            var b = new AssetHandle(g, "Texture2D", "B"); // 同 Guid 同 TypeName，不同 DebugName
            var c = new AssetHandle(g, "Mesh");           // 同 Guid 不同 TypeName

            AssertTrue(a.Equals(b), "同 Guid+TypeName 应 Equals");
            AssertTrue(a == b, "== 应 true");
            AssertTrue(a != c, "同 Guid 不同 TypeName 应 !=");
            AssertTrue(!a.Equals(c), "同 Guid 不同 TypeName 不应 Equals");
            AssertTrue(a.GetHashCode() == b.GetHashCode(), "Equals 对象 GetHashCode 应相等");

            var zero = AssetHandle.Zero;
            AssertTrue(zero == AssetHandle.Zero, "Zero == Zero");
            AssertTrue(zero != a, "Zero != 有效句柄");
        }

        // =========================================================
        // 2. AssetHandleRegistry
        // =========================================================

        /// <summary>Register/Resolve round-trip，Resolve<T> 类型匹配返回实例。</summary>
        public static void TestRegistryRegisterResolve()
        {
            AssetHandleRegistry.Clear();
            var h = AssetHandle.New("FakeTexture", "Wall");
            var tex = new FakeTexture { Name = "Wall" };

            AssetHandleRegistry.Register(h, tex);
            AssertTrue(AssetHandleRegistry.IsLoaded(h), "注册后 IsLoaded=true");

            var resolved = AssetHandleRegistry.Resolve<FakeTexture>(h);
            AssertTrue(resolved != null, "Resolve<FakeTexture> 应命中");
            AssertEqual("Wall", resolved.Name, "Resolve 返回正确对象");

            // 类型不匹配返回 null
            var wrongType = AssetHandleRegistry.Resolve<FakeMesh>(h);
            AssertTrue(wrongType == null, "Resolve<FakeMesh> 类型不匹配应返回 null");

            AssetHandleRegistry.Clear();
        }

        /// <summary>Resolve Zero handle 返回 null（不查表）。</summary>
        public static void TestRegistryResolveZero()
        {
            AssetHandleRegistry.Clear();
            var r = AssetHandleRegistry.Resolve<FakeTexture>(AssetHandle.Zero);
            AssertTrue(r == null, "Resolve Zero 应返回 null");
            AssertTrue(!AssetHandleRegistry.IsLoaded(AssetHandle.Zero), "IsLoaded Zero 应 false");
        }

        /// <summary>Unregister 后 Resolve 返回 null，IsLoaded=false。</summary>
        public static void TestRegistryUnregister()
        {
            AssetHandleRegistry.Clear();
            var h = AssetHandle.New("FakeMesh");
            var mesh = new FakeMesh { Name = "Temp" };
            AssetHandleRegistry.Register(h, mesh);
            AssertTrue(AssetHandleRegistry.IsLoaded(h), "注册后应 loaded");

            AssetHandleRegistry.Unregister(h);
            AssertTrue(!AssetHandleRegistry.IsLoaded(h), "Unregister 后 IsLoaded=false");
            var r = AssetHandleRegistry.Resolve<FakeMesh>(h);
            AssertTrue(r == null, "Unregister 后 Resolve 应 null");

            // 重复 Unregister 不抛异常
            AssetHandleRegistry.Unregister(h);
        }

        /// <summary>WeakReference 语义：无强引用时对象可被 GC，Resolve 返回 null。</summary>
        public static void TestRegistryWeakReferenceSemantics()
        {
            AssetHandleRegistry.Clear();
            var h = AssetHandle.New("FakeTexture");
            var tex = new FakeTexture { Name = "GC" };
            AssetHandleRegistry.Register(h, tex);

            // 仍持有 tex 强引用，Resolve 应命中
            var r = AssetHandleRegistry.Resolve<FakeTexture>(h);
            AssertTrue(r != null, "持有强引用时 Resolve 应命中");

            // 置 null + 强制 GC：WeakReference 可能回收（但不强保证）
            tex = null;
            r = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            // 不强制断言 IsLoaded，因 GC 时机不确定；仅确认不抛异常
            AssetHandleRegistry.Clear();
        }

        /// <summary>Clear 清空所有句柄。</summary>
        public static void TestRegistryClear()
        {
            var h1 = AssetHandle.New("FakeTexture");
            var h2 = AssetHandle.New("FakeMesh");
            AssetHandleRegistry.Register(h1, new FakeTexture());
            AssetHandleRegistry.Register(h2, new FakeMesh());
            AssertTrue(AssetHandleRegistry.IsLoaded(h1), "Clear 前 h1 应 loaded");
            AssertTrue(AssetHandleRegistry.IsLoaded(h2), "Clear 前 h2 应 loaded");

            AssetHandleRegistry.Clear();
            AssertTrue(!AssetHandleRegistry.IsLoaded(h1), "Clear 后 h1 应未加载");
            AssertTrue(!AssetHandleRegistry.IsLoaded(h2), "Clear 后 h2 应未加载");
        }

        // =========================================================
        // 3. SoftAssetPtr<T>
        // =========================================================

        /// <summary>默认构造：Handle=Zero/IsLoaded=false/Get()=null。</summary>
        public static void TestSoftPtrDefault()
        {
            var ptr = new SoftAssetPtr<FakeTexture>();
            AssertEqual(AssetHandle.Zero, ptr.Handle, "默认 Handle 应 Zero");
            AssertTrue(!ptr.IsLoaded, "默认 IsLoaded=false");
            var got = ptr.Get();
            AssertTrue(got == null, "默认 Get() 应 null");
        }

        /// <summary>Get() 懒加载：注册前 null，注册后命中，缓存生效。</summary>
        public static void TestSoftPtrLazyLoad()
        {
            AssetHandleRegistry.Clear();
            var h = AssetHandle.New("FakeTexture", "Cached");
            var tex = new FakeTexture { Name = "Cached" };
            var ptr = new SoftAssetPtr<FakeTexture>(h);

            // 未注册
            var before = ptr.Get();
            AssertTrue(before == null, "未注册时 Get 应 null");
            AssertTrue(!ptr.IsLoaded, "未注册 IsLoaded=false");

            // 注册后 Get 命中
            AssetHandleRegistry.Register(h, tex);
            var got = ptr.Get();
            AssertTrue(got != null, "注册后 Get 应命中");
            AssertEqual("Cached", got.Name, "Get 返回正确对象");
            AssertTrue(ptr.IsLoaded, "命中后 IsLoaded=true");

            // 二次 Get 返回缓存（同一实例）
            var again = ptr.Get();
            AssertTrue(ReferenceEquals(got, again), "二次 Get 应返回缓存实例");

            // 隐式转换
            FakeTexture? implicitTex = ptr;
            AssertTrue(implicitTex != null, "隐式转换应命中");

            AssetHandleRegistry.Clear();
        }

        /// <summary>SetHandle 切换到不同 handle 重置缓存；切到相同 handle 不重置。</summary>
        public static void TestSoftPtrSetHandleResetsCache()
        {
            AssetHandleRegistry.Clear();
            var h1 = AssetHandle.New("FakeTexture", "A");
            var h2 = AssetHandle.New("FakeTexture", "B");
            var tex1 = new FakeTexture { Name = "A" };
            var tex2 = new FakeTexture { Name = "B" };
            AssetHandleRegistry.Register(h1, tex1);
            AssetHandleRegistry.Register(h2, tex2);

            var ptr = new SoftAssetPtr<FakeTexture>(h1);
            var got1 = ptr.Get();
            AssertTrue(got1 != null && got1.Name == "A", "初始 Get 应 A");

            // SetHandle 到不同 handle：缓存重置
            ptr.SetHandle(h2);
            AssertEqual(h2, ptr.Handle, "SetHandle 后 Handle 更新");
            var got2 = ptr.Get();
            AssertTrue(got2 != null && got2.Name == "B", "切换后 Get 应 B");

            // SetHandle 到相同 handle：不重置（缓存保留）
            var cachedBefore = ptr.Get();
            ptr.SetHandle(h2); // 同 handle
            var cachedAfter = ptr.Get();
            AssertTrue(ReferenceEquals(cachedBefore, cachedAfter), "同 handle SetHandle 不应重置缓存");

            // SetHandle 切到 Zero：缓存重置 → Get 返回 null
            ptr.SetHandle(AssetHandle.Zero);
            var zero = ptr.Get();
            AssertTrue(zero == null, "切到 Zero 后 Get 应 null");

            AssetHandleRegistry.Clear();
        }
    }
}
