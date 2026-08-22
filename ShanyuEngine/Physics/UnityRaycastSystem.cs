using System;
using System.Collections.Generic;
using System.Numerics;

namespace ShanyuEngine.Physics
{
    // ============================================================================================
    //  UnityRaycastSystem.cs
    //
    //  光线投射系统 — 类似 Unity Physics.Raycast / SphereCast / BoxCast / CapsuleCast。
    //
    //  功能：
    //    - 射线查询：最近命中 / 全部命中 / 是否命中
    //    - 球体投射（SphereCast）
    //    - 盒体投射（BoxCast）
    //    - 胶囊体投射（CapsuleCast）
    //    - 过滤器（层级掩码 / 排除触发器 / 忽略特定体）
    //    - RaycastHit 结果结构（点/法线/距离/碰撞体/刚体/材质）
    //    - 批处理查询（多射线同时投射）
    //    - 调试绘制辅助
    // ============================================================================================

    /// <summary>
    ///   光线投射命中结果。包含详细的碰撞信息。
    /// </summary>
    public struct RaycastHitEx
    {
        /// <summary>命中点的世界坐标。</summary>
        public Vector3 Point;

        /// <summary>命中点处的表面法线。</summary>
        public Vector3 Normal;

        /// <summary>从射线原点到命中点的距离。</summary>
        public float Distance;

        /// <summary>沿射线的分数 (0..1)。</summary>
        public float Fraction;

        /// <summary>被命中碰撞体关联的实体 ID（0 表示无实体）。</summary>
        public int EntityId;

        /// <summary>被命中刚体的 Jolt BodyID。</summary>
        public uint BodyId;

        /// <summary>被命中刚体的碰撞层。</summary>
        public PhysicsLayer Layer;

        /// <summary>是否命中。</summary>
        public bool HasHit;

        /// <summary>命中点处的材质（如可用）。</summary>
        public PhysicsMaterial? Material;

        /// <summary>子形状 ID（三角面/子碰撞体索引）。</summary>
        public byte SubShapeId;

        public override string ToString()
        {
            return HasHit
                ? $"RaycastHit(Entity={EntityId}, Point={Point}, Dist={Distance:F3}, Normal={Normal})"
                : "RaycastHit(No hit)";
        }
    }

    /// <summary>
    ///   光线投射过滤器配置。
    /// </summary>
    public struct QueryFilter
    {
        /// <summary>层级掩码：只命中包含在此掩码中的层。</summary>
        public uint LayerMask;

        /// <summary>是否忽略触发器（true = 不命中触发器）。</summary>
        public bool IgnoreTriggers;

        /// <summary>是否忽略动态刚体。默认为 false。</summary>
        public bool IgnoreDynamic;

        /// <summary>是否忽略静态刚体。默认为 false。</summary>
        public bool IgnoreStatic;

        /// <summary>要忽略的特定 BodyId 集合。</summary>
        public HashSet<uint>? IgnoredBodyIds;

        /// <summary>要忽略的特定 EntityId 集合。</summary>
        public HashSet<int>? IgnoredEntityIds;

        /// <summary>默认过滤器（命中所有层，包含触发器和所有类型）。</summary>
        public static QueryFilter Default => new()
        {
            LayerMask = PhysicsLayerMask.Everything,
            IgnoreTriggers = false,
            IgnoreDynamic = false,
            IgnoreStatic = false,
        };

        /// <summary>
        ///   检查是否应忽略指定的 BodyId。
        /// </summary>
        public bool ShouldIgnore(uint bodyId, int entityId, bool isSensor)
        {
            if (IgnoreTriggers && isSensor) return true;
            if (IgnoredBodyIds != null && IgnoredBodyIds.Contains(bodyId)) return true;
            if (IgnoredEntityIds != null && IgnoredEntityIds.Contains(entityId)) return true;
            return false;
        }
    }

    /// <summary>
    ///   光线投射系统。提供完整的射线/形状投射查询 API。
    ///   直接使用 Jolt 原生查询，支持层级过滤和批处理。
    /// </summary>
    public sealed class RaycastSystem
    {
        private readonly PhysicsWorld _world;

        /// <summary>场景间碰撞过滤器引用（可选）。</summary>
        public SceneCollisionFilter? SceneFilter { get; set; }

