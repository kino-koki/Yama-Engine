using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace ShanyuEngine.Sequencer
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - 序列器曲线编辑系统 (Sequence Curve Editor System)
    // --------------------------------------------------------------------------
    // 提供曲线关键帧编辑与评估：
    //   - SequenceCurve / CurveKeyframe
    //   - 6 种插值模式 (Constant/Linear/Cubic/SmoothStep/CatmullRom/Custom)
    //   - CurveEditor：编辑器操作（选择/添加/删除/拖拽/贝塞尔手柄/缩放/平移/框选/对齐/平铺）
    //   - 多曲线编辑、曲线操作、曲线评估
    //   - 曲线导入导出（CSV/JSON/二进制）
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────────────────
    // 枚举定义
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>曲线插值模式。</summary>
    public enum CurveInterpolationMode
    {
        /// <summary>常量（阶跃）：保持前一关键帧值。</summary>
        Constant,
        /// <summary>线性插值。</summary>
        Linear,
        /// <summary>三次贝塞尔插值。</summary>
        Cubic,
        /// <summary>平滑阶梯（Hermite smoothstep）。</summary>
        SmoothStep,
        /// <summary>Catmull-Rom 样条插值。</summary>
        CatmullRom,
        /// <summary>自定义插值（通过委托）。</summary>
        Custom
    }

    /// <summary>外推模式：曲线范围之外的取值方式。</summary>
    public enum CurveExtrapolationMode
    {
        /// <summary>循环。</summary>
        Cycle,
        /// <summary>循环（含镜像）。</summary>
        CycleOffset,
        /// <summary>延伸最后一个值。</summary>
        Clamp,
        /// <summary>线性延伸。</summary>
        Linear,
        /// <summary>回到零。</summary>
        Constant
    }

    /// <summary>关键帧切线模式。</summary>
    public enum TangentMode
    {
        /// <summary>自动平滑。</summary>
        Auto,
        /// <summary>用户自定义。</summary>
        User,
        /// <summary>线性（切线指向相邻关键帧）。</summary>
        Linear,
        /// <summary>断裂（入切线和出切线独立）。</summary>
        Broken
    }

    // ──────────────────────────────────────────────────────────────────────────
    // CurveKeyframe - 关键帧
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>曲线关键帧：曲线上的一个数据点，携带切线信息。</summary>
    public class CurveKeyframe : IComparable<CurveKeyframe>
    {
        /// <summary>关键帧唯一标识。</summary>
        public Guid Id { get; } = Guid.NewGuid();

        /// <summary>时间（秒/帧）。</summary>
        public double Time { get; set; }

        /// <summary>数值。</summary>
        public float Value { get; set; }

        /// <summary>入切线（控制左侧曲率，X 分量代表时间偏移，Y 分量代表值偏移）。</summary>
        public Vector2 TangentIn { get; set; }

        /// <summary>出切线（控制右侧曲率）。</summary>
        public Vector2 TangentOut { get; set; }

        /// <summary>插值模式（此帧到下一帧之间使用）。</summary>
        public CurveInterpolationMode InterpolationMode { get; set; } = CurveInterpolationMode.Cubic;

        /// <summary>切线模式。</summary>
        public TangentMode TangentMode { get; set; } = TangentMode.Auto;

        /// <summary>是否切线断裂（入/出切线独立）。</summary>
        public bool IsTangentBroken { get; set; }

        /// <summary>是否选中（编辑器状态）。</summary>
        public bool IsSelected { get; set; }

        public CurveKeyframe() { }

        public CurveKeyframe(double time, float value)
        {
            Time = time;
            Value = value;
        }

        public CurveKeyframe(double time, float value, Vector2 tangentIn, Vector2 tangentOut)
        {
            Time = time;
            Value = value;
            TangentIn = tangentIn;
            TangentOut = tangentOut;
        }

        public int CompareTo(CurveKeyframe? other)
        {
            if (other == null) return 1;
            return Time.CompareTo(other.Time);
        }

        /// <summary>自动计算平滑切线（基于相邻关键帧）。</summary>
        public void ComputeAutoTangent(CurveKeyframe? prev, CurveKeyframe? next)
        {
            float slope = 0;

            if (prev != null && next != null)
            {
                float dt = (float)(next.Time - prev.Time);
                if (dt > 1e-6f)
                    slope = (next.Value - prev.Value) / dt;
            }
            else if (prev != null)
            {
                float dt = (float)(Time - prev.Time);
                if (dt > 1e-6f)
                    slope = (Value - prev.Value) / dt;
            }
            else if (next != null)
            {
                float dt = (float)(next.Time - Time);
                if (dt > 1e-6f)
                    slope = (next.Value - Value) / dt;
            }

            // 默认切线长度为相邻帧间距的 1/3
            float tangentLength = 0.3f;
            if (prev != null) tangentLength = (float)Math.Min((Time - prev.Time) * 0.33, tangentLength);
            if (next != null) tangentLength = Math.Min(tangentLength, (float)(next.Time - Time) * 0.33f);

            if (!IsTangentBroken)
            {
                TangentIn = new Vector2(-tangentLength, slope * tangentLength);
                TangentOut = new Vector2(tangentLength, slope * tangentLength);
            }
            else
            {
                if (TangentIn == Vector2.Zero)
                    TangentIn = new Vector2(-tangentLength, slope * tangentLength);
                if (TangentOut == Vector2.Zero)
                    TangentOut = new Vector2(tangentLength, slope * tangentLength);
            }
        }

        /// <summary>序列化为 JSON 字典。</summary>
        public Dictionary<string, object> ToDict()
        {
            return new Dictionary<string, object>
            {
                ["time"] = Time,
                ["value"] = Value,
                ["tangentInX"] = TangentIn.X,
                ["tangentInY"] = TangentIn.Y,
                ["tangentOutX"] = TangentOut.X,
                ["tangentOutY"] = TangentOut.Y,
                ["interpolation"] = InterpolationMode.ToString(),
                ["tangentMode"] = TangentMode.ToString(),
                ["broken"] = IsTangentBroken
            };
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // SequenceCurve - 序列曲线
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 序列曲线：关键帧容器与求值器。
    /// 支持多种插值模式和外推模式。
    /// </summary>
    public class SequenceCurve
    {
        /// <summary>曲线名称。</summary>
        public string Name { get; set; } = "Curve";

        /// <summary>曲线显示颜色。</summary>
        public uint Color { get; set; } = 0xFFFFFFFF;

        /// <summary>预外推模式（曲线起点之前）。</summary>
        public CurveExtrapolationMode PreExtrapolation { get; set; } = CurveExtrapolationMode.Clamp;

        /// <summary>后外推模式（曲线终点之后）。</summary>
        public CurveExtrapolationMode PostExtrapolation { get; set; } = CurveExtrapolationMode.Clamp;

        /// <summary>自定义插值委托（CurveInterpolationMode.Custom 时使用）。</summary>
        public Func<CurveKeyframe, CurveKeyframe, float, float>? CustomInterpolation { get; set; }

        /// <summary>关键帧列表。</summary>
        private readonly List<CurveKeyframe> _keyframes = new();

        /// <summary>关键帧只读访问。</summary>
        public IReadOnlyList<CurveKeyframe> Keyframes => _keyframes;

        /// <summary>关键帧数量。</summary>
        public int KeyframeCount => _keyframes.Count;

        /// <summary>曲线是否为空。</summary>
        public bool IsEmpty => _keyframes.Count == 0;

        /// <summary>曲线时间范围。</summary>
        public (double startTime, double endTime) TimeRange
        {
            get
            {
                if (IsEmpty) return (0, 0);
                return (_keyframes[0].Time, _keyframes[_keyframes.Count - 1].Time);
            }
        }

        public SequenceCurve() { }

        public SequenceCurve(string name)
        {
            Name = name;
        }

        // ── 关键帧管理 ──────────────────────────────────────────────

        /// <summary>添加关键帧。</summary>
        public CurveKeyframe AddKeyframe(double time, float value)
        {
            var key = new CurveKeyframe(time, value);
            return AddKeyframe(key);
        }

        /// <summary>添加关键帧（带完整参数）。</summary>
        public CurveKeyframe AddKeyframe(CurveKeyframe keyframe)
        {
            _keyframes.Add(keyframe);
            _keyframes.Sort();
            RecomputeAutoTangents();
            return keyframe;
        }

        /// <summary>移除关键帧。</summary>
        public bool RemoveKeyframe(CurveKeyframe keyframe)
        {
            if (_keyframes.Remove(keyframe))
            {
                RecomputeAutoTangents();
                return true;
            }
            return false;
        }

        /// <summary>按 ID 移除关键帧。</summary>
        public bool RemoveKeyframeById(Guid id)
        {
            for (int i = 0; i < _keyframes.Count; i++)
            {
                if (_keyframes[i].Id == id)
                {
                    _keyframes.RemoveAt(i);
                    RecomputeAutoTangents();
                    return true;
                }
            }
            return false;
        }

        /// <summary>按时间查找关键帧。</summary>
        public CurveKeyframe? FindKeyframeAt(double time, double epsilon = 0.001)
        {
            foreach (var kf in _keyframes)
            {
                if (Math.Abs(kf.Time - time) < epsilon) return kf;
            }
            return null;
        }

        /// <summary>重新计算所有自动切线。</summary>
        public void RecomputeAutoTangents()
        {
            for (int i = 0; i < _keyframes.Count; i++)
            {
                if (_keyframes[i].TangentMode == TangentMode.Auto)
                {
                    var prev = i > 0 ? _keyframes[i - 1] : null;
                    var next = i < _keyframes.Count - 1 ? _keyframes[i + 1] : null;
                    _keyframes[i].ComputeAutoTangent(prev, next);
                }
            }
        }

        // ── 曲线评估 ──────────────────────────────────────────────

        /// <summary>在指定时间采样曲线值。</summary>
        public float Evaluate(double time)
        {
            if (IsEmpty) return 0;

            // 外推：时间在曲线范围之前
            if (time < _keyframes[0].Time)
            {
                return ApplyPreExtrapolation(time);
            }

            // 外推：时间在曲线范围之后
            if (time > _keyframes[_keyframes.Count - 1].Time)
            {
                return ApplyPostExtrapolation(time);
            }

            // 精确匹配
            for (int i = 0; i < _keyframes.Count; i++)
            {
                if (Math.Abs(_keyframes[i].Time - time) < 1e-8)
                    return _keyframes[i].Value;
            }

            // 找到插值区间
            for (int i = 0; i < _keyframes.Count - 1; i++)
            {
                if (time >= _keyframes[i].Time && time <= _keyframes[i + 1].Time)
                {
                    return Interpolate(_keyframes[i], _keyframes[i + 1], time);
                }
            }

            return _keyframes[_keyframes.Count - 1].Value;
        }

        /// <summary>在两个关键帧之间插值。</summary>
        private float Interpolate(CurveKeyframe from, CurveKeyframe to, double time)
        {
            float t = (float)((time - from.Time) / (to.Time - from.Time));
            t = Math.Clamp(t, 0, 1);

            switch (from.InterpolationMode)
            {
                case CurveInterpolationMode.Constant:
                    return from.Value;

                case CurveInterpolationMode.Linear:
                    return from.Value + (to.Value - from.Value) * t;

                case CurveInterpolationMode.Cubic:
                    return CubicBezier(from, to, t);

                case CurveInterpolationMode.SmoothStep:
                    float s = t * t * (3f - 2f * t);
                    return from.Value + (to.Value - from.Value) * s;

                case CurveInterpolationMode.CatmullRom:
                    return CatmullRomEvaluate(from, to, t);

                case CurveInterpolationMode.Custom:
                    if (CustomInterpolation != null)
                        return CustomInterpolation(from, to, t);
                    return from.Value + (to.Value - from.Value) * t;

                default:
                    return from.Value + (to.Value - from.Value) * t;
            }
        }

        /// <summary>三次贝塞尔插值。</summary>
        private static float CubicBezier(CurveKeyframe from, CurveKeyframe to, float t)
        {
            // 控制点
            float p0y = from.Value;
            float p1y = from.Value + from.TangentOut.Y;
            float p2y = to.Value + to.TangentIn.Y;
            float p3y = to.Value;

            float p0x = 0;
            float p1x = from.TangentOut.X;
            float p2x = 1 + to.TangentIn.X;
            float p3x = 1;

            // 求解贝塞尔时间参数（二分搜索）
            float solvedT = SolveBezierX(p0x, p1x, p2x, p3x, t, 8);

            // 计算 Y 值
            float u = 1 - solvedT;
            return u * u * u * p0y +
                   3 * u * u * solvedT * p1y +
                   3 * u * solvedT * solvedT * p2y +
                   solvedT * solvedT * solvedT * p3y;
        }

        /// <summary>二分法求解贝塞尔 X 参数。</summary>
        private static float SolveBezierX(float p0, float p1, float p2, float p3, float x, int iterations)
        {
            float lo = 0f, hi = 1f;
            float mid = 0.5f;

            for (int i = 0; i < iterations; i++)
            {
                float bx = BezierValue(p0, p1, p2, p3, mid);
                if (Math.Abs(bx - x) < 1e-6f) break;
                if (bx < x) lo = mid; else hi = mid;
                mid = (lo + hi) * 0.5f;
            }
            return mid;
        }

        private static float BezierValue(float p0, float p1, float p2, float p3, float t)
        {
            float u = 1 - t;
            return u * u * u * p0 +
                   3 * u * u * t * p1 +
                   3 * u * t * t * p2 +
                   t * t * t * p3;
        }

        /// <summary>Catmull-Rom 插值（需要前后关键帧）。</summary>
        private float CatmullRomEvaluate(CurveKeyframe from, CurveKeyframe to, float t)
        {
            int fromIdx = _keyframes.IndexOf(from);
            float p0 = fromIdx > 0 ? _keyframes[fromIdx - 1].Value : from.Value;
            float p1 = from.Value;
            float p2 = to.Value;
            float p3 = fromIdx < _keyframes.Count - 2 ? _keyframes[fromIdx + 2].Value : to.Value;

            float t2 = t * t;
            float t3 = t2 * t;
            return 0.5f * ((2 * p1) +
                (-p0 + p2) * t +
                (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2 +
                (-p0 + 3 * p1 - 3 * p2 + p3) * t3);
        }

        // ── 外推 ──────────────────────────────────────────────────

        private float ApplyPreExtrapolation(double time)
        {
            if (IsEmpty) return 0;
            var first = _keyframes[0];
            var second = _keyframes.Count > 1 ? _keyframes[1] : first;

            return PreExtrapolation switch
            {
                CurveExtrapolationMode.Clamp => first.Value,
                CurveExtrapolationMode.Constant => 0,
                CurveExtrapolationMode.Linear => EvaluateLinearExtrapolation(first, second, time),
                CurveExtrapolationMode.Cycle => EvaluateCycle(time, false),
                CurveExtrapolationMode.CycleOffset => EvaluateCycle(time, true),
                _ => first.Value
            };
        }

        private float ApplyPostExtrapolation(double time)
        {
            if (IsEmpty) return 0;
            var last = _keyframes[_keyframes.Count - 1];
            var prev = _keyframes.Count > 1 ? _keyframes[_keyframes.Count - 2] : last;

            return PostExtrapolation switch
            {
                CurveExtrapolationMode.Clamp => last.Value,
                CurveExtrapolationMode.Constant => 0,
                CurveExtrapolationMode.Linear => EvaluateLinearExtrapolation(prev, last, time),
                CurveExtrapolationMode.Cycle => EvaluateCycle(time, false),
                CurveExtrapolationMode.CycleOffset => EvaluateCycle(time, true),
                _ => last.Value
            };
        }

        private float EvaluateLinearExtrapolation(CurveKeyframe a, CurveKeyframe b, double time)
        {
            float dt = (float)(b.Time - a.Time);
            if (Math.Abs(dt) < 1e-6f) return a.Value;
            float slope = (b.Value - a.Value) / dt;
            return a.Value + slope * (float)(time - a.Time);
        }

        private float EvaluateCycle(double time, bool offset)
        {
            if (_keyframes.Count < 2) return 0;
            double range = _keyframes[_keyframes.Count - 1].Time - _keyframes[0].Time;
            if (range < 1e-8) return _keyframes[0].Value;

            double wrapped = ((time % range) + range) % range;
            float baseVal = Evaluate(wrapped);

            if (offset)
            {
                int cycles = (int)Math.Floor((time - _keyframes[0].Time) / range);
                float valRange = _keyframes[_keyframes.Count - 1].Value - _keyframes[0].Value;
                return baseVal + cycles * valRange;
            }

            return baseVal;
        }

        // ── 导入导出 ──────────────────────────────────────────────

        /// <summary>导出为 CSV 格式。</summary>
        public string ExportCSV()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Time,Value,TangentInX,TangentInY,TangentOutX,TangentOutY,InterpolationMode");
            foreach (var kf in _keyframes)
            {
                sb.AppendLine($"{kf.Time},{kf.Value},{kf.TangentIn.X},{kf.TangentIn.Y},{kf.TangentOut.X},{kf.TangentOut.Y},{kf.InterpolationMode}");
            }
            return sb.ToString();
        }

        /// <summary>从 CSV 导入。</summary>
        public void ImportCSV(string csv)
        {
            _keyframes.Clear();
            var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 1; i < lines.Length; i++)
            {
                var parts = lines[i].Split(',');
                if (parts.Length < 7) continue;

                var kf = new CurveKeyframe
                {
                    Time = double.Parse(parts[0].Trim()),
                    Value = float.Parse(parts[1].Trim()),
                    TangentIn = new Vector2(float.Parse(parts[2]), float.Parse(parts[3])),
                    TangentOut = new Vector2(float.Parse(parts[4]), float.Parse(parts[5])),
                    InterpolationMode = Enum.Parse<CurveInterpolationMode>(parts[6].Trim())
                };
                _keyframes.Add(kf);
            }
            _keyframes.Sort();
        }

        /// <summary>导出为 JSON 格式。</summary>
        public string ExportJSON()
        {
            var data = new Dictionary<string, object>
            {
                ["name"] = Name,
                ["preExtrapolation"] = PreExtrapolation.ToString(),
                ["postExtrapolation"] = PostExtrapolation.ToString(),
                ["keyframes"] = new List<object>()
            };

            var kfList = (List<object>)data["keyframes"];
            foreach (var kf in _keyframes)
                kfList.Add(kf.ToDict());

            return JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        }

        /// <summary>从 JSON 导入。</summary>
        public void ImportJSON(string json)
        {
            _keyframes.Clear();
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("name", out var nameProp))
                Name = nameProp.GetString() ?? Name;

            if (root.TryGetProperty("keyframes", out var kfArray))
            {
                foreach (var kfElem in kfArray.EnumerateArray())
                {
                    var kf = new CurveKeyframe
                    {
                        Time = kfElem.GetProperty("time").GetDouble(),
                        Value = (float)kfElem.GetProperty("value").GetDouble(),
                        TangentIn = new Vector2(
                            (float)kfElem.GetProperty("tangentInX").GetDouble(),
                            (float)kfElem.GetProperty("tangentInY").GetDouble()),
                        TangentOut = new Vector2(
                            (float)kfElem.GetProperty("tangentOutX").GetDouble(),
                            (float)kfElem.GetProperty("tangentOutY").GetDouble())
                    };

                    if (kfElem.TryGetProperty("interpolation", out var interpProp))
                        Enum.TryParse(interpProp.GetString(), out kf.InterpolationMode);

                    _keyframes.Add(kf);
                }
            }
            _keyframes.Sort();
        }

        /// <summary>导出为二进制格式。</summary>
        public byte[] ExportBinary()
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            writer.Write(Name);
            writer.Write((int)PreExtrapolation);
            writer.Write((int)PostExtrapolation);
            writer.Write(_keyframes.Count);

            foreach (var kf in _keyframes)
            {
                writer.Write(kf.Time);
                writer.Write(kf.Value);
                writer.Write(kf.TangentIn.X);
                writer.Write(kf.TangentIn.Y);
                writer.Write(kf.TangentOut.X);
                writer.Write(kf.TangentOut.Y);
                writer.Write((int)kf.InterpolationMode);
                writer.Write(kf.IsTangentBroken);
            }

            return ms.ToArray();
        }

        /// <summary>从二进制导入。</summary>
        public void ImportBinary(byte[] data)
        {
            _keyframes.Clear();
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);

            Name = reader.ReadString();
            PreExtrapolation = (CurveExtrapolationMode)reader.ReadInt32();
            PostExtrapolation = (CurveExtrapolationMode)reader.ReadInt32();
            int count = reader.ReadInt32();

            for (int i = 0; i < count; i++)
            {
                var kf = new CurveKeyframe
                {
                    Time = reader.ReadDouble(),
                    Value = reader.ReadSingle(),
                    TangentIn = new Vector2(reader.ReadSingle(), reader.ReadSingle()),
                    TangentOut = new Vector2(reader.ReadSingle(), reader.ReadSingle()),
                    InterpolationMode = (CurveInterpolationMode)reader.ReadInt32(),
                    IsTangentBroken = reader.ReadBoolean()
                };
                _keyframes.Add(kf);
            }
            _keyframes.Sort();
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // CurveEvaluator - 曲线评估工具
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>曲线评估工具集：采样、积分、微分、长度计算。</summary>
    public static class CurveEvaluator
    {
        /// <summary>均匀采样曲线，返回采样点列表。</summary>
        public static List<(double time, float value)> Sample(SequenceCurve curve, double startTime, double endTime, int sampleCount)
        {
            var result = new List<(double, float)>(sampleCount);
            double step = (endTime - startTime) / Math.Max(1, sampleCount - 1);

            for (int i = 0; i < sampleCount; i++)
            {
                double t = startTime + step * i;
                result.Add((t, curve.Evaluate(t)));
            }
            return result;
        }

        /// <summary>计算曲线的数值积分（梯形法）。</summary>
        public static float Integrate(SequenceCurve curve, double startTime, double endTime, int steps = 100)
        {
            if (steps < 1) steps = 1;
            double step = (endTime - startTime) / steps;
            float sum = 0;
            float prev = curve.Evaluate(startTime);

            for (int i = 1; i <= steps; i++)
            {
                float curr = curve.Evaluate(startTime + step * i);
                sum += (prev + curr) * (float)step * 0.5f;
                prev = curr;
            }
            return sum;
        }

        /// <summary>计算曲线在指定时间点的数值导数。</summary>
        public static float Differentiate(SequenceCurve curve, double time, double delta = 0.001)
        {
            float y1 = curve.Evaluate(time - delta);
            float y2 = curve.Evaluate(time + delta);
            return (y2 - y1) / (float)(2 * delta);
        }

        /// <summary>计算曲线近似弧长。</summary>
        public static float CalculateLength(SequenceCurve curve, double startTime, double endTime, int steps = 100)
        {
            if (steps < 1) steps = 1;
            double step = (endTime - startTime) / steps;
            float length = 0;
            float prev = curve.Evaluate(startTime);

            for (int i = 1; i <= steps; i++)
            {
                float curr = curve.Evaluate(startTime + step * i);
                float dy = curr - prev;
                length += MathF.Sqrt((float)step * (float)step + dy * dy);
                prev = curr;
            }
            return length;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // CurveOperations - 曲线操作
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>曲线操作工具集：复制、粘贴、镜像、反转、循环、缩放、偏移、烘焙、简化。</summary>
    public static class CurveOperations
    {
        /// <summary>复制曲线。</summary>
        public static SequenceCurve Clone(SequenceCurve source)
        {
            var clone = new SequenceCurve(source.Name + " (Copy)")
            {
                PreExtrapolation = source.PreExtrapolation,
                PostExtrapolation = source.PostExtrapolation,
                Color = source.Color
            };

            foreach (var kf in source.Keyframes)
            {
                clone.AddKeyframe(new CurveKeyframe(
                    kf.Time, kf.Value,
                    kf.TangentIn, kf.TangentOut)
                {
                    InterpolationMode = kf.InterpolationMode,
                    TangentMode = kf.TangentMode,
                    IsTangentBroken = kf.IsTangentBroken
                });
            }
            return clone;
        }

        /// <summary>镜像曲线（水平翻转，以零值为中心）。</summary>
        public static void MirrorHorizontal(SequenceCurve curve)
        {
            foreach (var kf in curve.Keyframes)
            {
                kf.Value = -kf.Value;
                kf.TangentIn = new Vector2(kf.TangentIn.X, -kf.TangentIn.Y);
                kf.TangentOut = new Vector2(kf.TangentOut.X, -kf.TangentOut.Y);
            }
        }

        /// <summary>镜像曲线（垂直翻转，以曲线中点时间为中心）。</summary>
        public static void MirrorVertical(SequenceCurve curve)
        {
            var (start, end) = curve.TimeRange;
            double mid = (start + end) * 0.5;

            foreach (var kf in curve.Keyframes.ToList())
            {
                kf.Time = 2 * mid - kf.Time;
                kf.TangentIn = new Vector2(-kf.TangentIn.X, kf.TangentIn.Y);
                kf.TangentOut = new Vector2(-kf.TangentOut.X, kf.TangentOut.Y);
            }
        }

        /// <summary>反转曲线（时间轴反转）。</summary>
        public static void Reverse(SequenceCurve curve)
        {
            var (start, end) = curve.TimeRange;
            foreach (var kf in curve.Keyframes)
            {
                kf.Time = start + end - kf.Time;
                // 切线方向也需要反转
                kf.TangentIn = new Vector2(-kf.TangentIn.X, kf.TangentIn.Y);
                kf.TangentOut = new Vector2(-kf.TangentOut.X, kf.TangentOut.Y);
            }
        }

        /// <summary>缩放曲线（时间缩放 + 值缩放）。</summary>
        public static void Scale(SequenceCurve curve, float timeScale = 1.0f, float valueScale = 1.0f, double pivotTime = 0)
        {
            foreach (var kf in curve.Keyframes)
            {
                kf.Time = pivotTime + (kf.Time - pivotTime) * timeScale;
                kf.Value *= valueScale;
                kf.TangentIn = new Vector2(kf.TangentIn.X * timeScale, kf.TangentIn.Y * valueScale);
                kf.TangentOut = new Vector2(kf.TangentOut.X * timeScale, kf.TangentOut.Y * valueScale);
            }
        }

        /// <summary>偏移曲线（时间偏移 + 值偏移）。</summary>
        public static void Offset(SequenceCurve curve, double timeOffset = 0, float valueOffset = 0)
        {
            foreach (var kf in curve.Keyframes)
            {
                kf.Time += timeOffset;
                kf.Value += valueOffset;
            }
        }

        /// <summary>循环曲线（复制曲线内容 N 次）。</summary>
        public static void LoopCurve(SequenceCurve curve, int times)
        {
            if (times <= 1) return;
            var (start, end) = curve.TimeRange;
            double duration = end - start;
            if (duration <= 0) return;

            var keys = new List<CurveKeyframe>(curve.Keyframes);
            for (int i = 1; i < times; i++)
            {
                float offset = (float)(i * duration);
                foreach (var kf in keys)
                {
                    curve.AddKeyframe(new CurveKeyframe(
                        kf.Time + offset,
                        kf.Value + i * (curve.Keyframes[curve.KeyframeCount - 1].Value - curve.Keyframes[0].Value),
                        kf.TangentIn, kf.TangentOut)
                    {
                        InterpolationMode = kf.InterpolationMode
                    });
                }
            }
        }

        /// <summary>烘焙曲线：将曲线按固定帧率采样为新的常量关键帧。</summary>
        public static SequenceCurve Bake(SequenceCurve source, double frameRate, double startTime, double endTime)
        {
            var baked = new SequenceCurve(source.Name + " (Baked)");
            double step = 1.0 / frameRate;

            for (double t = startTime; t <= endTime; t += step)
            {
                baked.AddKeyframe(new CurveKeyframe(t, source.Evaluate(t))
                {
                    InterpolationMode = CurveInterpolationMode.Linear
                });
            }
            return baked;
        }

        /// <summary>简化曲线：使用 Ramer-Douglas-Peucker 算法减少关键帧数量。</summary>
        public static void Simplify(SequenceCurve curve, float tolerance = 0.1f)
        {
            if (curve.KeyframeCount <= 2) return;

            var sampled = CurveEvaluator.Sample(curve, curve.TimeRange.startTime, curve.TimeRange.endTime, 200);

            // 收集采样点
            var points = new List<Vector2>(sampled.Count);
            foreach (var (time, value) in sampled)
                points.Add(new Vector2((float)time, value));

            var simplified = RdpSimplify(points, tolerance);

            // 重建关键帧
            curve.ImportCSV(string.Empty); // 清空
            foreach (var pt in simplified)
            {
                curve.AddKeyframe(pt.X, pt.Y);
            }
        }

        /// <summary>Ramer-Douglas-Peucker 简化算法。</summary>
        private static List<Vector2> RdpSimplify(List<Vector2> points, float tolerance)
        {
            if (points.Count <= 2) return new List<Vector2>(points);

            float maxDist = 0;
            int maxIdx = 0;

            Vector2 start = points[0];
            Vector2 end = points[points.Count - 1];

            for (int i = 1; i < points.Count - 1; i++)
            {
                float d = PerpendicularDistance(points[i], start, end);
                if (d > maxDist)
                {
                    maxDist = d;
                    maxIdx = i;
                }
            }

            if (maxDist > tolerance)
            {
                var left = RdpSimplify(points.GetRange(0, maxIdx + 1), tolerance);
                var right = RdpSimplify(points.GetRange(maxIdx, points.Count - maxIdx), tolerance);

                var result = new List<Vector2>(left);
                result.RemoveAt(result.Count - 1); // 去掉重复点
                result.AddRange(right);
                return result;
            }

            return new List<Vector2> { start, end };
        }

        private static float PerpendicularDistance(Vector2 pt, Vector2 lineStart, Vector2 lineEnd)
        {
            float dx = lineEnd.X - lineStart.X;
            float dy = lineEnd.Y - lineStart.Y;
            float lenSq = dx * dx + dy * dy;

            if (lenSq < 1e-8f)
                return Vector2.Distance(pt, lineStart);

            float t = Math.Clamp(((pt.X - lineStart.X) * dx + (pt.Y - lineStart.Y) * dy) / lenSq, 0, 1);
            Vector2 proj = new Vector2(lineStart.X + t * dx, lineStart.Y + t * dy);
            return Vector2.Distance(pt, proj);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // CurveEditor - 曲线编辑器
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 曲线编辑器：管理曲线的选择、操作、显示参数。
    /// 支持贝塞尔手柄编辑、框选、对齐、平铺等编辑器功能。
    /// </summary>
    public class CurveEditor
    {
        /// <summary>当前编辑的曲线列表（多曲线编辑）。</summary>
        private readonly List<SequenceCurve> _curves = new();

        /// <summary>选中的关键帧列表。</summary>
        private readonly HashSet<Guid> _selectedKeyframeIds = new();

        /// <summary>获取所有曲线。</summary>
        public IReadOnlyList<SequenceCurve> Curves => _curves;

        /// <summary>选中的关键帧。</summary>
        public IReadOnlyCollection<Guid> SelectedKeyframeIds => _selectedKeyframeIds;

        // ── 视图参数 ──────────────────────────────────────────────

        /// <summary>视图时间范围（可见区域）。</summary>
        public (double startTime, double endTime) ViewTimeRange { get; set; } = (0, 10);

        /// <summary>视图值范围。</summary>
        public (float minValue, float maxValue) ViewValueRange { get; set; } = (-1, 1);

        /// <summary>视图平移（像素偏移）。</summary>
        public Vector2 ViewPan { get; set; }

        /// <summary>视图缩放。</summary>
        public Vector2 ViewZoom { get; set; } = Vector2.One;

        /// <summary>是否显示网格。</summary>
        public bool ShowGrid { get; set; } = true;

        /// <summary>网格线间距。</summary>
        public double GridSpacing { get; set; } = 0.25;

        // ── 曲线管理 ──────────────────────────────────────────────

        /// <summary>添加曲线到编辑器。</summary>
        public void AddCurve(SequenceCurve curve)
        {
            if (curve != null && !_curves.Contains(curve))
                _curves.Add(curve);
        }

        /// <summary>从编辑器移除曲线。</summary>
        public bool RemoveCurve(SequenceCurve curve)
        {
            return curve != null && _curves.Remove(curve);
        }

        // ── 选择操作 ──────────────────────────────────────────────

        /// <summary>选中关键帧。</summary>
        public void SelectKeyframe(Guid keyframeId)
        {
            _selectedKeyframeIds.Add(keyframeId);
        }

        /// <summary>取消选中关键帧。</summary>
        public void DeselectKeyframe(Guid keyframeId)
        {
            _selectedKeyframeIds.Remove(keyframeId);
        }

        /// <summary>切换关键帧选择。</summary>
        public void ToggleKeyframeSelection(Guid keyframeId)
        {
            if (!_selectedKeyframeIds.Add(keyframeId))
                _selectedKeyframeIds.Remove(keyframeId);
        }

        /// <summary>全选。</summary>
        public void SelectAll()
        {
            foreach (var curve in _curves)
                foreach (var kf in curve.Keyframes)
                    _selectedKeyframeIds.Add(kf.Id);
        }

        /// <summary>取消全选。</summary>
        public void DeselectAll()
        {
            _selectedKeyframeIds.Clear();
        }

        /// <summary>框选（选择在矩形范围内的关键帧）。</summary>
        public void BoxSelect(double startTime, float minValue, double endTime, float maxValue)
        {
            foreach (var curve in _curves)
            {
                foreach (var kf in curve.Keyframes)
                {
                    if (kf.Time >= startTime && kf.Time <= endTime &&
                        kf.Value >= minValue && kf.Value <= maxValue)
                    {
                        _selectedKeyframeIds.Add(kf.Id);
                    }
                }
            }
        }

        // ── 编辑操作 ──────────────────────────────────────────────

        /// <summary>添加关键帧到指定曲线。</summary>
        public CurveKeyframe? AddKeyframe(SequenceCurve curve, double time, float value)
        {
            if (!_curves.Contains(curve)) return null;
            return curve.AddKeyframe(time, value);
        }

        /// <summary>删除选中的关键帧。</summary>
        public int DeleteSelectedKeyframes()
        {
            int count = 0;
            foreach (var curve in _curves)
            {
                for (int i = curve.Keyframes.Count - 1; i >= 0; i--)
                {
                    if (_selectedKeyframeIds.Contains(curve.Keyframes[i].Id))
                    {
                        curve.RemoveKeyframe(curve.Keyframes[i]);
                        count++;
                    }
                }
            }
            _selectedKeyframeIds.Clear();
            return count;
        }

        /// <summary>拖拽选中的关键帧。</summary>
        public void DragSelectedKeyframes(double deltaTime, float deltaValue)
        {
            foreach (var curve in _curves)
            {
                foreach (var kf in curve.Keyframes)
                {
                    if (_selectedKeyframeIds.Contains(kf.Id))
                    {
                        kf.Time += deltaTime;
                        kf.Value += deltaValue;
                    }
                }
            }
            // 重新排序
            foreach (var curve in _curves)
                curve.RecomputeAutoTangents();
        }

        /// <summary>平铺关键帧（均匀分布时间）。</summary>
        public void DistributeSelectedKeyframes()
        {
            // 收集选中的关键帧
            var selected = new List<CurveKeyframe>();
            foreach (var curve in _curves)
                foreach (var kf in curve.Keyframes)
                    if (_selectedKeyframeIds.Contains(kf.Id))
                        selected.Add(kf);

            if (selected.Count < 3) return;

            selected.Sort((a, b) => a.Time.CompareTo(b.Time));

            double startTime = selected[0].Time;
            double endTime = selected[selected.Count - 1].Time;
            double step = (endTime - startTime) / (selected.Count - 1);

            for (int i = 1; i < selected.Count - 1; i++)
            {
                selected[i].Time = startTime + step * i;
            }
        }

        /// <summary>对齐关键帧值（将选中关键帧值设为平均值）。</summary>
        public void AlignSelectedValues()
        {
            var selected = new List<CurveKeyframe>();
            foreach (var curve in _curves)
                foreach (var kf in curve.Keyframes)
                    if (_selectedKeyframeIds.Contains(kf.Id))
                        selected.Add(kf);

            if (selected.Count == 0) return;

            float avg = 0;
            foreach (var kf in selected) avg += kf.Value;
            avg /= selected.Count;

            foreach (var kf in selected) kf.Value = avg;
        }

        // ── 贝塞尔手柄操作 ──────────────────────────────────────────

        /// <summary>设置关键帧的入切线。</summary>
        public void SetTangentIn(Guid keyframeId, Vector2 tangentIn)
        {
            var kf = FindKeyframe(keyframeId);
            if (kf == null) return;

            kf.TangentIn = tangentIn;

            // 如果不是断裂模式，镜像出切线
            if (!kf.IsTangentBroken)
            {
                kf.TangentOut = new Vector2(-tangentIn.X, -tangentIn.Y);
            }
        }

        /// <summary>设置关键帧的出切线。</summary>
        public void SetTangentOut(Guid keyframeId, Vector2 tangentOut)
        {
            var kf = FindKeyframe(keyframeId);
            if (kf == null) return;

            kf.TangentOut = tangentOut;

            if (!kf.IsTangentBroken)
            {
                kf.TangentIn = new Vector2(-tangentOut.X, -tangentOut.Y);
            }
        }

        /// <summary>切换切线断裂模式。</summary>
        public void ToggleTangentBroken(Guid keyframeId)
        {
            var kf = FindKeyframe(keyframeId);
            if (kf != null)
                kf.IsTangentBroken = !kf.IsTangentBroken;
        }

        // ── 视图操作 ──────────────────────────────────────────────

        /// <summary>缩放视图。</summary>
        public void ZoomView(float zoomDelta, Vector2 center)
        {
            float newZoomX = Math.Clamp(ViewZoom.X * (1f / zoomDelta), 0.01f, 100f);
            float newZoomY = Math.Clamp(ViewZoom.Y * (1f / zoomDelta), 0.01f, 100f);
            ViewZoom = new Vector2(newZoomX, newZoomY);
        }

        /// <summary>平移视图。</summary>
        public void PanView(Vector2 delta)
        {
            ViewPan += delta;
        }

        /// <summary>适配视图到曲线内容。</summary>
        public void FitViewToContent()
        {
            if (_curves.Count == 0) return;

            double minTime = double.MaxValue;
            double maxTime = double.MinValue;
            float minValue = float.MaxValue;
            float maxValue = float.MinValue;

            foreach (var curve in _curves)
            {
                foreach (var kf in curve.Keyframes)
                {
                    if (kf.Time < minTime) minTime = kf.Time;
                    if (kf.Time > maxTime) maxTime = kf.Time;
                    if (kf.Value < minValue) minValue = kf.Value;
                    if (kf.Value > maxValue) maxValue = kf.Value;
                }
            }

            if (minTime == double.MaxValue) return;

            ViewTimeRange = (minTime, maxTime);
            ViewValueRange = (minValue, maxValue);
            ViewPan = Vector2.Zero;
            ViewZoom = Vector2.One;
        }

        /// <summary>查找关键帧。</summary>
        private CurveKeyframe? FindKeyframe(Guid id)
        {
            foreach (var curve in _curves)
                foreach (var kf in curve.Keyframes)
                    if (kf.Id == id) return kf;
            return null;
        }
    }
}
