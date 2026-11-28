using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using YamaEngine.Scripting.Abi;

namespace YamaEngine.Tests
{
    /// <summary>
    /// 第二十二轮回归（12 项）：YAMA 行为层声明 → 已实现 L2 绑定 ABI 的真实接线。
    /// 证明「用 YAMA 开发游戏」不只是摆场景，还能声明行为并被还原成
    /// ScriptBinding / PropertyBag / NodeRef；同时锁住诚实边界：
    /// 不是 VM、不是 DSL 解释器、不谎报信号已连接、不谎报生命周期已挂载。
    /// </summary>
    public static class YamaScriptBindingTests
    {
        private static void Require(bool ok, string msg)
        {
            if (!ok) throw new InvalidOperationException("YAMA_R22_FAIL: " + msg);
        }
        private static bool IsRoot(string dir)
            => File.Exists(Path.Combine(dir, "schemas", "yama-script.schema.json"))
            && File.Exists(Path.Combine(dir, "ide", "codeoss", "language-profile.json"));
        private static string FindRoot()
        {
            string? env = Environment.GetEnvironmentVariable("YAMA_REPO_ROOT");
            if (!string.IsNullOrEmpty(env) && IsRoot(env)) return env;
            foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            {
                var d = new DirectoryInfo(start);
                for (int i = 0; i < 16 && d != null; i++, d = d.Parent)
                    if (IsRoot(d.FullName)) return d.FullName;
            }
            throw new InvalidOperationException("未找到仓库根（yama-script.schema.json）");
        }
        private static string R => _r ??= FindRoot();
        private static string? _r;
        private static string Rd(string rel) => File.ReadAllText(
            Path.Combine(R, rel.Replace('/', Path.DirectorySeparatorChar)));
        private static JsonElement J(string rel)
        {
            using var doc = JsonDocument.Parse(Rd(rel));
            return doc.RootElement.Clone();
        }

        private static readonly string[] Kinds = {
            "bool", "int32", "int64", "single", "double", "string",
            "vector2", "vector3", "vector4" };
        private static readonly string[] Hooks = { "ready", "process", "physics", "destroy" };
        private static readonly string[] ConfigFiles = {
            "ide/codeoss/product.json", "ide/codeoss/UPSTREAM.lock.json", ".gitmodules",
            "ci/codeoss-gate.yml", "scripts/fetch_codeoss.py",
            "scripts/build_codeoss_minimal.py", "ide/codeoss/language-profile.json",
            "schemas/yama-script.schema.json", "scripts/check_yama_project.py" };

        private static readonly string Example =
            "Templates/Basic2D/Content/Scripts/PlayerController.yama";

        /// <summary>C# 的 string.Replace 没有 Python 的 count 参数；只替换首次出现。</summary>
        private static string ReplaceFirst(string source, string old, string replacement)
        {
            int idx = source.IndexOf(old, StringComparison.Ordinal);
            if (idx < 0) return source;
            return source.Substring(0, idx) + replacement
                 + source.Substring(idx + old.Length);
        }

        private static ScriptRegistry NewRegistry()
        {
            var registry = new ScriptRegistry();
            registry.Register("PlayerController", b => new object());
            registry.Register("CameraRig", b => new object());
            registry.Register("NpcBrain", b => new object());
            return registry;
        }

        public static void TestR22SchemaDeclaresStrictBindingContract()
        {
            var s = J("schemas/yama-script.schema.json");
            Require(s.GetProperty("$id").GetString()!.Contains("yama-script.schema.json",
                    StringComparison.Ordinal), "$id");
            Require(s.GetProperty("additionalProperties").GetBoolean() == false,
                    "顶层应禁止额外字段");
            Require(s.GetProperty("required").GetArrayLength() == 3, "必填 3 项");
            var kind = s.GetProperty("$defs").GetProperty("TypedValue")
                        .GetProperty("properties").GetProperty("kind").GetProperty("enum");
            var got = new HashSet<string>();
            foreach (var x in kind.EnumerateArray()) got.Add(x.GetString() ?? "");
            foreach (var k in Kinds) Require(got.Contains(k), "kind 白名单缺 " + k);
            Require(got.Count == Kinds.Length, "kind 白名单应恰为 9 项");
        }

