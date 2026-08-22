using System;
using System.Collections.Generic;
using System.Numerics;

namespace ShanyuEngine.Input.Enhanced
{
    #region InputProcessor 基类

    /// <summary>
    /// 输入处理器基类 —— 全局输入后处理
    /// 处理器在修改器之后、动作状态机之前对值进行变换
    /// 类似 UE 的 Input Processor 或 Unity 的 Processors
    /// 与 InputModifier 的区别：Processor 作用于动作级别，Modifier 作用于绑定级别
    /// </summary>
    public abstract class InputProcessor
    {
        /// <summary>处理器名称</summary>
        public string Name { get; set; }

        /// <summary>执行优先级 (数值越小越先执行，默认 0)</summary>
        public int Priority { get; set; } = 0;

        /// <summary>是否启用</summary>
        public bool Enabled { get; set; } = true;

        protected InputProcessor(string name = "Processor")
        {
            Name = name;
        }

        /// <summary>处理 float 值</summary>
        public abstract float Process(float value);

        /// <summary>处理 Vector2 值 (可选重写)</summary>
        public virtual Vector2 Process(Vector2 value)
        {
            return new Vector2(Process(value.X), Process(value.Y));
        }

        /// <summary>处理 Vector3 值 (可选重写)</summary>
        public virtual Vector3 Process(Vector3 value)
        {
            return new Vector3(Process(value.X), Process(value.Y), Process(value.Z));
        }
    }

    #endregion

    #region DeadzoneProcessor —— 全局死区

    /// <summary>
    /// 全局死区处理器 —— 对所有输入值应用死区过滤
    /// </summary>
    public class DeadzoneProcessor : InputProcessor
    {
        /// <summary>内死区半径 (低于此值归零)</summary>
        public float Min { get; set; } = 0.1f;

        /// <summary>外死区 (高于此值裁剪)</summary>
        public float Max { get; set; } = 1.0f;

        /// <summary>是否使用径向死区</summary>
        public bool Radial { get; set; } = true;

        public DeadzoneProcessor(float min = 0.1f, float max = 1.0f, bool radial = true) : base("Deadzone")
        {
            Min = Math.Max(0f, min);
            Max = Math.Max(Min, max);
            Radial = radial;
        }

        public override float Process(float value)
        {
            if (!Enabled) return value;
            if (Math.Abs(value) <= Min) return 0f;
            if (Math.Abs(value) >= Max) return Math.Sign(value) * Max;
            // 重新映射 [Min, Max] → [0, Max]
            float sign = Math.Sign(value);
            return sign * ((Math.Abs(value) - Min) / (Max - Min) * Max);
        }

        public override Vector2 Process(Vector2 value)
        {
            if (!Enabled) return value;

            if (Radial)
            {
                float mag = value.Length();
                if (mag <= Min) return Vector2.Zero;
                if (mag >= Max) return Vector2.Normalize(value) * Max;
                float newMag = ((mag - Min) / (Max - Min)) * Max;
                return value * (newMag / mag);
            }
            return new Vector2(Process(value.X), Process(value.Y));
        }

        public override Vector3 Process(Vector3 value)
        {
            if (!Enabled) return value;

            if (Radial)
            {
                float mag = value.Length();
                if (mag <= Min) return Vector3.Zero;
                if (mag >= Max) return Vector3.Normalize(value) * Max;
                float newMag = ((mag - Min) / (Max - Min)) * Max;
                return value * (newMag / mag);
            }
            return new Vector3(Process(value.X), Process(value.Y), Process(value.Z));
        }
    }

    #endregion

    #region NormalizeProcessor —— 归一化

    /// <summary>
    /// 归一化处理器 —— 将值归一化到 [-1, 1] 范围
    /// </summary>
    public class NormalizeProcessor : InputProcessor
    {
        /// <summary>是否保持符号 (正/负)</summary>
        public bool PreserveSign { get; set; } = true;

        /// <summary>归一化模式</summary>
        public enum NormalizeMode
        {
            /// <summary>裁剪到 [-1, 1]</summary>
            Clamp,
            /// <summary>缩放到 [-1, 1] (按最大值比例)</summary>
            Scale,
            /// <summary>只归一化向量长度</summary>
            VectorLength
        }

