// ShanyuEngine - 山雨游戏引擎
// GameplayCueSystem.cs - 提示系统 视觉/音频反馈
// Cue就是GameplayCue 玩家看到的特效 听到的声音 震屏等
// 跟UE的UGameplayCueManager对齐
// 这个系统负责统一管理所有游戏表现的触发

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;

namespace ShanyuEngine.Gameplay
{
    #region Cue枚举和类型

    /// <summary>
    /// Cue类型 表现的类型
    /// </summary>
    public enum CueType : byte
    {
        // 视觉特效 粒子模型等
        Visual = 0,
        // 音效
        Audio = 1,
        // 震屏
        CameraShake = 2,
        // 镜头特效 比如模糊变色
        CameraEffect = 3,
        // UI提示 飘字等
        UI = 4,
        // 物理反馈 比如击退
        Physical = 5,
        // 组合 包含多个子Cue
        Composite = 6,
    }

    /// <summary>
    /// Cue触发时机
    /// </summary>
    public enum CueTriggerMoment : byte
    {
        // 应用时 效果加上时触发
        OnApply = 0,
        // 移除时 效果移除时触发
        OnRemove = 1,
        // 周期触发 持续效果每周期触发一次
        OnPeriod = 2,
        // 激活时 能力激活时触发
        OnActivate = 3,
        // 结束时 能力结束时触发
        OnEnd = 4,
        // 执行时 命中时触发
        OnExecute = 5,
        // 持续 持续播放
        WhileActive = 6,
    }

    /// <summary>
    /// Cue优先级
    /// </summary>
    public enum CuePriority : byte
    {
        // 低优先级 可以被挤掉
        Low = 0,
        // 普通 默认
        Normal = 1,
        // 高优先级
        High = 2,
        // 关键 必须播放
        Critical = 3,
    }

    /// <summary>
    /// Cue播放模式
    /// </summary>
    public enum CuePlayMode : byte
    {
        // 一次性 播完就没了
        OneShot = 0,
        // 循环 持续播放
        Looping = 1,
        // 持续 跟随效果生命周期
        Duration = 2,
    }

    #endregion

    #region Cue参数

    /// <summary>
    /// Cue参数 传递给表现层的数据
    /// </summary>
    public class CueParameters
    {
        // 触发位置 世界坐标
        public Vector3 Location { get; set; }

        // 触发方向
        public Vector3 Direction { get; set; } = Vector3.UnitZ;

        // 触发旋转
        public Quaternion Rotation { get; set; } = Quaternion.Identity;

        // 缩放
        public float Scale { get; set; } = 1f;

        // 来源组件
        public GameplayAbilityComponent? SourceComponent { get; set; }

        // 目标组件
        public GameplayAbilityComponent? TargetComponent { get; set; }

        // 数值参数 比如伤害数值飘字
        public float Magnitude { get; set; }

        // 字符串参数 比如飘字内容
        public string? Text { get; set; }

        // 颜色
        public Vector4 Color { get; set; } = new Vector4(1, 1, 1, 1);

        // 持续时间 -1表示用默认
        public float Duration { get; set; } = -1f;

        // 自定义数据 扩展用
        public Dictionary<string, object?> CustomData { get; } = new();

        // 关联的效果实例ID
        public long EffectInstanceId { get; set; }

        // 关联的能力实例ID
        public long AbilityInstanceId { get; set; }

        // 标签 过滤用
        public List<string> Tags { get; set; } = new();

        /// <summary>
        /// 设置自定义数据
        /// </summary>
        public void SetCustomData(string key, object? value)
        {
            CustomData[key] = value;
        }

        /// <summary>
        /// 获取自定义数据
        /// </summary>
        public T? GetCustomData<T>(string key)
        {
            if (CustomData.TryGetValue(key, out var value) && value is T t)
                return t;
            return default;
        }

        public CueParameters Clone()
        {
            var clone = new CueParameters
            {
                Location = Location,
                Direction = Direction,
                Rotation = Rotation,
                Scale = Scale,
                SourceComponent = SourceComponent,
                TargetComponent = TargetComponent,
                Magnitude = Magnitude,
                Text = Text,
                Color = Color,
                Duration = Duration,
                EffectInstanceId = EffectInstanceId,
                AbilityInstanceId = AbilityInstanceId,
            };

            foreach (var kv in CustomData)
                clone.CustomData[kv.Key] = kv.Value;

            foreach (var t in Tags)
                clone.Tags.Add(t);

            return clone;
        }
    }

    #endregion

    #region Cue规格定义

    /// <summary>
    /// Cue规格 静态描述一个表现
    /// </summary>
    public class GameplayCueSpec
    {
        // 唯一ID
        public int CueId { get; set; }

        // 名字
        public string Name { get; set; } = string.Empty;

        // 类型
        public CueType Type { get; set; } = CueType.Visual;

        // 触发时机
        public CueTriggerMoment TriggerMoment { get; set; } = CueTriggerMoment.OnApply;

        // 优先级
        public CuePriority Priority { get; set; } = CuePriority.Normal;

        // 播放模式
        public CuePlayMode PlayMode { get; set; } = CuePlayMode.OneShot;

        // 资源路径 粒子/音效/模型等
        public string AssetPath { get; set; } = string.Empty;

        // 备选资源路径 随机选一个
        public List<string> AlternateAssetPaths { get; set; } = new();

        // 默认持续时间
        public float DefaultDuration { get; set; } = 1f;

