#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Chievfx.Mcp.Extensions.Cameras;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;

namespace Chievfx.Mcp.Editor.Tests
{
    public sealed class ChievfxMcpCamerasExtensionTests
    {
        [SetUp]
        public void SetUp()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [TearDown]
        public void TearDown()
        {
            StageUtility.GoToMainStage();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AssetDatabase.DeleteAsset(ChievfxMcpCamerasQaFixture.GeneratedFolder);
            AssetDatabase.DeleteAsset(ChievfxMcpCamerasQaFixture.ScenePath);
            AssetDatabase.DeleteAsset(ChievfxMcpCamerasQaFixture.SequencerScenePath);
        }

        [Test]
        public void StatusReportsTimelineAndCinemachineDependencyGates()
        {
            var status = Resource("chievfx://extensions/chievfx.cameras/status");

            Assert.AreEqual("chievfx.cameras", status["extensionId"]);
            Assert.AreEqual("com.unity.cinemachine", Row(status, "cinemachine")["packageName"]);
            Assert.AreEqual("com.unity.timeline", Row(status, "timeline")["packageName"]);
            Assert.AreEqual(status["cinemachineApiFamily"], Row(status, "cinemachine")["cinemachineApiFamily"]);
            Assert.IsTrue(status.ContainsKey("sequencerCameraAvailable"));
            Assert.IsTrue(status.ContainsKey("sequencerCameraTypeLoaded"));
            Assert.IsTrue(Row(status, "sequencerCamera").ContainsKey("sequencerCameraAvailable"));
            Assert.IsTrue(Row(status, "sequencerCamera").ContainsKey("sequencerCameraTypeLoaded"));
            Assert.IsTrue(Row(status, "splinesDolly").ContainsKey("optionalPackageName"));
            Assert.IsTrue(Row(status, "splinesDolly").ContainsKey("optionalVersionDefineActive"));
            Assert.IsTrue(Row(status, "splinesDolly").ContainsKey("secondaryHelperTypeName"));
            Assert.IsTrue(Row(status, "inputAxisController").ContainsKey("helperTypeName"));
            Assert.AreEqual("Unity.Cinemachine.InputAxis", Row(status, "inputAxisController")["secondaryHelperTypeName"]);
            Assert.AreEqual("Unity.Cinemachine.IInputAxisOwner", Row(status, "inputAxisController")["tertiaryHelperTypeName"]);
            Assert.IsTrue(Row(status, "inputSystem").ContainsKey("primaryTypeName"));
            Assert.IsTrue(Row(status, "blenderSettings").ContainsKey("helperTypeName"));
            Assert.IsTrue(Row(status, "impulse").ContainsKey("secondaryHelperTypeName"));
            Assert.IsTrue(status.ContainsKey("collisionImpulseSourceTypeLoaded"));
            Assert.IsTrue(status.ContainsKey("externalImpulseListenerTypeLoaded"));
            Assert.IsTrue(Row(status, "confiner2D").ContainsKey("helperTypeName"));
            Assert.AreEqual("UnityEngine.Collider2D", Row(status, "confiner2D")["secondaryHelperTypeName"]);
            Assert.IsTrue(Row(status, "confiner3D").ContainsKey("helperTypeName"));
            Assert.AreEqual("UnityEngine.Collider", Row(status, "confiner3D")["secondaryHelperTypeName"]);
            Assert.IsTrue(status.ContainsKey("obsoleteConfinerTypeLoaded"));
            CollectionAssert.Contains(new[] { "absent", "cm3", "cm3LegacyObsolete", "cm2" }, Row(status, "cinemachine")["apiFamily"]);
            Assert.IsTrue(StringArray(status, "prompts").Contains("cameras-ending-session-slowmo-zoom"));
            Assert.IsTrue(StringArray(status, "prompts").Contains("gamefeel-ending-session-slowmo"));
            Assert.IsTrue(StringArray(status, "prompts").Contains("cameras-cinemachine-sequencer-camera"));
            Assert.IsTrue(StringArray(status, "prompts").Contains("cameras-cinemachine-splines-dolly"));
            Assert.IsTrue(StringArray(status, "prompts").Contains("cameras-cinemachine-input-axis-controller"));
            Assert.IsTrue(StringArray(status, "prompts").Contains("cameras-cinemachine-impulse-shake"));
            Assert.IsTrue(StringArray(status, "resources").Contains("chievfx://extensions/chievfx.cameras/cinemachine/splines-dolly"));
            Assert.IsTrue(StringArray(status, "resources").Contains("chievfx://extensions/chievfx.cameras/cinemachine/input-axis-controllers"));
            Assert.IsTrue(StringArray(status, "resources").Contains("chievfx://extensions/chievfx.cameras/cinemachine/blender-settings"));
            Assert.IsTrue(StringArray(status, "resources").Contains("chievfx://extensions/chievfx.cameras/cinemachine/impulse"));
            Assert.IsTrue(StringArray(status, "resources").Contains("chievfx://extensions/chievfx.cameras/cinemachine/confiner-2d"));
            Assert.IsTrue(StringArray(status, "resources").Contains("chievfx://extensions/chievfx.cameras/cinemachine/confiner-3d"));
            Assert.IsTrue(StringArray(status, "workflowNotes").Any(note => note.Contains("does not create hidden runtime owners", StringComparison.Ordinal)));
            Assert.IsTrue(StringArray(status, "workflowNotes").Any(note => note.Contains("does not invent input assets", StringComparison.Ordinal)));
            Assert.IsTrue(StringArray(status, "workflowNotes").Any(note => note.Contains("does not add hidden impulse trigger scripts", StringComparison.Ordinal)));
            Assert.IsTrue(StringArray(status, "workflowNotes").Any(note => note.Contains("read-only inventory resources", StringComparison.Ordinal)));
            Assert.IsTrue(StringArray(status, "workflowNotes").Any(note => note.Contains("screenshot-camera", StringComparison.Ordinal)));
        }

