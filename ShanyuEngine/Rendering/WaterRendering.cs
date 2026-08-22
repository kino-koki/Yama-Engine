using System; using System.Collections.Generic; using System.Numerics; namespace ShanyuEngine.Rendering;
using ShanyuEngine.World;

public enum WaterQuality { Low, Medium, High, Ultra, Cinema }
public enum WaterRenderMode { Reflection, Refraction, Simple, Underwater, WaveOnly }
public enum ShoreBlendMode { DistanceBased, HeightBased, TextureBased, VertexColor }

public struct WaterConfig
{
    public float Level = 0; public Vector2 Size = new(10000, 10000);
    public int Subdivisions = 64; public WaterQuality Quality = WaterQuality.High;
    public float WaveAmplitude = 0.5f, WaveFrequency = 0.02f, WaveSpeed = 0.5f, WaveLength = 10f;
    public int WaveCount = 8; public float WindSpeed = 10, WindDirection = 0;
    public Vector4 ShallowColor = new(0.2f, 0.5f, 0.7f, 1), DeepColor = new(0, 0.1f, 0.2f, 1);
    public float ShoreBlendDist = 5f; public Vector4 ShoreColor = new(0.4f, 0.6f, 0.5f, 1);
    public float FresnelPower = 3f, FresnelStrength = 0.5f;
    public float ReflectionIntensity = 0.3f, RefractionIntensity = 0.1f;
    public float Roughness = 0.2f, Specular = 0.8f;
    public float FoamAmount = 0.3f, FoamSpeed = 0.5f;
    public float Clarity = 5f, Absorption = 2f;
    public Vector3 FlowDirection = new(1, 0, 0);
    public WaterConfig() { }
}

public struct GerstnerWave
{
    public Vector2 Direction; public float Amplitude, Frequency, Speed, Steepness;
    public float GetHeight(Vector2 pos, float time)
    {
        float d = Vector2.Dot(Direction, pos);
        return Amplitude * MathF.Sin(d * Frequency + time * Speed);
    }
    public (Vector3 pos, Vector3 normal) Sample(Vector2 pos, float time)
    {
        float d = Vector2.Dot(Direction, pos);
        float s = MathF.Sin(d * Frequency + time * Speed);
        float c = MathF.Cos(d * Frequency + time * Speed);
        float q = Steepness / (Frequency * WaveCount * Amplitude);
        float x = Direction.X * q * Amplitude * c;
        float z = Direction.Y * q * Amplitude * c;
        float y = Amplitude * s;
        float wx = -Direction.X * Frequency * Amplitude * c;
        float wz = -Direction.Y * Frequency * Amplitude * c;
        float wy = -Frequency * Amplitude * s;
        return (new(x, y, z), Vector3.Normalize(new(-wx, wy, -wz)));
    }
    public int WaveCount;
}

public sealed class WaterWaveSimulation
{
    private readonly GerstnerWave[] _waves;
    private readonly int _waveCount;
    private float _time;

    public WaterWaveSimulation(WaterConfig cfg)
    {
        _waveCount = cfg.WaveCount; _waves = new GerstnerWave[_waveCount];
        var rng = new Random(42);
        for (int i = 0; i < _waveCount; i++)
        {
            float angle = (float)(rng.NextDouble() * MathF.PI * 2);
            _waves[i] = new GerstnerWave
            {
                Direction = new(MathF.Cos(angle + cfg.WindDirection), MathF.Sin(angle + cfg.WindDirection)),
                Amplitude = cfg.WaveAmplitude * (float)(rng.NextDouble() * 0.5f + 0.5f) / _waveCount,
                Frequency = cfg.WaveFrequency * (float)(rng.NextDouble() * 2 + 1),
                Speed = cfg.WaveSpeed * (float)(rng.NextDouble() + 0.5f),
                Steepness = (float)(rng.NextDouble() * 0.3f + 0.1f),
                WaveCount = _waveCount,
            };
        }
    }

    public void Update(float dt) { _time += dt; }

