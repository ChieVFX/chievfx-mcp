#nullable enable
using System.Collections.Generic;

namespace Chievfx.Mcp.Editor
{
    internal sealed class RoleDefinition
    {
        public string Kind { get; set; } = "built-in";

        public string Id { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        public List<string> EnabledCategoryIds { get; set; } = new();

        public List<string> EnabledToolIds { get; set; } = new();

        public List<string> EnabledPromptNames { get; set; } = new();

        public string AssetPath { get; set; } = string.Empty;

        public ChievfxMcpToolRoleAsset? Asset { get; set; }

        public string Key => ChievfxMcpToolSelectionFormatting.BuildRoleKey(Kind, Id, AssetPath);
    }

    internal sealed class ToolRow
    {
        public string Id { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        public string Category { get; set; } = "General";

        public string DescriptorHash { get; set; } = string.Empty;

        public string DescriptorPreview { get; set; } = "{}";

        public int DescriptorBytes { get; set; }

        public int EstimatedTokens { get; set; }

        public int DescriptionEstimatedTokens { get; set; }

        public string CallEnvelopePreview { get; set; } = "{}";

        public int CallEnvelopeBytes { get; set; }

        public int CallEnvelopeEstimatedTokens { get; set; }

        public string ResponseEstimateLabel { get; set; } = string.Empty;

        public string SchemaJson { get; set; } = "{}";

        public bool Required { get; set; }

        public bool Enabled { get; set; }
    }
}
