using System.Collections.Generic;
using ShanyuEngine.Core;

namespace ShanyuEngine.Animation;

/// <summary>
/// Lightweight tween engine for value interpolation over time.
/// Supports easing functions, looping, ping-pong, chaining, and custom curves.
/// Designed for UI animations, camera shakes, material transitions, and gameplay feedback.
/// </summary>
public static class TweenEngine
{
    private static readonly List<ITween> _activeTweens = new();
    private static readonly List<ITween> _pendingAdd = new();
    private static readonly List<ITween> _pendingRemove = new();
    private static int _nextId = 1;
    private static bool _updating;

    /// <summary>Total active tweens.</summary>
    public static int ActiveCount => _activeTweens.Count;

    /// <summary>Update all active tweens. Call from main loop.</summary>
    public static void Update(double deltaTime)
    {
        _updating = true;

        // Add pending
        if (_pendingAdd.Count > 0)
        {
            _activeTweens.AddRange(_pendingAdd);
            _pendingAdd.Clear();
        }

        float dt = (float)deltaTime;

        foreach (var tween in _activeTweens)
        {
            if (tween.Paused) continue;
            tween.Advance(dt);
            tween.Invoke();

            if (tween.Completed && tween.LoopCount > 0)
            {
                tween.Reset();
                tween.LoopCount--;
                if (tween.LoopCount > 0 || tween.LoopCount == -1)
                {
                    tween.Completed = false;
                    if (tween.YoYo)
                        tween.Reverse = !tween.Reverse;
                }
            }

            if (tween.Completed)
            {
                tween.OnComplete?.Invoke();
                _pendingRemove.Add(tween);
            }
        }

        // Cleanup completed
        if (_pendingRemove.Count > 0)
        {
            foreach (var t in _pendingRemove)
                _activeTweens.Remove(t);
            _pendingRemove.Clear();
        }

        _updating = false;
    }

    /// <summary>Kill all active tweens.</summary>
    public static void KillAll()
    {
        _activeTweens.Clear();
        _pendingAdd.Clear();
        _pendingRemove.Clear();
    }

    /// <summary>Kill tweens matching a predicate.</summary>
    public static void KillWhere(Func<ITween, bool> predicate)
    {
        _activeTweens.RemoveAll(t => { if (predicate(t)) { _pendingAdd.Remove(t); return true; } return false; });
    }

    #region Float Tween

    public static ITween To(float from, float to, float duration, Action<float> onUpdate)
    {
        return Register(new FloatTween
        {
            From = from, To = to, Duration = duration,
            OnUpdate = onUpdate, Id = _nextId++
        });
    }

    public static ITween FromTo(float from, float to, float duration, Action<float> onUpdate) => To(from, to, duration, onUpdate);

    #endregion

    #region Vector2/3/4 Tween

    public static ITween To(System.Numerics.Vector2 from, System.Numerics.Vector2 to, float duration, Action<System.Numerics.Vector2> onUpdate)
    {
        return Register(new Vector2Tween
        {
            From = from, To = to, Duration = duration,
            OnUpdate = onUpdate, Id = _nextId++
        });
    }

    public static ITween To(System.Numerics.Vector3 from, System.Numerics.Vector3 to, float duration, Action<System.Numerics.Vector3> onUpdate)
    {
        return Register(new Vector3Tween
        {
            From = from, To = to, Duration = duration,
            OnUpdate = onUpdate, Id = _nextId++
        });
    }

    public static ITween To(System.Numerics.Vector4 from, System.Numerics.Vector4 to, float duration, Action<System.Numerics.Vector4> onUpdate)
    {
        return Register(new Vector4Tween
        {
            From = from, To = to, Duration = duration,
            OnUpdate = onUpdate, Id = _nextId++
        });
    }

    #endregion

    #region Color Tween (packed as Vector4)

    public static ITween Color(System.Numerics.Vector4 from, System.Numerics.Vector4 to, float duration, Action<System.Numerics.Vector4> onUpdate)
    {
        return To(from, to, duration, onUpdate);
    }

    #endregion

    #region Delay

    public static ITween Delay(float seconds, Action onComplete)
    {
        return Register(new FloatTween
        {
            From = 0, To = 1, Duration = seconds,
            OnUpdate = _ => { },
            OnComplete = onComplete,
            Id = _nextId++
        });
    }

    #endregion

    #region Internal

    private static ITween Register(ITween tween)
    {
        if (_updating)
            _pendingAdd.Add(tween);
        else
            _activeTweens.Add(tween);
        return tween;
    }

    #endregion
}

#region Easing Functions

