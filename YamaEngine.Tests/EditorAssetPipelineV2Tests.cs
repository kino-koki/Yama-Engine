using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using YamaEngine.Assets;
using YamaEngine.Editor;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class EditorAssetPipelineV2Tests
    {
        public static void TestEditorWorkflowPreventsHierarchyCyclesAndSupportsUndo()
        {
            var workspace = new EditorWorkspace();
            var root = workspace.Hierarchy.CreateRoot("Root");
            var child = workspace.Hierarchy.CreateRoot("Child");

            workspace.Hierarchy.Parent(child.Id, root.Id);
            var errors = workspace.Hierarchy.Validate();

            AssertEqual(0, errors.Count, "层级挂接后不应产生验证错误。");
            AssertTrue(root.Children.Any(n => n.Id.Equals(child.Id)), "子节点应该挂到 Root 下。");

            var caught = false;
            try
            {
                workspace.Hierarchy.Parent(root.Id, child.Id);
            }
            catch (InvalidOperationException)
            {
                caught = true;
            }

            AssertTrue(caught, "层级系统必须阻止父子循环。");

            workspace.Undo.Execute(workspace, new RenameNodeCommand(child.Id, "RenamedChild"));
            AssertEqual("RenamedChild", child.Name, "命令执行后节点名应该更新。");

            AssertTrue(workspace.Undo.Undo(workspace), "撤销栈应该能撤销一次重命名。");
            AssertEqual("Child", child.Name, "撤销后节点名应该恢复。");

            AssertTrue(workspace.Undo.Redo(workspace), "重做栈应该能重做一次重命名。");
            AssertEqual("RenamedChild", child.Name, "重做后节点名应该再次更新。");
        }

        public static void TestInspectorSessionRejectsWrongValueTypes()
        {
            var inspector = new InspectorSession();
            inspector.Bind("Transform/Visible", typeof(bool), true);
            inspector.Bind("Transform/Name", typeof(string), "Camera");

            var okErrors = inspector.Apply(new Dictionary<string, object?>
            {
                ["Transform/Visible"] = false,
                ["Transform/Name"] = "Main Camera"
            });

            AssertEqual(0, okErrors.Count, "合法 Inspector patch 不应产生错误。");
            AssertEqual(false, inspector.Properties["Transform/Visible"].Value, "布尔属性应该被更新。");
            AssertEqual("Main Camera", inspector.Properties["Transform/Name"].Value, "字符串属性应该被更新。");

            var badErrors = inspector.Apply(new Dictionary<string, object?>
            {
                ["Transform/Visible"] = "not a bool",
                ["Transform/Missing"] = 42
            });

            AssertEqual(2, badErrors.Count, "错误类型和未知属性都应该被报告。");
            AssertTrue(badErrors.Any(e => e.Contains("需要 Boolean")), "错误类型应包含期望类型。");
            AssertTrue(badErrors.Any(e => e.Contains("没有绑定属性")), "未知属性应被报告。");
        }

        public static void TestAssetImportPipelineImportsChangedTextAndBuildsCatalog(string tempRoot)
        {
            var sourceRoot = Path.Combine(tempRoot, "AssetPipelineV2Sources");
            var artifactRoot = Path.Combine(tempRoot, "AssetPipelineV2Artifacts");
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(artifactRoot);

            var source = Path.Combine(sourceRoot, "dialogue.txt");
            File.WriteAllText(source, "yama会把换行统一掉\r\n第二行\r\n");

            var pipeline = AssetImportPipelineV2.CreateDefault();
            var first = pipeline.ImportChanged(new[] { source }, artifactRoot);

            AssertEqual(1, first.Count, "首次导入应该生成一个 artifact。");
            AssertTrue(File.Exists(first[0].ArtifactPath), "Artifact 文件应该落盘。");
            AssertEqual(1, pipeline.Dependencies.GetDependencies(source).Count, "依赖图应该记录源文件依赖。");

            var second = pipeline.ImportChanged(new[] { source }, artifactRoot);
            AssertEqual(0, second.Count, "源文件未变化时不应重复导入。");

            File.AppendAllText(source, "第三行\n");
            var third = pipeline.ImportChanged(new[] { source }, artifactRoot);
            AssertEqual(1, third.Count, "源文件变化后应该重新导入。");
            AssertTrue(third[0].ContentHash != first[0].ContentHash, "内容变化后 hash 应该变化。");

            var catalog = new ContentCatalogBuilderV2();
            catalog.Add("dialogue/main", third[0]);
            var errors = catalog.Validate();
            AssertEqual(0, errors.Count, "有效内容目录不应报错。");
            AssertTrue(catalog.ToJsonLike().Contains("dialogue/main"), "内容目录导出应包含地址。");
        }
    }
}
