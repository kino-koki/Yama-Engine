// ============================================================================
// ShanyuEngine AudioDemo.cs
// 山雨引擎 - 音频演示与测试音效生成
// ============================================================================

using System;

namespace ShanyuEngine.Audio
{
    /// <summary>
    /// 音频演示 — 生成测试音效。
    /// </summary>
    public static class AudioDemo
    {
        /// <summary>
        /// 生成正弦波音效（测试用）。
        /// </summary>
        public static AudioSampleClip CreateSineWave(float frequency, float duration, float sampleRate = 48000)
        {
            int sampleCount = (int)(duration * sampleRate);
            var samples = new float[sampleCount];

            for (int i = 0; i < sampleCount; i++)
            {
                samples[i] = MathF.Sin(2 * MathF.PI * frequency * i / sampleRate) * 0.5f;
            }

            return new AudioSampleClip
            {
                Name = $"Sine_{frequency}Hz",
                Samples = samples,
                SampleRate = (int)sampleRate,
                Channels = 1
            };
        }

        /// <summary>
        /// 生成白噪声。
        /// </summary>
        public static AudioSampleClip CreateWhiteNoise(float duration, float sampleRate = 48000)
        {
            int sampleCount = (int)(duration * sampleRate);
            var samples = new float[sampleCount];
            var random = new Random();

            for (int i = 0; i < sampleCount; i++)
            {
                samples[i] = (float)(random.NextDouble() * 2.0 - 1.0) * 0.3f;
            }

            return new AudioSampleClip
            {
                Name = "WhiteNoise",
                Samples = samples,
                SampleRate = (int)sampleRate,
                Channels = 1
            };
        }

        /// <summary>
        /// 生成爆炸音效（噪声+低频指数衰减）。
        /// </summary>
        public static AudioSampleClip CreateExplosionSound(float sampleRate = 48000)
        {
            float duration = 1.0f;
            int sampleCount = (int)(duration * sampleRate);
            var samples = new float[sampleCount];
            var random = new Random();

            for (int i = 0; i < sampleCount; i++)
            {
                float t = i / sampleRate;
                float envelope = MathF.Exp(-t * 4.0f); // 指数衰减
                samples[i] = (float)(random.NextDouble() * 2.0 - 1.0) * envelope * 0.8f;
            }

            return new AudioSampleClip
            {
                Name = "Explosion",
                Samples = samples,
                SampleRate = (int)sampleRate,
                Channels = 1
            };
        }

        /// <summary>
        /// 生成 440Hz 测试音（A4标准音高）。
        /// </summary>
        public static AudioSampleClip CreateTestTone(float duration = 1.0f, float sampleRate = 48000)
        {
            return CreateSineWave(440f, duration, sampleRate);
        }
    }
}
