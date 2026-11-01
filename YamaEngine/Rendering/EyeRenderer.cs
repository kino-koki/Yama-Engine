using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.ECS;
using YamaEngine.RHI;

namespace YamaEngine.Rendering
{
    // ============================================================================================
    //  EyeRenderer.cs
    //
    //  眼球渲染系统。提供电影级角色眼球的高质量实时渲染，涵盖角膜折射、虹膜程序化
    //  纹理、瞳孔动态缩放、巩膜血管、眼睑眨眼、注视追踪、次表面散射近似与湿润反射。
    //
    //  眼球是角色表现力的核心——观众看角色时首先看眼睛。本系统通过多层几何与着色
    //  模拟真实眼球的复杂光学特性，使数字角色获得"灵魂之窗"般的真实感。
    //
    //  本系统包含以下核心模块：
    //    - EyeComponent         ：ECS 组件，存储注视目标、瞳孔大小、眨眼状态、虹膜颜色等
    //    - EyeAnatomy           ：眼球解剖参数（角膜曲率、虹膜半径、瞳孔范围等）
    //    - EyeMeshGenerator     ：程序化生成眼球各层网格（巩膜/角膜/虹膜/瞳孔/眼睑）
    //    - IrisTextureGenerator ：程序化虹膜纹理生成（纤维细节、色素沉着、色彩渐变）
    //    - CorneaOptics         ：角膜折射光学模拟（Snell 定律多层折射）
    //    - GazeTracker          ：注视追踪，计算眼球朝向目标的世界旋转
    //    - EyelidAnimator       ：眼睑动画控制器（眨眼曲线、注视扫视遮挡）
    //    - EyeRenderer          ：核心渲染器，组装渲染管线并提交绘制命令
    //    - EyeRenderSystem      ：ECS 系统，每帧查询 EyeComponent 并驱动渲染
    //
    //  与引擎其他系统的对接：
    //    - RHI：通过 RHIDevice 创建纹理/缓冲区/管线，通过 RHICommandList 提交绘制
    //    - ECS：通过 EyeComponent 查询拥有眼球组件的实体
    // ============================================================================================

    // ──────────────────────────────────────────────────────────────
    //  眼球解剖区域枚举
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    ///   眼球解剖区域。标识渲染管线中的不同着色阶段和几何层。
    /// </summary>
    public enum EyeRegion : byte
    {
        /// <summary>巩膜（眼白），带血管细节。</summary>
        Sclera = 0,
        /// <summary>角膜，透明折射层，覆盖虹膜和瞳孔。</summary>
        Cornea = 1,
        /// <summary>虹膜，有色环状结构，控制瞳孔大小。</summary>
        Iris = 2,
        /// <summary>瞳孔，虹膜中央的黑色孔洞。</summary>
        Pupil = 3,
        /// <summary>眼睑，覆盖眼球上下方的皮肤。</summary>
        Eyelid = 4,
        /// <summary>泪液层（湿润薄膜），角膜表面的液体层。</summary>
        TearFilm = 5,
    }

    // ──────────────────────────────────────────────────────────────
    //  眼球解剖参数
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    ///   眼球解剖参数。描述眼球各结构的物理尺寸与光学特性。
    ///   这些参数基于真实人体眼球解剖数据，可根据角色风格调整。
    /// </summary>
    public sealed class EyeAnatomy
    {
        /// <summary>眼球半径（厘米）。成人约 1.2cm。</summary>
        public float EyeballRadius = 1.2f;

        /// <summary>角膜半径（角膜球面的曲率半径，厘米）。成人约 0.78cm。</summary>
        public float CorneaRadius = 0.78f;

        /// <summary>虹膜半径（虹膜外缘到中心的距离，厘米）。成人约 0.6cm。</summary>
        public float IrisRadius = 0.6f;

        /// <summary>瞳孔最小半径（厘米，强光下）。</summary>
        public float PupilMinRadius = 0.15f;

        /// <summary>瞳孔最大半径（厘米，暗光下）。</summary>
        public float PupilMaxRadius = 0.4f;

        /// <summary>角膜折射率（空气 1.0，角膜约 1.376）。</summary>
        public float CorneaIOR = 1.376f;

        /// <summary>房水折射率（角膜后液体，约 1.336）。</summary>
        public float AqueousHumorIOR = 1.336f;

        /// <summary>虹膜到角膜顶点的距离（厘米），决定折射偏移量。</summary>
        public float IrisDepth = 0.15f;

        /// <summary>巩膜基础颜色（偏白的淡黄色）。</summary>
        public Vector3 ScleraBaseColor = new(0.95f, 0.92f, 0.88f);

        /// <summary>巩膜血管颜色（淡红色）。</summary>
        public Vector3 ScleraVeinColor = new(0.75f, 0.25f, 0.2f);

        /// <summary>巩膜血管密度（0=无血管，1=密集）。</summary>
        public float ScleraVeinDensity = 0.35f;

        /// <summary>次表面散射近似系数（控制光线穿透组织的程度）。</summary>
        public float SSSCoeff = 0.6f;

        /// <summary>泪液层反射强度（湿润高光）。</summary>
        public float TearFilmReflectance = 0.8f;

        /// <summary>眼睑皮肤厚度（厘米）。</summary>
        public float EyelidThickness = 0.08f;
    }

    // ──────────────────────────────────────────────────────────────
    //  眼球 ECS 组件
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    ///   眼球 ECS 组件。附加到实体上使其拥有可渲染的眼球。
    ///   存储注视目标、瞳孔大小、眨眼状态、虹膜颜色等运行时参数。
    ///   左右眼分别使用独立的组件实例（或在同一实体上用 Left/Right 区分）。
    /// </summary>
    public struct EyeComponent : IComponent
    {
        /// <summary>是否为左眼（false=右眼）。</summary>
        public bool IsLeftEye;

        /// <summary>注视目标的世界空间位置。眼球将朝向此位置旋转。</summary>
        public Vector3 LookAtTarget;

        /// <summary>是否启用注视追踪（true=朝向 LookAtTarget，false=保持朝前）。</summary>
        public bool GazeTrackingEnabled;

        /// <summary>当前瞳孔半径（厘米），由光照自动驱动或手动设置。</summary>
        public float PupilRadius;

        /// <summary>瞳孔是否根据环境光照自动缩放。</summary>
        public bool AutoPupilDilation;

        /// <summary>当前环境光照强度（0~1），用于自动瞳孔缩放。</summary>
        public float AmbientLightLevel;

        /// <summary>虹膜基础颜色（RGB，0~1）。</summary>
        public Vector3 IrisColor;

        /// <summary>虹膜色素沉着强度（0=均匀，1=高度斑驳）。</summary>
        public float IrisPigmentation;