        [Test]
        public void ManifestAdvertisesPromptOnlyEndingSessionSlowMoGuidance()
        {
            var manifestPath = Path.Combine("Temp", "ChievfxMcpCamerasSlowMoPromptManifestTest.json");
            ChievfxMcpExtensionRegistry.ExportManifest(manifestPath);

            var manifest = File.ReadAllText(manifestPath);
            StringAssert.Contains("\"name\": \"gamefeel-ending-session-slowmo\"", manifest);
            StringAssert.Contains("\"name\": \"cameras-cinemachine-sequencer-camera\"", manifest);
            StringAssert.Contains("\"name\": \"cameras-cinemachine-input-axis-controller\"", manifest);
            StringAssert.Contains("\"name\": \"cameras-cinemachine-impulse-shake\"", manifest);
            StringAssert.Contains("\"name\": \"cinemachine-sequencer-create\"", manifest);
            StringAssert.Contains("\"name\": \"cameras-cinemachine-splines-dolly\"", manifest);
            StringAssert.Contains("\"name\": \"cinemachine-spline-dolly-set\"", manifest);
            StringAssert.Contains("\"name\": \"cinemachine-confiner-set\"", manifest);
            StringAssert.Contains("\"category\": \"cinemachine-and-timeline\"", manifest);
            StringAssert.Contains("\"uri\": \"chievfx://extensions/chievfx.cameras/cinemachine/sequencers\"", manifest);
            StringAssert.Contains("\"uri\": \"chievfx://extensions/chievfx.cameras/cinemachine/splines-dolly\"", manifest);
            StringAssert.Contains("\"uri\": \"chievfx://extensions/chievfx.cameras/cinemachine/input-axis-controllers\"", manifest);
            StringAssert.Contains("\"uri\": \"chievfx://extensions/chievfx.cameras/cinemachine/blender-settings\"", manifest);
            StringAssert.Contains("\"uri\": \"chievfx://extensions/chievfx.cameras/cinemachine/impulse\"", manifest);
            StringAssert.Contains("\"uri\": \"chievfx://extensions/chievfx.cameras/cinemachine/confiner-2d\"", manifest);
            StringAssert.Contains("\"uri\": \"chievfx://extensions/chievfx.cameras/cinemachine/confiner-3d\"", manifest);
            StringAssert.Contains("\"uriTemplate\": \"chievfx://extensions/chievfx.cameras/cinemachine/sequencer/{pathOrInstanceId}\"", manifest);
            StringAssert.Contains("\"category\": \"Game Feel\"", manifest);
            StringAssert.Contains("Read chievfx.cameras status/resources before mutation", manifest);
            StringAssert.Contains("screenshot-camera for QA", manifest);
            StringAssert.Contains("Author the SplineContainer path manually in Unity first", manifest);
            StringAssert.Contains("MCP should not invent input assets", manifest);
            StringAssert.Contains("PlayerInput may clone action assets per player at runtime", manifest);
            StringAssert.Contains("MCP should not add hidden impulse trigger scripts", manifest);
            StringAssert.Contains("channel masks", manifest);
            StringAssert.Contains("SplineContainer knots and shape manually", manifest);
            StringAssert.Contains("MCP must not add hidden MonoBehaviours", manifest);
            StringAssert.Contains("Time.unscaledDeltaTime", manifest);
            StringAssert.Contains("WaitForSecondsRealtime", manifest);
            StringAssert.Contains("adjustFixedDeltaTime tradeoff", manifest);
            StringAssert.Contains("AudioSource.pitch", manifest);
            StringAssert.Contains("AudioMixer.updateMode", manifest);
            StringAssert.Contains("AudioMixerUpdateMode", manifest);
            StringAssert.Contains("AnimatorUpdateMode.UnscaledTime", manifest);
            StringAssert.Contains("QA checklist", manifest);
            StringAssert.Contains("Goal: {goal}", manifest);
            StringAssert.Contains("public void StartEndingSlowMo()\\n{{", manifest);
            StringAssert.Contains("if (adjustFixedDeltaTime)\\n            {{", manifest);
            StringAssert.Contains("RestoreTimeScale();\\n}}\\n", manifest);
        }

        [Test]
        public void CinemachineAbsentKeepsCinemachineResourcesAndToolsUnavailable()
        {
            var status = Resource("chievfx://extensions/chievfx.cameras/status");
            var cinemachine = Row(status, "cinemachine");
            if (Equals(true, cinemachine["available"]))
            {
                Assert.Ignore("Cinemachine is available in this project.");
            }

            Assert.AreEqual(false, cinemachine["available"]);
            Assert.AreEqual(false, cinemachine["packageInstalled"]);
            Assert.AreEqual(false, cinemachine["versionDefineActive"]);
            Assert.AreEqual("absent", cinemachine["apiFamily"]);
            Assert.AreEqual("absent", status["cinemachineApiFamily"]);

            AssertCinemachineUnavailable(Resource("chievfx://extensions/chievfx.cameras/cinemachine/cameras"));
            AssertCinemachineUnavailable(Resource("chievfx://extensions/chievfx.cameras/cinemachine/camera/MissingCamera"));
            AssertCinemachineUnavailable(Resource("chievfx://extensions/chievfx.cameras/cinemachine/brains"));
            AssertSequencerUnavailable(Resource("chievfx://extensions/chievfx.cameras/cinemachine/sequencers"));
            AssertSequencerUnavailable(Resource("chievfx://extensions/chievfx.cameras/cinemachine/sequencer/MissingSequencer"));
            AssertAdvancedHelperUnavailable(Resource("chievfx://extensions/chievfx.cameras/cinemachine/splines-dolly"), "splinesDolly");
            AssertAdvancedHelperUnavailable(Resource("chievfx://extensions/chievfx.cameras/cinemachine/input-axis-controllers"), "inputAxisController");
            AssertAdvancedHelperUnavailable(Resource("chievfx://extensions/chievfx.cameras/cinemachine/blender-settings"), "blenderSettings");
            AssertAdvancedHelperUnavailable(Resource("chievfx://extensions/chievfx.cameras/cinemachine/impulse"), "impulse");
            AssertAdvancedHelperUnavailable(Resource("chievfx://extensions/chievfx.cameras/cinemachine/confiner-2d"), "confiner2D");
            AssertAdvancedHelperUnavailable(Resource("chievfx://extensions/chievfx.cameras/cinemachine/confiner-3d"), "confiner3D");
            AssertCinemachineUnavailable(RunTool("brain-ensure", "{'cameraName':'QaGameplayCamera','dryRun':true}"));
            AssertCinemachineUnavailable(RunTool("cinemachine-create", "{'name':'QaCinemachineCamera','dryRun':true}"));
            AssertCinemachineUnavailable(RunTool("cinemachine-set", "{'targetPath':'MissingCamera','priority':20,'dryRun':true}"));
            AssertSequencerUnavailable(RunTool("cinemachine-sequencer-create", "{'name':'QaSequencer','dryRun':true}"));
            AssertAdvancedHelperUnavailable(RunTool("cinemachine-spline-dolly-set", "{'targetPath':'MissingCamera','splinePath':'MissingSpline','dryRun':true}"), "splinesDolly");
            AssertAdvancedHelperUnavailable(RunTool("cinemachine-blender-settings-set", "{'assetPath':'Assets/GeneratedChievfxMcpCameraQa/Missing.asset','dryRun':true}"), "blenderSettings");
            AssertAdvancedHelperUnavailable(RunTool("cinemachine-confiner-set", "{'dimension':'2d','targetPath':'MissingCamera','colliderPath':'MissingCollider','dryRun':true}"), "confiner2D");
            AssertAdvancedHelperUnavailable(RunTool("cinemachine-confiner-set", "{'dimension':'3d','targetPath':'MissingCamera','colliderPath':'MissingCollider','dryRun':true}"), "confiner3D");
            AssertCinemachineUnavailable(RunTool("timeline-shot-sequence-create", "{'directorName':'QaDirector','dryRun':true}"));
            Assert.IsNull(GameObject.Find("QaSequencer"));
            Assert.IsFalse(SceneManager.GetActiveScene().isDirty);
        }

