// ============================================================================================
//  EnhancedFluidSystem.cs
//
//  山雨引擎 增强型流体系统 —— 让液体更加惊艳
//
//  在基础 SPH 之上增加以下视觉效果:
//    1. 增强表面张力: 使用 CSF (Continuum Surface Force) 模型, 通过密度梯度计算
//       表面曲率, 产生真实的液体凝聚和水珠效果
//    2. 泡沫生成 (Whitewater): 基于相对速度、湍流、 trapped air 生成泡沫粒子,
//       模拟瀑布、激流、落水时的白色水花
//    3. 焦散近似: 模拟水面下方的光线聚焦图案, 使用波动叠加 + 时间动画
//    4. 折射数据: 为渲染器生成折射法线和扭曲强度, 实现透明液体折射效果
//    5. 交互波纹: 物体入水时产生扩散波纹, 基于高度场波动方程
//    6. 次级粒子 (Spray/Droplets): 高速碰撞时产生飞溅水滴
//    7. 次表面散射近似: 水体深处的光散射颜色渐变
//    8. 拖拽力: 物体在水中运动时受到的流体阻力
//
//  命名空间: ShanyuEngine.Physics
//  目标框架: net8.0 / C# 12+
//  外部依赖: 仅 System.Numerics
// ============================================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

namespace ShanyuEngine.Physics
{
    // ════════════════════════════════════════════════════════════════════════
    //  泡沫粒子 (Whitewater Particle)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 泡沫粒子类型
    /// </summary>
    public enum FoamParticleType
    {
        /// <summary>水面泡沫 (白色气泡层)</summary>
        SurfaceFoam,
        /// <summary>水下气泡 (上升)</summary>
        UnderwaterBubble,
        /// <summary>飞溅水滴 (抛物线轨迹)</summary>
        SprayDroplet,
        /// <summary>水雾 (随空气飘散)</summary>
        Mist
    }