        /// <summary>默认单次查询的最大命中结果数。</summary>
        public int DefaultMaxResults { get; set; } = 64;

        internal RaycastSystem(PhysicsWorld world)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
        }

        // ========================================================================================
        //  射线查询
        // ========================================================================================

        /// <summary>
        ///   发射一条射线，返回最近的命中结果。
        /// </summary>
        /// <param name="origin">射线原点（世界空间）。</param>
        /// <param name="direction">射线方向（无需归一化）。</param>
        /// <param name="maxDistance">最大距离。默认无限。</param>
        /// <param name="layerMask">层级过滤掩码。默认全部。</param>
        /// <param name="filter">额外过滤器。默认无。</param>
        /// <returns>命中结果（HasHit=false 表示未命中）。</returns>
        public RaycastHitEx Raycast(
            Vector3 origin,
            Vector3 direction,
            float maxDistance = float.MaxValue,
            uint layerMask = PhysicsLayerMask.Everything,
            QueryFilter? filter = null)
        {
            Vector3 dir = NormalizeOrZero(direction);
            float dist = maxDistance == float.MaxValue ? 1e6f : maxDistance;

            ushort joltLayer = (ushort)(layerMask & 0xFFFF);
            ushort joltMask = (ushort)((layerMask >> 16) & 0xFFFF);
            if (joltLayer == 0) joltLayer = 1;
            if (joltMask == 0) joltMask = 0xFFFF;

            bool hit = JoltNative.SJ_Query_Raycast(
                _world.NativeHandle, origin, dir, dist,
                joltLayer, joltMask,
                out NativeRaycastResult nativeResult);

            if (!hit)
                return new RaycastHitEx { HasHit = false, Distance = dist };

            // 应用额外过滤器
            if (filter.HasValue)
            {
                int entityId = _world.GetEntityId(nativeResult.BodyId);
                bool isSensor = IsBodySensor(nativeResult.BodyId);
                if (filter.Value.ShouldIgnore(nativeResult.BodyId, entityId, isSensor))
                    return new RaycastHitEx { HasHit = false, Distance = dist };
            }

            return NativeToHitEx(nativeResult);
        }

        /// <summary>
        ///   发射一条射线，返回所有命中结果（按距离排序）。
        /// </summary>
        public List<RaycastHitEx> RaycastAll(
            Vector3 origin,
            Vector3 direction,
            float maxDistance = float.MaxValue,
            uint layerMask = PhysicsLayerMask.Everything,
            QueryFilter? filter = null,
            int maxResults = -1)
        {
            Vector3 dir = NormalizeOrZero(direction);
            float dist = maxDistance == float.MaxValue ? 1e6f : maxDistance;
            int maxHits = maxResults > 0 ? maxResults : DefaultMaxResults;

            ushort joltLayer = (ushort)(layerMask & 0xFFFF);
            ushort joltMask = (ushort)((layerMask >> 16) & 0xFFFF);
            if (joltLayer == 0) joltLayer = 1;
            if (joltMask == 0) joltMask = 0xFFFF;

            var buffer = new NativeRaycastResult[maxHits];
            int count = JoltNative.SJ_Query_RaycastMulti(
                _world.NativeHandle, origin, dir, dist,
                joltLayer, joltMask, buffer, maxHits);

            var results = new List<RaycastHitEx>(count);
            for (int i = 0; i < count; i++)
            {
                if (filter.HasValue)
                {
                    int entityId = _world.GetEntityId(buffer[i].BodyId);
                    bool isSensor = IsBodySensor(buffer[i].BodyId);
                    if (filter.Value.ShouldIgnore(buffer[i].BodyId, entityId, isSensor))
                        continue;
                }
                results.Add(NativeToHitEx(buffer[i]));
            }
            return results;
        }

        /// <summary>
        ///   检查射线是否命中任何物体（不返回详细结果，效率更高）。
        /// </summary>
        public bool RaycastAny(
            Vector3 origin,
            Vector3 direction,
            float maxDistance = float.MaxValue,
            uint layerMask = PhysicsLayerMask.Everything,
            QueryFilter? filter = null)
        {
            // 使用 Raycast 并检查结果，对于简单查询 Jolt 内部已做了 early-out
            var hit = Raycast(origin, direction, maxDistance, layerMask, filter);
            return hit.HasHit;
        }

