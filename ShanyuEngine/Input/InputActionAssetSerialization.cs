using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShanyuEngine.Input.Enhanced
{
    #region InputActionAssetSerialization —— 输入动作资产序列化

    /// <summary>
    /// 输入动作资产序列化器
    /// 负责 InputActionAsset 的 JSON 序列化/反序列化、版本迁移、导入导出、热重载
    /// </summary>
    public static class InputActionAssetSerialization
    {
        /// <summary>当前序列化格式版本</summary>
        public const int CurrentVersion = 2;

        /// <summary>序列化选项 (优化可读性)</summary>
        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters =
            {
                new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
            }
        };

        #region 序列化

        /// <summary>
        /// 将 InputActionAsset 序列化为 JSON 字符串
        /// </summary>
        public static string SerializeToJson(InputActionAsset asset)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));

            var dto = AssetToDTO(asset);
            return JsonSerializer.Serialize(dto, _jsonOptions);
        }

        /// <summary>
        /// 将 InputActionAsset 序列化为 JSON 文件
        /// </summary>
        public static void SerializeToFile(InputActionAsset asset, string filePath)
        {
            string json = SerializeToJson(asset);
            File.WriteAllText(filePath, json);
        }

        /// <summary>
        /// 将 InputActionAsset 序列化为二进制 (JSON + Base64)
        /// </summary>
        public static byte[] SerializeToBinary(InputActionAsset asset)
        {
            string json = SerializeToJson(asset);
            return System.Text.Encoding.UTF8.GetBytes(json);
        }

        #endregion

        #region 反序列化

        /// <summary>
        /// 从 JSON 字符串反序列化为 InputActionAsset
        /// </summary>
        public static InputActionAsset DeserializeFromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) throw new ArgumentException("JSON 字符串为空");

            var dto = JsonSerializer.Deserialize<InputActionAssetDTO>(json, _jsonOptions);
            if (dto == null) throw new InvalidOperationException("JSON 反序列化失败");

            // 版本迁移
            dto = MigrateVersion(dto);

            return DTOToAsset(dto);
        }

        /// <summary>
        /// 从 JSON 文件反序列化为 InputActionAsset
        /// </summary>
        public static InputActionAsset DeserializeFromFile(string filePath)
        {
            string json = File.ReadAllText(filePath);
            return DeserializeFromJson(json);
        }

        /// <summary>
        /// 从二进制数据反序列化
        /// </summary>
        public static InputActionAsset DeserializeFromBinary(byte[] data)
        {
            string json = System.Text.Encoding.UTF8.GetString(data);
            return DeserializeFromJson(json);
        }

        #endregion

        #region 版本迁移

        /// <summary>
        /// 版本迁移：将旧版本格式升级到当前版本
        /// </summary>
        public static InputActionAssetDTO MigrateVersion(InputActionAssetDTO dto)
        {
            if (dto.Version >= CurrentVersion) return dto;

            switch (dto.Version)
            {
                case 1:
                    // v1 → v2: 将 actionMaps 重命名为 maps, 添加 bindings 字段
                    dto.Version = 2;
                    // v1 格式兼容处理
                    if (dto.Maps == null && dto.V1ActionMaps != null)
                    {
                        dto.Maps = dto.V1ActionMaps;
                        dto.V1ActionMaps = null;
                    }
                    break;

                default:
                    dto.Version = CurrentVersion;
                    break;
            }

            return dto;
        }

        #endregion

        #region DTO 转换

        /// <summary>Asset → DTO</summary>
        private static InputActionAssetDTO AssetToDTO(InputActionAsset asset)
        {
            var dto = new InputActionAssetDTO
            {
                Name = asset.Name,
                Version = CurrentVersion,
                Maps = new List<InputActionMapDTO>(),
                ControlSchemes = new List<ControlSchemeDTO>(),
                BindingOverrides = new List<BindingOverrideDTO>()
            };

            foreach (var map in asset.ActionMaps.Values)
            {
                var mapDto = new InputActionMapDTO
                {
                    Name = map.Name,
                    DisplayName = map.DisplayName,
                    Description = map.Description,
                    Priority = map.Priority,
                    Enabled = map.Enabled,
                    ControlScheme = map.ControlScheme,
                    Actions = new List<EnhancedInputActionDTO>()
                };

                foreach (var action in map.Actions.Values)
                {
                    var actionDto = ActionToDTO(action);
                    mapDto.Actions.Add(actionDto);
                }

                dto.Maps.Add(mapDto);
            }

            foreach (var scheme in asset.ControlSchemes)
            {
                dto.ControlSchemes.Add(SchemeToDTO(scheme));
            }

            foreach (var ov in asset.BindingOverrides)
            {
                dto.BindingOverrides.Add(new BindingOverrideDTO
                {
                    ActionMap = ov.ActionMap,
                    Action = ov.Action,
                    OriginalBinding = ov.OriginalBinding,
                    OverrideBinding = ov.OverrideBinding,
                    ControlScheme = ov.ControlScheme
                });
            }

            return dto;
        }

        /// <summary>DTO → Asset</summary>
        private static InputActionAsset DTOToAsset(InputActionAssetDTO dto)
        {
            var asset = new InputActionAsset(dto.Name)
            {
                Version = dto.Version
            };

            if (dto.Maps != null)
            {
                foreach (var mapDto in dto.Maps)
                {
                    var map = asset.AddActionMap(mapDto.Name, mapDto.DisplayName, mapDto.Description);
                    map.Priority = mapDto.Priority;
                    map.Enabled = mapDto.Enabled;
                    map.ControlScheme = mapDto.ControlScheme;

                    if (mapDto.Actions != null)
                    {
                        foreach (var actionDto in mapDto.Actions)
                        {
                            var action = map.AddAction(actionDto.Name, actionDto.ExpectedControlType, actionDto.Description);
                            action.Enabled = actionDto.Enabled;

                            // 恢复绑定
                            if (actionDto.Bindings != null)
                            {
                                foreach (var bindingDto in actionDto.Bindings)
                                {
                                    action.AddBinding(BindingDTOToBinding(bindingDto));
                                }
                            }

                            // 恢复组合绑定
                            if (actionDto.Composites != null)
                            {
                                foreach (var compDto in actionDto.Composites)
                                {
                                    var composite = CreateCompositeFromDTO(compDto);
                                    if (composite != null)
                                    {
                                        action.Composites[compDto.Name] = composite;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            if (dto.ControlSchemes != null)
            {
                foreach (var schemeDto in dto.ControlSchemes)
                {
                    asset.ControlSchemes.Add(DTOToScheme(schemeDto));
                }
            }

            if (dto.BindingOverrides != null)
            {
                foreach (var ovDto in dto.BindingOverrides)
                {
                    asset.BindingOverrides.Add(new BindingOverride(
                        ovDto.ActionMap, ovDto.Action,
                        ovDto.OriginalBinding, ovDto.OverrideBinding,
                        ovDto.ControlScheme
                    ));
                }
            }

            return asset;
        }

        /// <summary>Action → DTO</summary>
        private static EnhancedInputActionDTO ActionToDTO(EnhancedInputAction action)
        {
            var dto = new EnhancedInputActionDTO
            {
                Name = action.Name,
                ExpectedControlType = action.ExpectedControlType,
                Description = action.Description,
                Enabled = action.Enabled,
                Bindings = new List<InputBindingDTO>(),
                Composites = new List<InputBindingCompositeDTO>()
            };

            foreach (var binding in action.Bindings)
            {
                dto.Bindings.Add(BindingToDTO(binding));
            }

            foreach (var composite in action.Composites.Values)
            {
                dto.Composites.Add(CompositeToDTO(composite));
            }

            return dto;
        }

        /// <summary>Binding → DTO</summary>
        private static InputBindingDTO BindingToDTO(InputBinding binding)
        {
            return new InputBindingDTO
            {
                Id = binding.Id,
                Path = binding.Path,
                BindingType = binding.BindingType,
                CompositeName = binding.CompositeName,
                CompositePart = binding.CompositePart,
                Groups = binding.Groups,
                Enabled = binding.Enabled,
                Weight = binding.Weight
            };
        }

        /// <summary>DTO → Binding</summary>
        private static InputBinding BindingDTOToBinding(InputBindingDTO dto)
        {
            return new InputBinding
            {
                Path = dto.Path,
                BindingType = dto.BindingType,
                CompositeName = dto.CompositeName,
                CompositePart = dto.CompositePart,
                Groups = dto.Groups,
                Enabled = dto.Enabled,
                Weight = dto.Weight
            };
        }

        /// <summary>Composite → DTO</summary>
        private static InputBindingCompositeDTO CompositeToDTO(InputBindingComposite composite)
        {
            return new InputBindingCompositeDTO
            {
                Name = composite.Name,
                TypeName = composite.GetType().Name,
                ValueType = composite.ValueType
            };
        }

        /// <summary>DTO → Composite</summary>
        private static InputBindingComposite CreateCompositeFromDTO(InputBindingCompositeDTO dto)
        {
            return dto.TypeName switch
            {
                "AxisComposite" => new AxisComposite(dto.Name),
                "DpadComposite" => new DpadComposite(dto.Name),
                "Vector2Composite" => new Vector2Composite(dto.Name),
                "OneModifierComposite" => new OneModifierComposite(dto.Name),
                "TwoModifiersComposite" => new TwoModifiersComposite(dto.Name),
                "Vector3Composite" => new Vector3Composite(dto.Name),
                _ => null
            };
        }

        /// <summary>ControlScheme → DTO</summary>
        private static ControlSchemeDTO SchemeToDTO(ControlScheme scheme)
        {
            return new ControlSchemeDTO
            {
                Name = scheme.Name,
                DisplayName = scheme.DisplayName,
                Description = scheme.Description,
                IsDefault = scheme.IsDefault,
                RequiredDeviceTypes = new List<string>(scheme.RequiredDeviceTypes),
                BindingOverrides = new List<BindingOverrideDTO>()
            };
        }

        /// <summary>DTO → ControlScheme</summary>
        private static ControlScheme DTOToScheme(ControlSchemeDTO dto)
        {
            var scheme = new ControlScheme(dto.Name, dto.DisplayName)
            {
                Description = dto.Description,
                IsDefault = dto.IsDefault
            };
            if (dto.RequiredDeviceTypes != null)
            {
                foreach (var deviceType in dto.RequiredDeviceTypes)
                    scheme.AddRequiredDevice(deviceType);
            }
            return scheme;
        }

        #endregion

        #region 热重载

        /// <summary>
        /// 热重载 —— 修改 JSON 文件后运行时更新绑定
        /// 比较新旧资产差异，仅更新变化的绑定
        /// </summary>
        public static InputActionAsset HotReload(InputActionAsset oldAsset, string jsonFilePath)
        {
            if (!File.Exists(jsonFilePath))
                throw new FileNotFoundException($"文件不存在: {jsonFilePath}");

            string json = File.ReadAllText(jsonFilePath);
            return HotReloadFromJson(oldAsset, json);
        }

        /// <summary>
        /// 热重载 (从 JSON 字符串)
        /// </summary>
        public static InputActionAsset HotReloadFromJson(InputActionAsset oldAsset, string newJson)
        {
            var newAsset = DeserializeFromJson(newJson);

            // 保留旧资产的运行时状态 (哪些映射表是启用的)
            foreach (var oldMap in oldAsset.ActionMaps.Values)
            {
                if (newAsset.TryGetActionMap(oldMap.Name, out var newMap))
                {
                    newMap.Enabled = oldMap.Enabled;
                }
            }

            return newAsset;
        }

        /// <summary>
        /// 热重载管理器 —— 监控文件变化并自动重载
        /// </summary>
        public class HotReloadWatcher
        {
            private InputActionAsset _asset;
            private readonly string _filePath;
            private DateTime _lastWriteTime;
            private float _reloadDelay = 0.5f;
            private float _reloadTimer;
            private bool _pendingReload;

            /// <summary>热重载完成事件</summary>
            public event Action<InputActionAsset> OnHotReloaded;

            public HotReloadWatcher(InputActionAsset asset, string filePath)
            {
                _asset = asset;
                _filePath = filePath;
                _lastWriteTime = File.GetLastWriteTime(filePath);
            }

            /// <summary>更新 (检测文件变化)</summary>
            public void Update(float deltaTime)
            {
                if (!File.Exists(_filePath)) return;

                DateTime currentWriteTime = File.GetLastWriteTime(_filePath);
                if (currentWriteTime != _lastWriteTime)
                {
                    _pendingReload = true;
                    _reloadTimer = 0f;
                    _lastWriteTime = currentWriteTime;
                }

                if (_pendingReload)
                {
                    _reloadTimer += deltaTime;
                    if (_reloadTimer >= _reloadDelay)
                    {
                        _pendingReload = false;
                        try
                        {
                            _asset = HotReload(_asset, _filePath);
                            OnHotReloaded?.Invoke(_asset);
                        }
                        catch (Exception)
                        {
                            // 重载失败，忽略
                        }
                    }
                }
            }
        }

        #endregion

        #region 导入/导出

        /// <summary>
        /// 导出为 JSON 文件
        /// </summary>
        public static void Export(InputActionAsset asset, string filePath)
        {
            SerializeToFile(asset, filePath);
        }

        /// <summary>
        /// 从 JSON 文件导入
        /// </summary>
        public static InputActionAsset Import(string filePath)
        {
            return DeserializeFromFile(filePath);
        }

        /// <summary>
        /// 导出所有绑定覆盖为用户配置文件
        /// </summary>
        public static void ExportOverrides(InputActionAsset asset, string filePath)
        {
            var overrideDto = new { bindingOverrides = asset.BindingOverrides };
            string json = JsonSerializer.Serialize(overrideDto, _jsonOptions);
            File.WriteAllText(filePath, json);
        }

        /// <summary>
        /// 导入绑定覆盖 (合并到现有资产)
        /// </summary>
        public static void ImportOverrides(InputActionAsset asset, string filePath)
        {
            string json = File.ReadAllText(filePath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("bindingOverrides", out var overridesEl))
            {
                foreach (var el in overridesEl.EnumerateArray())
                {
                    if (el.TryGetProperty("actionMap", out var mapEl) &&
                        el.TryGetProperty("action", out var actionEl) &&
                        el.TryGetProperty("originalBinding", out var origEl) &&
                        el.TryGetProperty("overrideBinding", out var overEl))
                    {
                        asset.BindingOverrides.Add(new BindingOverride(
                            mapEl.GetString(),
                            actionEl.GetString(),
                            origEl.GetString(),
                            overEl.GetString(),
                            el.TryGetProperty("controlScheme", out var csEl) ? csEl.GetString() : null
                        ));
                    }
                }
            }
        }

        #endregion
    }

    #endregion

    #region DTO 数据结构 (用于 JSON 序列化)

    /// <summary>资产 DTO</summary>
    public class InputActionAssetDTO
    {
        public string Name { get; set; }
        public int Version { get; set; }
        public List<InputActionMapDTO> Maps { get; set; }
        public List<ControlSchemeDTO> ControlSchemes { get; set; }
        public List<BindingOverrideDTO> BindingOverrides { get; set; }
        /// <summary>v1 兼容字段</summary>
        [JsonPropertyName("actionMaps")]
        public List<InputActionMapDTO> V1ActionMaps { get; set; }
    }

    /// <summary>映射表 DTO</summary>
    internal class InputActionMapDTO
    {
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public int Priority { get; set; }
        public bool Enabled { get; set; }
        public string ControlScheme { get; set; }
        public List<EnhancedInputActionDTO> Actions { get; set; }
    }

    /// <summary>动作 DTO</summary>
    internal class EnhancedInputActionDTO
    {
        public string Name { get; set; }
        public InputActionType ExpectedControlType { get; set; }
        public string Description { get; set; }
        public bool Enabled { get; set; }
        public List<InputBindingDTO> Bindings { get; set; }
        public List<InputBindingCompositeDTO> Composites { get; set; }
    }

    /// <summary>绑定 DTO</summary>
    internal class InputBindingDTO
    {
        public string Id { get; set; }
        public string Path { get; set; }
        public InputBindingType BindingType { get; set; }
        public string CompositeName { get; set; }
        public string CompositePart { get; set; }
        public string Groups { get; set; }
        public bool Enabled { get; set; }
        public float Weight { get; set; }
    }

    /// <summary>组合绑定 DTO</summary>
    internal class InputBindingCompositeDTO
    {
        public string Name { get; set; }
        public string TypeName { get; set; }
        public InputActionType ValueType { get; set; }
    }

    /// <summary>控制方案 DTO</summary>
    internal class ControlSchemeDTO
    {
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public bool IsDefault { get; set; }
        public List<string> RequiredDeviceTypes { get; set; }
        public List<BindingOverrideDTO> BindingOverrides { get; set; }
    }

    /// <summary>绑定覆盖 DTO</summary>
    internal class BindingOverrideDTO
    {
        public string ActionMap { get; set; }
        public string Action { get; set; }
        public string OriginalBinding { get; set; }
        public string OverrideBinding { get; set; }
        public string ControlScheme { get; set; }
    }

    #endregion
}
