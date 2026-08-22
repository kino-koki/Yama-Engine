using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using ImGuiNET;
using ShanyuEngine.Assets;
using ShanyuEngine.ECS;
using ShanyuEngine.Input;
using ShanyuEngine.RHI;

using LogLevel = ShanyuEngine.Config.LogLevel;
namespace ShanyuEngine.Editor
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - 编辑器 UI 框架 (EditorUI.cs)
    //
    // 本文件提供编辑器 UI 的基础框架:
    //   - 编辑器主题系统 (EditorTheme / EditorThemeManager)
    //   - 日志类型与控制台接口 (LogLevel / ConsoleLogEntry / IEditorConsole)
    //   - 停靠空间布局模型 (DockSpace / DockNode, 用于 JSON 布局序列化)
    //   - 可插拔面板接口 (IEditorPanel) 与面板基类 (EditorPanel)
    //   - 编辑器共享上下文 (EditorContext)
    //   - 控制台命令注册表 (CommandRegistry)
    //   - 数学扩展 (EditorMathExtensions)
    //
    // 具体面板实现位于 Panels/ 子目录; 主窗口编排位于 MainEditorWindow.cs。
    // ════════════════════════════════════════════════════════════════════════

    // ─────────────────────────── 主题系统 ───────────────────────────

    public enum EditorThemePreset
    {
        Dark,
        Light,
        Custom
    }

    public sealed class EditorTheme
    {
        public string Name = "Dark";
        public EditorThemePreset Preset = EditorThemePreset.Dark;

        public Vector4 Background = new(0.10f, 0.10f, 0.12f, 1.00f);
        public Vector4 Panel = new(0.16f, 0.16f, 0.19f, 1.00f);
        public Vector4 PanelHover = new(0.22f, 0.22f, 0.26f, 1.00f);
        public Vector4 PanelActive = new(0.26f, 0.26f, 0.30f, 1.00f);
        public Vector4 Text = new(0.90f, 0.90f, 0.92f, 1.00f);
        public Vector4 TextDim = new(0.55f, 0.55f, 0.60f, 1.00f);
        public Vector4 Accent = new(0.26f, 0.59f, 0.98f, 1.00f);
        public Vector4 AccentHover = new(0.35f, 0.66f, 1.00f, 1.00f);
        public Vector4 Border = new(0.25f, 0.25f, 0.28f, 0.60f);
        public Vector4 Separator = new(0.20f, 0.20f, 0.23f, 1.00f);
        public Vector4 Success = new(0.30f, 0.70f, 0.40f, 1.00f);
        public Vector4 Warning = new(0.95f, 0.75f, 0.20f, 1.00f);
        public Vector4 Error = new(0.90f, 0.30f, 0.30f, 1.00f);

        public void ApplyPreset(EditorThemePreset preset)
        {
            Preset = preset;
            switch (preset)
            {
                case EditorThemePreset.Dark:
                    Name = "Dark";
                    Background = new Vector4(0.10f, 0.10f, 0.12f, 1.00f);
                    Panel = new Vector4(0.16f, 0.16f, 0.19f, 1.00f);
                    PanelHover = new Vector4(0.22f, 0.22f, 0.26f, 1.00f);
                    PanelActive = new Vector4(0.26f, 0.26f, 0.30f, 1.00f);
                    Text = new Vector4(0.90f, 0.90f, 0.92f, 1.00f);
                    TextDim = new Vector4(0.55f, 0.55f, 0.60f, 1.00f);
                    Accent = new Vector4(0.26f, 0.59f, 0.98f, 1.00f);
                    AccentHover = new Vector4(0.35f, 0.66f, 1.00f, 1.00f);
                    break;
                case EditorThemePreset.Light:
                    Name = "Light";
                    Background = new Vector4(0.86f, 0.86f, 0.88f, 1.00f);
                    Panel = new Vector4(0.92f, 0.92f, 0.94f, 1.00f);
                    PanelHover = new Vector4(0.84f, 0.84f, 0.88f, 1.00f);
                    PanelActive = new Vector4(0.80f, 0.80f, 0.84f, 1.00f);
                    Text = new Vector4(0.12f, 0.12f, 0.14f, 1.00f);
                    TextDim = new Vector4(0.45f, 0.45f, 0.50f, 1.00f);
                    Accent = new Vector4(0.20f, 0.50f, 0.90f, 1.00f);
                    AccentHover = new Vector4(0.30f, 0.58f, 0.98f, 1.00f);
                    break;
                case EditorThemePreset.Custom:
                    Name = "Custom";
                    break;
            }
        }

        public void ApplyToImGuiStyle()
        {
            var style = ImGui.GetStyle();
            style.WindowRounding = 4.0f;
            style.ChildRounding = 4.0f;
            style.FrameRounding = 3.0f;
            style.PopupRounding = 3.0f;
            style.ScrollbarRounding = 6.0f;
            style.GrabRounding = 3.0f;
            style.TabRounding = 4.0f;
            style.WindowBorderSize = 1.0f;
            style.FrameBorderSize = 0.0f;
            style.WindowPadding = new Vector2(8, 8);
            style.FramePadding = new Vector2(6, 3);
            style.ItemSpacing = new Vector2(8, 4);
            style.ItemInnerSpacing = new Vector2(4, 4);

            var colors = style.Colors;
            colors[(int)ImGuiCol.WindowBg] = Background;
            colors[(int)ImGuiCol.ChildBg] = Panel;
            colors[(int)ImGuiCol.PopupBg] = Panel;
            colors[(int)ImGuiCol.Border] = Border;
            colors[(int)ImGuiCol.BorderShadow] = Vector4.Zero;
            colors[(int)ImGuiCol.Text] = Text;
            colors[(int)ImGuiCol.TextDisabled] = TextDim;
            colors[(int)ImGuiCol.FrameBg] = Panel;
            colors[(int)ImGuiCol.FrameBgHovered] = PanelHover;
            colors[(int)ImGuiCol.FrameBgActive] = PanelActive;
            colors[(int)ImGuiCol.TitleBg] = new Vector4(0.08f, 0.08f, 0.10f, 1.00f);
            colors[(int)ImGuiCol.TitleBgActive] = new Vector4(0.12f, 0.12f, 0.15f, 1.00f);
            colors[(int)ImGuiCol.TitleBgCollapsed] = new Vector4(0.06f, 0.06f, 0.08f, 1.00f);
            colors[(int)ImGuiCol.MenuBarBg] = new Vector4(0.14f, 0.14f, 0.17f, 1.00f);
            colors[(int)ImGuiCol.ScrollbarBg] = new Vector4(0.06f, 0.06f, 0.08f, 1.00f);
            colors[(int)ImGuiCol.ScrollbarGrab] = PanelActive;
            colors[(int)ImGuiCol.ScrollbarGrabHovered] = PanelHover;
            colors[(int)ImGuiCol.ScrollbarGrabActive] = Accent;
            colors[(int)ImGuiCol.CheckMark] = Accent;
            colors[(int)ImGuiCol.SliderGrab] = Accent;
            colors[(int)ImGuiCol.SliderGrabActive] = AccentHover;
            colors[(int)ImGuiCol.Button] = Panel;
            colors[(int)ImGuiCol.ButtonHovered] = PanelHover;
            colors[(int)ImGuiCol.ButtonActive] = PanelActive;
            colors[(int)ImGuiCol.Header] = PanelHover;
            colors[(int)ImGuiCol.HeaderHovered] = PanelActive;
            colors[(int)ImGuiCol.HeaderActive] = Accent;
            colors[(int)ImGuiCol.Separator] = Separator;
            colors[(int)ImGuiCol.SeparatorHovered] = PanelHover;
            colors[(int)ImGuiCol.SeparatorActive] = Accent;
            colors[(int)ImGuiCol.ResizeGrip] = PanelActive;
            colors[(int)ImGuiCol.ResizeGripHovered] = Accent;
            colors[(int)ImGuiCol.ResizeGripActive] = AccentHover;
            colors[(int)ImGuiCol.Tab] = PanelActive;
            colors[(int)ImGuiCol.TabHovered] = Accent;
            colors[(int)ImGuiCol.TabActive] = AccentHover;
            colors[(int)ImGuiCol.TabUnfocused] = new Vector4(0.12f, 0.12f, 0.15f, 1.00f);
            colors[(int)ImGuiCol.TabUnfocusedActive] = PanelActive;
            colors[(int)ImGuiCol.DockingPreview] = Accent;
            colors[(int)ImGuiCol.DockingEmptyBg] = new Vector4(0.06f, 0.06f, 0.08f, 1.00f);
        }

        public string SaveToJson()
        {
            var opts = new JsonSerializerOptions { WriteIndented = true };
            return JsonSerializer.Serialize(this, opts);
        }

        public static EditorTheme LoadFromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonSerializer.Deserialize<EditorTheme>(json); }
            catch { return null; }
        }

        public void SaveToFile(string path)
        {
            try { File.WriteAllText(path, SaveToJson()); }
            catch (Exception) { /* 忽略 */ }
        }

        public static EditorTheme LoadFromFile(string path)
        {
            try { return LoadFromJson(File.ReadAllText(path)); }
            catch (Exception) { return null; }
        }
    }

    public sealed class EditorThemeManager
    {
        private readonly Dictionary<string, EditorTheme> _themes = new();
        public EditorTheme Current { get; private set; }

        public EditorThemeManager()
        {
            var dark = new EditorTheme();
            dark.ApplyPreset(EditorThemePreset.Dark);
            Register("Dark", dark);

            var light = new EditorTheme();
            light.ApplyPreset(EditorThemePreset.Light);
            Register("Light", light);

            Current = dark;
        }

        public void Register(string name, EditorTheme theme)
        {
            if (theme == null) return;
            theme.Name = name;
            _themes[name] = theme;
        }

        public EditorTheme Get(string name) => _themes.TryGetValue(name, out var t) ? t : null;
        public IEnumerable<string> Available => _themes.Keys;

        public void SetActive(string name)
        {
            var t = Get(name);
            if (t != null) { Current = t; Current.ApplyToImGuiStyle(); }
        }

        public void ApplyCurrent() => Current?.ApplyToImGuiStyle();
    }

    // ─────────────────────────── 日志 ───────────────────────────


    public struct ConsoleLogEntry
    {
        public DateTime Time;
        public LogLevel Level;
        public string Message;
        public string Category;
        public string Source;
    }

    /// <summary>控制台日志收集接口, 由 ConsolePanel 实现。</summary>
    public interface IEditorConsole
    {
        void AddLog(LogLevel level, string message, string category = "", string source = "");
        void Clear();
        int Count { get; }
    }

    // ─────────────────────────── 停靠空间模型 ───────────────────────────

    public enum DockDirection
    {
        Center,
        Left,
        Right,
        Top,
        Bottom
    }

    public enum DockNodeType
    {
        Empty,
        Tab,
        SplitHorizontal,
        SplitVertical
    }

    public struct RectangleF
    {
        public float X, Y, Width, Height;
        public RectangleF(float x, float y, float w, float h) { X = x; Y = y; Width = w; Height = h; }
        public Vector2 Position => new(X, Y);
        public Vector2 Size => new(Width, Height);
    }

    public sealed class DockNode
    {
        public DockNodeType Type = DockNodeType.Empty;
        public RectangleF Rect;
        public DockNode Parent;
        public DockNode ChildA;
        public DockNode ChildB;
        public readonly List<string> Panels = new();
    }

    /// <summary>
    /// 停靠空间: 维护已注册面板, 并提供 JSON 布局序列化 (可见性 / 顺序)。
    /// 实际 ImGui 停靠由 MainEditorWindow 通过 ImGui.DockSpace 提供;
    /// 本类作为面板注册表与持久化数据的容器。
    /// </summary>
    public sealed class DockSpace
    {
        private readonly Dictionary<string, IEditorPanel> _panels = new();
        private readonly List<string> _order = new();

        public Vector2 Size;
        public DockNode Root;

        public DockSpace(Vector2 size)
        {
            Size = size;
            Root = new DockNode { Type = DockNodeType.Tab, Rect = new RectangleF(0, 0, size.X, size.Y) };
        }

        public void RegisterPanel(IEditorPanel panel)
        {
            if (panel == null) return;
            _panels[panel.Id] = panel;
            if (!_order.Contains(panel.Id)) _order.Add(panel.Id);
        }

        public void UnregisterPanel(string id)
        {
            _panels.Remove(id);
            _order.Remove(id);
        }

        public IEditorPanel GetPanel(string id) => _panels.TryGetValue(id, out var p) ? p : null;
        public IEnumerable<IEditorPanel> Panels => _order.SelectNotNull(id => _panels.TryGetValue(id, out var p) ? p : null);
        public IEnumerable<string> PanelIds => _order;

        public void SetPanelVisible(string id, bool visible)
        {
            if (_panels.TryGetValue(id, out var p)) p.IsVisible = visible;
        }

        public void Dock(IEditorPanel panel, DockNode target, DockDirection dir)
        {
            if (panel == null || target == null) return;
            if (!target.Panels.Contains(panel.Id)) target.Panels.Add(panel.Id);
        }

        public void Render(EditorContext ctx)
        {
            foreach (var p in Panels) p.Render(ctx);
        }

        public void SaveLayout(string path)
        {
            try
            {
                using var ms = new MemoryStream();
                using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
                {
                    w.WriteStartObject();
                    w.WritePropertyName("panels");
                    w.WriteStartArray();
                    foreach (var id in _order)
                    {
                        var p = _panels[id];
                        w.WriteStartObject();
                        w.WriteString("id", id);
                        w.WriteString("title", p.Title);
                        w.WriteBoolean("visible", p.IsVisible);
                        w.WriteBoolean("closed", p.IsClosed);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                File.WriteAllText(path, System.Text.Encoding.UTF8.GetString(ms.ToArray()));
            }
            catch (Exception) { /* 忽略 */ }
        }

        public void LoadLayout(string path)
        {
            try
            {
                var json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("panels", out var arr)) return;
                foreach (var item in arr.EnumerateArray())
                {
                    var id = item.GetProperty("id").GetString();
                    if (string.IsNullOrEmpty(id) || !_panels.TryGetValue(id, out var p)) continue;
                    if (item.TryGetProperty("visible", out var v)) p.IsVisible = v.GetBoolean();
                    if (item.TryGetProperty("closed", out var c)) p.IsClosed = c.GetBoolean();
                }
            }
            catch (Exception) { /* 忽略 */ }
        }
    }

    // ─────────────────────────── 共享上下文 ───────────────────────────

    // MainEditorWindow 的完整实现见 MainEditorWindow.cs (同一程序集)

    /// <summary>
    /// 编辑器共享上下文: 为所有面板提供对核心系统与世界状态的访问,
    /// 以及面板间通信的后向引用 (Editor -> MainEditorWindow)。
    /// </summary>
    public sealed class EditorContext
    {
        public readonly EditorWorld World;
        public readonly AssetDatabase Assets;
        public readonly EntityComponentSystem Ecs;
        public readonly InputSystem Input;
        public EditorTheme Theme;
        public IEditorConsole Console;
        public MainEditorWindow Editor;

        public float DeltaTime;
        public float ElapsedTime;
        public float Fps;

        public EditorContext(EditorWorld world, AssetDatabase assets, EntityComponentSystem ecs,
                             InputSystem input, EditorTheme theme)
        {
            World = world;
            Assets = assets;
            Ecs = ecs;
            Input = input;
            Theme = theme;
        }

        public SelectionSystem Selection => World?.Selection;
        public EditorCommandStack Undo => World?.UndoStack;
        public EditorSceneManager SceneManager => World?.SceneManager;
        public PieStateMachine Pie => World?.Pie;
        public LayerSystem Layers => World?.Layers;
        public EditorCamera Camera => World?.Camera;
    }

    // ─────────────────────────── 面板接口与基类 ───────────────────────────

    /// <summary>可插拔编辑器面板接口。</summary>
    public interface IEditorPanel
    {
        string Title { get; }
        string Id { get; }
        bool IsVisible { get; set; }
        bool IsClosed { get; set; }
        bool CanClose { get; }
        void Initialize(EditorContext ctx);
        void Render(EditorContext ctx);
    }

    /// <summary>
    /// 面板基类: 封装 ImGui 窗口的 Begin/End 生命周期、关闭按钮、
    /// 最小尺寸约束、首次初始化与异常保护。子类只需重写 OnDraw。
    /// </summary>
    public abstract class EditorPanel : IEditorPanel
    {
        public string Title { get; }
        public string Id { get; }
        public bool IsVisible { get; set; } = true;
        public bool IsClosed { get; set; }
        public bool CanClose { get; set; } = true;
        public Vector2 MinSize { get; set; } = new(160, 100);

        /// <summary>传递给 ImGui.Begin 的完整标签 (使用 ### 保持停靠位置稳定)。</summary>
        public string WindowLabel => Title + "###" + Id;

        private bool _initialized;

        protected EditorPanel(string title, string id = null)
        {
            Title = title ?? GetType().Name;
            Id = id ?? Title;
        }

        public virtual void Initialize(EditorContext ctx) { }

        /// <summary>子类实现面板内容绘制。</summary>
        protected abstract void OnDraw(EditorContext ctx);

        /// <summary>子类可重写以自定义窗口标志。</summary>
        protected virtual ImGuiWindowFlags GetWindowFlags() => ImGuiWindowFlags.None;

        public void Render(EditorContext ctx)
        {
            if (!IsVisible || IsClosed) return;
            if (!_initialized) { try { Initialize(ctx); } catch (Exception) { } _initialized = true; }

            ImGui.SetNextWindowSizeConstraints(MinSize, new Vector2(float.MaxValue, float.MaxValue));
            var flags = GetWindowFlags();
            bool open = true;
            if (CanClose)
            {
                if (!ImGui.Begin(WindowLabel, ref open, flags)) { ImGui.End(); return; }
            }
            else
            {
                if (!ImGui.Begin(WindowLabel, flags)) { ImGui.End(); return; }
            }

            try { OnDraw(ctx); }
            catch (Exception ex)
            {
                ImGui.TextColored(new Vector4(0.9f, 0.3f, 0.3f, 1f), $"面板绘制异常: {ex.Message}");
            }

            ImGui.End();

            if (!open && CanClose) IsClosed = true;
        }
    }

    // ─────────────────────────── 控制台命令注册表 ───────────────────────────

    public sealed class CommandRegistry
    {
        private readonly Dictionary<string, CommandEntry> _commands = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _cvars = new(StringComparer.OrdinalIgnoreCase);

        public delegate void CommandHandler(string[] args, EditorContext ctx);

        private struct CommandEntry
        {
            public string Name;
            public string Description;
            public CommandHandler Handler;
        }

        public void Register(string name, string description, CommandHandler handler)
        {
            if (string.IsNullOrWhiteSpace(name) || handler == null) return;
            _commands[name.Trim().ToLowerInvariant()] = new CommandEntry { Name = name, Description = description, Handler = handler };
        }

        public bool Unregister(string name) => _commands.Remove(name.Trim().ToLowerInvariant());

        public IEnumerable<string> GetCommandNames() => _commands.Keys;
        public IEnumerable<(string Name, string Desc)> GetCommands() => _commands.Values.Select(c => (c.Name, c.Description));

        public void SetCvar(string name, string value) => _cvars[name.Trim().ToLowerInvariant()] = value;
        public string GetCvar(string name, string def = "") => _cvars.TryGetValue(name.Trim().ToLowerInvariant(), out var v) ? v : def;
        public IEnumerable<KeyValuePair<string, string>> GetCvars() => _cvars;

        public bool Execute(string line, EditorContext ctx)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;
            line = line.Trim();
            if (line.StartsWith("//") || line.StartsWith("#")) return true;

            // cvar 设置: name value
            var parts = SplitArgs(line);
            if (parts.Length >= 2 && !_commands.ContainsKey(parts[0].ToLowerInvariant()) && !parts[0].StartsWith("/"))
            {
                SetCvar(parts[0], string.Join(' ', parts, 1, parts.Length - 1));
                ctx?.Console?.AddLog(LogLevel.Info, $"cvar {parts[0]} = {GetCvar(parts[0])}", "cvar");
                return true;
            }

            // 命令: /cmd args 或 cmd args
            var cmdName = parts[0].TrimStart('/').ToLowerInvariant();
            if (_commands.TryGetValue(cmdName, out var entry))
            {
                var args = new string[parts.Length - 1];
                if (args.Length > 0) Array.Copy(parts, 1, args, 0, args.Length);
                try { entry.Handler(args, ctx); }
                catch (Exception ex) { ctx?.Console?.AddLog(LogLevel.Error, $"命令 '{cmdName}' 执行失败: {ex.Message}", "cmd"); }
                return true;
            }

            // cvar 查询: name
            if (_cvars.TryGetValue(cmdName, out var cv))
            {
                ctx?.Console?.AddLog(LogLevel.Info, $"{cmdName} = {cv}", "cvar");
                return true;
            }

            ctx?.Console?.AddLog(LogLevel.Warning, $"未知命令: {cmdName}", "cmd");
            return false;
        }

        private static string[] SplitArgs(string line)
        {
            var result = new List<string>();
            int i = 0;
            while (i < line.Length)
            {
                while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
                if (i >= line.Length) break;
                if (line[i] == '"')
                {
                    i++;
                    int start = i;
                    while (i < line.Length && line[i] != '"') i++;
                    result.Add(line.Substring(start, i - start));
                    if (i < line.Length) i++;
                }
                else
                {
                    int start = i;
                    while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
                    result.Add(line.Substring(start, i - start));
                }
            }
            return result.ToArray();
        }
    }

    // ─────────────────────────── 数学扩展 ───────────────────────────

    public static class EditorMathExtensions
    {
        public static Vector3 ToEuler(this Quaternion q)
        {
            Vector3 angles;
            // roll (x)
            float sinr = 2.0f * (q.W * q.X + q.Y * q.Z);
            float cosr = 1.0f - 2.0f * (q.X * q.X + q.Y * q.Y);
            angles.X = MathF.Atan2(sinr, cosr);
            // pitch (y)
            float sinp = 2.0f * (q.W * q.Y - q.Z * q.X);
            if (MathF.Abs(sinp) >= 1.0f) angles.Y = MathF.CopySign(MathF.PI / 2.0f, sinp);
            else angles.Y = MathF.Asin(sinp);
            // yaw (z)
            float siny = 2.0f * (q.W * q.Z + q.X * q.Y);
            float cosy = 1.0f - 2.0f * (q.Y * q.Y + q.Z * q.Z);
            angles.Z = MathF.Atan2(siny, cosy);
            return angles;
        }

        public static Quaternion FromEuler(Vector3 e)
        {
            float cy = MathF.Cos(e.Z * 0.5f), sy = MathF.Sin(e.Z * 0.5f);
            float cp = MathF.Cos(e.Y * 0.5f), sp = MathF.Sin(e.Y * 0.5f);
            float cr = MathF.Cos(e.X * 0.5f), sr = MathF.Sin(e.X * 0.5f);
            return new Quaternion(
                sr * cp * cy - cr * sp * sy,
                cr * sp * cy + sr * cp * sy,
                cr * cp * sy - sr * sp * cy,
                cr * cp * cy + sr * sp * sy);
        }

        public static Vector3 ToEulerDegrees(this Quaternion q) => ToEuler(q) * (180f / MathF.PI);
        public static Quaternion FromEulerDegrees(Vector3 deg) => FromEuler(deg * (MathF.PI / 180f));

        public static float ClampSafe(this float v, float min, float max) => Math.Clamp(v, min, max);
        public static float Lerp(float a, float b, float t) => a + (b - a) * t;
    }

    internal static class LinqHelper
    {
        public static IEnumerable<T> SelectNotNull<S, T>(this IEnumerable<S> src, Func<S, T> sel) where T : class
        {
            foreach (var s in src) { var v = sel(s); if (v != null) yield return v; }
        }
    }
}
