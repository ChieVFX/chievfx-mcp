#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Chievfx.Mcp.Editor;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Chievfx.Mcp.Editor
{
    internal static class ChievfxMcpFirstPartyExtensionLoader
    {
        private static readonly ExtensionRegistrationType[] KnownRegistrationTypes =
        {
            new(
                "Chievfx.Mcp.Extensions.Ecs",
                "Chievfx.Mcp.Extensions.Ecs.ChievfxMcpEcsExtension"),
            new(
                "Chievfx.Mcp.Extensions.Ugui",
                "Chievfx.Mcp.Extensions.Ugui.ChievfxMcpUguiExtension"),
            new(
                "Chievfx.Mcp.Extensions.UiToolkit",
                "Chievfx.Mcp.Extensions.UiToolkit.ChievfxMcpUiToolkitExtension"),
            new(
                "Chievfx.Mcp.Extensions.Particles",
                "Chievfx.Mcp.Extensions.Particles.ChievfxMcpParticlesExtension"),
            new(
                "Chievfx.Mcp.Extensions.Cameras",
                "Chievfx.Mcp.Extensions.Cameras.ChievfxMcpCamerasExtension"),
            new(
                "Chievfx.Mcp.Editor",
                "Chievfx.Mcp.Extensions.Control.ChievfxMcpControlExtension"),
        };

        public static void EnsureLoaded()
        {
            foreach (var registrationType in KnownRegistrationTypes)
            {
                EnsureRegistrationTypeLoaded(registrationType);
            }
        }

        private static void EnsureRegistrationTypeLoaded(ExtensionRegistrationType registrationType)
        {
            try
            {
                var assembly = FindLoadedAssembly(registrationType.AssemblyName)
                    ?? Assembly.Load(registrationType.AssemblyName);
                var type = assembly.GetType(registrationType.TypeName, throwOnError: false);
                if (type == null)
                {
                    Debug.LogWarning(
                        $"ChievFX MCP could not find extension registration type '{registrationType.TypeName}' in '{registrationType.AssemblyName}'.");
                    return;
                }

                RuntimeHelpers.RunClassConstructor(type.TypeHandle);
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"ChievFX MCP could not load first-party extension assembly '{registrationType.AssemblyName}'. {ex.Message}");
            }
        }

        private static Assembly? FindLoadedAssembly(string assemblyName)
        {
            return UnityAssemblies.GetLoaded()
                .FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, assemblyName, StringComparison.Ordinal));
        }

        private readonly struct ExtensionRegistrationType
        {
            public ExtensionRegistrationType(string assemblyName, string typeName)
            {
                AssemblyName = assemblyName;
                TypeName = typeName;
            }

            public string AssemblyName { get; }

            public string TypeName { get; }
        }
    }
}

namespace Chievfx.Mcp.Extensions.Control
{
    [InitializeOnLoad]
    internal static partial class ChievfxMcpControlExtension
    {
        private const string ExtensionId = "chievfx.control";
        private const string Category = "control";
        private const string EssentialsCategory = "essentials";
        private const string StatusUri = "chievfx://extensions/chievfx.control/status";
        private static bool? playModeOverrideForTests;

        static ChievfxMcpControlExtension()
        {
            Chievfx.Mcp.Editor.ChievfxMcpExtensionRegistry.RegisterExtension(CreateDescriptor());
        }

        public static object? RunToolForTests(string toolName, string argsJson)
        {
            return RunTool(toolName, string.IsNullOrWhiteSpace(argsJson) ? new JObject() : JObject.Parse(argsJson));
        }

        public static object? ReadResourceForTests(string uri)
        {
            return ReadResource(uri);
        }

        public static void SetPlayModeOverrideForTests(bool? isPlaying)
        {
            playModeOverrideForTests = isPlaying;
        }

