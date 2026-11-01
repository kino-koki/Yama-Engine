using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using YamaEngine.Physics;
using YamaEngine.Audio;
using YamaEngine.Particles;
using YamaEngine.World;
using YamaEngine.Config;
using YamaEngine.Input;
using YamaEngine.Networking;
using YamaEngine.DCC;
using YamaEngine.Assets;

namespace YamaEngine.Core
{
    public class EngineSystems
    {
        public readonly CoreTime Time = new();
        public readonly RuntimePerformance Performance = new();
        public readonly ConsoleVariableSystem ConsoleVariables = new();
        public readonly PhysicsScene Physics = new();
        public readonly AudioSystem Audio = new();
        public readonly ParticleSystemCore Particles = new();
        public readonly List<ParticleEffect> ActiveParticleEffects = new();
        public readonly WorldStreamingGridSystem WorldStreamingGrid = new();
        public readonly NetworkSystem Network = new();
        public readonly InputSystem Input = new();
        public readonly DCCBridge DCC = new();
        public AssetDatabase Assets { get; private set; }
        public readonly ViewportCamera ViewportCamera = new();
        public readonly RenderingFlags Rendering = new();
        public readonly SelectionSystem Selection = new();
        public readonly GizmoSystem Gizmo = new();
        public readonly EditorGrid EditorGrid = new();
        public readonly EditorConsole Console = new();

        public string ProjectPath = "";
        public string ProjectName = "Untitled";
        public bool IsPlaying;
        public bool IsPaused;
        public float GlobalTimeScale = 1.0f;
        public event Action OnEngineStart;
        public event Action OnEngineTick;
        public event Action OnEngineStop;
        public event Action<string> OnLogMessage;

        public EngineSystems(string projectPath = "")
        {
            ProjectPath = string.IsNullOrEmpty(projectPath)
                ? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "YamaEngine_Projects")
                : projectPath;
            Assets = new AssetDatabase(ProjectPath);
            ConsoleVariables.Register("r.ShowGrid", "1", "Show editor grid");
            ConsoleVariables.Register("r.ShowGizmos", "1", "Show editor gizmos");
            ConsoleVariables.Register("r.Wireframe", "0", "Force wireframe mode");
            ConsoleVariables.Register("r.Shadows", "1", "Enable shadows");
            ConsoleVariables.Register("r.AntiAliasing", "1", "Enable anti-aliasing");
            ConsoleVariables.Register("r.MotionBlur", "0", "Motion blur effect");
            ConsoleVariables.Register("r.Bloom", "1", "Bloom post processing");
            ConsoleVariables.Register("r.Exposure", "1.0", "Camera exposure");
            ConsoleVariables.Register("r.Contrast", "1.0", "Color contrast");
            ConsoleVariables.Register("r.Saturation", "1.0", "Color saturation");
            ConsoleVariables.Register("g.TimeScale", "1.0", "Global time scale");
            ConsoleVariables.Register("g.CameraSpeed", "5.0", "Editor camera speed");
            ConsoleVariables.Register("g.CameraMouseSensitivity", "0.002", "Camera mouse sensitivity");
            ConsoleVariables.Register("e.SnapGrid", "1.0", "Grid snap size");
            ConsoleVariables.Register("e.SnapAngle", "15.0", "Rotation snap angle");
            ConsoleVariables.Register("d.DebugDraw", "0", "Enable debug drawing");
            ConsoleVariables.Register("s.ShowStats", "1", "Show statistics overlay");
            ConsoleVariables.TrySet("g.TimeScale", "1.0");
            Log("Engine systems initialized");
        }

        public void SetProject(string projectPath, string projectName)
        {
            if (string.IsNullOrWhiteSpace(projectPath)) throw new ArgumentException("Project path is required.", nameof(projectPath));
            ProjectPath = projectPath;
            ProjectName = string.IsNullOrWhiteSpace(projectName) ? "Untitled" : projectName;
            Assets = new AssetDatabase(ProjectPath);
            Log($"Project loaded: {ProjectName} ({ProjectPath})");
        }

        public void Start()
        {
            OnEngineStart?.Invoke();
            Log("Engine started");
        }

