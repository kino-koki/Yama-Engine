using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ShanyuEngine.Sequencer;

namespace ShanyuEngine.DataSystem
{
    // ====================================================================
    // 曲线表格系统（CurveTableSystem）
    // ====================================================================
    // 类似 UE UCurveTable。提供多名称→多曲线集合、查找、采样和导入。
    // 常用曲线表：伤害曲线、经验曲线、掉落概率曲线、难度曲线。
    // ====================================================================

    /// <summary>
    /// 浮点曲线关键帧。定义曲线上一个点的（时间，值）对。
    /// </summary>

    /// <summary>
    /// 曲线插值模式。
    /// </summary>
    public enum CurveInterpMode
    {
        /// <summary>线性插值</summary>
        Linear,
        /// <summary>阶梯（常量保持）</summary>
        Constant,
        /// <summary>三次 Hermite 插值（需要切线信息）</summary>
        Cubic,
        /// <summary>自动平滑（Catmull-Rom 样式）</summary>
        Auto
    }

    /// <summary>
    /// 浮点曲线数据（FCurveData）。存储一条曲线的关键帧数组，
    /// 提供采样、长度和遍历能力。
    /// </summary>
    public sealed class CurveData
    {
        /// <summary>关键帧列表（按时间排序）</summary>
        public List<CurveKeyframe> Keys { get; } = new();

        /// <summary>曲线外采样模式</summary>
        public CurveExtrapolation Extrapolation { get; set; } = CurveExtrapolation.Clamp;

        /// <summary>曲线长度（最大时间值）</summary>
        public float Length => Keys.Count > 0 ? Keys[Keys.Count - 1].Time : 0f;

        /// <summary>曲线最小时间值</summary>
        public float MinTime => Keys.Count > 0 ? Keys[0].Time : 0f;

        /// <summary>曲线值范围</summary>
        public (float min, float max) ValueRange
        {
            get
            {
                if (Keys.Count == 0) return (0f, 0f);
                float min = Keys[0].Value, max = Keys[0].Value;
                foreach (var kf in Keys)
                {
                    if (kf.Value < min) min = kf.Value;
                    if (kf.Value > max) max = kf.Value;
                }
                return (min, max);
            }
        }

        /// <summary>关键帧数量</summary>
        public int KeyCount => Keys.Count;

        /// <summary>添加关键帧（自动按时间排序插入）</summary>
        public void AddKey(CurveKeyframe key)
        {
            Keys.Add(key);
            Keys.Sort();
        }

        /// <summary>添加关键帧（简化版）</summary>
        public void AddKey(float time, float value, CurveInterpMode mode = CurveInterpMode.Linear)
        {
            AddKey(new CurveKeyframe(time, value, mode));
        }

