// UnitySceneManagerDepthTests.cs — Unity5 SceneManagerReal 深度验证
// 覆盖：LoadSceneMode 枚举、SceneLifecycleState 枚举、SceneReal 默认字段、
//       LoadScene/UnloadScene/SetActiveScene/GetSceneByName/GetSceneAt/GetSceneByBuildIndex。
using System;
using YamaEngine.GameFramework;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

public static class UnitySceneManagerDepthTests
{
    private static void AssertFalse(bool c, string msg) => AssertTrue(!c, msg);

    // ════════════════════════════════════════════════════════════════
    //  一、枚举
    // ════════════════════════════════════════════════════════════════

    /// <summary>LoadSceneMode 三态（比 Unity 多一个 UnloadAdditive）。</summary>
    public static void TestLoadSceneModeValues()
    {
        AssertEqual(0, (int)LoadSceneMode.Single, "Single=0");
        AssertEqual(1, (int)LoadSceneMode.Additive, "Additive=1");
        AssertEqual(2, (int)LoadSceneMode.UnloadAdditive, "UnloadAdditive=2");
    }

    /// <summary>SceneLifecycleState 六态。</summary>
    public static void TestSceneLifecycleStateValues()
    {
        AssertEqual(0, (int)SceneLifecycleState.Unloaded, "Unloaded=0");
        AssertEqual(1, (int)SceneLifecycleState.Loading, "Loading=1");
        AssertEqual(2, (int)SceneLifecycleState.Loaded, "Loaded=2");
        AssertEqual(3, (int)SceneLifecycleState.Activating, "Activating=3");
        AssertEqual(4, (int)SceneLifecycleState.Active, "Active=4");
        AssertEqual(5, (int)SceneLifecycleState.Unloading, "Unloading=5");
    }

    // ════════════════════════════════════════════════════════════════
    //  二、SceneReal 默认字段
    // ════════════════════════════════════════════════════════════════

    /// <summary>SceneReal 默认构造（name="")：BuildIndex=-1，IsLoaded=false，State=Unloaded。</summary>
    public static void TestSceneRealDefaults()
    {
        var scene = new SceneReal("", null);
        AssertTrue(string.IsNullOrEmpty(scene.Name), "默认 Name 空");
        AssertTrue(string.IsNullOrEmpty(scene.Path), "默认 Path 空");
        AssertEqual(-1, scene.BuildIndex, "默认 BuildIndex=-1");
        AssertFalse(scene.IsLoaded, "默认 IsLoaded=false");
        AssertFalse(scene.IsActive, "默认 IsActive=false");
        AssertEqual(SceneLifecycleState.Unloaded, scene.State, "默认 State=Unloaded");
        AssertEqual(0, scene.GameObjectCount, "默认 GameObjectCount=0");
    }

    /// <summary>SceneReal 构造函数传 Name/Path。</summary>
    public static void TestSceneRealCtor()
    {
        var scene = new SceneReal("Level1", "Scenes/Level1.yamascene");
        AssertEqual("Level1", scene.Name, "Name=Level1");
        AssertEqual("Scenes/Level1.yamascene", scene.Path, "Path 设置");
    }

    // ════════════════════════════════════════════════════════════════
    //  三、SceneManagerReal Load/Unload/SetActive
    // ════════════════════════════════════════════════════════════════

    /// <summary>初始状态：0 场景，无 ActiveScene。</summary>
    public static void TestManagerInitialState()
    {
        var mgr = new SceneManagerReal();
        AssertEqual(0, mgr.SceneCount, "初始 0 场景");
        AssertTrue(mgr.LoadedScenes is not null && mgr.LoadedScenes.Count == 0, "LoadedScenes 空");
        AssertTrue(mgr.ActiveScene is null, "初始 ActiveScene=null");
    }

    /// <summary>LoadScene 创建场景并设为 Active。</summary>
    public static void TestLoadSceneSingle()
    {
        var mgr = new SceneManagerReal();
        var scene = mgr.LoadScene("Main");
        AssertTrue(scene is not null, "LoadScene 返回非 null");
        AssertEqual("Main", scene.Name, "Name=Main");
        AssertTrue(scene.IsLoaded, "IsLoaded=true");
        AssertEqual(SceneLifecycleState.Active, scene.State, "State=Active");
        AssertEqual(1, mgr.SceneCount, "加载后 1 场景");
        AssertEqual(scene, mgr.ActiveScene, "ActiveScene=刚加载场景");
    }

