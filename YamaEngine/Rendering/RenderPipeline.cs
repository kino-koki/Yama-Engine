// RenderPipeline.cs - Full rendering pipeline for Yama Engine
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Numerics = System.Numerics;

namespace YamaEngine.Rendering;

public enum RenderPath { Forward, Deferred, ClusteredForward, ClusteredDeferred }
public enum ShadowQuality { Disabled, Low = 256, Medium = 512, High = 1024, Ultra = 2048, Cinema = 4096 }
public enum AntiAliasing { None, FXAA, SMAA_1X, SMAA_T2X, TAA_Low, TAA_Medium, TAA_High, MSAA_2X = 10, MSAA_4X = 20, MSAA_8X = 30 }
public enum ReflectionMethod { SSR, SSR_Roughness, RayTraced, Precomputed, None }
public enum GlobalIllumination { None, VoxelGI, Lightmap, DDGI, RayTraced }

[Flags]
public enum RenderFeatureFlags : ulong
{
    None = 0, ShadowMaps = 1 << 0, CascadedShadows = 1 << 1,
    ContactShadows = 1 << 2, ScreenSpaceAO = 1 << 3, ScreenSpaceGI = 1 << 4,
    ScreenSpaceRefl = 1 << 5, Bloom = 1 << 6, Tonemapping = 1 << 7,
    ColorGrading = 1 << 8, Vignette = 1 << 9, ChromaticAberration = 1 << 10,
    DepthOfField = 1 << 11, MotionBlur = 1 << 12, LensFlare = 1 << 13,
    VolumetricFog = 1 << 14, GlobalIllumination = 1 << 15, Decals = 1 << 16,
    SkinnedMeshes = 1 << 17, InstancedMeshes = 1 << 18, GPUDrivenCulling = 1 << 19,
    VirtualTexturing = 1 << 20, TemporalUpsampling = 1 << 21, HDR = 1 << 22,
}

public sealed class RenderPipelineSettings
{
    public RenderPath Path = RenderPath.Forward;
    public ShadowQuality ShadowQuality = ShadowQuality.High;
    public AntiAliasing AA = AntiAliasing.TAA_Medium;
    public ReflectionMethod Reflections = ReflectionMethod.SSR;
    public GlobalIllumination GI = GlobalIllumination.None;
    public int ShadowAtlasSize = 4096;
    public bool HDR = true; public bool VSync = true;
    public bool DynamicResolution = false; public float ResolutionScale = 1.0f;
    public RenderFeatureFlags FeatureFlags = RenderFeatureFlags.ShadowMaps |
        RenderFeatureFlags.CascadedShadows | RenderFeatureFlags.ScreenSpaceAO |
        RenderFeatureFlags.Bloom | RenderFeatureFlags.Tonemapping |
        RenderFeatureFlags.ColorGrading | RenderFeatureFlags.HDR;
}

#region Frustum & Culling Types

public struct AABB
{
    public Numerics.Vector3 Min, Max;
    public Numerics.Vector3 Center => (Min + Max) * 0.5f;
    public Numerics.Vector3 Extents => (Max - Min) * 0.5f;
    public float Radius => Extents.Length();
    public float SurfaceArea { get { var d = Max - Min; return 2 * (d.X * d.Y + d.X * d.Z + d.Y * d.Z); } }
    public AABB(Numerics.Vector3 min, Numerics.Vector3 max) { Min = min; Max = max; }