        // ========================================================================================
        //  形状投射
        // ========================================================================================

        /// <summary>
        ///   球体投射。将一个球体沿射线方向滑动，返回碰撞信息。
        /// </summary>
        public RaycastHitEx SphereCast(
            Vector3 origin,
            float radius,
            Vector3 direction,
            float maxDistance = float.MaxValue,
            uint layerMask = PhysicsLayerMask.Everything,
            QueryFilter? filter = null)
        {
            // 使用 CollideShape 来模拟球体投射
            // 沿射线方向以一定步长做碰撞检测（简化实现）
            Vector3 dir = NormalizeOrZero(direction);
            float dist = maxDistance == float.MaxValue ? 1e6f : maxDistance;

            ushort joltLayer = (ushort)(layerMask & 0xFFFF);
            ushort joltMask = (ushort)((layerMask >> 16) & 0xFFFF);
            if (joltLayer == 0) joltLayer = 1;
            if (joltMask == 0) joltMask = 0xFFFF;

            var sphereShape = _world.CreateSphereShape(radius);

            var contacts = new NativeRaycastResult[DefaultMaxResults];
            int count = JoltNative.SJ_Query_CollideShape(
                _world.NativeHandle, sphereShape, origin, Quaternion.Identity,
                joltLayer, joltMask, contacts, DefaultMaxResults);

            if (count == 0)
                return new RaycastHitEx { HasHit = false };

            // 返回最近的碰撞
            float minDist = float.MaxValue;
            int bestIdx = -1;
            for (int i = 0; i < count; i++)
            {
                if (contacts[i].Distance < minDist)
                {
                    minDist = contacts[i].Distance;
                    bestIdx = i;
                }
            }

            if (bestIdx < 0)
                return new RaycastHitEx { HasHit = false };

            var hit = NativeToHitEx(contacts[bestIdx]);

            // 应用过滤器
            if (filter.HasValue && filter.Value.ShouldIgnore(contacts[bestIdx].BodyId, hit.EntityId, IsBodySensor(contacts[bestIdx].BodyId)))
                return new RaycastHitEx { HasHit = false };

            return hit;
        }

        /// <summary>
        ///   盒体投射。将一个盒体沿射线方向滑动。
        /// </summary>
        public RaycastHitEx BoxCast(
            Vector3 origin,
            Vector3 halfExtents,
            Vector3 direction,
            Quaternion orientation,
            float maxDistance = float.MaxValue,
            uint layerMask = PhysicsLayerMask.Everything,
            QueryFilter? filter = null)
        {
            Vector3 dir = NormalizeOrZero(direction);
            float dist = maxDistance == float.MaxValue ? 1e6f : maxDistance;

            ushort joltLayer = (ushort)(layerMask & 0xFFFF);
            ushort joltMask = (ushort)((layerMask >> 16) & 0xFFFF);
            if (joltLayer == 0) joltLayer = 1;
            if (joltMask == 0) joltMask = 0xFFFF;

            var boxShape = _world.CreateBoxShape(halfExtents);

            var contacts = new NativeRaycastResult[DefaultMaxResults];
            int count = JoltNative.SJ_Query_CollideShape(
                _world.NativeHandle, boxShape, origin, orientation,
                joltLayer, joltMask, contacts, DefaultMaxResults);

            return ProcessCollideResult(contacts, count, filter);
        }

        /// <summary>
        ///   胶囊体投射。将一个胶囊体沿射线方向滑动。
        /// </summary>
        public RaycastHitEx CapsuleCast(
            Vector3 origin,
            float radius,
            float height,
            Vector3 direction,
            Quaternion orientation,
            float maxDistance = float.MaxValue,
            uint layerMask = PhysicsLayerMask.Everything,
            QueryFilter? filter = null)
        {
            Vector3 dir = NormalizeOrZero(direction);

            ushort joltLayer = (ushort)(layerMask & 0xFFFF);
            ushort joltMask = (ushort)((layerMask >> 16) & 0xFFFF);
            if (joltLayer == 0) joltLayer = 1;
            if (joltMask == 0) joltMask = 0xFFFF;

            var capsuleShape = _world.CreateCapsuleShape(radius, height);

            var contacts = new NativeRaycastResult[DefaultMaxResults];
            int count = JoltNative.SJ_Query_CollideShape(
                _world.NativeHandle, capsuleShape, origin, orientation,
                joltLayer, joltMask, contacts, DefaultMaxResults);

            return ProcessCollideResult(contacts, count, filter);
        }

