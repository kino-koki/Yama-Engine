using System; using System.Collections.Generic; using System.Numerics; namespace ShanyuEngine.Rendering;

public enum TonemapOperator { Linear, Reinhard, ACES, Filmic, Uncharted2, Uchimura, Hable, Custom }
public enum ColorSpace { sRGB, Linear, Rec709, Rec2020, DCI_P3, ACEScg }
public enum WhiteBalanceMode { Custom, Auto, Daylight, Cloudy, Shade, Tungsten, Fluorescent, Flash, Kelvin }
public enum LutResolution { Low16 = 16, Medium32 = 32, High64 = 64 }


public sealed class ColorGrading
{
    public ColorGradingSettings Settings;

    public Vector3 Apply(Vector3 color)
    {
        color = ApplyWhiteBalance(color);
        color = ApplyHueShift(color);
        color = ApplyContrast(color);
        color = ApplySaturation(color);
        color = ApplyVibrance(color);
        color = ApplyLiftGammaGain(color);
        color = ApplyShadowsMidtonesHighlights(color);
        color = ApplyTonemapping(color);
        return color;
    }

    public Vector3 ApplyTonemapping(Vector3 color)
    {
        return Settings.Tonemap switch
        {
            TonemapOperator.Linear => color * Settings.Exposure,
            TonemapOperator.Reinhard => color / (Vector3.One + color) * Settings.Exposure,
            TonemapOperator.ACES => ACESFitted(color * Settings.Exposure),
            TonemapOperator.Filmic => Filmic(color * Settings.Exposure),
            TonemapOperator.Uncharted2 => Uncharted2(color * Settings.Exposure),
            TonemapOperator.Hable => Hable(color * Settings.Exposure),
            TonemapOperator.Uchimura => Uchimura(color * Settings.Exposure, 11.2f, 0.17f, 0.22f, 0.1f, 0.2f, 0.1f),
            _ => color * Settings.Exposure,
        };
    }

    public Vector3 ApplyWhiteBalance(Vector3 color)
    {
        if (MathF.Abs(Settings.Temperature) < 0.01f && MathF.Abs(Settings.Tint) < 0.01f) return color;
        float t = Settings.Temperature; float tint = Settings.Tint;
        float r = color.X * (1f + t * 0.02f) + color.Y * tint * 0.01f;
        float g = color.Y * (1f - MathF.Abs(t) * 0.01f);
        float b = color.Z * (1f - t * 0.02f) + color.Y * tint * -0.01f;
        return new(MathF.Max(0,r), MathF.Max(0,g), MathF.Max(0,b));
    }

    public Vector3 ApplyContrast(Vector3 color)
    {
        if (MathF.Abs(Settings.Contrast - 1) < 0.001f) return color;
        float midpoint = MathF.Pow(0.5f, Settings.GammaValue);
        return (color - new Vector3(midpoint)) * Settings.Contrast + new Vector3(midpoint);
    }

    public Vector3 ApplySaturation(Vector3 color)
    {
        if (MathF.Abs(Settings.Saturation - 1) < 0.001f) return color;
        float lum = color.X * 0.2126f + color.Y * 0.7152f + color.Z * 0.0722f;
        return Vector3.Lerp(new Vector3(lum), color, Settings.Saturation);
    }

    public Vector3 ApplyVibrance(Vector3 color)
    {
        if (MathF.Abs(Settings.Vibrance) < 0.001f) return color;
        float maxC = MathF.Max(color.X, MathF.Max(color.Y, color.Z));
        float minC = MathF.Min(color.X, MathF.Min(color.Y, color.Z));
        float sat = maxC - minC;
        float vib = Settings.Vibrance * (1f - sat);
        float lum = color.X * 0.2126f + color.Y * 0.7152f + color.Z * 0.0722f;
        return Vector3.Lerp(new Vector3(lum), color, 1f + vib);
    }

    public Vector3 ApplyLiftGammaGain(Vector3 color)
    {
        Vector3 result = color * Settings.Gain;
        result = new(MathF.Pow(result.X, 1f / Settings.Gamma.X), MathF.Pow(result.Y, 1f / Settings.Gamma.Y), MathF.Pow(result.Z, 1f / Settings.Gamma.Z));
        result += Settings.Lift;
        return result;
    }