        /// <summary>归一化模式</summary>
        public NormalizeMode Mode { get; set; } = NormalizeMode.Clamp;

        public NormalizeProcessor() : base("Normalize") { }

        public NormalizeProcessor(NormalizeMode mode) : base("Normalize")
        {
            Mode = mode;
        }

        public override float Process(float value)
        {
            if (!Enabled) return value;
            switch (Mode)
            {
                case NormalizeMode.Clamp:
                    return Math.Clamp(value, -1f, 1f);
                case NormalizeMode.Scale:
                    return value != 0f ? value / Math.Abs(value) * Math.Sign(value) : 0f;
                default:
                    return Math.Clamp(value, -1f, 1f);
            }
        }

        public override Vector2 Process(Vector2 value)
        {
            if (!Enabled) return value;
            if (Mode == NormalizeMode.VectorLength)
            {
                float len = value.Length();
                return len > 0.001f ? value / len : Vector2.Zero;
            }
            return new Vector2(Process(value.X), Process(value.Y));
        }

        public override Vector3 Process(Vector3 value)
        {
            if (!Enabled) return value;
            if (Mode == NormalizeMode.VectorLength)
            {
                float len = value.Length();
                return len > 0.001f ? value / len : Vector3.Zero;
            }
            return new Vector3(Process(value.X), Process(value.Y), Process(value.Z));
        }
    }

    #endregion

    #region ScaleProcessor —— 全局缩放

    /// <summary>
    /// 全局缩放处理器
    /// </summary>
    public class ScaleProcessor : InputProcessor
    {
        /// <summary>缩放系数</summary>
        public float Factor { get; set; } = 1.0f;

        /// <summary>是否对 Vector 使用统一缩放</summary>
        public bool Uniform { get; set; } = true;

        /// <summary>各分量缩放系数</summary>
        public Vector3 PerAxisFactor { get; set; } = Vector3.One;

        public ScaleProcessor(float factor = 1.0f) : base("Scale")
        {
            Factor = factor;
        }

        public ScaleProcessor(float x, float y, float z) : base("Scale")
        {
            Uniform = false;
            PerAxisFactor = new Vector3(x, y, z);
        }

        public override float Process(float value)
        {
            return Enabled ? value * Factor : value;
        }

        public override Vector2 Process(Vector2 value)
        {
            if (!Enabled) return value;
            return Uniform
                ? value * Factor
                : new Vector2(value.X * PerAxisFactor.X, value.Y * PerAxisFactor.Y);
        }

        public override Vector3 Process(Vector3 value)
        {
            if (!Enabled) return value;
            return Uniform ? value * Factor : value * PerAxisFactor;
        }
    }

    #endregion

    #region InvertProcessor —— 全局反转

    /// <summary>
    /// 全局反转处理器
    /// </summary>
    public class InvertProcessor : InputProcessor
    {
        /// <summary>是否反转 X/一维值</summary>
        public bool InvertX { get; set; } = true;
        /// <summary>是否反转 Y</summary>
        public bool InvertY { get; set; } = false;
        /// <summary>是否反转 Z</summary>
        public bool InvertZ { get; set; } = false;

        public InvertProcessor(bool invertX = true) : base("Invert")
        {
            InvertX = invertX;
        }

        public InvertProcessor(bool invertX, bool invertY, bool invertZ) : base("Invert")
        {
            InvertX = invertX;
            InvertY = invertY;
            InvertZ = invertZ;
        }

        public override float Process(float value)
        {
            return Enabled && InvertX ? -value : value;
        }

        public override Vector2 Process(Vector2 value)
        {
            if (!Enabled) return value;
            return new Vector2(InvertX ? -value.X : value.X, InvertY ? -value.Y : value.Y);
        }

        public override Vector3 Process(Vector3 value)
        {
            if (!Enabled) return value;
            return new Vector3(InvertX ? -value.X : value.X, InvertY ? -value.Y : value.Y, InvertZ ? -value.Z : value.Z);
        }
    }

    #endregion