        private static Chievfx.Mcp.Editor.ChievfxMcpExtensionDescriptor CreateDescriptor()
        {
            var api = InputApi.TryCreate(out _, out var reason);
            var descriptor = new Chievfx.Mcp.Editor.ChievfxMcpExtensionDescriptor
            {
                Id = ExtensionId,
                DisplayName = "ChievFX MCP Control",
                Version = "0.1.0",
                Description = api
                    ? "New Input System-first keyboard, mouse, and touch input event helpers for Play Mode control."
                    : "Control helpers unavailable until com.unity.inputsystem is installed and loaded.",
                ToolRunner = RunTool,
                ResourceReader = ReadResource,
            };
            descriptor.Resources.Add(new Chievfx.Mcp.Editor.ChievfxMcpResourceDescriptor
            {
                Id = "control-status",
                Uri = StatusUri,
                Name = "Control extension status",
                Description = "Reports Input System availability, Play Mode state, current devices, and mutation gate requirements.",
                MimeType = "application/json",
                Category = EssentialsCategory,
            });
            descriptor.Tools.Add(Tool("editor-playmode-set", "Enter or exit Unity Play Mode.", PlayModeSetSchema(), EssentialsCategory));
            descriptor.Tools.Add(Tool("shader-status", "Why is it magenta/cyan? Reports shader compile errors and whether variants are still compiling. No args scans renderers in open scenes for missing/error shaders (magenta); path targets one shader or material.", ShaderStatusSchema(), EssentialsCategory));
            if (api)
            {
                descriptor.Tools.Add(Tool("input-control-keyboard-event", "Queue a New Input System keyboard down, up, or tap event. Tap holds the key for holdFrames player frames so wasPressedThisFrame edges are visible to game Update() code, and returns a completionMarker for events-wait.", KeyboardSchema()));
                descriptor.Tools.Add(Tool("input-control-keyboard-sequence", "Type a string (text) or tap a key list (keys) in ONE call — each key held holdFrames and spaced gapFrames apart, dispatched across player frames. Returns a completionMarker for events-wait. Use for typing and real-time/action input without a round-trip per key.", KeyboardSequenceSchema()));
                descriptor.Tools.Add(Tool("input-control-mouse-event", "Queue a New Input System mouse button or move event. Real injection routes through a virtual mouse (capturePointer, default true) so the OS cursor cannot overwrite injected positions; tap is frame-spaced and returns a completionMarker.", MouseSchema()));
                descriptor.Tools.Add(Tool("input-control-mouse-gesture", "Queue a mouse down/move/up gesture whose steps dispatch one per player frame. Returns a completionMarker for events-wait; dryRun:true only previews it.", GestureSchema()));
                descriptor.Tools.Add(Tool("input-control-touch-event", "Queue a New Input System touchscreen down, move, up, or tap event. Tap holds the touch for holdFrames player frames and returns a completionMarker for events-wait.", TouchSchema()));
                descriptor.Tools.Add(Tool("input-control-pointer-capture", "Begin, end, or inspect a pointer capture session: injected mouse events drive a virtual mouse while physical mice are disabled, preventing the OS cursor from overwriting injected positions. Ends automatically on Play Mode exit.", PointerCaptureSchema()));
            }

            _ = reason;
            return descriptor;
        }

        private static Chievfx.Mcp.Editor.ChievfxMcpToolDescriptor Tool(string name, string description, JObject schema, string? category = null)
        {
            return new Chievfx.Mcp.Editor.ChievfxMcpToolDescriptor
            {
                Name = name,
                Description = description,
                Category = category ?? Category,
                InputSchema = schema,
            };
        }

        private static object? RunTool(string toolName, JToken args)
        {
            if (string.Equals(toolName, "editor-playmode-set", StringComparison.Ordinal))
            {
                return PlayModeSet(args);
            }

            if (string.Equals(toolName, "shader-status", StringComparison.Ordinal))
            {
                return ShaderStatus(args);
            }

            if (!InputApi.TryCreate(out var api, out var reason))
            {
                return Result(toolName, null, null, true, false, Array.Empty<object>(), new[] { reason }, new[] { $"Tool '{toolName}' requires com.unity.inputsystem and loaded Input System types." });
            }

            return toolName switch
            {
                "input-control-keyboard-event" => Keyboard(args, api!),
                "input-control-keyboard-sequence" => KeyboardSequence(args, api!),
                "input-control-mouse-event" => Mouse(args, api!),
                "input-control-mouse-gesture" => Gesture(args, api!),
                "input-control-touch-event" => Touch(args, api!),
                "input-control-pointer-capture" => PointerCapture(args, api!),
                _ => throw new InvalidOperationException($"Unknown Control extension tool '{toolName}'."),
            };
        }

        private static object? ReadResource(string uri)
        {
            if (!string.Equals(uri, StatusUri, StringComparison.Ordinal))
            {
                return null;
            }