    public Vector3 ApplyHueShift(Vector3 color)
    {
        if (MathF.Abs(Settings.HueShift) < 0.001f) return color;
        float cos = MathF.Cos(Settings.HueShift); float sin = MathF.Sin(Settings.HueShift);
        float r = color.X * (0.299f + 0.701f * cos + 0.168f * sin) + color.Y * (0.587f - 0.587f * cos + 0.330f * sin) + color.Z * (0.114f - 0.114f * cos - 0.497f * sin);
        float g = color.X * (0.299f - 0.299f * cos - 0.328f * sin) + color.Y * (0.587f + 0.413f * cos + 0.035f * sin) + color.Z * (0.114f - 0.114f * cos + 0.292f * sin);
        float b = color.X * (0.299f - 0.300f * cos + 1.250f * sin) + color.Y * (0.587f - 0.588f * cos - 1.050f * sin) + color.Z * (0.114f + 0.886f * cos - 0.203f * sin);
        return new(MathF.Max(0,r), MathF.Max(0,g), MathF.Max(0,b));
    }

    public Vector3 ApplyShadowsMidtonesHighlights(Vector3 color)
    {
        float lum = color.X * 0.2126f + color.Y * 0.7152f + color.Z * 0.0722f;

        float sw = 1f - SmoothStep(Settings.ShadowStart, Settings.ShadowEnd, lum);
        float mw = SmoothStep(Settings.ShadowStart, Settings.ShadowEnd, lum) * (1f - SmoothStep(Settings.HighlightStart, Settings.HighlightEnd, lum));
        float hw = SmoothStep(Settings.HighlightStart, Settings.HighlightEnd, lum);

        return color + Settings.Shadows * sw + Settings.Midtones * mw + Settings.Highlights * hw;
    }

    public float ApplyVignette(Vector2 uv, float intensity)
    {
        if (intensity <= 0) return 1f;
        float dist = Vector2.Distance(uv, new(0.5f, 0.5f));
        float vig = 1f - SmoothStep(0.3f, 0.5f + Settings.VignetteSmoothness, dist * 1.4f);
        return Math.Clamp(vig + (1f - intensity), 0, 1);
    }

    public float ApplyFilmGrain(float value, float strength)
    {
        if (strength <= 0) return value;
        float noise = (float)(new Random().NextDouble() * 2 - 1);
        return value + noise * strength;
    }

    public (Vector2 redOffset, Vector2 blueOffset) ApplyChromaticAberration(float intensity)
    {
        if (intensity <= 0) return (Vector2.Zero, Vector2.Zero);
        float r = intensity * 0.003f; float b = intensity * 0.003f;
        return (new(r, 0), new(-b, 0));
    }

    private static Vector3 ACESFitted(Vector3 color)
    {
        float a = 2.51f, b = 0.03f, c = 2.43f, d = 0.59f, e = 0.14f;
        return Vector3.Clamp(color * (a * color + b) / (color * (c * color + d) + e), Vector3.Zero, Vector3.One);
    }

    private static Vector3 Filmic(Vector3 color)
    {
        var x = color; var x2 = x * x; var x3 = x2 * x;
        return (x * (6.2f * x + 0.5f)) / (x * (6.2f * x + 1.7f) + 0.06f);
    }

    private static Vector3 Uncharted2(Vector3 color)
    {
        float A = 0.15f, B = 0.50f, C = 0.10f, D = 0.20f, E = 0.02f, F = 0.30f, W = 11.2f;
        var x = color;
        var result = ((x * (A * x + C * B) + D * E) / (x * (A * x + B) + D * F)) - E / F;
        var white = ((W * (A * W + C * B) + D * E) / (W * (A * W + B) + D * F)) - E / F;
        return Vector3.Clamp(result / white, Vector3.Zero, Vector3.One);
    }

    private static Vector3 Hable(Vector3 color)
    {
        float A = 0.22f, B = 0.30f, C = 0.10f, D = 0.20f, E = 0.01f, F = 0.30f;
        var x = color;
        return ((x * (A * x + C * B) + D * E) / (x * (A * x + B) + D * F)) - E / F;
    }

    private static Vector3 Uchimura(Vector3 color, float P, float a, float m, float l, float c, float b)
    {
        float l0 = ((P - m) * l) / a;
        float L0 = m - m / a;
        float L1 = m + (1f - m) / (1f - a);
        float S0 = m / l0;
        float S1 = (1f - m) / (l0 * (1f - a) * (l - m));
        return Vector3.Clamp(color / (color + Vector3.One), Vector3.Zero, Vector3.One);
    }

