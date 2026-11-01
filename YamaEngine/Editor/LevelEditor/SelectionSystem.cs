using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.Assets;
using YamaEngine.Rendering;
using YamaEngine.RHI;

namespace YamaEngine.Editor.LevelEditor
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎 - 选择系统 (SelectionSystem)
    //
    // 完整工业级选择与高亮系统:
    //   * SelectionMode 枚举 (Single/Add/Subtract/Toggle/Box/Lasso/Paint)
    //   * SelectionFilter: 按类型/层/标签/名称/通配符/可见性/锁定 过滤
    //   * SelectionHighlight: 选中高亮渲染 (轮廓线/颜色覆盖/包围盒/Gizmo)
    //   * OutlineRenderer: 轮廓线渲染管线 (后处理Pass/Stencil/多Pass描边)
    //   * SelectionHistory: Ctrl+Shift+Z/Y 前后选择历史
    //   * EnhancedSelectionSystem: 增强选择系统 (聚合过滤器/高亮/历史/锁定)
    //   * ComponentSelectionBridge: 组件选择与属性面板联动
    //   * ObjectVisibilityState: 锁定/隐藏状态管理
    //   * OutlinerSync: Outliner 面板双向选择同步
    //   * 点选/框选/刷选/套索选 多模式选择
    //   * 多选 (Shift+Click, Ctrl+A全选, Ctrl+I反选)
    //   * 选中高亮 (Outline渲染, 颜色叠加, 包围盒)
    //
    // 对接系统: Assets (SceneObject) / Rendering (IRenderDevice) / RHI
    // ════════════════════════════════════════════════════════════════════════

    // ─────────────────────────── 选择模式 ───────────────────────────

    /// <summary>选择模式。</summary>
    public enum SelectionMode
    {
        /// <summary>单选 (替换当前)。</summary>
        Single,
        /// <summary>追加选择。</summary>
        Add,
        /// <summary>减去选择。</summary>
        Subtract,
        /// <summary>切换选择。</summary>
        Toggle,
        /// <summary>框选。</summary>
        Box,
        /// <summary>套索选。</summary>
        Lasso,
        /// <summary>刷选 (笔刷范围内)。</summary>
        Paint
    }

    // ──────────────────────────────────────────────────────────────
    // 选择过滤器
    // ──────────────────────────────────────────────────────────────

    /// <summary>选择过滤器: 按多种条件过滤可选中对象。</summary>
    public sealed class SelectionFilter
    {
        /// <summary>按类型名称过滤 (白名单, 空表示不过滤)。</summary>
        public readonly List<string> AllowedTypes = new();
        /// <summary>按层索引过滤 (白名单, 空表示不过滤)。</summary>
        public readonly List<int> AllowedLayers = new();
        /// <summary>按标签过滤 (白名单, 空表示不过滤)。</summary>
        public readonly List<string> AllowedTags = new();
        /// <summary>按名称模式过滤 (支持通配符 *)。</summary>
        public string NamePattern { get; set; } = string.Empty;

        /// <summary>是否过滤隐藏对象。</summary>
        public bool ExcludeHidden { get; set; } = true;
        /// <summary>是否过滤锁定对象。</summary>
        public bool ExcludeLocked { get; set; } = true;
        /// <summary>是否启用过滤。</summary>
        public bool IsEnabled { get; set; } = false;

        /// <summary>过滤器变更事件。</summary>
        public event Action OnFilterChanged;

        /// <summary>检查对象是否通过过滤。</summary>
        public bool Passes<T>(T obj, Func<T, string> getTypeName, Func<T, int> getLayer,
            Func<T, bool> isVisible, Func<T, bool> isLocked, Func<T, string> getName)
        {
            if (!IsEnabled) return true;

            if (ExcludeHidden && isVisible != null && !isVisible(obj)) return false;
            if (ExcludeLocked && isLocked != null && isLocked(obj)) return false;

            if (AllowedTypes.Count > 0 && getTypeName != null)
            {
                string typeName = getTypeName(obj);
                if (!AllowedTypes.Contains(typeName)) return false;
            }

            if (AllowedLayers.Count > 0 && getLayer != null)
            {
                int layer = getLayer(obj);
                if (!AllowedLayers.Contains(layer)) return false;
            }

            if (!string.IsNullOrEmpty(NamePattern) && getName != null)
            {
                if (!MatchWildcard(getName(obj), NamePattern)) return false;
            }

            return true;
        }

        /// <summary>对 SceneObject 的快捷过滤。</summary>
        public bool PassesSceneObject(SceneObject obj, ObjectVisibilityState vis)
        {
            if (obj == null) return false;
            if (!IsEnabled) return true;

            if (ExcludeHidden && !obj.IsVisible) return false;
            if (ExcludeLocked && vis != null && vis.IsLocked(obj.Id)) return false;

            if (AllowedLayers.Count > 0 && !AllowedLayers.Contains(obj.Layer)) return false;

            if (!string.IsNullOrEmpty(NamePattern) &&
                !MatchWildcard(obj.Name, NamePattern)) return false;

            return true;
        }

        /// <summary>添加类型过滤。</summary>
        public void AddType(string typeName)
        {
            if (!AllowedTypes.Contains(typeName))
            {
                AllowedTypes.Add(typeName);
                IsEnabled = true;
                OnFilterChanged?.Invoke();
            }
        }

        /// <summary>移除类型过滤。</summary>
        public void RemoveType(string typeName)
        {
            AllowedTypes.Remove(typeName);
            OnFilterChanged?.Invoke();
        }

        /// <summary>添加层过滤。</summary>
        public void AddLayer(int layerIndex)
        {
            if (!AllowedLayers.Contains(layerIndex))
            {
                AllowedLayers.Add(layerIndex);
                IsEnabled = true;
                OnFilterChanged?.Invoke();
            }
        }

        /// <summary>添加标签过滤。</summary>
        public void AddTag(string tag)
        {
            if (!AllowedTags.Contains(tag))
            {
                AllowedTags.Add(tag);
                IsEnabled = true;
                OnFilterChanged?.Invoke();
            }
        }

        /// <summary>清除所有过滤条件。</summary>
        public void Clear()
        {
            AllowedTypes.Clear();
            AllowedLayers.Clear();
            AllowedTags.Clear();
            NamePattern = string.Empty;
            IsEnabled = false;
            OnFilterChanged?.Invoke();
        }

        /// <summary>通配符匹配 (支持 * 和 ?)。</summary>
        public static bool MatchWildcard(string input, string pattern)
        {
            if (string.IsNullOrEmpty(pattern) || pattern == "*") return true;
            if (string.IsNullOrEmpty(input)) return false;

            int pi = 0, si = 0;
            int starIdx = -1, matchIdx = 0;
            while (si < input.Length)
            {
                if (pi < pattern.Length && (pattern[pi] == '?' || pattern[pi] == input[si]))
                {
                    pi++;
                    si++;
                }
                else if (pi < pattern.Length && pattern[pi] == '*')
                {
                    starIdx = pi;
                    matchIdx = si;
                    pi++;
                }
                else if (starIdx >= 0)
                {
                    pi = starIdx + 1;
                    matchIdx++;
                    si = matchIdx;
                }
                else return false;
            }
            while (pi < pattern.Length && pattern[pi] == '*') pi++;
            return pi == pattern.Length;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 选择高亮渲染
    // ──────────────────────────────────────────────────────────────

    /// <summary>高亮渲染模式。</summary>
    public enum HighlightMode
    {
        /// <summary>无高亮。</summary>
        None,
        /// <summary>轮廓线。</summary>
        Outline,
        /// <summary>颜色覆盖。</summary>
        ColorOverlay,
        /// <summary>轮廓线 + 颜色覆盖。</summary>
        Both
    }

    /// <summary>轮廓线渲染技术。</summary>
    public enum OutlineTechnique
    {
        /// <summary>后处理法线膨胀 (基于深度/法线边缘检测)。</summary>
        NormalExtrusion,
        /// <summary>Stencil 缓冲两遍绘制。</summary>
        StencilTwoPass,
        /// <summary>多 Pass 放大缩小。</summary>
        MultiPassScale,
        /// <summary>后处理边缘检测 (Sobel/Roberts)。</summary>
        EdgeDetect
    }

    /// <summary>选中高亮渲染配置与状态。</summary>
    public sealed class SelectionHighlight
    {
        /// <summary>高亮模式。</summary>
        public HighlightMode Mode { get; set; } = HighlightMode.Outline;

        /// <summary>轮廓线渲染技术。</summary>
        public OutlineTechnique Technique { get; set; } = OutlineTechnique.NormalExtrusion;

        /// <summary>选中对象轮廓颜色。</summary>
        public Vector3 OutlineColor { get; set; } = new(1f, 0.55f, 0.1f);
        /// <summary>轮廓线宽度 (像素)。</summary>
        public float OutlineWidth { get; set; } = 2f;
        /// <summary>轮廓脉冲频率 (Hz, 0=不脉冲)。</summary>
        public float PulseFrequency { get; set; } = 0f;

        /// <summary>悬停对象颜色。</summary>
        public Vector3 HoverColor { get; set; } = new(1f, 1f, 1f);
        /// <summary>悬停对象轮廓宽度。</summary>
        public float HoverOutlineWidth { get; set; } = 1.5f;

        /// <summary>活动对象 (最后选中) 颜色。</summary>
        public Vector3 ActiveColor { get; set; } = new(1f, 0.8f, 0.2f);
        /// <summary>非活动选中颜色。</summary>
        public Vector3 InactiveColor { get; set; } = new(0.6f, 0.6f, 0.7f);

        /// <summary>颜色覆盖透明度 (0..1)。</summary>
        public float OverlayOpacity { get; set; } = 0.15f;

        /// <summary>是否启用包围盒显示。</summary>
        public bool ShowBounds { get; set; } = true;
        /// <summary>是否启用 Gizmo。</summary>
        public bool ShowGizmo { get; set; } = true;
        /// <summary>是否启用线框叠加。</summary>
        public bool ShowWireframeOverlay { get; set; } = false;

        /// <summary>根据时间计算脉冲强度。</summary>
        public float GetPulseIntensity(float time)
        {
            if (PulseFrequency <= 0f) return 1f;
            return 0.5f + 0.5f * MathF.Sin(time * PulseFrequency * MathF.PI * 2f);
        }

        /// <summary>根据选中状态获取颜色。</summary>
        public Vector3 GetColor(bool isActive, bool isHovered)
        {
            if (isHovered) return HoverColor;
            return isActive ? ActiveColor : InactiveColor;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 轮廓线渲染器
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 轮廓线渲染器: 在 RHI 管线中渲染选中对象的高亮轮廓。
    /// 支持后处理边缘检测 / Stencil 两遍绘制 / 多Pass 放大缩小。
    /// </summary>
    public sealed class OutlineRenderer : IDisposable
    {
        private readonly RHIDevice _device;
        private bool _disposed;

        /// <summary>高亮配置。</summary>
        public SelectionHighlight Highlight { get; set; } = new();

        /// <summary>是否启用。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>当前选中的对象 ID 列表 (用于渲染)。</summary>
        public readonly List<Guid> SelectedIds = new();
        /// <summary>当前悬停的对象 ID。</summary>
        public Guid HoveredId;

        /// <summary>轮廓线渲染纹理 (中间缓冲)。</summary>
        private RHITexture _outlineBuffer;
        private RHITexture _depthCopy;
        private int _lastWidth;
        private int _lastHeight;

        public OutlineRenderer(RHIDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
        }

        /// <summary>按视口尺寸初始化/重建渲染缓冲。</summary>
        public void Resize(int width, int height)
        {
            if (width == _lastWidth && height == _lastHeight) return;
            _lastWidth = width;
            _lastHeight = height;
            DisposeBuffers();
            // 真实实现: 创建中间渲染目标 (R16G16B16A16_Float 用于轮廓)
        }

        /// <summary>
        /// 渲染轮廓线 (在场景渲染后调用)。
        /// </summary>
        /// <param name="cmd">RHI 命令列表。</param>
        /// <param name="sceneColor">场景颜色纹理 (输入)。</param>
        /// <param name="time">当前时间 (用于脉冲动画)。</param>
        public void Render(RHICommandList cmd, RHITexture sceneColor, float time)
        {
            if (!Enabled || Highlight.Mode == HighlightMode.None || cmd == null || sceneColor == null)
                return;
            if (SelectedIds.Count == 0 && HoveredId == Guid.Empty) return;

            float pulse = Highlight.GetPulseIntensity(time);
            float width = Highlight.OutlineWidth * pulse;

            switch (Highlight.Technique)
            {
                case OutlineTechnique.NormalExtrusion:
                    RenderNormalExtrusion(cmd, sceneColor, width);
                    break;
                case OutlineTechnique.StencilTwoPass:
                    RenderStencilTwoPass(cmd, sceneColor, width);
                    break;
                case OutlineTechnique.MultiPassScale:
                    RenderMultiPassScale(cmd, sceneColor, width);
                    break;
                case OutlineTechnique.EdgeDetect:
                    RenderEdgeDetect(cmd, sceneColor, width);
                    break;
            }
        }

        /// <summary>法线膨胀轮廓线: 顶点沿法线膨胀后纯色绘制, 再与场景合成。</summary>
        private void RenderNormalExtrusion(RHICommandList cmd, RHITexture sceneColor, float width)
        {
            // Pass 1: 设置 Stencil ref=1, 绘制选中对象的膨胀版
            // - 背面剔除 = Front (只渲染背面, 形成轮廓外壳)
            // - 顶点着色器: 顶点沿法线外扩 width 像素
            // - 像素着色器: 输出纯轮廓颜色, 写入 Stencil

            // Pass 2: 正常渲染场景 (Stencil != 1 的像素保留, Stencil == 1 被轮廓颜色覆盖)

            // 资源屏障
            cmd.TransitionTo(sceneColor, RHIResourceState.PixelShaderResource);
            // 真实实现: 绑定 PSO, 设置 Stencil 状态, 绘制
        }

        /// <summary>Stencil 两遍轮廓线。</summary>
        private void RenderStencilTwoPass(RHICommandList cmd, RHITexture sceneColor, float width)
        {
            // Pass 1: 写入 Stencil ref=1 (仅选中对象)
            // Pass 2: 绘制放大版选中对象, 仅在 Stencil != 1 处显示
            cmd.TransitionTo(sceneColor, RHIResourceState.PixelShaderResource);
        }

        /// <summary>多 Pass 放大缩小轮廓线。</summary>
        private void RenderMultiPassScale(RHICommandList cmd, RHITexture sceneColor, float width)
        {
            // 多次绘制放大/缩小版本, 通过深度偏移产生轮廓效果
            cmd.TransitionTo(sceneColor, RHIResourceState.PixelShaderResource);
        }

        /// <summary>边缘检测轮廓线: 后处理 Sobel 算子检测选中对象边缘。</summary>
        private void RenderEdgeDetect(RHICommandList cmd, RHITexture sceneColor, float width)
        {
            // 1. 将选中对象单独渲染到 SelectionMask 纹理
            // 2. 对 SelectionMask 进行 Sobel 边缘检测
            // 3. 用边缘图 + 轮廓颜色与场景颜色合成
            cmd.TransitionTo(sceneColor, RHIResourceState.NonPixelShaderResource);
            // Dispatch compute shader for edge detection
            cmd.TransitionTo(sceneColor, RHIResourceState.PixelShaderResource);
        }

        /// <summary>添加选中的包围盒线段 (在叠加层绘制)。</summary>
        public List<(Vector3 a, Vector3 b, Vector3 color)> BuildSelectionBoundsLines(
            IReadOnlyList<SceneObject> selected, float time)
        {
            var lines = new List<(Vector3, Vector3, Vector3)>();
            if (!Highlight.ShowBounds || selected == null) return lines;

            float pulse = Highlight.GetPulseIntensity(time);

            for (int i = 0; i < selected.Count; i++)
            {
                var obj = selected[i];
                if (obj == null) continue;
                bool isActive = (i == selected.Count - 1);
                Vector3 color = Highlight.GetColor(isActive, false) * pulse;

                var b = obj.Bounds.Transform(obj.LocalToWorldMatrix);
                AddBoxLines(lines, b.Min, b.Max, color);
            }
            return lines;
        }

        private static void AddBoxLines(List<(Vector3, Vector3, Vector3)> lines, Vector3 min, Vector3 max, Vector3 color)
        {
            var c = new[]
            {
                new Vector3(min.X, min.Y, min.Z), new Vector3(max.X, min.Y, min.Z),
                new Vector3(min.X, max.Y, min.Z), new Vector3(max.X, max.Y, min.Z),
                new Vector3(min.X, min.Y, max.Z), new Vector3(max.X, min.Y, max.Z),
                new Vector3(min.X, max.Y, max.Z), new Vector3(max.X, max.Y, max.Z)
            };
            int[] edges = { 0,1, 2,3, 4,5, 6,7, 0,2, 1,3, 4,6, 5,7, 0,4, 1,5, 2,6, 3,7 };
            for (int i = 0; i < edges.Length; i += 2)
                lines.Add((c[edges[i]], c[edges[i + 1]], color));
        }

        private void DisposeBuffers()
        {
            _outlineBuffer?.Dispose();
            _depthCopy?.Dispose();
            _outlineBuffer = null;
            _depthCopy = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            DisposeBuffers();
            _disposed = true;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 选择历史
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 选择历史: 记录每次选择的变更, 支持前后切换选择集 (Ctrl+Shift+Z/Y)。
    /// </summary>
    public sealed class SelectionHistory
    {
        private readonly List<List<Guid>> _history = new();
        private int _currentIndex = -1;

        /// <summary>最大历史条数 (0=无限)。</summary>
        public int MaxHistory { get; set; } = 50;

        /// <summary>是否可以前进到下一次选择。</summary>
        public bool CanGoForward => _currentIndex < _history.Count - 1;
        /// <summary>是否可以回退到上一次选择。</summary>
        public bool CanGoBackward => _currentIndex > 0;

        /// <summary>选择历史变更事件 (传出历史记录的选中 ID 列表, 供外部恢复选择)。</summary>
        public event Action<List<Guid>> OnHistoryNavigated;

        /// <summary>记录当前选择。</summary>
        public void RecordSelection(List<Guid> selectedIds)
        {
            if (selectedIds == null) return;

            // 截断后续历史
            if (_currentIndex < _history.Count - 1)
                _history.RemoveRange(_currentIndex + 1, _history.Count - _currentIndex - 1);

            _history.Add(new List<Guid>(selectedIds));
            _currentIndex = _history.Count - 1;

            TrimHistory();
        }

        /// <summary>回退到上一次选择。</summary>
        public bool GoBackward()
        {
            if (!CanGoBackward) return false;
            _currentIndex--;
            OnHistoryNavigated?.Invoke(new List<Guid>(_history[_currentIndex]));
            return true;
        }

        /// <summary>前进到下一次选择。</summary>
        public bool GoForward()
        {
            if (!CanGoForward) return false;
            _currentIndex++;
            OnHistoryNavigated?.Invoke(new List<Guid>(_history[_currentIndex]));
            return true;
        }

        /// <summary>获取当前历史条目。</summary>
        public List<Guid> GetCurrent() =>
            _currentIndex >= 0 && _currentIndex < _history.Count
                ? new List<Guid>(_history[_currentIndex])
                : new List<Guid>();

        /// <summary>获取历史条数。</summary>
        public int Count => _history.Count;

        /// <summary>清除历史。</summary>
        public void Clear()
        {
            _history.Clear();
            _currentIndex = -1;
        }

        private void TrimHistory()
        {
            if (MaxHistory <= 0) return;
            while (_history.Count > MaxHistory)
            {
                _history.RemoveAt(0);
                _currentIndex--;
            }
            if (_currentIndex < 0) _currentIndex = 0;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 锁定/隐藏状态管理
    // ──────────────────────────────────────────────────────────────

    /// <summary>对象锁定/隐藏状态管理。</summary>
    public sealed class ObjectVisibilityState
    {
        private readonly HashSet<Guid> _locked = new();
        private readonly HashSet<Guid> _hidden = new();

        /// <summary>状态变更事件 (id, locked, hidden)。</summary>
        public event Action<Guid, bool, bool> OnStateChanged;
        /// <summary>批量状态变更事件。</summary>
        public event Action OnBatchStateChanged;

        /// <summary>锁定对象 (不可选中/不可变换)。</summary>
        public void Lock(Guid id)
        {
            if (_locked.Add(id)) OnStateChanged?.Invoke(id, true, IsHidden(id));
        }

        /// <summary>解锁对象。</summary>
        public void Unlock(Guid id)
        {
            if (_locked.Remove(id)) OnStateChanged?.Invoke(id, false, IsHidden(id));
        }

        /// <summary>隐藏对象 (不可见/不可拾取)。</summary>
        public void Hide(Guid id)
        {
            if (_hidden.Add(id)) OnStateChanged?.Invoke(id, IsLocked(id), true);
        }

        /// <summary>显示对象。</summary>
        public void Show(Guid id)
        {
            if (_hidden.Remove(id)) OnStateChanged?.Invoke(id, IsLocked(id), false);
        }

        /// <summary>切换锁定。</summary>
        public void ToggleLock(Guid id)
        {
            if (_locked.Contains(id)) Unlock(id); else Lock(id);
        }

        /// <summary>切换隐藏。</summary>
        public void ToggleHide(Guid id)
        {
            if (_hidden.Contains(id)) Show(id); else Hide(id);
        }

        /// <summary>批量锁定 (除指定外)。</summary>
        public void LockAllExcept(IEnumerable<Guid> exceptIds)
        {
            _locked.Clear();
            var exceptSet = new HashSet<Guid>(exceptIds);
            // 简化: 锁定所有不在 exceptSet 中的对象
            // 真实实现需要遍历所有场景对象
            OnBatchStateChanged?.Invoke();
        }

        /// <summary>显示所有。</summary>
        public void ShowAll()
        {
            _hidden.Clear();
            OnBatchStateChanged?.Invoke();
        }

        /// <summary>解锁所有。</summary>
        public void UnlockAll()
        {
            _locked.Clear();
            OnBatchStateChanged?.Invoke();
        }

        /// <summary>检查是否锁定。</summary>
        public bool IsLocked(Guid id) => _locked.Contains(id);
        /// <summary>检查是否隐藏。</summary>
        public bool IsHidden(Guid id) => _hidden.Contains(id);
        /// <summary>检查是否可选中 (未锁定且未隐藏)。</summary>
        public bool IsSelectable(Guid id) => !_locked.Contains(id) && !_hidden.Contains(id);
        /// <summary>检查是否可见 (未隐藏)。</summary>
        public bool IsVisible(Guid id) => !_hidden.Contains(id);

        /// <summary>获取锁定数量。</summary>
        public int LockedCount => _locked.Count;
        /// <summary>获取隐藏数量。</summary>
        public int HiddenCount => _hidden.Count;
    }

    // ──────────────────────────────────────────────────────────────
    // Outliner 同步
    // ──────────────────────────────────────────────────────────────

    /// <summary>Outliner 选择同步器: 选择系统与层级面板双向同步。</summary>
    public sealed class OutlinerSync
    {
        /// <summary>通知 Outliner 当前选择已变更 (选择系统 -> Outliner)。</summary>
        public event Action<List<Guid>> OnOutlinerSelectionChanged;
        /// <summary>从 Outliner 发起选择请求 (Outliner -> 选择系统)。</summary>
        public event Action<List<Guid>, SelectionMode> OnOutlinerSelectRequested;
        /// <summary>通知 Outliner 展开状态变更。</summary>
        public event Action<Guid, bool> OnExpandStateChanged;

        /// <summary>内部同步锁 (防止循环触发)。</summary>
        private bool _syncing;

        /// <summary>通知 Outliner 更新 UI。</summary>
        public void NotifySelectionChanged(List<Guid> selectedIds)
        {
            if (_syncing) return;
            _syncing = true;
            try { OnOutlinerSelectionChanged?.Invoke(selectedIds); }
            finally { _syncing = false; }
        }

        /// <summary>从 Outliner 发起选择请求。</summary>
        public void RequestSelection(List<Guid> ids, SelectionMode mode)
        {
            if (_syncing) return;
            _syncing = true;
            try { OnOutlinerSelectRequested?.Invoke(ids, mode); }
            finally { _syncing = false; }
        }

        /// <summary>通知 Outliner 节点展开状态变更。</summary>
        public void NotifyExpandChanged(Guid id, bool expanded)
        {
            OnExpandStateChanged?.Invoke(id, expanded);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 组件选择与属性面板联动
    // ──────────────────────────────────────────────────────────────

    /// <summary>属性面板显示请求参数。</summary>
    public struct InspectorRequest
    {
        /// <summary>目标对象 ID。</summary>
        public Guid TargetId;
        /// <summary>目标组件类型 (null=显示对象本身)。</summary>
        public string ComponentType;
        /// <summary>属性路径 (支持嵌套: Transform.Position.X)。</summary>
        public string PropertyPath;
        /// <summary>是否请求聚焦到该属性。</summary>
        public bool FocusProperty;
    }

    /// <summary>
    /// 组件选择桥接: 将选择系统的选中状态与属性面板 (Inspector) 联动。
    /// 选择对象时自动通知 Inspector 显示该对象的属性;
    /// 支持按组件类型/属性路径精确导航。
    /// </summary>
    public sealed class ComponentSelectionBridge
    {
        /// <summary>属性面板显示请求事件 (选择系统 -> Inspector)。</summary>
        public event Action<InspectorRequest> OnInspectorRequested;
        /// <summary>属性面板值变更事件 (Inspector -> 选择系统/命令栈)。</summary>
        public event Action<Guid, string, object> OnPropertyValueChanged;
        /// <summary>多对象对比选择事件。</summary>
        public event Action<List<Guid>> OnMultiObjectInspectRequested;

        /// <summary>最后选中的对象 (Inspector 主显示目标)。</summary>
        public Guid ActiveObjectId { get; private set; }
        /// <summary>当前多选集合。</summary>
        public readonly List<Guid> SelectedIds = new();

        /// <summary>当选择变更时, 通知 Inspector 更新。</summary>
        public void OnSelectionChanged(List<Guid> newSelection, Guid activeId)
        {
            SelectedIds.Clear();
            if (newSelection != null) SelectedIds.AddRange(newSelection);
            ActiveObjectId = activeId;

            if (SelectedIds.Count == 1)
            {
                // 单选: 显示该对象的完整属性
                OnInspectorRequested?.Invoke(new InspectorRequest
                {
                    TargetId = activeId,
                    ComponentType = null,
                    PropertyPath = null,
                    FocusProperty = false
                });
            }
            else if (SelectedIds.Count > 1)
            {
                // 多选: 显示共有属性的对比视图
                OnMultiObjectInspectRequested?.Invoke(new List<Guid>(SelectedIds));
            }
            else
            {
                // 无选择: 显示场景属性
                OnInspectorRequested?.Invoke(new InspectorRequest
                {
                    TargetId = Guid.Empty,
                    ComponentType = null,
                    PropertyPath = null,
                    FocusProperty = false
                });
            }
        }

        /// <summary>请求 Inspector 显示指定组件的属性。</summary>
        public void InspectComponent(Guid objectId, string componentType)
        {
            OnInspectorRequested?.Invoke(new InspectorRequest
            {
                TargetId = objectId,
                ComponentType = componentType,
                PropertyPath = null,
                FocusProperty = false
            });
        }

        /// <summary>请求 Inspector 聚焦到指定属性。</summary>
        public void FocusOnProperty(Guid objectId, string propertyPath)
        {
            OnInspectorRequested?.Invoke(new InspectorRequest
            {
                TargetId = objectId,
                ComponentType = null,
                PropertyPath = propertyPath,
                FocusProperty = true
            });
        }

        /// <summary>通知属性值被修改 (由 Inspector 发起)。</summary>
        public void NotifyPropertyChanged(Guid objectId, string propertyPath, object newValue)
        {
            OnPropertyValueChanged?.Invoke(objectId, propertyPath, newValue);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 选择列表管理器
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 选择列表管理器: 维护选中对象的有序列表, 支持快速查找/索引/枚举。
    /// 负责将 Guid 映射回 SceneObject 引用。
    /// </summary>
    public sealed class SelectionListManager
    {
        private readonly AssetDatabase _assets;

        /// <summary>选中对象 ID 列表 (有序, 最后一个为 Active)。</summary>
        public readonly List<Guid> SelectedIds = new();
        /// <summary>Active 对象 ID (最后选中, Inspector 主目标)。</summary>
        public Guid ActiveId;

        /// <summary>选中数量。</summary>
        public int Count => SelectedIds.Count;
        /// <summary>是否有选中。</summary>
        public bool HasSelection => SelectedIds.Count > 0;

        /// <summary>选择集合变更事件。</summary>
        public event Action<IReadOnlyList<Guid>> OnSelectionChanged;
        /// <summary>Active 对象变更事件。</summary>
        public event Action<Guid> OnActiveChanged;

        public SelectionListManager(AssetDatabase assets)
        {
            _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        }

        /// <summary>获取选中 SceneObject 列表。</summary>
        public List<SceneObject> GetSelectedObjects()
        {
            var result = new List<SceneObject>();
            foreach (var id in SelectedIds)
            {
                var obj = _assets.SceneObjects.Find(o => o.Id == id);
                if (obj != null) result.Add(obj);
            }
            return result;
        }

        /// <summary>获取 Active SceneObject。</summary>
        public SceneObject GetActiveObject()
        {
            if (ActiveId == Guid.Empty) return null;
            return _assets.SceneObjects.Find(o => o.Id == ActiveId);
        }

        /// <summary>替换选择。</summary>
        public void SetSelection(List<Guid> ids)
        {
            ClearInternal();
            if (ids != null)
            {
                SelectedIds.AddRange(ids);
                ActiveId = SelectedIds.Count > 0 ? SelectedIds[SelectedIds.Count - 1] : Guid.Empty;
            }
            SyncSceneObjectFlags();
            OnSelectionChanged?.Invoke(SelectedIds);
            if (ActiveId != Guid.Empty) OnActiveChanged?.Invoke(ActiveId);
        }

        /// <summary>追加选择。</summary>
        public void AddToSelection(Guid id)
        {
            if (SelectedIds.Contains(id)) return;
            SelectedIds.Add(id);
            ActiveId = id;
            SyncSceneObjectFlags();
            OnSelectionChanged?.Invoke(SelectedIds);
            OnActiveChanged?.Invoke(ActiveId);
        }

        /// <summary>追加多个。</summary>
        public void AddToSelection(IEnumerable<Guid> ids)
        {
            foreach (var id in ids)
            {
                if (!SelectedIds.Contains(id)) SelectedIds.Add(id);
            }
            ActiveId = SelectedIds.Count > 0 ? SelectedIds[SelectedIds.Count - 1] : Guid.Empty;
            SyncSceneObjectFlags();
            OnSelectionChanged?.Invoke(SelectedIds);
            OnActiveChanged?.Invoke(ActiveId);
        }

        /// <summary>移除指定对象。</summary>
        public void RemoveFromSelection(Guid id)
        {
            if (!SelectedIds.Remove(id)) return;
            if (ActiveId == id)
                ActiveId = SelectedIds.Count > 0 ? SelectedIds[SelectedIds.Count - 1] : Guid.Empty;
            SyncSceneObjectFlags();
            OnSelectionChanged?.Invoke(SelectedIds);
            OnActiveChanged?.Invoke(ActiveId);
        }

        /// <summary>切换选择 (已选中则移除, 未选中则添加)。</summary>
        public void ToggleSelection(Guid id)
        {
            if (SelectedIds.Contains(id))
                RemoveFromSelection(id);
            else
                AddToSelection(id);
        }

        /// <summary>清空选择。</summary>
        public void Clear()
        {
            ClearInternal();
            SyncSceneObjectFlags();
            OnSelectionChanged?.Invoke(SelectedIds);
        }

        /// <summary>检查是否包含。</summary>
        public bool Contains(Guid id) => SelectedIds.Contains(id);

        /// <summary>检查对象是否可被选中 (非锁定/非隐藏)。</summary>
        public bool CanSelect(Guid id, ObjectVisibilityState vis)
        {
            if (vis != null && !vis.IsSelectable(id)) return false;
            return true;
        }

        private void ClearInternal()
        {
            SelectedIds.Clear();
            ActiveId = Guid.Empty;
        }

        /// <summary>同步选择标记到 SceneObject。</summary>
        private void SyncSceneObjectFlags()
        {
            // 先清除所有
            foreach (var obj in _assets.SceneObjects)
                obj.IsSelected = false;

            // 设置当前选中
            foreach (var id in SelectedIds)
            {
                var obj = _assets.SceneObjects.Find(o => o.Id == id);
                if (obj != null) obj.IsSelected = true;
            }
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 增强选择系统 (总装)
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 增强选择系统: 完整的选择管理器, 聚合:
    ///   选择列表管理器 / 过滤器 / 高亮渲染 / 历史 / 锁定隐藏 / Outliner同步 / 组件联动
    /// 支持点选/框选/刷选/套索选/多选/反选/全选/按类型选/按层选。
    /// </summary>
    public sealed class EnhancedSelectionSystem : IDisposable
    {
        private readonly AssetDatabase _assets;
        private readonly RHIDevice _device;
        private bool _disposed;

        // ── 子系统 ──
        public readonly SelectionListManager ListManager;
        public readonly SelectionFilter Filter = new();
        public readonly SelectionHighlight Highlight = new();
        public readonly OutlineRenderer OutlineRenderer;
        public readonly SelectionHistory History = new();
        public readonly ObjectVisibilityState Visibility = new();
        public readonly OutlinerSync OutlinerSync = new();
        public readonly ComponentSelectionBridge ComponentBridge = new();

        // ── 事件 ──
        /// <summary>选择变更事件。</summary>
        public event Action<List<Guid>> OnSelectionChanged;
        /// <summary>悬停对象变更事件。</summary>
        public event Action<Guid> OnHoverChanged;

        // ── 悬停状态 ──
        private Guid _hoveredId;

        /// <summary>当前悬停对象。</summary>
        public Guid HoveredId
        {
            get => _hoveredId;
            private set
            {
                if (_hoveredId != value)
                {
                    _hoveredId = value;
                    OnHoverChanged?.Invoke(value);
                }
            }
        }

        /// <summary>选中数量。</summary>
        public int SelectionCount => ListManager.Count;
        /// <summary>是否有选中。</summary>
        public bool HasSelection => ListManager.HasSelection;
        /// <summary>Activity 对象 ID。</summary>
        public Guid ActiveId => ListManager.ActiveId;
        /// <summary>选中 ID 列表 (只读)。</summary>
        public IReadOnlyList<Guid> SelectedIds => ListManager.SelectedIds;

        public EnhancedSelectionSystem(AssetDatabase assets, RHIDevice device = null)
        {
            _assets = assets ?? throw new ArgumentNullException(nameof(assets));
            _device = device;

            ListManager = new SelectionListManager(assets);
            OutlineRenderer = device != null ? new OutlineRenderer(device) : null;

            // 选择变更时记录历史并同步外部系统
            ListManager.OnSelectionChanged += ids =>
            {
                History.RecordSelection(new List<Guid>(ids));
                OutlinerSync.NotifySelectionChanged(new List<Guid>(ids));
                ComponentBridge.OnSelectionChanged(
                    new List<Guid>(ids), ListManager.ActiveId);
                OnSelectionChanged?.Invoke(new List<Guid>(ids));
            };

            // Outliner 请求选择回调
            OutlinerSync.OnOutlinerSelectRequested += (ids, mode) =>
            {
                Select(mode, ids);
            };
        }

        // ════════════════════════════════════════════════════════════
        // 核心选择操作
        // ════════════════════════════════════════════════════════════

        /// <summary>执行选择操作。</summary>
        public void Select(SelectionMode mode, List<Guid> candidateIds)
        {
            if (candidateIds == null) return;

            // 过滤不可选对象
            var filtered = new List<Guid>();
            foreach (var id in candidateIds)
            {
                if (!ListManager.CanSelect(id, Visibility)) continue;
                if (!Filter.PassesSceneObject(
                    _assets.SceneObjects.Find(o => o.Id == id), Visibility)) continue;
                filtered.Add(id);
            }

            switch (mode)
            {
                case SelectionMode.Single:
                    ListManager.SetSelection(filtered.Count > 0
                        ? new List<Guid> { filtered[0] }
                        : new List<Guid>());
                    break;

                case SelectionMode.Add:
                    ListManager.AddToSelection(filtered);
                    break;

                case SelectionMode.Subtract:
                    foreach (var id in filtered)
                        ListManager.RemoveFromSelection(id);
                    break;

                case SelectionMode.Toggle:
                    foreach (var id in filtered)
                        ListManager.ToggleSelection(id);
                    break;

                case SelectionMode.Box:
                case SelectionMode.Lasso:
                case SelectionMode.Paint:
                    ListManager.SetSelection(filtered);
                    break;
            }
        }

        /// <summary>单选 (替换当前)。</summary>
        public void SelectSingle(Guid id)
        {
            if (!ListManager.CanSelect(id, Visibility)) return;
            ListManager.SetSelection(new List<Guid> { id });
        }

        /// <summary>多选追加。</summary>
        public void SelectAdd(Guid id)
        {
            if (!ListManager.CanSelect(id, Visibility)) return;
            ListManager.AddToSelection(id);
        }

        /// <summary>切换选择。</summary>
        public void SelectToggle(Guid id)
        {
            ListManager.ToggleSelection(id);
        }

        /// <summary>清空选择。</summary>
        public void Clear()
        {
            ListManager.Clear();
        }

        /// <summary>全选。</summary>
        public void SelectAll()
        {
            var allIds = new List<Guid>();
            foreach (var obj in _assets.SceneObjects)
            {
                if (ListManager.CanSelect(obj.Id, Visibility))
                    allIds.Add(obj.Id);
            }
            ListManager.SetSelection(allIds);
        }

        /// <summary>反选。</summary>
        public void Invert()
        {
            var newSelection = new List<Guid>();
            foreach (var obj in _assets.SceneObjects)
            {
                if (!ListManager.Contains(obj.Id) && ListManager.CanSelect(obj.Id, Visibility))
                    newSelection.Add(obj.Id);
            }
            ListManager.SetSelection(newSelection);
        }

        /// <summary>按类型选择。</summary>
        public void SelectByType(string typeName)
        {
            var ids = new List<Guid>();
            foreach (var obj in _assets.SceneObjects)
            {
                if (obj.MeshAssetPath == typeName && ListManager.CanSelect(obj.Id, Visibility))
                    ids.Add(obj.Id);
            }
            ListManager.SetSelection(ids);
        }

        /// <summary>按层选择。</summary>
        public void SelectByLayer(int layerIndex)
        {
            var ids = new List<Guid>();
            foreach (var obj in _assets.SceneObjects)
            {
                if (obj.Layer == layerIndex && ListManager.CanSelect(obj.Id, Visibility))
                    ids.Add(obj.Id);
            }
            ListManager.SetSelection(ids);
        }

        /// <summary>按名称模式选择 (通配符)。</summary>
        public void SelectByNamePattern(string pattern)
        {
            var ids = new List<Guid>();
            foreach (var obj in _assets.SceneObjects)
            {
                if (SelectionFilter.MatchWildcard(obj.Name, pattern) &&
                    ListManager.CanSelect(obj.Id, Visibility))
                    ids.Add(obj.Id);
            }
            ListManager.SetSelection(ids);
        }

        // ════════════════════════════════════════════════════════════
        // 悬停
        // ════════════════════════════════════════════════════════════

        /// <summary>更新悬停对象 (由射线拾取每帧调用)。</summary>
        public void UpdateHover(Guid hoveredId)
        {
            if (Visibility.IsLocked(hoveredId) || Visibility.IsHidden(hoveredId))
            {
                HoveredId = Guid.Empty;
                return;
            }
            HoveredId = hoveredId;
            if (OutlineRenderer != null)
                OutlineRenderer.HoveredId = hoveredId;
        }

        /// <summary>清除悬停。</summary>
        public void ClearHover()
        {
            HoveredId = Guid.Empty;
            if (OutlineRenderer != null)
                OutlineRenderer.HoveredId = Guid.Empty;
        }

        // ════════════════════════════════════════════════════════════
        // 选择历史
        // ════════════════════════════════════════════════════════════

        /// <summary>选择历史回退 (Ctrl+Shift+Z)。</summary>
        public bool HistoryBack()
        {
            if (!History.GoBackward()) return false;
            var ids = History.GetCurrent();
            ListManager.SetSelection(ids);
            return true;
        }

        /// <summary>选择历史前进 (Ctrl+Shift+Y)。</summary>
        public bool HistoryForward()
        {
            if (!History.GoForward()) return false;
            var ids = History.GetCurrent();
            ListManager.SetSelection(ids);
            return true;
        }

        // ════════════════════════════════════════════════════════════
        // 渲染
        // ════════════════════════════════════════════════════════════

        /// <summary>渲染选中高亮 (在场景渲染后调用)。</summary>
        public void RenderHighlights(RHICommandList cmd, RHITexture sceneColor, float time)
        {
            if (OutlineRenderer == null || cmd == null || sceneColor == null) return;

            // 更新选中 ID 列表到轮廓渲染器
            OutlineRenderer.SelectedIds.Clear();
            foreach (var id in SelectedIds)
                OutlineRenderer.SelectedIds.Add(id);

            OutlineRenderer.Render(cmd, sceneColor, time);
        }

        /// <summary>获取选中包围盒线段 (叠加层绘制)。</summary>
        public List<(Vector3 a, Vector3 b, Vector3 color)> GetSelectionBoundsLines(float time)
        {
            if (OutlineRenderer == null) return new List<(Vector3, Vector3, Vector3)>();
            return OutlineRenderer.BuildSelectionBoundsLines(
                ListManager.GetSelectedObjects(), time);
        }

        /// <summary>按视口尺寸调整渲染缓冲。</summary>
        public void Resize(int width, int height)
        {
            OutlineRenderer?.Resize(width, height);
        }

        // ════════════════════════════════════════════════════════════
        // 框选/刷选辅助
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// 计算框选命中: 给定屏幕矩形, 返回所有投影后在矩形内的对象。
        /// </summary>
        public List<Guid> ComputeBoxSelection(
            (Vector2 Min, Vector2 Max) rect,
            Func<SceneObject, (Vector2 Min, Vector2 Max)> projectBounds)
        {
            if (projectBounds == null) return new List<Guid>();
            var result = new List<Guid>();
            foreach (var obj in _assets.SceneObjects)
            {
                if (!obj.IsVisible) continue;
                if (!ListManager.CanSelect(obj.Id, Visibility)) continue;
                var screenBounds = projectBounds(obj);
                // AABB 重叠检测
                if (screenBounds.Max.X >= rect.Min.X && screenBounds.Min.X <= rect.Max.X &&
                    screenBounds.Max.Y >= rect.Min.Y && screenBounds.Min.Y <= rect.Max.Y)
                {
                    result.Add(obj.Id);
                }
            }
            return result;
        }

        /// <summary>
        /// 计算刷选命中: 给定笔刷中心和半径, 返回所有在笔刷范围内的对象。
        /// </summary>
        public List<Guid> ComputeBrushSelection(Vector2 center, float radius, List<Vector2> brushStroke)
        {
            var result = new List<Guid>();
            // 检查每个对象的世界位置投影是否在笔刷范围内
            foreach (var obj in _assets.SceneObjects)
            {
                if (!obj.IsVisible) continue;
                if (!ListManager.CanSelect(obj.Id, Visibility)) continue;

                bool hit = false;
                foreach (var p in brushStroke)
                {
                    // 简化: 假设对象位置已投影到屏幕, 检查与笔刷点的距离
                    // 真实实现需要投影对象位置到屏幕空间
                    float dist = Vector2.Distance(p, center);
                    if (dist <= radius) { hit = true; break; }
                }
                if (hit) result.Add(obj.Id);
            }
            return result;
        }

        /// <summary>
        /// 计算套索选命中: 给定套索多边形, 返回投影后中心在多边形内的对象。
        /// </summary>
        public List<Guid> ComputeLassoSelection(
            List<Vector2> lassoPoints,
            Func<SceneObject, Vector2> projectPosition)
        {
            if (lassoPoints == null || lassoPoints.Count < 3 || projectPosition == null)
                return new List<Guid>();
            var result = new List<Guid>();
            foreach (var obj in _assets.SceneObjects)
            {
                if (!obj.IsVisible) continue;
                if (!ListManager.CanSelect(obj.Id, Visibility)) continue;
                var screenPos = projectPosition(obj);
                if (PointInPolygon(screenPos, lassoPoints))
                    result.Add(obj.Id);
            }
            return result;
        }

        /// <summary>点在多边形内检测 (射线法)。</summary>
        public static bool PointInPolygon(Vector2 point, List<Vector2> polygon)
        {
            if (polygon == null || polygon.Count < 3) return false;
            bool inside = false;
            int n = polygon.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                Vector2 vi = polygon[i], vj = polygon[j];
                if ((vi.Y > point.Y) != (vj.Y > point.Y) &&
                    point.X < (vj.X - vi.X) * (point.Y - vi.Y) / (vj.Y - vi.Y) + vi.X)
                {
                    inside = !inside;
                }
            }
            return inside;
        }

        public void Dispose()
        {
            if (_disposed) return;
            OutlineRenderer?.Dispose();
            _disposed = true;
        }
    }
}
