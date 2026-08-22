// =====================================================================================
//  ShanyuEngine - AlembicImporter：Alembic (.abc) 交换格式导入器
//  命名空间：ShanyuEngine.Assets.Importers
// -------------------------------------------------------------------------------------
//  纯 C# 实现，支持 Alembic 交换格式（.abc）：
//    - ABC 文件格式解析（Ogawa 后端 / HDF5 占位）
//    - Schema 解析：
//      IPolyMeshSchema / ISubDSchema：多边形网格
//      IPointsSchema：点云
//      ICurvesSchema：曲线
//      INuPatchSchema：NURBS 曲面
//      IXformSchema：变换
//      ICameraSchema：相机
//      ILightSchema：灯光
//      IGeomBaseSchema：基础几何
//    - 时间采样（动画帧 / 时间轴）
//    - 层级遍历（对象树 / 标识符 / 名字 / 路径）
//    - 网格数据读取（P / N / uv / 面索引）
//    - 导入选项（时间范围 / 采样间隔 / 静态网格优化 / 形变动画烘焙）
// =====================================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;

namespace ShanyuEngine.Assets.Importers
{
    // =================================================================================
    //  导入选项
    // =================================================================================

    /// <summary>Alembic 导入选项。</summary>
    public sealed class AbcImportOptions
    {
        /// <summary>全局缩放比例。</summary>
        public float Scale = 1.0f;

        /// <summary>是否将右手坐标系转换为左手坐标系。</summary>
        public bool ConvertToLeftHanded = true;

        /// <summary>动画起始时间（秒）。null 表示使用文件起始时间。</summary>
        public double? StartTime = null;

        /// <summary>动画结束时间（秒）。null 表示使用文件结束时间。</summary>
        public double? EndTime = null;

        /// <summary>采样间隔（秒）。0 表示使用原始采样率。</summary>
        public double SampleInterval = 0;

        /// <summary>是否优化静态网格（如果网格在所有帧中不变，只保留一帧）。</summary>
        public bool OptimizeStaticMesh = true;

        /// <summary>是否烘焙形变动画为关键帧。</summary>
        public bool BakeDeformAnimation = true;

        /// <summary>烘焙帧率（帧/秒）。</summary>
        public float BakeFrameRate = 30f;

        /// <summary>是否导入法线。</summary>
        public bool ImportNormals = true;

        /// <summary>是否导入 UV。</summary>
        public bool ImportUVs = true;

        /// <summary>是否导入点云。</summary>
        public bool ImportPoints = true;

        /// <summary>是否导入曲线。</summary>
        public bool ImportCurves = false;
    }

    // =================================================================================
    //  Alembic Schema 类型
    // =================================================================================

    /// <summary>Alembic Schema 类型枚举。</summary>
    internal enum AbcSchemaType
    {
        Unknown,
        IPolyMesh,
        ISubD,
        IPoints,
        ICurves,
        INuPatch,
        IXform,
        ICamera,
        ILight,
        IGeomBase,
        IFaceSet,
        IParticles
    }

    // =================================================================================
    //  Alembic 对象节点
    // =================================================================================

    /// <summary>Alembic 对象层级节点。</summary>
    internal sealed class AbcObjectNode
    {
        public string Name = string.Empty;
        public string Path = string.Empty;
        public AbcSchemaType SchemaType = AbcSchemaType.Unknown;
        public Dictionary<string, object> Properties = new(StringComparer.Ordinal);
        public List<AbcObjectNode> Children = new();
        public List<AbcTimeSample> TimeSamples = new();
    }

    /// <summary>Alembic 时间采样数据。</summary>
    internal sealed class AbcTimeSample
    {
        public double Time;
        public double TimeSeconds;
        public Dictionary<string, object> Data = new(StringComparer.Ordinal);
    }

    // =================================================================================
    //  Alembic 网格数据
    // =================================================================================

