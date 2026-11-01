using System;
using System.IO;
using System.Numerics;
using System.Linq;
using System.Collections.Generic;
using YamaEngine.Assets;
using YamaEngine.Scenes;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class SceneTests
    {
        public static void TestSceneSerializerSaveLoadRoundtrip(string tempRoot)
        {
            var projectPath = Path.Combine(tempRoot, "SceneProject");
            Directory.CreateDirectory(projectPath);
            var db = new AssetDatabase(projectPath);

            var camera = db.CreateObject("Main Camera", "Camera", new Vector3(0, 2, -5));
            var cameraComponent = camera.AddComponent<CameraComponent>();
            cameraComponent.IsMain = true;
            cameraComponent.FieldOfView = 70f;

            var light = db.CreateObject("Sun", "DirectionalLight", new Vector3(0, 4, 0));
            var lightComponent = light.AddComponent<LightComponent>();
            lightComponent.LightType = "Directional";
            lightComponent.Intensity = 2.5f;

            var scenePath = Path.Combine(projectPath, "Content", "Scenes", "Main.yama");
            var saved = SceneSerializer.Save(scenePath, db.SceneObjects, "Main");
            var loaded = SceneSerializer.Load(scenePath);
            var materialized = SceneSerializer.Materialize(loaded);

            AssertEqual(SceneSerializer.CurrentFormatVersion, saved.FormatVersion, "Scene format version should be written.");
            AssertEqual("Main", loaded.Name, "Scene name should roundtrip.");
            AssertEqual(2, materialized.Count, "Scene objects should roundtrip.");
            AssertTrue(materialized.Any(o => o.Name == "Main Camera" && o.GetComponent<CameraComponent>()?.IsMain == true), "Camera component should roundtrip.");
            AssertTrue(materialized.Any(o => o.Name == "Sun" && Math.Abs((o.GetComponent<LightComponent>()?.Intensity ?? 0) - 2.5f) < 0.001f), "Light component should roundtrip.");
        }
    }
}
