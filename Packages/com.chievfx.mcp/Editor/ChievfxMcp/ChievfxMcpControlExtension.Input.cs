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

namespace Chievfx.Mcp.Extensions.Control
{
    internal static partial class ChievfxMcpControlExtension
    {
        private static Dictionary<string, object?> Keyboard(JToken args, InputApi api)
        {
            var action = ReadAction(args);
            var keyName = ReadString(args, "key") ?? ReadString(args, "targetKey") ?? string.Empty;
            var dryRun = ResolveDryRun(args);
            var holdFrames = ReadHoldFrames(args);
            var holdMs = ReadDouble(args, "durationMs", 0d);
            var errors = new List<string>();
            var warnings = new List<string>();
            var queued = new List<object>();
            if (!OneOf(action, "down", "up", "tap")) errors.Add("action must be one of: down, up, tap.");
            if (holdMs < 0d || holdMs > 60000d) errors.Add("durationMs must be between 0 and 60000.");
            if (holdMs > 0d && action != "tap") warnings.Add("durationMs applies to tap only; down/up are single events.");
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
                            new ChievfxMcpControlInputPlayback.Step { FrameGapBefore = holdFrames, SecondsGapBefore = holdMs / 1000d, Dispatch = () => api.QueueKeyboardKey(key!, false, -1d) },
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
            if (action == "tap")
            {
                result["holdFrames"] = holdFrames;
                if (holdMs > 0d)
                {
                    result["holdMs"] = holdMs;
                }
            }

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
            result["hint"] = "Await: pass awaitMs to this call next time, or events-wait marker=" + completionMarker + " sinceEventId=" + eventCursorBefore;
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
    }
}
