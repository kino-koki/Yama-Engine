// ══════════════════════════════════════════════════════════════════════════
// EngineSubsystemScaffold.cs
//
// 本文件提供一批"应当存在但当前代码库尚未实现"的子系统/包装类型的
// 最小可编译占位定义（scaffold）。目的是让 YamaEngine 在机械层面
// 通过 C# 编译（消除 CS0246 / 后续 CS1061）。
//
// 这些类型只暴露被引用点实际用到的成员，并以安全默认值实现。
// 真正的生产实现需要按各模块规格补全（参见 EVALUATION_AND_SPEC）。
//
// ⚠️ 这是编译脚手架，不是功能实现。
// ══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.RHI;

namespace YamaEngine
{
    // ────────────────────────────────────────────────────────────────────
    // 兼容枚举（被简化绑定 API 引用，但原 RHI 层未定义）
    // ────────────────────────────────────────────────────────────────────
    [System.Flags]
    public enum ShaderStage : uint
    {
        Vertex = 1,
        Hull = 2,
        Domain = 4,
        Geometry = 8,
        Pixel = 16,
        Compute = 32,
        All = 0xFFFFFFFF
    }

    public enum ShadingRate
    {
        Full = 0,
        Half = 1,
        Quarter = 2,
        OneEighth = 3
    }

    // ────────────────────────────────────────────────────────────────────
    // MaterialLibrary — 内置材质库包装（提供默认材质注册点）
    // ────────────────────────────────────────────────────────────────────
    public class MaterialLibrary
    {
        public MaterialLibrary() { }

        public MaterialLibrary(RHIDevice device)
        {
            Device = device;
        }

        public RHIDevice? Device { get; }



















        // ── 材质事件 ──
        /// <summary>材质编译完成事件 (materialId, shaderCode)。</summary>
        public event Action<object, object>? OnMaterialCompiled;
        /// <summary>材质参数变更事件 (materialId, parameterName, value)。</summary>
        public event Action<object, string, object>? OnMaterialParameterChanged;
        /// <summary>材质纹理绑定变更事件 (materialId, slot, texture)。</summary>
        public event Action<object, object, object>? OnMaterialTextureChanged;
        /// <summary>材质热重载事件 (materialId)。</summary>
        public event Action<object>? OnMaterialHotReload;

        private readonly Dictionary<object, Material> _materials = new();

        /// <summary>注册材质到库。</summary>
        public Material AddMaterial(object materialId)
        {
            var material = new Material(materialId);
            _materials[materialId] = material;
            TotalMaterialCount = _materials.Count;
            return material;
        }

        /// <summary>获取全部已注册材质。</summary>
        public IEnumerable<Material> GetAllMaterials()
        {
            return _materials.Values;
        }

        /// <summary>按 ID 获取材质 (不存在返回 null)。</summary>
        public Material? GetMaterial(object materialId)
        {
            return _materials.TryGetValue(materialId, out var m) ? m : null;
        }

        /// <summary>默认材质。</summary>
        public Material GetDefaultMaterial()
        {
            if (_defaultMaterial == null)
                _defaultMaterial = _materials.Count > 0 ? FirstMaterial() : new Material("__default__");
            return _defaultMaterial;
        }

        private Material? _defaultMaterial;

        private Material FirstMaterial()
        {
            foreach (var m in _materials.Values)
                return m;
            return new Material("__default__");
        }

        /// <summary>错误材质。</summary>
        public Material GetErrorMaterial() => new Material("__error__");

        /// <summary>调试材质。</summary>
        public Material GetDebugMaterial() => new Material("__debug__");

        /// <summary>已注册材质总数。</summary>
        public int TotalMaterialCount;
    }

    /// <summary>
    /// 材质 (最小集成表示) — 供 WorldIntegrationHelper 与管线材质同步使用。
    /// </summary>
    public class Material
    {
        public Material(object materialId)
        {
            MaterialId = materialId;
        }

        public object MaterialId { get; }

        /// <summary>是否已编译。</summary>
        public bool IsCompiled { get; set; }

        /// <summary>获取编译后着色器字节码。</summary>
        public object GetShaderBytecode() => ShaderBytecode;

        /// <summary>获取参数布局。</summary>
        public object GetParameterLayout() => ParameterLayout;

        /// <summary>重新编译材质 (返回是否成功)。</summary>
        public bool Recompile()
        {
            IsCompiled = true;
            return true;
        }

        public object ShaderBytecode { get; set; } = Array.Empty<byte>();
        public object ParameterLayout { get; set; } = new();
    }
}

