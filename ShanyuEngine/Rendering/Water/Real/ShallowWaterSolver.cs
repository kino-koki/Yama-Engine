using System;
using System.Numerics;

namespace ShanyuEngine.Rendering.Water.Real
{
    /// <summary>
    /// 浅水方程求解器 - 基于真实流体力学，但经过GPU优化
    /// 使用高度场法，复杂度O(n)而非O(n³)，可在CPU上快速求解
    /// 目标：完全真实的物理效果 + 极小的GPU渲染压力
    /// </summary>
    public sealed class ShallowWaterSolver
    {
        private readonly float[,] _height;      // 水面高度
        private readonly float[,] _velocity;     // 水平速度
        private readonly float[,] _targetHeight; // 目标高度（地形+水深）
        private readonly bool[,] _isWater;       // 是否有水
        
        private readonly int _resolution;        // 网格分辨率
        private readonly float _spacing;          // 网格间距（米）
        private readonly WaterPhysicsSettings _settings;
        
        // 物理常数（真实值）
        private const float Gravity = 9.81f;      // 重力加速度 m/s²
        private const float AirDensity = 1.225f; // 空气密度 kg/m³
        private const float WaterDensity = 1000f; // 水密度 kg/m³
        
        public int Resolution => _resolution;
        public float Spacing => _spacing;
        public float CurrentTime { get; private set; }
        
        public ShallowWaterSolver(int resolution, float spacing, WaterPhysicsSettings settings)
        {
            _resolution = resolution;
            _spacing = spacing;
            _settings = settings ?? new WaterPhysicsSettings();
            
            _height = new float[resolution, resolution];
            _velocity = new float[resolution, resolution];
            _targetHeight = new float[resolution, resolution];
            _isWater = new bool[resolution, resolution];
            
            InitializeWater();
        }
        
        /// <summary>
        /// 初始化水面
        /// </summary>
        private void InitializeWater()
        {
            for (int x = 0; x < _resolution; x++)
            {
                for (int y = 0; y < _resolution; y++)
                {
                    _height[x, y] = 0.5f;  // 初始水位0.5m
                    _velocity[x, y] = 0f;
                    _targetHeight[x, y] = 0.5f;
                    _isWater[x, y] = _settings.InitialWaterLevel > 0;
                    
                    if (_isWater[x, y])
                    {
                        _height[x, y] = _settings.InitialWaterLevel;
                        _targetHeight[x, y] = _settings.InitialWaterLevel;
                    }
                }
            }
        }
        
        /// <summary>
        /// 设置地形高度（影响目标水位）
        /// </summary>
        public void SetTerrain(float[,] terrain, float waterDepth)
        {
            for (int x = 0; x < _resolution; x++)
            {
                for (int y = 0; y < _resolution; y++)
                {
                    _targetHeight[x, y] = terrain[x, y] + waterDepth;
                }
            }
        }
        
        /// <summary>
        /// 在指定位置添加水
        /// </summary>
        public void AddWater(float centerX, float centerZ, float radius, float amount)
        {
            int gridX = WorldToGrid(centerX);
            int gridY = WorldToGrid(centerZ);
            int gridRadius = (int)MathF.Ceiling(radius / _spacing);
            
            for (int x = -gridRadius; x <= gridRadius; x++)
            {
                for (int y = -gridRadius; y <= gridRadius; y++)
                {
                    int tx = gridX + x;
                    int ty = gridY + y;
                    
                    if (tx >= 0 && tx < _resolution && ty >= 0 && ty < _resolution)
                    {
                        float dist = MathF.Sqrt(x * x + y * y) * _spacing;
                        if (dist <= radius)
                        {
                            float falloff = 1.0f - dist / radius;
                            _height[tx, ty] += amount * falloff;
                            _targetHeight[tx, ty] = _height[tx, ty];
                            _isWater[tx, ty] = true;
                        }
                    }
                }
            }
        }
        