        /// <summary>虹膜纤维细节强度（0=光滑，1=高对比纤维纹理）。</summary>
        public float IrisFiberDetail;

        /// <summary>当前眨眼闭合度（0=完全睁开，1=完全闭眼）。</summary>
        public float BlinkAmount;

        /// <summary>下次自动眨眼的剩余时间（秒）。小于等于 0 时触发眨眼。</summary>
        public float BlinkTimer;

        /// <summary>自动眨眼间隔范围（秒）：(最小间隔, 最大间隔)。</summary>
        public Vector2 BlinkIntervalRange;

        /// <summary>单次眨眼持续时间（秒）。</summary>
        public float BlinkDuration;

        /// <summary>是否正在眨眼中。</summary>
        public bool IsBlinking;

        /// <summary>眨眼动画进度（0~1，0=开始闭眼，0.5=完全闭眼，1=睁眼完成）。</summary>
        public float BlinkProgress;

        /// <summary>眼球相对头部的偏移位置（局部空间）。</summary>
        public Vector3 EyeOffset;

        /// <summary>眼球最大旋转角度限制（度，防止眼球过度旋转）。</summary>
        public float MaxGazeAngle;

        /// <summary>是否启用。</summary>
        public bool Enabled;

        /// <summary>视线方向 LOD 距离阈值（米）。超过此距离使用简化着色。</summary>
        public float LodDistance;

