// =====================================================================================
//  YamaEngine - DemoSceneGenerator
//  命名空间：YamaEngine.Rendering
// -------------------------------------------------------------------------------------
//  演示场景生成器 -- 引擎启动时如果没有加载场景，自动生成一个可见的演示场景。
//  包含多种基础几何体的真实数学参数化生成，不依赖外部库。
// =====================================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.Modeling;

namespace YamaEngine.Rendering
{
    /// <summary>
    /// 演示场景生成器 - 创建默认的可视化场景内容。
    /// 让引擎启动后立即能看到 3D 内容（而非灰色清屏）。
    /// 所有几何体均通过真实数学公式参数化生成。
    /// </summary>
    public static class DemoSceneGenerator
    {
        /// <summary>
        /// 生成一个包含多种几何体的演示场景。
        /// 包含地面、立方体、球体、圆环、圆柱、锥体和光照指示器。
        /// </summary>
        public static List<DemoMesh> GenerateDemoScene()
        {
            var meshes = new List<DemoMesh>();

            // 1. 地面 (大平面)
            meshes.Add(CreatePlane("GroundPlane", 20, 20,
                new Vector4(0.4f, 0.4f, 0.4f, 1.0f)));

            // 2. 中心立方体
            meshes.Add(CreateCube("CenterCube", 1.0f,
                new Vector4(0.8f, 0.3f, 0.2f, 1.0f),
                new Vector3(0, 0.5f, 0)));

            // 3. 球体
            meshes.Add(CreateSphere("Sphere", 0.5f, 24, 16,
                new Vector4(0.2f, 0.5f, 0.8f, 1.0f),
                new Vector3(-2, 0.5f, 0)));

            // 4. 圆环
            meshes.Add(CreateTorus("Torus", 0.4f, 0.15f, 24, 12,
                new Vector4(0.8f, 0.8f, 0.2f, 1.0f),
                new Vector3(2, 0.5f, 0)));

            // 5. 圆柱
            meshes.Add(CreateCylinder("Cylinder", 0.3f, 1.0f, 16,
                new Vector4(0.2f, 0.8f, 0.4f, 1.0f),
                new Vector3(0, 0.5f, -2)));

            // 6. 锥体
            meshes.Add(CreateCone("Cone", 0.5f, 1.0f, 16,
                new Vector4(0.7f, 0.2f, 0.7f, 1.0f),
                new Vector3(0, 0.5f, 2)));

            // 7. 光照指示器（小球放在光源位置）
            meshes.Add(CreateSphere("LightIndicator", 0.1f, 8, 8,
                new Vector4(1.0f, 1.0f, 0.8f, 1.0f),
                new Vector3(5, 8, 5)));

            return meshes;
        }

        // =================================================================================
        //  几何体创建方法（真实数学实现）
        // =================================================================================

        /// <summary>
        /// 创建 XZ 平面网格。
        /// 顶点排列为 widthSegments x depthSegments 的网格。
        /// 法线统一朝上 (0,1,0)，UV 按 x/width, z/depth 映射。
        /// 每格由 2 个三角形构成。
        /// </summary>
        public static DemoMesh CreatePlane(string name, float width, float depth,
            Vector4 baseColor, Vector3? position = null)
        {
            int widthSegments = 20;
            int depthSegments = 20;

            var mesh = new EditableMesh { Name = name };
            mesh.Vertices.EnsureCapacity((widthSegments + 1) * (depthSegments + 1));
            mesh.Normals.EnsureCapacity((widthSegments + 1) * (depthSegments + 1));
            mesh.UV0.EnsureCapacity((widthSegments + 1) * (depthSegments + 1));
            mesh.Faces.EnsureCapacity(widthSegments * depthSegments * 2);

            float halfW = width * 0.5f;
            float halfD = depth * 0.5f;

            for (int iz = 0; iz <= depthSegments; iz++)
            {
                float z = -halfD + depth * iz / depthSegments;
                for (int ix = 0; ix <= widthSegments; ix++)
                {
                    float x = -halfW + width * ix / widthSegments;
                    int vertIdx = iz * (widthSegments + 1) + ix;

                    mesh.Vertices.Add(new Vector3(x, 0, z));
                    mesh.Normals.Add(new Vector3(0, 1, 0));
                    mesh.UV0.Add(new Vector2((float)ix / widthSegments, (float)iz / depthSegments));
                }
            }

            for (int iz = 0; iz < depthSegments; iz++)
            {
                for (int ix = 0; ix < widthSegments; ix++)
                {
                    int a = iz * (widthSegments + 1) + ix;
                    int b = a + 1;
                    int c = a + (widthSegments + 1);
                    int d = c + 1;

                    mesh.Faces.Add(new MeshFace(a, c, b));
                    mesh.Faces.Add(new MeshFace(b, c, d));
                }
            }

            return new DemoMesh
            {
                Name = name,
                Mesh = mesh,
                Position = position ?? Vector3.Zero,
                Rotation = Quaternion.Identity,
                Scale = Vector3.One,
                BaseColor = baseColor,
                Metallic = 0.0f,
                Roughness = 0.8f
            };
        }

