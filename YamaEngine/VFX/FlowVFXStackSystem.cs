using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using System.Runtime.CompilerServices;

namespace YamaEngine.VFX
{
    /// <summary>
    /// 3x3 矩阵（Shader/PinDataType Matrix3x3 占位类型）。只保留单位阵与 9 个分量。
    /// </summary>
    public struct Matrix3x3
    {
        public float M11, M12, M13;
        public float M21, M22, M23;
        public float M31, M32, M33;

        /// <summary>单位矩阵。</summary>
        public static readonly Matrix3x3 Identity = new Matrix3x3
        {
            M11 = 1f, M22 = 1f, M33 = 1f
        };

        public Matrix3x3(
            float m11, float m12, float m13,
            float m21, float m22, float m23,
            float m31, float m32, float m33)
        {
            M11 = m11; M12 = m12; M13 = m13;
            M21 = m21; M22 = m22; M23 = m23;
            M31 = m31; M32 = m32; M33 = m33;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // yama引擎 - FlowVFX 风格模块堆栈系统
    //
    // 类似 UE FlowVFX Stack / Unity VFX Graph:
    //   * StackEmitter: 堆栈发射器 (模块堆栈 + 参数集合)
    //   * StackModule: 堆栈模块 (输入/输出/依赖/版本)
    //   * ModuleInputPin / ModuleOutputPin: 模块引脚
    //   * ModuleDependency: 模块依赖图 (拓扑排序)
    //   * StackParameterCollection: 参数集合 (Float/Int/Bool/Vector/Color/Texture/Mesh/Enum)
    //   * StackDataInterface: 数据接口 (用户数据→模块数据桥接)
    //   * HLSLGenerator: 堆栈→HLSL 着色器代码生成 (CPU→C#, GPU→HLSL)
    //   * 参数绑定: 游戏参数→发射器参数实时绑定
    // ════════════════════════════════════════════════════════════════════════

    #region 引脚系统

    /// <summary>引脚数据类型。</summary>
    public enum PinDataType
    {
        Float,
        Int,
        Bool,
        Vector2,
        Vector3,
        Vector4,
        Color,
        Matrix3x3,
        Matrix4x4,
        Texture,
        Mesh,
        Enum,
        Object,
        Array
    }

    /// <summary>模块输入引脚。</summary>
    public class ModuleInputPin
    {
        public string Name { get; set; } = "Input";
        public string DisplayName { get; set; } = "Input";
        public PinDataType DataType { get; set; } = PinDataType.Float;
        public object DefaultValue { get; set; }
        public bool IsRequired { get; set; } = true;
        public bool IsConnected { get; private set; }
        public ModuleOutputPin ConnectedSource { get; private set; }

        /// <summary>引脚描述。</summary>
        public string Description { get; set; } = "";

        /// <summary>最小/最大值约束 (仅数值类型)。</summary>
        public float MinValue { get; set; } = float.MinValue;
        public float MaxValue { get; set; } = float.MaxValue;

        /// <summary>连接到输出引脚。</summary>
        public void Connect(ModuleOutputPin source)
        {
            ConnectedSource = source;
            IsConnected = true;
            source.AddConnection(this);
        }

        /// <summary>断开连接。</summary>
        public void Disconnect()
        {
            if (ConnectedSource != null)
            {
                ConnectedSource.RemoveConnection(this);
            }
            ConnectedSource = null;
            IsConnected = false;
        }

        /// <summary>获取当前值 (如果已连接则从源获取)。</summary>
        public object GetValue(StackParameterCollection parameters)
        {
            if (IsConnected && ConnectedSource != null)
                return ConnectedSource.GetValue(parameters);
            if (DefaultValue != null)
                return DefaultValue;
            return GetDefaultForType(DataType);
        }

        private static object GetDefaultForType(PinDataType type) => type switch
        {
            PinDataType.Float => 0f,
            PinDataType.Int => 0,
            PinDataType.Bool => false,
            PinDataType.Vector2 => Vector2.Zero,
            PinDataType.Vector3 => Vector3.Zero,
            PinDataType.Vector4 => Vector4.Zero,
            PinDataType.Color => Vector4.One,
            PinDataType.Matrix3x3 => Matrix3x3.Identity,
            PinDataType.Matrix4x4 => Matrix4x4.Identity,
            _ => null
        };
    }

    /// <summary>模块输出引脚。</summary>
    public class ModuleOutputPin
    {
        public string Name { get; set; } = "Output";
        public string DisplayName { get; set; } = "Output";
        public PinDataType DataType { get; set; } = PinDataType.Float;
        public object Value { get; set; }
        public string Description { get; set; } = "";

        private readonly List<ModuleInputPin> _connections = new List<ModuleInputPin>();
        public IReadOnlyList<ModuleInputPin> Connections => _connections;

        public void AddConnection(ModuleInputPin target) => _connections.Add(target);
        public void RemoveConnection(ModuleInputPin target) => _connections.Remove(target);

        public object GetValue(StackParameterCollection parameters)
        {
            return Value ?? GetDefaultForType(DataType);
        }

        private static object GetDefaultForType(PinDataType type) => type switch
        {
            PinDataType.Float => 0f,
            PinDataType.Int => 0,
            PinDataType.Bool => false,
            PinDataType.Vector2 => Vector2.Zero,
            PinDataType.Vector3 => Vector3.Zero,
            PinDataType.Vector4 => Vector4.Zero,
            PinDataType.Color => Vector4.One,
            _ => null
        };
    }

    #endregion

    #region 模块依赖图

    /// <summary>模块依赖关系。</summary>
    public class ModuleDependency
    {
        public StackModule Source { get; set; }
        public StackModule Target { get; set; }
        public string DependencyType { get; set; } = "Data";
        public bool IsOptional { get; set; }

        public ModuleDependency(StackModule source, StackModule target, string depType = "Data")
        {
            Source = source;
            Target = target;
            DependencyType = depType;
        }
    }

    /// <summary>模块依赖图 (有向无环图)。</summary>
    public class ModuleDependencyGraph
    {
        private readonly Dictionary<string, StackModule> _modules = new Dictionary<string, StackModule>();
        private readonly List<ModuleDependency> _dependencies = new List<ModuleDependency>();
        private readonly Dictionary<string, List<string>> _adjacency = new Dictionary<string, List<string>>();
        private readonly Dictionary<string, int> _inDegree = new Dictionary<string, int>();

        /// <summary>添加模块到图中。</summary>
        public void AddModule(StackModule module)
        {
            _modules[module.ModuleId] = module;
            if (!_adjacency.ContainsKey(module.ModuleId))
                _adjacency[module.ModuleId] = new List<string>();
            if (!_inDegree.ContainsKey(module.ModuleId))
                _inDegree[module.ModuleId] = 0;
        }

        /// <summary>添加依赖边。</summary>
        public void AddDependency(ModuleDependency dep)
        {
            _dependencies.Add(dep);
            _adjacency.TryGetValue(dep.Source.ModuleId, out var edges);
            if (edges != null)
            {
                edges.Add(dep.Target.ModuleId);
                _inDegree.TryGetValue(dep.Target.ModuleId, out int deg);
                _inDegree[dep.Target.ModuleId] = deg + 1;
            }
        }

        /// <summary>拓扑排序 (Kahn 算法)。</summary>
        public List<StackModule> TopologicalSort()
        {
            var result = new List<StackModule>();
            var queue = new Queue<string>();
            var inDeg = new Dictionary<string, int>(_inDegree);

            foreach (var kvp in inDeg)
            {
                if (kvp.Value == 0)
                    queue.Enqueue(kvp.Key);
            }

            while (queue.Count > 0)
            {
                string current = queue.Dequeue();
                if (_modules.TryGetValue(current, out var module))
                    result.Add(module);

                if (_adjacency.TryGetValue(current, out var neighbors))
                {
                    foreach (string neighbor in neighbors)
                    {
                        inDeg[neighbor]--;
                        if (inDeg[neighbor] == 0)
                            queue.Enqueue(neighbor);
                    }
                }
            }

            // 检测循环依赖
            if (result.Count != _modules.Count)
            {
                // 有循环, 将剩余模块追加到末尾
                foreach (var kvp in _modules)
                {
                    if (!result.Contains(kvp.Value))
                        result.Add(kvp.Value);
                }
            }

            return result;
        }

        /// <summary>检测是否存在循环依赖。</summary>
        public bool HasCycle()
        {
            var visited = new HashSet<string>();
            var recursionStack = new HashSet<string>();

            foreach (var module in _modules.Keys)
            {
                if (DFS(module, visited, recursionStack))
                    return true;
            }
            return false;
        }

        private bool DFS(string node, HashSet<string> visited, HashSet<string> stack)
        {
            if (stack.Contains(node)) return true;
            if (visited.Contains(node)) return false;

            visited.Add(node);
            stack.Add(node);

            if (_adjacency.TryGetValue(node, out var neighbors))
            {
                foreach (string neighbor in neighbors)
                {
                    if (DFS(neighbor, visited, stack))
                        return true;
                }
            }

            stack.Remove(node);
            return false;
        }

        /// <summary>获取模块的所有直接依赖。</summary>
        public List<StackModule> GetDependencies(string moduleId)
        {
            var deps = new List<StackModule>();
            foreach (var dep in _dependencies)
            {
                if (dep.Target.ModuleId == moduleId && _modules.TryGetValue(dep.Source.ModuleId, out var m))
                    deps.Add(m);
            }
            return deps;
        }

        /// <summary>清除。</summary>
        public void Clear()
        {
            _modules.Clear();
            _dependencies.Clear();
            _adjacency.Clear();
            _inDegree.Clear();
        }
    }

    #endregion

    #region StackParameterCollection

    /// <summary>参数类型枚举。</summary>
    public enum StackParameterType
    {
        Float,
        Int,
        Bool,
        Vector,
        Color,
        Texture,
        Mesh,
        Enum,
        String
    }

    /// <summary>堆栈参数。</summary>
    public class StackParameter
    {
        public string Name { get; set; } = "Param";
        public string DisplayName { get; set; } = "Param";
        public StackParameterType Type { get; set; }
        public object Value { get; set; }
        public object DefaultValue { get; set; }
        public string Description { get; set; } = "";
        public bool IsExposed { get; set; }
        public float MinFloat { get; set; } = float.MinValue;
        public float MaxFloat { get; set; } = float.MaxValue;
        public int MinInt { get; set; } = int.MinValue;
        public int MaxInt { get; set; } = int.MaxValue;

        public StackParameter(string name, StackParameterType type, object defaultValue)
        {
            Name = name;
            DisplayName = name;
            Type = type;
            Value = defaultValue;
            DefaultValue = defaultValue;
        }

        public T GetValue<T>()
        {
            if (Value is T t) return t;
            return default;
        }

        public void SetValue(object value)
        {
            Value = value;
        }

        public void Reset()
        {
            Value = DefaultValue;
        }
    }

    /// <summary>参数集合。</summary>
    public class StackParameterCollection
    {
        private readonly Dictionary<string, StackParameter> _parameters = new Dictionary<string, StackParameter>();
        private readonly Dictionary<string, object> _runtimeValues = new Dictionary<string, object>();

        public int Count => _parameters.Count;

        /// <summary>添加参数。</summary>
        public void AddParameter(StackParameter param)
        {
            _parameters[param.Name] = param;
        }

        /// <summary>获取参数。</summary>
        public StackParameter GetParameter(string name)
        {
            _parameters.TryGetValue(name, out var param);
            return param;
        }

        /// <summary>获取参数值。</summary>
        public T GetParameterValue<T>(string name)
        {
            if (_runtimeValues.TryGetValue(name, out var runtimeVal) && runtimeVal is T rt)
                return rt;
            if (_parameters.TryGetValue(name, out var param) && param.Value is T val)
                return val;
            return default;
        }

        /// <summary>设置参数值。</summary>
        public void SetParameterValue(string name, object value)
        {
            _runtimeValues[name] = value;
            if (_parameters.TryGetValue(name, out var param))
                param.Value = value;
        }

        /// <summary>获取所有参数。</summary>
        public IEnumerable<StackParameter> GetAllParameters() => _parameters.Values;

        /// <summary>重置所有参数到默认值。</summary>
        public void ResetAll()
        {
            foreach (var param in _parameters.Values)
                param.Reset();
            _runtimeValues.Clear();
        }

        /// <summary>复制参数集合。</summary>
        public StackParameterCollection Clone()
        {
            var clone = new StackParameterCollection();
            foreach (var kvp in _parameters)
            {
                var p = new StackParameter(kvp.Value.Name, kvp.Value.Type, kvp.Value.DefaultValue)
                {
                    DisplayName = kvp.Value.DisplayName,
                    Description = kvp.Value.Description,
                    IsExposed = kvp.Value.IsExposed,
                    Value = kvp.Value.Value,
                    MinFloat = kvp.Value.MinFloat,
                    MaxFloat = kvp.Value.MaxFloat,
                    MinInt = kvp.Value.MinInt,
                    MaxInt = kvp.Value.MaxInt
                };
                clone.AddParameter(p);
            }
            return clone;
        }
    }

    #endregion

    #region StackDataInterface

    /// <summary>
    /// 数据接口: 桥接用户数据到模块数据。
    /// 类似 UE FlowVFX Data Interface。
    /// </summary>
    public abstract class StackDataInterface
    {
        public string InterfaceName { get; protected set; } = "DataInterface";
        public bool IsBound { get; protected set; }

        /// <summary>绑定到外部数据源。</summary>
        public abstract bool Bind(object dataSource);

        /// <summary>解绑。</summary>
        public abstract void Unbind();

        /// <summary>获取数据 (按索引)。</summary>
        public abstract object GetData(int index);

        /// <summary>获取数据数量。</summary>
        public abstract int GetDataCount();

        /// <summary>接口是否有效。</summary>
        public virtual bool IsValid() => IsBound;
    }

    /// <summary>数组数据接口。</summary>
    public class ArrayDataInterface : StackDataInterface
    {
        private Array _array;

        public ArrayDataInterface()
        {
            InterfaceName = "ArrayDataInterface";
        }

        public void SetArray(Array array)
        {
            _array = array;
            IsBound = array != null;
        }

        public override bool Bind(object dataSource)
        {
            if (dataSource is Array arr)
            {
                _array = arr;
                IsBound = true;
                return true;
            }
            return false;
        }

        public override void Unbind()
        {
            _array = null;
            IsBound = false;
        }

        public override object GetData(int index)
        {
            if (_array == null || index < 0 || index >= _array.Length)
                return null;
            return _array.GetValue(index);
        }

        public override int GetDataCount() => _array?.Length ?? 0;
    }

    /// <summary>曲线数据接口。</summary>
    public class CurveDataInterface : StackDataInterface
    {
        private Dictionary<string, FloatCurve> _curves = new Dictionary<string, FloatCurve>();

        public CurveDataInterface()
        {
            InterfaceName = "CurveDataInterface";
        }

        public void AddCurve(string name, FloatCurve curve)
        {
            _curves[name] = curve;
            IsBound = true;
        }

        public float SampleCurve(string name, float time)
        {
            if (_curves.TryGetValue(name, out var curve))
                return curve.Evaluate(time);
            return 0f;
        }

        public override bool Bind(object dataSource)
        {
            if (dataSource is Dictionary<string, FloatCurve> curves)
            {
                _curves = curves;
                IsBound = true;
                return true;
            }
            return false;
        }

        public override void Unbind()
        {
            _curves.Clear();
            IsBound = false;
        }

        public override object GetData(int index) => null;
        public override int GetDataCount() => _curves.Count;
    }

    /// <summary>纹理数据接口。</summary>
    public class TextureDataInterface : StackDataInterface
    {
        private object _texture;
        private int _width;
        private int _height;

        public TextureDataInterface()
        {
            InterfaceName = "TextureDataInterface";
        }

        public void SetTexture(object texture, int width, int height)
        {
            _texture = texture;
            _width = width;
            _height = height;
            IsBound = true;
        }

        public override bool Bind(object dataSource)
        {
            _texture = dataSource;
            IsBound = dataSource != null;
            return IsBound;
        }

        public override void Unbind()
        {
            _texture = null;
            IsBound = false;
        }

        public override object GetData(int index) => _texture;
        public override int GetDataCount() => 1;
    }

    #endregion

    #region StackModule

    /// <summary>模块执行阶段。</summary>
    public enum StackModuleStage
    {
        Spawn,
        Update,
        Render,
        Event
    }

    /// <summary>模块计算目标。</summary>
    public enum ModuleComputeTarget
    {
        CPU,
        GPU,
        Hybrid
    }

    /// <summary>
    /// 堆栈模块。FlowVFX 风格的可视化模块节点。
    /// </summary>
    public class StackModule
    {
        // ── 元数据 ──
        public string ModuleId { get; } = Guid.NewGuid().ToString("N").Substring(0, 8);
        public string ModuleName { get; set; } = "StackModule";
        public string DisplayName { get; set; } = "Stack Module";
        public string Description { get; set; } = "";
        public string Category { get; set; } = "Generic";
        public int Version { get; set; } = 1;

        // ── 执行 ──
        public StackModuleStage Stage { get; set; } = StackModuleStage.Update;
        public ModuleComputeTarget ComputeTarget { get; set; } = ModuleComputeTarget.CPU;
        public int ExecutionOrder { get; set; } = 0;
        public bool Enabled { get; set; } = true;

        // ── 引脚 ──
        public readonly List<ModuleInputPin> Inputs = new List<ModuleInputPin>();
        public readonly List<ModuleOutputPin> Outputs = new List<ModuleOutputPin>();

        // ── 依赖 ──
        public readonly List<string> DependencyIds = new List<string>();

        // ── 数据接口 ──
        public Dictionary<string, StackDataInterface> DataInterfaces = new Dictionary<string, StackDataInterface>();

        /// <summary>所属发射器 (运行时设置)。</summary>
        internal StackEmitter OwnerEmitter;

        /// <summary>添加输入引脚。</summary>
        public ModuleInputPin AddInput(string name, PinDataType type, object defaultValue = null, bool required = true)
        {
            var pin = new ModuleInputPin
            {
                Name = name,
                DisplayName = name,
                DataType = type,
                DefaultValue = defaultValue,
                IsRequired = required
            };
            Inputs.Add(pin);
            return pin;
        }

        /// <summary>添加输出引脚。</summary>
        public ModuleOutputPin AddOutput(string name, PinDataType type, object value = null)
        {
            var pin = new ModuleOutputPin
            {
                Name = name,
                DisplayName = name,
                DataType = type,
                Value = value
            };
            Outputs.Add(pin);
            return pin;
        }

        /// <summary>获取输入引脚。</summary>
        public ModuleInputPin GetInput(string name)
        {
            foreach (var pin in Inputs)
                if (pin.Name == name) return pin;
            return null;
        }

        /// <summary>获取输出引脚。</summary>
        public ModuleOutputPin GetOutput(string name)
        {
            foreach (var pin in Outputs)
                if (pin.Name == name) return pin;
            return null;
        }

        /// <summary>注册数据接口。</summary>
        public void RegisterDataInterface(string name, StackDataInterface dataInterface)
        {
            DataInterfaces[name] = dataInterface;
        }

        /// <summary>模块初始化 (每系统初始化时调用一次)。</summary>
        public virtual void OnInitialize(StackParameterCollection parameters) { }

        /// <summary>模块 Spawn 阶段执行。</summary>
        public virtual void OnSpawn(ref Particle particle, StackParameterCollection parameters) { }

        /// <summary>模块 Update 阶段执行。</summary>
        public virtual void OnUpdate(ref Particle particle, float dt, StackParameterCollection parameters) { }

        /// <summary>模块 Render 阶段执行。</summary>
        public virtual void OnRender(ref Particle particle, StackParameterCollection parameters) { }

        /// <summary>模块事件执行。</summary>
        public virtual void OnEvent(string eventType, ref Particle particle, StackParameterCollection parameters) { }

        /// <summary>模块全局 Tick。</summary>
        public virtual void OnTick(float systemTime, float dt, StackParameterCollection parameters) { }

        /// <summary>重置模块。</summary>
        public virtual void OnReset() { }

        /// <summary>获取模块依赖。</summary>
        public virtual List<string> GetDependencies() => DependencyIds;
    }

    #endregion

    #region StackEmitter

    /// <summary>模块执行管线模式。</summary>
    public enum ExecutionPipelineMode
    {
        Sequential,
        DependencyOrder,
        Parallel,
        Hybrid
    }

    /// <summary>
    /// 堆栈发射器。管理模块堆栈和参数集合。
    /// 类似 UE FlowVFX Emitter + Module Stack。
    /// </summary>
    public class StackEmitter
    {
        // ── 标识 ──
        public string EmitterName { get; set; } = "StackEmitter";
        public string EmitterId { get; } = Guid.NewGuid().ToString("N").Substring(0, 8);

        // ── 模块堆栈 ──
        public readonly List<StackModule> ModuleStack = new List<StackModule>();
        public ModuleDependencyGraph DependencyGraph { get; } = new ModuleDependencyGraph();
        public ExecutionPipelineMode PipelineMode { get; set; } = ExecutionPipelineMode.DependencyOrder;

        // ── 参数 ──
        public StackParameterCollection Parameters { get; } = new StackParameterCollection();

        // ── 发射参数 ──
        public float SpawnRate { get; set; } = 10f;
        public int BurstCount { get; set; }
        public float MaxParticles { get; set; } = 10000;
        public bool IsEnabled { get; set; } = true;

        // ── 运行时 ──
        public float EmitterTime { get; private set; }
        private float _spawnAccumulator;
        private List<StackModule> _sortedModules;

        /// <summary>参数绑定映射。</summary>
        private readonly Dictionary<string, string> _paramBindings = new Dictionary<string, string>();

        public StackEmitter()
        {
            // 注册默认参数
            Parameters.AddParameter(new StackParameter("SpawnRate", StackParameterType.Float, 10f));
            Parameters.AddParameter(new StackParameter("BurstCount", StackParameterType.Int, 0));
            Parameters.AddParameter(new StackParameter("Lifetime", StackParameterType.Float, 1f));
            Parameters.AddParameter(new StackParameter("Position", StackParameterType.Vector, Vector3.Zero));
            Parameters.AddParameter(new StackParameter("Color", StackParameterType.Color, Vector4.One));
        }

        /// <summary>添加模块到堆栈。</summary>
        public void AddModule(StackModule module)
        {
            module.OwnerEmitter = this;
            ModuleStack.Add(module);
            DependencyGraph.AddModule(module);
            RebuildDependencyGraph();
        }

        /// <summary>移除模块。</summary>
        public bool RemoveModule(string moduleId)
        {
            for (int i = 0; i < ModuleStack.Count; i++)
            {
                if (ModuleStack[i].ModuleId == moduleId)
                {
                    ModuleStack.RemoveAt(i);
                    RebuildDependencyGraph();
                    return true;
                }
            }
            return false;
        }

        /// <summary>查找模块。</summary>
        public T FindModule<T>(string name) where T : StackModule
        {
            foreach (var m in ModuleStack)
            {
                if (m is T typed && (typed.ModuleName == name || typed.ModuleId == name))
                    return typed;
            }
            return null;
        }

        /// <summary>绑定参数 (游戏参数名 → 发射器参数名)。</summary>
        public void BindParameter(string gameParamName, string emitterParamName)
        {
            _paramBindings[gameParamName] = emitterParamName;
        }

        /// <summary>更新绑定参数值。</summary>
        public void UpdateBindings(Dictionary<string, object> gameParams)
        {
            foreach (var kvp in _paramBindings)
            {
                if (gameParams.TryGetValue(kvp.Key, out var value))
                {
                    Parameters.SetParameterValue(kvp.Value, value);
                }
            }
        }

        /// <summary>重建依赖图。</summary>
        private void RebuildDependencyGraph()
        {
            DependencyGraph.Clear();
            foreach (var module in ModuleStack)
            {
                DependencyGraph.AddModule(module);
            }
            foreach (var module in ModuleStack)
            {
                foreach (var depId in module.GetDependencies())
                {
                    var source = ModuleStack.Find(m => m.ModuleId == depId);
                    if (source != null)
                    {
                        DependencyGraph.AddDependency(new ModuleDependency(source, module));
                    }
                }
            }
            _sortedModules = DependencyGraph.TopologicalSort();
        }

        /// <summary>获取排序后的模块列表。</summary>
        public List<StackModule> GetSortedModules()
        {
            if (_sortedModules == null)
                RebuildDependencyGraph();
            return _sortedModules;
        }

        /// <summary>初始化发射器。</summary>
        public void Initialize()
        {
            foreach (var module in GetSortedModules())
            {
                module.OnInitialize(Parameters);
            }
        }

        /// <summary>重置发射器。</summary>
        public void Reset()
        {
            EmitterTime = 0f;
            _spawnAccumulator = 0f;
            foreach (var module in ModuleStack)
                module.OnReset();
        }

        /// <summary>发射器 Tick。</summary>
        public void Tick(float dt)
        {
            if (!IsEnabled) return;
            EmitterTime += dt;
            _spawnAccumulator += SpawnRate * dt;

            foreach (var module in GetSortedModules())
            {
                module.OnTick(EmitterTime, dt, Parameters);
            }
        }

        /// <summary>获取本帧应发射粒子数。</summary>
        public int GetSpawnCount(float dt)
        {
            _spawnAccumulator += SpawnRate * dt;
            int count = (int)_spawnAccumulator;
            if (count > 0)
                _spawnAccumulator -= count;
            return count;
        }

        /// <summary>Spawn 粒子。</summary>
        public void SpawnParticle(ref Particle particle)
        {
            float seed = Random.Shared.NextSingle();
            particle.Initialize(Parameters.GetParameterValue<float>("Lifetime"), seed);

            var sorted = GetSortedModules();
            for (int i = 0; i < sorted.Count; i++)
            {
                var module = sorted[i];
                if (!module.Enabled) continue;
                if (module.Stage == StackModuleStage.Spawn)
                    module.OnSpawn(ref particle, Parameters);
            }
        }

        /// <summary>Update 粒子。</summary>
        public void UpdateParticle(ref Particle particle, float dt)
        {
            var sorted = GetSortedModules();
            for (int i = 0; i < sorted.Count; i++)
            {
                var module = sorted[i];
                if (!module.Enabled) continue;
                if (module.Stage == StackModuleStage.Update)
                    module.OnUpdate(ref particle, dt, Parameters);
            }

            // 基础运动积分
            particle.Position += particle.Velocity * dt;
        }

        /// <summary>Render 粒子。</summary>
        public void RenderParticle(ref Particle particle)
        {
            var sorted = GetSortedModules();
            for (int i = 0; i < sorted.Count; i++)
            {
                var module = sorted[i];
                if (!module.Enabled) continue;
                if (module.Stage == StackModuleStage.Render)
                    module.OnRender(ref particle, Parameters);
            }
        }

        /// <summary>触发事件。</summary>
        public void TriggerEvent(string eventType, ref Particle particle)
        {
            var sorted = GetSortedModules();
            for (int i = 0; i < sorted.Count; i++)
            {
                var module = sorted[i];
                if (!module.Enabled) continue;
                if (module.Stage == StackModuleStage.Event)
                    module.OnEvent(eventType, ref particle, Parameters);
            }
        }
    }

    #endregion

    #region HLSL 生成器

    /// <summary>HLSL 代码生成器。将堆栈模块转换为 GPU 着色器代码。</summary>
    public class HLSLGenerator
    {
        private readonly StringBuilder _sb = new StringBuilder();
        private readonly HashSet<string> _includes = new HashSet<string>();
        private int _indentLevel;

        private void Indent() => _indentLevel++;
        private void Unindent() => _indentLevel--;
        /// <summary>写入空白行。</summary>
        private void WriteLine() => _sb.AppendLine();
        private void WriteLine(string line)
        {
            for (int i = 0; i < _indentLevel; i++)
                _sb.Append("    ");
            _sb.AppendLine(line);
        }

        /// <summary>生成完整的粒子计算着色器。</summary>
        public string GenerateComputeShader(StackEmitter emitter, string csVersion = "5_0")
        {
            _sb.Clear();
            _includes.Clear();

            // 头部
            WriteLine($"// yama引擎 - 自动生成的粒子计算着色器");
            WriteLine($"// 发射器: {emitter.EmitterName}");
            WriteLine($"// 生成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            WriteLine();

            // 常量缓冲区
            GenerateConstantBuffer(emitter);

            // 结构体
            GenerateParticleStruct();

            // RW 结构化缓冲区
            GenerateRWBuffers();

            // 工具函数
            GenerateUtilityFunctions();

            // Spawn 函数
            GenerateSpawnFunction(emitter);

            // Update 函数
            GenerateUpdateFunction(emitter);

            // 主函数
            GenerateMainCSFunction(csVersion);

            return _sb.ToString();
        }

        /// <summary>生成顶点着色器 (用于渲染)。</summary>
        public string GenerateVertexShader(StackEmitter emitter, string vsVersion = "5_0")
        {
            _sb.Clear();

            WriteLine("// yama引擎 - 自动生成的粒子顶点着色器");
            WriteLine($"// 发射器: {emitter.EmitterName}");
            WriteLine();

            GenerateConstantBuffer(emitter);
            GenerateParticleStruct();

            WriteLine("struct VS_INPUT");
            WriteLine("{");
            Indent();
            WriteLine("uint instanceID : SV_InstanceID;");
            WriteLine("uint vertexID : SV_VertexID;");
            Unindent();
            WriteLine("};");
            WriteLine();

            WriteLine("struct VS_OUTPUT");
            WriteLine("{");
            Indent();
            WriteLine("float4 position : SV_POSITION;");
            WriteLine("float2 uv : TEXCOORD0;");
            WriteLine("float4 color : COLOR0;");
            Unindent();
            WriteLine("};");
            WriteLine();

            WriteLine($"VS_OUTPUT main(VS_INPUT input)");
            WriteLine("{");
            Indent();
            WriteLine("VS_OUTPUT output;");
            WriteLine("Particle p = ParticleBuffer[input.instanceID];");
            WriteLine("float lifeRatio = p.Life / p.MaxLife;");
            WriteLine();

            // Billboard 变换
            WriteLine("// Billboard 变换");
            WriteLine("float3 right = float3(ViewMatrix[0][0], ViewMatrix[1][0], ViewMatrix[2][0]);");
            WriteLine("float3 up = float3(ViewMatrix[0][1], ViewMatrix[1][1], ViewMatrix[2][1]);");
            WriteLine("float3 pos = p.Position;");
            WriteLine("float2 offset = GetBillboardOffset(input.vertexID, p.Size);");
            WriteLine("pos += right * offset.x + up * offset.y;");
            WriteLine();

            WriteLine("output.position = mul(float4(pos, 1.0), ViewProjectionMatrix);");
            WriteLine("output.uv = GetBillboardUV(input.vertexID);");
            WriteLine("output.color = float4(p.Color.rgb, p.Color.a * p.Alpha);");
            Unindent();
            WriteLine("}");
            WriteLine();

            return _sb.ToString();
        }

        private void GenerateConstantBuffer(StackEmitter emitter)
        {
            WriteLine("cbuffer ParticleConstants : register(b0)");
            WriteLine("{");
            Indent();
            WriteLine("float4x4 ViewProjectionMatrix;");
            WriteLine("float4x4 ViewMatrix;");
            WriteLine("float3 CameraPosition;");
            WriteLine("float DeltaTime;");
            WriteLine("float SystemTime;");
            WriteLine("uint MaxParticles;");
            WriteLine("uint ActiveParticleCount;");
            WriteLine("float SpawnRate;");
            WriteLine("uint NewParticleCount;");
            Unindent();
            WriteLine("};");
            WriteLine();
        }

        private void GenerateParticleStruct()
        {
            WriteLine("struct Particle");
            WriteLine("{");
            Indent();
            WriteLine("float3 Position;");
            WriteLine("float Padding0;");
            WriteLine("float3 Velocity;");
            WriteLine("float Padding1;");
            WriteLine("float3 Acceleration;");
            WriteLine("float Padding2;");
            WriteLine("float4 Color;");
            WriteLine("float Size;");
            WriteLine("float Rotation;");
            WriteLine("float RotationSpeed;");
            WriteLine("float Alpha;");
            WriteLine("float Life;");
            WriteLine("float MaxLife;");
            WriteLine("float RandomSeed;");
            WriteLine("float Padding3;");
            WriteLine("float4 CustomData;");
            WriteLine("uint IsAlive;");
            WriteLine("uint3 Padding4;");
            Unindent();
            WriteLine("};");
            WriteLine();
        }

        private void GenerateRWBuffers()
        {
            WriteLine("RWStructuredBuffer<Particle> ParticleBuffer : register(u0);");
            WriteLine("AppendStructuredBuffer<uint> DeadList : register(u1);");
            WriteLine();
        }

        private void GenerateUtilityFunctions()
        {
            if (_includes.Add("billboard"))
            {
                WriteLine("// Billboard 工具函数");
                WriteLine("float2 GetBillboardOffset(uint vertexID, float size)");
                WriteLine("{");
                Indent();
                WriteLine("float2 offsets[4] = {");
                Indent();
                WriteLine("float2(-0.5, -0.5),");
                WriteLine("float2( 0.5, -0.5),");
                WriteLine("float2( 0.5,  0.5),");
                WriteLine("float2(-0.5,  0.5)");
                Unindent();
                WriteLine("};");
                WriteLine("return offsets[vertexID % 4] * size;");
                Unindent();
                WriteLine("}");
                WriteLine();

                WriteLine("float2 GetBillboardUV(uint vertexID)");
                WriteLine("{");
                Indent();
                WriteLine("float2 uvs[4] = {");
                Indent();
                WriteLine("float2(0, 1),");
                WriteLine("float2(1, 1),");
                WriteLine("float2(1, 0),");
                WriteLine("float2(0, 0)");
                Unindent();
                WriteLine("};");
                WriteLine("return uvs[vertexID % 4];");
                Unindent();
                WriteLine("}");
                WriteLine();
            }

            if (_includes.Add("random"))
            {
                WriteLine("// 伪随机数生成");
                WriteLine("uint pcg_hash(uint input)");
                WriteLine("{");
                Indent();
                WriteLine("uint state = input * 747796405u + 2891336453u;");
                WriteLine("uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;");
                WriteLine("return (word >> ((word >> 22u) + 4u));");
                Unindent();
                WriteLine("}");
                WriteLine();

                WriteLine("float random_float(uint seed)");
                WriteLine("{");
                Indent();
                WriteLine("return (float(pcg_hash(seed)) / 4294967295.0);");
                Unindent();
                WriteLine("}");
                WriteLine();
            }

            if (_includes.Add("curl"))
            {
                WriteLine("// 3D Curl Noise");
                WriteLine("float3 curl_noise(float3 p)");
                WriteLine("{");
                Indent();
                WriteLine("float e = 0.01;");
                WriteLine("float3 dx = float3(e, 0, 0), dy = float3(0, e, 0), dz = float3(0, 0, e);");
                WriteLine("float3 curl;");
                WriteLine("curl.x = (noise3d(p + dy).z - noise3d(p - dy).z) - (noise3d(p + dz).y - noise3d(p - dz).y);");
                WriteLine("curl.y = (noise3d(p + dz).x - noise3d(p - dz).x) - (noise3d(p + dx).z - noise3d(p - dx).z);");
                WriteLine("curl.z = (noise3d(p + dx).y - noise3d(p - dx).y) - (noise3d(p + dy).x - noise3d(p - dy).x);");
                WriteLine("return curl / (2.0 * e);");
                Unindent();
                WriteLine("}");
                WriteLine();
            }
        }

        private void GenerateSpawnFunction(StackEmitter emitter)
        {
            WriteLine("// Spawn 阶段");
            WriteLine("void SpawnParticle(inout Particle p, uint seed)");
            WriteLine("{");
            Indent();

            foreach (var module in emitter.GetSortedModules())
            {
                if (!module.Enabled || module.Stage != StackModuleStage.Spawn) continue;
                if (module.ComputeTarget == ModuleComputeTarget.GPU ||
                    module.ComputeTarget == ModuleComputeTarget.Hybrid)
                {
                    WriteLine($"// {module.ModuleName} ({module.ModuleId})");
                    GenerateModuleHLSL_Spawn(module);
                    WriteLine();
                }
            }

            Unindent();
            WriteLine("}");
            WriteLine();
        }

        private void GenerateUpdateFunction(StackEmitter emitter)
        {
            WriteLine("// Update 阶段");
            WriteLine("void UpdateParticle(inout Particle p, float dt)");
            WriteLine("{");
            Indent();

            foreach (var module in emitter.GetSortedModules())
            {
                if (!module.Enabled || module.Stage != StackModuleStage.Update) continue;
                if (module.ComputeTarget == ModuleComputeTarget.GPU ||
                    module.ComputeTarget == ModuleComputeTarget.Hybrid)
                {
                    WriteLine($"// {module.ModuleName} ({module.ModuleId})");
                    GenerateModuleHLSL_Update(module);
                    WriteLine();
                }
            }

            // 基础积分
            WriteLine("// 基础运动积分");
            WriteLine("p.Position += p.Velocity * dt;");
            WriteLine("p.Life -= dt;");
            WriteLine("if (p.Life <= 0.0) p.IsAlive = 0;");

            Unindent();
            WriteLine("}");
            WriteLine();
        }

        private void GenerateModuleHLSL_Spawn(StackModule module)
        {
            // 根据模块名生成对应的 HLSL
            switch (module.ModuleName)
            {
                case "Lifetime":
                    WriteLine("float lifetime = Parameters_Lifetime;");
                    WriteLine("p.MaxLife = lifetime;");
                    WriteLine("p.Life = lifetime;");
                    break;
                case "Velocity":
                    WriteLine("float speed = Parameters_Speed;");
                    WriteLine("float coneAngle = Parameters_ConeAngle;");
                    WriteLine("float theta = random_float(seed) * 3.14159265 * 2.0;");
                    WriteLine("float phi = acos(2.0 * random_float(seed + 1) - 1.0) * coneAngle;");
                    WriteLine("p.Velocity = float3(sin(phi)*cos(theta), cos(phi), sin(phi)*sin(theta)) * speed;");
                    break;
                case "Size":
                    WriteLine("p.Size = Parameters_Size;");
                    break;
                case "Color":
                    WriteLine("p.Color = Parameters_Color;");
                    break;
                default:
                    WriteLine($"// TODO: 生成 {module.ModuleName} 的 HLSL Spawn 代码");
                    break;
            }
        }

        private void GenerateModuleHLSL_Update(StackModule module)
        {
            switch (module.ModuleName)
            {
                case "Gravity":
                    WriteLine("p.Velocity += float3(0, Parameters_GravityY, 0) * dt;");
                    break;
                case "Drag":
                    WriteLine("float speed = length(p.Velocity);");
                    WriteLine("float drag = Parameters_DragCoeff;");
                    WriteLine("p.Velocity *= max(0.0, 1.0 - drag * dt);");
                    break;
                case "Turbulence":
                    WriteLine("float3 turb = curl_noise(p.Position * Parameters_TurbFreq);");
                    WriteLine("p.Velocity += turb * Parameters_TurbStrength * dt;");
                    break;
                case "Noise":
                    WriteLine("p.Velocity += float3(");
                    WriteLine("    sin(p.RandomSeed * 100 + SystemTime),");
                    WriteLine("    sin(p.RandomSeed * 200 + SystemTime),");
                    WriteLine("    sin(p.RandomSeed * 300 + SystemTime)");
                    WriteLine(") * Parameters_NoiseStrength * dt;");
                    break;
                case "Force":
                    WriteLine("float3 toCenter = Parameters_ForcePosition - p.Position;");
                    WriteLine("float dist = length(toCenter);");
                    WriteLine("if (dist > 0.001 && dist < Parameters_ForceRadius) {");
                    Indent();
                    WriteLine("float falloff = 1.0 - dist / Parameters_ForceRadius;");
                    WriteLine("p.Velocity += normalize(-toCenter) * Parameters_ForceStrength * falloff * dt;");
                    Unindent();
                    WriteLine("}");
                    break;
                case "Alpha":
                    WriteLine("float lifeRatio = p.Life / p.MaxLife;");
                    WriteLine("p.Alpha = Parameters_FadeOut ? lifeRatio / Parameters_FadeOutDuration : 1.0;");
                    break;
                default:
                    WriteLine($"// TODO: 生成 {module.ModuleName} 的 HLSL Update 代码");
                    break;
            }
        }

        private void GenerateMainCSFunction(string csVersion)
        {
            WriteLine($"[numthreads(256, 1, 1)]");
            WriteLine($"void CSMain(uint3 groupID : SV_GroupID, uint groupIndex : SV_GroupIndex, uint3 dispatchThreadID : SV_DispatchThreadID)");
            WriteLine("{");
            Indent();
            WriteLine("uint idx = dispatchThreadID.x;");
            WriteLine("if (idx >= MaxParticles) return;");
            WriteLine();

            // Spawn 路径
            WriteLine("// Spawn 新粒子");
            WriteLine("if (idx < NewParticleCount) {");
            Indent();
            WriteLine("uint seed = pcg_hash(idx + (uint)(SystemTime * 1000));");
            WriteLine("Particle p;");
            WriteLine("p.IsAlive = 1;");
            WriteLine("p.RandomSeed = random_float(seed);");
            WriteLine("SpawnParticle(p, seed);");
            WriteLine("ParticleBuffer[idx] = p;");
            Unindent();
            WriteLine("}");
            WriteLine();

            // Update 路径
            WriteLine("// Update 现有粒子");
            WriteLine("else if (idx < ActiveParticleCount) {");
            Indent();
            WriteLine("Particle p = ParticleBuffer[idx];");
            WriteLine("if (p.IsAlive) {");
            Indent();
            WriteLine("UpdateParticle(p, DeltaTime);");
            WriteLine("ParticleBuffer[idx] = p;");
            Unindent();
            WriteLine("}");
            Unindent();
            WriteLine("}");

            Unindent();
            WriteLine("}");
        }

        /// <summary>生成参数常量缓冲区 (为特定模块)。</summary>
        public string GenerateParameterBuffer(StackParameterCollection parameters)
        {
            _sb.Clear();
            WriteLine("cbuffer ModuleParameters : register(b1)");
            WriteLine("{");
            Indent();

            foreach (var param in parameters.GetAllParameters())
            {
                string hlslType = param.Type switch
                {
                    StackParameterType.Float => "float",
                    StackParameterType.Int => "int",
                    StackParameterType.Bool => "bool",
                    StackParameterType.Vector => "float3",
                    StackParameterType.Color => "float4",
                    _ => "float"
                };
                WriteLine($"{hlslType} Parameters_{param.Name};");
            }

            Unindent();
            WriteLine("};");

            return _sb.ToString();
        }
    }

    #endregion

    #region FlowVFXStackSystem (系统级管理)

    /// <summary>
    /// FlowVFX 风格堆栈系统管理器。
    /// 管理所有 StackEmitter 和全局参数绑定。
    /// </summary>
    public class FlowVFXStackSystem
    {
        private readonly List<StackEmitter> _emitters = new List<StackEmitter>();
        private readonly Dictionary<string, object> _globalParameters = new Dictionary<string, object>();
        private readonly HLSLGenerator _hlslGenerator = new HLSLGenerator();

        public bool IsInitialized { get; private set; }
        public int ActiveEmitterCount => _emitters.Count;

        /// <summary>创建新发射器。</summary>
        public StackEmitter CreateEmitter(string name = "NewEmitter")
        {
            var emitter = new StackEmitter { EmitterName = name };
            _emitters.Add(emitter);
            return emitter;
        }

        /// <summary>移除发射器。</summary>
        public bool RemoveEmitter(string emitterId)
        {
            for (int i = 0; i < _emitters.Count; i++)
            {
                if (_emitters[i].EmitterId == emitterId)
                {
                    _emitters.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        /// <summary>获取发射器。</summary>
        public StackEmitter GetEmitter(string emitterId)
        {
            foreach (var e in _emitters)
                if (e.EmitterId == emitterId) return e;
            return null;
        }

        /// <summary>设置全局参数。</summary>
        public void SetGlobalParameter(string name, object value)
        {
            _globalParameters[name] = value;
        }

        /// <summary>更新所有发射器的参数绑定。</summary>
        public void UpdateAllBindings()
        {
            foreach (var emitter in _emitters)
            {
                emitter.UpdateBindings(_globalParameters);
            }
        }

        /// <summary>初始化系统。</summary>
        public void Initialize()
        {
            foreach (var emitter in _emitters)
                emitter.Initialize();
            IsInitialized = true;
        }

        /// <summary>重置系统。</summary>
        public void Reset()
        {
            foreach (var emitter in _emitters)
                emitter.Reset();
        }

        /// <summary>为发射器生成 GPU 计算着色器。</summary>
        public string GenerateComputeShaderForEmitter(string emitterId)
        {
            var emitter = GetEmitter(emitterId);
            if (emitter == null) return null;
            return _hlslGenerator.GenerateComputeShader(emitter);
        }

        /// <summary>为发射器生成 GPU 顶点着色器。</summary>
        public string GenerateVertexShaderForEmitter(string emitterId)
        {
            var emitter = GetEmitter(emitterId);
            if (emitter == null) return null;
            return _hlslGenerator.GenerateVertexShader(emitter);
        }

        /// <summary>系统 Tick。</summary>
        public void Tick(float dt)
        {
            UpdateAllBindings();
            foreach (var emitter in _emitters)
                emitter.Tick(dt);
        }
    }

    #endregion
}