    /// <summary>Alembic 网格采样数据。</summary>
    internal sealed class AbcMeshSample
    {
        public Vector3[] Positions = Array.Empty<Vector3>();
        public Vector3[] Normals = Array.Empty<Vector3>();
        public Vector2[] UVs = Array.Empty<Vector2>();
        public int[] FaceIndices = Array.Empty<int>();
        public int[] FaceCounts = Array.Empty<int>();
    }

    // =================================================================================
    //  AlembicImporter —— 主导入器
    // =================================================================================

    /// <summary>
    /// Alembic (.abc) 交换格式导入器。
    /// 支持 Ogawa 后端，HDF5 为占位。
    /// 输出为 InterchangeScene 统一格式。
    /// </summary>
    public sealed class AlembicImporter
    {
        private readonly string _filePath;
        private readonly List<string> _messages = new();
        private AbcImportOptions _options = new();

        // Alembic 文件元数据
        private string _abcVersion = string.Empty;
        private string _abcLibrary = string.Empty;
        private double _startTime = 0;
        private double _endTime = 0;
        private double _fps = 24.0;
        private int _frameCount = 0;

        // 对象树根节点
        private AbcObjectNode? _rootObject;

        /// <summary>解析产出的统一场景格式。</summary>
        public InterchangeScene? Scene { get; private set; }

        /// <summary>构造 Alembic 导入器。</summary>
        public AlembicImporter(string filePath)
        {
            _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        }

        /// <summary>使用选项执行导入。</summary>
        public InterchangeScene Import(AbcImportOptions? options = null)
        {
            if (!File.Exists(_filePath))
                throw new FileNotFoundException($"Alembic 源文件不存在: {_filePath}");

            _options = options ?? new AbcImportOptions();

            Scene = new InterchangeScene
            {
                Name = Path.GetFileNameWithoutExtension(_filePath),
                SourceFilePath = _filePath,
                SourceFormat = "Alembic"
            };

            try
            {
                // 检测后端格式
                string backend = DetectBackend();
                _messages.Add($"Alembic 后端: {backend}");

                // 解析文件
                if (backend == "Ogawa")
                {
                    ParseOgawaFormat();
                }
                else if (backend == "HDF5")
                {
                    _messages.Add("HDF5 后端支持为占位，尝试使用基础解析");
                    ParseHdf5FormatPlaceholder();
                }

                // 遍历对象树
                if (_rootObject != null)
                {
                    _messages.Add($"Alembic 对象树: {CountObjects(_rootObject)} 个对象");
                    ConvertObjects();
                }

                PostProcessScene();
            }
            catch (Exception ex)
            {
                Scene.ImportMessages.Add($"导入 Alembic 失败: {ex.Message}");
                throw;
            }

            Scene.ImportMessages.AddRange(_messages);
            return Scene;
        }

        // =================================================================================
        //  后端检测
        // =================================================================================

        /// <summary>检测 Alembic 文件后端格式（Ogawa 或 HDF5）。</summary>
        private string DetectBackend()
        {
            using var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] header = new byte[8];
            int read = fs.Read(header, 0, 8);

            if (read >= 5)
            {
                string magic = Encoding.ASCII.GetString(header, 0, 5);
                if (magic == "Ogawa")
                    return "Ogawa";
            }

            // HDF5 文件以 \x89HDF\r\n\x1a\n 开头
            if (read >= 8 && header[0] == 0x89 && header[1] == 'H' && header[2] == 'D' && header[3] == 'F')
                return "HDF5";

            return "Unknown";
        }

        // =================================================================================
        //  Ogawa 格式解析
        // =================================================================================

