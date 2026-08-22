using ShanyuEngine.Testing;

namespace ShanyuEngine.Tests
{
    /// <summary>
    /// 编辑器测试：CommandStack 撤销/重做
    /// </summary>
    [TestClass]
    public class EditorTests : EngineTestFixture
    {
        [TestMethod]
        public void EditorCommandStackUndoRedoCreateAndDelete()
        {
            var projectPath = Path.Combine(TempDirectory, "EditorCommandProject");
            Directory.CreateDirectory(projectPath);

            var db = new ShanyuEngine.Assets.AssetDatabase(projectPath);
            var stack = new ShanyuEngine.Editor.EditorCommandStack();

            var create = new ShanyuEngine.Editor.CreateObjectCommand(db, "UndoCube", "Assets/Meshes/Cube.obj", new System.Numerics.Vector3(1, 2, 3));
            stack.Execute(create);

            Assert.AreEqual(1, db.ObjectCount);
            Assert.AreEqual("Create Object 'UndoCube'", stack.UndoDescription);
            Assert.IsTrue(stack.CanUndo);
            Assert.IsFalse(stack.CanRedo);

            stack.Undo();
            Assert.AreEqual(0, db.ObjectCount);
            Assert.IsFalse(stack.CanUndo);
            Assert.IsTrue(stack.CanRedo);

            stack.Redo();
            Assert.AreEqual(1, db.ObjectCount);
            var restored = db.FindObjectByName("UndoCube");
            Assert.IsNotNull(restored);
            Assert.AreEqual(new System.Numerics.Vector3(1, 2, 3), restored!.Position);

            var delete = new ShanyuEngine.Editor.DeleteObjectCommand(db, restored);
            stack.Execute(delete);
            Assert.AreEqual(0, db.ObjectCount);

            stack.Undo();
            Assert.AreEqual(1, db.ObjectCount);

            stack.Redo();
            Assert.AreEqual(0, db.ObjectCount);
        }

        [TestMethod]
        public void EditorCommandStackUndoRedoMoveAndPropertyEdit()
        {
            var projectPath = Path.Combine(TempDirectory, "EditorTransformCommandProject");
            Directory.CreateDirectory(projectPath);

            var db = new ShanyuEngine.Assets.AssetDatabase(projectPath);
            var obj = db.CreateObject("EditableCube", "Assets/Meshes/Cube.obj", System.Numerics.Vector3.Zero);
            obj.BaseColor = new System.Numerics.Vector3(1, 1, 1);

            var stack = new ShanyuEngine.Editor.EditorCommandStack();
            stack.Execute(new ShanyuEngine.Editor.MoveObjectCommand(obj, new System.Numerics.Vector3(4, 5, 6)));
            Assert.AreEqual(new System.Numerics.Vector3(4, 5, 6), obj.Position);

            stack.Undo();
            Assert.AreEqual(System.Numerics.Vector3.Zero, obj.Position);

            stack.Redo();
            Assert.AreEqual(new System.Numerics.Vector3(4, 5, 6), obj.Position);

            stack.Execute(new ShanyuEngine.Editor.SetObjectPropertyCommand<System.Numerics.Vector3>(
                obj,
                "BaseColor",
                o => o.BaseColor,
                (o, value) => o.BaseColor = value,
                new System.Numerics.Vector3(0.2f, 0.4f, 0.8f)));

            Assert.AreEqual(new System.Numerics.Vector3(0.2f, 0.4f, 0.8f), obj.BaseColor);

            stack.Undo();
            Assert.AreEqual(new System.Numerics.Vector3(1, 1, 1), obj.BaseColor);

            stack.Redo();
            Assert.AreEqual(new System.Numerics.Vector3(0.2f, 0.4f, 0.8f), obj.BaseColor);
        }
    }
}
