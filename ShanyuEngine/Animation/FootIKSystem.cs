using System;
using System.Collections.Generic;
using System.Numerics;
using ShanyuEngine.Skinning;

namespace ShanyuEngine.Animation.FootIK
{
    // ============================================================================================
    //  FootIKSystem.cs
    //
    //  脚部 IK 系统（Foot IK System）。
    //
    //  功能要点：
    //    - 两骨骼 IK（大腿-小腿-脚）：自包含求解器，与引擎既有 TwoBoneIKSolver 采用相同的
    //      世界/局部旋转约定（world = parent * local）。
    //    - 地面追踪：通过 IGroundProbe 射线检测地面高度，将脚部贴合地面。
    //    - 脚部对齐：按地面法线对齐脚部上方向（保留航向 yaw）。
    //    - 反向 IK（脚不旋转）：MaintainFootRotation 模式下仅弯曲腿骨，脚部朝向保持不变。
    //    - 平滑过渡：脚部 IK 权重在站立/行走/腾空之间平滑过渡，并提供髋部下沉补偿。
    //
    //  复用既有 Skeleton / SkeletonPose 数据类型。
    //  注：置于子命名空间 ShanyuEngine.Animation.FootIK，与既有 AnimationRigging.cs 中的
    //  IKSolver / TwoBoneIKSolver 等类型隔离，保证可编译。
    // ============================================================================================

    // ──────────────────────────────────────────────────────────────
    //  地面追踪
    // ──────────────────────────────────────────────────────────────

    /// <summary>地面探测结果。</summary>
    public struct GroundProbeResult
    {
        /// <summary>是否命中地面。</summary>
        public bool Hit;
        /// <summary>命中点世界坐标。</summary>
        public Vector3 Point;
        /// <summary>命中点法线（朝上）。</summary>
        public Vector3 Normal;
        /// <summary>射线行进距离。</summary>
        public float Distance;

        public static GroundProbeResult Miss => new() { Hit = false, Normal = Vector3.UnitY };
    }

    /// <summary>
    /// 地面探测接口。由物理系统或自定义逻辑实现，供 FootIKSolver 进行射线检测。
    /// </summary>
    public interface IGroundProbe
    {
        /// <summary>是否有效（可用）。</summary>
        bool IsValid { get; }

        /// <summary>从 origin 沿 direction 发射长度 maxDistance 的射线，返回命中结果。</summary>
        GroundProbeResult Trace(Vector3 origin, Vector3 direction, float maxDistance);
    }

    /// <summary>
    /// 平地探测：在固定高度 y=GroundHeight 的水平面上检测，法线恒为上方向。
    /// 用于无物理场景下的测试与占位。
    /// </summary>
    public sealed class FlatGroundProbe : IGroundProbe
    {
        public float GroundHeight { get; set; } = 0f;
        public Vector3 Up { get; set; } = Vector3.UnitY;
        public bool IsValid => true;

        public GroundProbeResult Trace(Vector3 origin, Vector3 direction, float maxDistance)
        {
            // 仅处理沿 -Up 方向的垂直射线
            float denom = Vector3.Dot(direction, -Up);
            if (MathF.Abs(denom) < 1e-6f) return GroundProbeResult.Miss;
            float t = (GroundHeight - Vector3.Dot(origin, Up)) / -denom / Vector3.Dot(Up, Up);
            if (t < 0f || t > maxDistance) return GroundProbeResult.Miss;
            return new GroundProbeResult
            {
                Hit = true,
                Point = origin + direction * t,
                Normal = Up,
                Distance = t
            };
        }
    }

    /// <summary>基于委托的地面探测：将 Trace 转发给外部函数（接入真实物理）。</summary>
    public sealed class FuncGroundProbe : IGroundProbe
    {
        private readonly Func<Vector3, Vector3, float, GroundProbeResult> _trace;
        public bool IsValid { get; set; } = true;

        public FuncGroundProbe(Func<Vector3, Vector3, float, GroundProbeResult> trace)
        {
            _trace = trace ?? throw new ArgumentNullException(nameof(trace));
        }

        public GroundProbeResult Trace(Vector3 origin, Vector3 direction, float maxDistance)
            => _trace(origin, direction, maxDistance);
    }

