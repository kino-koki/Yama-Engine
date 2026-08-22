using System.Diagnostics;
using System.IO;
using System.Numerics;
using ShanyuEngine.Animation;
using ShanyuEngine.Assets;
using ShanyuEngine.Modeling;
using ShanyuEngine.Skinning;

namespace ShanyuEngine.Rendering
{
    public interface IRenderDevice
    {
        string Name { get; }
        RenderDeviceCapabilities Capabilities { get; }
        FrameStats Render(IRenderScene scene, float deltaTime);
    }

    /// <summary>
    ///   可调整大小的渲染设备接口。由支持窗口尺寸变化的渲染后端实现
    ///   （如 <see cref="VulkanRenderDevice"/>），<see cref="Runtime.RuntimeGameLoop"/>
    ///   通过此接口在窗口大小改变时通知渲染设备重建交换链/渲染目标。
    /// </summary>
    public interface IResizableRenderDevice : IRenderDevice
    {
        /// <summary>调整渲染目标尺寸（重建交换链/深度缓冲等）。</summary>
        /// <param name="width">新宽度（像素）。</param>
        /// <param name="height">新高度（像素）。</param>
        void Resize(int width, int height);
    }

    public interface IRenderScene
    {
        string Name { get; }
        IReadOnlyList<RenderMesh> Meshes { get; }
        IReadOnlyList<RenderCamera> Cameras { get; }
        IReadOnlyList<RenderLight> Lights { get; }
        FrameStats Stats { get; }
    }

    public sealed class NullRenderDevice : IRenderDevice
    {
        public string Name { get; }
        public RenderDeviceCapabilities Capabilities { get; } = new()
        {
            Backend = "Null",
            SupportsPbrMaterials = true,
            SupportsShadows = true,
            SupportsInstancing = false
        };

        public NullRenderDevice(string name = "Shanyu Null Render Device")
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Shanyu Null Render Device" : name;
        }

        public FrameStats Render(IRenderScene scene, float deltaTime)
        {
            if (scene == null) throw new ArgumentNullException(nameof(scene));

            var sw = Stopwatch.StartNew();
            var visibleMeshes = scene.Meshes.Count(m => m.IsVisible);
            sw.Stop();

            return new FrameStats
            {
                MeshCount = scene.Meshes.Count,
                VisibleMeshCount = visibleMeshes,
                DrawCallCount = visibleMeshes,
                CameraCount = scene.Cameras.Count,
                LightCount = scene.Lights.Count,
                FrameTimeMilliseconds = MathF.Max(0f, (float)sw.Elapsed.TotalMilliseconds),
                DeltaTimeSeconds = MathF.Max(0f, deltaTime)
            };
        }

