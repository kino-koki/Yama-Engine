using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Linq;

namespace YamaEngine.Physics
{
    // ============================================================================================
    //  PhysicsSerialization.cs
    //
    //  物理世界的序列化/反序列化系统。把整个物理场景(刚体、约束、形状、材质等)存到文件
    //  或者从文件加载。支持二进制和JSON两种格式。
    //
    //  用法:
    //    var serializer = new PhysicsSerializer();
    //    serializer.SaveToFile(world, "scene.physics");
    //    // 或者
    //    var data = serializer.SerializeToBinary(world);
    //    serializer.DeserializeFromBinary(data, world);
    //
    //  注意: 序列化的时候不要有线程在改物理世界，最好先暂停模拟。
    //  TODO: 支持增量保存(只存变化的部分)。
    // ============================================================================================

    /// <summary>序列化格式。</summary>
    public enum PhysicsSerializationFormat : byte
    {
        /// <summary>二进制格式。紧凑、快速。</summary>
        Binary = 0,
        /// <summary>JSON格式。可读、可手动编辑。</summary>
        Json = 1,
        /// <summary>压缩二进制。用Gzip压缩，省空间。</summary>
        CompressedBinary = 2,
    }

    /// <summary>序列化版本号。向后兼容用。</summary>
    public static class PhysicsSerializationVersion
    {
        public const int Current = 1;
        public const int MinCompatible = 1;
    }

    /// <summary>序列化选项。</summary>
    public class SerializationOptions
    {
        /// <summary>格式。</summary>
        public PhysicsSerializationFormat Format = PhysicsSerializationFormat.Binary;

        /// <summary>是否包含调试信息。</summary>
        public bool IncludeDebugInfo = false;

        /// <summary>是否包含性能数据。</summary>
        public bool IncludeProfilingData = false;

        /// <summary>是否压缩。</summary>
        public bool Compress = false;

        /// <summary>是否保存形状的原始顶点数据(对于mesh/convex hull)。</summary>
        public bool SaveShapeGeometry = true;

        /// <summary>是否保存body的速度和力。</summary>
        public bool SaveDynamicState = true;

        /// <summary>是否保存接触点(一般不存，运行时重新算)。</summary>
        public bool SaveContacts = false;

        /// <summary>是否保存约束。</summary>
        public bool SaveConstraints = true;

        /// <summary>是否保存材质。</summary>
        public bool SaveMaterials = true;

        /// <summary>是否保存岛屿信息。</summary>
        public bool SaveIslands = false;

        /// <summary>是否保存休眠状态。</summary>
        public bool SaveSleepState = true;

        /// <summary>字符串编码。</summary>
        public Encoding TextEncoding = Encoding.UTF8;

        /// <summary>浮点数精度(JSON用)。小数位数。</summary>
        public int FloatPrecision = 6;

        /// <summary>是否验证序列化数据。</summary>
        public bool ValidateOnLoad = true;

        /// <summary>最大允许的body数量(防止加载恶意文件导致OOM)。</summary>
        public int MaxBodies = 1000000;

        /// <summary>最大允许的形状数量。</summary>
        public int MaxShapes = 100000;

        /// <summary>最大允许的约束数量。</summary>
        public int MaxConstraints = 100000;

        /// <summary>最大文件大小(字节)。</summary>
        public long MaxFileSize = 1024 * 1024 * 1024; // 1GB

        public static SerializationOptions Default => new();
        public static SerializationOptions Json => new() { Format = PhysicsSerializationFormat.Json };
        public static SerializationOptions Compact => new()
        {
            Format = PhysicsSerializationFormat.CompressedBinary,
            Compress = true,
            SaveShapeGeometry = false,
            SaveDynamicState = false,
            SaveContacts = false,
        };
    }

    /// <summary>序列化结果。</summary>
    public struct SerializationResult
    {
        public bool Success;
        public string? ErrorMessage;
        public int BodyCount;
        public int ShapeCount;
        public int ConstraintCount;
        public int MaterialCount;
        public long DataSizeBytes;
        public double TimeMs;

        public static SerializationResult Ok(int bodies, int shapes, int constraints, int materials,
            long size, double time)
        {
            return new SerializationResult
            {
                Success = true,
                BodyCount = bodies,
                ShapeCount = shapes,
                ConstraintCount = constraints,
                MaterialCount = materials,
                DataSizeBytes = size,
                TimeMs = time,
            };
        }

        public static SerializationResult Fail(string error)
        {
            return new SerializationResult { Success = false, ErrorMessage = error };
        }
    }

    /// <summary>反序列化结果。</summary>
    public struct DeserializationResult
    {
        public bool Success;
        public string? ErrorMessage;
        public int BodiesLoaded;
        public int ShapesLoaded;
        public int ConstraintsLoaded;
        public int MaterialsLoaded;
        public int Version;
        public double TimeMs;
        public List<string>? Warnings;

        public static DeserializationResult Ok(int bodies, int shapes, int constraints,
            int materials, int version, double time, List<string>? warnings = null)
        {
            return new DeserializationResult
            {
                Success = true,
                BodiesLoaded = bodies,
                ShapesLoaded = shapes,
                ConstraintsLoaded = constraints,
                MaterialsLoaded = materials,
                Version = version,
                TimeMs = time,
                Warnings = warnings,
            };
        }

        public static DeserializationResult Fail(string error)
        {
            return new DeserializationResult { Success = false, ErrorMessage = error };
        }
    }

    // ============================================================================================
    //  序列化数据结构 (中间表示)
    // ============================================================================================

    /// <summary>物理场景的序列化数据。</summary>
    public class PhysicsSceneData
    {
        public int Version = PhysicsSerializationVersion.Current;
        public string EngineName = "YamaEngine.Physics";
        public DateTime CreatedTime = DateTime.Now;
        public Vector3 Gravity = new(0, -9.81f, 0);
        public float FixedTimestep = 1f / 60f;

        public List<SerializedShape> Shapes = new();
        public List<SerializedMaterial> Materials = new();
        public List<SerializedBody> Bodies = new();
        public List<SerializedConstraint> Constraints = new();
        public Dictionary<string, string> Metadata = new();

        // 统计信息(可选)
        public SerializedStats? Stats;
    }

    /// <summary>序列化的形状数据。</summary>
    public class SerializedShape
    {
        public int Id;
        public string Name = string.Empty;
        public CollisionShapeType Type;
        public Vector3 BoxHalfExtents;
        public float SphereRadius;
        public float CapsuleRadius;
        public float CapsuleHalfHeight;
        public float CylinderRadius;
        public float CylinderHalfHeight;
        public float ConeRadius;
        public float ConeHeight;
        public Vector3[]? ConvexVertices;
        public int[]? ConvexIndices;
        public Vector3[]? MeshVertices;
        public int[]? MeshIndices;
        public SerializedHeightfield? Heightfield;
        public List<SerializedSubShape>? CompoundChildren;
        public Vector3 LocalOffset;
        public Quaternion LocalRotation = Quaternion.Identity;
        public float Density = 1f;
    }

    /// <summary>序列化的子形状。</summary>
    public class SerializedSubShape
    {
        public int ShapeId;
        public Vector3 Offset;
        public Quaternion Rotation = Quaternion.Identity;
    }

    /// <summary>序列化的高度场。</summary>
    public class SerializedHeightfield
    {
        public int GridSizeX;
        public int GridSizeZ;
        public Vector2 SampleSpacing;
        public Vector3 Origin;
        public float[] Heights;
        public byte[] MaterialIndices;
        public float MinHeight;
        public float MaxHeight;
    }

    /// <summary>序列化的材质。</summary>
    public class SerializedMaterial
    {
        public int Id;
        public string Name = string.Empty;
        public float Friction = 0.6f;
        public float Restitution = 0.2f;
        public float Density = 1f;
        public Vector4 DebugColor = new(1, 1, 1, 1);
        public Dictionary<string, string> Properties = new();
    }

    /// <summary>序列化的刚体。</summary>
    public class SerializedBody
    {
        public int Id;
        public string Name = string.Empty;
        public int EntityId;
        public RigidbodyType BodyType = RigidbodyType.Dynamic;

        // 变换
        public Vector3 Position;
        public Quaternion Orientation = Quaternion.Identity;

        // 运动学状态
        public Vector3 LinearVelocity;
        public Vector3 AngularVelocity;
        public Vector3 Force;
        public Vector3 Torque;

        // 质量
        public float Mass = 1f;
        public Matrix4x4 InertiaTensor = Matrix4x4.Identity;

        // 形状
        public int ShapeId = -1;

        // 材质
        public int MaterialId = -1;

        // 行为参数
        public float LinearDamping = 0.05f;
        public float AngularDamping = 0.25f;
        public float GravityScale = 1f;
        public bool UseCCD;
        public bool IsSleeping;
        public float SleepTimer;

        // 碰撞
        public ushort CollisionLayer = 1;
        public ushort CollisionMask = 0xFFFF;
        public bool IsSensor;
        public bool EnableCollision = true;

        // 自定义属性
        public Dictionary<string, string> CustomProperties = new();
    }

    /// <summary>序列化的约束。</summary>
    public class SerializedConstraint
    {
        public int Id;
        public string Name = string.Empty;
        public SerializationConstraintType Type;
        public int BodyAId = -1;
        public int BodyBId = -1;
        public Vector3 AnchorA;
        public Vector3 AnchorB;
        public Vector3 AxisA = Vector3.UnitX;
        public Vector3 AxisB = Vector3.UnitX;
        public float MinLimit = float.MinValue;
        public float MaxLimit = float.MaxValue;
        public bool EnableCollision;
        public float BreakThreshold = float.MaxValue;
        public Dictionary<string, string> Properties = new();
    }

    /// <summary>序列化的统计信息。</summary>
    public class SerializedStats
    {
        public int BodyCount;
        public int ActiveBodyCount;
        public int SleepingBodyCount;
        public int StaticBodyCount;
        public int ConstraintCount;
        public int ContactCount;
        public int IslandCount;
        public float TotalMass;
        public float AvgSpeed;
        public float MaxSpeed;
    }