        // 音量 音效用
        public float Volume { get; set; } = 1f;

        // 音调
        public float Pitch { get; set; } = 1f;

        // 是否跟随目标
        public bool AttachToTarget { get; set; } = false;

        // 附着骨骼名字
        public string AttachSocket { get; set; } = string.Empty;

        // 偏移位置
        public Vector3 LocationOffset { get; set; }

        // 偏移旋转
        public Quaternion RotationOffset { get; set; } = Quaternion.Identity;

        // 最远播放距离 超过这个距离不播放 优化用
        public float MaxPlayDistance { get; set; } = 100f;

        // 是否受距离衰减 音量
        public bool DistanceAttenuation { get; set; } = true;

        // 同时最大播放数量 限制同屏特效
        public int MaxConcurrentPlays { get; set; } = 10;

        // 冷却时间 防止刷屏
        public float Cooldown { get; set; } = 0f;

        // 随机延迟 范围内随机
        public float RandomDelayMin { get; set; } = 0f;
        public float RandomDelayMax { get; set; } = 0f;

        // 子Cue列表 组合Cue用
        public List<int> ChildCueIds { get; set; } = new();

        // 条件标签 满足才播放
        public List<string> RequiredTags { get; set; } = new();

        // 排除标签 有这些标签就不播
        public List<string> BlockedTags { get; set; } = new();

        // 元数据
        public Dictionary<string, string> Metadata { get; set; } = new();

        public override string ToString()
        {
            return $"[CueSpec {CueId}:{Name} {Type}]";
        }
    }

    /// <summary>
    /// Cue规格仓库
    /// </summary>
    public class GameplayCueSpecRepository
    {
        private readonly Dictionary<int, GameplayCueSpec> _specs = new();
        private readonly Dictionary<string, int> _nameToId = new();
        private readonly object _lock = new();

        public bool Register(GameplayCueSpec spec)
        {
            if (spec == null || spec.CueId <= 0) return false;

            lock (_lock)
            {
                if (_specs.ContainsKey(spec.CueId)) return false;
                _specs[spec.CueId] = spec;
                if (!string.IsNullOrEmpty(spec.Name))
                    _nameToId[spec.Name] = spec.CueId;
                return true;
            }
        }

        public bool Unregister(int cueId)
        {
            lock (_lock)
            {
                if (!_specs.TryGetValue(cueId, out var spec)) return false;
                _specs.Remove(cueId);
                if (!string.IsNullOrEmpty(spec.Name))
                    _nameToId.Remove(spec.Name);
                return true;
            }
        }

        public GameplayCueSpec? GetById(int cueId)
        {
            lock (_lock)
            {
                _specs.TryGetValue(cueId, out var spec);
                return spec;
            }
        }

        public GameplayCueSpec? GetByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            lock (_lock)
            {
                if (!_nameToId.TryGetValue(name, out var id)) return null;
                _specs.TryGetValue(id, out var spec);
                return spec;
            }
        }

        public List<GameplayCueSpec> GetAll()
        {
            lock (_lock)
            {
                return _specs.Values.ToList();
            }
        }

        public List<GameplayCueSpec> GetByType(CueType type)
        {
            lock (_lock)
            {
                return _specs.Values.Where(s => s.Type == type).ToList();
            }
        }

        public List<GameplayCueSpec> GetByTriggerMoment(CueTriggerMoment moment)
        {
            lock (_lock)
            {
                return _specs.Values.Where(s => s.TriggerMoment == moment).ToList();
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _specs.Clear();
                _nameToId.Clear();
            }
        }

