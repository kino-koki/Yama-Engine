// ============================================================================================
//  OriginShiftCoordinator.cs — 自研创新 3：大世界流动原点，解决 float32 精度丢失。
//
//  痛点：Jolt 内部用 float32，世界坐标 >~10km 就抖。本协调器维护一个"流动原点"，
//  当焦点（相机/玩家）偏离当前原点超阈值时，整体重基址：所有 body 位置减去 delta，
//  速度不变。对玩法代码透明——玩法用"世界坐标"，协调器在传入 Jolt 前转成"原点相对坐标"。
//
//  用法：
//    var os = world.UseOriginShift();
//    每帧：os.UpdateFocus(cameraWorldPos);  // 自动重基址
//    查询/创建体时用 os.Local(worldPos) 得到 Jolt 坐标；os.World(joltPos) 转回世界坐标。
// ============================================================================================
using System;
using System.Numerics;

namespace ShanyuEngine.Physics
{
    /// <summary>大世界流动原点协调器。</summary>
    public sealed class OriginShiftCoordinator
    {
        private readonly PhysicsWorld _world;
        /// <summary>当前原点（世界坐标）。Jolt 内部存储的是 world - origin。</summary>
        public Vector3 Origin { get; private set; }
        /// <summary>触发重基址的距离阈值（米）。默认 2km。</summary>
        public float ShiftThreshold { get; set; } = 2000f;
        /// <summary>重基址时是否激活受影响的体。</summary>
        public bool ReactivateOnShift { get; set; } = false;

        public OriginShiftCoordinator(PhysicsWorld world, Vector3 initialOrigin)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            Origin = initialOrigin;
        }

        /// <summary>世界坐标 → Jolt 局部坐标（减去当前原点）。</summary>
        public Vector3 Local(Vector3 worldPos) => worldPos - Origin;
        /// <summary>Jolt 局部坐标 → 世界坐标（加回原点）。</summary>
        public Vector3 World(Vector3 localPos) => localPos + Origin;

        /// <summary>每帧用焦点世界坐标更新。偏离原点超阈值则整体重基址。</summary>
        public void UpdateFocus(Vector3 focusWorldPos)
        {
            float dist = (focusWorldPos - Origin).Length();
            if (dist > ShiftThreshold)
                ShiftOrigin(focusWorldPos);
        }

        /// <summary>把原点移到 newOriginWorld。所有 body 位置重基址（减去 delta），速度不变。</summary>
        public void ShiftOrigin(Vector3 newOriginWorld)
        {
            Vector3 delta = newOriginWorld - Origin;
            // 批量重基址：每个 body 位置减 delta，旋转不变
            foreach (uint id in _world.GetAllBodyIds())
            {
                if (!_world.IsValid(id)) continue;
                Vector3 pos = _world.GetPosition(id);
                Quaternion rot = _world.GetRotation(id);
                _world.SetPosition(id, pos - delta, rot, ReactivateOnShift);
            }
            Origin = newOriginWorld;
        }

        // PhysicsWorld.Step 前后钩子（当前无需操作，保留扩展点）
        internal void BeforeStep() { }
        internal void AfterStep() { }
    }
}
