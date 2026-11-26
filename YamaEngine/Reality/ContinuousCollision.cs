using System;
namespace YamaEngine.Reality
{
    /// <summary>
    /// 连续碰撞检测（CCD）：求最早接触时间 TOI，防止高速物体一帧穿过薄壁。
    /// 离散检测只在帧末判断重叠，是「子弹穿纸」类 bug 的根源。
    /// 返回 [0, dt] 内的 TOI；无接触返回 -1。
    /// </summary>
    public static class ContinuousCollision
    {
        /// <summary>运动球 vs 无限平面 planeNormal·x = planeD（法向单位，指向球运动来的外侧）。</summary>
        public static float SweptSpherePlane(RVec3 p0, RVec3 velocity, float radius,
                                             RVec3 planeNormal, float planeD, float dt)
        {
            float denom = velocity.Dot(planeNormal);
            if (MathF.Abs(denom) < 1e-12f) return -1f; // 平行掠过
            float dist0 = p0.Dot(planeNormal) - planeD;
            float target = radius;
            float t = (target - dist0) / denom;
            if (t < 0f || t > dt) return -1f;
            // 必须是从外向内运动（dist0 递减），否则是离开平面
            if (denom > 0f && dist0 > target) return -1f;
            return t;
        }

        /// <summary>两个运动球（相对运动化为射线 vs 静态球）。</summary>
        public static float SweptSphereSphere(RVec3 p0, RVec3 v0, float r0,
                                              RVec3 p1, RVec3 v1, float r1, float dt)
        {
            RVec3 dp = p0 - p1;
            RVec3 dv = v0 - v1;
            float R = r0 + r1;
            float a = dv.LengthSq();
            float c = dp.LengthSq() - R * R;
            if (c <= 0f) return 0f; // 初始已重叠
            if (a < 1e-12f) return -1f;
            float b = 2f * dp.Dot(dv);
            if (b >= 0f) return -1f; // 相互远离
            float disc = b * b - 4f * a * c;
            if (disc < 0f) return -1f;
            float t = (-b - MathF.Sqrt(disc)) / (2f * a);
            return (t >= 0f && t <= dt) ? t : -1f;
        }

        /// <summary>按 TOI 把一次位移拆成「碰撞前 + 碰撞后」，用于子步推进。</summary>
        public static float TimeOfImpactClamped(float toi, float dt)
            => toi < 0f ? dt : RealityKernels.Clamp(toi, 0f, dt);
    }
}
