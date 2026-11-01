using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.World;

namespace YamaEngine.Editor.LevelEditor
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎 - 世界分区编辑器 (WorldStreamingGridEditor)
    //
    // 类似 UE World Partition Editor 的世界分区可视编辑系统:
    //   * WorldStreamingGridEditorConfig: 网格大小/加载范围/流式距离/HLOD设置
    //   * PartitionCell 可视化: 网格线/加载状态颜色/内容统计
    //   * HLOD配置: LOD距离/Actor合并/简化比例/生成
    //   * 流式体积编辑器: StreamingVolume (加载/卸载/优先级)
    //   * 加载范围调试可视化: 球体+距离标注
    //   * 数据层编辑器: DataLayer (分层加载/可见性切换)
    //   * 世界边界管理
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────
    // 世界分区编辑器配置
    // ──────────────────────────────────────────────────────────────

    /// <summary>世界分区编辑器配置。</summary>
    public sealed class WorldStreamingGridEditorConfig
    {
        /// <summary>网格单元大小 (世界单位)。</summary>
        public float CellSize { get; set; } = 256f;
        /// <summary>加载半径 (网格数)。</summary>
        public int LoadRadius { get; set; } = 3;
        /// <summary>卸载半径 (网格数)。</summary>
        public int UnloadRadius { get; set; } = 5;
        /// <summary>最大同时加载数。</summary>
        public int MaxConcurrentLoads { get; set; } = 4;
        /// <summary>最大活跃单元数。</summary>
        public int MaxActiveCells { get; set; } = 64;
        /// <summary>是否启用异步加载。</summary>
        public bool AsyncLoading { get; set; } = true;
        /// <summary>LOD 距离因子。</summary>
        public float LodDistanceFactor { get; set; } = 1f;

        /// <summary>HLOD 配置。</summary>
        public HlodConfig Hlod { get; set; } = new();

        /// <summary>应用到 WorldStreamingGridSystem.StreamingOptions。</summary>
        public StreamingOptions ToStreamingOptions()
        {
            return new StreamingOptions
            {
                CellSize = CellSize,
                LoadRadius = LoadRadius,
                UnloadRadius = UnloadRadius,
                MaxConcurrentLoads = MaxConcurrentLoads,
                MaxActiveCells = MaxActiveCells,
                AsyncLoading = AsyncLoading,
                LODDistanceFactor = LodDistanceFactor
            };
        }
    }

    // ──────────────────────────────────────────────────────────────
    // HLOD 配置
    // ──────────────────────────────────────────────────────────────

    /// <summary>HLOD (Hierarchical Level of Detail) 配置。</summary>
    public sealed class HlodConfig
    {
        /// <summary>LOD 0 距离。</summary>
        public float Lod0Distance { get; set; } = 50f;
        /// <summary>LOD 1 距离。</summary>
        public float Lod1Distance { get; set; } = 100f;
        /// <summary>LOD 2 距离。</summary>
        public float Lod2Distance { get; set; } = 200f;
        /// <summary>LOD 3 距离。</summary>
        public float Lod3Distance { get; set; } = 500f;

        /// <summary>是否启用 Actor 自动合并。</summary>
        public bool EnableActorMerging { get; set; } = true;
        /// <summary>每个 HLOD 集群最大 Actor 数。</summary>
        public int MaxActorsPerCluster { get; set; } = 32;
        /// <summary>简化比例 (0..1, 0=不简化)。</summary>
        public float SimplificationRatio { get; set; } = 0.5f;

        /// <summary>是否自动生成。</summary>
        public bool AutoGenerate { get; set; } = false;

        /// <summary>生成回调。</summary>
        public event Action OnGenerateRequested;

        /// <summary>请求 HLOD 生成。</summary>
        public void RequestGenerate() => OnGenerateRequested?.Invoke();

        /// <summary>根据距离获取 LOD 等级。</summary>
        public int GetLodLevel(float distance)
        {
            if (distance < Lod0Distance) return 0;
            if (distance < Lod1Distance) return 1;
            if (distance < Lod2Distance) return 2;
            return 3;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 分区单元格可视化
    // ──────────────────────────────────────────────────────────────

    /// <summary>分区单元格可视化数据。</summary>
    public sealed class PartitionCellVisualization
    {
        /// <summary>是否显示网格线。</summary>
        public bool ShowGridLines { get; set; } = true;
        /// <summary>是否显示加载状态颜色。</summary>
        public bool ShowLoadStateColors { get; set; } = true;
        /// <summary>是否显示内容统计。</summary>
        public bool ShowContentStats { get; set; } = false;
        /// <summary>网格线颜色。</summary>
        public Vector3 GridLineColor { get; set; } = new(0.3f, 0.3f, 0.35f);

        /// <summary>加载状态颜色映射。</summary>
        public static readonly Dictionary<CellState, Vector3> StateColors = new()
        {
            { CellState.Unloaded, new Vector3(0.2f, 0.2f, 0.2f) },
            { CellState.Loading, new Vector3(1.0f, 0.8f, 0.2f) },
            { CellState.Loaded, new Vector3(0.2f, 0.8f, 0.3f) },
            { CellState.Active, new Vector3(0.2f, 0.6f, 1.0f) },
            { CellState.Unloading, new Vector3(0.8f, 0.4f, 0.2f) }
        };

        /// <summary>构建单元格网格线段。</summary>
        public List<(Vector3 A, Vector3 B, Vector3 Color)> BuildCellGridLines(
            IReadOnlyList<WorldCell> cells, float yPlane)
        {
            var lines = new List<(Vector3, Vector3, Vector3)>();
            if (!ShowGridLines) return lines;

            foreach (var cell in cells)
            {
                Vector3 color = ShowLoadStateColors
                    ? StateColors.GetValueOrDefault(cell.State, GridLineColor)
                    : GridLineColor;

                float half = cell.Size * 0.5f;
                float cx = cell.Center.X;
                float cz = cell.Center.Z;

                // 底部四条边
                Vector3 a = new(cx - half, yPlane, cz - half);
                Vector3 b = new(cx + half, yPlane, cz - half);
                Vector3 c = new(cx + half, yPlane, cz + half);
                Vector3 d = new(cx - half, yPlane, cz + half);

                lines.Add((a, b, color));
                lines.Add((b, c, color));
                lines.Add((c, d, color));
                lines.Add((d, a, color));
            }

            return lines;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 流式体积编辑器
    // ──────────────────────────────────────────────────────────────

    /// <summary>流式体积操作模式。</summary>
    public enum StreamingVolumeMode
    {
        Load,
        Unload,
        Priority
    }

    /// <summary>
    /// 流式体积: 定义一个空间范围, 控制进入/离开时单元的加载/卸载行为。
    /// </summary>
    public sealed class StreamingVolume
    {
        /// <summary>体积 ID。</summary>
        public Guid Id { get; } = Guid.NewGuid();
        /// <summary>名称。</summary>
        public string Name { get; set; } = "StreamingVolume";
        /// <summary>世界位置。</summary>
        public Vector3 Position { get; set; }
        /// <summary>盒体尺寸。</summary>
        public Vector3 BoxSize { get; set; } = new(256f, 256f, 256f);
        /// <summary>优先级。</summary>
        public float Priority { get; set; } = 1f;
        /// <summary>操作模式。</summary>
        public StreamingVolumeMode Mode { get; set; } = StreamingVolumeMode.Load;
        /// <summary>是否启用。</summary>
        public bool IsEnabled { get; set; } = true;
        /// <summary>加载半径覆盖 (0 = 使用全局设置)。</summary>
        public float OverrideLoadRadius { get; set; }

        /// <summary>判断一个世界坐标是否在体积内。</summary>
        public bool Contains(Vector3 point)
        {
            Vector3 half = BoxSize * 0.5f;
            return point.X >= Position.X - half.X && point.X <= Position.X + half.X &&
                   point.Y >= Position.Y - half.Y && point.Y <= Position.Y + half.Y &&
                   point.Z >= Position.Z - half.Z && point.Z <= Position.Z + half.Z;
        }

        /// <summary>构建体积线框。</summary>
        public List<(Vector3 A, Vector3 B)> BuildWireframe()
        {
            var lines = new List<(Vector3, Vector3)>();
            Vector3 half = BoxSize * 0.5f;
            Vector3 mn = Position - half;
            Vector3 mx = Position + half;

            var c = new[]
            {
                new Vector3(mn.X, mn.Y, mn.Z), new Vector3(mx.X, mn.Y, mn.Z),
                new Vector3(mx.X, mx.Y, mn.Z), new Vector3(mn.X, mx.Y, mn.Z),
                new Vector3(mn.X, mn.Y, mx.Z), new Vector3(mx.X, mn.Y, mx.Z),
                new Vector3(mx.X, mx.Y, mx.Z), new Vector3(mn.X, mx.Y, mx.Z)
            };

            int[] edges = { 0,1, 1,2, 2,3, 3,0, 4,5, 5,6, 6,7, 7,4, 0,4, 1,5, 2,6, 3,7 };
            for (int i = 0; i < edges.Length; i += 2)
                lines.Add((c[edges[i]], c[edges[i + 1]]));

            return lines;
        }
    }

    /// <summary>流式体积编辑器: 管理所有流式体积的创建/编辑/删除。</summary>
    public sealed class StreamingVolumeEditor
    {
        /// <summary>所有流式体积。</summary>
        public readonly List<StreamingVolume> Volumes = new();

        /// <summary>体积变更事件。</summary>
        public event Action OnVolumesChanged;

        /// <summary>创建新的流式体积。</summary>
        public StreamingVolume Create(Vector3 position, Vector3 size)
        {
            var volume = new StreamingVolume
            {
                Position = position,
                BoxSize = size
            };
            Volumes.Add(volume);
            OnVolumesChanged?.Invoke();
            return volume;
        }

        /// <summary>删除流式体积。</summary>
        public bool Remove(Guid id)
        {
            int idx = Volumes.FindIndex(v => v.Id == id);
            if (idx < 0) return false;
            Volumes.RemoveAt(idx);
            OnVolumesChanged?.Invoke();
            return true;
        }

        /// <summary>获取指定位置的活跃体积列表。</summary>
        public List<StreamingVolume> GetActiveVolumesAt(Vector3 position)
        {
            var result = new List<StreamingVolume>();
            foreach (var v in Volumes)
            {
                if (v.IsEnabled && v.Contains(position))
                    result.Add(v);
            }
            return result;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 加载范围调试可视化
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 加载范围调试可视化: 在视口中显示加载/卸载范围球体和距离标注。
    /// </summary>
    public sealed class LoadRangeDebugVisualizer
    {
        /// <summary>是否显示。</summary>
        public bool Visible { get; set; } = false;

        /// <summary>视点位置。</summary>
        public Vector3 Viewpoint { get; set; }

        /// <summary>加载半径 (世界单位)。</summary>
        public float LoadRadiusWorld { get; set; }
        /// <summary>卸载半径 (世界单位)。</summary>
        public float UnloadRadiusWorld { get; set; }

        /// <summary>加载区域颜色。</summary>
        public Vector3 LoadColor { get; set; } = new(0.2f, 0.8f, 0.3f);
        /// <summary>卸载区域颜色。</summary>
        public Vector3 UnloadColor { get; set; } = new(0.8f, 0.3f, 0.2f);

        /// <summary>构建范围圈线段。</summary>
        public List<(Vector3 A, Vector3 B, Vector3 Color)> BuildRangeCircles(float yPlane)
        {
            var lines = new List<(Vector3, Vector3, Vector3)>();
            if (!Visible) return lines;

            int segments = 64;

            // 加载范围圈
            lines.AddRange(BuildCircle(Viewpoint, LoadRadiusWorld, yPlane, LoadColor, segments));
            // 卸载范围圈
            lines.AddRange(BuildCircle(Viewpoint, UnloadRadiusWorld, yPlane, UnloadColor, segments));

            // 十字线标注
            lines.Add((Viewpoint, new Vector3(Viewpoint.X + LoadRadiusWorld, yPlane, Viewpoint.Z), LoadColor));
            lines.Add((Viewpoint, new Vector3(Viewpoint.X - LoadRadiusWorld, yPlane, Viewpoint.Z), LoadColor));
            lines.Add((Viewpoint, new Vector3(Viewpoint.X, yPlane, Viewpoint.Z + LoadRadiusWorld), LoadColor));
            lines.Add((Viewpoint, new Vector3(Viewpoint.X, yPlane, Viewpoint.Z - LoadRadiusWorld), LoadColor));

            return lines;
        }

        private static List<(Vector3, Vector3, Vector3)> BuildCircle(
            Vector3 center, float radius, float y, Vector3 color, int segments)
        {
            var lines = new List<(Vector3, Vector3, Vector3)>();
            for (int i = 0; i < segments; i++)
            {
                float a1 = (float)i / segments * MathF.PI * 2f;
                float a2 = (float)(i + 1) / segments * MathF.PI * 2f;
                Vector3 p1 = new(center.X + MathF.Cos(a1) * radius, y, center.Z + MathF.Sin(a1) * radius);
                Vector3 p2 = new(center.X + MathF.Cos(a2) * radius, y, center.Z + MathF.Sin(a2) * radius);
                lines.Add((p1, p2, color));
            }
            return lines;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 数据层编辑器
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 数据层: 一组可独立加载/卸载的 Actor 集合, 支持分层世界构建。
    /// </summary>
    public sealed class DataLayer
    {
        /// <summary>层 ID。</summary>
        public Guid Id { get; } = Guid.NewGuid();
        /// <summary>层名称。</summary>
        public string Name { get; set; }
        /// <summary>是否在编辑器中加载。</summary>
        public bool IsLoaded { get; set; } = true;
        /// <summary>是否在编辑器中可见。</summary>
        public bool IsVisible { get; set; } = true;
        /// <summary>是否可编辑。</summary>
        public bool IsEditable { get; set; } = true;
        /// <summary>层数据: Actor ID 列表。</summary>
        public readonly List<int> ActorIds = new();
        /// <summary>所属数据层组。</summary>
        public string LayerGroup { get; set; } = "Default";

        public DataLayer(string name)
        {
            Name = name;
        }
    }

    /// <summary>
    /// 数据层编辑器: 管理所有数据层的创建/加载/可见性/编辑。
    /// </summary>
    public sealed class DataLayerEditor
    {
        /// <summary>所有数据层。</summary>
        public readonly List<DataLayer> Layers = new();

        /// <summary>层数变更事件。</summary>
        public event Action OnLayersChanged;

        /// <summary>创建新数据层。</summary>
        public DataLayer CreateLayer(string name)
        {
            var layer = new DataLayer(name);
            Layers.Add(layer);
            OnLayersChanged?.Invoke();
            return layer;
        }

        /// <summary>删除数据层。</summary>
        public bool RemoveLayer(Guid id)
        {
            int idx = Layers.FindIndex(l => l.Id == id);
            if (idx < 0) return false;
            Layers.RemoveAt(idx);
            OnLayersChanged?.Invoke();
            return true;
        }

        /// <summary>设置层的加载状态。</summary>
        public void SetLayerLoaded(Guid id, bool loaded)
        {
            var layer = Layers.Find(l => l.Id == id);
            if (layer != null)
            {
                layer.IsLoaded = loaded;
                OnLayersChanged?.Invoke();
            }
        }

        /// <summary>切换层的可见性。</summary>
        public void ToggleLayerVisibility(Guid id)
        {
            var layer = Layers.Find(l => l.Id == id);
            if (layer != null)
            {
                layer.IsVisible = !layer.IsVisible;
                OnLayersChanged?.Invoke();
            }
        }

        /// <summary>添加 Actor 到指定层。</summary>
        public void AddActorToLayer(Guid layerId, int actorId)
        {
            var layer = Layers.Find(l => l.Id == layerId);
            if (layer != null && !layer.ActorIds.Contains(actorId))
            {
                layer.ActorIds.Add(actorId);
                OnLayersChanged?.Invoke();
            }
        }

        /// <summary>从层移除 Actor。</summary>
        public void RemoveActorFromLayer(Guid layerId, int actorId)
        {
            var layer = Layers.Find(l => l.Id == layerId);
            if (layer != null)
            {
                layer.ActorIds.Remove(actorId);
                OnLayersChanged?.Invoke();
            }
        }

        /// <summary>获取 Actor 所属的所有层。</summary>
        public List<DataLayer> GetLayersForActor(int actorId)
        {
            var result = new List<DataLayer>();
            foreach (var l in Layers)
                if (l.ActorIds.Contains(actorId))
                    result.Add(l);
            return result;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 世界边界管理
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 世界边界: 定义关卡世界的有效范围, 对象超出边界时警告或约束。
    /// </summary>
    public sealed class WorldBoundary
    {
        /// <summary>边界最小点。</summary>
        public Vector3 Min { get; set; } = new(-2048f, -512f, -2048f);
        /// <summary>边界最大点。</summary>
        public Vector3 Max { get; set; } = new(2048f, 512f, 2048f);
        /// <summary>是否启用软约束 (警告但不阻止)。</summary>
        public bool SoftClamp { get; set; } = true;
        /// <summary>是否启用硬约束 (阻止超出)。</summary>
        public bool HardClamp { get; set; }

        /// <summary>世界尺寸。</summary>
        public Vector3 Size => Max - Min;
        /// <summary>世界中心。</summary>
        public Vector3 Center => (Min + Max) * 0.5f;

        /// <summary>约束位置到边界内。</summary>
        public Vector3 ClampPosition(Vector3 position)
        {
            if (!HardClamp) return position;
            return Vector3.Clamp(position, Min, Max);
        }

        /// <summary>检查位置是否在边界内。</summary>
        public bool IsInside(Vector3 position)
        {
            return position.X >= Min.X && position.X <= Max.X &&
                   position.Y >= Min.Y && position.Y <= Max.Y &&
                   position.Z >= Min.Z && position.Z <= Max.Z;
        }

        /// <summary>检查位置是否超出边界 (带容差)。</summary>
        public bool IsOutside(Vector3 position, float tolerance = 0f)
        {
            return position.X < Min.X - tolerance || position.X > Max.X + tolerance ||
                   position.Y < Min.Y - tolerance || position.Y > Max.Y + tolerance ||
                   position.Z < Min.Z - tolerance || position.Z > Max.Z + tolerance;
        }

        /// <summary>构建边界线框。</summary>
        public List<(Vector3 A, Vector3 B)> BuildWireframe()
        {
            var lines = new List<(Vector3, Vector3)>();
            var c = new[]
            {
                new Vector3(Min.X, Min.Y, Min.Z), new Vector3(Max.X, Min.Y, Min.Z),
                new Vector3(Max.X, Max.Y, Min.Z), new Vector3(Min.X, Max.Y, Min.Z),
                new Vector3(Min.X, Min.Y, Max.Z), new Vector3(Max.X, Min.Y, Max.Z),
                new Vector3(Max.X, Max.Y, Max.Z), new Vector3(Min.X, Max.Y, Max.Z)
            };
            int[] edges = { 0,1, 1,2, 2,3, 3,0, 4,5, 5,6, 6,7, 7,4, 0,4, 1,5, 2,6, 3,7 };
            for (int i = 0; i < edges.Length; i += 2)
                lines.Add((c[edges[i]], c[edges[i + 1]]));
            return lines;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 世界分区编辑器 (总装)
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 世界分区编辑器: 聚合配置/可视化/体积/数据层/边界管理,
    /// 供编辑器主循环调用更新和渲染。
    /// </summary>
    public sealed class WorldStreamingGridEditor
    {
        /// <summary>配置。</summary>
        public WorldStreamingGridEditorConfig Config { get; set; } = new();
        /// <summary>单元格可视化。</summary>
        public PartitionCellVisualization CellVisualization { get; set; } = new();
        /// <summary>流式体积编辑器。</summary>
        public StreamingVolumeEditor StreamingVolumeEditor { get; } = new();
        /// <summary>加载范围调试可视化。</summary>
        public LoadRangeDebugVisualizer LoadRangeDebug { get; set; } = new();
        /// <summary>数据层编辑器。</summary>
        public DataLayerEditor DataLayerEditor { get; } = new();
        /// <summary>世界边界。</summary>
        public WorldBoundary Boundary { get; set; } = new();

        /// <summary>配置变更事件。</summary>
        public event Action OnConfigChanged;

        /// <summary>应用配置到 WorldStreamingGridSystem。</summary>
        public void ApplyConfig(World.WorldStreamingGridSystem partitionSystem)
        {
            if (partitionSystem == null) return;
            partitionSystem.SetOptions(Config.ToStreamingOptions());
            OnConfigChanged?.Invoke();
        }

        /// <summary>每帧更新。</summary>
        public void Update(float dt) { }
    }
}
