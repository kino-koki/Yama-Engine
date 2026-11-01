// =====================================================================================
//  YamaEngine - InterchangeScene：FBX / USD 统一中间场景格式
//  命名空间：YamaEngine.Assets.Importers
// -------------------------------------------------------------------------------------
//  此类型定义与 YamaEngine.Assets 命名空间中的 InterchangeBaseNode / 
//  InterchangeBaseNodeContainer 互为补充——前者是 UE5 风格的可管线化 IR 节点树，
//  此处是轻量的扁平化中间格式，专为 FBX / USD 解析器直接产出设计。
//
//  InterchangeScene 包含场景层级、网格、材质、骨骼、动画等所有属性，
//  解析器将源文件解析为该格式后，再桥接到 InterchangeBaseNodeContainer 供管线处理。
// =====================================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.Assets.Importers
{
    // =================================================================================
    //  InterchangeScene —— 统一中间场景格式
    //  包含场景层级 + 网格 + 材质 + 骨骼 + 动画
    // =================================================================================

    /// <summary>
    /// 统一中间场景格式。FBX 和 USD 解析器的共同输出目标。
    /// 场景结构为节点树（InterchangeNode），每个节点可挂载网格 / 材质 / 骨骼引用。
    /// </summary>
    public sealed class InterchangeScene
    {
        /// <summary>场景名称（通常为文件名无扩展名）。</summary>
        public string Name = string.Empty;

        /// <summary>源文件路径。</summary>
        public string SourceFilePath = string.Empty;

        /// <summary>源文件格式标识（"FBX" / "USD"）。</summary>
        public string SourceFormat = string.Empty;

        /// <summary>根节点列表（场景层级树的顶层）。</summary>
        public List<InterchangeNode> RootNodes = new();

        /// <summary>扁平网格列表（场景中所有网格）。</summary>
        public List<InterchangeMesh> Meshes = new();

        /// <summary>扁平材质列表（场景中所有材质）。</summary>
        public List<InterchangeMaterial> Materials = new();

        /// <summary>扁平骨骼列表（场景中所有骨骼）。</summary>
        public List<InterchangeBone> Bones = new();

        /// <summary>动画剪辑列表。</summary>
        public List<InterchangeAnimationClip> AnimationClips = new();

        /// <summary>导入过程中记录的警告 / 错误消息。</summary>
        public List<string> ImportMessages = new();

        private Dictionary<string, InterchangeNode>? _nodesCache;

        /// <summary>节点索引表（名称 → 节点），按需从根节点树构建。</summary>
        public Dictionary<string, InterchangeNode> Nodes
        {
            get
            {
                if (_nodesCache == null)
                {
                    _nodesCache = new Dictionary<string, InterchangeNode>(StringComparer.Ordinal);
                    void AddRecursive(InterchangeNode? n)
                    {
                        if (n == null) return;
                        if (!string.IsNullOrEmpty(n.Name))
                            _nodesCache[n.Name] = n;
                        foreach (var c in n.Children) AddRecursive(c);
                    }
                    foreach (var r in RootNodes) AddRecursive(r);
                }
                return _nodesCache;
            }
        }
    }

    // =================================================================================
    //  InterchangeNode —— 场景层级节点
    // =================================================================================

    /// <summary>
    /// 场景层级节点。包含名称、变换矩阵、子节点、以及网格/材质/骨骼引用。
    /// </summary>
    public sealed class InterchangeNode
    {
        /// <summary>节点名称。</summary>
        public string Name = string.Empty;

        /// <summary>局部变换矩阵（相对于父节点）。</summary>
        public Matrix4x4 LocalTransform = Matrix4x4.Identity;

        /// <summary>平移（从 LocalTransform 分解得到，方便访问）。</summary>
        public Vector3 Translation => LocalTransform.Translation;

        /// <summary>旋转四元数（从 LocalTransform 分解得到）。</summary>
        public Quaternion Rotation
        {
            get
            {
                Matrix4x4.Decompose(LocalTransform, out _, out var r, out _);
                return r;
            }
        }

        /// <summary>缩放（从 LocalTransform 分解得到）。</summary>
        public Vector3 Scale
        {
            get
            {
                Matrix4x4.Decompose(LocalTransform, out var s, out _, out _);
                return s;
            }
        }

        /// <summary>子节点列表。</summary>
        public List<InterchangeNode> Children = new();

        /// <summary>引用的网格索引（-1 表示无网格）。</summary>
        public int MeshIndex = -1;

        /// <summary>引用的材质索引列表（按子网格 / 面组顺序）。</summary>
        public List<int> MaterialIndices = new();

        /// <summary>引用的骨骼索引（-1 表示无蒙皮）。</summary>
        public int BoneIndex = -1;

        /// <summary>元数据字典（格式特定附加信息）。</summary>
        public Dictionary<string, string> Metadata = new(StringComparer.Ordinal);

        /// <summary>是否为纯变换节点（无网格 / 材质 / 骨骼引用）。</summary>
        public bool IsTransformNode => MeshIndex < 0 && MaterialIndices.Count == 0 && BoneIndex < 0;
    }

    // =================================================================================
    //  InterchangeMesh —— 网格数据
    // =================================================================================

    /// <summary>
    /// 网格：包含几何数据（顶点 / 索引 / UV / 法线 / 切线 / 颜色 / 骨骼权重）。
    /// </summary>
    public sealed class InterchangeMesh
    {
        /// <summary>网格名称。</summary>
        public string Name = string.Empty;

        /// <summary>所属节点名称（对应 <see cref="InterchangeNode.Name"/>，用于应用节点变换）。</summary>
        public string NodeId = string.Empty;

        /// <summary>顶点位置数组。</summary>
        public List<Vector3> Vertices = new();

        /// <summary>三角形索引数组（每 3 个一组构成一个三角形）。</summary>
        public List<int> Indices = new();

        /// <summary>法线数组（与顶点一一对应）。</summary>
        public List<Vector3> Normals = new();

        /// <summary>UV 通道 0 数组（与顶点一一对应）。</summary>
        public List<Vector2> UV0 = new();

        /// <summary>切线数组（与顶点一一对应，w 分量表示副法线方向）。</summary>
        public List<Vector4> Tangents = new();

        /// <summary>顶点颜色数组（与顶点一一对应）。</summary>
        public List<Vector4> Colors = new();

        /// <summary>骨骼权重数组（与顶点一一对应）。</summary>
        public List<InterchangeVertexWeight> BoneWeights = new();

        /// <summary>使用该网格的材质索引列表（按子网格顺序）。</summary>
        public List<int> MaterialIndices = new();

        /// <summary>三角形数量。</summary>
        public int TriangleCount => Indices.Count / 3;

        /// <summary>元数据字典（格式特定附加信息）。</summary>
        public Dictionary<string, string> Metadata = new(StringComparer.Ordinal);
    }

    // =================================================================================
    //  InterchangeVertexWeight —— 顶点骨骼权重
    // =================================================================================

    /// <summary>
    /// 单顶点的骨骼权重。最多支持 4 根骨骼影响。
    /// </summary>
    public struct InterchangeVertexWeight
    {
        /// <summary>骨骼索引（在 InterchangeScene.Bones 中的索引）。</summary>
        public int BoneIndex0; public int BoneIndex1; public int BoneIndex2; public int BoneIndex3;

        /// <summary>对应骨骼的权重（应归一化到 [0,1] 且和为 1）。</summary>
        public float Weight0; public float Weight1; public float Weight2; public float Weight3;

        /// <summary>有效骨骼数量（0~4）。</summary>
        public int Influences
        {
            get
            {
                int c = 0;
                if (Weight0 > 0) c++;
                if (Weight1 > 0) c++;
                if (Weight2 > 0) c++;
                if (Weight3 > 0) c++;
                return c;
            }
        }

        public InterchangeVertexWeight(int b0, float w0) { BoneIndex0 = b0; Weight0 = w0; BoneIndex1 = BoneIndex2 = BoneIndex3 = 0; Weight1 = Weight2 = Weight3 = 0f; }
        public InterchangeVertexWeight(int b0, float w0, int b1, float w1)
        { BoneIndex0 = b0; Weight0 = w0; BoneIndex1 = b1; Weight1 = w1; BoneIndex2 = BoneIndex3 = 0; Weight2 = Weight3 = 0f; }
        public InterchangeVertexWeight(int b0, float w0, int b1, float w1, int b2, float w2)
        { BoneIndex0 = b0; Weight0 = w0; BoneIndex1 = b1; Weight1 = w1; BoneIndex2 = b2; Weight2 = w2; BoneIndex3 = 0; Weight3 = 0f; }
        public InterchangeVertexWeight(int b0, float w0, int b1, float w1, int b2, float w2, int b3, float w3)
        { BoneIndex0 = b0; Weight0 = w0; BoneIndex1 = b1; Weight1 = w1; BoneIndex2 = b2; Weight2 = w2; BoneIndex3 = b3; Weight3 = w3; }

        /// <summary>归一化权重（确保权重和为 1）。</summary>
        public InterchangeVertexWeight Normalize()
        {
            float sum = Weight0 + Weight1 + Weight2 + Weight3;
            if (sum > 1e-6f)
            {
                float inv = 1f / sum;
                return new InterchangeVertexWeight(BoneIndex0, Weight0 * inv, BoneIndex1, Weight1 * inv,
                    BoneIndex2, Weight2 * inv, BoneIndex3, Weight3 * inv);
            }
            return this;
        }
    }

    // =================================================================================
    //  InterchangeMaterial —— 材质数据
    // =================================================================================

    /// <summary>
    /// PBR 材质：包含漫反射/法线/粗糙度/金属度/自发光贴图路径。
    /// </summary>
    public sealed class InterchangeMaterial
    {
        /// <summary>材质名称。</summary>
        public string Name = string.Empty;

        /// <summary>漫反射颜色（线性 RGB）。</summary>
        public Vector3 DiffuseColor = Vector3.One;

        /// <summary>自发光颜色（线性 RGB）。</summary>
        public Vector3 EmissiveColor = Vector3.Zero;

        /// <summary>自发光强度。</summary>
        public float EmissiveIntensity = 0f;

        /// <summary>粗糙度 [0,1]。</summary>
        public float Roughness = 0.5f;

        /// <summary>金属度 [0,1]。</summary>
        public float Metallic = 0f;

        /// <summary>不透明度 [0,1]（1=不透明）。</summary>
        public float Opacity = 1f;

        /// <summary>是否双面渲染。</summary>
        public bool DoubleSided = false;

        /// <summary>漫反射贴图路径（可为空）。</summary>
        public string DiffuseTexturePath = string.Empty;

        /// <summary>法线贴图路径（可为空）。</summary>
        public string NormalTexturePath = string.Empty;

        /// <summary>粗糙度贴图路径（可为空）。</summary>
        public string RoughnessTexturePath = string.Empty;

        /// <summary>金属度贴图路径（可为空）。</summary>
        public string MetallicTexturePath = string.Empty;

        /// <summary>自发光贴图路径（可为空）。</summary>
        public string EmissiveTexturePath = string.Empty;

        /// <summary>环境光遮蔽贴图路径（可为空）。</summary>
        public string OcclusionTexturePath = string.Empty;

        /// <summary>额外纹理槽位键值对。</summary>
        public Dictionary<string, string> ExtraTextureSlots = new(StringComparer.Ordinal);

        /// <summary>着色模型（"Phong" / "Lambert" / "PBR" / "Standard"）。</summary>
        public string ShadingModel = "PBR";
    }

    // =================================================================================
    //  InterchangeBone —— 骨骼数据
    // =================================================================================

    /// <summary>
    /// 单根骨骼：名称、父级、绑定姿势矩阵。
    /// </summary>
    public sealed class InterchangeBone
    {
        /// <summary>骨骼名称。</summary>
        public string Name = string.Empty;

        /// <summary>父骨骼名称（根骨骼为空）。</summary>
        public string ParentName = string.Empty;

        /// <summary>父骨骼索引（-1 表示根骨骼）。</summary>
        public int ParentIndex = -1;

        /// <summary>绑定姿势位置（局部空间）。</summary>
        public Vector3 LocalPosition;
        /// <summary>绑定姿势旋转（局部空间）。</summary>
        public Quaternion LocalRotation = Quaternion.Identity;
        /// <summary>绑定姿势缩放（局部空间）。</summary>
        public Vector3 LocalScale = Vector3.One;

        /// <summary>绑定姿势矩阵（局部空间，相对于父骨骼）。</summary>
        public Matrix4x4 BindPoseLocal = Matrix4x4.Identity;

        /// <summary>绑定姿势的逆矩阵（世界空间），用于蒙皮计算。</summary>
        public Matrix4x4 InverseBindPose = Matrix4x4.Identity;
    }

    // =================================================================================
    //  InterchangeAnimationClip —— 动画剪辑
    // =================================================================================

    /// <summary>
    /// 动画剪辑：包含名称、时长、帧率以及骨骼动画通道列表。
    /// </summary>
    public sealed class InterchangeAnimationClip
    {
        /// <summary>动画剪辑名称。</summary>
        public string Name = string.Empty;

        /// <summary>动画时长（秒）。</summary>
        public float Duration;

        /// <summary>帧率（帧 / 秒）。</summary>
        public float FrameRate = 30f;

        /// <summary>骨骼轨道列表。</summary>
        public List<InterchangeBoneTrack> BoneTracks = new();

        /// <summary>元数据字典（格式特定附加信息，如根运动骨骼、循环标记等）。</summary>
        public Dictionary<string, string> Metadata = new(StringComparer.Ordinal);
    }

    // =================================================================================
    //  InterchangeBoneTrack —— 单根骨骼的动画轨道
    // =================================================================================

    /// <summary>
    /// 单根骨骼的动画轨道：包含位置 / 旋转 / 缩放关键帧序列。
    /// </summary>
    public sealed class InterchangeBoneTrack
    {
        /// <summary>目标骨骼名称。</summary>
        public string BoneName = string.Empty;

        /// <summary>位置关键帧（时间秒, 位移向量）。</summary>
        public List<(float Time, Vector3 Value)> PositionKeys = new();

        /// <summary>旋转关键帧（时间秒, 四元数）。</summary>
        public List<(float Time, Quaternion Value)> RotationKeys = new();

        /// <summary>缩放关键帧（时间秒, 缩放向量）。</summary>
        public List<(float Time, Vector3 Value)> ScaleKeys = new();
    }

    // =================================================================================
    //  Bridge：InterchangeScene <-> InterchangeBaseNodeContainer 转换
    //
    //  将解析器产出的轻量 InterchangeScene 桥接到 UE5 风格 IR 节点树，
    //  以便复用 InterchangeJobManager 的管线 / 工厂流水线。
    // =================================================================================

    /// <summary>
    /// 桥接器：将 InterchangeScene 转换为 InterchangeBaseNodeContainer（IR 节点树）。
    /// 转换后可通过 InterchangeJobManager 的管线 / 工厂系统进一步处理。
    /// </summary>
    public static class InterchangeSceneBridge
    {
        /// <summary>
        /// 将 InterchangeScene 转换为 IR 节点容器。
        /// </summary>
        /// <param name="scene">解析器产出的中间场景。</param>
        /// <returns>填充好的 IR 节点容器。</returns>
        public static InterchangeBaseNodeContainer ToNodeContainer(InterchangeScene scene)
        {
            var container = new InterchangeBaseNodeContainer();

            // 设置源节点
            container.Source = new InterchangeSourceNode
            {
                FilePath = scene.SourceFilePath,
                Format = scene.SourceFormat,
                UpAxis = InterchangeUpAxis.YUp,
                CoordSystem = InterchangeCoordSystem.RightHanded,
                UnitScale = 1.0f
            };

            // 创建根场景节点
            var rootSceneNode = new InterchangeSceneNode
            {
                UniqueId = scene.SourceFormat + "_Root_" + Guid.NewGuid().ToString("N"),
                DisplayLabel = scene.Name
            };
            container.AddNode(rootSceneNode);

            // 1) 添加所有材质节点
            var materialUidMap = new Dictionary<int, string>(); // scene.Materials index -> UID
            for (int i = 0; i < scene.Materials.Count; i++)
            {
                var sm = scene.Materials[i];
                var matNode = new InterchangeMaterialNode
                {
                    UniqueId = scene.SourceFormat + "_Mat_" + i,
                    DisplayLabel = sm.Name,
                    BaseColor = sm.DiffuseColor,
                    Metallic = sm.Metallic,
                    Roughness = sm.Roughness,
                    Emissive = sm.EmissiveColor,
                    EmissiveIntensity = sm.EmissiveIntensity,
                    Alpha = sm.Opacity,
                    DoubleSided = sm.DoubleSided
                };
                if (!string.IsNullOrEmpty(sm.DiffuseTexturePath))
                    matNode.TextureSlots[InterchangeMaterialNode.Slots.BaseColor] = sm.DiffuseTexturePath;
                if (!string.IsNullOrEmpty(sm.NormalTexturePath))
                    matNode.TextureSlots[InterchangeMaterialNode.Slots.Normal] = sm.NormalTexturePath;
                if (!string.IsNullOrEmpty(sm.EmissiveTexturePath))
                    matNode.TextureSlots[InterchangeMaterialNode.Slots.Emissive] = sm.EmissiveTexturePath;
                container.AddNode(matNode, rootSceneNode.UniqueId);
                materialUidMap[i] = matNode.UniqueId;
            }

            // 2) 添加所有网格节点
            var meshUidMap = new Dictionary<int, string>(); // scene.Meshes index -> UID
            for (int i = 0; i < scene.Meshes.Count; i++)
            {
                var sm = scene.Meshes[i];
                var meshNode = new InterchangeMeshNode
                {
                    UniqueId = scene.SourceFormat + "_Mesh_" + i,
                    DisplayLabel = sm.Name,
                    Vertices = sm.Vertices,
                    Indices = sm.Indices,
                    Normals = sm.Normals,
                    UV0 = sm.UV0,
                    Tangents = sm.Tangents
                };
                // 映射材质引用
                foreach (var mi in sm.MaterialIndices)
                {
                    if (materialUidMap.TryGetValue(mi, out var matUid))
                        meshNode.MaterialUids.Add(matUid);
                }
                container.AddNode(meshNode, rootSceneNode.UniqueId);
                meshUidMap[i] = meshNode.UniqueId;
            }

            // 3) 添加所有骨骼节点
            var boneUidMap = new Dictionary<int, string>(); // scene.Bones index -> UID
            if (scene.Bones.Count > 0)
            {
                var skelNode = new InterchangeSkeletonNode
                {
                    UniqueId = scene.SourceFormat + "_Skel_" + Guid.NewGuid().ToString("N"),
                    DisplayLabel = scene.Name + "_Skeleton"
                };
                for (int i = 0; i < scene.Bones.Count; i++)
                {
                    var sb = scene.Bones[i];
                    skelNode.Bones.Add(new YamaEngine.Assets.InterchangeBone
                    {
                        Name = sb.Name,
                        ParentName = sb.ParentName,
                        LocalPosition = sb.BindPoseLocal.Translation,
                        LocalRotation = Quaternion.CreateFromRotationMatrix(sb.BindPoseLocal),
                        LocalScale = new Vector3(
                            new Vector3(sb.BindPoseLocal.M11, sb.BindPoseLocal.M12, sb.BindPoseLocal.M13).Length(),
                            new Vector3(sb.BindPoseLocal.M21, sb.BindPoseLocal.M22, sb.BindPoseLocal.M23).Length(),
                            new Vector3(sb.BindPoseLocal.M31, sb.BindPoseLocal.M32, sb.BindPoseLocal.M33).Length())
                    });
                    boneUidMap[i] = skelNode.UniqueId + "_Bone_" + i;
                }
                container.AddNode(skelNode, rootSceneNode.UniqueId);
            }

            // 4) 递归转换场景层级
            foreach (var rootNode in scene.RootNodes)
                ConvertNodeHierarchy(rootNode, rootSceneNode.UniqueId, container, meshUidMap, materialUidMap, boneUidMap, scene);

            return container;
        }

        private static void ConvertNodeHierarchy(InterchangeNode node, string parentUid,
            InterchangeBaseNodeContainer container,
            Dictionary<int, string> meshUidMap,
            Dictionary<int, string> materialUidMap,
            Dictionary<int, string> boneUidMap,
            InterchangeScene scene)
        {
            var sceneNode = new InterchangeSceneNode
            {
                UniqueId = scene.SourceFormat + "_SceneNode_" + Guid.NewGuid().ToString("N"),
                DisplayLabel = node.Name,
                Translation = node.Translation,
                Rotation = node.Rotation,
                Scale = node.Scale
            };

            // 关联网格
            if (node.MeshIndex >= 0 && meshUidMap.TryGetValue(node.MeshIndex, out var meshUid))
                sceneNode.GeometryUid = meshUid;

            // 关联材质
            foreach (var mi in node.MaterialIndices)
            {
                if (materialUidMap.TryGetValue(mi, out var matUid))
                    sceneNode.MaterialUids.Add(matUid);
            }

            container.AddNode(sceneNode, parentUid);

            // 递归子节点
            foreach (var child in node.Children)
                ConvertNodeHierarchy(child, sceneNode.UniqueId, container, meshUidMap, materialUidMap, boneUidMap, scene);
        }
    }
}
