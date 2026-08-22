using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using ShanyuEngine.ECS;

namespace ShanyuEngine.Gameplay
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - 存档系统 (Gameplay Save System)
    //
    // 本文件实现一套数据驱动的持久化框架, 涵盖:
    //   1. SaveFormat / SaveCompression / SaveSlotStatus  —— 枚举
    //   2. SaveSlot           —— 存档槽位 (名称/时间/缩略图/时长/版本/元数据/状态)
    //   3. SaveData           —— 存档数据 (实体快照/全局变量/玩家状态/时间/场景)
    //   4. EntitySnapshot     —— 实体快照 (ID/名称/变换/组件列表)
    //   5. ComponentSnapshot  —— 组件快照 (类型名/字段映射)
    //   6. ISaveableObject    —— 可存档对象接口 (GetSaveData/LoadFromData)
    //   7. SaveLoadContext    —— 存读档上下文 (ECS 引用/资产解析回调)
    //   8. VersionMigration   —— 版本迁移器 (注册迁移器/逐版本升级)
    //   9. SaveSerializer     —— 序列化器 (二进制/JSON/Gzip/CRC32)
    //  10. SaveSystem         —— 存档系统 (槽位/自动存档/快存快取/校验/列表)
    //
    // 注意: 本命名空间 (ShanyuEngine.Gameplay) 中的类型与 ShanyuEngine.Core 中的
    //       存档类型 (SaveSlotInfo / SaveSerializer / WorldSnapshot 等) 相互独立,
    //       两者位于不同命名空间, 不会冲突. 本系统面向 Gameplay 层的数据驱动存档.
    //
    // 对接系统:
    //   * ECS       : ShanyuEngine.ECS.EntityComponentSystem / IComponent
    //   * Scripting : 通过 ISaveableObject 接口与脚本层对接
    //                 (任意脚本/对象实现该接口即可被纳入存档)
    // ════════════════════════════════════════════════════════════════════════

    // ── 枚举 ──────────────────────────────────────────────────────────────────

    /// <summary>存档序列化格式。</summary>
    public enum SaveFormat
    {
        /// <summary>紧凑二进制 (BinaryWriter / BinaryReader)。</summary>
        Binary = 0,
        /// <summary>JSON 文本 (System.Text.Json)。</summary>
        Json = 1,
        /// <summary>二进制 + Gzip 压缩。</summary>
        CompressedBinary = 2,
    }

    /// <summary>存档压缩算法。</summary>
    public enum SaveCompression
    {
        /// <summary>不压缩。</summary>
        None = 0,
        /// <summary>Gzip 流式压缩。</summary>
        Gzip = 1,
    }

    /// <summary>存档槽位状态。</summary>
    public enum SaveSlotStatus
    {
        /// <summary>空槽位 (无存档文件)。</summary>
        Empty = 0,
        /// <summary>存档有效 (校验通过)。</summary>
        Valid = 1,
        /// <summary>存档损坏 (CRC 校验失败或反序列化异常)。</summary>
        Corrupted = 2,
        /// <summary>存档被锁定 (正在写入)。</summary>
        Locked = 3,
    }

    // ── 数据结构 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 存档槽位元数据。每个存档槽对应一份独立存档, 包含展示与校验信息。
    /// </summary>
    [Serializable]
    public class SaveSlot
    {
        /// <summary>存档显示名称。</summary>
        public string Name = string.Empty;

        /// <summary>最后保存时间 (UTC)。</summary>
        public DateTime Timestamp = DateTime.UtcNow;

        /// <summary>缩略图路径 (存档选择界面展示)。</summary>
        public string ThumbnailPath = string.Empty;

        /// <summary>游戏内累计时长 (秒)。</summary>
        public float PlayDuration;

        /// <summary>存档数据版本号 (用于版本迁移)。</summary>
        public int Version;

        /// <summary>附加元数据 (键值对)。</summary>
        public Dictionary<string, string> Metadata = new();

        /// <summary>槽位状态。</summary>
        public SaveSlotStatus Status = SaveSlotStatus.Empty;
    }

    /// <summary>
    /// 组件快照。记录单个组件的字段映射 (字段名 -> 字符串值)。
    /// </summary>
    [Serializable]
    public class ComponentSnapshot
    {
        /// <summary>组件/对象类型全名。</summary>
        public string TypeName = string.Empty;

        /// <summary>字段映射 (字段名 -> 字符串值)。</summary>
        public Dictionary<string, string> Fields = new();
    }

    /// <summary>
    /// 实体快照。记录单个实体的 ID、名称、变换与其所有组件。
    /// </summary>
    [Serializable]
    public class EntitySnapshot
    {
        /// <summary>实体 ID。</summary>
        public int EntityId;

        /// <summary>实体名称。</summary>
        public string Name = string.Empty;

        /// <summary>世界位置。</summary>
        public Vector3 Position = Vector3.Zero;

        /// <summary>旋转 (四元数)。</summary>
        public Quaternion Rotation = Quaternion.Identity;

        /// <summary>缩放。</summary>
        public Vector3 Scale = new(1f, 1f, 1f);

        /// <summary>组件快照列表。</summary>
        public List<ComponentSnapshot> Components = new();
    }

    /// <summary>
    /// 存档数据。世界实体快照、全局变量、玩家状态、时间与场景信息的总集合。
    /// </summary>
    [Serializable]
    public class SaveData
    {
        /// <summary>存档数据版本号。</summary>
        public int Version;

        /// <summary>场景名称。</summary>
        public string SceneName = string.Empty;

        /// <summary>一天内的时间 (0~24)。</summary>
        public float TimeOfDay;

        /// <summary>本局游戏已流逝时间 (秒)。</summary>
        public float ElapsedTime;

        /// <summary>保存时刻 (UTC)。</summary>
        public DateTime SavedAt = DateTime.UtcNow;

        /// <summary>世界实体快照列表。</summary>
        public List<EntitySnapshot> Entities = new();

        /// <summary>全局游戏变量。</summary>
        public Dictionary<string, string> GlobalVariables = new();

        /// <summary>玩家状态 (键值对)。</summary>
        public Dictionary<string, string> PlayerState = new();

        /// <summary>全局可存档对象数据 (ISaveableObject 产出)。</summary>
        public List<ComponentSnapshot> SaveableObjects = new();
    }

    // ── 接口与委托 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 可存档对象接口。任意需要参与存档的对象 (如游戏管理器、脚本实例) 实现该接口,
    /// 即可由 <see cref="SaveSystem"/> 自动捕获与还原其状态。
    /// </summary>
    public interface ISaveableObject
    {
        /// <summary>采集当前状态为组件快照。</summary>
        ComponentSnapshot GetSaveData();

        /// <summary>从组件快照还原状态。</summary>
        void LoadFromData(ComponentSnapshot data);
    }

    /// <summary>版本迁移委托: 将 <paramref name="data"/> 从某版本升级到下一版本 (返回 null 表示保持原数据)。</summary>
    public delegate SaveData? SaveMigrator(SaveData data, SaveLoadContext context);

    // ── 上下文与迁移 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 存读档上下文。携带 ECS 引用与资产解析回调, 供迁移器与可存档对象使用。
    /// </summary>
    public class SaveLoadContext
    {
        /// <summary>实体组件系统 (可能为 null, 如纯文件校验场景)。</summary>
        public EntityComponentSystem? ECS { get; set; }

        /// <summary>资产解析回调: 输入资产标识 (GUID/路径), 返回运行时资产。</summary>
        public Func<string, object?>? ResolveAsset { get; set; }

        /// <summary>自定义用户数据 (供迁移器传递任意上下文)。</summary>
        public Dictionary<string, object?> UserData { get; } = new();

        public SaveLoadContext() { }

        public SaveLoadContext(EntityComponentSystem? ecs) => ECS = ecs;

        public SaveLoadContext(EntityComponentSystem? ecs, Func<string, object?>? resolveAsset)
        {
            ECS = ecs;
            ResolveAsset = resolveAsset;
        }
    }

    /// <summary>
    /// 版本迁移器。按链式迁移将旧版本 <see cref="SaveData"/> 逐步升级到当前版本。
    /// 每个迁移器负责将 "版本 N" 升级到 "版本 N+1"。
    /// </summary>
    public class VersionMigration
    {
        /// <summary>当前存档数据版本。</summary>
        public static readonly int CurrentVersion = 1;

        private readonly Dictionary<int, SaveMigrator> _migrators = new();

        /// <summary>注册迁移器: 将 <paramref name="fromVersion"/> 升级到 fromVersion+1。</summary>
        public void Register(int fromVersion, SaveMigrator migrator)
        {
            if (migrator == null) throw new ArgumentNullException(nameof(migrator));
            _migrators[fromVersion] = migrator;
        }

        /// <summary>按迁移链将 <paramref name="data"/> 升级到 <see cref="CurrentVersion"/>。</summary>
        public SaveData Migrate(SaveData data, SaveLoadContext context)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (context == null) throw new ArgumentNullException(nameof(context));

            int version = data.Version;
            while (version < CurrentVersion && _migrators.TryGetValue(version, out var migrator))
            {
                data = migrator(data, context) ?? data;
                version++;
                data.Version = version;
            }
            // 即使没有注册迁移器, 也对齐到当前版本号
            if (data.Version < CurrentVersion)
                data.Version = CurrentVersion;
            return data;
        }
    }

    // ── 序列化器 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 存档序列化器。在 <see cref="SaveData"/> 与字节数组之间转换,
    /// 支持二进制 / JSON 两种编码, 可选 Gzip 压缩, 并写入 CRC32 校验。
    /// 文件格式为自描述头 + 负载, 反序列化时自动识别编码与压缩方式。
    /// </summary>
    public class SaveSerializer
    {
        /// <summary>文件头魔数 ("SYGP" 小端)。</summary>
        public const uint Magic = 0x50475953; // 'S','Y','G','P'

        /// <summary>编码格式 (实例序列化时使用)。</summary>
        public SaveFormat Format { get; set; } = SaveFormat.CompressedBinary;

        /// <summary>压缩方式 (实例序列化时使用)。</summary>
        public SaveCompression Compression { get; set; } = SaveCompression.Gzip;

        /// <summary>JSON 选项 (公共字段 + 枚举字符串 + 自定义向量转换器)。</summary>
        public static readonly JsonSerializerOptions JsonOpts = new()
        {
            IncludeFields = true,
            WriteIndented = false,
            Converters = { new JsonStringEnumConverter(), new Vector3Converter(), new QuaternionConverter() },
        };

        private static readonly uint[] Crc32Table = BuildCrc32Table();

        public SaveSerializer() { }

        public SaveSerializer(SaveFormat format, SaveCompression compression)
        {
            Format = format;
            Compression = compression;
        }

        // ── 实例接口 (使用 Format / Compression) ──────────────────────────────

        /// <summary>使用实例配置将 <see cref="SaveData"/> 序列化为带自描述头的字节数组。</summary>
        public byte[] Serialize(SaveData data) => Serialize(data, Format, Compression);

        /// <summary>从带自描述头的字节数组反序列化 (自动识别编码与压缩, 校验 CRC)。</summary>
        public SaveData Deserialize(byte[] raw) => DeserializeCore(raw);

        // ── 静态接口 ──────────────────────────────────────────────────────────

        /// <summary>按指定格式与压缩方式序列化为带自描述头的字节数组。</summary>
        public static byte[] Serialize(SaveData data, SaveFormat format, SaveCompression compression)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));

            bool useJson = format == SaveFormat.Json;
            byte[] payload = useJson ? EncodeJson(data) : EncodeBinary(data);

            bool useGzip = compression == SaveCompression.Gzip || format == SaveFormat.CompressedBinary;
            if (useGzip) payload = Compress(payload);

            using var ms = new MemoryStream(16 + payload.Length);
            using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: false);
            bw.Write(Magic);
            bw.Write((byte)format);
            bw.Write((byte)compression);
            bw.Write(data.Version);
            bw.Write(ComputeCrc32(payload));
            bw.Write(payload.Length);
            bw.Write(payload);
            bw.Flush();
            return ms.ToArray();
        }

        private static SaveData DeserializeCore(byte[] raw)
        {
            if (raw == null) throw new ArgumentNullException(nameof(raw));
            using var ms = new MemoryStream(raw);
            using var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: false);

            uint magic = br.ReadUInt32();
            if (magic != Magic)
                throw new InvalidDataException($"存档魔数不匹配: 0x{magic:X8} (期望 0x{Magic:X8})");

            SaveFormat format = (SaveFormat)br.ReadByte();
            SaveCompression compression = (SaveCompression)br.ReadByte();
            int version = br.ReadInt32();
            uint expectedCrc = br.ReadUInt32();
            int payloadLen = br.ReadInt32();
            byte[] payload = br.ReadBytes(payloadLen);

            uint actualCrc = ComputeCrc32(payload);
            if (actualCrc != expectedCrc)
                throw new InvalidDataException($"存档 CRC 校验失败: 0x{actualCrc:X8} (期望 0x{expectedCrc:X8})");

            bool useGzip = compression == SaveCompression.Gzip || format == SaveFormat.CompressedBinary;
            if (useGzip) payload = Decompress(payload);

            SaveData data = format == SaveFormat.Json ? DecodeJson(payload) : DecodeBinary(payload);
            data.Version = version;
            return data;
        }

        // ── 二进制编码 (无头负载) ─────────────────────────────────────────────

        /// <summary>将 <see cref="SaveData"/> 编码为二进制负载 (无自描述头)。</summary>
        public static byte[] EncodeBinary(SaveData data)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: false);
            WriteBinary(bw, data);
            bw.Flush();
            return ms.ToArray();
        }

        /// <summary>从二进制负载 (无头) 解码 <see cref="SaveData"/>。</summary>
        public static SaveData DecodeBinary(byte[] payload)
        {
            using var ms = new MemoryStream(payload);
            using var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: false);
            return ReadBinary(br);
        }

        private static void WriteBinary(BinaryWriter bw, SaveData data)
        {
            bw.Write(data.Version);
            bw.Write(data.SceneName ?? string.Empty);
            bw.Write(data.TimeOfDay);
            bw.Write(data.ElapsedTime);
            bw.Write(data.SavedAt.Ticks);

            bw.Write(data.Entities.Count);
            foreach (var ent in data.Entities)
            {
                bw.Write(ent.EntityId);
                bw.Write(ent.Name ?? string.Empty);
                WriteVector3(bw, ent.Position);
                WriteQuaternion(bw, ent.Rotation);
                WriteVector3(bw, ent.Scale);
                bw.Write(ent.Components.Count);
                foreach (var comp in ent.Components)
                {
                    bw.Write(comp.TypeName ?? string.Empty);
                    bw.Write(comp.Fields.Count);
                    foreach (var kvp in comp.Fields)
                    {
                        bw.Write(kvp.Key ?? string.Empty);
                        bw.Write(kvp.Value ?? string.Empty);
                    }
                }
            }

            WriteStringMap(bw, data.GlobalVariables);
            WriteStringMap(bw, data.PlayerState);

            bw.Write(data.SaveableObjects.Count);
            foreach (var so in data.SaveableObjects)
            {
                bw.Write(so.TypeName ?? string.Empty);
                bw.Write(so.Fields.Count);
                foreach (var kvp in so.Fields)
                {
                    bw.Write(kvp.Key ?? string.Empty);
                    bw.Write(kvp.Value ?? string.Empty);
                }
            }
        }

        private static SaveData ReadBinary(BinaryReader br)
        {
            var data = new SaveData
            {
                Version = br.ReadInt32(),
                SceneName = br.ReadString(),
                TimeOfDay = br.ReadSingle(),
                ElapsedTime = br.ReadSingle(),
                SavedAt = new DateTime(br.ReadInt64(), DateTimeKind.Utc),
            };

            int entCount = br.ReadInt32();
            for (int i = 0; i < entCount; i++)
            {
                var ent = new EntitySnapshot
                {
                    EntityId = br.ReadInt32(),
                    Name = br.ReadString(),
                    Position = ReadVector3(br),
                    Rotation = ReadQuaternion(br),
                    Scale = ReadVector3(br),
                };
                int compCount = br.ReadInt32();
                for (int j = 0; j < compCount; j++)
                {
                    var comp = new ComponentSnapshot { TypeName = br.ReadString() };
                    int fieldCount = br.ReadInt32();
                    for (int k = 0; k < fieldCount; k++)
                        comp.Fields[br.ReadString()] = br.ReadString();
                    ent.Components.Add(comp);
                }
                data.Entities.Add(ent);
            }

            ReadStringMap(br, data.GlobalVariables);
            ReadStringMap(br, data.PlayerState);

            int soCount = br.ReadInt32();
            for (int i = 0; i < soCount; i++)
            {
                var so = new ComponentSnapshot { TypeName = br.ReadString() };
                int fieldCount = br.ReadInt32();
                for (int k = 0; k < fieldCount; k++)
                    so.Fields[br.ReadString()] = br.ReadString();
                data.SaveableObjects.Add(so);
            }

            return data;
        }

        private static void WriteStringMap(BinaryWriter bw, Dictionary<string, string> map)
        {
            bw.Write(map.Count);
            foreach (var kvp in map)
            {
                bw.Write(kvp.Key ?? string.Empty);
                bw.Write(kvp.Value ?? string.Empty);
            }
        }

        private static void ReadStringMap(BinaryReader br, Dictionary<string, string> map)
        {
            int count = br.ReadInt32();
            for (int i = 0; i < count; i++)
                map[br.ReadString()] = br.ReadString();
        }

        private static void WriteVector3(BinaryWriter bw, Vector3 v)
        {
            bw.Write(v.X); bw.Write(v.Y); bw.Write(v.Z);
        }

        private static Vector3 ReadVector3(BinaryReader br)
            => new(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());

        private static void WriteQuaternion(BinaryWriter bw, Quaternion q)
        {
            bw.Write(q.X); bw.Write(q.Y); bw.Write(q.Z); bw.Write(q.W);
        }

        private static Quaternion ReadQuaternion(BinaryReader br)
            => new(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());

        // ── JSON 编码 (无头负载) ──────────────────────────────────────────────

        /// <summary>将 <see cref="SaveData"/> 编码为 JSON 负载 (UTF-8 字节, 无头)。</summary>
        public static byte[] EncodeJson(SaveData data)
            => JsonSerializer.SerializeToUtf8Bytes(data, JsonOpts);

        /// <summary>从 JSON 负载 (UTF-8 字节, 无头) 解码 <see cref="SaveData"/>。</summary>
        public static SaveData DecodeJson(byte[] payload)
            => JsonSerializer.Deserialize<SaveData>(payload, JsonOpts)!;

        // ── Gzip 压缩 ─────────────────────────────────────────────────────────

        /// <summary>使用 Gzip 压缩字节数据。</summary>
        public static byte[] Compress(byte[] data)
        {
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                gz.Write(data, 0, data.Length);
            return ms.ToArray();
        }

        /// <summary>使用 Gzip 解压字节数据。</summary>
        public static byte[] Decompress(byte[] compressed)
        {
            using var inMs = new MemoryStream(compressed);
            using var gz = new GZipStream(inMs, CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            gz.CopyTo(outMs);
            return outMs.ToArray();
        }

        // ── CRC32 (IEEE 802.3, 多项式 0xEDB88320) ─────────────────────────────

        /// <summary>计算字节数组的 CRC32 校验值。</summary>
        public static uint ComputeCrc32(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            uint crc = 0xFFFFFFFFu;
            for (int i = 0; i < data.Length; i++)
                crc = Crc32Table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }

        private static uint[] BuildCrc32Table()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint crc = i;
                for (int j = 0; j < 8; j++)
                    crc = (crc & 1) != 0 ? (0xEDB88320u ^ (crc >> 1)) : (crc >> 1);
                table[i] = crc;
            }
            return table;
        }

        // ── 向量解析 (供转换器与字段还原共用) ─────────────────────────────────

        /// <summary>解析 "X,Y,Z" 为 <see cref="Vector3"/>。</summary>
        public static Vector3 ParseVector3(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return Vector3.Zero;
            var p = s.Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 3) return Vector3.Zero;
            return new Vector3(
                float.Parse(p[0], CultureInfo.InvariantCulture),
                float.Parse(p[1], CultureInfo.InvariantCulture),
                float.Parse(p[2], CultureInfo.InvariantCulture));
        }

        /// <summary>解析 "X,Y,Z,W" 为 <see cref="Quaternion"/>。</summary>
        public static Quaternion ParseQuaternion(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return Quaternion.Identity;
            var p = s.Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 4) return Quaternion.Identity;
            return new Quaternion(
                float.Parse(p[0], CultureInfo.InvariantCulture),
                float.Parse(p[1], CultureInfo.InvariantCulture),
                float.Parse(p[2], CultureInfo.InvariantCulture),
                float.Parse(p[3], CultureInfo.InvariantCulture));
        }

        /// <summary>解析 "X,Y,Z,W" 为 <see cref="Vector4"/>。</summary>
        public static Vector4 ParseVector4(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return Vector4.Zero;
            var p = s.Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 4) return Vector4.Zero;
            return new Vector4(
                float.Parse(p[0], CultureInfo.InvariantCulture),
                float.Parse(p[1], CultureInfo.InvariantCulture),
                float.Parse(p[2], CultureInfo.InvariantCulture),
                float.Parse(p[3], CultureInfo.InvariantCulture));
        }

        /// <summary>解析 "X,Y" 为 <see cref="Vector2"/>。</summary>
        public static Vector2 ParseVector2(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return Vector2.Zero;
            var p = s.Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 2) return Vector2.Zero;
            return new Vector2(
                float.Parse(p[0], CultureInfo.InvariantCulture),
                float.Parse(p[1], CultureInfo.InvariantCulture));
        }

        // ── JSON 向量转换器 (字符串形式 "X,Y,Z", 紧凑且无歧义) ───────────────

        private sealed class Vector3Converter : JsonConverter<Vector3>
        {
            public override Vector3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                if (reader.TokenType == JsonTokenType.String)
                    return ParseVector3(reader.GetString() ?? string.Empty);
                if (reader.TokenType != JsonTokenType.StartObject)
                    throw new JsonException("无法解析 Vector3");
                float x = 0, y = 0, z = 0;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName) continue;
                    string name = reader.GetString()!;
                    reader.Read();
                    float val = reader.TokenType == JsonTokenType.String
                        ? float.Parse(reader.GetString()!, CultureInfo.InvariantCulture)
                        : reader.GetSingle();
                    switch (name)
                    {
                        case "X": x = val; break;
                        case "Y": y = val; break;
                        case "Z": z = val; break;
                    }
                }
                return new Vector3(x, y, z);
            }

            public override void Write(Utf8JsonWriter writer, Vector3 value, JsonSerializerOptions options)
                => writer.WriteStringValue(
                    string.Create(CultureInfo.InvariantCulture, $"{value.X},{value.Y},{value.Z}"));
        }

        private sealed class QuaternionConverter : JsonConverter<Quaternion>
        {
            public override Quaternion Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                if (reader.TokenType == JsonTokenType.String)
                    return ParseQuaternion(reader.GetString() ?? string.Empty);
                if (reader.TokenType != JsonTokenType.StartObject)
                    throw new JsonException("无法解析 Quaternion");
                float x = 0, y = 0, z = 0, w = 1;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName) continue;
                    string name = reader.GetString()!;
                    reader.Read();
                    float val = reader.TokenType == JsonTokenType.String
                        ? float.Parse(reader.GetString()!, CultureInfo.InvariantCulture)
                        : reader.GetSingle();
                    switch (name)
                    {
                        case "X": x = val; break;
                        case "Y": y = val; break;
                        case "Z": z = val; break;
                        case "W": w = val; break;
                    }
                }
                return new Quaternion(x, y, z, w);
            }

            public override void Write(Utf8JsonWriter writer, Quaternion value, JsonSerializerOptions options)
                => writer.WriteStringValue(
                    string.Create(CultureInfo.InvariantCulture, $"{value.X},{value.Y},{value.Z},{value.W}"));
        }
    }

    // ── 存档系统 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 存档系统。提供槽位存读、自动存档计时器、快存快取、槽位枚举与校验,
    /// 并负责在 <see cref="EntityComponentSystem"/> 与 <see cref="SaveData"/> 之间转换。
    /// </summary>
    public class SaveSystem
    {
        /// <summary>快存快取使用的特殊槽位索引。</summary>
        public const int QuickSaveSlotIndex = -1;

        /// <summary>自动存档默认槽位索引。</summary>
        public const int AutoSaveSlotIndex = 0;

        private readonly SaveSerializer _serializer = new();
        private readonly List<ISaveableObject> _globalSaveables = new();
        private readonly object _ioLock = new();

        private SaveData? _quickSaveCache;
        private float _autoSaveTimer;
        private Func<SaveData>? _autoSaveDataProvider;

        /// <summary>存档根目录。</summary>
        public string SaveDirectory { get; }

        /// <summary>序列化格式。</summary>
        public SaveFormat Format
        {
            get => _serializer.Format;
            set => _serializer.Format = value;
        }

        /// <summary>压缩方式。</summary>
        public SaveCompression Compression
        {
            get => _serializer.Compression;
            set => _serializer.Compression = value;
        }

        /// <summary>版本迁移器。</summary>
        public VersionMigration Migration { get; } = new();

        // ── 自动存档配置 ──────────────────────────────────────────────────────

        /// <summary>是否启用自动存档。</summary>
        public bool AutoSaveEnabled { get; set; }

        /// <summary>自动存档间隔 (秒)。</summary>
        public float AutoSaveInterval { get; set; } = 300f;

        /// <summary>自动存档使用的槽位索引。</summary>
        public int AutoSaveSlot { get; set; } = AutoSaveSlotIndex;

        // ── 事件 ──────────────────────────────────────────────────────────────

        /// <summary>保存完成事件 (参数: 文件路径)。</summary>
        public event Action<string>? OnSaveCompleted;

        /// <summary>加载完成事件 (参数: 文件路径)。</summary>
        public event Action<string>? OnLoadCompleted;

        /// <summary>错误事件 (参数: 操作名, 异常)。</summary>
        public event Action<string, Exception>? OnError;

        public SaveSystem(string saveDirectory)
        {
            SaveDirectory = saveDirectory ?? throw new ArgumentNullException(nameof(saveDirectory));
            Directory.CreateDirectory(saveDirectory);
        }

        public SaveSystem(string saveDirectory, SaveFormat format, SaveCompression compression)
            : this(saveDirectory)
        {
            Format = format;
            Compression = compression;
        }

        // ── 可存档对象注册 ────────────────────────────────────────────────────

        /// <summary>注册全局可存档对象 (游戏管理器等, 按 TypeName 匹配还原)。</summary>
        public void RegisterSaveable(ISaveableObject saveable)
        {
            if (saveable == null) throw new ArgumentNullException(nameof(saveable));
            if (!_globalSaveables.Contains(saveable)) _globalSaveables.Add(saveable);
        }

        /// <summary>取消注册全局可存档对象。</summary>
        public bool UnregisterSaveable(ISaveableObject saveable)
            => saveable != null && _globalSaveables.Remove(saveable);

        // ── ECS 与 SaveData 转换 ──────────────────────────────────────────────

        /// <summary>
        /// 从 ECS 采集一份存档数据。
        /// 每个实体的 Transform 写入 <see cref="EntitySnapshot"/> 变换字段,
        /// 其余已知组件以反射方式采集字段映射, 已注册的 ISaveableObject 采集到 <see cref="SaveData.SaveableObjects"/>。
        /// </summary>
        public SaveData CaptureFromECS(
            EntityComponentSystem ecs,
            string sceneName,
            Dictionary<string, string>? globalVariables = null,
            Dictionary<string, string>? playerState = null,
            float timeOfDay = 0f,
            float elapsedTime = 0f)
        {
            if (ecs == null) throw new ArgumentNullException(nameof(ecs));

            var data = new SaveData
            {
                Version = VersionMigration.CurrentVersion,
                SceneName = sceneName ?? string.Empty,
                TimeOfDay = timeOfDay,
                ElapsedTime = elapsedTime,
                SavedAt = DateTime.UtcNow,
            };

            if (globalVariables != null)
                foreach (var kvp in globalVariables) data.GlobalVariables[kvp.Key] = kvp.Value;
            if (playerState != null)
                foreach (var kvp in playerState) data.PlayerState[kvp.Key] = kvp.Value;

            foreach (int eid in ecs.AllEntities)
            {
                var tf = ecs.GetComponent<TransformComponent>(eid);
                var entSnap = new EntitySnapshot
                {
                    EntityId = eid,
                    Name = ecs.GetEntityName(eid),
                    Position = tf.Position,
                    Rotation = tf.Rotation,
                    Scale = tf.Scale,
                };

                foreach (var type in ComponentTypes)
                {
                    if (type == typeof(TransformComponent)) continue; // 已写入变换字段
                    var compSnap = CaptureComponent(ecs, eid, type);
                    if (compSnap != null) entSnap.Components.Add(compSnap);
                }
                data.Entities.Add(entSnap);
            }

            foreach (var saveable in _globalSaveables)
            {
                try
                {
                    var snap = saveable.GetSaveData();
                    if (snap == null) continue;
                    if (string.IsNullOrEmpty(snap.TypeName))
                        snap.TypeName = saveable.GetType().FullName;
                    data.SaveableObjects.Add(snap);
                }
                catch (Exception ex) { OnError?.Invoke(nameof(CaptureFromECS), ex); }
            }

            return data;
        }

        /// <summary>
        /// 将存档数据还原到 ECS。
        /// 重新创建实体并建立 旧ID -> 新ID 映射, 还原组件, 修正父子关系, 还原全局可存档对象。
        /// </summary>
        /// <param name="data">存档数据 (会先执行版本迁移, 已为当前版本时为空操作)。</param>
        /// <param name="ecs">目标 ECS。</param>
        /// <param name="clearExisting">是否先清空 ECS。</param>
        public void RestoreToECS(SaveData data, EntityComponentSystem ecs, bool clearExisting = true)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (ecs == null) throw new ArgumentNullException(nameof(ecs));

            data = Migration.Migrate(data, new SaveLoadContext(ecs));

            if (clearExisting) ecs.Clear();

            var idMap = new Dictionary<int, int>();

            // 阶段 1: 创建实体 (CreateEntity 会自动附加 TransformComponent)
            foreach (var entSnap in data.Entities)
            {
                int newId = ecs.CreateEntity(
                    entSnap.Name,
                    entSnap.Position,
                    entSnap.Rotation,
                    entSnap.Scale);
                idMap[entSnap.EntityId] = newId;
            }

            // 阶段 2: 还原其余组件
            foreach (var entSnap in data.Entities)
            {
                if (!idMap.TryGetValue(entSnap.EntityId, out int newId)) continue;
                foreach (var compSnap in entSnap.Components)
                    RestoreComponent(ecs, newId, compSnap);
            }

            // 阶段 3: 修正 RelationshipComponent 的父子 ID 引用
            FixupRelationships(ecs, data, idMap);

            // 阶段 4: 还原全局可存档对象
            RestoreSaveables(data);
        }

        private ComponentSnapshot? CaptureComponent(EntityComponentSystem ecs, int entityId, Type type)
        {
            var hasMethod = HasComponentGeneric.MakeGenericMethod(type);
            if (!(bool)hasMethod.Invoke(ecs, new object[] { entityId })!) return null;

            var getMethod = GetComponentGeneric.MakeGenericMethod(type);
            object comp = getMethod.Invoke(ecs, new object[] { entityId })!;

            var fields = new Dictionary<string, string>();
            foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                fields[f.Name] = FormatValue(f.GetValue(comp), f.FieldType);

            return new ComponentSnapshot { TypeName = type.FullName!, Fields = fields };
        }

        private void RestoreComponent(EntityComponentSystem ecs, int entityId, ComponentSnapshot snap)
        {
            Type? type = ResolveComponentType(snap.TypeName);
            if (type == null) return; // 未知类型, 跳过

            object comp = Activator.CreateInstance(type)!;
            foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!snap.Fields.TryGetValue(f.Name, out string? val)) continue;
                object? converted = ParseFieldValue(val, f.FieldType);
                if (converted != null) f.SetValue(comp, converted);
            }

            var addMethod = AddComponentGeneric.MakeGenericMethod(type);
            addMethod.Invoke(ecs, new object[] { entityId, comp });
        }

        private static void FixupRelationships(EntityComponentSystem ecs, SaveData data, Dictionary<int, int> idMap)
        {
            foreach (var entSnap in data.Entities)
            {
                if (!idMap.TryGetValue(entSnap.EntityId, out int newId)) continue;
                if (!ecs.HasComponent<RelationshipComponent>(newId)) continue;

                var rel = ecs.GetComponent<RelationshipComponent>(newId);
                // 0 是 "无父实体" 的哨兵值 (RelationshipComponent 为结构体, ParentId 默认 0; 实体 ID 从 1 开始)
                rel.ParentId = rel.ParentId <= 0 ? 0
                    : (idMap.TryGetValue(rel.ParentId, out int newParent) ? newParent : 0);

                if (rel.Children != null)
                {
                    var remapped = new List<int>(rel.Children.Count);
                    foreach (int child in rel.Children)
                        if (idMap.TryGetValue(child, out int newChild)) remapped.Add(newChild);
                    rel.Children = remapped;
                }
                ecs.SetComponent(newId, rel);
            }
        }

        private void RestoreSaveables(SaveData data)
        {
            if (data.SaveableObjects.Count == 0 || _globalSaveables.Count == 0) return;

            // 按类型名取首份快照 (一一匹配; 同类型多实例取第一个)
            var byType = new Dictionary<string, ComponentSnapshot>();
            foreach (var s in data.SaveableObjects)
                if (!byType.ContainsKey(s.TypeName)) byType[s.TypeName] = s;

            foreach (var saveable in _globalSaveables)
            {
                string key = saveable.GetType().FullName ?? saveable.GetType().Name;
                if (!byType.TryGetValue(key, out var snap)) continue;
                try { saveable.LoadFromData(snap); }
                catch (Exception ex) { OnError?.Invoke(nameof(RestoreSaveables), ex); }
            }
        }

        // ── 文件存读 ──────────────────────────────────────────────────────────

        /// <summary>保存到任意文件路径。</summary>
        public void Save(SaveData data, string filePath)
        {
            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            byte[] raw = _serializer.Serialize(data);
            lock (_ioLock) File.WriteAllBytes(filePath, raw);
            OnSaveCompleted?.Invoke(filePath);
        }

        /// <summary>从任意文件路径加载 (自动迁移)。文件不存在返回 null。</summary>
        public SaveData? Load(string filePath)
        {
            if (!File.Exists(filePath)) return null;
            byte[] raw;
            lock (_ioLock) raw = File.ReadAllBytes(filePath);

            SaveData data = _serializer.Deserialize(raw);
            data = Migration.Migrate(data, new SaveLoadContext());
            OnLoadCompleted?.Invoke(filePath);
            return data;
        }

        // ── 槽位存读 ──────────────────────────────────────────────────────────

        /// <summary>获取槽位数据文件路径。</summary>
        public string GetSlotPath(int slotIndex)
            => Path.Combine(SaveDirectory, SlotPrefix(slotIndex) + ".dat");

        /// <summary>获取槽位元数据文件路径。</summary>
        public string GetSlotMetaPath(int slotIndex)
            => Path.Combine(SaveDirectory, SlotPrefix(slotIndex) + ".meta.json");

        /// <summary>获取槽位锁文件路径。</summary>
        public string GetSlotLockPath(int slotIndex)
            => Path.Combine(SaveDirectory, SlotPrefix(slotIndex) + ".lock");

        /// <summary>保存到指定槽位 (同时写入数据与元数据)。</summary>
        /// <param name="slotIndex">槽位索引 (负数表示快存快取槽)。</param>
        /// <param name="data">存档数据。</param>
        /// <param name="thumbnailPath">缩略图路径 (可选)。</param>
        /// <param name="displayName">显示名称 (可选)。</param>
        public void SaveToSlot(int slotIndex, SaveData data, string? thumbnailPath = null, string? displayName = null)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));

            string dataPath = GetSlotPath(slotIndex);
            string metaPath = GetSlotMetaPath(slotIndex);
            string lockPath = GetSlotLockPath(slotIndex);

            lock (_ioLock)
            {
                File.WriteAllText(lockPath, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                try
                {
                    byte[] raw = _serializer.Serialize(data);
                    File.WriteAllBytes(dataPath, raw);

                    var slot = new SaveSlot
                    {
                        Name = displayName ?? $"{data.SceneName} - Slot {slotIndex}",
                        Timestamp = data.SavedAt,
                        ThumbnailPath = thumbnailPath ?? string.Empty,
                        PlayDuration = data.ElapsedTime,
                        Version = data.Version,
                        Status = SaveSlotStatus.Valid,
                    };
                    slot.Metadata["SceneName"] = data.SceneName;
                    slot.Metadata["EntityCount"] = data.Entities.Count.ToString(CultureInfo.InvariantCulture);
                    slot.Metadata["SlotIndex"] = slotIndex.ToString(CultureInfo.InvariantCulture);
                    foreach (var kvp in data.GlobalVariables) slot.Metadata[kvp.Key] = kvp.Value;

                    File.WriteAllText(metaPath, JsonSerializer.Serialize(slot, SaveSerializer.JsonOpts));
                    OnSaveCompleted?.Invoke(dataPath);
                }
                finally
                {
                    if (File.Exists(lockPath)) File.Delete(lockPath);
                }
            }
        }

        /// <summary>从指定槽位加载 (自动迁移)。槽位不存在返回 null。</summary>
        public SaveData? LoadFromSlot(int slotIndex)
        {
            string dataPath = GetSlotPath(slotIndex);
            if (!File.Exists(dataPath)) return null;

            byte[] raw;
            lock (_ioLock) raw = File.ReadAllBytes(dataPath);

            SaveData data = _serializer.Deserialize(raw);
            data = Migration.Migrate(data, new SaveLoadContext());
            OnLoadCompleted?.Invoke(dataPath);
            return data;
        }

        /// <summary>删除指定槽位的存档 (数据与元数据)。</summary>
        public bool DeleteSlot(int slotIndex)
        {
            string dataPath = GetSlotPath(slotIndex);
            string metaPath = GetSlotMetaPath(slotIndex);
            string lockPath = GetSlotLockPath(slotIndex);
            bool removed = false;
            lock (_ioLock)
            {
                if (File.Exists(dataPath)) { File.Delete(dataPath); removed = true; }
                if (File.Exists(metaPath)) File.Delete(metaPath);
                if (File.Exists(lockPath)) File.Delete(lockPath);
            }
            return removed;
        }

        /// <summary>枚举所有槽位的元数据 (不含快存快取槽)。</summary>
        public List<SaveSlot> ListSlots()
        {
            var list = new List<SaveSlot>();
            if (!Directory.Exists(SaveDirectory)) return list;

            foreach (var metaPath in Directory.EnumerateFiles(SaveDirectory, "slot_*.meta.json"))
            {
                try
                {
                    string json = File.ReadAllText(metaPath);
                    var slot = JsonSerializer.Deserialize<SaveSlot>(json, SaveSerializer.JsonOpts);
                    if (slot != null)
                    {
                        if (slot.Status == SaveSlotStatus.Empty) slot.Status = SaveSlotStatus.Valid;
                        list.Add(slot);
                    }
                }
                catch
                {
                    // 损坏的元数据
                    string file = Path.GetFileName(metaPath);              // slot_000.meta.json
                    string stem = Path.GetFileNameWithoutExtension(file);  // slot_000.meta
                    stem = Path.GetFileNameWithoutExtension(stem);         // slot_000
                    list.Add(new SaveSlot { Name = stem, Status = SaveSlotStatus.Corrupted });
                }
            }
            return list;
        }

        /// <summary>
        /// 校验指定槽位: 尝试反序列化并检查 CRC, 返回带状态的 <see cref="SaveSlot"/>。
        /// </summary>
        public SaveSlot ValidateSlot(int slotIndex)
        {
            string dataPath = GetSlotPath(slotIndex);
            string lockPath = GetSlotLockPath(slotIndex);
            string metaPath = GetSlotMetaPath(slotIndex);

            if (File.Exists(lockPath))
                return new SaveSlot { Name = $"Slot {slotIndex}", Status = SaveSlotStatus.Locked };

            if (!File.Exists(dataPath))
                return new SaveSlot { Name = $"Slot {slotIndex}", Status = SaveSlotStatus.Empty };

            try
            {
                byte[] raw;
                lock (_ioLock) raw = File.ReadAllBytes(dataPath);
                _serializer.Deserialize(raw); // 触发 CRC 校验与解码

                SaveSlot slot;
                if (File.Exists(metaPath))
                {
                    slot = JsonSerializer.Deserialize<SaveSlot>(
                        File.ReadAllText(metaPath), SaveSerializer.JsonOpts) ?? new SaveSlot();
                }
                else
                {
                    slot = new SaveSlot { Name = $"Slot {slotIndex}" };
                }
                slot.Status = SaveSlotStatus.Valid;
                return slot;
            }
            catch
            {
                return new SaveSlot { Name = $"Slot {slotIndex}", Status = SaveSlotStatus.Corrupted };
            }
        }

        // ── 快存快取 ──────────────────────────────────────────────────────────

        /// <summary>快速保存 (内存缓存 + 落盘到快存槽)。</summary>
        public void QuickSave(SaveData data, string? thumbnailPath = null)
        {
            _quickSaveCache = data;
            try { SaveToSlot(QuickSaveSlotIndex, data, thumbnailPath, "Quick Save"); }
            catch (Exception ex) { OnError?.Invoke(nameof(QuickSave), ex); }
        }

        /// <summary>快速加载 (优先内存缓存, 其次快存槽)。</summary>
        public SaveData? QuickLoad()
        {
            if (_quickSaveCache != null) return _quickSaveCache;
            return LoadFromSlot(QuickSaveSlotIndex);
        }

        /// <summary>清除快存缓存。</summary>
        public void ClearQuickSaveCache() => _quickSaveCache = null;

        // ── 自动存档 ──────────────────────────────────────────────────────────

        /// <summary>启用自动存档。</summary>
        /// <param name="intervalSeconds">存档间隔 (秒)。</param>
        /// <param name="dataProvider">提供当前存档数据的回调。</param>
        /// <param name="slotIndex">自动存档槽位。</param>
        public void EnableAutoSave(float intervalSeconds, Func<SaveData> dataProvider, int slotIndex = AutoSaveSlotIndex)
        {
            if (dataProvider == null) throw new ArgumentNullException(nameof(dataProvider));
            AutoSaveEnabled = true;
            AutoSaveInterval = intervalSeconds;
            AutoSaveSlot = slotIndex;
            _autoSaveDataProvider = dataProvider;
            _autoSaveTimer = 0f;
        }

        /// <summary>禁用自动存档。</summary>
        public void DisableAutoSave()
        {
            AutoSaveEnabled = false;
            _autoSaveDataProvider = null;
        }

        /// <summary>每帧更新: 推进自动存档计时器, 到时触发存档。</summary>
        public void Update(float dt)
        {
            if (!AutoSaveEnabled || _autoSaveDataProvider == null) return;
            _autoSaveTimer += dt;
            if (_autoSaveTimer < AutoSaveInterval) return;

            _autoSaveTimer = 0f;
            try
            {
                SaveData? data = _autoSaveDataProvider();
                if (data != null) SaveToSlot(AutoSaveSlot, data, displayName: "Auto Save");
            }
            catch (Exception ex) { OnError?.Invoke(nameof(Update), ex); }
        }

        // ── 异步包装 ──────────────────────────────────────────────────────────

        /// <summary>异步保存到文件。</summary>
        public Task SaveAsync(string filePath, SaveData data)
            => Task.Run(() => Save(data, filePath));

        /// <summary>异步从文件加载。</summary>
        public Task<SaveData?> LoadAsync(string filePath)
            => Task.Run(() => Load(filePath));

        /// <summary>异步保存到槽位。</summary>
        public Task SaveToSlotAsync(int slotIndex, SaveData data, string? thumbnailPath = null, string? displayName = null)
            => Task.Run(() => SaveToSlot(slotIndex, data, thumbnailPath, displayName));

        /// <summary>异步从槽位加载。</summary>
        public Task<SaveData?> LoadFromSlotAsync(int slotIndex)
            => Task.Run(() => LoadFromSlot(slotIndex));

        // ── 辅助: 字段格式化与解析 ────────────────────────────────────────────

        private static string FormatValue(object? val, Type fieldType)
        {
            if (val == null) return string.Empty;
            if (fieldType == typeof(Vector3))
            {
                var v = (Vector3)val;
                return string.Create(CultureInfo.InvariantCulture, $"{v.X},{v.Y},{v.Z}");
            }
            if (fieldType == typeof(Quaternion))
            {
                var q = (Quaternion)val;
                return string.Create(CultureInfo.InvariantCulture, $"{q.X},{q.Y},{q.Z},{q.W}");
            }
            if (fieldType == typeof(Vector4))
            {
                var v4 = (Vector4)val;
                return string.Create(CultureInfo.InvariantCulture, $"{v4.X},{v4.Y},{v4.Z},{v4.W}");
            }
            if (fieldType == typeof(Vector2))
            {
                var v2 = (Vector2)val;
                return string.Create(CultureInfo.InvariantCulture, $"{v2.X},{v2.Y}");
            }
            if (fieldType == typeof(Guid)) return ((Guid)val).ToString("N");
            if (fieldType == typeof(List<int>)) return string.Join(",", (List<int>)val);
            if (fieldType.IsEnum) return val.ToString() ?? string.Empty;
            if (fieldType == typeof(bool)) return (bool)val ? "true" : "false";
            if (fieldType.IsPrimitive || fieldType == typeof(decimal))
                return Convert.ToString(val, CultureInfo.InvariantCulture) ?? string.Empty;
            if (fieldType == typeof(string)) return (string)val;
            return val.ToString() ?? string.Empty;
        }

        private static object? ParseFieldValue(string str, Type fieldType)
        {
            if (fieldType == typeof(string)) return str;
            if (fieldType == typeof(Vector3)) return SaveSerializer.ParseVector3(str);
            if (fieldType == typeof(Quaternion)) return SaveSerializer.ParseQuaternion(str);
            if (fieldType == typeof(Vector4)) return SaveSerializer.ParseVector4(str);
            if (fieldType == typeof(Vector2)) return SaveSerializer.ParseVector2(str);
            if (fieldType == typeof(Guid)) return Guid.TryParse(str, out var g) ? g : Guid.Empty;
            if (fieldType == typeof(List<int>))
            {
                return str.Split(',', StringSplitOptions.RemoveEmptyEntries)
                          .Select(s => int.Parse(s, CultureInfo.InvariantCulture))
                          .ToList();
            }
            if (fieldType.IsEnum) return Enum.Parse(fieldType, str, ignoreCase: true);
            if (fieldType == typeof(bool))
                return str.Equals("true", StringComparison.OrdinalIgnoreCase);
            if (fieldType == typeof(float)) return float.Parse(str, CultureInfo.InvariantCulture);
            if (fieldType == typeof(double)) return double.Parse(str, CultureInfo.InvariantCulture);
            if (fieldType == typeof(int)) return int.Parse(str, CultureInfo.InvariantCulture);
            if (fieldType == typeof(uint)) return uint.Parse(str, CultureInfo.InvariantCulture);
            if (fieldType == typeof(long)) return long.Parse(str, CultureInfo.InvariantCulture);
            if (fieldType == typeof(decimal)) return decimal.Parse(str, CultureInfo.InvariantCulture);
            return Convert.ChangeType(str, fieldType, CultureInfo.InvariantCulture);
        }

        // ── 辅助: ECS 反射缓存与类型表 ────────────────────────────────────────

        private static readonly Type[] ComponentTypes =
        {
            typeof(TransformComponent),
            typeof(MeshComponent),
            typeof(LightComponent),
            typeof(CameraComponent),
            typeof(TagComponent),
            typeof(RelationshipComponent),
        };

        private static readonly Dictionary<string, Type> ComponentTypeMap = BuildComponentTypeMap();

        private static Dictionary<string, Type> BuildComponentTypeMap()
        {
            var map = new Dictionary<string, Type>();
            foreach (var t in ComponentTypes)
            {
                map[t.FullName!] = t;
                map[t.Name] = t;
            }
            return map;
        }

        private static Type? ResolveComponentType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;
            if (ComponentTypeMap.TryGetValue(typeName, out var t)) return t;
            return Type.GetType(typeName);
        }

        private static readonly MethodInfo HasComponentGeneric =
            (from m in typeof(EntityComponentSystem).GetMethods()
             where m.Name == nameof(EntityComponentSystem.HasComponent) && m.IsGenericMethod
             select m).First();

        private static readonly MethodInfo GetComponentGeneric =
            (from m in typeof(EntityComponentSystem).GetMethods()
             where m.Name == nameof(EntityComponentSystem.GetComponent) && m.IsGenericMethod
             select m).First();

        private static readonly MethodInfo AddComponentGeneric =
            (from m in typeof(EntityComponentSystem).GetMethods()
             where m.Name == nameof(EntityComponentSystem.AddComponent) && m.IsGenericMethod
                  && m.GetParameters().Length == 2
             select m).First();

        private static string SlotPrefix(int slotIndex)
            => slotIndex == QuickSaveSlotIndex ? "quick_save" : $"slot_{slotIndex:D3}";
    }
}
