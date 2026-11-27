using System;
namespace YamaEngine.Reality
{
    /// <summary>单帧管线观测值。</summary>
    public readonly struct YrFrameStats
    {
        public readonly long Tick;
        public readonly int BroadPhasePairs;
        public readonly int HitPairs;
        public readonly int ContactPoints;
        public readonly int IslandCount;
        public readonly float EarliestToi;
        public readonly double LedgerResidual;
        public YrFrameStats(long tick, int broad, int hit, int pts, int islands,
                            float toi, double residual)
        { Tick = tick; BroadPhasePairs = broad; HitPairs = hit; ContactPoints = pts;
          IslandCount = islands; EarliestToi = toi; LedgerResidual = residual; }
    }

    /// <summary>
    /// 统一物理管线（引擎级编排）：把前四轮各自正确的组件接成一个 Godot
    /// PhysicsServer 式的单一入口。
    ///
    ///   固定步时钟 → 同步 OBB → 宽相 SAP → 窄相(CCD 预筛/SAT/流形)
    ///   → 约束岛 → 序列冲量接触 → 关节 → 积分 → 休眠 → 位级快照
    ///
    /// 全部状态以 float 存储、全部遍历显式定序；Snapshot/Restore 支持
    /// 位级校验和回滚重放。账本只做观测，不参与伪造守恒。
    /// </summary>
    public sealed class YrPhysicsPipeline
    {
        private readonly RigidBody3D[] _bodies;
        private readonly RVec3[] _halfExtents;
        private readonly YrObb[] _obbs;
        private readonly YrFixedClock _clock = new YrFixedClock(1.0 / 60.0, 8);
        private readonly YrIslandSolver _islands = new YrIslandSolver();
        private readonly YrContactSolver _contacts = new YrContactSolver();
        private readonly YrJointSolver _joints = new YrJointSolver();
        private readonly YrNarrowPhase _narrow = new YrNarrowPhase();
        private readonly YrRollbackBuffer _rollback = new YrRollbackBuffer();
        private readonly YrToiAdvance _toi = new YrToiAdvance();
        private readonly ConservationLedger _ledger = new ConservationLedger();
        private long _tick;
        private YrFrameStats _lastStats;

        public YrPhysicsPipeline(RigidBody3D[] bodies, RVec3[] halfExtents)
        {
            _bodies = bodies ?? Array.Empty<RigidBody3D>();
            _halfExtents = halfExtents ?? Array.Empty<RVec3>();
            if (_halfExtents.Length < _bodies.Length)
                throw new ArgumentException("每个刚体必须给出 OBB 半尺寸");
            _obbs = new YrObb[_bodies.Length];
            SyncObbs();
            _contacts.SetBodies(_bodies);
            _joints.SetBodies(_bodies);
        }

        public RigidBody3D[] Bodies => _bodies;
        public YrObb[] Obbs => _obbs;
        public YrFixedClock Clock => _clock;
        public YrIslandSolver Islands => _islands;
        public YrNarrowPhase Narrow => _narrow;
        public YrRollbackBuffer Rollback => _rollback;
        public ConservationLedger Ledger => _ledger;
        public long Tick => _tick;
        public YrFrameStats LastStats => _lastStats;

        /// <summary>是否启用按岛休眠；关闭后所有刚体每步都积分（便于守恒断言）。</summary>
        public bool SleepEnabled { get; set; } = true;
        public float SleepSpeedThreshold { get; set; } = 0.01f;
        public int SleepFrames { get; set; } = 60;
        public int SolverIterations { get; set; } = 10;
        public YrSurface Surface { get; set; } = YrSurface.Default;
        /// <summary>
        /// 穿透位置投影强度。默认 0.4；设 0 可关闭。
        /// 注意：位置瞬移是非物理修正，会改变轨道角动量项 r×(mv)，
        /// 因此严格角动量守恒要求 beta=0（见第五轮文档）。
        /// </summary>
        public float PositionCorrectionBeta { get; set; } = 0.4f;
        /// <summary>
        /// 启用连续碰撞子步回退。默认 false，保持原有 65 项回归语义不变；
        /// 设为 true 后 Step(realDt) 走 StepFixedToi：按 TOI 拆步、纯速度级冲量、
        /// 不做位置投影，严格守恒总线动量与总角动量。
        /// </summary>
        public bool UseToiSubsteps { get; set; } = false;
        public int ToiMaxSubsteps { get; set; } = 8;
        /// <summary>
        /// 用扫掠 AABB BVH 的候选对替代 O(n²) 全对 CCD。默认 false，
        /// 保持第六轮语义；开启后 LastCcdPairs 可观测候选对数量。
        /// </summary>
        public bool UseBvhCcdPairs { get; set; } = false;
        /// <summary>启用角速度保守膨胀 CCD（默认 false）。</summary>
        public bool UseAngularSweep { get; set; } = false;
        /// <summary>窄相使用 Sutherland-Hodgman 面裁剪流形（默认 false）。</summary>
        public bool UseFaceClipping
        {
            get => _narrow.UseFaceClipping;
            set => _narrow.UseFaceClipping = value;
        }
        public int LastCcdPairs => _toi.LastCcdPairs;
        public int LastToiSubsteps => _toi.LastSubsteps;
        public int LastToiHits => _toi.LastHits;
        public int LastToiContactPoints => _toi.LastContactPoints;

