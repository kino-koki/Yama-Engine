// AudioBackendTests.cs — 音频后端 (软件/OpenAL) 验证套件
// 覆盖:
//   * SoftwareAudioBackend 捕获 PCM (提交 → 回读 + 计数器)
//   * SoftwareAudioBackend 导出 16-bit WAV (RIFF/WAVE/fmt/data 头正确)
//   * SoftwareAudioBackend Clear/Shutdown 重置
//   * OpenALSoftBackend 原生库缺失时优雅回退 (不抛异常)
//   * AdvancedAudioEngine 软件后端集成 (InitializeBackend(Software) → Backend 就绪)
// 全部 CPU 级确定性, 不依赖真实音频硬件/原生库。
using System;
using System.Linq;
using System.Text;
using YamaEngine.Audio;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

/// <summary>音频后端验证套件（对齐 UE4.7 跨平台音频设备后端）。</summary>
public sealed class AudioBackendTests
{
    /// <summary>SoftwareAudioBackend 捕获提交的 PCM。</summary>
    public static void TestSoftwareBackendCapturesPcm()
    {
        var backend = new SoftwareAudioBackend();
        AssertTrue(backend.Initialize(48000, 2, 512), "软件后端应初始化成功。");
        AssertEqual(AudioBackendType.Software, backend.BackendType, "BackendType 应为 Software。");
        AssertTrue(backend.IsInitialized, "应已初始化。");

        var buf = new float[] { 0.1f, -0.2f, 0.3f, -0.4f };
        backend.SubmitBuffer(buf);
        backend.SubmitBuffer(buf);

        AssertEqual(2L, backend.SubmittedBufferCount, "应提交 2 个缓冲。");
        AssertEqual(8L, backend.TotalRenderedSamples, "应累积 8 个样本。");
        var rendered = backend.GetRenderedSamples();
        AssertEqual(8, rendered.Length, "回读样本数应为 8。");
        AssertEqual(0.1f, rendered[0], "首样本应保留。");
        AssertEqual(-0.4f, rendered[3], "第 4 样本应保留。");
        AssertEqual(4, backend.GetRenderedFrameCount(), "帧数 = 样本/声道 = 4。");
    }

    /// <summary>SoftwareAudioBackend 导出 16-bit WAV。</summary>
    public static void TestSoftwareBackendExportWav16()
    {
        var backend = new SoftwareAudioBackend();
        backend.Initialize(44100, 2, 512);
        backend.SubmitBuffer(new float[] { 1f, -1f, 0.5f, -0.5f });

        var wav = backend.ExportWav16Bit();

        AssertEqual("RIFF", Encoding.ASCII.GetString(wav, 0, 4), "WAV 头应为 RIFF。");
        AssertEqual("WAVE", Encoding.ASCII.GetString(wav, 8, 4), "应为 WAVE 格式。");
        AssertEqual("fmt ", Encoding.ASCII.GetString(wav, 12, 4), "应为 fmt 块。");
        AssertEqual("data", Encoding.ASCII.GetString(wav, 36, 4), "应为 data 块。");

        // data 大小 = 样本数 * 2 字节 = 4 * 2 = 8
        int dataSize = BitConverter.ToInt32(wav, 40);
        AssertEqual(8, dataSize, "data 块大小应为 8 字节。");
        // RIFF 大小 = 文件总长 - 8 = 52 - 8 = 44
        int riffSize = BitConverter.ToInt32(wav, 4);
        AssertEqual(44, riffSize, "RIFF 块大小应为 44。");
        // 文件总长 = 44(头) + 8(data 大小) = 52
        AssertEqual(52, wav.Length, "WAV 文件总长应为 52。");
        // fmt 块校验: 采样率/声道/位深
        AssertEqual(44100, BitConverter.ToInt32(wav, 24), "采样率应为 44100。");
        AssertEqual((short)2, BitConverter.ToInt16(wav, 22), "声道数应为 2。");
        AssertEqual((short)16, BitConverter.ToInt16(wav, 34), "位深应为 16。");
    }

    /// <summary>SoftwareAudioBackend Clear/Shutdown 重置。</summary>
    public static void TestSoftwareBackendClearAndShutdown()
    {
        var backend = new SoftwareAudioBackend();
        backend.Initialize(48000, 2, 512);
        backend.SubmitBuffer(new float[] { 0.1f, 0.2f });

        backend.Clear();
        AssertEqual(0L, backend.TotalRenderedSamples, "Clear 应清空样本。");
        AssertEqual(0, backend.GetRenderedSamples().Length, "Clear 后回读应为空。");

        backend.SubmitBuffer(new float[] { 0.5f, 0.5f });
        backend.Shutdown();
        AssertTrue(!backend.IsInitialized, "Shutdown 后应未初始化。");
        AssertEqual(0, backend.GetRenderedSamples().Length, "Shutdown 应清空样本。");
    }

    /// <summary>OpenALSoftBackend 原生库缺失时优雅回退 (不抛异常)。</summary>
    public static void TestOpenALBackendNoThrowWhenNativeMissing()
    {
        var backend = new OpenALSoftBackend();
        bool ok = false;
        try
        {
            ok = backend.Initialize(48000, 2, 512);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"OpenAL Initialize 不应抛出异常: {ex.Message}");
        }

        // 原生库存在 → 已初始化; 缺失 → 返回 false (上层回退 Dummy), 两者皆合法
        if (ok)
        {
            AssertTrue(backend.IsInitialized, "OpenAL 应已初始化。");
            AssertEqual(AudioBackendType.OpenAL, backend.BackendType, "BackendType 应为 OpenAL。");
            backend.Shutdown();
        }
        else
        {
            AssertTrue(!backend.IsInitialized, "原生库缺失时应优雅返回 false。");
        }
    }

    /// <summary>AdvancedAudioEngine 软件后端集成。</summary>
    public static void TestEngineSoftwareBackendIntegration()
    {
        var engine = new AdvancedAudioEngine();
        bool ok = engine.InitializeBackend(AudioBackendType.Software);

        AssertTrue(ok, "软件后端初始化应成功。");
        AssertTrue(engine.IsBackendInitialized, "引擎后端应已初始化。");
        AssertTrue(engine.Backend is SoftwareAudioBackend, "后端应为 SoftwareAudioBackend。");
        AssertEqual(AudioBackendType.Software, engine.Backend!.BackendType, "引擎后端类型应为 Software。");
    }
}
