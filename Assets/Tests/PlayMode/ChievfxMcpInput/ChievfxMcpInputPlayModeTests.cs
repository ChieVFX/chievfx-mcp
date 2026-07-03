#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using UnityEngine.UIElements;

namespace Chievfx.Mcp.Input.PlayMode.Tests
{
    public sealed class ChievfxMcpInputPlayModeTests
    {
        private static readonly List<object> AddedInputDevices = new();
        private readonly List<UnityEngine.Object> cleanupObjects = new();
        private int uguiClickCount;
        private int uiToolkitClickCount;
        private int uiToolkitPointerDownCount;
        private int uiToolkitPointerUpCount;

        [UnityTest]
        public IEnumerator MouseAndTouchAffectRuntimeTargets()
        {
            RequireInputSystem();
            BuildScene();
            EnsureInputDevice("Mouse");
            EnsureInputDevice("Touchscreen");
            yield return null;
            PopulateUiToolkitDocument();
            Canvas.ForceUpdateCanvases();
            yield return WaitForUiToolkitLayout();

            var uguiPoint = RectCenterScreenPoint("InputMcpUguiCanvas/InputMcpUguiButton");
            yield return MouseClick(uguiPoint);
            Assert.AreEqual(1, uguiClickCount, "Mouse MCP events should drive uGUI through InputSystemUIInputModule.");

            yield return TouchClick(uguiPoint);
            Assert.AreEqual(2, uguiClickCount, "Touch MCP events should drive uGUI through InputSystemUIInputModule.");

            var uiToolkitPoint = UiToolkitButtonScreenPoint();
            var uiToolkitProbe = RunRuntimeUiProbe(ProbeScreenArgs(uiToolkitPoint));
            Assert.Greater(
                Convert.ToInt32(Row(uiToolkitProbe, "uitoolkit")["count"], CultureInfo.InvariantCulture),
                0,
                "UI Toolkit runtime probe should resolve the test button before input dispatch. " + DescribeRow(uiToolkitProbe));
            yield return MouseClick(uiToolkitPoint);
            Assert.IsTrue(UiToolkitButtonIsUnderMouse(), "Mouse MCP events should move the pointer onto the UI Toolkit runtime button.");
            Assert.GreaterOrEqual(uiToolkitPointerDownCount, 1, "Mouse MCP events should send pointer down to UI Toolkit runtime panels.");
            Assert.GreaterOrEqual(uiToolkitPointerUpCount, 1, "Mouse MCP events should send pointer up to UI Toolkit runtime panels.");
            Assert.AreEqual(1, uiToolkitClickCount, "Mouse MCP events should click UI Toolkit runtime panels.");

            yield return TouchClick(uiToolkitPoint);
            Assert.AreEqual(2, uiToolkitClickCount, "Touch MCP events should click UI Toolkit runtime panels.");

            var physicsPoint = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            yield return MouseMoveHitsPhysicsTarget(physicsPoint);
            yield return TouchDownHitsPhysicsTarget(physicsPoint);
        }

        [UnityTest]
        public IEnumerator KeyboardTapProducesFrameVisibleEdges()
        {
            RequireInputSystem();
            EnsureInputDevice("Keyboard");
            var probe = Track(new GameObject("InputMcpKeyboardEdgeProbe")).AddComponent<KeyboardEdgeProbe>();
            probe.KeyName = "F8";
            yield return null;

            var result = RunControlTool("input-control-keyboard-event", "{'action':'tap','key':'F8','allowStateMutation':true}");
            Assert.AreEqual(true, result["ok"], DescribeRow(result));
            Assert.AreEqual("scheduled", result["status"], "Real keyboard taps should be frame-scheduled. " + DescribeRow(result));
            Assert.IsNotNull(result["completionMarker"], "Scheduled taps should report a completion marker for events-wait.");

            for (var i = 0; i < 60 && probe.ReleaseEdges == 0; i++)
            {
                yield return null;
            }

            Assert.AreEqual(1, probe.PressEdges, "Keyboard tap should produce exactly one wasPressedThisFrame edge visible to MonoBehaviour.Update().");
            Assert.AreEqual(1, probe.ReleaseEdges, "Keyboard tap should produce exactly one wasReleasedThisFrame edge visible to MonoBehaviour.Update().");
            Assert.GreaterOrEqual(probe.HeldFrames, 1, "Keyboard tap should hold the key for at least one full player frame.");
        }