        [Test]
        public void AdvancedHelperGateClassifierReportsOptionalAndCm3Reasons()
        {
            var available = ChievfxMcpCamerasExtension.CreateAdvancedHelperGateForTests("splinesDolly", true, "3.1.6", "cm3", true, true, "com.unity.splines", true, "2.7.2", true);
            Assert.AreEqual(true, available["available"]);
            Assert.AreEqual(true, available["optionalGateAvailable"]);

            var optionalMissing = ChievfxMcpCamerasExtension.CreateAdvancedHelperGateForTests("splinesDolly", true, "3.1.6", "cm3", true, true, "com.unity.splines", false, null, false);
            Assert.AreEqual(false, optionalMissing["available"]);
            StringAssert.Contains("com.unity.splines package is not installed", Convert.ToString(optionalMissing["reason"]));

            var optionalDefineInactive = ChievfxMcpCamerasExtension.CreateAdvancedHelperGateForTests("splinesDolly", true, "3.1.6", "cm3", true, true, "com.unity.splines", true, "2.7.2", true, optionalVersionDefineActive: false);
            Assert.AreEqual(false, optionalDefineInactive["available"]);
            StringAssert.Contains("CHIEVFX_MCP_HAS_SPLINES is not active", Convert.ToString(optionalDefineInactive["reason"]));

            var typeMissing = ChievfxMcpCamerasExtension.CreateAdvancedHelperGateForTests("impulse", true, "3.1.6", "cm3", true, false, null, false, null, false);
            Assert.AreEqual(false, typeMissing["available"]);
            StringAssert.Contains("required type Test.Helper is not loaded", Convert.ToString(typeMissing["reason"]));

            var defineInactive = ChievfxMcpCamerasExtension.CreateAdvancedHelperGateForTests("confiner3D", true, "3.1.6", "cm3", false, true, null, false, null, false);
            Assert.AreEqual(false, defineInactive["available"]);
            StringAssert.Contains("CHIEVFX_MCP_HAS_CINEMACHINE is not active", Convert.ToString(defineInactive["reason"]));

            var cm2 = ChievfxMcpCamerasExtension.CreateAdvancedHelperGateForTests("inputAxisController", true, "2.10.0", "cm2", true, true, null, false, null, false);
            Assert.AreEqual(false, cm2["available"]);
            StringAssert.Contains("Cinemachine 2.x API detected", Convert.ToString(cm2["reason"]));

            var legacy = ChievfxMcpCamerasExtension.CreateAdvancedHelperGateForTests("blenderSettings", true, "3.0.0", "cm3LegacyObsolete", true, true, null, false, null, false);
            Assert.AreEqual(false, legacy["available"]);
            StringAssert.Contains("legacy obsolete", Convert.ToString(legacy["reason"]));
        }

        [Test]
        public void CinemachineApiFamilyClassifierPrefersLoadedTypesThenPackageVersion()
        {
            Assert.AreEqual("cm3", ChievfxMcpCamerasExtension.ClassifyCinemachineApiFamilyForTests(false, null, true, true, true));
            Assert.AreEqual("cm3LegacyObsolete", ChievfxMcpCamerasExtension.ClassifyCinemachineApiFamilyForTests(false, null, false, true, true));
            Assert.AreEqual("cm2", ChievfxMcpCamerasExtension.ClassifyCinemachineApiFamilyForTests(false, null, false, false, true));
            Assert.AreEqual("cm3", ChievfxMcpCamerasExtension.ClassifyCinemachineApiFamilyForTests(true, "3.1.0-pre.1", false, false, false));
            Assert.AreEqual("cm2", ChievfxMcpCamerasExtension.ClassifyCinemachineApiFamilyForTests(true, "2.10.0", false, false, false));
            Assert.AreEqual("absent", ChievfxMcpCamerasExtension.ClassifyCinemachineApiFamilyForTests(false, null, false, false, false));
        }

