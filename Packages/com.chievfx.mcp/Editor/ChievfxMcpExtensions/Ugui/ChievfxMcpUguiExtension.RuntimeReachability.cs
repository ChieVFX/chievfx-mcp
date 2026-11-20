#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using UnityEngine;
using UnityEngine.EventSystems;
using static Chievfx.Mcp.Extensions.Ugui.UguiRuntimeControlHelpers;
using static Chievfx.Mcp.Extensions.Ugui.UguiRuntimeHelpers;
using static Chievfx.Mcp.Extensions.Ugui.UguiSharedHelpers;

namespace Chievfx.Mcp.Extensions.Ugui
{
    /// <summary>
    /// Says when an element the runtime tools acted on is one a player could not see or touch. Tools reach a
    /// target by path straight through CanvasGroups and disabled canvases, so typing into a field under an
    /// alpha 0, blocksRaycasts off group "succeeded" while the player had no way to do the same.
    /// </summary>
    internal static class UguiRuntimeReachability
    {
        // A player can neither see nor tap it: switched off, under a disabled Canvas, or under CanvasGroups that are
        // fully transparent or block raycasts. The overlap fallback of a missed raycast must not pick such an element.
        internal static bool IsHiddenFromPlayer(GameObject target)
        {
            if (!target.activeInHierarchy)
            {
                return true;
            }

            var alpha = 1f;
            var groupsIgnored = false;
            for (var current = target.transform; current != null; current = current.parent)
            {
                if (current.TryGetComponent<Canvas>(out var canvas) && !canvas.enabled)
                {
                    return true;
                }

                if (groupsIgnored)
                {
                    continue;
                }

                foreach (var group in current.GetComponents<CanvasGroup>())
                {
                    if (!group.enabled)
                    {
                        continue;
                    }

                    alpha *= group.alpha;
                    if (!group.blocksRaycasts)
                    {
                        return true;
                    }

                    groupsIgnored |= group.ignoreParentGroups;
                }
            }

            return alpha <= 0.001f;
        }

        // What a tap at the control's centre lands on when that is not the control: a window drawn over it (a modal's
        // full-screen backdrop). Tools reach a control by path straight through such a window; a player cannot. A hit on
        // the control's own child, or on anything whose click goes to the same receiver, is the control's own tap.
        internal static bool TryFindCover(GameObject target, UguiDependencyStatus status, [NotNullWhen(true)] out GameObject? cover, out int sortingOrder)
        {
            cover = null;
            sortingOrder = 0;
            var eventSystem = EventSystem.current;
            if (eventSystem == null || !UguiRuntimeTools.TryGetTargetScreenCenter(target, status, out var center))
            {
                return false;
            }

            var results = new List<RaycastResult>();
            try
            {
                UguiTargetSizeRaycast.RaycastAll(eventSystem, new PointerEventData(eventSystem) { position = center }, results);
            }
            catch (Exception)
            {
                return false;
            }

            if (results.Count == 0 || results[0].gameObject is not GameObject top
                || top == target || top.transform.IsChildOf(target.transform))
            {
                return false;
            }

            var receiver = ExecuteEvents.GetEventHandler<IPointerClickHandler>(target);
            if (receiver != null && ExecuteEvents.GetEventHandler<IPointerClickHandler>(top) == receiver)
            {
                return false;
            }

            cover = top;
            sortingOrder = results[0].sortingOrder;
            return true;
        }

        internal static void AddCoverWarning(GameObject target, UguiDependencyStatus status, List<string> warnings)
        {
            if (TryFindCover(target, status, out var cover, out var sortingOrder))
            {
                warnings.Add($"'{GetTransformPath(target.transform)}' is covered at its centre by '{GetTransformPath(cover.transform)}' (sorting order {sortingOrder}): "
                    + "a player's tap there lands on that, not on this control. The tool acted on it anyway.");
            }
        }

        internal static void AddPlayerReachabilityWarnings(GameObject target, UguiDependencyStatus status, List<string> warnings)
        {
            var path = GetTransformPath(target.transform);
            var alpha = 1f;
            string? fadedBy = null;
            string? blockedBy = null;
            string? lockedBy = null;
            string? disabledCanvas = null;
            string? switchedOffAt = null;
            var groupsIgnored = false;
            for (var current = target.transform; current != null; current = current.parent)
            {
                // The outermost switched-off object is the one that hides the whole branch.
                if (!current.gameObject.activeSelf)
                {
                    switchedOffAt = GetTransformPath(current);
                }

                // TryGetComponent: in the Editor a missing GetComponent<T>() result is a fake-null object that
                // passes an "is T" check and throws on first use.
                if (disabledCanvas == null && current.TryGetComponent<Canvas>(out var canvas) && !canvas.enabled)
                {
                    disabledCanvas = GetTransformPath(current);
                }

                if (groupsIgnored)
                {
                    continue;
                }

                foreach (var group in current.GetComponents<CanvasGroup>())
                {
                    if (!group.enabled)
                    {
                        continue;
                    }

                    alpha *= group.alpha;
                    if (fadedBy == null && group.alpha <= 0.001f)
                    {
                        fadedBy = GetTransformPath(current);
                    }

                    if (blockedBy == null && !group.blocksRaycasts)
                    {
                        blockedBy = GetTransformPath(current);
                    }

                    if (lockedBy == null && !group.interactable)
                    {
                        lockedBy = GetTransformPath(current);
                    }

                    groupsIgnored |= group.ignoreParentGroups;
                }
            }

            if (switchedOffAt != null)
            {
                warnings.Add(switchedOffAt == path
                    ? $"'{path}' is inactive (GameObject switched off), so it is not on screen. The tool acted on it anyway; a player could not."
                    : $"'{path}' is inactive: '{switchedOffAt}' is switched off, so the branch is not on screen. The tool acted on it anyway; a player could not.");
            }

            if (disabledCanvas != null)
            {
                warnings.Add($"'{path}' is not rendered: the Canvas on '{disabledCanvas}' is disabled. The tool acted on it anyway; a player could not.");
            }

            if (alpha <= 0.001f)
            {
                warnings.Add(fadedBy == null
                    ? $"'{path}' is invisible to the player: its CanvasGroups multiply to alpha 0. The tool acted on it anyway; a player could not."
                    : $"'{path}' is invisible to the player: the CanvasGroup on '{fadedBy}' has alpha 0. The tool acted on it anyway; a player could not.");
            }

            if (blockedBy != null)
            {
                warnings.Add($"'{path}' does not receive pointer input: the CanvasGroup on '{blockedBy}' has blocksRaycasts off, so a player cannot tap it.");
            }

            if (lockedBy != null)
            {
                warnings.Add($"'{path}' is not interactable: the CanvasGroup on '{lockedBy}' has interactable off.");
            }

            if (target.activeInHierarchy
                && disabledCanvas == null
                && target.GetComponent<RectTransform>() != null
                && !TryGetUguiScreenZone(target, status, ResolveRuntimeUiScreenSize(status), out _))
            {
                warnings.Add($"'{path}' is outside the visible screen area.");
            }
        }
    }
}
