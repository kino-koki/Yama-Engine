using System;
using System.Collections.Generic;
using System.Linq;

namespace ShanyuEngine.UI
{
    public readonly struct AdvancedUIRect
    {
        public readonly float X;
        public readonly float Y;
        public readonly float Width;
        public readonly float Height;

        public AdvancedUIRect(float x, float y, float width, float height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }
    }

    public sealed class AdvancedUILayoutSolver
    {
        private readonly float _width;
        private readonly float _height;
        private readonly int _columns;
        private readonly int _rows;
        private readonly float[] _columnWeights;
        private readonly float[] _rowWeights;
        private float _left;
        private float _top;
        private float _right;
        private float _bottom;
        private float _gapX;
        private float _gapY;

        private AdvancedUILayoutSolver(float width, float height, int columns, int rows)
        {
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
            if (columns <= 0) throw new ArgumentOutOfRangeException(nameof(columns));
            if (rows <= 0) throw new ArgumentOutOfRangeException(nameof(rows));
            _width = width;
            _height = height;
            _columns = columns;
            _rows = rows;
            _columnWeights = Enumerable.Repeat(1f, columns).ToArray();
            _rowWeights = Enumerable.Repeat(1f, rows).ToArray();
        }

        public static AdvancedUILayoutSolver CreateGrid(float width, float height, int columns, int rows)
        {
            return new AdvancedUILayoutSolver(width, height, columns, rows);
        }

        public AdvancedUILayoutSolver WithPadding(float left, float top, float right, float bottom)
        {
            _left = Math.Max(0, left);
            _top = Math.Max(0, top);
            _right = Math.Max(0, right);
            _bottom = Math.Max(0, bottom);
            return this;
        }

        public AdvancedUILayoutSolver WithGap(float x, float y)
        {
            _gapX = Math.Max(0, x);
            _gapY = Math.Max(0, y);
            return this;
        }

        public AdvancedUILayoutSolver WithColumnWeight(int column, float weight)
        {
            _columnWeights[column] = Math.Max(0.0001f, weight);
            return this;
        }

        public AdvancedUILayoutSolver WithRowWeight(int row, float weight)
        {
            _rowWeights[row] = Math.Max(0.0001f, weight);
            return this;
        }

        public IReadOnlyList<AdvancedUIRect> Solve()
        {
            var result = new List<AdvancedUIRect>(_columns * _rows);
            var availableWidth = Math.Max(0, _width - _left - _right - _gapX * (_columns - 1));
            var availableHeight = Math.Max(0, _height - _top - _bottom - _gapY * (_rows - 1));
            var columnTotal = _columnWeights.Sum();
            var rowTotal = _rowWeights.Sum();

            var y = _top;
            for (var row = 0; row < _rows; row++)
            {
                var h = availableHeight * _rowWeights[row] / rowTotal;
                var x = _left;
                for (var column = 0; column < _columns; column++)
                {
                    var w = availableWidth * _columnWeights[column] / columnTotal;
                    result.Add(new AdvancedUIRect(x, y, w, h));
                    x += w + _gapX;
                }
                y += h + _gapY;
            }
            return result;
        }
    }

    public enum UIInteractionState
    {
        Normal,
        Hovered,
        Pressed,
        Disabled,
        Focused
    }

    public sealed class AdvancedStyleSheet
    {
        private sealed class StyleRule
        {
            public string Selector = "";
            public Dictionary<string, string> Declarations = new();
            public int Specificity;
            public UIInteractionState? State;
        }

        private readonly Dictionary<string, string> _variables = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<StyleRule> _rules = new();

        public AdvancedStyleSheet SetVariable(string name, string value)
        {
            _variables[name] = value;
            return this;
        }

        public AdvancedStyleSheet AddRule(string selector, Dictionary<string, string> declarations)
        {
            var state = ParseState(selector);
            var baseSelector = selector.Split(':')[0];
            _rules.Add(new StyleRule
            {
                Selector = baseSelector,
                Declarations = new Dictionary<string, string>(declarations, StringComparer.OrdinalIgnoreCase),
                Specificity = baseSelector.StartsWith(".", StringComparison.Ordinal) ? 20 : 10,
                State = state
            });
            return this;
        }

        public Dictionary<string, string> Resolve(string element, IEnumerable<string> classes, UIInteractionState state)
        {
            var classSet = new HashSet<string>(classes ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var resolved = new Dictionary<string, (string Value, int Specificity, int Order)>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < _rules.Count; i++)
            {
                var rule = _rules[i];
                if (rule.State.HasValue && rule.State.Value != state) continue;
                if (!Matches(rule.Selector, element, classSet)) continue;
                foreach (var pair in rule.Declarations)
                {
                    var value = ExpandVariables(pair.Value);
                    if (!resolved.TryGetValue(pair.Key, out var previous) ||
                        rule.Specificity > previous.Specificity ||
                        (rule.Specificity == previous.Specificity && i >= previous.Order))
                    {
                        resolved[pair.Key] = (value, rule.Specificity, i);
                    }
                }
            }
            return resolved.ToDictionary(p => p.Key, p => p.Value.Value, StringComparer.OrdinalIgnoreCase);
        }

        private bool Matches(string selector, string element, HashSet<string> classes)
        {
            if (selector.StartsWith(".", StringComparison.Ordinal))
                return classes.Contains(selector[1..]);
            return string.Equals(selector, element, StringComparison.OrdinalIgnoreCase);
        }

        private string ExpandVariables(string value)
        {
            if (!value.StartsWith("var(", StringComparison.Ordinal) || !value.EndsWith(")", StringComparison.Ordinal))
                return value;
            var name = value.Substring(4, value.Length - 5);
            return _variables.TryGetValue(name, out var resolved) ? resolved : value;
        }

