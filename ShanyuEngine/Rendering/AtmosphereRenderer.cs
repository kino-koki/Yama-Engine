using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using ShanyuEngine.RHI;
using ShanyuEngine.Shaders;

namespace ShanyuEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════
    // Atmosphere Renderer
    // Physical sky model based on Rayleigh + Mie scattering, multi-scattering
    // approximation, aerial perspective, ray-marched volumetric clouds with
    // noise-based shapes, cloud shadows, god-ray light shafts, and FFT-based
    // ocean rendering with Gerstner wave displacement.
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Configuration for the atmosphere and sky rendering system.
    /// </summary>
    public sealed class AtmosphereSettings
    {
        // ── Planet / atmosphere geometry ──────────────────────────────
        /// <summary>Planet radius in metres (Earth = 6371e3).</summary>
        public float PlanetRadius = 6371000f;
        /// <summary>Atmosphere thickness above the surface in metres.</summary>
        public float AtmosphereHeight = 100000f;
        /// <summary>Rayleigh scale height (m). Determines falloff of air density.</summary>
        public float RayleighScaleHeight = 8500f;
        /// <summary>Mie scale height (m). Determines falloff of aerosol density.</summary>
        public float MieScaleHeight = 1200f;

        // ── Scattering coefficients ───────────────────────────────────
        /// <summary>Rayleigh scattering coefficients at sea level (RGB).</summary>
        public Vector3 RayleighScattering = new(5.802e-6f, 13.558e-6f, 33.100e-6f);
        /// <summary>Mie scattering coefficient at sea level.</summary>
        public float MieScattering = 21e-6f;
        /// <summary>Mie absorption coefficient at sea level.</summary>
        public float MieAbsorption = 1.2e-6f;
        /// <summary>Mie phase function asymmetry parameter g (forward scattering).</summary>
        public float MiePhaseG = 0.758f;
        /// <summary>Ground albedo for multi-bounce contributions.</summary>
        public Vector3 GroundAlbedo = new(0.3f, 0.3f, 0.3f);

        // ── Sun ───────────────────────────────────────────────────────
        /// <summary>Sun direction (normalized, points towards sun).</summary>
        public Vector3 SunDirection = Vector3.Normalize(new Vector3(0.5f, 0.6f, 0.4f));
        /// <summary>Sun illuminance in lux.</summary>
        public float SunIlluminance = 100000f;
        /// <summary>Sun angular radius in radians (0.00935 for our sun).</summary>
        public float SunAngularRadius = 0.00935f;

        // ── Aerial perspective ────────────────────────────────────────
        /// <summary>Enables distance-based atmospheric fog tinting.</summary>
        public bool EnableAerialPerspective = true;
        /// <summary>Maximum fog distance in world units.</summary>
        public float FogMaxDistance = 5000f;
        /// <summary>Fog density multiplier.</summary>
        public float FogDensity = 1.0f;

        // ── Volumetric clouds ─────────────────────────────────────────
        /// <summary>Enables ray-marched volumetric cloud rendering.</summary>
        public bool EnableClouds = true;
        /// <summary>Cloud layer start altitude (m).</summary>
        public float CloudAltitudeStart = 1500f;
        /// <summary>Cloud layer thickness (m).</summary>
        public float CloudThickness = 3500f;
        /// <summary>Number of ray-march steps through the cloud layer.</summary>
        public int CloudRayMarchSteps = 64;
        /// <summary>Cloud density multiplier.</summary>
        public float CloudDensity = 1.0f;
        /// <summary>Cloud coverage [0,1].</summary>
        public float CloudCoverage = 0.55f;
        /// <summary>Wind direction (horizontal).</summary>
        public Vector2 CloudWindDirection = new(1f, 0.2f);
        /// <summary>Wind speed in m/s.</summary>
        public float CloudWindSpeed = 15f;
        /// <summary>Enables cloud shadow projection.</summary>
        public bool EnableCloudShadows = true;

        // ── God rays ──────────────────────────────────────────────────
        /// <summary>Enables screen-space god-ray light shafts.</summary>
        public bool EnableGodRays = true;
        /// <summary>Number of god-ray radial samples.</summary>
        public int GodRaySamples = 64;
        /// <summary>God ray intensity.</summary>
        public float GodRayIntensity = 0.4f;
        /// <summary>God ray decay per sample.</summary>
        public float GodRayDecay = 0.96f;
        /// <summary>God ray exposure weight.</summary>
        public float GodRayExposure = 0.25f;

        // ── Ocean ─────────────────────────────────────────────────────
        /// <summary>Enables FFT-based ocean water rendering.</summary>
        public bool EnableOcean = true;
        /// <summary>FFT grid resolution (must be power of 2).</summary>
        public int OceanFFTSize = 256;
        /// <summary>Ocean patch size in world units.</summary>
        public float OceanPatchSize = 2000f;
        /// <summary>Wind speed for wave spectrum (m/s).</summary>
        public float OceanWindSpeed = 12f;
        /// <summary>Wind direction for wave propagation.</summary>
        public Vector2 OceanWindDirection = new(1f, 0f);
        /// <summary>Wave amplitude scale.</summary>
        public float OceanAmplitude = 1.0f;
        /// <summary>Deep water colour.</summary>
        public Vector3 OceanDeepColor = new(0.02f, 0.08f, 0.15f);
        /// <summary>Shallow water colour.</summary>
        public Vector3 OceanShallowColor = new(0.1f, 0.4f, 0.5f);
        /// <summary>Ocean choppiness for Gerstner waves.</summary>
        public float OceanChoppiness = 1.2f;
        /// <summary>Time scale for wave animation.</summary>
        public float OceanTimeScale = 1.0f;
    }

    /// <summary>
    /// Precomputed atmosphere lookup tables. The transmittance LUT maps
    /// (view zenith, view horizon) to a transmittance RGB; the multi-scatter
    /// LUT stores the accumulated higher-order scattering.
    /// </summary>
    public sealed class AtmosphereLUT
    {
        /// <summary>Transmittance LUT (2D texture, R16G16B16A16_Float).</summary>
        public RHITexture? Transmittance;
        /// <summary>Multi-scattering LUT (2D texture, R16G16B16A16_Float).</summary>
        public RHITexture? MultiScatter;
        /// <summary>Sky-view LUT (2D texture storing sky radiance for all directions).</summary>
        public RHITexture? SkyView;
        /// <summary>Aerial-perspective LUT (3D texture storing transmittance × distance).</summary>
        public RHITexture? AerialPerspective;

        /// <summary>SRV handles for shader binding.</summary>
        public RHIDescriptorHandle TransmittanceSRV;
        /// <summary>Multi-scatter SRV.</summary>
        public RHIDescriptorHandle MultiScatterSRV;
        /// <summary>Sky-view SRV.</summary>
        public RHIDescriptorHandle SkyViewSRV;
        /// <summary>Aerial-perspective SRV.</summary>
        public RHIDescriptorHandle AerialPerspectiveSRV;

        /// <summary>Width of the transmittance LUT.</summary>
        public const int TransmittanceWidth = 256;
        /// <summary>Height of the transmittance LUT.</summary>
        public const int TransmittanceHeight = 64;
        /// <summary>Width of the sky-view LUT.</summary>
        public const int SkyViewWidth = 256;
        /// <summary>Height of the sky-view LUT.</summary>
        public const int SkyViewHeight = 128;
        /// <summary>Width of the aerial-perspective LUT.</summary>
        public const int AerialWidth = 32;
        /// <summary>Height of the aerial-perspective LUT.</summary>
        public const int AerialHeight = 32;
        /// <summary>Depth slices of the aerial-perspective LUT.</summary>
        public const int AerialDepth = 32;
    }

    /// <summary>
    /// CPU-side transmittance computation following the Bruneton &amp; Nakamae
    /// precomputation method. Computes the optical depth along a ray from a
    /// point at altitude <paramref name="r"/> in view direction <paramref name="mu"/>
    /// (cosine of the view zenith angle) up to atmosphere exit or ground intersection.
    /// </summary>
    public static class AtmospherePhysics
    {
        /// <summary>Planet centre is at origin; altitude is measured from surface.</summary>
        public const float Epsilon = 1e-4f;

        /// <summary>
        /// Computes whether the ray (origin at radius <paramref name="r"/>, direction
        /// cosine <paramref name="mu"/>) intersects the ground before leaving the atmosphere.
        /// </summary>
        public static bool RayIntersectsGround(float r, float mu, float planetRadius)
        {
            var r2 = r * r;
            var mu2 = mu * mu;
            var rp = planetRadius;
            var disc = r2 * (mu2 - 1f) + rp * rp;
            return disc >= 0f && mu < 0f;
        }

        /// <summary>
        /// Solves the ray-sphere intersection for the atmosphere boundary.
        /// Returns the distance to the nearest atmosphere exit point along the ray,
        /// or to the ground intersection if the ray hits the planet first.
        /// </summary>
        public static float RaySphereDistance(float r, float mu, float planetRadius, float atmosphereRadius)
        {
            // Distance to atmosphere boundary
            var r2 = r * r;
            var mu2 = mu * mu;
            var ra2 = atmosphereRadius * atmosphereRadius;
            var discAtm = r2 * (mu2 - 1f) + ra2;
            var dAtm = discAtm >= 0f ? (-mu * r + MathF.Sqrt(discAtm)) : float.MaxValue;

            // Distance to ground
            var rp2 = planetRadius * planetRadius;
            var discGr = r2 * (mu2 - 1f) + rp2;
            var dGr = discGr >= 0f && mu < 0f ? (-mu * r - MathF.Sqrt(discGr)) : float.MaxValue;

            return MathF.Min(dAtm, dGr);
        }

        /// <summary>
        /// Computes the Rayleigh optical depth along a segment of length
        /// <paramref name="dt"/> at altitude <paramref name="r"/> using the
        /// exponential density falloff exp(-h/H_r).
        /// </summary>
        public static float RayleighOpticalDepth(float r, float mu, float dt,
            float planetRadius, float scaleHeight, int samples = 8)
        {
            var depth = 0f;
            var step = dt / samples;
            for (var i = 0; i < samples; i++)
            {
                var t = (i + 0.5f) * step;
                var ri = MathF.Sqrt(r * r + 2f * r * mu * t + t * t);
                var hi = ri - planetRadius;
                var density = MathF.Exp(-MathF.Max(0f, hi) / scaleHeight);
                depth += density * step;
            }
            return depth;
        }

        /// <summary>
        /// Computes the Mie optical depth with the same integration scheme but
        /// the Mie scale height.
        /// </summary>
        public static float MieOpticalDepth(float r, float mu, float dt,
            float planetRadius, float scaleHeight, int samples = 8)
        {
            return RayleighOpticalDepth(r, mu, dt, planetRadius, scaleHeight, samples);
        }

        /// <summary>
        /// Computes the transmittance (Beer-Lambert) along a ray segment.
        /// Returns RGB transmittance in [0,1].
        /// </summary>
        public static Vector3 ComputeTransmittance(
            float r, float mu, float dt,
            in AtmosphereSettings s)
        {
            var rayleighDepth = RayleighOpticalDepth(r, mu, dt, s.PlanetRadius, s.RayleighScaleHeight);
            var mieDepth = MieOpticalDepth(r, mu, dt, s.PlanetRadius, s.MieScaleHeight);

            var tauR = s.RayleighScattering * rayleighDepth;
            var tauM = new Vector3(s.MieScattering + s.MieAbsorption) * mieDepth;

            return TransmittanceFromOpticalDepth(tauR + tauM);
        }

        /// <summary>Beer-Lambert transmittance from optical depth vector.</summary>
        public static Vector3 TransmittanceFromOpticalDepth(Vector3 opticalDepth)
        {
            return new Vector3(
                MathF.Exp(-opticalDepth.X),
                MathF.Exp(-opticalDepth.Y),
                MathF.Exp(-opticalDepth.Z));
        }

        /// <summary>
        /// Rayleigh phase function. Azimuthally symmetric; equals 3/(16*pi) * (1 + cos^2(theta)).
        /// </summary>
        public static float RayleighPhase(float cosTheta)
        {
            const float k = 3f / (16f * MathF.PI);
            return k * (1f + cosTheta * cosTheta);
        }

        /// <summary>
        /// Cornette-Shanks phase function (improved Mie / Henyey-Greenstein).
        /// More physically accurate near forward-scattering angles.
        /// </summary>
        public static float MiePhase(float cosTheta, float g)
        {
            var g2 = g * g;
            var num = (3f * (1f - g2)) * (1f + cosTheta * cosTheta);
            var denom = (2f * (2f + g2)) * MathF.Pow(1f + g2 - 2f * g * cosTheta, 1.5f);
            return num / denom;
        }

        /// <summary>
        /// Computes single-scattered sky radiance for a given view direction.
        /// This is the CPU reference; the GPU shader mirrors this computation.
        /// </summary>
        public static Vector3 ComputeSingleScattering(
            Vector3 worldPos, Vector3 viewDir, in AtmosphereSettings s)
        {
            var r = worldPos.Length();
            if (r < s.PlanetRadius) r = s.PlanetRadius + 1f;
            var mu = Vector3.Dot(worldPos, viewDir) / r;
            var atmRadius = s.PlanetRadius + s.AtmosphereHeight;
            var dt = RaySphereDistance(r, mu, s.PlanetRadius, atmRadius);

            // Sun direction cosine
            var muSun = Vector3.Dot(Vector3.Normalize(worldPos), s.SunDirection);

            var result = Vector3.Zero;
            var steps = 32;
            var step = dt / steps;

            for (var i = 0; i < steps; i++)
            {
                var t = (i + 0.5f) * step;
                var pos = worldPos + viewDir * t;
                var ri = pos.Length();
                var mui = Vector3.Dot(pos, viewDir) / ri;
                var muSunI = Vector3.Dot(Vector3.Normalize(pos), s.SunDirection);

                // Transmittance from camera to this point
                var tCam = ComputeTransmittance(r, mu, t, in s);
                // Transmittance from this point to sun (or ground)
                var dtSun = RaySphereDistance(ri, muSunI, s.PlanetRadius, atmRadius);
                var tSun = ComputeTransmittance(ri, muSunI, dtSun, in s);

                // Density at this point
                var hi = ri - s.PlanetRadius;
                var rayleighDensity = MathF.Exp(-MathF.Max(0f, hi) / s.RayleighScaleHeight);
                var mieDensity = MathF.Exp(-MathF.Max(0f, hi) / s.MieScaleHeight);

                // Phase functions
                var cosTheta = Vector3.Dot(viewDir, s.SunDirection);
                var phaseR = RayleighPhase(cosTheta);
                var phaseM = MiePhase(cosTheta, s.MiePhaseG);

                // Single scattering contribution
                var lumR = s.RayleighScattering * phaseR * rayleighDensity;
                var lumM = new Vector3(s.MieScattering) * phaseM * mieDensity;

                result += tCam * tSun * (lumR + lumM) * step;
            }

            // Scale by sun irradiance
            var sunColor = new Vector3(1f, 1f, 0.95f);
            return result * sunColor * s.SunIlluminance * 0.0001f;
        }

        /// <summary>
        /// Computes the multi-scattering approximation (2nd-order and higher)
        /// using a precomputed multi-scatter factor. This accounts for light
        /// that bounces multiple times in the atmosphere before reaching the eye.
        /// </summary>
        public static Vector3 ComputeMultiScattering(
            Vector3 worldPos, in AtmosphereSettings s, Vector3 multiScatterLUT)
        {
            // The multi-scatter LUT stores Psi = (1 + f_ms) / (1 - f_ms) where
            // f_ms is the fraction of light scattered more than once.
            // The final multi-scattered radiance is:
            //   L_ms = L_single * (Psi - 1)
            var r = worldPos.Length();
            if (r < s.PlanetRadius) r = s.PlanetRadius + 1f;
            var muSun = Vector3.Dot(Vector3.Normalize(worldPos), s.SunDirection);

            // Interpolate from the LUT (simplified - in practice the GPU samples it)
            var fMs = multiScatterLUT;
            var psi = Vector3.One + fMs;

            // Ground albedo bounce
            var groundBounce = s.GroundAlbedo * MathF.Max(0f, muSun) * 0.1f;

            return psi * groundBounce;
        }

        /// <summary>
        /// Computes aerial perspective transmittance and in-scattering for
        /// a pixel at distance <paramref name="distance"/> from the camera.
        /// </summary>
        public static (Vector3 Transmittance, Vector3 InScattering) ComputeAerialPerspective(
            Vector3 cameraPos, Vector3 viewDir, float distance, in AtmosphereSettings s)
        {
            var r = cameraPos.Length();
            if (r < s.PlanetRadius) r = s.PlanetRadius + 1f;
            var mu = Vector3.Dot(cameraPos, viewDir) / r;

            // Clamp distance to fog maximum
            var dt = MathF.Min(distance, s.FogMaxDistance);

            // Compute transmittance along the view ray
            var transmittance = ComputeTransmittance(r, mu, dt, in s);

            // Compute in-scattering (simplified single-scatter from sun)
            var muSun = Vector3.Dot(Vector3.Normalize(cameraPos), s.SunDirection);
            var cosTheta = Vector3.Dot(viewDir, s.SunDirection);
            var phaseR = RayleighPhase(cosTheta);
            var phaseM = MiePhase(cosTheta, s.MiePhaseG);

            var atmRadius = s.PlanetRadius + s.AtmosphereHeight;
            var steps = 16;
            var step = dt / steps;
            var inScatter = Vector3.Zero;

            for (var i = 0; i < steps; i++)
            {
                var t = (i + 0.5f) * step;
                var pos = cameraPos + viewDir * t;
                var ri = pos.Length();
                var hi = ri - s.PlanetRadius;
                var rd = MathF.Exp(-MathF.Max(0f, hi) / s.RayleighScaleHeight);
                var md = MathF.Exp(-MathF.Max(0f, hi) / s.MieScaleHeight);

                var muSunI = Vector3.Dot(Vector3.Normalize(pos), s.SunDirection);
                var dtSun = RaySphereDistance(ri, muSunI, s.PlanetRadius, atmRadius);
                var tSun = ComputeTransmittance(ri, muSunI, dtSun, in s);
                var tCam = ComputeTransmittance(r, mu, t, in s);

                var lumR = s.RayleighScattering * phaseR * rd;
                var lumM = new Vector3(s.MieScattering) * phaseM * md;
                inScatter += tCam * tSun * (lumR + lumM) * step * s.FogDensity;
            }

            return (transmittance, inScatter);
        }
    }

    /// <summary>
    /// Volumetric cloud noise generator. Produces 3D Perlin-Worley and Worley
    /// noise textures used as the shape and detail basis for cloud ray-marching.
    /// </summary>
    public sealed class CloudNoiseGenerator
    {
        /// <summary>Shape noise texture (3D, R8G8B8A8, 128^3).</summary>
        public RHITexture? ShapeNoise;
        /// <summary>Detail noise texture (3D, R8, 32^3).</summary>
        public RHITexture? DetailNoise;
        /// <summary>Weather texture (2D, R8G8B8A8, 1024^2) controlling coverage/type.</summary>
        public RHITexture? WeatherMap;

        /// <summary>Shape noise SRV.</summary>
        public RHIDescriptorHandle ShapeNoiseSRV;
        /// <summary>Detail noise SRV.</summary>
        public RHIDescriptorHandle DetailNoiseSRV;
        /// <summary>Weather map SRV.</summary>
        public RHIDescriptorHandle WeatherSRV;

        /// <summary>Shape noise resolution.</summary>
        public const int ShapeRes = 128;
        /// <summary>Detail noise resolution.</summary>
        public const int DetailRes = 32;
        /// <summary>Weather map resolution.</summary>
        public const int WeatherRes = 1024;

        private readonly int _seed;
        private readonly float[] _permutation = new float[512];

        /// <summary>Creates a noise generator with the given seed.</summary>
        public CloudNoiseGenerator(int seed = 42)
        {
            _seed = seed;
            InitPermutation();
        }

        private void InitPermutation()
        {
            var rng = new Random(_seed);
            var p = new float[256];
            for (var i = 0; i < 256; i++) p[i] = i;
            // Fisher-Yates shuffle
            for (var i = 255; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (p[i], p[j]) = (p[j], p[i]);
            }
            for (var i = 0; i < 512; i++)
                _permutation[i] = p[i & 255];
        }

        /// <summary>
        /// Generates the 3D shape noise texture on the CPU and uploads it to the GPU.
        /// Channel R = Perlin-Worley, G = Worley (freq 1), B = Worley (freq 2),
        /// A = Worley (freq 4).
        /// </summary>
        public void GenerateShapeNoise(RHIDevice device)
        {
            var size = ShapeRes;
            var data = new byte[size * size * size * 4];

            for (var z = 0; z < size; z++)
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var idx = (z * size * size + y * size + x) * 4;
                var fx = (float)x / size;
                var fy = (float)y / size;
                var fz = (float)z / size;

                // Perlin-Worley (channel R)
                var pw = PerlinNoise3D(fx * 4f, fy * 4f, fz * 4f) * 0.5f + 0.5f;
                var worley1 = WorleyNoise3D(fx * 4f, fy * 4f, fz * 4f, 1);
                var worley2 = WorleyNoise3D(fx * 8f, fy * 8f, fz * 8f, 1);
                var worley4 = WorleyNoise3D(fx * 16f, fy * 16f, fz * 16f, 1);

                // Combine Perlin-Worley
                var perlinWorley = Math.Clamp(pw - worley1 * 0.2f, 0f, 1f);

                data[idx + 0] = (byte)(perlinWorley * 255f);
                data[idx + 1] = (byte)(worley1 * 255f);
                data[idx + 2] = (byte)(worley2 * 255f);
                data[idx + 3] = (byte)(worley4 * 255f);
            }

            var desc = new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture3D,
                Format = RHIFormat.R8G8B8A8_UNorm,
                Width = size, Height = size, DepthOrArraySize = size,
                MipLevels = 1, SampleCount = 1, SampleQuality = 0,
                Flags = RHITextureFlags.ShaderResource
            };
            ShapeNoise = device.CreateTexture(desc, "CloudShapeNoise");
            var byteSpan = MemoryMarshal.AsBytes(data.AsSpan());
            device.UploadToTexture(ShapeNoise, byteSpan, size, size, 0, 0);
            ShapeNoiseSRV = device.CreateSRV(ShapeNoise);
        }

        /// <summary>
        /// Generates the 3D detail noise texture (Worley at high frequency).
        /// </summary>
        public void GenerateDetailNoise(RHIDevice device)
        {
            var size = DetailRes;
            var data = new byte[size * size * size];

            for (var z = 0; z < size; z++)
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var idx = z * size * size + y * size + x;
                var fx = (float)x / size;
                var fy = (float)y / size;
                var fz = (float)z / size;

                var w1 = WorleyNoise3D(fx * 6f, fy * 6f, fz * 6f, 1);
                var w2 = WorleyNoise3D(fx * 12f, fy * 12f, fz * 12f, 1);
                var detail = Math.Clamp(w1 * 0.625f + w2 * 0.25f, 0f, 1f);
                data[idx] = (byte)(detail * 255f);
            }

            var desc = new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture3D,
                Format = RHIFormat.R8_UNorm,
                Width = size, Height = size, DepthOrArraySize = size,
                MipLevels = 1, SampleCount = 1, SampleQuality = 0,
                Flags = RHITextureFlags.ShaderResource
            };
            DetailNoise = device.CreateTexture(desc, "CloudDetailNoise");
            var byteSpan = MemoryMarshal.AsBytes(data.AsSpan());
            device.UploadToTexture(DetailNoise, byteSpan, size, size, 0, 0);
            DetailNoiseSRV = device.CreateSRV(DetailNoise);
        }

        /// <summary>
        /// Generates the 2D weather map controlling cloud coverage, type, and height.
        /// R = coverage, G = cloud type (0=fluffy, 1=anvil), B = height gradient, A = unused.
        /// </summary>
        public void GenerateWeatherMap(RHIDevice device, float coverage = 0.55f)
        {
            var size = WeatherRes;
            var data = new byte[size * size * 4];
            var rng = new Random(_seed + 7);

            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var idx = (y * size + x) * 4;
                var fx = (float)x / size;
                var fy = (float)y / size;

                // Base coverage from large-scale Perlin
                var cov = PerlinNoise2D(fx * 3f, fy * 3f) * 0.5f + 0.5f;
                cov = Math.Clamp(cov * coverage * 2f, 0f, 1f);

                // Cloud type
                var type = PerlinNoise2D(fx * 1.5f + 100f, fy * 1.5f + 100f) * 0.5f + 0.5f;

                // Small-scale variation
                var detail = PerlinNoise2D(fx * 8f, fy * 8f) * 0.5f + 0.5f;
                cov = Math.Clamp(cov + (detail - 0.5f) * 0.15f, 0f, 1f);

                data[idx + 0] = (byte)(cov * 255f);
                data[idx + 1] = (byte)(type * 255f);
                data[idx + 2] = (byte)(detail * 255f);
                data[idx + 3] = 255;
            }

            var desc = new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R8G8B8A8_UNorm,
                Width = size, Height = size, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1, SampleQuality = 0,
                Flags = RHITextureFlags.ShaderResource
            };
            WeatherMap = device.CreateTexture(desc, "CloudWeatherMap");
            var byteSpan = MemoryMarshal.AsBytes(data.AsSpan());
            device.UploadToTexture(WeatherMap, byteSpan, size, size, 0, 0);
            WeatherSRV = device.CreateSRV(WeatherMap);
        }

        // ── Noise primitives ──────────────────────────────────────────

        private float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);
        private float Lerp(float a, float b, float t) => a + t * (b - a);

        private float Grad(int hash, float x, float y, float z)
        {
            var h = hash & 15;
            var u = h < 8 ? x : y;
            var v = h < 4 ? y : (h == 12 || h == 14 ? x : z);
            return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
        }

        /// <summary>3D Perlin noise in [-1,1].</summary>
        public float PerlinNoise3D(float x, float y, float z)
        {
            var xi = (int)MathF.Floor(x) & 255;
            var yi = (int)MathF.Floor(y) & 255;
            var zi = (int)MathF.Floor(z) & 255;
            var xf = x - MathF.Floor(x);
            var yf = y - MathF.Floor(y);
            var zf = z - MathF.Floor(z);
            var u = Fade(xf);
            var v = Fade(yf);
            var w = Fade(zf);

            var p = _permutation;
            var a = (int)p[xi] + yi;
            var aa = (int)p[a] + zi;
            var ab = (int)p[a + 1] + zi;
            var b = (int)p[xi + 1] + yi;
            var ba = (int)p[b] + zi;
            var bb = (int)p[b + 1] + zi;

            var x1 = Lerp(Grad((int)p[aa], xf, yf, zf), Grad((int)p[ba], xf - 1, yf, zf), u);
            var x2 = Lerp(Grad((int)p[ab], xf, yf - 1, zf), Grad((int)p[bb], xf - 1, yf - 1, zf), u);
            var y1 = Lerp(x1, x2, v);

            x1 = Lerp(Grad((int)p[aa + 1], xf, yf, zf - 1), Grad((int)p[ba + 1], xf - 1, yf, zf - 1), u);
            x2 = Lerp(Grad((int)p[ab + 1], xf, yf - 1, zf - 1), Grad((int)p[bb + 1], xf - 1, yf - 1, zf - 1), u);
            var y2 = Lerp(x1, x2, v);

            return Lerp(y1, y2, w);
        }

        /// <summary>2D Perlin noise in [-1,1].</summary>
        public float PerlinNoise2D(float x, float y)
        {
            return PerlinNoise3D(x, y, 0f);
        }

        /// <summary>
        /// 3D Worley (cellular) noise. Returns the F1 distance to the nearest
        /// feature point, normalized to [0,1].
        /// </summary>
        public float WorleyNoise3D(float x, float y, float z, int seed)
        {
            var xi = (int)MathF.Floor(x);
            var yi = (int)MathF.Floor(y);
            var zi = (int)MathF.Floor(z);
            var minDist = float.MaxValue;

            for (var dz = -1; dz <= 1; dz++)
            for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                var cx = xi + dx;
                var cy = yi + dy;
                var cz = zi + dz;

                // Hash-based feature point
                var h = Hash3(cx, cy, cz, seed);
                var fx = dx + (h & 0xFF) / 255f;
                var fy = dy + ((h >> 8) & 0xFF) / 255f;
                var fz = dz + ((h >> 16) & 0xFF) / 255f;

                var dist = (fx - (x - xi)) * (fx - (x - xi)) +
                           (fy - (y - yi)) * (fy - (y - yi)) +
                           (fz - (z - zi)) * (fz - (z - zi));
                if (dist < minDist) minDist = dist;
            }

            return Math.Clamp(MathF.Sqrt(minDist), 0f, 1f);
        }

        private int Hash3(int x, int y, int z, int seed)
        {
            var h = (x * 374761393 + y * 668265263 + z * 1442695040 + seed * 2246822519) & 0x7FFFFFFF;
            h = (h ^ (h >> 13)) * 1274126177;
            return (int)((h ^ (h >> 16)) & 0x7FFFFFFF);
        }
    }

    /// <summary>
    /// Volumetric cloud renderer using ray-marching through 3D noise textures.
    /// Implements forward-scattering Beer-Lambert extinction with Henyey-Greenstein
    /// phase, powder effect, and ambient lighting from the cloud layer.
    /// </summary>
    public sealed class VolumetricCloudRenderer
    {
        private readonly RHIDevice _device;
        private readonly CloudNoiseGenerator _noise;
        private AtmosphereSettings _settings;

        /// <summary>Cloud colour output SRV (read by the composite pass).</summary>
        public RHIDescriptorHandle CloudColorSRV;
        /// <summary>Cloud alpha output SRV.</summary>
        public RHIDescriptorHandle CloudAlphaSRV;

        private RHITexture? _cloudColor;
        private RHITexture? _cloudAlpha;
        private RHIRootSignature? _rootSig;
        private RHIPipelineState? _marchPSO;

        /// <summary>Creates the cloud renderer.</summary>
        public VolumetricCloudRenderer(RHIDevice device, CloudNoiseGenerator noise, AtmosphereSettings settings)
        {
            _device = device;
            _noise = noise;
            _settings = settings;
        }

        /// <summary>Allocates the cloud render targets at the given resolution.</summary>
        public void AllocateTargets(int width, int height)
        {
            var colorDesc = new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R16G16B16A16_Float,
                Width = width, Height = height, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1, SampleQuality = 0,
                Flags = RHITextureFlags.RenderTarget | RHITextureFlags.UnorderedAccess | RHITextureFlags.ShaderResource
            };
            _cloudColor = _device.CreateTexture(colorDesc, "CloudColor");
            CloudColorSRV = _device.CreateSRV(_cloudColor);

            var alphaDesc = new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R8_UNorm,
                Width = width, Height = height, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1, SampleQuality = 0,
                Flags = RHITextureFlags.RenderTarget | RHITextureFlags.UnorderedAccess | RHITextureFlags.ShaderResource
            };
            _cloudAlpha = _device.CreateTexture(alphaDesc, "CloudAlpha");
            CloudAlphaSRV = _device.CreateSRV(_cloudAlpha);
        }

        /// <summary>
        /// Computes cloud density at a given world position using the shape and
        /// detail noise textures. This is the CPU reference for the GPU shader.
        /// </summary>
        public float ComputeCloudDensity(Vector3 worldPos, float heightFraction)
        {
            // Sample shape noise (simulated - GPU samples the 3D texture)
            var shapeU = worldPos.X * 0.001f;
            var shapeV = worldPos.Y * 0.001f;
            var shapeW = worldPos.Z * 0.001f;
            var perlinWorley = _noise.PerlinNoise3D(shapeU * 4, shapeV * 4, shapeW * 4) * 0.5f + 0.5f;
            var worley1 = _noise.WorleyNoise3D(shapeU * 4, shapeV * 4, shapeW * 4, 1);
            var worley2 = _noise.WorleyNoise3D(shapeU * 8, shapeV * 8, shapeW * 8, 1);
            var worley4 = _noise.WorleyNoise3D(shapeU * 16, shapeV * 16, shapeW * 16, 1);

            // Combine into shape density
            var shape = Math.Clamp(perlinWorley - worley1 * 0.2f - worley2 * 0.1f - worley4 * 0.05f, 0f, 1f);

            // Height gradient (remap based on altitude within cloud layer)
            var heightGradient = HeightGradient(heightFraction);

            // Coverage from weather map
            var coverage = _settings.CloudCoverage;
            var cloudType = 0.5f; // simulated weather map

            // Apply coverage modulation
            var density = Math.Clamp(shape - (1f - coverage), 0f, 1f);
            density *= heightGradient;

            // Erode with detail noise
            var detail = _noise.WorleyNoise3D(shapeU * 6, shapeV * 6, shapeW * 6, 2);
            detail = Math.Clamp(detail * 0.5f, 0f, 1f);
            density = Math.Clamp(density - detail * 0.25f * heightFraction, 0f, 1f);

            return density * _settings.CloudDensity;
        }

        /// <summary>
        /// Computes the height gradient for cloud density shaping.
        /// Bottom: dense, middle: variable, top: wispy.
        /// </summary>
        public float HeightGradient(float h)
        {
            // h in [0,1] where 0 = cloud base, 1 = cloud top
            var bottom = MathF.Exp(-h * 3f);
            var top = MathF.Exp(-(1f - h) * 5f);
            return bottom * (1f - h * 0.5f) + top * h * 0.3f;
        }

        /// <summary>
        /// Henyey-Greenstein phase function for cloud forward scattering.
        /// </summary>
        public float HenyeyGreenstein(float cosTheta, float g)
        {
            var g2 = g * g;
            return (1f - g2) / MathF.Pow(1f + g2 - 2f * g * cosTheta, 1.5f) / (4f * MathF.PI);
        }

        /// <summary>
        /// Powder effect: clouds appear denser at grazing angles.
        /// </summary>
        public float PowderEffect(float density, float cosTheta)
        {
            var powder = 1f - MathF.Exp(-density * 2f);
            return powder + (1f - powder) * MathF.Max(0f, cosTheta);
        }

        /// <summary>
        /// Ray-marches a single cloud ray (CPU reference). The GPU dispatch
        /// performs this per-pixel in a compute shader.
        /// </summary>
        public (Vector3 Color, float Alpha) RayMarchCloud(
            Vector3 rayOrigin, Vector3 rayDir, Vector3 sunDir,
            Vector3 sunColor, Vector3 ambientColor)
        {
            // Intersect ray with cloud layer slab
            var cloudBottom = _settings.CloudAltitudeStart;
            var cloudTop = cloudBottom + _settings.CloudThickness;

            // Find entry and exit points in the cloud slab
            var (tEnter, tExit) = IntersectSlab(rayOrigin, rayDir, cloudBottom, cloudTop);
            if (tEnter < 0f || tExit <= tEnter)
                return (Vector3.Zero, 0f);

            var dt = (tExit - tEnter) / _settings.CloudRayMarchSteps;
            var transmittance = 1f;
            var scatteredLight = Vector3.Zero;
            var cosTheta = Vector3.Dot(rayDir, sunDir);

            // Beer-Penumbra: light march uses fewer steps
            var lightSteps = 6;
            var lightDt = _settings.CloudThickness / lightSteps;

            for (var i = 0; i < _settings.CloudRayMarchSteps; i++)
            {
                var t = tEnter + (i + 0.5f) * dt;
                var pos = rayOrigin + rayDir * t;
                var heightFraction = (pos.Y - cloudBottom) / _settings.CloudThickness;

                var density = ComputeCloudDensity(pos, heightFraction);
                if (density <= 0.001f) continue;

                // Extinction (Beer-Lambert)
                var extinction = density * dt * 0.3f;
                var sampleTransmittance = MathF.Exp(-extinction);

                // Light march toward sun
                var lightTransmittance = 1f;
                for (var j = 0; j < lightSteps; j++)
                {
                    var lt = (j + 0.5f) * lightDt;
                    var lpos = pos + sunDir * lt;
                    var lh = (lpos.Y - cloudBottom) / _settings.CloudThickness;
                    if (lh < 0f || lh > 1f) break;
                    var ld = ComputeCloudDensity(lpos, lh);
                    lightTransmittance *= MathF.Exp(-ld * lightDt * 0.3f);
                }

                // Phase function (forward scattering)
                var phase = HenyeyGreenstein(cosTheta, 0.2f);
                var powder = PowderEffect(density, cosTheta);

                // Scattered light = sun * phase * powder * lightTransmittance + ambient
                var luminance = sunColor * phase * powder * lightTransmittance +
                                ambientColor * (1f - heightFraction) * 0.5f;

                scatteredLight += luminance * transmittance * (1f - sampleTransmittance);
                transmittance *= sampleTransmittance;

                if (transmittance < 0.01f) break;
            }

            var alpha = 1f - transmittance;
            var color = alpha > 0.001f ? scatteredLight / alpha : Vector3.Zero;
            return (color, alpha);
        }

        private (float enter, float exit) IntersectSlab(Vector3 origin, Vector3 dir, float yBottom, float yTop)
        {
            var tEnter = 0f;
            var tExit = float.MaxValue;

            if (MathF.Abs(dir.Y) > 1e-6f)
            {
                var tBottom = (yBottom - origin.Y) / dir.Y;
                var tTop = (yTop - origin.Y) / dir.Y;
                var tNear = MathF.Min(tBottom, tTop);
                var tFar = MathF.Max(tBottom, tTop);
                tEnter = MathF.Max(tEnter, tNear);
                tExit = MathF.Min(tExit, tFar);
            }
            else
            {
                if (origin.Y < yBottom || origin.Y > yTop)
                    return (-1f, -1f);
            }

            return (tEnter, tExit);
        }

        /// <summary>
        /// Dispatches the GPU cloud ray-march compute shader over the screen.
        /// </summary>
        public unsafe void Render(RHICommandList cmd, int width, int height,
            Vector3 cameraPos, Matrix4x4 invViewProj, float time)
        {
            if (_cloudColor == null || _cloudAlpha == null) return;
            if (_marchPSO == null)
            {
                BuildPipeline();
                if (_marchPSO == null) return;
            }

            cmd.SetPipelineState(_marchPSO);
            cmd.SetComputeRootSignature(_rootSig!);

            // Bind noise textures
            cmd.SetComputeRootDescriptorTable(1, _noise.ShapeNoiseSRV);
            cmd.SetComputeRootDescriptorTable(2, _noise.DetailNoiseSRV);
            cmd.SetComputeRootDescriptorTable(3, _noise.WeatherSRV);

            // Bind output UAVs
            var colorUAV = _device.CreateUAV(_cloudColor);
            var alphaUAV = _device.CreateUAV(_cloudAlpha);
            cmd.SetComputeRootDescriptorTable(4, colorUAV);
            cmd.SetComputeRootDescriptorTable(5, alphaUAV);

            // Upload constants
            var cb = new CloudMarchCB
            {
                CameraPos = cameraPos,
                InvViewProj = invViewProj,
                SunDirection = _settings.SunDirection,
                CloudBottom = _settings.CloudAltitudeStart,
                CloudTop = _settings.CloudAltitudeStart + _settings.CloudThickness,
                Steps = _settings.CloudRayMarchSteps,
                Coverage = _settings.CloudCoverage,
                Density = _settings.CloudDensity,
                Time = time,
                WindDir = _settings.CloudWindDirection * _settings.CloudWindSpeed,
                Resolution = new Vector2(width, height)
            };
            cmd.SetComputeRoot32BitConstants(0u, (uint)(sizeof(CloudMarchCB) / 4), in cb, 0u);

            var groupsX = (width + 7) / 8;
            var groupsY = (height + 7) / 8;
            cmd.Dispatch(groupsX, groupsY, 1);
        }

        private void BuildPipeline()
        {
            var rootDesc = new RHIRootSignatureDesc
            {
                Parameters = new[]
                {
                    RootParam.Constants(1, 0),    // CB
                    RootParam.SRV(1, 0),          // Shape noise
                    RootParam.SRV(1, 1),          // Detail noise
                    RootParam.SRV(1, 2),          // Weather map
                    RootParam.UAV(1, 0),          // Color output
                    RootParam.UAV(1, 1),          // Alpha output
                },
                AllowInputAssembler = false
            };
            _rootSig = _device.CreateRootSignature(rootDesc);

            var psoDesc = new RHIComputePipelineDesc
            {
                Name = "CloudMarch",
                CS = ShaderManager.Instance.GetBytecode("Atmosphere/Atmosphere", "CloudMarchCS", RHIShaderStage.Compute)
            };
            _marchPSO = _device.CreateComputePipelineState(psoDesc);
        }
    }

    /// <summary>
    /// Cloud shadow renderer. Projects cloud density onto the ground plane
    /// using the sun direction to compute shadow coordinates.
    /// </summary>
    public sealed class CloudShadowRenderer
    {
        private readonly RHIDevice _device;
        private readonly AtmosphereSettings _settings;

        /// <summary>Shadow texture SRV (R8, sampled by the lighting pass).</summary>
        public RHIDescriptorHandle ShadowSRV;
        private RHITexture? _shadowTex;

        /// <summary>Creates the cloud shadow renderer.</summary>
        public CloudShadowRenderer(RHIDevice device, AtmosphereSettings settings)
        {
            _device = device;
            _settings = settings;
        }

        /// <summary>Allocates the cloud shadow texture at a given world-space resolution.</summary>
        public void Allocate(int texSize, float worldSize)
        {
            var desc = new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R8_UNorm,
                Width = texSize, Height = texSize, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1, SampleQuality = 0,
                Flags = RHITextureFlags.UnorderedAccess | RHITextureFlags.ShaderResource
            };
            _shadowTex = _device.CreateTexture(desc, "CloudShadow");
            ShadowSRV = _device.CreateSRV(_shadowTex);
        }

        /// <summary>
        /// Computes the cloud shadow factor at a given world position.
        /// Returns 0 = fully shadowed, 1 = no shadow.
        /// </summary>
        public float ComputeShadow(Vector3 worldPos, VolumetricCloudRenderer clouds)
        {
            // Project from world position toward the sun to the cloud layer
            var sunDir = _settings.SunDirection;
            var cloudHeight = _settings.CloudAltitudeStart + _settings.CloudThickness * 0.5f;

            // Compute intersection with cloud layer plane
            if (MathF.Abs(sunDir.Y) < 1e-6f) return 1f;
            var t = (cloudHeight - worldPos.Y) / sunDir.Y;
            if (t < 0f) return 1f;

            var cloudPos = worldPos + sunDir * t;
            var heightFraction = 0.5f;
            var density = clouds.ComputeCloudDensity(cloudPos, heightFraction);

            // Beer-Lambert shadow
            return MathF.Exp(-density * 2f);
        }
    }

    /// <summary>
    /// God-ray / light-shaft renderer. Uses a screen-space radial blur
    /// centred on the sun's screen position, sampling the depth buffer
    /// to create volumetric light scattering.
    /// </summary>
    public sealed class GodRayRenderer
    {
        private readonly RHIDevice _device;
        private readonly AtmosphereSettings _settings;

        /// <summary>God-ray output SRV (additive blend onto scene).</summary>
        public RHIDescriptorHandle GodRaySRV;
        private RHITexture? _godRayTex;
        private RHITexture? _tempTex;
        private RHIRootSignature? _rootSig;
        private RHIPipelineState? _godRayPSO;

        /// <summary>Creates the god-ray renderer.</summary>
        public GodRayRenderer(RHIDevice device, AtmosphereSettings settings)
        {
            _device = device;
            _settings = settings;
        }

        /// <summary>Allocates god-ray textures at the given resolution.</summary>
        public void Allocate(int width, int height)
        {
            var desc = new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R16G16B16A16_Float,
                Width = width, Height = height, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1, SampleQuality = 0,
                Flags = RHITextureFlags.RenderTarget | RHITextureFlags.UnorderedAccess | RHITextureFlags.ShaderResource
            };
            _godRayTex = _device.CreateTexture(desc, "GodRays");
            GodRaySRV = _device.CreateSRV(_godRayTex);
            _tempTex = _device.CreateTexture(desc, "GodRaysTemp");
        }

        /// <summary>
        /// Renders god rays by sampling the scene colour along radial lines
        /// toward the sun's screen-space position. Each sample is attenuated
        /// by a decay factor and accumulated.
        /// </summary>
        public unsafe void Render(RHICommandList cmd, RHIDescriptorHandle sceneColorSRV,
            RHIDescriptorHandle depthSRV, Vector2 sunScreenPos, int width, int height)
        {
            if (_godRayTex == null || _godRayPSO == null)
            {
                BuildPipeline();
                if (_godRayPSO == null) return;
            }

            var godRayUAV = _device.CreateUAV(_godRayTex);
            cmd.SetPipelineState(_godRayPSO);
            cmd.SetComputeRootSignature(_rootSig!);
            cmd.SetComputeRootDescriptorTable(1, sceneColorSRV);
            cmd.SetComputeRootDescriptorTable(2, depthSRV);
            cmd.SetComputeRootDescriptorTable(3, godRayUAV);

            var cb = new GodRayCB
            {
                SunScreenPos = sunScreenPos,
                Samples = _settings.GodRaySamples,
                Intensity = _settings.GodRayIntensity,
                Decay = _settings.GodRayDecay,
                Exposure = _settings.GodRayExposure,
                Resolution = new Vector2(width, height),
                InvResolution = new Vector2(1f / width, 1f / height)
            };
            cmd.SetComputeRoot32BitConstants(0u, (uint)(sizeof(GodRayCB) / 4), in cb, 0u);

            var groupsX = (width + 7) / 8;
            var groupsY = (height + 7) / 8;
            cmd.Dispatch(groupsX, groupsY, 1);
        }

        /// <summary>
        /// CPU reference for the god-ray accumulation at a single pixel.
        /// </summary>
        public Vector3 ComputeGodRayPixel(
            Vector2 uv, Vector2 sunScreenPos,
            Func<Vector2, Vector3> sampleScene,
            int samples, float decay, float exposure)
        {
            var delta = (sunScreenPos - uv) / samples;
            var illumination = Vector3.Zero;
            var pos = uv;

            for (var i = 0; i < samples; i++)
            {
                pos += delta;
                var color = sampleScene(pos);
                illumination += color * exposure;
                exposure *= decay;
            }

            return illumination / samples;
        }

        private void BuildPipeline()
        {
            var rootDesc = new RHIRootSignatureDesc
            {
                Parameters = new[]
                {
                    RootParam.Constants(1, 0),
                    RootParam.SRV(1, 0),
                    RootParam.SRV(1, 1),
                    RootParam.UAV(1, 0),
                },
                AllowInputAssembler = false
            };
            _rootSig = _device.CreateRootSignature(rootDesc);

            var psoDesc = new RHIComputePipelineDesc
            {
                Name = "GodRay",
                CS = ShaderManager.Instance.GetBytecode("Atmosphere/Atmosphere", "GodRayCS", RHIShaderStage.Compute)
            };
            _godRayPSO = _device.CreateComputePipelineState(psoDesc);
        }
    }

    /// <summary>
    /// FFT-based ocean water renderer. Generates a Phillips wave spectrum,
    /// performs inverse FFT to obtain height/displacement maps, and renders
    /// the ocean surface with Gerstner wave displacement and Fresnel reflection.
    /// </summary>
    public sealed class OceanRenderer
    {
        private readonly RHIDevice _device;
        private readonly AtmosphereSettings _settings;

        // FFT working buffers
        private RHIBuffer? _heightFieldSpectrum;   // complex (R32G32_Float, Re/Im)
        private RHIBuffer? _displacementSpectrumX; // horizontal displacement
        private RHIBuffer? _displacementSpectrumZ; // vertical displacement
        private RHITexture? _heightMap;
        private RHITexture? _normalMap;
        private RHITexture? _displacementMap;

        /// <summary>Height map SRV.</summary>
        public RHIDescriptorHandle HeightMapSRV;
        /// <summary>Normal map SRV.</summary>
        public RHIDescriptorHandle NormalMapSRV;
        /// <summary>Displacement map SRV.</summary>
        public RHIDescriptorHandle DisplacementSRV;

        private RHIRootSignature? _spectrumRootSig;
        private RHIPipelineState? _spectrumPSO;
        private RHIRootSignature? _fftRootSig;
        private RHIPipelineState? _fftPSO;

        // CPU-side spectrum for reference computation
        private readonly Complex[] _h0;        // initial spectrum (frequency domain)
        private readonly Complex[] _h0Conj;    // conjugate of initial spectrum
        private readonly float[] _phillips;    // Phillips spectrum magnitude

        /// <summary>FFT grid size (N x N).</summary>
        public readonly int N;
        /// <summary>World-space patch length.</summary>
        public readonly float PatchSize;

        /// <summary>Creates the ocean renderer.</summary>
        public OceanRenderer(RHIDevice device, AtmosphereSettings settings)
        {
            _device = device;
            _settings = settings;
            N = settings.OceanFFTSize;
            PatchSize = settings.OceanPatchSize;
            _h0 = new Complex[N * N];
            _h0Conj = new Complex[N * N];
            _phillips = new float[N * N];
            GeneratePhillipsSpectrum();
        }

        /// <summary>
        /// Generates the Phillips wave spectrum H0(k) at t=0.
        /// The Phillips spectrum models wind-driven ocean waves:
        ///   P(k) = A * exp(-1/(kL)^2) / k^4 * |k_hat . wind_hat|^2
        /// where L = V^2/g is the largest possible wave.
        /// </summary>
        public void GeneratePhillipsSpectrum()
        {
            var rng = new Random(1234);
            var windSpeed = _settings.OceanWindSpeed;
            var windDir = Vector2.Normalize(_settings.OceanWindDirection);
            var g = 9.81f;
            var L = windSpeed * windSpeed / g; // largest wave from wind speed
            var A = _settings.OceanAmplitude * 1e-3f;

            for (var y = 0; y < N; y++)
            for (var x = 0; x < N; x++)
            {
                var kx = (x - N / 2) * (2f * MathF.PI / PatchSize);
                var ky = (y - N / 2) * (2f * MathF.PI / PatchSize);
                var kMag = MathF.Sqrt(kx * kx + ky * ky);

                if (kMag < 1e-6f)
                {
                    _phillips[y * N + x] = 0f;
                    _h0[y * N + x] = Complex.Zero;
                    _h0Conj[y * N + x] = Complex.Zero;
                    continue;
                }

                var kxHat = kx / kMag;
                var kyHat = ky / kMag;

                // Phillips spectrum
                var dot = kxHat * windDir.X + kyHat * windDir.Y;
                var phillips = A * MathF.Exp(-1f / (kMag * L * (kMag * L))) /
                               (kMag * kMag * kMag * kMag) * (dot * dot);

                // Suppress small waves
                var l = L * 0.01f;
                phillips *= MathF.Exp(-kMag * kMag * l * l);

                _phillips[y * N + x] = phillips;

                // Generate complex Gaussian random number (Box-Muller)
                var u1 = rng.NextDouble();
                var u2 = rng.NextDouble();
                var r = MathF.Sqrt(-2f * (float)Math.Log(u1 + 1e-10)) * MathF.Sqrt(phillips);
                var theta = (float)(u2 * 2f * Math.PI);
                _h0[y * N + x] = new Complex(r * MathF.Cos(theta), r * MathF.Sin(theta));
                _h0Conj[y * N + x] = Complex.Conjugate(_h0[y * N + x]);
            }
        }

        /// <summary>
        /// Computes the height field spectrum H(k, t) at time t using the
        /// dispersion relation omega(k) = sqrt(g * |k|).
        ///   H(k, t) = H0(k) * exp(i*omega*t) + conj(H0(-k)) * exp(-i*omega*t)
        /// </summary>
        public Complex[] ComputeHeightSpectrum(float time)
        {
            var spectrum = new Complex[N * N];
            var g = 9.81f;
            var ts = time * _settings.OceanTimeScale;

            for (var y = 0; y < N; y++)
            for (var x = 0; x < N; x++)
            {
                var kx = (x - N / 2) * (2f * MathF.PI / PatchSize);
                var ky = (y - N / 2) * (2f * MathF.PI / PatchSize);
                var kMag = MathF.Sqrt(kx * kx + ky * ky);
                if (kMag < 1e-6f) continue;

                var omega = MathF.Sqrt(g * kMag);
                var expwt = new Complex(MathF.Cos(omega * ts), MathF.Sin(omega * ts));
                var expmwt = new Complex(MathF.Cos(-omega * ts), MathF.Sin(-omega * ts));

                // Index of -k: flip both x and y around centre
                var mx = N - x;
                var my = N - y;
                var idx = y * N + x;
                var midx = (my % N) * N + (mx % N);

                spectrum[idx] = _h0[idx] * expwt + _h0Conj[midx] * expmwt;
            }

            return spectrum;
        }

        /// <summary>
        /// Computes the horizontal displacement spectrum using the Choppy wave
        /// modification: D(k, t) = -i * k_hat / |k| * H(k, t).
        /// </summary>
        public (Complex[] Dx, Complex[] Dz) ComputeDisplacementSpectrum(Complex[] heightSpectrum)
        {
            var dx = new Complex[N * N];
            var dz = new Complex[N * N];
            var choppy = _settings.OceanChoppiness;

            for (var y = 0; y < N; y++)
            for (var x = 0; x < N; x++)
            {
                var kx = (x - N / 2) * (2f * MathF.PI / PatchSize);
                var ky = (y - N / 2) * (2f * MathF.PI / PatchSize);
                var kMag = MathF.Sqrt(kx * kx + ky * ky);
                if (kMag < 1e-6f) continue;

                var idx = y * N + x;
                var h = heightSpectrum[idx];
                // -i * k_hat / |k|
                var factor = -choppy / kMag;
                dx[idx] = new Complex(factor * kx * h.Imaginary, -factor * kx * h.Real);
                dz[idx] = new Complex(factor * ky * h.Imaginary, -factor * ky * h.Real);
            }

            return (dx, dz);
        }

        /// <summary>
        /// Performs an in-place 1D Cooley-Tukey radix-2 FFT on a complex array.
        /// Used as the CPU reference for the GPU butterfly FFT.
        /// </summary>
        public static void FFT1D(Complex[] data, bool inverse)
        {
            var n = data.Length;
            if (n <= 1) return;

            // Bit-reversal permutation
            for (int i = 1, j = 0; i < n; i++)
            {
                var bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1)
                    j ^= bit;
                j ^= bit;
                if (i < j)
                    (data[i], data[j]) = (data[j], data[i]);
            }

            // Butterfly
            for (var len = 2; len <= n; len <<= 1)
            {
                var halfLen = len >> 1;
                var angle = (inverse ? 2f : -2f) * MathF.PI / len;
                var wlen = new Complex(MathF.Cos(angle), MathF.Sin(angle));

                for (var i = 0; i < n; i += len)
                {
                    var w = Complex.One;
                    for (var j = 0; j < halfLen; j++)
                    {
                        var u = data[i + j];
                        var v = data[i + j + halfLen] * w;
                        data[i + j] = u + v;
                        data[i + j + halfLen] = u - v;
                        w *= wlen;
                    }
                }
            }

            if (inverse)
            {
                var invN = 1f / n;
                for (var i = 0; i < n; i++)
                    data[i] *= invN;
            }
        }

        /// <summary>
        /// Performs a 2D inverse FFT on the height spectrum to obtain the
        /// height field, and on the displacement spectra to obtain displacement.
        /// Returns (heightField, displacementX, displacementZ, normals).
        /// </summary>
        public (float[] Height, Vector2[] Displacement, Vector3[] Normals) ComputeOceanField(float time)
        {
            var heightSpectrum = ComputeHeightSpectrum(time);
            var (dxSpec, dzSpec) = ComputeDisplacementSpectrum(heightSpectrum);

            // 2D IFFT: apply 1D IFFT to each row, then each column
            for (var y = 0; y < N; y++)
            {
                var row = new Complex[N];
                var rowDx = new Complex[N];
                var rowDz = new Complex[N];
                for (var x = 0; x < N; x++)
                {
                    row[x] = heightSpectrum[y * N + x];
                    rowDx[x] = dxSpec[y * N + x];
                    rowDz[x] = dzSpec[y * N + x];
                }
                FFT1D(row, true);
                FFT1D(rowDx, true);
                FFT1D(rowDz, true);
                for (var x = 0; x < N; x++)
                {
                    heightSpectrum[y * N + x] = row[x];
                    dxSpec[y * N + x] = rowDx[x];
                    dzSpec[y * N + x] = rowDz[x];
                }
            }

            for (var x = 0; x < N; x++)
            {
                var col = new Complex[N];
                var colDx = new Complex[N];
                var colDz = new Complex[N];
                for (var y = 0; y < N; y++)
                {
                    col[y] = heightSpectrum[y * N + x];
                    colDx[y] = dxSpec[y * N + x];
                    colDz[y] = dzSpec[y * N + x];
                }
                FFT1D(col, true);
                FFT1D(colDx, true);
                FFT1D(colDz, true);
                for (var y = 0; y < N; y++)
                {
                    heightSpectrum[y * N + x] = col[y];
                    dxSpec[y * N + x] = colDx[y];
                    dzSpec[y * N + x] = colDz[y];
                }
            }

            // Extract real parts and compute normals via finite differences
            var heights = new float[N * N];
            var displacements = new Vector2[N * N];
            var normals = new Vector3[N * N];

            for (var i = 0; i < N * N; i++)
            {
                heights[i] = heightSpectrum[i].Real;
                displacements[i] = new Vector2(dxSpec[i].Real, dzSpec[i].Real);
            }

            // Compute normals from height field
            var dx = PatchSize / N;
            for (var y = 0; y < N; y++)
            for (var x = 0; x < N; x++)
            {
                var xp = (x + 1) % N;
                var xm = (x + N - 1) % N;
                var yp = (y + 1) % N;
                var ym = (y + N - 1) % N;

                var hL = heights[y * N + xm];
                var hR = heights[y * N + xp];
                var hD = heights[ym * N + x];
                var hU = heights[yp * N + x];

                var nx = (hL - hR) / (2f * dx);
                var nz = (hD - hU) / (2f * dx);
                var ny = 1f;
                normals[y * N + x] = Vector3.Normalize(new Vector3(nx, ny, nz));
            }

            return (heights, displacements, normals);
        }

        /// <summary>
        /// Computes Gerstner wave displacement for a single wave at a given
        /// position and time. Used for LOD or simplified water rendering.
        /// </summary>
        public static Vector3 GerstnerWave(
            Vector2 position, Vector2 waveDir, float amplitude,
            float wavelength, float speed, float time, float steepness)
        {
            var k = 2f * MathF.PI / wavelength;
            var c = MathF.Sqrt(9.81f / k) * speed;
            var f = k * (Vector2.Dot(waveDir, position) - c * time);
            var QA = steepness * amplitude;

            var dx = QA * waveDir.X * MathF.Cos(f);
            var dz = QA * waveDir.Y * MathF.Cos(f);
            var dy = amplitude * MathF.Sin(f);

            return new Vector3(dx, dy, dz);
        }

        /// <summary>
        /// Computes the sum of multiple Gerstner waves for a given position.
        /// </summary>
        public static (Vector3 Displacement, Vector3 Normal) SumGerstnerWaves(
            Vector2 position, GerstnerWaveParams[] waves, float time)
        {
            var displacement = Vector3.Zero;
            var normal = new Vector3(0f, 1f, 0f);

            foreach (var w in waves)
            {
                var d = GerstnerWave(position, w.Direction, w.Amplitude,
                    w.Wavelength, w.Speed, time, w.Steepness);
                displacement += d;

                // Compute normal contribution
                var k = 2f * MathF.PI / w.Wavelength;
                var c = MathF.Sqrt(9.81f / k) * w.Speed;
                var f = k * (Vector2.Dot(w.Direction, position) - c * time);
                var QA = w.Steepness * w.Amplitude;

                normal.X -= QA * w.Direction.X * k * MathF.Cos(f);
                normal.Z -= QA * w.Direction.Y * k * MathF.Cos(f);
                normal.Y -= w.Amplitude * k * MathF.Sin(f);
            }

            return (displacement, Vector3.Normalize(normal));
        }

        /// <summary>
        /// Computes the Fresnel reflectance for water using the Schlick
        /// approximation with F0 = 0.02 for water.
        /// </summary>
        public static float FresnelWater(float cosTheta)
        {
            const float f0 = 0.02f;
            return f0 + (1f - f0) * MathF.Pow(1f - cosTheta, 5f);
        }

        /// <summary>
        /// Computes the ocean surface BRDF combining Fresnel reflection,
        /// subsurface scattering approximation, and sun specular.
        /// </summary>
        public Vector3 ComputeOceanBRDF(
            Vector3 viewDir, Vector3 normal, Vector3 sunDir,
            Vector3 sunColor, Vector3 skyColor, float depth)
        {
            var NdotV = MathF.Max(0f, Vector3.Dot(normal, viewDir));
            var NdotL = MathF.Max(0f, Vector3.Dot(normal, sunDir));
            var fresnel = FresnelWater(NdotV);

            // Deep/shallow colour blend
            var waterColor = Vector3.Lerp(_settings.OceanShallowColor, _settings.OceanDeepColor,
                Math.Clamp(depth / 10f, 0f, 1f));

            // Sun specular (GGX-ish sharp highlight)
            var H = Vector3.Normalize(viewDir + sunDir);
            var NdotH = MathF.Max(0f, Vector3.Dot(normal, H));
            var specular = MathF.Pow(NdotH, 256f) * 0.8f;

            // Subsurface scattering approximation (light through wave crest)
            var sss = MathF.Max(0f, Vector3.Dot(-sunDir, viewDir)) * MathF.Pow(NdotL, 2f) * 0.3f;

            var reflection = skyColor * fresnel;
            var refraction = waterColor * (1f - fresnel) * NdotL;
            var sunSpec = sunColor * specular * NdotL;
            var sssColor = waterColor * sss;

            return reflection + refraction + sunSpec + sssColor;
        }

        /// <summary>
        /// Allocates GPU resources for the ocean height, normal, and displacement maps.
        /// </summary>
        public void AllocateGPUResources()
        {
            var heightDesc = new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R16_Float,
                Width = N, Height = N, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1, SampleQuality = 0,
                Flags = RHITextureFlags.UnorderedAccess | RHITextureFlags.ShaderResource
            };
            _heightMap = _device.CreateTexture(heightDesc, "OceanHeight");
            HeightMapSRV = _device.CreateSRV(_heightMap);

            var normalDesc = new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R16G16B16A16_Float,
                Width = N, Height = N, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1, SampleQuality = 0,
                Flags = RHITextureFlags.UnorderedAccess | RHITextureFlags.ShaderResource
            };
            _normalMap = _device.CreateTexture(normalDesc, "OceanNormal");
            NormalMapSRV = _device.CreateSRV(_normalMap);

            var dispDesc = new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R16G16B16A16_Float,
                Width = N, Height = N, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1, SampleQuality = 0,
                Flags = RHITextureFlags.UnorderedAccess | RHITextureFlags.ShaderResource
            };
            _displacementMap = _device.CreateTexture(dispDesc, "OceanDisplacement");
            DisplacementSRV = _device.CreateSRV(_displacementMap);
        }

        /// <summary>
        /// Uploads the CPU-computed ocean field to GPU textures.
        /// </summary>
        public void UploadOceanField(float[] heights, Vector2[] displacements, Vector3[] normals)
        {
            // Upload heights as R16_Float
            var heightData = new ushort[N * N];
            for (var i = 0; i < heights.Length; i++)
                heightData[i] = BitConverter.HalfToUInt16Bits((Half)heights[i]);
            var heightBytes = MemoryMarshal.AsBytes(heightData.AsSpan());
            _device.UploadToTexture(_heightMap!, heightBytes, N, N, 0, 0);

            // Upload normals as R16G16B16A16_Float
            var normalData = new Half[N * N * 4];
            for (var i = 0; i < normals.Length; i++)
            {
                normalData[i * 4 + 0] = (Half)normals[i].X;
                normalData[i * 4 + 1] = (Half)normals[i].Y;
                normalData[i * 4 + 2] = (Half)normals[i].Z;
                normalData[i * 4 + 3] = (Half)0f;
            }
            var normalBytes = MemoryMarshal.AsBytes(normalData.AsSpan());
            _device.UploadToTexture(_normalMap!, normalBytes, N, N, 0, 0);

            // Upload displacements as R16G16B16A16_Float (XY = displacement, ZW = pad)
            var dispData = new Half[N * N * 4];
            for (var i = 0; i < displacements.Length; i++)
            {
                dispData[i * 4 + 0] = (Half)displacements[i].X;
                dispData[i * 4 + 1] = (Half)displacements[i].Y;
                dispData[i * 4 + 2] = (Half)0f;
                dispData[i * 4 + 3] = (Half)0f;
            }
            var dispBytes = MemoryMarshal.AsBytes(dispData.AsSpan());
            _device.UploadToTexture(_displacementMap!, dispBytes, N, N, 0, 0);
        }
    }

    /// <summary>Parameters for a single Gerstner wave.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GerstnerWaveParams
    {
        /// <summary>Wave propagation direction (normalized).</summary>
        public Vector2 Direction;
        /// <summary>Wave amplitude in metres.</summary>
        public float Amplitude;
        /// <summary>Wavelength in metres.</summary>
        public float Wavelength;
        /// <summary>Phase speed multiplier.</summary>
        public float Speed;
        /// <summary>Steepness [0,1].</summary>
        public float Steepness;
    }

    /// <summary>Constant buffer for the cloud ray-march compute shader.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CloudMarchCB
    {
        /// <summary>Camera world position.</summary>
        public Vector3 CameraPos;
        /// <summary>Number of ray-march steps.</summary>
        public int Steps;
        /// <summary>Inverse view-projection matrix.</summary>
        public Matrix4x4 InvViewProj;
        /// <summary>Sun direction (normalized).</summary>
        public Vector3 SunDirection;
        /// <summary>Cloud layer bottom altitude.</summary>
        public float CloudBottom;
        /// <summary>Cloud layer top altitude.</summary>
        public float CloudTop;
        /// <summary>Cloud coverage [0,1].</summary>
        public float Coverage;
        /// <summary>Cloud density multiplier.</summary>
        public float Density;
        /// <summary>Current time.</summary>
        public float Time;
        /// <summary>Wind offset vector.</summary>
        public Vector2 WindDir;
        /// <summary>Screen resolution.</summary>
        public Vector2 Resolution;
    }

    /// <summary>Constant buffer for the god-ray compute shader.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GodRayCB
    {
        /// <summary>Sun position in screen UV space.</summary>
        public Vector2 SunScreenPos;
        /// <summary>Number of radial samples.</summary>
        public int Samples;
        /// <summary>God-ray intensity.</summary>
        public float Intensity;
        /// <summary>Per-sample decay.</summary>
        public float Decay;
        /// <summary>Exposure weight.</summary>
        public float Exposure;
        /// <summary>Screen resolution.</summary>
        public Vector2 Resolution;
        /// <summary>Inverse screen resolution.</summary>
        public Vector2 InvResolution;
    }

    /// <summary>
    /// Master atmosphere renderer that orchestrates the physical sky,
    /// aerial perspective, volumetric clouds, cloud shadows, god rays,
    /// and ocean rendering subsystems.
    /// </summary>
    public sealed class AtmosphereRenderer : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly AtmosphereSettings _settings;

        /// <summary>Precomputed atmosphere LUTs.</summary>
        public readonly AtmosphereLUT LUTs = new();
        /// <summary>Cloud noise generator.</summary>
        public readonly CloudNoiseGenerator NoiseGen;
        /// <summary>Volumetric cloud renderer.</summary>
        public readonly VolumetricCloudRenderer Clouds;
        /// <summary>Cloud shadow renderer.</summary>
        public readonly CloudShadowRenderer CloudShadows;
        /// <summary>God-ray renderer.</summary>
        public readonly GodRayRenderer GodRays;
        /// <summary>Ocean renderer.</summary>
        public readonly OceanRenderer Ocean;

        /// <summary>Sky colour output SRV.</summary>
        public RHIDescriptorHandle SkyColorSRV;
        /// <summary>Sky colour render target.</summary>
        public RHITexture? SkyColor;

        private RHIRootSignature? _skyRootSig;
        private RHIPipelineState? _skyPSO;
        private RHIRootSignature? _aerialRootSig;
        private RHIPipelineState? _aerialPSO;

        private bool _disposed;

        /// <summary>Creates the atmosphere renderer and all subsystems.</summary>
        public AtmosphereRenderer(RHIDevice device, AtmosphereSettings? settings = null)
        {
            _device = device;
            _settings = settings ?? new AtmosphereSettings();
            NoiseGen = new CloudNoiseGenerator();
            Clouds = new VolumetricCloudRenderer(device, NoiseGen, _settings);
            CloudShadows = new CloudShadowRenderer(device, _settings);
            GodRays = new GodRayRenderer(device, _settings);
            Ocean = new OceanRenderer(device, _settings);
        }

        /// <summary>
        /// Initializes all precomputed LUTs and noise textures. Call once
        /// after device creation.
        /// </summary>
        public void Initialize()
        {
            AllocateLUTs();
            NoiseGen.GenerateShapeNoise(_device);
            NoiseGen.GenerateDetailNoise(_device);
            NoiseGen.GenerateWeatherMap(_device, _settings.CloudCoverage);
            CloudShadows.Allocate(512, 2000f);
            Ocean.AllocateGPUResources();
            BuildSkyPipeline();
            BuildAerialPipeline();
        }

        /// <summary>
        /// Resizes all screen-space targets for the given resolution.
        /// </summary>
        public void OnResize(int width, int height)
        {
            SkyColor?.Dispose();
            var skyDesc = new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R16G16B16A16_Float,
                Width = width, Height = height, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1, SampleQuality = 0,
                Flags = RHITextureFlags.RenderTarget | RHITextureFlags.UnorderedAccess | RHITextureFlags.ShaderResource
            };
            SkyColor = _device.CreateTexture(skyDesc, "SkyColor");
            SkyColorSRV = _device.CreateSRV(SkyColor);

            Clouds.AllocateTargets(width, height);
            GodRays.Allocate(width, height);
        }

        private void AllocateLUTs()
        {
            // Transmittance LUT
            var tDesc = new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R16G16B16A16_Float,
                Width = AtmosphereLUT.TransmittanceWidth,
                Height = AtmosphereLUT.TransmittanceHeight,
                DepthOrArraySize = 1, MipLevels = 1, SampleCount = 1, SampleQuality = 0,
                Flags = RHITextureFlags.RenderTarget | RHITextureFlags.UnorderedAccess | RHITextureFlags.ShaderResource
            };
            LUTs.Transmittance = _device.CreateTexture(tDesc, "TransmittanceLUT");
            LUTs.TransmittanceSRV = _device.CreateSRV(LUTs.Transmittance);

            // Multi-scatter LUT
            var msDesc = new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R16G16B16A16_Float,
                Width = 32, Height = 32,
                DepthOrArraySize = 1, MipLevels = 1, SampleCount = 1, SampleQuality = 0,
                Flags = RHITextureFlags.RenderTarget | RHITextureFlags.UnorderedAccess | RHITextureFlags.ShaderResource
            };
            LUTs.MultiScatter = _device.CreateTexture(msDesc, "MultiScatterLUT");
            LUTs.MultiScatterSRV = _device.CreateSRV(LUTs.MultiScatter);

            // Sky-view LUT
            var svDesc = new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R16G16B16A16_Float,
                Width = AtmosphereLUT.SkyViewWidth,
                Height = AtmosphereLUT.SkyViewHeight,
                DepthOrArraySize = 1, MipLevels = 1, SampleCount = 1, SampleQuality = 0,
                Flags = RHITextureFlags.RenderTarget | RHITextureFlags.UnorderedAccess | RHITextureFlags.ShaderResource
            };
            LUTs.SkyView = _device.CreateTexture(svDesc, "SkyViewLUT");
            LUTs.SkyViewSRV = _device.CreateSRV(LUTs.SkyView);

            // Aerial-perspective LUT (3D)
            var apDesc = new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture3D,
                Format = RHIFormat.R16G16B16A16_Float,
                Width = AtmosphereLUT.AerialWidth,
                Height = AtmosphereLUT.AerialHeight,
                DepthOrArraySize = AtmosphereLUT.AerialDepth,
                MipLevels = 1, SampleCount = 1, SampleQuality = 0,
                Flags = RHITextureFlags.UnorderedAccess | RHITextureFlags.ShaderResource
            };
            LUTs.AerialPerspective = _device.CreateTexture(apDesc, "AerialPerspectiveLUT");
            LUTs.AerialPerspectiveSRV = _device.CreateSRV(LUTs.AerialPerspective);
        }

        private void BuildSkyPipeline()
        {
            var rootDesc = new RHIRootSignatureDesc
            {
                Parameters = new[]
                {
                    RootParam.Constants(1, 0),
                    RootParam.SRV(1, 0),
                    RootParam.SRV(1, 1),
                    RootParam.UAV(1, 0),
                },
                AllowInputAssembler = false
            };
            _skyRootSig = _device.CreateRootSignature(rootDesc);

            var psoDesc = new RHIComputePipelineDesc
            {
                Name = "SkyRender",
                CS = ShaderManager.Instance.GetBytecode("Atmosphere/Atmosphere", "SkyRenderCS", RHIShaderStage.Compute)
            };
            _skyPSO = _device.CreateComputePipelineState(psoDesc);
        }

        private void BuildAerialPipeline()
        {
            var rootDesc = new RHIRootSignatureDesc
            {
                Parameters = new[]
                {
                    RootParam.Constants(1, 0),
                    RootParam.SRV(1, 0),  // Scene depth
                    RootParam.SRV(1, 1),  // Transmittance LUT
                    RootParam.UAV(1, 0),  // Output
                },
                AllowInputAssembler = false
            };
            _aerialRootSig = _device.CreateRootSignature(rootDesc);

            var psoDesc = new RHIComputePipelineDesc
            {
                Name = "AerialPerspective",
                CS = ShaderManager.Instance.GetBytecode("Atmosphere/Atmosphere", "AerialPerspectiveCS", RHIShaderStage.Compute)
            };
            _aerialPSO = _device.CreateComputePipelineState(psoDesc);
        }

        /// <summary>
        /// Precomputes the transmittance and multi-scatter LUTs by dispatching
        /// the compute shaders. Call once per settings change.
        /// </summary>
        public unsafe void PrecomputeLUTs(RHICommandList cmd)
        {
            if (LUTs.Transmittance == null || _skyPSO == null) return;

            // Transmittance precompute
            cmd.SetPipelineState(_skyPSO);
            cmd.SetComputeRootSignature(_skyRootSig!);

            var tUAV = _device.CreateUAV(LUTs.Transmittance);
            var msUAV = _device.CreateUAV(LUTs.MultiScatter!);
            cmd.SetComputeRootDescriptorTable(2, msUAV);
            cmd.SetComputeRootDescriptorTable(3, tUAV);

            var cb = new SkyPrecomputeCB
            {
                PlanetRadius = _settings.PlanetRadius,
                AtmosphereHeight = _settings.AtmosphereHeight,
                RayleighScaleHeight = _settings.RayleighScaleHeight,
                MieScaleHeight = _settings.MieScaleHeight,
                RayleighScattering = _settings.RayleighScattering,
                MieScattering = _settings.MieScattering,
                MieAbsorption = _settings.MieAbsorption,
                MiePhaseG = _settings.MiePhaseG,
                GroundAlbedo = _settings.GroundAlbedo,
                SunDirection = _settings.SunDirection,
                Pad0 = 0
            };
            cmd.SetComputeRoot32BitConstants(0u, (uint)(sizeof(SkyPrecomputeCB) / 4), in cb, 0u);

            var tx = (AtmosphereLUT.TransmittanceWidth + 7) / 8;
            var ty = (AtmosphereLUT.TransmittanceHeight + 7) / 8;
            cmd.Dispatch(tx, ty, 1);
        }

        /// <summary>
        /// Renders the full atmospheric frame: sky, clouds, cloud shadows,
        /// aerial perspective, and god rays.
        /// </summary>
        public unsafe void Render(RHICommandList cmd, int width, int height,
            Vector3 cameraPos, Matrix4x4 viewProj, Matrix4x4 invViewProj,
            RHIDescriptorHandle sceneColorSRV, RHIDescriptorHandle depthSRV,
            Vector2 sunScreenPos, float time)
        {
            // 1. Sky pass
            if (SkyColor != null && _skyPSO != null)
            {
                cmd.SetPipelineState(_skyPSO);
                cmd.SetComputeRootSignature(_skyRootSig!);
                cmd.SetComputeRootDescriptorTable(1, LUTs.TransmittanceSRV);
                cmd.SetComputeRootDescriptorTable(2, LUTs.SkyViewSRV);
                var skyUAV = _device.CreateUAV(SkyColor);
                cmd.SetComputeRootDescriptorTable(3, skyUAV);

                var cb = new SkyRenderCB
                {
                    CameraPos = cameraPos,
                    InvViewProj = invViewProj,
                    SunDirection = _settings.SunDirection,
                    SunIlluminance = _settings.SunIlluminance,
                    PlanetRadius = _settings.PlanetRadius,
                    AtmosphereHeight = _settings.AtmosphereHeight,
                    Resolution = new Vector2(width, height)
                };
                    cmd.SetComputeRoot32BitConstants(0u, (uint)(sizeof(SkyRenderCB) / 4), in cb, 0u);

                var gx = (width + 7) / 8;
                var gy = (height + 7) / 8;
                cmd.Dispatch(gx, gy, 1);
            }

            // 2. Volumetric clouds
            if (_settings.EnableClouds)
            {
                Clouds.Render(cmd, width, height, cameraPos, invViewProj, time);
            }

            // 3. God rays
            if (_settings.EnableGodRays)
            {
                GodRays.Render(cmd, sceneColorSRV, depthSRV, sunScreenPos, width, height);
            }
        }

        /// <summary>
        /// Updates the ocean field at the given simulation time. Computes the
        /// wave spectrum on CPU and uploads the result to GPU textures.
        /// </summary>
        public void UpdateOcean(float time)
        {
            if (!_settings.EnableOcean) return;
            var (heights, displacements, normals) = Ocean.ComputeOceanField(time);
            Ocean.UploadOceanField(heights, displacements, normals);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            LUTs.Transmittance?.Dispose();
            LUTs.MultiScatter?.Dispose();
            LUTs.SkyView?.Dispose();
            LUTs.AerialPerspective?.Dispose();
            NoiseGen.ShapeNoise?.Dispose();
            NoiseGen.DetailNoise?.Dispose();
            NoiseGen.WeatherMap?.Dispose();
            SkyColor?.Dispose();
            _skyRootSig?.Dispose();
            _skyPSO?.Dispose();
            _aerialRootSig?.Dispose();
            _aerialPSO?.Dispose();
        }
    }

    /// <summary>Constant buffer for the sky precompute shader.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SkyPrecomputeCB
    {
        /// <summary>Planet radius.</summary>
        public float PlanetRadius;
        /// <summary>Atmosphere height.</summary>
        public float AtmosphereHeight;
        /// <summary>Rayleigh scale height.</summary>
        public float RayleighScaleHeight;
        /// <summary>Mie scale height.</summary>
        public float MieScaleHeight;
        /// <summary>Rayleigh scattering coefficients.</summary>
        public Vector3 RayleighScattering;
        /// <summary>Mie scattering coefficient.</summary>
        public float MieScattering;
        /// <summary>Mie absorption coefficient.</summary>
        public float MieAbsorption;
        /// <summary>Mie phase g parameter.</summary>
        public float MiePhaseG;
        /// <summary>Pad.</summary>
        public float Pad0;
        /// <summary>Ground albedo.</summary>
        public Vector3 GroundAlbedo;
        /// <summary>Sun direction.</summary>
        public Vector3 SunDirection;
        /// <summary>Pad.</summary>
        public float SunIlluminance;
    }

    /// <summary>Constant buffer for the sky render shader.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SkyRenderCB
    {
        /// <summary>Camera world position.</summary>
        public Vector3 CameraPos;
        /// <summary>Pad.</summary>
        public float SunIlluminance;
        /// <summary>Inverse view-projection.</summary>
        public Matrix4x4 InvViewProj;
        /// <summary>Sun direction.</summary>
        public Vector3 SunDirection;
        /// <summary>Planet radius.</summary>
        public float PlanetRadius;
        /// <summary>Atmosphere height.</summary>
        public float AtmosphereHeight;
        /// <summary>Screen resolution.</summary>
        public Vector2 Resolution;
    }

    // ── Root parameter helper (shared utility) ─────────────────────────

    internal static class RootParam
    {
        public static RHIRootParameter Constants(int num32, uint reg) => new()
        {
            ParameterType = RHIRootParameterType.Constants,
            ShaderVisibility = RHIShaderVisibility.All,
            Constants = new RHIRootConstants { Num32BitValues = num32, ShaderRegister = reg, RegisterSpace = 0 }
        };

        public static RHIRootParameter SRV(int count, uint baseReg) => new()
        {
            ParameterType = RHIRootParameterType.DescriptorTable,
            ShaderVisibility = RHIShaderVisibility.All,
            DescriptorTable = new RHIRootDescriptorTable
            {
                Ranges = new[] { new RHIRootDescriptorRange
                {
                    RangeType = RHIDescriptorRangeType.SRV,
                    NumDescriptors = count,
                    BaseShaderRegister = baseReg,
                    RegisterSpace = 0,
                    OffsetInDescriptorsFromTableStart = 0
                }}
            }
        };

        public static RHIRootParameter UAV(int count, uint baseReg) => new()
        {
            ParameterType = RHIRootParameterType.DescriptorTable,
            ShaderVisibility = RHIShaderVisibility.All,
            DescriptorTable = new RHIRootDescriptorTable
            {
                Ranges = new[] { new RHIRootDescriptorRange
                {
                    RangeType = RHIDescriptorRangeType.UAV,
                    NumDescriptors = count,
                    BaseShaderRegister = baseReg,
                    RegisterSpace = 0,
                    OffsetInDescriptorsFromTableStart = 0
                }}
            }
        };
    }

    // ── Complex number type for FFT ────────────────────────────────────

    /// <summary>
    /// Simple complex number struct for CPU-side FFT computation.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct Complex
    {
        /// <summary>Real part.</summary>
        public readonly float Real;
        /// <summary>Imaginary part.</summary>
        public readonly float Imaginary;

        /// <summary>Creates a complex number.</summary>
        public Complex(float real, float imaginary) { Real = real; Imaginary = imaginary; }

        /// <summary>Zero.</summary>
        public static Complex Zero => new(0f, 0f);
        /// <summary>One.</summary>
        public static Complex One => new(1f, 0f);

        /// <summary>Magnitude.</summary>
        public float Magnitude => MathF.Sqrt(Real * Real + Imaginary * Imaginary);
        /// <summary>Phase.</summary>
        public float Phase => MathF.Atan2(Imaginary, Real);

        /// <summary>Complex conjugate.</summary>
        public static Complex Conjugate(Complex c) => new(c.Real, -c.Imaginary);

        /// <summary>Addition.</summary>
        public static Complex operator +(Complex a, Complex b) => new(a.Real + b.Real, a.Imaginary + b.Imaginary);
        /// <summary>Subtraction.</summary>
        public static Complex operator -(Complex a, Complex b) => new(a.Real - b.Real, a.Imaginary - b.Imaginary);
        /// <summary>Multiplication.</summary>
        public static Complex operator *(Complex a, Complex b) =>
            new(a.Real * b.Real - a.Imaginary * b.Imaginary,
                a.Real * b.Imaginary + a.Imaginary * b.Real);
        /// <summary>Scalar multiplication.</summary>
        public static Complex operator *(Complex a, float s) => new(a.Real * s, a.Imaginary * s);
    }
}
