# yama游戏引擎使用文档

## 快速开始

### 1. 创建新项目

```csharp
using YamaEngine.Projects;

// 从模板创建新项目
var project = ProjectSystem.CreateProject(
    projectPath: @"C:\MyGame",
    projectName: "MyAwesomeGame",
    templatePath: @"Templates\Basic3D"
);

// 保存项目配置
ProjectSystem.SaveProject(project);

// 重新加载项目
var loadedProject = ProjectSystem.LoadProject(@"C:\MyGame");
```

### 2. 初始化资产数据库

```csharp
using YamaEngine.Assets;

// 创建资产数据库
var assets = new AssetDatabase(projectPath);

// 导入资产
var meshAsset = assets.ImportAsset(@"Models\mycharacter.obj", AssetType.Mesh);
var textureAsset = assets.ImportAsset(@"Textures\diffuse.png", AssetType.Texture2D);

// 查询资产
var allMeshes = assets.GetAllAssets(AssetType.Mesh);
var assetByGuid = assets.GetAsset(meshAsset.Guid);

// 创建场景对象
var cube = assets.CreateObject("Player", "Box", new System.Numerics.Vector3(0, 1, 0));
var light = assets.CreateLight("Directional", new System.Numerics.Vector3(5, 10, 5));
var camera = assets.CreateCamera(new System.Numerics.Vector3(0, 2, 8), isMain: true);

// 保存场景
assets.SaveScene(@"Content\Scenes\Main.yama", "Main");
```

### 3. 使用 Prefab 系统

```csharp
using YamaEngine.Prefabs;

// 创建 Prefab 系统
var prefabSystem = new PrefabSystem(projectRoot, assets);

// 保存 Prefab
var sourceObj = assets.CreateObject("Door", "Box");
var prefab = prefabSystem.SavePrefab(sourceObj, "Assets/Prefabs/Door.shprefab");

// 实例化 Prefab
var instance = prefabSystem.InstantiatePrefab(prefab.Guid, new System.Numerics.Vector3(10, 0, 5));

// 使用追踪实例化（支持覆盖传播）
var trackedInstance = prefabSystem.InstantiatePrefabTracked(prefab.Guid, new System.Numerics.Vector3(20, 0, 5));

// 记录属性覆盖
prefabSystem.RecordOverride(trackedInstance.Id, "Position", new System.Numerics.Vector3(21, 0, 5));

// 撤销覆盖
prefabSystem.RevertOverride(trackedInstance.Id, "Position");

// 创建变体
var variant = prefabSystem.CreateVariant(prefab.Guid, "Assets/Prefabs/GreenDoor.shprefab", "GreenDoor");
```

### 4. 运行时加载

```csharp
using YamaEngine.Runtime;

// 加载导出的游戏包
var loadResult = RuntimeSceneLoader.LoadPackage(@"Builds\MyGame");

if (loadResult.Success)
{
    var world = loadResult.World;
    Console.WriteLine($"Loaded scene: {world.SceneName}");
    Console.WriteLine($"Objects: {world.Objects.Count}");

    // 创建游戏循环
    var loop = new RuntimeGameLoop(world, renderDevice, new RuntimeLoopOptions
    {
        UseFixedStep = true,
        FixedDeltaTime = 1f / 60f,
        MaxFixedStepsPerTick = 4
    });

    // 运行循环
    while (isRunning)
    {
        var deltaTime = GetDeltaTime();
        var result = loop.Tick(deltaTime);
        
        Console.WriteLine($"Frame {result.FrameIndex}: {result.DeltaTimeSeconds * 1000:F2}ms, " +
                        $"Fixed steps: {result.FixedStepCount}");
    }
}
```

### 5. 构建游戏

