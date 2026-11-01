// ==================================================================================================
//  BuildConfiguration.cs — yama引擎构建配置
// ==================================================================================================
//  命名空间: YamaEngine.Build
//
//  功能:
//    定义游戏打包构建的所有配置参数：目标平台、场景列表、资产路径、输出目录等。
// ==================================================================================================

using System;
using System.Collections.Generic;

namespace YamaEngine.Build
{
    /// <summary>目标构建平台。</summary>

    /// <summary>构建配置。</summary>
    public sealed class BuildConfiguration
    {
        /// <summary>项目根目录。</summary>
        public string ProjectPath { get; set; } = string.Empty;

        /// <summary>构建输出目录。</summary>
        public string OutputPath { get; set; } = string.Empty;

        /// <summary>构建名称（用于生成目录名）。</summary>
        public string BuildName { get; set; } = "GameBuild";

        /// <summary>目标平台。</summary>
        public BuildTarget Target { get; set; } = BuildTarget.Windows64;

        /// <summary>起始场景索引（在 Scenes 列表中的位置）。</summary>
        public int StartSceneIndex { get; set; } = 0;

        /// <summary>要包含的场景文件路径列表。</summary>
        public List<string> Scenes { get; set; } = new();

        /// <summary>要包含的资产目录列表。</summary>
        public List<string> AssetFolders { get; set; } = new();

        /// <summary>是否包含脚本 DLL。</summary>
        public bool IncludeScripts { get; set; } = true;

        /// <summary>是否包含源码（用于调试）。</summary>
        public bool IncludeSource { get; set; } = false;

        /// <summary>是否为开发构建（包含调试符号）。</summary>
        public bool DevelopmentBuild { get; set; } = false;

        /// <summary>压缩资产包。</summary>
        public bool CompressAssets { get; set; } = true;

        /// <summary>生成安装包 (.zip)。</summary>
        public bool CreateInstaller { get; set; } = false;

        /// <summary>版本号。</summary>
        public string Version { get; set; } = "1.0.0";

        /// <summary>公司名称。</summary>
        public string CompanyName { get; set; } = "Yama Games";

        /// <summary>产品名称。</summary>
        public string ProductName { get; set; } = "Game";

        /// <summary>图标路径。</summary>
        public string? IconPath { get; set; }

        /// <summary>窗口启动尺寸。</summary>
        public (int Width, int Height) WindowSize { get; set; } = (1280, 720);

        /// <summary>全屏启动。</summary>
        public bool Fullscreen { get; set; } = false;

        /// <summary>垂直同步。</summary>
        public bool VSync { get; set; } = true;

        /// <summary>构建日期。</summary>
        public DateTime BuildDate { get; set; } = DateTime.Now;

        /// <summary>构建号（自动生成）。</summary>
        public int BuildNumber { get; set; } = 1;

        public static BuildConfiguration Default => new();
    }
}