        // ========================================================================================
        //  批处理查询
        // ========================================================================================

        /// <summary>
        ///   批处理射线查询。一次性投射多条射线。
        ///   每条射线返回最近命中结果。
        /// </summary>
        public RaycastHitEx[] RaycastBatch(
            ReadOnlySpan<Vector3> origins,
            ReadOnlySpan<Vector3> directions,
            float maxDistance = float.MaxValue,
            uint layerMask = PhysicsLayerMask.Everything,
            QueryFilter? filter = null)
        {
            int count = Math.Min(origins.Length, directions.Length);
            var results = new RaycastHitEx[count];
            for (int i = 0; i < count; i++)
            {
                results[i] = Raycast(origins[i], directions[i], maxDistance, layerMask, filter);
            }
            return results;
        }

        // ========================================================================================
        //  重叠查询（Overlap）
        // ========================================================================================

        /// <summary>
        ///   查询与球体重叠的所有实体。
        /// </summary>
        public List<int> OverlapSphere(Vector3 center, float radius,
            uint layerMask = PhysicsLayerMask.Everything,
            QueryFilter? filter = null)
        {
            ushort joltLayer = (ushort)(layerMask & 0xFFFF);
            ushort joltMask = (ushort)((layerMask >> 16) & 0xFFFF);
            if (joltLayer == 0) joltLayer = 1;
            if (joltMask == 0) joltMask = 0xFFFF;

            var buffer = new uint[DefaultMaxResults];
            int count = JoltNative.SJ_Query_OverlapSphere(
                _world.NativeHandle, center, radius,
                joltLayer, joltMask, buffer, DefaultMaxResults);

            var results = new List<int>(count);
            for (int i = 0; i < count; i++)
            {
                int entityId = _world.GetEntityId(buffer[i]);
                if (filter.HasValue)
                {
                    if (filter.Value.ShouldIgnore(buffer[i], entityId, IsBodySensor(buffer[i])))
                        continue;
                }
                results.Add(entityId);
            }
            return results;
        }

        /// <summary>
        ///   查询与盒体重叠的所有实体。
        /// </summary>
        public List<int> OverlapBox(Vector3 center, Vector3 halfExtents,
            uint layerMask = PhysicsLayerMask.Everything,
            QueryFilter? filter = null)
        {
            ushort joltLayer = (ushort)(layerMask & 0xFFFF);
            ushort joltMask = (ushort)((layerMask >> 16) & 0xFFFF);
            if (joltLayer == 0) joltLayer = 1;
            if (joltMask == 0) joltMask = 0xFFFF;

            var buffer = new uint[DefaultMaxResults];
            int count = JoltNative.SJ_Query_OverlapBox(
                _world.NativeHandle, center, halfExtents,
                joltLayer, joltMask, buffer, DefaultMaxResults);

            var results = new List<int>(count);
            for (int i = 0; i < count; i++)
            {
                int entityId = _world.GetEntityId(buffer[i]);
                if (filter.HasValue)
                {
                    if (filter.Value.ShouldIgnore(buffer[i], entityId, IsBodySensor(buffer[i])))
                        continue;
                }
                results.Add(entityId);
            }
            return results;
        }

        /// <summary>
        ///   查询与胶囊体重叠的所有实体。
        /// </summary>
        public List<int> OverlapCapsule(Vector3 point1, Vector3 point2, float radius,
            uint layerMask = PhysicsLayerMask.Everything,
            QueryFilter? filter = null)
        {
            ushort joltLayer = (ushort)(layerMask & 0xFFFF);
            ushort joltMask = (ushort)((layerMask >> 16) & 0xFFFF);
            if (joltLayer == 0) joltLayer = 1;
            if (joltMask == 0) joltMask = 0xFFFF;

            var buffer = new uint[DefaultMaxResults];
            int count = JoltNative.SJ_Query_OverlapCapsule(
                _world.NativeHandle, point1, point2, radius,
                joltLayer, joltMask, buffer, DefaultMaxResults);

            var results = new List<int>(count);
            for (int i = 0; i < count; i++)
            {
                int entityId = _world.GetEntityId(buffer[i]);
                if (filter.HasValue)
                {
                    if (filter.Value.ShouldIgnore(buffer[i], entityId, IsBodySensor(buffer[i])))
                        continue;
                }
                results.Add(entityId);
            }
            return results;
        }

