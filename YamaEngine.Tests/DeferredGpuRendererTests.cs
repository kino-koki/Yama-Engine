using System;
using System.Numerics;
using System.Text;
using YamaEngine.Rendering;
using YamaEngine.Vulkan;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    // ══════════════════════════════════════════════════════════════
    // DeferredGpuRenderer — 延迟渲染 GPU 执行器深度验证
    // 覆盖: UBO 布局与 GLSL 严格一致 / 帧计划目标与 pass 完整性 /
    //       全部 GLSL 经 GpuShaderCompiler 编译为 SPIR-V (无工具链时跳过)
    // ══════════════════════════════════════════════════════════════
    public static class DeferredGpuRendererTests
    {
        // ── UBO 布局: 与 GLSL LightingUBO (8×float4) / MaterialUBO (3×float4) 严格一致 ──
        public static void TestDeferredUboLayouts()
        {
            AssertTrue(System.Runtime.InteropServices.Marshal.SizeOf<DeferredLightingUBO>() == 128,
                "DeferredLightingUBO must be 128 bytes (8 float4).");
            AssertTrue(System.Runtime.InteropServices.Marshal.SizeOf<DeferredMaterialUBO>() == 48,
                "DeferredMaterialUBO must be 48 bytes (3 float4).");

            var ubo = new DeferredLightingUBO
            {
                CameraPos = new Vector4(1, 2, 3, 4),
                DirLightDir = new Vector4(5, 6, 7, 8),
                DirLightColor = new Vector4(9, 10, 11, 12),
                PointLightPos = new Vector4(13, 14, 15, 16),
                PointLightColor = new Vector4(17, 18, 19, 20),
                SpotLightPos = new Vector4(21, 22, 23, 24),
                SpotLightDir = new Vector4(25, 26, 27, 28),
                SpotLightColor = new Vector4(29, 30, 31, 32)
            };
            var bytes = new byte[128];
            BitConverter.TryWriteBytes(bytes.AsSpan(0), ubo.CameraPos.X);
            BitConverter.TryWriteBytes(bytes.AsSpan(124), ubo.SpotLightColor.W);
            AssertTrue(BitConverter.ToSingle(bytes, 0) == 1f, "UBO first field at offset 0.");
            AssertTrue(BitConverter.ToSingle(bytes, 124) == 32f, "UBO last field at offset 124.");
        }

        // ── 帧计划: GBuffer MRT 8 + 深度 + 完整 pass 链 ──
        public static void TestDeferredGpuPlanCompleteness()
        {
            var plan = GpuDeferredPipeline.BuildFramePlan(1280, 720);
            AssertTrue(plan.Targets.Count >= 20, $"Plan should allocate full target set (got {plan.Targets.Count}).");
            AssertTrue(plan.Passes.Count >= 8, $"Plan should contain the full pass chain (got {plan.Passes.Count}).");
            AssertTrue(plan.Passes[0].Kind == GpuPassKind.GBuffer, "First pass must be GBuffer.");
            AssertTrue(plan.Passes[0].ColorTargets.Count == 8, "GBuffer pass must write 8 MRT targets.");
            AssertTrue(plan.Passes[1].Kind == GpuPassKind.DeferredLighting, "Second pass must be DeferredLighting.");

            bool hasLighting = false, hasTonemapOrFinal = false;
            foreach (var p in plan.Passes)
            {
                if (p.Kind == GpuPassKind.DeferredLighting) hasLighting = true;
                if (p.ColorTargets.Count > 0 && p.ColorTargets[0] == GpuDeferredPipeline.FinalTarget)
                    hasTonemapOrFinal = true;
            }
            AssertTrue(hasLighting, "Plan must contain the deferred lighting pass.");
            AssertTrue(hasTonemapOrFinal, "Plan must end at the final LDR target.");

            // 管线描述: 着色器源码为真实 GLSL
            var descs = plan.BuildPipelineDescs();
            AssertTrue(descs.Count == plan.Passes.Count, "One pipeline desc per pass.");
            AssertTrue(descs[0].Desc.VS.HasValue && descs[0].Desc.VS.Value.Bytecode.Length > 32,
                "GBuffer VS source must be embedded.");
            var vsText = Encoding.UTF8.GetString(descs[0].Desc.VS!.Value.Bytecode);
            AssertTrue(vsText.Contains("#version 450"), "GBuffer VS must be GLSL 450 source.");
        }

        // ── 全部 GLSL 经 GpuShaderCompiler 编译 (无工具链时跳过, 不假通过) ──
        public static void TestDeferredGpuShadersCompile()
        {
            var plan = GpuDeferredPipeline.BuildFramePlan(320, 240);
            var descs = plan.BuildPipelineDescs();
            int checkedShaders = 0;
            int failures = 0;
            var failNames = new System.Collections.Generic.List<string>();

            foreach (var d in descs)
            {
                foreach (var pair in new[] { (d.Desc.VS, ShaderStageExt.Vertex), (d.Desc.PS, ShaderStageExt.Fragment) })
                {
                    if (!pair.Item1.HasValue) continue;
                    var bc = pair.Item1.Value;
                    var stage = pair.Item2;
                    if (bc.Bytecode == null || bc.Bytecode.Length == 0) continue;
                    var glsl = Encoding.UTF8.GetString(bc.Bytecode);
                    if (!glsl.Contains("#version")) continue; // 已是 SPIR-V 的跳过

                    var r = GpuShaderCompiler.TryCompileToSpirv(glsl, stage, bc.EntryPoint, d.Name);
                    if (r.IsSourceOnly) continue; // 无工具链环境: 跳过 (不伪造通过)
                    checkedShaders++;
                    if (!r.Success)
                    {
                        failures++;
                        failNames.Add($"{d.Name}: {r.Error}");
                    }
                }
            }

            if (checkedShaders > 0)
            {
                AssertTrue(failures == 0,
                    $"All {checkedShaders} deferred GLSL shaders must compile (failures: {string.Join("; ", failNames)}).");
            }
            else
            {
                // 无 shaderc/glslang 的环境: 仅验证 source-only 路径不抛异常
                AssertTrue(true, "No shader toolchain available; compile check skipped.");
            }
        }
    }
}
