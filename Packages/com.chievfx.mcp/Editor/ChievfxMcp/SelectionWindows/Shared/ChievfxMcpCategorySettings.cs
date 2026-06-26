#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Chievfx.Mcp.Editor
{
    /// <summary>
    /// Reads and writes the shared category collapse config
    /// (UserSettings/ChievfxMcpCategorySelection.json). The Python MCP server reads
    /// the same file to decide which categories collapse in initialize.instructions.
    /// </summary>
    internal static class ChievfxMcpCategorySettings
    {
        private const int SchemaVersion = 1;

        public static readonly string[] DefaultAlwaysSuppliedCategories =
        {
            "essentials",
            "editor-window",
            "script-execution-tests",
            "control",
            "ui-runtime-common",
        };

        internal sealed class Settings
        {
            public bool ForceAllAlwaysSupplied;
            public readonly HashSet<string> AlwaysSupplied = new(StringComparer.OrdinalIgnoreCase);
        }

        public static Settings Load()
        {
            var settings = new Settings();
            foreach (var name in DefaultAlwaysSuppliedCategories)
            {
                settings.AlwaysSupplied.Add(name);
            }

            try
            {
                if (File.Exists(ChievfxMcpToolPolicy.CategorySelectionPath))
                {
                    var root = JToken.Parse(File.ReadAllText(ChievfxMcpToolPolicy.CategorySelectionPath));
                    if (root is JObject rootObj)
                    {
                        if (rootObj["forceAllCategoriesAlwaysSupplied"] is JValue force
                            && force.Type == JTokenType.Boolean)
                        {
                            settings.ForceAllAlwaysSupplied = force.Value<bool>();
                        }

                        if (rootObj["alwaysSuppliedCategories"] is JArray listed)
                        {
                            settings.AlwaysSupplied.Clear();
                            foreach (var item in listed)
                            {
                                if (item.Type == JTokenType.String)
                                {
                                    var name = item.Value<string>();
                                    if (!string.IsNullOrWhiteSpace(name))
                                    {
                                        settings.AlwaysSupplied.Add(name!);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"ChievFX MCP could not read category selection. Using defaults. {ex.Message}");
            }

            return settings;
        }

        public static bool IsAlwaysSupplied(string category)
        {
            var settings = Load();
            return settings.ForceAllAlwaysSupplied || settings.AlwaysSupplied.Contains(category);
        }

        public static bool ForceAll => Load().ForceAllAlwaysSupplied;

        public static void SetCategoryAlwaysSupplied(string category, bool alwaysSupplied)
        {
            if (string.IsNullOrWhiteSpace(category))
            {
                return;
            }

            var settings = Load();
            if (alwaysSupplied)
            {
                settings.AlwaysSupplied.Add(category);
            }
            else
            {
                settings.AlwaysSupplied.RemoveWhere(name => string.Equals(name, category, StringComparison.OrdinalIgnoreCase));
            }

            Save(settings);
        }

        public static void SetForceAll(bool forceAll)
        {
            var settings = Load();
            settings.ForceAllAlwaysSupplied = forceAll;
            Save(settings);
        }

        private static void Save(Settings settings)
        {
            try
            {
                var path = ChievfxMcpToolPolicy.CategorySelectionPath;
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var payload = new JObject
                {
                    ["schemaVersion"] = SchemaVersion,
                    ["updatedAtUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    ["forceAllCategoriesAlwaysSupplied"] = settings.ForceAllAlwaysSupplied,
                    ["alwaysSuppliedCategories"] = new JArray(
                        settings.AlwaysSupplied.OrderBy(name => name, StringComparer.Ordinal).Cast<object>().ToArray())
                };

                File.WriteAllText(path, payload.ToString(Newtonsoft.Json.Formatting.Indented) + "\n");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"ChievFX MCP could not write category selection. {ex.Message}");
            }
        }
    }
}