    /// <summary>约束类型。</summary>
    public enum SerializationConstraintType
    {
        Fixed,
        Point,
        Hinge,
        Slider,
        Cone,
        Ball,
        Distance,
        Spring,
        Custom,
    }

    // ============================================================================================
    //  二进制读写器
    // ============================================================================================

    /// <summary>二进制物理数据读写器。处理底层字节读写。</summary>
    public class PhysicsBinaryWriter : IDisposable
    {
        private readonly BinaryWriter _writer;
        private readonly MemoryStream _stream;
        private bool _disposed;

        public PhysicsBinaryWriter()
        {
            _stream = new MemoryStream(64 * 1024);
            _writer = new BinaryWriter(_stream, Encoding.UTF8, leaveOpen: false);
        }

        public byte[] ToArray()
        {
            Flush();
            return _stream.ToArray();
        }

        public void Flush() => _writer.Flush();

        public long Length => _stream.Length;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _writer.Dispose();
        }

        // ---- 基本类型 ----
        public void WriteByte(byte v) => _writer.Write(v);
        public void WriteSByte(sbyte v) => _writer.Write(v);
        public void WriteBool(bool v) => _writer.Write(v);
        public void WriteInt16(short v) => _writer.Write(v);
        public void WriteUInt16(ushort v) => _writer.Write(v);
        public void WriteInt32(int v) => _writer.Write(v);
        public void WriteUInt32(uint v) => _writer.Write(v);
        public void WriteInt64(long v) => _writer.Write(v);
        public void WriteUInt64(ulong v) => _writer.Write(v);
        public void WriteSingle(float v) => _writer.Write(v);
        public void WriteDouble(double v) => _writer.Write(v);

        // ---- 向量/矩阵 ----
        public void WriteVector2(Vector2 v)
        {
            _writer.Write(v.X);
            _writer.Write(v.Y);
        }

        public void WriteVector3(Vector3 v)
        {
            _writer.Write(v.X);
            _writer.Write(v.Y);
            _writer.Write(v.Z);
        }

        public void WriteVector4(Vector4 v)
        {
            _writer.Write(v.X);
            _writer.Write(v.Y);
            _writer.Write(v.Z);
            _writer.Write(v.W);
        }

        public void WriteQuaternion(Quaternion q)
        {
            _writer.Write(q.X);
            _writer.Write(q.Y);
            _writer.Write(q.Z);
            _writer.Write(q.W);
        }

        public void WriteMatrix4x4(Matrix4x4 m)
        {
            _writer.Write(m.M11); _writer.Write(m.M12); _writer.Write(m.M13); _writer.Write(m.M14);
            _writer.Write(m.M21); _writer.Write(m.M22); _writer.Write(m.M23); _writer.Write(m.M24);
            _writer.Write(m.M31); _writer.Write(m.M32); _writer.Write(m.M33); _writer.Write(m.M34);
            _writer.Write(m.M41); _writer.Write(m.M42); _writer.Write(m.M43); _writer.Write(m.M44);
        }

        // ---- 字符串 ----
        public void WriteString(string? s)
        {
            if (string.IsNullOrEmpty(s))
            {
                _writer.Write(0);
                return;
            }
            byte[] bytes = Encoding.UTF8.GetBytes(s);
            _writer.Write(bytes.Length);
            _writer.Write(bytes);
        }

        // ---- 数组 ----
        public void WriteFloatArray(float[]? arr)
        {
            if (arr == null)
            {
                _writer.Write(0);
                return;
            }
            _writer.Write(arr.Length);
            for (int i = 0; i < arr.Length; i++)
                _writer.Write(arr[i]);
        }

        public void WriteIntArray(int[]? arr)
        {
            if (arr == null)
            {
                _writer.Write(0);
                return;
            }
            _writer.Write(arr.Length);
            for (int i = 0; i < arr.Length; i++)
                _writer.Write(arr[i]);
        }

        public void WriteVector3Array(Vector3[]? arr)
        {
            if (arr == null)
            {
                _writer.Write(0);
                return;
            }
            _writer.Write(arr.Length);
            for (int i = 0; i < arr.Length; i++)
            {
                _writer.Write(arr[i].X);
                _writer.Write(arr[i].Y);
                _writer.Write(arr[i].Z);
            }
        }

        public void WriteByteArray(byte[]? arr)
        {
            if (arr == null)
            {
                _writer.Write(0);
                return;
            }
            _writer.Write(arr.Length);
            if (arr.Length > 0)
                _writer.Write(arr);
        }

