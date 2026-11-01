using YamaEngine.Testing;

namespace YamaEngine.Tests
{
    /// <summary>
    /// 建模测试：EditableMesh、MeshEditOperations、UVUnwrapper
    /// </summary>
    [TestClass]
    public class ModelingTests : EngineTestFixture
    {
        [TestMethod]
        public void EditableMeshCubeSaveLoadRoundtrip()
        {
            var projectPath = Path.Combine(TempDirectory, "MeshProject");
            Directory.CreateDirectory(projectPath);
            var meshPath = Path.Combine(projectPath, "Assets", "Meshes", "EditableCube.shmesh");

            var mesh = YamaEngine.Modeling.EditableMesh.CreateCube("EditableCube", 2f);
            mesh.RecalculateNormals();
            mesh.Write(meshPath);

            Assert.IsTrue(File.Exists(meshPath));
            Assert.IsTrue(File.Exists(meshPath + ".shmeta"));

            var loaded = YamaEngine.Modeling.EditableMesh.Read(meshPath);
            Assert.AreEqual("EditableCube", loaded.Name);
            Assert.AreEqual(8, loaded.Vertices.Count);
            Assert.AreEqual(12, loaded.Faces.Count);
            Assert.AreEqual(8, loaded.Normals.Count);
            Assert.AreEqual(0, loaded.Validate().Count);

            var meta = YamaEngine.Assets.AssetMeta.Read(meshPath + ".shmeta");
            Assert.AreEqual(YamaEngine.Assets.AssetType.Mesh.ToString(), meta.Type);
        }

        [TestMethod]
        public void MeshEditExtrudeFaceCreatesCapAndSideWalls()
        {
            var mesh = YamaEngine.Modeling.EditableMesh.CreateCube("ExtrudeCube", 2f);
            int originalVertexCount = mesh.Vertices.Count;
            int originalFaceCount = mesh.Faces.Count;

            var result = YamaEngine.Modeling.MeshEditOperations.ExtrudeFace(mesh, 0, 0.5f);

            Assert.AreEqual(originalVertexCount + 3, mesh.Vertices.Count);
            Assert.AreEqual(originalFaceCount + 6, mesh.Faces.Count);
            Assert.AreEqual(0, mesh.Validate().Count);
            Assert.AreEqual(3, result.CapVertices.Length);
        }

        [TestMethod]
        public void MeshEditInsetFaceShrinksRingTowardsCentroid()
        {
            var mesh = YamaEngine.Modeling.EditableMesh.CreateCube("InsetCube", 2f);
            int originalVertexCount = mesh.Vertices.Count;
            int originalFaceCount = mesh.Faces.Count;

            var result = YamaEngine.Modeling.MeshEditOperations.InsetFace(mesh, 0, 0.4f);

            Assert.AreEqual(originalVertexCount + 3, mesh.Vertices.Count);
            Assert.AreEqual(originalFaceCount + 6, mesh.Faces.Count);
            Assert.AreEqual(0, mesh.Validate().Count);
        }

        [TestMethod]
        public void MeshEditBevelEdgeChamfersTriangle()
        {
            var mesh = YamaEngine.Modeling.EditableMesh.CreateCube("BevelCube", 2f);
            int originalFaceCount = mesh.Faces.Count;
            int originalVertexCount = mesh.Vertices.Count;
            var face = mesh.Faces[0];

            var result = YamaEngine.Modeling.MeshEditOperations.BevelEdge(mesh, 0, face.A, face.B, 0.25f);

            Assert.AreEqual(originalVertexCount + 2, mesh.Vertices.Count);
            Assert.AreEqual(originalFaceCount + 2, mesh.Faces.Count);
            Assert.AreEqual(2, result.InsertedVertices.Length);
            Assert.AreEqual(0, mesh.Validate().Count);
        }

        [TestMethod]
        public void UvUnwrapperPlanarBoxAndSphericalProjection()
        {
            var mesh = YamaEngine.Modeling.EditableMesh.CreateCube("UvCube", 2f);

            YamaEngine.Modeling.UVUnwrapper.ProjectPlanar(mesh, YamaEngine.Modeling.UvProjectionAxis.Z);
            Assert.AreEqual(mesh.Vertices.Count, mesh.UV0.Count);
            Assert.IsTrue(mesh.UV0.All(uv => uv.X >= 0f && uv.X <= 1f && uv.Y >= 0f && uv.Y <= 1f));

            YamaEngine.Modeling.UVUnwrapper.ProjectBox(mesh);
            Assert.AreEqual(mesh.Vertices.Count, mesh.UV0.Count);

            YamaEngine.Modeling.UVUnwrapper.ProjectSpherical(mesh);
            Assert.AreEqual(mesh.Vertices.Count, mesh.UV0.Count);
            Assert.IsTrue(mesh.UV0.All(uv => uv.X >= 0f && uv.X <= 1f && uv.Y >= 0f && uv.Y <= 1f));
        }

        [TestMethod]
        public void MeshEditLoopCutSplitsSharedEdge()
        {
            var mesh = YamaEngine.Modeling.EditableMesh.CreateCube("LoopCutCube", 2f);
            var faceA = mesh.Faces[0];
            int originalFaceCount = mesh.Faces.Count;
            int originalVertexCount = mesh.Vertices.Count;

            var result = YamaEngine.Modeling.MeshEditOperations.LoopCut(mesh, 0, 1, faceA.A, faceA.B, 0.5f);

            Assert.AreEqual(originalVertexCount + 1, mesh.Vertices.Count);
            Assert.AreEqual(originalFaceCount + 2, mesh.Faces.Count);
            Assert.IsTrue(result.InsertedVertex >= originalVertexCount);
            Assert.AreEqual(0, mesh.Validate().Count);
        }
    }
}
