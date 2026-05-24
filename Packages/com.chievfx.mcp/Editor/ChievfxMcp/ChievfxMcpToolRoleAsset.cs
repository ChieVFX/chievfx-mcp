#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Chievfx.Mcp.Editor
{
    [CreateAssetMenu(fileName = "ChievfxMcpToolRole", menuName = "ChievFX/MCP Tool Role")]
    internal sealed class ChievfxMcpToolRoleAsset : ScriptableObject
    {
        public string roleId = string.Empty;

        public string displayName = "Custom MCP Role";

        [TextArea(2, 4)]
        public string description = "Project-specific MCP tool preset.";

        public List<string> enabledCategoryIds = new();

        public List<string> enabledToolIds = new();

        private void OnValidate()
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                roleId = "custom-" + Guid.NewGuid().ToString("N");
            }

            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = name;
            }
        }
    }
}