    private static float SmoothStep(float e0, float e1, float x)
    {
        float t = Math.Clamp((x - e0) / (e1 - e0), 0, 1);
        return t * t * (3 - 2 * t);
    }

    public static string[] OperatorNames = { "Linear", "Reinhard", "ACES", "Filmic", "Uncharted2", "Uchimura", "Hable" };
    public static float[] OperatorEval(Vector3 color, TonemapOperator op)
    {
        var cg = new ColorGrading { Settings = new ColorGradingSettings { Tonemap = op, Exposure = 1 } };
        var r = cg.ApplyTonemapping(color);
        return new float[] { r.X, r.Y, r.Z };
    }
}

public sealed class BloomEffect
{
    public float Intensity = 1f, Threshold = 1f, SoftThreshold = 0.5f, Scatter = 0.7f;
    public int DownsampleSteps = 6, UpsampleSteps = 6;
    public float AnamorphicRatio = 1f;
    public float[] GenerateKernel(int radius, float sigma)
    {
        int size = radius * 2 + 1; float[] k = new float[size]; float sum = 0;
        for (int i = 0; i < size; i++)
        {
            int x = i - radius; k[i] = MathF.Exp(-(x * x) / (2 * sigma * sigma));
            sum += k[i];
        }
        for (int i = 0; i < size; i++) k[i] /= sum;
        return k;
    }

    public float Prefilter(float luminance)
    {
        if (Threshold <= 0) return luminance;
        float soft = SoftThreshold * Threshold;
        if (luminance > Threshold - soft)
            return luminance - Threshold + soft * (luminance > Threshold ? 1f : 0.5f * (luminance - (Threshold - soft)) / soft);
        return 0;
    }
}

public sealed class MotionBlurEffect
{
    public int SampleCount = 8; public float Strength = 1f, MaxRadius = 0.5f;
    public bool FullResolution; public int Downsample = 2;
}

public sealed class DepthOfFieldEffect
{
    public float FocalDistance = 10f, FocalRegion = 3f, MaxBlurSize = 0.5f;
    public int SampleCount = 16;
    public bool Autofocus; public float FocusSpeed = 5f;
    public enum Quality { Low4Taps, Med8Taps, High16Taps, Cinematic32Taps }
    public Quality BlurQuality = Quality.High16Taps;

    public float ComputeCoC(float depth, float focalDist, float focalRegion)
    {
        float diff = MathF.Abs(depth - focalDist);
        return Math.Clamp(diff / MathF.Max(focalRegion, 0.001f) * MaxBlurSize, 0, MaxBlurSize);
    }
}

public sealed class LensFlareEffect
{
    public float Intensity = 0.2f, Brightness = 1f, Scale = 1f;
    public int GhostCount = 5; public float GhostDispersal = 0.3f, HaloWidth = 0.5f;
    public float FlareThreshold = 0.5f;
    public float ChromaticAberration = 0.002f;
    public float[] GenerateGhostPositions(float angle, float brightness)
    {
        var pos = new float[GhostCount * 2];
        for (int i = 0; i < GhostCount; i++)
        {
            float t = (i + 1) / (float)GhostCount;
            float p = t * GhostDispersal;
            float x = MathF.Cos(angle) * p; float y = MathF.Sin(angle) * p;
            float fade = MathF.Max(0, 1f - t) * brightness;
            pos[i * 2] = x; pos[i * 2 + 1] = y * fade;
        }
        return pos;
    }
}

public sealed class SSAOEffect
{
    public float Radius = 0.5f, Power = 2f, Bias = 0.025f;
    public int SampleCount = 16; public float BlurSigma = 2f;
    public bool HalfResolution = false;
    public int BlurPasses = 2;

    public float[] GenerateSampleKernel()
    {
        var rng = new Random(42); var kernel = new float[SampleCount * 3];
        for (int i = 0; i < SampleCount; i++)
        {
            float scale = (float)i / SampleCount; scale = 0.1f + 0.9f * scale * scale;
            float theta = (float)(rng.NextDouble() * MathF.PI * 2);
            float phi = (float)(rng.NextDouble() * MathF.PI * 0.5f);
            float x = MathF.Cos(theta) * MathF.Sin(phi);
            float y = MathF.Sin(theta) * MathF.Sin(phi);
            float z = MathF.Cos(phi);
            kernel[i * 3] = x * scale; kernel[i * 3 + 1] = y * scale; kernel[i * 3 + 2] = z * scale;
        }
        return kernel;
    }
}