            var available = InputApi.TryCreate(out var api, out var reason);
            return new Dictionary<string, object?>
            {
                ["uri"] = uri,
                ["extensionId"] = ExtensionId,
                ["available"] = available,
                ["dependencyReason"] = reason,
                ["package"] = "com.unity.inputsystem",
                ["playMode"] = IsPlaying,
                ["requiresPlayModeForMutation"] = true,
                ["requiresAllowStateMutation"] = false,
                ["keyboard"] = DeviceRow("Keyboard", api?.Keyboard),
                ["mouse"] = DeviceRow("Mouse", api?.Mouse),
                ["touchscreen"] = DeviceRow("Touchscreen", api?.Touchscreen),
                ["pointerCapture"] = ChievfxMcpControlPointerCapture.Status(),
                ["pendingInputSequences"] = ChievfxMcpControlInputPlayback.PendingSequenceCount,
                ["gameView"] = GameViewStateRow(),
                ["tools"] = available
                    ? new[] { "editor-playmode-set", "input-control-keyboard-event", "input-control-keyboard-sequence", "input-control-mouse-event", "input-control-mouse-gesture", "input-control-touch-event", "input-control-pointer-capture" }
                    : new[] { "editor-playmode-set" },
                ["warnings"] = available ? Array.Empty<string>() : new[] { reason },
            };
        }

        private static Dictionary<string, object?> PlayModeSet(JToken args)
        {
            var errors = new List<string>();
            var stateToken = new[] { "isPlaying", "play", "playing", "enabled" }
                .Select(key => args[key])
                .FirstOrDefault(token => token?.Type == JTokenType.Boolean);
            var hasRequestedState = stateToken != null;
            if (!hasRequestedState)
            {
                errors.Add("editor-playmode-set requires isPlaying boolean (aliases play/playing are also accepted).");
            }

            var requested = hasRequestedState && stateToken!.Value<bool>();
            var before = IsPlaying;
            // Snapshot the event cursor BEFORE toggling play. Boot logs (e.g. Bootstrap.Awake Debug.Log)
            // fire during the play transition and get higher eventIds; waiting from this cursor catches them,
            // avoiding the cursor-after-op race where events-wait defaults to lastEventId and skips them.
            var eventCursorBefore = global::Chievfx.Mcp.Editor.ChievfxMcpBridgeHost.EventJournal.CurrentEventId();
            if (errors.Count == 0)
            {
                EditorApplication.isPlaying = requested;
            }

            var ok = errors.Count == 0;
            var result = new Dictionary<string, object?>
            {
                ["ok"] = ok,
                ["status"] = ok ? (before == requested ? "unchanged" : "requested") : "failed",
                ["requestedIsPlaying"] = hasRequestedState ? requested : null,
                ["isPlaying"] = IsPlaying,
                ["isPlayingOrWillChangePlaymode"] = EditorApplication.isPlayingOrWillChangePlaymode,
                // "Play is already on" read as "the game runs" while the editor stood paused.
                ["isPaused"] = EditorApplication.isPaused,
                ["eventCursorBefore"] = eventCursorBefore,
                ["eventCursorAfter"] = global::Chievfx.Mcp.Editor.ChievfxMcpBridgeHost.EventJournal.CurrentEventId(),
            };

            if (!ok)
            {
                result["validationErrors"] = errors.ToArray();
            }

            return result;
        }

        private static Dictionary<string, object?> MutationGateRow(bool dryRun)
        {
            return new Dictionary<string, object?> { ["requiresPlayMode"] = true, ["playMode"] = IsPlaying, ["dryRun"] = dryRun };
        }

        private static Dictionary<string, object?> GameViewStateRow()
        {
            return new Dictionary<string, object?>
            {
                ["focused"] = IsGameViewFocused(),
                ["applicationFocused"] = UnityEditorInternal.InternalEditorUtility.isApplicationActive,
                ["inputRouting"] = ChievfxMcpControlPointerCapture.CurrentRoutingBehavior(),
                ["inputRoutingOverridden"] = ChievfxMcpControlPointerCapture.RoutingOverridden,
            };
        }

