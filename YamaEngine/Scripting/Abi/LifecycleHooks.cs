using System;
using System.Collections.Generic;

namespace YamaEngine.Scripting.Abi
{
    /// <summary>对齐 Godot 3 的 _ready / _process / _physics_process / _exit_tree 生命周期。</summary>
    public interface IScriptLifecycle
    {
        void OnReady();
        void OnProcess(double deltaSeconds);
        void OnPhysics(double deltaSeconds);
        void OnDestroy();
    }

    /// <summary>生命周期调度器：单个脚本抛错被隔离，不中断整帧循环（硬约束 4）。</summary>
    public sealed class LifecycleDispatcher
    {
        private readonly object _gate = new object();
        private readonly List<IScriptLifecycle> _scripts = new List<IScriptLifecycle>();
        private readonly List<Exception> _errors = new List<Exception>();

        public void Register(IScriptLifecycle script)
        {
            if (script == null) throw new ArgumentNullException(nameof(script));
            lock (_gate) { if (!_scripts.Contains(script)) _scripts.Add(script); }
        }

        public bool Unregister(IScriptLifecycle script)
        {
            if (script == null) return false;
            lock (_gate) { return _scripts.Remove(script); }
        }

        public int Count { get { lock (_gate) { return _scripts.Count; } } }

        public int DispatchReady() => Dispatch(s => s.OnReady());
        public int DispatchProcess(double deltaSeconds) => Dispatch(s => s.OnProcess(deltaSeconds));
        public int DispatchPhysics(double deltaSeconds) => Dispatch(s => s.OnPhysics(deltaSeconds));
        public int DispatchDestroy() => Dispatch(s => s.OnDestroy());

        public IReadOnlyList<Exception> Errors
        {
            get { lock (_gate) { return _errors.ToArray(); } }
        }

        private int Dispatch(Action<IScriptLifecycle> action)
        {
            IScriptLifecycle[] snapshot;
            lock (_gate) { snapshot = _scripts.ToArray(); }
            int invoked = 0;
            foreach (var script in snapshot)
            {
                try { action(script); invoked++; }
                catch (Exception ex) { lock (_gate) { _errors.Add(ex); } }
            }
            return invoked;
        }
    }
}
