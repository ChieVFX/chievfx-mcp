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
                return UnityAssemblies.GetLoaded()
                    .Select(assembly => assembly.GetType(fullName, throwOnError: false))
                    .FirstOrDefault(type => type != null);
            }
        }
    }
}