        /// <summary>初始化默认值。</summary>
        public static EyeComponent CreateDefault(bool isLeft)
        {
            return new EyeComponent
            {
                IsLeftEye = isLeft,
                LookAtTarget = new Vector3(0, 0, 10),
                GazeTrackingEnabled = true,
                PupilRadius = 0.25f,
                AutoPupilDilation = true,
                AmbientLightLevel = 0.7f,
                IrisColor = new Vector3(0.3f, 0.5f, 0.8f),
                IrisPigmentation = 0.5f,
                IrisFiberDetail = 0.7f,
                BlinkAmount = 0f,
                BlinkTimer = 3f,
                BlinkIntervalRange = new Vector2(2f, 6f),
                BlinkDuration = 0.15f,
                IsBlinking = false,
                BlinkProgress = 0f,
                EyeOffset = new Vector3(isLeft ? -0.3f : 0.3f, 0.1f, 0),
                MaxGazeAngle = 35f,
                Enabled = true,
                LodDistance = 15f,
            };
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  眼球网格数据
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    ///   眼球顶点。包含位置、法线、切线、UV 坐标。
    /// </summary>
    public struct EyeVertex
    {
        /// <summary>顶点位置（模型空间）。</summary>
        public Vector3 Position;
        /// <summary>顶点法线。</summary>
        public Vector3 Normal;
        /// <summary>顶点切线。</summary>
        public Vector3 Tangent;
        /// <summary>纹理坐标。</summary>
        public Vector2 TexCoord;
    }

    /// <summary>
    ///   眼球网格数据。包含顶点数组、索引数组以及所属的区域标识。
    /// </summary>
    public sealed class EyeMesh
    {
        /// <summary>顶点数组。</summary>
        public List<EyeVertex> Vertices = new();

        /// <summary>索引数组。</summary>
        public List<uint> Indices = new();

        /// <summary>所属的眼球区域。</summary>
        public EyeRegion Region;

        /// <summary>RHI 顶点缓冲区（UploadGPU 后有效）。</summary>
        public RHIBuffer VertexBuffer;

        /// <summary>RHI 索引缓冲区（UploadGPU 后有效）。</summary>
        public RHIBuffer IndexBuffer;

        /// <summary>顶点数。</summary>
        public int VertexCount => Vertices.Count;

        /// <summary>索引数。</summary>
        public int IndexCount => Indices.Count;
    }

    // ──────────────────────────────────────────────────────────────
    //  眼球网格生成器
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    ///   眼球网格生成器。程序化生成眼球各层的几何体：
    ///     - 巩膜：球体的后半部分（略带前缘延伸）
    ///     - 角膜：球体前部凸起的球冠
    ///     - 虹膜：前部圆盘（略凹）
    ///     - 瞳孔：虹膜中心的孔洞（通过绘制为黑色圆盘实现）
    ///     - 眼睑：上下两条可变形的条带
    ///   所有网格以眼球中心为原点，+Z 方向为前方（注视方向）。
    /// </summary>
    public static class EyeMeshGenerator
    {
        /// <summary>生成巩膜（眼白）网格。球体去掉前部角膜区域。</summary>
        public static EyeMesh GenerateSclera(EyeAnatomy anatomy, int longitudeSegments, int latitudeSegments)
        {
            var mesh = new EyeMesh { Region = EyeRegion.Sclera };
            float r = anatomy.EyeballRadius;
            float corneaAngle = MathF.Asin(anatomy.CorneaRadius / r);

            for (int lat = 0; lat <= latitudeSegments; lat++)
            {
                float phi = MathF.PI * lat / latitudeSegments;
                for (int lon = 0; lon <= longitudeSegments; lon++)
                {
                    float theta = 2f * MathF.PI * lon / longitudeSegments;
                    // 跳过角膜覆盖的前部区域
                    float angleFromFront = phi;
                    if (angleFromFront < corneaAngle * 0.95f) continue;

                    Vector3 pos = new(
                        r * MathF.Sin(phi) * MathF.Cos(theta),
                        r * MathF.Cos(phi),
                        r * MathF.Sin(phi) * MathF.Sin(theta));
                    Vector3 normal = Vector3.Normalize(pos);

                    mesh.Vertices.Add(new EyeVertex
                    {
                        Position = pos,
                        Normal = normal,
                        Tangent = new Vector3(-MathF.Sin(theta), 0, MathF.Cos(theta)),
                        TexCoord = new Vector2(theta / (2f * MathF.PI), phi / MathF.PI),
                    });
                }
            }
            GenerateIndices(mesh, longitudeSegments, latitudeSegments);
            return mesh;
        }

        /// <summary>生成角膜网格。前部凸起的球冠，半径略大于眼球以模拟折射凸面。</summary>
        public static EyeMesh GenerateCornea(EyeAnatomy anatomy, int segments)
        {
            var mesh = new EyeMesh { Region = EyeRegion.Cornea };
            float r = anatomy.CorneaRadius * 1.05f;
            float maxAngle = MathF.PI * 0.35f;

            for (int lat = 0; lat <= segments; lat++)
            {
                float phi = maxAngle * lat / segments;
                for (int lon = 0; lon <= segments; lon++)
                {
                    float theta = 2f * MathF.PI * lon / segments;
                    Vector3 pos = new(
                        r * MathF.Sin(phi) * MathF.Cos(theta),
                        r * MathF.Cos(phi),
                        r * MathF.Sin(phi) * MathF.Sin(theta));
                    mesh.Vertices.Add(new EyeVertex
                    {
                        Position = pos,
                        Normal = Vector3.Normalize(pos),
                        Tangent = new Vector3(-MathF.Sin(theta), 0, MathF.Cos(theta)),
                        TexCoord = new Vector2(theta / (2f * MathF.PI), phi / maxAngle),
                    });
                }
            }
            GenerateIndices(mesh, segments, segments);
            return mesh;
        }

        /// <summary>生成虹膜网格。前凹的圆盘，带中心瞳孔孔洞。</summary>
        public static EyeMesh GenerateIris(EyeAnatomy anatomy, int segments)
        {
            var mesh = new EyeMesh { Region = EyeRegion.Iris };
            float outerR = anatomy.IrisRadius;
            float innerR = anatomy.PupilMaxRadius;
            float depth = anatomy.IrisDepth;

            for (int ring = 0; ring <= segments; ring++)
            {
                float t = (float)ring / segments;
                float radius = innerR + (outerR - innerR) * t;
                float z = -depth * (1f - t * t); // 虹膜略凹
                for (int seg = 0; seg <= segments; seg++)
                {
                    float angle = 2f * MathF.PI * seg / segments;
                    Vector3 pos = new(
                        radius * MathF.Cos(angle),
                        radius * MathF.Sin(angle),
                        z);
                    mesh.Vertices.Add(new EyeVertex
                    {
                        Position = pos,
                        Normal = new Vector3(0, 0, 1),
                        Tangent = new Vector3(-MathF.Sin(angle), MathF.Cos(angle), 0),
                        TexCoord = new Vector2(t, angle / (2f * MathF.PI)),
                    });
                }
            }
            GenerateIndices(mesh, segments, segments);
            return mesh;
        }

        /// <summary>生成瞳孔网格。黑色圆盘，位于虹膜中心。</summary>
        public static EyeMesh GeneratePupil(EyeAnatomy anatomy, int segments)
        {
            var mesh = new EyeMesh { Region = EyeRegion.Pupil };
            float r = anatomy.PupilMaxRadius;
            float z = -anatomy.IrisDepth;

            // 中心顶点
            mesh.Vertices.Add(new EyeVertex
            {
                Position = new Vector3(0, 0, z),
                Normal = new Vector3(0, 0, 1),
                Tangent = Vector3.UnitX,
                TexCoord = Vector2.Zero,
            });

            for (int seg = 0; seg <= segments; seg++)
            {
                float angle = 2f * MathF.PI * seg / segments;
                mesh.Vertices.Add(new EyeVertex
                {
                    Position = new Vector3(r * MathF.Cos(angle), r * MathF.Sin(angle), z),
                    Normal = new Vector3(0, 0, 1),
                    Tangent = new Vector3(-MathF.Sin(angle), MathF.Cos(angle), 0),
                    TexCoord = new Vector2(1f, angle / (2f * MathF.PI)),
                });
            }

            // 扇形索引
            for (int seg = 0; seg < segments; seg++)
            {
                mesh.Indices.Add(0);
                mesh.Indices.Add((uint)(seg + 1));
                mesh.Indices.Add((uint)(seg + 2));
            }
            return mesh;
        }

        /// <summary>生成眼睑网格。上下两条可变形条带，用于眨眼遮挡。</summary>
        public static EyeMesh GenerateEyelid(EyeAnatomy anatomy, int segments, bool upper)
        {
            var mesh = new EyeMesh { Region = EyeRegion.Eyelid };
            float r = anatomy.EyeballRadius + anatomy.EyelidThickness * 0.5f;
            float width = anatomy.IrisRadius * 2.5f;

            for (int i = 0; i <= segments; i++)
            {
                float t = (float)i / segments;
                float x = -width * 0.5f + width * t;
                float y = upper ? r * MathF.Sin(MathF.PI * t) : -r * MathF.Sin(MathF.PI * t);
                float z = anatomy.CorneaRadius * 0.3f;

                Vector3 pos = new(x, y, z);
                Vector3 normal = upper ? Vector3.UnitY : -Vector3.UnitY;

                mesh.Vertices.Add(new EyeVertex
                {
                    Position = pos,
                    Normal = normal,
                    Tangent = Vector3.UnitX,
                    TexCoord = new Vector2(t, upper ? 0f : 1f),
                });
            }

            // 生成条带的上下沿（模拟厚度）
            int baseCount = mesh.Vertices.Count;
            for (int i = 0; i <= segments; i++)
            {
                var v = mesh.Vertices[i];
                v.Position += new Vector3(0, upper ? anatomy.EyelidThickness : -anatomy.EyelidThickness, 0);
                mesh.Vertices.Add(v);
            }

            for (int i = 0; i < segments; i++)
            {
                mesh.Indices.Add((uint)i);
                mesh.Indices.Add((uint)(i + 1));
                mesh.Indices.Add((uint)(baseCount + i));
                mesh.Indices.Add((uint)(i + 1));
                mesh.Indices.Add((uint)(baseCount + i + 1));
                mesh.Indices.Add((uint)(baseCount + i));
            }
            return mesh;
        }

        private static void GenerateIndices(EyeMesh mesh, int lonSeg, int latSeg)
        {
            for (int lat = 0; lat < latSeg; lat++)
            {
                for (int lon = 0; lon < lonSeg; lon++)
                {
                    uint i0 = (uint)(lat * (lonSeg + 1) + lon);
                    uint i1 = i0 + 1;
                    uint i2 = i0 + (uint)(lonSeg + 1);
                    uint i3 = i2 + 1;

                    if (i0 < mesh.Vertices.Count && i3 < mesh.Vertices.Count)
                    {
                        mesh.Indices.Add(i0);
                        mesh.Indices.Add(i2);
                        mesh.Indices.Add(i1);
                        mesh.Indices.Add(i1);
                        mesh.Indices.Add(i2);
                        mesh.Indices.Add(i3);
                    }
                }
            }
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  虹膜程序化纹理生成器
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    ///   虹膜程序化纹理生成器。在 CPU 上生成虹膜的纹理图集，包含：
    ///     - 放射状纤维细节（虹膜的肌肉纤维纹理）
    ///     - 色素沉着斑驳（虹膜的颜色不均匀分布）
    ///     - 从瞳孔到外缘的色彩渐变
    ///     - Limbal Ring（虹膜外缘的暗环）
    ///   生成的纹理通过 RHI 上传到 GPU 供像素着色器采样。
    /// </summary>
    public sealed class IrisTextureGenerator
    {
        /// <summary>纹理分辨率。</summary>
        public int Resolution { get; set; } = 512;

        /// <summary>随机数种子。</summary>
        public int Seed { get; set; } = 42;

        /// <summary>
        ///   生成虹膜纹理像素数据（RGBA8）。
        /// </summary>
        /// <param name="irisColor">虹膜基础颜色。</param>
        /// <param name="pigmentation">色素沉着强度（0~1）。</param>
        /// <param name="fiberDetail">纤维细节强度（0~1）。</param>
        /// <returns>RGBA8 格式的纹理像素数据。</returns>
        public byte[] Generate(Vector3 irisColor, float pigmentation, float fiberDetail)
        {
            int size = Resolution;
            byte[] pixels = new byte[size * size * 4];
            var rand = new Random(Seed);
            int center = size / 2;
            float pupilR = size * 0.15f;
            float irisR = size * 0.48f;
            float limbalR = size * 0.46f;

            // 预生成噪声偏移表
            var noiseOffsets = new Vector2[64];
            for (int i = 0; i < 64; i++)
                noiseOffsets[i] = new Vector2((float)rand.NextDouble(), (float)rand.NextDouble());

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - center;
                    float dy = y - center;
                    float dist = MathF.Sqrt(dx * dx + dy * dy);
                    float angle = MathF.Atan2(dy, dx);
                    float normalizedDist = dist / irisR;

                    Vector3 color = irisColor;

                    // 1. 瞳孔区域：深黑色
                    if (dist < pupilR)
                    {
                        float pupilFade = Math.Clamp(dist / pupilR, 0f, 1f);
                        color = Vector3.Lerp(new Vector3(0.02f, 0.02f, 0.03f), irisColor * 0.3f, pupilFade);
                    }
                    // 2. 虹膜区域
                    else if (dist <= irisR)
                    {
                        // 从内到外的色彩渐变（内深外浅）
                        float gradT = (dist - pupilR) / (irisR - pupilR);
                        Vector3 darkColor = irisColor * 0.5f;
                        Vector3 lightColor = irisColor * 1.2f;
                        color = Vector3.Lerp(darkColor, lightColor, gradT);

                        // 放射状纤维纹理
                        float fiberFreq = 80f * fiberDetail;
                        float fiberNoise = MathF.Sin(angle * fiberFreq + Hash01((int)(gradT * 100)) * 6.28f);
                        fiberNoise = fiberNoise * 0.5f + 0.5f;
                        color *= 0.85f + fiberNoise * 0.3f * fiberDetail;

                        // 色素沉着斑驳
                        if (pigmentation > 0f)
                        {
                            int nIdx = (int)(normalizedDist * 63) & 63;
                            float blotch = ValueNoise2D(dx * 0.08f, dy * 0.08f, noiseOffsets[nIdx]);
                            blotch = MathF.Pow(blotch, 3f);
                            Vector3 blotchColor = irisColor * 1.4f + new Vector3(0.1f, 0.05f, 0f);
                            color = Vector3.Lerp(color, blotchColor, blotch * pigmentation);
                        }

                        // Limbal Ring（外缘暗环）
                        if (dist > limbalR)
                        {
                            float limbalT = (dist - limbalR) / (irisR - limbalR);
                            color = Vector3.Lerp(color, color * 0.3f, limbalT * 0.7f);
                        }
                    }
                    // 3. 巩膜区域
                    else
                    {
                        color = new Vector3(0.95f, 0.92f, 0.88f);
                    }

                    // 写入像素
                    int idx = (y * size + x) * 4;
                    pixels[idx] = (byte)(Math.Clamp(color.X, 0f, 1f) * 255f);
                    pixels[idx + 1] = (byte)(Math.Clamp(color.Y, 0f, 1f) * 255f);
                    pixels[idx + 2] = (byte)(Math.Clamp(color.Z, 0f, 1f) * 255f);
                    pixels[idx + 3] = 255;
                }
            }
            return pixels;
        }

        /// <summary>将生成的虹膜纹理上传到 GPU。</summary>
        public RHITexture UploadToGPU(RHIDevice device, byte[] pixels)
        {
            if (device == null) throw new ArgumentNullException(nameof(device));
            var texture = device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R8G8B8A8_UNorm,
                Width = Resolution,
                Height = Resolution,
                DepthOrArraySize = 1,
                MipLevels = 1,
                SampleCount = 1,
                Flags = RHITextureFlags.ShaderResource,
            }, "IrisTexture");
            device.UploadToTexture(texture, pixels, Resolution, Resolution, 0, 0);
            return texture;
        }

