using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Linq;

namespace YamaEngine.Physics
{
    // ============================================================================================
    //  PhysicsDebugger.cs
    //
    //  物理调试可视化系统。把物理世界里的AABB、接触点、约束、速度向量等等都用线框
    //  画出来，方便开发的时候看物理到底在干啥。
    //
    //  用法:
    //    var dbg = new PhysicsDebugger();
    //    dbg.Config.ShowAABBs = true;
    //    dbg.Config.ShowContacts = true;
    //    dbg.BeginFrame();
    //    dbg.DrawWorld(world);
    //    dbg.EndFrame();
    //    // 然后把 dbg.RenderBuffer 丢给渲染管线画出来
    //
    //  注意: 这个东西开销不小，别在正式版开。Release里把Enable关掉。
    //  TODO: 加上Gizmo交互(选中某个body高亮显示它的constraint网络)。
    // ============================================================================================

    /// <summary>调试绘制图元类型。</summary>
    public enum DebugPrimitiveType : byte
    {
        Line = 0,
        Triangle = 1,
        Point = 2,
        Text = 3,
        Box = 4,
        Sphere = 5,
        Arrow = 6,
        Circle = 7,
        Quad = 8,
        Cross = 9,
    }

    /// <summary>调试绘制通道(分层)。</summary>
    public enum DebugRenderLayer : byte
    {
        /// <summary>默认层。一般信息。</summary>
        Default = 0,
        /// <summary>AABB/Bounds。</summary>
        Bounds = 1,
        /// <summary>接触点/法线。</summary>
        Contacts = 2,
        /// <summary>约束/关节。</summary>
        Constraints = 3,
        /// <summary>速度/角速度向量。</summary>
        Velocities = 4,
        /// <summary>力的可视化。</summary>
        Forces = 5,
        /// <summary>形状线框。</summary>
        Shapes = 6,
        /// <summary>岛屿/休眠状态。</summary>
        Islands = 7,
        /// <summary>性能/统计信息。</summary>
        Stats = 8,
        /// <summary>射线检测可视化。</summary>
        Raycasts = 9,
        /// <summary>用户自定义。</summary>
        Custom = 15,
    }

    /// <summary>调试绘制颜色(预定义)。</summary>
    public static class DebugColors
    {
        public static readonly Vector4 White = new(1, 1, 1, 1);
        public static readonly Vector4 Black = new(0, 0, 0, 1);
        public static readonly Vector4 Red = new(1, 0, 0, 1);
        public static readonly Vector4 Green = new(0, 1, 0, 1);
        public static readonly Vector4 Blue = new(0, 0.4f, 1, 1);
        public static readonly Vector4 Yellow = new(1, 1, 0, 1);
        public static readonly Vector4 Cyan = new(0, 1, 1, 1);
        public static readonly Vector4 Magenta = new(1, 0, 1, 1);
        public static readonly Vector4 Orange = new(1, 0.5f, 0, 1);
        public static readonly Vector4 Purple = new(0.6f, 0, 0.8f, 1);
        public static readonly Vector4 Gray = new(0.5f, 0.5f, 0.5f, 1);
        public static readonly Vector4 DarkGray = new(0.25f, 0.25f, 0.25f, 1);
        public static readonly Vector4 LightGray = new(0.75f, 0.75f, 0.75f, 1);
        public static readonly Vector4 Brown = new(0.5f, 0.3f, 0.1f, 1);
        public static readonly Vector4 Pink = new(1, 0.5f, 0.7f, 1);
        public static readonly Vector4 Lime = new(0.5f, 1, 0, 1);
        public static readonly Vector4 Teal = new(0, 0.5f, 0.5f, 1);
        public static readonly Vector4 Navy = new(0, 0, 0.5f, 1);
        public static readonly Vector4 Maroon = new(0.5f, 0, 0, 1);
        public static readonly Vector4 Olive = new(0.5f, 0.5f, 0, 1);

        /// <summary>根据状态/类型返回对应颜色。</summary>
        public static Vector4 ForBodyState(BodyDebugState state)
        {
            return state switch
            {
                BodyDebugState.Active => Red,
                BodyDebugState.Sleeping => Blue,
                BodyDebugState.Kinematic => Green,
                BodyDebugState.Static => Gray,
                BodyDebugState.Trigger => Yellow,
                BodyDebugState.Selected => Magenta,
                _ => White,
            };
        }

        /// <summary>根据碰撞响应类型返回颜色。</summary>
        public static Vector4 ForCollisionSeverity(CollisionSeverity severity)
        {
            return severity switch
            {
                CollisionSeverity.None => DarkGray,
                CollisionSeverity.Low => Green,
                CollisionSeverity.Medium => Yellow,
                CollisionSeverity.High => Orange,
                CollisionSeverity.Critical => Red,
                _ => White,
            };
        }
    }

    /// <summary>刚体的调试状态。</summary>
    public enum BodyDebugState
    {
        Active,
        Sleeping,
        Kinematic,
        Static,
        Trigger,
        Selected,
        Disabled,
    }

    /// <summary>碰撞严重程度。</summary>
    public enum CollisionSeverity
    {
        None,
        Low,
        Medium,
        High,
        Critical,
    }

    /// <summary>调试绘制顶点。</summary>
    public struct DebugVertex
    {
        public Vector3 Position;
        public Vector4 Color;
        public Vector2 UV;
    }

    /// <summary>单个调试绘制图元。</summary>
    public struct DebugPrimitive
    {
        public DebugPrimitiveType Type;
        public DebugRenderLayer Layer;
        public DebugVertex V0;
        public DebugVertex V1;
        public DebugVertex V2;
        public float Size;          // 线宽/点大小
        public float DepthBias;     // 用来避免Z-fighting
        public bool DepthTest;      // 是否做深度测试
        public int UserData;        // 关联的body/constraint id
        public double Timestamp;    // 创建时间(用于超时清理)
    }

    /// <summary>调试文本元素。</summary>
    public struct DebugText
    {
        public Vector3 Position;        // 世界坐标(nullptr表示屏幕坐标)
        public Vector2 ScreenPos;       // 屏幕坐标(像素)
        public string Text;
        public Vector4 Color;
        public float Size;
        public DebugRenderLayer Layer;
        public bool WorldSpace;         // true=3D世界空间, false=屏幕空间
        public double Timestamp;
        public float Lifetime;          // 存活时间(秒)
    }

    /// <summary>调试配置。</summary>
    public class DebugDrawConfig
    {
        // ---- 总开关 ----
        /// <summary>总开关。关掉之后啥都不画。</summary>
        public bool Enable = true;

        // ---- 各类元素开关 ----
        public bool ShowAABBs = false;
        public bool ShowShapes = true;
        public bool ShowContacts = true;
        public bool ShowContactNormals = true;
        public bool ShowContactImpulses = false;
        public bool ShowConstraints = false;
        public bool ShowConstraintLimits = false;
        public bool ShowVelocities = false;
        public bool ShowAngularVelocities = false;
        public bool ShowForces = false;
        public bool ShowTorques = false;
        public bool ShowIslands = false;
        public bool ShowSleepingBodies = true;
        public bool ShowActiveBodies = true;
        public bool ShowCenterOfMass = false;
        public bool ShowInertiaAxes = false;
        public bool ShowBodyInfo = false;
        public bool ShowStats = true;
        public bool ShowRaycasts = false;
        public bool ShowOverlaps = false;
        public bool ShowSweptShapes = false;
        public bool ShowBroadphasePairs = false;
        public bool ShowNormals = false;
        public bool ShowGrid = false;
        public bool ShowAxes = true;
        public bool ShowWarnings = true;

        // ---- 颜色配置 ----
        public Vector4 AABBColor = DebugColors.Lime;
        public Vector4 ShapeColor = DebugColors.White;
        public Vector4 ShapeColorSleeping = DebugColors.Blue;
        public Vector4 ShapeColorKinematic = DebugColors.Green;
        public Vector4 ShapeColorStatic = DebugColors.Gray;
        public Vector4 ShapeColorTrigger = DebugColors.Yellow;
        public Vector4 ShapeColorSelected = DebugColors.Magenta;
        public Vector4 ContactColor = DebugColors.Red;
        public Vector4 ContactNormalColor = DebugColors.Cyan;
        public Vector4 ConstraintColor = DebugColors.Yellow;
        public Vector4 VelocityColor = DebugColors.Green;
        public Vector4 AngularVelocityColor = DebugColors.Cyan;
        public Vector4 ForceColor = DebugColors.Orange;
        public Vector4 TorqueColor = DebugColors.Purple;
        public Vector4 IslandColors0 = DebugColors.Red;
        public Vector4 IslandColors1 = DebugColors.Green;
        public Vector4 IslandColors2 = DebugColors.Blue;
        public Vector4 IslandColors3 = DebugColors.Yellow;
        public Vector4 IslandColors4 = DebugColors.Magenta;
        public Vector4 IslandColors5 = DebugColors.Cyan;
        public Vector4 COMColor = DebugColors.Pink;
        public Vector4 InertiaAxisColor = DebugColors.Orange;
        public Vector4 GridColor = new(0.3f, 0.3f, 0.3f, 0.5f);
        public Vector4 AxisXColor = DebugColors.Red;
        public Vector4 AxisYColor = DebugColors.Green;
        public Vector4 AxisZColor = DebugColors.Blue;
        public Vector4 RaycastColor = DebugColors.Cyan;
        public Vector4 RaycastHitColor = DebugColors.Red;
        public Vector4 OverlapColor = DebugColors.Yellow;
        public Vector4 SweptShapeColor = new(1, 1, 0, 0.3f);
        public Vector4 BroadphasePairColor = new(1, 1, 0, 0.2f);
        public Vector4 NormalColor = DebugColors.Cyan;
        public Vector4 WarningColor = DebugColors.Orange;

        // ---- 尺寸参数 ----
        public float LineWidth = 1.5f;
        public float ContactPointSize = 6f;
        public float COMPointSize = 8f;
        public float VelocityScale = 0.1f;      // 1m/s画多长
        public float ForceScale = 0.001f;       // 1N画多长
        public float AngularVelocityScale = 0.1f;
        public float NormalLength = 0.5f;
        public float ArrowHeadSize = 0.1f;
        public float GridSize = 1f;
        public int GridExtent = 50;              // 网格延伸多少格
        public float AxisLength = 1f;
        public float DepthBias = 0.001f;
        public float TextSize = 14f;

        // ---- 筛选 ----
        /// <summary>只显示指定body id的调试信息。0=显示所有。</summary>
        public int FilterBodyId = 0;
        /// <summary>只显示指定碰撞层的body。-1=所有层。</summary>
        public int FilterLayer = -1;
        /// <summary>最大绘制图元数量(防止爆掉)。</summary>
        public int MaxPrimitives = 50000;
        /// <summary>最大文本数量。</summary>
        public int MaxTexts = 1000;
        /// <summary>接触点的最小冲量阈值。低于此值不画。</summary>
        public float MinContactImpulse = 0f;
        /// <summary>速度的最小值。低于此值不画。</summary>
        public float MinVelocity = 0.1f;
        /// <summary>力的最小值。低于此值不画。</summary>
        public float MinForce = 0.1f;

        // ---- 行为 ----
        public bool DepthTest = true;
        public bool PersistentContacts = false;   // 接触点持续显示
        public float ContactLifetime = 0.1f;       // 接触点显示时长
        public bool FadeOldPrimitives = false;     // 老图元淡出
        public bool ShowStatistics = true;         // 显示统计信息
        public bool ColorByIsland = false;         // 按岛屿着色
        public bool ColorBySpeed = false;          // 按速度着色
        public bool ColorByMass = false;           // 按质量着色
        public float SpeedColorMax = 20f;          // 速度着色的最大值
        public float MassColorMax = 100f;          // 质量着色的最大值

        /// <summary>复制配置。</summary>
        public DebugDrawConfig Clone()
        {
            return (DebugDrawConfig)MemberwiseClone();
        }
    }

