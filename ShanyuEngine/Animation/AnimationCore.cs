using System; using System.Collections.Generic; using System.Numerics; using System.Runtime.InteropServices; namespace ShanyuEngine.Animation;

public enum IKMethod { FABRIK, CCD, TwoBone, LookAt, SplineIK }
public enum AnimationBlendMode { Override, Additive, Masked }
public enum RootMotionMode { Ignore, Apply, Extract }

public struct BoneWeight
{
    public int Index0, Index1, Index2, Index3;
    public float Weight0, Weight1, Weight2, Weight3;
    public void Normalize() { float s = Weight0+Weight1+Weight2+Weight3; if (s > 0) { Weight0/=s; Weight1/=s; Weight2/=s; Weight3/=s; } }
}

public sealed class RigPose
{
    public Vector3[] Positions; public Quaternion[] Rotations; public Vector3[] Scales;
    public float[] BoneLengths; public int[] ParentIndices;
    public int BoneCount => Positions?.Length ?? 0;

    public void SetToBindPose(Vector3[] bindPosePos, Quaternion[] bindPoseRot)
    {
        for (int i = 0; i < BoneCount; i++) { Positions[i] = bindPosePos[i]; Rotations[i] = bindPoseRot[i]; Scales[i] = Vector3.One; }
    }

    public void BlendTo(RigPose target, float alpha)
    {
        alpha = Math.Clamp(alpha, 0, 1);
        int count = Math.Min(BoneCount, target.BoneCount);
        for (int i = 0; i < count; i++)
        {
            Positions[i] = Vector3.Lerp(Positions[i], target.Positions[i], alpha);
            Rotations[i] = Quaternion.Slerp(Rotations[i], target.Rotations[i], alpha);
        }
    }

    public void ApplyAdditive(RigPose additive)
    {
        int count = Math.Min(BoneCount, additive.BoneCount);
        for (int i = 0; i < count; i++)
        {
            Positions[i] += additive.Positions[i];
            Rotations[i] *= additive.Rotations[i];
        }
    }

    public Matrix4x4[] BuildSkinMatrices(Matrix4x4[] bindTransformInv)
    {
        var world = ComputeWorldMatrices();
        int count = Math.Min(world.Length, bindTransformInv.Length);
        var skin = new Matrix4x4[count];
        for (int i = 0; i < count; i++) skin[i] = world[i] * bindTransformInv[i];
        return skin;
    }

    public Matrix4x4[] ComputeWorldMatrices()
    {
        int n = BoneCount; var world = new Matrix4x4[n];
        for (int i = 0; i < n; i++)
        {
            world[i] = Matrix4x4.CreateScale(Scales[i]) * Matrix4x4.CreateFromQuaternion(Rotations[i]) * Matrix4x4.CreateTranslation(Positions[i]);
            if (ParentIndices[i] >= 0) world[i] *= world[ParentIndices[i]];
        }
        return world;
    }
}

public sealed class AnimationClipCore
{
    public string Name; public float Duration; public float FrameRate = 30;
    public bool Loop; public float Speed = 1f;
    public Dictionary<int, Vector3[]> PositionKeys = new();
    public Dictionary<int, Quaternion[]> RotationKeys = new();
    public Dictionary<int, Vector3[]> ScaleKeys = new();
    public bool HasEvents; public List<AnimEvent> Events = new();

    public (Vector3 pos, Quaternion rot, Vector3 scale) Sample(int boneIndex, float time, bool loop = true)
    {
        float t = loop ? time % Duration : Math.Clamp(time, 0, Duration);
        float frac = t / Duration;
        int frameCount = (int)(Duration * FrameRate);
        float frameFrac = frac * frameCount;
        int frameA = (int)frameFrac % Math.Max(frameCount, 1);
        int frameB = (frameA + 1) % Math.Max(frameCount, 1);
        float blend = frameFrac - (int)frameFrac;

        Vector3 pos = PositionKeys.TryGetValue(boneIndex, out var pk) && pk.Length > 0
            ? Vector3.Lerp(pk[Math.Min(frameA, pk.Length-1)], pk[Math.Min(frameB, pk.Length-1)], blend)
            : Vector3.Zero;
        Quaternion rot = RotationKeys.TryGetValue(boneIndex, out var rk) && rk.Length > 0
            ? Quaternion.Slerp(rk[Math.Min(frameA, rk.Length-1)], rk[Math.Min(frameB, rk.Length-1)], blend)
            : Quaternion.Identity;
        Vector3 scale = ScaleKeys.TryGetValue(boneIndex, out var sk) && sk.Length > 0
            ? Vector3.Lerp(sk[Math.Min(frameA, sk.Length-1)], sk[Math.Min(frameB, sk.Length-1)], blend)
            : Vector3.One;
        return (pos, rot, scale);
    }

    public AnimEvent[] GetEventsAtTime(float time, float prevTime)
    {
        var triggered = new List<AnimEvent>();
        foreach (var e in Events)
        {
            if (e.Time >= prevTime && e.Time < time) triggered.Add(e);
            else if (Loop && e.Time >= prevTime - Duration && e.Time < time - Duration) triggered.Add(e);
        }
        return triggered.ToArray();
    }

