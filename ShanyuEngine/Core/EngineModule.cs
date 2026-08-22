// ============================================================================================
//  EngineModule.cs
//  山雨引擎 (ShanyuEngine) 模块化生命周期系统
// ============================================================================================
//  将所有子系统统一为模块化生命周期管理。每个模块遵循：
//    Initialize → PostInitialize → Tick → PreShutdown → Shutdown
//  支持模块依赖解析、自动排序、启动耗时监控。
//  设计参考 UE IModuleInterface + Unity RuntimeInitializeOnLoadMethod。
// ============================================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace ShanyuEngine.Core
{
    // ====== 模块接口 ======
    /// <summary>所有引擎模块必须实现的接口。</summary>
    public interface IEngineModule
    {
        /// <summary>模块名称。</summary>
        string Name { get; }

        /// <summary>模块优先级（越低越先初始化）。</summary>
        int Priority { get; }

        /// <summary>依赖的模块名称列表。</summary>
        string[] Dependencies { get; }

        /// <summary>初始化模块。在此阶段分配资源。</summary>
        bool Initialize();

        /// <summary>后初始化。在此阶段访问其他已初始化的模块。</summary>
        bool PostInitialize();

        /// <summary>每帧更新。</summary>
        void Update(float deltaTime);

        /// <summary>预关闭。在此阶段释放对其他模块的引用。</summary>
        void PreShutdown();

        /// <summary>关闭。在此阶段释放本模块资源。</summary>
        void Shutdown();
    }

    // ====== 基础模块（提供默认实现） ======
    public abstract class EngineModuleBase : IEngineModule
    {
        public abstract string Name { get; }
        public virtual int Priority => 50;
        public virtual string[] Dependencies => Array.Empty<string>();

        public virtual bool Initialize() => true;
        public virtual bool PostInitialize() => true;
        public virtual void Update(float deltaTime) { }
        public virtual void PreShutdown() { }
        public virtual void Shutdown() { }

        /// <summary>此模块是否已成功初始化。</summary>
        public bool IsInitialized { get; protected set; }

        /// <summary>此模块是否已关闭。</summary>
        public bool IsShutdown { get; protected set; }
    }

    // ====== 模块状态 ======
    public enum ModuleState
    {
        Uninitialized,
        Initialized,
        PostInitialized,
        Updating,
        PreShutdown,
        Shutdown,
        Failed
    }

    // ====== 模块元数据 ======
    internal class ModuleMetadata
    {
        public IEngineModule Module;
        public ModuleState State;
        public TimeSpan InitDuration;
        public TimeSpan PostInitDuration;
        public string? ErrorMessage;

        public ModuleMetadata(IEngineModule module)
        {
            Module = module;
            State = ModuleState.Uninitialized;
        }
    }

    // ====== 模块调用统计 ======
    public readonly struct ModuleTimingInfo
    {
        public readonly string Name;
        public readonly ModuleState State;
        public readonly double InitMs;
        public readonly double PostInitMs;
        public readonly bool HasError;

        public ModuleTimingInfo(string name, ModuleState state, double initMs, double postInitMs, bool hasError)
        {
            Name = name; State = state; InitMs = initMs; PostInitMs = postInitMs; HasError = hasError;
        }
    }

    // ====== 模块管理器 ======
    /// <summary>
    /// 山雨引擎模块化管理器。所有子系统通过此类注册并统一管理生命周期。
    /// 用法：
    /// <code>
    /// ModuleManager.Register(new RenderModule());
    /// ModuleManager.Register(new PhysicsModule());
    /// ModuleManager.InitializeAll();  // 按依赖顺序初始化
    /// 
    /// while (running) {
    ///     ModuleManager.UpdateAll(deltaTime);
    /// }
    /// 
    /// ModuleManager.ShutdownAll();  // 按依赖逆序关闭
    /// </code>
    /// </summary>
    public static class ModuleManager
    {
        // ====== 内部存储 ======
        private static readonly ConcurrentDictionary<string, ModuleMetadata> _modules = new();
        private static readonly List<ModuleMetadata> _sortedModules = new();
        private static volatile bool _initialized;
        private static readonly object _lock = new();

        // ====== 状态 ======
        public static bool IsInitialized => _initialized;
        public static int ModuleCount => _modules.Count;
        public static int FailedModuleCount { get; private set; }

        // ====== 注册模块 ======
        /// <summary>注册一个模块实例。</summary>
        public static void Register(IEngineModule module)
        {
            if (module == null) throw new ArgumentNullException(nameof(module));
            if (_modules.ContainsKey(module.Name))
            {
                Diagnostics.Log.Warning($"模块已注册: {module.Name}");
                return;
            }

            _modules[module.Name] = new ModuleMetadata(module);
            Diagnostics.Log.Debug($"模块已注册: {module.Name}", Diagnostics.LogCategory.Core);
        }

        /// <summary>通过类型自动注册（使用无参构造）。</summary>
        public static void Register<T>() where T : IEngineModule, new()
        {
            Register(new T());
        }

        /// <summary>获取已注册的模块。</summary>
        public static T? Get<T>() where T : class, IEngineModule
        {
            foreach (var meta in _modules.Values)
            {
                if (meta.Module is T typed)
                    return typed;
            }
            return null;
        }

        /// <summary>按名称获取模块。</summary>
        public static IEngineModule? GetByName(string name)
        {
            return _modules.TryGetValue(name, out var meta) ? meta.Module : null;
        }

        // ====== 初始化所有模块 ======
        /// <summary>按依赖顺序初始化所有已注册的模块。</summary>
        public static bool InitializeAll()
        {
            if (_initialized)
            {
                Diagnostics.Log.Warning("ModuleManager 已初始化");
                return true;
            }

            var sw = Stopwatch.StartNew();

            // 1. 拓扑排序
            var sorted = TopologicalSort();
            if (sorted == null)
            {
                Diagnostics.Log.Error("模块依赖存在循环引用，无法初始化");
                return false;
            }

            // 2. Initialize
            foreach (var meta in sorted)
            {
                var moduleSw = Stopwatch.StartNew();
                try
                {
                    bool success = meta.Module.Initialize();
                    moduleSw.Stop();
                    meta.InitDuration = moduleSw.Elapsed;

                    if (success)
                    {
                        meta.State = ModuleState.Initialized;
                        Diagnostics.Log.Info($"  [{meta.Module.Name}] 初始化完成 ({moduleSw.Elapsed.TotalMilliseconds:F1}ms)",
                            Diagnostics.LogCategory.Core);
                    }
                    else
                    {
                        meta.State = ModuleState.Failed;
                        meta.ErrorMessage = "Initialize 返回 false";
                        FailedModuleCount++;
                        Diagnostics.Log.Error($"  [{meta.Module.Name}] 初始化失败");
                    }
                }
                catch (Exception ex)
                {
                    moduleSw.Stop();
                    meta.InitDuration = moduleSw.Elapsed;
                    meta.State = ModuleState.Failed;
                    meta.ErrorMessage = ex.Message;
                    FailedModuleCount++;
                    Diagnostics.Log.Error($"  [{meta.Module.Name}] 初始化异常: {ex.Message}",
                        Diagnostics.LogCategory.Core);
                }
            }

            // 3. PostInitialize
            foreach (var meta in sorted)
            {
                if (meta.State == ModuleState.Failed) continue;

                var moduleSw = Stopwatch.StartNew();
                try
                {
                    bool success = meta.Module.PostInitialize();
                    moduleSw.Stop();
                    meta.PostInitDuration = moduleSw.Elapsed;

                    if (success)
                    {
                        meta.State = ModuleState.PostInitialized;
                        meta.Module.IsInitialized = true;
                    }
                    else
                    {
                        meta.State = ModuleState.Failed;
                        meta.ErrorMessage = "PostInitialize 返回 false";
                        FailedModuleCount++;
                        Diagnostics.Log.Error($"  [{meta.Module.Name}] PostInitialize 失败");
                    }
                }
                catch (Exception ex)
                {
                    moduleSw.Stop();
                    meta.PostInitDuration = moduleSw.Elapsed;
                    meta.State = ModuleState.Failed;
                    meta.ErrorMessage = ex.Message;
                    FailedModuleCount++;
                    Diagnostics.Log.Error($"  [{meta.Module.Name}] PostInitialize 异常: {ex.Message}",
                        Diagnostics.LogCategory.Core);
                }
            }

            sw.Stop();
            _initialized = true;

            // 报告
            int successCount = sorted.Count(m => m.State != ModuleState.Failed);
            Diagnostics.Log.Info(
                $"模块初始化完成: {successCount}/{sorted.Count} 成功, 耗时 {sw.Elapsed.TotalMilliseconds:F0}ms");

            return FailedModuleCount == 0;
        }

        // ====== 所有模块每帧更新 ======
        /// <summary>按注册顺序（不一定是依赖顺序）更新所有模块。</summary>
        public static void UpdateAll(float deltaTime)
        {
            if (!_initialized) return;

            foreach (var meta in _sortedModules)
            {
                if (meta.State == ModuleState.Failed) continue;
                if (meta.State < ModuleState.PostInitialized) continue;

                try
                {
                    meta.State = ModuleState.Updating;
                    meta.Module.Update(deltaTime);
                }
                catch (Exception ex)
                {
                    Diagnostics.Log.Error(
                        $"[ModuleManager] {meta.Module.Name}.Update 异常: {ex.Message}",
                        Diagnostics.LogCategory.Core);
                }
            }
        }

        // ====== 关闭所有模块（逆序） ======
        public static void ShutdownAll()
        {
            if (!_initialized) return;

            Diagnostics.Log.Info("正在关闭所有引擎模块...");

            // 逆序关闭
            for (int i = _sortedModules.Count - 1; i >= 0; i--)
            {
                var meta = _sortedModules[i];
                if (meta.State == ModuleState.Failed) continue;

                try
                {
                    meta.State = ModuleState.PreShutdown;
                    meta.Module.PreShutdown();
                }
                catch (Exception ex)
                {
                    Diagnostics.Log.Error(
                        $"[ModuleManager] {meta.Module.Name}.PreShutdown 异常: {ex.Message}");
                }

                try
                {
                    meta.Module.Shutdown();
                    meta.State = ModuleState.Shutdown;
                    meta.Module.IsShutdown = true;
                }
                catch (Exception ex)
                {
                    Diagnostics.Log.Error(
                        $"[ModuleManager] {meta.Module.Name}.Shutdown 异常: {ex.Message}");
                }
            }

            _modules.Clear();
            _sortedModules.Clear();
            _initialized = false;
            Diagnostics.Log.Info("所有引擎模块已关闭");
        }

        // ====== 拓扑排序 ======
        private static List<ModuleMetadata>? TopologicalSort()
        {
            var sorted = new List<ModuleMetadata>();
            var visited = new HashSet<string>();
            var visiting = new HashSet<string>();

            bool Dfs(string name)
            {
                if (visiting.Contains(name))
                    return false; // 循环依赖

                if (visited.Contains(name))
                    return true;

                if (!_modules.TryGetValue(name, out var meta))
                    return true; // 未知依赖，忽略

                visiting.Add(name);

                foreach (var dep in meta.Module.Dependencies)
                {
                    if (!Dfs(dep))
                        return false;
                }

                visiting.Remove(name);
                visited.Add(name);
                sorted.Add(meta);
                return true;
            }

            foreach (var name in _modules.Keys)
            {
                if (!Dfs(name))
                    return null;
            }

            _sortedModules.Clear();
            _sortedModules.AddRange(sorted);
            return sorted;
        }

        // ====== 计时报告 ======
        public static ModuleTimingInfo[] GetTimingReport()
        {
            return _sortedModules.Select(m => new ModuleTimingInfo(
                m.Module.Name,
                m.State,
                m.InitDuration.TotalMilliseconds,
                m.PostInitDuration.TotalMilliseconds,
                m.State == ModuleState.Failed
            )).ToArray();
        }

        // ====== 输出报告 ======
        public static string GenerateReport()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("===== 引擎模块报告 =====");
            sb.AppendLine($"总模块数: {_modules.Count}");
            sb.AppendLine($"初始化成功: {_modules.Count - FailedModuleCount}");
            sb.AppendLine($"初始化失败: {FailedModuleCount}");
            sb.AppendLine();

            foreach (var meta in _sortedModules)
            {
                string status = meta.State switch
                {
                    ModuleState.Failed => $"失败: {meta.ErrorMessage}",
                    ModuleState.Shutdown => "已关闭",
                    _ => meta.State.ToString()
                };
                sb.AppendLine($"  [{meta.Module.Name}] {status} " +
                    $"(Init: {meta.InitDuration.TotalMilliseconds:F1}ms, " +
                    $"PostInit: {meta.PostInitDuration.TotalMilliseconds:F1}ms)");
            }

            return sb.ToString();
        }
    }

    // ====== 引擎模块特性 ======
    /// <summary>
    /// 标记一个模块类。ModuleManager 可以通过程序集扫描自动注册带有此特性的模块。
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public class EngineModuleAttribute : Attribute
    {
        public string Name { get; }
        public bool AutoRegister { get; set; } = true;
        public EngineModuleAttribute(string name) => Name = name;
    }

    // ====== 内置核心模块实现 ======
    /// <summary>核心模块 —— 初始化地基系统。</summary>
    [EngineModule("Core", AutoRegister = true)]
    public sealed class CoreModule : EngineModuleBase
    {
        public override string Name => "Core";
        public override int Priority => -100; // 最先加载

        public override bool Initialize()
        {
            Diagnostics.Log.Info("山雨引擎核心模块初始化...");
            Diagnostics.Log.Info($"  平台: {(Environment.Is64BitProcess ? "64-bit" : "32-bit")}, " +
                $"CPU 核数: {Environment.ProcessorCount}, " +
                $"框架: {Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
            return true;
        }

        public override bool PostInitialize()
        {
            // 初始化时间管理器
            _ = Time.TimeManager.Instance;
            return true;
        }
    }

    /// <summary>Job 系统模块。</summary>
    [EngineModule("JobSystem")]
    public sealed class JobModule : EngineModuleBase
    {
        public override string Name => "JobSystem";
        public override int Priority => -80;
        public override string[] Dependencies => new[] { "Core" };

        private Jobs.JobSystem? _jobSystem;

        public override bool Initialize()
        {
            _jobSystem = new Jobs.JobSystem();
            Diagnostics.Log.Info($"JobSystem 已启动: {_jobSystem.WorkerCount} 个工作线程");
            return true;
        }

        public override void Shutdown()
        {
            _jobSystem?.Dispose();
        }
    }

    /// <summary>配置文件模块。</summary>
    [EngineModule("Config")]
    public sealed class ConfigModule : EngineModuleBase
    {
        public override string Name => "Config";
        public override int Priority => -60;
        public override string[] Dependencies => new[] { "Core" };

        public override bool Initialize()
        {
            Config.ConfigManager.Instance.Initialize("config_project.json", "config_user.json");
            return true;
        }
    }

    /// <summary>虚拟文件系统模块。</summary>
    [EngineModule("VirtualFileSystem")]
    public sealed class VFSModule : EngineModuleBase
    {
        public override string Name => "VirtualFileSystem";
        public override int Priority => -40;
        public override string[] Dependencies => new[] { "Core", "Config" };

        public override bool Initialize()
        {
            Diagnostics.Log.Info("虚拟文件系统已就绪");
            return true;
        }
    }

    // ====== 模块管理器扩展 — 程序集自动发现 ======
    public static class ModuleDiscovery
    {
        /// <summary>扫描当前程序集，自动注册所有带 EngineModule 特性的模块。</summary>
        public static void AutoRegisterFromAssembly(Assembly? assembly = null)
        {
            var asm = assembly ?? Assembly.GetExecutingAssembly();
            foreach (var type in asm.GetTypes())
            {
                var attr = type.GetCustomAttribute<EngineModuleAttribute>();
                if (attr == null || !attr.AutoRegister) continue;

                if (typeof(IEngineModule).IsAssignableFrom(type) && !type.IsAbstract)
                {
                    try
                    {
                        var module = (IEngineModule)Activator.CreateInstance(type)!;
                        ModuleManager.Register(module);
                    }
                    catch (Exception ex)
                    {
                        Diagnostics.Log.Error($"自动注册模块 {type.Name} 失败: {ex.Message}");
                    }
                }
            }
        }
    }
}