        // ---- 字典 ----
        public void WriteStringDict(Dictionary<string, string>? dict)
        {
            if (dict == null || dict.Count == 0)
            {
                _writer.Write(0);
                return;
            }
            _writer.Write(dict.Count);
            foreach (var kvp in dict)
            {
                WriteString(kvp.Key);
                WriteString(kvp.Value);
            }
        }
    }

    /// <summary>二进制物理数据读取器。</summary>
    public class PhysicsBinaryReader : IDisposable
    {
        private readonly BinaryReader _reader;
        private bool _disposed;

        public PhysicsBinaryReader(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            _reader = new BinaryReader(new MemoryStream(data), Encoding.UTF8, leaveOpen: false);
        }

        public long Position => _reader.BaseStream.Position;
        public long Length => _reader.BaseStream.Length;
        public bool HasMore => _reader.BaseStream.Position < _reader.BaseStream.Length;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _reader.Dispose();
        }

        public byte ReadByte() => _reader.ReadByte();
        public sbyte ReadSByte() => _reader.ReadSByte();
        public bool ReadBool() => _reader.ReadBoolean();
        public short ReadInt16() => _reader.ReadInt16();
        public ushort ReadUInt16() => _reader.ReadUInt16();
        public int ReadInt32() => _reader.ReadInt32();
        public uint ReadUInt32() => _reader.ReadUInt32();
        public long ReadInt64() => _reader.ReadInt64();
        public ulong ReadUInt64() => _reader.ReadUInt64();
        public float ReadSingle() => _reader.ReadSingle();
        public double ReadDouble() => _reader.ReadDouble();

        public Vector2 ReadVector2() => new(_reader.ReadSingle(), _reader.ReadSingle());

        public Vector3 ReadVector3() => new(_reader.ReadSingle(), _reader.ReadSingle(), _reader.ReadSingle());

        public Vector4 ReadVector4() => new(_reader.ReadSingle(), _reader.ReadSingle(),
            _reader.ReadSingle(), _reader.ReadSingle());

        public Quaternion ReadQuaternion() => new(_reader.ReadSingle(), _reader.ReadSingle(),
            _reader.ReadSingle(), _reader.ReadSingle());

        public Matrix4x4 ReadMatrix4x4()
        {
            return new Matrix4x4(
                _reader.ReadSingle(), _reader.ReadSingle(), _reader.ReadSingle(), _reader.ReadSingle(),
                _reader.ReadSingle(), _reader.ReadSingle(), _reader.ReadSingle(), _reader.ReadSingle(),
                _reader.ReadSingle(), _reader.ReadSingle(), _reader.ReadSingle(), _reader.ReadSingle(),
                _reader.ReadSingle(), _reader.ReadSingle(), _reader.ReadSingle(), _reader.ReadSingle());
        }

        public string? ReadString()
        {
            int len = _reader.ReadInt32();
            if (len == 0) return null;
            if (len < 0 || len > 1024 * 1024 * 100) // 100MB上限
                throw new InvalidDataException($"字符串长度异常: {len}");
            byte[] bytes = _reader.ReadBytes(len);
            return Encoding.UTF8.GetString(bytes);
        }

        public float[]? ReadFloatArray()
        {
            int len = _reader.ReadInt32();
            if (len == 0) return null;
            if (len < 0 || len > 100_000_000)
                throw new InvalidDataException($"数组长度异常: {len}");
            var arr = new float[len];
            for (int i = 0; i < len; i++)
                arr[i] = _reader.ReadSingle();
            return arr;
        }

        public int[]? ReadIntArray()
        {
            int len = _reader.ReadInt32();
            if (len == 0) return null;
            if (len < 0 || len > 100_000_000)
                throw new InvalidDataException($"数组长度异常: {len}");
            var arr = new int[len];
            for (int i = 0; i < len; i++)
                arr[i] = _reader.ReadInt32();
            return arr;
        }

        public Vector3[]? ReadVector3Array()
        {
            int len = _reader.ReadInt32();
            if (len == 0) return null;
            if (len < 0 || len > 100_000_000)
                throw new InvalidDataException($"数组长度异常: {len}");
            var arr = new Vector3[len];
            for (int i = 0; i < len; i++)
            {
                arr[i] = new Vector3(_reader.ReadSingle(), _reader.ReadSingle(), _reader.ReadSingle());
            }
            return arr;
        }

        public byte[]? ReadByteArray()
        {
            int len = _reader.ReadInt32();
            if (len == 0) return null;
            if (len < 0 || len > 100_000_000)
                throw new InvalidDataException($"数组长度异常: {len}");
            return _reader.ReadBytes(len);
        }

        public Dictionary<string, string> ReadStringDict()
        {
            int count = _reader.ReadInt32();
            var dict = new Dictionary<string, string>(count);
            for (int i = 0; i < count; i++)
            {
                string? key = ReadString();
                string? val = ReadString();
                if (key != null)
                    dict[key] = val ?? string.Empty;
            }
            return dict;
        }
    }

    // ============================================================================================
    //  序列化器
    // ============================================================================================

    /// <summary>
    /// 物理序列化器。负责把物理场景存到文件/字节数组，或者从文件/字节数组加载。
    /// </summary>
    public class PhysicsSerializer
    {
        private SerializationOptions _options = new();
        public SerializationOptions Options
        {
            get => _options;
            set => _options = value ?? new SerializationOptions();
        }

        // ========================================================================================
        //  公开API
        // ========================================================================================

        /// <summary>序列化到字节数组。</summary>
        public (byte[] data, SerializationResult result) SerializeToBytes(
            IPhysicsSerializableWorld world)
        {
            if (world == null)
                return (Array.Empty<byte>(), SerializationResult.Fail("world is null"));

            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                // 1. 收集数据
                var sceneData = CollectSceneData(world);

                // 2. 序列化
                byte[] data;
                if (_options.Format == PhysicsSerializationFormat.Json)
                {
                    data = SerializeToJsonBytes(sceneData);
                }
                else
                {
                    data = SerializeToBinaryBytes(sceneData);
                }

                // 3. 压缩
                if (_options.Format == PhysicsSerializationFormat.CompressedBinary || _options.Compress)
                {
                    data = Compress(data);
                }

                sw.Stop();
                var result = SerializationResult.Ok(
                    sceneData.Bodies.Count,
                    sceneData.Shapes.Count,
                    sceneData.Constraints.Count,
                    sceneData.Materials.Count,
                    data.Length,
                    sw.Elapsed.TotalMilliseconds);

                return (data, result);
            }
            catch (Exception ex)
            {
                sw.Stop();
                return (Array.Empty<byte>(), SerializationResult.Fail(ex.Message));
            }
        }

        /// <summary>保存到文件。</summary>
        public SerializationResult SaveToFile(IPhysicsSerializableWorld world, string path)
        {
            if (string.IsNullOrEmpty(path))
                return SerializationResult.Fail("path is empty");

            var (data, result) = SerializeToBytes(world);
            if (!result.Success)
                return result;

            try
            {
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllBytes(path, data);
                return result;
            }
            catch (Exception ex)
            {
                return SerializationResult.Fail($"写文件失败: {ex.Message}");
            }
        }

        /// <summary>从字节数组反序列化。</summary>
        public (PhysicsSceneData? scene, DeserializationResult result) DeserializeFromBytes(
            byte[] data)
        {
            if (data == null || data.Length == 0)
                return (null, DeserializationResult.Fail("data is null or empty"));

            if (data.Length > _options.MaxFileSize)
                return (null, DeserializationResult.Fail(
                    $"文件太大: {data.Length} > {_options.MaxFileSize}"));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var warnings = new List<string>();

            try
            {
                // 1. 检测格式并解压
                byte[] actualData = data;
                if (IsCompressed(data))
                {
                    actualData = Decompress(data);
                }

                // 2. 检测是JSON还是二进制
                PhysicsSceneData? scene;
                if (IsJson(actualData))
                {
                    scene = DeserializeFromJsonBytes(actualData);
                }
                else
                {
                    scene = DeserializeFromBinaryBytes(actualData);
                }

                if (scene == null)
                {
                    sw.Stop();
                    return (null, DeserializationResult.Fail("反序列化失败"));
                }

                // 3. 版本检查
                if (scene.Version < PhysicsSerializationVersion.MinCompatible)
                {
                    warnings.Add($"版本过低: {scene.Version} < {PhysicsSerializationVersion.MinCompatible}");
                }
                if (scene.Version > PhysicsSerializationVersion.Current)
                {
                    warnings.Add($"版本过高: {scene.Version} > {PhysicsSerializationVersion.Current}, 尝试兼容加载");
                }

                // 4. 验证
                if (_options.ValidateOnLoad)
                {
                    var (valid, msg) = ValidateScene(scene);
                    if (!valid)
                    {
                        sw.Stop();
                        return (null, DeserializationResult.Fail($"验证失败: {msg}"));
                    }
                }

                // 5. 数量限制检查
                if (scene.Bodies.Count > _options.MaxBodies)
                {
                    sw.Stop();
                    return (null, DeserializationResult.Fail(
                        $"body数量超限: {scene.Bodies.Count} > {_options.MaxBodies}"));
                }
                if (scene.Shapes.Count > _options.MaxShapes)
                {
                    sw.Stop();
                    return (null, DeserializationResult.Fail(
                        $"shape数量超限: {scene.Shapes.Count} > {_options.MaxShapes}"));
                }
                if (scene.Constraints.Count > _options.MaxConstraints)
                {
                    sw.Stop();
                    return (null, DeserializationResult.Fail(
                        $"constraint数量超限: {scene.Constraints.Count} > {_options.MaxConstraints}"));
                }

                sw.Stop();
                var result = DeserializationResult.Ok(
                    scene.Bodies.Count,
                    scene.Shapes.Count,
                    scene.Constraints.Count,
                    scene.Materials.Count,
                    scene.Version,
                    sw.Elapsed.TotalMilliseconds,
                    warnings.Count > 0 ? warnings : null);

                return (scene, result);
            }
            catch (Exception ex)
            {
                sw.Stop();
                return (null, DeserializationResult.Fail(ex.Message));
            }
        }

        /// <summary>从文件加载。</summary>
        public (PhysicsSceneData? scene, DeserializationResult result) LoadFromFile(string path)
        {
            if (string.IsNullOrEmpty(path))
                return (null, DeserializationResult.Fail("path is empty"));
            if (!File.Exists(path))
                return (null, DeserializationResult.Fail($"文件不存在: {path}"));

            try
            {
                byte[] data = File.ReadAllBytes(path);
                return DeserializeFromBytes(data);
            }
            catch (Exception ex)
            {
                return (null, DeserializationResult.Fail($"读文件失败: {ex.Message}"));
            }
        }

        /// <summary>加载并应用到物理世界。</summary>
        public DeserializationResult LoadAndApply(string path, IPhysicsSerializableWorld world)
        {
            if (world == null)
                return DeserializationResult.Fail("world is null");

            var (scene, result) = LoadFromFile(path);
            if (!result.Success || scene == null)
                return result;

            try
            {
                ApplySceneToWorld(scene, world);
                return result;
            }
            catch (Exception ex)
            {
                return DeserializationResult.Fail($"应用失败: {ex.Message}");
            }
        }

        // ========================================================================================
        //  数据收集
        // ========================================================================================

        /// <summary>从物理世界收集场景数据。</summary>
        private PhysicsSceneData CollectSceneData(IPhysicsSerializableWorld world)
        {
            var scene = new PhysicsSceneData
            {
                Gravity = world.Gravity,
                FixedTimestep = world.FixedTimestep,
                CreatedTime = DateTime.Now,
            };

            // 收集材质
            if (_options.SaveMaterials)
            {
                foreach (var mat in world.GetMaterials())
                {
                    scene.Materials.Add(SerializeMaterial(mat));
                }
            }

            // 收集形状
            foreach (var shapeInfo in world.GetShapes())
            {
                scene.Shapes.Add(SerializeShape(shapeInfo));
            }

            // 收集body
            foreach (var bodyInfo in world.GetBodies())
            {
                scene.Bodies.Add(SerializeBody(bodyInfo));
            }

            // 收集约束
            if (_options.SaveConstraints)
            {
                foreach (var conInfo in world.GetConstraints())
                {
                    scene.Constraints.Add(SerializeConstraint(conInfo));
                }
            }

            // 元数据
            scene.Metadata["EngineVersion"] = "1.0";
            scene.Metadata["SerializerVersion"] = PhysicsSerializationVersion.Current.ToString();
            scene.Metadata["Options"] = $"Format={_options.Format},Compress={_options.Compress}";

            return scene;
        }

        private SerializedMaterial SerializeMaterial(PhysicsMaterialInfo mat)
        {
            return new SerializedMaterial
            {
                Id = mat.Id,
                Name = mat.Name ?? string.Empty,
                Friction = mat.Friction,
                Restitution = mat.Restitution,
                Density = mat.Density,
                DebugColor = mat.DebugColor,
                Properties = mat.Properties ?? new(),
            };
        }

        private SerializedShape SerializeShape(PhysicsShapeInfo shape)
        {
            var s = new SerializedShape
            {
                Id = shape.Id,
                Name = shape.Name ?? string.Empty,
                Type = shape.Type,
                LocalOffset = shape.LocalOffset,
                LocalRotation = shape.LocalRotation,
                Density = shape.Density,
            };

            switch (shape.Type)
            {
                case CollisionShapeType.Box:
                    s.BoxHalfExtents = shape.BoxHalfExtents;
                    break;
                case CollisionShapeType.Sphere:
                    s.SphereRadius = shape.SphereRadius;
                    break;
                case CollisionShapeType.Capsule:
                    s.CapsuleRadius = shape.CapsuleRadius;
                    s.CapsuleHalfHeight = shape.CapsuleHalfHeight;
                    break;
                case CollisionShapeType.Cylinder:
                    s.CylinderRadius = shape.CylinderRadius;
                    s.CylinderHalfHeight = shape.CylinderHalfHeight;
                    break;
                case CollisionShapeType.Cone:
                    s.ConeRadius = shape.ConeRadius;
                    s.ConeHeight = shape.ConeHeight;
                    break;
                case CollisionShapeType.ConvexHull:
                    if (_options.SaveShapeGeometry)
                    {
                        s.ConvexVertices = shape.ConvexVertices;
                        s.ConvexIndices = shape.ConvexIndices;
                    }
                    break;
                case CollisionShapeType.Mesh:
                    if (_options.SaveShapeGeometry)
                    {
                        s.MeshVertices = shape.MeshVertices;
                        s.MeshIndices = shape.MeshIndices;
                    }
                    break;
                case CollisionShapeType.Heightfield:
                    if (_options.SaveShapeGeometry && shape.Heightfield != null)
                    {
                        s.Heightfield = new SerializedHeightfield
                        {
                            GridSizeX = shape.Heightfield.GridSizeX,
                            GridSizeZ = shape.Heightfield.GridSizeZ,
                            SampleSpacing = shape.Heightfield.SampleSpacing,
                            Origin = shape.Heightfield.Origin,
                            Heights = shape.Heightfield.Heights,
                            MaterialIndices = shape.Heightfield.MaterialIndices,
                            MinHeight = shape.Heightfield.MinHeight,
                            MaxHeight = shape.Heightfield.MaxHeight,
                        };
                    }
                    break;
                case CollisionShapeType.Compound:
                    if (shape.CompoundChildren != null)
                    {
                        s.CompoundChildren = new List<SerializedSubShape>();
                        foreach (var child in shape.CompoundChildren)
                        {
                            s.CompoundChildren.Add(new SerializedSubShape
                            {
                                ShapeId = child.ShapeId,
                                Offset = child.Offset,
                                Rotation = child.Rotation,
                            });
                        }
                    }
                    break;
            }

            return s;
        }

        private SerializedBody SerializeBody(PhysicsBodyInfo body)
        {
            var b = new SerializedBody
            {
                Id = body.Id,
                Name = body.Name ?? string.Empty,
                EntityId = body.EntityId,
                BodyType = body.BodyType,
                Position = body.Position,
                Orientation = body.Orientation,
                Mass = body.Mass,
                InertiaTensor = body.InertiaTensor,
                ShapeId = body.ShapeId,
                MaterialId = body.MaterialId,
                LinearDamping = body.LinearDamping,
                AngularDamping = body.AngularDamping,
                GravityScale = body.GravityScale,
                UseCCD = body.UseCCD,
                CollisionLayer = body.CollisionLayer,
                CollisionMask = body.CollisionMask,
                IsSensor = body.IsSensor,
                EnableCollision = body.EnableCollision,
                CustomProperties = body.CustomProperties ?? new(),
            };

            if (_options.SaveDynamicState)
            {
                b.LinearVelocity = body.LinearVelocity;
                b.AngularVelocity = body.AngularVelocity;
                b.Force = body.Force;
                b.Torque = body.Torque;
            }

            if (_options.SaveSleepState)
            {
                b.IsSleeping = body.IsSleeping;
                b.SleepTimer = body.SleepTimer;
            }

            return b;
        }

        private SerializedConstraint SerializeConstraint(PhysicsConstraintInfo con)
        {
            var c = new SerializedConstraint
            {
                Id = con.Id,
                Name = con.Name ?? string.Empty,
                Type = con.Type,
                BodyAId = con.BodyAId,
                BodyBId = con.BodyBId,
                AnchorA = con.AnchorA,
                AnchorB = con.AnchorB,
                AxisA = con.AxisA,
                AxisB = con.AxisB,
                MinLimit = con.MinLimit,
                MaxLimit = con.MaxLimit,
                EnableCollision = con.EnableCollision,
                BreakThreshold = con.BreakThreshold,
                Properties = con.Properties ?? new(),
            };
            return c;
        }

        // ========================================================================================
        //  二进制序列化
        // ========================================================================================

        private byte[] SerializeToBinaryBytes(PhysicsSceneData scene)
        {
            using var writer = new PhysicsBinaryWriter();

            // 文件头
            WriteFileHeader(writer);

            // 场景信息
            WriteSceneInfo(writer, scene);

            // 材质
            writer.WriteInt32(scene.Materials.Count);
            foreach (var mat in scene.Materials)
                WriteMaterial(writer, mat);

            // 形状
            writer.WriteInt32(scene.Shapes.Count);
            foreach (var shape in scene.Shapes)
                WriteShape(writer, shape);

            // Body
            writer.WriteInt32(scene.Bodies.Count);
            foreach (var body in scene.Bodies)
                WriteBody(writer, body);

            // 约束
            writer.WriteInt32(scene.Constraints.Count);
            foreach (var con in scene.Constraints)
                WriteConstraint(writer, con);

            // 统计信息(可选)
            if (_options.IncludeProfilingData && scene.Stats != null)
            {
                writer.WriteBool(true);
                WriteStats(writer, scene.Stats);
            }
            else
            {
                writer.WriteBool(false);
            }

            // 文件尾
            writer.WriteInt32(CalculateChecksum(writer.ToArray()));

            return writer.ToArray();
        }

        private void WriteFileHeader(PhysicsBinaryWriter writer)
        {
            // 魔数 "SPHD" = Yama Physics Data
            writer.WriteByte((byte)'S');
            writer.WriteByte((byte)'P');
            writer.WriteByte((byte)'H');
            writer.WriteByte((byte)'D');

            // 版本
            writer.WriteInt32(PhysicsSerializationVersion.Current);

            // 格式标志
            writer.WriteByte((byte)_options.Format);
            writer.WriteBool(_options.Compress);
            writer.WriteBool(_options.IncludeDebugInfo);
            writer.WriteBool(_options.SaveShapeGeometry);
            writer.WriteBool(_options.SaveDynamicState);
            writer.WriteBool(_options.SaveConstraints);
            writer.WriteBool(_options.SaveMaterials);

            // 保留字节(以后扩展用)
            writer.WriteInt32(0); // reserved
            writer.WriteInt32(0);
        }

        private void WriteSceneInfo(PhysicsBinaryWriter writer, PhysicsSceneData scene)
        {
            writer.WriteString(scene.EngineName);
            writer.WriteInt64(scene.CreatedTime.ToBinary());
            writer.WriteVector3(scene.Gravity);
            writer.WriteSingle(scene.FixedTimestep);
            writer.WriteStringDict(scene.Metadata);
        }

        private void WriteMaterial(PhysicsBinaryWriter writer, SerializedMaterial mat)
        {
            writer.WriteInt32(mat.Id);
            writer.WriteString(mat.Name);
            writer.WriteSingle(mat.Friction);
            writer.WriteSingle(mat.Restitution);
            writer.WriteSingle(mat.Density);
            writer.WriteVector4(mat.DebugColor);
            writer.WriteStringDict(mat.Properties);
        }

        private void WriteShape(PhysicsBinaryWriter writer, SerializedShape shape)
        {
            writer.WriteInt32(shape.Id);
            writer.WriteString(shape.Name);
            writer.WriteByte((byte)shape.Type);

            switch (shape.Type)
            {
                case CollisionShapeType.Box:
                    writer.WriteVector3(shape.BoxHalfExtents);
                    break;
                case CollisionShapeType.Sphere:
                    writer.WriteSingle(shape.SphereRadius);
                    break;
                case CollisionShapeType.Capsule:
                    writer.WriteSingle(shape.CapsuleRadius);
                    writer.WriteSingle(shape.CapsuleHalfHeight);
                    break;
                case CollisionShapeType.Cylinder:
                    writer.WriteSingle(shape.CylinderRadius);
                    writer.WriteSingle(shape.CylinderHalfHeight);
                    break;
                case CollisionShapeType.Cone:
                    writer.WriteSingle(shape.ConeRadius);
                    writer.WriteSingle(shape.ConeHeight);
                    break;
                case CollisionShapeType.ConvexHull:
                    writer.WriteVector3Array(shape.ConvexVertices);
                    writer.WriteIntArray(shape.ConvexIndices);
                    break;
                case CollisionShapeType.Mesh:
                    writer.WriteVector3Array(shape.MeshVertices);
                    writer.WriteIntArray(shape.MeshIndices);
                    break;
                case CollisionShapeType.Heightfield:
                    if (shape.Heightfield != null)
                    {
                        writer.WriteBool(true);
                        var hf = shape.Heightfield;
                        writer.WriteInt32(hf.GridSizeX);
                        writer.WriteInt32(hf.GridSizeZ);
                        writer.WriteVector2(hf.SampleSpacing);
                        writer.WriteVector3(hf.Origin);
                        writer.WriteFloatArray(hf.Heights);
                        writer.WriteByteArray(hf.MaterialIndices);
                        writer.WriteSingle(hf.MinHeight);
                        writer.WriteSingle(hf.MaxHeight);
                    }
                    else
                    {
                        writer.WriteBool(false);
                    }
                    break;
                case CollisionShapeType.Compound:
                    if (shape.CompoundChildren != null)
                    {
                        writer.WriteInt32(shape.CompoundChildren.Count);
                        foreach (var child in shape.CompoundChildren)
                        {
                            writer.WriteInt32(child.ShapeId);
                            writer.WriteVector3(child.Offset);
                            writer.WriteQuaternion(child.Rotation);
                        }
                    }
                    else
                    {
                        writer.WriteInt32(0);
                    }
                    break;
            }

            writer.WriteVector3(shape.LocalOffset);
            writer.WriteQuaternion(shape.LocalRotation);
            writer.WriteSingle(shape.Density);
        }

        private void WriteBody(PhysicsBinaryWriter writer, SerializedBody body)
        {
            writer.WriteInt32(body.Id);
            writer.WriteString(body.Name);
            writer.WriteInt32(body.EntityId);
            writer.WriteByte((byte)body.BodyType);

            writer.WriteVector3(body.Position);
            writer.WriteQuaternion(body.Orientation);

            if (_options.SaveDynamicState)
            {
                writer.WriteBool(true);
                writer.WriteVector3(body.LinearVelocity);
                writer.WriteVector3(body.AngularVelocity);
                writer.WriteVector3(body.Force);
                writer.WriteVector3(body.Torque);
            }
            else
            {
                writer.WriteBool(false);
            }

            writer.WriteSingle(body.Mass);
            writer.WriteMatrix4x4(body.InertiaTensor);
            writer.WriteInt32(body.ShapeId);
            writer.WriteInt32(body.MaterialId);
            writer.WriteSingle(body.LinearDamping);
            writer.WriteSingle(body.AngularDamping);
            writer.WriteSingle(body.GravityScale);
            writer.WriteBool(body.UseCCD);

            if (_options.SaveSleepState)
            {
                writer.WriteBool(true);
                writer.WriteBool(body.IsSleeping);
                writer.WriteSingle(body.SleepTimer);
            }
            else
            {
                writer.WriteBool(false);
            }

            writer.WriteUInt16(body.CollisionLayer);
            writer.WriteUInt16(body.CollisionMask);
            writer.WriteBool(body.IsSensor);
            writer.WriteBool(body.EnableCollision);

            writer.WriteStringDict(body.CustomProperties);
        }

        private void WriteConstraint(PhysicsBinaryWriter writer, SerializedConstraint con)
        {
            writer.WriteInt32(con.Id);
            writer.WriteString(con.Name);
            writer.WriteByte((byte)con.Type);
            writer.WriteInt32(con.BodyAId);
            writer.WriteInt32(con.BodyBId);
            writer.WriteVector3(con.AnchorA);
            writer.WriteVector3(con.AnchorB);
            writer.WriteVector3(con.AxisA);
            writer.WriteVector3(con.AxisB);
            writer.WriteSingle(con.MinLimit);
            writer.WriteSingle(con.MaxLimit);
            writer.WriteBool(con.EnableCollision);
            writer.WriteSingle(con.BreakThreshold);
            writer.WriteStringDict(con.Properties);
        }

        private void WriteStats(PhysicsBinaryWriter writer, SerializedStats stats)
        {
            writer.WriteInt32(stats.BodyCount);
            writer.WriteInt32(stats.ActiveBodyCount);
            writer.WriteInt32(stats.SleepingBodyCount);
            writer.WriteInt32(stats.StaticBodyCount);
            writer.WriteInt32(stats.ConstraintCount);
            writer.WriteInt32(stats.ContactCount);
            writer.WriteInt32(stats.IslandCount);
            writer.WriteSingle(stats.TotalMass);
            writer.WriteSingle(stats.AvgSpeed);
            writer.WriteSingle(stats.MaxSpeed);
        }

        // ========================================================================================
        //  二进制反序列化
        // ========================================================================================

        private PhysicsSceneData? DeserializeFromBinaryBytes(byte[] data)
        {
            using var reader = new PhysicsBinaryReader(data);

            // 文件头
            if (!ReadAndValidateHeader(reader))
                return null;

            // 场景信息
            var scene = ReadSceneInfo(reader);
            if (scene == null) return null;

            // 材质
            int matCount = reader.ReadInt32();
            if (matCount < 0 || matCount > _options.MaxShapes * 10) return null;
            for (int i = 0; i < matCount; i++)
            {
                var mat = ReadMaterial(reader);
                if (mat != null) scene.Materials.Add(mat);
            }

            // 形状
            int shapeCount = reader.ReadInt32();
            if (shapeCount < 0 || shapeCount > _options.MaxShapes) return null;
            for (int i = 0; i < shapeCount; i++)
            {
                var shape = ReadShape(reader);
                if (shape != null) scene.Shapes.Add(shape);
            }

            // Body
            int bodyCount = reader.ReadInt32();
            if (bodyCount < 0 || bodyCount > _options.MaxBodies) return null;
            for (int i = 0; i < bodyCount; i++)
            {
                var body = ReadBody(reader);
                if (body != null) scene.Bodies.Add(body);
            }

            // 约束
            int conCount = reader.ReadInt32();
            if (conCount < 0 || conCount > _options.MaxConstraints) return null;
            for (int i = 0; i < conCount; i++)
            {
                var con = ReadConstraint(reader);
                if (con != null) scene.Constraints.Add(con);
            }

            // 统计信息
            if (reader.ReadBool())
            {
                scene.Stats = ReadStats(reader);
            }

            return scene;
        }

        private bool ReadAndValidateHeader(PhysicsBinaryReader reader)
        {
            // 魔数
            byte m0 = reader.ReadByte();
            byte m1 = reader.ReadByte();
            byte m2 = reader.ReadByte();
            byte m3 = reader.ReadByte();

            if (m0 != (byte)'S' || m1 != (byte)'P' || m2 != (byte)'H' || m3 != (byte)'D')
                return false;

            // 版本
            int version = reader.ReadInt32();
            if (version < PhysicsSerializationVersion.MinCompatible)
                return false;

            // 格式标志
            reader.ReadByte();  // format
            reader.ReadBool();  // compress
            reader.ReadBool();  // debug
            reader.ReadBool();  // geometry
            reader.ReadBool();  // dynamicState
            reader.ReadBool();  // constraints
            reader.ReadBool();  // materials

            // 保留
            reader.ReadInt32();
            reader.ReadInt32();

            return true;
        }

        private PhysicsSceneData? ReadSceneInfo(PhysicsBinaryReader reader)
        {
            var scene = new PhysicsSceneData();
            scene.EngineName = reader.ReadString() ?? "Unknown";
            long timeBinary = reader.ReadInt64();
            try { scene.CreatedTime = DateTime.FromBinary(timeBinary); }
            catch { scene.CreatedTime = DateTime.Now; }
            scene.Gravity = reader.ReadVector3();
            scene.FixedTimestep = reader.ReadSingle();
            scene.Metadata = reader.ReadStringDict();
            return scene;
        }

        private SerializedMaterial? ReadMaterial(PhysicsBinaryReader reader)
        {
            var mat = new SerializedMaterial();
            mat.Id = reader.ReadInt32();
            mat.Name = reader.ReadString() ?? string.Empty;
            mat.Friction = reader.ReadSingle();
            mat.Restitution = reader.ReadSingle();
            mat.Density = reader.ReadSingle();
            mat.DebugColor = reader.ReadVector4();
            mat.Properties = reader.ReadStringDict();
            return mat;
        }

        private SerializedShape? ReadShape(PhysicsBinaryReader reader)
        {
            var shape = new SerializedShape();
            shape.Id = reader.ReadInt32();
            shape.Name = reader.ReadString() ?? string.Empty;
            shape.Type = (CollisionShapeType)reader.ReadByte();

            switch (shape.Type)
            {
                case CollisionShapeType.Box:
                    shape.BoxHalfExtents = reader.ReadVector3();
                    break;
                case CollisionShapeType.Sphere:
                    shape.SphereRadius = reader.ReadSingle();
                    break;
                case CollisionShapeType.Capsule:
                    shape.CapsuleRadius = reader.ReadSingle();
                    shape.CapsuleHalfHeight = reader.ReadSingle();
                    break;
                case CollisionShapeType.Cylinder:
                    shape.CylinderRadius = reader.ReadSingle();
                    shape.CylinderHalfHeight = reader.ReadSingle();
                    break;
                case CollisionShapeType.Cone:
                    shape.ConeRadius = reader.ReadSingle();
                    shape.ConeHeight = reader.ReadSingle();
                    break;
                case CollisionShapeType.ConvexHull:
                    shape.ConvexVertices = reader.ReadVector3Array();
                    shape.ConvexIndices = reader.ReadIntArray();
                    break;
                case CollisionShapeType.Mesh:
                    shape.MeshVertices = reader.ReadVector3Array();
                    shape.MeshIndices = reader.ReadIntArray();
                    break;
                case CollisionShapeType.Heightfield:
                    if (reader.ReadBool())
                    {
                        shape.Heightfield = new SerializedHeightfield
                        {
                            GridSizeX = reader.ReadInt32(),
                            GridSizeZ = reader.ReadInt32(),
                            SampleSpacing = reader.ReadVector2(),
                            Origin = reader.ReadVector3(),
                            Heights = reader.ReadFloatArray() ?? Array.Empty<float>(),
                            MaterialIndices = reader.ReadByteArray(),
                            MinHeight = reader.ReadSingle(),
                            MaxHeight = reader.ReadSingle(),
                        };
                    }
                    break;
                case CollisionShapeType.Compound:
                    int childCount = reader.ReadInt32();
                    if (childCount > 0)
                    {
                        shape.CompoundChildren = new List<SerializedSubShape>(childCount);
                        for (int i = 0; i < childCount; i++)
                        {
                            shape.CompoundChildren.Add(new SerializedSubShape
                            {
                                ShapeId = reader.ReadInt32(),
                                Offset = reader.ReadVector3(),
                                Rotation = reader.ReadQuaternion(),
                            });
                        }
                    }
                    break;
            }

            shape.LocalOffset = reader.ReadVector3();
            shape.LocalRotation = reader.ReadQuaternion();
            shape.Density = reader.ReadSingle();
            return shape;
        }

        private SerializedBody? ReadBody(PhysicsBinaryReader reader)
        {
            var body = new SerializedBody();
            body.Id = reader.ReadInt32();
            body.Name = reader.ReadString() ?? string.Empty;
            body.EntityId = reader.ReadInt32();
            body.BodyType = (RigidbodyType)reader.ReadByte();

            body.Position = reader.ReadVector3();
            body.Orientation = reader.ReadQuaternion();

            if (reader.ReadBool())
            {
                body.LinearVelocity = reader.ReadVector3();
                body.AngularVelocity = reader.ReadVector3();
                body.Force = reader.ReadVector3();
                body.Torque = reader.ReadVector3();
            }

            body.Mass = reader.ReadSingle();
            body.InertiaTensor = reader.ReadMatrix4x4();
            body.ShapeId = reader.ReadInt32();
            body.MaterialId = reader.ReadInt32();
            body.LinearDamping = reader.ReadSingle();
            body.AngularDamping = reader.ReadSingle();
            body.GravityScale = reader.ReadSingle();
            body.UseCCD = reader.ReadBool();

            if (reader.ReadBool())
            {
                body.IsSleeping = reader.ReadBool();
                body.SleepTimer = reader.ReadSingle();
            }

            body.CollisionLayer = reader.ReadUInt16();
            body.CollisionMask = reader.ReadUInt16();
            body.IsSensor = reader.ReadBool();
            body.EnableCollision = reader.ReadBool();

            body.CustomProperties = reader.ReadStringDict();
            return body;
        }

        private SerializedConstraint? ReadConstraint(PhysicsBinaryReader reader)
        {
            var con = new SerializedConstraint();
            con.Id = reader.ReadInt32();
            con.Name = reader.ReadString() ?? string.Empty;
            con.Type = (SerializationConstraintType)reader.ReadByte();
            con.BodyAId = reader.ReadInt32();
            con.BodyBId = reader.ReadInt32();
            con.AnchorA = reader.ReadVector3();
            con.AnchorB = reader.ReadVector3();
            con.AxisA = reader.ReadVector3();
            con.AxisB = reader.ReadVector3();
            con.MinLimit = reader.ReadSingle();
            con.MaxLimit = reader.ReadSingle();
            con.EnableCollision = reader.ReadBool();
            con.BreakThreshold = reader.ReadSingle();
            con.Properties = reader.ReadStringDict();
            return con;
        }

        private SerializedStats ReadStats(PhysicsBinaryReader reader)
        {
            return new SerializedStats
            {
                BodyCount = reader.ReadInt32(),
                ActiveBodyCount = reader.ReadInt32(),
                SleepingBodyCount = reader.ReadInt32(),
                StaticBodyCount = reader.ReadInt32(),
                ConstraintCount = reader.ReadInt32(),
                ContactCount = reader.ReadInt32(),
                IslandCount = reader.ReadInt32(),
                TotalMass = reader.ReadSingle(),
                AvgSpeed = reader.ReadSingle(),
                MaxSpeed = reader.ReadSingle(),
            };
        }

        // ========================================================================================
        //  JSON序列化
        // ========================================================================================

        private byte[] SerializeToJsonBytes(PhysicsSceneData scene)
        {
            string json = SerializeToJsonString(scene);
            return Encoding.UTF8.GetBytes(json);
        }

        private string SerializeToJsonString(PhysicsSceneData scene)
        {
            var sb = new StringBuilder(64 * 1024);
            var fmt = new NumberFormatInfo { NumberDecimalSeparator = "." };

            void WriteFloat(StringBuilder sb, float v)
            {
                sb.Append(v.ToString("F6", fmt));
            }

            void WriteVector3(StringBuilder sb, Vector3 v)
            {
                sb.Append('[');
                WriteFloat(sb, v.X); sb.Append(',');
                WriteFloat(sb, v.Y); sb.Append(',');
                WriteFloat(sb, v.Z);
                sb.Append(']');
            }

            void WriteQuat(StringBuilder sb, Quaternion q)
            {
                sb.Append('[');
                WriteFloat(sb, q.X); sb.Append(',');
                WriteFloat(sb, q.Y); sb.Append(',');
                WriteFloat(sb, q.Z); sb.Append(',');
                WriteFloat(sb, q.W);
                sb.Append(']');
            }

            void WriteString(StringBuilder sb, string? s)
            {
                if (string.IsNullOrEmpty(s))
                {
                    sb.Append("\"\"");
                    return;
                }
                // 转义
                sb.Append('"');
                foreach (char c in s)
                {
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < 32) sb.Append($"\\u{(int)c:X4}");
                            else sb.Append(c);
                            break;
                    }
                }
                sb.Append('"');
            }

            sb.Append('{');

            // 版本和元信息
            sb.Append("\"version\":"); sb.Append(scene.Version); sb.Append(',');
            sb.Append("\"engineName\":"); WriteString(sb, scene.EngineName); sb.Append(',');
            sb.Append("\"createdTime\":\"").Append(scene.CreatedTime.ToString("o")).Append("\",");
            sb.Append("\"gravity\":"); WriteVector3(sb, scene.Gravity); sb.Append(',');
            sb.Append("\"fixedTimestep\":"); WriteFloat(sb, scene.FixedTimestep); sb.Append(',');

            // 材质
            sb.Append("\"materials\":[");
            for (int i = 0; i < scene.Materials.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var m = scene.Materials[i];
                sb.Append('{');
                sb.Append("\"id\":"); sb.Append(m.Id); sb.Append(',');
                sb.Append("\"name\":"); WriteString(sb, m.Name); sb.Append(',');
                sb.Append("\"friction\":"); WriteFloat(sb, m.Friction); sb.Append(',');
                sb.Append("\"restitution\":"); WriteFloat(sb, m.Restitution); sb.Append(',');
                sb.Append("\"density\":"); WriteFloat(sb, m.Density);
                sb.Append('}');
            }
            sb.Append("],");

            // 形状
            sb.Append("\"shapes\":[");
            for (int i = 0; i < scene.Shapes.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var s = scene.Shapes[i];
                sb.Append('{');
                sb.Append("\"id\":"); sb.Append(s.Id); sb.Append(',');
                sb.Append("\"name\":"); WriteString(sb, s.Name); sb.Append(',');
                sb.Append("\"type\":\""); sb.Append(s.Type.ToString()); sb.Append("\",");
                // 根据类型写不同字段
                switch (s.Type)
                {
                    case CollisionShapeType.Box:
                        sb.Append("\"halfExtents\":"); WriteVector3(sb, s.BoxHalfExtents);
                        break;
                    case CollisionShapeType.Sphere:
                        sb.Append("\"radius\":"); WriteFloat(sb, s.SphereRadius);
                        break;
                    case CollisionShapeType.Capsule:
                        sb.Append("\"radius\":"); WriteFloat(sb, s.CapsuleRadius); sb.Append(',');
                        sb.Append("\"halfHeight\":"); WriteFloat(sb, s.CapsuleHalfHeight);
                        break;
                }
                sb.Append('}');
            }
            sb.Append("],");

            // Body
            sb.Append("\"bodies\":[");
            for (int i = 0; i < scene.Bodies.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var b = scene.Bodies[i];
                sb.Append('{');
                sb.Append("\"id\":"); sb.Append(b.Id); sb.Append(',');
                sb.Append("\"name\":"); WriteString(sb, b.Name); sb.Append(',');
                sb.Append("\"type\":\""); sb.Append(b.BodyType.ToString()); sb.Append("\",");
                sb.Append("\"position\":"); WriteVector3(sb, b.Position); sb.Append(',');
                sb.Append("\"orientation\":"); WriteQuat(sb, b.Orientation); sb.Append(',');
                sb.Append("\"mass\":"); WriteFloat(sb, b.Mass); sb.Append(',');
                sb.Append("\"shapeId\":"); sb.Append(b.ShapeId); sb.Append(',');
                sb.Append("\"materialId\":"); sb.Append(b.MaterialId);
                sb.Append('}');
            }
            sb.Append("],");

            // 约束
            sb.Append("\"constraints\":[");
            for (int i = 0; i < scene.Constraints.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var c = scene.Constraints[i];
                sb.Append('{');
                sb.Append("\"id\":"); sb.Append(c.Id); sb.Append(',');
                sb.Append("\"type\":\""); sb.Append(c.Type.ToString()); sb.Append("\",");
                sb.Append("\"bodyA\":"); sb.Append(c.BodyAId); sb.Append(',');
                sb.Append("\"bodyB\":"); sb.Append(c.BodyBId); sb.Append(',');
                sb.Append("\"anchorA\":"); WriteVector3(sb, c.AnchorA); sb.Append(',');
                sb.Append("\"anchorB\":"); WriteVector3(sb, c.AnchorB);
                sb.Append('}');
            }
            sb.Append(']');

            sb.Append('}');
            return sb.ToString();
        }

        private PhysicsSceneData? DeserializeFromJsonBytes(byte[] data)
        {
            // 简化的JSON解析器
            // 实际项目中应该用System.Text.Json或者Newtonsoft.Json
            // 这里为了不依赖外部库，写了个简化版
            string json = Encoding.UTF8.GetString(data);
            return ParseJson(json);
        }

        private PhysicsSceneData? ParseJson(string json)
        {
            // TODO: 实现完整的JSON解析
            // 目前只解析基本字段
            var scene = new PhysicsSceneData();

            // 检测版本
            int verIdx = json.IndexOf("\"version\":");
            if (verIdx >= 0)
            {
                verIdx += 10;
                int end = json.IndexOf(',', verIdx);
                if (end < 0) end = json.IndexOf('}', verIdx);
                if (int.TryParse(json.Substring(verIdx, end - verIdx).Trim(), out int v))
                    scene.Version = v;
            }

            // 解析gravity
            int gravIdx = json.IndexOf("\"gravity\":");
            if (gravIdx >= 0)
            {
                gravIdx += 10;
                int start = json.IndexOf('[', gravIdx);
                int end = json.IndexOf(']', start);
                if (start >= 0 && end > start)
                {
                    string[] parts = json.Substring(start + 1, end - start - 1).Split(',');
                    if (parts.Length == 3 &&
                        float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                        float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
                        float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                    {
                        scene.Gravity = new Vector3(x, y, z);
                    }
                }
            }

            return scene;
        }

        // ========================================================================================
        //  应用到世界
        // ========================================================================================

        /// <summary>把场景数据应用到物理世界。</summary>
        public void ApplySceneToWorld(PhysicsSceneData scene, IPhysicsSerializableWorld world)
        {
            if (scene == null || world == null) return;

            // 清空现有
            world.ClearAll();

            // 设置重力
            world.Gravity = scene.Gravity;

            // 加载材质
            var matIdMap = new Dictionary<int, int>();
            foreach (var mat in scene.Materials)
            {
                int newId = world.AddMaterial(new PhysicsMaterialInfo
                {
                    Id = mat.Id,
                    Name = mat.Name,
                    Friction = mat.Friction,
                    Restitution = mat.Restitution,
                    Density = mat.Density,
                    DebugColor = mat.DebugColor,
                    Properties = mat.Properties,
                });
                matIdMap[mat.Id] = newId;
            }

            // 加载形状
            var shapeIdMap = new Dictionary<int, int>();
            foreach (var shape in scene.Shapes)
            {
                int newId = world.AddShape(DeserializeShapeToInfo(shape));
                shapeIdMap[shape.Id] = newId;
            }

            // 加载body
            var bodyIdMap = new Dictionary<int, int>();
            foreach (var body in scene.Bodies)
            {
                int shapeId = body.ShapeId >= 0 && shapeIdMap.TryGetValue(body.ShapeId, out var sid) ? sid : -1;
                int matId = body.MaterialId >= 0 && matIdMap.TryGetValue(body.MaterialId, out var mid) ? mid : -1;

                var info = new PhysicsBodyInfo
                {
                    Id = body.Id,
                    Name = body.Name,
                    EntityId = body.EntityId,
                    BodyType = body.BodyType,
                    Position = body.Position,
                    Orientation = body.Orientation,
                    LinearVelocity = body.LinearVelocity,
                    AngularVelocity = body.AngularVelocity,
                    Force = body.Force,
                    Torque = body.Torque,
                    Mass = body.Mass,
                    InertiaTensor = body.InertiaTensor,
                    ShapeId = shapeId,
                    MaterialId = matId,
                    LinearDamping = body.LinearDamping,
                    AngularDamping = body.AngularDamping,
                    GravityScale = body.GravityScale,
                    UseCCD = body.UseCCD,
                    IsSleeping = body.IsSleeping,
                    SleepTimer = body.SleepTimer,
                    CollisionLayer = body.CollisionLayer,
                    CollisionMask = body.CollisionMask,
                    IsSensor = body.IsSensor,
                    EnableCollision = body.EnableCollision,
                    CustomProperties = body.CustomProperties,
                };

                int newId = world.AddBody(info);
                bodyIdMap[body.Id] = newId;
            }

            // 加载约束
            foreach (var con in scene.Constraints)
            {
                int bodyA = con.BodyAId >= 0 && bodyIdMap.TryGetValue(con.BodyAId, out var ba) ? ba : -1;
                int bodyB = con.BodyBId >= 0 && bodyIdMap.TryGetValue(con.BodyBId, out var bb) ? bb : -1;

                world.AddConstraint(new PhysicsConstraintInfo
                {
                    Id = con.Id,
                    Name = con.Name,
                    Type = con.Type,
                    BodyAId = bodyA,
                    BodyBId = bodyB,
                    AnchorA = con.AnchorA,
                    AnchorB = con.AnchorB,
                    AxisA = con.AxisA,
                    AxisB = con.AxisB,
                    MinLimit = con.MinLimit,
                    MaxLimit = con.MaxLimit,
                    EnableCollision = con.EnableCollision,
                    BreakThreshold = con.BreakThreshold,
                    Properties = con.Properties,
                });
            }
        }

        private PhysicsShapeInfo DeserializeShapeToInfo(SerializedShape shape)
        {
            var info = new PhysicsShapeInfo
            {
                Id = shape.Id,
                Name = shape.Name,
                Type = shape.Type,
                BoxHalfExtents = shape.BoxHalfExtents,
                SphereRadius = shape.SphereRadius,
                CapsuleRadius = shape.CapsuleRadius,
                CapsuleHalfHeight = shape.CapsuleHalfHeight,
                CylinderRadius = shape.CylinderRadius,
                CylinderHalfHeight = shape.CylinderHalfHeight,
                ConeRadius = shape.ConeRadius,
                ConeHeight = shape.ConeHeight,
                ConvexVertices = shape.ConvexVertices,
                ConvexIndices = shape.ConvexIndices,
                MeshVertices = shape.MeshVertices,
                MeshIndices = shape.MeshIndices,
                LocalOffset = shape.LocalOffset,
                LocalRotation = shape.LocalRotation,
                Density = shape.Density,
            };

            if (shape.Heightfield != null)
            {
                info.Heightfield = new PhysicsHeightfieldInfo
                {
                    GridSizeX = shape.Heightfield.GridSizeX,
                    GridSizeZ = shape.Heightfield.GridSizeZ,
                    SampleSpacing = shape.Heightfield.SampleSpacing,
                    Origin = shape.Heightfield.Origin,
                    Heights = shape.Heightfield.Heights,
                    MaterialIndices = shape.Heightfield.MaterialIndices,
                    MinHeight = shape.Heightfield.MinHeight,
                    MaxHeight = shape.Heightfield.MaxHeight,
                };
            }

            if (shape.CompoundChildren != null)
            {
                info.CompoundChildren = new List<PhysicsSubShapeInfo>();
                foreach (var child in shape.CompoundChildren)
                {
                    info.CompoundChildren.Add(new PhysicsSubShapeInfo
                    {
                        ShapeId = child.ShapeId,
                        Offset = child.Offset,
                        Rotation = child.Rotation,
                    });
                }
            }

            return info;
        }

        // ========================================================================================
        //  压缩
        // ========================================================================================

        private byte[] Compress(byte[] data)
        {
            using var output = new MemoryStream(data.Length / 2);
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
            {
                gzip.Write(data, 0, data.Length);
            }
            return output.ToArray();
        }

        private byte[] Decompress(byte[] data)
        {
            using var input = new MemoryStream(data);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream(data.Length * 4);
            gzip.CopyTo(output);
            return output.ToArray();
        }

        private bool IsCompressed(byte[] data)
        {
            // GZip魔数: 0x1F 0x8B
            return data.Length >= 2 && data[0] == 0x1F && data[1] == 0x8B;
        }

        private bool IsJson(byte[] data)
        {
            if (data.Length == 0) return false;
            // 跳过BOM
            int start = 0;
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
                start = 3;
            // 跳过空白
            while (start < data.Length && char.IsWhiteSpace((char)data[start]))
                start++;
            return start < data.Length && (data[start] == '{' || data[start] == '[');
        }

        // ========================================================================================
        //  验证
        // ========================================================================================

        private (bool valid, string? message) ValidateScene(PhysicsSceneData scene)
        {
            // 检查body的shapeId是否存在
            var shapeIds = new HashSet<int>();
            foreach (var s in scene.Shapes) shapeIds.Add(s.Id);

            foreach (var body in scene.Bodies)
            {
                if (body.ShapeId >= 0 && !shapeIds.Contains(body.ShapeId))
                {
                    // 警告但不阻止
                    // return (false, $"Body {body.Id} 引用了不存在的shape {body.ShapeId}");
                }
                if (body.Mass < 0)
                    return (false, $"Body {body.Id} 质量为负: {body.Mass}");
                if (float.IsNaN(body.Position.X) || float.IsNaN(body.Position.Y) || float.IsNaN(body.Position.Z))
                    return (false, $"Body {body.Id} 位置包含NaN");
            }

            // 检查约束的bodyId
            var bodyIds = new HashSet<int>();
            foreach (var b in scene.Bodies) bodyIds.Add(b.Id);

            foreach (var con in scene.Constraints)
            {
                if (con.BodyAId >= 0 && !bodyIds.Contains(con.BodyAId))
                    return (false, $"Constraint {con.Id} 引用了不存在的body {con.BodyAId}");
                if (con.BodyBId >= 0 && !bodyIds.Contains(con.BodyBId))
                    return (false, $"Constraint {con.Id} 引用了不存在的body {con.BodyBId}");
            }

            return (true, null);
        }

        // ========================================================================================
        //  校验和
        // ========================================================================================

        private int CalculateChecksum(byte[] data)
        {
            // 简单的CRC32-like校验
            int hash = 0;
            for (int i = 0; i < data.Length; i++)
            {
                hash = (hash * 31) ^ data[i];
            }
            return hash;
        }

        // ========================================================================================
        //  场景差异/合并
        // ========================================================================================

        /// <summary>计算两个场景的差异。</summary>
        public SceneDiff ComputeDiff(PhysicsSceneData a, PhysicsSceneData b)
        {
            var diff = new SceneDiff();

            // 找出新增/删除/修改的body
            var bBodies = b.Bodies.ToDictionary(x => x.Id);
            var aBodies = a.Bodies.ToDictionary(x => x.Id);

            foreach (var id in bBodies.Keys.Except(aBodies.Keys))
                diff.AddedBodies.Add(bBodies[id]);
            foreach (var id in aBodies.Keys.Except(bBodies.Keys))
                diff.RemovedBodies.Add(aBodies[id]);
            foreach (var id in aBodies.Keys.Intersect(bBodies.Keys))
            {
                if (!BodiesEqual(aBodies[id], bBodies[id]))
                    diff.ModifiedBodies.Add(bBodies[id]);
            }

            // 同样处理shapes和constraints
            // (省略，逻辑类似)

            return diff;
        }

        private bool BodiesEqual(SerializedBody a, SerializedBody b)
        {
            return a.Position == b.Position &&
                   a.Orientation == b.Orientation &&
                   a.Mass == b.Mass &&
                   a.BodyType == b.BodyType;
        }

        /// <summary>把差异应用到场景。</summary>
        public void ApplyDiff(PhysicsSceneData target, SceneDiff diff)
        {
            // 删除
            foreach (var body in diff.RemovedBodies)
                target.Bodies.RemoveAll(b => b.Id == body.Id);

            // 新增
            foreach (var body in diff.AddedBodies)
                target.Bodies.Add(body);

            // 修改
            foreach (var body in diff.ModifiedBodies)
            {
                int idx = target.Bodies.FindIndex(b => b.Id == body.Id);
                if (idx >= 0)
                    target.Bodies[idx] = body;
            }
        }
    }

    /// <summary>场景差异。</summary>
    public class SceneDiff
    {
        public List<SerializedBody> AddedBodies = new();
        public List<SerializedBody> RemovedBodies = new();
        public List<SerializedBody> ModifiedBodies = new();
        public List<SerializedShape> AddedShapes = new();
        public List<SerializedShape> RemovedShapes = new();
        public List<SerializedConstraint> AddedConstraints = new();
        public List<SerializedConstraint> RemovedConstraints = new();

        public bool HasChanges => AddedBodies.Count > 0 || RemovedBodies.Count > 0 ||
            ModifiedBodies.Count > 0 || AddedShapes.Count > 0 || RemovedShapes.Count > 0 ||
            AddedConstraints.Count > 0 || RemovedConstraints.Count > 0;
    }

    // ============================================================================================
    //  序列化接口 (物理世界需要实现)
    // ============================================================================================

    /// <summary>可序列化的物理世界接口。</summary>
    public interface IPhysicsSerializableWorld
    {
        Vector3 Gravity { get; set; }
        float FixedTimestep { get; }

        IEnumerable<PhysicsBodyInfo> GetBodies();
        IEnumerable<PhysicsShapeInfo> GetShapes();
        IEnumerable<PhysicsMaterialInfo> GetMaterials();
        IEnumerable<PhysicsConstraintInfo> GetConstraints();

        int AddBody(PhysicsBodyInfo info);
        int AddShape(PhysicsShapeInfo info);
        int AddMaterial(PhysicsMaterialInfo info);
        int AddConstraint(PhysicsConstraintInfo info);

        void RemoveBody(int id);
        void RemoveShape(int id);
        void RemoveConstraint(int id);

        void ClearAll();
    }

    /// <summary>刚体类型(和Real/Rigidbody.cs里的一致)。</summary>
    public enum RigidbodyType
    {
        Dynamic,
        Static,
        Kinematic,
    }

    // 引用CollisionShapeType (在CollisionDispatcher.cs里定义)
    // 这里不重复定义，用别名
    // public enum CollisionShapeType { ... }

    /// <summary>物理body信息(序列化用)。</summary>
    public class PhysicsBodyInfo
    {
        public int Id;
        public string? Name;
        public int EntityId;
        public RigidbodyType BodyType = RigidbodyType.Dynamic;
        public Vector3 Position;
        public Quaternion Orientation = Quaternion.Identity;
        public Vector3 LinearVelocity;
        public Vector3 AngularVelocity;
        public Vector3 Force;
        public Vector3 Torque;
        public float Mass = 1f;
        public Matrix4x4 InertiaTensor = Matrix4x4.Identity;
        public int ShapeId = -1;
        public int MaterialId = -1;
        public float LinearDamping = 0.05f;
        public float AngularDamping = 0.25f;
        public float GravityScale = 1f;
        public bool UseCCD;
        public bool IsSleeping;
        public float SleepTimer;
        public ushort CollisionLayer = 1;
        public ushort CollisionMask = 0xFFFF;
        public bool IsSensor;
        public bool EnableCollision = true;
        public Dictionary<string, string> CustomProperties = new();
    }

    /// <summary>物理形状信息(序列化用)。</summary>
    public class PhysicsShapeInfo
    {
        public int Id;
        public string? Name;
        public CollisionShapeType Type;
        public Vector3 BoxHalfExtents = new(0.5f);
        public float SphereRadius = 0.5f;
        public float CapsuleRadius = 0.3f;
        public float CapsuleHalfHeight = 0.5f;
        public float CylinderRadius = 0.3f;
        public float CylinderHalfHeight = 0.5f;
        public float ConeRadius = 0.3f;
        public float ConeHeight = 1f;
        public Vector3[]? ConvexVertices;
        public int[]? ConvexIndices;
        public Vector3[]? MeshVertices;
        public int[]? MeshIndices;
        public PhysicsHeightfieldInfo? Heightfield;
        public List<PhysicsSubShapeInfo>? CompoundChildren;
        public Vector3 LocalOffset;
        public Quaternion LocalRotation = Quaternion.Identity;
        public float Density = 1f;
    }

    /// <summary>子形状信息。</summary>
    public class PhysicsSubShapeInfo
    {
        public int ShapeId;
        public Vector3 Offset;
        public Quaternion Rotation = Quaternion.Identity;
    }

    /// <summary>高度场信息。</summary>
    public class PhysicsHeightfieldInfo
    {
        public int GridSizeX;
        public int GridSizeZ;
        public Vector2 SampleSpacing = Vector2.One;
        public Vector3 Origin;
        public float[] Heights = Array.Empty<float>();
        public byte[]? MaterialIndices;
        public float MinHeight;
        public float MaxHeight;
    }

    /// <summary>物理材质信息。</summary>
    public class PhysicsMaterialInfo
    {
        public int Id;
        public string? Name;
        public float Friction = 0.6f;
        public float Restitution = 0.2f;
        public float Density = 1f;
        public Vector4 DebugColor = new(1, 1, 1, 1);
        public Dictionary<string, string> Properties = new();
    }

    /// <summary>物理约束信息。</summary>
    public class PhysicsConstraintInfo
    {
        public int Id;
        public string? Name;
        public SerializationConstraintType Type;
        public int BodyAId = -1;
        public int BodyBId = -1;
        public Vector3 AnchorA;
        public Vector3 AnchorB;
        public Vector3 AxisA = Vector3.UnitX;
        public Vector3 AxisB = Vector3.UnitX;
        public float MinLimit = float.MinValue;
        public float MaxLimit = float.MaxValue;
        public bool EnableCollision;
        public float BreakThreshold = float.MaxValue;
        public Dictionary<string, string> Properties = new();
    }

    // ============================================================================================
    //  场景导出器 (导出为其他格式)
    // ============================================================================================

    /// <summary>场景导出器。把物理场景导出为各种格式。</summary>
    public class PhysicsSceneExporter
    {
        /// <summary>导出为Bullet的 .bullet 格式(简化版)。</summary>
        public string ExportToBullet(PhysicsSceneData scene)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Bullet Physics scene export");
            sb.AppendLine("# Generated by YamaEngine.Physics");
            sb.AppendLine();

            sb.AppendLine($"# Gravity: {scene.Gravity.X} {scene.Gravity.Y} {scene.Gravity.Z}");
            sb.AppendLine();

            foreach (var body in scene.Bodies)
            {
                sb.AppendLine($"# Body {body.Id}: {body.Name}");
                sb.AppendLine($"body {body.Id}");
                sb.AppendLine($"  type {body.BodyType}");
                sb.AppendLine($"  pos {body.Position.X} {body.Position.Y} {body.Position.Z}");
                sb.AppendLine($"  rot {body.Orientation.X} {body.Orientation.Y} {body.Orientation.Z} {body.Orientation.W}");
                sb.AppendLine($"  mass {body.Mass}");
                sb.AppendLine($"  shape {body.ShapeId}");
                sb.AppendLine($"  material {body.MaterialId}");
                sb.AppendLine($"  layer {body.CollisionLayer}");
                sb.AppendLine($"  ccd {body.UseCCD}");
                sb.AppendLine();
            }

            return sb.ToString();
        }

        /// <summary>导出为OBJ格式(只导出碰撞形状的几何)。</summary>
        public string ExportToObj(PhysicsSceneData scene)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# YamaEngine Physics scene OBJ export");
            int vertexOffset = 0;

            foreach (var shape in scene.Shapes)
            {
                sb.AppendLine($"o Shape_{shape.Id}_{shape.Name}");

                if (shape.Type == CollisionShapeType.Mesh && shape.MeshVertices != null)
                {
                    foreach (var v in shape.MeshVertices)
                    {
                        sb.AppendLine($"v {v.X} {v.Y} {v.Z}");
                    }
                    if (shape.MeshIndices != null)
                    {
                        for (int i = 0; i < shape.MeshIndices.Length; i += 3)
                        {
                            sb.AppendLine($"f {shape.MeshIndices[i] + 1 + vertexOffset} " +
                                          $"{shape.MeshIndices[i + 1] + 1 + vertexOffset} " +
                                          $"{shape.MeshIndices[i + 2] + 1 + vertexOffset}");
                        }
                    }
                    vertexOffset += shape.MeshVertices.Length;
                }
                else if (shape.Type == CollisionShapeType.ConvexHull && shape.ConvexVertices != null)
                {
                    foreach (var v in shape.ConvexVertices)
                    {
                        sb.AppendLine($"v {v.X} {v.Y} {v.Z}");
                    }
                    vertexOffset += shape.ConvexVertices.Length;
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }

        /// <summary>导出为CSV格式(只导出body的位置和速度)。</summary>
        public string ExportToCsv(PhysicsSceneData scene)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Id,Name,Type,PosX,PosY,PosZ,RotX,Roty,RotZ,RotW,VelX,VelY,VelZ,Mass,ShapeId,MaterialId");

            foreach (var body in scene.Bodies)
            {
                sb.AppendLine($"{body.Id},{body.Name},{body.BodyType}," +
                              $"{body.Position.X},{body.Position.Y},{body.Position.Z}," +
                              $"{body.Orientation.X},{body.Orientation.Y},{body.Orientation.Z},{body.Orientation.W}," +
                              $"{body.LinearVelocity.X},{body.LinearVelocity.Y},{body.LinearVelocity.Z}," +
                              $"{body.Mass},{body.ShapeId},{body.MaterialId}");
            }

            return sb.ToString();
        }
    }

    // ============================================================================================
    //  自动保存管理器
    // ============================================================================================

    /// <summary>自动保存管理器。定时保存物理场景。</summary>
    public class PhysicsAutoSaver : IDisposable
    {
        private readonly PhysicsSerializer _serializer;
        private readonly IPhysicsSerializableWorld _world;
        private readonly string _saveDir;
        private readonly float _interval;
        private float _timer;
        private int _saveCount;
        private int _maxSaves = 10;
        private bool _disposed;

        public bool Enable { get; set; } = true;
        public int MaxSaves { get => _maxSaves; set => _maxSaves = Math.Max(1, value); }

        public PhysicsAutoSaver(PhysicsSerializer serializer, IPhysicsSerializableWorld world,
            string saveDir, float intervalSeconds = 60f)
        {
            _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _saveDir = saveDir ?? throw new ArgumentNullException(nameof(saveDir));
            _interval = Math.Max(1f, intervalSeconds);
            _timer = 0;

            if (!Directory.Exists(_saveDir))
                Directory.CreateDirectory(_saveDir);
        }

        /// <summary>每帧更新。</summary>
        public void Update(float dt)
        {
            if (_disposed || !Enable) return;

            _timer += dt;
            if (_timer >= _interval)
            {
                _timer = 0;
                SaveNow();
            }
        }

        /// <summary>立即保存。</summary>
        public SerializationResult SaveNow()
        {
            string filename = $"physics_autosave_{DateTime.Now:yyyyMMdd_HHmmss}.physics";
            string path = Path.Combine(_saveDir, filename);

            var result = _serializer.SaveToFile(_world, path);
            if (result.Success)
            {
                _saveCount++;
                CleanupOldSaves();
            }
            return result;
        }

        /// <summary>清理旧的自动保存。</summary>
        private void CleanupOldSaves()
        {
            try
            {
                var files = Directory.GetFiles(_saveDir, "physics_autosave_*.physics")
                    .OrderByDescending(f => f)
                    .Skip(_maxSaves)
                    .ToArray();

                foreach (var f in files)
                {
                    try { File.Delete(f); } catch { }
                }
            }
            catch { }
        }

        /// <summary>获取最近的保存文件列表。</summary>
        public string[] GetRecentSaves()
        {
            try
            {
                return Directory.GetFiles(_saveDir, "physics_autosave_*.physics")
                    .OrderByDescending(f => f)
                    .ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        public void Dispose()
        {
            _disposed = true;
        }
    }
}
