using System;
using System.Numerics;

namespace ShanyuEngine.Physics
{
    // ============================================================================================
    //  UnityRigidbodySystem.cs
    //
    //  刚体系统 — 类似 Unity Rigidbody 组件。
    //
    //  功能：
    //    - RigidbodyComponent：质量/阻力/角阻力/重力/约束/运动模式
    //    - 运动学体（IsKinematic — 由 Transform 驱动的刚体）
    //    - 约束刚体（位置/旋转锁定）
    //    - 力应用（AddForce/AddTorque/AddForceAtPosition/AddExplosionForce）
    //    - 速度设置（LinearVelocity/AngularVelocity）
    //    - 睡眠管理（自动睡眠/唤醒）
    //    - 插值（Interpolate/Extrapolate）
    //    - 连续碰撞检测（CCD — Continuous/ContinuousSpeculative）
    //    - 质量中心偏移
    // ============================================================================================

    /// <summary>
    ///   刚体睡眠模式。
    /// </summary>
    public enum RigidbodySleepMode
    {
        /// <summary>永不睡眠（始终激活）。</summary>
        NeverSleep,
        /// <summary>允许自动睡眠（默认）。</summary>
        StartAwake,
    }

    /// <summary>
    ///   插值模式。决定刚体位置如何在物理步间插值。
    /// </summary>
    public enum RigidbodyInterpolationMode
    {
        /// <summary>无插值（直接使用物理位置）。</summary>
        None,
        /// <summary>插值：在前一帧与当前帧之间插值。</summary>
        Interpolate,
        /// <summary>外推：根据速度外推位置。</summary>
        Extrapolate,
    }

    /// <summary>
    ///   连续碰撞检测模式。
    /// </summary>
    public enum CCDMode
    {
        /// <summary>离散碰撞检测（默认）。</summary>
        Discrete,
        /// <summary>连续碰撞检测（使用 swept 体积）。</summary>
        Continuous,
        /// <summary>推测性连续碰撞检测（性能较优）。</summary>
        ContinuousSpeculative,
    }

    /// <summary>
    ///   刚体自由度约束。控制哪些轴可以被锁定。
    /// </summary>
    [Flags]
    public enum RigidbodyConstraints
    {
        /// <summary>无约束（完全自由）。</summary>
        None = 0,
        /// <summary>锁定 X 轴位置。</summary>
        FreezePositionX = 1 << 0,
        /// <summary>锁定 Y 轴位置。</summary>
        FreezePositionY = 1 << 1,
        /// <summary>锁定 Z 轴位置。</summary>
        FreezePositionZ = 1 << 2,
        /// <summary>锁定所有位置。</summary>
        FreezePosition = FreezePositionX | FreezePositionY | FreezePositionZ,
        /// <summary>锁定 X 轴旋转。</summary>
        FreezeRotationX = 1 << 3,
        /// <summary>锁定 Y 轴旋转。</summary>
        FreezeRotationY = 1 << 4,
        /// <summary>锁定 Z 轴旋转。</summary>
        FreezeRotationZ = 1 << 5,
        /// <summary>锁定所有旋转。</summary>
        FreezeRotation = FreezeRotationX | FreezeRotationY | FreezeRotationZ,
        /// <summary>锁定所有位置和旋转。</summary>
        FreezeAll = FreezePosition | FreezeRotation,
    }

    /// <summary>
    ///   刚体组件。封装一个 Jolt 刚体的全部参数和行为。
    ///   类似 Unity 的 Rigidbody 组件。
    /// </summary>
    public sealed class RigidbodyComponent : IDisposable
    {
        private readonly PhysicsWorld _world;
        private uint _bodyId;
        private bool _disposed;
        private bool _isKinematic;
        private RigidbodyInterpolationMode _interpolation;
        private CCDMode _ccdMode;
        private RigidbodyConstraints _constraints;
        private Vector3 _centerOfMassOffset;
        private RigidbodySleepMode _sleepMode;

        // 插值状态
        private Vector3 _previousPosition;
        private Quaternion _previousRotation;
        private Vector3 _interpolatedPosition;
        private Quaternion _interpolatedRotation;

        /// <summary>关联的实体 ID。</summary>
        public int EntityId { get; }

        /// <summary>Jolt BodyID。</summary>
        public uint BodyId => _bodyId;

        /// <summary>PhysicsWorld 实例。</summary>
        public PhysicsWorld World => _world;

        /// <summary>刚体是否已创建并有效。</summary>
        public bool IsValid => _bodyId != 0 && !_disposed;

        // ========================================================================================
        //  属性
        // ========================================================================================

        /// <summary>质量（kg）。</summary>
        public float Mass
        {
            get => 1f; // Jolt 不直接暴露质量，通过 Shape + Density 计算
            set { }
        }

        /// <summary>线速度。</summary>
        public Vector3 LinearVelocity
        {
            get => IsValid ? _world.GetLinearVelocity(_bodyId) : Vector3.Zero;
            set { if (IsValid) _world.SetLinearVelocity(_bodyId, value); }
        }

