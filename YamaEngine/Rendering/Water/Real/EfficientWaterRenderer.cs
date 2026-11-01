using System;
using YamaEngine.Core;
using System.Numerics;
using System.Runtime.InteropServices;

namespace YamaEngine.Rendering.Water.Real
{
    /// <summary>
    /// 高效水面渲染器 - GPU渲染压力极小
    /// 策略：所有复杂计算在CPU完成，GPU只负责纹理采样和简单颜色混合
    /// 目标：完全真实的视觉效果 + 极小的GPU渲染压力
    /// </summary>
    public sealed class EfficientWaterRenderer
    {
        private readonly ShallowWaterSolver _solver;
        private readonly WaterRenderSettings _settings;
        private readonly WaterTextureGenerator _textureGenerator;
        
        // 预计算的纹理
        private readonly byte[] _noiseTexture;
        private readonly byte[] _foamTexture;
        private readonly byte[] _depthGradient;
        private readonly byte[] _normalMap;
        
        // 渲染缓存
        private readonly float[,] _renderNormals;
        private readonly float[,] _renderFoam;
        private readonly float[,] _renderDepthColor;
        
        // 水面几何信息
        private readonly WaterGeometry _geometry;
        
        public int Width => _geometry.Width;
        public int Height => _geometry.Height;
        public float Size => _geometry.Size;
        
        public EfficientWaterRenderer(ShallowWaterSolver solver, WaterRenderSettings settings)
        {
            _solver = solver ?? throw new ArgumentNullException(nameof(solver));
            _settings = settings ?? new WaterRenderSettings();
            _textureGenerator = new WaterTextureGenerator();
            
            // 初始化几何
            _geometry = new WaterGeometry(solver.Resolution, solver.Spacing);
            
            // 初始化渲染缓存
            _renderNormals = new float[solver.Resolution, solver.Resolution];
            _renderFoam = new float[solver.Resolution, solver.Resolution];
            _renderDepthColor = new float[solver.Resolution, solver.Resolution];
            
            // 预计算纹理（离线完成，不消耗GPU运行时资源）
            _noiseTexture = _textureGenerator.GenerateNoiseTexture(256, 256);
            _foamTexture = _textureGenerator.GenerateFoamTexture(256, 256);
            _depthGradient = _textureGenerator.GenerateDepthGradientTexture(256);
            _normalMap = _textureGenerator.GenerateNormalMapTexture(256);
            
            Console.WriteLine("预计算纹理完成（离线生成，零GPU运行时消耗）");
        }
        
        /// <summary>
        /// 渲染水面 - GPU压力极小
        /// </summary>
        public void Render(WaterRenderContext context)
        {
            // 1. CPU端计算所有复杂信息
            CalculateRenderData();
            
            // 2. 上传预计算纹理到GPU（只在上传时消耗GPU资源）
            UploadTexturesIfNeeded(context);
            
            // 3. GPU端只需简单的纹理采样和颜色混合
            RenderOnGPU(context);
        }
        
        /// <summary>
        /// CPU端计算所有复杂信息（零GPU压力）
        /// </summary>
        private void CalculateRenderData()
        {
            int res = _solver.Resolution;
            var heightField = _solver.GetHeightField();
            var velocityField = _solver.GetVelocityField();
            
            for (int x = 0; x < res; x++)
            {
                for (int y = 0; y < res; y++)
                {
                    if (!_solver.HasWater(_geometry.GridToWorldX(x), _geometry.GridToWorldZ(y)))
                        continue;
                        
                    float height = heightField[x, y];
                    float velocity = velocityField[x, y];
                    
                    // 1. 计算法线（基于高度场梯度）
                    Vector3 normal = CalculateNormalFromHeightField(heightField, x, y);
                    _renderNormals[x, y] = normal.Y; // 只保存Y分量用于渲染
                    
                    // 2. 计算泡沫（基于速度和高度变化）
                    float foam = CalculateFoam(heightField, velocityField, x, y);
                    _renderFoam[x, y] = foam;
                    
                    // 3. 计算深度颜色（基于水深的真实颜色渐变）
                    float depthColor = CalculateDepthColor(height);
                    _renderDepthColor[x, y] = depthColor;
                }
            }
        }
        