        [Test]
        public void SequencerGateClassifierReportsClearUnavailableReasons()
        {
            var available = ChievfxMcpCamerasExtension.CreateSequencerGateForTests(true, "3.1.6", "cm3", true, true, true, true, true, true, true);
            Assert.AreEqual(true, available["available"]);
            Assert.AreEqual(true, available["sequencerCameraAvailable"]);

            var absent = ChievfxMcpCamerasExtension.CreateSequencerGateForTests(false, null, "absent", false, false, false, false, false, false, false);
            Assert.AreEqual(false, absent["available"]);
            StringAssert.Contains("package is not installed", Convert.ToString(absent["reason"]));

            var cm2 = ChievfxMcpCamerasExtension.CreateSequencerGateForTests(true, "2.10.0", "cm2", true, false, false, false, false, false, false);
            Assert.AreEqual(false, cm2["available"]);
            StringAssert.Contains("Cinemachine 2.x API detected", Convert.ToString(cm2["reason"]));

            var legacy = ChievfxMcpCamerasExtension.CreateSequencerGateForTests(true, "3.0.0", "cm3LegacyObsolete", true, false, false, false, false, false, false);
            Assert.AreEqual(false, legacy["available"]);
            StringAssert.Contains("legacy obsolete", Convert.ToString(legacy["reason"]));

            var missingType = ChievfxMcpCamerasExtension.CreateSequencerGateForTests(true, "3.1.6", "cm3", true, false, true, true, true, true, true);
            Assert.AreEqual(false, missingType["available"]);
            Assert.AreEqual(false, missingType["sequencerCameraTypeLoaded"]);
            StringAssert.Contains("types are not loaded", Convert.ToString(missingType["reason"]));
        }

        [Test]
        public void TimelineAvailableManifestAndResourcesExposeCompactReadOnlyShapes()
        {
            RequireTimeline();

            var manifestPath = Path.Combine("Temp", "ChievfxMcpCamerasExtensionManifestTest.json");
            ChievfxMcpExtensionRegistry.ExportManifest(manifestPath);

            var manifest = File.ReadAllText(manifestPath);
            StringAssert.Contains("\"id\": \"chievfx.cameras\"", manifest);
            StringAssert.Contains("\"name\": \"timeline-director-create\"", manifest);
            StringAssert.Contains("\"name\": \"timeline-director-preview\"", manifest);
            StringAssert.Contains("\"uri\": \"chievfx://extensions/chievfx.cameras/timeline/directors\"", manifest);
            StringAssert.Contains("\"uri\": \"chievfx://extensions/chievfx.cameras/timeline/assets\"", manifest);

            CreateTimelineDirectorWithAsset("QaTimelineResources", ChievfxMcpCamerasQaFixture.TimelineAssetPath);
            SaveGeneratedScene("TimelineResourcesScene.unity");
            Assert.IsFalse(SceneManager.GetActiveScene().isDirty);

            var directors = Resource("chievfx://extensions/chievfx.cameras/timeline/directors");
            Assert.AreEqual(96, directors["maxRows"]);
            Assert.IsTrue(Rows(directors, "directors").Any(row => Equals("QaTimelineResources", row["path"])));
            Assert.IsFalse(SceneManager.GetActiveScene().isDirty);

            var directorDetail = Resource("chievfx://extensions/chievfx.cameras/timeline/director/QaTimelineResources");
            var director = Row(directorDetail, "target");
            Assert.AreEqual("QaTimelineResources", director["path"]);
            Assert.IsTrue(director.ContainsKey("clips"));
            Assert.IsTrue(director.ContainsKey("tracks"));
            Assert.IsTrue(director.ContainsKey("signals"));
            Assert.IsTrue(director.ContainsKey("bindings"));
            Assert.IsFalse(SceneManager.GetActiveScene().isDirty);

            var assets = Resource("chievfx://extensions/chievfx.cameras/timeline/assets");
            Assert.AreEqual(128, assets["maxRows"]);
            Assert.IsTrue(Rows(assets, "assets").Any(row => Equals(ChievfxMcpCamerasQaFixture.TimelineAssetPath, row["path"])));

            var assetDetail = Row(Resource("chievfx://extensions/chievfx.cameras/timeline/asset/" + Uri.EscapeDataString(ChievfxMcpCamerasQaFixture.TimelineAssetPath)), "asset");
            Assert.IsTrue(assetDetail.ContainsKey("clips"));
            Assert.IsTrue(assetDetail.ContainsKey("tracks"));
            Assert.IsTrue(assetDetail.ContainsKey("signals"));
        }

        [Test]
        public void TimelineDirectorToolsRespectDryRunUndoDirtyAndPreviewRestore()
        {
            RequireTimeline();

            SaveGeneratedScene("TimelineDryRunScene.unity");
            var dryAssetPath = ChievfxMcpCamerasQaFixture.GeneratedFolder + "/DryRunTimeline.playable";
            var dryRun = RunTool("timeline-director-create", "{'name':'DryRunDirector','assetPath':'" + dryAssetPath + "','createAsset':true,'dryRun':true}");
            Assert.AreEqual(true, dryRun["dryRun"]);
            Assert.AreEqual("DryRunDirector", dryRun["wouldCreateDirector"]);
            Assert.IsNull(GameObject.Find("DryRunDirector"));
            Assert.IsFalse(File.Exists(dryAssetPath));
            Assert.IsFalse(SceneManager.GetActiveScene().isDirty);

            var undoCreated = RunTool("timeline-director-create", "{'name':'UndoTimelineDirector','createAsset':false}");
            Assert.AreEqual(false, undoCreated["dryRun"]);
            Assert.NotNull(GameObject.Find("UndoTimelineDirector"));
            Undo.PerformUndo();
            Assert.IsNull(GameObject.Find("UndoTimelineDirector"));

            var result = CreateTimelineDirectorWithAsset("PreviewTimelineDirector", ChievfxMcpCamerasQaFixture.TimelineAssetPath);
            var directorObject = GameObject.Find("PreviewTimelineDirector");
            Assert.NotNull(directorObject);
            var director = directorObject!.GetComponent<PlayableDirector>();
            Assert.IsTrue(SceneManager.GetActiveScene().isDirty);

            Assert.IsTrue(EditorSceneManager.SaveScene(SceneManager.GetActiveScene(), GeneratedScenePath("TimelinePreviewScene.unity")));
            Assert.IsFalse(SceneManager.GetActiveScene().isDirty);
            director.time = 0.25d;

            var previewDryRun = RunTool("timeline-director-preview", "{'directorPath':'PreviewTimelineDirector','time':1.2,'action':'evaluate','dryRun':true}");
            Assert.AreEqual(true, previewDryRun["dryRun"]);
            Assert.AreEqual(0.25d, director.time, 0.0001d);
            Assert.IsFalse(SceneManager.GetActiveScene().isDirty);

            var preview = RunTool("timeline-director-preview", "{'directorPath':'PreviewTimelineDirector','time':1.2,'action':'evaluate'}");
            Assert.AreEqual(false, preview["dryRun"]);
            Assert.AreEqual(0.25d, Convert.ToDouble(preview["previousTime"]));
            Assert.AreEqual(true, preview["restoredTime"]);
            Assert.AreEqual(0.25d, director.time, 0.0001d);
            StringAssert.Contains("screenshot-camera", (string)preview["visualQaHint"]!);
            Assert.IsTrue(File.Exists(ChievfxMcpCamerasQaFixture.TimelineAssetPath));
            Assert.IsNotNull(Row(result, "target"));
        }