        public void Tick(float realDeltaTime)
        {
            var scaledDt = realDeltaTime * GlobalTimeScale;
            Time.Update(scaledDt, realDeltaTime);
            Performance.BeginFrame();
            Input.Update(scaledDt);
            if (!IsPaused)
            {
                var transforms = new List<ECS.TransformComponent>();
                var entityMap = new Dictionary<int, int>();
                Physics.Update(scaledDt, transforms, entityMap);
            }
            Particles.Update(scaledDt, ViewportCamera.Position);
            Audio.Update(scaledDt);
            Network.Update(scaledDt);
            if (Input.EditorContext.TryGetAction("OrbitZoom", out var zoom) && Input.MouseInViewport)
            {
                float wheelDelta = Input.GetAxis(InputAxis.MouseWheel);
                if (Math.Abs(wheelDelta) > 0.001f)
                    ViewportCamera.Zoom(wheelDelta * 0.5f);
            }
            if (Input.MouseInViewport)
            {
                var move = new Vector3();
                if (Input.EditorContext.TryGetAction("OrbitForward", out var f) && f.IsPressed) move.Z -= 1;
                if (Input.EditorContext.TryGetAction("OrbitBackward", out var b) && b.IsPressed) move.Z += 1;
                if (Input.EditorContext.TryGetAction("OrbitLeft", out var l) && l.IsPressed) move.X -= 1;
                if (Input.EditorContext.TryGetAction("OrbitRight", out var r) && r.IsPressed) move.X += 1;
                if (Input.EditorContext.TryGetAction("OrbitUp", out var u) && u.IsPressed) move.Y += 1;
                if (Input.EditorContext.TryGetAction("OrbitDown", out var d) && d.IsPressed) move.Y -= 1;
                float speed = ConsoleVariables.Get("g.CameraSpeed", 5.0f) * scaledDt;
                if (Input.IsKeyDown(InputKey.Shift)) speed *= 3.0f;
                if (move.LengthSquared() > 0) ViewportCamera.Dolly(move * speed);
            }
            if (Input.MouseInViewport && Input.IsKeyDown(InputKey.MouseRight))
            {
                var look = Input.GetLookVector();
                if (look.LengthSquared() > 0)
                    ViewportCamera.Orbit(look.X, look.Y);
            }
            if (Input.MouseInViewport && Input.IsKeyDown(InputKey.MouseMiddle))
            {
                float panX = -Input.GetAxis(InputAxis.MouseDeltaX) * 0.01f;
                float panY = Input.GetAxis(InputAxis.MouseDeltaY) * 0.01f;
                if (Math.Abs(panX) > 0.0001f || Math.Abs(panY) > 0.0001f)
                    ViewportCamera.Pan(panX, panY);
            }
            if (Assets != null)
            {
                foreach (var obj in Assets.SceneObjects)
                {
                    foreach (var comp in obj.Components)
                        comp.Update(scaledDt, obj);
                }
            }
            OnEngineTick?.Invoke();
            Performance.EndFrame();
        }

        public void Stop()
        {
            OnEngineStop?.Invoke();
            Log("Engine stopped");
        }

        public void SetPlaying(bool playing)
        {
            IsPlaying = playing;
            IsPaused = false;
            Log(playing ? "▶ Play mode" : "■ Stop mode");
        }

        public void TogglePause()
        {
            IsPaused = !IsPaused;
            Log(IsPaused ? "⏸ Paused" : "▶ Resumed");
        }

        public void Log(string message)
        {
            try
            {
                Debug.WriteLine($"[Engine] {message}");
            }
            catch { }
            Console?.AddLog(message, "Info");
            OnLogMessage?.Invoke(message);
        }

        public void LogWarning(string message)
        {
            try { Debug.WriteLine($"[Engine WARN] {message}"); } catch { }
            Console?.AddLog(message, "Warning");
        }