        public int Count
        {
            get { lock (_lock) { return _specs.Count; } }
        }
    }

    #endregion

    #region Cue实例 运行时

    /// <summary>
    /// Cue实例 运行时播放中的Cue
    /// </summary>
    public class GameplayCueInstance
    {
        // 唯一实例ID
        public long InstanceId { get; internal set; }

        // 引用规格
        public GameplayCueSpec Spec { get; }

        // 参数
        public CueParameters Parameters { get; }

        // 播放开始时间
        public float StartTime { get; internal set; }

        // 已经播放时间
        public float ElapsedTime { get; internal set; }

        // 是否还在播放
        public bool IsActive { get; internal set; } = true;

        // 是否已经停止
        public bool IsStopped { get; internal set; }

        // 实例ID自增
        private static long _nextId = 1;

        public GameplayCueInstance(GameplayCueSpec spec, CueParameters parameters)
        {
            Spec = spec ?? throw new ArgumentNullException(nameof(spec));
            Parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
            InstanceId = System.Threading.Interlocked.Increment(ref _nextId);
        }

        /// <summary>
        /// 是否应该自动结束
        /// </summary>
        public bool ShouldAutoEnd
        {
            get
            {
                if (Spec.PlayMode == CuePlayMode.Looping) return false;
                float duration = Parameters.Duration > 0 ? Parameters.Duration : Spec.DefaultDuration;
                return ElapsedTime >= duration;
            }
        }

        /// <summary>
        /// 获取播放进度 0-1
        /// </summary>
        public float GetProgress()
        {
            float duration = Parameters.Duration > 0 ? Parameters.Duration : Spec.DefaultDuration;
            if (duration <= 0f) return 1f;
            return Math.Min(1f, ElapsedTime / duration);
        }

        /// <summary>
        /// 停止播放
        /// </summary>
        public void Stop()
        {
            if (IsStopped) return;
            IsStopped = true;
            IsActive = false;
        }

        public override string ToString()
        {
            return $"[CueInstance {InstanceId} {Spec.Name} {(IsActive ? "Active" : "Stopped")}]";
        }
    }

    #endregion

    #region Cue处理器接口

    /// <summary>
    /// Cue处理器接口 实际执行表现的逻辑
    /// 每种类型有不同的处理器
    /// </summary>
    public interface IGameplayCueHandler
    {
        CueType HandledType { get; }

        /// <summary>
        /// 处理Cue播放
        /// </summary>
        GameplayCueInstance HandlePlay(GameplayCueSpec spec, CueParameters parameters);

        /// <summary>
        /// 处理Cue停止
        /// </summary>
        void HandleStop(GameplayCueInstance instance);

        /// <summary>
        /// 处理Cue更新
        /// </summary>
        void HandleUpdate(GameplayCueInstance instance, float deltaTime);
    }

    /// <summary>
    /// 视觉特效处理器 占位实现
    /// 实际项目里对接粒子系统
    /// </summary>
    public class VisualCueHandler : IGameplayCueHandler
    {
        public CueType HandledType => CueType.Visual;

        public GameplayCueInstance HandlePlay(GameplayCueSpec spec, CueParameters parameters)
        {
            // 实际项目里这里要:
            // 1. 加载粒子资源
            // 2. 创建粒子实例
            // 3. 设置位置旋转
            // 4. 附着到目标(如果需要)
            // 5. 播放

            var instance = new GameplayCueInstance(spec, parameters);
            return instance;
        }

        public void HandleStop(GameplayCueInstance instance)
        {
            // 实际项目里这里要停止粒子销毁实例
            instance.Stop();
        }

        public void HandleUpdate(GameplayCueInstance instance, float deltaTime)
        {
            // 更新粒子位置等
        }
    }

    /// <summary>
    /// 音效处理器 占位实现
    /// </summary>
    public class AudioCueHandler : IGameplayCueHandler
    {
        public CueType HandledType => CueType.Audio;

        public GameplayCueInstance HandlePlay(GameplayCueSpec spec, CueParameters parameters)
        {
            // 实际项目里这里要:
            // 1. 加载音频资源
            // 2. 创建音频源
            // 3. 设置音量音调
            // 4. 设置3D位置(如果需要)
            // 5. 播放

            var instance = new GameplayCueInstance(spec, parameters);
            return instance;
        }

        public void HandleStop(GameplayCueInstance instance)
        {
            instance.Stop();
        }

        public void HandleUpdate(GameplayCueInstance instance, float deltaTime)
        {
            // 更新音频源位置等
        }
    }

    /// <summary>
    /// 震屏处理器 占位实现
    /// </summary>
    public class CameraShakeCueHandler : IGameplayCueHandler
    {
        public CueType HandledType => CueType.CameraShake;

        public GameplayCueInstance HandlePlay(GameplayCueSpec spec, CueParameters parameters)
        {
            // 实际项目里这里要触发相机震动
            var instance = new GameplayCueInstance(spec, parameters);
            return instance;
        }

        public void HandleStop(GameplayCueInstance instance)
        {
            instance.Stop();
        }

        public void HandleUpdate(GameplayCueInstance instance, float deltaTime)
        {
            // 更新震动
        }
    }

    /// <summary>
    /// UI提示处理器 飘字等
    /// </summary>
    public class UICueHandler : IGameplayCueHandler
    {
        public CueType HandledType => CueType.UI;

        public GameplayCueInstance HandlePlay(GameplayCueSpec spec, CueParameters parameters)
        {
            // 实际项目里这里要创建飘字UI
            var instance = new GameplayCueInstance(spec, parameters);
            return instance;
        }

        public void HandleStop(GameplayCueInstance instance)
        {
            instance.Stop();
        }

        public void HandleUpdate(GameplayCueInstance instance, float deltaTime)
        {
            // 更新飘字位置 淡出
        }
    }

    /// <summary>
    /// 组合Cue处理器 同时触发多个子Cue
    /// </summary>
    public class CompositeCueHandler : IGameplayCueHandler
    {
        public CueType HandledType => CueType.Composite;

        private readonly GameplayCueManager _manager;

        public CompositeCueHandler(GameplayCueManager manager)
        {
            _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        }

        public GameplayCueInstance HandlePlay(GameplayCueSpec spec, CueParameters parameters)
        {
            var instance = new GameplayCueInstance(spec, parameters);

            // 触发所有子Cue
            foreach (var childId in spec.ChildCueIds)
            {
                _manager.PlayCue(childId, parameters.Clone());
            }

            return instance;
        }

        public void HandleStop(GameplayCueInstance instance)
        {
            // 子Cue会自己管理生命周期
            instance.Stop();
        }

        public void HandleUpdate(GameplayCueInstance instance, float deltaTime)
        {
        }
    }

    #endregion

    #region Cue管理器

    /// <summary>
    /// Cue管理器 全局管理Cue的播放
    /// </summary>
    public class GameplayCueManager
    {
        // 单例
        private static GameplayCueManager? _instance;
        private static readonly object _instanceLock = new();

        public static GameplayCueManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_instanceLock)
                    {
                        _instance ??= new GameplayCueManager();
                    }
                }
                return _instance;
            }
        }

        // Cue规格仓库
        public GameplayCueSpecRepository Specs { get; } = new();

        // 处理器 按类型索引
        private readonly Dictionary<CueType, IGameplayCueHandler> _handlers = new();
        private readonly object _handlersLock = new();

        // 所有活跃的Cue实例
        private readonly Dictionary<long, GameplayCueInstance> _activeInstances = new();
        private readonly object _instancesLock = new();

        // 按规格ID索引 方便统计
        private readonly Dictionary<int, List<GameplayCueInstance>> _instancesBySpecId = new();

        // 观察者位置 用于距离剔除
        public Vector3 ObserverPosition { get; set; }

        // 随机数生成器
        private readonly Random _random = new();

        // Cue播放事件
        public event Action<GameplayCueInstance>? CuePlayed;
        public event Action<GameplayCueInstance>? CueStopped;

        // 当前时间
        public float CurrentTime { get; private set; }

        // 冷却记录 防止刷屏
        private readonly Dictionary<int, float> _cooldownRecords = new();
        private readonly object _cooldownLock = new();

        private GameplayCueManager()
        {
            // 注册默认处理器
            RegisterHandler(new VisualCueHandler());
            RegisterHandler(new AudioCueHandler());
            RegisterHandler(new CameraShakeCueHandler());
            RegisterHandler(new UICueHandler());
            RegisterHandler(new CompositeCueHandler(this));
        }

        /// <summary>
        /// 注册处理器
        /// </summary>
        public void RegisterHandler(IGameplayCueHandler handler)
        {
            if (handler == null) return;
            lock (_handlersLock)
            {
                _handlers[handler.HandledType] = handler;
            }
        }

        /// <summary>
        /// 播放Cue
        /// </summary>
        public GameplayCueInstance? PlayCue(int cueId, CueParameters? parameters = null)
        {
            var spec = Specs.GetById(cueId);
            if (spec == null) return null;

            return PlayCueInternal(spec, parameters ?? new CueParameters());
        }

        /// <summary>
        /// 按名字播放Cue
        /// </summary>
        public GameplayCueInstance? PlayCueByName(string name, CueParameters? parameters = null)
        {
            var spec = Specs.GetByName(name);
            if (spec == null) return null;

            return PlayCueInternal(spec, parameters ?? new CueParameters());
        }

        /// <summary>
        /// 内部播放逻辑
        /// </summary>
        private GameplayCueInstance? PlayCueInternal(GameplayCueSpec spec, CueParameters parameters)
        {
            // 冷却检查
            if (spec.Cooldown > 0f)
            {
                lock (_cooldownLock)
                {
                    if (_cooldownRecords.TryGetValue(spec.CueId, out var lastTime))
                    {
                        if (CurrentTime - lastTime < spec.Cooldown)
                            return null;
                    }
                    _cooldownRecords[spec.CueId] = CurrentTime;
                }
            }

            // 数量限制
            if (spec.MaxConcurrentPlays > 0)
            {
                int current = 0;
                lock (_instancesLock)
                {
                    if (_instancesBySpecId.TryGetValue(spec.CueId, out var list))
                    {
                        current = list.Count(i => i.IsActive);
                    }
                }

                if (current >= spec.MaxConcurrentPlays)
                    return null;
            }

            // 距离剔除
            if (spec.MaxPlayDistance > 0f && spec.DistanceAttenuation)
            {
                float distance = Vector3.Distance(parameters.Location, ObserverPosition);
                if (distance > spec.MaxPlayDistance)
                    return null;
            }

            // 标签条件检查
            if (spec.RequiredTags.Count > 0)
            {
                var sourceTags = parameters.SourceComponent?.Tags;
                if (sourceTags != null)
                {
                    foreach (var tag in spec.RequiredTags)
                    {
                        if (!sourceTags.HasTag(tag))
                            return null;
                    }
                }
            }

            if (spec.BlockedTags.Count > 0)
            {
                var sourceTags = parameters.SourceComponent?.Tags;
                if (sourceTags != null)
                {
                    foreach (var tag in spec.BlockedTags)
                    {
                        if (sourceTags.HasTag(tag))
                            return null;
                    }
                }
            }

            // 随机延迟
            if (spec.RandomDelayMax > 0f)
            {
                // 简化版 不实际延迟 直接调整开始时间
                // 实际项目里要加入延迟队列
            }

            // 应用位置偏移
            if (spec.LocationOffset != Vector3.Zero)
            {
                parameters.Location += spec.LocationOffset;
            }

            // 应用音量衰减
            if (spec.DistanceAttenuation && spec.Type == CueType.Audio)
            {
                float distance = Vector3.Distance(parameters.Location, ObserverPosition);
                if (distance > 0f && spec.MaxPlayDistance > 0f)
                {
                    float attenuation = 1f - (distance / spec.MaxPlayDistance);
                    parameters.Magnitude = Math.Max(0f, attenuation);
                }
            }

            // 找到处理器
            IGameplayCueHandler? handler;
            lock (_handlersLock)
            {
                _handlers.TryGetValue(spec.Type, out handler);
            }

            if (handler == null)
                return null;

            // 处理播放
            var instance = handler.HandlePlay(spec, parameters);
            instance.StartTime = CurrentTime;

            lock (_instancesLock)
            {
                _activeInstances[instance.InstanceId] = instance;

                if (!_instancesBySpecId.TryGetValue(spec.CueId, out var list))
                {
                    list = new List<GameplayCueInstance>();
                    _instancesBySpecId[spec.CueId] = list;
                }
                list.Add(instance);
            }

            CuePlayed?.Invoke(instance);
            return instance;
        }

        /// <summary>
        /// 停止Cue
        /// </summary>
        public bool StopCue(long instanceId)
        {
            GameplayCueInstance? instance;
            lock (_instancesLock)
            {
                if (!_activeInstances.TryGetValue(instanceId, out instance))
                    return false;
            }

            return StopCueInternal(instance);
        }

        /// <summary>
        /// 停止指定规格的所有Cue
        /// </summary>
        public int StopAllBySpecId(int cueId)
        {
            List<GameplayCueInstance> toStop;
            lock (_instancesLock)
            {
                if (!_instancesBySpecId.TryGetValue(cueId, out var list))
                    return 0;
                toStop = list.Where(i => i.IsActive).ToList();
            }

            int count = 0;
            foreach (var inst in toStop)
            {
                if (StopCueInternal(inst))
                    count++;
            }
            return count;
        }

        /// <summary>
        /// 内部停止逻辑
        /// </summary>
        private bool StopCueInternal(GameplayCueInstance instance)
        {
            if (instance == null || instance.IsStopped) return false;

            IGameplayCueHandler? handler;
            lock (_handlersLock)
            {
                _handlers.TryGetValue(instance.Spec.Type, out handler);
            }

            handler?.HandleStop(instance);

            lock (_instancesLock)
            {
                _activeInstances.Remove(instance.InstanceId);

                if (_instancesBySpecId.TryGetValue(instance.Spec.CueId, out var list))
                {
                    list.Remove(instance);
                    if (list.Count == 0)
                        _instancesBySpecId.Remove(instance.Spec.CueId);
                }
            }

            CueStopped?.Invoke(instance);
            return true;
        }

        /// <summary>
        /// 停止所有Cue
        /// </summary>
        public void StopAll()
        {
            List<GameplayCueInstance> toStop;
            lock (_instancesLock)
            {
                toStop = _activeInstances.Values.ToList();
            }

            foreach (var inst in toStop)
            {
                StopCueInternal(inst);
            }
        }

        /// <summary>
        /// 获取所有活跃Cue
        /// </summary>
        public List<GameplayCueInstance> GetAllActiveCues()
        {
            lock (_instancesLock)
            {
                return _activeInstances.Values.ToList();
            }
        }

        /// <summary>
        /// 活跃Cue数量
        /// </summary>
        public int ActiveCueCount
        {
            get
            {
                lock (_instancesLock)
                {
                    return _activeInstances.Count;
                }
            }
        }

        /// <summary>
        /// Tick更新
        /// </summary>
        public void Tick(float deltaTime)
        {
            CurrentTime += deltaTime;

            List<GameplayCueInstance> snapshot;
            lock (_instancesLock)
            {
                snapshot = _activeInstances.Values.ToList();
            }

            List<long> toRemove = new();

            foreach (var instance in snapshot)
            {
                instance.ElapsedTime += deltaTime;

                // 找到处理器更新
                IGameplayCueHandler? handler;
                lock (_handlersLock)
                {
                    _handlers.TryGetValue(instance.Spec.Type, out handler);
                }

                handler?.HandleUpdate(instance, deltaTime);

                // 检查是否应该结束
                if (instance.ShouldAutoEnd)
                {
                    toRemove.Add(instance.InstanceId);
                }
            }

            // 移除结束的
            foreach (var id in toRemove)
            {
                StopCue(id);
            }

            // 清理过期的冷却记录
            if (CurrentTime % 10f < deltaTime)
            {
                CleanupCooldowns();
            }
        }

        /// <summary>
        /// 清理过期冷却记录
        /// </summary>
        private void CleanupCooldowns()
        {
            lock (_cooldownLock)
            {
                var toRemove = _cooldownRecords
                    .Where(kv => CurrentTime - kv.Value > 60f)
                    .Select(kv => kv.Key)
                    .ToList();

                foreach (var id in toRemove)
                {
                    _cooldownRecords.Remove(id);
                }
            }
        }

        /// <summary>
        /// 关闭系统
        /// </summary>
        public void Shutdown()
        {
            StopAll();
            Specs.Clear();

            lock (_handlersLock)
            {
                _handlers.Clear();
            }

            lock (_cooldownLock)
            {
                _cooldownRecords.Clear();
            }
        }
    }

    #endregion

    #region Cue事件 与GAS集成

    /// <summary>
    /// Cue事件 由能力/效果系统触发
    /// </summary>
    public struct CueEvent
    {
        public int CueId { get; set; }
        public CueTriggerMoment Moment { get; set; }
        public CueParameters Parameters { get; set; }

        public CueEvent(int cueId, CueTriggerMoment moment, CueParameters parameters)
        {
            CueId = cueId;
            Moment = moment;
            Parameters = parameters;
        }
    }

    /// <summary>
    /// Cue事件处理器 把GAS事件转换为Cue播放
    /// </summary>
    public class CueEventHandler
    {
        private readonly GameplayCueManager _manager;

        public CueEventHandler(GameplayCueManager manager)
        {
            _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        }

        /// <summary>
        /// 处理效果应用事件
        /// </summary>
        public void OnEffectApplied(GameplayEffectContainerFull container, GameplayEffectInstance instance)
        {
            foreach (var cueId in instance.Spec.OnApplyCueIds)
            {
                var parameters = new CueParameters
                {
                    EffectInstanceId = instance.InstanceId,
                    Magnitude = instance.StackCount,
                };

                _manager.PlayCue(cueId, parameters);
            }
        }

        /// <summary>
        /// 处理效果移除事件
        /// </summary>
        public void OnEffectRemoved(GameplayEffectContainerFull container, GameplayEffectInstance instance, EffectRemoveReason reason)
        {
            foreach (var cueId in instance.Spec.OnRemoveCueIds)
            {
                var parameters = new CueParameters
                {
                    EffectInstanceId = instance.InstanceId,
                };

                _manager.PlayCue(cueId, parameters);
            }
        }

        /// <summary>
        /// 处理效果周期触发事件
        /// </summary>
        public void OnEffectPeriodTriggered(GameplayEffectContainerFull container, GameplayEffectInstance instance)
        {
            foreach (var cueId in instance.Spec.OnPeriodCueIds)
            {
                var parameters = new CueParameters
                {
                    EffectInstanceId = instance.InstanceId,
                    Magnitude = instance.StackCount,
                };

                _manager.PlayCue(cueId, parameters);
            }
        }

        /// <summary>
        /// 处理能力激活事件
        /// </summary>
        public void OnAbilityActivated(GameplayAbilityComponent component, AbilityInstance instance, AbilityActivationResult result)
        {
            if (result != AbilityActivationResult.Success) return;

            foreach (var cueId in instance.Spec.CueIds)
            {
                var parameters = new CueParameters
                {
                    SourceComponent = component,
                    AbilityInstanceId = instance.InstanceId,
                };

                _manager.PlayCue(cueId, parameters);
            }
        }
    }

    #endregion

    #region Cue预设工厂

    /// <summary>
    /// Cue预设工厂 创建常用Cue
    /// </summary>
    public static class GameplayCueFactory
    {
        /// <summary>
        /// 创建伤害飘字Cue
        /// </summary>
        public static GameplayCueSpec CreateDamageNumberCue(int cueId)
        {
            return new GameplayCueSpec
            {
                CueId = cueId,
                Name = "DamageNumber",
                Type = CueType.UI,
                TriggerMoment = CueTriggerMoment.OnExecute,
                PlayMode = CuePlayMode.OneShot,
                AssetPath = "UI/FloatingText/Damage",
                DefaultDuration = 1.5f,
                MaxConcurrentPlays = 50,
            };
        }

        /// <summary>
        /// 创建治疗飘字Cue
        /// </summary>
        public static GameplayCueSpec CreateHealNumberCue(int cueId)
        {
            return new GameplayCueSpec
            {
                CueId = cueId,
                Name = "HealNumber",
                Type = CueType.UI,
                TriggerMoment = CueTriggerMoment.OnExecute,
                PlayMode = CuePlayMode.OneShot,
                AssetPath = "UI/FloatingText/Heal",
                DefaultDuration = 1.5f,
                MaxConcurrentPlays = 50,
            };
        }

        /// <summary>
        /// 创建命中特效
        /// </summary>
        public static GameplayCueSpec CreateHitEffectCue(int cueId)
        {
            return new GameplayCueSpec
            {
                CueId = cueId,
                Name = "HitEffect",
                Type = CueType.Visual,
                TriggerMoment = CueTriggerMoment.OnExecute,
                PlayMode = CuePlayMode.OneShot,
                AssetPath = "VFX/Hit/Default",
                DefaultDuration = 0.5f,
                MaxConcurrentPlays = 30,
                MaxPlayDistance = 50f,
            };
        }

        /// <summary>
        /// 创建火球特效
        /// </summary>
        public static GameplayCueSpec CreateFireballCue(int cueId)
        {
            return new GameplayCueSpec
            {
                CueId = cueId,
                Name = "FireballEffect",
                Type = CueType.Composite,
                TriggerMoment = CueTriggerMoment.OnActivate,
                PlayMode = CuePlayMode.OneShot,
                DefaultDuration = 1f,
                ChildCueIds = new List<int>(),
            };
        }

        /// <summary>
        /// 创建受击音效
        /// </summary>
        public static GameplayCueSpec CreateHitSoundCue(int cueId)
        {
            return new GameplayCueSpec
            {
                CueId = cueId,
                Name = "HitSound",
                Type = CueType.Audio,
                TriggerMoment = CueTriggerMoment.OnExecute,
                PlayMode = CuePlayMode.OneShot,
                AssetPath = "Audio/SFX/Hit/Default",
                DefaultDuration = 0.5f,
                Volume = 0.8f,
                MaxConcurrentPlays = 20,
                MaxPlayDistance = 30f,
                DistanceAttenuation = true,
            };
        }

        /// <summary>
        /// 创建施法音效
        /// </summary>
        public static GameplayCueSpec CreateCastSoundCue(int cueId)
        {
            return new GameplayCueSpec
            {
                CueId = cueId,
                Name = "CastSound",
                Type = CueType.Audio,
                TriggerMoment = CueTriggerMoment.OnActivate,
                PlayMode = CuePlayMode.OneShot,
                AssetPath = "Audio/SFX/Cast/Default",
                DefaultDuration = 0.3f,
                Volume = 1f,
                MaxPlayDistance = 40f,
                DistanceAttenuation = true,
            };
        }

        /// <summary>
        /// 创建震屏Cue
        /// </summary>
        public static GameplayCueSpec CreateCameraShakeCue(int cueId, float intensity)
        {
            return new GameplayCueSpec
            {
                CueId = cueId,
                Name = "CameraShake",
                Type = CueType.CameraShake,
                TriggerMoment = CueTriggerMoment.OnExecute,
                PlayMode = CuePlayMode.OneShot,
                DefaultDuration = 0.3f,
                Priority = CuePriority.High,
                Metadata = new Dictionary<string, string>
                {
                    { "Intensity", intensity.ToString() }
                },
            };
        }

        /// <summary>
        /// 创建buff光环特效
        /// </summary>
        public static GameplayCueSpec CreateBuffAuraCue(int cueId)
        {
            return new GameplayCueSpec
            {
                CueId = cueId,
                Name = "BuffAura",
                Type = CueType.Visual,
                TriggerMoment = CueTriggerMoment.OnApply,
                PlayMode = CuePlayMode.Duration,
                AssetPath = "VFX/Aura/Buff",
                DefaultDuration = 5f,
                AttachToTarget = true,
                MaxConcurrentPlays = 10,
            };
        }

        /// <summary>
        /// 创建debuff特效
        /// </summary>
        public static GameplayCueSpec CreateDebuffEffectCue(int cueId)
        {
            return new GameplayCueSpec
            {
                CueId = cueId,
                Name = "DebuffEffect",
                Type = CueType.Visual,
                TriggerMoment = CueTriggerMoment.OnApply,
                PlayMode = CuePlayMode.Duration,
                AssetPath = "VFX/Status/Debuff",
                DefaultDuration = 5f,
                AttachToTarget = true,
                AttachSocket = "Root",
            };
        }

        /// <summary>
        /// 创建死亡特效
        /// </summary>
        public static GameplayCueSpec CreateDeathEffectCue(int cueId)
        {
            return new GameplayCueSpec
            {
                CueId = cueId,
                Name = "DeathEffect",
                Type = CueType.Composite,
                TriggerMoment = CueTriggerMoment.OnExecute,
                PlayMode = CuePlayMode.OneShot,
                DefaultDuration = 2f,
                Priority = CuePriority.Critical,
            };
        }

        /// <summary>
        /// 获取所有预设
        /// </summary>
        public static List<GameplayCueSpec> GetAllPresets()
        {
            return new List<GameplayCueSpec>
            {
                CreateDamageNumberCue(3001),
                CreateHealNumberCue(3002),
                CreateHitEffectCue(3003),
                CreateFireballCue(3004),
                CreateHitSoundCue(3005),
                CreateCastSoundCue(3006),
                CreateCameraShakeCue(3007, 1f),
                CreateBuffAuraCue(3008),
                CreateDebuffEffectCue(3009),
                CreateDeathEffectCue(3010),
            };
        }

        /// <summary>
        /// 注册所有预设
        /// </summary>
        public static void RegisterAll(GameplayCueManager manager)
        {
            foreach (var spec in GetAllPresets())
            {
                manager.Specs.Register(spec);
            }
        }
    }

    #endregion

    #region Cue通知器 简化触发

    /// <summary>
    /// Cue通知器 提供简化的触发接口
    /// </summary>
    public static class GameplayCueNotifier
    {
        private static GameplayCueManager? _manager;

        public static void Initialize(GameplayCueManager manager)
        {
            _manager = manager;
        }

        /// <summary>
        /// 触发伤害飘字
        /// </summary>
        public static void NotifyDamage(Vector3 location, float damage, bool isCrit = false)
        {
            if (_manager == null) return;

            var parameters = new CueParameters
            {
                Location = location,
                Magnitude = damage,
                Text = ((int)damage).ToString(),
                Color = isCrit ? new Vector4(1, 0.8f, 0, 1) : new Vector4(1, 1, 1, 1),
                CustomData = { { "IsCrit", isCrit } },
            };

            _manager.PlayCueByName("DamageNumber", parameters);
        }

        /// <summary>
        /// 触发治疗飘字
        /// </summary>
        public static void NotifyHeal(Vector3 location, float heal)
        {
            if (_manager == null) return;

            var parameters = new CueParameters
            {
                Location = location,
                Magnitude = heal,
                Text = "+" + ((int)heal).ToString(),
                Color = new Vector4(0, 1, 0, 1),
            };

            _manager.PlayCueByName("HealNumber", parameters);
        }

        /// <summary>
        /// 触发命中特效
        /// </summary>
        public static void NotifyHit(Vector3 location, Vector3 direction)
        {
            if (_manager == null) return;

            var parameters = new CueParameters
            {
                Location = location,
                Direction = direction,
            };

            _manager.PlayCueByName("HitEffect", parameters);
            _manager.PlayCueByName("HitSound", parameters);
        }

        /// <summary>
        /// 触发施法特效
        /// </summary>
        public static void NotifyCast(Vector3 location, Vector3 direction)
        {
            if (_manager == null) return;

            var parameters = new CueParameters
            {
                Location = location,
                Direction = direction,
            };

            _manager.PlayCueByName("CastSound", parameters);
        }

        /// <summary>
        /// 触发震屏
        /// </summary>
        public static void NotifyCameraShake(float intensity)
        {
            if (_manager == null) return;

            var parameters = new CueParameters
            {
                Magnitude = intensity,
            };

            _manager.PlayCueByName("CameraShake", parameters);
        }
    }

    #endregion

    #region Cue统计和分析

    /// <summary>
    /// Cue统计信息
    /// </summary>
    public class CueStatistics
    {
        public int TotalSpecs { get; set; }
        public int ActiveInstances { get; set; }
        public Dictionary<CueType, int> ActiveByType { get; set; } = new();
        public Dictionary<int, int> PlayCountBySpec { get; set; } = new();
        public List<string> TopPlayedCues { get; set; } = new();
    }

    /// <summary>
    /// Cue分析器
    /// </summary>
    public class CueAnalyzer
    {
        private readonly GameplayCueManager _manager;

        // 播放计数
        private readonly Dictionary<int, int> _playCounts = new();
        private readonly object _countLock = new();

        public CueAnalyzer(GameplayCueManager manager)
        {
            _manager = manager ?? throw new ArgumentNullException(nameof(manager));
            _manager.CuePlayed += OnCuePlayed;
        }

        private void OnCuePlayed(GameplayCueInstance instance)
        {
            lock (_countLock)
            {
                _playCounts[instance.Spec.CueId] = _playCounts.GetValueOrDefault(instance.Spec.CueId) + 1;
            }
        }

        /// <summary>
        /// 收集统计
        /// </summary>
        public CueStatistics CollectStatistics()
        {
            var stats = new CueStatistics
            {
                TotalSpecs = _manager.Specs.Count,
                ActiveInstances = _manager.ActiveCueCount,
            };

            // 按类型统计
            var activeCues = _manager.GetAllActiveCues();
            foreach (var cue in activeCues)
            {
                stats.ActiveByType[cue.Spec.Type] = stats.ActiveByType.GetValueOrDefault(cue.Spec.Type) + 1;
            }

            // 播放计数
            lock (_countLock)
            {
                stats.PlayCountBySpec = new Dictionary<int, int>(_playCounts);
                stats.TopPlayedCues = _playCounts
                    .OrderByDescending(kv => kv.Value)
                    .Take(10)
                    .Select(kv =>
                    {
                        var spec = _manager.Specs.GetById(kv.Key);
                        return $"{spec?.Name ?? kv.Key.ToString()}: {kv.Value}";
                    })
                    .ToList();
            }

            return stats;
        }

        /// <summary>
        /// 导出报告
        /// </summary>
        public string ExportReport()
        {
            var stats = CollectStatistics();
            var sb = new StringBuilder();

            sb.AppendLine("=== Cue System Statistics ===");
            sb.AppendLine($"Total Specs: {stats.TotalSpecs}");
            sb.AppendLine($"Active Instances: {stats.ActiveInstances}");
            sb.AppendLine();

            sb.AppendLine("--- Active by Type ---");
            foreach (var kv in stats.ActiveByType)
            {
                sb.AppendLine($"  {kv.Key}: {kv.Value}");
            }

            sb.AppendLine();
            sb.AppendLine("--- Top Played Cues ---");
            foreach (var s in stats.TopPlayedCues)
            {
                sb.AppendLine($"  {s}");
            }

            return sb.ToString();
        }

        /// <summary>
        /// 重置统计
        /// </summary>
        public void Reset()
        {
            lock (_countLock)
            {
                _playCounts.Clear();
            }
        }
    }

    #endregion

    #region Cue系统初始化辅助

    /// <summary>
    /// Cue系统启动器
    /// </summary>
    public static class CueSystemBootstrap
    {
        public static GameplayCueManager Initialize()
        {
            var manager = GameplayCueManager.Instance;
            GameplayCueFactory.RegisterAll(manager);
            GameplayCueNotifier.Initialize(manager);
            return manager;
        }

        public static string RunSmokeTest()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Cue System Smoke Test ===");

            var manager = Initialize();
            sb.AppendLine($"System initialized with {manager.Specs.Count} specs");

            // 设置观察者位置
            manager.ObserverPosition = Vector3.Zero;

            // 播放伤害飘字
            var damageParams = new CueParameters
            {
                Location = new Vector3(0, 0, 0),
                Magnitude = 50f,
                Text = "50",
            };

            var instance = manager.PlayCueByName("DamageNumber", damageParams);
            sb.AppendLine($"Played DamageNumber: {instance != null}");

            // 播放命中特效
            var hitParams = new CueParameters
            {
                Location = new Vector3(10, 0, 0),
                Direction = Vector3.UnitX,
            };

            instance = manager.PlayCueByName("HitEffect", hitParams);
            sb.AppendLine($"Played HitEffect: {instance != null}");

            // 播放音效
            instance = manager.PlayCueByName("HitSound", hitParams);
            sb.AppendLine($"Played HitSound: {instance != null}");

            // Tick几次
            for (int i = 0; i < 10; i++)
            {
                manager.Tick(0.1f);
            }

            sb.AppendLine($"Active cues after 1s: {manager.ActiveCueCount}");

            // 使用通知器
            GameplayCueNotifier.NotifyDamage(new Vector3(5, 0, 0), 100f, true);
            GameplayCueNotifier.NotifyHeal(new Vector3(5, 0, 0), 30f);
            GameplayCueNotifier.NotifyHit(new Vector3(5, 0, 0), Vector3.UnitX);

            sb.AppendLine($"After notifier calls, active cues: {manager.ActiveCueCount}");

            // 距离剔除测试
            var farParams = new CueParameters
            {
                Location = new Vector3(1000, 0, 0),
            };
            instance = manager.PlayCueByName("HitEffect", farParams);
            sb.AppendLine($"Far cue played (should be null): {instance == null}");

            manager.Shutdown();
            sb.AppendLine("Test complete");
            return sb.ToString();
        }
    }

    #endregion

} // namespace ShanyuEngine.Gameplay
