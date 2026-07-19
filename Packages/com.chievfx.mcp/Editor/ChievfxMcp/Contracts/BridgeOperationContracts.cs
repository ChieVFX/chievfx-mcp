#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEditor.PackageManager.Requests;
using UnityEditor.TestTools.TestRunner.Api;
using PackageManagerPackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Chievfx.Mcp.Editor
{
    internal sealed class LogEntryDto
    {
        public LogEntryDto(string logType, string message, DateTime timestamp, string stackTrace, long eventId = 0)
        {
            LogType = logType;
            Message = message;
            Timestamp = timestamp;
            StackTrace = stackTrace;
            EventId = eventId;
        }

        public string LogType { get; }

        public string Message { get; }

        public DateTime Timestamp { get; }

        public string StackTrace { get; }

        // Event-journal cursor assigned when this entry was captured by the bridge (0 when it came from
        // the live Unity Console or Editor.log rather than the in-memory capture, which carry no cursor).
        public long EventId { get; }
    }

    internal enum StackTraceMode
    {
        None,
        FirstLine,
        Full
    }

    internal enum PackageSourceFilter
    {
        All,
        Registry,
        Embedded,
        Local,
        Git,
        BuiltIn,
        LocalTarball
    }

    internal enum PackageRequestKind
    {
        List,
        Search,
        Add,
        Remove,
        VerifyAfterReload
    }

    internal sealed class PendingPackageRequest
    {
        public string Id { get; set; } = string.Empty;

        public PackageRequestKind Kind { get; set; }

        public ListRequest? ListRequest { get; set; }

        public SearchRequest? SearchRequest { get; set; }

        public AddRequest? AddRequest { get; set; }

        public RemoveRequest? RemoveRequest { get; set; }

        public string PackageId { get; set; } = string.Empty;

        public string ExpectedPackageName { get; set; } = string.Empty;

        public PackageSourceFilter SourceFilter { get; set; } = PackageSourceFilter.All;

        public string NameFilter { get; set; } = string.Empty;

        public bool DirectDependenciesOnly { get; set; }

        public bool OfflineMode { get; set; } = true;

        public string Query { get; set; } = string.Empty;

        public int MaxResults { get; set; } = McpLimits.DefaultPackageMaxResults;

        public bool RestoredAfterDomainReload { get; set; }

        public bool CancellationRequested { get; set; }

        public PackageOperationCheckpoint? Checkpoint { get; set; }
    }

    internal sealed class PackageOperationCheckpoint
    {
        public string Id { get; set; } = string.Empty;

        public string ToolName { get; set; } = string.Empty;

        public string PackageId { get; set; } = string.Empty;

        public string ExpectedPackageName { get; set; } = string.Empty;

        public string CreatedAtUtc { get; set; } = string.Empty;

        public Dictionary<string, string> ManifestDependenciesBefore { get; set; } = new(StringComparer.Ordinal);
    }

    internal sealed class ScriptInvocationResult
    {
        public bool TimedOut { get; set; }

        public object? Value { get; set; }

        public Exception? Exception { get; set; }

        public string? ReturnValueType { get; set; }

        public object? SerializedReturnValue { get; set; }

        public bool ReturnValueTruncated { get; set; }
    }

    internal sealed class PendingScriptInvocationRequest
    {
        public string Id { get; set; } = string.Empty;

        public JToken Args { get; set; } = new JObject();

        public DateTime StartedUtc { get; set; }

        public DateTime InvocationQueuedUtc { get; set; }

        public Thread? WorkerThread { get; set; }

        public MethodInfo? Method { get; set; }

        public string MethodLabel { get; set; } = "script-execute";

        public object?[]? Values { get; set; }

        public ManualResetEventSlim Completion { get; } = new(false);

        public bool InvocationStarted { get; set; }

        public bool InvocationCompleted { get; set; }

        public bool ResponseWritten { get; set; }

        public bool TimedOut { get; set; }

        public object? Value { get; set; }

        public Exception? Exception { get; set; }

        public string? ReturnValueType { get; set; }

        public object? SerializedReturnValue { get; set; }

        public bool ReturnValueTruncated { get; set; }
    }

    internal sealed class PendingTestRequest
    {
        public string Id { get; set; } = string.Empty;

        public DateTime StartedUtc { get; set; }

        public int TimeoutMs { get; set; } = McpLimits.DefaultTestTimeoutMs;

        public bool IncludePassingTests { get; set; }

        public bool IncludeMessages { get; set; } = true;

        public bool IncludeStackTrace { get; set; }

        public bool IncludeLogs { get; set; }

        public bool IncludeLogsStackTrace { get; set; }

        public string LogType { get; set; } = UnityEngine.LogType.Warning.ToString();

        public int MaxResults { get; set; } = McpLimits.DefaultTestMaxResults;

        public int LogStartIndex { get; set; }

        public bool Completed { get; set; }

        public bool CancellationRequested { get; set; }

        public TestRunnerApi? Api { get; set; }

        public ChievfxTestCallbacks? Callbacks { get; set; }
    }

    internal sealed class ChievfxTestCallbacks : ICallbacks
    {
        public ChievfxTestCallbacks(string id)
        {
            Id = id;
        }

        private string Id { get; }

        public void RunStarted(ITestAdaptor testsToRun)
        {
        }

        public void RunFinished(ITestResultAdaptor result)
        {
            ChievfxMcpBridge.CompleteTestRun(Id, result);
        }

        public void TestStarted(ITestAdaptor test)
        {
        }

        public void TestFinished(ITestResultAdaptor result)
        {
        }
    }

    internal sealed class TestResultDto
    {
        public string Name { get; set; } = string.Empty;

        public string FullName { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public double Duration { get; set; }

        public string Message { get; set; } = string.Empty;

        public string StackTrace { get; set; } = string.Empty;
    }

    internal sealed class PackageSearchCandidate
    {
        public PackageManagerPackageInfo Package { get; set; } = null!;

        public int Rank { get; set; }

        public bool IsInstalled { get; set; }
    }

    internal sealed class MethodDto
    {
        public int? index { get; set; }

        public string ns { get; set; } = string.Empty;

        public string type { get; set; } = string.Empty;

        public string method { get; set; } = string.Empty;

        public string signature { get; set; } = string.Empty;

        public string @return { get; set; } = string.Empty;

        public ParameterDto[] @params { get; set; } = Array.Empty<ParameterDto>();

        public bool @static { get; set; }

        public string visibility { get; set; } = string.Empty;

        public object? callFilter { get; set; }
    }

    internal sealed class ParameterDto
    {
        public string type { get; set; } = string.Empty;

        public string name { get; set; } = string.Empty;
    }

    internal sealed class SerializedMemberDto
    {
        public string typeName { get; set; } = string.Empty;

        public string name { get; set; } = string.Empty;

        public object? value { get; set; }
    }
}
