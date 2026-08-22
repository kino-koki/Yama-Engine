using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using ShanyuEngine.Rendering;

namespace ShanyuEngine.GameFramework
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - 真实场景管理 (SceneManagementReal)
    //
    //   * SceneReal: 场景（名称/根Actors/IsLoaded/IsActive/BuildIndex/Path）
    //   * SceneManagerReal: 场景管理器（Load/Unload/GetActive/SetActive/生命周期）
    //   * SceneStreamingReal: 流式加载（WorldStreamingGrid/网格分区/优先级/异步加载）
    // ════════════════════════════════════════════════════════════════════════

    // ════════════════════════════════════════════════════════════════════════
    // 场景生命周期状态
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>场景生命周期状态。</summary>
    public enum SceneLifecycleState
    {
        /// <summary>未加载。</summary>
        Unloaded,
        /// <summary>正在加载。</summary>
        Loading,
        /// <summary>已加载（未激活）。</summary>
        Loaded,
        /// <summary>正在激活。</summary>
        Activating,
        /// <summary>已激活（活跃场景）。</summary>
        Active,
        /// <summary>正在卸载。</summary>
        Unloading
    }

    // ════════════════════════════════════════════════════════════════════════
    // 加载模式
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>场景加载模式。</summary>
    public enum LoadSceneMode
    {
        /// <summary>单场景模式：卸载当前场景 → 加载新场景。</summary>
        Single,
        /// <summary>叠加模式：保留当前场景 → 叠加加载新场景。</summary>
        Additive,
        /// <summary>卸载叠加：仅卸载叠加的场景。</summary>
        UnloadAdditive
    }

    // ════════════════════════════════════════════════════════════════════════
    // SceneReal: 场景
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 场景：包含一组GameObjectReal的容器。拥有名称、加载状态、BuildIndex和路径。
    /// </summary>
    public class SceneReal
    {
        /// <summary>场景名称。</summary>
        public string Name { get; set; }

        /// <summary>场景路径（资源路径）。</summary>
        public string Path { get; set; }

        /// <summary>BuildIndex（构建索引）。</summary>
        public int BuildIndex { get; set; } = -1;

        /// <summary>是否已加载。</summary>
        public bool IsLoaded => State == SceneLifecycleState.Loaded || State == SceneLifecycleState.Active;

        /// <summary>是否激活。</summary>
        public bool IsActive => State == SceneLifecycleState.Active;

        /// <summary>场景生命周期状态。</summary>
        public SceneLifecycleState State { get; internal set; } = SceneLifecycleState.Unloaded;

        /// <summary>根GameObject列表。</summary>
        public IReadOnlyList<GameObjectReal> RootGameObjects => _rootObjects;

        /// <summary>场景中所有GameObject列表（递归）。</summary>
        public IReadOnlyList<GameObjectReal> AllGameObjects
        {
            get
            {
                var all = new List<GameObjectReal>();
                CollectAllGameObjects(_rootObjects, all);
                return all;
            }
        }

        /// <summary>场景中GameObject总数。</summary>
        public int GameObjectCount => AllGameObjects.Count;

        private readonly List<GameObjectReal> _rootObjects = new();

        // 场景加载/卸载回调
        public event Action<SceneReal>? SceneLoaded;
        public event Action<SceneReal>? SceneUnloaded;
        public event Action<SceneReal>? SceneActivated;
        public event Action<SceneReal>? SceneDeactivated;

        public SceneReal(string name, string path = null)
        {
            Name = name;
            Path = path;
        }

        /// <summary>添加根GameObject到场景。</summary>
        internal void AddRootObject(GameObjectReal obj)
        {
            if (obj == null) throw new ArgumentNullException(nameof(obj));
            obj.Scene = this;
            _rootObjects.Add(obj);
        }

        /// <summary>从场景移除根GameObject。</summary>
        internal bool RemoveRootObject(GameObjectReal obj)
        {
            if (!_rootObjects.Contains(obj)) return false;
            obj.Scene = null;
            _rootObjects.Remove(obj);
            return true;
        }

        /// <summary>查找场景中的GameObject（按名称，递归）。</summary>
        public GameObjectReal FindGameObject(string name)
        {
            foreach (var root in _rootObjects)
            {
                if (root.Name == name) return root;
                var found = root.Find(name);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>递归收集所有GameObject。</summary>
        private static void CollectAllGameObjects(IReadOnlyList<GameObjectReal> roots, List<GameObjectReal> result)
        {
            foreach (var obj in roots)
            {
                result.Add(obj);
                CollectAllGameObjects(obj.Children, result);
            }
        }

        /// <summary>获取场景序列化数据（用于跨场景传输/保存）。</summary>
        internal string SerializeSceneData()
        {
            var sb = new StringBuilder();
            sb.Append($"{{\"name\":\"{Name}\",\"path\":\"{Path}\",\"buildIndex\":{BuildIndex},\"roots\":[");
            for (int i = 0; i < _rootObjects.Count; i++)
            {
                if (i > 0) sb.Append(",");
                SerializeGameObject(_rootObjects[i], sb);
            }
            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>序列化单个GameObject的基本信息。</summary>
        private static void SerializeGameObject(GameObjectReal obj, StringBuilder sb)
        {
            sb.Append($"{{\"name\":\"{obj.Name}\",\"tag\":\"{obj.Tag}\",\"layer\":{obj.Layer},");
            sb.Append($"\"pos\":[{obj.Transform.Position.X},{obj.Transform.Position.Y},{obj.Transform.Position.Z}],");
            sb.Append($"\"children\":[");
            for (int i = 0; i < obj.Children.Count; i++)
            {
                if (i > 0) sb.Append(",");
                SerializeGameObject(obj.Children[i], sb);
            }
            sb.Append("]}}");
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // SceneManagerReal: 场景管理器
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 场景管理器：管理场景的加载、卸载、激活和生命周期。
    /// </summary>
    public sealed class SceneManagerReal
    {
        /// <summary>所有已加载场景。</summary>
        public IReadOnlyList<SceneReal> LoadedScenes => _loadedScenes;

        /// <summary>已加载场景数量。</summary>
        public int SceneCount => _loadedScenes.Count;

        /// <summary>当前活跃场景。</summary>
        public SceneReal ActiveScene => _activeScene;

        /// <summary>场景加载完成事件。</summary>
        public event Action<SceneReal>? OnSceneLoaded;

        /// <summary>场景卸载完成事件。</summary>
        public event Action<SceneReal>? OnSceneUnloaded;

        /// <summary>活跃场景变更事件。</summary>
        public event Action<SceneReal, SceneReal>? OnActiveSceneChanged;

        private readonly List<SceneReal> _loadedScenes = new();
        private SceneReal _activeScene;
        private readonly Dictionary<string, SceneReal> _sceneByName = new();
        private readonly Dictionary<int, SceneReal> _sceneByBuildIndex = new();
        private readonly object _lock = new();

        // 场景数据提供器（从文件读取场景数据）
        public Func<string, string> SceneDataProvider { get; set; }

        /// <summary>
        /// 加载场景。
        /// LoadMode:
        ///   - Single: 卸载所有当前场景 → 加载新场景
        ///   - Additive: 保留当前场景 → 叠加加载
        ///   - UnloadAdditive: 仅卸载最后加载的叠加场景
        ///
        /// 真实流程: 读取场景文件 → 反序列化Actor树 → 实例化 → Awake → Start → 激活
        /// </summary>
        public SceneReal LoadScene(string sceneName, LoadSceneMode mode = LoadSceneMode.Single)
        {
            lock (_lock)
            {
                switch (mode)
                {
                    case LoadSceneMode.UnloadAdditive:
                        return HandleUnloadAdditive();

                    case LoadSceneMode.Single:
                        // 卸载所有已加载场景
                        UnloadAllScenesInternal();
                        break;

                    case LoadSceneMode.Additive:
                        // 叠加模式：不做特殊处理
                        break;
                }

                // 检查是否已加载
                if (_sceneByName.TryGetValue(sceneName, out var existingScene))
                {
                    if (existingScene.IsLoaded)
                        return existingScene;
                    // 已有实例但未加载，重新加载
                }

                // 创建场景
                var scene = new SceneReal(sceneName, $"Scenes/{sceneName}.shanyuscene");
                scene.State = SceneLifecycleState.Loading;

                // 读取场景数据（使用SceneDataProvider或空数据）
                string sceneData = null;
                if (SceneDataProvider != null)
                {
                    try
                    {
                        sceneData = SceneDataProvider(scene.Path);
                    }
                    catch
                    {
                        sceneData = null; // 文件不存在，创建空场景
                    }
                }

                // 反序列化场景 → 创建GameObject树
                if (!string.IsNullOrEmpty(sceneData))
                {
                    DeserializeSceneData(scene, sceneData);
                }

                // 初始化所有GameObject（Awake → Start）
                InitializeSceneGameObjects(scene);

                // 设置为已加载状态
                scene.State = SceneLifecycleState.Loaded;
                _loadedScenes.Add(scene);
                _sceneByName[sceneName] = scene;

                // 单场景模式自动设为活跃
                if (mode == LoadSceneMode.Single)
                {
                    SetActiveScene(scene);
                }
                else if (_activeScene == null)
                {
                    SetActiveScene(scene);
                }

                OnSceneLoaded?.Invoke(scene);
                return scene;
            }
        }

        /// <summary>
        /// 卸载场景。
        /// 真实流程: 遍历Actor树 → OnDestroy → 释放资源 → 从活跃场景列表移除。
        /// </summary>
        public void UnloadScene(SceneReal scene)
        {
            if (scene == null) return;
            lock (_lock)
            {
                if (!_loadedScenes.Contains(scene))
                    return;

                scene.State = SceneLifecycleState.Unloading;

                // 递归销毁所有GameObject
                var allObjects = scene.AllGameObjects;
                for (int i = allObjects.Count - 1; i >= 0; i--)
                {
                    allObjects[i].Destroy();
                }
                scene._rootObjects.Clear();

                scene.State = SceneLifecycleState.Unloaded;
                _loadedScenes.Remove(scene);
                _sceneByName.Remove(scene.Name);
                _sceneByBuildIndex.Remove(scene.BuildIndex);

                // 如果卸载的是活跃场景，切换到第一个剩余场景
                if (_activeScene == scene)
                {
                    _activeScene = _loadedScenes.Count > 0 ? _loadedScenes[0] : null;
                    OnActiveSceneChanged?.Invoke(scene, _activeScene);
                }

                OnSceneUnloaded?.Invoke(scene);
            }
        }

        /// <summary>
        /// 设置活跃场景。
        /// </summary>
        public void SetActiveScene(SceneReal scene)
        {
            if (scene == null) throw new ArgumentNullException(nameof(scene));
            lock (_lock)
            {
                if (!_loadedScenes.Contains(scene))
                    throw new InvalidOperationException("Cannot set an unloaded scene as active.");

                var oldActive = _activeScene;

                // 停用旧场景
                if (oldActive != null && oldActive != scene)
                {
                    oldActive.State = SceneLifecycleState.Loaded;
                    oldActive.SceneDeactivated?.Invoke(oldActive);
                }

                _activeScene = scene;
                scene.State = SceneLifecycleState.Active;
                scene.SceneActivated?.Invoke(scene);

                OnActiveSceneChanged?.Invoke(oldActive, scene);
            }
        }

        /// <summary>获取活跃场景。</summary>
        public SceneReal GetActiveScene()
        {
            return _activeScene;
        }

        /// <summary>按索引获取已加载场景。</summary>
        public SceneReal GetSceneAt(int index)
        {
            if (index < 0 || index >= _loadedScenes.Count)
                return null;
            return _loadedScenes[index];
        }

        /// <summary>按名称获取场景。</summary>
        public SceneReal GetSceneByName(string name)
        {
            _sceneByName.TryGetValue(name, out var scene);
            return scene;
        }

        /// <summary>按BuildIndex获取场景。</summary>
        public SceneReal GetSceneByBuildIndex(int buildIndex)
        {
            _sceneByBuildIndex.TryGetValue(buildIndex, out var scene);
            return scene;
        }

        // ═══════════════════════════════════════════════════════════════════
        // 内部方法
        // ═══════════════════════════════════════════════════════════════════

        private void UnloadAllScenesInternal()
        {
            for (int i = _loadedScenes.Count - 1; i >= 0; i--)
            {
                var scene = _loadedScenes[i];
                scene.State = SceneLifecycleState.Unloading;

                var allObjects = scene.AllGameObjects;
                for (int j = allObjects.Count - 1; j >= 0; j--)
                    allObjects[j].Destroy();

                scene._rootObjects.Clear();
                scene.State = SceneLifecycleState.Unloaded;
                scene.SceneUnloaded?.Invoke(scene);
                OnSceneUnloaded?.Invoke(scene);
            }

            _loadedScenes.Clear();
            _sceneByName.Clear();
            _sceneByBuildIndex.Clear();
            _activeScene = null;
        }

        private SceneReal HandleUnloadAdditive()
        {
            // 卸载最后加载的叠加场景
            if (_loadedScenes.Count > 0)
            {
                var lastScene = _loadedScenes[_loadedScenes.Count - 1];
                if (lastScene != _activeScene) // 不卸载主场景
                {
                    UnloadScene(lastScene);
                    return lastScene;
                }
            }
            return null;
        }

        /// <summary>
        /// 反序列化场景数据 → 创建GameObject树。
        /// 真实实现：解析场景JSON → 创建GameObject → 还原层级 → 设置属性。
        /// </summary>
        private void DeserializeSceneData(SceneReal scene, string sceneData)
        {
            // 简化的JSON解析（不使用外部JSON库，手动解析基础结构）
            // 真实场景中会用完整的JSON解析器或引擎自定义格式
            try
            {
                // 查找"roots"数组
                int rootsIdx = sceneData.IndexOf("\"roots\":[", StringComparison.Ordinal);
                if (rootsIdx < 0) return;

                int startIdx = rootsIdx + "\"roots\":[".Length;
                var rootObjects = ParseGameObjectArray(sceneData, ref startIdx);
                foreach (var obj in rootObjects)
                {
                    scene.AddRootObject(obj);
                }
            }
            catch
            {
                // 解析失败，创建空场景
            }
        }

        /// <summary>
        /// 简化JSON解析：解析GameObject数组。
        /// </summary>
        private List<GameObjectReal> ParseGameObjectArray(string json, ref int pos)
        {
            var result = new List<GameObjectReal>();
            pos = SkipWhitespace(json, pos);

            if (pos >= json.Length || json[pos] != '[')
                return result;

            pos++; // skip '['
            pos = SkipWhitespace(json, pos);

            while (pos < json.Length && json[pos] != ']')
            {
                if (json[pos] == '{')
                {
                    var obj = ParseSingleGameObject(json, ref pos);
                    if (obj != null)
                        result.Add(obj);
                }
                else
                {
                    pos++;
                }
                pos = SkipWhitespace(json, pos);

                if (pos < json.Length && json[pos] == ',')
                {
                    pos++;
                    pos = SkipWhitespace(json, pos);
                }
            }

            if (pos < json.Length && json[pos] == ']')
                pos++;

            return result;
        }

        /// <summary>
        /// 解析单个GameObject的JSON。
        /// </summary>
        private GameObjectReal ParseSingleGameObject(string json, ref int pos)
        {
            pos = SkipWhitespace(json, pos);
            if (pos >= json.Length || json[pos] != '{')
                return null;

            int braceStart = pos;
            int depth = 0;
            // 找到匹配的闭合括号
            for (int i = pos; i < json.Length; i++)
            {
                if (json[i] == '{') depth++;
                else if (json[i] == '}') { depth--; if (depth == 0) { pos = i + 1; break; } }
            }

            string objJson = json.Substring(braceStart, pos - braceStart);

            // 解析name字段
            var obj = new GameObjectReal(ExtractJsonStringField(objJson, "name") ?? "GameObject");
            string tag = ExtractJsonStringField(objJson, "tag");
            if (tag != null) obj.Tag = tag;

            // 解析pos字段
            string posStr = ExtractJsonArrayField(objJson, "pos");
            if (posStr != null)
            {
                var parts = posStr.Split(',');
                if (parts.Length == 3)
                {
                    float x = float.Parse(parts[0].Trim());
                    float y = float.Parse(parts[1].Trim());
                    float z = float.Parse(parts[2].Trim());
                    obj.Transform.LocalPosition = new Vector3(x, y, z);
                }
            }

            // 递归解析children
            int childrenIdx = objJson.IndexOf("\"children\":", StringComparison.Ordinal);
            if (childrenIdx >= 0)
            {
                int childPos = childrenIdx + "\"children\":".Length;
                int childStart = childPos;
                var children = ParseGameObjectArray(objJson, ref childStart);

                foreach (var child in children)
                {
                    child.SetParent(obj);
                }
            }

            return obj;
        }

        private static int SkipWhitespace(string s, int pos)
        {
            while (pos < s.Length && char.IsWhiteSpace(s[pos]))
                pos++;
            return pos;
        }

        private static string ExtractJsonStringField(string json, string fieldName)
        {
            string key = $"\"{fieldName}\":";
            int idx = json.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0) return null;
            idx += key.Length;
            idx = SkipWhitespace(json, idx);
            if (idx >= json.Length || json[idx] != '"') return null;
            int start = idx + 1;
            int end = json.IndexOf('"', start);
            if (end < 0) return null;
            return json.Substring(start, end - start);
        }

        private static string ExtractJsonArrayField(string json, string fieldName)
        {
            string key = $"\"{fieldName}\":";
            int idx = json.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0) return null;
            idx += key.Length;
            idx = SkipWhitespace(json, idx);
            if (idx >= json.Length || json[idx] != '[') return null;
            int start = idx + 1;
            int end = json.IndexOf(']', start);
            if (end < 0) return null;
            return json.Substring(start, end - start);
        }

        /// <summary>
        /// 初始化场景中所有GameObject（Awake → Start）。
        /// </summary>
        private void InitializeSceneGameObjects(SceneReal scene)
        {
            // 广度优先初始化
            var queue = new Queue<GameObjectReal>();
            foreach (var root in scene.RootGameObjects)
                queue.Enqueue(root);

            while (queue.Count > 0)
            {
                var obj = queue.Dequeue();
                obj.Initialize();

                foreach (var child in obj.Children)
                    queue.Enqueue(child);
            }

            // 第二遍：Start
            queue.Clear();
            foreach (var root in scene.RootGameObjects)
                queue.Enqueue(root);

            while (queue.Count > 0)
            {
                var obj = queue.Dequeue();
                obj.Start();

                foreach (var child in obj.Children)
                    queue.Enqueue(child);
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // SceneStreamingReal: 流式加载
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>流式加载策略。</summary>
    public enum StreamingStrategy
    {
        /// <summary>同步加载（阻塞线程）。</summary>
        Synchronous,
        /// <summary>异步加载（后台线程）。</summary>
        Asynchronous,
        /// <summary>优先级队列加载。</summary>
        PriorityQueue
    }

    /// <summary>流式单元格状态。</summary>
    public enum CellState
    {
        Unloaded,
        Loading,
        Loaded,
        Unloading
    }

    /// <summary>
    /// 世界分区单元格：网格分区的基本单元。
    /// </summary>
    public sealed class StreamingCell
    {
        /// <summary>网格坐标。</summary>
        public Vector3Int GridPosition { get; }

        /// <summary>单元格世界空间边界。</summary>
        public BoundingBox Bounds { get; }

        /// <summary>单元格状态。</summary>
        public CellState State { get; internal set; } = CellState.Unloaded;

        /// <summary>加载优先级（数值越小越优先）。</summary>
        public float LoadPriority { get; set; } = float.MaxValue;

        /// <summary>关联的场景。</summary>
        public SceneReal Scene { get; internal set; }

        /// <summary>距离观察者的距离。</summary>
        public float DistanceToViewer { get; set; } = float.MaxValue;

        /// <summary>单元格中的根对象数量。</summary>
        public int ObjectCount => Scene?.RootGameObjects.Count ?? 0;

        public StreamingCell(Vector3Int gridPos, Vector3 worldOrigin, float cellSize)
        {
            GridPosition = gridPos;
            Vector3 min = worldOrigin + new Vector3(gridPos.X * cellSize, gridPos.Y * cellSize, gridPos.Z * cellSize);
            Vector3 max = min + new Vector3(cellSize, cellSize, cellSize);
            Bounds = new BoundingBox(min, max);
        }
    }

    /// <summary>
    /// 简化的轴对齐包围盒。
    /// </summary>

    /// <summary>三维整数坐标。</summary>
    public struct Vector3Int : IEquatable<Vector3Int>
    {
        public int X, Y, Z;
        public Vector3Int(int x, int y, int z) { X = x; Y = y; Z = z; }
        public bool Equals(Vector3Int other) => X == other.X && Y == other.Y && Z == other.Z;
        public override bool Equals(object obj) => obj is Vector3Int v && Equals(v);
        public override int GetHashCode() => HashCode.Combine(X, Y, Z);
        public static bool operator ==(Vector3Int a, Vector3Int b) => a.Equals(b);
        public static bool operator !=(Vector3Int a, Vector3Int b) => !a.Equals(b);
        public override string ToString() => $"({X},{Y},{Z})";
    }

    /// <summary>
    /// 世界分区：将大世界划分为网格单元格，按需加载/卸载。
    /// 类似UE的WorldStreamingGrid / Unity的Addressables。
    /// </summary>
    public sealed class SceneStreamingReal
    {
        /// <summary>网格单元格大小。</summary>
        public float CellSize { get; }

        /// <summary>加载半径（超过此距离的单元格开始加载）。</summary>
        public float LoadRadius { get; set; } = 200f;

        /// <summary>卸载半径（超过此距离的单元格被卸载）。</summary>
        public float UnloadRadius { get; set; } = 300f;

        /// <summary>预加载半径（提前加载的区域）。</summary>
        public float PreloadRadius { get; set; } = 250f;

        /// <summary>流式加载策略。</summary>
        public StreamingStrategy Strategy { get; set; } = StreamingStrategy.PriorityQueue;

        /// <summary>单元格缓存。</summary>
        public IReadOnlyDictionary<Vector3Int, StreamingCell> Cells => _cells;

        /// <summary>已加载单元格数量。</summary>
        public int LoadedCellCount { get; private set; }

        /// <summary>观察者位置（通常是玩家位置）。</summary>
        public Vector3 ViewerPosition { get; set; }

        private readonly Dictionary<Vector3Int, StreamingCell> _cells = new();
        private readonly SceneManagerReal _sceneManager;
        private readonly Vector3 _worldOrigin;
        private readonly object _lock = new();

        // 异步加载队列（最小堆按优先级排序）
        private readonly List<(float priority, Vector3Int pos)> _loadQueue = new();
        private readonly HashSet<Vector3Int> _pendingLoads = new();

        public event Action<StreamingCell>? CellLoaded;
        public event Action<StreamingCell>? CellUnloaded;

        public SceneStreamingReal(SceneManagerReal sceneManager, float cellSize = 100f, Vector3? worldOrigin = null)
        {
            _sceneManager = sceneManager ?? throw new ArgumentNullException(nameof(sceneManager));
            CellSize = cellSize;
            _worldOrigin = worldOrigin ?? Vector3.Zero;
        }

        /// <summary>
        /// 按网格坐标加载分区。
        /// </summary>
        public StreamingCell LoadCell(int x, int y, int z)
        {
            var gridPos = new Vector3Int(x, y, z);
            lock (_lock)
            {
                if (_cells.TryGetValue(gridPos, out var existing))
                {
                    if (existing.State == CellState.Loaded || existing.State == CellState.Loading)
                        return existing;
                }

                var cell = new StreamingCell(gridPos, _worldOrigin, CellSize);
                cell.State = CellState.Loading;
                _cells[gridPos] = cell;

                if (Strategy == StreamingStrategy.Synchronous)
                {
                    PerformCellLoad(cell);
                }
                else
                {
                    EnqueueCellLoad(cell);
                }

                return cell;
            }
        }

        /// <summary>
        /// 卸载分区。
        /// </summary>
        public bool UnloadCell(int x, int y, int z)
        {
            var gridPos = new Vector3Int(x, y, z);
            lock (_lock)
            {
                if (!_cells.TryGetValue(gridPos, out var cell))
                    return false;

                if (cell.State != CellState.Loaded)
                    return false;

                cell.State = CellState.Unloading;
                PerformCellUnload(cell);
                return true;
            }
        }

        /// <summary>
        /// 更新流式加载：根据观察者位置加载/卸载单元格。
        /// 应每帧调用。
        /// </summary>
        public void UpdateStreaming()
        {
            if (Strategy != StreamingStrategy.PriorityQueue)
                return;

            lock (_lock)
            {
                // 1. 计算观察者所在的网格坐标
                int viewerGridX = (int)MathF.Floor((ViewerPosition.X - _worldOrigin.X) / CellSize);
                int viewerGridY = (int)MathF.Floor((ViewerPosition.Y - _worldOrigin.Y) / CellSize);
                int viewerGridZ = (int)MathF.Floor((ViewerPosition.Z - _worldOrigin.Z) / CellSize);

                // 2. 计算需要在LoadRadius范围内的网格坐标范围
                int loadCellsRadius = (int)MathF.Ceiling(LoadRadius / CellSize);
                int unloadCellsRadius = (int)MathF.Ceiling(UnloadRadius / CellSize);

                // 3. 遍历范围内所有网格位置
                for (int dx = -loadCellsRadius; dx <= loadCellsRadius; dx++)
                {
                    for (int dy = -loadCellsRadius; dy <= loadCellsRadius; dy++)
                    {
                        for (int dz = -loadCellsRadius; dz <= loadCellsRadius; dz++)
                        {
                            var gridPos = new Vector3Int(viewerGridX + dx, viewerGridY + dy, viewerGridZ + dz);
                            float dist = Vector3.Distance(
                                ViewerPosition,
                                _worldOrigin + new Vector3(gridPos.X * CellSize + CellSize * 0.5f,
                                                            gridPos.Y * CellSize + CellSize * 0.5f,
                                                            gridPos.Z * CellSize + CellSize * 0.5f));

                            if (dist <= LoadRadius)
                            {
                                // 在加载半径内，需要加载
                                if (!_cells.ContainsKey(gridPos))
                                {
                                    LoadCell(gridPos.X, gridPos.Y, gridPos.Z);
                                }
                            }
                        }
                    }
                }

                // 4. 卸载超出UnloadRadius的单元格
                var toUnload = new List<Vector3Int>();
                foreach (var kvp in _cells)
                {
                    if (kvp.Value.State != CellState.Loaded)
                        continue;

                    float dist = kvp.Value.Bounds.DistanceTo(ViewerPosition);
                    kvp.Value.DistanceToViewer = dist;
                    kvp.Value.LoadPriority = dist;

                    if (dist > UnloadRadius)
                    {
                        toUnload.Add(kvp.Key);
                    }
                }

                foreach (var pos in toUnload)
                {
                    UnloadCell(pos.X, pos.Y, pos.Z);
                }

                // 5. 处理加载队列（按优先级处理一批）
                ProcessLoadQueue(4); // 每帧最多处理4个
            }
        }

        /// <summary>
        /// 检查指定世界坐标所在的单元格是否已加载。
        /// </summary>
        public bool IsCellLoaded(Vector3 worldPosition)
        {
            int x = (int)MathF.Floor((worldPosition.X - _worldOrigin.X) / CellSize);
            int y = (int)MathF.Floor((worldPosition.Y - _worldOrigin.Y) / CellSize);
            int z = (int)MathF.Floor((worldPosition.Z - _worldOrigin.Z) / CellSize);
            var gridPos = new Vector3Int(x, y, z);
            lock (_lock)
            {
                return _cells.TryGetValue(gridPos, out var cell) && cell.State == CellState.Loaded;
            }
        }

        /// <summary>执行单元格加载。</summary>
        private void PerformCellLoad(StreamingCell cell)
        {
            string cellName = $"Cell_{cell.GridPosition.X}_{cell.GridPosition.Y}_{cell.GridPosition.Z}";
            cell.Scene = new SceneReal(cellName, $"StreamingCells/{cellName}.shanyuscene");
            cell.Scene.BuildIndex = cell.GridPosition.GetHashCode();
            cell.Scene.State = SceneLifecycleState.Active;

            // 在实际实现中，这里会从磁盘加载单元格数据
            // 创建默认的根对象
            var rootObj = new GameObjectReal($"{cellName}_Root");
            rootObj.Transform.LocalPosition = cell.Bounds.Center;
            cell.Scene.AddRootObject(rootObj);

            cell.State = CellState.Loaded;
            LoadedCellCount++;
            CellLoaded?.Invoke(cell);
        }

        /// <summary>执行单元格卸载。</summary>
        private void PerformCellUnload(StreamingCell cell)
        {
            if (cell.Scene != null)
            {
                var allObjects = cell.Scene.AllGameObjects;
                for (int i = allObjects.Count - 1; i >= 0; i--)
                    allObjects[i].Destroy();
                cell.Scene._rootObjects.Clear();
            }

            cell.State = CellState.Unloaded;
            cell.Scene = null;
            LoadedCellCount--;
            _cells.Remove(cell.GridPosition);
            CellUnloaded?.Invoke(cell);
        }

        /// <summary>将单元格加入优先级加载队列。</summary>
        private void EnqueueCellLoad(StreamingCell cell)
        {
            float priority = cell.Bounds.DistanceTo(ViewerPosition);
            cell.LoadPriority = priority;

            if (!_pendingLoads.Contains(cell.GridPosition))
            {
                _loadQueue.Add((priority, cell.GridPosition));
                _pendingLoads.Add(cell.GridPosition);
            }
        }

        /// <summary>从优先级队列中处理N个加载请求。</summary>
        private void ProcessLoadQueue(int maxCount)
        {
            if (_loadQueue.Count == 0) return;

            // 按优先级排序（距离近的优先）
            _loadQueue.Sort((a, b) => a.priority.CompareTo(b.priority));

            int processed = 0;
            while (_loadQueue.Count > 0 && processed < maxCount)
            {
                var (priority, pos) = _loadQueue[0];
                _loadQueue.RemoveAt(0);
                _pendingLoads.Remove(pos);

                if (_cells.TryGetValue(pos, out var cell) && cell.State == CellState.Loading)
                {
                    PerformCellLoad(cell);
                    processed++;
                }
            }
        }

        /// <summary>卸载所有已加载的单元格。</summary>
        public void UnloadAll()
        {
            lock (_lock)
            {
                var allCells = _cells.Values.ToList();
                foreach (var cell in allCells)
                {
                    if (cell.State == CellState.Loaded)
                        PerformCellUnload(cell);
                }
                _loadQueue.Clear();
                _pendingLoads.Clear();
                LoadedCellCount = 0;
            }
        }
    }
}
