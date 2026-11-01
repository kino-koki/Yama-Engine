using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using System.Linq;
using System.Text;

namespace YamaEngine.Physics
{
    // ============================================================================================
    //  PhysicsJobSystem.cs
    //
    //  物理多线程任务系统。把物理模拟的各个阶段(宽相位、窄相位、求解、积分等)拆成
    //  小任务，扔到线程池里并行跑。物理引擎本身是数据驱动的，天然适合并行化。
    //
    //  用法:
    //    var jobSystem = new PhysicsJobSystem(workerCount: 4);
    //    jobSystem.Initialize();
    //
    //    // 每帧
    //    using var batch = jobSystem.CreateBatch();
    //    for (int i = 0; i < islands.Count; i++)
    //    {
    //        batch.Schedule(new IntegrateIslandJob { Island = islands[i], Dt = dt });
    //    }
    //    batch.Wait();
    //
    //  注意: 不要在job里访问非线程安全的东西。job之间不要有数据依赖。
    //  TODO: 支持job依赖图(目前是简单的barrier同步)。
    // ============================================================================================

    /// <summary>任务优先级。</summary>
    public enum JobPriority : byte
    {
        /// <summary>高优先级。立即执行。</summary>
        High = 0,
        /// <summary>普通优先级。</summary>
        Normal = 1,
        /// <summary>低优先级。空闲时执行。</summary>
        Low = 2,
    }

    /// <summary>任务状态。</summary>
    public enum JobState : byte
    {
        /// <summary>已创建但还没调度。</summary>
        Created,
        /// <summary>在队列里等待执行。</summary>
        Queued,
        /// <summary>正在执行。</summary>
        Running,
        /// <summary>执行完成。</summary>
        Completed,
        /// <summary>执行失败。</summary>
        Failed,
        /// <summary>被取消。</summary>
        Cancelled,
    }

    /// <summary>任务统计。</summary>
    public struct JobStats
    {
        public int TotalJobsScheduled;
        public int TotalJobsCompleted;
        public int TotalJobsFailed;
        public int TotalJobsCancelled;
        public double TotalTimeMs;
        public double AvgJobTimeMs;
        public double MaxJobTimeMs;
        public long TotalBytesAllocated;
        public int CurrentQueueLength;
        public int ActiveWorkers;
        public int IdleWorkers;

        public void Reset()
        {
            TotalJobsScheduled = 0;
            TotalJobsCompleted = 0;
            TotalJobsFailed = 0;
            TotalJobsCancelled = 0;
            TotalTimeMs = 0;
            AvgJobTimeMs = 0;
            MaxJobTimeMs = 0;
            TotalBytesAllocated = 0;
            CurrentQueueLength = 0;
            ActiveWorkers = 0;
            IdleWorkers = 0;
        }
    }

    // ============================================================================================
    //  Job接口
    // ============================================================================================

    /// <summary>物理任务接口。所有并行执行的物理任务都要实现这个。</summary>
    public interface IPhysicsJob
    {
        /// <summary>执行任务。</summary>
        void Execute();

        /// <summary>任务名称(用于调试和profiling)。</summary>
        string Name { get; }

        /// <summary>优先级。</summary>
        JobPriority Priority { get; }
    }

    /// <summary>带返回值的任务接口。</summary>
    public interface IPhysicsJob<T> : IPhysicsJob
    {
        /// <summary>任务结果。</summary>
        T Result { get; }
    }

    // ============================================================================================
    //  常用Job实现
    // ============================================================================================

    /// <summary>并行for循环任务。把一段循环拆成多段并行执行。</summary>
    public struct ParallelForJob : IPhysicsJob
    {
        public Action<int> Body;
        public int StartIndex;
        public int EndIndex;
        public string JobName;

        public readonly string Name => JobName;
        public readonly JobPriority Priority => JobPriority.Normal;

        public ParallelForJob(string name, int start, int end, Action<int> body)
        {
            JobName = name;
            StartIndex = start;
            EndIndex = end;
            Body = body;
        }

        public void Execute()
        {
            for (int i = StartIndex; i < EndIndex; i++)
            {
                Body(i);
            }
        }
    }

    /// <summary>岛屿积分任务。对一个岛屿内的所有body做积分。</summary>
    public class IntegrateIslandJob : IPhysicsJob
    {
        public int IslandId;
        public int[] BodyIds;
        public Vector3[] Positions;
        public Quaternion[] Orientations;
        public Vector3[] Velocities;
        public Vector3[] AngularVelocities;
        public float[] InverseMasses;
        public Matrix4x4[] InverseInertias;
        public Vector3 Gravity;
        public float Dt;
        public float LinearDamping;
        public float AngularDamping;

        public string Name => $"IntegrateIsland_{IslandId}";
        public JobPriority Priority => JobPriority.High;

        public IntegrateIslandJob()
        {
            BodyIds = Array.Empty<int>();
            Positions = Array.Empty<Vector3>();
            Orientations = Array.Empty<Quaternion>();
            Velocities = Array.Empty<Vector3>();
            AngularVelocities = Array.Empty<Vector3>();
            InverseMasses = Array.Empty<float>();
            InverseInertias = Array.Empty<Matrix4x4>();
        }

        public void Execute()
        {
            int count = BodyIds.Length;
            float linDamp = MathF.Pow(1f - LinearDamping, Dt * 60f);
            float angDamp = MathF.Pow(1f - AngularDamping, Dt * 60f);
            float halfDt = Dt * 0.5f;

            for (int i = 0; i < count; i++)
            {
                float invMass = InverseMasses[i];
                if (invMass <= 0f) continue; // 静态物体跳过

                // 重力
                Velocities[i] += Gravity * Dt;

                // 阻尼
                Velocities[i] *= linDamp;
                AngularVelocities[i] *= angDamp;

                // 积分位置
                Positions[i] += Velocities[i] * Dt;

                // 积分朝向
                Vector3 av = AngularVelocities[i];
                Quaternion dq = new(av.X * halfDt, av.Y * halfDt, av.Z * halfDt, 0f);
                Quaternion q = Orientations[i];
                Quaternion nq = new(
                    dq.X * q.W + dq.W * q.X + dq.Y * q.Z - dq.Z * q.Y,
                    dq.Y * q.W + dq.W * q.Y + dq.Z * q.X - dq.X * q.Z,
                    dq.Z * q.W + dq.W * q.Z + dq.X * q.Y - dq.Y * q.X,
                    dq.W * q.W - dq.X * q.X - dq.Y * q.Y - dq.Z * q.Z);
                float len = MathF.Sqrt(nq.X * nq.X + nq.Y * nq.Y + nq.Z * nq.Z + nq.W * nq.W);
                if (len > 1e-6f)
                {
                    float inv = 1f / len;
                    Orientations[i] = new Quaternion(nq.X * inv, nq.Y * inv, nq.Z * inv, nq.W * inv);
                }
            }
        }
    }

