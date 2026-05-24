#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEditor.SceneManagement;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;
using PackageManagerClient = UnityEditor.PackageManager.Client;
using PackageManagerPackageInfo = UnityEditor.PackageManager.PackageInfo;


namespace Chievfx.Mcp.Editor
{
    internal sealed partial class PackageAsyncOperationService
    {
        private bool restored;

        public void RestorePendingPackageOperations()
        {
            if (restored)
            {
                return;
            }

            restored = true;
            foreach (var checkpoint in LoadPackageOperationCheckpoints())
            {
                if (pendingRequests.Any(pending => string.Equals(pending.Id, checkpoint.Id, StringComparison.Ordinal)))
                {
                    continue;
                }

                pendingRequests.Add(new PendingPackageRequest
                {
                    Id = checkpoint.Id,
                    Kind = PackageRequestKind.VerifyAfterReload,
                    ListRequest = PackageManagerClient.List(true, true),
                    PackageId = checkpoint.PackageId,
                    ExpectedPackageName = checkpoint.ExpectedPackageName,
                    Checkpoint = checkpoint,
                    RestoredAfterDomainReload = true
                });
                OperationStore.MarkWaiting(checkpoint.Id, "Restored package operation after domain reload; verifying result.", false);
            }
        }

        private PackageOperationCheckpoint[] LoadPackageOperationCheckpoints()
        {
            var payload = SessionState.GetString(BridgeRuntimeState.PendingPackageOperationsSessionKey, string.Empty);
            if (string.IsNullOrWhiteSpace(payload))
            {
                return Array.Empty<PackageOperationCheckpoint>();
            }

            try
            {
                return JsonConvert.DeserializeObject<PackageOperationCheckpoint[]>(payload, BridgeRuntimeState.JsonOptions) ?? Array.Empty<PackageOperationCheckpoint>();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"ChievFX MCP could not restore pending package operations. {ex.GetBaseException().Message}");
                SessionState.SetString(BridgeRuntimeState.PendingPackageOperationsSessionKey, string.Empty);
                return Array.Empty<PackageOperationCheckpoint>();
            }
        }

        private void PersistPackageOperationCheckpoint(PackageOperationCheckpoint checkpoint)
        {
            var checkpoints = LoadPackageOperationCheckpoints()
                .Where(existing => !string.Equals(existing.Id, checkpoint.Id, StringComparison.Ordinal))
                .Concat(new[] { checkpoint })
                .ToArray();
            PersistPackageOperationCheckpoints(checkpoints);
        }

        private void RemovePackageOperationCheckpoint(string id)
        {
            PersistPackageOperationCheckpoints(LoadPackageOperationCheckpoints()
                .Where(checkpoint => !string.Equals(checkpoint.Id, id, StringComparison.Ordinal))
                .ToArray());
        }

        private void PersistPendingPackageOperationCheckpoints()
        {
            PersistPackageOperationCheckpoints(pendingRequests
                .Select(pending => pending.Checkpoint)
                .Where(checkpoint => checkpoint != null)
                .Select(checkpoint => checkpoint!)
                .ToArray());
        }

        private void PersistPackageOperationCheckpoints(PackageOperationCheckpoint[] checkpoints)
        {
            SessionState.SetString(
                BridgeRuntimeState.PendingPackageOperationsSessionKey,
                checkpoints.Length == 0 ? string.Empty : JsonConvert.SerializeObject(checkpoints, BridgeRuntimeState.JsonOptions));
        }

    }
}
