// ============================================================================
//  YamaEngine.AI.AIPerceptionSystem.cs
//  yama引擎 -- AI 感知系统 (AI Perception System)
//  对齐 UE AI Perception / AI Sense / AI Stimulus
// ----------------------------------------------------------------------------
//  完整的 AI 感知系统，包含：
//    1. AIPerceptionComponent  - 感知组件 (附着到Pawn)
//    2. AISense               - 感官基类 (更新/注册目标/配置)
//    3. AISense_Sight         - 视觉 (视距/视野角度/视锥/后视忽略/环境遮挡)
//    4. AISense_Hearing       - 听觉 (听觉范围/噪音强度/年龄衰减/声音事件)
//    5. AISense_Damage        - 伤害感知 (来源/方向/强度)
//    6. AISense_Touch        - 触觉 (接触碰撞触发)
//    7. AISense_Prediction    - 预测 (预测目标移动轨迹)
//    8. AIStimulus            - 刺激 (来源Actor/位置/年龄/强度/感官类型)
//    9. AIPerceptionEvent     - 感知事件 (新增/更新/丢失目标/配置变更)
//   10. AIPerceptionConfig    - 感知配置 (每个感官的灵敏度/范围/角度)
//   11. 团队感知 (同一队伍共享感知信息)
//   12. 感知调试可视化
// ============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.AI
{
    // ========================================================================
    //  一、枚举
    // ========================================================================

    /// <summary>
    /// 感知状态。
    /// </summary>
    public enum AIPerceptionState
    {
        Unknown,
        Sensed,
        Lost
    }

    /// <summary>
    /// 感知事件类型。
    /// </summary>
    public enum AIPerceptionEventType
    {
        TargetAdded,
        TargetUpdated,
        TargetLost,
        ConfigChanged
    }

    /// <summary>
    /// 感官类型标识。
    /// </summary>
    public enum AISenseType
    {
        None = 0,
        Sight = 1,
        Hearing = 2,
        Damage = 4,
        Touch = 8,
        Prediction = 16
    }

    /// <summary>
    /// 团队ID。
    /// </summary>
    public enum AITeamId
    {
        None = 0,
        TeamA = 1,
        TeamB = 2,
        TeamC = 3,
        Neutral = 99
    }

    // ========================================================================
    //  二、AI 刺激 (AIStimulus)
    // ========================================================================

    /// <summary>
    /// AI 刺激。描述一个感知事件的全部信息。
    /// </summary>
    public sealed class AIStimulus
    {
        /// <summary>来源 Actor。</summary>
        public YamaEngine.GameFramework.Actor Source { get; set; }
        /// <summary>刺激位置（世界坐标）。</summary>
        public Vector3 Position { get; set; }
        /// <summary>刺激年龄（秒），从产生至今经过的时间。</summary>
        public float Age { get; set; }
        /// <summary>刺激强度 (0~1)。</summary>
        public float Intensity { get; set; } = 1f;
        /// <summary>感官类型。</summary>
        public AISenseType SenseType { get; set; }
        /// <summary>感知状态。</summary>
        public AIPerceptionState PerceptionState { get; set; }
        /// <summary>刺激方向（从感知者指向来源）。</summary>
        public Vector3 Direction { get; set; }
        /// <summary>创建时间戳。</summary>
        public float Timestamp { get; private set; }

        public AIStimulus()
        {
            Timestamp = 0f;
        }

        public AIStimulus(YamaEngine.GameFramework.Actor source, Vector3 position, AISenseType senseType,
            float intensity = 1f, Vector3 direction = default)
        {
            Source = source;
            Position = position;
            SenseType = senseType;
            Intensity = intensity;
            Direction = direction;
            Age = 0f;
        }

        public void ResetAge(float currentTime)
        {
            Age = currentTime - Timestamp;
        }

        /// <summary>更新时间戳（由感知系统在重新注册/刷新时调用）。</summary>
        public void SetTimestamp(float currentTime)
        {
            Timestamp = currentTime;
        }
    }

    // ========================================================================
    //  三、感知事件 (AIPerceptionEvent)
    // ========================================================================

    /// <summary>
    /// 感知事件。通知感知组件目标变化。
    /// </summary>
    public sealed class AIPerceptionEvent
    {
        public AIPerceptionEventType EventType { get; set; }
        public YamaEngine.GameFramework.Actor Target { get; set; }
        public AIStimulus Stimulus { get; set; }
        public AIPerceptionComponent Sender { get; set; }

        public AIPerceptionEvent() { }

        public AIPerceptionEvent(AIPerceptionEventType type, YamaEngine.GameFramework.Actor target,
            AIStimulus stimulus, AIPerceptionComponent sender)
        {
            EventType = type;
            Target = target;
            Stimulus = stimulus;
            Sender = sender;
        }
    }

    // ========================================================================
    //  四、感知配置 (AIPerceptionConfig)
    // ========================================================================

    /// <summary>
    /// 单个感官的配置。
    /// </summary>
    public sealed class AISenseConfig
    {
        public bool Enabled { get; set; } = true;
        public float Sensitivity { get; set; } = 1f;
        public float Range { get; set; } = 100f;
        public float Angle { get; set; } = 90f;
        public float MaxAge { get; set; } = 5f;
        public float MinIntensity { get; set; } = 0.1f;
        public bool DebugDraw { get; set; } = false;
    }

    /// <summary>
    /// 感知组件配置集合。
    /// </summary>
    public sealed class AIPerceptionConfig
    {
        public AISenseConfig SightConfig { get; set; } = new AISenseConfig
        {
            Range = 500f, Angle = 90f, MaxAge = 3f, Sensitivity = 1f
        };

        public AISenseConfig HearingConfig { get; set; } = new AISenseConfig
        {
            Range = 200f, Angle = 360f, MaxAge = 5f, Sensitivity = 1f
        };

        public AISenseConfig DamageConfig { get; set; } = new AISenseConfig
        {
            Range = 500f, Angle = 360f, MaxAge = 10f, Sensitivity = 1.5f
        };

        public AISenseConfig TouchConfig { get; set; } = new AISenseConfig
        {
            Range = 2f, Angle = 360f, MaxAge = 1f, Sensitivity = 1f
        };

        public AISenseConfig PredictionConfig { get; set; } = new AISenseConfig
        {
            Range = 300f, Angle = 360f, MaxAge = 2f, Sensitivity = 0.8f
        };
    }

    // ========================================================================
    //  五、感官基类 (AISense)
    // ========================================================================

    /// <summary>
    /// AI 感官基类。定义感官的更新、注册、配置接口。
    /// </summary>
    public abstract class AISense
    {
        public AISenseType SenseType { get; protected set; } = AISenseType.None;
        public string SenseName { get; protected set; } = "UnnamedSense";
        public bool IsEnabled { get; set; } = true;

        /// <summary>更新感官（每帧调用）。</summary>
        public abstract void Update(AIPerceptionComponent perceptionComponent, float deltaTime, float currentTime);

        /// <summary>注册一个可被感知的目标。</summary>
        public virtual void RegisterTarget(YamaEngine.GameFramework.Actor target) { }

        /// <summary>注销一个目标。</summary>
        public virtual void UnregisterTarget(YamaEngine.GameFramework.Actor target) { }

        /// <summary>配置感官。</summary>
        public virtual void Configure(AISenseConfig config) { }

        /// <summary>调试绘制。</summary>
        public virtual void DebugDraw(AIPerceptionComponent perceptionComponent,
            Action<string, Vector3, Vector3> drawLine,
            Action<Vector3, float, Vector3> drawSphere) { }
    }

    // ========================================================================
    //  六、视觉感官 (AISense_Sight)
    // ========================================================================

    /// <summary>
    /// 视觉感官。视距/视野角度/视锥/后视忽略/环境遮挡检测/灯光影响。
    /// </summary>
    public sealed class AISense_Sight : AISense
    {
        /// <summary>视距。</summary>
        public float SightRange { get; set; } = 500f;
        /// <summary>视野角度（度）。</summary>
        public float FieldOfView { get; set; } = 90f;
        /// <summary>后视距离（视锥之外的感知距离）。</summary>
        public float PeripheralVision { get; set; } = 0f;
        /// <summary>感知年龄限制（秒）。</summary>
        public float MaxAge { get; set; } = 3f;
        /// <summary>最小感知强度。</summary>
        public float MinIntensity { get; set; } = 0.1f;
        /// <summary>是否忽略友方。</summary>
        public bool IgnoreFriendly { get; set; } = true;
        /// <summary>是否启用环境遮挡检测。</summary>
        public bool EnableOcclusionCheck { get; set; } = true;
        /// <summary>是否受灯光影响。</summary>
        public bool EnableLightInfluence { get; set; } = false;
        /// <summary>灯光影响系数。</summary>
        public float LightInfluence { get; set; } = 0.5f;

        /// <summary>射线检测回调。</summary>
        public Func<Vector3, Vector3, bool> RaycastCheck { get; set; }

        public AISense_Sight()
        {
            SenseType = AISenseType.Sight;
            SenseName = "Sight";
        }

        public override void Configure(AISenseConfig config)
        {
            SightRange = config.Range;
            FieldOfView = config.Angle;
            MaxAge = config.MaxAge;
            MinIntensity = config.MinIntensity;
        }

        public override void Update(AIPerceptionComponent perceptionComponent, float deltaTime, float currentTime)
        {
            if (!IsEnabled) return;

            var owner = perceptionComponent.OwnerPawn;
            if (owner == null) return;

            Vector3 ownerPos = owner.Position;
            Quaternion ownerRot = owner.Rotation;
            Vector3 forward = GetForward(ownerRot);
            float fovRad = (FieldOfView / 2f) * (MathF.PI / 180f);

            foreach (var target in perceptionComponent.RegisteredTargets)
            {
                if (target == null) continue;

                // 友方忽略
                if (IgnoreFriendly && perceptionComponent.IsSameTeam(target))
                    continue;

                Vector3 targetPos = target.Position;
                Vector3 toTarget = targetPos - ownerPos;
                float distance = toTarget.Length();

                // 视距检查
                if (distance > SightRange) continue;

                // 视锥检查
                float angle = AngleBetween(forward, toTarget);
                bool inFov = angle <= fovRad;
                bool inPeripheral = !inFov && PeripheralVision > 0f && distance <= PeripheralVision;

                if (!inFov && !inPeripheral) continue;

                // 遮挡检测
                if (EnableOcclusionCheck && RaycastCheck != null)
                {
                    if (RaycastCheck(ownerPos + Vector3.UnitY, targetPos + Vector3.UnitY))
                        continue; // 被遮挡
                }

                // 计算强度
                float intensity = 1f - (distance / SightRange);
                if (!inFov && inPeripheral)
                    intensity *= 0.3f;

                // 灯光影响
                if (EnableLightInfluence)
                    intensity *= LightInfluence;

                intensity = Math.Clamp(intensity, 0f, 1f);
                if (intensity < MinIntensity) continue;

                var direction = distance > 0.001f ? Vector3.Normalize(toTarget) : forward;
                var stimulus = new AIStimulus(target, targetPos, AISenseType.Sight, intensity, direction);
                perceptionComponent.RegisterStimulus(target, stimulus);
            }
        }

        public override void DebugDraw(AIPerceptionComponent perceptionComponent,
            Action<string, Vector3, Vector3> drawLine,
            Action<Vector3, float, Vector3> drawSphere)
        {
            var owner = perceptionComponent.OwnerPawn;
            if (owner == null) return;

            Vector3 pos = owner.Position + Vector3.UnitY;
            Vector3 forward = GetForward(owner.Rotation);
            float fovRad = (FieldOfView / 2f) * (MathF.PI / 180f);

            // 绘制视锥
            float range = SightRange;
            Vector3 left = RotateY(forward, fovRad) * range;
            Vector3 right = RotateY(forward, -fovRad) * range;

            drawLine?.Invoke("Sight", pos, pos + left);
            drawLine?.Invoke("Sight", pos, pos + right);
            drawLine?.Invoke("Sight", pos + left, pos + right);
        }

        private static Vector3 GetForward(Quaternion rotation)
        {
            return Vector3.Transform(new Vector3(0, 0, 1), rotation);
        }

        private static float AngleBetween(Vector3 from, Vector3 to)
        {
            float dot = Vector3.Dot(Vector3.Normalize(from), Vector3.Normalize(to));
            return MathF.Acos(Math.Clamp(dot, -1f, 1f));
        }

        private static Vector3 RotateY(Vector3 v, float angle)
        {
            float cos = MathF.Cos(angle);
            float sin = MathF.Sin(angle);
            return new Vector3(v.X * cos + v.Z * sin, v.Y, -v.X * sin + v.Z * cos);
        }
    }

    // ========================================================================
    //  七、听觉感官 (AISense_Hearing)
    // ========================================================================

    /// <summary>
    /// 听觉感官。听觉范围/噪音强度/年龄衰减/环境噪声。
    /// </summary>
    public sealed class AISense_Hearing : AISense
    {
        public float HearingRange { get; set; } = 200f;
        public float MinNoiseIntensity { get; set; } = 0.3f;
        public float MaxAge { get; set; } = 5f;
        public float AgeDecayRate { get; set; } = 0.5f;
        public float AmbientNoise { get; set; } = 0.05f;

        /// <summary>声音事件列表（外部触发）。</summary>
        private readonly List<(Vector3 Position, float Intensity, float Timestamp)> _soundEvents =
            new List<(Vector3, float, float)>();

        public AISense_Hearing()
        {
            SenseType = AISenseType.Hearing;
            SenseName = "Hearing";
        }

        public override void Configure(AISenseConfig config)
        {
            HearingRange = config.Range;
            MaxAge = config.MaxAge;
            MinNoiseIntensity = config.MinIntensity;
        }

        /// <summary>触发声音事件。</summary>
        public void ReportNoiseEvent(Vector3 position, float intensity, float currentTime)
        {
            _soundEvents.Add((position, intensity, currentTime));
        }

        /// <summary>报告由Actor发出的噪音。</summary>
        public void ReportNoiseEvent(YamaEngine.GameFramework.Actor source, float intensity, float currentTime)
        {
            if (source != null)
                _soundEvents.Add((source.Position, intensity, currentTime));
        }

        public override void Update(AIPerceptionComponent perceptionComponent, float deltaTime, float currentTime)
        {
            if (!IsEnabled) return;

            var owner = perceptionComponent.OwnerPawn;
            if (owner == null) return;

            Vector3 ownerPos = owner.Position;

            // 处理声音事件
            for (int i = _soundEvents.Count - 1; i >= 0; i--)
            {
                var evt = _soundEvents[i];
                float age = currentTime - evt.Timestamp;
                if (age > MaxAge)
                {
                    _soundEvents.RemoveAt(i);
                    continue;
                }

                float distance = Vector3.Distance(ownerPos, evt.Position);
                if (distance > HearingRange) continue;

                // 噪音随距离衰减
                float intensity = evt.Intensity * (1f - distance / HearingRange) - AmbientNoise;
                // 年龄衰减
                intensity -= age * AgeDecayRate;

                if (intensity < MinNoiseIntensity) continue;

                var direction = distance > 0.001f
                    ? Vector3.Normalize(evt.Position - ownerPos)
                    : Vector3.Zero;

                var stimulus = new AIStimulus(null, evt.Position, AISenseType.Hearing,
                    Math.Clamp(intensity, 0f, 1f), direction);
                stimulus.Age = age;
                perceptionComponent.RegisterStimulus(null, stimulus);
            }
        }

        public override void DebugDraw(AIPerceptionComponent perceptionComponent,
            Action<string, Vector3, Vector3> drawLine,
            Action<Vector3, float, Vector3> drawSphere)
        {
            var owner = perceptionComponent.OwnerPawn;
            if (owner == null) return;
            drawSphere?.Invoke(owner.Position, HearingRange, new Vector3(0.5f, 0.2f, 1f));
        }
    }

    // ========================================================================
    //  八、伤害感知 (AISense_Damage)
    // ========================================================================

    /// <summary>
    /// 伤害感知感官。来源/方向/强度。
    /// </summary>
    public sealed class AISense_Damage : AISense
    {
        public float DamageRange { get; set; } = 500f;
        public float MinDamageIntensity { get; set; } = 0.1f;
        public float MaxAge { get; set; } = 10f;

        private readonly List<(YamaEngine.GameFramework.Actor Instigator, Vector3 Position,
            float Intensity, float Timestamp)> _damageEvents =
            new List<(YamaEngine.GameFramework.Actor, Vector3, float, float)>();

        public AISense_Damage()
        {
            SenseType = AISenseType.Damage;
            SenseName = "Damage";
        }

        public override void Configure(AISenseConfig config)
        {
            DamageRange = config.Range;
            MaxAge = config.MaxAge;
            MinDamageIntensity = config.MinIntensity;
        }

        /// <summary>报告伤害事件。</summary>
        public void ReportDamageEvent(YamaEngine.GameFramework.Actor instigator, Vector3 position,
            float damageAmount, float currentTime)
        {
            _damageEvents.Add((instigator, position, damageAmount, currentTime));
        }

        public override void Update(AIPerceptionComponent perceptionComponent, float deltaTime, float currentTime)
        {
            if (!IsEnabled) return;

            var owner = perceptionComponent.OwnerPawn;
            if (owner == null) return;

            Vector3 ownerPos = owner.Position;

            for (int i = _damageEvents.Count - 1; i >= 0; i--)
            {
                var evt = _damageEvents[i];
                float age = currentTime - evt.Timestamp;
                if (age > MaxAge)
                {
                    _damageEvents.RemoveAt(i);
                    continue;
                }

                float distance = Vector3.Distance(ownerPos, evt.Position);
                if (distance > DamageRange) continue;

                float intensity = Math.Clamp(evt.Intensity * (1f - distance / DamageRange), 0f, 1f);
                if (intensity < MinDamageIntensity) continue;

                var direction = distance > 0.001f
                    ? Vector3.Normalize(evt.Position - ownerPos)
                    : Vector3.Zero;

                var stimulus = new AIStimulus(evt.Instigator, evt.Position, AISenseType.Damage,
                    intensity, direction);
                stimulus.Age = age;
                perceptionComponent.RegisterStimulus(evt.Instigator, stimulus);
            }
        }
    }

    // ========================================================================
    //  九、触觉感官 (AISense_Touch)
    // ========================================================================

    /// <summary>
    /// 触觉感官。接触碰撞触发。
    /// </summary>
    public sealed class AISense_Touch : AISense
    {
        public float TouchRadius { get; set; } = 2f;
        public float MaxAge { get; set; } = 1f;

        private readonly List<(YamaEngine.GameFramework.Actor Other, Vector3 ContactPoint,
            float Timestamp)> _touchEvents =
            new List<(YamaEngine.GameFramework.Actor, Vector3, float)>();

        public AISense_Touch()
        {
            SenseType = AISenseType.Touch;
            SenseName = "Touch";
        }

        public override void Configure(AISenseConfig config)
        {
            TouchRadius = config.Range;
            MaxAge = config.MaxAge;
        }

        /// <summary>报告接触碰撞事件。</summary>
        public void ReportTouchEvent(YamaEngine.GameFramework.Actor other, Vector3 contactPoint,
            float currentTime)
        {
            _touchEvents.Add((other, contactPoint, currentTime));
        }

        public override void Update(AIPerceptionComponent perceptionComponent, float deltaTime, float currentTime)
        {
            if (!IsEnabled) return;

            for (int i = _touchEvents.Count - 1; i >= 0; i--)
            {
                var evt = _touchEvents[i];
                float age = currentTime - evt.Timestamp;
                if (age > MaxAge)
                {
                    _touchEvents.RemoveAt(i);
                    continue;
                }

                var stimulus = new AIStimulus(evt.Other, evt.ContactPoint, AISenseType.Touch, 1f,
                    Vector3.Zero);
                stimulus.Age = age;
                perceptionComponent.RegisterStimulus(evt.Other, stimulus);
            }
        }
    }

    // ========================================================================
    //  十、预测感官 (AISense_Prediction)
    // ========================================================================

    /// <summary>
    /// 预测感官。预测目标移动轨迹。
    /// </summary>
    public sealed class AISense_Prediction : AISense
    {
        public float PredictionRange { get; set; } = 300f;
        public float PredictionTime { get; set; } = 2f;
        public float MaxAge { get; set; } = 2f;
        public float MinConfidence { get; set; } = 0.3f;

        public AISense_Prediction()
        {
            SenseType = AISenseType.Prediction;
            SenseName = "Prediction";
        }

        public override void Configure(AISenseConfig config)
        {
            PredictionRange = config.Range;
            MaxAge = config.MaxAge;
            MinConfidence = config.MinIntensity;
        }

        /// <summary>预测目标未来的位置。</summary>
        public Vector3 PredictTargetPosition(YamaEngine.GameFramework.Actor target,
            Vector3 currentVelocity, float predictionTime)
        {
            if (target == null) return Vector3.Zero;
            return target.Position + currentVelocity * predictionTime;
        }

        /// <summary>预测目标是否会到达某位置。</summary>
        public bool WillTargetReach(YamaEngine.GameFramework.Actor target,
            Vector3 targetPosition, Vector3 targetVelocity,
            float tolerance, float predictionTime)
        {
            if (target == null) return false;
            var predicted = PredictTargetPosition(target, targetVelocity, predictionTime);
            float dist = Vector3.Distance(predicted, targetPosition);
            return dist <= tolerance;
        }

        public override void Update(AIPerceptionComponent perceptionComponent, float deltaTime, float currentTime)
        {
            if (!IsEnabled) return;

            var owner = perceptionComponent.OwnerPawn;
            if (owner == null) return;

            // 更新已感知目标的预测位置
            var knownTargets = perceptionComponent.GetKnownTargets();
            foreach (var kvp in knownTargets)
            {
                var target = kvp.Key;
                var lastStimulus = kvp.Value;

                if (target == null) continue;

                float distance = Vector3.Distance(owner.Position, target.Position);
                if (distance > PredictionRange) continue;

                // 使用最后已知的位置和方向进行简单线性预测
                var predictedPos = target.Position + lastStimulus.Direction * PredictionTime;
                float confidence = 1f - (distance / PredictionRange);

                if (confidence < MinConfidence) continue;

                var direction = distance > 0.001f
                    ? Vector3.Normalize(predictedPos - owner.Position)
                    : Vector3.Zero;

                var stimulus = new AIStimulus(target, predictedPos, AISenseType.Prediction,
                    confidence, direction);
                perceptionComponent.RegisterStimulus(target, stimulus);
            }
        }
    }

    // ========================================================================
    //  十一、AI 感知组件 (AIPerceptionComponent)
    // ========================================================================

    /// <summary>
    /// AI 感知组件。附着到 Pawn，管理所有感官和感知结果。
    /// </summary>
    public sealed class AIPerceptionComponent
    {
        /// <summary>所属 Pawn。</summary>
        public YamaEngine.GameFramework.Pawn OwnerPawn { get; private set; }

        /// <summary>感知配置。</summary>
        public AIPerceptionConfig Config { get; set; } = new AIPerceptionConfig();

        /// <summary>团队ID。</summary>
        public AITeamId TeamId { get; set; } = AITeamId.None;

        /// <summary>已注册的可感知目标列表。</summary>
        public List<YamaEngine.GameFramework.Actor> RegisteredTargets { get; } =
            new List<YamaEngine.GameFramework.Actor>();

        /// <summary>已激活的感官列表。</summary>
        public List<AISense> Senses { get; } = new List<AISense>();

        /// <summary>当前已知的感知目标（Actor -> 最新刺激）。</summary>
        private readonly Dictionary<YamaEngine.GameFramework.Actor, AIStimulus> _knownTargets =
            new Dictionary<YamaEngine.GameFramework.Actor, AIStimulus>();

        /// <summary>事件回调。</summary>
        public Action<AIPerceptionEvent> OnPerceptionUpdated { get; set; }

        /// <summary>目标新增回调。</summary>
        public Action<YamaEngine.GameFramework.Actor, AIStimulus> OnTargetAdded { get; set; }

        /// <summary>目标丢失回调。</summary>
        public Action<YamaEngine.GameFramework.Actor> OnTargetLost { get; set; }

        private float _currentTime = 0f;

        public AIPerceptionComponent() { }

        /// <summary>初始化感知组件。</summary>
        public void Initialize(YamaEngine.GameFramework.Pawn owner)
        {
            OwnerPawn = owner;
            RegisterDefaultSenses();
        }

        /// <summary>注册默认感官。</summary>
        public void RegisterDefaultSenses()
        {
            var sight = new AISense_Sight();
            sight.Configure(Config.SightConfig);
            Senses.Add(sight);

            var hearing = new AISense_Hearing();
            hearing.Configure(Config.HearingConfig);
            Senses.Add(hearing);

            var damage = new AISense_Damage();
            damage.Configure(Config.DamageConfig);
            Senses.Add(damage);

            var touch = new AISense_Touch();
            touch.Configure(Config.TouchConfig);
            Senses.Add(touch);
        }

        /// <summary>添加自定义感官。</summary>
        public void AddSense(AISense sense)
        {
            Senses.Add(sense);
        }

        /// <summary>移除感官。</summary>
        public void RemoveSense(AISenseType senseType)
        {
            Senses.RemoveAll(s => s.SenseType == senseType);
        }

        /// <summary>获取指定类型的感官。</summary>
        public T GetSense<T>() where T : AISense
        {
            foreach (var sense in Senses)
                if (sense is T typed) return typed;
            return null;
        }

        /// <summary>注册可感知的目标。</summary>
        public void RegisterTarget(YamaEngine.GameFramework.Actor target)
        {
            if (target == null || RegisteredTargets.Contains(target)) return;
            RegisteredTargets.Add(target);
            foreach (var sense in Senses)
                sense.RegisterTarget(target);
        }

        /// <summary>注销目标。</summary>
        public void UnregisterTarget(YamaEngine.GameFramework.Actor target)
        {
            if (target == null) return;
            RegisteredTargets.Remove(target);
            foreach (var sense in Senses)
                sense.UnregisterTarget(target);
            ForgetTarget(target);
        }

        /// <summary>每帧更新感知。</summary>
        public void Update(float deltaTime)
        {
            _currentTime += deltaTime;

            // 更新所有感官
            foreach (var sense in Senses)
                sense.Update(this, deltaTime, _currentTime);

            // 检查已感知目标是否超龄
            var expired = new List<YamaEngine.GameFramework.Actor>();
            foreach (var kvp in _knownTargets)
            {
                float maxAge = GetMaxAgeForSense(kvp.Value.SenseType);
                if (_currentTime - kvp.Value.Timestamp > maxAge)
                    expired.Add(kvp.Key);
            }

            foreach (var target in expired)
                ForgetTarget(target);
        }

        /// <summary>注册刺激。</summary>
        public void RegisterStimulus(YamaEngine.GameFramework.Actor source, AIStimulus stimulus)
        {
            if (stimulus == null) return;

            bool isNew = !_knownTargets.ContainsKey(source);

            stimulus.SetTimestamp(_currentTime);
            _knownTargets[source] = stimulus;

            if (isNew)
            {
                OnTargetAdded?.Invoke(source, stimulus);
                OnPerceptionUpdated?.Invoke(new AIPerceptionEvent(
                    AIPerceptionEventType.TargetAdded, source, stimulus, this));
            }
            else
            {
                OnPerceptionUpdated?.Invoke(new AIPerceptionEvent(
                    AIPerceptionEventType.TargetUpdated, source, stimulus, this));
            }
        }

        /// <summary>忘记目标。</summary>
        public void ForgetTarget(YamaEngine.GameFramework.Actor target)
        {
            if (target != null && _knownTargets.Remove(target))
            {
                OnTargetLost?.Invoke(target);
                OnPerceptionUpdated?.Invoke(new AIPerceptionEvent(
                    AIPerceptionEventType.TargetLost, target, null, this));
            }
        }

        /// <summary>获取所有已知目标。</summary>
        public Dictionary<YamaEngine.GameFramework.Actor, AIStimulus> GetKnownTargets()
        {
            return new Dictionary<YamaEngine.GameFramework.Actor, AIStimulus>(_knownTargets);
        }

        /// <summary>获取指定目标的最新刺激。</summary>
        public AIStimulus GetStimulusForTarget(YamaEngine.GameFramework.Actor target)
        {
            _knownTargets.TryGetValue(target, out var stimulus);
            return stimulus;
        }

        /// <summary>获取最近的已感知目标。</summary>
        public YamaEngine.GameFramework.Actor GetClosestTarget()
        {
            YamaEngine.GameFramework.Actor closest = null;
            float minDist = float.MaxValue;

            foreach (var kvp in _knownTargets)
            {
                if (kvp.Key == null) continue;
                float dist = Vector3.Distance(OwnerPawn?.Position ?? Vector3.Zero,
                    kvp.Key.Position);
                if (dist < minDist)
                {
                    minDist = dist;
                    closest = kvp.Key;
                }
            }
            return closest;
        }

        /// <summary>按感官类型筛选已知目标。</summary>
        public List<YamaEngine.GameFramework.Actor> GetTargetsBySense(AISenseType senseType)
        {
            var result = new List<YamaEngine.GameFramework.Actor>();
            foreach (var kvp in _knownTargets)
            {
                if (kvp.Value.SenseType == senseType)
                    result.Add(kvp.Key);
            }
            return result;
        }

        /// <summary>检查是否与目标同一队伍。</summary>
        public bool IsSameTeam(YamaEngine.GameFramework.Actor other)
        {
            if (TeamId == AITeamId.None) return false;
            // 简化实现：通过标签检查
            if (other != null && other.ActorTags != null)
                return other.ActorTags.Contains($"Team_{TeamId}");
            return false;
        }

        private float GetMaxAgeForSense(AISenseType senseType)
        {
            switch (senseType)
            {
                case AISenseType.Sight: return Config.SightConfig.MaxAge;
                case AISenseType.Hearing: return Config.HearingConfig.MaxAge;
                case AISenseType.Damage: return Config.DamageConfig.MaxAge;
                case AISenseType.Touch: return Config.TouchConfig.MaxAge;
                case AISenseType.Prediction: return Config.PredictionConfig.MaxAge;
                default: return 5f;
            }
        }

        /// <summary>清除所有感知信息。</summary>
        public void ClearAll()
        {
            _knownTargets.Clear();
            RegisteredTargets.Clear();
        }
    }

    // ========================================================================
    //  十二、团队感知系统
    // ========================================================================

    /// <summary>
    /// 团队感知管理器。同一队伍共享感知信息。
    /// </summary>
    public sealed class AITeamPerception
    {
        private readonly Dictionary<AITeamId, List<AIPerceptionComponent>> _teamMembers =
            new Dictionary<AITeamId, List<AIPerceptionComponent>>();

        /// <summary>共享信息的最小强度阈值。</summary>
        public float ShareMinIntensity { get; set; } = 0.5f;

        /// <summary>是否启用团队共享。</summary>
        public bool EnableSharing { get; set; } = true;

        /// <summary>注册团队成员。</summary>
        public void RegisterMember(AITeamId teamId, AIPerceptionComponent component)
        {
            if (!_teamMembers.ContainsKey(teamId))
                _teamMembers[teamId] = new List<AIPerceptionComponent>();
            if (!_teamMembers[teamId].Contains(component))
                _teamMembers[teamId].Add(component);
        }

        /// <summary>注销团队成员。</summary>
        public void UnregisterMember(AITeamId teamId, AIPerceptionComponent component)
        {
            if (_teamMembers.TryGetValue(teamId, out var members))
                members.Remove(component);
        }

        /// <summary>共享感知信息（每帧调用）。</summary>
        public void SharePerception()
        {
            if (!EnableSharing) return;

            foreach (var kvp in _teamMembers)
            {
                var teamId = kvp.Key;
                var members = kvp.Value;

                foreach (var member in members)
                {
                    var knownTargets = member.GetKnownTargets();
                    foreach (var targetKvp in knownTargets)
                    {
                        if (targetKvp.Value.Intensity < ShareMinIntensity) continue;

                        // 共享给同队其他成员
                        foreach (var otherMember in members)
                        {
                            if (otherMember == member) continue;
                            var existingStimulus = otherMember.GetStimulusForTarget(targetKvp.Key);
                            if (existingStimulus == null || existingStimulus.Intensity < targetKvp.Value.Intensity)
                            {
                                otherMember.RegisterStimulus(targetKvp.Key, targetKvp.Value);
                            }
                        }
                    }
                }
            }
        }

        /// <summary>获取团队成员数量。</summary>
        public int GetTeamMemberCount(AITeamId teamId)
        {
            if (_teamMembers.TryGetValue(teamId, out var members))
                return members.Count;
            return 0;
        }
    }

    // ========================================================================
    //  十三、感知调试可视化
    // ========================================================================

    /// <summary>
    /// 感知调试可视化。
    /// </summary>
    public sealed class AIPerceptionDebug
    {
        public bool ShowSightCone { get; set; } = true;
        public bool ShowHearingRange { get; set; } = true;
        public bool ShowPerceptionLines { get; set; } = true;
        public bool ShowStimulusPositions { get; set; } = true;

        public Action<string, Vector3, Vector3> DrawLine { get; set; }
        public Action<Vector3, float, Vector3> DrawSphere { get; set; }

        /// <summary>绘制感知调试信息。</summary>
        public void Draw(AIPerceptionComponent perceptionComponent)
        {
            if (perceptionComponent?.OwnerPawn == null) return;

            Vector3 pos = perceptionComponent.OwnerPawn.Position;

            foreach (var sense in perceptionComponent.Senses)
            {
                sense.DebugDraw(perceptionComponent, DrawLine, DrawSphere);
            }

            if (ShowPerceptionLines)
            {
                var knownTargets = perceptionComponent.GetKnownTargets();
                foreach (var kvp in knownTargets)
                {
                    if (kvp.Key == null) continue;
                    DrawLine?.Invoke("Perception", pos, kvp.Key.Position);
                    DrawSphere?.Invoke(kvp.Key.Position, 0.3f, new Vector3(1, 0, 0));
                }
            }
        }
    }
}