        /// <summary>移除指定时间的关键帧</summary>
        public bool RemoveKey(float time)
        {
            for (int i = 0; i < Keys.Count; i++)
            {
                if (Math.Abs(Keys[i].Time - time) < 1e-6f)
                {
                    Keys.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        /// <summary>移除指定索引的关键帧</summary>
        public void RemoveKeyAt(int index)
        {
            if (index >= 0 && index < Keys.Count)
                Keys.RemoveAt(index);
        }

        /// <summary>清空关键帧</summary>
        public void Clear() => Keys.Clear();

        /// <summary>
        /// 在指定时间采样曲线值。
        /// 支持线性、常量、三次和自动插值。
        /// </summary>
        public float Evaluate(float time)
        {
            if (Keys.Count == 0) return 0f;
            if (Keys.Count == 1) return Keys[0].Value;

            // 超出范围的外推
            if (time <= Keys[0].Time)
            {
                return Extrapolation switch
                {
                    CurveExtrapolation.Clamp => Keys[0].Value,
                    CurveExtrapolation.LinearExtend => Lerp(Keys[0].Value, Keys[1].Value,
                        time / (Keys[1].Time - Keys[0].Time)),
                    CurveExtrapolation.Cycle => EvaluateCyclic(time),
                    _ => Keys[0].Value
                };
            }

            if (time >= Keys[Keys.Count - 1].Time)
            {
                return Extrapolation switch
                {
                    CurveExtrapolation.Clamp => Keys[Keys.Count - 1].Value,
                    CurveExtrapolation.LinearExtend => Lerp(
                        Keys[Keys.Count - 2].Value, Keys[Keys.Count - 1].Value, 1f),
                    CurveExtrapolation.Cycle => EvaluateCyclic(time),
                    _ => Keys[Keys.Count - 1].Value
                };
            }

            // 找到时间所在的区间
            int idx = 0;
            for (int i = 0; i < Keys.Count - 1; i++)
            {
                if (time >= Keys[i].Time && time <= Keys[i + 1].Time)
                {
                    idx = i;
                    break;
                }
            }

            var k0 = Keys[idx];
            var k1 = Keys[idx + 1];
            float dt = k1.Time - k0.Time;
            if (dt < 1e-8f) return k0.Value;

            float alpha = (time - k0.Time) / dt;

            return k0.InterpMode switch
            {
                CurveInterpMode.Constant => k0.Value,
                CurveInterpMode.Cubic => EvaluateCubic(k0, k1, alpha, dt),
                CurveInterpMode.Auto => EvaluateAuto(idx, alpha, dt),
                _ => Lerp(k0.Value, k1.Value, alpha)
            };
        }

        /// <summary>获取指定时间处的切线斜率</summary>
        public float EvaluateDerivative(float time)
        {
            const float delta = 0.001f;
            float v0 = Evaluate(time - delta);
            float v1 = Evaluate(time + delta);
            return (v1 - v0) / (2f * delta);
        }

        private float Lerp(float a, float b, float t) => a + (b - a) * t;

        private float EvaluateCubic(CurveKeyframe k0, CurveKeyframe k1, float alpha, float dt)
        {
            float t0 = float.IsNaN(k0.LeaveTangent) ? (k1.Value - k0.Value) / dt : k0.LeaveTangent;
            float t1 = float.IsNaN(k1.ArriveTangent) ? (k1.Value - k0.Value) / dt : k1.ArriveTangent;

            // Hermite basis
            float t2 = alpha * alpha;
            float t3 = t2 * alpha;
            float h00 = 2f * t3 - 3f * t2 + 1f;
            float h10 = t3 - 2f * t2 + alpha;
            float h01 = -2f * t3 + 3f * t2;
            float h11 = t3 - t2;

            return h00 * k0.Value + h10 * t0 * dt + h01 * k1.Value + h11 * t1 * dt;
        }

        private float EvaluateAuto(int idx, float alpha, float dt)
        {
            // Catmull-Rom 样式自动切线
            float p0 = idx > 0 ? Keys[idx - 1].Value : Keys[idx].Value;
            float p1 = Keys[idx].Value;
            float p2 = Keys[idx + 1].Value;
            float p3 = idx + 2 < Keys.Count ? Keys[idx + 2].Value : Keys[idx + 1].Value;

            // Catmull-Rom 到 Hermite 切线
            float t0 = 0.5f * (p2 - p0) / dt;
            float t1 = 0.5f * (p3 - p1) / dt;

            var k0 = new CurveKeyframe(Keys[idx].Time, p1, CurveInterpMode.Cubic, 0f, t0);
            var k1 = new CurveKeyframe(Keys[idx + 1].Time, p2, CurveInterpMode.Cubic, t1, 0f);
            return EvaluateCubic(k0, k1, alpha, dt);
        }

        private float EvaluateCyclic(float time)
        {
            if (Keys.Count < 2) return Keys.Count > 0 ? Keys[0].Value : 0f;
            float range = Length - MinTime;
            if (range < 1e-8f) return Keys[0].Value;
            float wrapped = MinTime + ((time - MinTime) % range + range) % range;
            return Evaluate(wrapped);
        }
    }

    /// <summary>
    /// 曲线外推模式。
    /// </summary>
    public enum CurveExtrapolation
    {
        /// <summary>钳位到两端值</summary>
        Clamp,
        /// <summary>线性延伸</summary>
        LinearExtend,
        /// <summary>循环</summary>
        Cycle
    }

    /// <summary>
    /// 曲线表格。存储多条命名曲线的集合。
    /// 类似 UE UCurveTable，通过名称查找和采样曲线。
    /// </summary>
    public sealed class CurveTable : DataAsset
    {
        private readonly Dictionary<string, CurveData> _curves = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _curveNames = new();

        /// <summary>曲线数量</summary>
        public int CurveCount => _curves.Count;

        /// <summary>所有曲线名称</summary>
        public IReadOnlyList<string> CurveNames => _curveNames;

        public CurveTable()
        {
            Category = "Data/Curves";
        }

        /// <summary>添加曲线</summary>
        public void AddCurve(string name, CurveData curve)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("Curve name cannot be empty.", nameof(name));
            _curves[name] = curve ?? throw new ArgumentNullException(nameof(curve));
            if (!_curveNames.Contains(name)) _curveNames.Add(name);
        }

        /// <summary>移除曲线</summary>
        public bool RemoveCurve(string name)
        {
            if (_curves.Remove(name))
            {
                _curveNames.Remove(name);
                return true;
            }
            return false;
        }

        /// <summary>获取曲线</summary>
        public CurveData? GetCurve(string name) =>
            _curves.TryGetValue(name, out var curve) ? curve : null;

        /// <summary>在指定曲线上采样</summary>
        public float Evaluate(string curveName, float time, float defaultValue = 0f)
        {
            if (_curves.TryGetValue(curveName, out var curve))
                return curve.Evaluate(time);
            return defaultValue;
        }

        /// <summary>在指定曲线上采样切线斜率</summary>
        public float EvaluateDerivative(string curveName, float time, float defaultValue = 0f)
        {
            if (_curves.TryGetValue(curveName, out var curve))
                return curve.EvaluateDerivative(time);
            return defaultValue;
        }

        /// <summary>判断是否存在指定曲线</summary>
        public bool HasCurve(string name) => _curves.ContainsKey(name);

        /// <summary>按名称获取所有曲线</summary>
        public IReadOnlyDictionary<string, CurveData> GetAllCurves() => _curves;

        /// <summary>清空所有曲线</summary>
        public void Clear()
        {
            _curves.Clear();
            _curveNames.Clear();
        }

        // ── 导入导出 ──

        /// <summary>
        /// 从 CSV 导入曲线表格。
        /// 格式：每行代表一条曲线，格式为：
        ///   CurveName, Time0, Value0, Time1, Value1, ...
        /// 首行为表头：Name,Time0,Val0,Time1,Val1,...
        /// </summary>
        public int ImportCsv(string csv)
        {
            Clear();
            var lines = csv.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            if (lines.Length < 2) return 0;

            // 解析表头获取时间列数
            var headers = ParseCsvLine(lines[0]);
            int imported = 0;

            for (int i = 1; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;

                var values = ParseCsvLine(line);
                if (values.Length < 3) continue;

                string curveName = values[0];
                var curve = new CurveData();

                // 值对 (time, value)
                for (int j = 1; j + 1 < values.Length; j += 2)
                {
                    if (float.TryParse(values[j], NumberStyles.Float, CultureInfo.InvariantCulture, out float time) &&
                        float.TryParse(values[j + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float val))
                    {
                        curve.AddKey(time, val);
                    }
                }

                if (curve.KeyCount > 0)
                {
                    AddCurve(curveName, curve);
                    imported++;
                }
            }
            return imported;
        }

        /// <summary>从 CSV 文件导入</summary>
        public int ImportCsvFile(string filePath)
        {
            if (!File.Exists(filePath)) return 0;
            return ImportCsv(File.ReadAllText(filePath, Encoding.UTF8));
        }

        /// <summary>导出为 CSV</summary>
        public string ExportCsv()
        {
            var sb = new StringBuilder();

            // 先统计最大关键帧数以确定列数
            int maxKeys = _curves.Values.Max(c => c.KeyCount);
            if (maxKeys == 0) return string.Empty;

            // 表头
            var headers = new List<string> { "Name" };
            for (int i = 0; i < maxKeys; i++)
            {
                headers.Add($"Time{i}");
                headers.Add($"Val{i}");
            }
            sb.AppendLine(string.Join(",", headers));

            // 数据行
            foreach (var kvp in _curves)
            {
                var values = new List<string> { kvp.Key };
                foreach (var kf in kvp.Value.Keys)
                {
                    values.Add(kf.Time.ToString(CultureInfo.InvariantCulture));
                    values.Add(kf.Value.ToString(CultureInfo.InvariantCulture));
                }
                sb.AppendLine(string.Join(",", values));
            }

            return sb.ToString();
        }

        /// <summary>导出为 CSV 文件</summary>
        public bool ExportCsvFile(string filePath)
        {
            try
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(filePath, ExportCsv(), Encoding.UTF8);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to export CurveTable CSV: {ex.Message}");
                return false;
            }
        }

        private static string[] ParseCsvLine(string line)
        {
            var result = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"' && !inQuotes) { inQuotes = true; continue; }
                if (c == '"' && inQuotes)
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else { inQuotes = false; }
                    continue;
                }
                if (c == ',' && !inQuotes) { result.Add(sb.ToString()); sb.Clear(); continue; }
                sb.Append(c);
            }
            result.Add(sb.ToString());
            return result.ToArray();
        }