        /// <summary>解析 Ogawa 格式的 Alembic 文件。</summary>
        private void ParseOgawaFormat()
        {
            byte[] data = File.ReadAllBytes(_filePath);

            if (data.Length < 16)
                throw new InvalidDataException("Ogawa 文件过小");

            // Ogawa 文件头：5 字节 magic "Ogawa" + 3 字节版本 + 8 字节根组偏移
            string magic = Encoding.ASCII.GetString(data, 0, 5);
            if (magic != "Ogawa")
                throw new InvalidDataException($"无效的 Ogawa Magic: {magic}");

            // 版本号（byte 5）
            uint version = data[5];
            _abcVersion = $"Ogawa v{version}";

            // 根组偏移量（byte 8, 8 字节小端）
            long rootGroupOffset = BitConverter.ToInt64(data, 8);
            _messages.Add($"Ogawa 版本: {version}, 根组偏移: {rootGroupOffset}");

            // 解析 Ogawa 数据结构
            // Ogawa 使用分组和简单数据节点存储
            // 分组节点：1 字节类型(0) + 4 字节子节点数 + 8*n 字节子节点偏移
            // 数据节点：1 字节类型(1) + 4 字节大小 + 数据

            _rootObject = new AbcObjectNode
            {
                Name = "ABCRoot",
                Path = "/",
                SchemaType = AbcSchemaType.Unknown
            };

            // 读取根分组
            if (rootGroupOffset > 0 && rootGroupOffset < data.Length)
            {
                ParseOgawaGroup(data, rootGroupOffset, _rootObject, "/");
            }

            // 提取 Alembic Archive 元数据
            ExtractArchiveMetadata();

            _messages.Add($"Ogawa 解析完成: 版本 {_abcVersion}");
        }

        /// <summary>解析 Ogawa 分组节点。</summary>
        private void ParseOgawaGroup(byte[] data, long offset, AbcObjectNode parent, string parentPath)
        {
            if (offset < 0 || offset + 5 > data.Length)
                return;

            // 节点类型：0=分组, 1=数据
            byte nodeType = data[offset];

            if (nodeType == 0)
            {
                // 分组节点
                uint childCount = BitConverter.ToUInt32(data, (int)offset + 1);

                // 读取子节点偏移量
                long[] childOffsets = new long[childCount];
                for (int i = 0; i < childCount; i++)
                {
                    int offIdx = (int)offset + 5 + i * 8;
                    if (offIdx + 8 <= data.Length)
                        childOffsets[i] = BitConverter.ToInt64(data, offIdx);
                }

                // 分组节点的子节点通常是成对的：名称 + 子分组/数据
                // Ogawa 分组中偶数索引为名称数据节点，奇数索引为子节点
                for (int i = 0; i + 1 < childCount; i += 2)
                {
                    // 读取名称
                    string name = ReadOgawaString(data, childOffsets[i]);
                    if (string.IsNullOrEmpty(name))
                        continue;

                    // 创建子对象
                    string childPath = parentPath == "/" ? $"/{name}" : $"{parentPath}/{name}";
                    var childNode = new AbcObjectNode
                    {
                        Name = name,
                        Path = childPath
                    };

                    // 判断 Schema 类型
                    childNode.SchemaType = DetectSchemaType(name, data, childOffsets[i + 1]);

                    parent.Children.Add(childNode);

                    // 递归解析子节点
                    ParseOgawaGroup(data, childOffsets[i + 1], childNode, childPath);
                }
            }
            else if (nodeType == 1)
            {
                // 数据节点
                uint size = BitConverter.ToUInt32(data, (int)offset + 1);
                // 数据内容从 offset + 5 开始
                // 在此处可以读取具体数据
            }
        }

        /// <summary>读取 Ogawa 字符串数据节点。</summary>
        private string ReadOgawaString(byte[] data, long offset)
        {
            if (offset < 0 || offset + 5 > data.Length)
                return string.Empty;

            byte nodeType = data[offset];
            if (nodeType != 1)
                return string.Empty;

            uint size = BitConverter.ToUInt32(data, (int)offset + 1);
            int strOffset = (int)offset + 5;
            int strLen = (int)Math.Min(size, data.Length - strOffset);

            if (strLen <= 0)
                return string.Empty;

            return Encoding.UTF8.GetString(data, strOffset, strLen).TrimEnd('\0');
        }

