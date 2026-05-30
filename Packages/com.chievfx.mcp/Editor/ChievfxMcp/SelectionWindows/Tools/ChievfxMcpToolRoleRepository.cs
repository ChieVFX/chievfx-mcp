#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using static Chievfx.Mcp.Editor.ChievfxMcpSelectionUi;
using static Chievfx.Mcp.Editor.ChievfxMcpToolSelectionFormatting;
using Debug = UnityEngine.Debug;

namespace Chievfx.Mcp.Editor
{
    internal static class ChievfxMcpToolRoleRepository
    {
        public static List<RoleDefinition> LoadRoleDefinitions()
        {
            var roles = new List<RoleDefinition>();
            if (File.Exists(ChievfxMcpToolPolicy.ToolRolePresetsPath))
            {
                try
                {
                    var root = JToken.Parse(File.ReadAllText(ChievfxMcpToolPolicy.ToolRolePresetsPath));
                    if (root["roles"] is JArray roleArray)
                    {
                        foreach (var item in roleArray.OfType<JObject>())
                        {
                            var id = ReadString(item, "id");
                            if (string.IsNullOrWhiteSpace(id))
                            {
                                continue;
                            }

                            roles.Add(new RoleDefinition
                            {
                                Kind = "built-in",
                                Id = id,
                                DisplayName = ReadString(item, "displayName", id),
                                Description = ReadString(item, "description"),
                                EnabledCategoryIds = ReadStringArray(item["enabledCategoryIds"]),
                                EnabledToolIds = ReadStringArray(item["enabledToolIds"]),
                                EnabledPromptNames = ReadStringArray(item["enabledPromptNames"])
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"ChievFX MCP could not read role presets. {ex.Message}");
                }
            }

            foreach (var guid in AssetDatabase.FindAssets("t:ChievfxMcpToolRoleAsset"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadAssetAtPath<ChievfxMcpToolRoleAsset>(path);
                if (asset == null)
                {
                    continue;
                }

                var id = string.IsNullOrWhiteSpace(asset.roleId) ? Path.GetFileNameWithoutExtension(path) : asset.roleId;
                roles.Add(new RoleDefinition
                {
                    Kind = "custom",
                    Id = id,
                    DisplayName = string.IsNullOrWhiteSpace(asset.displayName) ? asset.name : asset.displayName,
                    Description = asset.description,
                    EnabledCategoryIds = asset.enabledCategoryIds.Where(value => !string.IsNullOrWhiteSpace(value)).ToList(),
                    EnabledToolIds = asset.enabledToolIds.Where(value => !string.IsNullOrWhiteSpace(value)).ToList(),
                    EnabledPromptNames = new List<string>(),
                    AssetPath = path,
                    Asset = asset
                });
            }

            return roles;
        }
    }
}
