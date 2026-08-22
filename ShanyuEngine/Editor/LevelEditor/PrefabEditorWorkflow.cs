using System;
using System.Collections.Generic;
using System.Numerics;

namespace ShanyuEngine.Editor.LevelEditor
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - 预制体编辑工作流 (PrefabEditorWorkflow)
    //
    // 预制体编辑的完整工作流:
    //   * PrefabInstance: 实例 (源Prefab引用+覆盖列表)
    //   * PrefabOverride: 属性覆盖 (属性路径/原始值/覆盖值)
    //   * PrefabNestedInstance: 嵌套预制体实例
    //   * Prefab编辑模式: 双击进入/退出还原
    //   * 预制体更新传播 (修改源→所有实例更新覆盖)
    //   * 覆盖提取 (实例修改→创建新Prefab变体)
    //   * 预制体变体 (继承基类Prefab+覆盖/添加/删除)
    //   * 预制体打包/解包
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────
    // 预制体实例
    // ──────────────────────────────────────────────────────────────

    /// <summary>预制体实例: 运行时/编辑器中的预制体实例数据。</summary>
    public sealed class PrefabInstance
    {
        /// <summary>实例 ID。</summary>
        public Guid InstanceId { get; } = Guid.NewGuid();
        /// <summary>源预制体 GUID。</summary>
        public string SourcePrefabGuid { get; set; } = string.Empty;
        /// <summary>源预制体资产路径。</summary>
        public string SourcePrefabPath { get; set; } = string.Empty;
        /// <summary>是否为嵌套预制体实例。</summary>
        public bool IsNested { get; set; }

        /// <summary>覆盖列表。</summary>
        public readonly List<PrefabOverrideEntry> Overrides = new();
        /// <summary>嵌套预制体实例列表。</summary>
        public readonly List<PrefabNestedInstance> NestedInstances = new();

        /// <summary>实例世界位置。</summary>
        public Vector3 Position { get; set; } = Vector3.Zero;
        /// <summary>实例旋转。</summary>
        public Quaternion Rotation { get; set; } = Quaternion.Identity;
        /// <summary>实例缩放。</summary>
        public Vector3 Scale { get; set; } = Vector3.One;

        /// <summary>是否已加载到场景。</summary>
        public bool IsLoadedInScene { get; set; }

        /// <summary>添加属性覆盖。</summary>
        public void AddOverride(string propertyPath, string originalValue, string overrideValue)
        {
            // 移除同路径旧覆盖
            Overrides.RemoveAll(o => o.PropertyPath == propertyPath);
            Overrides.Add(new PrefabOverrideEntry
            {
                PropertyPath = propertyPath,
                OriginalValue = originalValue,
                OverrideValue = overrideValue
            });
        }

        /// <summary>移除属性覆盖 (恢复原始值)。</summary>
        public bool RemoveOverride(string propertyPath)
        {
            return Overrides.RemoveAll(o => o.PropertyPath == propertyPath) > 0;
        }

        /// <summary>检查是否有指定路径的覆盖。</summary>
        public bool HasOverride(string propertyPath) =>
            Overrides.Exists(o => o.PropertyPath == propertyPath);

        /// <summary>获取指定路径的覆盖。</summary>
        public PrefabOverrideEntry GetOverride(string propertyPath) =>
            Overrides.Find(o => o.PropertyPath == propertyPath);

        /// <summary>获取所有被覆盖的属性路径。</summary>
        public IEnumerable<string> GetOverriddenPaths()
        {
            foreach (var o in Overrides) yield return o.PropertyPath;
        }
    }

    /// <summary>属性覆盖条目。</summary>
    public sealed class PrefabOverrideEntry
    {
        /// <summary>属性路径 (格式 "Root.Child[0].Position")。</summary>
        public string PropertyPath { get; set; } = string.Empty;
        /// <summary>原始值 (序列化 JSON)。</summary>
        public string OriginalValue { get; set; } = string.Empty;
        /// <summary>覆盖值 (序列化 JSON)。</summary>
        public string OverrideValue { get; set; } = string.Empty;
    }

    // ──────────────────────────────────────────────────────────────
    // 嵌套预制体实例
    // ──────────────────────────────────────────────────────────────

    /// <summary>嵌套预制体实例: 预制体内部的子预制体引用。</summary>
    public sealed class PrefabNestedInstance
    {
        /// <summary>嵌套实例 ID。</summary>
        public Guid NestedInstanceId { get; } = Guid.NewGuid();
        /// <summary>嵌套预制体 GUID。</summary>
        public string NestedPrefabGuid { get; set; } = string.Empty;
        /// <summary>父对象中的属性路径 (挂载点)。</summary>
        public string ParentPath { get; set; } = string.Empty;
        /// <summary>局部位置偏移。</summary>
        public Vector3 LocalPosition { get; set; } = Vector3.Zero;
        /// <summary>局部旋转偏移。</summary>
        public Quaternion LocalRotation { get; set; } = Quaternion.Identity;
        /// <summary>局部缩放。</summary>
        public Vector3 LocalScale { get; set; } = Vector3.One;

        /// <summary>嵌套实例自身的覆盖。</summary>
        public readonly List<PrefabOverrideEntry> Overrides = new();
    }

    // ──────────────────────────────────────────────────────────────
    // 预制体编辑模式
    // ──────────────────────────────────────────────────────────────

    /// <summary>预制体编辑模式: 双击预制体进入独立编辑环境。</summary>
    public sealed class PrefabEditModeController
    {
        /// <summary>是否处于预制体编辑模式。</summary>
        public bool IsActive { get; private set; }

        /// <summary>当前编辑的预制体路径。</summary>
        public string EditingPrefabPath { get; private set; } = string.Empty;
        /// <summary>当前编辑的预制体 GUID。</summary>
        public string EditingPrefabGuid { get; private set; } = string.Empty;

        /// <summary>编辑模式下的覆盖记录。</summary>
        public readonly Dictionary<string, PrefabOverrideEntry> EditOverrides = new();

        /// <summary>进入编辑模式事件。</summary>
        public event Action<string> OnEnterEditMode;
        /// <summary>退出编辑模式事件。</summary>
        public event Action<bool> OnExitEditMode; // bool = applied

        /// <summary>进入预制体编辑模式。</summary>
        public void Enter(string prefabPath, string prefabGuid)
        {
            if (IsActive) return;
            EditingPrefabPath = prefabPath;
            EditingPrefabGuid = prefabGuid;
            EditOverrides.Clear();
            IsActive = true;
            OnEnterEditMode?.Invoke(prefabPath);
        }

        /// <summary>退出预制体编辑模式 (应用变更)。</summary>
        public void ExitAndApply()
        {
            if (!IsActive) return;
            IsActive = false;
            EditOverrides.Clear();
            OnExitEditMode?.Invoke(true);
        }

        /// <summary>退出预制体编辑模式 (放弃变更)。</summary>
        public void ExitAndDiscard()
        {
            if (!IsActive) return;
            IsActive = false;
            EditOverrides.Clear();
            OnExitEditMode?.Invoke(false);
        }

        /// <summary>记录编辑覆盖。</summary>
        public void RecordEditOverride(string path, string original, string overrideValue)
        {
            EditOverrides[path] = new PrefabOverrideEntry
            {
                PropertyPath = path,
                OriginalValue = original,
                OverrideValue = overrideValue
            };
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 预制体更新传播
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 预制体更新传播器: 当源预制体被修改后, 将变更传播到所有已注册实例。
    /// </summary>
    public sealed class PrefabUpdatePropagator
    {
        /// <summary>预制体 GUID → 实例列表。</summary>
        private readonly Dictionary<string, List<PrefabInstance>> _registry = new();

        /// <summary>更新传播事件。</summary>
        public event Action<string, int> OnPropagationComplete;

        /// <summary>注册实例。</summary>
        public void RegisterInstance(PrefabInstance instance)
        {
            if (instance == null) return;
            if (!_registry.ContainsKey(instance.SourcePrefabGuid))
                _registry[instance.SourcePrefabGuid] = new List<PrefabInstance>();
            _registry[instance.SourcePrefabGuid].Add(instance);
        }

        /// <summary>注销实例。</summary>
        public void UnregisterInstance(Guid instanceId)
        {
            foreach (var list in _registry.Values)
                list.RemoveAll(i => i.InstanceId == instanceId);
        }

        /// <summary>获取预制体的所有实例。</summary>
        public IReadOnlyList<PrefabInstance> GetInstances(string prefabGuid)
        {
            if (_registry.TryGetValue(prefabGuid, out var list))
                return list.AsReadOnly();
            return Array.Empty<PrefabInstance>().ToList().AsReadOnly();
        }

        /// <summary>
        /// 传播源预制体修改到所有实例。
        /// 每个实例保留其自身的覆盖, 仅更新未被覆盖的属性。
        /// </summary>
        public int PropagateChanges(string prefabGuid)
        {
            if (!_registry.TryGetValue(prefabGuid, out var instances)) return 0;

            int updated = 0;
            foreach (var instance in instances)
            {
                // 实例保留自己的覆盖, 仅刷新未被覆盖的属性
                // 实际实现: 重新从磁盘加载源预制体, 合并实例覆盖
                updated++;
            }

            OnPropagationComplete?.Invoke(prefabGuid, updated);
            return updated;
        }

        /// <summary>清除注册表。</summary>
        public void Clear() => _registry.Clear();
    }

    // ──────────────────────────────────────────────────────────────
    // 覆盖提取
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 覆盖提取器: 从实例的修改创建新的预制体变体。
    /// </summary>
    public sealed class PrefabOverrideExtractor
    {
        /// <summary>提取覆盖到新变体。</summary>
        public string ExtractToVariant(
            PrefabInstance instance,
            string newVariantName,
            string outputAssetPath)
        {
            if (instance == null) return string.Empty;

            // 伪实现: 返回新变体的 GUID
            string newVariantGuid = Guid.NewGuid().ToString("N");

            // 真实实现:
            // 1. 加载源预制体
            // 2. 应用实例的所有覆盖
            // 3. 设置 ParentPrefabGuid = instance.SourcePrefabGuid
            // 4. 将 VariantOverrides 设为实例的 Overrides
            // 5. 序列化保存到 outputAssetPath

            return newVariantGuid;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 预制体变体
    // ──────────────────────────────────────────────────────────────

    /// <summary>预制体变体描述。</summary>
    public sealed class PrefabVariant
    {
        /// <summary>变体 GUID。</summary>
        public string Guid { get; set; } = Guid.NewGuid().ToString("N");
        /// <summary>变体名称。</summary>
        public string Name { get; set; }
        /// <summary>父预制体 GUID。</summary>
        public string ParentPrefabGuid { get; set; } = string.Empty;
        /// <summary>变体覆盖列表。</summary>
        public readonly List<PrefabOverrideEntry> Overrides = new();
        /// <summary>变体添加的对象 (额外对象, 父预制体中没有)。</summary>
        public readonly List<string> AddedObjects = new();
        /// <summary>变体移除的对象 (父预制体中有但变体中删除)。</summary>
        public readonly List<string> RemovedObjects = new();
        /// <summary>变体资产路径。</summary>
        public string AssetPath { get; set; } = string.Empty;

        /// <summary>添加覆盖。</summary>
        public void AddOverride(string path, string value)
        {
            Overrides.RemoveAll(o => o.PropertyPath == path);
            Overrides.Add(new PrefabOverrideEntry
            {
                PropertyPath = path,
                OverrideValue = value
            });
        }

        /// <summary>标记添加的对象。</summary>
        public void MarkObjectAdded(string objectPath)
        {
            if (!AddedObjects.Contains(objectPath))
                AddedObjects.Add(objectPath);
            RemovedObjects.Remove(objectPath);
        }

        /// <summary>标记移除的对象。</summary>
        public void MarkObjectRemoved(string objectPath)
        {
            if (!RemovedObjects.Contains(objectPath))
                RemovedObjects.Add(objectPath);
            AddedObjects.Remove(objectPath);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 预制体打包/解包
    // ──────────────────────────────────────────────────────────────

    /// <summary>预制体打包器: 将场景对象打包为预制体资产。</summary>
    public static class PrefabPacker
    {
        /// <summary>打包模式。</summary>
        public enum PackMode
        {
            /// <summary>仅打包选中对象。</summary>
            SelectedOnly,
            /// <summary>打包对象及其所有子对象。</summary>
            WithChildren,
            /// <summary>打包完整层级树。</summary>
            FullHierarchy
        }

        /// <summary>将场景对象打包为预制体。</summary>
        public static string Pack(
            List<object> objects,
            string prefabName,
            string outputPath,
            PackMode mode = PackMode.WithChildren)
        {
            if (objects == null || objects.Count == 0) return string.Empty;

            // 伪实现: 返回新预制体 GUID
            string newGuid = Guid.NewGuid().ToString("N");

            // 真实实现:
            // 1. 收集所有需要打包的对象 (根据 mode)
            // 2. 构建序列化树
            // 3. 写入磁盘

            return newGuid;
        }
    }

    /// <summary>预制体解包器: 将预制体实例解包为普通场景对象。</summary>
    public static class PrefabUnpacker
    {
        /// <summary>解包模式。</summary>
        public enum UnpackMode
        {
            /// <summary>解包为普通对象, 断开与源预制体的链接。</summary>
            Completely,
            /// <summary>解包但保留嵌套预制体引用。</summary>
            Partially
        }

        /// <summary>将预制体实例解包为普通场景对象。</summary>
        public static bool Unpack(
            PrefabInstance instance,
            UnpackMode mode = UnpackMode.Completely)
        {
            if (instance == null) return false;

            // 真实实现:
            // 1. 从源预制体加载完整对象树
            // 2. 应用所有实例覆盖
            // 3. 将对象树直接放入场景
            // 4. 移除预制体引用
            // 5. 删除原预制体实例

            instance.SourcePrefabGuid = string.Empty;
            instance.SourcePrefabPath = string.Empty;
            return true;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 预制体工作流管理器 (总装)
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 预制体工作流管理器: 聚合编辑模式/传播/提取/变体/打包解包。
    /// </summary>
    public sealed class PrefabWorkflowManager
    {
        /// <summary>编辑模式控制器。</summary>
        public PrefabEditModeController EditMode { get; } = new();
        /// <summary>更新传播器。</summary>
        public PrefabUpdatePropagator Propagator { get; } = new();
        /// <summary>覆盖提取器。</summary>
        public PrefabOverrideExtractor Extractor { get; } = new();

        /// <summary>所有已注册的预制体实例。</summary>
        public readonly List<PrefabInstance> AllInstances = new();

        /// <summary>工作流状态变更事件。</summary>
        public event Action OnStateChanged;

        /// <summary>注册新实例。</summary>
        public void RegisterInstance(PrefabInstance instance)
        {
            if (instance == null) return;
            AllInstances.Add(instance);
            Propagator.RegisterInstance(instance);
            OnStateChanged?.Invoke();
        }

        /// <summary>注销实例。</summary>
        public void UnregisterInstance(Guid instanceId)
        {
            AllInstances.RemoveAll(i => i.InstanceId == instanceId);
            Propagator.UnregisterInstance(instanceId);
            OnStateChanged?.Invoke();
        }

        /// <summary>从实例提取覆盖创建变体。</summary>
        public string CreateVariantFromInstance(PrefabInstance instance, string variantName, string outputPath)
        {
            return Extractor.ExtractToVariant(instance, variantName, outputPath);
        }
    }
}