public static class Easing
{
    public static float Linear(float t) => t;
    public static float QuadIn(float t) => t * t;
    public static float QuadOut(float t) => t * (2 - t);
    public static float QuadInOut(float t) => t < 0.5f ? 2 * t * t : -1 + (4 - 2 * t) * t;
    public static float CubicIn(float t) => t * t * t;
    public static float CubicOut(float t) { t -= 1; return t * t * t + 1; }
    public static float CubicInOut(float t) => t < 0.5f ? 4 * t * t * t : (t - 1) * (2 * t - 2) * (2 * t - 2) + 1;
    public static float SineIn(float t) => 1 - (float)Math.Cos(t * Math.PI * 0.5f);
    public static float SineOut(float t) => (float)Math.Sin(t * Math.PI * 0.5f);
    public static float SineInOut(float t) => (float)(1 - Math.Cos(Math.PI * t)) * 0.5f;
    public static float ExpoIn(float t) => t <= 0 ? 0 : (float)Math.Pow(2, 10 * (t - 1));
    public static float ExpoOut(float t) => t >= 1 ? 1 : 1 - (float)Math.Pow(2, -10 * t);
    public static float BounceOut(float t)
    {
        if (t < 1 / 2.75f) return 7.5625f * t * t;
        if (t < 2 / 2.75f) { t -= 1.5f / 2.75f; return 7.5625f * t * t + 0.75f; }
        if (t < 2.5f / 2.75f) { t -= 2.25f / 2.75f; return 7.5625f * t * t + 0.9375f; }
        t -= 2.625f / 2.75f; return 7.5625f * t * t + 0.984375f;
    }
    public static float ElasticOut(float t)
    {
        if (t >= 1) return 1;
        float p = 0.3f;
        return (float)(Math.Pow(2, -10 * t) * Math.Sin((t - p / 4) * (2 * Math.PI) / p) + 1);
    }
    public static float Apply(string name, float t)
    {
        return name switch
        {
            "quadin" => QuadIn(t), "quadout" => QuadOut(t),
            "quadinout" => QuadInOut(t), "cubicin" => CubicIn(t),
            "cubicout" => CubicOut(t), "cubicinout" => CubicInOut(t),
            "sinein" => SineIn(t), "sineout" => SineOut(t),
            "sineinout" => SineInOut(t), "expoin" => ExpoIn(t),
            "expoout" => ExpoOut(t), "bounceout" => BounceOut(t),
            "elasticout" => ElasticOut(t),
            _ => t
        };
    }
}

#endregion

#region Interfaces & Types

public interface ITween
{
    int Id { get; }
    bool Paused { get; set; }
    bool Completed { get; set; }
    bool Reverse { get; set; }
    bool YoYo { get; set; }
    int LoopCount { get; set; }
    float Duration { get; }
    string Easing { get; set; }
    Action? OnComplete { get; set; }
    void Advance(float dt);
    void Invoke();
    void Reset();
}

internal abstract class BaseTween : ITween
{
    public int Id { get; set; }
    public bool Paused { get; set; }
    public bool Completed { get; set; }
    public bool Reverse { get; set; }
    public bool YoYo { get; set; }
    public int LoopCount { get; set; } = 1;
    public float Duration { get; set; } = 1f;
    public string Easing { get; set; } = "quadout";
    public Action? OnComplete { get; set; }
    public Action? OnStart { get; set; }
    public Action? OnCancel { get; set; }
    public float Delay { get; set; }
    public object? UserData { get; set; }

    protected float _elapsed;
    protected bool _started;
    protected float _delayRemaining;

    public virtual void Advance(float dt)
    {
        if (Completed) return;

        if (_delayRemaining > 0)
        {
            _delayRemaining -= dt;
            if (_delayRemaining > 0) return;
            dt = -_delayRemaining;
        }

        if (!_started)
        {
            _started = true;
            OnStart?.Invoke();
        }

        _elapsed += dt;
        if (_elapsed >= Duration)
        {
            _elapsed = Duration;
            Completed = true;
        }
    }

    public abstract void Invoke();

    public virtual void Reset()
    {
        _elapsed = 0;
        _started = false;
        _delayRemaining = Delay;
        Completed = false;
    }

    public void Cancel()
    {
        Completed = true;
        OnCancel?.Invoke();
    }
}

internal sealed class FloatTween : BaseTween
{
    public float From, To;
    public Action<float>? OnUpdate;

    public override void Invoke()
    {
        float t = _elapsed / Duration;
        float eased = Easing.Apply(Easing, t);
        float value = Reverse ? To + (From - To) * eased : From + (To - From) * eased;
        OnUpdate?.Invoke(value);
    }
}

internal sealed class Vector2Tween : BaseTween
{
    public System.Numerics.Vector2 From, To;
    public Action<System.Numerics.Vector2>? OnUpdate;

    public override void Invoke()
    {
        float t = _elapsed / Duration;
        float eased = Easing.Apply(Easing, t);
        var value = Reverse
            ? To + (From - To) * eased
            : From + (To - From) * eased;
        OnUpdate?.Invoke(value);
    }
}

internal sealed class Vector3Tween : BaseTween
{
    public System.Numerics.Vector3 From, To;
    public Action<System.Numerics.Vector3>? OnUpdate;

    public override void Invoke()
    {
        float t = _elapsed / Duration;
        float eased = Easing.Apply(Easing, t);
        var value = Reverse
            ? To + (From - To) * eased
            : From + (To - From) * eased;
        OnUpdate?.Invoke(value);
    }
}

internal sealed class Vector4Tween : BaseTween
{
    public System.Numerics.Vector4 From, To;
    public Action<System.Numerics.Vector4>? OnUpdate;

    public override void Invoke()
    {
        float t = _elapsed / Duration;
        float eased = Easing.Apply(Easing, t);
        var value = Reverse
            ? To + (From - To) * eased
            : From + (To - From) * eased;
        OnUpdate?.Invoke(value);
    }
}

#endregion
