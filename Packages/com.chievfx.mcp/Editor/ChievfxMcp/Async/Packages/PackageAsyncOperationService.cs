#nullable enable
using static Chievfx.Mcp.Editor.McpLimits;
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
    internal sealed partial class PackageAsyncOperationService : BridgeDomainServiceBase
    {
        private readonly List<PendingPackageRequest> pendingRequests = new();

            public bool HasPendingRequests => pendingRequests.Count > 0;

        public bool IsPackageTool(string toolName)
        {
            return toolName switch
            {
                "package-list" => true,
                "package-search" => true,
                "package-add" => true,
                "package-remove" => true,
                _ => false
            };
        }

        public void StartPackageToolRequest(string id, string toolName, JToken args)
        {
            switch (toolName)
            {
                case "package-list":
                    StartPackageListRequest(id, args);
                    break;
                case "package-search":
                    StartPackageSearchRequest(id, args);
                    break;
                case "package-add":
                    StartPackageAddRequest(id, args);
                    break;
                case "package-remove":
                    StartPackageRemoveRequest(id, args);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown package tool '{toolName}'.");
            }

            EventJournal.Write(
                "editor",
                "package-start",
                "info",
                $"{toolName} started.",
                operationId: id,
                data: new Dictionary<string, object?> { ["toolName"] = toolName });
        }

        private void StartPackageListRequest(string id, JToken args)
        {
            var directDependenciesOnly = ReadBool(args, "directDependenciesOnly", false);
            var offlineMode = ReadBool(args, "offlineMode", true);
            var sourceFilter = ReadPackageSourceFilter(args);
            var nameFilter = ReadString(args, "nameFilter");
            var request = PackageManagerClient.List(offlineMode, !directDependenciesOnly);

            pendingRequests.Add(new PendingPackageRequest
            {
                Id = id,
                Kind = PackageRequestKind.List,
                ListRequest = request,
                SourceFilter = sourceFilter,
                NameFilter = nameFilter ?? string.Empty,
                DirectDependenciesOnly = directDependenciesOnly,
                OfflineMode = offlineMode
            });
            OperationStore.MarkWaiting(id, "Waiting for Unity Package Manager package-list.", false);
        }

        private void StartPackageSearchRequest(string id, JToken args)
        {
            var query = ReadRequiredPackageText(args, "query");
            var maxResults = ClampInt(ReadInt(args, "maxResults", DefaultPackageMaxResults), 1, HardPackageMaxResults);
            var offlineMode = ReadBool(args, "offlineMode", true);

            pendingRequests.Add(new PendingPackageRequest
            {
                Id = id,
                Kind = PackageRequestKind.Search,
                ListRequest = PackageManagerClient.List(true, true),
                SearchRequest = PackageManagerClient.SearchAll(offlineMode),
                Query = query,
                MaxResults = maxResults,
                OfflineMode = offlineMode
            });
            OperationStore.MarkWaiting(id, "Waiting for Unity Package Manager package-search.", false);
        }

        private void StartPackageAddRequest(string id, JToken args)
        {
            var packageId = ReadRequiredPackageText(args, "packageId");
            ValidatePackageAddId(packageId);

            var checkpoint = new PackageOperationCheckpoint
            {
                Id = id,
                ToolName = "package-add",
                PackageId = packageId,
                ExpectedPackageName = TryGetRegistryPackageName(packageId) ?? string.Empty,
                CreatedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ManifestDependenciesBefore = ReadManifestDependencies()
            };

            PersistPackageOperationCheckpoint(checkpoint);
            try
            {
                pendingRequests.Add(new PendingPackageRequest
                {
                    Id = id,
                    Kind = PackageRequestKind.Add,
                    AddRequest = PackageManagerClient.Add(packageId),
                    PackageId = packageId,
                    ExpectedPackageName = checkpoint.ExpectedPackageName,
                    Checkpoint = checkpoint
                });
                OperationStore.MarkWaiting(id, "Waiting for Unity Package Manager package-add; Unity may domain reload.", false);
                PersistPendingPackageOperationCheckpoints();
            }
            catch
            {
                RemovePackageOperationCheckpoint(id);
                throw;
            }
        }

        private void StartPackageRemoveRequest(string id, JToken args)
        {
            var packageId = ReadRequiredPackageText(args, "packageId");
            ValidatePackageRemoveId(packageId);
            var manifestDependencies = ReadManifestDependencies();
            if (!manifestDependencies.ContainsKey(packageId))
            {
                throw new InvalidOperationException(
                    $"Package '{packageId}' is not a direct manifest dependency. package-remove only removes direct non-built-in dependencies.");
            }

            var checkpoint = new PackageOperationCheckpoint
            {
                Id = id,
                ToolName = "package-remove",
                PackageId = packageId,
                ExpectedPackageName = packageId,
                CreatedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ManifestDependenciesBefore = manifestDependencies
            };

            PersistPackageOperationCheckpoint(checkpoint);
            try
            {
                pendingRequests.Add(new PendingPackageRequest
                {
                    Id = id,
                    Kind = PackageRequestKind.Remove,
                    RemoveRequest = PackageManagerClient.Remove(packageId),
                    PackageId = packageId,
                    ExpectedPackageName = packageId,
                    Checkpoint = checkpoint
                });
                OperationStore.MarkWaiting(id, "Waiting for Unity Package Manager package-remove; Unity may domain reload.", false);
                PersistPendingPackageOperationCheckpoints();
            }
            catch
            {
                RemovePackageOperationCheckpoint(id);
                throw;
            }
        }

        public void ProcessPendingPackageRequests()
        {
            for (var i = pendingRequests.Count - 1; i >= 0; i--)
            {
                var pending = pendingRequests[i];
                if (OperationStore.IsCancellationRequested(pending.Id) && !pending.CancellationRequested)
                {
                    pending.CancellationRequested = true;
                    OperationStore.MarkCancelRequested(
                        pending.Id,
                        "Cancellation requested, but Unity Package Manager requests are not cancellable. Waiting for completion.");
                }

                if (!IsPackageRequestComplete(pending))
                {
                    continue;
                }

                try
                {
                    Transport.WriteResponse(pending.Id, CompletePackageRequest(pending));
                    OperationStore.Complete(
                        pending.Id,
                        "completed",
                        pending.CancellationRequested
                            ? "Package operation completed after cancellation was requested."
                            : "Package operation completed.");
                    EventJournal.Write(
                        "editor",
                        "package-finish",
                        pending.CancellationRequested ? "warning" : "info",
                        pending.CancellationRequested
                            ? "Package operation finished after cancellation was requested."
                            : "Package operation finished.",
                        operationId: pending.Id,
                        data: new Dictionary<string, object?> { ["kind"] = pending.Kind.ToString(), ["packageId"] = pending.PackageId });
                }
                catch (Exception ex)
                {
                    OperationStore.Complete(pending.Id, "failed", ex.GetBaseException().Message);
                    Transport.WriteResponse(pending.Id, new { ok = false, error = ex.GetBaseException().Message });
                    EventJournal.Write(
                        "editor",
                        "package-finish",
                        "error",
                        $"Package operation failed. {ex.GetBaseException().Message}",
                        operationId: pending.Id,
                        data: new Dictionary<string, object?> { ["kind"] = pending.Kind.ToString(), ["packageId"] = pending.PackageId });
                }
                finally
                {
                    pendingRequests.RemoveAt(i);
                    PersistPendingPackageOperationCheckpoints();
                }
            }
        }

        private bool IsPackageRequestComplete(PendingPackageRequest pending)
        {
            return pending.Kind switch
            {
                PackageRequestKind.List => pending.ListRequest?.IsCompleted == true,
                PackageRequestKind.Search => pending.ListRequest?.IsCompleted == true && pending.SearchRequest?.IsCompleted == true,
                PackageRequestKind.Add => pending.AddRequest?.IsCompleted == true,
                PackageRequestKind.Remove => pending.RemoveRequest?.IsCompleted == true,
                PackageRequestKind.VerifyAfterReload => pending.ListRequest?.IsCompleted == true,
                _ => false
            };
        }

        private object CompletePackageRequest(PendingPackageRequest pending)
        {
            return pending.Kind switch
            {
                PackageRequestKind.List => CompletePackageListRequest(pending),
                PackageRequestKind.Search => CompletePackageSearchRequest(pending),
                PackageRequestKind.Add => CompletePackageAddRequest(pending),
                PackageRequestKind.Remove => CompletePackageRemoveRequest(pending),
                PackageRequestKind.VerifyAfterReload => CompletePackageReloadVerification(pending),
                _ => new { ok = false, error = $"Unknown package request kind '{pending.Kind}'." }
            };
        }

        private object CompletePackageListRequest(PendingPackageRequest pending)
        {
            var request = pending.ListRequest ?? throw new InvalidOperationException("package-list request was not started.");
            ThrowIfPackageRequestFailed(request, "package-list");
            return new
            {
                ok = true,
                contentType = "json",
                result = CreatePackageListResult(
                    ToPackageInfoArray(request.Result),
                    pending.SourceFilter,
                    pending.NameFilter,
                    pending.DirectDependenciesOnly,
                    pending.OfflineMode)
            };
        }

        private object CompletePackageSearchRequest(PendingPackageRequest pending)
        {
            var listRequest = pending.ListRequest ?? throw new InvalidOperationException("package-search installed package request was not started.");
            var searchRequest = pending.SearchRequest ?? throw new InvalidOperationException("package-search registry request was not started.");
            ThrowIfPackageRequestFailed(listRequest, "package-search installed list");

            string? registrySearchError = null;
            IEnumerable<PackageManagerPackageInfo> registryPackages = Array.Empty<PackageManagerPackageInfo>();
            if (searchRequest.Status == StatusCode.Failure)
            {
                registrySearchError = FormatPackageError(searchRequest.Error);
            }
            else
            {
                registryPackages = ToPackageInfoArray(searchRequest.Result);
            }

            return new
            {
                ok = true,
                contentType = "json",
                result = CreatePackageSearchResult(
                    pending.Query,
                    pending.MaxResults,
                    pending.OfflineMode,
                    ToPackageInfoArray(listRequest.Result),
                    registryPackages,
                    registrySearchError)
            };
        }

        private object CompletePackageAddRequest(PendingPackageRequest pending)
        {
            var request = pending.AddRequest ?? throw new InvalidOperationException("package-add request was not started.");
            ThrowIfPackageRequestFailed(request, "package-add");
            var manifestDependenciesAfter = ReadManifestDependencies();
            var package = request.Result;
            var directDependencies = ReadManifestDependencies();
            return CreatePackageMutationResponse(
                operation: "add",
                packageId: pending.PackageId,
                completed: true,
                restoredAfterDomainReload: false,
                package: package != null ? PackageInfoToDto(package, directDependencies) : null,
                manifestChanges: GetManifestDependencyChanges(pending.Checkpoint?.ManifestDependenciesBefore, manifestDependenciesAfter),
                verification: "request-completed");
        }

        private object CompletePackageRemoveRequest(PendingPackageRequest pending)
        {
            var request = pending.RemoveRequest ?? throw new InvalidOperationException("package-remove request was not started.");
            ThrowIfPackageRequestFailed(request, "package-remove");
            var manifestDependenciesAfter = ReadManifestDependencies();
            return CreatePackageMutationResponse(
                operation: "remove",
                packageId: pending.PackageId,
                completed: true,
                restoredAfterDomainReload: false,
                package: null,
                manifestChanges: GetManifestDependencyChanges(pending.Checkpoint?.ManifestDependenciesBefore, manifestDependenciesAfter),
                verification: "request-completed");
        }

    }
}