        public void LogError(string message)
        {
            try { Debug.WriteLine($"[Engine ERROR] {message}"); } catch { }
            Console?.AddLog(message, "Error");
        }
    }

    public class CoreTime
    {
        public float Time;
        public float RealTime;
        public float DeltaTime;
        public float RealDeltaTime;
        public int FrameCount;
        public float FPS = 60f;
        private float _fpsAccumulator;
        private int _fpsFrames;

        public void Update(float dt, float realDt)
        {
            Time += dt;
            RealTime += realDt;
            DeltaTime = dt;
            RealDeltaTime = realDt;
            FrameCount++;
            _fpsAccumulator += realDt;
            _fpsFrames++;
            if (_fpsAccumulator >= 0.5f)
            {
                FPS = _fpsFrames / _fpsAccumulator;
                _fpsAccumulator = 0f;
                _fpsFrames = 0;
            }
        }
    }

    public class RuntimePerformance
    {
        public float FrameTime;
        public float GPUUtilization;
        public float CPUUtilization;
        public long AllocatedMemoryMB;
        public int DrawCalls;
        public int TriangleCount;
        public int VertexCount;
        public long FrameStartTime;
        private readonly System.Diagnostics.Stopwatch _sw = new();

        public void BeginFrame()
        {
            _sw.Restart();
            FrameStartTime = System.Diagnostics.Stopwatch.GetTimestamp();
            DrawCalls = 0;
            TriangleCount = 0;
            VertexCount = 0;
        }

        public void EndFrame()
        {
            _sw.Stop();
            FrameTime = (float)_sw.Elapsed.TotalMilliseconds;
        }

        public string GetSummary()
        {
            return $"Frame: {FrameTime:0.00}ms | Draw: {DrawCalls} | Tris: {TriangleCount:N0} | Verts: {VertexCount:N0}";
        }
    }

    public class ViewportCamera
    {
        public Vector3 Position = new(5, 4, 8);
        public Vector3 Target = Vector3.Zero;
        public Vector3 Up = Vector3.UnitY;
        public float Yaw = -0.55f;
        public float Pitch = 0.35f;
        public float Distance = 10f;
        public float FOV = 50f;
        public float Aspect = 1.777f;
        public float Near = 0.1f;
        public float Far = 1000f;
        public bool IsOrtho;
        public float OrthoSize = 10f;

        public Matrix4x4 ViewMatrix
        {
            get
            {
                var forward = new Vector3(MathF.Cos(Pitch) * MathF.Sin(Yaw), MathF.Sin(Pitch), MathF.Cos(Pitch) * MathF.Cos(Yaw));
                var camPos = Target - forward * Distance;
                Position = camPos;
                return Matrix4x4.CreateLookAt(camPos, Target, Up);
            }
        }

        public Matrix4x4 ProjectionMatrix =>
            IsOrtho ? Matrix4x4.CreateOrthographic(OrthoSize * Aspect, OrthoSize, Near, Far)
                    : Matrix4x4.CreatePerspectiveFieldOfView(FOV * (float)(Math.PI / 180), Aspect, Near, Far);

        public Vector3 Forward
        {
            get
            {
                var forward = new Vector3(MathF.Cos(Pitch) * MathF.Sin(Yaw), MathF.Sin(Pitch), MathF.Cos(Pitch) * MathF.Cos(Yaw));
                return Vector3.Normalize(forward);
            }
        }

        public Vector3 Right => Vector3.Normalize(Vector3.Cross(Forward, Up));

        public void Orbit(float dx, float dy)
        {
            Yaw += dx * 3f;
            Pitch += dy * 3f;
            Pitch = Math.Clamp(Pitch, -1.5f, 1.5f);
        }

        public void Pan(float dx, float dy)
        {
            Target += Right * dx * Distance * 0.5f;
            Target += Up * dy * Distance * 0.5f;
        }

        public void Dolly(Vector3 delta)
        {
            Target += Forward * delta.Z + Right * delta.X + Up * delta.Y;
        }

        public void Zoom(float amount)
        {
            Distance *= 1f - amount;
            Distance = Math.Clamp(Distance, 0.3f, 500f);
        }

        public void Frame(YamaEngine.Assets.AABB bounds)
        {
            Target = bounds.Center;
            var size = Vector3.Distance(bounds.Max, bounds.Min);
            Distance = Math.Max(2f, size * 1.2f);
        }

        public void LookFrom(string direction)
        {
            switch (direction)
            {
                case "Front": Yaw = 0; Pitch = 0; break;
                case "Back": Yaw = (float)Math.PI; Pitch = 0; break;
                case "Right": Yaw = (float)Math.PI / 2; Pitch = 0; break;
                case "Left": Yaw = -(float)Math.PI / 2; Pitch = 0; break;
                case "Top": Yaw = 0; Pitch = (float)Math.PI / 2 - 0.01f; break;
                case "Bottom": Yaw = 0; Pitch = -(float)Math.PI / 2 + 0.01f; break;
            }
        }
    }

    public class RenderingFlags
    {
        public bool ShowGrid = true;
        public bool ShowGizmos = true;
        public bool ShowSelectionOutline = true;
        public bool ShowFpsCounter = true;
        public bool ShowStatsPanel = true;
        public bool ShowWorldAxis = true;
        public bool UseDeferredRendering = true;
        public bool EnableShadows = true;
        public bool EnableAntiAliasing = true;
        public bool EnableBloom;
        public bool EnableAmbientOcclusion = true;
        public bool EnableScreenSpaceReflections;
        public float ShadowDistance = 40f;
        public float ShadowBias = 0.005f;
        public int ShadowMapSize = 1024;
    }

    public class SelectionSystem
    {
        public Assets.SceneObject Active;
        public readonly List<Assets.SceneObject> SelectionList = new();
        public event Action<Assets.SceneObject> OnSelectionChanged;

        public void Select(Assets.SceneObject obj, bool additive = false)
        {
            if (!additive)
            {
                foreach (var o in SelectionList) if (o != null) o.IsSelected = false;
                SelectionList.Clear();
            }
            if (obj != null)
            {
                obj.IsSelected = true;
                if (!SelectionList.Contains(obj)) SelectionList.Add(obj);
            }
            Active = obj;
            OnSelectionChanged?.Invoke(obj);
        }

        public void Toggle(Assets.SceneObject obj)
        {
            if (obj == null) return;
            if (SelectionList.Contains(obj))
            {
                obj.IsSelected = false;
                SelectionList.Remove(obj);
                Active = SelectionList.Count > 0 ? SelectionList[0] : null;
            }
            else
            {
                obj.IsSelected = true;
                SelectionList.Add(obj);
                Active = obj;
            }
            OnSelectionChanged?.Invoke(Active);
        }

        public void Clear()
        {
            foreach (var o in SelectionList) if (o != null) o.IsSelected = false;
            SelectionList.Clear();
            Active = null;
            OnSelectionChanged?.Invoke(null);
        }

        public int Count => SelectionList.Count;
    }

    public class GizmoSystem
    {
        public GizmoMode Mode = GizmoMode.Translate;
        public GizmoSpace Space = GizmoSpace.Local;
        public bool SnapEnabled;
        public float SnapPosition = 1f;
        public float SnapRotation = 15f;
        public float SnapScale = 0.1f;
    }

    public enum GizmoMode
    {
        Translate, Rotate, Scale, Universal
    }

    public enum GizmoSpace
    {
        World, Local
    }

    public class EditorGrid
    {
        public float Size = 20f;
        public int MajorLineInterval = 10;
        public float LineThickness = 1f;
        public Vector3 ColorMinor = new(0.3f, 0.3f, 0.3f);
        public Vector3 ColorMajor = new(0.5f, 0.5f, 0.5f);
        public Vector3 ColorAxisX = new(1f, 0.2f, 0.2f);
        public Vector3 ColorAxisZ = new(0.2f, 0.4f, 1f);
    }

    public class EditorConsole
    {
        public struct LogEntry
        {
            public DateTime Timestamp;
            public string Message;
            public string Level;
        }

        public readonly List<LogEntry> Logs = new();
        public int MaxEntries = 1000;
        public event Action<LogEntry> OnLogAdded;

        public void AddLog(string message, string level = "Info")
        {
            var entry = new LogEntry { Timestamp = DateTime.Now, Message = message, Level = level };
            Logs.Add(entry);
            if (Logs.Count > MaxEntries) Logs.RemoveAt(0);
            OnLogAdded?.Invoke(entry);
        }

        public List<LogEntry> GetLogs(string filter = null)
        {
            if (string.IsNullOrEmpty(filter)) return new List<LogEntry>(Logs);
            var filtered = new List<LogEntry>();
            foreach (var log in Logs)
            {
                if (log.Message.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    log.Level.Equals(filter, StringComparison.OrdinalIgnoreCase))
                    filtered.Add(log);
            }
            return filtered;
        }

        public void Clear()
        {
            Logs.Clear();
        }
    }
}