        public void SetJoints(YrJoint[] joints) => _joints.SetJoints(joints ?? Array.Empty<YrJoint>());

        private void SyncObbs()
        {
            for (int i = 0; i < _bodies.Length; i++)
                _obbs[i] = new YrObb(_bodies[i].Position, _bodies[i].Orientation,
                                     _halfExtents[i]);
        }

        /// <summary>真实帧 dt → 固定步数（Godot `_physics_process` 语义）。</summary>
        public int Step(double realDt)
        {
            int steps = _clock.Advance(realDt);
            for (int s = 0; s < steps; s++)
            {
                if (UseToiSubsteps) StepFixedToi(_clock.FixedDt);
                else StepFixed(_clock.FixedDt);
            }
            return steps;
        }

        /// <summary>
        /// 连续碰撞子步路径：不做位置投影，严格守恒。
        /// 与 StepFixed 并列存在，避免改动已验证的原路径造成回归。
        /// </summary>
        public void StepFixedToi(float dt)
        {
            if (dt <= 0f) throw new ArgumentOutOfRangeException(nameof(dt));
            SyncObbs();

            var pairs0 = YrBroadPhase.SweepAndPrune(_obbs);
            YrContact[] initial = _narrow.Build(pairs0, _obbs, _bodies, dt, Surface);
            var edges = new System.Collections.Generic.List<(int, int)>();
            for (int k = 0; k < initial.Length; k++)
                edges.Add((initial[k].BodyA, initial[k].BodyB));
            YrJoint[] js = _joints.Joints;
            for (int k = 0; k < js.Length; k++) edges.Add((js[k].A, js[k].B));
            _islands.Build(_bodies.Length, edges.ToArray());

            _toi.UseAngularInflation = UseAngularSweep;
            (int, int)[]? ccdPairs = UseBvhCcdPairs
                ? YrSweptBroadphase.SweptPairs(_obbs, _bodies, dt)
                : null;
            _toi.Advance(_bodies, _obbs, _halfExtents, _narrow, _contacts, _joints,
                         dt, Surface, SolverIterations, ToiMaxSubsteps, ccdPairs);

            var vx = new float[_bodies.Length];
            var vy = new float[_bodies.Length];
            var vz = new float[_bodies.Length];
            for (int i = 0; i < _bodies.Length; i++)
            {
                RVec3 v = _bodies[i].LinearVelocity;
                vx[i] = v.X; vy[i] = v.Y; vz[i] = v.Z;
            }
            _islands.Update(vx, vy, vz, SleepSpeedThreshold,
                            SleepEnabled ? SleepFrames : int.MaxValue / 2);

            _tick++;
            _lastStats = new YrFrameStats(
                _tick, pairs0.Length, _narrow.LastHitPairs, _toi.LastContactPoints,
                _islands.IslandCount, _narrow.LastEarliestToi, _ledger.ClosedResidual());
        }

