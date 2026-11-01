using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.ECS;

namespace YamaEngine.Physics
{
    // ============================================================================================
    //  VehiclePhysics.cs
    //
    //  yama引擎 —— 载具物理系统
    //
    //  本文件实现一套基于射线悬挂 + Pacejka 魔术公式轮胎 + 扭矩曲线引擎的载具物理方案，
    //  对齐 UE5 Chaos / Unity WheelCollider 的整体思路，并与引擎自身的 PhysicsWorld（Jolt 后端）
    //  对接：载具自身作为 RigidBody 参与模拟，每帧把悬挂/轮胎/引擎/刹车产生的力通过
    //  AddForce / AddImpulseAtPosition 施加到刚体上。
    //
    //  主要组成：
    //    1. WheelComponent          —— 车轮（悬挂 / 转向 / 驱动力 / 制动力）
    //    2. RaycastSuspension       —— 射线悬挂（弹簧-阻尼）
    //    3. PacejkaTireModel        —— Pacejka 魔术公式轮胎力模型
    //    4. EngineModel             —— 引擎模型（扭矩曲线 / 转速 / 惯量）
    //    5. Transmission            —— 变速箱（手动 / 自动换挡）
    //    6. Differential            —— 差速器（开式 / 限滑差速器）
    //    7. SteeringSystem          —— 转向系统（阿克曼转向几何）
    //    8. BrakeSystem             —— 制动系统（ABS / TCS / ESP）
    //    9. VehicleAerodynamics     —— 载具空气动力学
    //   10. WheeledVehicleComponent —— 轮式载具主组件
    //   11. IVehicleExtension       —— 摩托车 / 坦克 / 飞行载具扩展接口
    //   12. VehicleSimulationSystem —— 系统入口（对接 ECS）
    //
    //  与 PhysicsWorld 对接说明：
    //    - 载具创建时通过 PhysicsWorld.CreateBoxBody 生成刚体，得到 BodyId。
    //    - 每帧从 PhysicsWorld 读取位置/速度，做悬挂射线 Raycast，再把合力 AddForce。
    //    - 轮胎接触力通过 AddImpulseAtPosition 在接触点施加，产生正确扭矩。
    // ============================================================================================

    /// <summary>差速器类型。</summary>
    public enum DifferentialType
    {
        /// <summary>开式差速器：允许左右轮转速差异，但低附着一侧会打滑空转。</summary>
        Open,
        /// <summary>限滑差速器（LSD）：限制左右转速差，把扭矩传向高附着侧。</summary>
        LimitedSlip,
        /// <summary>锁止差速器：左右轮强制同速。</summary>
        Locked
    }

    /// <summary>变速箱换挡模式。</summary>
    public enum GearboxMode
    {
        /// <summary>手动换挡。</summary>
        Manual,
        /// <summary>自动换挡。</summary>
        Automatic
    }

    /// <summary>驱动布局。</summary>
    public enum DriveLayout
    {
        FrontWheelDrive,
        RearWheelDrive,
        AllWheelDrive
    }

    // ============================================================================================
    //  PacejkaTireModel —— Pacejka 魔术公式轮胎力模型
    // ============================================================================================

    /// <summary>
    ///   Pacejka "魔术公式" 轮胎力模型。
    ///   公式：y(x) = D * sin(C * atan(B * x - E * (B * x - atan(B * x))))
    ///   其中 x 为侧偏角（纵向力）或侧偏角（侧向力），y 为力或力矩。
    ///   这里实现简化版纵向 + 侧向力，参数 B/C/D/E 可调。
    /// </summary>
    public struct PacejkaTireModel
    {
        /// <summary>刚度因子 B。</summary>
        public float B;
        /// <summary>形状因子 C。</summary>
        public float C;
        /// <summary>峰值因子 D（决定最大力）。</summary>
        public float D;
        /// <summary>曲率因子 E。</summary>
        public float E;

        /// <summary>纵向滑移率对峰值力的放大（纵向）。</summary>
        public float LongitudinalFrictionMultiplier;
        /// <summary>侧偏角对峰值力的放大（侧向）。</summary>
        public float LateralFrictionMultiplier;

        /// <summary>默认干燥沥青路面参数。</summary>
        public static PacejkaTireModel DryAsphalt => new()
        {
            B = 10f, C = 1.65f, D = 1f, E = -0.5f,
            LongitudinalFrictionMultiplier = 1.0f,
            LateralFrictionMultiplier = 1.0f
        };

        /// <summary>湿滑路面参数。</summary>
        public static PacejkaTireModel WetAsphalt => new()
        {
            B = 10f, C = 1.65f, D = 0.7f, E = -0.5f,
            LongitudinalFrictionMultiplier = 0.7f,
            LateralFrictionMultiplier = 0.7f
        };

        /// <summary>泥土/越野路面参数。</summary>
        public static PacejkaTireModel Dirt => new()
        {
            B = 8f, C = 1.5f, D = 0.85f, E = -0.3f,
            LongitudinalFrictionMultiplier = 0.85f,
            LateralFrictionMultiplier = 0.8f
        };

        /// <summary>
        ///   计算魔术公式输出值。
        /// </summary>
        /// <param name="x">输入量（滑移率或侧偏角的弧度）。</param>
        public readonly float Evaluate(float x)
        {
            float Bx = B * x;
            float inner = Bx - E * (Bx - MathF.Atan(Bx));
            return D * MathF.Sin(C * MathF.Atan(inner));
        }

        /// <summary>计算纵向力（归一化，乘以法向力得牛顿）。</summary>
        public readonly float ComputeLongitudinalForce(float slipRatio)
        {
            // slipRatio 通常在 -1..1 范围
            return Evaluate(slipRatio) * LongitudinalFrictionMultiplier;
        }

