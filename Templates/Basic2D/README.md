# Basic2D 模板

对齐 Godot 3.5 的专用 2D 工作流（矩阵 G3-036 ~ G3-044）。

- `yama.project.json`：`rendering.pipeline = "Cel2D"`，走 `YamaEngine/Cel2D/` 的 14 个文件
- `Content/Scenes/Main.yama`：Player / Ground(TileMap) / Camera2D 三个对象，
  字段结构与 `Scenes/SceneSerializer.cs` 的 `JsonSerializer` 一致（见 `schemas/yscene.schema.json`）
- `Content/Tilesets/Default.tileset.json`：新增 TileSet 图块数据库示例（G3-040），
  schema 见 `schemas/tileset.schema.json`

**验收**：S3 用这个模板再跑一遍；断言坐标单位为像素、YSort 遮挡正确、相机跟随生效。
**状态**：纯数据模板，不含 `.cs`，零编译风险；`Editor/LevelEditor/GridSnappingSystem.cs` 的
图块刷绘接线仍需真机核验。
