#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Chievfx.Mcp.Editor;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using static Chievfx.Mcp.Extensions.Ugui.UguiRuntimeHelpers;
using static Chievfx.Mcp.Extensions.Ugui.UguiSharedHelpers;

namespace Chievfx.Mcp.Extensions.Ugui
{
    /// <summary>
    /// Replays a pointer tap or drag the way the uGUI input modules do (StandaloneInputModule /
    /// InputSystemUIInputModule): the same PointerEventData fields, the same receivers (ExecuteHierarchy for
    /// press, the pressed object for release, the click handler only when press and release agree), and a
    /// record of which components actually responded to each event, so a tool reports what happened rather
    /// than "some interface was implemented".
    /// </summary>
    internal static class UguiRuntimePointerDispatch
    {
        internal sealed class PointerDispatchReport
        {
            public List<string> Events { get; } = new();

            public List<Dictionary<string, object?>> Details { get; } = new();

            public List<string> Warnings { get; } = new();

            // Object whose click handler ran, or the press-driven control (Slider, InputField) that acted on
            // PointerDown. Null when the tap reached nothing that reacts to it.
            public GameObject? Responder { get; set; }

            public bool Clicked => Responder != null;

            // Something other than a Selectable's own highlight/selection handling reacted to PointerDown or
            // PointerUp: an EventTrigger entry or a custom press handler.
            public bool PressHandled { get; set; }

            public GameObject? DragResponder { get; set; }
        }

        // Controls that do their work on PointerDown rather than on PointerClick.
        private static readonly string[] PressDrivenControlTypeNames =
        {
            "UnityEngine.UI.Slider",
            "UnityEngine.UI.Scrollbar",
            "UnityEngine.UI.InputField",
            "TMPro.TMP_InputField",
        };

        private static readonly FieldInfo? UnityEventCallsField = typeof(UnityEventBase).GetField("m_Calls", BindingFlags.Instance | BindingFlags.NonPublic);

        internal static PointerDispatchReport DispatchTap(EventSystem eventSystem, GameObject target, Vector2 screenPosition, bool targetFromScreenPosition)
        {
            var report = new PointerDispatchReport();
            var pointer = CreatePressPointer(eventSystem, target, screenPosition, targetFromScreenPosition, out var currentOverGo);

            EnterHierarchy(pointer, currentOverGo);
            var pressReceiver = Press(eventSystem, pointer, currentOverGo, report);

            var upReceiver = pointer.pointerPress;
            var upResponders = FindResponders<IPointerUpHandler>(upReceiver, EventTriggerType.PointerUp);
            if (upReceiver != null)
            {
                ExecuteEvents.Execute(upReceiver, pointer, ExecuteEvents.pointerUpHandler);
            }

            RecordEvent(report, "pointerUp", upReceiver, upResponders);
            report.PressHandled |= HasPressResponder(upResponders);

            var clickReceiver = ExecuteEvents.GetEventHandler<IPointerClickHandler>(currentOverGo);
            var clickResponders = Array.Empty<Component>();
            if (clickReceiver != null && pointer.pointerClick == clickReceiver && pointer.eligibleForClick)
            {
                clickResponders = FindResponders<IPointerClickHandler>(clickReceiver, EventTriggerType.PointerClick);
                ExecuteEvents.Execute(clickReceiver, pointer, ExecuteEvents.pointerClickHandler);
            }

            RecordEvent(report, "pointerClick", clickReceiver, clickResponders);
            ReleasePointer(pointer);
            ExitHierarchy(pointer);

            var pressDrivenControl = pressReceiver == null
                ? null
                : pressReceiver.GetComponents<Component>().FirstOrDefault(IsPressDrivenControl);
            report.Responder = clickResponders.Length > 0
                ? clickReceiver
                : pressDrivenControl == null ? null : pressReceiver;
            AddTapWarnings(report, target, clickReceiver, clickResponders);
            return report;
        }

