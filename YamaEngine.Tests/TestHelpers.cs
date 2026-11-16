using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using YamaEngine.Assets;
using YamaEngine.Gameplay;
using YamaEngine.Input;
using YamaEngine.Modeling;
using YamaEngine.Runtime;
using YamaEngine.Scenes;

namespace YamaEngine.Tests
{
    public static class TestHelpers
    {
        /// <summary>
        /// 全局失败计数器。各测试方法抛出的异常由 Program 的分组 catch 捕获，
        /// 分组应调用 RecordFailure() 累加，最终由 Program 据此设置进程退出码，
        /// 确保 CI 能正确判定测试失败（避免"打印 FAIL 但退出 0"）。
        /// </summary>
        public static int GlobalFailures = 0;

        public static void RecordFailure() => GlobalFailures++;

        public static void AssertTrue(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        public static void AssertEqual<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new InvalidOperationException($"{message} Expected: {expected}; Actual: {actual}");
            }
        }

        /// <summary>
        /// 断言执行 action 抛出指定类型异常（或其子类）。
        /// </summary>
        public static void AssertThrows<TEx>(Action action, string message) where TEx : Exception
        {
            try
            {
                action();
            }
            catch (TEx)
            {
                return; // 符合预期
            }
            catch (Exception other)
            {
                throw new InvalidOperationException($"{message} 期望异常 {typeof(TEx).Name}，实际抛出 {other.GetType().Name}: {other.Message}");
            }
            throw new InvalidOperationException($"{message} 期望抛出 {typeof(TEx).Name}，但未抛任何异常");
        }

        public static EditableMesh CreateSculptPlane()
        {
            var mesh = new EditableMesh { Name = "SculptPlane" };
            mesh.Vertices.AddRange(new[]
            {
                new Vector3(-1, -1, 0),
                new Vector3( 0, -1, 0),
                new Vector3( 1, -1, 0),
                new Vector3(-1,  0, 0),
                new Vector3( 0,  0, 0),
                new Vector3( 1,  0, 0),
                new Vector3(-1,  1, 0),
                new Vector3( 0,  1, 0),
                new Vector3( 1,  1, 0)
            });
            mesh.UV0.AddRange(Enumerable.Repeat(Vector2.Zero, mesh.Vertices.Count));
            mesh.Faces.AddRange(new[]
            {
                new MeshFace(0, 1, 4), new MeshFace(0, 4, 3),
                new MeshFace(1, 2, 5), new MeshFace(1, 5, 4),
                new MeshFace(3, 4, 7), new MeshFace(3, 7, 6),
                new MeshFace(4, 5, 8), new MeshFace(4, 8, 7)
            });
            mesh.RecalculateNormals();
            return mesh;
        }

        public static float TriangleArea(EditableMesh mesh, MeshFace face)
        {
            var a = mesh.Vertices[face.A];
            var b = mesh.Vertices[face.B];
            var c = mesh.Vertices[face.C];
            return 0.5f * Vector3.Cross(b - a, c - a).Length();
        }
    }

    sealed class RuntimeCounterComponent : IComponent
    {
        private readonly Vector3 _velocity;

        public string Name => "RuntimeCounter";
        public int UpdateCount { get; private set; }
        public float TotalDeltaSeconds { get; private set; }

        public RuntimeCounterComponent(Vector3 velocity)
        {
            _velocity = velocity;
        }

        public void Update(float deltaTime, SceneObject owner)
        {
            UpdateCount++;
            TotalDeltaSeconds += deltaTime;
            owner.Position += _velocity * deltaTime;
        }
    }

    sealed class RuntimeInputMoverComponent : IComponent, IRuntimeInputComponent
    {
        private readonly float _speed;

        public string Name => "RuntimeInputMover";
        public int InputUpdateCount { get; private set; }
        public int RegularUpdateCount { get; private set; }
        public Vector2 LastMovement { get; private set; }

        public RuntimeInputMoverComponent(float speed)
        {
            _speed = speed;
        }

        public void UpdateInput(float deltaTime, SceneObject owner, InputSystem input)
        {
            InputUpdateCount++;
            LastMovement = input.GetMovementVector();
            owner.Position += new Vector3(LastMovement.X, 0, LastMovement.Y) * _speed * deltaTime;
        }

        public void Update(float deltaTime, SceneObject owner)
        {
            RegularUpdateCount++;
        }
    }

    sealed class TrackingActor : Actor
    {
        public int BeginPlayCount { get; private set; }
        public int TickCount { get; private set; }
        public int EndPlayCount { get; private set; }

        protected override void OnBeginPlay() => BeginPlayCount++;
        protected override void OnTick(float deltaTime) => TickCount++;
        protected override void OnEndPlay() => EndPlayCount++;
    }

    sealed class TrackingComponent : ActorComponent
    {
        public int BeginPlayCount { get; private set; }
        public int TickCount { get; private set; }
        public int EndPlayCount { get; private set; }

        protected override void OnBeginPlay() => BeginPlayCount++;
        protected override void OnTick(float deltaTime) => TickCount++;
        protected override void OnEndPlay() => EndPlayCount++;
    }
}
