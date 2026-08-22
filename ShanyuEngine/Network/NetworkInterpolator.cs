// ============================================================================
// NetworkInterpolator.cs
// 山雨游戏引擎 - 网络插值器/外推器
// 让远程玩家的移动看起来不那么卡
// ============================================================================
// NOTE: 插值延迟设100ms比较稳 太短了会抖 太长了手感差
// 外推是用物理公式预测未来位置 当包丢了的时候特别有用
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ShanyuEngine.Network
{
    /// <summary>
    /// 插值方法
    /// </summary>
    public enum InterpolationMethod
    {
        /// <summary>线性插值</summary>
        Linear,
        /// <summary>三次样条</summary>
        CubicSpline,
        /// <summary>Catmull-Rom</summary>
        CatmullRom,
        /// <summary>Hermite</summary>
        Hermite,
        /// <summary>指数衰减(适合平滑过渡)</summary>
        ExponentialDecay,
        /// <summary>无插值(直接跳)</summary>
        None
    }

    /// <summary>
    /// 外推方法
    /// </summary>
    public enum ExtrapolationMethod
    {
        /// <summary>线性外推(匀速)</summary>
        Linear,
        /// <summary>二次外推(匀加速)</summary>
        Quadratic,
        /// <summary>基于物理(考虑重力等)</summary>
        Physics,
        /// <summary>不外推</summary>
        None
    }

    /// <summary>
    /// 插值状态
    /// </summary>
    public enum InterpolationState
    {
        /// <summary>空闲</summary>
        Idle,
        /// <summary>正在插值</summary>
        Interpolating,
        /// <summary>正在外推(数据不够)</summary>
        Extrapolating,
        /// <summary>等待数据</summary>
        WaitingForData,
        /// <summary>数据量过多 跳过插值</summary>
        CaughtUp
    }

    /// <summary>
    /// 时间戳状态样本
    /// </summary>
    public struct TimedSample
    {
        /// <summary>远程时间戳(毫秒)</summary>
        public long RemoteTimestamp;

        /// <summary>本地接收时间(毫秒)</summary>
        public long LocalReceiveTime;

        /// <summary>位置</summary>
        public System.Numerics.Vector3 Position;

        /// <summary>旋转</summary>
        public System.Numerics.Quaternion Rotation;

        /// <summary>缩放</summary>
        public System.Numerics.Vector3 Scale;

        /// <summary>速度(如果有)</summary>
        public System.Numerics.Vector3 Velocity;

        /// <summary>角速度(如果有)</summary>
        public System.Numerics.Vector3 AngularVelocity;

        /// <summary>自定义浮点值</summary>
        public float CustomFloat;

        /// <summary>创建样本
        /// </summary>
        public static TimedSample Create(long timestamp, System.Numerics.Vector3 position,
            System.Numerics.Quaternion rotation = default)
        {
            return new TimedSample
            {
                RemoteTimestamp = timestamp,
                LocalReceiveTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Position = position,
                Rotation = rotation,
                Scale = System.Numerics.Vector3.One,
                Velocity = System.Numerics.Vector3.Zero
            };
        }
    }

    /// <summary>
    /// 插值配置
    /// </summary>
    public class InterpolationConfig
    {
        /// <summary>插值方法</summary>
        public InterpolationMethod Method { get; set; } = InterpolationMethod.Hermite;

        /// <summary>外推方法</summary>
        public ExtrapolationMethod Extrapolation { get; set; } = ExtrapolationMethod.Linear;

        /// <summary>插值延迟(毫秒) - NOTE: 100ms比较稳</summary>
        public int InterpolationDelay { get; set; } = 100;

        /// <summary>最大插值时间(毫秒)</summary>
        public int MaxInterpolationTime { get; set; } = 500;

        /// <summary>最大外推时间(毫秒)</summary>
        public int MaxExtrapolationTime { get; set; } = 200;

        /// <summary>样本缓冲区大小</summary>
        public int BufferSize { get; set; } = 60;

        /// <summary>指数衰减速率</summary>
        public float ExponentialDecayRate { get; set; } = 5.0f;

        /// <summary>外推速度衰减</summary>
        public float ExtrapolationVelocityDamping { get; set; } = 0.95f;

        /// <summary>位置修正阈值</summary>
        public float PositionCorrectionThreshold { get; set; } = 2.0f;

        /// <summary>位置修正速率</summary>
        public float PositionCorrectionRate { get; set; } = 0.3f;

        /// <summary>旋转修正阈值(弧度)</summary>
        public float RotationCorrectionThreshold { get; set; } = 0.5f;

        /// <summary>是否启用量化平滑(过滤微小抖动)</summary>
        public bool EnableQuantizationSmoothing { get; set; } = true;

        /// <summary>量化精度(米)</summary>
        public float QuantizationPrecision { get; set; } = 0.001f;

        /// <summary>时间偏移补偿(服务端-客户端的时钟差)</summary>
        public long TimeOffset { get; set; } = 0;

        /// <summary>默认配置</summary>
        public static InterpolationConfig Default => new InterpolationConfig();

        /// <summary>快速配置(低延迟)</summary>
        public static InterpolationConfig Fast => new InterpolationConfig
        {
            InterpolationDelay = 50,
            Method = InterpolationMethod.Linear,
            Extrapolation = ExtrapolationMethod.Linear,
            BufferSize = 30
        };

        /// <summary>平滑配置(高延迟)</summary>
        public static InterpolationConfig Smooth => new InterpolationConfig
        {
            InterpolationDelay = 200,
            Method = InterpolationMethod.CatmullRom,
            Extrapolation = ExtrapolationMethod.Quadratic,
            BufferSize = 120,
            ExponentialDecayRate = 3.0f,
            PositionCorrectionRate = 0.15f
        };
    }

    /// <summary>
    /// 插值结果
    /// </summary>
    public struct InterpolationResult
    {
        /// <summary>插值位置</summary>
        public System.Numerics.Vector3 Position;

        /// <summary>插值旋转</summary>
        public System.Numerics.Quaternion Rotation;

        /// <summary>插值缩放</summary>
        public System.Numerics.Vector3 Scale;

        /// <summary>当前状态</summary>
        public InterpolationState State;

        /// <summary>插值进度(0-1)</summary>
        public float Progress;

        /// <summary>外推时长(毫秒)</summary>
        public float ExtrapolationDuration;

        /// <summary>是否位置被修正了</summary>
        public bool PositionCorrected;
    }

    /// <summary>
    /// 单个对象的插值器
    /// </summary>
    public class ObjectInterpolator : IDisposable
    {
        /// <summary>对象ID</summary>
        public uint ObjectId { get; set; }

        /// <summary>配置</summary>
        public InterpolationConfig Config { get; set; }

        /// <summary>当前状态</summary>
        public InterpolationState State { get; private set; } = InterpolationState.WaitingForData;

        /// <summary>当前插值结果</summary>
        public InterpolationResult CurrentResult { get; private set; }

        // 样本缓冲区(用链表方便插入和排序)
        private LinkedList<TimedSample> _sampleBuffer;

        // 当前显示的位置
        private System.Numerics.Vector3 _displayPosition;
        private System.Numerics.Quaternion _displayRotation;
        private System.Numerics.Vector3 _displayScale = System.Numerics.Vector3.One;

        // 外推起始状态
        private TimedSample _extrapolationStart;
        private long _extrapolationStartTime;
        private System.Numerics.Vector3 _extrapolationVelocity;
        private System.Numerics.Vector3 _extrapolationAcceleration;

        // 位置修正目标
        private System.Numerics.Vector3 _correctionTarget;
        private System.Numerics.Quaternion _correctionTargetRotation;
        private bool _isCorrecting;
        private float _correctionProgress;

        // 最后一次收到样本的时间
        private long _lastSampleTime;

        // 统计
        private int _totalSamplesReceived;
        private int _totalInterpolations;
        private int _totalExtrapolations;
        private int _totalCorrections;
        private float _maxExtrapolationUsed;

        // 是否有效
        private bool _valid;
        private bool _disposed;

        public ObjectInterpolator(uint objectId, InterpolationConfig config = null)
        {
            ObjectId = objectId;
            Config = config ?? InterpolationConfig.Default;
            _sampleBuffer = new LinkedList<TimedSample>();
            _valid = true;
        }

        /// <summary>
        /// 添加新的样本
        /// </summary>
        public void AddSample(TimedSample sample)
        {
            if (!_valid || _disposed) return;

            // 量化平滑 - 如果位置变化太小就忽略
            if (Config.EnableQuantizationSmoothing && _sampleBuffer.Count > 0)
            {
                var last = _sampleBuffer.Last.Value;
                float dist = System.Numerics.Vector3.Distance(last.Position, sample.Position);
                if (dist < Config.QuantizationPrecision && sample.RemoteTimestamp <= last.RemoteTimestamp)
                {
                    return;
                }
            }

            // 按时间戳插入(保持有序)
            var node = _sampleBuffer.Last;
            while (node != null && node.Value.RemoteTimestamp > sample.RemoteTimestamp)
            {
                node = node.Previous;
            }

            if (node == null)
            {
                _sampleBuffer.AddFirst(sample);
            }
            else
            {
                _sampleBuffer.AddAfter(node, sample);
            }

            // 裁剪缓冲区
            while (_sampleBuffer.Count > Config.BufferSize)
            {
                _sampleBuffer.RemoveFirst();
            }

            _lastSampleTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _totalSamplesReceived++;

            // 如果正在外推, 检查是否需要停止外推
            if (State == InterpolationState.Extrapolating)
            {
                // 收到新数据, 切换回插值
                State = InterpolationState.Interpolating;
                _isCorrecting = false;
            }
        }

        /// <summary>
        /// 获取插值结果
        /// </summary>
        public InterpolationResult GetInterpolatedValue(long currentTime)
        {
            if (!_valid || _sampleBuffer.Count < 2)
            {
                if (_sampleBuffer.Count == 1)
                {
                    var s = _sampleBuffer.First.Value;
                    _displayPosition = s.Position;
                    _displayRotation = s.Rotation;
                    _displayScale = s.Scale;
                }
                State = InterpolationState.WaitingForData;
                return CreateResult(InterpolationState.WaitingForData, 0);
            }

            // 计算目标时间(当前时间 - 插值延迟)
            long targetTime = currentTime - Config.InterpolationDelay + Config.TimeOffset;

            // 查找目标时间对应的两个样本
            var result = InterpolateAtTime(targetTime);

            // 更新显示值
            if (State == InterpolationState.Interpolating || State == InterpolationState.CaughtUp)
            {
                _displayPosition = result.Position;
                _displayRotation = result.Rotation;
                _displayScale = result.Scale;
                _totalInterpolations++;
            }
            else if (State == InterpolationState.Extrapolating)
            {
                _totalExtrapolations++;
                _maxExtrapolationUsed = Math.Max(_maxExtrapolationUsed, result.ExtrapolationDuration);
            }

            // 位置修正
            if (_isCorrecting)
            {
                ApplyCorrection(currentTime);
            }

            CurrentResult = result;
            return result;
        }

        /// <summary>
        /// 在指定时间进行插值
        /// </summary>
        private InterpolationResult InterpolateAtTime(long targetTime)
        {
            // 找到目标时间前后的样本
            LinkedListNode<TimedSample> before = null;
            LinkedListNode<TimedSample> after = null;

            for (var node = _sampleBuffer.First; node != null; node = node.Next)
            {
                if (node.Value.RemoteTimestamp <= targetTime)
                {
                    before = node;
                }
                else if (after == null && node.Value.RemoteTimestamp > targetTime)
                {
                    after = node;
                    break;
                }
            }

            // 如果目标时间在最新样本之后
            if (before != null && after == null)
            {
                // 检查是否需要外推
                long timeSinceLastSample = targetTime - before.Value.RemoteTimestamp;

                if (timeSinceLastSample > Config.MaxExtrapolationTime)
                {
                    // 外推时间太长, 直接用最后一个样本
                    State = InterpolationState.Idle;
                    return CreateResult(InterpolationState.Idle, 1.0f, before.Value.Position, before.Value.Rotation);
                }

                // 执行外推
                return Extrapolate(targetTime, before);
            }

            // 如果目标时间在最早样本之前(不太常见, 说明数据延迟了)
            if (before == null && after != null)
            {
                State = InterpolationState.WaitingForData;
                return CreateResult(InterpolationState.WaitingForData, 0, after.Value.Position, after.Value.Rotation);
            }

            if (before == null || after == null)
            {
                State = InterpolationState.WaitingForData;
                var sample = _sampleBuffer.First != null ? _sampleBuffer.First.Value : default;
                return CreateResult(InterpolationState.WaitingForData, 0, sample.Position, sample.Rotation);
            }

            // 计算插值参数t
            float timeRange = after.Value.RemoteTimestamp - before.Value.RemoteTimestamp;
            float t = timeRange > 0
                ? (float)(targetTime - before.Value.RemoteTimestamp) / timeRange
                : 1.0f;
            t = Math.Clamp(t, 0, 1);

            // 根据方法进行插值
            System.Numerics.Vector3 position;
            System.Numerics.Quaternion rotation;
            System.Numerics.Vector3 scale;

            switch (Config.Method)
            {
                case InterpolationMethod.Linear:
                    position = Lerp(before.Value.Position, after.Value.Position, t);
                    rotation = Slerp(before.Value.Rotation, after.Value.Rotation, t);
                    scale = Lerp(before.Value.Scale, after.Value.Scale, t);
                    break;

                case InterpolationMethod.CubicSpline:
                    position = CubicSplineInterpolate(before, after, t);
                    rotation = Slerp(before.Value.Rotation, after.Value.Rotation, t);
                    scale = Lerp(before.Value.Scale, after.Value.Scale, t);
                    break;

                case InterpolationMethod.CatmullRom:
                    position = CatmullRomInterpolate(before, after, t);
                    rotation = Slerp(before.Value.Rotation, after.Value.Rotation, t);
                    scale = Lerp(before.Value.Scale, after.Value.Scale, t);
                    break;

                case InterpolationMethod.Hermite:
                    position = HermiteInterpolate(before, after, t, timeRange);
                    rotation = Slerp(before.Value.Rotation, after.Value.Rotation, t);
                    scale = Lerp(before.Value.Scale, after.Value.Scale, t);
                    break;

                case InterpolationMethod.ExponentialDecay:
                    position = ExponentialDecayInterpolate(before.Value.Position, after.Value.Position, t);
                    rotation = Slerp(before.Value.Rotation, after.Value.Rotation, t);
                    scale = Lerp(before.Value.Scale, after.Value.Scale, t);
                    break;

                default:
                    position = after.Value.Position;
                    rotation = after.Value.Rotation;
                    scale = after.Value.Scale;
                    break;
            }

            // 检查是否追上了最新数据
            if (t >= 0.95f && after == _sampleBuffer.Last)
            {
                State = InterpolationState.CaughtUp;
            }
            else
            {
                State = InterpolationState.Interpolating;
            }

            return CreateResult(State, t, position, rotation, scale);
        }

        /// <summary>
        /// 外推
        /// </summary>
        private InterpolationResult Extrapolate(long targetTime, LinkedListNode<TimedSample> lastSample)
        {
            State = InterpolationState.Extrapolating;

            long timeDiff = targetTime - lastSample.Value.RemoteTimestamp;
            float t = timeDiff / 1000f; // 转换为秒
            float duration = (float)timeDiff;

            // 如果外推起始点变了, 更新外推参数
            if (_extrapolationStart.RemoteTimestamp != lastSample.Value.RemoteTimestamp)
            {
                _extrapolationStart = lastSample.Value;
                _extrapolationStartTime = lastSample.Value.RemoteTimestamp;

                // 计算速度(从最近的样本推算)
                _extrapolationVelocity = EstimateVelocity(lastSample);
                _extrapolationAcceleration = System.Numerics.Vector3.Zero;

                // 尝试估算加速度
                var prevNode = lastSample.Previous;
                if (prevNode != null)
                {
                    var prevVel = EstimateVelocity(prevNode);
                    float dt = (lastSample.Value.RemoteTimestamp - prevNode.Value.RemoteTimestamp) / 1000f;
                    if (dt > 0)
                    {
                        _extrapolationAcceleration = (_extrapolationVelocity - prevVel) / dt;
                    }
                }
            }

            System.Numerics.Vector3 position;

            switch (Config.Extrapolation)
            {
                case ExtrapolationMethod.Linear:
                    position = lastSample.Value.Position + _extrapolationVelocity * t;
                    break;

                case ExtrapolationMethod.Quadratic:
                    position = lastSample.Value.Position + _extrapolationVelocity * t + 0.5f * _extrapolationAcceleration * t * t;
                    break;

                case ExtrapolationMethod.Physics:
                    // 简单物理: 速度 * 时间, 加上速度衰减
                    var dampedVel = _extrapolationVelocity * (float)Math.Pow(Config.ExtrapolationVelocityDamping, t * 10);
                    position = lastSample.Value.Position + dampedVel * t;
                    // 加上重力
                    position += new System.Numerics.Vector3(0, -4.9f * t * t, 0); // g/2 * t^2
                    break;

                default:
                    position = lastSample.Value.Position;
                    break;
            }

            return CreateResult(InterpolationState.Extrapolating, 0, position,
                lastSample.Value.Rotation, lastSample.Value.Scale, duration);
        }

        /// <summary>
        /// 估算速度
        /// </summary>
        private System.Numerics.Vector3 EstimateVelocity(LinkedListNode<TimedSample> node)
        {
 if (node.Previous != null)
            {
                float dt = (node.Value.RemoteTimestamp - node.Previous.Value.RemoteTimestamp) / 1000f;
                if (dt > 0.001f)
                {
                    return (node.Value.Position - node.Previous.Value.Position) / dt;
                }
            }

            if (node.Value.Velocity.Length() > 0)
            {
                return node.Value.Velocity;
            }

            return System.Numerics.Vector3.Zero;
        }

        /// <summary>
        /// 触发位置修正(当收到服务端确认位置偏差太大时)
        /// </summary>
        public void TriggerCorrection(System.Numerics.Vector3 targetPos, System.Numerics.Quaternion targetRot)
        {
            _correctionTarget = targetPos;
            _correctionTargetRotation = targetRot;
            _correctionProgress = 0;
            _isCorrecting = true;
            _totalCorrections++;
        }

        /// <summary>
        /// 应用位置修正
        /// </summary>
        private void ApplyCorrection(long currentTime)
        {
            _correctionProgress += Config.PositionCorrectionRate * 0.016f; // 大约60fps

            if (_correctionProgress >= 1.0f)
            {
                _displayPosition = _correctionTarget;
                _displayRotation = _correctionTargetRotation;
                _isCorrecting = false;
                return;
            }

            // 平滑修正
            _displayPosition = System.Numerics.Vector3.Lerp(
                _displayPosition, _correctionTarget,
                _correctionProgress);
            _displayRotation = Slerp(
                _displayRotation, _correctionTargetRotation,
                _correctionProgress);
        }

        // ==================== 插值算法 ====================

        /// <summary>
        /// 线性插值
        /// </summary>
        private static System.Numerics.Vector3 Lerp(System.Numerics.Vector3 a, System.Numerics.Vector3 b, float t)
        {
            return a + (b - a) * t;
        }

        /// <summary>
        /// 球面线性插值
        /// </summary>
        private static System.Numerics.Quaternion Slerp(System.Numerics.Quaternion a, System.Numerics.Quaternion b, float t)
        {
            float dot = a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;

            if (dot < 0)
            {
                b = new System.Numerics.Quaternion(-b.X, -b.Y, -b.Z, -b.W);
                dot = -dot;
            }

            if (dot > 0.9995f)
            {
                return Normalize(new System.Numerics.Quaternion(
                    a.X + (b.X - a.X) * t,
                    a.Y + (b.Y - a.Y) * t,
                    a.Z + (b.Z - a.Z) * t,
                    a.W + (b.W - a.W) * t));
            }

            float theta = (float)Math.Acos(Math.Clamp(dot, -1, 1));
            float sinTheta = (float)Math.Sin(theta);
            float wa = (float)Math.Sin((1 - t) * theta) / sinTheta;
            float wb = (float)Math.Sin(t * theta) / sinTheta;

            return new System.Numerics.Quaternion(
                wa * a.X + wb * b.X,
                wa * a.Y + wb * b.Y,
                wa * a.Z + wb * b.Z,
                wa * a.W + wb * b.W);
        }

        /// <summary>
        /// 四元数归一化
        /// </summary>
        private static System.Numerics.Quaternion Normalize(System.Numerics.Quaternion q)
        {
            float len = (float)Math.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
            if (len < 0.0001f) return System.Numerics.Quaternion.Identity;
            return new System.Numerics.Quaternion(q.X / len, q.Y / len, q.Z / len, q.W / len);
        }

        /// <summary>
        /// 三次样条插值
        /// </summary>
        private System.Numerics.Vector3 CubicSplineInterpolate(
            LinkedListNode<TimedSample> before, LinkedListNode<TimedSample> after, float t)
        {
            // 获取额外的前后点
            var p0 = before.Previous?.Value ?? before.Value;
            var p1 = before.Value;
            var p2 = after.Value;
            var p3 = after.Next?.Value ?? after.Value;

            // 标准三次样条
            float t2 = t * t;
            float t3 = t2 * t;

            return 0.5f * (
                (2 * p1.Position) +
                (-p0.Position + p2.Position) * t +
                (2 * p0.Position - 5 * p1.Position + 4 * p2.Position - p3.Position) * t2 +
                (-p0.Position + 3 * p1.Position - 3 * p2.Position + p3.Position) * t3
            );
        }

        /// <summary>
        /// Catmull-Rom插值
        /// </summary>
        private System.Numerics.Vector3 CatmullRomInterpolate(
            LinkedListNode<TimedSample> before, LinkedListNode<TimedSample> after, float t)
        {
            var p0 = before.Previous?.Value ?? before.Value;
            var p1 = before.Value;
            var p2 = after.Value;
            var p3 = after.Next?.Value ?? after.Value;

            float t2 = t * t;
            float t3 = t2 * t;

            return 0.5f * (
                2 * p1.Position +
                (-p0.Position + p2.Position) * t +
                (2 * p0.Position - 5 * p1.Position + 4 * p2.Position - p3.Position) * t2 +
                (-p0.Position + 3 * p1.Position - 3 * p2.Position + p3.Position) * t3
            );
        }

        /// <summary>
        /// Hermite插值(考虑速度)
        /// </summary>
        private System.Numerics.Vector3 HermiteInterpolate(
            LinkedListNode<TimedSample> before, LinkedListNode<TimedSample> after,
            float t, float timeRange)
        {
            float dt = timeRange / 1000f;
            if (dt < 0.001f) dt = 0.001f;

            // 切线(速度)
            System.Numerics.Vector3 m0 = EstimateVelocity(before);
            System.Numerics.Vector3 m1 = EstimateVelocity(after);

            // Hermite基函数
            float t2 = t * t;
            float t3 = t2 * t;

            float h00 = 2 * t3 - 3 * t2 + 1;
            float h10 = t3 - 2 * t2 + t;
            float h01 = -2 * t3 + 3 * t2;
            float h11 = t3 - t2;

            return h00 * before.Value.Position + h10 * m0 * dt + h01 * after.Value.Position + h11 * m1 * dt;
        }

        /// <summary>
        /// 指数衰减插值(平滑过渡, 适合消除抖动)
        /// </summary>
        private System.Numerics.Vector3 ExponentialDecayInterpolate(
            System.Numerics.Vector3 from, System.Numerics.Vector3 to, float t)
        {
            // 指数衰减: 快速接近目标, 然后慢慢收敛
            float factor = 1 - (float)Math.Exp(-Config.ExponentialDecayRate * t);
            return Lerp(from, to, factor);
        }

        /// <summary>
        /// 创建结果
        /// </summary>
        private InterpolationResult CreateResult(
            InterpolationState state, float progress,
            System.Numerics.Vector3? pos = null,
            System.Numerics.Quaternion? rot = null,
            System.Numerics.Vector3? scale = null,
            float extrapDuration = 0)
        {
            return new InterpolationResult
            {
                Position = pos ?? _displayPosition,
                Rotation = rot ?? _displayRotation,
                Scale = scale ?? _displayScale,
                State = state,
                Progress = progress,
                ExtrapolationDuration = extrapDuration
            };
        }

        /// <summary>
        /// 获取缓冲区中的样本数量
        /// </summary>
        public int SampleCount => _sampleBuffer?.Count ?? 0;

        /// <summary>
        /// 清空缓冲区
        /// </summary>
        public void Clear()
        {
            _sampleBuffer?.Clear();
            State = InterpolationState.WaitingForData;
            _isCorrecting = false;
        }

        /// <summary>
        /// 获取统计信息
        /// </summary>
        public string GetStatistics()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"  对象 {ObjectId}:");
            sb.AppendLine($"    状态: {State}");
            sb.AppendLine($"    样本数: {SampleCount}");
            sb.AppendLine($"    收到样本: {_totalSamplesReceived}");
            sb.AppendLine($"    插值次数: {_totalInterpolations}");
            sb.AppendLine($"    外推次数: {_totalExtrapolations}");
            sb.AppendLine($"    修正次数: {_totalCorrections}");
            sb.AppendLine($"    最大外推时长: {_maxExtrapolationUsed:F1}ms");
            return sb.ToString();
        }

        public void Dispose()
        {
            _disposed = true;
            _valid = false;
            _sampleBuffer?.Clear();
        }
    }

    /// <summary>
    /// 网络插值器管理器
    /// 管理所有对象的插值器
    /// </summary>
    public class NetworkInterpolator : IDisposable
    {
        /// <summary>所有对象的插值器</summary>
        private readonly Dictionary<uint, ObjectInterpolator> _interpolators = new Dictionary<uint, ObjectInterpolator>();

        /// <summary>默认配置</summary>
        public InterpolationConfig DefaultConfig { get; set; } = new InterpolationConfig();

        /// <summary>全局时钟偏移</summary>
        public long GlobalTimeOffset { get; set; } = 0;

        private readonly object _lock = new object();
        private bool _disposed;

        /// <summary>
        /// 注册对象的插值器
        /// </summary>
        public ObjectInterpolator Register(uint objectId, InterpolationConfig config = null)
        {
            lock (_lock)
            {
                if (_interpolators.ContainsKey(objectId))
                {
                    return _interpolators[objectId];
                }

                var interp = new ObjectInterpolator(objectId, config ?? DefaultConfig);
                _interpolators[objectId] = interp;
                return interp;
            }
        }

        /// <summary>
        /// 注销插值器
        /// </summary>
        public void Unregister(uint objectId)
        {
            lock (_lock)
            {
                if (_interpolators.TryGetValue(objectId, out var interp))
                {
                    interp.Dispose();
                    _interpolators.Remove(objectId);
                }
            }
        }

        /// <summary>
        /// 添加样本
        /// </summary>
        public void AddSample(uint objectId, TimedSample sample)
        {
            ObjectInterpolator interp;
            lock (_lock)
            {
                if (!_interpolators.TryGetValue(objectId, out interp))
                {
                    interp = Register(objectId);
                }
            }
            interp.AddSample(sample);
        }

        /// <summary>
        /// 获取插值结果
        /// </summary>
        public InterpolationResult GetInterpolatedValue(uint objectId)
        {
            long currentTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return GetInterpolatedValue(objectId, currentTime);
        }

        /// <summary>
        /// 获取插值结果(指定时间)
        /// </summary>
        public InterpolationResult GetInterpolatedValue(uint objectId, long currentTime)
        {
            lock (_lock)
            {
                if (_interpolators.TryGetValue(objectId, out var interp))
                {
                    return interp.GetInterpolatedValue(currentTime + GlobalTimeOffset);
                }
            }
            return default;
        }

        /// <summary>
        /// 更新所有插值器(每帧调用)
        /// </summary>
        public void UpdateAll()
        {
            long currentTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            lock (_lock)
            {
                foreach (var interp in _interpolators.Values)
                {
                    interp.GetInterpolatedValue(currentTime + GlobalTimeOffset);
                }
            }
        }

        /// <summary>
        /// 触发位置修正
        /// </summary>
        public void TriggerCorrection(uint objectId, System.Numerics.Vector3 targetPos,
            System.Numerics.Quaternion targetRot)
        {
            lock (_lock)
            {
                if (_interpolators.TryGetValue(objectId, out var interp))
                {
                    interp.TriggerCorrection(targetPos, targetRot);
                }
            }
        }

        /// <summary>
        /// 获取所有统计信息
        /// </summary>
        public string GetStatisticsReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("===== 插值器统计 =====");
            sb.AppendLine($"插值器数量: {_interpolators.Count}");
            sb.AppendLine($"全局时间偏移: {GlobalTimeOffset}ms");

            lock (_lock)
            {
                foreach (var interp in _interpolators.Values)
                {
                    sb.Append(interp.GetStatistics());
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// 清除所有插值器
        /// </summary>
        public void ClearAll()
        {
            lock (_lock)
            {
                foreach (var interp in _interpolators.Values)
                {
                    interp.Dispose();
                }
                _interpolators.Clear();
            }
        }

        /// <summary>
        /// 获取插值器数量
        /// </summary>
        public int Count
        {
            get { lock (_lock) { return _interpolators.Count; } }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            if (disposing)
            {
                ClearAll();
            }
        }

        ~NetworkInterpolator()
        {
            Dispose(false);
        }
    }
}