        /// <summary>计算侧向力（归一化，乘以法向力得牛顿）。</summary>
        public readonly float ComputeLateralForce(float slipAngleRad)
        {
            return Evaluate(slipAngleRad) * LateralFrictionMultiplier;
        }
    }

    // ============================================================================================
    //  RaycastSuspension —— 射线悬挂
    // ============================================================================================

    /// <summary>
    ///   射线悬挂模型。从悬挂起点向下投射射线，命中地面后用弹簧-阻尼公式
    ///   计算支撑力。相比真实物理车轮，射线悬挂稳定、可控、开销低，是游戏载具主流方案。
    /// </summary>
    public class RaycastSuspension
    {
        /// <summary>悬挂最大行程（米）。</summary>
        public float MaxTravel = 0.5f;

        /// <summary>弹簧刚度（N/m）。</summary>
        public float SpringStiffness = 35000f;

        /// <summary>阻尼系数（N·s/m）。</summary>
        public float DampingCoefficient = 4500f;

        /// <summary>悬挂静止时长（压缩量，米）。</summary>
        public float RestLength = 0.3f;

        /// <summary>射线起点相对载具中心的局部偏移。</summary>
        public Vector3 LocalAnchor = Vector3.Zero;

        /// <summary>射线方向（局部，通常 (0,-1,0)）。</summary>
        public Vector3 LocalRayDir = new(0, -1, 0);

        // —— 运行时状态 ——
        /// <summary>当前压缩量（米，0=完全伸展）。</summary>
        public float Compression { get; private set; }

        /// <summary>当前压缩速度（m/s，正=压缩中）。</summary>
        public float CompressionVelocity { get; private set; }

        /// <summary>上一次是否接触地面。</summary>
        public bool IsGrounded { get; private set; }

        /// <summary>接触点世界坐标。</summary>
        public Vector3 ContactPoint { get; private set; }

        /// <summary>接触法线（世界坐标）。</summary>
        public Vector3 ContactNormal { get; private set; } = Vector3.UnitY;

        private float _prevCompression;

        /// <summary>
        ///   执行悬挂射线并计算支撑力。
        /// </summary>
        /// <param name="world">物理世界。</param>
        /// <param name="bodyId">载具刚体 ID。</param>
        /// <param name="bodyPos">载具世界位置。</param>
        /// <param name="bodyRot">载具世界朝向。</param>
        /// <param name="dt">时间步长。</param>
        /// <returns>悬挂支撑力（N），未接地则为 0。</returns>
        public float Simulate(PhysicsWorld world, uint bodyId, Vector3 bodyPos,
            Quaternion bodyRot, float dt)
        {
            if (world == null) return 0f;

            // 把局部锚点/方向变换到世界空间
            Vector3 worldAnchor = bodyPos + Vector3.Transform(LocalAnchor, bodyRot);
            Vector3 worldDir = Vector3.Normalize(Vector3.Transform(LocalRayDir, bodyRot));

            float rayLen = MaxTravel + RestLength + 0.3f; // 额外余量确保能探测到地面
            if (!world.Raycast(worldAnchor, worldDir, rayLen, out var hit))
            {
                IsGrounded = false;
                _prevCompression = Compression;
                Compression = 0f;
                CompressionVelocity = 0f;
                return 0f;
            }

            IsGrounded = true;
            ContactPoint = hit.Position;   // NativeVec3 -> Vector3 (隐式转换)
            ContactNormal = hit.Normal;

            // 压缩量 = (静止长度 + 最大行程) - 当前距离
            float distToGround = hit.Distance;
            float targetCompression = Math.Clamp((RestLength + MaxTravel) - distToGround, 0f, MaxTravel);

            // 压缩速度（数值微分）
            CompressionVelocity = (targetCompression - _prevCompression) / Math.Max(dt, 1e-5f);
            _prevCompression = Compression;
            Compression = targetCompression;

            // 弹簧-阻尼力（沿世界方向，向上托起载具）
            float springForce = SpringStiffness * Compression;
            float damperForce = DampingCoefficient * CompressionVelocity;
            float totalForce = springForce + damperForce;
            if (totalForce < 0f) totalForce = 0f; // 悬挂只能推不能拉

            return totalForce;
        }
    }

    // ============================================================================================
    //  WheelComponent —— 车轮
    // ============================================================================================

    /// <summary>
    ///   车轮组件。每个车轮包含一组悬挂、轮胎模型、转向角/驱动力/制动力，
    ///   并记录与地面的接触状态。
    /// </summary>
    public class WheelComponent
    {
        /// <summary>所属载具。</summary>
        public WheeledVehicleComponent? Vehicle { get; set; }

        /// <summary>悬挂。</summary>
        public RaycastSuspension Suspension { get; set; } = new();

        /// <summary>轮胎模型。</summary>
        public PacejkaTireModel TireModel = PacejkaTireModel.DryAsphalt;

        /// <summary>轮胎半径（米）。</summary>
        public float Radius { get; set; } = 0.35f;

        /// <summary>轮胎质量（kg，用于转动惯量估算）。</summary>
        public float Mass { get; set; } = 20f;

        /// <summary>是否受转向控制。</summary>
        public bool IsSteerable { get; set; }

        /// <summary>是否受驱动力控制。</summary>
        public bool IsPowered { get; set; }

        /// <summary>是否受制动控制。</summary>
        public bool IsBraked { get; set; }

        /// <summary>当前转向角（度）。</summary>
        public float SteerAngle { get; set; }

        /// <summary>当前角速度（rad/s）。</summary>
        public float AngularVelocity { get; private set; }

        /// <summary>当前转速（RPM，由角速度换算）。</summary>
        public float RPM => AngularVelocity * 60f / (2f * MathF.PI);

        /// <summary>本帧纵向滑移率。</summary>
        public float SlipRatio { get; private set; }

        /// <summary>本帧侧偏角（弧度）。</summary>
        public float SlipAngle { get; private set; }

