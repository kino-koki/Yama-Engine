using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using YamaEngine.Reality;
namespace YamaEngine.Editor.CodeOss
{
    /// <summary>
    /// 自研 Yama ↔ Code - OSS 统一门面：干净 MIT 配置 + 扩展清单 + LSP/DAP 客户端
    /// + 命令路由。不执行外部进程（命令只返回可执行的结构化意图，由宿主调用）。
    /// </summary>
    public sealed class YamaCodeOssBridge
    {
        public YamaCodeOssProduct Product { get; }
        public string ManifestJson { get; }
        public YamaLspClient Lsp { get; } = new YamaLspClient();
        public YamaDapClient Dap { get; } = new YamaDapClient();
        public List<string> Diagnostics { get; } = new List<string>();

        public YamaCodeOssBridge(string productJson, string version)
        {
            Product = YamaCodeOssProduct.FromJson(productJson);
            ManifestJson = YamaExtensionManifest.Build(version);
            if (!Product.IsCleanMitBuild)
                Diagnostics.Add("product.json 不是干净 MIT 构建");
            if (!YamaExtensionManifest.IsWellFormed(ManifestJson))
                Diagnostics.Add("扩展清单格式非法");
        }

        public bool IsCleanMitBuild
            => Product.IsCleanMitBuild && Diagnostics.Count == 0;

        /// <summary>命令路由：返回 (handled, intent, payload)。逐位确定。</summary>
        public (bool Handled, string Intent, string Payload) Dispatch(string command)
        {
            switch (command)
            {
                case "yama.build":
                    return (true, "dotnet build",
                        "YamaEngine/YamaEngine.csproj -c Debug -p:Platform=x64 -warnaserror");
                case "yama.test":
                    return (true, "dotnet test",
                        "YamaEngine.Tests/YamaEngine.Tests.csproj -c Debug -p:Platform=x64");
                case "yama.smoke":
                    return (true, "dotnet run",
                        "--project YamaCLI/YamaCLI.csproj -c Debug -- smoke 60");
                case "yama.snapshot":
                    return (true, "snapshot", "YrPhysicsPipeline.Snapshot");
                case "yama.rollback":
                    return (true, "rollback", "YrPhysicsPipeline.Restore/RewindTo");
                case "yama.sotaStatus":
                    return (true, "audit", "docs/SOTA_GAP_ANALYSIS.md G0-G8");
                case "yama.coupledMass":
                    return (true, "audit",
                        "YrPhysicsPipeline.UseCoupledMassMatrix + LastCoupledTrace");
                // 第二十轮：最轻量构建面 + Tuan 真实被调用能力进入引擎侧命令路由。
                case "yama.codeossMinimal":
                    return (true, "verify",
                        "scripts/build_codeoss_minimal.py --check -> clean_mit_build + config_sha256 (read-only)");
                case "yama.invoke":
                    return (true, "delegate",
                        "integrations/tuan_cli/yama_bridge.py::invoke(action, params) read-only fail-closed");
                case "yama.capabilities":
                    return (true, "capabilities",
                        "integrations/tuan_cli/contract.json -> 13 tools / 13 subcommands / 7 actions");
                default:
                    return (false, string.Empty, string.Empty);
            }
        }

        /// <summary>构造 initialize 请求并返回帧字节数（供宿主接线前自检）。</summary>
        public int InitializeFrame(string rootUri)
            => YamaJsonRpcFraming.Encode(Lsp.Initialize(rootUri)).Length;
    }
}