        /// <summary>
        /// 创建标准立方体。8 个唯一顶点，6 面 x 2 三角形 = 12 三角形 = 36 索引。
        /// 每面法线独立，确保平面着色效果正确（需用面法线或平滑组）。
        /// 此处使用 24 顶点版本（每面 4 个独立顶点），以支持正确的法线和 UV。
        /// </summary>
        public static DemoMesh CreateCube(string name, float size,
            Vector4 baseColor, Vector3? position = null)
        {
            float h = size * 0.5f;

            var mesh = new EditableMesh { Name = name };

            // 6 面，每面 4 个顶点（共 24 个顶点）
            // 面 0: +Z (前面)
            mesh.Vertices.AddRange(new Vector3[] { new(-h, -h, h), new(h, -h, h), new(h, h, h), new(-h, h, h) });
            mesh.Normals.AddRange(new Vector3[] { new(0, 0, 1), new(0, 0, 1), new(0, 0, 1), new(0, 0, 1) });
            mesh.UV0.AddRange(new Vector2[] { new(0, 0), new(1, 0), new(1, 1), new(0, 1) });

            // 面 1: -Z (后面)
            mesh.Vertices.AddRange(new Vector3[] { new(h, -h, -h), new(-h, -h, -h), new(-h, h, -h), new(h, h, -h) });
            mesh.Normals.AddRange(new Vector3[] { new(0, 0, -1), new(0, 0, -1), new(0, 0, -1), new(0, 0, -1) });
            mesh.UV0.AddRange(new Vector2[] { new(0, 0), new(1, 0), new(1, 1), new(0, 1) });

            // 面 2: +Y (顶面)
            mesh.Vertices.AddRange(new Vector3[] { new(-h, h, h), new(h, h, h), new(h, h, -h), new(-h, h, -h) });
            mesh.Normals.AddRange(new Vector3[] { new(0, 1, 0), new(0, 1, 0), new(0, 1, 0), new(0, 1, 0) });
            mesh.UV0.AddRange(new Vector2[] { new(0, 0), new(1, 0), new(1, 1), new(0, 1) });

            // 面 3: -Y (底面)
            mesh.Vertices.AddRange(new Vector3[] { new(-h, -h, -h), new(h, -h, -h), new(h, -h, h), new(-h, -h, h) });
            mesh.Normals.AddRange(new Vector3[] { new(0, -1, 0), new(0, -1, 0), new(0, -1, 0), new(0, -1, 0) });
            mesh.UV0.AddRange(new Vector2[] { new(0, 0), new(1, 0), new(1, 1), new(0, 1) });

            // 面 4: +X (右面)
            mesh.Vertices.AddRange(new Vector3[] { new(h, -h, h), new(h, -h, -h), new(h, h, -h), new(h, h, h) });
            mesh.Normals.AddRange(new Vector3[] { new(1, 0, 0), new(1, 0, 0), new(1, 0, 0), new(1, 0, 0) });
            mesh.UV0.AddRange(new Vector2[] { new(0, 0), new(1, 0), new(1, 1), new(0, 1) });

            // 面 5: -X (左面)
            mesh.Vertices.AddRange(new Vector3[] { new(-h, -h, -h), new(-h, -h, h), new(-h, h, h), new(-h, h, -h) });
            mesh.Normals.AddRange(new Vector3[] { new(-1, 0, 0), new(-1, 0, 0), new(-1, 0, 0), new(-1, 0, 0) });
            mesh.UV0.AddRange(new Vector2[] { new(0, 0), new(1, 0), new(1, 1), new(0, 1) });

            // 每面 2 个三角形（扇形三角化：0-1-2, 0-2-3）
            for (int face = 0; face < 6; face++)
            {
                int baseIdx = face * 4;
                mesh.Faces.Add(new MeshFace(baseIdx, baseIdx + 1, baseIdx + 2));
                mesh.Faces.Add(new MeshFace(baseIdx, baseIdx + 2, baseIdx + 3));
            }

            return new DemoMesh
            {
                Name = name,
                Mesh = mesh,
                Position = position ?? Vector3.Zero,
                Rotation = Quaternion.Identity,
                Scale = Vector3.One,
                BaseColor = baseColor,
                Metallic = 0.1f,
                Roughness = 0.5f
            };
        }

