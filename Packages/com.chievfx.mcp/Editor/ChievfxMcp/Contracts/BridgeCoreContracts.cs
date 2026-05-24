#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Chievfx.Mcp.Editor
{
    internal sealed class BridgeRequest
    {
        public string? id { get; set; }

        public string? toolName { get; set; }

        public JToken? arguments { get; set; }

        public int timeoutMs { get; set; }
    }

    internal sealed class BridgeEventStream
    {
        public int schemaVersion { get; set; } = 1;

        public long lastEventId { get; set; }

        public long truncatedBeforeEventId { get; set; }

        public List<BridgeEventRecord> events { get; set; } = new();
    }

    internal sealed class BridgeEventRecord
    {
        public long eventId { get; set; }

        public string timestamp { get; set; } = string.Empty;

        public string source { get; set; } = string.Empty;

        public string type { get; set; } = string.Empty;

        public string level { get; set; } = string.Empty;

        public string message { get; set; } = string.Empty;

        public string? marker { get; set; }

        public string? operationId { get; set; }

        public Dictionary<string, object?>? data { get; set; }
    }

    internal sealed class ImageResult
    {
        public ImageResult(string mimeType, string base64)
            : this(mimeType, base64, null)
        {
        }

        public ImageResult(string mimeType, string base64, Dictionary<string, object?>? metadata)
        {
            MimeType = mimeType;
            Base64 = base64;
            Metadata = metadata;
        }

        public string MimeType { get; }

        public string Base64 { get; }

        public Dictionary<string, object?>? Metadata { get; }
    }

    internal sealed class EditorWindowCaptureResult
    {
        public EditorWindowCaptureResult(string backend, byte[] png, Rect captureRect, Rect capturePixelRect)
        {
            Backend = backend;
            Png = png;
            CaptureRect = captureRect;
            CapturePixelRect = capturePixelRect;
        }

        public string Backend { get; }

        public byte[] Png { get; }

        public Rect CaptureRect { get; }

        public Rect CapturePixelRect { get; }
    }

    internal sealed class TemporaryOverlayCanvasCameraScope : IDisposable
    {
        private readonly List<TemporaryOverlayCanvasState> states;
        private bool disposed;

        public TemporaryOverlayCanvasCameraScope(List<TemporaryOverlayCanvasState> states)
        {
            this.states = states;
        }

        public int CanvasCount => states.Count;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            foreach (var state in states)
            {
                try
                {
                    state.RenderModeProperty.SetValue(state.Canvas, state.RenderMode);
                    state.WorldCameraProperty.SetValue(state.Canvas, state.WorldCamera);
                    state.PlaneDistanceProperty?.SetValue(state.Canvas, state.PlaneDistance);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"ChievFX MCP failed to restore temporary Canvas capture state. {ex.GetBaseException().Message}");
                }
            }
        }
    }

    internal sealed class TemporaryOverlayCanvasState
    {
        public TemporaryOverlayCanvasState(
            Component canvas,
            PropertyInfo renderModeProperty,
            PropertyInfo worldCameraProperty,
            PropertyInfo? planeDistanceProperty,
            object? renderMode,
            object? worldCamera,
            object? planeDistance)
        {
            Canvas = canvas;
            RenderModeProperty = renderModeProperty;
            WorldCameraProperty = worldCameraProperty;
            PlaneDistanceProperty = planeDistanceProperty;
            RenderMode = renderMode;
            WorldCamera = worldCamera;
            PlaneDistance = planeDistance;
        }

        public Component Canvas { get; }

        public PropertyInfo RenderModeProperty { get; }

        public PropertyInfo WorldCameraProperty { get; }

        public PropertyInfo? PlaneDistanceProperty { get; }

        public object? RenderMode { get; }

        public object? WorldCamera { get; }

        public object? PlaneDistance { get; }
    }

    internal enum EditorWindowCaptureArea
    {
        View,
        Content,
        Window
    }

    internal sealed class EditorWindowScreenshotSettings
    {
        public EditorWindowTargetSpec Target { get; set; } = new();

        public bool OpenIfMissing { get; set; }

        public bool SelectDockedTab { get; set; } = true;

        public EditorWindowCaptureArea CaptureArea { get; set; } = EditorWindowCaptureArea.View;

        public string CaptureAreaText { get; set; } = "view";

        public int DelayFrames { get; set; } = McpLimits.DefaultEditorWindowScreenshotDelayFrames;

        public int DelayMs { get; set; } = McpLimits.DefaultEditorWindowScreenshotDelayMs;

        public bool DelayFramesExplicit { get; set; }

        public bool DelayMsExplicit { get; set; }

        public string WaitStrategy { get; set; } = "default-conservative-delay";

        public int MaxDimension { get; set; } = McpLimits.MaxScreenshotDimension;
    }

    internal sealed class EditorWindowTargetSpec
    {
        public string Source { get; set; } = "focused";

        public int? InstanceId { get; set; }

        public string? TypeName { get; set; }

        public string? TitleContains { get; set; }

        public string? MenuPath { get; set; }

        public bool Focused { get; set; }

        public bool MouseOver { get; set; }
    }

    internal sealed class PendingEditorWindowScreenshotRequest
    {
        public string Id { get; set; } = string.Empty;

        public DateTime StartedUtc { get; set; }

        public double StartedEditorTime { get; set; }

        public long StartedEditorUpdateTick { get; set; }

        public int TimeoutMs { get; set; } = 30000;

        public bool Completed { get; set; }

        public EditorWindow? Window { get; set; }

        public EditorWindowCaptureArea CaptureArea { get; set; } = EditorWindowCaptureArea.View;

        public string RequestedCaptureArea { get; set; } = "view";

        public int MaxDimension { get; set; } = McpLimits.MaxScreenshotDimension;

        public long DueEditorUpdateTick { get; set; }

        public double DueEditorTime { get; set; }

        public int EffectiveDelayFrames { get; set; }

        public int EffectiveDelayMs { get; set; }

        public bool DelayFramesExplicit { get; set; }

        public bool DelayMsExplicit { get; set; }

        public string WaitStrategy { get; set; } = "default-conservative-delay";

        public bool SelectedDockedTab { get; set; }

        public object? PreviousDockArea { get; set; }

        public EditorWindow? PreviousSelectedDockedWindow { get; set; }

        public List<string> Warnings { get; set; } = new();

        public List<string> Diagnostics { get; set; } = new();
    }
}
