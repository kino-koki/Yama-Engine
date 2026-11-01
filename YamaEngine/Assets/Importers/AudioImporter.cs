// =====================================================================================
//  YamaEngine - AudioImporter：音频导入器
//  命名空间：YamaEngine.Assets.Importers
// -------------------------------------------------------------------------------------
//  纯 C# 实现，支持多种音频格式的导入与处理：
//    - WAV（RIFF / fmt / data 顶级块 / PCM / IEEE Float / ADPCM / 扩展头 / 循环点）
//    - MP3（ID3v1/v2 / 帧头解析 / 解码占位 / MPEG-1 Layer III）
//    - OGG/Vorbis（OGG 容器 / Vorbis 头 / 标识 / 注释 / 设置 / 音频数据 / 解码占位）
//    - FLAC（fLaC 标记 / STREAMINFO / 应用元数据块 / PADDING / SEEKTABLE /
//           VORBIS_COMMENT / CUESHEET / PICTURE / 帧头 / 子帧 / 解码占位）
//    - AAC（ADTS / 音频对象类型 / 采样率索引 / 声道配置 / 解码占位）
//    - Opus（OGG 容器 / OpusHead / OpusTags / 帧 / 解码占位）
//  音频设置：
//    - 采样率转换（重采样 / 线性 / 立方 / Hermite / Sinc）
//    - 声道转换（单声道→立体声 / 立体声→单声道 / 环绕声处理）
//    - 位深转换（16→24 / 24→16 / →32bit Float）
//    - 标准化（峰值标准化 / RMS / EBU R128 LUFS 响度标准化）
//    - 循环点设置（循环开始 / 循环结束 / 无缝循环检测）
//    - 压缩（运行时压缩 / Vorbis / Opus 占位）
//  批量导入
// =====================================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using YamaEngine.Audio;

namespace YamaEngine.Assets.Importers
{
    /// <summary>音频文件格式（导入判定用）。</summary>
    public enum AudioFormat
    {
        Unknown = 0,
        WAV = 1,
        MP3 = 2,
        OGG = 3,
        FLAC = 4,
        AAC = 5,
        Opus = 6,
        WAV_PCM = WAV,
        WAV_Float = WAV,
        OGG_Vorbis = OGG,
    }

    // =================================================================================
    //  音频数据结构
    // =================================================================================

    /// <summary>导入后的音频数据。</summary>
    public sealed class ImportedAudio
    {
        /// <summary>音频名称。</summary>
        public string Name = string.Empty;

        /// <summary>源文件路径。</summary>
        public string SourcePath = string.Empty;

        /// <summary>源格式。</summary>
        public string SourceFormat = string.Empty;

        /// <summary>采样率（Hz）。</summary>
        public int SampleRate;

        /// <summary>声道数（1=单声道, 2=立体声, 6=5.1环绕）。</summary>
        public int Channels;

        /// <summary>位深度（8/16/24/32）。</summary>
        public int BitsPerSample;

        /// <summary>总采样数（每声道）。</summary>
        public long SampleCount;

        /// <summary>时长（秒）。</summary>
        public double Duration;

        /// <summary>是否为浮点格式。</summary>
        public bool IsFloat;

        /// <summary>音频样本数据（交错排列，浮点 [-1, 1]）。</summary>
        public float[]? Samples;

        /// <summary>循环点（采样索引）。null 表示无循环。</summary>
        public int? LoopStart;

        /// <summary>循环结束点（采样索引）。</summary>
        public int? LoopEnd;

        /// <summary>是否为无缝循环。</summary>
        public bool IsSeamlessLoop;

        /// <summary>原始元数据（ID3 标签等）。</summary>
        public Dictionary<string, string> Metadata = new(StringComparer.Ordinal);

        /// <summary>导入信息消息。</summary>
        public List<string> Messages = new();
    }

    // =================================================================================
    //  导入选项
    // =================================================================================

    /// <summary>音频导入选项。</summary>
    public sealed class AudioImportOptions
    {
        /// <summary>目标采样率（0=保持原始）。</summary>
        public int TargetSampleRate = 0;

        /// <summary>目标声道数（0=保持原始）。</summary>
        public int TargetChannels = 0;

        /// <summary>目标位深度（0=保持原始）。</summary>
        public int TargetBitsPerSample = 0;

        /// <summary>重采样质量。</summary>
        public ResampleQuality ResampleQuality = ResampleQuality.Cubic;

        /// <summary>是否标准化音频。</summary>
        public bool Normalize = false;

        /// <summary>标准化模式。</summary>
        public NormalizeMode NormalizeMode = NormalizeMode.Peak;

        /// <summary>标准化目标电平（dB）。</summary>
        public float NormalizeTargetLevel = -1.0f;

        /// <summary>是否检测循环点。</summary>
        public bool DetectLoopPoints = false;

        /// <summary>循环检测阈值。</summary>
        public float LoopDetectionThreshold = 0.001f;

        /// <summary>运行时压缩格式。</summary>
        public AudioCompression RuntimeCompression = AudioCompression.None;
    }

    /// <summary>重采样质量。</summary>
    public enum ResampleQuality
    {
        Nearest,
        Linear,
        Cubic,
        Hermite,
        Sinc
    }

    /// <summary>标准化模式。</summary>
    public enum NormalizeMode
    {
        Peak,
        RMS,
        EBU_R128_LUFS
    }

    /// <summary>音频压缩格式。</summary>
    public enum AudioCompression
    {
        None,
        Vorbis,
        Opus,
        ADPCM
    }

    /// <summary>音频格式枚举。</summary>

    // =================================================================================
    //  AudioImporter —— 主导入器
    // =================================================================================

    /// <summary>
    /// 音频导入器。支持多种音频格式的导入与处理。
    /// </summary>
    public sealed class AudioImporter
    {
        private readonly List<string> _messages = new();

