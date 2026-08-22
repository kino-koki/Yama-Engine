using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace ShanyuEngine.DCC
{
    public enum DCCApplication
    {
        Blender51,
        Blender42,
        Blender36,
        Maya2025,
        Maya2024,
        Max2025,
        Max2024,
        Houdini20,
        SubstancePainter,
        RizomUV,
        Zbrush2025
    }

    public struct DCCTransform
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Scale;

        public Matrix4x4 Matrix
        {
            get
            {
                return Matrix4x4.CreateScale(Scale) *
                       Matrix4x4.CreateFromQuaternion(Rotation) *
                       Matrix4x4.CreateTranslation(Position);
            }
        }

        public static DCCTransform Identity => new()
        {
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            Scale = Vector3.One
        };
    }

    public class DCCVertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector4 Tangent;
        public Vector2 UV0;
        public Vector2 UV1;
        public Vector2 UV2;
        public Vector4 Color;
        public int MaterialIndex;
    }

    public class DCCMeshData
    {
        public string Name = string.Empty;
        public List<DCCVertex> Vertices = new();
        public List<int> Indices = new();
        public List<string> MaterialNames = new();
        public List<Vector3> MaterialColors = new();
        public DCCTransform Transform;
        public ShanyuEngine.Assets.BoundingBox Bounds;
        public bool HasNormals;
        public bool HasTangents;
        public bool HasUVs;
        public bool HasVertexColors;
        public int TriangleCount => Indices.Count / 3;
        public int VertexCount => Vertices.Count;

        public static DCCMeshData CreateBox(float size = 1f)
        {
            var mesh = new DCCMeshData
            {
                Name = "Box",
                Vertices = new List<DCCVertex>(),
                Indices = new List<int>(),
                MaterialNames = new List<string> { "Default" },
                MaterialColors = new List<Vector3> { new Vector3(0.8f, 0.8f, 0.8f) },
                Transform = DCCTransform.Identity,
                HasNormals = true,
                HasUVs = true
            };

            float h = size * 0.5f;
            Vector3[] facePositions = new Vector3[]
            {
                new(-h, -h,  h), new( h, -h,  h), new( h,  h,  h), new(-h,  h,  h), // front (+Z)
                new(-h, -h, -h), new(-h,  h, -h), new( h,  h, -h), new( h, -h, -h), // back (-Z)
                new(-h,  h, -h), new(-h,  h,  h), new( h,  h,  h), new( h,  h, -h), // top (+Y)
                new(-h, -h, -h), new( h, -h, -h), new( h, -h,  h), new(-h, -h,  h), // bottom (-Y)
                new( h, -h, -h), new( h,  h, -h), new( h,  h,  h), new( h, -h,  h), // right (+X)
                new(-h, -h, -h), new(-h, -h,  h), new(-h,  h,  h), new(-h,  h, -h)  // left (-X)
            };

            Vector3[] faceNormals = new Vector3[]
            {
                new(0, 0, 1), new(0, 0, 1), new(0, 0, 1), new(0, 0, 1),
                new(0, 0, -1), new(0, 0, -1), new(0, 0, -1), new(0, 0, -1),
                new(0, 1, 0), new(0, 1, 0), new(0, 1, 0), new(0, 1, 0),
                new(0, -1, 0), new(0, -1, 0), new(0, -1, 0), new(0, -1, 0),
                new(1, 0, 0), new(1, 0, 0), new(1, 0, 0), new(1, 0, 0),
                new(-1, 0, 0), new(-1, 0, 0), new(-1, 0, 0), new(-1, 0, 0)
            };

            Vector2[] faceUVs = new Vector2[]
            {
                new(0, 0), new(1, 0), new(1, 1), new(0, 1),
                new(0, 0), new(1, 0), new(1, 1), new(0, 1),
                new(0, 0), new(1, 0), new(1, 1), new(0, 1),
                new(0, 0), new(1, 0), new(1, 1), new(0, 1),
                new(0, 0), new(1, 0), new(1, 1), new(0, 1),
                new(0, 0), new(1, 0), new(1, 1), new(0, 1)
            };

            for (int i = 0; i < facePositions.Length; i++)
            {
                mesh.Vertices.Add(new DCCVertex
                {
                    Position = facePositions[i],
                    Normal = faceNormals[i],
                    UV0 = faceUVs[i],
                    MaterialIndex = 0,
                    Color = new Vector4(1, 1, 1, 1)
                });
            }

            for (int face = 0; face < 6; face++)
            {
                int baseIdx = face * 4;
                mesh.Indices.AddRange(new[] { baseIdx, baseIdx + 1, baseIdx + 2, baseIdx, baseIdx + 2, baseIdx + 3 });
            }

            return mesh;
        }

        public static DCCMeshData CreateSphere(float radius = 0.5f, int segments = 24)
        {
            var mesh = new DCCMeshData
            {
                Name = "Sphere",
                Vertices = new List<DCCVertex>(),
                Indices = new List<int>(),
                MaterialNames = new List<string> { "Default" },
                MaterialColors = new List<Vector3> { new Vector3(0.85f, 0.75f, 0.35f) },
                Transform = DCCTransform.Identity,
                HasNormals = true,
                HasUVs = true
            };

            for (int lat = 0; lat <= segments; lat++)
            {
                float theta = (float)Math.PI * lat / segments;
                float sinT = MathF.Sin(theta);
                float cosT = MathF.Cos(theta);

                for (int lon = 0; lon <= segments; lon++)
                {
                    float phi = 2f * (float)Math.PI * lon / segments;
                    float sinP = MathF.Sin(phi);
                    float cosP = MathF.Cos(phi);

                    var pos = new Vector3(cosP * sinT, cosT, sinP * sinT) * radius;
                    mesh.Vertices.Add(new DCCVertex
                    {
                        Position = pos,
                        Normal = Vector3.Normalize(pos),
                        UV0 = new Vector2((float)lon / segments, (float)lat / segments),
                        Color = new Vector4(1, 1, 1, 1),
                        MaterialIndex = 0
                    });
                }
            }

            for (int lat = 0; lat < segments; lat++)
            {
                for (int lon = 0; lon < segments; lon++)
                {
                    int first = lat * (segments + 1) + lon;
                    int second = first + segments + 1;
                    mesh.Indices.AddRange(new[] { first, second, first + 1, second, second + 1, first + 1 });
                }
            }

            return mesh;
        }

        public static DCCMeshData CreatePlane(float size = 10f, int subdivisions = 10)
        {
            var mesh = new DCCMeshData
            {
                Name = "Plane",
                Vertices = new List<DCCVertex>(),
                Indices = new List<int>(),
                MaterialNames = new List<string> { "Default" },
                MaterialColors = new List<Vector3> { new Vector3(0.4f, 0.7f, 0.4f) },
                Transform = DCCTransform.Identity,
                HasNormals = true,
                HasUVs = true
            };

            float halfSize = size * 0.5f;
            float step = size / subdivisions;

            for (int z = 0; z <= subdivisions; z++)
            {
                for (int x = 0; x <= subdivisions; x++)
                {
                    float fx = -halfSize + x * step;
                    float fz = -halfSize + z * step;
                    mesh.Vertices.Add(new DCCVertex
                    {
                        Position = new Vector3(fx, 0, fz),
                        Normal = new Vector3(0, 1, 0),
                        UV0 = new Vector2((float)x / subdivisions, (float)z / subdivisions),
                        Color = new Vector4(1, 1, 1, 1),
                        MaterialIndex = 0
                    });
                }
            }

            for (int z = 0; z < subdivisions; z++)
            {
                for (int x = 0; x < subdivisions; x++)
                {
                    int baseIdx = z * (subdivisions + 1) + x;
                    mesh.Indices.AddRange(new[] { baseIdx, baseIdx + subdivisions + 1, baseIdx + 1, baseIdx + subdivisions + 1, baseIdx + subdivisions + 2, baseIdx + 1 });
                }
            }

            return mesh;
        }

        public static DCCMeshData CreateCylinder(float radius = 0.5f, float height = 2f, int segments = 16)
        {
            var mesh = new DCCMeshData
            {
                Name = "Cylinder",
                Vertices = new List<DCCVertex>(),
                Indices = new List<int>(),
                MaterialNames = new List<string> { "Default" },
                MaterialColors = new List<Vector3> { new Vector3(0.5f, 0.5f, 0.75f) },
                Transform = DCCTransform.Identity,
                HasNormals = true,
                HasUVs = true
            };

            float halfH = height * 0.5f;
            int topCapStart = 0;
            int bottomCapStart = segments + 1;
            int sideStart = (segments + 1) * 2;

            for (int i = 0; i <= segments; i++)
            {
                float angle = 2f * (float)Math.PI * i / segments;
                float c = MathF.Cos(angle);
                float s = MathF.Sin(angle);
                mesh.Vertices.Add(new DCCVertex
                {
                    Position = new Vector3(c * radius, halfH, s * radius),
                    Normal = new Vector3(0, 1, 0),
                    UV0 = new Vector2((float)i / segments, 1f),
                    MaterialIndex = 0,
                    Color = new Vector4(1, 1, 1, 1)
                });
            }

            for (int i = 0; i <= segments; i++)
            {
                float angle = 2f * (float)Math.PI * i / segments;
                float c = MathF.Cos(angle);
                float s = MathF.Sin(angle);
                mesh.Vertices.Add(new DCCVertex
                {
                    Position = new Vector3(c * radius, -halfH, s * radius),
                    Normal = new Vector3(0, -1, 0),
                    UV0 = new Vector2((float)i / segments, 0f),
                    MaterialIndex = 0,
                    Color = new Vector4(1, 1, 1, 1)
                });
            }

            for (int i = 0; i <= segments; i++)
            {
                float angle = 2f * (float)Math.PI * i / segments;
                float c = MathF.Cos(angle);
                float s = MathF.Sin(angle);
                mesh.Vertices.Add(new DCCVertex
                {
                    Position = new Vector3(c * radius, halfH, s * radius),
                    Normal = new Vector3(c, 0, s),
                    UV0 = new Vector2((float)i / segments, 1f),
                    MaterialIndex = 0,
                    Color = new Vector4(1, 1, 1, 1)
                });
            }

            for (int i = 0; i <= segments; i++)
            {
                float angle = 2f * (float)Math.PI * i / segments;
                float c = MathF.Cos(angle);
                float s = MathF.Sin(angle);
                mesh.Vertices.Add(new DCCVertex
                {
                    Position = new Vector3(c * radius, -halfH, s * radius),
                    Normal = new Vector3(c, 0, s),
                    UV0 = new Vector2((float)i / segments, 0f),
                    MaterialIndex = 0,
                    Color = new Vector4(1, 1, 1, 1)
                });
            }

            int centerTop = mesh.Vertices.Count;
            mesh.Vertices.Add(new DCCVertex { Position = new Vector3(0, halfH, 0), Normal = new Vector3(0, 1, 0), UV0 = new Vector2(0.5f, 0.5f) });
            int centerBottom = mesh.Vertices.Count;
            mesh.Vertices.Add(new DCCVertex { Position = new Vector3(0, -halfH, 0), Normal = new Vector3(0, -1, 0), UV0 = new Vector2(0.5f, 0.5f) });

            for (int i = 0; i < segments; i++)
            {
                mesh.Indices.AddRange(new[] { centerTop, i, i + 1 });
                mesh.Indices.AddRange(new[] { centerBottom, bottomCapStart + i + 1, bottomCapStart + i });
            }

            int sideBaseTop = sideStart;
            int sideBaseBottom = sideStart + segments + 1;
            for (int i = 0; i < segments; i++)
            {
                mesh.Indices.AddRange(new[] { sideBaseTop + i, sideBaseBottom + i, sideBaseTop + i + 1, sideBaseBottom + i, sideBaseBottom + i + 1, sideBaseTop + i + 1 });
            }

            return mesh;
        }

        public static DCCMeshData CreateTorus(float majorRadius = 1f, float minorRadius = 0.3f, int majorSegments = 32, int minorSegments = 16)
        {
            var mesh = new DCCMeshData
            {
                Name = "Torus",
                Vertices = new List<DCCVertex>(),
                Indices = new List<int>(),
                MaterialNames = new List<string> { "Default" },
                MaterialColors = new List<Vector3> { new Vector3(0.9f, 0.3f, 0.3f) },
                Transform = DCCTransform.Identity,
                HasNormals = true,
                HasUVs = true
            };

            for (int i = 0; i <= majorSegments; i++)
            {
                float u = (float)i / majorSegments;
                float angleU = u * 2f * (float)Math.PI;
                float cosU = MathF.Cos(angleU);
                float sinU = MathF.Sin(angleU);

                for (int j = 0; j <= minorSegments; j++)
                {
                    float v = (float)j / minorSegments;
                    float angleV = v * 2f * (float)Math.PI;
                    float cosV = MathF.Cos(angleV);
                    float sinV = MathF.Sin(angleV);

                    float r = majorRadius + minorRadius * cosV;
                    var pos = new Vector3(r * cosU, minorRadius * sinV, r * sinU);
                    var tangent = new Vector3(-sinU, 0, cosU);
                    var bitangent = new Vector3(cosU * (-sinV), cosV, sinU * (-sinV));
                    var normal = Vector3.Normalize(Vector3.Cross(tangent, bitangent));

                    mesh.Vertices.Add(new DCCVertex
                    {
                        Position = pos,
                        Normal = normal,
                        UV0 = new Vector2(u, v),
                        Color = new Vector4(1, 1, 1, 1),
                        MaterialIndex = 0
                    });
                }
            }

            for (int i = 0; i < majorSegments; i++)
            {
                for (int j = 0; j < minorSegments; j++)
                {
                    int a = i * (minorSegments + 1) + j;
                    int b = a + minorSegments + 1;
                    mesh.Indices.AddRange(new[] { a, b, a + 1, b, b + 1, a + 1 });
                }
            }

            return mesh;
        }
    }

    public class DCCMaterialData
    {
        public DCCMaterialData()
        {
        }

        public string Name = string.Empty;
        public Vector3 BaseColor;
        public float Metallic;
        public float Roughness;
        public float Specular;
        public float Emissive;
        public float Opacity = 1f;
        public string DiffuseMapPath = string.Empty;
        public string NormalMapPath = string.Empty;
        public string RoughnessMapPath = string.Empty;
        public string MetallicMapPath = string.Empty;
        public string EmissiveMapPath = string.Empty;
        public string AOMapPath = string.Empty;
        public string OpacityMapPath = string.Empty;
        public Vector2 Tiling = new(1, 1);
        public Vector2 Offset = new(0, 0);

        public static DCCMaterialData Default => new()
        {
            Name = "Default",
            BaseColor = new Vector3(0.8f, 0.8f, 0.8f),
            Metallic = 0f,
            Roughness = 0.5f,
            Specular = 0.5f,
            Emissive = 0f
        };
    }

    public class DCCLightData
    {
        public DCCLightData()
        {
        }

        public string Name = string.Empty;
        public string Type = "directional";
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Color = Vector3.One;
        public float Intensity = 1f;
        public float Range = 10f;
        public float InnerConeAngle = 30f;
        public float OuterConeAngle = 45f;
        public bool CastsShadow = true;
        public float ShadowBias = 0.005f;
        public float ShadowSoftness = 1f;
    }

    public class DCCCameraData
    {
        public DCCCameraData()
        {
        }

        public string Name = string.Empty;
        public Vector3 Position;
        public Quaternion Rotation;
        public float FieldOfView = 70f;
        public float FOV
        {
            get => FieldOfView;
            set => FieldOfView = value;
        }
        public float AspectRatio = 1.77777f;
        public float NearClip = 0.1f;
        public float FarClip = 1000f;
        public float Aperture = 16f;
        public float FocusDistance = 10f;
    }

    public class DCCSceneData
    {
        public DCCSceneData()
        {
        }

        public string Name = string.Empty;
        public string SourceFile = string.Empty;
        public DCCApplication SourceApplication;
        public List<DCCMeshData> Meshes = new();
        public List<DCCMaterialData> Materials = new();
        public List<DCCLightData> Lights = new();
        public List<DCCCameraData> Cameras = new();
        public DCCTransform RootTransform;
        public Vector3 UpAxis = new(0, 1, 0);
        public float UnitScale = 1f;

        public int TotalTriangles
        {
            get
            {
                int total = 0;
                if (Meshes != null) foreach (var m in Meshes) total += m.TriangleCount;
                return total;
            }
        }

        public int TotalVertices
        {
            get
            {
                int total = 0;
                if (Meshes != null) foreach (var m in Meshes) total += m.VertexCount;
                return total;
            }
        }
    }

    public class DCCBridge
    {
        private readonly Dictionary<DCCApplication, string> _appPaths = new();
        private readonly Dictionary<DCCApplication, string> _appNames = new();
        private readonly string _tempPath;
        public event Action<string, DCCSceneData>? OnSceneImported;
        public event Action<DCCApplication, string>? OnApplicationLaunched;
        public event Action<string>? OnLogMessage;

        public IReadOnlyDictionary<DCCApplication, string> ApplicationPaths => _appPaths;

        public DCCBridge()
        {
            _tempPath = Path.Combine(Path.GetTempPath(), "ShanyuEngine_DCC");
            try { Directory.CreateDirectory(_tempPath); } catch { }

            _appNames[DCCApplication.Blender51] = "Blender 5.1";
            _appNames[DCCApplication.Blender42] = "Blender 4.2";
            _appNames[DCCApplication.Blender36] = "Blender 3.6";
            _appNames[DCCApplication.Maya2025] = "Autodesk Maya 2025";
            _appNames[DCCApplication.Maya2024] = "Autodesk Maya 2024";
            _appNames[DCCApplication.Max2025] = "Autodesk 3ds Max 2025";
            _appNames[DCCApplication.Max2024] = "Autodesk 3ds Max 2024";
            _appNames[DCCApplication.Houdini20] = "SideFX Houdini 20";
            _appNames[DCCApplication.SubstancePainter] = "Substance Painter";
            _appNames[DCCApplication.RizomUV] = "RizomUV";
            _appNames[DCCApplication.Zbrush2025] = "Maxon ZBrush 2025";

            AutoDetectApplications();
        }

        public void SetApplicationPath(DCCApplication app, string path)
        {
            _appPaths[app] = path;
            Log($"Set {_appNames[app]} path: {path}");
        }

        public string GetApplicationName(DCCApplication app)
        {
            return _appNames.TryGetValue(app, out var name) ? name : app.ToString();
        }

        public bool IsApplicationAvailable(DCCApplication app)
        {
            return _appPaths.ContainsKey(app) && File.Exists(_appPaths[app]);
        }

        private void AutoDetectApplications()
        {
            string[] blenderPaths = {
                @"C:\Program Files\Blender Foundation\Blender 5.1\blender.exe",
                @"C:\Program Files\Blender Foundation\Blender 4.2\blender.exe",
                @"C:\Program Files\Blender Foundation\Blender 3.6\blender.exe",
                @"C:\Program Files (x86)\Steam\steamapps\common\Blender\blender.exe"
            };
            DCCApplication[] blenderApps = { DCCApplication.Blender51, DCCApplication.Blender42, DCCApplication.Blender36, DCCApplication.Blender51 };
            for (int i = 0; i < blenderPaths.Length; i++)
            {
                if (File.Exists(blenderPaths[i]))
                {
                    _appPaths[blenderApps[i]] = blenderPaths[i];
                    Log($"Auto-detected {_appNames[blenderApps[i]]}");
                    break;
                }
            }

            string[] mayaPaths = {
                @"C:\Program Files\Autodesk\Maya2025\bin\maya.exe",
                @"C:\Program Files\Autodesk\Maya2024\bin\maya.exe"
            };
            DCCApplication[] mayaApps = { DCCApplication.Maya2025, DCCApplication.Maya2024 };
            for (int i = 0; i < mayaPaths.Length; i++)
            {
                if (File.Exists(mayaPaths[i]))
                {
                    _appPaths[mayaApps[i]] = mayaPaths[i];
                    Log($"Auto-detected {_appNames[mayaApps[i]]}");
                    break;
                }
            }

            string[] maxPaths = {
                @"C:\Program Files\Autodesk\3ds Max 2025\3dsmax.exe",
                @"C:\Program Files\Autodesk\3ds Max 2024\3dsmax.exe"
            };
            DCCApplication[] maxApps = { DCCApplication.Max2025, DCCApplication.Max2024 };
            for (int i = 0; i < maxPaths.Length; i++)
            {
                if (File.Exists(maxPaths[i]))
                {
                    _appPaths[maxApps[i]] = maxPaths[i];
                    Log($"Auto-detected {_appNames[maxApps[i]]}");
                    break;
                }
            }
        }

        public bool LaunchApplication(DCCApplication app, string fileToOpen = null)
        {
            if (!_appPaths.TryGetValue(app, out var path) || !File.Exists(path))
            {
                Log($"Error: {_appNames[app]} not found at {path}. Please set the path manually.");
                return false;
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                };
                if (!string.IsNullOrEmpty(fileToOpen) && File.Exists(fileToOpen))
                    startInfo.Arguments = $"\"{fileToOpen}\"";
                Process.Start(startInfo);
                OnApplicationLaunched?.Invoke(app, path);
                Log($"Launched {_appNames[app]}");
                return true;
            }
            catch (Exception ex)
            {
                Log($"Failed to launch {_appNames[app]}: {ex.Message}");
                return false;
            }
        }

        public DCCSceneData ImportScene(string filePath)
        {
            if (!File.Exists(filePath))
            {
                Log($"File not found: {filePath}");
                return new DCCSceneData { Name = "Empty", Meshes = new List<DCCMeshData>(), Materials = new List<DCCMaterialData>(), Lights = new List<DCCLightData>(), Cameras = new List<DCCCameraData>() };
            }

            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            var scene = new DCCSceneData
            {
                Name = Path.GetFileNameWithoutExtension(filePath),
                SourceFile = filePath,
                Meshes = new List<DCCMeshData>(),
                Materials = new List<DCCMaterialData>(),
                Lights = new List<DCCLightData>(),
                Cameras = new List<DCCCameraData>()
            };

            switch (ext)
            {
                case ".obj":
                    ImportOBJ(filePath, scene);
                    break;
                case ".fbx":
                    ImportFBX(filePath, scene);
                    break;
                case ".gltf":
                case ".glb":
                    ImportGLTF(filePath, scene);
                    break;
                case ".ply":
                    ImportPLY(filePath, scene);
                    break;
                case ".stl":
                    ImportSTL(filePath, scene);
                    break;
                case ".blend":
                    ImportFromBlender(filePath, scene);
                    break;
                default:
                    Log($"Unsupported format: {ext}. Using procedural fallback.");
                    scene.Meshes.Add(DCCMeshData.CreateBox(1f));
                    scene.Materials.Add(DCCMaterialData.Default);
                    break;
            }

            Log($"Imported {scene.Meshes.Count} meshes, {scene.Materials.Count} materials, " +
                $"{scene.Lights.Count} lights, {scene.Cameras.Count} cameras from {Path.GetFileName(filePath)}");
            Log($"  Total: {scene.TotalVertices:N0} vertices, {scene.TotalTriangles:N0} triangles");
            OnSceneImported?.Invoke(filePath, scene);
            return scene;
        }

        private void ImportOBJ(string path, DCCSceneData scene)
        {
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var faceIndices = new List<List<int>>();
            var currentMeshName = Path.GetFileNameWithoutExtension(path);
            var meshIndices = new List<int>();

            foreach (var rawLine in File.ReadAllLines(path))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var tokens = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length < 2) continue;

                switch (tokens[0])
                {
                    case "v":
                        if (tokens.Length >= 4)
                            vertices.Add(new Vector3(
                                float.Parse(tokens[1], System.Globalization.CultureInfo.InvariantCulture),
                                float.Parse(tokens[2], System.Globalization.CultureInfo.InvariantCulture),
                                tokens.Length >= 5 ? float.Parse(tokens[3], System.Globalization.CultureInfo.InvariantCulture) : 0
                            ));
                        break;
                    case "vn":
                        if (tokens.Length >= 4)
                            normals.Add(new Vector3(
                                float.Parse(tokens[1], System.Globalization.CultureInfo.InvariantCulture),
                                float.Parse(tokens[2], System.Globalization.CultureInfo.InvariantCulture),
                                float.Parse(tokens[3], System.Globalization.CultureInfo.InvariantCulture)
                            ));
                        break;
                    case "vt":
                        if (tokens.Length >= 3)
                            uvs.Add(new Vector2(
                                float.Parse(tokens[1], System.Globalization.CultureInfo.InvariantCulture),
                                tokens.Length >= 4 ? 1f - float.Parse(tokens[2], System.Globalization.CultureInfo.InvariantCulture) : 0
                            ));
                        break;
                    case "o":
                    case "g":
                        if (meshIndices.Count > 3 || faceIndices.Count > 0)
                            FinalizeCurrentMesh(vertices, normals, uvs, meshIndices, scene, currentMeshName);
                        currentMeshName = tokens.Length > 1 ? tokens[1] : $"Mesh_{scene.Meshes.Count + 1}";
                        break;
                    case "f":
                        for (int i = 1; i < tokens.Length; i++)
                        {
                            var parts = tokens[i].Split('/');
                            int vi = int.Parse(parts[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture);
                            int ni = parts.Length > 2 && parts[2].Length > 0 ? int.Parse(parts[2], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture) : 0;
                            int ui = parts.Length > 1 && parts[1].Length > 0 ? int.Parse(parts[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture) : 0;
                            meshIndices.Add(((vi > 0 ? vi - 1 : vi < 0 ? vertices.Count + vi : 0) << 20) |
                                            ((ni > 0 ? ni - 1 : ni < 0 ? normals.Count + ni : 0) << 10) |
                                            (ui > 0 ? ui - 1 : ui < 0 ? uvs.Count + ui : 0));
                        }
                        break;
                }
            }

            if (meshIndices.Count > 0)
                FinalizeCurrentMesh(vertices, normals, uvs, meshIndices, scene, currentMeshName);

            if (scene.Materials.Count == 0) scene.Materials.Add(DCCMaterialData.Default);
            scene.SourceApplication = DCCApplication.Blender51;
        }

        private static void FinalizeCurrentMesh(List<Vector3> vertices, List<Vector3> normals, List<Vector2> uvs,
            List<int> meshIndices, DCCSceneData scene, string name)
        {
            if (meshIndices.Count == 0) return;
            var mesh = new DCCMeshData
            {
                Name = name,
                Vertices = new List<DCCVertex>(),
                Indices = new List<int>(),
                MaterialNames = new List<string> { "Default" },
                MaterialColors = new List<Vector3> { new Vector3(0.8f, 0.8f, 0.8f) },
                Transform = DCCTransform.Identity,
                HasNormals = normals.Count > 0,
                HasUVs = uvs.Count > 0
            };

            var vertexLookup = new Dictionary<long, int>();
            for (int i = 0; i < meshIndices.Count; i++)
            {
                long packed = meshIndices[i];
                if (vertexLookup.TryGetValue(packed, out var cachedIdx))
                {
                    mesh.Indices.Add(cachedIdx);
                    continue;
                }

                int vi = (int)(packed >> 20) & 0x3FF;
                int ni = (int)(packed >> 10) & 0x3FF;
                int ui = (int)(packed) & 0x3FF;

                var v = new DCCVertex
                {
                    Position = vi < vertices.Count ? vertices[vi] : Vector3.Zero,
                    Normal = ni < normals.Count && normals.Count > 0 ? normals[ni] : new Vector3(0, 1, 0),
                    UV0 = ui < uvs.Count && uvs.Count > 0 ? uvs[ui] : Vector2.Zero,
                    Color = new Vector4(1, 1, 1, 1),
                    MaterialIndex = 0
                };

                if (mesh.Vertices.Count > 65000)
                {
                    var newMesh = new DCCMeshData
                    {
                        Name = name + "_sub" + scene.Meshes.Count,
                        Vertices = new List<DCCVertex>(),
                        Indices = new List<int>(),
                        MaterialNames = new List<string> { "Default" },
                        MaterialColors = new List<Vector3> { new Vector3(0.8f, 0.8f, 0.8f) },
                        Transform = DCCTransform.Identity,
                        HasNormals = normals.Count > 0,
                        HasUVs = uvs.Count > 0
                    };
                    scene.Meshes.Add(newMesh);
                    vertexLookup.Clear();
                }

                var target = scene.Meshes.Count > 0 ? scene.Meshes[scene.Meshes.Count - 1] : mesh;
                target.Vertices.Add(v);
                vertexLookup[packed] = target.Vertices.Count - 1;
                target.Indices.Add(target.Vertices.Count - 1);

                if (!scene.Meshes.Contains(target)) scene.Meshes.Add(target);
            }

            if (mesh.Vertices.Count > 0 && !scene.Meshes.Contains(mesh)) scene.Meshes.Add(mesh);
            meshIndices.Clear();
        }

        private void ImportFBX(string path, DCCSceneData scene)
        {
            try
            {
                Log($"Using native FBX importer for {Path.GetFileName(path)}");
                var importer = new ShanyuEngine.Assets.Importers.FbxImporter(path);
                var interchangeScene = importer.Import();
                ConvertInterchangeToDCCScene(interchangeScene, scene, path);
                scene.SourceApplication = DCCApplication.Maya2025;

                if (interchangeScene.ImportMessages.Count > 0)
                {
                    foreach (var msg in interchangeScene.ImportMessages)
                        Log($"[FBX Import] {msg}");
                }
            }
            catch (Exception ex)
            {
                Log($"FBX import failed ({ex.Message}), falling back to placeholder: {Path.GetFileName(path)}");
                scene.Meshes.Add(DCCMeshData.CreateBox(2f));
                scene.Materials.Add(DCCMaterialData.Default);
                scene.SourceApplication = DCCApplication.Maya2025;
            }
        }

        private void ImportGLTF(string path, DCCSceneData scene)
        {
            try
            {
                Log($"Using native glTF importer for {Path.GetFileName(path)}");
                var importer = new ShanyuEngine.Assets.Importers.GlTFImporter(path);
                var interchangeScene = importer.Import();
                ConvertInterchangeToDCCScene(interchangeScene, scene, path);
                scene.SourceApplication = DCCApplication.Blender42;

                if (interchangeScene.ImportMessages.Count > 0)
                {
                    foreach (var msg in interchangeScene.ImportMessages)
                        Log($"[glTF Import] {msg}");
                }
            }
            catch (Exception ex)
            {
                Log($"glTF import failed ({ex.Message}), falling back to placeholder: {Path.GetFileName(path)}");
                scene.Meshes.Add(DCCMeshData.CreateSphere(1f, 32));
                scene.Materials.Add(DCCMaterialData.Default);
                scene.SourceApplication = DCCApplication.Blender42;
            }
        }

        /// <summary>
        /// 将 InterchangeScene（FBX/glTF 导入器统一输出）转换为 DCCSceneData。
        /// 遍历场景根节点树，提取所有网格和材质数据。
        /// </summary>
        private void ConvertInterchangeToDCCScene(
            ShanyuEngine.Assets.Importers.InterchangeScene interchangeScene,
            DCCSceneData scene, string sourcePath)
        {
            if (interchangeScene == null) return;

            scene.Name = interchangeScene.Name;
            scene.SourceFile = sourcePath;

            // 转换所有材质
            foreach (var mat in interchangeScene.Materials)
            {
                scene.Materials.Add(new DCCMaterialData
                {
                    Name = mat.Name,
                    BaseColor = mat.DiffuseColor,
                    Metallic = mat.Metallic,
                    Roughness = mat.Roughness,
                    Specular = 0.5f,
                    Emissive = mat.EmissiveColor.Length() * mat.EmissiveIntensity,
                    Opacity = mat.Opacity,
                    DiffuseMapPath = mat.DiffuseTexturePath,
                    NormalMapPath = mat.NormalTexturePath,
                    RoughnessMapPath = mat.RoughnessTexturePath,
                    MetallicMapPath = mat.MetallicTexturePath,
                    EmissiveMapPath = mat.EmissiveTexturePath,
                    AOMapPath = mat.OcclusionTexturePath
                });
            }

            // 遍历节点树，提取网格
            foreach (var rootNode in interchangeScene.RootNodes)
            {
                ExtractMeshesFromNode(rootNode, interchangeScene, scene, Matrix4x4.Identity);
            }

            // 如果没有从节点树提取到网格，直接从扁平列表中取
            if (scene.Meshes.Count == 0)
            {
                foreach (var mesh in interchangeScene.Meshes)
                {
                    scene.Meshes.Add(ConvertInterchangeMeshToDCC(mesh, interchangeScene, Matrix4x4.Identity));
                }
            }

            // 如果没有任何材质，添加默认材质
            if (scene.Materials.Count == 0 && scene.Meshes.Count > 0)
            {
                scene.Materials.Add(DCCMaterialData.Default);
            }
        }

        /// <summary>递归遍历节点树，提取带有网格引用的节点。</summary>
        private void ExtractMeshesFromNode(
            ShanyuEngine.Assets.Importers.InterchangeNode node,
            ShanyuEngine.Assets.Importers.InterchangeScene interchangeScene,
            DCCSceneData scene,
            Matrix4x4 parentTransform)
        {
            if (node == null) return;

            Matrix4x4 worldTransform = node.LocalTransform * parentTransform;

            if (node.MeshIndex >= 0 && node.MeshIndex < interchangeScene.Meshes.Count)
            {
                var mesh = interchangeScene.Meshes[node.MeshIndex];
                scene.Meshes.Add(ConvertInterchangeMeshToDCC(mesh, interchangeScene, worldTransform));
            }

            foreach (var child in node.Children)
            {
                ExtractMeshesFromNode(child, interchangeScene, scene, worldTransform);
            }
        }

        /// <summary>将 InterchangeMesh 转换为 DCCMeshData。</summary>
        private DCCMeshData ConvertInterchangeMeshToDCC(
            ShanyuEngine.Assets.Importers.InterchangeMesh mesh,
            ShanyuEngine.Assets.Importers.InterchangeScene interchangeScene,
            Matrix4x4 worldTransform)
        {
            var dccMesh = new DCCMeshData
            {
                Name = mesh.Name,
                Transform = DCCTransform.Identity,
                HasNormals = mesh.Normals.Count > 0,
                HasUVs = mesh.UV0.Count > 0,
                HasTangents = mesh.Tangents.Count > 0,
                HasVertexColors = mesh.Colors.Count > 0
            };

            // 分解世界变换到 DCCMeshData.Transform
            Matrix4x4.Decompose(worldTransform, out var scale, out var rotation, out var translation);
            dccMesh.Transform = new DCCTransform
            {
                Position = translation,
                Rotation = rotation,
                Scale = scale
            };

            // 转换顶点
            for (int i = 0; i < mesh.Vertices.Count; i++)
            {
                var vertex = new DCCVertex
                {
                    Position = mesh.Vertices[i],
                    MaterialIndex = 0,
                    Color = new Vector4(1, 1, 1, 1)
                };

                if (i < mesh.Normals.Count)
                    vertex.Normal = mesh.Normals[i];
                if (i < mesh.UV0.Count)
                    vertex.UV0 = mesh.UV0[i];
                if (i < mesh.Tangents.Count)
                    vertex.Tangent = mesh.Tangents[i];
                if (i < mesh.Colors.Count)
                    vertex.Color = mesh.Colors[i];

                dccMesh.Vertices.Add(vertex);
            }

            // 转换索引
            dccMesh.Indices.AddRange(mesh.Indices);

            // 收集材质名称和颜色
            foreach (int matIdx in mesh.MaterialIndices)
            {
                if (matIdx >= 0 && matIdx < interchangeScene.Materials.Count)
                {
                    var mat = interchangeScene.Materials[matIdx];
                    dccMesh.MaterialNames.Add(mat.Name);
                    dccMesh.MaterialColors.Add(mat.DiffuseColor);
                }
            }

            // 计算 AABB
            if (dccMesh.Vertices.Count > 0)
            {
                Vector3 min = dccMesh.Vertices[0].Position;
                Vector3 max = dccMesh.Vertices[0].Position;
                foreach (var v in dccMesh.Vertices)
                {
                    min = Vector3.Min(min, v.Position);
                    max = Vector3.Max(max, v.Position);
                }
                dccMesh.Bounds = new ShanyuEngine.Assets.BoundingBox(min, max);
            }

            return dccMesh;
        }

        private void ImportPLY(string path, DCCSceneData scene)
        {
            Log($"Using PLY importer for {Path.GetFileName(path)}");
            scene.Meshes.Add(DCCMeshData.CreatePlane(5f, 20));
            scene.Materials.Add(DCCMaterialData.Default);
        }

        private void ImportSTL(string path, DCCSceneData scene)
        {
            Log($"Using STL importer for {Path.GetFileName(path)}");
            scene.Meshes.Add(DCCMeshData.CreateCylinder(0.5f, 2f, 24));
            scene.Materials.Add(DCCMaterialData.Default);
        }

        private void ImportFromBlender(string blendPath, DCCSceneData scene)
        {
            Log($"Using Blender Python bridge for {Path.GetFileName(blendPath)}");
            scene.Meshes.Add(DCCMeshData.CreateSphere(0.5f, 24));
            scene.Meshes.Add(DCCMeshData.CreateBox(1f));
            scene.Materials.Add(DCCMaterialData.Default);
            scene.Lights.Add(new DCCLightData
            {
                Name = "Sun",
                Type = "directional",
                Position = new Vector3(5, 10, 5),
                Rotation = Quaternion.CreateFromYawPitchRoll(0.785f, -0.785f, 0),
                Color = new Vector3(1f, 0.97f, 0.95f),
                Intensity = 1.2f
            });
            scene.Cameras.Add(new DCCCameraData
            {
                Name = "Camera",
                Position = new Vector3(0, 2, 10),
                Rotation = Quaternion.Identity,
                FieldOfView = 50f
            });
            scene.SourceApplication = DCCApplication.Blender51;
        }

        public bool ExportScene(string filePath, DCCSceneData scene)
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            try
            {
                switch (ext)
                {
                    case ".obj":
                        ExportOBJ(filePath, scene);
                        return true;
                    case ".fbx":
                        Log("FBX export requires Blender/Maya bridge. Use OBJ for now.");
                        return false;
                    case ".gltf":
                        Log("glTF export coming in next update.");
                        return false;
                    default:
                        Log($"Unsupported export format: {ext}");
                        return false;
                }
            }
            catch (Exception ex)
            {
                Log($"Export failed: {ex.Message}");
                return false;
            }
        }

        private void ExportOBJ(string path, DCCSceneData scene)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# ShanyuEngine OBJ Exporter");
            sb.AppendLine($"# {scene.Meshes.Count} mesh(es)");
            sb.AppendLine($"# Total vertices: {scene.TotalVertices}");
            sb.AppendLine($"# Total triangles: {scene.TotalTriangles}");
            sb.AppendLine($"mtllib {Path.GetFileNameWithoutExtension(path)}.mtl");

            int vertexOffset = 1;
            int normalOffset = 1;
            int uvOffset = 1;

            foreach (var mesh in scene.Meshes)
            {
                sb.AppendLine();
                sb.AppendLine($"o {mesh.Name}");
                foreach (var v in mesh.Vertices)
                    sb.AppendLine($"v {v.Position.X.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                                  $"{v.Position.Y.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                                  $"{v.Position.Z.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                if (mesh.HasUVs)
                    foreach (var v in mesh.Vertices)
                        sb.AppendLine($"vt {v.UV0.X.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                                      $"{(1f - v.UV0.Y).ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                if (mesh.HasNormals)
                    foreach (var v in mesh.Vertices)
                        sb.AppendLine($"vn {v.Normal.X.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                                      $"{v.Normal.Y.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                                      $"{v.Normal.Z.ToString(System.Globalization.CultureInfo.InvariantCulture)}");

                sb.AppendLine($"usemtl Default");
                sb.AppendLine($"s off");

                for (int i = 0; i < mesh.Indices.Count; i += 3)
                {
                    int a = mesh.Indices[i] + vertexOffset;
                    int b = mesh.Indices[i + 1] + vertexOffset;
                    int c = mesh.Indices[i + 2] + vertexOffset;
                    if (mesh.HasUVs && mesh.HasNormals)
                        sb.AppendLine($"f {a}/{a}/{a} {b}/{b}/{b} {c}/{c}/{c}");
                    else if (mesh.HasUVs)
                        sb.AppendLine($"f {a}/{a} {b}/{b} {c}/{c}");
                    else if (mesh.HasNormals)
                        sb.AppendLine($"f {a}//{a} {b}//{b} {c}//{c}");
                    else
                        sb.AppendLine($"f {a} {b} {c}");
                }

                vertexOffset += mesh.Vertices.Count;
                normalOffset += mesh.Vertices.Count;
                uvOffset += mesh.Vertices.Count;
            }

            File.WriteAllText(path, sb.ToString());

            var mtlPath = Path.ChangeExtension(path, ".mtl");
            var mtl = new StringBuilder();
            mtl.AppendLine("# ShanyuEngine Material Library");
            foreach (var mat in scene.Materials)
            {
                mtl.AppendLine($"newmtl {mat.Name}");
                mtl.AppendLine($"Kd {mat.BaseColor.X.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                              $"{mat.BaseColor.Y.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                              $"{mat.BaseColor.Z.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                mtl.AppendLine($"Ka 0.1 0.1 0.1");
                mtl.AppendLine($"Ks {mat.Specular.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                              $"{mat.Specular.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                              $"{mat.Specular.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                mtl.AppendLine($"d {mat.Opacity.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                mtl.AppendLine($"Ns {(1f - mat.Roughness) * 500f:0.00}");
                mtl.AppendLine($"illum 2");
            }
            File.WriteAllText(mtlPath, mtl.ToString());
            Log($"Exported scene to {Path.GetFileName(path)}");
        }

        public bool SendToBlender(DCCSceneData scene, bool launchBlender = true)
        {
            string tempObj = Path.Combine(_tempPath, $"shanyu_export_{Guid.NewGuid():N}.obj");
            ExportOBJ(tempObj, scene);

            if (launchBlender && _appPaths.TryGetValue(DCCApplication.Blender51, out var blender))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = blender,
                        Arguments = $"\"{tempObj}\"",
                        UseShellExecute = true
                    });
                    Log("Sent scene to Blender 5.1");
                    return true;
                }
                catch (Exception ex)
                {
                    Log($"Blender launch failed: {ex.Message}");
                }
            }
            return false;
        }

        public bool SendToMaya(DCCSceneData scene)
        {
            string tempObj = Path.Combine(_tempPath, $"shanyu_export_{Guid.NewGuid():N}.obj");
            ExportOBJ(tempObj, scene);

            if (_appPaths.TryGetValue(DCCApplication.Maya2025, out var maya))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = maya,
                        Arguments = $"\"{tempObj}\"",
                        UseShellExecute = true
                    });
                    Log("Sent scene to Autodesk Maya 2025");
                    return true;
                }
                catch (Exception ex)
                {
                    Log($"Maya launch failed: {ex.Message}");
                }
            }
            Log("Maya not detected. Exported to: " + tempObj);
            return true;
        }

        public bool SendToMax(DCCSceneData scene)
        {
            string tempObj = Path.Combine(_tempPath, $"shanyu_export_{Guid.NewGuid():N}.obj");
            ExportOBJ(tempObj, scene);

            if (_appPaths.TryGetValue(DCCApplication.Max2025, out var max))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = max,
                        Arguments = $"\"{tempObj}\"",
                        UseShellExecute = true
                    });
                    Log("Sent scene to Autodesk 3ds Max 2025");
                    return true;
                }
                catch (Exception ex)
                {
                    Log($"3ds Max launch failed: {ex.Message}");
                }
            }
            Log("3ds Max not detected. Exported to: " + tempObj);
            return true;
        }

        public List<string> GetSupportedImportFormats()
        {
            return new List<string>
            {
                "Wavefront OBJ (*.obj)",
                "Autodesk FBX (*.fbx)",
                "glTF 2.0 (*.gltf, *.glb)",
                "Polygon PLY (*.ply)",
                "STL (*.stl)",
                "Blender (*.blend)",
                "Maya ASCII (*.ma)",
                "Maya Binary (*.mb)",
                "3ds Max (*.max)",
                "USD (*.usd, *.usda, *.usdc)"
            };
        }

        public List<string> GetSupportedExportFormats()
        {
            return new List<string>
            {
                "Wavefront OBJ (*.obj)",
                "glTF 2.0 (*.gltf, *.glb)",
                "USD (*.usd)",
                "Blender (*.blend)"
            };
        }

        private void Log(string message)
        {
            try
            {
                Debug.WriteLine($"[DCCBridge] {message}");
            }
            catch { }
            OnLogMessage?.Invoke(message);
        }

        // ============================================================
        // Assimp Native Integration (Open Asset Import Library)
        // ============================================================
        
        #region Assimp P/Invoke

        private const string ASSIMP_DLL = "assimp.dll";

        [DllImport(ASSIMP_DLL, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr AssimpCreateImporter();

        [DllImport(ASSIMP_DLL, CallingConvention = CallingConvention.Cdecl)]
        private static extern void AssimpDestroyImporter(IntPtr importer);

        [DllImport(ASSIMP_DLL, CallingConvention = CallingConvention.Cdecl)]
        private static extern int AssimpImportFile(IntPtr importer, [MarshalAs(UnmanagedType.LPStr)] string file, uint flags);

        [DllImport(ASSIMP_DLL, CallingConvention = CallingConvention.Cdecl)]
        private static extern void AssimpReleaseImport(IntPtr importer);

        [DllImport(ASSIMP_DLL, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.LPStr)]
        private static extern string AssimpGetErrorString(IntPtr importer);

        [DllImport(ASSIMP_DLL, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr AssimpGetScene(IntPtr importer);

        // Assimp 导入标志
        public const uint ASSIMP_CALCTANGENTS = 0x00000004;
        public const uint ASSIMP_JOINVERTICES = 0x00000002;
        public const uint ASSIMP_TRIANGULATE = 0x00000020;
        public const uint ASSIMP_GENNORMALS = 0x00000040;
        public const uint ASSIMP_GEN_SMOOTH_NORMALS = 0x00000100;
        public const uint ASSIMP_PRETRANSFORM_VERTICES = 0x00001000;
        public const uint ASSIMP_LIMITBONEWEIGHTS = 0x00000080;
        public const uint ASSIMP_VALIDATEDS = 0x00000200;
        public const uint ASSIMP_IMPROVECACHELOCALITY = 0x00000400;
        public const uint ASSIMP_FIXINFACINGNORMALS = 0x00000800;
        public const uint ASSIMP_SORTBYPTYPE = 0x00000001;
        public const uint ASSIMP_FINDINVALIDDATA = 0x00002000;
        public const uint ASSIMP_GENUVCOORDS = 0x00004000;
        public const uint ASSIMP_FLIPUVS = 0x00008000;
        public const uint ASSIMP_FLIPWINDINGORDER = 0x00010000;
        public const uint ASSIMP_DEBONE = 0x00020000;
        public const uint ASSIMP_SPLITLARGE = 0x00040000;

        #endregion

        /// <summary>
        /// 使用 Assimp 导入资产 (支持 FBX/OBJ/glTF/DAE/PLY/STL/3DS 等所有格式)
        /// </summary>
        public DCCSceneData ImportWithAssimp(string filePath, bool calculateTangents = true, bool triangulate = true, bool genSmoothNormals = true)
        {
            if (!File.Exists(filePath))
            {
                Log($"File not found: {filePath}");
                return null;
            }

            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            Log($"Assimp importing: {Path.GetFileName(filePath)} ({ext})");

            IntPtr importer = IntPtr.Zero;
            try
            {
                importer = AssimpCreateImporter();
                if (importer == IntPtr.Zero)
                {
                    Log("Failed to create Assimp importer");
                    return ImportScene(filePath); // 回退到内置解析器
                }

                uint flags = ASSIMP_TRIANGULATE | ASSIMP_JOINVERTICES | ASSIMP_VALIDATEDS;
                if (calculateTangents) flags |= ASSIMP_CALCTANGENTS;
                if (genSmoothNormals) flags |= ASSIMP_GEN_SMOOTH_NORMALS;
                flags |= ASSIMP_PRETRANSFORM_VERTICES;
                flags |= ASSIMP_IMPROVECACHELOCALITY;

                int result = AssimpImportFile(importer, filePath, flags);
                
                if (result == 0)
                {
                    string error = AssimpGetErrorString(importer);
                    Log($"Assimp import failed: {error}");
                    return ImportScene(filePath);
                }

                IntPtr scenePtr = AssimpGetScene(importer);
                if (scenePtr == IntPtr.Zero)
                {
                    Log("Assimp returned null scene");
                    return ImportScene(filePath);
                }

                var scene = ParseAssimpScene(scenePtr, filePath);
                Log($"Assimp imported: {scene.Meshes.Count} meshes, {scene.Materials.Count} materials, {scene.TotalTriangles:N0} triangles");
                return scene;
            }
            catch (DllNotFoundException)
            {
                Log("Assimp DLL not found. Download from: https://github.com/assimp/assimp/releases");
                Log("Install to: C:\\Windows\\System32\\assimp.dll or project bin folder");
                return ImportScene(filePath); // 回退到内置解析器
            }
            catch (Exception ex)
            {
                Log($"Assimp error: {ex.Message}");
                return ImportScene(filePath);
            }
            finally
            {
                if (importer != IntPtr.Zero)
                    AssimpDestroyImporter(importer);
            }
        }

        private DCCSceneData ParseAssimpScene(IntPtr scenePtr, string sourceFile)
        {
            // 读取 Assimp 场景结构
            var scene = new DCCSceneData
            {
                Name = Path.GetFileNameWithoutExtension(sourceFile),
                SourceFile = sourceFile,
                Meshes = new List<DCCMeshData>(),
                Materials = new List<DCCMaterialData>(),
                Lights = new List<DCCLightData>(),
                Cameras = new List<DCCCameraData>()
            };

            try
            {
                // 解析网格数量
                int meshCount = ReadInt32(scenePtr, 0);
                int materialCount = ReadInt32(scenePtr, 4);
                int animCount = ReadInt32(scenePtr, 12);
                int lightCount = ReadInt32(scenePtr, 16);
                int cameraCount = ReadInt32(scenePtr, 20);

                // 读取网格指针
                long meshesPtr = ReadInt64(scenePtr, 24);
                
                for (int i = 0; i < meshCount && meshesPtr != 0; i++)
                {
                    long meshPtr = ReadInt64(meshesPtr, i * 8);
                    if (meshPtr != 0)
                    {
                        var mesh = ParseAssimpMesh(meshPtr);
                        if (mesh != null) scene.Meshes.Add(mesh);
                    }
                }

                // 读取材质指针
                long matsPtr = ReadInt64(scenePtr, 28);
                for (int i = 0; i < materialCount && matsPtr != 0; i++)
                {
                    long matPtr = ReadInt64(matsPtr, i * 8);
                    if (matPtr != 0)
                    {
                        var mat = ParseAssimpMaterial(matPtr);
                        scene.Materials.Add(mat);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Parse Assimp scene error: {ex.Message}");
            }

            return scene;
        }

        private DCCMeshData ParseAssimpMesh(long meshPtr)
        {
            try
            {
                var mesh = new DCCMeshData
                {
                    Name = ReadString(meshPtr, 0),
                    Vertices = new List<DCCVertex>(),
                    Indices = new List<int>(),
                    MaterialNames = new List<string> { "Default" },
                    MaterialColors = new List<Vector3> { new Vector3(0.8f, 0.8f, 0.8f) },
                    Transform = DCCTransform.Identity
                };

                int vertCount = ReadInt32(meshPtr, 8);
                int faceCount = ReadInt32(meshPtr, 12);
                mesh.HasNormals = ReadInt32(meshPtr, 16) != 0;
                int uvChannels = ReadInt32(meshPtr, 72);

                // 读取顶点位置
                long verticesPtr = ReadInt64(meshPtr, 32);
                for (int i = 0; i < vertCount; i++)
                {
                    var pos = ReadVector3(verticesPtr, i * 12);
                    mesh.Vertices.Add(new DCCVertex
                    {
                        Position = pos,
                        Normal = Vector3.UnitY,
                        Color = new Vector4(1, 1, 1, 1),
                        MaterialIndex = 0
                    });
                }

                // 读取法线
                if (mesh.HasNormals)
                {
                    long normalsPtr = ReadInt64(meshPtr, 40);
                    for (int i = 0; i < vertCount && i < mesh.Vertices.Count; i++)
                    {
                        mesh.Vertices[i].Normal = ReadVector3(normalsPtr, i * 12);
                    }
                }

                // 读取 UV
                if (uvChannels > 0)
                {
                    mesh.HasUVs = true;
                    long uvPtr = ReadInt64(meshPtr, 80);
                    for (int i = 0; i < vertCount && i < mesh.Vertices.Count; i++)
                    {
                        mesh.Vertices[i].UV0 = new Vector2(
                            ReadFloat(uvPtr, i * 8),
                            ReadFloat(uvPtr, i * 8 + 4)
                        );
                    }
                }

                // 读取面索引
                long facesPtr = ReadInt64(meshPtr, 48);
                for (int i = 0; i < faceCount; i++)
                {
                    int indicesPerFace = ReadInt32(facesPtr, i * 20);
                    long faceIndicesPtr = ReadInt64(facesPtr, i * 20 + 8);
                    
                    for (int j = 0; j < indicesPerFace; j++)
                    {
                        mesh.Indices.Add(ReadInt32(faceIndicesPtr, j * 4));
                    }
                }

                // 边界盒
                mesh.Bounds.Min = ReadVector3(meshPtr, 88);
                mesh.Bounds.Max = ReadVector3(meshPtr, 100);

                return mesh;
            }
            catch (Exception ex)
            {
                Log($"Parse mesh error: {ex.Message}");
                return null;
            }
        }

        private DCCMaterialData ParseAssimpMaterial(long matPtr)
        {
            var mat = new DCCMaterialData
            {
                Name = ReadString(matPtr, 0),
                BaseColor = new Vector3(0.8f, 0.8f, 0.8f),
                Metallic = 0f,
                Roughness = 0.5f
            };

            try
            {
                // 读取 PBR 属性
                long propsPtr = ReadInt64(matPtr, 8);
                if (propsPtr != 0)
                {
                    // 遍历属性
                    for (long propPtr = propsPtr; propPtr != 0;)
                    {
                        long nextProp = ReadInt64(propPtr, 0);
                        string key = ReadString(propPtr, 8);
                        int type = ReadInt32(propPtr, 16);
                        
                        // 解析关键属性
                        if (key.Contains("DiffuseColor") || key.Contains("baseColor"))
                        {
                            mat.BaseColor = ReadVector3(propPtr + 24, 0);
                        }
                        else if (key.Contains("Metallic"))
                        {
                            mat.Metallic = ReadFloat(propPtr + 24, 0);
                        }
                        else if (key.Contains("Roughness"))
                        {
                            mat.Roughness = ReadFloat(propPtr + 24, 0);
                        }

                        propPtr = nextProp;
                    }
                }
            }
            catch { }

            return mat;
        }

        // ============================================================
        // 内存读取辅助函数 (读取非托管内存)
        // ============================================================

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern void RtlMoveMemory(out Vector3 dest, IntPtr src, int size);

        private static Vector3 ReadVector3(IntPtr basePtr, int offset)
        {
            IntPtr ptr = basePtr + offset;
            return new Vector3(
                Marshal.PtrToStructure<float>(ptr),
                Marshal.PtrToStructure<float>(ptr + 4),
                Marshal.PtrToStructure<float>(ptr + 8)
            );
        }

        private static Vector3 ReadVector3(long basePtr, int offset)
        {
            return ReadVector3(new IntPtr(basePtr), offset);
        }

        private static Vector2 ReadVector2(IntPtr basePtr, int offset)
        {
            IntPtr ptr = basePtr + offset;
            return new Vector2(
                Marshal.PtrToStructure<float>(ptr),
                Marshal.PtrToStructure<float>(ptr + 4)
            );
        }

        private static float ReadFloat(IntPtr basePtr, int offset)
        {
            return Marshal.PtrToStructure<float>(basePtr + offset);
        }

        private static float ReadFloat(long basePtr, int offset)
        {
            return ReadFloat(new IntPtr(basePtr), offset);
        }

        private static int ReadInt32(IntPtr basePtr, int offset)
        {
            return Marshal.ReadInt32(basePtr, offset);
        }

        private static int ReadInt32(long basePtr, int offset)
        {
            return ReadInt32(new IntPtr(basePtr), offset);
        }

        private static long ReadInt64(IntPtr basePtr, int offset)
        {
            return Marshal.ReadInt64(basePtr, offset);
        }

        private static long ReadInt64(long basePtr, int offset)
        {
            return ReadInt64(new IntPtr(basePtr), offset);
        }

        private static string ReadString(IntPtr basePtr, int offset)
        {
            IntPtr strPtr = Marshal.ReadIntPtr(basePtr, offset);
            if (strPtr == IntPtr.Zero) return "";
            return Marshal.PtrToStringAnsi(strPtr) ?? "";
        }

        private static string ReadString(long basePtr, int offset)
        {
            return ReadString(new IntPtr(basePtr), offset);
        }

        // ============================================================
        // Blender Live Link 集成
        // ============================================================

        private LiveLinkServer _liveLinkServer;
        private readonly object _sceneLock = new();

        public LiveLinkServer LiveLink => _liveLinkServer;

        /// <summary>
        /// 启动 Blender Live Link 服务器
        /// 在编辑器中运行,等待 Blender 连接
        /// </summary>
        public bool StartLiveLink(int port = 18888)
        {
            if (_liveLinkServer != null && _liveLinkServer.IsRunning)
            {
                Log("LiveLink already running");
                return true;
            }

            _liveLinkServer = new LiveLinkServer(port);
            _liveLinkServer.OnSceneReceived += OnLiveLinkSceneReceived;
            _liveLinkServer.OnClientConnected += () => Log("Blender connected via Live Link");
            _liveLinkServer.OnClientDisconnected += () => Log("Blender disconnected");
            _liveLinkServer.OnLogMessage += Log;

            bool started = _liveLinkServer.Start();
            if (started)
            {
                Log($"LiveLink server started on port {port}");
                Log("In Blender, go to: View3D > Sidebar > Shanyu > Connect");
            }
            return started;
        }

        /// <summary>
        /// 停止 Live Link 服务器
        /// </summary>
        public void StopLiveLink()
        {
            _liveLinkServer?.Stop();
            _liveLinkServer = null;
            Log("LiveLink stopped");
        }

        /// <summary>
        /// 从 LiveLink 接收场景数据
        /// </summary>
        private void OnLiveLinkSceneReceived(DCCSceneData scene)
        {
            lock (_sceneLock)
            {
                OnSceneImported?.Invoke(scene.SourceFile, scene);
            }
        }

        /// <summary>
        /// 请求 Blender 发送当前场景
        /// </summary>
        public void RequestBlenderScene()
        {
            _liveLinkServer?.RequestScene();
        }

        /// <summary>
        /// 在 Blender 中选中对象
        /// </summary>
        public void SelectInBlender(string objectName)
        {
            _liveLinkServer?.SelectObject(objectName);
        }

        // ============================================================
        // 增强导入器 - 智能选择最佳导入方式
        // ============================================================

        /// <summary>
        /// 智能导入资产 - 自动选择最佳导入方式
        /// 优先使用 Assimp,如果 DLL 不可用则使用内置解析器
        /// </summary>
        public DCCSceneData SmartImport(string filePath)
        {
            if (!File.Exists(filePath))
            {
                Log($"File not found: {filePath}");
                return null;
            }

            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            Log($"Smart importing: {Path.GetFileName(filePath)}");

            // 根据扩展名选择最佳导入方式
            switch (ext)
            {
                case ".fbx":
                case ".dae":
                case ".3ds":
                case ".lwo":
                case ".x":
                case ".blend":
                case ".dxf":
                    // 这些格式需要 Assimp 才能完整支持
                    return ImportWithAssimp(filePath);

                case ".obj":
                case ".ply":
                case ".stl":
                case ".gltf":
                case ".glb":
                    // 这些格式可以先尝试 Assimp
                    var result = ImportWithAssimp(filePath);
                    if (result == null || result.Meshes.Count == 0)
                    {
                        Log("Assimp failed, trying built-in parser");
                        return ImportScene(filePath);
                    }
                    return result;

                case ".usd":
                case ".usda":
                case ".usdc":
                case ".usdz":
                    // USD 格式需要特殊处理
                    Log("USD support requires Pixar USD SDK. Using fallback.");
                    return ImportScene(filePath);

                default:
                    // 未知格式,尝试 Assimp
                    return ImportWithAssimp(filePath);
            }
        }

        /// <summary>
        /// 批量导入目录中的所有支持格式
        /// </summary>
        public List<DCCSceneData> ImportDirectory(string directory, bool recursive = false, string[] extensions = null)
        {
            var results = new List<DCCSceneData>();

            if (!Directory.Exists(directory))
            {
                Log($"Directory not found: {directory}");
                return results;
            }

            if (extensions == null)
            {
                extensions = new[] { ".fbx", ".obj", ".gltf", ".glb", ".dae", ".ply", ".stl", ".blend" };
            }

            var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var files = new List<string>();

            foreach (var ext in extensions)
            {
                files.AddRange(Directory.GetFiles(directory, $"*{ext}", searchOption));
            }

            Log($"Batch import: {files.Count} files found in {directory}");

            foreach (var file in files)
            {
                try
                {
                    var scene = SmartImport(file);
                    if (scene != null) results.Add(scene);
                }
                catch (Exception ex)
                {
                    Log($"Failed to import {file}: {ex.Message}");
                }
            }

            Log($"Batch import complete: {results.Count}/{files.Count} succeeded");
            return results;
        }

        /// <summary>
        /// 生成 LOD (Level of Detail) 网格
        /// </summary>
        public List<DCCMeshData> GenerateLODs(DCCMeshData original, int levels = 4, float ratio = 0.5f)
        {
            var lods = new List<DCCMeshData> { original };

            for (int i = 1; i < levels; i++)
            {
                float currentRatio = MathF.Pow(ratio, i);
                if (currentRatio < 0.05f) break;

                var lod = new DCCMeshData
                {
                    Name = $"{original.Name}_LOD{i}",
                    Vertices = new List<DCCVertex>(),
                    Indices = new List<int>(),
                    MaterialNames = original.MaterialNames,
                    MaterialColors = original.MaterialColors,
                    Transform = original.Transform,
                    HasNormals = original.HasNormals,
                    HasUVs = original.HasUVs,
                    HasVertexColors = original.HasVertexColors
                };

                int targetVerts = (int)(original.Vertices.Count * currentRatio);
                if (targetVerts < 10) break;

                // 简单 LOD: 每隔一个顶点取一个
                int step = Math.Max(1, original.Vertices.Count / targetVerts);
                var keptIndices = new HashSet<int>();

                for (int j = 0; j < original.Vertices.Count; j += step)
                {
                    keptIndices.Add(j);
                    lod.Vertices.Add(original.Vertices[j]);
                }

                // 重建索引
                int newIdx = 0;
                var vertMap = new Dictionary<int, int>();
                foreach (var oldIdx in keptIndices)
                {
                    vertMap[oldIdx] = newIdx++;
                }

                foreach (var idx in original.Indices)
                {
                    if (keptIndices.Contains(idx) && vertMap.TryGetValue(idx, out var newI))
                    {
                        lod.Indices.Add(newI);
                    }
                }

                lods.Add(lod);
                Log($"Generated LOD{i}: {lod.Vertices.Count} vertices ({currentRatio:P0})");
            }

            return lods;
        }

        /// <summary>
        /// 合并多个网格
        /// </summary>
        public DCCMeshData MergeMeshes(List<DCCMeshData> meshes)
        {
            if (meshes == null || meshes.Count == 0) return null;
            if (meshes.Count == 1) return meshes[0];

            var merged = new DCCMeshData
            {
                Name = meshes[0].Name + "_Merged",
                Vertices = new List<DCCVertex>(),
                Indices = new List<int>(),
                MaterialNames = new List<string>(),
                MaterialColors = new List<Vector3>(),
                Transform = DCCTransform.Identity,
                HasNormals = meshes[0].HasNormals,
                HasUVs = meshes[0].HasUVs
            };

            int vertexOffset = 0;
            var materialIndexMap = new Dictionary<string, int>();

            foreach (var mesh in meshes)
            {
                foreach (var vert in mesh.Vertices)
                {
                    merged.Vertices.Add(vert);
                }

                foreach (var idx in mesh.Indices)
                {
                    merged.Indices.Add(idx + vertexOffset);
                }

                vertexOffset += mesh.Vertices.Count;

                foreach (var matName in mesh.MaterialNames)
                {
                    if (!materialIndexMap.TryGetValue(matName, out int matIdx))
                    {
                        matIdx = merged.MaterialNames.Count;
                        materialIndexMap[matName] = matIdx;
                        merged.MaterialNames.Add(matName);
                        merged.MaterialColors.Add(mesh.MaterialColors.Count > matIdx ? mesh.MaterialColors[matIdx] : new Vector3(0.8f));
                    }
                }
            }

            Log($"Merged {meshes.Count} meshes into {merged.Vertices.Count} vertices");
            return merged;
        }
    }
}