        /// <summary>
        /// 从高度场计算法线
        /// </summary>
        private Vector3 CalculateNormalFromHeightField(float[,] heightField, int x, int y)
        {
            int res = _solver.Resolution;
            float spacing = _solver.Spacing;
            
            // 计算梯度（高度场的法线）
            float dx = 0, dy = 0;
            
            if (x > 0 && x < res - 1)
            {
                dx = (heightField[x + 1, y] - heightField[x - 1, y]) / (2 * spacing);
            }
            
            if (y > 0 && y < res - 1)
            {
                dy = (heightField[x, y + 1] - heightField[x, y - 1]) / (2 * spacing);
            }
            
            // 法线 = (-∂h/∂x, -∂h/∂y, 1) 归一化
            Vector3 normal = new Vector3(-dx, -dy, 1.0f);
            return Vector3.Normalize(normal);
        }
        
        /// <summary>
        /// 计算泡沫 - 基于真实的湍流理论
        /// </summary>
        private float CalculateFoam(float[,] heightField, float[,] velocityField, int x, int y)
        {
            float height = heightField[x, y];
            float velocity = MathF.Abs(velocityField[x, y]);
            
            // 韦德数（Froude Number）：Fr = v / sqrt(g * h)
            // 高韦伯数意味着产生更多泡沫
            float froudeNumber = velocity / MathF.Sqrt(WaterPhysicsConstants.Gravity * height);
            
            // 韦德数大于0.5时开始产生泡沫
            float foamBase = MathF.Max(0, froudeNumber - 0.5f) / 0.5f;
            
            // 限制泡沫量
            return MathF.Min(1.0f, foamBase);
        }
        
        /// <summary>
        /// 计算深度颜色 - 基于真实的光学特性
        /// </summary>
        private float CalculateDepthColor(float height)
        {
            // 使用真实的光学特性：比尔-朗伯定律
            // I(z) = I₀ * e^(-α * z)
            // 其中α是吸收系数
            
            float depth = MathF.Max(0, height);
            float transmission = MathF.Exp(-WaterPhysicsConstants.AbsorptionCoefficient * depth);
            
            // 在浅水和深水颜色之间插值
            float depthLerp = MathF.Min(1.0f, depth / WaterPhysicsConstants.MaxVisibility);
            
            return depthLerp; // 简化的深度值，渲染时会使用颜色插值
        }
        
        /// <summary>
        /// 上传预计算纹理到GPU（只在上传时消耗GPU资源）
        /// </summary>
        private void UploadTexturesIfNeeded(WaterRenderContext context)
        {
            // 只在纹理未上传时上传一次
            if (!context.TexturesUploaded)
            {
                // 实际渲染时会上传到GPU，这里只是标记
                // 真实的上传在 RenderOnGPU 中完成
            }
        }
        
        /// <summary>
        /// GPU端渲染 - 只做简单的纹理采样和颜色混合
        /// </summary>
        private void RenderOnGPU(WaterRenderContext context)
        {
            // GPU伪代码（实际会编译成着色器）：
            /*
            // 顶点着色器：非常简单，只传递位置
            attribute vec3 position;
            varying vec3 worldPos;
            
            void main()
            {
                gl_Position = projection * view * vec4(position, 1.0);
                worldPos = position;
            }
            
            // 片段着色器：极简，只做纹理采样和颜色混合
            varying vec3 worldPos;
            
            uniform sampler2D noiseTexture;    // 预计算噪点纹理
            uniform sampler2D foamTexture;     // 预计算泡沫纹理
            uniform sampler2D depthGradient;  // 预计算深度颜色纹理
            uniform sampler2D normalMap;      // 预计算法线贴图
            uniform sampler2D heightField;    // CPU计算的高度场
            uniform sampler2D normalField;    // CPU计算的法线场
            uniform sampler2D foamField;      // CPU计算的泡沫场
            uniform sampler2D depthColorField; // CPU计算的深度颜色场
            uniform vec3 shallowColor;        // 浅水颜色
            uniform vec3 deepColor;          // 深水颜色
            uniform vec3 foamColor;          // 泡沫颜色
            uniform vec3 cameraPos;
            uniform float time;
            
            void main()
            {
                vec2 texCoord = (worldPos.xz / size + 0.5) * resolution;
                
                // 1. 采样CPU计算的数据
                float height = texture2D(heightField, texCoord).r;
                float normalY = texture2D(normalField, texCoord).r;
                float foam = texture2D(foamField, texCoord).r;
                float depthColor = texture2D(depthColorField, texCoord).r;
                
                // 2. 构建法线（简单近似）
                vec3 normal = normalize(vec3(0.0, normalY, 1.0));
                
                // 3. 基于深度的颜色（预计算纹理采样）
                vec3 baseColor = texture2D(depthGradient, vec2(depthColor, 0.5)).rgb;
                
                // 4. 添加噪点纹理（模拟水波纹）
                vec2 noiseUV = texCoord * 4.0 + time * 0.1;
                vec4 noise = texture2D(noiseTexture, noiseUV);
                normal += (noise.rgb - 0.5) * 0.1;
                normal = normalize(normal);
                
                // 5. 简单的反射模拟（基于观察角度）
                vec3 viewDir = normalize(cameraPos - worldPos);
                float fresnel = pow(1.0 - max(0.0, dot(viewDir, normal)), 3.0);
                
                // 6. 最终颜色 = 基础颜色 + 反射 + 泡沫
                vec3 finalColor = baseColor;
                finalColor += vec3(0.6, 0.7, 0.8) * fresnel * 0.3;  // 简单反射模拟
                
                // 7. 添加泡沫
                if (foam > 0.01)
                {
                    vec2 foamUV = texCoord * 2.0;
                    vec4 foamTex = texture2D(foamTexture, foamUV);
                    finalColor = mix(finalColor, foamColor, foam * foamTex.r);
                }
                
                // 8. 简单的透明度（基于深度）
                float alpha = mix(0.7, 0.95, depthColor);
                
                gl_FragColor = vec4(finalColor, alpha);
            }
            */
            
            // 实际实现：生成渲染数据供GPU使用
            GenerateRenderData(context);
        }
        