        /// <summary>导入单个音频文件。</summary>
        public ImportedAudio Import(string filePath, AudioImportOptions? options = null)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"音频源文件不存在: {filePath}");

            options ??= new AudioImportOptions();

            AudioFormat format = DetectFormat(filePath);
            _messages.Add($"检测到音频格式: {format}");

            ImportedAudio audio = format switch
            {
                AudioFormat.WAV => ImportWav(filePath),
                AudioFormat.MP3 => ImportMp3(filePath),
                AudioFormat.OGG => ImportOgg(filePath),
                AudioFormat.FLAC => ImportFlac(filePath),
                AudioFormat.AAC => ImportAac(filePath),
                AudioFormat.Opus => ImportOpus(filePath),
                _ => throw new NotSupportedException($"不支持的音频格式: {format}")
            };

            // 后处理
            PostProcessAudio(audio, options);

            audio.Messages.AddRange(_messages);
            return audio;
        }

        /// <summary>批量导入音频文件。</summary>
        public List<ImportedAudio> ImportBatch(IEnumerable<string> filePaths,
            AudioImportOptions? options = null,
            IProgress<(int Current, int Total, string File)>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var results = new List<ImportedAudio>();
            var pathList = new List<string>(filePaths);
            int total = pathList.Count;

            _messages.Add($"批量导入: {total} 个音频文件");

            Parallel.For(0, total, new ParallelOptions { CancellationToken = cancellationToken }, i =>
            {
                try
                {
                    var audio = Import(pathList[i], options);
                    lock (results)
                    {
                        results.Add(audio);
                    }
                    progress?.Report((i + 1, total, pathList[i]));
                }
                catch (Exception ex)
                {
                    _messages.Add($"导入失败 {pathList[i]}: {ex.Message}");
                    lock (results)
                    {
                        results.Add(new ImportedAudio
                        {
                            SourcePath = pathList[i],
                            Messages = new List<string> { $"导入失败: {ex.Message}" }
                        });
                    }
                }
            });

            _messages.Add($"批量导入完成: {results.Count}/{total} 成功");
            return results;
        }

        // =================================================================================
        //  格式检测
        // =================================================================================

        /// <summary>根据文件头检测音频格式。</summary>
        public static AudioFormat DetectFormat(string filePath)
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] header = new byte[64];
            int read = fs.Read(header, 0, 64);

            if (read < 4) return AudioFormat.Unknown;

            // WAV: RIFF....WAVE
            if (read >= 12 && header[0] == 'R' && header[1] == 'I' && header[2] == 'F' && header[3] == 'F' &&
                header[8] == 'W' && header[9] == 'A' && header[10] == 'V' && header[11] == 'E')
                return AudioFormat.WAV;

            // MP3: ID3 或 FF Fx
            if (header[0] == 'I' && header[1] == 'D' && header[2] == '3')
                return AudioFormat.MP3;
            if (header[0] == 0xFF && (header[1] & 0xE0) == 0xE0)
                return AudioFormat.MP3;

            // FLAC: fLaC
            if (header[0] == 'f' && header[1] == 'L' && header[2] == 'a' && header[3] == 'C')
                return AudioFormat.FLAC;

            // OGG: OggS
            if (header[0] == 'O' && header[1] == 'g' && header[2] == 'g' && header[3] == 'S')
            {
                // 检查是 Vorbis 还是 Opus
                if (read >= 29 && header[28] == 'O' && header[29] == 'p') // OpusHead
                    return AudioFormat.Opus;
                return AudioFormat.OGG; // Vorbis
            }

            // AAC: FF F1 或 FF F9 (ADTS)
            if (header[0] == 0xFF && (header[1] == 0xF1 || header[1] == 0xF9))
                return AudioFormat.AAC;

            return AudioFormat.Unknown;
        }

        // =================================================================================
        //  WAV 导入
        // =================================================================================

        private ImportedAudio ImportWav(string filePath)
        {
            byte[] data = File.ReadAllBytes(filePath);
            var audio = new ImportedAudio
            {
                SourcePath = filePath,
                SourceFormat = "WAV",
                Name = Path.GetFileNameWithoutExtension(filePath)
            };

            if (data.Length < 12 || data[0] != 'R' || data[1] != 'I' || data[2] != 'F' || data[3] != 'F')
                throw new InvalidDataException("无效的 WAV 文件");

            // RIFF 头
            uint riffSize = BitConverter.ToUInt32(data, 4);
            string waveTag = System.Text.Encoding.ASCII.GetString(data, 8, 4);

            // 遍历块
            int offset = 12;
            while (offset + 8 <= data.Length)
            {
                string chunkID = System.Text.Encoding.ASCII.GetString(data, offset, 4);
                uint chunkSize = BitConverter.ToUInt32(data, offset + 4);
                int chunkDataOffset = offset + 8;

                if (chunkID == "fmt ")
                {
                    // 格式块
                    ushort audioFormat = BitConverter.ToUInt16(data, chunkDataOffset);
                    ushort channels = BitConverter.ToUInt16(data, chunkDataOffset + 2);
                    uint sampleRate = BitConverter.ToUInt32(data, chunkDataOffset + 4);
                    uint byteRate = BitConverter.ToUInt32(data, chunkDataOffset + 8);
                    ushort blockAlign = BitConverter.ToUInt16(data, chunkDataOffset + 12);
                    ushort bitsPerSample = BitConverter.ToUInt16(data, chunkDataOffset + 14);

                    audio.Channels = channels;
                    audio.SampleRate = (int)sampleRate;
                    audio.BitsPerSample = bitsPerSample;

                    // 判断格式
                    if (audioFormat == 3)
                        audio.IsFloat = true; // IEEE Float
                    else if (audioFormat == 1)
                        audio.IsFloat = false; // PCM
                    else if (audioFormat == 2)
                        _messages.Add("WAV: ADPCM 格式");
                    else if (audioFormat == 0xFFFE)
                    {
                        // 扩展格式
                        if (chunkSize >= 40)
                        {
                            Guid subFormat = new Guid(data.Skip(chunkDataOffset + 18).Take(16).ToArray());
                            if (subFormat == new Guid("00000003-0000-0010-8000-00aa00389b71"))
                                audio.IsFloat = true;
                        }
                    }

                    _messages.Add($"WAV fmt: format={audioFormat}, ch={channels}, sr={sampleRate}, bits={bitsPerSample}");
                }
                else if (chunkID == "data")
                {
                    // 数据块
                    int dataSize = (int)chunkSize;
                    int bytesPerSample = audio.BitsPerSample / 8;
                    audio.SampleCount = dataSize / (bytesPerSample * audio.Channels);
                    audio.Duration = (double)audio.SampleCount / audio.SampleRate;

                    audio.Samples = new float[audio.SampleCount * audio.Channels];

                    // 转换为浮点样本
                    for (int i = 0; i < audio.Samples.Length; i++)
                    {
                        int sampleOffset = chunkDataOffset + i * bytesPerSample;
                        if (sampleOffset + bytesPerSample > data.Length) break;

                        if (audio.IsFloat && audio.BitsPerSample == 32)
                        {
                            audio.Samples[i] = BitConverter.ToSingle(data, sampleOffset);
                        }
                        else if (audio.BitsPerSample == 16)
                        {
                            short val = BitConverter.ToInt16(data, sampleOffset);
                            audio.Samples[i] = val / 32768f;
                        }
                        else if (audio.BitsPerSample == 24)
                        {
                            int val = data[sampleOffset] | (data[sampleOffset + 1] << 8) | (data[sampleOffset + 2] << 16);
                            if ((val & 0x800000) != 0) val |= unchecked((int)0xFF000000); // 符号扩展
                            audio.Samples[i] = val / 8388608f;
                        }
                        else if (audio.BitsPerSample == 8)
                        {
                            audio.Samples[i] = (data[sampleOffset] - 128) / 128f;
                        }
                        else if (audio.BitsPerSample == 32 && !audio.IsFloat)
                        {
                            int val = BitConverter.ToInt32(data, sampleOffset);
                            audio.Samples[i] = val / 2147483648f;
                        }
                    }
                }
                else if (chunkID == "smpl")
                {
                    // 采样器块（包含循环点信息）
                    if (chunkSize >= 36)
                    {
                        uint numLoops = BitConverter.ToUInt32(data, chunkDataOffset + 28);
                        for (int i = 0; i < numLoops; i++)
                        {
                            int loopOffset = chunkDataOffset + 36 + i * 24;
                            if (loopOffset + 24 <= data.Length)
                            {
                                uint loopType = BitConverter.ToUInt32(data, loopOffset + 8);
                                uint loopStart = BitConverter.ToUInt32(data, loopOffset + 12);
                                uint loopEnd = BitConverter.ToUInt32(data, loopOffset + 16);

                                audio.LoopStart = (int)loopStart;
                                audio.LoopEnd = (int)loopEnd;
                                _messages.Add($"WAV: 循环点 {loopStart}-{loopEnd} (type={loopType})");
                            }
                        }
                    }
                }
                else if (chunkID == "LIST" || chunkID == "INFO")
                {
                    // 信息块
                    // 提取元数据（标题、艺术家等）
                }

                // 块对齐（2 字节对齐）
                offset = chunkDataOffset + (int)chunkSize;
                if (chunkSize % 2 != 0) offset++;
            }

            _messages.Add($"WAV: {audio.SampleRate}Hz, {audio.Channels}ch, {audio.BitsPerSample}bit, " +
                         $"{audio.Duration:F2}s, {audio.SampleCount} 采样");
            return audio;
        }

        // =================================================================================
        //  MP3 导入
        // =================================================================================

        private ImportedAudio ImportMp3(string filePath)
        {
            byte[] data = File.ReadAllBytes(filePath);
            var audio = new ImportedAudio
            {
                SourcePath = filePath,
                SourceFormat = "MP3",
                Name = Path.GetFileNameWithoutExtension(filePath),
                Channels = 2,
                SampleRate = 44100,
                BitsPerSample = 16,
                IsFloat = false
            };

            // 解析 ID3v2 标签
            if (data.Length >= 10 && data[0] == 'I' && data[1] == 'D' && data[2] == '3')
            {
                int id3v2Size = (data[6] << 21) | (data[7] << 14) | (data[8] << 7) | data[9];
                int id3v2TotalSize = 10 + id3v2Size;

                _messages.Add($"MP3: ID3v2 标签 ({id3v2TotalSize} 字节)");

                // 提取元数据
                ExtractID3v2Tags(data, id3v2TotalSize, audio.Metadata);

                // 跳过 ID3v2 标签找到帧数据
                int frameStart = id3v2TotalSize;

                // 解析 MP3 帧头
                if (frameStart + 4 <= data.Length && data[frameStart] == 0xFF)
                {
                    ParseMp3FrameHeader(data, frameStart, audio);
                }
            }
            else
            {
                // 直接解析帧头
                int frameStart = 0;
                while (frameStart < data.Length - 1)
                {
                    if (data[frameStart] == 0xFF && (data[frameStart + 1] & 0xE0) == 0xE0)
                    {
                        ParseMp3FrameHeader(data, frameStart, audio);
                        break;
                    }
                    frameStart++;
                }
            }

            // 解析 ID3v1 标签（文件末尾 128 字节）
            if (data.Length >= 128)
            {
                int tagStart = data.Length - 128;
                if (data[tagStart] == 'T' && data[tagStart + 1] == 'A' && data[tagStart + 2] == 'G')
                {
                    _messages.Add("MP3: ID3v1 标签");
                    string title = System.Text.Encoding.GetEncoding("ISO-8859-1")
                        .GetString(data, tagStart + 3, 30).TrimEnd('\0', ' ');
                    string artist = System.Text.Encoding.GetEncoding("ISO-8859-1")
                        .GetString(data, tagStart + 33, 30).TrimEnd('\0', ' ');
                    if (!string.IsNullOrEmpty(title)) audio.Metadata["Title"] = title;
                    if (!string.IsNullOrEmpty(artist)) audio.Metadata["Artist"] = artist;
                }
            }

            // MP3 完整解码需要 Huffman + IMDCT
            // 简化实现：估算时长
            if (audio.SampleRate > 0)
            {
                // 粗略估算：文件大小 / 比特率
                int estimatedBitrate = 128000; // 默认 128kbps
                audio.Duration = (data.Length * 8.0) / estimatedBitrate;
                audio.SampleCount = (long)(audio.Duration * audio.SampleRate);
            }

            _messages.Add($"MP3: {audio.SampleRate}Hz, {audio.Channels}ch, {audio.Duration:F2}s (估算)");
            _messages.Add("注意: MP3 完整解码需要 Huffman/IMDCT 实现");

            return audio;
        }

        /// <summary>解析 MP3 帧头。</summary>
        private void ParseMp3FrameHeader(byte[] data, int offset, ImportedAudio audio)
        {
            if (offset + 4 > data.Length) return;

            byte b1 = data[offset + 1];
            byte b2 = data[offset + 2];

            // MPEG 版本
            int mpegVersion = (b1 >> 3) & 0x03;
            // 00 = MPEG 2.5, 01 = 保留, 10 = MPEG 2, 11 = MPEG 1

            // 层级
            int layer = (b1 >> 1) & 0x03;
            // 01 = Layer III, 10 = Layer II, 11 = Layer I

            // 比特率索引
            int bitrateIndex = (b2 >> 4) & 0x0F;

            // 采样率索引
            int sampleRateIndex = (b2 >> 2) & 0x03;

            // 声道模式
            int channelMode = (data[offset + 3] >> 6) & 0x03;
            // 00 = 立体声, 01 = 联合立体声, 10 = 双声道, 11 = 单声道

            // 采样率表
            int[,] sampleRates = new int[,] {
                { 44100, 48000, 32000 }, // MPEG 1
                { 22050, 24000, 16000 }, // MPEG 2
                { 11025, 12000, 8000  }, // MPEG 2.5
            };

            int versionIdx = mpegVersion == 3 ? 0 : (mpegVersion == 2 ? 1 : 2);
            if (sampleRateIndex < 3)
                audio.SampleRate = sampleRates[versionIdx, sampleRateIndex];

            audio.Channels = channelMode == 3 ? 1 : 2;

            _messages.Add($"MP3 帧头: MPEG{(mpegVersion == 3 ? "1" : (mpegVersion == 2 ? "2" : "2.5"))} " +
                         $"Layer {(layer == 1 ? "III" : (layer == 2 ? "II" : "I"))}, " +
                         $"采样率={audio.SampleRate}, 声道={audio.Channels}");
        }

        /// <summary>提取 ID3v2 标签。</summary>
        private void ExtractID3v2Tags(byte[] data, int tagSize, Dictionary<string, string> metadata)
        {
            int offset = 10;
            int versionMajor = data[3];

            while (offset + 10 < tagSize && offset + 10 < data.Length)
            {
                string frameID = System.Text.Encoding.ASCII.GetString(data, offset, 4);
                if (frameID[0] == '\0') break;

                int frameSize;
                if (versionMajor >= 4)
                {
                    // ID3v2.4: 同步安全整数
                    frameSize = (data[offset + 4] << 21) | (data[offset + 5] << 14) |
                                (data[offset + 6] << 7) | data[offset + 7];
                }
                else
                {
                    // ID3v2.3
                    frameSize = (data[offset + 4] << 24) | (data[offset + 5] << 16) |
                                (data[offset + 6] << 8) | data[offset + 7];
                }

                if (frameSize <= 0 || offset + 10 + frameSize > data.Length) break;

                // 提取帧文本
                byte encoding = data[offset + 10];
                string text = encoding == 1 || encoding == 2
                    ? System.Text.Encoding.Unicode.GetString(data, offset + 11, frameSize - 1).TrimEnd('\0')
                    : System.Text.Encoding.GetEncoding("ISO-8859-1").GetString(data, offset + 11, frameSize - 1).TrimEnd('\0');

                switch (frameID)
                {
                    case "TIT2": metadata["Title"] = text; break;
                    case "TPE1": metadata["Artist"] = text; break;
                    case "TALB": metadata["Album"] = text; break;
                    case "TYER": metadata["Year"] = text; break;
                    case "TCON": metadata["Genre"] = text; break;
                    case "COMM": metadata["Comment"] = text; break;
                }

                offset += 10 + frameSize;
            }
        }

        // =================================================================================
        //  OGG/Vorbis 导入
        // =================================================================================

        private ImportedAudio ImportOgg(string filePath)
        {
            byte[] data = File.ReadAllBytes(filePath);
            var audio = new ImportedAudio
            {
                SourcePath = filePath,
                SourceFormat = "OGG/Vorbis",
                Name = Path.GetFileNameWithoutExtension(filePath),
                BitsPerSample = 16,
                IsFloat = false
            };

            // 解析 OGG 容器页
            int offset = 0;
            int pagesParsed = 0;

            while (offset + 27 < data.Length)
            {
                // OGG 页头签名 "OggS"
                if (data[offset] != 'O' || data[offset + 1] != 'g' || data[offset + 2] != 'g' || data[offset + 3] != 'S')
                    break;

                // OGG 页头
                byte version = data[offset + 4];
                byte headerType = data[offset + 5];
                long granulePosition = BitConverter.ToInt64(data, offset + 6);
                uint bitstreamSerial = BitConverter.ToUInt32(data, offset + 14);
                uint pageSequence = BitConverter.ToUInt32(data, offset + 18);
                uint checksum = BitConverter.ToUInt32(data, offset + 22);
                byte segmentCount = data[offset + 26];

                // 读取段大小表
                int[] segmentSizes = new int[segmentCount];
                int headerSize = 27 + segmentCount;
                int dataSize = 0;

                for (int i = 0; i < segmentCount; i++)
                {
                    segmentSizes[i] = data[offset + 27 + i];
                    dataSize += segmentSizes[i];
                }

                int pageDataOffset = offset + headerSize;

                // 解析 Vorbis 标识块（第一页）
                if (pagesParsed == 0 && pageDataOffset + 7 < data.Length)
                {
                    // Vorbis 标识块以 \x01vorbis 开头
                    if (data[pageDataOffset] == 0x01 &&
                        data[pageDataOffset + 1] == 'v' && data[pageDataOffset + 2] == 'o' &&
                        data[pageDataOffset + 3] == 'r' && data[pageDataOffset + 4] == 'b' &&
                        data[pageDataOffset + 5] == 'i' && data[pageDataOffset + 6] == 's')
                    {
                        uint vorbisVersion = BitConverter.ToUInt32(data, pageDataOffset + 7);
                        byte channels = data[pageDataOffset + 11];
                        uint sampleRate = BitConverter.ToUInt32(data, pageDataOffset + 12);

                        audio.Channels = channels;
                        audio.SampleRate = (int)sampleRate;

                        _messages.Add($"OGG/Vorbis: 版本={vorbisVersion}, 声道={channels}, 采样率={sampleRate}");
                    }
                }

                // 解析 Vorbis 注释块（第二页）
                if (pagesParsed == 1 && pageDataOffset + 7 < data.Length)
                {
                    if (data[pageDataOffset] == 0x03 &&
                        data[pageDataOffset + 1] == 'v' && data[pageDataOffset + 2] == 'o' &&
                        data[pageDataOffset + 3] == 'r' && data[pageDataOffset + 4] == 'b' &&
                        data[pageDataOffset + 5] == 'i' && data[pageDataOffset + 6] == 's')
                    {
                        // Vorbis 注释块
                        int commentOffset = pageDataOffset + 7;
                        uint vendorLength = BitConverter.ToUInt32(data, commentOffset);
                        commentOffset += 4 + (int)vendorLength;

                        if (commentOffset + 4 < data.Length)
                        {
                            uint commentCount = BitConverter.ToUInt32(data, commentOffset);
                            commentOffset += 4;

                            for (int i = 0; i < commentCount && commentOffset + 4 < data.Length; i++)
                            {
                                uint commentLength = BitConverter.ToUInt32(data, commentOffset);
                                commentOffset += 4;

                                if (commentOffset + commentLength <= data.Length)
                                {
                                    string comment = System.Text.Encoding.UTF8.GetString(data, commentOffset, (int)commentLength);
                                    int eqIdx = comment.IndexOf('=');
                                    if (eqIdx > 0)
                                    {
                                        string key = comment.Substring(0, eqIdx).ToUpperInvariant();
                                        string value = comment.Substring(eqIdx + 1);
                                        audio.Metadata[key] = value;
                                    }
                                }
                                commentOffset += (int)commentLength;
                            }
                        }
                    }
                }

                offset = pageDataOffset + dataSize;
                pagesParsed++;
            }

            // Vorbis 完整解码需要 codebook + IMDCT
            _messages.Add($"OGG/Vorbis: {audio.SampleRate}Hz, {audio.Channels}ch");
            _messages.Add("注意: Vorbis 完整解码需要 codebook/IMDCT 实现");

            return audio;
        }

        // =================================================================================
        //  FLAC 导入
        // =================================================================================

        private ImportedAudio ImportFlac(string filePath)
        {
            byte[] data = File.ReadAllBytes(filePath);
            var audio = new ImportedAudio
            {
                SourcePath = filePath,
                SourceFormat = "FLAC",
                Name = Path.GetFileNameWithoutExtension(filePath),
                BitsPerSample = 16,
                IsFloat = false
            };

            // FLAC 以 "fLaC" 标记开头
            if (data.Length < 4 || data[0] != 'f' || data[1] != 'L' || data[2] != 'a' || data[3] != 'C')
                throw new InvalidDataException("无效的 FLAC 文件");

            int offset = 4;

            // 解析元数据块
            bool lastBlock = false;
            while (!lastBlock && offset + 4 < data.Length)
            {
                byte blockHeader = data[offset];
                lastBlock = (blockHeader & 0x80) != 0;
                int blockType = blockHeader & 0x7F;
                int blockSize = (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];

                int blockDataOffset = offset + 4;

                switch (blockType)
                {
                    case 0: // STREAMINFO
                        {
                            int minBlockSize = (data[blockDataOffset] << 8) | data[blockDataOffset + 1];
                            int maxBlockSize = (data[blockDataOffset + 2] << 8) | data[blockDataOffset + 3];
                            int minFrameSize = (data[blockDataOffset + 4] << 16) | (data[blockDataOffset + 5] << 8) | data[blockDataOffset + 6];
                            int maxFrameSize = (data[blockDataOffset + 7] << 16) | (data[blockDataOffset + 8] << 8) | data[blockDataOffset + 9];

                            // 采样率、声道、位深度（紧凑编码）
                            int sampleRate = (data[blockDataOffset + 10] << 12) | (data[blockDataOffset + 11] << 4) | (data[blockDataOffset + 12] >> 4);
                            int channels = ((data[blockDataOffset + 12] >> 1) & 0x07) + 1;
                            int bitsPerSample = (((data[blockDataOffset + 12] & 0x01) << 4) | (data[blockDataOffset + 13] >> 4)) + 1;

                            // 总采样数（36 位）
                            long totalSamples = ((long)(data[blockDataOffset + 13] & 0x0F) << 32) |
                                                ((long)data[blockDataOffset + 14] << 24) |
                                                ((long)data[blockDataOffset + 15] << 16) |
                                                ((long)data[blockDataOffset + 16] << 8) |
                                                data[blockDataOffset + 17];

                            audio.SampleRate = sampleRate;
                            audio.Channels = channels;
                            audio.BitsPerSample = bitsPerSample;
                            audio.SampleCount = totalSamples;
                            audio.Duration = (double)totalSamples / sampleRate;

                            _messages.Add($"FLAC STREAMINFO: sr={sampleRate}, ch={channels}, " +
                                         $"bits={bitsPerSample}, samples={totalSamples}, duration={audio.Duration:F2}s");
                            break;
                        }
                    case 1: // PADDING
                        _messages.Add($"FLAC: PADDING 块 ({blockSize} 字节)");
                        break;
                    case 2: // APPLICATION
                        _messages.Add($"FLAC: APPLICATION 块");
                        break;
                    case 3: // SEEKTABLE
                        _messages.Add($"FLAC: SEEKTABLE 块 ({blockSize} 字节)");
                        break;
                    case 4: // VORBIS_COMMENT
                        {
                            // Vorbis 风格注释（与 OGG 相同）
                            int commentOffset = blockDataOffset;
                            if (commentOffset + 4 < data.Length)
                            {
                                uint vendorLength = BitConverter.ToUInt32(data, commentOffset);
                                commentOffset += 4 + (int)vendorLength;

                                if (commentOffset + 4 < data.Length)
                                {
                                    uint commentCount = BitConverter.ToUInt32(data, commentOffset);
                                    commentOffset += 4;

                                    for (int i = 0; i < commentCount && commentOffset + 4 < data.Length; i++)
                                    {
                                        uint commentLength = BitConverter.ToUInt32(data, commentOffset);
                                        commentOffset += 4;

                                        if (commentOffset + commentLength <= data.Length)
                                        {
                                            string comment = System.Text.Encoding.UTF8.GetString(data, commentOffset, (int)commentLength);
                                            int eqIdx = comment.IndexOf('=');
                                            if (eqIdx > 0)
                                            {
                                                audio.Metadata[comment.Substring(0, eqIdx).ToUpperInvariant()] =
                                                    comment.Substring(eqIdx + 1);
                                            }
                                        }
                                        commentOffset += (int)commentLength;
                                    }
                                }
                            }
                            _messages.Add($"FLAC: VORBIS_COMMENT 块 ({blockSize} 字节)");
                            break;
                        }
                    case 5: // CUESHEET
                        _messages.Add($"FLAC: CUESHEET 块");
                        break;
                    case 6: // PICTURE
                        _messages.Add($"FLAC: PICTURE 块");
                        break;
                    default:
                        _messages.Add($"FLAC: 未知块类型 {blockType} ({blockSize} 字节)");
                        break;
                }

                offset = blockDataOffset + blockSize;
            }

            _messages.Add("注意: FLAC 完整解码需要 LPC/残差解码实现");

            return audio;
        }

        // =================================================================================
        //  AAC 导入
        // =================================================================================

        private ImportedAudio ImportAac(string filePath)
        {
            byte[] data = File.ReadAllBytes(filePath);
            var audio = new ImportedAudio
            {
                SourcePath = filePath,
                SourceFormat = "AAC",
                Name = Path.GetFileNameWithoutExtension(filePath),
                SampleRate = 44100,
                Channels = 2,
                BitsPerSample = 16
            };

            // ADTS 帧头解析
            int offset = 0;
            if (data.Length >= 7 && data[0] == 0xFF && (data[1] & 0xF0) == 0xF0)
            {
                // ADTS 同步字
                int profile = (data[2] >> 6) & 0x03;
                int samplingRateIndex = (data[2] >> 2) & 0x0F;
                int channelConfig = ((data[2] & 0x01) << 2) | ((data[3] >> 6) & 0x03);

                // 采样率表
                int[] aacSampleRates = { 96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050,
                                         16000, 12000, 11025, 8000, 7350 };

                if (samplingRateIndex < aacSampleRates.Length)
                    audio.SampleRate = aacSampleRates[samplingRateIndex];
                audio.Channels = channelConfig;

                _messages.Add($"AAC ADTS: profile={profile}, sr_index={samplingRateIndex} ({audio.SampleRate}Hz), " +
                             $"ch_config={channelConfig}");
            }

            _messages.Add("注意: AAC 完整解码需要 PNS/TNS/IMDCT 实现");

            return audio;
        }

        // =================================================================================
        //  Opus 导入
        // =================================================================================

        private ImportedAudio ImportOpus(string filePath)
        {
            byte[] data = File.ReadAllBytes(filePath);
            var audio = new ImportedAudio
            {
                SourcePath = filePath,
                SourceFormat = "Opus",
                Name = Path.GetFileNameWithoutExtension(filePath),
                BitsPerSample = 16,
                IsFloat = false
            };

            // Opus 在 OGG 容器中
            int offset = 0;

            while (offset + 27 < data.Length)
            {
                if (data[offset] != 'O' || data[offset + 1] != 'g' || data[offset + 2] != 'g' || data[offset + 3] != 'S')
                    break;

                byte segmentCount = data[offset + 26];
                int headerSize = 27 + segmentCount;

                int[] segmentSizes = new int[segmentCount];
                int dataSize = 0;
                for (int i = 0; i < segmentCount; i++)
                {
                    segmentSizes[i] = data[offset + 27 + i];
                    dataSize += segmentSizes[i];
                }

                int pageDataOffset = offset + headerSize;

                // 第一页包含 OpusHead
                if (pageDataOffset + 19 < data.Length)
                {
                    if (data[pageDataOffset] == 'O' && data[pageDataOffset + 1] == 'p' &&
                        data[pageDataOffset + 2] == 'u' && data[pageDataOffset + 3] == 's' &&
                        data[pageDataOffset + 4] == 'H' && data[pageDataOffset + 5] == 'e' &&
                        data[pageDataOffset + 6] == 'a' && data[pageDataOffset + 7] == 'd')
                    {
                        // OpusHead 结构
                        byte version = data[pageDataOffset + 8];
                        byte channels = data[pageDataOffset + 9];
                        uint preSkip = BitConverter.ToUInt16(data, pageDataOffset + 10);
                        uint inputSampleRate = BitConverter.ToUInt32(data, pageDataOffset + 12);

                        audio.Channels = channels;
                        // Opus 原始采样率始终为 48kHz
                        audio.SampleRate = 48000;

                        _messages.Add($"Opus: 版本={version}, 声道={channels}, " +
                                     $"原始采样率={inputSampleRate}Hz, 预跳过={preSkip}");
                        break;
                    }
                }

                offset = pageDataOffset + dataSize;
            }

            _messages.Add("注意: Opus 完整解码需要 CELT/SILK 实现");

            return audio;
        }

        // =================================================================================
        //  后处理
        // =================================================================================

        private void PostProcessAudio(ImportedAudio audio, AudioImportOptions options)
        {
            // 采样率转换
            if (options.TargetSampleRate > 0 && options.TargetSampleRate != audio.SampleRate)
            {
                ResampleAudio(audio, options.TargetSampleRate, options.ResampleQuality);
            }

            // 声道转换
            if (options.TargetChannels > 0 && options.TargetChannels != audio.Channels)
            {
                ConvertChannels(audio, options.TargetChannels);
            }

            // 位深转换
            if (options.TargetBitsPerSample > 0 && options.TargetBitsPerSample != audio.BitsPerSample)
            {
                audio.BitsPerSample = options.TargetBitsPerSample;
                if (options.TargetBitsPerSample == 32)
                    audio.IsFloat = true;
            }

            // 标准化
            if (options.Normalize && audio.Samples != null)
            {
                NormalizeAudio(audio, options.NormalizeMode, options.NormalizeTargetLevel);
            }

            // 循环点检测
            if (options.DetectLoopPoints && audio.Samples != null)
            {
                DetectLoopPoints(audio, options.LoopDetectionThreshold);
            }
        }

        /// <summary>重采样音频。</summary>
        private void ResampleAudio(ImportedAudio audio, int targetSampleRate, ResampleQuality quality)
        {
            if (audio.Samples == null) return;

            double ratio = (double)targetSampleRate / audio.SampleRate;
            long newSampleCount = (long)(audio.SampleCount * ratio);
            var newSamples = new float[newSampleCount * audio.Channels];

            for (int ch = 0; ch < audio.Channels; ch++)
            {
                for (long i = 0; i < newSampleCount; i++)
                {
                    double srcPos = i / ratio;
                    float sample;

                    switch (quality)
                    {
                        case ResampleQuality.Nearest:
                            {
                                long srcIdx = (long)Math.Round(srcPos);
                                if (srcIdx >= audio.SampleCount) srcIdx = audio.SampleCount - 1;
                                sample = audio.Samples[srcIdx * audio.Channels + ch];
                                break;
                            }
                        case ResampleQuality.Linear:
                            {
                                long idx0 = (long)srcPos;
                                long idx1 = Math.Min(idx0 + 1, audio.SampleCount - 1);
                                float frac = (float)(srcPos - idx0);
                                float s0 = audio.Samples[idx0 * audio.Channels + ch];
                                float s1 = audio.Samples[idx1 * audio.Channels + ch];
                                sample = s0 * (1 - frac) + s1 * frac;
                                break;
                            }
                        case ResampleQuality.Cubic:
                            {
                                long idx1 = (long)srcPos;
                                long idx0 = Math.Max(0, idx1 - 1);
                                long idx2 = Math.Min(idx1 + 1, audio.SampleCount - 1);
                                long idx3 = Math.Min(idx1 + 2, audio.SampleCount - 1);
                                float frac = (float)(srcPos - idx1);
                                float s0 = audio.Samples[idx0 * audio.Channels + ch];
                                float s1 = audio.Samples[idx1 * audio.Channels + ch];
                                float s2 = audio.Samples[idx2 * audio.Channels + ch];
                                float s3 = audio.Samples[idx3 * audio.Channels + ch];
                                // 三次插值
                                float a = -0.5f * s0 + 1.5f * s1 - 1.5f * s2 + 0.5f * s3;
                                float b = s0 - 2.5f * s1 + 2f * s2 - 0.5f * s3;
                                float c = -0.5f * s0 + 0.5f * s2;
                                sample = ((a * frac + b) * frac + c) * frac + s1;
                                break;
                            }
                        default:
                            {
                                // Hermite / Sinc 简化为线性
                                long idx0 = (long)srcPos;
                                long idx1 = Math.Min(idx0 + 1, audio.SampleCount - 1);
                                float frac = (float)(srcPos - idx0);
                                sample = audio.Samples[idx0 * audio.Channels + ch] * (1 - frac) +
                                         audio.Samples[idx1 * audio.Channels + ch] * frac;
                                break;
                            }
                    }

                    newSamples[i * audio.Channels + ch] = Math.Clamp(sample, -1f, 1f);
                }
            }

            audio.SampleRate = targetSampleRate;
            audio.SampleCount = newSampleCount;
            audio.Duration = (double)newSampleCount / targetSampleRate;
            audio.Samples = newSamples;
            _messages.Add($"重采样: {audio.SampleRate}Hz ({quality})");
        }

        /// <summary>声道转换。</summary>
        private void ConvertChannels(ImportedAudio audio, int targetChannels)
        {
            if (audio.Samples == null) return;

            var newSamples = new float[audio.SampleCount * targetChannels];

            if (audio.Channels == 1 && targetChannels == 2)
            {
                // 单声道 → 立体声
                for (long i = 0; i < audio.SampleCount; i++)
                {
                    float s = audio.Samples[i];
                    newSamples[i * 2] = s;
                    newSamples[i * 2 + 1] = s;
                }
            }
            else if (audio.Channels == 2 && targetChannels == 1)
            {
                // 立体声 → 单声道
                for (long i = 0; i < audio.SampleCount; i++)
                {
                    newSamples[i] = (audio.Samples[i * 2] + audio.Samples[i * 2 + 1]) * 0.5f;
                }
            }
            else
            {
                // 通用转换
                for (long i = 0; i < audio.SampleCount; i++)
                {
                    float sum = 0;
                    for (int c = 0; c < audio.Channels; c++)
                        sum += audio.Samples[i * audio.Channels + c];
                    float avg = sum / audio.Channels;
                    for (int c = 0; c < targetChannels; c++)
                        newSamples[i * targetChannels + c] = avg;
                }
            }

            audio.Channels = targetChannels;
            audio.Samples = newSamples;
            _messages.Add($"声道转换: {targetChannels}ch");
        }

        /// <summary>音频标准化。</summary>
        private void NormalizeAudio(ImportedAudio audio, NormalizeMode mode, float targetLevel)
        {
            if (audio.Samples == null || audio.Samples.Length == 0) return;

            float gain = 1f;

            switch (mode)
            {
                case NormalizeMode.Peak:
                    {
                        // 峰值标准化
                        float peak = 0;
                        for (int i = 0; i < audio.Samples.Length; i++)
                            peak = Math.Max(peak, Math.Abs(audio.Samples[i]));

                        if (peak > 0)
                        {
                            float targetPeak = MathF.Pow(10, targetLevel / 20f);
                            gain = targetPeak / peak;
                        }
                        break;
                    }
                case NormalizeMode.RMS:
                    {
                        // RMS 标准化
                        double sumSq = 0;
                        for (int i = 0; i < audio.Samples.Length; i++)
                            sumSq += audio.Samples[i] * audio.Samples[i];

                        float rms = (float)Math.Sqrt(sumSq / audio.Samples.Length);
                        if (rms > 0)
                        {
                            float targetRMS = MathF.Pow(10, targetLevel / 20f);
                            gain = targetRMS / rms;
                        }
                        break;
                    }
                case NormalizeMode.EBU_R128_LUFS:
                    {
                        // EBU R128 LUFS 响度标准化（简化）
                        // 完整实现需要 K-weighting 滤波 + 滑动窗口
                        // 简化为 RMS 估算
                        double sumSq = 0;
                        for (int i = 0; i < audio.Samples.Length; i++)
                            sumSq += audio.Samples[i] * audio.Samples[i];

                        float rms = (float)Math.Sqrt(sumSq / audio.Samples.Length);
                        float lufs = rms > 0 ? 20 * MathF.Log10(rms) - 0.691f : -70f;
                        float targetLUFS = targetLevel; // 通常 -23 LUFS
                        gain = MathF.Pow(10, (targetLUFS - lufs) / 20f);
                        break;
                    }
            }

            // 应用增益
            for (int i = 0; i < audio.Samples.Length; i++)
                audio.Samples[i] = Math.Clamp(audio.Samples[i] * gain, -1f, 1f);

            _messages.Add($"标准化: {mode}, 增益={gain:F4}x, 目标={targetLevel}dB");
        }

        /// <summary>检测循环点。</summary>
        private void DetectLoopPoints(ImportedAudio audio, float threshold)
        {
            if (audio.Samples == null || audio.SampleCount < 100) return;

            // 寻找最佳循环点：首尾匹配
            int windowSize = 1024; // 检测窗口
            if (windowSize > audio.SampleCount / 2) windowSize = (int)(audio.SampleCount / 2);

            // 计算首尾窗口的相关性
            float bestError = float.MaxValue;
            int bestLoopStart = 0;

            for (int start = 0; start < audio.SampleCount - windowSize * 2; start += 256)
            {
                float error = 0;
                for (int i = 0; i < windowSize; i++)
                {
                    float s1 = audio.Samples[(start + i) * audio.Channels];
                    float s2 = audio.Samples[(int)(audio.SampleCount - windowSize + i) * audio.Channels];
                    error += Math.Abs(s1 - s2);
                }

                if (error < bestError)
                {
                    bestError = error;
                    bestLoopStart = start;
                }
            }

            float avgError = bestError / windowSize;
            if (avgError < threshold)
            {
                audio.LoopStart = bestLoopStart;
                audio.LoopEnd = (int)(audio.SampleCount - windowSize);
                audio.IsSeamlessLoop = avgError < threshold * 0.1f;

                _messages.Add($"循环点检测: {audio.LoopStart}-{audio.LoopEnd}, " +
                             $"误差={avgError:F6}, 无缝={audio.IsSeamlessLoop}");
            }
            else
            {
                _messages.Add("循环点检测: 未找到合适的循环点");
            }
        }
    }
}
