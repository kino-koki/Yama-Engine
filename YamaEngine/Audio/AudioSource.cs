// ============================================================================
// YamaEngine AudioSource.cs
// yama引擎 - 活跃音频源与音频片段
// ============================================================================

using System;
using System.Numerics;

namespace YamaEngine.Audio
{
    /// <summary>
    /// 活跃音频源 — 表示一个正在播放的声音实例。
    /// </summary>
    public class AudioSource
    {
        public AudioSampleClip? Clip;
        public Vector3 Position;
        public float Volume = 1.0f;
        public float Pitch = 1.0f;
        public bool Is3D = true;
        public bool Loop = false;

        // 播放位置（样本索引）
        public int PlayTime;

        /// <summary>
        /// 读取样本到输出缓冲区。
        /// </summary>
        /// <returns>true = 播放结束。</returns>
        public bool ReadSamples(float[] outputBuffer, int frames, int channels)
        {
            if (Clip == null || Clip.Samples == null) return true;

            int samplesToRead = frames * channels;
            int remaining = Clip.Samples.Length - PlayTime;
            if (remaining <= 0)
            {
                if (Loop)
                {
                    PlayTime = 0;
                    remaining = Clip.Samples.Length;
                }
                else
                {
                    return true; // 播放结束
                }
            }

            int readCount = Math.Min(samplesToRead, remaining);

            // 单声道素材 -> 立体声输出 的自动上混
            if (Clip.Channels == 1 && channels == 2)
            {
                int monoFrames = Math.Min(frames, remaining);
                for (int f = 0; f < monoFrames; f++)
                {
                    float sample = Clip.Samples[PlayTime + f] * Volume;
                    outputBuffer[f * 2] += sample;
                    outputBuffer[f * 2 + 1] += sample;
                }
                PlayTime += monoFrames;
            }
            else if (Clip.Channels == channels)
            {
                // 声道数匹配，直接拷贝
                for (int i = 0; i < readCount; i++)
                {
                    outputBuffer[i] += Clip.Samples[PlayTime + i] * Volume;
                }
                PlayTime += readCount;
            }
            else
            {
                // 其他情况：只填充前通道
                for (int i = 0; i < readCount; i++)
                {
                    outputBuffer[i] += Clip.Samples[PlayTime + i] * Volume;
                }
                PlayTime += readCount;
            }

            return false; // 未结束
        }
    }

    /// <summary>
    /// 音频片段（基于浮点采样）。
    /// </summary>
    public class AudioSampleClip
    {
        public string Name = "";
        public float[] Samples = Array.Empty<float>();
        public int SampleRate = 48000;
        public int Channels = 1;
        public float Duration => SampleRate > 0 ? (float)Samples.Length / (SampleRate * Channels) : 0f;
    }
}