        /// <summary>本帧接地法向力（N）。</summary>
        public float NormalForce { get; set; }

        /// <summary>本帧纵向力（N，世界方向）。</summary>
        public Vector3 LongitudinalForce { get; private set; }

        /// <summary>本帧侧向力（N，世界方向）。</summary>
        public Vector3 LateralForce { get; private set; }

        /// <summary>是否接地。</summary>
        public bool IsGrounded => Suspension.IsGrounded;

        /// <summary>接触点。</summary>
        public Vector3 ContactPoint => Suspension.ContactPoint;

        /// <summary>接触法线。</summary>
        public Vector3 ContactNormal => Suspension.ContactNormal;

        /// <summary>由差速器分配来的驱动力矩（N·m）。</summary>
        public float DriveTorque { get; set; }

        /// <summary>本帧制动力矩（N·m）。</summary>
        public float BrakeTorque { get; set; }

        private float _wheelInertia;

        /// <summary>构造。</summary>
        public WheelComponent()
        {
            // 圆柱转动惯量 I = 0.5 * m * r^2
            _wheelInertia = 0.5f * Mass * Radius * Radius;
        }

        /// <summary>
        ///   更新车轮：执行悬挂射线、计算轮胎力、积分角速度。
        /// </summary>
        /// <param name="bodyLinearVel">载具质心线速度（世界）。</param>
        /// <param name="dt">时间步长。</param>
        public void Update(Vector3 bodyLinearVel, float dt)
        {
            NormalForce = 0f;
            LongitudinalForce = Vector3.Zero;
            LateralForce = Vector3.Zero;
            SlipRatio = 0f;
            SlipAngle = 0f;

            if (!IsGrounded || Vehicle == null)
            {
                // 未接地时轮子自由转动（摩擦衰减）
                AngularVelocity *= 0.98f;
                return;
            }

            // 计算接触点处的地面线速度（近似用质心速度 + 角速度叉乘）
            Vector3 contactVel = bodyLinearVel;

            // 轮子前向 / 右向（世界）
            Quaternion wheelRot = Vehicle.BodyRotation
                * Quaternion.CreateFromYawPitchRoll(SteerAngle * MathF.PI / 180f, 0, 0);
            Vector3 forward = Vector3.Transform(-Vector3.UnitZ, wheelRot);
            Vector3 right = Vector3.Transform(Vector3.UnitX, wheelRot);
            // 取与地面切平面：用接触法线剔除法向分量
            Vector3 normal = ContactNormal;
            forward = Vector3.Normalize(forward - normal * Vector3.Dot(forward, normal));
            right = Vector3.Normalize(right - normal * Vector3.Dot(right, normal));

            // 纵向速度（沿轮子前向）与侧向速度（沿轮子右向）
            float vLong = Vector3.Dot(contactVel, forward);
            float vLat = Vector3.Dot(contactVel, right);

            // 纵向滑移率 = (轮缘线速度 - 车体纵向速度) / max(|车体纵向速度|, eps)
            float wheelLinearVel = AngularVelocity * Radius;
            float denom = Math.Max(MathF.Abs(vLong), 2f);
            SlipRatio = (wheelLinearVel - vLong) / denom;
            SlipRatio = Math.Clamp(SlipRatio, -1f, 1f);

            // 侧偏角 = atan2(侧向速度, 纵向速度)
            SlipAngle = MathF.Atan2(vLat, MathF.Abs(vLong) + 0.1f);

            // 轮胎力（归一化 × 法向力）
            float fx = TireModel.ComputeLongitudinalForce(SlipRatio) * NormalForce;
            float fy = TireModel.ComputeLateralForce(SlipAngle) * NormalForce;

            LongitudinalForce = forward * fx;
            LateralForce = right * fy;

            // 积分轮子角速度：合力矩 = 驱动力矩 - 制动力矩 - 地面反向力矩
            float groundReactionTorque = fx * Radius; // 地面对轮缘纵向力的反作用
            float netTorque = DriveTorque - BrakeTorque - groundReactionTorque;
            AngularVelocity += (netTorque / Math.Max(_wheelInertia, 1e-3f)) * dt;

            // 限制极端角速度
            AngularVelocity = Math.Clamp(AngularVelocity, -300f, 300f);
        }

        /// <summary>把车轮力通过接触点施加到载具刚体。</summary>
        public void ApplyForces(PhysicsWorld world, uint bodyId)
        {
            if (world == null || bodyId == 0 || !IsGrounded) return;

            // 悬挂支撑力沿接触法线
            Vector3 suspensionForce = ContactNormal * NormalForce;
            // 轮胎合力
            Vector3 tireForce = LongitudinalForce + LateralForce;
            Vector3 total = suspensionForce + tireForce;

            world.AddImpulseAtPosition(bodyId, total * (1f / 60f), ContactPoint);
        }
    }

    // ============================================================================================
    //  EngineModel —— 引擎模型
    // ============================================================================================

    /// <summary>
    ///   引擎模型。用扭矩曲线描述发动机在不同转速下输出的扭矩，并维护当前转速。
    /// </summary>
    public class EngineModel
    {
        /// <summary>怠速 RPM。</summary>
        public float IdleRPM = 800f;

        /// <summary>最大 RPM（红线）。</summary>
        public float MaxRPM = 7000f;

        /// <summary>当前 RPM。</summary>
        public float CurrentRPM { get; set; }

        /// <summary>引擎转动惯量（kg·m²）。</summary>
        public float Inertia = 0.25f;

        /// <summary>扭矩曲线采样点：(RPM, 扭矩 N·m)。</summary>
        public List<(float rpm, float torque)> TorqueCurve = new();

        /// <summary>节气门开度 0..1。</summary>
        public float Throttle { get; set; }

        /// <summary>引擎制动（松油门时的阻力扭矩）。</summary>
        public float EngineBrakingTorque = 40f;

