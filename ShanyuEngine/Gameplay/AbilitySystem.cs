using System; using System.Collections.Generic; using System.Numerics; namespace ShanyuEngine.Gameplay;
using ShanyuEngine.Gameplay.Effects;

public enum AbilityInput { None, Confirm, Cancel, Skill1, Skill2, Skill3, Skill4, Ultimate, Dodge, Jump, Sprint, Crouch, Interact, Reload, Use, Aim, Shoot, Melee, Block, Parry, Throw, Special }
public enum AbilityState { Inactive, Active, Cooldown, Channeling, Canceled, Finished }
public enum EffectType { Instant, Duration, Periodic, Infinite }
public enum DamageType { Physical, Magic, Fire, Ice, Lightning, Holy, Dark, Poison, Bleed, True }






public sealed class GameplayEffectManager
{
    private readonly List<ActiveEffect> _effects = new();
    public IReadOnlyList<ActiveEffect> Effects => _effects;
    public event Action<GameplayEffectSpec, bool>? OnEffectApplied, OnEffectRemoved;

    public void ApplyEffect(GameplayEffectSpec spec, GameplayTagContainer tags, int sourceLevel = 1)
    {
        if (spec.RequiredTags.Count > 0) foreach (var t in spec.RequiredTags) if (!tags.HasTag(t)) return;
        if (spec.BlockedTags.Count > 0) foreach (var t in spec.BlockedTags) if (tags.HasTag(t)) return;

        foreach (var ae in _effects)
        {
            if (ae.Spec.Name == spec.Name)
            {
                if (!spec.Stackable) return;
                var updated = ae;
                if (updated.Stacks < spec.MaxStacks)
                { updated.Stacks++; updated.RemainingTime = spec.Duration; _effects[_effects.IndexOf(ae)] = updated; }
                return;
            }
        }

        _effects.Add(new ActiveEffect
        {
            Spec = spec, Stacks = 1, RemainingTime = spec.Duration,
            PeriodTimer = spec.Period, SourceLevel = sourceLevel
        });
        foreach (var t in spec.GrantedTags) tags.AddTag(t.Tag);
        OnEffectApplied?.Invoke(spec, true);
    }

    public void Tick(float dt, GameplayTagContainer tags, ref Dictionary<string, float> stats)
    {
        for (int i = _effects.Count - 1; i >= 0; i--)
        {
            var e = _effects[i];
            if (e.Paused) continue;

            if (e.Spec.Type == EffectType.Instant && e.RemainingTime <= 0)
            {
                foreach (var kv in e.Spec.Modifiers) { if (stats.ContainsKey(kv.Key)) stats[kv.Key] += kv.Value * e.Stacks; }
                _effects.RemoveAt(i); OnEffectRemoved?.Invoke(e.Spec, true); continue;
            }

            if (e.Spec.Type == EffectType.Duration || e.Spec.Type == EffectType.Periodic)
            {
                e.RemainingTime -= dt;
                if (e.Spec.Type == EffectType.Periodic)
                {
                    e.PeriodTimer -= dt;
                    if (e.PeriodTimer <= 0)
                    {
                        e.PeriodTimer = e.Spec.Period;
                        foreach (var kv in e.Spec.Modifiers) { if (stats.ContainsKey(kv.Key)) stats[kv.Key] += kv.Value * e.Stacks; }
                    }
                }
                if (e.RemainingTime <= 0)
                {
                    foreach (var t in e.Spec.GrantedTags) tags.RemoveTag(t.Tag);
                    _effects.RemoveAt(i); OnEffectRemoved?.Invoke(e.Spec, true);
                }
                else _effects[i] = e;
            }
        }
    }

    public void RemoveAll() { _effects.Clear(); }
    public void Remove(string name) { _effects.RemoveAll(e => e.Spec.Name == name); }
    public bool HasEffect(string name) => _effects.Exists(e => e.Spec.Name == name);
    public int GetStackCount(string name) { var e = _effects.Find(e => e.Spec.Name == name); return e.Stacks; }
}