        internal static PointerDispatchReport DispatchDrag(EventSystem eventSystem, GameObject target, Vector2 start, Vector2 end, bool targetFromScreenPosition)
        {
            var report = new PointerDispatchReport();
            var pointer = CreatePressPointer(eventSystem, target, start, targetFromScreenPosition, out var currentOverGo);

            EnterHierarchy(pointer, currentOverGo);
            Press(eventSystem, pointer, currentOverGo, report);

            var dragReceiver = pointer.pointerDrag;
            pointer.position = end;
            pointer.delta = end - start;
            if (dragReceiver != null)
            {
                RecordEvent(report, "beginDrag", dragReceiver, FindResponders<IBeginDragHandler>(dragReceiver, EventTriggerType.BeginDrag));
                ExecuteEvents.Execute(dragReceiver, pointer, ExecuteEvents.beginDragHandler);
                pointer.dragging = true;

                // Once a drag starts on something other than the pressed object, the input module releases
                // the press there, so a Button inside a ScrollRect never fires its click.
                if (pointer.pointerPress != dragReceiver && pointer.pointerPress != null)
                {
                    ExecuteEvents.Execute(pointer.pointerPress, pointer, ExecuteEvents.pointerUpHandler);
                    pointer.eligibleForClick = false;
                    pointer.pointerPress = null;
                    pointer.rawPointerPress = null;
                }

                var dragResponders = FindResponders<IDragHandler>(dragReceiver, EventTriggerType.Drag);
                ExecuteEvents.Execute(dragReceiver, pointer, ExecuteEvents.dragHandler);
                RecordEvent(report, "drag", dragReceiver, dragResponders);
                if (dragResponders.Length > 0)
                {
                    report.DragResponder = dragReceiver;
                }
            }
            else
            {
                RecordEvent(report, "drag", null, Array.Empty<Component>());
            }

            if (pointer.pointerPress != null)
            {
                ExecuteEvents.Execute(pointer.pointerPress, pointer, ExecuteEvents.pointerUpHandler);
            }

            if (dragReceiver != null)
            {
                var dropTarget = RaycastTopHit(eventSystem, end);
                if (dropTarget != null)
                {
                    ExecuteEvents.ExecuteHierarchy(dropTarget, pointer, ExecuteEvents.dropHandler);
                }

                ExecuteEvents.Execute(dragReceiver, pointer, ExecuteEvents.endDragHandler);
                RecordEvent(report, "endDrag", dragReceiver, FindResponders<IEndDragHandler>(dragReceiver, EventTriggerType.EndDrag));
            }

            ReleasePointer(pointer);
            ExitHierarchy(pointer);

            if (report.DragResponder == null)
            {
                report.Warnings.Add(dragReceiver == null
                    ? $"Drag started on '{GetTransformPath(currentOverGo.transform)}', but nothing there or above it handles drag (no IDragHandler: ScrollRect, Slider, Scrollbar or a custom drag handler), so nothing moved."
                    : $"Drag reached '{GetTransformPath(dragReceiver.transform)}', but no component there responded to OnDrag (an EventTrigger needs a Drag entry with listeners).");
            }

            return report;
        }

        // A real pointer event carries the raycast that produced it, and handlers read it —
        // eventData.pointerCurrentRaycast.gameObject to identify what was hit, .worldPosition to place
        // something in the world. Use the raycast hit on the target (or, for a screen position, inside it);
        // when the target is reached by path and nothing at its centre hits it, describe the target itself.
        private static PointerEventData CreatePressPointer(
            EventSystem eventSystem,
            GameObject target,
            Vector2 screenPosition,
            bool targetFromScreenPosition,
            out GameObject currentOverGo)
        {
            var pointer = new PointerEventData(eventSystem)
            {
                button = PointerEventData.InputButton.Left,
                position = screenPosition,
                delta = Vector2.zero,
            };

            var results = new List<RaycastResult>();
            try
            {
                eventSystem.RaycastAll(pointer, results);
            }
            catch (Exception)
            {
                // A raycaster that throws must not take the interaction down with it.
                results.Clear();
            }

            var hitIndex = results.FindIndex(result => result.gameObject != null
                && (targetFromScreenPosition
                    ? IsSelfOrDescendant(result.gameObject, target)
                    : result.gameObject == target));
            if (hitIndex >= 0)
            {
                pointer.pointerCurrentRaycast = results[hitIndex];
                currentOverGo = targetFromScreenPosition ? results[hitIndex].gameObject : target;
            }
            else
            {
                pointer.pointerCurrentRaycast = new RaycastResult
                {
                    gameObject = target,
                    module = FindRaycasterFor(target),
                    screenPosition = screenPosition,
                    worldPosition = target.transform.position,
                    worldNormal = -target.transform.forward,
                    displayIndex = 0,
                };
                currentOverGo = target;
            }

            return pointer;
        }