    public float GetHeight(Vector3 worldPos)
    {
        Vector2 pos = new(worldPos.X, worldPos.Z); float h = 0;
        for (int i = 0; i < _waveCount; i++) h += _waves[i].GetHeight(pos, _time);
        return h;
    }

    public (Vector3 pos, Vector3 normal) GetSurface(Vector3 worldPos)
    {
        Vector2 pos = new(worldPos.X, worldPos.Z);
        Vector3 totalPos = new(0, 0, 0); Vector3 totalNormal = new(0, _waveCount, 0);
        for (int i = 0; i < _waveCount; i++)
        {
            var (p, n) = _waves[i].Sample(pos, _time);
            totalPos += p; totalNormal += n;
        }
        totalNormal = Vector3.Normalize(totalNormal);
        return (new(worldPos.X, totalPos.Y, worldPos.Z), totalNormal);
    }

    public GerstnerWave[] GetWaves() => _waves;
}

public sealed class WaterBody
{
    public WaterConfig Config; public WaterWaveSimulation Waves;
    public Vector3 Position; public bool Visible, IsUnderwater;
    public AABB Bounds;

    public WaterBody(WaterConfig cfg)
    {
        Config = cfg; Waves = new(cfg);
        float hw = cfg.Size.X * 0.5f, hd = cfg.Size.Y * 0.5f;
        Bounds = new(new(-hw, cfg.Level - 2, -hd), new(hw, cfg.Level + 2, hd));
    }

    public void Update(float dt)
    {
        Waves.Update(dt);
    }

    public float WaveHeight(Vector3 worldPos) => Waves.GetHeight(worldPos) + Config.Level;

    public Vector3 WaveNormal(Vector3 worldPos)
    {
        var (_, n) = Waves.GetSurface(worldPos);
        return n;
    }

    public bool TestUnderwater(Vector3 cameraPos)
    {
        IsUnderwater = cameraPos.Y < WaveHeight(cameraPos);
        return IsUnderwater;
    }

    public float GetOpacity(Vector3 worldPos, float viewDist)
    {
        float depth = MathF.Max(0, Config.Level - worldPos.Y);
        float clarity = MathF.Exp(-depth * Config.Absorption);
        return Math.Clamp(1f - clarity, 0.1f, 0.95f);
    }

    public Vector4 GetWaterColor(Vector3 worldPos, float viewAngle)
    {
        float depth = MathF.Max(0, Config.Level - worldPos.Y);
        float t = Math.Clamp(depth / Config.Clarity, 0, 1);
        float fresnel = MathF.Pow(1f - MathF.Abs(viewAngle), Config.FresnelPower) * Config.FresnelStrength;
        var color = Vector4.Lerp(Config.ShallowColor, Config.DeepColor, t);
        return new(color.X, color.Y, color.Z, Math.Clamp(fresnel + 0.3f, 0, 1));
    }
}

public sealed class WaterManager
{
    private readonly List<WaterBody> _bodies = new();
    private readonly WaterConfig _defaultConfig;

    public WaterManager(WaterConfig? cfg = null) { _defaultConfig = cfg ?? new WaterConfig(); }

    public WaterBody AddBody(Vector3 position, WaterConfig? config = null)
    {
        var cfg = config ?? _defaultConfig;
        var body = new WaterBody(cfg) { Position = position };
        _bodies.Add(body); return body;
    }

    public void RemoveBody(WaterBody body) => _bodies.Remove(body);
    public IReadOnlyList<WaterBody> Bodies => _bodies;
    public void Clear() => _bodies.Clear();

    public void Update(float dt)
    {
        foreach (var b in _bodies) b.Update(dt);
    }

    public WaterBody? FindNearest(Vector3 pos)
    {
        WaterBody? best = null; float bestD = float.MaxValue;
        foreach (var b in _bodies)
        {
            float d = Vector3.DistanceSquared(pos, b.Position);
            if (d < bestD) { bestD = d; best = b; }
        }
        return best;
    }

