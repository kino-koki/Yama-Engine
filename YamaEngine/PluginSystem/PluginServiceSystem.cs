using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading;

namespace YamaEngine.PluginSystem
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎 - 插件服务系统 (PluginServiceSystem)
    //
    // 插件间服务注册与依赖注入:
    //   * IService 接口 (服务基类)
    //   * PluginServiceRegistry (注册 / 注销 / 查找 / 依赖注入)
    //   * 服务生命周期 (Singleton / Scoped / Transient)
    //   * 服务发现 (接口→实现映射 / 自动注册 / 程序集扫描)
    //   * 服务注入 (构造函数注入 / 属性注入 / 方法注入)
    //   * 服务覆盖 (高优先级插件覆盖低优先级插件的服务实现)
    //   * 服务依赖 (循环依赖检测 / 加载顺序)
    //   * 内置服务 (日志 / 配置 / 事件 / 资产 / 网络 / 存档)
    // ════════════════════════════════════════════════════════════════════════

    #region 服务接口

    /// <summary>服务接口。</summary>
    public interface IService
    {
        /// <summary>服务名称。</summary>
        string ServiceName { get; }

        /// <summary>服务初始化。</summary>
        void Initialize();

        /// <summary>服务关闭。</summary>
        void Shutdown();
    }

    /// <summary>服务接口 (泛型)。</summary>
    public interface IService<T> : IService where T : class { }

    #endregion

    #region 服务生命周期

    /// <summary>服务生命周期。</summary>
    public enum ServiceLifetime
    {
        /// <summary>单例 (全局唯一实例)</summary>
        Singleton,
        /// <summary>作用域 (同一作用域内唯一)</summary>
        Scoped,
        /// <summary>瞬态 (每次获取创建新实例)</summary>
        Transient
    }

    #endregion

    #region 服务描述符

    /// <summary>服务描述符。</summary>
    public sealed class ServiceDescriptor
    {
        public Type ServiceType { get; set; }
        public Type ImplementationType { get; set; }
        public object Instance { get; set; }
        public Func<IServiceProvider, object> Factory { get; set; }
        public ServiceLifetime Lifetime { get; set; } = ServiceLifetime.Singleton;
        public string PluginId { get; set; }
        public int Priority { get; set; } = 100;
        public bool IsInitialized { get; set; }

        public override string ToString() =>
            $"{ServiceType?.Name} → {ImplementationType?.Name} [{Lifetime}] P:{Priority} [{PluginId}]";
    }

    #endregion

    #region 服务覆盖信息

    /// <summary>服务覆盖记录。</summary>
    public sealed class ServiceOverrideRecord
    {
        public Type ServiceType { get; set; }
        public string OriginalPluginId { get; set; }
        public string OverridePluginId { get; set; }
        public DateTime OverrideTime { get; set; } = DateTime.Now;

        public override string ToString() =>
            $"{ServiceType.Name}: {OriginalPluginId} → {OverridePluginId}";
    }

    #endregion

    #region 内置服务接口

    /// <summary>日志服务接口。</summary>
    public interface ILogService : IService
    {
        void Log(PluginLogLevel level, string message, string pluginId = null);
    }

    /// <summary>配置服务接口。</summary>
    public interface IConfigService : IService
    {
        T GetConfig<T>(string pluginId, string key, T defaultValue = default);
        void SetConfig<T>(string pluginId, string key, T value);
        void SaveConfig(string pluginId);
    }

    /// <summary>事件服务接口。</summary>
    public interface IEventService : IService
    {
        void Publish<T>(T evt) where T : PluginEvent;
        void Subscribe<T>(Action<T> handler, string pluginId = null, EventPriority priority = EventPriority.Normal) where T : PluginEvent;
    }

    /// <summary>资产服务接口。</summary>
    public interface IAssetService : IService
    {
        byte[] LoadAsset(string path);
        string LoadAssetText(string path);
        bool AssetExists(string path);
    }

    /// <summary>网络服务接口。</summary>
    public interface INetworkService : IService
    {
        bool IsConnected { get; }
        void Send(byte[] data);
        byte[] Receive();
    }

    /// <summary>存档服务接口。</summary>
    public interface ISaveService : IService
    {
        void SaveGame(string slotName);
        void LoadGame(string slotName);
        string[] GetSaveSlots();
    }

    #endregion

    #region 内置服务实现

    /// <summary>内置日志服务。</summary>
    public sealed class LogService : ILogService
    {
        public string ServiceName => "LogService";

        public void Initialize() { }
        public void Shutdown() { }

        public void Log(PluginLogLevel level, string message, string pluginId = null)
        {
            var logger = new PluginLogger(pluginId ?? "Engine");
            logger.Write(level, message);
        }
    }

    /// <summary>内置配置服务。</summary>
    public sealed class ConfigService : IConfigService
    {
        public string ServiceName => "ConfigService";
        private readonly ConcurrentDictionary<string, PluginConfig> _configs = new ConcurrentDictionary<string, PluginConfig>();

        public void Initialize() { }
        public void Shutdown() { }

        public T GetConfig<T>(string pluginId, string key, T defaultValue = default)
        {
            if (_configs.TryGetValue(pluginId, out var config))
                return config.Get(key, defaultValue);
            return defaultValue;
        }

        public void SetConfig<T>(string pluginId, string key, T value)
        {
            var config = _configs.GetOrAdd(pluginId, id => new PluginConfig(id));
            config.Set(key, value);
        }

        public void SaveConfig(string pluginId)
        {
            if (_configs.TryGetValue(pluginId, out var config))
                config.Save();
        }
    }

    /// <summary>内置事件服务。</summary>
    public sealed class EventService : IEventService
    {
        public string ServiceName => "EventService";

        public void Initialize() { }
        public void Shutdown() { }

        public void Publish<T>(T evt) where T : PluginEvent
            => PluginEventBus.Instance.Publish(evt);

        public void Subscribe<T>(Action<T> handler, string pluginId = null, EventPriority priority = EventPriority.Normal) where T : PluginEvent
            => PluginEventBus.Instance.Subscribe(handler, pluginId, priority);
    }

    /// <summary>内置资产服务。</summary>
    public sealed class AssetService : IAssetService
    {
        public string ServiceName => "AssetService";

        public void Initialize() { }
        public void Shutdown() { }

        public byte[] LoadAsset(string path) => PluginAssetProvider.Instance.LoadAssetBytes(path);
        public string LoadAssetText(string path) => PluginAssetProvider.Instance.LoadAssetText(path);
        public bool AssetExists(string path) => PluginAssetProvider.Instance.AssetExists(path);
    }

    #endregion

    #region 服务注册表

    /// <summary>插件服务注册表 (依赖注入容器)。</summary>
    public sealed class PluginServiceRegistry : IServiceProvider
    {
        private static readonly Lazy<PluginServiceRegistry> _instance =
            new Lazy<PluginServiceRegistry>(() => new PluginServiceRegistry());
        public static PluginServiceRegistry Instance => _instance.Value;

        // 服务描述符: ServiceType -> sorted list (by priority desc)
        private readonly ConcurrentDictionary<Type, List<ServiceDescriptor>> _services =
            new ConcurrentDictionary<Type, List<ServiceDescriptor>>();
        private readonly object _lock = new object();

        // 单例缓存
        private readonly ConcurrentDictionary<Type, object> _singletons = new ConcurrentDictionary<Type, object>();

        // 服务覆盖记录
        private readonly List<ServiceOverrideRecord> _overrides = new List<ServiceOverrideRecord>();

        // 服务依赖图
        private readonly Dictionary<Type, List<Type>> _dependencyGraph = new Dictionary<Type, List<Type>>();

        // 日志
        private readonly PluginLogger _logger = new PluginLogger("ServiceRegistry");

        // 事件
        public event Action<ServiceDescriptor> OnServiceRegistered;
        public event Action<ServiceDescriptor> OnServiceUnregistered;
        public event Action<ServiceOverrideRecord> OnServiceOverridden;

        // 配置
        public bool EnablePropertyInjection { get; set; } = true;
        public bool EnableMethodInjection { get; set; } = false;

        private PluginServiceRegistry()
        {
            RegisterBuiltInServices();
        }

        #region 内置服务注册

        private void RegisterBuiltInServices()
        {
            Register<ILogService, LogService>(ServiceLifetime.Singleton, "Engine", 0);
            Register<IConfigService, ConfigService>(ServiceLifetime.Singleton, "Engine", 0);
            Register<IEventService, EventService>(ServiceLifetime.Singleton, "Engine", 0);
            Register<IAssetService, AssetService>(ServiceLifetime.Singleton, "Engine", 0);
        }

        #endregion

        #region 注册

        /// <summary>注册服务 (接口→实现)。</summary>
        public void Register<TInterface, TImplementation>(TImplementation instance = null)
            where TImplementation : class, TInterface, new()
        {
            Register<TInterface, TImplementation>(ServiceLifetime.Singleton, null, 100, instance);
        }

        /// <summary>注册服务 (带生命周期)。</summary>
        public void Register<TInterface, TImplementation>(ServiceLifetime lifetime, string pluginId, int priority, TImplementation instance = null)
            where TImplementation : class, TInterface, new()
        {
            var descriptor = new ServiceDescriptor
            {
                ServiceType = typeof(TInterface),
                ImplementationType = typeof(TImplementation),
                Instance = instance,
                Lifetime = lifetime,
                PluginId = pluginId ?? "Unknown",
                Priority = priority
            };

            RegisterDescriptor(descriptor);
        }

        /// <summary>注册服务 (带工厂)。</summary>
        public void Register<TService>(Func<IServiceProvider, TService> factory, ServiceLifetime lifetime = ServiceLifetime.Singleton, string pluginId = null, int priority = 100)
            where TService : class
        {
            var descriptor = new ServiceDescriptor
            {
                ServiceType = typeof(TService),
                Factory = factory,
                Lifetime = lifetime,
                PluginId = pluginId ?? "Unknown",
                Priority = priority
            };

            RegisterDescriptor(descriptor);
        }

        /// <summary>注册服务实例 (单例)。</summary>
        public void RegisterInstance<TService>(TService instance, string pluginId = null, int priority = 100)
            where TService : class
        {
            var descriptor = new ServiceDescriptor
            {
                ServiceType = typeof(TService),
                Instance = instance,
                Lifetime = ServiceLifetime.Singleton,
                PluginId = pluginId ?? "Unknown",
                Priority = priority,
                IsInitialized = true
            };

            RegisterDescriptor(descriptor);
        }

        private void RegisterDescriptor(ServiceDescriptor descriptor)
        {
            var type = descriptor.ServiceType;

            lock (_lock)
            {
                if (!_services.TryGetValue(type, out var list))
                {
                    list = new List<ServiceDescriptor>();
                    _services[type] = list;
                }

                // 检查是否覆盖已有服务
                if (list.Count > 0)
                {
                    var topDescriptor = list[0];
                    if (descriptor.Priority > topDescriptor.Priority)
                    {
                        // 高优先级覆盖
                        var overrideRecord = new ServiceOverrideRecord
                        {
                            ServiceType = type,
                            OriginalPluginId = topDescriptor.PluginId,
                            OverridePluginId = descriptor.PluginId
                        };
                        _overrides.Add(overrideRecord);
                        OnServiceOverridden?.Invoke(overrideRecord);
                        _logger.Info($"服务覆盖: {type.Name} [{topDescriptor.PluginId} → {descriptor.PluginId}]");
                    }
                }

                list.Add(descriptor);
                list.Sort((a, b) => b.Priority.CompareTo(a.Priority));

                // 清除单例缓存 (如果覆盖了)
                if (descriptor.Lifetime == ServiceLifetime.Singleton)
                {
                    _singletons.TryRemove(type, out _);
                }
            }

            _logger.Debug($"注册服务: {descriptor}");
            OnServiceRegistered?.Invoke(descriptor);
        }

        /// <summary>注销插件的所有服务。</summary>
        public void UnregisterAll(string pluginId)
        {
            lock (_lock)
            {
                foreach (var kv in _services)
                {
                    var removed = kv.Value.RemoveAll(d => d.PluginId == pluginId);
                    if (removed > 0)
                    {
                        _singletons.TryRemove(kv.Key, out _);
                        _logger.Debug($"注销服务: {kv.Key.Name} x{removed} [{pluginId}]");
                    }
                }
            }
        }

        /// <summary>注销特定服务。</summary>
        public bool Unregister<TService>(string pluginId = null) where TService : class
        {
            var type = typeof(TService);
            lock (_lock)
            {
                if (!_services.TryGetValue(type, out var list)) return false;

                int removed;
                if (pluginId == null)
                {
                    removed = list.Count;
                    list.Clear();
                }
                else
                {
                    removed = list.RemoveAll(d => d.PluginId == pluginId);
                }

                if (removed > 0)
                {
                    _singletons.TryRemove(type, out _);
                    _logger.Debug($"注销服务: {type.Name} x{removed}");
                    return true;
                }
            }
            return false;
        }

        #endregion

        #region 获取服务

        /// <summary>获取服务 (泛型)。</summary>
        public T GetService<T>() where T : class
        {
            return (T)GetService(typeof(T));
        }

        /// <summary>获取服务。</summary>
        public object GetService(Type serviceType)
        {
            ServiceDescriptor descriptor = GetTopDescriptor(serviceType);
            if (descriptor == null) return null;

            return ResolveService(descriptor);
        }

        /// <summary>获取必需服务 (不存在则抛异常)。</summary>
        public T GetRequiredService<T>() where T : class
        {
            var service = GetService<T>();
            if (service == null)
                throw new InvalidOperationException($"服务未注册: {typeof(T).Name}");
            return service;
        }

        /// <summary>获取所有实现。</summary>
        public IEnumerable<T> GetServices<T>() where T : class
        {
            var type = typeof(T);
            if (!_services.TryGetValue(type, out var list)) return Enumerable.Empty<T>();
            return list.Select(d => ResolveService(d) as T).Where(s => s != null);
        }

        private ServiceDescriptor GetTopDescriptor(Type serviceType)
        {
            if (!_services.TryGetValue(serviceType, out var list) || list.Count == 0)
                return null;
            return list[0]; // 最高优先级
        }

        private object ResolveService(ServiceDescriptor descriptor)
        {
            if (descriptor.Lifetime == ServiceLifetime.Singleton)
            {
                if (_singletons.TryGetValue(descriptor.ServiceType, out var cached))
                    return cached;

                var instance = CreateInstance(descriptor);
                if (instance != null)
                {
                    _singletons[descriptor.ServiceType] = instance;

                    // 初始化 IService
                    if (instance is IService service && !descriptor.IsInitialized)
                    {
                        service.Initialize();
                        descriptor.IsInitialized = true;
                    }
                }
                return instance;
            }

            if (descriptor.Lifetime == ServiceLifetime.Transient)
            {
                return CreateInstance(descriptor);
            }

            // Scoped (简化为单例)
            if (_singletons.TryGetValue(descriptor.ServiceType, out var scoped))
                return scoped;

            var scopedInstance = CreateInstance(descriptor);
            _singletons[descriptor.ServiceType] = scopedInstance;
            return scopedInstance;
        }

        private object CreateInstance(ServiceDescriptor descriptor)
        {
            object instance;

            // 1. 预创建实例
            if (descriptor.Instance != null)
            {
                instance = descriptor.Instance;
            }
            // 2. 工厂
            else if (descriptor.Factory != null)
            {
                instance = descriptor.Factory(this);
            }
            // 3. 构造函数注入
            else if (descriptor.ImplementationType != null)
            {
                instance = CreateWithConstructorInjection(descriptor.ImplementationType);
            }
            else
            {
                return null;
            }

            // 属性注入
            if (EnablePropertyInjection && instance != null)
                InjectProperties(instance);

            // 方法注入
            if (EnableMethodInjection && instance != null)
                InjectMethods(instance);

            return instance;
        }

        #endregion

        #region 依赖注入

        /// <summary>构造函数注入。</summary>
        private object CreateWithConstructorInjection(Type implementationType)
        {
            var constructors = implementationType.GetConstructors()
                .OrderByDescending(c => c.GetParameters().Length)
                .ToArray();

            foreach (var ctor in constructors)
            {
                var parameters = ctor.GetParameters();
                var args = new object[parameters.Length];
                bool canResolve = true;

                for (int i = 0; i < parameters.Length; i++)
                {
                    var paramType = parameters[i].ParameterType;
                    var paramValue = GetService(paramType);
                    if (paramValue == null && !parameters[i].HasDefaultValue)
                    {
                        canResolve = false;
                        break;
                    }
                    args[i] = paramValue ?? parameters[i].DefaultValue;
                }

                if (canResolve)
                {
                    try
                    {
                        return ctor.Invoke(args);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning($"构造函数注入失败: {implementationType.Name}: {ex.Message}");
                    }
                }
            }

            // 尝试无参构造
            try
            {
                return Activator.CreateInstance(implementationType);
            }
            catch (Exception ex)
            {
                _logger.Error($"无法创建实例: {implementationType.Name}", ex);
                return null;
            }
        }

        /// <summary>属性注入。</summary>
        private void InjectProperties(object instance)
        {
            var type = instance.GetType();
            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanWrite && p.GetIndexParameters().Length == 0);

            foreach (var prop in properties)
            {
                // 检查是否已赋值
                var currentValue = prop.GetValue(instance);
                if (currentValue != null) continue;

                var service = GetService(prop.PropertyType);
                if (service != null)
                {
                    try
                    {
                        prop.SetValue(instance, service);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning($"属性注入失败: {type.Name}.{prop.Name}: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>方法注入。</summary>
        private void InjectMethods(object instance)
        {
            var type = instance.GetType();
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == "Inject" || m.GetCustomAttribute<InjectAttribute>() != null);

            foreach (var method in methods)
            {
                var parameters = method.GetParameters();
                var args = new object[parameters.Length];
                bool canResolve = true;

                for (int i = 0; i < parameters.Length; i++)
                {
                    args[i] = GetService(parameters[i].ParameterType);
                    if (args[i] == null && !parameters[i].HasDefaultValue)
                    {
                        canResolve = false;
                        break;
                    }
                }

                if (canResolve)
                {
                    try
                    {
                        method.Invoke(instance, args);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning($"方法注入失败: {type.Name}.{method.Name}: {ex.Message}");
                    }
                }
            }
        }

        #endregion

        #region 服务发现

        /// <summary>从程序集扫描并注册服务。</summary>
        public void RegisterFromAssembly(Assembly assembly, string pluginId, int priority = 100)
        {
            var types = assembly.GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract && typeof(IService).IsAssignableFrom(t));

            foreach (var type in types)
            {
                // 查找该类型实现的 IService<T> 接口
                var serviceInterfaces = type.GetInterfaces()
                    .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IService<>));

                if (serviceInterfaces.Any())
                {
                    foreach (var iface in serviceInterfaces)
                    {
                        RegisterDescriptor(new ServiceDescriptor
                        {
                            ServiceType = iface,
                            ImplementationType = type,
                            Lifetime = ServiceLifetime.Singleton,
                            PluginId = pluginId,
                            Priority = priority
                        });
                    }
                }
                else if (typeof(IService).IsAssignableFrom(type))
                {
                    RegisterDescriptor(new ServiceDescriptor
                    {
                        ServiceType = type,
                        ImplementationType = type,
                        Lifetime = ServiceLifetime.Singleton,
                        PluginId = pluginId,
                        Priority = priority
                    });
                }
            }

            _logger.Info($"从程序集 {assembly.GetName().Name} 注册服务 [{pluginId}]");
        }

        /// <summary>获取所有已注册的服务类型。</summary>
        public List<Type> GetRegisteredServiceTypes()
        {
            return _services.Keys.ToList();
        }

        /// <summary>获取服务的所有描述符。</summary>
        public List<ServiceDescriptor> GetDescriptors(Type serviceType)
        {
            return _services.TryGetValue(serviceType, out var list) ? list.ToList() : new List<ServiceDescriptor>();
        }

        /// <summary>服务是否已注册。</summary>
        public bool IsRegistered(Type serviceType)
        {
            return _services.ContainsKey(serviceType) && _services[serviceType].Count > 0;
        }

        /// <summary>服务是否已注册。</summary>
        public bool IsRegistered<TService>() where TService : class
        {
            return IsRegistered(typeof(TService));
        }

        #endregion

        #region 服务依赖

        /// <summary>注册服务依赖。</summary>
        public void RegisterDependency(Type dependent, Type dependency)
        {
            if (!_dependencyGraph.ContainsKey(dependent))
                _dependencyGraph[dependent] = new List<Type>();
            if (!_dependencyGraph[dependent].Contains(dependency))
                _dependencyGraph[dependent].Add(dependency);
        }

        /// <summary>检测循环依赖。</summary>
        public List<List<Type>> DetectCircularDependencies()
        {
            var cycles = new List<List<Type>>();
            var visited = new HashSet<Type>();
            var path = new List<Type>();
            var inPath = new HashSet<Type>();

            void Visit(Type type)
            {
                if (inPath.Contains(type))
                {
                    int idx = path.IndexOf(type);
                    var cycle = path.Skip(idx).ToList();
                    cycle.Add(type);
                    cycles.Add(cycle);
                    return;
                }
                if (visited.Contains(type)) return;

                inPath.Add(type);
                path.Add(type);

                if (_dependencyGraph.TryGetValue(type, out var deps))
                {
                    foreach (var dep in deps)
                        Visit(dep);
                }

                path.RemoveAt(path.Count - 1);
                inPath.Remove(type);
                visited.Add(type);
            }

            foreach (var type in _dependencyGraph.Keys)
                Visit(type);

            return cycles;
        }

        /// <summary>获取服务依赖的初始化顺序 (拓扑排序)。</summary>
        public List<Type> GetInitializationOrder()
        {
            var sorted = new List<Type>();
            var visited = new HashSet<Type>();
            var visiting = new HashSet<Type>();

            void Visit(Type type)
            {
                if (visited.Contains(type)) return;
                if (visiting.Contains(type)) return; // 循环，跳过

                visiting.Add(type);
                if (_dependencyGraph.TryGetValue(type, out var deps))
                {
                    foreach (var dep in deps)
                        Visit(dep);
                }
                visiting.Remove(type);
                visited.Add(type);
                sorted.Add(type);
            }

            foreach (var type in _services.Keys)
                Visit(type);

            return sorted;
        }

        #endregion

        #region 服务覆盖

        /// <summary>获取服务覆盖记录。</summary>
        public IReadOnlyList<ServiceOverrideRecord> GetOverrideRecords() => _overrides.AsReadOnly();

        /// <summary>获取服务的提供者插件。</summary>
        public string GetServiceProvider(Type serviceType)
        {
            var desc = GetTopDescriptor(serviceType);
            return desc?.PluginId;
        }

        #endregion

        #region 关闭

        /// <summary>关闭所有服务。</summary>
        public void ShutdownAll()
        {
            foreach (var kv in _singletons)
            {
                if (kv.Value is IService service)
                {
                    try { service.Shutdown(); }
                    catch (Exception ex) { _logger.Error($"关闭服务失败: {kv.Key.Name}", ex); }
                }
            }
            _singletons.Clear();
            _services.Clear();
            _dependencyGraph.Clear();
        }

        #endregion
    }

    #endregion

    #region 注入标记特性

    /// <summary>标记需要方法注入的方法。</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class InjectAttribute : Attribute
    {
    }

    #endregion
}
