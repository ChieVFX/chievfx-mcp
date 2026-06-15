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
    internal static class ChievfxMcpCamerasExtension
    {
        private const string ExtensionId = "chievfx.cameras";
        private const string Category = "cinemachine-and-timeline";
        private const string GameFeelCategory = "Game Feel";
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

        private static object? ReadResource(string uri)
        {
            var status = GetDependencyStatus();
            if (string.Equals(uri, StatusUri, StringComparison.Ordinal))
            {
                return ReadStatusResource(uri, status);
            }

            if (string.Equals(uri, CinemachineCamerasUri, StringComparison.Ordinal))
            {
                return status.Cinemachine.Available ? ReadCinemachineCamerasResource(uri, status) : CreateCinemachineUnavailable(status, $"Resource '{uri}'");
            }

            if (uri.StartsWith(CinemachineCameraDetailPrefix, StringComparison.Ordinal))
            {
                return status.Cinemachine.Available ? ReadCinemachineCameraDetailResource(uri, DecodeSegment(uri.Substring(CinemachineCameraDetailPrefix.Length)), status) : CreateCinemachineUnavailable(status, $"Resource '{uri}'");
            }

            if (string.Equals(uri, CinemachineBrainsUri, StringComparison.Ordinal))
            {
                return status.Cinemachine.Available ? ReadCinemachineBrainsResource(uri, status) : CreateCinemachineUnavailable(status, $"Resource '{uri}'");
            }

            if (uri.StartsWith(CinemachineBrainDetailPrefix, StringComparison.Ordinal))
            {
                return status.Cinemachine.Available ? ReadCinemachineBrainDetailResource(uri, DecodeSegment(uri.Substring(CinemachineBrainDetailPrefix.Length)), status) : CreateCinemachineUnavailable(status, $"Resource '{uri}'");
            }

            if (string.Equals(uri, CinemachineSequencersUri, StringComparison.Ordinal))
            {
                return status.Sequencer.Available ? ReadSequencersResource(uri, status) : CreateSequencerUnavailable(status, $"Resource '{uri}'");
            }

            if (uri.StartsWith(CinemachineSequencerDetailPrefix, StringComparison.Ordinal))
            {
                return status.Sequencer.Available ? ReadSequencerDetailResource(uri, DecodeSegment(uri.Substring(CinemachineSequencerDetailPrefix.Length)), status) : CreateSequencerUnavailable(status, $"Resource '{uri}'");
            }

            if (string.Equals(uri, CinemachineSplinesDollyUri, StringComparison.Ordinal))
            {
                return SplinesDollyAvailable(status) ? ReadSplinesDollyResource(uri, status) : CreateAdvancedHelperUnavailable(status, status.SplinesDolly, $"Resource '{uri}'");
            }

            if (string.Equals(uri, CinemachineInputAxisControllersUri, StringComparison.Ordinal))
            {
                return status.InputAxisController.Available ? ReadInputAxisControllersResource(uri, status) : CreateAdvancedHelperUnavailable(status, status.InputAxisController, $"Resource '{uri}'");
            }

            if (string.Equals(uri, CinemachineBlenderSettingsUri, StringComparison.Ordinal))
            {
                return BlenderSettingsAvailable(status) ? ReadBlenderSettingsResource(uri, status) : CreateBlenderSettingsUnavailable(status, $"Resource '{uri}'");
            }

            if (string.Equals(uri, CinemachineImpulseUri, StringComparison.Ordinal))
            {
                return status.Impulse.Available ? ReadImpulseResource(uri, status) : CreateAdvancedHelperUnavailable(status, status.Impulse, $"Resource '{uri}'");
            }

            if (string.Equals(uri, CinemachineConfiner2DUri, StringComparison.Ordinal))
            {
                return ConfinerAvailable(status, status.Confiner2D) ? ReadConfinerResource(uri, status, status.Confiner2D) : CreateConfinerUnavailable(status, status.Confiner2D, $"Resource '{uri}'");
            }

            if (string.Equals(uri, CinemachineConfiner3DUri, StringComparison.Ordinal))
            {
                return ConfinerAvailable(status, status.Confiner3D) ? ReadConfinerResource(uri, status, status.Confiner3D) : CreateConfinerUnavailable(status, status.Confiner3D, $"Resource '{uri}'");
            }

            if (string.Equals(uri, TimelineDirectorsUri, StringComparison.Ordinal))
            {
                return status.Timeline.Available ? ReadTimelineDirectorsResource(uri, status) : CreateUnavailable(StatusUri, status, $"Resource '{uri}' requires {TimelinePackageName}, loaded UnityEngine.Timeline types, and active CHIEVFX_MCP_HAS_TIMELINE.");
            }

            if (uri.StartsWith(TimelineDirectorDetailPrefix, StringComparison.Ordinal))
            {
                return status.Timeline.Available ? ReadTimelineDirectorDetailResource(uri, DecodeSegment(uri.Substring(TimelineDirectorDetailPrefix.Length)), status) : CreateUnavailable(StatusUri, status, $"Resource '{uri}' requires {TimelinePackageName}, loaded UnityEngine.Timeline types, and active CHIEVFX_MCP_HAS_TIMELINE.");
            }

            if (string.Equals(uri, TimelineAssetsUri, StringComparison.Ordinal))
            {
                return status.Timeline.Available ? ReadTimelineAssetsResource(uri, status) : CreateUnavailable(StatusUri, status, $"Resource '{uri}' requires {TimelinePackageName}, loaded UnityEngine.Timeline types, and active CHIEVFX_MCP_HAS_TIMELINE.");
            }

            if (uri.StartsWith(TimelineAssetDetailPrefix, StringComparison.Ordinal))
            {
                return status.Timeline.Available ? ReadTimelineAssetDetailResource(uri, DecodeSegment(uri.Substring(TimelineAssetDetailPrefix.Length)), status) : CreateUnavailable(StatusUri, status, $"Resource '{uri}' requires {TimelinePackageName}, loaded UnityEngine.Timeline types, and active CHIEVFX_MCP_HAS_TIMELINE.");
            }

            throw new InvalidOperationException($"Unknown camera/cutscene extension resource '{uri}'.");
        }

        private static object? RunTool(string toolName, JToken args)
        {
            var status = GetDependencyStatus();
            return toolName switch
            {
                "brain-ensure" => status.Cinemachine.Available ? EnsureBrain(args, status) : CreateCinemachineUnavailable(status, $"Tool '{toolName}'"),
                "cinemachine-create" => status.Cinemachine.Available ? CreateCinemachineCamera(args, status) : CreateCinemachineUnavailable(status, $"Tool '{toolName}'"),
                "cinemachine-set" => status.Cinemachine.Available ? SetCinemachineCamera(args, status) : CreateCinemachineUnavailable(status, $"Tool '{toolName}'"),
                "cinemachine-sequencer-create" => status.Sequencer.Available ? CreateSequencerCamera(args, status) : CreateSequencerUnavailable(status, $"Tool '{toolName}'"),
                "cinemachine-spline-dolly-set" => SplinesDollyAvailable(status) ? SetSplineDolly(args, status) : CreateAdvancedHelperUnavailable(status, status.SplinesDolly, $"Tool '{toolName}'"),
                "cinemachine-blender-settings-set" => BlenderSettingsAvailable(status) ? SetBlenderSettings(args, status) : CreateBlenderSettingsUnavailable(status, $"Tool '{toolName}'"),
                "cinemachine-confiner-set" => SetConfiner(args, status),
                "timeline-director-create" => status.Timeline.Available ? CreateTimelineDirector(args, status) : CreateUnavailable(StatusUri, status, $"Tool '{toolName}' requires {TimelinePackageName}, loaded UnityEngine.Timeline types, and active CHIEVFX_MCP_HAS_TIMELINE."),
                "timeline-shot-sequence-create" => status.Cinemachine.Available && status.Timeline.Available ? CreateShotSequence(args, status) : CreateShotSequenceUnavailable(status, $"Tool '{toolName}'"),
                "timeline-director-preview" => status.Timeline.Available ? PreviewDirector(args, status) : CreateUnavailable(StatusUri, status, $"Tool '{toolName}' requires {TimelinePackageName}, loaded UnityEngine.Timeline types, and active CHIEVFX_MCP_HAS_TIMELINE."),
                _ => throw new InvalidOperationException($"Unknown camera/cutscene extension tool '{toolName}'."),
            };
        }

        private static Dictionary<string, object?> ReadStatusResource(string uri, DependencyStatus status)
        {
            var result = CreateEnvelope(uri, status);
            result["available"] = status.AnyAvailable;
            result["dependencyReason"] = status.Reason;
            result["cinemachineApiFamily"] = status.Cinemachine.ApiFamily;
            result["sequencerCameraAvailable"] = status.Sequencer.Available;
            result["sequencerCameraTypeLoaded"] = status.Sequencer.SequencerCameraTypeLoaded;
            result["sequencerCameraUnavailableReason"] = status.Sequencer.Reason;
            result["cinemachine"] = status.Cinemachine.ToDictionary();
            result["sequencerCamera"] = status.Sequencer.ToDictionary();
            result["splinesDolly"] = status.SplinesDolly.ToDictionary();
            result["inputAxisController"] = status.InputAxisController.ToDictionary();
            result["inputSystem"] = status.InputSystem.ToDictionary();
            result["blenderSettings"] = status.BlenderSettings.ToDictionary();
            result["impulse"] = status.Impulse.ToDictionary();
            result["collisionImpulseSourceTypeLoaded"] = status.CollisionImpulseSourceType != null;
            result["externalImpulseListenerTypeLoaded"] = status.ExternalImpulseListenerType != null;
            result["confiner2D"] = status.Confiner2D.ToDictionary();
            result["confiner3D"] = status.Confiner3D.ToDictionary();
            result["obsoleteConfinerTypeLoaded"] = status.ObsoleteConfinerType != null;
            result["obsoleteConfinerWarning"] = status.ObsoleteConfinerType == null
                ? null
                : "Deprecated Unity.Cinemachine.CinemachineConfiner is loaded; helper will warn only and will not author obsolete confiners.";
            result["timeline"] = status.Timeline.ToDictionary();
            result["resources"] = new[]
            {
                StatusUri,
                CinemachineCamerasUri,
                CinemachineBrainsUri,
                CinemachineSequencersUri,
                CinemachineSplinesDollyUri,
                CinemachineInputAxisControllersUri,
                CinemachineBlenderSettingsUri,
                CinemachineImpulseUri,
                CinemachineConfiner2DUri,
                CinemachineConfiner3DUri,
                TimelineDirectorsUri,
                TimelineAssetsUri,
            };
            result["resourceTemplates"] = new[]
            {
                CinemachineCameraDetailPrefix + "{pathOrInstanceId}",
                CinemachineBrainDetailPrefix + "{pathOrInstanceId}",
                CinemachineSequencerDetailPrefix + "{pathOrInstanceId}",
                TimelineDirectorDetailPrefix + "{pathOrInstanceId}",
                TimelineAssetDetailPrefix + "{guidOrPath}",
            };
            result["tools"] = new[]
            {
                "brain-ensure",
                "cinemachine-create",
                "cinemachine-set",
                "cinemachine-sequencer-create",
                "cinemachine-spline-dolly-set",
                "cinemachine-blender-settings-set",
                "cinemachine-confiner-set",
                "timeline-director-create",
                "timeline-shot-sequence-create",
                "timeline-director-preview",
            };
            result["prompts"] = new[] { "cameras-ending-session-slowmo-zoom", "gamefeel-ending-session-slowmo", "cameras-cinemachine-sequencer-camera", "cameras-cinemachine-splines-dolly", "cameras-cinemachine-input-axis-controller", "cameras-cinemachine-impulse-shake" };
            result["workflowNotes"] = new[]
            {
                "Ending-session slow-mo should be integrated in runtime game code with Time.timeScale plus Time.fixedDeltaTime restore; MCP tools only author Camera/Timeline assets.",
                "gamefeel-ending-session-slowmo is prompt-only guidance; MCP does not create hidden runtime owners or mutate Time globals.",
                "Read status and read-only inventory resources before using mutating camera/cutscene tools.",
                "Splines dolly helper binds existing CinemachineCamera and existing SplineContainer only; author spline knots/shape manually in Unity first.",
                "InputAxisController guidance is prompt/read-only only; MCP does not invent input assets or hidden input forwarding scripts.",
                "Impulse guidance is prompt/read-only only; MCP does not add hidden impulse trigger scripts or invent gameplay trigger ownership.",
                "Use screenshot-camera against authored Cinemachine/Unity camera fixtures for visual QA.",
                "Timeline shot overlaps ignore CinemachineBrain default/custom blend settings; use Timeline clip overlap for Timeline blend feel.",
                "Confiner2D cache invalidation can be expensive; invalidate bounding-shape/lens caches only when explicitly requested after collider or lens changes.",
            };
            return result;
        }

        private static Dictionary<string, object?> ReadCinemachineCamerasResource(string uri, DependencyStatus status)
        {
            var cameras = EnumerateComponents(status.Cinemachine.CameraType!)
                .Take(MaxCameraRows + 1)
                .ToArray();
            var result = CreateEnvelope(uri, status);
            result["stage"] = DescribeCurrentStage();
            result["count"] = Math.Min(cameras.Length, MaxCameraRows);
            result["capped"] = cameras.Length > MaxCameraRows;
            result["maxRows"] = MaxCameraRows;
            result["cameras"] = cameras.Take(MaxCameraRows).Select(camera => DescribeCinemachineCamera(camera, detail: false)).ToArray();
            return result;
        }

        private static Dictionary<string, object?> ReadCinemachineCameraDetailResource(string uri, string pathOrInstanceId, DependencyStatus status)
        {
            var camera = ResolveComponent(status.Cinemachine.CameraType!, pathOrInstanceId);
            if (camera == null)
            {
                return CreateNotFound(uri, status, "cinemachine-camera", pathOrInstanceId);
            }

            var result = CreateEnvelope(uri, status);
            result["stage"] = DescribeCurrentStage();
            result["target"] = DescribeCinemachineCamera(camera, detail: true);
            return result;
        }

        private static Dictionary<string, object?> ReadCinemachineBrainsResource(string uri, DependencyStatus status)
        {
            var brains = EnumerateComponents(status.Cinemachine.BrainType!)
                .Take(MaxBrainRows + 1)
                .ToArray();
            var result = CreateEnvelope(uri, status);
            result["stage"] = DescribeCurrentStage();
            result["count"] = Math.Min(brains.Length, MaxBrainRows);
            result["capped"] = brains.Length > MaxBrainRows;
            result["maxRows"] = MaxBrainRows;
            result["brains"] = brains.Take(MaxBrainRows).Select(DescribeBrain).ToArray();
            return result;
        }

        private static Dictionary<string, object?> ReadCinemachineBrainDetailResource(string uri, string pathOrInstanceId, DependencyStatus status)
        {
            var brain = ResolveComponent(status.Cinemachine.BrainType!, pathOrInstanceId);
            if (brain == null)
            {
                return CreateNotFound(uri, status, "cinemachine-brain", pathOrInstanceId);
            }

            var result = CreateEnvelope(uri, status);
            result["stage"] = DescribeCurrentStage();
            result["target"] = DescribeBrain(brain);
            return result;
        }

        private static Dictionary<string, object?> ReadSequencersResource(string uri, DependencyStatus status)
        {
            var sequencers = EnumerateComponents(status.Sequencer.SequencerCameraType!)
                .Take(MaxSequencerRows + 1)
                .ToArray();
            var result = CreateEnvelope(uri, status);
            result["stage"] = DescribeCurrentStage();
            result["count"] = Math.Min(sequencers.Length, MaxSequencerRows);
            result["capped"] = sequencers.Length > MaxSequencerRows;
            result["maxRows"] = MaxSequencerRows;
            result["sequencers"] = sequencers.Take(MaxSequencerRows).Select(sequencer => DescribeSequencer(sequencer, detail: false)).ToArray();
            return result;
        }

        private static Dictionary<string, object?> ReadSequencerDetailResource(string uri, string pathOrInstanceId, DependencyStatus status)
        {
            var sequencer = ResolveComponent(status.Sequencer.SequencerCameraType!, pathOrInstanceId);
            if (sequencer == null)
            {
                return CreateNotFound(uri, status, "cinemachine-sequencer-camera", pathOrInstanceId);
            }

            var result = CreateEnvelope(uri, status);
            result["stage"] = DescribeCurrentStage();
            result["target"] = DescribeSequencer(sequencer, detail: true);
            return result;
        }

        private static Dictionary<string, object?> ReadSplinesDollyResource(string uri, DependencyStatus status)
        {
            var dollies = EnumerateComponents(status.SplinesDolly.HelperType!)
                .Take(MaxAdvancedHelperRows + 1)
                .ToArray();
            var splineContainers = EnumerateComponents(status.SplinesDolly.OptionalType!)
                .Take(MaxAdvancedHelperRows + 1)
                .ToArray();
            var camerasWithDolly = status.Cinemachine.CameraType == null
                ? Array.Empty<Component>()
                : EnumerateComponents(status.Cinemachine.CameraType)
                    .Where(camera => camera.GetComponent(status.SplinesDolly.HelperType!) != null)
                    .Take(MaxAdvancedHelperRows + 1)
                    .ToArray();

            var result = CreateEnvelope(uri, status);
            result["helper"] = status.SplinesDolly.ToDictionary();
            result["stage"] = DescribeCurrentStage();
            result["count"] = Math.Min(dollies.Length, MaxAdvancedHelperRows);
            result["capped"] = dollies.Length > MaxAdvancedHelperRows;
            result["maxRows"] = MaxAdvancedHelperRows;
            result["splineContainerCount"] = Math.Min(splineContainers.Length, MaxAdvancedHelperRows);
            result["splineContainersCapped"] = splineContainers.Length > MaxAdvancedHelperRows;
            result["splineContainers"] = splineContainers.Take(MaxAdvancedHelperRows).Select(container => DescribeSplineContainer(container, status)).ToArray();
            result["cameraCount"] = Math.Min(camerasWithDolly.Length, MaxAdvancedHelperRows);
            result["cameras"] = camerasWithDolly.Take(MaxAdvancedHelperRows).Select(camera => DescribeSplineDollyCamera(camera, status)).ToArray();
            result["splinesDollies"] = dollies.Take(MaxAdvancedHelperRows).Select(dolly => DescribeSplinesDolly(dolly, status)).ToArray();
            result["authoringPolicy"] = "Read-only inventory plus cinemachine-spline-dolly-set. Author SplineContainer knots and shape manually in Unity first; MCP does not create or reshape spline geometry.";
            result["visualQaHint"] = "Preview/evaluate start, mid, and end camera positions, then capture screenshot-camera from the Unity Camera with CinemachineBrain; verify target framing changes and no black frames.";
            return result;
        }

        private static Dictionary<string, object?> ReadAdvancedComponentInventoryResource(string uri, DependencyStatus status, AdvancedHelperStatus helper, string rowsKey, Func<Component, Dictionary<string, object?>> describe)
        {
            var components = EnumerateComponents(helper.HelperType!)
                .Take(MaxAdvancedHelperRows + 1)
                .ToArray();
            var result = CreateEnvelope(uri, status);
            result["helper"] = helper.ToDictionary();
            result["stage"] = DescribeCurrentStage();
            result["count"] = Math.Min(components.Length, MaxAdvancedHelperRows);
            result["capped"] = components.Length > MaxAdvancedHelperRows;
            result["maxRows"] = MaxAdvancedHelperRows;
            result[rowsKey] = components.Take(MaxAdvancedHelperRows).Select(describe).ToArray();
            return result;
        }

        private static Dictionary<string, object?> ReadInputAxisControllersResource(string uri, DependencyStatus status)
        {
            var controllers = EnumerateComponents(status.InputAxisController.HelperType!)
                .Take(MaxAdvancedHelperRows + 1)
                .ToArray();
            var owners = status.InputAxisController.TertiaryHelperType == null
                ? Array.Empty<Component>()
                : EnumerateComponentsImplementing(status.InputAxisController.TertiaryHelperType)
                    .Take(MaxAdvancedHelperRows + 1)
                    .ToArray();
            var result = CreateEnvelope(uri, status);
            result["helper"] = status.InputAxisController.ToDictionary();
            result["inputSystem"] = status.InputSystem.ToDictionary();
            result["stage"] = DescribeCurrentStage();
            result["count"] = Math.Min(controllers.Length, MaxAdvancedHelperRows);
            result["capped"] = controllers.Length > MaxAdvancedHelperRows;
            result["maxRows"] = MaxAdvancedHelperRows;
            result["inputAxisControllers"] = controllers.Take(MaxAdvancedHelperRows).Select(component => DescribeInputAxisController(component, status)).ToArray();
            result["axisOwnerCount"] = Math.Min(owners.Length, MaxAdvancedHelperRows);
            result["axisOwnersCapped"] = owners.Length > MaxAdvancedHelperRows;
            result["axisOwners"] = owners.Take(MaxAdvancedHelperRows).Select(DescribeAdvancedComponent).ToArray();
            result["authoringPolicy"] = "Prompt/read-only guidance only. MCP should not invent input assets, add hidden input forwarding scripts, edit Packages/manifest.json, or silently switch project input handling.";
            result["axisOwnerGuidance"] = "Detect existing IInputAxisOwner implementers before suggesting controller wiring; owners expose Cinemachine InputAxis values and should not be inferred from object names alone.";
            result["inputSystemGuidance"] = status.InputSystem.Available
                ? "Input System-specific guidance is enabled because InputActionReference and PlayerInput types are loaded; reference existing actions only, and remember PlayerInput may clone action assets per player at runtime."
                : "Input System-specific guidance is unavailable; discuss legacy input/manual value driving without fabricating InputAction assets.";
            result["visualQaHint"] = "screenshot-camera is secondary for input setup; manually exercise actions or legacy axes in a controlled fixture and verify camera orientation/FOV.";
            result["warnings"] = InputAxisControllerWarnings(controllers, owners, status).ToArray();
            return result;
        }

        private static Dictionary<string, object?> ReadBlenderSettingsResource(string uri, DependencyStatus status)
        {
            var assets = EnumerateAssetsOfType(status.BlenderSettings.HelperType!)
                .Take(MaxAdvancedHelperRows + 1)
                .ToArray();
            var customBlendBrains = status.Cinemachine.BrainType == null
                ? Array.Empty<Component>()
                : EnumerateComponents(status.Cinemachine.BrainType)
                    .Where(brain => ReadMember(brain, "CustomBlends") != null)
                    .Take(MaxAdvancedHelperRows + 1)
                    .ToArray();
            var cameraNames = CurrentCinemachineCameraNames(status);
            var result = CreateEnvelope(uri, status);
            result["helper"] = status.BlenderSettings.ToDictionary();
            result["stage"] = DescribeCurrentStage();
            result["count"] = Math.Min(assets.Length + customBlendBrains.Length, MaxAdvancedHelperRows);
            result["capped"] = assets.Length + customBlendBrains.Length > MaxAdvancedHelperRows;
            result["maxRows"] = MaxAdvancedHelperRows;
            result["maxBlendEntries"] = MaxCustomBlendEntries;
            result["assets"] = assets.Take(MaxAdvancedHelperRows).Select(asset => DescribeBlenderSettingsAsset(asset, cameraNames)).ToArray();
            result["brainCustomBlends"] = customBlendBrains.Take(MaxAdvancedHelperRows).Select(brain => DescribeBrainCustomBlends(brain, cameraNames)).ToArray();
            result["fallbackBehavior"] = "CinemachineBrain uses a matching custom blend when found, otherwise DefaultBlend. Entries using ANY CAMERA are wildcard fallbacks.";
            result["timelineNote"] = "Timeline Cinemachine shot overlaps ignore Brain default/custom blend settings; adjacent Timeline clip overlap controls Timeline blend feel.";
            return result;
        }

        private static Dictionary<string, object?> ReadImpulseResource(string uri, DependencyStatus status)
        {
            var sources = EnumerateComponents(status.Impulse.HelperType!)
                .Take(MaxAdvancedHelperRows + 1)
                .ToArray();
            var listeners = status.Impulse.SecondaryHelperType == null
                ? Array.Empty<Component>()
                : EnumerateComponents(status.Impulse.SecondaryHelperType)
                    .Take(MaxAdvancedHelperRows + 1)
                    .ToArray();
            var collisionSources = status.CollisionImpulseSourceType == null
                ? Array.Empty<Component>()
                : EnumerateComponents(status.CollisionImpulseSourceType)
                    .Take(MaxAdvancedHelperRows + 1)
                    .ToArray();
            var externalListeners = status.ExternalImpulseListenerType == null
                ? Array.Empty<Component>()
                : EnumerateComponents(status.ExternalImpulseListenerType)
                    .Take(MaxAdvancedHelperRows + 1)
                    .ToArray();
            var result = CreateEnvelope(uri, status);
            result["helper"] = status.Impulse.ToDictionary();
            result["stage"] = DescribeCurrentStage();
            result["count"] = Math.Min(sources.Length + listeners.Length, MaxAdvancedHelperRows);
            result["capped"] = sources.Length + listeners.Length > MaxAdvancedHelperRows;
            result["maxRows"] = MaxAdvancedHelperRows;
            result["sources"] = sources.Take(MaxAdvancedHelperRows).Select(DescribeImpulseComponent).ToArray();
            result["listeners"] = listeners.Take(MaxAdvancedHelperRows).Select(DescribeImpulseComponent).ToArray();
            result["optionalTypes"] = new Dictionary<string, object?>
            {
                ["collisionImpulseSourceTypeName"] = "Unity.Cinemachine.CinemachineCollisionImpulseSource",
                ["collisionImpulseSourceTypeLoaded"] = status.CollisionImpulseSourceType != null,
                ["externalImpulseListenerTypeName"] = "Unity.Cinemachine.CinemachineExternalImpulseListener",
                ["externalImpulseListenerTypeLoaded"] = status.ExternalImpulseListenerType != null,
            };
            result["collisionSources"] = collisionSources.Take(MaxAdvancedHelperRows).Select(DescribeImpulseComponent).ToArray();
            result["externalListeners"] = externalListeners.Take(MaxAdvancedHelperRows).Select(DescribeImpulseComponent).ToArray();
            result["sourceChannels"] = sources.Select(ImpulseChannelLabel).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            result["listenerChannels"] = listeners.Select(ImpulseChannelLabel).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            result["authoringPolicy"] = "Prompt/read-only guidance only. MCP should not add hidden impulse trigger scripts or invent gameplay events; triggers belong in explicit game-owned code or user-authored QA fixtures.";
            result["pairingGuidance"] = "Verify at least one source and one listener exist, then compare channel masks before tuning amplitude, gain, distance, or damping.";
            result["visualQaHint"] = "In Play Mode, trigger an explicit source and capture screenshot-camera before/during/after shake on the Unity Camera with CinemachineBrain; visual diff is best-effort because impulse timing is transient.";
            result["warnings"] = ImpulseWarnings(sources, listeners).ToArray();
            return result;
        }

        private static Dictionary<string, object?> ReadConfinerResource(string uri, DependencyStatus status, AdvancedHelperStatus helper)
        {
            var confiners = EnumerateComponents(helper.HelperType!)
                .Take(MaxAdvancedHelperRows + 1)
                .ToArray();
            var obsoleteConfiners = status.ObsoleteConfinerType == null
                ? Array.Empty<Component>()
                : EnumerateComponents(status.ObsoleteConfinerType)
                    .Take(MaxAdvancedHelperRows + 1)
                    .ToArray();
            var warnings = new List<string>
            {
                "Confiner2D cache invalidation can be expensive for complex polygon/composite colliders; use invalidateCache only after explicit collider topology changes.",
                "If the camera lens changes and the confiner exposes a lens cache, use invalidateLensCache explicitly before visual QA.",
            };
            if (obsoleteConfiners.Length > 0)
            {
                warnings.Add("Deprecated Unity.Cinemachine.CinemachineConfiner components detected. This helper reports them only and will not author or migrate obsolete confiners.");
            }

            var result = CreateEnvelope(uri, status);
            result["helper"] = helper.ToDictionary();
            result["stage"] = DescribeCurrentStage();
            result["count"] = Math.Min(confiners.Length, MaxAdvancedHelperRows);
            result["capped"] = confiners.Length > MaxAdvancedHelperRows;
            result["maxRows"] = MaxAdvancedHelperRows;
            result["confiners"] = confiners.Take(MaxAdvancedHelperRows).Select(DescribeConfiner).ToArray();
            result["obsoleteConfinerTypeLoaded"] = status.ObsoleteConfinerType != null;
            result["obsoleteConfinerCount"] = Math.Min(obsoleteConfiners.Length, MaxAdvancedHelperRows);
            result["obsoleteConfiners"] = obsoleteConfiners.Take(MaxAdvancedHelperRows).Select(DescribeAdvancedComponent).ToArray();
            result["warnings"] = warnings.ToArray();
            return result;
        }

        private static Dictionary<string, object?> ReadTimelineDirectorsResource(string uri, DependencyStatus status)
        {
            var directors = EnumerateDirectors()
                .Take(MaxDirectorRows + 1)
                .ToArray();
            var result = CreateEnvelope(uri, status);
            result["stage"] = DescribeCurrentStage();
            result["count"] = Math.Min(directors.Length, MaxDirectorRows);
            result["capped"] = directors.Length > MaxDirectorRows;
            result["maxRows"] = MaxDirectorRows;
            result["directors"] = directors.Take(MaxDirectorRows).Select(director => DescribeDirector(director, detail: false)).ToArray();
            return result;
        }

        private static Dictionary<string, object?> ReadTimelineDirectorDetailResource(string uri, string pathOrInstanceId, DependencyStatus status)
        {
            var director = ResolveDirector(pathOrInstanceId);
            if (director == null)
            {
                return CreateNotFound(uri, status, "timeline-director", pathOrInstanceId);
            }

            var result = CreateEnvelope(uri, status);
            result["stage"] = DescribeCurrentStage();
            result["target"] = DescribeDirector(director, detail: true);
            return result;
        }

        private static Dictionary<string, object?> ReadTimelineAssetsResource(string uri, DependencyStatus status)
        {
            var assets = EnumerateTimelineAssetPaths(status)
                .Take(MaxAssetRows + 1)
                .ToArray();
            var result = CreateEnvelope(uri, status);
            result["count"] = Math.Min(assets.Length, MaxAssetRows);
            result["capped"] = assets.Length > MaxAssetRows;
            result["maxRows"] = MaxAssetRows;
            result["assets"] = assets.Take(MaxAssetRows).Select(path => DescribeTimelineAsset(path, status, detail: false)).ToArray();
            return result;
        }

        private static Dictionary<string, object?> ReadTimelineAssetDetailResource(string uri, string guidOrPath, DependencyStatus status)
        {
            var path = ResolveAssetPath(guidOrPath);
            var asset = string.IsNullOrWhiteSpace(path) ? null : AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (asset == null || !status.Timeline.AssetType!.IsInstanceOfType(asset))
            {
                return CreateNotFound(uri, status, "timeline-asset", guidOrPath);
            }

            var result = CreateEnvelope(uri, status);
            result["asset"] = DescribeTimelineAsset(path!, status, detail: true);
            return result;
        }

        private static Dictionary<string, object?> EnsureBrain(JToken args, DependencyStatus status)
        {
            var dryRun = OptionalBool(args, "dryRun", false);
            var createCameraIfMissing = OptionalBool(args, "createCameraIfMissing", true);
            var camera = ResolveCamera(args, allowCreate: createCameraIfMissing, dryRun: dryRun);
            if (camera == null)
            {
                if (dryRun && createCameraIfMissing)
                {
                    var preview = CreateCommandEnvelope(CinemachineBrainsUri, status, dryRun);
                    preview["wouldCreateCamera"] = ReadString(args, "cameraName") ?? "Main Camera";
                    preview["wouldCreateBrain"] = true;
                    return preview;
                }

                throw new ArgumentException("Expected targetPath or instanceId for a Camera, or createCameraIfMissing=true with name.");
            }

            var existing = camera.GetComponent(status.Cinemachine.BrainType!);
            var wouldCreate = existing == null;
            if (!dryRun && wouldCreate)
            {
                Undo.RegisterCompleteObjectUndo(camera.gameObject, "Ensure Cinemachine Brain");
                existing = Undo.AddComponent(camera.gameObject, status.Cinemachine.BrainType!);
                MarkChanged(camera);
            }

            var result = CreateCommandEnvelope(BrainDetailUri(existing as Component ?? camera), status, dryRun);
            result["target"] = DescribeUnityCamera(camera);
            result["brain"] = existing != null ? DescribeBrain(existing) : null;
            result["wouldCreate"] = wouldCreate;
            return result;
        }

        private static Dictionary<string, object?> CreateCinemachineCamera(JToken args, DependencyStatus status)
        {
            var dryRun = OptionalBool(args, "dryRun", false);
            var name = ReadString(args, "name") ?? "Cinemachine Camera";
            var warnings = new List<string>();
            var parent = TryString(args, "parentPath", out var parentPath) ? ResolveTransform(parentPath) : null;
            var target = TryString(args, "targetPath", out var targetPath) ? ResolveTransform(targetPath) : null;
            var position = args["position"] != null ? ReadVector3(args["position"]!, "position") : (Vector3?)null;
            var distance = OptionalFloat(args, "distance");
            var priority = OptionalInt(args, "priority");
            var lens = args["lens"] as JObject;

            if (dryRun)
            {
                var preview = CreateCommandEnvelope(CinemachineCamerasUri, status, dryRun);
                preview["wouldCreate"] = name;
                preview["parentPath"] = parent != null ? GetTransformPath(parent) : null;
                preview["targetPath"] = target != null ? GetTransformPath(target) : null;
                preview["warnings"] = warnings.ToArray();
                return preview;
            }

            var gameObject = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(gameObject, "Create Cinemachine Camera");
            if (parent != null)
            {
                Undo.SetTransformParent(gameObject.transform, parent, "Parent Cinemachine Camera");
            }

            if (position.HasValue)
            {
                Undo.RecordObject(gameObject.transform, "Position Cinemachine Camera");
                gameObject.transform.position = position.Value;
            }
            else if (target != null && distance.HasValue)
            {
                Undo.RecordObject(gameObject.transform, "Position Cinemachine Camera");
                gameObject.transform.position = target.position + new Vector3(0f, 0f, -Mathf.Max(0.01f, distance.Value));
                gameObject.transform.LookAt(target);
            }

            var camera = Undo.AddComponent(gameObject, status.Cinemachine.CameraType!);
            if (priority.HasValue)
            {
                TrySetMember(camera, "Priority", priority.Value, warnings);
            }

            if (target != null)
            {
                TrySetCameraTarget(camera, target, warnings);
            }

            if (lens != null)
            {
                ApplyLensPatch(camera, lens, warnings);
            }

            MarkChanged(camera);
            var result = CreateCommandEnvelope(CinemachineDetailUri(camera), status, dryRun);
            result["target"] = DescribeCinemachineCamera(camera, detail: true);
            result["warnings"] = warnings.ToArray();
            return result;
        }

        private static Dictionary<string, object?> SetCinemachineCamera(JToken args, DependencyStatus status)
        {
            var dryRun = OptionalBool(args, "dryRun", false);
            var camera = RequireComponent(args, status.Cinemachine.CameraType!, "CinemachineCamera");
            var warnings = new List<string>();
            var changes = new List<string>();

            if (TryInt(args, "priority", out var priority))
            {
                changes.Add("priority");
                if (!dryRun)
                {
                    Undo.RecordObject(camera, "Set Cinemachine Camera Priority");
                    TrySetMember(camera, "Priority", priority, warnings);
                }
            }

            if (TryString(args, "targetPath", out var targetPath))
            {
                var target = ResolveTransform(targetPath) ?? throw new ArgumentException($"Could not resolve targetPath '{targetPath}'.");
                changes.Add("target");
                if (!dryRun)
                {
                    Undo.RecordObject(camera, "Set Cinemachine Camera Target");
                    TrySetCameraTarget(camera, target, warnings);
                }
            }

            if (args["lens"] is JObject lens)
            {
                changes.Add("lens");
                if (!dryRun)
                {
                    Undo.RecordObject(camera, "Set Cinemachine Camera Lens");
                    ApplyLensPatch(camera, lens, warnings);
                }
            }

            if (TryBool(args, "enabled", out var enabled) && camera is Behaviour behaviour)
            {
                changes.Add("enabled");
                if (!dryRun)
                {
                    Undo.RecordObject(behaviour, "Set Cinemachine Camera Enabled");
                    behaviour.enabled = enabled;
                }
            }

            if (!dryRun)
            {
                MarkChanged(camera);
            }

            var result = CreateCommandEnvelope(CinemachineDetailUri(camera), status, dryRun);
            result["target"] = DescribeCinemachineCamera(camera, detail: true);
            result["changedFields"] = changes.ToArray();
            result["warnings"] = warnings.ToArray();
            result["blendSafeNotes"] = new[]
            {
                "Camera target/lens/priority changes do not edit Timeline clip overlap.",
                "Timeline shot blend feel is controlled by adjacent clip overlap; brain default/custom blends remain separate.",
            };
            return result;
        }

        private static Dictionary<string, object?> SetBlenderSettings(JToken args, DependencyStatus status)
        {
            if (args["dryRun"] == null || args["dryRun"]!.Type == JTokenType.Null)
            {
                throw new ArgumentException("dryRun is required for cinemachine-blender-settings-set.");
            }

            var dryRun = OptionalBool(args, "dryRun", false);
            var assetPath = ReadString(args, "assetPath");
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                throw new ArgumentException("assetPath is required and must be explicit.");
            }

            assetPath = assetPath!.Trim();
            if (!assetPath.StartsWith("Assets/", StringComparison.Ordinal) || !assetPath.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("assetPath must be an explicit Assets/*.asset path.");
            }

            var existingAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (existingAsset != null && !status.BlenderSettings.HelperType!.IsInstanceOfType(existingAsset))
            {
                throw new ArgumentException($"Asset '{assetPath}' exists but is not a CinemachineBlenderSettings asset.");
            }

            var blendSpecs = ReadBlenderBlendSpecs(args["blends"]);
            var warnings = new List<string>();
            AddBlendValidationWarnings(blendSpecs, CurrentCinemachineCameraNames(status), warnings);
            var assignToSelectedBrain = OptionalBool(args, "assignToSelectedBrain", false);
            var selectedBrain = assignToSelectedBrain ? ResolveSelectedBrain(status.Cinemachine.BrainType!) : null;
            if (assignToSelectedBrain && selectedBrain == null)
            {
                warnings.Add("assignToSelectedBrain=true, but Selection does not contain a CinemachineBrain.");
            }

            if (dryRun)
            {
                var preview = CreateCommandEnvelope(CinemachineBlenderSettingsUri, status, dryRun);
                preview["assetPath"] = assetPath;
                preview["wouldCreateAsset"] = existingAsset == null;
                preview["wouldUpdateAsset"] = existingAsset != null;
                preview["wouldAssignSelectedBrain"] = assignToSelectedBrain && selectedBrain != null;
                preview["selectedBrain"] = selectedBrain != null ? DescribeBrain(selectedBrain) : null;
                preview["blendCount"] = blendSpecs.Length;
                preview["maxBlendEntries"] = MaxCustomBlendEntries;
                preview["plannedEntries"] = blendSpecs.Select(DescribeBlendSpec).ToArray();
                preview["warnings"] = warnings.ToArray();
                preview["timelineNote"] = "Timeline Cinemachine shot overlaps ignore Brain default/custom blend settings; adjacent Timeline clip overlap controls Timeline blend feel.";
                return preview;
            }

            Undo.IncrementCurrentGroup();
            var undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Set Cinemachine Blender Settings");

            var asset = existingAsset;
            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance(status.BlenderSettings.HelperType!);
                asset.name = System.IO.Path.GetFileNameWithoutExtension(assetPath);
                EnsureAssetFolder(assetPath);
                AssetDatabase.CreateAsset(asset, assetPath);
            }

            Undo.RecordObject(asset, "Set Cinemachine Blender Settings");
            ApplyBlenderSettings(asset, blendSpecs, status, warnings);
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssetIfDirty(asset);

            if (assignToSelectedBrain && selectedBrain != null)
            {
                Undo.RecordObject(selectedBrain, "Assign Cinemachine Blender Settings");
                if (!TrySetMember(selectedBrain, "CustomBlends", asset, warnings))
                {
                    warnings.Add("CinemachineBrain.CustomBlends field was not writable.");
                }

                MarkChanged(selectedBrain);
            }

            Undo.CollapseUndoOperations(undoGroup);

            var result = CreateCommandEnvelope(CinemachineBlenderSettingsUri, status, dryRun);
            result["asset"] = DescribeBlenderSettingsAsset(asset, CurrentCinemachineCameraNames(status));
            result["createdAsset"] = existingAsset == null;
            result["assignedBrain"] = selectedBrain != null ? DescribeBrain(selectedBrain) : null;
            result["assetCleanupPath"] = existingAsset == null ? assetPath : null;
            result["undoNotes"] = new[]
            {
                "Brain assignment and asset object edits are registered with Undo.",
                "New asset files are written through AssetDatabase; if Undo does not remove the file in this Unity version, delete assetCleanupPath.",
            };
            result["warnings"] = warnings.ToArray();
            result["timelineNote"] = "Timeline Cinemachine shot overlaps ignore Brain default/custom blend settings; adjacent Timeline clip overlap controls Timeline blend feel.";
            return result;
        }

        private static Dictionary<string, object?> SetSplineDolly(JToken args, DependencyStatus status)
        {
            if (args["dryRun"] == null || args["dryRun"]!.Type == JTokenType.Null)
            {
                throw new ArgumentException("dryRun is required for cinemachine-spline-dolly-set.");
            }

            var dryRun = OptionalBool(args, "dryRun", false);
            var camera = RequireComponent(args, status.Cinemachine.CameraType!, "CinemachineCamera");
            var splineContainer = RequireNamedComponent(args, status.SplinesDolly.OptionalType!, "SplineContainer", "splinePath", "splineInstanceId");
            var position = OptionalFloat(args, "position") ?? OptionalFloat(args, "cameraPosition");
            var positionUnits = ReadString(args, "positionUnits") ?? ReadString(args, "units");
            var autoDollyEnabled = OptionalBoolNullable(args, "autoDollyEnabled");
            var warnings = new List<string>();
            var changes = new List<string> { "splineContainer" };
            if (position.HasValue)
            {
                changes.Add("cameraPosition");
            }

            if (!string.IsNullOrWhiteSpace(positionUnits))
            {
                changes.Add("positionUnits");
            }

            if (autoDollyEnabled.HasValue)
            {
                changes.Add("autoDollyEnabled");
            }

            var existing = camera.gameObject.GetComponent(status.SplinesDolly.HelperType!);
            if (dryRun)
            {
                var preview = CreateCommandEnvelope(CinemachineSplinesDollyUri, status, dryRun);
                preview["camera"] = DescribeCinemachineCamera(camera, detail: false);
                preview["splineContainer"] = DescribeSplineContainer(splineContainer, status);
                preview["wouldAddSplineDolly"] = existing == null;
                preview["wouldAssignExistingSplineContainer"] = true;
                preview["plannedFields"] = changes.ToArray();
                preview["position"] = position.HasValue ? (object)Round(position.Value) : null;
                preview["positionUnits"] = positionUnits;
                preview["autoDollyEnabled"] = autoDollyEnabled;
                preview["geometryMutation"] = false;
                preview["warnings"] = warnings.ToArray();
                return preview;
            }

            Undo.IncrementCurrentGroup();
            var undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Set Cinemachine Spline Dolly");

            var dolly = existing;
            if (dolly == null)
            {
                Undo.RegisterCompleteObjectUndo(camera.gameObject, "Add Cinemachine Spline Dolly");
                dolly = Undo.AddComponent(camera.gameObject, status.SplinesDolly.HelperType!);
            }

            Undo.RecordObject(dolly, "Set Cinemachine Spline Dolly");
            if (!TrySetFirstMember(dolly, new[] { "Spline", "SplineContainer", "Container" }, splineContainer, warnings))
            {
                warnings.Add("SplineContainer field was not writable; assign the spline manually.");
            }

            if (position.HasValue && !TrySetFirstMember(dolly, new[] { "CameraPosition", "Position" }, position.Value, warnings))
            {
                warnings.Add("CameraPosition/Position field was not writable.");
            }

            if (!string.IsNullOrWhiteSpace(positionUnits) && !TrySetFirstMember(dolly, new[] { "PositionUnits", "Units" }, positionUnits!, warnings))
            {
                warnings.Add("PositionUnits field was not writable.");
            }

            if (autoDollyEnabled.HasValue)
            {
                SetAutoDollyEnabled(dolly, autoDollyEnabled.Value, warnings);
            }

            MarkChanged(dolly);
            Undo.CollapseUndoOperations(undoGroup);

            var result = CreateCommandEnvelope(CinemachineSplinesDollyUri, status, dryRun);
            result["camera"] = DescribeSplineDollyCamera(camera, status);
            result["splineDolly"] = DescribeSplinesDolly(dolly, status);
            result["splineContainer"] = DescribeSplineContainer(splineContainer, status);
            result["addedSplineDolly"] = existing == null;
            result["changedFields"] = changes.ToArray();
            result["geometryMutation"] = false;
            result["warnings"] = warnings.ToArray();
            result["visualQaHint"] = "Use screenshot-camera at start/mid/end dolly positions on the Unity Camera with CinemachineBrain; verify target framing changes and no black frames.";
            return result;
        }

        private static Dictionary<string, object?> SetConfiner(JToken args, DependencyStatus status)
        {
            if (args["dryRun"] == null || args["dryRun"]!.Type == JTokenType.Null)
            {
                throw new ArgumentException("dryRun is required for cinemachine-confiner-set.");
            }

            var dimension = (ReadString(args, "dimension") ?? string.Empty).Trim().ToLowerInvariant();
            if (!string.Equals(dimension, "2d", StringComparison.Ordinal) && !string.Equals(dimension, "3d", StringComparison.Ordinal))
            {
                throw new ArgumentException("dimension is required and must be '2d' or '3d'.");
            }

            var helper = dimension == "2d" ? status.Confiner2D : status.Confiner3D;
            if (!ConfinerAvailable(status, helper))
            {
                return CreateConfinerUnavailable(status, helper, "Tool 'cinemachine-confiner-set'");
            }

            var dryRun = OptionalBool(args, "dryRun", false);
            var camera = RequireComponent(args, status.Cinemachine.CameraType!, "CinemachineCamera");
            var collider = RequireCollider(args, helper.SecondaryHelperType!, dimension == "2d" ? "Collider2D" : "Collider");
            var damping = OptionalFloat(args, "damping");
            var slowingDistance = OptionalFloat(args, "slowingDistance");
            var invalidateCache = OptionalBool(args, "invalidateCache", false);
            var invalidateLensCache = OptionalBool(args, "invalidateLensCache", false);
            var warnings = new List<string>();
            var changes = new List<string> { dimension == "2d" ? "boundingShape2D" : "boundingVolume" };

            if (invalidateCache)
            {
                changes.Add("invalidateCache");
                warnings.Add("Explicit cache invalidation requested. Confiner2D cache rebuilds can be expensive for complex collider topology.");
            }

            if (invalidateLensCache)
            {
                changes.Add("invalidateLensCache");
                warnings.Add("Explicit lens cache invalidation requested. Use this after lens/FOV/orthographic-size changes when the confiner exposes lens caching.");
            }

            if (damping.HasValue)
            {
                changes.Add("damping");
            }

            if (slowingDistance.HasValue)
            {
                changes.Add("slowingDistance");
            }

            var existing = camera.gameObject.GetComponent(helper.HelperType!);
            if (dryRun)
            {
                var preview = CreateCommandEnvelope(dimension == "2d" ? CinemachineConfiner2DUri : CinemachineConfiner3DUri, status, dryRun);
                preview["camera"] = DescribeCinemachineCamera(camera, detail: false);
                preview["collider"] = DescribeObjectReference(collider);
                preview["wouldAddConfiner"] = existing == null;
                preview["wouldAssignExistingCollider"] = true;
                preview["wouldInvalidateCache"] = invalidateCache;
                preview["wouldInvalidateLensCache"] = invalidateLensCache;
                preview["plannedFields"] = changes.ToArray();
                preview["warnings"] = warnings.ToArray();
                preview["geometryMutation"] = false;
                return preview;
            }

            Undo.IncrementCurrentGroup();
            var undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Set Cinemachine Confiner");

            var confiner = existing;
            if (confiner == null)
            {
                Undo.RegisterCompleteObjectUndo(camera.gameObject, "Add Cinemachine Confiner");
                confiner = Undo.AddComponent(camera.gameObject, helper.HelperType!);
            }

            Undo.RecordObject(confiner, "Set Cinemachine Confiner");
            var colliderMember = dimension == "2d" ? "BoundingShape2D" : "BoundingVolume";
            if (!TrySetMember(confiner, colliderMember, collider, warnings))
            {
                warnings.Add($"{colliderMember} field was not writable; assign the collider manually.");
            }

            if (damping.HasValue && !TrySetMember(confiner, "Damping", damping.Value, warnings))
            {
                warnings.Add("Damping field was not writable.");
            }

            if (slowingDistance.HasValue && !TrySetMember(confiner, "SlowingDistance", slowingDistance.Value, warnings))
            {
                warnings.Add("SlowingDistance field was not writable.");
            }

            if (invalidateCache)
            {
                InvokeCacheInvalidation(confiner, new[] { "InvalidateBoundingShapeCache", "InvalidateBoundingVolumeCache", "InvalidateCache" }, warnings);
            }

            if (invalidateLensCache)
            {
                InvokeCacheInvalidation(confiner, new[] { "InvalidateLensCache" }, warnings);
            }

            MarkChanged(confiner);
            Undo.CollapseUndoOperations(undoGroup);

            var result = CreateCommandEnvelope(dimension == "2d" ? CinemachineConfiner2DUri : CinemachineConfiner3DUri, status, dryRun);
            result["camera"] = DescribeCinemachineCamera(camera, detail: false);
            result["confiner"] = DescribeConfiner(confiner);
            result["collider"] = DescribeObjectReference(collider);
            result["addedConfiner"] = existing == null;
            result["changedFields"] = changes.ToArray();
            result["geometryMutation"] = false;
            result["warnings"] = warnings.ToArray();
            result["visualQaHint"] = "Use screenshot-camera on the gameplay camera inside bounds and near/over the edge; verify confinement and no black frames.";
            return result;
        }

        private static Dictionary<string, object?> CreateSequencerCamera(JToken args, DependencyStatus status)
        {
            var dryRun = OptionalBool(args, "dryRun", false);
            var name = ReadString(args, "name") ?? "Cinemachine Sequencer Camera";
            var parent = TryString(args, "parentPath", out var parentPath) ? ResolveTransform(parentPath) : null;
            var subject = TryString(args, "targetPath", out var targetPath) ? ResolveTransform(targetPath) : null;
            var position = args["position"] != null ? ReadVector3(args["position"]!, "position") : (Vector3?)null;
            var loop = OptionalBool(args, "loop", true);
            var ensureBrain = OptionalBool(args, "ensureBrain", false);
            var createCameraIfMissing = OptionalBool(args, "createCameraIfMissing", false);
            var gameCamera = ensureBrain ? ResolveCamera(args, allowCreate: createCameraIfMissing, dryRun: dryRun) : null;
            var shotSpecs = ReadSequencerShotSpecs(args);
            var warnings = new List<string>();

            if (shotSpecs.Length == 0)
            {
                shotSpecs = new[]
                {
                    new SequencerShotSpec("Sequencer Shot 1", 1d, 35f, 6f, 10, "Cut", 0f),
                    new SequencerShotSpec("Sequencer Shot 2", 1d, 24f, 3f, 20, "EaseInOut", 0.5f),
                };
            }

            if (dryRun)
            {
                var preview = CreateCommandEnvelope(CinemachineSequencersUri, status, dryRun);
                preview["wouldCreateSequencer"] = name;
                preview["parentPath"] = parent != null ? GetTransformPath(parent) : null;
                preview["targetPath"] = subject != null ? GetTransformPath(subject) : null;
                preview["loop"] = loop;
                preview["shotCount"] = shotSpecs.Length;
                preview["wouldEnsureBrain"] = ensureBrain;
                preview["wouldCreateCamera"] = ensureBrain && gameCamera == null && createCameraIfMissing ? ReadString(args, "cameraName") ?? "Main Camera" : null;
                preview["warnings"] = warnings.ToArray();
                return preview;
            }

            Undo.IncrementCurrentGroup();
            var undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Create Cinemachine Sequencer Camera");

            var gameObject = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(gameObject, "Create Cinemachine Sequencer Camera");
            if (parent != null)
            {
                Undo.SetTransformParent(gameObject.transform, parent, "Parent Cinemachine Sequencer Camera");
            }

            if (position.HasValue)
            {
                Undo.RecordObject(gameObject.transform, "Position Cinemachine Sequencer Camera");
                gameObject.transform.position = position.Value;
            }
            else if (subject != null)
            {
                Undo.RecordObject(gameObject.transform, "Position Cinemachine Sequencer Camera");
                gameObject.transform.position = subject.position;
            }

            var sequencer = Undo.AddComponent(gameObject, status.Sequencer.SequencerCameraType!);
            TrySetMember(sequencer, "Loop", loop, warnings);

            Component? brain = null;
            if (ensureBrain)
            {
                if (gameCamera == null)
                {
                    throw new ArgumentException("ensureBrain requires cameraPath/cameraInstanceId, an existing MainCamera, or createCameraIfMissing=true.");
                }

                brain = gameCamera.GetComponent(status.Cinemachine.BrainType!) ?? Undo.AddComponent(gameCamera.gameObject, status.Cinemachine.BrainType!);
                MarkChanged(brain);
            }

            var childCameras = new List<Component>();
            foreach (var spec in shotSpecs)
            {
                childCameras.Add(CreateSequencerChildCamera(sequencer.transform, spec, subject, status, warnings));
            }

            SetSequencerInstructions(sequencer, childCameras, shotSpecs, status, warnings);
            MarkChanged(sequencer);
            Undo.CollapseUndoOperations(undoGroup);

            var result = CreateCommandEnvelope(SequencerDetailUri(sequencer), status, dryRun);
            result["target"] = DescribeSequencer(sequencer, detail: true);
            result["brain"] = brain != null ? DescribeBrain(brain) : null;
            result["warnings"] = warnings.ToArray();
            result["visualQaHint"] = "Activate or raise priority on the Sequencer Camera, then use screenshot-camera on the Unity Camera with CinemachineBrain at start, after first hold, and during a blend.";
            return result;
        }

        private static Dictionary<string, object?> CreateTimelineDirector(JToken args, DependencyStatus status)
        {
            var dryRun = OptionalBool(args, "dryRun", false);
            var name = ReadString(args, "name") ?? "Timeline Director";
            var assetPath = ReadString(args, "assetPath");
            var createAsset = OptionalBool(args, "createAsset", !string.IsNullOrWhiteSpace(assetPath));

            if (dryRun)
            {
                var preview = CreateCommandEnvelope(TimelineDirectorsUri, status, dryRun);
                preview["wouldCreateDirector"] = name;
                preview["wouldCreateAsset"] = createAsset ? assetPath : null;
                return preview;
            }

            var gameObject = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(gameObject, "Create Timeline Director");
            var director = Undo.AddComponent<PlayableDirector>(gameObject);

            if (createAsset)
            {
                assetPath = EnsureTimelineAssetPath(assetPath ?? $"Assets/{SanitizeAssetName(name)}.playable");
                var asset = CreateTimelineAsset(assetPath, status);
                Undo.RecordObject(director, "Assign Timeline Asset");
                director.playableAsset = asset as PlayableAsset;
            }
            else if (!string.IsNullOrWhiteSpace(assetPath))
            {
                var asset = AssetDatabase.LoadAssetAtPath<PlayableAsset>(assetPath);
                if (asset == null)
                {
                    throw new ArgumentException($"Timeline assetPath '{assetPath}' did not load as PlayableAsset.");
                }

                Undo.RecordObject(director, "Assign Timeline Asset");
                director.playableAsset = asset;
            }

            if (TryDouble(args, "time", out var time))
            {
                director.time = Math.Max(0d, time);
            }

            MarkChanged(director);
            var result = CreateCommandEnvelope(DirectorDetailUri(director), status, dryRun);
            result["target"] = DescribeDirector(director, detail: true);
            return result;
        }

        private static Dictionary<string, object?> CreateShotSequence(JToken args, DependencyStatus status)
        {
            var dryRun = OptionalBool(args, "dryRun", false);
            var warnings = new List<string>();
            var director = ResolveDirectorFromArgs(args);
            var assetPath = ReadString(args, "assetPath") ?? "Assets/Cinematics/EndingSessionSlowMoZoom.playable";
            var gameCamera = ResolveCamera(args, allowCreate: true, dryRun: dryRun);
            if (gameCamera == null && !dryRun)
            {
                throw new ArgumentException("Shot sequence requires or creates a Unity Camera for CinemachineBrain binding.");
            }

            var shotSpecs = ReadShotSpecs(args);
            if (shotSpecs.Length == 0)
            {
                shotSpecs = new[]
                {
                    new ShotSpec("Ending Wide Shot", 0d, 1.5d, 35f, 6f, 10),
                    new ShotSpec("Ending SlowMo Zoom", 1.25d, 2.25d, 22f, 2.5f, 20),
                };
            }

            if (dryRun)
            {
                var preview = CreateCommandEnvelope(TimelineDirectorsUri, status, dryRun);
                preview["wouldCreateOrUseDirector"] = director != null ? GetTransformPath(director.transform) : ReadString(args, "directorName") ?? "Ending Session Director";
                preview["wouldCreateAsset"] = director != null && director.playableAsset != null ? AssetDatabase.GetAssetPath(director.playableAsset) : assetPath;
                preview["wouldCreateOrUseCamera"] = gameCamera != null ? GetTransformPath(gameCamera.transform) : ReadString(args, "cameraName") ?? "Main Camera";
                preview["shotCount"] = shotSpecs.Length;
                preview["runtimeSlowMoNote"] = RuntimeSlowMoNote();
                return preview;
            }

            Undo.IncrementCurrentGroup();
            var undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Create Cinemachine Timeline Shot Sequence");

            var brain = gameCamera!.GetComponent(status.Cinemachine.BrainType!) ?? Undo.AddComponent(gameCamera.gameObject, status.Cinemachine.BrainType!);
            director ??= CreateDirectorObject(ReadString(args, "directorName") ?? "Ending Session Director");
            var timelineAsset = director.playableAsset as UnityEngine.Object;
            if (timelineAsset == null || !status.Timeline.AssetType!.IsInstanceOfType(timelineAsset))
            {
                timelineAsset = (UnityEngine.Object)CreateTimelineAsset(EnsureTimelineAssetPath(assetPath), status);
                Undo.RecordObject(director, "Assign Timeline Asset");
                director.playableAsset = timelineAsset as PlayableAsset;
            }

            var track = CreateCinemachineTrack(timelineAsset, status, ReadString(args, "trackName") ?? "Cinemachine Shots");
            director.SetGenericBinding(track as UnityEngine.Object, brain as UnityEngine.Object);

            var subject = TryString(args, "targetPath", out var targetPath) ? ResolveTransform(targetPath) : null;
            var createdShots = new List<object?>();
            foreach (var spec in shotSpecs)
            {
                var camera = CreateShotCamera(spec, subject, status, warnings);
                var clip = CreateCinemachineShotClip(track, camera, spec, warnings);
                createdShots.Add(new Dictionary<string, object?>
                {
                    ["camera"] = DescribeCinemachineCamera(camera, detail: false),
                    ["clip"] = DescribeClip(clip),
                });
            }

            MarkChanged(director);
            if (timelineAsset != null)
            {
                EditorUtility.SetDirty(timelineAsset);
                AssetDatabase.SaveAssetIfDirty(timelineAsset);
            }

            Undo.CollapseUndoOperations(undoGroup);

            var result = CreateCommandEnvelope(DirectorDetailUri(director), status, dryRun);
            result["target"] = DescribeDirector(director, detail: true);
            result["brain"] = DescribeBrain(brain);
            result["shots"] = createdShots.ToArray();
            result["warnings"] = warnings.ToArray();
            result["runtimeSlowMoNote"] = RuntimeSlowMoNote();
            result["visualQaHint"] = "Use screenshot-camera on the bound Unity Camera after scrubbing the director.";
            return result;
        }

        private static Dictionary<string, object?> PreviewDirector(JToken args, DependencyStatus status)
        {
            var dryRun = OptionalBool(args, "dryRun", false);
            var director = RequireDirector(args);
            var action = (ReadString(args, "action") ?? "evaluate").ToLowerInvariant();
            var time = OptionalDouble(args, "time");
            var previousTime = director.time;
            var previousState = director.state.ToString();
            var restoredTime = false;

            if (!dryRun)
            {
                Undo.RecordObject(director, "Preview Timeline Director");
                if (time.HasValue)
                {
                    director.time = Math.Max(0d, time.Value);
                }

                switch (action)
                {
                    case "evaluate":
                    case "scrub":
                        director.Evaluate();
                        break;
                    case "play":
                        director.Play();
                        break;
                    case "stop":
                        director.Stop();
                        break;
                    default:
                        throw new ArgumentException("action must be evaluate, scrub, play, or stop.");
                }

                if ((string.Equals(action, "evaluate", StringComparison.Ordinal) || string.Equals(action, "scrub", StringComparison.Ordinal))
                    && Math.Abs(director.time - previousTime) > 0.0001d)
                {
                    director.time = previousTime;
                    director.Evaluate();
                    restoredTime = Math.Abs(director.time - previousTime) <= 0.0001d;
                }
            }

            var result = CreateCommandEnvelope(DirectorDetailUri(director), status, dryRun);
            result["action"] = action;
            result["previousTime"] = Math.Round(previousTime, 4);
            result["previousState"] = previousState;
            result["restoredTime"] = restoredTime;
            result["stateAfterPreview"] = director.state.ToString();
            result["target"] = DescribeDirector(director, detail: true);
            result["visualQaHint"] = "For camera-composed validation, use screenshot-camera on the bound Unity Camera rather than Game View screenshots.";
            return result;
        }

        private static DependencyStatus GetDependencyStatus()
        {
            var packageInfo = PackageManagerPackageInfo.FindForPackageName(CinemachinePackageName);
            var cinemachineCamera = FindLoadedType("Unity.Cinemachine.CinemachineCamera");
            var cinemachineLegacyVirtualCamera = FindLoadedType("Unity.Cinemachine.CinemachineVirtualCamera");
            var cinemachine2VirtualCamera = FindLoadedType("Cinemachine.CinemachineVirtualCamera");
            var cinemachineBrain = FindLoadedType("Unity.Cinemachine.CinemachineBrain");
            var cinemachineTrack = FindLoadedType("Unity.Cinemachine.CinemachineTrack");
            var cinemachineShot = FindLoadedType("Unity.Cinemachine.CinemachineShot");
            var cinemachineSequencerCamera = FindLoadedType("Unity.Cinemachine.CinemachineSequencerCamera");
            var cinemachineSequencerInstruction = cinemachineSequencerCamera?.GetNestedType("Instruction", BindingFlags.Public | BindingFlags.NonPublic);
            var cinemachineVirtualCameraBase = FindLoadedType("Unity.Cinemachine.CinemachineVirtualCameraBase");
            var cinemachineBlendDefinition = FindLoadedType("Unity.Cinemachine.CinemachineBlendDefinition");
            var cinemachineSplineDolly = FindLoadedType("Unity.Cinemachine.CinemachineSplineDolly");
            var cinemachineSplineRoll = FindLoadedType("Unity.Cinemachine.CinemachineSplineRoll");
            var cinemachineInputAxisController = FindLoadedType("Unity.Cinemachine.CinemachineInputAxisController");
            var cinemachineInputAxis = FindLoadedType("Unity.Cinemachine.InputAxis");
            var cinemachineInputAxisOwner = FindLoadedType("Unity.Cinemachine.IInputAxisOwner");
            var cinemachineBlenderSettings = FindLoadedType("Unity.Cinemachine.CinemachineBlenderSettings");
            var cinemachineImpulseSource = FindLoadedType("Unity.Cinemachine.CinemachineImpulseSource");
            var cinemachineImpulseListener = FindLoadedType("Unity.Cinemachine.CinemachineImpulseListener");
            var cinemachineCollisionImpulseSource = FindLoadedType("Unity.Cinemachine.CinemachineCollisionImpulseSource");
            var cinemachineExternalImpulseListener = FindLoadedType("Unity.Cinemachine.CinemachineExternalImpulseListener");
            var cinemachineConfiner2D = FindLoadedType("Unity.Cinemachine.CinemachineConfiner2D");
            var cinemachineConfiner3D = FindLoadedType("Unity.Cinemachine.CinemachineConfiner3D");
            var cinemachineObsoleteConfiner = FindLoadedType("Unity.Cinemachine.CinemachineConfiner");
            var collider2D = FindLoadedType("UnityEngine.Collider2D");
            var collider3D = FindLoadedType("UnityEngine.Collider");
            var cinemachineApiFamily = DetermineCinemachineApiFamily(packageInfo != null, packageInfo?.version, cinemachineCamera, cinemachineLegacyVirtualCamera, cinemachine2VirtualCamera);
            var timelineAsset = FindLoadedType("UnityEngine.Timeline.TimelineAsset");
            var trackAsset = FindLoadedType("UnityEngine.Timeline.TrackAsset");
            var timelineClip = FindLoadedType("UnityEngine.Timeline.TimelineClip");
            var playableDirector = FindLoadedType("UnityEngine.Playables.PlayableDirector");
            var splinesPackageInfo = PackageManagerPackageInfo.FindForPackageName(SplinesPackageName);
            var splineContainer = FindLoadedType("UnityEngine.Splines.SplineContainer");
            var inputSystemPackageInfo = PackageManagerPackageInfo.FindForPackageName(InputSystemPackageName);
            var inputActionReference = FindLoadedType("UnityEngine.InputSystem.InputActionReference");
            var playerInput = FindLoadedType("UnityEngine.InputSystem.PlayerInput");

            var cinemachine = new PackageStatus(
                CinemachinePackageName,
                packageInfo != null,
                packageInfo?.version,
                cinemachineApiFamily,
                cinemachineApiFamily == CinemachineApiFamilyCm3 && cinemachineCamera != null && cinemachineBrain != null,
                CinemachineVersionDefineActive,
                cinemachineCamera,
                cinemachineBrain,
                cinemachineTrack,
                cinemachineShot);
            var sequencer = new SequencerCameraStatus(
                packageInfo != null,
                packageInfo?.version,
                cinemachineApiFamily,
                CinemachineVersionDefineActive,
                cinemachineSequencerCamera,
                cinemachineSequencerInstruction,
                cinemachineCamera,
                cinemachineVirtualCameraBase,
                cinemachineBlendDefinition,
                cinemachineBrain);
            var timelinePackageInfo = PackageManagerPackageInfo.FindForPackageName(TimelinePackageName);
            var timeline = new PackageStatus(
                TimelinePackageName,
                timelinePackageInfo != null,
                timelinePackageInfo?.version,
                null,
                timelineAsset != null && trackAsset != null && timelineClip != null && playableDirector != null,
                TimelineVersionDefineActive,
                timelineAsset,
                trackAsset,
                timelineClip,
                playableDirector);
            var splinesDolly = new AdvancedHelperStatus(
                "splinesDolly",
                "Splines Dolly",
                packageInfo != null,
                packageInfo?.version,
                cinemachineApiFamily,
                CinemachineVersionDefineActive,
                cinemachineSplineDolly,
                "Unity.Cinemachine.CinemachineSplineDolly",
                SplinesPackageName,
                splinesPackageInfo != null,
                splinesPackageInfo?.version,
                splineContainer,
                "UnityEngine.Splines.SplineContainer",
                cinemachineSplineRoll,
                "Unity.Cinemachine.CinemachineSplineRoll",
                optionalVersionDefineActive: SplinesVersionDefineActive);
            var inputSystem = new OptionalPackageStatus(
                InputSystemPackageName,
                inputSystemPackageInfo != null,
                inputSystemPackageInfo?.version,
                inputActionReference,
                "UnityEngine.InputSystem.InputActionReference",
                playerInput,
                "UnityEngine.InputSystem.PlayerInput");
            var inputAxisController = new AdvancedHelperStatus(
                "inputAxisController",
                "InputAxisController",
                packageInfo != null,
                packageInfo?.version,
                cinemachineApiFamily,
                CinemachineVersionDefineActive,
                cinemachineInputAxisController,
                "Unity.Cinemachine.CinemachineInputAxisController",
                null,
                false,
                null,
                null,
                null,
                cinemachineInputAxis,
                "Unity.Cinemachine.InputAxis",
                cinemachineInputAxisOwner,
                "Unity.Cinemachine.IInputAxisOwner");
            var blenderSettings = new AdvancedHelperStatus(
                "blenderSettings",
                "Blender Settings/custom blends",
                packageInfo != null,
                packageInfo?.version,
                cinemachineApiFamily,
                CinemachineVersionDefineActive,
                cinemachineBlenderSettings,
                "Unity.Cinemachine.CinemachineBlenderSettings",
                null,
                false,
                null,
                null,
                null);
            var impulse = new AdvancedHelperStatus(
                "impulse",
                "Impulse",
                packageInfo != null,
                packageInfo?.version,
                cinemachineApiFamily,
                CinemachineVersionDefineActive,
                cinemachineImpulseSource,
                "Unity.Cinemachine.CinemachineImpulseSource",
                null,
                false,
                null,
                null,
                null,
                cinemachineImpulseListener,
                "Unity.Cinemachine.CinemachineImpulseListener");
            var confiner2D = new AdvancedHelperStatus(
                "confiner2D",
                "Confiner2D",
                packageInfo != null,
                packageInfo?.version,
                cinemachineApiFamily,
                CinemachineVersionDefineActive,
                cinemachineConfiner2D,
                "Unity.Cinemachine.CinemachineConfiner2D",
                null,
                false,
                null,
                null,
                null,
                collider2D,
                "UnityEngine.Collider2D");
            var confiner3D = new AdvancedHelperStatus(
                "confiner3D",
                "Confiner3D",
                packageInfo != null,
                packageInfo?.version,
                cinemachineApiFamily,
                CinemachineVersionDefineActive,
                cinemachineConfiner3D,
                "Unity.Cinemachine.CinemachineConfiner3D",
                null,
                false,
                null,
                null,
                null,
                collider3D,
                "UnityEngine.Collider");
            return new DependencyStatus(cinemachine, sequencer, splinesDolly, inputAxisController, inputSystem, blenderSettings, impulse, cinemachineCollisionImpulseSource, cinemachineExternalImpulseListener, confiner2D, confiner3D, cinemachineObsoleteConfiner, timeline);
        }

        private static string DetermineCinemachineApiFamily(bool packageInstalled, string? packageVersion, Type? cm3CameraType, Type? cm3LegacyObsoleteCameraType, Type? cm2VirtualCameraType)
        {
            if (cm3CameraType != null)
            {
                return CinemachineApiFamilyCm3;
            }

            if (cm3LegacyObsoleteCameraType != null)
            {
                return CinemachineApiFamilyCm3LegacyObsolete;
            }

            if (cm2VirtualCameraType != null)
            {
                return CinemachineApiFamilyCm2;
            }

            if (packageInstalled)
            {
                if (IsPackageMajorVersion(packageVersion, 3))
                {
                    return CinemachineApiFamilyCm3;
                }

                if (IsPackageMajorVersion(packageVersion, 2))
                {
                    return CinemachineApiFamilyCm2;
                }
            }

            return CinemachineApiFamilyAbsent;
        }

        private static bool IsPackageMajorVersion(string? packageVersion, int major)
        {
            if (string.IsNullOrWhiteSpace(packageVersion))
            {
                return false;
            }

            var dotIndex = packageVersion!.IndexOf('.');
            var majorText = dotIndex >= 0 ? packageVersion.Substring(0, dotIndex) : packageVersion;
            return int.TryParse(majorText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMajor) && parsedMajor == major;
        }

        private static Type? FindLoadedType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type? type;
                try
                {
                    type = assembly.GetType(fullName, throwOnError: false);
                }
                catch
                {
                    continue;
                }

                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        private static IEnumerable<Component> EnumerateComponents(Type componentType)
        {
            foreach (var root in GetCurrentScene().GetRootGameObjects())
            {
                foreach (var component in root.GetComponentsInChildren(componentType, includeInactive: true).OfType<Component>())
                {
                    yield return component;
                }
            }
        }

        private static IEnumerable<Component> EnumerateComponentsImplementing(Type interfaceType)
        {
            foreach (var root in GetCurrentScene().GetRootGameObjects())
            {
                foreach (var component in root.GetComponentsInChildren<Component>(includeInactive: true))
                {
                    if (component != null && interfaceType.IsInstanceOfType(component))
                    {
                        yield return component;
                    }
                }
            }
        }

        private static IEnumerable<PlayableDirector> EnumerateDirectors()
        {
            return GetCurrentScene().GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<PlayableDirector>(includeInactive: true))
                .OrderBy(director => GetTransformPath(director.transform), StringComparer.Ordinal);
        }

        private static IEnumerable<string> EnumerateTimelineAssetPaths(DependencyStatus status)
        {
            return AssetDatabase.FindAssets("t:TimelineAsset")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Where(path =>
                {
                    var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                    return asset != null && status.Timeline.AssetType!.IsInstanceOfType(asset);
                })
                .OrderBy(path => path, StringComparer.Ordinal);
        }

        private static IEnumerable<UnityEngine.Object> EnumerateAssetsOfType(Type assetType)
        {
            var paths = AssetDatabase.FindAssets("t:" + assetType.Name)
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .OrderBy(path => path, StringComparer.Ordinal);
            foreach (var path in paths)
            {
                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                if (asset != null && assetType.IsInstanceOfType(asset))
                {
                    yield return asset;
                }
            }
        }

        private static Dictionary<string, object?> DescribeCinemachineCamera(Component camera, bool detail)
        {
            var result = new Dictionary<string, object?>
            {
                ["name"] = camera.name,
                ["path"] = GetTransformPath(camera.transform),
                ["instanceId"] = UnityObjectIdentity.GetLegacyInstanceId(camera),
                ["activeInHierarchy"] = camera.gameObject.activeInHierarchy,
                ["enabled"] = camera is Behaviour behaviour ? behaviour.enabled : null,
                ["detailUri"] = CinemachineDetailUri(camera),
                ["priority"] = ReadMember(camera, "Priority"),
                ["lens"] = DescribeLens(ReadMember(camera, "Lens")),
                ["target"] = DescribeObjectReference(ReadMember(camera, "Target") ?? ReadMember(camera, "TrackingTarget") ?? ReadMember(camera, "Follow") ?? ReadMember(camera, "LookAt")),
            };

            if (detail)
            {
                result["unityTransform"] = DescribeTransform(camera.transform);
                result["safeSetFields"] = new[] { "targetPath", "priority", "lens.fieldOfView", "lens.orthographicSize", "lens.nearClipPlane", "lens.farClipPlane", "enabled" };
            }

            return result;
        }

        private static Dictionary<string, object?> DescribeSplineContainer(Component container, DependencyStatus status)
        {
            var result = DescribeAdvancedComponent(container);
            var spline = ReadMember(container, "Spline");
            var splines = ReadMember(container, "Splines");
            result["spline"] = DescribeMemberValue(spline);
            result["splines"] = DescribeMemberValue(splines);
            result["splineRoll"] = DescribeObjectReferenceOrNull(GetOptionalComponent(container.gameObject, status.SplinesDolly.SecondaryHelperType));
            result["hasSplineRoll"] = GetOptionalComponent(container.gameObject, status.SplinesDolly.SecondaryHelperType) != null;
            return result;
        }

        private static Dictionary<string, object?> DescribeSplineDollyCamera(Component camera, DependencyStatus status)
        {
            var result = DescribeCinemachineCamera(camera, detail: false);
            var dolly = camera.gameObject.GetComponent(status.SplinesDolly.HelperType!);
            result["splineDolly"] = dolly != null ? DescribeSplinesDolly(dolly, status) : null;
            result["splineRoll"] = DescribeObjectReferenceOrNull(GetOptionalComponent(camera.gameObject, status.SplinesDolly.SecondaryHelperType));
            result["hasSplineRoll"] = GetOptionalComponent(camera.gameObject, status.SplinesDolly.SecondaryHelperType) != null;
            return result;
        }

        private static Dictionary<string, object?> DescribeSplinesDolly(Component dolly, DependencyStatus status)
        {
            var result = DescribeAdvancedComponent(dolly);
            var splineContainer = ReadFirstMember(dolly, new[] { "Spline", "SplineContainer", "Container" }) as Component;
            var camera = status.Cinemachine.CameraType == null ? null : dolly.GetComponent(status.Cinemachine.CameraType);
            result["camera"] = camera != null ? DescribeCinemachineCamera(camera, detail: false) : null;
            result["splineContainer"] = splineContainer != null ? DescribeSplineContainer(splineContainer, status) : null;
            result["cameraPosition"] = new Dictionary<string, object?>
            {
                ["value"] = DescribeMemberValue(ReadFirstMember(dolly, new[] { "CameraPosition", "Position" })),
                ["units"] = DescribeMemberValue(ReadFirstMember(dolly, new[] { "PositionUnits", "Units" })),
            };
            result["autoDolly"] = DescribeMemberValue(ReadMember(dolly, "AutoDolly"));
            result["autoDollyMode"] = DescribeAutoDollyMode(ReadMember(dolly, "AutoDolly"));
            result["splineRoll"] = DescribeObjectReferenceOrNull(GetOptionalComponent(splineContainer?.gameObject, status.SplinesDolly.SecondaryHelperType) ?? GetOptionalComponent(dolly.gameObject, status.SplinesDolly.SecondaryHelperType));
            result["hasSplineRoll"] = GetOptionalComponent(splineContainer?.gameObject, status.SplinesDolly.SecondaryHelperType) != null || GetOptionalComponent(dolly.gameObject, status.SplinesDolly.SecondaryHelperType) != null;
            result["warnings"] = SplineDollyWarnings(dolly, splineContainer, status).ToArray();
            return result;
        }

        private static Dictionary<string, object?> DescribeInputAxisController(Component controller, DependencyStatus status)
        {
            var result = DescribeAdvancedComponent(controller);
            AddMemberIfPresent(result, controller, "PlayerIndex", "playerIndex");
            AddMemberIfPresent(result, controller, "ScanRecursively", "scanRecursively");
            AddMemberIfPresent(result, controller, "SuppressInputWhileDragging", "suppressInputWhileDragging");
            AddMemberIfPresent(result, controller, "Controllers", "controllers");
            result["inputSystem"] = status.InputSystem.ToDictionary();
            result["inputSystemReferences"] = status.InputSystem.Available
                ? DescribeInputSystemReferences(controller, status).Take(MaxAdvancedHelperRows).ToArray()
                : Array.Empty<Dictionary<string, object?>>();
            result["axisOwnerCandidate"] = status.InputAxisController.TertiaryHelperType == null
                ? null
                : DescribeNearestAxisOwner(controller, status.InputAxisController.TertiaryHelperType);
            return result;
        }

        private static Dictionary<string, object?> DescribeBlenderSettingsAsset(UnityEngine.Object asset, HashSet<string> cameraNames)
        {
            var result = DescribeAssetObject(asset);
            var specs = ReadCustomBlendSpecs(ReadMember(asset, "CustomBlends") ?? ReadMember(asset, "m_CustomBlends"));
            var warnings = new List<string>();
            AddBlendValidationWarnings(specs, cameraNames, warnings);
            result["blendCount"] = Math.Min(specs.Length, MaxCustomBlendEntries);
            result["blendEntriesCapped"] = specs.Length > MaxCustomBlendEntries;
            result["usesAnyCamera"] = specs.Any(UsesAnyCamera);
            result["blends"] = specs.Take(MaxCustomBlendEntries).Select(DescribeBlendSpec).ToArray();
            result["warnings"] = warnings.ToArray();
            return result;
        }

        private static Dictionary<string, object?> DescribeBrainCustomBlends(Component brain, HashSet<string> cameraNames)
        {
            var customBlends = ReadMember(brain, "CustomBlends");
            var result = DescribeBrain(brain);
            var specs = ReadCustomBlendSpecs(customBlends == null ? null : ReadMember(customBlends, "CustomBlends"));
            var warnings = new List<string>();
            AddBlendValidationWarnings(specs, cameraNames, warnings);
            result["customBlendsAsset"] = customBlends is UnityEngine.Object asset ? DescribeAssetObject(asset) : null;
            result["blendCount"] = Math.Min(specs.Length, MaxCustomBlendEntries);
            result["blendEntriesCapped"] = specs.Length > MaxCustomBlendEntries;
            result["usesAnyCamera"] = specs.Any(UsesAnyCamera);
            result["blends"] = specs.Take(MaxCustomBlendEntries).Select(DescribeBlendSpec).ToArray();
            result["warnings"] = warnings.ToArray();
            return result;
        }

        private static Dictionary<string, object?> DescribeImpulseComponent(Component impulse)
        {
            var result = DescribeAdvancedComponent(impulse);
            AddMemberIfPresent(result, impulse, "ImpulseDefinition", "impulseDefinition");
            AddMemberIfPresent(result, impulse, "Channel", "channel");
            AddMemberIfPresent(result, impulse, "ImpulseChannel", "impulseChannel");
            AddMemberIfPresent(result, impulse, "Gain", "gain");
            AddMemberIfPresent(result, impulse, "Use2DDistance", "use2DDistance");
            AddMemberIfPresent(result, impulse, "ReactionSettings", "reactionSettings");
            return result;
        }

        private static Dictionary<string, object?>? DescribeNearestAxisOwner(Component controller, Type axisOwnerType)
        {
            for (var current = controller.transform; current != null; current = current.parent)
            {
                foreach (var component in current.GetComponents<Component>())
                {
                    if (component != null && !ReferenceEquals(component, controller) && axisOwnerType.IsInstanceOfType(component))
                    {
                        return DescribeAdvancedComponent(component);
                    }
                }
            }

            return null;
        }

        private static IEnumerable<string> InputAxisControllerWarnings(Component[] controllers, Component[] owners, DependencyStatus status)
        {
            if (controllers.Length > 0 && owners.Length == 0)
            {
                yield return "CinemachineInputAxisController components exist, but no loaded IInputAxisOwner implementers were found in current stage.";
            }

            if (!status.InputSystem.Available)
            {
                yield return "Input System-specific inspection is gated off because com.unity.inputsystem and/or InputActionReference/PlayerInput types are unavailable.";
            }
        }

        private static IEnumerable<string> ImpulseWarnings(Component[] sources, Component[] listeners)
        {
            if (sources.Length > 0 && listeners.Length == 0)
            {
                yield return "Impulse sources exist, but no CinemachineImpulseListener was found; shake will not be received by Cinemachine cameras.";
            }

            if (listeners.Length > 0 && sources.Length == 0)
            {
                yield return "Impulse listeners exist, but no CinemachineImpulseSource was found; provide an explicit game-owned trigger source before QA.";
            }

            var sourceChannels = sources.Select(ImpulseChannelLabel).Where(value => value.Length > 0).ToHashSet(StringComparer.Ordinal);
            var listenerChannels = listeners.Select(ImpulseChannelLabel).Where(value => value.Length > 0).ToHashSet(StringComparer.Ordinal);
            if (sourceChannels.Count > 0 && listenerChannels.Count > 0 && !sourceChannels.Overlaps(listenerChannels))
            {
                yield return "Impulse source and listener channel labels do not visibly overlap; verify channel masks before tuning gain or amplitude.";
            }
        }

        private static string ImpulseChannelLabel(Component impulse)
        {
            var channel = ReadMember(impulse, "Channel") ?? ReadMember(impulse, "ImpulseChannel");
            return channel == null ? string.Empty : Convert.ToString(DescribeMemberValue(channel), CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private static Dictionary<string, object?> DescribeConfiner(Component confiner)
        {
            var result = DescribeAdvancedComponent(confiner);
            var boundingShape2D = ReadMember(confiner, "BoundingShape2D");
            var boundingVolume = ReadMember(confiner, "BoundingVolume");
            result["boundingShape2D"] = DescribeMemberValue(boundingShape2D);
            result["boundingVolume"] = DescribeMemberValue(boundingVolume);
            AddMemberIfPresent(result, confiner, "ConfineMode", "confineMode");
            AddMemberIfPresent(result, confiner, "Damping", "damping");
            AddMemberIfPresent(result, confiner, "SlowingDistance", "slowingDistance");
            result["cacheStatus"] = DescribeConfinerCacheStatus(confiner);
            var warnings = new List<string>();
            if (boundingShape2D == null && confiner.GetType().Name.Contains("2D", StringComparison.Ordinal))
            {
                warnings.Add("Confiner2D has no BoundingShape2D assigned.");
            }

            if (boundingVolume == null && confiner.GetType().Name.Contains("3D", StringComparison.Ordinal))
            {
                warnings.Add("Confiner3D has no BoundingVolume assigned.");
            }

            result["warnings"] = warnings.ToArray();
            return result;
        }

        private static Dictionary<string, object?> DescribeConfinerCacheStatus(Component confiner)
        {
            var type = confiner.GetType();
            var cacheMembers = new Dictionary<string, object?>(StringComparer.Ordinal);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var property in type.GetProperties(flags))
            {
                if (!property.CanRead || property.GetIndexParameters().Length != 0 || !property.Name.Contains("Cache", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    cacheMembers[property.Name] = DescribeMemberValue(property.GetValue(confiner));
                }
                catch
                {
                    cacheMembers[property.Name] = "unreadable";
                }
            }

            foreach (var field in type.GetFields(flags))
            {
                if (!field.Name.Contains("Cache", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    cacheMembers[field.Name] = DescribeMemberValue(field.GetValue(confiner));
                }
                catch
                {
                    cacheMembers[field.Name] = "unreadable";
                }
            }

            return new Dictionary<string, object?>
            {
                ["invalidateBoundingShapeCacheAvailable"] = type.GetMethod("InvalidateBoundingShapeCache", flags, null, Type.EmptyTypes, null) != null,
                ["invalidateBoundingVolumeCacheAvailable"] = type.GetMethod("InvalidateBoundingVolumeCache", flags, null, Type.EmptyTypes, null) != null,
                ["invalidateLensCacheAvailable"] = type.GetMethod("InvalidateLensCache", flags, null, Type.EmptyTypes, null) != null,
                ["cacheMembers"] = cacheMembers,
                ["notes"] = new[]
                {
                    "Cache status depends on members exposed by the loaded Cinemachine version.",
                    "Confiner2D bounding-shape cache invalidation can be expensive; run it only after explicit collider topology changes.",
                    "Lens cache invalidation is separate when exposed; run it explicitly after lens/FOV/orthographic-size changes.",
                },
            };
        }

        private static Dictionary<string, object?> DescribeAdvancedComponent(Component component)
        {
            return new Dictionary<string, object?>
            {
                ["name"] = component.name,
                ["path"] = GetTransformPath(component.transform),
                ["instanceId"] = UnityObjectIdentity.GetLegacyInstanceId(component),
                ["type"] = component.GetType().FullName,
                ["activeInHierarchy"] = component.gameObject.activeInHierarchy,
                ["enabled"] = component is Behaviour behaviour ? behaviour.enabled : null,
            };
        }

        private static IEnumerable<Dictionary<string, object?>> DescribeCustomBlends(object? blends)
        {
            foreach (var blend in EnumerateObjects(blends))
            {
                yield return new Dictionary<string, object?>
                {
                    ["from"] = Convert.ToString(ReadMember(blend, "From"), CultureInfo.InvariantCulture),
                    ["to"] = Convert.ToString(ReadMember(blend, "To"), CultureInfo.InvariantCulture),
                    ["blend"] = DescribeBlend(ReadMember(blend, "Blend")),
                };
            }
        }

        private static IEnumerable<Dictionary<string, object?>> DescribeInputSystemReferences(Component component, DependencyStatus status)
        {
            foreach (var item in EnumerateMemberValues(component))
            {
                if (status.InputSystem.PrimaryType != null && status.InputSystem.PrimaryType.IsInstanceOfType(item.Value))
                {
                    yield return new Dictionary<string, object?>
                    {
                        ["member"] = item.Name,
                        ["kind"] = "InputActionReference",
                        ["value"] = DescribeMemberValue(item.Value),
                    };
                }
                else if (status.InputSystem.SecondaryType != null && status.InputSystem.SecondaryType.IsInstanceOfType(item.Value))
                {
                    yield return new Dictionary<string, object?>
                    {
                        ["member"] = item.Name,
                        ["kind"] = "PlayerInput",
                        ["value"] = DescribeMemberValue(item.Value),
                    };
                }
            }
        }

        private static Dictionary<string, object?> DescribeSequencer(Component sequencer, bool detail)
        {
            var warnings = new List<string>();
            var instructions = EnumerateInstructions(ReadMember(sequencer, "Instructions"))
                .Take(MaxSequencerInstructionRows + 1)
                .ToArray();
            var result = new Dictionary<string, object?>
            {
                ["name"] = sequencer.name,
                ["path"] = GetTransformPath(sequencer.transform),
                ["instanceId"] = UnityObjectIdentity.GetLegacyInstanceId(sequencer),
                ["activeInHierarchy"] = sequencer.gameObject.activeInHierarchy,
                ["enabled"] = sequencer is Behaviour behaviour ? behaviour.enabled : null,
                ["detailUri"] = SequencerDetailUri(sequencer),
                ["parentCameraPath"] = GetTransformPath(sequencer.transform),
                ["parentCamera"] = DescribeObjectReference(sequencer),
                ["priority"] = ReadMember(sequencer, "Priority"),
                ["lens"] = DescribeLens(ReadMember(sequencer, "Lens")),
                ["target"] = DescribeObjectReference(ReadMember(sequencer, "Target") ?? ReadMember(sequencer, "TrackingTarget") ?? ReadMember(sequencer, "Follow") ?? ReadMember(sequencer, "LookAt")),
                ["loop"] = ReadMember(sequencer, "Loop"),
                ["instructionCount"] = Math.Min(instructions.Length, MaxSequencerInstructionRows),
                ["instructionCap"] = MaxSequencerInstructionRows,
                ["instructionsCapped"] = instructions.Length > MaxSequencerInstructionRows,
            };

            var describedInstructions = instructions
                .Take(MaxSequencerInstructionRows)
                .Select((instruction, index) => DescribeSequencerInstruction(sequencer, instruction, index, warnings))
                .ToArray();
            result["instructions"] = describedInstructions;
            result["warnings"] = warnings.ToArray();

            if (detail)
            {
                result["unityTransform"] = DescribeTransform(sequencer.transform);
                result["safeAuthoringTool"] = "cinemachine-sequencer-create";
                result["previewQaHint"] = "Use screenshot-camera on a Unity Camera with CinemachineBrain at start, after first hold, and during blend.";
            }

            return result;
        }

        private static IEnumerable<object> EnumerateInstructions(object? instructions)
        {
            if (instructions is not System.Collections.IEnumerable enumerable)
            {
                yield break;
            }

            foreach (var instruction in enumerable)
            {
                if (instruction != null)
                {
                    yield return instruction;
                }
            }
        }

        private static Dictionary<string, object?> DescribeSequencerInstruction(Component sequencer, object instruction, int index, List<string> warnings)
        {
            var camera = ReadMember(instruction, "Camera");
            var holdSeconds = Round(ReadFloatMember(instruction, "Hold") ?? 0f);
            var blend = DescribeBlend(ReadMember(instruction, "Blend"));
            var blendSummary = blend.TryGetValue("summary", out var summary)
                ? Convert.ToString(summary, CultureInfo.InvariantCulture)
                : blend.TryGetValue("style", out var style)
                    ? Convert.ToString(style, CultureInfo.InvariantCulture)
                    : "missing blend";
            if (camera == null)
            {
                warnings.Add($"Instruction {index} has no child camera assigned.");
            }
            else if (camera is Component component && component.transform.parent != sequencer.transform)
            {
                warnings.Add($"Instruction {index} camera '{GetTransformPath(component.transform)}' is not a direct child of sequencer '{GetTransformPath(sequencer.transform)}'.");
            }

            return new Dictionary<string, object?>
            {
                ["index"] = index,
                ["camera"] = DescribeObjectReference(camera),
                ["holdSeconds"] = holdSeconds,
                ["blend"] = blend,
                ["summary"] = $"Hold {holdSeconds}s, blend {blendSummary}",
            };
        }

        private static Dictionary<string, object?> DescribeBlend(object? blend)
        {
            if (blend == null)
            {
                return new Dictionary<string, object?> { ["missing"] = true };
            }

            return new Dictionary<string, object?>
            {
                ["style"] = Convert.ToString(ReadMember(blend, "Style"), CultureInfo.InvariantCulture),
                ["time"] = Round(ReadFloatMember(blend, "Time")),
                ["blendTime"] = Round(ReadFloatMember(blend, "BlendTime")),
                ["summary"] = Convert.ToString(blend, CultureInfo.InvariantCulture),
            };
        }

        private static Dictionary<string, object?> DescribeBrain(Component brain)
        {
            var customBlends = ReadMember(brain, "CustomBlends");
            return new Dictionary<string, object?>
            {
                ["name"] = brain.name,
                ["path"] = GetTransformPath(brain.transform),
                ["instanceId"] = UnityObjectIdentity.GetLegacyInstanceId(brain),
                ["camera"] = DescribeUnityCamera(brain.GetComponent<Camera>()),
                ["enabled"] = brain is Behaviour behaviour ? behaviour.enabled : null,
                ["detailUri"] = BrainDetailUri(brain),
                ["defaultBlend"] = Convert.ToString(ReadMember(brain, "DefaultBlend"), CultureInfo.InvariantCulture),
                ["customBlendsAsset"] = customBlends is UnityEngine.Object asset ? DescribeAssetObject(asset) : null,
                ["fallbackBehavior"] = "Uses matching CustomBlends entry when available; otherwise falls back to DefaultBlend.",
            };
        }

        private static Dictionary<string, object?> DescribeUnityCamera(Camera? camera)
        {
            if (camera == null)
            {
                return new Dictionary<string, object?> { ["missing"] = true };
            }

            return new Dictionary<string, object?>
            {
                ["name"] = camera.name,
                ["path"] = GetTransformPath(camera.transform),
                ["instanceId"] = UnityObjectIdentity.GetLegacyInstanceId(camera),
                ["fieldOfView"] = Round(camera.fieldOfView),
                ["orthographic"] = camera.orthographic,
                ["orthographicSize"] = Round(camera.orthographicSize),
                ["nearClipPlane"] = Round(camera.nearClipPlane),
                ["farClipPlane"] = Round(camera.farClipPlane),
            };
        }

        private static Dictionary<string, object?> DescribeDirector(PlayableDirector director, bool detail)
        {
            var asset = director.playableAsset as UnityEngine.Object;
            var result = new Dictionary<string, object?>
            {
                ["name"] = director.name,
                ["path"] = GetTransformPath(director.transform),
                ["instanceId"] = UnityObjectIdentity.GetLegacyInstanceId(director),
                ["detailUri"] = DirectorDetailUri(director),
                ["time"] = Math.Round(director.time, 4),
                ["duration"] = Math.Round(director.duration, 4),
                ["state"] = director.state.ToString(),
                ["playOnAwake"] = director.playOnAwake,
                ["asset"] = asset != null ? DescribeAssetObject(asset) : null,
            };

            if (detail && asset != null)
            {
                result["clips"] = DescribeTimelineClips(asset).Take(MaxClipRows).ToArray();
                result["maxClipRows"] = MaxClipRows;
                result["tracks"] = DescribeTimelineTracks(asset).ToArray();
                result["signals"] = DescribeTimelineSignals(asset).ToArray();
                result["bindings"] = DescribeTimelineBindings(director, asset).ToArray();
            }

            return result;
        }

        private static Dictionary<string, object?> DescribeTimelineAsset(string path, DependencyStatus status, bool detail)
        {
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            var result = new Dictionary<string, object?>
            {
                ["name"] = asset != null ? asset.name : System.IO.Path.GetFileNameWithoutExtension(path),
                ["path"] = path,
                ["guid"] = AssetDatabase.AssetPathToGUID(path),
                ["detailUri"] = TimelineAssetDetailUri(path),
                ["type"] = asset != null ? asset.GetType().FullName : null,
            };
            if (detail && asset != null)
            {
                result["clips"] = DescribeTimelineClips(asset).Take(MaxClipRows).ToArray();
                result["maxClipRows"] = MaxClipRows;
                result["tracks"] = DescribeTimelineTracks(asset).ToArray();
                result["signals"] = DescribeTimelineSignals(asset).ToArray();
            }

            return result;
        }

        private static IEnumerable<Dictionary<string, object?>> DescribeTimelineTracks(UnityEngine.Object timelineAsset)
        {
            foreach (var track in InvokeEnumerable(timelineAsset, "GetOutputTracks"))
            {
                yield return DescribeTrack(track);
            }
        }

        private static Dictionary<string, object?> DescribeTrack(object track)
        {
            return new Dictionary<string, object?>
            {
                ["name"] = Convert.ToString(ReadMember(track, "name"), CultureInfo.InvariantCulture),
                ["type"] = track.GetType().FullName,
                ["muted"] = ReadMember(track, "muted"),
                ["clipCount"] = InvokeEnumerable(track, "GetClips").Count(),
                ["clips"] = InvokeEnumerable(track, "GetClips").Take(MaxClipRows).Select(DescribeClip).ToArray(),
            };
        }

        private static IEnumerable<Dictionary<string, object?>> DescribeTimelineSignals(UnityEngine.Object timelineAsset)
        {
            foreach (var track in InvokeEnumerable(timelineAsset, "GetOutputTracks"))
            {
                foreach (var marker in InvokeEnumerable(track, "GetMarkers"))
                {
                    yield return DescribeSignal(marker, track);
                }
            }
        }

        private static Dictionary<string, object?> DescribeSignal(object marker, object track)
        {
            return new Dictionary<string, object?>
            {
                ["name"] = Convert.ToString(ReadMember(marker, "name"), CultureInfo.InvariantCulture),
                ["type"] = marker.GetType().FullName,
                ["track"] = Convert.ToString(ReadMember(track, "name"), CultureInfo.InvariantCulture),
                ["time"] = ReadMember(marker, "time"),
                ["asset"] = DescribeMaybeAsset(ReadMember(marker, "asset") ?? ReadMember(marker, "signalAsset")),
            };
        }

        private static IEnumerable<Dictionary<string, object?>> DescribeTimelineBindings(PlayableDirector director, UnityEngine.Object timelineAsset)
        {
            foreach (var track in InvokeEnumerable(timelineAsset, "GetOutputTracks"))
            {
                var trackObject = track as UnityEngine.Object;
                var boundObject = trackObject != null ? director.GetGenericBinding(trackObject) : null;
                yield return new Dictionary<string, object?>
                {
                    ["trackName"] = Convert.ToString(ReadMember(track, "name"), CultureInfo.InvariantCulture),
                    ["trackType"] = track.GetType().FullName,
                    ["boundObject"] = DescribeObjectReference(boundObject),
                };
            }
        }

        private static IEnumerable<Dictionary<string, object?>> DescribeTimelineClips(UnityEngine.Object timelineAsset)
        {
            var tracks = InvokeEnumerable(timelineAsset, "GetOutputTracks");
            foreach (var track in tracks)
            {
                foreach (var clip in InvokeEnumerable(track, "GetClips"))
                {
                    yield return DescribeClip(clip);
                }
            }
        }

        private static Dictionary<string, object?> DescribeClip(object? clip)
        {
            if (clip == null)
            {
                return new Dictionary<string, object?> { ["missing"] = true };
            }

            return new Dictionary<string, object?>
            {
                ["displayName"] = ReadMember(clip, "displayName"),
                ["start"] = ReadMember(clip, "start"),
                ["duration"] = ReadMember(clip, "duration"),
                ["end"] = ReadMember(clip, "end"),
                ["assetType"] = ReadMember(clip, "asset")?.GetType().FullName,
            };
        }

        private static object CreateTimelineAsset(string assetPath, DependencyStatus status)
        {
            var existing = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (existing != null)
            {
                if (!status.Timeline.AssetType!.IsInstanceOfType(existing))
                {
                    throw new ArgumentException($"Asset '{assetPath}' exists but is not a TimelineAsset.");
                }

                return existing;
            }

            var asset = ScriptableObject.CreateInstance(status.Timeline.AssetType!);
            asset.name = System.IO.Path.GetFileNameWithoutExtension(assetPath);
            EnsureAssetFolder(assetPath);
            AssetDatabase.CreateAsset(asset, assetPath);
            AssetDatabase.SaveAssetIfDirty(asset);
            return asset;
        }

        private static object CreateCinemachineTrack(UnityEngine.Object timelineAsset, DependencyStatus status, string trackName)
        {
            if (status.Cinemachine.TrackType == null)
            {
                throw new InvalidOperationException("CinemachineTrack type is not loaded.");
            }

            var method = timelineAsset.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(item => item.Name == "CreateTrack" && item.GetParameters().Length >= 1 && item.GetParameters()[0].ParameterType == typeof(Type));
            if (method == null)
            {
                throw new InvalidOperationException("TimelineAsset.CreateTrack(Type, ...) method is unavailable.");
            }

            var parameters = method.GetParameters();
            object? track = parameters.Length switch
            {
                1 => method.Invoke(timelineAsset, new object?[] { status.Cinemachine.TrackType }),
                2 => method.Invoke(timelineAsset, new object?[] { status.Cinemachine.TrackType, null }),
                _ => method.Invoke(timelineAsset, new object?[] { status.Cinemachine.TrackType, null, trackName }),
            };
            if (track == null)
            {
                throw new InvalidOperationException("TimelineAsset.CreateTrack returned null.");
            }

            TrySetMember(track, "name", trackName, new List<string>());
            return track;
        }

        private static Component CreateShotCamera(ShotSpec spec, Transform? subject, DependencyStatus status, List<string> warnings)
        {
            var gameObject = new GameObject(spec.Name);
            Undo.RegisterCreatedObjectUndo(gameObject, "Create Cinemachine Shot Camera");
            if (subject != null)
            {
                gameObject.transform.position = subject.position + new Vector3(0f, 0f, -Mathf.Max(0.01f, spec.Distance));
                gameObject.transform.LookAt(subject);
            }

            var camera = Undo.AddComponent(gameObject, status.Cinemachine.CameraType!);
            TrySetMember(camera, "Priority", spec.Priority, warnings);
            if (subject != null)
            {
                TrySetCameraTarget(camera, subject, warnings);
            }

            ApplyLensPatch(camera, new JObject { ["fieldOfView"] = spec.FieldOfView }, warnings);
            MarkChanged(camera);
            return camera;
        }

        private static Component CreateSequencerChildCamera(Transform parent, SequencerShotSpec spec, Transform? subject, DependencyStatus status, List<string> warnings)
        {
            var gameObject = new GameObject(spec.Name);
            Undo.RegisterCreatedObjectUndo(gameObject, "Create Sequencer Child Camera");
            Undo.SetTransformParent(gameObject.transform, parent, "Parent Sequencer Child Camera");
            if (subject != null)
            {
                gameObject.transform.position = subject.position + new Vector3(0f, 0f, -Mathf.Max(0.01f, spec.Distance));
                gameObject.transform.LookAt(subject);
            }

            var camera = Undo.AddComponent(gameObject, status.Cinemachine.CameraType!);
            TrySetMember(camera, "Priority", spec.Priority, warnings);
            if (subject != null)
            {
                TrySetCameraTarget(camera, subject, warnings);
            }

            ApplyLensPatch(camera, new JObject { ["fieldOfView"] = spec.FieldOfView }, warnings);
            MarkChanged(camera);
            return camera;
        }

        private static void SetSequencerInstructions(Component sequencer, IReadOnlyList<Component> cameras, IReadOnlyList<SequencerShotSpec> specs, DependencyStatus status, List<string> warnings)
        {
            var instructionType = status.Sequencer.InstructionType!;
            var listType = typeof(List<>).MakeGenericType(instructionType);
            var instructions = (System.Collections.IList)Activator.CreateInstance(listType)!;
            for (var i = 0; i < cameras.Count; i++)
            {
                var instruction = Activator.CreateInstance(instructionType);
                if (instruction == null)
                {
                    warnings.Add("Could not create CinemachineSequencerCamera.Instruction.");
                    continue;
                }

                TrySetMember(instruction, "Camera", cameras[i], warnings);
                TrySetMember(instruction, "Hold", Math.Max(0f, (float)specs[i].Hold), warnings);
                TrySetMember(instruction, "Blend", CreateBlendDefinition(status, specs[i].BlendStyle, specs[i].BlendTime, warnings), warnings);
                instructions.Add(instruction);
            }

            Undo.RecordObject(sequencer, "Set Sequencer Instructions");
            if (!TrySetMember(sequencer, "Instructions", instructions, warnings))
            {
                warnings.Add("CinemachineSequencerCamera.Instructions field was not writable.");
            }
        }

        private static object? CreateBlendDefinition(DependencyStatus status, string style, float time, List<string> warnings)
        {
            var blend = Activator.CreateInstance(status.Sequencer.BlendDefinitionType!);
            if (blend == null)
            {
                warnings.Add("Could not create CinemachineBlendDefinition.");
                return null;
            }

            TrySetMember(blend, "Style", style, warnings);
            TrySetMember(blend, "Time", Math.Max(0f, time), warnings);
            return blend;
        }

        private static void ApplyBlenderSettings(UnityEngine.Object asset, BlenderBlendSpec[] specs, DependencyStatus status, List<string> warnings)
        {
            var customBlendType = BlenderCustomBlendType(status);
            if (customBlendType == null)
            {
                throw new InvalidOperationException("CinemachineBlenderSettings.CustomBlend type is not loaded.");
            }

            var array = Array.CreateInstance(customBlendType, specs.Length);
            for (var i = 0; i < specs.Length; i++)
            {
                var customBlend = Activator.CreateInstance(customBlendType);
                if (customBlend == null)
                {
                    warnings.Add("Could not create CinemachineBlenderSettings.CustomBlend.");
                    continue;
                }

                TrySetMember(customBlend, "From", specs[i].From, warnings);
                TrySetMember(customBlend, "To", specs[i].To, warnings);
                TrySetMember(customBlend, "Blend", CreateBlendDefinition(status, specs[i].Style, specs[i].Time, warnings), warnings);
                array.SetValue(customBlend, i);
            }

            if (!TrySetMember(asset, "CustomBlends", array, warnings)
                && !TrySetMember(asset, "m_CustomBlends", array, warnings))
            {
                warnings.Add("CinemachineBlenderSettings.CustomBlends field was not writable.");
            }
        }

        private static BlenderBlendSpec[] ReadBlenderBlendSpecs(JToken? token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return Array.Empty<BlenderBlendSpec>();
            }

            if (token is not JArray array)
            {
                throw new ArgumentException("blends must be an array.");
            }

            if (array.Count > MaxCustomBlendEntries)
            {
                throw new ArgumentException($"blends is capped at {MaxCustomBlendEntries} entries.");
            }

            return array
                .OfType<JObject>()
                .Select(item =>
                {
                    var from = NormalizeBlendCameraName(ReadString(item, "from") ?? "ANY CAMERA");
                    var to = NormalizeBlendCameraName(ReadString(item, "to") ?? "ANY CAMERA");
                    var style = ReadString(item, "style") ?? ReadString(item, "blendStyle") ?? "EaseInOut";
                    var time = OptionalFloat(item, "time") ?? OptionalFloat(item, "blendTime") ?? 0.5f;
                    return new BlenderBlendSpec(from, to, style, Math.Max(0f, time));
                })
                .ToArray();
        }

        private static BlenderBlendSpec[] ReadCustomBlendSpecs(object? blends)
        {
            return EnumerateObjects(blends)
                .Select(blend =>
                {
                    var from = NormalizeBlendCameraName(Convert.ToString(ReadMember(blend, "From"), CultureInfo.InvariantCulture) ?? "ANY CAMERA");
                    var to = NormalizeBlendCameraName(Convert.ToString(ReadMember(blend, "To"), CultureInfo.InvariantCulture) ?? "ANY CAMERA");
                    var blendDefinition = ReadMember(blend, "Blend");
                    var style = blendDefinition == null ? "missing" : Convert.ToString(ReadMember(blendDefinition, "Style"), CultureInfo.InvariantCulture) ?? "missing";
                    var time = blendDefinition == null ? 0f : ReadFloatMember(blendDefinition, "Time") ?? ReadFloatMember(blendDefinition, "BlendTime") ?? 0f;
                    return new BlenderBlendSpec(from, to, style, Math.Max(0f, time));
                })
                .ToArray();
        }

        private static string NormalizeBlendCameraName(string value)
        {
            return string.Equals(value.Trim(), "any camera", StringComparison.OrdinalIgnoreCase)
                ? "ANY CAMERA"
                : value.Trim();
        }

        private static void AddBlendValidationWarnings(BlenderBlendSpec[] specs, HashSet<string> cameraNames, List<string> warnings)
        {
            for (var i = 0; i < specs.Length; i++)
            {
                AddCameraNameWarning(specs[i].From, i, "from", cameraNames, warnings);
                AddCameraNameWarning(specs[i].To, i, "to", cameraNames, warnings);
            }

            for (var i = 0; i < specs.Length; i++)
            {
                for (var j = i + 1; j < specs.Length; j++)
                {
                    if (!BlendsOverlap(specs[i], specs[j]) || BlendSpecificity(specs[i]) != BlendSpecificity(specs[j]))
                    {
                        continue;
                    }

                    if (string.Equals(specs[i].From, specs[j].From, StringComparison.Ordinal)
                        && string.Equals(specs[i].To, specs[j].To, StringComparison.Ordinal))
                    {
                        warnings.Add($"Custom blend entries {i} and {j} duplicate '{specs[i].From}' -> '{specs[i].To}'; earlier entry wins.");
                    }
                    else
                    {
                        warnings.Add($"Custom blend entries {i} and {j} can match the same camera switch with equal specificity; ordering decides which blend wins.");
                    }
                }
            }
        }

        private static void AddCameraNameWarning(string cameraName, int index, string fieldName, HashSet<string> cameraNames, List<string> warnings)
        {
            if (string.Equals(cameraName, "ANY CAMERA", StringComparison.Ordinal) || cameraNames.Contains(cameraName))
            {
                return;
            }

            warnings.Add($"Custom blend entry {index} {fieldName} camera '{cameraName}' does not match any loaded CinemachineCamera name; this is allowed but may never match.");
        }

        private static bool BlendsOverlap(BlenderBlendSpec left, BlenderBlendSpec right)
        {
            return BlendEndpointOverlaps(left.From, right.From) && BlendEndpointOverlaps(left.To, right.To);
        }

        private static bool BlendEndpointOverlaps(string left, string right)
        {
            return string.Equals(left, "ANY CAMERA", StringComparison.Ordinal)
                || string.Equals(right, "ANY CAMERA", StringComparison.Ordinal)
                || string.Equals(left, right, StringComparison.Ordinal);
        }

        private static int BlendSpecificity(BlenderBlendSpec spec)
        {
            var specificity = 0;
            if (!string.Equals(spec.From, "ANY CAMERA", StringComparison.Ordinal))
            {
                specificity++;
            }

            if (!string.Equals(spec.To, "ANY CAMERA", StringComparison.Ordinal))
            {
                specificity++;
            }

            return specificity;
        }

        private static bool UsesAnyCamera(BlenderBlendSpec spec)
        {
            return string.Equals(spec.From, "ANY CAMERA", StringComparison.Ordinal)
                || string.Equals(spec.To, "ANY CAMERA", StringComparison.Ordinal);
        }

        private static Dictionary<string, object?> DescribeBlendSpec(BlenderBlendSpec spec)
        {
            return new Dictionary<string, object?>
            {
                ["from"] = spec.From,
                ["to"] = spec.To,
                ["blend"] = new Dictionary<string, object?>
                {
                    ["style"] = spec.Style,
                    ["time"] = Round(spec.Time),
                    ["blendTime"] = Round(spec.Time),
                    ["summary"] = $"{spec.Style} {Round(spec.Time)}s",
                },
            };
        }

        private static object? CreateCinemachineShotClip(object track, Component camera, ShotSpec spec, List<string> warnings)
        {
            var createClip = track.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(method => method.Name == "CreateClip" && method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType == typeof(Type));
            if (createClip == null)
            {
                warnings.Add("CinemachineTrack.CreateClip(Type) unavailable; shot camera was created without Timeline clip.");
                return null;
            }

            var shotType = FindLoadedType("Unity.Cinemachine.CinemachineShot");
            if (shotType == null)
            {
                warnings.Add("CinemachineShot type unavailable; shot camera was created without Timeline clip.");
                return null;
            }

            var clip = createClip.Invoke(track, new object?[] { shotType });
            if (clip == null)
            {
                warnings.Add("CinemachineTrack.CreateClip returned null.");
                return null;
            }

            TrySetMember(clip, "displayName", spec.Name, warnings);
            TrySetMember(clip, "start", spec.Start, warnings);
            TrySetMember(clip, "duration", spec.Duration, warnings);
            var clipAsset = ReadMember(clip, "asset");
            if (clipAsset != null)
            {
                SetCinemachineShotCamera(clipAsset, camera, warnings);
            }

            return clip;
        }

        private static void SetCinemachineShotCamera(object shotAsset, Component camera, List<string> warnings)
        {
            var property = shotAsset.GetType().GetProperty("VirtualCamera", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property == null || !property.CanWrite)
            {
                warnings.Add("CinemachineShot.VirtualCamera property unavailable.");
                return;
            }

            var value = property.GetValue(shotAsset);
            if (value != null && value.GetType().IsValueType)
            {
                var field = value.GetType().GetField("defaultValue", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null)
                {
                    field.SetValue(value, camera);
                    property.SetValue(shotAsset, value);
                    return;
                }
            }

            if (property.PropertyType.IsInstanceOfType(camera))
            {
                property.SetValue(shotAsset, camera);
                return;
            }

            warnings.Add("Could not assign CinemachineShot.VirtualCamera; assign camera manually if clip is empty.");
        }

        private static void ApplyLensPatch(Component camera, JObject lensPatch, List<string> warnings)
        {
            var lens = ReadMember(camera, "Lens");
            if (lens == null)
            {
                warnings.Add("CinemachineCamera Lens property unavailable.");
                return;
            }

            var lensType = lens.GetType();
            var mutableLens = lens;
            foreach (var property in lensPatch.Properties())
            {
                var memberName = property.Name switch
                {
                    "fieldOfView" => "FieldOfView",
                    "orthographicSize" => "OrthographicSize",
                    "nearClipPlane" => "NearClipPlane",
                    "farClipPlane" => "FarClipPlane",
                    "modeOverride" => "ModeOverride",
                    _ => property.Name,
                };
                if (!TrySetMember(mutableLens, memberName, ConvertJToken(property.Value), warnings))
                {
                    warnings.Add($"Lens field '{property.Name}' was not applied.");
                }
            }

            TrySetMember(camera, "Lens", mutableLens, warnings);
        }

        private static void TrySetCameraTarget(Component camera, Transform target, List<string> warnings)
        {
            if (TrySetMember(camera, "Target", target, warnings)
                || TrySetMember(camera, "TrackingTarget", target, warnings)
                || TrySetMember(camera, "Follow", target, warnings))
            {
                return;
            }

            TrySetMember(camera, "LookAt", target, warnings);
        }

        private static Camera? ResolveCamera(JToken args, bool allowCreate, bool dryRun)
        {
            if (TryInt(args, "cameraInstanceId", out var cameraInstanceId) || TryInt(args, "instanceId", out cameraInstanceId))
            {
                var obj = UnityObjectIdentity.LegacyInstanceIdToObject(cameraInstanceId);
                if (obj is Camera camera)
                {
                    return camera;
                }

                if (obj is GameObject targetGameObject)
                {
                    return targetGameObject.GetComponent<Camera>();
                }
            }

            if (TryString(args, "cameraPath", out var cameraPath) || TryString(args, "targetPath", out cameraPath))
            {
                var transform = ResolveTransform(cameraPath);
                var camera = transform != null ? transform.GetComponent<Camera>() : null;
                if (camera != null)
                {
                    return camera;
                }
            }

            var mainCamera = Camera.main;
            if (mainCamera != null)
            {
                return mainCamera;
            }

            if (!allowCreate || dryRun)
            {
                return null;
            }

            var gameObject = new GameObject(ReadString(args, "cameraName") ?? "Main Camera");
            Undo.RegisterCreatedObjectUndo(gameObject, "Create Unity Camera");
            gameObject.tag = "MainCamera";
            return Undo.AddComponent<Camera>(gameObject);
        }

        private static Component? ResolveSelectedBrain(Type brainType)
        {
            foreach (var selected in Selection.objects)
            {
                if (selected is Component component && brainType.IsInstanceOfType(component))
                {
                    return component;
                }

                if (selected is GameObject gameObject)
                {
                    var brain = gameObject.GetComponent(brainType);
                    if (brain != null)
                    {
                        return brain;
                    }
                }
            }

            return null;
        }

        private static HashSet<string> CurrentCinemachineCameraNames(DependencyStatus status)
        {
            return status.Cinemachine.CameraType == null
                ? new HashSet<string>(StringComparer.Ordinal)
                : EnumerateComponents(status.Cinemachine.CameraType)
                    .Select(component => component.name)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .ToHashSet(StringComparer.Ordinal);
        }

        private static PlayableDirector? ResolveDirectorFromArgs(JToken args)
        {
            if (TryInt(args, "directorInstanceId", out var instanceId) || TryInt(args, "instanceId", out instanceId))
            {
                return UnityObjectIdentity.LegacyInstanceIdToObject(instanceId) as PlayableDirector;
            }

            if (TryString(args, "directorPath", out var path) || TryString(args, "targetPath", out path))
            {
                return ResolveDirector(path);
            }

            return null;
        }

        private static PlayableDirector RequireDirector(JToken args)
        {
            return ResolveDirectorFromArgs(args) ?? throw new ArgumentException("Expected directorPath or directorInstanceId for a PlayableDirector.");
        }

        private static PlayableDirector? ResolveDirector(string pathOrInstanceId)
        {
            if (int.TryParse(pathOrInstanceId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var instanceId))
            {
                return UnityObjectIdentity.LegacyInstanceIdToObject(instanceId) as PlayableDirector;
            }

            var normalizedPath = pathOrInstanceId.Trim('/');
            return EnumerateDirectors().FirstOrDefault(director => string.Equals(GetTransformPath(director.transform), normalizedPath, StringComparison.Ordinal));
        }

        private static Component RequireComponent(JToken args, Type componentType, string displayName)
        {
            Component? component = null;
            if (TryInt(args, "instanceId", out var instanceId))
            {
                component = UnityObjectIdentity.LegacyInstanceIdToObject(instanceId) as Component;
            }

            if (component == null && TryString(args, "targetPath", out var targetPath))
            {
                component = ResolveComponent(componentType, targetPath);
            }

            if (component == null || !componentType.IsInstanceOfType(component))
            {
                throw new ArgumentException($"Expected targetPath or instanceId for an existing {displayName}.");
            }

            return component;
        }

        private static Component RequireNamedComponent(JToken args, Type componentType, string displayName, string pathFieldName, string instanceIdFieldName)
        {
            Component? component = null;
            if (TryInt(args, instanceIdFieldName, out var instanceId))
            {
                var obj = UnityObjectIdentity.LegacyInstanceIdToObject(instanceId);
                component = obj switch
                {
                    Component resolved when componentType.IsInstanceOfType(resolved) => resolved,
                    GameObject gameObject => gameObject.GetComponent(componentType),
                    _ => null,
                };
            }

            if (component == null && TryString(args, pathFieldName, out var path))
            {
                component = ResolveComponent(componentType, path);
            }

            if (component == null || !componentType.IsInstanceOfType(component))
            {
                throw new ArgumentException($"Expected {pathFieldName} or {instanceIdFieldName} for an existing {displayName}. Existing spline geometry is not created or edited.");
            }

            return component;
        }

        private static Component RequireCollider(JToken args, Type colliderType, string displayName)
        {
            Component? collider = null;
            if (TryInt(args, "colliderInstanceId", out var instanceId))
            {
                var obj = UnityObjectIdentity.LegacyInstanceIdToObject(instanceId);
                collider = obj switch
                {
                    Component component when colliderType.IsInstanceOfType(component) => component,
                    GameObject gameObject => gameObject.GetComponent(colliderType),
                    _ => null,
                };
            }

            if (collider == null && TryString(args, "colliderPath", out var colliderPath))
            {
                var transform = ResolveTransform(colliderPath);
                collider = transform == null ? null : transform.GetComponent(colliderType);
            }

            if (collider == null || !colliderType.IsInstanceOfType(collider))
            {
                throw new ArgumentException($"Expected colliderPath or colliderInstanceId for an existing {displayName}. Collider geometry is not created or edited.");
            }

            return collider;
        }

        private static Component? ResolveComponent(Type componentType, string pathOrInstanceId)
        {
            if (int.TryParse(pathOrInstanceId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var instanceId))
            {
                var obj = UnityObjectIdentity.LegacyInstanceIdToObject(instanceId);
                return obj is Component component && componentType.IsInstanceOfType(component) ? component : null;
            }

            var normalizedPath = pathOrInstanceId.Trim('/');
            return EnumerateComponents(componentType)
                .FirstOrDefault(component => string.Equals(GetTransformPath(component.transform), normalizedPath, StringComparison.Ordinal));
        }

        private static Transform? ResolveTransform(string pathOrInstanceId)
        {
            if (int.TryParse(pathOrInstanceId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var instanceId))
            {
                var obj = UnityObjectIdentity.LegacyInstanceIdToObject(instanceId);
                return obj switch
                {
                    Transform transform => transform,
                    GameObject gameObject => gameObject.transform,
                    Component component => component.transform,
                    _ => null,
                };
            }

            var normalizedPath = pathOrInstanceId.Trim('/');
            foreach (var root in GetCurrentScene().GetRootGameObjects())
            {
                if (string.Equals(root.name, normalizedPath, StringComparison.Ordinal))
                {
                    return root.transform;
                }

                if (normalizedPath.StartsWith(root.name + "/", StringComparison.Ordinal))
                {
                    var child = root.transform.Find(normalizedPath.Substring(root.name.Length + 1));
                    if (child != null)
                    {
                        return child;
                    }
                }
            }

            return null;
        }

        private static PlayableDirector CreateDirectorObject(string name)
        {
            var gameObject = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(gameObject, "Create Timeline Director");
            return Undo.AddComponent<PlayableDirector>(gameObject);
        }

        private static ShotSpec[] ReadShotSpecs(JToken args)
        {
            if (args["shots"] is not JArray shots)
            {
                return Array.Empty<ShotSpec>();
            }

            return shots.OfType<JObject>()
                .Select((shot, index) => new ShotSpec(
                    ReadString(shot, "name") ?? $"Shot {index + 1}",
                    OptionalDouble(shot, "start") ?? index,
                    OptionalDouble(shot, "duration") ?? 1d,
                    OptionalFloat(shot, "fieldOfView") ?? OptionalFloat(shot, "fov") ?? 35f,
                    OptionalFloat(shot, "distance") ?? 4f,
                    OptionalInt(shot, "priority") ?? (10 + index)))
                .ToArray();
        }

        private static SequencerShotSpec[] ReadSequencerShotSpecs(JToken args)
        {
            if (args["shots"] is not JArray shots)
            {
                return Array.Empty<SequencerShotSpec>();
            }

            return shots.OfType<JObject>()
                .Select((shot, index) => new SequencerShotSpec(
                    ReadString(shot, "name") ?? $"Sequencer Shot {index + 1}",
                    OptionalDouble(shot, "holdSeconds") ?? OptionalDouble(shot, "hold") ?? 1d,
                    OptionalFloat(shot, "fieldOfView") ?? OptionalFloat(shot, "fov") ?? 35f,
                    OptionalFloat(shot, "distance") ?? 4f,
                    OptionalInt(shot, "priority") ?? (10 + index),
                    ReadString(shot, "blendStyle") ?? "Cut",
                    OptionalFloat(shot, "blendTime") ?? 0f))
                .ToArray();
        }

        private static Dictionary<string, object?> DescribeLens(object? lens)
        {
            if (lens == null)
            {
                return new Dictionary<string, object?> { ["missing"] = true };
            }

            return new Dictionary<string, object?>
            {
                ["fieldOfView"] = Round(ReadFloatMember(lens, "FieldOfView")),
                ["orthographicSize"] = Round(ReadFloatMember(lens, "OrthographicSize")),
                ["nearClipPlane"] = Round(ReadFloatMember(lens, "NearClipPlane")),
                ["farClipPlane"] = Round(ReadFloatMember(lens, "FarClipPlane")),
                ["modeOverride"] = Convert.ToString(ReadMember(lens, "ModeOverride"), CultureInfo.InvariantCulture),
            };
        }

        private static Dictionary<string, object?> DescribeTransform(Transform transform)
        {
            return new Dictionary<string, object?>
            {
                ["position"] = Vector3Row(transform.position),
                ["rotationEuler"] = Vector3Row(transform.rotation.eulerAngles),
                ["localScale"] = Vector3Row(transform.localScale),
            };
        }

        private static Dictionary<string, object?> DescribeObjectReference(object? value)
        {
            if (value is Transform transform)
            {
                return new Dictionary<string, object?>
                {
                    ["name"] = transform.name,
                    ["path"] = GetTransformPath(transform),
                    ["instanceId"] = UnityObjectIdentity.GetLegacyInstanceId(transform),
                };
            }

            if (value is GameObject gameObject)
            {
                return new Dictionary<string, object?>
                {
                    ["name"] = gameObject.name,
                    ["path"] = GetTransformPath(gameObject.transform),
                    ["instanceId"] = UnityObjectIdentity.GetLegacyInstanceId(gameObject),
                };
            }

            if (value is Component component)
            {
                return new Dictionary<string, object?>
                {
                    ["name"] = component.name,
                    ["path"] = GetTransformPath(component.transform),
                    ["instanceId"] = UnityObjectIdentity.GetLegacyInstanceId(component),
                    ["type"] = component.GetType().FullName,
                };
            }

            return new Dictionary<string, object?> { ["value"] = Convert.ToString(value, CultureInfo.InvariantCulture) };
        }

        private static object? DescribeObjectReferenceOrNull(object? value)
        {
            return value == null ? null : DescribeObjectReference(value);
        }

        private static Dictionary<string, object?> DescribeAssetObject(UnityEngine.Object asset)
        {
            var path = AssetDatabase.GetAssetPath(asset);
            return new Dictionary<string, object?>
            {
                ["name"] = asset.name,
                ["path"] = path,
                ["guid"] = string.IsNullOrWhiteSpace(path) ? string.Empty : AssetDatabase.AssetPathToGUID(path),
                ["type"] = asset.GetType().FullName,
            };
        }

        private static object? DescribeMaybeAsset(object? value)
        {
            return value is UnityEngine.Object asset ? DescribeAssetObject(asset) : null;
        }

        private static Component? GetOptionalComponent(GameObject? gameObject, Type? componentType)
        {
            return gameObject == null || componentType == null ? null : gameObject.GetComponent(componentType);
        }

        private static object? ReadFirstMember(object target, string[] memberNames)
        {
            foreach (var memberName in memberNames)
            {
                var value = ReadMember(target, memberName);
                if (value != null)
                {
                    return value;
                }
            }

            return null;
        }

        private static bool TrySetFirstMember(object target, string[] memberNames, object? value, List<string> warnings)
        {
            foreach (var memberName in memberNames)
            {
                if (TrySetMember(target, memberName, value, warnings))
                {
                    return true;
                }
            }

            return false;
        }

        private static void SetAutoDollyEnabled(Component dolly, bool enabled, List<string> warnings)
        {
            var autoDolly = ReadMember(dolly, "AutoDolly");
            if (autoDolly == null)
            {
                warnings.Add("AutoDolly field was not found.");
                return;
            }

            if (!TrySetMember(autoDolly, "Enabled", enabled, warnings))
            {
                warnings.Add("AutoDolly.Enabled field was not writable.");
                return;
            }

            if (!TrySetMember(dolly, "AutoDolly", autoDolly, warnings))
            {
                warnings.Add("AutoDolly field was not writable after changing Enabled.");
            }
        }

        private static string DescribeAutoDollyMode(object? autoDolly)
        {
            if (autoDolly == null)
            {
                return "missing";
            }

            var enabled = ReadMember(autoDolly, "Enabled");
            return enabled is bool isEnabled ? (isEnabled ? "enabled" : "disabled") : Convert.ToString(autoDolly, CultureInfo.InvariantCulture) ?? "unknown";
        }

        private static IEnumerable<string> SplineDollyWarnings(Component dolly, Component? splineContainer, DependencyStatus status)
        {
            if (splineContainer == null)
            {
                yield return "CinemachineSplineDolly has no SplineContainer assigned.";
            }

            var camera = status.Cinemachine.CameraType == null ? null : dolly.GetComponent(status.Cinemachine.CameraType);
            var target = camera == null ? null : ReadMember(camera, "Target") ?? ReadMember(camera, "TrackingTarget") ?? ReadMember(camera, "Follow") ?? ReadMember(camera, "LookAt");
            if (target == null)
            {
                yield return "CinemachineCamera has no target/follow/look-at assigned; framing changes may be hard to validate.";
            }
        }

        private static void InvokeCacheInvalidation(Component component, string[] methodNames, List<string> warnings)
        {
            foreach (var methodName in methodNames)
            {
                var method = component.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (method == null)
                {
                    continue;
                }

                try
                {
                    method.Invoke(component, null);
                }
                catch (Exception ex)
                {
                    warnings.Add($"Cache invalidation method {methodName} failed: {ex.Message}");
                }

                return;
            }

            warnings.Add("Requested cache invalidation, but no matching cache invalidation method was found on this confiner type.");
        }

        private static IEnumerable<object> InvokeEnumerable(object target, string methodName)
        {
            var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            var result = method?.Invoke(target, null) as System.Collections.IEnumerable;
            if (result == null)
            {
                yield break;
            }

            foreach (var item in result)
            {
                if (item != null)
                {
                    yield return item;
                }
            }
        }

        private static bool TrySetMember(object target, string memberName, object? value, List<string> warnings)
        {
            var type = target.GetType();
            var property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.CanWrite)
            {
                try
                {
                    property.SetValue(target, ConvertValue(value, property.PropertyType));
                    return true;
                }
                catch (Exception ex)
                {
                    warnings.Add($"Could not set {memberName}: {ex.Message}");
                    return false;
                }
            }

            var field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                try
                {
                    field.SetValue(target, ConvertValue(value, field.FieldType));
                    return true;
                }
                catch (Exception ex)
                {
                    warnings.Add($"Could not set {memberName}: {ex.Message}");
                    return false;
                }
            }

            return false;
        }

        private static object? ReadMember(object target, string memberName)
        {
            var type = target.GetType();
            var property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.CanRead)
            {
                return property.GetValue(target);
            }

            var field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field?.GetValue(target);
        }

        private static void AddMemberIfPresent(Dictionary<string, object?> result, object target, string memberName, string outputName)
        {
            var value = ReadMember(target, memberName);
            if (value != null)
            {
                result[outputName] = DescribeMemberValue(value);
            }
        }

        private static object? DescribeMemberValue(object? value)
        {
            if (value == null)
            {
                return null;
            }

            if (value is string || value.GetType().IsPrimitive || value is decimal)
            {
                return value;
            }

            if (value.GetType().IsEnum)
            {
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            }

            if (value is UnityEngine.Object unityObject)
            {
                return unityObject is Component || unityObject is GameObject || unityObject is Transform
                    ? DescribeObjectReference(unityObject)
                    : DescribeAssetObject(unityObject);
            }

            if (value is System.Collections.IEnumerable enumerable)
            {
                return new Dictionary<string, object?>
                {
                    ["type"] = value.GetType().FullName,
                    ["count"] = enumerable.Cast<object?>().Count(item => item != null),
                };
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static IEnumerable<object> EnumerateObjects(object? value)
        {
            if (value is not System.Collections.IEnumerable enumerable || value is string)
            {
                yield break;
            }

            foreach (var item in enumerable)
            {
                if (item != null)
                {
                    yield return item;
                }
            }
        }

        private static IEnumerable<(string Name, object Value)> EnumerateMemberValues(object target)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var property in target.GetType().GetProperties(flags))
            {
                if (!property.CanRead || property.GetIndexParameters().Length != 0)
                {
                    continue;
                }

                object? value;
                try
                {
                    value = property.GetValue(target);
                }
                catch
                {
                    continue;
                }

                foreach (var item in ExpandMemberValue(property.Name, value))
                {
                    yield return item;
                }
            }

            foreach (var field in target.GetType().GetFields(flags))
            {
                object? value;
                try
                {
                    value = field.GetValue(target);
                }
                catch
                {
                    continue;
                }

                foreach (var item in ExpandMemberValue(field.Name, value))
                {
                    yield return item;
                }
            }
        }

        private static IEnumerable<(string Name, object Value)> ExpandMemberValue(string name, object? value)
        {
            if (value == null)
            {
                yield break;
            }

            yield return (name, value);
            foreach (var item in EnumerateObjects(value))
            {
                yield return (name, item);
            }
        }

        private static float? ReadFloatMember(object target, string memberName)
        {
            var value = ReadMember(target, memberName);
            return value == null ? null : Convert.ToSingle(value, CultureInfo.InvariantCulture);
        }

        private static object? ConvertValue(object? value, Type targetType)
        {
            if (value == null)
            {
                return null;
            }

            var nonNullableType = Nullable.GetUnderlyingType(targetType) ?? targetType;
            if (nonNullableType.IsInstanceOfType(value))
            {
                return value;
            }

            if (nonNullableType.IsEnum)
            {
                return Enum.Parse(nonNullableType, Convert.ToString(value, CultureInfo.InvariantCulture)!, ignoreCase: true);
            }

            if (nonNullableType == typeof(float))
            {
                return Convert.ToSingle(value, CultureInfo.InvariantCulture);
            }

            if (nonNullableType == typeof(double))
            {
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }

            if (nonNullableType == typeof(int))
            {
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }

            if (nonNullableType == typeof(bool))
            {
                return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
            }

            return value;
        }

        private static object? ConvertJToken(JToken token)
        {
            return token.Type switch
            {
                JTokenType.Integer => token.Value<int>(),
                JTokenType.Float => token.Value<float>(),
                JTokenType.Boolean => token.Value<bool>(),
                JTokenType.String => token.Value<string>(),
                _ => token.ToString(),
            };
        }

        private static Scene GetCurrentScene()
        {
            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            return prefabStage != null ? prefabStage.scene : SceneManager.GetActiveScene();
        }

        private static Dictionary<string, object?> DescribeCurrentStage()
        {
            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            var scene = GetCurrentScene();
            return new Dictionary<string, object?>
            {
                ["kind"] = prefabStage != null ? "prefab-stage" : "scene",
                ["scenePath"] = scene.path,
                ["sceneName"] = scene.name,
                ["prefabAssetPath"] = prefabStage?.assetPath,
            };
        }

        private static string GetTransformPath(Transform transform)
        {
            var parts = new Stack<string>();
            var current = transform;
            while (current != null)
            {
                parts.Push(current.name);
                current = current.parent;
            }

            return string.Join("/", parts);
        }

        private static void MarkChanged(UnityEngine.Object obj)
        {
            EditorUtility.SetDirty(obj);
            if (obj is Component component)
            {
                EditorUtility.SetDirty(component.gameObject);
                if (!EditorApplication.isPlayingOrWillChangePlaymode && component.gameObject.scene.IsValid())
                {
                    EditorSceneManager.MarkSceneDirty(component.gameObject.scene);
                }
            }
        }

        private static string CinemachineDetailUri(Component camera)
        {
            return CinemachineCameraDetailPrefix + EncodeSegment(UnityObjectIdentity.GetEntityIdText(camera));
        }

        private static string BrainDetailUri(Component brain)
        {
            return CinemachineBrainDetailPrefix + EncodeSegment(UnityObjectIdentity.GetEntityIdText(brain));
        }

        private static string SequencerDetailUri(Component sequencer)
        {
            return CinemachineSequencerDetailPrefix + EncodeSegment(UnityObjectIdentity.GetEntityIdText(sequencer));
        }

        private static string DirectorDetailUri(PlayableDirector director)
        {
            return TimelineDirectorDetailPrefix + EncodeSegment(UnityObjectIdentity.GetEntityIdText(director));
        }

        private static string TimelineAssetDetailUri(string path)
        {
            var guid = AssetDatabase.AssetPathToGUID(path);
            return TimelineAssetDetailPrefix + EncodeSegment(string.IsNullOrWhiteSpace(guid) ? path : guid);
        }

        private static Dictionary<string, object?> CreateCommandEnvelope(string uri, DependencyStatus status, bool dryRun)
        {
            var result = CreateEnvelope(uri, status);
            result["ok"] = true;
            result["dryRun"] = dryRun;
            return result;
        }

        private static Dictionary<string, object?> CreateEnvelope(string uri, DependencyStatus status)
        {
            return new Dictionary<string, object?>();
        }

        private static Dictionary<string, object?> CreateUnavailable(string statusUri, DependencyStatus status, string message)
        {
            return new Dictionary<string, object?>
            {
                ["ok"] = false,
                ["unavailable"] = true,
                ["message"] = message,
                ["statusUri"] = statusUri,
                ["reason"] = status.Reason,
            };
        }

        private static Dictionary<string, object?> CreateCinemachineUnavailable(DependencyStatus status, string subject)
        {
            return CreateUnavailable(
                StatusUri,
                status,
                $"{subject} requires Cinemachine 3.x Unity.Cinemachine.CinemachineCamera authoring API. {status.Cinemachine.Reason}");
        }

        private static Dictionary<string, object?> CreateSequencerUnavailable(DependencyStatus status, string subject)
        {
            return CreateUnavailable(
                StatusUri,
                status,
                $"{subject} requires CM3 Sequencer Camera: {CinemachinePackageName} installed, CHIEVFX_MCP_HAS_CINEMACHINE active, apiFamily=cm3, and loaded Unity.Cinemachine.CinemachineSequencerCamera, Instruction, CinemachineCamera, CinemachineVirtualCameraBase, CinemachineBlendDefinition, and CinemachineBrain. {status.Sequencer.Reason}");
        }

        private static Dictionary<string, object?> CreateAdvancedHelperUnavailable(DependencyStatus status, AdvancedHelperStatus helper, string subject)
        {
            var optional = helper.OptionalGateRequired
                ? $", plus {helper.OptionalPackageName} installed, CHIEVFX_MCP_HAS_SPLINES active, and loaded {helper.OptionalTypeName}"
                : string.Empty;
            var secondary = helper.SecondaryHelperTypeName == null ? string.Empty : $", and loaded {helper.SecondaryHelperTypeName}";
            return CreateUnavailable(
                StatusUri,
                status,
                $"{subject} requires CM3 {helper.DisplayName}: {CinemachinePackageName} installed, CHIEVFX_MCP_HAS_CINEMACHINE active, apiFamily=cm3, loaded {helper.HelperTypeName}{secondary}{optional}. {helper.Reason}");
        }

        private static bool SplinesDollyAvailable(DependencyStatus status)
        {
            return status.Cinemachine.Available && status.SplinesDolly.Available;
        }

        private static bool ConfinerAvailable(DependencyStatus status, AdvancedHelperStatus helper)
        {
            return status.Cinemachine.Available && helper.Available;
        }

        private static Dictionary<string, object?> CreateConfinerUnavailable(DependencyStatus status, AdvancedHelperStatus helper, string subject)
        {
            var colliderType = helper.SecondaryHelperTypeName ?? "UnityEngine.Collider/Collider2D";
            var reason = !status.Cinemachine.Available ? status.Cinemachine.Reason : helper.Reason;
            return CreateUnavailable(
                StatusUri,
                status,
                $"{subject} requires CM3 {helper.DisplayName}: {CinemachinePackageName} installed, CHIEVFX_MCP_HAS_CINEMACHINE active, apiFamily=cm3, loaded Unity.Cinemachine.CinemachineCamera, Unity.Cinemachine.CinemachineBrain, {helper.HelperTypeName}, and {colliderType}. Deprecated Unity.Cinemachine.CinemachineConfiner is warning-only and will not be authored. {reason}");
        }

        private static Dictionary<string, object?> CreateBlenderSettingsUnavailable(DependencyStatus status, string subject)
        {
            var result = CreateUnavailable(
                StatusUri,
                status,
                $"{subject} requires CM3 Blender Settings: {CinemachinePackageName} installed, CHIEVFX_MCP_HAS_CINEMACHINE active, apiFamily=cm3, and loaded Unity.Cinemachine.CinemachineBlenderSettings, CustomBlend, CinemachineBlendDefinition, CinemachineBrain, and CinemachineCamera. {BlenderSettingsUnavailableReason(status)}");
            result["requiredTypes"] = BlenderSettingsRequiredTypes(status);
            return result;
        }

        private static bool BlenderSettingsAvailable(DependencyStatus status)
        {
            return status.Cinemachine.Available
                && status.BlenderSettings.Available
                && BlenderCustomBlendType(status) != null
                && status.Sequencer.BlendDefinitionType != null;
        }

        private static Type? BlenderCustomBlendType(DependencyStatus status)
        {
            return status.BlenderSettings.HelperType?.GetNestedType("CustomBlend", BindingFlags.Public | BindingFlags.NonPublic);
        }

        private static string BlenderSettingsUnavailableReason(DependencyStatus status)
        {
            if (!status.Cinemachine.Available)
            {
                return status.Cinemachine.Reason;
            }

            if (!status.BlenderSettings.Available)
            {
                return status.BlenderSettings.Reason;
            }

            if (BlenderCustomBlendType(status) == null)
            {
                return "CM3 package detected, but CinemachineBlenderSettings.CustomBlend type is not loaded.";
            }

            if (status.Sequencer.BlendDefinitionType == null)
            {
                return "CM3 package detected, but CinemachineBlendDefinition type is not loaded.";
            }

            return "available";
        }

        private static Dictionary<string, object?> BlenderSettingsRequiredTypes(DependencyStatus status)
        {
            return new Dictionary<string, object?>
            {
                ["cinemachineBlenderSettingsTypeLoaded"] = status.BlenderSettings.HelperType != null,
                ["customBlendTypeLoaded"] = BlenderCustomBlendType(status) != null,
                ["blendDefinitionTypeLoaded"] = status.Sequencer.BlendDefinitionType != null,
                ["brainTypeLoaded"] = status.Cinemachine.BrainType != null,
                ["cinemachineCameraTypeLoaded"] = status.Cinemachine.CameraType != null,
            };
        }

        private static Dictionary<string, object?> CreateShotSequenceUnavailable(DependencyStatus status, string subject)
        {
            if (!status.Cinemachine.Available)
            {
                return CreateCinemachineUnavailable(status, subject);
            }

            return CreateUnavailable(StatusUri, status, $"{subject} requires {TimelinePackageName}, loaded UnityEngine.Timeline types, and active CHIEVFX_MCP_HAS_TIMELINE. {status.Timeline.Reason}");
        }

        private static Dictionary<string, object?> CreateNotFound(string uri, DependencyStatus status, string kind, string key)
        {
            var result = CreateEnvelope(uri, status);
            result["ok"] = false;
            result["error"] = "not-found";
            result["kind"] = kind;
            result["key"] = key;
            return result;
        }

        private static string EncodeSegment(string value)
        {
            return Uri.EscapeDataString(value);
        }

        private static string DecodeSegment(string value)
        {
            return Uri.UnescapeDataString(value);
        }

        private static string? ResolveAssetPath(string guidOrPath)
        {
            var guidPath = AssetDatabase.GUIDToAssetPath(guidOrPath);
            return string.IsNullOrEmpty(guidPath) ? guidOrPath : guidPath;
        }

        private static string EnsureTimelineAssetPath(string path)
        {
            var normalized = string.IsNullOrWhiteSpace(path) ? "Assets/Cinematics/Timeline.playable" : path;
            if (!normalized.EndsWith(".playable", StringComparison.OrdinalIgnoreCase))
            {
                normalized += ".playable";
            }

            return normalized;
        }