    public static AABB FromCenterExtents(Numerics.Vector3 c, Numerics.Vector3 e) => new(c - e, c + e);
    public static AABB FromPoints(ReadOnlySpan<Numerics.Vector3> pts)
    {
        if (pts.Length == 0) return new AABB();
        var mn = pts[0]; var mx = pts[0];
        for (int i = 1; i < pts.Length; i++) { mn = Numerics.Vector3.Min(mn, pts[i]); mx = Numerics.Vector3.Max(mx, pts[i]); }
        return new AABB(mn, mx);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Numerics.Vector3 GetPositiveVertex(Numerics.Vector3 n) => new(n.X >= 0 ? Max.X : Min.X, n.Y >= 0 ? Max.Y : Min.Y, n.Z >= 0 ? Max.Z : Min.Z);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Numerics.Vector3 GetNegativeVertex(Numerics.Vector3 n) => new(n.X >= 0 ? Min.X : Max.X, n.Y >= 0 ? Min.Y : Max.Y, n.Z >= 0 ? Min.Z : Max.Z);
    public bool Intersects(AABB o) => Min.X <= o.Max.X && Max.X >= o.Min.X && Min.Y <= o.Max.Y && Max.Y >= o.Min.Y && Min.Z <= o.Max.Z && Max.Z >= o.Min.Z;
    public bool Contains(Numerics.Vector3 p) => p.X >= Min.X && p.X <= Max.X && p.Y >= Min.Y && p.Y <= Max.Y && p.Z >= Min.Z && p.Z <= Max.Z;
    public AABB Transform(Numerics.Matrix4x4 m)
    {
        var c = GetCorners(); for (int i = 0; i < 8; i++) c[i] = Numerics.Vector3.Transform(c[i], m);
        return FromPoints(c);
    }
    public Numerics.Vector3[] GetCorners() => new Numerics.Vector3[] {
        new(Min.X,Min.Y,Min.Z), new(Max.X,Min.Y,Min.Z), new(Min.X,Max.Y,Min.Z), new(Max.X,Max.Y,Min.Z),
        new(Min.X,Min.Y,Max.Z), new(Max.X,Min.Y,Max.Z), new(Min.X,Max.Y,Max.Z), new(Max.X,Max.Y,Max.Z) };
    public void Expand(float a) { Min -= new Numerics.Vector3(a); Max += new Numerics.Vector3(a); }
    public void Encapsulate(Numerics.Vector3 p) { Min = Numerics.Vector3.Min(Min, p); Max = Numerics.Vector3.Max(Max, p); }
    public void Encapsulate(AABB o) { Min = Numerics.Vector3.Min(Min, o.Min); Max = Numerics.Vector3.Max(Max, o.Max); }
}

public struct Sphere { public Numerics.Vector3 Center; public float Radius; public Sphere(Numerics.Vector3 c, float r) { Center = c; Radius = r; } }
public struct Plane { public Numerics.Vector3 Normal; public float Distance; public Plane(Numerics.Vector3 n, float d) { Normal = n; Distance = d; } }
public struct Ray { public Numerics.Vector3 Origin, Direction; public Ray(Numerics.Vector3 o, Numerics.Vector3 d) { Origin = o; Direction = d; } public Numerics.Vector3 GetPoint(float t) => Origin + Direction * t; }

public enum IntersectionResult { Outside, Inside, Intersect }

public struct Frustum
{
    public Plane[] Planes;

    public static Frustum FromViewProjection(Numerics.Matrix4x4 vp)
    {
        var p = new Plane[6];
        p[0] = new Plane(new(vp.M14+vp.M11,vp.M24+vp.M21,vp.M34+vp.M31), vp.M44+vp.M41);
        p[1] = new Plane(new(vp.M14-vp.M11,vp.M24-vp.M21,vp.M34-vp.M31), vp.M44-vp.M41);
        p[2] = new Plane(new(vp.M14-vp.M12,vp.M24-vp.M22,vp.M34-vp.M32), vp.M44-vp.M42);
        p[3] = new Plane(new(vp.M14+vp.M12,vp.M24+vp.M22,vp.M34+vp.M32), vp.M44+vp.M42);
        p[4] = new Plane(new(vp.M13,vp.M23,vp.M33), vp.M43);
        p[5] = new Plane(new(vp.M14-vp.M13,vp.M24-vp.M23,vp.M34-vp.M33), vp.M44-vp.M43);
        for (int i = 0; i < 6; i++) { float l = MathF.Sqrt(p[i].Normal.X*p[i].Normal.X+p[i].Normal.Y*p[i].Normal.Y+p[i].Normal.Z*p[i].Normal.Z); if (l > 0) { p[i].Normal /= l; p[i].Distance /= l; } }
        return new Frustum { Planes = p };
    }

    public IntersectionResult Intersects(AABB b)
    {
        bool anyOut = false;
        for (int i = 0; i < 6; i++)
        {
            var pv = b.GetPositiveVertex(Planes[i].Normal);
            if (Numerics.Vector3.Dot(pv, Planes[i].Normal) + Planes[i].Distance < 0) return IntersectionResult.Outside;
            var nv = b.GetNegativeVertex(Planes[i].Normal);
            if (Numerics.Vector3.Dot(nv, Planes[i].Normal) + Planes[i].Distance < 0) anyOut = true;
        }
        return anyOut ? IntersectionResult.Intersect : IntersectionResult.Inside;
    }
}

#endregion

#region View Data

public struct ViewData
{
    public Numerics.Matrix4x4 ViewMatrix, ProjectionMatrix, ViewProjectionMatrix, InvViewProjection, PrevViewProjection;
    public Numerics.Vector3 CameraPosition, CameraDirection;
    public float NearPlane, FarPlane, FOV, AspectRatio, JitterX, JitterY, DeltaTime, TotalTime;
    public int FrameCount, RenderWidth, RenderHeight;
    public Frustum GetFrustum() => Frustum.FromViewProjection(ViewProjectionMatrix);
}

#endregion

#region Frame Resource Pool

public sealed class FrameResourcePool : IDisposable
{
    private readonly int _poolSize;
    private int _frameIndex;
    private bool _disposed;