        public EngineModel()
        {
            // 默认扭矩曲线：典型自然吸气汽油机
            TorqueCurve.Add((800f, 100f));
            TorqueCurve.Add((2000f, 180f));
            TorqueCurve.Add((3500f, 240f));
            TorqueCurve.Add((5000f, 260f));
            TorqueCurve.Add((6500f, 220f));
            TorqueCurve.Add((7000f, 180f));
        }

        /// <summary>查询某 RPM 下油门全开扭矩（线性插值）。</summary>
        public float GetTorqueAtRPM(float rpm)
        {
            if (TorqueCurve.Count == 0) return 0f;
            if (rpm <= TorqueCurve[0].rpm) return TorqueCurve[0].torque;
            for (int i = 1; i < TorqueCurve.Count; i++)
            {
                if (rpm <= TorqueCurve[i].rpm)
                {
                    var (r0, t0) = TorqueCurve[i - 1];
                    var (r1, t1) = TorqueCurve[i];
                    float a = (rpm - r0) / Math.Max(r1 - r0, 1e-3f);
                    return t0 + (t1 - t0) * a;
                }
            }
            return TorqueCurve[^1].torque;
        }

        /// <summary>更新引擎转速并输出驱动扭矩。</summary>
        /// <param name="drivenWheelRPM">被驱动轮的平均 RPM（由离合器/变速箱传递）。</param>
        /// <param name="dt">时间步长。</param>
        /// <returns>引擎输出扭矩（N·m）。</returns>
        public float Update(float drivenWheelRPM, float dt)
        {
            // 限制转速区间
            float targetRPM = Math.Clamp(drivenWheelRPM, IdleRPM, MaxRPM);
            if (Throttle < 0.01f) targetRPM = Math.Max(IdleRPM, targetRPM);

            // 平滑逼近目标转速（模拟飞轮惯量）
            float omega = CurrentRPM * 2f * MathF.PI / 60f;
            float targetOmega = targetRPM * 2f * MathF.PI / 60f;
            float torque = GetTorqueAtRPM(CurrentRPM) * Throttle;
            if (Throttle < 0.01f) torque -= EngineBrakingTorque;

            float angularAccel = torque / Math.Max(Inertia, 1e-3f);
            omega += angularAccel * dt;
            // 朝目标收敛（简化：取目标与积分的混合）
            omega = MathHelper.Lerp(omega, targetOmega, 0.5f);

            CurrentRPM = Math.Clamp(omega * 60f / (2f * MathF.PI), 0f, MaxRPM + 500f);
            return torque;
        }
    }

    // ============================================================================================
    //  Transmission —— 变速箱 + Differential —— 差速器
    // ============================================================================================

    /// <summary>变速箱（手动 / 自动）。</summary>
    public class Transmission
    {
        /// <summary>换挡模式。</summary>
        public GearboxMode Mode = GearboxMode.Automatic;

        /// <summary>各挡齿比（含倒挡，倒挡放最前为负值）。</summary>
        public float[] GearRatios = { -3.5f, 0f, 3.6f, 2.1f, 1.4f, 1.0f, 0.8f };

        /// <summary>主减速比。</summary>
        public float FinalDrive = 3.7f;

        /// <summary>当前挡位索引（0=倒挡，1=空挡，2+=前进挡）。</summary>
        public int CurrentGear = 2;

        /// <summary>换挡延迟（秒）。</summary>
        public float ShiftTime = 0.2f;

        /// <summary>自动升挡 RPM 阈值。</summary>
        public float UpshiftRPM = 6000f;

        /// <summary>自动降挡 RPM 阈值。</summary>
        public float DownshiftRPM = 2000f;

        /// <summary>换挡计时器（>0 表示正在换挡，输出扭矩为 0）。</summary>
        public float ShiftTimer { get; private set; }

        /// <summary>当前总齿比（挡位 × 主减速）。</summary>
        public float CurrentRatio => GearRatios[CurrentGear] * FinalDrive;

        /// <summary>是否处于换挡中。</summary>
        public bool IsShifting => ShiftTimer > 0f;

        public void ShiftUp()
        {
            if (CurrentGear < GearRatios.Length - 1)
            {
                CurrentGear++;
                ShiftTimer = ShiftTime;
            }
        }

        public void ShiftDown()
        {
            if (CurrentGear > 0)
            {
                CurrentGear--;
                ShiftTimer = ShiftTime;
            }
        }

        /// <summary>推进自动换挡逻辑。</summary>
        public void UpdateAuto(float engineRPM, float throttle, float dt)
        {
            if (ShiftTimer > 0f)
            {
                ShiftTimer -= dt;
                return;
            }
            if (Mode != GearboxMode.Automatic) return;

            // 仅前进挡区间自动升降
            if (CurrentGear >= 2)
            {
                if (engineRPM > UpshiftRPM && CurrentGear < GearRatios.Length - 1)
                    ShiftUp();
                else if (engineRPM < DownshiftRPM && throttle > 0.3f && CurrentGear > 2)
                    ShiftDown();
            }
            else if (CurrentGear == 1 && throttle > 0.2f)
            {
                CurrentGear = 2;
                ShiftTimer = ShiftTime;
            }
        }

        /// <summary>把引擎扭矩转换为变速箱输出扭矩。</summary>
        public float GetOutputTorque(float engineTorque)
        {
            if (IsShifting || CurrentGear == 1) return 0f; // 空挡或换挡中断动力
            return engineTorque * CurrentRatio;
        }
    }

    /// <summary>差速器。</summary>
    public class Differential
    {
        public DifferentialType Type = DifferentialType.Open;

        /// <summary>限滑差速器预载力（N·m）。</summary>
        public float LimitedSlipPreload = 50f;