    public void SamplePose(RigPose pose, float time, bool loop = true)
    {
        for (int i = 0; i < pose.BoneCount; i++)
        {
            var (p, r, s) = Sample(i, time, loop);
            if (PositionKeys.ContainsKey(i)) pose.Positions[i] = p;
            if (RotationKeys.ContainsKey(i)) pose.Rotations[i] = r;
            if (ScaleKeys.ContainsKey(i)) pose.Scales[i] = s;
        }
    }

    public static AnimationClipCore CreateFromDiff(AnimationClipCore a, AnimationClipCore b, float blendWeight)
    {
        var clip = new AnimationClipCore { Name = $"{a.Name}_Blend_{b.Name}", Duration = a.Duration, FrameRate = a.FrameRate, Loop = a.Loop };
        foreach (var kv in a.PositionKeys)
        {
            var bHas = b.PositionKeys.TryGetValue(kv.Key, out var bKeys);
            if (!bHas) continue;
            int frames = Math.Min(kv.Value.Length, bKeys?.Length ?? 0);
            var blended = new Vector3[frames];
            for (int i = 0; i < frames; i++) blended[i] = Vector3.Lerp(kv.Value[i], (bKeys ?? kv.Value)[Math.Min(i, (bKeys?.Length ?? 1)-1)], blendWeight);
            clip.PositionKeys[kv.Key] = blended;
        }
        return clip;
    }
}

public struct AnimEvent
{
    public string Name, Parameter; public float Time;
    public object? Data;
}



public struct AnimNotify
{
    public string Name; public float Time;
    public Action<object>? Callback;
}

public sealed class AnimStateMachine
{
    public string Name = "Default";
    private readonly Dictionary<string, AnimState> _states = new();
    private AnimState? _current, _previous;
    private float _transitionTime, _transitionDuration;
    public AnimState? Current => _current;
    public float CurrentTime { get; private set; }

    public event Action<string, string>? OnStateEnter, OnStateExit;

    public AnimState AddState(string name, AnimationClipCore? clip = null)
    {
        var s = new AnimState { Name = name, Clip = clip };
        _states[name] = s; return s;
    }

    public void AddTransition(string from, string to, Func<AnimStateMachine, bool> condition, float duration = 0.2f)
    {
        if (!_states.TryGetValue(from, out var fs)) return;
        fs.Transitions.Add(new AnimTransition { FromState = from, ToState = to, Condition = condition, Duration = duration });
    }

    public void SetState(string name, bool force = false)
    {
        if (!_states.ContainsKey(name)) return;
        if (!force && _current?.Name == name) return;
        _previous = _current;
        _current = _states[name];
        _transitionTime = 0;
        _transitionDuration = _previous != null ? 0.2f : 0;
        CurrentTime = 0;
        OnStateEnter?.Invoke(name, _previous?.Name ?? "");
    }

    public void Tick(float dt)
    {
        if (_current == null) return;

        _transitionTime += dt;
        if (_current.Clip != null) CurrentTime += dt * _current.Speed;

        foreach (var t in _current.Transitions)
        {
            if (t.Condition != null && t.Condition(this))
            {
                if (t.InverseCondition) continue;
                SetState(t.ToState); break;
            }
        }

        foreach (var n in _current.Notifies)
            if (CurrentTime >= n.Time && CurrentTime - dt < n.Time) n.Callback?.Invoke(null);
    }

    public void SamplePose(RigPose pose, float deltaTime)
    {
        if (_current?.Clip != null) _current.Clip.SamplePose(pose, CurrentTime, _current.Loop);
        if (_previous != null && _transitionDuration > 0)
        {
            float alpha = Math.Clamp(_transitionTime / _transitionDuration, 0, 1);
            var prevPose = new RigPose { Positions = (Vector3[])pose.Positions.Clone(), Rotations = (Quaternion[])pose.Rotations.Clone(), Scales = (Vector3[])pose.Scales.Clone() };
            if (_previous.Clip != null) _previous.Clip.SamplePose(prevPose, CurrentTime, _previous.Loop);
            for (int i = 0; i < pose.BoneCount; i++)
            {
                pose.Positions[i] = Vector3.Lerp(prevPose.Positions[i], pose.Positions[i], alpha);
                pose.Rotations[i] = Quaternion.Slerp(prevPose.Rotations[i], pose.Rotations[i], alpha);
            }
            if (alpha >= 1f) _previous = null;
        }
    }

    public bool IsInState(string name) => _current?.Name == name;
    public bool WasInState(string name) => _previous?.Name == name;
    public float GetNormalizedTime() => _current?.Clip != null ? CurrentTime / MathF.Max(_current.Clip.Duration, 0.001f) : 0;
    public bool HasFinished() => _current?.Clip != null && CurrentTime >= _current.Clip.Duration && !_current.Loop;
}

