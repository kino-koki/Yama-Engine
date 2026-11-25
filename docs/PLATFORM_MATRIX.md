# 平台与导出矩阵（对照 Godot 3.5 官方支持列表）

> Godot 3.5 官方：编辑器与导出支持 Windows 7+ / macOS 10.12+ / Linux / HTML5(WebAssembly)；
> 运行导出项目支持 Android 4.4+ / iOS 10+ / 主机 / 无头 Linux+macOS 服务器。
> Yama 远端 `Platform/` 只有 7 个文件、5 个适配层。下表逐平台给真实状态。

| 平台 | Godot 3.5 | Yama 对应 | 渲染后端 | 状态 | 下一步 |
|---|---|---|---|---|---|
| Windows 7+ x64 | ✅ | `Win32Window.cs`(535) / `YamaEditor`(WinUI3) | Vulkan 1.4 | **唯一主目标** | 真机 S3–S12 |
| Windows 32-bit | ✅ | `Platforms=x64`（csproj 固定） | — | ❌ | 若要 32 位需改 csproj |
| macOS 10.12+ (x86/ARM) | ✅ | 仅 `SDL2Window.cs` 通用层 | 无 Metal / 无 GL | ❌ 缺失 | `macos-latest` runner + 适配层 |
| Linux (x86/ARM) | ✅ | `LinuxAdapter.cs`(831) + SDL2 | 无 GL | ⚠️ 部分 | `ubuntu-latest` 冒烟 |
| HTML5 / WebAssembly | ✅ | 无 | **无 GLES2 后端** | ❌ 阻断 | 先补 GLES2 或砍宣称 |
| Android 4.4+ | ✅ | `AndroidAdapter.cs`(788) | Vulkan(部分设备) | ⚠️ 未出包 | 出包 + 真机安装 |
| iOS 10+ | ✅ | 无适配层 | — | ❌ 缺失 | Xcode 工程 + MoltenVK |
| 主机 PS5/Xbox/Switch | 第三方发行商 | `Platforms/ConsoleSDKAbstraction.cs`(994，74 处 TODO) | — | 占位·NDA | 条件编译隔离，默认不编入 |
| 无头服务器 | ✅ | `YamaCLI smoke 60` | 无 | ✅ 已实现 | 纳入 S1 |
| HarmonyOS | Godot 无 | `HarmonyOSAdapter.cs`(622) | — | 扩展项 | 不计入 Godot 对照 |

## 渲染后端的关键阻断

Godot 3.5 用 **OpenGL ES 2.0（移动 / Web 推荐）+ OpenGL 2.1（桌面推荐）** 双渲染器按平台分级。
Yama csproj 注释明确「D3D12 后端已废弃并整体删除，**唯一图形后端是 Vulkan**」。
这意味着：

- **Web / 低端移动**在补齐 GLES2 之前无法支持（Vulkan 在浏览器不可用，低端 Android 未必有 Vulkan 1.3）。
- 不要再写「跨平台全支持」的宣称，除非先补后端或明确降级目标平台。

## 导出模板清单（本轮补的骨架）

`Templates/Export/<platform>/template.json` 六份，定义「运行时二进制 + 资源根 + 清单 + 启动参数」的契约。
**它们是规格，不是可运行包**；产出真实包需要 `YamaCLI build --platform <p>` + 真机编译。

| 平台 | 模板 | 状态 |
|---|---|---|
| windows | `Templates/Export/windows/template.json` | 规格已补，待实现 |
| linux | `Templates/Export/linux/template.json` | 规格已补，待实现 |
| macos | `Templates/Export/macos/template.json` | 规格已补，缺适配层 |
| web | `Templates/Export/web/template.json` | 规格已补，**缺 GLES2 后端** |
| android | `Templates/Export/android/template.json` | 规格已补，待出包 |
| ios | `Templates/Export/ios/template.json` | 规格已补，缺适配层 |

## 主机 SDK 的处理（NDA 边界）

`ConsoleSDKAbstraction.cs` 里 `PS5SDKStub` / `XboxSDKStub` / `SwitchSDKStub` 都是 TODO，
第 925/932/938 行直接 `throw new PlatformNotSupportedException("... Build with PS5 symbol.")`。
Godot 3 也是靠第三方发行商解决主机。建议：

1. 用 `#if YAMA_WITH_CONSOLE_SDK` 包住这些类型，默认构建**根本不编译**；
2. 文档写「主机需自行取得授权」，删掉任何「支持 PS5」的误导表述；
3. 不要让 stub 出现在 Release 产物里（`capability_audit.py` 加了 `console_stub` 检查）。

## 验收

- S12：`YamaCLI build --platform <p>` 每平台出包，干净机器解压即运行
- 三平台 CI：`windows-2022` / `ubuntu-latest` / `macos-latest` 各跑一次 build

> 测试工程当前 TFM 是 `net10.0-windows10.0.19041.0`，**绑死 Windows**，这是 Linux/macOS CI 的前置阻断；
> 建议降到 `net10.0` 并对少数 Win32 用例加 `[SupportedOSPlatform("windows")]`。