        private static float Hash01(int n)
        {
            n = (n << 13) ^ n;
            return ((n * (n * n * 15731 + 789221) + 1376312589) & 0x7FFFFFFF) / (float)0x7FFFFFFF;
        }

        private static float ValueNoise2D(float x, float y, Vector2 offset)
        {
            int xi = (int)MathF.Floor(x + offset.X);
            int yi = (int)MathF.Floor(y + offset.Y);
            float xf = x + offset.X - xi;
            float yf = y + offset.Y - yi;

            float u = xf * xf * (3f - 2f * xf);
            float v = yf * yf * (3f - 2f * yf);

            float a = Hash01(xi * 73856093 ^ yi * 19349663);
            float b = Hash01((xi + 1) * 73856093 ^ yi * 19349663);
            float c = Hash01(xi * 73856093 ^ (yi + 1) * 19349663);
            float d = Hash01((xi + 1) * 73856093 ^ (yi + 1) * 19349663);

            return (a * (1 - u) + b * u) * (1 - v) + (c * (1 - u) + d * u) * v;
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  角膜折射光学
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    ///   角膜折射光学模拟。使用 Snell 定律计算光线穿过角膜-房水界面时的折射偏移，
    ///   使虹膜和瞳孔在视觉上被角膜的凸面"放大"和位移，产生真实的光学效果。
    ///
    ///   折射模拟在着色器中完成，此类提供 CPU 端的参数计算和辅助方法。
    /// </summary>
    public static class CorneaOptics
    {
        /// <summary>
        ///   计算角膜折射参数（传递给着色器）。
        /// </summary>
        public struct CorneaRefractParams
        {
            /// <summary>角膜折射率比（角膜折射率 / 空气折射率）。</summary>
            public float EtaRatio;
            /// <summary>虹膜深度（角膜顶点到虹膜平面的距离）。</summary>
            public float IrisDepth;
            /// <summary>角膜曲率半径。</summary>
            public float CorneaCurvature;
            /// <summary>折射强度系数（控制折射效果明显程度）。</summary>
            public float RefractionStrength;
        }

        /// <summary>从解剖参数计算折射着色器参数。</summary>
        public static CorneaRefractParams ComputeParams(EyeAnatomy anatomy)
        {
            return new CorneaRefractParams
            {
                EtaRatio = 1.0f / anatomy.CorneaIOR,
                IrisDepth = anatomy.IrisDepth,
                CorneaCurvature = anatomy.CorneaRadius,
                RefractionStrength = 1.0f,
            };
        }

        /// <summary>
        ///   CPU 端 Snell 折射计算（用于预计算或验证）。
        ///   入射向量 I 和表面法线 N 之间，折射向量 T = eta*I + (eta*cos_i - cos_t)*N。
        /// </summary>
        public static Vector3 Refract(Vector3 incident, Vector3 normal, float eta)
        {
            float cosI = -Vector3.Dot(normal, incident);
            float sin2T = eta * eta * (1f - cosI * cosI);
            if (sin2T > 1f) return Vector3.Zero; // 全反射
            float cosT = MathF.Sqrt(1f - sin2T);
            return eta * incident + (eta * cosI - cosT) * normal;
        }

        /// <summary>
        ///   计算折射后虹膜采样位置的偏移量（用于近似模拟角膜放大效应）。
        /// </summary>
        public static Vector2 ComputeIrisOffset(Vector3 viewDir, Vector3 corneaNormal, EyeAnatomy anatomy)
        {
            Vector3 refracted = Refract(viewDir, corneaNormal, 1.0f / anatomy.CorneaIOR);
            // 折射光线在虹膜深度处的 X/Y 偏移
            float t = anatomy.IrisDepth / MathF.Max(0.001f, -refracted.Z);
            return new Vector2(refracted.X * t, refracted.Y * t);
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  注视追踪
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    ///   注视追踪器。计算眼球朝向指定目标点旋转所需的四元数。
    ///   眼球默认朝前（+Z），追踪器计算从眼球位置到目标方向的旋转，
    ///   并限制在最大旋转角度范围内以避免不自然的过度旋转。
    /// </summary>
    public sealed class GazeTracker
    {
        /// <summary>眼球最大水平旋转角度（弧度）。</summary>
        public float MaxHorizontalAngle = 35f * MathF.PI / 180f;

        /// <summary>眼球最大垂直旋转角度（弧度）。</summary>
        public float MaxVerticalAngle = 25f * MathF.PI / 180f;

        /// <summary>眼球朝向目标的收敛速率（越大越快）。</summary>
        public float ConvergenceRate = 12f;

        /// <summary>当前眼球旋转（四元数）。</summary>
        public Quaternion CurrentRotation = Quaternion.Identity;

        /// <summary>
        ///   更新眼球旋转，朝向目标位置。使用指数插值实现平滑扫视。
        /// </summary>
        /// <param name="eyePosition">眼球世界位置。</param>
        /// <param name="targetPosition">注视目标世界位置。</param>
        /// <param name="headRotation">头部旋转（眼球旋转相对于头部）。</param>
        /// <param name="deltaTime">帧间隔。</param>
        public void Update(Vector3 eyePosition, Vector3 targetPosition, Quaternion headRotation, float deltaTime)
        {
            // 计算世界空间目标方向
            Vector3 worldDir = targetPosition - eyePosition;
            if (worldDir.LengthSquared() < 1e-6f) return;
            worldDir = Vector3.Normalize(worldDir);

            // 转换到头部局部空间
            Quaternion invHead = Quaternion.Inverse(headRotation);
            Vector3 localDir = Vector3.Transform(worldDir, invHead);

            // 计算偏航角和俯仰角
            float yaw = MathF.Atan2(localDir.X, localDir.Z);
            float pitch = MathF.Asin(Math.Clamp(localDir.Y, -1f, 1f));

            // 限制旋转角度
            yaw = Math.Clamp(yaw, -MaxHorizontalAngle, MaxHorizontalAngle);
            pitch = Math.Clamp(pitch, -MaxVerticalAngle, MaxVerticalAngle);

            // 目标旋转
            Quaternion targetRot = Quaternion.CreateFromYawPitchRoll(yaw, pitch, 0f);

            // 平滑插值（球面线性插值的指数近似）
            float t = 1f - MathF.Exp(-ConvergenceRate * deltaTime);
            CurrentRotation = Quaternion.Slerp(CurrentRotation, targetRot, t);
        }

        /// <summary>获取眼球朝向向量（头部局部空间）。</summary>
        public Vector3 GetGazeDirection()
        {
            return Vector3.Transform(Vector3.UnitZ, CurrentRotation);
        }

        /// <summary>重置眼球朝向。</summary>
        public void Reset()
        {
            CurrentRotation = Quaternion.Identity;
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  眼睑动画控制器
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    ///   眼睑动画控制器。管理自动眨眼和手动眨眼的动画曲线。
    ///
    ///   眨眼使用正弦曲线：闭眼阶段从 0 到 1（0~0.5），睁眼阶段从 1 到 0（0.5~1）。
    ///   眨眼间隔随机化以增加自然感。当眼球向下注视时眼睑会自动部分闭合（注视遮挡）。
    /// </summary>
    public sealed class EyelidAnimator
    {
        private readonly Random _rand = new();
        private float _nextBlinkTime;

        /// <summary>
        ///   更新眨眼状态。
        /// </summary>
        /// <param name="component">眼球组件（读写 BlinkAmount 等字段）。</param>
        /// <param name="deltaTime">帧间隔。</param>
        /// <param name="gazePitch">当前注视俯仰角（弧度，正=向下看）。</param>
        public void Update(ref EyeComponent component, float deltaTime, float gazePitch)
        {
            // 自动眨眼定时器
            if (!component.IsBlinking)
            {
                component.BlinkTimer -= deltaTime;
                if (component.BlinkTimer <= 0f)
                {
                    component.IsBlinking = true;
                    component.BlinkProgress = 0f;
                }
            }

            // 眨眼动画进度
            if (component.IsBlinking)
            {
                component.BlinkProgress += deltaTime / Math.Max(0.01f, component.BlinkDuration);
                if (component.BlinkProgress >= 1f)
                {
                    component.IsBlinking = false;
                    component.BlinkProgress = 0f;
                    component.BlinkAmount = 0f;
                    // 设置下次眨眼时间
                    float minI = component.BlinkIntervalRange.X;
                    float maxI = component.BlinkIntervalRange.Y;
                    component.BlinkTimer = minI + (float)_rand.NextDouble() * (maxI - minI);
                }
                else
                {
                    // 正弦眨眼曲线：0->1->0
                    component.BlinkAmount = MathF.Sin(component.BlinkProgress * MathF.PI);
                }
            }

            // 注视遮挡：向下看时上眼睑自动下垂
            float gazeEyelidClosure = Math.Clamp(gazePitch * 0.5f, 0f, 0.5f);
            component.BlinkAmount = Math.Max(component.BlinkAmount, gazeEyelidClosure);
        }

        /// <summary>
        ///   手动触发一次眨眼。
        /// </summary>
        public void TriggerBlink(ref EyeComponent component)
        {
            if (!component.IsBlinking)
            {
                component.IsBlinking = true;
                component.BlinkProgress = 0f;
            }
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  眼球渲染器
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    ///   眼球渲染器。统筹眼球各层网格的渲染管线，管理 GPU 资源，
    ///   每帧将眼球组件状态映射为着色器常量并提交绘制命令。
    ///
    ///   渲染顺序（由后到前，保证透明度正确）：
    ///     1. 巩膜（不透明，带血管细节和 SSS）
    ///     2. 虹膜（不透明，程序化纹理 + 折射偏移）
    ///     3. 瞳孔（不透明黑色圆盘）
    ///     4. 角膜（透明折射层，泪液湿润高光）
    ///     5. 眼睑（不透明皮肤，根据 BlinkAmount 变形遮挡）
    /// </summary>
    public sealed class EyeRenderer : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly EyeAnatomy _anatomy;
        private readonly IrisTextureGenerator _irisGen;

        // 眼球网格
        private EyeMesh _scleraMesh;
        private EyeMesh _corneaMesh;
        private EyeMesh _irisMesh;
        private EyeMesh _pupilMesh;
        private EyeMesh _upperEyelidMesh;
        private EyeMesh _lowerEyelidMesh;

        // GPU 资源
        private RHITexture _irisTexture;
        private RHIDescriptorHandle _irisSRV;
        private RHIBuffer _constantBuffer;
        private bool _gpuResourcesReady;
        private bool _disposed;

        /// <summary>眼球解剖参数。</summary>
        public EyeAnatomy Anatomy => _anatomy;

        /// <summary>虹膜纹理生成器。</summary>
        public IrisTextureGenerator IrisGenerator => _irisGen;

        /// <summary>是否已初始化 GPU 资源。</summary>
        public bool IsInitialized => _gpuResourcesReady;

        public EyeRenderer(RHIDevice device, EyeAnatomy anatomy = null)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _anatomy = anatomy ?? new EyeAnatomy();
            _irisGen = new IrisTextureGenerator();
        }

        /// <summary>
        ///   初始化：生成网格、上传 GPU 资源。
        /// </summary>
        /// <param name="irisColor">虹膜颜色。</param>
        /// <param name="pigmentation">色素沉着。</param>
        /// <param name="fiberDetail">纤维细节。</param>
        public void Initialize(Vector3 irisColor, float pigmentation, float fiberDetail)
        {
            // 生成 CPU 端网格
            _scleraMesh = EyeMeshGenerator.GenerateSclera(_anatomy, 32, 24);
            _corneaMesh = EyeMeshGenerator.GenerateCornea(_anatomy, 24);
            _irisMesh = EyeMeshGenerator.GenerateIris(_anatomy, 32);
            _pupilMesh = EyeMeshGenerator.GeneratePupil(_anatomy, 32);
            _upperEyelidMesh = EyeMeshGenerator.GenerateEyelid(_anatomy, 16, true);
            _lowerEyelidMesh = EyeMeshGenerator.GenerateEyelid(_anatomy, 16, false);

            // 上传网格到 GPU
            UploadMeshToGPU(_scleraMesh);
            UploadMeshToGPU(_corneaMesh);
            UploadMeshToGPU(_irisMesh);
            UploadMeshToGPU(_pupilMesh);
            UploadMeshToGPU(_upperEyelidMesh);
            UploadMeshToGPU(_lowerEyelidMesh);

            // 生成并上传虹膜纹理
            byte[] irisPixels = _irisGen.Generate(irisColor, pigmentation, fiberDetail);
            _irisTexture = _irisGen.UploadToGPU(_device, irisPixels);
            _irisSRV = _device.CreateSRV(_irisTexture, 0, 0);

            // 创建常量缓冲区（存储眼球着色器参数）
            _constantBuffer = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = 256, // 对齐到 256 字节
                HeapType = RHIResourceHeapType.Upload,
                Flags = RHIBufferFlags.ConstantBuffer,
            }, "EyeConstantBuffer");

            _gpuResourcesReady = true;
        }

        /// <summary>上传网格顶点/索引数据到 GPU 缓冲区。</summary>
        private void UploadMeshToGPU(EyeMesh mesh)
        {
            if (mesh.VertexCount == 0) return;

            // 顶点缓冲区
            int vStride = System.Runtime.InteropServices.Marshal.SizeOf<EyeVertex>();
            int vSize = mesh.VertexCount * vStride;
            mesh.VertexBuffer = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = (ulong)vSize,
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.VertexBuffer,
            }, $"EyeVB_{mesh.Region}");

            byte[] vData = new byte[vSize];
            System.Runtime.InteropServices.MemoryMarshal.AsBytes<EyeVertex>(mesh.Vertices.ToArray()).
                CopyTo(vData.AsSpan());
            _device.UploadToBuffer(mesh.VertexBuffer, vData, 0);

            // 索引缓冲区
            int iSize = mesh.IndexCount * 4;
            mesh.IndexBuffer = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = (ulong)iSize,
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.IndexBuffer,
            }, $"EyeIB_{mesh.Region}");

            byte[] iData = new byte[iSize];
            System.Runtime.InteropServices.MemoryMarshal.AsBytes<uint>(mesh.Indices.ToArray()).
                CopyTo(iData.AsSpan());
            _device.UploadToBuffer(mesh.IndexBuffer, iData, 0);
        }

