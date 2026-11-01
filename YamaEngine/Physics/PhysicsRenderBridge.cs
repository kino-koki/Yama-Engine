// ==================================================================================================
//  PhysicsRenderBridge.cs — 物理→渲染桥接器
//
//  命名空间: YamaEngine.Physics
//  依赖:     PhysicsWorld, RigidBody, YamaEngine.Assets
//
//  功能:
//    封装物理世界到渲染场景对象的变换同步逻辑。
//    在物理步进后自动将 RigidBody 的 Position/Rotation 回写到 SceneObject,
//    使物理模拟结果在渲染视口中可见。
// ==================================================================================================

using System;
using System.Collections.Generic;
using YamaEngine.Assets;

namespace YamaEngine.Physics
{
    /// <summary>
    /// 物理→渲染桥接器。维护物理体索引与场景对象实例的映射,
    /// 在物理步进完成后批量同步变换数据。
    /// </summary>
    public sealed class PhysicsRenderBridge
    {
        private readonly PhysicsWorldReal _physicsWorld;
        private readonly AssetDatabase? _assets;

        /// <summary>物理体索引 → 场景对象实例的映射。</summary>
        private readonly Dictionary<int, SceneObject> _bodyToSceneObject = new();

        /// <summary>物理体索引 → 渲染目标的映射 (用于演示场景等无SceneObject的情况)。</summary>
        private readonly Dictionary<int, PhysicsRenderTarget> _bodyToRenderTarget = new();

        /// <summary>
        /// 创建物理→渲染桥接器。
        /// </summary>
        /// <param name="physicsWorld">物理世界实例。</param>
        /// <param name="assets">资产数据库 (可选, 用于查找场景对象)。</param>
        public PhysicsRenderBridge(PhysicsWorldReal physicsWorld, AssetDatabase? assets = null)
        {
            _physicsWorld = physicsWorld ?? throw new ArgumentNullException(nameof(physicsWorld));
            _assets = assets;
        }

        /// <summary>
        /// 注册物理体与场景对象的映射关系。
        /// </summary>
        /// <param name="bodyIndex">物理体在 PhysicsWorld 中的索引。</param>
        /// <param name="sceneObject">对应的场景对象。</param>
        public void Register(int bodyIndex, SceneObject sceneObject)
        {
            if (sceneObject == null) return;
            _bodyToSceneObject[bodyIndex] = sceneObject;
        }

        /// <summary>
        /// 注册物理体与渲染目标的映射关系 (用于演示场景等)。
        /// </summary>
        /// <param name="bodyIndex">物理体在 PhysicsWorld 中的索引。</param>
        /// <param name="renderTarget">对应的渲染目标。</param>
        public void Register(int bodyIndex, PhysicsRenderTarget renderTarget)
        {
            if (renderTarget == null) return;
            _bodyToRenderTarget[bodyIndex] = renderTarget;
        }

        /// <summary>
        /// 取消注册指定的物理体映射。
        /// </summary>
        public void Unregister(int bodyIndex)
        {
            _bodyToSceneObject.Remove(bodyIndex);
        }

        /// <summary>
        /// 清除所有映射。
        /// </summary>
        public void Clear()
        {
            _bodyToSceneObject.Clear();
        }

        /// <summary>
        /// 获取当前注册的映射数量。
        /// </summary>
        public int Count => _bodyToSceneObject.Count + _bodyToRenderTarget.Count;

        /// <summary>
        /// 批量同步: 将所有已注册物理体的变换回写到对应的场景对象或渲染目标。
        /// 应在 PhysicsWorld.Step() 之后调用。
        /// </summary>
        public void SyncTransforms()
        {
            // 同步到 SceneObject
            foreach (var kvp in _bodyToSceneObject)
            {
                int bodyIndex = kvp.Key;
                var sceneObj = kvp.Value;

                if (sceneObj == null) continue;
                if (bodyIndex < 0 || bodyIndex >= _physicsWorld.BodyCount) continue;

                var body = _physicsWorld.GetBody(bodyIndex);
                if (body == null) continue;

                // PhysicsVector3 → Vector3, PhysicsQuaternion → Quaternion (隐式转换)
                sceneObj.Position = body.Position;
                sceneObj.Rotation = body.Rotation;
            }

            // 同步到 PhysicsRenderTarget
            foreach (var kvp in _bodyToRenderTarget)
            {
                int bodyIndex = kvp.Key;
                var target = kvp.Value;

                if (target == null) continue;
                if (bodyIndex < 0 || bodyIndex >= _physicsWorld.BodyCount) continue;

                var body = _physicsWorld.GetBody(bodyIndex);
                if (body == null) continue;

                target.SetWorldTransform(body.Position, body.Rotation);
            }
        }
    }
}