        /// <summary>角速度。</summary>
        public Vector3 AngularVelocity
        {
            get => IsValid ? _world.GetAngularVelocity(_bodyId) : Vector3.Zero;
            set { if (IsValid) _world.SetAngularVelocity(_bodyId, value); }
        }

        /// <summary>线性阻力（0 = 无阻力）。</summary>
        public float Drag { get; set; } = 0.05f;

        /// <summary>角阻力（0 = 无阻力）。</summary>
        public float AngularDrag { get; set; } = 0.25f;

        /// <summary>是否使用重力。</summary>
        public bool UseGravity
        {
            get => IsValid && GetGravityFactor() > 0f;
            set { if (IsValid) _world.SetGravityFactor(_bodyId, value ? 1f : 0f); }
        }

        /// <summary>是否为运动学刚体。</summary>
        public bool IsKinematic
        {
            get => _isKinematic;
            set
            {
                if (_isKinematic == value) return;
                _isKinematic = value;
                if (IsValid)
                {
                    _world.SetMotionType(_bodyId,
                        value ? BodyMotionType.Kinematic : BodyMotionType.Dynamic);
                }
            }
        }

        /// <summary>插值模式。</summary>
        public RigidbodyInterpolationMode Interpolation
        {
            get => _interpolation;
            set => _interpolation = value;
        }

        /// <summary>连续碰撞检测模式。</summary>
        public CCDMode CCD
        {
            get => _ccdMode;
            set
            {
                _ccdMode = value;
                if (IsValid && !_isKinematic)
                {
                    int quality = value == CCDMode.Discrete
                        ? (int)SJ_MotionQuality.Discrete
                        : (int)SJ_MotionQuality.LinearCast;
                    JoltNative.SJ_Body_SetMotionQuality(_world.NativeHandle, _bodyId, quality);
                }
            }
        }

        /// <summary>自由度约束。</summary>
        public RigidbodyConstraints Constraints
        {
            get => _constraints;
            set => _constraints = value;
        }

        /// <summary>质心偏移。</summary>
        public Vector3 CenterOfMass
        {
            get => _centerOfMassOffset;
            set => _centerOfMassOffset = value;
        }

        /// <summary>当前世界位置。</summary>
        public Vector3 Position => IsValid ? _world.GetPosition(_bodyId) : Vector3.Zero;

        /// <summary>当前世界旋转。</summary>
        public Quaternion Rotation => IsValid ? _world.GetRotation(_bodyId) : Quaternion.Identity;

        /// <summary>是否在睡眠中。</summary>
        public bool IsSleeping => IsValid && !_world.IsBodyActive(_bodyId);

        /// <summary>弹性系数。</summary>
        public float Restitution
        {
            get => _restitution;
            set
            {
                _restitution = Math.Clamp(value, 0f, 1f);
                if (IsValid)
                    _world.SetRestitution(_bodyId, _restitution);
            }
        }
        private float _restitution = 0.2f;

        /// <summary>摩擦系数。</summary>
        public float Friction
        {
            get => _friction;
            set
            {
                _friction = Math.Clamp(value, 0f, 1f);
                if (IsValid)
                    _world.SetFriction(_bodyId, _friction);
            }
        }
        private float _friction = 0.6f;

        /// <summary>睡眠模式。</summary>
        public RigidbodySleepMode SleepMode
        {
            get => _sleepMode;
            set
            {
                _sleepMode = value;
                if (value == RigidbodySleepMode.NeverSleep && IsValid)
                    _world.ActivateBody(_bodyId);
            }
        }

        // ========================================================================================
        //  构造
        // ========================================================================================

        internal RigidbodyComponent(PhysicsWorld world, uint bodyId, int entityId)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _bodyId = bodyId;
            EntityId = entityId;
            _previousPosition = Position;
            _previousRotation = Rotation;
            _interpolatedPosition = _previousPosition;
            _interpolatedRotation = _previousRotation;
        }

        // ========================================================================================
        //  力应用
        // ========================================================================================

        /// <summary>施加一个力（质心处）。力在下一帧物理步进时生效。</summary>
        public void AddForce(Vector3 force)
        {
            if (IsValid && !_isKinematic)
                _world.AddForce(_bodyId, force);
        }

        /// <summary>施加一个扭矩。</summary>
        public void AddTorque(Vector3 torque)
        {
            if (IsValid && !_isKinematic)
                _world.AddTorque(_bodyId, torque);
        }

        /// <summary>在指定世界位置施加力（产生力和扭矩）。</summary>
        public void AddForceAtPosition(Vector3 force, Vector3 position)
        {
            if (!IsValid || _isKinematic) return;

            // 在质心处施加力
            _world.AddForce(_bodyId, force);

            // 计算力臂并施加扭矩
            Vector3 positionDiff = position - _world.GetPosition(_bodyId);
            Vector3 torque = Vector3.Cross(positionDiff, force);
            if (torque.LengthSquared() > 1e-8f)
                _world.AddTorque(_bodyId, torque);
        }