#pragma warning disable CS8602 // Roslyn does not narrow folder after the Unity AssetDatabase guard below.
        private static void EnsureAssetFolder(string assetPath)
        {
            var folder = (System.IO.Path.GetDirectoryName(assetPath) ?? string.Empty).Replace("\\", "/");
            if (string.IsNullOrWhiteSpace(folder) || AssetDatabase.IsValidFolder(folder))
            {
                return;
            }

            var parts = folder.Split('/');
            var current = parts[0];
            for (var i = 1; i < parts.Length; i++)
            {
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, parts[i]);
                }

                current = next;
            }
        }
#pragma warning restore CS8602

        private static string SanitizeAssetName(string name)
        {
            foreach (var invalid in System.IO.Path.GetInvalidFileNameChars())
            {
                name = name.Replace(invalid, '_');
            }

            return string.IsNullOrWhiteSpace(name) ? "Timeline" : name;
        }

        private static string RuntimeSlowMoNote()
        {
            return "Runtime slow-mo is intentionally not hidden in editor authoring tools. During the ending session, game code should tween Time.timeScale (for example 0.2-0.4), set Time.fixedDeltaTime = baseFixedDeltaTime * Time.timeScale while slowed, then restore both values after the Timeline beat.";
        }

        private static JObject BrainEnsureSchema()
        {
            var schema = BaseSchema("Ensure CinemachineBrain on a Unity Camera.");
            var properties = (JObject)schema["properties"]!;
            properties["cameraPath"] = StringSchema("Transform path of Unity Camera.");
            properties["cameraInstanceId"] = IntSchema("Instance id of Camera or owning GameObject.");
            properties["cameraName"] = StringSchema("Name to use when creating a missing Camera.");
            properties["createCameraIfMissing"] = BoolSchema("Create a Camera when no existing Camera resolves. Default true.");
            properties["dryRun"] = BoolSchema("Validate target/create plan without mutating scene state.");
            return schema;
        }

        private static JObject CinemachineCreateSchema()
        {
            var schema = BaseSchema("Create a Cinemachine 3 camera.");
            var properties = (JObject)schema["properties"]!;
            properties["name"] = StringSchema("New Cinemachine camera GameObject name.");
            properties["parentPath"] = StringSchema("Optional parent transform path.");
            properties["targetPath"] = StringSchema("Optional subject transform path for target/follow/look-at and distance placement.");
            properties["position"] = Vector3Schema("World position for the camera.");
            properties["distance"] = NumberSchema("Distance behind target on negative Z when position is omitted.");
            properties["priority"] = IntSchema("Cinemachine priority.");
            properties["lens"] = LensSchema();
            properties["dryRun"] = BoolSchema("Validate create plan without mutating scene state.");
            return schema;
        }

        private static JObject CinemachineSetSchema()
        {
            var schema = TargetSchema("Patch an existing CinemachineCamera.");
            var properties = (JObject)schema["properties"]!;
            properties["targetPath"] = StringSchema("Transform path for target/follow/look-at.");
            properties["priority"] = IntSchema("Cinemachine priority.");
            properties["lens"] = LensSchema();
            properties["enabled"] = BoolSchema("Enable/disable camera component.");
            properties["dryRun"] = BoolSchema("Validate patch without mutating scene state.");
            return schema;
        }

        private static JObject SequencerCreateSchema()
        {
            var schema = BaseSchema("Create a CM3 CinemachineSequencerCamera plus child CinemachineCameras.");
            var properties = (JObject)schema["properties"]!;
            properties["name"] = StringSchema("New Sequencer Camera GameObject name.");
            properties["parentPath"] = StringSchema("Optional parent transform path.");
            properties["targetPath"] = StringSchema("Optional subject transform path for child camera targets and distance placement.");
            properties["position"] = Vector3Schema("World position for the Sequencer Camera parent.");
            properties["loop"] = BoolSchema("Set Sequencer Camera Loop. Default true.");
            properties["ensureBrain"] = BoolSchema("Ensure CinemachineBrain on a Unity Camera. Default false.");
            properties["cameraPath"] = StringSchema("Unity Camera transform path for optional Brain.");
            properties["cameraInstanceId"] = IntSchema("Camera or owning GameObject instance id for optional Brain.");
            properties["cameraName"] = StringSchema("Unity Camera name when createCameraIfMissing=true.");
            properties["createCameraIfMissing"] = BoolSchema("Create a Unity Camera only when ensureBrain=true and no camera resolves. Default false.");
            properties["shots"] = new JObject
            {
                ["type"] = "array",
                ["description"] = "Shot objects: name, hold/holdSeconds, fieldOfView/fov, distance, priority, blendStyle, blendTime. Creates child CinemachineCameras and Sequencer Instructions only; never creates Timeline assets.",
            };
            properties["dryRun"] = BoolSchema("Validate Sequencer authoring plan without mutating scene/assets.");
            return schema;
        }

        private static JObject SplineDollySetSchema()
        {
            var schema = TargetSchema("Add/update a CM3 CinemachineSplineDolly on an existing CinemachineCamera using an existing SplineContainer.");
            schema["required"] = new JArray("dryRun");
            var properties = (JObject)schema["properties"]!;
            properties["splinePath"] = StringSchema("Transform path containing an existing UnityEngine.Splines.SplineContainer. Spline knots/shape are never created or edited.");
            properties["splineInstanceId"] = IntSchema("Instance id of an existing SplineContainer or owning GameObject.");
            properties["position"] = NumberSchema("Optional dolly camera position value.");
            properties["cameraPosition"] = NumberSchema("Alias for position.");
            properties["positionUnits"] = StringSchema("Optional Cinemachine PositionUnits value such as Normalized, Distance, or Knot.");
            properties["units"] = StringSchema("Alias for positionUnits.");
            properties["autoDollyEnabled"] = BoolSchema("Optional AutoDolly.Enabled toggle when the CM3 type exposes it.");
            properties["dryRun"] = BoolSchema("Required. Validate existing camera/spline plan without mutating scene state.");
            return schema;
        }

        private static JObject BlenderSettingsSetSchema()
        {
            var schema = BaseSchema("Create or update a CM3 CinemachineBlenderSettings asset.");
            schema["required"] = new JArray("assetPath", "dryRun");
            var properties = (JObject)schema["properties"]!;
            properties["assetPath"] = StringSchema("Explicit Assets/*.asset path for the Blender Settings asset.");
            properties["assignToSelectedBrain"] = BoolSchema("Assign asset to the selected CinemachineBrain with Undo. Default false.");
            properties["blends"] = new JObject
            {
                ["type"] = "array",
                ["description"] = $"Custom blend entries capped at {MaxCustomBlendEntries}: from, to, style/blendStyle, time/blendTime. Use ANY CAMERA for wildcard endpoints.",
                ["maxItems"] = MaxCustomBlendEntries,
            };
            properties["dryRun"] = BoolSchema("Required. Validate asset/update/assignment plan without mutating assets or scene state.");
            return schema;
        }

        private static JObject ConfinerSetSchema()
        {
            var schema = TargetSchema("Add/update a CM3 Confiner2D or Confiner3D on an existing CinemachineCamera using an existing collider.");
            schema["required"] = new JArray("dimension", "dryRun");
            var properties = (JObject)schema["properties"]!;
            properties["dimension"] = StringSchema("'2d' for CinemachineConfiner2D with Collider2D, or '3d' for CinemachineConfiner3D with Collider.");
            properties["colliderPath"] = StringSchema("Transform path containing an existing Collider2D/Collider. Collider geometry is never created or edited.");
            properties["colliderInstanceId"] = IntSchema("Instance id of an existing Collider2D/Collider or owning GameObject.");
            properties["damping"] = NumberSchema("Optional Damping field when exposed by the confiner.");
            properties["slowingDistance"] = NumberSchema("Optional SlowingDistance field when exposed by the confiner.");
            properties["invalidateCache"] = BoolSchema("Explicitly invalidate bounding-shape/volume cache when the confiner exposes such a method. Default false.");
            properties["invalidateLensCache"] = BoolSchema("Explicitly invalidate lens cache when exposed after lens/FOV/orthographic-size changes. Default false.");
            properties["dryRun"] = BoolSchema("Required. Validate existing camera/collider plan without mutating scene state.");
            return schema;
        }

        private static JObject TimelineDirectorCreateSchema()
        {
            var schema = BaseSchema("Create a PlayableDirector and optional TimelineAsset.");
            var properties = (JObject)schema["properties"]!;
            properties["name"] = StringSchema("Director GameObject name.");
            properties["assetPath"] = StringSchema("Timeline asset path. Use .playable.");
            properties["createAsset"] = BoolSchema("Create missing TimelineAsset and assign it.");
            properties["time"] = NumberSchema("Initial director time.");
            properties["dryRun"] = BoolSchema("Validate create plan without mutating scene/assets.");
            return schema;
        }

        private static JObject ShotSequenceCreateSchema()
        {
            var schema = BaseSchema("Create a CinemachineTrack/CinemachineShot sequence.");
            var properties = (JObject)schema["properties"]!;
            properties["directorPath"] = StringSchema("Existing PlayableDirector path.");
            properties["directorInstanceId"] = IntSchema("Existing PlayableDirector instance id.");
            properties["directorName"] = StringSchema("New director name when directorPath is omitted.");
            properties["cameraPath"] = StringSchema("Unity Camera path for CinemachineBrain binding.");
            properties["cameraName"] = StringSchema("Unity Camera name when creating missing camera.");
            properties["targetPath"] = StringSchema("Subject transform path for shot camera target and distance placement.");
            properties["assetPath"] = StringSchema("Timeline asset path to create/assign.");
            properties["trackName"] = StringSchema("Cinemachine track name.");
            properties["shots"] = new JObject
            {
                ["type"] = "array",
                ["description"] = "Optional shot objects: name, start, duration, fieldOfView/fov, distance, priority. Overlap adjacent clip times for blends.",
            };
            properties["dryRun"] = BoolSchema("Validate sequence plan without mutating scene/assets.");
            return schema;
        }

        private static JObject TimelinePreviewSchema()
        {
            var schema = BaseSchema("Scrub/evaluate/play/stop a PlayableDirector.");
            var properties = (JObject)schema["properties"]!;
            properties["directorPath"] = StringSchema("PlayableDirector transform path.");
            properties["directorInstanceId"] = IntSchema("PlayableDirector instance id.");
            properties["time"] = NumberSchema("Director time before action.");
            properties["action"] = StringSchema("evaluate, scrub, play, or stop.");
            properties["dryRun"] = BoolSchema("Validate target/action without mutating preview state.");
            return schema;
        }

        private static JObject BaseSchema(string description)
        {
            return new JObject
            {
                ["type"] = "object",
                ["description"] = description,
                ["properties"] = new JObject(),
            };
        }

        private static JObject TargetSchema(string description)
        {
            var schema = BaseSchema(description);
            var properties = (JObject)schema["properties"]!;
            properties["targetPath"] = StringSchema("Component transform path.");
            properties["instanceId"] = IntSchema("Component instance id.");
            return schema;
        }

        private static JObject LensSchema()
        {
            return new JObject
            {
                ["type"] = "object",
                ["description"] = "Safe lens patch fields.",
                ["properties"] = new JObject
                {
                    ["fieldOfView"] = NumberSchema("Perspective FOV."),
                    ["orthographicSize"] = NumberSchema("Orthographic size."),
                    ["nearClipPlane"] = NumberSchema("Near clip plane."),
                    ["farClipPlane"] = NumberSchema("Far clip plane."),
                    ["modeOverride"] = StringSchema("Cinemachine lens mode override when supported."),
                },
            };
        }

        private static JObject Vector3Schema(string description)
        {
            return new JObject
            {
                ["type"] = "object",
                ["description"] = description,
                ["properties"] = new JObject
                {
                    ["x"] = NumberSchema("X"),
                    ["y"] = NumberSchema("Y"),
                    ["z"] = NumberSchema("Z"),
                },
            };
        }

        private static JObject StringSchema(string description)
        {
            return new JObject { ["type"] = "string", ["description"] = description };
        }

        private static JObject BoolSchema(string description)
        {
            return new JObject { ["type"] = "boolean", ["description"] = description };
        }

        private static JObject NumberSchema(string description)
        {
            return new JObject { ["type"] = "number", ["description"] = description };
        }

        private static JObject IntSchema(string description)
        {
            return new JObject { ["type"] = "integer", ["description"] = description };
        }

        private static string? ReadString(JToken args, string name)
        {
            var token = args[name];
            return token == null || token.Type == JTokenType.Null ? null : token.Value<string>();
        }

        private static bool TryString(JToken args, string name, out string value)
        {
            value = string.Empty;
            var token = args[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                return false;
            }

            value = token.Value<string>() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(value);
        }

        private static bool TryInt(JToken args, string name, out int value)
        {
            value = 0;
            var token = args[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                return false;
            }

            value = token.Value<int>();
            return true;
        }

        private static bool TryBool(JToken args, string name, out bool value)
        {
            value = false;
            var token = args[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                return false;
            }

            value = token.Value<bool>();
            return true;
        }

        private static bool TryDouble(JToken args, string name, out double value)
        {
            value = 0d;
            var token = args[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                return false;
            }

            value = token.Value<double>();
            return true;
        }

        private static bool OptionalBool(JToken args, string name, bool fallback)
        {
            var token = args[name];
            return token == null || token.Type == JTokenType.Null ? fallback : token.Value<bool>();
        }

        private static bool? OptionalBoolNullable(JToken args, string name)
        {
            var token = args[name];
            return token == null || token.Type == JTokenType.Null ? null : token.Value<bool>();
        }

        private static int? OptionalInt(JToken args, string name)
        {
            var token = args[name];
            return token == null || token.Type == JTokenType.Null ? null : token.Value<int>();
        }

        private static float? OptionalFloat(JToken args, string name)
        {
            var token = args[name];
            return token == null || token.Type == JTokenType.Null ? null : token.Value<float>();
        }

        private static double? OptionalDouble(JToken args, string name)
        {
            var token = args[name];
            return token == null || token.Type == JTokenType.Null ? null : token.Value<double>();
        }

        private static Vector3 ReadVector3(JToken token, string fieldName)
        {
            if (token is not JObject obj)
            {
                throw new ArgumentException($"{fieldName} must be an object with x/y/z numbers.");
            }

            return new Vector3(obj["x"]?.Value<float>() ?? 0f, obj["y"]?.Value<float>() ?? 0f, obj["z"]?.Value<float>() ?? 0f);
        }

        private static Dictionary<string, object?> Vector3Row(Vector3 value)
        {
            return new Dictionary<string, object?>
            {
                ["x"] = Round(value.x),
                ["y"] = Round(value.y),
                ["z"] = Round(value.z),
            };
        }

        private static double? Round(float? value)
        {
            return value.HasValue ? Math.Round(value.Value, 4) : null;
        }

        private static double Round(float value)
        {
            return Math.Round(value, 4);
        }

        private readonly struct ShotSpec
        {
            public ShotSpec(string name, double start, double duration, float fieldOfView, float distance, int priority)
            {
                Name = name;
                Start = start;
                Duration = duration;
                FieldOfView = fieldOfView;
                Distance = distance;
                Priority = priority;
            }

            public string Name { get; }

            public double Start { get; }

            public double Duration { get; }

            public float FieldOfView { get; }

            public float Distance { get; }

            public int Priority { get; }
        }

        private readonly struct SequencerShotSpec
        {
            public SequencerShotSpec(string name, double hold, float fieldOfView, float distance, int priority, string blendStyle, float blendTime)
            {
                Name = name;
                Hold = hold;
                FieldOfView = fieldOfView;
                Distance = distance;
                Priority = priority;
                BlendStyle = blendStyle;
                BlendTime = blendTime;
            }

            public string Name { get; }

            public double Hold { get; }

            public float FieldOfView { get; }

            public float Distance { get; }

            public int Priority { get; }

            public string BlendStyle { get; }

            public float BlendTime { get; }
        }

        private readonly struct BlenderBlendSpec
        {
            public BlenderBlendSpec(string from, string to, string style, float time)
            {
                From = from;
                To = to;
                Style = style;
                Time = time;
            }

            public string From { get; }

            public string To { get; }

            public string Style { get; }

            public float Time { get; }
        }

        private readonly struct OptionalPackageStatus
        {
            public OptionalPackageStatus(string packageName, bool packageInstalled, string? packageVersion, Type? primaryType, string primaryTypeName, Type? secondaryType, string secondaryTypeName)
            {
                PackageName = packageName;
                PackageInstalled = packageInstalled;
                PackageVersion = packageVersion;
                PrimaryType = primaryType;
                PrimaryTypeName = primaryTypeName;
                SecondaryType = secondaryType;
                SecondaryTypeName = secondaryTypeName;
            }

            public string PackageName { get; }

            public bool PackageInstalled { get; }

            public string? PackageVersion { get; }

            public Type? PrimaryType { get; }

            public string PrimaryTypeName { get; }

            public Type? SecondaryType { get; }

            public string SecondaryTypeName { get; }

            public bool PrimaryTypeLoaded => PrimaryType != null;

            public bool SecondaryTypeLoaded => SecondaryType != null;

            public bool TypesLoaded => PrimaryTypeLoaded && SecondaryTypeLoaded;

            public bool Available => PackageInstalled && TypesLoaded;

            public string Reason
            {
                get
                {
                    if (Available)
                    {
                        return "available";
                    }

                    if (!PackageInstalled)
                    {
                        return $"{PackageName} package is not installed.";
                    }

                    return $"{PackageName} package detected, but {PrimaryTypeName} and/or {SecondaryTypeName} types are not loaded.";
                }
            }

            public Dictionary<string, object?> ToDictionary()
            {
                return new Dictionary<string, object?>
                {
                    ["available"] = Available,
                    ["packageName"] = PackageName,
                    ["packageVersion"] = PackageVersion,
                    ["packageInstalled"] = PackageInstalled,
                    ["typesLoaded"] = TypesLoaded,
                    ["primaryTypeName"] = PrimaryTypeName,
                    ["primaryTypeLoaded"] = PrimaryTypeLoaded,
                    ["secondaryTypeName"] = SecondaryTypeName,
                    ["secondaryTypeLoaded"] = SecondaryTypeLoaded,
                    ["reason"] = Reason,
                };
            }
        }

        private readonly struct AdvancedHelperStatus
        {
            public AdvancedHelperStatus(
                string key,
                string displayName,
                bool packageInstalled,
                string? packageVersion,
                string? apiFamily,
                bool versionDefineActive,
                Type? helperType,
                string helperTypeName,
                string? optionalPackageName,
                bool optionalPackageInstalled,
                string? optionalPackageVersion,
                Type? optionalType,
                string? optionalTypeName,
                Type? secondaryHelperType = null,
                string? secondaryHelperTypeName = null,
                Type? tertiaryHelperType = null,
                string? tertiaryHelperTypeName = null,
                bool optionalVersionDefineActive = true)
            {
                Key = key;
                DisplayName = displayName;
                PackageInstalled = packageInstalled;
                PackageVersion = packageVersion;
                ApiFamily = apiFamily;
                VersionDefineActive = versionDefineActive;
                HelperType = helperType;
                HelperTypeName = helperTypeName;
                OptionalPackageName = optionalPackageName;
                OptionalPackageInstalled = optionalPackageInstalled;
                OptionalPackageVersion = optionalPackageVersion;
                OptionalType = optionalType;
                OptionalTypeName = optionalTypeName;
                SecondaryHelperType = secondaryHelperType;
                SecondaryHelperTypeName = secondaryHelperTypeName;
                TertiaryHelperType = tertiaryHelperType;
                TertiaryHelperTypeName = tertiaryHelperTypeName;
                OptionalVersionDefineActive = optionalVersionDefineActive;
            }

            public string Key { get; }

            public string DisplayName { get; }

            public bool PackageInstalled { get; }

            public string? PackageVersion { get; }

            public string? ApiFamily { get; }

            public bool VersionDefineActive { get; }

            public Type? HelperType { get; }

            public string HelperTypeName { get; }

            public string? OptionalPackageName { get; }

            public bool OptionalPackageInstalled { get; }

            public string? OptionalPackageVersion { get; }

            public Type? OptionalType { get; }

            public string? OptionalTypeName { get; }

            public Type? SecondaryHelperType { get; }

            public string? SecondaryHelperTypeName { get; }

            public Type? TertiaryHelperType { get; }

            public string? TertiaryHelperTypeName { get; }

            public bool OptionalVersionDefineActive { get; }

            public bool HelperTypeLoaded => HelperType != null;

            public bool SecondaryHelperTypeLoaded => SecondaryHelperTypeName == null || SecondaryHelperType != null;

            public bool TertiaryHelperTypeLoaded => TertiaryHelperTypeName == null || TertiaryHelperType != null;

            public bool OptionalGateRequired => OptionalPackageName != null;

            public bool OptionalGateAvailable => !OptionalGateRequired || (OptionalPackageInstalled && OptionalType != null && OptionalVersionDefineActive);

            public bool TypesLoaded => HelperTypeLoaded && SecondaryHelperTypeLoaded && TertiaryHelperTypeLoaded && OptionalGateAvailable;

            public bool Available => PackageInstalled
                && VersionDefineActive
                && ApiFamily == CinemachineApiFamilyCm3
                && TypesLoaded;

            public string Reason
            {
                get
                {
                    if (Available)
                    {
                        return "available";
                    }

                    if (!PackageInstalled)
                    {
                        return $"{CinemachinePackageName} package is not installed.";
                    }

                    if (ApiFamily == CinemachineApiFamilyCm2)
                    {
                        return $"Cinemachine 2.x API detected; {DisplayName} requires Cinemachine 3.x. No package upgrade or package mutation was attempted.";
                    }

                    if (ApiFamily == CinemachineApiFamilyCm3LegacyObsolete)
                    {
                        return $"Cinemachine 3 legacy obsolete Unity.Cinemachine.CinemachineVirtualCamera API detected; {DisplayName} requires CM3 helper types. No package mutation was attempted.";
                    }

                    if (ApiFamily != CinemachineApiFamilyCm3)
                    {
                        return $"Cinemachine apiFamily is '{ApiFamily ?? "unknown"}', not cm3.";
                    }

                    if (!VersionDefineActive)
                    {
                        return "CHIEVFX_MCP_HAS_CINEMACHINE is not active yet; wait for Unity compile or refresh assembly definitions.";
                    }

                    if (!HelperTypeLoaded)
                    {
                        return $"CM3 package detected, but required type {HelperTypeName} is not loaded.";
                    }

                    if (!SecondaryHelperTypeLoaded)
                    {
                        return $"CM3 package detected, but required type {SecondaryHelperTypeName} is not loaded.";
                    }

                    if (!TertiaryHelperTypeLoaded)
                    {
                        return $"CM3 package detected, but required type {TertiaryHelperTypeName} is not loaded.";
                    }

                    if (OptionalGateRequired && !OptionalPackageInstalled)
                    {
                        return $"{OptionalPackageName} package is not installed.";
                    }

                    if (OptionalGateRequired && !OptionalVersionDefineActive)
                    {
                        return $"CHIEVFX_MCP_HAS_SPLINES is not active yet; wait for Unity compile or refresh assembly definitions.";
                    }

                    if (OptionalGateRequired && OptionalType == null)
                    {
                        return $"{OptionalPackageName} package detected, but required type {OptionalTypeName} is not loaded.";
                    }

                    return $"{DisplayName} unavailable.";
                }
            }

            public Dictionary<string, object?> ToDictionary()
            {
                var result = new Dictionary<string, object?>
                {
                    ["available"] = Available,
                    ["key"] = Key,
                    ["displayName"] = DisplayName,
                    ["packageName"] = CinemachinePackageName,
                    ["packageVersion"] = PackageVersion,
                    ["packageInstalled"] = PackageInstalled,
                    ["apiFamily"] = ApiFamily,
                    ["cinemachineApiFamily"] = ApiFamily,
                    ["versionDefineActive"] = VersionDefineActive,
                    ["typesLoaded"] = TypesLoaded,
                    ["helperTypeName"] = HelperTypeName,
                    ["helperTypeLoaded"] = HelperTypeLoaded,
                    ["secondaryHelperTypeName"] = SecondaryHelperTypeName,
                    ["secondaryHelperTypeLoaded"] = SecondaryHelperTypeLoaded,
                    ["tertiaryHelperTypeName"] = TertiaryHelperTypeName,
                    ["tertiaryHelperTypeLoaded"] = TertiaryHelperTypeLoaded,
                    ["reason"] = Reason,
                };

                if (OptionalGateRequired)
                {
                    result["optionalPackageName"] = OptionalPackageName;
                    result["optionalPackageVersion"] = OptionalPackageVersion;
                    result["optionalPackageInstalled"] = OptionalPackageInstalled;
                    result["optionalTypeName"] = OptionalTypeName;
                    result["optionalTypeLoaded"] = OptionalType != null;
                    result["optionalVersionDefineActive"] = OptionalVersionDefineActive;
                    result["optionalGateAvailable"] = OptionalGateAvailable;
                }

                return result;
            }
        }

        private readonly struct SequencerCameraStatus
        {
            public SequencerCameraStatus(
                bool packageInstalled,
                string? packageVersion,
                string? apiFamily,
                bool versionDefineActive,
                Type? sequencerCameraType,
                Type? instructionType,
                Type? cameraType,
                Type? virtualCameraBaseType,
                Type? blendDefinitionType,
                Type? brainType)
            {
                PackageInstalled = packageInstalled;
                PackageVersion = packageVersion;
                ApiFamily = apiFamily;
                VersionDefineActive = versionDefineActive;
                SequencerCameraType = sequencerCameraType;
                InstructionType = instructionType;
                CameraType = cameraType;
                VirtualCameraBaseType = virtualCameraBaseType;
                BlendDefinitionType = blendDefinitionType;
                BrainType = brainType;
            }

            public bool PackageInstalled { get; }

            public string? PackageVersion { get; }

            public string? ApiFamily { get; }

            public bool VersionDefineActive { get; }

            public Type? SequencerCameraType { get; }

            public Type? InstructionType { get; }

            public Type? CameraType { get; }

            public Type? VirtualCameraBaseType { get; }

            public Type? BlendDefinitionType { get; }

            public Type? BrainType { get; }

            public bool SequencerCameraTypeLoaded => SequencerCameraType != null;

            public bool TypesLoaded => SequencerCameraType != null
                && InstructionType != null
                && CameraType != null
                && VirtualCameraBaseType != null
                && BlendDefinitionType != null
                && BrainType != null;

            public bool Available => PackageInstalled
                && VersionDefineActive
                && ApiFamily == CinemachineApiFamilyCm3
                && TypesLoaded;

            public string Reason
            {
                get
                {
                    if (Available)
                    {
                        return "available";
                    }

                    if (!PackageInstalled)
                    {
                        return $"{CinemachinePackageName} package is not installed.";
                    }

                    if (ApiFamily == CinemachineApiFamilyCm2)
                    {
                        return "Cinemachine 2.x API detected; Sequencer Camera requires Cinemachine 3.x. No package upgrade or package mutation was attempted.";
                    }

                    if (ApiFamily == CinemachineApiFamilyCm3LegacyObsolete)
                    {
                        return "Cinemachine 3 legacy obsolete Unity.Cinemachine.CinemachineVirtualCamera API detected; Sequencer Camera requires CM3 CinemachineSequencerCamera and CinemachineCamera. No package mutation was attempted.";
                    }

                    if (ApiFamily != CinemachineApiFamilyCm3)
                    {
                        return $"Cinemachine apiFamily is '{ApiFamily ?? "unknown"}', not cm3.";
                    }

                    if (!VersionDefineActive)
                    {
                        return "CHIEVFX_MCP_HAS_CINEMACHINE is not active yet; wait for Unity compile or refresh assembly definitions.";
                    }

                    if (!TypesLoaded)
                    {
                        return "CM3 package detected, but one or more Sequencer Camera types are not loaded.";
                    }

                    return "Sequencer Camera unavailable.";
                }
            }

            public Dictionary<string, object?> ToDictionary()
            {
                return new Dictionary<string, object?>
                {
                    ["available"] = Available,
                    ["packageName"] = CinemachinePackageName,
                    ["packageVersion"] = PackageVersion,
                    ["packageInstalled"] = PackageInstalled,
                    ["apiFamily"] = ApiFamily,
                    ["cinemachineApiFamily"] = ApiFamily,
                    ["versionDefineActive"] = VersionDefineActive,
                    ["typesLoaded"] = TypesLoaded,
                    ["sequencerCameraAvailable"] = Available,
                    ["sequencerCameraTypeLoaded"] = SequencerCameraTypeLoaded,
                    ["instructionTypeLoaded"] = InstructionType != null,
                    ["cinemachineCameraTypeLoaded"] = CameraType != null,
                    ["virtualCameraBaseTypeLoaded"] = VirtualCameraBaseType != null,
                    ["blendDefinitionTypeLoaded"] = BlendDefinitionType != null,
                    ["brainTypeLoaded"] = BrainType != null,
                    ["reason"] = Reason,
                };
            }
        }

        private readonly struct PackageStatus
        {
            public PackageStatus(string packageName, bool packageInstalled, string? packageVersion, string? apiFamily, bool typesLoaded, bool versionDefineActive, Type? cameraOrAssetType, Type? brainOrTrackType, Type? trackOrClipType, Type? shotOrDirectorType)
            {
                PackageName = packageName;
                PackageInstalled = packageInstalled;
                PackageVersion = packageVersion;
                ApiFamily = apiFamily;
                TypesLoaded = typesLoaded;
                VersionDefineActive = versionDefineActive;
                if (packageName == CinemachinePackageName)
                {
                    CameraType = cameraOrAssetType;
                    BrainType = brainOrTrackType;
                    TrackType = trackOrClipType;
                    ShotType = shotOrDirectorType;
                    AssetType = null;
                    TimelineTrackType = null;
                    ClipType = null;
                    DirectorType = null;
                }
                else
                {
                    AssetType = cameraOrAssetType;
                    TimelineTrackType = brainOrTrackType;
                    ClipType = trackOrClipType;
                    DirectorType = shotOrDirectorType;
                    CameraType = null;
                    BrainType = null;
                    TrackType = null;
                    ShotType = null;
                }
            }

            public string PackageName { get; }

            public bool PackageInstalled { get; }

            public string? PackageVersion { get; }

            public string? ApiFamily { get; }

            public bool TypesLoaded { get; }

            public bool VersionDefineActive { get; }

            public bool Available => PackageInstalled && TypesLoaded && VersionDefineActive;

            public Type? CameraType { get; }

            public Type? BrainType { get; }

            public Type? TrackType { get; }

            public Type? ShotType { get; }

            public Type? AssetType { get; }

            public Type? TimelineTrackType { get; }

            public Type? ClipType { get; }

            public Type? DirectorType { get; }

            public string Reason
            {
                get
                {
                    if (Available)
                    {
                        return "available";
                    }

                    if (PackageName == CinemachinePackageName)
                    {
                        if (ApiFamily == CinemachineApiFamilyCm2)
                        {
                            return "Cinemachine 2.x API detected; chievfx.cameras authoring tools are deferred until Cinemachine 3.x Unity.Cinemachine.CinemachineCamera is available. No package upgrade or package mutation was attempted.";
                        }

                        if (ApiFamily == CinemachineApiFamilyCm3LegacyObsolete)
                        {
                            return "Cinemachine 3 legacy obsolete Unity.Cinemachine.CinemachineVirtualCamera API detected; chievfx.cameras authoring tools currently require Unity.Cinemachine.CinemachineCamera. No package mutation was attempted.";
                        }
                    }

                    if (!PackageInstalled)
                    {
                        return $"{PackageName} package is not installed.";
                    }

                    if (PackageName == CinemachinePackageName && ApiFamily == CinemachineApiFamilyCm3 && !TypesLoaded)
                    {
                        return "Cinemachine 3.x package detected, but Unity.Cinemachine.CinemachineCamera and Unity.Cinemachine.CinemachineBrain types are not loaded.";
                    }

                    if (!TypesLoaded)
                    {
                        return $"{PackageName} runtime/editor types are not loaded.";
                    }

                    return "asmdef versionDefine is not active yet; wait for Unity compile or refresh assembly definitions.";
                }
            }

            public Dictionary<string, object?> ToDictionary()
            {
                var result = new Dictionary<string, object?>
                {
                    ["available"] = Available,
                    ["packageName"] = PackageName,
                    ["packageVersion"] = PackageVersion,
                    ["packageInstalled"] = PackageInstalled,
                    ["typesLoaded"] = TypesLoaded,
                    ["versionDefineActive"] = VersionDefineActive,
                    ["reason"] = Reason,
                };

                if (PackageName == CinemachinePackageName)
                {
                    result["apiFamily"] = ApiFamily;
                    result["cinemachineApiFamily"] = ApiFamily;
                }

                return result;
            }
        }

        private readonly struct DependencyStatus
        {
            public DependencyStatus(PackageStatus cinemachine, SequencerCameraStatus sequencer, AdvancedHelperStatus splinesDolly, AdvancedHelperStatus inputAxisController, OptionalPackageStatus inputSystem, AdvancedHelperStatus blenderSettings, AdvancedHelperStatus impulse, Type? collisionImpulseSourceType, Type? externalImpulseListenerType, AdvancedHelperStatus confiner2D, AdvancedHelperStatus confiner3D, Type? obsoleteConfinerType, PackageStatus timeline)
            {
                Cinemachine = cinemachine;
                Sequencer = sequencer;
                SplinesDolly = splinesDolly;
                InputAxisController = inputAxisController;
                InputSystem = inputSystem;
                BlenderSettings = blenderSettings;
                Impulse = impulse;
                CollisionImpulseSourceType = collisionImpulseSourceType;
                ExternalImpulseListenerType = externalImpulseListenerType;
                Confiner2D = confiner2D;
                Confiner3D = confiner3D;
                ObsoleteConfinerType = obsoleteConfinerType;
                Timeline = timeline;
            }

            public PackageStatus Cinemachine { get; }

            public SequencerCameraStatus Sequencer { get; }

            public AdvancedHelperStatus SplinesDolly { get; }

            public AdvancedHelperStatus InputAxisController { get; }

            public OptionalPackageStatus InputSystem { get; }

            public AdvancedHelperStatus BlenderSettings { get; }

            public AdvancedHelperStatus Impulse { get; }

            public Type? CollisionImpulseSourceType { get; }

            public Type? ExternalImpulseListenerType { get; }

            public AdvancedHelperStatus Confiner2D { get; }

            public AdvancedHelperStatus Confiner3D { get; }

            public Type? ObsoleteConfinerType { get; }

            public PackageStatus Timeline { get; }

            public bool AnyAvailable => Cinemachine.Available || Sequencer.Available || SplinesDolly.Available || InputAxisController.Available || BlenderSettings.Available || Impulse.Available || Confiner2D.Available || Confiner3D.Available || Timeline.Available;

            public string Reason => AnyAvailable ? "one-or-more-dependencies-available" : "Cinemachine and Timeline extension dependencies are unavailable.";

            public Dictionary<string, object?> ToDictionary()
            {
                return new Dictionary<string, object?>
                {
                    ["available"] = AnyAvailable,
                    ["cinemachine"] = Cinemachine.ToDictionary(),
                    ["sequencerCamera"] = Sequencer.ToDictionary(),
                    ["splinesDolly"] = SplinesDolly.ToDictionary(),
                    ["inputAxisController"] = InputAxisController.ToDictionary(),
                    ["inputSystem"] = InputSystem.ToDictionary(),
                    ["blenderSettings"] = BlenderSettings.ToDictionary(),
                    ["impulse"] = Impulse.ToDictionary(),
                    ["collisionImpulseSourceTypeLoaded"] = CollisionImpulseSourceType != null,
                    ["externalImpulseListenerTypeLoaded"] = ExternalImpulseListenerType != null,
                    ["confiner2D"] = Confiner2D.ToDictionary(),
                    ["confiner3D"] = Confiner3D.ToDictionary(),
                    ["obsoleteConfinerTypeLoaded"] = ObsoleteConfinerType != null,
                    ["obsoleteConfinerTypeName"] = "Unity.Cinemachine.CinemachineConfiner",
                    ["timeline"] = Timeline.ToDictionary(),
                };
            }
        }
    }
}
