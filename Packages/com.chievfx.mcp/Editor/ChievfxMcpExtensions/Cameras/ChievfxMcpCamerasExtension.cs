#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Chievfx.Mcp.Editor;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using PackageManagerPackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Chievfx.Mcp.Extensions.Cameras
{
    [InitializeOnLoad]
    internal static partial class ChievfxMcpCamerasExtension
    {
        private const string ExtensionId = "chievfx.cameras";
        private const string Category = "cinemachine-and-timeline";
        private const string GameFeelCategory = "game-feel";
        private const string CinemachinePackageName = "com.unity.cinemachine";
        private const string TimelinePackageName = "com.unity.timeline";
        private const string SplinesPackageName = "com.unity.splines";
        private const string InputSystemPackageName = "com.unity.inputsystem";
        private const string UriPrefix = "chievfx://extensions/chievfx.cameras/";
        private const string StatusUri = UriPrefix + "status";
        private const string CinemachineCamerasUri = UriPrefix + "cinemachine/cameras";
        private const string CinemachineCameraDetailPrefix = UriPrefix + "cinemachine/camera/";
        private const string CinemachineBrainsUri = UriPrefix + "cinemachine/brains";
        private const string CinemachineBrainDetailPrefix = UriPrefix + "cinemachine/brain/";
        private const string CinemachineSequencersUri = UriPrefix + "cinemachine/sequencers";
        private const string CinemachineSequencerDetailPrefix = UriPrefix + "cinemachine/sequencer/";
        private const string CinemachineSplinesDollyUri = UriPrefix + "cinemachine/splines-dolly";
        private const string CinemachineInputAxisControllersUri = UriPrefix + "cinemachine/input-axis-controllers";
        private const string CinemachineBlenderSettingsUri = UriPrefix + "cinemachine/blender-settings";
        private const string CinemachineImpulseUri = UriPrefix + "cinemachine/impulse";
        private const string CinemachineConfiner2DUri = UriPrefix + "cinemachine/confiner-2d";
        private const string CinemachineConfiner3DUri = UriPrefix + "cinemachine/confiner-3d";
        private const string TimelineDirectorsUri = UriPrefix + "timeline/directors";
        private const string TimelineDirectorDetailPrefix = UriPrefix + "timeline/director/";
        private const string TimelineAssetsUri = UriPrefix + "timeline/assets";
        private const string TimelineAssetDetailPrefix = UriPrefix + "timeline/asset/";
        private const string CinemachineApiFamilyAbsent = "absent";
        private const string CinemachineApiFamilyCm3 = "cm3";
        private const string CinemachineApiFamilyCm3LegacyObsolete = "cm3LegacyObsolete";
        private const string CinemachineApiFamilyCm2 = "cm2";
        private const int MaxCameraRows = 96;
        private const int MaxBrainRows = 64;
        private const int MaxSequencerRows = 64;
        private const int MaxSequencerInstructionRows = 96;
        private const int MaxAdvancedHelperRows = 64;
        private const int MaxCustomBlendEntries = 32;
        private const int MaxDirectorRows = 96;
        private const int MaxAssetRows = 128;
        private const int MaxClipRows = 96;

#if CHIEVFX_MCP_HAS_CINEMACHINE
        private const bool CinemachineVersionDefineActive = true;
#else
        private const bool CinemachineVersionDefineActive = false;
#endif

#if CHIEVFX_MCP_HAS_TIMELINE
        private const bool TimelineVersionDefineActive = true;
#else
        private const bool TimelineVersionDefineActive = false;
#endif

#if CHIEVFX_MCP_HAS_SPLINES
        private const bool SplinesVersionDefineActive = true;
#else
        private const bool SplinesVersionDefineActive = false;
#endif

        static ChievfxMcpCamerasExtension()
        {
            ChievfxMcpExtensionRegistry.RegisterExtension(CreateDescriptor());
        }

        public static object? ReadResourceForTests(string uri)
        {
            return ReadResource(uri);
        }

        public static object? RunToolForTests(string toolName, string argsJson)
        {
            return RunTool(toolName, string.IsNullOrWhiteSpace(argsJson) ? new JObject() : JObject.Parse(argsJson));
        }

        public static string ClassifyCinemachineApiFamilyForTests(bool packageInstalled, string? packageVersion, bool hasCm3Camera, bool hasCm3LegacyObsoleteCamera, bool hasCm2VirtualCamera)
        {
            return DetermineCinemachineApiFamily(
                packageInstalled,
                packageVersion,
                hasCm3Camera ? typeof(GameObject) : null,
                hasCm3LegacyObsoleteCamera ? typeof(GameObject) : null,
                hasCm2VirtualCamera ? typeof(GameObject) : null);
        }

        public static Dictionary<string, object?> CreateSequencerGateForTests(bool packageInstalled, string? packageVersion, string apiFamily, bool versionDefineActive, bool hasSequencerCamera, bool hasInstruction, bool hasCamera, bool hasVirtualCameraBase, bool hasBlendDefinition, bool hasBrain)
        {
            var status = new SequencerCameraStatus(
                packageInstalled,
                packageVersion,
                apiFamily,
                versionDefineActive,
                hasSequencerCamera ? typeof(GameObject) : null,
                hasInstruction ? typeof(GameObject) : null,
                hasCamera ? typeof(GameObject) : null,
                hasVirtualCameraBase ? typeof(GameObject) : null,
                hasBlendDefinition ? typeof(GameObject) : null,
                hasBrain ? typeof(GameObject) : null);
            return status.ToDictionary();
        }

        public static Dictionary<string, object?> CreateAdvancedHelperGateForTests(string key, bool packageInstalled, string? packageVersion, string apiFamily, bool versionDefineActive, bool hasHelperType, string? optionalPackageName, bool optionalPackageInstalled, string? optionalPackageVersion, bool hasOptionalType, bool optionalVersionDefineActive = true)
        {
            var status = new AdvancedHelperStatus(
                key,
                "Test helper",
                packageInstalled,
                packageVersion,
                apiFamily,
                versionDefineActive,
                hasHelperType ? typeof(GameObject) : null,
                "Test.Helper",
                optionalPackageName,
                optionalPackageInstalled,
                optionalPackageVersion,
                hasOptionalType ? typeof(GameObject) : null,
                optionalPackageName == null ? null : "Test.Optional",
                optionalVersionDefineActive: optionalVersionDefineActive);
            return status.ToDictionary();
        }

        internal static object? RunToolForTests(string toolName, JToken args)
        {
            return RunTool(toolName, args);
        }

        private static ChievfxMcpExtensionDescriptor CreateDescriptor()
        {
            var status = GetDependencyStatus();
            var descriptor = new ChievfxMcpExtensionDescriptor
            {
                Id = ExtensionId,
                DisplayName = "ChievFX MCP Cameras and Cutscenes",
                Version = "0.1.0",
                Description = status.AnyAvailable
                    ? "First-party optional helpers for Cinemachine camera authoring and Timeline cutscene setup."
                    : "First-party camera/cutscene helpers unavailable until Cinemachine and/or Timeline packages are installed, compiled, and loaded.",
                ToolRunner = RunTool,
                ResourceReader = ReadResource,
            };

            descriptor.Resources.Add(Resource("cameras-status", StatusUri, "Camera/cutscene extension status", "Reports Cinemachine and Timeline package/type/versionDefine availability."));
            descriptor.Resources.Add(Resource("cameras-cinemachine-cameras", CinemachineCamerasUri, "Cinemachine camera summary", "Compact summary of CinemachineCamera components in current scene or prefab stage."));
            descriptor.Resources.Add(Resource("cameras-cinemachine-brains", CinemachineBrainsUri, "Cinemachine brain summary", "Compact summary of CinemachineBrain components on Unity Cameras."));
            descriptor.Resources.Add(Resource("cameras-cinemachine-sequencers", CinemachineSequencersUri, "Cinemachine Sequencer Camera summary", "Compact CM3-only summary of CinemachineSequencerCamera components in current scene or prefab stage."));
            descriptor.Resources.Add(Resource("cameras-cinemachine-splines-dolly", CinemachineSplinesDollyUri, "Cinemachine Splines Dolly inventory", "Read-only capped CM3 inventory of CinemachineSplineDolly components, gated by Splines package/types."));
            descriptor.Resources.Add(Resource("cameras-cinemachine-input-axis-controllers", CinemachineInputAxisControllersUri, "Cinemachine InputAxisController inventory", "Read-only capped CM3 inventory of CinemachineInputAxisController components with Input System rows only when Input System types are loaded."));
            descriptor.Resources.Add(Resource("cameras-cinemachine-blender-settings", CinemachineBlenderSettingsUri, "Cinemachine Blender Settings inventory", "Read-only capped CM3 inventory of CinemachineBlenderSettings assets and brain custom blends."));
            descriptor.Resources.Add(Resource("cameras-cinemachine-impulse", CinemachineImpulseUri, "Cinemachine Impulse inventory", "Read-only capped CM3 inventory of Cinemachine impulse sources and listeners."));
            descriptor.Resources.Add(Resource("cameras-cinemachine-confiner-2d", CinemachineConfiner2DUri, "Cinemachine Confiner2D inventory", "Read-only capped CM3 inventory of CinemachineConfiner2D components."));
            descriptor.Resources.Add(Resource("cameras-cinemachine-confiner-3d", CinemachineConfiner3DUri, "Cinemachine Confiner3D inventory", "Read-only capped CM3 inventory of CinemachineConfiner3D components."));
            descriptor.Resources.Add(Resource("cameras-timeline-directors", TimelineDirectorsUri, "Timeline director summary", "Compact summary of PlayableDirector components using Timeline assets."));
            descriptor.Resources.Add(Resource("cameras-timeline-assets", TimelineAssetsUri, "Timeline asset summary", "Compact project summary of TimelineAsset assets."));
            descriptor.ResourceTemplates.Add(Template("cameras-cinemachine-camera-detail", CinemachineCameraDetailPrefix + "{pathOrInstanceId}", "Cinemachine camera detail", "CinemachineCamera detail by instance id or URL-encoded transform path."));
            descriptor.ResourceTemplates.Add(Template("cameras-cinemachine-brain-detail", CinemachineBrainDetailPrefix + "{pathOrInstanceId}", "Cinemachine brain detail", "CinemachineBrain detail by instance id or URL-encoded transform path."));
            descriptor.ResourceTemplates.Add(Template("cameras-cinemachine-sequencer-detail", CinemachineSequencerDetailPrefix + "{pathOrInstanceId}", "Cinemachine Sequencer Camera detail", "CinemachineSequencerCamera detail by instance id or URL-encoded transform path."));
            descriptor.ResourceTemplates.Add(Template("cameras-timeline-director-detail", TimelineDirectorDetailPrefix + "{pathOrInstanceId}", "Timeline director detail", "PlayableDirector detail by instance id or URL-encoded transform path."));
            descriptor.ResourceTemplates.Add(Template("cameras-timeline-asset-detail", TimelineAssetDetailPrefix + "{guidOrPath}", "Timeline asset detail", "TimelineAsset detail by GUID or URL-encoded asset path."));

            descriptor.Prompts.Add(new ChievfxMcpPromptDescriptor
            {
                Name = "cameras-ending-session-slowmo-zoom",
                Title = "Author ending-session slow-mo zoom",
                Description = "Workflow prompt for safe Cinemachine/Timeline setup of a two-shot slow-mo ending beat.",
                Category = Category,
                Arguments = new JArray
                {
                    new JObject
                    {
                        ["name"] = "goal",
                        ["description"] = "Optional subject, timing, and camera-feel notes.",
                        ["required"] = false,
                    },
                },
                StaticText =
                    "Build editor-authored camera/cutscene state, not hidden runtime globals. Read chievfx.cameras status/resources first, ensure a CinemachineBrain on the gameplay Camera, create a wide shot and a tighter FOV/distance zoom CinemachineCamera, then create a two-shot Timeline sequence with optional clip start/duration/overlap. Use screenshot-camera for visual QA. Runtime slow-mo must be integrated by game code around the ending session, e.g. tween Time.timeScale and Time.fixedDeltaTime during the beat, then restore both values. Goal: {goal}",
            });

            descriptor.Prompts.Add(new ChievfxMcpPromptDescriptor
            {
                Name = "gamefeel-ending-session-slowmo",
                Title = "Guide ending-session slow motion",
                Description = "Prompt-only guidance for user-owned Unity slow motion around an ending-session beat.",
                Category = GameFeelCategory,
                Arguments = new JArray
                {
                    new JObject
                    {
                        ["name"] = "goal",
                        ["description"] = "Optional gameplay, camera, and timing goals for the ending-session beat.",
                        ["required"] = false,
                    },
                },
                StaticText =
                    "Design ending-session slow motion as user-owned runtime code. MCP must not add hidden MonoBehaviours, mutate scenes automatically, or change Time.timeScale/Time.fixedDeltaTime globals. The user chooses the owner: session manager, ending controller, Timeline signal receiver, or state machine. Goal: {goal}\n\n" +
                    "Workflow:\n" +
                    "1. Identify the exact event that starts and cancels the ending session.\n" +
                    "2. Put the slow-motion coroutine on an explicit project-owned object.\n" +
                    "3. Capture original Time.timeScale and Time.fixedDeltaTime before changing them.\n" +
                    "4. Drive ramp/hold/restore with unscaled timing so the sequence is not slowed by itself.\n" +
                    "5. Restore both values on normal completion, cancel, owner disable/destroy, scene unload, or fail-safe timeout.\n" +
                    "6. Author camera movement, Timeline shots, animation beats, and audio transitions separately; let code only own global time.\n\n" +
                    "User-owned coroutine snippet:\n" +
                    "```csharp\n" +
                    "private Coroutine endingSlowMo;\n" +
                    "private float savedTimeScale;\n" +
                    "private float savedFixedDeltaTime;\n" +
                    "private bool hasSavedTimeScale;\n\n" +
                    "public void StartEndingSlowMo()\n" +
                    "{{\n" +
                    "    StopEndingSlowMo();\n" +
                    "    endingSlowMo = StartCoroutine(EndingSlowMoRoutine(0.15f, 0.35f, 1.25f, adjustFixedDeltaTime: true));\n" +
                    "}}\n\n" +
                    "public void StopEndingSlowMo()\n" +
                    "{{\n" +
                    "    if (endingSlowMo != null)\n" +
                    "    {{\n" +
                    "        StopCoroutine(endingSlowMo);\n" +
                    "        endingSlowMo = null;\n" +
                    "    }}\n\n" +
                    "    RestoreTimeScale();\n" +
                    "}}\n\n" +
                    "private IEnumerator EndingSlowMoRoutine(float targetScale, float rampSeconds, float holdSeconds, bool adjustFixedDeltaTime)\n" +
                    "{{\n" +
                    "    savedTimeScale = Time.timeScale;\n" +
                    "    savedFixedDeltaTime = Time.fixedDeltaTime;\n" +
                    "    hasSavedTimeScale = true;\n\n" +
                    "    try\n" +
                    "    {{\n" +
                    "        for (var t = 0f; t < rampSeconds; t += Time.unscaledDeltaTime)\n" +
                    "        {{\n" +
                    "            var k = rampSeconds <= 0f ? 1f : Mathf.Clamp01(t / rampSeconds);\n" +
                    "            var scale = Mathf.Lerp(savedTimeScale, targetScale, k);\n" +
                    "            Time.timeScale = scale;\n" +
                    "            if (adjustFixedDeltaTime)\n" +
                    "            {{\n" +
                    "                Time.fixedDeltaTime = savedFixedDeltaTime * scale;\n" +
                    "            }}\n" +
                    "            yield return null;\n" +
                    "        }}\n\n" +
                    "        Time.timeScale = targetScale;\n" +
                    "        if (adjustFixedDeltaTime)\n" +
                    "        {{\n" +
                    "            Time.fixedDeltaTime = savedFixedDeltaTime * targetScale;\n" +
                    "        }}\n\n" +
                    "        yield return new WaitForSecondsRealtime(holdSeconds);\n" +
                    "    }}\n" +
                    "    finally\n" +
                    "    {{\n" +
                    "        endingSlowMo = null;\n" +
                    "        RestoreTimeScale();\n" +
                    "    }}\n" +
                    "}}\n\n" +
                    "private void RestoreTimeScale()\n" +
                    "{{\n" +
                    "    if (!hasSavedTimeScale)\n" +
                    "    {{\n" +
                    "        return;\n" +
                    "    }}\n\n" +
                    "    Time.timeScale = savedTimeScale;\n" +
                    "    Time.fixedDeltaTime = savedFixedDeltaTime;\n" +
                    "    hasSavedTimeScale = false;\n" +
                    "}}\n\n" +
                    "private void OnDisable()\n" +
                    "{{\n" +
                    "    StopEndingSlowMo();\n" +
                    "}}\n" +
                    "```\n\n" +
                    "Restore policy: always restore captured originals, not assumed defaults like 1.0 or 0.02. If another system may own time scale too, centralize ownership or use a stack/service; do not let two controllers fight over Time globals.\n\n" +
                    "adjustFixedDeltaTime tradeoff: scaling fixedDeltaTime keeps physics step density visually smoother in slow motion, but increases physics work per real second and can affect determinism. Leaving fixedDeltaTime unchanged is cheaper and more stable, but physics motion may look coarse during very low timeScale. Pick intentionally per project.\n\n" +
                    "Timing caveats: Time.deltaTime, WaitForSeconds, Animator default update, and many tweens are scaled by Time.timeScale. Use Time.unscaledDeltaTime, WaitForSecondsRealtime, unscaled tween update modes, or AnimatorUpdateMode.UnscaledTime for UI/camera/audio transitions that must keep real-time pacing.\n\n" +
                    "Physics/coroutine/audio/animation caveats: FixedUpdate frequency follows fixedDeltaTime; Rigidbody interactions can feel different at very low scales. Coroutines waiting on scaled time may stall. Audio is not automatically pitched down by Time.timeScale; drive AudioSource.pitch or AudioMixer snapshots if desired, choose AudioMixer.updateMode / AudioMixerUpdateMode deliberately so snapshot timing follows the intended scaled or unscaled clock, then restore. Animator and Timeline update modes should be authored deliberately, especially for kill-cams, victory poses, camera zooms, and UI overlays.\n\n" +
                    "Timeline/camera workflow: read chievfx.cameras status/resources before mutation, author Cinemachine/Timeline shots as normal assets or scene objects, preview visually with screenshot-camera, and trigger slow motion from explicit game code or Timeline signals. Avoid MCP-created hidden runtime objects for this first slice.\n\n" +
                    "QA checklist: verify prompt metadata exposes gamefeel-ending-session-slowmo; confirm content says MCP does not mutate Time globals; check snippet captures/restores timeScale and fixedDeltaTime; check unscaled timing is named; check adjustFixedDeltaTime, physics, scaled coroutine, audio, animation, Timeline/camera, cancel/disable, and fail-safe guidance are present.",
            });

            descriptor.Prompts.Add(new ChievfxMcpPromptDescriptor
            {
                Name = "cameras-cinemachine-sequencer-camera",
                Title = "Author CM3 Sequencer Camera shots",
                Description = "CM3-only guidance for choosing Cinemachine Sequencer Camera versus Timeline.",
                Category = Category,
                Arguments = new JArray
                {
                    new JObject
                    {
                        ["name"] = "goal",
                        ["description"] = "Optional camera sequence goal, shot list, and QA notes.",
                        ["required"] = false,
                    },
                },
                StaticText =
                    "Use Cinemachine Sequencer Camera for compact scene-authored camera beats that only need child CinemachineCameras, holds, blends, optional looping, and live preview through a CinemachineBrain. Use Timeline when the beat must coordinate animation, audio, signals, multi-track timing, asset reuse, or non-camera events. Read chievfx.cameras status/resources before mutation; Sequencer Camera requires Cinemachine 3, CHIEVFX_MCP_HAS_CINEMACHINE, Unity.Cinemachine.CinemachineSequencerCamera, Instruction, CinemachineCamera, CinemachineVirtualCameraBase, CinemachineBlendDefinition, and CinemachineBrain. Do not edit Packages/manifest.json or create Timeline assets for this workflow. Use screenshot-camera for QA, not Game View alone. Goal: {goal}",
            });

            descriptor.Prompts.Add(new ChievfxMcpPromptDescriptor
            {
                Name = "cameras-cinemachine-splines-dolly",
                Title = "Bind CM3 camera to existing Unity Spline",
                Description = "Prompt-only guidance for CM3 CinemachineSplineDolly setup using existing SplineContainer paths.",
                Category = Category,
                Arguments = new JArray
                {
                    new JObject
                    {
                        ["name"] = "goal",
                        ["description"] = "Optional dolly camera goal, existing camera/spline names, and QA notes.",
                        ["required"] = false,
                    },
                },
                StaticText =
                    "Author the SplineContainer path manually in Unity first. MCP should not install packages, edit Packages/manifest.json, create spline knots, reshape spline geometry, or guess a path. Read chievfx.cameras status and chievfx://extensions/chievfx.cameras/cinemachine/splines-dolly before mutation. The narrow MCP action is cinemachine-spline-dolly-set: bind an existing CinemachineCamera to an existing UnityEngine.Splines.SplineContainer, optionally set camera position/value units, and register Undo. Requires Cinemachine 3, CHIEVFX_MCP_HAS_CINEMACHINE, CHIEVFX_MCP_HAS_SPLINES, com.unity.splines, Unity.Cinemachine.CinemachineSplineDolly, CinemachineSplineRoll, CinemachineCamera, CinemachineBrain, and UnityEngine.Splines.SplineContainer. Use screenshot-camera at start/mid/end dolly positions after previewing/evaluating the camera; verify target framing changes and no black frames. Goal: {goal}",
            });

            descriptor.Prompts.Add(new ChievfxMcpPromptDescriptor
            {
                Name = "cameras-cinemachine-input-axis-controller",
                Title = "Guide CM3 InputAxisController wiring",
                Description = "Prompt-only guidance for CinemachineInputAxisController setup and QA.",
                Category = Category,
                Arguments = new JArray
                {
                    new JObject
                    {
                        ["name"] = "goal",
                        ["description"] = "Optional input/camera goal, existing owner names, and QA notes.",
                        ["required"] = false,
                    },
                },
                StaticText =
                    "Prompt-only CM3 InputAxisController guidance. Read chievfx.cameras status and chievfx://extensions/chievfx.cameras/cinemachine/input-axis-controllers first. Requires com.unity.cinemachine, apiFamily == cm3, CHIEVFX_MCP_HAS_CINEMACHINE, Unity.Cinemachine.CinemachineInputAxisController, Unity.Cinemachine.InputAxis, and Unity.Cinemachine.IInputAxisOwner. Input System-specific guidance additionally requires com.unity.inputsystem, UnityEngine.InputSystem.InputActionReference, and UnityEngine.InputSystem.PlayerInput.\n\n" +
                    "Axis owner detection: inventory existing CinemachineInputAxisController components and nearby IInputAxisOwner implementers before suggesting wiring. Treat owners as existing camera/gameplay components that expose Cinemachine InputAxis values; do not infer axes from object names alone.\n\n" +
                    "Input backend choice: if Input System is installed and loaded, reference existing InputActionReference assets/actions only after the user identifies them. If it is absent, explain legacy input/manual value driving at a high level instead of fabricating InputAction assets. PlayerInput caveat: PlayerInput may clone action assets per player at runtime, so serialized InputActionReference inspection is not proof of the runtime action instance. Confirm player index/control scheme ownership in project code or Play Mode fixtures.\n\n" +
                    "Guardrails: MCP should not invent input assets, add hidden input bootstrap scripts, edit Packages/manifest.json, or silently switch project input handling. Any runtime input forwarding belongs in explicit game-owned code.\n\n" +
                    "QA: screenshot-camera is secondary for input setup. In a controlled fixture, manually exercise actions/legacy axes and verify driven camera orientation, zoom/FOV, damping feel, and no unintended player cross-talk. Goal: {goal}",
            });

            descriptor.Prompts.Add(new ChievfxMcpPromptDescriptor
            {
                Name = "cameras-cinemachine-impulse-shake",
                Title = "Guide CM3 Impulse Source/Listener shake",
                Description = "Prompt-only guidance for Cinemachine Impulse source/listener pairing and transient visual QA.",
                Category = Category,
                Arguments = new JArray
                {
                    new JObject
                    {
                        ["name"] = "goal",
                        ["description"] = "Optional shake goal, existing source/listener names, channel masks, and QA notes.",
                        ["required"] = false,
                    },
                },
                StaticText =
                    "Prompt-only CM3 Impulse guidance. Read chievfx.cameras status and chievfx://extensions/chievfx.cameras/cinemachine/impulse first. Requires com.unity.cinemachine, apiFamily == cm3, CHIEVFX_MCP_HAS_CINEMACHINE, Unity.Cinemachine.CinemachineImpulseSource, and Unity.Cinemachine.CinemachineImpulseListener. Inventory optional Unity.Cinemachine.CinemachineCollisionImpulseSource and Unity.Cinemachine.CinemachineExternalImpulseListener when loaded.\n\n" +
                    "Pairing model: sources emit impulses; listeners on the Brain camera or active camera rig receive them through matching channel masks. Verify at least one source and one listener exist, then compare channel/impulseChannel fields before blaming amplitude, gain, distance, or damping. Source and listener layer/channel choices should be user-authored, not guessed.\n\n" +
                    "Trigger ownership: MCP should not add hidden impulse trigger scripts or invent gameplay events. Trigger calls belong in explicit game-owned code, animation events, Timeline signals, collision callbacks already present in the project, or a visible QA fixture created by the user.\n\n" +
                    "QA: use a Play Mode fixture with an explicit source trigger. Capture screenshot-camera before, during, and after shake on the Unity Camera with CinemachineBrain; accept best-effort visual diff because impulse timing is transient. Goal: {goal}",
            });

            descriptor.Tools.Add(Tool("brain-ensure", "Create or ensure a CinemachineBrain on a Unity Camera.", BrainEnsureSchema()));
            descriptor.Tools.Add(Tool("cinemachine-create", "Create a Cinemachine 3 camera with optional target, lens, position, priority, and distance offset.", CinemachineCreateSchema()));
            descriptor.Tools.Add(Tool("cinemachine-set", "Set safe Cinemachine camera target, lens, priority, and enabled fields.", CinemachineSetSchema()));
            descriptor.Tools.Add(Tool("cinemachine-sequencer-create", "Create a CM3 Cinemachine Sequencer Camera with child shot cameras.", SequencerCreateSchema()));
            descriptor.Tools.Add(Tool("cinemachine-spline-dolly-set", "Add/update a CM3 CinemachineSplineDolly on an existing CinemachineCamera using an existing SplineContainer.", SplineDollySetSchema()));
            descriptor.Tools.Add(Tool("cinemachine-blender-settings-set", "Create/update a CM3 CinemachineBlenderSettings asset and optionally assign it to the selected Brain.", BlenderSettingsSetSchema()));
            descriptor.Tools.Add(Tool("cinemachine-confiner-set", "Add/update a CM3 Cinemachine Confiner2D or Confiner3D on an existing CinemachineCamera using an existing collider.", ConfinerSetSchema()));
            descriptor.Tools.Add(Tool("timeline-director-create", "Create a PlayableDirector and optional TimelineAsset.", TimelineDirectorCreateSchema()));
            descriptor.Tools.Add(Tool("timeline-shot-sequence-create", "Create a Cinemachine Timeline shot sequence using CinemachineTrack/CinemachineShot.", ShotSequenceCreateSchema()));
            descriptor.Tools.Add(Tool("timeline-director-preview", "Scrub or preview a PlayableDirector via time and Evaluate/Play/Stop.", TimelinePreviewSchema()));

            return descriptor;
        }

        private static ChievfxMcpResourceDescriptor Resource(string id, string uri, string name, string description)
        {
            return new ChievfxMcpResourceDescriptor
            {
                Id = id,
                Uri = uri,
                Name = name,
                Description = description,
                MimeType = "application/json",
                Category = Category,
            };
        }

        private static ChievfxMcpResourceTemplateDescriptor Template(string id, string uriTemplate, string name, string description)
        {
            return new ChievfxMcpResourceTemplateDescriptor
            {
                Id = id,
                UriTemplate = uriTemplate,
                Name = name,
                Description = description,
                MimeType = "application/json",
                Category = Category,
            };
        }

        private static ChievfxMcpToolDescriptor Tool(string name, string description, JObject schema)
        {
            return new ChievfxMcpToolDescriptor
            {
                Name = name,
                Description = description,
                Category = Category,
                InputSchema = schema,
            };
        }
    }
}
