// FlowVFXDepthTests.cs — Unity5 VFX Graph 风格模块化粒子管线深度验证
// 复用 YamaEngine.Particles 的 FlowVFX*Real 系列（SoA 池 / 模块堆栈 / 事件队列 / 发射器状态机）。
// 与既有 Shuriken ParticleDepth 互补：本块覆盖 VFX Graph 的 "上下文+模块堆栈" 模型：
//   * FlowVFXParticleDataReal: SoA 池分配/释放/Reset/GetNormalizedLife
//   * ParticleSorter: 距离降序/生命升序/材质分组
//   * FlowVFXEmitterReal: Rate/Burst/Distance 三模式、Delay/Loop/MaxLoops 状态机
//   * FlowVFXSimulationReal: Spawn/Update/Render 模块堆栈 + 事件队列
//   * 各模块真实行为: Gravity/Drag/Wind/Color/Size/Lifetime/Vortex/CurlNoise
// 全部 CPU 级确定性数值验证。

using System;
using System.Linq;
using System.Numerics;
using YamaEngine.Particles;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class FlowVFXDepthTests
    {
        private static void Near(float got, float want, float tol, string msg)
            => AssertTrue(MathF.Abs(got - want) <= tol,
                $"{msg} (got {got:F4}, want {want:F4}±{tol:F4})");

        private static void NearVec(Vector3 got, Vector3 want, float tol, string msg)
        {
            Near(got.X, want.X, tol, msg + " X");
            Near(got.Y, want.Y, tol, msg + " Y");
            Near(got.Z, want.Z, tol, msg + " Z");
        }

        // =========================================================
        // 1. FlowVFXParticleDataReal 池
        // =========================================================

        /// <summary>Allocate 分配唯一槽位、InitDefaults 写默认值。</summary>
        public static void TestDataAllocateDefaults()
        {
            var data = new FlowVFXParticleDataReal(capacity: 16);
            int idx = data.Allocate();
            AssertTrue(idx == 0, $"首个分配应得槽 0，实际 {idx}。");
            data.InitDefaults(idx);
            AssertTrue(data.Alive[idx], "分配后 Alive 应为 true。");
            AssertEqual(1, data.ActiveCount, "ActiveCount 应为 1。");
            AssertEqual(Vector4.One, data.Color[idx], "默认颜色应为白。");
            AssertEqual(1f, data.Life[idx], "默认生命 1s。");
            AssertEqual(1f, data.Mass[idx], "默认质量 1。");
            AssertEqual(Vector2.One, data.Size[idx], "默认大小 1×1。");
        }

        /// <summary>池满时 Allocate 返回 -1；Release 回收后可复用。</summary>
        public static void TestDataPoolFullAndReuse()
        {
            var data = new FlowVFXParticleDataReal(capacity: 4);
            for (int i = 0; i < 4; i++)
                AssertTrue(data.Allocate() >= 0, $"第 {i + 1} 次分配应成功。");
            AssertEqual(4, data.ActiveCount, "应分配 4 个槽。");
            AssertTrue(data.Allocate() == -1, "池满后分配应返回 -1。");

            int freed = data.ActiveIndices[1];
            data.Release(freed);
            AssertEqual(3, data.ActiveCount, "释放后 ActiveCount 应为 3。");
            int recycled = data.Allocate();
            AssertEqual(freed, recycled, "释放的槽应被复用。");
        }

        /// <summary>GetNormalizedLife = Life/MaxLife（1=刚出生, 0=即将死亡）。</summary>
        public static void TestDataNormalizedLife()
        {
            var data = new FlowVFXParticleDataReal(capacity: 4);
            int idx = data.Allocate();
            data.MaxLife[idx] = 4f;
            data.Life[idx] = 4f;
            Near(1f, data.GetNormalizedLife(idx), 1e-5f, "满生命应归一化为 1");

            data.Life[idx] = 2f;
            Near(0.5f, data.GetNormalizedLife(idx), 1e-5f, "半生命归一化为 0.5");

            data.Life[idx] = 0f;
            Near(0f, data.GetNormalizedLife(idx), 1e-5f, "零生命归一化为 0");

            // MaxLife=0 防退化
            int j = data.Allocate();
            data.MaxLife[j] = 0f;
            Near(0f, data.GetNormalizedLife(j), 1e-5f, "MaxLife=0 应返回 0 防退化。");
        }

        /// <summary>Reset 清空活跃列表、回收所有槽位。</summary>
        public static void TestDataReset()
        {
            var data = new FlowVFXParticleDataReal(capacity: 8);
            for (int i = 0; i < 8; i++) data.Allocate();
            AssertEqual(8, data.ActiveCount, "分配 8 个。");
            data.Reset();
            AssertEqual(0, data.ActiveCount, "Reset 后应为空。");
            AssertTrue(data.Allocate() >= 0, "Reset 后应可重新分配。");
        }

        // =========================================================
        // 2. ParticleSorter
        // =========================================================

        private static FlowVFXParticleDataReal BuildSortFixture(Vector3 camPos, out int[] order)
        {
            var data = new FlowVFXParticleDataReal(capacity: 4);
            // 4 个粒子：不同深度（Z 距离相机）、不同生命、不同材质 ID
            var positions = new[] {
                new Vector3(0, 0, -1f),  // 近
                new Vector3(0, 0, -5f),  // 远
                new Vector3(0, 0, -2f),  // 中
                new Vector3(0, 0, -3f),  // 中远
            };
            for (int i = 0; i < 4; i++)
            {
                int idx = data.Allocate();
                data.Position[idx] = positions[i];
                data.Life[idx] = 4f - i;         // 4,3,2,1
                data.CustomData[idx] = new Vector4(i % 2, 0, 0, 0); // 材质 ID 0/1/0/1
            }
            order = data.ActiveIndices.ToArray();
            return data;
        }

        /// <summary>距离降序排序：远→近（距离大→小）。</summary>
        public static void TestSortDistanceDescending()
        {
            var cam = new Vector3(0, 0, 0);
            var data = BuildSortFixture(cam, out _);
            ParticleSorter.Sort(data, RealParticleSortMode.DistanceToCameraDescending, cam);

            // 期望 Z 距离：5,3,2,1
            float[] expectedZ = { -5f, -3f, -2f, -1f };
            for (int i = 0; i < 4; i++)
                Near(expectedZ[i], data.Position[data.ActiveIndices[i]].Z, 1e-4f, $"排序位置 {i}");
        }

        /// <summary>生命升序排序：即将死亡的在前。</summary>
        public static void TestSortLifeAscending()
        {
            var data = BuildSortFixture(Vector3.Zero, out _);
            ParticleSorter.Sort(data, RealParticleSortMode.LifeAscending, Vector3.Zero);

            for (int i = 0; i < 4; i++)
                AssertEqual(i + 1, (int)data.Life[data.ActiveIndices[i]], $"位置 {i} 生命应升序。");
        }

        /// <summary>按材质 ID 排序（CustomData.X）。</summary>
        public static void TestSortByMaterial()
        {
            var data = BuildSortFixture(Vector3.Zero, out _);
            ParticleSorter.Sort(data, RealParticleSortMode.ByMaterial, Vector3.Zero);

            int[] expected = { 0, 0, 1, 1 };
            for (int i = 0; i < 4; i++)
                AssertEqual(expected[i], (int)data.CustomData[data.ActiveIndices[i]].X, $"位置 {i} 材质分组。");
        }

        // =========================================================
        // 3. FlowVFXEmitterReal 状态机与三模式
        // =========================================================

        /// <summary>Rate 模式：每秒 60 个，1/60s 应发 1 个（含累积）。</summary>
        public static void TestEmitterRateMode()
        {
            var e = new FlowVFXEmitterReal
            {
                SpawnMode = RealSpawnMode.Rate,
                SpawnRate = 60f,
                DelayDuration = 0f,
                LoopDuration = 5f,
                MaxLoops = -1
            };
            e.Reset();

            var dt = 1f / 60f;
            int total = 0;
            for (int frame = 0; frame < 60; frame++)
            {
                if (e.Tick(dt, out var req)) total += req.Count;
            }
            AssertTrue(total >= 58 && total <= 62,
                $"60 帧×(60/s) 应约 60 个粒子，实际 {total}。");
        }

        /// <summary>Delay 期间不发；Delay 结束后开始发。</summary>
        public static void TestEmitterDelayGate()
        {
            var e = new FlowVFXEmitterReal
            {
                SpawnMode = RealSpawnMode.Rate,
                SpawnRate = 100f,
                DelayDuration = 0.5f,
                LoopDuration = 10f,
                MaxLoops = -1
            };
            e.Reset();

            int duringDelay = 0;
            for (int i = 0; i < 15; i++)  // 0.25s < 0.5s delay
                if (e.Tick(1f / 60f, out var r)) duringDelay += r.Count;
            AssertEqual(0, duringDelay, "延迟期间不应发射。");

            int afterDelay = 0;
            for (int i = 0; i < 30; i++)  // 0.25–0.75s
                if (e.Tick(1f / 60f, out var r)) afterDelay += r.Count;
            AssertTrue(afterDelay > 0, "延迟结束后应开始发射。");
        }

        /// <summary>MaxLoops 限制循环：单循环 Duration=0.5s、MaxLoops=2 → 约 1s 后 End。</summary>
        public static void TestEmitterMaxLoopsEnds()
        {
            var e = new FlowVFXEmitterReal
            {
                SpawnMode = RealSpawnMode.Rate,
                SpawnRate = 0f,     // 关闭 rate，只关心状态机
                DelayDuration = 0f,
                LoopDuration = 0.5f,
                MaxLoops = 2
            };
            e.Reset();

            float elapsed = 0f;
            bool ended = false;
            for (int frame = 0; frame < 200; frame++)
            {
                bool alive = e.Tick(1f / 60f, out _);
                elapsed += 1f / 60f;
                if (!alive) { ended = true; break; }
            }
            AssertTrue(ended, "MaxLoops=2 应让发射器在 2 个循环后结束。");
            AssertTrue(elapsed >= 0.9f && elapsed <= 1.5f,
                $"应在约 1s 后结束，实际 {elapsed:F2}s。");
        }

        /// <summary>Burst 列表按时间触发一次。</summary>
        public static void TestEmitterBurstMode()
        {
            var e = new FlowVFXEmitterReal
            {
                SpawnMode = RealSpawnMode.Burst,
                SpawnRate = 0f,
                DelayDuration = 0f,
                LoopDuration = 10f,
                MaxLoops = -1
            };
            e.Bursts.Add(new SpawnBurst(time: 0.5f, count: 20));
            e.Reset();

            int total = 0;
            for (int frame = 0; frame < 90; frame++)  // 1.5s
            {
                if (e.Tick(1f / 60f, out var r)) total += r.Count;
            }
            AssertEqual(20, total, "Burst 触发应正好发射 20 个（仅触发一次）。");
        }

        // =========================================================
        // 4. 模块真实行为
        // =========================================================

        /// <summary>GravityModule 让速度沿重力方向累积（a=g·dt，与质量无关时）。</summary>
        public static void TestModuleGravity()
        {
            var data = new FlowVFXParticleDataReal(capacity: 4);
            int idx = data.Allocate();
            data.InitDefaults(idx);
            data.Velocity[idx] = Vector3.Zero;
            data.Mass[idx] = 1f;

            var g = new GravityModule { Direction = new Vector3(0, -1, 0), Strength = 9.81f, UseMass = false };
            g.Execute(data, 0, data.ActiveCount, dt: 1f);

            NearVec(data.Velocity[idx], new Vector3(0, -9.81f, 0), 1e-3f, "重力 1s 后速度");
        }

        /// <summary>DragModule 让速度衰减（Quadratic=false 为线性阻力）。</summary>
        public static void TestModuleDragLinear()
        {
            var data = new FlowVFXParticleDataReal(capacity: 4);
            int idx = data.Allocate();
            data.InitDefaults(idx);
            data.Velocity[idx] = new Vector3(10, 0, 0);

            var drag = new DragModule { Drag = 0.5f, Quadratic = false };
            drag.Execute(data, 0, data.ActiveCount, dt: 1f);

            // 线性阻力: v *= (1 - drag*dt) = 0.5 → 5
            Near(data.Velocity[idx].X, 5f, 1e-3f, "线性阻力 1s 后 X 速度");
        }

        /// <summary>ColorUpdateModule 按生命 t 插值 StartColor→EndColor。</summary>
        public static void TestModuleColorUpdate()
        {
            var data = new FlowVFXParticleDataReal(capacity: 4);
            int idx = data.Allocate();
            data.InitDefaults(idx);
            data.MaxLife[idx] = 2f;
            data.Life[idx] = 1f;  // t=0.5 老化程度
            data.Color[idx] = Vector4.One;

            var color = new ColorUpdateModule
            {
                StartColor = new Vector4(1, 0, 0, 1),
                EndColor = new Vector4(0, 0, 1, 0),
                FadeAlphaOnly = false
            };
            color.Execute(data, 0, data.ActiveCount, dt: 0f);

            Near(data.Color[idx].X, 0.5f, 1e-3f, "颜色 R 中点");
            Near(data.Color[idx].Z, 0.5f, 1e-3f, "颜色 B 中点");
            Near(data.Color[idx].W, 0.5f, 1e-3f, "透明度 A 中点");
        }

        /// <summary>SizeUpdateModule 按生命 t 插值 StartSize→EndSize。</summary>
        public static void TestModuleSizeUpdate()
        {
            var data = new FlowVFXParticleDataReal(capacity: 4);
            int idx = data.Allocate();
            data.InitDefaults(idx);
            data.MaxLife[idx] = 4f;
            data.Life[idx] = 1f;  // t=0.75

            var size = new SizeUpdateModule
            {
                StartSize = new Vector2(0.4f, 0.4f),
                EndSize = new Vector2(0.0f, 0.0f)
            };
            size.Execute(data, 0, data.ActiveCount, dt: 0f);

            Near(data.Size[idx].X, 0.1f, 1e-3f, "大小 X 在 t=0.75 应剩 1/4");
            Near(data.Size[idx].Y, 0.1f, 1e-3f, "大小 Y");
        }

        /// <summary>VortexModule 产生切向速度（vortex 只改速度，不改位置）。</summary>
        public static void TestModuleVortexTangential()
        {
            var data = new FlowVFXParticleDataReal(capacity: 4);
            int idx = data.Allocate();
            data.InitDefaults(idx);
            data.Position[idx] = new Vector3(1, 0, 0);   // 距轴原点 1
            data.Velocity[idx] = Vector3.Zero;

            var vortex = new VortexModule
            {
                AxisOrigin = Vector3.Zero,
                AxisDirection = new Vector3(0, 1, 0),
                AngularSpeed = MathF.PI,   // 半圈/秒
                AxialForce = 0f,
                RadialForce = 0f
            };
            vortex.Execute(data, 0, data.ActiveCount, dt: 1f);

            // tangent = axis × radial = Y × X = -Z → 速度沿 -Z 方向
            NearVec(data.Velocity[idx], new Vector3(0, 0, -MathF.PI), 1e-3f, "旋涡切向速度");
        }

        /// <summary>OrbitModule 围绕中心旋转 + RadiusDecay 径向收缩。</summary>
        public static void TestModuleOrbitDecay()
        {
            var data = new FlowVFXParticleDataReal(capacity: 4);
            int idx = data.Allocate();
            data.InitDefaults(idx);
            data.Position[idx] = new Vector3(1, 0, 0);

            var orbit = new OrbitModule
            {
                Center = Vector3.Zero,
                Axis = new Vector3(0, 1, 0),
                AngularSpeed = 0f,          // 不旋转
                RadiusDecay = 1f            // 每秒径向缩 1
            };
            orbit.Execute(data, 0, data.ActiveCount, dt: 0.5f);

            // AngularSpeed=0 → 不旋转；RadiusDecay*dt = 0.5 → 径向缩 0.5 → 半径 0.5
            float radius = data.Position[idx].Length();
            Near(radius, 0.5f, 1e-3f, "轨道收缩后半径");
        }

        /// <summary>VelocityConstrainModule LockY 锁定 Y 轴速度。</summary>
        public static void TestModuleVelocityLock()
        {
            var data = new FlowVFXParticleDataReal(capacity: 4);
            int idx = data.Allocate();
            data.InitDefaults(idx);
            data.Velocity[idx] = new Vector3(3, 4, 5);

            var vc = new VelocityConstrainModule { LockY = true, MinSpeed = 0f, MaxSpeed = float.MaxValue };
            vc.Execute(data, 0, data.ActiveCount, dt: 0f);

            Near(3f, data.Velocity[idx].X, 1e-5f, "X 速度不变");
            Near(0f, data.Velocity[idx].Y, 1e-5f, "Y 速度被锁");
            Near(5f, data.Velocity[idx].Z, 1e-5f, "Z 速度不变");
        }

        /// <summary>CurlNoiseForceModule 对静止粒子施加非零速度扰动。</summary>
        public static void TestModuleCurlNoiseNonZero()
        {
            var data = new FlowVFXParticleDataReal(capacity: 4);
            int idx = data.Allocate();
            data.InitDefaults(idx);
            data.Position[idx] = new Vector3(0.5f, 0.3f, 0.7f);
            data.Velocity[idx] = Vector3.Zero;

            var curl = new CurlNoiseForceModule
            {
                Strength = 10f,
                Frequency = 1.0f,
                TimeScale = 0.0f,   // 静止时间，确保非零 curl
                Octaves = 2
            };
            curl.Execute(data, 0, data.ActiveCount, dt: 1f);

            float speed = data.Velocity[idx].Length();
            AssertTrue(speed > 1e-4f, $"Curl 噪声应产生非零速度，实际 {speed:F6}。");
        }

        // =========================================================
        // 5. FlowVFXSimulationReal 管线
        // =========================================================

        /// <summary>Spawner.Rate + Simulation：发射粒子数符合预期。</summary>
        public static void TestSimSpawnerRate()
        {
            var sim = new FlowVFXSimulationReal(maxParticles: 128)
            {
                EmitterPosition = Vector3.Zero,
                IsRunning = true,
                IsPaused = false
            };
            sim.Spawner.SpawnRate = 60f;
            sim.Spawner.EnableRate = true;

            for (int i = 0; i < 60; i++) sim.Simulate(1f / 60f);

            // Spawner.TotalSpawned 记录累积发射数（外部 SpawnNewParticles 不更新 innerSim.TotalSpawned）
            long total = sim.Spawner.TotalSpawned;
            AssertTrue(total >= 55 && total <= 65, $"60 帧×60/s 应约 60 个粒子，实际 {total}。");
        }

        /// <summary>IsPaused 暂停时不更新；IsRunning=false 停止。</summary>
        public static void TestSimPauseStop()
        {
            var sim = new FlowVFXSimulationReal(maxParticles: 32);
            sim.Spawner.SpawnRate = 60f;
            sim.Spawner.EnableRate = true;

            sim.IsPaused = true;
            for (int i = 0; i < 30; i++) sim.Simulate(1f / 60f);
            AssertEqual(0, sim.Spawner.TotalSpawned, "暂停时应无粒子发射。");

            sim.IsPaused = false;
            sim.IsRunning = false;
            for (int i = 0; i < 30; i++) sim.Simulate(1f / 60f);
            AssertEqual(0, sim.Spawner.TotalSpawned, "停止时应无粒子发射。");
        }

        /// <summary>Gravity + LifetimeUpdate：粒子受重力下落、生命归零后被淘汰。</summary>
        public static void TestSimGravityAndCull()
        {
            var sim = new FlowVFXSimulationReal(maxParticles: 32);
            sim.Spawner.SpawnRate = 60f;
            sim.Spawner.EnableRate = true;
            sim.AddUpdateModule(new GravityModule { Direction = new Vector3(0, -1, 0), Strength = 9.81f, UseMass = false });
            sim.AddUpdateModule(new LifetimeUpdateModule());

            // 发射 1 帧 → ~1 粒子
            sim.Simulate(1f / 60f);
            int spawned = sim.Data.ActiveCount;
            AssertTrue(spawned >= 1, "首帧应发射至少 1 粒子。");

            // 停止发射，让现有粒子自然死亡
            sim.Spawner.EnableRate = false;
            sim.Spawner.SpawnRate = 0f;

            // 模拟 5s，让生命归零（默认生命 1–3s）
            for (int i = 0; i < 300; i++) sim.Simulate(1f / 60f);

            // 所有粒子都应已淘汰
            AssertEqual(0, sim.Data.ActiveCount, "5s 后所有粒子应死亡淘汰。");
        }

        /// <summary>事件队列：粒子死亡产生 Death 事件。</summary>
        public static void TestSimDeathEvents()
        {
            var sim = new FlowVFXSimulationReal(maxParticles: 32);
            sim.Spawner.SpawnRate = 0f;
            sim.Spawner.EnableRate = false;

            // 手动分配一个粒子，设生命极短
            int idx = sim.Data.Allocate();
            sim.Data.InitDefaults(idx);
            sim.Data.Life[idx] = 0.01f;
            sim.Data.MaxLife[idx] = 0.01f;
            sim.Data.Position[idx] = new Vector3(1, 2, 3);
            sim.Data.Velocity[idx] = new Vector3(0, 5, 0);
            sim.AddUpdateModule(new LifetimeUpdateModule());

            sim.Simulate(1f / 60f);   // 一帧后生命归零淘汰

            AssertEqual(0, sim.Data.ActiveCount, "粒子应被淘汰。");
            AssertTrue(sim.Events.Count >= 1, $"应有至少 1 个 Death 事件，实际 {sim.Events.Count}。");

            var evt = sim.Events.Events[0];
            AssertEqual(ParticleEventType.Died, evt.Type, "事件类型应为 Died。");
            AssertEqual(idx, evt.ParticleIndex, "事件 ParticleIndex 应匹配。");
        }

        /// <summary>Spawn+Update+Death 完整管线：颜色渐变贯穿粒子生命周期。</summary>
        public static void TestSimFullPipelineColorFade()
        {
            var sim = new FlowVFXSimulationReal(maxParticles: 8);
            sim.Spawner.SpawnRate = 60f;
            sim.Spawner.EnableRate = true;

            // 出生颜色红，死亡颜色蓝
            sim.AddSpawnModule(new InitializeColorModule
            {
                ColorMin = new Vector4(1, 0, 0, 1),
                ColorMax = new Vector4(1, 0, 0, 1)
            });
            sim.AddUpdateModule(new ColorUpdateModule
            {
                StartColor = new Vector4(1, 0, 0, 1),
                EndColor = new Vector4(0, 0, 1, 1),
                FadeAlphaOnly = false
            });
            // 强制 1s 生命
            sim.AddSpawnModule(new InitializeLifeModule { LifeMin = 1f, LifeMax = 1f });
            sim.AddUpdateModule(new LifetimeUpdateModule());

            sim.Simulate(1f / 60f);  // 发射首帧
            AssertTrue(sim.Data.ActiveCount >= 1, "应发射至少 1 粒子。");

            int idx = sim.Data.ActiveIndices[0];
            float rStart = sim.Data.Color[idx].X;
            float bStart = sim.Data.Color[idx].Z;
            AssertTrue(rStart > 0.9f, $"出生颜色应偏红，实际 R={rStart:F3}。");
            AssertTrue(bStart < 0.1f, $"出生颜色应低蓝，实际 B={bStart:F3}。");

            // 半生命周期后颜色应偏向蓝（半程时 R≈B≈0.5，加一点余量断言 R<=B）
            for (int i = 0; i < 30; i++) sim.Simulate(1f / 60f);   // 累计 0.5s
            if (sim.Data.ActiveCount > 0)
            {
                int j = sim.Data.ActiveIndices[0];
                float rMid = sim.Data.Color[j].X;
                float bMid = sim.Data.Color[j].Z;
                AssertTrue(bMid >= rMid - 0.05f, $"半生命后应偏蓝（允许浮点余量），实际 R={rMid:F3} B={bMid:F3}。");
            }
        }

        /// <summary>FrameBudgetManager 粒子预算：MaxActiveParticles 满时不超发。</summary>
        public static void TestSimParticleBudget()
        {
            var sim = new FlowVFXSimulationReal(maxParticles: 16);
            sim.Spawner.SpawnRate = 1000f;   // 极高发射率
            sim.Spawner.EnableRate = true;
            sim.Budget.MaxSpawnPerFrame = 4;   // 限制首帧发射数
            sim.Budget.MaxActiveParticles = 8; // 预算限制 8

            for (int i = 0; i < 30; i++) sim.Simulate(1f / 60f);

            AssertTrue(sim.Data.ActiveCount <= 8, $"活跃数应不超预算 8，实际 {sim.Data.ActiveCount}。");
        }

        /// <summary>TotalSpawned 累计统计正确。</summary>
        public static void TestSimTotalSpawned()
        {
            var sim = new FlowVFXSimulationReal(maxParticles: 128);
            sim.Spawner.SpawnRate = 30f;
            sim.Spawner.EnableRate = true;

            for (int i = 0; i < 60; i++) sim.Simulate(1f / 60f);

            long total = sim.Spawner.TotalSpawned;
            AssertTrue(total >= 28 && total <= 32, $"60 帧×30/s 应约 30 个，实际 {total}。");
        }
    }
}
