using System; using System.Collections.Generic; using System.Numerics; using System.Runtime.InteropServices; namespace YamaEngine.Rendering;

public enum GeometryBufferTexture { Albedo = 0, Normal, RoughnessMetallic, Depth, Velocity, Emission, Specular, Subsurface, Stencil, Count }


public sealed class GeometryBuffer
{
    private readonly GeometryBufferConfig _config;
    private readonly IntPtr[] _textures = new IntPtr[(int)GeometryBufferTexture.Count];

    public GeometryBuffer(GeometryBufferConfig config) { _config = config; }

    public void Clear()
    {
        for (int i = 0; i < _textures.Length; i++) _textures[i] = IntPtr.Zero;
    }

    public void Resolve(in FrameResourcePool pool)
    {
    }

    public void Dispose()
    {
        for (int i = 0; i < _textures.Length; i++)
            if (_textures[i] != IntPtr.Zero) Marshal.FreeHGlobal(_textures[i]);
    }
}

public struct PunctualLight
{
    public Vector3 Position, Direction, Color; public float Intensity, Range;
    public float SpotAngle, SpotBlur; public int LightType;
    public Matrix4x4 ViewProjection; public int ShadowMapIndex;
}

public struct LightGridCell
{
    public int Offset, Count;
}

public sealed class ClusteredLightCulling
{
    private int _gridSizeX = 16, _gridSizeY = 9, _gridSizeZ = 32;
    private int _maxLightsPerCell = 64;

    public (int x, int y, int z) GridSize => (_gridSizeX, _gridSizeY, _gridSizeZ);
    public void SetGrid(int x, int y, int z) { _gridSizeX = x; _gridSizeY = y; _gridSizeZ = z; }

    public int[] CullLights(PunctualLight[] lights, Matrix4x4 view, Matrix4x4 proj, int screenW, int screenH, float near, float far)
    {
        int totalCells = _gridSizeX * _gridSizeY * _gridSizeZ;
        int[] lightIndices = new int[lights.Length * _maxLightsPerCell];
        var cellCounts = new int[totalCells];

        float zScale = _gridSizeZ / MathF.Log(far / near + 1f);

        for (int i = 0; i < lights.Length; i++)
        {
            Vector3 viewPos = Vector3.Transform(lights[i].Position, view);
            float radius = lights[i].Range;

            int minZ = Math.Max(0, (int)(MathF.Log(MathF.Max(viewPos.Z - radius, near) / near + 1f) * zScale));
            int maxZ = Math.Min(_gridSizeZ - 1, (int)(MathF.Log(MathF.Min(viewPos.Z + radius, far) / near + 1f) * zScale));

            for (int z = minZ; z <= maxZ; z++)
            {
                for (int y = 0; y < _gridSizeY; y++)
                {
                    for (int x = 0; x < _gridSizeX; x++)
                    {
                        int cell = z * _gridSizeY * _gridSizeX + y * _gridSizeX + x;
                        if (cellCounts[cell] < _maxLightsPerCell)
                            lightIndices[cell * _maxLightsPerCell + cellCounts[cell]++] = i;
                    }
                }
            }
        }

        var result = new int[totalCells * (_maxLightsPerCell + 1)];
        for (int c = 0; c < totalCells; c++)
        {
            result[c * (_maxLightsPerCell + 1)] = cellCounts[c];
            Array.Copy(lightIndices, c * _maxLightsPerCell, result, c * (_maxLightsPerCell + 1) + 1, cellCounts[c]);
        }
        return result;
    }
}

public struct MaterialShaderParams
{
    public Vector4 BaseColor = new(1, 1, 1, 1); public Vector3 Emissive = Vector3.Zero;
    public float Metallic, Roughness, Specular, AmbientOcclusion, Opacity, NormalStrength;
    public float Subsurface, ClearCoat, ClearCoatRoughness, Anisotropy;
    public int AlbedoMap, NormalMap, RoughnessMap, MetallicMap, AOMap, EmissiveMap, OpacityMap;
    public MaterialShaderParams() { Metallic = 0; Roughness = 0.5f; Specular = 0.5f; AmbientOcclusion = 1; Opacity = 1; NormalStrength = 1; }
}

