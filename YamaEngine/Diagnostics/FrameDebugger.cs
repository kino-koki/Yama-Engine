using System.Collections.Concurrent;
using System.Diagnostics;

namespace YamaEngine.Diagnostics;

/// <summary>
/// Frame-by-frame debugging tool that captures per-frame performance data.
/// Allows capturing snapshots of CPU/GPU timing, draw calls, memory allocations,
/// and system events for diagnosing frame rate issues.
/// </summary>
public static class FrameDebugger
{
    private static readonly Stopwatch _frameStopwatch = new();
    private static readonly ConcurrentQueue<FrameSnapshot> _capturedFrames = new();
    private static readonly ConcurrentQueue<FrameEvent> _frameEvents = new();
    private const int MaxCapturedFrames = 120;

    /// <summary>Whether frame capture is enabled.</summary>
    public static bool IsCapturing { get; private set; }

    /// <summary>Number of captured frames available for inspection.</summary>
    public static int CapturedFrameCount => _capturedFrames.Count;

    /// <summary>Start capturing frame data.</summary>
    public static void StartCapture()
    {
        IsCapturing = true;
        _frameStopwatch.Restart();
        Logger.Info("[FrameDebugger] Capture started");
    }

    /// <summary>Stop capturing frame data.</summary>
    public static void StopCapture()
    {
        IsCapturing = false;
        _frameStopwatch.Stop();
        Logger.Info($"[FrameDebugger] Capture stopped: {_capturedFrames.Count} frames captured");
    }

    /// <summary>Clear all captured data.</summary>
    public static void Clear()
    {
        _capturedFrames.Clear();
        _frameEvents.Clear();
    }

    /// <summary>Record a frame event (e.g. "BeginDraw", "Present").</summary>
    public static void RecordEvent(string category, string label, long durationMicroseconds = 0)
    {
        if (!IsCapturing) return;
        _frameEvents.Enqueue(new FrameEvent
        {
            Category = category,
            Label = label,
            Timestamp = _frameStopwatch.Elapsed.TotalMilliseconds,
            DurationUs = durationMicroseconds,
        });
    }

    /// <summary>Record a frame snapshot. Call at the end of each frame.</summary>
    public static void SnapshotFrame(string? label = null)
    {
        if (!IsCapturing) return;

        var snapshot = new FrameSnapshot
        {
            FrameNumber = s_frameCounter++,
            Label = label ?? $"Frame {s_frameCounter - 1}",
            Timestamp = DateTime.UtcNow,
            Events = _frameEvents.ToArray(),
            TotalFrameTimeMs = _frameStopwatch.Elapsed.TotalMilliseconds,
        };
        _frameEvents.Clear();
        _capturedFrames.Enqueue(snapshot);

        // Keep ring buffer
        while (_capturedFrames.Count > MaxCapturedFrames)
            _capturedFrames.TryDequeue(out _);

        _frameStopwatch.Restart();
    }

    private static int s_frameCounter;

    /// <summary>Get captured frames (oldest first).</summary>
    public static FrameSnapshot[] GetCapturedFrames()
    {
        return _capturedFrames.ToArray();
    }

    /// <summary>Get the most recent frame snapshot.</summary>
    public static FrameSnapshot? GetLastFrame()
    {
        return _capturedFrames.TryPeek(out var last) ? last : null;
    }

    /// <summary>Save captured frame data to a JSON file for offline analysis.</summary>
    public static void ExportCapture(string filePath)
    {
        try
        {
            var frames = GetCapturedFrames();
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                CaptureTime = DateTime.UtcNow,
                FrameCount = frames.Length,
                Frames = frames,
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(filePath, json);
            Logger.Info($"[FrameDebugger] Exported {frames.Length} frames to {filePath}");
        }
        catch (Exception ex)
        {
            Logger.Error($"[FrameDebugger] Export failed: {ex.Message}");
        }
    }
}

#region Data Types

/// <summary>A single frame's performance data.</summary>
public sealed class FrameSnapshot
{
    public int FrameNumber { get; set; }
    public string Label { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public double TotalFrameTimeMs { get; set; }
    public FrameEvent[] Events { get; set; } = Array.Empty<FrameEvent>();

    public double FPS => TotalFrameTimeMs > 0 ? 1000.0 / TotalFrameTimeMs : 0;
}

/// <summary>An event recorded within a frame.</summary>
public sealed class FrameEvent
{
    public string Category { get; set; } = "";
    public string Label { get; set; } = "";
    public double Timestamp { get; set; }
    public long DurationUs { get; set; }
}

#endregion