    /// <summary>LoadScene Single 模式重复加载：先 UnloadAll 再新建 → 返回新实例。</summary>
    public static void TestLoadSceneExisting()
    {
        var mgr = new SceneManagerReal();
        var s1 = mgr.LoadScene("Main");
        var s2 = mgr.LoadScene("Main"); // Single 卸载所有再创建
        AssertFalse(ReferenceEquals(s1, s2), "Single 模式重复加载返回新实例");
        AssertEqual(1, mgr.SceneCount, "仍只有 1 场景");
        AssertEqual("Main", s2.Name, "新实例 Name=Main");
    }

    /// <summary>LoadScene Additive 不切换 ActiveScene。</summary>
    public static void TestLoadSceneAdditive()
    {
        var mgr = new SceneManagerReal();
        var main = mgr.LoadScene("Main", LoadSceneMode.Single);
        AssertEqual(main, mgr.ActiveScene, "Main 为 Active");

        var sub = mgr.LoadScene("SubLevel", LoadSceneMode.Additive);
        AssertTrue(sub is not null, "Additive 场景加载");
        AssertEqual("SubLevel", sub.Name, "Additive Name");
        AssertEqual(2, mgr.SceneCount, "2 场景");
        // Additive 不切换 Active
        AssertEqual(main, mgr.ActiveScene, "Additive 不改 Active");
    }

    /// <summary>UnloadScene 卸载后场景不再在列表。</summary>
    public static void TestUnloadScene()
    {
        var mgr = new SceneManagerReal();
        var main = mgr.LoadScene("Main");
        var sub = mgr.LoadScene("SubLevel", LoadSceneMode.Additive);
        AssertEqual(2, mgr.SceneCount, "卸载前 2 场景");

        mgr.UnloadScene(sub);
        AssertEqual(1, mgr.SceneCount, "卸载后 1 场景");
        AssertEqual(main, mgr.ActiveScene, "卸载 Additive 不影响 Active");

        // 已卸载场景再卸载为 no-op
        mgr.UnloadScene(sub);
        AssertEqual(1, mgr.SceneCount, "二次卸载 no-op");
    }

    /// <summary>UnloadScene(null) no-op 不抛。</summary>
    public static void TestUnloadSceneNull()
    {
        var mgr = new SceneManagerReal();
        mgr.UnloadScene(null!); // 不抛
        AssertEqual(0, mgr.SceneCount, "null 卸载不影响");
    }

    /// <summary>UnloadScene 卸载 Active 场景后 Active 切到剩余场景或 null。</summary>
    public static void TestUnloadActiveScene()
    {
        var mgr = new SceneManagerReal();
        var main = mgr.LoadScene("Main");
        mgr.UnloadScene(main);
        AssertTrue(mgr.ActiveScene is null, "卸载 Active 后 Active=null");
        AssertEqual(0, mgr.SceneCount, "全部卸载");
    }

    /// <summary>SetActiveScene 切换 Active 场景。</summary>
    public static void TestSetActiveScene()
    {
        var mgr = new SceneManagerReal();
        var main = mgr.LoadScene("Main");
        var sub = mgr.LoadScene("SubLevel", LoadSceneMode.Additive);

        mgr.SetActiveScene(sub);
        AssertEqual(sub, mgr.ActiveScene, "Active 切到 Sub");
        AssertTrue(sub.IsActive, "Sub.IsActive=true");
        AssertFalse(main.IsActive, "Main.IsActive=false");
    }

    /// <summary>SetActiveScene 未加载场景抛异常。</summary>
    public static void TestSetActiveSceneUnloadedThrows()
    {
        var mgr = new SceneManagerReal();
        var orphan = new SceneReal("Ghost");
        bool threw = false;
        try { mgr.SetActiveScene(orphan); }
        catch (Exception) { threw = true; }
        AssertTrue(threw, "未加载场景 SetActive 抛异常");
    }

    // ════════════════════════════════════════════════════════════════
    //  四、查找 API
    // ════════════════════════════════════════════════════════════════

