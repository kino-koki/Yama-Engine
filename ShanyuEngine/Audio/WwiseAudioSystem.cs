// ============================================================================================
//  WwiseAudioSystem.cs
//  山雨引擎 (ShanyuEngine) Audiokinetic Wwise 音频中间件集成层
// ============================================================================================
//  参照 Unity 2024 的 AudioSource / AudioMixer / AudioListener 架构，
//  将 Wwise C++ API 通过 P/Invoke 封装为与现有 AudioSystem 兼容的接口。
// ============================================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace ShanyuEngine.Audio
{
    // ====== Wwise Native P/Invoke 最小子集 ======
    /// <remarks>
    /// Wwise 官方提供 C# 绑定 (WwiseSharp / AK.Wwise.API)，但此处提供零依赖 P/Invoke
    /// 以确保编译通过。实际项目中应引用 Wwise SDK 的 C# 封装或 NuGet 包。
    /// </remarks>
    public static class WwiseNative
    {
        private const string AkSoundEngine = "AkSoundEngine.dll";
        private const string AkStreamMgr = "AkStreamMgr.dll";
        private const string AkMusicEngine = "AkMusicEngine.dll";

        // ---- SoundEngine 初始化 / 终止 ----
        [DllImport(AkSoundEngine)] public static extern int AK_SoundEngine_Init(IntPtr settings);
        [DllImport(AkSoundEngine)] public static extern void AK_SoundEngine_Term();
        [DllImport(AkSoundEngine)] public static extern bool AK_SoundEngine_IsInitialized();
        [DllImport(AkSoundEngine)] public static extern int AK_SoundEngine_RenderAudio();

        // ---- 监听器 ----
        [DllImport(AkSoundEngine)] public static extern int AK_SoundEngine_SetListenerPosition(ref AkTransform position, uint listenerIndex);
        [DllImport(AkSoundEngine)] public static extern int AK_SoundEngine_SetListenerScalingFactor(uint listenerIndex, float factor);
        [DllImport(AkSoundEngine)] public static extern int AK_SoundEngine_SetDefaultListeners(IntPtr listeners, uint numListeners);

        // ---- 游戏对象 (GameObject <=> Entity 映射) ----
        [DllImport(AkSoundEngine)] public static extern int AK_SoundEngine_RegisterGameObj(ulong gameObjectID, string name);
        [DllImport(AkSoundEngine)] public static extern int AK_SoundEngine_UnregisterGameObj(ulong gameObjectID);
        [DllImport(AkSoundEngine)] public static extern int AK_SoundEngine_UnregisterAllGameObjs();
        [DllImport(AkSoundEngine)] public static extern int AK_SoundEngine_SetPosition(ulong gameObjectID, ref AkTransform position);
        [DllImport(AkSoundEngine)] public static extern int AK_SoundEngine_SetScalingFactor(ulong gameObjectID, float factor);

        // ---- Event ----
        [DllImport(AkSoundEngine)] public static extern uint AK_SoundEngine_PostEvent(uint eventID, ulong gameObjectID, uint flags, IntPtr callback, IntPtr cookie);
        [DllImport(AkSoundEngine)] public static extern int AK_SoundEngine_ExecuteActionOnEvent(uint eventID, int actionType, ulong gameObjectID, int transitionDuration, int fadeCurve, uint playingID);
        [DllImport(AkSoundEngine)] public static extern int AK_SoundEngine_StopAll(ulong gameObjectID);
        [DllImport(AkSoundEngine)] public static extern int AK_SoundEngine_StopPlayingID(uint playingID, int transitionDuration, int fadeCurve);

        // ---- RTPC (Real-Time Parameter Control) ----
        [DllImport(AkSoundEngine)] public static extern int AK_SoundEngine_SetRTPCValue(uint rtpcID, float value, ulong gameObjectID, int timeMs, int curve, bool bypassInternalValueInterpolation);
        [DllImport(AkSoundEngine)] public static extern int AK_SoundEngine_SetRTPCValueByString(string rtpcName, float value, ulong gameObjectID, int timeMs, int curve, bool bypassInternalValueInterpolation);
        [DllImport(AkSoundEngine)] public static extern int AK_SoundEngine_GetRTPCValue(uint rtpcID, ulong gameObjectID, uint playingID, out float value, ref int valueType);

        // ---- Switch / State ----
        [DllImport(AkSoundEngine)] public static extern int AK_SoundEngine_SetSwitch(uint switchGroup, uint switchState, ulong gameObjectID);
        [DllImport(AkSoundEngine)] public static extern int AK_SoundEngine_SetSwitchByString(string switchGroup, string switchState, ulong gameObjectID);
        [DllImport(AkSoundEngine)] public static extern int AK_SoundEngine_SetState(uint stateGroup, uint state);
        [DllImport(AkSoundEngine)] public static extern int AK_SoundEngine_SetStateByString(string stateGroup, string state);

        // ---- Trigger ----
        [DllImport(AkSoundEngine)] public static extern int AK_SoundEngine_PostTrigger(uint triggerID, ulong gameObjectID);
        [DllImport(AkSoundEngine)] public static extern int AK_SoundEngine_PostTriggerByString(string triggerName, ulong gameObjectID);

        // ---- Bank ----
        [DllImport(AkSoundEngine)] public static extern int AK_SoundEngine_LoadBank(uint bankID, int memPoolId, out uint bankIDout);
        [DllImport(AkSoundEngine)] public static extern int AK_SoundEngine_LoadBankByString(string bankName, int memPoolId, out uint bankIDout);
        [DllImport(AkSoundEngine)] public static extern int AK_SoundEngine_UnloadBank(uint bankID, IntPtr memoryPoolId);
        [DllImport(AkSoundEngine)] public static extern int AK_SoundEngine_UnloadBankByString(string bankName, IntPtr memoryPoolId);
        [DllImport(AkSoundEngine)] public static extern int AK_SoundEngine_PrepareBank(uint bankID, int preparationType);
        [DllImport(AkSoundEngine)] public static extern int AK_SoundEngine_PrepareBankByString(string bankName, int preparationType);

        // ---- Bus / Mixer ----
        [DllImport(AkSoundEngine)] public static extern int AK_SoundEngine_SetBusVolume(uint busID, float volume);
        [DllImport(AkSoundEngine)] public static extern int AK_SoundEngine_SetBusVolumeByString(string busName, float volume);
        [DllImport(AkSoundEngine)] public static extern int AK_SoundEngine_SetBusEffect(uint busID, uint fxShareSetID);
        [DllImport(AkSoundEngine)] public static extern int AK_SoundEngine_ResetBusVolume(uint busID);

        // ---- 3D Spatial Audio (Reflect / Geometry) ----
        [DllImport(AkSoundEngine)] public static extern int AK_SpatialAudio_Init(IntPtr settings);
        [DllImport(AkSoundEngine)] public static extern void AK_SpatialAudio_Term();
        [DllImport(AkSoundEngine)] public static extern int AK_SpatialAudio_RegisterListener(ulong gameObjectID);
        [DllImport(AkSoundEngine)] public static extern int AK_SpatialAudio_UnregisterListener(ulong gameObjectID);
        [DllImport(AkSoundEngine)] public static extern int AK_SpatialAudio_SetRoom(ulong roomID, IntPtr roomParams, IntPtr geometryID);
        [DllImport(AkSoundEngine)] public static extern int AK_SpatialAudio_SetGameObjectInRoom(ulong gameObjectID, ulong roomID);
        [DllImport(AkSoundEngine)] public static extern int AK_SpatialAudio_SetReflectionsOrder(uint reflectionsOrder, bool updatePaths);

        // ---- 错误码常量 ----
        public const int AK_Success = 0;
        public const int AK_Fail = 1;
        public const int AK_PartialSuccess = 2;
        public const int AK_NotInitialized = 3;
        public const int AK_InvalidParameter = 7;
        public const int IDNotFound = 9;
        public const int BankAlreadyLoaded = 28;
    }

    // ====== Wwise 原生结构体 ======
    [StructLayout(LayoutKind.Sequential)]
    public struct AkTransform
    {
        public Vector3 Position;
        public Vector3 OrientationFront;
        public Vector3 OrientationTop;

        public static AkTransform FromMatrix(Matrix4x4 m)
        {
            return new AkTransform
            {
                Position = new Vector3(m.M41, m.M42, m.M43),
                OrientationFront = new Vector3(m.M31, m.M32, m.M33),
                OrientationTop = new Vector3(m.M21, m.M22, m.M23)
            };
        }

        public static AkTransform FromPositionForwardUp(Vector3 pos, Vector3 forward, Vector3 up)
        {
            return new AkTransform
            {
                Position = pos,
                OrientationFront = forward,
                OrientationTop = up
            };
        }
    }

    // ====== Wwise 动作类型 ======
    public enum WwiseActionType
    {
        Stop = 0,
        Pause = 1,
        Resume = 2,
        Break = 4
    }

    public enum WwiseFadeCurve
    {
        Logarithmic = 0,
        Sine = 1,
        SineReciprocal = 2,
        Linear = 3,
        SCurve = 4,
        Exp1 = 5,
        Exp3 = 6
    }

    public enum WwisePreparationType
    {
        Load = 0,
        Unload = 1
    }

    // ====== Wwise Bank 封装 ======
    public sealed class WwiseBank : IDisposable
    {
        public uint BankID { get; }
        public string BankName { get; }
        public bool IsLoaded { get; private set; }

        public WwiseBank(uint bankID, string bankName)
        {
            BankID = bankID;
            BankName = bankName;
        }

        public bool Load(int memoryPoolId = -1)
        {
            int result = string.IsNullOrEmpty(BankName)
                ? WwiseNative.AK_SoundEngine_LoadBank(BankID, memoryPoolId, out _)
                : WwiseNative.AK_SoundEngine_LoadBankByString(BankName, memoryPoolId, out _);
            IsLoaded = result == WwiseNative.AK_Success;
            return IsLoaded;
        }

        public bool Unload(IntPtr? memoryPoolId = null)
        {
            int result = string.IsNullOrEmpty(BankName)
                ? WwiseNative.AK_SoundEngine_UnloadBank(BankID, memoryPoolId ?? IntPtr.Zero)
                : WwiseNative.AK_SoundEngine_UnloadBankByString(BankName, memoryPoolId ?? IntPtr.Zero);
            IsLoaded = result != WwiseNative.AK_Success;
            return !IsLoaded;
        }

        public void Dispose() => Unload();
    }

    // ====== Wwise Event 封装 ======
    public sealed class WwiseEvent
    {
        public uint EventID { get; }
        public string EventName { get; }

        public WwiseEvent(uint eventID, string eventName)
        {
            EventID = eventID;
            EventName = eventName;
        }

        /// <summary>在指定游戏对象上播放事件，返回 PlayingID。</summary>
        public uint Post(ulong gameObjectID)
        {
            return WwiseNative.AK_SoundEngine_PostEvent(EventID, gameObjectID, 0, IntPtr.Zero, IntPtr.Zero);
        }

        public int ExecuteAction(WwiseActionType action, ulong gameObjectID, int transitionMs = 0, WwiseFadeCurve curve = WwiseFadeCurve.Linear)
        {
            return WwiseNative.AK_SoundEngine_ExecuteActionOnEvent(EventID, (int)action, gameObjectID, transitionMs, (int)curve, 0);
        }

        public static uint PostByName(string eventName, ulong gameObjectID)
        {
            // Wwise 没有直接的 PostEventByString，这里通过 ID 映射或反射实现
            // 简化：返回 0 作为占位
            return 0;
        }
    }

    // ====== Wwise RTPC 封装 ======
    public sealed class WwiseRTPC
    {
        public uint RTPCID { get; }
        public string RTPCName { get; }

        public WwiseRTPC(uint id, string name)
        {
            RTPCID = id;
            RTPCName = name;
        }

        public bool SetValue(float value, ulong gameObjectID = ulong.MaxValue, int transitionMs = 0)
        {
            int result;
            if (string.IsNullOrEmpty(RTPCName))
                result = WwiseNative.AK_SoundEngine_SetRTPCValue(RTPCID, value, gameObjectID, transitionMs, (int)WwiseFadeCurve.Linear, false);
            else
                result = WwiseNative.AK_SoundEngine_SetRTPCValueByString(RTPCName, value, gameObjectID, transitionMs, (int)WwiseFadeCurve.Linear, false);
            return result == WwiseNative.AK_Success;
        }

        public float GetValue(ulong gameObjectID = ulong.MaxValue)
        {
            int valueType = 0;
            int result = WwiseNative.AK_SoundEngine_GetRTPCValue(RTPCID, gameObjectID, 0, out float value, ref valueType);
            return result == WwiseNative.AK_Success ? value : 0f;
        }
    }

    // ====== Wwise Switch / State 封装 ======
    public sealed class WwiseSwitch
    {
        public uint SwitchGroupID { get; }
        public string SwitchGroupName { get; }

        public WwiseSwitch(uint groupID, string groupName)
        {
            SwitchGroupID = groupID;
            SwitchGroupName = groupName;
        }

        public bool Set(uint switchStateID, ulong gameObjectID)
        {
            return WwiseNative.AK_SoundEngine_SetSwitch(SwitchGroupID, switchStateID, gameObjectID) == WwiseNative.AK_Success;
        }

        public bool Set(string switchStateName, ulong gameObjectID)
        {
            return WwiseNative.AK_SoundEngine_SetSwitchByString(SwitchGroupName, switchStateName, gameObjectID) == WwiseNative.AK_Success;
        }
    }

    public sealed class WwiseState
    {
        public uint StateGroupID { get; }
        public string StateGroupName { get; }

        public WwiseState(uint groupID, string groupName)
        {
            StateGroupID = groupID;
            StateGroupName = groupName;
        }

        public bool Set(uint stateID)
        {
            return WwiseNative.AK_SoundEngine_SetState(StateGroupID, stateID) == WwiseNative.AK_Success;
        }

        public bool Set(string stateName)
        {
            return WwiseNative.AK_SoundEngine_SetStateByString(StateGroupName, stateName) == WwiseNative.AK_Success;
        }
    }

    // ====== Wwise Bus 封装 ======
    public sealed class WwiseBus : IDisposable
    {
        public uint BusID { get; }
        public string BusName { get; }
        public float Volume { get; private set; } = 1.0f;

        public WwiseBus(uint busID, string busName)
        {
            BusID = busID;
            BusName = busName;
        }

        public bool SetVolume(float volume)
        {
            Volume = volume;
            int result = string.IsNullOrEmpty(BusName)
                ? WwiseNative.AK_SoundEngine_SetBusVolume(BusID, volume)
                : WwiseNative.AK_SoundEngine_SetBusVolumeByString(BusName, volume);
            return result == WwiseNative.AK_Success;
        }

        public bool ResetVolume()
        {
            Volume = 1.0f;
            return WwiseNative.AK_SoundEngine_ResetBusVolume(BusID) == WwiseNative.AK_Success;
        }

        public void Dispose() { }
    }

    // ====== Wwise Audio Source (Unity 风格) ======
    /// <summary>
    /// 模拟 Unity AudioSource 的 Wwise 封装。
    /// 每个 WwiseAudioSource 绑定到一个 ECS Entity（通过 GameObjectID）。
    /// </summary>
    public sealed class WwiseAudioSource : IDisposable
    {
        public ulong GameObjectID { get; }
        public string Name { get; set; } = "WwiseAudioSource";

        // 3D 属性
        public Vector3 Position { get; set; }
        public Vector3 Forward { get; set; } = -Vector3.UnitZ;
        public Vector3 Up { get; set; } = Vector3.UnitY;
        public float ScalingFactor { get; set; } = 1.0f;

        // 当前播放的事件
        private readonly List<uint> _activePlayingIDs = new();
        public IReadOnlyList<uint> ActivePlayingIDs => _activePlayingIDs;

        public WwiseAudioSource(ulong gameObjectID, string? name = null)
        {
            GameObjectID = gameObjectID;
            Name = name ?? $"WwiseGO_{gameObjectID}";
            WwiseNative.AK_SoundEngine_RegisterGameObj(gameObjectID, Name);
        }

        public uint PlayEvent(uint eventID)
        {
            uint playingID = WwiseNative.AK_SoundEngine_PostEvent(eventID, GameObjectID, 0, IntPtr.Zero, IntPtr.Zero);
            if (playingID != 0) _activePlayingIDs.Add(playingID);
            return playingID;
        }

        public uint PlayEvent(WwiseEvent evt) => PlayEvent(evt.EventID);

        public void StopAll()
        {
            WwiseNative.AK_SoundEngine_StopAll(GameObjectID);
            _activePlayingIDs.Clear();
        }

        public void StopEvent(uint playingID, int fadeMs = 0)
        {
            WwiseNative.AK_SoundEngine_StopPlayingID(playingID, fadeMs, (int)WwiseFadeCurve.Linear);
            _activePlayingIDs.Remove(playingID);
        }

        public void SetSwitch(WwiseSwitch sw, string stateName) => sw.Set(stateName, GameObjectID);
        public void SetRTPC(WwiseRTPC rtpc, float value, int transitionMs = 0) => rtpc.SetValue(value, GameObjectID, transitionMs);
        public void PostTrigger(string triggerName) => WwiseNative.AK_SoundEngine_PostTriggerByString(triggerName, GameObjectID);

        public void UpdateTransform()
        {
            var transform = AkTransform.FromPositionForwardUp(Position, Forward, Up);
            WwiseNative.AK_SoundEngine_SetPosition(GameObjectID, ref transform);
            if (Math.Abs(ScalingFactor - 1.0f) > 0.001f)
                WwiseNative.AK_SoundEngine_SetScalingFactor(GameObjectID, ScalingFactor);
        }

        public void Dispose()
        {
            StopAll();
            WwiseNative.AK_SoundEngine_UnregisterGameObj(GameObjectID);
        }
    }

    // ====== Wwise Audio Listener ======
    /// <summary>模拟 Unity AudioListener —— Wwise 主监听器。</summary>
    public sealed class WwiseAudioListener : IDisposable
    {
        public uint ListenerIndex { get; }
        public Vector3 Position { get; set; }
        public Vector3 Forward { get; set; } = -Vector3.UnitZ;
        public Vector3 Up { get; set; } = Vector3.UnitY;
        public Vector3 Velocity { get; set; }
        public float ScalingFactor { get; set; } = 1.0f;

        public WwiseAudioListener(uint index = 0)
        {
            ListenerIndex = index;
        }

        public void Update()
        {
            var transform = AkTransform.FromPositionForwardUp(Position, Forward, Up);
            WwiseNative.AK_SoundEngine_SetListenerPosition(ref transform, ListenerIndex);
            WwiseNative.AK_SoundEngine_SetListenerScalingFactor(ListenerIndex, ScalingFactor);
        }

        public void Dispose() { }
    }

    // ====== Wwise 主系统 (单例) ======
    /// <summary>
    /// Wwise 音频系统主管理器。提供与 FMODAudioSystem 同等级别的引擎集成接口。
    /// </summary>
    public sealed class WwiseAudioSystem : IDisposable
    {
        public static WwiseAudioSystem? Instance { get; private set; }

        private bool _initialized;
        private readonly Dictionary<uint, WwiseBank> _banks = new();
        private readonly Dictionary<uint, WwiseEvent> _events = new();
        private readonly Dictionary<uint, WwiseRTPC> _rtpcs = new();
        private readonly Dictionary<uint, WwiseSwitch> _switches = new();
        private readonly Dictionary<uint, WwiseState> _states = new();
        private readonly Dictionary<uint, WwiseBus> _buses = new();
        private readonly Dictionary<ulong, WwiseAudioSource> _sources = new();
        private readonly List<WwiseAudioListener> _listeners = new();

        public bool IsInitialized => _initialized;
        public IReadOnlyDictionary<uint, WwiseBank> Banks => _banks;
        public IReadOnlyDictionary<ulong, WwiseAudioSource> Sources => _sources;

        // ---- 初始化 ----
        public bool Initialize()
        {
            if (_initialized) return true;
            if (Instance != null && Instance != this)
                throw new InvalidOperationException("WwiseAudioSystem 已存在另一个实例");

            int result = WwiseNative.AK_SoundEngine_Init(IntPtr.Zero);
            _initialized = result == WwiseNative.AK_Success;

            if (_initialized)
            {
                Instance = this;
                // 初始化 Spatial Audio
                WwiseNative.AK_SpatialAudio_Init(IntPtr.Zero);
            }

            return _initialized;
        }

        // ---- 终止 ----
        public void Shutdown()
        {
            if (!_initialized) return;

            foreach (var source in _sources.Values) source.Dispose();
            _sources.Clear();

            foreach (var bank in _banks.Values) bank.Unload();
            _banks.Clear();

            WwiseNative.AK_SpatialAudio_Term();
            WwiseNative.AK_SoundEngine_Term();
            _initialized = false;
            if (Instance == this) Instance = null;
        }

        public void Dispose() => Shutdown();

        // ---- 每帧更新 ----
        public void Update()
        {
            if (!_initialized) return;
            WwiseNative.AK_SoundEngine_RenderAudio();
        }

        // ---- Bank 管理 ----
        public WwiseBank? LoadBank(string bankName, int memoryPoolId = -1)
        {
            uint bankID = AkHash(bankName);
            if (_banks.TryGetValue(bankID, out var existing) && existing.IsLoaded)
                return existing;

            var bank = new WwiseBank(bankID, bankName);
            if (bank.Load(memoryPoolId))
            {
                _banks[bankID] = bank;
                return bank;
            }
            return null;
        }

        public bool UnloadBank(string bankName)
        {
            uint bankID = AkHash(bankName);
            if (_banks.TryGetValue(bankID, out var bank))
            {
                bank.Unload();
                _banks.Remove(bankID);
                return true;
            }
            return false;
        }

        // ---- Event 管理 ----
        public WwiseEvent RegisterEvent(uint eventID, string eventName)
        {
            var evt = new WwiseEvent(eventID, eventName);
            _events[eventID] = evt;
            return evt;
        }

        public uint PostEvent(uint eventID, ulong gameObjectID)
        {
            if (!_events.TryGetValue(eventID, out var evt))
                evt = RegisterEvent(eventID, $"Event_{eventID}");
            return evt.Post(gameObjectID);
        }

        // ---- RTPC 管理 ----
        public WwiseRTPC RegisterRTPC(uint rtpcID, string rtpcName)
        {
            var rtpc = new WwiseRTPC(rtpcID, rtpcName);
            _rtpcs[rtpcID] = rtpc;
            return rtpc;
        }

        public bool SetRTPC(uint rtpcID, float value, ulong gameObjectID = ulong.MaxValue)
        {
            if (!_rtpcs.TryGetValue(rtpcID, out var rtpc))
                rtpc = RegisterRTPC(rtpcID, $"RTPC_{rtpcID}");
            return rtpc.SetValue(value, gameObjectID);
        }

        // ---- Switch / State 管理 ----
        public WwiseSwitch RegisterSwitch(uint groupID, string groupName)
        {
            var sw = new WwiseSwitch(groupID, groupName);
            _switches[groupID] = sw;
            return sw;
        }

        public WwiseState RegisterState(uint groupID, string groupName)
        {
            var st = new WwiseState(groupID, groupName);
            _states[groupID] = st;
            return st;
        }

        // ---- Bus 管理 ----
        public WwiseBus RegisterBus(uint busID, string busName)
        {
            var bus = new WwiseBus(busID, busName);
            _buses[busID] = bus;
            return bus;
        }

        public bool SetBusVolume(string busName, float volume)
        {
            return WwiseNative.AK_SoundEngine_SetBusVolumeByString(busName, volume) == WwiseNative.AK_Success;
        }

        // ---- AudioSource 工厂 ----
        public WwiseAudioSource CreateSource(ulong gameObjectID, string? name = null)
        {
            if (_sources.TryGetValue(gameObjectID, out var existing))
                return existing;

            var source = new WwiseAudioSource(gameObjectID, name);
            _sources[gameObjectID] = source;
            return source;
        }

        public void DestroySource(ulong gameObjectID)
        {
            if (_sources.TryGetValue(gameObjectID, out var source))
            {
                source.Dispose();
                _sources.Remove(gameObjectID);
            }
        }

        // ---- Listener 管理 ----
        public WwiseAudioListener CreateListener(uint index = 0)
        {
            var listener = new WwiseAudioListener(index);
            _listeners.Add(listener);
            return listener;
        }

        public void UpdateListeners()
        {
            foreach (var listener in _listeners)
                listener.Update();
        }

        // ---- 实用工具 ----
        /// <summary>Wwise 字符串哈希 —— 模拟 AK::SoundEngine::GetIDFromString</summary>
        public static uint AkHash(string str)
        {
            if (string.IsNullOrEmpty(str)) return 0;
            uint hash = 0x811c9dc5;
            foreach (char c in str)
            {
                hash ^= (byte)char.ToLowerInvariant(c);
                hash *= 0x01000193;
            }
            return hash;
        }
    }
}