        [UnityTest]
        public IEnumerator PointerCaptureRoutesMouseInjectionToVirtualDevice()
        {
            RequireInputSystem();
            EnsureInputDevice("Mouse");
            yield return null;

            var target = new Vector2(211f, 137f);
            var moveResult = RunControlTool("input-control-mouse-event", PositionArgs("move", target, null));
            Assert.AreEqual(true, moveResult["ok"], DescribeRow(moveResult));

            var status = RunControlTool("input-control-pointer-capture", "{'action':'status'}");
            Assert.AreEqual(true, Row(status, "pointerCapture")["active"], "Real mouse injection should begin a pointer capture session by default. " + DescribeRow(status));

            yield return null;
            yield return null;
            for (var i = 0; i < 3; i++)
            {
                var position = ReadInputVector("UnityEngine.InputSystem.Mouse", "current", "position");
                Assert.AreEqual(target.x, position.x, 0.5f, "Injected mouse X should persist while pointer capture is active (frame " + i + ").");
                Assert.AreEqual(target.y, position.y, 0.5f, "Injected mouse Y should persist while pointer capture is active (frame " + i + ").");
                yield return null;
            }

            var end = RunControlTool("input-control-pointer-capture", "{'action':'end'}");
            Assert.AreEqual(true, end["ok"], DescribeRow(end));
            Assert.AreEqual(false, Row(end, "pointerCapture")["active"], "Ending pointer capture should remove the virtual mouse and re-enable physical mice.");
            yield return null;
        }

        private sealed class KeyboardEdgeProbe : MonoBehaviour
        {
            public string KeyName = "F8";
            public int PressEdges;
            public int ReleaseEdges;
            public int HeldFrames;

            private void Update()
            {
                var control = ResolveKeyControl();
                if (control == null)
                {
                    return;
                }

                if (ReadBool(control, "wasPressedThisFrame")) PressEdges++;
                if (ReadBool(control, "wasReleasedThisFrame")) ReleaseEdges++;
                if (ReadBool(control, "isPressed")) HeldFrames++;
            }

            private object? ResolveKeyControl()
            {
                var keyboardType = FindType("UnityEngine.InputSystem.Keyboard");
                var keyType = FindType("UnityEngine.InputSystem.Key");
                var keyboard = keyboardType?.GetProperty("current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (keyboard == null || keyType == null)
                {
                    return null;
                }

                var key = Enum.Parse(keyType, KeyName);
                return keyboard.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault(property => property.Name == "Item" && property.GetIndexParameters().FirstOrDefault()?.ParameterType == keyType)
                    ?.GetValue(keyboard, new[] { key });
            }

            private static bool ReadBool(object control, string name)
            {
                return control.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)?.GetValue(control) is bool value && value;
            }
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            RemoveAddedInputDevices();
            foreach (var item in cleanupObjects.Where(item => item != null))
            {
                UnityEngine.Object.Destroy(item);
            }

            cleanupObjects.Clear();
            yield return null;
        }

        private static Dictionary<string, object?> RunControlTool(string toolName, string argsJson)
        {
            return (Dictionary<string, object?>)ControlExtensionType()
                .GetMethod("RunToolForTests", BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, new object[] { toolName, argsJson })!;
        }

        private static void RequireInputSystem()
        {
            var status = (Dictionary<string, object?>)ControlExtensionType()
                .GetMethod("ReadResourceForTests", BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, new object[] { "chievfx://extensions/chievfx.control/status" })!;
            if (!Equals(status["available"], true))
            {
                Assert.Ignore("Input System package/types are not loaded in this project.");
            }
        }

        private IEnumerator MouseClick(Vector2 position)
        {
            RunControlTool("input-control-mouse-event", PositionArgs("move", position, "left"));
            yield return null;
            RunControlTool("input-control-mouse-event", PositionArgs("down", position, "left"));
            yield return null;
            RunControlTool("input-control-mouse-event", PositionArgs("up", position, "left"));
            yield return WaitForInputProcessing();
        }

