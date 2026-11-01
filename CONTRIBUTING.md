# 贡献指南

感谢你对yama游戏引擎的兴趣！本文档介绍如何参与项目贡献。

## 快速开始

```bash
# 1. Fork & Clone
git clone https://github.com/<your-username>/YamaEngine.git
cd YamaEngine

# 2. 还原依赖
dotnet restore YamaEditor.sln

# 3. 构建引擎核心
dotnet build YamaEngine\YamaEngine.csproj -c Debug -p:Platform=x64

# 4. 运行测试
dotnet test YamaEngine.Tests\YamaEngine.Tests.csproj
```

## 开发环境要求

| 工具 | 版本 |
|------|------|
| .NET SDK | 8.0+ |
| Visual Studio | 2022 17.8+ |
| Windows App SDK | 1.8+ |
| Vulkan 驱动 | 1.3+ (推荐 1.4) |

## 代码规范

### C# 代码风格

- 使用 `var` 声明局部变量（类型明显时）
- 使用 4 空格缩进，不使用 Tab
- 文件头注释使用 `// ══...═` 分隔区域
- 公共 API 必须有 `/// <summary>` XML 文档注释
- 遵循已有命名约定：
  - 类/方法/属性: `PascalCase`
  - 私有字段: `_camelCase`
  - 常量: `PascalCase`
  - 命名空间: `YamaEngine.{Module}`

### 提交规范

使用 [Conventional Commits](https://www.conventionalcommits.org/) 格式：

```
<type>(<scope>): <description>

[optional body]

[optional footer]
```

类型 (`type`):
- `feat`: 新功能
- `fix`: Bug 修复
- `refactor`: 重构（不改变行为）
- `perf`: 性能优化
- `docs`: 文档变更
- `test`: 测试相关
- `chore`: 构建/工具变更

示例:
```
feat(physics): 添加布料-刚体双向碰撞
fix(vulkan): 修复描述符池泄漏
docs(readme): 更新构建步骤
```

## Pull Request 流程

1. **创建分支**: 从 `main` 创建特性分支 `feat/your-feature` 或 `fix/issue-123`
2. **编写代码**: 遵循代码规范，添加必要的注释
3. **编写测试**: 新功能需附带单元测试
4. **本地验证**: 确保 `dotnet build` 和 `dotnet test` 通过
5. **提交 PR**: 描述变更内容、动机和测试方式

### PR 标题格式

```
<type>(<scope>): <description>
```

### PR 检查清单

- [ ] 代码可编译 (`dotnet build` 成功)
- [ ] 测试通过 (`dotnet test` 成功)
- [ ] 新功能附带测试
- [ ] 公共 API 有 XML 文档注释
- [ ] 提交信息遵循 Conventional Commits
- [ ] 不引入新的第三方依赖（除非必要且已讨论）

## 项目结构

```
YamaEngine/          引擎核心库 (C# .NET 8)
YamaEditor/          WinUI 3 编辑器外壳
YamaEngine.Tests/    单元测试
YamaEngineBridge/    C++ native 桥接 (Assimp)
BlenderBridge/         Blender 插件
MayaBridge/            Maya 插件
MaxBridge/             3ds Max 插件
```

## Issue 报告

报告 Bug 时请包含：
- 操作系统和 GPU 型号
- .NET SDK 版本 (`dotnet --version`)
- 复现步骤
- 预期行为 vs 实际行为
- 错误日志（如有）

## 许可证

贡献的代码将在 [MIT License](./LICENSE) 下发布。提交 PR 即表示同意此许可。