namespace YamaEngine.GameFramework
{
    // ────────────────────────────────────────────────────────────────────
    // SceneComponent — Actor 变换层级根组件（UE 风格）
    // ────────────────────────────────────────────────────────────────────
    public class SceneComponent
    {
        public Vector3 WorldPosition { get; set; } = Vector3.Zero;
        public Quaternion WorldRotation { get; set; } = Quaternion.Identity;
        public Vector3 WorldScale { get; set; } = Vector3.One;

        public void SetWorldPosition(Vector3 pos) => WorldPosition = pos;
        public void SetWorldRotation(Quaternion rot) => WorldRotation = rot;
        public void SetWorldScale(Vector3 scale) => WorldScale = scale;
    }
}

namespace YamaEngine.Physics
{
    // ────────────────────────────────────────────────────────────────────
    // PhysicsScene — Jolt/物理世界场景包装（向后兼容层）
    // 注：PhysicsSceneManager / PhysicsSceneHandle 已由
    //     Physics/UnityPhysicsSceneSystem.cs 真实定义，此处不再重复。
    // ────────────────────────────────────────────────────────────────────
    public class PhysicsScene
    {
        public PhysicsScene() { }
        public PhysicsWorld? World { get; set; }






        // ── 兼容成员自动补齐（占位/默认） ──
        public object Update(params object[] __args) => default;
        public object RemoveRigidBody(params object[] __args) => default;
        public object AddRigidBody(params object[] __args) => default;
        public object AddBoxCollider(params object[] __args) => default;
        public object ApplyImpulse(params object[] __args) => default;
        public object GetLinearVelocity(params object[] __args) => default;
        public object GetPosition(params object[] __args) => default;
    }
}

namespace YamaEngine.Animation
{
    // ────────────────────────────────────────────────────────────────────
    // BlendSpace — 混合空间资源（UE5 BlendSpace 等价）
    // ────────────────────────────────────────────────────────────────────
    public class BlendSpace
    {
        public float BlendParameterX { get; set; }
        public float BlendParameterY { get; set; }
        public bool ApplyRootMotion { get; set; }
        public float LengthSeconds { get; set; } = 1f;

        public object? Sample(object? skeleton, float time) => null;
    }
}

namespace YamaEngine.World
{
    using YamaEngine.Rendering;

    // ────────────────────────────────────────────────────────────────────
    // TerrainSystem — 地形系统（高度/法线/Splat 查询 + 节点图引脚）
    // 注：TerrainHeightmap 已由 World/TerrainSystem.cs 真实定义（sealed）。
    // ────────────────────────────────────────────────────────────────────
    public class TerrainSystem
    {
        public TerrainHeightmap Heightmap { get; set; } = new TerrainHeightmap(1, 1, 1);
        public Dictionary<string, object> Parameters { get; } = new();

        public float SampleHeight(Vector3 position) => 0f;
        public float SampleSlope(Vector3 position) => 0f;
        public Vector3 SampleNormal(Vector3 position) => Vector3.UnitY;

        public object[] GetInputPins() => System.Array.Empty<object>();
        public object[] GetOutputPins() => System.Array.Empty<object>();
    }
}

namespace YamaEngine.VFX
{
    // ────────────────────────────────────────────────────────────────────
    // ParticleModuleCoreCore — 粒子模块基类（FindModule<T> 约束用）
    // ────────────────────────────────────────────────────────────────────
    public abstract class ParticleModuleCoreCore
    {
        public string ModuleName { get; set; } = string.Empty;
    }
}

namespace YamaEngine.Diagnostics
{
    // ────────────────────────────────────────────────────────────────────
    // CPUHotSpot / LeakSuspect — 性能/内存报告数据载体
    // ────────────────────────────────────────────────────────────────────
    public class CPUHotSpot
    {



















        // ── 兼容成员自动补齐（占位/默认） ──
        public string FunctionName;
        public dynamic SelfMs;
        public int CallCount;
        public dynamic AvgMs;
    }

    public class LeakSuspect
    {
















        // ── 兼容成员自动补齐（占位/默认） ──
        public string Tag;
        public float SizeBytes;
        public float Age;
    }
}

namespace YamaEngine.Core
{
    using YamaEngine.Rendering;