        private IEnumerator TouchClick(Vector2 position)
        {
            RunControlTool("input-control-touch-event", PositionArgs("down", position, null));
            yield return null;
            RunControlTool("input-control-touch-event", PositionArgs("up", position, null));
            yield return WaitForInputProcessing();
        }

        private IEnumerator MouseMoveHitsPhysicsTarget(Vector2 position)
        {
            RunControlTool("input-control-mouse-event", PositionArgs("move", position, "left"));
            yield return null;

            var mousePosition = ReadInputVector("UnityEngine.InputSystem.Mouse", "current", "position");
            AssertPhysicsRaycastHitsTarget(mousePosition, "Mouse MCP events should move the pointer over the physics target.");
        }

        private IEnumerator TouchDownHitsPhysicsTarget(Vector2 position)
        {
            RunControlTool("input-control-touch-event", PositionArgs("down", position, null));
            yield return null;

            var touchPosition = ReadInputVector("UnityEngine.InputSystem.Touchscreen", "current", "primaryTouch", "position");
            AssertPhysicsRaycastHitsTarget(touchPosition, "Touch MCP events should move primaryTouch over the physics target.");

            RunControlTool("input-control-touch-event", PositionArgs("up", position, null));
            yield return null;
        }

        private static string PositionArgs(string action, Vector2 position, string? button)
        {
            var json = "{'action':'" + action + "','screenPosition':{'x':"
                + position.x.ToString(CultureInfo.InvariantCulture)
                + ",'y':"
                + position.y.ToString(CultureInfo.InvariantCulture)
                + "},'allowStateMutation':true";
            if (button != null && action != "move")
            {
                json += ",'button':'" + button + "'";
            }

            return json + "}";
        }

        private static string ProbeScreenArgs(Vector2 position)
        {
            return "{'x':"
                + position.x.ToString(CultureInfo.InvariantCulture)
                + ",'y':"
                + position.y.ToString(CultureInfo.InvariantCulture)
                + "}";
        }

        private static string ScreenPositionArgs(Vector2 position)
        {
            return "{'screenPosition':{'x':"
                + position.x.ToString(CultureInfo.InvariantCulture)
                + ",'y':"
                + position.y.ToString(CultureInfo.InvariantCulture)
                + "}}";
        }

        private void BuildScene()
        {
            uguiClickCount = 0;
            uiToolkitClickCount = 0;
            uiToolkitPointerDownCount = 0;
            uiToolkitPointerUpCount = 0;
            var scene = SceneManager.CreateScene("InputMcpPlayModeRuntimeScene");
            SceneManager.SetActiveScene(scene);

            var cameraObject = Track(new GameObject("InputMcpCamera"));
            var camera = cameraObject.AddComponent<Camera>();
            camera.orthographic = true;
            camera.orthographicSize = 3f;
            camera.transform.position = new Vector3(0f, 0f, -10f);
            cameraObject.tag = "MainCamera";

            var raycastTarget = Track(GameObject.CreatePrimitive(PrimitiveType.Cube));
            raycastTarget.name = "InputMcpRaycastTarget";
            raycastTarget.transform.position = Vector3.zero;
            raycastTarget.AddComponent<Rigidbody>().isKinematic = true;
            Physics.SyncTransforms();

            var eventSystem = Track(new GameObject("EventSystem"));
            eventSystem.AddComponent<EventSystem>();
            AddInputSystemUiModule(eventSystem);

            CreateUguiButton();
            CreateUiToolkitDocument();
        }