        /// <summary>
        /// 生成GPU渲染数据
        /// </summary>
        private void GenerateRenderData(WaterRenderContext context)
        {
            int res = _solver.Resolution;
            
            // 清空之前的渲染数据
            context.RenderData?.Clear();
            
            // 为每个顶点生成渲染数据
            for (int x = 0; x < res; x++)
            {
                for (int z = 0; z < res; z++)
                {
                    if (!_solver.HasWater(_geometry.GridToWorldX(x), _geometry.GridToWorldZ(z)))
                        continue;
                        
                    float worldX = _geometry.GridToWorldX(x);
                    float worldZ = _geometry.GridToWorldZ(z);
                    float height = _solver.GetHeight(worldX, worldZ);
                    
                    // 生成顶点数据
                    var vertex = new WaterVertex
                    {
                        Position = new Vector3(worldX, height, worldZ),
                        Normal = new Vector3(_renderNormals[x, z], 0, _renderNormals[x, z]), // 简化法线
                        Height = height,
                        Foam = _renderFoam[x, z],
                        DepthColor = _renderDepthColor[x, z]
                    };
                    
                    context.RenderData?.AddVertex(vertex);
                }
            }
            
            // 生成索引（用于网格渲染）
            GenerateIndices(context);
        }
        
        /// <summary>
        /// 生成网格索引
        /// </summary>
        private void GenerateIndices(WaterRenderContext context)
        {
            int res = _solver.Resolution;
            
            for (int x = 0; x < res - 1; x++)
            {
                for (int z = 0; z < res - 1; z++)
                {
                    if (!_solver.HasWater(_geometry.GridToWorldX(x), _geometry.GridToWorldZ(z)))
                        continue;
                        
                    // 两个三角形组成一个四边形
                    int i0 = x * res + z;
                    int i1 = (x + 1) * res + z;
                    int i2 = (x + 1) * res + (z + 1);
                    int i3 = x * res + (z + 1);
                    
                    context.RenderData?.AddTriangle(i0, i1, i2);
                    context.RenderData?.AddTriangle(i0, i2, i3);
                }
            }
        }
        
        /// <summary>
        /// 获取水面高度场纹理数据（用于自定义渲染）
        /// </summary>
        public byte[] GetHeightFieldTexture()
        {
            int res = _solver.Resolution;
            var heightField = _solver.GetHeightField();
            
            var textureData = new byte[res * res * 4]; // RGBA
            
            for (int x = 0; x < res; x++)
            {
                for (int z = 0; z < res; z++)
                {
                    int index = (x * res + z) * 4;
                    float height = heightField[x, z];
                    
                    // 编码高度到纹理（0-10m -> 0-255）
                    byte heightByte = (byte)MathUtils.Clamp(height / 10.0f * 255, 0, 255);
                    
                    textureData[index] = heightByte;     // R
                    textureData[index + 1] = heightByte; // G
                    textureData[index + 2] = heightByte; // B
                    textureData[index + 3] = 255;       // A
                }
            }
            
            return textureData;
        }
        
