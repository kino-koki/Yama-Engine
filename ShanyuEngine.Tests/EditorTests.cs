using System;
using System.IO;
using System.Numerics;
using System.Linq;
using System.Collections.Generic;
using ShanyuEngine.Assets;
using ShanyuEngine.CodeEditing;
using ShanyuEngine.Editor;
using static ShanyuEngine.Tests.TestHelpers;

namespace ShanyuEngine.Tests
{
    public static class EditorTests
    {
        public static void TestEditorCommandStackUndoRedoCreateAndDelete(string tempRoot)
        {
            var projectPath = Path.Combine(tempRoot, "EditorCommandProject");
            Directory.CreateDirectory(projectPath);

            var db = new AssetDatabase(projectPath);
            var stack = new EditorCommandStack();

            var create = new CreateObjectCommand(db, "UndoCube", "Assets/Meshes/Cube.obj", new Vector3(1, 2, 3));
            stack.Execute(create);

            AssertEqual(1, db.ObjectCount, "Create command should add one object.");
            AssertEqual("Create Object 'UndoCube'", stack.UndoDescription, "Undo description should expose last command.");
            AssertTrue(stack.CanUndo, "Stack should allow undo after executing create.");
            AssertTrue(!stack.CanRedo, "Redo stack should be cleared after executing create.");

            stack.Undo();
            AssertEqual(0, db.ObjectCount, "Undo create should remove created object.");
            AssertTrue(!stack.CanUndo, "Stack should have no undo commands after undoing create.");
            AssertTrue(stack.CanRedo, "Stack should allow redo after undo.");

            stack.Redo();
            AssertEqual(1, db.ObjectCount, "Redo create should restore created object.");
            var restored = db.FindObjectByName("UndoCube");
            AssertTrue(restored != null, "Redo create should restore object by name.");
            AssertEqual(new Vector3(1, 2, 3), restored!.Position, "Redo create should keep original transform.");

            var delete = new DeleteObjectCommand(db, restored);
            stack.Execute(delete);
            AssertEqual(0, db.ObjectCount, "Delete command should remove object.");

            stack.Undo();
            AssertEqual(1, db.ObjectCount, "Undo delete should restore object.");
            AssertTrue(db.SceneObjects.Any(o => o.Id == restored.Id), "Undo delete should preserve original object identity.");

            stack.Redo();
            AssertEqual(0, db.ObjectCount, "Redo delete should remove object again.");
        }

        public static void TestEditorCommandStackUndoRedoMoveAndPropertyEdit(string tempRoot)
        {
            var projectPath = Path.Combine(tempRoot, "EditorTransformCommandProject");
            Directory.CreateDirectory(projectPath);

            var db = new AssetDatabase(projectPath);
            var obj = db.CreateObject("EditableCube", "Assets/Meshes/Cube.obj", Vector3.Zero);
            obj.BaseColor = new Vector3(1, 1, 1);

            var stack = new EditorCommandStack();
            stack.Execute(new MoveObjectCommand(obj, new Vector3(4, 5, 6)));
            AssertEqual(new Vector3(4, 5, 6), obj.Position, "Move command should update object position.");

            stack.Undo();
            AssertEqual(Vector3.Zero, obj.Position, "Undo move should restore previous position.");

            stack.Redo();
            AssertEqual(new Vector3(4, 5, 6), obj.Position, "Redo move should reapply target position.");

            stack.Execute(new SetObjectPropertyCommand<Vector3>(
                obj,
                "BaseColor",
                o => o.BaseColor,
                (o, value) => o.BaseColor = value,
                new Vector3(0.2f, 0.4f, 0.8f)));

            AssertEqual(new Vector3(0.2f, 0.4f, 0.8f), obj.BaseColor, "Property command should update BaseColor.");
            AssertTrue(!stack.CanRedo, "Executing a new command after redo should clear redo history.");

            stack.Undo();
            AssertEqual(new Vector3(1, 1, 1), obj.BaseColor, "Undo property command should restore previous BaseColor.");

            stack.Redo();
            AssertEqual(new Vector3(0.2f, 0.4f, 0.8f), obj.BaseColor, "Redo property command should reapply BaseColor.");
        }

