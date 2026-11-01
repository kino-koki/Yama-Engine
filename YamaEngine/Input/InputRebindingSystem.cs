using System;
using System.Collections.Generic;

namespace YamaEngine.Input.Enhanced
{
    #region ControlScheme —— 控制方案

    /// <summary>
    /// 控制方案 —— 定义一套绑定配置，适用于特定输入设备组合
    /// 类似 Unity 的 Control Scheme 或 UE 的 Input Mapping
    /// 例如: "KeyboardMouse", "Gamepad", "Touch", "VR"
    /// </summary>
    public class ControlScheme
    {
        /// <summary>方案名称</summary>
        public string Name { get; set; }

        /// <summary>显示名称</summary>
        public string DisplayName { get; set; }

        /// <summary>方案描述</summary>
        public string Description { get; set; } = "";

        /// <summary>需要的设备类型列表 (如 "Keyboard", "Mouse", "Gamepad")</summary>
        public List<string> RequiredDeviceTypes { get; set; } = new List<string>();

        /// <summary>此方案的绑定覆盖列表</summary>
        public List<BindingOverride> BindingOverrides { get; set; } = new List<BindingOverride>();

        /// <summary>是否为默认方案</summary>
        public bool IsDefault { get; set; }

        public ControlScheme(string name, string displayName = null)
        {
            Name = name;
            DisplayName = displayName ?? name;
        }

        /// <summary>添加需要的设备类型</summary>
        public ControlScheme AddRequiredDevice(string deviceType)
        {
            if (!RequiredDeviceTypes.Contains(deviceType))
                RequiredDeviceTypes.Add(deviceType);
            return this;
        }

        /// <summary>添加绑定覆盖</summary>
        public ControlScheme AddBindingOverride(BindingOverride bindingOverride)
        {
            BindingOverrides.Add(bindingOverride);
            return this;
        }

        /// <summary>移除绑定覆盖</summary>
        public bool RemoveBindingOverride(BindingOverride bindingOverride) => BindingOverrides.Remove(bindingOverride);

        /// <summary>查找指定动作的覆盖绑定</summary>
        public BindingOverride FindOverride(string actionMap, string actionName)
        {
            foreach (var o in BindingOverrides)
            {
                if (o.ActionMap == actionMap && o.Action == actionName)
                    return o;
            }
            return null;
        }
    }

    #endregion

    #region InputRebinding —— 重绑定操作

    /// <summary>
    /// 输入重绑定操作 —— 记录一次绑定更改
    /// </summary>
    public class InputRebinding
    {
        /// <summary>目标动作</summary>
        public EnhancedInputAction Action { get; set; }

        /// <summary>目标绑定索引</summary>
        public int BindingIndex { get; set; }

        /// <summary>新绑定路径</summary>
        public string NewBindingPath { get; set; }

        /// <summary>旧绑定路径 (备份)</summary>
        public string OldBindingPath { get; set; }

        /// <summary>适用的控制方案 (null = 全局)</summary>
        public string ControlScheme { get; set; }

        /// <summary>重绑定是否已完成</summary>
        public bool IsComplete { get; private set; }

        /// <summary>完成时间</summary>
        public float CompleteTime { get; private set; }

        /// <summary>完成回调</summary>
        public event Action<InputRebinding> OnComplete;

        public InputRebinding(EnhancedInputAction action, int bindingIndex, string newBindingPath, string controlScheme = null)
        {
            Action = action;
            BindingIndex = bindingIndex;
            NewBindingPath = newBindingPath;
            ControlScheme = controlScheme;
        }

        /// <summary>执行重绑定</summary>
        public void Apply()
        {
            if (Action != null && Action.Bindings.Count > BindingIndex)
            {
                var binding = Action.Bindings[BindingIndex];
                OldBindingPath = binding.Path;
                binding.Path = NewBindingPath;
                IsComplete = true;
                OnComplete?.Invoke(this);
            }
        }

        /// <summary>撤销重绑定 (恢复原绑定)</summary>
        public void Revert()
        {
            if (Action != null && !string.IsNullOrEmpty(OldBindingPath) && Action.Bindings.Count > BindingIndex)
            {
                Action.Bindings[BindingIndex].Path = OldBindingPath;
                IsComplete = false;
            }
        }
    }

    #endregion

    #region InputRebindingContext —— 重绑定上下文

    /// <summary>
    /// 输入重绑定上下文 —— 管理重绑定操作的完整生命周期
    /// 包括等待用户输入、超时、取消等
    /// </summary>
    public class InputRebindingContext
    {
        /// <summary>重绑定状态</summary>
        public enum RebindingState
        {
            /// <summary>空闲</summary>
            Idle,
            /// <summary>等待用户输入</summary>
            WaitingForInput,
            /// <summary>已完成</summary>
            Completed,
            /// <summary>已取消</summary>
            Canceled,
            /// <summary>超时</summary>
            TimedOut
        }

        /// <summary>当前状态</summary>
        public RebindingState State { get; private set; } = RebindingState.Idle;

