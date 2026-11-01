using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace YamaEngine.Physics
{
    // ============================================================================================
    //  VehiclePhysicsSystem.cs
    //
    //  载具物理系统。实现汽车/坦克/摩托等载具的物理模拟。
    //  核心组件：
    //    1. 车体底盘(Chassis) - 一个刚体
    //    2. 车轮(Wheel) - 4个(通常)，用射线检测(Raycast Wheel)模拟，不真正建刚体
    //    3. 悬挂(Suspension) - 弹簧+阻尼，连接车轮和底盘
    //    4. 引擎(Engine) - 输出扭矩，通过传动系传到轮子
    //    5. 转向(Steering) - 改变前轮朝向
    //    6. 刹车(Brake) - 给轮子施加摩擦扭矩
    //
    //  射线车轮(Raycast Wheel)是游戏里主流做法：
    //    每帧从车轮位置向下发射一条射线，命中地面就算出悬挂压缩量，
    //    据此施加弹簧力。比真正建车轮刚体简单得多，且效果够用。
    //
    //  注意：质量不能为0(车体和引擎都需要质量参数)
    //  TODO: 摩托车只有2轮，平衡需要额外的gyro/扭矩辅助
    // ============================================================================================

    /// <summary>车轮数据。</summary>
    public struct Wheel
    {
        public Vector3 LocalPosition;       // 车辆局部系下的位置
        public float Radius;
        public float SuspensionRestLength;  // 悬挂静止长度
        public float SuspensionStiffness;   // 弹簧刚度
        public SuspensionDamping SuspensionDamping;           // 阻尼系数
        public float MaxSuspensionLength;   // 悬挂最大拉伸
        public float MaxSuspensionCompression;
        public float FrictionSlip;          // 侧滑摩擦
        public float RollFriction;          // 滚动摩擦
        public bool IsFrontWheel;           // 前轮(转向用)
        public bool IsDrivenWheel;          // 驱动轮(引擎扭矩传到这)
        public bool IsBrakeWheel;           // 刹车轮
        public float SteerAngle;            // 当前转向角(弧度)
        public float RotationAngle;         // 当前旋转角(视觉效果)
        public float EngineTorque;          // 当前引擎施加的扭矩
        public float BrakeTorque;           // 当前刹车扭矩
        public bool IsInContact;            // 是否接触地面
        public Vector3 ContactPoint;        // 接触点(世界系)
        public Vector3 ContactNormal;       // 接触法线
        public float SuspensionForce;       // 当前悬挂力
        public float SuspensionLength;      // 当前悬挂长度
        public float WheelSkidInfo;         // 0~1，1表示完全抓地，0表示完全打滑
        public Vector3 WheelForwardDir;     // 当前轮子前向(世界系)
        public Vector3 WheelRightDir;       // 当前轮子右向(世界系)
        public float ForwardSpeed;          // 沿前向的速度
        public float SideSpeed;             // 沿侧向的速度(侧滑)
        public float AngularVelocity;       // 轮子自转角速度
    }

    /// <summary>悬挂阻尼参数。</summary>
    public struct SuspensionDamping
    {
        public float Compression;   // 压缩阻尼
        public float Relaxation;    // 回弹阻尼
    }

    /// <summary>引擎数据。</summary>
    public class Engine
    {
        public float MaxTorque = 500f;      // 最大扭矩(N·m)
        public float MaxRpm = 7000f;        // 最大转速
        public float IdleRpm = 800f;        // 怠速
        public float CurrentRpm = 800f;
        public float Throttle;              // 油门 0~1
        public float BrakeInput;            // 刹车 0~1
        public float CurrentTorque;
        // 扭矩曲线: rpm -> torque系数(0~1)
        // 简化用线性，正经实现要有扭矩曲线
        public Func<float, float> TorqueCurve = rpm => MathF.Sin(MathF.PI * rpm / 7000f);

        public void Update(float dt, float wheelAngularVel, int drivenWheelCount)
        {
            if (drivenWheelCount <= 0) drivenWheelCount = 1;
            // 简化: RPM和轮子角速度成正比
            CurrentRpm = Math.Clamp(wheelAngularVel * 60f / (2f * MathF.PI) + IdleRpm, IdleRpm, MaxRpm);
            float torqueFactor = TorqueCurve(CurrentRpm);
            CurrentTorque = MaxTorque * torqueFactor * Throttle;
        }
    }

    /// <summary>传动系。</summary>
    public class Drivetrain
    {
        public float[] GearRatios = { 3.5f, 2.5f, 1.8f, 1.3f, 1.0f, 0.8f };  // 1~6档
        public float ReverseRatio = 3.0f;
        public float FinalDriveRatio = 3.7f;     // 主减速比
        public int CurrentGear = 1;              // 0=倒挡 1~6=前进
        public bool IsAutomatic = true;
        public float AutoUpshiftRpm = 5500f;
        public float AutoDownshiftRpm = 1500f;

        public float GetCurrentRatio()
        {
            if (CurrentGear == 0) return -ReverseRatio;
            if (CurrentGear >= 1 && CurrentGear <= GearRatios.Length)
                return GearRatios[CurrentGear - 1];
            return 1f;
        }

        public float GetTotalRatio() => GetCurrentRatio() * FinalDriveRatio;

        public void ShiftUp()
        {
            if (CurrentGear < GearRatios.Length) CurrentGear++;
        }

        public void ShiftDown()
        {
            if (CurrentGear > 0) CurrentGear--;
        }

        public void UpdateAutomatic(float rpm)
        {
            if (!IsAutomatic) return;
            if (rpm > AutoUpshiftRpm && CurrentGear < GearRatios.Length)
                CurrentGear++;
            else if (rpm < AutoDownshiftRpm && CurrentGear > 1)
                CurrentGear--;
        }
    }

    /// <summary>
    /// 载具物理系统主类。
    /// </summary>
    public sealed class VehiclePhysicsSystem
    {
        // 车辆参数
        public BodyHandle ChassisBody;
        public PhysicsWorldV2? World;
        public readonly List<Wheel> Wheels = new(4);
        public readonly Engine EngineData = new();
        public readonly Drivetrain DrivetrainData = new();

        // 控制输入
        public float SteeringInput;     // -1(左) ~ 1(右)
        public float ThrottleInput;     // 0 ~ 1
        public float BrakeInput;        // 0 ~ 1
        public float HandbrakeInput;    // 0 ~ 1
        public bool ReverseGear;

        // 配置
        public float MaxSteerAngle = 0.5f;       // 最大转向角(弧度，约30度)
        public float SteerSpeed = 3f;            // 转向回正速度
        public float DownForce = 0f;             // 空气下压力系数
        public float RollBar = 0.1f;             // 防倾杆系数
        public float CenterOfMassOffset = 0f;    // 质心降低偏移(防翻车)

        public bool IsAlive = true;
        public float SpeedKmh => _speedKmh;
        private float _speedKmh;

        // =========================================================================
        //  初始化
        // =========================================================================

        public VehiclePhysicsSystem(PhysicsWorldV2 world, BodyHandle chassis)
        {
            World = world;
            ChassisBody = chassis;
        }

        /// <summary>添加一个车轮。</summary>
        public void AddWheel(Vector3 localPos, float radius, bool isFront, bool isDriven, bool isBrake)
        {
            Wheels.Add(new Wheel
            {
                LocalPosition = localPos,
                Radius = radius,
                SuspensionRestLength = 0.3f,
                SuspensionStiffness = 30000f,
                SuspensionDamping = new SuspensionDamping
                {
                    Compression = 4400f,
                    Relaxation = 4400f
                },
                MaxSuspensionLength = 0.5f,
                MaxSuspensionCompression = 0.1f,
                FrictionSlip = 2.0f,
                RollFriction = 0.01f,
                IsFrontWheel = isFront,
                IsDrivenWheel = isDriven,
                IsBrakeWheel = isBrake
            });
        }

        /// <summary>配置默认的小汽车(4轮)。</summary>
        public void SetupDefaultCar(Vector3 chassisHalfExtents)
        {
            // 注意：localPos是车轮中心相对底盘质心的位置
            float wheelX = chassisHalfExtents.X * 0.8f;
            float wheelZ = chassisHalfExtents.Z * 0.7f;
            float wheelY = -chassisHalfExtents.Y * 0.5f;

            AddWheel(new Vector3(-wheelX, wheelY, wheelZ), 0.4f, true, false, true);   // 左前
            AddWheel(new Vector3(wheelX, wheelY, wheelZ), 0.4f, true, false, true);    // 右前
            AddWheel(new Vector3(-wheelX, wheelY, -wheelZ), 0.4f, false, true, true);  // 左后
            AddWheel(new Vector3(wheelX, wheelY, -wheelZ), 0.4f, false, true, true);   // 右后
        }

        // =========================================================================
        //  更新
        // =========================================================================

        public void Update(float dt)
        {
            if (!IsAlive || World == null) return;

            // 1) 更新引擎和传动
            UpdateEngineAndDrivetrain(dt);

            // 2) 更新转向
            UpdateSteering(dt);

            // 3) 更新每个车轮
            float totalWheelAngularVel = 0f;
            int drivenCount = 0;
            foreach (ref var w in Wheels.AsSpanRef())
            {
                UpdateWheel(ref w, dt);
                if (w.IsDrivenWheel)
                {
                    totalWheelAngularVel += w.AngularVelocity;
                    drivenCount++;
                }
            }
            if (drivenCount > 0)
                totalWheelAngularVel /= drivenCount;

            // 4) 更新引擎RPM(基于驱动轮转速)
            EngineData.Update(dt, totalWheelAngularVel, drivenCount);
            DrivetrainData.UpdateAutomatic(EngineData.CurrentRpm);

            // 5) 空气下压力
            if (DownForce > 0)
            {
                ref readonly var body = ref World.GetBodyRef(ChassisBody);
                float speed = body.LinearVelocity.Length();
                World.ApplyForce(ChassisBody, new Vector3(0, -DownForce * speed * speed, 0));
            }

            // 6) 更新速度记录
            ref readonly var b = ref World.GetBodyRef(ChassisBody);
            _speedKmh = b.LinearVelocity.Length() * 3.6f;
        }

        private void UpdateEngineAndDrivetrain(float dt)
        {
            EngineData.Throttle = ThrottleInput;
            EngineData.BrakeInput = BrakeInput;
            DrivetrainData.CurrentGear = ReverseGear ? 0 : Math.Max(1, DrivetrainData.CurrentGear);
        }

        private void UpdateSteering(float dt)
        {
            float targetSteer = SteeringInput * MaxSteerAngle;
            // 反向时转向反向(更直观)
            if (ReverseGear) targetSteer = -targetSteer;

            // 速度越高转向角越小(高速时方向盘别打死)
            ref readonly var body = ref World!.GetBodyRef(ChassisBody);
            float speed = body.LinearVelocity.Length();
            float speedFactor = Math.Clamp(1f - speed / 50f, 0.3f, 1f);
            targetSteer *= speedFactor;

            foreach (ref var w in Wheels.AsSpanRef())
            {
                if (w.IsFrontWheel)
                {
                    // 平滑过渡
                    float diff = targetSteer - w.SteerAngle;
                    w.SteerAngle += diff * Math.Clamp(SteerSpeed * dt, 0f, 1f);
                }
            }
        }

        private void UpdateWheel(ref Wheel wheel, float dt)
        {
            if (World == null) return;
            var body = World.GetBodyRef(ChassisBody);

            // 车轮世界系位置
            Vector3 worldPos = Vector3.Transform(wheel.LocalPosition, body.Orientation) + body.Position;

            // 射线方向: 沿车体向下(悬挂方向)
            Vector3 rayDir = Vector3.Transform(new Vector3(0, -1, 0), body.Orientation);
            float rayLength = wheel.SuspensionRestLength + wheel.Radius + wheel.MaxSuspensionLength;

            // 发射射线
            wheel.IsInContact = World.Raycast(worldPos, rayDir, rayLength, out var hit);
            wheel.ContactPoint = hit.Point;
            wheel.ContactNormal = hit.Normal;

            if (wheel.IsInContact)
            {
                // 算悬挂压缩量
                float hitDistance = hit.Distance;
                wheel.SuspensionLength = Math.Clamp(hitDistance - wheel.Radius,
                    0f, wheel.MaxSuspensionLength);
                float compression = wheel.SuspensionRestLength - wheel.SuspensionLength;
                compression = Math.Clamp(compression, -wheel.MaxSuspensionCompression, wheel.MaxSuspensionLength);

                // 悬挂弹簧力 F = k * x
                float springForce = wheel.SuspensionStiffness * compression;

                // 阻尼力 F = c * v
                // 算悬挂相对速度(车轮沿悬挂方向的速度)
                Vector3 r = worldPos - body.Position;
                Vector3 pointVel = body.LinearVelocity + Vector3.Cross(body.AngularVelocity, r);
                float suspVel = Vector3.Dot(pointVel, rayDir);
                float dampForce = suspVel > 0
                    ? wheel.SuspensionDamping.Relaxation * suspVel
                    : wheel.SuspensionDamping.Compression * suspVel;

                wheel.SuspensionForce = Math.Max(0, springForce - dampForce);

                // 把力施加到底盘
                Vector3 force = rayDir * -wheel.SuspensionForce;
                World.ApplyImpulseAtPoint(ChassisBody, force * dt, worldPos);

                // 算轮子的前向和右向
                // 前向: 车体前向旋转了steerAngle
                Quaternion steerRot = Quaternion.CreateFromAxisAngle(new Vector3(0, 1, 0), wheel.SteerAngle);
                Vector3 forward = Vector3.Transform(Vector3.Transform(new Vector3(0, 0, 1), steerRot), body.Orientation);
                Vector3 right = Vector3.Cross(Vector3.Transform(new Vector3(0, 1, 0), body.Orientation), forward);
                wheel.WheelForwardDir = forward;
                wheel.WheelRightDir = right;

                // 接触点相对速度
                Vector3 contactR = hit.Point - body.Position;
                Vector3 contactVel = body.LinearVelocity + Vector3.Cross(body.AngularVelocity, contactR);

                // 分解到前向和侧向
                wheel.ForwardSpeed = Vector3.Dot(contactVel, forward);
                wheel.SideSpeed = Vector3.Dot(contactVel, right);

                // 引擎扭矩 -> 驱动力
                if (wheel.IsDrivenWheel && EngineData.CurrentTorque > 0)
                {
                    float wheelTorque = EngineData.CurrentTorque * DrivetrainData.GetTotalRatio();
                    float driveForce = wheelTorque / wheel.Radius;
                    // 施加驱动力
                    World.ApplyImpulseAtPoint(ChassisBody, forward * driveForce * dt, hit.Point);
                    wheel.EngineTorque = wheelTorque;
                    wheel.AngularVelocity += (wheelTorque / (0.5f * 5f * wheel.Radius * wheel.Radius)) * dt; // I = 0.5*m*r^2
                }
                else
                {
                    wheel.EngineTorque = 0;
                }

                // 刹车
                float brakeTorque = 0;
                if (wheel.IsBrakeWheel && BrakeInput > 0)
                    brakeTorque = BrakeInput * 2000f;
                if (HandbrakeInput > 0 && !wheel.IsFrontWheel)
                    brakeTorque = Math.Max(brakeTorque, HandbrakeInput * 3000f);
                wheel.BrakeTorque = brakeTorque;

                // 刹车减速
                if (brakeTorque > 0)
                {
                    float brakeForce = brakeTorque / wheel.Radius;
                    Vector3 brakeDir = wheel.ForwardSpeed > 0 ? -forward : forward;
                    World.ApplyImpulseAtPoint(ChassisBody, brakeDir * brakeForce * dt, hit.Point);
                    wheel.AngularVelocity *= MathF.Pow(0.5f, dt);  // 强减速
                }

                // 侧向摩擦力(防止侧滑)
                float frictionForce = -wheel.SideSpeed * wheel.FrictionSlip * wheel.SuspensionForce / 100f;
                // 限制最大摩擦力(防止侧滑时突然反弹)
                float maxFriction = wheel.SuspensionForce * wheel.FrictionSlip;
                frictionForce = Math.Clamp(frictionForce, -maxFriction, maxFriction);
                World.ApplyImpulseAtPoint(ChassisBody, right * frictionForce * dt, hit.Point);

                // 滚动摩擦(微小减速)
                float rollForce = -wheel.ForwardSpeed * wheel.RollFriction;
                World.ApplyImpulseAtPoint(ChassisBody, forward * rollForce * dt, hit.Point);

                // 打滑信息(用于音效/特效)
                float maxSideForce = wheel.SuspensionForce * wheel.FrictionSlip;
                wheel.WheelSkidInfo = Math.Clamp(1f - MathF.Abs(wheel.SideSpeed) / 10f, 0f, 1f);

                // 轮子旋转(视觉)
                wheel.RotationAngle += wheel.ForwardSpeed / wheel.Radius * dt;
            }
            else
            {
                wheel.SuspensionForce = 0;
                wheel.SuspensionLength = wheel.SuspensionRestLength;
                // 空中时轮子自由旋转
                if (wheel.IsDrivenWheel && EngineData.Throttle > 0)
                    wheel.AngularVelocity += EngineData.CurrentTorque * DrivetrainData.GetTotalRatio() * dt;
                wheel.RotationAngle += wheel.AngularVelocity * dt;
            }
        }

        // =========================================================================
        //  控制
        // =========================================================================

        /// <summary>设置转向(-1左, 0中, 1右)。</summary>
        public void SetSteering(float steer) => SteeringInput = Math.Clamp(steer, -1f, 1f);

        /// <summary>设置油门(0~1)。</summary>
        public void SetThrottle(float throttle) => ThrottleInput = Math.Clamp(throttle, 0f, 1f);

        /// <summary>设置刹车(0~1)。</summary>
        public void SetBrake(float brake) => BrakeInput = Math.Clamp(brake, 0f, 1f);

        /// <summary>手刹(0~1)。</summary>
        public void SetHandbrake(float hb) => HandbrakeInput = Math.Clamp(hb, 0f, 1f);

        /// <summary>挂倒挡。</summary>
        public void SetReverse(bool reverse)
        {
            ReverseGear = reverse;
            if (reverse) DrivetrainData.CurrentGear = 0;
            else if (DrivetrainData.CurrentGear == 0) DrivetrainData.CurrentGear = 1;
        }

        /// <summary>手动换挡。</summary>
        public void ShiftUp() => DrivetrainData.ShiftUp();
        public void ShiftDown() => DrivetrainData.ShiftDown();

        // =========================================================================
        //  查询
        // =========================================================================

        public Vector3 GetChassisPosition()
        {
            if (World == null) return Vector3.Zero;
            return World.GetBodyRef(ChassisBody).Position;
        }

        public Quaternion GetChassisOrientation()
        {
            if (World == null) return Quaternion.Identity;
            return World.GetBodyRef(ChassisBody).Orientation;
        }

        public Vector3 GetChassisVelocity()
        {
            if (World == null) return Vector3.Zero;
            return World.GetBodyRef(ChassisBody).LinearVelocity;
        }

        public int GetGear() => DrivetrainData.CurrentGear;
        public float GetRpm() => EngineData.CurrentRpm;
        public float GetSpeedKmh() => _speedKmh;

        /// <summary>获取所有车轮的渲染数据。</summary>
        public IEnumerable<(Vector3 pos, float rotationAngle, float steerAngle, bool inContact, float skid)> GetWheelRenderData()
        {
            if (World == null) return Array.Empty<(Vector3, float, float, bool, float)>();
            ref readonly var body = ref World.GetBodyRef(ChassisBody);
            var result = new List<(Vector3, float, float, bool, float)>(Wheels.Count);
            foreach (var w in Wheels)
            {
                Vector3 worldPos = Vector3.Transform(w.LocalPosition, body.Orientation) + body.Position;
                result.Add((worldPos, w.RotationAngle, w.SteerAngle, w.IsInContact, w.WheelSkidInfo));
            }
            return result;
        }
    }

    /// <summary>
    /// 给Span加AsSpanRef扩展方法(实际.NET里Span<T>不支持foreach ref，这里给个工具)。
    /// </summary>
    internal static class SpanRefExtensions
    {
        public static SpanRef<T> AsSpanRef<T>(this List<T> list) where T : struct
        {
            return new SpanRef<T>(list);
        }
    }

    /// <summary>支持foreach ref的Span包装。</summary>
    public ref struct SpanRef<T> where T : struct
    {
        private readonly List<T> _list;
        public SpanRef(List<T> list) { _list = list; }
        public int Length => _list.Count;
        public ref T this[int i] => ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_list)[i];
        public Enumerator GetEnumerator() => new(this);

        public ref struct Enumerator
        {
            private readonly SpanRef<T> _span;
            private int _index;
            public Enumerator(SpanRef<T> span) { _span = span; _index = -1; }
            public ref T Current => ref _span[_index];
            public bool MoveNext() => ++_index < _span.Length;
        }
    }
}
