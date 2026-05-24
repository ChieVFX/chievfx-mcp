#nullable enable
using static Chievfx.Mcp.Editor.McpLimits;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEditor.SceneManagement;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;
using PackageManagerClient = UnityEditor.PackageManager.Client;
using PackageManagerPackageInfo = UnityEditor.PackageManager.PackageInfo;


namespace Chievfx.Mcp.Editor
{
    internal sealed partial class TestAsyncOperationService : BridgeDomainServiceBase
    {
        private readonly List<PendingTestRequest> pendingRequests = new();

            public bool HasActiveRequests => pendingRequests.Any(pending => !pending.Completed);

        public bool IsTestTool(string toolName)
        {
            return string.Equals(toolName, "tests-run", StringComparison.Ordinal);
        }

        public void StartTestToolRequest(string id, string toolName, JToken args)
        {
            if (!string.Equals(toolName, "tests-run", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unknown test tool '{toolName}'.");
            }

            if (pendingRequests.Any(pending => !pending.Completed))
            {
                throw new InvalidOperationException("A tests-run request is already running. Wait for it to finish before starting another.");
            }

            ValidateNoDirtyOpenScenes();
            var timeoutMs = ClampInt(ReadInt(args, "timeoutMs", DefaultTestTimeoutMs), 1000, HardTestTimeoutMs);
            var pendingRequest = new PendingTestRequest
            {
                Id = id,
                StartedUtc = DateTime.UtcNow,
                TimeoutMs = timeoutMs,
                IncludePassingTests = ReadBool(args, "includePassingTests", false),
                IncludeMessages = ReadBool(args, "includeMessages", true),
                IncludeStackTrace = ReadBool(args, "includeStacktrace", ReadBool(args, "includeStackTrace", false)),
                IncludeLogs = ReadBool(args, "includeLogs", false),
                IncludeLogsStackTrace = ReadBool(args, "includeLogsStacktrace", false),
                LogType = ReadEnum(args, "logType", UnityEngine.LogType.Warning).ToString(),
                MaxResults = ClampInt(ReadInt(args, "maxResults", DefaultTestMaxResults), 1, HardTestMaxResults),
                LogStartIndex = ConsoleLogBridgeService.GetLogEntryCount()
            };

            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            var callbacks = new ChievfxTestCallbacks(id);
            pendingRequest.Api = api;
            pendingRequest.Callbacks = callbacks;
            pendingRequests.Add(pendingRequest);
            OperationStore.MarkWaiting(id, "Waiting for Unity Test Runner.", true);
            EventJournal.Write(
                "editor",
                "test-run-start",
                "info",
                "Unity Test Runner started.",
                operationId: id,
                data: new Dictionary<string, object?> { ["timeoutMs"] = timeoutMs });
            try
            {
                api.RegisterCallbacks(callbacks);
                api.Execute(new ExecutionSettings(CreateTestFilter(args)));
            }
            catch
            {
                CleanupTestRequest(pendingRequest);
                pendingRequests.Remove(pendingRequest);
                throw;
            }
        }

        private Filter CreateTestFilter(JToken args)
        {
            var testMode = ReadEnum(args, "testMode", TestMode.EditMode);
            var filter = new Filter
            {
                testMode = testMode
            };

            var testAssembly = ReadString(args, "testAssembly");
            if (!string.IsNullOrWhiteSpace(testAssembly))
            {
                filter.assemblyNames = new[] { testAssembly!.Trim() };
            }

            var testNamespace = ReadString(args, "testNamespace");
            var testClass = ReadString(args, "testClass");
            var testMethod = ReadString(args, "testMethod");
            if (!string.IsNullOrWhiteSpace(testMethod))
            {
                filter.testNames = new[] { BuildFullTestName(testNamespace, testClass, testMethod!) };
            }
            else if (!string.IsNullOrWhiteSpace(testClass))
            {
                filter.groupNames = new[] { BuildFullTestName(testNamespace, null, testClass!) };
            }
            else if (!string.IsNullOrWhiteSpace(testNamespace))
            {
                filter.groupNames = new[] { testNamespace!.Trim() };
            }

            return filter;
        }

        private string BuildFullTestName(string? testNamespace, string? testClass, string name)
        {
            var trimmedName = name.Trim();
            if (trimmedName.Contains(".", StringComparison.Ordinal))
            {
                return trimmedName;
            }

            var parts = new[] { testNamespace, testClass, trimmedName }
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .Select(part => part!.Trim());
            return string.Join(".", parts);
        }

        private void ValidateNoDirtyOpenScenes()
        {
            var dirtyScenes = new List<string>();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isDirty)
                {
                    dirtyScenes.Add(string.IsNullOrEmpty(scene.path) ? scene.name : scene.path);
                }
            }

