#nullable enable
using System.Collections.Generic;
using UnityEngine;
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
        internal static void AddPlayerReachabilityWarnings(GameObject target, UguiDependencyStatus status, List<string> warnings)
        {
            var path = GetTransformPath(target.transform);
            var alpha = 1f;
            string? fadedBy = null;
            string? blockedBy = null;
            string? lockedBy = null;
            string? disabledCanvas = null;
            var groupsIgnored = false;
            for (var current = target.transform; current != null; current = current.parent)
            {
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