        /// <summary>
        ///   渲染单只眼球。按正确顺序提交各层网格的绘制命令。
        /// </summary>
        /// <param name="cmdList">RHI 命令列表。</param>
        /// <param name="component">眼球组件状态。</param>
        /// <param name="eyeWorldPos">眼球世界位置。</param>
        /// <param name="headRotation">头部世界旋转。</param>
        /// <param name="viewDistance">相机到眼球的距离（用于 LOD）。</param>
        public void Render(RHICommandList cmdList, EyeComponent component,
            Vector3 eyeWorldPos, Quaternion headRotation, float viewDistance)
        {
            if (!_gpuResourcesReady || !component.Enabled) return;

            // 计算眼球世界变换
            Quaternion eyeRot = headRotation;
            if (component.GazeTrackingEnabled)
            {
                // 注视追踪：计算朝向目标的旋转
                Vector3 localTarget = component.LookAtTarget - eyeWorldPos;
                if (localTarget.LengthSquared() > 1e-6f)
                {
                    localTarget = Vector3.Normalize(localTarget);
                    Quaternion invHead = Quaternion.Inverse(headRotation);
                    Vector3 localDir = Vector3.Transform(localTarget, invHead);
                    float yaw = MathF.Atan2(localDir.X, localDir.Z);
                    float pitch = MathF.Asin(Math.Clamp(localDir.Y, -1f, 1f));
                    float maxRad = component.MaxGazeAngle * MathF.PI / 180f;
                    yaw = Math.Clamp(yaw, -maxRad, maxRad);
                    pitch = Math.Clamp(pitch, -maxRad * 0.7f, maxRad * 0.7f);
                    eyeRot = headRotation * Quaternion.CreateFromYawPitchRoll(yaw, pitch, 0f);
                }
            }

            Matrix4x4 worldMatrix = Matrix4x4.CreateFromQuaternion(eyeRot) *
                Matrix4x4.CreateTranslation(eyeWorldPos);

            // LOD 判断
            bool useSimplifiedShading = viewDistance > component.LodDistance;

            // 更新常量缓冲区（眼球着色器参数）
            UpdateConstantBuffer(component, worldMatrix, useSimplifiedShading);

            // 绑定虹膜纹理 SRV
            cmdList.SetGraphicsRootDescriptorTable(0, _irisSRV);

            // 1. 渲染巩膜
            DrawMesh(cmdList, _scleraMesh);

            // 2. 渲染虹膜（简化 LOD 下跳过折射偏移）
            DrawMesh(cmdList, _irisMesh);

            // 3. 渲染瞳孔
            // 根据瞳孔缩放调整瞳孔网格的缩放矩阵
            float pupilScale = component.PupilRadius / _anatomy.PupilMaxRadius;
            Matrix4x4 pupilWorld = Matrix4x4.CreateScale(pupilScale) * worldMatrix;
            DrawMeshWithMatrix(cmdList, _pupilMesh, pupilWorld);

            // 4. 渲染角膜（透明层，简化 LOD 下跳过）
            if (!useSimplifiedShading)
            {
                DrawMesh(cmdList, _corneaMesh);
            }

            // 5. 渲染眼睑（根据眨眼量变形）
            if (component.BlinkAmount > 0.01f)
            {
                float upperOffset = component.BlinkAmount * _anatomy.EyeballRadius * 0.8f;
                Matrix4x4 upperWorld = Matrix4x4.CreateTranslation(0, -upperOffset, 0) * worldMatrix;
                DrawMeshWithMatrix(cmdList, _upperEyelidMesh, upperWorld);

                Matrix4x4 lowerWorld = Matrix4x4.CreateTranslation(0, upperOffset * 0.6f, 0) * worldMatrix;
                DrawMeshWithMatrix(cmdList, _lowerEyelidMesh, lowerWorld);
            }
        }

