// =====================================================================================
//  ShanyuEngine - AnimationImporter：动画导入器
//  命名空间：ShanyuEngine.Assets.Importers
// -------------------------------------------------------------------------------------
//  纯 C# 实现，支持多种动画来源的导入与处理：
//    - FBX 动画（调用已有 FBXImporter）
//    - glTF 动画（调用已有 glTFImporter）
//    - BVH（Biovision Hierarchy 动画捕捉格式 / 层级 / 通道 / 帧数据）
//    - Collada DAE（Collada 动画 / 通道 / 采样器）
//  BVH 解析：
//    - 层级解析（HIERARCHY / ROOT / JOINT / OFFSET / CHANNELS / End Site）
//    - 动画数据解析（MOTION / Frames / Frame Time / 数据值）
//    - BVH → 引擎骨骼动画转换（旋转顺序 / 平移 / 缩放）
//  动画处理：
//    - 采样率调整（原始帧率→目标帧率 / 重采样）
//    - 动画压缩（关键帧简化 / 曲线拟合 / 线性近似）
//    - 根运动提取（根骨骼平移→根运动轨迹）
//    - 循环动画检测（首尾姿态比较 / 循环动画标记）
//    - 动画分割（一个文件多个动画→多个动画剪辑）
//    - 骨骼重定向（不同骨骼之间的动画重定向）
// =====================================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using System.Xml;

namespace ShanyuEngine.Assets.Importers
{
    // =================================================================================
    //  导入选项
    // =================================================================================

    /// <summary>动画导入选项。</summary>
    public sealed class AnimationImportOptions
    {
        /// <summary>目标帧率。</summary>
        public float TargetFrameRate = 30f;

        /// <summary>是否压缩动画（关键帧简化）。</summary>
        public bool CompressAnimation = true;

        /// <summary>压缩容差（位置/旋转误差阈值）。</summary>
        public float CompressionTolerance = 0.001f;

        /// <summary>是否提取根运动。</summary>
        public bool ExtractRootMotion = false;

        /// <summary>根骨骼名称。</summary>
        public string RootBoneName = "Root";

        /// <summary>是否检测循环动画。</summary>
        public bool DetectLooping = false;

        /// <summary>循环检测阈值。</summary>
        public float LoopDetectionThreshold = 0.01f;

        /// <summary>是否分割动画（按时间范围分割多个剪辑）。</summary>
        public List<AnimationSplitRange>? SplitRanges = null;

        /// <summary>骨骼重定向映射（源骨骼名 → 目标骨骼名）。</summary>
        public Dictionary<string, string>? RetargetMapping = null;

        /// <summary>是否将 BVH 旋转顺序转换为引擎四元数。</summary>
        public bool ConvertBVHRotationOrder = true;

        /// <summary>全局缩放比例。</summary>
        public float Scale = 1.0f;
    }

    /// <summary>动画分割范围。</summary>
    public sealed class AnimationSplitRange
    {
        public string Name = string.Empty;
        public float StartTime;
        public float EndTime;
        public bool Loop = false;
    }

    // =================================================================================
    //  动画格式枚举
    // =================================================================================

    /// <summary>动画源格式。</summary>
    public enum AnimationFormat
    {
        Unknown,
        FBX,
        glTF,
        BVH,
        Collada
    }

    // =================================================================================
    //  BVH 数据结构
    // =================================================================================

    /// <summary>BVH 骨骼节点。</summary>
    internal sealed class BvhNode
    {
        public string Name = string.Empty;
        public Vector3 Offset;
        public int[] Channels = new int[6]; // 通道索引（-1=无此通道）
        public string ChannelOrder = string.Empty; // 如 "Xposition Yposition Zposition Zrotation Xrotation Yrotation"
        public int ChannelCount;
        public bool IsEndSite = false;
        public List<BvhNode> Children = new();
    }

    // =================================================================================
    //  AnimationImporter —— 主导入器
    // =================================================================================

    /// <summary>
    /// 动画导入器。支持 FBX、glTF、BVH、Collada DAE 动画格式。
    /// 输出为 InterchangeScene（含 AnimationClips）统一格式。
    /// </summary>
    public sealed class AnimationImporter
    {
        private readonly string _filePath;
        private readonly List<string> _messages = new();
        private AnimationImportOptions _options = new();

        /// <summary>解析产出的统一场景格式。</summary>
        public InterchangeScene? Scene { get; private set; }

        /// <summary>构造动画导入器。</summary>
        public AnimationImporter(string filePath)
        {
            _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        }

        /// <summary>使用选项执行导入。</summary>
        public InterchangeScene Import(AnimationImportOptions? options = null)
        {
            if (!File.Exists(_filePath))
                throw new FileNotFoundException($"动画源文件不存在: {_filePath}");

            _options = options ?? new AnimationImportOptions();

            Scene = new InterchangeScene
            {
                Name = Path.GetFileNameWithoutExtension(_filePath),
                SourceFilePath = _filePath,
                SourceFormat = "Animation"
            };

            try
            {
                AnimationFormat format = DetectFormat();
                _messages.Add($"检测到动画格式: {format}");

                switch (format)
                {
                    case AnimationFormat.BVH:
                        ImportBvh();
                        break;
                    case AnimationFormat.Collada:
                        ImportCollada();
                        break;
                    case AnimationFormat.FBX:
                        ImportFromFbx();
                        break;
                    case AnimationFormat.glTF:
                        ImportFromGltf();
                        break;
                    default:
                        throw new NotSupportedException($"不支持的动画格式: {format}");
                }

                // 后处理
                PostProcessAnimations();
            }
            catch (Exception ex)
            {
                Scene.ImportMessages.Add($"导入动画失败: {ex.Message}");
                throw;
            }

            Scene.ImportMessages.AddRange(_messages);
            return Scene;
        }