            if (dirtyScenes.Count > 0)
            {
                throw new InvalidOperationException("tests-run requires all open scenes to be saved. Dirty scenes: " + string.Join(", ", dirtyScenes));
            }
        }

        public void ProcessPendingTestRequests()
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
                    CancelPendingTestRequest(pending);
                    pendingRequests.RemoveAt(i);
                    continue;
                }

                if ((DateTime.UtcNow - pending.StartedUtc).TotalMilliseconds <= pending.TimeoutMs)
                {
                    continue;
                }

                pending.Completed = true;
                OperationStore.Complete(pending.Id, "failed", $"tests-run timed out after {pending.TimeoutMs} ms.");
                EventJournal.Write(
                    "editor",
                    "test-run-finish",
                    "error",
                    $"Unity Test Runner timed out after {pending.TimeoutMs} ms.",
                    operationId: pending.Id,
                    data: new Dictionary<string, object?> { ["timeoutMs"] = pending.TimeoutMs });
                Transport.WriteResponse(pending.Id, new
                {
                    ok = false,
                    error = $"tests-run timed out after {pending.TimeoutMs} ms. Narrow filters or increase MCP/tool timeout."
                });
                CleanupTestRequest(pending);
                pendingRequests.RemoveAt(i);
            }
        }

        public void CompleteTestRun(string id, object result)
        {
            var pending = pendingRequests.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
            if (pending == null || pending.Completed)
            {
                return;
            }

            try
            {
                pending.Completed = true;
                Transport.WriteResponse(id, new
                {
                    ok = true,
                    contentType = "json",
                    result = CreateTestRunResult(pending, result)
                });
                OperationStore.Complete(id, "completed", "tests-run completed.");
                EventJournal.Write(
                    "editor",
                    "test-run-finish",
                    "info",
                    "Unity Test Runner completed.",
                    operationId: id);
            }
            catch (Exception ex)
            {
                OperationStore.Complete(id, "failed", ex.GetBaseException().Message);
                Transport.WriteResponse(id, new { ok = false, error = ex.GetBaseException().Message });
                EventJournal.Write(
                    "editor",
                    "test-run-finish",
                    "error",
                    $"Unity Test Runner failed. {ex.GetBaseException().Message}",
                    operationId: id);
            }
            finally
            {
                CleanupTestRequest(pending);
                pendingRequests.Remove(pending);
            }
        }

        private object CreateTestRunResult(PendingTestRequest pending, object rootResult)
        {
            var allResults = CollectLeafTestResults(rootResult);
            var failed = allResults.Count(result => string.Equals(result.Status, "Failed", StringComparison.Ordinal));
            var passed = allResults.Count(result => string.Equals(result.Status, "Passed", StringComparison.Ordinal));
            var skipped = allResults.Count(result => string.Equals(result.Status, "Skipped", StringComparison.Ordinal));
            var selectedResults = allResults
                .Where(result => pending.IncludePassingTests || !string.Equals(result.Status, "Passed", StringComparison.Ordinal))
                .Take(pending.MaxResults + 1)
                .ToArray();
            var truncated = selectedResults.Length > pending.MaxResults;

            return new
            {
                summary = new
                {
                    status = failed > 0 ? "Failed" : allResults.Count == 0 ? "Unknown" : "Passed",
                    totalTests = allResults.Count,
                    passedTests = passed,
                    failedTests = failed,
                    skippedTests = skipped,
                    duration = FormatDuration(ReflectionBridgeService.ReadReflectedDoubleProperty(rootResult, "Duration")),
                    noTests = allResults.Count == 0
                },
                results = selectedResults
                    .Take(pending.MaxResults)
                    .Select(result => CreateTestResultOutput(result, pending.IncludeMessages, pending.IncludeStackTrace))
                    .ToArray(),
                resultsTruncated = truncated,
                logs = ConsoleLogBridgeService.CreateLogOutputs(
                    pending.LogStartIndex,
                    pending.IncludeLogs,
                    pending.LogType,
                    pending.IncludeLogsStackTrace,
                    MaxTestLogEntries),
                durationMs = (int)Math.Round((DateTime.UtcNow - pending.StartedUtc).TotalMilliseconds),
                timeoutMs = pending.TimeoutMs
            };
        }

        private List<TestResultDto> CollectLeafTestResults(object rootResult)
        {
            var results = new List<TestResultDto>();
            CollectLeafTestResults(rootResult, results);
            return results;
        }

        private void CollectLeafTestResults(object result, List<TestResultDto> results)
        {
            var children = ReflectionBridgeService.ReadReflectedEnumerableProperty(result, "Children").ToArray();
            if (children.Length > 0)
            {
                foreach (var child in children)
                {
                    CollectLeafTestResults(child, results);
                }

                return;
            }

            results.Add(new TestResultDto
            {
                Name = ReflectionBridgeService.ReadReflectedStringProperty(result, "Name") ?? string.Empty,
                FullName = ReflectionBridgeService.ReadReflectedStringProperty(result, "FullName") ?? ReflectionBridgeService.ReadReflectedStringProperty(result, "Name") ?? string.Empty,
                Status = NormalizeTestStatus(Convert.ToString(ReflectionBridgeService.ReadReflectedProperty(result, "TestStatus"), CultureInfo.InvariantCulture) ?? string.Empty),
                Duration = ReflectionBridgeService.ReadReflectedDoubleProperty(result, "Duration"),
                Message = ReflectionBridgeService.ReadReflectedStringProperty(result, "Message") ?? string.Empty,
                StackTrace = ReflectionBridgeService.ReadReflectedStringProperty(result, "StackTrace") ?? string.Empty
            });
        }

        private object CreateTestResultOutput(TestResultDto result, bool includeMessage, bool includeStackTrace)
        {
            var output = new Dictionary<string, object?>
            {
                ["name"] = string.IsNullOrWhiteSpace(result.FullName) ? result.Name : result.FullName,
                ["status"] = result.Status,
                ["duration"] = FormatDuration(result.Duration)
            };

            if (includeMessage && !string.IsNullOrWhiteSpace(result.Message))
            {
                var truncated = false;
                output["message"] = TrimText(result.Message, MaxTestMessageChars, ref truncated);
                if (truncated)
                {
                    output["messageTruncated"] = true;
                }
            }

            if (includeStackTrace && !string.IsNullOrWhiteSpace(result.StackTrace))
            {
                var truncated = false;
                output["stackTrace"] = TrimText(result.StackTrace, MaxTestStackTraceChars, ref truncated);
                if (truncated)
                {
                    output["stackTraceTruncated"] = true;
                }
            }

            return output;
        }

        private string NormalizeTestStatus(string status)
        {
            if (status.IndexOf("Passed", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Passed";
            }

            if (status.IndexOf("Skipped", StringComparison.OrdinalIgnoreCase) >= 0
                || status.IndexOf("Inconclusive", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Skipped";
            }

            return "Failed";
        }

        private string FormatDuration(double seconds)
        {
            return seconds.ToString("0.###", CultureInfo.InvariantCulture) + "s";
        }

        private void CancelPendingTestRequest(PendingTestRequest pending)
        {
            pending.Completed = true;
            pending.CancellationRequested = true;
            TryCancelTestRunnerApi(pending.Api);
            OperationStore.Complete(pending.Id, "cancelled", "tests-run cancellation requested.");
            EventJournal.Write(
                "editor",
                "test-run-finish",
                "warning",
                "Unity Test Runner cancellation requested.",
                operationId: pending.Id);
            Transport.WriteResponse(pending.Id, new
            {
                ok = false,
                error = $"Bridge operation {pending.Id} cancelled. Unity Test Runner cancellation was requested best-effort."
            });
            CleanupTestRequest(pending);
        }

        private void TryCancelTestRunnerApi(TestRunnerApi? api)
        {
            if (api == null)
            {
                return;
            }

            foreach (var methodName in new[] { "CancelTestRun", "CancelRun" })
            {
                try
                {
                    var method = api.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                    if (method != null)
                    {
                        method.Invoke(api, null);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"ChievFX MCP tests-run cancellation via {methodName} failed. {ex.GetBaseException().Message}");
                }
            }
        }

        private void CleanupTestRequest(PendingTestRequest pending)
        {
            if (pending.Api != null && pending.Callbacks != null)
            {
                try
                {
                    pending.Api.UnregisterCallbacks(pending.Callbacks);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"ChievFX MCP tests-run callback cleanup failed. {ex.GetBaseException().Message}");
                }
            }

            if (pending.Api != null)
            {
                Object.DestroyImmediate(pending.Api);
                pending.Api = null;
            }

            pending.Callbacks = null;
        }

    }
}
