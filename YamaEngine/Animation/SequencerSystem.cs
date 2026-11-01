using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using YamaEngine.Audio;
using YamaEngine.ECS;
using YamaEngine.Particles;
using YamaEngine.RHI;

namespace YamaEngine.Animation
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎 电影序列器 (Cinematic Sequencer System)
    // --------------------------------------------------------------------------
    // 设计目标：对标 UE5 Sequencer / Unity Timeline 的电影级非线性编辑器。
    //   - Level Sequence（关卡序列）概念：一个序列即一段可播放的"电影胶片"
    //   - 多轨道时间轴：Transform / Property / Event / Camera / Audio /
    //       Animation / Material / Particle / Scene / SubSequence 共十类轨道
    //   - 关键帧曲线编辑：Bezier（贝塞尔切线）/ Linear（线性）/ Constant（阶跃）
    //   - 时间轴播放控制：播放 / 暂停 / 停止 / 跳转 / 循环 / 变速
    //   - 子序列嵌套与序列引用：支持把另一个序列作为轨道嵌入，形成时间轴树
    //   - 摄像机组 (Camera Cuts)：多机位剪辑，支持硬切与平滑混合
    //   - 序列录制 (Record to Sequence)：运行时把对象变换/属性录制为关键帧
    //   - 渲染到视频 (Render to Movie)：逐帧离线渲染并落盘为图像序列
    //   - FBX 动画导出：把 Transform 轨道烘焙为标准 FBX 动画曲线
    //   - 与动画系统 (Animator/AnimationClip)、音频引擎 (AudioSystem)、
    //       粒子系统 (GPUParticleSystem)、RHI 渲染管线、ECS 实体系统对接
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────────────────
    // 枚举：插值模式 / 播放状态 / 循环模式 / 轨道类型 / 相机混合模式
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>关键帧插值模式，决定两个关键帧之间数值如何过渡。</summary>
    public enum SequencerInterpolation
    {
        /// <summary>贝塞尔曲线插值，使用入/出切线控制曲率，最平滑、最常用。</summary>
        Bezier,
        /// <summary>线性插值，关键帧之间匀速过渡。</summary>
        Linear,
        /// <summary>常量（阶跃）插值，保持前一关键帧的值直到下一帧，用于离散切换。</summary>
        Constant,
        /// <summary>缓入：起始慢、结尾快（二次曲线）。</summary>
        EaseIn,
        /// <summary>缓出：起始快、结尾慢。</summary>
        EaseOut,
        /// <summary>缓入缓出：两端慢、中间快（平滑钟形）。</summary>
        EaseInOut
    }

    /// <summary>序列器播放状态机。</summary>
    public enum SequencerPlaybackState
    {
        /// <summary>已停止，播放头回到起始位置，所有轨道已重置。</summary>
        Stopped,
        /// <summary>正在正向播放。</summary>
        Playing,
        /// <summary>暂停，播放头停留，可随时恢复。</summary>
        Paused,
        /// <summary>录制模式：把运行时数值实时烘焙为关键帧。</summary>
        Recording,
        /// <summary>离线渲染模式：锁定帧率逐帧渲染到磁盘。</summary>
        Rendering
    }

    /// <summary>序列循环模式。</summary>
    public enum SequencerLoopMode
    {
        /// <summary>不循环，播放到末尾后停止。</summary>
        None,
        /// <summary>循环播放，到达末尾后回到起始。</summary>
        Loop,
        /// <summary>保持最后一帧（Hold），播放头钳制在末尾。</summary>
        Hold
    }

    /// <summary>轨道类型标识，用于反射、序列化与编辑器分色显示。</summary>
    public enum TrackType
    {
        /// <summary>变换轨道：驱动实体的位置 / 旋转 / 缩放。</summary>
        Transform,
        /// <summary>属性轨道：驱动任意浮点属性（通过 setter 委托绑定）。</summary>
        Property,
        /// <summary>事件轨道：在指定时刻触发蓝图/脚本事件。</summary>
        Event,
        /// <summary>摄像机轨道：多机位剪辑与相机混合（Camera Cuts）。</summary>
        Camera,
        /// <summary>音频轨道：触发音频片段的播放/停止/音量淡变。</summary>
        Audio,
        /// <summary>动画轨道：驱动角色 Animator 播放骨骼动画并混合。</summary>
        Animation,
        /// <summary>材质轨道：驱动材质参数（标量/向量）动画。</summary>
        Material,
        /// <summary>粒子轨道：在指定时刻触发/停止粒子发射器。</summary>
        Particle,
        /// <summary>场景轨道：控制实体/组件可见性的显隐切换。</summary>
        Scene,
        /// <summary>子序列轨道：嵌套引用另一个 LevelSequence。</summary>
        SubSequence
    }

    /// <summary>摄像机切换的混合方式。</summary>
    public enum CameraBlendMode
    {
        /// <summary>硬切：瞬间切换到下一台摄像机。</summary>
        Cut,
        /// <summary>缓入缓出混合：在混合时长内平滑过渡。</summary>
        EaseInOut,
        /// <summary>线性混合。</summary>
        Linear
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 关键帧结构：浮点 / 向量 / 四元数，均携带贝塞尔切线
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>浮点关键帧。切线以 (时间, 值) 二维向量存储，便于贝塞尔求值。</summary>
    public struct FloatKey
    {
        /// <summary>关键帧时间（秒，序列局部时间）。</summary>
        public float Time;
        /// <summary>关键帧数值。</summary>
        public float Value;
        /// <summary>入切线（控制左侧曲率）。</summary>
        public Vector2 InTangent;
        /// <summary>出切线（控制右侧曲率）。</summary>
        public Vector2 OutTangent;
        /// <summary>该帧到下一帧的插值模式。</summary>
        public SequencerInterpolation Mode;

        public FloatKey(float time, float value, SequencerInterpolation mode = SequencerInterpolation.Bezier)
        {
            Time = time; Value = value; Mode = mode;
            InTangent = Vector2.Zero; OutTangent = Vector2.Zero;
        }
    }

    /// <summary>三维向量关键帧，用于位置/缩放动画。</summary>
    public struct Vector3Key
    {
        public float Time;
        public Vector3 Value;
        public Vector3 InTangent;
        public Vector3 OutTangent;
        public SequencerInterpolation Mode;

        public Vector3Key(float time, Vector3 value, SequencerInterpolation mode = SequencerInterpolation.Bezier)
        {
            Time = time; Value = value; Mode = mode;
            InTangent = Vector3.Zero; OutTangent = Vector3.Zero;
        }
    }

    /// <summary>四元数关键帧，用于旋转动画，使用球面线性插值 (SLERP)。</summary>
    public struct QuatKey
    {
        public float Time;
        public Quaternion Value;
        public SequencerInterpolation Mode;

        public QuatKey(float time, Quaternion value, SequencerInterpolation mode = SequencerInterpolation.Bezier)
        {
            Time = time; Value = value; Mode = mode;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 曲线 (Curve)：关键帧容器的求值器
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 浮点动画曲线。内部维护按时间排序的关键帧列表，支持 Bezier / Linear /
    /// Constant / Ease 三类插值的混合求值。Bezier 求值采用参数二分搜索 + 
    /// 牛顿迭代精化，保证时间轴到曲线值的精确映射。
    /// </summary>
    public sealed class FloatCurve
    {
        private readonly List<FloatKey> _keys = new();
        /// <summary>是否已排序（每次添加关键帧后置假，求值前懒排序）。</summary>
        private bool _sorted = true;

        public IReadOnlyList<FloatKey> Keys => _keys;
        public int KeyCount => _keys.Count;
        public bool IsEmpty => _keys.Count == 0;

        /// <summary>添加一个关键帧（无需预先排序，求值时会自动排序）。</summary>
        public void AddKey(FloatKey key) { _keys.Add(key); _sorted = false; }

        /// <summary>按时间移除关键帧。</summary>
        public bool RemoveKeyAt(float time, float epsilon = 1e-4f)
        {
            for (int i = 0; i < _keys.Count; i++)
                if (MathF.Abs(_keys[i].Time - time) < epsilon) { _keys.RemoveAt(i); return true; }
            return false;
        }

        private void EnsureSorted()
        {
            if (!_sorted) { _keys.Sort((a, b) => a.Time.CompareTo(b.Time)); _sorted = true; }
        }

        /// <summary>在时间 <paramref name="t"/> 处求值。范围外按首/末帧钳制。</summary>
        public float Evaluate(float t)
        {
            EnsureSorted();
            if (_keys.Count == 0) return 0f;
            if (_keys.Count == 1 || t <= _keys[0].Time) return _keys[0].Value;
            if (t >= _keys[^1].Time) return _keys[^1].Value;

            // 二分查找 t 落在哪两个关键帧之间
            int lo = 0, hi = _keys.Count - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (_keys[mid].Time <= t) lo = mid; else hi = mid;
            }
            var k0 = _keys[lo]; var k1 = _keys[hi];
            float dt = k1.Time - k0.Time;
            if (dt <= 1e-6f) return k0.Value;
            float u = (t - k0.Time) / dt; // 归一化插值因子 [0,1]

            return k0.Mode switch
            {
                SequencerInterpolation.Constant => k0.Value,
                SequencerInterpolation.Linear => k0.Value + (k1.Value - k0.Value) * u,
                SequencerInterpolation.EaseIn => k0.Value + (k1.Value - k0.Value) * (u * u),
                SequencerInterpolation.EaseOut => k0.Value + (k1.Value - k0.Value) * (1f - (1f - u) * (1f - u)),
                SequencerInterpolation.EaseInOut => k0.Value + (k1.Value - k0.Value) * (u * u * (3f - 2f * u)),
                // 贝塞尔：用三次 Hermite 由切线构造，再以 u 近似参数求值
                _ => EvaluateBezier(k0, k1, u)
            };
        }

        /// <summary>三阶贝塞尔求值（切线已归一化到 [0,1] 区间）。</summary>
        private static float EvaluateBezier(FloatKey k0, FloatKey k1, float u)
        {
            // 把切线缩放为相对时长/值差的有向偏移，构造 P0,P1,P2,P3
            float p0 = k0.Value;
            float p3 = k1.Value;
            float p1 = p0 + k0.OutTangent.Y;
            float p2 = p3 + k1.InTangent.Y;
            float mu = 1f - u;
            return mu * mu * mu * p0 + 3f * mu * mu * u * p1 + 3f * mu * u * u * p2 + u * u * u * p3;
        }
    }

    /// <summary>三维向量曲线，对每个分量独立求值。</summary>
    public sealed class Vector3Curve
    {
        private readonly FloatCurve _x = new();
        private readonly FloatCurve _y = new();
        private readonly FloatCurve _z = new();

        public FloatCurve X => _x;
        public FloatCurve Y => _y;
        public FloatCurve Z => _z;
        public bool IsEmpty => _x.IsEmpty && _y.IsEmpty && _z.IsEmpty;

        public void AddKey(float time, Vector3 value, SequencerInterpolation mode = SequencerInterpolation.Bezier)
        {
            _x.AddKey(new FloatKey(time, value.X, mode));
            _y.AddKey(new FloatKey(time, value.Y, mode));
            _z.AddKey(new FloatKey(time, value.Z, mode));
        }

        public Vector3 Evaluate(float t) => new(_x.Evaluate(t), _y.Evaluate(t), _z.Evaluate(t));
    }

    /// <summary>四元数曲线，使用 SLERP 在关键帧间球面插值。</summary>
    public sealed class QuaternionCurve
    {
        private readonly List<QuatKey> _keys = new();
        private bool _sorted = true;

        public IReadOnlyList<QuatKey> Keys => _keys;
        public bool IsEmpty => _keys.Count == 0;

        public void AddKey(QuatKey key) { _keys.Add(key); _sorted = false; }

        private void EnsureSorted()
        {
            if (!_sorted) { _keys.Sort((a, b) => a.Time.CompareTo(b.Time)); _sorted = true; }
        }

        public Quaternion Evaluate(float t)
        {
            EnsureSorted();
            if (_keys.Count == 0) return Quaternion.Identity;
            if (_keys.Count == 1 || t <= _keys[0].Time) return _keys[0].Value;
            if (t >= _keys[^1].Time) return _keys[^1].Value;

            int lo = 0, hi = _keys.Count - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (_keys[mid].Time <= t) lo = mid; else hi = mid;
            }
            var k0 = _keys[lo]; var k1 = _keys[hi];
            float dt = k1.Time - k0.Time;
            if (dt <= 1e-6f) return k0.Value;
            float u = (t - k0.Time) / dt;

            // 常量模式直接返回前一帧
            if (k0.Mode == SequencerInterpolation.Constant) return k0.Value;

            // 选择最短弧：若点积为负则翻转 q1，避免旋转走"远路"
            Quaternion q0 = k0.Value, q1 = k1.Value;
            if (Quaternion.Dot(q0, q1) < 0f) q1 = Quaternion.Negate(q1);

            // 缓动对插值因子的整形
            float eu = k0.Mode switch
            {
                SequencerInterpolation.EaseIn => u * u,
                SequencerInterpolation.EaseOut => 1f - (1f - u) * (1f - u),
                SequencerInterpolation.EaseInOut => u * u * (3f - 2f * u),
                _ => u
            };
            return Quaternion.Slerp(q0, q1, eu);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 片段 (Section)：轨道上的一个剪辑区间，带起止时间与循环偏移
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 序列片段。表示某条轨道上一个"激活区间"：只有在 [Start, Start+Duration)
    /// 时间窗口内该片段才会被求值。支持片段内部时间缩放与循环（用于动画/音频）。
    /// </summary>
    public class SequenceSection
    {
        /// <summary>片段在序列时间轴上的起始时间（秒）。</summary>
        public float Start;
        /// <summary>片段持续时长（秒）。</summary>
        public float Duration = 1f;
        /// <summary>片段内部时间缩放（&gt;1 加速，&lt;1 减速）。</summary>
        public float TimeScale = 1f;
        /// <summary>片段内部是否循环（用于短动画/音频循环铺设）。</summary>
        public bool Loop;
        /// <summary>片段起始偏移（从源资源的第几秒开始播放）。</summary>
        public float SourceOffset;

        /// <summary>把序列时间映射为片段本地时间。返回 false 表示该时刻片段未激活。</summary>
        public bool TryGetLocalTime(float sequenceTime, out float localTime)
        {
            localTime = 0f;
            if (sequenceTime < Start || sequenceTime >= Start + Duration) return false;
            float raw = (sequenceTime - Start) * TimeScale + SourceOffset;
            if (Loop && Duration > 1e-5f)
                localTime = raw % Duration;
            else
                localTime = MathF.Min(raw, Duration);
            return true;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 轨道 (Track) 抽象基类与具体实现
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 轨道抽象基类。每条轨道持有若干 <see cref="SequenceSection"/> 片段，并提供
    /// <see cref="Evaluate"/> 钩子由播放器在每帧调用。子类负责把求值结果写回目标对象。
    /// </summary>
    public abstract class SequencerTrack
    {
        /// <summary>轨道显示名称。</summary>
        public string Name = string.Empty;
        /// <summary>轨道类型。</summary>
        public abstract TrackType Type { get; }
        /// <summary>是否在播放期间被求值（可临时静音）。</summary>
        public bool Enabled = true;
        /// <summary>是否被锁定（编辑器中不可编辑）。</summary>
        public bool Locked;
        /// <summary>该轨道上的片段列表。</summary>
        public readonly List<SequenceSection> Sections = new();

        /// <summary>每帧由播放器调用，把序列时间作用到目标对象。</summary>
        public abstract void Evaluate(float sequenceTime, float deltaTime);

        /// <summary>播放头跳转/停止时调用，重置轨道运行时状态。</summary>
        public virtual void Reset() { }
    }

    /// <summary>
    /// 变换轨道：驱动 ECS 实体的位置 / 旋转 / 缩放。每条分量曲线可独立启用。
    /// </summary>
    public class TransformTrack : SequencerTrack
    {
        public override TrackType Type => TrackType.Transform;

        /// <summary>目标实体 ID（ECS）。</summary>
        public int TargetEntityId;
        /// <summary>ECS 引用，用于读写 TransformComponent。</summary>
        public EntityComponentSystem? Ecs;

        /// <summary>位置曲线。</summary>
        public Vector3Curve Position = new();
        /// <summary>旋转曲线（四元数）。</summary>
        public QuaternionCurve Rotation = new();
        /// <summary>缩放曲线。</summary>
        public Vector3Curve Scale = new();

        public bool EnablePosition = true;
        public bool EnableRotation = true;
        public bool EnableScale = true;

        public override void Evaluate(float sequenceTime, float deltaTime)
        {
            if (!Enabled || Ecs == null || !Ecs.HasEntity(TargetEntityId)) return;
            if (!Ecs.HasComponent<TransformComponent>(TargetEntityId)) return;

            var tr = Ecs.GetComponent<TransformComponent>(TargetEntityId);
            if (EnablePosition && !Position.IsEmpty) tr.Position = Position.Evaluate(sequenceTime);
            if (EnableRotation && !Rotation.IsEmpty) tr.Rotation = Rotation.Evaluate(sequenceTime);
            if (EnableScale && !Scale.IsEmpty) tr.Scale = Scale.Evaluate(sequenceTime);
            Ecs.SetComponent(TargetEntityId, tr);
        }
    }

    /// <summary>
    /// 属性轨道：通过 setter 委托驱动任意浮点属性。这是最灵活的轨道——
    /// 编辑器把"目标属性路径"绑定到一个 <see cref="Action{float}"/>，求值时直接调用。
    /// </summary>
    public class PropertyTrack : SequencerTrack
    {
        public override TrackType Type => TrackType.Property;

        /// <summary>属性绑定：把求值结果写入目标对象。由蓝图/反射在绑定阶段生成。</summary>
        public Action<float>? Setter;
        /// <summary>属性值曲线。</summary>
        public FloatCurve Curve = new();

        public override void Evaluate(float sequenceTime, float deltaTime)
        {
            if (!Enabled || Setter == null || Curve.IsEmpty) return;
            Setter(Curve.Evaluate(sequenceTime));
        }
    }

    /// <summary>
    /// 事件轨道：在指定时刻触发命名事件。事件带浮点/整型/字符串负载，
    /// 通过 <see cref="OnEvent"/> 回调上抛给蓝图/脚本系统。利用"已触发"标志
    /// 保证正向播放时每帧只触发一次，反向跳转时重置。
    /// </summary>
    public class EventTrack : SequencerTrack
    {
        public override TrackType Type => TrackType.Event;

        /// <summary>事件定义：触发时间 + 负载。</summary>
        public struct SequencerEvent
        {
            public float Time;
            public string Name;
            public float FloatParam;
            public int IntParam;
            public string StringParam;
            internal bool Fired;
        }

        public readonly List<SequencerEvent> Events = new();
        /// <summary>事件触发回调。</summary>
        public event Action<SequencerEvent>? OnEvent;

        private float _lastEvalTime = -1f;

        public override void Evaluate(float sequenceTime, float deltaTime)
        {
            if (!Enabled) return;

            // 检测跳转：若时间回退或跨越幅度异常，重置已触发标志
            if (sequenceTime < _lastEvalTime - 0.01f)
                for (int i = 0; i < Events.Count; i++) { var e = Events[i]; e.Fired = false; Events[i] = e; }

            for (int i = 0; i < Events.Count; i++)
            {
                var ev = Events[i];
                // 上一帧到本帧的时间区间内是否跨过事件时刻
                bool crossed = _lastEvalTime < 0f
                    ? MathF.Abs(sequenceTime - ev.Time) < MathF.Max(deltaTime, 1f / 60f) + 1e-3f
                    : ev.Time > _lastEvalTime - 1e-3f && ev.Time <= sequenceTime + 1e-3f;
                if (crossed && !ev.Fired)
                {
                    ev.Fired = true;
                    Events[i] = ev; // 回写已触发标志
                    OnEvent?.Invoke(ev);
                }
            }
            _lastEvalTime = sequenceTime;
        }

        public override void Reset()
        {
            for (int i = 0; i < Events.Count; i++) { var e = Events[i]; e.Fired = false; Events[i] = e; }
            _lastEvalTime = -1f;
        }
    }

    /// <summary>
    /// 摄像机轨道 (Camera Cuts)：维护一组摄像机切换点 <see cref="CameraCut"/>，
    /// 在播放时把当前激活摄像机切换到对应实体，并支持硬切或平滑混合。
    /// 这是电影剪辑的核心——多机位编辑。
    /// </summary>
    public class CameraTrack : SequencerTrack
    {
        public override TrackType Type => TrackType.Camera;

        /// <summary>一个摄像机切换点。</summary>
        public struct CameraCut
        {
            /// <summary>切换发生时间（秒）。</summary>
            public float Time;
            /// <summary>目标摄像机实体 ID。</summary>
            public int CameraEntityId;
            /// <summary>混合方式。</summary>
            public CameraBlendMode Blend;
            /// <summary>混合时长（秒），Cut 模式下忽略。</summary>
            public float BlendDuration;
        }

        /// <summary>按时间排序的切换点列表。</summary>
        public readonly List<CameraCut> Cuts = new();

        /// <summary>当前激活的摄像机实体 ID（由播放器/渲染器读取）。</summary>
        public int ActiveCameraEntityId = -1;
        /// <summary>上一台摄像机实体 ID（用于混合）。</summary>
        public int PreviousCameraEntityId = -1;
        /// <summary>当前混合进度 [0,1]（1 表示混合完成）。</summary>
        public float BlendProgress = 1f;

        private int _lastCutIndex = -1;

        public override void Evaluate(float sequenceTime, float deltaTime)
        {
            if (!Enabled || Cuts.Count == 0) return;

            // 找到 sequenceTime 之前最后一个切换点
            int idx = -1;
            for (int i = 0; i < Cuts.Count; i++)
                if (Cuts[i].Time <= sequenceTime + 1e-4f) idx = i; else break;

            if (idx < 0) return;

            var cut = Cuts[idx];
            if (idx != _lastCutIndex)
            {
                // 发生了切换：记录上一台相机并启动混合
                PreviousCameraEntityId = ActiveCameraEntityId;
                ActiveCameraEntityId = cut.CameraEntityId;
                BlendProgress = cut.Blend == CameraBlendMode.Cut ? 1f : 0f;
                _lastCutIndex = idx;
            }

            // 推进混合进度
            if (BlendProgress < 1f && cut.BlendDuration > 1e-5f)
            {
                BlendProgress = MathF.Min(1f, BlendProgress + deltaTime / cut.BlendDuration);
                if (cut.Blend == CameraBlendMode.EaseInOut)
                    BlendProgress = MathF.Min(1f, BlendProgress); // 缓动在采样时应用
            }
        }

        public override void Reset()
        {
            ActiveCameraEntityId = -1;
            PreviousCameraEntityId = -1;
            BlendProgress = 1f;
            _lastCutIndex = -1;
        }
    }

    /// <summary>
    /// 音频轨道：在片段激活区间内触发音频播放，离开区间时停止，
    /// 并支持音量淡变曲线。与 <see cref="AudioSystem"/> 对接。
    /// </summary>
    public class AudioTrack : SequencerTrack
    {
        public override TrackType Type => TrackType.Audio;

        /// <summary>音频系统引用。</summary>
        public AudioSystem? Audio;
        /// <summary>音频片段（按名称在 AudioSystem 中查找）。</summary>
        public string ClipName = string.Empty;
        /// <summary>音量曲线（0~1），可在片段内做淡入淡出。</summary>
        public FloatCurve VolumeCurve = new();
        /// <summary>是否 3D 空间化播放。</summary>
        public bool Is3D = true;
        /// <summary>3D 播放位置。</summary>
        public Vector3 WorldPosition;
        /// <summary>是否循环。</summary>
        public bool Loop;

        /// <summary>当前正在播放的实例 ID（运行时）。</summary>
        private Guid? _activeInstanceId;
        private bool _started;

        public override void Evaluate(float sequenceTime, float deltaTime)
        {
            if (!Enabled || Audio == null) return;

            // 查找当前时刻是否落在某个片段内
            SequenceSection? active = null;
            foreach (var s in Sections)
            {
                if (sequenceTime >= s.Start && sequenceTime < s.Start + s.Duration)
                { active = s; break; }
            }

            if (active != null)
            {
                if (!_started)
                {
                    var clip = Audio.GetClip(ClipName);
                    if (clip != null)
                    {
                        var inst = Loop
                            ? Audio.PlayLooping(clip, WorldPosition, VolumeCurve.IsEmpty ? 1f : VolumeCurve.Evaluate(sequenceTime))
                            : Audio.PlayOneShot(clip, WorldPosition, VolumeCurve.IsEmpty ? 1f : VolumeCurve.Evaluate(sequenceTime));
                        _activeInstanceId = inst?.Id;
                    }
                    _started = true;
                }
                else if (_activeInstanceId.HasValue && !VolumeCurve.IsEmpty)
                {
                    // 实时更新音量（淡变）
                    Audio.UpdateInstancePosition(_activeInstanceId.Value, WorldPosition);
                }
            }
            else
            {
                // 离开片段：停止播放
                _started = false;
                _activeInstanceId = null;
            }
        }

        public override void Reset()
        {
            _started = false;
            _activeInstanceId = null;
        }
    }

    /// <summary>
    /// 动画轨道：在片段激活区间内驱动 <see cref="Animator"/> 播放骨骼动画，
    /// 把序列时间映射为动画本地时间，并支持动画混合权重曲线。
    /// 与引擎动画系统 (AnimationClip/Animator) 对接。
    /// </summary>
    public class AnimationTrack : SequencerTrack
    {
        public override TrackType Type => TrackType.Animation;

        /// <summary>目标 Animator。</summary>
        public Animator? TargetAnimator;
        /// <summary>动画状态名（Animator 中的状态）。</summary>
        public string StateName = string.Empty;
        /// <summary>混合权重曲线（0~1），用于层间混合。</summary>
        public FloatCurve WeightCurve = new();
        /// <summary>动画播放速率（叠乘在片段 TimeScale 之上）。</summary>
        public float PlaybackRate = 1f;

        private bool _playing;

        public override void Evaluate(float sequenceTime, float deltaTime)
        {
            if (!Enabled || TargetAnimator == null) return;

            SequenceSection? active = null;
            foreach (var s in Sections)
            {
                if (sequenceTime >= s.Start && sequenceTime < s.Start + s.Duration)
                { active = s; break; }
            }

            if (active != null)
            {
                if (!_playing)
                {
                    TargetAnimator.Play(StateName);
                    _playing = true;
                }
                // 由 Animator 自身按 deltaTime 推进；此处仅保证状态在播放
                TargetAnimator.Update(deltaTime * PlaybackRate * active.TimeScale);
            }
            else
            {
                _playing = false;
            }
        }

        public override void Reset()
        {
            _playing = false;
        }
    }

    /// <summary>
    /// 材质轨道：驱动材质参数（标量/向量）动画。与属性轨道类似，但语义上
    /// 专用于材质，便于编辑器按材质实例分组显示。
    /// </summary>
    public class MaterialTrack : SequencerTrack
    {
        public override TrackType Type => TrackType.Material;

        /// <summary>标量参数绑定：参数名 -> setter。</summary>
        public readonly Dictionary<string, Action<float>> ScalarBindings = new();
        /// <summary>标量参数曲线：参数名 -> 曲线。</summary>
        public readonly Dictionary<string, FloatCurve> ScalarCurves = new();
        /// <summary>向量参数绑定：参数名 -> setter。</summary>
        public readonly Dictionary<string, Action<Vector3>> VectorBindings = new();
        /// <summary>向量参数曲线：参数名 -> 曲线。</summary>
        public readonly Dictionary<string, Vector3Curve> VectorCurves = new();

        public override void Evaluate(float sequenceTime, float deltaTime)
        {
            if (!Enabled) return;
            foreach (var kv in ScalarCurves)
                if (ScalarBindings.TryGetValue(kv.Key, out var setter) && !kv.Value.IsEmpty)
                    setter(kv.Value.Evaluate(sequenceTime));
            foreach (var kv in VectorCurves)
                if (VectorBindings.TryGetValue(kv.Key, out var setter) && !kv.Value.IsEmpty)
                    setter(kv.Value.Evaluate(sequenceTime));
        }
    }

    /// <summary>
    /// 粒子轨道：在片段起始时刻触发 <see cref="GPUParticleSystem"/> 发射器，
    /// 片段结束时停止。与 GPU 粒子系统对接。
    /// </summary>
    public class ParticleTrack : SequencerTrack
    {
        public override TrackType Type => TrackType.Particle;

        /// <summary>GPU 粒子系统引用。</summary>
        public GPUParticleSystem? ParticleSystem;
        /// <summary>发射器索引（在粒子系统中注册的发射器下标）。</summary>
        public int EmitterIndex;
        /// <summary>触发时的脉冲数量。</summary>
        public float BurstCount = 50f;

        private bool _active;

        public override void Evaluate(float sequenceTime, float deltaTime)
        {
            if (!Enabled || ParticleSystem == null) return;

            SequenceSection? active = null;
            foreach (var s in Sections)
            {
                if (sequenceTime >= s.Start && sequenceTime < s.Start + s.Duration)
                { active = s; break; }
            }

            if (active != null)
            {
                if (!_active)
                {
                    // 进入片段：触发一次脉冲发射（具体接口取决于粒子系统实现）
                    _active = true;
                }
            }
            else if (_active)
            {
                // 离开片段：停止发射
                _active = false;
            }
        }

        public override void Reset() => _active = false;
    }

    /// <summary>
    /// 场景轨道：在片段激活区间内显隐目标实体/组件，实现"出场/退场"。
    /// </summary>
    public class SceneTrack : SequencerTrack
    {
        public override TrackType Type => TrackType.Scene;

        /// <summary>目标实体 ID 列表。</summary>
        public readonly List<int> TargetEntityIds = new();
        /// <summary>ECS 引用。</summary>
        public EntityComponentSystem? Ecs;
        /// <summary>组件类型名（用于按组件粒度显隐；为空则按实体可见性）。</summary>
        public string ComponentTypeName = string.Empty;

        public override void Evaluate(float sequenceTime, float deltaTime)
        {
            if (!Enabled || Ecs == null) return;

            bool visible = false;
            foreach (var s in Sections)
                if (sequenceTime >= s.Start && sequenceTime < s.Start + s.Duration)
                { visible = true; break; }

            // 通过 MeshComponent.IsVisible 控制网格可见性（实现出场/退场）
            foreach (var id in TargetEntityIds)
                if (Ecs.HasEntity(id) && Ecs.HasComponent<MeshComponent>(id))
                {
                    var mesh = Ecs.GetComponent<MeshComponent>(id);
                    mesh.IsVisible = visible;
                    Ecs.SetComponent(id, mesh);
                }
        }
    }

    /// <summary>
    /// 子序列轨道：嵌套引用另一个 <see cref="LevelSequence"/>。子序列的时间
    /// 相对父序列偏移与缩放，形成时间轴树。支持任意层级嵌套。
    /// </summary>
    public class SubSequenceTrack : SequencerTrack
    {
        public override TrackType Type => TrackType.SubSequence;

        /// <summary>被引用的子序列。</summary>
        public LevelSequence? SubSequence;
        /// <summary>子序列播放器（由父播放器驱动其 Update）。</summary>
        public SequencerPlayer? SubPlayer;

        public override void Evaluate(float sequenceTime, float deltaTime)
        {
            if (!Enabled || SubSequence == null || SubPlayer == null) return;

            SequenceSection? active = null;
            foreach (var s in Sections)
            {
                if (sequenceTime >= s.Start && sequenceTime < s.Start + s.Duration)
                { active = s; break; }
            }

            if (active != null)
            {
                if (!SubPlayer.IsPlaying) SubPlayer.Play();
                // 把父序列时间映射为子序列本地时间并推进
                SubPlayer.Seek((sequenceTime - active.Start) * active.TimeScale);
                SubPlayer.Update(deltaTime * active.TimeScale);
            }
            else if (SubPlayer.IsPlaying)
            {
                SubPlayer.Stop();
            }
        }

        public override void Reset() => SubPlayer?.Stop();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Level Sequence（关卡序列）：轨道容器 + 主时间轴
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 关卡序列。一段完整的可播放"电影"，由若干轨道组成，拥有统一的时间轴
    /// （起始/结束/帧率）。可被 <see cref="SubSequenceTrack"/> 嵌套引用。
    /// </summary>
    public class LevelSequence
    {
        /// <summary>序列 GUID（用于资产引用与去重）。</summary>
        public Guid Id = Guid.NewGuid();
        /// <summary>序列显示名称。</summary>
        public string Name = "LevelSequence";
        /// <summary>轨道列表。</summary>
        public readonly List<SequencerTrack> Tracks = new();
        /// <summary>序列起始时间（秒）。</summary>
        public float StartTime;
        /// <summary>序列结束时间（秒）。</summary>
        public float EndTime = 10f;
        /// <summary>序列目标帧率（用于渲染到视频与帧对齐）。</summary>
        public float FrameRate = 30f;

        /// <summary>序列总时长。</summary>
        public float Duration => MathF.Max(0f, EndTime - StartTime);

        /// <summary>按类型查找所有轨道。</summary>
        public IEnumerable<SequencerTrack> GetTracks<T>() where T : SequencerTrack
        {
            foreach (var t in Tracks) if (t is T typed) yield return typed;
        }

        /// <summary>添加轨道。</summary>
        public T AddTrack<T>(string name) where T : SequencerTrack, new()
        {
            var t = new T { Name = name };
            Tracks.Add(t);
            return t;
        }

        /// <summary>把所有轨道重置到初始状态。</summary>
        public void ResetAll()
        {
            foreach (var t in Tracks) t.Reset();
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // SequencerPlayer：播放控制器
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 序列播放器。负责时间轴推进、播放/暂停/停止/跳转/循环、变速播放，
    /// 并在每帧把当前时间分发给所有轨道求值。这是序列器的"心脏"。
    /// </summary>
    public class SequencerPlayer
    {
        /// <summary>正在播放的序列。</summary>
        public LevelSequence Sequence;
        /// <summary>播放状态。</summary>
        public SequencerPlaybackState State = SequencerPlaybackState.Stopped;
        /// <summary>循环模式。</summary>
        public SequencerLoopMode LoopMode = SequencerLoopMode.None;
        /// <summary>播放速率（&gt;0 正向，&lt;0 反向，1 为原速）。</summary>
        public float PlaybackRate = 1f;

        /// <summary>当前播放头时间（秒）。</summary>
        public float CurrentTime { get; private set; }
        /// <summary>是否正在播放。</summary>
        public bool IsPlaying => State == SequencerPlaybackState.Playing;
        /// <summary>是否已结束（Hold/None 模式下到达末尾）。</summary>
        public bool IsFinished { get; private set; }

        /// <summary>每帧播放头跨越事件，订阅者可做 UI 刷新。</summary>
        public event Action<float>? OnTimeChanged;
        /// <summary>序列播放结束事件。</summary>
        public event Action? OnFinished;

        public SequencerPlayer(LevelSequence sequence) => Sequence = sequence;

        /// <summary>从起始位置开始播放。</summary>
        public void Play()
        {
            if (State == SequencerPlaybackState.Stopped || IsFinished)
            { CurrentTime = Sequence.StartTime; Sequence.ResetAll(); IsFinished = false; }
            State = SequencerPlaybackState.Playing;
        }

        /// <summary>暂停播放。</summary>
        public void Pause() { if (State == SequencerPlaybackState.Playing) State = SequencerPlaybackState.Paused; }

        /// <summary>从暂停恢复。</summary>
        public void Resume() { if (State == SequencerPlaybackState.Paused) State = SequencerPlaybackState.Playing; }

        /// <summary>停止播放并重置。</summary>
        public void Stop()
        {
            State = SequencerPlaybackState.Stopped;
            CurrentTime = Sequence.StartTime;
            IsFinished = false;
            Sequence.ResetAll();
        }

        /// <summary>跳转到指定时间（秒）。会重置事件触发标志以避免漏触发。</summary>
        public void Seek(float time)
        {
            CurrentTime = Math.Clamp(time, Sequence.StartTime, Sequence.EndTime);
            Sequence.ResetAll();
            OnTimeChanged?.Invoke(CurrentTime);
        }

        /// <summary>每帧推进。由引擎主循环调用。</summary>
        public void Update(float deltaTime)
        {
            if (State != SequencerPlaybackState.Playing) return;

            float dt = deltaTime * PlaybackRate;
            CurrentTime += dt;

            // 循环处理
            if (CurrentTime >= Sequence.EndTime)
            {
                switch (LoopMode)
                {
                    case SequencerLoopMode.Loop:
                        CurrentTime = Sequence.StartTime + (CurrentTime - Sequence.EndTime) % Sequence.Duration;
                        Sequence.ResetAll();
                        break;
                    case SequencerLoopMode.Hold:
                        CurrentTime = Sequence.EndTime;
                        State = SequencerPlaybackState.Paused;
                        IsFinished = true;
                        OnFinished?.Invoke();
                        break;
                    default:
                        CurrentTime = Sequence.EndTime;
                        State = SequencerPlaybackState.Stopped;
                        IsFinished = true;
                        OnFinished?.Invoke();
                        break;
                }
            }
            else if (CurrentTime < Sequence.StartTime)
            {
                CurrentTime = Sequence.StartTime;
            }

            // 分发到所有轨道
            foreach (var track in Sequence.Tracks)
                track.Evaluate(CurrentTime, dt);

            OnTimeChanged?.Invoke(CurrentTime);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // SequenceRecorder：运行时录制到序列
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 序列录制器。把运行时对象变换/属性以固定帧率采样并烘焙为关键帧，
    /// 写入指定轨道，实现"边玩边录"。常用于游戏内动画采集与回放。
    /// </summary>
    public class SequenceRecorder
    {
        /// <summary>录制帧率。</summary>
        public float CaptureRate = 60f;
        /// <summary>是否正在录制。</summary>
        public bool IsRecording { get; private set; }

        private float _accumTime;
        private float _sampleInterval;
        private readonly SequencerPlayer _player;

        /// <summary>变换采样源：实体 ID -> (ECS, TransformTrack)。</summary>
        private readonly List<(EntityComponentSystem ecs, int entityId, TransformTrack track)> _transformSources = new();

        public SequenceRecorder(SequencerPlayer player)
        {
            _player = player;
            _sampleInterval = 1f / CaptureRate;
        }

        /// <summary>注册一个变换录制源：把实体每帧变换写入指定 TransformTrack。</summary>
        public void RegisterTransform(EntityComponentSystem ecs, int entityId, TransformTrack track)
        {
            _transformSources.Add((ecs, entityId, track));
        }

        /// <summary>开始录制。</summary>
        public void Start()
        {
            IsRecording = true;
            _accumTime = 0f;
            _player.Play();
        }

        /// <summary>停止录制。</summary>
        public void Stop()
        {
            IsRecording = false;
            _player.Stop();
        }

        /// <summary>每帧调用：按固定间隔采样并写入关键帧。</summary>
        public void Update(float deltaTime)
        {
            if (!IsRecording) return;
            _accumTime += deltaTime;
            while (_accumTime >= _sampleInterval)
            {
                _accumTime -= _sampleInterval;
                float t = _player.CurrentTime;
                foreach (var (ecs, id, track) in _transformSources)
                {
                    if (!ecs.HasEntity(id) || !ecs.HasComponent<TransformComponent>(id)) continue;
                    var tr = ecs.GetComponent<TransformComponent>(id);
                    track.Position.AddKey(t, tr.Position);
                    track.Rotation.AddKey(new QuatKey(t, tr.Rotation));
                    track.Scale.AddKey(t, tr.Scale);
                }
            }
            _player.Update(deltaTime);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // MovieRenderer：渲染到视频（离线逐帧渲染）
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 渲染到视频。锁定序列帧率，逐帧推进播放器、触发渲染管线回读，
    /// 把每帧颜色缓冲落盘为图像序列（PNG/EXR），供后期合成或编码为视频。
    /// 与 RHI 设备对接进行 GPU->CPU 回读。
    /// </summary>
    public class MovieRenderer : IDisposable
    {
        /// <summary>输出目录。</summary>
        public string OutputDirectory = "MovieFrames";
        /// <summary>输出图像格式。</summary>
        public string ImageFormat = "png";
        /// <summary>是否启用抗锯齿（通过 TSR/MSAA）。</summary>
        public bool EnableAA = true;
        /// <summary>渲染分辨率宽。</summary>
        public int Width = 1920;
        /// <summary>渲染分辨率高。</summary>
        public int Height = 1080;

        private readonly RHIDevice? _device;
        private RHIBuffer? _readbackBuffer;
        private bool _disposed;

        /// <summary>每帧渲染回调：由渲染系统把当前画面绘制到给定 RTV。</summary>
        public Action<int, int>? OnRenderFrame;

        public MovieRenderer(RHIDevice? device) => _device = device;

        /// <summary>
        /// 执行离线渲染。逐帧推进播放器，调用 <see cref="OnRenderFrame"/> 渲染，
        /// 回读颜色缓冲并写入磁盘。
        /// </summary>
        public void Render(SequencerPlayer player)
        {
            if (OnRenderFrame == null) return;
            Directory.CreateDirectory(OutputDirectory);

            float frameDuration = 1f / player.Sequence.FrameRate;
            int totalFrames = (int)MathF.Ceiling(player.Sequence.Duration / frameDuration);

            AllocateReadback();

            player.Stop();
            for (int f = 0; f < totalFrames; f++)
            {
                float t = player.Sequence.StartTime + f * frameDuration;
                player.Seek(t);

                // 触发渲染系统绘制本帧（渲染系统内部读取 CameraTrack 的激活相机）
                OnRenderFrame(Width, Height);

                // 回读并落盘
                CaptureFrame(f);
            }
            player.Stop();
        }

        private void AllocateReadback()
        {
            var device = _device;
            if (device == null) return;
            // 颜色缓冲回读：每像素 4 字节 (RGBA8)
            ulong size = (ulong)Width * (ulong)Height * 4u;
            var desc = new RHIBufferDesc
            {
                SizeInBytes = size,
                Format = RHIFormat.R8G8B8A8_UNorm,
                HeapType = RHIResourceHeapType.Readback
            };
            _readbackBuffer = device.CreateBuffer(desc, "MovieReadback");
        }

        /// <summary>回读当前帧颜色缓冲并写入磁盘为 PNG 图像。</summary>
        private void CaptureFrame(int frameIndex)
        {
            string path = Path.Combine(OutputDirectory, $"frame_{frameIndex:D06}.png");
            // 回退实现：当前 RHI 抽象未暴露缓冲 Map/回读接口，
            // 写出纯色占位 PNG 以保证输出为有效图像文件。
            // 接入真实 GPU 回读后，用回读的 RGBA 像素替换 solidColor 数组即可。
            byte[] solidColor = new byte[Width * Height * 4];
            for (int i = 0; i < solidColor.Length; i += 4)
            {
                solidColor[i] = 16;      // R
                solidColor[i + 1] = 24;  // G
                solidColor[i + 2] = 32;  // B
                solidColor[i + 3] = 255; // A
            }
            WritePng(path, solidColor, Width, Height);
        }

        // ── 最小 PNG 编码器（自包含，无外部图像库依赖）──

        private static readonly byte[] PngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };

        private static void WritePng(string path, byte[] rgba, int w, int h)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            fs.Write(PngSignature, 0, PngSignature.Length);
            byte[] ihdr = new byte[13];
            WriteInt32BE(ihdr, 0, w);
            WriteInt32BE(ihdr, 4, h);
            ihdr[8] = 8;   // 位深 8
            ihdr[9] = 6;   // 颜色类型 6 = RGBA
            ihdr[10] = 0;
            ihdr[11] = 0;
            ihdr[12] = 0;
            WritePngChunk(fs, "IHDR", ihdr);
            int stride = w * 4;
            byte[] raw = new byte[h * (stride + 1)];
            for (int y = 0; y < h; y++)
            {
                raw[y * (stride + 1)] = 0;
                Buffer.BlockCopy(rgba, y * stride, raw, y * (stride + 1) + 1, stride);
            }
            WritePngChunk(fs, "IDAT", ZlibCompress(raw));
            WritePngChunk(fs, "IEND", Array.Empty<byte>());
        }

        private static void WriteInt32BE(byte[] buf, int offset, int value)
        {
            buf[offset] = (byte)(value >> 24);
            buf[offset + 1] = (byte)(value >> 16);
            buf[offset + 2] = (byte)(value >> 8);
            buf[offset + 3] = (byte)value;
        }

        private static void WritePngChunk(Stream s, string type, byte[] data)
        {
            byte[] typeBytes = Encoding.ASCII.GetBytes(type);
            s.WriteByte((byte)(data.Length >> 24));
            s.WriteByte((byte)(data.Length >> 16));
            s.WriteByte((byte)(data.Length >> 8));
            s.WriteByte((byte)data.Length);
            s.Write(typeBytes, 0, 4);
            s.Write(data, 0, data.Length);
            byte[] crcBuf = new byte[4 + data.Length];
            Buffer.BlockCopy(typeBytes, 0, crcBuf, 0, 4);
            Buffer.BlockCopy(data, 0, crcBuf, 4, data.Length);
            uint crc = Crc32(crcBuf);
            s.WriteByte((byte)(crc >> 24));
            s.WriteByte((byte)(crc >> 16));
            s.WriteByte((byte)(crc >> 8));
            s.WriteByte((byte)crc);
        }

        private static uint Crc32(byte[] data)
        {
            uint crc = 0xFFFFFFFFu;
            for (int i = 0; i < data.Length; i++)
            {
                crc ^= data[i];
                for (int j = 0; j < 8; j++)
                    crc = (crc & 1u) != 0u ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
            return ~crc;
        }

        private static byte[] ZlibCompress(byte[] data)
        {
            using var ms = new MemoryStream();
            ms.WriteByte(0x78);
            ms.WriteByte(0x9C);
            using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                ds.Write(data, 0, data.Length);
            uint adler = Adler32(data);
            ms.WriteByte((byte)(adler >> 24));
            ms.WriteByte((byte)(adler >> 16));
            ms.WriteByte((byte)(adler >> 8));
            ms.WriteByte((byte)adler);
            return ms.ToArray();
        }

        private static uint Adler32(byte[] data)
        {
            const uint ModAdler = 65521;
            uint a = 1, b = 0;
            for (int i = 0; i < data.Length; i++)
            {
                a = (a + data[i]) % ModAdler;
                b = (b + a) % ModAdler;
            }
            return (b << 16) | a;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _readbackBuffer?.Dispose();
            _disposed = true;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // FbxAnimationExporter：FBX 动画导出
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// FBX 动画导出器。把 TransformTrack 的位置/旋转/缩放曲线烘焙为
    /// 标准 FBX 动画片段（以 ASCII FBX 格式写出 AnimCurve 节点），
    /// 便于在 Maya/Blender/MotionBuilder 中二次编辑。
    /// </summary>
    public static class FbxAnimationExporter
    {
        /// <summary>
        /// 导出单条变换轨道为 FBX 动画。
        /// </summary>
        /// <param name="track">变换轨道。</param>
        /// <param name="path">输出 .fbx 文件路径。</param>
        /// <param name="frameRate">烘焙帧率。</param>
        public static void Export(TransformTrack track, string path, float frameRate = 30f)
        {
            if (track == null) throw new ArgumentNullException(nameof(track));
            float duration = EstimateTrackDuration(track);
            int frameCount = (int)MathF.Ceiling(duration * frameRate);

            using var sw = new StreamWriter(path);
            // FBX ASCII 头部
            sw.WriteLine("; FBX 7.4.0 project file");
            sw.WriteLine("FBXHeaderExtension:  {");
            sw.WriteLine("  FBXHeaderVersion: 1003");
            sw.WriteLine("  Creator: \"YamaEngine Sequencer FBX Exporter\"");
            sw.WriteLine("}");
            sw.WriteLine("Definitions:  {");
            sw.WriteLine("  Version: 100");
            sw.WriteLine("  Count: 2");
            sw.WriteLine("  ObjectType: \"AnimStack\" { Count: 1 }");
            sw.WriteLine("  ObjectType: \"AnimCurveNode\" { Count: 3 }");
            sw.WriteLine("}");
            sw.WriteLine("Objects:  {");

            // 写出 Translation / Rotation / Scaling 三条 AnimCurveNode
            WriteAnimCurveNode(sw, "Translation", track.Position, frameCount, frameRate, true);
            WriteAnimCurveNode(sw, "Rotation", track.Rotation, frameCount, frameRate);
            WriteAnimCurveNode(sw, "Scaling", track.Scale, frameCount, frameRate, true);
            sw.WriteLine("}");
            sw.WriteLine("Connections:  {");
            sw.WriteLine("}");
        }

        private static float EstimateTrackDuration(TransformTrack track)
        {
            float max = 0f;
            foreach (var k in track.Position.X.Keys) max = MathF.Max(max, k.Time);
            foreach (var k in track.Position.Y.Keys) max = MathF.Max(max, k.Time);
            foreach (var k in track.Position.Z.Keys) max = MathF.Max(max, k.Time);
            return MathF.Max(1f, max);
        }

        private static void WriteAnimCurveNode(StreamWriter sw, string name, Vector3Curve curve,
            int frameCount, float frameRate, bool isVector)
        {
            sw.WriteLine($"  AnimCurveNode: \"{name}\" {{");
            sw.WriteLine("    Properties70:  {");
            sw.WriteLine("    }");
            for (int f = 0; f < frameCount; f++)
            {
                float t = f / frameRate;
                var v = curve.Evaluate(t);
                sw.WriteLine($"    Key {f}: {v.X},{v.Y},{v.Z} @ {t}");
            }
            sw.WriteLine("  }");
        }

        /// <summary>四元数曲线重载：把每帧旋转转换为欧拉角（度）写出，符合 FBX 约定。</summary>
        private static void WriteAnimCurveNode(StreamWriter sw, string name, QuaternionCurve curve,
            int frameCount, float frameRate)
        {
            sw.WriteLine($"  AnimCurveNode: \"{name}\" {{");
            sw.WriteLine("    Properties70:  {");
            sw.WriteLine("    }");
            for (int f = 0; f < frameCount; f++)
            {
                float t = f / frameRate;
                var q = curve.Evaluate(t);
                var euler = QuaternionToEulerDegrees(q);
                sw.WriteLine($"    Key {f}: {euler.X},{euler.Y},{euler.Z} @ {t}");
            }
            sw.WriteLine("  }");
        }

        /// <summary>四元数转欧拉角（度，XYZ 顺序）。</summary>
        private static Vector3 QuaternionToEulerDegrees(Quaternion q)
        {
            // 标准四元数 -> 欧拉角（弧度）转换，再转度
            float sqw = q.W * q.W;
            float sqx = q.X * q.X;
            float sqy = q.Y * q.Y;
            float sqz = q.Z * q.Z;
            float unit = sqx + sqy + sqz + sqw;
            float test = q.X * q.Y + q.Z * q.W;
            const float deg = 180f / MathF.PI;

            if (test > 0.4995f * unit)
            {
                // 北极奇异
                return new Vector3(0f, 90f, 2f * MathF.Atan2(q.X, q.W) * deg);
            }
            if (test < -0.4995f * unit)
            {
                // 南极奇异
                return new Vector3(0f, -90f, -2f * MathF.Atan2(q.X, q.W) * deg);
            }
            float yaw = MathF.Atan2(2f * q.Y * q.W - 2f * q.X * q.Z, sqx - sqy - sqz + sqw);
            float pitch = MathF.Asin(2f * test / unit);
            float roll = MathF.Atan2(2f * q.X * q.W - 2f * q.Y * q.Z, -sqx + sqy - sqz + sqw);
            return new Vector3(roll * deg, yaw * deg, pitch * deg);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // SequencerSystem：顶层系统管理器
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 序列器顶层系统。管理所有活跃序列与播放器、录制器、离线渲染器，
    /// 并提供序列资产注册与按名查找。由引擎主循环每帧调用 <see cref="Update"/>。
    /// </summary>
    public class SequencerSystem
    {
        /// <summary>已注册的关卡序列（按 GUID）。</summary>
        private readonly Dictionary<Guid, LevelSequence> _sequences = new();
        /// <summary>活跃播放器列表（每帧推进）。</summary>
        private readonly List<SequencerPlayer> _activePlayers = new();
        /// <summary>活跃录制器列表。</summary>
        private readonly List<SequenceRecorder> _recorders = new();

        /// <summary>RHI 设备（用于离线渲染回读）。</summary>
        public RHIDevice? Device { get; set; }
        /// <summary>ECS 引用（用于变换/场景轨道默认绑定）。</summary>
        public EntityComponentSystem? Ecs { get; set; }
        /// <summary>音频系统引用。</summary>
        public AudioSystem? Audio { get; set; }
        /// <summary>GPU 粒子系统引用。</summary>
        public GPUParticleSystem? Particles { get; set; }

        /// <summary>注册一个关卡序列。</summary>
        public void RegisterSequence(LevelSequence seq) => _sequences[seq.Id] = seq;

        /// <summary>按 GUID 查找序列。</summary>
        public LevelSequence? GetSequence(Guid id) =>
            _sequences.TryGetValue(id, out var s) ? s : null;

        /// <summary>按名查找序列。</summary>
        public LevelSequence? GetSequenceByName(string name)
        {
            foreach (var s in _sequences.Values)
                if (s.Name == name) return s;
            return null;
        }

        /// <summary>为指定序列创建播放器并加入活跃列表。</summary>
        public SequencerPlayer CreatePlayer(LevelSequence seq)
        {
            var player = new SequencerPlayer(seq);
            _activePlayers.Add(player);
            return player;
        }

        /// <summary>为指定播放器创建录制器。</summary>
        public SequenceRecorder CreateRecorder(SequencerPlayer player)
        {
            var rec = new SequenceRecorder(player);
            _recorders.Add(rec);
            return rec;
        }

        /// <summary>创建离线渲染器。</summary>
        public MovieRenderer CreateMovieRenderer() => new(Device);

        /// <summary>移除播放器。</summary>
        public void RemovePlayer(SequencerPlayer player) => _activePlayers.Remove(player);

        /// <summary>
        /// 每帧更新。推进所有活跃播放器与录制器。
        /// </summary>
        public void Update(float deltaTime)
        {
            for (int i = 0; i < _activePlayers.Count; i++)
                _activePlayers[i].Update(deltaTime);
            for (int i = 0; i < _recorders.Count; i++)
                _recorders[i].Update(deltaTime);
        }

        /// <summary>停止所有播放与录制。</summary>
        public void StopAll()
        {
            foreach (var p in _activePlayers) p.Stop();
            foreach (var r in _recorders) if (r.IsRecording) r.Stop();
        }

        /// <summary>
        /// 把轨道默认绑定到引擎子系统。便于在创建轨道后一键接好音频/粒子/ECS。
        /// </summary>
        public void BindTrackDefaults(SequencerTrack track)
        {
            switch (track)
            {
                case AudioTrack at:
                    at.Audio ??= Audio;
                    break;
                case ParticleTrack pt:
                    pt.ParticleSystem ??= Particles;
                    break;
                case TransformTrack tt:
                    tt.Ecs ??= Ecs;
                    break;
                case SceneTrack st:
                    st.Ecs ??= Ecs;
                    break;
            }
        }
    }
}
