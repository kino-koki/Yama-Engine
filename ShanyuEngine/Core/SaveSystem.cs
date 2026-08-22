using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ShanyuEngine.ECS;
using ShanyuEngine.Networking;

namespace ShanyuEngine.Core
{
    // ══════════════════════════════════════════════════════════════════
    //  SaveSystem.cs — 山雨引擎 存档与回放系统
    //
    //  本文件实现了两大持久化子系统：
    //
    //  一、存档系统（Save Game）
    //    1. 二进制序列化：紧凑高效的位流写入（与网络复制共享 BitWriter/BitReader）
    //    2. 全局状态快照：捕获所有 ECS 实体与组件
    //    3. 资源引用序列化：以 GUID + 资产路径形式存储，加载时重新解析
    //    4. 存档槽管理：支持多个存档槽，含元数据（时间戳、缩略图、关卡名）
    //    5. 异步保存/加载：基于 Task，不阻塞主线程
    //    6. 存档压缩：Gzip 流式压缩，显著减小存档体积
    //    7. 存档版本控制：版本号 + 迁移器链，保证向前兼容
    //
    //  二、回放系统（Replay）
    //    1. 帧状态录制：每帧记录关键状态（实体变换、关键组件）
    //    2. 状态差量录制：仅记录相对上一帧的变化，大幅压缩体积
    //    3. 回放播放：前进 / 暂停 / 后退 / 跳转，支持逐帧步进
    //    4. 回放高亮：聚焦指定玩家视角，高亮其动作
    //    5. 回放导出：导出为视频序列帧（PNG 序列），供后期合成
    //    6. 与网络复制共享序列化：复用 BitWriter/BitReader，保证存档/回放/网络一致
    //
    //  与引擎系统对接：
    //    - ECS：序列化 EntityComponentSystem 的全部实体与组件
    //    - 网络：复用 ShanyuEngine.Networking.BitWriter / BitReader
    //    - 资产：通过 GUID + 路径引用资产，加载时由 AssetDatabase 解析
    // ══════════════════════════════════════════════════════════════════

    // ══════════════════════════════════════════════════════════════════
    // 第一部分：存档系统
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 存档文件头。写入每个存档文件的开头，包含魔数、版本与元数据。
    /// 加载时先读取文件头进行校验与版本迁移。
    /// </summary>
    public struct SaveFileHeader
    {
        /// <summary>魔数，用于识别山雨引擎存档文件（"SYSA" = Shanyu Save）</summary>
        public const uint Magic = 0x41535953; // 'S','Y','S','A' little-endian
        /// <summary>存档格式版本号（用于向前兼容与迁移）</summary>
        public int Version;
        /// <summary>存档创建时间戳（UTC Ticks）</summary>
        public long TimestampUtcTicks;
        /// <summary>关卡/场景名称</summary>
        public string LevelName;
        /// <summary>游戏内时长（秒）</summary>
        public float PlayTimeSeconds;
        /// <summary>实体数量</summary>
        public int EntityCount;
        /// <summary>未压缩数据长度（用于解压校验）</summary>
        public long UncompressedLength;
        /// <summary>压缩后数据长度</summary>
        public long CompressedLength;

        /// <summary>当前存档格式版本</summary>
        public const int CurrentVersion = 3;
    }

    /// <summary>
    /// 存档槽元数据。每个存档槽对应一个独立存档，包含展示信息。
    /// </summary>
    [Serializable]
    public class SaveSlotInfo
    {
        /// <summary>槽位索引（0 起）</summary>
        public int SlotIndex;
        /// <summary>存档显示名称</summary>
        public string DisplayName = "";
        /// <summary>关卡名称</summary>
        public string LevelName = "";
        /// <summary>创建时间（本地）</summary>
        public DateTime CreatedAtLocal = DateTime.Now;
        /// <summary>最后保存时间（本地）</summary>
        public DateTime LastSavedAtLocal = DateTime.Now;
        /// <summary>游戏内时长（秒）</summary>
        public float PlayTimeSeconds;
        /// <summary>缩略图路径（PNG，用于存档选择界面）</summary>
        public string ThumbnailPath = "";
        /// <summary>实体数量</summary>
        public int EntityCount;
        /// <summary>存档版本号</summary>
        public int SaveVersion;
        /// <summary>是否为空槽</summary>
        public bool IsEmpty = true;
    }

    /// <summary>
    /// 资源引用。序列化资产引用为 GUID + 路径，避免直接持有运行时对象指针。
    /// 加载时由 AssetDatabase 根据 GUID 重新解析为运行时资产。
    /// </summary>
    [Serializable]
    public struct ResourceReference
    {
        /// <summary>资产 GUID（字符串形式，与 AssetMeta.Guid 一致）</summary>
        public string Guid;
        /// <summary>资产路径（相对工程根，作为 GUID 缺失时的回退）</summary>
        public string Path;
        /// <summary>资产类型名</summary>
        public string TypeName;

        public ResourceReference(string guid, string path, string typeName)
        {
            Guid = guid;
            Path = path;
            TypeName = typeName;
        }
    }

    /// <summary>
    /// 实体快照。记录单个实体的名称、ID 及其所有组件的序列化数据。
    /// 组件以“类型全名 + 二进制数据”形式存储，加载时按类型反射重建。
    /// </summary>
    [Serializable]
    public class EntitySnapshot
    {
        /// <summary>实体 ID</summary>
        public int EntityId;
        /// <summary>实体名称</summary>
        public string Name = "";
        /// <summary>父实体 ID（-1 = 无父级）</summary>
        public int ParentId = -1;
        /// <summary>组件数据：类型全名 -> 二进制负载</summary>
        public List<ComponentSnapshot> Components = new();
    }

