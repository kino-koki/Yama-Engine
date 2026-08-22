using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ShanyuEngine.ECS;

namespace ShanyuEngine.Core
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - 游戏功能插件系统 (GameFeaturesSystem)
    //
    // 本文件实现了完整的游戏功能插件（Game Feature）生命周期管理,
    // 设计灵感来自 Unreal Engine 5 的 GameFeatures / PluginManager, 但
    // 针对山雨引擎的 ECS + 子系统架构做了定制化对接。
    //
    // 核心能力:
    //   * GameFeature          —— 功能定义（名称/版本/依赖/状态/插件 DLL 路径）
    //   * GameFeaturePlugin    —— 插件 DLL 动态加载/卸载（基于 AssemblyLoadContext）
    //   * GameFeatureState     —— 状态机（Installed->Registered->Loaded->Active->...）
    //   * GameFeatureAction    —— 激活/停用动作基类与若干内置实现
    //       - GameFeatureAction_AddComponent    : 注册 ECS 组件类型
    //       - GameFeatureAction_RegisterSystem  : 注册 ECS 系统
    //       - GameFeatureAction_LoadAsset       : 加载资源
    //       - GameFeatureAction_InjectConfig    : 注入配置到子系统
    //   * GameFeaturesSubsystem—— 子系统管理器（注册/加载/卸载/依赖拓扑排序/热加载）
    //   * PluginManifest       —— 插件清单文件结构（JSON 格式）
    //
    // 对接系统:
    //   - Core/EngineBootstrap : 作为引擎子系统注册与驱动
    //   - ECS                  : 动态注册组件 / 系统
    //   - Assets/AssetDatabase : 通过回调加载资源
    //   - Scripting/ScriptEngine: 插件内脚本程序集可被脚本引擎引用
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────
    // 1. 功能状态枚举 (GameFeatureState)
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 游戏功能插件的生命周期状态。
    /// 状态转换严格单向推进, 不允许跳跃, 由 <see cref="GameFeaturesSubsystem"/> 集中驱动。
    /// </summary>
    public enum GameFeatureState
    {
        /// <summary>已安装: 清单文件已被发现并解析, 但尚未注册到子系统。</summary>
        Installed,
        /// <summary>已注册: 功能对象已创建并登记到子系统, 依赖关系已建立。</summary>
        Registered,
        /// <summary>已加载: 插件 DLL 已载入内存, 类型已就绪, 但尚未激活。</summary>
        Loaded,
        /// <summary>活动: 所有激活动作已执行, 功能对游戏可见可用。</summary>
        Active,
        /// <summary>停用中: 正在执行停用动作, 此为瞬态, 完成后进入 Unloaded。</summary>
        Deactivating,
        /// <summary>已卸载: 插件 DLL 已卸载, 资源已释放, 可重新加载或移除。</summary>
        Unloaded,
        /// <summary>错误: 加载或激活过程中发生异常, 需人工介入修复。</summary>
        Error
    }

    // ──────────────────────────────────────────────────────────────
    // 2. 插件清单 (PluginManifest)
    //    JSON 格式, 描述插件元数据、依赖、动作列表。
    //    每个 .syplugin 文件即一份序列化的 PluginManifest。
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 插件清单文件结构。
    /// 对应磁盘上的 <c>*.syplugin</c> JSON 文件, 是功能插件注册的入口。
    /// </summary>
    public sealed class PluginManifest
    {
        /// <summary>功能唯一标识（建议使用反向域名, 如 com.shanyu.battle）。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>面向用户的显示名称。</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>功能语义版本号（语义化版本, 如 1.4.2）。</summary>
        public string Version { get; set; } = "0.0.0";

        /// <summary>作者信息。</summary>
        public string Author { get; set; } = string.Empty;

        /// <summary>功能描述。</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>插件 DLL 文件相对路径（相对清单文件所在目录）。</summary>
        public string DllPath { get; set; } = string.Empty;

        /// <summary>依赖的其他功能名称列表（必须先于本功能激活）。</summary>
        public List<string> Dependencies { get; set; } = new();

        /// <summary>本功能提供的可选模块列表（可被其他功能依赖）。</summary>
        public List<string> Provides { get; set; } = new();

        /// <summary>激活时执行的动作序列（按顺序执行）。</summary>
        public List<PluginManifestAction> ActivateActions { get; set; } = new();

        /// <summary>停用时执行的动作序列（按逆序执行）。</summary>
        public List<PluginManifestAction> DeactivateActions { get; set; } = new();

        /// <summary>默认是否启用（引擎启动时自动激活）。</summary>
        public bool bDefaultEnabled { get; set; }

        /// <summary>所需的最小引擎版本（语义化版本字符串）。</summary>
        public string MinEngineVersion { get; set; } = "1.0.0";

        /// <summary>是否为内置功能（不可卸载, 仅可停用）。</summary>
        public bool bBuiltIn { get; set; }

        /// <summary>从 JSON 文本反序列化为清单对象。</summary>
        public static PluginManifest? FromJson(string json)
        {
            try
            {
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                };
                return JsonSerializer.Deserialize<PluginManifest>(json, options);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GameFeatures] 清单反序列化失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>将清单对象序列化为 JSON 文本。</summary>
        public string ToJson()
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            return JsonSerializer.Serialize(this, options);
        }
    }

    /// <summary>
    /// 清单中的动作声明。运行时由 <see cref="GameFeaturesSubsystem"/>
    /// 根据类型名实例化为对应的 <see cref="GameFeatureAction"/>。
    /// </summary>
    public sealed class PluginManifestAction
    {
        /// <summary>动作类型全名（如 AddComponent / RegisterSystem / LoadAsset / InjectConfig）。</summary>
        public string Type { get; set; } = string.Empty;

        /// <summary>动作参数（键值对, 具体含义由动作子类决定）。</summary>
        public Dictionary<string, string> Parameters { get; set; } = new();
    }

    // ──────────────────────────────────────────────────────────────
    // 3. 功能定义 (GameFeature)
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 游戏功能定义。
    /// 一个 GameFeature 对应一份清单文件, 持有运行时状态与插件实例。
    /// 功能对象本身是轻量的, 真正的重资源（程序集）由插件按需载入。
    /// </summary>
    public sealed class GameFeature
    {
        /// <summary>功能唯一标识。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>显示名称。</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>语义版本号。</summary>
        public Version Version { get; set; } = new(0, 0, 0);

        /// <summary>依赖的其他功能名称列表。</summary>
        public List<string> Dependencies { get; set; } = new();

        /// <summary>本功能提供的模块名。</summary>
        public List<string> Provides { get; set; } = new();

        /// <summary>当前运行时状态。</summary>
        public GameFeatureState State { get; set; } = GameFeatureState.Installed;

        /// <summary>是否默认启用。</summary>
        public bool bDefaultEnabled { get; set; }

        /// <summary>是否为内置功能。</summary>
        public bool bBuiltIn { get; set; }

        /// <summary>插件 DLL 绝对路径（若为空则纯配置型功能, 无程序集）。</summary>
        public string DllPath { get; set; } = string.Empty;

        /// <summary>清单文件所在目录（用于解析相对路径）。</summary>
        public string BaseDirectory { get; set; } = string.Empty;

        /// <summary>对应的插件实例（DLL 加载后非空）。</summary>
        public GameFeaturePlugin? Plugin { get; set; }

        /// <summary>激活动作列表（运行时由清单实例化）。</summary>
        public List<GameFeatureAction> ActivateActions { get; set; } = new();

        /// <summary>停用动作列表。</summary>
        public List<GameFeatureAction> DeactivateActions { get; set; } = new();

        /// <summary>原始清单引用。</summary>
        public PluginManifest? Manifest { get; set; }

        /// <summary>从清单构建功能对象。</summary>
        public static GameFeature FromManifest(PluginManifest manifest, string baseDir)
        {
            Version ver;
            try { ver = Version.Parse(manifest.Version); }
            catch { ver = new Version(0, 0, 0); }

            var dllPath = string.IsNullOrEmpty(manifest.DllPath)
                ? string.Empty
                : Path.GetFullPath(Path.Combine(baseDir, manifest.DllPath));

            return new GameFeature
            {
                Name = manifest.Name,
                DisplayName = string.IsNullOrEmpty(manifest.DisplayName) ? manifest.Name : manifest.DisplayName,
                Version = ver,
                Dependencies = new List<string>(manifest.Dependencies),
                Provides = new List<string>(manifest.Provides),
                State = GameFeatureState.Installed,
                bDefaultEnabled = manifest.bDefaultEnabled,
                bBuiltIn = manifest.bBuiltIn,
                DllPath = dllPath,
                BaseDirectory = baseDir,
                Manifest = manifest
            };
        }

        public override string ToString() => $"{DisplayName} v{Version} [{State}]";
    }

    // ──────────────────────────────────────────────────────────────
    // 4. 插件程序集管理 (GameFeaturePlugin)
    //    基于 AssemblyLoadContext 实现 DLL 的动态加载与卸载,
    //    支持热重载。每个功能插件拥有独立的 ALC, 互不污染。
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 插件程序集包装。
    /// 负责将功能 DLL 载入到独立的 <see cref="AssemblyLoadContext"/>,
    /// 以实现隔离与卸载。卸载后该上下文及其全部程序集可被 GC 回收。
    /// </summary>
    public sealed class GameFeaturePlugin
    {
        /// <summary>所属功能名。</summary>
        public string FeatureName { get; }

        /// <summary>插件 DLL 绝对路径。</summary>
        public string DllPath { get; }

        /// <summary>可收集的程序集加载上下文（支持卸载）。</summary>
        private readonly PluginAssemblyLoadContext _context;

        /// <summary>已加载的主程序集。</summary>
        public Assembly? Assembly { get; private set; }

        /// <summary>是否已加载。</summary>
        public bool IsLoaded => Assembly != null;

        /// <summary>插件入口点实例（实现 IGameFeatureModule 接口, 可为空）。</summary>
        public IGameFeatureModule? Module { get; private set; }

        public GameFeaturePlugin(string featureName, string dllPath)
        {
            FeatureName = featureName;
            DllPath = dllPath;
            _context = new PluginAssemblyLoadContext(featureName, dllPath);
        }

        /// <summary>加载插件程序集并实例化模块入口。</summary>
        public bool Load()
        {
            if (IsLoaded) return true;
            try
            {
                if (!File.Exists(DllPath))
                {
                    Console.WriteLine($"[GameFeatures] 插件 DLL 不存在: {DllPath}");
                    return false;
                }
                Assembly = _context.LoadFromAssemblyPath(DllPath);
                Module = DiscoverModule(Assembly);
                Module?.OnLoaded();
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GameFeatures] 加载插件 {FeatureName} 失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>扫描程序集中实现 IGameFeatureModule 的类型并实例化。</summary>
        private IGameFeatureModule? DiscoverModule(Assembly asm)
        {
            foreach (var type in asm.GetTypes())
            {
                if (typeof(IGameFeatureModule).IsAssignableFrom(type) && !type.IsAbstract && !type.IsInterface)
                {
                    try { return (IGameFeatureModule)Activator.CreateInstance(type)!; }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[GameFeatures] 实例化模块 {type.Name} 失败: {ex.Message}");
                    }
                }
            }
            return null;
        }

        /// <summary>卸载插件程序集。调用后所有从该程序集创建的对象都应废弃。</summary>
        public bool Unload()
        {
            if (!IsLoaded) return true;
            try
            {
                Module?.OnUnloaded();
                Module = null;
                Assembly = null;
                _context.Unload();
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GameFeatures] 卸载插件 {FeatureName} 失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>从程序集中查找指定类型（用于动作实例化或反射注册）。</summary>
        public Type? FindType(string fullName)
        {
            if (Assembly == null) return null;
            return Assembly.GetType(fullName);
        }
    }

    /// <summary>
    /// 插件可收集程序集加载上下文。
    /// 使用自定义解析逻辑确保插件依赖的程序集能从插件目录加载,
    /// 同时允许引擎核心程序集从默认上下文共享（避免重复加载引擎类型）。
    /// </summary>
    internal sealed class PluginAssemblyLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;
        private readonly string _pluginDir;

        public PluginAssemblyLoadContext(string name, string mainAssemblyPath) : base(name, isCollectible: true)
        {
            _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
            _pluginDir = Path.GetDirectoryName(mainAssemblyPath) ?? string.Empty;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // 引擎核心程序集由默认上下文提供, 避免类型身份分裂
            if (assemblyName.Name != null &&
                (assemblyName.Name.StartsWith("ShanyuEngine", StringComparison.OrdinalIgnoreCase) ||
                 assemblyName.Name.StartsWith("System", StringComparison.OrdinalIgnoreCase) ||
                 assemblyName.Name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)))
            {
                return null; // 返回 null 表示由默认上下文解析
            }

            // 优先用依赖解析器（处理 .deps.json）
            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            if (path != null) return LoadFromAssemblyPath(path);

            // 回退: 在插件目录按简单名查找
            var simple = Path.Combine(_pluginDir, assemblyName.Name + ".dll");
            if (File.Exists(simple)) return LoadFromAssemblyPath(simple);

            return null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path != null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
        }
    }

    /// <summary>
    /// 插件模块入口接口。
    /// 插件 DLL 可暴露一个实现该接口的类型, 在加载/卸载时收到通知,
    /// 用于执行自定义初始化（如注册控制台命令、钩子等）。
    /// </summary>
    public interface IGameFeatureModule
    {
        /// <summary>插件程序集被加载时调用。</summary>
        void OnLoaded();
        /// <summary>插件程序集即将卸载时调用。</summary>
        void OnUnloaded();
    }

    // ──────────────────────────────────────────────────────────────
    // 5. 功能动作系统 (GameFeatureAction)
    //    动作是功能激活/停用时执行的具体操作单元。
    //    每个动作实现 Activate / Deactivate 一对方法, 保证可逆。
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 游戏功能动作基类。
    /// 激活时按列表顺序执行 <see cref="Activate"/>,
    /// 停用时按列表逆序执行 <see cref="Deactivate"/>, 以保证资源正确释放。
    /// </summary>
    public abstract class GameFeatureAction
    {
        /// <summary>动作所属功能。</summary>
        public GameFeature? Owner { get; set; }

        /// <summary>动作参数（来自清单）。</summary>
        public Dictionary<string, string> Parameters { get; set; } = new();

        /// <summary>从参数字典初始化动作。</summary>
        public abstract void Configure(Dictionary<string, string> parameters);

        /// <summary>激活: 执行正向操作（注册组件/系统/加载资源等）。</summary>
        public abstract void Activate(GameFeaturesSubsystem subsystem);

        /// <summary>停用: 执行反向操作（注销组件/系统/卸载资源等）。</summary>
        public abstract void Deactivate(GameFeaturesSubsystem subsystem);

        /// <summary>读取必需参数, 缺失时抛出异常。</summary>
        protected string Require(string key)
        {
            if (Parameters.TryGetValue(key, out var v)) return v;
            throw new InvalidOperationException($"动作 {GetType().Name} 缺少必需参数: {key}");
        }

        /// <summary>读取可选参数, 缺失时返回默认值。</summary>
        protected string Optional(string key, string defaultValue = "")
            => Parameters.TryGetValue(key, out var v) ? v : defaultValue;
    }

    /// <summary>
    /// 动作: 注册 ECS 组件类型。
    /// 将插件程序集中定义的组件类型注册到引擎, 使其在编辑器与运行时可见。
    /// 停用时注销该组件类型。
    /// </summary>
    public sealed class GameFeatureAction_AddComponent : GameFeatureAction
    {
        /// <summary>组件类型全名（程序集限定）。</summary>
        private string _componentTypeName = string.Empty;

        /// <summary>注册后分配的类别名（用于编辑器分类）。</summary>
        private string _category = string.Empty;

        public override void Configure(Dictionary<string, string> parameters)
        {
            Parameters = parameters;
            _componentTypeName = Require("ComponentType");
            _category = Optional("Category", "Gameplay");
        }

        public override void Activate(GameFeaturesSubsystem subsystem)
        {
            var type = ResolveType(subsystem);
            if (type == null)
            {
                Console.WriteLine($"[GameFeatures] AddComponent: 找不到类型 {_componentTypeName}");
                return;
            }
            subsystem.RegisterComponentType(type, _category);
            Console.WriteLine($"[GameFeatures] 已注册组件类型: {type.FullName} (分类: {_category})");
        }

        public override void Deactivate(GameFeaturesSubsystem subsystem)
        {
            var type = ResolveType(subsystem);
            if (type == null) return;
            subsystem.UnregisterComponentType(type);
            Console.WriteLine($"[GameFeatures] 已注销组件类型: {type.FullName}");
        }

        private Type? ResolveType(GameFeaturesSubsystem subsystem)
        {
            // 优先从插件程序集查找
            if (Owner?.Plugin?.Assembly != null)
            {
                var t = Owner.Plugin.Assembly.GetType(_componentTypeName);
                if (t != null) return t;
            }
            // 回退到全局类型查找
            return Type.GetType(_componentTypeName);
        }
    }

    /// <summary>
    /// 动作: 注册 ECS 系统。
    /// 将插件定义的系统类型实例化并加入 ECS 系统列表, 使其参与每帧更新。
    /// 停用时从 ECS 移除并释放。
    /// </summary>
    public sealed class GameFeatureAction_RegisterSystem : GameFeatureAction
    {
        /// <summary>系统类型全名。</summary>
        private string _systemTypeName = string.Empty;

        /// <summary>系统更新顺序优先级（数值越小越先执行）。</summary>
        private int _order;

        /// <summary>运行时创建的系统实例（停用时用）。</summary>
        private ISystem? _systemInstance;

        public override void Configure(Dictionary<string, string> parameters)
        {
            Parameters = parameters;
            _systemTypeName = Require("SystemType");
            int.TryParse(Optional("Order", "100"), out _order);
        }

        public override void Activate(GameFeaturesSubsystem subsystem)
        {
            Type? type = null;
            if (Owner?.Plugin?.Assembly != null)
                type = Owner.Plugin.Assembly.GetType(_systemTypeName);
            type ??= Type.GetType(_systemTypeName);
            if (type == null)
            {
                Console.WriteLine($"[GameFeatures] RegisterSystem: 找不到类型 {_systemTypeName}");
                return;
            }
            try
            {
                _systemInstance = (ISystem)Activator.CreateInstance(type)!;
                subsystem.ECS?.AddSystem(_systemInstance);
                subsystem.RegisterActiveSystem(Owner!, _systemInstance);
                Console.WriteLine($"[GameFeatures] 已注册系统: {type.FullName} (Order={_order})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GameFeatures] 注册系统 {type.FullName} 失败: {ex.Message}");
            }
        }

        public override void Deactivate(GameFeaturesSubsystem subsystem)
        {
            if (_systemInstance == null) return;
            subsystem.ECS?.RemoveSystem(_systemInstance);
            subsystem.UnregisterActiveSystem(Owner!, _systemInstance);
            Console.WriteLine($"[GameFeatures] 已移除系统: {_systemInstance.Name}");
            _systemInstance = null;
        }
    }

    /// <summary>
    /// 动作: 加载资源。
    /// 通过回调将资源路径推给 AssetDatabase 进行加载,
    /// 停用时通知 AssetDatabase 释放该资源。
    /// </summary>
    public sealed class GameFeatureAction_LoadAsset : GameFeatureAction
    {
        /// <summary>资源相对路径（相对工程根或功能目录）。</summary>
        private string _assetPath = string.Empty;

        /// <summary>资源类型标签（供 AssetDatabase 选择加载器）。</summary>
        private string _assetType = string.Empty;

        /// <summary>是否阻塞直到加载完成。</summary>
        private bool _bBlocking;

        public override void Configure(Dictionary<string, string> parameters)
        {
            Parameters = parameters;
            _assetPath = Require("AssetPath");
            _assetType = Optional("AssetType", "Generic");
            bool.TryParse(Optional("bBlocking", "false"), out _bBlocking);
        }

        public override void Activate(GameFeaturesSubsystem subsystem)
        {
            var fullPath = ResolveAssetPath(subsystem);
            subsystem.LoadAssetForFeature(Owner!, fullPath, _assetType, _bBlocking);
            Console.WriteLine($"[GameFeatures] 已请求加载资源: {fullPath} (类型: {_assetType})");
        }

        public override void Deactivate(GameFeaturesSubsystem subsystem)
        {
            var fullPath = ResolveAssetPath(subsystem);
            subsystem.UnloadAssetForFeature(Owner!, fullPath);
            Console.WriteLine($"[GameFeatures] 已请求卸载资源: {fullPath}");
        }

        private string ResolveAssetPath(GameFeaturesSubsystem subsystem)
        {
            if (Path.IsPathRooted(_assetPath)) return _assetPath;
            // 优先相对功能目录解析, 其次相对工程根
            var featureRelative = Path.Combine(Owner?.BaseDirectory ?? "", _assetPath);
            if (File.Exists(featureRelative)) return Path.GetFullPath(featureRelative);
            return _assetPath;
        }
    }

    /// <summary>
    /// 动作: 注入配置。
    /// 将清单中声明的配置键值对注入到目标子系统, 用于参数化功能行为。
    /// 停用时恢复默认值（若提供了 Default）。
    /// </summary>
    public sealed class GameFeatureAction_InjectConfig : GameFeatureAction
    {
        /// <summary>目标子系统名（在子系统注册表中查找）。</summary>
        private string _targetSubsystem = string.Empty;

        /// <summary>配置键值对集合。</summary>
        private readonly List<(string Key, string Value, string? Default)> _configs = new();

        public override void Configure(Dictionary<string, string> parameters)
        {
            Parameters = parameters;
            _targetSubsystem = Require("TargetSubsystem");
            // 参数中以 "Config." 为前缀的键视为要注入的配置项
            foreach (var kv in parameters)
            {
                if (kv.Key.StartsWith("Config.", StringComparison.OrdinalIgnoreCase))
                {
                    var key = kv.Key.Substring("Config.".Length);
                    var parts = kv.Value.Split('|');
                    var value = parts[0];
                    var def = parts.Length > 1 ? parts[1] : null;
                    _configs.Add((key, value, def));
                }
            }
        }

        public override void Activate(GameFeaturesSubsystem subsystem)
        {
            var target = subsystem.GetSubsystem(_targetSubsystem);
            if (target == null)
            {
                Console.WriteLine($"[GameFeatures] InjectConfig: 找不到子系统 {_targetSubsystem}");
                return;
            }
            foreach (var (key, value, _) in _configs)
            {
                subsystem.InjectConfig(target, key, value);
            }
            Console.WriteLine($"[GameFeatures] 已向 {_targetSubsystem} 注入 {_configs.Count} 项配置");
        }

        public override void Deactivate(GameFeaturesSubsystem subsystem)
        {
            var target = subsystem.GetSubsystem(_targetSubsystem);
            if (target == null) return;
            foreach (var (key, _, def) in _configs)
            {
                if (def != null)
                    subsystem.InjectConfig(target, key, def);
                else
                    subsystem.RemoveConfig(target, key);
            }
            Console.WriteLine($"[GameFeatures] 已从 {_targetSubsystem} 恢复 {_configs.Count} 项配置");
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 6. 功能状态变更事件
    // ──────────────────────────────────────────────────────────────

    /// <summary>功能状态变更事件参数。</summary>
    public sealed class GameFeatureStateChangedEventArgs : EventArgs
    {
        /// <summary>功能名。</summary>
        public string FeatureName { get; init; } = string.Empty;

        /// <summary>旧状态。</summary>
        public GameFeatureState OldState { get; init; }

        /// <summary>新状态。</summary>
        public GameFeatureState NewState { get; init; }

        /// <summary>功能对象引用。</summary>
        public GameFeature? Feature { get; init; }

        public override string ToString()
            => $"[{FeatureName}] {OldState} -> {NewState}";
    }

    // ──────────────────────────────────────────────────────────────
    // 7. 子系统管理器 (GameFeaturesSubsystem)
    //    这是整个插件系统的核心, 负责功能的发现、注册、依赖解析、
    //    生命周期驱动、热加载与事件分发。
    //    作为引擎子系统, 在 EngineBootstrap 初始化时被创建与注册。
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 游戏功能子系统管理器。
    /// 单例式运行, 负责协调所有游戏功能插件的全生命周期。
    /// </summary>
    public sealed class GameFeaturesSubsystem : IDisposable
    {
        // ── 单例 ──────────────────────────────────────────────────
        private static GameFeaturesSubsystem? _instance;
        /// <summary>全局实例（由引擎引导器设置）。</summary>
        public static GameFeaturesSubsystem Instance => _instance ??= new GameFeaturesSubsystem();

        // ── 内部存储 ──────────────────────────────────────────────

        /// <summary>已注册的全部功能（按名称索引）。</summary>
        private readonly Dictionary<string, GameFeature> _features = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>已注册的组件类型集合（含分类）。</summary>
        private readonly Dictionary<Type, string> _registeredComponents = new();

        /// <summary>功能 -> 其激活的系统实例列表。</summary>
        private readonly Dictionary<string, List<ISystem>> _activeSystems = new();

        /// <summary>功能 -> 其加载的资源路径列表。</summary>
        private readonly Dictionary<string, List<string>> _loadedAssets = new();

        /// <summary>已注册的引擎子系统（用于 InjectConfig / 查找）。</summary>
        private readonly Dictionary<string, object> _subsystems = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>子系统配置存储（子系统 -> 键 -> 值）。</summary>
        private readonly Dictionary<object, Dictionary<string, string>> _subsystemConfigs = new();

        /// <summary>动作类型注册表（类型名 -> 构造工厂）。</summary>
        private readonly Dictionary<string, Func<GameFeatureAction>> _actionFactories = new(StringComparer.OrdinalIgnoreCase);

        // ── 引擎引用 ──────────────────────────────────────────────

        /// <summary>ECS 世界引用（用于注册组件/系统）。</summary>
        public EntityComponentSystem? ECS { get; set; }

        /// <summary>资产数据库引用（用于加载/卸载资源）。</summary>
        public object? AssetDatabase { get; set; }

        /// <summary>资源加载回调（由外部 AssetDatabase 注入）。</summary>
        public Action<string, string, bool>? AssetLoadCallback { get; set; }

        /// <summary>资源卸载回调。</summary>
        public Action<string>? AssetUnloadCallback { get; set; }

        // ── 事件 ──────────────────────────────────────────────────

        /// <summary>功能状态变更事件。</summary>
        public event EventHandler<GameFeatureStateChangedEventArgs>? FeatureStateChanged;

        /// <summary>功能注册事件。</summary>
        public event Action<GameFeature>? FeatureRegistered;

        /// <summary>功能卸载事件。</summary>
        public event Action<GameFeature>? FeatureUnloaded;

        // ── 依赖解析结果缓存 ──────────────────────────────────────
        private List<string>? _topologicalOrderCache;
        private bool _topoCacheDirty = true;

        /// <summary>已注册功能数量。</summary>
        public int FeatureCount => _features.Count;

        /// <summary>当前活动（Active）功能数量。</summary>
        public int ActiveFeatureCount => _features.Values.Count(f => f.State == GameFeatureState.Active);

        // ──────────────────────────────────────────────────────────
        // 构造与初始化
        // ──────────────────────────────────────────────────────────

        private GameFeaturesSubsystem()
        {
            RegisterBuiltinActions();
        }

        /// <summary>注册内置动作工厂。</summary>
        private void RegisterBuiltinActions()
        {
            RegisterAction("AddComponent", () => new GameFeatureAction_AddComponent());
            RegisterAction("RegisterSystem", () => new GameFeatureAction_RegisterSystem());
            RegisterAction("LoadAsset", () => new GameFeatureAction_LoadAsset());
            RegisterAction("InjectConfig", () => new GameFeatureAction_InjectConfig());
        }

        /// <summary>
        /// 初始化子系统, 绑定引擎引用。
        /// 由 <see cref="EngineBootstrap"/> 在初始化阶段调用。
        /// </summary>
        public void Initialize(EntityComponentSystem ecs, object? assetDatabase = null)
        {
            ECS = ecs;
            AssetDatabase = assetDatabase;
            Console.WriteLine("[GameFeatures] 游戏功能子系统已初始化");
        }

        /// <summary>注册自定义动作类型（供第三方插件扩展）。</summary>
        public void RegisterAction(string typeName, Func<GameFeatureAction> factory)
        {
            _actionFactories[typeName] = factory;
        }

        // ──────────────────────────────────────────────────────────
        // 子系统注册（供 InjectConfig 与查找）
        // ──────────────────────────────────────────────────────────

        /// <summary>注册一个引擎子系统, 供功能插件配置注入与查找。</summary>
        public void RegisterSubsystem(string name, object subsystem)
        {
            _subsystems[name] = subsystem;
        }

        /// <summary>按名获取已注册子系统。</summary>
        public object? GetSubsystem(string name)
            => _subsystems.TryGetValue(name, out var s) ? s : null;

        /// <summary>向子系统注入一项配置。</summary>
        public void InjectConfig(object subsystem, string key, string value)
        {
            if (!_subsystemConfigs.TryGetValue(subsystem, out var dict))
            {
                dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _subsystemConfigs[subsystem] = dict;
            }
            dict[key] = value;
            // 若子系统实现 IConfigurationTarget 接口, 通知其应用配置
            if (subsystem is IConfigurationTarget configurable)
                configurable.ApplyConfig(key, value);
        }

        /// <summary>移除子系统的一项配置。</summary>
        public void RemoveConfig(object subsystem, string key)
        {
            if (_subsystemConfigs.TryGetValue(subsystem, out var dict))
                dict.Remove(key);
        }

        // ──────────────────────────────────────────────────────────
        // 功能发现与注册
        // ──────────────────────────────────────────────────────────

        /// <summary>从指定目录扫描所有 .syplugin 清单文件并注册。</summary>
        public int DiscoverPlugins(string pluginsDirectory)
        {
            int count = 0;
            if (!Directory.Exists(pluginsDirectory))
            {
                Console.WriteLine($"[GameFeatures] 插件目录不存在: {pluginsDirectory}");
                return 0;
            }
            foreach (var file in Directory.EnumerateFiles(pluginsDirectory, "*.syplugin", SearchOption.AllDirectories))
            {
                if (RegisterFromManifest(file))
                    count++;
            }
            Console.WriteLine($"[GameFeatures] 从 {pluginsDirectory} 发现并注册了 {count} 个功能插件");
            return count;
        }

        /// <summary>从清单文件注册一个功能。</summary>
        public bool RegisterFromManifest(string manifestPath)
        {
            try
            {
                var json = File.ReadAllText(manifestPath, Encoding.UTF8);
                var manifest = PluginManifest.FromJson(json);
                if (manifest == null) return false;
                var baseDir = Path.GetDirectoryName(manifestPath) ?? string.Empty;
                return RegisterFeature(manifest, baseDir);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GameFeatures] 读取清单 {manifestPath} 失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>从清单对象注册一个功能。</summary>
        public bool RegisterFeature(PluginManifest manifest, string baseDir)
        {
            if (string.IsNullOrEmpty(manifest.Name))
            {
                Console.WriteLine("[GameFeatures] 清单缺少 Name 字段, 跳过注册");
                return false;
            }
            if (_features.ContainsKey(manifest.Name))
            {
                Console.WriteLine($"[GameFeatures] 功能 {manifest.Name} 已存在, 跳过重复注册");
                return false;
            }

            var feature = GameFeature.FromManifest(manifest, baseDir);
            // 实例化激活/停用动作
            InstantiateActions(feature);
            _features[manifest.Name] = feature;
            feature.State = GameFeatureState.Registered;
            _topoCacheDirty = true;
            FeatureRegistered?.Invoke(feature);
            Console.WriteLine($"[GameFeatures] 已注册功能: {feature.Name} v{feature.Version}");
            return true;
        }

        /// <summary>根据清单声明实例化动作对象。</summary>
        private void InstantiateActions(GameFeature feature)
        {
            if (feature.Manifest == null) return;
            foreach (var decl in feature.Manifest.ActivateActions)
                InstantiateAction(decl, feature, feature.ActivateActions);
            foreach (var decl in feature.Manifest.DeactivateActions)
                InstantiateAction(decl, feature, feature.DeactivateActions);
        }

        private void InstantiateAction(PluginManifestAction decl, GameFeature owner, List<GameFeatureAction> target)
        {
            if (!_actionFactories.TryGetValue(decl.Type, out var factory))
            {
                Console.WriteLine($"[GameFeatures] 未知动作类型: {decl.Type}, 跳过");
                return;
            }
            var action = factory();
            action.Owner = owner;
            action.Parameters = decl.Parameters ?? new Dictionary<string, string>();
            action.Configure(action.Parameters);
            target.Add(action);
        }

        // ──────────────────────────────────────────────────────────
        // 依赖解析（拓扑排序）
        // ──────────────────────────────────────────────────────────

        /// <summary>
        /// 对所有已注册功能做拓扑排序, 保证依赖在前。
        /// 使用 Kahn 算法, 检测循环依赖并抛出异常。
        /// </summary>
        public List<string> ResolveDependencies()
        {
            if (!_topoCacheDirty && _topologicalOrderCache != null)
                return _topologicalOrderCache;

            var result = new List<string>();
            var inDegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var graph = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var name in _features.Keys)
            {
                inDegree[name] = 0;
                graph[name] = new List<string>();
            }
            foreach (var feature in _features.Values)
            {
                foreach (var dep in feature.Dependencies)
                {
                    if (!_features.ContainsKey(dep))
                    {
                        Console.WriteLine($"[GameFeatures] 功能 {feature.Name} 依赖缺失的功能: {dep}");
                        continue;
                    }
                    graph[dep].Add(feature.Name);
                    inDegree[feature.Name]++;
                }
            }

            // 入度为 0 的节点入队
            var queue = new Queue<string>();
            foreach (var kv in inDegree)
                if (kv.Value == 0) queue.Enqueue(kv.Key);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                result.Add(current);
                foreach (var next in graph[current])
                {
                    inDegree[next]--;
                    if (inDegree[next] == 0) queue.Enqueue(next);
                }
            }

            if (result.Count != _features.Count)
            {
                var cyclic = _features.Keys.Except(result).ToList();
                throw new InvalidOperationException(
                    $"[GameFeatures] 检测到循环依赖, 涉及功能: {string.Join(", ", cyclic)}");
            }

            _topologicalOrderCache = result;
            _topoCacheDirty = false;
            return result;
        }

        /// <summary>获取指定功能的依赖闭包（含传递依赖）。</summary>
        public List<string> GetDependencyClosure(string featureName)
        {
            var result = new List<string>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Visit(string name)
            {
                if (!visited.Add(name)) return;
                if (_features.TryGetValue(name, out var f))
                {
                    foreach (var dep in f.Dependencies)
                        Visit(dep);
                    result.Add(name);
                }
            }
            Visit(featureName);
            return result;
        }

        // ──────────────────────────────────────────────────────────
        // 生命周期驱动: 加载 / 激活 / 停用 / 卸载
        // ──────────────────────────────────────────────────────────

        /// <summary>激活指定功能（含其依赖）。</summary>
        public bool ActivateFeature(string featureName)
        {
            if (!_features.TryGetValue(featureName, out var feature))
            {
                Console.WriteLine($"[GameFeatures] 未找到功能: {featureName}");
                return false;
            }

            // 先激活所有依赖
            foreach (var dep in feature.Dependencies)
            {
                if (_features.TryGetValue(dep, out var depFeature) &&
                    depFeature.State != GameFeatureState.Active)
                {
                    if (!ActivateFeature(dep)) return false;
                }
            }

            // 加载插件 DLL（若有）
            if (!string.IsNullOrEmpty(feature.DllPath) &&
                feature.State < GameFeatureState.Loaded)
            {
                feature.Plugin = new GameFeaturePlugin(feature.Name, feature.DllPath);
                if (!feature.Plugin.Load())
                {
                    TransitionState(feature, GameFeatureState.Error);
                    return false;
                }
                TransitionState(feature, GameFeatureState.Loaded);
            }

            // 执行激活动作
            TransitionState(feature, GameFeatureState.Active, isActivating: true);
            foreach (var action in feature.ActivateActions)
            {
                try { action.Activate(this); }
                catch (Exception ex)
                {
                    Console.WriteLine($"[GameFeatures] 动作 {action.GetType().Name} 激活失败: {ex.Message}");
                }
            }

            // 通知插件模块
            feature.Plugin?.Module?.OnLoaded();
            Console.WriteLine($"[GameFeatures] 功能已激活: {feature.Name}");
            return true;
        }

        /// <summary>停用指定功能（若被其他活动功能依赖则拒绝停用）。</summary>
        public bool DeactivateFeature(string featureName)
        {
            if (!_features.TryGetValue(featureName, out var feature))
            {
                Console.WriteLine($"[GameFeatures] 未找到功能: {featureName}");
                return false;
            }
            if (feature.State != GameFeatureState.Active)
            {
                Console.WriteLine($"[GameFeatures] 功能 {featureName} 当前状态 {feature.State}, 无需停用");
                return true;
            }

            // 检查是否有其他活动功能依赖本功能
            foreach (var other in _features.Values)
            {
                if (other.Name == featureName) continue;
                if (other.State == GameFeatureState.Active &&
                    other.Dependencies.Contains(featureName, StringComparer.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"[GameFeatures] 功能 {featureName} 仍被 {other.Name} 依赖, 拒绝停用");
                    return false;
                }
            }

            // 执行停用动作（逆序）
            TransitionState(feature, GameFeatureState.Deactivating);
            for (int i = feature.DeactivateActions.Count - 1; i >= 0; i--)
            {
                try { feature.DeactivateActions[i].Deactivate(this); }
                catch (Exception ex)
                {
                    Console.WriteLine($"[GameFeatures] 动作停用失败: {ex.Message}");
                }
            }
            // 若没有专门的停用动作, 则逆序执行激活动作的 Deactivate
            if (feature.DeactivateActions.Count == 0)
            {
                for (int i = feature.ActivateActions.Count - 1; i >= 0; i--)
                {
                    try { feature.ActivateActions[i].Deactivate(this); }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[GameFeatures] 动作反向停用失败: {ex.Message}");
                    }
                }
            }

            // 卸载插件 DLL
            if (feature.Plugin != null)
            {
                feature.Plugin.Unload();
                feature.Plugin = null;
            }

            TransitionState(feature, GameFeatureState.Unloaded);
            FeatureUnloaded?.Invoke(feature);
            Console.WriteLine($"[GameFeatures] 功能已停用: {feature.Name}");
            return true;
        }

        /// <summary>卸载并从注册表移除一个功能（必须先停用）。</summary>
        public bool UnregisterFeature(string featureName)
        {
            if (!_features.TryGetValue(featureName, out var feature)) return false;
            if (feature.bBuiltIn)
            {
                Console.WriteLine($"[GameFeatures] 内置功能 {featureName} 不可注销");
                return false;
            }
            if (feature.State == GameFeatureState.Active)
                DeactivateFeature(featureName);
            _features.Remove(featureName);
            _topoCacheDirty = true;
            Console.WriteLine($"[GameFeatures] 已注销功能: {featureName}");
            return true;
        }

        /// <summary>
        /// 热安装: 运行时从一个清单文件安装并激活新功能模块。
        /// 返回是否安装并激活成功。
        /// </summary>
        public bool HotInstall(string manifestPath)
        {
            Console.WriteLine($"[GameFeatures] 热安装: {manifestPath}");
            if (!RegisterFromManifest(manifestPath)) return false;
            // 取刚注册的功能名
            var manifest = PluginManifest.FromJson(File.ReadAllText(manifestPath, Encoding.UTF8));
            if (manifest == null) return false;
            return ActivateFeature(manifest.Name);
        }

        /// <summary>
        /// 热卸载: 运行时停用并注销一个功能模块。
        /// </summary>
        public bool HotUninstall(string featureName)
        {
            Console.WriteLine($"[GameFeatures] 热卸载: {featureName}");
            return UnregisterFeature(featureName);
        }

        /// <summary>激活所有标记为默认启用的功能（引擎启动时调用）。</summary>
        public void ActivateDefaultFeatures()
        {
            var order = ResolveDependencies();
            foreach (var name in order)
            {
                if (_features.TryGetValue(name, out var f) && f.bDefaultEnabled && f.State != GameFeatureState.Active)
                    ActivateFeature(name);
            }
        }

        // ──────────────────────────────────────────────────────────
        // 状态转换
        // ──────────────────────────────────────────────────────────

        private void TransitionState(GameFeature feature, GameFeatureState newState, bool isActivating = false)
        {
            if (feature.State == newState) return;
            var old = feature.State;
            feature.State = newState;
            FeatureStateChanged?.Invoke(this, new GameFeatureStateChangedEventArgs
            {
                FeatureName = feature.Name,
                OldState = old,
                NewState = newState,
                Feature = feature
            });
        }

        // ──────────────────────────────────────────────────────────
        // 组件类型注册（供动作调用）
        // ──────────────────────────────────────────────────────────

        /// <summary>注册一个组件类型到引擎（使其在编辑器与运行时可见）。</summary>
        public void RegisterComponentType(Type componentType, string category)
        {
            _registeredComponents[componentType] = category;
            // 若 ECS 有扩展注册点, 在此调用
            // (当前 EntityComponentSystem 使用泛型字典, 组件类型在首次 AddComponent<T> 时自动注册)
        }

        /// <summary>注销一个组件类型。</summary>
        public void UnregisterComponentType(Type componentType)
        {
            _registeredComponents.Remove(componentType);
        }

        /// <summary>获取所有已注册的组件类型及其分类。</summary>
        public IReadOnlyDictionary<Type, string> GetRegisteredComponents() => _registeredComponents;

        // ──────────────────────────────────────────────────────────
        // 系统实例注册（供动作调用）
        // ──────────────────────────────────────────────────────────

        /// <summary>登记一个功能激活的系统实例（停用时移除）。</summary>
        public void RegisterActiveSystem(GameFeature owner, ISystem system)
        {
            if (!_activeSystems.TryGetValue(owner.Name, out var list))
            {
                list = new List<ISystem>();
                _activeSystems[owner.Name] = list;
            }
            list.Add(system);
        }

        /// <summary>移除一个功能激活的系统实例。</summary>
        public void UnregisterActiveSystem(GameFeature owner, ISystem system)
        {
            if (_activeSystems.TryGetValue(owner.Name, out var list))
                list.Remove(system);
        }

        // ──────────────────────────────────────────────────────────
        // 资源加载（供动作调用）
        // ──────────────────────────────────────────────────────────

        /// <summary>为功能加载一个资源（通过外部注入的回调）。</summary>
        public void LoadAssetForFeature(GameFeature owner, string path, string assetType, bool blocking)
        {
            if (!_loadedAssets.TryGetValue(owner.Name, out var list))
            {
                list = new List<string>();
                _loadedAssets[owner.Name] = list;
            }
            list.Add(path);
            AssetLoadCallback?.Invoke(path, assetType, blocking);
        }

        /// <summary>卸载功能加载的资源。</summary>
        public void UnloadAssetForFeature(GameFeature owner, string path)
        {
            if (_loadedAssets.TryGetValue(owner.Name, out var list))
                list.Remove(path);
            AssetUnloadCallback?.Invoke(path);
        }

        // ──────────────────────────────────────────────────────────
        // 查询
        // ──────────────────────────────────────────────────────────

        /// <summary>按名获取功能。</summary>
        public GameFeature? GetFeature(string name)
            => _features.TryGetValue(name, out var f) ? f : null;

        /// <summary>获取所有已注册功能。</summary>
        public IReadOnlyCollection<GameFeature> GetAllFeatures() => _features.Values;

        /// <summary>获取所有处于指定状态的功能。</summary>
        public List<GameFeature> GetFeaturesByState(GameFeatureState state)
            => _features.Values.Where(f => f.State == state).ToList();

        /// <summary>生成功能状态摘要报告。</summary>
        public string GetStatusReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("═══ 游戏功能子系统状态 ═══");
            sb.AppendLine($"注册功能数: {FeatureCount} | 活动功能数: {ActiveFeatureCount}");
            sb.AppendLine($"已注册组件类型: {_registeredComponents.Count}");
            sb.AppendLine($"已注册动作类型: {_actionFactories.Count}");
            sb.AppendLine("───── 功能列表 ─────");
            foreach (var f in _features.Values)
            {
                sb.AppendLine($"  {f.Name,-30} v{f.Version,-10} [{f.State}] {(f.bDefaultEnabled ? "(默认启用)" : "")}");
                if (f.Dependencies.Count > 0)
                    sb.AppendLine($"    依赖: {string.Join(", ", f.Dependencies)}");
            }
            return sb.ToString();
        }

        // ──────────────────────────────────────────────────────────
        // 每帧更新（由引擎主循环调用）
        // ──────────────────────────────────────────────────────────

        /// <summary>每帧更新。当前主要用于触发待处理的异步加载完成回调。</summary>
        public void Update(float dt)
        {
            // 异步资源加载完成的回调处理可在此处进行
            // 当前为占位, 实际项目可轮询加载队列
        }

        // ──────────────────────────────────────────────────────────
        // 关闭
        // ──────────────────────────────────────────────────────────

        /// <summary>停用所有活动功能并卸载插件。</summary>
        public void Dispose()
        {
            // 逆序停用所有活动功能
            var active = _features.Values
                .Where(f => f.State == GameFeatureState.Active)
                .Select(f => f.Name)
                .ToList();
            foreach (var name in active)
                DeactivateFeature(name);

            _features.Clear();
            _registeredComponents.Clear();
            _activeSystems.Clear();
            _loadedAssets.Clear();
            _subsystems.Clear();
            _subsystemConfigs.Clear();
            Console.WriteLine("[GameFeatures] 游戏功能子系统已关闭");
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 8. 可配置子系统接口
    //    实现该接口的子系统可被 GameFeatureAction_InjectConfig 直接配置。
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 可配置子系统接口。
    /// 引擎子系统实现此接口后, 功能插件即可通过 InjectConfig 动作
    /// 在激活时向其注入运行时配置（如难度系数、重力倍率等）。
    /// </summary>
    public interface IConfigurationTarget
    {
        /// <summary>应用一项配置。</summary>
        void ApplyConfig(string key, string value);
    }
}