        /// <summary>
        /// 创建 UV 球体。
        /// 参数化公式：
        ///   x = r * sin(theta) * cos(phi)
        ///   y = r * cos(theta)
        ///   z = r * sin(theta) * sin(phi)
        /// 其中 theta 是纬度角 [0, PI]，phi 是经度角 [0, 2*PI]。
        /// 极点处顶点合并以避免奇点问题。
        /// </summary>
        public static DemoMesh CreateSphere(string name, float radius, int longitudeSegments, int latitudeSegments,
            Vector4 baseColor, Vector3? position = null)
        {
            radius = Math.Max(radius, 0.001f);
            longitudeSegments = Math.Max(longitudeSegments, 3);
            latitudeSegments = Math.Max(latitudeSegments, 2);

            var mesh = new EditableMesh { Name = name };

            // 生成顶点
            for (int lat = 0; lat <= latitudeSegments; lat++)
            {
                float theta = (float)Math.PI * lat / latitudeSegments;
                float sinT = MathF.Sin(theta);
                float cosT = MathF.Cos(theta);

                for (int lon = 0; lon <= longitudeSegments; lon++)
                {
                    float phi = 2f * (float)Math.PI * lon / longitudeSegments;
                    float sinP = MathF.Sin(phi);
                    float cosP = MathF.Cos(phi);

                    float x = radius * sinT * cosP;
                    float y = radius * cosT;
                    float z = radius * sinT * sinP;

                    mesh.Vertices.Add(new Vector3(x, y, z));
                    mesh.Normals.Add(Vector3.Normalize(new Vector3(x, y, z)));
                    mesh.UV0.Add(new Vector2((float)lon / longitudeSegments, (float)lat / latitudeSegments));
                }
            }

            // 生成三角形索引
            for (int lat = 0; lat < latitudeSegments; lat++)
            {
                for (int lon = 0; lon < longitudeSegments; lon++)
                {
                    int first = lat * (longitudeSegments + 1) + lon;
                    int second = first + longitudeSegments + 1;

                    if (lat != 0)
                    {
                        mesh.Faces.Add(new MeshFace(first, second, first + 1));
                    }

                    if (lat != latitudeSegments - 1)
                    {
                        mesh.Faces.Add(new MeshFace(first + 1, second, second + 1));
                    }
                }
            }

            return new DemoMesh
            {
                Name = name,
                Mesh = mesh,
                Position = position ?? Vector3.Zero,
                Rotation = Quaternion.Identity,
                Scale = Vector3.One,
                BaseColor = baseColor,
                Metallic = 0.2f,
                Roughness = 0.3f
            };
        }

        /// <summary>
        /// 创建参数化圆环（Torus）。
        /// 参数化公式：
        ///   x = (R + r * cos(v)) * cos(u)
        ///   y = r * sin(v)
        ///   z = (R + r * cos(v)) * sin(u)
        /// 其中 R 是主半径，r 是管道半径，
        /// u 是围绕主圆的角 [0, 2*PI]，v 是围绕管道截面的角 [0, 2*PI]。
        /// 法线通过切线和副切线的叉积计算：
        ///   tangent  = dP/du = (-(R + r*cos(v)) * sin(u), 0, (R + r*cos(v)) * cos(u))
        ///   bitangent= dP/dv = (-r * sin(v) * cos(u), r * cos(v), -r * sin(v) * sin(u))
        ///   normal   = cross(tangent, bitangent) 归一化后指向外侧
        /// </summary>
        public static DemoMesh CreateTorus(string name, float majorRadius, float minorRadius,
            int majorSegments, int minorSegments,
            Vector4 baseColor, Vector3? position = null)
        {
            majorRadius = Math.Max(majorRadius, 0.001f);
            minorRadius = Math.Max(minorRadius, 0.001f);
            majorSegments = Math.Max(majorSegments, 3);
            minorSegments = Math.Max(minorSegments, 3);

            var mesh = new EditableMesh { Name = name };

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

                    // 位置
                    float r = majorRadius + minorRadius * cosV;
                    var pos = new Vector3(r * cosU, minorRadius * sinV, r * sinU);

                    // 切线 (dP/du)
                    var tangent = new Vector3(-sinU, 0, cosU);

                    // 副切线 (dP/dv)
                    var bitangent = new Vector3(cosU * (-sinV), cosV, sinU * (-sinV));

                    // 法线 = normalize(cross(tangent, bitangent))
                    var normal = Vector3.Normalize(Vector3.Cross(tangent, bitangent));

                    mesh.Vertices.Add(pos);
                    mesh.Normals.Add(normal);
                    mesh.UV0.Add(new Vector2(u, v));
                }
            }

