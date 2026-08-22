// ============================================================================================
//  StackStabilizer.cs — 自研创新 4：堆叠微震抑制。
//
//  痛点：迭代求解器在 resting stack 上的经典微震（速度不为零但极小，长期抖动）。
//  机制：Step 后扫描活动体；对"近静止"体（|vel| < threshold 且持续 N 帧）施加额外速度阻尼
//  （×dampFactor）使其更快归零。"持续 N 帧"门限防止与活跃运动对抗。
//
//  保守设计：只阻尼，不投影位置（投影可能引起下沉）。如需更强稳定可调 DampFactor。
// ============================================================================================
using System;
using System.Collections.Generic;
using System.Numerics;

namespace ShanyuEngine.Physics
{
    /// <summary>堆叠稳定器。</summary>
    public sealed class StackStabilizer
    {
        /// <summary>线速度大小低于此值视为"近静止"（米/秒）。</summary>
        public float RestLinearThreshold { get; set; } = 0.05f;
        /// <summary>角速度大小低于此值视为"近静止"（弧度/秒）。</summary>
        public float RestAngularThreshold { get; set; } = 0.1f;
        /// <summary>持续多少帧近静止才施加阻尼。</summary>
        public int RequiredRestFrames { get; set; } = 3;
        /// <summary>阻尼系数（0.5 = 速度减半）。1.0 = 无阻尼。</summary>
        public float DampFactor { get; set; } = 0.5f;
        /// <summary>是否同时阻尼角速度。</summary>
        public bool DampAngular { get; set; } = true;

        private readonly Dictionary<uint, int> _restFrames = new();

        /// <summary>Step 后调用。扫描活动体，对持续近静止的体施加阻尼。</summary>
        public void AfterStep(PhysicsWorld world, float dt)
        {
            if (world == null) return;
            foreach (uint id in world.GetAllBodyIds())
            {
                if (!world.IsBodyActive(id)) { _restFrames[id] = 0; continue; }

                Vector3 lv = world.GetLinearVelocity(id);
                float ls = lv.Length();
                float as_ = world.GetAngularVelocity(id).Length();

                if (ls < RestLinearThreshold && as_ < RestAngularThreshold)
                {
                    int n = (_restFrames.TryGetValue(id, out int v) ? v : 0) + 1;
                    _restFrames[id] = n;

                    if (n >= RequiredRestFrames)
                    {
                        // 施加阻尼
                        world.SetLinearVelocity(id, lv * DampFactor);
                        if (DampAngular)
                        {
                            Vector3 av = world.GetAngularVelocity(id);
                            world.SetAngularVelocity(id, av * DampFactor);
                        }
                    }
                }
                else
                {
                    _restFrames[id] = 0;
                }
            }
        }

        /// <summary>清除某体的静止计数（体被强制激活时调用）。</summary>
        public void ResetBody(uint bodyId) => _restFrames.Remove(bodyId);

        /// <summary>清除全部。</summary>
        public void Reset() => _restFrames.Clear();
    }
}