    public FrameResourcePool(int poolSize = 3) { _poolSize = poolSize; }
    public void AdvanceFrame() { _frameIndex = (_frameIndex + 1) % _poolSize; }
    public void SignalCompletion() { }
    public void Dispose() { if (_disposed) return; _disposed = true; }
}

#endregion

#region GPU Culling System

public sealed class GPUDrivenCullingSystem
{
    public struct VisibleInstance { public int InstanceId; public uint MeshletOffset, MeshletCount, LodIndex; public float ScreenSize; }
    public struct DrawCommand { public uint IndexCount, InstanceCount, FirstIndex, VertexOffset, FirstInstance, MaterialId; }

    private readonly List<VisibleInstance> _visible = new();
    private readonly List<DrawCommand> _draws = new();
    private int _culled;

    public IReadOnlyList<VisibleInstance> Visible => _visible;
    public IReadOnlyList<DrawCommand> DrawCalls => _draws;
    public int CulledCount => _culled;

    public void Cull(Frustum f, ReadOnlySpan<AABB> bounds, ReadOnlySpan<int> ids)
    {
        _visible.Clear(); _culled = 0;
        for (int i = 0; i < bounds.Length && i < ids.Length; i++)
        {
            if (f.Intersects(bounds[i]) != IntersectionResult.Outside)
                _visible.Add(new VisibleInstance { InstanceId = ids[i] });
            else _culled++;
        }
    }
}

#endregion

#region Render Passes

public interface IRenderPass : IDisposable { string Name { get; } bool IsEnabled { get; } void Execute(ViewData view, SceneRenderData scene, FrameResourcePool pool, GPUDrivenCullingSystem culling); }

public abstract class BaseRenderPass : IRenderPass
{
    public abstract string Name { get; }
    public virtual bool IsEnabled => true;
    public abstract void Execute(ViewData view, SceneRenderData scene, FrameResourcePool pool, GPUDrivenCullingSystem culling);
    public virtual void Dispose() { }
}

public sealed class ShadowMapPass : BaseRenderPass
{
    public override string Name => "ShadowMap";
    public int CascadeCount = 4;
    public int MapSize = 1024;

    public float[] GetCascadeSplits(float near, float far)
    {
        var s = new float[CascadeCount];
        for (int i = 0; i < CascadeCount; i++)
        {
            float p = (i + 1f) / CascadeCount;
            float log = near * MathF.Pow(far / near, p);
            float uni = near + (far - near) * p;
            s[i] = 0.85f * log + 0.15f * uni;
        }
        return s;
    }

    public override void Execute(ViewData view, SceneRenderData scene, FrameResourcePool pool, GPUDrivenCullingSystem culling) { }
}

public sealed class SSAOPass : BaseRenderPass
{
    public override string Name => "SSAO";
    public float Radius = 0.5f, Power = 2f;
    public int SampleCount = 16;

