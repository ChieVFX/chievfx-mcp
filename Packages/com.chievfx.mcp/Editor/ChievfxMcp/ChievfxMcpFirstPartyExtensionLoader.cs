#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
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
            return AppDomain.CurrentDomain.GetAssemblies()
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
    internal static class ChievfxMcpControlExtension
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
                descriptor.Tools.Add(Tool("input-control-mouse-gesture", "Queue a mouse down/move/up gesture whose steps dispatch one per player frame. Defaults dryRun=true; real runs return a completionMarker for events-wait.", GestureSchema()));
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
                ["eventCursorBefore"] = eventCursorBefore,
                ["eventCursorAfter"] = global::Chievfx.Mcp.Editor.ChievfxMcpBridgeHost.EventJournal.CurrentEventId(),
            };

            if (!ok)
            {
                result["validationErrors"] = errors.ToArray();
            }

            return result;
        }

        private static Dictionary<string, object?> Keyboard(JToken args, InputApi api)
        {
            var action = ReadAction(args);
            var keyName = ReadString(args, "key") ?? ReadString(args, "targetKey") ?? string.Empty;
            var dryRun = ResolveDryRun(args);
            var holdFrames = ReadHoldFrames(args);
            var errors = new List<string>();
            var warnings = new List<string>();
            var queued = new List<object>();
            if (!OneOf(action, "down", "up", "tap")) errors.Add("action must be one of: down, up, tap.");
            if (api.Keyboard == null) errors.Add("Keyboard.current is null; no keyboard device is available.");
            if (!api.TryKey(keyName, out var key, out var keyError)) errors.Add(keyError);
            object? control = null;
            if (errors.Count == 0 && !api.TryKeyboardControl(key!, out control, out var controlError)) errors.Add(controlError);
            Gate(dryRun, errors);
            string? completionMarker = null;
            if (errors.Count == 0)
            {
                foreach (var item in action == "tap" ? new[] { "down", "up" } : new[] { action })
                {
                    queued.Add(EventRow("Keyboard", item, keyName, null, null, -1d));
                }

                if (!dryRun)
                {
                    ChievfxMcpControlPointerCapture.EnsureInputRoutingOverride();
                    if (action == "tap")
                    {
                        completionMarker = ChievfxMcpControlInputPlayback.Schedule("keyboard-tap", new[]
                        {
                            new ChievfxMcpControlInputPlayback.Step { FrameGapBefore = 0, Dispatch = () => api.QueueKeyboardKey(key!, true, -1d) },
                            new ChievfxMcpControlInputPlayback.Step { FrameGapBefore = holdFrames, Dispatch = () => api.QueueKeyboardKey(key!, false, -1d) },
                        });
                    }
                    else
                    {
                        api.QueueKeyboardKey(key!, action == "down", -1d);
                    }
                }
            }

            var mutated = !dryRun && errors.Count == 0;
            var result = Result("input-control-keyboard-event", "Keyboard", action, dryRun, mutated, queued.ToArray(), warnings.ToArray(), errors.ToArray());
            return WithScheduling(result, completionMarker, mutated);
        }

        // Batch keyboard input in ONE call: type a string or tap a key list, scheduled across player
        // frames (holdFrames per key, gapFrames between) so a real-time game sees each edge in Update().
        private static Dictionary<string, object?> KeyboardSequence(JToken args, InputApi api)
        {
            var dryRun = ResolveDryRun(args);
            var holdFrames = ReadHoldFrames(args);
            var gapFrames = Mathf.Clamp(ReadInt(args, "gapFrames", 2), 0, 300);
            var errors = new List<string>();
            var warnings = new List<string>();
            var queued = new List<object>();

            // Ordered (key name, needs-shift) list from either text or an explicit key list.
            var tokens = new List<(string name, bool shift)>();
            var text = ReadString(args, "text");
            var keysArray = args["keys"] as JArray;
            if (!string.IsNullOrEmpty(text))
            {
                foreach (var ch in text!)
                {
                    if (TryMapChar(ch, out var mappedKey, out var mappedShift))
                    {
                        tokens.Add((mappedKey, mappedShift));
                    }
                    else
                    {
                        warnings.Add($"Skipped unmapped character '{ch}'.");
                    }
                }
            }
            else if (keysArray != null && keysArray.Count > 0)
            {
                foreach (var item in keysArray)
                {
                    var name = item?.Type == JTokenType.String ? item.Value<string>() : null;
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        tokens.Add((name!, false));
                    }
                }
            }
            else
            {
                errors.Add("keyboard sequence requires text (a string) or keys (an array of key names).");
            }

            if (api.Keyboard == null) errors.Add("Keyboard.current is null; no keyboard device is available.");

            var resolved = new List<(object key, string name, bool shift)>();
            foreach (var (name, shift) in tokens)
            {
                if (!api.TryKey(name, out var key, out var keyError))
                {
                    errors.Add(keyError);
                    continue;
                }

                resolved.Add((key!, name, shift));
            }

            Gate(dryRun, errors);
            foreach (var entry in resolved)
            {
                queued.Add(EventRow("Keyboard", "tap", entry.name, null, null, -1d));
            }

            string? completionMarker = null;
            if (errors.Count == 0 && !dryRun && resolved.Count > 0)
            {
                ChievfxMcpControlPointerCapture.EnsureInputRoutingOverride();
                api.TryKey("LeftShift", out var shiftKey, out _);
                var playback = new List<ChievfxMcpControlInputPlayback.Step>();
                var first = true;
                foreach (var entry in resolved)
                {
                    var item = entry;
                    playback.Add(new ChievfxMcpControlInputPlayback.Step
                    {
                        FrameGapBefore = first ? 0 : gapFrames,
                        Dispatch = () =>
                        {
                            if (item.shift && shiftKey != null) api.QueueKeyboardKey(shiftKey, true, -1d);
                            api.QueueKeyboardKey(item.key, true, -1d);
                        },
                    });
                    playback.Add(new ChievfxMcpControlInputPlayback.Step
                    {
                        FrameGapBefore = holdFrames,
                        Dispatch = () =>
                        {
                            api.QueueKeyboardKey(item.key, false, -1d);
                            if (item.shift && shiftKey != null) api.QueueKeyboardKey(shiftKey, false, -1d);
                        },
                    });
                    first = false;
                }

                completionMarker = ChievfxMcpControlInputPlayback.Schedule("keyboard-sequence", playback);
            }

            var mutated = !dryRun && errors.Count == 0;
            var result = Result("input-control-keyboard-sequence", "Keyboard", "sequence", dryRun, mutated, queued.ToArray(), warnings.ToArray(), errors.ToArray());
            result["keyCount"] = resolved.Count;
            return WithScheduling(result, completionMarker, mutated);
        }

        // US-layout char -> Input System Key name (+ shift). Best-effort; unmapped chars are skipped.
        private static bool TryMapChar(char c, out string keyName, out bool shift)
        {
            shift = false;
            keyName = string.Empty;
            if (c >= 'a' && c <= 'z') { keyName = char.ToUpperInvariant(c).ToString(); return true; }
            if (c >= 'A' && c <= 'Z') { keyName = c.ToString(); shift = true; return true; }
            if (c >= '0' && c <= '9') { keyName = "Digit" + c; return true; }
            switch (c)
            {
                case ' ': keyName = "Space"; return true;
                case '\n': keyName = "Enter"; return true;
                case '\t': keyName = "Tab"; return true;
                case '-': keyName = "Minus"; return true;
                case '=': keyName = "Equals"; return true;
                case '[': keyName = "LeftBracket"; return true;
                case ']': keyName = "RightBracket"; return true;
                case '\\': keyName = "Backslash"; return true;
                case ';': keyName = "Semicolon"; return true;
                case '\'': keyName = "Quote"; return true;
                case ',': keyName = "Comma"; return true;
                case '.': keyName = "Period"; return true;
                case '/': keyName = "Slash"; return true;
                case '`': keyName = "Backquote"; return true;
                case '!': keyName = "Digit1"; shift = true; return true;
                case '@': keyName = "Digit2"; shift = true; return true;
                case '#': keyName = "Digit3"; shift = true; return true;
                case '$': keyName = "Digit4"; shift = true; return true;
                case '%': keyName = "Digit5"; shift = true; return true;
                case '^': keyName = "Digit6"; shift = true; return true;
                case '&': keyName = "Digit7"; shift = true; return true;
                case '*': keyName = "Digit8"; shift = true; return true;
                case '(': keyName = "Digit9"; shift = true; return true;
                case ')': keyName = "Digit0"; shift = true; return true;
                case '_': keyName = "Minus"; shift = true; return true;
                case '+': keyName = "Equals"; shift = true; return true;
                case '{': keyName = "LeftBracket"; shift = true; return true;
                case '}': keyName = "RightBracket"; shift = true; return true;
                case '|': keyName = "Backslash"; shift = true; return true;
                case ':': keyName = "Semicolon"; shift = true; return true;
                case '"': keyName = "Quote"; shift = true; return true;
                case '<': keyName = "Comma"; shift = true; return true;
                case '>': keyName = "Period"; shift = true; return true;
                case '?': keyName = "Slash"; shift = true; return true;
                case '~': keyName = "Backquote"; shift = true; return true;
                default: return false;
            }
        }

        private static Dictionary<string, object?> Mouse(JToken args, InputApi api)
        {
            var action = ReadAction(args);
            var dryRun = ResolveDryRun(args);
            var capturePointer = ReadBool(args, "capturePointer", true);
            var holdFrames = ReadHoldFrames(args);
            var errors = new List<string>();
            var warnings = new List<string>();
            var queued = new List<object>();
            Vector2? uiToolkitClickPosition = null;
            string? completionMarker = null;
            if (!OneOf(action, "down", "up", "tap", "move")) errors.Add("action must be one of: down, up, tap, move.");
            if (api.Mouse == null) errors.Add("Mouse.current is null; no mouse device is available.");

            if (action == "move")
            {
                var hasPosition = TryPosition(args, out var position);
                var hasDelta = TryVector(args, "delta", out var delta);
                if (!hasPosition && !hasDelta) errors.Add("move action requires position/screenPosition (an {x,y} object), top-level x and y numbers, or delta.");
                if (hasPosition && hasDelta) warnings.Add("Both position and delta were provided; queued absolute position and reported delta for caller context.");
                object? control = null;
                if (errors.Count == 0 && !api.TryMouseControl(hasPosition ? "position" : "delta", out control, out var controlError)) errors.Add(controlError);
                Gate(dryRun, errors);
                if (errors.Count == 0)
                {
                    queued.Add(EventRow("Mouse", "move", null, hasPosition ? position : null, hasDelta ? delta : null, -1d));
                    if (!dryRun)
                    {
                        BeginPointerSession(api, capturePointer, warnings);
                        api.QueueMouseMove(hasPosition ? position : null, hasDelta ? delta : null, -1d);
                    }
                }
            }
            else
            {
                var button = ReadString(args, "button") ?? "left";
                var hasPosition = TryPosition(args, out var position);
                object? control = null;
                if (errors.Count == 0 && !api.TryMouseButton(button, out control, out var buttonError)) errors.Add(buttonError);
                if (errors.Count == 0 && hasPosition && !api.TryMouseControl("position", out _, out var positionError)) errors.Add(positionError);
                Gate(dryRun, errors);
                if (errors.Count == 0)
                {
                    if (hasPosition)
                    {
                        queued.Add(EventRow("Mouse", "move", null, position, null, -1d));
                    }

                    foreach (var item in action == "tap" ? new[] { "down", "up" } : new[] { action })
                    {
                        queued.Add(EventRow("Mouse", item, button, null, null, -1d));
                    }

                    if (!dryRun)
                    {
                        BeginPointerSession(api, capturePointer, warnings);
                        var positionOverride = hasPosition ? position : (Vector2?)null;
                        if (action == "tap")
                        {
                            completionMarker = ChievfxMcpControlInputPlayback.Schedule("mouse-tap", new[]
                            {
                                new ChievfxMcpControlInputPlayback.Step
                                {
                                    FrameGapBefore = 0,
                                    Dispatch = () =>
                                    {
                                        if (hasPosition) api.QueueMouseMove(position, null, -1d);
                                        api.QueueMouseButton(button, true, -1d, positionOverride);
                                    },
                                },
                                new ChievfxMcpControlInputPlayback.Step
                                {
                                    FrameGapBefore = holdFrames,
                                    Dispatch = () =>
                                    {
                                        api.QueueMouseButton(button, false, -1d, positionOverride);
                                        DispatchUiRuntimeClickToJournal(hasPosition ? position : api.ReadMousePosition());
                                    },
                                },
                            });
                        }
                        else
                        {
                            if (hasPosition) api.QueueMouseMove(position, null, -1d);
                            api.QueueMouseButton(button, action == "down", -1d, positionOverride);
                            if (action == "up")
                            {
                                if (hasPosition)
                                {
                                    uiToolkitClickPosition = position;
                                }
                                else
                                {
                                    // The release position lives in the player state buffers; read
                                    // it there once the queued release has been applied.
                                    ChievfxMcpControlInputPlayback.RunInInputUpdate(() => DispatchUiRuntimeClickToJournal(api.ReadMousePosition()));
                                }
                            }
                        }
                    }
                }
            }

            var mutated = !dryRun && errors.Count == 0;
            if (mutated && uiToolkitClickPosition.HasValue)
            {
                TryDispatchUiRuntimeClick(uiToolkitClickPosition.Value, warnings);
            }

            var result = Result("input-control-mouse-event", "Mouse", action, dryRun, mutated, queued.ToArray(), warnings.ToArray(), errors.ToArray());
            return WithScheduling(result, completionMarker, mutated);
        }

        private static Dictionary<string, object?> Gesture(JToken args, InputApi api)
        {
            var dryRun = ResolveDryRun(args);
            var errors = new List<string>();
            var queued = new List<object>();
            var hasStart = TryPosition(args, out var start) || TryVector(args, "startPosition", out start);
            var hasDelta = TryVector(args, "delta", out var delta);
            // Accept endPosition as an alternative to delta: derive delta = end - start. This is the
            // more intuitive way to express a drag and matches how positions are given elsewhere.
            if (!hasDelta && TryVector(args, "endPosition", out var end))
            {
                if (!hasStart)
                {
                    errors.Add("endPosition requires startPosition (or position) to derive the gesture delta.");
                }
                else
                {
                    delta = end - start;
                    hasDelta = true;
                }
            }

            if (!hasDelta)
            {
                errors.Add("mouse gesture requires delta (or startPosition + endPosition).");
            }

            var durationMs = ReadDouble(args, "durationMs", 250d);
            var ease = Norm(ReadString(args, "ease") ?? "inout");
            var steps = ReadInt(args, "steps", 0);
            steps = steps > 0 ? steps : Mathf.Clamp(Mathf.CeilToInt((float)Math.Max(durationMs, 16d) / 16f), 1, 120);
            if (!OneOf(ease, "inout", "in", "out")) errors.Add("ease must be one of: inout, in, out.");
            if (durationMs < 0d || durationMs > 60000d) errors.Add("durationMs must be between 0 and 60000.");
            if (steps < 1 || steps > 240) errors.Add("steps must be between 1 and 240.");
            if (api.Mouse == null) errors.Add("Mouse.current is null; no mouse device is available.");
            var button = ReadString(args, "button") ?? "left";
            var includeDown = ReadBool(args, "includeDown", true);
            var includeUp = ReadBool(args, "includeUp", true);
            object? buttonControl = null;
            object? positionControl = null;
            object? deltaControl = null;
            if (errors.Count == 0 && (includeDown || includeUp) && !api.TryMouseButton(button, out buttonControl, out var buttonError)) errors.Add(buttonError);
            if (errors.Count == 0 && hasStart && !api.TryMouseControl("position", out positionControl, out var positionError)) errors.Add(positionError);
            if (errors.Count == 0 && !api.TryMouseControl("delta", out deltaControl, out var deltaError)) errors.Add(deltaError);
            Gate(dryRun, errors);
            var warnings = new List<string>();
            string? completionMarker = null;
            if (errors.Count == 0)
            {
                var capturePointer = ReadBool(args, "capturePointer", true);
                var time = api.Time;
                var playback = new List<ChievfxMcpControlInputPlayback.Step>();
                if (includeDown)
                {
                    queued.Add(EventRow("Mouse", "down", button, null, null, time));
                    playback.Add(new ChievfxMcpControlInputPlayback.Step
                    {
                        FrameGapBefore = 0,
                        Dispatch = () =>
                        {
                            if (hasStart) api.QueueMouseMove(start, null, -1d);
                            api.QueueMouseButton(button, true, -1d, hasStart ? start : (Vector2?)null);
                        },
                    });
                }

                var previous = Vector2.zero;
                for (var i = 1; i <= steps; i++)
                {
                    var t = (float)i / steps;
                    var current = delta * Ease(t, ease);
                    var frameDelta = current - previous;
                    previous = current;
                    var eventTime = time + (durationMs / 1000d) * t;
                    var framePosition = hasStart ? start + current : (Vector2?)null;
                    queued.Add(EventRow("Mouse", "move", null, framePosition, frameDelta, eventTime));
                    playback.Add(new ChievfxMcpControlInputPlayback.Step
                    {
                        FrameGapBefore = 1,
                        Dispatch = () => api.QueueMouseMove(framePosition, frameDelta, -1d),
                    });
                }

                if (includeUp)
                {
                    var upTime = time + durationMs / 1000d;
                    queued.Add(EventRow("Mouse", "up", button, null, null, upTime));
                    var stepCount = steps;
                    playback.Add(new ChievfxMcpControlInputPlayback.Step
                    {
                        FrameGapBefore = 2,
                        Dispatch = () =>
                        {
                            api.QueueMouseButton(button, false, -1d, hasStart ? start + delta : (Vector2?)null);
                            if (hasStart && includeDown)
                            {
                                DispatchUiPointerDragToJournal(start, delta, stepCount);
                            }
                        },
                    });
                }

                if (!dryRun)
                {
                    BeginPointerSession(api, capturePointer, warnings);
                    completionMarker = ChievfxMcpControlInputPlayback.Schedule("mouse-gesture", playback);
                }
            }

            var mutated = !dryRun && errors.Count == 0;
            var result = Result("input-control-mouse-gesture", "Mouse", "gesture", dryRun, mutated, queued.ToArray(), warnings.ToArray(), errors.ToArray());
            if (dryRun || errors.Count > 0)
            {
                result["durationMs"] = durationMs;
                result["steps"] = steps;
                result["ease"] = ease;
            }

            return WithScheduling(result, completionMarker, mutated);
        }

        private static Dictionary<string, object?> Touch(JToken args, InputApi api)
        {
            var action = ReadAction(args);
            var dryRun = ResolveDryRun(args);
            var errors = new List<string>();
            var warnings = new List<string>();
            var queued = new List<object>();
            Vector2? uiToolkitClickPosition = null;
            if (!OneOf(action, "down", "up", "tap", "move")) errors.Add("action must be one of: down, up, tap, move.");
            if (api.Touchscreen == null) errors.Add("Touchscreen.current is null; no touchscreen device is available.");
            var touchId = ReadInt(args, "touchId", 1);
            if (touchId < 1) errors.Add("touchId must be greater than or equal to 1.");
            var hasPosition = TryPosition(args, out var position);
            var hasDelta = TryVector(args, "delta", out var delta);
            if (!hasPosition && !string.Equals(action, "up", StringComparison.Ordinal))
            {
                errors.Add("touch down, tap, and move actions require position/screenPosition (an {x,y} object) or top-level x and y numbers.");
            }

            if (hasDelta && (action == "down" || action == "tap"))
            {
                warnings.Add("delta is ignored for touch down/tap start events; position is the authoritative touchscreen coordinate.");
            }

            object? primaryTouch = null;
            if (errors.Count == 0 && !api.TryTouchControl("primaryTouch", out primaryTouch, out var controlError))
            {
                errors.Add(controlError);
            }

            Gate(dryRun, errors);
            string? completionMarker = null;
            if (errors.Count == 0)
            {
                var holdFrames = ReadHoldFrames(args);
                var resolvedPosition = hasPosition ? position : api.ReadPrimaryTouchPosition();
                foreach (var item in action == "tap"
                    ? new[] { ("down", Vector2.zero), ("up", Vector2.zero) }
                    : new[] { (action, hasDelta ? delta : Vector2.zero) })
                {
                    queued.Add(EventRow("Touchscreen", item.Item1, touchId.ToString(CultureInfo.InvariantCulture), resolvedPosition, item.Item2, -1d));
                }

                if (!dryRun)
                {
                    ChievfxMcpControlPointerCapture.EnsureInputRoutingOverride();
                    if (action == "tap")
                    {
                        completionMarker = ChievfxMcpControlInputPlayback.Schedule("touch-tap", new[]
                        {
                            new ChievfxMcpControlInputPlayback.Step
                            {
                                FrameGapBefore = 0,
                                Dispatch = () => api.QueueTouch(touchId, "Began", resolvedPosition, Vector2.zero, -1d),
                            },
                            new ChievfxMcpControlInputPlayback.Step
                            {
                                FrameGapBefore = holdFrames,
                                Dispatch = () =>
                                {
                                    api.QueueTouch(touchId, "Ended", resolvedPosition, Vector2.zero, -1d);
                                    DispatchUiRuntimeClickToJournal(resolvedPosition);
                                },
                            },
                        });
                    }
                    else
                    {
                        api.QueueTouch(touchId, TouchPhaseForAction(action), hasPosition ? position : (Vector2?)null, hasDelta ? delta : Vector2.zero, -1d);
                        if (action == "up")
                        {
                            if (hasPosition)
                            {
                                uiToolkitClickPosition = position;
                            }
                            else
                            {
                                // The release position lives in the player state buffers; read it
                                // there once the queued release has been applied.
                                ChievfxMcpControlInputPlayback.RunInInputUpdate(() => DispatchUiRuntimeClickToJournal(api.ReadPrimaryTouchPosition()));
                            }
                        }
                    }
                }
            }

            var mutated = !dryRun && errors.Count == 0;
            if (mutated && uiToolkitClickPosition.HasValue)
            {
                TryDispatchUiRuntimeClick(uiToolkitClickPosition.Value, warnings);
            }
            else if (mutated && action == "move" && hasPosition && hasDelta)
            {
                TryDispatchUiPointerDrag(position - delta, delta, 12, warnings);
            }

            var result = Result("input-control-touch-event", "Touchscreen", action, dryRun, mutated, queued.ToArray(), warnings.ToArray(), errors.ToArray());
            result["touchId"] = touchId;
            return WithScheduling(result, completionMarker, mutated);
        }

        private static Dictionary<string, object?> PointerCapture(JToken args, InputApi api)
        {
            var action = Norm(ReadString(args, "action"));
            var errors = new List<string>();
            var warnings = new List<string>();
            if (!OneOf(action, "begin", "end", "status")) errors.Add("action must be one of: begin, end, status.");

            if (errors.Count == 0 && action == "begin")
            {
                Gate(dryRun: false, errors);
                if (errors.Count == 0)
                {
                    if (!ChievfxMcpControlPointerCapture.TryBegin(out var created, out var seedPosition, out var captureError))
                    {
                        errors.Add(captureError);
                    }
                    else if (created)
                    {
                        // Seed the virtual mouse with the physical cursor position so game code
                        // reading Mouse.current does not observe a jump to (0, 0).
                        api.QueueMouseMove(seedPosition, Vector2.zero, -1d);
                    }
                }
            }
            else if (errors.Count == 0 && action == "end")
            {
                if (!ChievfxMcpControlPointerCapture.EndSession(out var endError))
                {
                    warnings.Add(endError);
                }
            }

            var ok = errors.Count == 0;
            var result = new Dictionary<string, object?>
            {
                ["ok"] = ok,
                ["status"] = ok ? "success" : "failed",
                ["action"] = action,
                ["pointerCapture"] = ChievfxMcpControlPointerCapture.Status(),
            };
            if (warnings.Count > 0) result["warnings"] = warnings.ToArray();
            if (!ok)
            {
                result["validationErrors"] = errors.ToArray();
                result["playMode"] = IsPlaying;
                result["mutationGate"] = MutationGateRow(dryRun: false);
            }

            return result;
        }

        private static int ReadHoldFrames(JToken args)
        {
            return Mathf.Clamp(ReadInt(args, "holdFrames", 2), 1, 300);
        }

        // Real injection is the default (calling an input tool means you want the input). Explicit
        // dryRun wins; the legacy allowStateMutation:false still forces a preview for back-compat.
        // Injection is gated to Play Mode by Gate(), so there is no accidental edit-mode mutation.
        private static bool ResolveDryRun(JToken args)
        {
            if (args["dryRun"]?.Type == JTokenType.Boolean)
            {
                return args["dryRun"]!.Value<bool>();
            }

            if (args["allowStateMutation"]?.Type == JTokenType.Boolean && !args["allowStateMutation"]!.Value<bool>())
            {
                return true;
            }

            return false;
        }

        private static Dictionary<string, object?> WithScheduling(Dictionary<string, object?> result, string? completionMarker, bool mutated)
        {
            if (completionMarker == null || !mutated)
            {
                return result;
            }

            // The sequence dispatches on later player frames, so the completion event can fire before the
            // caller arms events-wait. Return the pre-dispatch cursor so a wait/check can use it as
            // sinceEventId and still catch an already-finished sequence.
            var eventCursorBefore = global::Chievfx.Mcp.Editor.ChievfxMcpBridgeHost.EventJournal.CurrentEventId();
            result["status"] = "scheduled";
            result["completionMarker"] = completionMarker;
            result["eventCursorBefore"] = eventCursorBefore;
            result["hint"] = "Await: events-wait marker=" + completionMarker + " sinceEventId=" + eventCursorBefore;
            return result;
        }

        private static void BeginPointerSession(InputApi api, bool capturePointer, List<string> warnings)
        {
            ChievfxMcpControlPointerCapture.EnsureInputRoutingOverride();
            if (!capturePointer)
            {
                return;
            }

            if (!ChievfxMcpControlPointerCapture.TryBegin(out var created, out var seedPosition, out var captureError))
            {
                if (!string.IsNullOrEmpty(captureError))
                {
                    warnings.Add("Pointer capture unavailable: " + captureError + " The OS cursor may overwrite injected positions each frame.");
                }

                return;
            }

            if (created)
            {
                api.QueueMouseMove(seedPosition, Vector2.zero, -1d);
            }
        }

        private static void DispatchUiRuntimeClickToJournal(Vector2 screenPosition)
        {
            var warnings = new List<string>();
            TryDispatchUiRuntimeClick(screenPosition, warnings);
            foreach (var warning in warnings)
            {
                ChievfxMcpControlInputPlayback.Journal("dispatch-warning", "warning", warning);
            }
        }

        private static void DispatchUiPointerDragToJournal(Vector2 screenStartPosition, Vector2 screenDelta, int steps)
        {
            var warnings = new List<string>();
            TryDispatchUiPointerDrag(screenStartPosition, screenDelta, steps, warnings);
            foreach (var warning in warnings)
            {
                ChievfxMcpControlInputPlayback.Journal("dispatch-warning", "warning", warning);
            }
        }

        // Long gestures queue dozens of near-identical move events; dumping them all is noise. Show the
        // full list only when small, otherwise summarize to count + first + last.
        private static object SummarizeQueuedEvents(object[] queued)
        {
            const int maxInline = 8;
            if (queued.Length <= maxInline)
            {
                return queued;
            }

            return new Dictionary<string, object?>
            {
                ["count"] = queued.Length,
                ["first"] = queued[0],
                ["last"] = queued[queued.Length - 1],
                ["note"] = $"{queued.Length} events queued; showing first and last only.",
            };
        }

        private static Dictionary<string, object?> Result(string tool, string? device, string? action, bool dryRun, bool mutated, object[] queued, string[] warnings, string[] errors)
        {
            var ok = errors.Length == 0;
            var result = new Dictionary<string, object?>
            {
                ["ok"] = ok,
                ["status"] = ok ? (dryRun ? "dry-run" : "success") : "failed",
                ["device"] = device,
                ["action"] = action,
            };

            if (dryRun)
            {
                result["queuedEvents"] = SummarizeQueuedEvents(queued);
                result["queuedEventCount"] = queued.Length;
                result["dryRun"] = true;
                result["mutated"] = mutated;
                result["playMode"] = IsPlaying;
                result["mutationGate"] = MutationGateRow(dryRun);
                result["coordinateConvention"] = CoordinateConventionRow();
            }

            if (!dryRun)
            {
                // Injected input is only consumed on player-loop frames. Paused Play Mode produces none,
                // so the events would sit unconsumed until the watchdog drops them — say so now rather
                // than after a wait that looks like a hang.
                if (mutated && EditorApplication.isPaused)
                {
                    warnings = warnings
                        .Append("Play Mode is paused, so no player frames advance and injected input cannot be consumed. Resume Play Mode (editor-playmode-set), then retry; the queued input is dropped shortly with an 'input-stalled'/'sequence-stalled' event.")
                        .ToArray();
                }

                // Only surface the game-view state when it is actually a problem; on the happy path it is
                // pure noise. When injected input may be muted, warn AND include the state for context.
                var gameView = GameViewStateRow();
                if (mutated && Equals(gameView["focused"], false) && !Equals(gameView["inputRoutingOverridden"], true))
                {
                    warnings = warnings
                        .Append("Game view is not focused and the input routing override is inactive; the editor may mute injected pointer/keyboard events. Focus the Game view (editor-window-focus) before injecting.")
                        .ToArray();
                    result["gameView"] = gameView;
                }
            }

            if (warnings.Length > 0)
            {
                result["warnings"] = warnings;
            }

            if (!ok)
            {
                result["tool"] = tool;
                result["queuedEvents"] = SummarizeQueuedEvents(queued);
                result["queuedEventCount"] = queued.Length;
                result["dryRun"] = dryRun;
                result["mutated"] = mutated;
                result["playMode"] = IsPlaying;
                result["validationErrors"] = errors;
                result["dependency"] = new Dictionary<string, object?> { ["package"] = "com.unity.inputsystem", ["available"] = InputApi.TryCreate(out _, out var reason), ["reason"] = reason };
                result["mutationGate"] = MutationGateRow(dryRun);
                result["coordinateConvention"] = CoordinateConventionRow();
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
            ["durationMs"] = Num("Optional metadata; tap queues down then up."),
            ["holdFrames"] = Int("Player frames to hold the key during tap. Default 2, range 1..300."),
            ["dryRun"] = Bool("Report intended events without input mutation. Defaults to !allowStateMutation, so allowStateMutation=true alone performs a real run."),
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
            ["capturePointer"] = Bool("Route injection through a virtual mouse and disable physical mice so the OS cursor cannot overwrite injected positions. Default true; ends on Play Mode exit or input-control-pointer-capture end."),
            ["dryRun"] = Bool("Report intended events without input mutation. Defaults to !allowStateMutation, so allowStateMutation=true alone performs a real run."),
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
            ["dryRun"] = Bool("Report intended events without input mutation. Defaults to !allowStateMutation, so allowStateMutation=true alone performs a real run."),
            ["allowStateMutation"] = Bool("Deprecated and optional; real injection is the default in Play Mode. Set dryRun:true to preview instead."),
        });

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

        // Magenta (error/missing shader) and cyan (variant still compiling) are the two most common
        // rendering symptoms, and both previously needed a hand-written ShaderUtil probe to diagnose.
        private static Dictionary<string, object?> ShaderStatus(JToken args)
        {
            var path = ReadString(args, "path");
            var includeWarnings = ReadBool(args, "includeWarnings", false);
            var maxMessages = Math.Clamp(ReadInt(args, "maxMessages", 10), 1, 100);

            var result = new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["compiling"] = UnityEditor.ShaderUtil.anythingCompiling,
            };

            if (UnityEditor.ShaderUtil.anythingCompiling)
            {
                result["hint"] = "Shader variants are still compiling — objects can render cyan/placeholder until they finish. Re-check before trusting a screenshot.";
            }

            var shaders = new List<(Shader Shader, string Origin)>();
            var missing = new List<Dictionary<string, object?>>();

            if (!string.IsNullOrWhiteSpace(path))
            {
                CollectShadersFromAssetPath(path!, shaders, result);
            }
            else
            {
                CollectShadersFromOpenScenes(shaders, missing);
                result["scanned"] = "open-scene renderers";
            }

            var rows = new List<Dictionary<string, object?>>();
            foreach (var (shader, origin) in shaders
                         .GroupBy(entry => entry.Shader, entry => entry.Origin)
                         .Select(group => (Shader: group.Key, Origin: string.Join(", ", group.Distinct().Take(3)))))
            {
                if (shader == null)
                {
                    continue;
                }

                var messages = ReadShaderMessages(shader, includeWarnings, maxMessages, out var errorCount, out var warningCount);
                if (errorCount == 0 && messages.Count == 0)
                {
                    continue;
                }

                rows.Add(new Dictionary<string, object?>
                {
                    ["shader"] = shader.name,
                    ["assetPath"] = AssetDatabase.GetAssetPath(shader),
                    ["usedBy"] = origin,
                    ["errorCount"] = errorCount,
                    ["warningCount"] = warningCount,
                    ["messages"] = messages,
                });
            }

            if (rows.Count > 0)
            {
                result["shadersWithErrors"] = rows;
            }

            if (missing.Count > 0)
            {
                result["missingShaders"] = missing;
            }

            if (rows.Count == 0 && missing.Count == 0)
            {
                result["summary"] = UnityEditor.ShaderUtil.anythingCompiling
                    ? "No shader errors found, but variants are still compiling."
                    : "No shader errors or missing shaders found.";
            }
            else
            {
                // Carry the next step in the result: a diagnostic that arrives when it is needed gets
                // read, unlike a catalogue the caller has to think to consult.
                result["summary"] = $"{rows.Count} shader(s) with errors, {missing.Count} missing/error material shader(s) — these render magenta.";
                result["nextStep"] = "Fix the reported file/line. If a pixel still looks wrong once shaders compile, use frame-debugger-pick-pixel to find the draw call that wrote it instead of toggling effects.";
            }

            return result;
        }

        private static void CollectShadersFromAssetPath(string path, List<(Shader, string)> shaders, Dictionary<string, object?> result)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            if (shader != null)
            {
                shaders.Add((shader, path));
                return;
            }

            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material != null)
            {
                if (material.shader != null)
                {
                    shaders.Add((material.shader, path));
                }
                else
                {
                    result["note"] = $"Material '{path}' has no shader assigned (renders magenta).";
                }

                return;
            }

            throw new ArgumentException($"No shader or material found at '{path}'.");
        }

        private static void CollectShadersFromOpenScenes(List<(Shader, string)> shaders, List<Dictionary<string, object?>> missing)
        {
#pragma warning disable CS0618
            foreach (var renderer in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
#pragma warning restore CS0618
            {
                foreach (var material in renderer.sharedMaterials)
                {
                    var rendererPath = HierarchyPath(renderer.gameObject);
                    if (material == null)
                    {
                        missing.Add(new Dictionary<string, object?>
                        {
                            ["gameObject"] = rendererPath,
                            ["reason"] = "material slot is empty",
                        });
                        continue;
                    }

                    if (material.shader == null)
                    {
                        missing.Add(new Dictionary<string, object?>
                        {
                            ["gameObject"] = rendererPath,
                            ["material"] = material.name,
                            ["reason"] = "material has no shader",
                        });
                        continue;
                    }

                    shaders.Add((material.shader, rendererPath));
                }
            }
        }

        private static string HierarchyPath(GameObject gameObject)
        {
            var path = gameObject.name;
            for (var parent = gameObject.transform.parent; parent != null; parent = parent.parent)
            {
                path = parent.name + "/" + path;
            }

            return "/" + path;
        }

        private static List<Dictionary<string, object?>> ReadShaderMessages(
            Shader shader,
            bool includeWarnings,
            int maxMessages,
            out int errorCount,
            out int warningCount)
        {
            errorCount = 0;
            warningCount = 0;
            var rows = new List<Dictionary<string, object?>>();
            UnityEditor.ShaderMessage[] messages;
            try
            {
                messages = UnityEditor.ShaderUtil.GetShaderMessages(shader);
            }
            catch (Exception)
            {
                return rows;
            }

            foreach (var message in messages)
            {
                var isError = message.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error;
                if (isError)
                {
                    errorCount++;
                }
                else
                {
                    warningCount++;
                }

                if ((!isError && !includeWarnings) || rows.Count >= maxMessages)
                {
                    continue;
                }

                var row = new Dictionary<string, object?>
                {
                    ["severity"] = isError ? "error" : "warning",
                    ["message"] = message.message,
                };
                if (!string.IsNullOrEmpty(message.file))
                {
                    row["file"] = message.file;
                }

                if (message.line > 0)
                {
                    row["line"] = message.line;
                }

                // messageDetails is "Compiling Subshader: N, Pass: X, <stage> program..." followed by a
                // multi-line dump of every platform define and disabled keyword. The first line says which
                // pass/variant failed (useful); the rest is hundreds of tokens of noise.
                var details = message.messageDetails;
                if (!string.IsNullOrEmpty(details))
                {
                    var firstLineEnd = details.IndexOf('\n');
                    row["variant"] = (firstLineEnd >= 0 ? details.Substring(0, firstLineEnd) : details).Trim();
                }

                rows.Add(row);
            }

            return rows;
        }

        private static JObject PlayModeSetSchema() => Schema(new JObject
        {
            // isPlaying is the sole advertised, canonical property. The play/playing aliases are kept
            // in the full schema (and enabled is read by the handler) for back-compat, but hidden from
            // the advertised surface via ADVERTISED_PROPERTY_OMISSIONS. They must stay declared here:
            // schemas use additionalProperties=false, so strict clients strip properties the full schema
            // omits. No required list - a client enforcing `required: isPlaying` would reject alias-only calls.
            ["isPlaying"] = Bool("Enter (true) or exit (false) Play Mode."),
            ["play"] = Bool("Alias for isPlaying."),
            ["playing"] = Bool("Alias for isPlaying."),
            // waitForReady/settleMs/timeoutMs are honored by the MCP server (it polls the heartbeat), not
            // the Unity handler, but must be declared here so additionalProperties=false clients keep them.
            ["waitForReady"] = Bool("Block until Play Mode actually reached the requested state (default true)."),
            ["settleMs"] = Int("Extra ms after the transition for frames to render (default 250)."),
            ["timeoutMs"] = Int("Max ms to wait for the transition (default 120000 entering, 30000 exiting — entering domain-reloads and can be slow on large projects)."),
        });

        private static JObject Schema(JObject properties, params string[] required)
        {
            var schema = new JObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = properties };
            if (required.Length > 0) schema["required"] = new JArray(required);
            return schema;
        }

        private static JObject Str(string description) => new() { ["type"] = "string", ["description"] = description };
        private static JObject Bool(string description) => new() { ["type"] = "boolean", ["description"] = description };
        private static JObject Num(string description) => new() { ["type"] = "number", ["description"] = description };
        private static JObject Int(string description) => new() { ["type"] = "integer", ["description"] = description };
        private static JObject Enum(string description, params string[] values) => new() { ["type"] = "string", ["description"] = description, ["enum"] = new JArray(values) };
        private static JObject Vector(string description) => new()
        {
            ["type"] = "object",
            ["description"] = description,
            ["additionalProperties"] = false,
            // x/y need no description; the names are self-evident and repeat across every vector arg.
            ["properties"] = new JObject { ["x"] = new JObject { ["type"] = "number" }, ["y"] = new JObject { ["type"] = "number" } },
        };

        private sealed class InputApi
        {
            private readonly Type keyboardType;
            private readonly Type mouseType;
            private readonly Type touchscreenType;
            private readonly Type keyType;
            private readonly Type keyboardStateType;
            private readonly Type mouseStateType;
            private readonly Type mouseButtonType;
            private readonly Type touchStateType;
            private readonly Type touchPhaseType;

            private InputApi(Type inputSystemType, Type keyboardType, Type mouseType, Type touchscreenType, Type keyType, Type keyboardStateType, Type mouseStateType, Type mouseButtonType, Type touchStateType, Type touchPhaseType)
            {
                this.keyboardType = keyboardType;
                this.mouseType = mouseType;
                this.touchscreenType = touchscreenType;
                this.keyType = keyType;
                this.keyboardStateType = keyboardStateType;
                this.mouseStateType = mouseStateType;
                this.mouseButtonType = mouseButtonType;
                this.touchStateType = touchStateType;
                this.touchPhaseType = touchPhaseType;
                Keyboard = keyboardType.GetProperty("current", BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase)?.GetValue(null);
                Touchscreen = touchscreenType.GetProperty("current", BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase)?.GetValue(null);
                Time = EditorApplication.timeSinceStartup;
                _ = inputSystemType;
            }

            public object? Keyboard { get; }

            // Resolved per access: while a pointer capture session is active, injected events must
            // go to the virtual mouse, and the session may begin mid-call.
            public object? Mouse => ChievfxMcpControlPointerCapture.VirtualMouse
                ?? mouseType.GetProperty("current", BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase)?.GetValue(null);

            public object? Touchscreen { get; }
            public double Time { get; }

            public static bool TryCreate(out InputApi? api, out string reason)
            {
                api = null;
#if CHIEVFX_MCP_HAS_INPUTSYSTEM
                var inputSystemType = FindType("UnityEngine.InputSystem.InputSystem");
                var keyboardType = FindType("UnityEngine.InputSystem.Keyboard");
                var mouseType = FindType("UnityEngine.InputSystem.Mouse");
                var touchscreenType = FindType("UnityEngine.InputSystem.Touchscreen");
                var keyType = FindType("UnityEngine.InputSystem.Key");
                var keyboardStateType = FindType("UnityEngine.InputSystem.LowLevel.KeyboardState");
                var mouseStateType = FindType("UnityEngine.InputSystem.LowLevel.MouseState");
                var mouseButtonType = FindType("UnityEngine.InputSystem.LowLevel.MouseButton");
                var touchStateType = FindType("UnityEngine.InputSystem.LowLevel.TouchState");
                var touchPhaseType = FindType("UnityEngine.InputSystem.TouchPhase");
                if (inputSystemType == null || keyboardType == null || mouseType == null || touchscreenType == null || keyType == null || keyboardStateType == null || mouseStateType == null || mouseButtonType == null || touchStateType == null || touchPhaseType == null)
                {
                    reason = "Input System package is installed but required UnityEngine.InputSystem types are not loaded.";
                    return false;
                }

                api = new InputApi(inputSystemType, keyboardType, mouseType, touchscreenType, keyType, keyboardStateType, mouseStateType, mouseButtonType, touchStateType, touchPhaseType);
                reason = "Input System types loaded.";
                return true;
#else
                reason = "com.unity.inputsystem package version define is not active.";
                return false;
#endif
            }

            public bool TryKey(string name, out object? key, out string error)
            {
                key = null;
                if (string.IsNullOrWhiteSpace(name))
                {
                    error = "key is required.";
                    return false;
                }

                var aliases = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["return"] = "Enter",
                    ["esc"] = "Escape",
                    ["spacebar"] = "Space",
                    ["ctrl"] = "LeftCtrl",
                    ["control"] = "LeftCtrl",
                    ["shift"] = "LeftShift",
                    ["alt"] = "LeftAlt",
                    // Bare digits map to the Digit* row keys (the Key enum has no "1").
                    ["0"] = "Digit0",
                    ["1"] = "Digit1",
                    ["2"] = "Digit2",
                    ["3"] = "Digit3",
                    ["4"] = "Digit4",
                    ["5"] = "Digit5",
                    ["6"] = "Digit6",
                    ["7"] = "Digit7",
                    ["8"] = "Digit8",
                    ["9"] = "Digit9",
                    // Arrow shorthands.
                    ["up"] = "UpArrow",
                    ["down"] = "DownArrow",
                    ["left"] = "LeftArrow",
                    ["right"] = "RightArrow",
                    ["del"] = "Delete",
                    ["ins"] = "Insert",
                    ["pgup"] = "PageUp",
                    ["pgdn"] = "PageDown",
                };
                var normalized = Norm(name);
                var match = System.Enum.GetNames(keyType).FirstOrDefault(enumName => Norm(enumName) == normalized)
                    ?? (aliases.TryGetValue(normalized, out var alias) ? alias : null);
                if (match == null)
                {
                    error = $"Invalid key '{name}'. Use a UnityEngine.InputSystem.Key enum name (e.g. A, Digit1, Space, UpArrow); bare digits 0-9 and up/down/left/right are also accepted.";
                    return false;
                }

                key = System.Enum.Parse(keyType, match);
                error = string.Empty;
                return true;
            }

            public bool TryKeyboardControl(object key, out object? control, out string error)
            {
                control = keyboardType.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault(property => property.Name == "Item" && property.GetIndexParameters().FirstOrDefault()?.ParameterType == keyType)
                    ?.GetValue(Keyboard, new[] { key });
                error = control == null ? $"Keyboard control for key '{key}' is unavailable." : string.Empty;
                return control != null;
            }

            public bool TryMouseButton(string name, out object? control, out string error)
            {
                var property = Norm(name) switch
                {
                    "" or "left" => "leftButton",
                    "right" => "rightButton",
                    "middle" => "middleButton",
                    "forward" => "forwardButton",
                    "back" => "backButton",
                    _ => null,
                };
                control = property == null ? null : mouseType.GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)?.GetValue(Mouse);
                error = property == null ? $"Invalid mouse button '{name}'. Use left, right, middle, forward, or back." : control == null ? $"Mouse control '{property}' is unavailable." : string.Empty;
                return control != null;
            }

            public bool TryMouseControl(string name, out object? control, out string error)
            {
                control = mouseType.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)?.GetValue(Mouse);
                error = control == null ? $"Mouse control '{name}' is unavailable." : string.Empty;
                return control != null;
            }

            public bool TryTouchControl(string name, out object? control, out string error)
            {
                control = touchscreenType.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)?.GetValue(Touchscreen);
                error = control == null ? $"Touchscreen control '{name}' is unavailable." : string.Empty;
                return control != null;
            }

            // The Queue* methods defer state building AND application into the player loop's input
            // update (RunInInputUpdate): reads there hit the player state buffers game code sees,
            // and InputState.Change bypasses the native event queue, which silently drops events
            // when the editor application has no OS focus.

            public void QueueKeyboardKey(object key, bool pressed, double eventTime)
            {
                _ = eventTime;
                ChievfxMcpControlInputPlayback.RunInInputUpdate(() =>
                {
                    var state = CreateKeyboardState();
                    SetKeyboardStateKey(state, key, pressed);
                    ChievfxMcpControlInputPlayback.ApplyState(Keyboard!, state, keyboardStateType);
                });
            }

            public void QueueMouseButton(string buttonName, bool pressed, double eventTime, Vector2? position = null)
            {
                _ = eventTime;
                ChievfxMcpControlInputPlayback.RunInInputUpdate(() =>
                {
                    // MouseState is a full snapshot: without an explicit position, a button change
                    // keeps the current pointer position. Callers pass the intended position when
                    // the batch also moves the pointer.
                    var state = CreateMouseState(position, null);
                    state = WithMouseButton(state, buttonName, pressed);
                    ChievfxMcpControlInputPlayback.ApplyState(Mouse!, state, mouseStateType);
                    ChievfxMcpControlAppliedInputState.RecordMouse(position ?? ReadMouseVector("position"), Vector2.zero);
                });
            }

            public void QueueMouseMove(Vector2? position, Vector2? delta, double eventTime)
            {
                _ = eventTime;
                ChievfxMcpControlInputPlayback.RunInInputUpdate(() =>
                {
                    var resolvedDelta = delta;
                    if (position.HasValue && !resolvedDelta.HasValue)
                    {
                        // Real mice always report delta; synthesize it so delta-driven game code
                        // (e.g. mouse-vs-gamepad input mode detectors) reacts to injected movement.
                        resolvedDelta = position.Value - ReadMouseVector("position");
                    }

                    var state = CreateMouseState(position, resolvedDelta);
                    ChievfxMcpControlInputPlayback.ApplyState(Mouse!, state, mouseStateType);
                    ChievfxMcpControlAppliedInputState.RecordMouse(position ?? ReadMouseVector("position"), resolvedDelta ?? Vector2.zero);
                });
            }

            public void QueueTouch(int touchId, string phaseName, Vector2? position, Vector2 delta, double eventTime)
            {
                _ = eventTime;
                ChievfxMcpControlInputPlayback.RunInInputUpdate(() =>
                {
                    var resolvedPosition = position ?? ReadPrimaryTouchPosition();
                    var state = CreateTouchState(touchId, phaseName, resolvedPosition, delta);
                    // TouchState targets an individual TouchControl, not the whole Touchscreen
                    // (whose state format differs). Mirror onto primaryTouch so Touchscreen-level
                    // reads and bindings see the injected touch.
                    var slot = ResolveTouchSlotControl(touchId);
                    if (slot != null)
                    {
                        ChievfxMcpControlInputPlayback.ApplyState(slot, state, touchStateType);
                    }

                    if (TryTouchControl("primaryTouch", out var primaryTouch, out _) && primaryTouch != null)
                    {
                        ChievfxMcpControlInputPlayback.ApplyState(primaryTouch, state, touchStateType);
                    }
                });
            }

            private object? ResolveTouchSlotControl(int touchId)
            {
                if (touchscreenType.GetProperty("touches", BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)?.GetValue(Touchscreen)
                    is not System.Collections.IEnumerable touches)
                {
                    return null;
                }

                object? freeSlot = null;
                foreach (var control in touches)
                {
                    if (control == null)
                    {
                        continue;
                    }

                    if (ReadControlValue(control, "touchId") is int currentId && currentId == touchId)
                    {
                        return control;
                    }

                    if (freeSlot == null)
                    {
                        var phase = ReadControlValue(control, "phase")?.ToString();
                        if (phase is null or "None" or "Ended" or "Canceled")
                        {
                            freeSlot = control;
                        }
                    }
                }

                return freeSlot;
            }

            private static object? ReadControlValue(object parentControl, string childName)
            {
                var child = parentControl.GetType().GetProperty(childName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)?.GetValue(parentControl);
                var readValue = child?.GetType().GetMethod("ReadValue", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                return readValue?.Invoke(child, Array.Empty<object>());
            }

            private object CreateKeyboardState()
            {
                var state = Activator.CreateInstance(keyboardStateType)!;
                if (keyboardType.GetProperty("allKeys", BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)?.GetValue(Keyboard) is System.Collections.IEnumerable keys)
                {
                    foreach (var keyControl in keys)
                    {
                        var keyCode = keyControl?.GetType().GetProperty("keyCode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)?.GetValue(keyControl);
                        if (keyCode != null)
                        {
                            SetKeyboardStateKey(state, keyCode, ReadPressed(keyControl));
                        }
                    }
                }

                return state;
            }

            private void SetKeyboardStateKey(object state, object key, bool pressed)
            {
                keyboardStateType.GetMethod("Set", BindingFlags.Instance | BindingFlags.Public, null, new[] { keyType, typeof(bool) }, null)
                    ?.Invoke(state, new[] { key, pressed });
            }

            private object CreateMouseState(Vector2? position, Vector2? delta)
            {
                var state = Activator.CreateInstance(mouseStateType)!;
                foreach (var buttonName in new[] { "left", "right", "middle", "forward", "back" })
                {
                    if (TryMouseButton(buttonName, out var control, out _))
                    {
                        state = WithMouseButton(state, buttonName, ReadPressed(control));
                    }
                }

                SetMouseVector(state, "position", position ?? ReadMouseVector("position"));
                SetMouseVector(state, "delta", delta ?? Vector2.zero);
                return state;
            }

            private object WithMouseButton(object state, string buttonName, bool pressed)
            {
                var enumName = Norm(buttonName) switch
                {
                    "" or "left" => "Left",
                    "right" => "Right",
                    "middle" => "Middle",
                    "forward" => "Forward",
                    "back" => "Back",
                    _ => "Left",
                };
                var button = System.Enum.Parse(mouseButtonType, enumName);
                return mouseStateType.GetMethod("WithButton", BindingFlags.Instance | BindingFlags.Public, null, new[] { mouseButtonType, typeof(bool) }, null)
                    ?.Invoke(state, new[] { button, pressed }) ?? state;
            }

            private static bool ReadPressed(object? control)
            {
                return control?.GetType().GetProperty("isPressed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)?.GetValue(control) is bool pressed && pressed;
            }

            private Vector2 ReadMouseVector(string property)
            {
                var control = mouseType.GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)?.GetValue(Mouse);
                var readValue = control?.GetType().GetMethod("ReadValue", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                return readValue?.ReturnType == typeof(Vector2) && readValue.Invoke(control, Array.Empty<object>()) is Vector2 value ? value : Vector2.zero;
            }

            public Vector2 ReadMousePosition()
            {
                return ReadMouseVector("position");
            }

            private void SetMouseVector(object state, string fieldName, Vector2 value)
            {
                var field = mouseStateType.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
                if (field != null && field.FieldType == typeof(Vector2))
                {
                    field.SetValue(state, value);
                }
            }

            public Vector2 ReadPrimaryTouchPosition()
            {
                var primaryTouch = touchscreenType.GetProperty("primaryTouch", BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)?.GetValue(Touchscreen);
                var position = primaryTouch?.GetType().GetProperty("position", BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)?.GetValue(primaryTouch);
                var readValue = position?.GetType().GetMethod("ReadValue", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                return readValue?.ReturnType == typeof(Vector2) && readValue.Invoke(position, Array.Empty<object>()) is Vector2 value ? value : Vector2.zero;
            }

            private object CreateTouchState(int touchId, string phaseName, Vector2 position, Vector2 delta)
            {
                var state = Activator.CreateInstance(touchStateType)!;
                SetTouchMember(state, "touchId", touchId);
                SetTouchMember(state, "position", position);
                SetTouchMember(state, "delta", delta);
                SetTouchMember(state, "startPosition", position);
                SetTouchMember(state, "pressure", phaseName is "Ended" or "Canceled" ? 0f : 1f);
                SetTouchMember(state, "phase", System.Enum.Parse(touchPhaseType, phaseName));
                SetTouchMember(state, "isPrimaryTouch", true);
                return state;
            }

            private void SetTouchMember(object state, string memberName, object value)
            {
                var property = touchStateType.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
                if (property != null && property.CanWrite && CanAssign(property.PropertyType, value))
                {
                    property.SetValue(state, value);
                    return;
                }

                var field = touchStateType.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
                if (field != null && CanAssign(field.FieldType, value))
                {
                    field.SetValue(state, value);
                }
            }

            private static bool CanAssign(Type targetType, object value)
            {
                var valueType = value.GetType();
                if (targetType.IsInstanceOfType(value))
                {
                    return true;
                }

                return targetType.IsEnum && valueType == targetType;
            }

            private static Type? FindType(string fullName)
            {
                return AppDomain.CurrentDomain.GetAssemblies()
                    .Select(assembly => assembly.GetType(fullName, throwOnError: false))
                    .FirstOrDefault(type => type != null);
            }
        }
    }
}