    /// <summary>宽相位检测任务。对一组body做AABB更新和重叠检测。</summary>
    public class BroadphaseJob : IPhysicsJob
    {
        public int StartIndex;
        public int EndIndex;
        public Vector3[] Positions;
        public Quaternion[] Orientations;
        public Vector3[] HalfExtents; // 简化: 用AABB
        public int[] BodyIds;
        public ConcurrentBag<(int a, int b)> OutputPairs;

        public string Name => $"Broadphase_{StartIndex}_{EndIndex}";
        public JobPriority Priority => JobPriority.High;

        public BroadphaseJob()
        {
            OutputPairs = new ConcurrentBag<(int, int)>();
        }

        public void Execute()
        {
            // 简化的O(n²)宽相位(实际应该用SAP/树)
            // 这里只是job系统的演示
            for (int i = StartIndex; i < EndIndex; i++)
            {
                Vector3 minA = Positions[i] - HalfExtents[i];
                Vector3 maxA = Positions[i] + HalfExtents[i];

                for (int j = i + 1; j < BodyIds.Length; j++)
                {
                    Vector3 minB = Positions[j] - HalfExtents[j];
                    Vector3 maxB = Positions[j] + HalfExtents[j];

                    // AABB重叠测试
                    if (minA.X <= maxB.X && maxA.X >= minB.X &&
                        minA.Y <= maxB.Y && maxA.Y >= minB.Y &&
                        minA.Z <= maxB.Z && maxA.Z >= minB.Z)
                    {
                        OutputPairs.Add((BodyIds[i], BodyIds[j]));
                    }
                }
            }
        }
    }

    /// <summary>窄相位检测任务。对一组候选pair做精确碰撞检测。</summary>
    public class NarrowphaseJob : IPhysicsJob
    {
        public struct CollisionPair
        {
            public int BodyA;
            public int BodyB;
            public Vector3 PosA;
            public Quaternion RotA;
            public Vector3 PosB;
            public Quaternion RotB;
        }

        public CollisionPair[] Pairs;
        public int StartIndex;
        public int EndIndex;
        public ConcurrentBag<NarrowphaseResult> Results;

        public string Name => $"Narrowphase_{StartIndex}_{EndIndex}";
        public JobPriority Priority => JobPriority.High;

        public NarrowphaseJob()
        {
            Results = new ConcurrentBag<NarrowphaseResult>();
        }

        public void Execute()
        {
            for (int i = StartIndex; i < EndIndex; i++)
            {
                var pair = Pairs[i];
                // 简化: 只做距离检测
                float dist = Vector3.Distance(pair.PosA, pair.PosB);
                if (dist < 1f) // 假设半径都是0.5
                {
                    Results.Add(new NarrowphaseResult
                    {
                        BodyA = pair.BodyA,
                        BodyB = pair.BodyB,
                        HasCollision = true,
                        ContactPoint = (pair.PosA + pair.PosB) * 0.5f,
                        ContactNormal = dist > 1e-6f
                            ? Vector3.Normalize(pair.PosA - pair.PosB)
                            : Vector3.UnitY,
                        PenetrationDepth = 1f - dist,
                    });
                }
            }
        }
    }

    /// <summary>窄相位结果。</summary>
    public struct NarrowphaseResult
    {
        public int BodyA;
        public int BodyB;
        public bool HasCollision;
        public Vector3 ContactPoint;
        public Vector3 ContactNormal;
        public float PenetrationDepth;
    }

    /// <summary>接触求解任务。对一组接触约束做迭代求解。</summary>
    public class ContactSolverJob : IPhysicsJob
    {
        public struct ContactConstraint
        {
            public int BodyA;
            public int BodyB;
            public Vector3 ContactPoint;
            public Vector3 ContactNormal;
            public float PenetrationDepth;
            public float InvMassA;
            public float InvMassB;
            public Matrix4x4 InvInertiaA;
            public Matrix4x4 InvInertiaB;
            public Vector3 PosA;
            public Vector3 PosB;
            public Vector3 VelA;
            public Vector3 VelB;
            public Vector3 AngVelA;
            public Vector3 AngVelB;
            public float Restitution;
            public float Friction;
            public float Slope;
        }

        public ContactConstraint[] Constraints;
        public int StartIndex;
        public int EndIndex;
        public int Iterations;
        public Vector3[] LinearVelocityDeltaA;
        public Vector3[] LinearVelocityDeltaB;
        public Vector3[] AngularVelocityDeltaA;
        public Vector3[] AngularVelocityDeltaB;

        public string Name => $"ContactSolver_{StartIndex}_{EndIndex}";
        public JobPriority Priority => JobPriority.Normal;

        public void Execute()
        {
            // 序贯冲量法(SI)
            for (int iter = 0; iter < Iterations; iter++)
            {
                for (int i = StartIndex; i < EndIndex; i++)
                {
                    ref var c = ref Constraints[i];

                    // 计算相对速度
                    Vector3 rA = c.ContactPoint - c.PosA;
                    Vector3 rB = c.ContactPoint - c.PosB;
                    Vector3 velA = c.VelA + Vector3.Cross(c.AngVelA, rA) + LinearVelocityDeltaA[i];
                    Vector3 velB = c.VelB + Vector3.Cross(c.AngVelB, rB) + LinearVelocityDeltaB[i];
                    Vector3 relVel = velA - velB;

                    // 法向相对速度
                    float vn = Vector3.Dot(relVel, c.ContactNormal);

                    // 计算有效质量
                    Vector3 raCrossN = Vector3.Cross(rA, c.ContactNormal);
                    Vector3 rbCrossN = Vector3.Cross(rB, c.ContactNormal);
                    float invMassSum = c.InvMassA + c.InvMassB;
                    float tA = Vector3.Dot(raCrossN, Vector3.Transform(raCrossN, c.InvInertiaA));
                    float tB = Vector3.Dot(rbCrossN, Vector3.Transform(rbCrossN, c.InvInertiaB));
                    float effectiveMass = invMassSum + tA + tB;

                    if (effectiveMass < 1e-8f) continue;

                    // 位置修正(Baumgarte)
                    float baumgarte = 0.2f;
                    float slop = c.Slope;
                    float correction = -baumgarte * MathF.Max(c.PenetrationDepth - slop, 0f) / (1f / 60f);

                    // 目标速度
                    float targetVn = vn + correction;
                    if (targetVn < 0) targetVn = 0;

                    // 冲量
                    float impulse = -(targetVn) / effectiveMass;
                    impulse = MathF.Max(impulse, 0);

                    Vector3 impulseVec = c.ContactNormal * impulse;

                    // 应用冲量增量
                    LinearVelocityDeltaA[i] += impulseVec * c.InvMassA;
                    LinearVelocityDeltaB[i] -= impulseVec * c.InvMassB;
                    AngularVelocityDeltaA[i] += Vector3.Transform(Vector3.Cross(rA, impulseVec), c.InvInertiaA);
                    AngularVelocityDeltaB[i] -= Vector3.Transform(Vector3.Cross(rB, impulseVec), c.InvInertiaB);
                }
            }
        }
    }