```csharp
using YamaEngine.Build;

// 配置构建
var config = new BuildConfig
{
    Target = BuildTarget.Windows,
    BuildType = BuildType.Release,
    Architecture = PlatformArchitecture.x64,
    ProjectRoot = projectPath,
    OutputDirectory = @"Builds\MyGame",
    IntermediateDirectory = @"Intermediate",
    MaxParallelCookJobs = Environment.ProcessorCount,
    IncrementalBuild = true
};

// 创建构建管道
var pipeline = new BuildPipelineReal();

// 使用进度报告
var progress = new ConsoleBuildProgress();

// 执行构建
var result = pipeline.Build(config, progress);

if (result.Status == BuildStatus.Success)
{
    Console.WriteLine($"构建成功！输出: {result.OutputPath}");
    Console.WriteLine($"耗时: {result.TotalTimeMs / 1000:F2}秒");
    Console.WriteLine($"资产数量: {result.CookedAssetCount}");
}
```

### 6. 引擎诊断

```csharp
using YamaEngine.Diagnostics;

// 运行诊断检查
var diagnostics = EngineDiagnostics.Run(new ProjectDiagnosticsOptions
{
    ProjectRoot = projectPath,
    Assets = assets,
    ExportDirectory = @"Builds\MyGame"
});

// 检查结果
Console.WriteLine($"诊断结果: {(diagnostics.Success ? "通过" : "失败")}");
Console.WriteLine($"错误: {diagnostics.ErrorCount}, 警告: {diagnostics.WarningCount}");

foreach (var issue in diagnostics.Issues)
{
    Console.WriteLine($"[{issue.Severity}] {issue.Code}: {issue.Message}");
    Console.WriteLine($"  路径: {issue.Path}");
}
```

### 7. 配置系统

```csharp
using YamaEngine.Config;

// 创建配置系统
var cvars = new ConsoleVariableSystem();

// 注册自定义变量
var gravityVar = cvars.Register("physics.gravity", "-9.81", 
    "重力加速度", CVarFlags.Physics | CVarFlags.SaveToConfig, -50f, 50f);

// 获取值
float gravity = cvars.Get("physics.gravity", -9.81f);
bool vsync = cvars.Get("r.vsync", true);

// 设置值
cvars.TrySet("r.vsync", "false");
gravityVar.SetValue(-15.0f);

// 执行命令
cvars.ExecuteCommand("set r.resolution_x 1920");
cvars.ExecuteCommand("toggle r.debug_draw");
cvars.ExecuteCommand("help");

// 保存/加载配置
cvars.SaveConfig("game.cfg");
cvars.LoadConfig("game.cfg");
```

## 核心概念

### 项目结构

```
MyGame/
├── yama.project.json      # 项目配置
├── Assets/                   # 原始资产文件
│   ├── Models/
│   ├── Textures/
│   └── Audio/
├── Content/                  # 引擎内容
│   ├── Scenes/
│   ├── Materials/
│   └── Scripts/
├── Builds/                   # 构建输出
├── Library/                  # 缓存和临时文件
└── ProjectSettings/          # 项目设置
```

### 资产类型

- **Mesh**: 3D 网格模型 (.obj, .fbx, .shmesh)
- **Texture**: 纹理图片 (.png, .jpg, .dds)
- **Material**: 材质定义 (.shmat)
- **Shader**: 着色器 (.shgraph)
- **Prefab**: 预制体 (.shprefab)
- **Scene**: 场景文件 (.yama)
- **Audio**: 音频文件 (.wav, .mp3, .ogg)
- **Animation**: 动画剪辑

### 构建目标

- **Windows**: Windows 桌面应用
- **Linux**: Linux 桌面应用
- **Mac**: macOS 桌面应用
- **Android**: Android APK
- **iOS**: iOS 应用

### 配置变量分类

- **engine.***: 引擎核心设置
- **r.***: 渲染相关
- **p.***: 物理相关
- **a.***: 音频相关
- **net.***: 网络相关
- **g.***: 游戏玩法相关
- **ui.***: 用户界面相关

## 高级功能

### 建模工具集成

```csharp
using YamaEngine.Modeling;

// 创建可编辑网格
var mesh = EditableMesh.CreateCube("MyCube", 1.0f);

// 编辑操作
MeshEditOperations.ExtrudeFace(mesh, faceIndex, 0.5f);
MeshEditOperations.InsetFace(mesh, faceIndex, 0.2f);
MeshEditOperations.BevelEdge(mesh, edgeIndex, 0.1f);

// 雕刻工具
SculptTool.Inflate(mesh, brushCenter, radius, strength, falloff);
SculptTool.Smooth(mesh, brushCenter, radius, iterations);

// UV 展开
UVUnwrapper.PlanarProjection(mesh, axis: Vector3.UnitY);
UVUnwrapper.SphericalProjection(mesh);

// 保存
mesh.Write("Assets/Models/MyCube.shmesh");
```

