#nullable enable

namespace Chievfx.Mcp.Editor
{
    internal static class McpLimits
    {
        public const int MaxScreenshotDimension = 16384;
        public const int DefaultGameViewScreenshotMaxDimension = 960;
        public const int MaxLogEntries = 1000;
        public const int DefaultLogMaxEntries = 50;
        public const int HardLogMaxEntries = 200;
        public const int DefaultLogLastMinutes = 10;
        public const int MaxLogMessageChars = 1000;
        public const int MaxStackTraceChars = 4000;
        public const int MaxToolTextChars = 40000;
        public const int DefaultReflectionMaxResults = 10;
        public const int HardReflectionMaxResults = 100;
        public const int DefaultSceneMaxResults = 200;
        public const int HardSceneMaxResults = 1000;
        public const int DefaultGameObjectMaxResults = 100;
        public const int HardGameObjectMaxResults = 500;
        public const int DefaultGameObjectMaxDepth = 4;
        public const int HardGameObjectMaxDepth = 25;
        public const int DefaultResourceMaxResults = 160;
        public const int DefaultResourceMaxDepth = 6;
        public const int DefaultResourceFilterMaxResults = 80;
        public const int HardResourceFilterMaxResults = 200;
        public const int MaxResourceFilterSegmentChars = 1024;
        public const int MaxResourceFilterValueChars = 256;
        public const int MaxResourceFilterValues = 8;
        public const int MaxResourceFilterFolders = 8;
        public const int DefaultSceneUsageLocationCap = 120;
        public const int HardSceneUsageLocationCap = 300;
        public const int MaxSceneUsageSampleLocations = 5;
        public const int MaxSceneUsageResourceTextChars = 36000;
        public const int MaxSceneUsageScanWarnings = 20;
        public const int MaxSceneUsageSkippedComponents = 40;
        public const int DefaultMaterialProfileTextureLinkCap = 80;
        public const int DefaultMaterialProfileLocationCap = 120;
        public const int MaxComponentPreviewTypes = 12;
        public const int MaxSerializedFields = 80;
        public const int MaxSerializedStringChars = 500;
        public const int MaxReturnValueChars = 4000;
        public const int DefaultPackageMaxResults = 10;
        public const int HardPackageMaxResults = 50;
        public const int MaxPackageIdChars = 512;
        public const int MaxScriptCodeChars = 200000;
        public const int MaxScriptDiagnostics = 50;
        public const int DefaultScriptLogEntries = 50;
        public const int DefaultScriptTimeoutMs = 60000;
        public const int HardScriptTimeoutMs = 300000;
        public const int DefaultTestTimeoutMs = 60000;
        public const int HardTestTimeoutMs = 300000;
        public const int DefaultTestMaxResults = 200;
        public const int HardTestMaxResults = 1000;
        public const int MaxTestMessageChars = 2000;
        public const int MaxTestStackTraceChars = 6000;
        public const int MaxTestLogEntries = 200;
        public const int DefaultEditorWindowMaxResults = 200;
        public const int HardEditorWindowMaxResults = 500;
        public const int DefaultEditorWindowScreenshotDelayFrames = 2;
        public const int DefaultEditorWindowScreenshotDelayMs = 1000;
        public const int HardEditorWindowScreenshotDelayFrames = 120;
        public const int HardEditorWindowScreenshotDelayMs = 10000;
        public const int OperationRecordTtlMinutes = 60;
        public const int StaleOperationMinutes = 10;
        public const int MaxOperationRecords = 200;
        public const int MaxEventEntries = 1000;
        public const int MaxEventStreamChars = 512000;
        public const int MaxEventMessageChars = 1000;
        public const int MaxEventDataStringChars = 2000;
        public const int MaxEventMarkerChars = 256;
    }
}