    public float[] GenerateKernel()
    {
        var rng = new Random(42);
        var k = new float[SampleCount * 3];
        for (int i = 0; i < SampleCount; i++)
        {
            float s = (float)i / SampleCount; s = 0.1f + 0.9f * s * s;
            k[i*3] = (float)(rng.NextDouble()*2-1)*s; k[i*3+1] = (float)(rng.NextDouble()*2-1)*s; k[i*3+2] = (float)(rng.NextDouble()*0.5f+0.5f)*s;
        }
        return k;
    }
    public override void Execute(ViewData v, SceneRenderData s, FrameResourcePool p, GPUDrivenCullingSystem c) { }
}

public sealed class BloomPass : BaseRenderPass
{
    public override string Name => "Bloom";
    public float Intensity = 1f, Threshold = 1f, SoftThreshold = 0.5f;
    public int DownsampleCount = 6;
    public override void Execute(ViewData v, SceneRenderData s, FrameResourcePool p, GPUDrivenCullingSystem c) { }
}

public sealed class TonemappingPass : BaseRenderPass
{
    public override string Name => "Tonemapping";
    public float Exposure = 1f, WhitePoint = 11.2f;
    public static float ACES(float x) { float a=2.51f,b=0.03f,c=2.43f,d=0.59f,e=0.14f; return Math.Clamp((x*(a*x+b))/(x*(c*x+d)+e),0,1); }
    public static float Reinhard(float x) => x / (1f + x);
    public override void Execute(ViewData v, SceneRenderData s, FrameResourcePool p, GPUDrivenCullingSystem c) { }
}

public sealed class DepthOfFieldPass : BaseRenderPass { public override string Name => "DepthOfField"; public float FocalDistance = 10f, FocalRegion = 3f, MaxBlur = 0.5f; public override void Execute(ViewData v, SceneRenderData s, FrameResourcePool p, GPUDrivenCullingSystem c) { } }
public sealed class MotionBlurPass : BaseRenderPass { public override string Name => "MotionBlur"; public int SampleCount = 8; public float Strength = 1f; public override void Execute(ViewData v, SceneRenderData s, FrameResourcePool p, GPUDrivenCullingSystem c) { } }
public sealed class ScreenSpaceReflectionPass : BaseRenderPass { public override string Name => "SSR"; public int MaxSteps = 64; public float Thickness = 0.1f; public override void Execute(ViewData v, SceneRenderData s, FrameResourcePool p, GPUDrivenCullingSystem c) { } }
public sealed class GeometryBufferPass : BaseRenderPass { public override string Name => "GeometryBuffer"; public override void Execute(ViewData v, SceneRenderData s, FrameResourcePool p, GPUDrivenCullingSystem c) { } }
public sealed class MainLightingPass : BaseRenderPass { public override string Name => "MainLighting"; public override void Execute(ViewData v, SceneRenderData s, FrameResourcePool p, GPUDrivenCullingSystem c) { } }
public sealed class PresentPass : BaseRenderPass { public override string Name => "Present"; public override void Execute(ViewData v, SceneRenderData s, FrameResourcePool p, GPUDrivenCullingSystem c) { } }

#endregion

#region Scene Render Data

public sealed class SceneRenderData
{
    public AABB[] InstanceBounds = Array.Empty<AABB>();
    public int[] InstanceIds = Array.Empty<int>();
    public Numerics.Matrix4x4[] InstanceTransforms = Array.Empty<Numerics.Matrix4x4>();
    public Sphere[] LightBounds = Array.Empty<Sphere>();
    public int VisibleLightCount, TotalLightCount;
}

#endregion

#region Virtual Texture System

public sealed class VirtualTextureSystem
{
    public struct Page { public long Address; public int MipLevel, PhysicalLocation; public ulong LastAccessFrame; public bool Resident; }
    private readonly Dictionary<long, Page> _table = new();
    private readonly Queue<int> _free = new();
    private int _faults, _requests;
    public int PageFaults => _faults;
    public VirtualTextureSystem(int pool = 4096, int pageSize = 128)
    {
        int max = (pool / pageSize) * (pool / pageSize);
        for (int i = 0; i < max; i++) _free.Enqueue(i);
    }
    public Page RequestPage(long addr, int mip, ulong frame)
    {
        _requests++;
        if (_table.TryGetValue(addr, out var p)) { p.LastAccessFrame = frame; _table[addr] = p; return p; }
        _faults++;
        if (_free.Count == 0) EvictLRU(frame);
        if (_free.Count > 0)
        {
            var np = new Page { Address = addr, MipLevel = mip, PhysicalLocation = _free.Dequeue(), LastAccessFrame = frame, Resident = true };
            _table[addr] = np; return np;
        }
        return default;
    }
    private void EvictLRU(ulong frame)
    {
        long ok = 0; ulong of = ulong.MaxValue;
        foreach (var kv in _table) { if (kv.Value.LastAccessFrame < of) { of = kv.Value.LastAccessFrame; ok = kv.Key; } }
        if (_table.TryGetValue(ok, out var ev)) { _free.Enqueue(ev.PhysicalLocation); _table.Remove(ok); }
    }
}

#endregion

#region Light Types & Manager

public sealed class LightManager
{
    private readonly List<LightData> _lights = new();
    private readonly List<LightData> _visible = new();
    public int LightCount => _lights.Count;
    public int VisibleLightCount => _visible.Count;
    public IReadOnlyList<LightData> Lights => _lights;
    public IReadOnlyList<LightData> VisibleLights => _visible;

