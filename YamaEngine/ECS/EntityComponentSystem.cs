using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace YamaEngine.ECS
{
    // ============================
    // ECS 查询用的 ref 委托（System.Action 不支持 ref 类型参数，故自定义）
    // ============================
    public delegate void RefAction<T1>(Entity entity, ref T1 c1) where T1 : struct, IComponent;
    public delegate void RefAction<T1, T2>(Entity entity, ref T1 c1, ref T2 c2) where T1 : struct, IComponent where T2 : struct, IComponent;
    public delegate void RefAction<T1, T2, T3>(Entity entity, ref T1 c1, ref T2 c2, ref T3 c3) where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent;

    // ============================
    // 1. 基础接口与类型（保持兼容）
    // ============================
    public interface IComponent { }

    public struct TransformComponent : IComponent
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Scale;

        public Matrix4x4 Matrix
        {
            get
            {
                return Matrix4x4.CreateScale(Scale) *
                       Matrix4x4.CreateFromQuaternion(Rotation) *
                       Matrix4x4.CreateTranslation(Position);
            }
        }

        public Vector3 Forward => Vector3.Transform(new Vector3(0, 0, -1), Rotation);
        public Vector3 Right => Vector3.Transform(new Vector3(1, 0, 0), Rotation);
        public Vector3 Up => Vector3.Transform(new Vector3(0, 1, 0), Rotation);
    }

    public struct MeshComponent : IComponent
    {
        public Guid MeshId;
        public Guid MaterialId;
        public bool CastsShadow;
        public bool ReceivesShadow;
        public bool IsVisible;
        public int TriangleCount;
        public int VertexCount;

        // 兼容别名 (EditorSceneController 等使用)
        public int AssetReferenceId;
        public int MaterialIndex;
    }

    public struct LightComponent : IComponent
    {
        public enum LightType { Directional, Point, Spot, Area }
        public LightType Type;
        public Vector3 Color;
        public float Intensity;
        public float Range;
        public float InnerConeAngle;
        public float OuterConeAngle;
        public bool CastsShadow;
        public float ShadowBias;
    }

    public struct CameraComponent : IComponent
    {
        public float FieldOfView;
        public float AspectRatio;
        public float NearClip;
        public float FarClip;
        public bool IsMain;
        public bool IsOrthographic;
        public float OrthographicSize;

        // 兼容别名 (EditorSceneController 等使用)
        public float FOV { get => FieldOfView; set => FieldOfView = value; }
        public float NearPlane { get => NearClip; set => NearClip = value; }
        public float FarPlane { get => FarClip; set => FarClip = value; }
        public bool IsMainCamera { get => IsMain; set => IsMain = value; }
    }

    public struct RigidbodyComponent : IComponent
    {
        public YamaEngine.Physics.ColliderType ColliderType;
        public Vector3 ColliderSize;
        public bool IsKinematic;
        public float Mass;
    }

    public struct TagComponent : IComponent
    {
        public string Tag;
        public int Layer;
        public uint LayerMask;
        public bool IsStatic;
    }

    public struct RelationshipComponent : IComponent
    {
        public int ParentId;
        public List<int> Children;
    }

    // ============================
    // 2. Entity 标识
    // ============================
    public readonly struct Entity : IEquatable<Entity>
    {
        public readonly int Index;
        public readonly int Generation;

        public Entity(int index, int generation)
        {
            Index = index;
            Generation = generation;
        }

        public static readonly Entity Null = new Entity(0, 0);

        public bool IsNull => Index == 0;

        public bool Equals(Entity other) => Index == other.Index && Generation == other.Generation;
        public override bool Equals(object obj) => obj is Entity e && Equals(e);
        public override int GetHashCode() => HashCode.Combine(Index, Generation);
        public static bool operator ==(Entity a, Entity b) => a.Equals(b);
        public static bool operator !=(Entity a, Entity b) => !a.Equals(b);
        public override string ToString() => $"Entity({Index}:{Generation})";
    }

    // ============================
    // 3. ComponentType 与类型注册
    // ============================
    public readonly struct ComponentType : IEquatable<ComponentType>
    {
        public readonly int TypeIndex;
        public readonly int Size;
        public readonly Type Type;

        private ComponentType(int typeIndex, int size, Type type)
        {
            TypeIndex = typeIndex;
            Size = size;
            Type = type;
        }

        public bool Equals(ComponentType other) => TypeIndex == other.TypeIndex;
        public override bool Equals(object obj) => obj is ComponentType ct && Equals(ct);
        public override int GetHashCode() => TypeIndex;
        public static bool operator ==(ComponentType a, ComponentType b) => a.Equals(b);
        public static bool operator !=(ComponentType a, ComponentType b) => !a.Equals(b);

        private static readonly Dictionary<Type, ComponentType> s_typeMap = new();
        private static int s_nextTypeIndex = 1;
        private static readonly object s_lock = new();

        public static ComponentType Get<T>() where T : struct, IComponent
        {
            Type t = typeof(T);
            lock (s_lock)
            {
                if (s_typeMap.TryGetValue(t, out var ct))
                    return ct;

                int idx = s_nextTypeIndex++;
                var newCt = new ComponentType(idx, Unsafe.SizeOf<T>(), t);
                s_typeMap[t] = newCt;
                return newCt;
            }
        }

        public static ComponentType Get(Type type)
        {
            lock (s_lock)
            {
                if (s_typeMap.TryGetValue(type, out var ct))
                    return ct;

                int idx = s_nextTypeIndex++;
                int size = type.IsValueType ? (int?)type.GetMethod("GetSize")?.Invoke(null, null) ?? 0 : 0;
                if (size == 0 && type.IsValueType)
                {
                    // Fallback: use Marshal.SizeOf or a generic helper if possible
                    // For known struct types we rely on Unsafe.SizeOf<T> via generic path
                    size = 64; // generous fallback
                }
                var newCt = new ComponentType(idx, size, type);
                s_typeMap[type] = newCt;
                return newCt;
            }
        }

        public static IReadOnlyDictionary<Type, ComponentType> AllTypes => s_typeMap;
    }

    // ============================
    // 4. Archetype 与 Chunk（核心存储）
    // ============================
    public sealed class Chunk
    {
        public const int Capacity = 128; // 每个Chunk容纳的实体数

        public readonly Archetype Archetype;
        public readonly int ComponentCount;

        // SOA 存储: 每个组件类型一个数组
        internal readonly Array[] ComponentArrays;
        internal readonly Entity[] Entities;
        internal int Count;

        public Chunk(Archetype archetype)
        {
            Archetype = archetype;
            ComponentCount = archetype.ComponentTypes.Length;
            Entities = new Entity[Capacity];
            ComponentArrays = new Array[ComponentCount];

            for (int i = 0; i < ComponentCount; i++)
            {
                var ct = archetype.ComponentTypes[i];
                ComponentArrays[i] = Array.CreateInstance(ct.Type, Capacity);
            }
            Count = 0;
        }

        public bool IsFull => Count >= Capacity;
        public bool IsEmpty => Count == 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T GetComponent<T>(int indexInChunk, int componentIndex) where T : struct, IComponent
        {
            return ref ((T[])ComponentArrays[componentIndex])[indexInChunk];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetComponent<T>(int indexInChunk, int componentIndex, in T value) where T : struct, IComponent
        {
            ((T[])ComponentArrays[componentIndex])[indexInChunk] = value;
        }

        internal int AddEntity(Entity entity)
        {
            if (IsFull) throw new InvalidOperationException("Chunk is full");
            int index = Count++;
            Entities[index] = entity;
            return index;
        }

        internal void RemoveEntity(int indexInChunk)
        {
            int last = Count - 1;
            if (indexInChunk != last)
            {
                // 将最后一个实体移动到被删除的位置
                Entities[indexInChunk] = Entities[last];
                for (int i = 0; i < ComponentCount; i++)
                {
                    var arr = ComponentArrays[i];
                    arr.SetValue(arr.GetValue(last), indexInChunk);
                }
            }
            Count--;
        }
    }

    public sealed class Archetype
    {
        public readonly ComponentType[] ComponentTypes; // 已排序
        public readonly int Hash;
        public IReadOnlyList<Chunk> Chunks => _chunks;

        private readonly List<Chunk> _chunks = new();
        private readonly Dictionary<ComponentType, int> _componentIndexMap = new();

        public int EntityCount { get; private set; }

        public Archetype(ComponentType[] types)
        {
            ComponentTypes = types.OrderBy(t => t.TypeIndex).ToArray();
            Hash = ComputeHash(ComponentTypes);

            for (int i = 0; i < ComponentTypes.Length; i++)
                _componentIndexMap[ComponentTypes[i]] = i;
        }

        public bool HasComponent(ComponentType type) => _componentIndexMap.ContainsKey(type);
        public int GetComponentIndex(ComponentType type) => _componentIndexMap.TryGetValue(type, out var idx) ? idx : -1;

        internal Chunk AcquireChunk()
        {
            // 查找非满Chunk
            for (int i = 0; i < _chunks.Count; i++)
            {
                if (!_chunks[i].IsFull)
                    return _chunks[i];
            }
            var chunk = new Chunk(this);
            _chunks.Add(chunk);
            return chunk;
        }

        internal void AddEntityCount() => EntityCount++;
        internal void RemoveEntityCount() => EntityCount--;

        internal void ReleaseChunkIfEmpty(Chunk chunk)
        {
            if (chunk.IsEmpty)
            {
                _chunks.Remove(chunk);
            }
        }

        private static int ComputeHash(ComponentType[] types)
        {
            int hash = 0;
            foreach (var t in types)
                hash = hash * 31 + t.GetHashCode();
            return hash;
        }

        public bool MatchesQuery(ComponentType[] all, ComponentType[] any, ComponentType[] none)
        {
            if (none != null)
            {
                foreach (var t in none)
                    if (HasComponent(t)) return false;
            }
            if (all != null)
            {
                foreach (var t in all)
                    if (!HasComponent(t)) return false;
            }
            if (any != null && any.Length > 0)
            {
                bool hasAny = false;
                foreach (var t in any)
                {
                    if (HasComponent(t))
                    {
                        hasAny = true;
                        break;
                    }
                }
                if (!hasAny) return false;
            }
            return true;
        }
    }

    // ============================
    // 5. EntityManager
    // ============================
    public sealed class EntityManager
    {
        private struct EntityInfo
        {
            public int Generation;
            public Archetype Archetype;
            public Chunk Chunk;
            public int IndexInChunk;
            public bool IsValid;
        }

        private EntityInfo[] _entities;
        private int _entityCount;
        private readonly Queue<int> _freeIndices = new();
        private readonly Dictionary<int, string> _entityNames = new();
        private readonly Dictionary<int, Archetype> _archetypes = new(); // Hash -> Archetype
        private readonly object _lock = new();

        public int EntityCount => _entityCount;

        public event Action<Entity>? EntityCreated;
        public event Action<Entity>? EntityDestroyed;

        public EntityManager(int initialCapacity = 1024)
        {
            _entities = new EntityInfo[initialCapacity];
            _entityCount = 0;
        }

        public Entity CreateEntity(string name = null)
        {
            lock (_lock)
            {
                int index;
                if (_freeIndices.Count > 0)
                {
                    index = _freeIndices.Dequeue();
                }
                else
                {
                    index = _entityCount + 1;
                    if (index >= _entities.Length)
                    {
                        Array.Resize(ref _entities, _entities.Length * 2);
                    }
                    _entityCount = index;
                }

                int generation = _entities[index].Generation;
                if (generation == 0) generation = 1;
                _entities[index] = new EntityInfo
                {
                    Generation = generation,
                    IsValid = true
                };

                var entity = new Entity(index, generation);
                _entityNames[index] = name ?? $"Entity_{index}";

                EntityCreated?.Invoke(entity);
                return entity;
            }
        }

        public void DestroyEntity(Entity entity)
        {
            lock (_lock)
            {
                if (!IsValid(entity)) return;

                ref var info = ref _entities[entity.Index];

                // 从Chunk中移除
                if (info.Chunk != null)
                {
                    info.Chunk.RemoveEntity(info.IndexInChunk);
                    info.Archetype.RemoveEntityCount();
                    info.Archetype.ReleaseChunkIfEmpty(info.Chunk);

                    // 更新被移动实体的位置信息
                    if (info.IndexInChunk < info.Chunk.Count)
                    {
                        var movedEntity = info.Chunk.Entities[info.IndexInChunk];
                        ref var movedInfo = ref _entities[movedEntity.Index];
                        movedInfo.IndexInChunk = info.IndexInChunk;
                    }
                }

                info.IsValid = false;
                info.Generation++;
                info.Chunk = null;
                info.Archetype = null;
                _freeIndices.Enqueue(entity.Index);
                _entityNames.Remove(entity.Index);

                EntityDestroyed?.Invoke(entity);
            }
        }

        public bool IsValid(Entity entity)
        {
            if (entity.Index <= 0 || entity.Index >= _entities.Length) return false;
            ref var info = ref _entities[entity.Index];
            return info.IsValid && info.Generation == entity.Generation;
        }

        public string GetName(Entity entity) => _entityNames.TryGetValue(entity.Index, out var n) ? n : "Entity";
        public void SetName(Entity entity, string name) => _entityNames[entity.Index] = name;

        public T AddComponent<T>(Entity entity, T component) where T : struct, IComponent
        {
            lock (_lock)
            {
                if (!IsValid(entity)) throw new InvalidOperationException("Invalid entity");
                var ct = ComponentType.Get<T>();
                ref var info = ref _entities[entity.Index];

                if (info.Archetype != null && info.Archetype.HasComponent(ct))
                {
                    // 已存在，直接设置
                    int compIdx = info.Archetype.GetComponentIndex(ct);
                    info.Chunk.SetComponent<T>(info.IndexInChunk, compIdx, component);
                    return component;
                }

                // 迁移到新Archetype
                var oldArchetype = info.Archetype;
                var oldChunk = info.Chunk;
                int oldIndex = info.IndexInChunk;

                var newTypes = new List<ComponentType>();
                if (oldArchetype != null)
                    newTypes.AddRange(oldArchetype.ComponentTypes);
                newTypes.Add(ct);

                var newArchetype = GetOrCreateArchetype(newTypes.ToArray());
                var newChunk = newArchetype.AcquireChunk();
                int newIndex = newChunk.AddEntity(entity);

                // 复制旧组件数据
                if (oldArchetype != null)
                {
                    foreach (var oldCt in oldArchetype.ComponentTypes)
                    {
                        int oldCompIdx = oldArchetype.GetComponentIndex(oldCt);
                        int newCompIdx = newArchetype.GetComponentIndex(oldCt);
                        var oldArr = oldChunk.ComponentArrays[oldCompIdx];
                        var newArr = newChunk.ComponentArrays[newCompIdx];
                        newArr.SetValue(oldArr.GetValue(oldIndex), newIndex);
                    }

                    oldChunk.RemoveEntity(oldIndex);
                    oldArchetype.RemoveEntityCount();
                    oldArchetype.ReleaseChunkIfEmpty(oldChunk);

                    // 更新被移动实体的位置
                    if (oldIndex < oldChunk.Count)
                    {
                        var movedEntity = oldChunk.Entities[oldIndex];
                        ref var movedInfo = ref _entities[movedEntity.Index];
                        movedInfo.IndexInChunk = oldIndex;
                    }
                }

                // 设置新组件
                int compIndex = newArchetype.GetComponentIndex(ct);
                newChunk.SetComponent<T>(newIndex, compIndex, component);

                info.Archetype = newArchetype;
                info.Chunk = newChunk;
                info.IndexInChunk = newIndex;
                newArchetype.AddEntityCount();

                return component;
            }
        }

        public bool HasComponent<T>(Entity entity) where T : struct, IComponent
        {
            if (!IsValid(entity)) return false;
            var ct = ComponentType.Get<T>();
            ref var info = ref _entities[entity.Index];
            return info.Archetype != null && info.Archetype.HasComponent(ct);
        }

        public T GetComponent<T>(Entity entity) where T : struct, IComponent
        {
            if (!IsValid(entity)) return default;
            var ct = ComponentType.Get<T>();
            ref var info = ref _entities[entity.Index];
            if (info.Archetype == null || !info.Archetype.HasComponent(ct))
                return default;
            int compIdx = info.Archetype.GetComponentIndex(ct);
            return info.Chunk.GetComponent<T>(info.IndexInChunk, compIdx);
        }

        public void SetComponent<T>(Entity entity, T component) where T : struct, IComponent
        {
            if (!IsValid(entity)) return;
            var ct = ComponentType.Get<T>();
            ref var info = ref _entities[entity.Index];
            if (info.Archetype == null || !info.Archetype.HasComponent(ct))
                return;
            int compIdx = info.Archetype.GetComponentIndex(ct);
            info.Chunk.SetComponent<T>(info.IndexInChunk, compIdx, component);
        }

        public bool RemoveComponent<T>(Entity entity) where T : struct, IComponent
        {
            lock (_lock)
            {
                if (!IsValid(entity)) return false;
                var ct = ComponentType.Get<T>();
                ref var info = ref _entities[entity.Index];
                if (info.Archetype == null || !info.Archetype.HasComponent(ct))
                    return false;

                var oldArchetype = info.Archetype;
                var oldChunk = info.Chunk;
                int oldIndex = info.IndexInChunk;

                var newTypes = oldArchetype.ComponentTypes.Where(t => t != ct).ToArray();
                var newArchetype = newTypes.Length > 0 ? GetOrCreateArchetype(newTypes) : null;
                Chunk newChunk = null;
                int newIndex = -1;

                if (newArchetype != null)
                {
                    newChunk = newArchetype.AcquireChunk();
                    newIndex = newChunk.AddEntity(entity);

                    foreach (var oldCt in newTypes)
                    {
                        int oldCompIdx = oldArchetype.GetComponentIndex(oldCt);
                        int newCompIdx = newArchetype.GetComponentIndex(oldCt);
                        var oldArr = oldChunk.ComponentArrays[oldCompIdx];
                        var newArr = newChunk.ComponentArrays[newCompIdx];
                        newArr.SetValue(oldArr.GetValue(oldIndex), newIndex);
                    }
                    newArchetype.AddEntityCount();
                }

                oldChunk.RemoveEntity(oldIndex);
                oldArchetype.RemoveEntityCount();
                oldArchetype.ReleaseChunkIfEmpty(oldChunk);

                if (oldIndex < oldChunk.Count)
                {
                    var movedEntity = oldChunk.Entities[oldIndex];
                    ref var movedInfo = ref _entities[movedEntity.Index];
                    movedInfo.IndexInChunk = oldIndex;
                }

                info.Archetype = newArchetype;
                info.Chunk = newChunk;
                info.IndexInChunk = newIndex;

                return true;
            }
        }

        internal Archetype GetOrCreateArchetype(ComponentType[] types)
        {
            int hash = ComputeArchetypeHash(types);
            lock (_lock)
            {
                if (_archetypes.TryGetValue(hash, out var archetype))
                {
                    // 需要验证组件类型完全一致（防止哈希冲突）
                    if (archetype.ComponentTypes.Length == types.Length)
                    {
                        bool match = true;
                        for (int i = 0; i < types.Length; i++)
                        {
                            if (archetype.ComponentTypes[i] != types[i])
                            {
                                match = false;
                                break;
                            }
                        }
                        if (match) return archetype;
                    }
                }

                var newArchetype = new Archetype(types);
                _archetypes[hash] = newArchetype;
                return newArchetype;
            }
        }

        internal void GetAllArchetypes(List<Archetype> result)
        {
            lock (_lock)
            {
                result.AddRange(_archetypes.Values);
            }
        }

        private static int ComputeArchetypeHash(ComponentType[] types)
        {
            int hash = 0;
            foreach (var t in types)
                hash = hash * 31 + t.GetHashCode();
            return hash;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _archetypes.Clear();
                _entityNames.Clear();
                _freeIndices.Clear();
                _entityCount = 0;
                _entities = new EntityInfo[1024];
                EntityCreated = null;
                EntityDestroyed = null;
            }
        }
    }

    // ============================
    // 6. EntityQuery
    // ============================
    public sealed class EntityQuery
    {
        private readonly EntityManager _entityManager;
        private readonly ComponentType[] _all;
        private readonly ComponentType[] _any;
        private readonly ComponentType[] _none;
        private List<Archetype> _matchedArchetypes = new();
        private bool _isDirty = true;

        public EntityQuery(EntityManager entityManager, ComponentType[] all = null, ComponentType[] any = null, ComponentType[] none = null)
        {
            _entityManager = entityManager;
            _all = all;
            _any = any;
            _none = none;
        }

        public void Invalidate() => _isDirty = true;

        private void UpdateMatchedArchetypes()
        {
            if (!_isDirty) return;
            _matchedArchetypes.Clear();

            var allArchetypes = new List<Archetype>();
            _entityManager.GetAllArchetypes(allArchetypes);

            foreach (var archetype in allArchetypes)
            {
                if (archetype.MatchesQuery(_all, _any, _none))
                    _matchedArchetypes.Add(archetype);
            }
            _isDirty = false;
        }

        public void ForEach<T1>(RefAction<T1> action)
            where T1 : struct, IComponent
        {
            UpdateMatchedArchetypes();
            var ct1 = ComponentType.Get<T1>();

            foreach (var archetype in _matchedArchetypes)
            {
                int idx1 = archetype.GetComponentIndex(ct1);
                if (idx1 < 0) continue;

                foreach (var chunk in archetype.Chunks)
                {
                    for (int i = 0; i < chunk.Count; i++)
                    {
                        var entity = chunk.Entities[i];
                        ref var c1 = ref chunk.GetComponent<T1>(i, idx1);
                        action(entity, ref c1);
                    }
                }
            }
        }

        public void ForEach<T1, T2>(RefAction<T1, T2> action)
            where T1 : struct, IComponent
            where T2 : struct, IComponent
        {
            UpdateMatchedArchetypes();
            var ct1 = ComponentType.Get<T1>();
            var ct2 = ComponentType.Get<T2>();

            foreach (var archetype in _matchedArchetypes)
            {
                int idx1 = archetype.GetComponentIndex(ct1);
                int idx2 = archetype.GetComponentIndex(ct2);
                if (idx1 < 0 || idx2 < 0) continue;

                foreach (var chunk in archetype.Chunks)
                {
                    for (int i = 0; i < chunk.Count; i++)
                    {
                        var entity = chunk.Entities[i];
                        ref var c1 = ref chunk.GetComponent<T1>(i, idx1);
                        ref var c2 = ref chunk.GetComponent<T2>(i, idx2);
                        action(entity, ref c1, ref c2);
                    }
                }
            }
        }

        public void ForEach<T1, T2, T3>(RefAction<T1, T2, T3> action)
            where T1 : struct, IComponent
            where T2 : struct, IComponent
            where T3 : struct, IComponent
        {
            UpdateMatchedArchetypes();
            var ct1 = ComponentType.Get<T1>();
            var ct2 = ComponentType.Get<T2>();
            var ct3 = ComponentType.Get<T3>();

            foreach (var archetype in _matchedArchetypes)
            {
                int idx1 = archetype.GetComponentIndex(ct1);
                int idx2 = archetype.GetComponentIndex(ct2);
                int idx3 = archetype.GetComponentIndex(ct3);
                if (idx1 < 0 || idx2 < 0 || idx3 < 0) continue;

                foreach (var chunk in archetype.Chunks)
                {
                    for (int i = 0; i < chunk.Count; i++)
                    {
                        var entity = chunk.Entities[i];
                        ref var c1 = ref chunk.GetComponent<T1>(i, idx1);
                        ref var c2 = ref chunk.GetComponent<T2>(i, idx2);
                        ref var c3 = ref chunk.GetComponent<T3>(i, idx3);
                        action(entity, ref c1, ref c2, ref c3);
                    }
                }
            }
        }

        public List<Entity> ToEntityList()
        {
            UpdateMatchedArchetypes();
            var result = new List<Entity>();
            foreach (var archetype in _matchedArchetypes)
            {
                foreach (var chunk in archetype.Chunks)
                {
                    for (int i = 0; i < chunk.Count; i++)
                        result.Add(chunk.Entities[i]);
                }
            }
            return result;
        }

        public int GetEntityCount()
        {
            UpdateMatchedArchetypes();
            int count = 0;
            foreach (var archetype in _matchedArchetypes)
                count += archetype.EntityCount;
            return count;
        }
    }

    public static class EntityQueryBuilder
    {
        public static EntityQuery WithAll<T1>(this EntityManager em) where T1 : struct, IComponent
            => new EntityQuery(em, all: new[] { ComponentType.Get<T1>() });
        public static EntityQuery WithAll<T1, T2>(this EntityManager em)
            where T1 : struct, IComponent where T2 : struct, IComponent
            => new EntityQuery(em, all: new[] { ComponentType.Get<T1>(), ComponentType.Get<T2>() });
        public static EntityQuery WithAll<T1, T2, T3>(this EntityManager em)
            where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
            => new EntityQuery(em, all: new[] { ComponentType.Get<T1>(), ComponentType.Get<T2>(), ComponentType.Get<T3>() });
    }

    // ============================
    // 7. JobSystem
    // ============================
    public interface IJobForEach<T1>
        where T1 : struct, IComponent
    {
        void Execute(Entity entity, ref T1 component1);
    }

    public interface IJobForEach<T1, T2>
        where T1 : struct, IComponent
        where T2 : struct, IComponent
    {
        void Execute(Entity entity, ref T1 component1, ref T2 component2);
    }

    public interface IJobForEach<T1, T2, T3>
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
    {
        void Execute(Entity entity, ref T1 component1, ref T2 component2, ref T3 component3);
    }

    public static class JobSystem
    {
        public static void Run<TJob, T1>(EntityQuery query, TJob job)
            where TJob : struct, IJobForEach<T1>
            where T1 : struct, IComponent
        {
            var ct1 = ComponentType.Get<T1>();
            var archetypes = new List<Archetype>();
            // 这里简化处理：EntityQuery需要暴露匹配的原型列表
            // 实际实现中可通过内部方法获取
        }

        // 简化版并行执行：对查询结果使用Parallel.ForEach
        public static void ParallelForEach<T1>(EntityQuery query, RefAction<T1> action)
            where T1 : struct, IComponent
        {
            var ct1 = ComponentType.Get<T1>();
            // 由于Chunk级别的SOA存储，可以按Chunk并行
            // 这里使用反射获取匹配的archetypes（简化实现）
            // 在实际生产代码中，EntityQuery应暴露内部匹配列表
            query.ForEach(action); // Fallback to sequential for safety
        }
    }

    // ============================
    // 8. System 与 SystemGroup
    // ============================
    public enum SystemGroupType
    {
        Initialization,
        PreUpdate,
        Update,
        PostUpdate,
        Presentation
    }

    public interface ISystem
    {
        string Name { get; }
        void Update(float dt, EntityComponentSystem ecs);
    }

    public abstract class SystemBase : ISystem
    {
        public abstract string Name { get; }
        protected World World { get; private set; }
        protected EntityManager EntityManager => World?.EntityManager;

        public abstract void Update(float dt, EntityComponentSystem ecs);

        internal void SetWorld(World world) => World = world;
    }

    public sealed class SystemGroup
    {
        public SystemGroupType Type { get; }
        private readonly List<ISystem> _systems = new();
        public IReadOnlyList<ISystem> Systems => _systems;

        public SystemGroup(SystemGroupType type)
        {
            Type = type;
        }

        public void AddSystem(ISystem system) => _systems.Add(system);
        public void RemoveSystem(ISystem system) => _systems.Remove(system);
        public void InsertSystem(int index, ISystem system) => _systems.Insert(index, system);
        public void Clear() => _systems.Clear();

        public void Update(float dt, EntityComponentSystem ecs)
        {
            foreach (var system in _systems)
            {
                if (system is SystemBase sb) sb.SetWorld(ecs.World);
                system.Update(dt, ecs);
            }
        }
    }

    // ============================
    // 9. World（顶级容器）
    // ============================
    public sealed class World
    {
        public EntityManager EntityManager { get; }
        public IReadOnlyDictionary<SystemGroupType, SystemGroup> SystemGroups => _systemGroups;

        private readonly Dictionary<SystemGroupType, SystemGroup> _systemGroups = new();
        private readonly Dictionary<string, EntityQuery> _queries = new();

        public World(string name = "DefaultWorld", int initialCapacity = 1024)
        {
            Name = name;
            EntityManager = new EntityManager(initialCapacity);

            foreach (SystemGroupType gt in Enum.GetValues<SystemGroupType>())
            {
                _systemGroups[gt] = new SystemGroup(gt);
            }
        }

        public string Name { get; }

        /// <summary>Back-reference to the owning EntityComponentSystem (set by EntityComponentSystem ctor).</summary>
        public EntityComponentSystem ECS { get; internal set; }

        public Entity CreateEntity(string name = null, Vector3? position = null, Quaternion? rotation = null, Vector3? scale = null)
        {
            var entity = EntityManager.CreateEntity(name);
            EntityManager.AddComponent(entity, new TransformComponent
            {
                Position = position ?? Vector3.Zero,
                Rotation = rotation ?? Quaternion.Identity,
                Scale = scale ?? new Vector3(1, 1, 1)
            });
            return entity;
        }

        public void DestroyEntity(Entity entity) => EntityManager.DestroyEntity(entity);

        public void AddSystem(ISystem system, SystemGroupType group = SystemGroupType.Update)
        {
            _systemGroups[group].AddSystem(system);
        }

        public void RemoveSystem(ISystem system, SystemGroupType group)
        {
            _systemGroups[group].RemoveSystem(system);
        }

        public void RemoveSystem(ISystem system)
        {
            foreach (var group in _systemGroups.Values)
                group.RemoveSystem(system);
        }

        public EntityQuery GetQuery(ComponentType[] all = null, ComponentType[] any = null, ComponentType[] none = null)
        {
            var key = BuildQueryKey(all, any, none);
            if (!_queries.TryGetValue(key, out var query))
            {
                query = new EntityQuery(EntityManager, all, any, none);
                _queries[key] = query;
            }
            return query;
        }

        public void Update(float dt)
        {
            foreach (var group in _systemGroups.Values.OrderBy(g => (int)g.Type))
            {
                group.Update(dt, ECS);
            }
        }

        public void UpdateGroup(float dt, SystemGroupType groupType)
        {
            if (_systemGroups.TryGetValue(groupType, out var group))
            {
                group.Update(dt, ECS);
            }
        }

        public void Dispose()
        {
            foreach (var group in _systemGroups.Values)
                group.Clear();
            _systemGroups.Clear();
            _queries.Clear();
            EntityManager.Dispose();
        }

        private static string BuildQueryKey(ComponentType[] all, ComponentType[] any, ComponentType[] none)
        {
            var sb = new System.Text.StringBuilder();
            if (all != null) { sb.Append("A"); foreach (var t in all.OrderBy(t => t.TypeIndex)) sb.Append(t.TypeIndex); }
            if (any != null) { sb.Append("Y"); foreach (var t in any.OrderBy(t => t.TypeIndex)) sb.Append(t.TypeIndex); }
            if (none != null) { sb.Append("N"); foreach (var t in none.OrderBy(t => t.TypeIndex)) sb.Append(t.TypeIndex); }
            return sb.ToString();
        }
    }

    // ============================
    // 10. 并行System基类与Job调度
    // ============================
    public abstract class ParallelSystemBase<T1> : SystemBase
        where T1 : struct, IComponent
    {
        private EntityQuery _query;

        public sealed override void Update(float dt, EntityComponentSystem ecs)
        {
            if (_query == null)
                _query = new EntityQuery(ecs.World.EntityManager, all: new[] { ComponentType.Get<T1>() });

            var ct1 = ComponentType.Get<T1>();
            var archetypes = new List<Archetype>();
            ecs.World.EntityManager.GetAllArchetypes(archetypes);

            var matched = new List<(Archetype archetype, int idx1)>();
            foreach (var archetype in archetypes)
            {
                if (!archetype.HasComponent(ct1)) continue;
                matched.Add((archetype, archetype.GetComponentIndex(ct1)));
            }

            Parallel.ForEach(matched, match =>
            {
                var archetype = match.archetype;
                int idx1 = match.idx1;
                foreach (var chunk in archetype.Chunks)
                {
                    for (int i = 0; i < chunk.Count; i++)
                    {
                        var entity = chunk.Entities[i];
                        ref var c1 = ref chunk.GetComponent<T1>(i, idx1);
                        Execute(entity, ref c1);
                    }
                }
            });
        }

        public abstract void Execute(Entity entity, ref T1 component1);
    }

    public abstract class ParallelSystemBase<T1, T2> : SystemBase
        where T1 : struct, IComponent
        where T2 : struct, IComponent
    {
        public sealed override void Update(float dt, EntityComponentSystem ecs)
        {
            var ct1 = ComponentType.Get<T1>();
            var ct2 = ComponentType.Get<T2>();
            var archetypes = new List<Archetype>();
            ecs.World.EntityManager.GetAllArchetypes(archetypes);

            var matched = new List<(Archetype archetype, int idx1, int idx2)>();
            foreach (var archetype in archetypes)
            {
                if (!archetype.HasComponent(ct1) || !archetype.HasComponent(ct2)) continue;
                matched.Add((archetype, archetype.GetComponentIndex(ct1), archetype.GetComponentIndex(ct2)));
            }

            Parallel.ForEach(matched, match =>
            {
                var archetype = match.archetype;
                int idx1 = match.idx1;
                int idx2 = match.idx2;
                foreach (var chunk in archetype.Chunks)
                {
                    for (int i = 0; i < chunk.Count; i++)
                    {
                        var entity = chunk.Entities[i];
                        ref var c1 = ref chunk.GetComponent<T1>(i, idx1);
                        ref var c2 = ref chunk.GetComponent<T2>(i, idx2);
                        Execute(entity, ref c1, ref c2);
                    }
                }
            });
        }

        public abstract void Execute(Entity entity, ref T1 component1, ref T2 component2);
    }

    public abstract class ParallelSystemBase<T1, T2, T3> : SystemBase
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
    {
        public sealed override void Update(float dt, EntityComponentSystem ecs)
        {
            var ct1 = ComponentType.Get<T1>();
            var ct2 = ComponentType.Get<T2>();
            var ct3 = ComponentType.Get<T3>();
            var archetypes = new List<Archetype>();
            ecs.World.EntityManager.GetAllArchetypes(archetypes);

            var matched = new List<(Archetype archetype, int idx1, int idx2, int idx3)>();
            foreach (var archetype in archetypes)
            {
                if (!archetype.HasComponent(ct1) || !archetype.HasComponent(ct2) || !archetype.HasComponent(ct3)) continue;
                matched.Add((archetype, archetype.GetComponentIndex(ct1), archetype.GetComponentIndex(ct2), archetype.GetComponentIndex(ct3)));
            }

            Parallel.ForEach(matched, match =>
            {
                var archetype = match.archetype;
                int idx1 = match.idx1;
                int idx2 = match.idx2;
                int idx3 = match.idx3;
                foreach (var chunk in archetype.Chunks)
                {
                    for (int i = 0; i < chunk.Count; i++)
                    {
                        var entity = chunk.Entities[i];
                        ref var c1 = ref chunk.GetComponent<T1>(i, idx1);
                        ref var c2 = ref chunk.GetComponent<T2>(i, idx2);
                        ref var c3 = ref chunk.GetComponent<T3>(i, idx3);
                        Execute(entity, ref c1, ref c2, ref c3);
                    }
                }
            });
        }

        public abstract void Execute(Entity entity, ref T1 component1, ref T2 component2, ref T3 component3);
    }

    // ============================
    // 11. 向后兼容层
    // ============================
    public sealed class EntityComponentSystem : IDisposable
    {
        private readonly World _world;
        private readonly Dictionary<int, Entity> _legacyIdToEntity = new();
        private int _legacyId = 1;

        public int EntityCount => _world.EntityManager.EntityCount;
        public int ComponentCount
        {
            get
            {
                int count = 0;
                var archetypes = new List<Archetype>();
                _world.EntityManager.GetAllArchetypes(archetypes);
                foreach (var a in archetypes)
                    count += a.EntityCount * a.ComponentTypes.Length;
                return count;
            }
        }

        public event Action<int>? EntityCreated;
        public event Action<int>? EntityDestroyed;

        public EntityComponentSystem()
        {
            _world = new World("LegacyWorld");
            _world.ECS = this;
        }

        public int CreateEntity(string name = null, Vector3? position = null, Quaternion? rotation = null, Vector3? scale = null)
        {
            var entity = _world.CreateEntity(name, position, rotation, scale);
            int legacyId = _legacyId++;
            _legacyIdToEntity[legacyId] = entity;
            EntityCreated?.Invoke(legacyId);
            return legacyId;
        }

        public void DestroyEntity(int entityId)
        {
            if (_legacyIdToEntity.TryGetValue(entityId, out var entity))
            {
                _world.DestroyEntity(entity);
                _legacyIdToEntity.Remove(entityId);
                EntityDestroyed?.Invoke(entityId);
            }
        }

        public bool HasEntity(int id) => _legacyIdToEntity.TryGetValue(id, out var e) && _world.EntityManager.IsValid(e);

        public string GetEntityName(int id) => _legacyIdToEntity.TryGetValue(id, out var e) ? _world.EntityManager.GetName(e) : "Entity";
        public void SetEntityName(int id, string name)
        {
            if (_legacyIdToEntity.TryGetValue(id, out var e))
                _world.EntityManager.SetName(e, name);
        }

        public T AddComponent<T>(int entityId, T component) where T : struct, IComponent
        {
            if (_legacyIdToEntity.TryGetValue(entityId, out var e))
                return _world.EntityManager.AddComponent(e, component);
            return component;
        }

        public bool HasComponent<T>(int entityId) where T : struct, IComponent
        {
            return _legacyIdToEntity.TryGetValue(entityId, out var e) && _world.EntityManager.HasComponent<T>(e);
        }

        public T GetComponent<T>(int entityId) where T : struct, IComponent
        {
            if (_legacyIdToEntity.TryGetValue(entityId, out var e))
                return _world.EntityManager.GetComponent<T>(e);
            return default;
        }

        public void SetComponent<T>(int entityId, T component) where T : struct, IComponent
        {
            if (_legacyIdToEntity.TryGetValue(entityId, out var e))
                _world.EntityManager.SetComponent(e, component);
        }

        public bool RemoveComponent<T>(int entityId) where T : struct, IComponent
        {
            return _legacyIdToEntity.TryGetValue(entityId, out var e) && _world.EntityManager.RemoveComponent<T>(e);
        }

        public List<int> QueryWithComponent<T>() where T : struct, IComponent
        {
            var result = new List<int>();
            var query = new EntityQuery(_world.EntityManager, all: new[] { ComponentType.Get<T>() });
            var entities = query.ToEntityList();
            foreach (var entity in entities)
            {
                foreach (var kvp in _legacyIdToEntity)
                {
                    if (kvp.Value == entity) { result.Add(kvp.Key); break; }
                }
            }
            return result;
        }

        public List<int> QueryWithComponents<T1, T2>()
            where T1 : struct, IComponent
            where T2 : struct, IComponent
        {
            var result = new List<int>();
            var query = new EntityQuery(_world.EntityManager, all: new[] { ComponentType.Get<T1>(), ComponentType.Get<T2>() });
            var entities = query.ToEntityList();
            foreach (var entity in entities)
            {
                foreach (var kvp in _legacyIdToEntity)
                {
                    if (kvp.Value == entity) { result.Add(kvp.Key); break; }
                }
            }
            return result;
        }

        public IEnumerable<int> AllEntities => _legacyIdToEntity.Keys.Where(id => HasEntity(id));

        public void AddSystem(ISystem system) => _world.AddSystem(system);
        public void RemoveSystem(ISystem system) => _world.RemoveSystem(system);

        public void Update(float dt) => _world.Update(dt);

        public void Clear()
        {
            foreach (var id in _legacyIdToEntity.Keys.ToList())
                DestroyEntity(id);
            _legacyIdToEntity.Clear();
            _legacyId = 1;
        }

        public void Dispose()
        {
            _world.Dispose();
            _legacyIdToEntity.Clear();
        }

        // 提供对新World的访问，便于迁移到新API
        public World World => _world;
    }
}
