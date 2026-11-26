using System;
using System.Collections.Generic;
using YamaEngine.Scripting.Abi;

namespace YamaEngine.Tests
{
    /// <summary>
    /// L2 绑定 ABI 的确定性自检。不依赖 xunit / 文件 / GPU / 网络，
    /// 由 XunitAdapter 反射发现 public static void Test* 并执行。
    /// </summary>
    public static class ScriptAbiTests
    {
        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("ScriptAbi self-test failed: " + message);
        }

        public static void TestScriptVariantRoundtrip()
        {
            Check(ScriptVariant.Null.Kind == ScriptVariantKind.Null, "Null kind");
            Check(ScriptVariant.From(42).AsInt32() == 42, "int roundtrip");
            Check(ScriptVariant.From(3.5).AsDouble() > 3.49 && ScriptVariant.From(3.5).AsDouble() < 3.51, "double roundtrip");
            Check(ScriptVariant.From("yama").AsString() == "yama", "string roundtrip");
            Check(ScriptVariant.From(true).AsBool(), "bool roundtrip");
            var v = ScriptVariant.From(new System.Numerics.Vector3(1, 2, 3));
            Check(v.Kind == ScriptVariantKind.Vector3, "vector3 kind");
            Check(v.TryGet<System.Numerics.Vector3>(out var vec) && vec.Z == 3, "vector3 unboxing");
            Check(ScriptVariant.FromObject(null).Kind == ScriptVariantKind.Null, "FromObject null");
            Check(ScriptVariant.FromObject(7).AsInt32() == 7, "FromObject int");
        }

        public static void TestNodeHandleTableLifecycle()
        {
            var table = new NodeHandleTable();
            var target = new object();
            var node = table.Register(target);
            Check(node.IsValid, "handle valid");
            Check(table.Count == 1, "count after register");
            Check(table.TryResolve(node, out var resolved) && ReferenceEquals(target, resolved), "resolve");
            Check(table.Release(node), "release");
            Check(!table.TryResolve(node, out _), "resolve after release");
            Check(table.Count == 0, "count after release");
        }

        public static void TestPropertyBagMigration()
        {
            var oldBag = new PropertyBag();
            oldBag.Set("speed", ScriptVariant.From(4.5));
            oldBag.Set("name", ScriptVariant.From("npc"));
            var newBag = new PropertyBag();
            newBag.Set("speed", ScriptVariant.From(9.0));
            int moved = PropertyBag.Migrate(oldBag, newBag);
            Check(moved == 1, "only missing keys migrate");
            Check(newBag.Get("speed", ScriptVariant.Null).AsDouble() > 8.9, "existing key preserved");
            Check(newBag.Get("name", ScriptVariant.Null).AsString() == "npc", "missing key restored");
            Check(newBag.Clone().Get("name", ScriptVariant.Null).AsString() == "npc", "clone");
        }

        public static void TestPropertyBagReflectionCapture()
        {
            var sample = new SampleScript { Speed = 2.5, Label = "hero" };
            var bag = PropertyBag.CaptureFrom(sample);
            Check(bag.Get("Speed", ScriptVariant.Null).AsDouble() > 2.4, "field capture");
            Check(bag.Get("Label", ScriptVariant.Null).AsString() == "hero", "property capture");
            var target = new SampleScript();
            int applied = bag.ApplyTo(target);
            Check(applied == 2, "apply count");
            Check(target.Speed > 2.4 && target.Label == "hero", "apply values");
        }

        public static void TestSignalBusIsolation()
        {
            var bus = new SignalBus();
            int hits = 0;
            var sub = bus.Connect("tick", _ => hits++);
            bus.Connect("tick", _ => throw new InvalidOperationException("expected handler failure"));
            bus.Connect("tick", _ => hits++);
            int delivered = bus.Emit("tick", ScriptVariant.From(1));
            Check(delivered == 2, "healthy handlers delivered despite one failure");
            Check(hits == 2, "hit count");
            Check(bus.Errors.Count == 1, "error isolated and recorded");
            Check(bus.Disconnect(sub), "disconnect");
            Check(bus.Emit("tick") == 1, "one healthy handler remains");
        }

        public static void TestLifecycleDispatcherIsolation()
        {
            var dispatcher = new LifecycleDispatcher();
            dispatcher.Register(new CountingScript());
            dispatcher.Register(new ThrowingScript());
            Check(dispatcher.DispatchReady() == 1, "ready isolates throw");
            Check(dispatcher.DispatchProcess(0.016) == 1, "process isolates throw");
            Check(dispatcher.Errors.Count == 2, "errors recorded");
        }

        public static void TestScriptRegistryAndBinding()
        {
            var handles = new NodeHandleTable();
            var target = new object();
            var node = handles.Register(target);
            var registry = new ScriptRegistry();
            registry.Register("Sample", b => new SampleScript { Speed = b.Properties.Get("speed", ScriptVariant.From(1.0)).AsDouble() });
            var binding = new ScriptBinding("Sample", node, handles, registry);
            binding.Properties.Set("speed", ScriptVariant.From(6.25));
            var instance = binding.Instantiate() as SampleScript;
            Check(instance != null && instance.Speed > 6.2, "factory reads binding state");
            Check(binding.TryResolveNode(out var resolved) && ReferenceEquals(target, resolved), "binding resolves node");
            var captured = binding.CaptureState();
            var reloaded = new ScriptBinding("Sample", node, handles, registry);
            Check(reloaded.RestoreState(captured) == 1, "state migrated after reload");
            Check(reloaded.Properties.Get("speed", ScriptVariant.Null).AsDouble() > 6.2, "state value kept");
            Check(registry.Count == 1, "registry count");
        }

        private sealed class SampleScript
        {
            [ScriptSerializeField] public double Speed;
            [ScriptSerializeField] public string Label { get; set; } = string.Empty;
        }

        private sealed class CountingScript : IScriptLifecycle
        {
            public int Ready;
            public void OnReady() { Ready++; }
            public void OnProcess(double deltaSeconds) { Ready++; }
            public void OnPhysics(double deltaSeconds) { }
            public void OnDestroy() { }
        }

        private sealed class ThrowingScript : IScriptLifecycle
        {
            public void OnReady() => throw new InvalidOperationException("expected");
            public void OnProcess(double deltaSeconds) => throw new InvalidOperationException("expected");
            public void OnPhysics(double deltaSeconds) { }
            public void OnDestroy() { }
        }
    }
}