### 依赖图管理

```csharp
using YamaEngine.Assets;

// 构建依赖图
var dependencyGraph = AssetDependencyGraph.Build(new AssetDependencyGraphOptions
{
    ProjectRoot = projectPath,
    Assets = assets,
    SceneName = "Main"
});

// 查询依赖关系
var deps = dependencyGraph.GetDirectDependencies("scene:Main");
var transitive = dependencyGraph.GetTransitiveDependencies("Assets/Models/hero.fbx");

// 检查缺失引用
foreach (var missing in dependencyGraph.MissingReferences)
{
    Console.WriteLine($"缺失引用: {missing.SourceId} -> {missing.TargetId}");
}
```

### 渲染图

```csharp
using YamaEngine.Rendering;

// 创建渲染图
var renderGraph = new RenderGraph();

// 添加渲染通道
var depthPass = renderGraph.AddPass("DepthPrepass")
    .WriteTexture("SceneDepth", Format.R32_FLOAT);

var colorPass = renderGraph.AddPass("ForwardPass")
    .ReadTexture("SceneDepth")
    .WriteTexture("ColorBuffer", Format.R8G8B8A8_UNORM);

// 编译渲染图
var compiledGraph = renderGraph.Compile();

// 执行渲染
renderDevice.ExecuteRenderGraph(compiledGraph, deltaTime);
```

## 故障排除

### 常见问题

1. **资产导入失败**
   - 检查文件路径是否正确
   - 确认文件格式支持
   - 查看 AssetDatabase 错误日志

2. **构建失败**
   - 运行 EngineDiagnostics 检查项目完整性
   - 清理 Intermediate 目录重新构建
   - 检查磁盘空间

3. **运行时崩溃**
   - 检查资产文件完整性
   - 验证场景引用的资产存在
   - 查看运行时日志

### 调试技巧

```csharp
// 启用详细日志
cvars.ExecuteCommand("set engine.log_level verbose");

// 显示渲染统计
cvars.ExecuteCommand("toggle r.draw_calls_debug");

// 物理调试绘制
cvars.ExecuteCommand("toggle p.debug_draw");

// 查看性能统计
cvars.ExecuteCommand("show_stats");
```

## 性能优化

### 建议配置

```csharp
// 高性能配置
cvars.ExecuteCommand("set r.ssao false");
cvars.ExecuteCommand("set r.ssr false");
cvars.ExecuteCommand("set r.shadows low");
cvars.ExecuteCommand("set r.texture_quality medium");
cvars.ExecuteCommand("set r.particles_max 5000");

// 高质量配置
cvars.ExecuteCommand("set r.ssao true");
cvars.ExecuteCommand("set r.ssr true");
cvars.ExecuteCommand("set r.shadows ultra");
cvars.ExecuteCommand("set r.texture_quality high");
cvars.ExecuteCommand("set r.aa_mode taa");
```

### 构建优化

```csharp
// 启用增量构建
config.IncrementalBuild = true;

// 压缩算法
config.Compression = CompressionAlgorithm.LZ4;

// 代码优化
config.CodeOptimization = CodeOptimization.Full;

// 最大并行任务
config.MaxParallelCookJobs = Environment.ProcessorCount;
```

## 更多资源

- **测试示例**: `YamaEngine.Tests/Program.cs`
- **设计文档**: `docs/superpowers/specs/`
- **API 文档**: 查看各模块的 XML 注释
- **构建脚本**: `build.sh`

## 贡献

yama引擎采用模块化设计，欢迎贡献：

1. 遵循现有代码风格
2. 添加单元测试
3. 更新文档
4. 提交 Pull Request

---

**版本**: v1.0.0  
**最后更新**: 2026-07-13  
**许可证**: 详见 LICENSE 文件