        private static bool IsGameViewFocused()
        {
            for (var type = EditorWindow.focusedWindow?.GetType(); type != null; type = type.BaseType)
            {
                if (string.Equals(type.FullName, "UnityEditor.GameView", StringComparison.Ordinal)
                    || string.Equals(type.FullName, "UnityEditor.PlayModeView", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static Dictionary<string, object?> CoordinateConventionRow()
        {
            return new Dictionary<string, object?> { ["origin"] = "bottom-left", ["unit"] = "screen-pixels", ["xAxis"] = "right", ["yAxis"] = "up" };
        }

        private static void Gate(bool dryRun, List<string> errors)
        {
            if (dryRun) return;
            if (!IsPlaying) errors.Add("Real input injection requires Play Mode. Set dryRun=true to preview outside Play Mode.");
        }

        // Synthetic UI dispatch is a FALLBACK for scenes without an active InputSystemUIInputModule
        // (e.g. UI Toolkit-only games with no EventSystem). When the module is present, injected
        // device state already drives uGUI and UI Toolkit through the regular pointer pipeline;
        // dispatching synthetically as well would double-deliver every click.

        private static void TryDispatchUiRuntimeClick(Vector2 screenPosition, List<string> warnings)
        {
            if (HasActiveInputSystemUiModule())
            {
                return;
            }

            try
            {
                Chievfx.Mcp.Editor.ChievfxMcpFirstPartyExtensionLoader.EnsureLoaded();
                Chievfx.Mcp.Editor.ChievfxMcpRuntimeUiAdapterRegistry.RuntimeClick(UiRuntimeClickArgs(screenPosition));
            }
            catch (Exception ex)
            {
                warnings.Add("Runtime UI click dispatch skipped: " + RootMessage(ex));
            }
        }

        private static JObject UiRuntimeClickArgs(Vector2 screenPosition)
        {
            return new JObject
            {
                ["framework"] = "all",
                ["x"] = screenPosition.x,
                ["y"] = screenPosition.y,
            };
        }

        private static void TryDispatchUiPointerDrag(Vector2 screenStartPosition, Vector2 screenDelta, int steps, List<string> warnings)
        {
            if (HasActiveInputSystemUiModule())
            {
                return;
            }

            try
            {
                Chievfx.Mcp.Editor.ChievfxMcpFirstPartyExtensionLoader.EnsureLoaded();
                Chievfx.Mcp.Editor.ChievfxMcpRuntimeUiAdapterRegistry.RuntimeDrag(UiRuntimeDragArgs(screenStartPosition, screenDelta, steps));
            }
            catch (Exception ex)
            {
                warnings.Add("Runtime UI pointer drag skipped: " + RootMessage(ex));
            }
        }

        private static bool HasActiveInputSystemUiModule()
        {
            try
            {
                var eventSystemType = Type.GetType("UnityEngine.EventSystems.EventSystem, UnityEngine.UI", throwOnError: false);
                var eventSystem = eventSystemType?.GetProperty("current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                var module = eventSystem?.GetType().GetProperty("currentInputModule", BindingFlags.Instance | BindingFlags.Public)?.GetValue(eventSystem);
                // Only the Input System UI module reacts to injected device state; the legacy
                // StandaloneInputModule polls UnityEngine.Input and never sees it, so the
                // synthetic fallback must still run in that case.
                return module != null && module.GetType().Name == "InputSystemUIInputModule";
            }
            catch
            {
                return false;
            }
        }

        private static JObject UiRuntimeDragArgs(Vector2 screenStartPosition, Vector2 screenDelta, int? steps)
        {
            var args = new JObject
            {
                ["framework"] = "all",
                ["x"] = screenStartPosition.x,
                ["y"] = screenStartPosition.y,
                ["deltaX"] = screenDelta.x,
                ["deltaY"] = screenDelta.y,
            };
            if (steps.HasValue)
            {
                args["steps"] = Mathf.Clamp(steps.Value, 1, 120);
            }

            return args;
        }

        private static string RootMessage(Exception ex)
        {
            while (ex is TargetInvocationException && ex.InnerException != null)
            {
                ex = ex.InnerException;
            }

            return string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
        }

        private static bool IsPlaying => playModeOverrideForTests ?? EditorApplication.isPlaying;

        private static Dictionary<string, object?> EventRow(string device, string action, string? target, Vector2? position, Vector2? delta, double time)
        {
            return new Dictionary<string, object?>
            {
                ["device"] = device,
                ["action"] = action,
                ["target"] = target,
                ["position"] = position.HasValue ? Vec(position.Value) : null,
                ["delta"] = delta.HasValue ? Vec(delta.Value) : null,
                ["time"] = time < 0d ? "default" : time.ToString("0.######", CultureInfo.InvariantCulture),
            };
        }

        private static Dictionary<string, object?> Vec(Vector2 v) => new() { ["x"] = v.x, ["y"] = v.y };

        private static Dictionary<string, object?> DeviceRow(string kind, object? device)
        {
            return new Dictionary<string, object?>
            {
                ["kind"] = kind,
                ["available"] = device != null,
                ["type"] = device?.GetType().FullName,
                ["displayName"] = device?.GetType().GetProperty("displayName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)?.GetValue(device)?.ToString(),
                ["name"] = device?.GetType().GetProperty("name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)?.GetValue(device)?.ToString(),
            };
        }

        private static float Ease(float t, string ease)
        {
            t = Mathf.Clamp01(t);
            return ease switch
            {
                "in" => t * t,
                "out" => 1f - (1f - t) * (1f - t),
                _ => t < 0.5f ? 2f * t * t : 1f - Mathf.Pow(-2f * t + 2f, 2f) / 2f,
            };
        }

        private static string TouchPhaseForAction(string action)
        {
            return action switch
            {
                "down" => "Began",
                "move" => "Moved",
                "up" => "Ended",
                _ => "Moved",
            };
        }

        private static string? ReadString(JToken token, string name) => token[name]?.Type == JTokenType.String ? token[name]!.Value<string>() : null;
        private static bool ReadBool(JToken token, string name, bool value) => token[name]?.Type == JTokenType.Boolean ? token[name]!.Value<bool>() : value;
        private static int ReadInt(JToken token, string name, int value) => token[name]?.Type == JTokenType.Integer ? token[name]!.Value<int>() : value;
        private static double ReadDouble(JToken token, string name, double value) => token[name]?.Type is JTokenType.Integer or JTokenType.Float ? token[name]!.Value<double>() : value;
        private static bool OneOf(string value, params string[] options) => options.Any(option => string.Equals(value, option, StringComparison.Ordinal));
        private static string Norm(string? value) => new((value ?? string.Empty).Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        private static bool TryVector(JToken token, string name, out Vector2 value)
        {
            value = default;
            if (token[name] is not JObject obj) return false;
            value = new Vector2(obj["x"]?.Value<float>() ?? 0f, obj["y"]?.Value<float>() ?? 0f);
            return obj["x"] != null || obj["y"] != null;
        }

        private static bool TryPosition(JToken args, out Vector2 position)
        {
            if (TryVector(args, "position", out position) || TryVector(args, "screenPosition", out position))
            {
                return true;
            }

            // Other UI tools take top-level x/y; accept the same shape here.
            if (args["x"]?.Type is JTokenType.Integer or JTokenType.Float
                && args["y"]?.Type is JTokenType.Integer or JTokenType.Float)
            {
                position = new Vector2(args["x"]!.Value<float>(), args["y"]!.Value<float>());
                return true;
            }

            return false;
        }

        private static string ReadAction(JToken args)
        {
            return Norm(ReadString(args, "action") ?? ReadString(args, "eventType") ?? ReadString(args, "type"));
        }

        private static JObject KeyboardSchema() => Schema(new JObject
        {
            ["action"] = Enum("Keyboard action. Required (alias: eventType).", "down", "up", "tap"),
            ["eventType"] = Enum("Alias for action.", "down", "up", "tap"),
            ["key"] = Str("Input System Key enum name."),
            ["durationMs"] = Num("Tap: hold the key at least this long (ms), as well as holdFrames."),
            ["holdFrames"] = Int("Player frames to hold the key during tap. Default 2, range 1..300."),
            ["awaitMs"] = AwaitMs(),
            ["dryRun"] = Bool("Report intended events without injecting them (default false: the input is real)."),
            ["allowStateMutation"] = Bool("Deprecated and optional; real injection is the default in Play Mode. Set dryRun:true to preview instead."),
        }, "key");

        private static JObject KeyboardSequenceSchema() => Schema(new JObject
        {
            ["text"] = Str("Text to type; each character becomes a key tap (US layout, shift auto-applied)."),
            ["keys"] = new JObject
            {
                ["type"] = "array",
                ["items"] = new JObject { ["type"] = "string" },
                ["description"] = "Key names to tap in order, e.g. [\"W\",\"W\",\"Space\"]. Digit/arrow aliases accepted.",
            },
            ["holdFrames"] = Int("Player frames to hold each key. Default 2."),
            ["gapFrames"] = Int("Player frames between keys. Default 2."),
            ["awaitMs"] = AwaitMs(),
            ["dryRun"] = Bool("Preview without injecting (default false)."),
            ["allowStateMutation"] = Bool("Deprecated no-op; real injection is the default."),
        });

        private static JObject MouseSchema() => Schema(new JObject
        {
            ["action"] = Enum("Mouse action. Required (alias: eventType).", "down", "up", "tap", "move"),
            ["eventType"] = Enum("Alias for action.", "down", "up", "tap", "move"),
            ["button"] = Enum("Mouse button.", "left", "right", "middle", "forward", "back"),
            ["position"] = Vector("Absolute screen position, origin bottom-left."),
            ["screenPosition"] = Vector("Alias for position."),
            ["x"] = Num("Alias for position.x (pair with y)."),
            ["y"] = Num("Alias for position.y (pair with x)."),
            ["delta"] = Vector("Relative mouse delta."),
            ["holdFrames"] = Int("Player frames to hold the button during tap. Default 2, range 1..300."),
            ["awaitMs"] = AwaitMs(),
            ["capturePointer"] = Bool("Route injection through a virtual mouse and disable physical mice so the OS cursor cannot overwrite injected positions. Default true; ends on Play Mode exit or input-control-pointer-capture end."),
            ["dryRun"] = Bool("Report intended events without injecting them (default false: the input is real)."),
            ["allowStateMutation"] = Bool("Deprecated and optional; real injection is the default in Play Mode. Set dryRun:true to preview instead."),
        });

        private static JObject GestureSchema() => Schema(new JObject
        {
            ["button"] = Enum("Mouse button.", "left", "right", "middle", "forward", "back"),
            ["startPosition"] = Vector("Start screen position."),
            ["position"] = Vector("Alias for startPosition."),
            ["screenPosition"] = Vector("Alias for startPosition."),
            ["x"] = Num("Alias for startPosition.x."),
            ["y"] = Num("Alias for startPosition.y."),
            ["delta"] = Vector("Total gesture delta. Provide this OR endPosition."),
            ["endPosition"] = Vector("End screen position; delta = end - start."),
            ["durationMs"] = Num("Gesture duration (ms)."),
            ["steps"] = Int("Interpolation steps, 1..240."),
            ["ease"] = Enum("Interpolation curve.", "inout", "in", "out"),
            ["includeDown"] = Bool("Press at the start (default true)."),
            ["includeUp"] = Bool("Release at the end (default true)."),
            ["awaitMs"] = AwaitMs(),
            ["capturePointer"] = Bool("Route through a virtual mouse so the OS cursor can't overwrite positions (default true)."),
            ["dryRun"] = Bool("Preview without injecting (default false)."),
            ["allowStateMutation"] = Bool("Deprecated no-op; real injection is the default."),
        });

        private static JObject TouchSchema() => Schema(new JObject
        {
            ["action"] = Enum("Touch action. Required (alias: eventType).", "down", "up", "tap", "move"),
            ["eventType"] = Enum("Alias for action.", "down", "up", "tap", "move"),
            ["touchId"] = Int("Touch identifier. Defaults to 1."),
            ["position"] = Vector("Absolute screen position, origin bottom-left."),
            ["screenPosition"] = Vector("Alias for position."),
            ["x"] = Num("Alias for position.x (pair with y)."),
            ["y"] = Num("Alias for position.y (pair with x)."),
            ["delta"] = Vector("Relative touch delta for move/up metadata."),
            ["holdFrames"] = Int("Player frames to hold the touch during tap. Default 2, range 1..300."),
            ["awaitMs"] = AwaitMs(),
            ["dryRun"] = Bool("Report intended events without injecting them (default false: the input is real)."),
            ["allowStateMutation"] = Bool("Deprecated and optional; real injection is the default in Play Mode. Set dryRun:true to preview instead."),
        });

        // Taken off the call by the ChievFX server, which waits for this tool's completionMarker and appends the end.
        private static JObject AwaitMs() => Int("Wait up to this many ms for the input to finish; the end comes in this reply.");

        private static JObject PointerCaptureSchema() => Schema(new JObject
        {
            ["action"] = Enum("Pointer capture action.", "begin", "end", "status"),
            ["allowStateMutation"] = Bool("Deprecated and optional; no longer required for begin."),
        }, "action");

        private static JObject ShaderStatusSchema() => Schema(new JObject
        {
            ["path"] = Str("Asset path of a .shader or a material to inspect. Omit to scan renderers in the open scenes."),
            ["includeWarnings"] = Bool("Include warning-severity shader messages too (default false: errors only)."),
            ["maxMessages"] = Int("Max messages per shader (default 10)."),
        });
    }
}
