# Shanyu Unity6 Foundation Design

## 目标

把当前 Shanyu 从“可启动、可保存场景、可导出 runtime 包”的原型，推进到 Unity6 类工作流的第一层地基。第一批不追求一次性实现 Unity6 的全部渲染、动画、导航、包管理和协作能力，而是先建立这些能力都依赖的工程系统：项目、资产、GUID、Prefab、组件、脚本入口和构建管线接口。

本阶段完成后，用户应能像使用 Unity 一样完成基础流程：新建项目、导入资源、生成稳定 `.shmeta`、创建场景对象、保存 Prefab、实例化 Prefab、保存场景、导出可运行 demo 包。

## 范围

本阶段包含：

- 项目系统：读取和创建 `shanyu.project.json`，从 `Templates/Basic3D` 复制项目。
- AssetDatabase 2.0：为资产生成 `.shmeta`，保存稳定 GUID、类型、导入设置和依赖。
- GUID 引用：场景和 Prefab 引用资产 GUID，不只依赖路径。
- Prefab 系统：保存 `*.shprefab`，实例化 Prefab，保留源 Prefab GUID。
- 组件模型入口：为对象保存组件列表，定义脚本组件的序列化格式和生命周期接口。
- Build Pipeline 接口：把当前 `Export Demo` 整理成可扩展 `BuildPipeline`，后续可支持 Windows、开发构建、发布构建。
- 验证：新增烟测覆盖项目创建、资产 meta、Prefab roundtrip、场景加载和 runtime smoke。

本阶段不包含：

- 完整 Unity6 渲染管线。
- Terrain、Animator、Timeline、VFX Graph、NavMesh、Package Manager。
- 真正 C# 热重载或 Roslyn 编译沙箱。
- 完整窗口化 3D 渲染 runtime。现有 `ShanyuRuntime` 继续作为可运行加载器。

## 推荐方案

采用“稳定地基优先”的路线。先做数据模型、序列化格式和编辑器入口，再扩展渲染和复杂系统。

理由：

- Unity6 的核心工作流依赖 Asset GUID、Prefab 和项目结构。
- 没有稳定 GUID，后续材质、脚本、Prefab、场景引用都会返工。
- 没有 Prefab，编辑器体验很难接近 Unity。
- 没有 BuildPipeline 抽象，导出 runtime 会继续堆在 `MainWindow.xaml.cs` 里。

## 架构

新增或重构以下模块：

### `ProjectSystem`

位置：`ShanyuEngine/Projects/ProjectSystem.cs`

职责：

- 创建项目目录。
- 读取和写入 `shanyu.project.json`。
- 维护项目根目录、启动场景、模板名、构建目录。
- 从 `Templates/Basic3D` 复制新项目。

主要类型：

- `ShanyuProject`
- `ProjectSystem`
- `ProjectTemplate`

### `AssetDatabase`

位置：继续使用 `ShanyuEngine/Assets/AssetDatabase.cs`，必要时拆分为多个文件。

职责：

- 导入资源。
- 创建 `.shmeta`。
- 通过 GUID 查询资源。
- 维护 path 到 GUID 的索引。
- 提供依赖查询。

新增类型：

- `AssetGuid`
- `AssetMeta`
- `AssetImportSettings`
- `AssetReference`

`.shmeta` 示例：

```json
{
  "guid": "b1e1f3b2f7e447c8af8844c85ff761d4",
  "assetPath": "Assets/Models/Cube.obj",
  "type": "Mesh",
  "importedAtUtc": "2026-06-20T00:00:00Z",
  "sourceHash": "sha256...",
  "importSettings": {
    "scale": 1.0,
    "generateColliders": false,
    "readWrite": true
  },
  "dependencies": []
}
```

### `PrefabSystem`

位置：`ShanyuEngine/Prefabs/PrefabSystem.cs`

职责：

- 从 `SceneObject` 保存 Prefab。
- 从 `*.shprefab` 实例化 SceneObject。
- 保存 Prefab 源 GUID。
- 支持最小 override：Transform、Material、Active 状态。

Prefab 示例：

```json
{
  "version": 1,
  "guid": "prefab-guid",
  "name": "Enemy",
  "root": {
    "name": "Enemy",
    "mesh": { "guid": "mesh-guid" },
    "material": { "guid": "material-guid" },
    "transform": {
      "position": { "x": 0, "y": 0, "z": 0 },
      "rotation": { "x": 0, "y": 0, "z": 0, "w": 1 },
      "scale": { "x": 1, "y": 1, "z": 1 }
    },
    "components": []
  }
}
```

### `ComponentModel`

位置：`ShanyuEngine/Components`