        /// <summary>限滑系数（0=开式，1=锁止）。</summary>
        public float LimitedSlipFactor = 0.5f;

        /// <summary>
        ///   把输入扭矩分配到左右两个驱动轮。
        /// </summary>
        public void Distribute(float inputTorque, WheelComponent left, WheelComponent right)
        {
            if (left == null || right == null) return;
            if (inputTorque == 0f)
            {
                left.DriveTorque = 0f;
                right.DriveTorque = 0f;
                return;
            }

            switch (Type)
            {
                case DifferentialType.Locked:
                    // 锁止：左右各半，转速强制一致（此处仅分配扭矩）
                    left.DriveTorque = inputTorque * 0.5f;
                    right.DriveTorque = inputTorque * 0.5f;
                    break;

                case DifferentialType.LimitedSlip:
                    // 限滑：根据两侧角速度差转移扭矩
                    float angVelDiff = left.AngularVelocity - right.AngularVelocity;
                    // 角速度慢的一侧多分一点
                    float bias = Math.Clamp(angVelDiff * 0.01f * LimitedSlipFactor, -0.4f, 0.4f);
                    left.DriveTorque = inputTorque * (0.5f - bias) + LimitedSlipPreload * (angVelDiff > 0 ? 1f : 0f);
                    right.DriveTorque = inputTorque * (0.5f + bias) + LimitedSlipPreload * (angVelDiff < 0 ? 1f : 0f);
                    break;

                case DifferentialType.Open:
                default:
                    // 开式：均分扭矩（低附着一侧打滑时由 SlipRatio 自然限制）
                    left.DriveTorque = inputTorque * 0.5f;
                    right.DriveTorque = inputTorque * 0.5f;
                    break;
            }
        }
    }

    // ============================================================================================
    //  SteeringSystem —— 阿克曼转向 / BrakeSystem —— ABS/TCS/ESP / Aerodynamics
    // ============================================================================================

    /// <summary>转向系统（阿克曼转向几何）。</summary>
    public class SteeringSystem
    {
        /// <summary>最大转向角（度）。</summary>
        public float MaxSteerAngle = 35f;

        /// <summary>转向输入 -1..1。</summary>
        public float Input { get; set; }

        /// <summary>前轴距（米）。</summary>
        public float FrontAxleDistance = 1.3f;

        /// <summary>后轴距（米）。</summary>
        public float RearAxleDistance = 1.5f;

        /// <summary>轮距（米）。</summary>
        public float TrackWidth = 1.6f;

        /// <summary>当前左轮转向角（度）。</summary>
        public float LeftWheelAngle { get; private set; }

        /// <summary>当前右轮转向角（度）。</summary>
        public float RightWheelAngle { get; private set; }

        /// <summary>基础转向角（度）。</summary>
        public float BaseSteerAngle { get; private set; }

        /// <summary>更新阿克曼转向：内外轮转向角不同。</summary>
        public void Update()
        {
            BaseSteerAngle = Math.Clamp(Input, -1f, 1f) * MaxSteerAngle;
            if (MathF.Abs(BaseSteerAngle) < 0.01f)
            {
                LeftWheelAngle = 0f;
                RightWheelAngle = 0f;
                return;
            }

            // 阿克曼：内侧轮转向角大于外侧轮
            float wheelbase = FrontAxleDistance + RearAxleDistance;
            float steerRad = BaseSteerAngle * MathF.PI / 180f;
            float turnRadius = wheelbase / MathF.Tan(MathF.Abs(steerRad));

            float innerAngle = MathF.Atan(wheelbase / (turnRadius - TrackWidth * 0.5f)) * 180f / MathF.PI;
            float outerAngle = MathF.Atan(wheelbase / (turnRadius + TrackWidth * 0.5f)) * 180f / MathF.PI;

            if (BaseSteerAngle > 0f)
            {
                // 左转：左轮为内侧
                LeftWheelAngle = innerAngle;
                RightWheelAngle = outerAngle;
            }
            else
            {
                LeftWheelAngle = -outerAngle;
                RightWheelAngle = -innerAngle;
            }
        }
    }

    /// <summary>制动系统（ABS / TCS / ESP）。</summary>
    public class BrakeSystem
    {
        /// <summary>最大制动力矩（N·m，每轮）。</summary>
        public float MaxBrakeTorque = 2500f;

        /// <summary>手刹最大制动力矩（仅后轮）。</summary>
        public float HandbrakeTorque = 3000f;

        /// <summary>是否启用 ABS（防抱死）。</summary>
        public bool EnableABS = true;

        /// <summary>是否启用 TCS（牵引力控制）。</summary>
        public bool EnableTCS = true;

        /// <summary>是否启用 ESP（车身电子稳定）。</summary>
        public bool EnableESP = true;

        /// <summary>制动输入 0..1。</summary>
        public float BrakeInput { get; set; }

        /// <summary>手刹输入 0..1。</summary>
        public float HandbrakeInput { get; set; }

        // ABS 状态
        private bool _absActive;
        private float _absTimer;

        /// <summary>为每个车轮计算制动力矩，并应用 ABS/TCS/ESP 修正。</summary>
        public void Apply(WheelComponent wheel, float slipRatio, float dt, bool isRear)
        {
            float brake = BrakeInput * MaxBrakeTorque;
            if (isRear) brake += HandbrakeInput * HandbrakeTorque;

            // ABS：检测到抱死（滑移率过大）则短暂释放制动
            if (EnableABS && BrakeInput > 0.1f && slipRatio < -0.2f)
            {
                _absActive = true;
                _absTimer = 0.05f; // 释放 50ms
            }
            if (_absActive)
            {
                _absTimer -= dt;
                if (_absTimer <= 0f) _absActive = false;
                else brake *= 0.1f; // 大幅减小制动力，让轮子恢复转动
            }

            // TCS：检测到驱动打滑（滑移率过大）则减小驱动力矩
            if (EnableTCS && wheel.IsPowered && slipRatio > 0.2f)
            {
                wheel.DriveTorque *= 0.5f;
            }

            wheel.BrakeTorque = brake;
        }