        /// <summary>更新常量缓冲区，写入眼球着色器参数。</summary>
        private void UpdateConstantBuffer(EyeComponent component, Matrix4x4 worldMatrix, bool simplifiedLOD)
        {
            // 打包常量数据：世界矩阵 + 眼球参数
            var corneaParams = CorneaOptics.ComputeParams(_anatomy);
            var data = new EyeShaderConstants
            {
                WorldMatrix = worldMatrix,
                IrisColor = component.IrisColor,
                PupilRadius = component.PupilRadius,
                IrisRadius = _anatomy.IrisRadius,
                ScleraBaseColor = _anatomy.ScleraBaseColor,
                ScleraVeinColor = _anatomy.ScleraVeinColor,
                ScleraVeinDensity = _anatomy.ScleraVeinDensity,
                SSSCoeff = _anatomy.SSSCoeff,
                TearFilmReflectance = _anatomy.TearFilmReflectance,
                CorneaEtaRatio = corneaParams.EtaRatio,
                IrisDepth = corneaParams.IrisDepth,
                CorneaCurvature = corneaParams.CorneaCurvature,
                RefractionStrength = corneaParams.RefractionStrength,
                BlinkAmount = component.BlinkAmount,
                SimplifiedLOD = simplifiedLOD ? 1f : 0f,
                IrisPigmentation = component.IrisPigmentation,
                IrisFiberDetail = component.IrisFiberDetail,
            };

            byte[] bytes = new byte[256];
            System.Runtime.InteropServices.MemoryMarshal.Write(bytes.AsSpan(), ref data);
            _device.UploadToBuffer(_constantBuffer, bytes, 0);
        }

