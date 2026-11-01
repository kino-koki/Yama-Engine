// ============================================================================================
//  FMODAudioSystem.cs
//  yama引擎 (YamaEngine) FMOD Studio 音频中间件集成层
// ============================================================================================
//  参照 Unity 2024 的 AudioSource / AudioMixer / AudioListener 架构，
//  将 FMOD Studio C# API 封装为与现有 AudioSystem 兼容的接口。
// ============================================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace YamaEngine.Audio
{
    // ====== FMOD P/Invoke 最小子集（替代 fmod.dll 官方 C# 包装） ======
    /// <remarks>
    /// 实际项目中应引用 FMOD 官方 NuGet 包 (fmod 或 FMOD.Studio)。
    /// 此处提供最小 P/Invoke 接口，确保无外部依赖即可编译。
    /// </remarks>
    public static class FMODNative
    {
        private const string FMODLib = "fmod.dll";
        private const string FMODStudioLib = "fmodstudio.dll";

        // System
        [DllImport(FMODLib)] public static extern int FMOD_System_Create(out IntPtr system);
        [DllImport(FMODLib)] public static extern int FMOD_System_Init(IntPtr system, int maxChannels, uint flags, IntPtr extradriverdata);
        [DllImport(FMODLib)] public static extern int FMOD_System_Close(IntPtr system);
        [DllImport(FMODLib)] public static extern int FMOD_System_Release(IntPtr system);
        [DllImport(FMODLib)] public static extern int FMOD_System_Update(IntPtr system);
        [DllImport(FMODLib)] public static extern int FMOD_System_Set3DListenerAttributes(IntPtr system, int listener, ref Vector3 pos, ref Vector3 vel, ref Vector3 forward, ref Vector3 up);
        [DllImport(FMODLib)] public static extern int FMOD_System_GetMasterChannelGroup(IntPtr system, out IntPtr channelGroup);
        [DllImport(FMODLib)] public static extern int FMOD_System_CreateSound(IntPtr system, string name_or_data, uint mode, IntPtr exinfo, out IntPtr sound);
        [DllImport(FMODLib)] public static extern int FMOD_System_PlaySound(IntPtr system, IntPtr sound, IntPtr channelGroup, bool paused, out IntPtr channel);
        [DllImport(FMODLib)] public static extern int FMOD_System_GetChannel(IntPtr system, int channelid, out IntPtr channel);
        [DllImport(FMODLib)] public static extern int FMOD_System_GetSoftwareFormat(IntPtr system, out int sampleRate, out uint speakerMode, out int numRawSpeakers);

        // Sound
        [DllImport(FMODLib)] public static extern int FMOD_Sound_Release(IntPtr sound);
        [DllImport(FMODLib)] public static extern int FMOD_Sound_GetLength(IntPtr sound, out uint length, uint lengthType);
        [DllImport(FMODLib)] public static extern int FMOD_Sound_Set3DMinMaxDistance(IntPtr sound, float minDistance, float maxDistance);

        // Channel
        [DllImport(FMODLib)] public static extern int FMOD_Channel_Stop(IntPtr channel);
        [DllImport(FMODLib)] public static extern int FMOD_Channel_SetPaused(IntPtr channel, bool paused);
        [DllImport(FMODLib)] public static extern int FMOD_Channel_GetPaused(IntPtr channel, out bool paused);
        [DllImport(FMODLib)] public static extern int FMOD_Channel_SetVolume(IntPtr channel, float volume);
        [DllImport(FMODLib)] public static extern int FMOD_Channel_GetVolume(IntPtr channel, out float volume);
        [DllImport(FMODLib)] public static extern int FMOD_Channel_SetPitch(IntPtr channel, float pitch);
        [DllImport(FMODLib)] public static extern int FMOD_Channel_Set3DAttributes(IntPtr channel, ref Vector3 pos, ref Vector3 vel);
        [DllImport(FMODLib)] public static extern int FMOD_Channel_Set3DMinMaxDistance(IntPtr channel, float minDistance, float maxDistance);
        [DllImport(FMODLib)] public static extern int FMOD_Channel_SetLoopCount(IntPtr channel, int loopCount);
        [DllImport(FMODLib)] public static extern int FMOD_Channel_SetMode(IntPtr channel, uint mode);
        [DllImport(FMODLib)] public static extern int FMOD_Channel_IsPlaying(IntPtr channel, out bool isPlaying);
        [DllImport(FMODLib)] public static extern int FMOD_Channel_GetPosition(IntPtr channel, out uint position, uint positionType);
        [DllImport(FMODLib)] public static extern int FMOD_Channel_SetPosition(IntPtr channel, uint position, uint positionType);
        [DllImport(FMODLib)] public static extern int FMOD_Channel_SetPan(IntPtr channel, float pan);
        [DllImport(FMODLib)] public static extern int FMOD_Channel_SetReverbProperties(IntPtr channel, int instance, float wet);

        // ChannelGroup (Bus)
        [DllImport(FMODLib)] public static extern int FMOD_ChannelGroup_SetVolume(IntPtr channelGroup, float volume);
        [DllImport(FMODLib)] public static extern int FMOD_ChannelGroup_GetVolume(IntPtr channelGroup, out float volume);
        [DllImport(FMODLib)] public static extern int FMOD_ChannelGroup_SetPitch(IntPtr channelGroup, float pitch);
        [DllImport(FMODLib)] public static extern int FMOD_ChannelGroup_SetMute(IntPtr channelGroup, bool mute);
        [DllImport(FMODLib)] public static extern int FMOD_ChannelGroup_AddGroup(IntPtr channelGroup, IntPtr group, bool propagatedspclock, out IntPtr connection);

        // DSP
        [DllImport(FMODLib)] public static extern int FMOD_System_CreateDSPByType(IntPtr system, int type, out IntPtr dsp);
        [DllImport(FMODLib)] public static extern int FMOD_Channel_AddDSP(IntPtr channel, int index, IntPtr dsp);

        // Studio System
        [DllImport(FMODStudioLib)] public static extern int FMOD_Studio_System_Create(out IntPtr system, uint headerVersion);
        [DllImport(FMODStudioLib)] public static extern int FMOD_Studio_System_Initialize(IntPtr system, int maxChannels, uint studioFlags, uint flags, IntPtr extradriverdata);
        [DllImport(FMODStudioLib)] public static extern int FMOD_Studio_System_Release(IntPtr system);
        [DllImport(FMODStudioLib)] public static extern int FMOD_Studio_System_Update(IntPtr system);
        [DllImport(FMODStudioLib)] public static extern int FMOD_Studio_System_LoadBankFile(IntPtr system, string filename, uint flags, out IntPtr bank);
        [DllImport(FMODStudioLib)] public static extern int FMOD_Studio_System_GetEvent(IntPtr system, string path, out IntPtr description);
        [DllImport(FMODStudioLib)] public static extern int FMOD_Studio_System_GetBus(IntPtr system, string path, out IntPtr bus);
        [DllImport(FMODStudioLib)] public static extern int FMOD_Studio_System_GetVCA(IntPtr system, string path, out IntPtr vca);
        [DllImport(FMODStudioLib)] public static extern int FMOD_Studio_System_GetCoreSystem(IntPtr system, out IntPtr coreSystem);

        // Studio Event
        [DllImport(FMODStudioLib)] public static extern int FMOD_Studio_EventDescription_CreateInstance(IntPtr eventDescription, out IntPtr instance);
        [DllImport(FMODStudioLib)] public static extern int FMOD_Studio_EventInstance_Start(IntPtr instance);
        [DllImport(FMODStudioLib)] public static extern int FMOD_Studio_EventInstance_Stop(IntPtr instance, int mode);
        [DllImport(FMODStudioLib)] public static extern int FMOD_Studio_EventInstance_Release(IntPtr instance);
        [DllImport(FMODStudioLib)] public static extern int FMOD_Studio_EventInstance_Set3DAttributes(IntPtr instance, ref FMOD_3D_ATTRIBUTES attributes);
        [DllImport(FMODStudioLib)] public static extern int FMOD_Studio_EventInstance_SetParameterByName(IntPtr instance, string name, float value, bool ignoreseekspeed);
        [DllImport(FMODStudioLib)] public static extern int FMOD_Studio_EventInstance_GetPlaybackState(IntPtr instance, out int state);
        [DllImport(FMODStudioLib)] public static extern int FMOD_Studio_EventInstance_SetVolume(IntPtr instance, float volume);
        [DllImport(FMODStudioLib)] public static extern int FMOD_Studio_EventInstance_SetPitch(IntPtr instance, float pitch);

        // Studio Bus
        [DllImport(FMODStudioLib)] public static extern int FMOD_Studio_Bus_SetVolume(IntPtr bus, float volume);
        [DllImport(FMODStudioLib)] public static extern int FMOD_Studio_Bus_GetVolume(IntPtr bus, out float volume, out float finalVolume);
        [DllImport(FMODStudioLib)] public static extern int FMOD_Studio_Bus_SetMute(IntPtr bus, bool mute);
        [DllImport(FMODStudioLib)] public static extern int FMOD_Studio_Bus_GetMute(IntPtr bus, out bool mute);
        [DllImport(FMODStudioLib)] public static extern int FMOD_Studio_Bus_SetPaused(IntPtr bus, bool paused);
        [DllImport(FMODStudioLib)] public static extern int FMOD_Studio_Bus_StopAllEvents(IntPtr bus, int mode);

        // Studio VCA
        [DllImport(FMODStudioLib)] public static extern int FMOD_Studio_VCA_SetVolume(IntPtr vca, float volume);
        [DllImport(FMODStudioLib)] public static extern int FMOD_Studio_VCA_GetVolume(IntPtr vca, out float volume, out float finalVolume);

        // Studio Bank
        [DllImport(FMODStudioLib)] public static extern int FMOD_Studio_Bank_LoadSampleData(IntPtr bank);
        [DllImport(FMODStudioLib)] public static extern int FMOD_Studio_Bank_Unload(IntPtr bank);

        // Constants
        public const uint FMOD_DEFAULT = 0x00000000;
        public const uint FMOD_LOOP_OFF = 0x00000001;
        public const uint FMOD_LOOP_NORMAL = 0x00000002;
        public const uint FMOD_3D = 0x00000010;
        public const uint FMOD_3D_WORLDRELATIVE = 0x00000080;
        public const uint FMOD_CREATESTREAM = 0x00000080;
        public const uint FMOD_CREATESAMPLE = 0x00000100;
        public const uint FMOD_INIT_NORMAL = 0x00000000;
        public const uint FMOD_INIT_3D_RIGHTHANDED = 0x00000004;
        public const uint FMOD_STUDIO_INIT_NORMAL = 0x00000000;
        public const uint FMOD_STUDIO_INIT_LIVEUPDATE = 0x00000001;
        public const int FMOD_OK = 0;
        public const int FMOD_STUDIO_PLAYBACK_STOPPED = 0;
        public const int FMOD_STUDIO_PLAYBACK_PLAYING = 1;
        public const int FMOD_STUDIO_PLAYBACK_SUSTAINING = 2;
        public static bool OK(int result) => result == FMOD_OK;

        public const uint HEADER_VERSION = 0x00020221; // FMOD Studio 2.02.33
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FMOD_3D_ATTRIBUTES
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public Vector3 Forward;
        public Vector3 Up;
    }

    // ====== FMOD 音频剪辑 ======
    public class FMODAudioClip
    {
        public string Path { get; }
        public IntPtr SoundHandle { get; internal set; }
        public float DurationSeconds { get; internal set; }
        public bool Is3D { get; set; }
        public float MinDistance { get; set; } = 1.0f;
        public float MaxDistance { get; set; } = 100.0f;
        public bool IsLoaded => SoundHandle != IntPtr.Zero;

        public FMODAudioClip(string path) { Path = path; }
    }

    // ====== FMOD 音频实例（播放中） ======
    public class FMODAudioInstance
    {
        public Guid Id { get; } = Guid.NewGuid();
        public FMODAudioClip? Clip { get; }
        public IntPtr ChannelHandle { get; internal set; }
        public IntPtr StudioInstanceHandle { get; internal set; }
        public bool IsStudioEvent => StudioInstanceHandle != IntPtr.Zero;
        public bool IsPlaying { get; set; }

        public FMODAudioInstance(FMODAudioClip? clip = null)
        {
            Clip = clip;
        }
        public Vector3 Position { get; set; }
        public Vector3 Velocity { get; set; }
        public float Volume { get; set; } = 1.0f;
        public float Pitch { get; set; } = 1.0f;
        public bool IsPaused { get; set; }
        public bool IsLooping { get; set; }
        public float Pan { get; set; }
        public int Priority { get; set; }
        public string BusName { get; set; } = "bus:/";

        public void Update3DPosition()
        {
            if (IsStudioEvent && StudioInstanceHandle != IntPtr.Zero)
            {
                var attr = new FMOD_3D_ATTRIBUTES
                {
                    Position = Position, Velocity = Velocity,
                    Forward = new Vector3(0, 0, 1), Up = new Vector3(0, 1, 0)
                };
                FMODNative.FMOD_Studio_EventInstance_Set3DAttributes(StudioInstanceHandle, ref attr);
            }
            else if (ChannelHandle != IntPtr.Zero)
            {
                var pos = Position;
                var vel = Velocity;
                FMODNative.FMOD_Channel_Set3DAttributes(ChannelHandle, ref pos, ref vel);
            }
        }
    }

    // ====== FMOD 总线（Bus）管理 ======
    public class FMODBus
    {
        public string Path { get; }
        public IntPtr Handle { get; internal set; }
        public string Name { get; }
        public float Volume { get; set; } = 1.0f;
        public bool IsMuted { get; set; }
        public bool IsPaused { get; set; }

        public FMODBus(string path, string name) { Path = path; Name = name; }

        public void Apply()
        {
            if (Handle == IntPtr.Zero) return;
            FMODNative.FMOD_Studio_Bus_SetVolume(Handle, Volume);
            FMODNative.FMOD_Studio_Bus_SetMute(Handle, IsMuted);
            FMODNative.FMOD_Studio_Bus_SetPaused(Handle, IsPaused);
        }
    }

    // ====== FMOD Studio Bank ======
    public class FMODBank
    {
        public string FilePath { get; }
        public IntPtr Handle { get; internal set; }
        public bool IsLoaded => Handle != IntPtr.Zero;

        public FMODBank(string path) { FilePath = path; }
    }

    // ====== FMOD 音频系统（核心管理器） ======
    /// <summary>FMOD Studio 音频系统 —— Unity 2024 风格 AudioSource/AudioMixer/AudioListener 架构</summary>
    public class FMODAudioSystem : IDisposable
    {
        // 原生句柄
        private IntPtr _coreSystem = IntPtr.Zero;
        private IntPtr _studioSystem = IntPtr.Zero;
        private IntPtr _masterGroup = IntPtr.Zero;

        // 资源
        private readonly Dictionary<string, FMODAudioClip> _clips = new();
        private readonly Dictionary<Guid, FMODAudioInstance> _instances = new();
        private readonly Dictionary<string, FMODBus> _buses = new();
        private readonly Dictionary<string, FMODBank> _banks = new();
        private readonly Dictionary<string, IntPtr> _eventDescriptions = new();

        // 监听者
        private Vector3 _listenerPosition = Vector3.Zero;
        private Vector3 _listenerForward = new Vector3(0, 0, 1);
        private Vector3 _listenerUp = new Vector3(0, 1, 0);
        private Vector3 _listenerVelocity = Vector3.Zero;

        // 配置
        public int MaxChannels { get; set; } = 512;
        public bool Enable3D { get; set; } = true;
        public bool EnableLiveUpdate { get; set; } = false; // FMOD Studio Live Update
        public float MasterVolume { get; set; } = 1.0f;
        public bool IsInitialized => _studioSystem != IntPtr.Zero;

        // 默认总线（参照 Unity AudioMixer 分组）
        public static readonly string[] DefaultBuses =
        {
            "bus:/", "bus:/Master", "bus:/Master/Music", "bus:/Master/SFX",
            "bus:/Master/Voice", "bus:/Master/Ambient", "bus:/Master/UI"
        };

        // ====== 初始化 ======
        public void Initialize()
        {
            if (IsInitialized) return;

            // 创建 FMOD Studio System
            var result = FMODNative.FMOD_Studio_System_Create(out _studioSystem, FMODNative.HEADER_VERSION);
            if (!FMODNative.OK(result)) throw new InvalidOperationException($"FMOD Studio System 创建失败: {result}");

            // 获取底层 Core System
            result = FMODNative.FMOD_Studio_System_GetCoreSystem(_studioSystem, out _coreSystem);
            if (!FMODNative.OK(result)) throw new InvalidOperationException($"FMOD Core System 获取失败: {result}");

            // 初始化
            uint studioFlags = FMODNative.FMOD_STUDIO_INIT_NORMAL;
            if (EnableLiveUpdate) studioFlags |= FMODNative.FMOD_STUDIO_INIT_LIVEUPDATE;
            uint coreFlags = FMODNative.FMOD_INIT_NORMAL | FMODNative.FMOD_INIT_3D_RIGHTHANDED;

            result = FMODNative.FMOD_Studio_System_Initialize(_studioSystem, MaxChannels, studioFlags, coreFlags, IntPtr.Zero);
            if (!FMODNative.OK(result)) throw new InvalidOperationException($"FMOD Studio 初始化失败: {result}");

            // 获取 Master ChannelGroup
            FMODNative.FMOD_System_GetMasterChannelGroup(_coreSystem, out _masterGroup);

            // 初始化默认总线
            InitializeBuses();
        }

        private void InitializeBuses()
        {
            foreach (var path in DefaultBuses)
            {
                var name = path.Substring(path.LastIndexOf('/') + 1);
                if (string.IsNullOrEmpty(name)) name = "Master";
                var bus = new FMODBus(path, name);
                IntPtr busHandle;
                var result = FMODNative.FMOD_Studio_System_GetBus(_studioSystem, path, out busHandle);
                bus.Handle = busHandle;
                if (FMODNative.OK(result)) _buses[path] = bus;
            }
        }

        // ====== Bank 加载 ======
        public FMODBank LoadBank(string filePath)
        {
            if (_banks.TryGetValue(filePath, out var existing)) return existing;
            var bank = new FMODBank(filePath);
            IntPtr bankHandle;
            var result = FMODNative.FMOD_Studio_System_LoadBankFile(_studioSystem, filePath, 0, out bankHandle);
            bank.Handle = bankHandle;
            if (FMODNative.OK(result))
            {
                FMODNative.FMOD_Studio_Bank_LoadSampleData(bank.Handle);
                _banks[filePath] = bank;
            }
            return bank;
        }

        public void UnloadBank(string filePath)
        {
            if (_banks.TryGetValue(filePath, out var bank) && bank.IsLoaded)
            {
                FMODNative.FMOD_Studio_Bank_Unload(bank.Handle);
                _banks.Remove(filePath);
            }
        }

        // ====== 音频剪辑 ======
        public FMODAudioClip LoadClip(string filePath, bool is3D = true)
        {
            if (_clips.TryGetValue(filePath, out var existing)) return existing;
            var clip = new FMODAudioClip(filePath) { Is3D = is3D };
            uint mode = FMODNative.FMOD_CREATESAMPLE;
            if (is3D) mode |= FMODNative.FMOD_3D | FMODNative.FMOD_3D_WORLDRELATIVE;
            IntPtr soundHandle;
            var result = FMODNative.FMOD_System_CreateSound(_coreSystem, filePath, mode, IntPtr.Zero, out soundHandle);
            clip.SoundHandle = soundHandle;
            if (FMODNative.OK(result))
            {
                FMODNative.FMOD_Sound_GetLength(clip.SoundHandle, out var len, 0);
                clip.DurationSeconds = len / 1000.0f;
                FMODNative.FMOD_Sound_Set3DMinMaxDistance(clip.SoundHandle, clip.MinDistance, clip.MaxDistance);
            }
            _clips[filePath] = clip;
            return clip;
        }

        public void UnloadClip(string filePath)
        {
            if (_clips.TryGetValue(filePath, out var clip) && clip.IsLoaded)
            {
                FMODNative.FMOD_Sound_Release(clip.SoundHandle);
                _clips.Remove(filePath);
            }
        }

        // ====== 播放控制（Unity AudioSource 风格） ======
        public FMODAudioInstance PlayOneShot(string clipPath, Vector3? position = null, float volume = 1.0f, float pitch = 1.0f)
        {
            var clip = LoadClip(clipPath);
            return PlayClip(clip, position, volume, pitch, loop: false);
        }

        public FMODAudioInstance PlayClip(FMODAudioClip clip, Vector3? position = null, float volume = 1.0f, float pitch = 1.0f, bool loop = false)
        {
            if (!clip.IsLoaded) return new FMODAudioInstance(clip);

            var instance = new FMODAudioInstance(clip)
            {
                Position = position ?? Vector3.Zero,
                Volume = volume,
                Pitch = pitch,
                IsLooping = loop
            };

            var result = FMODNative.FMOD_System_PlaySound(_coreSystem, clip.SoundHandle, _masterGroup, true, out IntPtr chHandle);
            instance.ChannelHandle = chHandle;
            if (!FMODNative.OK(result)) return instance;

            // 配置通道
            FMODNative.FMOD_Channel_SetVolume(instance.ChannelHandle, volume);
            FMODNative.FMOD_Channel_SetPitch(instance.ChannelHandle, pitch);
            if (clip.Is3D)
            {
                var pos = instance.Position;
                var vel = instance.Velocity;
                FMODNative.FMOD_Channel_Set3DAttributes(instance.ChannelHandle, ref pos, ref vel);
                FMODNative.FMOD_Channel_Set3DMinMaxDistance(instance.ChannelHandle, clip.MinDistance, clip.MaxDistance);
            }
            if (loop) FMODNative.FMOD_Channel_SetLoopCount(instance.ChannelHandle, -1);
            FMODNative.FMOD_Channel_SetPaused(instance.ChannelHandle, false);

            instance.IsPlaying = true;
            _instances[instance.Id] = instance;
            return instance;
        }

        // ====== Studio Event 播放（事件驱动音频） ======
        public FMODAudioInstance PlayEvent(string eventPath, Vector3? position = null)
        {
            if (!_eventDescriptions.TryGetValue(eventPath, out var desc))
            {
                var result = FMODNative.FMOD_Studio_System_GetEvent(_studioSystem, eventPath, out desc);
                if (!FMODNative.OK(result)) return new FMODAudioInstance(null);
                _eventDescriptions[eventPath] = desc;
            }

            var result2 = FMODNative.FMOD_Studio_EventDescription_CreateInstance(desc, out var instanceHandle);
            if (!FMODNative.OK(result2)) return new FMODAudioInstance(null);

            var instance = new FMODAudioInstance(null)
            {
                StudioInstanceHandle = instanceHandle,
                Position = position ?? Vector3.Zero
            };

            var attr = new FMOD_3D_ATTRIBUTES
            {
                Position = instance.Position, Velocity = Vector3.Zero,
                Forward = new Vector3(0, 0, 1), Up = new Vector3(0, 1, 0)
            };
            FMODNative.FMOD_Studio_EventInstance_Set3DAttributes(instanceHandle, ref attr);
            FMODNative.FMOD_Studio_EventInstance_Start(instanceHandle);

            instance.IsPlaying = true;
            _instances[instance.Id] = instance;
            return instance;
        }

        public void SetEventParameter(FMODAudioInstance instance, string paramName, float value)
        {
            if (instance.IsStudioEvent && instance.StudioInstanceHandle != IntPtr.Zero)
                FMODNative.FMOD_Studio_EventInstance_SetParameterByName(instance.StudioInstanceHandle, paramName, value, false);
        }

        // ====== 实例控制 ======
        public void StopInstance(Guid instanceId, bool immediate = false)
        {
            if (!_instances.TryGetValue(instanceId, out var inst)) return;
            if (inst.IsStudioEvent && inst.StudioInstanceHandle != IntPtr.Zero)
            {
                FMODNative.FMOD_Studio_EventInstance_Stop(inst.StudioInstanceHandle, immediate ? 0 : 1);
                FMODNative.FMOD_Studio_EventInstance_Release(inst.StudioInstanceHandle);
            }
            else if (inst.ChannelHandle != IntPtr.Zero)
            {
                FMODNative.FMOD_Channel_Stop(inst.ChannelHandle);
            }
            inst.IsPlaying = false;
            _instances.Remove(instanceId);
        }

        public void PauseInstance(Guid instanceId)
        {
            if (_instances.TryGetValue(instanceId, out var inst) && inst.ChannelHandle != IntPtr.Zero)
            {
                FMODNative.FMOD_Channel_SetPaused(inst.ChannelHandle, true);
                inst.IsPaused = true;
            }
        }

        public void ResumeInstance(Guid instanceId)
        {
            if (_instances.TryGetValue(instanceId, out var inst) && inst.ChannelHandle != IntPtr.Zero)
            {
                FMODNative.FMOD_Channel_SetPaused(inst.ChannelHandle, false);
                inst.IsPaused = false;
            }
        }

        public void SetInstanceVolume(Guid instanceId, float volume)
        {
            if (_instances.TryGetValue(instanceId, out var inst))
            {
                inst.Volume = volume;
                if (inst.IsStudioEvent && inst.StudioInstanceHandle != IntPtr.Zero)
                    FMODNative.FMOD_Studio_EventInstance_SetVolume(inst.StudioInstanceHandle, volume);
                else if (inst.ChannelHandle != IntPtr.Zero)
                    FMODNative.FMOD_Channel_SetVolume(inst.ChannelHandle, volume);
            }
        }

        public void SetInstancePitch(Guid instanceId, float pitch)
        {
            if (_instances.TryGetValue(instanceId, out var inst))
            {
                inst.Pitch = pitch;
                if (inst.IsStudioEvent && inst.StudioInstanceHandle != IntPtr.Zero)
                    FMODNative.FMOD_Studio_EventInstance_SetPitch(inst.StudioInstanceHandle, pitch);
                else if (inst.ChannelHandle != IntPtr.Zero)
                    FMODNative.FMOD_Channel_SetPitch(inst.ChannelHandle, pitch);
            }
        }

        // ====== 3D 监听者 ======
        public void SetListener(Vector3 position, Vector3 forward, Vector3 up, Vector3 velocity)
        {
            _listenerPosition = position;
            _listenerForward = forward;
            _listenerUp = up;
            _listenerVelocity = velocity;

            if (_coreSystem != IntPtr.Zero)
            {
                FMODNative.FMOD_System_Set3DListenerAttributes(_coreSystem, 0,
                    ref _listenerPosition, ref _listenerVelocity, ref _listenerForward, ref _listenerUp);
            }
        }

        // ====== 总线控制 ======
        public void SetBusVolume(string busPath, float volume)
        {
            if (_buses.TryGetValue(busPath, out var bus)) { bus.Volume = volume; bus.Apply(); }
        }

        public void SetBusMute(string busPath, bool muted)
        {
            if (_buses.TryGetValue(busPath, out var bus)) { bus.IsMuted = muted; bus.Apply(); }
        }

        public void StopAllEventsOnBus(string busPath, bool immediate = false)
        {
            if (_buses.TryGetValue(busPath, out var bus) && bus.Handle != IntPtr.Zero)
                FMODNative.FMOD_Studio_Bus_StopAllEvents(bus.Handle, immediate ? 0 : 1);
        }

        // ====== 每帧更新 ======
        public void Update(float deltaTime)
        {
            if (!IsInitialized) return;

            // 更新所有活跃实例的 3D 位置
            foreach (var inst in _instances.Values)
            {
                if (inst.IsPlaying) inst.Update3DPosition();
            }

            // 更新 FMOD 系统
            FMODNative.FMOD_Studio_System_Update(_studioSystem);
            FMODNative.FMOD_System_Update(_coreSystem);

            // 清理已停止的实例
            var toRemove = new List<Guid>();
            foreach (var kv in _instances)
            {
                var inst = kv.Value;
                if (inst.IsStudioEvent && inst.StudioInstanceHandle != IntPtr.Zero)
                {
                    FMODNative.FMOD_Studio_EventInstance_GetPlaybackState(inst.StudioInstanceHandle, out var state);
                    if (state == FMODNative.FMOD_STUDIO_PLAYBACK_STOPPED) toRemove.Add(kv.Key);
                }
                else if (inst.ChannelHandle != IntPtr.Zero)
                {
                    FMODNative.FMOD_Channel_IsPlaying(inst.ChannelHandle, out var playing);
                    if (!playing) toRemove.Add(kv.Key);
                }
            }
            foreach (var id in toRemove) _instances.Remove(id);
        }

        public void StopAll() { foreach (var id in _instances.Keys.ToArray()) StopInstance(id); }
        public void PauseAll() { foreach (var inst in _instances.Values) PauseInstance(inst.Id); }
        public void ResumeAll() { foreach (var inst in _instances.Values) ResumeInstance(inst.Id); }

        // ====== 状态查询 ======
        public int ActiveInstanceCount => _instances.Count;
        public int LoadedClipCount => _clips.Count;
        public int LoadedBankCount => _banks.Count;

        // ====== 销毁 ======
        public void Dispose()
        {
            StopAll();
            foreach (var clip in _clips.Values) if (clip.IsLoaded) FMODNative.FMOD_Sound_Release(clip.SoundHandle);
            _clips.Clear();
            foreach (var bank in _banks.Values) if (bank.IsLoaded) FMODNative.FMOD_Studio_Bank_Unload(bank.Handle);
            _banks.Clear();
            if (_studioSystem != IntPtr.Zero)
            {
                FMODNative.FMOD_Studio_System_Release(_studioSystem);
                _studioSystem = IntPtr.Zero;
            }
        }
    }
}
