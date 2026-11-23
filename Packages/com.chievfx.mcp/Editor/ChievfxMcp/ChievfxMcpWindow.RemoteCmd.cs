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
        // HTTP-only by design: the tool exists so a second machine can reach this host's shell through
        // the MCP endpoint. Under stdio the client already runs here and has its own shell, so the card
        // stays hidden and the Python gate refuses to arm.
        private VisualElement CreateRemoteCommandCard()
        {
            var card = CreateSectionCard("Remote Command Pass-through (remote-cmd)");
            remoteCmdCard = card;

            card.Add(CreateMutedLabel(
                "Exposes a remote-cmd MCP tool that runs command lines on THIS machine for whichever client is "
                + "connected over HTTP. Off unless enabled here: no preset, role, or \"enable all\" can turn it on."));

            remoteCmdEnabledToggle = new Toggle("Enable remote-cmd tool")
            {
                value = ChievfxMcpToolPolicy.RemoteCmdEnabled,
                tooltip = "When on, HTTP clients that present the security key can run commands on this machine."
            };
            remoteCmdEnabledToggle.RegisterValueChangedCallback(evt =>
                ApplyRemoteCmdChange(ChievfxMcpToolPolicy.RemoteCmdEnabledKey, evt.newValue));
            card.Add(remoteCmdEnabledToggle);

            remoteCmdPatternField = new TextField("Allowed programs (regex)")
            {
                value = ChievfxMcpToolPolicy.RemoteCmdAllowPattern,
                tooltip =
                    "Regex matched against the program being run — its executable name, or its full path when the "
                    + "pattern itself contains a path separator. It is NOT matched against the arguments: arguments "
                    + "cannot be filtered meaningfully (adb's own `shell` subcommand is a shell, and Unity's "
                    + "-executeMethod runs project C#), so this narrows which programs may run, not what they do.\n\n"
                    + "While a pattern is set, each call must be a single-line invocation of one program: pipes, "
                    + "redirection, chaining, command substitution and multi-line input are refused, and interpreters "
                    + "(sh, bash, cmd, powershell, python, ...) are refused whatever the pattern says, because "
                    + "allowing one would bypass the filter entirely.\n\nEmpty = no restriction at all."
            };
            remoteCmdPatternField.RegisterValueChangedCallback(evt =>
                ApplyRemoteCmdChange(ChievfxMcpToolPolicy.RemoteCmdAllowPatternKey, evt.newValue));
            card.Add(remoteCmdPatternField);
            card.Add(CreateMutedLabel($"Default: {ChievfxMcpToolPolicy.DefaultRemoteCmdAllowPattern} (Unity CLI and adb)."));

            remoteCmdUnrestrictedHelpBox = new HelpBox(string.Empty, HelpBoxMessageType.Error);
            card.Add(remoteCmdUnrestrictedHelpBox);

            remoteCmdUnrestrictedAckToggle = new Toggle("I understand: any command may run on this machine")
            {
                value = ChievfxMcpToolPolicy.RemoteCmdUnrestrictedAck,
                tooltip = "Required before an empty program filter is accepted. The tool stays unavailable without it."
            };
            remoteCmdUnrestrictedAckToggle.RegisterValueChangedCallback(evt =>
                ApplyRemoteCmdChange(ChievfxMcpToolPolicy.RemoteCmdUnrestrictedAckKey, evt.newValue));
            card.Add(remoteCmdUnrestrictedAckToggle);

            remoteCmdSecurityKeyField = new TextField("Security key")
            {
                value = ChievfxMcpToolPolicy.RemoteCmdSecurityKey,
                tooltip =
                    $"Shared secret every HTTP request must carry as the {ChievfxMcpToolPolicy.RemoteCmdSecurityKeyHeader} "
                    + "header (Authorization: Bearer <key> also works). Written into the MCP client config for this "
                    + "project. A browser cannot set custom headers on a cross-origin request, so this also keeps a "
                    + "web page the user has open from driving this port."
            };
            remoteCmdSecurityKeyField.RegisterValueChangedCallback(evt =>
                ApplyRemoteCmdChange(ChievfxMcpToolPolicy.RemoteCmdSecurityKeyKey, evt.newValue));
            card.Add(remoteCmdSecurityKeyField);
            remoteCmdSecurityKeyHelpBox = new HelpBox(string.Empty, HelpBoxMessageType.Warning);
            card.Add(remoteCmdSecurityKeyHelpBox);
            card.Add(CreateActionRow(
                CreateButton("Regenerate Key", RegenerateRemoteCmdSecurityKey),
                CreateButton("Copy Key", CopyRemoteCmdSecurityKey),
                CreateButton("Open Audit Log", OpenRemoteCmdAuditLog)));

            remoteCmdAllowedIpsField = new TextField("Allowed client IPs")
            {
                value = ChievfxMcpToolPolicy.RemoteCmdAllowedIps,
                tooltip = "Comma-separated IPs or CIDR ranges allowed to call remote-cmd, e.g. 127.0.0.1, 192.168.1.0/24. Empty = any address that can reach the port."
            };
            remoteCmdAllowedIpsField.RegisterValueChangedCallback(evt =>
                ApplyRemoteCmdChange(ChievfxMcpToolPolicy.RemoteCmdAllowedIpsKey, evt.newValue));
            card.Add(remoteCmdAllowedIpsField);
            remoteCmdAllowedIpsHelpBox = new HelpBox(string.Empty, HelpBoxMessageType.Warning);
            card.Add(remoteCmdAllowedIpsHelpBox);

            remoteCmdBindHostField = new TextField("HTTP bind address")
            {
                value = ChievfxMcpToolPolicy.RemoteCmdBindHost,
                tooltip =
                    "Address the HTTP server binds. 127.0.0.1 (default) is not reachable from other machines — "
                    + "forward it over SSH (ssh -L 27247:127.0.0.1:27247 thishost) to keep the port private. "
                    + "0.0.0.0 exposes it to the network, and then every request must carry the security key."
            };
            remoteCmdBindHostField.RegisterValueChangedCallback(evt =>
                ApplyRemoteCmdChange(ChievfxMcpToolPolicy.RemoteCmdBindHostKey, evt.newValue));
            card.Add(remoteCmdBindHostField);
            remoteCmdBindHostHelpBox = new HelpBox(string.Empty, HelpBoxMessageType.Warning);
            card.Add(remoteCmdBindHostHelpBox);

            remoteCmdTimeoutField = new IntegerField("Command timeout ms")
            {
                value = ChievfxMcpToolPolicy.RemoteCmdTimeoutMs,
                tooltip = "A command that outlives this is killed with its whole process tree. Callers may pass a smaller value."
            };
            remoteCmdTimeoutField.RegisterValueChangedCallback(evt =>
                ApplyRemoteCmdChange(ChievfxMcpToolPolicy.RemoteCmdTimeoutMsKey, Math.Max(1000, evt.newValue)));
            card.Add(remoteCmdTimeoutField);

            remoteCmdMaxOutputField = new IntegerField("Max output bytes")
            {
                value = ChievfxMcpToolPolicy.RemoteCmdMaxOutputBytes,
                tooltip = "Cap for stdout and for stderr, each. Keeps a chatty command (adb logcat, a full build log) from filling the MCP response."
            };
            remoteCmdMaxOutputField.RegisterValueChangedCallback(evt =>
                ApplyRemoteCmdChange(ChievfxMcpToolPolicy.RemoteCmdMaxOutputBytesKey, Math.Max(1024, evt.newValue)));
            card.Add(remoteCmdMaxOutputField);

            remoteCmdStatusLabel = CreateMutedLabel(string.Empty);
            card.Add(remoteCmdStatusLabel);
            return card;
        }

        private void ApplyRemoteCmdChange(string prefsKey, bool value)
        {
            EditorPrefs.SetBool(prefsKey, value);
            AfterRemoteCmdChange();
        }

        private void ApplyRemoteCmdChange(string prefsKey, string value)
        {
            EditorPrefs.SetString(prefsKey, value ?? string.Empty);
            AfterRemoteCmdChange();
        }

        private void ApplyRemoteCmdChange(string prefsKey, int value)
        {
            EditorPrefs.SetInt(prefsKey, value);
            AfterRemoteCmdChange();
        }

        // The mirror file is what the Python server reads, and the security key travels in the client
        // config, so both are rewritten on every edit: a key changed here and not there locks the client
        // out with a 401 that looks like a broken server.
        private void AfterRemoteCmdChange()
        {
            ChievfxMcpToolPolicy.WriteRemoteCmdSettings();
            if (ChievfxMcpToolPolicy.AutoWriteClientConfigs)
            {
                EnsureAllClientConfigs();
            }

            RefreshUi();
        }

        private void RegenerateRemoteCmdSecurityKey()
        {
            var key = Guid.NewGuid().ToString("N");
            EditorPrefs.SetString(ChievfxMcpToolPolicy.RemoteCmdSecurityKeyKey, key);
            if (remoteCmdSecurityKeyField != null)
            {
                remoteCmdSecurityKeyField.SetValueWithoutNotify(key);
            }

            AfterRemoteCmdChange();
            EditorUtility.DisplayDialog(
                "ChievFX MCP",
                "New remote-cmd security key generated. Reload MCP tools in the client (or restart it) so it picks up the new header.",
                "OK");
        }

        private void CopyRemoteCmdSecurityKey()
        {
            EditorGUIUtility.systemCopyBuffer = ChievfxMcpToolPolicy.RemoteCmdSecurityKey;
        }

        private void OpenRemoteCmdAuditLog()
        {
            var path = Path.Combine(ChievfxMcpToolPolicy.BridgeDirectory, "remote-cmd-audit.log");
            if (!File.Exists(path))
            {
                EditorUtility.DisplayDialog("ChievFX MCP", $"No remote-cmd calls have been recorded yet.\n\n{path}", "OK");
                return;
            }

            EditorUtility.RevealInFinder(path);
        }

        private void RefreshRemoteCommandCard(string transport)
        {
            if (remoteCmdCard == null)
            {
                return;
            }

            var isHttp = transport == TransportHttp;
            remoteCmdCard.style.display = isHttp ? DisplayStyle.Flex : DisplayStyle.None;
            if (!isHttp)
            {
                return;
            }

            var enabled = ChievfxMcpToolPolicy.RemoteCmdEnabled;
            var pattern = ChievfxMcpToolPolicy.RemoteCmdAllowPattern;
            var unrestricted = string.IsNullOrWhiteSpace(pattern);
            var acknowledged = ChievfxMcpToolPolicy.RemoteCmdUnrestrictedAck;
            var key = ChievfxMcpToolPolicy.RemoteCmdSecurityKey;
            var allowedIps = ChievfxMcpToolPolicy.ParseRemoteCmdAllowedIps(ChievfxMcpToolPolicy.RemoteCmdAllowedIps);
            var loopback = ChievfxMcpToolPolicy.RemoteCmdBindHostIsLoopback;

            foreach (var element in new VisualElement?[]
                     {
                         remoteCmdPatternField, remoteCmdSecurityKeyField, remoteCmdAllowedIpsField,
                         remoteCmdBindHostField, remoteCmdTimeoutField, remoteCmdMaxOutputField,
                         remoteCmdUnrestrictedAckToggle
                     })
            {
                element?.SetEnabled(enabled);
            }

            if (remoteCmdUnrestrictedAckToggle != null)
            {
                remoteCmdUnrestrictedAckToggle.style.display = unrestricted ? DisplayStyle.Flex : DisplayStyle.None;
            }

            if (remoteCmdUnrestrictedHelpBox != null)
            {
                remoteCmdUnrestrictedHelpBox.style.display = enabled && unrestricted ? DisplayStyle.Flex : DisplayStyle.None;
                remoteCmdUnrestrictedHelpBox.text =
                    "The program filter is empty: ANY command — including multi-line shell scripts — can be run on this "
                    + "machine by whoever holds the security key. That is full control of this user account: reading and "
                    + "writing any file it can reach, installing software, and using its credentials. Only leave it empty "
                    + "for a host you would hand someone the keyboard to."
                    + (acknowledged ? string.Empty : " remote-cmd stays unavailable until this is acknowledged below.");
            }

            if (remoteCmdSecurityKeyHelpBox != null)
            {
                var keyMissing = enabled && string.IsNullOrWhiteSpace(key);
                remoteCmdSecurityKeyHelpBox.style.display = keyMissing ? DisplayStyle.Flex : DisplayStyle.None;
                remoteCmdSecurityKeyHelpBox.text = "A non-empty security key is required; remote-cmd stays unavailable without one.";
            }

            if (remoteCmdAllowedIpsHelpBox != null)
            {
                var anyIp = enabled && allowedIps.Length == 0;
                remoteCmdAllowedIpsHelpBox.style.display = anyIp ? DisplayStyle.Flex : DisplayStyle.None;
                remoteCmdAllowedIpsHelpBox.text = loopback
                    ? "The list is empty, so a command from ANY address that can reach this port is accepted. The bind "
                      + "address is loopback, so today that means this machine (and anything tunnelling into it)."
                    : "The list is empty, so a command from ANY IP that can reach this port is accepted, and the port is "
                      + "bound to a network address. Name the machines you actually use.";
            }

            if (remoteCmdBindHostHelpBox != null)
            {
                remoteCmdBindHostHelpBox.style.display = loopback ? DisplayStyle.None : DisplayStyle.Flex;
                remoteCmdBindHostHelpBox.text =
                    $"Bound to {ChievfxMcpToolPolicy.RemoteCmdBindHost}: this port is reachable from other machines on the "
                    + "network, so every MCP request must carry the security key. An SSH tunnel to 127.0.0.1 is the safer "
                    + "way to reach it from another machine.";
            }

            if (remoteCmdStatusLabel != null)
            {
                remoteCmdStatusLabel.text = BuildRemoteCmdStatusText(enabled, unrestricted, acknowledged, key, allowedIps.Length, loopback);
            }
        }

        // Mirrors the Python gate (remote_cmd_gate) so the window never claims the tool is live when the
        // server would refuse to expose it.
        private static string BuildRemoteCmdStatusText(
            bool enabled,
            bool unrestricted,
            bool acknowledged,
            string key,
            int allowedIpCount,
            bool loopback)
        {
            if (!enabled)
            {
                return "remote-cmd is not exposed. The MCP server omits the tool entirely while this is off.";
            }

            if (string.IsNullOrWhiteSpace(key))
            {
                return "remote-cmd is NOT exposed: set a security key.";
            }

            if (unrestricted && !acknowledged)
            {
                return "remote-cmd is NOT exposed: the filter is empty and the acknowledgement above is unchecked.";
            }

            var scope = unrestricted ? "any command" : "programs matching the filter, one invocation per call";
            var reach = loopback ? "this machine only (tunnel in from elsewhere)" : "any host that can reach the port";
            var ips = allowedIpCount == 0 ? "any IP" : $"{allowedIpCount} allowed IP entr{(allowedIpCount == 1 ? "y" : "ies")}";
            return $"remote-cmd is exposed to HTTP clients: {scope}; reachable from {reach}; {ips}; key required. "
                   + "Restart the HTTP server after changing the bind address.";
        }

        private static string HttpUrl(int port)
        {
            return $"http://127.0.0.1:{port}";
        }

        public static bool ShowExperimentalPromptsTab => EditorPrefs.GetBool(ShowExperimentalPromptsEditorPrefsKey, false);

        public static bool ShowExperimentalAutonomyTools => EditorPrefs.GetBool(ShowExperimentalAutonomyToolsEditorPrefsKey, false);
    }
}