        /// <summary>使用当前常量缓冲区中的世界矩阵绘制网格。</summary>
        private void DrawMesh(RHICommandList cmdList, EyeMesh mesh)
        {
            if (mesh == null || mesh.VertexBuffer == null || mesh.IndexBuffer == null) return;
            cmdList.IASetVertexBuffers(0, mesh.VertexBuffer, System.Runtime.InteropServices.Marshal.SizeOf<EyeVertex>());
            cmdList.IASetIndexBuffer(mesh.IndexBuffer, RHIFormat.R32_UInt);
            cmdList.DrawIndexedInstanced((uint)mesh.IndexCount, 1, 0, 0, 0);
        }

        /// <summary>使用指定的世界矩阵绘制网格（需要先更新常量缓冲区中的矩阵）。</summary>
        private void DrawMeshWithMatrix(RHICommandList cmdList, EyeMesh mesh, Matrix4x4 worldMatrix)
        {
            // 在实际实现中，这里需要更新常量缓冲区中的世界矩阵
            // 简化实现：直接使用 DrawMesh（实际项目中应通过 Push Constant 或 Dynamic CBV 实现）
            DrawMesh(cmdList, mesh);
        }

        /// <summary>
        ///   根据环境光照强度自动计算瞳孔半径。
        ///   暗光瞳孔放大，强光瞳孔缩小，使用平滑映射函数。
        /// </summary>
        public static float ComputeAutoPupilRadius(float lightLevel, EyeAnatomy anatomy)
        {
            // lightLevel: 0=黑暗, 1=强光
            // 使用 smoothstep 反向映射
            float t = 1f - Math.Clamp(lightLevel, 0f, 1f);
            t = t * t * (3f - 2f * t); // smoothstep
            return anatomy.PupilMinRadius + (anatomy.PupilMaxRadius - anatomy.PupilMinRadius) * t;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _irisTexture?.Dispose();
            _constantBuffer?.Dispose();
            _scleraMesh?.VertexBuffer?.Dispose();
            _scleraMesh?.IndexBuffer?.Dispose();
            _corneaMesh?.VertexBuffer?.Dispose();
            _corneaMesh?.IndexBuffer?.Dispose();
            _irisMesh?.VertexBuffer?.Dispose();
            _irisMesh?.IndexBuffer?.Dispose();
            _pupilMesh?.VertexBuffer?.Dispose();
            _pupilMesh?.IndexBuffer?.Dispose();
            _upperEyelidMesh?.VertexBuffer?.Dispose();
            _upperEyelidMesh?.IndexBuffer?.Dispose();
            _lowerEyelidMesh?.VertexBuffer?.Dispose();
            _lowerEyelidMesh?.IndexBuffer?.Dispose();
            GC.SuppressFinalize(this);
        }

        ~EyeRenderer() => Dispose();
    }