        // =================================================================================
        //  格式检测
        // =================================================================================

        private AnimationFormat DetectFormat()
        {
            string ext = Path.GetExtension(_filePath).ToLowerInvariant();

            return ext switch
            {
                ".bvh" => AnimationFormat.BVH,
                ".dae" => AnimationFormat.Collada,
                ".fbx" => AnimationFormat.FBX,
                ".gltf" or ".glb" => AnimationFormat.glTF,
                _ => AnimationFormat.Unknown
            };
        }

        // =================================================================================
        //  BVH 导入
        // =================================================================================

        private void ImportBvh()
        {
            string content = File.ReadAllText(_filePath, Encoding.UTF8);
            _messages.Add($"BVH 文件: {content.Length} 字符");

            // BVH 分为两部分：HIERARCHY 和 MOTION
            int motionIdx = content.IndexOf("MOTION", StringComparison.OrdinalIgnoreCase);
            if (motionIdx < 0)
                throw new InvalidDataException("BVH 文件缺少 MOTION 段");

            string hierarchyText = content.Substring(0, motionIdx);
            string motionText = content.Substring(motionIdx);

            // 解析层级
            BvhNode? root = ParseBvhHierarchy(hierarchyText);
            if (root == null)
                throw new InvalidDataException("BVH 层级解析失败");

            _messages.Add($"BVH 层级解析完成: 根骨骼={root.Name}");

            // 解析动画数据
            ParseBvhMotion(motionText, root);

            // 转换为引擎骨骼和动画
            ConvertBvhToScene(root);
        }