    #region SmoothProcessor —— 全局平滑

    /// <summary>
    /// 全局平滑处理器 —— 对所有输入值应用时间维度的平滑
    /// </summary>
    public class SmoothProcessor : InputProcessor
    {
        /// <summary>平滑因子 (EMA alpha, 0~1)</summary>
        public float SmoothingFactor { get; set; } = 0.2f;

        [System.Text.Json.Serialization.JsonIgnore]
        private float _smoothedFloat;
        [System.Text.Json.Serialization.JsonIgnore]
        private Vector2 _smoothedVec2;
        [System.Text.Json.Serialization.JsonIgnore]
        private Vector3 _smoothedVec3;
        [System.Text.Json.Serialization.JsonIgnore]
        private bool _initialized;

        public SmoothProcessor(float smoothingFactor = 0.2f) : base("Smooth")
        {
            SmoothingFactor = Math.Clamp(smoothingFactor, 0.01f, 0.99f);
        }

        public override float Process(float value)
        {
            if (!Enabled) return value;
            if (!_initialized) { _smoothedFloat = value; _initialized = true; }
            else _smoothedFloat += (value - _smoothedFloat) * SmoothingFactor;
            return _smoothedFloat;
        }

        public override Vector2 Process(Vector2 value)
        {
            if (!Enabled) return value;
            if (!_initialized) { _smoothedVec2 = value; _initialized = true; }
            else _smoothedVec2 += (value - _smoothedVec2) * SmoothingFactor;
            return _smoothedVec2;
        }

        public override Vector3 Process(Vector3 value)
        {
            if (!Enabled) return value;
            if (!_initialized) { _smoothedVec3 = value; _initialized = true; }
            else _smoothedVec3 += (value - _smoothedVec3) * SmoothingFactor;
            return _smoothedVec3;
        }

        /// <summary>重置平滑状态</summary>
        public void ResetSmooth()
        {
            _initialized = false;
            _smoothedFloat = 0f;
            _smoothedVec2 = Vector2.Zero;
            _smoothedVec3 = Vector3.Zero;
        }
    }

    #endregion

    #region ProcessorPipeline —— 处理器管线

    /// <summary>
    /// 处理器管线 —— 按优先级顺序执行多个处理器
    /// 值流: input → processor[0](priority=0) → processor[1](priority=1) → ... → output
    /// </summary>
    public class ProcessorPipeline
    {
        /// <summary>处理器列表 (已按优先级排序)</summary>
        public List<InputProcessor> Processors { get; } = new List<InputProcessor>();

        /// <summary>管线名称</summary>
        public string Name { get; set; } = "ProcessorPipeline";

        public ProcessorPipeline() { }

        public ProcessorPipeline(string name)
        {
            Name = name;
        }

        /// <summary>添加处理器并自动排序</summary>
        public void Add(InputProcessor processor)
        {
            Processors.Add(processor);
            SortByPriority();
        }

        /// <summary>移除处理器</summary>
        public bool Remove(InputProcessor processor) => Processors.Remove(processor);

        /// <summary>清空所有处理器</summary>
        public void Clear() => Processors.Clear();

        private void SortByPriority()
        {
            Processors.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        }

        /// <summary>处理 float 值</summary>
        public float Process(float value)
        {
            foreach (var proc in Processors)
            {
                if (proc.Enabled)
                    value = proc.Process(value);
            }
            return value;
        }

        /// <summary>处理 Vector2 值</summary>
        public Vector2 Process(Vector2 value)
        {
            foreach (var proc in Processors)
            {
                if (proc.Enabled)
                    value = proc.Process(value);
            }
            return value;
        }

        /// <summary>处理 Vector3 值</summary>
        public Vector3 Process(Vector3 value)
        {
            foreach (var proc in Processors)
            {
                if (proc.Enabled)
                    value = proc.Process(value);
            }
            return value;
        }

        /// <summary>重置所有处理器的运行时状态</summary>
        public void ResetAll()
        {
            foreach (var proc in Processors)
            {
                if (proc is SmoothProcessor sp) sp.ResetSmooth();
            }
        }
    }

    #endregion
}