    // ──────────────────────────────────────────────────────────────
    //  底层数学（与引擎 RigPoseMath 同约定）
    // ──────────────────────────────────────────────────────────────

    internal static class FootRigMath
    {
        public static Vector3 GetWorldPosition(Matrix4x4[] world, int index)
            => new(world[index].M41, world[index].M42, world[index].M43);

        /// <summary>骨骼世界旋转（沿父链组合局部旋转：world = parent * local）。</summary>
        public static Quaternion GetWorldRotation(Skeleton skeleton, SkeletonPose pose, int index)
        {
            var rot = pose.GetRotation(index);
            int p = skeleton.Bones[index].ParentIndex;
            while (p >= 0)
            {
                rot = pose.GetRotation(p) * rot;
                p = skeleton.Bones[p].ParentIndex;
            }
            return rot;
        }

        /// <summary>设置骨骼局部旋转使其世界旋转等于 worldRot。</summary>
        public static void SetWorldRotation(Skeleton skeleton, SkeletonPose pose, int index, Quaternion worldRot)
        {
            int parent = skeleton.Bones[index].ParentIndex;
            var local = parent >= 0
                ? Quaternion.Inverse(GetWorldRotation(skeleton, pose, parent)) * worldRot
                : worldRot;
            pose.SetRotation(index, Quaternion.Normalize(local));
        }

