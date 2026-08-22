# 山雨游戏引擎入门教程

## 教程 1: 创建你的第一个游戏

### 步骤 1: 创建项目

创建一个控制台应用程序，添加对 ShanyuEngine 的引用：

```csharp
using ShanyuEngine.Projects;
using ShanyuEngine.Assets;
using ShanyuEngine.Runtime;
using ShanyuEngine.Rendering;

var projectPath = @"C:\MyFirstGame";

// 从基础模板创建项目
var project = ProjectSystem.CreateProject(
    projectPath: projectPath,
    projectName: "MyFirstGame",
    templatePath: @"Templates\Basic3D"
);

Console.WriteLine($"项目创建成功: {project.Name}");
```

### 步骤 2: 添加游戏对象

```csharp
var assets = new AssetDatabase(projectPath);

// 创建玩家角色
var player = assets.CreateObject("Player", "Box", new System.Numerics.Vector3(0, 1, 0));
player.BaseColor = new System.Numerics.Vector3(1, 0.3f, 0.3f); // 红色

// 创建地面
var ground = assets.CreateObject("Ground", "Plane", System.Numerics.Vector3.Zero);
ground.Scale = new System.Numerics.Vector3(10, 1, 10);
ground.BaseColor = new System.Numerics.Vector3(0.5f, 0.5f, 0.5f);

// 创建灯光
var sunLight = assets.CreateLight("Sun", new System.Numerics.Vector3(5, 10, 5));
var lightComponent = sunLight.GetComponent<LightComponent>();
lightComponent.LightType = "Directional";
lightComponent.Intensity = 1.2f;

// 创建相机
var camera = assets.CreateCamera(new System.Numerics.Vector3(0, 3, 10), true);
```

### 步骤 3: 保存场景

```csharp
assets.SaveScene(@"Content\Scenes\Level1.shanyu", "Level1");
Console.WriteLine("场景保存成功！");
```

### 步骤 4: 构建游戏

```csharp
using ShanyuEngine.Build;

var config = new BuildConfig
{
    Target = BuildTarget.Windows,
    BuildType = BuildType.Release,
    ProjectRoot = projectPath,
    OutputDirectory = Path.Combine(projectPath, "Builds", "MyFirstGame")
};

var pipeline = new BuildPipelineReal();
var progress = new ConsoleBuildProgress();
var result = pipeline.Build(config, progress);

if (result.Status == BuildStatus.Success)
{
    Console.WriteLine($"游戏构建成功！输出位置: {result.OutputPath}");
}
```

### 步骤 5: 运行游戏

```csharp
var loadResult = RuntimeSceneLoader.LoadPackage(result.OutputPath);

if (loadResult.Success)
{
    // 创建简单渲染设备
    var renderDevice = new NullRenderDevice();
    
    // 创建游戏循环
    var loop = new RuntimeGameLoop(loadResult.World, renderDevice, new RuntimeLoopOptions
    {
        UseFixedStep = true,
        FixedDeltaTime = 1f / 60f
    });

    // 运行 100 帧
    for (int frame = 0; frame < 100; frame++)
    {
        var tickResult = loop.Tick(1f / 60f);
        if (frame % 10 == 0)
        {
            Console.WriteLine($"Frame {tickResult.FrameIndex}: " +
                            $"Objects={tickResult.FrameStats.ObjectCount}, " +
                            $"DrawCalls={tickResult.FrameStats.DrawCallCount}");
        }
    }
}
```

## 教程 2: 使用 Prefab 创建可复用对象

### 创建 Door Prefab

```csharp
using ShanyuEngine.Prefabs;

// 创建 Prefab 系统
var prefabSystem = new PrefabSystem(projectPath, assets);

// 创建门对象
var doorObj = assets.CreateObject("Door", "Box", System.Numerics.Vector3.Zero);
doorObj.Scale = new System.Numerics.Vector3(1, 2.5f, 0.1f);

// 保存为 Prefab
var doorPrefab = prefabSystem.SavePrefab(doorObj, "Assets/Prefabs/Door.shprefab");
Console.WriteLine($"Door Prefab 保存成功，GUID: {doorPrefab.Guid}");
```

### 实例化多个门