        private static GameObject? Press(EventSystem eventSystem, PointerEventData pointer, GameObject currentOverGo, PointerDispatchReport report)
        {
            pointer.eligibleForClick = true;
            pointer.delta = Vector2.zero;
            pointer.dragging = false;
            pointer.useDragThreshold = true;
            pointer.pressPosition = pointer.position;
            pointer.pointerPressRaycast = pointer.pointerCurrentRaycast;

            // Pressing somewhere that cannot take selection deselects the current selection, as the input
            // modules do; an InputField loses focus when the player taps elsewhere.
            var selectHandler = ExecuteEvents.GetEventHandler<ISelectHandler>(currentOverGo);
            if (selectHandler != eventSystem.currentSelectedGameObject)
            {
                eventSystem.SetSelectedGameObject(null, pointer);
            }

            var downReceiver = ExecuteEvents.GetEventHandler<IPointerDownHandler>(currentOverGo);
            var downResponders = FindResponders<IPointerDownHandler>(downReceiver, EventTriggerType.PointerDown);
            var pressed = ExecuteEvents.ExecuteHierarchy(currentOverGo, pointer, ExecuteEvents.pointerDownHandler);
            RecordEvent(report, "pointerDown", downReceiver, downResponders);
            report.PressHandled = HasPressResponder(downResponders);

            var clickReceiver = ExecuteEvents.GetEventHandler<IPointerClickHandler>(currentOverGo);
            if (pressed == null)
            {
                pressed = clickReceiver;
            }

            pointer.clickCount = 1;
            pointer.clickTime = Time.unscaledTime;
            pointer.pointerPress = pressed;
            pointer.rawPointerPress = currentOverGo;
            pointer.pointerClick = clickReceiver;
            pointer.pointerDrag = ExecuteEvents.GetEventHandler<IDragHandler>(currentOverGo);
            if (pointer.pointerDrag != null)
            {
                ExecuteEvents.Execute(pointer.pointerDrag, pointer, ExecuteEvents.initializePotentialDrag);
            }

            return downResponders.Length > 0 ? downReceiver : null;
        }

        private static void ReleasePointer(PointerEventData pointer)
        {
            pointer.eligibleForClick = false;
            pointer.pointerPress = null;
            pointer.rawPointerPress = null;
            pointer.pointerClick = null;
            pointer.dragging = false;
            pointer.pointerDrag = null;
        }

        private static void EnterHierarchy(PointerEventData pointer, GameObject currentOverGo)
        {
            for (var current = currentOverGo.transform; current != null; current = current.parent)
            {
                ExecuteEvents.Execute(current.gameObject, pointer, ExecuteEvents.pointerEnterHandler);
                pointer.hovered.Add(current.gameObject);
            }

            pointer.pointerEnter = currentOverGo;
        }

        // A lifted finger leaves the element, so hover state (a Button's highlighted tint) does not linger
        // after the tap the way it would under a parked mouse cursor.
        private static void ExitHierarchy(PointerEventData pointer)
        {
            foreach (var hovered in pointer.hovered.ToArray())
            {
                if (hovered != null)
                {
                    ExecuteEvents.Execute(hovered, pointer, ExecuteEvents.pointerExitHandler);
                }
            }

            pointer.hovered.Clear();
            pointer.pointerEnter = null;
        }

        private static GameObject? RaycastTopHit(EventSystem eventSystem, Vector2 screenPosition)
        {
            var results = new List<RaycastResult>();
            try
            {
                eventSystem.RaycastAll(new PointerEventData(eventSystem) { position = screenPosition }, results);
            }
            catch (Exception)
            {
                return null;
            }

            return results.Count == 0 ? null : results[0].gameObject;
        }

        private static BaseRaycaster? FindRaycasterFor(GameObject target)
        {
            for (var current = target.transform; current != null; current = current.parent)
            {
                if (current.TryGetComponent<Canvas>(out _) && current.TryGetComponent<BaseRaycaster>(out var raycaster))
                {
                    return raycaster;
                }
            }

            return null;
        }

        private static bool IsSelfOrDescendant(GameObject candidate, GameObject ancestor)
        {
            return candidate == ancestor || candidate.transform.IsChildOf(ancestor.transform);
        }

        // ExecuteEvents.Execute reports "handled" whenever some component implements the interface. An
        // EventTrigger implements all of them, so a trigger with only PointerDown/PointerUp entries swallowed
        // a click and the tool claimed success while nothing ran. A component counts here only if it will do
        // something: an EventTrigger needs an entry for this event with listeners, a plain Button needs onClick
        // listeners.
        internal static Component[] FindResponders<THandler>(GameObject? receiver, EventTriggerType triggerType)
            where THandler : IEventSystemHandler
        {
            if (receiver == null)
            {
                return Array.Empty<Component>();
            }

            return receiver.GetComponents<Component>()
                .Where(component => component is THandler && (component is not Behaviour behaviour || behaviour.isActiveAndEnabled))
                .Where(component => WillRespond(component, triggerType))
                .ToArray();
        }