        /// <summary>ESP：根据车身横摆角速度修正转向与制动（简化版）。</summary>
        /// <param name="yawRate">车身横摆角速度（rad/s）。</param>
        /// <param name="targetYawRate">期望横摆角速度。</param>
        /// <param name="steering">转向系统，用于微调。</param>
        public void ApplyESP(float yawRate, float targetYawRate, SteeringSystem steering)
        {
            if (!EnableESP) return;
            float err = targetYawRate - yawRate;
            // 转向不足（err<0）时增大转向，过度（err>0）时减小
            steering.Input += Math.Clamp(err * 0.2f, -0.1f, 0.1f);
        }
    }

    /// <summary>载具空气动力学。</summary>
    public class VehicleAerodynamics
    {
        /// <summary>空气密度（kg/m³）。</summary>
        public float AirDensity = 1.225f;

        /// <summary>迎风面积（m²）。</summary>
        public float FrontalArea = 2.2f;

        /// <summary>空气阻力系数 Cd。</summary>
        public float DragCoefficient = 0.32f;

        /// <summary>下压力系数 Cl（负值产生下压力）。</summary>
        public float LiftCoefficient = -0.1f;

        /// <summary>计算空气阻力与下压力，返回 (阻力向量, 下压力大小)。</summary>
        public (Vector3 drag, float downforce) Compute(Vector3 velocity, Vector3 up)
        {
            float speed = velocity.Length();
            if (speed < 0.1f) return (Vector3.Zero, 0f);

            float dragMag = 0.5f * AirDensity * DragCoefficient * FrontalArea * speed * speed;
            Vector3 drag = -Vector3.Normalize(velocity) * dragMag;

            float liftMag = 0.5f * AirDensity * LiftCoefficient * FrontalArea * speed * speed;
            // 下压力沿 -up
            float downforce = -liftMag; // 正值表示向下压
            return (drag, downforce);
        }
    }

    // ============================================================================================
    //  WheeledVehicleComponent —— 轮式载具主组件 + 扩展接口
    // ============================================================================================

    /// <summary>
    ///   轮式载具扩展接口。用于在 WheeledVehicleComponent 之外叠加特殊载具行为
    ///   （摩托车倾倒稳定、坦克履带、飞行载具升力等）。
    /// </summary>
    public interface IVehicleExtension
    {
        /// <summary>扩展名称。</summary>
        string Name { get; }

        /// <summary>每帧更新扩展逻辑。</summary>
        void Update(WheeledVehicleComponent vehicle, float dt);
    }

    /// <summary>摩托车扩展：低速倾倒稳定 + 压弯倾斜。</summary>
    public class MotorcycleExtension : IVehicleExtension
    {
        public string Name => "Motorcycle";
        /// <summary>压弯倾斜灵敏度。</summary>
        public float LeanSensitivity = 0.5f;
        /// <summary>低速扶正力（防止倾倒）。</summary>
        public float StabilizeTorque = 500f;

        public void Update(WheeledVehicleComponent vehicle, float dt)
        {
            if (vehicle?.PhysicsWorld == null || vehicle.BodyId == 0) return;
            // 根据转向与速度施加压弯倾斜扭矩
            float steer = vehicle.Steering.Input;
            var vel = vehicle.LinearVelocity;
            float speed = vel.Length();
            float lean = -steer * speed * LeanSensitivity * 0.05f;
            // 绕前向施加扭矩
            Vector3 forward = Vector3.Transform(-Vector3.UnitZ, vehicle.BodyRotation);
            vehicle.PhysicsWorld.AddTorque(vehicle.BodyId, forward * lean * 100f);
            // 低速扶正
            if (speed < 5f)
            {
                Vector3 up = Vector3.Transform(Vector3.UnitY, vehicle.BodyRotation);
                vehicle.PhysicsWorld.AddTorque(vehicle.BodyId, Vector3.Cross(up, Vector3.UnitY) * StabilizeTorque);
            }
        }
    }

    /// <summary>坦克扩展：履带式差速转向（一侧制动实现原地转向）。</summary>
    public class TankExtension : IVehicleExtension
    {
        public string Name => "Tank";
        /// <summary>左右履带速度差转向强度。</summary>
        public float SkidSteerStrength = 2f;

        public void Update(WheeledVehicleComponent vehicle, float dt)
        {
            if (vehicle?.PhysicsWorld == null || vehicle.BodyId == 0) return;
            float steer = vehicle.Steering.Input;
            // 通过对左右轮施加差速制动力实现履带转向
            foreach (var w in vehicle.Wheels)
            {
                if (!w.IsGrounded) continue;
                // 右转时制动左侧，左转时制动右侧
                bool leftSide = w.Suspension.LocalAnchor.X < 0f;
                if ((steer > 0.1f && leftSide) || (steer < -0.1f && !leftSide))
                    w.BrakeTorque += 1500f * MathF.Abs(steer);
            }
        }
    }

    /// <summary>飞行载具扩展：升力面 + 推力矢量。</summary>
    public class FlyingVehicleExtension : IVehicleExtension
    {
        public string Name => "FlyingVehicle";
        public float WingArea = 16f;
        public float LiftCoefficient = 0.4f;
        public Vector3 Thrust = Vector3.Zero; // 由输入设置

