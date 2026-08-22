// ============================================================================================
//  NavComponents.cs
//  山雨引擎 (ShanyuEngine) ECS 导航系统组件
// ============================================================================================
//  为 ECS 中的实体提供导航/寻路相关组件。支持 NavMesh、路径请求、避障等。
// ============================================================================================

using System.Collections.Generic;
using System.Numerics;

namespace ShanyuEngine.ECS
{
    /// <summary>导航代理配置。标记实体为可寻路的 Agent。</summary>
    public struct NavAgentComponent : IComponent
    {
        /// <summary>代理半径。</summary>
        public float Radius;

        /// <summary>代理高度。</summary>
        public float Height;

        /// <summary>最大速度。</summary>
        public float MaxSpeed;

        /// <summary>最大加速度。</summary>
        public float MaxAcceleration;

        /// <summary>旋转速度 (度/秒)。</summary>
        public float AngularSpeed;

        /// <summary>停止距离。</summary>
        public float StoppingDistance;

        /// <summary>避障优先级 (0=最优先)。</summary>
        public int AvoidancePriority;

        /// <summary>当前速度。</summary>
        public Vector3 Velocity;

        /// <summary>当前路径的剩余距离。</summary>
        public float RemainingDistance;

        /// <summary>是否已到达目的地。</summary>
        public bool HasReachedDestination;

        /// <summary>是否已启用导航。</summary>
        public bool IsActive;

        /// <summary>目标位置。</summary>
        public Vector3 Destination;

        /// <summary>路径点列表。</summary>
        public List<Vector3>? Path;
    }

    /// <summary>导航障碍物。阻挡 NavAgent 的通过。</summary>
    public struct NavObstacleComponent : IComponent
    {
        /// <summary>障碍物形状 (0=Box, 1=Capsule, 2=Mesh)。</summary>
        public int Shape;

        /// <summary>障碍物尺寸。</summary>
        public Vector3 Size;

        /// <summary>是否启用。</summary>
        public bool IsActive;
    }

    /// <summary>寻路请求（短期组件，完成后移除）。</summary>
    public struct PathRequestComponent : IComponent
    {
        /// <summary>起点。</summary>
        public Vector3 Start;

        /// <summary>终点。</summary>
        public Vector3 End;

        /// <summary>请求 ID。</summary>
        public int RequestId;

        /// <summary>是否已完成。</summary>
        public bool IsCompleted;

        /// <summary>结果路径。</summary>
        public List<Vector3>? ResultPath;

        /// <summary>是否成功。</summary>
        public bool FoundPath;
    }

    /// <summary>动态障碍物区域（标记区域使 NavMesh 重新生成）。</summary>
    public struct DynamicNavObstacleComponent : IComponent
    {
        /// <summary>影响的 NavMesh 区域类型。</summary>
        public int AreaType;

        /// <summary>影响范围。</summary>
        public float Radius;

        /// <summary>是否临时阻挡。</summary>
        public bool IsTemporary;
    }
}