        private void CreateUguiButton()
        {
            var canvasObject = Track(new GameObject("InputMcpUguiCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster)));
            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 10;
            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(800f, 600f);

            var buttonObject = Track(new GameObject("InputMcpUguiButton", typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.Button)));
            buttonObject.transform.SetParent(canvasObject.transform, worldPositionStays: false);
            var rect = (RectTransform)buttonObject.transform;
            rect.anchorMin = new Vector2(0.25f, 0.5f);
            rect.anchorMax = new Vector2(0.25f, 0.5f);
            rect.sizeDelta = new Vector2(220f, 100f);
            buttonObject.GetComponent<UnityEngine.UI.Image>().raycastTarget = true;
            buttonObject.GetComponent<UnityEngine.UI.Button>().onClick.AddListener(() => uguiClickCount++);
        }

        private void CreateUiToolkitDocument()
        {
            var documentObject = Track(new GameObject("InputMcpUiToolkitDocument"));
            var document = documentObject.AddComponent<UIDocument>();
            AddUiToolkitPanelInputComponents(documentObject);
            var panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
            cleanupObjects.Add(panelSettings);
            SetMemberValue(panelSettings, "sortingOrder", 50);
            SetMemberValue(panelSettings, "scaleMode", PanelScaleMode.ConstantPixelSize);
            SetMemberValue(panelSettings, "scale", 1f);
            SetMemberValue(panelSettings, "referenceResolution", new Vector2Int(800, 600));
            SetMemberValue(panelSettings, "themeStyleSheet", LoadRuntimeThemeStyleSheet());
            document.panelSettings = panelSettings;
        }

        private void PopulateUiToolkitDocument()
        {
            var document = UnityEngine.Object.FindAnyObjectByType<UIDocument>()!;
            var root = document.rootVisualElement;
            root.Clear();
            root.pickingMode = PickingMode.Ignore;
            root.style.position = Position.Relative;
            root.style.width = Screen.width;
            root.style.height = Screen.height;
            var button = new UnityEngine.UIElements.Button(() => uiToolkitClickCount++)
            {
                name = "InputMcpUiToolkitButton",
                text = "UITK INPUT"
            };
            button.pickingMode = PickingMode.Position;
            // TrickleDown: since Unity 6, Button's built-in Clickable manipulator calls
            // StopImmediatePropagation on pointer down/up, so default-phase callbacks
            // registered after it never fire.
            button.RegisterCallback<PointerDownEvent>(_ => uiToolkitPointerDownCount++, TrickleDown.TrickleDown);
            button.RegisterCallback<PointerUpEvent>(_ => uiToolkitPointerUpCount++, TrickleDown.TrickleDown);
            button.style.position = Position.Absolute;
            button.style.left = Screen.width * 0.75f - 110f;
            button.style.top = Screen.height * 0.5f - 50f;
            button.style.width = 220f;
            button.style.height = 100f;
            root.Add(button);
        }

        private static IEnumerator WaitForUiToolkitLayout()
        {
            for (var i = 0; i < 10; i++)
            {
                var document = UnityEngine.Object.FindAnyObjectByType<UIDocument>()!;
                var button = document.rootVisualElement.Q("InputMcpUiToolkitButton");
                if (button != null && button.worldBound.width > 1f && button.worldBound.height > 1f)
                {
                    yield break;
                }

                yield return null;
            }

            Assert.Fail("UI Toolkit test button layout was not ready.");
        }
        private static IEnumerator WaitForInputProcessing()
        {
            for (var i = 0; i < 5; i++)
            {
                yield return null;
            }
        }

        private static Vector2 UiToolkitButtonScreenPoint()
        {
            var document = UnityEngine.Object.FindAnyObjectByType<UIDocument>()!;
            var button = document.rootVisualElement.Q("InputMcpUiToolkitButton");
            var center = button.worldBound.center;
            return center;
        }

        private static bool UiToolkitButtonIsUnderMouse()
        {
            var mousePosition = ReadInputVector("UnityEngine.InputSystem.Mouse", "current", "position");
            var document = UnityEngine.Object.FindAnyObjectByType<UIDocument>()!;
            var button = document.rootVisualElement.Q("InputMcpUiToolkitButton");
            return button.worldBound.Contains(mousePosition);
        }

        private static Vector2 RectCenterScreenPoint(string path)
        {
            var rect = (RectTransform)GameObject.Find(path)!.transform;
            return RectTransformUtility.WorldToScreenPoint(null, rect.position);
        }

        private static void AddInputSystemUiModule(GameObject eventSystem)
        {
            var moduleType = FindType("UnityEngine.InputSystem.UI.InputSystemUIInputModule");
            Assert.IsNotNull(moduleType, "InputSystemUIInputModule type must be loaded for runtime input tests.");
            if (eventSystem.GetComponent(moduleType!) == null)
            {
                var module = eventSystem.AddComponent(moduleType!);
                moduleType!.GetMethod("AssignDefaultActions", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.Invoke(module, Array.Empty<object>());
            }
        }

        private static void AddUiToolkitPanelInputComponents(GameObject documentObject)
        {
            AddOptionalComponent(documentObject, "UnityEngine.UIElements.PanelRaycaster");
            AddOptionalComponent(documentObject, "UnityEngine.UIElements.PanelEventHandler");
        }

        private static void AddOptionalComponent(GameObject gameObject, string typeName)
        {
            var componentType = FindType(typeName);
            if (componentType != null && gameObject.GetComponent(componentType) == null)
            {
                gameObject.AddComponent(componentType);
            }
        }

        private static void EnsureInputDevice(string layout)
        {
            var inputSystemType = FindType("UnityEngine.InputSystem.InputSystem");
            Assert.IsNotNull(inputSystemType);
            var deviceType = FindType("UnityEngine.InputSystem." + layout);
            Assert.IsNotNull(deviceType);
            if (deviceType!.GetProperty("current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) != null)
            {
                return;
            }

            var device = AddInputDevice(inputSystemType!, deviceType, layout);
            if (device != null)
            {
                AddedInputDevices.Add(device);
            }
        }

        private static object? AddInputDevice(Type inputSystemType, Type deviceType, string layout)
        {
            var addDeviceMethods = inputSystemType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method => method.Name == "AddDevice")
                .Select(method => new { Method = method, Parameters = method.GetParameters() })
                .ToArray();

            var layoutOverload = addDeviceMethods
                .Where(candidate =>
                    !candidate.Method.IsGenericMethodDefinition
                    && candidate.Parameters.Length == 3
                    && candidate.Parameters.All(parameter => parameter.ParameterType == typeof(string)))
                .Select(candidate => candidate.Method)
                .FirstOrDefault();
            if (layoutOverload != null)
            {
                return layoutOverload.Invoke(null, new object?[] { layout, null, null });
            }

            var genericStringOverload = addDeviceMethods
                .Where(candidate =>
                    candidate.Method.IsGenericMethodDefinition
                    && candidate.Method.GetGenericArguments().Length == 1
                    && candidate.Parameters.Length == 1
                    && candidate.Parameters[0].ParameterType == typeof(string))
                .Select(candidate => candidate.Method)
                .FirstOrDefault();
            if (genericStringOverload != null)
            {
                return genericStringOverload.MakeGenericMethod(deviceType).Invoke(null, new object?[] { layout });
            }

            var availableSignatures = string.Join(", ", addDeviceMethods.Select(candidate => candidate.Method.ToString()));
            Assert.Fail("Supported InputSystem.AddDevice overload not found. Available: " + availableSignatures);
            return null;
        }

        private static void RemoveAddedInputDevices()
        {
            if (AddedInputDevices.Count == 0)
            {
                return;
            }

            var inputSystemType = FindType("UnityEngine.InputSystem.InputSystem");
            var removeDevice = inputSystemType?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(method => method.Name == "RemoveDevice" && method.GetParameters().Length == 1);
            foreach (var device in AddedInputDevices.ToArray())
            {
                try
                {
                    removeDevice?.Invoke(null, new[] { device });
                }
                catch (TargetInvocationException)
                {
                }
            }

            AddedInputDevices.Clear();
        }

        private static Vector2 ReadInputVector(string deviceTypeName, string deviceProperty, string controlProperty)
        {
            var device = FindType(deviceTypeName)!.GetProperty(deviceProperty, BindingFlags.Public | BindingFlags.Static)!.GetValue(null);
            var control = device!.GetType().GetProperty(controlProperty, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)!.GetValue(device);
            return (Vector2)control!.GetType().GetMethod("ReadValue", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null)!.Invoke(control, Array.Empty<object>())!;
        }

        private static Vector2 ReadInputVector(string deviceTypeName, string deviceProperty, string childControlProperty, string controlProperty)
        {
            var device = FindType(deviceTypeName)!.GetProperty(deviceProperty, BindingFlags.Public | BindingFlags.Static)!.GetValue(null);
            var childControl = device!.GetType().GetProperty(childControlProperty, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)!.GetValue(device);
            var control = childControl!.GetType().GetProperty(controlProperty, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)!.GetValue(childControl);
            return (Vector2)control!.GetType().GetMethod("ReadValue", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null)!.Invoke(control, Array.Empty<object>())!;
        }

        private static string DescribeRow(Dictionary<string, object?> row)
        {
            return string.Join("; ", row.Select(pair => pair.Key + "=" + (pair.Value is object[] array ? "[" + array.Length + "]" : pair.Value ?? "null")));
        }

        private static void AssertPhysicsRaycastHitsTarget(Vector2 screenPosition, string message)
        {
            var ray = Camera.main!.ScreenPointToRay(screenPosition);
            Assert.AreEqual("InputMcpRaycastTarget", Physics.Raycast(ray, out var hit, 100f) ? hit.rigidbody!.name : string.Empty, message);
        }

        private GameObject Track(GameObject gameObject)
        {
            cleanupObjects.Add(gameObject);
            return gameObject;
        }

        private static void SetMemberValue(object target, string memberName, object? value)
        {
            var type = target.GetType();
            var property = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (property != null && property.CanWrite)
            {
                property.SetValue(target, value);
                return;
            }

            var field = type.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            field?.SetValue(target, value);
        }

        private static UnityEngine.Object? LoadRuntimeThemeStyleSheet()
        {
            var assetDatabaseType = FindType("UnityEditor.AssetDatabase");
            var themeStyleSheetType = FindType("UnityEngine.UIElements.ThemeStyleSheet");
            var loadAssetAtPath = assetDatabaseType?.GetMethod("LoadAssetAtPath", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(Type) }, null);
            return loadAssetAtPath?.Invoke(null, new object[] { "Assets/UI Toolkit/UnityThemes/UnityDefaultRuntimeTheme.tss", themeStyleSheetType! }) as UnityEngine.Object;
        }

        private static Type? FindType(string fullName)
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(fullName, throwOnError: false))
                .FirstOrDefault(type => type != null);
        }

