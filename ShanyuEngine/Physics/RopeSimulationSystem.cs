using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ShanyuEngine.Physics
{
    // ============================================================================================
    //  RopeSimulationSystem.cs
    //
    //  绳索模拟系统。绳索本质上是1D的质点-弹簧链，用Verlet积分+距离约束(PBD)实现。
    //  和布料类似但更简单，因为是1维的。
    //
    //  特殊处理：
    //    - 长度约束：相邻质点距离固定，但允许整体拉伸(刚度<1)
    //    - 最长距离约束：绳子不能无限拉伸，超过max length就硬性截断
    //    - 弯曲约束：可选，让绳子不容易打结
    //    - 自碰撞：可选，绳子和自己不交叉
    //
    //  注意：质量不能为0，约束迭代次数影响刚度
    //  TODO: 绳子绕圆柱的缠绕还没做，需要曲线-曲面碰撞
    // ============================================================================================

    /// <summary>绳索节点。</summary>
    public struct RopeNode
    {
        public Vector3 Position;
        public Vector3 PreviousPosition;
        public float Mass;
        public float InvMass;
        public bool Pinned;
        public Vector3 PinnedPosition;
        public float Radius;
    }

    /// <summary>绳索段(相邻节点间的约束)。</summary>
    public struct RopeSegment
    {
        public int NodeA;
        public int NodeB;
        public float RestLength;
        public float Stiffness;     // 0~1
        public float MaxLength;     // 最大允许长度(防过度拉伸)
        public bool Broken;         // 断裂标记
    }

    /// <summary>
    /// 绳索模拟系统主类。
    /// </summary>
    public sealed class RopeSimulationSystem
    {
        private readonly List<RopeNode> _nodes = new(128);
        private readonly List<RopeSegment> _segments = new(128);

        private Vector3 _gravity = new(0, -9.81f, 0);
        private float _damping = 0.02f;
        private float _wind = 0f;
        private Vector3 _windDir = new(1, 0, 0);

        private int _solverIterations = 16;
        private int _subSteps = 4;
        private float _groundY = float.MinValue;

        // 弯曲约束
        private bool _enableBendConstraint = false;
        private float _bendStiffness = 0.1f;

        public int NodeCount => _nodes.Count;
        public int SegmentCount => _segments.Count;

        // =========================================================================
        //  参数
        // =========================================================================

        public void SetGravity(Vector3 g) => _gravity = g;
        public void SetDamping(float d) => _damping = Math.Clamp(d, 0f, 1f);
        public void SetWind(float strength, Vector3 dir)
        {
            _wind = strength;
            _windDir = Vector3.Normalize(dir);
        }
        public void SetSolverIterations(int n) => _solverIterations = Math.Clamp(n, 1, 128);
        public void SetSubSteps(int n) => _subSteps = Math.Clamp(n, 1, 32);
        public void SetGround(float y) => _groundY = y;
        public void EnableBendConstraint(bool enable, float stiffness = 0.1f)
        {
            _enableBendConstraint = enable;
            _bendStiffness = stiffness;
        }

        // =========================================================================
        //  创建绳索
        // =========================================================================

        /// <summary>从头创建绳索，一节一节加。</summary>
        public int AddNode(Vector3 position, float mass = 0.1f, float radius = 0.02f)
        {
            // 注意：质量不能为0
            mass = MathF.Max(mass, 0.001f);
            _nodes.Add(new RopeNode
            {
                Position = position,
                PreviousPosition = position,
                Mass = mass,
                InvMass = 1f / mass,
                Radius = radius
            });
            return _nodes.Count - 1;
        }

        public void AddSegment(int a, int b, float stiffness = 1f, float maxLengthFactor = 1.1f)
        {
            if (a < 0 || b < 0 || a >= _nodes.Count || b >= _nodes.Count) return;
            float rest = Vector3.Distance(_nodes[a].Position, _nodes[b].Position);
            _segments.Add(new RopeSegment
            {
                NodeA = a,
                NodeB = b,
                RestLength = rest,
                Stiffness = stiffness,
                MaxLength = rest * maxLengthFactor,
                Broken = false
            });
        }

        /// <summary>
        /// 一次性创建一条直线绳索。
        /// </summary>
        public void CreateRope(Vector3 start, Vector3 end, int segments,
            float totalMass, float stiffness, bool pinStart = true, bool pinEnd = false)
        {
            _nodes.Clear();
            _segments.Clear();

            float perMass = totalMass / (segments + 1);
            Vector3 step = (end - start) / segments;

            for (int i = 0; i <= segments; i++)
            {
                Vector3 pos = start + step * i;
                AddNode(pos, perMass);
            }

            for (int i = 0; i < segments; i++)
                AddSegment(i, i + 1, stiffness);

            if (pinStart) PinNode(0);
            if (pinEnd) PinNode(segments);
        }

        /// <summary>创建一条悬垂的绳子(两端固定，中间下垂)。</summary>
        public void CreateHangingRope(Vector3 anchorA, Vector3 anchorB, int segments,
            float totalMass, float stiffness)
        {
            CreateRope(anchorA, anchorB, segments, totalMass, stiffness, true, true);

            // 给每个中间节点加一点向下的初始位置，让它自然下垂
            Vector3 mid = (anchorA + anchorB) * 0.5f;
            float sag = Vector3.Distance(anchorA, anchorB) * 0.1f;
            for (int i = 1; i < segments; i++)
            {
                float t = (float)i / segments;
                Vector3 basePos = Vector3.Lerp(anchorA, anchorB, t);
                // 二次曲线下垂
                float dip = 4f * t * (1f - t) * sag;
                var p = _nodes[i];
                p.Position = basePos - new Vector3(0, dip, 0);
                p.PreviousPosition = p.Position;
                _nodes[i] = p;
            }
        }

        /// <summary>创建一条鞭子(一头粗一头细)。</summary>
        public void CreateWhip(Vector3 handle, Vector3 tipDir, int segments,
            float handleMass, float tipMass, float stiffness)
        {
            _nodes.Clear();
            _segments.Clear();

            Vector3 step = Vector3.Normalize(tipDir);
            for (int i = 0; i <= segments; i++)
            {
                float t = (float)i / segments;
                float mass = MathHelper.Lerp(handleMass, tipMass, t);
                Vector3 pos = handle + step * (i * 0.1f);
                AddNode(pos, mass, MathHelper.Lerp(0.03f, 0.005f, t));
            }

            for (int i = 0; i < segments; i++)
                AddSegment(i, i + 1, stiffness);

            PinNode(0);
        }

        // =========================================================================
        //  模拟
        // =========================================================================

        public void Step(float dt)
        {
            if (_nodes.Count == 0) return;
            float subDt = dt / _subSteps;
            for (int i = 0; i < _subSteps; i++)
                StepOnce(subDt);
        }

        private void StepOnce(float dt)
        {
            var span = CollectionsMarshal.AsSpan(_nodes);

            // 1) Verlet预测
            PredictPositions(span, dt);

            // 2) 距离约束求解
            for (int iter = 0; iter < _solverIterations; iter++)
            {
                SolveDistanceConstraints(span);

                if (_enableBendConstraint)
                    SolveBendConstraints(span);
            }

            // 3) 外部碰撞
            SolveExternalCollisions(span);

            // 4) 钉住的节点
            for (int i = 0; i < span.Length; i++)
            {
                if (span[i].Pinned)
                {
                    span[i].Position = span[i].PinnedPosition;
                    span[i].PreviousPosition = span[i].PinnedPosition;
                }
            }
        }

        private void PredictPositions(Span<RopeNode> nodes, float dt)
        {
            float dt2 = dt * dt;
            float dampFactor = 1f - _damping;

            for (int i = 0; i < nodes.Length; i++)
            {
                ref var n = ref nodes[i];
                if (n.Pinned) continue;

                Vector3 velocity = (n.Position - n.PreviousPosition) * dampFactor;
                Vector3 newPos = n.Position + velocity + _gravity * dt2;

                // 风
                if (_wind > 0)
                    newPos += _windDir * _wind * dt2 * (1f - (float)i / nodes.Length * 0.5f);

                n.PreviousPosition = n.Position;
                n.Position = newPos;
            }
        }

        private void SolveDistanceConstraints(Span<RopeNode> nodes)
        {
            var segments = CollectionsMarshal.AsSpan(_segments);
            for (int i = 0; i < segments.Length; i++)
            {
                ref var s = ref segments[i];
                if (s.Broken) continue;

                ref var a = ref nodes[s.NodeA];
                ref var b = ref nodes[s.NodeB];

                Vector3 delta = b.Position - a.Position;
                float dist = delta.Length();
                if (dist < 1e-6f) continue;

                // 检查是否超过最大长度(断裂)
                if (dist > s.MaxLength)
                {
                    s.Broken = true;
                    continue;
                }

                float totalInvMass = a.InvMass + b.InvMass;
                if (totalInvMass < 1e-8f) continue;

                float diff = (dist - s.RestLength) / dist;
                float correction = diff * s.Stiffness;
                Vector3 offset = delta * correction;

                if (!a.Pinned)
                    a.Position += offset * (a.InvMass / totalInvMass);
                if (!b.Pinned)
                    b.Position -= offset * (b.InvMass / totalInvMass);
            }
        }

        /// <summary>
        /// 弯曲约束：让连续3个节点尽量保持直线，减少打结。
        /// 对每3个连续节点(i, i+1, i+2)，约束i和i+2的距离接近|i+1 - i| + |i+2 - i+1|。
        /// </summary>
        private void SolveBendConstraints(Span<RopeNode> nodes)
        {
            // 遍历连续的3节点组
            var segments = CollectionsMarshal.AsSpan(_segments);
            for (int i = 0; i < segments.Length - 1; i++)
            {
                ref var s1 = ref segments[i];
                ref var s2 = ref segments[i + 1];
                if (s1.Broken || s2.Broken) continue;

                int i0 = s1.NodeA;
                int i1 = s1.NodeB; // == s2.NodeA
                int i2 = s2.NodeB;

                ref var p0 = ref nodes[i0];
                ref var p1 = ref nodes[i1];
                ref var p2 = ref nodes[i2];

                // 期望的i0-i2距离 = s1.RestLength + s2.RestLength(直线情况)
                float restDist = s1.RestLength + s2.RestLength;
                Vector3 delta = p2.Position - p0.Position;
                float dist = delta.Length();
                if (dist < 1e-6f) continue;

                // 只在弯曲时(距离小于直线距离)施加，拉伸不约束
                if (dist >= restDist) continue;

                float diff = (dist - restDist) / dist * _bendStiffness;
                Vector3 offset = delta * diff;

                float totalInvMass = p0.InvMass + p1.InvMass * 2f + p2.InvMass;
                if (totalInvMass < 1e-8f) continue;

                if (!p0.Pinned)
                    p0.Position -= offset * (p0.InvMass / totalInvMass);
                if (!p1.Pinned)
                    p1.Position += offset * 0f; // 中间节点不修(简化)
                if (!p2.Pinned)
                    p2.Position += offset * (p2.InvMass / totalInvMass);
            }
        }

        private void SolveExternalCollisions(Span<RopeNode> nodes)
        {
            // 地面
            if (_groundY > float.MinValue)
            {
                for (int i = 0; i < nodes.Length; i++)
                {
                    if (nodes[i].Position.Y < _groundY + nodes[i].Radius)
                    {
                        nodes[i].Position = nodes[i].Position with { Y = _groundY + nodes[i].Radius };
                        // 摩擦：减少水平方向"速度"
                        Vector3 vel = nodes[i].Position - nodes[i].PreviousPosition;
                        nodes[i].PreviousPosition = nodes[i].Position - new Vector3(vel.X * 0.3f, vel.Y, vel.Z * 0.3f);
                    }
                }
            }
        }

        // =========================================================================
        //  外部API
        // =========================================================================

        public Vector3 GetNodePosition(int idx)
        {
            if (idx < 0 || idx >= _nodes.Count) return Vector3.Zero;
            return _nodes[idx].Position;
        }

        public void SetNodePosition(int idx, Vector3 pos)
        {
            if (idx < 0 || idx >= _nodes.Count) return;
            var n = _nodes[idx];
            n.Position = pos;
            n.PreviousPosition = pos;
            _nodes[idx] = n;
        }

        public void PinNode(int idx)
        {
            if (idx < 0 || idx >= _nodes.Count) return;
            var n = _nodes[idx];
            n.Pinned = true;
            n.PinnedPosition = n.Position;
            n.InvMass = 0f;
            _nodes[idx] = n;
        }

        public void UnpinNode(int idx)
        {
            if (idx < 0 || idx >= _nodes.Count) return;
            var n = _nodes[idx];
            n.Pinned = false;
            n.InvMass = n.Mass > 0f ? 1f / n.Mass : 0f;
            _nodes[idx] = n;
        }

        /// <summary>施加冲量(鞭打效果)。</summary>
        public void ApplyImpulse(int idx, Vector3 impulse)
        {
            if (idx < 0 || idx >= _nodes.Count) return;
            var n = _nodes[idx];
            if (n.Pinned) return;
            n.PreviousPosition = n.Position - impulse * 0.016f;
            _nodes[idx] = n;
        }

        /// <summary>施加一个力到某个节点。</summary>
        public void ApplyForce(int idx, Vector3 force, float dt)
        {
            if (idx < 0 || idx >= _nodes.Count) return;
            var n = _nodes[idx];
            if (n.Pinned) return;
            n.Position += force * n.InvMass * dt * dt;
            _nodes[idx] = n;
        }

        /// <summary>获取所有节点位置(渲染用)。</summary>
        public IEnumerable<Vector3> GetPositions()
        {
            foreach (var n in _nodes)
                yield return n.Position;
        }

        /// <summary>获取所有线段(渲染用)。</summary>
        public IEnumerable<(Vector3 a, Vector3 b, bool broken)> GetSegments()
        {
            foreach (var s in _segments)
                yield return (_nodes[s.NodeA].Position, _nodes[s.NodeB].Position, s.Broken);
        }

        /// <summary>获取绳子的总长度(当前实际长度)。</summary>
        public float GetCurrentLength()
        {
            float total = 0f;
            foreach (var s in _segments)
            {
                if (s.Broken) continue;
                total += Vector3.Distance(_nodes[s.NodeA].Position, _nodes[s.NodeB].Position);
            }
            return total;
        }

        /// <summary>获取绳子的静止长度。</summary>
        public float GetRestLength()
        {
            float total = 0f;
            foreach (var s in _segments)
                total += s.RestLength;
            return total;
        }

        /// <summary>判断绳子是否已断。</summary>
        public bool IsBroken()
        {
            foreach (var s in _segments)
                if (s.Broken) return true;
            return false;
        }

        /// <summary>切断绳子在某段。</summary>
        public void Cut(int segmentIdx)
        {
            if (segmentIdx < 0 || segmentIdx >= _segments.Count) return;
            var s = _segments[segmentIdx];
            _segments[segmentIdx] = s with { Broken = true };
        }

        /// <summary>在某个位置切断绳子。</summary>
        public void CutAt(Vector3 worldPos, float threshold = 0.1f)
        {
            for (int i = 0; i < _segments.Count; i++)
            {
                if (_segments[i].Broken) continue;
                var a = _nodes[_segments[i].NodeA].Position;
                var b = _nodes[_segments[i].NodeB].Position;
                Vector3 closest = ClosestPointOnSegment(worldPos, a, b);
                if (Vector3.DistanceSquared(worldPos, closest) < threshold * threshold)
                {
                    Cut(i);
                    return;
                }
            }
        }

        public void Clear()
        {
            _nodes.Clear();
            _segments.Clear();
        }

        // =========================================================================
        //  和刚体碰撞
        // =========================================================================

        /// <summary>绳子和球体碰撞。</summary>
        public void CollideWithSphere(Vector3 center, float radius, float friction = 0.2f)
        {
            var span = CollectionsMarshal.AsSpan(_nodes);
            for (int i = 0; i < span.Length; i++)
            {
                ref var n = ref span[i];
                Vector3 d = n.Position - center;
                float dist = d.Length();
                if (dist < radius + n.Radius && dist > 1e-6f)
                {
                    Vector3 normal = d / dist;
                    n.Position = center + normal * (radius + n.Radius);
                    // 摩擦
                    Vector3 vel = n.Position - n.PreviousPosition;
                    Vector3 tan = vel - normal * Vector3.Dot(vel, normal);
                    n.PreviousPosition = n.Position - (vel - tan * friction);
                }
            }
        }

        /// <summary>绳子和胶囊体(圆柱+两端球)碰撞。常用于绳子绕杆子缠绕。</summary>
        public void CollideWithCapsule(Vector3 p0, Vector3 p1, float radius, float friction = 0.2f)
        {
            var span = CollectionsMarshal.AsSpan(_nodes);
            for (int i = 0; i < span.Length; i++)
            {
                ref var n = ref span[i];
                Vector3 closest = ClosestPointOnSegment(n.Position, p0, p1);
                Vector3 d = n.Position - closest;
                float dist = d.Length();
                if (dist < radius + n.Radius && dist > 1e-6f)
                {
                    Vector3 normal = d / dist;
                    n.Position = closest + normal * (radius + n.Radius);
                    Vector3 vel = n.Position - n.PreviousPosition;
                    Vector3 tan = vel - normal * Vector3.Dot(vel, normal);
                    n.PreviousPosition = n.Position - (vel - tan * friction);
                }
            }
        }

        /// <summary>绳子和无限平面碰撞。</summary>
        public void CollideWithPlane(Vector3 point, Vector3 normal, float friction = 0.2f)
        {
            normal = Vector3.Normalize(normal);
            var span = CollectionsMarshal.AsSpan(_nodes);
            for (int i = 0; i < span.Length; i++)
            {
                ref var n = ref span[i];
                float dist = Vector3.Dot(n.Position - point, normal);
                if (dist < n.Radius)
                {
                    n.Position -= normal * (dist - n.Radius);
                    Vector3 vel = n.Position - n.PreviousPosition;
                    Vector3 tan = vel - normal * Vector3.Dot(vel, normal);
                    n.PreviousPosition = n.Position - (vel - tan * friction);
                }
            }
        }

        // =========================================================================
        //  辅助
        // =========================================================================

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector3 ClosestPointOnSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float t = Vector3.Dot(p - a, ab) / ab.LengthSquared();
            t = Math.Clamp(t, 0f, 1f);
            return a + ab * t;
        }

        /// <summary>算绳子上某个参数位置的切线方向(渲染/挂东西用)。</summary>
        public Vector3 GetTangentAt(float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            float total = GetRestLength();
            if (total < 1e-6f) return Vector3.UnitY;

            float target = t * total;
            float acc = 0f;
            for (int i = 0; i < _segments.Count; i++)
            {
                if (_segments[i].Broken) continue;
                float segLen = _segments[i].RestLength;
                if (acc + segLen >= target)
                {
                    var a = _nodes[_segments[i].NodeA].Position;
                    var b = _nodes[_segments[i].NodeB].Position;
                    Vector3 dir = b - a;
                    if (dir.LengthSquared() < 1e-10f) return Vector3.UnitY;
                    return Vector3.Normalize(dir);
                }
                acc += segLen;
            }
            return Vector3.UnitY;
        }

        /// <summary>算绳子上某个参数位置的世界坐标。</summary>
        public Vector3 GetPositionAt(float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            float total = GetRestLength();
            if (total < 1e-6f) return _nodes.Count > 0 ? _nodes[0].Position : Vector3.Zero;

            float target = t * total;
            float acc = 0f;
            for (int i = 0; i < _segments.Count; i++)
            {
                if (_segments[i].Broken) continue;
                float segLen = _segments[i].RestLength;
                if (acc + segLen >= target)
                {
                    float localT = (target - acc) / segLen;
                    var a = _nodes[_segments[i].NodeA].Position;
                    var b = _nodes[_segments[i].NodeB].Position;
                    return Vector3.Lerp(a, b, localT);
                }
                acc += segLen;
            }
            return _nodes[^1].Position;
        }
    }

    /// <summary>数学辅助(避免和引擎其他模块的MathHelper冲突，这里简单内联)。</summary>
}
