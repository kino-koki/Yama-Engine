using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.Core
{
    public class EngineTime
    {
        public float DeltaTime;
        public float UnscaledDeltaTime;
        public float FixedDeltaTime = 1f / 60f;
        public float TimeScale = 1f;
        public float TotalTime;
        public float RealTotalTime;
        public int FrameCount;
        public float SmoothFrameRate = 60f;
        private float _frameRateAccumulator;
        private int _frameRateSamples;

        public void Update(float dt)
        {
            UnscaledDeltaTime = dt;
            DeltaTime = dt * TimeScale;
            TotalTime += DeltaTime;
            RealTotalTime += dt;
            FrameCount++;

            _frameRateAccumulator += dt;
            _frameRateSamples++;
            if (_frameRateAccumulator >= 0.5f)
            {
                float instantRate = _frameRateSamples / _frameRateAccumulator;
                SmoothFrameRate = SmoothFrameRate * 0.9f + instantRate * 0.1f;
                _frameRateAccumulator = 0f;
                _frameRateSamples = 0;
            }
        }

        public void Reset()
        {
            DeltaTime = 0;
            TotalTime = 0;
            RealTotalTime = 0;
            FrameCount = 0;
        }
    }

    public class EnginePerformance
    {
        public float UpdateTime;
        public float RenderTime;
        public float PhysicsTime;
        public float AudioTime;
        public float ParticleTime;
        public float ScriptTime;
        public float NetworkTime;
        public float WorldStreamingGridTime;
        public float CPUTime;
        public float GPUAvailableMemoryMB = 8192f;
        public float GPUUsedMemoryMB = 2340f;
        public long ManagedMemoryKB;
        public long TotalProcessMemoryKB;
        public int DrawCalls;
        public int TriangleCount;
        public int VertexCount;
        public int TextureCount;
        public int MaterialCount;

        public void Reset()
        {
            UpdateTime = 0;
            RenderTime = 0;
            PhysicsTime = 0;
            AudioTime = 0;
            ParticleTime = 0;
            ScriptTime = 0;
            NetworkTime = 0;
            WorldStreamingGridTime = 0;
            DrawCalls = 0;
            TriangleCount = 0;
            VertexCount = 0;
        }
    }

    public class EngineScene
    {
        public string Name = "Untitled";
        public string Path = "";
        public readonly List<SceneObject> Objects = new();
        public Vector3 AmbientColor = new(0.2f, 0.2f, 0.25f);
        public Vector3 SkyColor = new(0.53f, 0.81f, 0.92f);
        public float FogDistance = 500f;
        public float FogDensity = 0.01f;
        public Vector3 FogColor = new(0.7f, 0.75f, 0.8f);
        public float GravityStrength = 9.81f;
        public bool IsDirty;
        public bool IsActive = true;

        public void AddObject(SceneObject obj)
        {
            Objects.Add(obj);
            IsDirty = true;
        }

        public bool RemoveObject(Guid id)
        {
            for (int i = 0; i < Objects.Count; i++)
            {
                if (Objects[i].Id == id)
                {
                    Objects.RemoveAt(i);
                    IsDirty = true;
                    return true;
                }
            }
            return false;
        }

        public SceneObject? FindObject(Guid id)
        {
            foreach (var obj in Objects)
                if (obj.Id == id) return obj;
            return null;
        }

        public void Clear()
        {
            Objects.Clear();
            IsDirty = true;
        }
    }

    public class SceneObject
    {
        public Guid Id = Guid.NewGuid();
        public string Name = "New Entity";
        public string Type = "Empty";
        public Vector3 Position;
        public Quaternion Rotation = Quaternion.Identity;
        public Vector3 Scale = new(1, 1, 1);
        public bool IsVisible = true;
        public bool IsSelected;
        public readonly Dictionary<string, object> Properties = new();
        public readonly List<SceneObject> Children = new();
        public Guid ParentId;
        public int Layer;
        public uint LayerMask = 0xFFFFFFFF;
        public bool IsStatic;
        public Guid MeshAssetId;
        public Guid MaterialId;
        public Vector3 BoundsCenter;
        public Vector3 BoundsSize = new(1, 1, 1);
        public float Radius = 0.5f;

        public Matrix4x4 Transform
        {
            get
            {
                return Matrix4x4.CreateScale(Scale) *
                       Matrix4x4.CreateFromQuaternion(Rotation) *
                       Matrix4x4.CreateTranslation(Position);
            }
        }

        public Vector3 Forward => Vector3.Transform(new Vector3(0, 0, -1), Rotation);
        public Vector3 Right => Vector3.Transform(new Vector3(1, 0, 0), Rotation);
        public Vector3 Up => Vector3.Transform(new Vector3(0, 1, 0), Rotation);
    }

    public class SceneCamera
    {
        public string Name = "Main Camera";
        public Vector3 Position = new(0, 2, 5);
        public Quaternion Rotation = Quaternion.Identity;
        public float FieldOfView = 70f;
        public float AspectRatio = 16f / 9f;
        public float NearClip = 0.1f;
        public float FarClip = 1000f;
        public float OrthographicSize = 5f;
        public bool IsOrthographic;
        public bool IsMain = true;
        public Vector3 ClearColor = new(0.53f, 0.81f, 0.92f);
        public float MovementSpeed = 10f;
        public bool ShowGrid = true;
        public bool FrustumCulling = true;

        public Matrix4x4 ViewMatrix
        {
            get => Matrix4x4.CreateLookAt(Position, Position + Forward, Up);
        }

        public Matrix4x4 ProjectionMatrix
        {
            get
            {
                if (IsOrthographic)
                    return Matrix4x4.CreateOrthographic(OrthographicSize * 2f * AspectRatio, OrthographicSize * 2f, NearClip, FarClip);
                else
                    return Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView * MathF.PI / 180f, AspectRatio, NearClip, FarClip);
            }
        }

        public Vector3 Forward => Vector3.Transform(new Vector3(0, 0, -1), Rotation);
        public Vector3 Right => Vector3.Transform(new Vector3(1, 0, 0), Rotation);
        public Vector3 Up => Vector3.Transform(new Vector3(0, 1, 0), Rotation);

        public void Move(Vector3 offset) => Position += offset;
        public void Rotate(float yaw, float pitch)
        {
            var qYaw = Quaternion.CreateFromYawPitchRoll(yaw, 0, 0);
            var qPitch = Quaternion.CreateFromYawPitchRoll(0, pitch, 0);
            Rotation = Quaternion.Normalize(Rotation * qYaw * qPitch);
        }
    }
}