        private static Type ControlExtensionType()
        {
            var type = FindType("Chievfx.Mcp.Extensions.Control.ChievfxMcpControlExtension");
            Assert.IsNotNull(type, "Control extension must be loaded in the Editor while PlayMode tests run.");
            return type!;
        }

        private static Dictionary<string, object?> RunRuntimeUiProbe(string argsJson)
        {
            EnsureRuntimeUiRegistry();
            var extensionRegistry = FindType("Chievfx.Mcp.Editor.ChievfxMcpExtensionRegistry");
            Assert.IsNotNull(extensionRegistry, "ChievFX extension registry must be loaded in the Editor while PlayMode tests run.");
            var jTokenType = FindType("Newtonsoft.Json.Linq.JToken");
            Assert.IsNotNull(jTokenType, "Newtonsoft.Json.Linq must be loaded in the Editor while PlayMode tests run.");
            var method = extensionRegistry!.GetMethod(
                "TryRunTool",
                BindingFlags.NonPublic | BindingFlags.Static,
                null,
                new[] { typeof(string), jTokenType!, typeof(object).MakeByRefType() },
                null);
            Assert.IsNotNull(method);
            var parameters = new object?[] { "ui-runtime-probe", ParseJsonToken(argsJson), null };
            Assert.IsTrue((bool)method!.Invoke(null, parameters)!);
            return (Dictionary<string, object?>)parameters[2]!;
        }

        private static void EnsureRuntimeUiRegistry()
        {
            var registryType = FindType("Chievfx.Mcp.Editor.ChievfxMcpRuntimeUiAdapterRegistry");
            Assert.IsNotNull(registryType, "Runtime UI adapter registry must be loaded in the Editor while PlayMode tests run.");
            registryType!.GetMethod("EnsureRegistered", BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, null);
        }

        private static object ParseJsonToken(string argsJson)
        {
            var jObjectType = FindType("Newtonsoft.Json.Linq.JObject");
            Assert.IsNotNull(jObjectType, "Newtonsoft.Json.Linq must be loaded in the Editor while PlayMode tests run.");
            return jObjectType!.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null)!
                .Invoke(null, new object[] { argsJson })!;
        }

        private static Dictionary<string, object?> Row(Dictionary<string, object?> source, string key)
        {
            return (Dictionary<string, object?>)source[key]!;
        }
    }
}
