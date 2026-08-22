using System;
using System.Collections.Generic;
using System.Numerics;

namespace ShanyuEngine.VFX
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - 光束/拖尾系统
    //
    // 光束和拖尾特效:
    //   * BeamComponent: 光束组件 (起点/终点/宽度/颜色/纹理/动画/噪声/弯曲)
    //   * BeamEmitter: 光束发射器 (持续/脉冲/按事件触发)
    //   * TrailRenderer: 拖尾渲染 (记录轨迹点→带状渲染/渐变宽度/渐变颜色)
    //   * TrailData: 拖尾数据 (位置历史/颜色历史/宽度历史/最大点数)
    //   * RibbonRenderer: 飘带渲染 (多顶点宽度平滑飘带/纹理映射)
    //   * SplineBeam: 样条光束 (沿样条曲线/噪声扰动/宽度变化)
    // ════════════════════════════════════════════════════════════════════════

    #region BeamComponent

    /// <summary>光束动画模式。</summary>
    public enum BeamAnimationMode
    {
        None,
        Scroll,
        Pulse,
        RandomFlicker,
        Wave
    }

    /// <summary>光束弯曲模式。</summary>
    public enum BeamBendMode
    {
        Straight,
        Noise,
        Curve,
        Gravity
    }

    /// <summary>
    /// 光束组件。定义单个光束的视觉属性。
    /// </summary>
    public class BeamComponent
    {
        // ── 标识 ──
        public string Name { get; set; } = "Beam";
        public bool IsEnabled { get; set; } = true;
        public bool IsVisible { get; set; } = true;

        // ── 端点 ──
        public Vector3 StartPosition { get; set; } = Vector3.Zero;
        public Vector3 EndPosition { get; set; } = new Vector3(0, 10, 0);
        public TransformSpace StartSpace { get; set; } = TransformSpace.World;
        public TransformSpace EndSpace { get; set; } = TransformSpace.World;

        // ── 外观 ──
        public float Width { get; set; } = 0.1f;
        public float WidthStart { get; set; } = 0.1f;
        public float WidthEnd { get; set; } = 0.1f;
        public Vector4 Color { get; set; } = new Vector4(1, 1, 1, 1);
        public Vector4 ColorStart { get; set; } = new Vector4(1, 1, 1, 1);
        public Vector4 ColorEnd { get; set; } = new Vector4(1, 1, 1, 1);
        public string Texture { get; set; }
        public float TileCount { get; set; } = 1f;

        // ── 动画 ──
        public BeamAnimationMode AnimationMode { get; set; } = BeamAnimationMode.None;
        public float AnimationSpeed { get; set; } = 1f;
        public float AnimationPhase { get; set; }
        private float _animationTimer;

        // ── 弯曲 ──
        public BeamBendMode BendMode { get; set; } = BeamBendMode.Straight;
        public float BendAmount { get; set; } = 1f;
        public float BendFrequency { get; set; } = 1f;
        public float BendSeed { get; set; }

        // ── 噪声 ──
        public float NoiseStrength { get; set; }
        public float NoiseFrequency { get; set; } = 1f;
        public float NoiseSpeed { get; set; } = 1f;

        // ── 衰减 ──
        public float FadeInDuration { get; set; } = 0f;
        public float FadeOutDuration { get; set; } = 0f;
        public float Lifetime { get; set; } = -1f;
        private float _age;

        // ── 交互 ──
        public bool CastShadows { get; set; }
        public bool ReceiveShadows { get; set; }
        public bool AffectDynamic { get; set; }

        // ── 碰撞 ──
        public bool EnableCollision { get; set; }
        public float CollisionRadius { get; set; } = 0.1f;
        public float MaxRange { get; set; } = 100f;

        /// <summary>更新光束。</summary>
        public void Update(float dt)
        {
            if (!IsEnabled) return;
            _age += dt;
            _animationTimer += dt;

            if (Lifetime > 0f && _age >= Lifetime)
            {
                IsEnabled = false;
            }
        }

        /// <summary>获取当前透明度。</summary>
        public float GetCurrentAlpha()
        {
            float alpha = Color.W;
            if (FadeInDuration > 0f && _age < FadeInDuration)
                alpha *= _age / FadeInDuration;
            if (FadeOutDuration > 0f && Lifetime > 0f)
            {
                float remaining = Lifetime - _age;
                if (remaining < FadeOutDuration)
                    alpha *= remaining / FadeOutDuration;
            }
            return alpha;
        }

        /// <summary>获取动画 UV 偏移。</summary>
        public float GetAnimationOffset()
        {
            switch (AnimationMode)
            {
                case BeamAnimationMode.Scroll:
                    return _animationTimer * AnimationSpeed;
                case BeamAnimationMode.Pulse:
                    return MathF.Sin(_animationTimer * AnimationSpeed) * 0.5f;
                case BeamAnimationMode.Wave:
                    return MathF.Sin(_animationTimer * AnimationSpeed + AnimationPhase) * BendAmount;
                case BeamAnimationMode.RandomFlicker:
                    return (SimpleNoise(_animationTimer * AnimationSpeed + BendSeed)) * BendAmount;
                default:
                    return 0f;
            }
        }

        /// <summary>获取弯曲后的中间点列表。</summary>
        public List<Vector3> GetBentPoints(int segmentCount)
        {
            var points = new List<Vector3>(segmentCount + 1);

            for (int i = 0; i <= segmentCount; i++)
            {
                float t = (float)i / segmentCount;
                Vector3 point = Vector3.Lerp(StartPosition, EndPosition, t);

                if (BendMode != BeamBendMode.Straight)
                {
                    Vector3 bendOffset = Vector3.Zero;
                    float t2 = _animationTimer;

                    switch (BendMode)
                    {
                        case BeamBendMode.Noise:
                            bendOffset = new Vector3(
                                SimpleNoise(t * BendFrequency + BendSeed + t2 * NoiseSpeed) * NoiseStrength,
                                SimpleNoise(t * BendFrequency + BendSeed + 31.7f + t2 * NoiseSpeed) * NoiseStrength,
                                SimpleNoise(t * BendFrequency + BendSeed + 67.3f + t2 * NoiseSpeed) * NoiseStrength);
                            break;

                        case BeamBendMode.Curve:
                            float curveAmount = MathF.Sin(t * MathF.PI) * BendAmount;
                            // 向侧方弯曲
                            Vector3 dir = Vector3.Normalize(EndPosition - StartPosition);
                            Vector3 perp = Vector3.Cross(dir, Vector3.UnitY);
                            if (perp.LengthSquared() < 0.001f)
                                perp = Vector3.Cross(dir, Vector3.UnitX);
                            bendOffset = Vector3.Normalize(perp) * curveAmount;
                            break;

                        case BeamBendMode.Gravity:
                            float gravity = BendAmount * t * t;
                            bendOffset = new Vector3(0, -gravity, 0);
                            break;
                    }

                    point += bendOffset;
                }

                points.Add(point);
            }

            return points;
        }

        /// <summary>在指定 t 处获取宽度。</summary>
        public float GetWidthAt(float t)
        {
            return MathHelper.Lerp(WidthStart, WidthEnd, t) * Width;
        }

        /// <summary>在指定 t 处获取颜色。</summary>
        public Vector4 GetColorAt(float t)
        {
            return Vector4.Lerp(ColorStart, ColorEnd, t) * new Vector4(1, 1, 1, GetCurrentAlpha());
        }

        private static float SimpleNoise(float x)
        {
            return MathF.Sin(x * 1.1f + MathF.Sin(x * 0.7f) * 3.14f) * 0.5f;
        }
    }

    /// <summary>变换空间。</summary>
    public enum TransformSpace
    {
        World,
        Local,
        Component
    }

    #endregion

    #region BeamEmitter

    /// <summary>光束发射模式。</summary>
    public enum BeamEmitMode
    {
        Continuous,
        Pulsed,
        EventTriggered
    }

    /// <summary>
    /// 光束发射器。管理光束的发射逻辑。
    /// </summary>
    public class BeamEmitter
    {
        public string Name { get; set; } = "BeamEmitter";
        public bool IsEnabled { get; set; } = true;
        public BeamEmitMode EmitMode { get; set; } = BeamEmitMode.Continuous;

        // ── 脉冲参数 ──
        public float PulseInterval { get; set; } = 1f;
        public float PulseDuration { get; set; } = 0.5f;
        public float PulseFadeIn { get; set; } = 0.1f;
        public float PulseFadeOut { get; set; } = 0.1f;

        // ── 光束模板 ──
        public BeamComponent BeamTemplate { get; set; } = new BeamComponent();

        // ── 动态端点 ──
        public Func<Vector3> GetStartPosition { get; set; }
        public Func<Vector3> GetEndPosition { get; set; }

        // ── 内部 ──
        private float _pulseTimer;
        private bool _isPulsing;
        private float _pulseElapsed;
        public readonly List<BeamComponent> ActiveBeams = new List<BeamComponent>();

        /// <summary>更新发射器。</summary>
        public void Update(float dt)
        {
            if (!IsEnabled) return;

            // 更新动态端点
            if (GetStartPosition != null)
                BeamTemplate.StartPosition = GetStartPosition();
            if (GetEndPosition != null)
                BeamTemplate.EndPosition = GetEndPosition();

            switch (EmitMode)
            {
                case BeamEmitMode.Continuous:
                    BeamTemplate.Update(dt);
                    break;

                case BeamEmitMode.Pulsed:
                    UpdatePulse(dt);
                    break;

                case BeamEmitMode.EventTriggered:
                    UpdateActiveBeams(dt);
                    break;
            }
        }

        private void UpdatePulse(float dt)
        {
            _pulseTimer += dt;

            if (!_isPulsing && _pulseTimer >= PulseInterval)
            {
                _isPulsing = true;
                _pulseElapsed = 0f;
                _pulseTimer = 0f;

                // 创建脉冲光束
                var beam = CloneTemplate();
                beam.FadeInDuration = PulseFadeIn;
                beam.FadeOutDuration = PulseFadeOut;
                beam.Lifetime = PulseDuration;
                ActiveBeams.Add(beam);
            }

            if (_isPulsing)
            {
                _pulseElapsed += dt;
                if (_pulseElapsed >= PulseDuration)
                    _isPulsing = false;
            }

            UpdateActiveBeams(dt);
        }

        private void UpdateActiveBeams(float dt)
        {
            for (int i = ActiveBeams.Count - 1; i >= 0; i--)
            {
                ActiveBeams[i].Update(dt);
                if (!ActiveBeams[i].IsEnabled)
                    ActiveBeams.RemoveAt(i);
            }
        }

        /// <summary>触发事件光束。</summary>
        public void TriggerEvent()
        {
            if (EmitMode != BeamEmitMode.EventTriggered) return;

            var beam = CloneTemplate();
            beam.StartPosition = BeamTemplate.StartPosition;
            beam.EndPosition = BeamTemplate.EndPosition;
            ActiveBeams.Add(beam);
        }

        private BeamComponent CloneTemplate()
        {
            return new BeamComponent
            {
                StartPosition = BeamTemplate.StartPosition,
                EndPosition = BeamTemplate.EndPosition,
                Width = BeamTemplate.Width,
                WidthStart = BeamTemplate.WidthStart,
                WidthEnd = BeamTemplate.WidthEnd,
                Color = BeamTemplate.Color,
                ColorStart = BeamTemplate.ColorStart,
                ColorEnd = BeamTemplate.ColorEnd,
                Texture = BeamTemplate.Texture,
                AnimationMode = BeamTemplate.AnimationMode,
                AnimationSpeed = BeamTemplate.AnimationSpeed,
                BendMode = BeamTemplate.BendMode,
                BendAmount = BeamTemplate.BendAmount,
                BendFrequency = BeamTemplate.BendFrequency,
                BendSeed = BeamTemplate.BendSeed + Random.Shared.NextSingle() * 100f,
                NoiseStrength = BeamTemplate.NoiseStrength,
                NoiseFrequency = BeamTemplate.NoiseFrequency,
                IsEnabled = true,
                IsVisible = true
            };
        }
    }

    #endregion

    #region TrailData (扩展)

    /// <summary>
    /// 拖尾数据。记录位置/颜色/宽度的历史轨迹。
    /// </summary>
    public class TrailDataExtended
    {
        // ── 历史数据 ──
        private readonly List<Vector3> _positions = new List<Vector3>();
        private readonly List<Vector4> _colors = new List<Vector4>();
        private readonly List<float> _widths = new List<float>();
        private readonly List<float> _ages = new List<float>();

        // ── 配置 ──
        public int MaxPoints { get; set; } = 64;
        public float PointLifetime { get; set; } = 0.5f;
        public float UpdateInterval { get; set; } = 0.01f;
        private float _updateTimer;
        private float _oldestPointAge;

        public int PointCount => _positions.Count;
        public bool IsActive { get; set; } = true;

        // ── 渲染参数 ──
        public float WidthStart { get; set; } = 1f;
        public float WidthEnd { get; set; } = 0f;
        public Vector4 ColorStart { get; set; } = Vector4.One;
        public Vector4 ColorEnd { get; set; } = Vector4.One;
        public float WidthMultiplier { get; set; } = 1f;
        public bool UseColorOverLife { get; set; }
        public bool UseWidthOverLife { get; set; } = true;

        /// <summary>添加新点。</summary>
        public void AddPoint(Vector3 position, Vector4 color, float width)
        {
            if (MaxPoints <= 0) return;

            _positions.Add(position);
            _colors.Add(color);
            _widths.Add(width);
            _ages.Add(0f);

            // 限制最大点数
            while (_positions.Count > MaxPoints)
            {
                _positions.RemoveAt(0);
                _colors.RemoveAt(0);
                _widths.RemoveAt(0);
                _ages.RemoveAt(0);
            }
        }

        /// <summary>更新拖尾数据。</summary>
        public void Update(float dt)
        {
            if (!IsActive) return;

            _updateTimer += dt;

            // 老化所有点
            for (int i = _ages.Count - 1; i >= 0; i--)
            {
                _ages[i] += dt;

                if (_ages[i] >= PointLifetime)
                {
                    _positions.RemoveAt(i);
                    _colors.RemoveAt(i);
                    _widths.RemoveAt(i);
                    _ages.RemoveAt(i);
                }
            }
        }

        /// <summary>是否需要添加新点。</summary>
        public bool ShouldAddPoint() => _updateTimer >= UpdateInterval;

        public void ResetUpdateTimer() => _updateTimer = 0f;

        /// <summary>重置。</summary>
        public void Reset()
        {
            _positions.Clear();
            _colors.Clear();
            _widths.Clear();
            _ages.Clear();
            _updateTimer = 0f;
        }

        /// <summary>获取带状渲染顶点 (每段 4 个顶点)。</summary>
        public void GetRibbonVertices(List<RibbonVertex> vertices, Vector3 cameraForward)
        {
            if (_positions.Count < 2) return;

            vertices.Clear();
            Vector3 up = Vector3.UnitY;
            Vector3 right = Vector3.Cross(cameraForward, up);
            if (right.LengthSquared() < 0.001f)
            {
                right = Vector3.Cross(cameraForward, Vector3.UnitX);
            }
            right = Vector3.Normalize(right);

            for (int i = 0; i < _positions.Count - 1; i++)
            {
                float age0 = _ages[i] / PointLifetime;
                float age1 = _ages[i + 1] / PointLifetime;

                Vector4 color0 = UseColorOverLife
                    ? Vector4.Lerp(ColorStart, ColorEnd, age0) * _colors[i]
                    : _colors[i];
                Vector4 color1 = UseColorOverLife
                    ? Vector4.Lerp(ColorStart, ColorEnd, age1) * _colors[i + 1]
                    : _colors[i + 1];

                float width0 = UseWidthOverLife
                    ? MathHelper.Lerp(WidthStart, WidthEnd, age0) * _widths[i] * WidthMultiplier
                    : _widths[i] * WidthMultiplier;
                float width1 = UseWidthOverLife
                    ? MathHelper.Lerp(WidthStart, WidthEnd, age1) * _widths[i + 1] * WidthMultiplier
                    : _widths[i + 1] * WidthMultiplier;

                // 计算 up 方向 (沿轨迹方向)
                Vector3 segment = _positions[i + 1] - _positions[i];
                if (segment.LengthSquared() > 0.0001f)
                {
                    Vector3 segDir = Vector3.Normalize(segment);
                    Vector3 segRight = Vector3.Cross(segDir, cameraForward);
                    if (segRight.LengthSquared() > 0.0001f)
                        right = Vector3.Normalize(segRight);
                }

                Vector3 p0a = _positions[i] + right * width0 * 0.5f;
                Vector3 p0b = _positions[i] - right * width0 * 0.5f;
                Vector3 p1a = _positions[i + 1] + right * width1 * 0.5f;
                Vector3 p1b = _positions[i + 1] - right * width1 * 0.5f;

                float uv0 = (float)i / (_positions.Count - 1);
                float uv1 = (float)(i + 1) / (_positions.Count - 1);

                vertices.Add(new RibbonVertex { Position = p0a, UV = new Vector2(0, uv0), Color = color0 });
                vertices.Add(new RibbonVertex { Position = p0b, UV = new Vector2(1, uv0), Color = color0 });
                vertices.Add(new RibbonVertex { Position = p1a, UV = new Vector2(0, uv1), Color = color1 });
                vertices.Add(new RibbonVertex { Position = p1b, UV = new Vector2(1, uv1), Color = color1 });
            }
        }
    }

    /// <summary>飘带顶点。</summary>
    public struct RibbonVertex
    {
        public Vector3 Position;
        public Vector2 UV;
        public Vector4 Color;
        public Vector3 Normal;
    }

    #endregion

    #region TrailRenderer

    /// <summary>
    /// 拖尾渲染器。将 TrailData 渲染为带状几何体。
    /// </summary>
    public class TrailRenderer
    {
        public string Name { get; set; } = "TrailRenderer";
        public bool IsEnabled { get; set; } = true;
        public string Material { get; set; }
        public string Texture { get; set; }
        public float WidthMultiplier { get; set; } = 1f;
        public bool FaceCamera { get; set; } = true;
        public bool WorldSpaceTrail { get; set; } = true;

        // ── 颜色/宽度 ──
        public Vector4 ColorOverLifeStart { get; set; } = new Vector4(1, 1, 1, 1);
        public Vector4 ColorOverLifeEnd { get; set; } = new Vector4(1, 1, 1, 0);
        public float WidthOverLifeStart { get; set; } = 1f;
        public float WidthOverLifeEnd { get; set; } = 0f;

        // ── 拖尾数据 ──
        public TrailDataExtended Trail { get; } = new TrailDataExtended();
        private readonly List<RibbonVertex> _ribbonVertices = new List<RibbonVertex>();

        /// <summary>更新拖尾 (每帧调用)。</summary>
        public void Update(float dt, Vector3 currentPosition, Vector4 color, float width, Vector3 cameraForward)
        {
            if (!IsEnabled) return;

            Trail.Update(dt);

            if (Trail.ShouldAddPoint())
            {
                Trail.AddPoint(currentPosition, color, width);
                Trail.ResetUpdateTimer();
            }
        }

        /// <summary>获取渲染数据。</summary>
        public IReadOnlyList<RibbonVertex> GetRenderData(Vector3 cameraForward)
        {
            Trail.GetRibbonVertices(_ribbonVertices, cameraForward);
            return _ribbonVertices;
        }

        /// <summary>重置拖尾。</summary>
        public void ResetTrail()
        {
            Trail.Reset();
        }
    }

    #endregion

    #region RibbonRenderer

    /// <summary>
    /// 飘带渲染器。多顶点宽度的平滑飘带。
    /// </summary>
    public class RibbonRenderer
    {
        public string Name { get; set; } = "RibbonRenderer";
        public bool IsEnabled { get; set; } = true;
        public string Material { get; set; }
        public string Texture { get; set; }

        // ── 飘带参数 ──
        public int VertexCount { get; set; } = 16;           // 横截面顶点数
        public float Width { get; set; } = 1f;
        public float Length { get; set; } = 10f;
        public int Segments { get; set; } = 32;
        public float WidthTaper { get; set; } = 0.5f;        // 端部收窄比例
        public bool Closed { get; set; }                       // 是否封闭管

        // ── 动画 ──
        public float WaveAmplitude { get; set; }
        public float WaveFrequency { get; set; } = 1f;
        public float WaveSpeed { get; set; } = 1f;

        // ── 颜色 ──
        public Vector4 ColorStart { get; set; } = Vector4.One;
        public Vector4 ColorEnd { get; set; } = Vector4.One;

        private readonly List<RibbonVertex> _vertices = new List<RibbonVertex>();
        private readonly List<ushort> _indices = new List<ushort>();

        /// <summary>生成飘带网格。</summary>
        public void GenerateMesh(Vector3 start, Vector3 end, Vector3 up, float time)
        {
            _vertices.Clear();
            _indices.Clear();

            Vector3 direction = end - start;
            float length = direction.Length();
            if (length < 0.001f) return;

            Vector3 dir = direction / length;
            Vector3 right = Vector3.Cross(dir, up);
            if (right.LengthSquared() < 0.001f)
                right = Vector3.Cross(dir, Vector3.UnitX);
            right = Vector3.Normalize(right);

            // 生成顶点
            for (int seg = 0; seg <= Segments; seg++)
            {
                float t = (float)seg / Segments;
                Vector3 pos = start + dir * (length * t);

                // 波浪扰动
                if (WaveAmplitude > 0f)
                {
                    float wave = MathF.Sin(t * WaveFrequency * MathF.PI * 2 + time * WaveSpeed) * WaveAmplitude;
                    pos += right * wave;
                }

                // 宽度渐变 (两端收窄)
                float widthTaper = Width * (1f - WidthTaper * MathF.Pow(Math.Abs(t * 2 - 1), 2));
                float segWidth = widthTaper;

                // 颜色渐变
                Vector4 color = Vector4.Lerp(ColorStart, ColorEnd, t);

                // 横截面顶点
                for (int v = 0; v < VertexCount; v++)
                {
                    float angle = (float)v / VertexCount * MathF.PI * 2;
                    Vector3 offset = right * MathF.Cos(angle) * segWidth * 0.5f +
                                     up * MathF.Sin(angle) * segWidth * 0.5f;
                    float u = (float)v / VertexCount;
                    float vCoord = t;

                    _vertices.Add(new RibbonVertex
                    {
                        Position = pos + offset,
                        UV = new Vector2(u, vCoord),
                        Color = color,
                        Normal = Vector3.Normalize(offset)
                    });
                }
            }

            // 生成索引
            for (int seg = 0; seg < Segments; seg++)
            {
                for (int v = 0; v < VertexCount; v++)
                {
                    int nextV = (v + 1) % VertexCount;
                    int v00 = seg * VertexCount + v;
                    int v01 = seg * VertexCount + nextV;
                    int v10 = (seg + 1) * VertexCount + v;
                    int v11 = (seg + 1) * VertexCount + nextV;

                    _indices.Add((ushort)v00);
                    _indices.Add((ushort)v10);
                    _indices.Add((ushort)v11);

                    _indices.Add((ushort)v00);
                    _indices.Add((ushort)v11);
                    _indices.Add((ushort)v01);
                }
            }
        }

        public IReadOnlyList<RibbonVertex> GetVertices() => _vertices;
        public IReadOnlyList<ushort> GetIndices() => _indices;
    }

    #endregion

    #region SplineBeam

    /// <summary>
    /// 样条光束。沿样条曲线生成光束，支持噪声扰动和宽度变化。
    /// </summary>
    public class SplineBeam
    {
        public string Name { get; set; } = "SplineBeam";
        public bool IsEnabled { get; set; } = true;
        public bool IsVisible { get; set; } = true;

        // ── 样条控制点 ──
        public readonly List<Vector3> ControlPoints = new List<Vector3>();
        public int Segments { get; set; } = 32;

        // ── 外观 ──
        public float Width { get; set; } = 0.1f;
        public Vector4 Color { get; set; } = new Vector4(1, 1, 1, 1);
        public Vector4 ColorStart { get; set; } = new Vector4(1, 1, 1, 1);
        public Vector4 ColorEnd { get; set; } = new Vector4(1, 1, 1, 1);
        public string Texture { get; set; }

        // ── 宽度变化 ──
        public bool WidthOverLength { get; set; }
        public float WidthStart { get; set; } = 0.2f;
        public float WidthEnd { get; set; } = 0.05f;
        public bool PulsingWidth { get; set; }
        public float PulseSpeed { get; set; } = 2f;

        // ── 噪声 ──
        public float NoiseStrength { get; set; }
        public float NoiseFrequency { get; set; } = 1f;
        public float NoiseSpeed { get; set; } = 1f;
        public float NoiseSeed { get; set; }

        // ── 动画 ──
        public float ScrollSpeed { get; set; } = 1f;
        public float AnimationTime { get; private set; }

        // ── 衰减 ──
        public float FadeInDuration { get; set; } = 0f;
        public float FadeOutDuration { get; set; } = 0f;
        public float Lifetime { get; set; } = -1f;
        private float _age;

        /// <summary>添加控制点。</summary>
        public void AddControlPoint(Vector3 point)
        {
            ControlPoints.Add(point);
        }

        /// <summary>Catmull-Rom 样条插值。</summary>
        public Vector3 EvaluateSpline(float t)
        {
            if (ControlPoints.Count == 0) return Vector3.Zero;
            if (ControlPoints.Count == 1) return ControlPoints[0];
            if (ControlPoints.Count == 2) return Vector3.Lerp(ControlPoints[0], ControlPoints[1], t);

            int n = ControlPoints.Count - 1;
            float scaledT = t * n;
            int i = Math.Clamp((int)scaledT, 0, n - 1);
            float localT = scaledT - i;

            Vector3 p0 = ControlPoints[Math.Max(0, i - 1)];
            Vector3 p1 = ControlPoints[i];
            Vector3 p2 = ControlPoints[Math.Min(n, i + 1)];
            Vector3 p3 = ControlPoints[Math.Min(n, i + 2)];

            return CatmullRom(p0, p1, p2, p3, localT);
        }

        private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t;
            float t3 = t2 * t;

            return 0.5f * (
                (2f * p1) +
                (-p0 + p2) * t +
                (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
                (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }

        /// <summary>获取光束点列表 (含噪声扰动)。</summary>
        public List<BeamPoint> GetBeamPoints()
        {
            var points = new List<BeamPoint>(Segments + 1);

            for (int i = 0; i <= Segments; i++)
            {
                float t = (float)i / Segments;
                Vector3 pos = EvaluateSpline(t);

                // 噪声扰动
                if (NoiseStrength > 0f)
                {
                    float noiseT = t * NoiseFrequency + AnimationTime * NoiseSpeed;
                    pos += new Vector3(
                        SimpleNoise(noiseT + NoiseSeed) * NoiseStrength,
                        SimpleNoise(noiseT + NoiseSeed + 31.7f) * NoiseStrength,
                        SimpleNoise(noiseT + NoiseSeed + 67.3f) * NoiseStrength);
                }

                // 宽度
                float width = Width;
                if (WidthOverLength)
                    width = MathHelper.Lerp(WidthStart, WidthEnd, t);
                if (PulsingWidth)
                    width *= 0.7f + 0.3f * MathF.Sin(AnimationTime * PulseSpeed + t * 10f);

                // 颜色
                Vector4 color = Vector4.Lerp(ColorStart, ColorEnd, t);

                points.Add(new BeamPoint
                {
                    Position = pos,
                    Width = width,
                    Color = color,
                    UV = new Vector2(0f, t * (float)ControlPoints.Count / Segments)
                });
            }

            return points;
        }

        /// <summary>更新。</summary>
        public void Update(float dt)
        {
            if (!IsEnabled) return;
            _age += dt;
            AnimationTime += dt;

            if (Lifetime > 0f && _age >= Lifetime)
                IsEnabled = false;
        }

        private static float SimpleNoise(float x)
        {
            return MathF.Sin(x * 1.1f + MathF.Sin(x * 0.7f) * 3.14f) * 0.5f;
        }
    }

    /// <summary>光束点数据。</summary>
    public struct BeamPoint
    {
        public Vector3 Position;
        public float Width;
        public Vector4 Color;
        public Vector2 UV;
    }

    #endregion

    #region MathHelper (扩展引用)

    /// <summary>数学辅助 (如已在 ParticleSystem 中定义则忽略, 此处仅为保证编译)。</summary>
    internal static class MathHelperExt
    {
        [MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static float Lerp(float a, float b, float t) => a + (b - a) * Math.Clamp(t, 0f, 1f);
    }

    #endregion
}