        /// <summary>目标动作</summary>
        public EnhancedInputAction TargetAction { get; private set; }

        /// <summary>目标绑定索引 (-1 = 自动选择)</summary>
        public int TargetBindingIndex { get; private set; } = -1;

        /// <summary>超时时间 (秒, 0 = 无超时)</summary>
        public float Timeout { get; set; } = 10f;

        /// <summary>已等待时间</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public float ElapsedTime { get; private set; }

        /// <summary>要排除的设备类型 (重绑定时忽略这些设备的输入)</summary>
        public List<string> ExcludedDevices { get; set; } = new List<string>();

        /// <summary>要排除的绑定路径</summary>
        public List<string> ExcludedPaths { get; set; } = new List<string>();

        /// <summary>适用的控制方案</summary>
        public string ControlScheme { get; set; }

        /// <summary>重绑定完成时的回调</summary>
        public event Action<InputRebinding, string> OnRebound;
        /// <summary>重绑定取消时的回调</summary>
        public event Action OnCanceled;
        /// <summary>重绑定超时时的回调</summary>
        public event Action OnTimedOut;

        private InputRebinding _currentRebinding;

        /// <summary>开始重绑定操作</summary>
        public void Start(EnhancedInputAction action, int bindingIndex = -1)
        {
            TargetAction = action;
            TargetBindingIndex = bindingIndex < 0 ? 0 : bindingIndex;
            State = RebindingState.WaitingForInput;
            ElapsedTime = 0f;
        }

        /// <summary>完成重绑定 (由外部调用，传入检测到的新绑定路径)</summary>
        public void Complete(string newBindingPath)
        {
            if (State != RebindingState.WaitingForInput) return;

            _currentRebinding = new InputRebinding(TargetAction, TargetBindingIndex, newBindingPath, ControlScheme);
            _currentRebinding.Apply();
            State = RebindingState.Completed;
            OnRebound?.Invoke(_currentRebinding, newBindingPath);
        }

        /// <summary>取消重绑定</summary>
        public void Cancel()
        {
            State = RebindingState.Canceled;
            TargetAction = null;
            OnCanceled?.Invoke();
        }

        /// <summary>每帧更新 (用于超时检测)</summary>
        public void Update(float deltaTime)
        {
            if (State == RebindingState.WaitingForInput)
            {
                ElapsedTime += deltaTime;
                if (Timeout > 0f && ElapsedTime >= Timeout)
                {
                    State = RebindingState.TimedOut;
                    TargetAction = null;
                    OnTimedOut?.Invoke();
                }
            }
        }

        /// <summary>获取当前重绑定操作</summary>
        public InputRebinding GetCurrentRebinding() => _currentRebinding;
    }

    #endregion

    #region RebindingUI 接口

    /// <summary>
    /// 重绑定 UI 接口 —— 定义重绑定过程中的 UI 交互
    /// 实现此接口来创建自定义的重绑定 UI (如按钮提示、进度显示)
    /// </summary>
    public interface IRebindingUI
    {
        /// <summary>显示重绑定提示 (告诉用户正在等待输入)</summary>
        void ShowPrompt(string actionName, string currentBinding);

        /// <summary>更新进度</summary>
        void UpdateProgress(float elapsedTime, float timeout);

        /// <summary>显示重绑定完成</summary>
        void ShowComplete(string actionName, string newBinding);

        /// <summary>显示重绑定取消</summary>
        void ShowCanceled(string actionName);

        /// <summary>显示超时</summary>
        void ShowTimeout(string actionName);

        /// <summary>隐藏 UI</summary>
        void Hide();
    }

    /// <summary>
    /// 默认重绑定 UI 实现 (基于日志/控制台输出)
    /// </summary>
    public class DefaultRebindingUI : IRebindingUI
    {
        public void ShowPrompt(string actionName, string currentBinding)
        {
            Console.WriteLine($"[Rebinding] 等待输入... 动作: {actionName} (当前: {currentBinding})");
        }

        public void UpdateProgress(float elapsedTime, float timeout)
        {
            // 静默更新，不输出
        }

        public void ShowComplete(string actionName, string newBinding)
        {
            Console.WriteLine($"[Rebinding] 完成! 动作: {actionName} → {newBinding}");
        }

        public void ShowCanceled(string actionName)
        {
            Console.WriteLine($"[Rebinding] 已取消: {actionName}");
        }

        public void ShowTimeout(string actionName)
        {
            Console.WriteLine($"[Rebinding] 超时: {actionName}");
        }

        public void Hide() { }
    }

    #endregion

    #region InputRebindingManager —— 重绑定管理器

    /// <summary>
    /// 输入重绑定管理器 —— 管理所有重绑定操作、方案切换、存储/加载
    /// </summary>
    public class InputRebindingManager
    {
        private static InputRebindingManager _instance;
        /// <summary>全局单例</summary>
        public static InputRebindingManager Instance
        {
            get
            {
                if (_instance == null) _instance = new InputRebindingManager();
                return _instance;
            }
        }

