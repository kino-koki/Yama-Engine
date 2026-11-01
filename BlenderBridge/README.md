# Yama Engine Blender 5.1 Bridge Addon

## 功能特性

### 1. Live Link 实时同步
- WebSocket 风格的 TCP 通信
- 实时同步场景更新
- 选中对象实时传输
- 变换数据实时更新

### 2. 资产导出
- **原生格式**: `.yama` (JSON 格式的 Yama 专有格式)
- **行业标准**: FBX、glTF 2.0 (GLB)
- **OBJ**: Wavefront OBJ 格式

### 3. 场景数据导出
- 完整网格数据 (顶点、法线、UV、颜色)
- PBR 材质 (Metallic/Roughness/Emission)
- 骨骼动画
- 光照和相机
- 父子层级关系

### 4. 辅助工具
- 基础图元快速创建
- PBR 材质快速创建
- LOD 自动生成
- 变换应用
- 命名规范工具

## 安装方法

### 方法 1: 用户插件目录 (推荐)
```
1. 关闭 Blender
2. 复制 `yama_bridge.py` 到:
   %APPDATA%\Blender Foundation\Blender\5.1\scripts\addons\

   或

   C:\Users\<用户名>\AppData\Roaming\Blender Foundation\Blender\5.1\scripts\addons\

3. 启动 Blender 5.1
4. 编辑 → 偏好设置 → 插件
5. 搜索 "Yama Engine"
6. 启用插件
7. 侧边栏出现 "Yama" 选项卡
```

### 方法 2: 临时测试
```
1. 打开 Blender
2. 编辑 → 偏好设置 → 插件
3. 点击 "安装" 按钮
4. 选择 `yama_bridge.py` 文件
5. 启用插件
```

### 方法 3: 脚本目录 (开发用)
```
1. 复制到 Blender 脚本目录:
   %APPDATA%\Blender Foundation\Blender\5.1\scripts\startup\

2. Blender 启动时自动加载
```

## 使用方法

### Live Link 连接

1. **在 Yama Editor 中**:
   - 打开编辑器
   - DCC Bridge 工具栏会有 "启动 Live Link" 按钮

2. **在 Blender 中**:
   - 找到右侧边栏 (N键) → "Yama" 选项卡
   - 点击 "Connect" 按钮
   - 状态显示 "Connected"

3. **开始同步**:
   - "Sync Scene" - 发送完整场景
   - "Sync Selection" - 发送选中对象
   - "Sync Transform" - 发送变换

### 文件导出

1. **Yama 格式 (.yama)**:
   - 在 Blender 中选择 "Export" → "Export Scene"
   - 保存为 `.yama` JSON 文件

2. **FBX 格式**:
   - 选择 "Export FBX"
   - 导出到项目目录

3. **glTF 格式**:
   - 选择 "Export glTF"
   - 支持 GLB 二进制格式

### 快速创建

1. **基础图元**:
   - 侧边栏 → "Create" 面板
   - 点击按钮创建 Cube/Sphere/Cylinder 等

2. **PBR 材质**:
   - 选中对象后
   - "Create PBR Material" → 选择类型

### Blender 热键

```
G     - 移动
R     - 旋转
S     - 缩放
Tab   - 切换模式
N     - 侧边栏
```

## 面板说明

### 主面板 (Yama Engine)

```
┌─────────────────────────────┐
│  Connected: localhost:18888 │  ← 连接状态
│                      [X]   │  ← 断开按钮
├─────────────────────────────┤
│  Sync                       │
│  [Sync Scene      ]         │  ← 同步整个场景
│  [Sync Selection ]         │  ← 同步选中
│  [Sync Transform ]          │  ← 同步变换
└─────────────────────────────┘
```

### 导出面板

```
┌─────────────────────────────┐
│  Export                     │
│  Native Formats             │
│  [Export .yama ]         │
├─────────────────────────────┤
│  Industry Formats           │
│  [Export FBX      ]        │
│  [Export glTF     ]        │
└─────────────────────────────┘
```

### 创建面板

```
┌─────────────────────────────┐
│  Create                     │
│  Primitives                │
│  [Cube] [Sphere]           │
│  [Cylinder] [Cone]         │
│  [Torus] [Plane]           │
├─────────────────────────────┤
│  Materials                 │
│  [Create PBR Material >]   │
└─────────────────────────────┘
```

### 工具面板

```
┌─────────────────────────────┐
│  Tools                     │
│  Utilities                 │
│  [Apply Transforms]        │
│  [Rename (SM_)]           │
│  [Generate LODs]          │
├─────────────────────────────┤
│  Statistics                │
│  Meshes: 12               │
│  Materials: 5             │
│  Triangles: 48,000        │
└─────────────────────────────┘
```

## 导出数据格式

### .yama 格式 (JSON)

```json
{
  "version": "1.0.0",
  "source": "Blender 5.1",
  "name": "MyScene",
  "frame": {
    "start": 1,
    "end": 250,
    "fps": 24
  },
  "meshes": [
    {
      "name": "Cube",
      "vertices": [[0,0,0], [1,0,0], ...],
      "normals": [[0,0,1], ...],
      "uvs": [[0,0], [1,0], ...],
      "indices": [0,1,2, 0,2,3, ...],
      "materials": [
        {"name": "MetalMat", "metallic": 1.0, "roughness": 0.2}
      ]
    }
  ],
  "animations": [...],
  "stats": {
    "meshCount": 1,
    "totalTriangles": 12
  }
}
```

## 故障排除

### 连接失败

1. 检查 Yama Editor 是否正在运行
2. 检查端口 18888 是否被占用
3. 检查防火墙设置
4. 查看 Blender 控制台错误信息

### 导出失败

1. 确保有选中对象
2. 检查是否有足够的磁盘空间
3. 查看 Blender 系统控制台 (窗口 → 切换系统控制台)

### Live Link 不同步

1. 断开连接后重新连接
2. 重启 Blender 和编辑器
3. 检查网络连接

## 技术细节

### 通信协议

```
端口: 18888 (TCP)
协议: 自定义二进制协议
帧格式: [4字节长度][JSON数据]
```

### Blender API 兼容性

- Blender 5.1.0+
- Python 3.11+
- API: `bpy`, `mathutils`

## 更新日志

### v1.0.0 (2024)
- 初始版本
- Live Link 支持
- FBX/glTF 导出
- PBR 材质导出
- 骨骼动画支持
- LOD 生成

## 许可证

MIT License - Yama Engine Team
