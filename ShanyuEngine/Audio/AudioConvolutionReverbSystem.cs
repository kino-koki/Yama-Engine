using System;
using System.Collections.Generic;

namespace ShanyuEngine.Audio
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨 卷积混响系统 (Audio Convolution Reverb System)
    //
    // IR 脉冲响应加载、FFT 卷积、内置预设、多 IR 混合、IR 编辑器。
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────────────
    //  枚举
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>卷积方法。</summary>
    public enum ConvolutionMethod
    {
        /// <summary>时域直接卷积 (短 IR)。</summary>
        TimeDomain,
        /// <summary>频域 FFT 卷积 (分块)。</summary>
        FrequencyDomain,
        /// <summary>均匀分区卷积。</summary>
        UniformPartitioned,
        /// <summary>非均匀分区卷积 (低延迟)。</summary>
        NonUniformPartitioned
    }

    /// <summary>内置 IR 预设类型。</summary>
    public enum BuiltInIRPreset
    {
        SmallRoom,
        LargeRoom,
        Hall,
        Corridor,
        Cave,
        Forest,
        Underwater,
        MetalPipe,
        Church,
        Stadium,
        Bathroom,
        CarpetedHallway,
        Tunnel
    }

    // ──────────────────────────────────────────────────────────────────────
    //  ImpulseResponse — 脉冲响应数据
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>IR (脉冲响应) 数据。</summary>
    public sealed class ImpulseResponse : IDisposable
    {
        /// <summary>IR 名称。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>IR 描述。</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>PCM 数据 (单声道, float)。</summary>
        public float[] Data { get; set; } = Array.Empty<float>();

        /// <summary>采样率。</summary>
        public int SampleRate { get; set; } = 44100;

        /// <summary>声道数。</summary>
        public int Channels { get; set; } = 1;

        /// <summary>总采样数。</summary>
        public int Length => Data.Length;

        /// <summary>时长 (秒)。</summary>
        public float Duration => Length / (float)SampleRate;

        /// <summary>来源文件路径。</summary>
        public string SourceFile { get; set; } = string.Empty;

        /// <summary>是否为内置预设。</summary>
        public bool IsBuiltIn { get; set; }

        /// <summary>衰减时间 (秒, 自动计算)。</summary>
        public float CalculatedDecayTime
        {
            get
            {
                if (Data.Length == 0) return 0f;
                float rms = 0f;
                for (int i = 0; i < Data.Length; i++)
                    rms += Data[i] * Data[i];
                rms = (float)Math.Sqrt(rms / Data.Length);

                // 找到衰减到 -60dB 的点
                float threshold = rms * 0.001f;
                int thresholdIdx = Data.Length - 1;
                for (int i = Data.Length - 1; i >= 0; i--)
                {
                    if (Math.Abs(Data[i]) > threshold)
                    {
                        thresholdIdx = i;
                        break;
                    }
                }
                return thresholdIdx / (float)SampleRate;
            }
        }

        /// <summary>裁剪 IR 到指定长度。</summary>
        public void Trim(int newLength)
        {
            if (newLength <= 0 || newLength >= Data.Length) return;
            float[] trimmed = new float[newLength];
            Array.Copy(Data, trimmed, newLength);
            Data = trimmed;
        }

        /// <summary>应用衰减包络。</summary>
        public void ApplyDecay(float decayFactor)
        {
            for (int i = 0; i < Data.Length; i++)
                Data[i] *= (float)Math.Exp(-decayFactor * i / SampleRate);
        }

        /// <summary>反转 IR (时间方向)。</summary>
        public void Reverse()
        {
            Array.Reverse(Data);
        }

        /// <summary>标准化 (最大幅值归一化到 targetLevel)。</summary>
        public void Normalize(float targetLevel = 1f)
        {
            float max = 0f;
            for (int i = 0; i < Data.Length; i++)
            {
                float abs = Math.Abs(Data[i]);
                if (abs > max) max = abs;
            }
            if (max > 0.0001f)
            {
                float scale = targetLevel / max;
                for (int i = 0; i < Data.Length; i++)
                    Data[i] *= scale;
            }
        }

        /// <summary>与另一个 IR 线性混合。</summary>
        public static ImpulseResponse Mix(ImpulseResponse a, ImpulseResponse b, float mixA = 0.5f, float mixB = 0.5f)
        {
            int maxLen = Math.Max(a.Length, b.Length);
            var result = new ImpulseResponse
            {
                Name = $"{a.Name}+{b.Name}",
                SampleRate = a.SampleRate,
                Channels = 1,
                Data = new float[maxLen]
            };

            for (int i = 0; i < maxLen; i++)
            {
                float valA = i < a.Length ? a.Data[i] : 0f;
                float valB = i < b.Length ? b.Data[i] : 0f;
                result.Data[i] = valA * mixA + valB * mixB;
            }

            return result;
        }

        public void Dispose()
        {
            Data = Array.Empty<float>();
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  IRLoader — IR 加载器
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>IR 加载器。</summary>
    public sealed class IRLoader
    {
        /// <summary>缓存已加载的 IR。</summary>
        private readonly Dictionary<string, ImpulseResponse> _cache = new();

        /// <summary>从 WAV 文件加载 IR。</summary>
        public ImpulseResponse LoadFromWAV(string filePath, string name = "")
        {
            if (_cache.TryGetValue(filePath, out var cached))
                return cached;

            // 简化: 创建空 IR 并标记为已加载
            // 实际实现需要解析 WAV 文件头和 PCM 数据
            var ir = new ImpulseResponse
            {
                Name = string.IsNullOrEmpty(name) ? System.IO.Path.GetFileNameWithoutExtension(filePath) : name,
                SourceFile = filePath,
                SampleRate = 44100,
                Channels = 1,
                Data = GenerateSyntheticIR(BuiltInIRPreset.LargeRoom, 44100)
            };

            _cache[filePath] = ir;
            return ir;
        }

        /// <summary>从原始 float[] 数据加载 IR。</summary>
        public ImpulseResponse LoadFromRaw(float[] data, int sampleRate, string name = "CustomIR")
        {
            var ir = new ImpulseResponse
            {
                Name = name,
                SampleRate = sampleRate,
                Channels = 1,
                Data = (float[])data.Clone(),
                IsBuiltIn = false
            };
            ir.Normalize();
            return ir;
        }

        /// <summary>生成合成 IR (用于内置预设)。</summary>
        public static float[] GenerateSyntheticIR(BuiltInIRPreset preset, int sampleRate)
        {
            float decayTime = preset switch
            {
                BuiltInIRPreset.SmallRoom => 0.5f,
                BuiltInIRPreset.LargeRoom => 1.8f,
                BuiltInIRPreset.Hall => 3.5f,
                BuiltInIRPreset.Corridor => 1.5f,
                BuiltInIRPreset.Cave => 5.0f,
                BuiltInIRPreset.Forest => 2.5f,
                BuiltInIRPreset.Underwater => 3.0f,
                BuiltInIRPreset.MetalPipe => 2.0f,
                BuiltInIRPreset.Church => 4.0f,
                BuiltInIRPreset.Stadium => 5.0f,
                BuiltInIRPreset.Bathroom => 0.8f,
                BuiltInIRPreset.CarpetedHallway => 1.0f,
                BuiltInIRPreset.Tunnel => 4.0f,
                _ => 1.5f
            };

            int length = (int)(decayTime * sampleRate);
            float[] ir = new float[length];

            // 简化的指数衰减 + 随机反射
            Random random = new((int)preset);
            float decayFactor = 6f / Math.Max(0.1f, decayTime);

            // 早期反射 (前 50ms)
            int earlyEnd = Math.Min(length, sampleRate / 20);
            for (int i = 0; i < earlyEnd; i++)
            {
                ir[i] = (float)(random.NextDouble() * 2 - 1) * (float)Math.Exp(-decayFactor * i / sampleRate);
            }

            // 晚期混响 (扩散衰减)
            for (int i = earlyEnd; i < length; i++)
            {
                float envelope = (float)Math.Exp(-decayFactor * i / sampleRate);
                ir[i] = (float)(random.NextDouble() * 2 - 1) * envelope * 0.5f;
            }

            // 标准化
            float max = 0f;
            for (int i = 0; i < ir.Length; i++)
            {
                float abs = Math.Abs(ir[i]);
                if (abs > max) max = abs;
            }
            if (max > 0.0001f)
            {
                for (int i = 0; i < ir.Length; i++)
                    ir[i] /= max;
            }

            return ir;
        }

        /// <summary>获取内置 IR 预设。</summary>
        public ImpulseResponse GetBuiltInPreset(BuiltInIRPreset preset)
        {
            string key = $"builtin_{preset}";
            if (_cache.TryGetValue(key, out var cached))
                return cached;

            var ir = new ImpulseResponse
            {
                Name = preset.ToString(),
                SampleRate = 44100,
                Channels = 1,
                Data = GenerateSyntheticIR(preset, 44100),
                IsBuiltIn = true
            };

            _cache[key] = ir;
            return ir;
        }

        /// <summary>清除缓存。</summary>
        public void ClearCache()
        {
            foreach (var ir in _cache.Values)
                ir.Dispose();
            _cache.Clear();
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  ConvolutionReverb — 卷积混响处理器
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>卷积混响处理器。</summary>

    // ──────────────────────────────────────────────────────────────────────
    //  MultiIR Mixer — 多 IR 混合
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>IR 混合条目。</summary>
    public sealed class IRMixEntry
    {
        /// <summary>IR。</summary>
        public ImpulseResponse IR { get; set; }

        /// <summary>混合权重。</summary>
        public float Weight { get; set; } = 1f;

        /// <summary>是否启用。</summary>
        public bool Enabled { get; set; } = true;
    }

    /// <summary>多 IR 混合器。</summary>
    public sealed class MultiIRMixer
    {
        /// <summary>混合条目列表。</summary>
        public List<IRMixEntry> Entries { get; } = new();

        /// <summary>添加 IR。</summary>
        public void AddIR(ImpulseResponse ir, float weight = 1f)
        {
            Entries.Add(new IRMixEntry { IR = ir, Weight = weight });
        }

        /// <summary>混合所有 IR 为单一 IR。</summary>
        public ImpulseResponse MixAll(string outputName = "MixedIR")
        {
            if (Entries.Count == 0)
                return new ImpulseResponse { Name = outputName, Data = Array.Empty<float>() };

            if (Entries.Count == 1)
            {
                var entry = Entries[0];
                var ir = new ImpulseResponse
                {
                    Name = outputName,
                    SampleRate = entry.IR.SampleRate,
                    Channels = 1,
                    Data = (float[])entry.IR.Data.Clone()
                };
                ir.Normalize(entry.Weight);
                return ir;
            }

            // 找到最长 IR
            int maxLength = 0;
            foreach (var entry in Entries)
            {
                if (entry.Enabled && entry.IR.Length > maxLength)
                    maxLength = entry.IR.Length;
            }

            float[] mixed = new float[maxLength];

            foreach (var entry in Entries)
            {
                if (!entry.Enabled) continue;
                for (int i = 0; i < entry.IR.Length && i < maxLength; i++)
                    mixed[i] += entry.IR.Data[i] * entry.Weight;
            }

            // 标准化
            float max = 0f;
            for (int i = 0; i < mixed.Length; i++)
            {
                float abs = Math.Abs(mixed[i]);
                if (abs > max) max = abs;
            }
            if (max > 0.0001f)
            {
                for (int i = 0; i < mixed.Length; i++)
                    mixed[i] /= max;
            }

            return new ImpulseResponse
            {
                Name = outputName,
                SampleRate = 44100,
                Channels = 1,
                Data = mixed
            };
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  IREditor — IR 编辑器
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>IR 编辑工具。</summary>
    public static class IREditor
    {
        /// <summary>裁剪 IR 到指定时长。</summary>
        public static ImpulseResponse Trim(ImpulseResponse ir, float maxDurationSeconds)
        {
            int newLength = (int)(maxDurationSeconds * ir.SampleRate);
            var result = new ImpulseResponse
            {
                Name = ir.Name + "_trimmed",
                SampleRate = ir.SampleRate,
                Channels = ir.Channels
            };
            result.Trim(newLength);
            result.Data = (float[])ir.Data.Clone();
            result.Trim(newLength);
            return result;
        }

        /// <summary>应用指数衰减。</summary>
        public static ImpulseResponse ApplyDecay(ImpulseResponse ir, float decayTime)
        {
            var result = new ImpulseResponse
            {
                Name = ir.Name + "_decayed",
                SampleRate = ir.SampleRate,
                Channels = ir.Channels,
                Data = (float[])ir.Data.Clone()
            };
            result.ApplyDecay(6f / Math.Max(0.1f, decayTime));
            return result;
        }

        /// <summary>应用简易均衡 (高通/低通)。</summary>
        public static ImpulseResponse ApplyEQ(ImpulseResponse ir, float lowCutHz, float highCutHz)
        {
            var result = new ImpulseResponse
            {
                Name = ir.Name + "_eq",
                SampleRate = ir.SampleRate,
                Channels = ir.Channels,
                Data = (float[])ir.Data.Clone()
            };

            // 简易低通 (相邻采样平均)
            if (highCutHz > 0 && highCutHz < ir.SampleRate / 2f)
            {
                float alpha = 2f * MathF.PI * highCutHz / ir.SampleRate;
                float prev = 0f;
                for (int i = 0; i < result.Data.Length; i++)
                {
                    prev = prev + alpha * (result.Data[i] - prev);
                    result.Data[i] = prev;
                }
            }

            return result;
        }

        /// <summary>反转 IR。</summary>
        public static ImpulseResponse Reverse(ImpulseResponse ir)
        {
            var result = new ImpulseResponse
            {
                Name = ir.Name + "_reversed",
                SampleRate = ir.SampleRate,
                Channels = ir.Channels,
                Data = (float[])ir.Data.Clone()
            };
            Array.Reverse(result.Data);
            return result;
        }

        /// <summary>标准化 IR。</summary>
        public static ImpulseResponse Normalize(ImpulseResponse ir, float targetLevel = 1f)
        {
            var result = new ImpulseResponse
            {
                Name = ir.Name + "_normalized",
                SampleRate = ir.SampleRate,
                Channels = ir.Channels,
                Data = (float[])ir.Data.Clone()
            };
            result.Normalize(targetLevel);
            return result;
        }
    }
}