    public int Add(LightData l) { _lights.Add(l); return _lights.Count - 1; }
    public bool Remove(int i) { if (i < 0 || i >= _lights.Count) return false; _lights.RemoveAt(i); return true; }
    public void Update(int i, LightData l) { if (i >= 0 && i < _lights.Count) _lights[i] = l; }
    public void Clear() { _lights.Clear(); _visible.Clear(); }

    public void Cull(Frustum f, ReadOnlySpan<Sphere> bounds)
    {
        _visible.Clear();
        for (int i = 0; i < _lights.Count && i < bounds.Length; i++)
            if (f.Intersects(new AABB(bounds[i].Center - new Numerics.Vector3(bounds[i].Radius), bounds[i].Center + new Numerics.Vector3(bounds[i].Radius))) != IntersectionResult.Outside)
                _visible.Add(_lights[i]);
    }

    public void SortByImportance(Numerics.Vector3 camPos) =>
        _visible.Sort((a, b) => Numerics.Vector3.DistanceSquared(a.Position, camPos).CompareTo(Numerics.Vector3.DistanceSquared(b.Position, camPos)));
}

#endregion

#region Decal System


public sealed class DecalManager
{
    private readonly List<DecalData> _decals = new();
    private readonly List<DecalData> _visible = new();
    private int _max = 1024;
    public int DecalCount => _decals.Count;
    public int Add(DecalData d) { if (_decals.Count >= _max) return -1; _decals.Add(d); return _decals.Count - 1; }
    public bool Remove(int i) { if (i < 0 || i >= _decals.Count) return false; _decals.RemoveAt(i); return true; }
    public void Clear() { _decals.Clear(); _visible.Clear(); }
    public void Cull(Frustum f, ReadOnlySpan<AABB> bounds)
    {
        _visible.Clear();
        for (int i = 0; i < _decals.Count && i < bounds.Length; i++)
            if (f.Intersects(bounds[i]) != IntersectionResult.Outside) _visible.Add(_decals[i]);
        _visible.Sort((a, b) => a.SortOrder.CompareTo(b.SortOrder));
    }
}

#endregion

#region Render Pipeline Core

public sealed class RenderPipeline : IDisposable
{
    private readonly RenderPipelineSettings _settings;
    private readonly FrameResourcePool _framePool;
    private readonly GPUDrivenCullingSystem _culling = new();
    private readonly List<IRenderPass> _passes = new();
    private ViewData _currentView;
    private bool _disposed;

    public RenderPipelineSettings Settings => _settings;
    public ViewData CurrentView => _currentView;
    public event Action? OnBeforeRender, OnAfterRender;

    public RenderPipeline(RenderPipelineSettings? s = null)
    {
        _settings = s ?? new RenderPipelineSettings();
        _framePool = new FrameResourcePool(3);
        BuildPasses();
    }

    private void BuildPasses()
    {
        _passes.Clear();
        if (_settings.ShadowQuality != ShadowQuality.Disabled) _passes.Add(new ShadowMapPass());
        if (_settings.Path == RenderPath.Deferred || _settings.Path == RenderPath.ClusteredDeferred) _passes.Add(new GeometryBufferPass());
        _passes.Add(new MainLightingPass());
        if ((_settings.FeatureFlags & RenderFeatureFlags.ScreenSpaceAO) != 0) _passes.Add(new SSAOPass());
        if ((_settings.FeatureFlags & RenderFeatureFlags.ScreenSpaceRefl) != 0) _passes.Add(new ScreenSpaceReflectionPass());
        if ((_settings.FeatureFlags & RenderFeatureFlags.Bloom) != 0) _passes.Add(new BloomPass());
        if ((_settings.FeatureFlags & RenderFeatureFlags.DepthOfField) != 0) _passes.Add(new DepthOfFieldPass());
        if ((_settings.FeatureFlags & RenderFeatureFlags.MotionBlur) != 0) _passes.Add(new MotionBlurPass());
        if ((_settings.FeatureFlags & RenderFeatureFlags.Tonemapping) != 0) _passes.Add(new TonemappingPass());
        _passes.Add(new PresentPass());
    }

    public void Render(ViewData view, SceneRenderData scene)
    {
        _currentView = view;
        OnBeforeRender?.Invoke();
        _framePool.AdvanceFrame();
        var f = view.GetFrustum();
        _culling.Cull(f, scene.InstanceBounds, scene.InstanceIds);
        foreach (var p in _passes) { if (p.IsEnabled) p.Execute(view, scene, _framePool, _culling); }
        _framePool.SignalCompletion();
        OnAfterRender?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        foreach (var p in _passes) p.Dispose();
        _framePool.Dispose();
    }
}

#endregion

