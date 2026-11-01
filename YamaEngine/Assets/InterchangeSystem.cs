// =====================================================================================
//  YamaEngine - Interchange 资产导入框架 (InterchangeSystem.cs)
//  命名空间：YamaEngine.Assets
// -------------------------------------------------------------------------------------
//  设计理念（对齐 Unreal Engine 5 的 Interchange Framework）：
//
//    UE5 的 Interchange Framework 是一套统一的、可扩展的多格式资产导入/导出管线，
//    核心思想是「解耦源文件解析与目标资产生成」：
//      1. 源文件解析器 (IInterchangeSourceParser)：把 FBX / OBJ / glTF / USD 等源文件
//         解析为格式无关的「中间表示」IR——即 InterchangeBaseNodeContainer 节点树。
//         —— 对齐 UE5 中 UInterchangeSourceNode + UInterchangeBaseNodeContainer。
//      2. 管线 (InterchangePipelineBase)：对 IR 节点树做处理 / 过滤 / 转换，例如
//         坐标轴变换、网格合并、法线/切线生成、材质默认值填充、LOD 简化、变换烘焙等。
//         —— 对齐 UE5 中 UInterchangePipeline::Execute(container)。
//      3. 工厂 (IInterchangeFactory)：从 IR 节点生成引擎原生资产（EditableMesh /
//         材质 / 纹理 / 动画 / 骨骼），并通过 AssetDatabase.RegisterAsset 注册到资产数据库。
//         —— 对齐 UE5 中 UInterchangeFactory::CreateAsset(node)。
//      4. 任务管理 (InterchangeJobManager)：编排「解析 -> 管线 -> 工厂」三段式流水线，
//         支持任务队列、异步执行、进度回调、取消与日志。
//         —— 对齐 UE5 中 UInterchangeManager / UInterchangePipelineStack。
//      5. 翻译器 (InterchangeTranslator)：把 IR 节点树翻译为场景对象树 SceneObject，
//         供场景编辑器直接使用；调试器 (InterchangeDebugger) 输出 IR 树供可视化。
//
//    与已有 AssetDatabase 的对接（严格使用已核实 API）：
//      - AssetDatabase.RegisterAsset(assetPath, guid, type, name) -> AssetMetadata  (触发 OnAssetImported)
//      - AssetDatabase.ImportAsset(sourceFile, type) -> AssetMetadata
//      - AssetDatabase.GetAsset(path) / GetAsset(guid) / GetAllAssets(filter) 用于重用检测
//      - AssetDatabase.OnAssetImported / OnAssetUpdated 事件回调
//    与 EditableMesh 的对接：
//      - EditableMesh.Vertices / Normals / UV0 / Faces(MeshFace A/B/C) / BoneWeights
//      - EditableMesh.RecalculateNormals() / EditableMesh.CreateCube(name, size)
//
//    本文件为「单一文件」实现，所有 Interchange 类型集中于此，便于编辑器与脚本调用。
// =====================================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using YamaEngine.Modeling;

namespace YamaEngine.Assets
{
    // =================================================================================
    //  第一部分：InterchangeAssetType —— Interchange 内部资产类型
    //
    //  对齐 UE5 中 EInterchangeAssetType 的概念：解析阶段产出的 IR 节点所描述的
    //  资产语义类型。它与引擎 AssetDatabase.AssetType 解耦——后者是数据库注册类型，
    //  前者是 IR 描述类型。工厂负责在前者与后者之间做映射。
    // =================================================================================

    /// <summary>
    /// Interchange 内部资产类型（对齐 UE5 EInterchangeAssetType）。
    /// 描述一个 IR 节点在语义上代表哪一类资产，用于工厂匹配与管线过滤。
    /// </summary>
    public enum InterchangeAssetType
    {
        /// <summary>网格：包含顶点 / 索引 / 法线 / UV / 切线 / 骨骼绑定。</summary>
        Mesh,
        /// <summary>动画：骨骼变换轨道序列，含时长与帧率。</summary>
        Animation,
        /// <summary>材质：PBR 属性与纹理引用槽位。</summary>
        Material,
        /// <summary>纹理：图像文件路径 / 格式 / sRGB / 尺寸。</summary>
        Texture,
        /// <summary>骨骼：骨骼层级与局部变换。</summary>
        Skeleton,
        /// <summary>相机：视场 / 近远裁剪面等。</summary>
        Camera,
        /// <summary>灯光：类型 / 颜色 / 强度 / 范围。</summary>
        Light,
        /// <summary>场景节点：变换层级与几何体引用（对应 UE5 UInterchangeSceneNode）。</summary>
        SceneNode,
        /// <summary>音频：音频文件路径与格式。</summary>
        Audio
    }

    // =================================================================================
    //  第二部分：InterchangeSourceNode —— 源节点（描述导入源）
    //
    //  对齐 UE5 中 UInterchangeSourceNode：记录被导入源文件的元信息，包括文件路径、
    //  格式、坐标轴约定（UpAxis / FrontAxis / CoordSystem）与单位缩放。它是 IR 容器
    //  的「源头」，管线（尤其是 AxisTransformPipeline）据此决定坐标变换。
    // =================================================================================

    /// <summary>坐标轴朝向约定（对齐 UE5 EInterchangeAxis 与 Maya/Max/Blender 约定）。</summary>
    public enum InterchangeUpAxis
    {
        /// <summary>Y 轴向上（Unreal / Unity / glTF 默认）。</summary>
        YUp,
        /// <summary>Z 轴向上（Maya / 3ds Max / OBJ 默认）。</summary>
        ZUp
    }

    /// <summary>坐标系手性（对齐 UE5 EInterchangeCoordSystem）。</summary>
    public enum InterchangeCoordSystem
    {
        /// <summary>左手系（Unreal 默认：+X 前、+Y 右、+Z 上）。</summary>
        LeftHanded,
        /// <summary>右手系（glTF / OpenGL / Maya 默认）。</summary>
        RightHanded
    }

    /// <summary>
    /// 源节点：描述一次导入的源文件信息（对齐 UE5 UInterchangeSourceNode）。
    /// 包含文件路径、格式标识、坐标轴/单位约定，供管线消费。
    /// </summary>
    public sealed class InterchangeSourceNode
    {
        /// <summary>源文件绝对路径。</summary>
        public string FilePath = string.Empty;
        /// <summary>格式标识（"FBX" / "OBJ" / "GLTF" / "GLB" / "USD" / "IMAGE"）。</summary>
        public string Format = string.Empty;
        /// <summary>源文件坐标轴朝向（默认 ZUp，OBJ/Maya 约定）。</summary>
        public InterchangeUpAxis UpAxis = InterchangeUpAxis.ZUp;
        /// <summary>源文件坐标系手性。</summary>
        public InterchangeCoordSystem CoordSystem = InterchangeCoordSystem.RightHanded;
        /// <summary>源文件单位相对米的缩放系数（1 单位 = UnitScale 米）。OBJ 常为 1，FBX 常为 1（cm 则 0.01）。</summary>
        public float UnitScale = 1.0f;
        /// <summary>源文件内容哈希（用于增量重导入判断）。</summary>
        public string ContentHash = string.Empty;

