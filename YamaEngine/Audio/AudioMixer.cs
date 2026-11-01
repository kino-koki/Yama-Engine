using System; using System.Collections.Generic; using System.Numerics; namespace YamaEngine.Audio;

public enum AudioBusType { Master, Music, SFX, Voice, Ambient, UI, Dialogue, Reverb, Paused, World }
public enum AudioCurveType { Linear, Logarithmic, Exponential, Custom }
public enum AudioFilterType { None, LowPass, HighPass, BandPass, Notch, AllPass, ParametricEQ }
public enum AudioChannel { Mono, Stereo, Surround51, Surround71, Binaural, HRTF }
public enum AudioSpatialization { None, Simple3D, HRTF, Pan2D }
public enum AudioCulling { AlwaysPlay, DistanceCull, OcclusionCull, PortalCull }

public struct AudioClipData
{
    public string Name, FilePath; public float Duration, SampleRate;
    public int Channels; public float[]? Samples;
    public bool IsStreaming, IsLooping, Is3D;
    public float PreloadedSeconds = 2f;
    public AudioClipData() { PreloadedSeconds = 2f; }
}

public struct AudioSourceConfig
{
    public string Id; public AudioClipData? Clip;
    public float Volume = 1f, Pitch = 1f, Pan = 0f;
    public bool Loop, PlayOnAwake, Mute;
    public float MinDistance = 1f, MaxDistance = 500f, Rolloff = 1f;
    public AudioSpatialization Spatialization = AudioSpatialization.Simple3D;
    public AudioCulling Culling = AudioCulling.DistanceCull;
    public AudioBusType Bus = AudioBusType.SFX;
    public int Priority = 128;
    public float DopplerLevel = 1f, Spread, ConeInner, ConeOuter, ConeOuterVolume = 1f;
    public Vector3 Position, Velocity;
    public AudioSourceConfig() { }
}

public sealed class AudioSourceInstance
{
    public AudioSourceConfig Config; public bool IsPlaying, IsPaused, IsStopped = true;
    public float PlaybackTime, CurrentVolume, CurrentPitch;
    public Vector3 Position, Velocity;
    public int ActiveVoices;
    public float FadeTarget = 1f, FadeTime, FadeTimer;
    public AudioFilterType Filter; public float FilterFreq = 22000, FilterQ = 0.707f;
    internal int _voiceIndex = -1;
    internal float _lastHeardTime;

    public void Play() { IsPlaying = true; IsPaused = false; IsStopped = false; PlaybackTime = 0; }
    public void Stop() { IsPlaying = false; IsPaused = false; IsStopped = true; PlaybackTime = 0; }
    public void Pause() { IsPlaying = false; IsPaused = true; }
    public void Resume() { if (IsPaused) { IsPlaying = true; IsPaused = false; } }
    public void FadeTo(float target, float time) { FadeTarget = target; FadeTime = time; FadeTimer = 0; }
    public bool IsFinished => !Config.Loop && PlaybackTime >= (Config.Clip?.Duration ?? 0);
}




public sealed class Audio3DSpatializer
{
    private Vector3 _listenerPos, _listenerForward, _listenerUp;
    private float _dopplerFactor = 1f;

    public void SetListener(Vector3 pos, Vector3 fwd, Vector3 up)
    { _listenerPos = pos; _listenerForward = Vector3.Normalize(fwd); _listenerUp = Vector3.Normalize(up); }

    public (float attenuation, float pan) Calculate3D(Vector3 sourcePos)
    {
        Vector3 toSource = sourcePos - _listenerPos;
        float dist = toSource.Length();
        if (dist < 0.001f) return (1f, 0f);

        Vector3 dir = toSource / dist;
        float forwardDot = Vector3.Dot(dir, _listenerForward);
        float rightDot = Vector3.Dot(dir, Vector3.Normalize(Vector3.Cross(_listenerForward, _listenerUp)));

        float attenuation = 1f / (1f + 0.1f * dist);
        float pan = Math.Clamp(rightDot, -1f, 1f);
        return (Math.Clamp(attenuation, 0, 1), pan);
    }

    public float CalculateDoppler(Vector3 sourceVel, Vector3 listenerVel, float soundSpeed = 343f)
    {
        Vector3 relVel = sourceVel - listenerVel;
        Vector3 toSource = Vector3.Normalize(sourceVel);
        float dot = Vector3.Dot(relVel, toSource);
        return 1f + _dopplerFactor * dot / soundSpeed;
    }
}

public sealed class StreamingAudioManager
{
    private readonly Dictionary<string, AudioClipData> _streamingClips = new();
    private int _bufferSizeSeconds = 4, _preloadBuffers = 2;
    private long _totalStreamedBytes;
    private int _activeStreams;

    public int ActiveStreams => _activeStreams;
    public long TotalStreamedBytes => _totalStreamedBytes;

    public void RegisterStreamingClip(string name, string filePath, float duration, int channels = 2)
    {
        _streamingClips[name] = new AudioClipData
        { Name = name, FilePath = filePath, Duration = duration, Channels = channels, SampleRate = 48000, IsStreaming = true };
    }

    public AudioClipData? GetClip(string name) => _streamingClips.TryGetValue(name, out var c) ? c : null;

    public float[] StreamChunk(string name, int chunkIndex, int chunkSize)
    {
        _activeStreams++;
        var data = new float[chunkSize];
        _totalStreamedBytes += chunkSize * 4;
        _activeStreams--;
        return data;
    }
}

public sealed class VoiceChatManager
{
    private bool _isActive, _isMuted, _isDeafened;
    private float _micVolume = 1f, _playbackVolume = 1f;
    private int _sampleRate = 48000, _frameSize = 480;
    private float _noiseGateThreshold = 0.01f;
    private readonly Queue<short[]> _pendingFrames = new();

    public bool IsActive { get => _isActive; set => _isActive = value; }
    public bool IsMuted { get => _isMuted; set => _isMuted = value; }
    public bool IsDeafened { get => _isDeafened; set => _isDeafened = value; }
    public float MicVolume { get => _micVolume; set => _micVolume = Math.Clamp(value, 0, 2); }
    public int SampleRate => _sampleRate;

    public short[] ProcessMicInput(short[] rawInput)
    {
        if (_isMuted) return Array.Empty<short>();
        var processed = new short[rawInput.Length];
        float threshold = _noiseGateThreshold * short.MaxValue;
        for (int i = 0; i < rawInput.Length; i++)
        {
            float sample = rawInput[i] * _micVolume;
            if (MathF.Abs(sample) < threshold) sample = 0;
            processed[i] = (short)Math.Clamp(sample, short.MinValue, short.MaxValue);
        }
        return processed;
    }

    public void EnqueueRemoteFrame(short[] frame) { _pendingFrames.Enqueue(frame); }

    public short[] GetNextFrame()
    {
        return _pendingFrames.TryDequeue(out var frame) ? frame : Array.Empty<short>();
    }

    public int PendingFrames => _pendingFrames.Count;
    public void Clear() { _pendingFrames.Clear(); }
}
