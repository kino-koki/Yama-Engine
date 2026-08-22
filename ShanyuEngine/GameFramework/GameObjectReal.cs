using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace ShanyuEngine.GameFramework
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - 真实游戏对象系统 (GameObjectReal)
    //
    //   * GameObjectReal: 游戏对象（Name/Tag/Layer/IsActive/Transform/Parent/Children/Components/Scene）
    //   * TransformReal: 变换组件（Position/Rotation/Scale/矩阵计算/脏标记/LookAt/Translate/Rotate）
    //   * ComponentReal: 组件基类（生命周期/Awake→Start→OnEnable→Update→OnDisable→OnDestroy）
    //   * 层级管理（SetParent/GetChild/GetSiblingIndex/SetSiblingIndex）
    // ════════════════════════════════════════════════════════════════════════

    // ════════════════════════════════════════════════════════════════════════
    // ComponentReal: 组件基类
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 组件唯一性属性：标记组件是否允许多个实例。
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public sealed class AllowMultipleAttribute : Attribute { }

    /// <summary>
    /// 组件基类。提供生命周期管理和GameObject引用。
    /// 生命周期: Awake -> Start -> OnEnable -> Update -> OnDisable -> OnDestroy
    /// </summary>
    public class ComponentReal
    {
        /// <summary>所属GameObject。</summary>
        public GameObjectReal GameObject { get; internal set; }

        /// <summary>组件是否启用。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>组件标签。</summary>
        public string Tag { get; set; } = string.Empty;

        /// <summary>组件是否已初始化（Awake已调用）。</summary>
        public bool IsInitialized { get; private set; }

        /// <summary>组件是否已启动（Start已调用）。</summary>
        public bool IsStarted { get; private set; }

        /// <summary>
        /// 当组件被创建时调用（仅一次）。
        /// </summary>
        public virtual void Awake() { IsInitialized = true; }

        /// <summary>
        /// 当组件第一次启用时调用（仅一次，在OnEnable之前）。
        /// </summary>
        public virtual void Start() { IsStarted = true; }

        /// <summary>
        /// 当组件启用时调用。
        /// </summary>
        public virtual void OnEnable() { }

        /// <summary>
        /// 每帧更新。
        /// </summary>
        public virtual void Update(float dt) { }

        /// <summary>
        /// 当组件禁用时调用。
        /// </summary>
        public virtual void OnDisable() { }

        /// <summary>
        /// 当组件销毁时调用。
        /// </summary>
        public virtual void OnDestroy() { }

        /// <summary>内部标记为已初始化。</summary>
        internal void MarkInitialized() { IsInitialized = true; }

        /// <summary>内部标记为已启动。</summary>
        internal void MarkStarted() { IsStarted = true; }
    }

    // ════════════════════════════════════════════════════════════════════════
    // TransformReal: 变换组件
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 变换脏标记枚举。
    /// </summary>
    [Flags]
    internal enum TransformDirtyFlags
    {
        Clean = 0,
        Position = 1,
        Rotation = 2,
        Scale = 4,
        All = Position | Rotation | Scale
    }

    /// <summary>
    /// 真实变换组件：支持层级变换、脏标记惰性矩阵重建、空间转换。
    /// </summary>
    public sealed class TransformReal
    {
        // 本地空间属性
        private Vector3 _localPosition;
        private Quaternion _localRotation;
        private Vector3 _localScale;

        // 缓存的世界矩阵
        private Matrix4x4 _localToWorldMatrix;
        private Matrix4x4 _worldToLocalMatrix;

        // 脏标记
        private TransformDirtyFlags _dirtyFlags = TransformDirtyFlags.All;

        // 父子关系
        private TransformReal _parent;
        private readonly List<TransformReal> _children = new();

        /// <summary>所属GameObject。</summary>
        public GameObjectReal GameObject { get; internal set; }

        /// <summary>本地位置。</summary>
        public Vector3 LocalPosition
        {
            get => _localPosition;
            set
            {
                _localPosition = value;
                MarkDirty(TransformDirtyFlags.Position);
            }
        }

        /// <summary>本地旋转（四元数）。</summary>
        public Quaternion LocalRotation
        {
            get => _localRotation;
            set
            {
                _localRotation = (value.W != 0 || value.X != 0 || value.Y != 0 || value.Z != 0)
                    ? Quaternion.Normalize(value)
                    : Quaternion.Identity;
                MarkDirty(TransformDirtyFlags.Rotation);
            }
        }

        /// <summary>本地缩放。</summary>
        public Vector3 LocalScale
        {
            get => _localScale;
            set
            {
                _localScale = value;
                MarkDirty(TransformDirtyFlags.Scale);
            }
        }

        /// <summary>世界位置。</summary>
        public Vector3 Position
        {
            get
            {
                EnsureMatrixUpdated();
                // 从矩阵提取位置
                return new Vector3(_localToWorldMatrix.M41, _localToWorldMatrix.M42, _localToWorldMatrix.M43);
            }
            set
            {
                if (_parent != null)
                {
                    // 转换为父空间的本地坐标
                    _localPosition = Vector3.Transform(value - _parent.Position,
                        Quaternion.Inverse(_parent.WorldRotation));
                }
                else
                {
                    _localPosition = value;
                }
                MarkDirty(TransformDirtyFlags.Position);
            }
        }

        /// <summary>世界旋转。</summary>
        public Quaternion WorldRotation
        {
            get
            {
                EnsureMatrixUpdated();
                // 从矩阵提取旋转
                return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(_localToWorldMatrix));
            }
            set
            {
                if (_parent != null)
                {
                    _localRotation = Quaternion.Normalize(
                        Quaternion.Inverse(_parent.WorldRotation) * value);
                }
                else
                {
                    _localRotation = Quaternion.Normalize(value);
                }
                MarkDirty(TransformDirtyFlags.Rotation);
            }
        }

        /// <summary>世界缩放（近似值，仅对均匀缩放精确）。</summary>
        public Vector3 LossyScale
        {
            get
            {
                Vector3 scale = _localScale;
                var p = _parent;
                while (p != null)
                {
                    scale *= p._localScale;
                    p = p._parent;
                }
                return scale;
            }
        }

        /// <summary>本地到世界变换矩阵（惰性计算，脏标记驱动）。</summary>
        public Matrix4x4 LocalToWorldMatrix
        {
            get
            {
                EnsureMatrixUpdated();
                return _localToWorldMatrix;
            }
        }

        /// <summary>世界到本地变换矩阵。</summary>
        public Matrix4x4 WorldToLocalMatrix
        {
            get
            {
                EnsureMatrixUpdated();
                return _worldToLocalMatrix;
            }
        }

        /// <summary>前方方向（-Z轴在世界空间中）。</summary>
        public Vector3 Forward
        {
            get
            {
                EnsureMatrixUpdated();
                return -Vector3.Normalize(new Vector3(_localToWorldMatrix.M31, _localToWorldMatrix.M32, _localToWorldMatrix.M33));
            }
        }

        /// <summary>右方向（+X轴在世界空间中）。</summary>
        public Vector3 Right
        {
            get
            {
                EnsureMatrixUpdated();
                return Vector3.Normalize(new Vector3(_localToWorldMatrix.M11, _localToWorldMatrix.M12, _localToWorldMatrix.M13));
            }
        }

        /// <summary>上方向（+Y轴在世界空间中）。</summary>
        public Vector3 Up
        {
            get
            {
                EnsureMatrixUpdated();
                return Vector3.Normalize(new Vector3(_localToWorldMatrix.M21, _localToWorldMatrix.M22, _localToWorldMatrix.M23));
            }
        }

        /// <summary>父Transform。</summary>
        public TransformReal Parent => _parent;

        /// <summary>子Transform列表。</summary>
        public IReadOnlyList<TransformReal> Children => _children;

        /// <summary>子Transform数量。</summary>
        public int ChildCount => _children.Count;

        /// <summary>根Transform（递归查找）。</summary>
        public TransformReal Root
        {
            get
            {
                var current = this;
                while (current._parent != null)
                    current = current._parent;
                return current;
            }
        }

        public TransformReal()
        {
            _localPosition = Vector3.Zero;
            _localRotation = Quaternion.Identity;
            _localScale = Vector3.One;
        }

        /// <summary>设置欧拉角旋转。</summary>
        public void SetEulerAngles(float x, float y, float z)
        {
            LocalRotation = Quaternion.CreateFromYawPitchRoll(
                MathF.ToRadians(y), MathF.ToRadians(x), MathF.ToRadians(z));
        }

        /// <summary>
        /// 使变换朝向目标（LookAt）。
        /// </summary>
        public void LookAt(Vector3 target, Vector3? up = null)
        {
            Vector3 upVec = up ?? Vector3.UnitY;
            Vector3 forward = Vector3.Normalize(target - Position);
            if (Vector3.Dot(forward, upVec) > 0.999f)
                upVec = Vector3.UnitX; // 避免平行向量退化

            Matrix4x4 lookMatrix = Matrix4x4.CreateLookAt(Position, target, upVec);
            WorldRotation = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(lookMatrix));
        }

        /// <summary>
        /// 平移（Local或World空间）。
        /// </summary>
        public void Translate(Vector3 translation, Space relativeTo = Space.Local)
        {
            if (relativeTo == Space.Local)
            {
                // 将本地方向转换为世界方向
                Vector3 worldTranslation = Vector3.Transform(translation, WorldRotation);
                Position += worldTranslation;
            }
            else
            {
                Position += translation;
            }
        }

        /// <summary>
        /// 旋转（Local或World空间，欧拉角）。
        /// </summary>
        public void Rotate(Vector3 eulers, Space relativeTo = Space.Local)
        {
            Quaternion deltaQ = Quaternion.CreateFromYawPitchRoll(
                MathF.ToRadians(eulers.Y), MathF.ToRadians(eulers.X), MathF.ToRadians(eulers.Z));

            if (relativeTo == Space.Local)
            {
                LocalRotation = Quaternion.Normalize(LocalRotation * deltaQ);
            }
            else
            {
                WorldRotation = Quaternion.Normalize(deltaQ * WorldRotation);
            }
        }

        /// <summary>
        /// 绕指定轴旋转指定角度。
        /// </summary>
        public void RotateAround(Vector3 axis, float angleDegrees)
        {
            float angleRad = MathF.ToRadians(angleDegrees);
            Quaternion deltaQ = Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), angleRad);
            WorldRotation = Quaternion.Normalize(deltaQ * WorldRotation);
        }

        /// <summary>
        /// 将点从本地空间转换到世界空间。
        /// </summary>
        public Vector3 TransformPoint(Vector3 localPoint)
        {
            return Vector3.Transform(localPoint, LocalToWorldMatrix);
        }

        /// <summary>
        /// 将方向从本地空间转换到世界空间。
        /// </summary>
        public Vector3 TransformDirection(Vector3 localDirection)
        {
            return Vector3.TransformNormal(localDirection, WorldRotation);
        }

        /// <summary>
        /// 将向量从本地空间转换到世界空间（不受平移影响）。
        /// </summary>
        public Vector3 TransformVector(Vector3 localVector)
        {
            return Vector3.TransformNormal(localVector, LocalToWorldMatrix);
        }

        /// <summary>
        /// 将点从世界空间转换到本地空间。
        /// </summary>
        public Vector3 InverseTransformPoint(Vector3 worldPoint)
        {
            return Vector3.Transform(worldPoint, WorldToLocalMatrix);
        }

        /// <summary>
        /// 将方向从世界空间转换到本地空间。
        /// </summary>
        public Vector3 InverseTransformDirection(Vector3 worldDirection)
        {
            return Vector3.TransformNormal(worldDirection, Quaternion.Inverse(WorldRotation));
        }

        // ═══════════════════════════════════════════════════════════════════
        // 矩阵重建（脏标记驱动，惰性计算）
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>标记脏（当Position/Rotation/Scale变化时调用）。</summary>
        private void MarkDirty(TransformDirtyFlags flags)
        {
            _dirtyFlags |= flags;

            // 递归标记所有子Transform为脏
            foreach (var child in _children)
            {
                child._dirtyFlags |= TransformDirtyFlags.All;
            }
        }

        /// <summary>
        /// 确保矩阵是最新的（惰性重建）。
        /// 仅当脏标记被设置时才重新计算。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnsureMatrixUpdated()
        {
            if (_dirtyFlags == TransformDirtyFlags.Clean)
                return;

            // 计算本地矩阵: S * R * T
            Matrix4x4 localMatrix =
                Matrix4x4.CreateScale(_localScale) *
                Matrix4x4.CreateFromQuaternion(_localRotation) *
                Matrix4x4.CreateTranslation(_localPosition);

            // 递归组合父变换
            if (_parent != null)
            {
                _parent.EnsureMatrixUpdated();
                _localToWorldMatrix = localMatrix * _parent._localToWorldMatrix;
            }
            else
            {
                _localToWorldMatrix = localMatrix;
            }

            // 计算逆矩阵
            if (Matrix4x4.Invert(_localToWorldMatrix, out Matrix4x4 invMatrix))
            {
                _worldToLocalMatrix = invMatrix;
            }
            else
            {
                _worldToLocalMatrix = Matrix4x4.Identity;
            }

            _dirtyFlags = TransformDirtyFlags.Clean;
        }

        // ═══════════════════════════════════════════════════════════════════
        // 父子关系
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// 设置父Transform。从旧父移除 → 添加到新父 → 更新世界变换。
        /// </summary>
        internal void SetParent(TransformReal newParent, bool worldPositionStays = true)
        {
            if (newParent == _parent)
                return;

            // 不能设置自己或自己的子节点为父
            if (newParent != null && IsChildOf(newParent))
                throw new InvalidOperationException("Cannot set a child or self as parent.");

            // 保存世界位置（如果需要保持）
            Vector3 worldPos = worldPositionStays ? Position : Vector3.Zero;
            Quaternion worldRot = worldPositionStays ? WorldRotation : Quaternion.Identity;

            // 从旧父移除
            if (_parent != null)
            {
                _parent._children.Remove(this);
            }

            _parent = newParent;

            // 添加到新父
            if (_parent != null)
            {
                _parent._children.Add(this);

                if (worldPositionStays)
                {
                    // 反算本地变换以保持世界位置不变
                    Matrix4x4 parentWorldInverse;
                    if (Matrix4x4.Invert(_parent.LocalToWorldMatrix, out parentWorldInverse))
                    {
                        Matrix4x4 localMatrix = worldPos.ToMatrix() * worldRot.ToMatrix();
                        Matrix4x4 relativeMatrix = localMatrix * parentWorldInverse;

                        _localPosition = new Vector3(relativeMatrix.M41, relativeMatrix.M42, relativeMatrix.M43);
                        _localRotation = Quaternion.Normalize(
                            Quaternion.CreateFromRotationMatrix(relativeMatrix));

                        // 缩放保持原值
                    }
                    else
                    {
                        _localPosition = Vector3.Zero;
                        _localRotation = Quaternion.Identity;
                    }
                }
            }

            // 标记脏，触发矩阵重建
            MarkDirty(TransformDirtyFlags.All);
        }

        /// <summary>检查是否是指定Transform的子节点（递归）。</summary>
        public bool IsChildOf(TransformReal parent)
        {
            var current = _parent;
            while (current != null)
            {
                if (current == parent) return true;
                current = current._parent;
            }
            return false;
        }

        /// <summary>获取指定索引的子Transform。</summary>
        public TransformReal GetChild(int index)
        {
            if (index < 0 || index >= _children.Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _children[index];
        }

        /// <summary>查找子Transform（按名称，深度优先）。</summary>
        public TransformReal Find(string name)
        {
            foreach (var child in _children)
            {
                if (child.GameObject?.Name == name)
                    return child;
                var found = child.Find(name);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>在父节点的子列表中的索引。</summary>
        public int GetSiblingIndex()
        {
            if (_parent == null) return 0;
            return _parent._children.IndexOf(this);
        }

        /// <summary>设置在父节点的子列表中的索引。</summary>
        public void SetSiblingIndex(int index)
        {
            if (_parent == null) return;
            if (index < 0) index = 0;
            if (index >= _parent._children.Count) index = _parent._children.Count - 1;

            _parent._children.Remove(this);
            _parent._children.Insert(index, this);
        }
    }

    /// <summary>空间枚举。</summary>
    public enum Space
    {
        World,
        Local
    }

    /// <summary>Vector3矩阵辅助扩展。</summary>
    internal static class Vector3MatrixExtensions
    {
        public static Matrix4x4 ToMatrix(this Vector3 position)
        {
            return Matrix4x4.CreateTranslation(position);
        }

        public static Matrix4x4 ToMatrix(this Quaternion rotation)
        {
            return Matrix4x4.CreateFromQuaternion(rotation);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // GameObjectReal: 游戏对象
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 游戏对象：山雨引擎运行时对象的基本单位。
    /// 包含Transform、组件列表、父子层级和场景引用。
    /// </summary>
    public class GameObjectReal
    {
        // 唯一ID
        private static long s_nextId = 1;

        /// <summary>全局唯一ID。</summary>
        public long Id { get; }

        /// <summary>对象名称。</summary>
        public string Name { get; set; } = "GameObject";

        /// <summary>标签（用于查找和分组）。</summary>
        public string Tag { get; set; } = "Default";

        /// <summary>层级（用于摄像机裁剪和物理碰撞过滤）。</summary>
        public int Layer { get; set; } = 0;

        /// <summary>是否激活（影响Update和子对象）。</summary>
        public bool IsActive { get; set; } = true;

        /// <summary>变换组件。</summary>
        public TransformReal Transform { get; }

        /// <summary>所有组件。</summary>
        private readonly List<ComponentReal> _components = new();

        /// <summary>组件列表（只读）。</summary>
        public IReadOnlyList<ComponentReal> Components => _components;

        /// <summary>所属场景。</summary>
        public SceneReal Scene { get; internal set; }

        /// <summary>父对象。</summary>
        public GameObjectReal Parent => Transform.Parent?.GameObject;

        /// <summary>子对象列表。</summary>
        public IReadOnlyList<GameObjectReal> Children
        {
            get
            {
                var result = new List<GameObjectReal>();
                foreach (var childT in Transform.Children)
                {
                    if (childT.GameObject != null)
                        result.Add(childT.GameObject);
                }
                return result;
            }
        }

        /// <summary>子对象数量。</summary>
        public int ChildCount => Transform.ChildCount;

        /// <summary>是否已初始化。</summary>
        public bool IsInitialized { get; private set; }

        /// <summary>是否已启动。</summary>
        public bool IsStarted { get; private set; }

        public GameObjectReal(string name = "GameObject")
        {
            Id = System.Threading.Interlocked.Increment(ref s_nextId);
            Name = name;
            Transform = new TransformReal { GameObject = this };
        }

        /// <summary>
        /// 设置父对象。从旧父移除 → 添加到新父 → 更新世界变换。
        /// </summary>
        public void SetParent(GameObjectReal parent, bool worldPositionStays = true)
        {
            if (parent == null)
            {
                Transform.SetParent(null, worldPositionStays);
            }
            else
            {
                Transform.SetParent(parent.Transform, worldPositionStays);
            }
        }

        /// <summary>获取指定索引的子对象。</summary>
        public GameObjectReal GetChild(int index)
        {
            if (index < 0 || index >= Transform.ChildCount)
                throw new ArgumentOutOfRangeException(nameof(index));
            return Transform.GetChild(index).GameObject;
        }

        /// <summary>查找子对象（按名称，递归深度优先）。</summary>
        public GameObjectReal Find(string name)
        {
            var childT = Transform.Find(name);
            return childT?.GameObject;
        }

        /// <summary>在父对象中的兄弟索引。</summary>
        public int GetSiblingIndex()
        {
            return Transform.GetSiblingIndex();
        }

        /// <summary>设置在父对象中的兄弟索引。</summary>
        public void SetSiblingIndex(int index)
        {
            Transform.SetSiblingIndex(index);
        }

        // ═══════════════════════════════════════════════════════════════════
        // 组件管理
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// 添加组件（指定类型）。同一类型只允许一个实例，除非标记[AllowMultiple]。
        /// </summary>
        public T AddComponent<T>() where T : ComponentReal, new()
        {
            // 唯一性检查
            var compType = typeof(T);
            bool allowMultiple = compType.GetCustomAttributes(typeof(AllowMultipleAttribute), false).Length > 0;
            if (!allowMultiple && GetComponent<T>() != null)
                throw new InvalidOperationException(
                    $"GameObject '{Name}' already has a component of type {compType.Name}. Use [AllowMultiple] to allow multiple instances.");

            var component = new T();
            component.GameObject = this;
            _components.Add(component);

            // 如果对象已初始化，立即Awake
            if (IsInitialized)
                component.Awake();
            if (IsStarted)
            {
                component.Start();
                if (component.Enabled)
                    component.OnEnable();
            }

            return component;
        }

        /// <summary>
        /// 添加已有组件实例。
        /// </summary>
        public void AddComponent(ComponentReal component)
        {
            if (component == null) throw new ArgumentNullException(nameof(component));
            component.GameObject = this;
            _components.Add(component);
        }

        /// <summary>
        /// 获取指定类型的组件。
        /// </summary>
        public T GetComponent<T>() where T : ComponentReal
        {
            foreach (var comp in _components)
            {
                if (comp is T typed)
                    return typed;
            }
            return null;
        }

        /// <summary>
        /// 获取指定类型的所有组件。
        /// </summary>
        public List<T> GetComponents<T>() where T : ComponentReal
        {
            var result = new List<T>();
            foreach (var comp in _components)
            {
                if (comp is T typed)
                    result.Add(typed);
            }
            return result;
        }

        /// <summary>
        /// 尝试获取组件。
        /// </summary>
        public bool TryGetComponent<T>(out T component) where T : ComponentReal
        {
            component = GetComponent<T>();
            return component != null;
        }

        /// <summary>
        /// 移除指定类型的组件。
        /// </summary>
        public bool RemoveComponent<T>() where T : ComponentReal
        {
            for (int i = 0; i < _components.Count; i++)
            {
                if (_components[i] is T)
                {
                    var comp = _components[i];
                    if (comp.Enabled)
                        comp.OnDisable();
                    comp.OnDestroy();
                    comp.GameObject = null;
                    _components.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        /// <summary>是否拥有指定类型的组件。</summary>
        public bool HasComponent<T>() where T : ComponentReal
        {
            return GetComponent<T>() != null;
        }

        // ═══════════════════════════════════════════════════════════════════
        // 生命周期
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// 初始化对象：调用所有组件的Awake。
        /// </summary>
        internal void Initialize()
        {
            if (IsInitialized) return;
            foreach (var comp in _components)
            {
                comp.Awake();
            }
            IsInitialized = true;
        }

        /// <summary>
        /// 启动对象：调用所有组件的Start和OnEnable。
        /// </summary>
        internal void Start()
        {
            if (IsStarted) return;
            foreach (var comp in _components)
            {
                if (!comp.Enabled) continue;
                comp.Start();
                comp.OnEnable();
            }
            IsStarted = true;
        }

        /// <summary>
        /// 更新对象：调用所有已启用组件的Update。
        /// </summary>
        internal void Update(float dt)
        {
            if (!IsActive) return;
            foreach (var comp in _components)
            {
                if (!comp.Enabled) continue;
                comp.Update(dt);
            }
        }

        /// <summary>
        /// 销毁对象：调用所有组件的OnDestroy。
        /// </summary>
        internal void Destroy()
        {
            foreach (var comp in _components)
            {
                if (comp.Enabled)
                    comp.OnDisable();
                comp.OnDestroy();
                comp.GameObject = null;
            }
            _components.Clear();
            IsInitialized = false;
            IsStarted = false;
        }
    }
}
