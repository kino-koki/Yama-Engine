# Yama 已有模块完善设计

## 目标

本阶段只完善仓库中已经存在且属于当前主干的模块，不新增空泛的大系统。目标是把 Yama 从“有编辑器和引擎结构”推进到“能稳定完成基础开发闭环”的 MVP：创建项目、导入资产、保存场景、创建 Prefab、导出可运行包，并用烟测验证。

完成后，用户应能打开 `YamaEditor.sln`，构建解决方案，运行 `YamaEngine.Tests`，并确信当前主干不是堆叠目录，而是一条连贯工作流。

## 范围

本阶段包含：

- 完善 `ProjectSystem`：项目创建、模板复制、默认目录、manifest 校验。
- 完善 `AssetDatabase`：资产导入、`.shmeta`、GUID 索引、场景对象管理。
- 完善 `PrefabSystem`：Prefab 保存、meta 写入、GUID 实例化、基础属性 roundtrip。
- 抽出场景序列化：让 `.yama` 保存/加载格式集中维护，不继续散在 `AssetDatabase` 中。
- 抽出 `BuildPipeline`：把编辑器中的导出逻辑迁到 `YamaEngine`。
- 完善 `YamaRuntime` smoke：能读取导出包 manifest、加载场景、跑短循环。
- 完善 `YamaEngine.Tests`：覆盖项目、资产、场景、Prefab、导出包验证。
- 清理编辑器文案：去掉夸张宣传式文本，改为专业、可验证的描述。

本阶段不包含：

- 完整 3D 渲染管线。
- D3D12/Vulkan/Metal 真实视口。
- AI、布料、流体、世界分块、蓝图、导航、网络同步等大系统扩展。
- 新增第三方大型源码。
- 新增与当前主干无关的语言实现。

## 推荐路线

采用“已有模块加固”路线。每个改动必须服务于一个可测试流程，避免再次出现目录很多但互不调用的情况。

优先级如下：

1. 构建稳定：`dotnet build YamaEditor.sln -c Debug -p:Platform=x64` 必须通过。
2. 数据稳定：项目、资产、场景、Prefab 都使用明确的 JSON 格式和 GUID。
3. 导出稳定：`BuildPipeline` 生成结构固定的 demo 包。
4. 测试稳定：`YamaEngine.Tests` 覆盖关键闭环。
5. 编辑器调用核心库：UI 只负责选择文件夹和展示结果，不承载核心导出逻辑。

## 模块设计

### `ProjectSystem`

位置：`YamaEngine/Projects/ProjectSystem.cs`

保留现有 `YamaProject` 和 `ProjectSystem`，补齐输入校验和 manifest 校验。

职责：

- 创建项目目录。
- 从 `Templates/Basic3D` 复制基础内容。
- 读取和写入 `yama.project.json`。
- 确保 `Assets`、`Content/Scenes`、`Content/Materials`、`Builds`、`Library`、`ProjectSettings` 存在。
- 返回明确错误，避免静默失败。

验收：

- 测试能创建项目。
- manifest 中项目名和启动场景能 roundtrip。
- 模板场景和默认材质存在。

### `AssetDatabase`

位置：`YamaEngine/Assets/AssetDatabase.cs`

保留现有 API，但将场景保存/加载细节迁出到 `SceneSerializer`。`AssetDatabase` 保留为资产索引和场景对象容器。

职责：

- 导入资产到 `Assets/`。
- 为资产写入 `.shmeta`。
- 注册资产 GUID、路径、类型、名称。
- 通过 GUID 查询资产。
- 管理当前场景对象列表。
- 暴露 `GetAllAssets()`、`AddObject()`、`CreateObject()` 等编辑器需要的接口。

改进点：

- 扩展类型识别：`.obj/.fbx/.gltf/.glb` 为 Mesh，常见图片为 Texture2D，音频为 Audio，`.shprefab` 为 Prefab，`.yama` 为 Scene。
- 统一路径为 `/` 分隔。
- 导入重复文件时保留唯一文件名，但 meta 中记录原始来源。
- `.shmeta` 读取失败时提供可诊断错误。

验收：

- 导入 OBJ 生成稳定 meta。
- 能通过 meta GUID 查回资产。
- 资产列表计数正确。

### `SceneSerializer`

位置：`YamaEngine/Scenes/SceneSerializer.cs`

新增集中式场景序列化模块，避免 `AssetDatabase` 同时承担资产库和场景格式两个职责。

职责：

- 保存 `.yama`。
- 加载 `.yama`。
- 序列化对象的名称、可见性、层、Transform、Mesh/Material 引用、基础材质参数、Prefab 源 GUID。
- 支持格式版本字段。

场景文件最小结构：

```json
{
  "formatVersion": 1,
  "name": "Main",
  "objects": []
}
```

错误处理：

- JSON 格式错误：抛出包含文件路径的异常。
- 字段缺失：使用安全默认值。
- 资产引用丢失：保留字符串引用，不阻止场景加载。

验收：

- 保存场景后重新加载，对象数量、名称、Transform、PrefabSourceGuid 一致。

### `PrefabSystem`

位置：`YamaEngine/Prefabs/PrefabSystem.cs`

保留现有 `PrefabAsset`、`PrefabObject`、`PrefabSystem`，补齐与 `AssetDatabase` 的关系。

职责：

- 从 `SceneObject` 保存 `*.shprefab`。
- 写入 `*.shprefab.shmeta`。
- 注册 Prefab 资产。
- 通过 GUID 实例化 Prefab。
- 保留 `PrefabSourceGuid`。

本阶段只支持基础属性 roundtrip，不做复杂 override 系统。