        [Test]
        public void TimelineDirectorResourceCapIsStableAndReadOnly()
        {
            RequireTimeline();

            for (var i = 0; i < 98; i++)
            {
                new GameObject("QaTimelineDirector" + i.ToString("D3")).AddComponent<PlayableDirector>();
            }

            SaveGeneratedScene("TimelineCapScene.unity");
            var directors = Resource("chievfx://extensions/chievfx.cameras/timeline/directors");

            Assert.AreEqual(96, directors["count"]);
            Assert.AreEqual(true, directors["capped"]);
            Assert.AreEqual(96, directors["maxRows"]);
            Assert.AreEqual(96, Rows(directors, "directors").Length);
            Assert.IsFalse(SceneManager.GetActiveScene().isDirty);
        }

        [Test]
        public void CameraQaFixtureBuildsCameraComposedTimelineSceneForScreenshotCamera()
        {
            RequireTimeline();

            ChievfxMcpCamerasQaFixture.BuildScene(saveSceneAsset: false);

            var camera = GameObject.Find(ChievfxMcpCamerasQaFixture.CameraName)!.GetComponent<Camera>();
            Assert.IsNotNull(camera);
            Assert.AreEqual(CameraClearFlags.SolidColor, camera.clearFlags);
            Assert.IsNotNull(GameObject.Find(ChievfxMcpCamerasQaFixture.TargetName));
            Assert.IsNotNull(GameObject.Find(ChievfxMcpCamerasQaFixture.DirectorName)!.GetComponent<PlayableDirector>());
        }

        [Test]
        public void PackagePresentShotSequenceFixtureExposesCameraBrainAndTimelineDetails()
        {
            RequireCinemachineAndTimeline();

            ChievfxMcpCamerasQaFixture.BuildScene(saveSceneAsset: false);

            var cameras = Resource("chievfx://extensions/chievfx.cameras/cinemachine/cameras");
            Assert.AreEqual(96, cameras["maxRows"]);
            Assert.IsTrue(Rows(cameras, "cameras").Any(row => Equals("Ending Wide Shot", row["name"])));
            Assert.IsTrue(Rows(cameras, "cameras").Any(row => Row(row, "lens").ContainsKey("fieldOfView")));
            Assert.IsTrue(Rows(cameras, "cameras").Any(row => Row(row, "target").ContainsKey("path")));

            var brains = Resource("chievfx://extensions/chievfx.cameras/cinemachine/brains");
            Assert.AreEqual(64, brains["maxRows"]);
            Assert.IsTrue(Rows(brains, "brains").Any(row => Equals(ChievfxMcpCamerasQaFixture.CameraName, Row(row, "camera")["name"])));
            Assert.IsTrue(Rows(brains, "brains").Any(row => row.ContainsKey("defaultBlend")));

            var directorDetail = Resource("chievfx://extensions/chievfx.cameras/timeline/director/" + ChievfxMcpCamerasQaFixture.DirectorName);
            var director = Row(directorDetail, "target");
            Assert.IsTrue(Rows(director, "tracks").Any(row => Convert.ToString(row["type"])!.Contains("CinemachineTrack", StringComparison.Ordinal)));
            Assert.IsTrue(Rows(director, "clips").Any(row => Convert.ToString(row["assetType"])!.Contains("CinemachineShot", StringComparison.Ordinal)));
            Assert.IsTrue(Rows(director, "bindings").Any(row => Row(row, "boundObject").ContainsKey("type")));
        }

        [Test]
        public void SequencerCreateDryRunUndoAndReadOnlyResources()
        {
            RequireSequencerCamera();

            new GameObject("QaSequencerTarget").transform.position = new Vector3(0f, 1f, 0f);
            SaveGeneratedScene("SequencerDryRunScene.unity");

            var timelineAssetsBeforeDryRun = TimelineAssetPaths();
            var dryRun = RunTool("cinemachine-sequencer-create", "{'name':'DryRunSequencer','targetPath':'QaSequencerTarget','loop':false,'shots':[{'name':'DryWide','hold':1.25,'fieldOfView':35,'blendStyle':'Cut','blendTime':0},{'name':'DryTight','hold':0.75,'fieldOfView':22,'blendStyle':'EaseInOut','blendTime':0.35}],'dryRun':true}");
            Assert.AreEqual(true, dryRun["dryRun"]);
            Assert.AreEqual("DryRunSequencer", dryRun["wouldCreateSequencer"]);
            Assert.AreEqual(2, dryRun["shotCount"]);
            Assert.IsNull(GameObject.Find("DryRunSequencer"));
            Assert.IsFalse(SceneManager.GetActiveScene().isDirty);
            AssertTimelineAssetSetUnchanged(timelineAssetsBeforeDryRun);

            var timelineAssetsBeforeCreate = TimelineAssetPaths();
            var created = RunTool("cinemachine-sequencer-create", "{'name':'UndoSequencer','targetPath':'QaSequencerTarget','loop':false,'shots':[{'name':'UndoWide','hold':1.25,'fieldOfView':35,'blendStyle':'Cut','blendTime':0},{'name':'UndoTight','hold':0.75,'fieldOfView':22,'blendStyle':'EaseInOut','blendTime':0.35}]}");
            Assert.AreEqual(false, created["dryRun"]);
            Assert.NotNull(GameObject.Find("UndoSequencer"));
            var detail = Row(created, "target");
            Assert.AreEqual(false, detail["loop"]);
            Assert.AreEqual(2, detail["instructionCount"]);
            Assert.AreEqual(2, Rows(detail, "instructions").Length);
            Assert.IsTrue(SceneManager.GetActiveScene().isDirty);
            AssertTimelineAssetSetUnchanged(timelineAssetsBeforeCreate);

            var sequencerType = FindLoadedType("Unity.Cinemachine.CinemachineSequencerCamera");
            var sequencer = GameObject.Find("UndoSequencer")!.GetComponent(sequencerType);
            Assert.IsTrue(ClearFirstInstructionCamera(sequencer));
            var warningDetail = Row(Resource("chievfx://extensions/chievfx.cameras/cinemachine/sequencer/UndoSequencer"), "target");
            Assert.IsTrue(StringArray(warningDetail, "warnings").Any(warning => warning.Contains("no child camera assigned", StringComparison.Ordinal)));

            Undo.PerformUndo();
            Assert.IsNull(GameObject.Find("UndoSequencer"));

            SaveGeneratedScene("SequencerReadOnlyScene.unity");
            for (var i = 0; i < 66; i++)
            {
                new GameObject("QaSequencerCap" + i.ToString("D3")).AddComponent(sequencerType);
            }

            SaveGeneratedScene("SequencerCapScene.unity");
            var sequencers = Resource("chievfx://extensions/chievfx.cameras/cinemachine/sequencers");
            Assert.AreEqual(64, sequencers["count"]);
            Assert.AreEqual(true, sequencers["capped"]);
            Assert.AreEqual(64, sequencers["maxRows"]);
            Assert.AreEqual(64, Rows(sequencers, "sequencers").Length);
            Assert.IsFalse(SceneManager.GetActiveScene().isDirty);
        }

