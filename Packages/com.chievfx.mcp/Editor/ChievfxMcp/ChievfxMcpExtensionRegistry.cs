#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Chievfx.Mcp.Editor
{
    public sealed class ChievfxMcpExtensionDescriptor
    {
        public string Id { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;

        public string Version { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        public List<ChievfxMcpToolDescriptor> Tools { get; } = new();

        public List<ChievfxMcpResourceDescriptor> Resources { get; } = new();

        public List<ChievfxMcpResourceTemplateDescriptor> ResourceTemplates { get; } = new();

        public List<ChievfxMcpPromptDescriptor> Prompts { get; } = new();

        public Func<string, JToken, object?>? ToolRunner { get; set; }

        public Func<string, object?>? ResourceReader { get; set; }
    }

    public sealed class ChievfxMcpToolDescriptor
    {
        public string Name { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        public string Category { get; set; } = "Extensions";

        public JObject InputSchema { get; set; } = new() { ["type"] = "object" };
    }

    public sealed class ChievfxMcpResourceDescriptor
    {
        public string Id { get; set; } = string.Empty;

        public string Uri { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        public string MimeType { get; set; } = "text/plain";

        public string Category { get; set; } = "Extensions";

        public bool Required { get; set; }

        public string? StaticText { get; set; }
    }

    public sealed class ChievfxMcpResourceTemplateDescriptor
    {
        public string Id { get; set; } = string.Empty;

        public string UriTemplate { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        public string MimeType { get; set; } = "text/plain";

        public string Category { get; set; } = "Extensions";

        public bool Required { get; set; }
    }

    public sealed class ChievfxMcpPromptDescriptor
    {
        public string Name { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        public string Category { get; set; } = "Extensions";

        public JArray Arguments { get; set; } = new();

        public bool Required { get; set; }

        public string? StaticText { get; set; }
    }

    public static class ChievfxMcpExtensionRegistry
    {
        public const int ManifestSchemaVersion = 1;
        public const string ExtensionUriPrefix = "chievfx://extensions/";

        private static readonly Regex ExtensionIdPattern = new(@"^[a-z0-9][a-z0-9._-]{0,127}$", RegexOptions.Compiled);
        private static readonly Regex CapabilityIdPattern = new(@"^[a-z0-9][a-z0-9_-]{0,127}$", RegexOptions.Compiled);
        private static readonly object SyncRoot = new();
        private static readonly Dictionary<string, RegisteredExtension> Extensions = new(StringComparer.Ordinal);

        private static readonly HashSet<string> CoreToolIds = new(StringComparer.Ordinal)
        {
            "asset-create",
            "asset-delete",
            "assets-refresh",
            "bridge-get-operation",
            "bridge-get-status",
            "console-clear-logs",
            "console-get-logs",
            "console-get-logs-single",
            "editor-window-focus",
            "editor-window-list",
            "editor-window-open",
            "events-check-since",
            "events-wait",
            "gameobject-component-update-or-create",
            "frame-debugger-control",
            "frame-debugger-drawcall-get",
            "frame-debugger-drawcall-screenshot",
            "frame-debugger-event-get",
            "frame-debugger-events-list",
            "frame-debugger-group-events-list",
            "frame-debugger-groups-list",
            "folder-ensure",
            "gameobject-component-get",
            "gameobject-create",
            "gameobject-duplicate",
            "gameobject-find",
            "gameobject-hierarchy",
            "gameobject-set-parent",
            "gameobject-transform-get",
            "gameobject-transform-update",
            "gameobject-update",
            "package-add",
            "package-list",
            "package-remove",
            "package-search",
            "prefab-close",
            "prefab-create",
            "prefab-instantiate",
            "prefab-open",
            "prefab-save",
            "profiler-counters-get",
            "profiler-get-state",
            "profiler-start-recording",
            "profiler-stop-recording",
            "profiler-window-control",
            "reflection-method-call",
            "reflection-method-find",
            "reflection-method-find-single",
            "recompile",
            "scene-create",
            "scene-list-available",
            "scene-list-opened",
            "scene-open",
            "scene-save",
            "screenshot-camera",
            "screenshot-editor-window",
            "screenshot-game-view",
            "script-execute",
            "tests-run",
            "tool-batch",
            "tools-get-role",
            "tools-get-roles",
            "tools-list-categories",
            "tools-list-category",
            "tools-set-enabled-state",
            "tools-set-role",
        };

        private static readonly HashSet<string> CoreResourceIds = new(
            ChievfxMcpCoreMetadata.Resources.Select(resource => resource.Id),
            StringComparer.Ordinal);

        private static readonly HashSet<string> CoreResourceTemplateIds = new(
            ChievfxMcpCoreMetadata.ResourceTemplates.Select(template => template.Id),
            StringComparer.Ordinal);

        private static readonly HashSet<string> CoreResourceUris = new(
            ChievfxMcpCoreMetadata.Resources.Select(resource => resource.Uri),
            StringComparer.Ordinal);

        private static readonly HashSet<string> CoreResourceTemplateUris = new(
            ChievfxMcpCoreMetadata.ResourceTemplates.Select(template => template.UriTemplate),
            StringComparer.Ordinal);

        private static readonly HashSet<string> CorePromptIds = new(
            ChievfxMcpCoreMetadata.Prompts.Select(prompt => prompt.Name),
            StringComparer.Ordinal);

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void RegisterExtension(ChievfxMcpExtensionDescriptor descriptor)
        {
            RegisterExtension(descriptor, Assembly.GetCallingAssembly());
        }

        public static object GetManifest()
        {
            return BuildManifest();
        }

        public static void ExportManifest(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ChievfxMcpToolPolicy.BridgeDirectory);
            var json = JsonConvert.SerializeObject(GetManifest(), Formatting.Indented);
            WriteAllTextAtomic(path, json + Environment.NewLine);
        }

        internal static void RegisterExtension(ChievfxMcpExtensionDescriptor descriptor, Assembly sourceAssembly)
        {
            if (descriptor == null)
            {
                throw new ArgumentNullException(nameof(descriptor));
            }

            var sourceAssemblyName = sourceAssembly.GetName().Name ?? "unknown";
            lock (SyncRoot)
            {
                ValidateDescriptor(descriptor, sourceAssemblyName);
                if (Extensions.ContainsKey(descriptor.Id))
                {
                    throw new InvalidOperationException($"ChievFX MCP extension id '{descriptor.Id}' is already registered.");
                }

                Extensions.Add(descriptor.Id, new RegisteredExtension(descriptor, sourceAssemblyName));
            }
        }

        internal static IReadOnlyList<object> GetRegisteredExtensionSummaries()
        {
            lock (SyncRoot)
            {
                return Extensions.Values
                    .OrderBy(item => item.Descriptor.Id, StringComparer.Ordinal)
                    .Select(item => (object)new
                    {
                        id = item.Descriptor.Id,
                        displayName = item.Descriptor.DisplayName,
                        sourceAssembly = item.SourceAssemblyName,
                        toolCount = item.Descriptor.Tools.Count,
                        resourceCount = item.Descriptor.Resources.Count,
                        resourceTemplateCount = item.Descriptor.ResourceTemplates.Count,
                        promptCount = item.Descriptor.Prompts.Count,
                    })
                    .ToArray();
            }
        }

        internal static bool TryReadResource(string uri, out object? result)
        {
            RegisteredExtension[] extensions;
            lock (SyncRoot)
            {
                extensions = Extensions.Values.ToArray();
            }

            foreach (var extension in extensions)
            {
                var descriptor = extension.Descriptor;
                if (descriptor.ResourceReader == null)
                {
                    continue;
                }

                var matchesResource = descriptor.Resources.Any(resource => string.Equals(resource.Uri, uri, StringComparison.Ordinal));
                var matchesTemplate = !matchesResource && descriptor.ResourceTemplates.Any(template => UriMatchesTemplate(uri, template.UriTemplate));
                if (!matchesResource && !matchesTemplate)
                {
                    continue;
                }

                result = descriptor.ResourceReader(uri);
                return true;
            }

            result = null;
            return false;
        }

        internal static bool TryRunTool(string toolName, JToken args, out object? result)
        {
            RegisteredExtension[] extensions;
            lock (SyncRoot)
            {
                extensions = Extensions.Values.ToArray();
            }

            foreach (var extension in extensions)
            {
                var descriptor = extension.Descriptor;
                if (descriptor.ToolRunner == null
                    || descriptor.Tools.All(tool => !string.Equals(tool.Name, toolName, StringComparison.Ordinal)))
                {
                    continue;
                }

                result = descriptor.ToolRunner(toolName, args);
                return true;
            }

            result = null;
            return false;
        }

        private static object BuildManifest()
        {
            lock (SyncRoot)
            {
                return new
                {
                    schemaVersion = ManifestSchemaVersion,
                    source = "Chievfx.Mcp.Editor.ChievfxMcpExtensionRegistry",
                    extensionUriPrefix = ExtensionUriPrefix,
                    extensions = Extensions.Values
                        .OrderBy(item => item.Descriptor.Id, StringComparer.Ordinal)
                        .Select(ToManifestExtension)
                        .ToArray(),
                };
            }
        }

        private static object ToManifestExtension(RegisteredExtension registered)
        {
            var descriptor = registered.Descriptor;
            return new
            {
                id = descriptor.Id,
                displayName = descriptor.DisplayName,
                version = descriptor.Version,
                description = descriptor.Description,
                sourceAssembly = registered.SourceAssemblyName,
                tools = descriptor.Tools
                    .OrderBy(item => item.Name, StringComparer.Ordinal)
                    .Select(item => new
                    {
                        name = item.Name,
                        description = item.Description,
                        category = item.Category,
                        inputSchema = NormalizeJson(item.InputSchema),
                    })
                    .ToArray(),
                resources = descriptor.Resources
                    .OrderBy(item => item.Id, StringComparer.Ordinal)
                    .Select(item => new
                    {
                        id = item.Id,
                        uri = item.Uri,
                        name = item.Name,
                        description = item.Description,
                        mimeType = item.MimeType,
                        category = item.Category,
                        required = item.Required,
                        staticText = item.StaticText,
                    })
                    .ToArray(),
                resourceTemplates = descriptor.ResourceTemplates
                    .OrderBy(item => item.Id, StringComparer.Ordinal)
                    .Select(item => new
                    {
                        id = item.Id,
                        uriTemplate = item.UriTemplate,
                        name = item.Name,
                        description = item.Description,
                        mimeType = item.MimeType,
                        category = item.Category,
                        required = item.Required,
                    })
                    .ToArray(),
                prompts = descriptor.Prompts
                    .OrderBy(item => item.Name, StringComparer.Ordinal)
                    .Select(item => new
                    {
                        name = item.Name,
                        title = item.Title,
                        description = item.Description,
                        category = item.Category,
                        required = item.Required,
                        staticText = item.StaticText,
                        arguments = NormalizeJson(item.Arguments),
                    })
                    .ToArray(),
            };
        }

        private static void ValidateDescriptor(ChievfxMcpExtensionDescriptor descriptor, string sourceAssemblyName)
        {
            if (string.IsNullOrWhiteSpace(descriptor.Id) || !ExtensionIdPattern.IsMatch(descriptor.Id))
            {
                throw new InvalidOperationException($"ChievFX MCP extension id '{descriptor.Id}' from {sourceAssemblyName} is invalid.");
            }

            var toolIds = new HashSet<string>(CoreToolIds, StringComparer.Ordinal);
            var promptNames = new HashSet<string>(CorePromptIds, StringComparer.Ordinal);
            var resourceIds = new HashSet<string>(CoreResourceIds.Concat(CoreResourceTemplateIds), StringComparer.Ordinal);
            var resourceUris = new HashSet<string>(CoreResourceUris.Concat(CoreResourceTemplateUris), StringComparer.Ordinal);

            foreach (var extension in Extensions.Values)
            {
                toolIds.UnionWith(extension.Descriptor.Tools.Select(item => item.Name));
                promptNames.UnionWith(extension.Descriptor.Prompts.Select(item => item.Name));
                resourceIds.UnionWith(extension.Descriptor.Resources.Select(item => item.Id));
                resourceIds.UnionWith(extension.Descriptor.ResourceTemplates.Select(item => item.Id));
                resourceUris.UnionWith(extension.Descriptor.Resources.Select(item => item.Uri));
                resourceUris.UnionWith(extension.Descriptor.ResourceTemplates.Select(item => item.UriTemplate));
            }

            foreach (var tool in descriptor.Tools)
            {
                ValidateCapabilityId(tool.Name, "tool", descriptor.Id);
                if (!toolIds.Add(tool.Name))
                {
                    throw new InvalidOperationException($"ChievFX MCP tool '{tool.Name}' is already reserved or registered.");
                }

                if (tool.InputSchema == null)
                {
                    throw new InvalidOperationException($"ChievFX MCP tool '{tool.Name}' must declare an input schema.");
                }
            }

            foreach (var resource in descriptor.Resources)
            {
                ValidateCapabilityId(resource.Id, "resource", descriptor.Id);
                ValidateExtensionUri(resource.Uri, "resource", resource.Id);
                if (!resourceIds.Add(resource.Id))
                {
                    throw new InvalidOperationException($"ChievFX MCP resource id '{resource.Id}' is already reserved or registered.");
                }

                if (!resourceUris.Add(resource.Uri))
                {
                    throw new InvalidOperationException($"ChievFX MCP resource URI '{resource.Uri}' is already reserved or registered.");
                }
            }

            foreach (var template in descriptor.ResourceTemplates)
            {
                ValidateCapabilityId(template.Id, "resource template", descriptor.Id);
                ValidateExtensionUri(template.UriTemplate, "resource template", template.Id);
                if (!resourceIds.Add(template.Id))
                {
                    throw new InvalidOperationException($"ChievFX MCP resource template id '{template.Id}' is already reserved or registered.");
                }

                if (!resourceUris.Add(template.UriTemplate))
                {
                    throw new InvalidOperationException($"ChievFX MCP resource template URI '{template.UriTemplate}' is already reserved or registered.");
                }
            }

            foreach (var prompt in descriptor.Prompts)
            {
                ValidateCapabilityId(prompt.Name, "prompt", descriptor.Id);
                if (!promptNames.Add(prompt.Name))
                {
                    throw new InvalidOperationException($"ChievFX MCP prompt '{prompt.Name}' is already reserved or registered.");
                }
            }
        }

        private static void ValidateCapabilityId(string value, string kind, string extensionId)
        {
            if (string.IsNullOrWhiteSpace(value) || !CapabilityIdPattern.IsMatch(value))
            {
                throw new InvalidOperationException($"ChievFX MCP extension '{extensionId}' has invalid {kind} id '{value}'.");
            }
        }

        private static void ValidateExtensionUri(string uri, string kind, string id)
        {
            if (string.IsNullOrWhiteSpace(uri) || !uri.StartsWith(ExtensionUriPrefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"ChievFX MCP {kind} '{id}' URI must start with {ExtensionUriPrefix}.");
            }
        }

        private static bool UriMatchesTemplate(string uri, string uriTemplate)
        {
            if (string.IsNullOrWhiteSpace(uri) || string.IsNullOrWhiteSpace(uriTemplate))
            {
                return false;
            }

            var pattern = "^" + Regex.Replace(
                Regex.Escape(uriTemplate),
                @"\\\{[A-Za-z0-9_]+\}",
                "[^/?#]+") + "$";
            return Regex.IsMatch(uri, pattern, RegexOptions.CultureInvariant);
        }

        private static JToken NormalizeJson(JToken? token)
        {
            if (token == null)
            {
                return JValue.CreateNull();
            }

            if (token is JObject obj)
            {
                var normalized = new JObject();
                foreach (var property in obj.Properties().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    normalized[property.Name] = NormalizeJson(property.Value);
                }

                return normalized;
            }

            if (token is JArray array)
            {
                return new JArray(array.Select(NormalizeJson));
            }

            return token.DeepClone();
        }

        private static void WriteAllTextAtomic(string path, string contents)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tempPath, contents);
            if (File.Exists(path))
            {
                File.Replace(tempPath, path, null);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }

        private sealed class RegisteredExtension
        {
            public RegisteredExtension(ChievfxMcpExtensionDescriptor descriptor, string sourceAssemblyName)
            {
                Descriptor = descriptor;
                SourceAssemblyName = sourceAssemblyName;
            }

            public ChievfxMcpExtensionDescriptor Descriptor { get; }

            public string SourceAssemblyName { get; }
        }
    }
}