        public void Update(WheeledVehicleComponent vehicle, float dt)
        {
            if (vehicle?.PhysicsWorld == null || vehicle.BodyId == 0) return;
            var vel = vehicle.LinearVelocity;
            float speed = vel.Length();
            // 机翼升力 L = 0.5 * rho * v^2 * S * Cl，沿载具 up
            float lift = 0.5f * 1.225f * speed * speed * WingArea * LiftCoefficient;
            Vector3 up = Vector3.Transform(Vector3.UnitY, vehicle.BodyRotation);
            vehicle.PhysicsWorld.AddForce(vehicle.BodyId, up * lift);
            // 推力沿载具前向
            Vector3 forward = Vector3.Transform(-Vector3.UnitZ, vehicle.BodyRotation);
            vehicle.PhysicsWorld.AddForce(vehicle.BodyId, forward * Thrust.Z + up * Thrust.Y);
        }
    }

    /// <summary>
    ///   轮式载具主组件。聚合车轮、引擎、变速箱、差速器、转向、制动、空气动力学，
    ///   并负责与 PhysicsWorld 交互（创建刚体、施加力）。
    /// </summary>
    public class WheeledVehicleComponent
    {
        /// <summary>关联的物理世界。</summary>
        public PhysicsWorld? PhysicsWorld { get; set; }

        /// <summary>载具刚体 ID（由 PhysicsWorld 分配）。</summary>
        public uint BodyId { get; private set; }

        /// <summary>实体 ID。</summary>
        public int EntityId { get; set; }

        /// <summary>驱动布局。</summary>
        public DriveLayout Layout = DriveLayout.AllWheelDrive;

        /// <summary>车轮列表。</summary>
        public List<WheelComponent> Wheels { get; } = new();

        /// <summary>引擎。</summary>
        public EngineModel Engine { get; set; } = new();

        /// <summary>变速箱。</summary>
        public Transmission Gearbox { get; set; } = new();

        /// <summary>差速器（前后各一，简化为单一）。</summary>
        public Differential Differential { get; set; } = new();

        /// <summary>转向系统。</summary>
        public SteeringSystem Steering { get; set; } = new();

        /// <summary>制动系统。</summary>
        public BrakeSystem Brakes { get; set; } = new();

        /// <summary>空气动力学。</summary>
        public VehicleAerodynamics Aero { get; set; } = new();

        /// <summary>扩展列表（摩托车 / 坦克 / 飞行）。</summary>
        public List<IVehicleExtension> Extensions { get; } = new();

        /// <summary>载具质量（kg）。</summary>
        public float Mass = 1200f;

        /// <summary>载具碰撞盒半尺寸。</summary>
        public Vector3 HalfExtents = new(1f, 0.6f, 2.2f);

        /// <summary>质心局部偏移（降低重心提升稳定性）。</summary>
        public Vector3 CenterOfMassOffset = new(0, -0.3f, 0);

        // —— 运行时状态（由物理世界回读） ——
        /// <summary>当前世界位置。</summary>
        public Vector3 BodyPosition { get; private set; }

        /// <summary>当前世界朝向。</summary>
        public Quaternion BodyRotation { get; private set; } = Quaternion.Identity;

        /// <summary>当前线速度。</summary>
        public Vector3 LinearVelocity { get; private set; }

        /// <summary>当前角速度。</summary>
        public Vector3 AngularVelocity { get; private set; }

        /// <summary>当前车速（km/h）。</summary>
        public float SpeedKmh => LinearVelocity.Length() * 3.6f;

        /// <summary>
        ///   在物理世界中创建载具刚体。
        /// </summary>
        public void Spawn(PhysicsWorld world, Vector3 position, Quaternion rotation)
        {
            PhysicsWorld = world ?? throw new ArgumentNullException(nameof(world));
            BodyId = world.CreateBoxBody(EntityId, position, rotation, HalfExtents, Mass,
                PhysicsLayers.Default, PhysicsLayers.All);
            BodyPosition = position;
            BodyRotation = rotation;
        }

        /// <summary>销毁载具刚体。</summary>
        public void Destroy()
        {
            if (PhysicsWorld != null && BodyId != 0)
            {
                PhysicsWorld.RemoveBody(BodyId);
                BodyId = 0;
            }
        }

        /// <summary>每帧更新载具物理。</summary>
        public void Update(float dt)
        {
            if (PhysicsWorld == null || BodyId == 0) return;

            // 1. 回读刚体状态
            BodyPosition = PhysicsWorld.GetPosition(BodyId);
            BodyRotation = PhysicsWorld.GetRotation(BodyId);
            LinearVelocity = PhysicsWorld.GetLinearVelocity(BodyId);
            AngularVelocity = PhysicsWorld.GetAngularVelocity(BodyId);

            // 2. 更新转向
            Steering.Update();

            // 3. 把转向角写入可转向车轮（应用阿克曼）
            int steerLeftRight = 0;
            foreach (var w in Wheels)
            {
                if (!w.IsSteerable) continue;
                w.SteerAngle = steerLeftRight == 0 ? Steering.LeftWheelAngle : Steering.RightWheelAngle;
                steerLeftRight++;
            }

            // 4. 引擎 + 变速箱
            float drivenWheelRPM = ComputeDrivenWheelRPM();
            float engineTorque = Engine.Update(drivenWheelRPM, dt);
            Gearbox.UpdateAuto(Engine.CurrentRPM, Engine.Throttle, dt);
            float gearboxTorque = Gearbox.GetOutputTorque(engineTorque);

            // 5. 差速器分配扭矩到驱动轮（按驱动布局选择驱动轮）
            var drivenWheels = GetDrivenWheels();
            if (drivenWheels.Count >= 2)
            {
                // 简化：按前后轴分组，每组调用差速器
                DistributeTorqueToAxle(gearboxTorque, drivenWheels);
            }
            else if (drivenWheels.Count == 1)
            {
                drivenWheels[0].DriveTorque = gearboxTorque;
            }

            // 6. 悬挂 + 车轮更新
            // 法向力初始化：把载具重量按车轮数均分作为初值，再由悬挂修正
            float weightPerWheel = Mass * 9.81f / Math.Max(Wheels.Count, 1);
            foreach (var w in Wheels)
            {
                w.NormalForce = weightPerWheel; // 初值
                float suspForce = w.Suspension.Simulate(PhysicsWorld, BodyId, BodyPosition, BodyRotation, dt);
                if (suspForce > 0f)
                {
                    w.NormalForce = suspForce; // 悬挂支撑力作为法向力
                }
                w.Vehicle = this;
                w.Update(LinearVelocity, dt);
            }

            // 7. 制动（含 ABS/TCS/ESP）
            foreach (var w in Wheels)
            {
                bool isRear = w.Suspension.LocalAnchor.Z < 0f;
                Brakes.Apply(w, w.SlipRatio, dt, isRear);
            }
            // ESP 简化：期望横摆 = 转向角 * 速度相关系数
            Brakes.ApplyESP(AngularVelocity.Y, Steering.Input * 0.5f, Steering);

            // 8. 空气动力学
            var (drag, downforce) = Aero.Compute(LinearVelocity,
                Vector3.Transform(Vector3.UnitY, BodyRotation));
            PhysicsWorld.AddForce(BodyId, drag);
            PhysicsWorld.AddForce(BodyId, Vector3.Transform(Vector3.UnitY, BodyRotation) * -downforce);

            // 9. 施加所有车轮力
            foreach (var w in Wheels)
                w.ApplyForces(PhysicsWorld, BodyId);

            // 10. 扩展（摩托车 / 坦克 / 飞行）
            foreach (var ext in Extensions)
                ext.Update(this, dt);
        }