职责：

- 统一内置组件和脚本组件的序列化。
- 定义生命周期接口。
- 为后续脚本系统保留字段序列化格式。

接口：

```csharp
public interface IShanyuComponent
{
    string TypeName { get; }
    void Awake(SceneObject owner) {}
    void Start(SceneObject owner) {}
    void Update(SceneObject owner, float deltaTime) {}
}
```

本阶段只做数据结构和内置组件迁移，不做完整脚本编译。

### `BuildPipeline`

位置：`ShanyuEngine/Build/BuildPipeline.cs`

职责：

- 接收项目、场景和目标平台。
- 复制 runtime。
- 复制依赖资产。
- 写入 manifest。
- 生成启动脚本。

现有 `MainWindow.ExportDemoDialog()` 应改为调用 `BuildPipeline.Build()`, UI 只负责选择目录和展示结果。

## 数据流

### 导入资源

1. 用户导入文件。
2. `AssetDatabase.ImportAsset()` 复制文件到 `Assets`。
3. 系统生成 GUID。
4. 写入 `.shmeta`。
5. 更新内存索引。
6. Project 面板刷新。

### 保存场景

1. 场景对象序列化。
2. Mesh、Material、Script 引用写成 GUID。
3. Prefab instance 写入源 Prefab GUID 和 override。
4. 输出 `.shanyu`。

### 实例化 Prefab

1. 用户选择 `*.shprefab`。
2. `PrefabSystem.LoadPrefab()` 读取 Prefab。
3. 通过 `AssetDatabase` 解析依赖 GUID。
4. 创建 `SceneObject`。
5. 标记 `PrefabSourceGuid`。

### 构建

1. 用户执行 `Export Demo`。
2. `BuildPipeline` 加载当前项目和启动场景。
3. 收集场景依赖资产。
4. 复制 runtime、场景、资产、manifest。
5. 生成 `run_shanyu_demo.bat`。
6. 可用 `ShanyuRuntime --smoke` 验证。

## 错误处理

- `.shmeta` 缺失：自动重建，但生成新 GUID 前必须检查是否有旧路径索引。
- GUID 找不到：场景加载保留 Missing Reference，并在 Console 输出错误。
- Prefab 源丢失：实例仍保留对象数据，但 Inspector 显示 `Missing Prefab Source`。
- 构建 runtime 缺失：导出仍生成场景包，并明确提示先构建 `ShanyuRuntime`。
- JSON 格式错误：加载失败并输出文件路径、字段名和异常摘要。

## 测试

新增临时或正式 smoke 测试：

- 创建 Basic3D 项目。
- 导入 OBJ，生成 `.shmeta`。
- 移动资源路径后通过 GUID 找回资产。
- 从对象创建 Prefab。
- 从 Prefab 实例化对象。
- 保存场景并重新加载。
- 执行 BuildPipeline 导出。
- 用 `ShanyuRuntime --smoke` 加载导出包。

构建验收：

- `dotnet build ShanyuEditor/ShanyuEditor.csproj --no-restore --nologo`
- `dotnet build ShanyuRuntime/ShanyuRuntime.csproj -c Release --no-restore --nologo`
- 两者保持 `0 错误`。阶段目标是尽量保持 `0 警告`，若底层旧模块有遗留警告，必须记录来源，不影响主线验收。

## 实施顺序

1. 新增 `ProjectSystem`，支持创建和打开 Basic3D 项目。
2. 升级 `AssetDatabase`，写入和读取 `.shmeta`。
3. 将 SceneObject 的 Mesh/Material 引用升级为 `AssetReference`，保留路径兼容层。
4. 新增 `PrefabSystem` 和 `*.shprefab`。
5. 将 `Export Demo` 迁移到 `BuildPipeline`。
6. 补 smoke 测试。
7. 编辑器接入：File New Project、Open Project、Create Prefab、Instantiate Prefab。

## 验收标准

本阶段完成时，用户能完成以下流程：

1. 在编辑器中新建 Basic3D 项目。
2. 导入一个 OBJ 文件，看到对应 `.shmeta`。
3. 将导入资产放入场景。
4. 把场景对象保存为 Prefab。
5. 删除对象后从 Prefab 恢复。
6. 保存场景，关闭后重新打开。
7. 导出 demo 包。
8. 双击启动脚本或运行 `ShanyuRuntime --smoke`，runtime 成功加载场景。

## 自检

- 无未定义的 TBD/TODO。
- 范围只覆盖 Unity6 地基功能，没有把所有 Unity6 大系统塞进同一阶段。
- 数据格式和模块边界明确。
- 每个新增系统都有职责、输入输出和验收方式。
