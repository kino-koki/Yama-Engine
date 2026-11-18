using System.Text.Json;
using YamaEngine.Animation;
using YamaEngine.Assets;
using YamaEngine.Build;
using YamaEngine.Cel2D;
using YamaEngine.CodeEditing;
using YamaEngine.Core;
using YamaEngine.DCC;
using YamaEngine.Diagnostics;
using YamaEngine.Editor;
using YamaEngine.Gameplay;
using YamaEngine.GameFramework;
using YamaEngine.Input;
using YamaEngine.Materials;
using YamaEngine.Modeling;
using YamaEngine.Prefabs;
using YamaEngine.Projects;
using YamaEngine.Rendering;
using YamaEngine.Runtime;
using YamaEngine.Scenes;
using YamaEngine.Skinning;
using YamaEngine.Tests;
using YamaEngine.VisualScript;

var tempRoot = Path.Combine(Path.GetTempPath(), "YamaEngineTests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempRoot);

try
{
    // ── AI NPC 正式接入：Kimodo 动作生成 + MOSS-Live 全双工融合（先跑，避免被历史用例阻塞） ──
    AiNpcIntegrationTests.TestConfigRoundtrip(tempRoot);
    AiNpcIntegrationTests.TestKimodoConverterProducesPlayableClip();
    AiNpcIntegrationTests.TestKimodoConverterRetargetsExistingSkeleton();
    AiNpcIntegrationTests.TestKimodoHttpClientRoundtrip();
    AiNpcIntegrationTests.TestMossLiveDuplexTransport();
    AiNpcIntegrationTests.TestAiNpcActionFusionAndBargeIn();
    AiNpcIntegrationTests.TestFootContactExtractionByFrame();
    AiNpcIntegrationTests.TestMotionPostProcessorPipelineIsStable();
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
    int animPass = 0, animTotal = 0;
    void RunAnim(string name, Action test)
    {
        animTotal++;
        try { test(); animPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunAnim("MotionMatchingSelectByVelocity", () => AnimationTests.TestMotionMatchingSelectsBestMatchByVelocity());
    RunAnim("RootMotionExtractAndVelocity", () => AnimationTests.TestRootMotionExtractionTranslationAndVelocity());
    RunAnim("AnimationCompressionCollinear", () => AnimationTests.TestAnimationCompressionRemovesRedundantCollinearKeys());
    RunAnim("LayeredBoneBlendMask", () => AnimationTests.TestLayeredBoneBlendMasksUpperBodyOnly());
    RunAnim("AnimSyncGroupPhaseLock", () => AnimationTests.TestAnimSyncGroupKeepsDifferentLengthClipsInPhase());
    RunAnim("AdditiveBlendRotation", () => AnimationTests.TestAdditiveBlendLayerAddsRotationDelta());
    RunAnim("AimOffsetWeightInterp", () => AnimationTests.TestAimOffsetAdditiveWeightInterpolation());
    Console.WriteLine($"  == AnimationDepth: {animPass}/{animTotal} passed ==");
    int resPass = 0, resTotal = 0;
    void RunResource(string name, Action test)
    {
        resTotal++;
        try { test(); resPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunResource("CacheAddGetSameInstance", () => ResourceTests.TestCacheAddGetReturnsSameInstance());
    RunResource("CacheMissReturnsNull", () => ResourceTests.TestCacheMissReturnsNull());
    RunResource("CacheEvictsLeastRecentlyUsed", () => ResourceTests.TestCacheEvictsLeastRecentlyUsed());
    RunResource("CacheReAddUpdatesExisting", () => ResourceTests.TestCacheReAddUpdatesExisting());
    RunResource("CacheRemoveDisposesResource", () => ResourceTests.TestCacheRemoveDisposesResource());
    RunResource("CacheEvictionDisposesResource", () => ResourceTests.TestCacheEvictionDisposesResource());
    RunResource("CacheClearDisposesAll", () => ResourceTests.TestCacheClearDisposesAll());
    RunResource("CacheTotalSizeBytesAccumulates", () => ResourceTests.TestCacheTotalSizeBytesAccumulates());
    RunResource("CachePathNormalization", () => ResourceTests.TestCachePathNormalization());
    RunResource("CacheUnlimitedCapacity", () => ResourceTests.TestCacheUnlimitedCapacity());
    RunResource("CachePlainObjectNoDisposeCrash", () => ResourceTests.TestCachePlainObjectNoDisposeCrash());
    Console.WriteLine($"  == ResourceDepth: {resPass}/{resTotal} passed ==");
    int canvasPass = 0, canvasTotal = 0;
    void RunCanvas(string name, Action test)
    {
        canvasTotal++;
        try { test(); canvasPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunCanvas("CanvasLayerSortsByLayerAscending", () => CanvasLayerTests.TestCanvasLayerSortsByLayerAscending());
    RunCanvas("CanvasLayerHiddenLayerSkippedInRenderOrder", () => CanvasLayerTests.TestCanvasLayerHiddenLayerSkippedInRenderOrder());
    RunCanvas("CanvasLayerGetVisibleLayers", () => CanvasLayerTests.TestCanvasLayerGetVisibleLayers());
    RunCanvas("CanvasLayerOffsetTranslation", () => CanvasLayerTests.TestCanvasLayerOffsetTranslation());
    RunCanvas("CanvasLayerScaleTransform", () => CanvasLayerTests.TestCanvasLayerScaleTransform());
    RunCanvas("CanvasLayerRotation90", () => CanvasLayerTests.TestCanvasLayerRotation90());
    RunCanvas("CanvasLayerFollowViewport", () => CanvasLayerTests.TestCanvasLayerFollowViewport());
    RunCanvas("CanvasLayerFollowViewportZoom", () => CanvasLayerTests.TestCanvasLayerFollowViewportZoom());
    RunCanvas("CanvasLayerAddRemoveChild", () => CanvasLayerTests.TestCanvasLayerAddRemoveChild());
    RunCanvas("CanvasLayerStackLookup", () => CanvasLayerTests.TestCanvasLayerStackLookup());
    RunCanvas("CanvasLayerStackRemove", () => CanvasLayerTests.TestCanvasLayerStackRemove());
    Console.WriteLine($"  == CanvasLayerDepth: {canvasPass}/{canvasTotal} passed ==");

    // ===== UILayout Depth (Unity5 uGUI RectTransform + LayoutGroups) =====
    int uilPass = 0, uilTotal = 0;
    void RunUILayout(string name, Action test)
    {
        uilTotal++;
        try { test(); uilPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunUILayout("UILayoutRectTransformDefaults", () => UILayoutDepthTests.TestRectTransformDefaults());
    RunUILayout("UILayoutSizeDeltaRoundTrip", () => UILayoutDepthTests.TestSizeDeltaRoundTrip());
    RunUILayout("UILayoutAnchoredPositionRoundTrip", () => UILayoutDepthTests.TestAnchoredPositionRoundTrip());
    RunUILayout("UILayoutCenterAnchorRect", () => UILayoutDepthTests.TestCenterAnchorRect());
    RunUILayout("UILayoutStretchAnchor", () => UILayoutDepthTests.TestStretchAnchor());
    RunUILayout("UILayoutTopLeftAnchor", () => UILayoutDepthTests.TestTopLeftAnchor());
    RunUILayout("UILayoutParentChildRect", () => UILayoutDepthTests.TestParentChildRect());
    RunUILayout("UILayoutVerticalStacksDown", () => UILayoutDepthTests.TestVerticalLayoutStacksDown());
    RunUILayout("UILayoutVerticalSpacing", () => UILayoutDepthTests.TestVerticalLayoutSpacing());
    RunUILayout("UILayoutHorizontalStacksRight", () => UILayoutDepthTests.TestHorizontalLayoutStacksRight());
    RunUILayout("UILayoutGrid2x2", () => UILayoutDepthTests.TestGridLayout2x2());
    Console.WriteLine($"  == UILayoutDepth: {uilPass}/{uilTotal} passed ==");

    // ===== UIControls Depth (Unity5 uGUI Widget interactions) =====
    int uicPass = 0, uicTotal = 0;
    void RunUIControls(string name, Action test)
    {
        uicTotal++;
        try { test(); uicPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunUIControls("UIControlsButtonUp", () => UIControlsDepthTests.TestButtonClickUpDefault());
    RunUIControls("UIControlsButtonDown", () => UIControlsDepthTests.TestButtonClickDownTrigger());
    RunUIControls("UIControlsButtonHover", () => UIControlsDepthTests.TestButtonHoverStateTransition());
    RunUIControls("UIControlsButtonDisabled", () => UIControlsDepthTests.TestButtonDisabledIgnoresClick());
    RunUIControls("UIControlsButtonNonLeft", () => UIControlsDepthTests.TestButtonNonLeftIgnored());
    RunUIControls("UIControlsSliderDrag", () => UIControlsDepthTests.TestSliderDragUpdatesValue());
    RunUIControls("UIControlsSliderStep", () => UIControlsDepthTests.TestSliderStepSize());
    RunUIControls("UIControlsSliderScroll", () => UIControlsDepthTests.TestSliderScroll());
    RunUIControls("UIControlsCheckBox", () => UIControlsDepthTests.TestCheckBoxToggle());
    RunUIControls("UIControlsCheckBox3State", () => UIControlsDepthTests.TestCheckBoxThreeState());
    RunUIControls("UIControlsComboBox", () => UIControlsDepthTests.TestComboBoxSelection());
    RunUIControls("UIControlsProgressBar", () => UIControlsDepthTests.TestProgressBarAnimation());
    RunUIControls("UIControlsTextBlock", () => UIControlsDepthTests.TestTextBlockDesiredSize());
    RunUIControls("UIControlsTextInput", () => UIControlsDepthTests.TestEditableTextBoxTextInput());
    RunUIControls("UIControlsMaxLength", () => UIControlsDepthTests.TestEditableTextBoxMaxLength());
    RunUIControls("UIControlsButtonSize", () => UIControlsDepthTests.TestButtonDesiredSize());
    RunUIControls("UIControlsImage", () => UIControlsDepthTests.TestImageTintColorAndStretch());
    Console.WriteLine($"  == UIControlsDepth: {uicPass}/{uicTotal} passed ==");

    // ===== AudioMixer Depth (Unity5 snapshots / ducking / spatializer) =====
    int amPass = 0, amTotal = 0;
    void RunAudioMixer(string name, Action test)
    {
        amTotal++;
        try { test(); amPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunAudioMixer("AudioMixerBusCascadeVolume", () => AudioMixerDepthTests.TestBusEffectiveVolumeCascade());
    RunAudioMixer("AudioMixerMasterFinalVolume", () => AudioMixerDepthTests.TestMasterBusFinalOutputVolume());
    RunAudioMixer("AudioMixerSnapshotSaveLoad", () => AudioMixerDepthTests.TestSnapshotSaveLoadInstant());
    RunAudioMixer("AudioMixerSnapshotUnknown", () => AudioMixerDepthTests.TestSnapshotLoadUnknownReturnsFalse());
    RunAudioMixer("AudioMixerSnapshotTransition", () => AudioMixerDepthTests.TestSnapshotLinearTransition());
    RunAudioMixer("AudioMixerDucking", () => AudioMixerDepthTests.TestDuckingStateMachine());
    RunAudioMixer("AudioMixerDistanceAtten", () => AudioMixerDepthTests.TestSpatializerDistanceAttenuation());
    RunAudioMixer("AudioMixerStereoPan", () => AudioMixerDepthTests.TestSpatializerStereoPan());
    RunAudioMixer("AudioMixerDoppler", () => AudioMixerDepthTests.TestSpatializerDoppler());
    RunAudioMixer("AudioMixerPropagationDelay", () => AudioMixerDepthTests.TestSpatializerPropagationDelay());
    Console.WriteLine($"  == AudioMixerDepth: {amPass}/{amTotal} passed ==");

    // ===== StandardShader Depth (Unity5 Standard Shader PBR + GPU cbuffer layout) =====
    int ssPass = 0, ssTotal = 0;
    void RunStdShader(string name, Action test)
    {
        ssTotal++;
        try { test(); ssPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunStdShader("StdShaderDefaults", () => StandardShaderDepthTests.TestStandardDefaults());
    RunStdShader("StdShaderMetallicWorkflow", () => StandardShaderDepthTests.TestMetallicWorkflow());
    RunStdShader("StdShaderSmoothnessMapping", () => StandardShaderDepthTests.TestSmoothnessToRoughnessMapping());
    RunStdShader("StdShaderRenderModeQueue", () => StandardShaderDepthTests.TestRenderModeRenderQueue());
    RunStdShader("StdShaderCutoutCutoff", () => StandardShaderDepthTests.TestCutoutAlphaCutoff());
    RunStdShader("StdShaderCbufferLayout", () => StandardShaderDepthTests.TestCbufferLayout());
    RunStdShader("StdShaderLittleEndian", () => StandardShaderDepthTests.TestCbufferLittleEndian());
    RunStdShader("StdShaderValidation", () => StandardShaderDepthTests.TestPropertyValidationBounds());
    RunStdShader("StdShaderEmissionHDR", () => StandardShaderDepthTests.TestEmissionHDR());
    RunStdShader("StdShaderDoubleSided", () => StandardShaderDepthTests.TestDoubleSidedFlag());
    Console.WriteLine($"  == StandardShaderDepth: {ssPass}/{ssTotal} passed ==");

    // ===== ShurikenParticle Depth (Unity5 Shuriken ParticleSystem: Curve/Module/Pipeline) =====
    int spPass = 0, spTotal = 0;
    void RunShuriken(string name, Action test)
    {
        spTotal++;
        try { test(); spPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunShuriken("ShurikenCurveConstant", () => ShurikenParticleDepthTests.TestCurveConstant());
    RunShuriken("ShurikenCurveLinear", () => ShurikenParticleDepthTests.TestCurveLinear());
    RunShuriken("ShurikenCurveLinearMultiKey", () => ShurikenParticleDepthTests.TestCurveLinearMultiKey());
    RunShuriken("ShurikenCurveTwoConstants", () => ShurikenParticleDepthTests.TestCurveTwoConstants());
    RunShuriken("ShurikenCurveBezier", () => ShurikenParticleDepthTests.TestCurveBezier());
    RunShuriken("ShurikenCurveBake", () => ShurikenParticleDepthTests.TestCurveBake());
    RunShuriken("ShurikenColorCurveDefault", () => ShurikenParticleDepthTests.TestColorCurveDefault());
    RunShuriken("ShurikenColorModuleAlphaFade", () => ShurikenParticleDepthTests.TestColorModuleDefaultAlphaFade());
    RunShuriken("ShurikenColorCurveCustom", () => ShurikenParticleDepthTests.TestColorCurveCustomGradient());
    RunShuriken("ShurikenSOAAllocateSlot", () => ShurikenParticleDepthTests.TestSOAAllocateSlot());
    RunShuriken("ShurikenSOAAliveFlags", () => ShurikenParticleDepthTests.TestSOAAliveFlags());
    RunShuriken("ShurikenSOAClearRecount", () => ShurikenParticleDepthTests.TestSOAClearRecount());
    RunShuriken("ShurikenSOAExhausted", () => ShurikenParticleDepthTests.TestSOAExhausted());
    RunShuriken("ShurikenEmitterRate", () => ShurikenParticleDepthTests.TestEmitterRateEmission());
    RunShuriken("ShurikenEmitterCone", () => ShurikenParticleDepthTests.TestEmitterConeShape());
    RunShuriken("ShurikenEmitterBox", () => ShurikenParticleDepthTests.TestEmitterBoxShape());
    RunShuriken("ShurikenEmitterBurst", () => ShurikenParticleDepthTests.TestEmitterBurst());
    RunShuriken("ShurikenLifetimeKill", () => ShurikenParticleDepthTests.TestLifetimeAgingKill());
    RunShuriken("ShurikenLifetimeNoKill", () => ShurikenParticleDepthTests.TestLifetimeNoKill());
    RunShuriken("ShurikenForceGravity", () => ShurikenParticleDepthTests.TestForceGravity());
    RunShuriken("ShurikenForceCustom", () => ShurikenParticleDepthTests.TestForceCustomDirection());
    RunShuriken("ShurikenColorFadeAlpha", () => ShurikenParticleDepthTests.TestColorModuleFadeAlpha());
    RunShuriken("ShurikenSizeOverLifetime", () => ShurikenParticleDepthTests.TestSizeModuleOverLifetime());
    RunShuriken("ShurikenSizeIndependent", () => ShurikenParticleDepthTests.TestSizeModuleIndependentAxes());
    RunShuriken("ShurikenRotation", () => ShurikenParticleDepthTests.TestRotationIntegration());
    RunShuriken("ShurikenPipelineOrdering", () => ShurikenParticleDepthTests.TestPipelineModuleOrdering());
    RunShuriken("ShurikenPipelineGetOrAdd", () => ShurikenParticleDepthTests.TestPipelineGetOrAdd());
    RunShuriken("ShurikenPipelineEventQueue", () => ShurikenParticleDepthTests.TestPipelineEventQueue());
    RunShuriken("ShurikenEventDispatch", () => ShurikenParticleDepthTests.TestEventQueueDispatch());
    RunShuriken("ShurikenPipelineFullUpdate", () => ShurikenParticleDepthTests.TestPipelineFullUpdate());
    Console.WriteLine($"  == ShurikenParticleDepth: {spPass}/{spTotal} passed ==");

    // ===== Mecanim Depth (Unity5 Animator Controller: 参数/条件/转移/混合曲线/ExitTime/Conduit) =====
    int mcPass = 0, mcTotal = 0;
    void RunMecanim(string name, Action test)
    {
        mcTotal++;
        try { test(); mcPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunMecanim("MecanimParamFactories", () => MecanimDepthTests.TestParameterFactories());
    RunMecanim("MecanimTriggerSetConsume", () => MecanimDepthTests.TestTriggerSetConsume());
    RunMecanim("MecanimParamSetters", () => MecanimDepthTests.TestStateMachineParameterSetters());
    RunMecanim("MecanimFloatConditions", () => MecanimDepthTests.TestFloatConditionOperators());
    RunMecanim("MecanimIntConditions", () => MecanimDepthTests.TestIntConditionOperators());
    RunMecanim("MecanimBoolConditions", () => MecanimDepthTests.TestBoolConditionOperators());
    RunMecanim("MecanimTriggerCondition", () => MecanimDepthTests.TestTriggerCondition());
    RunMecanim("MecanimMultiConditionAND", () => MecanimDepthTests.TestMultiConditionAND());
    RunMecanim("MecanimInstantTransition", () => MecanimDepthTests.TestInstantTransition());
    RunMecanim("MecanimCrossfadeProgress", () => MecanimDepthTests.TestCrossfadeTransitionProgress());
    RunMecanim("MecanimAlphaLinear", () => MecanimDepthTests.TestTransitionAlphaLinear());
    RunMecanim("MecanimBlendCurves", () => MecanimDepthTests.TestBlendCurves());
    RunMecanim("MecanimExitTime", () => MecanimDepthTests.TestExitTimeGate());
    RunMecanim("MecanimTriggerAutoConsume", () => MecanimDepthTests.TestTriggerAutoConsume());
    RunMecanim("MecanimLifecycleCallbacks", () => MecanimDepthTests.TestStateLifecycleCallbacks());
    RunMecanim("MecanimNoInterrupt", () => MecanimDepthTests.TestTransitionNoInterrupt());
    RunMecanim("MecanimGlobalConduit", () => MecanimDepthTests.TestGlobalConduitBlocks());
    RunMecanim("MecanimForceTransition", () => MecanimDepthTests.TestForceTransition());
    RunMecanim("MecanimInitialState", () => MecanimDepthTests.TestInitialStateIndex());
    RunMecanim("MecanimReset", () => MecanimDepthTests.TestStateMachineReset());
    Console.WriteLine($"  == MecanimDepth: {mcPass}/{mcTotal} passed ==");

    // Unity5 NavMesh 深度补充（Filter/Obstacle/ModifierVolume/Tile Salt/OffMesh/Link）
    int unmPass = 0, unmTotal = 0;
    void RunUnityNavMesh(string name, Action test)
    {
        unmTotal++;
        try { test(); unmPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunUnityNavMesh("UnityFilterDefaults", () => NavMeshQueryTests.TestFilterDefaults());
    RunUnityNavMesh("UnityFilterCustomCost", () => NavMeshQueryTests.TestFilterCustomAreaCost());
    RunUnityNavMesh("UnityFilterExcludeFlags", () => NavMeshQueryTests.TestFilterExcludeFlags());
    RunUnityNavMesh("UnityWalkFilter", () => NavMeshQueryTests.TestWalkFilter());
    RunUnityNavMesh("UnitySwimFilter", () => NavMeshQueryTests.TestSwimFilter());
    RunUnityNavMesh("UnityHighCostReachable", () => NavMeshQueryTests.TestHighCostAreaStillReachable());
    RunUnityNavMesh("UnityAreaCostAccum", () => NavMeshQueryTests.TestAreaCostAccumulates());
    RunUnityNavMesh("UnityObstacleHit", () => NavMeshQueryTests.TestDynamicObstacleContainsPoint());
    RunUnityNavMesh("UnityObstacleInactive", () => NavMeshQueryTests.TestDynamicObstacleInactive());
    RunUnityNavMesh("UnityObstacleInfluenceCarve", () => NavMeshQueryTests.TestDynamicObstacleInfluenceAndCarve());
    RunUnityNavMesh("UnityVolumeDisable", () => NavMeshQueryTests.TestModifierVolumeDisable());
    RunUnityNavMesh("UnityVolumeCostOverride", () => NavMeshQueryTests.TestModifierVolumeCostOverride());
    RunUnityNavMesh("UnityVolumeInactive", () => NavMeshQueryTests.TestModifierVolumeInactive());
    RunUnityNavMesh("UnityTileSaltInvalidates", () => NavMeshQueryTests.TestTileSaltInvalidatesRefs());
    RunUnityNavMesh("UnityOffMeshConnection", () => NavMeshQueryTests.TestOffMeshConnectionData());
    RunUnityNavMesh("UnityNavMeshLinkTypes", () => NavMeshQueryTests.TestNavMeshLinkTypes());
    Console.WriteLine($"  == UnityNavMeshDepth: {unmPass}/{unmTotal} passed ==");

    // ===== Unity5 PostProcess Volume 深度块 =====
    int uppPass = 0, uppTotal = 0;
    void RunUnityPP(string name, Action test)
    {
        uppTotal++;
        try { test(); uppPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunUnityPP("UnityPPVolumeGlobalAlways", () => UnityPostProcessDepthTests.TestVolumeGlobalAlwaysContains());
    RunUnityPP("UnityPPVolumeLocalAABB", () => UnityPostProcessDepthTests.TestVolumeLocalAABB());
    RunUnityPP("UnityPPVolumeAddEffectPriority", () => UnityPostProcessDepthTests.TestVolumeAddEffectAndPriority());
    RunUnityPP("UnityPPBloomDefaults", () => UnityPostProcessDepthTests.TestBloomDefaults());
    RunUnityPP("UnityPPBloomIntensityScale", () => UnityPostProcessDepthTests.TestBloomIntensityScaling());
    RunUnityPP("UnityPPTonemapReinhard", () => UnityPostProcessDepthTests.TestTonemapReinhard());
    RunUnityPP("UnityPPTonemapACES", () => UnityPostProcessDepthTests.TestTonemapACES());
    RunUnityPP("UnityPPTonemapAgXNeutral", () => UnityPostProcessDepthTests.TestTonemapAgXNeutralRange());
    RunUnityPP("UnityPPTonemapExposure", () => UnityPostProcessDepthTests.TestTonemapExposureScaling());
    RunUnityPP("UnityPPGradingIdentity", () => UnityPostProcessDepthTests.TestColorGradingIdentity());
    RunUnityPP("UnityPPGradingDesaturate", () => UnityPostProcessDepthTests.TestColorGradingDesaturate());
    RunUnityPP("UnityPPGradingSatBoost", () => UnityPostProcessDepthTests.TestColorGradingSaturationBoost());
    RunUnityPP("UnityPPGradingContrast", () => UnityPostProcessDepthTests.TestColorGradingContrast());
    RunUnityPP("UnityPPGradingTemperature", () => UnityPostProcessDepthTests.TestColorGradingTemperature());
    RunUnityPP("UnityPPExposureManual", () => UnityPostProcessDepthTests.TestExposureManual());
    RunUnityPP("UnityPPExposureAutoAdapt", () => UnityPostProcessDepthTests.TestExposureAutoAdapt());
    Console.WriteLine($"  == UnityPostProcessDepth: {uppPass}/{uppTotal} passed ==");

    // ===== Unity5 Physics 深度块 (PhysicsMaterial / Collider / QueryFilter / 枚举 / 真实 PhysicsWorld) =====
    int uphPass = 0, uphTotal = 0;
    void RunUnityPhys(string name, Action test)
    {
        uphTotal++;
        try { test(); uphPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunUnityPhys("UnityPhysMaterialDefaults", () => UnityPhysicsDepthTests.TestMaterialDefaults());
    RunUnityPhys("UnityPhysMaterialCtor", () => UnityPhysicsDepthTests.TestMaterialParameterizedCtor());
    RunUnityPhys("UnityPhysCombineFrictionAvg", () => UnityPhysicsDepthTests.TestCombineFrictionAverage());
    RunUnityPhys("UnityPhysCombineFrictionMin", () => UnityPhysicsDepthTests.TestCombineFrictionMin());
    RunUnityPhys("UnityPhysCombineFrictionMax", () => UnityPhysicsDepthTests.TestCombineFrictionMax());
    RunUnityPhys("UnityPhysCombineRestitutionMul", () => UnityPhysicsDepthTests.TestCombineRestitutionMultiply());
    RunUnityPhys("UnityPhysCalcMassBox", () => UnityPhysicsDepthTests.TestCalculateMassBox());
    RunUnityPhys("UnityPhysCalcMassSphere", () => UnityPhysicsDepthTests.TestCalculateMassSphere());
    RunUnityPhys("UnityPhysMaterialClone", () => UnityPhysicsDepthTests.TestMaterialClone());
    RunUnityPhys("UnityPhysMaterialPresets", () => UnityPhysicsDepthTests.TestMaterialPresets());
    RunUnityPhys("UnityPhysColliderBox", () => UnityPhysicsDepthTests.TestColliderBox());
    RunUnityPhys("UnityPhysColliderBoxCenter", () => UnityPhysicsDepthTests.TestColliderBoxCenter());
    RunUnityPhys("UnityPhysColliderSphere", () => UnityPhysicsDepthTests.TestColliderSphere());
    RunUnityPhys("UnityPhysColliderCapsule", () => UnityPhysicsDepthTests.TestColliderCapsule());
    RunUnityPhys("UnityPhysQueryFilterDefault", () => UnityPhysicsDepthTests.TestQueryFilterDefault());
    RunUnityPhys("UnityPhysQueryFilterIgnoreTrig", () => UnityPhysicsDepthTests.TestQueryFilterIgnoreTriggers());
    RunUnityPhys("UnityPhysQueryFilterExplicit", () => UnityPhysicsDepthTests.TestQueryFilterExplicitIgnore());
    RunUnityPhys("UnityPhysConstraints", () => UnityPhysicsDepthTests.TestRigidbodyConstraints());
    RunUnityPhys("UnityPhysCCDMode", () => UnityPhysicsDepthTests.TestCCDMode());
    RunUnityPhys("UnityPhysLayerEnumValues", () => UnityPhysicsDepthTests.TestPhysicsLayerEnumValues());
    RunUnityPhys("UnityPhysLayerMaskGetMask", () => UnityPhysicsDepthTests.TestLayerMaskGetMask());
    RunUnityPhys("UnityPhysLayerCollisionDefaults", () => UnityPhysicsDepthTests.TestLayerCollisionMatrixDefaults());
    RunUnityPhys("UnityPhysLayerCollisionCustom", () => UnityPhysicsDepthTests.TestLayerCollisionMatrixCustom());
    RunUnityPhys("UnityPhysLayerCollisionMask", () => UnityPhysicsDepthTests.TestLayerCollisionMask());
    Console.WriteLine($"  == UnityPhysicsDepth: {uphPass}/{uphTotal} passed ==");

    // ===== Terrain Depth (Unity5 heightmap / splatmap / grass / trees / water / collider) =====
    int terPass = 0, terTotal = 0;
    void RunTerrain(string name, Action test)
    {
        terTotal++;
        try { test(); terPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunTerrain("TerrainHeightmapSetClamp", () => TerrainDepthTests.TestHeightmapSetClamp());
    RunTerrain("TerrainBilinearSample", () => TerrainDepthTests.TestHeightmapBilinearSample());
    RunTerrain("TerrainOutsideZero", () => TerrainDepthTests.TestHeightmapOutsideReturnsZero());
    RunTerrain("TerrainFlatNormalUp", () => TerrainDepthTests.TestHeightmapFlatNormalUp());
    RunTerrain("TerrainAddHill", () => TerrainDepthTests.TestHeightmapAddHill());
    RunTerrain("TerrainSplatSetClamp", () => TerrainDepthTests.TestSplatmapSetClamp());
    RunTerrain("TerrainSplatSample", () => TerrainDepthTests.TestSplatmapBilinearSample());
    RunTerrain("TerrainGrassGen", () => TerrainDepthTests.TestGrassGenerationAndVisible());
    RunTerrain("TerrainTreeFilter", () => TerrainDepthTests.TestTreeGenerationHeightFilter());
    RunTerrain("TerrainWaterWave", () => TerrainDepthTests.TestWaterWaveHeight());
    RunTerrain("TerrainWaterNormal", () => TerrainDepthTests.TestWaterWaveNormal());
    RunTerrain("TerrainRaycast", () => TerrainDepthTests.TestColliderRaycastFlatGround());
    RunTerrain("TerrainEmbedCheck", () => TerrainDepthTests.TestColliderSampleEmbedded());
    RunTerrain("TerrainDebugSlope", () => TerrainDepthTests.TestDebuggerFlatSlopeZero());
    RunTerrain("TerrainProfile", () => TerrainDepthTests.TestDebuggerProfile());
    Console.WriteLine($"  == TerrainDepth: {terPass}/{terTotal} passed ==");

    // ===== GI Probes Depth (Unity5 SphericalHarmonics / GIProbe / ProbeGrid / ProbeBlending) =====
    int giPass = 0, giTotal = 0;
    void RunGI(string name, Action test)
    {
        giTotal++;
        try { test(); giPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunGI("GISH9BasisConstants", () => GIDepthTests.TestSH9BasisConstants());
    RunGI("GISH4BasisCount", () => GIDepthTests.TestSH4BasisCount());
    RunGI("GIUniformIrradiance", () => GIDepthTests.TestUniformIrradianceReconstruction());
    RunGI("GIDirectionalIrradiance", () => GIDepthTests.TestDirectionalIrradiance());
    RunGI("GIGIProbeAccumQuery", () => GIDepthTests.TestGIProbeAccumulateQuery());
    RunGI("GIProbeGridConstruct", () => GIDepthTests.TestProbeGridConstruction());
    RunGI("GIProbeGridSampleClamp", () => GIDepthTests.TestProbeGridSampleClamped());
    RunGI("GICullEmbedded", () => GIDepthTests.TestProbeGridCullEmbedded());
    RunGI("GIBlendingWallVis", () => GIDepthTests.TestProbeBlendingWallVisibility());
    Console.WriteLine($"  == GIDepth: {giPass}/{giTotal} passed ==");

    // ===== LOD Depth (Unity5 QEM simplification / LOD chain / LOD switch / Hysteresis / Group LOD) =====
    int lodPass = 0, lodTotal = 0;
    void RunLOD(string name, Action test)
    {
        lodTotal++;
        try { test(); lodPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunLOD("LODQEMPlaneZero", () => LODDepthTests.TestQEMatrixPlaneZeroError());
    RunLOD("LODQEMOffPlane", () => LODDepthTests.TestQEMatrixOffPlaneError());
    RunLOD("LODSimplifierReduces", () => LODDepthTests.TestSimplifierReducesTriangles());
    RunLOD("LODChain5Levels", () => LODDepthTests.TestLODChainDefault5Levels());
    RunLOD("LODThresholdsMonotonic", () => LODDepthTests.TestScreenSizeThresholdsMonotonicDecrease());
    RunLOD("LODCoverageSelect", () => LODDepthTests.TestLODControllerCoverageSelection());
    RunLOD("LODHysteresisNoFlicker", () => LODDepthTests.TestLODControllerHysteresisNoFlicker());
    RunLOD("LODGroupSync", () => LODDepthTests.TestLODControllerGroupSync());
    RunLOD("LODCrossFadeTransition", () => LODDepthTests.TestLODControllerCrossFadeTransition());
    Console.WriteLine($"  == LODDepth: {lodPass}/{lodTotal} passed ==");

    // ===== Atmospheric Scattering Depth (Unity5 Rayleigh/Mie/TransmittanceLUT) =====
    int atmPass = 0, atmTotal = 0;
    void RunAtm(string name, Action test)
    {
        atmTotal++;
        try { test(); atmPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunAtm("AtmRayleighBlueScattersMore", () => AtmosphericScatteringDepthTests.TestRayleighBlueScattersMore());
    RunAtm("AtmRayleighPhaseSymmetric", () => AtmosphericScatteringDepthTests.TestRayleighPhaseSymmetric());
    RunAtm("AtmRayleighOD", () => AtmosphericScatteringDepthTests.TestRayleighOpticalDepthZeroHeight());
    RunAtm("AtmMiePhaseForwardPeak", () => AtmosphericScatteringDepthTests.TestMiePhaseForwardPeak());
    RunAtm("AtmMieDualLobeEqualWeight", () => AtmosphericScatteringDepthTests.TestMieDualLobeEqualWeight());
    RunAtm("AtmTransmittanceBeerLambert", () => AtmosphericScatteringDepthTests.TestTransmittanceBeerLambert());
    RunAtm("AtmSkyColorHorizonBrighter", () => AtmosphericScatteringDepthTests.TestSkyColorHorizonBrighter());
    RunAtm("AtmTransmittanceLUTGenerate", () => AtmosphericScatteringDepthTests.TestTransmittanceLUTGenerate());
    Console.WriteLine($"  == AtmScatteringDepth: {atmPass}/{atmTotal} passed ==");

    // ===== Skinning Depth (Unity5 骨骼验证/世界矩阵层次/根运动/CPU蒙皮/绑定权重) =====
    int skPass = 0, skTotal = 0;
    void RunSk(string name, Action test)
    {
        skTotal++;
        try { test(); skPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunSk("SkValidateEmpty", () => SkinningDepthTests.TestValidateEmptySkeleton());
    RunSk("SkValidateParentRange", () => SkinningDepthTests.TestValidateParentIndexOutOfRange());
    RunSk("SkValidateForwardRef", () => SkinningDepthTests.TestValidateParentForwardRef());
    RunSk("SkValidateEmptyName", () => SkinningDepthTests.TestValidateEmptyName());
    RunSk("SkWorldHierarchy", () => SkinningDepthTests.TestWorldMatrixHierarchy());
    RunSk("SkWorldRotAccum", () => SkinningDepthTests.TestWorldMatrixRotationAccumulation());
    RunSk("SkPoseRootMotion", () => SkinningDepthTests.TestPoseRootMotion());
    RunSk("SkPoseTranslateAccum", () => SkinningDepthTests.TestPoseTranslateAccumulates());
    RunSk("SkPoseCopyFrom", () => SkinningDepthTests.TestPoseCopyFrom());
    RunSk("SkPoseCloneIndependence", () => SkinningDepthTests.TestPoseCloneIndependence());
    RunSk("SkAutoBindNormalized", () => SkinningDepthTests.TestAutoBindWeightsNormalized());
    RunSk("SkAutoBindMax1", () => SkinningDepthTests.TestAutoBindMaxInfluencesOne());
    RunSk("SkAutoBindIBCount", () => SkinningDepthTests.TestAutoBindInverseBindCount());
    RunSk("SkCpuBindPoseIdentity", () => SkinningDepthTests.TestCpuSkinningBindPoseIdentity());
    RunSk("SkCpuDeformMoves", () => SkinningDepthTests.TestCpuSkinningDeformMovesVertices());
    RunSk("SkCpuDeformRotates", () => SkinningDepthTests.TestCpuSkinningDeformRotatesVertices());
    RunSk("SkFindBoneNotFound", () => SkinningDepthTests.TestFindBoneNotFound());
    RunSk("SkAddBoneIndex", () => SkinningDepthTests.TestAddBoneReturnsIndex());
    Console.WriteLine($"  == SkinningDepth: {skPass}/{skTotal} passed ==");

    // ===== ShaderGraph Depth (Unity5 可视化着色器图核心) =====
    int sgPass = 0, sgTotal = 0;
    void RunSg(string name, Action test)
    {
        sgTotal++;
        try { test(); sgPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunSg("SgAddNodeDefaults", () => ShaderGraphDepthTests.TestAddNodeFillsDefaults());
    RunSg("SgRemoveNodeCascades", () => ShaderGraphDepthTests.TestRemoveNodeCascadesConnections());
    RunSg("SgTypeSame", () => ShaderGraphDepthTests.TestTypeSame());
    RunSg("SgTypeFloatBroadcast", () => ShaderGraphDepthTests.TestTypeFloatBroadcast());
    RunSg("SgTypeHighToLow", () => ShaderGraphDepthTests.TestTypeHighToLowTruncate());
    RunSg("SgTypeBoolNonTex", () => ShaderGraphDepthTests.TestTypeBoolNonTexture());
    RunSg("SgTypeTextureSame", () => ShaderGraphDepthTests.TestTypeTextureSameOnly());
    RunSg("SgConnectSuccess", () => ShaderGraphDepthTests.TestConnectSuccess());
    RunSg("SgConnectIncompatible", () => ShaderGraphDepthTests.TestConnectIncompatibleTypes());
    RunSg("SgConnectReplace", () => ShaderGraphDepthTests.TestConnectReplacesExisting());
    RunSg("SgConnectFanout", () => ShaderGraphDepthTests.TestConnectFanoutFromOutput());
    RunSg("SgDisconnect", () => ShaderGraphDepthTests.TestDisconnect());
    RunSg("SgValidateNoOutput", () => ShaderGraphDepthTests.TestValidateMissingOutput());
    RunSg("SgValidateTypeMismatch", () => ShaderGraphDepthTests.TestValidateTypeMismatch());
    RunSg("SgValidateCycle", () => ShaderGraphDepthTests.TestValidateCycleDetection());
    RunSg("SgValidateRequiredPin", () => ShaderGraphDepthTests.TestValidateRequiredPin());
    RunSg("SgValidateValidGraph", () => ShaderGraphDepthTests.TestValidateValidGraph());
    RunSg("SgTopoSortOrder", () => ShaderGraphDepthTests.TestTopologicalSortOrder());
    RunSg("SgTopoSortNoOutput", () => ShaderGraphDepthTests.TestTopologicalSortNoOutput());
    RunSg("SgCodeGenPbrMaster", () => ShaderGraphDepthTests.TestCodeGenPbrMaster());
    RunSg("SgCodeGenFresnel", () => ShaderGraphDepthTests.TestCodeGenFresnel());
    RunSg("SgCodeGenLerpDefaults", () => ShaderGraphDepthTests.TestCodeGenLerpDefaults());
    RunSg("SgCodeGenTextureSample", () => ShaderGraphDepthTests.TestCodeGenTextureSample());
    RunSg("SgPrecisionPrefix", () => ShaderGraphDepthTests.TestPrecisionPrefix());
    RunSg("SgPropertyMgmt", () => ShaderGraphDepthTests.TestPropertyManagement());
    RunSg("SgPropertyHlsl", () => ShaderGraphDepthTests.TestPropertyHlslMapping());
    RunSg("SgPreviewFactories", () => ShaderGraphDepthTests.TestNodePreviewFactories());
    Console.WriteLine($"  == ShaderGraphDepth: {sgPass}/{sgTotal} passed ==");

    // ===== FlowVFX Depth (Unity5 VFX Graph 风格模块化粒子) =====
    int fvPass = 0, fvTotal = 0;
    void RunFv(string name, Action test)
    {
        fvTotal++;
        try { test(); fvPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunFv("FvDataAllocateDefaults", () => FlowVFXDepthTests.TestDataAllocateDefaults());
    RunFv("FvDataPoolFullReuse", () => FlowVFXDepthTests.TestDataPoolFullAndReuse());
    RunFv("FvDataNormalizedLife", () => FlowVFXDepthTests.TestDataNormalizedLife());
    RunFv("FvDataReset", () => FlowVFXDepthTests.TestDataReset());
    RunFv("FvSortDistanceDesc", () => FlowVFXDepthTests.TestSortDistanceDescending());
    RunFv("FvSortLifeAsc", () => FlowVFXDepthTests.TestSortLifeAscending());
    RunFv("FvSortByMaterial", () => FlowVFXDepthTests.TestSortByMaterial());
    RunFv("FvEmitterRate", () => FlowVFXDepthTests.TestEmitterRateMode());
    RunFv("FvEmitterDelay", () => FlowVFXDepthTests.TestEmitterDelayGate());
    RunFv("FvEmitterMaxLoops", () => FlowVFXDepthTests.TestEmitterMaxLoopsEnds());
    RunFv("FvEmitterBurst", () => FlowVFXDepthTests.TestEmitterBurstMode());
    RunFv("FvGravity", () => FlowVFXDepthTests.TestModuleGravity());
    RunFv("FvDrag", () => FlowVFXDepthTests.TestModuleDragLinear());
    RunFv("FvColorUpdate", () => FlowVFXDepthTests.TestModuleColorUpdate());
    RunFv("FvSizeUpdate", () => FlowVFXDepthTests.TestModuleSizeUpdate());
    RunFv("FvVortex", () => FlowVFXDepthTests.TestModuleVortexTangential());
    RunFv("FvOrbitDecay", () => FlowVFXDepthTests.TestModuleOrbitDecay());
    RunFv("FvVelocityLock", () => FlowVFXDepthTests.TestModuleVelocityLock());
    RunFv("FvCurlNoise", () => FlowVFXDepthTests.TestModuleCurlNoiseNonZero());
    RunFv("FvSimRate", () => FlowVFXDepthTests.TestSimSpawnerRate());
    RunFv("FvSimPauseStop", () => FlowVFXDepthTests.TestSimPauseStop());
    RunFv("FvSimGravityCull", () => FlowVFXDepthTests.TestSimGravityAndCull());
    RunFv("FvSimDeathEvents", () => FlowVFXDepthTests.TestSimDeathEvents());
    RunFv("FvSimFullPipeline", () => FlowVFXDepthTests.TestSimFullPipelineColorFade());
    RunFv("FvSimBudget", () => FlowVFXDepthTests.TestSimParticleBudget());
    RunFv("FvSimTotalSpawned", () => FlowVFXDepthTests.TestSimTotalSpawned());
    Console.WriteLine($"  == FlowVFXDepth: {fvPass}/{fvTotal} passed ==");

    // ===== DataAsset Depth (Unity5 ScriptableObject/DataAsset + Registry/Manager) =====
    int daPass = 0, daTotal = 0;
    void RunDa(string name, Action test)
    {
        daTotal++;
        try { test(); daPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunDa("DaDisplayNameAttrFallback", () => DataAssetDepthTests.TestDisplayNameAttributeAndFallback());
    RunDa("DaCategoriesMultiFallback", () => DataAssetDepthTests.TestCategoriesMultiAndFallback());
    RunDa("DaDefaults", () => DataAssetDepthTests.TestDefaults());
    RunDa("DaLoadUnloadEvents", () => DataAssetDepthTests.TestLoadUnloadEvents());
    RunDa("DaDeepCopyIndependence", () => DataAssetDepthTests.TestDeepCopyIndependence());
    RunDa("DaRefIsValid", () => DataAssetDepthTests.TestReferenceIsValid());
    RunDa("DaRefResolveLifecycle", () => DataAssetDepthTests.TestReferenceResolveLifecycle());
    RunDa("DaRegistryDupIdThrows", () => DataAssetDepthTests.TestRegistryDuplicateIdThrows());
    RunDa("DaRegistryLookup", () => DataAssetDepthTests.TestRegistryLookup());
    RunDa("DaRegistryUnregister", () => DataAssetDepthTests.TestRegistryUnregister());
    RunDa("DaMgrRefCounts", () => DataAssetDepthTests.TestManagerRefCounts());
    RunDa("DaMgrGetAsset", () => DataAssetDepthTests.TestManagerGetAsset());
    RunDa("DaMgrAssetLoadedEvent", () => DataAssetDepthTests.TestManagerAssetLoadedEvent());
    RunDa("DaJsonSaveLoadRoundTrip", () => DataAssetDepthTests.TestJsonSaveLoadRoundTrip());
    RunDa("DaLoadFromFileMissing", () => DataAssetDepthTests.TestLoadFromFileMissing());
    RunDa("DaReloadAsset", () => DataAssetDepthTests.TestReloadAsset());
    RunDa("DaShutdownClears", () => DataAssetDepthTests.TestShutdownClears());
    Console.WriteLine($"  == DataAssetDepth: {daPass}/{daTotal} passed ==");

    // ===== AssetHandle Depth (Unity5 GUID 引用/热重载基建) =====
    int ahPass = 0, ahTotal = 0;
    void RunAh(string name, Action test)
    {
        ahTotal++;
        try { test(); ahPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunAh("AhZero", () => AssetHandleDepthTests.TestHandleZero());
    RunAh("AhNew", () => AssetHandleDepthTests.TestHandleNew());
    RunAh("AhParse", () => AssetHandleDepthTests.TestHandleParse());
    RunAh("AhToString", () => AssetHandleDepthTests.TestHandleToString());
    RunAh("AhEquality", () => AssetHandleDepthTests.TestHandleEquality());
    RunAh("AhRegRegisterResolve", () => AssetHandleDepthTests.TestRegistryRegisterResolve());
    RunAh("AhRegResolveZero", () => AssetHandleDepthTests.TestRegistryResolveZero());
    RunAh("AhRegUnregister", () => AssetHandleDepthTests.TestRegistryUnregister());
    RunAh("AhRegWeakRefSemantics", () => AssetHandleDepthTests.TestRegistryWeakReferenceSemantics());
    RunAh("AhRegClear", () => AssetHandleDepthTests.TestRegistryClear());
    RunAh("AhSoftPtrDefault", () => AssetHandleDepthTests.TestSoftPtrDefault());
    RunAh("AhSoftPtrLazyLoad", () => AssetHandleDepthTests.TestSoftPtrLazyLoad());
    RunAh("AhSoftPtrSetHandle", () => AssetHandleDepthTests.TestSoftPtrSetHandleResetsCache());
    Console.WriteLine($"  == AssetHandleDepth: {ahPass}/{ahTotal} passed ==");

    // ===== BinarySerializer Depth (Unity5 序列化/Header/Migration/JSON/CSV) =====
    int bsPass = 0, bsTotal = 0;
    void RunBs(string name, Action test)
    {
        bsTotal++;
        try { test(); bsPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunBs("BsPrimitivesRoundTrip", () => BinarySerializerDepthTests.TestPrimitivesRoundTrip());
    RunBs("BsStringRoundTrip", () => BinarySerializerDepthTests.TestStringRoundTrip());
    RunBs("BsBytesRoundTrip", () => BinarySerializerDepthTests.TestBytesRoundTrip());
    RunBs("BsTypedArrays", () => BinarySerializerDepthTests.TestTypedArrays());
    RunBs("BsVectorTypes", () => BinarySerializerDepthTests.TestVectorTypes());
    RunBs("BsMatrix4x4", () => BinarySerializerDepthTests.TestMatrix4x4());
    RunBs("BsWriteReadObject", () => BinarySerializerDepthTests.TestWriteReadObject());
    RunBs("BsCompressDecompress", () => BinarySerializerDepthTests.TestCompressDecompress());
    RunBs("BsSaveLoadFile", () => BinarySerializerDepthTests.TestSaveLoadFile());
    RunBs("BsReset", () => BinarySerializerDepthTests.TestReset());
    RunBs("BsVersionedHeader", () => BinarySerializerDepthTests.TestVersionedHeaderRoundTrip());
    RunBs("BsSaveWithHeader", () => BinarySerializerDepthTests.TestSaveWithHeaderRoundTrip());
    RunBs("BsMigration", () => BinarySerializerDepthTests.TestMigrationTriggered());
    RunBs("BsJsonWrapper", () => BinarySerializerDepthTests.TestJsonWrapper());
    RunBs("BsCsvSerializer", () => BinarySerializerDepthTests.TestCsvSerializer());
    RunBs("BsSerializedTypeEnum", () => BinarySerializerDepthTests.TestSerializedTypeEnum());
    Console.WriteLine($"  == BinarySerializerDepth: {bsPass}/{bsTotal} passed ==");

    // ===== SceneWorld Depth (Unity5 SceneManager + WorldReal + SceneManagerReal) =====
    int swPass = 0, swTotal = 0;
    void RunSw(string name, Action test)
    {
        swTotal++;
        try { test(); swPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunSw("SceneManagerNewScene", () => SceneWorldDepthTests.TestSceneManagerNewScene());
    RunSw("SceneManagerLoadSaveRoundtrip", () => SceneWorldDepthTests.TestSceneManagerLoadSaveRoundtrip());
    RunSw("SceneManagerSwitchPopStack", () => SceneWorldDepthTests.TestSceneManagerSwitchPopStack());
    RunSw("SceneManagerPushSubAndInstantiate", () => SceneWorldDepthTests.TestSceneManagerPushSubAndInstantiate());
    RunSw("SceneManagerAutoSaveAndTrySave", () => SceneWorldDepthTests.TestSceneManagerAutoSaveAndTrySave());
    RunSw("WorldRealDefaults", () => SceneWorldDepthTests.TestWorldRealDefaults());
    RunSw("WorldRealAddSystemAndUpdate", () => SceneWorldDepthTests.TestWorldRealAddSystemAndUpdate());
    RunSw("WorldRealFixedStep", () => SceneWorldDepthTests.TestWorldRealFixedStep());
    RunSw("WorldRealPauseAndTimeScale", () => SceneWorldDepthTests.TestWorldRealPauseAndTimeScale());
    RunSw("WorldRealStartStop", () => SceneWorldDepthTests.TestWorldRealStartStop());
    RunSw("WorldRealQueryCache", () => SceneWorldDepthTests.TestWorldRealQueryCache());
    RunSw("WorldManagerDefaults", () => SceneWorldDepthTests.TestWorldManagerDefaults());
    RunSw("WorldManagerUpdateAll", () => SceneWorldDepthTests.TestWorldManagerUpdateAll());
    RunSw("WorldManagerTransferEntity", () => SceneWorldDepthTests.TestWorldManagerTransferEntity());
    RunSw("SceneManagerRealSingle", () => SceneWorldDepthTests.TestSceneManagerRealSingle());
    RunSw("SceneManagerRealAdditiveAndSetActive", () => SceneWorldDepthTests.TestSceneManagerRealAdditiveAndSetActive());
    RunSw("SceneManagerRealUnloadAdditive", () => SceneWorldDepthTests.TestSceneManagerRealUnloadAdditive());
    RunSw("SceneManagerRealEvents", () => SceneWorldDepthTests.TestSceneManagerRealEvents());
    RunSw("SceneManagerRealSceneDataProvider", () => SceneWorldDepthTests.TestSceneManagerRealSceneDataProvider());
    Console.WriteLine($"  == SceneWorldDepth: {swPass}/{swTotal} passed ==");

    // ===== PostProcess Volume Depth (Unity5 PPV + VolumeBlendSystem) =====
    int ppvPass = 0, ppvTotal = 0;
    void RunPpv(string name, Action test)
    {
        ppvTotal++;
        try { test(); ppvPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunPpv("PPVSphereComputeWeight", () => PostProcessVolumeDepthTests.TestSphereComputeWeight());
    RunPpv("PPVBoxComputeWeight", () => PostProcessVolumeDepthTests.TestBoxComputeWeight());
    RunPpv("PPVInfiniteRangeVolume", () => PostProcessVolumeDepthTests.TestInfiniteRangeVolume());
    RunPpv("PPVDisabledVolume", () => PostProcessVolumeDepthTests.TestDisabledVolume());
    RunPpv("PPVCapsuleComputeWeight", () => PostProcessVolumeDepthTests.TestCapsuleComputeWeight());
    RunPpv("PPVConvexComputeWeight", () => PostProcessVolumeDepthTests.TestConvexComputeWeight());
    RunPpv("PPVSettingsBlendSingleOverride", () => PostProcessVolumeDepthTests.TestSettingsBlendSingleOverride());
    RunPpv("PPVSettingsBlendPriorityAndWeight", () => PostProcessVolumeDepthTests.TestSettingsBlendPriorityAndWeight());
    RunPpv("PPVSettingsBlendEnumByPriority", () => PostProcessVolumeDepthTests.TestSettingsBlendEnumByPriority());
    RunPpv("PPVApplyToPostProcessingStack", () => PostProcessVolumeDepthTests.TestApplyToPostProcessingStack());
    RunPpv("PPVVolumeBlendSystemUpdate", () => PostProcessVolumeDepthTests.TestVolumeBlendSystemUpdate());
    RunPpv("PPVVolumeBlendSystemCameraOutside", () => PostProcessVolumeDepthTests.TestVolumeBlendSystemCameraOutside());
    RunPpv("PPVVolumeBlendSystemPrioritySort", () => PostProcessVolumeDepthTests.TestVolumeBlendSystemPrioritySort());
    RunPpv("PPVVolumeBlendSystemRemoveAndClear", () => PostProcessVolumeDepthTests.TestVolumeBlendSystemRemoveAndClear());
    Console.WriteLine($"  == PostProcessVolumeDepth: {ppvPass}/{ppvTotal} passed ==");

    // ===== Tilemap + Autotile Depth (Unity5 Tilemap + Terrain) =====
    int tmapPass = 0, tmapTotal = 0;
    void RunTmap(string name, Action test)
    {
        tmapTotal++;
        try { test(); tmapPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunTmap("TileSetCRUD", () => TileMapDepthTests.TestTileSetCRUD());
    RunTmap("TileCustomData", () => TileMapDepthTests.TestTileCustomData());
    RunTmap("TileMapLayers", () => TileMapDepthTests.TestTileMapLayers());
    RunTmap("SetGetEraseCell", () => TileMapDepthTests.TestSetGetEraseCell());
    RunTmap("ClearLayerAndGetUsedCells", () => TileMapDepthTests.TestClearLayerAndGetUsedCells());
    RunTmap("CoordinateConversion", () => TileMapDepthTests.TestCoordinateConversion());
    RunTmap("WorldCoordinateConversion", () => TileMapDepthTests.TestWorldCoordinateConversion());
    RunTmap("AutoTilePickGreedy", () => TileMapDepthTests.TestAutoTilePickGreedy());
    RunTmap("AutoTilePickNoMatch", () => TileMapDepthTests.TestAutoTilePickNoMatch());
    RunTmap("GetAutoTileMask", () => TileMapDepthTests.TestGetAutoTileMask());
    RunTmap("AutoFillLayer", () => TileMapDepthTests.TestAutoFillLayer());
    RunTmap("AutoFillLayerNoRules", () => TileMapDepthTests.TestAutoFillLayerNoRules());
    RunTmap("TileNeighborBits", () => TileMapDepthTests.TestTileNeighborBits());
    RunTmap("PickAutoTileQuery", () => TileMapDepthTests.TestPickAutoTileQuery());
    RunTmap("Vector2I", () => TileMapDepthTests.TestVector2I());
    Console.WriteLine($"  == TileMapDepth: {tmapPass}/{tmapTotal} passed ==");

    // ===== Light2D Depth (Unity5 URP Light2D 等价) =====
    int l2dPass = 0, l2dTotal = 0;
    void RunL2D(string name, Action test)
    {
        l2dTotal++;
        try { test(); l2dPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunL2D("L2DDirectionalNoNormal", () => Light2DDepthTests.TestDirectionalNoNormal());
    RunL2D("L2DDirectionalLambert", () => Light2DDepthTests.TestDirectionalLambert());
    RunL2D("L2DPointDistanceFalloff", () => Light2DDepthTests.TestPointDistanceFalloff());
    RunL2D("L2DPointNearZeroDist", () => Light2DDepthTests.TestPointNearZeroDistance());
    RunL2D("L2DAreaAABBFalloff", () => Light2DDepthTests.TestAreaAABBFalloff());
    RunL2D("L2DAreaRotation", () => Light2DDepthTests.TestAreaRotation());
    RunL2D("L2DDisabledLight", () => Light2DDepthTests.TestDisabledLight());
    RunL2D("L2DZeroIntensity", () => Light2DDepthTests.TestZeroIntensity());
    RunL2D("L2DCloneIsolation", () => Light2DDepthTests.TestCloneIsolation());
    RunL2D("L2DRigAddRemoveClear", () => Light2DDepthTests.TestRigAddRemoveClear());
    RunL2D("L2DRigEvaluateShadingRoute", () => Light2DDepthTests.TestRigEvaluateShading());
    RunL2D("L2DRigShadingStack", () => Light2DDepthTests.TestRigShadingStack());
    RunL2D("L2DRigColorSingleDominant", () => Light2DDepthTests.TestRigColorSingleDominant());
    RunL2D("L2DRigColorNoLight", () => Light2DDepthTests.TestRigColorNoLight());
    RunL2D("L2DRigColorMultiLight", () => Light2DDepthTests.TestRigColorMultiLight());
    Console.WriteLine($"  == Light2DDepth: {l2dPass}/{l2dTotal} passed ==");

    // ===== CelShading2D Depth (Unity5 Toon Material + 逐帧动画) =====
    int celPass = 0, celTotal = 0;
    void RunCel(string name, Action test)
    {
        celTotal++;
        try { test(); celPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunCel("CelMaterialDefaults", () => CelShading2DDepthTests.TestMaterialDefaults());
    RunCel("CelMaterialClone", () => CelShading2DDepthTests.TestMaterialClone());
    RunCel("CelClipFramesDuration", () => CelShading2DDepthTests.TestClipFramesAndDuration());
    RunCel("CelPlayerStateTransitions", () => CelShading2DDepthTests.TestPlayerStateTransitions());
    RunCel("CelPlayerFrameAdvance", () => CelShading2DDepthTests.TestPlayerFrameAdvance());
    RunCel("CelPlayerLoop", () => CelShading2DDepthTests.TestPlayerLoop());
    RunCel("CelPlayerNoLoopEnd", () => CelShading2DDepthTests.TestPlayerNoLoopEnd());
    RunCel("CelPlayerPlaybackSpeed", () => CelShading2DDepthTests.TestPlayerPlaybackSpeed());
    RunCel("CelPlayerPauseFreezes", () => CelShading2DDepthTests.TestPlayerPauseFreezesTime());
    RunCel("CelPlayerStopResets", () => CelShading2DDepthTests.TestPlayerStopResets());
    RunCel("CelPlayerNoClipSafe", () => CelShading2DDepthTests.TestPlayerNoClipSafe());
    RunCel("CelPlayerNormalizedTime", () => CelShading2DDepthTests.TestPlayerNormalizedTime());
    RunCel("CelSpriteDataDefaults", () => CelShading2DDepthTests.TestSpriteDataDefaults());
    RunCel("CelClipAddFramesBatch", () => CelShading2DDepthTests.TestClipAddFramesBatch());
    Console.WriteLine($"  == CelShading2DDepth: {celPass}/{celTotal} passed ==");

    // ===== CelRendering2D Depth (Unity5 Sprite Sorter + 9-Slice + Camera Follow) =====
    int crPass = 0, crTotal = 0;
    void RunCR(string name, Action test)
    {
        crTotal++;
        try { test(); crPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunCR("SorterZOrder", () => CelRendering2DDepthTests.TestSorterZOrder());
    RunCR("SorterYOrder", () => CelRendering2DDepthTests.TestSorterYOrder());
    RunCR("SorterZTieBreakX", () => CelRendering2DDepthTests.TestSorterZTieBreakX());
    RunCR("SorterBatchByTextureThenZ", () => CelRendering2DDepthTests.TestSorterBatchByTextureThenZ());
    RunCR("SorterEmptyInput", () => CelRendering2DDepthTests.TestSorterEmptyInput());
    RunCR("BatchCountProperty", () => CelRendering2DDepthTests.TestBatchCountProperty());
    RunCR("9SliceProduces9Quads", () => CelRendering2DDepthTests.Test9SliceProduces9Quads());
    RunCR("9SliceCornersKeepBorder", () => CelRendering2DDepthTests.Test9SliceCornersKeepBorderSize());
    RunCR("9SliceCenterStretches", () => CelRendering2DDepthTests.Test9SliceCenterStretches());
    RunCR("9SliceCenterUV", () => CelRendering2DDepthTests.Test9SliceCenterUV());
    RunCR("9SliceSmallDestClips", () => CelRendering2DDepthTests.Test9SliceSmallDestClips());
    RunCR("9SliceInvalidRegion", () => CelRendering2DDepthTests.Test9SliceInvalidRegion());
    RunCR("9SliceBorderIsZero", () => CelRendering2DDepthTests.Test9SliceBorderIsZero());
    RunCR("9SliceToSprite", () => CelRendering2DDepthTests.Test9SliceToSprite());
    RunCR("9SliceBuildSprites", () => CelRendering2DDepthTests.Test9SliceBuildSprites());
    RunCR("CameraDefaults", () => CelRendering2DDepthTests.TestCameraDefaults());
    RunCR("CameraSnapToTarget", () => CelRendering2DDepthTests.TestCameraSnapToTarget());
    RunCR("CameraDeadZoneHolds", () => CelRendering2DDepthTests.TestCameraDeadZoneHolds());
    RunCR("CameraDeadZoneBreak", () => CelRendering2DDepthTests.TestCameraDeadZoneBreak());
    RunCR("CameraDirectLerp", () => CelRendering2DDepthTests.TestCameraDirectLerp());
    RunCR("CameraClampBigWorld", () => CelRendering2DDepthTests.TestCameraClampBigWorld());
    RunCR("CameraClampSmallWorld", () => CelRendering2DDepthTests.TestCameraClampSmallWorld());
    RunCR("CameraZoomAffectsClamp", () => CelRendering2DDepthTests.TestCameraZoomAffectsClamp());
    RunCR("CameraShakeDecay", () => CelRendering2DDepthTests.TestCameraShakeDecay());
    RunCR("CameraZeroDtNoOp", () => CelRendering2DDepthTests.TestCameraZeroDtNoOp());
    RunCR("Rectangle2D", () => CelRendering2DDepthTests.TestRectangle2D());
    Console.WriteLine($"  == CelRendering2DDepth: {crPass}/{crTotal} passed ==");

    // ===== CelAnimStateMachine2D Depth (Unity5 2D 动画状态机) =====
    int casmPass = 0, casmTotal = 0;
    void RunCasm(string name, Action test)
    {
        casmTotal++;
        try { test(); casmPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunCasm("CasmStateDefaults", () => CelAnimStateMachine2DDepthTests.TestStateDefaults());
    RunCasm("CasmTransitionDefaults", () => CelAnimStateMachine2DDepthTests.TestTransitionDefaults());
    RunCasm("CasmEnterSetsCurrentState", () => CelAnimStateMachine2DDepthTests.TestEnterSetsCurrentState());
    RunCasm("CasmEnterUnknownReturnsFalse", () => CelAnimStateMachine2DDepthTests.TestEnterUnknownStateReturnsFalse());
    RunCasm("CasmUpdateAdvancesClip", () => CelAnimStateMachine2DDepthTests.TestUpdateAdvancesClip());
    RunCasm("CasmConditionTransition", () => CelAnimStateMachine2DDepthTests.TestConditionTransition());
    RunCasm("CasmFireTrigger", () => CelAnimStateMachine2DDepthTests.TestFireTrigger());
    RunCasm("CasmFireRequiresFromState", () => CelAnimStateMachine2DDepthTests.TestFireRequiresFromState());
    RunCasm("CasmEnterAppliesSpeed", () => CelAnimStateMachine2DDepthTests.TestEnterAppliesSpeed());
    RunCasm("CasmBindSpriteSyncsRect", () => CelAnimStateMachine2DDepthTests.TestBindSpriteSyncsSourceRect());
    RunCasm("CasmIsFinishedAfterNoLoop", () => CelAnimStateMachine2DDepthTests.TestIsFinishedAfterNoLoopClip());
    RunCasm("CasmReEnterResetsFrame", () => CelAnimStateMachine2DDepthTests.TestReEnterResetsFrame());
    RunCasm("CasmCurrentStateIndex", () => CelAnimStateMachine2DDepthTests.TestCurrentStateIndexTracksOrder());
    RunCasm("CasmTransitionRestartsClip", () => CelAnimStateMachine2DDepthTests.TestTransitionRestartsClip());
    Console.WriteLine($"  == CelAnimStateMachine2DDepth: {casmPass}/{casmTotal} passed ==");

    // ===== CelSpriteAtlas Depth (Unity5 SpriteAtlas 等价物) =====
    int atlasPass = 0, atlasTotal = 0;
    void RunAtlas(string name, Action test)
    {
        atlasTotal++;
        try { test(); atlasPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunAtlas("RegionIsValid", () => CelSpriteAtlasDepthTests.TestRegionIsValid());
    RunAtlas("RegionCenterSize", () => CelSpriteAtlasDepthTests.TestRegionCenterSize());
    RunAtlas("RegionSourceRectNormalized", () => CelSpriteAtlasDepthTests.TestRegionSourceRectNormalized());
    RunAtlas("RegionSourceRectInvalidFallback", () => CelSpriteAtlasDepthTests.TestRegionSourceRectInvalidFallback());
    RunAtlas("AtlasCtorDefaults", () => CelSpriteAtlasDepthTests.TestAtlasCtorDefaults());
    RunAtlas("AddRegion", () => CelSpriteAtlasDepthTests.TestAddRegion());
    RunAtlas("AddRegionSameNameOverwrites", () => CelSpriteAtlasDepthTests.TestAddRegionSameNameOverwrites());
    RunAtlas("AddRegionEmptyNameThrows", () => CelSpriteAtlasDepthTests.TestAddRegionEmptyNameThrows());
    RunAtlas("AddRegionBadSizeThrows", () => CelSpriteAtlasDepthTests.TestAddRegionBadSizeThrows());
    RunAtlas("GetRegionMissing", () => CelSpriteAtlasDepthTests.TestGetRegionMissing());
    RunAtlas("GetSourceRect", () => CelSpriteAtlasDepthTests.TestGetSourceRect());
    RunAtlas("ContainsAndTryGet", () => CelSpriteAtlasDepthTests.TestContainsAndTryGet());
    RunAtlas("AddGrid", () => CelSpriteAtlasDepthTests.TestAddGrid());
    RunAtlas("AddGridMarginSpacing", () => CelSpriteAtlasDepthTests.TestAddGridMarginSpacing());
    RunAtlas("AddGridBadCellThrows", () => CelSpriteAtlasDepthTests.TestAddGridBadCellThrows());
    RunAtlas("Clear", () => CelSpriteAtlasDepthTests.TestClear());
    RunAtlas("RegionsOrdered", () => CelSpriteAtlasDepthTests.TestRegionsOrdered());
    Console.WriteLine($"  == CelSpriteAtlasDepth: {atlasPass}/{atlasTotal} passed ==");

    // ===== CelRenderLayers2D Depth (Unity5 Sorting Layers 等价物) =====
    int layPass = 0, layTotal = 0;
    void RunLayers(string name, Action test)
    {
        layTotal++;
        try { test(); layPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunLayers("LayerDefaults", () => CelRenderLayers2DDepthTests.TestLayerDefaults());
    RunLayers("LayerNamedCtor", () => CelRenderLayers2DDepthTests.TestLayerNamedCtor());
    RunLayers("AddLayerSortedByIndex", () => CelRenderLayers2DDepthTests.TestAddLayerSortedByIndex());
    RunLayers("GetOrCreateLayer", () => CelRenderLayers2DDepthTests.TestGetOrCreateLayer());
    RunLayers("RemoveAndClear", () => CelRenderLayers2DDepthTests.TestRemoveAndClear());
    RunLayers("AssignCreatesLayerAndAdds", () => CelRenderLayers2DDepthTests.TestAssignCreatesLayerAndAdds());
    RunLayers("FlattenSpritesOrder", () => CelRenderLayers2DDepthTests.TestFlattenSpritesOrder());
    RunLayers("HiddenLayerFiltered", () => CelRenderLayers2DDepthTests.TestHiddenLayerFiltered());
    RunLayers("ZeroOpacityFiltered", () => CelRenderLayers2DDepthTests.TestZeroOpacityLayerFiltered());
    RunLayers("VisibleUpToInclusive", () => CelRenderLayers2DDepthTests.TestVisibleUpToInclusive());
    RunLayers("AssignSameReference", () => CelRenderLayers2DDepthTests.TestAssignDoesNotCopyList());
    RunLayers("FlattenParticles", () => CelRenderLayers2DDepthTests.TestFlattenParticles());
    Console.WriteLine($"  == CelRenderLayers2DDepth: {layPass}/{layTotal} passed ==");

    // ===== CelParticleSystem2D Depth (Unity5 2D ParticleSystem 等价物) =====
    int partPass = 0, partTotal = 0;
    void RunParticles(string name, Action test)
    {
        partTotal++;
        try { test(); partPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunParticles("ParticleDefaults", () => CelParticleSystem2DDepthTests.TestParticleDefaults());
    RunParticles("EmitterDefaults", () => CelParticleSystem2DDepthTests.TestEmitterDefaults());
    RunParticles("EmitBurst", () => CelParticleSystem2DDepthTests.TestEmitBurst());
    RunParticles("EmitRate", () => CelParticleSystem2DDepthTests.TestEmitRate());
    RunParticles("UpdateRemovesExpired", () => CelParticleSystem2DDepthTests.TestUpdateRemovesExpired());
    RunParticles("SizeInterpolation", () => CelParticleSystem2DDepthTests.TestSizeInterpolation());
    RunParticles("ColorInterpolation", () => CelParticleSystem2DDepthTests.TestColorInterpolation());
    RunParticles("GravityAffectsVelocity", () => CelParticleSystem2DDepthTests.TestGravityAffectsVelocity());
    RunParticles("DragDecelerates", () => CelParticleSystem2DDepthTests.TestDragDecelerates());
    RunParticles("RotationSpeed", () => CelParticleSystem2DDepthTests.TestRotationSpeed());
    RunParticles("MaxParticlesCap", () => CelParticleSystem2DDepthTests.TestMaxParticlesCap());
    RunParticles("Clear", () => CelParticleSystem2DDepthTests.TestClear());
    RunParticles("ToSpriteDataEmptyPath", () => CelParticleSystem2DDepthTests.TestToSpriteDataEmptyPath());
    RunParticles("ToSpriteDataGenerates", () => CelParticleSystem2DDepthTests.TestToSpriteDataGenerates());
    RunParticles("QuantizeBands", () => CelParticleSystem2DDepthTests.TestQuantizeBands());
    RunParticles("NonLoopEmitterTimeout", () => CelParticleSystem2DDepthTests.TestNonLoopEmitterTimeout());
    RunParticles("DirectionSpread", () => CelParticleSystem2DDepthTests.TestDirectionSpread());
    RunParticles("ShapeCircle", () => CelParticleSystem2DDepthTests.TestShapeCircle());
    RunParticles("ShapeBox", () => CelParticleSystem2DDepthTests.TestShapeBox());
    RunParticles("NonLoopAccumTimeout", () => CelParticleSystem2DDepthTests.TestNonLoopAccumTimeout());
    Console.WriteLine($"  == CelParticleSystem2DDepth: {partPass}/{partTotal} passed ==");

    // ===== CelSkeletalAnimation2D Depth (Unity5 2D 骨骼动画 Spine 等价物) =====
    int skelPass = 0, skelTotal = 0;
    void RunSkel(string name, Action test)
    {
        skelTotal++;
        try { test(); skelPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunSkel("BoneDefaults", () => CelSkeletalAnimation2DDepthTests.TestBoneDefaults());
    RunSkel("PoseDefaults", () => CelSkeletalAnimation2DDepthTests.TestPoseDefaults());
    RunSkel("SkeletonAddBoneOrder", () => CelSkeletalAnimation2DDepthTests.TestSkeletonAddBoneOrder());
    RunSkel("SetPoseChain", () => CelSkeletalAnimation2DDepthTests.TestSetPoseChain());
    RunSkel("SetPoseRotationChain", () => CelSkeletalAnimation2DDepthTests.TestSetPoseRotationChain());
    RunSkel("TransformPoint", () => CelSkeletalAnimation2DDepthTests.TestTransformPoint());
    RunSkel("SkinningNoWeights", () => CelSkeletalAnimation2DDepthTests.TestSkinningNoWeights());
    RunSkel("SkinningBlend", () => CelSkeletalAnimation2DDepthTests.TestSkinningBlend());
    RunSkel("TrackEmpty", () => CelSkeletalAnimation2DDepthTests.TestTrackEmpty());
    RunSkel("TrackSingleKey", () => CelSkeletalAnimation2DDepthTests.TestTrackSingleKey());
    RunSkel("TrackLinearInterp", () => CelSkeletalAnimation2DDepthTests.TestTrackLinearInterp());
    RunSkel("TrackClampRange", () => CelSkeletalAnimation2DDepthTests.TestTrackClampRange());
    RunSkel("ClipDuration", () => CelSkeletalAnimation2DDepthTests.TestClipDuration());
    RunSkel("ClipSampleMultiTrack", () => CelSkeletalAnimation2DDepthTests.TestClipSampleMultiTrack());
    RunSkel("AnimatorPlayUpdate", () => CelSkeletalAnimation2DDepthTests.TestAnimatorPlayUpdate());
    RunSkel("AnimatorLoop", () => CelSkeletalAnimation2DDepthTests.TestAnimatorLoop());
    RunSkel("AnimatorNoLoopStop", () => CelSkeletalAnimation2DDepthTests.TestAnimatorNoLoopStop());
    RunSkel("AnimatorPlaybackSpeed", () => CelSkeletalAnimation2DDepthTests.TestAnimatorPlaybackSpeed());
    RunSkel("AnimatorPauseResumeStop", () => CelSkeletalAnimation2DDepthTests.TestAnimatorPauseResumeStop());
    RunSkel("LerpAngleShortestArc", () => CelSkeletalAnimation2DDepthTests.TestLerpAngleShortestArc());
    Console.WriteLine($"  == CelSkeletalAnimation2DDepth: {skelPass}/{skelTotal} passed ==");

    int cphysPass = 0, cphysTotal = 0;
    void RunPhys2D(string name, Action test)
    {
        cphysTotal++;
        try { test(); cphysPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name} — {ex.Message}"); }
    }
    RunPhys2D("CircleMass", () => CelPhysics2DDepthTests.TestCircleMass());
    RunPhys2D("BoxMass", () => CelPhysics2DDepthTests.TestBoxMass());
    RunPhys2D("PolygonMass", () => CelPhysics2DDepthTests.TestPolygonMass());
    RunPhys2D("BoxInertiaPositive", () => CelPhysics2DDepthTests.TestBoxInertiaPositive());
    RunPhys2D("BodyRecomputeMass", () => CelPhysics2DDepthTests.TestBodyRecomputeMass());
    RunPhys2D("SetStaticZeroMass", () => CelPhysics2DDepthTests.TestSetStaticZeroMass());
    RunPhys2D("ApplyImpulseChangesVelocity", () => CelPhysics2DDepthTests.TestApplyImpulseChangesVelocity());
    RunPhys2D("StaticImpulseNoEffect", () => CelPhysics2DDepthTests.TestStaticImpulseNoEffect());
    RunPhys2D("GravityIntegrationVelocity", () => CelPhysics2DDepthTests.TestGravityIntegrationVelocity());
    RunPhys2D("GravityDirectionDefault", () => CelPhysics2DDepthTests.TestGravityDirectionDefault());
    RunPhys2D("ZeroDeltaTimeNoStep", () => CelPhysics2DDepthTests.TestZeroDeltaTimeNoStep());
    RunPhys2D("CircleCircleSeparate", () => CelPhysics2DDepthTests.TestCircleCircleSeparate());
    RunPhys2D("CircleCircleDepthAndNormal", () => CelPhysics2DDepthTests.TestCircleCircleDepthAndNormal());
    RunPhys2D("ManifoldCircleBox", () => CelPhysics2DDepthTests.TestManifoldCircleBox());
    RunPhys2D("BoxBoxOverlap", () => CelPhysics2DDepthTests.TestBoxBoxOverlap());
    RunPhys2D("BoxBoxSeparate", () => CelPhysics2DDepthTests.TestBoxBoxSeparate());
    RunPhys2D("PolygonPolygonOverlap", () => CelPhysics2DDepthTests.TestPolygonPolygonOverlap());
    RunPhys2D("WorldResolvesOverlap", () => CelPhysics2DDepthTests.TestWorldResolvesOverlap());
    RunPhys2D("StaticBodyImmovable", () => CelPhysics2DDepthTests.TestStaticBodyImmovable());
    RunPhys2D("ContactEventFires", () => CelPhysics2DDepthTests.TestContactEventFires());
    RunPhys2D("RaycastHitCircle", () => CelPhysics2DDepthTests.TestRaycastHitCircle());
    RunPhys2D("RaycastMiss", () => CelPhysics2DDepthTests.TestRaycastMiss());
    RunPhys2D("RaycastNearest", () => CelPhysics2DDepthTests.TestRaycastNearest());
    Console.WriteLine($"  == CelPhysics2DDepth: {cphysPass}/{cphysTotal} passed ==");

    int gamePass = 0, gameTotal = 0;
    void RunGame2D(string name, Action test)
    {
        gameTotal++;
        try { test(); gamePass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name} — {ex.Message}"); }
    }
    RunGame2D("SceneDefaults", () => CelGame2DDepthTests.TestSceneDefaults());
    RunGame2D("SceneUpdateAdvancesTime", () => CelGame2DDepthTests.TestSceneUpdateAdvancesTime());
    RunGame2D("AddSpriteLayerRouting", () => CelGame2DDepthTests.TestAddSpriteLayerRouting());
    RunGame2D("AddAnimatedSpriteCreatesPlayer", () => CelGame2DDepthTests.TestAddAnimatedSpriteCreatesPlayer());
    RunGame2D("BuildFrameCollectsSprites", () => CelGame2DDepthTests.TestBuildFrameCollectsSprites());
    RunGame2D("BuildFrameSyncsCamera", () => CelGame2DDepthTests.TestBuildFrameSyncsCamera());
    RunGame2D("BuildFrameCameraFollowSync", () => CelGame2DDepthTests.TestBuildFrameCameraFollowSync());
    RunGame2D("UpdateWritesCameraFollowBack", () => CelGame2DDepthTests.TestUpdateWritesCameraFollowBack());
    RunGame2D("GameDefaults", () => CelGame2DDepthTests.TestGameDefaults());
    RunGame2D("GameClampsDeltaTime", () => CelGame2DDepthTests.TestGameClampsDeltaTime());
    RunGame2D("GameSmallDtNotClamped", () => CelGame2DDepthTests.TestGameSmallDtNotClamped());
    RunGame2D("GameSetSceneEvent", () => CelGame2DDepthTests.TestGameSetSceneEvent());
    RunGame2D("GameUpdateDrivesCurrentScene", () => CelGame2DDepthTests.TestGameUpdateDrivesCurrentScene());
    RunGame2D("CreateDefaultScene", () => CelGame2DDepthTests.TestCreateDefaultScene());
    Console.WriteLine($"  == CelGame2DDepth: {gamePass}/{gameTotal} passed ==");

    int camProjPass = 0, camProjTotal = 0;
    void RunCamProj(string name, Action test)
    {
        camProjTotal++;
        try { test(); camProjPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name} — {ex.Message}"); }
    }
    RunCamProj("CameraDefaults", () => CelCameraProjectionDepthTests.TestCameraDefaults());
    RunCamProj("ZoomClampedToOne", () => CelCameraProjectionDepthTests.TestZoomClampedToOne());
    RunCamProj("PositionAlias", () => CelCameraProjectionDepthTests.TestPositionAlias());
    RunCamProj("WorldToScreenCenter", () => CelCameraProjectionDepthTests.TestWorldToScreenCenter());
    RunCamProj("WorldToScreenRight", () => CelCameraProjectionDepthTests.TestWorldToScreenRight());
    RunCamProj("ScreenToWorldCenter", () => CelCameraProjectionDepthTests.TestScreenToWorldCenter());
    RunCamProj("RoundTripIdentity", () => CelCameraProjectionDepthTests.TestRoundTripIdentity());
    RunCamProj("RoundTripZoomed", () => CelCameraProjectionDepthTests.TestRoundTripZoomed());
    RunCamProj("ZoomScalesScreenOffset", () => CelCameraProjectionDepthTests.TestZoomScalesScreenOffset());
    RunCamProj("BuildProjectionValid", () => CelCameraProjectionDepthTests.TestBuildProjectionValid());
    RunCamProj("ProjectionCenterToOrigin", () => CelCameraProjectionDepthTests.TestProjectionCenterToOrigin());
    RunCamProj("ProjectionRightEdge", () => CelCameraProjectionDepthTests.TestProjectionRightEdge());
    RunCamProj("ProjectionTopFlipped", () => CelCameraProjectionDepthTests.TestProjectionTopFlipped());
    RunCamProj("ProjectionZoomNarrows", () => CelCameraProjectionDepthTests.TestProjectionZoomNarrows());
    Console.WriteLine($"  == CelCameraProjectionDepth: {camProjPass}/{camProjTotal} passed ==");

    int n2dPass = 0, n2dTotal = 0;
    void RunNode2D(string name, Action test)
    {
        n2dTotal++;
        try { test(); n2dPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunNode2D("Node2DAddChildEstablishesHierarchy", () => Node2DTests.TestNode2DAddChildEstablishesHierarchy());
    RunNode2D("Node2DSetParentReparents", () => Node2DTests.TestNode2DSetParentReparents());
    RunNode2D("Node2DGlobalTransformAccumulatesParent", () => Node2DTests.TestNode2DGlobalTransformAccumulatesParent());
    RunNode2D("Node2DParentRotationAffectsChild", () => Node2DTests.TestNode2DParentRotationAffectsChild());
    RunNode2D("Node2DSetGlobalPositionInverts", () => Node2DTests.TestNode2DSetGlobalPositionInverts());
    RunNode2D("Node2DToGlobalToLocalRoundTrip", () => Node2DTests.TestNode2DToGlobalToLocalRoundTrip());
    RunNode2D("Node2DVisibilityPropagates", () => Node2DTests.TestNode2DVisibilityPropagates());
    RunNode2D("Node2DSelfHiddenNotVisibleInTree", () => Node2DTests.TestNode2DSelfHiddenNotVisibleInTree());
    RunNode2D("Node2DZIndexRenderOrder", () => Node2DTests.TestNode2DZIndexRenderOrder());
    RunNode2D("Node2DHiddenSubtreeExcludedFromRender", () => Node2DTests.TestNode2DHiddenSubtreeExcludedFromRender());
    RunNode2D("Node2DModulateAccumulates", () => Node2DTests.TestNode2DModulateAccumulates());
    Console.WriteLine($"  == Node2DDepth: {n2dPass}/{n2dTotal} passed ==");
    int sp2dPass = 0, sp2dTotal = 0;
    void RunSprite2D(string name, Action test)
    {
        sp2dTotal++;
        try { test(); sp2dPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunSprite2D("Sprite2DInheritsTransform", () => Sprite2DCameraTests.TestSprite2DInheritsTransform());
    RunSprite2D("Sprite2DCenterGlobalWithOffset", () => Sprite2DCameraTests.TestSprite2DCenterGlobalWithOffset());
    RunSprite2D("Sprite2DFlipFlags", () => Sprite2DCameraTests.TestSprite2DFlipFlags());
    RunSprite2D("SpriteFramesRegisterAndCount", () => Sprite2DCameraTests.TestSpriteFramesRegisterAndCount());
    RunSprite2D("AnimatedSprite2DFrameAdvances", () => Sprite2DCameraTests.TestAnimatedSprite2DFrameAdvances());
    RunSprite2D("AnimatedSprite2DLoopWraps", () => Sprite2DCameraTests.TestAnimatedSprite2DLoopWraps());
    RunSprite2D("AnimatedSprite2DNonLoopStopsAtEnd", () => Sprite2DCameraTests.TestAnimatedSprite2DNonLoopStopsAtEnd());
    RunSprite2D("AnimatedSprite2DPauseResume", () => Sprite2DCameraTests.TestAnimatedSprite2DPauseResume());
    RunSprite2D("AnimatedSprite2DSpeedScale", () => Sprite2DCameraTests.TestAnimatedSprite2DSpeedScale());
    RunSprite2D("Camera2DWorldToScreen", () => Sprite2DCameraTests.TestCamera2DWorldToScreen());
    RunSprite2D("Camera2DZoomProjection", () => Sprite2DCameraTests.TestCamera2DZoomProjection());
    RunSprite2D("Camera2DLimitBounds", () => Sprite2DCameraTests.TestCamera2DLimitBounds());
    RunSprite2D("Camera2DCurrentSwitch", () => Sprite2DCameraTests.TestCamera2DCurrentSwitch());
    Console.WriteLine($"  == Sprite2DCameraDepth: {sp2dPass}/{sp2dTotal} passed ==");
    int parallaxPass = 0, parallaxTotal = 0;
    void RunParallax(string name, Action test)
    {
        parallaxTotal++;
        try { test(); parallaxPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunParallax("ParallaxLayerMotionScaleZeroStaysFixed", () => Parallax2DTests.TestParallaxLayerMotionScaleZeroStaysFixed());
    RunParallax("ParallaxLayerMotionScaleOneFollowsWorld", () => Parallax2DTests.TestParallaxLayerMotionScaleOneFollowsWorld());
    RunParallax("ParallaxLayerHalfSpeed", () => Parallax2DTests.TestParallaxLayerHalfSpeed());
    RunParallax("ParallaxScrollOffsetEqualsNegativeCamera", () => Parallax2DTests.TestParallaxScrollOffsetEqualsNegativeCamera());
    RunParallax("ParallaxLayerMotionMirroringWraps", () => Parallax2DTests.TestParallaxLayerMotionMirroringWraps());
    RunParallax("ParallaxScrollBaseOffsetAdds", () => Parallax2DTests.TestParallaxScrollBaseOffsetAdds());
    RunParallax("ParallaxNullCameraSafe", () => Parallax2DTests.TestParallaxNullCameraSafe());
    RunParallax("ParallaxLayerAddRemove", () => Parallax2DTests.TestParallaxLayerAddRemove());
    RunParallax("ParallaxPredictScreenOffset", () => Parallax2DTests.TestParallaxPredictScreenOffset());
    RunParallax("ParallaxMultipleLayersDifferentScales", () => Parallax2DTests.TestParallaxMultipleLayersDifferentScales());
    RunParallax("ParallaxIgnoreCameraZoomFlag", () => Parallax2DTests.TestParallaxIgnoreCameraZoomFlag());
    Console.WriteLine($"  == Parallax2DDepth: {parallaxPass}/{parallaxTotal} passed ==");
    int ysortPass = 0, ysortTotal = 0;
    void RunYSort(string name, Action test)
    {
        ysortTotal++;
        try { test(); ysortPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunYSort("YSortSortsByGlobalY", () => YSortTests.TestYSortSortsByGlobalY());
    RunYSort("YSortDisabledKeepsTreeOrder", () => YSortTests.TestYSortDisabledKeepsTreeOrder());
    RunYSort("YSortUsesGlobalYFromParent", () => YSortTests.TestYSortUsesGlobalYFromParent());
    RunYSort("YSortRespectsZIndexFirst", () => YSortTests.TestYSortRespectsZIndexFirst());
    RunYSort("YSortStableForEqualY", () => YSortTests.TestYSortStableForEqualY());
    Console.WriteLine($"  == YSortDepth: {ysortPass}/{ysortTotal} passed ==");
    int cidPass = 0, cidTotal = 0;
    void RunCanvasItemDraw(string name, Action test)
    {
        cidTotal++;
        try { test(); cidPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunCanvasItemDraw("DrawRectRecordsCommand", () => CanvasItemDrawTests.TestDrawRectRecordsCommand());
    RunCanvasItemDraw("DrawLineRecordsCommand", () => CanvasItemDrawTests.TestDrawLineRecordsCommand());
    RunCanvasItemDraw("DrawCircleRecordsCommand", () => CanvasItemDrawTests.TestDrawCircleRecordsCommand());
    RunCanvasItemDraw("DrawPolygonRecordsVertices", () => CanvasItemDrawTests.TestDrawPolygonRecordsVertices());
    RunCanvasItemDraw("DrawPolylineRecordsCommand", () => CanvasItemDrawTests.TestDrawPolylineRecordsCommand());
    RunCanvasItemDraw("ClearDrawCommandsEmptiesBuffer", () => CanvasItemDrawTests.TestClearDrawCommandsEmptiesBuffer());
    RunCanvasItemDraw("WorldDrawCommandsApplyTranslation", () => CanvasItemDrawTests.TestWorldDrawCommandsApplyTranslation());
    RunCanvasItemDraw("WorldDrawCommandsApplyRotation", () => CanvasItemDrawTests.TestWorldDrawCommandsApplyRotation());
    RunCanvasItemDraw("WorldDrawCommandsApplyScale", () => CanvasItemDrawTests.TestWorldDrawCommandsApplyScale());
    RunCanvasItemDraw("WorldDrawCommandsInheritParentTransform", () => CanvasItemDrawTests.TestWorldDrawCommandsInheritParentTransform());
    Console.WriteLine($"  == CanvasItemDrawDepth: {cidPass}/{cidTotal} passed ==");
    int twPass = 0, twTotal = 0;
    void RunTween(string name, Action test)
    {
        twTotal++;
        try { test(); twPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunTween("TweenLinearMovesPosition", () => TweenTests.TestTweenLinearMovesPosition());
    RunTween("TweenQuadInSlowStart", () => TweenTests.TestTweenQuadInSlowStart());
    RunTween("TweenSineOutFastStart", () => TweenTests.TestTweenSineOutFastStart());
    RunTween("TweenCubicInOutMidpointHalf", () => TweenTests.TestTweenCubicInOutMidpointHalf());
    RunTween("TweenSequentialSteps", () => TweenTests.TestTweenSequentialSteps());
    RunTween("TweenParallelSteps", () => TweenTests.TestTweenParallelSteps());
    RunTween("TweenLoopsTwice", () => TweenTests.TestTweenLoopsTwice());
    RunTween("TweenSpeedScaleDoubles", () => TweenTests.TestTweenSpeedScaleDoubles());
    RunTween("TweenDrivesRotation", () => TweenTests.TestTweenDrivesRotation());
    RunTween("TweenDrivesOpacity", () => TweenTests.TestTweenDrivesOpacity());
    RunTween("TweenFactoryUpdatesAndCleans", () => TweenTests.TestTweenFactoryUpdatesAndCleans());
    Console.WriteLine($"  == TweenDepth: {twPass}/{twTotal} passed ==");
    int a2dPass = 0, a2dTotal = 0;
    void RunArea2D(string name, Action test)
    {
        a2dTotal++;
        try { test(); a2dPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunArea2D("Area2DCircleCircleOverlap", () => Area2DTests.TestCircleCircleOverlap());
    RunArea2D("Area2DCircleCircleSeparate", () => Area2DTests.TestCircleCircleSeparate());
    RunArea2D("Area2DEnterThenExit", () => Area2DTests.TestEnterThenExit());
    RunArea2D("Area2DRectRectOverlap", () => Area2DTests.TestRectRectOverlap());
    RunArea2D("Area2DRectRectSeparateOnY", () => Area2DTests.TestRectRectSeparateOnY());
    RunArea2D("Area2DCircleRectOverlap", () => Area2DTests.TestCircleRectOverlap());
    RunArea2D("Area2DCapsuleCircleOverlap", () => Area2DTests.TestCapsuleCircleOverlap());
    RunArea2D("Area2DShapeOffset", () => Area2DTests.TestShapeOffset());
    RunArea2D("Area2DMonitorableSymmetry", () => Area2DTests.TestMonitorableSymmetry());
    RunArea2D("Area2DMonitoringDisabled", () => Area2DTests.TestMonitoringDisabled());
    RunArea2D("Area2DNoShapeSkipped", () => Area2DTests.TestNoShapeSkipped());
    RunArea2D("Area2DMultipleAreas", () => Area2DTests.TestMultipleAreas());
    Console.WriteLine($"  == Area2DDepth: {a2dPass}/{a2dTotal} passed ==");
    int kb2dPass = 0, kb2dTotal = 0;
    void RunKinematic(string name, Action test)
    {
        kb2dTotal++;
        try { test(); kb2dPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunKinematic("KB2DFreeMove", () => KinematicBody2DTests.TestFreeMove());
    RunKinematic("KB2DLandsOnFloor", () => KinematicBody2DTests.TestLandsOnFloor());
    RunKinematic("KB2DHitsWall", () => KinematicBody2DTests.TestHitsWall());
    RunKinematic("KB2DHitsCeiling", () => KinematicBody2DTests.TestHitsCeiling());
    RunKinematic("KB2DSlideAlongWall", () => KinematicBody2DTests.TestSlideAlongWall());
    RunKinematic("KB2DCollisionMaskFilter", () => KinematicBody2DTests.TestCollisionMaskFilter());
    RunKinematic("KB2DCollisionMaskHit", () => KinematicBody2DTests.TestCollisionMaskHit());
    RunKinematic("KB2DNoShapePassesThrough", () => KinematicBody2DTests.TestNoShapePassesThrough());
    RunKinematic("KB2DFloorNormalDefault", () => KinematicBody2DTests.TestFloorNormalDefault());
    Console.WriteLine($"  == KinematicBody2DDepth: {kb2dPass}/{kb2dTotal} passed ==");
    int tmPass = 0, tmTotal = 0;
    void RunTileMap(string name, Action test)
    {
        tmTotal++;
        try { test(); tmPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunTileMap("TileMapSetGetCell", () => TileMapTests.TestSetGetCell());
    RunTileMap("TileMapEraseCell", () => TileMapTests.TestEraseCell());
    RunTileMap("TileMapSetCellNegativeErases", () => TileMapTests.TestSetCellNegativeErases());
    RunTileMap("TileMapMultipleLayersIndependent", () => TileMapTests.TestMultipleLayersIndependent());
    RunTileMap("TileMapClearLayer", () => TileMapTests.TestClearLayer());
    RunTileMap("TileMapGetUsedCells", () => TileMapTests.TestGetUsedCells());
    RunTileMap("TileMapInvalidLayerBounds", () => TileMapTests.TestInvalidLayerBounds());
    RunTileMap("TileMapMapToLocal", () => TileMapTests.TestMapToLocal());
    RunTileMap("TileMapLocalToMapFloor", () => TileMapTests.TestLocalToMapFloor());
    RunTileMap("TileMapWorldToMapWithTranslation", () => TileMapTests.TestWorldToMapWithTranslation());
    RunTileMap("TileMapMapToWorldWithTranslation", () => TileMapTests.TestMapToWorldWithTranslation());
    RunTileMap("TileMapCustomCellSize", () => TileMapTests.TestCustomCellSize());
    RunTileMap("TileSetAddGet", () => TileMapTests.TestTileSetAddGet());
    RunTileMap("TileSetRemove", () => TileMapTests.TestTileSetRemove());
    RunTileMap("TileCustomData", () => TileMapTests.TestTileCustomData());
    RunTileMap("Vector2IEquality", () => TileMapTests.TestVector2IEquality());
    Console.WriteLine($"  == TileMapDepth: {tmPass}/{tmTotal} passed ==");
    int rb2dPass = 0, rb2dTotal = 0;
    void RunRigidBody2D(string name, Action test)
    {
        rb2dTotal++;
        try { test(); rb2dPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunRigidBody2D("RigidBody2DGravityIntegration", () => RigidBody2DTests.TestGravityIntegration());
    RunRigidBody2D("RigidBody2DGravityScaleZero", () => RigidBody2DTests.TestGravityScaleZero());
    RunRigidBody2D("RigidBody2DApplyImpulse", () => RigidBody2DTests.TestApplyImpulse());
    RunRigidBody2D("RigidBody2DApplyForce", () => RigidBody2DTests.TestApplyForce());
    RunRigidBody2D("RigidBody2DWeight", () => RigidBody2DTests.TestWeight());
    RunRigidBody2D("RigidBody2DLandInelastic", () => RigidBody2DTests.TestLandInelastic());
    RunRigidBody2D("RigidBody2DBounceElastic", () => RigidBody2DTests.TestBounceElastic());
    RunRigidBody2D("RigidBody2DFrictionTangent", () => RigidBody2DTests.TestFrictionTangent());
    RunRigidBody2D("RigidBody2DSleepOnLowVelocity", () => RigidBody2DTests.TestSleepOnLowVelocity());
    RunRigidBody2D("RigidBody2DNoSleepWhenDisabled", () => RigidBody2DTests.TestNoSleepWhenDisabled());
    RunRigidBody2D("RigidBody2DWorldFreeFall", () => RigidBody2DTests.TestWorldFreeFall());
    RunRigidBody2D("RigidBody2DWorldBounce", () => RigidBody2DTests.TestWorldBounce());
    RunRigidBody2D("RigidBody2DWorldRestsOnFloor", () => RigidBody2DTests.TestWorldRestsOnFloor());
    RunRigidBody2D("RigidBody2DCollisionMaskFilter", () => RigidBody2DTests.TestCollisionMaskFilter());
    RunRigidBody2D("RigidBody2DSleepingSkipsStep", () => RigidBody2DTests.TestSleepingSkipsStep());
    Console.WriteLine($"  == RigidBody2DDepth: {rb2dPass}/{rb2dTotal} passed ==");
    int rbRotPass = 0, rbRotTotal = 0;
    void RunRigidBody2DRotation(string name, Action test)
    {
        rbRotTotal++;
        try { test(); rbRotPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunRigidBody2DRotation("RigidBody2DRotationDiscInertia", () => RigidBody2DRotationTests.TestDiscInertia());
    RunRigidBody2DRotation("RigidBody2DRotationRectInertia", () => RigidBody2DRotationTests.TestRectInertia());
    RunRigidBody2DRotation("RigidBody2DRotationInertiaOverride", () => RigidBody2DRotationTests.TestInertiaOverride());
    RunRigidBody2DRotation("RigidBody2DRotationApplyAngularImpulse", () => RigidBody2DRotationTests.TestApplyAngularImpulse());
    RunRigidBody2DRotation("RigidBody2DRotationAngularImpulseAccumulates", () => RigidBody2DRotationTests.TestAngularImpulseAccumulates());
    RunRigidBody2DRotation("RigidBody2DRotationApplyTorque", () => RigidBody2DRotationTests.TestApplyTorque());
    RunRigidBody2DRotation("RigidBody2DRotationTorqueAccumulates", () => RigidBody2DRotationTests.TestTorqueAccumulates());
    RunRigidBody2DRotation("RigidBody2DRotationIntegrate", () => RigidBody2DRotationTests.TestIntegrateRotation());
    RunRigidBody2DRotation("RigidBody2DRotationTwoSteps", () => RigidBody2DRotationTests.TestRotationTwoSteps());
    RunRigidBody2DRotation("RigidBody2DRotationEccentricImpulse", () => RigidBody2DRotationTests.TestEccentricImpulse());
    RunRigidBody2DRotation("RigidBody2DRotationEccentricDirection", () => RigidBody2DRotationTests.TestEccentricImpulseDirection());
    RunRigidBody2DRotation("RigidBody2DRotationEccentricNoTorque", () => RigidBody2DRotationTests.TestEccentricImpulseNoTorque());
    RunRigidBody2DRotation("RigidBody2DRotationNoShapeInfiniteInertia", () => RigidBody2DRotationTests.TestNoShapeInfiniteInertia());
    RunRigidBody2DRotation("RigidBody2DRotationOverrideZeroFallback", () => RigidBody2DRotationTests.TestInertiaOverrideZeroFallsBack());
    RunRigidBody2DRotation("RigidBody2DRotationWorldStep", () => RigidBody2DRotationTests.TestWorldStepRotates());
    RunRigidBody2DRotation("RigidBody2DRotationImpulseThenStep", () => RigidBody2DRotationTests.TestApplyAngularImpulseThenStep());
    RunRigidBody2DRotation("RigidBody2DRotationAngularSleep", () => RigidBody2DRotationTests.TestAngularSleep());
    RunRigidBody2DRotation("RigidBody2DRotationNoSleepSpinning", () => RigidBody2DRotationTests.TestNoSleepWhenSpinning());
    RunRigidBody2DRotation("RigidBody2DRotationWakeUp", () => RigidBody2DRotationTests.TestWakeUpRestoresRotation());
    Console.WriteLine($"  == RigidBody2DRotationDepth: {rbRotPass}/{rbRotTotal} passed ==");
    int clipPass = 0, clipTotal = 0;
    void RunCanvasItemClip(string name, Action test)
    {
        clipTotal++;
        try { test(); clipPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunCanvasItemClip("ClipDefaultModeDisabled", () => CanvasItemClipTests.TestDefaultClipModeDisabled());
    RunCanvasItemClip("ClipSetMode", () => CanvasItemClipTests.TestSetClipMode());
    RunCanvasItemClip("ClipRectLocalAccess", () => CanvasItemClipTests.TestClipRectLocal());
    RunCanvasItemClip("ClipGlobalRectNoTransform", () => CanvasItemClipTests.TestGlobalClipRectNoTransform());
    RunCanvasItemClip("ClipGlobalRectWithTranslation", () => CanvasItemClipTests.TestGlobalClipRectWithTranslation());
    RunCanvasItemClip("ClipGlobalRectWithScale", () => CanvasItemClipTests.TestGlobalClipRectWithScale());
    RunCanvasItemClip("ClipRectFallbackToVisibilityRect", () => CanvasItemClipTests.TestClipRectFallbackToVisibilityRect());
    RunCanvasItemClip("ClipRectDefaultFallback", () => CanvasItemClipTests.TestClipRectDefaultFallback());
    RunCanvasItemClip("ClipNotClippedWhenInside", () => CanvasItemClipTests.TestNotClippedWhenInside());
    RunCanvasItemClip("ClipClippedWhenOutside", () => CanvasItemClipTests.TestClippedWhenOutside());
    RunCanvasItemClip("ClipMultipleAncestorsInside", () => CanvasItemClipTests.TestMultipleAncestorClipping());
    RunCanvasItemClip("ClipOneAncestorEnough", () => CanvasItemClipTests.TestOneAncestorClipsEnough());
    RunCanvasItemClip("ClipEffectiveRectNoClip", () => CanvasItemClipTests.TestEffectiveRectNoClip());
    RunCanvasItemClip("ClipEffectiveRectPartial", () => CanvasItemClipTests.TestEffectiveRectPartialIntersect());
    RunCanvasItemClip("ClipEffectiveRectNoIntersect", () => CanvasItemClipTests.TestEffectiveRectNoIntersect());
    RunCanvasItemClip("ClipRectsIntersectHelper", () => CanvasItemClipTests.TestRectsIntersectHelper());
    RunCanvasItemClip("ClipClearClipData", () => CanvasItemClipTests.TestClearClipData());
    RunCanvasItemClip("ClipModeAndDrawStillClips", () => CanvasItemClipTests.TestClipModeAndDrawStillClips());
    RunCanvasItemClip("ClipVisibilityRectAccess", () => CanvasItemClipTests.TestVisibilityRectLocalAccess());
    Console.WriteLine($"  == CanvasItemClipDepth: {clipPass}/{clipTotal} passed ==");
    int aspPass = 0, aspTotal = 0;
    void RunAnimatedSpriteEditor(string name, Action test)
    {
        aspTotal++;
        try { test(); aspPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunAnimatedSpriteEditor("SpriteFramesAddAnimation", () => AnimatedSpriteEditorTests.TestAddAnimationAndListNames());
    RunAnimatedSpriteEditor("SpriteFramesRemoveAnimation", () => AnimatedSpriteEditorTests.TestRemoveAnimation());
    RunAnimatedSpriteEditor("SpriteFramesSetGetFps", () => AnimatedSpriteEditorTests.TestSetAndGetFps());
    RunAnimatedSpriteEditor("SpriteFramesSetGetLoop", () => AnimatedSpriteEditorTests.TestSetAndGetLoop());
    RunAnimatedSpriteEditor("SpriteFramesGetFrameCount", () => AnimatedSpriteEditorTests.TestGetFrameCount());
    RunAnimatedSpriteEditor("SpriteFramesInsertFrame", () => AnimatedSpriteEditorTests.TestInsertFrameAtPosition());
    RunAnimatedSpriteEditor("SpriteFramesInsertFrameClamp", () => AnimatedSpriteEditorTests.TestInsertFrameClampIndex());
    RunAnimatedSpriteEditor("SpriteFramesRemoveFrame", () => AnimatedSpriteEditorTests.TestRemoveFrame());
    RunAnimatedSpriteEditor("SpriteFramesSetGetRegion", () => AnimatedSpriteEditorTests.TestSetGetFrameRegion());
    RunAnimatedSpriteEditor("SpriteFramesClear", () => AnimatedSpriteEditorTests.TestClearAllAnimations());
    RunAnimatedSpriteEditor("AnimSpriteStateMachine", () => AnimatedSpriteEditorTests.TestAnimSpriteStateMachine());
    RunAnimatedSpriteEditor("AnimSpriteFrameAdvancement", () => AnimatedSpriteEditorTests.TestAnimSpriteFrameAdvancement());
    RunAnimatedSpriteEditor("AnimSpriteLoopWrap", () => AnimatedSpriteEditorTests.TestAnimSpriteLoopWrap());
    RunAnimatedSpriteEditor("AnimSpriteNonLoopEnd", () => AnimatedSpriteEditorTests.TestAnimSpriteNonLoopEnd());
    RunAnimatedSpriteEditor("AnimSpriteSpeedScale", () => AnimatedSpriteEditorTests.TestAnimSpriteSpeedScale());
    RunAnimatedSpriteEditor("AnimSpriteSpeedScaleZero", () => AnimatedSpriteEditorTests.TestAnimSpriteSpeedScaleZero());
    RunAnimatedSpriteEditor("AnimSpriteSwitchAnimation", () => AnimatedSpriteEditorTests.TestAnimSpriteSwitchAnimation());
    RunAnimatedSpriteEditor("AnimSpritePlayUnknownThrows", () => AnimatedSpriteEditorTests.TestAnimSpritePlayUnknownThrows());
    RunAnimatedSpriteEditor("AnimSpritePauseStopsAdvance", () => AnimatedSpriteEditorTests.TestAnimSpritePauseStopsFrameAdvance());
    Console.WriteLine($"  == AnimatedSpriteEditorDepth: {aspPass}/{aspTotal} passed ==");
    int twlcPass = 0, twlcTotal = 0;
    void RunTweenLifecycle(string name, Action test)
    {
        twlcTotal++;
        try { test(); twlcPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunTweenLifecycle("TweenDelay", () => TweenLifecycleTests.TestDelay());
    RunTweenLifecycle("TweenDelayZero", () => TweenLifecycleTests.TestDelayZero());
    RunTweenLifecycle("TweenInterval", () => TweenLifecycleTests.TestInterval());
    RunTweenLifecycle("TweenCallback", () => TweenLifecycleTests.TestCallback());
    RunTweenLifecycle("TweenCallbackOrder", () => TweenLifecycleTests.TestCallbackOrder());
    RunTweenLifecycle("TweenFinishedEvent", () => TweenLifecycleTests.TestFinishedEvent());
    RunTweenLifecycle("TweenFinishedOnlyOnFinalEnd", () => TweenLifecycleTests.TestFinishedOnlyOnFinalEnd());
    RunTweenLifecycle("TweenSetParallel", () => TweenLifecycleTests.TestSetParallel());
    RunTweenLifecycle("TweenChain", () => TweenLifecycleTests.TestChain());
    RunTweenLifecycle("TweenKill", () => TweenLifecycleTests.TestKill());
    RunTweenLifecycle("TweenFactoryCleanup", () => TweenLifecycleTests.TestFactoryCleanup());
    RunTweenLifecycle("TweenIntervalAsGroupAnchor", () => TweenLifecycleTests.TestIntervalAsGroupAnchor());
    Console.WriteLine($"  == TweenLifecycleDepth: {twlcPass}/{twlcTotal} passed ==");
    int cpuPpPass = 0, cpuPpTotal = 0;
    void RunCPUParticles(string name, Action test)
    {
        cpuPpTotal++;
        try { test(); cpuPpPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunCPUParticles("CpuParticlesContinuousEmission", () => CPUParticles2DTests.TestContinuousEmission());
    RunCPUParticles("CpuParticlesContinuousOneSecond", () => CPUParticles2DTests.TestContinuousEmissionOneSecond());
    RunCPUParticles("CpuParticlesInitialVelocity", () => CPUParticles2DTests.TestInitialVelocity());
    RunCPUParticles("CpuParticlesPositionAdvance", () => CPUParticles2DTests.TestPositionAdvanceNoGravity());
    RunCPUParticles("CpuParticlesGravity", () => CPUParticles2DTests.TestGravityIntegration());
    RunCPUParticles("CpuParticlesLifetime", () => CPUParticles2DTests.TestLifetimeRecycle());
    RunCPUParticles("CpuParticlesEmittingFalse", () => CPUParticles2DTests.TestEmittingFalseStopsNew());
    RunCPUParticles("CpuParticlesOneShotBurst", () => CPUParticles2DTests.TestOneShotBurst());
    RunCPUParticles("CpuParticlesOneShotAllDie", () => CPUParticles2DTests.TestOneShotAllDie());
    RunCPUParticles("CpuParticlesHalfExplosiveness", () => CPUParticles2DTests.TestOneShotHalfExplosiveness());
    RunCPUParticles("CpuParticlesColorRamp", () => CPUParticles2DTests.TestColorRampGradient());
    RunCPUParticles("CpuParticlesColorFixed", () => CPUParticles2DTests.TestColorFixed());
    RunCPUParticles("CpuParticlesRestart", () => CPUParticles2DTests.TestRestart());
    RunCPUParticles("CpuParticlesRampSingleStop", () => CPUParticles2DTests.TestColorRampSingleStop());
    RunCPUParticles("CpuParticlesRampEmpty", () => CPUParticles2DTests.TestColorRampEmpty());
    RunCPUParticles("CpuParticlesContinuousOverlap", () => CPUParticles2DTests.TestContinuousOverlap());
    Console.WriteLine($"  == CPUParticles2DDepth: {cpuPpPass}/{cpuPpTotal} passed ==");
    int autoPass = 0, autoTotal = 0;
    void RunAutotile(string name, Action test)
    {
        autoTotal++;
        try { test(); autoPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunAutotile("TileMapAutotileNeighborBitValues", () => TileMapAutotileTests.TestNeighborBitValues());
    RunAutotile("TileMapAutotileNeighborFlagsCombine", () => TileMapAutotileTests.TestNeighborFlagsCombine());
    RunAutotile("TileMapAutotilePickNoCandidates", () => TileMapAutotileTests.TestPickNoCandidates());
    RunAutotile("TileMapAutotilePickWildcard", () => TileMapAutotileTests.TestPickWildcard());
    RunAutotile("TileMapAutotilePickRequiresSubset", () => TileMapAutotileTests.TestPickRequiresTemplateSubset());
    RunAutotile("TileMapAutotilePickMostSpecific", () => TileMapAutotileTests.TestPickMostSpecific());
    RunAutotile("TileMapAutotilePickFallsBack", () => TileMapAutotileTests.TestPickFallsBackToLessSpecific());
    RunAutotile("TileMapAutotileMaskIsolated", () => TileMapAutotileTests.TestMaskIsolated());
    RunAutotile("TileMapAutotileMaskFourCardinal", () => TileMapAutotileTests.TestMaskFourCardinal());
    RunAutotile("TileMapAutotileMaskDiagonal", () => TileMapAutotileTests.TestMaskDiagonal());
    RunAutotile("TileMapAutotileMaskNonTerrain", () => TileMapAutotileTests.TestMaskNonTerrainNeighbor());
    RunAutotile("TileMapAutotileMaskAllNeighbors", () => TileMapAutotileTests.TestMaskAllNeighbors());
    RunAutotile("TileMapAutotileFillNoRules", () => TileMapAutotileTests.TestFillNoRules());
    RunAutotile("TileMapAutotileFillSingleCell", () => TileMapAutotileTests.TestFillSingleCell());
    RunAutotile("TileMapAutotileFillHorizontalStrip", () => TileMapAutotileTests.TestFillHorizontalStrip());
    RunAutotile("TileMapAutotileFillCountMatches", () => TileMapAutotileTests.TestFillCountMatches());
    RunAutotile("TileMapAutotilePickNoWrite", () => TileMapAutotileTests.TestPickAutoTileNoWrite());
    RunAutotile("TileMapAutotileRulesRoundTrip", () => TileMapAutotileTests.TestRulesRoundTrip());
    RunAutotile("TileMapAutotileCandidateCount", () => TileMapAutotileTests.TestRulesCandidateCount());
    RunAutotile("TileMapAutotileFillQuadBlock", () => TileMapAutotileTests.TestFillQuadBlock());
    Console.WriteLine($"  == TileMapAutotileDepth: {autoPass}/{autoTotal} passed ==");
    int shaderMatPass = 0, shaderMatTotal = 0;
    void RunShaderMaterial(string name, Action test)
    {
        shaderMatTotal++;
        try { test(); shaderMatPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunShaderMaterial("ShaderMaterialDeclareUniform", () => ShaderMaterialTests.TestShaderDeclareUniform());
    RunShaderMaterial("ShaderMaterialDefaultMode", () => ShaderMaterialTests.TestShaderDefaultMode());
    RunShaderMaterial("ShaderMaterialDeclareChain", () => ShaderMaterialTests.TestShaderDeclareChain());
    RunShaderMaterial("ShaderMaterialInitialState", () => ShaderMaterialTests.TestMaterialInitialState());
    RunShaderMaterial("ShaderMaterialSetMarksDirty", () => ShaderMaterialTests.TestSetParameterMarksDirty());
    RunShaderMaterial("ShaderMaterialGetObject", () => ShaderMaterialTests.TestGetParameterObject());
    RunShaderMaterial("ShaderMaterialGetMissingNull", () => ShaderMaterialTests.TestGetMissingReturnsNull());
    RunShaderMaterial("ShaderMaterialGetTyped", () => ShaderMaterialTests.TestGetParameterTyped());
    RunShaderMaterial("ShaderMaterialColorParam", () => ShaderMaterialTests.TestColorParameter());
    RunShaderMaterial("ShaderMaterialHasParam", () => ShaderMaterialTests.TestHasParameter());
    RunShaderMaterial("ShaderMaterialRemoveParam", () => ShaderMaterialTests.TestRemoveParameter());
    RunShaderMaterial("ShaderMaterialClearParams", () => ShaderMaterialTests.TestClearParameters());
    RunShaderMaterial("ShaderMaterialTimeAdvance", () => ShaderMaterialTests.TestTimeAdvance());
    RunShaderMaterial("ShaderMaterialUpdateFrameDirty", () => ShaderMaterialTests.TestUpdateFrameMarksDirty());
    RunShaderMaterial("ShaderMaterialChangeShaderDirty", () => ShaderMaterialTests.TestChangeShaderMarksDirty());
    RunShaderMaterial("ShaderMaterialManualDirty", () => ShaderMaterialTests.TestManualDirty());
    RunShaderMaterial("ShaderMaterialNullThrows", () => ShaderMaterialTests.TestNullParameterThrows());
    Console.WriteLine($"  == ShaderMaterialDepth: {shaderMatPass}/{shaderMatTotal} passed ==");
    int navPass = 0, navTotal = 0;
    void RunNavigation(string name, Action test)
    {
        navTotal++;
        try { test(); navPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunNavigation("Navigation2DConstruction", () => Navigation2DTests.TestConstruction());
    RunNavigation("Navigation2DInBounds", () => Navigation2DTests.TestInBounds());
    RunNavigation("Navigation2DSolidToggle", () => Navigation2DTests.TestSolidToggle());
    RunNavigation("Navigation2DSolidOutOfBounds", () => Navigation2DTests.TestSolidOutOfBounds());
    RunNavigation("Navigation2DStraightPath", () => Navigation2DTests.TestStraightPath());
    RunNavigation("Navigation2DSameStartEnd", () => Navigation2DTests.TestSameStartEnd());
    RunNavigation("Navigation2DPathAroundWall", () => Navigation2DTests.TestPathAroundWall());
    RunNavigation("Navigation2DNoPath", () => Navigation2DTests.TestNoPath());
    RunNavigation("Navigation2DSolidStartOrEnd", () => Navigation2DTests.TestSolidStartOrEnd());
    RunNavigation("Navigation2DOutOfBoundsPath", () => Navigation2DTests.TestOutOfBoundsPath());
    RunNavigation("Navigation2DDiagonalCornerBlocked", () => Navigation2DTests.TestDiagonalCornerBlocked());
    RunNavigation("Navigation2DDiagonalAllowed", () => Navigation2DTests.TestDiagonalAllowed());
    RunNavigation("Navigation2DPathLength", () => Navigation2DTests.TestPathLength());
    RunNavigation("Navigation2DSimplePathWorld", () => Navigation2DTests.TestSimplePathWorldCoords());
    RunNavigation("Navigation2DSimplePathOffset", () => Navigation2DTests.TestSimplePathOffset());
    RunNavigation("Navigation2DBatchSolid", () => Navigation2DTests.TestBatchSolid());
    RunNavigation("Navigation2DMazePathValid", () => Navigation2DTests.TestMazePathValid());
    RunNavigation("Navigation2DInvalidConstruction", () => Navigation2DTests.TestInvalidConstruction());
    Console.WriteLine($"  == Navigation2DDepth: {navPass}/{navTotal} passed ==");

    // ===== GodotResourceLoader 深度验证（Godot 3 资源加载：引用计数/缓存/依赖/分帧线程加载）=====
    int godotResPass = 0, godotResTotal = 0;
    void RunGodotResourceLoader(string name, Action test)
    {
        godotResTotal++;
        try { test(); godotResPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunGodotResourceLoader("GodotResourceLoaderResourceRefCount", () => GodotResourceLoaderTests.TestResourceRefCount());
    RunGodotResourceLoader("GodotResourceLoaderResourceType", () => GodotResourceLoaderTests.TestResourceType());
    RunGodotResourceLoader("GodotResourceLoaderLoadCreatesAndCaches", () => GodotResourceLoaderTests.TestLoadCreatesAndCaches());
    RunGodotResourceLoader("GodotResourceLoaderLoadCacheHit", () => GodotResourceLoaderTests.TestLoadCacheHit());
    RunGodotResourceLoader("GodotResourceLoaderLoadMissing", () => GodotResourceLoaderTests.TestLoadMissing());
    RunGodotResourceLoader("GodotResourceLoaderUnload", () => GodotResourceLoaderTests.TestUnload());
    RunGodotResourceLoader("GodotResourceLoaderUnloadNotCached", () => GodotResourceLoaderTests.TestUnloadNotCached());
    RunGodotResourceLoader("GodotResourceLoaderClearCache", () => GodotResourceLoaderTests.TestClearCache());
    RunGodotResourceLoader("GodotResourceLoaderIgnoreCache", () => GodotResourceLoaderTests.TestIgnoreCache());
    RunGodotResourceLoader("GodotResourceLoaderDependencyLoad", () => GodotResourceLoaderTests.TestDependencyLoad());
    RunGodotResourceLoader("GodotResourceLoaderThreadedLoadFlow", () => GodotResourceLoaderTests.TestThreadedLoadFlow());
    RunGodotResourceLoader("GodotResourceLoaderThreadedLoadFailed", () => GodotResourceLoaderTests.TestThreadedLoadFailed());
    RunGodotResourceLoader("GodotResourceLoaderThreadedLoadCaches", () => GodotResourceLoaderTests.TestThreadedLoadCaches());
    RunGodotResourceLoader("GodotResourceLoaderThreadedRequestDedup", () => GodotResourceLoaderTests.TestThreadedRequestDedup());
    RunGodotResourceLoader("GodotResourceLoaderThreadedGetStatusUnknown", () => GodotResourceLoaderTests.TestThreadedGetStatusUnknown());
    RunGodotResourceLoader("GodotResourceLoaderClearThreadedTasks", () => GodotResourceLoaderTests.TestClearThreadedTasks());
    RunGodotResourceLoader("GodotResourceLoaderCacheCount", () => GodotResourceLoaderTests.TestCacheCount());
    Console.WriteLine($"  == GodotResourceLoaderDepth: {godotResPass}/{godotResTotal} passed ==");

    // ===== Timer 深度验证（Godot 3 Timer：wait_time/one_shot/autostart/time_left/time_scale）=====
    int timerPass = 0, timerTotal = 0;
    void RunTimer(string name, Action test)
    {
        timerTotal++;
        try { test(); timerPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunTimer("TimerDefaults", () => TimerDepthTests.TestDefaults());
    RunTimer("TimerOneShot", () => TimerDepthTests.TestOneShot());
    RunTimer("TimerLoop", () => TimerDepthTests.TestLoop());
    RunTimer("TimerStop", () => TimerDepthTests.TestStop());
    RunTimer("TimerWaitTimeResetWhileRunning", () => TimerDepthTests.TestWaitTimeResetWhileRunning());
    RunTimer("TimerAutostart", () => TimerDepthTests.TestAutostart());
    RunTimer("TimerNoAutostart", () => TimerDepthTests.TestNoAutostart());
    RunTimer("TimerTimeScaleDouble", () => TimerDepthTests.TestTimeScaleDouble());
    RunTimer("TimerTimeScaleZero", () => TimerDepthTests.TestTimeScaleZero());
    RunTimer("TimerTimeScaleHalf", () => TimerDepthTests.TestTimeScaleHalf());
    RunTimer("TimerLoopNoDrift", () => TimerDepthTests.TestLoopNoDrift());
    RunTimer("TimerTimeLeftZeroWhenStopped", () => TimerDepthTests.TestTimeLeftZeroWhenStopped());
    RunTimer("TimerTimeoutCount", () => TimerDepthTests.TestTimeoutCount());
    Console.WriteLine($"  == TimerDepth: {timerPass}/{timerTotal} passed ==");

    // ===== ResourceSaver 深度验证（Godot 3 ResourceSaver：自定义资源序列化 + 持久化 round-trip）=====
    int saverPass = 0, saverTotal = 0;
    void RunResourceSaver(string name, Action test)
    {
        saverTotal++;
        try { test(); saverPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunResourceSaver("ResourceSaverTextSerializerRoundTrip", () => ResourceSaverTests.TestTextSerializerRoundTrip());
    RunResourceSaver("ResourceSaverTextSerializerEscaping", () => ResourceSaverTests.TestTextSerializerEscaping());
    RunResourceSaver("ResourceSaverTextSerializerEmpty", () => ResourceSaverTests.TestTextSerializerEmpty());
    RunResourceSaver("ResourceSaverRegisterSerializer", () => ResourceSaverTests.TestRegisterSerializer());
    RunResourceSaver("ResourceSaverSaveWritesAndSetsPath", () => ResourceSaverTests.TestSaveWritesAndSetsPath());
    RunResourceSaver("ResourceSaverSaveRoundTripViaLoader", () => ResourceSaverTests.TestSaveRoundTripViaLoader());
    RunResourceSaver("ResourceSaverSaveUnregisteredTypeThrows", () => ResourceSaverTests.TestSaveUnregisteredTypeThrows());
    RunResourceSaver("ResourceSaverSaveNullArguments", () => ResourceSaverTests.TestSaveNullArguments());
    RunResourceSaver("ResourceSaverSerializerWrongTypeThrows", () => ResourceSaverTests.TestSerializerWrongTypeThrows());
    RunResourceSaver("ResourceSaverMultipleSavesOverwrite", () => ResourceSaverTests.TestMultipleSavesOverwrite());
    RunResourceSaver("ResourceSaverRoundTripStable", () => ResourceSaverTests.TestRoundTripStable());
    RunResourceSaver("ResourceSaverSaveMultipleResources", () => ResourceSaverTests.TestSaveMultipleResources());
    Console.WriteLine($"  == ResourceSaverDepth: {saverPass}/{saverTotal} passed ==");

    // ===== AnimationPlayer 补充深度验证（Stop 复位/Slerp/Scale/重启/多动画管理）=====
    int apPass = 0, apTotal = 0;
    void RunAnimationPlayerExtra(string name, Action test)
    {
        apTotal++;
        try { test(); apPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunAnimationPlayerExtra("AnimExtraDefaultState", () => AnimationPlayerExtraDepthTests.TestDefaultState());
    RunAnimationPlayerExtra("AnimExtraAddHasGet", () => AnimationPlayerExtraDepthTests.TestAddHasGet());
    RunAnimationPlayerExtra("AnimExtraAddOverwrites", () => AnimationPlayerExtraDepthTests.TestAddOverwrites());
    RunAnimationPlayerExtra("AnimExtraPlayUnknownThrows", () => AnimationPlayerExtraDepthTests.TestPlayUnknownThrows());
    RunAnimationPlayerExtra("AnimExtraPlayStarts", () => AnimationPlayerExtraDepthTests.TestPlayStarts());
    RunAnimationPlayerExtra("AnimExtraPlayAdvances", () => AnimationPlayerExtraDepthTests.TestPlayAdvances());
    RunAnimationPlayerExtra("AnimExtraStopResets", () => AnimationPlayerExtraDepthTests.TestStopResets());
    RunAnimationPlayerExtra("AnimExtraPlayRestarts", () => AnimationPlayerExtraDepthTests.TestPlayRestarts());
    RunAnimationPlayerExtra("AnimExtraRotationTrackSlerp", () => AnimationPlayerExtraDepthTests.TestRotationTrackSlerp());
    RunAnimationPlayerExtra("AnimExtraScaleTrackDrivesTransform", () => AnimationPlayerExtraDepthTests.TestScaleTrackDrivesTransform());
    RunAnimationPlayerExtra("AnimExtraTranslationTrackDrivesTransform", () => AnimationPlayerExtraDepthTests.TestTranslationTrackDrivesTransform());
    RunAnimationPlayerExtra("AnimExtraStopClearsQueue", () => AnimationPlayerExtraDepthTests.TestStopClearsQueue());
    RunAnimationPlayerExtra("AnimExtraSpeedScaleReset", () => AnimationPlayerExtraDepthTests.TestSpeedScaleReset());
    RunAnimationPlayerExtra("AnimExtraFinishStopsPlaying", () => AnimationPlayerExtraDepthTests.TestFinishStopsPlaying());
    RunAnimationPlayerExtra("AnimExtraLoopStaysPlaying", () => AnimationPlayerExtraDepthTests.TestLoopStaysPlaying());
    Console.WriteLine($"  == AnimationPlayerExtraDepth: {apPass}/{apTotal} passed ==");

    // ===== Camera2D 视口/旋转投影 深度验证（世界↔屏幕含旋转/缩放/边界/round-trip）=====
    int cam2dPass = 0, cam2dTotal = 0;
    void RunCamera2DViewport(string name, Action test)
    {
        cam2dTotal++;
        try { test(); cam2dPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunCamera2DViewport("Camera2DIdentityCenter", () => Camera2DViewportDepthTests.TestIdentityCenter());
    RunCamera2DViewport("Camera2DTranslateProjection", () => Camera2DViewportDepthTests.TestTranslateProjection());
    RunCamera2DViewport("Camera2DZoomIn", () => Camera2DViewportDepthTests.TestZoomIn());
    RunCamera2DViewport("Camera2DRoundTripNoRotation", () => Camera2DViewportDepthTests.TestRoundTripNoRotation());
    RunCamera2DViewport("Camera2DRoundTripWithRotation", () => Camera2DViewportDepthTests.TestRoundTripWithRotation());
    RunCamera2DViewport("Camera2DRotation90MapsRightToUp", () => Camera2DViewportDepthTests.TestRotation90MapsRightToUp());
    RunCamera2DViewport("Camera2DRotationForwardToScreenRight", () => Camera2DViewportDepthTests.TestRotationForwardToScreenRight());
    RunCamera2DViewport("Camera2DVisibleBoundsNoLimit", () => Camera2DViewportDepthTests.TestVisibleBoundsNoLimit());
    RunCamera2DViewport("Camera2DVisibleBoundsZoom", () => Camera2DViewportDepthTests.TestVisibleBoundsZoom());
    RunCamera2DViewport("Camera2DLimitClampsCamera", () => Camera2DViewportDepthTests.TestLimitClampsCamera());
    RunCamera2DViewport("Camera2DHalfDimensionsUnaffectedByRotation", () => Camera2DViewportDepthTests.TestHalfDimensionsUnaffectedByRotation());
    RunCamera2DViewport("Camera2DProjectionLinearity", () => Camera2DViewportDepthTests.TestProjectionLinearity());
    Console.WriteLine($"  == Camera2DViewportDepth: {cam2dPass}/{cam2dTotal} passed ==");

    // ===== Camera2D 平滑跟随（position_smoothing）深度验证 =====
    int camSmoothPass = 0, camSmoothTotal = 0;
    void RunCamera2DSmoothing(string name, Action test)
    {
        camSmoothTotal++;
        try { test(); camSmoothPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunCamera2DSmoothing("Camera2DSmoothDefaults", () => Camera2DSmoothingDepthTests.TestDefaults());
    RunCamera2DSmoothing("Camera2DSmoothDisabledNoMove", () => Camera2DSmoothingDepthTests.TestDisabledNoMove());
    RunCamera2DSmoothing("Camera2DSmoothEnabledMoves", () => Camera2DSmoothingDepthTests.TestEnabledMoves());
    RunCamera2DSmoothing("Camera2DSmoothHighSpeedFastApproach", () => Camera2DSmoothingDepthTests.TestHighSpeedFastApproach());
    RunCamera2DSmoothing("Camera2DSmoothFrameRateIndependence", () => Camera2DSmoothingDepthTests.TestFrameRateIndependence());
    RunCamera2DSmoothing("Camera2DSmoothConvergesToTarget", () => Camera2DSmoothingDepthTests.TestConvergesToTarget());
    RunCamera2DSmoothing("Camera2DSmoothZeroSpeedNoMove", () => Camera2DSmoothingDepthTests.TestZeroSpeedNoMove());
    RunCamera2DSmoothing("Camera2DSmoothYAxisFollow", () => Camera2DSmoothingDepthTests.TestYAxisFollow());
    RunCamera2DSmoothing("Camera2DSmoothTargetChange", () => Camera2DSmoothingDepthTests.TestTargetChange());
    RunCamera2DSmoothing("Camera2DSmoothTargetRoundTrip", () => Camera2DSmoothingDepthTests.TestTargetRoundTrip());
    RunCamera2DSmoothing("Camera2DSmoothExponentialDecay", () => Camera2DSmoothingDepthTests.TestExponentialDecay());
    Console.WriteLine($"  == Camera2DSmoothingDepth: {camSmoothPass}/{camSmoothTotal} passed ==");

    // ===== Camera2D 拖拽边距（drag_margin）深度验证 =====
    int camDragPass = 0, camDragTotal = 0;
    void RunCamera2DDragMargin(string name, Action test)
    {
        camDragTotal++;
        try { test(); camDragPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunCamera2DDragMargin("Camera2DDragDefaults", () => Camera2DDragMarginTests.TestDefaults());
    RunCamera2DDragMargin("Camera2DDragInsideMarginNoMove", () => Camera2DDragMarginTests.TestTargetInsideMarginNoMove());
    RunCamera2DDragMargin("Camera2DDragOutsideClamped", () => Camera2DDragMarginTests.TestTargetOutsideMarginClamped());
    RunCamera2DDragMargin("Camera2DDragDisabledDirect", () => Camera2DDragMarginTests.TestDisabledFollowsTargetDirectly());
    RunCamera2DDragMargin("Camera2DDragVerticalClamp", () => Camera2DDragMarginTests.TestVerticalClamp());
    RunCamera2DDragMargin("Camera2DDragNegativeClamp", () => Camera2DDragMarginTests.TestNegativeDirectionClamp());
    RunCamera2DDragMargin("Camera2DDragWithSmoothing", () => Camera2DDragMarginTests.TestDragMarginWithSmoothing());
    RunCamera2DDragMargin("Camera2DDragFollowWhenOutside", () => Camera2DDragMarginTests.TestDragMarginFollowWhenOutside());
    RunCamera2DDragMargin("Camera2DDragZeroMargin", () => Camera2DDragMarginTests.TestZeroMarginImmediateFollow());
    RunCamera2DDragMargin("Camera2DDragNoMutate", () => Camera2DDragMarginTests.TestComputeDoesNotMutate());
    RunCamera2DDragMargin("Camera2DDragSymmetric", () => Camera2DDragMarginTests.TestSymmetricNoMove());
    RunCamera2DDragMargin("Camera2DDragToggle", () => Camera2DDragMarginTests.TestToggleEnabled());
    Console.WriteLine($"  == Camera2DDragMarginDepth: {camDragPass}/{camDragTotal} passed ==");
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
    // ── UE5.8 级渲染深度验证 (TAA/TSR/LumenGI) ──
    int renderPass = 0, renderTotal = 0;
    void RunRender(string name, Action test)
    {
        renderTotal++;
        try { test(); renderPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunRender("HaltonSequenceKnownValues", () => RenderTests.TestHaltonSequenceMatchesKnownValues());
    RunRender("YCoCgColorSpaceRoundTrip", () => RenderTests.TestYCoCgColorSpaceRoundTrips());
    RunRender("OctahedralParamRoundTrip", () => RenderTests.TestOctahedralParameterizationRoundTripsUnitVectors());
    RunRender("LumenSHUniformRadiance", () => RenderTests.TestLumenProbeSHReconstructsUniformRadiance());
    RunRender("DecalProjectorMapping", () => RenderTests.TestDecalProjectorComputesWorldToLocalMapping());
    RunRender("DecalLifecycleFadeExpire", () => RenderTests.TestDecalLifecycleFadesOutAndExpires());
    RunRender("DecalSorterBackToFront", () => RenderTests.TestDecalSorterOrdersFarToNearForBackToFront());
    RunRender("MaterialInstanceOverrideResolve", () => RenderTests.TestMaterialInstanceResolvesOverridesAndFallbacks());
    RunRender("MaterialAssetTextureFlags", () => RenderTests.TestMaterialAssetComputeTextureFlags());
    RunRender("NaniteQEMPlaneZeroError", () => RenderTests.TestNaniteQEMPlaneZeroError());
    RunRender("GGXBrdfAnalyticValues", () => RenderTests.TestGGXBrdfAnalyticValues());
    RunRender("VirtualGeometryLodByError", () => RenderTests.TestVirtualGeometryLodSelectorPicksLodByError());
    RunRender("ReSTIRTemporalReservoirRis", () => RenderTests.TestReSTIRTemporalReservoirRis());
    RunRender("ReSTIRLightTreeCdfSampling", () => RenderTests.TestReSTIRLightTreeCdfSampling());
    RunRender("ReSTIRReservoirWeightAndAcceptance", () => RenderTests.TestReSTIRReservoirWeightAndAcceptance());
    RunRender("HzbHierarchicalDepthPyramid", () => RenderTests.TestHzbHierarchicalDepthPyramid());
    RunRender("ExponentialHeightFog", () => RenderTests.TestExponentialHeightFogDensityAndTransmittance());
    RunRender("HenyeyGreensteinPhase", () => RenderTests.TestHenyeyGreensteinPhaseFunction());
    RunRender("GpuInstanceDataFromTransform", () => RenderTests.TestGpuInstanceDataFromTransform());
    RunRender("RayMarcherEscapeAndHit", () => RenderTests.TestRayMarcherEscapesScreenAndHitsWall());
    Console.WriteLine($"  == RenderingDepth: {renderPass}/{renderTotal} passed ==");

    // ── UE5.8 级音频 DSP + 网络序列化深度验证 ──
    int audioNetPass = 0, audioNetTotal = 0;
    void RunAudioNet(string name, Action test)
    {
        audioNetTotal++;
        try { test(); audioNetPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunAudioNet("AudioDspSineRmsLowPass", () => AudioNetworkTests.TestAudioDspSineRmsAndLowPass());
    RunAudioNet("NetworkSerializationRoundTrip", () => AudioNetworkTests.TestNetworkSerializationRoundTrip());
    Console.WriteLine($"  == AudioNetworkDepth: {audioNetPass}/{audioNetTotal} passed ==");

    // ── UE5.8 级 AI GOAP 规划深度验证 ──
    int aiPass = 0, aiTotal = 0;
    void RunAI(string name, Action test)
    {
        aiTotal++;
        try { test(); aiPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunAI("GoapWorldStateSemantics", () => AITests.TestGoapWorldStateSemantics());
    RunAI("GoapPlannerOrderedPlan", () => AITests.TestGoapPlannerGeneratesOrderedPlan());
    RunAI("SeekBehavior", () => AITests.TestSeekBehaviorDrivesTowardTarget());
    RunAI("ArrivalBehavior", () => AITests.TestArrivalBehaviorSlowsNearTarget());
    RunAI("SeparationBehavior", () => AITests.TestSeparationBehaviorPushesAwayFromNeighbors());
    RunAI("AlignmentBehavior", () => AITests.TestAlignmentBehaviorMatchesNeighborVelocity());
    RunAI("CohesionBehavior", () => AITests.TestCohesionBehaviorSeeksGroupCenter());
    RunAI("PathOptimizationCornersTortuosity", () => AITests.TestPathOptimizationCornersAndTortuosity());
    Console.WriteLine($"  == AIDepth: {aiPass}/{aiTotal} passed ==");

    // ── UE5.8 级 Enhanced Input 深度验证 ──
    int inputPass = 0, inputTotal = 0;
    void RunInput(string name, Action test)
    {
        inputTotal++;
        try { test(); inputPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunInput("DeadZoneInnerRemapsAndZeroes", () => InputTests.TestDeadZoneInnerRemapsAndZeroes());
    RunInput("DeadZoneRadialVector2", () => InputTests.TestDeadZoneRadialVector2());
    RunInput("ScaleModifierUniformAndPerAxis", () => InputTests.TestScaleModifierUniformAndPerAxis());
    RunInput("InvertModifierComponents", () => InputTests.TestInvertModifierComponents());
    RunInput("ClampModifierBounds", () => InputTests.TestClampModifierBounds());
    RunInput("CurveModifierGamma", () => InputTests.TestCurveModifierGamma());
    RunInput("StepModifierQuantizes", () => InputTests.TestStepModifierQuantizes());
    RunInput("SwizzleModifierReorders", () => InputTests.TestSwizzleModifierReorders());
    RunInput("SmoothModifierEMAConverges", () => InputTests.TestSmoothModifierEMAConverges());
    RunInput("ModifierPipelineChainsInOrder", () => InputTests.TestModifierPipelineChainsInOrder());
    RunInput("HoldModifierGatesByHoldTime", () => InputTests.TestHoldModifierGatesByHoldTime());
    RunInput("TapModifierShortPressOnly", () => InputTests.TestTapModifierShortPressOnly());
    RunInput("PressTriggerRequiresStartedState", () => InputTests.TestPressTriggerRequiresStartedState());
    RunInput("ReleaseTriggerNoPriorPress", () => InputTests.TestReleaseTriggerNoPriorPressReturnsNone());
    RunInput("TapTriggerNoPriorPressDoesNotFire", () => InputTests.TestTapTriggerNoPriorPressDoesNotFire());
    RunInput("SlowTapTriggerRequiresMinHoldTime", () => InputTests.TestSlowTapTriggerRequiresMinHoldTime());
    RunInput("TriggerStateEnumValues", () => InputTests.TestTriggerStateEnumValues());
    Console.WriteLine($"  == InputDepth: {inputPass}/{inputTotal} passed ==");

    // ── UE5.8 级 ECS 核心深度验证 ──
    int ecsPass = 0, ecsTotal = 0;
    void RunECS(string name, Action test)
    {
        ecsTotal++;
        try { test(); ecsPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunECS("SparseSetSwapRemovePreservesDense", () => ECSTests.TestSparseSetSwapRemovePreservesDense());
    RunECS("SparseSetVersionInvalidationRecycle", () => ECSTests.TestSparseSetVersionInvalidationAndRecycle());
    RunECS("SchedulerRespectsDependencyChain", () => ECSTests.TestSchedulerRespectsDependencyChain());
    RunECS("SchedulerNoDependencyRegistrationOrder", () => ECSTests.TestSchedulerNoDependencyKeepsRegistrationOrder());
    RunECS("SchedulerSkipsDisabledSystem", () => ECSTests.TestSchedulerSkipsDisabledSystem());
    RunECS("EntityManagerLifecycleComponentsEvents", () => ECSTests.TestEntityManagerLifecycleComponentsEvents());
    RunECS("EntityManagerQueryAllOfNoneOf", () => ECSTests.TestEntityManagerQueryAllOfNoneOf());
    RunECS("EntityManagerRemoveComponentMigratesArchetype", () => ECSTests.TestEntityManagerRemoveComponentMigratesArchetype());
    Console.WriteLine($"  == ECSDepth: {ecsPass}/{ecsTotal} passed ==");

    // ── UE5.8 级 CPU 粒子模拟深度验证 ──
    int vfxPass = 0, vfxTotal = 0;
    void RunVFX(string name, Action test)
    {
        vfxTotal++;
        try { test(); vfxPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunVFX("GravitySemiImplicitEuler", () => VFXTests.TestGravitySemiImplicitEuler());
    RunVFX("LifetimeExpiryRecycles", () => VFXTests.TestLifetimeExpiryRecycles());
    RunVFX("BurstEmissionDeterministicCount", () => VFXTests.TestBurstEmissionDeterministicCount());
    RunVFX("RateEmissionAccumulator", () => VFXTests.TestRateEmissionAccumulator());
    RunVFX("PoolSlotReuseAfterExpiry", () => VFXTests.TestPoolSlotReuseAfterExpiry());
    RunVFX("InitialVelocityWithGravity", () => VFXTests.TestInitialVelocityWithGravity());
    Console.WriteLine($"  == VFXDepth: {vfxPass}/{vfxTotal} passed ==");

    // ── UE5.8 级作业系统深度验证 ──
    int jobsPass = 0, jobsTotal = 0;
    void RunJobs(string name, Action test)
    {
        jobsTotal++;
        try { test(); jobsPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunJobs("ParallelForCoversAllIndicesWithRemainder", () => JobsTests.TestParallelForCoversAllIndicesWithRemainder());
    RunJobs("ScheduleDependencyOrdering", () => JobsTests.TestScheduleDependencyOrdering());
    RunJobs("CombineHandleWaitsAllPrerequisites", () => JobsTests.TestCombineHandleWaitsAllPrerequisites());
    RunJobs("SingleJobCompleteSemantics", () => JobsTests.TestSingleJobCompleteSemantics());
    RunJobs("FlushDrainsPendingJobs", () => JobsTests.TestFlushDrainsPendingJobs());
    RunJobs("ParallelForBatchSizeOneEachIndexSeparate", () => JobsTests.TestParallelForBatchSizeOneEachIndexSeparate());
    Console.WriteLine($"  == JobsDepth: {jobsPass}/{jobsTotal} passed ==");

    // ── 路径追踪 BVH 加速结构深度验证 ──
    int ptbvhPass = 0, ptbvhTotal = 0;
    void RunPTBVH(string name, Action test)
    {
        ptbvhTotal++;
        try { test(); ptbvhPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunPTBVH("SingleTriangleLeaf", () => PathTracerBVHTests.TestBVHSingleTriangleLeaf());
    RunPTBVH("CoversAllTrianglesExactlyOnce", () => PathTracerBVHTests.TestBVHCoversAllTrianglesExactlyOnce());
    RunPTBVH("ChildrenAdjacentAndBoundsNest", () => PathTracerBVHTests.TestBVHChildrenAdjacentAndBoundsNest());
    RunPTBVH("RayTraversalClosestHit", () => PathTracerBVHTests.TestBVHRayTraversalClosestHit());
    RunPTBVH("DuplicateCentroidsStillSplits", () => PathTracerBVHTests.TestBVHDuplicateCentroidsStillSplits());
    RunPTBVH("LargeMeshDepthAndLayout", () => PathTracerBVHTests.TestBVHLargeMeshDepthAndLayout());
    RunPTBVH("IndexLayoutPreservesVertexIndices", () => PathTracerBVHTests.TestBVHIndexLayoutPreservesVertexIndices());
    RunPTBVH("AccelMergeMeshesRemapsIndices", () => PathTracerBVHTests.TestAccelMergeMeshesRemapsIndices());
    Console.WriteLine($"  == PathTracerBVH: {ptbvhPass}/{ptbvhTotal} passed ==");

    // ── 延迟渲染 GPU 执行器深度验证 ──
    int dgpuPass = 0, dgpuTotal = 0;
    void RunDGpu(string name, Action test)
    {
        dgpuTotal++;
        try { test(); dgpuPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunDGpu("DeferredUboLayouts", () => DeferredGpuRendererTests.TestDeferredUboLayouts());
    RunDGpu("DeferredGpuPlanCompleteness", () => DeferredGpuRendererTests.TestDeferredGpuPlanCompleteness());
    RunDGpu("DeferredGpuShadersCompile", () => DeferredGpuRendererTests.TestDeferredGpuShadersCompile());
    Console.WriteLine($"  == DeferredGpu: {dgpuPass}/{dgpuTotal} passed ==");

    // ── UE5.8 级引擎核心基础设施深度验证 ──
    int corePass = 0, coreTotal = 0;
    void RunCore(string name, Action test)
    {
        coreTotal++;
        try { test(); corePass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunCore("YamaRandomSeedReproducibility", () => CoreTests.TestYamaRandomSeedReproducibility());
    RunCore("YamaRandomRangeSemantics", () => CoreTests.TestYamaRandomRangeSemantics());
    RunCore("MathUtilsLerpInverseLerp", () => CoreTests.TestMathUtilsLerpInverseLerp());
    RunCore("MathUtilsSmoothStep", () => CoreTests.TestMathUtilsSmoothStep());
    RunCore("EasingEndpoints", () => CoreTests.TestEasingEndpoints());
    RunCore("TimeManagerFixedStepAccumulator", () => CoreTests.TestTimeManagerFixedStepAccumulator());
    RunCore("TimeManagerEditorStepAccumulation", () => CoreTests.TestTimeManagerEditorStepAccumulation());
    RunCore("TimeScaleClampsToZero", () => CoreTests.TestTimeScaleClampsToZero());
    Console.WriteLine($"  == CoreDepth: {corePass}/{coreTotal} passed ==");

    // ── UE5.8 级碰撞宽相 / 空间查询 / 射线检测深度验证 ──
    int bpPass = 0, bpTotal = 0;
    void RunBroadphase(string name, Action test)
    {
        bpTotal++;
        try { test(); bpPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunBroadphase("AABBGeometryUnionContainsIntersects", () => BroadphaseTests.TestAABBGeometry());
    RunBroadphase("AABBSlabRaycast", () => BroadphaseTests.TestAABBSlabRaycast());
    RunBroadphase("QueryAABBConsistencyAllAlgos", () => BroadphaseTests.TestQueryAABBConsistencyAcrossAlgorithms());
    RunBroadphase("FindPairsLayerMaskStaticFilter", () => BroadphaseTests.TestFindPairsLayerMaskAndStaticFilter());
    RunBroadphase("RaycastAllCandidatesOnPath", () => BroadphaseTests.TestRaycastAllCandidatesOnPath());
    RunBroadphase("DynamicTreeRemoveReflectsInQuery", () => BroadphaseTests.TestDynamicTreeRemoveReflectsInQuery());
    Console.WriteLine($"  == BroadphaseDepth: {bpPass}/{bpTotal} passed ==");

    // ── UE5.8 级窄相形状射线求交深度验证（Sphere/Box/Mesh-MT/Heightfield/Compound）──
    int rcPass = 0, rcTotal = 0;
    void RunRaycastShape(string name, Action test)
    {
        rcTotal++;
        try { test(); rcPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunRaycastShape("SphereShapeAnalyticRaycast", () => RaycastShapeTests.TestSphereShapeRaycast());
    RunRaycastShape("BoxShapeSlabAndNormal", () => RaycastShapeTests.TestBoxShapeRaycastSlabAndNormal());
    RunRaycastShape("MeshShapeMollerTrumbore", () => RaycastShapeTests.TestMeshShapeRaycastMollerTrumbore());
    RunRaycastShape("HeightfieldShapeRaycast", () => RaycastShapeTests.TestHeightfieldShapeRaycast());
    RunRaycastShape("CapsuleShapeCylinderAndCaps", () => RaycastShapeTests.TestCapsuleShapeRaycastCylinderAndCaps());
    RunRaycastShape("CompoundShapeNearestChild", () => RaycastShapeTests.TestCompoundShapeRaycastNearestChild());
    Console.WriteLine($"  == RaycastShapeDepth: {rcPass}/{rcTotal} passed ==");

    // ── UE5.8 级视锥剔除深度验证（Frustum 平面提取/三态判定/GPU Culling）──
    int frPass = 0, frTotal = 0;
    void RunFrustum(string name, Action test)
    {
        frTotal++;
        try { test(); frPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunFrustum("FrustumExtractsSixNormalizedPlanes", () => FrustumCullingTests.TestFrustumExtractsSixNormalizedPlanes());
    RunFrustum("FrustumIntersectsTriState", () => FrustumCullingTests.TestFrustumIntersectsTriState());
    RunFrustum("OrthographicFrustumTriState", () => FrustumCullingTests.TestOrthographicFrustumTriState());
    RunFrustum("GPUCullingSystemAccumulateAndReset", () => FrustumCullingTests.TestGPUCullingSystemAccumulatesVisibleAndCulled());
    Console.WriteLine($"  == FrustumDepth: {frPass}/{frTotal} passed ==");

    // ── UE5.8 级第二套独立宽相实现深度验证（SAH 动态树/稀疏哈希/SAP）──
    int bprPass = 0, bprTotal = 0;
    void RunBroadphaseReal(string name, Action test)
    {
        bprTotal++;
        try { test(); bprPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunBroadphaseReal("DynTreeInsertQueryRemoveUpdate", () => BroadphaseRealTests.TestDynamicAABBTreeInsertQueryRemove());
    RunBroadphaseReal("DynTreeOverlapPairs", () => BroadphaseRealTests.TestDynamicAABBTreeOverlapPairs());
    RunBroadphaseReal("SpatialHashGridQueryAndPairs", () => BroadphaseRealTests.TestSpatialHashGridQueryAndPairs());
    RunBroadphaseReal("SpatialHashGridSeparatesDisjointCells", () => BroadphaseRealTests.TestSpatialHashGridSeparatesDisjointCells());
    RunBroadphaseReal("SweepAndPruneOverlapAndPredictive", () => BroadphaseRealTests.TestSweepAndPruneDetectsOverlapAndPredictive());
    Console.WriteLine($"  == BroadphaseRealDepth: {bprPass}/{bprTotal} passed ==");

    // ── UE5.8 级窄相碰撞检测深度验证（SAT/GJK/EPA）──
    int cdPass = 0, cdTotal = 0;
    void RunCollision(string name, Action test)
    {
        cdTotal++;
        try { test(); cdPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunCollision("SATAABBvsAABB", () => CollisionDetectionTests.TestSATAABBvsAABB());
    RunCollision("SATOBBvsOBB", () => CollisionDetectionTests.TestSATOBBvsOBB());
    RunCollision("SATSpherevsOBB", () => CollisionDetectionTests.TestSATSpherevsOBB());
    RunCollision("SATAABBNormalSymmetry", () => CollisionDetectionTests.TestSATAABBNormalSymmetry());
    RunCollision("GJKSphereIntersectSeparate", () => CollisionDetectionTests.TestGJKSphereIntersectAndSeparate());
    RunCollision("GJKRaycast", () => CollisionDetectionTests.TestGJKRaycast());
    Console.WriteLine($"  == CollisionDetectionDepth: {cdPass}/{cdTotal} passed ==");

    // ── UE5.8 级 Animation IK 求解器深度验证（FABRIK/TwoBone/CCD 纯数学）──
    int ikPass = 0, ikTotal = 0;
    void RunIK(string name, Action test)
    {
        ikTotal++;
        try { test(); ikPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunIK("FABRIKReachableTarget", () => IKTests.TestFABRIKReachableTarget());
    RunIK("FABRIKUnreachableStretch", () => IKTests.TestFABRIKUnreachableStretch());
    RunIK("FABRIKAlreadyAtTarget", () => IKTests.TestFABRIKAlreadyAtTarget());
    RunIK("TwoBoneIKReachablePoleY", () => IKTests.TestTwoBoneIKReachablePoleY());
    RunIK("TwoBoneIKUnreachableClamps", () => IKTests.TestTwoBoneIKUnreachableClamps());
    RunIK("TwoBoneIKTargetAtRootFolds", () => IKTests.TestTwoBoneIKTargetAtRootFolds());
    RunIK("CCDReachableConverges", () => IKTests.TestCCDReachableConverges());
    RunIK("CCDUnreachableStretch", () => IKTests.TestCCDUnreachableStretch());
    RunIK("FABRIKFourJointPreservesAllLengths", () => IKTests.TestFABRIKFourJointPreservesAllLengths());
    RunIK("FABRIKAndCCDBothConvergeSameTarget", () => IKTests.TestFABRIKAndCCDBothConvergeSameTarget());
    RunIK("SpringIKHighStiffnessMovesTowardTarget", () => IKTests.TestSpringIKHighStiffnessMovesTowardTarget());
    RunIK("SpringIKZeroStiffnessChainStays", () => IKTests.TestSpringIKZeroStiffnessChainStays());
    RunIK("SpringIKConvergenceFlag", () => IKTests.TestSpringIKConvergenceFlag());
    Console.WriteLine($"  == IKDepth: {ikPass}/{ikTotal} passed ==");

    // ── UE5.8 级世界流送子系统深度验证（StreamingPriority/预测/组合/可见性）──
    int stPass = 0, stTotal = 0;
    void RunStreaming(string name, Action test)
    {
        stTotal++;
        try { test(); stPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunStreaming("StreamingPriorityGameplayForced", () => StreamingTests.TestStreamingPriorityGameplayForced());
    RunStreaming("StreamingPriorityHysteresis", () => StreamingTests.TestStreamingPriorityHysteresis());
    RunStreaming("StreamingPriorityDistanceOrdering", () => StreamingTests.TestStreamingPriorityDistanceOrdering());
    RunStreaming("StreamingPriorityInViewBonus", () => StreamingTests.TestStreamingPriorityInViewBonus());
    RunStreaming("StreamingPriorityFormula", () => StreamingTests.TestStreamingPriorityFormula());
    RunStreaming("IsInViewDirectionForward", () => StreamingTests.TestIsInViewDirectionForward());
    RunStreaming("IsInViewDirectionBehind", () => StreamingTests.TestIsInViewDirectionBehind());
    RunStreaming("IsInViewDirectionOutsideFOV", () => StreamingTests.TestIsInViewDirectionOutsideFOV());
    RunStreaming("PredictiveLoaderVelocityPrediction", () => StreamingTests.TestPredictiveLoaderVelocityPrediction());
    RunStreaming("PredictiveLoaderStationaryNoPrediction", () => StreamingTests.TestPredictiveLoaderStationaryNoPrediction());
    RunStreaming("PredictiveLoaderReset", () => StreamingTests.TestPredictiveLoaderReset());
    RunStreaming("WorldCompositionGetLevelsNearSorted", () => StreamingTests.TestWorldCompositionGetLevelsNearSorted());
    RunStreaming("WorldCompositionUpdatePriorities", () => StreamingTests.TestWorldCompositionUpdatePriorities());
    RunStreaming("VisibilityManagerDistanceCulling", () => StreamingTests.TestVisibilityManagerDistanceCulling());
    RunStreaming("VisibilityManagerInFrustum", () => StreamingTests.TestVisibilityManagerInFrustum());
    RunStreaming("StreamingGridGetCellAt", () => StreamingTests.TestStreamingGridGetCellAt());
    RunStreaming("StreamingGridForceLoadCell", () => StreamingTests.TestStreamingGridForceLoadCell());
    RunStreaming("StreamingGridRegisterEntity", () => StreamingTests.TestStreamingGridRegisterEntity());
    RunStreaming("StreamingGridGetCellsInRadius", () => StreamingTests.TestStreamingGridGetCellsInRadius());
    RunStreaming("SelectLodDistanceBranches", () => StreamingTests.TestSelectLodDistanceBranches());
    RunStreaming("SelectLodBoundaryPrecision", () => StreamingTests.TestSelectLodBoundaryPrecision());
    RunStreaming("ShouldLoadByDistance", () => StreamingTests.TestShouldLoadByDistance());
    Console.WriteLine($"  == StreamingDepth: {stPass}/{stTotal} passed ==");

    // ── UE5.8 级导航网格子系统深度验证（FindNearestPoly/PointInPoly/Raycast/MoveAlongSurface/A*）──
    int nmPass = 0, nmTotal = 0;
    void RunNavMesh(string name, Action test)
    {
        nmTotal++;
        try { test(); nmPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunNavMesh("NavMeshFindNearestPolyHit", () => NavMeshQueryTests.TestFindNearestPolyHit());
    RunNavMesh("NavMeshFindNearestPolyMiss", () => NavMeshQueryTests.TestFindNearestPolyMiss());
    RunNavMesh("NavMeshPointInPoly", () => NavMeshQueryTests.TestPointInPoly());
    RunNavMesh("NavMeshProjectPoint", () => NavMeshQueryTests.TestProjectPoint());
    RunNavMesh("NavMeshRaycastCrossesPoly", () => NavMeshQueryTests.TestRaycastCrossesPoly());
    RunNavMesh("NavMeshRaycastHitsBoundary", () => NavMeshQueryTests.TestRaycastHitsBoundary());
    RunNavMesh("NavMeshMoveAlongSurfaceStopsAtWall", () => NavMeshQueryTests.TestMoveAlongSurfaceStopsAtWall());
    RunNavMesh("NavMeshMoveAlongSurfaceReachesTarget", () => NavMeshQueryTests.TestMoveAlongSurfaceReachesTarget());
    RunNavMesh("NavMeshFindPathReachable", () => NavMeshQueryTests.TestFindPathReachable());
    RunNavMesh("NavMeshFindPathSamePoly", () => NavMeshQueryTests.TestFindPathSamePoly());
    RunNavMesh("NavMeshFindPathInvalidRef", () => NavMeshQueryTests.TestFindPathInvalidRef());
    RunNavMesh("NavMeshPolyRefPacking", () => NavMeshQueryTests.TestPolyRefPacking());
    Console.WriteLine($"  == NavMeshDepth: {nmPass}/{nmTotal} passed ==");

    // ── UE5.8 级 Utility AI + 路径平滑子系统深度验证（响应曲线/考虑因素/动作评分/漏斗算法/路径工具）──
    int uaPass = 0, uaTotal = 0;
    void RunUtilityAI(string name, Action test)
    {
        uaTotal++;
        try { test(); uaPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunUtilityAI("LinearCurveEndpoints", () => UtilityAIAndPathSmootherTests.TestLinearCurveEndpoints());
    RunUtilityAI("LogisticCurveCenterAndTails", () => UtilityAIAndPathSmootherTests.TestLogisticCurveCenterAndTails());
    RunUtilityAI("GaussianCurvePeak", () => UtilityAIAndPathSmootherTests.TestGaussianCurvePeak());
    RunUtilityAI("StepCurveHardSwitch", () => UtilityAIAndPathSmootherTests.TestStepCurveHardSwitch());
    RunUtilityAI("ResponseCurveClampsInput", () => UtilityAIAndPathSmootherTests.TestResponseCurveClampsInput());
    RunUtilityAI("ConsiderationWeightPow", () => UtilityAIAndPathSmootherTests.TestConsiderationWeightPow());
    RunUtilityAI("ConsiderationInvertInput", () => UtilityAIAndPathSmootherTests.TestConsiderationInvertInput());
    RunUtilityAI("ConsiderationDisabledReturnsOne", () => UtilityAIAndPathSmootherTests.TestConsiderationDisabledReturnsOne());
    RunUtilityAI("ActionMultiplyZeroKills", () => UtilityAIAndPathSmootherTests.TestActionMultiplyZeroKills());
    RunUtilityAI("ActionMinimum", () => UtilityAIAndPathSmootherTests.TestActionMinimum());
    RunUtilityAI("ActionAnchorRescue", () => UtilityAIAndPathSmootherTests.TestActionAnchorRescue());
    RunUtilityAI("ActionBaseWeight", () => UtilityAIAndPathSmootherTests.TestActionBaseWeight());
    RunUtilityAI("ActionEmptyConsiderationsReturnsOne", () => UtilityAIAndPathSmootherTests.TestActionEmptyConsiderationsReturnsOne());
    RunUtilityAI("FunnelEmptyPortals", () => UtilityAIAndPathSmootherTests.TestFunnelEmptyPortals());
    RunUtilityAI("FunnelStraightCorridor", () => UtilityAIAndPathSmootherTests.TestFunnelStraightCorridor());
    RunUtilityAI("FunnelBypassesCorner", () => UtilityAIAndPathSmootherTests.TestFunnelBypassesCorner());
    RunUtilityAI("PathTortuosity", () => UtilityAIAndPathSmootherTests.TestPathTortuosity());
    RunUtilityAI("PathReverse", () => UtilityAIAndPathSmootherTests.TestPathReverse());
    RunUtilityAI("PathFindSharpCorners", () => UtilityAIAndPathSmootherTests.TestPathFindSharpCorners());
    Console.WriteLine($"  == UtilityAIPathDepth: {uaPass}/{uaTotal} passed ==");

    // ── UE5.8 级编队子系统深度验证（十种队形几何/SlotAssigner 四策略/旋转矩阵）──
    int fmPass = 0, fmTotal = 0;
    void RunFormation(string name, Action test)
    {
        fmTotal++;
        try { test(); fmPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunFormation("LineSymmetryAndSpacing", () => FormationTests.TestLineSymmetryAndSpacing());
    RunFormation("ColumnAlongNegativeZ", () => FormationTests.TestColumnAlongNegativeZ());
    RunFormation("VSymmetry", () => FormationTests.TestVSymmetry());
    RunFormation("CircleRadiusAndAngle", () => FormationTests.TestCircleRadiusAndAngle());
    RunFormation("DiamondLayers", () => FormationTests.TestDiamondLayers());
    RunFormation("GenerateSlotsZero", () => FormationTests.TestGenerateSlotsZero());
    RunFormation("AssignNearest", () => FormationTests.TestAssignNearest());
    RunFormation("AssignFixedOrder", () => FormationTests.TestAssignFixedOrder());
    RunFormation("AssignPriority", () => FormationTests.TestAssignPriority());
    RunFormation("AssignRoleBased", () => FormationTests.TestAssignRoleBased());
    RunFormation("GetAgentTargetPositionWithRotation", () => FormationTests.TestGetAgentTargetPositionWithRotation());
    RunFormation("GetAgentTargetPositionUnregistered", () => FormationTests.TestGetAgentTargetPositionUnregistered());
    Console.WriteLine($"  == FormationDepth: {fmPass}/{fmTotal} passed ==");

    // ── UE5.8 级人群避让子系统深度验证（ORCA 半平面/障碍物/LP 求解/三场景）──
    int orcaPass = 0, orcaTotal = 0;
    void RunORCA(string name, Action test)
    {
        orcaTotal++;
        try { test(); orcaPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunORCA("PlaneFeasible", () => ORCAAvoidanceTests.TestPlaneFeasible());
    RunORCA("ObstacleLeftNormal", () => ORCAAvoidanceTests.TestObstacleLeftNormal());
    RunORCA("AddAgentSequentialId", () => ORCAAvoidanceTests.TestAddAgentSequentialId());
    RunORCA("AddObstacle", () => ORCAAvoidanceTests.TestAddObstacle());
    RunORCA("SolveLP_ProjectsOntoConstraint", () => ORCAAvoidanceTests.TestSolveLP_ProjectsOntoConstraint());
    RunORCA("SolveLP_NoConstraintSpeedClamp", () => ORCAAvoidanceTests.TestSolveLP_NoConstraintSpeedClamp());
    RunORCA("HeadOnCollisionDeflects", () => ORCAAvoidanceTests.TestHeadOnCollisionDeflects());
    RunORCA("RearEndDeflectsAndSlows", () => ORCAAvoidanceTests.TestRearEndDeflectsAndSlows());
    RunORCA("LateralPassSmallDeflection", () => ORCAAvoidanceTests.TestLateralPassSmallDeflection());
    RunORCA("ObstacleORCAPlaneGenerated", () => ORCAAvoidanceTests.TestObstacleORCAPlaneGenerated());
    Console.WriteLine($"  == ORCADepth: {orcaPass}/{orcaTotal} passed ==");

    // ── UE5.8 级战术空间推理深度验证 (ThreatField 威胁场) ──
    int spatialPass = 0, spatialTotal = 0;
    void RunSpatial(string name, Action test)
    {
        spatialTotal++;
        try { test(); spatialPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunSpatial("ThreatFalloff", () => SpatialReasoningTests.TestThreatFalloff());
    RunSpatial("ThreatLosBlock", () => SpatialReasoningTests.TestThreatLosBlock());
    RunSpatial("ThreatDecay", () => SpatialReasoningTests.TestThreatDecay());
    RunSpatial("FindSafestPoint", () => SpatialReasoningTests.TestFindSafestPoint());
    RunSpatial("ThreatClear", () => SpatialReasoningTests.TestThreatClear());
    Console.WriteLine($"  == SpatialDepth: {spatialPass}/{spatialTotal} passed ==");

    // ── UE5.8 级战术分析深度验证 (角色特性/小队状态/局势报告/角色分配) ──
    int tactPass = 0, tactTotal = 0;
    void RunTactical(string name, Action test)
    {
        tactTotal++;
        try { test(); tactPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunTactical("RoleTraits", () => TacticalAnalysisTests.TestRoleTraits());
    RunTactical("SquadCenterHealthCohesion", () => TacticalAnalysisTests.TestSquadCenterHealthCohesion());
    RunTactical("SquadCohesionDropsWhenSpread", () => TacticalAnalysisTests.TestSquadCohesionDropsWhenSpread());
    RunTactical("ReportDecisions", () => TacticalAnalysisTests.TestReportDecisions());
    RunTactical("AssignRolesOrder", () => TacticalAnalysisTests.TestAssignRolesOrder());
    Console.WriteLine($"  == TacticalDepth: {tactPass}/{tactTotal} passed ==");

    // ── UE5.8 级景深 (DoF) 深度验证 (CoC 薄镜头 + Bokeh 核) ──
    int dofPass = 0, dofTotal = 0;
    void RunDof(string name, Action test)
    {
        dofTotal++;
        try { test(); dofPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunDof("CoCFocusPlaneZero", () => DepthOfFieldTests.TestCoCFocusPlaneZero());
    RunDof("CoCNearFieldMonotonicSaturates", () => DepthOfFieldTests.TestCoCNearFieldMonotonicSaturates());
    RunDof("CoCFarFieldMonotonicSaturates", () => DepthOfFieldTests.TestCoCFarFieldMonotonicSaturates());
    RunDof("BokehCircularSamples", () => DepthOfFieldTests.TestBokehCircularSamples());
    RunDof("BokehHexagonalSamples", () => DepthOfFieldTests.TestBokehHexagonalSamples());
    RunDof("BokehBladeSamples", () => DepthOfFieldTests.TestBokehBladeSamples());
    Console.WriteLine($"  == DOFDepth: {dofPass}/{dofTotal} passed ==");

    // ── UE5.8 级蜂群智能深度验证 (FlowField 积分场 + InfluenceMap 影响图) ──
    int swarmPass = 0, swarmTotal = 0;
    void RunSwarm(string name, Action test)
    {
        swarmTotal++;
        try { test(); swarmPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunSwarm("FlowFieldIntegration", () => SwarmIntelligenceTests.TestFlowFieldIntegration());
    RunSwarm("FlowFieldObstacleBlocks", () => SwarmIntelligenceTests.TestFlowFieldObstacleBlocks());
    RunSwarm("FlowFieldDirectionTowardGoal", () => SwarmIntelligenceTests.TestFlowFieldDirectionTowardGoal());
    RunSwarm("InfluenceMapGaussian", () => SwarmIntelligenceTests.TestInfluenceMapGaussian());
    RunSwarm("InfluenceMapDecayAndClear", () => SwarmIntelligenceTests.TestInfluenceMapDecayAndClear());
    RunSwarm("InfluenceMapSample", () => SwarmIntelligenceTests.TestInfluenceMapSample());
    Console.WriteLine($"  == SwarmDepth: {swarmPass}/{swarmTotal} passed ==");

    // ── Vulkan 渲染后端深度验证 (ResourceStateTracker: barrier 消除纯逻辑) ──
    int vkPass = 0, vkTotal = 0;
    void RunVulkan(string name, Action test)
    {
        vkTotal++;
        try { test(); vkPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunVulkan("UntrackedImageNeedsUndefinedTransition", () => VulkanBackendTests.TestUntrackedImageNeedsUndefinedTransition());
    RunVulkan("RedundantImageBarrierEliminated", () => VulkanBackendTests.TestRedundantImageBarrierEliminated());
    RunVulkan("LayoutChangeNeedsBarrier", () => VulkanBackendTests.TestLayoutChangeNeedsBarrier());
    RunVulkan("BufferBarrierAccessOverlapLogic", () => VulkanBackendTests.TestBufferBarrierAccessOverlapLogic());
    RunVulkan("ClearResetsState", () => VulkanBackendTests.TestClearResetsState());
    RunVulkan("DescriptorLayoutHashDeterminism", () => VulkanBackendTests.TestDescriptorLayoutHashDeterminism());
    RunVulkan("DescriptorQuotaCheck", () => VulkanBackendTests.TestDescriptorQuotaCheck());
    RunVulkan("MemoryTypeSelection", () => VulkanBackendTests.TestMemoryTypeSelection());
    RunVulkan("TimestampToMs", () => VulkanBackendTests.TestTimestampToMs());
    RunVulkan("AllocatorAlignment", () => VulkanBackendTests.TestAllocatorAlignment());
    RunVulkan("AllocatorFragmentation", () => VulkanBackendTests.TestAllocatorFragmentation());
    RunVulkan("PipelineStateHash", () => VulkanBackendTests.TestPipelineStateHash());
    RunVulkan("PipelineCacheHitRate", () => VulkanBackendTests.TestPipelineCacheHitRate());
    RunVulkan("SyncTimeoutConversion", () => VulkanBackendTests.TestSyncTimeoutConversion());
    RunVulkan("FenceFrameIndex", () => VulkanBackendTests.TestFenceFrameIndex());
    RunVulkan("SwapchainImageCountSelection", () => VulkanBackendTests.TestSwapchainImageCountSelection());
    Console.WriteLine($"  == VulkanBackendDepth: {vkPass}/{vkTotal} passed ==");

    // ── UE4.7 级延迟渲染管线 (DeferredRenderPipeline) ──
    DeferredRenderTests.TestGBufferPackDecodeRoundtrip();
    DeferredRenderTests.TestGBufferNormalEncodingAxisAligned();
    DeferredRenderTests.TestGBufferShadingModelPacked();
    DeferredRenderTests.TestCascadePracticalSplitsMonotonic();
    DeferredRenderTests.TestCascadeLinearEqualSpacing();
    DeferredRenderTests.TestCascadePracticalLogWeightBoundaries();
    DeferredRenderTests.TestSSAOKernelHemisphere();
    DeferredRenderTests.TestDeferredLightDirectionalNonZero();
    DeferredRenderTests.TestDeferredLightBackfaceZero();
    DeferredRenderTests.TestDeferredLightMetalNoDiffuse();
    DeferredRenderTests.TestDeferredPointLightAttenuation();
    DeferredRenderTests.TestUnlitEmissiveIgnoresLight();
    DeferredRenderTests.TestDefaultDeferredGraphHasFourPasses();
    DeferredRenderTests.TestDefaultDeferredGraphResources();
    DeferredRenderTests.TestDeferredGraphExecutesWithNullDevice();
    EditorTests.TestEditorCommandStackUndoRedoCreateAndDelete(tempRoot);
    EditorTests.TestEditorCommandStackUndoRedoMoveAndPropertyEdit(tempRoot);
    RuntimeTests.TestRuntimeSceneLoaderLoadsExportedDemoPackage(tempRoot);
    RuntimeTests.TestRuntimeSceneLoaderReportsMissingPackageFiles(tempRoot);
    RuntimeTests.TestRuntimeGameLoopTicksComponentsAndRendersFrame();
    RuntimeTests.TestRuntimeGameLoopRunsFixedStepsWithAccumulator();
    int runtimePass = 0, runtimeTotal = 0;
    void RunRuntime(string name, Action test)
    {
        runtimeTotal++;
        try { test(); runtimePass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunRuntime("GameLoopSpiralOfDeathGuard", () => RuntimeTests.TestRuntimeGameLoopDropsExcessAccumulatedTime());
    RunRuntime("GameLoopAdvancesTimeAcrossFrames", () => RuntimeTests.TestRuntimeGameLoopAdvancesTotalTimeAcrossFrames());
    Console.WriteLine($"  == RuntimeDepth: {runtimePass}/{runtimeTotal} passed ==");

    // ── 追平 Godot 3 Node-tree 核心：TransformReal/GameObjectReal 场景树深度 ──
    int sceneTreePass = 0, sceneTreeTotal = 0;
    void RunSceneTree(string name, Action test)
    {
        sceneTreeTotal++;
        try { test(); sceneTreePass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunSceneTree("RootTransformIdentity", () => SceneTreeTests.TestRootTransformIdentity());
    RunSceneTree("ChildWorldPositionComposesParent", () => SceneTreeTests.TestChildWorldPositionComposesParent());
    RunSceneTree("ChildRotationPropagatesFromParent", () => SceneTreeTests.TestChildRotationPropagatesFromParent());
    RunSceneTree("ChildScalePropagatesFromParent", () => SceneTreeTests.TestChildScalePropagatesFromParent());
    RunSceneTree("WorldPositionStaysOnReparent", () => SceneTreeTests.TestWorldPositionStaysOnReparent());
    RunSceneTree("HierarchyTraversal", () => SceneTreeTests.TestHierarchyTraversal());
    RunSceneTree("CycleReparentForbidden", () => SceneTreeTests.TestCycleReparentForbidden());
    RunSceneTree("UnparentReturnsToRoot", () => SceneTreeTests.TestUnparentReturnsToRoot());
    RunSceneTree("TransformPointRoundTrip", () => SceneTreeTests.TestTransformPointRoundTrip());
    RunSceneTree("ComponentUniquenessEnforced", () => SceneTreeTests.TestComponentUniquenessEnforced());
    RunSceneTree("AllowMultipleComponents", () => SceneTreeTests.TestAllowMultipleComponents());
    RunSceneTree("ComponentAwakeRunsOnAdd", () => SceneTreeTests.TestComponentAwakeRunsOnAdd());
    RunSceneTree("ComponentRemoveCallsDestroy", () => SceneTreeTests.TestComponentRemoveCallsDestroy());
    RunSceneTree("ComponentQueryAPI", () => SceneTreeTests.TestComponentQueryAPI());
    RunSceneTree("GameObjectMetadata", () => SceneTreeTests.TestGameObjectMetadata());
    RunSceneTree("SceneInitializeRunsAwakeThenStart", () => SceneTreeTests.TestSceneInitializeRunsAwakeThenStart());
    RunSceneTree("SceneUpdateDrivesComponents", () => SceneTreeTests.TestSceneUpdateDrivesComponents());
    RunSceneTree("SceneDestroyCallsOnDestroy", () => SceneTreeTests.TestSceneDestroyCallsOnDestroy());
    RunSceneTree("SceneFindGameObjectRecursive", () => SceneTreeTests.TestSceneFindGameObjectRecursive());
    RunSceneTree("SceneFindGameObjectsWithTag", () => SceneTreeTests.TestSceneFindGameObjectsWithTag());
    RunSceneTree("SceneFindGameObjectsInLayer", () => SceneTreeTests.TestSceneFindGameObjectsInLayer());
    RunSceneTree("SceneAllGameObjectsRecursiveCount", () => SceneTreeTests.TestSceneAllGameObjectsRecursiveCount());
    Console.WriteLine($"  == SceneTreeDepth: {sceneTreePass}/{sceneTreeTotal} passed ==");

    // ── 协程系统深度验证（异步流程控制） ──
    int coroutinePass = 0, coroutineTotal = 0;
    void RunCoroutine(string name, Action test)
    {
        coroutineTotal++;
        try { test(); coroutinePass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunCoroutine("CoroutineBasicAdvanceAndComplete", () => CoroutineTests.TestCoroutineBasicAdvanceAndComplete());
    RunCoroutine("CoroutineWaitUntilPredicate", () => CoroutineTests.TestCoroutineWaitUntilPredicate());
    RunCoroutine("CoroutineWaitForFrames", () => CoroutineTests.TestCoroutineWaitForFrames());
    RunCoroutine("CoroutineNestedSubRoutine", () => CoroutineTests.TestCoroutineNestedSubRoutine());
    RunCoroutine("CoroutinePauseAndResume", () => CoroutineTests.TestCoroutinePauseAndResume());
    RunCoroutine("CoroutineStopCoroutine", () => CoroutineTests.TestCoroutineStopCoroutine());
    RunCoroutine("CoroutineExceptionTerminates", () => CoroutineTests.TestCoroutineExceptionTerminates());
    RunCoroutine("CoroutineStopAll", () => CoroutineTests.TestCoroutineStopAll());
    RunCoroutine("CoroutineWaitForSignal", () => CoroutineTests.TestCoroutineWaitForSignal());
    RunCoroutine("CoroutineWaitForSignalDisconnectOnStop", () => CoroutineTests.TestCoroutineWaitForSignalDisconnectOnStop());
    Console.WriteLine($"  == CoroutineDepth: {coroutinePass}/{coroutineTotal} passed ==");

    // ── 追平 Godot 3 对象生命周期与查询：SetActive/Instantiate/FindObjectsOfType/对象池 ──
    int sceneObjPass = 0, sceneObjTotal = 0;
    void RunSceneObject(string name, Action test)
    {
        sceneObjTotal++;
        try { test(); sceneObjPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunSceneObject("SetActiveTogglesEnableDisable", () => SceneObjectTests.TestSetActiveTogglesEnableDisable());
    RunSceneObject("InactiveSkipsUpdate", () => SceneObjectTests.TestInactiveSkipsUpdate());
    RunSceneObject("InstantiateDeepCopiesHierarchy", () => SceneObjectTests.TestInstantiateDeepCopiesHierarchy());
    RunSceneObject("InstantiateIsIndependent", () => SceneObjectTests.TestInstantiateIsIndependent());
    RunSceneObject("FindObjectsOfTypeFiltersActiveAndType", () => SceneObjectTests.TestFindObjectsOfTypeFiltersActiveAndType());
    RunSceneObject("FindComponentsOfTypeReturnsInstances", () => SceneObjectTests.TestFindComponentsOfTypeReturnsInstances());
    RunSceneObject("PoolGetReleaseReusesInstance", () => SceneObjectTests.TestPoolGetReleaseReusesInstance());
    RunSceneObject("PoolRespectsMaxSize", () => SceneObjectTests.TestPoolRespectsMaxSize());
    RunSceneObject("GroupMembershipAndQuery", () => SceneObjectTests.TestGroupMembershipAndQuery());
    RunSceneObject("GroupInactiveExcludedFromQuery", () => SceneObjectTests.TestGroupInactiveExcludedFromQuery());
    RunSceneObject("SignalConnectEmitOrder", () => SceneObjectTests.TestSignalConnectEmitOrder());
    RunSceneObject("SignalOneShotAndArgs", () => SceneObjectTests.TestSignalOneShotAndArgs());
    RunSceneObject("SignalDisconnectAndHasSignal", () => SceneObjectTests.TestSignalDisconnectAndHasSignal());
    RunSceneObject("TimerOneShotEmitsAndStops", () => SceneObjectTests.TestTimerOneShotEmitsAndStops());
    RunSceneObject("TimerLoopRestarts", () => SceneObjectTests.TestTimerLoopRestarts());
    RunSceneObject("TimerStopCancelsWithoutFire", () => SceneObjectTests.TestTimerStopCancelsWithoutFire());
    RunSceneObject("TimerWaitTimeChangeResetsCountdown", () => SceneObjectTests.TestTimerWaitTimeChangeResetsCountdown());
    RunSceneObject("PackedSceneInstantiateIndependent", () => SceneObjectTests.TestPackedSceneInstantiateIndependent());
    RunSceneObject("PackedSceneInstantiateMultiple", () => SceneObjectTests.TestPackedSceneInstantiateMultiple());
    RunSceneObject("TweenEaseKnownValues", () => SceneObjectTests.TestTweenEaseKnownValues());
    RunSceneObject("TweenFloatStepEasedProgress", () => SceneObjectTests.TestTweenFloatStepEasedProgress());
    RunSceneObject("TweenSequentialSteps", () => SceneObjectTests.TestTweenSequentialSteps());
    RunSceneObject("TweenCallbackStep", () => SceneObjectTests.TestTweenCallbackStep());
    RunSceneObject("TweenLoops", () => SceneObjectTests.TestTweenLoops());
    RunSceneObject("TweenPauseResume", () => SceneObjectTests.TestTweenPauseResume());
    RunSceneObject("DeferredCallFlushesAtEndOfFrame", () => SceneObjectTests.TestDeferredCallFlushesAtEndOfFrame());
    RunSceneObject("DeferredCallPreservesOrder", () => SceneObjectTests.TestDeferredCallPreservesOrder());
    RunSceneObject("DeferredCallWithArgs", () => SceneObjectTests.TestDeferredCallWithArgs());
    RunSceneObject("DeferredCallNoReentrantFlush", () => SceneObjectTests.TestDeferredCallNoReentrantFlush());
    RunSceneObject("DeferredCallExceptionDoesNotBlockQueue", () => SceneObjectTests.TestDeferredCallExceptionDoesNotBlockQueue());
    RunSceneObject("DeferredCallSafeSceneMutation", () => SceneObjectTests.TestDeferredCallSafeSceneMutation());
    RunSceneObject("ProcessModePausableSkipsWhenPaused", () => SceneObjectTests.TestProcessModePausableSkipsWhenPaused());
    RunSceneObject("ProcessModeAlwaysRunsWhenPaused", () => SceneObjectTests.TestProcessModeAlwaysRunsWhenPaused());
    RunSceneObject("ProcessModeWhenPausedOnlyRunsWhenPaused", () => SceneObjectTests.TestProcessModeWhenPausedOnlyRunsWhenPaused());
    RunSceneObject("ProcessModeDisabledNeverRuns", () => SceneObjectTests.TestProcessModeDisabledNeverRuns());
    RunSceneObject("ProcessModeInheritDefaultsToPausable", () => SceneObjectTests.TestProcessModeInheritDefaultsToPausable());
    RunSceneObject("ProcessModeInactiveOverridesAlways", () => SceneObjectTests.TestProcessModeInactiveOverridesAlways());
    RunSceneObject("AnimationPlayerPlayOnceFinishesAndEmits", () => SceneObjectTests.TestAnimationPlayerPlayOnceFinishesAndEmits());
    RunSceneObject("AnimationPlayerLoopWrapsWithoutFinish", () => SceneObjectTests.TestAnimationPlayerLoopWrapsWithoutFinish());
    RunSceneObject("AnimationPlayerPauseResumeHoldsPosition", () => SceneObjectTests.TestAnimationPlayerPauseResumeHoldsPosition());
    RunSceneObject("AnimationPlayerSpeedScaleScalesAdvance", () => SceneObjectTests.TestAnimationPlayerSpeedScaleScalesAdvance());
    RunSceneObject("AnimationPlayerSeekJumpsPosition", () => SceneObjectTests.TestAnimationPlayerSeekJumpsPosition());
    RunSceneObject("AnimationPlayerQueuePlaysNextAfterFinish", () => SceneObjectTests.TestAnimationPlayerQueuePlaysNextAfterFinish());
    RunSceneObject("AnimationPlayerValueTrackEmitsValueChanged", () => SceneObjectTests.TestAnimationPlayerValueTrackEmitsValueChanged());
    Console.WriteLine($"  == SceneObjectDepth: {sceneObjPass}/{sceneObjTotal} passed ==");
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
    // ── AI 驱动 NPC 系统（LLM 接线，超 UE5.8）──
    NpcTests.TestPersonalityClampsAndDescribes();
    NpcTests.TestEmotionTriggerDecayAndDominant();
    NpcTests.TestEmotionDominantTracksHighest();
    NpcTests.TestMemoryRememberDecayForgetAndSummarize();
    NpcTests.TestMemoryCapacityEvictsWeakest();
    NpcTests.TestRelationshipBidirectionalChange();
    NpcTests.TestScheduleActivityByHour();
    NpcTests.TestNpcCharacterTickDecaysMentality();
    NpcTests.TestNpcInteractUpdatesRelationships();
    NpcTests.TestNpcDialogueSystemPromptInjectsMentality();
    NpcTests.TestNpcDialogueLLMRoundtrip();
    NpcTests.TestNpcWorldTicksAndFinds();
    // ── LLM 引导行为驱动的 AI NPC (NpcBrain: 意图/决策/任务/群体社交) ──
    NpcBrainTests.TestParseIntentPlainJson();
    NpcBrainTests.TestParseIntentMarkdownCodeBlock();
    NpcBrainTests.TestParseIntentInvalidReturnsNull();
    NpcBrainTests.TestBuildIntentPromptInjectsMentality();
    NpcBrainTests.TestDecideAsyncAppliesIntentFromLLM();
    NpcBrainTests.TestDecideAsyncFallsBackToHeuristicOnBadJson();
    NpcBrainTests.TestDecideAsyncNoFallbackReturnsNull();
    NpcBrainTests.TestApplyHeuristicRespectsCustomSchedule();
    NpcBrainTests.TestTickGoToMovesNpcTowardsGoal();
    NpcBrainTests.TestTickFleeMovesAwayFromThreat();
    NpcBrainTests.TestTickTalkToKeepsPosition();
    NpcBrainTests.TestStopClearsExecution();
    NpcBrainTests.TestQuestActivateAdvanceComplete();
    NpcBrainTests.TestQuestFail();
    NpcBrainTests.TestQuestSystemReportProgressTriggersEvent();
    NpcBrainTests.TestQuestSystemIgnoresMismatchedTarget();
    NpcBrainTests.TestSocialSimulationGeneratesEventForNearbySocialPair();
    NpcBrainTests.TestSocialSimulationRespectsRadiusAndActivity();
    NpcBrainTests.TestSocialSimulationCooldownSuppressesRepeat();
    // ── 心智接入行为树/StateTree (NpcBrain → BT/StateTree 运行实例) ──
    NpcBrainWiringTests.TestBTTaskNpcIntentExecutesGoToToCompletion();
    NpcBrainWiringTests.TestBTTaskNpcIntentFailsWithoutBrain();
    NpcBrainWiringTests.TestBTTaskNpcIntentTalkToSucceedsImmediately();
    NpcBrainWiringTests.TestBTTaskNpcIntentFleeMovesAway();
    NpcBrainWiringTests.TestBTServiceSyncsIntentToBlackboard();
    NpcBrainWiringTests.TestStateTreeTaskNpcIntentExecutesGoTo();
    // ── AI 感知系统 (AIPerceptionComponent: 感官注册/刺激/遗忘/更新) ──
    PerceptionTests.TestPerceptionRegistersDefaultSenses();
    PerceptionTests.TestPerceptionStimulusTriggersOnTargetAdded();
    PerceptionTests.TestPerceptionForgetRemovesTarget();
    PerceptionTests.TestPerceptionUpdateAdvancesTime();
    // ── AIController TickAI 链路 (感知→记忆→状态机→决策→行为树) ──
    AIControllerTests.TestAIControllerPossessInitializesPerception();
    AIControllerTests.TestAIControllerTickAIDrivesPerception();
    AIControllerTests.TestAIControllerTickAIUpdatesMemory();
    AIControllerTests.TestAIControllerTickAIStateMachineRuns();
    // ── RealPhysicsEngine 约束/关节 (Hinge/Spring + Step 求解) ──
    PhysicsConstraintTests.TestPhysicsEngineCreatesConstraints();
    PhysicsConstraintTests.TestPhysicsEngineStepWithConstraints();
    PhysicsConstraintTests.TestPhysicsEngineSpringMaintainsFiniteState();
    // ── RealPhysicsEngine 真实物理正确性验证 (解析解对照) ──
    // 逐个 try/catch：失败记录并继续，最后汇总通过率（避免一个失败掩盖其余结果）。
    int physPass = 0, physTotal = 0;
    void RunPhys(string name, Action test)
    {
        physTotal++;
        try { test(); physPass++; Console.WriteLine($"  [PASS] {name}"); }
        catch (Exception ex) { TestHelpers.RecordFailure(); Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
    }
    RunPhys("FreeFallVelocity", () => PhysicsRealismTests.TestFreeFallVelocityMatchesAnalytic());
    RunPhys("FreeFallDisplacement", () => PhysicsRealismTests.TestFreeFallDisplacementMatchesAnalytic());
    RunPhys("MassIndependentFall", () => PhysicsRealismTests.TestMassIndependentFall());
    RunPhys("Projectile", () => PhysicsRealismTests.TestProjectileMatchesParabola());
    RunPhys("ImpulseMomentum", () => PhysicsRealismTests.TestImpulseMomentumTheorem());
    RunPhys("TorqueAngular", () => PhysicsRealismTests.TestTorqueAngularAcceleration());
    RunPhys("AngularMomentum", () => PhysicsRealismTests.TestAngularMomentumConservation());
    RunPhys("EnergyConservation", () => PhysicsRealismTests.TestFreeFallEnergyConservation());
    RunPhys("BoxSettlesFinite", () => PhysicsRealismTests.TestBoxOnGroundSettlesFinite());
    RunPhys("StaticFrictionHolds", () => PhysicsRealismTests.TestStaticFrictionHoldsBelowThreshold());
    RunPhys("StaticFrictionBreaks", () => PhysicsRealismTests.TestStaticFrictionBreaksAboveThreshold());
    RunPhys("KineticFrictionDecel", () => PhysicsRealismTests.TestKineticFrictionDeceleration());
    RunPhys("Restitution", () => PhysicsRealismTests.TestRestitutionBounceHeightRatio());
    RunPhys("StackStability", () => PhysicsRealismTests.TestStackStability());
    Console.WriteLine($"  == PhysicsRealism: {physPass}/{physTotal} passed ==");

    // ── 运行时子系统 (时间跳变钳制 / FPS 有限性 / 定时事件重入 / 暂停嵌套) ──
    RuntimeRealismTests.Run();

    // ── 网络子系统 (值序列化往返 / 复制属性脏标记 / 客户端预测纠错 / VarInt / 量化 / CRC32 / 位打包) ──
    NetworkTests.TestNetworkValueSerializerPrimitiveRoundTrip();
    NetworkTests.TestNetworkValueSerializerMathRoundTrip();
    NetworkTests.TestReplicatedPropertyDirtyTracking();
    NetworkTests.TestReplicatedPropertyShouldReplicate();
    NetworkTests.TestReplicatedPropertySerializeRoundTrip();
    NetworkTests.TestClientPredictorAdvancesState();
    NetworkTests.TestClientPredictorNoCorrectionOnMatch();
    NetworkTests.TestClientPredictorCorrectionOnMismatch();
    NetworkTests.TestVarIntRoundTrip();
    NetworkTests.TestFloatQuantizationRoundTrip();
    NetworkTests.TestCrc32KnownVector();
    NetworkTests.TestBitPackRoundTrip();
    NetworkTests.TestBitReaderSignExtendsSignedInt();
    // ── 复制图 / 兴趣管理 (ReplicationGraph: 距离/常相关/恒相关 + 复制器门控) ──
    ReplicationGraphTests.TestRelevanceByDistance();
    ReplicationGraphTests.TestAlwaysRelevantObject();
    ReplicationGraphTests.TestAlwaysRelevantToConnection();
    ReplicationGraphTests.TestConnectionMoveUpdatesRelevance();
    ReplicationGraphTests.TestObjectMoveUpdatesRelevance();
    ReplicationGraphTests.TestAverageRelevantConnections();
    ReplicationGraphTests.TestReplicatorGatesByInterest();
    // ── 网络会话管理 (连接到达→玩家入会话→建房/入房→开始→结束→离开, 会话状态机) ──
    NetworkSessionManagerTests.TestConnectedRegistersPlayerAndLobbyState();
    NetworkSessionManagerTests.TestDisconnectCleansUpPlayerAndState();
    NetworkSessionManagerTests.TestCreateSessionHostTransitionsToInRoom();
    NetworkSessionManagerTests.TestJoinSessionClientTransitionsToInRoom();
    NetworkSessionManagerTests.TestLeaveRoomReturnsToLobby();
    NetworkSessionManagerTests.TestStartEndGameStateAndEvents();
    NetworkSessionManagerTests.TestInterestGraphObserverLifecycle();
    NetworkSessionManagerTests.TestFullSessionPipelineEndToEnd();
    NetworkSessionManagerTests.TestHeartbeatAndUpdateNoThrow();
    // ── MicroCluster 可见性 (视锥剔除 + LOD 选择 + 流式加载 + 帧生命周期) ──
    MicroClusterVisibilityTests.TestFrustumCullingSelectsVisibleClusters();
    MicroClusterVisibilityTests.TestLodSelectsParentWhenProjectedSmall();
    MicroClusterVisibilityTests.TestBeginFrameResetsState();
    MicroClusterVisibilityTests.TestPopulateCullResultFillsIds();
    MicroClusterVisibilityTests.TestStreamingOutMakesClusterInvisible();
    MicroClusterVisibilityTests.TestRegisterRemoveClearClusters();
    MicroClusterVisibilityTests.TestEndToEndCullPipeline();
    // ── 打包崩溃上报集成 (CrashReporterConfig → PackageBuilder → ConfigureFromFile → 报告落盘) ──
    PackageCrashReportingTests.TestCrashReporterConfigRoundtrip();
    PackageCrashReportingTests.TestPackageGeneratesCrashReporterConfig();
    PackageCrashReportingTests.TestPackageDisablesCrashReporting();
    PackageCrashReportingTests.TestConfigureFromFileInitializesAndReports();
    PackageCrashReportingTests.TestForceReportGeneratesReportFile();
    // ── 音频后端 (软件/OpenAL 跨平台 + 引擎集成) ──
    AudioBackendTests.TestSoftwareBackendCapturesPcm();
    AudioBackendTests.TestSoftwareBackendExportWav16();
    AudioBackendTests.TestSoftwareBackendClearAndShutdown();
    AudioBackendTests.TestOpenALBackendNoThrowWhenNativeMissing();
    AudioBackendTests.TestEngineSoftwareBackendIntegration();
    // ── 音频解码器 (FLAC 位精确 + MD5 校验; Vorbis/MP3 移植进行中) ──
    AudioRealDecoderTests.TestFlacSineBitExact();
    AudioRealDecoderTests.TestFlacChirpStereoMd5();
    // ── 可视化蓝图运行时执行 (GraphNode.Execute 分发 + 数据流 + 条件/状态机) ──
    VisualBlueprintRuntimeTests.TestBeginPlaySetVariablePrintStringChain();
    VisualBlueprintRuntimeTests.TestSetVariableStoresComputedValue();
    VisualBlueprintRuntimeTests.TestBranchConditionalExecution();
    VisualBlueprintRuntimeTests.TestDoOnceFiresOnlyOnce();
    VisualBlueprintRuntimeTests.TestPureNodeDataFlowFeedsPrint();
    VisualBlueprintRuntimeTests.TestExecuteGraphTopologicalComputation();
    VisualBlueprintRuntimeTests.TestParseDefaultValueVariants();
    VisualBlueprintRuntimeTests.TestBlueprintJsonRoundtripPreservesExecution();
    // ── RPC 端到端闭环 (BuildPayload→传输→ReceiveAndExecute + 实例目标解析) ──
    RpcEndToEndTests.TestRpcIntParameterInvokesInstance();
    RpcEndToEndTests.TestRpcStringFloatParametersInvokeInstance();
    RpcEndToEndTests.TestRpcVector3ParameterInvokesInstance();
    RpcEndToEndTests.TestRpcStaticMethodInvokedWithoutTarget();
    RpcEndToEndTests.TestRpcMissingTargetResolverReportsError();
    RpcEndToEndTests.TestRpcUnresolvedTargetReportsError();
    RpcEndToEndTests.TestRpcCallStatsTracked();
    RpcEndToEndTests.TestBuildPayloadEncodesMethodId();
    // ── 着色器编译集成 (MaterialGraph→HLSL 源码 + CookShader blob + 变体/关键字/缓存) ──
    ShaderCompileIntegrationTests.TestMaterialGraphCompilesToHlsl();
    ShaderCompileIntegrationTests.TestCookShaderProducesWellFormedBlob();
    ShaderCompileIntegrationTests.TestCookShaderVariantsDistinct();
    ShaderCompileIntegrationTests.TestShaderCompileKeywordVariantDistinct();
    ShaderCompileIntegrationTests.TestShaderCacheHitOnSecondCompile();
    ShaderCompileIntegrationTests.TestCookShaderEmptyVariantListProducesHeaderOnly();
    // ── 网络真实传输层集成验证 (UDPTransport 真实 socket 本地回环 → RpcDispatcher 端到端) ──
    NetworkTransportIntegrationTests.TestUdpLoopbackDeliversRpcToInstance();
    NetworkTransportIntegrationTests.TestUdpLoopbackDeliversMultiParameter();
    NetworkTransportIntegrationTests.TestUdpLoopbackDeliversStaticRpc();
    // ── 音频编辑器集成 (EditorAudioBridge 后端选择器 + 音量 + AudioSettingsPanel 注册) ──
    AudioEditorIntegrationTests.TestAudioBridgeBackendSelectorSoftware();
    AudioEditorIntegrationTests.TestAudioBridgeBackendSelectorDummy();
    AudioEditorIntegrationTests.TestAudioBridgeAvailableBackends();
    AudioEditorIntegrationTests.TestAudioBridgeBackendDisplayName();
    AudioEditorIntegrationTests.TestAudioBridgeVolumeControls();
    AudioEditorIntegrationTests.TestAudioSettingsPanelRegistered();
    // ── 可视化蓝图编辑器交互 UX (BlueprintAsset 往返 + BlueprintClass 实例 + BlueprintEditorPanel 画布/撤销) ──
    BlueprintEditorIntegrationTests.TestBlueprintAssetJsonRoundtrip();
    BlueprintEditorIntegrationTests.TestBlueprintClassVariablesAndInstance();
    BlueprintEditorIntegrationTests.TestBlueprintInstanceBeginPlayExecutes();
    BlueprintEditorIntegrationTests.TestBlueprintEditorPanelAddNode();
    BlueprintEditorIntegrationTests.TestBlueprintEditorPanelUndoRedo();
    BlueprintEditorIntegrationTests.TestBlueprintEditorPanelRegistered();
    // ── 动画状态机/BlendTree (AnimatorController 多层管线闭环) ──
    AnimationStateMachineTests.TestAnimatorControllerTransitionsOnParameter();
    AnimationStateMachineTests.TestAnimatorControllerFiresEvents();
    AnimationStateMachineTests.TestBlendTreeStateMotionInterpolates();
    AnimationStateMachineTests.TestAnimatorDelegatesToController();
    // ── 动画蓝图运行时填充 (资产→工厂→Animator.Controller→BlendTreeReal 桥→ECS 资源填充) ──
    AnimationBlueprintTests.TestAnimationBlueprintBuildsControllerAndFillsHook();
    AnimationBlueprintTests.TestAnimationBlueprintBlendTreeStateInterpolates();
    AnimationBlueprintTests.TestBlendTreeRealMotionBridgesGraph();
    AnimationBlueprintTests.TestEcsBridgeFillsControllerFromBlueprint();
    // ── Control Rig 完整约束求值器 (UE5.8 深度级 任务A: 层次/空间/父约束/瞄准/IK/变换/端到端) ──
    ControlRigEvaluationTests.TestRigHierarchyComputesGlobals();
    ControlRigEvaluationTests.TestParentConstraintMaintainsOffset();
    ControlRigEvaluationTests.TestAimConstraintPointsAtTarget();
    ControlRigEvaluationTests.TestIKConstraintReachesTarget();
    ControlRigEvaluationTests.TestTransformConstraintMatchesSource();
    ControlRigEvaluationTests.TestSpaceConversionRoundtrip();
    ControlRigEvaluationTests.TestControlRigEvaluateWritesBones();
    // ── Full Body IK + 脚部锁定 + 脚部放置 (UE5.8 深度级 任务B) ──
    FullBodyIKTests.TestFullBodyIKReachesTarget();
    FullBodyIKTests.TestFullBodyIKPriorityOrdering();
    FullBodyIKTests.TestFootLockHoldsPosition();
    FullBodyIKTests.TestFootLockUnlockReleases();
    FullBodyIKTests.TestFootPlacementGeneratesAlternatingTargets();
    FullBodyIKTests.TestFootPlacementAdvancesPhase();
    // ── BlendSpace3D 三维混合空间 (任务C：UE5.8 UBlendSpace 三轴采样) ──
    BlendSpace3DTests.TestWeightAtSamplePoint();
    BlendSpace3DTests.TestTetrahedralBarycentric();
    BlendSpace3DTests.TestWeightsSumToOneAndNonNegative();
    BlendSpace3DTests.TestSegmentInterpolationPose();
    BlendSpace3DTests.TestConvexHullFallback();
    BlendSpace3DTests.TestTetrahedralizeValid();
    // ── Unity5 Mecanim BlendTree 2D 混合模式（SimpleDirectional/FreeformDirectional/Cartesian）──
    BlendTree2DModeTests.TestSimpleDirectionalZeroVectorNoMotion();
    BlendTree2DModeTests.TestSimpleDirectionalMagnitudeScale();
    BlendTree2DModeTests.TestSimpleDirectionalFullMagnitude();
    BlendTree2DModeTests.TestFreeformDirectionalRadiusIndependent();
    BlendTree2DModeTests.TestDirectionalModesDifferAtSmallRadius();
    BlendTree2DModeTests.TestFreeformDirectional45Degrees();
    BlendTree2DModeTests.TestFreeformCartesianBarycentric();
    BlendTree2DModeTests.TestSimpleDirectionalSampleNoTranslation();
    // ── 蒙太奇通知系统 + 分层 (任务D：UE5.8 AnimNotify/AnimNotifyState/MontageLayerStack) ──
    MontageNotifyTests.TestAnimNotifyFiresOnceAtTime();
    MontageNotifyTests.TestAnimNotifyStateBeginEnd();
    MontageNotifyTests.TestMontageNotifyTrackMultipleTracks();
    MontageNotifyTests.TestMontageLayerBlendWeights();
    MontageNotifyTests.TestMontageLayerAddRemove();
    MontageNotifyTests.TestNotifyLogCapturesEvents();
    // ── 动画蓝图编辑器面板 (任务E：UE5.8 AnimGraphEditor 状态图/参数/预览) ──
    AnimGraphEditorTests.TestPanelOpenNewAsset();
    AnimGraphEditorTests.TestPanelBuildPreview();
    AnimGraphEditorTests.TestPanelParametersDriveState();
    AnimGraphEditorTests.TestPanelSetTrigger();
    AnimGraphEditorTests.TestPanelTickPreviewAdvancesState();
    AnimGraphEditorTests.TestPanelRegisteredInMainWindow();
    // ── PoseAsset 姿态资产 + BlendPosesNode (任务F：UE5.8 UPoseAsset / Blend Poses by weight) ──
    PoseAssetTests.TestPoseAssetAddAndSetBone();
    PoseAssetTests.TestPoseAssetCapturePose();
    PoseAssetTests.TestPoseAssetApplyToPose();
    PoseAssetTests.TestBlendPosesNodeWeighted();
    PoseAssetTests.TestBlendPosesNodeSingleFull();
    PoseAssetTests.TestBlendPosesNodeWeightedRotation();
    // ── 双体系 Actor 架构接线 (GameFramework.Actor → GameplayWorld 派发) ──
    GameFrameworkWiringTests.TestGameFrameworkActorTicksInWorld();
    GameFrameworkWiringTests.TestAIControllerTickAIDrivenByWorld();
    GameFrameworkWiringTests.TestAIControllerPerceptionDrivesStateMachine();
    // ── EQS 环境查询系统 (生成器/测试器/得分排序/MaxResults 截断/缓存/AIController 端到端) ──
    EQSTests.TestEQSActorsInRadiusWithDistanceSorts();
    EQSTests.TestEQSGridGeneratorProducesExpectedCount();
    EQSTests.TestEQSMaxResultsTruncates();
    EQSTests.TestEQSTestTraceUsesRaycast();
    EQSTests.TestEQSManagerCaching();
    EQSTests.TestAIControllerRunEQSQueryEndToEnd();
    NpcBrainWiringTests.TestStateTreeTaskNpcIntentFailsWithoutBrain();
    NpcBrainWiringTests.TestFullChainLLMDecisionDrivesBehaviorTree();
    // ── 真实 LLM 端点配置工厂 (LLMConfigFactory) ──
    NpcBrainWiringTests.TestLLMConfigFactoryDeepSeekSetsEndpoint();
    NpcBrainWiringTests.TestLLMConfigFactoryLocalOllama();
    NpcBrainWiringTests.TestLLMConfigFactoryFromEnvironment();
    NpcBrainWiringTests.TestLLMConfigFactoryCreateClientIsReady();
    // ── GPU 渲染管线（Deferred + 后处理栈，真实 GLSL shader + RHI 管线规划）──
    GpuPipelineTests.TestGbufferShadersValidAndMrt8();
    GpuPipelineTests.TestDeferredLightingShaderValid();
    GpuPipelineTests.TestPostProcessStackShadersValid();
    GpuPipelineTests.TestFramePlanBuildsGbufferMrt8();
    GpuPipelineTests.TestFramePlanPassOrder();
    GpuPipelineTests.TestFramePlanRespectsFeatureFlags();
    GpuPipelineTests.TestBuildPipelineDescsProduceRhiDescs();
    GpuPipelineTests.TestGpuShaderCompilerDeterministic();
    GpuPipelineTests.TestGpuShaderCompilerRejectsEmpty();
    GpuPipelineTests.TestGpuPipelineBridgeExecutesPasses();
    GapAuditTests.TestIblShadersValid();
    GapAuditTests.TestAtmosphereShadersValid();
    GapAuditTests.TestAdditionalEffectShadersValid();
    GapAuditTests.TestAllFeaturePlanIncludesNewPasses();
    GapAuditTests.TestDefaultPlanExcludesOptionalPasses();
    GapAuditTests.TestBridgeStatsDetectNewPasses();
    GapAuditTests.TestGapAuditAllFeaturesHighCoverage();
    GapAuditTests.TestGapAuditIslandDetection();
    GapAuditTests.TestGapAuditPassBridgeMatch();
    GapAuditTests.TestGapAuditSummaryContainsMetrics();
    if (TestHelpers.GlobalFailures > 0)
{
    Console.WriteLine($"YamaEngine tests FAILED: {TestHelpers.GlobalFailures} test(s) failed.");
    Environment.Exit(1);
}
Console.WriteLine("All YamaEngine tests passed.");
}

finally
{
    try { Directory.Delete(tempRoot, true); } catch { }
}