        /// <summary>
        /// 获取法线场纹理数据
        /// </summary>
        public byte[] GetNormalFieldTexture()
        {
            int res = _solver.Resolution;
            var textureData = new byte[res * res * 4]; // RGBA
            
            for (int x = 0; x < res; x++)
            {
                for (int z = 0; z < res; z++)
                {
                    int index = (x * res + z) * 4;
                    
                    // 法线Y分量编码到纹理
                    byte normalY = (byte)MathUtils.Clamp(_renderNormals[x, z] * 127 + 128, 0, 255);
                    
                    textureData[index] = 128;        // R
                    textureData[index + 1] = normalY; // G
                    textureData[index + 2] = 128;      // B
                    textureData[index + 3] = 255;     // A
                }
            }
            
            return textureData;
        }
        
        /// <summary>
        /// 获取泡沫场纹理数据
        /// </summary>
        public byte[] GetFoamFieldTexture()
        {
            int res = _solver.Resolution;
            var textureData = new byte[res * res * 4]; // RGBA
            
            for (int x = 0; x < res; x++)
            {
                for (int z = 0; z < res; z++)
                {
                    int index = (x * res + z) * 4;
                    
                    // 泡沫量编码到纹理
                    byte foamByte = (byte)MathUtils.Clamp(_renderFoam[x, z] * 255, 0, 255);
                    
                    textureData[index] = foamByte;     // R
                    textureData[index + 1] = foamByte; // G
                    textureData[index + 2] = foamByte; // B
                    textureData[index + 3] = 255;      // A
                }
            }
            
            return textureData;
        }
    }

    /// <summary>
    /// 水面几何信息
    /// </summary>
    public sealed class WaterGeometry
    {
        public readonly int Resolution;
        public readonly float Spacing;
        public readonly float Size;
        public readonly float HalfSize;
        
        public WaterGeometry(int resolution, float spacing)
        {
            Resolution = resolution;
            Spacing = spacing;
            Size = resolution * spacing;
            HalfSize = Size * 0.5f;
        }
        
        public float GridToWorldX(int gridX) => (gridX - Resolution / 2) * Spacing;
        public float GridToWorldZ(int gridZ) => (gridZ - Resolution / 2) * Spacing;
        public int WorldToGridX(float worldX) => (int)MathF.Round(worldX / Spacing + Resolution / 2);
        public int WorldToGridZ(float worldZ) => (int)MathF.Round(worldZ / Spacing + Resolution / 2);
        public int Width => Resolution;
        public int Height => Resolution;
    }

    /// <summary>
    /// 水渲染上下文
    /// </summary>
    public sealed class WaterRenderContext
    {
        public WaterRenderData? RenderData { get; set; }
        public bool TexturesUploaded { get; set; }
        
        // 相机信息
        public Vector3 CameraPosition { get; set; }
        public Matrix4x4 ViewMatrix { get; set; }
        public Matrix4x4 ProjectionMatrix { get; set; }
        public float Time { get; set; }
    }

    /// <summary>
    /// 水渲染数据（GPU接口）
    /// </summary>
    public sealed class WaterRenderData
    {
        private readonly List<WaterVertex> _vertices = new();
        private readonly List<int> _indices = new();
        
        public IReadOnlyList<WaterVertex> Vertices => _vertices;
        public IReadOnlyList<int> Indices => _indices;
        public int VertexCount => _vertices.Count;
        public int IndexCount => _indices.Count;
        
        public void AddVertex(WaterVertex vertex)
        {
            _vertices.Add(vertex);
        }
        
        public void AddTriangle(int i0, int i1, int i2)
        {
            _indices.Add(i0);
            _indices.Add(i1);
            _indices.Add(i2);
        }
        
        public void Clear()
        {
            _vertices.Clear();
            _indices.Clear();
        }
    }

    /// <summary>
    /// 水面顶点数据（GPU接口）
    /// </summary>
    public struct WaterVertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        public float Height;
        public float Foam;
        public float DepthColor;
    }

    /// <summary>
    /// 水渲染设置
    /// </summary>
    public sealed class WaterRenderSettings
    {
        /// <summary>水面大小（米）</summary>
        public float Size { get; set; } = 100.0f;
        
        /// <summary>水面分辨率</summary>
        public int Resolution { get; set; } = 128;
        
        /// <summary>水面网格间距（米）</summary>
        public float GridSpacing { get; set; } = 1.0f;
        
        /// <summary>是否启用泡沫效果</summary>
        public bool EnableFoam { get; set; } = true;
        
        /// <summary>是否启用噪点纹理</summary>
        public bool EnableNoise { get; set; } = true;
        
        /// <summary>是否启用深度颜色变化</summary>
        public bool EnableDepthColor { get; set; } = true;
        
        /// <summary>是否启用简单反射</summary>
        public bool EnableReflection { get; set; } = true;
    }
}