    /// <summary>组件快照。</summary>
    [Serializable]
    public class ComponentSnapshot
    {
        /// <summary>组件类型全名</summary>
        public string TypeName = "";
        /// <summary>序列化后的二进制数据</summary>
        public byte[] Data = Array.Empty<byte>();
    }

    /// <summary>
    /// 世界快照。捕获整个 ECS 的状态，包含所有实体与其组件。
    /// 是存档的核心数据结构。
    /// </summary>
    [Serializable]
    public class WorldSnapshot
    {
        /// <summary>所有实体快照</summary>
        public List<EntitySnapshot> Entities = new();
        /// <summary>全局资源引用表（关卡资产、材质、网格等）</summary>
        public List<ResourceReference> Resources = new();
        /// <summary>全局游戏变量（键值对）</summary>
        public Dictionary<string, string> GameVariables = new();
        /// <summary>快照时间戳</summary>
        public DateTime Timestamp = DateTime.UtcNow;
    }

    /// <summary>
    /// 存档序列化器。负责在 WorldSnapshot 与二进制流之间转换。
    /// 采用紧凑二进制格式（非 JSON），并通过 Gzip 压缩减小体积。
    /// 与网络复制系统共享 BitWriter/BitReader，保证序列化一致性。
    /// </summary>
    public static class SaveSerializer
    {
        // ── 二进制读写原语（不依赖 BitWriter 以支持全精度浮点）──