            for (int i = 0; i < majorSegments; i++)
            {
                for (int j = 0; j < minorSegments; j++)
                {
                    int a = i * (minorSegments + 1) + j;
                    int b = a + minorSegments + 1;

                    mesh.Faces.Add(new MeshFace(a, b, a + 1));
                    mesh.Faces.Add(new MeshFace(a + 1, b, b + 1));
                }
            }

            return new DemoMesh
            {
                Name = name,
                Mesh = mesh,
                Position = position ?? Vector3.Zero,
                Rotation = Quaternion.Identity,
                Scale = Vector3.One,
                BaseColor = baseColor,
                Metallic = 0.3f,
                Roughness = 0.4f
            };
        }

        /// <summary>
        /// 创建圆柱体（含侧面 + 顶面 + 底面）。
        /// 侧面参数化：
        ///   x = r * cos(theta)
        ///   y = height * t
        ///   z = r * sin(theta)
        /// 其中 theta 是圆周角 [0, 2*PI]，t 是高度参数 [0, 1]。
        /// 顶面和底面使用扇形三角化（以圆心为中心）。
        /// </summary>
        public static DemoMesh CreateCylinder(string name, float radius, float height,
            int segments, Vector4 baseColor, Vector3? position = null)
        {
            radius = Math.Max(radius, 0.001f);
            height = Math.Max(height, 0.001f);
            segments = Math.Max(segments, 3);

            var mesh = new EditableMesh { Name = name };
            float halfH = height * 0.5f;

            // 侧面顶点（底部一圈 + 顶部一圈）
            for (int i = 0; i <= segments; i++)
            {
                float theta = 2f * (float)Math.PI * i / segments;
                float cosT = MathF.Cos(theta);
                float sinT = MathF.Sin(theta);

                // 底部顶点 (y = -halfH)
                mesh.Vertices.Add(new Vector3(radius * cosT, -halfH, radius * sinT));
                mesh.Normals.Add(new Vector3(cosT, 0, sinT));
                mesh.UV0.Add(new Vector2((float)i / segments, 0));

                // 顶部顶点 (y = +halfH)
                mesh.Vertices.Add(new Vector3(radius * cosT, halfH, radius * sinT));
                mesh.Normals.Add(new Vector3(cosT, 0, sinT));
                mesh.UV0.Add(new Vector2((float)i / segments, 1));
            }

            // 侧面三角形
            for (int i = 0; i < segments; i++)
            {
                int a = i * 2;       // 底部当前
                int b = a + 1;       // 顶部当前
                int c = a + 2;       // 底部下一个
                int d = a + 3;       // 顶部下一个

                mesh.Faces.Add(new MeshFace(a, c, b));
                mesh.Faces.Add(new MeshFace(b, c, d));
            }

            // 圆心顶点
            int bottomCenterIdx = mesh.Vertices.Count;
            mesh.Vertices.Add(new Vector3(0, -halfH, 0));
            mesh.Normals.Add(new Vector3(0, -1, 0));
            mesh.UV0.Add(new Vector2(0.5f, 0.5f));

            int topCenterIdx = mesh.Vertices.Count;
            mesh.Vertices.Add(new Vector3(0, halfH, 0));
            mesh.Normals.Add(new Vector3(0, 1, 0));
            mesh.UV0.Add(new Vector2(0.5f, 0.5f));

            // 底面扇形三角形（顺时针从外部看）
            for (int i = 0; i < segments; i++)
            {
                int bottomEdge = i * 2;
                int nextBottomEdge = ((i + 1) % segments) * 2;
                mesh.Faces.Add(new MeshFace(bottomCenterIdx, bottomEdge, nextBottomEdge));
            }

            // 顶面扇形三角形
            for (int i = 0; i < segments; i++)
            {
                int topEdge = i * 2 + 1;
                int nextTopEdge = ((i + 1) % segments) * 2 + 1;
                mesh.Faces.Add(new MeshFace(topCenterIdx, nextTopEdge, topEdge));
            }

            return new DemoMesh
            {
                Name = name,
                Mesh = mesh,
                Position = position ?? Vector3.Zero,
                Rotation = Quaternion.Identity,
                Scale = Vector3.One,
                BaseColor = baseColor,
                Metallic = 0.1f,
                Roughness = 0.6f
            };
        }

        /// <summary>
        /// 创建锥体（含侧面 + 底面）。
        /// 侧面参数化：
        ///   顶部汇聚于 (0, halfH, 0)
        ///   底部圆周: x = r * cos(theta), y = -halfH, z = r * sin(theta)
        /// 侧面法线通过底部顶点和顶部点的叉积计算。
        /// 底面使用扇形三角化（与圆柱相同）。
        /// </summary>
        public static DemoMesh CreateCone(string name, float radius, float height,
            int segments, Vector4 baseColor, Vector3? position = null)
        {
            radius = Math.Max(radius, 0.001f);
            height = Math.Max(height, 0.001f);
            segments = Math.Max(segments, 3);

            var mesh = new EditableMesh { Name = name };
            float halfH = height * 0.5f;

            // 顶部顶点
            int apexIdx = mesh.Vertices.Count;
            mesh.Vertices.Add(new Vector3(0, halfH, 0));
            mesh.Normals.Add(new Vector3(0, 1, 0));
            mesh.UV0.Add(new Vector2(0.5f, 1.0f));

            // 底部圆周顶点
            for (int i = 0; i <= segments; i++)
            {
                float theta = 2f * (float)Math.PI * i / segments;
                float cosT = MathF.Cos(theta);
                float sinT = MathF.Sin(theta);

                mesh.Vertices.Add(new Vector3(radius * cosT, -halfH, radius * sinT));
                mesh.UV0.Add(new Vector2((float)i / segments, 0));

                // 计算侧面法线
                // 侧面向量: 从底部点到顶部点的方向
                var sideDir = Vector3.Normalize(new Vector3(-radius * cosT, height, -radius * sinT));
                // 切线（圆周方向）
                var tangent = new Vector3(-sinT, 0, cosT);
                // 法线 = normalize(cross(tangent, sideDir))
                var normal = Vector3.Normalize(Vector3.Cross(tangent, sideDir));
                mesh.Normals.Add(normal);
            }

            // 侧面三角形（扇形：顶点 -> 当前底部 -> 下一个底部）
            for (int i = 0; i < segments; i++)
            {
                int bottomA = apexIdx + 1 + i;
                int bottomB = apexIdx + 1 + i + 1;
                mesh.Faces.Add(new MeshFace(apexIdx, bottomB, bottomA));
            }

            // 底面圆心
            int bottomCenterIdx = mesh.Vertices.Count;
            mesh.Vertices.Add(new Vector3(0, -halfH, 0));
            mesh.Normals.Add(new Vector3(0, -1, 0));
            mesh.UV0.Add(new Vector2(0.5f, 0.5f));

            // 底面扇形三角形
            for (int i = 0; i < segments; i++)
            {
                int edgeA = apexIdx + 1 + i;
                int edgeB = apexIdx + 1 + i + 1;
                mesh.Faces.Add(new MeshFace(bottomCenterIdx, edgeA, edgeB));
            }

            return new DemoMesh
            {
                Name = name,
                Mesh = mesh,
                Position = position ?? Vector3.Zero,
                Rotation = Quaternion.Identity,
                Scale = Vector3.One,
                BaseColor = baseColor,
                Metallic = 0.15f,
                Roughness = 0.55f
            };
        }
    }

    /// <summary>
    /// 演示网格数据结构。包含网格几何体、变换、PBR 材质参数。
    /// </summary>
    public struct DemoMesh
    {
        /// <summary>网格名称。</summary>
        public string Name;

        /// <summary>EditableMesh 几何数据（顶点、法线、UV、面）。</summary>
        public EditableMesh Mesh;

        /// <summary>世界空间位置。</summary>
        public Vector3 Position;

        /// <summary>旋转四元数。</summary>
        public Quaternion Rotation;

        /// <summary>缩放系数。</summary>
        public Vector3 Scale;

        /// <summary>基础颜色（RGBA 线性空间）。</summary>
        public Vector4 BaseColor;

        /// <summary>金属度 [0, 1]。</summary>
        public float Metallic;

        /// <summary>粗糙度 [0, 1]。</summary>
        public float Roughness;
    }
}