        /// <summary>计算驱动轮平均 RPM（用于引擎目标转速）。</summary>
        private float ComputeDrivenWheelRPM()
        {
            float sum = 0f;
            int count = 0;
            foreach (var w in Wheels)
            {
                if (w.IsPowered)
                {
                    sum += w.RPM;
                    count++;
                }
            }
            return count > 0 ? sum / count : Engine.IdleRPM;
        }

        /// <summary>根据驱动布局返回驱动轮。</summary>
        private List<WheelComponent> GetDrivenWheels()
        {
            var result = new List<WheelComponent>();
            foreach (var w in Wheels)
            {
                bool front = w.Suspension.LocalAnchor.Z > 0f;
                bool powered = Layout switch
                {
                    DriveLayout.FrontWheelDrive => front,
                    DriveLayout.RearWheelDrive => !front,
                    DriveLayout.AllWheelDrive => true,
                    _ => true
                };
                if (powered)
                {
                    w.IsPowered = true;
                    result.Add(w);
                }
                else
                {
                    w.IsPowered = false;
                    w.DriveTorque = 0f;
                }
            }
            return result;
        }

        /// <summary>把扭矩按轴分配到驱动轮（每轴调用差速器）。</summary>
        private void DistributeTorqueToAxle(float torque, List<WheelComponent> drivenWheels)
        {
            // 简化：按 Z 坐标分前后轴
            var front = new List<WheelComponent>();
            var rear = new List<WheelComponent>();
            foreach (var w in drivenWheels)
            {
                if (w.Suspension.LocalAnchor.Z > 0f) front.Add(w);
                else rear.Add(w);
            }

            float frontRatio = Layout == DriveLayout.RearWheelDrive ? 0f
                : Layout == DriveLayout.FrontWheelDrive ? 1f : 0.5f;

            float frontTorque = torque * frontRatio;
            float rearTorque = torque * (1f - frontRatio);

            if (front.Count >= 2) Differential.Distribute(frontTorque, front[0], front[1]);
            else if (front.Count == 1) front[0].DriveTorque = frontTorque;

            if (rear.Count >= 2) Differential.Distribute(rearTorque, rear[0], rear[1]);
            else if (rear.Count == 1) rear[0].DriveTorque = rearTorque;
        }

        /// <summary>添加一个车轮。</summary>
        public WheelComponent AddWheel(Vector3 localAnchor, bool steerable, bool braked)
        {
            var wheel = new WheelComponent
            {
                Vehicle = this,
                Suspension = { LocalAnchor = localAnchor }
            };
            wheel.IsSteerable = steerable;
            wheel.IsBraked = braked;
            Wheels.Add(wheel);
            return wheel;
        }
    }

    // ============================================================================================
    //  VehicleSimulationSystem —— 系统入口（对接 ECS）
    // ============================================================================================

    /// <summary>
    ///   载具模拟系统。集中更新所有注册的 WheeledVehicleComponent。
    ///   对接 ECS：可从实体查询载具组件引用并集中 tick。
    /// </summary>
    public class VehicleSimulationSystem : ECS.ISystem
    {
        private readonly List<WheeledVehicleComponent> _vehicles = new();
        private readonly PhysicsWorld _world;

        public string Name => "VehicleSimulationSystem";

        public VehicleSimulationSystem(PhysicsWorld world)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
        }

        /// <summary>注册载具。</summary>
        public void Register(WheeledVehicleComponent vehicle) => _vehicles.Add(vehicle);

        /// <summary>注销载具。</summary>
        public bool Unregister(WheeledVehicleComponent vehicle) => _vehicles.Remove(vehicle);

        public void Update(float dt, ECS.EntityComponentSystem ecs)
        {
            foreach (var v in _vehicles)
            {
                if (v.BodyId == 0) continue;
                v.Update(dt);

                // 把载具变换同步回 ECS（如有对应实体）
                if (ecs.HasEntity(v.EntityId) && ecs.HasComponent<ECS.TransformComponent>(v.EntityId))
                {
                    var t = ecs.GetComponent<ECS.TransformComponent>(v.EntityId);
                    t.Position = v.BodyPosition;
                    t.Rotation = v.BodyRotation;
                    ecs.SetComponent(v.EntityId, t);
                }
            }
        }
    }

    /// <summary>数学辅助（避免引入额外命名空间依赖）。</summary>
}