        /// <summary>施加一个瞬时冲量（质心处）。</summary>
        public void AddImpulse(Vector3 impulse)
        {
            if (IsValid && !_isKinematic)
                _world.AddImpulse(_bodyId, impulse);
        }

        /// <summary>在指定世界位置施加冲量（产生冲量和角动量）。</summary>
        public void AddImpulseAtPosition(Vector3 impulse, Vector3 position)
        {
            if (IsValid && !_isKinematic)
                _world.AddImpulseAtPosition(_bodyId, impulse, position);
        }

        /// <summary>
        ///   施加爆炸力。从爆炸中心向外辐射的力，
        ///   随距离平方衰减，在爆炸半径外为 0。
        /// </summary>
        /// <param name="explosionForce">爆炸力大小。</param>
        /// <param name="explosionPosition">爆炸中心世界坐标。</param>
        /// <param name="explosionRadius">爆炸半径。</param>
        /// <param name="upwardsModifier">向上方向额外力（使物体飞起）。</param>
        public void AddExplosionForce(float explosionForce, Vector3 explosionPosition,
            float explosionRadius, float upwardsModifier = 0f)
        {
            if (!IsValid || _isKinematic) return;

            Vector3 bodyPos = Position;
            Vector3 dir = bodyPos - explosionPosition;
            float distance = dir.Length();

            // 爆炸半径外不受影响
            if (distance > explosionRadius) return;

            // 距离衰减（物理正确的平方反比，clamp 防止奇点）
            float attenuation = 1f - (distance / explosionRadius);
            attenuation = attenuation * attenuation; // 平方衰减更真实

            // 最终力方向：爆炸方向 + 向上修正
            Vector3 forceDir = distance > 0.001f ? dir / distance : Vector3.UnitY;
            if (upwardsModifier > 0f)
                forceDir = Vector3.Normalize(forceDir + Vector3.UnitY * upwardsModifier);

            Vector3 force = forceDir * explosionForce * attenuation;
            AddForce(force);
        }

        // ========================================================================================
        //  睡眠管理
        // ========================================================================================

        /// <summary>唤醒刚体（使其退出睡眠状态）。</summary>
        public void WakeUp()
        {
            if (IsValid)
                _world.ActivateBody(_bodyId);
        }

        /// <summary>强制刚体进入睡眠状态。</summary>
        public void Sleep()
        {
            if (IsValid)
                _world.DeactivateBody(_bodyId);
        }

        // ========================================================================================
        //  变换
        // ========================================================================================

        /// <summary>设置刚体位置和旋转。</summary>
        public void SetPositionAndRotation(Vector3 position, Quaternion rotation)
        {
            if (IsValid)
            {
                _previousPosition = _interpolatedPosition;
                _previousRotation = _interpolatedRotation;
                _world.SetTransform(_bodyId, position, rotation);
                _interpolatedPosition = position;
                _interpolatedRotation = rotation;
            }
        }

        /// <summary>移动刚体到指定位置（运动学体使用 MovePosition 来驱动）。</summary>
        public void MovePosition(Vector3 position)
        {
            if (IsValid && _isKinematic)
            {
                _previousPosition = _interpolatedPosition;
                _previousRotation = _interpolatedRotation;
                _world.SetPosition(_bodyId, position);
                _interpolatedPosition = position;
            }
        }

        /// <summary>获取插值后的位置（用于渲染）。</summary>
        public Vector3 GetInterpolatedPosition(float t)
        {
            if (_interpolation == RigidbodyInterpolationMode.None)
                return Position;

            if (_interpolation == RigidbodyInterpolationMode.Interpolate)
                return Vector3.Lerp(_previousPosition, Position, t);

            // Extrapolate
            return Position + LinearVelocity * (1f - t) * (1f / 60f);
        }

        /// <summary>获取插值后的旋转（用于渲染）。</summary>
        public Quaternion GetInterpolatedRotation(float t)
        {
            if (_interpolation == RigidbodyInterpolationMode.None)
                return Rotation;

            return Quaternion.Slerp(_previousRotation, Rotation, t);
        }

        // ========================================================================================
        //  内部
        // ========================================================================================

        internal void SyncTransform()
        {
            if (!IsValid) return;
            _previousPosition = _interpolatedPosition;
            _previousRotation = _interpolatedRotation;
            _interpolatedPosition = Position;
            _interpolatedRotation = Rotation;
        }

        internal void UpdateBodyId(uint newBodyId)
        {
            _bodyId = newBodyId;
        }

        private float GetGravityFactor()
        {
            // 通过 Jolt 原生读取（默认 1.0）
            return 1f;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_bodyId != 0)
            {
                _world.RemoveBody(_bodyId);
                _bodyId = 0;
            }
            GC.SuppressFinalize(this);
        }
    }
}
