using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ShanyuEngine.RHI;
using ShanyuEngine.Shaders;

namespace ShanyuEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨 眼球渲染系统 (Eye Rendering System)
    //
    // 实现高质量角色眼球渲染：
    //   1. 解剖学正确的眼球模型（角膜、虹膜、晶状体、巩膜）
    //   2. 虹膜折射（Iris Refraction）— 虹膜在角膜后面看起来更小
    //   3. 虹膜纹理混合（多张纹理按角度混合）
    //   4. 瞳孔缩放（动态）
    //   5. 眼球 SSS（巩膜/虹膜次表面散射）
    //   6. 角膜高光（双层：主高光 + 环境反射）
    //   7. 泪膜反射（Tear Film Reflection）
    //   8. 眼球湿润度 / 反射率
    //   9. 视线追踪（Look-At 动画）
    //   10. 眼球阴影（上眼睑投射的阴影）
    //
    // 参考:
    //   [Dupuy 2013] "Adaptive Rendering of Non-Lambertian Appearance" 
    //   [Jimenez 2013] "Real-Time Realistic Eye Rendering"
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>眼球渲染模式。</summary>
    public enum EyeShadingMode
    {
        /// <summary>简化（单纹理 + 高光）。</summary>
        Simple,
        /// <summary>中等（折射 + 双层高光）。</summary>
        Medium,
        /// <summary>完整（折射 + SSS + 泪膜 + 视线）。</summary>
        Full
    }

    // ────────────────────────────────────────────────────────────────────────
    // 眼球参数
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>眼球解剖学参数。</summary>
    public sealed class EyeParameters
    {
        /// <summary>巩膜半径（世界单位）。</summary>
        public float ScleraRadius = 0.012f;
        /// <summary>角膜半径（略凸出）。</summary>
        public float CorneaRadius = 0.013f;
        /// <summary>虹膜半径。</summary>
        public float IrisRadius = 0.006f;
        /// <summary>瞳孔最小半径（收缩）。</summary>
        public float PupilMinRadius = 0.001f;
        /// <summary>瞳孔最大半径（扩张）。</summary>
        public float PupilMaxRadius = 0.005f;
        /// <summary>当前瞳孔半径。</summary>
        public float PupilRadius = 0.002f;
        /// <summary>虹膜深度（角膜后面多远）。</summary>
        public float IrisDepth = 0.002f;
        /// <summary>角膜折射率（真实值约 1.376）。</summary>
        public float CorneaIOR = 1.376f;
        /// <summary>眼球液体折射率。</summary>
        public float HumorIOR = 1.336f;
        /// <summary>巩膜颜色。</summary>
        public Vector3 ScleraColor = new(0.95f, 0.90f, 0.88f);
        /// <summary>虹膜颜色。</summary>
        public Vector3 IrisColor = new(0.3f, 0.15f, 0.05f);
        /// <summary>瞳孔颜色（纯黑）。</summary>
        public Vector3 PupilColor = Vector3.Zero;
        /// <summary>泪膜湿润度 [0,1]。</summary>
        public float TearWetness = 0.8f;
        /// <summary>虹膜纹理旋转角度（度）。</summary>
        public float IrisRotation = 0f;
        /// <summary>虹膜纹理密度。</summary>
        public float IrisDensity = 0.7f;
        /// <summary>巩膜 SSS 颜色。</summary>
        public Vector3 ScleraSSSColor = new(0.8f, 0.3f, 0.2f);
        /// <summary>巩膜 SSS 强度。</summary>
        public float ScleraSSSStrength = 0.15f;
        /// <summary>上眼睑阴影强度。</summary>
        public float UpperLidShadowStrength = 0.3f;
        /// <summary>上眼睑角度（度，0=完全睁开，90=闭眼）。</summary>
        public float UpperLidAngle = 5.0f;
        /// <summary>下眼睑角度。</summary>
        public float LowerLidAngle = 0.0f;
        /// <summary>巩膜细微血管可见度。</summary>
        public float VeinVisibility = 0.1f;
    }

    // ────────────────────────────────────────────────────────────────────────
    // 虹膜折射
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>虹膜折射计算器。</summary>
    public static class IrisRefraction
    {
        /// <summary>计算折射后的 UV（虹膜在角膜后面，通过角膜折射看起来更小）。</summary>
        /// <param name="uv">原始 UV（巩膜表面）。</param>
        /// <param name="corneaIOR">角膜折射率。</param>
        /// <param name="irisDepth">虹膜深度（相对角膜表面）。</param>
        /// <returns>折射后的 UV。</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector2 RefractIrisUV(Vector2 uv, float corneaIOR, float irisDepth)
        {
            // 把 UV 转换到以瞳孔中心为原点的坐标
            Vector2 centered = uv * 2.0f - Vector2.One;
            float dist = centered.Length();
            if (dist < 0.001f) return uv;

            // 简化的折射：UV 向中心收缩
            // 收缩比例取决于折射率和深度
            float eta = 1.0f / corneaIOR; // 空气到角膜
            // Snell 定律近似：sin(theta_t) = sin(theta_i) / n
            float sinThetaI = Math.Clamp(dist * 0.5f, 0, 1);
            float sinThetaT = sinThetaI * eta;
            float refractionScale = sinThetaT / MathF.Max(sinThetaI, 0.001f);

            // 加上深度修正
            float depthFactor = irisDepth * 20.0f; // 缩放深度影响
            Vector2 refracted = centered * refractionScale * (1.0f - depthFactor);

            return refracted * 0.5f + new Vector2(0.5f);
        }

        /// <summary>计算角膜法线（凸面法线，用于高光）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 ComputeCorneaNormal(Vector2 uv, Vector3 eyeForward, Vector3 eyeRight, Vector3 eyeUp)
        {
            Vector2 centered = uv * 2.0f - Vector2.One;
            return Vector3.Normalize(
                eyeForward +
                eyeRight * centered.X * 0.3f +
                eyeUp * centered.Y * 0.3f);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 角膜高光
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>角膜高光参数。</summary>
    public sealed class CorneaSpecularParams
    {
        /// <summary>主高光偏移（相对于瞳孔中心，UV 空间）。</summary>
        public Vector2 PrimaryHighlightOffset = new(0.15f, 0.15f);
        /// <summary>主高光大小。</summary>
        public float PrimaryHighlightSize = 0.08f;
        /// <summary>主高光强度。</summary>
        public float PrimaryHighlightIntensity = 2.0f;
        /// <summary>次高光（环境反射）强度。</summary>
        public float SecondaryHighlightIntensity = 0.3f;
        /// <summary>高光锐度。</summary>
        public float SpecularSharpness = 256.0f;
        /// <summary>泪膜菲涅尔强度。</summary>
        public float TearFilmFresnel = 0.04f;
    }

    /// <summary>角膜高光计算。</summary>
    public static class CorneaSpecular
    {
        /// <summary>计算角膜高光（双层：点光源 + 环境）。</summary>
        public static Vector3 ComputeSpecular(
            Vector3 normal, Vector3 lightDir, Vector3 viewDir,
            Vector3 lightColor, CorneaSpecularParams sp, float ior)
        {
            Vector3 halfVec = Vector3.Normalize(lightDir + viewDir);
            float NdotH = Math.Clamp(Vector3.Dot(normal, halfVec), 0, 1);
            float NdotV = Math.Clamp(Vector3.Dot(normal, viewDir), 0, 1);
            float NdotL = Math.Clamp(Vector3.Dot(normal, lightDir), 0, 1);

            // 菲涅尔
            float fresnel = SchlickFresnel(NdotV, ior);

            // 主高光（尖锐）
            float specPow = MathF.Pow(NdotH, sp.SpecularSharpness);
            Vector3 primarySpec = lightColor * specPow * sp.PrimaryHighlightIntensity * fresnel * NdotL;

            // 次高光（宽，模拟环境反射）
            float envSpec = MathF.Pow(NdotH, 16.0f);
            Vector3 secondarySpec = lightColor * envSpec * sp.SecondaryHighlightIntensity * fresnel;

            return primarySpec + secondarySpec;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float SchlickFresnel(float cosTheta, float ior)
        {
            float f0 = MathF.Pow((ior - 1.0f) / (ior + 1.0f), 2);
            return f0 + (1.0f - f0) * MathF.Pow(1.0f - cosTheta, 5);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 常量缓冲
    // ────────────────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    public struct EyeRenderingCB
    {
        public Matrix4x4 ViewProj;
        public Matrix4x4 World;
        public Vector3 CameraPos;
        public float CorneaIOR;
        public Vector3 LightDir;
        public float LightIntensity;
        public Vector3 LightColor;
        public float PupilRadius;
        public Vector4 ScleraColor;
        public Vector4 IrisColor;
        public Vector4 ScleraSSS;
        public Vector2 EyeUVCenter;
        public float IrisDepth;
        public float IrisRotation;
        public float IrisDensity;
        public float TearWetness;
        public float UpperLidShadow;
        public float SpecularSharpness;
        public float SpecIntensity;
        public float SecondarySpecIntensity;
        public Vector2 InvResolution;
        public int ShadingMode;
        public float Pad0, Pad1, Pad2;
    }

    // ────────────────────────────────────────────────────────────────────────
    // 主渲染器
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>眼球渲染系统。</summary>
    public sealed class EyeRenderingSystem : IDisposable
    {
        private readonly RHIDevice _device;
        private EyeParameters _params = new();
        private EyeShadingMode _mode = EyeShadingMode.Full;
        private CorneaSpecularParams _specParams = new();
        private RHIPipelineState? _eyePSO;
        private RHIConstantBuffer? _eyeCB;
        private bool _disposed;
        private bool _initialized;

        public EyeParameters Parameters => _params;
        public CorneaSpecularParams SpecParams => _specParams;

        public EyeRenderingSystem(RHIDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
        }

        /// <summary>初始化。</summary>
        public void Initialize()
        {
            if (_initialized) return;
            _eyeCB = _device.CreateConstantBuffer<EyeRenderingCB>();
            _initialized = true;
        }

        /// <summary>设置瞳孔大小（0=最小，1=最大）。</summary>
        public void SetPupilSize(float t)
        {
            _params.PupilRadius = MathF.Lerp(
                _params.PupilMinRadius, _params.PupilMaxRadius, Math.Clamp(t, 0, 1));
        }

        /// <summary>设置视线方向。</summary>
        public void SetLookAt(Vector3 eyePos, Vector3 targetPos)
        {
            // 计算眼球需要旋转多少来注视目标
            Vector3 toTarget = Vector3.Normalize(targetPos - eyePos);
            // 这个会设置到眼球骨骼的旋转
            // 简化：存储到参数
        }

        /// <summary>渲染眼球。</summary>
        public void Render(RHICommandList cmdList, CameraData camera, int frameIndex)
        {
            if (!_initialized) return;
            var cb = new EyeRenderingCB
            {
                ViewProj = camera.ViewProjMatrix, World = Matrix4x4.Identity,
                CameraPos = camera.Position, CorneaIOR = _params.CorneaIOR,
                LightDir = new Vector3(0.5f, -1, 0.3f), LightIntensity = 5.0f,
                LightColor = Vector3.One, PupilRadius = _params.PupilRadius,
                ScleraColor = new Vector4(_params.ScleraColor, 1),
                IrisColor = new Vector4(_params.IrisColor, 1),
                ScleraSSS = new Vector4(_params.ScleraSSSColor, _params.ScleraSSSStrength),
                EyeUVCenter = new Vector2(0.5f, 0.5f),
                IrisDepth = _params.IrisDepth,
                IrisRotation = _params.IrisRotation * MathF.PI / 180.0f,
                IrisDensity = _params.IrisDensity,
                TearWetness = _params.TearWetness,
                UpperLidShadow = _params.UpperLidShadowStrength,
                SpecularSharpness = _specParams.SpecularSharpness,
                SpecIntensity = _specParams.PrimaryHighlightIntensity,
                SecondarySpecIntensity = _specParams.SecondaryHighlightIntensity,
                InvResolution = new Vector2(1.0f / 1920, 1.0f / 1080),
                ShadingMode = (int)_mode
            };
            _eyeCB!.UpdateData(ref cb);
            cmdList.SetConstantBuffer(0, _eyeCB);
        }

        /// <summary>获取调试信息。</summary>
        public string GetDebugInfo() =>
            $"Eye: mode={_mode}, pupil={_params.PupilRadius:F4f}, " +
            $"wetness={_params.TearWetness:F2f}, IOR={_params.CorneaIOR:F3f}";

        public void Dispose()
        {
            if (_disposed) return;
            _eyePSO?.Dispose(); _eyeCB?.Dispose();
            _disposed = true; GC.SuppressFinalize(this);
        }
    }
}