    /// <summary>调试统计快照。</summary>
    public struct DebugStatsSnapshot
    {
        public int BodyCount;
        public int ActiveBodyCount;
        public int SleepingBodyCount;
        public int StaticBodyCount;
        public int KinematicBodyCount;
        public int ContactCount;
        public int ConstraintCount;
        public int IslandCount;
        public int BroadphasePairCount;
        public int NarrowphasePairCount;
        public int CCDCheckCount;
        public float TotalMass;
        public float AvgSpeed;
        public float MaxSpeed;
        public float StepTimeMs;
        public float BroadphaseTimeMs;
        public float NarrowphaseTimeMs;
        public float SolverTimeMs;
        public float IntegrateTimeMs;
        public long MemoryUsageBytes;
        public int DrawnPrimitives;
        public int DrawnTexts;
        public int FrameNumber;
        public float Fps;
    }

    /// <summary>选中body的信息。</summary>
    public class SelectedBodyInfo
    {
        public int BodyId;
        public Vector3 Position;
        public Quaternion Orientation;
        public Vector3 Velocity;
        public Vector3 AngularVelocity;
        public float Mass;
        public float InverseMass;
        public Matrix4x4 InertiaTensor;
        public BodyDebugState State;
        public int CollisionLayer;
        public int CollisionMask;
        public int IslandId;
        public float SleepTimer;
        public string ShapeName = string.Empty;
        public int ContactCount;
        public int ConstraintCount;
        public Vector3 Force;
        public Vector3 Torque;
        public Vector3 CenterOfMass;
        public Vector3 LinearDamping;
        public float AngularDamping;
        public bool UseCCD;
        public bool IsTrigger;
        public bool IsSleeping;
        public List<int> Contacts = new();
        public List<int> Constraints = new();
    }

    /// <summary>
    /// 物理调试可视化器。负责把物理世界的状态画成线框/点/文本，方便调试。
    /// </summary>
    public class PhysicsDebugger : IDisposable
    {
        // ---- 渲染缓冲 ----
        private readonly List<DebugPrimitive> _primitives = new(4096);
        private readonly List<DebugText> _texts = new(256);
        private readonly List<DebugPrimitive> _persistentPrimitives = new(256);
        private readonly List<DebugText> _persistentTexts = new(64);
        private readonly Dictionary<int, SelectedBodyInfo> _selectedBodies = new();

        // ---- 配置 ----
        private DebugDrawConfig _config = new();
        public DebugDrawConfig Config => _config;
        public void SetConfig(DebugDrawConfig cfg) => _config = cfg ?? new DebugDrawConfig();

        // ---- 状态 ----
        private bool _disposed;
        private bool _inFrame;
        private double _currentTime;
        private int _frameNumber;
        private DebugStatsSnapshot _lastStats;
        private readonly StringBuilder _statsBuilder = new(1024);

        // ---- 临时缓冲(避免每帧new) ----
        private readonly Vector3[] _boxCorners = new Vector3[8];
        private readonly Vector3[] _sphereVerts = new Vector3[64];
        private readonly List<Vector3> _lineStrip = new(128);

        // ---- 警告系统 ----
        private readonly List<(string msg, double time, Vector3? pos)> _warnings = new();
        private float _warningLifetime = 5f;

        // ---- 选中的物体 ----
        public IReadOnlyDictionary<int, SelectedBodyInfo> SelectedBodies => _selectedBodies;
        public bool HasSelection => _selectedBodies.Count > 0;

        /// <summary>当前帧的图元数量。</summary>
        public int PrimitiveCount => _primitives.Count;
        /// <summary>当前帧的文本数量。</summary>
        public int TextCount => _texts.Count;
        /// <summary>渲染缓冲(只读访问)。</summary>
        public IReadOnlyList<DebugPrimitive> RenderBuffer => _primitives;
        public IReadOnlyList<DebugText> TextBuffer => _texts;
        public IReadOnlyList<DebugPrimitive> PersistentPrimitives => _persistentPrimitives;
        public IReadOnlyList<DebugText> PersistentTexts => _persistentTexts;

        // ========================================================================================
        //  构造 / 销毁
        // ========================================================================================

        public PhysicsDebugger() { }

        public PhysicsDebugger(DebugDrawConfig config)
        {
            _config = config ?? new DebugDrawConfig();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _primitives.Clear();
            _texts.Clear();
            _persistentPrimitives.Clear();
            _persistentTexts.Clear();
            _selectedBodies.Clear();
            _warnings.Clear();
        }

        // ========================================================================================
        //  帧管理
        // ========================================================================================

        /// <summary>开始一帧。清空缓冲。</summary>
        public void BeginFrame()
        {
            if (_disposed) return;
            if (_inFrame)
            {
                // 上一帧没EndFrame？那直接覆盖
                EndFrame();
            }

            _inFrame = true;
            _primitives.Clear();
            _texts.Clear();
            _frameNumber++;

            // 清理过期的persistent元素
            CleanupPersistent();

            // 清理过期的警告
            CleanupWarnings();
        }

        /// <summary>结束一帧。</summary>
        public void EndFrame()
        {
            if (!_inFrame) return;
            _inFrame = false;

            // 限制图元数量
            if (_primitives.Count > _config.MaxPrimitives)
            {
                _primitives.RemoveRange(_config.MaxPrimitives, _primitives.Count - _config.MaxPrimitives);
            }
            if (_texts.Count > _config.MaxTexts)
            {
                _texts.RemoveRange(_config.MaxTexts, _texts.Count - _config.MaxTexts);
            }

            // 把persistent元素加进去
            if (_persistentPrimitives.Count > 0)
            {
                _primitives.AddRange(_persistentPrimitives);
            }
            if (_persistentTexts.Count > 0)
            {
                _texts.AddRange(_persistentTexts);
            }

            _lastStats.DrawnPrimitives = _primitives.Count;
            _lastStats.DrawnTexts = _texts.Count;
            _lastStats.FrameNumber = _frameNumber;
        }

        /// <summary>更新时间(用于过期清理)。</summary>
        public void UpdateTime(double time)
        {
            _currentTime = time;
        }

        // ========================================================================================
        //  基础绘制API
        // ========================================================================================

        /// <summary>画一条线。</summary>
        public void DrawLine(Vector3 a, Vector3 b, Vector4 color,
            DebugRenderLayer layer = DebugRenderLayer.Default,
            float width = 0f, bool depthTest = true, int userData = 0)
        {
            if (!_config.Enable || !_inFrame) return;

            var prim = new DebugPrimitive
            {
                Type = DebugPrimitiveType.Line,
                Layer = layer,
                V0 = new DebugVertex { Position = a, Color = color },
                V1 = new DebugVertex { Position = b, Color = color },
                Size = width > 0 ? width : _config.LineWidth,
                DepthTest = depthTest,
                DepthBias = _config.DepthBias,
                UserData = userData,
                Timestamp = _currentTime,
            };
            _primitives.Add(prim);
        }

        /// <summary>画一条带渐变色的线。</summary>
        public void DrawLineGradient(Vector3 a, Vector3 b, Vector4 colorA, Vector4 colorB,
            DebugRenderLayer layer = DebugRenderLayer.Default,
            float width = 0f, bool depthTest = true)
        {
            if (!_config.Enable || !_inFrame) return;

            var prim = new DebugPrimitive
            {
                Type = DebugPrimitiveType.Line,
                Layer = layer,
                V0 = new DebugVertex { Position = a, Color = colorA },
                V1 = new DebugVertex { Position = b, Color = colorB },
                Size = width > 0 ? width : _config.LineWidth,
                DepthTest = depthTest,
                DepthBias = _config.DepthBias,
                Timestamp = _currentTime,
            };
            _primitives.Add(prim);
        }

        /// <summary>画一个点。</summary>
        public void DrawPoint(Vector3 pos, Vector4 color, float size = 0f,
            DebugRenderLayer layer = DebugRenderLayer.Default, bool depthTest = true)
        {
            if (!_config.Enable || !_inFrame) return;

            var prim = new DebugPrimitive
            {
                Type = DebugPrimitiveType.Point,
                Layer = layer,
                V0 = new DebugVertex { Position = pos, Color = color },
                Size = size > 0 ? size : _config.ContactPointSize,
                DepthTest = depthTest,
                DepthBias = _config.DepthBias,
                Timestamp = _currentTime,
            };
            _primitives.Add(prim);
        }

        /// <summary>画一个三角形(线框)。</summary>
        public void DrawTriangle(Vector3 a, Vector3 b, Vector3 c, Vector4 color,
            DebugRenderLayer layer = DebugRenderLayer.Default,
            bool depthTest = true)
        {
            DrawLine(a, b, color, layer, 0, depthTest);
            DrawLine(b, c, color, layer, 0, depthTest);
            DrawLine(c, a, color, layer, 0, depthTest);
        }

        /// <summary>画一个填充三角形。</summary>
        public void DrawTriangleFilled(Vector3 a, Vector3 b, Vector3 c, Vector4 color,
            DebugRenderLayer layer = DebugRenderLayer.Default,
            bool depthTest = true)
        {
            if (!_config.Enable || !_inFrame) return;

            var prim = new DebugPrimitive
            {
                Type = DebugPrimitiveType.Triangle,
                Layer = layer,
                V0 = new DebugVertex { Position = a, Color = color },
                V1 = new DebugVertex { Position = b, Color = color },
                V2 = new DebugVertex { Position = c, Color = color },
                DepthTest = depthTest,
                DepthBias = _config.DepthBias,
                Timestamp = _currentTime,
            };
            _primitives.Add(prim);
        }

        /// <summary>画一个十字(标记位置)。</summary>
        public void DrawCross(Vector3 pos, float size, Vector4 color,
            DebugRenderLayer layer = DebugRenderLayer.Default, bool depthTest = true)
        {
            float s = size * 0.5f;
            DrawLine(pos + new Vector3(-s, 0, 0), pos + new Vector3(s, 0, 0), color, layer, 0, depthTest);
            DrawLine(pos + new Vector3(0, -s, 0), pos + new Vector3(0, s, 0), color, layer, 0, depthTest);
            DrawLine(pos + new Vector3(0, 0, -s), pos + new Vector3(0, 0, s), color, layer, 0, depthTest);
        }