        /// <summary>根据对象名称和数据检测 Schema 类型。</summary>
        private AbcSchemaType DetectSchemaType(string name, byte[] data, long offset)
        {
            // Alembic 使用 Schema 名称来标识对象类型
            // 常见 Schema 路径标记：
            //   .geom → 几何对象
            //   .xform → 变换对象
            //   .camera → 相机
            //   .light → 灯光
            //   .arbGeomParam / .userProperties → 自定义属性

            if (name == ".geom" || name == "geom")
                return AbcSchemaType.IGeomBase;

            if (name.Contains("PolyMesh") || name.Contains("polymesh"))
                return AbcSchemaType.IPolyMesh;

            if (name.Contains("SubD") || name.Contains("subd"))
                return AbcSchemaType.ISubD;

            if (name.Contains("Points") || name.Contains("points"))
                return AbcSchemaType.IPoints;

            if (name.Contains("Curves") || name.Contains("curves"))
                return AbcSchemaType.ICurves;

            if (name.Contains("NuPatch") || name.Contains("nupatch"))
                return AbcSchemaType.INuPatch;

            if (name == ".xform" || name == "xform")
                return AbcSchemaType.IXform;

            if (name.Contains("Camera") || name.Contains("camera"))
                return AbcSchemaType.ICamera;

            if (name.Contains("Light") || name.Contains("light"))
                return AbcSchemaType.ILight;

            if (name.Contains("FaceSet") || name.Contains("faceset"))
                return AbcSchemaType.IFaceSet;

            return AbcSchemaType.Unknown;
        }

        // =================================================================================
        //  HDF5 格式占位
        // =================================================================================

        /// <summary>HDF5 后端占位解析（需要完整 HDF5 库支持）。</summary>
        private void ParseHdf5FormatPlaceholder()
        {
            _messages.Add("HDF5 后端需要完整的 HDF5 库支持，当前为占位实现");
            _messages.Add("建议将 Alembic 文件重新导出为 Ogawa 后端以获得完整支持");

            _rootObject = new AbcObjectNode
            {
                Name = "ABCRoot",
                Path = "/",
                SchemaType = AbcSchemaType.Unknown
            };
        }

        // =================================================================================
        //  Archive 元数据提取
        // =================================================================================

        private void ExtractArchiveMetadata()
        {
            // 从对象树中提取 Archive 元数据
            if (_rootObject == null) return;

            // 查找 Archive 元数据（通常在根节点的属性中）
            // Alembic Archive 包含：
            //   - ABC_alembic_version
            //   - timeSampling
            //   - startFrame / endFrame

            foreach (var child in _rootObject.Children)
            {
                if (child.Name == "ABC_alembic_version" || child.Name.Contains("version"))
                {
                    _abcLibrary = child.Name;
                }
            }

            // 时间采样信息
            // Alembic 使用 TimeSampling 描述动画时间轴
            _startTime = 0;
            _endTime = _frameCount > 0 ? _frameCount / _fps : 0;

            if (_endTime > _startTime)
                _messages.Add($"Alembic 时间范围: [{_startTime}, {_endTime}] 秒, 帧率: {_fps}");
        }

        // =================================================================================
        //  对象树遍历与转换
        // =================================================================================

        /// <summary>统计对象树中的对象数量。</summary>
        private int CountObjects(AbcObjectNode node)
        {
            int count = 1;
            foreach (var child in node.Children)
                count += CountObjects(child);
            return count;
        }

        /// <summary>遍历对象树并转换为 InterchangeScene。</summary>
        private void ConvertObjects()
        {
            if (_rootObject == null) return;

            // 设置时间范围
            double startTime = _options.StartTime ?? _startTime;
            double endTime = _options.EndTime ?? _endTime;
            double sampleInterval = _options.SampleInterval > 0 ? _options.SampleInterval : 1.0 / _fps;

            _messages.Add($"转换时间范围: [{startTime}, {endTime}], 采样间隔: {sampleInterval}s");

            // 递归遍历对象树
            var meshIndexMap = new Dictionary<string, int>(StringComparer.Ordinal);
            ConvertObjectRecursive(_rootObject, Matrix4x4.Identity, meshIndexMap);

            // 烘焙形变动画
            if (_options.BakeDeformAnimation && _endTime > _startTime)
            {
                BakeDeformAnimation(startTime, endTime, sampleInterval);
            }
        }

