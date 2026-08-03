#nullable enable
using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Chievfx.Mcp.Editor
{
    internal sealed class BridgeFileTransport
    {
        private readonly BridgeRuntimeState runtimeState;
        private readonly BridgeOperationStore operationStore;
        private readonly BridgeFileTransportHandlers handlers;

        public BridgeFileTransport(
            BridgeRuntimeState runtimeState,
            BridgeOperationStore operationStore,
            BridgeFileTransportHandlers handlers)
        {
            this.runtimeState = runtimeState;
            this.operationStore = operationStore;
            this.handlers = handlers;
        }

        public void ProcessRequests()
        {
            if (!runtimeState.IsRunning)
            {
                return;
            }

            runtimeState.WriteHeartbeatIfDue(
                operationStore,
                handlers.EventJournal,
                new BridgeRuntimeBusyStatus(
                    runtimeState.PackageOperations.HasPendingRequests,
                    runtimeState.TestOperations.HasActiveRequests,
                    runtimeState.EditorWindowScreenshotOperations.HasActiveRequests,
                    runtimeState.ScriptOperations.IsBusy()));
            runtimeState.IncrementEditorUpdateTick();

            // Fires a recompile that was owed from before a Play Mode exit, once the editor is back in
            // edit mode. Checked on the tick rather than from a playModeStateChanged handler so it
            // works whether or not the exit domain-reloaded.
            BridgePendingRecompile.ProcessIfDue(handlers.EventJournal);
            handlers.ProcessPendingPackageRequests();
            handlers.ProcessPendingTestRequests();
            handlers.ProcessPendingScriptInvocationRequests();
            handlers.ProcessPendingEditorWindowScreenshotRequests();

            if (!Directory.Exists(ChievfxMcpToolPolicy.BridgeRequestDirectory))
            {
                handlers.EnsureStarted();
                return;
            }

            foreach (var requestPath in Directory.GetFiles(ChievfxMcpToolPolicy.BridgeRequestDirectory, "*.json").OrderBy(File.GetCreationTimeUtc))
            {
                ProcessRequestFile(requestPath);
            }
        }

        public void WriteResponse(string id, object payload)
        {
            Directory.CreateDirectory(ChievfxMcpToolPolicy.BridgeResponseDirectory);
            var responsePath = Path.Combine(ChievfxMcpToolPolicy.BridgeResponseDirectory, id + ".json");
            BridgeRuntimeState.WriteAllTextAtomic(responsePath, JsonConvert.SerializeObject(payload, BridgeRuntimeState.JsonOptions));
        }

        private void ProcessRequestFile(string requestPath)
        {
            var requestFileId = Path.GetFileNameWithoutExtension(requestPath);
            var processingPath = requestPath + ".processing";
            try
            {
                if (operationStore.IsCancellationRequested(requestFileId))
                {
                    CancelQueuedOperation(requestFileId, "Cancellation requested before Unity started the operation.");
                    File.Delete(requestPath);
                    return;
                }

                File.Move(requestPath, processingPath);
                var request = JsonConvert.DeserializeObject<BridgeRequest>(File.ReadAllText(processingPath), BridgeRuntimeState.JsonOptions)
                    ?? throw new InvalidOperationException("Bridge request is empty.");

                var id = request.id;
                if (string.IsNullOrWhiteSpace(id))
                {
                    throw new InvalidOperationException("Bridge request id is required.");
                }

                var toolName = request.toolName;
                if (string.IsNullOrWhiteSpace(toolName))
                {
                    throw new InvalidOperationException("Bridge request toolName is required.");
                }

                if (operationStore.IsCancellationRequested(id!))
                {
                    CancelQueuedOperation(id!, $"Cancellation requested before '{toolName}' started.");
                    return;
                }

                operationStore.MarkRunning(id!, toolName!, request.timeoutMs);
                var args = request.arguments ?? new JObject();
                if (handlers.IsPackageTool(toolName!))
                {
                    handlers.StartPackageToolRequest(id!, toolName!, args);
                }
                else if (handlers.IsTestTool(toolName!))
                {
                    handlers.StartTestToolRequest(id!, toolName!, args);
                }
                else if (handlers.IsScriptTool(toolName!))
                {
                    handlers.StartScriptToolRequest(id!, args);
                }
                else if (string.Equals(toolName, "screenshot-editor-window", StringComparison.Ordinal))
                {
                    handlers.StartEditorWindowScreenshotRequest(id!, args, request.timeoutMs);
                }
                else
                {
                    WriteResponse(id!, RunToolForResponse(toolName!, args));
                    operationStore.Complete(id!, "completed", $"{toolName} completed.");
                }
            }
            catch (Exception ex)
            {
                var id = requestFileId;
                operationStore.Complete(id, "failed", ex.GetBaseException().Message);
                WriteResponse(id, new { ok = false, error = ex.GetBaseException().Message });
            }
            finally
            {
                if (File.Exists(processingPath))
                {
                    File.Delete(processingPath);
                }
            }
        }

        private object RunToolForResponse(string toolName, JToken args)
        {
            var result = handlers.RunTool(toolName, args);
            if (result is ImageResult image)
            {
                return new
                {
                    ok = true,
                    contentType = "image",
                    mimeType = image.MimeType,
                    base64 = image.Base64,
                    metadata = image.Metadata
                };
            }

            return new
            {
                ok = true,
                contentType = "json",
                result
            };
        }

        private void CancelQueuedOperation(string id, string message)
        {
            operationStore.Complete(id, "cancelled", message);
            WriteResponse(id, new { ok = false, error = $"Bridge operation {id} cancelled before start." });
        }
    }

    internal sealed class BridgeFileTransportHandlers
    {
        public BridgeEventJournal EventJournal { get; set; } = null!;

        public Action EnsureStarted { get; set; } = null!;

        public Func<string, JToken, object?> RunTool { get; set; } = null!;

        public Func<string, JToken, object> ScheduleRefreshAssets { get; set; } = null!;

        public Func<string, bool> IsPackageTool { get; set; } = null!;

        public Action<string, string, JToken> StartPackageToolRequest { get; set; } = null!;

        public Action ProcessPendingPackageRequests { get; set; } = null!;

        public Func<string, bool> IsTestTool { get; set; } = null!;

        public Action<string, string, JToken> StartTestToolRequest { get; set; } = null!;

        public Action ProcessPendingTestRequests { get; set; } = null!;

        public Func<string, bool> IsScriptTool { get; set; } = null!;

        public Action<string, JToken> StartScriptToolRequest { get; set; } = null!;

        public Action ProcessPendingScriptInvocationRequests { get; set; } = null!;

        public Action<string, JToken, int> StartEditorWindowScreenshotRequest { get; set; } = null!;

        public Action ProcessPendingEditorWindowScreenshotRequests { get; set; } = null!;
    }
}