        /// <summary>最短弧旋转：把方向 from 映射到 to。</summary>
        public static Quaternion RotationFromTo(Vector3 from, Vector3 to)
        {
            var f = from.LengthSquared() < 1e-12f ? Vector3.UnitZ : Vector3.Normalize(from);
            var t = to.LengthSquared() < 1e-12f ? Vector3.UnitZ : Vector3.Normalize(to);
            float dot = Math.Clamp(Vector3.Dot(f, t), -1f, 1f);
            if (dot >= 0.999999f) return Quaternion.Identity;
            if (dot <= -0.999999f)
            {
                var axis = MathF.Abs(f.X) < 0.9f
                    ? Vector3.Normalize(Vector3.Cross(f, Vector3.UnitX))
                    : Vector3.Normalize(Vector3.Cross(f, Vector3.UnitY));
                return Quaternion.CreateFromAxisAngle(axis, MathF.PI);
            }
            var c = Vector3.Cross(f, t);
            var q = new Quaternion(c.X, c.Y, c.Z, 1f + dot);
            return Quaternion.Normalize(q);
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  两骨骼 IK 求解器
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 两骨骼 IK 求解参数与结果。求解 root(大腿)-mid(小腿)-tip(脚) 三骨链，
    /// 使 tip 到达目标位置，并可选对齐目标朝向。
    /// </summary>
    public static class TwoBoneIK
    {
        /// <summary>
        /// 求解两骨骼 IK。
        /// </summary>
        /// <param name="skeleton">骨架。</param>
        /// <param name="pose">姿态（将被就地修改局部旋转）。</param>
        /// <param name="world">当前世界矩阵数组（用于读取关节位置）。</param>
        /// <param name="root">根关节（大腿）索引。</param>
        /// <param name="mid">中间关节（小腿）索引。</param>
        /// <param name="tip">末端关节（脚）索引。</param>
        /// <param name="targetPos">目标位置。</param>
        /// <param name="tipTargetRot">末端目标朝向（maintainTipRot=false 时使用）。</param>
        /// <param name="pole">膝关节弯曲方向提示（世界坐标，可为 null）。</param>
        /// <param name="weight">IK 权重 [0,1]。</param>
        /// <param name="maintainTipRot">为 true 时不改变末端朝向（反向 IK：脚不旋转）。</param>
        public static void Solve(
            Skeleton skeleton, SkeletonPose pose, Matrix4x4[] world,
            int root, int mid, int tip,
            Vector3 targetPos, Quaternion tipTargetRot,
            Vector3? pole, float weight, bool maintainTipRot)
        {
            if (skeleton == null || pose == null) return;
            if (weight <= 0f) return;
            if (root < 0 || mid < 0 || tip < 0) return;
            if (root >= skeleton.Bones.Count || mid >= skeleton.Bones.Count || tip >= skeleton.Bones.Count) return;
            // 要求 mid 为 root 的子骨骼、tip 为 mid 的子骨骼
            if (skeleton.Bones[mid].ParentIndex != root) return;
            if (skeleton.Bones[tip].ParentIndex != mid) return;

            var rootPos = FootRigMath.GetWorldPosition(world, root);
            var midPos = FootRigMath.GetWorldPosition(world, mid);
            var tipPos = FootRigMath.GetWorldPosition(world, tip);

            float a = Vector3.Distance(rootPos, midPos);
            float b = Vector3.Distance(midPos, tipPos);
            if (a <= 1e-6f || b <= 1e-6f) return;

            var rootWorldRot = FootRigMath.GetWorldRotation(skeleton, pose, root);

            var target = targetPos;
            float d = Vector3.Distance(rootPos, target);
            float dClamped = Math.Clamp(d, MathF.Abs(a - b) + 1e-4f, a + b - 1e-4f);
            if (d > dClamped)
                target = rootPos + Vector3.Normalize(target - rootPos) * dClamped;

            // 解三角形求新中间关节位置
            var targetDir = target - rootPos;
            float tLen = targetDir.Length();
            if (tLen < 1e-6f) return;
            targetDir /= tLen;

            float x = (a * a - b * b + tLen * tLen) / (2f * tLen);
            float hSq = a * a - x * x;
            float h = hSq > 0f ? MathF.Sqrt(hSq) : 0f;

            Vector3 bendDir = Vector3.Zero;
            if (pole.HasValue)
            {
                var poleDir = pole.Value - rootPos;
                bendDir = poleDir - targetDir * Vector3.Dot(poleDir, targetDir);
            }
            if (bendDir.LengthSquared() < 1e-8f)
            {
                var cur = midPos - rootPos;
                bendDir = cur - targetDir * Vector3.Dot(cur, targetDir);
            }
            if (bendDir.LengthSquared() < 1e-8f)
            {
                bendDir = Vector3.Cross(targetDir, MathF.Abs(targetDir.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX);
            }
            bendDir = Vector3.Normalize(bendDir);

            var newMidPos = rootPos + targetDir * x + bendDir * h;

            // 旋转 root 使 root->mid 指向 newMidPos
            var deltaRoot = FootRigMath.RotationFromTo(midPos - rootPos, newMidPos - rootPos);
            deltaRoot = Quaternion.Slerp(Quaternion.Identity, deltaRoot, weight);
            var newRootWorldRot = Quaternion.Normalize(deltaRoot * rootWorldRot);
            FootRigMath.SetWorldRotation(skeleton, pose, root, newRootWorldRot);

            // 旋转 mid 使 mid->tip 指向 target
            var midWorldRot = FootRigMath.GetWorldRotation(skeleton, pose, mid);
            var deltaMid = FootRigMath.RotationFromTo(tipPos - midPos, target - newMidPos);
            deltaMid = Quaternion.Slerp(Quaternion.Identity, deltaMid, weight);
            var newMidWorldRot = Quaternion.Normalize(deltaMid * midWorldRot);
            FootRigMath.SetWorldRotation(skeleton, pose, mid, newMidWorldRot);

            // 末端朝向
            if (!maintainTipRot)
            {
                var tipWorldRot = FootRigMath.GetWorldRotation(skeleton, pose, tip);
                var blended = Quaternion.Slerp(tipWorldRot, tipTargetRot, weight);
                FootRigMath.SetWorldRotation(skeleton, pose, tip, blended);
            }
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  腿部配置与运行期状态
    // ──────────────────────────────────────────────────────────────

    /// <summary>一条腿的骨骼索引配置（大腿-小腿-脚，可选脚趾）。</summary>
    public struct FootIKLegSetup
    {
        public int ThighIndex;
        public int CalfIndex;
        public int FootIndex;
        public int ToeIndex;
        public bool IsValid => ThighIndex >= 0 && CalfIndex >= 0 && FootIndex >= 0;
    }

    /// <summary>单条腿的运行期 IK 状态（含平滑量）。</summary>
    public sealed class FootIKLeg
    {
        /// <summary>骨骼配置。</summary>
        public FootIKLegSetup Setup;

        /// <summary>目标 IK 权重（由外部运动逻辑设置，如站立/行走=1，腾空=0）。</summary>
        public float TargetWeight { get; set; } = 1f;

        /// <summary>当前平滑后的 IK 权重。</summary>
        public float CurrentWeight { get; set; }

        /// <summary>平滑后的脚部目标位置（世界）。</summary>
        public Vector3 SmoothedFootPosition { get; set; }

        /// <summary>平滑后的地面法线。</summary>
        public Vector3 SmoothedNormal { get; set; } = Vector3.UnitY;

        /// <summary>是否已初始化（首帧直接捕捉目标值）。</summary>
        public bool Initialized { get; set; }

        /// <summary>上一帧是否触地。</summary>
        public bool Grounded { get; set; } = true;

        /// <summary>膝部弯曲方向提示偏移（沿上方向上偏移，使膝盖向前弯）。</summary>
        public float KneeForwardBias { get; set; } = 0.1f;

        /// <summary>该腿的脚部偏移（沿上方向，高于地面的距离）。</summary>
        public float FootOffset { get; set; }
    }

    // ──────────────────────────────────────────────────────────────
    //  权重过渡曲线（按移动速度映射 IK 权重）
    // ──────────────────────────────────────────────────────────────

    /// <summary>按移动速度映射脚部 IK 目标权重的曲线（线性键）。</summary>
    public sealed class FootIKWeightCurve
    {
        private readonly List<(float Speed, float Weight)> _keys = new();

        public void AddKey(float speed, float weight)
        {
            _keys.Add((speed, weight));
            _keys.Sort((a, b) => a.Speed.CompareTo(b.Speed));
        }

        public float Evaluate(float speed)
        {
            if (_keys.Count == 0) return 1f;
            if (speed <= _keys[0].Speed) return _keys[0].Weight;
            if (speed >= _keys[^1].Speed) return _keys[^1].Weight;
            for (int i = 0; i < _keys.Count - 1; i++)
            {
                if (speed >= _keys[i].Speed && speed <= _keys[i + 1].Speed)
                {
                    float span = _keys[i + 1].Speed - _keys[i].Speed;
                    float k = span > 1e-6f ? (speed - _keys[i].Speed) / span : 0f;
                    return _keys[i].Weight + (_keys[i + 1].Weight - _keys[i].Weight) * k;
                }
            }
            return _keys[^1].Weight;
        }

        /// <summary>默认曲线：静止=1，行走=1，奔跑=0.4。</summary>
        public static FootIKWeightCurve DefaultLocomotion()
        {
            var c = new FootIKWeightCurve();
            c.AddKey(0f, 1f);
            c.AddKey(150f, 1f);
            c.AddKey(400f, 0.6f);
            c.AddKey(700f, 0.3f);
            return c;
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  FootIKSolver 主系统
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 脚部 IK 求解器。对一组腿执行地面追踪 + 两骨骼 IK + 脚部法线对齐，
    /// 并在站立/行走/腾空之间平滑过渡 IK 权重，必要时下沉髋部以使脚部贴合地面。
    /// </summary>
    public sealed class FootIKSolver
    {
        /// <summary>腿部列表（通常左、右各一条）。</summary>
        public List<FootIKLeg> Legs { get; } = new();

        /// <summary>地面探测器。</summary>
        public IGroundProbe? GroundProbe { get; set; }

        /// <summary>全局上方向（默认 +Y）。</summary>
        public Vector3 UpVector { get; set; } = Vector3.UnitY;

        /// <summary>射线起始高于脚部的距离。</summary>
        public float ProbeStartHeight { get; set; } = 1.0f;

        /// <summary>射线最大长度。</summary>
        public float ProbeDistance { get; set; } = 2.0f;

        /// <summary>默认脚部离地偏移（沿上方向）。</summary>
        public float FootOffset { get; set; } = 0.02f;

        /// <summary>全局 IK 权重。</summary>
        public float TotalWeight { get; set; } = 1f;

        /// <summary>权重平滑速率（越大越快，1/秒量级）。</summary>
        public float WeightLerpSpeed { get; set; } = 8f;

        /// <summary>位置平滑速率。</summary>
        public float PositionLerpSpeed { get; set; } = 12f;

        /// <summary>法线平滑速率。</summary>
        public float NormalLerpSpeed { get; set; } = 10f;

        /// <summary>是否按地面法线对齐脚部（保留航向）。</summary>
        public bool AlignFootToNormal { get; set; } = true;

        /// <summary>反向 IK：为 true 时脚部朝向保持不变，仅弯曲腿骨。</summary>
        public bool MaintainFootRotation { get; set; } = false;

        /// <summary>脚部“前方”轴（用于提取航向），默认 +Z。</summary>
        public Vector3 FootForwardAxis { get; set; } = Vector3.UnitZ;

        /// <summary>是否启用髋部下沉补偿。</summary>
        public bool EnableHipAdjustment { get; set; } = true;

        /// <summary>髋部骨骼索引（默认 0）。</summary>
        public int HipBoneIndex { get; set; } = 0;

        /// <summary>髋部下沉权重。</summary>
        public float HipAdjustWeight { get; set; } = 1f;

        /// <summary>髋部下沉平滑速率。</summary>
        public float HipLerpSpeed { get; set; } = 10f;

        /// <summary>按速度映射目标权重的曲线（可选）。</summary>
        public FootIKWeightCurve? WeightCurve { get; set; }

        private float _smoothedHipShift;

        public FootIKSolver() { }

        /// <summary>添加一条腿。</summary>
        public FootIKLeg AddLeg(FootIKLegSetup setup)
        {
            var leg = new FootIKLeg { Setup = setup };
            Legs.Add(leg);
            return leg;
        }

        /// <summary>设置移动状态，自动更新各腿目标权重。</summary>
        /// <param name="speed">当前移动速度。</param>
        /// <param name="grounded">角色是否触地。</param>
        public void SetLocomotion(float speed, bool grounded)
        {
            float w = grounded ? (WeightCurve?.Evaluate(speed) ?? 1f) : 0f;
            foreach (var leg in Legs)
            {
                leg.TargetWeight = w;
                leg.Grounded = grounded;
            }
        }

        /// <summary>
        /// 执行脚部 IK 求解，就地修改 <paramref name="pose"/>。
        /// </summary>
        /// <param name="skeleton">骨架。</param>
        /// <param name="pose">姿态。</param>
        /// <param name="deltaTime">帧间隔（秒）。</param>
        public void Solve(Skeleton skeleton, SkeletonPose pose, float deltaTime)
        {
            if (skeleton == null || pose == null) return;
            if (Legs.Count == 0) return;
            deltaTime = MathF.Max(deltaTime, 1e-5f);

            // ── 1. 髋部下沉补偿（先于 IK）──────────────────────────
            if (EnableHipAdjustment)
            {
                ApplyHipAdjustment(skeleton, pose, deltaTime);
            }

            // ── 2. 重新计算世界矩阵（髋部已移动）──────────────────
            var world = skeleton.ComputeWorldMatrices(pose);

            // ── 3. 逐腿求解 ────────────────────────────────────────
            for (int li = 0; li < Legs.Count; li++)
            {
                var leg = Legs[li];
                var setup = leg.Setup;
                if (!setup.IsValid) continue;
                if (setup.ThighIndex >= skeleton.Bones.Count ||
                    setup.CalfIndex >= skeleton.Bones.Count ||
                    setup.FootIndex >= skeleton.Bones.Count) continue;

                // 权重平滑过渡
                float targetW = Math.Clamp(leg.TargetWeight * TotalWeight, 0f, 1f);
                float wAlpha = SmoothFactor(WeightLerpSpeed, deltaTime);
                leg.CurrentWeight = Lerp(leg.CurrentWeight, targetW, wAlpha);
                if (leg.CurrentWeight <= 1e-4f) continue;

                // 脚部当前位置
                var footPos = FootRigMath.GetWorldPosition(world, setup.FootIndex);

                // 地面追踪
                Vector3 targetFootPos = footPos;
                Vector3 targetNormal = UpVector;
                bool hit = false;
                if (GroundProbe != null && GroundProbe.IsValid)
                {
                    var origin = footPos + UpVector * ProbeStartHeight;
                    var dir = -UpVector;
                    var res = GroundProbe.Trace(origin, dir, ProbeStartHeight + ProbeDistance);
                    if (res.Hit)
                    {
                        targetFootPos = res.Point + UpVector * (FootOffset + leg.FootOffset);
                        targetNormal = Vector3.Normalize(res.Normal);
                        hit = true;
                    }
                }
                leg.Grounded = hit;

                // 首帧直接捕捉
                if (!leg.Initialized)
                {
                    leg.SmoothedFootPosition = targetFootPos;
                    leg.SmoothedNormal = targetNormal;
                    leg.Initialized = true;
                }
                else
                {
                    float pAlpha = SmoothFactor(PositionLerpSpeed, deltaTime);
                    float nAlpha = SmoothFactor(NormalLerpSpeed, deltaTime);
                    leg.SmoothedFootPosition = Vector3.Lerp(leg.SmoothedFootPosition, targetFootPos, pAlpha);
                    leg.SmoothedNormal = Vector3.Normalize(Vector3.Lerp(leg.SmoothedNormal, targetNormal, nAlpha));
                }

                // 膝盖弯曲方向提示（在脚前方偏上）
                Vector3? pole = null;
                if (leg.KneeForwardBias != 0f)
                {
                    var hipPos = FootRigMath.GetWorldPosition(world, setup.ThighIndex);
                    var fwd = ExtractHorizontalForward(footPos, hipPos, world, setup.FootIndex, skeleton, pose);
                    pole = (hipPos + footPos) * 0.5f + fwd * leg.KneeForwardBias + UpVector * 0.1f;
                }

                // 末端目标朝向：按法线对齐（保留航向）或保持原朝向
                Quaternion tipTargetRot;
                bool maintainTip = MaintainFootRotation;
                if (MaintainFootRotation)
                {
                    tipTargetRot = FootRigMath.GetWorldRotation(skeleton, pose, setup.FootIndex);
                }
                else if (AlignFootToNormal)
                {
                    tipTargetRot = ComputeAlignedFootRotation(skeleton, pose, setup.FootIndex, leg.SmoothedNormal);
                }
                else
                {
                    tipTargetRot = FootRigMath.GetWorldRotation(skeleton, pose, setup.FootIndex);
                }

                // 两骨骼 IK
                TwoBoneIK.Solve(
                    skeleton, pose, world,
                    setup.ThighIndex, setup.CalfIndex, setup.FootIndex,
                    leg.SmoothedFootPosition, tipTargetRot,
                    pole, leg.CurrentWeight, maintainTip);
            }
        }

        // ── 髋部下沉补偿 ──────────────────────────────────────────
        private void ApplyHipAdjustment(Skeleton skeleton, SkeletonPose pose, float deltaTime)
        {
            if (HipBoneIndex < 0 || HipBoneIndex >= skeleton.Bones.Count) return;
            var world = skeleton.ComputeWorldMatrices(pose);

            float minShift = 0f; // 负值表示下沉
            foreach (var leg in Legs)
            {
                var setup = leg.Setup;
                if (!setup.IsValid) continue;
                if (setup.ThighIndex >= skeleton.Bones.Count) continue;
                var hipPos = FootRigMath.GetWorldPosition(world, setup.ThighIndex);
                var footPos = FootRigMath.GetWorldPosition(world, setup.FootIndex);

                // 检测地面目标
                Vector3 groundPoint = footPos;
                if (GroundProbe != null && GroundProbe.IsValid)
                {
                    var origin = footPos + UpVector * ProbeStartHeight;
                    var res = GroundProbe.Trace(origin, -UpVector, ProbeStartHeight + ProbeDistance);
                    if (res.Hit) groundPoint = res.Point;
                }

                // 腿长
                float a = Vector3.Distance(hipPos, FootRigMath.GetWorldPosition(world, setup.CalfIndex));
                float b = Vector3.Distance(FootRigMath.GetWorldPosition(world, setup.CalfIndex), footPos);
                float reach = (a + b) * 0.98f;

                // 髋部最高可处高度（使脚仍能触地）：groundY + reach
                float hipUp = Vector3.Dot(hipPos, UpVector);
                float groundUp = Vector3.Dot(groundPoint, UpVector);
                float desiredHipUp = groundUp + reach + FootOffset;
                // 若髋部高于该值，需要下沉
                float shift = desiredHipUp - hipUp; // 负 => 下沉
                if (shift < minShift) minShift = shift;
            }

            // 平滑
            float alpha = SmoothFactor(HipLerpSpeed, deltaTime);
            _smoothedHipShift = Lerp(_smoothedHipShift, minShift, alpha);

            ApplyHipTranslation(skeleton, pose, _smoothedHipShift * HipAdjustWeight);
        }

        /// <summary>
        /// 将髋部沿上方向整体平移 shift（负值=下沉）。假设每帧动画求值会重置姿态，
        /// 故在此应用完整偏移量（在动画基础偏移之上叠加），而非跨帧增量。
        /// </summary>
        private void ApplyHipTranslation(Skeleton skeleton, SkeletonPose pose, float shift)
        {
            if (MathF.Abs(shift) < 1e-6f) return;
            var cur = pose.GetTranslationOffset(HipBoneIndex);
            pose.SetTranslationOffset(HipBoneIndex, cur + UpVector * shift);
        }

        // ── 计算对齐法线后的脚部世界朝向（保留航向）──────────────
        private Quaternion ComputeAlignedFootRotation(Skeleton skeleton, SkeletonPose pose, int footIndex, Vector3 normal)
        {
            var footWorldRot = FootRigMath.GetWorldRotation(skeleton, pose, footIndex);
            // 提取当前水平航向
            var fwd = Vector3.Transform(FootForwardAxis, footWorldRot);
            fwd -= UpVector * Vector3.Dot(fwd, UpVector);
            if (fwd.LengthSquared() < 1e-6f) fwd = FootForwardAxis;
            fwd = Vector3.Normalize(fwd);

            // 航向旋转（绕上方向）
            float yaw = MathF.Atan2(Vector3.Dot(fwd, RightOfUp()), Vector3.Dot(fwd, FootForwardAxisOnPlane()));
            var yawRot = Quaternion.CreateFromAxisAngle(UpVector, yaw);

            // 法线对齐：把 UpVector 旋转到 normal
            var alignRot = FootRigMath.RotationFromTo(UpVector, normal);

            return Quaternion.Normalize(alignRot * yawRot);
        }

        // 与 UpVector 正交的“右”方向（用于航向角分解）
        private Vector3 RightOfUp()
        {
            var r = Vector3.Cross(UpVector, FootForwardAxis);
            if (r.LengthSquared() < 1e-6f) r = Vector3.Cross(UpVector, MathF.Abs(UpVector.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX);
            return Vector3.Normalize(r);
        }
        private Vector3 FootForwardAxisOnPlane()
        {
            var f = FootForwardAxis - UpVector * Vector3.Dot(FootForwardAxis, UpVector);
            if (f.LengthSquared() < 1e-6f) f = RightOfUp();
            return Vector3.Normalize(f);
        }

        // ── 提取脚部水平前向（用于膝盖 pole）─────────────────────
        private Vector3 ExtractHorizontalForward(Vector3 footPos, Vector3 hipPos, Matrix4x4[] world, int footIndex, Skeleton skeleton, SkeletonPose pose)
        {
            var footRot = FootRigMath.GetWorldRotation(skeleton, pose, footIndex);
            var fwd = Vector3.Transform(FootForwardAxis, footRot);
            fwd -= UpVector * Vector3.Dot(fwd, UpVector);
            if (fwd.LengthSquared() < 1e-6f)
            {
                fwd = footPos - hipPos;
                fwd -= UpVector * Vector3.Dot(fwd, UpVector);
            }
            if (fwd.LengthSquared() < 1e-6f) return FootForwardAxisOnPlane();
            return Vector3.Normalize(fwd);
        }

        // ── 平滑辅助 ─────────────────────────────────────────────
        private static float SmoothFactor(float speed, float dt)
        {
            // 指数平滑：1 - exp(-speed * dt)
            return 1f - MathF.Exp(-speed * dt);
        }

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    }
}
