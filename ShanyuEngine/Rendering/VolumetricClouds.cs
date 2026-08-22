using System; using System.Collections.Generic; using System.Numerics; namespace ShanyuEngine.Rendering;

public struct CloudLayerConfig
{
    public float Altitude = 2000, Thickness = 1000;
    public float Density = 1f, ExtinctionCoeff = 0.2f, ScatteringCoeff = 0.8f;
    public Vector4 CloudColor = new(1, 1, 1, 1);
    public Vector4 AmbientColor = new(0.3f, 0.4f, 0.6f, 1);
    public Vector4 SunColor = new(1, 0.95f, 0.85f, 1);
    public float Coverage = 0.5f, Sharpness = 0.8f, Crumple = 0.3f;
    public float WindSpeed = 10, WindDirection = 0;
    public float SampleCount = 64, MaxSteps = 128;
    public TextureShape CloudTexture = TextureShape.PerlinWorley;
    public CloudLayerConfig() { }
}

public enum TextureShape { PerlinWorley, Cellular, Voronoi, WorleyFBM, CurlNoise }

public sealed class CloudSimulation
{
    private CloudLayerConfig _config;
    private double _totalTime;

    public CloudLayerConfig Config { get => _config; set => _config = value; }

    public void Tick(float dt) { _totalTime += dt; }

    public float SampleCloudDensity(Vector3 worldPos)
    {
        float windX = _config.WindSpeed * (float)_totalTime * MathF.Cos(_config.WindDirection);
        float windZ = _config.WindSpeed * (float)_totalTime * MathF.Sin(_config.WindDirection);
        Vector3 p = new(worldPos.X + windX, (worldPos.Y - _config.Altitude) / MathF.Max(_config.Thickness, 1), worldPos.Z + windZ);
        if (p.Y < 0 || p.Y > 1) return 0;

        float density = _config.Density * (1f - MathF.Abs(p.Y - 0.5f) * 2f);
        float noise = Noise3D(p * 0.001f);
        density *= SmoothStep(_config.Coverage - 0.3f, _config.Coverage + 0.3f, noise);

        if (density > 0)
        {
            float detail = Noise3D(p * 0.005f + new Vector3(100, 0, 50));
            density *= 1f - _config.Crumple * (1f - detail);
        }
        return MathF.Max(0, density);
    }

    public Vector3 RenderCloud(Vector3 rayOrigin, Vector3 rayDir, float maxDist, Vector3 sunDir, Vector3 sunColor)
    {
        float near = _config.Altitude / MathF.Max(MathF.Abs(rayDir.Y), 0.0001f);
        float far = (_config.Altitude + _config.Thickness) / MathF.Max(MathF.Abs(rayDir.Y), 0.0001f);
        if (near < 0) near = 0; if (far > maxDist) far = maxDist;
        if (far <= near) return Vector3.Zero;

        float stepSize = (far - near) / _config.MaxSteps;
        Vector3 accum = Vector3.Zero; float transmittance = 1; float depth = near;

        for (int i = 0; i < _config.MaxSteps && transmittance > 0.01f; i++)
        {
            Vector3 pos = rayOrigin + rayDir * depth;
            float density = SampleCloudDensity(pos) * stepSize;
            if (density > 0)
            {
                float scattering = 1f + MathF.Pow(Math.Clamp(Vector3.Dot(rayDir, -sunDir), 0, 1), 4) * 0.5f;
                Vector3 lightExt = Vector3.Zero;
                for (int j = 0; j < 8; j++)
                {
                    Vector3 lightPos = pos + sunDir * (j * 100f);
                    lightExt += new Vector3(SampleCloudDensity(lightPos)) * 0.125f;
                }
                float lightTrans = MathF.Exp(-lightExt.X * _config.ExtinctionCoeff);
                Vector3 scattered = sunColor * lightTrans * density * scattering * _config.ScatteringCoeff;
                scattered += _config.AmbientColor.AsVec3() * density * _config.ScatteringCoeff * 0.3f;
                accum += scattered * transmittance;
                transmittance *= MathF.Exp(-density * _config.ExtinctionCoeff);
            }
            depth += stepSize;
        }
        return accum;
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static float Noise3D(Vector3 p)
    {
        return (SimplexNoise(p) + 1f) * 0.5f;
    }

    private static float SimplexNoise(Vector3 p)
    {
        // Simple gradient noise approximation
        int ix = (int)MathF.Floor(p.X); int iy = (int)MathF.Floor(p.Y); int iz = (int)MathF.Floor(p.Z);
        float fx = p.X - ix; float fy = p.Y - iy; float fz = p.Z - iz;
        var rand = new Random(ix * 73856093 + iy * 19349663 + iz * 83492791);
        float v000 = (float)(rand.NextDouble() * 2 - 1);
        float v100 = (float)(new Random((ix+1)*73856093 + iy*19349663 + iz*83492791).NextDouble() * 2 - 1);
        float v010 = (float)(new Random(ix*73856093 + (iy+1)*19349663 + iz*83492791).NextDouble() * 2 - 1);
        float v110 = (float)(new Random((ix+1)*73856093 + (iy+1)*19349663 + iz*83492791).NextDouble() * 2 - 1);
        float v001 = (float)(new Random(ix*73856093 + iy*19349663 + (iz+1)*83492791).NextDouble() * 2 - 1);
        float v101 = (float)(new Random((ix+1)*73856093 + iy*19349663 + (iz+1)*83492791).NextDouble() * 2 - 1);
        float v011 = (float)(new Random(ix*73856093 + (iy+1)*19349663 + (iz+1)*83492791).NextDouble() * 2 - 1);
        float v111 = (float)(new Random((ix+1)*73856093 + (iy+1)*19349663 + (iz+1)*83492791).NextDouble() * 2 - 1);

        fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy); fz = fz * fz * (3 - 2 * fz);
        return (v000*(1-fx)*(1-fy)*(1-fz) + v100*fx*(1-fy)*(1-fz) + v010*(1-fx)*fy*(1-fz) + v110*fx*fy*(1-fz) +
                v001*(1-fx)*(1-fy)*fz + v101*fx*(1-fy)*fz + v011*(1-fx)*fy*fz + v111*fx*fy*fz);
    }
}


