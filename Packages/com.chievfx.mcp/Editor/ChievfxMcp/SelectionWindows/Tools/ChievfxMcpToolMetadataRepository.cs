#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json.Linq;
using static Chievfx.Mcp.Editor.ChievfxMcpSelectionUi;

namespace Chievfx.Mcp.Editor
{
    internal static class ChievfxMcpToolMetadataRepository
    {

        public static ChievfxMcpToolMetadata LoadMetadata(
            string descriptorEstimateBasis,
            string descriptionEstimateBasis,
            string callEnvelopeEstimateBasis,
            string responseEstimateNote)
        {
            if (!File.Exists(ChievfxMcpToolPolicy.ServerScriptPath))
            {
                throw new FileNotFoundException("ChievFX MCP server script not found.", ChievfxMcpToolPolicy.ServerScriptPath);
            }

            ChievfxMcpExtensionManifestSnapshot.Refresh();

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ChievfxMcpPythonLauncher.ExecutablePath,
                    WorkingDirectory = ChievfxMcpToolPolicy.ProjectRoot,
                    Arguments = $"{QuoteArg(ChievfxMcpToolPolicy.ServerScriptPath)} --tool-metadata",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                }
            };

            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start python3 for ChievFX MCP tool metadata.");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(30000))
            {
                try
                {
                    process.Kill();
                }
                catch (InvalidOperationException)
                {
                    // Process exited before timeout cleanup.
                }

                throw new TimeoutException("Timed out reading ChievFX MCP tool metadata.");
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"ChievFX MCP metadata command failed ({process.ExitCode}). {stderr}");
            }

            var root = JToken.Parse(stdout);
            var requiredIds = new HashSet<string>(ChievfxMcpToolPolicy.RequiredToolIds, StringComparer.Ordinal);
            var toolsArray = root["tools"] as JArray ?? throw new InvalidOperationException("ChievFX MCP metadata response missing `tools` array.");
            var tools = new List<ToolRow>();
            foreach (var toolElement in toolsArray)
            {
                var name = toolElement["name"]?.Value<string>() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var inputSchema = toolElement["inputSchema"] is JToken schemaElement
                    ? FormatJson(schemaElement)
                    : "{}";

                var category = ReadString(toolElement, "category", "general");
                tools.Add(new ToolRow
                {
                    Id = name,
                    Description = ReadString(toolElement, "description"),
                    Category = category,
                    DescriptorHash = ReadString(toolElement, "descriptorHash"),
                    DescriptorPreview = ReadString(toolElement, "descriptorPreview", "{}"),
                    DescriptorBytes = ReadInt(toolElement, "descriptorBytes"),
                    EstimatedTokens = ReadInt(toolElement, "estimatedTokens"),
                    DescriptionEstimatedTokens = ReadInt(toolElement, "descriptionEstimatedTokens"),
                    CallEnvelopePreview = ReadString(toolElement, "callEnvelopePreview", "{}"),
                    CallEnvelopeBytes = ReadInt(toolElement, "callEnvelopeBytes"),
                    CallEnvelopeEstimatedTokens = ReadInt(toolElement, "callEnvelopeEstimatedTokens"),
                    ResponseEstimateLabel = ReadResponseEstimateLabel(toolElement),
                    SchemaJson = inputSchema,
                    Required = requiredIds.Contains(name) || string.Equals(category, "essentials", StringComparison.Ordinal)
                });
            }

            return new ChievfxMcpToolMetadata(
                tools,
                root["estimator"] is JToken estimatorElement && estimatorElement.Type == JTokenType.String
                    ? estimatorElement.Value<string>() ?? "unknown"
                    : "unknown",
                ReadString(root, "descriptorEstimateBasis", descriptorEstimateBasis),
                ReadString(root, "descriptionEstimateBasis", descriptionEstimateBasis),
                ReadString(root, "callEnvelopeEstimateBasis", callEnvelopeEstimateBasis),
                ReadString(root, "responseEstimateNote", responseEstimateNote));
        }
    }

    internal sealed class ChievfxMcpToolMetadata
    {
        public ChievfxMcpToolMetadata(
            IReadOnlyList<ToolRow> tools,
            string estimator,
            string descriptorEstimateBasis,
            string descriptionEstimateBasis,
            string callEnvelopeEstimateBasis,
            string responseEstimateNote)
        {
            Tools = tools;
            Estimator = estimator;
            DescriptorEstimateBasis = descriptorEstimateBasis;
            DescriptionEstimateBasis = descriptionEstimateBasis;
            CallEnvelopeEstimateBasis = callEnvelopeEstimateBasis;
            ResponseEstimateNote = responseEstimateNote;
        }

        public IReadOnlyList<ToolRow> Tools { get; }

        public string Estimator { get; }

        public string DescriptorEstimateBasis { get; }

        public string DescriptionEstimateBasis { get; }

        public string CallEnvelopeEstimateBasis { get; }

        public string ResponseEstimateNote { get; }
    }
}