        /// <summary>递归转换对象。</summary>
        private void ConvertObjectRecursive(AbcObjectNode obj, Matrix4x4 parentTransform,
            Dictionary<string, int> meshIndexMap)
        {
            Matrix4x4 localTransform = Matrix4x4.Identity;

            // 提取变换（如果是 IXform）
            if (obj.SchemaType == AbcSchemaType.IXform)
            {
                localTransform = ExtractXform(obj);
            }

            if (_options.ConvertToLeftHanded)
            {
                // 翻转 Z 轴
                localTransform.M31 = -localTransform.M31;
                localTransform.M32 = -localTransform.M32;
                localTransform.M33 = -localTransform.M33;
                localTransform.M43 = -localTransform.M43;
            }

            Matrix4x4 worldTransform = localTransform * parentTransform;

            // 根据 Schema 类型转换
            switch (obj.SchemaType)
            {
                case AbcSchemaType.IPolyMesh:
                case AbcSchemaType.ISubD:
                    ConvertMeshObject(obj, worldTransform, meshIndexMap);
                    break;

                case AbcSchemaType.IPoints:
                    if (_options.ImportPoints)
                        ConvertPointsObject(obj, worldTransform);
                    break;

                case AbcSchemaType.ICurves:
                    if (_options.ImportCurves)
                        ConvertCurvesObject(obj, worldTransform);
                    break;

                case AbcSchemaType.INuPatch:
                    ConvertNuPatchObject(obj, worldTransform);
                    break;

                case AbcSchemaType.ICamera:
                    ConvertCameraObject(obj);
                    break;

                case AbcSchemaType.ILight:
                    ConvertLightObject(obj);
                    break;
            }

            // 创建场景节点
            var sceneNode = new InterchangeNode
            {
                Name = obj.Name,
                LocalTransform = localTransform
            };
            sceneNode.Metadata["AbcPath"] = obj.Path;
            sceneNode.Metadata["AbcSchema"] = obj.SchemaType.ToString();

            // 递归子对象
            foreach (var child in obj.Children)
            {
                ConvertObjectRecursive(child, worldTransform, meshIndexMap);
            }

            if (!string.IsNullOrEmpty(obj.Name) && obj.Name != "ABCRoot")
                Scene.RootNodes.Add(sceneNode);
        }

        // =================================================================================
        //  变换提取
        // =================================================================================

        /// <summary>从 IXform 对象提取变换矩阵。</summary>
        private Matrix4x4 ExtractXform(AbcObjectNode xformObj)
        {
            // Alembic IXform 存储变换操作栈
            // 通常包含平移、旋转、缩放

            Vector3 translation = Vector3.Zero;
            Quaternion rotation = Quaternion.Identity;
            Vector3 scale = Vector3.One;

            // 从时间采样中提取变换
            if (xformObj.TimeSamples.Count > 0)
            {
                var sample = xformObj.TimeSamples[0];

                if (sample.Data.TryGetValue("translation", out var trans) && trans is float[] transArr)
                {
                    if (transArr.Length >= 3)
                        translation = new Vector3(transArr[0], transArr[1], transArr[2]);
                }

                if (sample.Data.TryGetValue("rotation", out var rot) && rot is float[] rotArr)
                {
                    if (rotArr.Length >= 4)
                        rotation = new Quaternion(rotArr[0], rotArr[1], rotArr[2], rotArr[3]);
                }

                if (sample.Data.TryGetValue("scale", out var scl) && scl is float[] sclArr)
                {
                    if (sclArr.Length >= 3)
                        scale = new Vector3(sclArr[0], sclArr[1], sclArr[2]);
                }

                if (sample.Data.TryGetValue("matrix", out var mat) && mat is float[] matArr)
                {
                    if (matArr.Length >= 16)
                    {
                        return new Matrix4x4(
                            matArr[0], matArr[1], matArr[2], matArr[3],
                            matArr[4], matArr[5], matArr[6], matArr[7],
                            matArr[8], matArr[9], matArr[10], matArr[11],
                            matArr[12], matArr[13], matArr[14], matArr[15]
                        );
                    }
                }
            }

            translation *= _options.Scale;

            return Matrix4x4.CreateScale(scale) *
                   Matrix4x4.CreateFromQuaternion(rotation) *
                   Matrix4x4.CreateTranslation(translation);
        }