public static class ShaderEvaluator
{
    public static Vector3 EvaluateLighting(Vector3 normal, Vector3 viewDir, Vector3 lightDir, Vector3 lightColor, float intensity, MaterialShaderParams mat)
    {
        float ndotl = Math.Clamp(Vector3.Dot(normal, lightDir), 0, 1);
        Vector3 diff = lightColor * ndotl * intensity * (1f - mat.Metallic);

        Vector3 halfVec = Vector3.Normalize(lightDir + viewDir);
        float ndoth = Math.Clamp(Vector3.Dot(normal, halfVec), 0, 1);
        float roughness = MathF.Max(mat.Roughness, 0.01f);
        float alpha = roughness * roughness;
        float ndoth2 = ndoth * ndoth;
        float denom = ndoth2 * (alpha * alpha - 1) + 1;
        float ggx = alpha * alpha / (MathF.PI * denom * denom);
        Vector3 spec = lightColor * ggx * intensity * MathF.PI;

        return diff * new Vector3(mat.BaseColor.X, mat.BaseColor.Y, mat.BaseColor.Z) + spec;
    }

    public static Vector3 EvaluateIBL(Vector3 normal, Vector3 viewDir, MaterialShaderParams mat, Vector3 irradiance, Vector3 prefiltered, float brdfLUT)
    {
        Vector3 kS = FresnelSchlickRoughness(Math.Clamp(Vector3.Dot(normal, viewDir), 0, 1), mat.BaseColor, mat.Roughness);
        Vector3 kD = (Vector3.One - kS) * (1f - mat.Metallic);
        Vector3 diffuse = irradiance * new Vector3(mat.BaseColor.X, mat.BaseColor.Y, mat.BaseColor.Z);
        Vector3 specular = prefiltered * (kS * brdfLUT);
        return diffuse + specular;
    }

    private static Vector3 FresnelSchlickRoughness(float cosTheta, Vector4 F0, float roughness)
    {
        float r = MathF.Max(roughness, 0.01f);
        return F0.AsVec3() + (Vector3.Max(new Vector3(1f - r), F0.AsVec3()) - F0.AsVec3()) * MathF.Pow(1f - cosTheta, 5f);
    }

    public static float GGXShadowing(float ndotl, float ndotv, float roughness)
    {
        float a = roughness * roughness;
        float ggxL = 2f * ndotl / (ndotl + MathF.Sqrt(a * a + (1f - a * a) * ndotl * ndotl));
        float ggxV = 2f * ndotv / (ndotv + MathF.Sqrt(a * a + (1f - a * a) * ndotv * ndotv));
        return ggxL * ggxV;
    }
}

public static class ShaderLibrary
{
    public static readonly string[] PostProcessVariants = {
        "None", "Linear", "ACES", "Reinhard", "Uchimura", "Filmic"
    };

    public static readonly string[] AntiAliasingMethods = {
        "None", "FXAA", "SMAA_1X", "TAA", "MSAA_2X", "MSAA_4X", "MSAA_8X"
    };

    public static readonly string[] ShadowQualityNames = {
        "Off", "Low(256)", "Med(512)", "High(1024)", "Ultra(2048)", "Cinema(4096)"
    };

    public static readonly string[] ReflectionQualities = {
        "Off", "Low(16steps)", "Med(32steps)", "High(64steps)", "Ultra(128steps)"
    };

    public static float GaussianWeight(float x, float sigma)
    {
        return (1f / (MathF.Sqrt(2f * MathF.PI) * sigma)) * MathF.Exp(-(x * x) / (2f * sigma * sigma));
    }

    public static float[] GenerateGaussianKernel(int radius, float sigma)
    {
        int size = radius * 2 + 1; var kernel = new float[size]; float sum = 0;
        for (int i = 0; i < size; i++) { int x = i - radius; kernel[i] = GaussianWeight(x, sigma); sum += kernel[i]; }
        for (int i = 0; i < size; i++) kernel[i] /= sum;
        return kernel;
    }

    public static float[] GeneratePoissonDisc(int count, int seed = 42)
    {
        var rng = new Random(seed); var samples = new float[count * 2];
        for (int i = 0; i < count; i++)
            { float a = (float)rng.NextDouble() * MathF.PI * 2; float r = MathF.Sqrt((float)rng.NextDouble()); samples[i*2] = MathF.Cos(a)*r; samples[i*2+1] = MathF.Sin(a)*r; }
        return samples;
    }

    public static float[] HaltonSequence(int index, int base_b)
    {
        float f = 1; float result = 0; int i = index;
        while (i > 0) { f /= base_b; result += f * (i % base_b); i /= base_b; }
        return new float[] { HaltonSequence2D(index, 2), HaltonSequence2D(index, 3) };
    }

    private static float HaltonSequence2D(int index, int base_b)
    {
        float f = 1; float result = 0; int i = index;
        while (i > 0) { f /= base_b; result += f * (i % base_b); i /= base_b; }
        return result;
    }