        public static void TestCodeWorkspaceGeneratorCreatesVsCodeCompatibleProject(string tempRoot)
        {
            var projectPath = Path.Combine(tempRoot, "CodeProject");
            Directory.CreateDirectory(projectPath);

            var result = CodeWorkspaceGenerator.Generate(new CodeWorkspaceOptions
            {
                ProjectRoot = projectPath,
                ProjectName = "CodeProject",
                EngineAssemblyName = "ShanyuEngine",
                GenerateVsCodeCompatibility = true
            });

            AssertTrue(File.Exists(result.WorkspaceFile), "Code workspace generator should write a .code-workspace file.");
            AssertTrue(File.Exists(result.ScriptProjectFile), "Code workspace generator should write a C# script project.");
            AssertTrue(File.Exists(result.TasksFile), "Code workspace generator should write VS Code compatible tasks.");
            AssertTrue(File.Exists(result.LaunchFile), "Code workspace generator should write VS Code compatible launch config.");
            AssertTrue(File.Exists(Path.Combine(result.ScriptsDirectory, "ShanyuScript.cs")), "Code workspace should include base script API.");
            AssertTrue(File.ReadAllText(result.TasksFile).Contains("dotnet build", StringComparison.OrdinalIgnoreCase), "VS Code compatible tasks should expose dotnet build.");
            AssertEqual(CodeEditorBoundary.ExternalToolCompatible, result.EditorBoundary, "VS Code compatibility must stay outside the engine runtime core.");
        }

        public static void TestInEngineCodeEditorEditsSavesAndReportsDiagnostics(string tempRoot)
        {
            var projectPath = Path.Combine(tempRoot, "InEngineCodeProject");
            Directory.CreateDirectory(projectPath);
            var workspace = CodeWorkspaceGenerator.Generate(new CodeWorkspaceOptions
            {
                ProjectRoot = projectPath,
                ProjectName = "InEngineCodeProject",
                EngineAssemblyName = "ShanyuEngine"
            });

            var editor = InEngineCodeEditor.Open(workspace.ScriptsDirectory);
            var document = editor.CreateScript("PlayerController", ScriptTemplate.ActorComponent);
            document.Replace("public override void OnUpdate(float deltaTime)\n    {\n    }", "public override void OnUpdate(float deltaTime)\n    {\n        Log(\"tick\");\n    }");
            document.Save();

            AssertTrue(File.Exists(document.FilePath), "In-engine code editor should save script files.");
            AssertTrue(File.ReadAllText(document.FilePath).Contains("Log(\"tick\")"), "Saved script should include in-engine edit.");

            var diagnostics = CodeDiagnostics.Analyze(document);
            AssertEqual(0, diagnostics.Count, "Valid script should not report diagnostics.");

            document.ApplyEdit(document.Text.Length, 0, "\n}");
            var brokenDiagnostics = CodeDiagnostics.Analyze(document);
            AssertTrue(brokenDiagnostics.Any(d => d.Severity == CodeDiagnosticSeverity.Error), "Code diagnostics should catch malformed braces before build.");
        }

        public static void TestExternalCodeEditorBridgeKeepsVsCodeOutOfRuntimeCore(string tempRoot)
        {
            var workspaceFile = Path.Combine(tempRoot, "BridgeProject", "BridgeProject.code-workspace");
            Directory.CreateDirectory(Path.GetDirectoryName(workspaceFile)!);
            File.WriteAllText(workspaceFile, "{}");

            var catalog = ExternalCodeEditorCatalog.CreateDefault();
            var plan = catalog.CreateOpenPlan(CodeEditorKind.CodeOSS, workspaceFile);

            AssertTrue(catalog.Editors.Any(e => e.Kind == CodeEditorKind.VSCode), "External editor catalog should include VS Code compatibility.");
            AssertTrue(catalog.Editors.Any(e => e.Kind == CodeEditorKind.CodeOSS), "External editor catalog should include Code-OSS compatibility.");
            AssertTrue(catalog.Editors.Any(e => e.Kind == CodeEditorKind.VSCodium), "External editor catalog should include VSCodium compatibility.");
            AssertEqual(CodeEditorBoundary.ExternalToolCompatible, plan.Boundary, "External editor bridge must not become runtime core.");
            AssertTrue(plan.ExecutableCandidates.Any(c => c.Contains("code", StringComparison.OrdinalIgnoreCase)), "Open plan should know Code-compatible executable candidates.");
            AssertTrue(plan.Arguments.Contains(workspaceFile), "Open plan should pass workspace file to external editor.");
            AssertTrue(plan.CorePolicy.RequiresSelfDevelopedRuntimeCore, "Core policy should keep gameplay/runtime/compiler integration self-developed.");
        }
    }
}
