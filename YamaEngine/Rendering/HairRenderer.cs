using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using YamaEngine.RHI;
using YamaEngine.Shaders;

namespace YamaEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════
    // Hair & Fur Renderer
    // Strand-based hair rendering with the Marschner BSDF (R, TT, TRT
    // lobes), hair LOD, anti-aliased line rendering, TressFX-style PBD
    // simulation, and a shell-based fur rendering fallback.
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Configuration for the hair and fur rendering system.
    /// </summary>
    public sealed class HairSettings
    {
        // ── Strand rendering ──────────────────────────────────────────
        /// <summary>Enables strand-based hair rendering.</summary>
        public bool EnableStrandRendering = true;
        /// <summary>Strand radius in world units (thickness).</summary>
        public float StrandRadius = 0.0003f;
        /// <summary>Maximum strand tessellation LOD.</summary>
        public int MaxStrandLOD = 4;
        /// <summary>Screen-space tessellation factor per pixel.</summary>
        public float TessellationPixelsPerSegment = 8f;
        /// <summary>Enables anti-aliased strand rendering.</summary>
        public bool EnableAntiAliasing = true;
        /// <summary>AA sample count for strand edges.</summary>
        public int StrandAASamples = 4;

        // ── Marschner BSDF ────────────────────────────────────────────
        /// <summary>R lobe intensity (surface reflection).</summary>
        public float RIntensity = 1.0f;
        /// <summary>TT lobe intensity (transmission).</summary>
        public float TTIntensity = 0.4f;
        /// <summary>TRT lobe intensity (internal reflection).</summary>
        public float TRTIntensity = 0.6f;
        /// <summary>Longitudinal roughness (M term).</summary>
        public float LongitudinalRoughness = 0.15f;
        /// <summary>Azimuthal roughness (N term).</summary>
        public float AzimuthalRoughness = 0.12f;
        /// <summary>Hair base colour (melanin tinted).</summary>
        public Vector3 HairColor = new(0.42f, 0.28f, 0.12f);
        /// <summary>Index of refaction of hair cortex.</summary>
        public float HairIOR = 1.55f;
        /// <summary>Shift of the R lobe (degrees).</summary>
        public float RShift = -10f;
        /// <summary>Shift of the TT lobe (degrees).</summary>
        public float TTShift = 5f;
        /// <summary>Shift of the TRT lobe (degrees).</summary>
        public float TRTShift = -15f;
        /// <summary>Enables approximate hair GI.</summary>
        public bool EnableHairGI = true;

        // ── Simulation ────────────────────────────────────────────────
        /// <summary>Enables TressFX-style PBD hair simulation.</summary>
        public bool EnableSimulation = true;
        /// <summary>Gravity vector.</summary>
        public Vector3 Gravity = new(0f, -9.81f, 0f);
        /// <summary>Wind force vector.</summary>
        public Vector3 Wind = Vector3.Zero;
        /// <summary>Number of PBD solver iterations.</summary>
        public int SolverIterations = 8;
        /// <summary>Global damping coefficient.</summary>
        public float Damping = 0.96f;
        /// <summary>Strand stiffness (distance constraint).</summary>
        public float StrandStiffness = 0.9f;
        /// <summary>Bend constraint stiffness.</summary>
        public float BendStiffness = 0.3f;
        /// <summary>Head/sphere collision radius.</summary>
        public float HeadRadius = 0.12f;
        /// <summary>Head/sphere collision centre.</summary>
        public Vector3 HeadCenter = Vector3.Zero;
        /// <summary>Enables self-collision via grid.</summary>
        public bool EnableSelfCollision = false;

        // ── Fur fallback ──────────────────────────────────────────────
        /// <summary>Enables shell-based fur rendering as a fallback.</summary>
        public bool EnableShellFur = false;
        /// <summary>Number of fur shells.</summary>
        public int FurShellCount = 16;
        /// <summary>Fur length in world units.</summary>
        public float FurLength = 0.05f;
        /// <summary>Fur density (strands per unit area).</summary>
        public float FurDensity = 0.8f;
        /// <summary>Fur colour at base.</summary>
        public Vector3 FurBaseColor = new(0.3f, 0.2f, 0.1f);
        /// <summary>Fur colour at tip.</summary>
        public Vector3 FurTipColor = new(0.6f, 0.4f, 0.2f);
    }

    // ── Marschner Hair BSDF ─────────────────────────────────────────

    /// <summary>
    /// Marschner hair scattering model. Computes the bidirectional
    /// scattering distribution function (BSDF) for a cylindrical hair
    /// fibre using the R (reflection), TT (transmission), and TRT
    /// (transmission-reflection-transmission) lobes.
    /// 
    /// The model decomposes scattering into:
    ///   S = M(theta_i, theta_r) * N(phi_i, phi_r)
    /// where M is the longitudinal scattering term (Gaussian) and
    /// N is the azimuthal scattering term (derived from the fibre's
    /// circular cross-section and internal refraction paths).
    /// </summary>
    public static class MarschnerBSDF
    {
        /// <summary>
        /// Evaluates the full Marschner BSDF for the given directions.
        /// </summary>
        /// <param name="viewDir">Direction from hair to viewer.</param>
        /// <param name="lightDir">Direction from hair to light.</param>
        /// <param name="tangent">Hair strand tangent (pointing toward tip).</param>
        /// <param name="settings">Hair settings with BSDF parameters.</param>
        /// <returns>Scattered radiance in the direction of the viewer.</returns>
        public static Vector3 Evaluate(
            Vector3 viewDir, Vector3 lightDir, Vector3 tangent,
            in HairSettings settings)
        {
            // Project directions into the normal plane (perpendicular to tangent)
            var V = ProjectToNormalPlane(viewDir, tangent);
            var L = ProjectToNormalPlane(lightDir, tangent);

            // Compute longitudinal angles
            var cosThetaI = Vector3.Dot(Vector3.Normalize(viewDir), Vector3.Normalize(tangent));
            var cosThetaR = Vector3.Dot(Vector3.Normalize(viewDir), Vector3.Normalize(tangent));
            // Note: theta is measured from the normal plane, so sin = component along tangent
            var sinThetaI = MathF.Sqrt(MathF.Max(0f, 1f - cosThetaI * cosThetaI));
            var sinThetaR = MathF.Sqrt(MathF.Max(0f, 1f - cosThetaR * cosThetaR));
            var thetaI = MathF.Asin(Math.Clamp(sinThetaI, -1f, 1f));
            var thetaR = MathF.Asin(Math.Clamp(sinThetaR, -1f, 1f));

            // Azimuthal angle difference
            var phiR = MathF.Atan2(V.Y, V.X);
            var phiI = MathF.Atan2(L.Y, L.X);
            var dPhi = ComputeAzimuthalDifference(phiR, phiI);

            // Compute each lobe
            var M_R = LongitudinalScattering(thetaI, thetaR, settings.RShift * MathF.PI / 180f,
                settings.LongitudinalRoughness);
            var M_TT = LongitudinalScattering(thetaI, thetaR, settings.TTShift * MathF.PI / 180f,
                settings.LongitudinalRoughness * 0.5f);
            var M_TRT = LongitudinalScattering(thetaI, thetaR, settings.TRTShift * MathF.PI / 180f,
                settings.LongitudinalRoughness * 1.5f);

            var N_R = AzimuthalScattering(dPhi, 0, settings.HairIOR, settings.AzimuthalRoughness);
            var N_TT = AzimuthalScattering(dPhi, 1, settings.HairIOR, settings.AzimuthalRoughness);
            var N_TRT = AzimuthalScattering(dPhi, 2, settings.HairIOR, settings.AzimuthalRoughness);

            // Absorption based on path length through fibre
            var absorptionR = ComputeAbsorption(0, settings.HairColor, settings.HairIOR);
            var absorptionTT = ComputeAbsorption(1, settings.HairColor, settings.HairIOR);
            var absorptionTRT = ComputeAbsorption(2, settings.HairColor, settings.HairIOR);

            // Combine lobes
            var result = Vector3.Zero;
            result += settings.RIntensity * M_R * N_R * absorptionR;
            result += settings.TTIntensity * M_TT * N_TT * absorptionTT;
            result += settings.TRTIntensity * M_TRT * N_TRT * absorptionTRT;

            return result;
        }

        /// <summary>
        /// Projects a direction onto the normal plane (perpendicular to
        /// the hair tangent).
        /// </summary>
        public static Vector3 ProjectToNormalPlane(Vector3 dir, Vector3 tangent)
        {
            var t = Vector3.Normalize(tangent);
            return dir - t * Vector3.Dot(dir, t);
        }

        /// <summary>
        /// Computes the azimuthal difference angle in [-pi, pi].
        /// </summary>
        public static float ComputeAzimuthalDifference(float phiR, float phiI)
        {
            var dPhi = phiR - phiI;
            while (dPhi > MathF.PI) dPhi -= 2f * MathF.PI;
            while (dPhi < -MathF.PI) dPhi += 2f * MathF.PI;
            return dPhi;
        }

        /// <summary>
        /// Longitudinal scattering function M. Uses a Gaussian centered at
        /// the shifted half-angle with width determined by roughness.
        /// </summary>
        public static float LongitudinalScattering(float thetaI, float thetaR, float shift, float roughness)
        {
            var thetaH = (thetaI + thetaR) * 0.5f + shift;
            var thetaD = (thetaI - thetaR) * 0.5f;

            // Gaussian with standard deviation = roughness
            var v = roughness * roughness;
            var gaussian = MathF.Exp(-thetaH * thetaH / (2f * v)) /
                           (MathF.Sqrt(2f * MathF.PI) * MathF.Max(v, 1e-6f));

            // Fresnel at the hair surface
            var cosThetaD = MathF.Cos(thetaD);
            var fresnel = FresnelDielectric(cosThetaD, 1f, 1.55f);

            return gaussian * fresnel / (MathF.Cos(thetaD) + 1e-6f);
        }

        /// <summary>
        /// Azimuthal scattering function N. For the R lobe (p=0) this is
        /// a simple Gaussian peaked at phi=0. For TT (p=1) and TRT (p=2),
        /// the peak is shifted by the refraction angle.
        /// </summary>
        public static float AzimuthalScattering(float phi, int p, float eta, float roughness)
        {
            if (p == 0)
            {
                // R lobe: reflection, peaked at phi = 0
                var variance = roughness * roughness;
                return MathF.Exp(-phi * phi / (2f * variance)) / (MathF.Sqrt(2f * MathF.PI) * MathF.Max(variance, 1e-6f));
            }

            // Compute the refraction angle gamma for p bounces
            var gammaP = ComputeRefractionAngle(phi, p, eta);
            if (float.IsNaN(gammaP)) return 0f;

            var dPhi = phi - 2f * p * gammaP - MathF.PI;
            // Normalize to [-pi, pi]
            while (dPhi > MathF.PI) dPhi -= 2f * MathF.PI;
            while (dPhi < -MathF.PI) dPhi += 2f * MathF.PI;

            var v = roughness * roughness;
            return MathF.Exp(-dPhi * dPhi / (2f * v)) / (MathF.Sqrt(2f * MathF.PI) * MathF.Max(v, 1e-6f));
        }

        /// <summary>
        /// Computes the refraction angle gamma_p for the p-th internal
        /// bounce using the Bravais fibre model.
        /// </summary>
        public static float ComputeRefractionAngle(float phi, int p, float eta)
        {
            // The Bravais modified index for a fibre:
            // eta' = sqrt(eta^2 - sin^2(theta_d)) / cos(theta_d)
            // For the azimuthal computation, we approximate with eta directly.
            var halfPhi = phi * 0.5f;
            var sinGamma = MathF.Sin(halfPhi) / eta;
            if (MathF.Abs(sinGamma) >= 1f) return float.NaN;
            var gamma = MathF.Asin(sinGamma);
            return gamma;
        }

        /// <summary>
        /// Computes absorption through the hair fibre based on the path
        /// length. For the R lobe (p=0) there is no internal path, so
        /// absorption is 1. For TT (p=1) and TRT (p=2), the absorption
        /// follows Beer-Lambert with the hair colour as the extinction.
        /// </summary>
        public static Vector3 ComputeAbsorption(int p, Vector3 hairColor, float eta)
        {
            if (p == 0) return Vector3.One;

            // Estimate path length through the fibre
            // For TT (p=1): path ~ 2 * cos(gamma) / cos(theta_d)
            // For TRT (p=2): path ~ 4 * cos(gamma) / cos(theta_d)
            var pathFactor = p switch { 1 => 1.0f, 2 => 2.5f, _ => 1f };

            // Absorption coefficient from hair colour
            var sigma = new Vector3(
                -MathF.Log(MathF.Max(1e-6f, hairColor.X)),
                -MathF.Log(MathF.Max(1e-6f, hairColor.Y)),
                -MathF.Log(MathF.Max(1e-6f, hairColor.Z)));

            return new Vector3(
                MathF.Exp(-sigma.X * pathFactor),
                MathF.Exp(-sigma.Y * pathFactor),
                MathF.Exp(-sigma.Z * pathFactor));
        }

        /// <summary>Schlick Fresnel approximation.</summary>
        public static float FresnelSchlick(float cosTheta, float f0)
        {
            return f0 + (1f - f0) * MathF.Pow(1f - cosTheta, 5f);
        }

        /// <summary>Dielectric Fresnel (unpolarized average).</summary>
        public static float FresnelDielectric(float cosThetaI, float etaI, float etaT)
        {
            var sinThetaT = etaI / etaT * MathF.Sqrt(MathF.Max(0f, 1f - cosThetaI * cosThetaI));
            if (sinThetaT >= 1f) return 1f; // Total internal reflection
            var cosThetaT = MathF.Sqrt(MathF.Max(0f, 1f - sinThetaT * sinThetaT));
            var rs = (etaI * cosThetaI - etaT * cosThetaT) / (etaI * cosThetaI + etaT * cosThetaT);
            var rp = (etaT * cosThetaI - etaI * cosThetaT) / (etaT * cosThetaI + etaI * cosThetaT);
            return 0.5f * (rs * rs + rp * rp);
        }

        /// <summary>
        /// Computes the diffuse approximation for hair (used for ambient
        /// and GI contribution). This is a simple Lambertian term scaled
        /// by the hair colour.
        /// </summary>
        public static Vector3 DiffuseTerm(Vector3 normal, Vector3 lightDir, Vector3 hairColor)
        {
            var NdotL = MathF.Max(0f, Vector3.Dot(normal, lightDir));
            return hairColor * NdotL * 0.3f;
        }

        /// <summary>
        /// Computes a simplified single-lobe approximation for LOD rendering.
        /// When strands are too small on screen, the full BSDF is replaced
        /// by a single shifted Gaussian lobe for performance.
        /// </summary>
        public static Vector3 EvaluateLOD(
            Vector3 viewDir, Vector3 lightDir, Vector3 tangent,
            in HairSettings settings, int lodLevel)
        {
            if (lodLevel <= 0)
                return Evaluate(viewDir, lightDir, tangent, in settings);

            // Simplified: only R lobe + diffuse
            var V = ProjectToNormalPlane(viewDir, tangent);
            var L = ProjectToNormalPlane(lightDir, tangent);
            var halfDir = Vector3.Normalize(V + L);
            var cosTheta = MathF.Max(0f, Vector3.Dot(V, L) / (V.Length() * L.Length() + 1e-6f));
            var NdotL = MathF.Max(0f, cosTheta);

            // Single specular lobe
            var spec = MathF.Pow(NdotL, 32f) * settings.RIntensity;
            var diffuse = settings.HairColor * NdotL * 0.4f;

            return diffuse + new Vector3(spec * 0.5f);
        }
    }

    // ── Strand data structures ──────────────────────────────────────

    /// <summary>
    /// A single hair strand vertex (particle) used in both rendering and
    /// simulation.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct HairStrandVertex
    {
        /// <summary>Current world position.</summary>
        public Vector3 Position;
        /// <summary>Previous frame position (for Verlet integration).</summary>
        public Vector3 OldPosition;
        /// <summary>Strand tangent at this vertex.</summary>
        public Vector3 Tangent;
        /// <summary>Rest position (for shape constraints).</summary>
        public Vector3 RestPosition;
        /// <summary>UV coordinate along the strand [0,1].</summary>
        public float V;
        /// <padding>Unused.</summary>
        public float Pad0;
        /// <padding>Unused.</summary>
        public float Pad1;
    }

    /// <summary>
    /// A hair strand consisting of a chain of vertices connected by
    /// distance constraints.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct HairStrand
    {
        /// <summary>Start index into the global vertex buffer.</summary>
        public int VertexStart;
        /// <summary>Number of vertices in this strand.</summary>
        public int VertexCount;
        /// <summary>Root position (fixed in simulation).</summary>
        public Vector3 RootPosition;
        /// <summary>Root direction (initial growth direction).</summary>
        public Vector3 RootDirection;
        /// <summary>Strand length.</summary>
        public float Length;
        /// <summary>Strand colour multiplier.</summary>
        public Vector3 Color;
        /// <summary>Strand thickness.</summary>
        public float Thickness;
    }

    /// <summary>
    /// A complete hair asset containing all strands, their vertices, and
    /// simulation parameters.
    /// </summary>
    public sealed class HairAsset
    {
        /// <summary>All strand vertices (concatenated).</summary>
        public List<HairStrandVertex> Vertices = new();
        /// <summary>All strands.</summary>
        public List<HairStrand> Strands = new();
        /// <summary>Rest distances between consecutive vertices per strand.</summary>
        public List<float> RestDistances = new();

        /// <summary>Total vertex count.</summary>
        public int TotalVertices => Vertices.Count;
        /// <summary>Total strand count.</summary>
        public int TotalStrands => Strands.Count;
        /// <summary>Maximum vertices per strand.</summary>
        public int MaxVerticesPerStrand { get; set; } = 32;

        /// <summary>
        /// Generates a set of hair strands on a sphere (head).
        /// </summary>
        public static HairAsset GenerateOnSphere(
            Vector3 center, float radius, int strandCount,
            int verticesPerStrand, float strandLength, int seed = 42)
        {
            var asset = new HairAsset { MaxVerticesPerStrand = verticesPerStrand };
            var rng = new Random(seed);

            for (var s = 0; s < strandCount; s++)
            {
                // Random point on upper hemisphere
                var theta = (float)(rng.NextDouble() * 2f * Math.PI);
                var phi = (float)(rng.NextDouble() * Math.PI * 0.5); // upper half
                var dir = new Vector3(
                    MathF.Sin(phi) * MathF.Cos(theta),
                    MathF.Cos(phi),
                    MathF.Sin(phi) * MathF.Sin(theta));
                var root = center + dir * radius;

                var strand = new HairStrand
                {
                    VertexStart = asset.Vertices.Count,
                    VertexCount = verticesPerStrand,
                    RootPosition = root,
                    RootDirection = dir,
                    Length = strandLength,
                    Color = new Vector3(0.42f, 0.28f, 0.12f),
                    Thickness = 0.0003f
                };

                // Create vertices along the strand
                var segmentLength = strandLength / (verticesPerStrand - 1);
                for (var v = 0; v < verticesPerStrand; v++)
                {
                    var t = (float)v / (verticesPerStrand - 1);
                    var pos = root + dir * strandLength * t;
                    // Add slight curl
                    var curl = new Vector3(
                        MathF.Sin(t * 8f) * 0.02f,
                        0f,
                        MathF.Cos(t * 8f) * 0.02f);
                    pos += curl;

                    asset.Vertices.Add(new HairStrandVertex
                    {
                        Position = pos,
                        OldPosition = pos,
                        Tangent = dir,
                        RestPosition = pos,
                        V = t,
                        Pad0 = 0f,
                        Pad1 = 0f
                    });
                    asset.RestDistances.Add(segmentLength);
                }

                asset.Strands.Add(strand);
            }

            return asset;
        }
    }

    // ── PBD Hair Simulation ─────────────────────────────────────────

    /// <summary>
    /// TressFX-style position-based dynamics (PBD) hair simulator.
    /// Uses Verlet integration for prediction, then solves distance
    /// constraints, bend constraints, and collision constraints
    /// iteratively to compute the final positions.
    /// </summary>
    public sealed class HairPBDSimulator
    {
        private readonly HairSettings _settings;
        private HairAsset? _asset;
        private Vector3[]? _predictedPositions;
        private Vector3[]? _velocities;

        // Spatial hash for self-collision
        private readonly Dictionary<long, List<int>> _spatialHash = new();
        private const float HashCellSize = 0.01f;

        /// <summary>Creates the simulator with the given settings.</summary>
        public HairPBDSimulator(HairSettings settings)
        {
            _settings = settings;
        }

        /// <summary>Binds a hair asset to this simulator.</summary>
        public void SetAsset(HairAsset asset)
        {
            _asset = asset;
            _predictedPositions = new Vector3[asset.TotalVertices];
            _velocities = new Vector3[asset.TotalVertices];
            for (var i = 0; i < asset.TotalVertices; i++)
            {
                _velocities[i] = Vector3.Zero;
                _predictedPositions[i] = asset.Vertices[i].Position;
            }
        }

        /// <summary>
        /// Performs one simulation step with the given delta time.
        /// </summary>
        public void Simulate(float dt)
        {
            if (_asset == null || _predictedPositions == null || _velocities == null) return;

            // Clamp dt to prevent instability
            dt = MathF.Min(dt, 1f / 30f);

            // 1. Predict positions (Verlet integration)
            PredictPositions(dt);

            // 2. Solve constraints iteratively
            for (var iter = 0; iter < _settings.SolverIterations; iter++)
            {
                SolveDistanceConstraints();
                SolveBendConstraints();
                SolveHeadCollision();
                if (_settings.EnableSelfCollision)
                    SolveSelfCollision();
            }

            // 3. Update velocities and positions
            UpdateVelocitiesAndPositions(dt);

            // 4. Update tangents
            UpdateTangents();
        }

        /// <summary>
        /// Predicts new positions using Verlet integration:
        ///   x_new = x + (x - x_old) * damping + a * dt^2
        /// </summary>
        private void PredictPositions(float dt)
        {
            var acceleration = _settings.Gravity + _settings.Wind;
            var dt2 = dt * dt;
            var damping = _settings.Damping;

            for (var s = 0; s < _asset!.Strands.Count; s++)
            {
                var strand = _asset.Strands[s];
                for (var v = 0; v < strand.VertexCount; v++)
                {
                    var idx = strand.VertexStart + v;
                    var vert = _asset.Vertices[idx];

                    // Root vertices (first 1-2) are pinned
                    if (v < 1)
                    {
                        _predictedPositions![idx] = vert.Position;
                        continue;
                    }

                    var velocity = (vert.Position - vert.OldPosition) * damping;
                    _velocities![idx] = velocity;
                    _predictedPositions![idx] = vert.Position + velocity + acceleration * dt2;
                }
            }
        }

        /// <summary>
        /// Solves distance constraints between consecutive vertices:
        ///   |x[i+1] - x[i]| = restLength
        /// Uses the PBD Gauss-Seidel projection.
        /// </summary>
        private void SolveDistanceConstraints()
        {
            if (_predictedPositions == null || _asset == null) return;
            var stiffness = _settings.StrandStiffness;

            for (var s = 0; s < _asset.Strands.Count; s++)
            {
                var strand = _asset.Strands[s];
                for (var v = 0; v < strand.VertexCount - 1; v++)
                {
                    var i0 = strand.VertexStart + v;
                    var i1 = strand.VertexStart + v + 1;
                    var restLen = _asset.RestDistances[i0];

                    var p0 = _predictedPositions[i0];
                    var p1 = _predictedPositions[i1];
                    var delta = p1 - p0;
                    var dist = delta.Length();
                    if (dist < 1e-8f) continue;

                    var diff = (dist - restLen) / dist;
                    var correction = delta * (0.5f * diff * stiffness);

                    // Root is pinned
                    if (v > 0)
                        _predictedPositions[i0] += correction;
                    if (v < strand.VertexCount - 2 || true)
                        _predictedPositions[i1] -= correction * (v == 0 ? 1f : 1f);
                }
            }
        }

        /// <summary>
        /// Solves bend constraints to maintain the strand's rest curvature.
        /// Constrains the angle between three consecutive vertices.
        /// </summary>
        private void SolveBendConstraints()
        {
            if (_predictedPositions == null || _asset == null) return;
            var stiffness = _settings.BendStiffness;

            for (var s = 0; s < _asset.Strands.Count; s++)
            {
                var strand = _asset.Strands[s];
                for (var v = 1; v < strand.VertexCount - 1; v++)
                {
                    var i0 = strand.VertexStart + v - 1;
                    var i1 = strand.VertexStart + v;
                    var i2 = strand.VertexStart + v + 1;

                    var p0 = _predictedPositions[i0];
                    var p1 = _predictedPositions[i1];
                    var p2 = _predictedPositions[i2];

                    // Rest direction
                    var restDir = _asset.Vertices[i2].RestPosition - _asset.Vertices[i0].RestPosition;
                    var restLen = restDir.Length();
                    if (restLen < 1e-8f) continue;
                    restDir /= restLen;

                    // Current direction
                    var curDir = p2 - p0;
                    var curLen = curDir.Length();
                    if (curLen < 1e-8f) continue;
                    curDir /= curLen;

                    // Project middle point toward rest shape
                    var restMid = _asset.Vertices[i0].RestPosition +
                                  (_asset.Vertices[i2].RestPosition - _asset.Vertices[i0].RestPosition) * 0.5f;
                    var restOffset = _asset.Vertices[i1].RestPosition - restMid;
                    var curMid = (p0 + p2) * 0.5f;
                    var target = curMid + restOffset;
                    _predictedPositions[i1] = Vector3.Lerp(p1, target, stiffness);
                }
            }
        }

        /// <summary>
        /// Solves collision with a sphere (head).
        /// </summary>
        private void SolveHeadCollision()
        {
            if (_predictedPositions == null || _asset == null) return;
            var center = _settings.HeadCenter;
            var radius = _settings.HeadRadius;

            for (var i = 0; i < _predictedPositions.Length; i++)
            {
                var offset = _predictedPositions[i] - center;
                var dist = offset.Length();
                if (dist < radius)
                {
                    var normal = dist > 1e-8f ? offset / dist : new Vector3(0, 1, 0);
                    _predictedPositions[i] = center + normal * radius;
                }
            }
        }

        /// <summary>
        /// Solves self-collision using a uniform spatial hash grid.
        /// </summary>
        private void SolveSelfCollision()
        {
            if (_predictedPositions == null || _asset == null) return;
            _spatialHash.Clear();

            // Build hash grid
            for (var i = 0; i < _predictedPositions.Length; i++)
            {
                var cell = HashCell(_predictedPositions[i]);
                if (!_spatialHash.ContainsKey(cell))
                    _spatialHash[cell] = new List<int>();
                _spatialHash[cell].Add(i);
            }

            // Check neighbours
            var collisionRadius = _settings.StrandRadius * 2f;
            var collisionRadius2 = collisionRadius * collisionRadius;

            for (var i = 0; i < _predictedPositions.Length; i++)
            {
                var cell = HashCell(_predictedPositions[i]);
                for (var dz = -1; dz <= 1; dz++)
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    var neighbourCell = cell + HashOffset(dx, dy, dz);
                    if (!_spatialHash.TryGetValue(neighbourCell, out var bucket)) continue;
                    foreach (var j in bucket)
                    {
                        if (j <= i) continue;
                        var diff = _predictedPositions[j] - _predictedPositions[i];
                        var dist2 = diff.LengthSquared();
                        if (dist2 < collisionRadius2 && dist2 > 1e-12f)
                        {
                            var dist = MathF.Sqrt(dist2);
                            var push = diff / dist * (collisionRadius - dist) * 0.5f;
                            _predictedPositions[i] -= push;
                            _predictedPositions[j] += push;
                        }
                    }
                }
            }
        }

        private long HashCell(Vector3 p)
        {
            var x = (long)MathF.Floor(p.X / HashCellSize);
            var y = (long)MathF.Floor(p.Y / HashCellSize);
            var z = (long)MathF.Floor(p.Z / HashCellSize);
            return (x * 73856093) ^ (y * 19349663) ^ (z * 83492791);
        }

        private static long HashOffset(int dx, int dy, int dz)
        {
            return ((long)dx * 73856093) ^ ((long)dy * 19349663) ^ ((long)dz * 83492791);
        }

        /// <summary>
        /// Updates velocities from the position change and writes back
        /// final positions to the asset.
        /// </summary>
        private void UpdateVelocitiesAndPositions(float dt)
        {
            if (_asset == null || _predictedPositions == null || _velocities == null) return;
            var invDt = 1f / MathF.Max(dt, 1e-6f);

            for (var i = 0; i < _asset.TotalVertices; i++)
            {
                var vert = _asset.Vertices[i];
                _velocities[i] = (_predictedPositions[i] - vert.Position) * invDt;
                vert.OldPosition = vert.Position;
                vert.Position = _predictedPositions[i];
                _asset.Vertices[i] = vert;
            }
        }

        /// <summary>
        /// Recomputes strand tangents from vertex positions.
        /// </summary>
        private void UpdateTangents()
        {
            if (_asset == null) return;
            for (var s = 0; s < _asset.Strands.Count; s++)
            {
                var strand = _asset.Strands[s];
                for (var v = 0; v < strand.VertexCount; v++)
                {
                    var idx = strand.VertexStart + v;
                    var vert = _asset.Vertices[idx];
                    if (v < strand.VertexCount - 1)
                    {
                        var next = _asset.Vertices[idx + 1].Position;
                        vert.Tangent = Vector3.Normalize(next - vert.Position);
                    }
                    else
                    {
                        var prev = _asset.Vertices[idx - 1].Position;
                        vert.Tangent = Vector3.Normalize(vert.Position - prev);
                    }
                    _asset.Vertices[idx] = vert;
                }
            }
        }

        /// <summary>
        /// Applies an impulse force to a range of strand vertices.
        /// </summary>
        public void ApplyImpulse(Vector3 worldPos, float radius, Vector3 impulse)
        {
            if (_asset == null || _velocities == null) return;
            for (var i = 0; i < _asset.TotalVertices; i++)
            {
                var dist = (_asset.Vertices[i].Position - worldPos).Length();
                if (dist < radius)
                {
                    var falloff = 1f - dist / radius;
                    _velocities[i] += impulse * falloff;
                }
            }
        }
    }

    // ── Strand Renderer ─────────────────────────────────────────────

    /// <summary>
    /// Renders hair strands as anti-aliased line strips / tube geometry.
    /// Supports LOD-based tessellation and screen-space AA.
    /// </summary>
    public sealed class HairStrandRenderer
    {
        private readonly RHIDevice _device;
        private readonly HairSettings _settings;

        private RHIBuffer? _vertexBuffer;
        private RHIBuffer? _strandBuffer;
        private RHIBuffer? _lodBuffer;
        private RHIDescriptorHandle _vertexSRV;
        private RHIDescriptorHandle _strandSRV;

        private RHIRootSignature? _rootSig;
        private RHIPipelineState? _tessellatePSO;
        private RHIPipelineState? _renderPSO;
        private RHIPipelineState? _meshShaderPSO;

        /// <summary>Output colour SRV.</summary>
        public RHIDescriptorHandle HairColorSRV;
        /// <summary>Output depth SRV.</summary>
        public RHIDescriptorHandle HairDepthSRV;
        private RHITexture? _hairColor;
        private RHITexture? _hairDepth;

        /// <summary>Creates the strand renderer.</summary>
        public HairStrandRenderer(RHIDevice device, HairSettings settings)
        {
            _device = device;
            _settings = settings;
        }

        /// <summary>
        /// Uploads a hair asset's vertex and strand data to GPU buffers.
        /// </summary>
        public void UploadAsset(HairAsset asset)
        {
            // Vertex buffer (structured)
            var vertexBytes = asset.TotalVertices * Marshal.SizeOf<HairStrandVertex>();
            var vbufDesc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)vertexBytes,
                Format = RHIFormat.Unknown,
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.ShaderResource | RHIBufferFlags.VertexBuffer,
                Stride = (ulong)Marshal.SizeOf<HairStrandVertex>()
            };
            _vertexBuffer?.Dispose();
            _vertexBuffer = _device.CreateBuffer(vbufDesc, "HairVertices");
            var vertData = new byte[vertexBytes];
            var vertHandle = GCHandle.Alloc(asset.Vertices.ToArray(), GCHandleType.Pinned);
            try
            {
                Marshal.Copy(vertHandle.AddrOfPinnedObject(), vertData, 0, vertexBytes);
            }
            finally
            {
                vertHandle.Free();
            }
            _device.UploadToBuffer(_vertexBuffer, vertData.AsSpan());

            _vertexSRV = _device.CreateSRV(_vertexBuffer, RHIFormat.Unknown, true, 0, (uint)asset.TotalVertices, (uint)Marshal.SizeOf<HairStrandVertex>());

            // Strand buffer (structured)
            var strandBytes = asset.TotalStrands * Marshal.SizeOf<HairStrand>();
            var sbufDesc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)strandBytes,
                Format = RHIFormat.Unknown,
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.ShaderResource,
                Stride = (ulong)Marshal.SizeOf<HairStrand>()
            };
            _strandBuffer?.Dispose();
            _strandBuffer = _device.CreateBuffer(sbufDesc, "HairStrands");
            var strandData = new byte[strandBytes];
            var strandHandle = GCHandle.Alloc(asset.Strands.ToArray(), GCHandleType.Pinned);
            try
            {
                Marshal.Copy(strandHandle.AddrOfPinnedObject(), strandData, 0, strandBytes);
            }
            finally
            {
                strandHandle.Free();
            }
            _device.UploadToBuffer(_strandBuffer, strandData.AsSpan());

            _strandSRV = _device.CreateSRV(_strandBuffer, RHIFormat.Unknown, true, 0, (uint)asset.TotalStrands, (uint)Marshal.SizeOf<HairStrand>());
        }

        /// <summary>
        /// Allocates screen-space render targets for hair colour and depth.
        /// </summary>
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
            _hairColor?.Dispose();
            _hairColor = _device.CreateTexture(colorDesc, "HairColor");
            HairColorSRV = _device.CreateSRV(_hairColor);

            var depthDesc = new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.D32_Float,
                Width = width, Height = height, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1, SampleQuality = 0,
                Flags = RHITextureFlags.DepthStencil | RHITextureFlags.ShaderResource,
                OptimizedClearValue = RHIClearValueHelper.Depth(1f, 0, RHIFormat.D32_Float)
            };
            _hairDepth?.Dispose();
            _hairDepth = _device.CreateTexture(depthDesc, "HairDepth");
            HairDepthSRV = _device.CreateSRV(_hairDepth);
        }

        /// <summary>
        /// Renders hair strands using the mesh shader pipeline if available,
        /// otherwise falls back to tessellated line strips.
        /// </summary>
        public unsafe void Render(RHICommandList cmd, int width, int height,
            Matrix4x4 viewProj, Vector3 cameraPos, Vector3 lightDir,
            Vector3 lightColor, RHIDescriptorHandle sceneDepthSRV,
            int strandCount, int maxVerticesPerStrand)
        {
            if (_hairColor == null) return;

            BuildPipelines();

            // Clear hair colour and depth
            var colorRTV = _device.CreateRTV(_hairColor);
            var depthDSV = _device.CreateDSV(_hairDepth!);
            cmd.ClearRenderTargetView(colorRTV, Vector4.Zero);
            cmd.ClearDepthStencilView(depthDSV, 1f, 0);

            // Bind targets
            cmd.OMSetRenderTargets(new[] { colorRTV }, depthDSV);
            cmd.SetViewport(new RHIViewport(width, height));
            cmd.SetScissorRect(new RHIScissorRect(0, 0, width, height));

            // Choose pipeline based on hardware support
            if (_device.SupportsMeshShaders && _meshShaderPSO != null)
            {
                RenderMeshShader(cmd, viewProj, cameraPos, lightDir, lightColor,
                    sceneDepthSRV, strandCount, maxVerticesPerStrand, width, height);
            }
            else if (_renderPSO != null)
            {
                RenderTessellated(cmd, viewProj, cameraPos, lightDir, lightColor,
                    sceneDepthSRV, strandCount, maxVerticesPerStrand, width, height);
            }
        }

        private unsafe void RenderMeshShader(RHICommandList cmd,
            Matrix4x4 viewProj, Vector3 cameraPos, Vector3 lightDir,
            Vector3 lightColor, RHIDescriptorHandle sceneDepthSRV,
            int strandCount, int maxVerts, int width, int height)
        {
            cmd.SetPipelineState(_meshShaderPSO!);
            cmd.SetComputeRootSignature(_rootSig!);

            cmd.SetComputeRootDescriptorTable(1, _vertexSRV);
            cmd.SetComputeRootDescriptorTable(2, _strandSRV);
            cmd.SetComputeRootDescriptorTable(3, sceneDepthSRV);

            var cb = new HairRenderCB
            {
                ViewProj = viewProj,
                CameraPos = cameraPos,
                LightDir = lightDir,
                LightColor = lightColor,
                HairColor = _settings.HairColor,
                StrandRadius = _settings.StrandRadius,
                RIntensity = _settings.RIntensity,
                TTIntensity = _settings.TTIntensity,
                TRTIntensity = _settings.TRTIntensity,
                LongitudinalRoughness = _settings.LongitudinalRoughness,
                AzimuthalRoughness = _settings.AzimuthalRoughness,
                HairIOR = _settings.HairIOR,
                StrandCount = strandCount,
                MaxVertsPerStrand = maxVerts,
                Resolution = new Vector2(width, height),
                TessellationPixelsPerSegment = _settings.TessellationPixelsPerSegment
            };
            cmd.SetComputeRoot32BitConstants(0, (uint)(sizeof(HairRenderCB) / 4), in cb, 0);

            // One thread group per N strands (amplification shader decides)
            var groups = (strandCount + 31) / 32;
            cmd.DispatchMesh(groups, 1, 1);
        }

        private unsafe void RenderTessellated(RHICommandList cmd,
            Matrix4x4 viewProj, Vector3 cameraPos, Vector3 lightDir,
            Vector3 lightColor, RHIDescriptorHandle sceneDepthSRV,
            int strandCount, int maxVerts, int width, int height)
        {
            cmd.SetPipelineState(_renderPSO!);
            cmd.SetComputeRootSignature(_rootSig!);

            cmd.SetComputeRootDescriptorTable(1, _vertexSRV);
            cmd.SetComputeRootDescriptorTable(2, _strandSRV);
            cmd.SetComputeRootDescriptorTable(3, sceneDepthSRV);

            var cb = new HairRenderCB
            {
                ViewProj = viewProj,
                CameraPos = cameraPos,
                LightDir = lightDir,
                LightColor = lightColor,
                HairColor = _settings.HairColor,
                StrandRadius = _settings.StrandRadius,
                RIntensity = _settings.RIntensity,
                TTIntensity = _settings.TTIntensity,
                TRTIntensity = _settings.TRTIntensity,
                LongitudinalRoughness = _settings.LongitudinalRoughness,
                AzimuthalRoughness = _settings.AzimuthalRoughness,
                HairIOR = _settings.HairIOR,
                StrandCount = strandCount,
                MaxVertsPerStrand = maxVerts,
                Resolution = new Vector2(width, height),
                TessellationPixelsPerSegment = _settings.TessellationPixelsPerSegment
            };
            cmd.SetComputeRoot32BitConstants(0, (uint)(sizeof(HairRenderCB) / 4), in cb, 0);

            // Draw each strand as a tessellated line strip
            // In practice this uses DrawIndexedInstancedIndirect with GPU-computed args
            cmd.IASetPrimitiveTopology(RHIPrimitiveTopology.TriangleList);
            // Each strand segment generates a quad (2 triangles)
            var totalSegments = strandCount * (maxVerts - 1);
            var vertexCount = totalSegments * 6;
            cmd.DrawInstanced(vertexCount, 1, 0, 0);
        }

        /// <summary>
        /// Computes the screen-space LOD level for a strand based on its
        /// projected screen size.
        /// </summary>
        public int ComputeLOD(Vector3 strandPos, Vector3 cameraPos, int screenWidth)
        {
            var dist = (strandPos - cameraPos).Length();
            if (dist < 1e-6f) return 0;
            // Approximate screen size in pixels
            var screenSize = (_settings.StrandRadius * screenWidth) / dist;
            if (screenSize > 16f) return 0;
            if (screenSize > 8f) return 1;
            if (screenSize > 4f) return 2;
            if (screenSize > 2f) return 3;
            return Math.Min(_settings.MaxStrandLOD, 4);
        }

        private void BuildPipelines()
        {
            if (_rootSig != null) return;

            var rootDesc = new RHIRootSignatureDesc
            {
                Parameters = new[]
                {
                    RootParam.Constants(1, 0),
                    RootParam.SRV(1, 0),   // Vertex buffer
                    RootParam.SRV(1, 1),   // Strand buffer
                    RootParam.SRV(1, 2),   // Scene depth
                },
                AllowInputAssembler = true
            };
            _rootSig = _device.CreateRootSignature(rootDesc);

            // Tessellated pipeline
            var tessDesc = new RHIGraphicsPipelineDesc
            {
                Name = "HairStrandTessellated",
                PrimitiveTopology = RHIPrimitiveTopology.TriangleList,
                BlendState = RHIBlendDesc.AlphaBlend,
                DepthStencilState = RHIDepthDesc.Default,
                RasterizerState = RHIRasterizerDesc.NoCull,
                RTVFormats = new[] { RHIFormat.R16G16B16A16_Float },
                DSVFormat = RHIFormat.D32_Float,
                VS = ShaderManager.Instance.GetBytecode("Hair/Hair", "HairVS", RHIShaderStage.Vertex),
                PS = ShaderManager.Instance.GetBytecode("Hair/Hair", "HairPS", RHIShaderStage.Pixel)
            };
            _renderPSO = _device.CreateGraphicsPipelineState(tessDesc);

            // Mesh shader pipeline
            if (_device.SupportsMeshShaders)
            {
                var meshDesc = new RHIMeshPipelineDesc
                {
                    Name = "HairStrandMeshShader",
                    BlendState = RHIBlendDesc.AlphaBlend,
                    DepthStencilState = RHIDepthDesc.Default,
                    RasterizerState = RHIRasterizerDesc.NoCull,
                    RTVFormats = new[] { RHIFormat.R16G16B16A16_Float },
                    DSVFormat = RHIFormat.D32_Float,
                    MS = ShaderManager.Instance.GetBytecode("Hair/Hair", "HairMS", RHIShaderStage.Mesh),
                    PS = ShaderManager.Instance.GetBytecode("Hair/Hair", "HairPS", RHIShaderStage.Pixel)
                };
                _meshShaderPSO = _device.CreateMeshPipelineState(meshDesc);
            }
        }

        /// <summary>
        /// Computes anti-aliased coverage for a hair strand fragment.
        /// Uses a coverage mask based on sub-pixel strand position.
        /// </summary>
        public static float ComputeAACoverage(
            Vector2 pixelCenter, Vector2 strandCenter,
            float strandRadius, Vector2 strandDir, int samples)
        {
            // Sample the strand cross-section at multiple sub-pixel positions
            var coverage = 0f;
            var perp = new Vector2(-strandDir.Y, strandDir.X);
            var sampleWeight = 1f / samples;

            for (var i = 0; i < samples; i++)
            {
                // Stratified sample positions within the pixel
                var offset = new Vector2(
                    (i + 0.5f) / samples - 0.5f,
                    ((i * 7919) % samples + 0.5f) / samples - 0.5f);
                var samplePos = pixelCenter + offset;
                var delta = samplePos - strandCenter;
                var perpDist = MathF.Abs(Vector2.Dot(delta, perp));
                if (perpDist < strandRadius)
                    coverage += sampleWeight;
            }

            return coverage;
        }
    }

    // ── Shell-based Fur Renderer ────────────────────────────────────

    /// <summary>
    /// Shell-based fur rendering fallback. Renders the mesh multiple times
    /// at increasing offsets along the normal, with a noise-based fur
    /// texture controlling density and length. This is a cheaper
    /// alternative to strand-based hair for dense fur coverage.
    /// </summary>
    public sealed class ShellFurRenderer
    {
        private readonly RHIDevice _device;
        private readonly HairSettings _settings;

        private RHITexture? _furTexture;
        private RHIDescriptorHandle _furTextureSRV;

        private RHIRootSignature? _rootSig;
        private RHIPipelineState? _shellPSO;

        /// <summary>Creates the shell fur renderer.</summary>
        public ShellFurRenderer(RHIDevice device, HairSettings settings)
        {
            _device = device;
            _settings = settings;
        }

        /// <summary>
        /// Generates the fur density texture used by the shell renderer.
        /// This is a 2D texture where each texel controls whether a fur
        /// strand exists at that UV location.
        /// </summary>
        public void GenerateFurTexture(int size = 256)
        {
            var data = new byte[size * size * 4];
            var rng = new Random(42);

            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var idx = (y * size + x) * 4;

                // Poisson-disk-like density distribution
                var fx = (float)x / size;
                var fy = (float)y / size;

                // Multi-octave noise for fur density
                var density = 0f;
                density += Noise2D(fx * 8f, fy * 8f) * 0.5f;
                density += Noise2D(fx * 16f, fy * 16f) * 0.25f;
                density += Noise2D(fx * 32f, fy * 32f) * 0.125f;
                density = Math.Clamp(density * 2f + 0.5f, 0f, 1f);

                // Apply density threshold
                var hasFur = density > (1f - _settings.FurDensity) ? 1f : 0f;

                // Random strand length variation
                var lengthVar = (float)(rng.NextDouble() * 0.3f + 0.7f);

                // Random direction variation
                var dirX = (float)(rng.NextDouble() * 2f - 1f) * 0.2f;
                var dirY = (float)(rng.NextDouble() * 2f - 1f) * 0.2f;

                data[idx + 0] = (byte)(hasFur * 255f);
                data[idx + 1] = (byte)(lengthVar * 255f);
                data[idx + 2] = (byte)((dirX * 0.5f + 0.5f) * 255f);
                data[idx + 3] = (byte)((dirY * 0.5f + 0.5f) * 255f);
            }

            var desc = new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R8G8B8A8_UNorm,
                Width = size, Height = size, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1, SampleQuality = 0,
                Flags = RHITextureFlags.ShaderResource
            };
            _furTexture = _device.CreateTexture(desc, "FurTexture");
            var byteSpan = MemoryMarshal.AsBytes(data.AsSpan());
            _device.UploadToTexture(_furTexture, byteSpan, size, size, 0, 0);
            _furTextureSRV = _device.CreateSRV(_furTexture);
        }

        /// <summary>
        /// Renders fur shells over a mesh. The mesh is rendered N times,
        /// each time offset along the vertex normal by a fraction of the
        /// fur length. Each shell samples the fur texture to determine
        /// visibility and colour.
        /// </summary>
        public unsafe void Render(RHICommandList cmd, RHIBuffer vertexBuffer,
            RHIBuffer indexBuffer, int indexCount, Matrix4x4 worldMatrix,
            Matrix4x4 viewProj, Vector3 cameraPos, int width, int height)
        {
            if (_furTexture == null)
            {
                GenerateFurTexture();
            }
            if (_shellPSO == null)
            {
                BuildPipeline();
                if (_shellPSO == null) return;
            }

            cmd.SetPipelineState(_shellPSO);
            cmd.SetComputeRootSignature(_rootSig!);
            cmd.SetComputeRootDescriptorTable(1, _furTextureSRV);

            cmd.IASetPrimitiveTopology(RHIPrimitiveTopology.TriangleList);
            cmd.IASetIndexBuffer(indexBuffer, RHIFormat.R32_UInt, 0);

            cmd.SetViewport(new RHIViewport(width, height));
            cmd.SetScissorRect(new RHIScissorRect(0, 0, width, height));

            var shellCount = _settings.FurShellCount;
            var furLength = _settings.FurLength;

            for (var shell = 0; shell < shellCount; shell++)
            {
                var layer = (float)shell / (shellCount - 1);
                var offset = layer * furLength;

                var cb = new ShellFurCB
                {
                    WorldMatrix = worldMatrix,
                    ViewProj = viewProj,
                    CameraPos = cameraPos,
                    ShellOffset = offset,
                    LayerFraction = layer,
                    FurLength = furLength,
                    FurBaseColor = _settings.FurBaseColor,
                    FurTipColor = _settings.FurTipColor,
                    ShellIndex = shell,
                    ShellCount = shellCount
                };
                cmd.SetComputeRoot32BitConstants(0, (uint)(sizeof(ShellFurCB) / 4), in cb, 0);

                cmd.DrawIndexedInstanced(indexCount, 1, 0, 0, 0);
            }
        }

        private void BuildPipeline()
        {
            var rootDesc = new RHIRootSignatureDesc
            {
                Parameters = new[]
                {
                    RootParam.Constants(1, 0),
                    RootParam.SRV(1, 0),  // Fur texture
                },
                AllowInputAssembler = true
            };
            _rootSig = _device.CreateRootSignature(rootDesc);

            var psoDesc = new RHIGraphicsPipelineDesc
            {
                Name = "ShellFur",
                PrimitiveTopology = RHIPrimitiveTopology.TriangleList,
                BlendState = RHIBlendDesc.AlphaBlend,
                DepthStencilState = RHIDepthDesc.DepthRead,
                RasterizerState = RHIRasterizerDesc.NoCull,
                RTVFormats = new[] { RHIFormat.R16G16B16A16_Float },
                DSVFormat = RHIFormat.D32_Float,
                VS = ShaderManager.Instance.GetBytecode("Hair/Hair", "ShellFurVS", RHIShaderStage.Vertex),
                PS = ShaderManager.Instance.GetBytecode("Hair/Hair", "ShellFurPS", RHIShaderStage.Pixel)
            };
            _shellPSO = _device.CreateGraphicsPipelineState(psoDesc);
        }

        /// <summary>Simple 2D value noise.</summary>
        private static float Noise2D(float x, float y)
        {
            var xi = (int)MathF.Floor(x);
            var yi = (int)MathF.Floor(y);
            var xf = x - MathF.Floor(x);
            var yf = y - MathF.Floor(y);

            var fade = xf * xf * (3f - 2f * xf);
            var fadey = yf * yf * (3f - 2f * yf);

            var h00 = Hash2D(xi, yi);
            var h10 = Hash2D(xi + 1, yi);
            var h01 = Hash2D(xi, yi + 1);
            var h11 = Hash2D(xi + 1, yi + 1);

            var x0 = Lerp(h00, h10, fade);
            var x1 = Lerp(h01, h11, fade);
            return Lerp(x0, x1, fadey) * 2f - 1f;
        }

        private static float Hash2D(int x, int y)
        {
            var h = (x * 374761393 + y * 668265263) & 0x7FFFFFFF;
            h = (h ^ (h >> 13)) * 1274126177;
            return ((h ^ (h >> 16)) & 0x7FFFFFFF) / (float)0x7FFFFFFF;
        }

        private static float Lerp(float a, float b, float t) => a + t * (b - a);
    }

    // ── Constant buffers ────────────────────────────────────────────

    /// <summary>Constant buffer for hair strand rendering.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct HairRenderCB
    {
        /// <summary>View-projection matrix.</summary>
        public Matrix4x4 ViewProj;
        /// <summary>Camera world position.</summary>
        public Vector3 CameraPos;
        /// <summary>Light direction.</summary>
        public Vector3 LightDir;
        /// <summary>Light colour.</summary>
        public Vector3 LightColor;
        /// <summary>Hair base colour.</summary>
        public Vector3 HairColor;
        /// <summary>Strand radius.</summary>
        public float StrandRadius;
        /// <summary>R lobe intensity.</summary>
        public float RIntensity;
        /// <summary>TT lobe intensity.</summary>
        public float TTIntensity;
        /// <summary>TRT lobe intensity.</summary>
        public float TRTIntensity;
        /// <summary>Longitudinal roughness.</summary>
        public float LongitudinalRoughness;
        /// <summary>Azimuthal roughness.</summary>
        public float AzimuthalRoughness;
        /// <summary>Hair index of refraction.</summary>
        public float HairIOR;
        /// <summary>Total strand count.</summary>
        public int StrandCount;
        /// <summary>Max vertices per strand.</summary>
        public int MaxVertsPerStrand;
        /// <summary>Screen resolution.</summary>
        public Vector2 Resolution;
        /// <summary>Tessellation factor (pixels per segment).</summary>
        public float TessellationPixelsPerSegment;
    }

    /// <summary>Constant buffer for shell-based fur rendering.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ShellFurCB
    {
        /// <summary>World matrix.</summary>
        public Matrix4x4 WorldMatrix;
        /// <summary>View-projection matrix.</summary>
        public Matrix4x4 ViewProj;
        /// <summary>Camera world position.</summary>
        public Vector3 CameraPos;
        /// <summary>Shell offset along normal.</summary>
        public float ShellOffset;
        /// <summary>Layer fraction [0,1].</summary>
        public float LayerFraction;
        /// <summary>Total fur length.</summary>
        public float FurLength;
        /// <summary>Fur base colour.</summary>
        public Vector3 FurBaseColor;
        /// <summary>Fur tip colour.</summary>
        public Vector3 FurTipColor;
        /// <summary>Current shell index.</summary>
        public int ShellIndex;
        /// <summary>Total shell count.</summary>
        public int ShellCount;
    }

    // ── Master Hair Renderer ────────────────────────────────────────

    /// <summary>
    /// Master hair and fur renderer that orchestrates strand-based rendering,
    /// PBD simulation, and shell-based fur fallback.
    /// </summary>
    public sealed class HairRenderer : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly HairSettings _settings;

        /// <summary>PBD hair simulator.</summary>
        public readonly HairPBDSimulator Simulator;
        /// <summary>Strand-based hair renderer.</summary>
        public readonly HairStrandRenderer Strands;
        /// <summary>Shell-based fur renderer.</summary>
        public readonly ShellFurRenderer Shells;

        private HairAsset? _asset;
        private bool _disposed;

        /// <summary>Creates the master hair renderer.</summary>
        public HairRenderer(RHIDevice device, HairSettings? settings = null)
        {
            _device = device;
            _settings = settings ?? new HairSettings();
            Simulator = new HairPBDSimulator(_settings);
            Strands = new HairStrandRenderer(device, _settings);
            Shells = new ShellFurRenderer(device, _settings);
        }

        /// <summary>
        /// Loads a hair asset for rendering and simulation.
        /// </summary>
        public void LoadAsset(HairAsset asset)
        {
            _asset = asset;
            if (_settings.EnableSimulation)
                Simulator.SetAsset(asset);
            if (_settings.EnableStrandRendering)
                Strands.UploadAsset(asset);
        }

        /// <summary>
        /// Updates the hair simulation for this frame.
        /// </summary>
        public void UpdateSimulation(float dt)
        {
            if (!_settings.EnableSimulation || _asset == null) return;
            Simulator.Simulate(dt);
            // Re-upload vertex positions if using strand rendering
            if (_settings.EnableStrandRendering)
                Strands.UploadAsset(_asset);
        }

        /// <summary>
        /// Renders hair/fur. Uses strand-based rendering by default and
        /// falls back to shell-based fur when <see cref="HairSettings.EnableShellFur"/>
        /// is true.
        /// </summary>
        public void Render(RHICommandList cmd, int width, int height,
            Matrix4x4 viewProj, Vector3 cameraPos, Vector3 lightDir,
            Vector3 lightColor, RHIDescriptorHandle sceneDepthSRV,
            RHIBuffer? furMeshVB, RHIBuffer? furMeshIB, int furMeshIndexCount,
            Matrix4x4 furMeshWorld)
        {
            if (_settings.EnableShellFur && furMeshVB != null && furMeshIB != null)
            {
                Shells.Render(cmd, furMeshVB, furMeshIB, furMeshIndexCount,
                    furMeshWorld, viewProj, cameraPos, width, height);
            }
            else if (_settings.EnableStrandRendering && _asset != null)
            {
                Strands.Render(cmd, width, height, viewProj, cameraPos,
                    lightDir, lightColor, sceneDepthSRV,
                    _asset.TotalStrands, _asset.MaxVerticesPerStrand);
            }
        }

        /// <summary>
        /// Resizes all screen-space targets.
        /// </summary>
        public void OnResize(int width, int height)
        {
            Strands.AllocateTargets(width, height);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
        }
    }
}
