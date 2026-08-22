using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace ShanyuEngine.Input
{
    // ====================================================================
    //  ShanyuEngine Real Input Rebinding System
    // ====================================================================
    //  真实输入重绑定系统：
    //    - InputRebindingReal: 重绑定流程（开始/取消/应用/保存/加载/重置）
    //    - ControlScheme: 控制方案（Keyboard&Mouse/Gamepad/Touch/SwitchPro等）
    //    - 绑定持久化（JSON序列化/按方案分组）
    //    - UI辅助（当前绑定/高亮/超时）
    //  所有方法包含完整算法实现。
    // ====================================================================

    #region 控制方案 - ControlScheme

    /// <summary>控制方案类型</summary>
    public enum ControlSchemeType
    {
        KeyboardAndMouse,
        Gamepad,
        Touch,
        SwitchPro,
        DualSense,
        DualShock,
        XboxController,
        SteeringWheel,
        FlightStick,
        Custom
    }

    /// <summary>控制方案 - 定义一组兼容的输入设备</summary>
    public class ControlScheme
    {
        /// <summary>方案名称</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>方案类型</summary>
        public ControlSchemeType Type { get; set; }

        /// <summary>兼容的设备类型</summary>
        public List<InputDeviceTypeReal> RequiredDevices { get; set; } = new();

        /// <summary>方案标签（用于UI显示）</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>方案图标名称</summary>
        public string IconName { get; set; } = string.Empty;

        /// <summary>检查设备是否兼容此方案</summary>
        public bool IsDeviceCompatible(InputDeviceTypeReal deviceType)
        {
            return RequiredDevices.Contains(deviceType);
        }

        /// <summary>检查一组设备是否满足方案要求</summary>
        public bool IsSatisfiedBy(IEnumerable<InputDeviceTypeReal> availableDevices)
        {
            var available = availableDevices.ToHashSet();
            return RequiredDevices.All(d => available.Contains(d));
        }

        /// <summary>预定义控制方案</summary>
        public static readonly ControlScheme KeyboardAndMouse = new()
        {
            Name = "KeyboardAndMouse",
            Type = ControlSchemeType.KeyboardAndMouse,
            DisplayName = "Keyboard & Mouse",
            IconName = "keyboard_mouse",
            RequiredDevices = { InputDeviceTypeReal.Keyboard, InputDeviceTypeReal.Mouse }
        };

        public static readonly ControlScheme Gamepad = new()
        {
            Name = "Gamepad",
            Type = ControlSchemeType.Gamepad,
            DisplayName = "Gamepad",
            IconName = "gamepad",
            RequiredDevices = { InputDeviceTypeReal.Gamepad }
        };

        public static readonly ControlScheme Touch = new()
        {
            Name = "Touch",
            Type = ControlSchemeType.Touch,
            DisplayName = "Touch",
            IconName = "touch",
            RequiredDevices = { InputDeviceTypeReal.Touch }
        };

        public static readonly ControlScheme SwitchPro = new()
        {
            Name = "SwitchPro",
            Type = ControlSchemeType.SwitchPro,
            DisplayName = "Switch Pro Controller",
            IconName = "switch_pro",
            RequiredDevices = { InputDeviceTypeReal.Gamepad }
        };

        public static readonly ControlScheme DualSense = new()
        {
            Name = "DualSense",
            Type = ControlSchemeType.DualSense,
            DisplayName = "DualSense (PS5)",
            IconName = "dualsense",
            RequiredDevices = { InputDeviceTypeReal.Gamepad }
        };

        /// <summary>获取所有预定义方案</summary>
        public static ControlScheme[] GetAllSchemes() => new[]
        {
            KeyboardAndMouse, Gamepad, Touch, SwitchPro, DualSense
        };
    }

    #endregion

    #region 绑定记录 - BindingRecord

    /// <summary>绑定记录（持久化用）</summary>
    public class BindingRecord
    {
        /// <summary>动作名称</summary>
        public string ActionName { get; set; } = string.Empty;

        /// <summary>绑定路径列表</summary>
        public List<string> BindingPaths { get; set; } = new();

        /// <summary>控制方案</summary>
        public string ControlScheme { get; set; } = string.Empty;

        /// <summary>处理器配置</summary>
        public List<ProcessorConfig> ProcessorConfigs { get; set; } = new();

        /// <summary>是否覆盖默认绑定</summary>
        public bool OverrideDefaults { get; set; } = true;
    }

    /// <summary>处理器配置（序列化用）</summary>
    public class ProcessorConfig
    {
        public string TypeName { get; set; } = string.Empty;
        public Dictionary<string, float> Parameters { get; set; } = new();
    }

    #endregion

    #region 重绑定状态

    /// <summary>重绑定状态</summary>
    public enum RebindState
    {
        Idle,           // 空闲
        WaitingForInput,// 等待用户输入
        Detected,       // 检测到输入（待确认）
        Completed,      // 完成
        Cancelled,      // 已取消
        TimedOut        // 超时
    }

    /// <summary>重绑定UI辅助数据</summary>
    public class RebindUIData
    {
        /// <summary>动作名称</summary>
        public string ActionName { get; set; } = string.Empty;

        /// <summary>当前绑定路径（用于显示）</summary>
        public string CurrentBindingDisplay { get; set; } = string.Empty;

        /// <summary>新检测到的绑定（用于高亮确认）</summary>
        public string DetectedBindingDisplay { get; set; } = string.Empty;

        /// <summary>等待中（用于UI高亮闪烁）</summary>
        public bool IsWaiting => State == RebindState.WaitingForInput;

        /// <summary>当前状态</summary>
        public RebindState State { get; set; } = RebindState.Idle;

        /// <summary>剩余超时秒数</summary>
        public float RemainingTimeout { get; set; }

        /// <summary>是否超时</summary>
        public bool IsTimedOut => State == RebindState.TimedOut;
    }

    #endregion

    #region 重绑定系统 - InputRebindingReal

    /// <summary>
    /// 真实输入重绑定系统
    /// 支持运行时重绑定、持久化保存/加载、默认绑定管理
    /// </summary>
    public class InputRebindingReal
    {
        private readonly Dictionary<string, InputActionMapReal> _actionMaps = new();
        private readonly Dictionary<string, BindingRecord> _bindingRecords = new();
        private readonly Dictionary<string, BindingRecord> _defaultBindings = new();
        private readonly Dictionary<string, List<BindingRecord>> _schemeBindings = new();

        private RebindState _state = RebindState.Idle;
        private string _rebindingAction = string.Empty;
        private string _rebindingScheme = string.Empty;
        private string _detectedBindingPath = string.Empty;
        private float _rebindTimeout = 10.0f;
        private float _elapsedTime;

        /// <summary>当前重绑定状态</summary>
        public RebindState State => _state;

        /// <summary>当前正在重绑定的动作名称</summary>
        public string CurrentRebindAction => _rebindingAction;

        /// <summary>重绑定超时设置（秒）</summary>
        public float RebindTimeout
        {
            get => _rebindTimeout;
            set => _rebindTimeout = Math.Max(1.0f, Math.Min(60.0f, value));
        }

        /// <summary>状态变化事件</summary>
        public event Action<RebindState, string>? OnStateChanged;

        /// <summary>绑定变更事件</summary>
        public event Action<string, string, string>? OnBindingChanged;

        /// <summary>绑定重置事件</summary>
        public event Action<string>? OnBindingReset;

        /// <summary>构造函数</summary>
        public InputRebindingReal()
        {
            // 初始化方案分组
            foreach (var scheme in ControlScheme.GetAllSchemes())
            {
                _schemeBindings[scheme.Name] = new List<BindingRecord>();
            }
        }

        /// <summary>注册动作映射</summary>
        public void RegisterActionMap(InputActionMapReal actionMap)
        {
            if (string.IsNullOrEmpty(actionMap.Name))
                throw new ArgumentException("动作映射名称不能为空");

            _actionMaps[actionMap.Name] = actionMap;

            // 自动记录默认绑定
            RecordDefaultBindings(actionMap);
        }

        /// <summary>注销动作映射</summary>
        public bool UnregisterActionMap(string name)
        {
            return _actionMaps.Remove(name);
        }

        /// <summary>
        /// 开始重绑定流程
        /// 1. 将动作设置为监听状态
        /// 2. 排除当前绑定（避免自反馈）
        /// 3. 等待用户输入
        /// </summary>
        public void StartRebind(string actionName, string controlScheme = "KeyboardAndMouse")
        {
            if (_state == RebindState.WaitingForInput)
            {
                // 已有重绑定进行中，先取消
                CancelRebind();
            }

            // 验证动作是否存在
            var action = FindAction(actionName);
            if (action == null)
            {
                OnStateChanged?.Invoke(RebindState.Cancelled, actionName);
                return;
            }

            _rebindingAction = actionName;
            _rebindingScheme = controlScheme;
            _detectedBindingPath = string.Empty;
            _elapsedTime = 0;
            _state = RebindState.WaitingForInput;

            OnStateChanged?.Invoke(RebindState.WaitingForInput, actionName);
        }

        /// <summary>
        /// 输入事件回调 - 用于重绑定流程中检测用户输入
        /// 当重绑定状态为WaitingForInput时，捕获第一个输入作为新绑定
        /// </summary>
        public void OnInputEvent(in InputEvent evt)
        {
            if (_state != RebindState.WaitingForInput) return;

            // 过滤：只接受兼容当前方案的输入
            if (!IsInputCompatibleWithScheme(evt, _rebindingScheme))
                return;

            // 构造绑定路径
            string bindingPath = BuildBindingPathFromEvent(evt);
            if (string.IsNullOrEmpty(bindingPath)) return;

            // 检查是否为排除的绑定（当前绑定自身）
            var action = FindAction(_rebindingAction);
            if (action != null)
            {
                foreach (var binding in action.Bindings)
                {
                    if (string.Equals(binding.Path, bindingPath, StringComparison.OrdinalIgnoreCase))
                        return; // 忽略当前绑定，避免自反馈
                }
            }

            _detectedBindingPath = bindingPath;
            _state = RebindState.Detected;
            OnStateChanged?.Invoke(RebindState.Detected, _rebindingAction);

            // 自动确认（也可以设为等待二次确认）
            ApplyBinding(_rebindingAction, bindingPath);
        }

        /// <summary>取消重绑定</summary>
        public void CancelRebind()
        {
            if (_state == RebindState.Idle) return;

            string action = _rebindingAction;
            _state = RebindState.Cancelled;
            _rebindingAction = string.Empty;
            _rebindingScheme = string.Empty;
            _detectedBindingPath = string.Empty;
            _elapsedTime = 0;

            OnStateChanged?.Invoke(RebindState.Cancelled, action);
        }

        /// <summary>确认检测到的绑定（需要二次确认时使用）</summary>
        public void ConfirmRebind()
        {
            if (_state != RebindState.Detected) return;

            ApplyBinding(_rebindingAction, _detectedBindingPath);
        }

        /// <summary>更新重绑定超时</summary>
        public void UpdateRebind(float deltaTime)
        {
            if (_state == RebindState.WaitingForInput)
            {
                _elapsedTime += deltaTime;
                if (_elapsedTime >= _rebindTimeout)
                {
                    _state = RebindState.TimedOut;
                    string action = _rebindingAction;
                    _rebindingAction = string.Empty;
                    OnStateChanged?.Invoke(RebindState.TimedOut, action);
                }
            }
        }

        /// <summary>
        /// 应用新绑定到动作
        /// 1. 找到动作
        /// 2. 移除旧绑定（当前方案）
        /// 3. 添加新绑定
        /// 4. 记录变更
        /// </summary>
        public void ApplyBinding(string actionName, string newBindingPath)
        {
            var action = FindAction(actionName);
            if (action == null)
            {
                _state = RebindState.Cancelled;
                return;
            }

            // 解析新绑定
            var parsedBinding = InputActionBindingReal.Parse(newBindingPath);
            var newBinding = new InputActionBinding
            {
                Path = newBindingPath,
                DeviceType = parsedBinding.DeviceType,
                ControlName = parsedBinding.ControlName,
                Scale = 1.0f
            };

            // 移除当前方案的旧绑定
            RemoveSchemeBindings(action, _rebindingScheme);

            // 添加新绑定
            action.Bindings.Add(newBinding);

            // 更新记录
            UpdateBindingRecord(actionName, newBindingPath, _rebindingScheme);

            string oldPath = GetBindingPath(actionName, _rebindingScheme);
            _state = RebindState.Completed;

            OnBindingChanged?.Invoke(actionName, oldPath, newBindingPath);
            OnStateChanged?.Invoke(RebindState.Completed, actionName);

            // 自动重置状态
            _rebindingAction = string.Empty;
            _rebindingScheme = string.Empty;
            _detectedBindingPath = string.Empty;
            _elapsedTime = 0;
        }

        /// <summary>应用绑定（指定确切方案）</summary>
        public void ApplyBinding(string actionName, string newBindingPath, string controlScheme)
        {
            string previousScheme = _rebindingScheme;
            _rebindingScheme = controlScheme;
            ApplyBinding(actionName, newBindingPath);
            _rebindingScheme = previousScheme;
        }

        /// <summary>
        /// 重置单个动作为默认绑定
        /// </summary>
        public void ResetToDefault(string actionName, string? controlScheme = null)
        {
            // 查找默认绑定
            string key = controlScheme != null
                ? $"{actionName}_{controlScheme}"
                : actionName;

            if (!_defaultBindings.TryGetValue(key, out var defaultRecord) &&
                !_defaultBindings.TryGetValue(actionName, out defaultRecord))
            {
                return;
            }

            var action = FindAction(actionName);
            if (action == null) return;

            // 清除所有绑定
            action.Bindings.Clear();

            // 恢复默认绑定
            foreach (var path in defaultRecord.BindingPaths)
            {
                var parsed = InputActionBindingReal.Parse(path);
                action.Bindings.Add(new InputActionBinding
                {
                    Path = path,
                    DeviceType = parsed.DeviceType,
                    ControlName = parsed.ControlName
                });
            }

            // 更新记录
            _bindingRecords[key] = new BindingRecord
            {
                ActionName = actionName,
                BindingPaths = new List<string>(defaultRecord.BindingPaths),
                ControlScheme = controlScheme ?? "default",
                OverrideDefaults = false
            };

            OnBindingReset?.Invoke(actionName);
        }

        /// <summary>重置所有绑定为默认</summary>
        public void ResetAllBindings()
        {
            foreach (var kvp in _defaultBindings)
            {
                var action = FindAction(kvp.Value.ActionName);
                if (action != null)
                {
                    action.Bindings.Clear();
                    foreach (var path in kvp.Value.BindingPaths)
                    {
                        var parsed = InputActionBindingReal.Parse(path);
                        action.Bindings.Add(new InputActionBinding
                        {
                            Path = path,
                            DeviceType = parsed.DeviceType,
                            ControlName = parsed.ControlName
                        });
                    }
                }
            }

            // 清除自定义记录
            _bindingRecords.Clear();
            foreach (var kvp in _defaultBindings)
            {
                _bindingRecords[kvp.Key] = new BindingRecord
                {
                    ActionName = kvp.Value.ActionName,
                    BindingPaths = new List<string>(kvp.Value.BindingPaths),
                    ControlScheme = kvp.Value.ControlScheme,
                    OverrideDefaults = false
                };
            }

            OnBindingReset?.Invoke("*");
        }

        /// <summary>
        /// 保存绑定到文件（JSON序列化）
        /// 按ControlScheme分组保存
        /// </summary>
        public void SaveBindings(string filePath)
        {
            var saveData = new
            {
                version = 2,
                timestamp = DateTime.UtcNow.ToString("O"),
                schemes = _schemeBindings
                    .Where(kvp => kvp.Value.Count > 0)
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                globalBindings = _bindingRecords.Values.ToList()
            };

            string dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            string json = JsonSerializer.Serialize(saveData, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            File.WriteAllText(filePath, json);
        }

        /// <summary>
        /// 从文件加载绑定（JSON反序列化）
        /// </summary>
        public bool LoadBindings(string filePath)
        {
            if (!File.Exists(filePath))
                return false;

            try
            {
                string json = File.ReadAllText(filePath);
                var jsonDoc = JsonDocument.Parse(json);

                // 读取全局绑定
                if (jsonDoc.RootElement.TryGetProperty("globalBindings", out var bindingsElem))
                {
                    foreach (var elem in bindingsElem.EnumerateArray())
                    {
                        var record = ParseBindingRecordFromJson(elem);
                        if (record != null)
                        {
                            string key = string.IsNullOrEmpty(record.ControlScheme)
                                ? record.ActionName
                                : $"{record.ActionName}_{record.ControlScheme}";
                            _bindingRecords[key] = record;
                        }
                    }
                }

                // 读取方案分组绑定
                if (jsonDoc.RootElement.TryGetProperty("schemes", out var schemesElem))
                {
                    foreach (var scheme in schemesElem.EnumerateObject())
                    {
                        string schemeName = scheme.Name;
                        var records = new List<BindingRecord>();
                        foreach (var elem in scheme.Value.EnumerateArray())
                        {
                            var record = ParseBindingRecordFromJson(elem);
                            if (record != null)
                                records.Add(record);
                        }
                        _schemeBindings[schemeName] = records;
                    }
                }

                // 应用加载的绑定到动作
                ApplyLoadedBindings();

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 保存绑定到PlayerPrefs模拟（键值对存储）
        /// </summary>
        public void SaveToPlayerPrefs(string prefix = "InputBindings_")
        {
            // 模拟PlayerPrefs存储：每个绑定一个键
            foreach (var record in _bindingRecords.Values)
            {
                string key = $"{prefix}{record.ActionName}";
                if (!string.IsNullOrEmpty(record.ControlScheme))
                    key += $"_{record.ControlScheme}";

                string value = string.Join(";", record.BindingPaths);
                PlayerPrefsSet(key, value);
            }
        }

        /// <summary>从PlayerPrefs模拟加载绑定</summary>
        public void LoadFromPlayerPrefs(string prefix = "InputBindings_")
        {
            // 遍历所有记录的key前缀
            var allKeys = PlayerPrefsGetAllKeys(prefix);
            foreach (var key in allKeys)
            {
                string? value = PlayerPrefsGet(key);
                if (string.IsNullOrEmpty(value)) continue;

                // 从key解析动作名和方案
                string rest = key.Substring(prefix.Length);
                string actionName = rest;
                string? scheme = null;
                int sepIdx = rest.LastIndexOf('_');
                if (sepIdx > 0)
                {
                    string possibleScheme = rest.Substring(sepIdx + 1);
                    if (ControlScheme.GetAllSchemes().Any(s => s.Name == possibleScheme))
                    {
                        actionName = rest.Substring(0, sepIdx);
                        scheme = possibleScheme;
                    }
                }

                var paths = value.Split(';', StringSplitOptions.RemoveEmptyEntries);
                _bindingRecords[key] = new BindingRecord
                {
                    ActionName = actionName,
                    BindingPaths = paths.ToList(),
                    ControlScheme = scheme ?? string.Empty,
                    OverrideDefaults = true
                };
            }

            ApplyLoadedBindings();
        }

        /// <summary>获取动作的当前绑定路径</summary>
        public string GetBindingPath(string actionName, string controlScheme = "")
        {
            string key = string.IsNullOrEmpty(controlScheme)
                ? actionName
                : $"{actionName}_{controlScheme}";

            if (_bindingRecords.TryGetValue(key, out var record) && record.BindingPaths.Count > 0)
                return record.BindingPaths[0];

            // 回退：查找默认
            if (_defaultBindings.TryGetValue(key, out var defaultRecord) && defaultRecord.BindingPaths.Count > 0)
                return defaultRecord.BindingPaths[0];

            var action = FindAction(actionName);
            if (action != null && action.Bindings.Count > 0)
                return action.Bindings[0].Path;

            return string.Empty;
        }

        /// <summary>获取动作的所有绑定路径</summary>
        public string[] GetAllBindingPaths(string actionName)
        {
            var action = FindAction(actionName);
            return action?.Bindings.Select(b => b.Path).ToArray() ?? Array.Empty<string>();
        }

        /// <summary>获取控制方案的所有绑定记录</summary>
        public List<BindingRecord> GetSchemeBindings(string schemeName)
        {
            return _schemeBindings.TryGetValue(schemeName, out var records)
                ? records : new List<BindingRecord>();
        }

        /// <summary>获取所有自定义绑定记录</summary>
        public IReadOnlyDictionary<string, BindingRecord> GetAllCustomBindings() => _bindingRecords;

        /// <summary>
        /// 获取重绑定UI辅助数据
        /// 用于UI渲染当前绑定状态
        /// </summary>
        public RebindUIData GetRebindUIData(string actionName)
        {
            var data = new RebindUIData
            {
                ActionName = actionName,
                State = _state,
                RemainingTimeout = Math.Max(0, _rebindTimeout - _elapsedTime)
            };

            // 当前绑定显示
            var action = FindAction(actionName);
            if (action != null && action.Bindings.Count > 0)
            {
                data.CurrentBindingDisplay = FormatBindingForDisplay(action.Bindings[0].Path);
            }
            else
            {
                data.CurrentBindingDisplay = "Unbound";
            }

            // 检测到的绑定显示
            if (_state == RebindState.Detected && _rebindingAction == actionName)
            {
                data.DetectedBindingDisplay = FormatBindingForDisplay(_detectedBindingPath);
            }

            // 等待中状态
            if (_state == RebindState.WaitingForInput && _rebindingAction == actionName)
            {
                data.State = RebindState.WaitingForInput;
            }

            return data;
        }

        /// <summary>格式化绑定路径用于UI显示</summary>
        public static string FormatBindingForDisplay(string bindingPath)
        {
            if (string.IsNullOrEmpty(bindingPath))
                return "Unbound";

            // 解析路径
            var parsed = InputActionBindingReal.Parse(bindingPath);
            string control = parsed.ControlName;

            // 友好名称映射
            return control.ToLowerInvariant() switch
            {
                "space" => "Space",
                "enter" => "Enter",
                "escape" => "Escape",
                "leftButton" => "Mouse Left",
                "rightButton" => "Mouse Right",
                "middleButton" => "Mouse Middle",
                "buttonSouth" => "A / Cross",
                "buttonEast" => "B / Circle",
                "buttonWest" => "X / Square",
                "buttonNorth" => "Y / Triangle",
                "leftShoulder" => "LB / L1",
                "rightShoulder" => "RB / R1",
                "leftTrigger" => "LT / L2",
                "rightTrigger" => "RT / R2",
                "leftStick" => "L-Stick",
                "rightStick" => "R-Stick",
                "dpadUp" => "D-Pad Up",
                "dpadDown" => "D-Pad Down",
                "dpadLeft" => "D-Pad Left",
                "dpadRight" => "D-Pad Right",
                "start" => "Start / Options",
                "back" => "Back / Select",
                "guide" => "Guide / Home",
                _ => FormatCamelCase(control)
            };
        }

        #region 私有方法

        /// <summary>记录默认绑定</summary>
        private void RecordDefaultBindings(InputActionMapReal actionMap)
        {
            foreach (var action in actionMap.Actions.Values)
            {
                string key = action.Name;
                if (!_defaultBindings.ContainsKey(key))
                {
                    _defaultBindings[key] = new BindingRecord
                    {
                        ActionName = action.Name,
                        BindingPaths = action.Bindings.Select(b => b.Path).ToList(),
                        ControlScheme = "default",
                        OverrideDefaults = false
                    };
                }
            }
        }

        /// <summary>查找动作</summary>
        private InputActionReal? FindAction(string actionName)
        {
            foreach (var map in _actionMaps.Values)
            {
                if (map.TryGetAction(actionName, out var action))
                    return action;
            }
            return null;
        }

        /// <summary>移除指定方案的动作绑定</summary>
        private void RemoveSchemeBindings(InputActionReal action, string scheme)
        {
            var targetDeviceTypes = GetSchemeDeviceTypes(scheme);
            action.Bindings.RemoveAll(b =>
                targetDeviceTypes.Contains(b.DeviceType) ||
                string.IsNullOrEmpty(b.Path));
        }

        /// <summary>获取方案对应的设备类型</summary>
        private static List<InputDeviceTypeReal> GetSchemeDeviceTypes(string schemeName)
        {
            var scheme = ControlScheme.GetAllSchemes()
                .FirstOrDefault(s => s.Name == schemeName);

            return scheme?.RequiredDevices ?? new List<InputDeviceTypeReal>
            {
                InputDeviceTypeReal.Unknown
            };
        }

        /// <summary>检查输入是否兼容方案</summary>
        private bool IsInputCompatibleWithScheme(in InputEvent evt, string schemeName)
        {
            var deviceTypes = GetSchemeDeviceTypes(schemeName);
            return deviceTypes.Contains(evt.DeviceType);
        }

        /// <summary>从输入事件构造绑定路径</summary>
        private string BuildBindingPathFromEvent(in InputEvent evt)
        {
            string deviceName = evt.DeviceType switch
            {
                InputDeviceTypeReal.Keyboard => "Keyboard",
                InputDeviceTypeReal.Mouse => "Mouse",
                InputDeviceTypeReal.Gamepad => "Gamepad",
                InputDeviceTypeReal.Touch => "Touch",
                _ => "Unknown"
            };

            string controlPath = evt.ControlPath;
            // 确保格式为 <Device>/control
            if (!controlPath.StartsWith("<"))
            {
                controlPath = $"{deviceName}/{controlPath}";
            }

            return $"<{controlPath}>";
        }

        /// <summary>更新绑定记录</summary>
        private void UpdateBindingRecord(string actionName, string bindingPath, string scheme)
        {
            string key = string.IsNullOrEmpty(scheme) ? actionName : $"{actionName}_{scheme}";

            _bindingRecords[key] = new BindingRecord
            {
                ActionName = actionName,
                BindingPaths = new List<string> { bindingPath },
                ControlScheme = scheme,
                OverrideDefaults = true
            };

            // 更新方案分组
            if (_schemeBindings.TryGetValue(scheme, out var schemeRecords))
            {
                var existing = schemeRecords.FirstOrDefault(r => r.ActionName == actionName);
                if (existing != null)
                {
                    existing.BindingPaths = new List<string> { bindingPath };
                }
                else
                {
                    schemeRecords.Add(new BindingRecord
                    {
                        ActionName = actionName,
                        BindingPaths = new List<string> { bindingPath },
                        ControlScheme = scheme
                    });
                }
            }
        }

        /// <summary>应用已加载的绑定到动作</summary>
        private void ApplyLoadedBindings()
        {
            foreach (var record in _bindingRecords.Values)
            {
                if (!record.OverrideDefaults) continue;

                var action = FindAction(record.ActionName);
                if (action == null) continue;

                // 移除方案绑定后重新添加
                RemoveSchemeBindings(action, record.ControlScheme);

                foreach (var path in record.BindingPaths)
                {
                    var parsed = InputActionBindingReal.Parse(path);
                    action.Bindings.Add(new InputActionBinding
                    {
                        Path = path,
                        DeviceType = parsed.DeviceType,
                        ControlName = parsed.ControlName
                    });
                }
            }
        }

        /// <summary>从JSON元素解析绑定记录</summary>
        private static BindingRecord? ParseBindingRecordFromJson(JsonElement elem)
        {
            try
            {
                var record = new BindingRecord();

                if (elem.TryGetProperty("actionName", out var nameElem))
                    record.ActionName = nameElem.GetString() ?? string.Empty;

                if (elem.TryGetProperty("bindingPaths", out var pathsElem))
                {
                    foreach (var path in pathsElem.EnumerateArray())
                    {
                        string? p = path.GetString();
                        if (!string.IsNullOrEmpty(p))
                            record.BindingPaths.Add(p);
                    }
                }

                if (elem.TryGetProperty("controlScheme", out var schemeElem))
                    record.ControlScheme = schemeElem.GetString() ?? string.Empty;

                return record;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>格式化CamelCase为友好名称</summary>
        private static string FormatCamelCase(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            var sb = new StringBuilder();
            sb.Append(char.ToUpper(text[0]));
            for (int i = 1; i < text.Length; i++)
            {
                if (char.IsUpper(text[i]) && i > 0 && !char.IsUpper(text[i - 1]))
                    sb.Append(' ');
                sb.Append(text[i]);
            }
            return sb.ToString();
        }

        #endregion

        #region PlayerPrefs模拟

        /// <summary>模拟PlayerPrefs存储（内存字典 + 文件持久化）</summary>
        private static readonly Dictionary<string, string> _playerPrefs = new();

        private static void PlayerPrefsSet(string key, string value)
        {
            _playerPrefs[key] = value;
        }

        private static string? PlayerPrefsGet(string key)
        {
            return _playerPrefs.TryGetValue(key, out string? value) ? value : null;
        }

        private static List<string> PlayerPrefsGetAllKeys(string prefix)
        {
            return _playerPrefs.Keys.Where(k => k.StartsWith(prefix)).ToList();
        }

        /// <summary>保存PlayerPrefs到文件</summary>
        public static void SavePlayerPrefsToFile(string filePath)
        {
            var lines = new List<string> { $"// PlayerPrefs - {DateTime.UtcNow:O}" };
            foreach (var kvp in _playerPrefs)
            {
                lines.Add($"{kvp.Key}={kvp.Value}");
            }
            File.WriteAllLines(filePath, lines);
        }

        /// <summary>从文件加载PlayerPrefs</summary>
        public static void LoadPlayerPrefsFromFile(string filePath)
        {
            if (!File.Exists(filePath)) return;
            _playerPrefs.Clear();
            foreach (var line in File.ReadAllLines(filePath))
            {
                if (string.IsNullOrEmpty(line) || line.StartsWith("//")) continue;
                int eqIdx = line.IndexOf('=');
                if (eqIdx > 0)
                {
                    string key = line.Substring(0, eqIdx);
                    string value = line.Substring(eqIdx + 1);
                    _playerPrefs[key] = value;
                }
            }
        }

        #endregion
    }

    #endregion
}
