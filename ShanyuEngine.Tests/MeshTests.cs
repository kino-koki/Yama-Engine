using System;
using System.IO;
using System.Numerics;
using System.Linq;
using System.Collections.Generic;
using ShanyuEngine.Assets;
using ShanyuEngine.Modeling;
using static ShanyuEngine.Tests.TestHelpers;

namespace ShanyuEngine.Tests
{
    public static class MeshTests
    {
        public static void TestEditableMeshCubeSaveLoadRoundtrip(string tempRoot)
        {
            var projectPath = Path.Combine(tempRoot, "MeshProject");
            Directory.CreateDirectory(projectPath);
            var meshPath = Path.Combine(projectPath, "Assets", "Meshes", "EditableCube.shmesh");

            var mesh = EditableMesh.CreateCube("EditableCube", 2f);
            mesh.RecalculateNormals();
            mesh.Write(meshPath);

            AssertTrue(File.Exists(meshPath), "Editable mesh file should be written.");
            AssertTrue(File.Exists(meshPath + ".shmeta"), "Editable mesh meta should be written.");

            var loaded = EditableMesh.Read(meshPath);
            AssertEqual("EditableCube", loaded.Name, "Mesh name should roundtrip.");
            AssertEqual(8, loaded.Vertices.Count, "Cube should have 8 vertices.");
            AssertEqual(12, loaded.Faces.Count, "Cube should have 12 triangle faces.");
            AssertEqual(8, loaded.Normals.Count, "Normals should be generated for each vertex.");
            AssertEqual(0, loaded.Validate().Count, "Loaded mesh should have no validation errors.");

            var meta = AssetMeta.Read(meshPath + ".shmeta");
            AssertEqual(AssetType.Mesh.ToString(), meta.Type, "Editable mesh should register as Mesh asset.");
            AssertEqual("Assets/Meshes/EditableCube.shmesh", meta.AssetPath, "Mesh meta should store project relative asset path.");
        }

        public static void TestMeshEditExtrudeFaceCreatesCapAndSideWalls()
        {
            var mesh = EditableMesh.CreateCube("ExtrudeCube", 2f);
            int originalFaceCount = mesh.Faces.Count;
            int originalVertexCount = mesh.Vertices.Count;
            var originalFace = mesh.Faces[0];
            var originalCentroid = (mesh.Vertices[originalFace.A] + mesh.Vertices[originalFace.B] + mesh.Vertices[originalFace.C]) / 3f;

            var result = MeshEditOperations.ExtrudeFace(mesh, 0, 0.5f);

            AssertEqual(originalVertexCount + 3, mesh.Vertices.Count, "Extrude should add 3 new vertices.");
            AssertEqual(originalFaceCount + 6, mesh.Faces.Count, "Extrude should add 6 side-wall triangles (3 quads).");
            AssertEqual(0, mesh.Validate().Count, "Extruded mesh should still be valid.");
            AssertEqual(3, result.CapVertices.Length, "Extrude result should expose 3 cap vertices.");

            var cap = mesh.Faces[result.CapFaceIndex];
            var capCentroid = (mesh.Vertices[cap.A] + mesh.Vertices[cap.B] + mesh.Vertices[cap.C]) / 3f;
            AssertTrue(Vector3.Distance(capCentroid, originalCentroid) > 0.4f, "Extruded cap should move along the face normal.");
        }

        public static void TestMeshEditInsetFaceShrinksRingTowardsCentroid()
        {
            var mesh = EditableMesh.CreateCube("InsetCube", 2f);
            int originalFaceCount = mesh.Faces.Count;
            int originalVertexCount = mesh.Vertices.Count;
            var originalFace = mesh.Faces[0];
            var originalArea = TriangleArea(mesh, originalFace);

            var result = MeshEditOperations.InsetFace(mesh, 0, 0.4f);

            AssertEqual(originalVertexCount + 3, mesh.Vertices.Count, "Inset should add 3 new vertices.");
            AssertEqual(originalFaceCount + 6, mesh.Faces.Count, "Inset should add 6 ring triangles.");
            AssertEqual(0, mesh.Validate().Count, "Inset mesh should still be valid.");

            var inner = mesh.Faces[result.CapFaceIndex];
            var innerArea = TriangleArea(mesh, inner);
            AssertTrue(innerArea < originalArea * 0.6f, "Inset cap should have smaller area than original face.");
        }