    public bool IsUnderwater(Vector3 pos)
    {
        foreach (var b in _bodies) if (b.TestUnderwater(pos)) return true;
        return false;
    }

    public void SetGlobalWaveParams(float amp, float freq, float speed)
    {
        var cfg = _defaultConfig;
        cfg.WaveAmplitude = amp; cfg.WaveFrequency = freq; cfg.WaveSpeed = speed;
    }
}

public sealed class WaterInteraction
{
    public struct FloatData
    {
        public int BodyId; public Vector3 Position;
        public float BuoyancyForce, WaterLevel;
        public bool IsFloating;
    }

    private readonly Dictionary<int, FloatData> _foats = new();

    public FloatData SampleFloation(Vector3 pos, WaterBody body, float volume, float density = 0.5f)
    {
        float wl = body.WaveHeight(pos);
        float subDepth = wl - pos.Y;
        if (subDepth <= 0) return new FloatData { IsFloating = false, WaterLevel = wl };

        float buoyancy = subDepth * volume * density * 9.81f;
        return new FloatData { IsFloating = true, BuoyancyForce = buoyancy, WaterLevel = wl, Position = pos };
    }

    public Vector3 ApplyBuoyancy(int objId, Vector3 pos, Vector3 vel, WaterBody body, float volume, float linearDrag = 0.5f)
    {
        var fd = SampleFloation(pos, body, volume);
        if (!fd.IsFloating) return vel;
        Vector3 force = new(0, fd.BuoyancyForce, 0);
        Vector3 drag = vel * -linearDrag * MathF.Max(0, fd.WaterLevel - pos.Y);
        return force + drag;
    }

    public float ComputeDrag(Vector3 pos, Vector3 vel, WaterBody body, float crossSection, float dragCoeff = 0.5f)
    {
        float wl = body.WaveHeight(pos);
        float subDepth = MathF.Max(0, wl - pos.Y);
        float fluidDensity = 1000f;
        float speed = vel.Length();
        return 0.5f * fluidDensity * speed * speed * dragCoeff * crossSection * subDepth;
    }
}


public sealed class ShorelineGenerator
{
    public struct ShorePoint
    {
        public Vector3 Position; public float WaveIntensity;
        public Vector3 Normal; public bool IsEroded;
    }

    public ShorePoint[] GenerateShoreline(TerrainHeightmap terrain, WaterBody water, int sampleCount = 500, float shoreWidth = 20)
    {
        var points = new List<ShorePoint>(); var rng = new Random(42);
        float hf = terrain.Size * 0.5f;

        for (int i = 0; i < sampleCount; i++)
        {
            float x = (float)(rng.NextDouble() - 0.5) * terrain.Size;
            float z = (float)(rng.NextDouble() - 0.5) * terrain.Size;
            Vector3 pos = new(x, 0, z);
            float terrainH = terrain.Sample(pos);
            float waterH = water.WaveHeight(pos);

            if (MathF.Abs(terrainH - waterH) < shoreWidth)
            {
                var normal = terrain.Normal(pos);
                float waveIntensity = MathF.Max(0, 1f - MathF.Abs(terrainH - waterH) / shoreWidth);
                points.Add(new ShorePoint { Position = pos, WaveIntensity = waveIntensity, Normal = normal, IsEroded = waveIntensity > 0.5f });
            }
        }
        return points.ToArray();
    }

    public Vector3[] GenerateFoamPoints(ShorePoint[] shore, float foamWidth = 2)
    {
        var foam = new List<Vector3>();
        foreach (var sp in shore)
        {
            if (!sp.IsEroded) continue;
            for (int i = 0; i < 3; i++)
            {
                var rng = new Random();
                Vector3 offset = new((float)(rng.NextDouble() - 0.5) * foamWidth, 0, (float)(rng.NextDouble() - 0.5) * foamWidth);
                foam.Add(sp.Position + offset + new Vector3(0, 0.1f, 0));
            }
        }
        return foam.ToArray();
    }
}