        private static bool WillRespond(Component component, EventTriggerType triggerType)
        {
            switch (component)
            {
                case EventTrigger trigger:
                    return HasTriggerListeners(trigger, triggerType);
                case Selectable selectable when !selectable.IsInteractable():
                    return false;
                case Button button when button.GetType() == typeof(Button) && triggerType == EventTriggerType.PointerClick:
                    return CountListeners(button.onClick) != 0;
                default:
                    return true;
            }
        }

        private static bool HasTriggerListeners(EventTrigger trigger, EventTriggerType triggerType)
        {
            return trigger.triggers.Any(entry => entry.eventID == triggerType
                && entry.callback != null
                && CountListeners(entry.callback) != 0);
        }

        // Listener count of a UnityEvent: enabled persistent calls plus AddListener calls. GetPersistentEventCount
        // alone misses every listener added from code, which is how games usually wire EventTriggers. Null when
        // the runtime list cannot be read, so the caller can treat the count as unknown instead of zero.
        internal static int? CountListeners(UnityEventBase unityEvent)
        {
            var persistent = 0;
            for (var index = 0; index < unityEvent.GetPersistentEventCount(); index++)
            {
                if (unityEvent.GetPersistentListenerState(index) != UnityEventCallState.Off)
                {
                    persistent++;
                }
            }

            var calls = UnityEventCallsField?.GetValue(unityEvent);
            var runtimeCalls = calls?.GetType().GetField("m_RuntimeCalls", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(calls) as ICollection;
            if (runtimeCalls == null)
            {
                return persistent > 0 ? persistent : null;
            }

            return persistent + runtimeCalls.Count;
        }

        private static bool HasPressResponder(Component[] responders)
        {
            return responders.Any(component => component is not Selectable);
        }

        private static bool IsPressDrivenControl(Component component)
        {
            var type = component.GetType();
            for (var current = type; current != null; current = current.BaseType)
            {
                if (PressDrivenControlTypeNames.Contains(current.FullName, StringComparer.Ordinal))
                {
                    return component is Selectable selectable && selectable.isActiveAndEnabled && selectable.IsInteractable();
                }
            }

            return false;
        }

        private static void RecordEvent(PointerDispatchReport report, string eventName, GameObject? receiver, Component[] responders)
        {
            report.Events.Add(eventName + ":" + (responders.Length == 0
                ? "none"
                : string.Join("+", responders.Select(component => component.GetType().Name).Distinct(StringComparer.Ordinal))));
            report.Details.Add(new Dictionary<string, object?>
            {
                ["event"] = eventName,
                ["receiver"] = receiver == null ? null : GetTransformPath(receiver.transform),
                ["responders"] = responders.Select(component => component.GetType().Name).ToArray(),
            });
        }

        private static void AddTapWarnings(PointerDispatchReport report, GameObject target, GameObject? clickReceiver, Component[] clickResponders)
        {
            if (report.Clicked)
            {
                return;
            }

            var targetPath = GetTransformPath(target.transform);
            var receiverPath = clickReceiver == null ? targetPath : GetTransformPath(clickReceiver.transform);
            if (clickReceiver != null
                && clickReceiver.TryGetComponent<Selectable>(out var selectable)
                && !selectable.IsInteractable())
            {
                report.Warnings.Add($"'{receiverPath}' is not interactable (Selectable.interactable is off, or a parent CanvasGroup disables interaction), so the click did nothing.");
                return;
            }

            if (report.PressHandled)
            {
                report.Warnings.Add(
                    $"PointerDown/PointerUp reached handlers on '{targetPath}' ({string.Join(", ", report.Events)}), but nothing handles PointerClick there. "
                    + "A control driven by press and release (held buttons, or ones that poll a pressed state in Update or read Input/touches) does not react to a synthetic same-frame tap; "
                    + "use input-control-mouse-event or input-control-touch-event for real input, then check the result with a screenshot.");
                return;
            }

            if (clickReceiver != null && clickResponders.Length == 0)
            {
                report.Warnings.Add(clickReceiver.GetComponent<EventTrigger>() != null
                    ? $"Click reached the EventTrigger on '{receiverPath}', but it has no PointerClick entry with listeners, so nothing ran."
                    : $"Click reached '{receiverPath}', but its click handler has no listeners (Button.onClick is empty), so nothing ran.");
                return;
            }

            report.Warnings.Add($"Click dispatched at '{targetPath}' but no recognized uGUI handler responded; the target is a raycast catcher (background, panel, label) with no click/submit handler, so nothing was triggered.");
        }
    }
}