        public static void TestR22LanguageProfileDeclaresHonestScriptBinding()
        {
            var p = J("ide/codeoss/language-profile.json");
            Require(p.GetProperty("primary").GetProperty("id").GetString() == "yama",
                    "主语言仍应为 yama");
            var sb = p.GetProperty("scriptBinding");
            Require(sb.GetProperty("schema").GetString() == "schemas/yama-script.schema.json",
                    "schema 路径");
            Require(sb.GetProperty("abi").GetString()!.Contains("Scripting/Abi",
                    StringComparison.Ordinal), "应指向已实现的 L2 ABI");
            Require(!sb.GetProperty("vmImplemented").GetBoolean(), "不得谎报 VM 已实现");
            string note = sb.GetProperty("note").GetString() ?? "";
            Require(note.Contains("不是 VM", StringComparison.Ordinal)
                 && note.Contains("不谎报", StringComparison.Ordinal), "诚实边界");
            var parsed = YamaEngine.Editor.CodeOss.YamaLanguageProfile.FromJson(
                Rd("ide/codeoss/language-profile.json"));
            Require(parsed.HasHonestDeclaredScriptBinding, "C# 侧应解析到声明层");
            Require(parsed.IsYamaFirst, "第二十一轮主语言判定不应回退");
        }

        public static void TestR22ExampleDeclarationParsesStrictly()
        {
            string json = Rd(Example);
            Require(YamaScriptDeclaration.TryParse(json, out var d, out var err) && d != null,
                    "示例声明应通过: " + err);
            Require(d.Schema == YamaScriptDeclaration.SchemaId, "schema");
            Require(d.TypeName == "PlayerController", "typeName");
            Require(d.Properties.Count == 5, "示例应有 5 个属性");
            Require(d.DeclaredSignals.Count == 2 && d.HasDeclaredSignal("tick"), "信号声明");
            Require(d.LifecycleHooks.Count == 2, "生命周期钩子");
        }

        public static void TestR22AllTemplateDeclarationsParse()
        {
            foreach (var rel in new[] { Example,
                "Templates/Basic3D/Content/Scripts/CameraRig.yama",
                "Templates/NpcRealityDemo/Content/Scripts/NpcBrain.yama" })
            {
                Require(YamaScriptDeclaration.TryParse(Rd(rel), out var d, out var err) && d != null,
                        rel + " 应通过: " + err);
                Require(d.TypeName.Length > 0, rel + " typeName");
            }
        }

        public static void TestR22ProjectContractsPointAtScriptFolder()
        {
            foreach (var t in new[] { "Basic2D", "Basic3D", "NpcRealityDemo" })
            {
                var p = J("Templates/" + t + "/yama.project.json");
                Require(p.GetProperty("primaryLanguage").GetString() == "yama", t);
                string sb = p.GetProperty("scriptBinding").GetString() ?? "";
                Require(sb == "Content/Scripts", t + " scriptBinding");
                string dir = Path.Combine(R, "Templates", t, "Content", "Scripts");
                Require(Directory.Exists(dir), t + " 目录存在");
                bool any = false;
                foreach (var f in Directory.GetFiles(dir, "*.yama")) any = true;
                Require(any, t + " 至少一个 .yama 声明");
            }
        }

        public static void TestR22VariantValuesRoundTripExactly()
        {
            Require(YamaScriptDeclaration.TryParse(Rd(Example), out var d, out _) && d != null,
                    "解析");
            Require(d.Properties["speed"].AsDouble() > 6.24
                 && d.Properties["speed"].AsDouble() < 6.26, "double");
            Require(d.Properties["jumpHeight"].Kind.ToString() == "Single", "single kind");
            Require(!d.Properties["invincible"].AsBool(), "bool");
            Require(d.Properties["label"].AsString() == "hero", "string");
            var v = d.Properties["spawnOffset"];
            Require(v.Kind.ToString() == "Vector3", "vector3 kind");
            Require(v.TryGet<System.Numerics.Vector3>(out var vec) && vec.Y == 1,
                    "vector3 分量");
        }

        public static void TestR22IllegalKindIsRejected()
        {
            string json = Rd(Example);
            foreach (var bad in new[] { "object", "array", "dictionary", "resourceRef",
                                        "nodeRef", "unknown" })
            {
                string broken = ReplaceFirst(json, "\"kind\": \"double\"", "\"kind\": \"" + bad + "\"");
                Require(broken != json, "替换应生效: " + bad);
                Require(!YamaScriptDeclaration.TryParse(broken, out _, out var err)
                        && err != null && err.Contains("白名单", StringComparison.Ordinal),
                        "非法 kind 应拒绝: " + bad);
            }
        }

        public static void TestR22WrongValueTypeIsRejected()
        {
            string json = Rd(Example);
            string broken = ReplaceFirst(json, "\"value\": 6.25", "\"value\": \"not-a-number\"");
            Require(broken != json, "替换应生效");
            Require(!YamaScriptDeclaration.TryParse(broken, out _, out var err)
                    && err != null, "double 值类型错误应拒绝");
            string broken2 = ReplaceFirst(json, "\"value\": false", "\"value\": \"false\"");
            Require(!YamaScriptDeclaration.TryParse(broken2, out _, out _),
                    "bool 值类型错误应拒绝");
        }