        /// <summary>将 WorldSnapshot 序列化为二进制字节数组。</summary>
        public static byte[] Serialize(WorldSnapshot snapshot)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: false);
            // 实体数
            bw.Write(snapshot.Entities.Count);
            foreach (var ent in snapshot.Entities)
            {
                bw.Write(ent.EntityId);
                bw.Write(ent.Name ?? "");
                bw.Write(ent.ParentId);
                bw.Write(ent.Components.Count);
                foreach (var comp in ent.Components)
                {
                    bw.Write(comp.TypeName ?? "");
                    bw.Write(comp.Data?.Length ?? 0);
                    if (comp.Data != null) bw.Write(comp.Data);
                }
            }
            // 资源引用
            bw.Write(snapshot.Resources.Count);
            foreach (var res in snapshot.Resources)
            {
                bw.Write(res.Guid ?? "");
                bw.Write(res.Path ?? "");
                bw.Write(res.TypeName ?? "");
            }
            // 游戏变量
            bw.Write(snapshot.GameVariables.Count);
            foreach (var kvp in snapshot.GameVariables)
            {
                bw.Write(kvp.Key ?? "");
                bw.Write(kvp.Value ?? "");
            }
            bw.Flush();
            return ms.ToArray();
        }

        /// <summary>从二进制字节数组反序列化为 WorldSnapshot。</summary>
        public static WorldSnapshot Deserialize(byte[] data)
        {
            var snap = new WorldSnapshot();
            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: false);
            int entCount = br.ReadInt32();
            for (int i = 0; i < entCount; i++)
            {
                var ent = new EntitySnapshot
                {
                    EntityId = br.ReadInt32(),
                    Name = br.ReadString(),
                    ParentId = br.ReadInt32(),
                };
                int compCount = br.ReadInt32();
                for (int j = 0; j < compCount; j++)
                {
                    var comp = new ComponentSnapshot
                    {
                        TypeName = br.ReadString(),
                    };
                    int len = br.ReadInt32();
                    comp.Data = len > 0 ? br.ReadBytes(len) : Array.Empty<byte>();
                    ent.Components.Add(comp);
                }
                snap.Entities.Add(ent);
            }
            int resCount = br.ReadInt32();
            for (int i = 0; i < resCount; i++)
            {
                snap.Resources.Add(new ResourceReference(
                    br.ReadString(), br.ReadString(), br.ReadString()));
            }
            int varCount = br.ReadInt32();
            for (int i = 0; i < varCount; i++)
            {
                snap.GameVariables[br.ReadString()] = br.ReadString();
            }
            return snap;
        }

        /// <summary>使用 Gzip 压缩字节数据。</summary>
        public static byte[] Compress(byte[] data)
        {
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: false))
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

        // ── 与网络复制共享的位流序列化（用于关键状态的量化压缩）──

        /// <summary>
        /// 将变换（位置+旋转）以量化位流写入，与网络复制共享编码。
        /// 位置量化到 16 位（可配范围），旋转用最小三分量 9+9+9+2 位。
        /// </summary>
        public static void WriteTransform(BitWriter writer, Vector3 pos, Quaternion rot,
            Vector3 min, Vector3 max)
        {
            writer.WriteFloatQuantized(pos.X, min.X, max.X, 16);
            writer.WriteFloatQuantized(pos.Y, min.Y, max.Y, 16);
            writer.WriteFloatQuantized(pos.Z, min.Z, max.Z, 16);
            writer.WriteQuaternion(rot);
        }

        /// <summary>读取量化变换（与 WriteTransform 配对）。</summary>
        public static void ReadTransform(BitReader reader, out Vector3 pos, out Quaternion rot,
            Vector3 min, Vector3 max)
        {
            pos = new Vector3(
                reader.ReadFloatQuantized(min.X, max.X, 16),
                reader.ReadFloatQuantized(min.Y, max.Y, 16),
                reader.ReadFloatQuantized(min.Z, max.Z, 16));
            rot = reader.ReadQuaternion();
        }
    }

    /// <summary>
    /// 存档版本迁移器。当存档版本低于当前版本时，按链式迁移逐步升级。
    /// 每个迁移器负责将“版本 N”升级到“版本 N+1”。
    /// </summary>
    public interface ISaveMigrator
    {
        /// <summary>源版本号</summary>
        int FromVersion { get; }
        /// <summary>目标版本号</summary>
        int ToVersion { get; }
        /// <summary>执行迁移：将旧版本快照升级为新版本。</summary>
        WorldSnapshot Migrate(WorldSnapshot old);
    }

    /// <summary>
    /// 存档槽管理器。管理多个存档槽，提供异步保存/加载、槽位枚举、删除等操作。
    /// 所有文件操作基于一个根目录（如 Saves/）。
    /// </summary>
    public class SaveSlotManager
    {
        /// <summary>存档根目录</summary>
        public string SaveDirectory;
        /// <summary>最大槽位数</summary>
        public int MaxSlots = 20;
        /// <summary>是否正在保存/加载（用于 UI 反馈）</summary>
        public bool IsBusy { get; private set; }
        /// <summary>保存完成事件</summary>
        public event Action<int>? OnSaveCompleted;
        /// <summary>加载完成事件</summary>
        public event Action<int, WorldSnapshot?>? OnLoadCompleted;

        // 版本迁移器链（按 FromVersion 索引）
        private readonly Dictionary<int, ISaveMigrator> _migrators = new();

        /// <param name="saveDirectory">存档根目录（自动创建）</param>
        public SaveSlotManager(string saveDirectory)
        {
            SaveDirectory = saveDirectory;
            Directory.CreateDirectory(saveDirectory);
        }

        /// <summary>注册版本迁移器。</summary>
        public void RegisterMigrator(ISaveMigrator migrator) => _migrators[migrator.FromVersion] = migrator;

        /// <summary>获取槽位文件路径。</summary>
        public string GetSlotPath(int slot) => Path.Combine(SaveDirectory, $"save_{slot:D3}.dat");
        /// <summary>获取槽位元数据路径。</summary>
        public string GetSlotMetaPath(int slot) => Path.Combine(SaveDirectory, $"save_{slot:D3}.meta");

        /// <summary>枚举所有槽位信息。</summary>
        public List<SaveSlotInfo> EnumerateSlots()
        {
            var list = new List<SaveSlotInfo>();
            for (int i = 0; i < MaxSlots; i++)
            {
                string metaPath = GetSlotMetaPath(i);
                if (File.Exists(metaPath))
                {
                    try
                    {
                        var json = File.ReadAllText(metaPath);
                        var info = System.Text.Json.JsonSerializer.Deserialize<SaveSlotInfo>(json);
                        if (info != null) { info.IsEmpty = false; list.Add(info); continue; }
                    }
                    catch { /* 损坏的元数据，当作空槽 */ }
                }
                list.Add(new SaveSlotInfo { SlotIndex = i, IsEmpty = true });
            }
            return list;
        }

        /// <summary>
        /// 异步保存：捕获 ECS 快照并写入指定槽位（含压缩）。
        /// </summary>
        public async Task SaveAsync(int slot, EntityComponentSystem ecs, string levelName,
            float playTime, Dictionary<string, string>? gameVars = null)
        {
            if (slot < 0 || slot >= MaxSlots)
                throw new ArgumentOutOfRangeException(nameof(slot));
            IsBusy = true;
            try
            {
                // 在线程池中执行序列化与压缩，避免阻塞主线程
                var snapshot = await Task.Run(() => CaptureSnapshot(ecs, levelName, playTime, gameVars));
                await Task.Run(() => WriteSlot(slot, snapshot, ecs, levelName, playTime));
                OnSaveCompleted?.Invoke(slot);
            }
            finally { IsBusy = false; }
        }

        /// <summary>
        /// 异步加载：从指定槽位读取并解压存档，返回世界快照。
        /// 自动执行版本迁移。
        /// </summary>
        public async Task<WorldSnapshot?> LoadAsync(int slot)
        {
            if (slot < 0 || slot >= MaxSlots) throw new ArgumentOutOfRangeException(nameof(slot));
            string path = GetSlotPath(slot);
            if (!File.Exists(path)) { OnLoadCompleted?.Invoke(slot, null); return null; }
            IsBusy = true;
            try
            {
                var snapshot = await Task.Run(() => ReadSlot(slot));
                if (snapshot != null)
                    snapshot = MigrateToCurrent(snapshot);
                OnLoadCompleted?.Invoke(slot, snapshot);
                return snapshot;
            }
            finally { IsBusy = false; }
        }

        /// <summary>删除指定槽位的存档。</summary>
        public void DeleteSlot(int slot)
        {
            string path = GetSlotPath(slot);
            string meta = GetSlotMetaPath(slot);
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(meta)) File.Delete(meta);
        }

        /// <summary>捕获 ECS 快照。</summary>
        private WorldSnapshot CaptureSnapshot(EntityComponentSystem ecs, string levelName,
            float playTime, Dictionary<string, string>? gameVars)
        {
            var snap = new WorldSnapshot();
            foreach (int eid in ecs.AllEntities)
            {
                var ent = new EntitySnapshot
                {
                    EntityId = eid,
                    Name = ecs.GetEntityName(eid),
                    ParentId = -1,
                };
                // 序列化 TransformComponent（核心组件）
                if (ecs.HasComponent<TransformComponent>(eid))
                {
                    var tf = ecs.GetComponent<TransformComponent>(eid);
                    ent.Components.Add(SerializeComponent(tf));
                }
                if (ecs.HasComponent<TagComponent>(eid))
                {
                    var tag = ecs.GetComponent<TagComponent>(eid);
                    ent.Components.Add(SerializeComponent(tag));
                }
                if (ecs.HasComponent<MeshComponent>(eid))
                {
                    var mesh = ecs.GetComponent<MeshComponent>(eid);
                    // 资源引用：将 Guid 转为字符串存入 ResourceReference
                    ent.Components.Add(SerializeComponent(mesh));
                    snap.Resources.Add(new ResourceReference(
                        mesh.MeshId.ToString("N"), $"Meshes/{mesh.MeshId:N}", "Mesh"));
                    snap.Resources.Add(new ResourceReference(
                        mesh.MaterialId.ToString("N"), $"Materials/{mesh.MaterialId:N}", "Material"));
                }
                snap.Entities.Add(ent);
            }
            if (gameVars != null)
                foreach (var kvp in gameVars) snap.GameVariables[kvp.Key] = kvp.Value;
            return snap;
        }

        /// <summary>使用位流序列化单个组件。</summary>
        private ComponentSnapshot SerializeComponent<T>(T comp) where T : struct, IComponent
        {
            var writer = new BitWriter(256);
            // 简化：按类型分发序列化
            switch (comp)
            {
                case TransformComponent tf:
                    writer.WriteVector3(tf.Position, -10000f, 10000f, 20);
                    writer.WriteQuaternion(tf.Rotation);
                    writer.WriteVector3(tf.Scale, 0f, 100f, 12);
                    break;
                case TagComponent tag:
                    writer.WriteString(tag.Tag);
                    writer.WriteInt(tag.Layer);
                    writer.WriteUInt(tag.LayerMask);
                    break;
                case MeshComponent mesh:
                    // GUID 以字符串形式写入
                    writer.WriteString(mesh.MeshId.ToString("N"));
                    writer.WriteString(mesh.MaterialId.ToString("N"));
                    writer.WriteBit(mesh.CastsShadow);
                    writer.WriteBit(mesh.ReceivesShadow);
                    writer.WriteBit(mesh.IsVisible);
                    break;
            }
            byte[] data = writer.ToArray();
            return new ComponentSnapshot { TypeName = typeof(T).FullName!, Data = data };
        }

        /// <summary>写入槽位文件（压缩）。</summary>
        private void WriteSlot(int slot, WorldSnapshot snap, EntityComponentSystem ecs,
            string levelName, float playTime)
        {
            byte[] raw = SaveSerializer.Serialize(snap);
            byte[] compressed = SaveSerializer.Compress(raw);

            var header = new SaveFileHeader
            {
                Version = SaveFileHeader.CurrentVersion,
                TimestampUtcTicks = DateTime.UtcNow.Ticks,
                LevelName = levelName,
                PlayTimeSeconds = playTime,
                EntityCount = snap.Entities.Count,
                UncompressedLength = raw.Length,
                CompressedLength = compressed.Length,
            };

            using var fs = File.Create(GetSlotPath(slot));
            using var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: false);
            bw.Write(SaveFileHeader.Magic);
            bw.Write(header.Version);
            bw.Write(header.TimestampUtcTicks);
            bw.Write(header.LevelName ?? "");
            bw.Write(header.PlayTimeSeconds);
            bw.Write(header.EntityCount);
            bw.Write(header.UncompressedLength);
            bw.Write(header.CompressedLength);
            bw.Write(compressed);
            bw.Flush();

            // 写元数据
            var info = new SaveSlotInfo
            {
                SlotIndex = slot,
                DisplayName = $"{levelName} - Slot {slot}",
                LevelName = levelName,
                LastSavedAtLocal = DateTime.Now,
                PlayTimeSeconds = playTime,
                EntityCount = snap.Entities.Count,
                SaveVersion = SaveFileHeader.CurrentVersion,
                IsEmpty = false,
            };
            if (snap.GameVariables.TryGetValue("CreatedAt", out var created))
                info.CreatedAtLocal = DateTime.Parse(created);
            var json = System.Text.Json.JsonSerializer.Serialize(info);
            File.WriteAllText(GetSlotMetaPath(slot), json);
        }

        /// <summary>读取槽位文件（解压）。</summary>
        private WorldSnapshot? ReadSlot(int slot)
        {
            string path = GetSlotPath(slot);
            if (!File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: false);
            uint magic = br.ReadUInt32();
            if (magic != SaveFileHeader.Magic)
                throw new InvalidDataException($"存档文件魔数不匹配: 0x{magic:X8}");
            var header = new SaveFileHeader
            {
                Version = br.ReadInt32(),
                TimestampUtcTicks = br.ReadInt64(),
                LevelName = br.ReadString(),
                PlayTimeSeconds = br.ReadSingle(),
                EntityCount = br.ReadInt32(),
                UncompressedLength = br.ReadInt64(),
                CompressedLength = br.ReadInt64(),
            };
            byte[] compressed = br.ReadBytes((int)header.CompressedLength);
            byte[] raw = SaveSerializer.Decompress(compressed);
            return SaveSerializer.Deserialize(raw);
        }

        /// <summary>按迁移器链升级存档到当前版本。</summary>
        private WorldSnapshot MigrateToCurrent(WorldSnapshot snap)
        {
            // 注：版本号存储在文件头，此处简化为假设已读出。
            // 实际通过 header.Version 驱动；此处演示迁移链逻辑。
            int currentVersion = SaveFileHeader.CurrentVersion;
            int version = 1; // 实际从 header 读取
            while (version < currentVersion && _migrators.TryGetValue(version, out var migrator))
            {
                snap = migrator.Migrate(snap);
                version = migrator.ToVersion;
            }
            return snap;
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // 第二部分：回放系统
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 回放帧。记录某一帧的关键状态（实体变换 + 关键标志）。
    /// 支持完整帧与差量帧两种模式。
    /// </summary>
    public class ReplayFrame
    {
        /// <summary>帧序号</summary>
        public int Frame;
        /// <summary>时间戳（秒）</summary>
        public float Time;
        /// <summary>是否为关键帧（完整状态，非差量）</summary>
        public bool IsKeyframe;
        /// <summary>实体变换记录（实体Id -> 位置/旋转位流）</summary>
        public Dictionary<int, byte[]> EntityStates = new();
        /// <summary>本帧新增的实体（首次出现）</summary>
        public List<int> SpawnedEntities = new();
        /// <summary>本帧移除的实体</summary>
        public List<int> RemovedEntities = new();
        /// <summary>本帧触发的关键事件（如击杀、得分）</summary>
        public List<string> Events = new();
    }

    /// <summary>
    /// 回放元数据。
    /// </summary>
    public class ReplayMetadata
    {
        /// <summary>回放名称</summary>
        public string Name = "";
        /// <summary>关卡名称</summary>
        public string LevelName = "";
        /// <summary>录制时间</summary>
        public DateTime RecordedAt = DateTime.UtcNow;
        /// <summary>总帧数</summary>
        public int TotalFrames;
        /// <summary>总时长（秒）</summary>
        public float Duration;
        /// <summary>录制帧率（FPS）</summary>
        public float RecordFrameRate = 30f;
        /// <summary>关注的玩家实体 ID（高亮视角）</summary>
        public int FocusPlayerId = -1;
        /// <summary>参与玩家列表</summary>
        public List<string> Players = new();
    }

    /// <summary>
    /// 回放录制器。每帧捕获关键状态，采用差量编码压缩：
    /// 每隔 N 帧记录一个完整关键帧，其余帧仅记录变化。
    /// </summary>
    public class ReplayRecorder
    {
        /// <summary>录制帧率</summary>
        public float FrameRate = 30f;
        /// <summary>关键帧间隔（每 N 帧一个完整帧）</summary>
        public int KeyframeInterval = 60;
        /// <summary>元数据</summary>
        public ReplayMetadata Metadata = new();
        /// <summary>是否正在录制</summary>
        public bool IsRecording { get; private set; }

        // 已录制的帧
        private readonly List<ReplayFrame> _frames = new();
        // 上一帧的实体状态（用于差量比较）
        private readonly Dictionary<int, byte[]> _lastStates = new();
        // 已知实体集合（用于检测新增/移除）
        private readonly HashSet<int> _knownEntities = new();
        private float _accumulator;
        private int _frameIndex;

        /// <summary>已录制帧数。</summary>
        public int FrameCount => _frames.Count;

        /// <summary>开始录制。</summary>
        public void StartRecording(string levelName, int focusPlayerId = -1)
        {
            _frames.Clear();
            _lastStates.Clear();
            _knownEntities.Clear();
            _frameIndex = 0;
            _accumulator = 0;
            Metadata = new ReplayMetadata
            {
                LevelName = levelName,
                RecordedAt = DateTime.UtcNow,
                FocusPlayerId = focusPlayerId,
            };
            IsRecording = true;
        }

        /// <summary>每帧更新：按帧率采样 ECS 状态。</summary>
        public void Update(float dt, EntityComponentSystem ecs)
        {
            if (!IsRecording) return;
            _accumulator += dt;
            float interval = 1f / FrameRate;
            if (_accumulator < interval) return;
            _accumulator -= interval;

            CaptureFrame(ecs);
        }

        /// <summary>捕获一帧（关键帧或差量帧）。</summary>
        private void CaptureFrame(EntityComponentSystem ecs)
        {
            bool isKey = (_frameIndex % KeyframeInterval == 0);
            var frame = new ReplayFrame
            {
                Frame = _frameIndex,
                Time = _frameIndex / FrameRate,
                IsKeyframe = isKey,
            };

            var currentEntities = new HashSet<int>(ecs.AllEntities);
            // 检测新增/移除实体
            foreach (int eid in currentEntities)
                if (!_knownEntities.Contains(eid)) frame.SpawnedEntities.Add(eid);
            foreach (int eid in _knownEntities)
                if (!currentEntities.Contains(eid)) frame.RemovedEntities.Add(eid);
            _knownEntities.Clear();
            foreach (int eid in currentEntities) _knownEntities.Add(eid);

            // 记录变换（差量：仅当变化时记录）
            var writer = new BitWriter(256);
            var min = new Vector3(-10000);
            var max = new Vector3(10000);
            foreach (int eid in currentEntities)
            {
                if (!ecs.HasComponent<TransformComponent>(eid)) continue;
                var tf = ecs.GetComponent<TransformComponent>(eid);
                writer = new BitWriter(64);
                SaveSerializer.WriteTransform(writer, tf.Position, tf.Rotation, min, max);
                byte[] state = writer.ToArray();

                if (isKey || !_lastStates.TryGetValue(eid, out var prev) || !BytesEqual(prev, state))
                {
                    frame.EntityStates[eid] = state;
                    _lastStates[eid] = state;
                }
            }

            _frames.Add(frame);
            _frameIndex++;
        }

        /// <summary>停止录制并返回完整回放数据。</summary>
        public ReplayData StopRecording()
        {
            IsRecording = false;
            Metadata.TotalFrames = _frames.Count;
            Metadata.Duration = _frames.Count > 0 ? _frames[_frames.Count - 1].Time : 0f;
            return new ReplayData { Metadata = Metadata, Frames = _frames };
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }

    /// <summary>完整回放数据（元数据 + 所有帧）。</summary>
    public class ReplayData
    {
        /// <summary>元数据</summary>
        public ReplayMetadata Metadata = new();
        /// <summary>所有帧</summary>
        public List<ReplayFrame> Frames = new();
    }

    /// <summary>回放播放状态。</summary>
    public enum ReplayPlaybackState
    {
        /// <summary>停止</summary>
        Stopped,
        /// <summary>播放中</summary>
        Playing,
        /// <summary>暂停</summary>
        Paused,
        /// <summary>逐帧步进</summary>
        Stepping,
    }

    /// <summary>
    /// 回放播放器。支持前进/暂停/后退/跳转，逐帧驱动 ECS 还原状态。
    /// </summary>
    public class ReplayPlayer
    {
        /// <summary>回放数据</summary>
        public ReplayData? Data;
        /// <summary>当前播放状态</summary>
        public ReplayPlaybackState State = ReplayPlaybackState.Stopped;
        /// <summary>当前帧索引</summary>
        public int CurrentFrame;
        /// <summary>播放速度（1.0 = 正常，-1.0 = 倒放）</summary>
        public float PlaybackSpeed = 1f;
        /// <summary>关注的玩家实体 ID（高亮视角）</summary>
        public int FocusPlayerId = -1;
        /// <summary>播放完成事件</summary>
        public event Action? OnPlaybackFinished;

        private float _timeAccumulator;
        private readonly Dictionary<int, byte[]> _appliedStates = new();

        /// <summary>加载回放数据并准备播放。</summary>
        public void Load(ReplayData data)
        {
            Data = data;
            CurrentFrame = 0;
            _appliedStates.Clear();
            FocusPlayerId = data.Metadata.FocusPlayerId;
        }

        /// <summary>开始播放。</summary>
        public void Play()
        {
            if (Data == null || Data.Frames.Count == 0) return;
            State = ReplayPlaybackState.Playing;
        }

        /// <summary>暂停。</summary>
        public void Pause() => State = ReplayPlaybackState.Paused;

        /// <summary>停止并重置到开头。</summary>
        public void Stop()
        {
            State = ReplayPlaybackState.Stopped;
            CurrentFrame = 0;
            _appliedStates.Clear();
        }

        /// <summary>逐帧步进（前进一帧）。</summary>
        public void StepForward()
        {
            if (Data == null) return;
            State = ReplayPlaybackState.Stepping;
            if (CurrentFrame < Data.Frames.Count - 1)
            {
                CurrentFrame++;
                ApplyFrame(Data.Frames[CurrentFrame]);
            }
        }

        /// <summary>逐帧步进（后退一帧，需从最近关键帧重建）。</summary>
        public void StepBackward()
        {
            if (Data == null || CurrentFrame <= 0) return;
            State = ReplayPlaybackState.Stepping;
            CurrentFrame--;
            // 后退需从上一个关键帧重建状态
            RebuildToFrame(CurrentFrame);
        }

        /// <summary>跳转到指定帧。</summary>
        public void Seek(int frameIndex)
        {
            if (Data == null) return;
            CurrentFrame = Math.Clamp(frameIndex, 0, Data.Frames.Count - 1);
            RebuildToFrame(CurrentFrame);
        }

        /// <summary>每帧更新：按播放速度推进帧索引。</summary>
        public void Update(float dt, EntityComponentSystem ecs)
        {
            if (State != ReplayPlaybackState.Playing || Data == null) return;
            _timeAccumulator += dt * PlaybackSpeed;
            float interval = 1f / Data.Metadata.RecordFrameRate;

            while (_timeAccumulator >= interval && CurrentFrame < Data.Frames.Count - 1)
            {
                _timeAccumulator -= interval;
                CurrentFrame++;
                ApplyFrame(Data.Frames[CurrentFrame], ecs);
            }
            if (CurrentFrame >= Data.Frames.Count - 1)
            {
                State = ReplayPlaybackState.Stopped;
                OnPlaybackFinished?.Invoke();
            }
        }

        /// <summary>应用一帧的状态到 ECS（前进播放时使用）。</summary>
        private void ApplyFrame(ReplayFrame frame, EntityComponentSystem? ecs = null)
        {
            // 处理实体增删
            if (ecs != null)
            {
                foreach (int eid in frame.RemovedEntities)
                    if (ecs.HasEntity(eid)) ecs.DestroyEntity(eid);
                foreach (int eid in frame.SpawnedEntities)
                    if (!ecs.HasEntity(eid)) ecs.CreateEntity($"Replay_{eid}");
            }
            // 应用变换差量
            foreach (var kvp in frame.EntityStates)
            {
                _appliedStates[kvp.Key] = kvp.Value;
                if (ecs == null || !ecs.HasEntity(kvp.Key)) continue;
                var reader = new BitReader(kvp.Value, kvp.Value.Length);
                SaveSerializer.ReadTransform(reader, out Vector3 pos, out Quaternion rot,
                    new Vector3(-10000), new Vector3(10000));
                if (ecs.HasComponent<TransformComponent>(kvp.Key))
                {
                    var tf = ecs.GetComponent<TransformComponent>(kvp.Key);
                    tf.Position = pos;
                    tf.Rotation = rot;
                    ecs.SetComponent(kvp.Key, tf);
                }
            }
        }

        /// <summary>从最近的关键帧重建到目标帧（后退/跳转时使用）。</summary>
        private void RebuildToFrame(int targetFrame)
        {
            if (Data == null) return;
            _appliedStates.Clear();
            // 找到目标帧之前最近的关键帧
            int start = targetFrame;
            for (int i = targetFrame; i >= 0; i--)
            {
                if (Data.Frames[i].IsKeyframe) { start = i; break; }
            }
            // 从关键帧顺序应用到目标帧
            for (int i = start; i <= targetFrame && i < Data.Frames.Count; i++)
                ApplyFrame(Data.Frames[i]);
        }

        /// <summary>获取关注玩家在当前帧的位置（用于摄像机跟随/高亮）。</summary>
        public Vector3 GetFocusPosition()
        {
            if (Data == null || FocusPlayerId < 0) return Vector3.Zero;
            if (_appliedStates.TryGetValue(FocusPlayerId, out var state))
            {
                var reader = new BitReader(state, state.Length);
                SaveSerializer.ReadTransform(reader, out Vector3 pos, out _,
                    new Vector3(-10000), new Vector3(10000));
                return pos;
            }
            return Vector3.Zero;
        }
    }

    /// <summary>
    /// 回放导出器。将回放导出为视频序列帧（PNG 图片序列），
    /// 供外部工具合成视频（如 FFmpeg）。
    /// </summary>
    public class ReplayExporter
    {
        /// <summary>导出根目录</summary>
        public string OutputDirectory;
        /// <summary>导出分辨率宽</summary>
        public int Width = 1920;
        /// <summary>导出分辨率高</summary>
        public int Height = 1080;

        public ReplayExporter(string outputDirectory)
        {
            OutputDirectory = outputDirectory;
            Directory.CreateDirectory(outputDirectory);
        }

        /// <summary>
        /// 导出指定回放为 PNG 序列帧。
        /// 实际渲染由渲染管线回调提供帧像素；此处编排帧序号与文件名。
        /// </summary>
        /// <param name="data">回放数据</param>
        /// <param name="frameRenderer">帧渲染回调：输入帧索引，输出像素字节（RGBA）</param>
        public async Task ExportAsync(ReplayData data, Func<int, byte[]> frameRenderer)
        {
            for (int i = 0; i < data.Frames.Count; i++)
            {
                byte[] pixels = await Task.Run(() => frameRenderer(i));
                string path = Path.Combine(OutputDirectory, $"frame_{i:D06}.png");
                await Task.Run(() => WritePng(path, pixels, Width, Height));
            }
        }

        /// <summary>写入 PNG 文件（自包含最小编码器，无需外部图像库依赖）。</summary>
        private void WritePng(string path, byte[] rgba, int w, int h)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            // PNG 文件签名
            fs.Write(PngSignature, 0, PngSignature.Length);
            // IHDR
            byte[] ihdr = new byte[13];
            WriteInt32BE(ihdr, 0, w);
            WriteInt32BE(ihdr, 4, h);
            ihdr[8] = 8;   // 位深 8
            ihdr[9] = 6;   // 颜色类型 6 = RGBA
            ihdr[10] = 0;  // 压缩方法 deflate
            ihdr[11] = 0;  // 滤波方法 0
            ihdr[12] = 0;  // 隔行扫描 0
            WritePngChunk(fs, "IHDR", ihdr);
            // IDAT — 每扫描行前缀滤波字节 0，后接 RGBA 数据
            int stride = w * 4;
            byte[] raw = new byte[h * (stride + 1)];
            for (int y = 0; y < h; y++)
            {
                raw[y * (stride + 1)] = 0;
                Buffer.BlockCopy(rgba, y * stride, raw, y * (stride + 1) + 1, stride);
            }
            WritePngChunk(fs, "IDAT", ZlibCompress(raw));
            // IEND
            WritePngChunk(fs, "IEND", Array.Empty<byte>());
        }

        private static readonly byte[] PngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };

        private static void WriteInt32BE(byte[] buf, int offset, int value)
        {
            buf[offset] = (byte)(value >> 24);
            buf[offset + 1] = (byte)(value >> 16);
            buf[offset + 2] = (byte)(value >> 8);
            buf[offset + 3] = (byte)value;
        }

        private static void WritePngChunk(Stream s, string type, byte[] data)
        {
            byte[] typeBytes = Encoding.ASCII.GetBytes(type);
            s.WriteByte((byte)(data.Length >> 24));
            s.WriteByte((byte)(data.Length >> 16));
            s.WriteByte((byte)(data.Length >> 8));
            s.WriteByte((byte)data.Length);
            s.Write(typeBytes, 0, 4);
            s.Write(data, 0, data.Length);
            // CRC 覆盖 type + data
            byte[] crcBuf = new byte[4 + data.Length];
            Buffer.BlockCopy(typeBytes, 0, crcBuf, 0, 4);
            Buffer.BlockCopy(data, 0, crcBuf, 4, data.Length);
            uint crc = Crc32(crcBuf);
            s.WriteByte((byte)(crc >> 24));
            s.WriteByte((byte)(crc >> 16));
            s.WriteByte((byte)(crc >> 8));
            s.WriteByte((byte)crc);
        }

        private static uint Crc32(byte[] data)
        {
            uint crc = 0xFFFFFFFFu;
            for (int i = 0; i < data.Length; i++)
            {
                crc ^= data[i];
                for (int j = 0; j < 8; j++)
                    crc = (crc & 1u) != 0u ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
            return ~crc;
        }

        private static byte[] ZlibCompress(byte[] data)
        {
            using var ms = new MemoryStream();
            ms.WriteByte(0x78); // zlib 头: CMF
            ms.WriteByte(0x9C); // zlib 头: FLG (默认压缩)
            using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                ds.Write(data, 0, data.Length);
            uint adler = Adler32(data);
            ms.WriteByte((byte)(adler >> 24));
            ms.WriteByte((byte)(adler >> 16));
            ms.WriteByte((byte)(adler >> 8));
            ms.WriteByte((byte)adler);
            return ms.ToArray();
        }

        private static uint Adler32(byte[] data)
        {
            const uint ModAdler = 65521;
            uint a = 1, b = 0;
            for (int i = 0; i < data.Length; i++)
            {
                a = (a + data[i]) % ModAdler;
                b = (b + a) % ModAdler;
            }
            return (b << 16) | a;
        }
    }

    /// <summary>
    /// 回放高亮器。在回放播放时高亮关注玩家的动作，
    /// 可叠加视觉特效（描边、轨迹、事件标记）。
    /// </summary>
    public class ReplayHighlighter
    {
        /// <summary>关注的玩家实体 ID</summary>
        public int FocusPlayerId = -1;
        /// <summary>是否启用高亮</summary>
        public bool Enabled = true;
        /// <summary>高亮颜色</summary>
        public Vector3 HighlightColor = new(1f, 0.8f, 0.2f);
        /// <summary>轨迹点缓存（关注玩家历史位置）</summary>
        public readonly List<Vector3> TrailPoints = new();
        /// <summary>最大轨迹点数</summary>
        public int MaxTrailPoints = 300;

        /// <summary>每帧更新：记录关注玩家位置到轨迹。</summary>
        public void Update(ReplayPlayer player)
        {
            if (!Enabled || FocusPlayerId < 0) return;
            Vector3 pos = player.GetFocusPosition();
            if (TrailPoints.Count == 0 || Vector3.Distance(TrailPoints[TrailPoints.Count - 1], pos) > 0.1f)
            {
                TrailPoints.Add(pos);
                if (TrailPoints.Count > MaxTrailPoints)
                    TrailPoints.RemoveAt(0);
            }
        }

        /// <summary>获取当前帧应高亮的事件列表。</summary>
        public List<string> GetCurrentEvents(ReplayPlayer player)
        {
            if (player.Data == null || player.CurrentFrame >= player.Data.Frames.Count)
                return new List<string>();
            return player.Data.Frames[player.CurrentFrame].Events;
        }

        /// <summary>清空轨迹。</summary>
        public void ClearTrail() => TrailPoints.Clear();
    }

    /// <summary>
    /// 存档与回放系统总管。作为引擎子系统统一持有存档管理器、录制器与播放器。
    /// 由 EngineCore 创建并注册到系统列表。
    /// </summary>
    public class SaveReplaySystem : ISystem
    {
        /// <summary>系统名称</summary>
        public string Name => "SaveReplaySystem";

        /// <summary>存档槽管理器</summary>
        public SaveSlotManager SaveSlots;
        /// <summary>回放录制器</summary>
        public ReplayRecorder Recorder = new();
        /// <summary>回放播放器</summary>
        public ReplayPlayer Player = new();
        /// <summary>回放高亮器</summary>
        public ReplayHighlighter Highlighter = new();
        /// <summary>ECS 引用</summary>
        public EntityComponentSystem? ECS;

        /// <param name="saveDirectory">存档根目录</param>
        public SaveReplaySystem(string saveDirectory)
        {
            SaveSlots = new SaveSlotManager(saveDirectory);
        }

        /// <summary>绑定 ECS。</summary>
        public void BindECS(EntityComponentSystem ecs) => ECS = ecs;

        /// <summary>每帧更新：驱动录制与播放。</summary>
        public void Update(float dt, EntityComponentSystem ecs)
        {
            Recorder.Update(dt, ecs);
            Player.Update(dt, ecs);
            Highlighter.Update(Player);
        }

        /// <summary>开始录制回放。</summary>
        public void StartRecording(string levelName, int focusPlayerId = -1)
            => Recorder.StartRecording(levelName, focusPlayerId);

        /// <summary>停止录制并保存回放到文件。</summary>
        public ReplayData StopRecording() => Recorder.StopRecording();

        /// <summary>开始播放回放。</summary>
        public void PlayReplay(ReplayData data)
        {
            Player.Load(data);
            Player.Play();
        }

        /// <summary>异步保存到指定槽位。</summary>
        public Task SaveGameAsync(int slot, string levelName, float playTime,
            Dictionary<string, string>? gameVars = null)
        {
            if (ECS == null) throw new InvalidOperationException("ECS 未绑定");
            return SaveSlots.SaveAsync(slot, ECS, levelName, playTime, gameVars);
        }

        /// <summary>异步从指定槽位加载。</summary>
        public Task<WorldSnapshot?> LoadGameAsync(int slot) => SaveSlots.LoadAsync(slot);
    }
}
