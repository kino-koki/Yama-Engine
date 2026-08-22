using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using ShanyuEngine.Input;

namespace ShanyuEngine.Assets
{
    // ════════════════════════════════════════════════════════════════════════════
    //  ScriptGlobals — 脚本可用的全局 API
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 提供给 C# 脚本的全局对象。脚本可通过 <c>Owner</c>、<c>DeltaTime</c>、<c>Input</c> 等访问引擎状态。
    /// </summary>
    public sealed class ScriptGlobals
    {
        /// <summary>当前脚本所属的场景对象。</summary>
        public SceneObject Owner { get; set; } = null!;

        /// <summary>帧间隔时间（秒）。</summary>
        public float DeltaTime { get; set; }

        /// <summary>简化输入 API。</summary>
        public ScriptInput Input { get; } = new ScriptInput();

        /// <summary>打印日志。</summary>
        public void Log(object? message) => Console.WriteLine($"[Script] {message}");
    }

    /// <summary>
    /// 脚本可用的简化输入 API。
    /// </summary>
    public sealed class ScriptInput
    {
        private static InputSystem? _input;

        internal static void SetInputSystem(InputSystem? input) => _input = input;

        /// <summary>检查指定按键是否被按下。</summary>
        public bool IsKeyDown(string key)
        {
            if (_input == null || string.IsNullOrEmpty(key)) return false;
            if (Enum.TryParse<InputKey>(key, ignoreCase: true, out var ik))
                return _input.IsKeyDown(ik);
            return false;
        }

        /// <summary>检查指定按键是否在当前帧被按下（边缘触发）。</summary>
        public bool IsKeyPressed(string key)
        {
            if (_input == null || string.IsNullOrEmpty(key)) return false;
            if (Enum.TryParse<InputKey>(key, ignoreCase: true, out var ik))
                return _input.IsKeyPressed(ik);
            return false;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  ScriptComponent — 简化版场景对象脚本组件
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 附加到 <see cref="SceneObject"/> 的轻量级脚本组件。使用 Roslyn C# Scripting 在运行时编译执行脚本代码。
    /// </summary>
    public sealed class ScriptComponent : IAssetComponent
    {
        public string Name => "Script";

        /// <summary>.cs 脚本文件路径（可选）。</summary>
        public string ScriptAssetPath { get; set; } = string.Empty;

        /// <summary>内联脚本内容（优先于文件）。</summary>
        public string ScriptText { get; set; } = string.Empty;

        /// <summary>是否启用。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>上次编译的诊断信息。</summary>
        public string LastError { get; private set; } = string.Empty;

        // ── 编译缓存 ──
        private Script<object>? _compiledScript;
        private string _lastScriptHash = string.Empty;
        private readonly ScriptGlobals _globals = new ScriptGlobals();
        private bool _started;
        private bool _destroyed;

        // ════════════════════════════════════════════════════════════
        //  生命周期方法
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// 编译并启动脚本。在对象首次激活时调用一次。
        /// </summary>
        public void OnStart()
        {
            if (_started || _destroyed) return;
            _started = Compile();
        }

        /// <summary>
        /// 每帧更新脚本。自动触发 OnStart（如果尚未启动）。
        /// </summary>
        public void OnUpdate(float dt)
        {
            if (!Enabled || _destroyed) return;
            if (!_started) OnStart();
            if (_compiledScript == null) return;

            _globals.DeltaTime = dt;

            try
            {
                _compiledScript.RunAsync(_globals).Wait();
            }
            catch (Exception ex)
            {
                LastError = $"运行时错误: {ex.GetBaseException().Message}";
            }
        }

        /// <summary>
        /// 销毁脚本。释放编译缓存并标记为已销毁。
        /// </summary>
        public void OnDestroy()
        {
            if (_destroyed) return;
            _destroyed = true;
            _compiledScript = null;
            _lastScriptHash = string.Empty;
        }

        // ════════════════════════════════════════════════════════════
        //  IAssetComponent 实现
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// IAssetComponent 要求的更新接口。内部设置 Owner 后调用 OnUpdate。
        /// </summary>
        public void Update(float deltaTime, SceneObject owner)
        {
            _globals.Owner = owner;
            OnUpdate(deltaTime);
        }

        // ════════════════════════════════════════════════════════════
        //  编译与执行
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// 编译脚本。使用 CSharpScript 编译内联脚本或文件内容。
        /// </summary>
        public bool Compile()
        {
            string source = GetSource();
            if (string.IsNullOrWhiteSpace(source))
            {
                LastError = "脚本内容为空";
                return false;
            }

            string hash = source.GetHashCode().ToString();
            if (_compiledScript != null && hash == _lastScriptHash)
                return true; // 已编译且未变更

            try
            {
                var options = ScriptOptions.Default
                    .WithReferences(
                        typeof(object).Assembly,
                        typeof(Vector3).Assembly,
                        typeof(SceneObject).Assembly)
                    .WithImports(
                        "System",
                        "System.Numerics",
                        "ShanyuEngine.Assets",
                        "ShanyuEngine.Input");

                _compiledScript = CSharpScript.Create(source, options, typeof(ScriptGlobals));
                _lastScriptHash = hash;
                LastError = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                _compiledScript = null;
                LastError = $"编译失败: {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// 编译并执行（一次性运行）。
        /// </summary>
        public bool CompileAndRun()
        {
            if (!Compile()) return false;
            if (_compiledScript == null) return false;

            try
            {
                _compiledScript.RunAsync(_globals).Wait();
                return true;
            }
            catch (Exception ex)
            {
                LastError = $"运行时错误: {ex.GetBaseException().Message}";
                return false;
            }
        }

        /// <summary>
        /// 每帧调用。编译（如需）并执行脚本，将 owner 和 dt 传递给全局对象。
        /// 兼容旧代码，内部委托给 Update -> OnUpdate。
        /// </summary>
        public void Tick(float dt, SceneObject owner)
        {
            Update(dt, owner);
        }

        /// <summary>
        /// 预编译脚本（不执行）。适合在加载时提前编译。
        /// </summary>
        public Task<bool> CompileAsync()
        {
            return Task.FromResult(Compile());
        }

        private string GetSource()
        {
            if (!string.IsNullOrWhiteSpace(ScriptText))
                return ScriptText;

            if (!string.IsNullOrEmpty(ScriptAssetPath) && File.Exists(ScriptAssetPath))
            {
                try { return File.ReadAllText(ScriptAssetPath); }
                catch { return string.Empty; }
            }

            return string.Empty;
        }
    }
}
