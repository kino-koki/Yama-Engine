using YamaEngine.Testing;

namespace YamaEngine.Tests
{
    /// <summary>
    /// 材质测试：PBR 材质保存加载、MaterialGraph
    /// </summary>
    [TestClass]
    public class MaterialTests : EngineTestFixture
    {
        [TestMethod]
        public void PbrMaterialSaveLoadAndMeta()
        {
            var projectPath = Path.Combine(TempDirectory, "MaterialProject");
            var materialPath = Path.Combine(projectPath, "Assets", "Materials", "BrushedSteel.shmat");

            var material = YamaEngine.Materials.PbrMaterial.Create("BrushedSteel");
            material.BaseColor = new System.Numerics.Vector3(0.75f, 0.72f, 0.68f);
            material.Metallic = 1f;
            material.Roughness = 0.28f;
            material.NormalMap = "Assets/Textures/brushed_normal.png";
            material.OcclusionMap = "Assets/Textures/brushed_ao.png";
            material.EmissiveColor = new System.Numerics.Vector3(0.02f, 0.02f, 0.02f);

            material.Write(materialPath);
            var loaded = YamaEngine.Materials.PbrMaterial.Read(materialPath);

            Assert.AreEqual("BrushedSteel", loaded.Name);
            Assert.AreEqual(1f, loaded.Metallic);
            Assert.AreEqual(0.28f, loaded.Roughness);
            Assert.AreEqual("Assets/Textures/brushed_normal.png", loaded.NormalMap);
            Assert.AreEqual(0, loaded.Validate().Count);

            var meta = YamaEngine.Assets.AssetMeta.Read(materialPath + ".shmeta");
            Assert.AreEqual(YamaEngine.Assets.AssetType.Material.ToString(), meta.Type);
        }

        [TestMethod]
        public void MaterialGraphEvaluatesPbrPreviewAndRoundtrips()
        {
            var projectPath = Path.Combine(TempDirectory, "GraphProject");
            var graphPath = Path.Combine(projectPath, "Assets", "Materials", "CopperGraph.shgraph");

            var graph = YamaEngine.Materials.MaterialGraph.Create("CopperGraph");
            var color = graph.AddNode(YamaEngine.Materials.MaterialNodeType.ConstantColor, "CopperColor");
            color.VectorValue = new System.Numerics.Vector4(0.95f, 0.45f, 0.22f, 1f);
            var metallic = graph.AddNode(YamaEngine.Materials.MaterialNodeType.ConstantFloat, "Metallic");
            metallic.FloatValue = 1f;
            var roughness = graph.AddNode(YamaEngine.Materials.MaterialNodeType.ConstantFloat, "Roughness");
            roughness.FloatValue = 0.32f;
            var master = graph.AddNode(YamaEngine.Materials.MaterialNodeType.PbrMaster, "PBR Master");
            graph.Connect(color.Id, "Color", master.Id, "BaseColor");
            graph.Connect(metallic.Id, "Value", master.Id, "Metallic");
            graph.Connect(roughness.Id, "Value", master.Id, "Roughness");

            Assert.AreEqual(0, graph.Validate().Count);
            var preview = graph.EvaluatePreview();
            Assert.AreEqual(1f, preview.Metallic);
            Assert.AreEqual(0.32f, preview.Roughness);

            graph.Write(graphPath);
            var loaded = YamaEngine.Materials.MaterialGraph.Read(graphPath);
            Assert.AreEqual("CopperGraph", loaded.Name);
            Assert.AreEqual(4, loaded.Nodes.Count);
            Assert.AreEqual(3, loaded.Edges.Count);
            Assert.AreEqual(0, loaded.Validate().Count);
        }

        [TestMethod]
        public void MaterialGraphDetectsCyclesAndMissingMaster()
        {
            var missingMaster = YamaEngine.Materials.MaterialGraph.Create("MissingMaster");
            missingMaster.AddNode(YamaEngine.Materials.MaterialNodeType.ConstantFloat, "OnlyValue");
            Assert.IsTrue(missingMaster.Validate().Any(e => e.Contains("PBR master")));

            var graph = YamaEngine.Materials.MaterialGraph.Create("CycleGraph");
            var a = graph.AddNode(YamaEngine.Materials.MaterialNodeType.Add, "A");
            var b = graph.AddNode(YamaEngine.Materials.MaterialNodeType.Multiply, "B");
            var master = graph.AddNode(YamaEngine.Materials.MaterialNodeType.PbrMaster, "PBR Master");
            graph.Connect(a.Id, "Out", b.Id, "A");
            graph.Connect(b.Id, "Out", a.Id, "A");
            graph.Connect(b.Id, "Out", master.Id, "Roughness");
            Assert.IsTrue(graph.Validate().Any(e => e.Contains("cycle")));
        }
    }
}