        public static void TestR22BadLifecycleHookIsRejected()
        {
            string json = Rd(Example);
            string broken = ReplaceFirst(json, "\"ready\"", "\"onReadyGodot\"");
            Require(broken != json, "替换应生效");
            Require(!YamaScriptDeclaration.TryParse(broken, out _, out _),
                    "非白名单钩子应拒绝");
            Require(Hooks.Length == 4, "钩子白名单应恰为 4");
        }

        public static void TestR22CreateBindingReallyRestoresAbi()
        {
            Require(YamaScriptDeclaration.TryParse(Rd(Example), out var d, out _) && d != null,
                    "解析");
            var handles = new NodeHandleTable();
            var registry = NewRegistry();
            var target = new object();
            ScriptBinding binding = d.CreateBinding(handles, registry, target);
            Require(binding.TypeName == "PlayerController", "TypeName");
            Require(binding.Node.IsValid, "NodeRef 应由句柄表生成");
            Require(binding.TryResolveNode(out var resolved) && ReferenceEquals(target, resolved),
                    "节点可解析且不暴露原生指针");
            Require(binding.Properties.Count == 5, "PropertyBag 应逐项灌入");
            Require(binding.Properties.Get("speed", ScriptVariant.Null).AsDouble() > 6.24,
                    "属性值应可从 ABI 读回");
            object instance = binding.Instantiate();
            Require(instance != null, "ScriptRegistry 工厂应可实例化");
        }

        public static void TestR22UnregisteredTypeIsFailClosed()
        {
            string json = ReplaceFirst(Rd(Example), "PlayerController", "NotRegisteredType");
            Require(YamaScriptDeclaration.TryParse(json, out var d, out _) && d != null, "解析");
            var handles = new NodeHandleTable();
            var registry = NewRegistry();
            bool threw = false;
            try { d.CreateBinding(handles, registry, new object()); }
            catch (InvalidOperationException) { threw = true; }
            Require(threw, "未注册类型名必须拒绝还原");
        }

        public static void TestR22SignalsAndLifecycleDoNotFakeWiring()
        {
            Require(YamaScriptDeclaration.TryParse(Rd(Example), out var d, out _) && d != null,
                    "解析");
            var bus = new SignalBus();
            Require(bus.Emit("tick") == 0, "声明信号不等于已连接 handler");
            Require(!d.HasDeclaredSignal("never"), "未声明信号应返回 false");
            var dispatcher = new LifecycleDispatcher();
            Require(!d.TryRegisterLifecycle(dispatcher, new object()),
                    "非 IScriptLifecycle 实例不得谎报挂载");
            Require(dispatcher.Count == 0, "调度器应仍为空");
            Require(d.TryRegisterLifecycle(dispatcher, new ReadyOnlyScript()),
                    "真正实现接口才注册");
            Require(dispatcher.DispatchReady() == 1, "注册后可调度");
        }

        public static void TestR22NineConfigDigestsAndSpecDocNoLongerStale()
        {
            var m = J("ide/codeoss/MINIMAL.lock.json");
            var declared = m.GetProperty("config_sha256");
            foreach (var rel in ConfigFiles)
                Require(declared.TryGetProperty(rel, out _), "摘要缺 " + rel);
            int n = 0;
            foreach (var _ in declared.EnumerateObject()) n++;
            Require(n == 9, "摘要键数应为 9，实际 " + n);
            string spec = Rd("docs/SCRIPTING_ABI_SPEC.md");
            Require(!spec.Contains("规格文档，不是实现", StringComparison.Ordinal),
                    "ABI 规格文档不得再自称未实现");
            Require(!spec.Contains("沙盒无 `dotnet`", StringComparison.Ordinal),
                    "不得再用已失效的无编译器理由");
            Require(spec.Contains("L2 已实现", StringComparison.Ordinal),
                    "应更正为 L2 已实现");
            Require(spec.Contains("不是 VM", StringComparison.Ordinal)
                 || spec.Contains("不实现 VM", StringComparison.Ordinal),
                    "应明确本轮不实现 VM");
            string round = Rd("docs/ROUND22_DELIVERY.md");
            Require(round.Contains("不谎报", StringComparison.Ordinal), "交付文档应有诚实边界");
        }

        private sealed class ReadyOnlyScript : IScriptLifecycle
        {
            public int Ready;
            public void OnReady() { Ready++; }
            public void OnProcess(double deltaSeconds) { }
            public void OnPhysics(double deltaSeconds) { }
            public void OnDestroy() { }
        }
    }
}