        /// <summary>
        /// 在指定位置添加扰动（如雨滴）
        /// </summary>
        public void AddPerturbation(float centerX, float centerZ, float radius, float strength)
        {
            int gridX = WorldToGrid(centerX);
            int gridY = WorldToGrid(centerZ);
            int gridRadius = (int)MathF.Ceiling(radius / _spacing);
            
            for (int x = -gridRadius; x <= gridRadius; x++)
            {
                for (int y = -gridRadius; y <= gridRadius; y++)
                {
                    int tx = gridX + x;
                    int ty = gridY + y;
                    
                    if (tx >= 0 && tx < _resolution && ty >= 0 && ty < _resolution && _isWater[tx, ty])
                    {
                        float dist = MathF.Sqrt(x * x + y * y) * _spacing;
                        if (dist <= radius)
                        {
                            float gaussian = MathF.Exp(-0.5f * dist * dist / (radius * radius * 0.3f));
                            _velocity[tx, ty] += strength * gaussian;
                        }
                    }
                }
            }
        }
        
        /// <summary>
        /// 物理模拟步进
        /// </summary>
        public void Step(float deltaTime)
        {
            CurrentTime += deltaTime;
            
            // 1. 浅水方程求解
            SolveShallowWaterEquations(deltaTime);
            
            // 2. 应用阻尼
            ApplyDamping();
            
            // 3. 应用边界条件
            ApplyBoundaryConditions();
        }
        
        /// <summary>
        /// 浅水方程求解 - 基于真实流体力学
        /// </summary>
        private void SolveShallowWaterEquations(float deltaTime)
        {
            float damping = _settings.Damping;
            float diffusion = _settings.Diffusion;
            float c = _settings.WaveSpeed;  // 波速 c = sqrt(g * h)
            
            // 波动方程：∂²h/∂t² = c² ∇²h - damping * ∂h/∂t
            // 使用显式有限差分法求解
            
            float[,] newHeight = new float[_resolution, _resolution];
            float[,] newVelocity = new float[_resolution, _resolution];
            
            for (int x = 1; x < _resolution - 1; x++)
            {
                for (int y = 1; y < _resolution - 1; y++)
                {
                    if (!_isWater[x, y])
                        continue;
                        
                    // 拉普拉斯算子：∇²h = (h[x+1] + h[x-1] + h[y+1] + h[y-1] - 4h) / Δx²
                    float laplacian = (
                        _height[x + 1, y] + _height[x - 1, y] + 
                        _height[x, y + 1] + _height[x, y - 1] - 
                        4 * _height[x, y]
                    ) / (_spacing * _spacing);
                    
                    // 波动方程
                    float acceleration = c * c * laplacian - damping * _velocity[x, y];
                    
                    // 更新速度
                    newVelocity[x, y] = _velocity[x, y] + acceleration * deltaTime;
                    
                    // 更新高度
                    newHeight[x, y] = _height[x, y] + newVelocity[x, y] * deltaTime;
                    
                    // 避免负高度
                    newHeight[x, y] = MathF.Max(0.001f, newHeight[x, y]);
                }
            }
            
            // 复制结果
            for (int x = 1; x < _resolution - 1; x++)
            {
                for (int y = 1; y < _resolution - 1; y++)
                {
                    if (_isWater[x, y])
                    {
                        _height[x, y] = newHeight[x, y];
                        _velocity[x, y] = newVelocity[x, y];
                    }
                }
            }
        }
        
        /// <summary>
        /// 应用阻尼
        /// </summary>
        private void ApplyDamping()
        {
            float damping = 1.0f - _settings.Damping;
            
            for (int x = 0; x < _resolution; x++)
            {
                for (int y = 0; y < _resolution; y++)
                {
                    if (_isWater[x, y])
                    {
                        _velocity[x, y] *= damping;
                    }
                }
            }
        }
        
        /// <summary>
        /// 应用边界条件
        /// </summary>
        private void ApplyBoundaryConditions()
        {
            // 反射边界条件（波在边界反射）
            for (int x = 0; x < _resolution; x++)
            {
                for (int y = 0; y < _resolution; y++)
                {
                    if (x == 0 || x == _resolution - 1 || y == 0 || y == _resolution - 1)
                    {
                        if (_isWater[x, y])
                        {
                            _velocity[x, y] *= -0.5f;  // 反射并衰减
                        }
                    }
                }
            }
        }
        
        /// <summary>
        /// 获取指定位置的水面高度
        /// </summary>
        public float GetHeight(float worldX, float worldZ)
        {
            int gridX = WorldToGrid(worldX);
            int gridY = WorldToGrid(worldZ);
            
            if (gridX >= 0 && gridX < _resolution && gridY >= 0 && gridY < _resolution)
            {
                return _height[gridX, gridY];
            }
            
            return 0f;
        }
        
