// ============================================================================
//  YamaEngine.Diagnostics.CrashReporterConfig.cs
//  yama引擎 -- 崩溃上报配置 (打包集成)
// ----------------------------------------------------------------------------
//  打包工具链闭环: 打包时生成 CrashReporterConfig.json, 运行时通过
//  CrashReporter.ConfigureFromFile 读取并自配置 (初始化 CrashHandler +
//  安装 AppDomain 未处理异常钩子 + 设置远端上报端点)。
//  使 "打包出可运行游戏 + 崩溃上报闭环" 可被测试验证。
// ============================================================================

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YamaEngine.Diagnostics
{
    /// <summary>
    /// 崩溃上报配置。由打包管线写入包内 <c>CrashReporterConfig.json</c>,
    /// 运行时由 <see cref="CrashReporter.ConfigureFromFile(string)"/> 读取并应用。
    /// </summary>
    public sealed class CrashReporterConfig
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>是否启用崩溃上报。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>崩溃报告输出目录 (相对包根或绝对路径)。</summary>
        public string ReportsDirectory { get; set; } = "Saved/Crashes";

        /// <summary>引擎版本 (写入崩溃报告)。</summary>
        public string EngineVersion { get; set; } = "1.0.0";

        /// <summary>远端上报端点 (可选; 为空则仅本地落盘)。</summary>
        public string? RemoteEndpoint { get; set; }

        /// <summary>项目名 (写入崩溃报告的自定义数据)。</summary>
        public string? ProjectName { get; set; }

        /// <summary>序列化为 JSON。</summary>
        public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

        /// <summary>从 JSON 反序列化 (无效/空则返回默认)。</summary>
        public static CrashReporterConfig FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new CrashReporterConfig();
            try { return JsonSerializer.Deserialize<CrashReporterConfig>(json, JsonOptions) ?? new CrashReporterConfig(); }
            catch (JsonException) { return new CrashReporterConfig(); }
        }

        /// <summary>
        /// 应用配置到静态 <see cref="CrashReporter"/>:
        /// 启用则初始化并安装处理器; 禁用则关闭。
        /// </summary>
        public void Apply()
        {
            if (!Enabled)
            {
                CrashReporter.Shutdown();
                return;
            }

            CrashReporter.Initialize(ReportsDirectory, EngineVersion);
            if (!string.IsNullOrEmpty(RemoteEndpoint))
                CrashReporter.SetRemoteEndpoint(RemoteEndpoint);
        }
    }
}
