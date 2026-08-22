using System;
using System.IO;
using System.Numerics;
using System.Linq;
using System.Collections.Generic;
using ShanyuEngine.Modeling;
using static ShanyuEngine.Tests.TestHelpers;

namespace ShanyuEngine.Tests
{
    public static class SculptTests
    {
        public static void TestSculptInflateBrushMovesVerticesAlongNormals()
        {
            var mesh = CreateSculptPlane();
            var beforeCenter = mesh.Vertices[4];

            var result = SculptTool.Apply(mesh, new SculptBrushSettings
            {
                Type = SculptBrushType.Inflate,
                Center = Vector3.Zero,
                Radius = 1.1f,
                Strength = 0.5f
            });

            AssertTrue(result.AffectedVertices > 0, "Inflate brush should affect vertices inside radius.");
            AssertTrue(mesh.Vertices[4].Z > beforeCenter.Z + 0.45f, "Inflate brush should move center vertex along normal.");
            AssertTrue(mesh.Vertices[0].Z == 0f, "Inflate brush should not affect vertices outside radius.");
            AssertEqual(0, mesh.Validate().Count, "Inflated mesh should remain valid.");
        }

        public static void TestSculptSmoothBrushReducesSurfaceNoise()
        {
            var mesh = CreateSculptPlane();
            mesh.Vertices[4] = new Vector3(0, 0, 2f);
            mesh.RecalculateNormals();

            var result = SculptTool.Apply(mesh, new SculptBrushSettings
            {
                Type = SculptBrushType.Smooth,
                Center = Vector3.Zero,
                Radius = 1.5f,
                Strength = 0.75f
            });

            AssertTrue(result.AffectedVertices > 0, "Smooth brush should affect noisy vertices.");
            AssertTrue(mesh.Vertices[4].Z < 2f, "Smooth brush should reduce the raised center height.");
            AssertTrue(mesh.Vertices[4].Z > 0f, "Smooth brush should preserve some local shape instead of flattening instantly.");
            AssertEqual(0, mesh.Validate().Count, "Smoothed mesh should remain valid.");
        }

        public static void TestSculptGrabBrushMovesVerticesWithFalloffAndMask()
        {
            var mesh = CreateSculptPlane();
            var mask = new SculptMask();
            mask.SetWeight(4, 0f);

            var result = SculptTool.Apply(mesh, new SculptBrushSettings
            {
                Type = SculptBrushType.Grab,
                Center = Vector3.Zero,
                Radius = 1.5f,
                Strength = 1f,
                Direction = Vector3.UnitX,
                Mask = mask
            });

            AssertTrue(result.AffectedVertices > 0, "Grab brush should affect vertices inside radius.");
            AssertEqual(Vector3.Zero, mesh.Vertices[4], "Masked center vertex should stay fixed.");
            AssertTrue(mesh.Vertices[1].X > 0f, "Unmasked nearby vertex should move along grab direction.");
            AssertTrue(result.MaxDisplacement <= 1.01f, "Grab displacement should be controlled by strength and falloff.");
            AssertEqual(0, mesh.Validate().Count, "Grabbed mesh should remain valid.");
        }
    }
}