        public FrameStats Render(IRenderScene scene, RenderGraph graph, float deltaTime)
        {
            if (scene == null) throw new ArgumentNullException(nameof(scene));
            if (graph == null) throw new ArgumentNullException(nameof(graph));

            var stats = Render(scene, deltaTime);
            var plan = graph.Compile();
            stats.RenderGraphPassCount = plan.Passes.Count;
            stats.TransientResourceCount = plan.Stats.TransientResourceCount;
            stats.ImportedResourceCount = plan.Stats.ImportedResourceCount;
            stats.EstimatedRenderGraphMemoryBytes = plan.Stats.EstimatedMemoryBytes;
            stats.RenderGraphErrorCount = plan.Errors.Count;
            stats.DrawCallCount += plan.IsValid ? plan.Passes.Count : 0;
            return stats;
        }
    }

    public static class RenderSceneBuilder
    {
        private static readonly Dictionary<string, EditableMesh> _meshDataCache = new();

        public static RenderSceneSnapshot FromAssetDatabase(AssetDatabase assets, string sceneName = "Main")
        {
            if (assets == null) throw new ArgumentNullException(nameof(assets));
            return FromObjects(assets.SceneObjects, sceneName);
        }

        public static RenderSceneSnapshot FromObjects(IEnumerable<SceneObject> objects, string sceneName = "Main")
        {
            if (objects == null) throw new ArgumentNullException(nameof(objects));
            var meshes = new List<RenderMesh>();
            var skinnedMeshes = new List<RenderSkinnedMesh>();
            var cameras = new List<RenderCamera>();
            var lights = new List<RenderLight>();

            foreach (var obj in objects)
            {
                if (obj == null) continue;
                var camera = obj.GetComponent<CameraComponent>();
                if (camera != null)
                {
                    cameras.Add(new RenderCamera
                    {
                        ObjectId = obj.Id,
                        Name = obj.Name,
                        WorldMatrix = obj.LocalToWorldMatrix,
                        FieldOfView = camera.FieldOfView,
                        NearClip = camera.NearClip,
                        FarClip = camera.FarClip,
                        AspectRatio = camera.AspectRatio,
                        IsMain = camera.IsMain
                    });
                }

                var light = obj.GetComponent<LightComponent>();
                if (light != null)
                {
                    lights.Add(new RenderLight
                    {
                        ObjectId = obj.Id,
                        Name = obj.Name,
                        WorldMatrix = obj.LocalToWorldMatrix,
                        LightType = light.LightType,
                        Color = light.Color,
                        Intensity = light.Intensity,
                        Range = light.Range,
                        CastsShadows = light.CastsShadows
                    });
                }

                // 检测骨骼动画对象，将其加入 SkinnedMeshes 列表
                var animator = obj.GetComponent<SkinnedMeshAnimatorComponent>();
                if (animator?.Skeleton != null && animator.CurrentBoneMatrices != null && animator.CurrentBoneMatrices.Length > 0)
                {
                    if (!string.IsNullOrEmpty(obj.MeshAssetPath) && File.Exists(obj.MeshAssetPath))
                    {
                        try
                        {
                            if (!_meshDataCache.TryGetValue(obj.MeshAssetPath, out var mesh))
                            {
                                mesh = EditableMesh.Read(obj.MeshAssetPath);
                                _meshDataCache[obj.MeshAssetPath] = mesh;
                            }

                            if (mesh?.BoneWeights != null && mesh.BoneWeights.Count > 0)
                            {
                                Matrix4x4[] skinMatrices = animator.CurrentBoneMatrices;

                                string skinPath = Path.ChangeExtension(obj.MeshAssetPath, ".skin");
                                if (File.Exists(skinPath))
                                {
                                    try
                                    {
                                        var binding = SkinBinding.Read(skinPath);
                                        skinMatrices = AnimationRenderBridge.ComputeSkinningMatrices(animator.CurrentBoneMatrices, binding);
                                    }
                                    catch { /* 忽略缺失或损坏的绑定文件 */ }
                                }

                                skinnedMeshes.Add(new RenderSkinnedMesh
                                {
                                    ObjectId = obj.Id,
                                    Name = obj.Name,
                                    MeshAssetPath = obj.MeshAssetPath,
                                    Mesh = mesh,
                                    WorldMatrix = obj.LocalToWorldMatrix,
                                    BoneMatrices = skinMatrices,
                                    IsVisible = obj.IsVisible,
                                    CastShadow = obj.CastShadow,
                                    ReceiveShadow = obj.ReceiveShadow,
                                    Material = new RenderMaterial
                                    {
                                        AssetPath = obj.MaterialAssetPath ?? string.Empty,
                                        TextureAssetPath = obj.TextureAssetPath ?? string.Empty,
                                        BaseColor = obj.BaseColor,
                                        Metallic = obj.Metallic,
                                        Roughness = obj.Roughness
                                    }
                                });

                                // 跳过常规网格渲染，避免重复绘制
                                if (!obj.IsVisible || !LooksRenderableMesh(obj.MeshAssetPath)) continue;
                                continue;
                            }
                        }
                        catch { /* 忽略无法加载的网格 */ }
                    }
                }

                if (!obj.IsVisible || !LooksRenderableMesh(obj.MeshAssetPath)) continue;

                meshes.Add(new RenderMesh
                {
                    ObjectId = obj.Id,
                    Name = obj.Name,
                    MeshAssetPath = obj.MeshAssetPath,
                    WorldMatrix = obj.LocalToWorldMatrix,
                    IsVisible = obj.IsVisible,
                    CastShadow = obj.CastShadow,
                    ReceiveShadow = obj.ReceiveShadow,
                    Material = new RenderMaterial
                    {
                        AssetPath = obj.MaterialAssetPath ?? string.Empty,
                        TextureAssetPath = obj.TextureAssetPath ?? string.Empty,
                        BaseColor = obj.BaseColor,
                        Metallic = obj.Metallic,
                        Roughness = obj.Roughness
                    }
                });
            }

            var stats = new FrameStats
            {
                MeshCount = meshes.Count + skinnedMeshes.Count,
                VisibleMeshCount = meshes.Count(m => m.IsVisible) + skinnedMeshes.Count(m => m.IsVisible),
                DrawCallCount = meshes.Count(m => m.IsVisible) + skinnedMeshes.Count(m => m.IsVisible),
                CameraCount = cameras.Count,
                LightCount = lights.Count,
                DeltaTimeSeconds = 0f,
                FrameTimeMilliseconds = 0f
            };

            return new RenderSceneSnapshot
            {
                Name = string.IsNullOrWhiteSpace(sceneName) ? "Main" : sceneName,
                Meshes = meshes,
                SkinnedMeshes = skinnedMeshes,
                Cameras = cameras,
                Lights = lights,
                Stats = stats
            };
        }

        private static bool LooksRenderableMesh(string? meshAssetPath)
        {
            if (string.IsNullOrWhiteSpace(meshAssetPath)) return false;
            if (meshAssetPath.StartsWith("Camera_", StringComparison.OrdinalIgnoreCase)) return false;
            if (meshAssetPath.StartsWith("Light_", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }
    }

    public sealed class RenderSceneSnapshot : IRenderScene
    {
        public string Name { get; set; } = "Main";
        public IReadOnlyList<RenderMesh> Meshes { get; set; } = Array.Empty<RenderMesh>();
        public IReadOnlyList<RenderCamera> Cameras { get; set; } = Array.Empty<RenderCamera>();
        public IReadOnlyList<RenderLight> Lights { get; set; } = Array.Empty<RenderLight>();
        public IReadOnlyList<RenderSkinnedMesh> SkinnedMeshes { get; set; } = Array.Empty<RenderSkinnedMesh>();
        public FrameStats Stats { get; set; } = new();
    }

    public sealed class RenderMesh
    {
        public Guid ObjectId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string MeshAssetPath { get; set; } = string.Empty;
        public Matrix4x4 WorldMatrix { get; set; } = Matrix4x4.Identity;
        public RenderMaterial Material { get; set; } = new();
        public bool IsVisible { get; set; } = true;
        public bool CastShadow { get; set; } = true;
        public bool ReceiveShadow { get; set; } = true;
    }

    /// <summary>
    /// 骨骼蒙皮网格渲染数据。包含 EditableMesh 引用、骨骼矩阵与材质。
    /// </summary>
    public sealed class RenderSkinnedMesh
    {
        public Guid ObjectId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string MeshAssetPath { get; set; } = string.Empty;
        public EditableMesh? Mesh { get; set; }
        public Matrix4x4 WorldMatrix { get; set; } = Matrix4x4.Identity;
        public Matrix4x4[] BoneMatrices { get; set; } = Array.Empty<Matrix4x4>();
        public RenderMaterial Material { get; set; } = new();
        public bool IsVisible { get; set; } = true;
        public bool CastShadow { get; set; } = true;
        public bool ReceiveShadow { get; set; } = true;
    }

    public sealed class RenderMaterial
    {
        public string AssetPath { get; set; } = string.Empty;
        public string TextureAssetPath { get; set; } = string.Empty;
        public Vector3 BaseColor { get; set; } = Vector3.One;
        public float Metallic { get; set; }
        public float Roughness { get; set; } = 0.5f;
    }

    public sealed class RenderCamera
    {
        public Guid ObjectId { get; set; }
        public string Name { get; set; } = string.Empty;
        public Matrix4x4 WorldMatrix { get; set; } = Matrix4x4.Identity;
        public float FieldOfView { get; set; } = 60f;
        public float NearClip { get; set; } = 0.1f;
        public float FarClip { get; set; } = 1000f;
        public float AspectRatio { get; set; } = 1.777f;
        public bool IsMain { get; set; }
    }

    public sealed class RenderLight
    {
        public Guid ObjectId { get; set; }
        public string Name { get; set; } = string.Empty;
        public Matrix4x4 WorldMatrix { get; set; } = Matrix4x4.Identity;
        public string LightType { get; set; } = "Directional";
        public Vector3 Color { get; set; } = Vector3.One;
        public float Intensity { get; set; } = 1f;
        public float Range { get; set; } = 10f;
        public bool CastsShadows { get; set; } = true;
    }

    public sealed class RenderDeviceCapabilities
    {
        public string Backend { get; set; } = "Unknown";
        public bool SupportsPbrMaterials { get; set; }
        public bool SupportsShadows { get; set; }
        public bool SupportsInstancing { get; set; }
    }

    public sealed class FrameStats
    {
        public int MeshCount { get; set; }
        public int VisibleMeshCount { get; set; }
        public int DrawCallCount { get; set; }
        public int CameraCount { get; set; }
        public int LightCount { get; set; }
        public float DeltaTimeSeconds { get; set; }
        public float FrameTimeMilliseconds { get; set; }
        public int RenderGraphPassCount { get; set; }
        public int TransientResourceCount { get; set; }
        public int ImportedResourceCount { get; set; }
        public long EstimatedRenderGraphMemoryBytes { get; set; }
        public int RenderGraphErrorCount { get; set; }
        public int VisibilityCandidateCount { get; set; }
        public int GpuVisibleDrawCount { get; set; }
        public int HzbOccludedCount { get; set; }
        public int SparseShadowAtlasAllocatedPages { get; set; }
        public int SparseShadowAtlasEvictedPages { get; set; }
    }
}