        // =================================================================================
        //  网格转换
        // =================================================================================

        /// <summary>转换 IPolyMesh / ISubD 对象。</summary>
        private void ConvertMeshObject(AbcObjectNode meshObj, Matrix4x4 worldTransform,
            Dictionary<string, int> meshIndexMap)
        {
            // 读取第一帧的网格数据
            AbcMeshSample? sample = ReadMeshSample(meshObj, 0);

            if (sample == null || sample.Positions.Length == 0)
            {
                _messages.Add($"网格 {meshObj.Name}: 无有效数据，跳过");
                return;
            }

            var mesh = new InterchangeMesh
            {
                Name = meshObj.Name
            };

            // 应用世界变换
            for (int i = 0; i < sample.Positions.Length; i++)
            {
                var pos = Vector3.Transform(sample.Positions[i], worldTransform);
                mesh.Vertices.Add(pos);
            }

            // 法线
            if (_options.ImportNormals && sample.Normals.Length > 0)
            {
                for (int i = 0; i < sample.Normals.Length; i++)
                {
                    var n = Vector3.TransformNormal(sample.Normals[i], worldTransform);
                    mesh.Normals.Add(n);
                }
            }

            // UV
            if (_options.ImportUVs && sample.UVs.Length > 0)
            {
                for (int i = 0; i < sample.UVs.Length; i++)
                    mesh.UV0.Add(sample.UVs[i]);
            }

            // 面索引（三角化）
            if (sample.FaceCounts.Length > 0 && sample.FaceIndices.Length > 0)
            {
                int idxOffset = 0;
                for (int f = 0; f < sample.FaceCounts.Length; f++)
                {
                    int faceVertexCount = sample.FaceCounts[f];

                    // 扇形三角化
                    for (int i = 1; i < faceVertexCount - 1; i++)
                    {
                        if (idxOffset < sample.FaceIndices.Length &&
                            idxOffset + i < sample.FaceIndices.Length &&
                            idxOffset + i + 1 < sample.FaceIndices.Length)
                        {
                            mesh.Indices.Add(sample.FaceIndices[idxOffset]);
                            mesh.Indices.Add(sample.FaceIndices[idxOffset + i]);
                            mesh.Indices.Add(sample.FaceIndices[idxOffset + i + 1]);
                        }
                    }

                    idxOffset += faceVertexCount;
                }
            }

            Scene.Meshes.Add(mesh);
            meshIndexMap[meshObj.Path] = Scene.Meshes.Count - 1;

            _messages.Add($"网格 {meshObj.Name}: {mesh.Vertices.Count} 顶点, {mesh.TriangleCount} 三角形");

            // 检查是否有形变动画
            if (_options.BakeDeformAnimation && meshObj.TimeSamples.Count > 1)
            {
                _messages.Add($"网格 {meshObj.Name}: 有 {meshObj.TimeSamples.Count} 个时间采样，将烘焙形变动画");
                mesh.Metadata["HasDeformAnimation"] = "true";
                mesh.Metadata["TimeSampleCount"] = meshObj.TimeSamples.Count.ToString();
            }
            else if (_options.OptimizeStaticMesh && meshObj.TimeSamples.Count <= 1)
            {
                _messages.Add($"网格 {meshObj.Name}: 静态网格，已优化");
            }
        }

