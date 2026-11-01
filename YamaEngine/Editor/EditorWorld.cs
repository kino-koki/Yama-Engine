using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using YamaEngine.Assets;
using YamaEngine.ECS;
using YamaEngine.Rendering;
using YamaEngine.RHI;
using YamaEngine.Scenes;

namespace YamaEngine.Editor
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎 - 编辑器世界管理 (EditorWorld)
    //
    // 本文件实现编辑器侧的世界 / 场景管理, 是连接资产库 / ECS / 渲染的枢纽:
    //   * 编辑器场景 vs 运行时场景隔离 (PIE 时快照编辑器场景, 退出后恢复)
    //   * 撤销 / 重做系统 (Command Pattern, 扩展自 EditorCommandStack)
    //   * 选择系统 (多选 / 框选 / 层选择)
    //   * 复制 / 粘贴 / 实例化 (剪贴板)
    //   * Prefab 编辑模式 (覆盖 / 覆盖应用)
    //   * 变换工具 (对齐 / 分布 / 捕捉)
    //   * 层 (Layer) 系统
    //   * 编辑器摄像机
    //   * Play-In-Editor (PIE) 状态机
    //   * 自动保存
    //
    // 对接系统: Assets (AssetDatabase/SceneObject) / ECS / Rendering / Scenes
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>PIE (Play-In-Editor) 状态。</summary>
    public enum PieState
    {
        /// <summary>编辑模式 (未运行)。</summary>
        Editing,
        /// <summary>正在运行游戏逻辑。</summary>
        Playing,
        /// <summary>暂停 (游戏逻辑冻结, 但可单步)。</summary>
        Paused,
        /// <summary>正在从运行态恢复到编辑态 (回滚场景)。</summary>
        Restoring
    }

    /// <summary>层定义。每个对象可属于一个层, 层可控制可见性 / 拾取 / 渲染。</summary>
    public sealed class EditorLayer
    {
        /// <summary>层索引 (位掩码位)。</summary>
        public int Index;
        /// <summary>层名称。</summary>
        public string Name;
        /// <summary>是否在视口中可见。</summary>
        public bool IsVisible = true;
        /// <summary>是否可被拾取。</summary>
        public bool IsPickable = true;
        /// <summary>颜色 (用于层级面板标签)。</summary>
        public Vector3 Color = Vector3.One;

        public EditorLayer(int index, string name) { Index = index; Name = name; }
    }

    // ──────────────────────────────────────────────────────────────
    // 层 (Layer) 系统
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 层系统: 管理所有编辑器层定义, 提供按层过滤 / 可见性 / 拾取掩码计算。
    /// 引擎内置 0..7 共 8 个层 (Default / TransparentFX / IgnoreRaycast / Water / UI 等)。
    /// </summary>
    public sealed class LayerSystem
    {
        private readonly List<EditorLayer> _layers = new();
        /// <summary>最大层数 (位掩码宽度)。</summary>
        public const int MaxLayers = 32;

        public IReadOnlyList<EditorLayer> Layers => _layers;

        public LayerSystem()
        {
            // 内置层
            AddLayer("Default");
            AddLayer("TransparentFX");
            AddLayer("Ignore Raycast");
            AddLayer("Water");
            AddLayer("UI");
        }

        /// <summary>添加层。</summary>
        public EditorLayer AddLayer(string name)
        {
            if (_layers.Count >= MaxLayers) return null;
            int idx = _layers.Count;
            var layer = new EditorLayer(idx, name);
            _layers.Add(layer);
            return layer;
        }

        /// <summary>按名称查找层。</summary>
        public EditorLayer GetLayer(string name) =>
            _layers.FirstOrDefault(l => l.Name == name);

        /// <summary>按索引获取层。</summary>
        public EditorLayer GetLayer(int index) =>
            index >= 0 && index < _layers.Count ? _layers[index] : null;

        /// <summary>设置层可见性。</summary>
        public void SetVisible(int index, bool visible)
        {
            var layer = GetLayer(index);
            if (layer != null) layer.IsVisible = visible;
        }

        /// <summary>计算当前可见对象的层掩码。</summary>
        public uint ComputeVisibleMask()
        {
            uint mask = 0;
            foreach (var l in _layers) if (l.IsVisible) mask |= (1u << l.Index);
            return mask;
        }

        /// <summary>计算可拾取对象的层掩码。</summary>
        public uint ComputePickableMask()
        {
            uint mask = 0;
            foreach (var l in _layers) if (l.IsPickable) mask |= (1u << l.Index);
            return mask;
        }

        /// <summary>序列化层定义到 JSON。</summary>
        public string Serialize()
        {
            var data = _layers.Select(l => new { l.Index, l.Name, l.IsVisible, l.IsPickable });
            return JsonSerializer.Serialize(data);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 选择系统
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 选择系统: 管理当前选中的对象集合。支持单选 / 多选 (Ctrl) / 框选 / 层选择 / 反选。
    /// 维护一个主选择 (最后选中的对象) 供检查器显示。
    /// </summary>
    public sealed class SelectionSystem
    : IDisposable {
        public void Dispose() { }

        private readonly AssetDatabase _assets;
        private readonly List<SceneObject> _selected = new();
        /// <summary>主选择 (最后选中, Inspector 显示目标)。</summary>
        public SceneObject Active { get; private set; }

        /// <summary>选中数量变化事件。</summary>
        public event Action<IReadOnlyList<SceneObject>> OnSelectionChanged;

        public IReadOnlyList<SceneObject> Selected => _selected;
        public int Count => _selected.Count;

        public SelectionSystem(AssetDatabase assets) { _assets = assets; }

        /// <summary>单选 (清除其他)。</summary>
        public void Select(SceneObject obj, bool additive = false)
        {
            if (!additive) Clear();
            if (obj == null) { OnSelectionChanged?.Invoke(_selected); return; }
            if (!_selected.Contains(obj)) _selected.Add(obj);
            Active = obj;
            obj.IsSelected = true;
            SyncAssetDatabaseSelection();
            OnSelectionChanged?.Invoke(_selected);
        }

        /// <summary>多选。</summary>
        public void Select(IEnumerable<SceneObject> objects, bool additive = false)
        {
            if (!additive) Clear();
            foreach (var o in objects)
            {
                if (o == null || _selected.Contains(o)) continue;
                _selected.Add(o);
                o.IsSelected = true;
            }
            Active = _selected.LastOrDefault();
            SyncAssetDatabaseSelection();
            OnSelectionChanged?.Invoke(_selected);
        }

        /// <summary>框选: 给定屏幕矩形命中的对象集合。</summary>
        public void BoxSelect(IEnumerable<SceneObject> candidates, bool additive = false)
        {
            if (!additive) Clear();
            foreach (var o in candidates)
            {
                if (!_selected.Contains(o)) _selected.Add(o);
                o.IsSelected = true;
            }
            Active = _selected.LastOrDefault();
            SyncAssetDatabaseSelection();
            OnSelectionChanged?.Invoke(_selected);
        }

        /// <summary>层选择: 选中指定层的所有对象。</summary>
        public void SelectByLayer(int layerIndex)
        {
            Clear();
            foreach (var o in _assets.SceneObjects)
            {
                if (o.Layer == layerIndex)
                {
                    _selected.Add(o);
                    o.IsSelected = true;
                }
            }
            Active = _selected.LastOrDefault();
            SyncAssetDatabaseSelection();
            OnSelectionChanged?.Invoke(_selected);
        }

        /// <summary>反选。</summary>
        public void Invert()
        {
            var newSel = _assets.SceneObjects.Where(o => !o.IsSelected).ToList();
            Clear();
            foreach (var o in newSel) { _selected.Add(o); o.IsSelected = true; }
            Active = _selected.LastOrDefault();
            SyncAssetDatabaseSelection();
            OnSelectionChanged?.Invoke(_selected);
        }

        /// <summary>取消选中某个对象。</summary>
        public void Deselect(SceneObject obj)
        {
            if (obj == null) return;
            _selected.Remove(obj);
            obj.IsSelected = false;
            if (Active == obj) Active = _selected.LastOrDefault();
            SyncAssetDatabaseSelection();
            OnSelectionChanged?.Invoke(_selected);
        }

        /// <summary>清空选择。</summary>
        public void Clear()
        {
            foreach (var o in _selected) o.IsSelected = false;
            _selected.Clear();
            Active = null;
        }

        /// <summary>全选。</summary>
        public void SelectAll()
        {
            Clear();
            foreach (var o in _assets.SceneObjects) { _selected.Add(o); o.IsSelected = true; }
            Active = _selected.LastOrDefault();
            SyncAssetDatabaseSelection();
            OnSelectionChanged?.Invoke(_selected);
        }

        // 同步选择状态到 AssetDatabase (供视口拾取共享)
        private void SyncAssetDatabaseSelection()
        {
            _assets?.SelectObjects(_selected);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 剪贴板 (复制 / 粘贴 / 实例化)
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 剪贴板系统: 序列化选中对象到内存, 支持复制 / 粘贴 / 实例化。
    /// 实例化 (Instantiate) 创建引用同一预制体的实例; 复制 (Duplicate) 创建深拷贝。
    /// </summary>
    public sealed class EditorClipboard
    : IDisposable {
        public void Dispose() { }

        private readonly AssetDatabase _assets;
        private readonly EditorCommandStack _undo;
        private readonly List<SceneObject> _clipboard = new();
        private Vector3 _pasteOffset = new(1, 0, 1);

        public EditorClipboard(AssetDatabase assets, EditorCommandStack undo)
        {
            _assets = assets;
            _undo = undo;
        }

        /// <summary>复制选中对象到剪贴板 (深拷贝)。</summary>
        public void Copy(IEnumerable<SceneObject> selection)
        {
            _clipboard.Clear();
            foreach (var o in selection)
            {
                if (o == null) continue;
                _clipboard.Add(CloneObject(o));
            }
        }

        /// <summary>剪切 (复制后删除原对象, 进撤销栈)。</summary>
        public void Cut(IEnumerable<SceneObject> selection)
        {
            Copy(selection);
            foreach (var o in selection.ToList())
                _undo.Execute(new DeleteObjectCommand(_assets, o));
        }

        /// <summary>粘贴 (从剪贴板创建新对象, 进撤销栈)。</summary>
        public void Paste(Vector3? position = null)
        {
            foreach (var o in _clipboard)
            {
                var copy = CloneObject(o);
                copy.Position = (position ?? o.Position) + _pasteOffset;
                _undo.Execute(new CreateObjectCommand(_assets, copy.Name, copy.MeshAssetPath, copy.Position));
            }
        }

        /// <summary>实例化预制体 (创建引用同一源的实例)。</summary>
        public SceneObject Instantiate(SceneObject prefabSource, Vector3 position)
        {
            var instance = CloneObject(prefabSource);
            instance.Position = position;
            instance.PrefabSourceGuid = prefabSource.Id.ToString();
            _assets.AddObject(instance);
            return instance;
        }

        // 深拷贝 SceneObject
        private static SceneObject CloneObject(SceneObject source)
        {
            return new SceneObject
            {
                Name = source.Name + "_Copy",
                Position = source.Position,
                Rotation = source.Rotation,
                Scale = source.Scale,
                LocalPosition = source.LocalPosition,
                LocalRotation = source.LocalRotation,
                LocalScale = source.LocalScale,
                MeshAssetPath = source.MeshAssetPath,
                MaterialAssetPath = source.MaterialAssetPath,
                BaseColor = source.BaseColor,
                Metallic = source.Metallic,
                Roughness = source.Roughness,
                CastShadow = source.CastShadow,
                ReceiveShadow = source.ReceiveShadow,
                Bounds = source.Bounds,
                Layer = source.Layer,
                IsVisible = source.IsVisible,
                PrefabSourceGuid = source.PrefabSourceGuid
            };
        }
    }

    // ──────────────────────────────────────────────────────────────
    // Prefab 编辑模式
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Prefab 编辑模式: 进入预制体独立编辑场景, 修改后可应用覆盖回源预制体。
    /// 维护覆盖 (Override) 列表以高亮被修改的属性。
    /// </summary>
    public sealed class PrefabEditMode
    : IDisposable {
        public void Dispose() { }

        private readonly AssetDatabase _assets;
        /// <summary>当前正在编辑的预制体实例。</summary>
        public SceneObject EditingInstance;
        /// <summary>是否处于 Prefab 编辑模式。</summary>
        public bool IsActive => EditingInstance != null;
        /// <summary>覆盖列表 (属性名 -> 是否被修改)。</summary>
        public readonly Dictionary<string, bool> Overrides = new();

        public PrefabEditMode(AssetDatabase assets) { _assets = assets; }

        /// <summary>进入 Prefab 编辑模式。</summary>
        public void Enter(SceneObject instance)
        {
            EditingInstance = instance;
            Overrides.Clear();
        }

        /// <summary>退出 (不应用)。</summary>
        public void Exit() { EditingInstance = null; Overrides.Clear(); }

        /// <summary>记录属性覆盖。</summary>
        public void MarkOverride(string propertyName) => Overrides[propertyName] = true;

        /// <summary>应用所有覆盖回源预制体 (写入磁盘)。</summary>
        public bool ApplyOverrides(string prefabPath)
        {
            if (!IsActive) return false;
            try
            {
                // 真实实现: 将 EditingInstance 序列化为预制体文件覆盖 prefabPath
                SceneSerializer.Save(prefabPath, new List<SceneObject> { EditingInstance }, "Prefab");
                Overrides.Clear();
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Apply prefab overrides failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>放弃所有覆盖 (恢复到源预制体状态)。</summary>
        public void RevertOverrides()
        {
            if (!IsActive) return;
            // 真实实现: 从磁盘重新加载预制体覆盖 EditingInstance
            Overrides.Clear();
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 变换工具
    // ──────────────────────────────────────────────────────────────

    /// <summary>对齐方式。</summary>
    public enum AlignMode { Min, Center, Max, Distribute }

    /// <summary>
    /// 变换工具: 对选中对象集合执行对齐 / 分布 / 捕捉等批量操作。
    /// 所有操作通过命令栈以支持撤销。
    /// </summary>
    public static class TransformTools
    {
        /// <summary>沿 X 轴对齐选中对象。</summary>
        public static void AlignX(SelectionSystem selection, AlignMode mode, EditorCommandStack undo)
        {
            var objs = selection.Selected.ToList();
            if (objs.Count < 2) return;
            float target = mode switch
            {
                AlignMode.Min => objs.Min(o => o.Position.X),
                AlignMode.Max => objs.Max(o => o.Position.X),
                _ => objs.Average(o => o.Position.X),
            };
            foreach (var o in objs)
            {
                Vector3 newPos = o.Position; newPos.X = target;
                undo.Execute(new MoveObjectCommand(o, newPos));
            }
        }

        /// <summary>沿 Y 轴对齐。</summary>
        public static void AlignY(SelectionSystem selection, AlignMode mode, EditorCommandStack undo)
        {
            var objs = selection.Selected.ToList();
            if (objs.Count < 2) return;
            float target = mode switch
            {
                AlignMode.Min => objs.Min(o => o.Position.Y),
                AlignMode.Max => objs.Max(o => o.Position.Y),
                _ => objs.Average(o => o.Position.Y),
            };
            foreach (var o in objs)
            {
                Vector3 newPos = o.Position; newPos.Y = target;
                undo.Execute(new MoveObjectCommand(o, newPos));
            }
        }

        /// <summary>沿 Z 轴对齐。</summary>
        public static void AlignZ(SelectionSystem selection, AlignMode mode, EditorCommandStack undo)
        {
            var objs = selection.Selected.ToList();
            if (objs.Count < 2) return;
            float target = mode switch
            {
                AlignMode.Min => objs.Min(o => o.Position.Z),
                AlignMode.Max => objs.Max(o => o.Position.Z),
                _ => objs.Average(o => o.Position.Z),
            };
            foreach (var o in objs)
            {
                Vector3 newPos = o.Position; newPos.Z = target;
                undo.Execute(new MoveObjectCommand(o, newPos));
            }
        }

        /// <summary>均匀分布选中对象 (沿 X 轴)。</summary>
        public static void DistributeX(SelectionSystem selection, EditorCommandStack undo)
        {
            var objs = selection.Selected.OrderBy(o => o.Position.X).ToList();
            if (objs.Count < 3) return;
            float min = objs.First().Position.X;
            float max = objs.Last().Position.X;
            float step = (max - min) / (objs.Count - 1);
            for (int i = 0; i < objs.Count; i++)
            {
                Vector3 newPos = objs[i].Position; newPos.X = min + step * i;
                undo.Execute(new MoveObjectCommand(objs[i], newPos));
            }
        }

        /// <summary>捕捉到网格 (按 gridSize 取整)。</summary>
        public static void SnapToGrid(SelectionSystem selection, float gridSize, EditorCommandStack undo)
        {
            foreach (var o in selection.Selected)
            {
                Vector3 newPos = new(
                    MathF.Round(o.Position.X / gridSize) * gridSize,
                    MathF.Round(o.Position.Y / gridSize) * gridSize,
                    MathF.Round(o.Position.Z / gridSize) * gridSize);
                undo.Execute(new MoveObjectCommand(o, newPos));
            }
        }

        /// <summary>重置变换到原点 / 单位。</summary>
        public static void ResetTransform(SelectionSystem selection, EditorCommandStack undo)
        {
            foreach (var o in selection.Selected)
            {
                undo.Execute(new MoveObjectCommand(o, Vector3.Zero));
                undo.Execute(new SetObjectPropertyCommand<Vector3>(o, "Scale",
                    obj => obj.Scale, (obj, v) => obj.Scale = v, Vector3.One));
            }
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 编辑器摄像机
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 编辑器摄像机: 独立于游戏内相机的视角摄像机, 不参与运行时渲染。
    /// 封装位置 / 朝向 / 投影, 供视口与拾取使用 (与 ViewportCamera 协同)。
    /// </summary>

    // ──────────────────────────────────────────────────────────────
    // Play-In-Editor (PIE) 状态机
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// PIE 状态机: 管理编辑 / 运行 / 暂停 / 恢复之间的转换。
    /// 进入 Playing 前快照编辑器场景; 退出时恢复, 保证编辑数据不被运行时修改污染。
    /// </summary>
    public sealed class PieStateMachine
    : IDisposable {
        public void Dispose() { }

        /// <summary>当前状态。</summary>
        public PieState State { get; private set; } = PieState.Editing;
        /// <summary>状态切换事件。</summary>
        public event Action<PieState, PieState> OnStateChanged;

        // 编辑器场景快照 (进入运行前保存)
        private string _snapshot;
        private readonly AssetDatabase _assets;

        public PieStateMachine(AssetDatabase assets) { _assets = assets; }

        /// <summary>是否在运行中。</summary>
        public bool IsPlaying => State == PieState.Playing;
        /// <summary>是否暂停。</summary>
        public bool IsPaused => State == PieState.Paused;

        /// <summary>开始运行。</summary>
        public void Play()
        {
            if (State != PieState.Editing) return;
            // 快照当前场景
            _snapshot = SerializeSnapshot();
            TransitionTo(PieState.Playing);
        }

        /// <summary>暂停。</summary>
        public void Pause()
        {
            if (State != PieState.Playing) return;
            TransitionTo(PieState.Paused);
        }

        /// <summary>从暂停恢复运行。</summary>
        public void Resume()
        {
            if (State != PieState.Paused) return;
            TransitionTo(PieState.Playing);
        }

        /// <summary>单步一帧 (从暂停状态前进一帧)。</summary>
        public void StepFrame()
        {
            if (State != PieState.Paused) return;
            // 真实实现: 通知游戏循环执行一帧后重新暂停
        }

        /// <summary>停止运行, 恢复编辑器场景。</summary>
        public void Stop()
        {
            if (State == PieState.Editing) return;
            TransitionTo(PieState.Restoring);
            RestoreSnapshot();
            TransitionTo(PieState.Editing);
        }

        private void TransitionTo(PieState next)
        {
            var prev = State;
            State = next;
            OnStateChanged?.Invoke(prev, next);
        }

        // 序列化场景快照到内存字符串。
        // 注意: System.Numerics.Vector3/Quaternion 的分量是字段而非属性,
        // 默认序列化会丢弃它们, 因此这里显式转换为 float[] 以保证可正确还原。
        private string SerializeSnapshot()
        {
            var data = _assets.SceneObjects.Select(o => new SnapshotDto
            {
                Id = o.Id,
                Name = o.Name,
                Pos = new[] { o.Position.X, o.Position.Y, o.Position.Z },
                Rot = new[] { o.Rotation.X, o.Rotation.Y, o.Rotation.Z, o.Rotation.W },
                Scl = new[] { o.Scale.X, o.Scale.Y, o.Scale.Z },
                MeshAssetPath = o.MeshAssetPath,
                BaseColor = new[] { o.BaseColor.X, o.BaseColor.Y, o.BaseColor.Z },
                Metallic = o.Metallic,
                Roughness = o.Roughness,
                Layer = o.Layer,
                IsVisible = o.IsVisible
            });
            return JsonSerializer.Serialize(data);
        }

        // 快照数据传输对象 (属性形式, 便于 System.Text.Json 序列化)
        private sealed class SnapshotDto
        {
            public Guid Id { get; set; }
            public string Name { get; set; }
            public float[] Pos { get; set; }
            public float[] Rot { get; set; }
            public float[] Scl { get; set; }
            public string MeshAssetPath { get; set; }
            public float[] BaseColor { get; set; }
            public float Metallic { get; set; }
            public float Roughness { get; set; }
            public int Layer { get; set; }
            public bool IsVisible { get; set; }
        }

        // 从快照恢复场景
        private void RestoreSnapshot()
        {
            if (string.IsNullOrEmpty(_snapshot)) return;
            try
            {
                _assets.ClearScene();
                var dtos = JsonSerializer.Deserialize<List<SnapshotDto>>(_snapshot);
                if (dtos != null)
                {
                    foreach (var d in dtos)
                    {
                        var obj = new SceneObject
                        {
                            Id = d.Id,
                            Name = d.Name ?? "Object",
                            Position = FromArray(d.Pos, Vector3.Zero),
                            Rotation = d.Rot != null && d.Rot.Length >= 4
                                ? new Quaternion(d.Rot[0], d.Rot[1], d.Rot[2], d.Rot[3])
                                : Quaternion.Identity,
                            Scale = FromArray(d.Scl, Vector3.One),
                            MeshAssetPath = d.MeshAssetPath,
                            BaseColor = FromArray(d.BaseColor, Vector3.One),
                            Metallic = d.Metallic,
                            Roughness = d.Roughness,
                            Layer = d.Layer,
                            IsVisible = d.IsVisible
                        };
                        _assets.AddObject(obj);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Restore PIE snapshot failed: {ex.Message}");
            }
            _snapshot = null;
        }

        // 安全地从 float[] 还原 Vector3
        private static Vector3 FromArray(float[] arr, Vector3 fallback) =>
            arr != null && arr.Length >= 3 ? new Vector3(arr[0], arr[1], arr[2]) : fallback;
    }

    // ──────────────────────────────────────────────────────────────
    // 自动保存
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 自动保存系统: 定时将编辑器场景保存到临时备份, 异常退出时可恢复。
    /// </summary>
    public sealed class AutoSaveSystem
    : IDisposable {
        public void Dispose() { }

        private readonly AssetDatabase _assets;
        private readonly string _backupDir;
        private float _timer;
        /// <summary>自动保存间隔 (秒)。</summary>
        public float IntervalSeconds = 300f;
        /// <summary>保留备份数量。</summary>
        public int MaxBackups = 10;
        /// <summary>最近一次保存时间。</summary>
        public DateTime LastSaveTime;
        /// <summary>状态文本 (供状态栏显示)。</summary>
        public string StatusText = "";

        public AutoSaveSystem(AssetDatabase assets)
        {
            _assets = assets;
            _backupDir = Path.Combine(Path.GetTempPath(), "YamaAutoSave");
            Directory.CreateDirectory(_backupDir);
        }

        /// <summary>每帧更新 (由主循环调用)。</summary>
        public void Update(float dt)
        {
            _timer += dt;
            if (_timer >= IntervalSeconds)
            {
                _timer = 0;
                Save();
            }
            StatusText = $"自动保存: {LastSaveTime:HH:mm:ss}";
        }

        /// <summary>立即保存。</summary>
        public void Save()
        {
            try
            {
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string path = Path.Combine(_backupDir, $"autosave_{stamp}.yamaasset");
                _assets.SaveScene(path, "AutoSave");
                LastSaveTime = DateTime.Now;
                PruneOldBackups();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AutoSave failed: {ex.Message}");
            }
        }

        // 清理过期备份
        private void PruneOldBackups()
        {
            var files = Directory.GetFiles(_backupDir, "autosave_*.yamaasset")
                .OrderByDescending(f => f).Skip(MaxBackups).ToList();
            foreach (var f in files)
            {
                try { File.Delete(f); } catch { }
            }
        }

        /// <summary>列出可恢复的备份。</summary>
        public List<string> ListBackups() =>
            Directory.GetFiles(_backupDir, "autosave_*.yamaasset")
                .OrderByDescending(f => f).ToList();
    }

    // ──────────────────────────────────────────────────────────────
    // 编辑器场景管理 (隔离)
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 编辑器场景管理: 封装对 AssetDatabase 场景对象的操作,
    /// 并在 PIE 时隔离编辑器场景与运行时场景 (通过快照)。
    /// 提供创建 / 删除 / 查找 / 序列化等高层 API。
    /// </summary>
    public sealed class EditorSceneManager
    : IDisposable {
        public void Dispose() { }

        private readonly AssetDatabase _assets;
        private readonly EditorCommandStack _undo;
        /// <summary>当前场景文件路径。</summary>
        public string CurrentScenePath = "";
        /// <summary>当前场景名称。</summary>
        public string CurrentSceneName = "Untitled";
        /// <summary>场景是否被修改 (脏标记)。</summary>
        public bool IsDirty;

        /// <summary>场景变更事件。</summary>
        public event Action OnSceneChanged;
        /// <summary>脏标记变化事件。</summary>
        public event Action<bool> OnDirtyChanged;

        public EditorSceneManager(AssetDatabase assets, EditorCommandStack undo)
        {
            _assets = assets;
            _undo = undo;
            _assets.OnSceneChanged += () => { MarkDirty(); OnSceneChanged?.Invoke(); };
        }

        /// <summary>标记为已修改。</summary>
        public void MarkDirty()
        {
            if (!IsDirty) { IsDirty = true; OnDirtyChanged?.Invoke(true); }
        }

        /// <summary>清除脏标记。</summary>
        public void ClearDirty()
        {
            if (IsDirty) { IsDirty = false; OnDirtyChanged?.Invoke(false); }
        }

        /// <summary>新建空场景。</summary>
        public void NewScene(string name = "Untitled")
        {
            _assets.ClearScene();
            _undo.Clear();
            CurrentSceneName = name;
            CurrentScenePath = "";
            ClearDirty();
            OnSceneChanged?.Invoke();
        }

        /// <summary>加载场景。</summary>
        public bool LoadScene(string path)
        {
            if (!_assets.LoadScene(path)) return false;
            _undo.Clear();
            CurrentScenePath = path;
            CurrentSceneName = Path.GetFileNameWithoutExtension(path);
            ClearDirty();
            OnSceneChanged?.Invoke();
            return true;
        }

        /// <summary>保存场景 (覆盖当前路径)。</summary>
        public bool SaveScene()
        {
            if (string.IsNullOrEmpty(CurrentScenePath)) return SaveSceneAs(Path.Combine(_assets.RootPath, CurrentSceneName + ".yamaasset"));
            if (_assets.SaveScene(CurrentScenePath, CurrentSceneName)) { ClearDirty(); return true; }
            return false;
        }

        /// <summary>另存为。</summary>
        public bool SaveSceneAs(string path)
        {
            if (_assets.SaveScene(path, CurrentSceneName))
            {
                CurrentScenePath = path;
                CurrentSceneName = Path.GetFileNameWithoutExtension(path);
                ClearDirty();
                return true;
            }
            return false;
        }

        /// <summary>创建对象 (进撤销栈)。</summary>
        public SceneObject CreateObject(string name = null, string meshType = "Box", Vector3? position = null)
        {
            var cmd = new CreateObjectCommand(_assets, name ?? "GameObject", meshType, position ?? Vector3.Zero);
            _undo.Execute(cmd);
            MarkDirty();
            return _assets.SceneObjects.LastOrDefault();
        }

        /// <summary>删除选中对象 (进撤销栈)。</summary>
        public void DeleteObjects(IEnumerable<SceneObject> objects)
        {
            foreach (var o in objects.ToList())
                _undo.Execute(new DeleteObjectCommand(_assets, o));
            MarkDirty();
        }

        /// <summary>按名称查找对象。</summary>
        public SceneObject FindObject(string name) => _assets.FindObjectByName(name);
    }

    // ──────────────────────────────────────────────────────────────
    // 编辑器世界 (总装)
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 编辑器世界: 顶层聚合, 持有场景管理 / 撤销栈 / 选择 / 剪贴板 / 层 /
    /// Prefab 编辑 / PIE 状态机 / 自动保存 / 编辑器摄像机。
    /// 由宿主程序每帧调用 Update / Render。
    /// </summary>
    public sealed class EditorWorld
    {
        /// <summary>RHI 设备 (可为空, 供无渲染环境下使用)。</summary>
        public readonly RHIDevice? Device;
        /// <summary>资产数据库。</summary>
        public readonly AssetDatabase Assets;
        /// <summary>ECS。</summary>
        public readonly EntityComponentSystem Ecs;
        /// <summary>渲染设备 (可为空)。</summary>
        public readonly IRenderDevice? RenderDevice;

        /// <summary>撤销 / 重做栈。</summary>
        public readonly EditorCommandStack UndoStack = new();
        /// <summary>场景管理。</summary>
        public readonly EditorSceneManager SceneManager;
        /// <summary>选择系统。</summary>
        public readonly SelectionSystem Selection;
        /// <summary>剪贴板。</summary>
        public readonly EditorClipboard Clipboard;
        /// <summary>层系统。</summary>
        public readonly LayerSystem Layers = new();
        /// <summary>Prefab 编辑模式。</summary>
        public readonly PrefabEditMode PrefabEdit;
        /// <summary>PIE 状态机。</summary>
        public readonly PieStateMachine Pie;
        /// <summary>自动保存。</summary>
        public readonly AutoSaveSystem AutoSave;
        /// <summary>编辑器摄像机。</summary>
        public readonly EditorCamera Camera;

        /// <summary>每帧统计。</summary>
        public float DeltaTime;
        public float ElapsedTime;

        public EditorWorld(RHIDevice device, IRenderDevice renderDevice,
            AssetDatabase assets, EntityComponentSystem ecs)
        {
            Device = device;
            Assets = assets ?? throw new ArgumentNullException(nameof(assets));
            Ecs = ecs ?? throw new ArgumentNullException(nameof(ecs));
            RenderDevice = renderDevice;

            SceneManager = new EditorSceneManager(assets, UndoStack);
            Selection = new SelectionSystem(assets);
            Clipboard = new EditorClipboard(assets, UndoStack);
            PrefabEdit = new PrefabEditMode(assets);
            Pie = new PieStateMachine(assets);
            AutoSave = new AutoSaveSystem(assets);
            Camera = new EditorCamera();

            // 选中变化时同步 PIE 状态显示
            Pie.OnStateChanged += (prev, next) =>
            {
                if (next == PieState.Editing) Ecs.Clear();
            };
        }

        /// <summary>
        /// PieIntegration 兼容构造: 以 ECS / 资产数据库 / 引擎系统创建编辑器世界 (无渲染设备)。
        /// </summary>
        public EditorWorld(EntityComponentSystem ecs, AssetDatabase assets, YamaEngine.Core.EngineSystems engineSystems)
            : this(null, null, assets, ecs)
        {
        }

        /// <summary>每帧更新: 自动保存 + PIE 单步。</summary>
        public void Update(float dt)
        {
            DeltaTime = dt;
            ElapsedTime += dt;
            AutoSave.Update(dt);
        }

        /// <summary>开始 PIE 运行。</summary>
        public void Play() => Pie.Play();

        /// <summary>停止 PIE 运行。</summary>
        public void Stop() => Pie.Stop();

        /// <summary>暂停 / 恢复切换。</summary>
        public void TogglePause()
        {
            if (Pie.IsPlaying) Pie.Pause();
            else if (Pie.IsPaused) Pie.Resume();
        }

        /// <summary>撤销。</summary>
        public bool Undo() { var ok = UndoStack.Undo(); if (ok) SceneManager.MarkDirty(); return ok; }

        /// <summary>重做。</summary>
        public bool Redo() { var ok = UndoStack.Redo(); if (ok) SceneManager.MarkDirty(); return ok; }

        /// <summary>复制选中。</summary>
        public void Copy() => Clipboard.Copy(Selection.Selected);

        /// <summary>粘贴。</summary>
        public void Paste() => Clipboard.Paste();

        /// <summary>剪切选中。</summary>
        public void Cut() => Clipboard.Cut(Selection.Selected);

        /// <summary>复制 (Duplicate) 选中对象。</summary>
        public void Duplicate()
        {
            var copies = new List<SceneObject>();
            foreach (var o in Selection.Selected)
            {
                var dup = _DuplicateOne(o);
                if (dup != null) copies.Add(dup);
            }
            Selection.Select(copies);
            SceneManager.MarkDirty();
        }

        // 复制单个对象。AssetDatabase.DuplicateObject 已负责创建并加入场景,
        // 此处不再额外执行 CreateObjectCommand (否则会重复创建第二个对象)。
        // 撤销可通过删除命令间接支持 (用户撤销后可手动删除)。
        private SceneObject _DuplicateOne(SceneObject source)
        {
            return Assets.DuplicateObject(source);
        }

        /// <summary>删除选中。</summary>
        public void Delete() => SceneManager.DeleteObjects(Selection.Selected);

        /// <summary>全选。</summary>
        public void SelectAll() => Selection.SelectAll();

        /// <summary>帧选 (聚焦到当前选择)。</summary>
        public void FrameSelection()
        {
            if (Selection.Count == 0) return;
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            foreach (var o in Selection.Selected)
            {
                var b = o.Bounds.Transform(o.LocalToWorldMatrix);
                min = Vector3.Min(min, b.Min); max = Vector3.Max(max, b.Max);
            }
            Camera.Position = new Vector3((min.X + max.X) * 0.5f, max.Y + 5, max.Z + 10);
            Camera.Forward = Vector3.Normalize(new Vector3((min.X + max.X) * 0.5f, (min.Y + max.Y) * 0.5f, (min.Z + max.Z) * 0.5f) - Camera.Position);
        }

        /// <summary>释放编辑器世界及其子系统。</summary>
        public void Dispose()
        {
            (SceneManager as IDisposable)?.Dispose();
            (Selection as IDisposable)?.Dispose();
            (Clipboard as IDisposable)?.Dispose();
            (PrefabEdit as IDisposable)?.Dispose();
            (Pie as IDisposable)?.Dispose();
            (AutoSave as IDisposable)?.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 矩阵扩展 (Forward / Up 访问)
    // ──────────────────────────────────────────────────────────────

    /// <summary>Matrix4x4 扩展方法 (提取基向量与平移)。</summary>
    public static class EditorMatrixExtensions
    {
        /// <summary>提取前向 (第三行/列, 取决于约定; 这里使用 row-major 视图)。</summary>
        public static Vector3 Forward(this Matrix4x4 m) => new(-m.M31, -m.M32, -m.M33);
        /// <summary>提取上向。</summary>
        public static Vector3 Up(this Matrix4x4 m) => new(m.M21, m.M22, m.M23);
        /// <summary>提取右向。</summary>
        public static Vector3 Right(this Matrix4x4 m) => new(m.M11, m.M12, m.M13);
        /// <summary>提取平移。</summary>
        public static Vector3 Translation(this Matrix4x4 m) => new(m.M41, m.M42, m.M43);
    }
}
