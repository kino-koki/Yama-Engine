using ShanyuEngine.Testing;

namespace ShanyuEngine.Tests
{
    /// <summary>
    /// 场景测试：SceneSerializer JSON 存读、版本控制、Prefab
    /// </summary>
    [TestClass]
    public class SceneTests : EngineTestFixture
    {
        [TestMethod]
        public void SceneSerializerSaveLoadRoundtrip()
        {
            var projectPath = Path.Combine(TempDirectory, "SceneProject");
            Directory.CreateDirectory(projectPath);
            var db = new ShanyuEngine.Assets.AssetDatabase(projectPath);

            var camera = db.CreateObject("Main Camera", "Camera", new System.Numerics.Vector3(0, 2, -5));
            var cameraComponent = camera.AddComponent<ShanyuEngine.Scenes.CameraComponent>();
            cameraComponent.IsMain = true;
            cameraComponent.FieldOfView = 70f;

            var light = db.CreateObject("Sun", "DirectionalLight", new System.Numerics.Vector3(0, 4, 0));
            var lightComponent = light.AddComponent<ShanyuEngine.Scenes.LightComponent>();
            lightComponent.LightType = "Directional";
            lightComponent.Intensity = 2.5f;

            var scenePath = Path.Combine(projectPath, "Content", "Scenes", "Main.shanyu");
            var saved = ShanyuEngine.Scenes.SceneSerializer.Save(scenePath, db.SceneObjects, "Main");
            var loaded = ShanyuEngine.Scenes.SceneSerializer.Load(scenePath);
            var materialized = ShanyuEngine.Scenes.SceneSerializer.Materialize(loaded);

            Assert.AreEqual(ShanyuEngine.Scenes.SceneSerializer.CurrentFormatVersion, saved.FormatVersion);
            Assert.AreEqual("Main", loaded.Name);
            Assert.AreEqual(2, materialized.Count);
            Assert.IsTrue(materialized.Any(o => o.Name == "Main Camera" && o.GetComponent<ShanyuEngine.Scenes.CameraComponent>()?.IsMain == true));
            Assert.IsTrue(materialized.Any(o => o.Name == "Sun" && Math.Abs((o.GetComponent<ShanyuEngine.Scenes.LightComponent>()?.Intensity ?? 0) - 2.5f) < 0.001f));
        }

        [TestMethod]
        public void PrefabSaveAndInstantiateRoundtrip()
        {
            var projectPath = Path.Combine(TempDirectory, "PrefabProject");
            Directory.CreateDirectory(projectPath);
            var db = new ShanyuEngine.Assets.AssetDatabase(projectPath);
            var prefabSystem = new ShanyuEngine.Prefabs.PrefabSystem(projectPath, db);

            var source = db.CreateObject("Enemy", "Box", new System.Numerics.Vector3(2, 1, 3));
            source.Scale = new System.Numerics.Vector3(1.5f, 2f, 1.5f);
            source.BaseColor = new System.Numerics.Vector3(1f, 0.2f, 0.1f);
            source.Metallic = 0.25f;
            source.Roughness = 0.4f;

            var prefab = prefabSystem.SavePrefab(source, "Assets/Prefabs/Enemy.shprefab");

            Assert.IsTrue(File.Exists(Path.Combine(projectPath, "Assets", "Prefabs", "Enemy.shprefab")));
            Assert.IsTrue(File.Exists(Path.Combine(projectPath, "Assets", "Prefabs", "Enemy.shprefab.shmeta")));
            Assert.IsFalse(string.IsNullOrWhiteSpace(prefab.Guid));

            var instance = prefabSystem.InstantiatePrefab(prefab.Guid, new System.Numerics.Vector3(5, 0, 0));

            Assert.AreEqual("Enemy", instance.Name);
            Assert.AreEqual(new System.Numerics.Vector3(5, 0, 0), instance.Position);
            Assert.AreEqual(source.Scale, instance.Scale);
            Assert.AreEqual(source.BaseColor, instance.BaseColor);
            Assert.AreEqual(source.MeshAssetPath, instance.MeshAssetPath);
        }

        [TestMethod]
        public void ProjectSystemCreatesBasic3DProject()
        {
            var templateRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Templates"));
            var projectPath = Path.Combine(TempDirectory, "MyGame");

            var project = ShanyuEngine.Projects.ProjectSystem.CreateProject(projectPath, "MyGame", Path.Combine(templateRoot, "Basic3D"));

            Assert.AreEqual("MyGame", project.Name);
            Assert.IsTrue(File.Exists(Path.Combine(projectPath, "shanyu.project.json")));
            Assert.IsTrue(Directory.Exists(Path.Combine(projectPath, "Assets")));

            var loaded = ShanyuEngine.Projects.ProjectSystem.LoadProject(projectPath);
            Assert.AreEqual("Content/Scenes/Main.shanyu", loaded.StartupScene);
        }

        [TestMethod]
        public void SceneJsonFormatVersion()
        {
            var projectPath = Path.Combine(TempDirectory, "VersionProject");
            Directory.CreateDirectory(projectPath);
            var db = new ShanyuEngine.Assets.AssetDatabase(projectPath);
            db.CreateObject("VersionObj", "Box", System.Numerics.Vector3.Zero);

            var scenePath = Path.Combine(projectPath, "Content", "Scenes", "VersionScene.shanyu");
            var saved = ShanyuEngine.Scenes.SceneSerializer.Save(scenePath, db.SceneObjects, "VersionScene");
            var loaded = ShanyuEngine.Scenes.SceneSerializer.Load(scenePath);

            Assert.IsTrue(saved.FormatVersion > 0, "Scene format version should be positive.");
            Assert.AreEqual("VersionScene", loaded.Name);
        }

        [TestMethod]
        public void EmptySceneRoundtrip()
        {
            var projectPath = Path.Combine(TempDirectory, "EmptySceneProject");
            Directory.CreateDirectory(projectPath);

            var scenePath = Path.Combine(projectPath, "Content", "Scenes", "Empty.shanyu");
            var saved = ShanyuEngine.Scenes.SceneSerializer.Save(scenePath, new List<ShanyuEngine.Scenes.SceneObject>(), "Empty");
            var loaded = ShanyuEngine.Scenes.SceneSerializer.Load(scenePath);
            var materialized = ShanyuEngine.Scenes.SceneSerializer.Materialize(loaded);

            Assert.AreEqual("Empty", loaded.Name);
            Assert.AreEqual(0, materialized.Count);
        }
    }
}