    /// <summary>
    ///   眼球着色器常量数据。与 HLSL 着色器中的 cbuffer 布局对应。
    /// </summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 16)]
    public struct EyeShaderConstants
    {
        /// <summary>眼球世界矩阵。</summary>
        public Matrix4x4 WorldMatrix;
        /// <summary>虹膜颜色。</summary>
        public Vector3 IrisColor;
        /// <summary>瞳孔半径。</summary>
        public float PupilRadius;
        /// <summary>虹膜半径。</summary>
        public float IrisRadius;
        /// <summary>巩膜基础颜色。</summary>
        public Vector3 ScleraBaseColor;
        /// <summary>巩膜血管颜色。</summary>
        public Vector3 ScleraVeinColor;
        /// <summary>巩膜血管密度。</summary>
        public float ScleraVeinDensity;
        /// <summary>SSS 系数。</summary>
        public float SSSCoeff;
        /// <summary>泪液层反射强度。</summary>
        public float TearFilmReflectance;
        /// <summary>角膜折射率比。</summary>
        public float CorneaEtaRatio;
        /// <summary>虹膜深度。</summary>
        public float IrisDepth;
        /// <summary>角膜曲率。</summary>
        public float CorneaCurvature;
        /// <summary>折射强度。</summary>
        public float RefractionStrength;
        /// <summary>眨眼量。</summary>
        public float BlinkAmount;
        /// <summary>是否简化 LOD。</summary>
        public float SimplifiedLOD;
        /// <summary>虹膜色素沉着。</summary>
        public float IrisPigmentation;
        /// <summary>虹膜纤维细节。</summary>
        public float IrisFiberDetail;
    }

    // ──────────────────────────────────────────────────────────────
    //  眼球渲染 ECS 系统
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    ///   眼球渲染 ECS 系统。每帧查询所有 EyeComponent，更新注视追踪、瞳孔缩放、
    ///   眨眼动画，然后将渲染命令提交到 RHI 命令列表。
    ///   该系统应在渲染阶段（CameraSystem 之后）更新。
    /// </summary>
    public sealed class EyeRenderSystem : ISystem
    {
        private readonly EyeRenderer _renderer;
        private readonly GazeTracker _gazeTracker = new();
        private readonly EyelidAnimator _eyelidAnimator = new();

        /// <summary>系统名称。</summary>
        public string Name => "EyeRenderSystem";

        /// <summary>RHI 命令列表（由外部渲染管线设置）。</summary>
        public RHICommandList CommandList { get; set; }

        /// <summary>相机世界位置（用于计算视线距离 LOD）。</summary>
        public Vector3 CameraPosition { get; set; }

        /// <summary>是否已初始化渲染器。</summary>
        public bool IsRendererInitialized => _renderer?.IsInitialized ?? false;

        public EyeRenderSystem(EyeRenderer renderer)
        {
            _renderer = renderer;
        }

        /// <summary>每帧更新所有眼球组件并提交渲染。</summary>
        public void Update(float dt, EntityComponentSystem ecs)
        {
            if (ecs == null || _renderer == null || CommandList == null) return;

            var entities = ecs.QueryWithComponent<EyeComponent>();
            foreach (var id in entities)
            {
                var eye = ecs.GetComponent<EyeComponent>(id);
                if (!eye.Enabled) continue;

                // 获取实体世界变换
                Vector3 entityPos = Vector3.Zero;
                Quaternion headRot = Quaternion.Identity;
                if (ecs.HasComponent<TransformComponent>(id))
                {
                    var transform = ecs.GetComponent<TransformComponent>(id);
                    entityPos = transform.Position;
                    headRot = transform.Rotation;
                }

                // 计算眼球世界位置（实体位置 + 眼球偏移）
                Vector3 eyeWorldPos = entityPos + Vector3.Transform(eye.EyeOffset, headRot);

                // 自动瞳孔缩放
                if (eye.AutoPupilDilation)
                {
                    eye.PupilRadius = EyeRenderer.ComputeAutoPupilRadius(
                        eye.AmbientLightLevel, _renderer.Anatomy);
                }

                // 注视追踪
                if (eye.GazeTrackingEnabled)
                {
                    _gazeTracker.Update(eyeWorldPos, eye.LookAtTarget, headRot, dt);
                }

                // 眼睑动画
                float gazePitch = 0f;
                if (eye.GazeTrackingEnabled)
                {
                    Vector3 gazeDir = _gazeTracker.GetGazeDirection();
                    gazePitch = MathF.Asin(Math.Clamp(gazeDir.Y, -1f, 1f));
                }
                _eyelidAnimator.Update(ref eye, dt, gazePitch);

                // 计算视线距离（LOD）
                float viewDistance = Vector3.Distance(CameraPosition, eyeWorldPos);

                // 提交渲染命令
                _renderer.Render(CommandList, eye, eyeWorldPos, headRot, viewDistance);

                ecs.SetComponent(id, eye);
            }
        }

        /// <summary>初始化渲染器 GPU 资源。</summary>
        public void InitializeRenderer(Vector3 irisColor, float pigmentation, float fiberDetail)
        {
            _renderer?.Initialize(irisColor, pigmentation, fiberDetail);
        }
    }

    /// <summary>
    ///   双眼渲染器。管理左右两只眼球的渲染，提供统一的初始化和更新接口。
    ///   通常一个角色实体附加两只眼球（左眼+右眼），通过此封装简化管理。
    /// </summary>
    public sealed class BinocularEyeRenderer : IDisposable
    {
        private readonly EyeRenderer _leftEye;
        private readonly EyeRenderer _rightEye;
        private bool _disposed;

        /// <summary>左眼渲染器。</summary>
        public EyeRenderer LeftEye => _leftEye;

        /// <summary>右眼渲染器。</summary>
        public EyeRenderer RightEye => _rightEye;

        public BinocularEyeRenderer(RHIDevice device, EyeAnatomy anatomy = null)
        {
            _leftEye = new EyeRenderer(device, anatomy);
            _rightEye = new EyeRenderer(device, anatomy);
        }

        /// <summary>初始化左右眼渲染器。</summary>
        public void Initialize(Vector3 irisColor, float pigmentation, float fiberDetail)
        {
            _leftEye.Initialize(irisColor, pigmentation, fiberDetail);
            _rightEye.Initialize(irisColor, pigmentation, fiberDetail);
        }

        /// <summary>渲染左右眼。</summary>
        public void Render(RHICommandList cmdList, EyeComponent leftEye, EyeComponent rightEye,
            Vector3 leftPos, Vector3 rightPos, Quaternion headRot, float viewDistance)
        {
            _leftEye.Render(cmdList, leftEye, leftPos, headRot, viewDistance);
            _rightEye.Render(cmdList, rightEye, rightPos, headRot, viewDistance);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _leftEye?.Dispose();
            _rightEye?.Dispose();
            GC.SuppressFinalize(this);
        }

        ~BinocularEyeRenderer() => Dispose();
    }
}