    /// <summary>AABB更新任务。批量更新body的AABB。</summary>
    public class UpdateAabbJob : IPhysicsJob
    {
        public int[] BodyIds;
        public Vector3[] Positions;
        public Quaternion[] Orientations;
        public Vector3[] HalfExtents;
        public Vector3[] AabbMin;
        public Vector3[] AabbMax;
        public int StartIndex;
        public int EndIndex;
        public float Margin;

        public string Name => $"UpdateAABB_{StartIndex}_{EndIndex}";
        public JobPriority Priority => JobPriority.High;

        public void Execute()
        {
            for (int i = StartIndex; i < EndIndex; i++)
            {
                // 旋转后的AABB(简化: 用旋转后的half extents的绝对值)
                Quaternion rot = Orientations[i];
                Vector3 he = HalfExtents[i];

                // 旋转AABB的8个角点，然后求新的AABB
                Matrix4x4 rotMat = Matrix4x4.CreateFromQuaternion(rot);
                Vector3 absX = new(MathF.Abs(rotMat.M11), MathF.Abs(rotMat.M12), MathF.Abs(rotMat.M13));
                Vector3 absY = new(MathF.Abs(rotMat.M21), MathF.Abs(rotMat.M22), MathF.Abs(rotMat.M23));
                Vector3 absZ = new(MathF.Abs(rotMat.M31), MathF.Abs(rotMat.M32), MathF.Abs(rotMat.M33));

                Vector3 rotatedHe = new(
                    he.X * absX.X + he.Y * absY.X + he.Z * absZ.X,
                    he.X * absX.Y + he.Y * absY.Y + he.Z * absZ.Y,
                    he.X * absX.Z + he.Y * absY.Z + he.Z * absZ.Z);

                AabbMin[i] = Positions[i] - rotatedHe - new Vector3(Margin);
                AabbMax[i] = Positions[i] + rotatedHe + new Vector3(Margin);
            }
        }
    }

    /// <summary>速度积分任务(不含位置积分)。</summary>
    public class IntegrateVelocityJob : IPhysicsJob
    {
        public int StartIndex;
        public int EndIndex;
        public Vector3[] Velocities;
        public Vector3[] AngularVelocities;
        public Vector3[] Forces;
        public Vector3[] Torques;
        public float[] InverseMasses;
        public Matrix4x4[] InverseInertias;
        public Vector3 Gravity;
        public float Dt;
        public float LinearDamping;
        public float AngularDamping;

        public string Name => $"IntegrateVel_{StartIndex}_{EndIndex}";
        public JobPriority Priority => JobPriority.High;

        public void Execute()
        {
            float linDamp = MathF.Pow(1f - LinearDamping, Dt);
            float angDamp = MathF.Pow(1f - AngularDamping, Dt);

            for (int i = StartIndex; i < EndIndex; i++)
            {
                if (InverseMasses[i] <= 0f) continue;

                // v += (F/m + g) * dt
                Velocities[i] += (Forces[i] * InverseMasses[i] + Gravity) * Dt;
                Velocities[i] *= linDamp;

                // w += I^-1 * T * dt
                Vector3 angAccel = Vector3.Transform(Torques[i], InverseInertias[i]);
                AngularVelocities[i] += angAccel * Dt;
                AngularVelocities[i] *= angDamp;
            }
        }
    }

    /// <summary>位置积分任务。</summary>
    public class IntegratePositionJob : IPhysicsJob
    {
        public int StartIndex;
        public int EndIndex;
        public Vector3[] Positions;
        public Quaternion[] Orientations;
        public Vector3[] Velocities;
        public Vector3[] AngularVelocities;
        public float[] InverseMasses;
        public float Dt;

        public string Name => $"IntegratePos_{StartIndex}_{EndIndex}";
        public JobPriority Priority => JobPriority.High;

        public void Execute()
        {
            float halfDt = Dt * 0.5f;

            for (int i = StartIndex; i < EndIndex; i++)
            {
                if (InverseMasses[i] <= 0f) continue;

                // x += v * dt
                Positions[i] += Velocities[i] * Dt;

                // q += 0.5 * (w * q) * dt
                Vector3 av = AngularVelocities[i];
                Quaternion q = Orientations[i];
                Quaternion dq = new(av.X * halfDt, av.Y * halfDt, av.Z * halfDt, 0f);
                Quaternion nq = new(
                    dq.X * q.W + dq.W * q.X + dq.Y * q.Z - dq.Z * q.Y,
                    dq.Y * q.W + dq.W * q.Y + dq.Z * q.X - dq.X * q.Z,
                    dq.Z * q.W + dq.W * q.Z + dq.X * q.Y - dq.Y * q.X,
                    dq.W * q.W - dq.X * q.X - dq.Y * q.Y - dq.Z * q.Z);
                float len = MathF.Sqrt(nq.X * nq.X + nq.Y * nq.Y + nq.Z * nq.Z + nq.W * nq.W);
                if (len > 1e-6f)
                {
                    float inv = 1f / len;
                    Orientations[i] = new Quaternion(nq.X * inv, nq.Y * inv, nq.Z * inv, nq.W * inv);
                }
            }
        }
    }

    /// <summary>睡眠检测任务。</summary>
    public class SleepCheckJob : IPhysicsJob
    {
        public int StartIndex;
        public int EndIndex;
        public Vector3[] Velocities;
        public Vector3[] AngularVelocities;
        public float[] InverseMasses;
        public bool[] Sleeping;
        public float[] SleepTimers;
        public float LinearThreshold = 0.05f;
        public float AngularThreshold = 0.1f;
        public float SleepTime = 1f;
        public float Dt;

        public string Name => $"SleepCheck_{StartIndex}_{EndIndex}";
        public JobPriority Priority => JobPriority.Normal;

        public void Execute()
        {
            float linThreshSq = LinearThreshold * LinearThreshold;
            float angThreshSq = AngularThreshold * AngularThreshold;

            for (int i = StartIndex; i < EndIndex; i++)
            {
                if (InverseMasses[i] <= 0f) continue;

                float linSq = Velocities[i].LengthSquared();
                float angSq = AngularVelocities[i].LengthSquared();

                if (linSq < linThreshSq && angSq < angThreshSq)
                {
                    SleepTimers[i] += Dt;
                    if (SleepTimers[i] >= SleepTime)
                    {
                        Sleeping[i] = true;
                        Velocities[i] = Vector3.Zero;
                        AngularVelocities[i] = Vector3.Zero;
                    }
                }
                else
                {
                    SleepTimers[i] = 0;
                    Sleeping[i] = false;
                }
            }
        }
    }

    // ============================================================================================
    //  工作线程
    // ============================================================================================

    /// <summary>工作线程。从队列里取任务执行。</summary>
    internal class PhysicsWorker : IDisposable
    {
        private readonly Thread _thread;
        private readonly PhysicsJobSystem _system;
        private readonly int _workerId;
        private volatile bool _running;
        private volatile bool _disposed;
        private readonly AutoResetEvent _wakeSignal = new(false);

        public int WorkerId => _workerId;
        public bool IsRunning => _running;
        public bool IsBusy { get; private set; }
        public long JobsExecuted;
        public double TotalActiveTimeMs;
        public double TotalIdleTimeMs;