        /// <summary>解析 BVH 层级段。</summary>
        private BvhNode? ParseBvhHierarchy(string text)
        {
            var lines = text.Split(new char[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            var nodeStack = new Stack<BvhNode>();
            BvhNode? root = null;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (string.IsNullOrEmpty(line)) continue;

                if (line.StartsWith("HIERARCHY", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (line.StartsWith("ROOT ", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("JOINT ", StringComparison.OrdinalIgnoreCase))
                {
                    var node = new BvhNode();
                    // 提取名称
                    int spaceIdx = line.IndexOf(' ');
                    node.Name = line.Substring(spaceIdx + 1).Trim();

                    // 初始化通道索引
                    for (int c = 0; c < 6; c++)
                        node.Channels[c] = -1;

                    // 读取 OFFSET 和 CHANNELS（接下来的几行）
                    while (i + 1 < lines.Length)
                    {
                        string nextLine = lines[i + 1].Trim();
                        if (nextLine.StartsWith("OFFSET", StringComparison.OrdinalIgnoreCase) ||
                            nextLine.StartsWith("OFFSET:", StringComparison.OrdinalIgnoreCase))
                        {
                            var offsetParts = nextLine.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                            if (offsetParts.Length >= 4)
                            {
                                node.Offset = new Vector3(
                                    ParseFloat(offsetParts[1]),
                                    ParseFloat(offsetParts[2]),
                                    ParseFloat(offsetParts[3])
                                );
                            }
                            i++;
                        }
                        else if (nextLine.StartsWith("CHANNELS", StringComparison.OrdinalIgnoreCase) ||
                                 nextLine.StartsWith("CHANNELS:", StringComparison.OrdinalIgnoreCase))
                        {
                            var chanParts = nextLine.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                            int chanCount = int.Parse(chanParts[1], CultureInfo.InvariantCulture);
                            node.ChannelCount = chanCount;
                            node.ChannelOrder = string.Join(" ", chanParts, 2, chanCount);

                            // 记录通道名称
                            for (int c = 0; c < chanCount && c < 6; c++)
                            {
                                node.Channels[c] = c; // 通道在帧数据中的索引
                            }
                            i++;
                        }
                        else
                        {
                            break;
                        }
                    }

                    if (nodeStack.Count > 0)
                        nodeStack.Peek().Children.Add(node);
                    else
                        root = node;

                    nodeStack.Push(node);
                }
                else if (line.StartsWith("End Site", StringComparison.OrdinalIgnoreCase))
                {
                    var endNode = new BvhNode
                    {
                        Name = "EndSite",
                        IsEndSite = true
                    };

                    // 读取 OFFSET
                    while (i + 1 < lines.Length)
                    {
                        string nextLine = lines[i + 1].Trim();
                        if (nextLine.StartsWith("OFFSET", StringComparison.OrdinalIgnoreCase))
                        {
                            var offsetParts = nextLine.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                            if (offsetParts.Length >= 4)
                            {
                                endNode.Offset = new Vector3(
                                    ParseFloat(offsetParts[1]),
                                    ParseFloat(offsetParts[2]),
                                    ParseFloat(offsetParts[3])
                                );
                            }
                            i++;
                            break;
                        }
                        i++;
                    }

                    if (nodeStack.Count > 0)
                        nodeStack.Peek().Children.Add(endNode);
                }
                else if (line == "}")
                {
                    if (nodeStack.Count > 0)
                        nodeStack.Pop();
                }
            }

            return root;
        }

        /// <summary>解析 BVH MOTION 段。</summary>
        private void ParseBvhMotion(string text, BvhNode root)
        {
            var lines = text.Split(new char[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

            int frameCount = 0;
            float frameTime = 0f;
            List<float[]> frameData = new();

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();

                if (line.StartsWith("Frames:", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("Frames ", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Split(new char[] { ':', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                        frameCount = int.Parse(parts[1], CultureInfo.InvariantCulture);
                }
                else if (line.StartsWith("Frame Time:", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("Frame Time ", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Split(new char[] { ':' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                        frameTime = ParseFloat(parts[1].Trim());
                }
                else if (!string.IsNullOrEmpty(line) && char.IsDigit(line[0]))
                {
                    // 帧数据行
                    var parts = line.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    var frame = new float[parts.Length];
                    for (int j = 0; j < parts.Length; j++)
                        frame[j] = ParseFloat(parts[j]);
                    frameData.Add(frame);
                }
            }

            _messages.Add($"BVH MOTION: {frameCount} 帧, 帧间隔={frameTime}s, 实际读取={frameData.Count} 帧");

            // 存储动画数据
            _bvhRoot = root;
            _bvhFrameData = frameData;
            _bvhFrameTime = frameTime;
        }

        private BvhNode? _bvhRoot;
        private List<float[]> _bvhFrameData = new();
        private float _bvhFrameTime;

        /// <summary>将 BVH 转换为引擎骨骼和动画。</summary>
        private void ConvertBvhToScene(BvhNode root)
        {
            // 1) 构建骨骼
            var boneMap = new Dictionary<string, int>(StringComparer.Ordinal);
            ConvertBvhNodeToBone(root, -1, boneMap);

            // 2) 构建动画剪辑
            var clip = new InterchangeAnimationClip
            {
                Name = Scene.Name,
                Duration = _bvhFrameData.Count * _bvhFrameTime,
                FrameRate = 1f / _bvhFrameTime
            };

            // 为每个骨骼创建轨道
            var trackMap = new Dictionary<string, InterchangeBoneTrack>(StringComparer.Ordinal);
            foreach (var bone in Scene.Bones)
            {
                var track = new InterchangeBoneTrack { BoneName = bone.Name };
                clip.BoneTracks.Add(track);
                trackMap[bone.Name] = track;
            }

            // 3) 逐帧解析动画数据
            int globalChannelOffset = 0;
            for (int frame = 0; frame < _bvhFrameData.Count; frame++)
            {
                float time = frame * _bvhFrameTime;
                float[] data = _bvhFrameData[frame];
                int channelOffset = 0;

                // 递归处理每个骨骼的通道数据
                ProcessBvhFrame(root, data, ref channelOffset, frame, time, trackMap);
            }

            Scene.AnimationClips.Add(clip);
            _messages.Add($"BVH 转换完成: {Scene.Bones.Count} 骨骼, {clip.BoneTracks.Count} 轨道, " +
                         $"{_bvhFrameData.Count} 帧, {clip.Duration:F2}s");
        }

        /// <summary>递归将 BVH 节点转换为引擎骨骼。</summary>
        private void ConvertBvhNodeToBone(BvhNode node, int parentIdx, Dictionary<string, int> boneMap)
        {
            if (node.IsEndSite)
            {
                foreach (var child in node.Children)
                    ConvertBvhNodeToBone(child, parentIdx, boneMap);
                return;
            }

            var bone = new InterchangeBone
            {
                Name = node.Name,
                ParentIndex = parentIdx,
                LocalPosition = node.Offset * _options.Scale,
                LocalRotation = Quaternion.Identity,
                LocalScale = Vector3.One
            };
            bone.BindPoseLocal = Matrix4x4.CreateScale(bone.LocalScale) *
                                 Matrix4x4.CreateFromQuaternion(bone.LocalRotation) *
                                 Matrix4x4.CreateTranslation(bone.LocalPosition);

            Scene.Bones.Add(bone);
            int boneIdx = Scene.Bones.Count - 1;
            boneMap[node.Name] = boneIdx;

            if (parentIdx >= 0)
            {
                bone.ParentName = Scene.Bones[parentIdx].Name;
            }

            foreach (var child in node.Children)
                ConvertBvhNodeToBone(child, boneIdx, boneMap);
        }

        /// <summary>处理单帧的 BVH 通道数据。</summary>
        private void ProcessBvhFrame(BvhNode node, float[] frameData, ref int channelOffset,
            int frame, float time, Dictionary<string, InterchangeBoneTrack> trackMap)
        {
            if (node.IsEndSite)
            {
                foreach (var child in node.Children)
                    ProcessBvhFrame(child, frameData, ref channelOffset, frame, time, trackMap);
                return;
            }

            Vector3 position = Vector3.Zero;
            Quaternion rotation = Quaternion.Identity;

            // 解析通道
            var chanNames = node.ChannelOrder.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            float xRot = 0, yRot = 0, zRot = 0;
            bool hasXRot = false, hasYRot = false, hasZRot = false;

            for (int c = 0; c < node.ChannelCount && channelOffset < frameData.Length; c++)
            {
                float value = frameData[channelOffset];
                string chanName = c < chanNames.Length ? chanNames[c] : string.Empty;

                switch (chanName)
                {
                    case "Xposition":
                        position.X = value * _options.Scale;
                        break;
                    case "Yposition":
                        position.Y = value * _options.Scale;
                        break;
                    case "Zposition":
                        position.Z = value * _options.Scale;
                        break;
                    case "Xrotation":
                        xRot = value;
                        hasXRot = true;
                        break;
                    case "Yrotation":
                        yRot = value;
                        hasYRot = true;
                        break;
                    case "Zrotation":
                        zRot = value;
                        hasZRot = true;
                        break;
                }

                channelOffset++;
            }

            // 将欧拉角转换为四元数
            // BVH 默认旋转顺序为 ZYX（先 Z 再 Y 再 X）
            if (hasXRot || hasYRot || hasZRot)
            {
                float radX = xRot * MathF.PI / 180f;
                float radY = yRot * MathF.PI / 180f;
                float radZ = zRot * MathF.PI / 180f;

                if (_options.ConvertBVHRotationOrder)
                {
                    // ZYX 旋转顺序
                    Quaternion qx = Quaternion.CreateFromRotationMatrix(Matrix4x4.CreateRotationX(radX));
                    Quaternion qy = Quaternion.CreateFromRotationMatrix(Matrix4x4.CreateRotationY(radY));
                    Quaternion qz = Quaternion.CreateFromRotationMatrix(Matrix4x4.CreateRotationZ(radZ));
                    rotation = qz * qy * qx; // ZYX
                }
            }

            // 添加关键帧
            if (trackMap.TryGetValue(node.Name, out var track))
            {
                // 根骨骼有位置通道
                if (node.ChannelCount > 3) // 有位置通道
                    track.PositionKeys.Add((time, position));

                track.RotationKeys.Add((time, rotation));
                track.ScaleKeys.Add((time, Vector3.One));
            }

            // 递归子节点
            foreach (var child in node.Children)
                ProcessBvhFrame(child, frameData, ref channelOffset, frame, time, trackMap);
        }

        // =================================================================================
        //  Collada DAE 导入
        // =================================================================================

        private void ImportCollada()
        {
            string content = File.ReadAllText(_filePath, Encoding.UTF8);
            _messages.Add($"Collada DAE 文件: {content.Length} 字符");

            var doc = new XmlDocument();
            doc.LoadXml(content);

            XmlNamespaceManager nsmgr = new XmlNamespaceManager(doc.NameTable);
            nsmgr.AddNamespace("c", "http://www.collada.org/2005/11/COLLADASchema");

            // 解析骨骼层级
            ParseColladaSkeletons(doc, nsmgr);

            // 解析动画
            ParseColladaAnimations(doc, nsmgr);
        }

        /// <summary>解析 Collada 骨骼层级。</summary>
        private void ParseColladaSkeletons(XmlDocument doc, XmlNamespaceManager nsmgr)
        {
            var nodeNodes = doc.SelectNodes("//c:node", nsmgr);
            if (nodeNodes == null) return;

            var boneMap = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (XmlNode nodeNode in nodeNodes)
            {
                string? nodeId = nodeNode.Attributes?["id"]?.Value;
                string? nodeName = nodeNode.Attributes?["name"]?.Value ?? nodeId;
                string? nodeType = nodeNode.Attributes?["type"]?.Value;

                if (string.IsNullOrEmpty(nodeName)) continue;

                var bone = new InterchangeBone
                {
                    Name = nodeName,
                    ParentIndex = -1
                };

                // 解析变换
                var matrixNode = nodeNode.SelectSingleNode("c:matrix", nsmgr);
                if (matrixNode != null)
                {
                    string matrixText = matrixNode.InnerText.Trim();
                    var parts = matrixText.Split(new char[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 16)
                    {
                        var m = new Matrix4x4(
                            ParseFloat(parts[0]), ParseFloat(parts[1]), ParseFloat(parts[2]), ParseFloat(parts[3]),
                            ParseFloat(parts[4]), ParseFloat(parts[5]), ParseFloat(parts[6]), ParseFloat(parts[7]),
                            ParseFloat(parts[8]), ParseFloat(parts[9]), ParseFloat(parts[10]), ParseFloat(parts[11]),
                            ParseFloat(parts[12]), ParseFloat(parts[13]), ParseFloat(parts[14]), ParseFloat(parts[15])
                        );
                        bone.BindPoseLocal = m;

                        // 提取平移/旋转/缩放
                        if (Matrix4x4.Decompose(m, out var scale, out var rot, out var trans))
                        {
                            bone.LocalPosition = trans;
                            bone.LocalRotation = rot;
                            bone.LocalScale = scale;
                        }
                    }
                }

                Scene.Bones.Add(bone);
                if (!string.IsNullOrEmpty(nodeName))
                    boneMap[nodeName] = Scene.Bones.Count - 1;
            }

            _messages.Add($"Collada 骨骼: {Scene.Bones.Count} 个");
        }

        /// <summary>解析 Collada 动画。</summary>
        private void ParseColladaAnimations(XmlDocument doc, XmlNamespaceManager nsmgr)
        {
            var animNodes = doc.SelectNodes("//c:animation", nsmgr);
            if (animNodes == null) return;

            foreach (XmlNode animNode in animNodes)
            {
                string? animId = animNode.Attributes?["id"]?.Value ?? "Animation";
                var clip = new InterchangeAnimationClip
                {
                    Name = animId,
                    FrameRate = 30f
                };

                // 解析 source（时间采样和值）
                var sources = new Dictionary<string, float[]>(StringComparer.Ordinal);
                var sourceNodes = animNode.SelectNodes("c:source", nsmgr);
                if (sourceNodes != null)
                {
                    foreach (XmlNode srcNode in sourceNodes)
                    {
                        string? srcId = srcNode.Attributes?["id"]?.Value;
                        var floatArray = srcNode.SelectSingleNode("c:float_array", nsmgr);
                        if (srcId != null && floatArray != null)
                        {
                            var parts = floatArray.InnerText.Trim().Split(new char[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                            var values = new float[parts.Length];
                            for (int i = 0; i < parts.Length; i++)
                                values[i] = ParseFloat(parts[i]);
                            sources[srcId] = values;
                        }
                    }
                }

                // 解析 sampler
                var samplers = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
                var samplerNodes = animNode.SelectNodes("c:sampler", nsmgr);
                if (samplerNodes != null)
                {
                    foreach (XmlNode sampNode in samplerNodes)
                    {
                        string? sampId = sampNode.Attributes?["id"]?.Value;
                        var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
                        var inputNodes = sampNode.SelectNodes("c:input", nsmgr);
                        if (inputNodes != null)
                        {
                            foreach (XmlNode inputNode in inputNodes)
                            {
                                string? semantic = inputNode.Attributes?["semantic"]?.Value;
                                string? source = inputNode.Attributes?["source"]?.Value;
                                if (semantic != null && source != null)
                                    inputs[semantic] = source.TrimStart('#');
                            }
                        }
                        if (sampId != null)
                            samplers[sampId] = inputs;
                    }
                }

                // 解析 channel（目标绑定）
                var channelNodes = animNode.SelectNodes("c:channel", nsmgr);
                if (channelNodes != null)
                {
                    foreach (XmlNode chanNode in channelNodes)
                    {
                        string? target = chanNode.Attributes?["target"]?.Value;
                        string? source = chanNode.Attributes?["source"]?.Value?.TrimStart('#');

                        if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(source))
                            continue;

                        // target 格式: boneName/transform(0)(0)
                        string[] targetParts = target.Split('/');
                        if (targetParts.Length < 2) continue;

                        string boneName = targetParts[0];
                        string transformType = targetParts[1].Split('(')[0];

                        if (!samplers.TryGetValue(source, out var samplerInputs))
                            continue;

                        // 获取输入（时间）和输出（值）
                        float[]? times = null;
                        float[]? values = null;

                        if (samplerInputs.TryGetValue("INPUT", out var inputSrc) && sources.ContainsKey(inputSrc))
                            times = sources[inputSrc];
                        if (samplerInputs.TryGetValue("OUTPUT", out var outputSrc) && sources.ContainsKey(outputSrc))
                            values = sources[outputSrc];

                        if (times == null || values == null) continue;

                        // 创建或获取轨道
                        var track = clip.BoneTracks.Find(t => t.BoneName == boneName);
                        if (track == null)
                        {
                            track = new InterchangeBoneTrack { BoneName = boneName };
                            clip.BoneTracks.Add(track);
                        }

                        // 根据变换类型添加关键帧
                        switch (transformType)
                        {
                            case "translate":
                                for (int i = 0; i < times.Length && i * 3 + 2 < values.Length; i++)
                                {
                                    track.PositionKeys.Add((times[i], new Vector3(
                                        values[i * 3], values[i * 3 + 1], values[i * 3 + 2])));
                                }
                                break;
                            case "rotate":
                                for (int i = 0; i < times.Length && i * 4 + 3 < values.Length; i++)
                                {
                                    // Collada 旋转格式：axis.x axis.y axis.z angle
                                    Vector3 axis = new(values[i * 4], values[i * 4 + 1], values[i * 4 + 2]);
                                    float angle = values[i * 4 + 3] * MathF.PI / 180f;
                                    track.RotationKeys.Add((times[i],
                                        Quaternion.CreateFromAxisAngle(axis, angle)));
                                }
                                break;
                            case "scale":
                                for (int i = 0; i < times.Length && i * 3 + 2 < values.Length; i++)
                                {
                                    track.ScaleKeys.Add((times[i], new Vector3(
                                        values[i * 3], values[i * 3 + 1], values[i * 3 + 2])));
                                }
                                break;
                        }

                        // 更新时长
                        if (times.Length > 0 && times[^1] > clip.Duration)
                            clip.Duration = times[^1];
                    }
                }

                Scene.AnimationClips.Add(clip);
                _messages.Add($"Collada 动画: {clip.Name}, {clip.BoneTracks.Count} 轨道, {clip.Duration:F2}s");
            }
        }

        // =================================================================================
        //  从 FBX 导入动画
        // =================================================================================

        private void ImportFromFbx()
        {
            _messages.Add("从 FBX 导入动画（委托 FBXImporter）");

            // 调用已有的 FBXImporter
            var fbxImporter = new FbxImporter(_filePath);
            var fbxScene = fbxImporter.Import();

            // 复制动画数据
            Scene.AnimationClips = fbxScene.AnimationClips;
            Scene.Bones = fbxScene.Bones;
            Scene.RootNodes = fbxScene.RootNodes;

            _messages.Add($"FBX 动画导入: {Scene.AnimationClips.Count} 剪辑, {Scene.Bones.Count} 骨骼");
        }

        // =================================================================================
        //  从 glTF 导入动画
        // =================================================================================

        private void ImportFromGltf()
        {
            _messages.Add("从 glTF 导入动画（委托 glTFImporter）");

            var gltfImporter = new GlTFImporter(_filePath);
            var gltfScene = gltfImporter.Import(new GltfImportOptions
            {
                ImportAnimations = true,
                ImportSkins = true,
                ImportCameras = false,
                ImportLights = false,
                ImportMorphTargets = false
            });

            // 复制动画数据
            Scene.AnimationClips = gltfScene.AnimationClips;
            Scene.Bones = gltfScene.Bones;
            Scene.RootNodes = gltfScene.RootNodes;

            _messages.Add($"glTF 动画导入: {Scene.AnimationClips.Count} 剪辑, {Scene.Bones.Count} 骨骼");
        }

        // =================================================================================
        //  后处理
        // =================================================================================

        private void PostProcessAnimations()
        {
            foreach (var clip in Scene.AnimationClips)
            {
                // 采样率调整
                if (_options.TargetFrameRate > 0 && Math.Abs(clip.FrameRate - _options.TargetFrameRate) > 0.01f)
                {
                    ResampleClip(clip, _options.TargetFrameRate);
                }

                // 动画压缩
                if (_options.CompressAnimation)
                {
                    CompressClip(clip, _options.CompressionTolerance);
                }

                // 根运动提取
                if (_options.ExtractRootMotion)
                {
                    ExtractRootMotion(clip, _options.RootBoneName);
                }

                // 循环检测
                if (_options.DetectLooping)
                {
                    DetectLoopingAnimation(clip, _options.LoopDetectionThreshold);
                }

                // 骨骼重定向
                if (_options.RetargetMapping != null && _options.RetargetMapping.Count > 0)
                {
                    RetargetAnimation(clip, _options.RetargetMapping);
                }
            }

            // 动画分割
            if (_options.SplitRanges != null && _options.SplitRanges.Count > 0 && Scene.AnimationClips.Count > 0)
            {
                SplitAnimations(Scene.AnimationClips[0], _options.SplitRanges);
            }
        }

        /// <summary>重采样动画剪辑到目标帧率。</summary>
        private void ResampleClip(InterchangeAnimationClip clip, float targetFrameRate)
        {
            float originalFrameRate = clip.FrameRate;
            float originalDuration = clip.Duration;
            float frameInterval = 1f / targetFrameRate;
            int newFrameCount = (int)(originalDuration * targetFrameRate) + 1;

            foreach (var track in clip.BoneTracks)
            {
                // 重采样位置
                if (track.PositionKeys.Count > 0)
                {
                    var newKeys = new List<(float, Vector3)>();
                    for (int i = 0; i < newFrameCount; i++)
                    {
                        float time = i * frameInterval;
                        var pos = InterpolatePosition(track.PositionKeys, time);
                        newKeys.Add((time, pos));
                    }
                    track.PositionKeys = newKeys;
                }

                // 重采样旋转
                if (track.RotationKeys.Count > 0)
                {
                    var newKeys = new List<(float, Quaternion)>();
                    for (int i = 0; i < newFrameCount; i++)
                    {
                        float time = i * frameInterval;
                        var rot = InterpolateRotation(track.RotationKeys, time);
                        newKeys.Add((time, rot));
                    }
                    track.RotationKeys = newKeys;
                }

                // 重采样缩放
                if (track.ScaleKeys.Count > 0)
                {
                    var newKeys = new List<(float, Vector3)>();
                    for (int i = 0; i < newFrameCount; i++)
                    {
                        float time = i * frameInterval;
                        var scale = InterpolateScale(track.ScaleKeys, time);
                        newKeys.Add((time, scale));
                    }
                    track.ScaleKeys = newKeys;
                }
            }

            clip.FrameRate = targetFrameRate;
            _messages.Add($"重采样: {originalFrameRate} → {targetFrameRate} fps");
        }

        private Vector3 InterpolatePosition(List<(float Time, Vector3 Value)> keys, float time)
        {
            if (keys.Count == 0) return Vector3.Zero;
            if (time <= keys[0].Time) return keys[0].Value;
            if (time >= keys[^1].Time) return keys[^1].Value;

            for (int i = 0; i < keys.Count - 1; i++)
            {
                if (time >= keys[i].Time && time <= keys[i + 1].Time)
                {
                    float t = (time - keys[i].Time) / (keys[i + 1].Time - keys[i].Time);
                    return Vector3.Lerp(keys[i].Value, keys[i + 1].Value, t);
                }
            }
            return keys[^1].Value;
        }

        private Quaternion InterpolateRotation(List<(float Time, Quaternion Value)> keys, float time)
        {
            if (keys.Count == 0) return Quaternion.Identity;
            if (time <= keys[0].Time) return keys[0].Value;
            if (time >= keys[^1].Time) return keys[^1].Value;

            for (int i = 0; i < keys.Count - 1; i++)
            {
                if (time >= keys[i].Time && time <= keys[i + 1].Time)
                {
                    float t = (time - keys[i].Time) / (keys[i + 1].Time - keys[i].Time);
                    return Quaternion.Slerp(keys[i].Value, keys[i + 1].Value, t);
                }
            }
            return keys[^1].Value;
        }

        private Vector3 InterpolateScale(List<(float Time, Vector3 Value)> keys, float time)
        {
            if (keys.Count == 0) return Vector3.One;
            if (time <= keys[0].Time) return keys[0].Value;
            if (time >= keys[^1].Time) return keys[^1].Value;

            for (int i = 0; i < keys.Count - 1; i++)
            {
                if (time >= keys[i].Time && time <= keys[i + 1].Time)
                {
                    float t = (time - keys[i].Time) / (keys[i + 1].Time - keys[i].Time);
                    return Vector3.Lerp(keys[i].Value, keys[i + 1].Value, t);
                }
            }
            return keys[^1].Value;
        }

        /// <summary>压缩动画剪辑（关键帧简化）。</summary>
        private void CompressClip(InterchangeAnimationClip clip, float tolerance)
        {
            int removedKeys = 0;

            foreach (var track in clip.BoneTracks)
            {
                // 位置关键帧简化
                removedKeys += SimplifyPositionKeys(track, tolerance);
                // 旋转关键帧简化
                removedKeys += SimplifyRotationKeys(track, tolerance);
                // 缩放关键帧简化
                removedKeys += SimplifyScaleKeys(track, tolerance);
            }

            _messages.Add($"动画压缩: 移除 {removedKeys} 个冗余关键帧 (容差={tolerance})");
        }

        private int SimplifyPositionKeys(InterchangeBoneTrack track, float tolerance)
        {
            if (track.PositionKeys.Count < 3) return 0;
            int originalCount = track.PositionKeys.Count;
            var newKeys = new List<(float, Vector3)> { track.PositionKeys[0] };

            for (int i = 1; i < track.PositionKeys.Count - 1; i++)
            {
                var prev = newKeys[^1];
                var next = track.PositionKeys[i + 1];
                var current = track.PositionKeys[i];

                // 线性插值检查
                float t = (current.Time - prev.Time) / (next.Time - prev.Time);
                var interpolated = Vector3.Lerp(prev.Value, next.Value, t);

                if (Vector3.Distance(current.Value, interpolated) > tolerance)
                    newKeys.Add(current);
            }

            newKeys.Add(track.PositionKeys[^1]);
            track.PositionKeys = newKeys;
            return originalCount - newKeys.Count;
        }

        private int SimplifyRotationKeys(InterchangeBoneTrack track, float tolerance)
        {
            if (track.RotationKeys.Count < 3) return 0;
            int originalCount = track.RotationKeys.Count;
            var newKeys = new List<(float, Quaternion)> { track.RotationKeys[0] };

            for (int i = 1; i < track.RotationKeys.Count - 1; i++)
            {
                var prev = newKeys[^1];
                var next = track.RotationKeys[i + 1];
                var current = track.RotationKeys[i];

                float t = (current.Time - prev.Time) / (next.Time - prev.Time);
                var interpolated = Quaternion.Slerp(prev.Value, next.Value, t);

                // 旋转误差用角度差衡量
                float angle = 2f * MathF.Acos(MathF.Min(MathF.Abs(Vector4.Dot(
                    new Vector4(current.Value.X, current.Value.Y, current.Value.Z, current.Value.W),
                    new Vector4(interpolated.X, interpolated.Y, interpolated.Z, interpolated.W))), 1f));

                if (angle > tolerance)
                    newKeys.Add(current);
            }

            newKeys.Add(track.RotationKeys[^1]);
            track.RotationKeys = newKeys;
            return originalCount - newKeys.Count;
        }

        private int SimplifyScaleKeys(InterchangeBoneTrack track, float tolerance)
        {
            if (track.ScaleKeys.Count < 3) return 0;
            int originalCount = track.ScaleKeys.Count;
            var newKeys = new List<(float, Vector3)> { track.ScaleKeys[0] };

            for (int i = 1; i < track.ScaleKeys.Count - 1; i++)
            {
                var prev = newKeys[^1];
                var next = track.ScaleKeys[i + 1];
                var current = track.ScaleKeys[i];

                float t = (current.Time - prev.Time) / (next.Time - prev.Time);
                var interpolated = Vector3.Lerp(prev.Value, next.Value, t);

                if (Vector3.Distance(current.Value, interpolated) > tolerance)
                    newKeys.Add(current);
            }

            newKeys.Add(track.ScaleKeys[^1]);
            track.ScaleKeys = newKeys;
            return originalCount - newKeys.Count;
        }

        /// <summary>提取根运动轨迹。</summary>
        private void ExtractRootMotion(InterchangeAnimationClip clip, string rootBoneName)
        {
            var rootTrack = clip.BoneTracks.Find(t => t.BoneName == rootBoneName);
            if (rootTrack == null || rootTrack.PositionKeys.Count == 0)
            {
                _messages.Add($"根运动提取: 未找到根骨骼 '{rootBoneName}' 的位置数据");
                return;
            }

            // 提取根运动轨迹
            var rootMotion = new List<(float Time, Vector3 Position)>(rootTrack.PositionKeys);

            // 将根运动数据存储在元数据中
            clip.Metadata ??= new Dictionary<string, string>(StringComparer.Ordinal);
            clip.Metadata["RootMotionBone"] = rootBoneName;
            clip.Metadata["RootMotionFrames"] = rootMotion.Count.ToString();

            _messages.Add($"根运动提取: {rootMotion.Count} 帧, 骨骼={rootBoneName}");
        }

        /// <summary>检测循环动画。</summary>
        private void DetectLoopingAnimation(InterchangeAnimationClip clip, float threshold)
        {
            if (clip.Duration < 0.1f) return;

            float totalError = 0;
            int errorCount = 0;

            foreach (var track in clip.BoneTracks)
            {
                // 比较首尾位置
                if (track.PositionKeys.Count >= 2)
                {
                    var first = track.PositionKeys[0];
                    var last = track.PositionKeys[^1];
                    totalError += Vector3.Distance(first.Value, last.Value);
                    errorCount++;
                }

                // 比较首尾旋转
                if (track.RotationKeys.Count >= 2)
                {
                    var first = track.RotationKeys[0];
                    var last = track.RotationKeys[^1];
                    float angle = 2f * MathF.Acos(MathF.Min(MathF.Abs(Vector4.Dot(
                        new Vector4(first.Value.X, first.Value.Y, first.Value.Z, first.Value.W),
                        new Vector4(last.Value.X, last.Value.Y, last.Value.Z, last.Value.W))), 1f));
                    totalError += angle;
                    errorCount++;
                }
            }

            if (errorCount > 0)
            {
                float avgError = totalError / errorCount;
                bool isLooping = avgError < threshold;

                clip.Metadata ??= new Dictionary<string, string>(StringComparer.Ordinal);
                clip.Metadata["IsLooping"] = isLooping.ToString();
                clip.Metadata["LoopError"] = avgError.ToString("F6", CultureInfo.InvariantCulture);

                _messages.Add($"循环检测: {(isLooping ? "是" : "否")} (误差={avgError:F6}, 阈值={threshold})");
            }
        }

        /// <summary>分割动画为多个剪辑。</summary>
        private void SplitAnimations(InterchangeAnimationClip sourceClip, List<AnimationSplitRange> ranges)
        {
            var newClips = new List<InterchangeAnimationClip>();

            foreach (var range in ranges)
            {
                var subClip = new InterchangeAnimationClip
                {
                    Name = range.Name,
                    Duration = range.EndTime - range.StartTime,
                    FrameRate = sourceClip.FrameRate
                };
                subClip.Metadata ??= new Dictionary<string, string>(StringComparer.Ordinal);
                subClip.Metadata["IsLooping"] = range.Loop.ToString();

                foreach (var track in sourceClip.BoneTracks)
                {
                    var newTrack = new InterchangeBoneTrack { BoneName = track.BoneName };

                    // 提取时间范围内的位置关键帧
                    foreach (var (time, pos) in track.PositionKeys)
                    {
                        if (time >= range.StartTime && time <= range.EndTime)
                            newTrack.PositionKeys.Add((time - range.StartTime, pos));
                    }

                    // 提取时间范围内的旋转关键帧
                    foreach (var (time, rot) in track.RotationKeys)
                    {
                        if (time >= range.StartTime && time <= range.EndTime)
                            newTrack.RotationKeys.Add((time - range.StartTime, rot));
                    }

                    // 提取时间范围内的缩放关键帧
                    foreach (var (time, scale) in track.ScaleKeys)
                    {
                        if (time >= range.StartTime && time <= range.EndTime)
                            newTrack.ScaleKeys.Add((time - range.StartTime, scale));
                    }

                    subClip.BoneTracks.Add(newTrack);
                }

                newClips.Add(subClip);
                _messages.Add($"动画分割: {subClip.Name} [{range.StartTime:F2}-{range.EndTime:F2}]s, " +
                             $"{subClip.BoneTracks.Count} 轨道");
            }

            // 替换原始剪辑
            Scene.AnimationClips.Clear();
            Scene.AnimationClips.AddRange(newClips);
        }

        /// <summary>骨骼重定向（将动画从源骨骼映射到目标骨骼）。</summary>
        private void RetargetAnimation(InterchangeAnimationClip clip, Dictionary<string, string> mapping)
        {
            var newTracks = new List<InterchangeBoneTrack>();

            foreach (var track in clip.BoneTracks)
            {
                if (mapping.TryGetValue(track.BoneName, out var targetBoneName))
                {
                    // 创建重命名后的轨道
                    var newTrack = new InterchangeBoneTrack
                    {
                        BoneName = targetBoneName,
                        PositionKeys = new List<(float, Vector3)>(track.PositionKeys),
                        RotationKeys = new List<(float, Quaternion)>(track.RotationKeys),
                        ScaleKeys = new List<(float, Vector3)>(track.ScaleKeys)
                    };
                    newTracks.Add(newTrack);
                    _messages.Add($"重定向: {track.BoneName} → {targetBoneName}");
                }
                else
                {
                    // 保留未映射的轨道
                    newTracks.Add(track);
                }
            }

            clip.BoneTracks = newTracks;
            _messages.Add($"骨骼重定向: {mapping.Count} 个映射");
        }

        // =================================================================================
        //  工具方法
        // =================================================================================

        private static float ParseFloat(string s)
        {
            if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float val))
                return val;
            return 0f;
        }
    }

    // =================================================================================
    //  AnimationImporter 适配器
    // =================================================================================

    /// <summary>动画导入器适配器（用于 AssetImportManager 注册）。</summary>
    public sealed class AnimationImporterAdapter : IAssetImporter
    {
        /// <summary>支持的扩展名。</summary>
        public IEnumerable<string> SupportedExtensions => new[] { "bvh", "dae", "fbx", "gltf", "glb" };

        /// <summary>导入动画文件。</summary>
        public InterchangeScene Import(string filePath)
        {
            var importer = new AnimationImporter(filePath);
            return importer.Import();
        }
    }
}