        public static void TestMeshEditBevelEdgeChamfersTriangle()
        {
            var mesh = EditableMesh.CreateCube("BevelCube", 2f);
            int originalFaceCount = mesh.Faces.Count;
            int originalVertexCount = mesh.Vertices.Count;
            var face = mesh.Faces[0];

            var result = MeshEditOperations.BevelEdge(mesh, 0, face.A, face.B, 0.25f);

            AssertEqual(originalVertexCount + 2, mesh.Vertices.Count, "Bevel should add 2 new vertices on the chamfered edge.");
            AssertEqual(originalFaceCount + 2, mesh.Faces.Count, "Bevel should split the original triangle into 3 triangles total (net +2).");
            AssertEqual(2, result.InsertedVertices.Length, "Bevel result should expose two chamfer vertices.");
            AssertEqual(0, mesh.Validate().Count, "Beveled mesh should still be valid.");
        }

        public static void TestMeshEditLoopCutSplitsSharedEdge()
        {
            var mesh = EditableMesh.CreateCube("LoopCutCube", 2f);
            // Cube triangles 0 and 1 share edge (0,2) since they are { (0,2,1), (0,3,2) }.
            var faceA = mesh.Faces[0];
            var faceB = mesh.Faces[1];
            int sharedStart = faceA.A; // 0
            int sharedEnd = faceA.B;   // 2
            int originalFaceCount = mesh.Faces.Count;
            int originalVertexCount = mesh.Vertices.Count;

            var result = MeshEditOperations.LoopCut(mesh, 0, 1, sharedStart, sharedEnd, 0.5f);

            AssertEqual(originalVertexCount + 1, mesh.Vertices.Count, "Loop cut should insert one midpoint vertex.");
            AssertEqual(originalFaceCount + 2, mesh.Faces.Count, "Loop cut should split two triangles into four (net +2).");
            AssertTrue(result.InsertedVertex >= originalVertexCount, "Loop cut should expose the inserted vertex index.");
            AssertEqual(0, mesh.Validate().Count, "Loop-cut mesh should still be valid.");
        }

        public static void TestUvUnwrapperPlanarBoxAndSphericalProjection()
        {
            var mesh = EditableMesh.CreateCube("UvCube", 2f);

            UVUnwrapper.ProjectPlanar(mesh, UvProjectionAxis.Z);
            AssertEqual(mesh.Vertices.Count, mesh.UV0.Count, "Planar unwrap should write one UV per vertex.");
            AssertTrue(mesh.UV0.All(uv => uv.X >= 0f && uv.X <= 1f && uv.Y >= 0f && uv.Y <= 1f), "Planar UVs should be normalized to 0..1.");

            UVUnwrapper.ProjectBox(mesh);
            AssertEqual(mesh.Vertices.Count, mesh.UV0.Count, "Box unwrap should keep UV count aligned with vertices.");
            AssertTrue(mesh.UV0.Any(uv => uv.X > 0.9f || uv.Y > 0.9f), "Box unwrap should occupy the available UV space.");

            UVUnwrapper.ProjectSpherical(mesh);
            AssertEqual(mesh.Vertices.Count, mesh.UV0.Count, "Spherical unwrap should keep UV count aligned with vertices.");
            AssertTrue(mesh.UV0.All(uv => uv.X >= 0f && uv.X <= 1f && uv.Y >= 0f && uv.Y <= 1f), "Spherical UVs should be normalized to 0..1.");
        }
    }
}
