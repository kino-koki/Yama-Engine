using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.Audio
{
    public enum AudioType
    {
        SFX,
        Music,
        Ambient,
        Voice,
        UI
    }

    public enum AudioState
    {
        Stopped,
        Playing,
        Paused,
        FadingIn,
        FadingOut
    }

    public class AudioClip
    {
        public Guid Id = Guid.NewGuid();
        public string Name;
        public string FilePath;
        public float Duration;
        public int SampleRate;
        public int Channels;
        public AudioType Type;
        public float Volume = 1f;
        public bool Is3D = true;
        public bool Looping;
        public byte[] Data;

        public AudioClip() { }
        public AudioClip(string name, float duration, AudioType type = AudioType.SFX)
        {
            Name = name;
            Duration = duration;
            Type = type;
        }
    }

    public class AudioInstance
    {
        public Guid Id = Guid.NewGuid();
        public AudioClip Clip;
        public Vector3 Position;
        public Vector3 Velocity;
        public float Volume = 1f;
        public float Pitch = 1f;
        public float Pan;
        public float PlaybackTime;
        public AudioState State;
        public bool Is3D;
        public float MinDistance = 1f;
        public float MaxDistance = 500f;
        public float RolloffFactor = 1f;
        public float DopplerFactor = 1f;
        public bool Loop;
        public int Priority;
        public DateTime StartTime;
        public float FadeDuration;
        public float FadeTarget;
        public AudioBus Bus = AudioBus.Master;
    }


    public class AudioBusState
    {
        public AudioBus Bus;
        public float Volume = 1f;
        public bool Muted;
        public bool Solo;
        public List<AudioInstance> Instances = new();
        public float PeakLevel;
        public float RMSLevel;
    }


    public class AudioSystem
    {
        private readonly Dictionary<Guid, AudioClip> _clips = new();
        private readonly Dictionary<Guid, AudioInstance> _instances = new();
        private readonly Dictionary<AudioBus, AudioBusState> _buses = new();
        private readonly List<AudioListener> _listeners = new();
        private readonly Dictionary<string, AudioClip> _clipRegistry = new();
        private int _maxInstances = 128;
        private float _masterVolume = 1f;
        private bool _isEnabled = true;
        private bool _isPaused;

        public int ActiveInstances
        {
            get
            {
                int count = 0;
                foreach (var inst in _instances.Values)
                    if (inst.State == AudioState.Playing || inst.State == AudioState.FadingIn || inst.State == AudioState.FadingOut)
                        count++;
                return count;
            }
        }

        public int ClipCount => _clips.Count;
        public int ListenerCount => _listeners.Count;
        public int BusCount => _buses.Count;
        public bool IsPaused { get => _isPaused; set => _isPaused = value; }
        public float MasterVolume { get => _masterVolume; set => _masterVolume = Math.Clamp(value, 0f, 2f); }

        public event Action<AudioInstance>? OnPlay;
        public event Action<AudioInstance>? OnStop;
        public event Action<AudioInstance>? OnPause;
        public event Action<AudioBus, float>? OnBusVolumeChanged;

        public AudioSystem()
        {
            // AudioBus 是类（非枚举）——遍历预定义静态总线，而非 Enum.GetValues
            foreach (AudioBus bus in new[] { AudioBus.Master, AudioBus.Music, AudioBus.SFX, AudioBus.Voice, AudioBus.Ambient, AudioBus.UI })
                _buses[bus] = new AudioBusState { Bus = bus };

            _listeners.Add(new AudioListener { Position = Vector3.Zero, Rotation = Quaternion.Identity });
        }

        /// <summary>初始化音频系统。</summary>
        public void Initialize(int sampleRate = 44100, int bufferSize = 2048)
        {
            _sampleRate = sampleRate;
            _bufferSize = bufferSize;
            _isEnabled = true;
        }

        private int _sampleRate = 44100;
        private int _bufferSize = 2048;

        /// <summary>设置主音量 (0~2)。</summary>
        public void SetMasterVolume(float volume) => MasterVolume = volume;

        public AudioClip RegisterClip(string name, string path, float duration, AudioType type = AudioType.SFX)
        {
            var clip = new AudioClip
            {
                Name = name,
                FilePath = path,
                Duration = duration,
                Type = type
            };
            _clips[clip.Id] = clip;
            _clipRegistry[name] = clip;
            return clip;
        }

        public AudioClip? GetClip(string name)
        {
            return _clipRegistry.TryGetValue(name, out var clip) ? clip : null;
        }

        public AudioClip? GetClip(Guid id)
        {
            return _clips.TryGetValue(id, out var clip) ? clip : null;
        }

        public AudioInstance PlayOneShot(AudioClip? clip, Vector3 position, float volume = 1f, float pitch = 1f)
        {
            if (clip == null || !_isEnabled || _isPaused) return null;

            if (_instances.Count >= _maxInstances)
                CleanupStopped();

            var instance = new AudioInstance
            {
                Clip = clip,
                Position = position,
                Volume = volume,
                Pitch = pitch,
                State = AudioState.Playing,
                Is3D = clip.Is3D,
                Loop = false,
                Priority = 100,
                StartTime = DateTime.Now,
                Bus = clip.Type == AudioType.Music ? AudioBus.Music :
                      clip.Type == AudioType.Voice ? AudioBus.Voice :
                      clip.Type == AudioType.Ambient ? AudioBus.Ambient :
                      clip.Type == AudioType.UI ? AudioBus.UI : AudioBus.SFX
            };
            _instances[instance.Id] = instance;
            _buses[instance.Bus].Instances.Add(instance);
            OnPlay?.Invoke(instance);
            return instance;
        }

        public AudioInstance PlayLooping(AudioClip? clip, Vector3 position, float volume = 1f, float fadeIn = 0f)
        {
            if (clip == null || !_isEnabled) return null;
            var instance = new AudioInstance
            {
                Clip = clip,
                Position = position,
                Volume = fadeIn > 0f ? 0f : volume,
                Pitch = 1f,
                State = fadeIn > 0f ? AudioState.FadingIn : AudioState.Playing,
                Is3D = clip.Is3D,
                Loop = true,
                Priority = 50,
                FadeDuration = fadeIn,
                FadeTarget = volume,
                StartTime = DateTime.Now,
                Bus = clip.Type == AudioType.Music ? AudioBus.Music : AudioBus.Ambient
            };
            _instances[instance.Id] = instance;
            _buses[instance.Bus].Instances.Add(instance);
            OnPlay?.Invoke(instance);
            return instance;
        }

        public void Stop(Guid instanceId, float fadeOut = 0f)
        {
            if (_instances.TryGetValue(instanceId, out var inst))
            {
                if (fadeOut > 0f)
                {
                    inst.State = AudioState.FadingOut;
                    inst.FadeDuration = fadeOut;
                    inst.FadeTarget = 0f;
                }
                else
                {
                    inst.State = AudioState.Stopped;
                    OnStop?.Invoke(inst);
                }
            }
        }

        public void Pause(Guid instanceId)
        {
            if (_instances.TryGetValue(instanceId, out var inst))
            {
                inst.State = AudioState.Paused;
                OnPause?.Invoke(inst);
            }
        }

        public void Resume(Guid instanceId)
        {
            if (_instances.TryGetValue(instanceId, out var inst) && inst.State == AudioState.Paused)
                inst.State = AudioState.Playing;
        }

        public void StopAll(AudioBus? bus = null)
        {
            var list = new List<AudioInstance>(_instances.Values);
            foreach (var inst in list)
            {
                if (!bus.HasValue || inst.Bus == bus.Value)
                    inst.State = AudioState.Stopped;
            }
        }

        public void PauseAll(AudioBus? bus = null)
        {
            foreach (var inst in _instances.Values)
                if (!bus.HasValue || inst.Bus == bus.Value)
                    inst.State = AudioState.Paused;
        }

        public void ResumeAll(AudioBus? bus = null)
        {
            foreach (var inst in _instances.Values)
                if (inst.State == AudioState.Paused && (!bus.HasValue || inst.Bus == bus.Value))
                    inst.State = AudioState.Playing;
        }

        public void SetBusVolume(AudioBus bus, float volume)
        {
            if (_buses.TryGetValue(bus, out var state))
            {
                state.Volume = Math.Clamp(volume, 0f, 2f);
                OnBusVolumeChanged?.Invoke(bus, state.Volume);
            }
        }

        public void SetBusMuted(AudioBus bus, bool muted)
        {
            if (_buses.TryGetValue(bus, out var state)) state.Muted = muted;
        }

        public float GetBusVolume(AudioBus bus)
        {
            return _buses.TryGetValue(bus, out var state) ? state.Volume : 1f;
        }

        public void SetListenerPosition(Vector3 position, Quaternion? rotation = null)
        {
            if (_listeners.Count > 0)
            {
                _listeners[0].Position = position;
                if (rotation.HasValue) _listeners[0].Rotation = rotation.Value;
            }
        }

        public void UpdateInstancePosition(Guid instanceId, Vector3 position)
        {
            if (_instances.TryGetValue(instanceId, out var inst)) inst.Position = position;
        }

        public void Update(float dt)
        {
            if (!_isEnabled || _isPaused) return;

            var toRemove = new List<Guid>();
            foreach (var kvp in _instances)
            {
                var inst = kvp.Value;
                if (inst.Clip == null) continue;

                if (inst.State == AudioState.Playing)
                {
                    inst.PlaybackTime += dt * inst.Pitch;
                    if (!inst.Loop && inst.PlaybackTime >= inst.Clip.Duration)
                    {
                        inst.State = AudioState.Stopped;
                        OnStop?.Invoke(inst);
                    }
                }
                else if (inst.State == AudioState.FadingIn)
                {
                    inst.PlaybackTime += dt * inst.Pitch;
                    inst.Volume = MathF.Min(inst.Volume + dt / MathF.Max(0.001f, inst.FadeDuration) * inst.FadeTarget, inst.FadeTarget);
                    if (inst.Volume >= inst.FadeTarget - 0.001f) inst.State = AudioState.Playing;
                }
                else if (inst.State == AudioState.FadingOut)
                {
                    inst.Volume = MathF.Max(0, inst.Volume - dt / MathF.Max(0.001f, inst.FadeDuration));
                    if (inst.Volume <= 0.001f)
                    {
                        inst.State = AudioState.Stopped;
                        OnStop?.Invoke(inst);
                    }
                }

                // Calculate 3D volume
                if (inst.Is3D && _listeners.Count > 0)
                {
                    var listener = _listeners[0];
                    float dist = Vector3.Distance(inst.Position, listener.Position);
                    if (dist < inst.MinDistance) inst.Volume = inst.Volume;
                    else if (dist > inst.MaxDistance) inst.Volume *= 0.01f;
                    else
                    {
                        float t = (dist - inst.MinDistance) / (inst.MaxDistance - inst.MinDistance);
                        inst.Volume *= 1f - t * inst.RolloffFactor;
                    }
                }

                if (inst.State == AudioState.Stopped) toRemove.Add(kvp.Key);
            }

            foreach (var id in toRemove)
            {
                var inst = _instances[id];
                if (_buses.TryGetValue(inst.Bus, out var bus)) bus.Instances.Remove(inst);
                _instances.Remove(id);
            }

            // Peak meter
            foreach (var bus in _buses.Values)
            {
                bus.PeakLevel = 0f;
                bus.RMSLevel = 0f;
                foreach (var inst in bus.Instances)
                {
                    if (inst.State == AudioState.Playing || inst.State == AudioState.FadingIn || inst.State == AudioState.FadingOut)
                    {
                        float effective = inst.Volume * bus.Volume * _masterVolume * 0.5f;
                        float wave = MathF.Abs(MathF.Sin(inst.PlaybackTime * 440f * MathF.PI * 2f)) * effective;
                        if (wave > bus.PeakLevel) bus.PeakLevel = wave;
                        bus.RMSLevel += effective * effective;
                    }
                }
                if (bus.Instances.Count > 0) bus.RMSLevel = MathF.Sqrt(bus.RMSLevel / bus.Instances.Count);
            }
        }

        private void CleanupStopped()
        {
            var ids = new List<Guid>();
            foreach (var kvp in _instances)
                if (kvp.Value.State == AudioState.Stopped) ids.Add(kvp.Key);
            foreach (var id in ids) _instances.Remove(id);
        }

        public int GetActiveInstanceCount()
        {
            int count = 0;
            foreach (var inst in _instances.Values)
                if (inst.State == AudioState.Playing || inst.State == AudioState.FadingIn || inst.State == AudioState.FadingOut)
                    count++;
            return count;
        }

        public float GetEffectiveVolume(AudioInstance inst)
        {
            return inst.Volume * GetBusVolume(inst.Bus) * _masterVolume;
        }

        public List<AudioBusState> GetBusStates()
        {
            return new List<AudioBusState>(_buses.Values);
        }

        public void Clear()
        {
            _instances.Clear();
            foreach (var bus in _buses.Values) bus.Instances.Clear();
        }
    }
}