        /// <summary>从文件扩展名推断格式标识。</summary>
        public static string FormatFromExtension(string filePath)
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            return ext switch
            {
                ".fbx" => "FBX",
                ".obj" => "OBJ",
                ".gltf" => "GLTF",
                ".glb" => "GLB",
                ".usd" or ".usda" or ".usdc" or ".usdz" => "USD",
                ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tga" or ".dds" or ".webp" or ".hdr" => "IMAGE",
                _ => "UNKNOWN"
            };
        }
    }

    // =================================================================================
    //  第三部分：InterchangeBaseNode —— IR 节点基类
    //
    //  对齐 UE5 中 UInterchangeBaseNode：所有 IR 节点的公共基类。每个节点拥有
    //  唯一 ID（UniqueId）、显示标签（DisplayLabel）、父节点 UID（ParentUid）、
    //  类型标识与一个元数据字典（Metadata），用于承载格式特定的附加信息。
    //  节点之间的父子关系由 InterchangeBaseNodeContainer 统一维护。
    // =================================================================================

    /// <summary>
    /// IR 节点基类（对齐 UE5 UInterchangeBaseNode）。
    /// 解析阶段产出的所有节点都派生自此类，构成 IR 节点树。
    /// </summary>
    public abstract class InterchangeBaseNode
    {
        /// <summary>节点唯一标识（在容器内全局唯一）。</summary>
        public string UniqueId = string.Empty;
        /// <summary>显示标签（编辑器中展示的名称）。</summary>
        public string DisplayLabel = "Node";
        /// <summary>父节点 UID（根节点为 null/空）。</summary>
        public string? ParentUid;
        /// <summary>节点语义类型。</summary>
        public InterchangeAssetType AssetType;
        /// <summary>子节点 UID 列表（由容器在添加节点时维护）。</summary>
        public List<string> ChildrenUids = new();
        /// <summary>元数据字典（承载格式特定附加信息，如 FBX 的 Layer / glTF 的 extras）。</summary>
        public Dictionary<string, string> Metadata = new(StringComparer.Ordinal);

        /// <summary>写入一条元数据。</summary>
        public void SetMetadata(string key, string value) => Metadata[key] = value;
        /// <summary>读取一条元数据，不存在则返回默认值。</summary>
        public string GetMetadata(string key, string defaultValue = "")
            => Metadata.TryGetValue(key, out var v) ? v : defaultValue;
        /// <summary>尝试读取元数据。</summary>
        public bool TryGetMetadata(string key, out string value) => Metadata.TryGetValue(key, out value!);
    }

    // =================================================================================
    //  第四部分：InterchangeBaseNodeContainer —— 节点容器（IR / 中间表示）
    //
    //  对齐 UE5 中 UInterchangeBaseNodeContainer：解析阶段的产物，是整个管线的
    //  「中间表示」(IR)。它以扁平字典 + 父子链的形式存储所有节点，支持按 UID 随机访问、
    //  按类型枚举、按父子关系遍历。管线与工厂均以此为输入输出。
    // =================================================================================

    /// <summary>
    /// 节点容器：IR 中间表示（对齐 UE5 UInterchangeBaseNodeContainer）。
    /// 解析器 Parse() 的返回值，管线 Execute() 的输入/输出，工厂的输入源。
    /// </summary>
    public sealed class InterchangeBaseNodeContainer
    {
        private readonly Dictionary<string, InterchangeBaseNode> _nodes = new(StringComparer.Ordinal);
        private readonly List<string> _rootUids = new();

        /// <summary>源节点（描述本次导入的源文件信息）。</summary>
        public InterchangeSourceNode Source = new();

        /// <summary>容器内节点总数。</summary>
        public int NodeCount => _nodes.Count;

        /// <summary>所有根节点 UID（只读）。</summary>
        public IReadOnlyList<string> RootUids => _rootUids;

        /// <summary>添加一个节点到容器，并建立父子关系。</summary>
        /// <param name="node">待添加节点（UniqueId 必须非空且唯一）。</param>
        /// <param name="parentUid">父节点 UID；为 null/空则作为根节点。</param>
        public void AddNode(InterchangeBaseNode node, string? parentUid = null)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            if (string.IsNullOrEmpty(node.UniqueId))
                node.UniqueId = Guid.NewGuid().ToString("N");

            _nodes[node.UniqueId] = node;
            node.ParentUid = string.IsNullOrEmpty(parentUid) ? null : parentUid;

            if (node.ParentUid == null)
            {
                if (!_rootUids.Contains(node.UniqueId)) _rootUids.Add(node.UniqueId);
            }
            else if (_nodes.TryGetValue(node.ParentUid, out var parent))
            {
                if (!parent.ChildrenUids.Contains(node.UniqueId))
                    parent.ChildrenUids.Add(node.UniqueId);
            }
        }

        /// <summary>按 UID 获取节点，不存在返回 null。</summary>
        public InterchangeBaseNode? GetNode(string uid)
            => string.IsNullOrEmpty(uid) ? null : (_nodes.TryGetValue(uid, out var n) ? n : null);

        /// <summary>获取所有根节点。</summary>
        public List<InterchangeBaseNode> GetRoots()
            => _rootUids.Select(GetNode).Where(n => n != null).ToList()!;

        /// <summary>获取指定节点的子节点。</summary>
        public List<InterchangeBaseNode> GetChildren(string uid)
        {
            var node = GetNode(uid);
            if (node == null) return new List<InterchangeBaseNode>();
            return node.ChildrenUids.Select(GetNode).Where(n => n != null).ToList()!;
        }

        /// <summary>获取指定类型的所有节点（泛型）。</summary>
        public List<T> GetNodes<T>() where T : InterchangeBaseNode
            => _nodes.Values.OfType<T>().ToList();

        /// <summary>获取指定 InterchangeAssetType 的所有节点。</summary>
        public List<InterchangeBaseNode> GetNodesByType(InterchangeAssetType type)
            => _nodes.Values.Where(n => n.AssetType == type).ToList();

        /// <summary>遍历所有节点（深度优先，从根开始）。</summary>
        public IEnumerable<InterchangeBaseNode> Traverse()
        {
            foreach (var rootUid in _rootUids)
                foreach (var n in Traverse(GetNode(rootUid)))
                    yield return n;
        }

        private IEnumerable<InterchangeBaseNode> Traverse(InterchangeBaseNode? node)
        {
            if (node == null) yield break;
            yield return node;
            foreach (var childUid in node.ChildrenUids)
                foreach (var n in Traverse(GetNode(childUid)))
                    yield return n;
        }

        /// <summary>移除一个节点（同时从父节点的子列表中移除）。</summary>
        public bool RemoveNode(string uid)
        {
            if (!_nodes.TryGetValue(uid, out var node)) return false;
            if (node.ParentUid != null && _nodes.TryGetValue(node.ParentUid, out var parent))
                parent.ChildrenUids.Remove(uid);
            _rootUids.Remove(uid);
            _nodes.Remove(uid);
            return true;
        }
    }

    // =================================================================================
    //  第五部分：派生节点 —— 场景 / 网格 / 材质 / 纹理 / 动画 / 骨骼
    //
    //  对齐 UE5 中各类 UInterchangeXxxNode：每个派生节点承载该类资产在 IR 中的
    //  具体数据。解析器负责填充这些数据，管线负责改写它们，工厂负责消费它们。
    // =================================================================================

    /// <summary>
    /// 场景节点（对齐 UE5 UInterchangeSceneNode）。
    /// 描述变换层级中的一个节点：局部 Translation/Rotation/Scale、几何体引用、父子关系。
    /// </summary>
    public sealed class InterchangeSceneNode : InterchangeBaseNode
    {
        /// <summary>局部平移。</summary>
        public Vector3 Translation = Vector3.Zero;
        /// <summary>局部旋转（四元数）。</summary>
        public Quaternion Rotation = Quaternion.Identity;
        /// <summary>局部缩放。</summary>
        public Vector3 Scale = Vector3.One;
        /// <summary>引用的几何体（网格节点）UID，可为空（纯变换节点）。</summary>
        public string? GeometryUid;
        /// <summary>引用的材质 UID 列表（按槽位顺序，可空）。</summary>
        public List<string> MaterialUids = new();

        /// <summary>构造时设置 AssetType。</summary>
        public InterchangeSceneNode() => AssetType = InterchangeAssetType.SceneNode;

        /// <summary>计算局部变换矩阵（行向量约定 v*M：S*R*T）。</summary>
        public Matrix4x4 LocalMatrix =>
            Matrix4x4.CreateScale(Scale) *
            Matrix4x4.CreateFromQuaternion(Rotation) *
            Matrix4x4.CreateTranslation(Translation);
    }

    /// <summary>
    /// 网格节点（对齐 UE5 UInterchangeMeshNode）。
    /// 承载顶点 / 索引 / 法线 / UV / 切线 / 骨骼绑定等几何数据，引用自几何体。
    /// </summary>
    public sealed class InterchangeMeshNode : InterchangeBaseNode
    {
        /// <summary>顶点位置数组。</summary>
        public List<Vector3> Vertices = new();
        /// <summary>三角形索引数组（每 3 个一组，逆时针为正面）。</summary>
        public List<int> Indices = new();
        /// <summary>法线数组（与顶点一一对应，可为空表示缺失）。</summary>
        public List<Vector3> Normals = new();
        /// <summary>UV0 通道数组（与顶点一一对应，可为空）。</summary>
        public List<Vector2> UV0 = new();
        /// <summary>切线数组（与顶点一一对应，可为空）。</summary>
        public List<Vector4> Tangents = new();
        /// <summary>骨骼绑定：每个顶点的骨骼权重（骨骼名 + 权重，最多 4 影响）。</summary>
        public List<InterchangeBoneWeight> BoneWeights = new();
        /// <summary>引用的骨骼节点 UID（蒙皮时使用）。</summary>
        public string? SkeletonUid;
        /// <summary>该网格使用的材质 UID 列表（按子网格/面组顺序）。</summary>
        public List<string> MaterialUids = new();

        /// <summary>构造时设置 AssetType。</summary>
        public InterchangeMeshNode() => AssetType = InterchangeAssetType.Mesh;

        /// <summary>三角形数量。</summary>
        public int TriangleCount => Indices.Count / 3;
    }

    /// <summary>顶点骨骼权重（最多 4 根骨骼影响）。</summary>
    public readonly struct InterchangeBoneWeight
    {
        /// <summary>骨骼名（对应 SkeletonNode 中的骨骼名）。</summary>
        public readonly string Bone0; public readonly string Bone1; public readonly string Bone2; public readonly string Bone3;
        /// <summary>权重（归一化）。</summary>
        public readonly float W0; public readonly float W1; public readonly float W2; public readonly float W3;
        public InterchangeBoneWeight(string b0, float w0, string b1 = "", float w1 = 0f, string b2 = "", float w2 = 0f, string b3 = "", float w3 = 0f)
        { Bone0 = b0; Bone1 = b1; Bone2 = b2; Bone3 = b3; W0 = w0; W1 = w1; W2 = w2; W3 = w3; }
    }

    /// <summary>
    /// 材质节点（对齐 UE5 UInterchangeMaterialNode + UInterchangeMaterialInstanceNode）。
    /// 承载 PBR 标量属性与纹理引用槽位（BaseColor/Metallic/Roughness/Emissive/Normal/ORM）。
    /// </summary>
    public sealed class InterchangeMaterialNode : InterchangeBaseNode
    {
        /// <summary>基色（线性 RGB）。</summary>
        public Vector3 BaseColor = Vector3.One;
        /// <summary>金属度 [0..1]。</summary>
        public float Metallic = 0f;
        /// <summary>粗糙度 [0..1]。</summary>
        public float Roughness = 0.5f;
        /// <summary>自发光颜色（线性 RGB）。</summary>
        public Vector3 Emissive = Vector3.Zero;
        /// <summary>自发光强度。</summary>
        public float EmissiveIntensity = 0f;
        /// <summary>不透明度 [0..1]。</summary>
        public float Alpha = 1f;
        /// <summary>Alpha 截断阈值。</summary>
        public float AlphaCutoff = 0.5f;
        /// <summary>是否双面。</summary>
        public bool DoubleSided = false;
        /// <summary>法线贴图强度。</summary>
        public float NormalScale = 1f;

        /// <summary>纹理槽位（槽位名 -> 纹理节点 UID）。</summary>
        public Dictionary<string, string> TextureSlots = new(StringComparer.Ordinal);

        /// <summary>构造时设置 AssetType。</summary>
        public InterchangeMaterialNode() => AssetType = InterchangeAssetType.Material;

        /// <summary>标准纹理槽位名（对齐 glTF PBR 与 UE5 材质属性）。</summary>
        public static class Slots
        {
            public const string BaseColor = "BaseColor";
            public const string MetallicRoughness = "MetallicRoughness";
            public const string Normal = "Normal";
            public const string Emissive = "Emissive";
            public const string Occlusion = "Occlusion";
            /// <summary>ORM 打包贴图（Occlusion/Roughness/Metallic 三通道）。</summary>
            public const string ORM = "ORM";
        }
    }

    /// <summary>
    /// 纹理节点（对齐 UE5 UInterchangeTextureNode）。
    /// 承载图像文件路径、像素格式、sRGB 标志与尺寸。
    /// </summary>
    public sealed class InterchangeTextureNode : InterchangeBaseNode
    {
        /// <summary>图像文件路径（可能是源文件相对路径或绝对路径）。</summary>
        public string FilePath = string.Empty;
        /// <summary>像素格式标识（"PNG"/"JPEG"/"TGA"/"DDS"/"WEBP"/"HDR"）。</summary>
        public string Format = string.Empty;
        /// <summary>是否 sRGB 颜色空间（颜色贴图为 true，数据贴图如法线/ORM 为 false）。</summary>
        public bool SRgb = true;
        /// <summary>图像宽度（0 表示未读取，留待纹理工厂填充）。</summary>
        public int Width;
        /// <summary>图像高度。</summary>
        public int Height;

        /// <summary>构造时设置 AssetType。</summary>
        public InterchangeTextureNode() => AssetType = InterchangeAssetType.Texture;
    }

    /// <summary>
    /// 动画轨道：单根骨骼的变换序列（对齐 UE5 UInterchangeAnimationTrack）。
    /// </summary>
    public sealed class InterchangeAnimationTrack
    {
        /// <summary>所属骨骼名。</summary>
        public string BoneName = string.Empty;
        /// <summary>位置关键帧（时间秒, 位移）。</summary>
        public List<(float Time, Vector3 Value)> PositionKeys = new();
        /// <summary>旋转关键帧（时间秒, 四元数）。</summary>
        public List<(float Time, Quaternion Value)> RotationKeys = new();
        /// <summary>缩放关键帧（时间秒, 缩放）。</summary>
        public List<(float Time, Vector3 Value)> ScaleKeys = new();
    }

    /// <summary>
    /// 动画节点（对齐 UE5 UInterchangeAnimationNode）。
    /// 承载轨道列表、时长与帧率。
    /// </summary>
    public sealed class InterchangeAnimationNode : InterchangeBaseNode
    {
        /// <summary>动画轨道列表。</summary>
        public List<InterchangeAnimationTrack> Tracks = new();
        /// <summary>动画时长（秒）。</summary>
        public float Duration = 0f;
        /// <summary>帧率（帧/秒）。</summary>
        public float FrameRate = 30f;

        /// <summary>构造时设置 AssetType。</summary>
        public InterchangeAnimationNode() => AssetType = InterchangeAssetType.Animation;
    }

    /// <summary>
    /// 骨骼节点（对齐 UE5 UInterchangeSkeletonNode）。
    /// 承载骨骼层级与每根骨骼的局部变换。
    /// </summary>
    public sealed class InterchangeSkeletonNode : InterchangeBaseNode
    {
        /// <summary>骨骼列表（按父先子后顺序排列，便于矩阵计算）。</summary>
        public List<InterchangeBone> Bones = new();

        /// <summary>构造时设置 AssetType。</summary>
        public InterchangeSkeletonNode() => AssetType = InterchangeAssetType.Skeleton;
    }

    /// <summary>单根骨骼（对齐 UE5 FInterchangeSkeletonBone）。</summary>
    public sealed class InterchangeBone
    {
        /// <summary>骨骼名。</summary>
        public string Name = string.Empty;
        /// <summary>父骨骼名（根骨骼为空）。</summary>
        public string ParentName = string.Empty;
        /// <summary>局部平移。</summary>
        public Vector3 LocalPosition = Vector3.Zero;
        /// <summary>局部旋转。</summary>
        public Quaternion LocalRotation = Quaternion.Identity;
        /// <summary>局部缩放。</summary>
        public Vector3 LocalScale = Vector3.One;
    }

    // =================================================================================
    //  第六部分：InterchangePipelineBase —— 管线基类与内置管线
    //
    //  对齐 UE5 中 UInterchangePipeline：管线接收 IR 容器，对其节点做处理 / 过滤 /
    //  转换，是「导入选项」的核心承载者。多个管线按顺序组成管线栈，依次执行。
    // =================================================================================

    /// <summary>
    /// 管线基类（对齐 UE5 UInterchangePipeline）。
    /// 派生类重写 Execute()，对 IR 容器做就地处理。
    /// </summary>
    public abstract class InterchangePipelineBase
    {
        /// <summary>管线显示名称。</summary>
        public string Name = "Pipeline";
        /// <summary>是否启用。</summary>
        public bool Enabled = true;

        /// <summary>执行管线：对容器做就地处理。</summary>
        public abstract void Execute(InterchangeBaseNodeContainer container);

        public override string ToString() => $"[{Name}] {(Enabled ? "ON" : "OFF")}";
    }

    /// <summary>
    /// 坐标轴变换管线（对齐 UE5 UInterchangeGenericMeshPipeline 的轴变换部分）。
    /// 根据源节点坐标约定（UpAxis/CoordSystem/UnitScale）与目标约定，计算变换矩阵
    /// 并应用到根场景节点的局部变换上；对没有场景节点包裹的孤立网格节点，则直接烘焙到顶点。
    ///
    /// 原理：一个作用于整个场景的全局刚体变换 R（旋转 + 均匀缩放）只需前乘到「根场景节点」
    /// 的局部变换即可——变换会沿父子层级自然传播，无需逐节点修改。这与 UE5 在根节点施加
    /// 轴变换矩阵的做法一致。
    /// </summary>
    public sealed class AxisTransformPipeline : InterchangePipelineBase
    {
        /// <summary>目标坐标轴朝向（默认 YUp，引擎约定）。</summary>
        public InterchangeUpAxis TargetUpAxis = InterchangeUpAxis.YUp;
        /// <summary>目标单位缩放（1 单位 = TargetUnitScale 米，默认 1）。</summary>
        public float TargetUnitScale = 1.0f;

        /// <summary>构造。</summary>
        public AxisTransformPipeline() => Name = "AxisTransform";

        /// <inheritdoc/>
        public override void Execute(InterchangeBaseNodeContainer container)
        {
            if (container.Source == null) return;
            var src = container.Source;

            // 1) 计算轴旋转矩阵：把源 UpAxis 旋到目标 UpAxis
            Matrix4x4 rot = Matrix4x4.Identity;
            if (src.UpAxis != TargetUpAxis)
            {
                // ZUp -> YUp：绕 X 轴旋转 -90°（使 (x,y,z)->(x,z,-y)）
                // YUp -> ZUp：绕 X 轴旋转 +90°
                float angle = (src.UpAxis == InterchangeUpAxis.ZUp && TargetUpAxis == InterchangeUpAxis.YUp)
                              ? -MathF.PI / 2f
                              : MathF.PI / 2f;
                rot = Matrix4x4.CreateRotationX(angle);
            }

            // 2) 单位缩放系数：源单位 -> 目标单位
            float s = (src.UnitScale > 0f && TargetUnitScale > 0f)
                ? src.UnitScale / TargetUnitScale
                : 1.0f;
            if (MathF.Abs(s - 1f) > 1e-6f)
                rot = Matrix4x4.CreateScale(s) * rot;

            // 若为单位矩阵则无需处理
            if (rot == Matrix4x4.Identity) return;

            // 3) 应用到根场景节点（前乘到局部变换）
            bool anySceneRoot = false;
            foreach (var node in container.GetRoots().ToList())
            {
                if (node is InterchangeSceneNode sn)
                {
                    anySceneRoot = true;
                    // newLocal = rot * oldLocal  （行向量约定：矩阵乘法顺序）
                    Matrix4x4 combined = rot * sn.LocalMatrix;
                    MatrixDecomposer.DecomposeTRS(combined, out var t, out var r, out var sc);
                    sn.Translation = t;
                    sn.Rotation = r;
                    sn.Scale = sc;
                }
            }

            // 4) 对没有场景节点包裹的孤立网格节点，直接烘焙到顶点/法线
            if (!anySceneRoot)
            {
                foreach (var mesh in container.GetNodes<InterchangeMeshNode>())
                    TransformMeshGeometry(mesh, rot);
            }
        }

        /// <summary>把矩阵变换应用到网格顶点与法线。</summary>
        private static void TransformMeshGeometry(InterchangeMeshNode mesh, Matrix4x4 m)
        {
            for (int i = 0; i < mesh.Vertices.Count; i++)
                mesh.Vertices[i] = Vector3.Transform(mesh.Vertices[i], m);
            // 法线用逆转置矩阵变换；对正交旋转 + 均匀缩放，直接用原矩阵即可
            for (int i = 0; i < mesh.Normals.Count; i++)
                mesh.Normals[i] = Vector3.Normalize(Vector3.TransformNormal(mesh.Normals[i], m));
        }
    }

    /// <summary>
    /// 网格处理管线（对齐 UE5 UInterchangeGenericMeshPipeline 的网格处理部分）。
    /// 提供合并重复顶点、生成法线、生成切线、UV 变换等子能力，可按开关启用。
    /// </summary>
    public sealed class MeshPipeline : InterchangePipelineBase
    {
        /// <summary>是否合并重复顶点（位置 + UV + 法线均相同）。</summary>
        public bool MergeDuplicateVertices = true;
        /// <summary>法线缺失时是否生成法线。</summary>
        public bool GenerateNormals = true;
        /// <summary>切线缺失时是否生成切线（基于 UV 的近似 Mikkspace）。</summary>
        public bool GenerateTangents = true;
        /// <summary>UV 平铺。</summary>
        public Vector2 UVTiling = Vector2.One;
        /// <summary>UV 偏移。</summary>
        public Vector2 UVOffset = Vector2.Zero;

        /// <summary>构造。</summary>
        public MeshPipeline() => Name = "MeshProcess";

        /// <inheritdoc/>
        public override void Execute(InterchangeBaseNodeContainer container)
        {
            foreach (var mesh in container.GetNodes<InterchangeMeshNode>())
            {
                if (MergeDuplicateVertices) MergeDuplicates(mesh);
                if (GenerateNormals && mesh.Normals.Count != mesh.Vertices.Count) GenerateMeshNormals(mesh);
                if (GenerateTangents && mesh.Tangents.Count != mesh.Vertices.Count) GenerateMeshTangents(mesh);
                if ((UVTiling != Vector2.One || UVOffset != Vector2.Zero) && mesh.UV0.Count == mesh.Vertices.Count)
                {
                    for (int i = 0; i < mesh.UV0.Count; i++)
                        mesh.UV0[i] = mesh.UV0[i] * UVTiling + UVOffset;
                }
            }
        }

        /// <summary>合并重复顶点（位置 + UV + 法线三元组相同），重映射索引。</summary>
        public static void MergeDuplicates(InterchangeMeshNode mesh)
        {
            var keyMap = new Dictionary<long, int>();
            var newVerts = new List<Vector3>(mesh.Vertices.Count);
            var newNormals = new List<Vector3>(mesh.Normals.Count == mesh.Vertices.Count ? mesh.Vertices.Count : 0);
            var newUVs = new List<Vector2>(mesh.UV0.Count == mesh.Vertices.Count ? mesh.Vertices.Count : 0);
            var remap = new int[mesh.Vertices.Count];
            bool hasN = mesh.Normals.Count == mesh.Vertices.Count;
            bool hasU = mesh.UV0.Count == mesh.Vertices.Count;

            for (int i = 0; i < mesh.Vertices.Count; i++)
            {
                long h = HashCombine(mesh.Vertices[i].GetHashCode(),
                            hasU ? mesh.UV0[i].GetHashCode() : 0,
                            hasN ? mesh.Normals[i].GetHashCode() : 0);
                if (keyMap.TryGetValue(h, out int idx))
                {
                    remap[i] = idx;
                }
                else
                {
                    idx = newVerts.Count;
                    keyMap[h] = idx;
                    newVerts.Add(mesh.Vertices[i]);
                    if (hasN) newNormals.Add(mesh.Normals[i]);
                    if (hasU) newUVs.Add(mesh.UV0[i]);
                    remap[i] = idx;
                }
            }

            mesh.Vertices = newVerts;
            if (hasN) mesh.Normals = newNormals;
            if (hasU) mesh.UV0 = newUVs;
            for (int i = 0; i < mesh.Indices.Count; i++)
                mesh.Indices[i] = remap[mesh.Indices[i]];
        }

        /// <summary>按面法线加权生成顶点法线（对齐 EditableMesh.RecalculateNormals 算法）。</summary>
        public static void GenerateMeshNormals(InterchangeMeshNode mesh)
        {
            var normals = new Vector3[mesh.Vertices.Count];
            for (int i = 0; i + 2 < mesh.Indices.Count; i += 3)
            {
                int i0 = mesh.Indices[i], i1 = mesh.Indices[i + 1], i2 = mesh.Indices[i + 2];
                if (i0 >= mesh.Vertices.Count || i1 >= mesh.Vertices.Count || i2 >= mesh.Vertices.Count) continue;
                var a = mesh.Vertices[i0]; var b = mesh.Vertices[i1]; var c = mesh.Vertices[i2];
                var n = Vector3.Cross(b - a, c - a);
                if (n.LengthSquared() > 1e-8f) n = Vector3.Normalize(n);
                normals[i0] += n; normals[i1] += n; normals[i2] += n;
            }
            for (int i = 0; i < normals.Length; i++)
                normals[i] = normals[i].LengthSquared() > 1e-8f ? Vector3.Normalize(normals[i]) : Vector3.UnitY;
            mesh.Normals = normals.ToList();
        }

        /// <summary>近似切线生成（基于 UV 梯度的 Lengyel 切线）。</summary>
        public static void GenerateMeshTangents(InterchangeMeshNode mesh)
        {
            if (mesh.UV0.Count != mesh.Vertices.Count) return;
            var t1 = new Vector3[mesh.Vertices.Count];
            var t2 = new Vector3[mesh.Vertices.Count];
            for (int i = 0; i + 2 < mesh.Indices.Count; i += 3)
            {
                int i0 = mesh.Indices[i], i1 = mesh.Indices[i + 1], i2 = mesh.Indices[i + 2];
                var v0 = mesh.Vertices[i0]; var v1 = mesh.Vertices[i1]; var v2 = mesh.Vertices[i2];
                var uv0 = mesh.UV0[i0]; var uv1 = mesh.UV0[i1]; var uv2 = mesh.UV0[i2];
                var dp1 = v1 - v0; var dp2 = v2 - v0;
                var duv1 = uv1 - uv0; var duv2 = uv2 - uv0;
                float r = (duv1.X * duv2.Y - duv1.Y * duv2.X);
                r = MathF.Abs(r) < 1e-8f ? 1f : 1f / r;
                var t = (dp1 * duv2.Y - dp2 * duv1.Y) * r;
                t1[i0] += t; t1[i1] += t; t1[i2] += t;
            }
            var tangents = new Vector4[mesh.Vertices.Count];
            for (int i = 0; i < mesh.Vertices.Count; i++)
            {
                var n = mesh.Normals.Count == mesh.Vertices.Count ? mesh.Normals[i] : Vector3.UnitY;
                var t = t1[i];
                t = t - n * Vector3.Dot(n, t);
                if (t.LengthSquared() > 1e-8f) t = Vector3.Normalize(t);
                tangents[i] = new Vector4(t, 1f);
            }
            mesh.Tangents = tangents.ToList();
        }

        private static long HashCombine(long a, long b, long c)
        {
            // 简单哈希组合，足以区分典型顶点
            long h = 0x517CC1B727220A95L;
            h = (h * 31 + a) ^ (h >> 23);
            h = (h * 31 + b) ^ (h >> 23);
            h = (h * 31 + c) ^ (h >> 23);
            return h;
        }
    }

    /// <summary>
    /// 材质处理管线（对齐 UE5 UInterchangeGenericMaterialPipeline）。
    /// 负责纹理路径重定向（相对路径 -> 项目资产路径）、属性默认值填充。
    /// </summary>
    public sealed class MaterialPipeline : InterchangePipelineBase
    {
        /// <summary>源文件所在目录（用于解析相对纹理路径）。</summary>
        public string? SourceDirectory;
        /// <summary>是否把纹理相对路径解析为绝对路径。</summary>
        public bool ResolveTexturePaths = true;

        /// <summary>构造。</summary>
        public MaterialPipeline() => Name = "MaterialProcess";

        /// <inheritdoc/>
        public override void Execute(InterchangeBaseNodeContainer container)
        {
            // 填充材质默认值（确保 PBR 参数在合理区间）
            foreach (var mat in container.GetNodes<InterchangeMaterialNode>())
            {
                mat.Metallic = Math.Clamp(mat.Metallic, 0f, 1f);
                mat.Roughness = Math.Clamp(mat.Roughness, 0f, 1f);
                mat.Alpha = Math.Clamp(mat.Alpha, 0f, 1f);
                mat.BaseColor = Vector3.Clamp(mat.BaseColor, Vector3.Zero, new Vector3(float.MaxValue));
            }

            // 纹理路径重定向
            if (!ResolveTexturePaths) return;
            foreach (var tex in container.GetNodes<InterchangeTextureNode>())
            {
                if (string.IsNullOrEmpty(tex.FilePath)) continue;
                if (Path.IsPathRooted(tex.FilePath)) continue;
                if (!string.IsNullOrEmpty(SourceDirectory))
                {
                    string resolved = Path.GetFullPath(Path.Combine(SourceDirectory, tex.FilePath));
                    tex.FilePath = resolved;
                }
            }
        }
    }

    /// <summary>
    /// 优化管线（对齐 UE5 UInterchangeGenericMeshPipeline 的优化部分）。
    /// 剥离未被引用的节点（网格 / 材质 / 纹理）、合并同材质网格、LOD 简化（占位）。
    /// </summary>
    public sealed class OptimizationPipeline : InterchangePipelineBase
    {
        /// <summary>是否剥离未引用节点。</summary>
        public bool StripUnreferenced = true;
        /// <summary>是否合并同材质网格。</summary>
        public bool MergeSameMaterialMeshes = true;
        /// <summary>LOD 简化比例（0~1，1=不简化，占位实现仅按比例抽稀索引）。</summary>
        public float LODReductionRatio = 1.0f;

        /// <summary>构造。</summary>
        public OptimizationPipeline() => Name = "Optimization";

        /// <inheritdoc/>
        public override void Execute(InterchangeBaseNodeContainer container)
        {
            if (StripUnreferenced) StripUnreferencedNodes(container);
            if (MergeSameMaterialMeshes) MergeSameMaterialMeshNodes(container);
            if (LODReductionRatio < 1f) ApplyLODReduction(container);
        }

        /// <summary>剥离未被任何场景节点引用的网格 / 材质 / 纹理节点。</summary>
        private static void StripUnreferencedNodes(InterchangeBaseNodeContainer container)
        {
            // 收集被引用的 UID 集合
            var referenced = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in container.Traverse())
            {
                if (node is InterchangeSceneNode sn)
                {
                    if (!string.IsNullOrEmpty(sn.GeometryUid)) referenced.Add(sn.GeometryUid);
                    foreach (var m in sn.MaterialUids) referenced.Add(m);
                }
                if (node is InterchangeMeshNode mn)
                {
                    foreach (var m in mn.MaterialUids) referenced.Add(m);
                    if (!string.IsNullOrEmpty(mn.SkeletonUid)) referenced.Add(mn.SkeletonUid);
                }
                if (node is InterchangeMaterialNode mat)
                    foreach (var kvp in mat.TextureSlots) referenced.Add(kvp.Value);
            }

            // 场景节点本身若有父节点则视为被引用；根场景节点保留
            foreach (var node in container.Traverse().ToList())
            {
                if (node is InterchangeSceneNode) { referenced.Add(node.UniqueId); continue; }
            }

            // 移除未被引用的非场景节点
            var toRemove = container.Traverse()
                .Where(n => n.AssetType != InterchangeAssetType.SceneNode && !referenced.Contains(n.UniqueId))
                .Select(n => n.UniqueId).ToList();
            foreach (var uid in toRemove) container.RemoveNode(uid);
        }

        /// <summary>合并引用相同材质的网格节点（仅处理无骨骼绑定的静态网格）。</summary>
        private static void MergeSameMaterialMeshNodes(InterchangeBaseNodeContainer container)
        {
            var meshes = container.GetNodes<InterchangeMeshNode>();
            if (meshes.Count < 2) return;
            // 按「材质签名」分组
            var groups = meshes
                .Where(m => m.BoneWeights.Count == 0)
                .GroupBy(m => string.Join("|", m.MaterialUids));
            foreach (var grp in groups)
            {
                var list = grp.ToList();
                if (list.Count < 2) continue;
                var first = list[0];
                for (int i = 1; i < list.Count; i++)
                {
                    var other = list[i];
                    int offset = first.Vertices.Count;
                    first.Vertices.AddRange(other.Vertices);
                    if (first.Normals.Count == first.Vertices.Count - offset)
                        first.Normals.AddRange(other.Normals);
                    if (first.UV0.Count == first.Vertices.Count - offset)
                        first.UV0.AddRange(other.UV0);
                    foreach (var idx in other.Indices) first.Indices.Add(idx + offset);
                    container.RemoveNode(other.UniqueId);
                    // 把引用了 other 的场景节点改为引用 first
                    foreach (var sn in container.GetNodes<InterchangeSceneNode>())
                        if (sn.GeometryUid == other.UniqueId) sn.GeometryUid = first.UniqueId;
                }
            }
        }

        /// <summary>LOD 简化占位：按比例抽稀索引（非真实网格简化）。</summary>
        private static void ApplyLODReduction(InterchangeBaseNodeContainer container)
        {
            // TODO: 接入真正的网格简化算法（如 Quadric Error Metric / Mesh Decimation）。
            // 当前实现仅作为管线钩子存在，不修改几何，避免破坏拓扑。
        }
    }

    /// <summary>
    /// 烘焙管线（对齐 UE5 中 BakePipelines）：把场景节点的世界变换烘焙到网格顶点，
    /// 使网格变为「已实例化」的世界空间几何，便于导出或运行时直接使用。
    /// </summary>
    public sealed class BakePipeline : InterchangePipelineBase
    {
        /// <summary>构造。</summary>
        public BakePipeline() => Name = "Bake";

        /// <inheritdoc/>
        public override void Execute(InterchangeBaseNodeContainer container)
        {
            // 计算每个场景节点的世界矩阵
            var worldMatrices = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
            foreach (var root in container.GetRoots())
                ComputeWorldMatrices(container, root, Matrix4x4.Identity, worldMatrices);

            // 把引用了网格的场景节点的世界变换烘焙进网格顶点
            foreach (var sn in container.GetNodes<InterchangeSceneNode>())
            {
                if (string.IsNullOrEmpty(sn.GeometryUid)) continue;
                if (!worldMatrices.TryGetValue(sn.UniqueId, out var world)) continue;
                var mesh = container.GetNode(sn.GeometryUid) as InterchangeMeshNode;
                if (mesh == null) continue;

                for (int i = 0; i < mesh.Vertices.Count; i++)
                    mesh.Vertices[i] = Vector3.Transform(mesh.Vertices[i], world);
                if (mesh.Normals.Count == mesh.Vertices.Count)
                {
                    if (Matrix4x4.Invert(world, out var invWorld))
                    {
                        var invTranspose = Matrix4x4.Transpose(invWorld);
                        for (int i = 0; i < mesh.Normals.Count; i++)
                            mesh.Normals[i] = Vector3.Normalize(Vector3.TransformNormal(mesh.Normals[i], invTranspose));
                    }
                }
                // 烘焙后清零场景节点变换
                sn.Translation = Vector3.Zero;
                sn.Rotation = Quaternion.Identity;
                sn.Scale = Vector3.One;
            }
        }

        private static void ComputeWorldMatrices(InterchangeBaseNodeContainer container, InterchangeBaseNode node,
            Matrix4x4 parentWorld, Dictionary<string, Matrix4x4> worldMatrices)
        {
            Matrix4x4 world = parentWorld;
            if (node is InterchangeSceneNode sn) world = sn.LocalMatrix * parentWorld;
            worldMatrices[node.UniqueId] = world;
            foreach (var child in container.GetChildren(node.UniqueId))
                ComputeWorldMatrices(container, child, world, worldMatrices);
        }
    }

    // =================================================================================
    //  第七部分：IInterchangeSourceParser —— 源文件解析器接口与内置解析器
    //
    //  对齐 UE5 中 UInterchangeSourceParser / UInterchangeTranslator：把源文件解析为
    //  IR 节点容器。OBJ 与 glTF 解析器实现基础真实解析逻辑；FBX / USD 为占位实现，
    //  标注 TODO 待接入真正的 SDK。
    // =================================================================================

    /// <summary>
    /// 源文件解析器接口（对齐 UE5 UInterchangeSourceParser）。
    /// Parse() 读取源文件并产出 IR 节点容器。
    /// </summary>
    public interface IInterchangeSourceParser
    {
        /// <summary>该解析器支持的格式标识（"FBX"/"OBJ"/"GLTF"/"GLB"/"USD"/"IMAGE"）。</summary>
        string SupportedFormat { get; }

        /// <summary>解析源文件，产出 IR 节点容器。</summary>
        InterchangeBaseNodeContainer Parse(string filePath);
    }

    /// <summary>
    /// FBX 解析器（自研纯 C# 实现）。
    /// 使用 YamaEngine.Assets.Importers.FbxImporter 解析 ASCII / 二进制 FBX，
    /// 无需外部 FBX SDK。支持 FBX 2010-2020 格式的几何 / 材质 / 骨骼 / 蒙皮解析。
    /// </summary>
    public sealed class FBXSourceParser : IInterchangeSourceParser
    {
        /// <inheritdoc/>
        public string SupportedFormat => "FBX";

        /// <inheritdoc/>
        public InterchangeBaseNodeContainer Parse(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"FBX 源文件不存在: {filePath}");

            // 使用自研纯 C# FBX 导入器
            var importer = new Importers.FbxImporter(filePath);
            var scene = importer.Import();

            // 桥接到 IR 节点容器
            return Importers.InterchangeSceneBridge.ToNodeContainer(scene);
        }
    }

    /// <summary>
    /// USD 解析器（自研纯 C# 实现）。
    /// 使用 YamaEngine.Assets.Importers.UsdImporter 解析 ASCII .usda / 二进制 .usdc，
    /// 无需外部 OpenUSD SDK。支持 Mesh / Material / Xform 解析。
    /// </summary>
    public sealed class USDSourceParser : IInterchangeSourceParser
    {
        /// <inheritdoc/>
        public string SupportedFormat => "USD";

        /// <inheritdoc/>
        public InterchangeBaseNodeContainer Parse(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"USD 源文件不存在: {filePath}");

            // 使用自研纯 C# USD 导入器
            var importer = new Importers.UsdImporter(filePath);
            var scene = importer.Import();

            // 桥接到 IR 节点容器
            return Importers.InterchangeSceneBridge.ToNodeContainer(scene);
        }
    }

    /// <summary>
    /// OBJ 解析器（实现基础真实解析）。
    /// 解析 Wavefront OBJ 的 v / vt / vn / f 行，以及通过 mtllib 引用的 MTL 材质。
    /// 支持多边形面（>3 顶点）的扇形三角化、负索引、按 usemtl 分组多网格。
    /// </summary>
    public sealed class OBJSourceParser : IInterchangeSourceParser
    {
        /// <inheritdoc/>
        public string SupportedFormat => "OBJ";

        /// <inheritdoc/>
        public InterchangeBaseNodeContainer Parse(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"OBJ 源文件不存在: {filePath}");

            var container = new InterchangeBaseNodeContainer();
            container.Source = new InterchangeSourceNode
            {
                FilePath = filePath,
                Format = SupportedFormat,
                UpAxis = InterchangeUpAxis.ZUp,   // OBJ 约定 Z 轴向上
                CoordSystem = InterchangeCoordSystem.RightHanded,
                UnitScale = 1.0f,
                ContentHash = ComputeFileHash(filePath)
            };

            var positions = new List<Vector3>();
            var texcoords = new List<Vector2>();
            var normals = new List<Vector3>();
            var materials = new Dictionary<string, InterchangeMaterialNode>(StringComparer.Ordinal);
            // 当前网格的面缓冲（按材质组分段）：每段含材质名 + 三角化后的顶点列表
            var groups = new List<ObjGroup>();
            ObjGroup? current = null;
            string? pendingMtl = null;

            var lines = File.ReadAllLines(filePath);
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;

                var sp = line.IndexOf(' ');
                if (sp < 0) continue;
                string tag = line.Substring(0, sp);
                string rest = line.Substring(sp + 1).Trim();

                switch (tag)
                {
                    case "v":
                        positions.Add(ParseVec3(rest));
                        break;
                    case "vt":
                        texcoords.Add(ParseVec2(rest));
                        break;
                    case "vn":
                        normals.Add(ParseVec3(rest));
                        break;
                    case "mtllib":
                        LoadMtl(Path.Combine(Path.GetDirectoryName(filePath)!, rest), materials);
                        break;
                    case "usemtl":
                        pendingMtl = rest;
                        if (current != null) { groups.Add(current); current = null; }
                        break;
                    case "g":
                    case "o":
                        if (current != null) { groups.Add(current); current = null; }
                        break;
                    case "f":
                        current ??= new ObjGroup { MaterialName = pendingMtl ?? string.Empty };
                        current.Faces.Add(rest);
                        break;
                }
            }
            if (current != null) groups.Add(current);

            // 构建根场景节点
            var root = new InterchangeSceneNode
            {
                UniqueId = "OBJ_Root",
                DisplayLabel = Path.GetFileNameWithoutExtension(filePath)
            };
            container.AddNode(root);

            // 把每个 group 转成一个 InterchangeMeshNode
            int meshIdx = 0;
            foreach (var grp in groups)
            {
                var meshNode = BuildMeshNode(positions, texcoords, normals, grp, meshIdx);
                container.AddNode(meshNode, root.UniqueId);

                // 关联材质
                if (!string.IsNullOrEmpty(grp.MaterialName) && materials.TryGetValue(grp.MaterialName, out var matNode))
                {
                    container.AddNode(matNode, root.UniqueId);
                    meshNode.MaterialUids.Add(matNode.UniqueId);
                    root.MaterialUids.Add(matNode.UniqueId);
                }

                var meshScene = new InterchangeSceneNode
                {
                    UniqueId = $"OBJ_MeshNode_{meshIdx}",
                    DisplayLabel = meshNode.DisplayLabel,
                    GeometryUid = meshNode.UniqueId
                };
                container.AddNode(meshScene, root.UniqueId);
                meshIdx++;
            }

            return container;
        }

        private sealed class ObjGroup
        {
            public string MaterialName = string.Empty;
            public List<string> Faces = new();
        }

        /// <summary>把一个 OBJ group（面列表）三角化并构建网格节点，按 (v,vt,vn) 去重顶点。</summary>
        private static InterchangeMeshNode BuildMeshNode(List<Vector3> pos, List<Vector2> uv, List<Vector3> norm,
            ObjGroup grp, int idx)
        {
            var mesh = new InterchangeMeshNode
            {
                UniqueId = $"OBJ_Mesh_{idx}",
                DisplayLabel = $"Mesh_{idx}"
            };
            var vertMap = new Dictionary<(int, int, int), int>();
            bool hasN = norm.Count > 0;
            bool hasU = uv.Count > 0;

            foreach (var faceLine in grp.Faces)
            {
                var corners = faceLine.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (corners.Length < 3) continue;
                var triIdx = new List<int>();
                foreach (var corner in corners)
                {
                    int pi = 0, ti = 0, ni = 0;
                    var parts = corner.Split('/');
                    // v / vt / vn，索引 1-based，负数表示相对当前已读数量
                    pi = ResolveIndex(parts[0], pos.Count);
                    if (parts.Length > 1 && parts[1].Length > 0) ti = ResolveIndex(parts[1], uv.Count);
                    if (parts.Length > 2 && parts[2].Length > 0) ni = ResolveIndex(parts[2], norm.Count);

                    var key = (pi, ti, ni);
                    if (!vertMap.TryGetValue(key, out int vIdx))
                    {
                        vIdx = mesh.Vertices.Count;
                        vertMap[key] = vIdx;
                        mesh.Vertices.Add(pi > 0 ? pos[pi - 1] : Vector3.Zero);
                        if (hasU) mesh.UV0.Add(ti > 0 ? uv[ti - 1] : Vector2.Zero);
                        if (hasN) mesh.Normals.Add(ni > 0 ? norm[ni - 1] : Vector3.UnitY);
                    }
                    triIdx.Add(vIdx);
                }

                // 扇形三角化：0,1,2 ; 0,2,3 ; 0,3,4 ...
                for (int i = 1; i + 1 < triIdx.Count; i++)
                {
                    mesh.Indices.Add(triIdx[0]);
                    mesh.Indices.Add(triIdx[i]);
                    mesh.Indices.Add(triIdx[i + 1]);
                }
            }
            return mesh;
        }

        /// <summary>解析 1-based 索引，支持负数（相对）。</summary>
        private static int ResolveIndex(string s, int count)
        {
            if (!int.TryParse(s, out int v)) return 0;
            if (v > 0) return v;
            if (v < 0) return count + v + 1; // 负索引：-1 指向最后一个
            return 0;
        }

        private static Vector3 ParseVec3(string s)
        {
            var p = s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            return new Vector3(
                p.Length > 0 && float.TryParse(p[0], out float x) ? x : 0f,
                p.Length > 1 && float.TryParse(p[1], out float y) ? y : 0f,
                p.Length > 2 && float.TryParse(p[2], out float z) ? z : 0f);
        }

        private static Vector2 ParseVec2(string s)
        {
            var p = s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            return new Vector2(
                p.Length > 0 && float.TryParse(p[0], out float x) ? x : 0f,
                p.Length > 1 && float.TryParse(p[1], out float y) ? y : 0f);
        }

        /// <summary>解析 MTL 材质文件（基础：newmtl/Kd/Ks/Ke/Ns/d/Pr/Pm/map_Kd/map_Bump）。</summary>
        private static void LoadMtl(string mtlPath, Dictionary<string, InterchangeMaterialNode> materials)
        {
            if (!File.Exists(mtlPath)) return;
            InterchangeMaterialNode? current = null;
            foreach (var raw in File.ReadAllLines(mtlPath))
            {
                var line = raw.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;
                var sp = line.IndexOf(' ');
                string tag = sp < 0 ? line : line.Substring(0, sp);
                string rest = sp < 0 ? string.Empty : line.Substring(sp + 1).Trim();

                switch (tag)
                {
                    case "newmtl":
                        current = new InterchangeMaterialNode
                        {
                            UniqueId = "OBJ_Mtl_" + Guid.NewGuid().ToString("N"),
                            DisplayLabel = rest
                        };
                        materials[rest] = current;
                        break;
                    case "Kd" when current != null:
                        current.BaseColor = ParseVec3(rest);
                        break;
                    case "Ke" when current != null:
                        current.Emissive = ParseVec3(rest);
                        break;
                    case "Ns" when current != null:
                        // Ns 为 Phong 高光指数，转换为粗糙度（近似）
                        float ns = float.TryParse(rest, out var nsv) ? nsv : 0f;
                        current.Roughness = MathF.Sqrt(2f / (ns + 2f));
                        break;
                    case "Pr" when current != null:
                        if (float.TryParse(rest, out var pr)) current.Roughness = pr;
                        break;
                    case "Pm" when current != null:
                        if (float.TryParse(rest, out var pm)) current.Metallic = pm;
                        break;
                    case "d" when current != null:
                        if (float.TryParse(rest, out var dv)) current.Alpha = dv;
                        break;
                    case "Tr" when current != null:
                        if (float.TryParse(rest, out var tr)) current.Alpha = 1f - tr;
                        break;
                    case "map_Kd" when current != null:
                        EnsureTextureSlot(current, InterchangeMaterialNode.Slots.BaseColor, rest);
                        break;
                    case "map_Bump" when current != null:
                    case "bump" when current != null:
                        EnsureTextureSlot(current, InterchangeMaterialNode.Slots.Normal, rest);
                        break;
                }
            }
        }

        private static void EnsureTextureSlot(InterchangeMaterialNode mat, string slot, string path)
        {
            // 创建纹理节点并由调用方挂到容器；此处先记录路径在元数据，工厂阶段再实例化
            mat.SetMetadata("tex_" + slot, path);
        }

        private static string ComputeFileHash(string filePath)
        {
            try
            {
                using var sha = System.Security.Cryptography.SHA256.Create();
                using var fs = File.OpenRead(filePath);
                var bytes = sha.ComputeHash(fs);
                var sb = new System.Text.StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
            catch { return string.Empty; }
        }
    }

    /// <summary>
    /// glTF / GLB 解析器（实现基础真实解析）。
    /// 解析 glTF 2.0 的 JSON 描述 + 二进制缓冲，读取 mesh primitive 的
    /// POSITION / NORMAL / TEXCOORD_0 与 indices，构建 IR 网格 / 场景 / 材质节点。
    /// 支持 .gltf（外置 .bin）与 .glb（内嵌二进制）两种形式。
    /// </summary>
    public sealed class GlTFSourceParser : IInterchangeSourceParser
    {
        /// <inheritdoc/>
        public string SupportedFormat => "GLTF";

        /// <inheritdoc/>
        public InterchangeBaseNodeContainer Parse(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"glTF 源文件不存在: {filePath}");

            var container = new InterchangeBaseNodeContainer();
            container.Source = new InterchangeSourceNode
            {
                FilePath = filePath,
                Format = InterchangeSourceNode.FormatFromExtension(filePath),
                UpAxis = InterchangeUpAxis.YUp,          // glTF 约定 Y 轴向上
                CoordSystem = InterchangeCoordSystem.RightHanded,
                UnitScale = 1.0f,                         // glTF 单位为米
                ContentHash = ComputeFileHash(filePath)
            };

            byte[] jsonBytes;
            List<byte[]> buffers = new();
            string dir = Path.GetDirectoryName(filePath)!;

            if (filePath.EndsWith(".glb", StringComparison.OrdinalIgnoreCase))
            {
                // GLB：12 字节头 + JSON chunk + BIN chunk
                var allBytes = File.ReadAllBytes(filePath);
                ParseGlb(allBytes, out jsonBytes, out var binBytes);
                buffers.Add(binBytes);
            }
            else
            {
                jsonBytes = File.ReadAllBytes(filePath);
            }

            using var doc = JsonDocument.Parse(jsonBytes);
            var root = doc.RootElement;

            // 加载 buffers（.gltf 外置 .bin；GLB 的 buffer 0 已是 binBytes）
            if (root.TryGetProperty("buffers", out var buffersEl))
            {
                int bi = 0;
                foreach (var buf in buffersEl.EnumerateArray())
                {
                    if (bi == 0 && filePath.EndsWith(".glb", StringComparison.OrdinalIgnoreCase)) { bi++; continue; }
                    long len = buf.GetProperty("byteLength").GetInt64();
                    byte[] data;
                    if (buf.TryGetProperty("uri", out var uriEl))
                    {
                        string uri = uriEl.GetString()!;
                        if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                            data = DecodeDataUri(uri);
                        else
                            data = File.ReadAllBytes(Path.Combine(dir, uri));
                    }
                    else
                    {
                        data = new byte[len];
                    }
                    buffers.Add(data);
                    bi++;
                }
            }

            // 解析 accessors / bufferViews
            var accessors = root.TryGetProperty("accessors", out var accEl) ? accEl.EnumerateArray().ToList() : new List<JsonElement>();
            var bufferViews = root.TryGetProperty("bufferViews", out var bvEl) ? bvEl.EnumerateArray().ToList() : new List<JsonElement>();

            // 解析材质
            var materialUids = new List<string>();
            if (root.TryGetProperty("materials", out var matEl))
            {
                int mi = 0;
                foreach (var m in matEl.EnumerateArray())
                {
                    var matNode = ParseMaterial(m, mi++);
                    container.AddNode(matNode, null);
                    materialUids.Add(matNode.UniqueId);
                }
            }

            // 解析网格
            var meshNodeUids = new List<List<string>>(); // meshes[i] -> primitive meshNode uids
            if (root.TryGetProperty("meshes", out var meshesEl))
            {
                int meshIdx = 0;
                foreach (var meshEl in meshesEl.EnumerateArray())
                {
                    var primUids = new List<string>();
                    string meshName = meshEl.TryGetProperty("name", out var mn) ? mn.GetString()! : $"Mesh_{meshIdx}";
                    int primIdx = 0;
                    foreach (var prim in meshEl.GetProperty("primitives").EnumerateArray())
                    {
                        var meshNode = ParsePrimitive(prim, accessors, bufferViews, buffers, meshName, meshIdx, primIdx, materialUids);
                        container.AddNode(meshNode, null);
                        primUids.Add(meshNode.UniqueId);
                        primIdx++;
                    }
                    meshNodeUids.Add(primUids);
                    meshIdx++;
                }
            }

            // 解析场景节点层级
            if (root.TryGetProperty("nodes", out var nodesEl))
            {
                var nodeEls = nodesEl.EnumerateArray().ToList();
                var nodeUids = new string[nodeEls.Count];
                for (int i = 0; i < nodeEls.Count; i++)
                {
                    var sn = ParseSceneNode(nodeEls[i], i, meshNodeUids);
                    nodeUids[i] = sn.UniqueId;
                    container.AddNode(sn, null);
                }
                // 建立父子关系
                for (int i = 0; i < nodeEls.Count; i++)
                {
                    if (nodeEls[i].TryGetProperty("children", out var chEl))
                    {
                        foreach (var c in chEl.EnumerateArray())
                        {
                            int ci = c.GetInt32();
                            var childNode = container.GetNode(nodeUids[ci]);
                            if (childNode != null)
                            {
                                container.RemoveNode(childNode.UniqueId);
                                container.AddNode(childNode, nodeUids[i]);
                            }
                        }
                    }
                }

                // 选定默认场景作为根的子树
                if (root.TryGetProperty("scenes", out var scenesEl) && scenesEl.GetArrayLength() > 0)
                {
                    int defaultScene = root.TryGetProperty("scene", out var sc) ? sc.GetInt32() : 0;
                    var scene = scenesEl.EnumerateArray().ToList()[defaultScene];
                    var rootSceneNode = new InterchangeSceneNode
                    {
                        UniqueId = "GLTF_Root",
                        DisplayLabel = "glTF_Scene"
                    };
                    container.AddNode(rootSceneNode, null);
                    if (scene.TryGetProperty("nodes", out var rootNodesEl))
                    {
                        foreach (var rn in rootNodesEl.EnumerateArray())
                        {
                            int ri = rn.GetInt32();
                            var child = container.GetNode(nodeUids[ri]);
                            if (child != null)
                            {
                                container.RemoveNode(child.UniqueId);
                                container.AddNode(child, rootSceneNode.UniqueId);
                            }
                        }
                    }
                }
            }

            return container;
        }

        private void ParseGlb(byte[] allBytes, out byte[] jsonBytes, out byte[] binBytes)
        {
            // 12 字节头：magic(u32) version(u32) length(u32)
            uint magic = BitConverter.ToUInt32(allBytes, 0);
            if (magic != 0x46546C67) // 'glTF'
                throw new InvalidDataException("非法的 GLB magic 标识");
            // 第一个 chunk：JSON
            int offset = 12;
            int chunkLen = (int)BitConverter.ToUInt32(allBytes, offset);
            uint chunkType = BitConverter.ToUInt32(allBytes, offset + 4);
            if (chunkType != 0x4E4F534A) // 'JSON'
                throw new InvalidDataException("GLB 第一个 chunk 必须是 JSON");
            jsonBytes = new byte[chunkLen];
            Array.Copy(allBytes, offset + 8, jsonBytes, 0, chunkLen);
            offset += 8 + chunkLen;

            // 第二个 chunk：BIN（可选）
            binBytes = Array.Empty<byte>();
            if (offset + 8 <= allBytes.Length)
            {
                int binLen = (int)BitConverter.ToUInt32(allBytes, offset);
                uint binType = BitConverter.ToUInt32(allBytes, offset + 4);
                if (binType == 0x004E4942) // 'BIN\0'
                {
                    binBytes = new byte[binLen];
                    Array.Copy(allBytes, offset + 8, binBytes, 0, binLen);
                }
            }
        }

        private InterchangeMaterialNode ParseMaterial(JsonElement m, int idx)
        {
            var mat = new InterchangeMaterialNode
            {
                UniqueId = "GLTF_Mat_" + idx,
                DisplayLabel = m.TryGetProperty("name", out var n) ? n.GetString()! : $"Material_{idx}"
            };
            if (m.TryGetProperty("pbrMetallicRoughness", out var pbr))
            {
                if (pbr.TryGetProperty("baseColorFactor", out var bcf) && bcf.GetArrayLength() >= 3)
                {
                    var arr = bcf.EnumerateArray().ToList();
                    mat.BaseColor = new Vector3(arr[0].GetSingle(), arr[1].GetSingle(), arr[2].GetSingle());
                    if (arr.Count > 3) mat.Alpha = arr[3].GetSingle();
                }
                if (pbr.TryGetProperty("metallicFactor", out var mf)) mat.Metallic = mf.GetSingle();
                if (pbr.TryGetProperty("roughnessFactor", out var rf)) mat.Roughness = rf.GetSingle();
            }
            if (m.TryGetProperty("emissiveFactor", out var ef) && ef.GetArrayLength() >= 3)
            {
                var arr = ef.EnumerateArray().ToList();
                mat.Emissive = new Vector3(arr[0].GetSingle(), arr[1].GetSingle(), arr[2].GetSingle());
                mat.EmissiveIntensity = 1f;
            }
            if (m.TryGetProperty("alphaCutoff", out var ac)) mat.AlphaCutoff = ac.GetSingle();
            if (m.TryGetProperty("doubleSided", out var ds)) mat.DoubleSided = ds.GetBoolean();
            return mat;
        }

        private InterchangeMeshNode ParsePrimitive(JsonElement prim, List<JsonElement> accessors, List<JsonElement> bufferViews,
            List<byte[]> buffers, string meshName, int meshIdx, int primIdx, List<string> materialUids)
        {
            var mesh = new InterchangeMeshNode
            {
                UniqueId = $"GLTF_Mesh_{meshIdx}_{primIdx}",
                DisplayLabel = $"{meshName}_{primIdx}"
            };
            var attrs = prim.GetProperty("attributes");

            // POSITION
            if (attrs.TryGetProperty("POSITION", out var posAcc))
            {
                var pos = ReadFloatAccessor(accessors[posAcc.GetInt32()], bufferViews, buffers, 3);
                for (int i = 0; i + 2 < pos.Length; i += 3)
                    mesh.Vertices.Add(new Vector3(pos[i], pos[i + 1], pos[i + 2]));
            }
            // NORMAL
            if (attrs.TryGetProperty("NORMAL", out var nrmAcc))
            {
                var nrm = ReadFloatAccessor(accessors[nrmAcc.GetInt32()], bufferViews, buffers, 3);
                for (int i = 0; i + 2 < nrm.Length; i += 3)
                    mesh.Normals.Add(new Vector3(nrm[i], nrm[i + 1], nrm[i + 2]));
            }
            // TEXCOORD_0
            if (attrs.TryGetProperty("TEXCOORD_0", out var uvAcc))
            {
                var uv = ReadFloatAccessor(accessors[uvAcc.GetInt32()], bufferViews, buffers, 2);
                for (int i = 0; i + 1 < uv.Length; i += 2)
                    mesh.UV0.Add(new Vector2(uv[i], uv[i + 1]));
            }

            // indices
            if (prim.TryGetProperty("indices", out var idxAcc))
            {
                var idx = ReadIntAccessor(accessors[idxAcc.GetInt32()], bufferViews, buffers);
                mesh.Indices.AddRange(idx);
            }
            else
            {
                // 无索引：顺序顶点
                for (int i = 0; i < mesh.Vertices.Count; i++) mesh.Indices.Add(i);
            }

            // 关联材质
            if (prim.TryGetProperty("material", out var matIdx) && matIdx.GetInt32() < materialUids.Count)
                mesh.MaterialUids.Add(materialUids[matIdx.GetInt32()]);

            return mesh;
        }

        private InterchangeSceneNode ParseSceneNode(JsonElement nodeEl, int idx, List<List<string>> meshNodeUids)
        {
            var sn = new InterchangeSceneNode
            {
                UniqueId = "GLTF_Node_" + idx,
                DisplayLabel = nodeEl.TryGetProperty("name", out var n) ? n.GetString()! : $"Node_{idx}"
            };
            if (nodeEl.TryGetProperty("translation", out var t) && t.GetArrayLength() >= 3)
            {
                var a = t.EnumerateArray().ToList();
                sn.Translation = new Vector3(a[0].GetSingle(), a[1].GetSingle(), a[2].GetSingle());
            }
            if (nodeEl.TryGetProperty("rotation", out var r) && r.GetArrayLength() >= 4)
            {
                var a = r.EnumerateArray().ToList();
                // glTF 旋转为 (x,y,z,w)
                sn.Rotation = new Quaternion(a[0].GetSingle(), a[1].GetSingle(), a[2].GetSingle(), a[3].GetSingle());
            }
            if (nodeEl.TryGetProperty("scale", out var s) && s.GetArrayLength() >= 3)
            {
                var a = s.EnumerateArray().ToList();
                sn.Scale = new Vector3(a[0].GetSingle(), a[1].GetSingle(), a[2].GetSingle());
            }
            if (nodeEl.TryGetProperty("mesh", out var mi))
            {
                int meshIdx = mi.GetInt32();
                if (meshIdx >= 0 && meshIdx < meshNodeUids.Count && meshNodeUids[meshIdx].Count > 0)
                    sn.GeometryUid = meshNodeUids[meshIdx][0]; // 取第一个 primitive 作为代表几何
            }
            return sn;
        }

        /// <summary>读取 accessor 为 float 数组（处理 VEC2/VEC3/VEC4 与 FLOAT/整数分量类型）。</summary>
        private float[] ReadFloatAccessor(JsonElement acc, List<JsonElement> bufferViews, List<byte[]> buffers, int components)
        {
            int accIdx_componentType = acc.GetProperty("componentType").GetInt32();
            int count = acc.GetProperty("count").GetInt32();
            if (!acc.TryGetProperty("bufferView", out var bvProp)) return Array.Empty<float>();
            int bvIdx = bvProp.GetInt32();
            int byteOffset = acc.TryGetProperty("byteOffset", out var bo) ? bo.GetInt32() : 0;
            var bv = bufferViews[bvIdx];
            int bufferIdx = bv.GetProperty("buffer").GetInt32();
            int bvOffset = bv.TryGetProperty("byteOffset", out var bvo) ? bvo.GetInt32() : 0;
            int byteStride = bv.TryGetProperty("byteStride", out var bs) ? bs.GetInt32() : 0;

            byte[] data = buffers[bufferIdx];
            int start = bvOffset + byteOffset;
            int compSize = ComponentSize(accIdx_componentType);
            int elemSize = compSize * components;
            if (byteStride == 0) byteStride = elemSize;
            bool normalized = acc.TryGetProperty("normalized", out var nz) && nz.GetBoolean();

            var result = new float[count * components];
            for (int i = 0; i < count; i++)
            {
                int p = start + i * byteStride;
                for (int c = 0; c < components; c++)
                {
                    result[i * components + c] = ReadComponent(data, p + c * compSize, accIdx_componentType, normalized);
                }
            }
            return result;
        }

        /// <summary>读取 accessor 为 int 索引数组（用于 indices）。</summary>
        private int[] ReadIntAccessor(JsonElement acc, List<JsonElement> bufferViews, List<byte[]> buffers)
        {
            int componentType = acc.GetProperty("componentType").GetInt32();
            int count = acc.GetProperty("count").GetInt32();
            if (!acc.TryGetProperty("bufferView", out var bvProp)) return Array.Empty<int>();
            int bvIdx = bvProp.GetInt32();
            int byteOffset = acc.TryGetProperty("byteOffset", out var bo) ? bo.GetInt32() : 0;
            var bv = bufferViews[bvIdx];
            int bufferIdx = bv.GetProperty("buffer").GetInt32();
            int bvOffset = bv.TryGetProperty("byteOffset", out var bvo) ? bvo.GetInt32() : 0;
            int byteStride = bv.TryGetProperty("byteStride", out var bs) ? bs.GetInt32() : 0;

            byte[] data = buffers[bufferIdx];
            int compSize = ComponentSize(componentType);
            int start = bvOffset + byteOffset;
            if (byteStride == 0) byteStride = compSize;

            var result = new int[count];
            for (int i = 0; i < count; i++)
            {
                int p = start + i * byteStride;
                result[i] = (int)ReadComponent(data, p, componentType, false);
            }
            return result;
        }

        private static int ComponentSize(int componentType) => componentType switch
        {
            5120 => 1, // BYTE
            5121 => 1, // UNSIGNED_BYTE
            5122 => 2, // SHORT
            5123 => 2, // UNSIGNED_SHORT
            5125 => 4, // UNSIGNED_INT
            5126 => 4, // FLOAT
            _ => 4
        };

        private static float ReadComponent(byte[] data, int offset, int componentType, bool normalized)
        {
            switch (componentType)
            {
                case 5126: // FLOAT
                    return BitConverter.ToSingle(data, offset);
                case 5123: // UNSIGNED_SHORT
                    return BitConverter.ToUInt16(data, offset);
                case 5125: // UNSIGNED_INT
                    return BitConverter.ToUInt32(data, offset);
                case 5122: // SHORT
                    short s = BitConverter.ToInt16(data, offset);
                    return normalized ? Math.Max(s / 32767f, -1f) : s;
                case 5121: // UNSIGNED_BYTE
                    byte b = data[offset];
                    return normalized ? b / 255f : b;
                case 5120: // BYTE
                    sbyte sb = (sbyte)data[offset];
                    return normalized ? Math.Max(sb / 127f, -1f) : sb;
                default:
                    return 0f;
            }
        }

        private static byte[] DecodeDataUri(string uri)
        {
            // data:application/octet-stream;base64,xxxx 或 data:image/png;base64,xxxx
            int comma = uri.IndexOf(',');
            if (comma < 0) return Array.Empty<byte>();
            string b64 = uri.Substring(comma + 1);
            return Convert.FromBase64String(b64);
        }

        private static string ComputeFileHash(string filePath)
        {
            try
            {
                using var sha = System.Security.Cryptography.SHA256.Create();
                using var fs = File.OpenRead(filePath);
                var bytes = sha.ComputeHash(fs);
                var sb = new System.Text.StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
            catch { return string.Empty; }
        }
    }

    /// <summary>
    /// 通用图像解析器（对齐 UE5 中纹理导入的元数据读取阶段）。
    /// 读取图片文件头，产出纹理节点（FilePath / Format / sRGB）。尺寸留待纹理工厂填充。
    /// 支持 PNG / JPEG / BMP / TGA / DDS / WEBP 的格式识别。
    /// </summary>
    public sealed class GenericImageParser : IInterchangeSourceParser
    {
        /// <inheritdoc/>
        public string SupportedFormat => "IMAGE";

        /// <inheritdoc/>
        public InterchangeBaseNodeContainer Parse(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"图像源文件不存在: {filePath}");

            var container = new InterchangeBaseNodeContainer();
            container.Source = new InterchangeSourceNode
            {
                FilePath = filePath,
                Format = SupportedFormat,
                ContentHash = ComputeFileHash(filePath)
            };

            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            string fmt = ext switch
            {
                ".png" => "PNG",
                ".jpg" or ".jpeg" => "JPEG",
                ".bmp" => "BMP",
                ".tga" => "TGA",
                ".dds" => "DDS",
                ".webp" => "WEBP",
                ".hdr" => "HDR",
                _ => "UNKNOWN"
            };

            // 数据贴图（法线 / ORM / 金属 / 粗糙 / 高度）默认 Linear，颜色贴图默认 sRGB
            string lowerName = Path.GetFileNameWithoutExtension(filePath).ToLowerInvariant();
            bool srgb = !lowerName.Contains("normal") && !lowerName.Contains("nor") &&
                        !lowerName.Contains("orm") && !lowerName.Contains("metallic") &&
                        !lowerName.Contains("roughness") && !lowerName.Contains("occlusion") &&
                        !lowerName.Contains("height");

            var tex = new InterchangeTextureNode
            {
                UniqueId = "IMG_Tex_" + Guid.NewGuid().ToString("N"),
                DisplayLabel = Path.GetFileNameWithoutExtension(filePath),
                FilePath = filePath,
                Format = fmt,
                SRgb = srgb
            };

            // 尝试读取 PNG/JPEG/BMP 尺寸（简单头解析）
            ReadImageSize(filePath, fmt, out int w, out int h);
            tex.Width = w;
            tex.Height = h;

            container.AddNode(tex, null);
            return container;
        }

        /// <summary>读取 PNG/JPEG/BMP 的宽高（仅解析文件头，避免引入图像库依赖）。</summary>
        private static void ReadImageSize(string path, string fmt, out int w, out int h)
        {
            w = h = 0;
            try
            {
                byte[] head = new byte[32];
                using var fs = File.OpenRead(path);
                int read = fs.Read(head, 0, head.Length);
                if (read < 8) return;
                switch (fmt)
                {
                    case "PNG":
                        // PNG: 宽高在 16..23 字节（大端）
                        if (read >= 24)
                        {
                            w = (head[16] << 24) | (head[17] << 16) | (head[18] << 8) | head[19];
                            h = (head[20] << 24) | (head[21] << 16) | (head[22] << 8) | head[23];
                        }
                        break;
                    case "BMP":
                        // BMP: 宽在 18..21，高在 22..25（小端）
                        if (read >= 26)
                        {
                            w = BitConverter.ToInt32(head, 18);
                            h = Math.Abs(BitConverter.ToInt32(head, 22));
                        }
                        break;
                    case "JPEG":
                        // JPEG 需扫描 SOF 标记，此处仅作占位（尺寸留待纹理工厂读取）
                        break;
                }
            }
            catch { }
        }

        private static string ComputeFileHash(string filePath)
        {
            try
            {
                using var sha = System.Security.Cryptography.SHA256.Create();
                using var fs = File.OpenRead(filePath);
                var bytes = sha.ComputeHash(fs);
                var sb = new System.Text.StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
            catch { return string.Empty; }
        }
    }

    // =================================================================================
    //  第八部分：IInterchangeFactory —— 目标资产工厂接口与内置工厂
    //
    //  对齐 UE5 中 UInterchangeFactory：从 IR 节点生成引擎原生资产，并注册到
    //  AssetDatabase。工厂是 IR 与引擎资产系统之间的「粘合层」。
    // =================================================================================

    /// <summary>
    /// 工厂创建结果：包含生成的引擎资产对象、注册得到的元数据与源 IR 节点。
    /// </summary>
    public sealed class InterchangeFactoryResult
    {
        /// <summary>生成的引擎资产对象（EditableMesh / MaterialAsset / TextureAsset / ...）。</summary>
        public object? Asset;
        /// <summary>注册到 AssetDatabase 得到的元数据。</summary>
        public AssetMetadata? Metadata;
        /// <summary>源 IR 节点。</summary>
        public InterchangeBaseNode? SourceNode;
    }

    /// <summary>
    /// 目标资产工厂接口（对齐 UE5 UInterchangeFactory）。
    /// 从 IR 节点生成引擎资产并注册到 AssetDatabase。
    /// </summary>
    public interface IInterchangeFactory
    {
        /// <summary>该工厂产出的 Interchange 资产类型。</summary>
        InterchangeAssetType TargetType { get; }

        /// <summary>是否能为指定节点创建资产。</summary>
        bool CanCreate(InterchangeBaseNode node);

        /// <summary>从节点创建资产并注册到 AssetDatabase。</summary>
        InterchangeFactoryResult CreateAsset(InterchangeBaseNode node, AssetDatabase database);
    }

    /// <summary>
    /// 网格工厂：InterchangeMeshNode -> EditableMesh + 注册到 AssetDatabase。
    /// 把 IR 网格节点的顶点 / 索引 / 法线 / UV 转换为引擎 EditableMesh，
    /// 并通过 AssetDatabase.RegisterAsset 注册（AssetType.Mesh）。
    /// </summary>
    public sealed class MeshFactory : IInterchangeFactory
    {
        /// <inheritdoc/>
        public InterchangeAssetType TargetType => InterchangeAssetType.Mesh;

        /// <inheritdoc/>
        public bool CanCreate(InterchangeBaseNode node) => node is InterchangeMeshNode;

        /// <inheritdoc/>
        public InterchangeFactoryResult CreateAsset(InterchangeBaseNode node, AssetDatabase database)
        {
            var result = new InterchangeFactoryResult { SourceNode = node };
            if (node is not InterchangeMeshNode meshNode) return result;

            // 把 IR 网格数据映射到引擎 EditableMesh
            var mesh = new EditableMesh { Name = meshNode.DisplayLabel };
            mesh.Vertices.AddRange(meshNode.Vertices);
            if (meshNode.Normals.Count == meshNode.Vertices.Count)
                mesh.Normals.AddRange(meshNode.Normals);
            if (meshNode.UV0.Count == meshNode.Vertices.Count)
                mesh.UV0.AddRange(meshNode.UV0);

            // IR 用索引数组，EditableMesh 用面列表（MeshFace A/B/C）
            for (int i = 0; i + 2 < meshNode.Indices.Count; i += 3)
                mesh.Faces.Add(new MeshFace(meshNode.Indices[i], meshNode.Indices[i + 1], meshNode.Indices[i + 2]));

            // 若法线缺失则重算
            if (mesh.Normals.Count != mesh.Vertices.Count)
                mesh.RecalculateNormals();

            // 骨骼权重映射
            if (meshNode.BoneWeights.Count > 0)
            {
                foreach (var bw in meshNode.BoneWeights)
                    mesh.BoneWeights.Add(new MeshBoneWeight(0, 0, 0, 0, bw.W0, bw.W1, bw.W2, bw.W3));
            }

            // 注册到 AssetDatabase（严格使用已核实 API）
            var guid = Guid.NewGuid();
            string assetPath = $"Assets/Interchange/Mesh/{mesh.Name}_{guid:N}.mesh";
            var meta = database.RegisterAsset(assetPath, guid, AssetType.Mesh, mesh.Name);
            result.Asset = mesh;
            result.Metadata = meta;
            return result;
        }
    }

    /// <summary>
    /// 材质工厂：InterchangeMaterialNode -> MaterialAsset + 注册到 AssetDatabase。
    /// 把 IR 材质节点的 PBR 属性与纹理槽位映射为引擎材质资产（AssetType.Material）。
    /// </summary>
    public sealed class MaterialFactory : IInterchangeFactory
    {
        /// <inheritdoc/>
        public InterchangeAssetType TargetType => InterchangeAssetType.Material;

        /// <inheritdoc/>
        public bool CanCreate(InterchangeBaseNode node) => node is InterchangeMaterialNode;

        /// <inheritdoc/>
        public InterchangeFactoryResult CreateAsset(InterchangeBaseNode node, AssetDatabase database)
        {
            var result = new InterchangeFactoryResult { SourceNode = node };
            if (node is not InterchangeMaterialNode matNode) return result;

            var mat = new MaterialAsset
            {
                Name = matNode.DisplayLabel,
                BaseColor = matNode.BaseColor,
                Metallic = matNode.Metallic,
                Roughness = matNode.Roughness,
                Emissive = matNode.Emissive,
                EmissiveIntensity = matNode.EmissiveIntensity,
                Alpha = matNode.Alpha,
                AlphaCutoff = matNode.AlphaCutoff,
                DoubleSided = matNode.DoubleSided,
                NormalScale = matNode.NormalScale
            };
            foreach (var slot in matNode.TextureSlots)
                mat.TextureSlots[slot.Key] = slot.Value;

            var guid = Guid.NewGuid();
            string assetPath = $"Assets/Interchange/Material/{mat.Name}_{guid:N}.mat";
            var meta = database.RegisterAsset(assetPath, guid, AssetType.Material, mat.Name);
            result.Asset = mat;
            result.Metadata = meta;
            return result;
        }
    }

    /// <summary>
    /// 纹理工厂：InterchangeTextureNode -> TextureAsset + 注册到 AssetDatabase。
    /// 把 IR 纹理节点映射为引擎纹理资产（AssetType.Texture2D）。
    /// </summary>
    public sealed class TextureFactory : IInterchangeFactory
    {
        /// <inheritdoc/>
        public InterchangeAssetType TargetType => InterchangeAssetType.Texture;

        /// <inheritdoc/>
        public bool CanCreate(InterchangeBaseNode node) => node is InterchangeTextureNode;

        /// <inheritdoc/>
        public InterchangeFactoryResult CreateAsset(InterchangeBaseNode node, AssetDatabase database)
        {
            var result = new InterchangeFactoryResult { SourceNode = node };
            if (node is not InterchangeTextureNode texNode) return result;

            // 若纹理文件存在，通过 ImportAsset 让 AssetDatabase 完成文件入库（触发 OnAssetImported）
            AssetMetadata? meta;
            if (!string.IsNullOrEmpty(texNode.FilePath) && File.Exists(texNode.FilePath))
            {
                meta = database.ImportAsset(texNode.FilePath, AssetType.Texture2D);
            }
            else
            {
                var guid = Guid.NewGuid();
                string assetPath = $"Assets/Interchange/Texture/{texNode.DisplayLabel}_{guid:N}.tex";
                meta = database.RegisterAsset(assetPath, guid, AssetType.Texture2D, texNode.DisplayLabel);
            }

            var tex = new TextureAsset
            {
                Name = texNode.DisplayLabel,
                SourcePath = texNode.FilePath,
                Format = texNode.Format,
                SRgb = texNode.SRgb,
                Width = texNode.Width,
                Height = texNode.Height
            };
            result.Asset = tex;
            result.Metadata = meta;
            return result;
        }
    }

    /// <summary>
    /// 动画工厂：InterchangeAnimationNode -> AnimationClipAsset + 注册到 AssetDatabase。
    /// 把 IR 动画节点映射为引擎动画资产（AssetType.Animation）。
    /// </summary>
    public sealed class AnimationFactory : IInterchangeFactory
    {
        /// <inheritdoc/>
        public InterchangeAssetType TargetType => InterchangeAssetType.Animation;

        /// <inheritdoc/>
        public bool CanCreate(InterchangeBaseNode node) => node is InterchangeAnimationNode;

        /// <inheritdoc/>
        public InterchangeFactoryResult CreateAsset(InterchangeBaseNode node, AssetDatabase database)
        {
            var result = new InterchangeFactoryResult { SourceNode = node };
            if (node is not InterchangeAnimationNode animNode) return result;

            var clip = new AnimationClipAsset
            {
                Name = animNode.DisplayLabel,
                Duration = animNode.Duration,
                FrameRate = animNode.FrameRate
            };
            foreach (var track in animNode.Tracks)
            {
                clip.Tracks.Add(new AnimationClipAsset.Track
                {
                    BoneName = track.BoneName,
                    PositionKeys = track.PositionKeys.ToList(),
                    RotationKeys = track.RotationKeys.ToList(),
                    ScaleKeys = track.ScaleKeys.ToList()
                });
            }

            var guid = Guid.NewGuid();
            string assetPath = $"Assets/Interchange/Animation/{clip.Name}_{guid:N}.anim";
            var meta = database.RegisterAsset(assetPath, guid, AssetType.Animation, clip.Name);
            result.Asset = clip;
            result.Metadata = meta;
            return result;
        }
    }

    /// <summary>
    /// 骨骼工厂：InterchangeSkeletonNode -> SkeletonAsset + 注册到 AssetDatabase。
    /// 把 IR 骨骼节点映射为引擎骨骼资产（AssetType.Skeleton）。
    /// </summary>
    public sealed class SkeletonFactory : IInterchangeFactory
    {
        /// <inheritdoc/>
        public InterchangeAssetType TargetType => InterchangeAssetType.Skeleton;

        /// <inheritdoc/>
        public bool CanCreate(InterchangeBaseNode node) => node is InterchangeSkeletonNode;

        /// <inheritdoc/>
        public InterchangeFactoryResult CreateAsset(InterchangeBaseNode node, AssetDatabase database)
        {
            var result = new InterchangeFactoryResult { SourceNode = node };
            if (node is not InterchangeSkeletonNode skelNode) return result;

            var skel = new SkeletonAsset { Name = skelNode.DisplayLabel };
            foreach (var bone in skelNode.Bones)
            {
                skel.Bones.Add(new SkeletonAsset.Bone
                {
                    Name = bone.Name,
                    ParentName = bone.ParentName,
                    LocalPosition = bone.LocalPosition,
                    LocalRotation = bone.LocalRotation,
                    LocalScale = bone.LocalScale
                });
            }

            var guid = Guid.NewGuid();
            string assetPath = $"Assets/Interchange/Skeleton/{skel.Name}_{guid:N}.skel";
            var meta = database.RegisterAsset(assetPath, guid, AssetType.Skeleton, skel.Name);
            result.Asset = skel;
            result.Metadata = meta;
            return result;
        }
    }

    // ---------------------------------------------------------------------------------
    //  Interchange 产出的轻量资产类型（材质 / 纹理 / 动画 / 骨骼）
    //  说明：引擎在 YamaEngine.Materials / YamaEngine.Animation / YamaEngine.Skinning
    //  下拥有完整的 PbrMaterial / AnimationClip / Skeleton 实现；此处定义轻量资产类型，
    //  用于在 Interchange 阶段承载导入结果，避免与上述命名空间产生类型歧义。
    //  工厂返回的 Asset 字段即为这些类型实例，可由下游系统进一步转换为完整引擎资产。
    // ---------------------------------------------------------------------------------

    /// <summary>Interchange 产出的材质资产（PBR 属性 + 纹理槽位）。</summary>
    public sealed class MaterialAsset
    {
        public string Name = "Material";
        public Vector3 BaseColor = Vector3.One;
        public float Metallic = 0f;
        public float Roughness = 0.5f;
        public Vector3 Emissive = Vector3.Zero;
        public float EmissiveIntensity = 0f;
        public float Alpha = 1f;
        public float AlphaCutoff = 0.5f;
        public bool DoubleSided = false;
        public float NormalScale = 1f;
        public Dictionary<string, string> TextureSlots = new(StringComparer.Ordinal);
    }

    /// <summary>Interchange 产出的纹理资产。</summary>
    public sealed class TextureAsset
    {
        public string Name = "Texture";
        public string SourcePath = string.Empty;
        public string Format = string.Empty;
        public bool SRgb = true;
        public int Width;
        public int Height;
    }

    /// <summary>Interchange 产出的动画资产（轨道 + 时长 + 帧率）。</summary>
    public sealed class AnimationClipAsset
    {
        public string Name = "Animation";
        public float Duration = 0f;
        public float FrameRate = 30f;
        public List<Track> Tracks = new();

        public sealed class Track
        {
            public string BoneName = string.Empty;
            public List<(float Time, Vector3 Value)> PositionKeys = new();
            public List<(float Time, Quaternion Value)> RotationKeys = new();
            public List<(float Time, Vector3 Value)> ScaleKeys = new();
        }
    }

    /// <summary>Interchange 产出的骨骼资产。</summary>
    public sealed class SkeletonAsset
    {
        public string Name = "Skeleton";
        public List<Bone> Bones = new();

        public sealed class Bone
        {
            public string Name = string.Empty;
            public string ParentName = string.Empty;
            public Vector3 LocalPosition = Vector3.Zero;
            public Quaternion LocalRotation = Quaternion.Identity;
            public Vector3 LocalScale = Vector3.One;
        }
    }

    // =================================================================================
    //  第九部分：InterchangeJob 与 InterchangeJobManager —— 任务与任务管理器
    //
    //  对齐 UE5 中 UInterchangePipelineStack + 调度逻辑：把「源文件 + 管线列表 + 工厂」
    //  封装为任务，由管理器异步执行（解析 -> 管线 -> 工厂），支持队列、进度、取消、日志。
    // =================================================================================

    /// <summary>任务类型（导入 / 导出）。</summary>
    public enum InterchangeJobType
    {
        /// <summary>导入：源文件 -> 引擎资产。</summary>
        Import,
        /// <summary>导出：引擎资产 -> 源文件。</summary>
        Export
    }

    /// <summary>任务状态。</summary>
    public enum InterchangeJobState
    {
        /// <summary>排队等待。</summary>
        Pending,
        /// <summary>运行中。</summary>
        Running,
        /// <summary>已完成。</summary>
        Completed,
        /// <summary>失败。</summary>
        Failed,
        /// <summary>已取消。</summary>
        Cancelled
    }

    /// <summary>
    /// 导入 / 导出任务（对齐 UE5 一次 Interchange 操作的封装）。
    /// 持有源文件、解析器、管线列表、工厂列表、状态与结果。
    /// </summary>
    public sealed class InterchangeJob
    {
        /// <summary>任务唯一 ID。</summary>
        public Guid Id = Guid.NewGuid();
        /// <summary>任务类型。</summary>
        public InterchangeJobType JobType = InterchangeJobType.Import;
        /// <summary>源文件路径（导入）或目标文件路径（导出）。</summary>
        public string FilePath = string.Empty;
        /// <summary>源文件解析器（导入用）。</summary>
        public IInterchangeSourceParser? Parser;
        /// <summary>管线列表（按顺序执行）。</summary>
        public List<InterchangePipelineBase> Pipelines = new();
        /// <summary>工厂列表（用于从 IR 节点生成资产）。</summary>
        public List<IInterchangeFactory> Factories = new();
        /// <summary>当前状态。</summary>
        public InterchangeJobState State = InterchangeJobState.Pending;
        /// <summary>进度 [0..1]。</summary>
        public float Progress;
        /// <summary>状态/错误消息。</summary>
        public string Message = string.Empty;
        /// <summary>产出的工厂结果列表。</summary>
        public List<InterchangeFactoryResult> Results = new();
        /// <summary>解析得到的 IR 容器（管线处理后的最终 IR）。</summary>
        public InterchangeBaseNodeContainer? Container;
        /// <summary>取消令牌源（管理器持有并触发取消）。</summary>
        internal CancellationTokenSource? Cts;
    }

    /// <summary>
    /// 任务管理器（对齐 UE5 UInterchangeManager 的调度职责）。
    /// 负责创建任务、按格式选择解析器、异步执行「解析 -> 管线 -> 工厂」三段式流水线，
    /// 提供任务队列、进度回调、日志与取消支持。
    /// </summary>
    public sealed class InterchangeJobManager
    {
        private readonly AssetDatabase _database;
        private readonly InterchangeSettings _settings;
        private readonly List<InterchangeJob> _queue = new();
        private readonly object _lock = new();

        /// <summary>已注册的解析器（按格式标识索引）。</summary>
        public Dictionary<string, IInterchangeSourceParser> Parsers { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>已注册的工厂。</summary>
        public List<IInterchangeFactory> Factories { get; } = new();

        /// <summary>进度回调（任务, 进度, 消息）。</summary>
        public event Action<InterchangeJob, float, string>? OnProgress;
        /// <summary>日志回调（任务, 级别, 消息）。</summary>
        public event Action<InterchangeJob, string, string>? OnLog;
        /// <summary>任务完成回调。</summary>
        public event Action<InterchangeJob>? OnJobCompleted;

        /// <summary>是否并行执行队列（false=串行）。</summary>
        public bool ParallelExecution = false;

        /// <summary>构造：注入 AssetDatabase 与设置。</summary>
        public InterchangeJobManager(AssetDatabase database, InterchangeSettings? settings = null)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _settings = settings ?? new InterchangeSettings();
            RegisterDefaultParsers();
            RegisterDefaultFactories();
        }

        /// <summary>注册默认解析器。</summary>
        private void RegisterDefaultParsers()
        {
            Parsers["FBX"] = new FBXSourceParser();
            Parsers["OBJ"] = new OBJSourceParser();
            Parsers["GLTF"] = new GlTFSourceParser();
            Parsers["GLB"] = new GlTFSourceParser();
            Parsers["USD"] = new USDSourceParser();
            Parsers["IMAGE"] = new GenericImageParser();
        }

        /// <summary>注册默认工厂。</summary>
        private void RegisterDefaultFactories()
        {
            Factories.Add(new MeshFactory());
            Factories.Add(new MaterialFactory());
            Factories.Add(new TextureFactory());
            Factories.Add(new AnimationFactory());
            Factories.Add(new SkeletonFactory());
        }

        /// <summary>按格式选择解析器。</summary>
        public IInterchangeSourceParser? SelectParser(string filePath)
        {
            string fmt = InterchangeSourceNode.FormatFromExtension(filePath);
            return Parsers.TryGetValue(fmt, out var p) ? p : null;
        }

        /// <summary>创建导入任务。</summary>
        public InterchangeJob CreateImportJob(string sourceFile, List<InterchangePipelineBase>? pipelines = null)
        {
            var job = new InterchangeJob
            {
                JobType = InterchangeJobType.Import,
                FilePath = sourceFile,
                Parser = SelectParser(sourceFile)
            };
            job.Pipelines.AddRange(pipelines ?? _settings.DefaultPipelines);
            job.Factories.AddRange(Factories);
            return job;
        }

        /// <summary>创建导出任务（占位：导出管线待实现）。</summary>
        public InterchangeJob CreateExportJob(string targetFile, List<InterchangePipelineBase>? pipelines = null)
        {
            var job = new InterchangeJob
            {
                JobType = InterchangeJobType.Export,
                FilePath = targetFile,
                Parser = null
            };
            job.Pipelines.AddRange(pipelines ?? new List<InterchangePipelineBase>());
            job.Factories.AddRange(Factories);
            return job;
        }

        /// <summary>把任务加入队列。</summary>
        public void Enqueue(InterchangeJob job)
        {
            lock (_lock) _queue.Add(job);
        }

        /// <summary>异步执行单个任务（解析 -> 管线 -> 工厂）。</summary>
        public async Task<InterchangeJob> RunJobAsync(InterchangeJob job)
        {
            job.Cts = new CancellationTokenSource();
            var token = job.Cts.Token;
            job.State = InterchangeJobState.Running;
            try
            {
                // ---- 1) 解析 ----
                if (job.JobType == InterchangeJobType.Import)
                {
                    if (job.Parser == null)
                        throw new InvalidOperationException($"未找到适合 {job.FilePath} 的解析器");
                    Log(job, "INFO", $"开始解析: {job.FilePath} ({job.Parser.SupportedFormat})");
                    Report(job, 0.1f, "解析中");
                    job.Container = await Task.Run(() => job.Parser.Parse(job.FilePath), token);
                    token.ThrowIfCancellationRequested();
                    Log(job, "INFO", $"解析完成，IR 节点数: {job.Container.NodeCount}");
                }
                else
                {
                    // 导出：待实现（需要从引擎资产构建 IR 再序列化为目标格式）
                    throw new NotSupportedException("导出管线待实现");
                }

                // ---- 2) 管线处理 ----
                int total = job.Pipelines.Count;
                for (int i = 0; i < total; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var pipe = job.Pipelines[i];
                    if (!pipe.Enabled) continue;
                    float p = 0.1f + 0.5f * (i + 1) / Math.Max(1, total);
                    Report(job, p, $"管线执行: {pipe.Name}");
                    Log(job, "INFO", $"执行管线 [{pipe.Name}]");
                    pipe.Execute(job.Container!);
                }

                // ---- 3) 工厂生成 ----
                Report(job, 0.7f, "资产生成中");
                int nodeIdx = 0;
                var nodes = job.Container!.Traverse().ToList();
                int nodeTotal = Math.Max(1, nodes.Count);
                foreach (var node in nodes)
                {
                    token.ThrowIfCancellationRequested();
                    foreach (var factory in job.Factories)
                    {
                        if (!factory.CanCreate(node)) continue;
                        var r = factory.CreateAsset(node, _database);
                        if (r.Asset != null)
                        {
                            job.Results.Add(r);
                            Log(job, "INFO", $"创建资产: {r.Asset.GetType().Name} -> {r.Metadata?.AssetPath}");
                        }
                        break;
                    }
                    nodeIdx++;
                    Report(job, 0.7f + 0.3f * nodeIdx / nodeTotal, $"生成资产 {nodeIdx}/{nodeTotal}");
                }

                job.State = InterchangeJobState.Completed;
                job.Progress = 1f;
                job.Message = $"完成，生成 {job.Results.Count} 个资产";
                Log(job, "INFO", job.Message);
            }
            catch (OperationCanceledException)
            {
                job.State = InterchangeJobState.Cancelled;
                job.Message = "任务已取消";
                Log(job, "WARN", job.Message);
            }
            catch (Exception ex)
            {
                job.State = InterchangeJobState.Failed;
                job.Message = ex.Message;
                Log(job, "ERROR", $"任务失败: {ex.Message}");
            }
            finally
            {
                OnJobCompleted?.Invoke(job);
            }
            return job;
        }

        /// <summary>异步执行队列中所有任务（按 ParallelExecution 决定串行/并行）。</summary>
        public async Task RunQueueAsync()
        {
            List<InterchangeJob> snapshot;
            lock (_lock) { snapshot = _queue.ToList(); _queue.Clear(); }

            if (ParallelExecution)
                await Task.WhenAll(snapshot.Select(RunJobAsync));
            else
            {
                foreach (var job in snapshot)
                    await RunJobAsync(job);
            }
        }

        /// <summary>取消指定任务。</summary>
        public void Cancel(InterchangeJob job)
        {
            try { job.Cts?.Cancel(); } catch { }
        }

        /// <summary>取消队列中所有任务。</summary>
        public void CancelAll()
        {
            lock (_lock)
            {
                foreach (var job in _queue) try { job.Cts?.Cancel(); } catch { }
            }
        }

        private void Report(InterchangeJob job, float progress, string message)
        {
            job.Progress = progress;
            job.Message = message;
            OnProgress?.Invoke(job, progress, message);
        }

        private void Log(InterchangeJob job, string level, string message)
        {
            OnLog?.Invoke(job, level, message);
        }
    }

    // =================================================================================
    //  第十部分：InterchangeSettings —— 全局设置
    //
    //  对齐 UE5 中 Interchange Project Settings：默认管线顺序、自动导入目录、
    //  文件扩展名与解析器关联表、坐标轴 / 单位默认值。
    // =================================================================================

    /// <summary>
    /// Interchange 全局设置（对齐 UE5 Interchange Project Settings）。
    /// </summary>
    public sealed class InterchangeSettings
    {
        /// <summary>默认导入管线顺序（按列表顺序执行）。</summary>
        public List<InterchangePipelineBase> DefaultPipelines = new()
        {
            new AxisTransformPipeline(),
            new MeshPipeline(),
            new MaterialPipeline(),
            new OptimizationPipeline(),
            new BakePipeline()
        };

        /// <summary>自动监视导入的目录列表（变化时触发重导入）。</summary>
        public List<string> AutoImportDirectories = new();

        /// <summary>文件扩展名 -> 解析器格式标识关联表。</summary>
        public Dictionary<string, string> FileAssociation = new(StringComparer.OrdinalIgnoreCase)
        {
            [".fbx"] = "FBX",
            [".obj"] = "OBJ",
            [".gltf"] = "GLTF",
            [".glb"] = "GLB",
            [".usd"] = "USD",
            [".usda"] = "USD",
            [".usdc"] = "USD",
            [".usdz"] = "USD",
            [".png"] = "IMAGE",
            [".jpg"] = "IMAGE",
            [".jpeg"] = "IMAGE",
            [".bmp"] = "IMAGE",
            [".tga"] = "IMAGE",
            [".dds"] = "IMAGE",
            [".webp"] = "IMAGE"
        };

        /// <summary>默认目标坐标轴朝向。</summary>
        public InterchangeUpAxis DefaultTargetUpAxis = InterchangeUpAxis.YUp;
        /// <summary>默认目标单位缩放（1 单位 = 该值米）。</summary>
        public float DefaultTargetUnitScale = 1.0f;
        /// <summary>是否在导入后自动烘焙场景变换到网格。</summary>
        public bool BakeTransformsOnImport = false;
        /// <summary>是否剥离未引用节点。</summary>
        public bool StripUnreferencedNodes = true;

        /// <summary>根据文件路径返回关联的解析器格式标识。</summary>
        public string GetFormatForFile(string filePath)
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            return FileAssociation.TryGetValue(ext, out var fmt) ? fmt : "UNKNOWN";
        }
    }

    // =================================================================================
    //  第十一部分：InterchangeTranslator —— 导入结果翻译为场景对象树
    //
    //  对齐 UE5 中把 IR 节点树翻译为场景层级供编辑器使用的逻辑。
    //  把 InterchangeSceneNode 层级翻译为引擎 SceneObject 树，挂上网格 / 材质引用。
    // =================================================================================

    /// <summary>
    /// 翻译器：把 IR 节点容器翻译为 SceneObject 树，供场景编辑器使用。
    /// </summary>
    public sealed class InterchangeTranslator
    {
        private readonly AssetDatabase _database;

        /// <summary>构造：注入 AssetDatabase（用于查找已注册资产路径）。</summary>
        public InterchangeTranslator(AssetDatabase database)
        {
            _database = database;
        }

        /// <summary>把 IR 容器翻译为场景对象树根节点列表。</summary>
        public List<SceneObject> Translate(InterchangeBaseNodeContainer container)
        {
            var roots = new List<SceneObject>();
            var map = new Dictionary<string, SceneObject>(StringComparer.Ordinal);

            // 第一遍：为所有场景节点创建 SceneObject
            foreach (var node in container.GetNodes<InterchangeSceneNode>())
            {
                var so = new SceneObject
                {
                    Name = node.DisplayLabel,
                    Position = node.Translation,
                    Rotation = node.Rotation,
                    Scale = node.Scale
                };
                // 关联网格资产
                if (!string.IsNullOrEmpty(node.GeometryUid))
                {
                    var mesh = container.GetNode(node.GeometryUid) as InterchangeMeshNode;
                    if (mesh != null) so.MeshAssetPath = mesh.DisplayLabel;
                }
                // 关联材质（取第一个）
                if (node.MaterialUids.Count > 0)
                {
                    var mat = container.GetNode(node.MaterialUids[0]) as InterchangeMaterialNode;
                    if (mat != null)
                    {
                        so.MaterialAssetPath = mat.DisplayLabel;
                        so.BaseColor = mat.BaseColor;
                        so.Metallic = mat.Metallic;
                        so.Roughness = mat.Roughness;
                    }
                }
                map[node.UniqueId] = so;
            }

            // 第二遍：建立父子关系
            foreach (var node in container.GetNodes<InterchangeSceneNode>())
            {
                var so = map[node.UniqueId];
                if (node.ParentUid != null && map.TryGetValue(node.ParentUid, out var parentSo))
                {
                    parentSo.Children.Add(so);
                    so.Parent = parentSo;
                }
                else
                {
                    roots.Add(so);
                }
            }

            return roots;
        }
    }

    // =================================================================================
    //  第十二部分：InterchangeDebugger —— 调试（IR 节点树可视化）
    //
    //  对齐 UE5 Interchange Debugger：返回 IR 节点树的文本结构，供编辑器可视化。
    // =================================================================================

    /// <summary>
    /// 调试器：输出 IR 节点树结构供编辑器可视化。
    /// </summary>
    public static class InterchangeDebugger
    {
        /// <summary>把容器的节点树转为带缩进的文本。</summary>
        public static string DumpNodeTree(InterchangeBaseNodeContainer container)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"=== Interchange IR Dump ({container.NodeCount} nodes) ===");
            var source = container.Source;
            sb.AppendLine($"Source: {source.FilePath} | Format: {source.Format} | Up: {source.UpAxis} | Unit: {source.UnitScale}");
            sb.AppendLine($"Hash: {source.ContentHash}");
            sb.AppendLine();
            foreach (var root in container.GetRoots())
                DumpNode(container, root, 0, sb);
            return sb.ToString();
        }

        private static void DumpNode(InterchangeBaseNodeContainer container, InterchangeBaseNode node, int depth, System.Text.StringBuilder sb)
        {
            string indent = new string(' ', depth * 2);
            sb.AppendLine($"{indent}- [{node.AsTypeIdentifier()}] {node.DisplayLabel} (uid={node.UniqueId})");
            DumpNodeDetails(node, indent + "    ", sb);
            foreach (var child in container.GetChildren(node.UniqueId))
                DumpNode(container, child, depth + 1, sb);
        }

        private static void DumpNodeDetails(InterchangeBaseNode node, string indent, System.Text.StringBuilder sb)
        {
            switch (node)
            {
                case InterchangeSceneNode sn:
                    sb.AppendLine($"{indent}T={sn.Translation} R={sn.Rotation} S={sn.Scale} Geo={sn.GeometryUid ?? "-"}");
                    break;
                case InterchangeMeshNode mn:
                    sb.AppendLine($"{indent}Verts={mn.Vertices.Count} Tris={mn.TriangleCount} Normals={mn.Normals.Count} UVs={mn.UV0.Count} Bones={mn.BoneWeights.Count}");
                    break;
                case InterchangeMaterialNode mat:
                    sb.AppendLine($"{indent}BaseColor={mat.BaseColor} Metallic={mat.Metallic} Roughness={mat.Roughness} TexSlots={mat.TextureSlots.Count}");
                    break;
                case InterchangeTextureNode tex:
                    sb.AppendLine($"{indent}Path={tex.FilePath} Format={tex.Format} sRGB={tex.SRgb} {tex.Width}x{tex.Height}");
                    break;
                case InterchangeAnimationNode anim:
                    sb.AppendLine($"{indent}Duration={anim.Duration}s FPS={anim.FrameRate} Tracks={anim.Tracks.Count}");
                    break;
                case InterchangeSkeletonNode skel:
                    sb.AppendLine($"{indent}Bones={skel.Bones.Count}");
                    break;
            }
            if (node.Metadata.Count > 0)
                sb.AppendLine($"{indent}Metadata: {string.Join(", ", node.Metadata.Select(kv => $"{kv.Key}={kv.Value}"))}");
        }
    }

    // =================================================================================
    //  辅助工具：TRS 矩阵分解
    // =================================================================================

    /// <summary>矩阵工具：把 TRS 矩阵分解回平移 / 旋转 / 缩放。</summary>
    internal static class MatrixDecomposer
    {
        /// <summary>分解 4x4 矩阵为 Translation / Rotation / Scale（假设无负缩放镜像）。</summary>
        public static void DecomposeTRS(Matrix4x4 m, out Vector3 translation, out Quaternion rotation, out Vector3 scale)
        {
            translation = m.Translation;
            // 提取行向量作为基轴（System.Numerics 为行主序，行向量约定 v*M）
            var r1 = new Vector3(m.M11, m.M12, m.M13);
            var r2 = new Vector3(m.M21, m.M22, m.M23);
            var r3 = new Vector3(m.M31, m.M32, m.M33);
            float sx = r1.Length();
            float sy = r2.Length();
            float sz = r3.Length();
            scale = new Vector3(
                sx > 1e-6f ? sx : 0f,
                sy > 1e-6f ? sy : 0f,
                sz > 1e-6f ? sz : 0f);

            // 去除缩放得到纯旋转矩阵
            if (sx > 1e-6f) r1 /= sx;
            if (sy > 1e-6f) r2 /= sy;
            if (sz > 1e-6f) r3 /= sz;
            var rot = new Matrix4x4(
                r1.X, r1.Y, r1.Z, 0,
                r2.X, r2.Y, r2.Z, 0,
                r3.X, r3.Y, r3.Z, 0,
                0, 0, 0, 1);
            rotation = Quaternion.CreateFromRotationMatrix(rot);
        }
    }

    /// <summary>InterchangeBaseNode 的扩展方法（类型标识字符串，供调试输出）。</summary>
    internal static class InterchangeNodeExtensions
    {
        public static string AsTypeIdentifier(this InterchangeBaseNode node) => node.AssetType switch
        {
            InterchangeAssetType.Mesh => "Mesh",
            InterchangeAssetType.Animation => "Anim",
            InterchangeAssetType.Material => "Mat",
            InterchangeAssetType.Texture => "Tex",
            InterchangeAssetType.Skeleton => "Skel",
            InterchangeAssetType.Camera => "Cam",
            InterchangeAssetType.Light => "Light",
            InterchangeAssetType.SceneNode => "Scene",
            InterchangeAssetType.Audio => "Audio",
            _ => "Node"
        };
    }
}