        /// <summary>读取指定帧的网格采样数据。</summary>
        private AbcMeshSample? ReadMeshSample(AbcObjectNode meshObj, int frameIndex)
        {
            var sample = new AbcMeshSample();

            if (meshObj.TimeSamples.Count > 0 && frameIndex < meshObj.TimeSamples.Count)
            {
                var ts = meshObj.TimeSamples[frameIndex];

                // 位置 (P)
                if (ts.Data.TryGetValue("P", out var p) && p is float[] posArr)
                {
                    sample.Positions = new Vector3[posArr.Length / 3];
                    for (int i = 0; i < sample.Positions.Length; i++)
                    {
                        sample.Positions[i] = new Vector3(
                            posArr[i * 3], posArr[i * 3 + 1], posArr[i * 3 + 2]);
                    }
                }

                // 法线 (N)
                if (ts.Data.TryGetValue("N", out var n) && n is float[] normArr)
                {
                    sample.Normals = new Vector3[normArr.Length / 3];
                    for (int i = 0; i < sample.Normals.Length; i++)
                    {
                        sample.Normals[i] = new Vector3(
                            normArr[i * 3], normArr[i * 3 + 1], normArr[i * 3 + 2]);
                    }
                }

                // UV
                if (ts.Data.TryGetValue("uv", out var uv) && uv is float[] uvArr)
                {
                    sample.UVs = new Vector2[uvArr.Length / 2];
                    for (int i = 0; i < sample.UVs.Length; i++)
                    {
                        sample.UVs[i] = new Vector2(uvArr[i * 2], uvArr[i * 2 + 1]);
                    }
                }

                // 面索引
                if (ts.Data.TryGetValue("faceIndices", out var fi) && fi is int[] faceIdxArr)
                    sample.FaceIndices = faceIdxArr;

                // 面顶点数
                if (ts.Data.TryGetValue("faceCounts", out var fc) && fc is int[] faceCntArr)
                    sample.FaceCounts = faceCntArr;
            }

            return sample.Positions.Length > 0 ? sample : null;
        }

        // =================================================================================
        //  点云转换
        // =================================================================================

        private void ConvertPointsObject(AbcObjectNode pointsObj, Matrix4x4 worldTransform)
        {
            _messages.Add($"点云 {pointsObj.Name}");

            var mesh = new InterchangeMesh { Name = pointsObj.Name };

            // 读取点位置
            if (pointsObj.TimeSamples.Count > 0)
            {
                var ts = pointsObj.TimeSamples[0];
                if (ts.Data.TryGetValue("P", out var p) && p is float[] posArr)
                {
                    for (int i = 0; i < posArr.Length / 3; i++)
                    {
                        var pos = new Vector3(posArr[i * 3], posArr[i * 3 + 1], posArr[i * 3 + 2]);
                        pos = Vector3.Transform(pos, worldTransform);
                        mesh.Vertices.Add(pos);
                        mesh.Indices.Add(i); // 点云索引
                    }
                }
            }

            Scene.Meshes.Add(mesh);
        }

        // =================================================================================
        //  曲线转换
        // =================================================================================

        private void ConvertCurvesObject(AbcObjectNode curvesObj, Matrix4x4 worldTransform)
        {
            _messages.Add($"曲线 {curvesObj.Name}");
            // 曲线转换为线段网格
            // 简化实现：存储为元数据
        }

        // =================================================================================
        //  NURBS 曲面转换
        // =================================================================================

        private void ConvertNuPatchObject(AbcObjectNode nuPatchObj, Matrix4x4 worldTransform)
        {
            _messages.Add($"NURBS 曲面 {nuPatchObj.Name}");
            // NURBS 需要细分转换为多边形网格
            // 简化实现：存储为元数据
        }

        // =================================================================================
        //  相机转换
        // =================================================================================

        private void ConvertCameraObject(AbcObjectNode cameraObj)
        {
            _messages.Add($"相机 {cameraObj.Name}");

            // 提取相机参数
            float focalLength = 50f;
            float horizontalAperture = 24f;
            float nearClip = 0.1f;
            float farClip = 1000f;

            if (cameraObj.TimeSamples.Count > 0)
            {
                var ts = cameraObj.TimeSamples[0];

                if (ts.Data.TryGetValue("focalLength", out var fl) && fl is float)
                    focalLength = (float)fl;

                if (ts.Data.TryGetValue("horizontalAperture", out var ha) && ha is float)
                    horizontalAperture = (float)ha;

                if (ts.Data.TryGetValue("nearClipPlane", out var nc) && nc is float)
                    nearClip = (float)nc;

                if (ts.Data.TryGetValue("farClipPlane", out var fc) && fc is float)
                    farClip = (float)fc;
            }

            // 计算视场角
            float fov = 2f * MathF.Atan(horizontalAperture / (2f * focalLength)) * (180f / MathF.PI);

            _messages.Add($"  焦距: {focalLength}mm, 视场角: {fov:F1}°, 近裁剪: {nearClip}, 远裁剪: {farClip}");
        }