        /// <summary>画一个箭头。</summary>
        public void DrawArrow(Vector3 start, Vector3 end, Vector4 color,
            DebugRenderLayer layer = DebugRenderLayer.Default,
            float headSize = 0f, bool depthTest = true)
        {
            if (!_config.Enable || !_inFrame) return;

            float hs = headSize > 0 ? headSize : _config.ArrowHeadSize;

            // 主线
            DrawLine(start, end, color, layer, 0, depthTest);

            // 箭头头部
            Vector3 dir = end - start;
            float len = dir.Length();
            if (len < 1e-5f) return;
            dir /= len;

            // 找两个垂直于dir的轴
            Vector3 up = Math.Abs(dir.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
            Vector3 right = Vector3.Normalize(Vector3.Cross(dir, up));
            up = Vector3.Normalize(Vector3.Cross(right, dir));

            Vector3 headBase = end - dir * hs;
            DrawLine(headBase + right * hs * 0.5f, end, color, layer, 0, depthTest);
            DrawLine(headBase - right * hs * 0.5f, end, color, layer, 0, depthTest);
            DrawLine(headBase + up * hs * 0.5f, end, color, layer, 0, depthTest);
            DrawLine(headBase - up * hs * 0.5f, end, color, layer, 0, depthTest);
        }

        /// <summary>画一个圆(在指定平面上)。</summary>
        public void DrawCircle(Vector3 center, Vector3 normal, float radius, Vector4 color,
            int segments = 32,
            DebugRenderLayer layer = DebugRenderLayer.Default, bool depthTest = true)
        {
            if (!_config.Enable || !_inFrame) return;
            if (radius <= 0) return;
            if (segments < 4) segments = 4;
            if (segments > 128) segments = 128;

            // 构造正交基
            Vector3 n = Vector3.Normalize(normal);
            Vector3 ref_ = Math.Abs(n.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
            Vector3 u = Vector3.Normalize(Vector3.Cross(n, ref_));
            Vector3 v = Vector3.Normalize(Vector3.Cross(n, u));

            float angleStep = MathF.PI * 2f / segments;
            Vector3 prev = center + (u * MathF.Cos(0) + v * MathF.Sin(0)) * radius;
            for (int i = 1; i <= segments; i++)
            {
                float a = i * angleStep;
                Vector3 cur = center + (u * MathF.Cos(a) + v * MathF.Sin(a)) * radius;
                DrawLine(prev, cur, color, layer, 0, depthTest);
                prev = cur;
            }
        }

        /// <summary>画一个3D文字。</summary>
        public void DrawText3D(Vector3 pos, string text, Vector4 color,
            float size = 0f,
            DebugRenderLayer layer = DebugRenderLayer.Default, float lifetime = 0f)
        {
            if (!_config.Enable || !_inFrame) return;
            if (string.IsNullOrEmpty(text)) return;

            var dt = new DebugText
            {
                Position = pos,
                Text = text,
                Color = color,
                Size = size > 0 ? size : _config.TextSize,
                Layer = layer,
                WorldSpace = true,
                Timestamp = _currentTime,
                Lifetime = lifetime,
            };
            _texts.Add(dt);
        }

        /// <summary>画一个屏幕空间文本。</summary>
        public void DrawTextScreen(Vector2 screenPos, string text, Vector4 color,
            float size = 0f,
            DebugRenderLayer layer = DebugRenderLayer.Default)
        {
            if (!_config.Enable || !_inFrame) return;
            if (string.IsNullOrEmpty(text)) return;

            var dt = new DebugText
            {
                ScreenPos = screenPos,
                Text = text,
                Color = color,
                Size = size > 0 ? size : _config.TextSize,
                Layer = layer,
                WorldSpace = false,
                Timestamp = _currentTime,
                Lifetime = 0f,
            };
            _texts.Add(dt);
        }

        // ========================================================================================
        //  复合图形
        // ========================================================================================

        /// <summary>画一个AABB(轴对齐包围盒)。</summary>
        public void DrawAABB(Vector3 min, Vector3 max, Vector4 color,
            DebugRenderLayer layer = DebugRenderLayer.Default, bool depthTest = true)
        {
            // 8个顶点
            _boxCorners[0] = new Vector3(min.X, min.Y, min.Z);
            _boxCorners[1] = new Vector3(max.X, min.Y, min.Z);
            _boxCorners[2] = new Vector3(max.X, max.Y, min.Z);
            _boxCorners[3] = new Vector3(min.X, max.Y, min.Z);
            _boxCorners[4] = new Vector3(min.X, min.Y, max.Z);
            _boxCorners[5] = new Vector3(max.X, min.Y, max.Z);
            _boxCorners[6] = new Vector3(max.X, max.Y, max.Z);
            _boxCorners[7] = new Vector3(min.X, max.Y, max.Z);

            // 底面
            DrawLine(_boxCorners[0], _boxCorners[1], color, layer, 0, depthTest);
            DrawLine(_boxCorners[1], _boxCorners[2], color, layer, 0, depthTest);
            DrawLine(_boxCorners[2], _boxCorners[3], color, layer, 0, depthTest);
            DrawLine(_boxCorners[3], _boxCorners[0], color, layer, 0, depthTest);
            // 顶面
            DrawLine(_boxCorners[4], _boxCorners[5], color, layer, 0, depthTest);
            DrawLine(_boxCorners[5], _boxCorners[6], color, layer, 0, depthTest);
            DrawLine(_boxCorners[6], _boxCorners[7], color, layer, 0, depthTest);
            DrawLine(_boxCorners[7], _boxCorners[4], color, layer, 0, depthTest);
            // 竖边
            DrawLine(_boxCorners[0], _boxCorners[4], color, layer, 0, depthTest);
            DrawLine(_boxCorners[1], _boxCorners[5], color, layer, 0, depthTest);
            DrawLine(_boxCorners[2], _boxCorners[6], color, layer, 0, depthTest);
            DrawLine(_boxCorners[3], _boxCorners[7], color, layer, 0, depthTest);
        }

        /// <summary>画一个OBB(有向包围盒)。</summary>
        public void DrawOBB(Vector3 center, Quaternion rotation, Vector3 halfExtents, Vector4 color,
            DebugRenderLayer layer = DebugRenderLayer.Default, bool depthTest = true)
        {
            // 8个局部顶点
            Vector3 h = halfExtents;
            _boxCorners[0] = new Vector3(-h.X, -h.Y, -h.Z);
            _boxCorners[1] = new Vector3(+h.X, -h.Y, -h.Z);
            _boxCorners[2] = new Vector3(+h.X, +h.Y, -h.Z);
            _boxCorners[3] = new Vector3(-h.X, +h.Y, -h.Z);
            _boxCorners[4] = new Vector3(-h.X, -h.Y, +h.Z);
            _boxCorners[5] = new Vector3(+h.X, -h.Y, +h.Z);
            _boxCorners[6] = new Vector3(+h.X, +h.Y, +h.Z);
            _boxCorners[7] = new Vector3(-h.X, +h.Y, +h.Z);

            // 变换到世界
            for (int i = 0; i < 8; i++)
            {
                _boxCorners[i] = center + Vector3.Transform(_boxCorners[i], rotation);
            }

            DrawLine(_boxCorners[0], _boxCorners[1], color, layer, 0, depthTest);
            DrawLine(_boxCorners[1], _boxCorners[2], color, layer, 0, depthTest);
            DrawLine(_boxCorners[2], _boxCorners[3], color, layer, 0, depthTest);
            DrawLine(_boxCorners[3], _boxCorners[0], color, layer, 0, depthTest);
            DrawLine(_boxCorners[4], _boxCorners[5], color, layer, 0, depthTest);
            DrawLine(_boxCorners[5], _boxCorners[6], color, layer, 0, depthTest);
            DrawLine(_boxCorners[6], _boxCorners[7], color, layer, 0, depthTest);
            DrawLine(_boxCorners[7], _boxCorners[4], color, layer, 0, depthTest);
            DrawLine(_boxCorners[0], _boxCorners[4], color, layer, 0, depthTest);
            DrawLine(_boxCorners[1], _boxCorners[5], color, layer, 0, depthTest);
            DrawLine(_boxCorners[2], _boxCorners[6], color, layer, 0, depthTest);
            DrawLine(_boxCorners[3], _boxCorners[7], color, layer, 0, depthTest);
        }

        /// <summary>画一个球体(经纬线)。</summary>
        public void DrawSphere(Vector3 center, float radius, Vector4 color,
            int segments = 16,
            DebugRenderLayer layer = DebugRenderLayer.Default, bool depthTest = true)
        {
            if (radius <= 0) return;
            if (segments < 4) segments = 4;
            if (segments > 64) segments = 64;

            // 三个主平面上的圆
            DrawCircle(center, Vector3.UnitX, radius, color, segments, layer, depthTest);
            DrawCircle(center, Vector3.UnitY, radius, color, segments, layer, depthTest);
            DrawCircle(center, Vector3.UnitZ, radius, color, segments, layer, depthTest);
        }

        /// <summary>画一个胶囊体。</summary>
        public void DrawCapsule(Vector3 center, Quaternion rotation,
            float radius, float halfHeight,
            Vector4 color, int segments = 16,
            DebugRenderLayer layer = DebugRenderLayer.Default, bool depthTest = true)
        {
            if (radius <= 0) return;

            // 局部坐标系: Y轴是胶囊的高度方向
            Vector3 up = Vector3.Transform(Vector3.UnitY, rotation);
            Vector3 right = Vector3.Transform(Vector3.UnitX, rotation);
            Vector3 forward = Vector3.Transform(Vector3.UnitZ, rotation);

            Vector3 top = center + up * halfHeight;
            Vector3 bot = center - up * halfHeight;

            // 上下两个球
            DrawSphere(top, radius, color, segments, layer, depthTest);
            DrawSphere(bot, radius, color, segments, layer, depthTest);

            // 连接的圆柱(4条线)
            DrawLine(top + right * radius, bot + right * radius, color, layer, 0, depthTest);
            DrawLine(top - right * radius, bot - right * radius, color, layer, 0, depthTest);
            DrawLine(top + forward * radius, bot + forward * radius, color, layer, 0, depthTest);
            DrawLine(top - forward * radius, bot - forward * radius, color, layer, 0, depthTest);

            // 两个中点圆
            DrawCircle(top, up, radius, color, segments, layer, depthTest);
            DrawCircle(bot, up, radius, color, segments, layer, depthTest);
        }

        /// <summary>画一个圆柱体。</summary>
        public void DrawCylinder(Vector3 center, Quaternion rotation,
            float radius, float halfHeight,
            Vector4 color, int segments = 16,
            DebugRenderLayer layer = DebugRenderLayer.Default, bool depthTest = true)
        {
            Vector3 up = Vector3.Transform(Vector3.UnitY, rotation);
            Vector3 right = Vector3.Transform(Vector3.UnitX, rotation);
            Vector3 forward = Vector3.Transform(Vector3.UnitZ, rotation);

            Vector3 top = center + up * halfHeight;
            Vector3 bot = center - up * halfHeight;

            DrawCircle(top, up, radius, color, segments, layer, depthTest);
            DrawCircle(bot, up, radius, color, segments, layer, depthTest);

            DrawLine(top + right * radius, bot + right * radius, color, layer, 0, depthTest);
            DrawLine(top - right * radius, bot - right * radius, color, layer, 0, depthTest);
            DrawLine(top + forward * radius, bot + forward * radius, color, layer, 0, depthTest);
            DrawLine(top - forward * radius, bot - forward * radius, color, layer, 0, depthTest);
        }

        /// <summary>画一个平面(有限大小)。</summary>
        public void DrawPlane(Vector3 point, Vector3 normal, float size, Vector4 color,
            DebugRenderLayer layer = DebugRenderLayer.Default, bool depthTest = true)
        {
            Vector3 n = Vector3.Normalize(normal);
            Vector3 ref_ = Math.Abs(n.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
            Vector3 u = Vector3.Normalize(Vector3.Cross(n, ref_));
            Vector3 v = Vector3.Normalize(Vector3.Cross(n, u));

            Vector3 c0 = point + u * size + v * size;
            Vector3 c1 = point - u * size + v * size;
            Vector3 c2 = point - u * size - v * size;
            Vector3 c3 = point + u * size - v * size;

            DrawLine(c0, c1, color, layer, 0, depthTest);
            DrawLine(c1, c2, color, layer, 0, depthTest);
            DrawLine(c2, c3, color, layer, 0, depthTest);
            DrawLine(c3, c0, color, layer, 0, depthTest);

            // 法线
            DrawArrow(point, point + n * size * 0.5f, color, layer, 0, depthTest);
        }

        /// <summary>画世界坐标轴。</summary>
        public void DrawAxes(Vector3 origin, float length = 1f, bool depthTest = true)
        {
            DrawLine(origin, origin + Vector3.UnitX * length, _config.AxisXColor,
                DebugRenderLayer.Default, 0, depthTest);
            DrawLine(origin, origin + Vector3.UnitY * length, _config.AxisYColor,
                DebugRenderLayer.Default, 0, depthTest);
            DrawLine(origin, origin + Vector3.UnitZ * length, _config.AxisZColor,
                DebugRenderLayer.Default, 0, depthTest);
        }

        /// <summary>画一个网格(XZ平面)。</summary>
        public void DrawGrid(Vector3 center, float cellSize, int extent,
            Vector4? color = null, bool depthTest = true)
        {
            Vector4 c = color ?? _config.GridColor;
            float half = cellSize * extent;

            for (int i = -extent; i <= extent; i++)
            {
                float x = center.X + i * cellSize;
                DrawLine(new Vector3(x, center.Y, center.Z - half),
                         new Vector3(x, center.Y, center.Z + half), c,
                         DebugRenderLayer.Default, 0, depthTest);

                float z = center.Z + i * cellSize;
                DrawLine(new Vector3(center.X - half, center.Y, z),
                         new Vector3(center.X + half, center.Y, z), c,
                         DebugRenderLayer.Default, 0, depthTest);
            }
        }

        /// <summary>画一个凸包(用顶点列表)。</summary>
        public void DrawConvexHull(Vector3[] vertices, Vector4 color,
            DebugRenderLayer layer = DebugRenderLayer.Default, bool depthTest = true)
        {
            if (vertices == null || vertices.Length < 4) return;

            // 简化: 画所有顶点之间的连线(会重复，但对于调试够了)
            // TODO: 应该用边列表只画边
            for (int i = 0; i < vertices.Length; i++)
            {
                for (int j = i + 1; j < vertices.Length; j++)
                {
                    // 只画距离较近的(避免画对角线)
                    float d = Vector3.Distance(vertices[i], vertices[j]);
                    // 找最近的几个作为边
                    // 这个简化做法在顶点多的时候会画很多线，实际应该用边的拓扑
                }
            }

            // 更好的做法: 找AABB然后画
            Vector3 min = vertices[0], max = vertices[0];
            foreach (var v in vertices)
            {
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }
            DrawAABB(min, max, color * 0.5f, layer, depthTest);

            // 画顶点
            foreach (var v in vertices)
            {
                DrawPoint(v, color, 4f, layer, depthTest);
            }
        }

        /// <summary>画一条折线。</summary>
        public void DrawLineStrip(Vector3[] points, Vector4 color, bool closed = false,
            DebugRenderLayer layer = DebugRenderLayer.Default, bool depthTest = true)
        {
            if (points == null || points.Length < 2) return;

            for (int i = 0; i < points.Length - 1; i++)
            {
                DrawLine(points[i], points[i + 1], color, layer, 0, depthTest);
            }
            if (closed && points.Length > 2)
            {
                DrawLine(points[^1], points[0], color, layer, 0, depthTest);
            }
        }

        // ========================================================================================
        //  Persistent 绘制 (跨帧保留)
        // ========================================================================================

        /// <summary>添加一个persistent线(会在多帧内显示，直到过期)。</summary>
        public void AddPersistentLine(Vector3 a, Vector3 b, Vector4 color, float lifetime,
            DebugRenderLayer layer = DebugRenderLayer.Default)
        {
            if (!_config.Enable) return;

            var prim = new DebugPrimitive
            {
                Type = DebugPrimitiveType.Line,
                Layer = layer,
                V0 = new DebugVertex { Position = a, Color = color },
                V1 = new DebugVertex { Position = b, Color = color },
                Size = _config.LineWidth,
                DepthTest = _config.DepthTest,
                DepthBias = _config.DepthBias,
                Timestamp = _currentTime + lifetime,
            };
            _persistentPrimitives.Add(prim);
        }

        /// <summary>添加一个persistent点。</summary>
        public void AddPersistentPoint(Vector3 pos, Vector4 color, float lifetime, float size = 0f,
            DebugRenderLayer layer = DebugRenderLayer.Default)
        {
            if (!_config.Enable) return;

            var prim = new DebugPrimitive
            {
                Type = DebugPrimitiveType.Point,
                Layer = layer,
                V0 = new DebugVertex { Position = pos, Color = color },
                Size = size > 0 ? size : _config.ContactPointSize,
                DepthTest = _config.DepthTest,
                Timestamp = _currentTime + lifetime,
            };
            _persistentPrimitives.Add(prim);
        }

        /// <summary>添加一个persistent文本。</summary>
        public void AddPersistentText(Vector3 pos, string text, Vector4 color, float lifetime,
            DebugRenderLayer layer = DebugRenderLayer.Default)
        {
            if (!_config.Enable || string.IsNullOrEmpty(text)) return;

            _persistentTexts.Add(new DebugText
            {
                Position = pos,
                Text = text,
                Color = color,
                Size = _config.TextSize,
                Layer = layer,
                WorldSpace = true,
                Timestamp = _currentTime + lifetime,
                Lifetime = lifetime,
            });
        }

        /// <summary>清空所有persistent元素。</summary>
        public void ClearPersistent()
        {
            _persistentPrimitives.Clear();
            _persistentTexts.Clear();
        }

        private void CleanupPersistent()
        {
            // 清理过期的persistent元素
            for (int i = _persistentPrimitives.Count - 1; i >= 0; i--)
            {
                if (_persistentPrimitives[i].Timestamp <= _currentTime)
                {
                    _persistentPrimitives.RemoveAt(i);
                }
            }
            for (int i = _persistentTexts.Count - 1; i >= 0; i--)
            {
                if (_persistentTexts[i].Timestamp <= _currentTime)
                {
                    _persistentTexts.RemoveAt(i);
                }
            }
        }

        // ========================================================================================
        //  警告系统
        // ========================================================================================

        /// <summary>记录一个警告。</summary>
        public void LogWarning(string message, Vector3? position = null)
        {
            if (!_config.ShowWarnings) return;
            _warnings.Add((message, _currentTime + _warningLifetime, position));
        }

        /// <summary>设置警告显示时长。</summary>
        public void SetWarningLifetime(float seconds)
        {
            _warningLifetime = MathF.Max(0.5f, seconds);
        }

        private void CleanupWarnings()
        {
            for (int i = _warnings.Count - 1; i >= 0; i--)
            {
                if (_warnings[i].time <= _currentTime)
                {
                    _warnings.RemoveAt(i);
                }
            }
        }

        /// <summary>绘制所有警告。</summary>
        public void DrawWarnings(Vector2 screenStart)
        {
            if (!_config.ShowWarnings || _warnings.Count == 0) return;

            float y = screenStart.Y;
            foreach (var w in _warnings)
            {
                DrawTextScreen(new Vector2(screenStart.X, y), w.msg, _config.WarningColor,
                    size: _config.TextSize, layer: DebugRenderLayer.Stats);
                y += _config.TextSize + 2;
            }
        }

        public int WarningCount => _warnings.Count;

        // ========================================================================================
        //  选中body
        // ========================================================================================

        /// <summary>选中一个body。</summary>
        public void SelectBody(int bodyId, SelectedBodyInfo info)
        {
            if (info == null) return;
            _selectedBodies[bodyId] = info;
        }

        /// <summary>取消选中。</summary>
        public void DeselectBody(int bodyId)
        {
            _selectedBodies.Remove(bodyId);
        }

        /// <summary>清空所有选中。</summary>
        public void ClearSelection()
        {
            _selectedBodies.Clear();
        }

        /// <summary>绘制选中body的详细信息。</summary>
        public void DrawSelectedBodyInfo(Vector2 screenStart)
        {
            if (_selectedBodies.Count == 0) return;

            float y = screenStart.Y;
            float x = screenStart.X;
            float lineH = _config.TextSize + 2;

            foreach (var kvp in _selectedBodies)
            {
                var info = kvp.Value;
                DrawTextScreen(new Vector2(x, y), $"=== Body #{info.BodyId} ===",
                    DebugColors.Yellow, _config.TextSize, DebugRenderLayer.Stats);
                y += lineH;

                DrawTextScreen(new Vector2(x, y),
                    $"Pos: ({info.Position.X:F2}, {info.Position.Y:F2}, {info.Position.Z:F2})",
                    DebugColors.White, _config.TextSize, DebugRenderLayer.Stats);
                y += lineH;

                DrawTextScreen(new Vector2(x, y),
                    $"Vel: ({info.Velocity.X:F2}, {info.Velocity.Y:F2}, {info.Velocity.Z:F2}) " +
                    $"|{info.Velocity.Length():F2} m/s",
                    DebugColors.Green, _config.TextSize, DebugRenderLayer.Stats);
                y += lineH;

                DrawTextScreen(new Vector2(x, y),
                    $"AngVel: ({info.AngularVelocity.X:F2}, {info.AngularVelocity.Y:F2}, {info.AngularVelocity.Z:F2})",
                    DebugColors.Cyan, _config.TextSize, DebugRenderLayer.Stats);
                y += lineH;

                DrawTextScreen(new Vector2(x, y),
                    $"Mass: {info.Mass:F3} kg  InvMass: {info.InverseMass:F4}",
                    DebugColors.White, _config.TextSize, DebugRenderLayer.Stats);
                y += lineH;

                DrawTextScreen(new Vector2(x, y),
                    $"State: {info.State}  Layer: {info.CollisionLayer}  Island: {info.IslandId}",
                    DebugColors.White, _config.TextSize, DebugRenderLayer.Stats);
                y += lineH;

                DrawTextScreen(new Vector2(x, y),
                    $"Shape: {info.ShapeName}  Contacts: {info.ContactCount}  Constraints: {info.ConstraintCount}",
                    DebugColors.White, _config.TextSize, DebugRenderLayer.Stats);
                y += lineH;

                DrawTextScreen(new Vector2(x, y),
                    $"Sleep: {info.SleepTimer:F2}s  CCD: {info.UseCCD}  Trigger: {info.IsTrigger}",
                    DebugColors.White, _config.TextSize, DebugRenderLayer.Stats);
                y += lineH;

                if (info.Force.LengthSquared() > 0.001f)
                {
                    DrawTextScreen(new Vector2(x, y),
                        $"Force: ({info.Force.X:F2}, {info.Force.Y:F2}, {info.Force.Z:F2})",
                        DebugColors.Orange, _config.TextSize, DebugRenderLayer.Stats);
                    y += lineH;
                }

                if (info.Torque.LengthSquared() > 0.001f)
                {
                    DrawTextScreen(new Vector2(x, y),
                        $"Torque: ({info.Torque.X:F2}, {info.Torque.Y:F2}, {info.Torque.Z:F2})",
                        DebugColors.Purple, _config.TextSize, DebugRenderLayer.Stats);
                    y += lineH;
                }

                y += lineH; // 空行
            }
        }

        // ========================================================================================
        //  统计信息绘制
        // ========================================================================================

        /// <summary>更新统计快照。</summary>
        public void UpdateStats(DebugStatsSnapshot stats)
        {
            _lastStats = stats;
        }

        /// <summary>绘制统计信息(左上角)。</summary>
        public void DrawStats(Vector2 screenStart)
        {
            if (!_config.ShowStatistics) return;

            _statsBuilder.Clear();
            _statsBuilder.AppendLine("=== Physics Stats ===");
            _statsBuilder.AppendLine($"Frame: {_lastStats.FrameNumber}  FPS: {_lastStats.Fps:F1}");
            _statsBuilder.AppendLine($"Bodies: {_lastStats.BodyCount} (Active: {_lastStats.ActiveBodyCount}, " +
                                     $"Sleep: {_lastStats.SleepingBodyCount}, Static: {_lastStats.StaticBodyCount})");
            _statsBuilder.AppendLine($"Contacts: {_lastStats.ContactCount}  " +
                                     $"Constraints: {_lastStats.ConstraintCount}  " +
                                     $"Islands: {_lastStats.IslandCount}");
            _statsBuilder.AppendLine($"Broadphase pairs: {_lastStats.BroadphasePairCount}  " +
                                     $"Narrowphase: {_lastStats.NarrowphasePairCount}");
            _statsBuilder.AppendLine($"Step: {_lastStats.StepTimeMs:F2} ms " +
                                     $"(BP: {_lastStats.BroadphaseTimeMs:F2}, " +
                                     $"NP: {_lastStats.NarrowphaseTimeMs:F2}, " +
                                     $"Solver: {_lastStats.SolverTimeMs:F2}, " +
                                     $"Int: {_lastStats.IntegrateTimeMs:F2})");
            _statsBuilder.AppendLine($"CCD checks: {_lastStats.CCDCheckCount}");
            _statsBuilder.AppendLine($"Total mass: {_lastStats.TotalMass:F1} kg  " +
                                     $"Avg speed: {_lastStats.AvgSpeed:F2} m/s  " +
                                     $"Max speed: {_lastStats.MaxSpeed:F2} m/s");
            _statsBuilder.AppendLine($"Memory: {_lastStats.MemoryUsageBytes / 1024.0 / 1024.0:F2} MB");
            _statsBuilder.AppendLine($"Drawn: {_lastStats.DrawnPrimitives} prims, " +
                                     $"{_lastStats.DrawnTexts} texts");

            float y = screenStart.Y;
            foreach (var line in _statsBuilder.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                DrawTextScreen(new Vector2(screenStart.X, y), line,
                    DebugColors.White, _config.TextSize, DebugRenderLayer.Stats);
                y += _config.TextSize + 2;
            }
        }

        // ========================================================================================
        //  物理世界绘制(高级API)
        // ========================================================================================

        /// <summary>
        /// 绘制整个物理世界。会根据Config的开关决定画哪些东西。
        /// 这是给开发者用的"一键可视化"接口。
        /// </summary>
        public void DrawWorld(IPhysicsDebugDataProvider world)
        {
            if (!_config.Enable || !_inFrame || world == null) return;

            // 1. 画坐标轴
            if (_config.ShowAxes)
            {
                DrawAxes(Vector3.Zero, _config.AxisLength);
            }

            // 2. 画网格
            if (_config.ShowGrid)
            {
                DrawGrid(Vector3.Zero, _config.GridSize, _config.GridExtent);
            }

            // 3. 画所有body
            var bodies = world.GetBodies();
            foreach (var body in bodies)
            {
                // 筛选
                if (_config.FilterBodyId != 0 && body.BodyId != _config.FilterBodyId)
                    continue;
                if (_config.FilterLayer >= 0 && body.CollisionLayer != _config.FilterLayer)
                    continue;

                DrawBody(body);
            }

            // 4. 画接触点
            if (_config.ShowContacts)
            {
                var contacts = world.GetContacts();
                foreach (var c in contacts)
                {
                    DrawContact(c);
                }
            }

            // 5. 画约束
            if (_config.ShowConstraints)
            {
                var constraints = world.GetConstraints();
                foreach (var con in constraints)
                {
                    DrawConstraint(con);
                }
            }

            // 6. 画岛屿边界
            if (_config.ShowIslands)
            {
                var islands = world.GetIslands();
                foreach (var island in islands)
                {
                    DrawIsland(island);
                }
            }

            // 7. 画射线检测
            if (_config.ShowRaycasts)
            {
                var raycasts = world.GetRecentRaycasts();
                foreach (var r in raycasts)
                {
                    DrawRaycast(r);
                }
            }

            // 8. 画宽相位pair
            if (_config.ShowBroadphasePairs)
            {
                var pairs = world.GetBroadphasePairs();
                foreach (var p in pairs)
                {
                    DrawBroadphasePair(p);
                }
            }
        }

        /// <summary>绘制单个body的调试信息。</summary>
        public void DrawBody(BodyDebugData body)
        {
            Vector4 shapeColor;

            // 按状态着色
            if (_selectedBodies.ContainsKey(body.BodyId))
            {
                shapeColor = _config.ShapeColorSelected;
            }
            else if (_config.ColorByIsland && body.IslandId >= 0)
            {
                shapeColor = GetIslandColor(body.IslandId);
            }
            else if (_config.ColorBySpeed)
            {
                float t = Math.Clamp(body.Velocity.Length() / _config.SpeedColorMax, 0, 1);
                shapeColor = new Vector4(t, 1 - t, 0, 1); // 红到绿
            }
            else if (_config.ColorByMass)
            {
                float t = Math.Clamp(body.Mass / _config.MassColorMax, 0, 1);
                shapeColor = new Vector4(0, t, 1 - t, 1);
            }
            else
            {
                shapeColor = body.State switch
                {
                    BodyDebugState.Sleeping => _config.ShapeColorSleeping,
                    BodyDebugState.Kinematic => _config.ShapeColorKinematic,
                    BodyDebugState.Static => _config.ShapeColorStatic,
                    BodyDebugState.Trigger => _config.ShapeColorTrigger,
                    _ => _config.ShapeColor,
                };
            }

            // 按开关筛选
            bool shouldDraw = body.State switch
            {
                BodyDebugState.Sleeping => _config.ShowSleepingBodies,
                BodyDebugState.Static => _config.ShowShapes,
                BodyDebugState.Kinematic => _config.ShowShapes,
                BodyDebugState.Trigger => _config.ShowShapes,
                BodyDebugState.Active => _config.ShowActiveBodies,
                _ => true,
            };

            if (!shouldDraw) return;

            // 画形状线框
            if (_config.ShowShapes)
            {
                DrawShapeWireframe(body, shapeColor);
            }

            // 画AABB
            if (_config.ShowAABBs)
            {
                DrawAABB(body.AABBMin, body.AABBMax, _config.AABBColor,
                    DebugRenderLayer.Bounds, _config.DepthTest);
            }

            // 画质心
            if (_config.ShowCenterOfMass)
            {
                DrawCross(body.CenterOfMass, 0.1f, _config.COMColor,
                    DebugRenderLayer.Default, _config.DepthTest);
                DrawPoint(body.CenterOfMass, _config.COMColor, _config.COMPointSize,
                    DebugRenderLayer.Default, _config.DepthTest);
            }

            // 画惯性主轴
            if (_config.ShowInertiaAxes)
            {
                DrawInertiaAxes(body, _config.InertiaAxisColor);
            }

            // 画速度向量
            if (_config.ShowVelocities && body.Velocity.LengthSquared() > _config.MinVelocity * _config.MinVelocity)
            {
                Vector3 velEnd = body.Position + body.Velocity * _config.VelocityScale;
                DrawArrow(body.Position, velEnd, _config.VelocityColor,
                    DebugRenderLayer.Velocities, 0, _config.DepthTest);
            }

            // 画角速度向量
            if (_config.ShowAngularVelocities && body.AngularVelocity.LengthSquared() > 0.01f)
            {
                Vector3 angEnd = body.Position + body.AngularVelocity * _config.AngularVelocityScale;
                DrawArrow(body.Position, angEnd, _config.AngularVelocityColor,
                    DebugRenderLayer.Velocities, 0, _config.DepthTest);
            }

            // 画力
            if (_config.ShowForces && body.Force.LengthSquared() > _config.MinForce * _config.MinForce)
            {
                Vector3 fEnd = body.Position + body.Force * _config.ForceScale;
                DrawArrow(body.Position, fEnd, _config.ForceColor,
                    DebugRenderLayer.Forces, 0, _config.DepthTest);
            }

            // 画扭矩
            if (_config.ShowTorques && body.Torque.LengthSquared() > 0.01f)
            {
                Vector3 tEnd = body.Position + body.Torque * _config.ForceScale;
                DrawArrow(body.Position, tEnd, _config.TorqueColor,
                    DebugRenderLayer.Forces, 0, _config.DepthTest);
            }

            // 画body信息文本
            if (_config.ShowBodyInfo)
            {
                string info = $"#{body.BodyId} m={body.Mass:F1} v={body.Velocity.Length():F2}";
                DrawText3D(body.Position + Vector3.UnitY * 0.5f, info, shapeColor,
                    _config.TextSize * 0.8f, DebugRenderLayer.Stats, 0f);
            }
        }

        /// <summary>绘制形状线框。</summary>
        public void DrawShapeWireframe(BodyDebugData body, Vector4 color)
        {
            switch (body.ShapeType)
            {
                case DebugShapeType.Box:
                    DrawOBB(body.Position, body.Orientation, body.BoxHalfExtents, color,
                        DebugRenderLayer.Shapes, _config.DepthTest);
                    break;

                case DebugShapeType.Sphere:
                    DrawSphere(body.Position, body.SphereRadius, color, 16,
                        DebugRenderLayer.Shapes, _config.DepthTest);
                    break;

                case DebugShapeType.Capsule:
                    DrawCapsule(body.Position, body.Orientation,
                        body.CapsuleRadius, body.CapsuleHalfHeight, color, 16,
                        DebugRenderLayer.Shapes, _config.DepthTest);
                    break;

                case DebugShapeType.Cylinder:
                    DrawCylinder(body.Position, body.Orientation,
                        body.CylinderRadius, body.CylinderHalfHeight, color, 16,
                        DebugRenderLayer.Shapes, _config.DepthTest);
                    break;

                case DebugShapeType.ConvexHull:
                    if (body.ConvexVertices != null)
                    {
                        // 变换到世界坐标
                        var worldVerts = new Vector3[body.ConvexVertices.Length];
                        for (int i = 0; i < body.ConvexVertices.Length; i++)
                        {
                            worldVerts[i] = body.Position +
                                Vector3.Transform(body.ConvexVertices[i], body.Orientation);
                        }
                        DrawConvexHull(worldVerts, color, DebugRenderLayer.Shapes, _config.DepthTest);
                    }
                    break;

                case DebugShapeType.Mesh:
                    // 网格只画AABB
                    DrawAABB(body.AABBMin, body.AABBMax, color * 0.7f,
                        DebugRenderLayer.Shapes, _config.DepthTest);
                    break;

                case DebugShapeType.Plane:
                    DrawPlane(body.Position, body.PlaneNormal, 5f, color,
                        DebugRenderLayer.Shapes, _config.DepthTest);
                    break;

                case DebugShapeType.Compound:
                    // 画AABB + 子形状数量
                    DrawAABB(body.AABBMin, body.AABBMax, color,
                        DebugRenderLayer.Shapes, _config.DepthTest);
                    DrawText3D(body.Position, $"Compound[{body.SubShapeCount}]",
                        color, _config.TextSize * 0.7f, DebugRenderLayer.Stats, 0f);
                    break;

                default:
                    // 未知形状，画个AABB
                    DrawAABB(body.AABBMin, body.AABBMax, color,
                        DebugRenderLayer.Shapes, _config.DepthTest);
                    break;
            }
        }

        /// <summary>画惯性主轴。</summary>
        public void DrawInertiaAxes(BodyDebugData body, Vector4 color)
        {
            // 惯性张量的特征向量就是主轴
            // 这里简化: 用body的orientation的三个轴
            Vector3 xAxis = Vector3.Transform(Vector3.UnitX, body.Orientation);
            Vector3 yAxis = Vector3.Transform(Vector3.UnitY, body.Orientation);
            Vector3 zAxis = Vector3.Transform(Vector3.UnitZ, body.Orientation);

            float len = 0.5f;
            DrawArrow(body.CenterOfMass, body.CenterOfMass + xAxis * len, _config.AxisXColor);
            DrawArrow(body.CenterOfMass, body.CenterOfMass + yAxis * len, _config.AxisYColor);
            DrawArrow(body.CenterOfMass, body.CenterOfMass + zAxis * len, _config.AxisZColor);
        }

        /// <summary>绘制接触点。</summary>
        public void DrawContact(ContactDebugData contact)
        {
            // 冲量太小就跳过
            if (contact.NormalImpulse < _config.MinContactImpulse) return;

            // 画接触点
            float severity = Math.Clamp(contact.NormalImpulse / 10f, 0, 1);
            Vector4 color = new(severity, 1 - severity, 0, 1);

            DrawPoint(contact.Point, color, _config.ContactPointSize,
                DebugRenderLayer.Contacts, _config.DepthTest);

            // 画法线
            if (_config.ShowContactNormals)
            {
                Vector3 normalEnd = contact.Point + contact.Normal * _config.NormalLength;
                DrawArrow(contact.Point, normalEnd, _config.ContactNormalColor,
                    DebugRenderLayer.Contacts, 0, _config.DepthTest);
            }

            // 画冲量
            if (_config.ShowContactImpulses && contact.NormalImpulse > 0.01f)
            {
                Vector3 impEnd = contact.Point + contact.Normal * contact.NormalImpulse * 0.1f;
                DrawArrow(contact.Point, impEnd, _config.ForceColor,
                    DebugRenderLayer.Contacts, 0, _config.DepthTest);

                string label = $"n={contact.NormalImpulse:F2}";
                if (contact.TangentImpulse.LengthSquared() > 0.01f)
                {
                    label += $" t={contact.TangentImpulse.Length():F2}";
                }
                DrawText3D(contact.Point + Vector3.UnitY * 0.1f, label, color,
                    _config.TextSize * 0.7f, DebugRenderLayer.Contacts, 0f);
            }

            // 持续显示
            if (_config.PersistentContacts)
            {
                AddPersistentPoint(contact.Point, color, _config.ContactLifetime);
            }
        }

        /// <summary>绘制约束。</summary>
        public void DrawConstraint(ConstraintDebugData constraint)
        {
            Vector3 a = constraint.AnchorA;
            Vector3 b = constraint.AnchorB;

            // 画连接线
            DrawLine(a, b, _config.ConstraintColor,
                DebugRenderLayer.Constraints, 0, _config.DepthTest);

            // 画两端的锚点
            DrawPoint(a, _config.ConstraintColor, _config.ContactPointSize,
                DebugRenderLayer.Constraints, _config.DepthTest);
            DrawPoint(b, _config.ConstraintColor, _config.ContactPointSize,
                DebugRenderLayer.Constraints, _config.DepthTest);

            // 画约束限制(角度/距离)
            if (_config.ShowConstraintLimits)
            {
                switch (constraint.Type)
                {
                    case DebugConstraintType.Distance:
                        // 画最小/最大距离的圆
                        if (constraint.MinLimit > 0)
                        {
                            DrawCircle(a, Vector3.UnitY, constraint.MinLimit,
                                _config.ConstraintColor * 0.3f, 16,
                                DebugRenderLayer.Constraints, _config.DepthTest);
                        }
                        if (constraint.MaxLimit > 0)
                        {
                            DrawCircle(a, Vector3.UnitY, constraint.MaxLimit,
                                _config.ConstraintColor * 0.5f, 16,
                                DebugRenderLayer.Constraints, _config.DepthTest);
                        }
                        break;

                    case DebugConstraintType.Hinge:
                        // 画铰链的旋转轴
                        Vector3 axis = constraint.AxisA;
                        DrawArrow(a - axis * 0.3f, a + axis * 0.3f, _config.ConstraintColor,
                            DebugRenderLayer.Constraints, 0, _config.DepthTest);

                        // 画角度限制
                        if (constraint.MinLimit > -MathF.PI && constraint.MaxLimit < MathF.PI)
                        {
                            // 画个扇形
                            float minA = constraint.MinLimit;
                            float maxA = constraint.MaxLimit;
                            Vector3 perp1 = Vector3.Normalize(Vector3.Cross(axis, Vector3.UnitY));
                            if (perp1.LengthSquared() < 0.01f)
                                perp1 = Vector3.Normalize(Vector3.Cross(axis, Vector3.UnitX));
                            Vector3 perp2 = Vector3.Normalize(Vector3.Cross(axis, perp1));

                            float r = 0.3f;
                            int steps = 16;
                            Vector3 prev = a;
                            for (int i = 0; i <= steps; i++)
                            {
                                float t = (float)i / steps;
                                float ang = minA + (maxA - minA) * t;
                                Vector3 dir = perp1 * MathF.Cos(ang) + perp2 * MathF.Sin(ang);
                                Vector3 cur = a + dir * r;
                                if (i > 0)
                                {
                                    DrawLine(prev, cur, _config.ConstraintColor * 0.5f,
                                        DebugRenderLayer.Constraints, 0, _config.DepthTest);
                                }
                                prev = cur;
                            }
                        }
                        break;

                    case DebugConstraintType.Cone:
                        // 画圆锥限制
                        DrawCircle(a, constraint.AxisA, constraint.MaxLimit,
                            _config.ConstraintColor * 0.5f, 24,
                            DebugRenderLayer.Constraints, _config.DepthTest);
                        break;

                    case DebugConstraintType.Ball:
                        // 球关节: 画个球
                        DrawSphere(a, 0.05f, _config.ConstraintColor, 8,
                            DebugRenderLayer.Constraints, _config.DepthTest);
                        break;
                }
            }

            // 标签
            if (_config.ShowBodyInfo)
            {
                Vector3 mid = (a + b) * 0.5f;
                DrawText3D(mid, constraint.Type.ToString(), _config.ConstraintColor,
                    _config.TextSize * 0.7f, DebugRenderLayer.Constraints, 0f);
            }
        }

        /// <summary>绘制岛屿。</summary>
        public void DrawIsland(IslandDebugData island)
        {
            Vector4 color = GetIslandColor(island.IslandId);

            // 画岛屿的AABB
            DrawAABB(island.AABBMin, island.AABBMax, color * 0.5f,
                DebugRenderLayer.Islands, _config.DepthTest);

            // 画岛屿中心
            Vector3 center = (island.AABBMin + island.AABBMax) * 0.5f;
            DrawText3D(center, $"Island#{island.IslandId} ({island.BodyCount} bodies)",
                color, _config.TextSize * 0.8f, DebugRenderLayer.Islands, 0f);

            // 如果岛屿在睡眠，加个标记
            if (island.IsSleeping)
            {
                DrawText3D(center + Vector3.UnitY * 0.3f, "[SLEEPING]",
                    DebugColors.Blue, _config.TextSize * 0.7f, DebugRenderLayer.Islands, 0f);
            }
        }

        /// <summary>绘制射线检测结果。</summary>
        public void DrawRaycast(RaycastDebugData raycast)
        {
            // 画射线
            DrawLine(raycast.Origin, raycast.End, _config.RaycastColor,
                DebugRenderLayer.Raycasts, 0, _config.DepthTest);

            if (raycast.HasHit)
            {
                // 画命中点
                DrawPoint(raycast.HitPoint, _config.RaycastHitColor, _config.ContactPointSize * 1.5f,
                    DebugRenderLayer.Raycasts, _config.DepthTest);

                // 画命中法线
                Vector3 normalEnd = raycast.HitPoint + raycast.HitNormal * _config.NormalLength;
                DrawArrow(raycast.HitPoint, normalEnd, _config.RaycastHitColor,
                    DebugRenderLayer.Raycasts, 0, _config.DepthTest);

                // 标签
                string info = $"d={raycast.HitDistance:F2}";
                DrawText3D(raycast.HitPoint + Vector3.UnitY * 0.1f, info, _config.RaycastHitColor,
                    _config.TextSize * 0.7f, DebugRenderLayer.Raycasts, 0f);
            }
        }

        /// <summary>绘制宽相位pair。</summary>
        public void DrawBroadphasePair(BroadphasePairDebugData pair)
        {
            Vector3 centerA = (pair.AABBMinA + pair.AABBMaxA) * 0.5f;
            Vector3 centerB = (pair.AABBMinB + pair.AABBMaxB) * 0.5f;
            DrawLine(centerA, centerB, _config.BroadphasePairColor,
                DebugRenderLayer.Default, 0, _config.DepthTest);
        }

        /// <summary>获取岛屿颜色。</summary>
        private Vector4 GetIslandColor(int islandId)
        {
            int idx = islandId % 6;
            return idx switch
            {
                0 => _config.IslandColors0,
                1 => _config.IslandColors1,
                2 => _config.IslandColors2,
                3 => _config.IslandColors3,
                4 => _config.IslandColors4,
                5 => _config.IslandColors5,
                _ => DebugColors.White,
            };
        }

        // ========================================================================================
        //  渲染数据提取 (给渲染管线用)
        // ========================================================================================

        /// <summary>获取所有图元(按layer分组)。</summary>
        public Dictionary<DebugRenderLayer, List<DebugPrimitive>> GetPrimitivesByLayer()
        {
            var result = new Dictionary<DebugRenderLayer, List<DebugPrimitive>>();
            foreach (var p in _primitives)
            {
                if (!result.TryGetValue(p.Layer, out var list))
                {
                    list = new List<DebugPrimitive>();
                    result[p.Layer] = list;
                }
                list.Add(p);
            }
            return result;
        }

        /// <summary>获取所有线段图元(展开成顶点对)。</summary>
        public int ExtractLines(Span<DebugVertex> outVertices)
        {
            int count = 0;
            foreach (var p in _primitives)
            {
                if (p.Type != DebugPrimitiveType.Line) continue;
                if (count + 2 > outVertices.Length) break;
                outVertices[count++] = p.V0;
                outVertices[count++] = p.V1;
            }
            return count;
        }

        /// <summary>获取所有三角形图元。</summary>
        public int ExtractTriangles(Span<DebugVertex> outVertices)
        {
            int count = 0;
            foreach (var p in _primitives)
            {
                if (p.Type != DebugPrimitiveType.Triangle) continue;
                if (count + 3 > outVertices.Length) break;
                outVertices[count++] = p.V0;
                outVertices[count++] = p.V1;
                outVertices[count++] = p.V2;
            }
            return count;
        }

        /// <summary>获取所有点图元。</summary>
        public int ExtractPoints(Span<DebugVertex> outVertices, Span<float> outSizes)
        {
            int count = 0;
            foreach (var p in _primitives)
            {
                if (p.Type != DebugPrimitiveType.Point) continue;
                if (count >= outVertices.Length) break;
                outVertices[count] = p.V0;
                if (count < outSizes.Length)
                    outSizes[count] = p.Size;
                count++;
            }
            return count;
        }

        /// <summary>获取所有文本。</summary>
        public IReadOnlyList<DebugText> GetTexts()
        {
            return _texts;
        }

        /// <summary>获取屏幕空间文本。</summary>
        public List<DebugText> GetScreenTexts()
        {
            var result = new List<DebugText>();
            foreach (var t in _texts)
            {
                if (!t.WorldSpace)
                    result.Add(t);
            }
            return result;
        }

        /// <summary>获取世界空间文本。</summary>
        public List<DebugText> GetWorldTexts()
        {
            var result = new List<DebugText>();
            foreach (var t in _texts)
            {
                if (t.WorldSpace)
                    result.Add(t);
            }
            return result;
        }

        // ========================================================================================
        //  快捷绘制方法
        // ========================================================================================

        /// <summary>快速画一个body的调试信息(不依赖world)。</summary>
        public void QuickDrawBody(int bodyId, Vector3 pos, Quaternion rot,
            Vector3 boxHalfExtents, Vector3 velocity, bool sleeping)
        {
            Vector4 color = sleeping ? _config.ShapeColorSleeping : _config.ShapeColor;
            DrawOBB(pos, rot, boxHalfExtents, color);

            if (_config.ShowVelocities && velocity.LengthSquared() > 0.01f)
            {
                DrawArrow(pos, pos + velocity * _config.VelocityScale, _config.VelocityColor);
            }

            if (_config.ShowBodyInfo)
            {
                DrawText3D(pos + Vector3.UnitY * 0.5f, $"#{bodyId}", color);
            }
        }

        /// <summary>画一个力场可视化。</summary>
        public void DrawForceField(Vector3 center, float radius, Vector3 fieldDirection,
            float strength, int gridResolution = 8)
        {
            Vector4 color = DebugColors.Orange;
            float arrowLen = Math.Clamp(strength * 0.1f, 0.1f, 1f);

            float step = radius * 2f / gridResolution;
            for (int x = 0; x <= gridResolution; x++)
            {
                for (int z = 0; z <= gridResolution; z++)
                {
                    Vector3 p = center + new Vector3(
                        -radius + x * step, 0, -radius + z * step);

                    // 只在圆内画
                    Vector3 d = p - center;
                    d.Y = 0;
                    if (d.LengthSquared() > radius * radius) continue;

                    DrawArrow(p, p + fieldDirection * arrowLen, color,
                        DebugRenderLayer.Forces, 0.05f, _config.DepthTest);
                }
            }

            // 画边界
            DrawCircle(center, Vector3.UnitY, radius, DebugColors.Gray, 32,
                DebugRenderLayer.Forces, _config.DepthTest);
        }

        /// <summary>画一个爆炸冲击波。</summary>
        public void DrawExplosion(Vector3 center, float currentRadius, float maxRadius,
            float damage)
        {
            // 当前冲击波
            float t = currentRadius / maxRadius;
            Vector4 color = new(1, 1 - t, 0, 1 - t * 0.5f);
            DrawSphere(center, currentRadius, color, 24,
                DebugRenderLayer.Forces, false);

            // 最大范围(虚线效果)
            DrawSphere(center, maxRadius, DebugColors.DarkGray * 0.5f, 16,
                DebugRenderLayer.Forces, false);

            // 中心
            DrawPoint(center, DebugColors.Red, 10f,
                DebugRenderLayer.Forces, false);

            // 标签
            DrawText3D(center + Vector3.UnitY * 0.5f,
                $"Boom! r={currentRadius:F1}/{maxRadius:F1} dmg={damage:F0}",
                DebugColors.Red, _config.TextSize, DebugRenderLayer.Forces, 0f);
        }

        /// <summary>画一条运动轨迹。</summary>
        public void DrawTrajectory(Vector3[] points, Vector4 color, bool showVelocities = false)
        {
            if (points == null || points.Length < 2) return;

            // 画轨迹线
            for (int i = 0; i < points.Length - 1; i++)
            {
                float t = (float)i / points.Length;
                Vector4 c = color * (1 - t * 0.5f);
                DrawLine(points[i], points[i + 1], c, DebugRenderLayer.Default, 0, false);
            }

            // 画起点和终点
            DrawPoint(points[0], DebugColors.Green, 8f, DebugRenderLayer.Default, false);
            DrawPoint(points[^1], DebugColors.Red, 8f, DebugRenderLayer.Default, false);

            // 可选: 画每个点的速度方向
            if (showVelocities)
            {
                for (int i = 1; i < points.Length - 1; i += 3)
                {
                    Vector3 vel = points[i] - points[i - 1];
                    if (vel.LengthSquared() > 0.001f)
                    {
                        DrawArrow(points[i], points[i] + vel * 5f, DebugColors.Cyan,
                            DebugRenderLayer.Velocities, 0.05f, false);
                    }
                }
            }
        }

        // ========================================================================================
        //  命令录制 / 回放 (用于测试和复现bug)
        // ========================================================================================

        /// <summary>录制一个调试命令(可以回放)。</summary>
        public void RecordCommand(string command, params object[] args)
        {
            // 简化: 直接记录到文本
            string line = command;
            if (args.Length > 0)
            {
                line += " " + string.Join(" ", args.Select(a => a?.ToString() ?? "null"));
            }
            // TODO: 实现真正的命令录制系统
            // 现在只记录到persistent text
        }

        // ========================================================================================
        //  快照 / 截图
        // ========================================================================================

        /// <summary>生成一个调试快照(可以保存或对比)。</summary>
        public DebugSnapshot TakeSnapshot()
        {
            var snap = new DebugSnapshot
            {
                FrameNumber = _frameNumber,
                Timestamp = _currentTime,
                Stats = _lastStats,
                PrimitiveCount = _primitives.Count,
                TextCount = _texts.Count,
                WarningCount = _warnings.Count,
                SelectedBodyIds = _selectedBodies.Keys.ToArray(),
            };

            // 复制图元(只复制前N个，避免太占内存)
            int maxCopy = Math.Min(1000, _primitives.Count);
            snap.Primitives = new DebugPrimitive[maxCopy];
            for (int i = 0; i < maxCopy; i++)
            {
                snap.Primitives[i] = _primitives[i];
            }

            return snap;
        }

        /// <summary>对比两个快照(用于检测异常变化)。</summary>
        public static string CompareSnapshots(DebugSnapshot a, DebugSnapshot b)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== Snapshot Comparison ===");
            sb.AppendLine($"Frame: {a.FrameNumber} -> {b.FrameNumber}");
            sb.AppendLine($"Primitives: {a.PrimitiveCount} -> {b.PrimitiveCount} " +
                         $"(diff: {b.PrimitiveCount - a.PrimitiveCount})");
            sb.AppendLine($"Bodies: {a.Stats.BodyCount} -> {b.Stats.BodyCount} " +
                         $"(diff: {b.Stats.BodyCount - a.Stats.BodyCount})");
            sb.AppendLine($"Active: {a.Stats.ActiveBodyCount} -> {b.Stats.ActiveBodyCount} " +
                         $"(diff: {b.Stats.ActiveBodyCount - a.Stats.ActiveBodyCount})");
            sb.AppendLine($"Contacts: {a.Stats.ContactCount} -> {b.Stats.ContactCount} " +
                         $"(diff: {b.Stats.ContactCount - a.Stats.ContactCount})");
            sb.AppendLine($"Step time: {a.Stats.StepTimeMs:F2} -> {b.Stats.StepTimeMs:F2} ms " +
                         $"(diff: {b.Stats.StepTimeMs - a.Stats.StepTimeMs:F2})");
            sb.AppendLine($"Memory: {a.Stats.MemoryUsageBytes / 1024.0 / 1024.0:F2} -> " +
                         $"{b.Stats.MemoryUsageBytes / 1024.0 / 1024.0:F2} MB");
            return sb.ToString();
        }

        // ========================================================================================
        //  配置预设
        // ========================================================================================

        /// <summary>应用预设配置: 性能分析模式。</summary>
        public void ApplyPreset_Profiling()
        {
            _config.ShowAABBs = false;
            _config.ShowShapes = false;
            _config.ShowContacts = false;
            _config.ShowConstraints = false;
            _config.ShowVelocities = false;
            _config.ShowStats = true;
            _config.ShowStatistics = true;
            _config.ShowAxes = false;
            _config.ShowGrid = false;
        }

        /// <summary>应用预设配置: 碰撞调试模式。</summary>
        public void ApplyPreset_CollisionDebug()
        {
            _config.ShowAABBs = true;
            _config.ShowShapes = true;
            _config.ShowContacts = true;
            _config.ShowContactNormals = true;
            _config.ShowContactImpulses = true;
            _config.ShowConstraints = true;
            _config.ShowConstraintLimits = true;
            _config.ShowStats = true;
            _config.PersistentContacts = true;
            _config.ContactLifetime = 0.5f;
        }

        /// <summary>应用预设配置: 运动学调试模式。</summary>
        public void ApplyPreset_KinematicsDebug()
        {
            _config.ShowAABBs = false;
            _config.ShowShapes = true;
            _config.ShowVelocities = true;
            _config.ShowAngularVelocities = true;
            _config.ShowForces = true;
            _config.ShowTorques = true;
            _config.ShowCenterOfMass = true;
            _config.ShowInertiaAxes = true;
            _config.ShowStats = true;
            _config.VelocityScale = 0.2f;
            _config.ForceScale = 0.002f;
        }

        /// <summary>应用预设配置: 岛屿/休眠调试模式。</summary>
        public void ApplyPreset_IslandDebug()
        {
            _config.ShowShapes = true;
            _config.ShowIslands = true;
            _config.ColorByIsland = true;
            _config.ShowSleepingBodies = true;
            _config.ShowActiveBodies = true;
            _config.ShowStats = true;
        }

        /// <summary>应用预设配置: 最小化(只画基本线框)。</summary>
        public void ApplyPreset_Minimal()
        {
            _config.ShowAABBs = false;
            _config.ShowShapes = true;
            _config.ShowContacts = false;
            _config.ShowConstraints = false;
            _config.ShowVelocities = false;
            _config.ShowStats = false;
            _config.ShowStatistics = false;
            _config.ShowAxes = false;
            _config.ShowGrid = false;
            _config.ShowBodyInfo = false;
        }

        /// <summary>应用预设配置: 全部打开(调试用，性能差)。</summary>
        public void ApplyPreset_All()
        {
            _config.ShowAABBs = true;
            _config.ShowShapes = true;
            _config.ShowContacts = true;
            _config.ShowContactNormals = true;
            _config.ShowContactImpulses = true;
            _config.ShowConstraints = true;
            _config.ShowConstraintLimits = true;
            _config.ShowVelocities = true;
            _config.ShowAngularVelocities = true;
            _config.ShowForces = true;
            _config.ShowTorques = true;
            _config.ShowCenterOfMass = true;
            _config.ShowInertiaAxes = true;
            _config.ShowIslands = true;
            _config.ShowBodyInfo = true;
            _config.ShowStats = true;
            _config.ShowStatistics = true;
            _config.ShowAxes = true;
            _config.ShowGrid = true;
            _config.ShowRaycasts = true;
            _config.ShowBroadphasePairs = true;
        }

        // ========================================================================================
        //  内部辅助
        // ========================================================================================

        /// <summary>检查某个layer是否启用。</summary>
        public bool IsLayerEnabled(DebugRenderLayer layer)
        {
            return layer switch
            {
                DebugRenderLayer.Bounds => _config.ShowAABBs,
                DebugRenderLayer.Contacts => _config.ShowContacts,
                DebugRenderLayer.Constraints => _config.ShowConstraints,
                DebugRenderLayer.Velocities => _config.ShowVelocities || _config.ShowAngularVelocities,
                DebugRenderLayer.Forces => _config.ShowForces || _config.ShowTorques,
                DebugRenderLayer.Shapes => _config.ShowShapes,
                DebugRenderLayer.Islands => _config.ShowIslands,
                DebugRenderLayer.Stats => _config.ShowStats,
                DebugRenderLayer.Raycasts => _config.ShowRaycasts,
                _ => true,
            };
        }

        /// <summary>清空所有缓冲。</summary>
        public void Clear()
        {
            _primitives.Clear();
            _texts.Clear();
        }

        /// <summary>获取当前帧的图元数量(按类型)。</summary>
        public int GetPrimitiveCount(DebugPrimitiveType type)
        {
            int count = 0;
            foreach (var p in _primitives)
            {
                if (p.Type == type) count++;
            }
            return count;
        }

        /// <summary>获取当前帧的图元数量(按layer)。</summary>
        public int GetPrimitiveCount(DebugRenderLayer layer)
        {
            int count = 0;
            foreach (var p in _primitives)
            {
                if (p.Layer == layer) count++;
            }
            return count;
        }
    }

    // ============================================================================================
    //  调试数据接口 (物理世界需要实现这个来给debugger提供数据)
    // ============================================================================================

    /// <summary>物理调试数据提供者接口。</summary>
    public interface IPhysicsDebugDataProvider
    {
        IEnumerable<BodyDebugData> GetBodies();
        IEnumerable<ContactDebugData> GetContacts();
        IEnumerable<ConstraintDebugData> GetConstraints();
        IEnumerable<IslandDebugData> GetIslands();
        IEnumerable<RaycastDebugData> GetRecentRaycasts();
        IEnumerable<BroadphasePairDebugData> GetBroadphasePairs();
    }

    /// <summary>调试形状类型。</summary>
    public enum DebugShapeType
    {
        Empty,
        Box,
        Sphere,
        Capsule,
        Cylinder,
        Cone,
        ConvexHull,
        Mesh,
        Heightfield,
        Compound,
        Plane,
    }

    /// <summary>调试约束类型。</summary>
    public enum DebugConstraintType
    {
        Distance,
        Point,
        Hinge,
        Cone,
        Ball,
        Slider,
        Fixed,
        Custom,
    }

    /// <summary>Body调试数据。</summary>
    public struct BodyDebugData
    {
        public int BodyId;
        public Vector3 Position;
        public Quaternion Orientation;
        public Vector3 Velocity;
        public Vector3 AngularVelocity;
        public Vector3 Force;
        public Vector3 Torque;
        public Vector3 CenterOfMass;
        public float Mass;
        public BodyDebugState State;
        public int CollisionLayer;
        public int CollisionMask;
        public int IslandId;
        public float SleepTimer;
        public bool UseCCD;
        public bool IsTrigger;
        public Vector3 AABBMin;
        public Vector3 AABBMax;

        // 形状数据(根据ShapeType取对应字段)
        public DebugShapeType ShapeType;
        public Vector3 BoxHalfExtents;
        public float SphereRadius;
        public float CapsuleRadius;
        public float CapsuleHalfHeight;
        public float CylinderRadius;
        public float CylinderHalfHeight;
        public Vector3 PlaneNormal;
        public Vector3[]? ConvexVertices;
        public int SubShapeCount;
    }

    /// <summary>接触点调试数据。</summary>
    public struct ContactDebugData
    {
        public int BodyIdA;
        public int BodyIdB;
        public Vector3 Point;
        public Vector3 Normal;          // 从B指向A
        public float PenetrationDepth;
        public float NormalImpulse;
        public Vector3 TangentImpulse;
        public float Friction;
        public float Restitution;
        public bool IsPersistent;
    }

    /// <summary>约束调试数据。</summary>
    public struct ConstraintDebugData
    {
        public int ConstraintId;
        public int BodyIdA;
        public int BodyIdB;
        public DebugConstraintType Type;
        public Vector3 AnchorA;
        public Vector3 AnchorB;
        public Vector3 AxisA;
        public Vector3 AxisB;
        public float MinLimit;
        public float MaxLimit;
        public float CurrentValue;
    }

    /// <summary>岛屿调试数据。</summary>
    public struct IslandDebugData
    {
        public int IslandId;
        public int BodyCount;
        public Vector3 AABBMin;
        public Vector3 AABBMax;
        public bool IsSleeping;
        public float SleepTimer;
    }

    /// <summary>射线检测调试数据。</summary>
    public struct RaycastDebugData
    {
        public Vector3 Origin;
        public Vector3 End;
        public Vector3 Direction;
        public float MaxDistance;
        public bool HasHit;
        public Vector3 HitPoint;
        public Vector3 HitNormal;
        public float HitDistance;
        public int HitBodyId;
    }

    /// <summary>宽相位pair调试数据。</summary>
    public struct BroadphasePairDebugData
    {
        public int BodyIdA;
        public int BodyIdB;
        public Vector3 AABBMinA;
        public Vector3 AABBMaxA;
        public Vector3 AABBMinB;
        public Vector3 AABBMaxB;
    }

    // ============================================================================================
    //  调试快照
    // ============================================================================================

    /// <summary>调试快照。</summary>
    public class DebugSnapshot
    {
        public int FrameNumber;
        public double Timestamp;
        public DebugStatsSnapshot Stats;
        public int PrimitiveCount;
        public int TextCount;
        public int WarningCount;
        public DebugPrimitive[]? Primitives;
        public int[]? SelectedBodyIds;

        public override string ToString()
        {
            return $"Snapshot Frame={FrameNumber} Prims={PrimitiveCount} Bodies={Stats.BodyCount}";
        }
    }

    // ============================================================================================
    //  调试覆盖层 (屏幕空间UI)
    // ============================================================================================

    /// <summary>
    /// 调试覆盖层。在屏幕上画各种调试信息(统计、警告、选中body信息等)。
    /// </summary>
    public class PhysicsDebugOverlay
    {
        private readonly PhysicsDebugger _debugger;
        private Vector2 _statsPos = new(10, 10);
        private Vector2 _selectedPos = new(10, 200);
        private Vector2 _warningPos = new(10, 500);
        private bool _showStats = true;
        private bool _showSelected = true;
        private bool _showWarnings = true;

        public Vector2 StatsPosition { get => _statsPos; set => _statsPos = value; }
        public Vector2 SelectedInfoPosition { get => _selectedPos; set => _selectedPos = value; }
        public Vector2 WarningPosition { get => _warningPos; set => _warningPos = value; }
        public bool ShowStats { get => _showStats; set => _showStats = value; }
        public bool ShowSelectedInfo { get => _showSelected; set => _showSelected = value; }
        public bool ShowWarnings { get => _showWarnings; set => _showWarnings = value; }

        public PhysicsDebugOverlay(PhysicsDebugger debugger)
        {
            _debugger = debugger ?? throw new ArgumentNullException(nameof(debugger));
        }

        /// <summary>绘制覆盖层。</summary>
        public void Draw()
        {
            if (_showStats)
            {
                _debugger.DrawStats(_statsPos);
            }
            if (_showSelected && _debugger.HasSelection)
            {
                _debugger.DrawSelectedBodyInfo(_selectedPos);
            }
            if (_showWarnings && _debugger.WarningCount > 0)
            {
                _debugger.DrawWarnings(_warningPos);
            }
        }
    }

    // ============================================================================================
    //  调试控制台 (命令行式交互)
    // ============================================================================================

    /// <summary>
    /// 调试控制台。可以通过命令行控制调试选项。
    /// 比如在游戏运行时输入 "show aabb" 就能打开AABB显示。
    /// </summary>
    public class PhysicsDebugConsole
    {
        private readonly PhysicsDebugger _debugger;
        private readonly Dictionary<string, Action<string[]>> _commands = new();
        private readonly List<string> _history = new();
        private const int MaxHistory = 100;

        public PhysicsDebugConsole(PhysicsDebugger debugger)
        {
            _debugger = debugger ?? throw new ArgumentNullException(nameof(debugger));
            RegisterCommands();
        }

        private void RegisterCommands()
        {
            _commands["help"] = CmdHelp;
            _commands["show"] = CmdShow;
            _commands["hide"] = CmdHide;
            _commands["preset"] = CmdPreset;
            _commands["select"] = CmdSelect;
            _commands["deselect"] = CmdDeselect;
            _commands["filter"] = CmdFilter;
            _commands["scale"] = CmdScale;
            _commands["color"] = CmdColor;
            _commands["clear"] = CmdClear;
            _commands["stats"] = CmdStats;
            _commands["snapshot"] = CmdSnapshot;
            _commands["warn"] = CmdWarn;
        }

        /// <summary>执行一条命令。</summary>
        public string Execute(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;

            _history.Add(input);
            if (_history.Count > MaxHistory)
                _history.RemoveAt(0);

            var parts = input.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return string.Empty;

            string cmd = parts[0].ToLowerInvariant();
            var args = parts.Length > 1 ? parts[1..] : Array.Empty<string>();

            if (_commands.TryGetValue(cmd, out var handler))
            {
                try
                {
                    handler(args);
                    return $"OK: {cmd}";
                }
                catch (Exception ex)
                {
                    return $"Error: {ex.Message}";
                }
            }
            return $"Unknown command: {cmd}. Type 'help' for available commands.";
        }

        private void CmdHelp(string[] args)
        {
            // 返回帮助信息
        }

        private void CmdShow(string[] args)
        {
            if (args.Length == 0) return;
            string what = args[0].ToLowerInvariant();
            switch (what)
            {
                case "aabb": _debugger.Config.ShowAABBs = true; break;
                case "shape": case "shapes": _debugger.Config.ShowShapes = true; break;
                case "contact": case "contacts": _debugger.Config.ShowContacts = true; break;
                case "normal": case "normals": _debugger.Config.ShowContactNormals = true; break;
                case "impulse": case "impulses": _debugger.Config.ShowContactImpulses = true; break;
                case "constraint": case "constraints": _debugger.Config.ShowConstraints = true; break;
                case "velocity": case "velocities": _debugger.Config.ShowVelocities = true; break;
                case "angvel": _debugger.Config.ShowAngularVelocities = true; break;
                case "force": case "forces": _debugger.Config.ShowForces = true; break;
                case "torque": case "torques": _debugger.Config.ShowTorques = true; break;
                case "com": case "centerofmass": _debugger.Config.ShowCenterOfMass = true; break;
                case "inertia": _debugger.Config.ShowInertiaAxes = true; break;
                case "island": case "islands": _debugger.Config.ShowIslands = true; break;
                case "info": _debugger.Config.ShowBodyInfo = true; break;
                case "stats": _debugger.Config.ShowStatistics = true; break;
                case "grid": _debugger.Config.ShowGrid = true; break;
                case "axes": _debugger.Config.ShowAxes = true; break;
                case "raycast": case "raycasts": _debugger.Config.ShowRaycasts = true; break;
                case "all": _debugger.ApplyPreset_All(); break;
            }
        }

        private void CmdHide(string[] args)
        {
            if (args.Length == 0) return;
            string what = args[0].ToLowerInvariant();
            switch (what)
            {
                case "aabb": _debugger.Config.ShowAABBs = false; break;
                case "shape": case "shapes": _debugger.Config.ShowShapes = false; break;
                case "contact": case "contacts": _debugger.Config.ShowContacts = false; break;
                case "constraint": case "constraints": _debugger.Config.ShowConstraints = false; break;
                case "velocity": case "velocities": _debugger.Config.ShowVelocities = false; break;
                case "force": case "forces": _debugger.Config.ShowForces = false; break;
                case "island": case "islands": _debugger.Config.ShowIslands = false; break;
                case "stats": _debugger.Config.ShowStatistics = false; break;
                case "grid": _debugger.Config.ShowGrid = false; break;
                case "axes": _debugger.Config.ShowAxes = false; break;
                case "all": _debugger.ApplyPreset_Minimal(); break;
            }
        }

        private void CmdPreset(string[] args)
        {
            if (args.Length == 0) return;
            string preset = args[0].ToLowerInvariant();
            switch (preset)
            {
                case "profile": case "profiling": _debugger.ApplyPreset_Profiling(); break;
                case "collision": _debugger.ApplyPreset_CollisionDebug(); break;
                case "kinematics": _debugger.ApplyPreset_KinematicsDebug(); break;
                case "island": _debugger.ApplyPreset_IslandDebug(); break;
                case "minimal": _debugger.ApplyPreset_Minimal(); break;
                case "all": _debugger.ApplyPreset_All(); break;
            }
        }

        private void CmdSelect(string[] args)
        {
            // 选中body (需要外部数据源)
        }

        private void CmdDeselect(string[] args)
        {
            _debugger.ClearSelection();
        }

        private void CmdFilter(string[] args)
        {
            if (args.Length == 0) return;
            if (int.TryParse(args[0], out int id))
            {
                _debugger.Config.FilterBodyId = id;
            }
        }

        private void CmdScale(string[] args)
        {
            if (args.Length < 2) return;
            string what = args[0].ToLowerInvariant();
            if (float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float val))
            {
                switch (what)
                {
                    case "velocity": _debugger.Config.VelocityScale = val; break;
                    case "force": _debugger.Config.ForceScale = val; break;
                    case "angvel": _debugger.Config.AngularVelocityScale = val; break;
                    case "normal": _debugger.Config.NormalLength = val; break;
                }
            }
        }

        private void CmdColor(string[] args)
        {
            // 颜色设置(简化)
        }

        private void CmdClear(string[] args)
        {
            _debugger.Clear();
            _debugger.ClearSelection();
            _debugger.ClearPersistent();
        }

        private void CmdStats(string[] args)
        {
            _debugger.Config.ShowStatistics = !_debugger.Config.ShowStatistics;
        }

        private void CmdSnapshot(string[] args)
        {
            var snap = _debugger.TakeSnapshot();
            // 可以保存到文件
        }

        private void CmdWarn(string[] args)
        {
            if (args.Length > 0)
            {
                string msg = string.Join(" ", args);
                _debugger.LogWarning(msg);
            }
        }

        /// <summary>获取命令历史。</summary>
        public IReadOnlyList<string> History => _history;

        /// <summary>获取所有可用命令。</summary>
        public IEnumerable<string> AvailableCommands => _commands.Keys;
    }
}