        /// <summary>
        /// 获取水面高度场（用于渲染）
        /// </summary>
        public float[,] GetHeightField()
        {
            return _height;
        }
        
        /// <summary>
        /// 获取水面速度场（用于渲染法线）
        /// </summary>
        public float[,] GetVelocityField()
        {
            return _velocity;
        }
        
        /// <summary>
        /// 检查指定位置是否有水
        /// </summary>
        public bool HasWater(float worldX, float worldZ)
        {
            int gridX = WorldToGrid(worldX);
            int gridY = WorldToGrid(worldZ);
            
            if (gridX >= 0 && gridX < _resolution && gridY >= 0 && gridY < _resolution)
            {
                return _isWater[gridX, gridY];
            }
            
            return false;
        }
        
        /// <summary>
        /// 获取水体体积（用于水位控制）
        /// </summary>
        public float GetTotalVolume()
        {
            float volume = 0f;
            
            for (int x = 0; x < _resolution; x++)
            {
                for (int y = 0; y < _resolution; y++)
                {
                    if (_isWater[x, y])
                    {
                        volume += _height[x, y] * _spacing * _spacing;
                    }
                }
            }
            
            return volume;
        }
        
        private int WorldToGrid(float worldCoord)
        {
            return (int)MathF.Round(worldCoord / _spacing + _resolution / 2);
        }
        
        private float GridToWorld(int gridCoord)
        {
            return (gridCoord - _resolution / 2) * _spacing;
        }
    }

    /// <summary>
    /// 水物理设置 - 真实的物理参数
    /// </summary>
    public sealed class WaterPhysicsSettings
    {
        /// <summary>初始水位（米）</summary>
        public float InitialWaterLevel { get; set; } = 0.5f;
        
        /// <summary>波速（m/s）- 基于 sqrt(g * h)</summary>
        public float WaveSpeed { get; set; } = 2.2f;  // sqrt(9.81 * 0.5)
        
        /// <summary>阻尼系数 - 模拟水的粘性</summary>
        public float Damping { get; set; } = 0.02f;
        
        /// <summary>扩散系数 - 模拟波的能量扩散</summary>
        public float Diffusion { get; set; } = 0.1f;
        
        /// <summary>表面张力系数</summary>
        public float SurfaceTension { get; set; } = 0.0728f;  // 水的表面张力 N/m
        
        /// <summary>粘度系数</summary>
        public float Viscosity { get; set; } = 0.001f;  // 水的动力粘度 Pa·s
    }

    /// <summary>
    /// 真实的水物理特性
    /// </summary>
    public static class WaterPhysicsConstants
    {
        // 物理常数
        public const float Gravity = 9.81f;              // 重力加速度 m/s²
        public const float WaterDensity = 1000f;         // 水密度 kg/m³
        public const float AirDensity = 1.225f;          // 空气密度 kg/m³
        public const float SurfaceTension = 0.0728f;     // 表面张力 N/m（20°C）
        public const float Viscosity = 0.001f;           // 动力粘度 Pa·s（20°C）
        public const float BulkModulus = 2.2e9f;          // 体积模量 Pa
        public const float SpecificHeat = 4186f;         // 比热容 J/(kg·K)
        public const float ThermalConductivity = 0.6f;  // 热导率 W/(m·K)
        
        // 光学特性
        public const float RefractiveIndex = 1.333f;     // 折射率
        public const float ReflectivityNormal = 0.02f;   // 垂直入射反射率
        public const float AbsorptionCoefficient = 0.1f; // 吸收系数
        public const float ScatteringCoefficient = 0.1f; // 散射系数
        
        // 颜色特性（基于真实光谱）
        public static readonly Vector3 ShallowWaterColor = new Vector3(0.2f, 0.5f, 0.8f);  // 浅水：蓝绿色
        public static readonly Vector3 DeepWaterColor = new Vector3(0.05f, 0.1f, 0.3f);   // 深水：深蓝色
        public static readonly Vector3 MurkyWaterColor = new Vector3(0.1f, 0.2f, 0.15f);  // 浑水：浑浊色
        public static readonly Vector3 FoamColor = new Vector3(0.9f, 0.95f, 1.0f);    // 泡沫：白色
        
        // 深度颜色渐变参数
        public const float ShallowDepth = 0.2f;     // 浅水深度
        public const float DeepDepth = 10.0f;       // 深水深度
        public const float MaxVisibility = 15.0f;   // 最大可见深度
    }
}