```csharp
// 在不同位置创建门
var door1 = prefabSystem.InstantiatePrefab(doorPrefab.Guid, 
    new System.Numerics.Vector3(3, 0, 0));
var door2 = prefabSystem.InstantiatePrefab(doorPrefab.Guid, 
    new System.Numerics.Vector3(-3, 0, 0));
var door3 = prefabSystem.InstantiatePrefab(doorPrefab.Guid, 
    new System.Numerics.Vector3(0, 0, 5));

// 添加到场景
assets.AddObject(door1);
assets.AddObject(door2);
assets.AddObject(door3);
```

### 创建 Prefab 变体

```csharp
// 创建绿色门变体
var greenDoor = prefabSystem.CreateVariant(
    parentPrefabGuid: doorPrefab.Guid,
    variantAssetPath: "Assets/Prefabs/GreenDoor.shprefab",
    variantName: "GreenDoor"
);

// 添加变体覆盖
prefabSystem.AddVariantOverride(greenDoor.Guid, "BaseColor", 
    new System.Numerics.Vector3(0.2f, 0.8f, 0.2f));
```

## 教程 3: 使用配置系统

### 注册游戏设置

```csharp
using ShanyuEngine.Config;

var cvars = new ConsoleVariableSystem();

// 游戏设置
cvars.Register("game.player_speed", "5.0", "玩家移动速度", 
    CVarFlags.Gameplay | CVarFlags.SaveToConfig, 1f, 20f);
cvars.Register("game.jump_force", "8.0", "跳跃力度", 
    CVarFlags.Gameplay | CVarFlags.SaveToConfig, 1f, 20f);
cvars.Register("game.gravity", "-9.81", "重力", 
    CVarFlags.Gameplay | CVarFlags.Physics | CVarFlags.SaveToConfig, -50f, 0f);

// 保存配置
cvars.SaveConfig(Path.Combine(projectPath, "game.cfg"));
```

### 在游戏循环中使用配置

```csharp
// 创建游戏循环时使用配置
var loop = new RuntimeGameLoop(loadResult.World, renderDevice, new RuntimeLoopOptions
{
    UseFixedStep = true,
    FixedDeltaTime = cvars.Get("engine.fixed_dt", 1f / 60f),
    MaxFixedStepsPerTick = cvars.Get("engine.max_substeps", 4)
});

// 在更新中使用配置
float playerSpeed = cvars.Get("game.player_speed", 5.0f);
float gravity = cvars.Get("game.gravity", -9.81f);
```

## 教程 4: 基础建模

### 创建自定义网格

```csharp
using ShanyuEngine.Modeling;

// 创建金字塔形状
var pyramid = new EditableMesh { Name = "Pyramid" };

// 添加顶点
pyramid.Vertices.AddRange(new[]
{
    new System.Numerics.Vector3(0, 1, 0),    // 顶点
    new System.Numerics.Vector3(-0.5f, 0, -0.5f), // 基底
    new System.Numerics.Vector3(0.5f, 0, -0.5f),
    new System.Numerics.Vector3(0.5f, 0, 0.5f),
    new System.Numerics.Vector3(-0.5f, 0, 0.5f)
});

// 添加面
pyramid.Faces.AddRange(new[]
{
    new MeshFace(0, 1, 2), // 正面
    new MeshFace(0, 2, 3), // 右面
    new MeshFace(0, 3, 4), // 背面
    new MeshFace(0, 4, 1), // 左面
    new MeshFace(1, 4, 3), // 底面1
    new MeshFace(1, 3, 2)  // 底面2
});

// 计算法线
pyramid.RecalculateNormals();

// 保存网格
pyramid.Write(Path.Combine(projectPath, "Assets", "Models", "pyramid.shmesh"));
```

### 编辑现有网格

```csharp
// 加载立方体
var cube = EditableMesh.CreateCube("EditableCube", 1.0f);

// 挤出一个面
MeshEditOperations.ExtrudeFace(cube, 0, 0.3f);

// 内缩另一个面
MeshEditOperations.InsetFace(cube, 1, 0.2f);

// 倒角边
MeshEditOperations.BevelEdge(cube, 5, 0.05f);

// 环切
MeshEditOperations.LoopCut(cube, 3, 0.5f);

// 保存编辑后的网格
cube.Write(Path.Combine(projectPath, "Assets", "Models", "edited_cube.shmesh"));
```

## 教程 5: 简单材质

### 创建 PBR 材质

