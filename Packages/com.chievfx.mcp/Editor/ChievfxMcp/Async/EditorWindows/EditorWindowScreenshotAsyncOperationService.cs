#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Chievfx.Mcp.Editor
{
    internal sealed class EditorWindowScreenshotAsyncOperationService : BridgeDomainServiceBase
    {
        private readonly List<PendingEditorWindowScreenshotRequest> pendingRequests = new();

            public bool HasActiveRequests => pendingRequests.Any(pending => !pending.Completed);

            public void StartEditorWindowScreenshotRequest(string id, JToken args, int timeoutMs)
        {
            if (pendingRequests.Any(pending => !pending.Completed))
            {
                throw new InvalidOperationException("A screenshot-editor-window request is already running. Wait for it to finish before starting another.");
            }

            var settings = EditorWindowBridgeService.ReadEditorWindowScreenshotSettings(args);
            var warnings = new List<string>();
            var diagnostics = new List<string>();
            if (HasProperty(args, "focus"))
            {
                diagnostics.Add("screenshot-editor-window ignores focus; use editor-window-focus for deliberate focus changes.");
            }

            var window = EditorWindowBridgeService.ResolveEditorWindowScreenshotTarget(settings.Target, settings.OpenIfMissing, diagnostics);
            var hostViewBeforeSelection = EditorWindowBridgeService.GetEditorWindowHostView(window);
            var previousSelectedDockedWindow = EditorWindowBridgeService.IsDockArea(hostViewBeforeSelection)
                ? EditorWindowBridgeService.GetDockAreaSelectedWindow(hostViewBeforeSelection)
                : null;
            var selectedDockedTab = EditorWindowBridgeService.PrepareEditorWindowForScreenshot(window, settings.SelectDockedTab, diagnostics);

            var pendingRequest = new PendingEditorWindowScreenshotRequest
            {
                Id = id,
                StartedUtc = DateTime.UtcNow,
                StartedEditorTime = EditorApplication.timeSinceStartup,
                StartedEditorUpdateTick = RuntimeState.EditorUpdateTick,
                TimeoutMs = timeoutMs > 0 ? timeoutMs : 30000,
                Window = window,
                CaptureArea = settings.CaptureArea,
                RequestedCaptureArea = settings.CaptureAreaText,
                MaxDimension = settings.MaxDimension,
                DueEditorUpdateTick = RuntimeState.EditorUpdateTick + settings.DelayFrames,
                DueEditorTime = EditorApplication.timeSinceStartup + settings.DelayMs / 1000d,
                EffectiveDelayFrames = settings.DelayFrames,
                EffectiveDelayMs = settings.DelayMs,
                DelayFramesExplicit = settings.DelayFramesExplicit,
                DelayMsExplicit = settings.DelayMsExplicit,
                WaitStrategy = settings.WaitStrategy,
                SelectedDockedTab = selectedDockedTab,
                PreviousDockArea = hostViewBeforeSelection,
                PreviousSelectedDockedWindow = previousSelectedDockedWindow,
                Warnings = warnings,
                Diagnostics = diagnostics
            };

            pendingRequests.Add(pendingRequest);
            OperationStore.MarkWaiting(
                id,
                $"Waiting for Unity editor repaint before EditorWindow screenshot capture ({settings.WaitStrategy}: {EditorWindowBridgeService.FormatEditorWindowScreenshotWait(settings.DelayFrames, settings.DelayMs)}).",
                true);
        }

        
            public void ProcessPendingEditorWindowScreenshotRequests()
        {
            for (var i = pendingRequests.Count - 1; i >= 0; i--)
            {
                var pending = pendingRequests[i];
                if (pending.Completed)
                {
                    continue;
                }

                if (OperationStore.IsCancellationRequested(pending.Id))
                {
                    pending.Completed = true;
                    EditorWindowBridgeService.RestoreEditorWindowScreenshotSelection(pending);
                    OperationStore.Complete(pending.Id, "cancelled", "screenshot-editor-window cancellation requested.");
                    Transport.WriteResponse(pending.Id, new { ok = false, error = "screenshot-editor-window cancelled." });
                    pendingRequests.RemoveAt(i);
                    continue;
                }

                var elapsedMs = (EditorApplication.timeSinceStartup - pending.StartedEditorTime) * 1000d;
                if (elapsedMs > pending.TimeoutMs)
                {
                    pending.Completed = true;
                    EditorWindowBridgeService.RestoreEditorWindowScreenshotSelection(pending);
                    OperationStore.Complete(pending.Id, "failed", $"screenshot-editor-window timed out after {pending.TimeoutMs} ms.");
                    Transport.WriteResponse(pending.Id, new { ok = false, error = $"screenshot-editor-window timed out after {pending.TimeoutMs} ms." });
                    pendingRequests.RemoveAt(i);
                    continue;
                }

                if (RuntimeState.EditorUpdateTick < pending.DueEditorUpdateTick || EditorApplication.timeSinceStartup < pending.DueEditorTime)
                {
                    EditorWindowBridgeService.RequestEditorWindowScreenshotRepaint(pending.Window);
                    continue;
                }

                try
                {
                    Transport.WriteResponse(pending.Id, EditorWindowBridgeService.RunEditorWindowScreenshotForResponse(pending));
                    OperationStore.Complete(pending.Id, "completed", "screenshot-editor-window completed.");
                }
                catch (Exception ex)
                {
                    OperationStore.Complete(pending.Id, "failed", ex.GetBaseException().Message);
                    Transport.WriteResponse(pending.Id, new { ok = false, error = ex.GetBaseException().Message });
                }
                finally
                {
                    EditorWindowBridgeService.RestoreEditorWindowScreenshotSelection(pending);
                    pending.Completed = true;
                    pendingRequests.RemoveAt(i);
                }
            }
        }

    }
}