        public PhysicsWorker(PhysicsJobSystem system, int id)
        {
            _system = system;
            _workerId = id;
            _running = true;
            _thread = new Thread(WorkerLoop)
            {
                Name = $"PhysicsWorker_{id}",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
            };
            _thread.Start();
        }

        /// <summary>唤醒工作线程。</summary>
        public void Wake()
        {
            if (_running)
                _wakeSignal.Set();
        }

        private void WorkerLoop()
        {
            var sw = new Stopwatch();
            while (_running)
            {
                // 尝试取任务
                IPhysicsJob? job = _system.TryDequeueJob();
                if (job != null)
                {
                    IsBusy = true;
                    sw.Restart();

                    try
                    {
                        job.Execute();
                        _system.OnJobCompleted(job, true, null);
                        JobsExecuted++;
                    }
                    catch (Exception ex)
                    {
                        _system.OnJobCompleted(job, false, ex);
                    }

                    sw.Stop();
                    TotalActiveTimeMs += sw.Elapsed.TotalMilliseconds;
                    IsBusy = false;
                }
                else
                {
                    // 没任务，等待
                    IsBusy = false;
                    var idleStart = sw.Elapsed;
                    _wakeSignal.WaitOne(10); // 最多等10ms
                    TotalIdleTimeMs += 10;
                }
            }
        }

        public void Stop()
        {
            _running = false;
            _wakeSignal.Set();
            _thread.Join(1000);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            _wakeSignal.Dispose();
        }
    }

    // ============================================================================================
    //  Job批处理
    // ============================================================================================

    /// <summary>Job批处理。一组job的集合，可以一起调度和等待。</summary>
    public class PhysicsJobBatch : IDisposable
    {
        private readonly PhysicsJobSystem _system;
        private readonly List<IPhysicsJob> _jobs = new();
        private readonly ManualResetEventSlim _allDone = new(false);
        private int _remainingJobs;
        private bool _disposed;
        private bool _scheduled;

        public int JobCount => _jobs.Count;
        public bool IsCompleted => _remainingJobs == 0;
        public double TotalTimeMs { get; private set; }

        internal PhysicsJobBatch(PhysicsJobSystem system)
        {
            _system = system;
        }

