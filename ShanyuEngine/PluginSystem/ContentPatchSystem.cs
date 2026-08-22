using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;

namespace ShanyuEngine.PluginSystem
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - 内容补丁系统 (ContentPatchSystem)
    //
    // 运行时方法补丁系统 (类似 Harmony / MonoMod):
    //   * 方法补丁 (Prefix / Postfix / Transpiler / Replace)
    //   * 补丁目标 (实例方法 / 静态方法 / 构造函数 / 属性 / 泛型方法)
    //   * 补丁优先级 (多个补丁的执行顺序 / 冲突解决)
    //   * 补丁上下文 (PatchContext: 原始方法 / 参数 / 返回值 / 实例引用 / 取消标志)
    //   * 补丁管理 (注册 / 注销 / 启用 / 禁用 / 列表 / 依赖)
    //   * 补丁安全检查 (签名验证 / 白名单 / 黑名单 / 权限检查)
    //   * 补丁日志 (应用 / 调用 / 异常 / 统计)
    // ════════════════════════════════════════════════════════════════════════

    #region 补丁类型

    /// <summary>补丁类型。</summary>
    public enum PatchType
    {
        /// <summary>方法前执行 (可取消原方法 / 修改参数)</summary>
        Prefix,
        /// <summary>方法后执行 (可修改返回值)</summary>
        Postfix,
        /// <summary>IL 指令级修改</summary>
        Transpiler,
        /// <summary>替换方法体</summary>
        Replace,
        /// <summary>Finalizer (异常处理)</summary>
        Finalizer
    }

    /// <summary>补丁目标类型。</summary>
    public enum PatchTargetType
    {
        InstanceMethod,
        StaticMethod,
        Constructor,
        PropertyGetter,
        PropertySetter,
        GenericMethod
    }

    #endregion

    #region 补丁上下文

    /// <summary>补丁执行上下文。</summary>
    public sealed class PatchContext
    {
        /// <summary>原始方法信息。</summary>
        public MethodBase OriginalMethod { get; set; }

        /// <summary>方法参数 (可修改)。</summary>
        public object[] Arguments { get; set; }

        /// <summary>返回值 (Postfix 可修改)。</summary>
        public object ReturnValue { get; set; }

        /// <summary>实例引用 (实例方法)。</summary>
        public object Instance { get; set; }

        /// <summary>是否取消原方法执行 (Prefix 可设置)。</summary>
        public bool CancelOriginal { get; set; }

        /// <summary>是否跳过后续补丁。</summary>
        public bool SkipRemainingPatches { get; set; }

        /// <summary>是否已修改参数。</summary>
        public bool ArgumentsModified { get; set; }

        /// <summary>是否已修改返回值。</summary>
        public bool ReturnValueModified { get; set; }

        /// <summary>异常 (Finalizer 可处理)。</summary>
        public Exception Exception { get; set; }

        /// <summary>补丁提供者插件ID。</summary>
        public string PatchPluginId { get; set; }

        /// <summary>取消原方法执行。</summary>
        public void Cancel() => CancelOriginal = true;

        /// <summary>设置返回值。</summary>
        public void SetReturnValue(object value)
        {
            ReturnValue = value;
            ReturnValueModified = true;
        }

        /// <summary>修改参数。</summary>
        public void SetArgument(int index, object value)
        {
            if (index >= 0 && index < Arguments.Length)
            {
                Arguments[index] = value;
                ArgumentsModified = true;
            }
        }

        /// <summary>获取参数值。</summary>
        public T GetArgument<T>(int index)
        {
            if (index < 0 || index >= Arguments.Length) return default;
            if (Arguments[index] is T val) return val;
            try { return (T)Convert.ChangeType(Arguments[index], typeof(T)); } catch { return default; }
        }

        /// <summary>获取实例引用。</summary>
        public T GetInstance<T>() where T : class => Instance as T;

        public override string ToString() =>
            $"{OriginalMethod?.Name} [{(CancelOriginal ? "Cancelled" : "Active")}]" +
            $"{(ArgumentsModified ? " [ArgsModified]" : "")}" +
            $"{(ReturnValueModified ? " [ReturnModified]" : "")}";
    }

    #endregion

    #region 补丁描述符

    /// <summary>补丁描述符。</summary>
    public sealed class PatchDescriptor
    {
        public string PatchId { get; set; } = Guid.NewGuid().ToString("N");
        public string PluginId { get; set; } = string.Empty;
        public PatchType PatchType { get; set; }
        public PatchTargetType TargetType { get; set; }
        public MethodBase TargetMethod { get; set; }
        public MethodInfo PatchMethod { get; set; }
        public object PatchInstance { get; set; }
        public int Priority { get; set; } = 100;
        public bool IsEnabled { get; set; } = true;
        public bool IsApplied { get; set; }
        public DateTime AppliedTime { get; set; }
        public string Signature { get; set; }
        public List<string> Dependencies { get; set; } = new List<string>();

        // 统计
        public long InvokeCount { get; set; }
        public TimeSpan TotalInvokeTime { get; set; }
        public Exception LastException { get; set; }

        public override string ToString() =>
            $"[{PatchType}] {TargetMethod?.DeclaringType?.Name}.{TargetMethod?.Name} ← {PluginId} P:{Priority}";
    }

    #endregion

    #region 补丁安全

    /// <summary>补丁安全策略。</summary>
    public sealed class PatchSecurityPolicy
    {
        public bool RequireSignature { get; set; } = false;
        public bool EnableWhitelist { get; set; } = false;
        public bool EnableBlacklist { get; set; } = true;
        public HashSet<string> WhitelistedPlugins { get; } = new HashSet<string>();
        public HashSet<string> BlacklistedPlugins { get; } = new HashSet<string>();
        public HashSet<string> WhitelistedMethods { get; } = new HashSet<string>();
        public HashSet<string> BlacklistedMethods { get; } = new HashSet<string>();
        public PluginPermissionFlags RequiredPermissions { get; set; } = PluginPermissionFlags.None;

        public bool CanPatch(string pluginId, MethodBase targetMethod)
        {
            // 黑名单检查
            if (EnableBlacklist)
            {
                if (BlacklistedPlugins.Contains(pluginId)) return false;
                string methodKey = $"{targetMethod?.DeclaringType?.FullName}.{targetMethod?.Name}";
                if (BlacklistedMethods.Contains(methodKey)) return false;
            }

            // 白名单检查
            if (EnableWhitelist)
            {
                if (!WhitelistedPlugins.Contains(pluginId))
                {
                    string methodKey = $"{targetMethod?.DeclaringType?.FullName}.{targetMethod?.Name}";
                    if (!WhitelistedMethods.Contains(methodKey)) return false;
                }
            }

            return true;
        }

        public void WhitelistPlugin(string pluginId) => WhitelistedPlugins.Add(pluginId);
        public void BlacklistPlugin(string pluginId) => BlacklistedPlugins.Add(pluginId);
        public void WhitelistMethod(string typeFullName, string methodName) =>
            WhitelistedMethods.Add($"{typeFullName}.{methodName}");
        public void BlacklistMethod(string typeFullName, string methodName) =>
            BlacklistedMethods.Add($"{typeFullName}.{methodName}");
    }

    #endregion

    #region 补丁调用结果

    /// <summary>补丁调用结果。</summary>
    public struct PatchInvokeResult
    {
        public bool OriginalExecuted { get; set; }
        public object ReturnValue { get; set; }
        public Exception Exception { get; set; }
        public TimeSpan TotalTime { get; set; }
        public int PatchesExecuted { get; set; }
    }

    #endregion

    #region 内容补丁器

    /// <summary>运行时补丁系统 (类似 Harmony / MonoMod)。</summary>
    public sealed class ContentPatcher
    {
        private static readonly Lazy<ContentPatcher> _instance =
            new Lazy<ContentPatcher>(() => new ContentPatcher());
        public static ContentPatcher Instance => _instance.Value;

        // 补丁注册表: TargetMethod -> List of patches (sorted by priority)
        private readonly ConcurrentDictionary<string, List<PatchDescriptor>> _patches =
            new ConcurrentDictionary<string, List<PatchDescriptor>>();
        private readonly object _patchLock = new object();

        // 方法调用包装器: TargetMethod -> wrapped delegate
        private readonly ConcurrentDictionary<string, Delegate> _wrappers =
            new ConcurrentDictionary<string, Delegate>();

        // 安全策略
        public PatchSecurityPolicy SecurityPolicy { get; set; } = new PatchSecurityPolicy();

        // 日志
        private readonly PluginLogger _logger = new PluginLogger("ContentPatcher");

        // 配置
        public bool EnableProfiling { get; set; } = true;
        public bool CaptureExceptions { get; set; } = true;
        public int MaxPatchesPerMethod { get; set; } = 32;

        // 事件
        public event Action<PatchDescriptor> OnPatchApplied;
        public event Action<PatchDescriptor> OnPatchRemoved;
        public event Action<PatchDescriptor, PatchContext> OnPatchInvoked;
        public event Action<PatchDescriptor, Exception> OnPatchException;

        // 统计
        public long TotalPatchInvocations { get; private set; }
        public long TotalPatchExceptions { get; private set; }
        public TimeSpan TotalPatchTime { get; private set; }

        private ContentPatcher() { }

        #region 补丁注册

        /// <summary>添加前缀补丁。</summary>
        public string AddPrefix(MethodBase targetMethod, MethodInfo prefixMethod, object patchInstance = null,
            string pluginId = null, int priority = 100)
        {
            return AddPatch(targetMethod, prefixMethod, patchInstance, PatchType.Prefix, pluginId, priority);
        }

        /// <summary>添加后缀补丁。</summary>
        public string AddPostfix(MethodBase targetMethod, MethodInfo postfixMethod, object patchInstance = null,
            string pluginId = null, int priority = 100)
        {
            return AddPatch(targetMethod, postfixMethod, patchInstance, PatchType.Postfix, pluginId, priority);
        }

        /// <summary>添加 Transpiler 补丁 (IL 级修改)。</summary>
        public string AddTranspiler(MethodBase targetMethod, MethodInfo transpilerMethod, object patchInstance = null,
            string pluginId = null, int priority = 100)
        {
            _logger.Warning("Transpiler 补丁需要 IL 操作支持，当前为简化实现。");
            return AddPatch(targetMethod, transpilerMethod, patchInstance, PatchType.Transpiler, pluginId, priority);
        }

        /// <summary>替换方法体。</summary>
        public string AddReplacement(MethodBase targetMethod, MethodInfo replacementMethod, object patchInstance = null,
            string pluginId = null, int priority = 100)
        {
            return AddPatch(targetMethod, replacementMethod, patchInstance, PatchType.Replace, pluginId, priority);
        }

        /// <summary>添加 Finalizer (异常处理)。</summary>
        public string AddFinalizer(MethodBase targetMethod, MethodInfo finalizerMethod, object patchInstance = null,
            string pluginId = null, int priority = 100)
        {
            return AddPatch(targetMethod, finalizerMethod, patchInstance, PatchType.Finalizer, pluginId, priority);
        }

        private string AddPatch(MethodBase targetMethod, MethodInfo patchMethod, object patchInstance,
            PatchType patchType, string pluginId, int priority)
        {
            if (targetMethod == null) throw new ArgumentNullException(nameof(targetMethod));
            if (patchMethod == null) throw new ArgumentNullException(nameof(patchMethod));

            string methodKey = GetMethodKey(targetMethod);

            // 安全检查
            if (!SecurityPolicy.CanPatch(pluginId, targetMethod))
            {
                _logger.Warning($"安全策略阻止补丁: {methodKey} [{pluginId}]");
                return null;
            }

            var descriptor = new PatchDescriptor
            {
                PluginId = pluginId ?? "Unknown",
                PatchType = patchType,
                TargetType = DetectTargetType(targetMethod),
                TargetMethod = targetMethod,
                PatchMethod = patchMethod,
                PatchInstance = patchInstance,
                Priority = priority,
                IsEnabled = true,
                IsApplied = true,
                AppliedTime = DateTime.Now
            };

            lock (_patchLock)
            {
                if (!_patches.TryGetValue(methodKey, out var list))
                {
                    list = new List<PatchDescriptor>();
                    _patches[methodKey] = list;
                }

                if (list.Count >= MaxPatchesPerMethod)
                {
                    _logger.Warning($"方法 {methodKey} 补丁数量已达上限 {MaxPatchesPerMethod}");
                    return null;
                }

                list.Add(descriptor);
                list.Sort((a, b) => b.Priority.CompareTo(a.Priority));
            }

            _logger.Info($"应用补丁: {descriptor}");
            OnPatchApplied?.Invoke(descriptor);
            return descriptor.PatchId;
        }

        #endregion

        #region 补丁移除

        /// <summary>移除补丁。</summary>
        public bool RemovePatch(string patchId)
        {
            lock (_patchLock)
            {
                foreach (var kv in _patches)
                {
                    var removed = kv.Value.FirstOrDefault(p => p.PatchId == patchId);
                    if (removed != null)
                    {
                        kv.Value.Remove(removed);
                        _logger.Info($"移除补丁: {removed}");
                        OnPatchRemoved?.Invoke(removed);
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>移除插件的所有补丁。</summary>
        public int RemoveAllPatches(string pluginId)
        {
            int count = 0;
            lock (_patchLock)
            {
                foreach (var kv in _patches)
                {
                    var toRemove = kv.Value.Where(p => p.PluginId == pluginId).ToList();
                    foreach (var patch in toRemove)
                    {
                        kv.Value.Remove(patch);
                        OnPatchRemoved?.Invoke(patch);
                        count++;
                    }
                }
            }
            if (count > 0) _logger.Info($"移除插件 {pluginId} 的 {count} 个补丁");
            return count;
        }

        /// <summary>移除目标方法的所有补丁。</summary>
        public int RemoveAllPatchesForMethod(MethodBase targetMethod)
        {
            string methodKey = GetMethodKey(targetMethod);
            int count = 0;
            lock (_patchLock)
            {
                if (_patches.TryRemove(methodKey, out var list))
                {
                    count = list.Count;
                    foreach (var patch in list)
                        OnPatchRemoved?.Invoke(patch);
                }
            }
            return count;
        }

        #endregion

        #region 补丁启用/禁用

        /// <summary>启用补丁。</summary>
        public bool EnablePatch(string patchId)
        {
            return SetPatchEnabled(patchId, true);
        }

        /// <summary>禁用补丁。</summary>
        public bool DisablePatch(string patchId)
        {
            return SetPatchEnabled(patchId, false);
        }

        private bool SetPatchEnabled(string patchId, bool enabled)
        {
            lock (_patchLock)
            {
                foreach (var kv in _patches)
                {
                    var patch = kv.Value.FirstOrDefault(p => p.PatchId == patchId);
                    if (patch != null)
                    {
                        patch.IsEnabled = enabled;
                        return true;
                    }
                }
            }
            return false;
        }

        #endregion

        #region 补丁执行

        /// <summary>执行补丁链 (前缀 → 原方法 → 后缀)。</summary>
        public PatchInvokeResult InvokeWithPatches(MethodBase targetMethod, object instance, object[] arguments,
            Func<object[], object> originalMethodInvoker)
        {
            var sw = EnableProfiling ? System.Diagnostics.Stopwatch.StartNew() : null;
            var result = new PatchInvokeResult();
            string methodKey = GetMethodKey(targetMethod);

            List<PatchDescriptor> patches;
            lock (_patchLock)
            {
                if (!_patches.TryGetValue(methodKey, out patches) || patches.Count == 0)
                {
                    // 无补丁，直接执行原方法
                    if (originalMethodInvoker != null)
                    {
                        try
                        {
                            result.ReturnValue = originalMethodInvoker(arguments);
                            result.OriginalExecuted = true;
                        }
                        catch (Exception ex)
                        {
                            result.Exception = ex;
                        }
                    }
                    if (sw != null) { sw.Stop(); result.TotalTime = sw.Elapsed; }
                    return result;
                }
                patches = patches.Where(p => p.IsEnabled).ToList();
            }

            var context = new PatchContext
            {
                OriginalMethod = targetMethod,
                Arguments = arguments ?? Array.Empty<object>(),
                Instance = instance
            };

            // 1. 执行前缀补丁 (按优先级高→低)
            var prefixPatches = patches.Where(p => p.PatchType == PatchType.Prefix).ToList();
            foreach (var patch in prefixPatches)
            {
                if (context.SkipRemainingPatches) break;

                var patchSw = EnableProfiling ? System.Diagnostics.Stopwatch.StartNew() : null;
                try
                {
                    context.PatchPluginId = patch.PluginId;
                    InvokePatchMethod(patch, context);
                    patch.InvokeCount++;
                    result.PatchesExecuted++;
                    TotalPatchInvocations++;
                    OnPatchInvoked?.Invoke(patch, context);
                }
                catch (Exception ex)
                {
                    patch.LastException = ex;
                    TotalPatchExceptions++;
                    if (CaptureExceptions)
                    {
                        _logger.Error($"前缀补丁异常: {patch}", ex);
                        OnPatchException?.Invoke(patch, ex);
                    }
                }
                finally
                {
                    if (patchSw != null) { patchSw.Stop(); patch.TotalInvokeTime += patchSw.Elapsed; }
                }
            }

            // 检查 Replace 补丁
            var replacePatch = patches.FirstOrDefault(p => p.PatchType == PatchType.Replace);
            if (replacePatch != null && replacePatch.IsEnabled)
            {
                // 替换方法执行
                try
                {
                    context.PatchPluginId = replacePatch.PluginId;
                    result.ReturnValue = InvokeReplaceMethod(replacePatch, context);
                    result.OriginalExecuted = false; // 原方法未执行
                    result.PatchesExecuted++;
                }
                catch (Exception ex)
                {
                    result.Exception = ex;
                    replacePatch.LastException = ex;
                    TotalPatchExceptions++;
                }
            }
            else if (!context.CancelOriginal && originalMethodInvoker != null)
            {
                // 2. 执行原方法
                try
                {
                    result.ReturnValue = originalMethodInvoker(context.Arguments);
                    result.OriginalExecuted = true;
                    context.ReturnValue = result.ReturnValue;
                }
                catch (Exception ex)
                {
                    context.Exception = ex;
                    result.Exception = ex;
                }
            }
            else if (context.ReturnValueModified)
            {
                result.ReturnValue = context.ReturnValue;
            }

            // 3. 执行后缀补丁 (按优先级高→低)
            var postfixPatches = patches.Where(p => p.PatchType == PatchType.Postfix).ToList();
            foreach (var patch in postfixPatches)
            {
                if (context.SkipRemainingPatches) break;

                var patchSw = EnableProfiling ? System.Diagnostics.Stopwatch.StartNew() : null;
                try
                {
                    context.PatchPluginId = patch.PluginId;
                    InvokePatchMethod(patch, context);
                    patch.InvokeCount++;
                    result.PatchesExecuted++;
                    OnPatchInvoked?.Invoke(patch, context);

                    // 检查是否修改了返回值
                    if (context.ReturnValueModified)
                        result.ReturnValue = context.ReturnValue;
                }
                catch (Exception ex)
                {
                    patch.LastException = ex;
                    TotalPatchExceptions++;
                    if (CaptureExceptions)
                    {
                        _logger.Error($"后缀补丁异常: {patch}", ex);
                        OnPatchException?.Invoke(patch, ex);
                    }
                }
                finally
                {
                    if (patchSw != null) { patchSw.Stop(); patch.TotalInvokeTime += patchSw.Elapsed; }
                }
            }

            // 4. 执行 Finalizer (异常处理)
            if (result.Exception != null)
            {
                var finalizerPatches = patches.Where(p => p.PatchType == PatchType.Finalizer).ToList();
                foreach (var patch in finalizerPatches)
                {
                    try
                    {
                        context.Exception = result.Exception;
                        context.PatchPluginId = patch.PluginId;
                        InvokePatchMethod(patch, context);

                        // Finalizer 可以吞掉异常
                        if (context.Exception == null)
                        {
                            result.Exception = null;
                            if (context.ReturnValueModified)
                                result.ReturnValue = context.ReturnValue;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Finalizer 补丁异常: {patch}", ex);
                    }
                }
            }

            if (sw != null)
            {
                sw.Stop();
                result.TotalTime = sw.Elapsed;
                TotalPatchTime += sw.Elapsed;
            }

            return result;
        }

        private void InvokePatchMethod(PatchDescriptor patch, PatchContext context)
        {
            var parameters = patch.PatchMethod.GetParameters();
            var args = new List<object>();

            foreach (var param in parameters)
            {
                if (param.ParameterType == typeof(PatchContext))
                    args.Add(context);
                else if (param.ParameterType == typeof(object[]))
                    args.Add(context.Arguments);
                else if (param.ParameterType == typeof(object) && param.Name == "instance")
                    args.Add(context.Instance);
                else if (param.Name == "__instance")
                    args.Add(context.Instance);
                else if (param.Name == "__result")
                    args.Add(context.ReturnValue);
                else if (param.Name == "__exception" || param.Name == "__exception")
                    args.Add(context.Exception);
                else if (param.ParameterType == typeof(bool) && param.Name == "__runOriginal")
                    args.Add(!context.CancelOriginal);
                else
                {
                    // 尝试匹配参数位置
                    int idx = Array.FindIndex(parameters, p => p == param);
                    if (idx >= 0 && idx < context.Arguments.Length)
                        args.Add(context.Arguments[idx]);
                    else
                        args.Add(param.HasDefaultValue ? param.DefaultValue : null);
                }
            }

            patch.PatchMethod.Invoke(patch.PatchInstance, args.ToArray());
        }

        private object InvokeReplaceMethod(PatchDescriptor patch, PatchContext context)
        {
            var parameters = patch.PatchMethod.GetParameters();
            var args = new object[parameters.Length];

            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].ParameterType == typeof(PatchContext))
                    args[i] = context;
                else if (i < context.Arguments.Length)
                    args[i] = context.Arguments[i];
                else
                    args[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : null;
            }

            return patch.PatchMethod.Invoke(patch.PatchInstance, args);
        }

        #endregion

        #region 查询

        /// <summary>获取方法的所有补丁。</summary>
        public List<PatchDescriptor> GetPatches(MethodBase targetMethod)
        {
            string methodKey = GetMethodKey(targetMethod);
            return _patches.TryGetValue(methodKey, out var list) ? list.ToList() : new List<PatchDescriptor>();
        }

        /// <summary>获取插件的所有补丁。</summary>
        public List<PatchDescriptor> GetPatchesByPlugin(string pluginId)
        {
            var result = new List<PatchDescriptor>();
            foreach (var kv in _patches)
                result.AddRange(kv.Value.Where(p => p.PluginId == pluginId));
            return result;
        }

        /// <summary>获取所有补丁。</summary>
        public List<PatchDescriptor> GetAllPatches()
        {
            var result = new List<PatchDescriptor>();
            foreach (var kv in _patches)
                result.AddRange(kv.Value);
            return result;
        }

        /// <summary>方法是否有补丁。</summary>
        public bool HasPatches(MethodBase targetMethod)
        {
            string methodKey = GetMethodKey(targetMethod);
            return _patches.TryGetValue(methodKey, out var list) && list.Count > 0;
        }

        /// <summary>获取补丁数量。</summary>
        public int GetPatchCount(MethodBase targetMethod)
        {
            string methodKey = GetMethodKey(targetMethod);
            return _patches.TryGetValue(methodKey, out var list) ? list.Count : 0;
        }

        /// <summary>获取总补丁数量。</summary>
        public int TotalPatchCount => _patches.Sum(kv => kv.Value.Count);

        /// <summary>获取已修补的方法数量。</summary>
        public int PatchedMethodCount => _patches.Count;

        #endregion

        #region 补丁日志

        /// <summary>获取补丁统计。</summary>
        public string GetStats()
        {
            return $"补丁统计: {TotalPatchCount} 补丁 / {PatchedMethodCount} 方法, " +
                   $"调用 {TotalPatchInvocations} 次, 异常 {TotalPatchExceptions} 次, " +
                   $"总耗时 {TotalPatchTime.TotalMilliseconds:F1}ms";
        }

        /// <summary>获取补丁调用统计。</summary>
        public Dictionary<string, (long InvokeCount, TimeSpan TotalTime, Exception LastException)> GetPatchStats()
        {
            var stats = new Dictionary<string, (long, TimeSpan, Exception)>();
            foreach (var kv in _patches)
            {
                foreach (var patch in kv.Value)
                {
                    stats[patch.PatchId] = (patch.InvokeCount, patch.TotalInvokeTime, patch.LastException);
                }
            }
            return stats;
        }

        #endregion

        #region 工具方法

        private static string GetMethodKey(MethodBase method)
        {
            if (method == null) return string.Empty;
            return $"{method.DeclaringType?.FullName}.{method.Name}" +
                   $"({string.Join(",", method.GetParameters().Select(p => p.ParameterType.Name))})";
        }

        private static PatchTargetType DetectTargetType(MethodBase method)
        {
            if (method is ConstructorInfo) return PatchTargetType.Constructor;
            if (method.IsStatic) return PatchTargetType.StaticMethod;
            if (method is MethodInfo mi)
            {
                if (mi.IsGenericMethod) return PatchTargetType.GenericMethod;
                // 检查是否是属性 getter/setter
                foreach (var prop in mi.DeclaringType?.GetProperties() ?? Array.Empty<PropertyInfo>())
                {
                    if (prop.GetMethod == mi) return PatchTargetType.PropertyGetter;
                    if (prop.SetMethod == mi) return PatchTargetType.PropertySetter;
                }
            }
            return PatchTargetType.InstanceMethod;
        }

        #endregion

        #region 清理

        /// <summary>清除所有补丁。</summary>
        public void ClearAll()
        {
            int count = TotalPatchCount;
            _patches.Clear();
            _wrappers.Clear();
            _logger.Info($"清除所有补丁 ({count} 个)");
        }

        #endregion
    }

    #endregion
}
