using System.Text.Json;
using ShanyuEngine.Animation;
using ShanyuEngine.Assets;
using ShanyuEngine.Build;
using ShanyuEngine.CodeEditing;
using ShanyuEngine.DCC;
using ShanyuEngine.Diagnostics;
using ShanyuEngine.Editor;
using ShanyuEngine.Gameplay;
using ShanyuEngine.Input;
using ShanyuEngine.Materials;
using ShanyuEngine.Modeling;
using ShanyuEngine.Prefabs;
using ShanyuEngine.Projects;
using ShanyuEngine.Rendering;
using ShanyuEngine.Runtime;
using ShanyuEngine.Scenes;
using ShanyuEngine.Skinning;

var tempRoot = Path.Combine(Path.GetTempPath(), "ShanyuEngineTests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempRoot);

try
{
    ProjectSystemTests.TestProjectSystemCreatesBasic3DProject(tempRoot);
    ProjectSystemTests.TestAssetImportCreatesStableMeta(tempRoot);
    SceneTests.TestSceneSerializerSaveLoadRoundtrip(tempRoot);
    PrefabTests.TestPrefabSaveAndInstantiateRoundtrip(tempRoot);
    MeshTests.TestEditableMeshCubeSaveLoadRoundtrip(tempRoot);
    MeshTests.TestMeshEditExtrudeFaceCreatesCapAndSideWalls();
    MeshTests.TestMeshEditInsetFaceShrinksRingTowardsCentroid();
    MeshTests.TestMeshEditBevelEdgeChamfersTriangle();
    MeshTests.TestMeshEditLoopCutSplitsSharedEdge();
    MeshTests.TestUvUnwrapperPlanarBoxAndSphericalProjection();
    MaterialTests.TestPbrMaterialSaveLoadAndMeta(tempRoot);
    MaterialTests.TestMaterialGraphEvaluatesPbrPreviewAndRoundtrips(tempRoot);
    MaterialTests.TestMaterialGraphDetectsCyclesAndMissingMaster();
    SculptTests.TestSculptInflateBrushMovesVerticesAlongNormals();
    SculptTests.TestSculptSmoothBrushReducesSurfaceNoise();
    SculptTests.TestSculptGrabBrushMovesVerticesWithFalloffAndMask();
    AnimationTests.TestSkeletonCreateBipedHierarchy();
    AnimationTests.TestSkeletonRoundtripWithMeta(tempRoot);
    AnimationTests.TestSkinBindingAutoWeightsAreNormalized();
    AnimationTests.TestCpuSkinningDeformsMeshWithPose();
    AnimationTests.TestAnimationClipSamplesBoneKeyframes();
    AnimationTests.TestAnimationClipLoopsAndRoundtrips(tempRoot);
    AnimationTests.TestAnimatorCrossFadesBetweenStates();
    BuildTests.TestBuildPipelineExportsPlayableDemoLayout(tempRoot);
    BuildTests.TestBuildPipelineWritesBuildReport(tempRoot);
    DiagnosticsTests.TestEngineDiagnosticsReportsHealthyProject(tempRoot);
    DiagnosticsTests.TestEngineDiagnosticsFindsMissingProjectFilesAndSceneReferences(tempRoot);
    AssetDatabaseTests.TestAssetDependencyGraphTracksSceneAndPrefabReferences(tempRoot);
    AssetDatabaseTests.TestAssetDependencyGraphReportsMissingReferences(tempRoot);
    RenderTests.TestRenderSceneBuilderCreatesStableSnapshot(tempRoot);
    RenderTests.TestNullRenderDeviceReturnsFrameStats(tempRoot);
    RenderTests.TestRenderGraphCompilesPassDependencies();
    RenderTests.TestRenderGraphDetectsCyclicResourceDependencies();
    RenderTests.TestRenderGraphReportsResourceLifetimesAndStats();
    RenderTests.TestNullRenderDeviceExecutesCompiledRenderGraph();
    DccTests.TestDccConnectorCatalogKeepsExternalToolsOutOfCore();
    RenderTests.TestHzbOcclusionBufferRejectsHiddenBounds();
    RenderTests.TestGpuDrivenDrawListBuildsVisibleCommands();
    RenderTests.TestSparseShadowAtlasSchedulerAllocatesPagesByLightPriority();
    RenderTests.TestAdvancedVisibilityWritesFrameStats();
    RenderTests.TestVirtualGeometryBuilderCreatesMeshClusters();
    RenderTests.TestVirtualGeometryLodSelectorChoosesLowerDetailAtDistance();
    RenderTests.TestVirtualGeometryDrawListUsesHzbOcclusion();
    EditorTests.TestEditorCommandStackUndoRedoCreateAndDelete(tempRoot);
    EditorTests.TestEditorCommandStackUndoRedoMoveAndPropertyEdit(tempRoot);
    RuntimeTests.TestRuntimeSceneLoaderLoadsExportedDemoPackage(tempRoot);
    RuntimeTests.TestRuntimeSceneLoaderReportsMissingPackageFiles(tempRoot);
    RuntimeTests.TestRuntimeGameLoopTicksComponentsAndRendersFrame();
    RuntimeTests.TestRuntimeGameLoopRunsFixedStepsWithAccumulator();
    RuntimeTests.TestRuntimeGameLoopSamplesInputBeforeComponents();
    RuntimeTests.TestRuntimeGameLoopReportsInputFrameStats();
    RuntimeTests.TestRuntimeLifecycleBridgeCreatesAndSyncsEcsEntity();
    RuntimeTests.TestRuntimeCoroutineAndStateMachineAdvanceDeterministically();
    RuntimeTests.TestRuntimeSaveGameStoreRoundtripsVariablesAndObjectTransforms();
    RuntimeInfrastructureTests.TestAdvancedUILayoutSolvesFlexibleRowsAndColumns();
    RuntimeInfrastructureTests.TestStyleCascadeResolvesVariablesStatesAndSpecificity();
    RuntimeInfrastructureTests.TestEventRouterCapturesTargetsBubblesAndStopsPropagation();
    RuntimeInfrastructureTests.TestAnimationTimelineSamplesEasingAndLoops();
    RuntimeInfrastructureTests.TestProfilerAggregatesSamplesAndExportsPercentiles();
    RuntimeInfrastructureTests.TestStructuredLoggerRedactsAndRoutesEntries();
    RuntimeInfrastructureTests.TestCrashReporterBuildsDeterministicBundle();
    RuntimeInfrastructureTests.TestAutomationRunnerFiltersRetriesAndReports();
    RuntimeInfrastructureTests.TestBenchmarkRunnerWarmsUpMeasuresAndComparesBaseline();
    RuntimeInfrastructureTests.TestBuildValidationAndCodeHealthDetectActionableIssues();
    EditorAssetPipelineV2Tests.TestEditorWorkflowPreventsHierarchyCyclesAndSupportsUndo();
    EditorAssetPipelineV2Tests.TestInspectorSessionRejectsWrongValueTypes();
    EditorAssetPipelineV2Tests.TestAssetImportPipelineImportsChangedTextAndBuildsCatalog(tempRoot);
    EditorTests.TestCodeWorkspaceGeneratorCreatesVsCodeCompatibleProject(tempRoot);
    EditorTests.TestInEngineCodeEditorEditsSavesAndReportsDiagnostics(tempRoot);
    EditorTests.TestExternalCodeEditorBridgeKeepsVsCodeOutOfRuntimeCore(tempRoot);
    GameplayTests.TestGameplayActorLifecycleAndComponentTick();
    GameplayTests.TestPlayerControllerPossessesPawnAndDrivesMovement();
    GameplayTests.TestGameplayWorldSpawnsDefaultPlayerAndScoresMatch();
    GameplayTests.TestGameplayWorldDestroysActorAndStopsTicking();
    Console.WriteLine("All ShanyuEngine tests passed.");
}

finally
{
    try { Directory.Delete(tempRoot, true); } catch { }
}
