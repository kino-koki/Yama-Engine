// =============================================================================
//  Yama Engine - FlowVFXRenderBridge.cs
//  yama引擎 - FlowVFX → 渲染桥接器
//
//  将 FlowVFXParticleSimulationReal 的 SoA 粒子数据转换为 GPU 可用的格式，
//  并驱动 ParticleRenderer 完成每帧上传与绘制。
// =============================================================================
using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.Particles;
using YamaEngine.Rendering;
using YamaEngine.RHI;
using ParticleRenderer = YamaEngine.Rendering.ParticleRenderer;

namespace YamaEngine.VFX
{
    /// <summary>
    /// FlowVFX → 渲染桥接器。将 FlowVFXParticleSimulationReal 的粒子数据转换为 GPU 可用的格式。
    /// </summary>
    public class FlowVFXRenderBridge
    {
        private readonly YamaEngine.Rendering.ParticleRenderer _particleRenderer;
        private readonly List<ParticleData> _particleDataCache = new();

        public FlowVFXRenderBridge(YamaEngine.Rendering.ParticleRenderer particleRenderer)
        {
            _particleRenderer = particleRenderer;
        }

        /// <summary>
        /// 每帧调用：从 FlowVFX 模拟读取粒子数据，上传到 GPU 并渲染。
        /// </summary>
        public void UpdateAndRender(RHICommandList cmd, FlowVFXParticleSimulationReal simulation, in CameraData camera)
        {
            _particleDataCache.Clear();

            var data = simulation.Data;
            if (data == null || data.ActiveCount == 0)
                return;

            // 提取相机右/上向量用于公告板
            Matrix4x4 view = camera.ViewMatrix;
            Vector3 cameraRight = new Vector3(view.M11, view.M21, view.M31);
            Vector3 cameraUp = new Vector3(view.M12, view.M22, view.M32);
            float rightLen = cameraRight.Length();
            float upLen = cameraUp.Length();
            if (rightLen > 1e-6f) cameraRight /= rightLen;
            if (upLen > 1e-6f) cameraUp /= upLen;

            // 遍历活跃粒子
            for (int i = 0; i < data.ActiveCount; i++)
            {
                int idx = data.ActiveIndices[i];
                if (!data.Alive[idx]) continue;

                float lifeRatio = data.MaxLife[idx] > 0f
                    ? 1f - (data.Life[idx] / data.MaxLife[idx])
                    : 0f;

                Vector2 size2 = data.Size[idx];
                float size = MathF.Max(size2.X, size2.Y);

                _particleDataCache.Add(new ParticleData
                {
                    Position = data.Position[idx],
                    Velocity = data.Velocity[idx],
                    Size = size,
                    Color = data.Color[idx],
                    Life = lifeRatio,
                    MaxLife = data.MaxLife[idx],
                    Rotation = data.Rotation[idx]
                });
            }

            // 上传到 GPU 并渲染
            if (_particleDataCache.Count > 0)
            {
                _particleRenderer.UploadParticles(_particleDataCache, cameraRight, cameraUp);
                _particleRenderer.RenderParticles(cmd, camera, Matrix4x4.Identity);
            }
        }
    }
}
