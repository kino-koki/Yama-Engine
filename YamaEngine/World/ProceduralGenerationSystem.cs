using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using YamaEngine.ECS;
using YamaEngine.Modeling;

namespace YamaEngine.World
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎程序化内容生成系统 (Yama PCG Framework)
    //
    // 设计目标: 提供一套对标 UE5 PCG (Procedural Content Generation Framework)
    // 的节点图驱动式程序化生成系统, 用于大规模场景物体散布(森林/岩石/草丛/建筑)、
    // 程序化关卡布局、生态分布与样条沿线生成。
    //
    // 架构总览:
    //   1. PCG Graph 节点图: 以有向无环图(DAG)组织生成节点, 通过拓扑排序决定
    //      执行顺序。每个节点接收输入数据(点集/图元/样条/变换)并输出变换后的数据。
    //      节点之间通过 PCGPin(引脚)连线, 引脚数据类型涵盖 Point/Primitive/
    //      Spline/Transform 四类。
    //   2. PCG Graph Executor 图执行器: 对图做分层拓扑排序, 同层节点可并行执行;
    //      支持节点结果缓存与脏标记增量更新。
    //   3. 内置节点库: PointGenerator(网格/随机/螺旋/泊松盘)、TransformPoints、
    //      BoundsModifier、Scatter(表面/体积散布)、Filter(属性/位置/法线/坡度)、
    //      Merge/Difference/Intersection(集合运算)、Attribute(属性读写)、
    //      Spline(样条生成)、MeshSampler(网格表面采样)、TerrainSampler(地形
    //      高度/法线/坡度采样)、SpawnInstance(实例化为 ECS 实体)。
    //   4. ECS 对接: PCGComponent 持有图引用与执行参数; PCGExecutionSystem 每帧
    //      检查脏标记并驱动图重算, 将结果缓存并实例化。
    //   5. 预设生成器: ForestGenerator/CityGenerator/RockDistributionGenerator/
    //      GrassFieldGenerator, 内部拼装节点图一键生成。
    //   6. 引擎集成: 通过 IPCGTerrainSampler 适配 TerrainSystem.Heightmap,
    //      通过 IPCGSpawner/ECSInstanceSpawner 将点云实例化为 ECS 实体。
    //
    // 数据流:  PCGGraph(节点图) -> PCGGraphExecutor(分层拓扑执行) ->
    //          PCGPointData(点云) -> SpawnInstanceNode(实例化) -> ECS 实体
    //
    // 可扩展性: 用户自定义节点只需继承 PCGGraphNode 并实现 Execute, 即可被图执行器
    //          调度; 也可通过 PCGNodeRegistry 注册全局类型 ID 供编辑器反射创建。
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────────────
    // 第一部分: PCG 数据类型 (Point / PointData / Primitive / Spline / Bounds)
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// PCG 引脚数据类型: 决定引脚之间是否可连接, 也决定节点期望的数据形态。
    /// 不同类型的引脚承载不同形态的程序化数据, 图执行器据此校验连线合法性。
    /// </summary>
    public enum PCGDataType
    {
        /// <summary>点集类型: 一组 PCGPoint, 是最核心的数据载体(对标 UE5 PointData)。</summary>
        Point,
        /// <summary>图元类型: 网格/材质引用, 供网格采样与表面散布使用。</summary>
        Primitive,
        /// <summary>样条线类型: 由控制点组成的样条, 供沿线采样使用。</summary>
        Spline,
        /// <summary>变换类型: 一个 4x4 矩阵, 用于对点集做整体变换。</summary>
        Transform
    }

    /// <summary>
    /// 轴对齐包围盒(AABB), 用于点集的空间裁剪、散布范围限制与视锥剔除。
    /// </summary>
    public struct PCGBounds
    {
        /// <summary>包围盒最小角。</summary>
        public Vector3 Min;
        /// <summary>包围盒最大角。</summary>
        public Vector3 Max;

        /// <summary>是否为有效包围盒(Min 各分量均小于等于 Max)。</summary>
        public readonly bool IsValid => Min.X <= Max.X && Min.Y <= Max.Y && Min.Z <= Max.Z;

        /// <summary>构造一个空(无效)包围盒, 等待 Encapsulate 扩张。</summary>
        public static PCGBounds Empty => new()
        {
            Min = new Vector3(float.MaxValue),
            Max = new Vector3(float.MinValue)
        };

        public PCGBounds(Vector3 min, Vector3 max)
        {
            Min = min;
            Max = max;
        }

        /// <summary>中心点。</summary>
        public readonly Vector3 Center => (Min + Max) * 0.5f;
        /// <summary>尺寸(各轴边长)。</summary>
        public readonly Vector3 Size => Max - Min;

        /// <summary>将一个点纳入包围盒(扩张边界)。</summary>
        public void Encapsulate(Vector3 p)
        {
            if (!IsValid) { Min = p; Max = p; return; }
            Min = Vector3.Min(Min, p);
            Max = Vector3.Max(Max, p);
        }

        /// <summary>将另一个包围盒纳入。</summary>
        public void Encapsulate(PCGBounds b)
        {
            if (!b.IsValid) return;
            Encapsulate(b.Min);
            Encapsulate(b.Max);
        }

        /// <summary>点是否在包围盒内(含边界)。</summary>
        public readonly bool Contains(Vector3 p)
        {
            return p.X >= Min.X && p.X <= Max.X &&
                   p.Y >= Min.Y && p.Y <= Max.Y &&
                   p.Z >= Min.Z && p.Z <= Max.Z;
        }

        /// <summary>按各轴范围扩展包围盒。</summary>
        public PCGBounds Expand(Vector3 amount) => new(Min - amount, Max + amount);
    }

    /// <summary>
    /// PCG 点数据: 程序化生成的最小单元。每个点携带完整变换信息(位置/旋转/缩放)、
    /// 颜色与一个属性字典, 可在地形/网格表面散布, 并最终转换为场景实体。
    /// 对标 UE5 PCG 的 FPCGPoint。
    /// 注意: 本结构体包含引用类型字段(Attributes), 故非 blittable; 属性字典作为
    /// CPU 端元数据存在, 上传 GPU 时仅拷贝前缀标量字段。
    /// </summary>
    public struct PCGPoint
    {
        /// <summary>世界空间位置。</summary>
        public Vector3 Position;
        /// <summary>旋转(四元数, 便于与 ECS TransformComponent 直接对接)。</summary>
        public Quaternion Rotation;
        /// <summary>缩放(各轴独立)。</summary>
        public Vector3 Scale;
        /// <summary>颜色属性(可用于实例着色变化或权重编码)。</summary>
        public Vector4 Color;
        /// <summary>密度值(0~1), 影响是否被过滤节点保留。</summary>
        public float Density;
        /// <summary>随机种子(用于该点关联物体的程序化变化, 如树干弯曲)。</summary>
        public uint Seed;
        /// <summary>地形坡度(度, 由 TerrainSamplerNode 写入, 供 FilterNode 使用)。</summary>
        public float Steepness;
        /// <summary>所属类别索引(供实例化器分配不同网格变体)。</summary>
        public int CategoryIndex;
        /// <summary>属性字典: 任意命名属性(float/int/Vector3/string 等)。</summary>
        public Dictionary<string, object>? Attributes;

        /// <summary>构造一个默认点(单位缩放/旋转, 满密度, 白色)。</summary>
        public static PCGPoint CreateDefault(Vector3 position)
        {
            return new PCGPoint
            {
                Position = position,
                Rotation = Quaternion.Identity,
                Scale = Vector3.One,
                Color = Vector4.One,
                Density = 1.0f,
                Seed = 0,
                Steepness = 0.0f,
                CategoryIndex = 0,
                Attributes = null
            };
        }

        /// <summary>构建该点的世界变换矩阵(缩放 -> 旋转 -> 平移)。</summary>
        public readonly Matrix4x4 ToMatrix()
        {
            return Matrix4x4.CreateScale(Scale) *
                   Matrix4x4.CreateFromQuaternion(Rotation) *
                   Matrix4x4.CreateTranslation(Position);
        }

        /// <summary>获取或新建该点的属性字典(懒初始化)。</summary>
        public Dictionary<string, object> GetAttributes()
        {
            Attributes ??= new Dictionary<string, object>();
            return Attributes;
        }

        /// <summary>写入一个属性(自动创建字典)。</summary>
        public void SetAttribute(string key, object value) => GetAttributes()[key] = value;

        /// <summary>尝试读取属性, 失败返回默认值。</summary>
        public T GetAttribute<T>(string key, T defaultValue)
        {
            if (Attributes != null && Attributes.TryGetValue(key, out var o) && o is T t) return t;
            return defaultValue;
        }

        /// <summary>是否包含某属性。</summary>
        public readonly bool HasAttribute(string key) => Attributes != null && Attributes.ContainsKey(key);

        /// <summary>浅拷贝(属性字典共享引用, 适合只读下游)。</summary>
        public PCGPoint Clone()
        {
            return new PCGPoint
            {
                Position = Position,
                Rotation = Rotation,
                Scale = Scale,
                Color = Color,
                Density = Density,
                Seed = Seed,
                Steepness = Steepness,
                CategoryIndex = CategoryIndex,
                Attributes = Attributes
            };
        }
    }

    /// <summary>属性元数据: 记录点集中出现的命名属性及其类型。</summary>
    public sealed class PCGAttributeMetadata
    {
        /// <summary>属性名。</summary>
        public string Name = string.Empty;
        /// <summary>属性运行时类型。</summary>
        public Type DataType = typeof(float);
    }

    /// <summary>
    /// PCG 点集合: 程序化生成的核心数据载体。持有点列表、轴对齐包围盒与属性元数据。
    /// 几乎所有节点都以 PCGPointData 作为输入/输出。
    /// </summary>
    public sealed class PCGPointData
    {
        /// <summary>点列表。</summary>
        public List<PCGPoint> Points = new();
        /// <summary>点集的轴对齐包围盒(由 Add/RecomputeBounds 维护)。</summary>
        public PCGBounds Bounds = PCGBounds.Empty;
        /// <summary>属性元数据(描述点集中可用的命名属性)。</summary>
        public Dictionary<string, PCGAttributeMetadata> AttributeMetadata = new();

        /// <summary>点数量。</summary>
        public int Count => Points.Count;

        /// <summary>添加一个点并自动扩张包围盒。</summary>
        public void Add(PCGPoint p)
        {
            Points.Add(p);
            Bounds.Encapsulate(p.Position);
        }

        /// <summary>添加一批点。</summary>
        public void AddRange(IEnumerable<PCGPoint> pts)
        {
            foreach (var p in pts) Add(p);
        }

        /// <summary>重新计算包围盒(外部直接修改 Points 后调用)。</summary>
        public void RecomputeBounds()
        {
            Bounds = PCGBounds.Empty;
            foreach (var p in Points) Bounds.Encapsulate(p.Position);
        }

        /// <summary>登记一个属性元数据。</summary>
        public void RegisterAttribute(string name, Type type)
        {
            if (!AttributeMetadata.ContainsKey(name))
                AttributeMetadata[name] = new PCGAttributeMetadata { Name = name, DataType = type };
        }

        /// <summary>浅拷贝(点列表深拷贝, 属性字典共享)。</summary>
        public PCGPointData Clone()
        {
            var d = new PCGPointData
            {
                Bounds = Bounds,
                AttributeMetadata = new Dictionary<string, PCGAttributeMetadata>(AttributeMetadata)
            };
            d.Points.AddRange(Points);
            return d;
        }

        /// <summary>按数量上限截断点集。</summary>
        public void TrimTo(int max)
        {
            if (max >= 0 && Points.Count > max) Points.RemoveRange(max, Points.Count - max);
        }
    }

    /// <summary>
    /// PCG 图元数据: 引用一个可采样网格与材质, 供 MeshSamplerNode/ScatterNode
    /// 在其表面采样点。直接复用建模系统的 EditableMesh。
    /// </summary>
    public sealed class PCGPrimitiveData
    {
        /// <summary>可采样网格(可为 null, 表示使用占位)。</summary>
        public EditableMesh? Mesh;
        /// <summary>网格资产 GUID(供实例化时引用)。</summary>
        public Guid MeshId;
        /// <summary>材质 GUID。</summary>
        public Guid MaterialId;
        /// <summary>图元的世界变换(将局部顶点变换到世界空间)。</summary>
        public Matrix4x4 Transform = Matrix4x4.Identity;

        /// <summary>获取世界空间顶点(应用 Transform)。</summary>
        public List<Vector3> GetWorldVertices()
        {
            var result = new List<Vector3>();
            if (Mesh == null) return result;
            foreach (var v in Mesh.Vertices) result.Add(Vector3.Transform(v, Transform));
            return result;
        }
    }

    /// <summary>样条线类型。</summary>
    public enum PCGSplineType
    {
        /// <summary>折线(分段线性)。</summary>
        Linear,
        /// <summary>贝塞尔(使用切线控制)。</summary>
        Bezier,
        /// <summary>Catmull-Rom(穿过所有控制点, 平滑)。</summary>
        CatmullRom
    }

    /// <summary>
    /// PCG 样条线数据: 由控制点与切线组成, 支持沿线均匀采样为点集。
    /// 供 SplineNode 生成、Spline 沿线散布使用。
    /// </summary>
    public sealed class PCGSplineData
    {
        /// <summary>控制点(世界空间)。</summary>
        public List<Vector3> ControlPoints = new();
        /// <summary>各控制点处的切线(贝塞尔模式使用)。</summary>
        public List<Vector3> Tangents = new();
        /// <summary>样条类型。</summary>
        public PCGSplineType Type = PCGSplineType.CatmullRom;
        /// <summary>是否闭合(首尾相连)。</summary>
        public bool Closed;

        /// <summary>在参数 t∈[0,1] 处采样一个点(沿整条样条均匀参数化)。</summary>
        public Vector3 Sample(float t)
        {
            if (ControlPoints.Count == 0) return Vector3.Zero;
            if (ControlPoints.Count == 1) return ControlPoints[0];

            int segments = Closed ? ControlPoints.Count : ControlPoints.Count - 1;
            float scaled = t * segments;
            int i = (int)MathF.Floor(scaled);
            if (i >= segments) i = segments - 1;
            float localT = scaled - i;
            int a = i;
            int b = Closed ? (i + 1) % ControlPoints.Count : Math.Min(i + 1, ControlPoints.Count - 1);

            return Type switch
            {
                PCGSplineType.Linear => Vector3.Lerp(ControlPoints[a], ControlPoints[b], localT),
                PCGSplineType.Bezier => SampleBezier(a, b, localT),
                _ => SampleCatmullRom(a, localT)
            };
        }

        private Vector3 SampleBezier(int a, int b, float t)
        {
            Vector3 p0 = ControlPoints[a];
            Vector3 p3 = ControlPoints[b];
            Vector3 p1 = a < Tangents.Count ? ControlPoints[a] + Tangents[a] : p0 + (p3 - p0) / 3f;
            Vector3 p2 = b < Tangents.Count ? ControlPoints[b] - Tangents[b] : p3 - (p3 - p0) / 3f;
            float u = 1 - t;
            return u * u * u * p0 + 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t * p3;
        }

        private Vector3 SampleCatmullRom(int i, float t)
        {
            int n = ControlPoints.Count;
            int p0 = Closed ? (i - 1 + n) % n : Math.Max(i - 1, 0);
            int p1 = i;
            int p2 = Closed ? (i + 1) % n : Math.Min(i + 1, n - 1);
            int p3 = Closed ? (i + 2) % n : Math.Min(i + 2, n - 1);
            Vector3 v0 = ControlPoints[p0], v1 = ControlPoints[p1], v2 = ControlPoints[p2], v3 = ControlPoints[p3];
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * (2 * v1 + (-v0 + v2) * t + (2 * v0 - 5 * v1 + 4 * v2 - v3) * t2 + (-v0 + 3 * v1 - 3 * v2 + v3) * t3);
        }

        /// <summary>沿样条均匀采样 count 个点, 返回 PCGPoint 列表。</summary>
        public List<PCGPoint> SamplePoints(int count)
        {
            var result = new List<PCGPoint>(count);
            if (count <= 0) return result;
            for (int i = 0; i < count; i++)
            {
                float t = count == 1 ? 0f : i / (float)(count - 1);
                var p = Sample(t);
                var pt = PCGPoint.CreateDefault(p);
                pt.Seed = (uint)(i * 2654435761u);
                result.Add(pt);
            }
            return result;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第二部分: PCG 图节点系统 (Pin / Edge / Node / Graph)
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>引脚方向。</summary>
    public enum PCGPinDirection
    {
        /// <summary>输入引脚(接收上游数据)。</summary>
        Input,
        /// <summary>输出引脚(向下游输出数据)。</summary>
        Output
    }

    /// <summary>
    /// PCG 引脚: 节点上的数据端口。输入引脚接收上游节点输出, 输出引脚持有本节点
    /// 计算结果。图执行器在调度节点前, 会把上游输出引脚的 Value 拷贝到对应输入引脚。
    /// </summary>
    public sealed class PCGPin
    {
        /// <summary>引脚唯一 ID。</summary>
        public Guid Id { get; } = Guid.NewGuid();
        /// <summary>引脚显示名(在同一节点内应唯一)。</summary>
        public string Name { get; }
        /// <summary>引脚数据类型。</summary>
        public PCGDataType DataType { get; }
        /// <summary>引脚方向。</summary>
        public PCGPinDirection Direction { get; }
        /// <summary>所属节点。</summary>
        public PCGGraphNode Owner { get; }
        /// <summary>引脚当前持有的数据(执行期由执行器/节点写入)。</summary>
        public object? Value { get; set; }
        /// <summary>是否为可选输入(无连接时节点仍可执行)。</summary>
        public bool Optional { get; set; }

        public PCGPin(string name, PCGDataType type, PCGPinDirection dir, PCGGraphNode owner)
        {
            Name = name;
            DataType = type;
            Direction = dir;
            Owner = owner;
        }
    }

    /// <summary>
    /// PCG 边: 从一个输出引脚连接到一个输入引脚的有向连线, 表示数据流向。
    /// 类型必须匹配(或目标为 Transform 兼容类型), 否则构造抛异常。
    /// </summary>
    public sealed class PCGEdge
    {
        /// <summary>边唯一 ID。</summary>
        public Guid Id { get; } = Guid.NewGuid();
        /// <summary>源输出引脚。</summary>
        public PCGPin FromPin { get; }
        /// <summary>目标输入引脚。</summary>
        public PCGPin ToPin { get; }
        /// <summary>源节点。</summary>
        public PCGGraphNode FromNode => FromPin.Owner;
        /// <summary>目标节点。</summary>
        public PCGGraphNode ToNode => ToPin.Owner;

        public PCGEdge(PCGPin from, PCGPin to)
        {
            if (from.Direction != PCGPinDirection.Output || to.Direction != PCGPinDirection.Input)
                throw new InvalidOperationException("PCG 边必须从输出引脚连到输入引脚。");
            if (from.DataType != to.DataType)
                throw new InvalidOperationException($"PCG 边类型不匹配: {from.DataType} -> {to.DataType}。");
            FromPin = from;
            ToPin = to;
        }
    }

    /// <summary>
    /// PCG 节点基接口: 任何可被图执行器调度的节点都应实现该接口。
    /// 通常通过继承 PCGGraphNode 实现, 以获得引脚/参数管理样板。
    /// </summary>
    public interface IPCGNode
    {
        /// <summary>节点唯一 ID。</summary>
        Guid Id { get; }
        /// <summary>节点显示名。</summary>
        string Name { get; set; }
        /// <summary>脏标记: 为 true 时执行结果需重算。</summary>
        bool IsDirty { get; set; }
        /// <summary>获取所有输入引脚。</summary>
        IReadOnlyList<PCGPin> GetInputPins();
        /// <summary>获取所有输出引脚。</summary>
        IReadOnlyList<PCGPin> GetOutputPins();
        /// <summary>执行节点逻辑(由图执行器在拓扑序中调用)。</summary>
        void Execute(PCGExecutionContext ctx);
    }

    /// <summary>
    /// PCG 节点抽象基类: 提供引脚管理、参数字典与输入读取/输出写入样板。
    /// 用户自定义节点只需继承本类并实现 Execute。
    /// </summary>
    public abstract class PCGGraphNode : IPCGNode
    {
        /// <summary>节点唯一 ID。</summary>
        public Guid Id { get; } = Guid.NewGuid();
        /// <summary>节点显示名。</summary>
        public string Name { get; set; }
        /// <summary>脏标记。</summary>
        public bool IsDirty { get; set; } = true;

        private readonly List<PCGPin> _inputs = new();
        private readonly List<PCGPin> _outputs = new();

        /// <summary>命名参数字典(供编辑器反射配置, 节点用 Param 读取)。</summary>
        public Dictionary<string, object> Parameters { get; } = new();

        /// <summary>输入引脚只读视图。</summary>
        public IReadOnlyList<PCGPin> InputPins => _inputs;
        /// <summary>输出引脚只读视图。</summary>
        public IReadOnlyList<PCGPin> OutputPins => _outputs;

        protected PCGGraphNode(string name) { Name = name; }

        /// <summary>获取所有输入引脚。</summary>
        public IReadOnlyList<PCGPin> GetInputPins() => _inputs;
        /// <summary>获取所有输出引脚。</summary>
        public IReadOnlyList<PCGPin> GetOutputPins() => _outputs;

        /// <summary>注册一个输入引脚。</summary>
        protected PCGPin AddInput(string name, PCGDataType type, bool optional = false)
        {
            var pin = new PCGPin(name, type, PCGPinDirection.Input, this) { Optional = optional };
            _inputs.Add(pin);
            return pin;
        }

        /// <summary>注册一个输出引脚。</summary>
        protected PCGPin AddOutput(string name, PCGDataType type)
        {
            var pin = new PCGPin(name, type, PCGPinDirection.Output, this);
            _outputs.Add(pin);
            return pin;
        }

        /// <summary>读取输入引脚数据(执行器已将上游输出拷贝到输入引脚 Value)。</summary>
        protected T? GetInput<T>(string name)
        {
            var pin = _inputs.Find(p => p.Name == name);
            if (pin == null || pin.Value == null) return default;
            return pin.Value is T t ? t : default;
        }

        /// <summary>写入输出引脚数据。</summary>
        protected void SetOutput<T>(string name, T data)
        {
            var pin = _outputs.Find(p => p.Name == name);
            if (pin != null) pin.Value = data;
        }

        /// <summary>读取命名参数, 缺省时返回默认值。</summary>
        protected T Param<T>(string key, T def)
        {
            return Parameters.TryGetValue(key, out var o) && o is T t ? t : def;
        }

        /// <summary>标记节点为脏(参数/连接变更后调用)。</summary>
        public void MarkDirty() => IsDirty = true;

        /// <summary>子类实现节点执行逻辑。</summary>
        public abstract void Execute(PCGExecutionContext ctx);
    }

    /// <summary>
    /// PCG 图: 持有节点列表与边列表, 提供拓扑排序与分层调度, 供执行器使用。
    /// 图本身也带脏标记, 连接/节点变更后置脏以触发重算。
    /// </summary>
    public sealed class PCGGraph
    {
        /// <summary>图名称。</summary>
        public string Name { get; set; } = "PCGGraph";
        /// <summary>节点列表。</summary>
        public List<IPCGNode> Nodes { get; } = new();
        /// <summary>边列表。</summary>
        public List<PCGEdge> Edges { get; } = new();
        /// <summary>图脏标记。</summary>
        public bool IsDirty { get; set; } = true;

        /// <summary>添加节点。</summary>
        public T AddNode<T>(T node) where T : IPCGNode
        {
            Nodes.Add(node);
            IsDirty = true;
            return node;
        }

        /// <summary>移除节点(同时移除关联的边)。</summary>
        public void RemoveNode(IPCGNode node)
        {
            Nodes.Remove(node);
            Edges.RemoveAll(e => e.FromNode == node || e.ToNode == node);
            IsDirty = true;
        }

        /// <summary>连接两个引脚(输出 -> 输入)。返回新建的边。</summary>
        public PCGEdge Connect(PCGPin from, PCGPin to)
        {
            // 一个输入引脚只允许一条入边: 先移除旧边
            Edges.RemoveAll(e => e.ToPin == to);
            var edge = new PCGEdge(from, to);
            Edges.Add(edge);
            IsDirty = true;
            return edge;
        }

        /// <summary>断开一条边。</summary>
        public void Disconnect(PCGEdge edge)
        {
            Edges.Remove(edge);
            IsDirty = true;
        }

        /// <summary>拓扑排序(Kahn 算法)。存在环则抛异常。</summary>
        public List<IPCGNode> TopologicalSort()
        {
            var levels = GetExecutionLevels();
            return levels.SelectMany(l => l).ToList();
        }

        /// <summary>
        /// 分层拓扑排序: 将无入边节点置于第 0 层, 移除后无入边的置于下一层, 依此类推。
        /// 同层节点之间无依赖, 可被并行执行。存在环则抛异常。
        /// </summary>
        public List<List<IPCGNode>> GetExecutionLevels()
        {
            var indeg = Nodes.ToDictionary(n => n.Id, n => 0);
            foreach (var e in Edges) indeg[e.ToNode.Id]++;

            var levels = new List<List<IPCGNode>>();
            var current = Nodes.Where(n => indeg[n.Id] == 0).ToList();

            while (current.Count > 0)
            {
                levels.Add(current);
                var next = new List<IPCGNode>();
                foreach (var n in current)
                {
                    foreach (var e in Edges.Where(x => x.FromNode == n))
                    {
                        indeg[e.ToNode.Id]--;
                        if (indeg[e.ToNode.Id] == 0) next.Add(e.ToNode);
                    }
                }
                current = next;
            }

            if (levels.Sum(l => l.Count) != Nodes.Count)
                throw new InvalidOperationException("PCG 图存在环, 无法拓扑排序。");
            return levels;
        }

        /// <summary>
        /// 获取图中(拓扑序最后一个)指定类型的输出引脚数据, 作为图的整体输出。
        /// </summary>
        public T? GetOutput<T>(PCGDataType type)
        {
            var order = TopologicalSort();
            for (int i = order.Count - 1; i >= 0; i--)
            {
                foreach (var pin in order[i].GetOutputPins())
                {
                    if (pin.DataType == type && pin.Value is T t) return t;
                }
            }
            return default;
        }

        /// <summary>查找指定名称的节点。</summary>
        public PCGGraphNode? FindNode(string name) => Nodes.OfType<PCGGraphNode>().FirstOrDefault(n => n.Name == name);
    }

    /// <summary>
    /// PCG 节点注册表: 以类型 ID 字符串注册节点工厂, 供编辑器反射创建与序列化。
    /// 内置节点在静态构造时自动注册; 用户可注册自定义节点。
    /// </summary>
    public static class PCGNodeRegistry
    {
        private static readonly Dictionary<string, Func<PCGGraphNode>> s_factories = new();

        /// <summary>注册一个节点工厂。</summary>
        public static void Register(string typeId, Func<PCGGraphNode> factory) => s_factories[typeId] = factory;

        /// <summary>按类型 ID 创建节点实例。</summary>
        public static PCGGraphNode? Create(string typeId) =>
            s_factories.TryGetValue(typeId, out var f) ? f() : null;

        /// <summary>所有已注册类型 ID。</summary>
        public static IEnumerable<string> RegisteredTypes => s_factories.Keys;

        static PCGNodeRegistry()
        {
            Register("PointGenerator", () => new PointGeneratorNode());
            Register("TransformPoints", () => new TransformPointsNode());
            Register("BoundsModifier", () => new BoundsModifierNode());
            Register("Scatter", () => new ScatterNode());
            Register("Filter", () => new FilterNode());
            Register("Merge", () => new MergeNode());
            Register("Difference", () => new DifferenceNode());
            Register("Intersection", () => new IntersectionNode());
            Register("Attribute", () => new AttributeNode());
            Register("Spline", () => new SplineNode());
            Register("MeshSampler", () => new MeshSamplerNode());
            Register("TerrainSampler", () => new TerrainSamplerNode());
            Register("SpawnInstance", () => new SpawnInstanceNode());
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第三部分: 图执行器与执行上下文
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>执行统计信息。</summary>
    public sealed class PCGExecutionStats
    {
        /// <summary>本次执行耗时(毫秒)。</summary>
        public double ElapsedMs;
        /// <summary>执行节点数。</summary>
        public int NodesExecuted;
        /// <summary>输出点数。</summary>
        public int OutputPointCount;
        /// <summary>是否启用了并行。</summary>
        public bool ParallelUsed;
    }

    /// <summary>
    /// PCG 执行上下文: 在一次图执行期内为所有节点提供共享服务与状态, 包括
    /// 地形采样器、ECS、实例化器、确定性随机数、时间与缓存。
    /// </summary>
    public sealed class PCGExecutionContext
    {
        /// <summary>ECS 实例(SpawnInstanceNode 创建实体时使用)。</summary>
        public EntityComponentSystem? ECS;
        /// <summary>地形采样器(可为 null)。</summary>
        public IPCGTerrainSampler? Terrain;
        /// <summary>实例化器(SpawnInstanceNode 调用)。</summary>
        public IPCGSpawner? Spawner;
        /// <summary>确定性随机数发生器。</summary>
        public PCGRng Random;
        /// <summary>全局种子。</summary>
        public int Seed;
        /// <summary>当前时间(用于动画化生成)。</summary>
        public float Time;
        /// <summary>是否并行执行同层节点。</summary>
        public bool Parallel;
        /// <summary>生成点数上限(节点据此裁剪)。</summary>
        public int MaxPoints = int.MaxValue;
        /// <summary>是否默认对齐地形(TerrainSamplerNode 缺省值)。</summary>
        public bool AlignToTerrain = true;
        /// <summary>执行统计。</summary>
        public PCGExecutionStats Stats = new();
        /// <summary>本次执行实例化的实体 ID 列表(并行执行下通过 AddSpawnedEntity 写入)。</summary>
        public List<int> SpawnedEntities = new();
        /// <summary>保护 SpawnedEntities 并发写入的锁(仅并行执行同层含 SpawnInstanceNode 时需要)。</summary>
        private readonly object _spawnLock = new();

        public PCGExecutionContext(int seed = 0)
        {
            Seed = seed;
            Random = new PCGRng(seed);
        }

        /// <summary>线程安全地向 SpawnedEntities 追加一个实体 ID(供 SpawnInstanceNode 在并行模式下调用)。</summary>
        public void AddSpawnedEntity(int entityId)
        {
            lock (_spawnLock) SpawnedEntities.Add(entityId);
        }
    }

    /// <summary>
    /// PCG 图执行器: 对图做分层拓扑排序, 按层顺序执行节点(同层可并行),
    /// 执行前将上游输出引脚数据拷贝到下游输入引脚, 执行后输出落在各节点输出引脚上。
    /// </summary>
    public sealed class PCGGraphExecutor
    {
        /// <summary>执行整张图。</summary>
        public PCGExecutionStats Execute(PCGGraph graph, PCGExecutionContext ctx)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ctx.Stats = new PCGExecutionStats { ParallelUsed = ctx.Parallel };

            // 1. 重置所有引脚 Value, 避免上一轮残留数据
            foreach (var n in graph.Nodes)
            {
                foreach (var p in n.GetInputPins()) p.Value = null;
                foreach (var p in n.GetOutputPins()) p.Value = null;
            }

            // 2. 分层拓扑排序
            var levels = graph.GetExecutionLevels();

            // 3. 构建 输入引脚 -> 源输出引脚 映射
            var sources = new Dictionary<Guid, PCGPin>();
            foreach (var e in graph.Edges) sources[e.ToPin.Id] = e.FromPin;

            // 4. 逐层执行
            foreach (var level in levels)
            {
                // 同层节点互不依赖, 可并行执行。共享 ctx 中的随机数(.NET 8 System.Random
                // 线程安全)与 SpawnedEntities(经 AddSpawnedEntity 加锁写入)均已做并发保护;
                // 但 ECS 本身的并发安全由 ECS 负责, 若图中含 SpawnInstanceNode 且 ECS 非线程
                // 安全, 建议关闭并行。
                if (ctx.Parallel && level.Count > 1)
                {
                    var levelCopy = level;
                    Parallel.For(0, levelCopy.Count, i =>
                    {
                        PrepareInputs(levelCopy[i], sources);
                        ExecuteNode(levelCopy[i], ctx);
                    });
                }
                else
                {
                    foreach (var node in level)
                    {
                        PrepareInputs(node, sources);
                        ExecuteNode(node, ctx);
                    }
                }
            }

            sw.Stop();
            ctx.Stats.ElapsedMs = sw.Elapsed.TotalMilliseconds;
            ctx.Stats.NodesExecuted = levels.Sum(l => l.Count);
            var result = graph.GetOutput<PCGPointData>(PCGDataType.Point);
            ctx.Stats.OutputPointCount = result?.Count ?? 0;

            graph.IsDirty = false;
            return ctx.Stats;
        }

        private static void PrepareInputs(IPCGNode node, Dictionary<Guid, PCGPin> sources)
        {
            foreach (var pin in node.GetInputPins())
            {
                if (sources.TryGetValue(pin.Id, out var src)) pin.Value = src.Value;
            }
        }

        private static void ExecuteNode(IPCGNode node, PCGExecutionContext ctx)
        {
            try
            {
                node.Execute(ctx);
                node.IsDirty = false;
            }
            catch (Exception ex)
            {
                // 单节点失败不应中断整图; 记录到控制台并继续
                Console.WriteLine($"[PCG] 节点 '{node.Name}' 执行失败: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 确定性随机数发生器(基于固定种子的 System.Random, 保证同种子可复现)。
    /// </summary>
    public sealed class PCGRng
    {
        private readonly Random _r;
        public PCGRng(int seed) { _r = new Random(seed); }

        /// <summary>[0,1) 浮点。</summary>
        public float NextFloat() => (float)_r.NextDouble();
        /// <summary>[a,b) 浮点。</summary>
        public float NextRange(float a, float b) => a + (b - a) * (float)_r.NextDouble();
        /// <summary>[0,n) 整数。</summary>
        public int NextInt(int n) => _r.Next(n);
        /// <summary>[a,b] 整数(含端点)。</summary>
        public int NextInt(int a, int b) => _r.Next(a, b + 1);

        /// <summary>单位球内随机方向(用于朝向抖动)。</summary>
        public Vector3 NextInsideUnitSphere()
        {
            float x = NextRange(-1, 1), y = NextRange(-1, 1), z = NextRange(-1, 1);
            return new Vector3(x, y, z);
        }
    }

    /// <summary>简易值噪声/fBm, 供生成器做高度变化与团块分布。</summary>
    internal static class PCGNoise
    {
        private static int Hash(int x, int y)
        {
            int h = (x * 374761393) ^ (y * 668265263);
            h = (h ^ (h >> 13)) * 1274126177;
            return h ^ (h >> 16);
        }

        private static float Fade(float t) => t * t * t * (t * (t * 6 - 15) + 10);

        /// <summary>二维值噪声 [0,1]。</summary>
        public static float ValueNoise(float x, float y)
        {
            int xi = (int)MathF.Floor(x), yi = (int)MathF.Floor(y);
            float xf = x - xi, yf = y - yi;
            float v00 = (Hash(xi, yi) & 0xFFFF) / 65535f;
            float v10 = (Hash(xi + 1, yi) & 0xFFFF) / 65535f;
            float v01 = (Hash(xi, yi + 1) & 0xFFFF) / 65535f;
            float v11 = (Hash(xi + 1, yi + 1) & 0xFFFF) / 65535f;
            float u = Fade(xf), v = Fade(yf);
            float a = v00 * (1 - u) + v10 * u;
            float b = v01 * (1 - u) + v11 * u;
            return a * (1 - v) + b * v;
        }

        /// <summary>分形布朗运动(fBm), 叠加多倍频值噪声。</summary>
        public static float Fbm(float x, float y, int octaves = 4, float persistence = 0.5f, float lacunarity = 2.0f)
        {
            float total = 0, amp = 1, freq = 1, max = 0;
            for (int i = 0; i < octaves; i++)
            {
                total += ValueNoise(x * freq, y * freq) * amp;
                max += amp;
                amp *= persistence;
                freq *= lacunarity;
            }
            return total / max;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第四部分: 地形与实例化适配
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 地形采样器接口: 解耦 PCG 与具体地形实现。TerrainSamplerNode 通过本接口
    /// 获取高度/法线/坡度, 从而可对接 TerrainSystem 或自定义高度场。
    /// </summary>
    public interface IPCGTerrainSampler
    {
        /// <summary>采样世界坐标处的高度(Y)。</summary>
        float SampleHeight(Vector3 worldPos);
        /// <summary>采样世界坐标处的法线。</summary>
        Vector3 SampleNormal(Vector3 worldPos);
        /// <summary>采样世界坐标处的坡度(度, 0=水平, 90=垂直)。</summary>
        float SampleSlope(Vector3 worldPos);
    }

    /// <summary>
    /// 基于 TerrainHeightmap 的地形采样器适配器。通过有限差分计算法线与坡度。
    /// </summary>
    public sealed class TerrainHeightmapSampler : IPCGTerrainSampler
    {
        private readonly TerrainHeightmap _heightmap;

        public TerrainHeightmapSampler(TerrainHeightmap heightmap)
        {
            _heightmap = heightmap;
        }

        /// <summary>采样高度。</summary>
        public float SampleHeight(Vector3 worldPos) => _heightmap.SampleWorldHeight(worldPos);

        /// <summary>用中心差分计算法线(step 取两个像元间距)。</summary>
        public Vector3 SampleNormal(Vector3 worldPos)
        {
            float step = _heightmap.QuadSize * 2f;
            float hL = _heightmap.SampleWorldHeight(worldPos - new Vector3(step, 0, 0));
            float hR = _heightmap.SampleWorldHeight(worldPos + new Vector3(step, 0, 0));
            float hD = _heightmap.SampleWorldHeight(worldPos - new Vector3(0, 0, step));
            float hU = _heightmap.SampleWorldHeight(worldPos + new Vector3(0, 0, step));
            Vector3 n = new(hL - hR, 2f * step, hD - hU);
            return n.LengthSquared() > 1e-8f ? Vector3.Normalize(n) : Vector3.UnitY;
        }

        /// <summary>坡度 = 法线与 +Y 的夹角(度)。</summary>
        public float SampleSlope(Vector3 worldPos)
        {
            Vector3 n = SampleNormal(worldPos);
            float d = Math.Clamp(n.Y, -1f, 1f);
            return MathF.Acos(d) * 180f / MathF.PI;
        }
    }

    /// <summary>资产变体: 实例化时按权重/属性匹配选择网格与材质。</summary>
    public sealed class PCGAssetVariant
    {
        /// <summary>变体名。</summary>
        public string Name = string.Empty;
        /// <summary>网格资产 GUID。</summary>
        public Guid MeshId;
        /// <summary>材质 GUID。</summary>
        public Guid MaterialId;
        /// <summary>权重(越大被选中概率越高)。</summary>
        public float Weight = 1f;
        /// <summary>属性匹配键(为 null 则不按属性过滤)。</summary>
        public string? AttributeKey;
        /// <summary>属性匹配值(AttributeKey 对应的期望值字符串)。</summary>
        public string? AttributeValue;
    }

    /// <summary>
    /// 实例化器接口: 将 PCG 点转为 ECS 实体或其它实例化物体。
    /// </summary>
    public interface IPCGSpawner
    {
        /// <summary>根据点生成一个实例, 返回实体 ID。</summary>
        int Spawn(PCGPoint point, PCGExecutionContext ctx);
        /// <summary>销毁一个实例。</summary>
        void Despawn(int entityId);
        /// <summary>销毁所有已生成实例。</summary>
        void DespawnAll();
    }

    /// <summary>
    /// 基于 ECS 的实例化器: 按变体权重/属性匹配选择网格, 创建带 TransformComponent
    /// 与 MeshComponent 的实体。维护已生成实体列表, 支持整体销毁。
    /// </summary>
    public sealed class ECSInstanceSpawner : IPCGSpawner
    {
        private readonly EntityComponentSystem _ecs;
        private readonly List<PCGAssetVariant> _variants;
        private readonly List<int> _spawned = new();
        private readonly PCGRng _rng;

        public ECSInstanceSpawner(EntityComponentSystem ecs, IEnumerable<PCGAssetVariant>? variants = null, int seed = 0)
        {
            _ecs = ecs;
            _variants = variants?.ToList() ?? new List<PCGAssetVariant>();
            _rng = new PCGRng(seed);
        }

        /// <summary>添加变体。</summary>
        public void AddVariant(PCGAssetVariant v) => _variants.Add(v);

        /// <summary>生成实例。</summary>
        public int Spawn(PCGPoint point, PCGExecutionContext ctx)
        {
            var variant = SelectVariant(point);
            int id = _ecs.CreateEntity("PCGInstance", point.Position, point.Rotation, point.Scale);
            _ecs.AddComponent(id, new MeshComponent
            {
                MeshId = variant?.MeshId ?? Guid.Empty,
                MaterialId = variant?.MaterialId ?? Guid.Empty,
                CastsShadow = true,
                ReceivesShadow = true,
                IsVisible = true,
                TriangleCount = 0,
                VertexCount = 0
            });
            _ecs.AddComponent(id, new TagComponent { Tag = "PCG", Layer = 0, LayerMask = 1, IsStatic = true });
            _spawned.Add(id);
            return id;
        }

        private PCGAssetVariant? SelectVariant(PCGPoint point)
        {
            if (_variants.Count == 0) return null;

            // 先按属性匹配过滤
            var candidates = _variants;
            if (point.Attributes != null)
            {
                var matched = _variants.Where(v =>
                    v.AttributeKey == null ||
                    (point.Attributes.TryGetValue(v.AttributeKey, out var val) &&
                     val?.ToString() == v.AttributeValue)).ToList();
                if (matched.Count > 0) candidates = matched;
            }

            // 按权重随机
            float total = candidates.Sum(v => MathF.Max(v.Weight, 0f));
            if (total <= 0f) return candidates[0];
            float r = _rng.NextFloat() * total;
            float acc = 0;
            foreach (var v in candidates)
            {
                acc += MathF.Max(v.Weight, 0f);
                if (r <= acc) return v;
            }
            return candidates[^1];
        }

        /// <summary>销毁实例。</summary>
        public void Despawn(int entityId)
        {
            if (_ecs.HasEntity(entityId)) _ecs.DestroyEntity(entityId);
            _spawned.Remove(entityId);
        }

        /// <summary>销毁所有实例。</summary>
        public void DespawnAll()
        {
            foreach (var id in _spawned)
                if (_ecs.HasEntity(id)) _ecs.DestroyEntity(id);
            _spawned.Clear();
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第五部分: 内置节点
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 点生成器节点: 在指定范围内生成点集。支持四种采样模式:
    /// Grid(网格)、Random(随机均匀)、Spiral(螺旋)、PoissonDisk(泊松盘蓝噪声)。
    /// </summary>
    public sealed class PointGeneratorNode : PCGGraphNode
    {
        /// <summary>采样模式。</summary>
        public enum Mode { Grid, Random, Spiral, PoissonDisk }

        public PointGeneratorNode() : base("PointGenerator")
        {
            AddOutput("Out", PCGDataType.Point);
        }

        public override void Execute(PCGExecutionContext ctx)
        {
            var mode = Param("Mode", Mode.PoissonDisk);
            float extentX = Param("ExtentX", 100f);
            float extentZ = Param("ExtentZ", 100f);
            var data = new PCGPointData();

            switch (mode)
            {
                case Mode.Grid:
                    BuildGrid(ctx, extentX, extentZ, data);
                    break;
                case Mode.Random:
                    BuildRandom(ctx, extentX, extentZ, data);
                    break;
                case Mode.Spiral:
                    BuildSpiral(ctx, extentX, extentZ, data);
                    break;
                default:
                    BuildPoisson(ctx, extentX, extentZ, data);
                    break;
            }

            data.TrimTo(ctx.MaxPoints);
            SetOutput("Out", data);
        }

        private void BuildGrid(PCGExecutionContext ctx, float ex, float ez, PCGPointData data)
        {
            float spacing = MathF.Max(Param("Spacing", 5f), 0.001f);
            float jitter = Param("Jitter", 0f);
            int nx = Math.Max(1, (int)(ex / spacing));
            int nz = Math.Max(1, (int)(ez / spacing));
            for (int z = 0; z <= nz; z++)
            {
                for (int x = 0; x <= nx; x++)
                {
                    float px = x * spacing + (jitter > 0 ? ctx.Random.NextRange(-jitter, jitter) : 0);
                    float pz = z * spacing + (jitter > 0 ? ctx.Random.NextRange(-jitter, jitter) : 0);
                    var p = PCGPoint.CreateDefault(new Vector3(px, 0, pz));
                    p.Seed = (uint)((x * 73856093) ^ (z * 19349663));
                    data.Add(p);
                }
            }
        }

        private void BuildRandom(PCGExecutionContext ctx, float ex, float ez, PCGPointData data)
        {
            int count = Math.Max(0, Param("Count", 500));
            for (int i = 0; i < count; i++)
            {
                var p = PCGPoint.CreateDefault(new Vector3(ctx.Random.NextFloat() * ex, 0, ctx.Random.NextFloat() * ez));
                p.Seed = (uint)(i * 2654435761u);
                data.Add(p);
            }
        }

        private void BuildSpiral(PCGExecutionContext ctx, float ex, float ez, PCGPointData data)
        {
            int count = Math.Max(0, Param("Count", 300));
            float turns = Param("SpiralTurns", 5f);
            float maxR = MathF.Min(ex, ez) * 0.5f;
            float cx = ex * 0.5f, cz = ez * 0.5f;
            for (int i = 0; i < count; i++)
            {
                float t = count == 1 ? 0 : (float)i / (count - 1);
                float ang = t * turns * 2f * MathF.PI;
                float r = t * maxR;
                var p = PCGPoint.CreateDefault(new Vector3(cx + MathF.Cos(ang) * r, 0, cz + MathF.Sin(ang) * r));
                p.Seed = (uint)(i * 2654435761u);
                data.Add(p);
            }
        }

        private void BuildPoisson(PCGExecutionContext ctx, float ex, float ez, PCGPointData data)
        {
            float minDist = MathF.Max(Param("PoissonRadius", 2f), 0.01f);
            var pts = PoissonDisk2D(ctx.Random, ex, ez, minDist);
            int i = 0;
            foreach (var v in pts)
            {
                var p = PCGPoint.CreateDefault(new Vector3(v.X, 0, v.Y));
                p.Seed = (uint)(i++ * 2654435761u);
                data.Add(p);
            }
        }

        /// <summary>Bridson 泊松盘采样(2D), 返回 [0,w]x[0,h] 内的蓝噪声点。</summary>
        private static List<Vector2> PoissonDisk2D(PCGRng rng, float w, float h, float minDist, int maxAttempts = 30)
        {
            var result = new List<Vector2>();
            float cellSize = minDist / MathF.Sqrt(2f);
            int gw = Math.Max(1, (int)MathF.Ceiling(w / cellSize));
            int gh = Math.Max(1, (int)MathF.Ceiling(h / cellSize));
            var grid = new Vector2?[gw * gh];
            var active = new List<Vector2>();

            Vector2 First(float ww, float hh) => new(rng.NextFloat() * ww, rng.NextFloat() * hh);
            int Idx(Vector2 p) => Math.Clamp((int)(p.X / cellSize), 0, gw - 1) + Math.Clamp((int)(p.Y / cellSize), 0, gh - 1) * gw;

            bool FarEnough(Vector2?[] g, Vector2 c)
            {
                int cx = Math.Clamp((int)(c.X / cellSize), 0, gw - 1);
                int cy = Math.Clamp((int)(c.Y / cellSize), 0, gh - 1);
                int x0 = Math.Max(0, cx - 2), x1 = Math.Min(gw - 1, cx + 2);
                int y0 = Math.Max(0, cy - 2), y1 = Math.Min(gh - 1, cy + 2);
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        var ex = g[x + y * gw];
                        if (ex.HasValue && Vector2.Distance(ex.Value, c) < minDist) return false;
                    }
                return true;
            }

            var p0 = First(w, h);
            result.Add(p0); active.Add(p0); grid[Idx(p0)] = p0;

            while (active.Count > 0)
            {
                int idx = rng.NextInt(active.Count);
                var center = active[idx];
                bool found = false;
                for (int i = 0; i < maxAttempts; i++)
                {
                    float ang = rng.NextFloat() * 2f * MathF.PI;
                    float r = minDist + rng.NextFloat() * minDist;
                    var cand = center + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * r;
                    if (cand.X < 0 || cand.Y < 0 || cand.X >= w || cand.Y >= h) continue;
                    if (FarEnough(grid, cand))
                    {
                        result.Add(cand); active.Add(cand); grid[Idx(cand)] = cand;
                        found = true;
                        break;
                    }
                }
                if (!found) active.RemoveAt(idx);
            }
            return result;
        }
    }

    /// <summary>
    /// 点变换节点: 对输入点集做旋转/缩放/偏移(可绕 Pivot)。输出变换后的点集。
    /// </summary>
    public sealed class TransformPointsNode : PCGGraphNode
    {
        public TransformPointsNode() : base("TransformPoints")
        {
            AddInput("In", PCGDataType.Point);
            AddOutput("Out", PCGDataType.Point);
        }

        public override void Execute(PCGExecutionContext ctx)
        {
            var input = GetInput<PCGPointData>("In");
            if (input == null) { SetOutput("Out", new PCGPointData()); return; }

            var offset = Param("Offset", Vector3.Zero);
            var eulerDeg = Param("Rotation", Vector3.Zero);
            var scale = Param("Scale", Vector3.One);
            var pivot = Param("Pivot", Vector3.Zero);
            float deg = MathF.PI / 180f;
            var q = Quaternion.CreateFromYawPitchRoll(eulerDeg.Y * deg, eulerDeg.X * deg, eulerDeg.Z * deg);

            var data = new PCGPointData();
            foreach (var p in input.Points)
            {
                var np = p.Clone();
                Vector3 rel = p.Position - pivot;
                np.Position = pivot + Vector3.Transform(rel, q) * scale + offset;
                np.Rotation = q * p.Rotation;
                np.Scale *= scale;
                data.Add(np);
            }
            SetOutput("Out", data);
        }
    }

    /// <summary>
    /// 边界修改节点: 重算/扩展/收缩/直接设置点集包围盒, 并输出 Bounds。
    /// 主要供下游 Scatter/Filter 作为区域参考。
    /// </summary>
    public sealed class BoundsModifierNode : PCGGraphNode
    {
        public enum ModifyMode { Recompute, Expand, Shrink, Set }

        public BoundsModifierNode() : base("BoundsModifier")
        {
            AddInput("In", PCGDataType.Point);
            AddOutput("Out", PCGDataType.Point);
            AddOutput("Bounds", PCGDataType.Transform);
        }

        public override void Execute(PCGExecutionContext ctx)
        {
            var input = GetInput<PCGPointData>("In");
            var data = input != null ? input.Clone() : new PCGPointData();
            data.RecomputeBounds();

            var mode = Param("Mode", ModifyMode.Recompute);
            var amount = Param("Amount", Vector3.Zero);
            var min = Param("Min", Vector3.Zero);
            var max = Param("Max", Vector3.One);

            switch (mode)
            {
                case ModifyMode.Expand:
                    data.Bounds = data.Bounds.Expand(amount);
                    break;
                case ModifyMode.Shrink:
                    data.Bounds = new PCGBounds(data.Bounds.Min + amount, data.Bounds.Max - amount);
                    break;
                case ModifyMode.Set:
                    data.Bounds = new PCGBounds(min, max);
                    break;
            }

            SetOutput("Out", data);
            SetOutput("Bounds", data.Bounds);
        }
    }

    /// <summary>
    /// 散布节点: 在表面(图元网格)或体积(AABB)内散布点。
    /// Surface 模式采样输入图元的三角面; Volume 模式在 Bounds/Extent 体积内随机散布。
    /// </summary>
    public sealed class ScatterNode : PCGGraphNode
    {
        public enum ScatterMode { Surface, Volume }

        public ScatterNode() : base("Scatter")
        {
            AddInput("Target", PCGDataType.Primitive, optional: true);
            AddInput("Bounds", PCGDataType.Transform, optional: true);
            AddOutput("Out", PCGDataType.Point);
        }

        public override void Execute(PCGExecutionContext ctx)
        {
            var mode = Param("Mode", ScatterMode.Volume);
            int count = Math.Max(0, Param("Count", 200));
            var data = new PCGPointData();

            if (mode == ScatterMode.Surface)
            {
                var prim = GetInput<PCGPrimitiveData>("Target");
                bool alignToSurface = Param("AlignToSurface", true);
                if (prim?.Mesh != null && prim.Mesh.Faces.Count > 0)
                    ScatterOnMesh(prim, count, ctx, data, alignToSurface);
            }
            else
            {
                // GetInput<PCGBounds> 返回 PCGBounds?(Nullable), 用 ?? 解包为非空结构体;
                // 缺省时回退到 Empty(无效包围盒), 后续再用 Extent 参数构造实际范围。
                PCGBounds bounds = GetInput<PCGBounds?>("Bounds") ?? PCGBounds.Empty;
                float ex = Param("ExtentX", 50f);
                float ez = Param("ExtentZ", 50f);
                var box = bounds.IsValid ? bounds : new PCGBounds(Vector3.Zero, new Vector3(ex, 0, ez));
                ScatterInVolume(box, count, ctx, data);
            }

            data.TrimTo(ctx.MaxPoints);
            SetOutput("Out", data);
        }

        private static void ScatterOnMesh(PCGPrimitiveData prim, int count, PCGExecutionContext ctx, PCGPointData data, bool alignToSurface)
        {
            var verts = prim.GetWorldVertices();
            // 按面积加权选择三角面
            var cum = new float[prim.Mesh!.Faces.Count];
            float total = 0;
            for (int i = 0; i < prim.Mesh.Faces.Count; i++)
            {
                var f = prim.Mesh.Faces[i];
                if (!f.IsValidFor(verts.Count)) { cum[i] = total; continue; }
                float area = 0.5f * Vector3.Cross(verts[f.B] - verts[f.A], verts[f.C] - verts[f.A]).Length();
                total += area;
                cum[i] = total;
            }
            if (total <= 0) return;
            // alignToSurface 由参数控制: true=沿表面法线对齐, false=保持默认朝向
            for (int i = 0; i < count; i++)
            {
                float r = ctx.Random.NextFloat() * total;
                int ti = Array.BinarySearch(cum, r);
                if (ti < 0) ti = ~ti;
                if (ti >= prim.Mesh.Faces.Count) ti = prim.Mesh.Faces.Count - 1;
                var f = prim.Mesh.Faces[ti];
                if (!f.IsValidFor(verts.Count)) continue;
                var p = RandomPointInTriangle(verts[f.A], verts[f.B], verts[f.C], ctx.Random);
                var pt = PCGPoint.CreateDefault(p);
                pt.Seed = (uint)(i * 2654435761u);
                pt.Rotation = alignToSurface
                    ? QuaternionFromUpToNormal(Vector3.Cross(verts[f.B] - verts[f.A], verts[f.C] - verts[f.A]))
                    : Quaternion.Identity;
                data.Add(pt);
            }
        }

        private static void ScatterInVolume(PCGBounds box, int count, PCGExecutionContext ctx, PCGPointData data)
        {
            for (int i = 0; i < count; i++)
            {
                float x = ctx.Random.NextRange(box.Min.X, box.Max.X);
                float y = ctx.Random.NextRange(box.Min.Y, box.Max.Y);
                float z = ctx.Random.NextRange(box.Min.Z, box.Max.Z);
                var p = PCGPoint.CreateDefault(new Vector3(x, y, z));
                p.Seed = (uint)(i * 2654435761u);
                data.Add(p);
            }
        }

        private static Vector3 RandomPointInTriangle(Vector3 a, Vector3 b, Vector3 c, PCGRng rng)
        {
            float r1 = MathF.Sqrt(rng.NextFloat());
            float r2 = rng.NextFloat();
            float u = 1 - r1, v = r1 * (1 - r2), w = r1 * r2;
            return a * u + b * v + c * w;
        }

        /// <summary>计算从 Y 轴(上方向)到目标法线的旋转四元数。</summary>
        private static Quaternion QuaternionFromUpToNormal(Vector3 normal)
        {
            float len = normal.Length();
            if (len < 1e-6f) return Quaternion.Identity;
            normal /= len;
            float dot = Math.Clamp(Vector3.Dot(Vector3.UnitY, normal), -1f, 1f);
            if (dot > 0.99999f) return Quaternion.Identity;
            if (dot < -0.99999f) return Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI);
            Vector3 axis = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, normal));
            float angle = MathF.Acos(dot);
            return Quaternion.CreateFromAxisAngle(axis, angle);
        }
    }

    /// <summary>
    /// 过滤节点: 按属性/位置/法线/坡度过滤点集, 输出保留点与剔除点。
    /// </summary>
    public sealed class FilterNode : PCGGraphNode
    {
        public enum FilterMode { Attribute, Position, Normal, Slope }

        public FilterNode() : base("Filter")
        {
            AddInput("In", PCGDataType.Point);
            AddOutput("Out", PCGDataType.Point);
            AddOutput("Rejected", PCGDataType.Point);
        }

        public override void Execute(PCGExecutionContext ctx)
        {
            var input = GetInput<PCGPointData>("In");
            var kept = new PCGPointData();
            var rejected = new PCGPointData();
            if (input == null) { SetOutput("Out", kept); SetOutput("Rejected", rejected); return; }

            var mode = Param("Mode", FilterMode.Slope);
            float minVal = Param("MinValue", 0f);
            float maxVal = Param("MaxValue", 1f);
            string attr = Param("Attribute", "Density");
            int axis = Param("Axis", 1); // 默认 Y
            float minA = Param("MinAxis", float.MinValue);
            float maxA = Param("MaxAxis", float.MaxValue);

            foreach (var p in input.Points)
            {
                bool keep = mode switch
                {
                    FilterMode.Attribute => InRange(p.GetAttribute(attr, 0f), minVal, maxVal),
                    FilterMode.Position => InRange(Component(p.Position, axis), minA, maxA),
                    FilterMode.Normal => InRange(Component(p.GetAttribute("Normal", Vector3.UnitY), axis), minA, maxA),
                    FilterMode.Slope => InRange(p.Steepness, minVal, maxVal),
                    _ => true
                };
                if (keep) kept.Add(p); else rejected.Add(p);
            }

            SetOutput("Out", kept);
            SetOutput("Rejected", rejected);
        }

        private static bool InRange(float v, float a, float b) => v >= a && v <= b;
        private static float Component(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
    }

    /// <summary>合并节点: 将最多 4 路输入点集合并为一个点集。</summary>
    public sealed class MergeNode : PCGGraphNode
    {
        public MergeNode() : base("Merge")
        {
            AddInput("In0", PCGDataType.Point, optional: true);
            AddInput("In1", PCGDataType.Point, optional: true);
            AddInput("In2", PCGDataType.Point, optional: true);
            AddInput("In3", PCGDataType.Point, optional: true);
            AddOutput("Out", PCGDataType.Point);
        }

        public override void Execute(PCGExecutionContext ctx)
        {
            var data = new PCGPointData();
            for (int i = 0; i < 4; i++)
            {
                var src = GetInput<PCGPointData>("In" + i);
                if (src != null) data.AddRange(src.Points);
            }
            data.RecomputeBounds();
            SetOutput("Out", data);
        }
    }

    /// <summary>差集节点: 输出 A 中与 B 任一点距离大于 Tolerance 的点。</summary>
    public sealed class DifferenceNode : PCGGraphNode
    {
        public DifferenceNode() : base("Difference")
        {
            AddInput("A", PCGDataType.Point);
            AddInput("B", PCGDataType.Point, optional: true);
            AddOutput("Out", PCGDataType.Point);
        }

        public override void Execute(PCGExecutionContext ctx)
        {
            var a = GetInput<PCGPointData>("A");
            var b = GetInput<PCGPointData>("B");
            var data = new PCGPointData();
            if (a == null) { SetOutput("Out", data); return; }
            float tol = MathF.Max(Param("Tolerance", 1f), 0f);
            float tolSq = tol * tol;

            foreach (var p in a.Points)
            {
                bool found = false;
                if (b != null)
                {
                    foreach (var q in b.Points)
                        if (Vector3.DistanceSquared(p.Position, q.Position) <= tolSq) { found = true; break; }
                }
                if (!found) data.Add(p);
            }
            SetOutput("Out", data);
        }
    }

    /// <summary>交集节点: 输出 A 中与 B 任一点距离小于 Tolerance 的点。</summary>
    public sealed class IntersectionNode : PCGGraphNode
    {
        public IntersectionNode() : base("Intersection")
        {
            AddInput("A", PCGDataType.Point);
            AddInput("B", PCGDataType.Point, optional: true);
            AddOutput("Out", PCGDataType.Point);
        }

        public override void Execute(PCGExecutionContext ctx)
        {
            var a = GetInput<PCGPointData>("A");
            var b = GetInput<PCGPointData>("B");
            var data = new PCGPointData();
            if (a == null || b == null) { SetOutput("Out", data); return; }
            float tol = MathF.Max(Param("Tolerance", 1f), 0f);
            float tolSq = tol * tol;

            foreach (var p in a.Points)
            {
                foreach (var q in b.Points)
                    if (Vector3.DistanceSquared(p.Position, q.Position) <= tolSq) { data.Add(p); break; }
            }
            SetOutput("Out", data);
        }
    }

    /// <summary>
    /// 属性节点: 对点集设置/累加/乘以/钳制一个命名属性。可用于密度、类别、缩放等。
    /// </summary>
    public sealed class AttributeNode : PCGGraphNode
    {
        public enum AttrOp { Set, Add, Multiply, Clamp }

        public AttributeNode() : base("Attribute")
        {
            AddInput("In", PCGDataType.Point);
            AddOutput("Out", PCGDataType.Point);
        }

        public override void Execute(PCGExecutionContext ctx)
        {
            var input = GetInput<PCGPointData>("In");
            var data = new PCGPointData();
            if (input == null) { SetOutput("Out", data); return; }

            var op = Param("Op", AttrOp.Set);
            string name = Param("Attribute", "Density");
            float value = Param("Value", 1f);
            float min = Param("Min", 0f);
            float max = Param("Max", 1f);
            data.AttributeMetadata[name] = new PCGAttributeMetadata { Name = name, DataType = typeof(float) };

            foreach (var p in input.Points)
            {
                var np = p.Clone();
                float cur = np.GetAttribute(name, 0f);
                float nv = op switch
                {
                    AttrOp.Set => value,
                    AttrOp.Add => cur + value,
                    AttrOp.Multiply => cur * value,
                    AttrOp.Clamp => Math.Clamp(cur, min, max),
                    _ => value
                };
                np.SetAttribute(name, nv);
                // 同步到内置字段以便下游快速访问
                if (name == "Density") np.Density = nv;
                data.Add(np);
            }
            SetOutput("Out", data);
        }
    }

    /// <summary>
    /// 样条线生成节点: 生成折线/圆/波形样条, 并沿样条采样为点集。
    /// 输出 Spline(样条数据) 与 Out(沿线采样点)。
    /// </summary>
    public sealed class SplineNode : PCGGraphNode
    {
        public enum SplineShape { Line, Circle, Wave }

        public SplineNode() : base("Spline")
        {
            AddInput("In", PCGDataType.Point, optional: true); // 可选: 以输入点作为控制点
            AddOutput("Spline", PCGDataType.Spline);
            AddOutput("Out", PCGDataType.Point);
        }

        public override void Execute(PCGExecutionContext ctx)
        {
            var spline = new PCGSplineData
            {
                Type = Param("SplineType", PCGSplineType.CatmullRom),
                Closed = Param("Closed", true)
            };

            var input = GetInput<PCGPointData>("In");
            var shape = Param("Shape", SplineShape.Circle);
            if (input != null && input.Count > 0)
            {
                foreach (var p in input.Points) spline.ControlPoints.Add(p.Position);
            }
            else
            {
                int segs = Math.Max(4, Param("Segments", 16));
                float radius = Param("Radius", 20f);
                float height = Param("Height", 0f);
                float waveAmp = Param("WaveAmplitude", 2f);
                for (int i = 0; i < segs; i++)
                {
                    float t = i / (float)segs;
                    float ang = t * 2f * MathF.PI;
                    float y = height + (shape == SplineShape.Wave ? MathF.Sin(t * 2f * MathF.PI * 2) * waveAmp : 0);
                    spline.ControlPoints.Add(new Vector3(MathF.Cos(ang) * radius, y, MathF.Sin(ang) * radius));
                }
            }

            int sampleCount = Math.Max(0, Param("SampleCount", 64));
            var pts = spline.SamplePoints(sampleCount);
            var data = new PCGPointData();
            foreach (var p in pts) data.Add(p);

            SetOutput("Spline", spline);
            SetOutput("Out", data);
        }
    }

    /// <summary>
    /// 网格采样节点: 在输入图元网格表面按面积加权采样点, 可对齐法线与随机缩放。
    /// </summary>
    public sealed class MeshSamplerNode : PCGGraphNode
    {
        public MeshSamplerNode() : base("MeshSampler")
        {
            AddInput("Mesh", PCGDataType.Primitive);
            AddOutput("Out", PCGDataType.Point);
        }

        public override void Execute(PCGExecutionContext ctx)
        {
            var prim = GetInput<PCGPrimitiveData>("Mesh");
            var data = new PCGPointData();
            if (prim?.Mesh == null || prim.Mesh.Faces.Count == 0) { SetOutput("Out", data); return; }

            int count = Math.Max(0, Param("Count", 100));
            bool alignNormal = Param("AlignToNormal", true);
            float minS = Param("MinScale", 0.8f);
            float maxS = Param("MaxScale", 1.2f);

            var verts = prim.GetWorldVertices();
            var normals = prim.Mesh.Normals;
            var faces = prim.Mesh.Faces;

            // 累积面积
            var cum = new float[faces.Count];
            float total = 0;
            for (int i = 0; i < faces.Count; i++)
            {
                if (!faces[i].IsValidFor(verts.Count)) { cum[i] = total; continue; }
                total += 0.5f * Vector3.Cross(verts[faces[i].B] - verts[faces[i].A], verts[faces[i].C] - verts[faces[i].A]).Length();
                cum[i] = total;
            }
            if (total <= 0) { SetOutput("Out", data); return; }

            for (int i = 0; i < count; i++)
            {
                float r = ctx.Random.NextFloat() * total;
                int ti = Array.BinarySearch(cum, r);
                if (ti < 0) ti = ~ti;
                if (ti >= faces.Count) ti = faces.Count - 1;
                var f = faces[ti];
                if (!f.IsValidFor(verts.Count)) continue;

                var pos = RandomPointInTriangle(verts[f.A], verts[f.B], verts[f.C], ctx.Random);
                var pt = PCGPoint.CreateDefault(pos);
                pt.Seed = (uint)(i * 2654435761u);
                pt.Scale *= ctx.Random.NextRange(minS, maxS);
                if (alignNormal && f.A < normals.Count)
                {
                    var n = Vector3.Normalize(prim.Mesh.Normals[f.A]);
                    pt.SetAttribute("Normal", n);
                }
                data.Add(pt);
            }
            SetOutput("Out", data);
        }

        private static Vector3 RandomPointInTriangle(Vector3 a, Vector3 b, Vector3 c, PCGRng rng)
        {
            float r1 = MathF.Sqrt(rng.NextFloat());
            float r2 = rng.NextFloat();
            return a * (1 - r1) + b * (r1 * (1 - r2)) + c * (r1 * r2);
        }
    }

    /// <summary>
    /// 地形采样节点: 读取每点处的地形高度/法线/坡度, 可对齐点到地表并写入属性。
    /// 这是 PCG 与 TerrainSystem 对接的核心节点。
    /// </summary>
    public sealed class TerrainSamplerNode : PCGGraphNode
    {
        public TerrainSamplerNode() : base("TerrainSampler")
        {
            AddInput("In", PCGDataType.Point);
            AddOutput("Out", PCGDataType.Point);
        }

        public override void Execute(PCGExecutionContext ctx)
        {
            var input = GetInput<PCGPointData>("In");
            var data = new PCGPointData();
            if (input == null) { SetOutput("Out", data); return; }

            var terrain = ctx.Terrain;
            bool align = Param("Align", ctx.AlignToTerrain);
            float yOffset = Param("HeightOffset", 0f);
            bool writeSlope = Param("WriteSlope", true);
            bool writeNormal = Param("WriteNormal", false);

            foreach (var p in input.Points)
            {
                var np = p.Clone();
                if (terrain != null)
                {
                    float h = terrain.SampleHeight(np.Position);
                    if (align) np.Position.Y = h + yOffset;
                    if (writeSlope)
                    {
                        np.Steepness = terrain.SampleSlope(np.Position);
                        np.SetAttribute("Slope", np.Steepness);
                    }
                    if (writeNormal)
                    {
                        var n = terrain.SampleNormal(np.Position);
                        np.SetAttribute("Normal", n);
                        // 让点朝向贴合法线(Up 对齐法线)
                        np.Rotation = AlignUp(np.Rotation, n);
                    }
                }
                data.Add(np);
            }
            SetOutput("Out", data);
        }

        /// <summary>将旋转的 +Y 轴对齐到目标法线。</summary>
        private static Quaternion AlignUp(Quaternion current, Vector3 normal)
        {
            Vector3 up = Vector3.Transform(Vector3.UnitY, current);
            if (Vector3.Dot(up, normal) > 0.9999f) return current;
            Vector3 axis = Vector3.Cross(up, normal);
            float len = axis.Length();
            if (len < 1e-5f) return current;
            axis /= len;
            float angle = MathF.Acos(Math.Clamp(Vector3.Dot(up, normal), -1f, 1f));
            var fix = Quaternion.CreateFromAxisAngle(axis, angle);
            return fix * current;
        }
    }

    /// <summary>
    /// 实例生成节点: 将输入点通过 Spawner 实例化为 ECS 实体, 并透传点集。
    /// 生成的实体 ID 收集到 ctx.SpawnedEntities。
    /// </summary>
    public sealed class SpawnInstanceNode : PCGGraphNode
    {
        public SpawnInstanceNode() : base("SpawnInstance")
        {
            AddInput("In", PCGDataType.Point);
            AddOutput("Out", PCGDataType.Point);
        }

        public override void Execute(PCGExecutionContext ctx)
        {
            var input = GetInput<PCGPointData>("In");
            var data = input != null ? input.Clone() : new PCGPointData();
            if (input == null || ctx.Spawner == null) { SetOutput("Out", data); return; }

            // "Respawn" 参数由 PCGExecutionSystem 在系统层统一处理(执行前先 Despawn 上一轮实例),
            // 此节点仅负责按当前点集生成实例, 故此处不重复销毁。读取参数以文档化其语义。
            _ = Param("Respawn", true);
            foreach (var p in input.Points)
            {
                int id = ctx.Spawner.Spawn(p, ctx);
                // 并行执行时通过加锁方法写入, 保证 SpawnedEntities 线程安全
                ctx.AddSpawnedEntity(id);
            }
            SetOutput("Out", data);
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第六部分: ECS 对接 (PCGComponent / PCGExecutionSystem)
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>PCG 执行参数。</summary>
    public struct PCGExecutionParams
    {
        /// <summary>随机种子。</summary>
        public int Seed;
        /// <summary>是否对齐地形(供节点缺省)。</summary>
        public bool AlignToTerrain;
        /// <summary>是否并行执行同层节点。</summary>
        public bool Parallel;
        /// <summary>当前时间。</summary>
        public float Time;
        /// <summary>点数上限。</summary>
        public int MaxPoints;

        public static PCGExecutionParams Default => new()
        {
            Seed = 12345,
            AlignToTerrain = true,
            Parallel = false,
            Time = 0f,
            MaxPoints = int.MaxValue
        };
    }

    /// <summary>
    /// PCG 组件: 挂在 ECS 实体上, 持有节点图引用、执行参数与缓存结果。
    /// 作为 struct IComponent 以符合 ECS 约束; 重量级状态(Graph/CachedResult/
    /// SpawnedEntities)为引用类型, 拷贝开销低。
    /// </summary>
    public struct PCGComponent : IComponent
    {
        /// <summary>节点图引用。</summary>
        public PCGGraph? Graph;
        /// <summary>执行参数。</summary>
        public PCGExecutionParams Params;
        /// <summary>脏标记: 为 true 时系统将重算图。</summary>
        public bool IsDirty;
        /// <summary>缓存的上一次执行结果(点云)。</summary>
        public PCGPointData? CachedResult;
        /// <summary>已生成的实体 ID 列表。</summary>
        public List<int>? SpawnedEntities;
    }

    /// <summary>
    /// PCG 执行系统: 每帧查询所有带 PCGComponent 的实体, 若脏标记为真则驱动
    /// PCGGraphExecutor 重算图, 缓存结果并(经 SpawnInstanceNode)实例化实体。
    /// 通过 Terrain/Spawner 属性注入地形采样器与实例化器。
    /// </summary>
    public sealed class PCGExecutionSystem : ISystem
    {
        /// <summary>系统名。</summary>
        public string Name => "PCGExecutionSystem";

        /// <summary>共享地形采样器(所有 PCG 实体共用)。</summary>
        public IPCGTerrainSampler? Terrain;

        /// <summary>共享实例化器。</summary>
        public IPCGSpawner? Spawner;

        private readonly PCGGraphExecutor _executor = new();

        public PCGExecutionSystem() { }

        /// <summary>用地形系统构造(自动包装 Heightmap 为采样器)。</summary>
        public PCGExecutionSystem(TerrainSystem terrainSystem, IPCGSpawner? spawner = null)
        {
            Terrain = new TerrainHeightmapSampler(terrainSystem.Heightmap);
            Spawner = spawner;
        }

        /// <summary>每帧更新。</summary>
        public void Update(float dt, EntityComponentSystem ecs)
        {
            var ids = ecs.QueryWithComponent<PCGComponent>();
            foreach (var id in ids)
            {
                var c = ecs.GetComponent<PCGComponent>(id);
                if (c.Graph == null) continue;
                if (!c.IsDirty && !c.Graph.IsDirty) continue;

                var p = c.Params;
                if (p.MaxPoints <= 0) p.MaxPoints = int.MaxValue;
                var ctx = new PCGExecutionContext(p.Seed)
                {
                    ECS = ecs,
                    Terrain = Terrain,
                    Spawner = Spawner,
                    Time = p.Time,
                    Parallel = p.Parallel,
                    MaxPoints = p.MaxPoints,
                    AlignToTerrain = p.AlignToTerrain
                };

                // 若要求重生成, 先销毁上一轮实例
                if (c.SpawnedEntities != null && c.SpawnedEntities.Count > 0 && Spawner != null)
                {
                    foreach (var sid in c.SpawnedEntities) Spawner.Despawn(sid);
                    c.SpawnedEntities.Clear();
                }

                try
                {
                    _executor.Execute(c.Graph, ctx);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[PCG] 实体 " + id + " 图执行失败: " + ex.Message);
                }

                // 缓存结果并回写组件, 清除脏标记
                c.CachedResult = c.Graph.GetOutput<PCGPointData>(PCGDataType.Point);
                c.SpawnedEntities = new List<int>(ctx.SpawnedEntities);
                c.IsDirty = false;
                c.Graph.IsDirty = false;
                ecs.SetComponent(id, c);
            }
        }

        /// <summary>手动标记某实体的 PCG 为脏, 下一帧重算。</summary>
        public void MarkDirty(EntityComponentSystem ecs, int entityId)
        {
            if (!ecs.HasComponent<PCGComponent>(entityId)) return;
            var c = ecs.GetComponent<PCGComponent>(entityId);
            c.IsDirty = true;
            ecs.SetComponent(entityId, c);
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第七部分: 预设生成器
    // 预设生成器内部拼装节点图并连线, 提供一键 Build()/Generate() 接口,
    // 演示如何用节点图组合出常见生态/城市场景。
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 森林生成器: 泊松盘生成树位 -> 地形对齐并写坡度 -> 坡度过滤(只留缓坡)
    /// -> 密度衰减 -> 实例化。可对接 ECSInstanceSpawner 按权重选择树种变体。
    /// </summary>
    public sealed class ForestGenerator
    {
        private readonly IPCGTerrainSampler _terrain;
        private readonly Vector2 _extent;
        private readonly float _maxSlope;

        /// <param name="terrain">地形采样器。</param>
        /// <param name="extent">生成区域大小(X=宽, Y=深)。</param>
        /// <param name="maxSlopeDeg">最大可种植坡度(度)。</param>
        public ForestGenerator(IPCGTerrainSampler terrain, Vector2 extent, float maxSlopeDeg = 30f)
        {
            _terrain = terrain; _extent = extent; _maxSlope = maxSlopeDeg;
        }

        /// <summary>构建森林生成节点图。</summary>
        public PCGGraph Build()
        {
            var graph = new PCGGraph { Name = "ForestGraph" };

            var gen = graph.AddNode(new PointGeneratorNode());
            gen.Parameters["Mode"] = PointGeneratorNode.Mode.PoissonDisk;
            gen.Parameters["ExtentX"] = _extent.X;
            gen.Parameters["ExtentZ"] = _extent.Y;
            gen.Parameters["PoissonRadius"] = 6f;

            var terrain = graph.AddNode(new TerrainSamplerNode());
            terrain.Parameters["Align"] = true;
            terrain.Parameters["WriteSlope"] = true;

            var filter = graph.AddNode(new FilterNode());
            filter.Parameters["Mode"] = FilterNode.FilterMode.Slope;
            filter.Parameters["MinValue"] = 0f;
            filter.Parameters["MaxValue"] = _maxSlope;

            var density = graph.AddNode(new AttributeNode());
            density.Parameters["Op"] = AttributeNode.AttrOp.Multiply;
            density.Parameters["Attribute"] = "Density";
            density.Parameters["Value"] = 0.9f;

            var spawn = graph.AddNode(new SpawnInstanceNode());

            graph.Connect(gen.GetOutputPins()[0], terrain.GetInputPins()[0]);
            graph.Connect(terrain.GetOutputPins()[0], filter.GetInputPins()[0]);
            graph.Connect(filter.GetOutputPins()[0], density.GetInputPins()[0]);
            graph.Connect(density.GetOutputPins()[0], spawn.GetInputPins()[0]);
            return graph;
        }

        /// <summary>一键生成并返回点云(可选实例化)。</summary>
        public PCGPointData Generate(int seed, IPCGSpawner? spawner = null, int maxPoints = 5000)
        {
            var graph = Build();
            var ctx = new PCGExecutionContext(seed)
            {
                Terrain = _terrain, Spawner = spawner, AlignToTerrain = true, MaxPoints = maxPoints
            };
            new PCGGraphExecutor().Execute(graph, ctx);
            return graph.GetOutput<PCGPointData>(PCGDataType.Point) ?? new PCGPointData();
        }
    }

    /// <summary>
    /// 城市生成器: 网格生成建筑地块 -> 地形对齐 -> 坡度过滤(只留平缓地段)
    /// -> 标记地块密度属性 -> 实例化。楼层高度变化可由自定义 Spawner 读取
    /// 点属性(如 "Height")实现。
    /// </summary>
    public sealed class CityGenerator
    {
        private readonly IPCGTerrainSampler _terrain;
        private readonly Vector2 _extent;
        private readonly float _blockSize;

        public CityGenerator(IPCGTerrainSampler terrain, Vector2 extent, float blockSize = 12f)
        {
            _terrain = terrain; _extent = extent; _blockSize = blockSize;
        }

        public PCGGraph Build()
        {
            var graph = new PCGGraph { Name = "CityGraph" };

            var gen = graph.AddNode(new PointGeneratorNode());
            gen.Parameters["Mode"] = PointGeneratorNode.Mode.Grid;
            gen.Parameters["ExtentX"] = _extent.X;
            gen.Parameters["ExtentZ"] = _extent.Y;
            gen.Parameters["Spacing"] = _blockSize;
            gen.Parameters["Jitter"] = 0.5f;

            var terrain = graph.AddNode(new TerrainSamplerNode());
            terrain.Parameters["Align"] = true;
            terrain.Parameters["WriteSlope"] = true;

            var filter = graph.AddNode(new FilterNode());
            filter.Parameters["Mode"] = FilterNode.FilterMode.Slope;
            filter.Parameters["MinValue"] = 0f;
            filter.Parameters["MaxValue"] = 15f;

            var attr = graph.AddNode(new AttributeNode());
            attr.Parameters["Op"] = AttributeNode.AttrOp.Set;
            attr.Parameters["Attribute"] = "Density";
            attr.Parameters["Value"] = 1f;

            var spawn = graph.AddNode(new SpawnInstanceNode());

            graph.Connect(gen.GetOutputPins()[0], terrain.GetInputPins()[0]);
            graph.Connect(terrain.GetOutputPins()[0], filter.GetInputPins()[0]);
            graph.Connect(filter.GetOutputPins()[0], attr.GetInputPins()[0]);
            graph.Connect(attr.GetOutputPins()[0], spawn.GetInputPins()[0]);
            return graph;
        }

        public PCGPointData Generate(int seed, IPCGSpawner? spawner = null, int maxPoints = 2000)
        {
            var graph = Build();
            var ctx = new PCGExecutionContext(seed)
            {
                Terrain = _terrain, Spawner = spawner, AlignToTerrain = true, MaxPoints = maxPoints
            };
            new PCGGraphExecutor().Execute(graph, ctx);
            return graph.GetOutput<PCGPointData>(PCGDataType.Point) ?? new PCGPointData();
        }
    }

    /// <summary>
    /// 岩石分布生成器: 随机散布 -> 地形对齐 -> 坡度过滤(只留陡坡, 岩石多在山壁)
    /// -> 实例化。
    /// </summary>
    public sealed class RockDistributionGenerator
    {
        private readonly IPCGTerrainSampler _terrain;
        private readonly Vector2 _extent;
        private readonly float _minSlope;

        public RockDistributionGenerator(IPCGTerrainSampler terrain, Vector2 extent, float minSlopeDeg = 35f)
        {
            _terrain = terrain; _extent = extent; _minSlope = minSlopeDeg;
        }

        public PCGGraph Build()
        {
            var graph = new PCGGraph { Name = "RockGraph" };

            var gen = graph.AddNode(new PointGeneratorNode());
            gen.Parameters["Mode"] = PointGeneratorNode.Mode.Random;
            gen.Parameters["ExtentX"] = _extent.X;
            gen.Parameters["ExtentZ"] = _extent.Y;
            gen.Parameters["Count"] = 800;

            var terrain = graph.AddNode(new TerrainSamplerNode());
            terrain.Parameters["Align"] = true;
            terrain.Parameters["WriteSlope"] = true;

            var filter = graph.AddNode(new FilterNode());
            filter.Parameters["Mode"] = FilterNode.FilterMode.Slope;
            filter.Parameters["MinValue"] = _minSlope;
            filter.Parameters["MaxValue"] = 90f;

            var spawn = graph.AddNode(new SpawnInstanceNode());

            graph.Connect(gen.GetOutputPins()[0], terrain.GetInputPins()[0]);
            graph.Connect(terrain.GetOutputPins()[0], filter.GetInputPins()[0]);
            graph.Connect(filter.GetOutputPins()[0], spawn.GetInputPins()[0]);
            return graph;
        }

        public PCGPointData Generate(int seed, IPCGSpawner? spawner = null, int maxPoints = 3000)
        {
            var graph = Build();
            var ctx = new PCGExecutionContext(seed)
            {
                Terrain = _terrain, Spawner = spawner, AlignToTerrain = true, MaxPoints = maxPoints
            };
            new PCGGraphExecutor().Execute(graph, ctx);
            return graph.GetOutput<PCGPointData>(PCGDataType.Point) ?? new PCGPointData();
        }
    }

    /// <summary>
    /// 草场生成器: 高密度泊松盘 -> 地形对齐 -> 坡度过滤(只留平缓草地)
    /// -> 密度衰减 -> 实例化。
    /// </summary>
    public sealed class GrassFieldGenerator
    {
        private readonly IPCGTerrainSampler _terrain;
        private readonly Vector2 _extent;
        private readonly float _maxSlope;

        public GrassFieldGenerator(IPCGTerrainSampler terrain, Vector2 extent, float maxSlopeDeg = 25f)
        {
            _terrain = terrain; _extent = extent; _maxSlope = maxSlopeDeg;
        }

        public PCGGraph Build()
        {
            var graph = new PCGGraph { Name = "GrassGraph" };

            var gen = graph.AddNode(new PointGeneratorNode());
            gen.Parameters["Mode"] = PointGeneratorNode.Mode.PoissonDisk;
            gen.Parameters["ExtentX"] = _extent.X;
            gen.Parameters["ExtentZ"] = _extent.Y;
            gen.Parameters["PoissonRadius"] = 0.8f;

            var terrain = graph.AddNode(new TerrainSamplerNode());
            terrain.Parameters["Align"] = true;
            terrain.Parameters["WriteSlope"] = true;

            var filter = graph.AddNode(new FilterNode());
            filter.Parameters["Mode"] = FilterNode.FilterMode.Slope;
            filter.Parameters["MinValue"] = 0f;
            filter.Parameters["MaxValue"] = _maxSlope;

            var density = graph.AddNode(new AttributeNode());
            density.Parameters["Op"] = AttributeNode.AttrOp.Multiply;
            density.Parameters["Attribute"] = "Density";
            density.Parameters["Value"] = 0.95f;

            var spawn = graph.AddNode(new SpawnInstanceNode());

            graph.Connect(gen.GetOutputPins()[0], terrain.GetInputPins()[0]);
            graph.Connect(terrain.GetOutputPins()[0], filter.GetInputPins()[0]);
            graph.Connect(filter.GetOutputPins()[0], density.GetInputPins()[0]);
            graph.Connect(density.GetOutputPins()[0], spawn.GetInputPins()[0]);
            return graph;
        }

        public PCGPointData Generate(int seed, IPCGSpawner? spawner = null, int maxPoints = 20000)
        {
            var graph = Build();
            var ctx = new PCGExecutionContext(seed)
            {
                Terrain = _terrain, Spawner = spawner, AlignToTerrain = true, MaxPoints = maxPoints
            };
            new PCGGraphExecutor().Execute(graph, ctx);
            return graph.GetOutput<PCGPointData>(PCGDataType.Point) ?? new PCGPointData();
        }
    }
}