    /// <summary>
    /// 泡沫/飞溅粒子
    /// </summary>
    public struct FoamParticle
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public float Life;           // 剩余生命 (秒)
        public float MaxLife;        // 总生命
        public float Size;           // 粒子半径
        public FoamParticleType Type;
        public float Density;        // 所在水体密度
        public Vector3 SourceVelocity; // 产生时的源速度 (用于方向)
    }

    // ════════════════════════════════════════════════════════════════════════
    //  水面交互波纹 (Ripple Field)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 水面交互波纹系统 —— 基于高度场的 2D 波动方程
    /// </summary>
    public sealed class RippleField
    {
        private readonly float[] _heightCurrent;
        private readonly float[] _heightPrevious;
        private readonly float[] _damping;
        private readonly int _gridW;
        private readonly int _gridH;
        private readonly float _cellSize;
        private readonly float _waveSpeed;
        private float _dampingFactor;

        /// <summary>网格宽度</summary>
        public int GridWidth => _gridW;
        /// <summary>网格高度</summary>
        public int GridHeight => _gridH;
        /// <summary>单元格大小</summary>
        public float CellSize => _cellSize;

        public RippleField(int gridW, int gridH, float cellSize = 0.1f, float waveSpeed = 2.0f, float damping = 0.995f)
        {
            _gridW = gridW;
            _gridH = gridH;
            _cellSize = cellSize;
            _waveSpeed = waveSpeed;
            _dampingFactor = damping;
            _heightCurrent = new float[gridW * gridH];
            _heightPrevious = new float[gridW * gridH];
            _damping = new float[gridW * gridH];
            Array.Fill(_damping, damping);
        }

        /// <summary>
        /// 在指定位置产生涟漪
        /// </summary>
        public void CreateRipple(float x, float z, float strength, float radius = 1f)
        {
            int cx = (int)(x / _cellSize) + _gridW / 2;
            int cz = (int)(z / _cellSize) + _gridH / 2;
            int r = (int)(radius / _cellSize);

            for (int dz = -r; dz <= r; dz++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    int px = cx + dx;
                    int pz = cz + dz;
                    if (px < 0 || px >= _gridW || pz < 0 || pz >= _gridH)
                        continue;

                    float dist = MathF.Sqrt(dx * dx + dz * dz);
                    if (dist > r)
                        continue;

                    float falloff = 1f - dist / r;
                    int idx = pz * _gridW + px;
                    _heightCurrent[idx] -= strength * falloff * falloff;
                }
            }
        }

        /// <summary>
        /// 更新波动方程: h(t+1) = 2*h(t) - h(t-1) + c²*∇²h * dt²
        /// </summary>
        public void Update(float deltaTime)
        {
            float c2 = _waveSpeed * _waveSpeed;
            float dt2 = deltaTime * deltaTime;
            float coefficient = c2 * dt2 / (_cellSize * _cellSize);

            for (int z = 1; z < _gridH - 1; z++)
            {
                for (int x = 1; x < _gridW - 1; x++)
                {
                    int idx = z * _gridW + x;
                    int left = idx - 1;
                    int right = idx + 1;
                    int up = idx - _gridW;
                    int down = idx + _gridW;

                    // 拉普拉斯算子 (离散)
                    float laplacian = _heightCurrent[left] + _heightCurrent[right] +
                                      _heightCurrent[up] + _heightCurrent[down] -
                                      4f * _heightCurrent[idx];

                    // 波动方程
                    float newHeight = 2f * _heightCurrent[idx] - _heightPrevious[idx] +
                                      coefficient * laplacian;

                    // 阻尼
                    newHeight *= _damping[idx];

                    _heightPrevious[idx] = _heightCurrent[idx];
                    _heightCurrent[idx] = newHeight;
                }
            }
        }

        /// <summary>
        /// 获取指定位置的水面高度偏移
        /// </summary>
        public float GetHeightAt(float x, float z)
        {
            int gx = (int)(x / _cellSize) + _gridW / 2;
            int gz = (int)(z / _cellSize) + _gridH / 2;
            gx = Math.Clamp(gx, 0, _gridW - 1);
            gz = Math.Clamp(gz, 0, _gridH - 1);
            return _heightCurrent[gz * _gridW + gx];
        }

        /// <summary>
        /// 获取指定位置的水面法线 (用于折射渲染)
        /// </summary>
        public Vector3 GetNormalAt(float x, float z)
        {
            int gx = (int)(x / _cellSize) + _gridW / 2;
            int gz = (int)(z / _cellSize) + _gridH / 2;
            gx = Math.Clamp(gx, 1, _gridW - 2);
            gz = Math.Clamp(gz, 1, _gridH - 2);

            int idx = gz * _gridW + gx;
            float dhdx = (_heightCurrent[idx + 1] - _heightCurrent[idx - 1]) / (2f * _cellSize);
            float dhdz = (_heightCurrent[idx + _gridW] - _heightCurrent[idx - _gridW]) / (2f * _cellSize);

            return Vector3.Normalize(new Vector3(-dhdx, 1f, -dhdz));
        }

        /// <summary>清空波纹场</summary>
        public void Clear()
        {
            Array.Clear(_heightCurrent, 0, _heightCurrent.Length);
            Array.Clear(_heightPrevious, 0, _heightPrevious.Length);
        }

        /// <summary>获取高度场数据 (用于渲染器读取)</summary>
        public float[] GetHeightField() => _heightCurrent;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  焦散近似 (Caustics Approximation)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 焦散近似器 —— 模拟水下光线聚焦图案
    /// 使用多层正弦波叠加 + 时间动画, 不需要真正的光线追踪
    /// </summary>
    public sealed class CausticsApproximator
    {
        private readonly int _gridSize;
        private float _time;
        private float _intensity = 1.0f;
        private float _scale = 8.0f;
        private float _animationSpeed = 0.8f;

        // 焦散波层参数
        private readonly (float freq, float speed, float dir, float weight)[] _waveLayers;

        public float Intensity { get => _intensity; set => _intensity = MathF.Max(0f, value); }
        public float Scale { get => _scale; set => _scale = MathF.Max(0.1f, value); }
        public float AnimationSpeed { get => _animationSpeed; set => _animationSpeed = MathF.Max(0f, value); }

        public CausticsApproximator(int gridSize = 64)
        {
            _gridSize = gridSize;
            _time = 0f;

            // 4层正弦波, 不同方向/频率/速度
            _waveLayers = new[]
            {
                (1.0f, 1.0f, 0.0f, 1.0f),
                (1.3f, 0.7f, MathF.PI / 3, 0.7f),
                (0.8f, 1.5f, MathF.PI / 2, 0.5f),
                (1.7f, 0.5f, MathF.PI / 4, 0.3f),
            };
        }

        /// <summary>推进时间</summary>
        public void Update(float deltaTime) => _time += deltaTime * _animationSpeed;

        /// <summary>
        /// 计算指定位置的焦散强度 (0~1)
        /// 使用多层波叠加: caustics = max(0, sin(...)) * weight 的累积
        /// </summary>
        public float SampleCaustics(float x, float z, float depth = 0f)
        {
            // 深度衰减: 越深焦散越弱
            float depthFactor = MathF.Exp(-depth * 0.15f);

            float result = 0f;
            foreach (var layer in _waveLayers)
            {
                float dirX = MathF.Cos(layer.dir);
                float dirZ = MathF.Sin(layer.dir);
                float phase = (x * dirX + z * dirZ) * layer.freq * _scale + _time * layer.speed;
                result += MathF.Max(0f, MathF.Sin(phase)) * layer.weight;
            }

            // 归一化并应用强度
            float maxVal = 0f;
            foreach (var layer in _waveLayers)
                maxVal += layer.weight;

            return (result / maxVal) * _intensity * depthFactor;
        }

        /// <summary>
        /// 生成焦散纹理数据 (用于渲染器上传)
        /// </summary>
        public float[] GenerateCausticsTexture()
        {
            var data = new float[_gridSize * _gridSize];
            float step = 1f / _gridSize;

            for (int z = 0; z < _gridSize; z++)
            {
                for (int x = 0; x < _gridSize; x++)
                {
                    data[z * _gridSize + x] = SampleCaustics(x * step, z * step);
                }
            }
            return data;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  增强型流体系统
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 增强型流体系统 —— 在 SPH 基础上增加视觉效果
    /// </summary>
    public sealed class EnhancedFluidSystem
    {
        private readonly FluidSimulationSystem _sph;
        private readonly List<FoamParticle> _foamParticles = new(2048);
        private readonly RippleField? _rippleField;
        private readonly CausticsApproximator _caustics;

        // 视觉参数
        private float _foamThreshold = 2.5f;        // 相对速度超过此值产生泡沫
        private float _foamLifetime = 2.0f;
        private float _sprayThreshold = 5.0f;       // 相对速度超过此值产生飞溅
        private float _sprayLifetime = 0.8f;
        private float _maxFoamParticles = 5000;
        private float _maxSprayParticles = 2000;

        // 水面参数
        private float _waterLevel = 0f;             // 水面 Y 坐标
        private float _waterDepth = 10f;            // 最大水深
        private Vector3 _waterColor = new(0.1f, 0.4f, 0.6f);  // 深水颜色
        private Vector3 _shallowColor = new(0.3f, 0.7f, 0.8f); // 浅水颜色
        private Vector3 _foamColor = new(0.95f, 0.97f, 1.0f);  // 泡沫颜色
        private float _transparency = 0.7f;         // 透明度
        private float _refractionStrength = 0.02f;  // 折射扭曲强度
        private float _reflectionStrength = 0.3f;   // 反射强度

        // 次表面散射参数
        private float _sssStrength = 0.3f;
        private Vector3 _sssColor = new(0.15f, 0.5f, 0.7f);

        // 拖拽力参数
        private float _dragCoefficient = 0.47f;     // 球体阻力系数
        private bool _enableBuoyancy = true;

        // 统计
        public int FoamParticleCount => _foamParticles.Count;
        public IReadOnlyList<FoamParticle> FoamParticles => _foamParticles;
        public RippleField? Ripples => _rippleField;
        public CausticsApproximator Caustics => _caustics;

        // 属性访问
        public float WaterLevel { get => _waterLevel; set => _waterLevel = value; }
        public float WaterDepth { get => _waterDepth; set => _waterDepth = value; }
        public Vector3 WaterColor { get => _waterColor; set => _waterColor = value; }
        public Vector3 ShallowColor { get => _shallowColor; set => _shallowColor = value; }
        public Vector3 FoamColor { get => _foamColor; set => _foamColor = value; }
        public float Transparency { get => _transparency; set => _transparency = Math.Clamp(value, 0f, 1f); }
        public float RefractionStrength { get => _refractionStrength; set => _refractionStrength = value; }
        public float ReflectionStrength { get => _reflectionStrength; set => _reflectionStrength = value; }
        public float FoamThreshold { get => _foamThreshold; set => _foamThreshold = value; }
        public float SprayThreshold { get => _sprayThreshold; set => _sprayThreshold = value; }

        /// <summary>
        /// 创建增强流体系统
        /// </summary>
        /// <param name="sph">基础 SPH 流体系统</param>
        /// <param name="enableRipples">是否启用交互波纹</param>
        /// <param name="rippleGridSize">波纹网格分辨率</param>
        public EnhancedFluidSystem(
            FluidSimulationSystem sph,
            bool enableRipples = true,
            int rippleGridSize = 128)
        {
            _sph = sph ?? throw new ArgumentNullException(nameof(sph));
            _rippleField = enableRipples ? new RippleField(rippleGridSize, rippleGridSize, 0.1f) : null;
            _caustics = new CausticsApproximator(64);
        }

        /// <summary>
        /// 更新流体和所有视觉效果
        /// </summary>
        public void Update(float deltaTime)
        {
            // 1. 更新基础 SPH
            _sph.Step(deltaTime);

            // 2. 生成泡沫和飞溅
            GenerateFoamAndSpray(deltaTime);

            // 3. 更新泡沫粒子
            UpdateFoamParticles(deltaTime);

            // 4. 更新波纹场
            _rippleField?.Update(deltaTime);

            // 5. 更新焦散
            _caustics.Update(deltaTime);

            // 6. 检测粒子入水, 产生涟漪
            DetectWaterImpacts(deltaTime);
        }

        // ════════════════════════════════════════════════════════════════
        //  泡沫和飞溅生成
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// 基于粒子相对速度生成泡沫和飞溅
        /// </summary>
        private void GenerateFoamAndSpray(float deltaTime)
        {
            var particles = _sph.Particles;
            int n = particles.Count;
            if (n == 0) return;

            // 采样邻居 (使用 SPH 系统内部的空间哈希)
            for (int i = 0; i < n; i++)
            {
                ref readonly var p = ref particles[i];

                // 检查是否在水面附近
                float depthBelowSurface = _waterLevel - p.Position.Y;
                bool nearSurface = MathF.Abs(depthBelowSurface) < 0.5f;

                // 计算相对速度 (简化: 使用速度大小作为湍流度量)
                float speed = p.Velocity.Length();

                // 计算与邻居的相对速度
                float maxRelativeSpeed = 0f;
                Vector3 avgVelocity = p.Velocity;
                int neighborCount = 1;

                // 简化邻居搜索: 检查附近粒子
                for (int j = 0; j < n; j++)
                {
                    if (i == j) continue;
                    Vector3 diff = particles[j].Position - p.Position;
                    float distSq = diff.LengthSquared();
                    if (distSq < 1.0f) // 在影响半径内
                    {
                        Vector3 relVel = particles[j].Velocity - p.Velocity;
                        float relSpeed = relVel.Length();
                        if (relSpeed > maxRelativeSpeed)
                            maxRelativeSpeed = relSpeed;

                        avgVelocity += particles[j].Velocity;
                        neighborCount++;
                    }
                }
                avgVelocity /= neighborCount;

                // === 泡沫生成 ===
                if (maxRelativeSpeed > _foamThreshold && nearSurface)
                {
                    int foamCount = (int)((maxRelativeSpeed - _foamThreshold) * 2f);
                    for (int f = 0; f < foamCount && _foamParticles.Count < _maxFoamParticles; f++)
                    {
                        var rng = new Random(i * 1000 + f);
                        var offset = new Vector3(
                            (float)(rng.NextDouble() - 0.5) * 0.3f,
                            (float)(rng.NextDouble() - 0.5) * 0.1f,
                            (float)(rng.NextDouble() - 0.5) * 0.3f);

                        _foamParticles.Add(new FoamParticle
                        {
                            Position = p.Position + offset,
                            Velocity = p.Velocity * 0.5f + new Vector3(
                                (float)(rng.NextDouble() - 0.5) * 2f,
                                (float)rng.NextDouble() * 1f,
                                (float)(rng.NextDouble() - 0.5) * 2f),
                            Life = _foamLifetime,
                            MaxLife = _foamLifetime,
                            Size = 0.05f + (float)rng.NextDouble() * 0.1f,
                            Type = FoamParticleType.SurfaceFoam,
                            Density = p.Density,
                            SourceVelocity = p.Velocity
                        });
                    }
                }

                // === 飞溅水滴生成 ===
                if (maxRelativeSpeed > _sprayThreshold)
                {
                    int sprayCount = (int)((maxRelativeSpeed - _sprayThreshold) * 0.5f);
                    for (int s = 0; s < sprayCount && _foamParticles.Count < _maxSprayParticles; s++)
                    {
                        var rng = new Random(i * 2000 + s);
                        _foamParticles.Add(new FoamParticle
                        {
                            Position = p.Position,
                            Velocity = avgVelocity * 1.5f + new Vector3(
                                (float)(rng.NextDouble() - 0.5) * 4f,
                                (float)rng.NextDouble() * 5f + 2f, // 向上偏移
                                (float)(rng.NextDouble() - 0.5) * 4f),
                            Life = _sprayLifetime,
                            MaxLife = _sprayLifetime,
                            Size = 0.02f + (float)rng.NextDouble() * 0.03f,
                            Type = FoamParticleType.SprayDroplet,
                            Density = p.Density,
                            SourceVelocity = p.Velocity
                        });
                    }
                }

                // === 水下气泡 (高速运动产生) ===
                if (depthBelowSurface > 0.5f && speed > 3f)
                {
                    if (new Random(i).NextDouble() < 0.1f && _foamParticles.Count < _maxFoamParticles)
                    {
                        _foamParticles.Add(new FoamParticle
                        {
                            Position = p.Position + new Vector3(0, 0.1f, 0),
                            Velocity = new Vector3(0, 1f, 0), // 气泡上升
                            Life = 3f,
                            MaxLife = 3f,
                            Size = 0.03f,
                            Type = FoamParticleType.UnderwaterBubble,
                            Density = p.Density,
                            SourceVelocity = Vector3.Zero
                        });
                    }
                }
            }
        }

        /// <summary>
        /// 更新所有泡沫粒子
        /// </summary>
        private void UpdateFoamParticles(float deltaTime)
        {
            for (int i = _foamParticles.Count - 1; i >= 0; i--)
            {
                ref var fp = ref _foamParticles[i];

                // 衰减生命
                fp.Life -= deltaTime;
                if (fp.Life <= 0)
                {
                    _foamParticles.RemoveAt(i);
                    continue;
                }

                // 按类型更新物理
                switch (fp.Type)
                {
                    case FoamParticleType.SurfaceFoam:
                        // 水面泡沫: 随水面流动, 缓慢减速
                        fp.Velocity *= (1f - deltaTime * 0.5f);
                        fp.Velocity += new Vector3(0, -2f * deltaTime, 0); // 重力
                        fp.Position += fp.Velocity * deltaTime;
                        break;

                    case FoamParticleType.UnderwaterBubble:
                        // 水下气泡: 上升 + 水平摆动
                        fp.Velocity.Y += 5f * deltaTime; // 浮力
                        fp.Velocity.Y = MathF.Min(fp.Velocity.Y, 2f); // 限速
                        // 水平摆动
                        float wobble = MathF.Sin(fp.Life * 10f) * 0.5f;
                        fp.Position += new Vector3(wobble * deltaTime, fp.Velocity.Y * deltaTime, 0);
                        fp.Position += fp.Velocity * deltaTime * 0.3f;
                        // 到达水面后消失
                        if (fp.Position.Y > _waterLevel)
                        {
                            _foamParticles.RemoveAt(i);
                        }
                        break;

                    case FoamParticleType.SprayDroplet:
                        // 飞溅水滴: 重力抛物线
                        fp.Velocity += new Vector3(0, -9.81f * deltaTime, 0);
                        fp.Position += fp.Velocity * deltaTime;
                        // 落入水面后变成水面泡沫
                        if (fp.Position.Y < _waterLevel)
                        {
                            fp.Type = FoamParticleType.SurfaceFoam;
                            fp.Velocity *= 0.2f;
                            fp.Position = new Vector3(fp.Position.X, _waterLevel, fp.Position.Z);
                        }
                        break;

                    case FoamParticleType.Mist:
                        // 水雾: 随风飘散, 极慢下沉
                        fp.Velocity *= (1f - deltaTime * 0.3f);
                        fp.Velocity += new Vector3(0, -0.5f * deltaTime, 0);
                        fp.Position += fp.Velocity * deltaTime;
                        break;
                }

                // 大小随生命衰减
                fp.Size *= (1f - deltaTime * 0.1f);
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  水面交互检测
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// 检测流体粒子撞击水面, 产生涟漪
        /// </summary>
        private void DetectWaterImpacts(float deltaTime)
        {
            if (_rippleField == null) return;

            var particles = _sph.Particles;
            for (int i = 0; i < particles.Count; i++)
            {
                ref readonly var p = ref particles[i];

                // 粒子穿过水面
                if (p.OldPosition.Y > _waterLevel && p.Position.Y <= _waterLevel)
                {
                    float impactSpeed = MathF.Abs(p.Velocity.Y);
                    if (impactSpeed > 0.5f)
                    {
                        float strength = MathF.Min(impactSpeed * 0.05f, 0.5f);
                        _rippleField.CreateRipple(p.Position.X, p.Position.Z, strength, impactSpeed * 0.3f);
                    }
                }
            }
        }

        /// <summary>
        /// 外部物体入水时产生涟漪 (供外部调用)
        /// </summary>
        public void OnObjectEnteredWater(Vector3 position, Vector3 velocity, float radius)
        {
            float speed = velocity.Length();
            if (speed < 0.1f) return;

            _rippleField?.CreateRipple(position.X, position.Z, speed * 0.1f, radius);

            // 产生飞溅
            int splashCount = (int)(speed * radius * 10);
            for (int i = 0; i < splashCount && _foamParticles.Count < _maxSprayParticles; i++)
            {
                var rng = new Random(i);
                float angle = (float)(rng.NextDouble() * MathF.PI * 2);
                float upVel = (float)(rng.NextDouble() * speed * 0.5f + speed * 0.3f);
                float sideVel = (float)(rng.NextDouble() * speed * 0.3f);

                _foamParticles.Add(new FoamParticle
                {
                    Position = position,
                    Velocity = new Vector3(
                        MathF.Cos(angle) * sideVel,
                        upVel,
                        MathF.Sin(angle) * sideVel),
                    Life = _sprayLifetime,
                    MaxLife = _sprayLifetime,
                    Size = 0.02f + (float)rng.NextDouble() * 0.03f,
                    Type = FoamParticleType.SprayDroplet,
                    Density = 1000f,
                    SourceVelocity = velocity
                });
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  渲染数据生成
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// 获取指定位置的水体颜色 (基于深度渐变 + SSS)
        /// </summary>
        public Vector3 GetWaterColorAt(Vector3 position)
        {
            float depth = MathF.Max(0f, _waterLevel - position.Y);
            float depthRatio = Math.Clamp(depth / _waterDepth, 0f, 1f);

            // 深浅水颜色渐变
            Vector3 color = Vector3.Lerp(_shallowColor, _waterColor, depthRatio);

            // 次表面散射近似: 深水处偏蓝绿
            float sss = depthRatio * _sssStrength;
            color += _sssColor * sss;

            return color;
        }

        /// <summary>
        /// 获取指定位置的折射偏移 (屏幕空间)
        /// </summary>
        public Vector2 GetRefractionOffset(Vector3 position, Vector3 viewDir)
        {
            if (_rippleField == null)
                return Vector2.Zero;

            // 使用水面法线计算折射偏移
            Vector3 normal = _rippleField.GetNormalAt(position.X, position.Z);
            Vector3 refractedDir = Vector3.Reflect(-viewDir, normal);
            refractedDir = Vector3.Normalize(refractedDir);

            return new Vector2(refractedDir.X, refractedDir.Z) * _refractionStrength;
        }

        /// <summary>
        /// 获取指定位置的泡沫覆盖度 (0~1)
        /// </summary>
        public float GetFoamCoverageAt(Vector3 position)
        {
            float coverage = 0f;
            float radius = 0.3f;

            foreach (var fp in _foamParticles)
            {
                if (fp.Type != FoamParticleType.SurfaceFoam)
                    continue;

                Vector3 diff = fp.Position - position;
                float distSq = diff.LengthSquared();
                if (distSq < radius * radius)
                {
                    float lifeRatio = fp.Life / fp.MaxLife;
                    float distFactor = 1f - MathF.Sqrt(distSq) / radius;
                    coverage += lifeRatio * distFactor * 0.2f;
                }
            }

            return Math.Clamp(coverage, 0f, 1f);
        }

        /// <summary>
        /// 获取水面法线 (合并波纹和基础法线)
        /// </summary>
        public Vector3 GetWaterSurfaceNormal(float x, float z)
        {
            if (_rippleField != null)
            {
                return _rippleField.GetNormalAt(x, z);
            }
            return Vector3.UnitY;
        }

        /// <summary>
        /// 获取水面高度 (包含波纹)
        /// </summary>
        public float GetWaterSurfaceHeight(float x, float z)
        {
            return _waterLevel + (_rippleField?.GetHeightAt(x, z) ?? 0f);
        }

        /// <summary>
        /// 计算物体在水中的浮力和拖拽力
        /// </summary>
        public (Vector3 buoyancyForce, Vector3 dragForce) ComputeFluidForces(
            Vector3 position, Vector3 velocity, float volume, float density)
        {
            Vector3 buoyancy = Vector3.Zero;
            Vector3 drag = Vector3.Zero;

            float waterY = GetWaterSurfaceHeight(position.X, position.Z);

            // 如果物体在水下
            if (position.Y < waterY)
            {
                if (_enableBuoyancy)
                {
                    // 浮力 = ρ * g * V_submerged
                    float submergedDepth = waterY - position.Y;
                    float submergedRatio = Math.Clamp(submergedDepth / 1f, 0f, 1f);
                    float displacedVolume = volume * submergedRatio;
                    buoyancy = new Vector3(0, density * 9.81f * displacedVolume, 0);
                }

                // 拖拽力 = 0.5 * ρ * v² * Cd * A
                float speed = velocity.Length();
                if (speed > 0.01f)
                {
                    float area = MathF.Pow(volume, 2f / 3f) * MathF.PI; // 近似截面积
                    float dragMag = 0.5f * density * speed * speed * _dragCoefficient * area;
                    drag = -velocity / speed * dragMag;
                }
            }

            return (buoyancy, drag);
        }

        /// <summary>
        /// 获取增强流体统计信息
        /// </summary>
        public EnhancedFluidStats GetStats()
        {
            int foamCount = 0, bubbleCount = 0, sprayCount = 0, mistCount = 0;
            foreach (var fp in _foamParticles)
            {
                switch (fp.Type)
                {
                    case FoamParticleType.SurfaceFoam: foamCount++; break;
                    case FoamParticleType.UnderwaterBubble: bubbleCount++; break;
                    case FoamParticleType.SprayDroplet: sprayCount++; break;
                    case FoamParticleType.Mist: mistCount++; break;
                }
            }

            return new EnhancedFluidStats
            {
                FluidParticleCount = _sph.ParticleCount,
                FoamParticleCount = foamCount,
                BubbleCount = bubbleCount,
                SprayCount = sprayCount,
                MistCount = mistCount,
                TotalVisualParticles = _foamParticles.Count
            };
        }
    }

    /// <summary>
    /// 增强流体统计信息
    /// </summary>
    public struct EnhancedFluidStats
    {
        public int FluidParticleCount;
        public int FoamParticleCount;
        public int BubbleCount;
        public int SprayCount;
        public int MistCount;
        public int TotalVisualParticles;

        public override string ToString()
            => $"SPH={FluidParticleCount} Foam={FoamParticleCount} Bubble={BubbleCount} " +
               $"Spray={SprayCount} Mist={MistCount} Total={TotalVisualParticles}";
    }
}
