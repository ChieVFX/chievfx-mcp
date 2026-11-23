#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using static Chievfx.Mcp.Editor.ChievfxMcpSelectionUi;

namespace Chievfx.Mcp.Editor
{
    internal sealed partial class ChievfxMcpWindow
    {
        private int GetPort()
        {
            return Mathf.Max(1, portField?.value ?? ChievfxMcpToolPolicy.DefaultMcpPort);
        }

        private int GetTimeout()
        {
            return Mathf.Max(1000, timeoutField?.value ?? ChievfxMcpToolPolicy.DefaultTimeoutMs);
        }

        private string GetTransport()
        {
            return transportField?.value == TransportHttp ? TransportHttp : TransportStdio;
        }

        private string GetClient()
        {
            // Validate against the full choice list. An explicit allow-list of two clients silently
            // reset every other selection (Kimi Code) back to Cursor on save and on Write Config.
            var value = clientField?.value;
            foreach (var choice in ClientChoices)
            {
                if (string.Equals(value, choice, StringComparison.Ordinal))
                {
                    return choice;
                }
            }

            return ClientCursor;
        }

        private void SavePreferences()
        {
            if (portField != null)
            {
                EditorPrefs.SetInt(PrefKey("port"), GetPort());
            }

            if (timeoutField != null)
            {
                EditorPrefs.SetInt(PrefKey("timeout"), GetTimeout());
            }

            if (transportField != null)
            {
                EditorPrefs.SetString(PrefKey("transport"), GetTransport());
            }

            if (clientField != null)
            {
                EditorPrefs.SetString(PrefKey("client"), GetClient());
            }

            if (autoReloadExternallyChangedScenesToggle != null)
            {
                EditorPrefs.SetBool(
                    ChievfxMcpToolPolicy.AutoReloadExternallyChangedScenesKey,
                    autoReloadExternallyChangedScenesToggle.value);
            }
        }

        // Static mirrors of the instance getters above, reading the same saved EditorPrefs so the
        // on-load auto-setup writes configs with the user's chosen transport/port/timeout.
        private static string GetSavedTransport()
        {
            return EditorPrefs.GetString(PrefKey("transport"), TransportStdio) == TransportHttp
                ? TransportHttp
                : TransportStdio;
        }

        private static int GetSavedPort()
        {
            return Mathf.Max(1, EditorPrefs.GetInt(PrefKey("port"), ChievfxMcpToolPolicy.DefaultMcpPort));
        }

        private static int GetSavedTimeout()
        {
            return Mathf.Max(1000, EditorPrefs.GetInt(PrefKey("timeout"), ChievfxMcpToolPolicy.DefaultTimeoutMs));
        }

        private static int LoadTransportIndex()
        {
            return EditorPrefs.GetString(PrefKey("transport"), TransportStdio) == TransportHttp ? 1 : 0;
        }

        private static int LoadClientIndex()
        {
            var saved = EditorPrefs.GetString(PrefKey("client"), ClientCursor);
            for (var i = 0; i < ClientChoices.Length; i++)
            {
                if (string.Equals(saved, ClientChoices[i], StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return 0;
        }

        private static ChievfxMcpTab LoadActiveTab()
        {
            var value = EditorPrefs.GetString(PrefKey("activeTab"), ChievfxMcpTab.Status.ToString());
            return Enum.TryParse(value, out ChievfxMcpTab tab) ? tab : ChievfxMcpTab.Status;
        }

        private static void SaveActiveTab(ChievfxMcpTab tab)
        {
            EditorPrefs.SetString(PrefKey("activeTab"), tab.ToString());
        }

        private static int LoadInt(string key, int defaultValue)
        {
            return EditorPrefs.GetInt(PrefKey(key), defaultValue);
        }

        private static string PrefKey(string key)
        {
            return $"{ChievfxMcpToolPolicy.ServerName}.{key}";
        }
    }
}
