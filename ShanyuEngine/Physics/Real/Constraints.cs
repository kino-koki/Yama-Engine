using System;
using System.Numerics;

namespace ShanyuEngine.Physics.Real
{
    /// <summary>
    /// 约束基类
    /// </summary>

    /// <summary>
    /// 铰链约束 - 限制物体绕特定轴旋转
    /// </summary>

    /// <summary>
    /// 弹簧约束 - 类似于弹簧的弹性连接
    /// </summary>
    public sealed class SpringConstraint : Constraint
    {
        private Vector3 _anchorA;      // 刚体A上的锚点（局部空间）
        private Vector3 _anchorB;      // 刚体B上的锚点（局部空间）
        private float _stiffness;      // 刚度系数
        private float _damping;        // 阻尼系数
        private float _restLength;     // 静态长度
        
        public SpringConstraint(Rigidbody? bodyA, Rigidbody? bodyB, Vector3 anchorA, Vector3 anchorB, float stiffness, float damping)
            : base(bodyA, bodyB)
        {
            _anchorA = anchorA;
            _anchorB = anchorB;
            _stiffness = stiffness;
            _damping = damping;
            _restLength = Vector3.Distance(anchorA, anchorB);
        }
        
        public void SetStiffness(float stiffness)
        {
            _stiffness = MathF.Max(0, stiffness);
        }
        
        public void SetDamping(float damping)
        {
            _damping = MathF.Max(0, damping);
        }
        
        public void SetRestLength(float length)
        {
            _restLength = MathF.Max(0, length);
        }
        
        public override void Solve(float deltaTime)
        {
            if (!Enabled)
                return;
                
            // 计算世界空间锚点和速度
            var worldAnchorA = BodyA != null ? 
                Vector3.Transform(_anchorA, BodyA.WorldTransform) : _anchorA;
            var worldAnchorB = BodyB != null ? 
                Vector3.Transform(_anchorB, BodyB.WorldTransform) : _anchorB;
                
            var velocityA = BodyA?.GetVelocityAtPoint(worldAnchorA) ?? Vector3.Zero;
            var velocityB = BodyB?.GetVelocityAtPoint(worldAnchorB) ?? Vector3.Zero;
            
            // 计算弹簧力
            Vector3 displacement = worldAnchorB - worldAnchorA;
            float currentLength = displacement.Length();
            
            if (currentLength < 1e-6f)
                return;
                
            Vector3 direction = displacement / currentLength;
            
            // 胡克定律 + 阻尼
            float extension = currentLength - _restLength;
            float springForce = _stiffness * extension;
            
            Vector3 relativeVelocity = velocityB - velocityA;
            float dampingForce = _damping * Vector3.Dot(relativeVelocity, direction);
            
            float totalForce = springForce + dampingForce;
            Vector3 force = direction * totalForce;
            
            // 应用力
            if (BodyA != null && !BodyA.IsStatic)
            {
                Vector3 torqueA = Vector3.Cross(worldAnchorA - BodyA.Position, force);
                BodyA.Force += force;
                BodyA.Torque += torqueA;
                BodyA.WakeUp();
            }
            
            if (BodyB != null && !BodyB.IsStatic)
            {
                Vector3 torqueB = Vector3.Cross(worldAnchorB - BodyB.Position, -force);
                BodyB.Force -= force;
                BodyB.Torque += torqueB;
                BodyB.WakeUp();
            }
        }
    }

    /// <summary>
    /// 求解器 - 协调所有约束的求解
    /// </summary>
    public sealed class Solver
    {
        private readonly PhysicsSettings _settings;
        private readonly float _contactErp;
        private readonly float _contactSlop;
        
        public Solver(PhysicsSettings settings)
        {
            _settings = settings;
            _contactErp = settings.ContactErp;
            _contactSlop = settings.ContactSlop;
        }
        
        /// <summary>
        /// 预处理约束
        /// </summary>
        public void PreSolve(List<Constraint> constraints, float deltaTime)
        {
            foreach (var constraint in constraints)
            {
                if (constraint.Enabled)
                {
                    constraint.Solve(deltaTime);
                }
            }
        }
        
        /// <summary>
        /// 迭代求解约束
        /// </summary>
        public void Iterate(List<Constraint> constraints, float deltaTime, int iterations)
        {
            for (int i = 0; i < iterations; i++)
            {
                PreSolve(constraints, deltaTime);
            }
        }
    }
}