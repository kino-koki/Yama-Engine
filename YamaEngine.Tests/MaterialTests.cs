using System;
using System.IO;
using System.Numerics;
using System.Linq;
using System.Collections.Generic;
using YamaEngine.Assets;
using YamaEngine.Materials;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class MaterialTests
    {
        public static void TestPbrMaterialSaveLoadAndMeta(string tempRoot)
        {
            var projectPath = Path.Combine(tempRoot, "MaterialProject");
            var materialPath = Path.Combine(projectPath, "Assets", "Materials", "BrushedSteel.shmat");

            var material = PbrMaterial.Create("BrushedSteel");
            material.BaseColor = new Vector3(0.75f, 0.72f, 0.68f);
            material.Metallic = 1f;
            material.Roughness = 0.28f;
            material.NormalMap = "Assets/Textures/brushed_normal.png";
            material.OcclusionMap = "Assets/Textures/brushed_ao.png";
            material.EmissiveColor = new Vector3(0.02f, 0.02f, 0.02f);

            material.Write(materialPath);
            var loaded = PbrMaterial.Read(materialPath);

            AssertEqual("BrushedSteel", loaded.Name, "PBR material name should roundtrip.");
            AssertEqual(1f, loaded.Metallic, "PBR metallic should roundtrip.");
            AssertEqual(0.28f, loaded.Roughness, "PBR roughness should roundtrip.");
            AssertEqual("Assets/Textures/brushed_normal.png", loaded.NormalMap, "PBR normal map should roundtrip.");
            AssertEqual(0, loaded.Validate().Count, "Loaded PBR material should be valid.");

            var meta = AssetMeta.Read(materialPath + ".shmeta");
            AssertEqual(AssetType.Material.ToString(), meta.Type, "PBR material should register as Material asset.");
            AssertEqual("Assets/Materials/BrushedSteel.shmat", meta.AssetPath, "Material meta should store project relative path.");
        }

        public static void TestMaterialGraphEvaluatesPbrPreviewAndRoundtrips(string tempRoot)
        {
            var projectPath = Path.Combine(tempRoot, "GraphProject");
            var graphPath = Path.Combine(projectPath, "Assets", "Materials", "CopperGraph.shgraph");

            var graph = MaterialGraph.Create("CopperGraph");
            var color = graph.AddNode(MaterialNodeType.ConstantColor, "CopperColor");
            color.VectorValue = new Vector4(0.95f, 0.45f, 0.22f, 1f);
            var metallic = graph.AddNode(MaterialNodeType.ConstantFloat, "Metallic");
            metallic.FloatValue = 1f;
            var roughness = graph.AddNode(MaterialNodeType.ConstantFloat, "Roughness");
            roughness.FloatValue = 0.32f;
            var master = graph.AddNode(MaterialNodeType.PbrMaster, "PBR Master");
            graph.Connect(color.Id, "Color", master.Id, "BaseColor");
            graph.Connect(metallic.Id, "Value", master.Id, "Metallic");
            graph.Connect(roughness.Id, "Value", master.Id, "Roughness");

            AssertEqual(0, graph.Validate().Count, "Material graph should validate before save.");
            var preview = graph.EvaluatePreview();
            AssertEqual(1f, preview.Metallic, "Material graph should evaluate metallic input.");
            AssertEqual(0.32f, preview.Roughness, "Material graph should evaluate roughness input.");
            AssertTrue(preview.BaseColor.X > 0.9f && preview.BaseColor.Z > 0.2f, "Material graph should evaluate base color input.");

            graph.Write(graphPath);
            var loaded = MaterialGraph.Read(graphPath);
            AssertEqual("CopperGraph", loaded.Name, "Material graph name should roundtrip.");
            AssertEqual(4, loaded.Nodes.Count, "Material graph nodes should roundtrip.");
            AssertEqual(3, loaded.Edges.Count, "Material graph edges should roundtrip.");
            AssertEqual(0, loaded.Validate().Count, "Loaded material graph should validate.");

            var meta = AssetMeta.Read(graphPath + ".shmeta");
            AssertEqual(AssetType.Shader.ToString(), meta.Type, "Material graph should register as Shader asset.");
            AssertEqual("Assets/Materials/CopperGraph.shgraph", meta.AssetPath, "Material graph meta should store project relative path.");
        }

        public static void TestMaterialGraphDetectsCyclesAndMissingMaster()
        {
            var missingMaster = MaterialGraph.Create("MissingMaster");
            missingMaster.AddNode(MaterialNodeType.ConstantFloat, "OnlyValue");
            AssertTrue(missingMaster.Validate().Any(e => e.Contains("PBR master")), "Material graph should require a PBR master node.");

            var graph = MaterialGraph.Create("CycleGraph");
            var a = graph.AddNode(MaterialNodeType.Add, "A");
            var b = graph.AddNode(MaterialNodeType.Multiply, "B");
            var master = graph.AddNode(MaterialNodeType.PbrMaster, "PBR Master");
            graph.Connect(a.Id, "Out", b.Id, "A");
            graph.Connect(b.Id, "Out", a.Id, "A");
            graph.Connect(b.Id, "Out", master.Id, "Roughness");
            AssertTrue(graph.Validate().Any(e => e.Contains("cycle")), "Material graph should detect node cycles.");
        }
    }
}