        /// <summary>所有控制方案</summary>
        public Dictionary<string, ControlScheme> ControlSchemes { get; } = new Dictionary<string, ControlScheme>();

        /// <summary>当前活动方案名称</summary>
        public string ActiveScheme { get; private set; }

        /// <summary>所有重绑定历史 (用于撤销)</summary>
        private readonly List<InputRebinding> _rebindingHistory = new List<InputRebinding>();

        /// <summary>当前重绑定上下文</summary>
        public InputRebindingContext CurrentContext { get; private set; }

        /// <summary>重绑定 UI</summary>
        public IRebindingUI UI { get; set; } = new DefaultRebindingUI();

        /// <summary>方案切换事件</summary>
        public event Action<string, string> OnSchemeChanged;
        /// <summary>重绑定完成事件</summary>
        public event Action<InputRebinding> OnRebindingCompleted;

        public InputRebindingManager() { }

        /// <summary>添加控制方案</summary>
        public void AddControlScheme(ControlScheme scheme)
        {
            ControlSchemes[scheme.Name] = scheme;
        }

        /// <summary>移除控制方案</summary>
        public bool RemoveControlScheme(string name) => ControlSchemes.Remove(name);

        /// <summary>切换控制方案</summary>
        public void SwitchScheme(string schemeName)
        {
            if (!ControlSchemes.ContainsKey(schemeName))
                throw new ArgumentException($"控制方案 '{schemeName}' 不存在");

            string oldScheme = ActiveScheme;
            ActiveScheme = schemeName;
            OnSchemeChanged?.Invoke(oldScheme, schemeName);

            // 应用方案的绑定覆盖
            ApplySchemeOverrides(schemeName);
        }

        /// <summary>应用方案的绑定覆盖</summary>
        private void ApplySchemeOverrides(string schemeName)
        {
            if (!ControlSchemes.TryGetValue(schemeName, out var scheme)) return;

            // 由 InputActionAssetSerialization 在加载时处理
            // 这里只触发事件通知
        }

        /// <summary>开始重绑定操作</summary>
        public InputRebindingContext StartRebinding(EnhancedInputAction action, int bindingIndex = -1, float timeout = 10f)
        {
            CurrentContext = new InputRebindingContext
            {
                Timeout = timeout,
                ControlScheme = ActiveScheme
            };
            CurrentContext.Start(action, bindingIndex);

            // 绑定 UI 回调
            CurrentContext.OnRebound += (rebinding, path) =>
            {
                _rebindingHistory.Add(rebinding);
                UI?.ShowComplete(action.Name, path);
                OnRebindingCompleted?.Invoke(rebinding);
            };
            CurrentContext.OnCanceled += () => UI?.ShowCanceled(action.Name);
            CurrentContext.OnTimedOut += () => UI?.ShowTimeout(action.Name);

            UI?.ShowPrompt(action.Name, action.Bindings.Count > bindingIndex ? action.Bindings[Math.Max(0, bindingIndex)].Path : "N/A");
            return CurrentContext;
        }

        /// <summary>取消当前重绑定</summary>
        public void CancelRebinding()
        {
            CurrentContext?.Cancel();
            CurrentContext = null;
            UI?.Hide();
        }

        /// <summary>撤销最后一次重绑定</summary>
        public bool UndoLastRebinding()
        {
            if (_rebindingHistory.Count == 0) return false;

            var last = _rebindingHistory[_rebindingHistory.Count - 1];
            last.Revert();
            _rebindingHistory.RemoveAt(_rebindingHistory.Count - 1);
            return true;
        }

        /// <summary>获取所有重绑定记录 (用于保存)</summary>
        public IReadOnlyList<InputRebinding> GetRebindingHistory() => _rebindingHistory.AsReadOnly();

        /// <summary>恢复默认方案绑定 (撤销所有自定义重绑定)</summary>
        public void RestoreDefaults()
        {
            for (int i = _rebindingHistory.Count - 1; i >= 0; i--)
            {
                _rebindingHistory[i].Revert();
            }
            _rebindingHistory.Clear();
        }

        /// <summary>更新 (处理超时)</summary>
        public void Update(float deltaTime)
        {
            CurrentContext?.Update(deltaTime);
        }

        /// <summary>获取所有方案名称</summary>
        public IEnumerable<string> GetSchemeNames() => ControlSchemes.Keys;

        /// <summary>获取当前活动方案</summary>
        public ControlScheme GetActiveScheme()
        {
            return string.IsNullOrEmpty(ActiveScheme) ? null : ControlSchemes.TryGetValue(ActiveScheme, out var s) ? s : null;
        }

        /// <summary>获取默认方案</summary>
        public ControlScheme GetDefaultScheme()
        {
            foreach (var scheme in ControlSchemes.Values)
            {
                if (scheme.IsDefault) return scheme;
            }
            return ControlSchemes.Count > 0 ? ControlSchemes.Values.GetEnumerator().Current : null;
        }
    }

    #endregion
}