        /// <summary>添加一个任务到批处理。</summary>
        public void Schedule(IPhysicsJob job)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PhysicsJobBatch));
            if (_scheduled) throw new InvalidOperationException("批处理已经调度了，不能再加任务");
            if (job == null) return;

            _jobs.Add(job);
        }

        /// <summary>提交批处理，开始执行所有任务。</summary>
        public void Commit()
        {
            if (_disposed || _scheduled) return;
            _scheduled = true;
            _remainingJobs = _jobs.Count;

            if (_jobs.Count == 0)
            {
                _allDone.Set();
                return;
            }

            // 把所有job扔到队列里
            foreach (var job in _jobs)
            {
                _system.EnqueueJob(job, OnJobDone);
            }

            // 唤醒所有worker
            _system.WakeAllWorkers();
        }

        /// <summary>job完成的回调。</summary>
        private void OnJobDone(IPhysicsJob job)
        {
            if (Interlocked.Decrement(ref _remainingJobs) == 0)
            {
                _allDone.Set();
            }
        }

        /// <summary>等待所有任务完成。</summary>
        public void Wait()
        {
            if (!_scheduled)
                Commit();
            _allDone.Wait();
        }

        /// <summary>等待所有任务完成(带超时)。</summary>
        public bool Wait(int timeoutMs)
        {
            if (!_scheduled)
                Commit();
            return _allDone.Wait(timeoutMs);
        }

        /// <summary>等待并返回是否超时。</summary>
        public bool Wait(TimeSpan timeout)
        {
            return Wait((int)timeout.TotalMilliseconds);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (!_allDone.IsSet)
            {
                Wait(5000); // 最多等5秒
            }
            _allDone.Dispose();
            _jobs.Clear();
        }
    }

    // ============================================================================================
    //  Job系统核心
    // ============================================================================================

    /// <summary>
    /// 物理多线程任务系统。管理worker线程池，调度和执行物理job。
    /// </summary>
    public class PhysicsJobSystem : IDisposable
    {
        // ---- 配置 ----
        private readonly int _workerCount;
        public int WorkerCount => _workerCount;

        // ---- 工作线程 ----
        private readonly PhysicsWorker[] _workers;

        // ---- 任务队列 ----
        private readonly ConcurrentQueue<JobQueueEntry> _highPriorityQueue = new();
        private readonly ConcurrentQueue<JobQueueEntry> _normalPriorityQueue = new();
        private readonly ConcurrentQueue<JobQueueEntry> _lowPriorityQueue = new();

        // ---- 统计 ----
        private JobStats _stats;
        public ref JobStats Stats => ref _stats;
        private long _totalJobsScheduled;
        private long _totalJobsCompleted;
        private long _totalJobsFailed;

        // ---- 状态 ----
        private volatile bool _running;
        private volatile bool _disposed;
        private int _activeWorkers;

        // ---- 事件 ----
        public event Action<IPhysicsJob>? JobCompleted;
        public event Action<IPhysicsJob, Exception>? JobFailed;

        /// <summary>队列里等待的任务数量。</summary>
        public int PendingJobCount =>
            _highPriorityQueue.Count + _normalPriorityQueue.Count + _lowPriorityQueue.Count;

        /// <summary>正在执行的worker数量。</summary>
        public int BusyWorkerCount
        {
            get
            {
                int count = 0;
                foreach (var w in _workers)
                    if (w.IsBusy) count++;
                return count;
            }
        }

        /// <summary>空闲的worker数量。</summary>
        public int IdleWorkerCount => _workerCount - BusyWorkerCount;

        /// <summary>是否在运行。</summary>
        public bool IsRunning => _running;

        /// <summary>队列条目。</summary>
        private struct JobQueueEntry
        {
            public IPhysicsJob Job;
            public Action<IPhysicsJob>? Callback;
        }

        // ========================================================================================
        //  构造 / 销毁
        // ========================================================================================

        /// <summary>创建Job系统。</summary>
        /// <param name="workerCount">工作线程数量。0=自动(等于CPU核心数)。</param>
        public PhysicsJobSystem(int workerCount = 0)
        {
            _workerCount = workerCount > 0 ? workerCount : Environment.ProcessorCount;
            _workers = new PhysicsWorker[_workerCount];
        }

        /// <summary>初始化并启动工作线程。</summary>
        public void Initialize()
        {
            if (_running) return;
            _running = true;

            for (int i = 0; i < _workerCount; i++)
            {
                _workers[i] = new PhysicsWorker(this, i);
            }
        }

        /// <summary>关闭并等待所有worker退出。</summary>
        public void Shutdown()
        {
            if (!_running) return;
            _running = false;

            // 等待队列清空
            int waitCount = 0;
            while (PendingJobCount > 0 && waitCount < 100)
            {
                Thread.Sleep(10);
                waitCount++;
            }

            // 停止所有worker
            foreach (var w in _workers)
            {
                w?.Dispose();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Shutdown();
        }

        // ========================================================================================
        //  任务调度
        // ========================================================================================

        /// <summary>把任务放入队列。</summary>
        public void EnqueueJob(IPhysicsJob job, Action<IPhysicsJob>? callback = null)
        {
            if (job == null) return;
            if (!_running) Initialize();

            var entry = new JobQueueEntry { Job = job, Callback = callback };

            switch (job.Priority)
            {
                case JobPriority.High:
                    _highPriorityQueue.Enqueue(entry);
                    break;
                case JobPriority.Low:
                    _lowPriorityQueue.Enqueue(entry);
                    break;
                default:
                    _normalPriorityQueue.Enqueue(entry);
                    break;
            }

            Interlocked.Increment(ref _totalJobsScheduled);
            _stats.TotalJobsScheduled++;

            // 唤醒一个空闲worker
            WakeIdleWorker();
        }

        /// <summary>从队列取出任务(给worker调用)。</summary>
        internal IPhysicsJob? TryDequeueJob()
        {
            JobQueueEntry entry;

            // 优先取高优先级
            if (_highPriorityQueue.TryDequeue(out entry))
            {
                ProcessCallback(entry);
                return entry.Job;
            }
            if (_normalPriorityQueue.TryDequeue(out entry))
            {
                ProcessCallback(entry);
                return entry.Job;
            }
            if (_lowPriorityQueue.TryDequeue(out entry))
            {
                ProcessCallback(entry);
                return entry.Job;
            }

            return null;
        }

        private Action<IPhysicsJob>? _currentCallback;
        private void ProcessCallback(JobQueueEntry entry)
        {
            // 把callback暂存，等job完成后再调
            _currentCallback = entry.Callback;
        }

        /// <summary>任务完成回调(给worker调用)。</summary>
        internal void OnJobCompleted(IPhysicsJob job, bool success, Exception? error)
        {
            if (success)
            {
                Interlocked.Increment(ref _totalJobsCompleted);
                _stats.TotalJobsCompleted++;
                JobCompleted?.Invoke(job);
            }
            else
            {
                Interlocked.Increment(ref _totalJobsFailed);
                _stats.TotalJobsFailed++;
                JobFailed?.Invoke(job, error!);
            }
        }

        /// <summary>唤醒一个空闲worker。</summary>
        private void WakeIdleWorker()
        {
            foreach (var w in _workers)
            {
                if (w != null && !w.IsBusy)
                {
                    w.Wake();
                    return;
                }
            }
        }

        /// <summary>唤醒所有worker。</summary>
        internal void WakeAllWorkers()
        {
            foreach (var w in _workers)
            {
                w?.Wake();
            }
        }

        // ========================================================================================
        //  批处理
        // ========================================================================================

        /// <summary>创建一个批处理。</summary>
        public PhysicsJobBatch CreateBatch()
        {
            return new PhysicsJobBatch(this);
        }

        /// <summary>并行for循环。把循环拆成多个job并行执行。</summary>
        public void ParallelFor(string name, int count, Action<int> body, int batchSize = 0)
        {
            if (count <= 0) return;

            if (batchSize <= 0)
            {
                // 自动计算batch大小
                batchSize = Math.Max(1, count / (_workerCount * 4));
            }

            int numBatches = (count + batchSize - 1) / batchSize;
            if (numBatches <= 1)
            {
                // 只有一个batch，直接跑
                for (int i = 0; i < count; i++)
                    body(i);
                return;
            }

            using var batch = CreateBatch();
            for (int b = 0; b < numBatches; b++)
            {
                int start = b * batchSize;
                int end = Math.Min(start + batchSize, count);
                batch.Schedule(new ParallelForJob($"{name}_{b}", start, end, body));
            }
            batch.Commit();
            batch.Wait();
        }

        /// <summary>并行for循环(带返回值收集)。</summary>
        public T[] ParallelForCollect<T>(string name, int count, Func<int, T> body, int batchSize = 0)
        {
            if (count <= 0) return Array.Empty<T>();

            var results = new T[count];

            if (batchSize <= 0)
            {
                batchSize = Math.Max(1, count / (_workerCount * 4));
            }

            int numBatches = (count + batchSize - 1) / batchSize;
            if (numBatches <= 1)
            {
                for (int i = 0; i < count; i++)
                    results[i] = body(i);
                return results;
            }

            using var batch = CreateBatch();
            for (int b = 0; b < numBatches; b++)
            {
                int start = b * batchSize;
                int end = Math.Min(start + batchSize, count);
                batch.Schedule(new ParallelForJob($"{name}_{b}", start, end, i =>
                {
                    results[i] = body(i);
                }));
            }
            batch.Commit();
            batch.Wait();

            return results;
        }

        // ========================================================================================
        //  同步原语
        // ========================================================================================

        /// <summary>等待所有队列里的任务完成，返回剩余未完成任务数(0 表示已空闲)。</summary>
        public int WaitIdle(int timeoutMs = 10000)
        {
            int waited = 0;
            while (PendingJobCount > 0 || BusyWorkerCount > 0)
            {
                Thread.Sleep(1);
                waited++;
                if (waited > timeoutMs) break;
            }
            return PendingJobCount + BusyWorkerCount;
        }

        /// <summary>等待直到队列空了。</summary>
        public bool WaitIdle(TimeSpan timeout)
        {
            return WaitIdle((int)timeout.TotalMilliseconds) <= 0;
        }

        // ========================================================================================
        //  统计
        // ========================================================================================

        /// <summary>获取统计信息。</summary>
        public JobStats GetStats()
        {
            _stats.CurrentQueueLength = PendingJobCount;
            _stats.ActiveWorkers = BusyWorkerCount;
            _stats.IdleWorkers = IdleWorkerCount;
            _stats.TotalJobsScheduled = (int)Interlocked.Read(ref _totalJobsScheduled);
            _stats.TotalJobsCompleted = (int)Interlocked.Read(ref _totalJobsCompleted);
            _stats.TotalJobsFailed = (int)Interlocked.Read(ref _totalJobsFailed);

            if (_stats.TotalJobsCompleted > 0)
            {
                _stats.AvgJobTimeMs = _stats.TotalTimeMs / _stats.TotalJobsCompleted;
            }

            return _stats;
        }

        /// <summary>重置统计。</summary>
        public void ResetStats()
        {
            _stats.Reset();
            Interlocked.Exchange(ref _totalJobsScheduled, 0);
            Interlocked.Exchange(ref _totalJobsCompleted, 0);
            Interlocked.Exchange(ref _totalJobsFailed, 0);
        }

        /// <summary>获取各worker的统计信息。</summary>
        public (int id, long jobs, double activeMs, double idleMs, double utilization)[] GetWorkerStats()
        {
            var result = new (int, long, double, double, double)[_workerCount];
            for (int i = 0; i < _workerCount; i++)
            {
                var w = _workers[i];
                if (w == null)
                {
                    result[i] = (i, 0, 0, 0, 0);
                    continue;
                }
                double total = w.TotalActiveTimeMs + w.TotalIdleTimeMs;
                double util = total > 0 ? w.TotalActiveTimeMs / total : 0;
                result[i] = (w.WorkerId, w.JobsExecuted, w.TotalActiveTimeMs, w.TotalIdleTimeMs, util);
            }
            return result;
        }

        // ========================================================================================
        //  调试
        // ========================================================================================

        /// <summary>生成统计报告字符串。</summary>
        public string GenerateReport()
        {
            var sb = new StringBuilder(512);
            sb.AppendLine("=== PhysicsJobSystem Report ===");
            sb.AppendLine($"Workers: {_workerCount} (Busy: {BusyWorkerCount}, Idle: {IdleWorkerCount})");
            sb.AppendLine($"Pending jobs: {PendingJobCount}");
            sb.AppendLine($"Total scheduled: {_stats.TotalJobsScheduled}");
            sb.AppendLine($"Total completed: {_stats.TotalJobsCompleted}");
            sb.AppendLine($"Total failed: {_stats.TotalJobsFailed}");
            sb.AppendLine($"Avg job time: {_stats.AvgJobTimeMs:F3} ms");
            sb.AppendLine($"Max job time: {_stats.MaxJobTimeMs:F3} ms");

            sb.AppendLine("\nWorker stats:");
            var workerStats = GetWorkerStats();
            foreach (var ws in workerStats)
            {
                sb.AppendLine($"  Worker#{ws.id}: jobs={ws.jobs}, " +
                              $"active={ws.activeMs:F1}ms, idle={ws.idleMs:F1}ms, " +
                              $"util={ws.utilization * 100:F1}%");
            }

            return sb.ToString();
        }
    }

    // ============================================================================================
    //  Job图 (依赖管理)
    // ============================================================================================

    /// <summary>Job依赖节点。</summary>
    public class JobNode
    {
        public IPhysicsJob Job;
        public List<JobNode> Dependencies = new();
        public List<JobNode> Dependents = new();
        public JobState State = JobState.Created;
        public int UnresolvedDependencies;

        public JobNode(IPhysicsJob job)
        {
            Job = job;
        }
    }

    /// <summary>Job依赖图。管理job之间的执行顺序。</summary>
    public class JobGraph : IDisposable
    {
        private readonly PhysicsJobSystem _system;
        private readonly Dictionary<string, JobNode> _nodes = new();
        private readonly List<JobNode> _allNodes = new();
        private bool _disposed;

        public int NodeCount => _allNodes.Count;

        public JobGraph(PhysicsJobSystem system)
        {
            _system = system ?? throw new ArgumentNullException(nameof(system));
        }

        /// <summary>添加一个job节点。</summary>
        public JobNode AddNode(string key, IPhysicsJob job)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(JobGraph));
            var node = new JobNode(job);
            _nodes[key] = node;
            _allNodes.Add(node);
            return node;
        }

        /// <summary>添加依赖关系: dependsOn 必须在 dependent 之前执行。</summary>
        public void AddDependency(string dependent, string dependsOn)
        {
            if (!_nodes.TryGetValue(dependent, out var depNode) ||
                !_nodes.TryGetValue(dependsOn, out var depOnNode))
                return;

            depOnNode.Dependents.Add(depNode);
            depNode.Dependencies.Add(depOnNode);
            depNode.UnresolvedDependencies++;
        }

        /// <summary>执行整个图。</summary>
        public void Execute()
        {
            if (_disposed) return;

            // 找到所有没有依赖的节点
            var ready = new Queue<JobNode>();
            foreach (var node in _allNodes)
            {
                if (node.UnresolvedDependencies == 0)
                {
                    ready.Enqueue(node);
                }
            }

            int completed = 0;
            int total = _allNodes.Count;

            while (completed < total)
            {
                // 调度所有ready的节点
                var toSchedule = new List<JobNode>();
                while (ready.Count > 0)
                {
                    toSchedule.Add(ready.Dequeue());
                }

                if (toSchedule.Count == 0)
                {
                    // 死锁? 检查是否有环
                    break;
                }

                // 用batch调度
                using var batch = _system.CreateBatch();
                foreach (var node in toSchedule)
                {
                    node.State = JobState.Queued;
                    batch.Schedule(node.Job);
                }
                batch.Commit();
                batch.Wait();

                // 标记完成，更新依赖
                foreach (var node in toSchedule)
                {
                    node.State = JobState.Completed;
                    completed++;

                    foreach (var dependent in node.Dependents)
                    {
                        dependent.UnresolvedDependencies--;
                        if (dependent.UnresolvedDependencies == 0)
                        {
                            ready.Enqueue(dependent);
                        }
                    }
                }
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _nodes.Clear();
            _allNodes.Clear();
        }
    }

    // ============================================================================================
    //  物理步进流水线 (把整个物理步进拆成多阶段job)
    // ============================================================================================

    /// <summary>物理步进流水线。把一帧的物理模拟拆成多个阶段并行执行。</summary>
    public class PhysicsPipeline : IDisposable
    {
        private readonly PhysicsJobSystem _jobSystem;
        private bool _disposed;

        // 数据
        private int _bodyCount;
        private Vector3[] _positions = Array.Empty<Vector3>();
        private Quaternion[] _orientations = Array.Empty<Quaternion>();
        private Vector3[] _velocities = Array.Empty<Vector3>();
        private Vector3[] _angularVelocities = Array.Empty<Vector3>();
        private Vector3[] _forces = Array.Empty<Vector3>();
        private Vector3[] _torques = Array.Empty<Vector3>();
        private float[] _inverseMasses = Array.Empty<float>();
        private Matrix4x4[] _inverseInertias = Array.Empty<Matrix4x4>();
        private Vector3[] _halfExtents = Array.Empty<Vector3>();
        private Vector3[] _aabbMin = Array.Empty<Vector3>();
        private Vector3[] _aabbMax = Array.Empty<Vector3>();
        private bool[] _sleeping = Array.Empty<bool>();
        private float[] _sleepTimers = Array.Empty<float>();
        private int[] _bodyIds = Array.Empty<int>();

        // 配置
        public Vector3 Gravity = new(0, -9.81f, 0);
        public float LinearDamping = 0.01f;
        public float AngularDamping = 0.05f;
        public float LinearSleepThreshold = 0.05f;
        public float AngularSleepThreshold = 0.1f;
        public float SleepTime = 1f;
        public float AabbMargin = 0.05f;
        public int SolverIterations = 8;

        // 统计
        public struct PipelineStats
        {
            public double TotalTimeMs;
            public double BroadphaseTimeMs;
            public double NarrowphaseTimeMs;
            public double SolverTimeMs;
            public double IntegrateTimeMs;
            public double SleepTimeMs;
            public int BodyCount;
            public int ContactCount;
            public int SleepingCount;
        }
        public PipelineStats Stats;

        public PhysicsPipeline(PhysicsJobSystem jobSystem)
        {
            _jobSystem = jobSystem ?? throw new ArgumentNullException(nameof(jobSystem));
        }

        /// <summary>设置body数据。</summary>
        public void SetBodies(int count)
        {
            if (count == _bodyCount) return;

            _bodyCount = count;
            _positions = new Vector3[count];
            _orientations = new Quaternion[count];
            _velocities = new Vector3[count];
            _angularVelocities = new Vector3[count];
            _forces = new Vector3[count];
            _torques = new Vector3[count];
            _inverseMasses = new float[count];
            _inverseInertias = new Matrix4x4[count];
            _halfExtents = new Vector3[count];
            _aabbMin = new Vector3[count];
            _aabbMax = new Vector3[count];
            _sleeping = new bool[count];
            _sleepTimers = new float[count];
            _bodyIds = new int[count];

            for (int i = 0; i < count; i++)
            {
                _bodyIds[i] = i;
                _orientations[i] = Quaternion.Identity;
                _inverseInertias[i] = Matrix4x4.Identity;
                _halfExtents[i] = new Vector3(0.5f);
                _inverseMasses[i] = 1f;
            }
        }

        /// <summary>执行一次物理步进。</summary>
        public void Step(float dt)
        {
            if (_bodyCount == 0) return;

            var totalSw = Stopwatch.StartNew();

            // 阶段1: 更新AABB (并行)
            var bpSw = Stopwatch.StartNew();
            int batchSize = Math.Max(1, _bodyCount / (_jobSystem.WorkerCount * 4));
            using (var bpBatch = _jobSystem.CreateBatch())
            {
                for (int start = 0; start < _bodyCount; start += batchSize)
                {
                    int end = Math.Min(start + batchSize, _bodyCount);
                    bpBatch.Schedule(new UpdateAabbJob
                    {
                        BodyIds = _bodyIds,
                        Positions = _positions,
                        Orientations = _orientations,
                        HalfExtents = _halfExtents,
                        AabbMin = _aabbMin,
                        AabbMax = _aabbMax,
                        StartIndex = start,
                        EndIndex = end,
                        Margin = AabbMargin,
                    });
                }
                bpBatch.Commit();
                bpBatch.Wait();
            }
            bpSw.Stop();
            Stats.BroadphaseTimeMs = bpSw.Elapsed.TotalMilliseconds;

            // 阶段2: 宽相位(找出候选pair)
            // 这里简化处理，实际应该用SAP/树
            var broadphasePairs = new ConcurrentBag<(int a, int b)>();
            using (var npBatch = _jobSystem.CreateBatch())
            {
                // 阶段3: 窄相位(精确检测)
                var npResults = new ConcurrentBag<NarrowphaseResult>();

                // 阶段4: 速度积分(并行)
                var intSw = Stopwatch.StartNew();
                using (var intBatch = _jobSystem.CreateBatch())
                {
                    for (int start = 0; start < _bodyCount; start += batchSize)
                    {
                        int end = Math.Min(start + batchSize, _bodyCount);
                        intBatch.Schedule(new IntegrateVelocityJob
                        {
                            StartIndex = start,
                            EndIndex = end,
                            Velocities = _velocities,
                            AngularVelocities = _angularVelocities,
                            Forces = _forces,
                            Torques = _torques,
                            InverseMasses = _inverseMasses,
                            InverseInertias = _inverseInertias,
                            Gravity = Gravity,
                            Dt = dt,
                            LinearDamping = LinearDamping,
                            AngularDamping = AngularDamping,
                        });
                    }
                    intBatch.Commit();
                    intBatch.Wait();
                }
                intSw.Stop();
                Stats.IntegrateTimeMs = intSw.Elapsed.TotalMilliseconds;

                // 阶段5: 位置积分(并行)
                using (var posBatch = _jobSystem.CreateBatch())
                {
                    for (int start = 0; start < _bodyCount; start += batchSize)
                    {
                        int end = Math.Min(start + batchSize, _bodyCount);
                        posBatch.Schedule(new IntegratePositionJob
                        {
                            StartIndex = start,
                            EndIndex = end,
                            Positions = _positions,
                            Orientations = _orientations,
                            Velocities = _velocities,
                            AngularVelocities = _angularVelocities,
                            InverseMasses = _inverseMasses,
                            Dt = dt,
                        });
                    }
                    posBatch.Commit();
                    posBatch.Wait();
                }

                // 阶段6: 睡眠检测(并行)
                var sleepSw = Stopwatch.StartNew();
                using (var sleepBatch = _jobSystem.CreateBatch())
                {
                    for (int start = 0; start < _bodyCount; start += batchSize)
                    {
                        int end = Math.Min(start + batchSize, _bodyCount);
                        sleepBatch.Schedule(new SleepCheckJob
                        {
                            StartIndex = start,
                            EndIndex = end,
                            Velocities = _velocities,
                            AngularVelocities = _angularVelocities,
                            InverseMasses = _inverseMasses,
                            Sleeping = _sleeping,
                            SleepTimers = _sleepTimers,
                            LinearThreshold = LinearSleepThreshold,
                            AngularThreshold = AngularSleepThreshold,
                            SleepTime = SleepTime,
                            Dt = dt,
                        });
                    }
                    sleepBatch.Commit();
                    sleepBatch.Wait();
                }
                sleepSw.Stop();
                Stats.SleepTimeMs = sleepSw.Elapsed.TotalMilliseconds;
            }

            totalSw.Stop();
            Stats.TotalTimeMs = totalSw.Elapsed.TotalMilliseconds;
            Stats.BodyCount = _bodyCount;

            // 统计睡眠数量
            int sleepingCount = 0;
            for (int i = 0; i < _bodyCount; i++)
                if (_sleeping[i]) sleepingCount++;
            Stats.SleepingCount = sleepingCount;
        }

        /// <summary>获取body数据(只读)。</summary>
        public ReadOnlySpan<Vector3> Positions => _positions;
        public ReadOnlySpan<Quaternion> Orientations => _orientations;
        public ReadOnlySpan<Vector3> Velocities => _velocities;
        public ReadOnlySpan<Vector3> AngularVelocities => _angularVelocities;
        public ReadOnlySpan<Vector3> AabbMin => _aabbMin;
        public ReadOnlySpan<Vector3> AabbMax => _aabbMax;
        public ReadOnlySpan<bool> Sleeping => _sleeping;

        /// <summary>设置body状态。</summary>
        public void SetBodyState(int index, Vector3 pos, Quaternion rot, Vector3 vel,
            Vector3 angVel, float invMass, Vector3 halfExtents)
        {
            if (index < 0 || index >= _bodyCount) return;
            _positions[index] = pos;
            _orientations[index] = rot;
            _velocities[index] = vel;
            _angularVelocities[index] = angVel;
            _inverseMasses[index] = invMass;
            _halfExtents[index] = halfExtents;
        }

        /// <summary>施加力。</summary>
        public void ApplyForce(int index, Vector3 force)
        {
            if (index < 0 || index >= _bodyCount) return;
            _forces[index] += force;
        }

        /// <summary>施加扭矩。</summary>
        public void ApplyTorque(int index, Vector3 torque)
        {
            if (index < 0 || index >= _bodyCount) return;
            _torques[index] += torque;
        }

        /// <summary>清空所有力和扭矩。</summary>
        public void ClearForces()
        {
            for (int i = 0; i < _bodyCount; i++)
            {
                _forces[i] = Vector3.Zero;
                _torques[i] = Vector3.Zero;
            }
        }

        /// <summary>唤醒所有body。</summary>
        public void WakeAll()
        {
            for (int i = 0; i < _bodyCount; i++)
            {
                _sleeping[i] = false;
                _sleepTimers[i] = 0;
            }
        }

        public void Dispose()
        {
            _disposed = true;
        }
    }

    // ============================================================================================
    //  任务调度策略
    // ============================================================================================

    /// <summary>任务调度策略。</summary>
    public enum ScheduleStrategy
    {
        /// <summary>平均分配。每个worker分到差不多数量的任务。</summary>
        EvenSplit,
        /// <summary>按岛屿大小分配。大岛屿单独跑，小岛屿合并。</summary>
        ByIslandSize,
        /// <summary>工作窃取。空闲worker从其他worker那里偷任务。</summary>
        WorkStealing,
        /// <summary>动态分配。任务队列，谁空闲谁拿。</summary>
        Dynamic,
    }

    /// <summary>任务调度器。根据策略把任务分配给worker。</summary>
    public class JobScheduler
    {
        private readonly PhysicsJobSystem _system;
        private ScheduleStrategy _strategy = ScheduleStrategy.Dynamic;
        private int _targetBatchSize = 64;

        public ScheduleStrategy Strategy
        {
            get => _strategy;
            set => _strategy = value;
        }

        public int TargetBatchSize
        {
            get => _targetBatchSize;
            set => _targetBatchSize = Math.Max(1, value);
        }

        public JobScheduler(PhysicsJobSystem system)
        {
            _system = system ?? throw new ArgumentNullException(nameof(system));
        }

        /// <summary>调度一批岛屿积分任务。</summary>
        public void ScheduleIslandIntegration(List<(int islandId, int bodyCount)> islands,
            Action<int> integrateIsland)
        {
            if (islands.Count == 0) return;

            switch (_strategy)
            {
                case ScheduleStrategy.EvenSplit:
                    ScheduleEvenSplit(islands, integrateIsland);
                    break;
                case ScheduleStrategy.ByIslandSize:
                    ScheduleByIslandSize(islands, integrateIsland);
                    break;
                case ScheduleStrategy.WorkStealing:
                    ScheduleWorkStealing(islands, integrateIsland);
                    break;
                default:
                    ScheduleDynamic(islands, integrateIsland);
                    break;
            }
        }

        private void ScheduleEvenSplit(List<(int islandId, int bodyCount)> islands,
            Action<int> integrateIsland)
        {
            int workers = _system.WorkerCount;
            int islandsPerWorker = (islands.Count + workers - 1) / workers;

            using var batch = _system.CreateBatch();
            for (int w = 0; w < workers; w++)
            {
                int start = w * islandsPerWorker;
                int end = Math.Min(start + islandsPerWorker, islands.Count);
                for (int i = start; i < end; i++)
                {
                    int idx = i;
                    batch.Schedule(new SimpleActionJob(
                        $"IslandIntegrate_{islands[idx].islandId}",
                        () => integrateIsland(islands[idx].islandId)));
                }
            }
            batch.Commit();
            batch.Wait();
        }

        private void ScheduleByIslandSize(List<(int islandId, int bodyCount)> islands,
            Action<int> integrateIsland)
        {
            // 按body数量排序，大的先跑
            islands.Sort((a, b) => b.bodyCount.CompareTo(a.bodyCount));

            using var batch = _system.CreateBatch();
            foreach (var (islandId, _) in islands)
            {
                int id = islandId;
                batch.Schedule(new SimpleActionJob(
                    $"IslandIntegrate_{id}",
                    () => integrateIsland(id)));
            }
            batch.Commit();
            batch.Wait();
        }

        private void ScheduleWorkStealing(List<(int islandId, int bodyCount)> islands,
            Action<int> integrateIsland)
        {
            // 工作窃取: 每个worker有自己的队列，空闲时偷别人的
            // 简化版: 直接用dynamic
            ScheduleDynamic(islands, integrateIsland);
        }

        private void ScheduleDynamic(List<(int islandId, int bodyCount)> islands,
            Action<int> integrateIsland)
        {
            using var batch = _system.CreateBatch();
            foreach (var (islandId, _) in islands)
            {
                int id = islandId;
                batch.Schedule(new SimpleActionJob(
                    $"IslandIntegrate_{id}",
                    () => integrateIsland(id)));
            }
            batch.Commit();
            batch.Wait();
        }
    }

    /// <summary>简单的Action包装job。</summary>
    public class SimpleActionJob : IPhysicsJob
    {
        private readonly Action _action;
        private readonly string _name;

        public string Name => _name;
        public JobPriority Priority => JobPriority.Normal;

        public SimpleActionJob(string name, Action action)
        {
            _name = name;
            _action = action;
        }

        public void Execute() => _action();
    }

    // ============================================================================================
    //  性能监控
    // ============================================================================================

    /// <summary>Job系统性能监控。</summary>
    public class JobSystemMonitor
    {
        private readonly PhysicsJobSystem _system;
        private readonly double[] _history;
        private int _historyIndex;
        private int _historyCount;
        private const int HistorySize = 120; // 2秒@60fps

        public double CurrentUtilization { get; private set; }
        public double AvgUtilization { get; private set; }
        public double MaxUtilization { get; private set; }
        public int PeakQueueLength { get; private set; }

        public JobSystemMonitor(PhysicsJobSystem system)
        {
            _system = system;
            _history = new double[HistorySize];
        }

        /// <summary>每帧更新。</summary>
        public void Update()
        {
            int busy = _system.BusyWorkerCount;
            int total = _system.WorkerCount;
            double util = total > 0 ? (double)busy / total : 0;

            CurrentUtilization = util;
            _history[_historyIndex] = util;
            _historyIndex = (_historyIndex + 1) % HistorySize;
            if (_historyCount < HistorySize) _historyCount++;

            // 计算平均值和最大值
            double sum = 0, max = 0;
            for (int i = 0; i < _historyCount; i++)
            {
                sum += _history[i];
                if (_history[i] > max) max = _history[i];
            }
            AvgUtilization = _historyCount > 0 ? sum / _historyCount : 0;
            MaxUtilization = max;

            int queueLen = _system.PendingJobCount;
            if (queueLen > PeakQueueLength) PeakQueueLength = queueLen;
        }

        /// <summary>重置。</summary>
        public void Reset()
        {
            _historyIndex = 0;
            _historyCount = 0;
            CurrentUtilization = 0;
            AvgUtilization = 0;
            MaxUtilization = 0;
            PeakQueueLength = 0;
        }

        /// <summary>获取利用率历史(用于画图表)。</summary>
        public double[] GetHistory()
        {
            var result = new double[_historyCount];
            for (int i = 0; i < _historyCount; i++)
            {
                int idx = (_historyIndex - _historyCount + i + HistorySize) % HistorySize;
                result[i] = _history[idx];
            }
            return result;
        }
    }
}