    // ────────────────────────────────────────────────────────────────────
    // SwapChain — 高层交换链包装（旧引导路径使用；与 RHISwapChain 区分）
    //
    // 注意：旧引导路径的构造签名 (RHIDevice, int, int, int, bool) 不含窗口
    // 句柄，无法实际构造 RHISwapChain。此处仅提供编译占位，运行时由
    // EngineApplication 的 RHISwapChain 路径承担真实交换链职责。
    // ────────────────────────────────────────────────────────────────────
    public class SwapChain
    {
        public SwapChain(RHIDevice device, int width, int height, int vsyncMode, bool fullscreen)
        {
            Device = device;
            Width = width;
            Height = height;
            BufferCount = 2;
            VSync = vsyncMode != 0;
        }

        public RHIDevice Device { get; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int BufferCount { get; }
        public bool VSync { get; }
        public int CurrentBackBufferIndex { get; private set; }

        // 占位：真实后缓冲由 EngineApplication 路径提供，此处返回 null 占位。
        public RHITexture BackBuffer => null!;
        public RHITexture GetBackBuffer(int index) => null!;
        public RHIDescriptorHandle GetBackBufferRTV(int index) => default;
        public void Present(bool vsync) { }
        public void Resize(int w, int h) { Width = w; Height = h; }
        public void Dispose() { }
    }

    // ────────────────────────────────────────────────────────────────────
    // PhysicsSystem — 物理系统包装
    // ────────────────────────────────────────────────────────────────────
    public class PhysicsSystem
    {
        public PhysicsSystem(object? engineConfig) { }
        public void SetFixedTimestep(float step) { }
        public void SetMaxSubSteps(int steps) { }
        public void Initialize() { }
    }

    // ────────────────────────────────────────────────────────────────────
    // UISystem — UI 系统包装
    // ────────────────────────────────────────────────────────────────────
    public class UISystem
    {
        public UISystem(RHIDevice device, RenderGraph renderGraph) { }
        public void SetViewport(int width, int height) { }
    }

    // ────────────────────────────────────────────────────────────────────
    // GameInstance — 游戏实例（持有全局游戏状态）
    // ────────────────────────────────────────────────────────────────────
    public class GameInstance
    {
        public GameInstance(WorldSystemsBootstrap bootstrap) { }
        public void Initialize() { }
    }
}

namespace YamaEngine.Rendering
{
    // ────────────────────────────────────────────────────────────────────
    // RenderResource — RenderGraph 集成返回的资源句柄包装
    // ────────────────────────────────────────────────────────────────────
    public class RenderResource
    {
        public string Name { get; set; } = string.Empty;
        public object? Handle { get; set; }
    }

    // ────────────────────────────────────────────────────────────────────
    // ProbeFieldHardwareRayTracer — ProbeField 硬件光追
    // ────────────────────────────────────────────────────────────────────
    public class ProbeFieldHardwareRayTracer
    {
        public ProbeFieldHardwareRayTracer(RHIDevice device, RenderGraph renderGraph) { }
        public int MaxBounces { get; set; } = 4;

        public void TraceIndirectLighting(RHICommandList cmd, in Matrix4x4 view, in Matrix4x4 proj,
            ProbeFieldSurfaceCache surfaceCache, object? geometryBuffer) { }
    }

    // ────────────────────────────────────────────────────────────────────
    // ProbeFieldVoxelSystem — ProbeField 体素系统
    // ────────────────────────────────────────────────────────────────────
    public class ProbeFieldVoxelSystem
    {
        public ProbeFieldVoxelSystem(RHIDevice device) { }
        public bool EnableDebugVisualization { get; set; }
        public void SetVoxelSize(float size) { }
        public void RenderVoxels(RHICommandList cmd, object? renderView) { }
    }

    // ────────────────────────────────────────────────────────────────────
    // ProbeFieldIntegration — 将 HWRT / SurfaceCache / Voxel 集成为一体
    // ────────────────────────────────────────────────────────────────────
    public class ProbeFieldIntegration
    {
        public ProbeFieldIntegration(ProbeFieldHardwareRayTracer hwrt,
            ProbeFieldSurfaceCache surfaceCache, ProbeFieldVoxelSystem voxel)
        {
            HardwareRayTracer = hwrt;
            SurfaceCache = surfaceCache;
            VoxelSystem = voxel;
        }

        public ProbeFieldHardwareRayTracer HardwareRayTracer { get; }
        public ProbeFieldSurfaceCache SurfaceCache { get; }
        public ProbeFieldVoxelSystem VoxelSystem { get; }

        public bool IsRegistered { get; set; }
        public int TraceMode { get; set; }
        public RHITexture? BrdfLut { get; set; }

        public void MarkPrimitiveDirty(int primitiveId, in Matrix4x4 transform) { }
        public void RegisterPrimitive(object primitive) { }
        public void UnregisterPrimitive(int primitiveId) { }
    }
}