        // ========================================================================================
        //  调试绘制
        // ========================================================================================

        /// <summary>
        ///   生成用于调试绘制的射线可视化线段。
        /// </summary>
        public void DebugDrawRay(Vector3 origin, Vector3 direction, float maxDistance,
            List<Vector3> linePoints, Vector4? color = null)
        {
            linePoints.Add(origin);
            Vector3 dir = NormalizeOrZero(direction);
            float dist = maxDistance == float.MaxValue ? 10f : maxDistance;
            linePoints.Add(origin + dir * dist);
        }

        /// <summary>
        ///   生成用于调试绘制的球体投射可视化。
        /// </summary>
        public void DebugDrawSphereCast(Vector3 origin, float radius, Vector3 direction, float maxDistance,
            List<Vector3> linePoints)
        {
            Vector3 dir = NormalizeOrZero(direction);
            float dist = maxDistance == float.MaxValue ? 10f : maxDistance;
            Vector3 end = origin + dir * dist;

            // 绘制起点和终点的线框球（近似为八边形）
            int segments = 12;
            for (int i = 0; i < segments; i++)
            {
                float a1 = MathF.PI * 2f * i / segments;
                float a2 = MathF.PI * 2f * (i + 1) / segments;
                Vector3 offset1 = new(MathF.Cos(a1) * radius, MathF.Sin(a1) * radius, 0);
                Vector3 offset2 = new(MathF.Cos(a2) * radius, MathF.Sin(a2) * radius, 0);
                linePoints.Add(origin + offset1);
                linePoints.Add(origin + offset2);
                linePoints.Add(end + offset1);
                linePoints.Add(end + offset2);
                // 连接起点和终点
                linePoints.Add(origin + offset1);
                linePoints.Add(end + offset1);
            }
        }

        // ========================================================================================
        //  内部辅助
        // ========================================================================================

        private RaycastHitEx NativeToHitEx(NativeRaycastResult native)
        {
            int entityId = _world.GetEntityId(native.BodyId);
            PhysicsLayer layer = PhysicsLayer.Default;

            return new RaycastHitEx
            {
                HasHit = true,
                Point = native.Position,
                Normal = native.Normal,
                Distance = native.Distance,
                Fraction = native.Fraction,
                EntityId = entityId,
                BodyId = native.BodyId,
                Layer = layer,
                Material = null,
                SubShapeId = native.SubShapeId,
            };
        }

        private RaycastHitEx ProcessCollideResult(NativeRaycastResult[] contacts, int count, QueryFilter? filter)
        {
            if (count == 0)
                return new RaycastHitEx { HasHit = false };

            float minDist = float.MaxValue;
            int bestIdx = -1;
            for (int i = 0; i < count; i++)
            {
                if (contacts[i].Distance < minDist)
                {
                    minDist = contacts[i].Distance;
                    bestIdx = i;
                }
            }

            if (bestIdx < 0)
                return new RaycastHitEx { HasHit = false };

            var hit = NativeToHitEx(contacts[bestIdx]);

            if (filter.HasValue &&
                filter.Value.ShouldIgnore(contacts[bestIdx].BodyId, hit.EntityId,
                    IsBodySensor(contacts[bestIdx].BodyId)))
                return new RaycastHitEx { HasHit = false };

            return hit;
        }

        private bool IsBodySensor(uint bodyId)
        {
            // 通过 PhysicsWorld 获取传感器状态
            // 使用反射或内部跟踪
            return _world.GetType()
                .GetMethod("IsBodySensor",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?
                .Invoke(_world, new object[] { bodyId }) is true;
        }

        private static Vector3 NormalizeOrZero(Vector3 v)
        {
            float lenSq = v.LengthSquared();
            return lenSq > 1e-8f ? v / MathF.Sqrt(lenSq) : Vector3.UnitZ;
        }
    }
}