        [Test]
        public void SequencerCameraQaFixtureBuildsVisibleCompositionForScreenshotCamera()
        {
            RequireSequencerCamera();

            var timelineAssetsBefore = TimelineAssetPaths();
            ChievfxMcpCamerasQaFixture.BuildSequencerCameraScene(saveSceneAsset: false);

            var camera = GameObject.Find(ChievfxMcpCamerasQaFixture.SequencerGameplayCameraName)!.GetComponent<Camera>();
            Assert.IsNotNull(camera);
            Assert.AreEqual(CameraClearFlags.SolidColor, camera.clearFlags);
            Assert.IsNotNull(GameObject.Find(ChievfxMcpCamerasQaFixture.SequencerTargetName));
            Assert.IsNotNull(GameObject.Find("QaCameraGround"));
            Assert.IsNotNull(GameObject.Find("QaCameraKeyLight")!.GetComponent<Light>());

            var status = Resource("chievfx://extensions/chievfx.cameras/status");
            var brainType = FindLoadedType("Unity.Cinemachine.CinemachineBrain");
            Assert.IsNotNull(GameObject.Find(ChievfxMcpCamerasQaFixture.SequencerGameplayCameraName)!.GetComponent(brainType));

            var sequencer = GameObject.Find(ChievfxMcpCamerasQaFixture.SequencerName);
            Assert.IsNotNull(sequencer);
            Assert.IsNotNull(sequencer!.transform.Find(ChievfxMcpCamerasQaFixture.SequencerWideShotName));
            Assert.IsNotNull(sequencer.transform.Find(ChievfxMcpCamerasQaFixture.SequencerTightShotName));
            Assert.IsNotNull(sequencer.transform.Find(ChievfxMcpCamerasQaFixture.SequencerBlendShotName));

            SaveGeneratedScene("SequencerFixtureReadOnlyScene.unity");
            var sequencers = Resource("chievfx://extensions/chievfx.cameras/cinemachine/sequencers");
            Assert.AreEqual(64, sequencers["maxRows"]);
            Assert.IsTrue(Rows(sequencers, "sequencers").Any(row => Equals(ChievfxMcpCamerasQaFixture.SequencerName, row["name"])));
            Assert.IsFalse(SceneManager.GetActiveScene().isDirty);

            var detail = Row(Resource("chievfx://extensions/chievfx.cameras/cinemachine/sequencer/" + Uri.EscapeDataString(ChievfxMcpCamerasQaFixture.SequencerName)), "target");
            Assert.AreEqual(false, detail["loop"]);
            Assert.AreEqual(3, detail["instructionCount"]);
            var instructions = Rows(detail, "instructions");
            Assert.AreEqual(3, instructions.Length);
            Assert.AreEqual(1.25d, Convert.ToDouble(instructions[0]["holdSeconds"]), 0.001d);
            Assert.AreEqual(0.8d, Convert.ToDouble(instructions[1]["holdSeconds"]), 0.001d);
            Assert.AreEqual(1.1d, Convert.ToDouble(instructions[2]["holdSeconds"]), 0.001d);
            Assert.IsTrue(instructions.All(instruction => Row(instruction, "blend").ContainsKey("summary")));
            Assert.IsTrue(instructions.All(instruction => Convert.ToString(instruction["summary"])!.Contains("Hold", StringComparison.Ordinal)));
            StringAssert.Contains("screenshot-camera", Convert.ToString(detail["previewQaHint"]));
            AssertTimelineAssetSetUnchanged(timelineAssetsBefore);
            Assert.AreEqual(true, Row(status, "sequencerCamera")["available"]);
        }