        /// <summary>单个固定步的完整管线（公开以便逐 tick 回放测试）。</summary>
        public void StepFixed(float dt)
        {
            if (dt <= 0f) throw new ArgumentOutOfRangeException(nameof(dt));
            SyncObbs();

            var pairs = YrBroadPhase.SweepAndPrune(_obbs);
            YrContact[] cs = _narrow.Build(pairs, _obbs, _bodies, dt, Surface);

            // 约束岛：接触图 + 关节图共同决定连通分量
            var edges = new System.Collections.Generic.List<(int, int)>();
            for (int k = 0; k < cs.Length; k++)
                edges.Add((cs[k].BodyA, cs[k].BodyB));
            YrJoint[] js = _joints.Joints;
            for (int k = 0; k < js.Length; k++) edges.Add((js[k].A, js[k].B));
            _islands.Build(_bodies.Length, edges.ToArray());

            _contacts.SetContacts(cs);
            _contacts.Prepare();
            _contacts.Solve(SolverIterations);
            _contacts.CorrectPositions(PositionCorrectionBeta, 0.005f);
            _narrow.CacheWarm(_contacts.Contacts);

            _joints.Solve(dt, SolverIterations);
            _joints.CorrectPositions(PositionCorrectionBeta);

            for (int i = 0; i < _bodies.Length; i++)
            {
                if (SleepEnabled && _islands.IsSleeping(i)) continue;
                _bodies[i].Integrate(dt);
            }

            var vx = new float[_bodies.Length];
            var vy = new float[_bodies.Length];
            var vz = new float[_bodies.Length];
            for (int i = 0; i < _bodies.Length; i++)
            {
                RVec3 v = _bodies[i].LinearVelocity;
                vx[i] = v.X; vy[i] = v.Y; vz[i] = v.Z;
            }
            _islands.Update(vx, vy, vz, SleepSpeedThreshold,
                            SleepEnabled ? SleepFrames : int.MaxValue / 2);

            _tick++;
            _lastStats = new YrFrameStats(
                _tick, pairs.Length, _narrow.LastHitPairs, cs.Length,
                _islands.IslandCount, _narrow.LastEarliestToi, _ledger.ClosedResidual());
        }

        /// <summary>记录当前位级快照（含 tick）。</summary>
        public void SaveFrame() => _rollback.Push(_tick, Snapshot());

        /// <summary>按固定顺序拼接全部刚体状态，用于逐位确定性校验。</summary>
        public float[] Snapshot()
        {
            var list = new System.Collections.Generic.List<float>();
            list.Add((float)_tick);
            list.Add((float)_bodies.Length);
            for (int i = 0; i < _bodies.Length; i++)
            {
                RigidBody3D b = _bodies[i];
                list.Add(b.Position.X); list.Add(b.Position.Y); list.Add(b.Position.Z);
                list.Add(b.Orientation.X); list.Add(b.Orientation.Y);
                list.Add(b.Orientation.Z); list.Add(b.Orientation.W);
                list.Add(b.LinearVelocity.X); list.Add(b.LinearVelocity.Y);
                list.Add(b.LinearVelocity.Z);
                list.Add(b.AngularMomentum.X); list.Add(b.AngularMomentum.Y);
                list.Add(b.AngularMomentum.Z);
                list.Add(b.Mass);
            }
            var warm = _narrow.ExportWarm();
            list.Add((float)warm.Length);
            for (int i = 0; i < warm.Length; i++)
            {
                list.Add((float)warm[i].Item1);
                list.Add((float)warm[i].Item2);
                list.Add(warm[i].Item3);
            }
            return list.ToArray();
        }

        public void Restore(float[] state)
        {
            if (state == null || state.Length < 2) throw new ArgumentException("快照非法");
            _tick = (long)state[0];
            int n = (int)state[1];
            if (n != _bodies.Length) throw new ArgumentException("刚体数量不匹配");
            int p = 2;
            for (int i = 0; i < n; i++)
            {
                if (p + 14 > state.Length) throw new ArgumentException("快照长度不足");
                RigidBody3D b = _bodies[i];
                b.Position = new RVec3(state[p], state[p + 1], state[p + 2]);
                b.Orientation = new RQuat(state[p + 3], state[p + 4], state[p + 5], state[p + 6]);
                b.LinearVelocity = new RVec3(state[p + 7], state[p + 8], state[p + 9]);
                b.AngularMomentum = new RVec3(state[p + 10], state[p + 11], state[p + 12]);
                b.Mass = state[p + 13];
                p += 14;
            }
            // 长度校验：2 + 14*n + 1 + 3*m。warm 段必须一起恢复，
            // 否则回滚后第一帧的 warm start 冲量与原始序列不同 → 逐位分叉。
            if (p + 1 > state.Length) throw new ArgumentException("快照缺 warm 段");
            int m = (int)state[p]; p++;
            if (p + 3 * m > state.Length) throw new ArgumentException("快照 warm 段长度不足");
            var warm = new (int, int, float)[m];
            for (int i = 0; i < m; i++)
            {
                warm[i] = ((int)state[p], (int)state[p + 1], state[p + 2]);
                p += 3;
            }
            _narrow.ImportWarm(warm);
            SyncObbs();
        }

        /// <summary>回滚到指定 tick：恢复状态并丢弃未来帧。</summary>
        public bool RewindTo(long tick)
        {
            if (!_rollback.TryGet(tick, out float[] state)) return false;
            Restore(state);
            _rollback.Rewind(tick);
            return true;
        }
    }
}