```csharp
using ShanyuEngine.Materials;

// 创建材质
var material = new PbrMaterial
{
    Name = "MetalPlate",
    AlbedoColor = new System.Numerics.Vector4(0.7f, 0.7f, 0.8f, 1.0f),
    Metallic = 0.9f,
    Roughness = 0.3f,
    NormalScale = 1.0f,
    EmissionColor = System.Numerics.Vector4.Zero
};

// 保存材质
var materialPath = Path.Combine(projectPath, "Content", "Materials", "MetalPlate.shmat");
Directory.CreateDirectory(Path.GetDirectoryName(materialPath)!);
File.WriteAllText(materialPath, JsonSerializer.Serialize(material, new JsonSerializerOptions
{
    WriteIndented = true
}));

// 在对象上使用材质
var metalObj = assets.CreateObject("MetalPlate", "Box");
metalObj.MaterialAssetPath = "Content/Materials/MetalPlate.shmat";
metalObj.Metallic = 0.9f;
metalObj.Roughness = 0.3f;
```

## 教程 6: 运行项目诊断

### 检查项目健康度

```csharp
using ShanyuEngine.Diagnostics;

// 运行完整诊断
var diagnostics = EngineDiagnostics.Run(new ProjectDiagnosticsOptions
{
    ProjectRoot = projectPath,
    Assets = assets,
    ExportDirectory = Path.Combine(projectPath, "Builds", "MyFirstGame")
});

// 输出诊断结果
Console.WriteLine($"项目健康度: {(diagnostics.Success ? "良好" : "有问题")}");
Console.WriteLine($"错误数量: {diagnostics.ErrorCount}");
Console.WriteLine($"警告数量: {diagnostics.WarningCount}");

// 显示详细问题
foreach (var issue in diagnostics.Issues.Where(i => i.Severity == ProjectDiagnosticSeverity.Error))
{
    Console.WriteLine($"错误: {issue.Code} - {issue.Message}");
    Console.WriteLine($"  位置: {issue.Path}");
}
```

## 教程 7: 构建和发布

### 创建发布版本

```csharp
// 发布配置
var releaseConfig = new BuildConfig
{
    Target = BuildTarget.Windows,
    BuildType = BuildType.Shipping,  // 发布版本
    Architecture = PlatformArchitecture.x64,
    ProjectRoot = projectPath,
    OutputDirectory = Path.Combine(projectPath, "Releases", "v1.0"),
    Compression = CompressionAlgorithm.LZMA,
    CodeOptimization = CodeOptimization.Full,
    StripEngineCode = true
};

// 构建发布版本
var releaseResult = pipeline.Build(releaseConfig, progress);

if (releaseResult.Status == BuildStatus.Success)
{
    Console.WriteLine($"发布版本构建成功！");
    Console.WriteLine($"包大小: {releaseResult.PackageSizeBytes / (1024 * 1024):F2} MB");
    Console.WriteLine($"构建时间: {releaseResult.TotalTimeMs / 1000:F2} 秒");
    
    // 生成构建报告
    var reportPath = Path.Combine(releaseConfig.OutputDirectory, "build-report.json");
    File.WriteAllText(reportPath, JsonSerializer.Serialize(releaseResult, new JsonSerializerOptions
    {
        WriteIndented = true
    }));
}
```

## 下一步

恭喜你完成了基础教程！现在你可以：

1. **深入学习**: 查阅 `USER_GUIDE.md` 了解更多高级功能
2. **查看示例**: 研究 `ShanyuEngine.Tests/` 中的示例代码
3. **阅读设计文档**: 查看 `docs/superpowers/specs/` 了解架构设计
4. **创建游戏**: 开始开发你自己的游戏！

## 常见问题

### Q: 如何调试游戏？

A: 使用配置系统启用调试输出：
```csharp
cvars.ExecuteCommand("set engine.log_level debug");
cvars.ExecuteCommand("toggle p.debug_draw");
cvars.ExecuteCommand("toggle r.draw_calls_debug");
```

### Q: 如何优化性能？

A: 调整配置变量：
```csharp
cvars.ExecuteCommand("set r.shadows medium");
cvars.ExecuteCommand("set r.particles_max 5000");
cvars.ExecuteCommand("set r.post_processing false");
```

### Q: 如何添加自定义资产类型？

A: 扩展 `AssetType` 枚举并在 `AssetDatabase` 中添加导入逻辑。

### Q: 如何支持多平台？

A: 在构建配置中设置不同的 `BuildTarget`，引擎会自动处理平台特定设置。

---

**祝你在山雨引擎中创造出精彩的游戏！**