        [Test]
        public void SplinesDollyToolBindsExistingCameraAndSplineWithDryRunUndo()
        {
            RequireSplinesDolly();

            RunTool("cinemachine-create", "{'name':'QaDollyCamera','dryRun':false}");
            var splineContainerType = FindLoadedType("UnityEngine.Splines.SplineContainer");
            var splineRollType = FindLoadedType("Unity.Cinemachine.CinemachineSplineRoll");
            var splineObject = new GameObject("QaDollySpline");
            splineObject.AddComponent(splineContainerType);
            splineObject.AddComponent(splineRollType);
            SaveGeneratedScene("SplinesDollyScene.unity");

            var dollyType = FindLoadedType("Unity.Cinemachine.CinemachineSplineDolly");
            var cameraObject = GameObject.Find("QaDollyCamera");
            Assert.IsNotNull(cameraObject);
            Assert.IsNull(cameraObject!.GetComponent(dollyType));

            var dryRun = RunTool("cinemachine-spline-dolly-set", "{'targetPath':'QaDollyCamera','splinePath':'QaDollySpline','position':0.5,'positionUnits':'Normalized','dryRun':true}");
            Assert.AreEqual(true, dryRun["dryRun"]);
            Assert.AreEqual(true, dryRun["wouldAddSplineDolly"]);
            Assert.AreEqual(false, dryRun["geometryMutation"]);
            Assert.IsNull(cameraObject.GetComponent(dollyType));
            Assert.IsFalse(SceneManager.GetActiveScene().isDirty);

            var created = RunTool("cinemachine-spline-dolly-set", "{'targetPath':'QaDollyCamera','splinePath':'QaDollySpline','position':0.5,'positionUnits':'Normalized','autoDollyEnabled':false,'dryRun':false}");
            Assert.AreEqual(false, created["dryRun"]);
            Assert.AreEqual(true, created["addedSplineDolly"]);
            Assert.AreEqual(false, created["geometryMutation"]);
            Assert.IsNotNull(cameraObject.GetComponent(dollyType));
            Assert.IsTrue(SceneManager.GetActiveScene().isDirty);

            var inventory = Resource("chievfx://extensions/chievfx.cameras/cinemachine/splines-dolly");
            Assert.AreEqual(64, inventory["maxRows"]);
            Assert.IsTrue(Rows(inventory, "splineContainers").Any(row => Equals("QaDollySpline", row["path"]) && Equals(true, row["hasSplineRoll"])));
            Assert.IsTrue(Rows(inventory, "cameras").Any(row => Equals("QaDollyCamera", row["path"])));
            var dollyRow = Rows(inventory, "splinesDollies").First(row => Equals("QaDollyCamera", row["path"]));
            Assert.IsTrue(Row(dollyRow, "cameraPosition").ContainsKey("value"));
            Assert.IsTrue(StringArray(dollyRow, "warnings").Any(warning => warning.Contains("no target", StringComparison.Ordinal)));
            StringAssert.Contains("screenshot-camera", Convert.ToString(inventory["visualQaHint"]));

            Undo.PerformUndo();
            Assert.IsNull(cameraObject.GetComponent(dollyType));
        }

        [Test]
        public void BlenderSettingsToolDryRunAssignAndResourceWarnings()
        {
            RequireBlenderSettings();

            RunTool("cinemachine-create", "{'name':'QaBlendFrom','dryRun':false}");
            RunTool("cinemachine-create", "{'name':'QaBlendTo','dryRun':false}");
            var brainObject = new GameObject("QaBlendGameplayCamera");
            brainObject.AddComponent<Camera>();
            RunTool("brain-ensure", "{'cameraPath':'QaBlendGameplayCamera','dryRun':false}");
            SaveGeneratedScene("BlenderSettingsScene.unity");

            var brainType = FindLoadedType("Unity.Cinemachine.CinemachineBrain");
            var brain = brainObject.GetComponent(brainType);
            Assert.IsNotNull(brain);
            Selection.activeGameObject = brainObject;

            var dryAssetPath = ChievfxMcpCamerasQaFixture.GeneratedFolder + "/DryRunBlends.asset";
            var dryRun = RunTool("cinemachine-blender-settings-set", "{'assetPath':'" + dryAssetPath + "','assignToSelectedBrain':true,'blends':[{'from':'QaBlendFrom','to':'ANY CAMERA','style':'EaseInOut','time':0.4},{'from':'ANY CAMERA','to':'QaBlendTo','style':'Cut','time':0},{'from':'ANY CAMERA','to':'QaBlendTo','style':'Cut','time':0},{'from':'MissingCamera','to':'QaBlendTo','style':'HardOut','time':0.25}],'dryRun':true}");
            Assert.AreEqual(true, dryRun["dryRun"]);
            Assert.AreEqual(true, dryRun["wouldCreateAsset"]);
            Assert.AreEqual(true, dryRun["wouldAssignSelectedBrain"]);
            Assert.AreEqual(4, dryRun["blendCount"]);
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(dryAssetPath));
            Assert.IsFalse(SceneManager.GetActiveScene().isDirty);
            Assert.IsTrue(StringArray(dryRun, "warnings").Any(warning => warning.Contains("duplicate", StringComparison.Ordinal)));
            Assert.IsTrue(StringArray(dryRun, "warnings").Any(warning => warning.Contains("equal specificity", StringComparison.Ordinal)));
            Assert.IsTrue(StringArray(dryRun, "warnings").Any(warning => warning.Contains("MissingCamera", StringComparison.Ordinal)));

            var assetPath = ChievfxMcpCamerasQaFixture.GeneratedFolder + "/GameplayBlends.asset";
            var created = RunTool("cinemachine-blender-settings-set", "{'assetPath':'" + assetPath + "','assignToSelectedBrain':true,'blends':[{'from':'QaBlendFrom','to':'QaBlendTo','style':'EaseInOut','time':0.6},{'from':'ANY CAMERA','to':'QaBlendTo','style':'Cut','time':0}],'dryRun':false}");
            Assert.AreEqual(false, created["dryRun"]);
            Assert.AreEqual(true, created["createdAsset"]);
            Assert.AreEqual(assetPath, created["assetCleanupPath"]);
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            Assert.IsNotNull(asset);
            Assert.AreEqual(assetPath, AssetDatabase.GetAssetPath((UnityEngine.Object)GetMember(brain!, "CustomBlends")!));

            var resource = Resource("chievfx://extensions/chievfx.cameras/cinemachine/blender-settings");
            Assert.AreEqual(64, resource["maxRows"]);
            Assert.AreEqual(32, resource["maxBlendEntries"]);
            Assert.IsTrue(Rows(resource, "assets").Any(row => Equals(assetPath, row["path"])));
            Assert.IsTrue(Rows(resource, "brainCustomBlends").Any(row => Equals("QaBlendGameplayCamera", row["name"])));
            StringAssert.Contains("Timeline", Convert.ToString(resource["timelineNote"]));
        }

        private static Dictionary<string, object?> Resource(string uri)
        {
            return (Dictionary<string, object?>)ChievfxMcpCamerasExtension.ReadResourceForTests(uri)!;
        }

        private static Dictionary<string, object?> RunTool(string toolName, string argsJson)
        {
            return (Dictionary<string, object?>)ChievfxMcpCamerasExtension.RunToolForTests(toolName, argsJson)!;
        }

        private static Dictionary<string, object?> CreateTimelineDirectorWithAsset(string name, string assetPath)
        {
            return RunTool("timeline-director-create", "{'name':'" + name + "','assetPath':'" + assetPath + "','createAsset':true}");
        }