    /// <summary>GetSceneByName 按名字查找。</summary>
    public static void TestGetSceneByName()
    {
        var mgr = new SceneManagerReal();
        var main = mgr.LoadScene("Main");
        var sub = mgr.LoadScene("SubLevel", LoadSceneMode.Additive);

        AssertEqual(main, mgr.GetSceneByName("Main"), "按名找 Main");
        AssertEqual(sub, mgr.GetSceneByName("SubLevel"), "按名找 SubLevel");
        AssertTrue(mgr.GetSceneByName("Ghost") is null, "找不到返回 null");
    }

    /// <summary>GetSceneAt 按索引查找（越界返回 null）。</summary>
    public static void TestGetSceneAt()
    {
        var mgr = new SceneManagerReal();
        var main = mgr.LoadScene("Main");
        var sub = mgr.LoadScene("SubLevel", LoadSceneMode.Additive);

        AssertEqual(main, mgr.GetSceneAt(0), "索引 0=Main");
        AssertEqual(sub, mgr.GetSceneAt(1), "索引 1=SubLevel");
        AssertTrue(mgr.GetSceneAt(99) is null, "越界返回 null");
        AssertTrue(mgr.GetSceneAt(-1) is null, "负索引返回 null");
    }

    /// <summary>GetSceneByBuildIndex：LoadScene 不设置 BuildIndex（恒 -1）→ 查不到任何场景。</summary>
    public static void TestGetSceneByBuildIndex()
    {
        var mgr = new SceneManagerReal();
        var main = mgr.LoadScene("Main");
        var sub = mgr.LoadScene("SubLevel", LoadSceneMode.Additive);

        // LoadScene 从不设置 BuildIndex，默认 -1
        AssertEqual(-1, main.BuildIndex, "LoadScene 不设置 BuildIndex=-1");
        AssertTrue(mgr.GetSceneByBuildIndex(0) is null, "BuildIndex 0 查不到（未分配）");
        AssertTrue(mgr.GetSceneByBuildIndex(999) is null, "未知 BuildIndex 返回 null");
    }

    /// <summary>GetActiveScene 返回当前 Active 场景。</summary>
    public static void TestGetActiveScene()
    {
        var mgr = new SceneManagerReal();
        var main = mgr.LoadScene("Main");
        AssertEqual(main, mgr.GetActiveScene(), "GetActiveScene=Main");
    }

    // ════════════════════════════════════════════════════════════════
    //  五、GameObjectReal 组件 API
    // ════════════════════════════════════════════════════════════════

    /// <summary>GameObjectReal 默认构造 Name="GameObject"。</summary>
    public static void TestGameObjectDefaults()
    {
        var go = new GameObjectReal();
        AssertEqual("GameObject", go.Name, "默认 Name");
    }

    /// <summary>GameObjectReal 自定义 Name。</summary>
    public static void TestGameObjectNamed()
    {
        var go = new GameObjectReal("Hero");
        AssertEqual("Hero", go.Name, "Name=Hero");
    }

    /// <summary>AddComponent&lt;T&gt; 单组件约束：非 [AllowMultiple] 时重复添加抛 InvalidOperationException。</summary>
    public static void TestGameObjectAddComponent()
    {
        var go = new GameObjectReal("Enemy");
        var rb = go.AddComponent<TestRbComponent>();
        AssertTrue(rb is not null, "AddComponent 返回组件");
        AssertTrue(go.HasComponent<TestRbComponent>(), "HasComponent=true");
        AssertEqual(rb, go.GetComponent<TestRbComponent>(), "GetComponent 返回同实例");

        // 重复添加同类型：默认禁止，抛异常
        bool threw = false;
        try { go.AddComponent<TestRbComponent>(); }
        catch (InvalidOperationException) { threw = true; }
        AssertTrue(threw, "重复添加同类型抛 InvalidOperationException");
        AssertEqual(rb, go.GetComponent<TestRbComponent>(), "原组件保持不变");
    }

    /// <summary>TryGetComponent 成功/失败。</summary>
    public static void TestGameObjectTryGetComponent()
    {
        var go = new GameObjectReal("NPC");
        go.AddComponent<TestRbComponent>();

        AssertTrue(go.TryGetComponent<TestRbComponent>(out var rb), "TryGetComponent 成功");
        AssertTrue(rb is not null, "out 参数非 null");

        AssertFalse(go.TryGetComponent<TestHealthComponent>(out var hp), "TryGetComponent 失败");
        AssertTrue(hp is null, "失败时 out=null");
    }

    private sealed class TestRbComponent : ComponentReal { }
    private sealed class TestHealthComponent : ComponentReal { }
}