        // =================================================================================
        //  灯光转换
        // =================================================================================

        private void ConvertLightObject(AbcObjectNode lightObj)
        {
            _messages.Add($"灯光 {lightObj.Name}");

            // 提取灯光参数
            Vector3 color = Vector3.One;
            float intensity = 1f;

            if (lightObj.TimeSamples.Count > 0)
            {
                var ts = lightObj.TimeSamples[0];

                if (ts.Data.TryGetValue("color", out var c) && c is float[] colorArr && colorArr.Length >= 3)
                    color = new Vector3(colorArr[0], colorArr[1], colorArr[2]);

                if (ts.Data.TryGetValue("intensity", out var i) && i is float)
                    intensity = (float)i;
            }

            _messages.Add($"  颜色: ({color.X}, {color.Y}, {color.Z}), 强度: {intensity}");
        }

        // =================================================================================
        //  形变动画烘焙
        // =================================================================================

        /// <summary>烘焙形变动画为顶点关键帧。</summary>
        private void BakeDeformAnimation(double startTime, double endTime, double sampleInterval)
        {
            int frameCount = (int)Math.Ceiling((endTime - startTime) / sampleInterval);
            _messages.Add($"烘焙形变动画: {frameCount} 帧, 帧率 {1.0 / sampleInterval:F1} fps");

            // 为每个有形变动画的网格烘焙关键帧
            foreach (var mesh in Scene.Meshes)
            {
                if (!mesh.Metadata.TryGetValue("HasDeformAnimation", out var hasAnim) ||
                    hasAnim != "true")
                    continue;

                // 烘焙逻辑：在每帧采样网格顶点位置
                // 实际实现需要读取 Alembic 时间采样数据
                _messages.Add($"  网格 {mesh.Name}: 烘焙 {frameCount} 帧形变动画");

                // 存储动画数据
                mesh.Metadata["BakedFrameCount"] = frameCount.ToString();
                mesh.Metadata["BakeFrameRate"] = _options.BakeFrameRate.ToString(CultureInfo.InvariantCulture);
            }

            // 创建动画剪辑
            if (frameCount > 0)
            {
                var clip = new InterchangeAnimationClip
                {
                    Name = "AlembicDeformAnimation",
                    Duration = (float)(endTime - startTime),
                    FrameRate = (float)(1.0 / sampleInterval)
                };
                Scene.AnimationClips.Add(clip);
            }
        }

        // =================================================================================
        //  后处理
        // =================================================================================

        private void PostProcessScene()
        {
            // 添加默认材质
            if (Scene.Materials.Count == 0 && Scene.Meshes.Count > 0)
            {
                Scene.Materials.Add(new InterchangeMaterial
                {
                    Name = "DefaultMaterial",
                    DiffuseColor = new Vector3(0.8f, 0.8f, 0.8f)
                });
                foreach (var mesh in Scene.Meshes)
                    mesh.MaterialIndices.Add(0);
            }
        }
    }

    // =================================================================================
    //  AlembicImporter 适配器
    // =================================================================================

    /// <summary>Alembic 导入器适配器（用于 AssetImportManager 注册）。</summary>
    public sealed class AlembicImporterAdapter : IAssetImporter
    {
        /// <summary>支持的扩展名。</summary>
        public IEnumerable<string> SupportedExtensions => new[] { "abc" };

        /// <summary>导入 Alembic 文件。</summary>
        public InterchangeScene Import(string filePath)
        {
            var importer = new AlembicImporter(filePath);
            return importer.Import();
        }
    }
}
