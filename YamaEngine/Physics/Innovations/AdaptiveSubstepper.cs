// ============================================================================================
//  AdaptiveSubstepper.cs — 自研创新 2：自适应子步进，解决快速小物体穿透。
//
//  痛点：Jolt collisionSteps 要手填，填错就 tunnel。本模块每步自动按"最快体在一帧内
//  移动的距离 / 最小体半径"算需要的子步数，并带滞后防帧间震荡。
//
//  机制：
//    n = ceil(vMax * dt / rMin)，clamp [MinSubsteps, MaxSubsteps]
//    滞后：若新算的 n 与上帧相差 1 以内且上帧 n 未满，保持上帧值。
// ============================================================================================
using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.Physics
{
    /// <summary>自适应子步进器。</summary>
    public sealed class AdaptiveSubstepper
    {
        /// <summary>最小子步数。</summary>
        public int MinSubsteps { get; set; } = 1;
        /// <summary>最大子步数（防止极端情况炸性能）。</summary>
        public int MaxSubsteps { get; set; } = 16;
        /// <summary>最小体半径估计（米）。用于算穿透风险。可由注册体时设置。</summary>
        public float MinBodyExtent { get; set; } = 0.1f;
        /// <summary>速度安全系数（>1 更保守，多算子步）。</summary>
        public float SafetyFactor { get; set; } = 1.1f;
        /// <summary>滞后窗口：新值与上帧差 ≤ 此值且未达上限则保持上帧。</summary>
        public int HysteresisBand { get; set; } = 1;

        /// <summary>上一帧实际使用的子步数。</summary>
        public int LastSubsteps { get; private set; } = 1;

        private readonly Dictionary<uint, float> _bodyExtents = new();

        /// <summary>注册某体的最小半径（创建体时调用，提高 rMin 估计精度）。</summary>
        public void RegisterBodyExtent(uint bodyId, float minExtent)
        {
            if (minExtent <= 0) return;
            _bodyExtents[bodyId] = minExtent;
            // 更新全局最小
            if (minExtent < MinBodyExtent) MinBodyExtent = minExtent;
        }

        /// <summary>注销。</summary>
        public void UnregisterBody(uint bodyId) => _bodyExtents.Remove(bodyId);

        /// <summary>计算本帧应使用的子步数。</summary>
        public int ComputeSubsteps(PhysicsWorld world, float dt)
        {
            float vMax = 0f;
            float rMin = MinBodyExtent;
            foreach (uint id in world.GetAllBodyIds())
            {
                if (!world.IsBodyActive(id)) continue;
                Vector3 v = world.GetLinearVelocity(id);
                float speed = v.Length();
                if (speed > vMax) vMax = speed;
                if (_bodyExtents.TryGetValue(id, out float ext) && ext < rMin) rMin = ext;
            }

            int n;
            if (vMax <= 0f || rMin <= 0f)
            {
                n = MinSubsteps;
            }
            else
            {
                float travel = vMax * dt * SafetyFactor;
                n = (int)MathF.Ceiling(travel / rMin);
                n = Math.Clamp(n, MinSubsteps, MaxSubsteps);
            }

            // 滞后：若新值与上帧差在小窗口内且未达上限，保持上帧（防震荡）
            if (LastSubsteps > 0 && Math.Abs(n - LastSubsteps) <= HysteresisBand && LastSubsteps < MaxSubsteps)
                n = LastSubsteps;

            LastSubsteps = n;
            return n;
        }

        /// <summary>自适应步进。供 PhysicsWorld.Step(dt) 在挂接后调用。</summary>
        public void StepAdaptive(PhysicsWorld world, float dt)
        {
            int steps = ComputeSubsteps(world, dt);
            // 用 world 的固定步长子步：每个子步 dt/steps，共 steps 次
            float subDt = dt / steps;
            for (int i = 0; i < steps; i++)
                world.Step(subDt, 1);
        }
    }
}
