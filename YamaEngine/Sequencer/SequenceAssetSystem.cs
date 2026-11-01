using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace YamaEngine.Sequencer
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎 - 序列器资产系统 (Sequence Asset System)
    // --------------------------------------------------------------------------
    // 管理序列资产的生命周期：
    //   - SequenceAsset：序列定义的持久化存储
    //   - SequenceTemplate：预定义参数化模板
    //   - SequenceInstance：运行时实例（从资产实例化）
    //   - SequenceExport：导出（视频/音频/JSON/烘焙到动画）
    //   - 序列预览（缩略图/预览播放/低质量预览）
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────────────────
    // SequenceAsset - 序列资产
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 序列资产：序列的持久化存储单元。
    /// 包含序列定义、元数据、版本信息。
    /// </summary>
    public class SequenceAsset
    {
        /// <summary>资产唯一标识。</summary>
        public Guid AssetId { get; } = Guid.NewGuid();

        /// <summary>资产名称。</summary>
        public string AssetName { get; set; } = "Untitled";

        /// <summary>资产文件路径。</summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>资产版本号。</summary>
        public int Version { get; set; } = 1;

        /// <summary>引擎版本（创建时）。</summary>
        public string EngineVersion { get; set; } = "1.0.0";

        /// <summary>创建时间。</summary>
        public DateTime CreatedTime { get; set; } = DateTime.UtcNow;

        /// <summary>修改时间。</summary>
        public DateTime ModifiedTime { get; set; } = DateTime.UtcNow;

        /// <summary>创建者。</summary>
        public string Author { get; set; } = string.Empty;

        /// <summary>描述。</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>标签。</summary>
        public HashSet<string> Tags { get; set; } = new();

        /// <summary>序列数据（核心序列定义）。</summary>
        public Sequence? SequenceData { get; set; }

        /// <summary>资产元数据（自定义键值对）。</summary>
        public Dictionary<string, object> Metadata { get; set; } = new();

        /// <summary>缩略图数据（PNG 字节数组）。</summary>
        public byte[]? Thumbnail { get; set; }

        /// <summary>资产是否已修改（未保存）。</summary>
        public bool IsDirty { get; set; }

        /// <summary>依赖的其他资产列表。</summary>
        public List<string> Dependencies { get; set; } = new();

        public SequenceAsset() { }

        public SequenceAsset(string name)
        {
            AssetName = name;
        }

        // ── 保存与加载 ──────────────────────────────────────────

        /// <summary>将序列资产序列化为 JSON。</summary>
        public string Serialize()
        {
            var data = new Dictionary<string, object>
            {
                ["assetId"] = AssetId.ToString(),
                ["assetName"] = AssetName,
                ["version"] = Version,
                ["engineVersion"] = EngineVersion,
                ["createdTime"] = CreatedTime.ToString("O"),
                ["modifiedTime"] = ModifiedTime.ToString("O"),
                ["author"] = Author,
                ["description"] = Description,
                ["tags"] = new List<string>(Tags),
                ["sequenceName"] = SequenceData?.Name ?? "",
                ["sequenceDuration"] = SequenceData?.Duration ?? 0,
                ["sequenceFrameRate"] = SequenceData?.FrameRate ?? 30,
                ["sequenceLoopMode"] = SequenceData?.LoopMode.ToString() ?? "None",
                ["metadata"] = Metadata,
                ["dependencies"] = Dependencies
            };

            // 序列化轨道信息
            var tracksData = new List<object>();
            if (SequenceData != null)
            {
                foreach (var track in SequenceData.Tracks)
                {
                    tracksData.Add(new Dictionary<string, object>
                    {
                        ["type"] = track.TrackTypeName,
                        ["name"] = track.Name,
                        ["priority"] = track.Priority,
                        ["sectionCount"] = track.SectionCount,
                        ["color"] = track.Color.ToString("X8")
                    });
                }
            }
            data["tracks"] = tracksData;

            return JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        }

        /// <summary>标记资产已修改。</summary>
        public void MarkDirty()
        {
            IsDirty = true;
            ModifiedTime = DateTime.UtcNow;
        }

        /// <summary>标记资产已保存。</summary>
        public void MarkSaved()
        {
            IsDirty = false;
            ModifiedTime = DateTime.UtcNow;
        }

        /// <summary>添加依赖。</summary>
        public void AddDependency(string assetPath)
        {
            if (!string.IsNullOrEmpty(assetPath) && !Dependencies.Contains(assetPath))
                Dependencies.Add(assetPath);
        }

        /// <summary>移除依赖。</summary>
        public void RemoveDependency(string assetPath)
        {
            Dependencies.Remove(assetPath);
        }

        /// <summary>更新版本号。</summary>
        public void BumpVersion()
        {
            Version++;
            MarkDirty();
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // SequenceTemplate - 序列模板
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 序列模板：预定义的序列蓝图。
    /// 支持参数化配置，可快速实例化为具体的序列。
    /// </summary>
    public class SequenceTemplate
    {
        /// <summary>模板名称。</summary>
        public string TemplateName { get; set; } = "Template";

        /// <summary>模板描述。</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>模板类别（如 "对话"、"战斗"、"过场"）。</summary>
        public string Category { get; set; } = string.Empty;

        /// <summary>模板参数定义。</summary>
        public List<TemplateParameter> Parameters { get; set; } = new();

        /// <summary>模板创建委托：根据参数创建序列。</summary>
        public Func<Dictionary<string, object>, Sequence>? CreateSequenceFunc { get; set; }

        /// <summary>模板缩略图。</summary>
        public byte[]? Thumbnail { get; set; }

        public SequenceTemplate() { }

        public SequenceTemplate(string name, string description)
        {
            TemplateName = name;
            Description = description;
        }

        /// <summary>使用指定参数实例化序列。</summary>
        public Sequence? Instantiate(Dictionary<string, object>? paramValues = null)
        {
            var effectiveParams = new Dictionary<string, object>();

            // 合并默认值与用户提供的值
            foreach (var param in Parameters)
            {
                if (paramValues != null && paramValues.TryGetValue(param.Name, out var val))
                    effectiveParams[param.Name] = val;
                else
                    effectiveParams[param.Name] = param.DefaultValue;
            }

            return CreateSequenceFunc?.Invoke(effectiveParams);
        }
    }

    /// <summary>模板参数定义。</summary>
    public class TemplateParameter
    {
        /// <summary>参数名称。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>参数显示名称。</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>参数类型名称。</summary>
        public string TypeName { get; set; } = "float";

        /// <summary>默认值。</summary>
        public object DefaultValue { get; set; }

        /// <summary>最小值（数值类型）。</summary>
        public double MinValue { get; set; } = double.MinValue;

        /// <summary>最大值（数值类型）。</summary>
        public double MaxValue { get; set; } = double.MaxValue;

        /// <summary>参数描述。</summary>
        public string Description { get; set; } = string.Empty;

        public TemplateParameter() { }

        public TemplateParameter(string name, object defaultValue, string typeName = "float")
        {
            Name = name;
            DisplayName = name;
            DefaultValue = defaultValue;
            TypeName = typeName;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // SequenceInstance - 运行时实例
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 序列运行时实例：从 SequenceAsset 实例化而来的可播放序列。
    /// 管理绑定解析、运行时状态、生命周期。
    /// </summary>
    public class SequenceInstance
    {
        /// <summary>实例唯一标识。</summary>
        public Guid InstanceId { get; } = Guid.NewGuid();

        /// <summary>源资产引用。</summary>
        public SequenceAsset? SourceAsset { get; set; }

        /// <summary>实例化后的序列。</summary>
        public Sequence? Sequence { get; set; }

        /// <summary>实例的播放控制器。</summary>
        public SequencerController? Controller { get; set; }

        /// <summary>运行时绑定上下文。</summary>
        public object? BindingsContext { get; set; }

        /// <summary>实例状态。</summary>
        public SequenceInstanceState State { get; private set; } = SequenceInstanceState.Uninitialized;

        /// <summary>实例名称。</summary>
        public string InstanceName { get; set; } = "Sequence Instance";

        /// <summary>绑定覆盖列表（运行时覆盖资产中的绑定）。</summary>
        public Dictionary<string, object> BindingOverrides { get; set; } = new();

        /// <summary>状态变化事件。</summary>
        public event Action<SequenceInstanceState, SequenceInstanceState>? OnStateChanged;

        /// <summary>播放完成事件。</summary>
        public event Action? OnPlaybackCompleted;

        public SequenceInstance() { }

        /// <summary>从资产实例化。</summary>
        public bool InstantiateFromAsset(SequenceAsset asset, object? context = null)
        {
            if (asset.SequenceData == null) return false;

            SourceAsset = asset;
            Sequence = new Sequence(asset.SequenceData.Name, asset.SequenceData.Duration, asset.SequenceData.FrameRate)
            {
                LoopMode = asset.SequenceData.LoopMode
            };

            BindingsContext = context;
            InstanceName = asset.AssetName;

            // 应用绑定覆盖
            ApplyBindingOverrides();

            // 创建控制器
            Controller = new SequencerController(Sequence)
            {
                EvaluationContext = context
            };

            Controller.OnPlaybackStateChanged += HandlePlaybackStateChanged;

            SetState(SequenceInstanceState.Ready);
            return true;
        }

        /// <summary>应用绑定覆盖。</summary>
        private void ApplyBindingOverrides()
        {
            if (Sequence == null || BindingsContext == null) return;

            foreach (var kvp in BindingOverrides)
            {
                foreach (var binding in Sequence.Bindings)
                {
                    if (binding.PropertyPath == kvp.Key)
                    {
                        binding.BoundObject = kvp.Value;
                        binding.IsResolved = true;
                    }
                }
            }
        }

        /// <summary>初始化并解析绑定。</summary>
        public void Initialize()
        {
            if (Sequence == null || Controller == null) return;

            Sequence.ResolveBindings(BindingsContext);
            SetState(SequenceInstanceState.Initialized);
        }

        /// <summary>播放实例。</summary>
        public void Play()
        {
            if (State != SequenceInstanceState.Ready && State != SequenceInstanceState.Initialized)
                return;

            SetState(SequenceInstanceState.Playing);
            Controller?.Play();
        }

        /// <summary>暂停。</summary>
        public void Pause()
        {
            SetState(SequenceInstanceState.Paused);
            Controller?.Pause();
        }

        /// <summary>恢复播放。</summary>
        public void Resume()
        {
            SetState(SequenceInstanceState.Playing);
            Controller?.Play();
        }

        /// <summary>停止。</summary>
        public void Stop()
        {
            Controller?.Stop();
            SetState(SequenceInstanceState.Stopped);
        }

        /// <summary>每帧更新。</summary>
        public void Tick(double deltaTime)
        {
            Controller?.Tick(deltaTime);
        }

        /// <summary>销毁实例，释放资源。</summary>
        public void Destroy()
        {
            Controller?.Stop();
            SetState(SequenceInstanceState.Destroyed);
            Sequence = null;
            Controller = null;
        }

        private void HandlePlaybackStateChanged(SequencePlaybackState newState)
        {
            if (newState == SequencePlaybackState.Stopped)
            {
                SetState(SequenceInstanceState.Stopped);
                OnPlaybackCompleted?.Invoke();
            }
        }

        private void SetState(SequenceInstanceState newState)
        {
            var old = State;
            State = newState;
            OnStateChanged?.Invoke(old, newState);
        }
    }

    /// <summary>序列实例状态。</summary>
    public enum SequenceInstanceState
    {
        /// <summary>未初始化。</summary>
        Uninitialized,
        /// <summary>就绪（等待初始化）。</summary>
        Ready,
        /// <summary>已初始化（绑定已解析）。</summary>
        Initialized,
        /// <summary>正在播放。</summary>
        Playing,
        /// <summary>已暂停。</summary>
        Paused,
        /// <summary>已停止。</summary>
        Stopped,
        /// <summary>已销毁。</summary>
        Destroyed
    }

    // ──────────────────────────────────────────────────────────────────────────
    // SequenceExport - 导出系统
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>导出格式。</summary>
    public enum SequenceExportFormat
    {
        /// <summary>视频（MP4/WebM）。</summary>
        Video,
        /// <summary>音频。</summary>
        Audio,
        /// <summary>JSON（序列数据）。</summary>
        JSON,
        /// <summary>烘焙到动画资产。</summary>
        BakedAnimation,
        /// <summary>FBX 动画。</summary>
        FBX,
        /// <summary>图像序列（PNG）。</summary>
        ImageSequence
    }

    /// <summary>导出配置。</summary>
    public class SequenceExportConfig
    {
        /// <summary>导出格式。</summary>
        public SequenceExportFormat Format { get; set; } = SequenceExportFormat.JSON;

        /// <summary>输出文件路径。</summary>
        public string OutputPath { get; set; } = string.Empty;

        /// <summary>分辨率宽度。</summary>
        public int ResolutionWidth { get; set; } = 1920;

        /// <summary>分辨率高度。</summary>
        public int ResolutionHeight { get; set; } = 1080;

        /// <summary>帧率。</summary>
        public double FrameRate { get; set; } = 30.0;

        /// <summary>视频编码质量 (0~100)。</summary>
        public int Quality { get; set; } = 90;

        /// <summary>导出范围起始。</summary>
        public double StartTime { get; set; }

        /// <summary>导出范围结束。</summary>
        public double EndTime { get; set; }

        /// <summary>是否包含音频轨道。</summary>
        public bool IncludeAudio { get; set; } = true;

        /// <summary>烘焙帧率（仅烘焙动画时使用）。</summary>
        public double BakeFrameRate { get; set; } = 30.0;

        /// <summary>导出进度回调 (0~1)。</summary>
        public Action<float>? OnProgress { get; set; }

        /// <summary>导出完成回调。</summary>
        public Action<bool, string>? OnComplete { get; set; }
    }

    /// <summary>
    /// 序列导出器：将序列导出为各种格式。
    /// </summary>
    public static class SequenceExport
    {
        /// <summary>导出序列到 JSON。</summary>
        public static string ExportToJSON(Sequence sequence)
        {
            if (sequence == null) throw new ArgumentNullException(nameof(sequence));

            var data = new Dictionary<string, object>
            {
                ["name"] = sequence.Name,
                ["duration"] = sequence.Duration,
                ["frameRate"] = sequence.FrameRate,
                ["loopMode"] = sequence.LoopMode.ToString(),
                ["playRate"] = sequence.PlaybackRate
            };

            // 轨道数据
            var tracksArray = new List<object>();
            foreach (var track in sequence.Tracks)
            {
                var trackData = new Dictionary<string, object>
                {
                    ["name"] = track.Name,
                    ["type"] = track.TrackTypeName,
                    ["priority"] = track.Priority,
                    ["isMuted"] = track.IsMuted,
                    ["isVisible"] = track.IsVisible
                };

                var sectionsArray = new List<object>();
                foreach (var section in track.Sections)
                {
                    sectionsArray.Add(new Dictionary<string, object>
                    {
                        ["name"] = section.Name,
                        ["startTime"] = section.StartTime,
                        ["endTime"] = section.EndTime,
                        ["isLooping"] = section.IsLooping,
                        ["canBlend"] = section.CanBlend,
                        ["isEnabled"] = section.IsEnabled
                    });
                }
                trackData["sections"] = sectionsArray;
                tracksArray.Add(trackData);
            }
            data["tracks"] = tracksArray;

            // 标记数据
            var markersArray = new List<object>();
            foreach (var marker in sequence.Markers)
            {
                markersArray.Add(new Dictionary<string, object>
                {
                    ["name"] = marker.Name,
                    ["time"] = marker.Time,
                    ["type"] = marker.MarkerType.ToString()
                });
            }
            data["markers"] = markersArray;

            return JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        }

        /// <summary>从 JSON 导入序列。</summary>
        public static Sequence? ImportFromJSON(string json)
        {
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var sequence = new Sequence
            {
                Name = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "",
                Duration = root.TryGetProperty("duration", out var durProp) ? durProp.GetDouble() : 10.0,
                FrameRate = root.TryGetProperty("frameRate", out var fpsProp) ? fpsProp.GetDouble() : 30.0
            };

            return sequence;
        }

        /// <summary>烘焙序列到动画数据（采样所有 Transform 轨道）。</summary>
        public static List<Dictionary<string, object>> BakeToAnimation(
            Sequence sequence,
            double startTime,
            double endTime,
            double frameRate)
        {
            var frames = new List<Dictionary<string, object>>();
            double step = 1.0 / frameRate;

            for (double t = startTime; t <= endTime; t += step)
            {
                var frame = new Dictionary<string, object>
                {
                    ["time"] = t,
                    ["frame"] = SequencerTime.TimeToFrame(t, frameRate),
                    ["tracks"] = new List<object>()
                };

                var trackData = (List<object>)frame["tracks"];
                foreach (var track in sequence.Tracks)
                {
                    trackData.Add(new Dictionary<string, object>
                    {
                        ["trackName"] = track.Name,
                        ["trackType"] = track.TrackTypeName
                    });
                }

                frames.Add(frame);
            }

            return frames;
        }

        /// <summary>导出为 CSV 格式的时间线摘要。</summary>
        public static string ExportTimelineCSV(Sequence sequence)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Type,Name,StartTime,EndTime,Duration,IsEnabled");

            foreach (var track in sequence.Tracks)
            {
                foreach (var section in track.Sections)
                {
                    sb.AppendLine($"Track.{track.TrackTypeName},{section.Name}," +
                        $"{section.StartTime:F3},{section.EndTime:F3},{section.Duration:F3},{section.IsEnabled}");
                }
            }

            foreach (var marker in sequence.Markers)
            {
                sb.AppendLine($"Marker,{marker.Name},{marker.Time:F3},,,{marker.MarkerType}");
            }

            return sb.ToString();
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // SequencePreview - 序列预览
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 序列预览：提供缩略图生成、预览播放和低质量预览功能。
    /// </summary>
    public class SequencePreview
    {
        /// <summary>关联的序列资产。</summary>
        public SequenceAsset? Asset { get; set; }

        /// <summary>缩略图帧（在序列的哪个时间点生成缩略图）。</summary>
        public double ThumbnailTime { get; set; }

        /// <summary>缩略图分辨率。</summary>
        public int ThumbnailWidth { get; set; } = 320;

        /// <summary>缩略图分辨率。</summary>
        public int ThumbnailHeight { get; set; } = 180;

        /// <summary>低质量预览分辨率倍率（相对原始分辨率的百分比）。</summary>
        public float LowQualityScale { get; set; } = 0.25f;

        /// <summary>预览控制器。</summary>
        private SequencerController? _previewController;

        /// <summary>是否正在预览播放。</summary>
        public bool IsPreviewPlaying => _previewController != null &&
            _previewController.TargetSequence?.PlaybackState == SequencePlaybackState.Playing;

        /// <summary>生成缩略图回调。</summary>
        public Func<Sequence, double, int, int, byte[]?>? GenerateThumbnailFunc { get; set; }

        /// <summary>生成缩略图。</summary>
        public byte[]? GenerateThumbnail()
        {
            if (Asset?.SequenceData == null || GenerateThumbnailFunc == null) return null;
            return GenerateThumbnailFunc(Asset.SequenceData, ThumbnailTime, ThumbnailWidth, ThumbnailHeight);
        }

        /// <summary>开始预览播放。</summary>
        public void StartPreview(double startTime = 0, float qualityScale = 0.5f)
        {
            if (Asset?.SequenceData == null) return;

            _previewController = new SequencerController(Asset.SequenceData);
            _previewController.JumpTo(startTime);
            _previewController.Play();
        }

        /// <summary>停止预览播放。</summary>
        public void StopPreview()
        {
            _previewController?.Stop();
            _previewController = null;
        }

        /// <summary>更新预览。</summary>
        public void UpdatePreview(double deltaTime)
        {
            _previewController?.Tick(deltaTime);
        }

        /// <summary>获取预览的当前时间。</summary>
        public double GetPreviewTime()
        {
            return _previewController?.TargetSequence?.CurrentTime ?? 0;
        }

        /// <summary>生成多帧预览条（用于时间轴缩略图条）。</summary>
        public List<byte[]?> GenerateTimelineStrip(int frameCount)
        {
            var result = new List<byte[]?>(frameCount);
            if (Asset?.SequenceData == null || GenerateThumbnailFunc == null)
            {
                for (int i = 0; i < frameCount; i++) result.Add(null);
                return result;
            }

            double duration = Asset.SequenceData.Duration;
            double step = duration / frameCount;

            for (int i = 0; i < frameCount; i++)
            {
                double time = step * i;
                int width = ThumbnailWidth / 4;
                int height = ThumbnailHeight / 4;
                result.Add(GenerateThumbnailFunc(Asset.SequenceData, time, width, height));
            }

            return result;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // SequenceAssetRegistry - 资产注册表
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 序列资产注册表：管理所有序列资产的注册与查找。
    /// </summary>
    public class SequenceAssetRegistry
    {
        /// <summary>所有已注册的资产。</summary>
        private readonly Dictionary<Guid, SequenceAsset> _assets = new();

        /// <summary>所有已注册的模板。</summary>
        private readonly Dictionary<string, SequenceTemplate> _templates = new();

        /// <summary>所有活跃的运行时实例。</summary>
        private readonly Dictionary<Guid, SequenceInstance> _instances = new();

        /// <summary>资产注册事件。</summary>
        public event Action<SequenceAsset>? OnAssetRegistered;

        /// <summary>资产注销事件。</summary>
        public event Action<SequenceAsset>? OnAssetUnregistered;

        /// <summary>获取所有资产。</summary>
        public IReadOnlyCollection<SequenceAsset> Assets => _assets.Values;

        /// <summary>获取所有模板。</summary>
        public IReadOnlyCollection<SequenceTemplate> Templates => _templates.Values;

        /// <summary>获取所有活跃实例。</summary>
        public IReadOnlyCollection<SequenceInstance> Instances => _instances.Values;

        /// <summary>注册资产。</summary>
        public void RegisterAsset(SequenceAsset asset)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            _assets[asset.AssetId] = asset;
            OnAssetRegistered?.Invoke(asset);
        }

        /// <summary>注销资产。</summary>
        public bool UnregisterAsset(Guid assetId)
        {
            if (_assets.Remove(assetId, out var asset))
            {
                OnAssetUnregistered?.Invoke(asset);
                return true;
            }
            return false;
        }

        /// <summary>按 ID 查找资产。</summary>
        public SequenceAsset? FindAsset(Guid assetId)
        {
            return _assets.TryGetValue(assetId, out var asset) ? asset : null;
        }

        /// <summary>按名称查找资产。</summary>
        public SequenceAsset? FindAssetByName(string name)
        {
            foreach (var asset in _assets.Values)
                if (asset.AssetName == name) return asset;
            return null;
        }

        /// <summary>注册模板。</summary>
        public void RegisterTemplate(SequenceTemplate template)
        {
            if (template == null) throw new ArgumentNullException(nameof(template));
            _templates[template.TemplateName] = template;
        }

        /// <summary>按名称查找模板。</summary>
        public SequenceTemplate? FindTemplate(string templateName)
        {
            return _templates.TryGetValue(templateName, out var template) ? template : null;
        }

        /// <summary>创建运行时实例。</summary>
        public SequenceInstance? CreateInstance(SequenceAsset asset, object? context = null)
        {
            var instance = new SequenceInstance();
            if (instance.InstantiateFromAsset(asset, context))
            {
                instance.Initialize();
                _instances[instance.InstanceId] = instance;
                return instance;
            }
            return null;
        }

        /// <summary>通过模板创建实例。</summary>
        public SequenceInstance? CreateInstanceFromTemplate(
            string templateName,
            Dictionary<string, object>? parameters = null,
            object? context = null)
        {
            var template = FindTemplate(templateName);
            if (template == null) return null;

            var sequence = template.Instantiate(parameters);
            if (sequence == null) return null;

            // 创建临时资产
            var tempAsset = new SequenceAsset(templateName)
            {
                SequenceData = sequence
            };

            return CreateInstance(tempAsset, context);
        }

        /// <summary>销毁实例。</summary>
        public bool DestroyInstance(Guid instanceId)
        {
            if (_instances.TryGetValue(instanceId, out var instance))
            {
                instance.Destroy();
                _instances.Remove(instanceId);
                return true;
            }
            return false;
        }

        /// <summary>获取活跃实例数量。</summary>
        public int ActiveInstanceCount => _instances.Count;
    }
}