        private static Dictionary<string, object?> Row(Dictionary<string, object?> source, string key)
        {
            return (Dictionary<string, object?>)source[key]!;
        }

        private static Dictionary<string, object?>[] Rows(Dictionary<string, object?> source, string key)
        {
            return source[key] switch
            {
                Dictionary<string, object?>[] rows => rows,
                object[] rows => rows.Cast<Dictionary<string, object?>>().ToArray(),
                _ => Array.Empty<Dictionary<string, object?>>(),
            };
        }

        private static string[] StringArray(Dictionary<string, object?> source, string key)
        {
            return source[key] switch
            {
                string[] strings => strings,
                object[] objects => objects.Cast<string>().ToArray(),
                _ => Array.Empty<string>(),
            };
        }

        private static string[] TimelineAssetPaths()
        {
            return AssetDatabase.FindAssets("t:TimelineAsset")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
        }

        private static void AssertTimelineAssetSetUnchanged(string[] before)
        {
            CollectionAssert.AreEqual(before, TimelineAssetPaths());
        }

        private static Type FindLoadedType(string fullName)
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(fullName, throwOnError: false))
                .FirstOrDefault(type => type != null)
                ?? throw new InvalidOperationException("Loaded type not found: " + fullName);
        }

        private static bool ClearFirstInstructionCamera(Component sequencer)
        {
            if (GetMember(sequencer, "Instructions") is not IList instructions || instructions.Count == 0)
            {
                return false;
            }

            var instruction = instructions[0];
            if (instruction == null || !SetMember(instruction, "Camera", null))
            {
                return false;
            }

            instructions[0] = instruction;
            SetMember(sequencer, "Instructions", instructions);
            return true;
        }

        private static object? GetMember(object target, string name)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = target.GetType();
            return type.GetProperty(name, flags)?.GetValue(target)
                ?? type.GetField(name, flags)?.GetValue(target);
        }

        private static bool SetMember(object target, string name, object? value)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = target.GetType();
            var property = type.GetProperty(name, flags);
            if (property is { CanWrite: true })
            {
                property.SetValue(target, value);
                return true;
            }

            var field = type.GetField(name, flags);
            if (field == null)
            {
                return false;
            }

            field.SetValue(target, value);
            return true;
        }

        private static void AssertUnavailable(Dictionary<string, object?> envelope)
        {
            Assert.AreEqual(false, envelope["ok"]);
            Assert.AreEqual(true, envelope["unavailable"]);
            Assert.AreEqual("chievfx://extensions/chievfx.cameras/status", envelope["statusUri"]);
            Assert.IsTrue(Row(envelope, "status").ContainsKey("cinemachine"));
            Assert.IsTrue(Row(envelope, "status").ContainsKey("timeline"));
        }

        private static void AssertCinemachineUnavailable(Dictionary<string, object?> envelope)
        {
            AssertUnavailable(envelope);
            StringAssert.Contains("Cinemachine 3.x Unity.Cinemachine.CinemachineCamera", Convert.ToString(envelope["message"]));
            Assert.AreEqual("absent", Row(Row(envelope, "status"), "cinemachine")["apiFamily"]);
        }

        private static void AssertSequencerUnavailable(Dictionary<string, object?> envelope)
        {
            AssertUnavailable(envelope);
            StringAssert.Contains("CM3 Sequencer Camera", Convert.ToString(envelope["message"]));
            Assert.IsTrue(Row(Row(envelope, "status"), "sequencerCamera").ContainsKey("sequencerCameraTypeLoaded"));
        }

        private static void AssertAdvancedHelperUnavailable(Dictionary<string, object?> envelope, string statusKey)
        {
            AssertUnavailable(envelope);
            StringAssert.Contains("requires CM3", Convert.ToString(envelope["message"]));
            Assert.IsTrue(Row(Row(envelope, "status"), statusKey).ContainsKey("helperTypeLoaded"));
            Assert.IsFalse(SceneManager.GetActiveScene().isDirty);
        }

        private static void RequireTimeline()
        {
            var status = Resource("chievfx://extensions/chievfx.cameras/status");
            if (!Equals(true, Row(status, "timeline")["available"]))
            {
                Assert.Ignore((string)Row(status, "timeline")["reason"]!);
            }
        }

        private static void RequireCinemachineAndTimeline()
        {
            var status = Resource("chievfx://extensions/chievfx.cameras/status");
            if (!Equals(true, Row(status, "cinemachine")["available"]) || !Equals(true, Row(status, "timeline")["available"]))
            {
                Assert.Ignore("Cinemachine and Timeline are not both available in this project. See CamerasQaFixture.md for package-present QA.");
            }
        }

        private static void RequireSequencerCamera()
        {
            var status = Resource("chievfx://extensions/chievfx.cameras/status");
            if (!Equals(true, Row(status, "sequencerCamera")["available"]))
            {
                Assert.Ignore((string)Row(status, "sequencerCamera")["reason"]!);
            }
        }

        private static void RequireBlenderSettings()
        {
            var resource = Resource("chievfx://extensions/chievfx.cameras/cinemachine/blender-settings");
            if (resource.TryGetValue("ok", out var ok) && Equals(false, ok) && Equals(true, resource["unavailable"]))
            {
                Assert.Ignore(Convert.ToString(resource["message"]));
            }
        }

        private static void RequireSplinesDolly()
        {
            var resource = Resource("chievfx://extensions/chievfx.cameras/cinemachine/splines-dolly");
            if (resource.TryGetValue("ok", out var ok) && Equals(false, ok) && Equals(true, resource["unavailable"]))
            {
                Assert.Ignore(Convert.ToString(resource["message"]));
            }
        }

        private static void SaveGeneratedScene(string sceneFileName)
        {
            Assert.IsTrue(EditorSceneManager.SaveScene(SceneManager.GetActiveScene(), GeneratedScenePath(sceneFileName)));
        }

        private static string GeneratedScenePath(string sceneFileName)
        {
            Directory.CreateDirectory(ChievfxMcpCamerasQaFixture.GeneratedFolder);
            return ChievfxMcpCamerasQaFixture.GeneratedFolder + "/" + sceneFileName;
        }
    }
}
