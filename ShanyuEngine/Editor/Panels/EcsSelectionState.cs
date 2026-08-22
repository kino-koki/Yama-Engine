// ==================================================================================================
//  EcsSelectionState.cs — 山雨引擎 ECS 实体选择状态
// ==================================================================================================
//  命名空间: ShanyuEngine.Editor.Panels
//  依赖:     ShanyuEngine.ECS
//
//  功能:
//    管理 ECS 实体的选择状态，支持单选/多选/切换选，
//    作为 EcsHierarchyPanel 与 EcsInspectorPanel 之间的通信桥梁。
// ==================================================================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace ShanyuEngine.Editor.Panels
{
    /// <summary>
    /// ECS 实体选择状态。跟踪当前选中的实体 ID 列表，
    /// 并在选择变化时通知订阅者。
    /// </summary>
    public sealed class EcsSelectionState
    {
        private readonly List<int> _selected = new();
        private int _active = -1;

        /// <summary>当前选中的实体 ID 列表（只读）。</summary>
        public IReadOnlyList<int> Selected => _selected;

        /// <summary>活跃实体 ID（最后选中的，Inspector 显示此实体）。</summary>
        public int Active
        {
            get => _active;
            private set => _active = value;
        }

        /// <summary>选中数量。</summary>
        public int Count => _selected.Count;

        /// <summary>是否有选中。</summary>
        public bool HasSelection => _selected.Count > 0;

        /// <summary>选择变化事件。</summary>
        public event Action<IReadOnlyList<int>>? OnSelectionChanged;

        /// <summary>活跃实体变化事件。</summary>
        public event Action<int>? OnActiveChanged;

        /// <summary>选中指定实体（替换当前选择）。</summary>
        public void Select(int entityId)
        {
            _selected.Clear();
            _selected.Add(entityId);
            _active = entityId;
            FireChanged();
        }

        /// <summary>追加选中。</summary>
        public void SelectAdd(int entityId)
        {
            if (!_selected.Contains(entityId))
                _selected.Add(entityId);
            _active = entityId;
            FireChanged();
        }

        /// <summary>切换选中状态。</summary>
        public void SelectToggle(int entityId)
        {
            if (_selected.Contains(entityId))
            {
                _selected.Remove(entityId);
                if (_active == entityId)
                    _active = _selected.Count > 0 ? _selected[^1] : -1;
            }
            else
            {
                _selected.Add(entityId);
                _active = entityId;
            }
            FireChanged();
        }

        /// <summary>清除所有选择。</summary>
        public void Clear()
        {
            if (_selected.Count == 0) return;
            _selected.Clear();
            _active = -1;
            FireChanged();
        }

        /// <summary>是否选中了指定实体。</summary>
        public bool IsSelected(int entityId) => _selected.Contains(entityId);

        /// <summary>设置活跃实体（必须在已选中列表中）。</summary>
        public void SetActive(int entityId)
        {
            if (!_selected.Contains(entityId)) return;
            if (_active == entityId) return;
            _active = entityId;
            OnActiveChanged?.Invoke(entityId);
        }

        private void FireChanged()
        {
            OnSelectionChanged?.Invoke(_selected);
            OnActiveChanged?.Invoke(_active);
        }
    }
}