        private static UIInteractionState? ParseState(string selector)
        {
            var parts = selector.Split(':');
            if (parts.Length != 2) return null;
            return parts[1].ToLowerInvariant() switch
            {
                "hover" or "hovered" => UIInteractionState.Hovered,
                "pressed" or "active" => UIInteractionState.Pressed,
                "disabled" => UIInteractionState.Disabled,
                "focus" or "focused" => UIInteractionState.Focused,
                _ => null
            };
        }
    }

    public enum UIEventPhase
    {
        Capture,
        Target,
        Bubble
    }

    public sealed class UIEvent
    {
        public string Type { get; }
        public string TargetId { get; }
        public bool IsPropagationStopped { get; private set; }
        public Dictionary<string, object> Data { get; } = new();

        public UIEvent(string type, string targetId)
        {
            Type = type;
            TargetId = targetId;
        }

        public void StopPropagation()
        {
            IsPropagationStopped = true;
        }
    }

    public sealed class UIEventTrace
    {
        public List<string> Nodes { get; } = new();
        public bool Stopped { get; internal set; }
    }

    public sealed class AdvancedUIEventRouter
    {
        private sealed class Node
        {
            public string Id = "";
            public string? ParentId;
        }

        private readonly Dictionary<string, Node> _nodes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<(string Id, UIEventPhase Phase), List<Action<UIEvent>>> _handlers = new();

        public void Register(string id, string? parentId)
        {
            _nodes[id] = new Node { Id = id, ParentId = parentId };
        }

        public void On(string id, UIEventPhase phase, Action<UIEvent> handler)
        {
            var key = (id, phase);
            if (!_handlers.TryGetValue(key, out var list))
                _handlers[key] = list = new List<Action<UIEvent>>();
            list.Add(handler);
        }

        public UIEventTrace Route(UIEvent ev)
        {
            var trace = new UIEventTrace();
            var path = BuildPath(ev.TargetId);
            foreach (var id in path.Take(path.Count - 1))
            {
                Invoke(id, UIEventPhase.Capture, ev, trace);
                if (ev.IsPropagationStopped) return Stop(trace);
            }

            Invoke(ev.TargetId, UIEventPhase.Target, ev, trace);
            if (ev.IsPropagationStopped) return Stop(trace);

            for (var i = path.Count - 2; i >= 0; i--)
            {
                Invoke(path[i], UIEventPhase.Bubble, ev, trace);
                if (ev.IsPropagationStopped) return Stop(trace);
            }
            return trace;
        }

        private List<string> BuildPath(string target)
        {
            var path = new List<string>();
            string? current = target;
            while (current != null && _nodes.TryGetValue(current, out var node))
            {
                path.Add(current);
                current = node.ParentId;
            }
            path.Reverse();
            return path;
        }

        private void Invoke(string id, UIEventPhase phase, UIEvent ev, UIEventTrace trace)
        {
            if (!_handlers.TryGetValue((id, phase), out var handlers)) return;
            foreach (var handler in handlers)
            {
                trace.Nodes.Add(id + ":" + phase);
                handler(ev);
                if (ev.IsPropagationStopped) break;
            }
        }

        private static UIEventTrace Stop(UIEventTrace trace)
        {
            trace.Stopped = true;
            return trace;
        }
    }

    public enum UIEasingKind
    {
        Linear,
        SmoothStep,
        EaseIn,
        EaseOut,
        EaseInOut
    }

    public sealed class AdvancedUIAnimationTimeline
    {
        private sealed class Key
        {
            public double Time;
            public float Value;
            public UIEasingKind Ease;
        }

        private readonly bool _loop;
        private readonly Dictionary<string, List<Key>> _tracks = new(StringComparer.OrdinalIgnoreCase);

        public AdvancedUIAnimationTimeline(bool loop = false)
        {
            _loop = loop;
        }

        public AdvancedUIAnimationTimeline AddFloatTrack(string property, double time, float value, UIEasingKind easing = UIEasingKind.Linear)
        {
            if (!_tracks.TryGetValue(property, out var keys))
                _tracks[property] = keys = new List<Key>();
            keys.Add(new Key { Time = time, Value = value, Ease = easing });
            keys.Sort((a, b) => a.Time.CompareTo(b.Time));
            return this;
        }

        public float SampleFloat(string property, double time)
        {
            if (!_tracks.TryGetValue(property, out var keys) || keys.Count == 0) return 0;
            var duration = keys[^1].Time;
            if (_loop && duration > 0)
                time %= duration;
            if (time <= keys[0].Time) return keys[0].Value;
            if (time >= keys[^1].Time) return keys[^1].Value;
            for (var i = 0; i < keys.Count - 1; i++)
            {
                var a = keys[i];
                var b = keys[i + 1];
                if (time < a.Time || time > b.Time) continue;
                var t = (float)((time - a.Time) / Math.Max(0.000001, b.Time - a.Time));
                t = ApplyEase(t, b.Ease);
                return a.Value + (b.Value - a.Value) * t;
            }
            return keys[^1].Value;
        }

        private static float ApplyEase(float t, UIEasingKind ease)
        {
            t = Math.Clamp(t, 0f, 1f);
            return ease switch
            {
                UIEasingKind.SmoothStep => t * t * (3f - 2f * t),
                UIEasingKind.EaseIn => t * t,
                UIEasingKind.EaseOut => 1f - (1f - t) * (1f - t),
                UIEasingKind.EaseInOut => t < 0.5f ? 2f * t * t : 1f - MathF.Pow(-2f * t + 2f, 2f) * 0.5f,
                _ => t
            };
        }
    }
}