        // ── 常用曲线表工厂 ──

        /// <summary>创建伤害倍率曲线表（按等级→伤害倍率）</summary>
        public static CurveTable CreateDamageCurveTable(int maxLevel = 100, float baseDamage = 10f, float growth = 1.15f)
        {
            var table = new CurveTable { AssetName = "DamageCurveTable" };
            var curve = new CurveData();
            float damage = baseDamage;
            for (int level = 0; level <= maxLevel; level++)
            {
                curve.AddKey(level, damage);
                damage *= growth;
            }
            table.AddCurve("DamageMultiplier", curve);
            return table;
        }

        /// <summary>创建经验曲线表（按等级→所需经验）</summary>
        public static CurveTable CreateExperienceCurveTable(int maxLevel = 100, float baseExp = 100f, float growth = 1.2f)
        {
            var table = new CurveTable { AssetName = "ExperienceCurveTable" };
            var curve = new CurveData();
            float exp = baseExp;
            for (int level = 0; level <= maxLevel; level++)
            {
                curve.AddKey(level, exp);
                exp *= growth;
            }
            table.AddCurve("ExpToLevel", curve);
            return table;
        }

        /// <summary>创建掉落概率曲线表（按难度→掉落概率 [0,1]）</summary>
        public static CurveTable CreateDropRateCurveTable(float maxDifficulty = 10f, float baseRate = 0.1f, float maxRate = 0.8f)
        {
            var table = new CurveTable { AssetName = "DropRateCurveTable" };
            var curve = new CurveData();
            curve.AddKey(0f, baseRate);
            curve.AddKey(maxDifficulty, maxRate);
            table.AddCurve("DropRate", curve);
            return table;
        }

        /// <summary>创建难度曲线表（按游戏时长→难度系数）</summary>
        public static CurveTable CreateDifficultyCurveTable(float maxTimeMinutes = 60f, float startDifficulty = 0.5f, float endDifficulty = 2.0f)
        {
            var table = new CurveTable { AssetName = "DifficultyCurveTable" };
            var curve = new CurveData();
            curve.AddKey(0f, startDifficulty);
            curve.AddKey(maxTimeMinutes * 0.5f, (startDifficulty + endDifficulty) * 0.5f);
            curve.AddKey(maxTimeMinutes, endDifficulty);
            table.AddCurve("DifficultyScale", curve);
            return table;
        }
    }
}