    public static Vector4 RGBMDecode(Vector4 rgbm, float range = 8f)
    {
        return new Vector4(rgbm.X * rgbm.W * range, rgbm.Y * rgbm.W * range, rgbm.Z * rgbm.W * range, 1);
    }

    public static Vector4 RGBMEncode(Vector3 color, float range = 8f)
    {
        float maxComp = MathF.Max(color.X, MathF.Max(color.Y, color.Z));
        float m = Math.Clamp(maxComp / range, 0, 1);
        float scale = m * range;
        return new Vector4(m > 0 ? color.X / scale : 0, m > 0 ? color.Y / scale : 0, m > 0 ? color.Z / scale : 0, m);
    }

    public static float Luminance(Vector3 color) => color.X * 0.2126f + color.Y * 0.7152f + color.Z * 0.0722f;

    public static float SmoothStep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0, 1);
        return t * t * (3 - 2 * t);
    }

    public static float LinearStep(float edge0, float edge1, float x)
    {
        return Math.Clamp((x - edge0) / (edge1 - edge0), 0, 1);
    }

    public static Vector3 WorldToScreen(Vector3 worldPos, Matrix4x4 viewProj, int screenW, int screenH)
    {
        Vector4 clip = Vector4.Transform(new Vector4(worldPos, 1), viewProj);
        if (MathF.Abs(clip.W) < 1e-10f) return Vector3.Zero;
        float ndcX = clip.X / clip.W; float ndcY = clip.Y / clip.W;
        return new Vector3((ndcX * 0.5f + 0.5f) * screenW, (0.5f - ndcY * 0.5f) * screenH, clip.Z / clip.W);
    }
}

public static class GeometryHelpers
{
    public static Vector3[] GenerateSphere(float radius, int segments, int rings)
    {
        var verts = new List<Vector3>();
        for (int r = 0; r <= rings; r++)
        {
            float theta = MathF.PI * r / rings;
            for (int s = 0; s <= segments; s++)
            {
                float phi = 2 * MathF.PI * s / segments;
                verts.Add(new Vector3(radius * MathF.Sin(theta) * MathF.Cos(phi), radius * MathF.Cos(theta), radius * MathF.Sin(theta) * MathF.Sin(phi)));
            }
        }
        return verts.ToArray();
    }

    public static Vector3[] GenerateBox(float sx, float sy, float sz)
    {
        float hx = sx * 0.5f, hy = sy * 0.5f, hz = sz * 0.5f;
        return new[] {
            new Vector3(-hx,-hy,-hz), new Vector3(hx,-hy,-hz), new Vector3(hx,hy,-hz), new Vector3(-hx,hy,-hz),
            new Vector3(-hx,-hy,hz), new Vector3(hx,-hy,hz), new Vector3(hx,hy,hz), new Vector3(-hx,hy,hz) };
    }

    public static int[] BoxIndices = {
        0,1,2, 0,2,3, 1,5,6, 1,6,2, 5,4,7, 5,7,6, 4,0,3, 4,3,7, 3,2,6, 3,6,7, 4,5,1, 4,1,0 };

    public static Vector3[] GenerateCapsule(float radius, float height, int segments, int rings)
    {
        var verts = new List<Vector3>();
        float halfH = height * 0.5f;
        int halfRings = rings / 2;

        for (int r = 0; r <= halfRings; r++)
        {
            float theta = MathF.PI * 0.5f * r / halfRings;
            for (int s = 0; s <= segments; s++)
            {
                float phi = 2 * MathF.PI * s / segments;
                Vector3 dir = new(MathF.Sin(theta) * MathF.Cos(phi), MathF.Cos(theta), MathF.Sin(theta) * MathF.Sin(phi));
                verts.Add(new Vector3(radius * dir.X, halfH + radius * dir.Y, radius * dir.Z));
            }
        }

        for (int r = 1; r <= halfRings; r++)
        {
            float theta = MathF.PI * 0.5f + MathF.PI * 0.5f * r / halfRings;
            for (int s = 0; s <= segments; s++)
            {
                float phi = 2 * MathF.PI * s / segments;
                Vector3 dir = new(MathF.Sin(theta) * MathF.Cos(phi), MathF.Cos(theta), MathF.Sin(theta) * MathF.Sin(phi));
                verts.Add(new Vector3(radius * dir.X, -halfH + radius * dir.Y, radius * dir.Z));
            }
        }
        return verts.ToArray();
    }
}