验收：

- 对象保存成 Prefab。
- Prefab meta 存在。
- 通过 GUID 实例化后名称、Transform、材质参数、mesh 引用一致。

### `BuildPipeline`

位置：`YamaEngine/Build/BuildPipeline.cs`

新增核心导出模块，替代编辑器中内联的 `ExportDemoDialog` 文件复制逻辑。

职责：

- 接收项目根路径、输出目录、当前场景路径、资产库。
- 保存或复制当前场景到导出包。
- 复制已导入资产。
- 写入 `yama.demo.json`。
- 尝试复制 `YamaRuntime` 构建产物。
- 生成 `run_yama_demo.bat`。
- 返回 `BuildResult`，包含导出目录、是否复制 runtime、对象数、资产数、警告列表。

导出包结构：

```text
YamaPlayableDemo/
  yama.demo.json
  run_yama_demo.bat
  README.txt
  Content/
    MainScene.yama
    Assets/
  Runtime/
```

错误处理：

- Runtime 未构建：仍导出场景和资产，但 `BuildResult` 标记 warning。
- 场景路径不存在：返回失败结果，不生成半成品包。
- 资产缺失：跳过该资产并记录 warning。

验收：

- 测试能生成 manifest。
- manifest 中场景路径、对象数、资产数正确。
- 没有 runtime 时仍能得到可诊断 warning。

### `YamaRuntime`

位置：`YamaRuntime/Program.cs`

保留现有 manifest 加载与 `--smoke` 参数。完善错误码和输出，使其适合作为导出包验证工具。

职责：

- 读取 `yama.demo.json`。
- 加载 manifest 指向的 `.yama`。
- 初始化 `EngineSystems`。
- 输出项目名、场景、对象数、资产数。
- `--smoke` 模式跑 5 帧后退出。

验收：

- smoke 模式成功返回 0。
- manifest 缺失、场景缺失、JSON 错误均返回非 0。

### `YamaEditor`

位置：`YamaEditor/MainWindow.xaml.cs`

编辑器只做 UI 编排，不承载核心业务逻辑。

改动：

- `ExportDemoDialog()` 调用 `BuildPipeline.Build()`。
- 创建项目、打开项目、导入资产、创建 Prefab 后刷新 UI。
- 文案从“UE5.7-level industrial engine”等不可验证描述改成“Yama Engine MVP”。
- 日志使用清晰、可诊断文本。

验收：

- 编辑器构建通过。
- 菜单项仍能调用对应功能。
- 导出结果在日志中显示输出目录和 warnings。

### `YamaEngine.Tests`

位置：`YamaEngine.Tests/Program.cs`

扩展现有烟测，不引入复杂测试框架，保持命令行可跑。

测试顺序：

1. 创建 Basic3D 项目。
2. 导入 OBJ。
3. 保存场景并重新加载。
4. 创建 Prefab 并实例化。
5. 使用 `BuildPipeline` 导出 demo 包。
6. 校验 `yama.demo.json`、场景、资产、脚本文件存在。

验收命令：

```powershell
dotnet restore YamaEditor.sln
dotnet build YamaEditor.sln -c Debug -p:Platform=x64
dotnet run --project YamaEngine.Tests/YamaEngine.Tests.csproj
```

## 数据流

### 新建项目

1. 编辑器选择父目录和项目名。
2. `ProjectSystem.CreateProject()` 复制模板。
3. 写入 manifest。
4. `App.UseProject()` 创建 `EngineSystems`。
5. 尝试加载启动场景。

### 导入资产

1. 编辑器选择源文件。
2. `AssetDatabase.ImportAsset()` 复制到 `Assets/`。
3. 创建 `.shmeta`。
4. 注册 GUID 索引。
5. 编辑器刷新资产面板。

### 保存场景

1. 编辑器调用 `AssetDatabase.SaveScene()`。
2. `AssetDatabase` 将对象列表交给 `SceneSerializer`。
3. `SceneSerializer` 写入 `.yama`。

### 创建 Prefab

1. 编辑器获取选中对象。
2. `PrefabSystem.SavePrefab()` 写入 `Assets/Prefabs/*.shprefab`。
3. 写入 `.shmeta`。
4. `AssetDatabase` 注册 Prefab。

### 导出 Demo

1. 编辑器选择输出目录。
2. `BuildPipeline.Build()` 生成 `YamaPlayableDemo`。
3. 复制场景和资产。
4. 写入 manifest 和启动脚本。
5. 编辑器显示成功或 warnings。

## 质量边界

本阶段的“完善”不以目录数量衡量，而以闭环能否稳定运行为准。任何新增代码必须满足：

- 被主干调用。
- 有测试覆盖或能被 smoke 验证。
- 不重复已有模块职责。
- 不引入无法构建的依赖。
- 不使用夸张宣传式命名。

## 实施顺序

1. 新增 `SceneSerializer` 和对应测试。
2. 新增 `BuildPipeline` 和对应测试。
3. 修改 `AssetDatabase` 调用 `SceneSerializer`。
4. 修改 `YamaEditor` 调用 `BuildPipeline`。
5. 清理编辑器文案。
6. 跑 `dotnet build` 和测试。
7. 更新 `README.md` 的功能边界与验证命令。

## 验收标准

- 解决方案构建通过。
- 测试命令输出 `All YamaEngine tests passed.`。
- 导出目录包含 `yama.demo.json`、`Content/MainScene.yama`、`run_yama_demo.bat`。
- 如果 runtime 已构建，导出包包含 `Runtime/YamaRuntime.exe`。
- `README.md` 与实际功能一致，不再承诺未实现的大系统。
