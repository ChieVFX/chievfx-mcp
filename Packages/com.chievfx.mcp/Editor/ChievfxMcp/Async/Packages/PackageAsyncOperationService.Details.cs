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
    internal sealed partial class PackageAsyncOperationService
    {
        private static object CompletePackageReloadVerification(PendingPackageRequest pending)
        {
            var request = pending.ListRequest ?? throw new InvalidOperationException("package operation verification request was not started.");
            ThrowIfPackageRequestFailed(request, "package operation verification");
            var checkpoint = pending.Checkpoint ?? throw new InvalidOperationException("package operation verification checkpoint is missing.");
            var manifestDependenciesAfter = ReadManifestDependencies();
            var manifestChanges = GetManifestDependencyChanges(checkpoint.ManifestDependenciesBefore, manifestDependenciesAfter);
            var installedPackage = ToPackageInfoArray(request.Result).FirstOrDefault(package => string.Equals(package.name, checkpoint.ExpectedPackageName, StringComparison.Ordinal));

            if (string.Equals(checkpoint.ToolName, "package-add", StringComparison.Ordinal))
            {
                var verified = manifestChanges.Length > 0 || installedPackage != null;
                if (!verified)
                {
                    throw new InvalidOperationException(
                        $"package-add for '{checkpoint.PackageId}' survived a domain reload, but the package could not be verified. Run package-list and retry if needed.");
                }

                var directDependencies = ReadManifestDependencies();
                return CreatePackageMutationResponse(
                    operation: "add",
                    packageId: checkpoint.PackageId,
                    completed: true,
                    restoredAfterDomainReload: true,
                    package: installedPackage != null ? PackageInfoToDto(installedPackage, directDependencies) : null,
                    manifestChanges: manifestChanges,
                    verification: installedPackage != null ? "package-list" : "manifest-diff");
            }

            if (string.Equals(checkpoint.ToolName, "package-remove", StringComparison.Ordinal))
            {
                var verified = !manifestDependenciesAfter.ContainsKey(checkpoint.PackageId);
                if (!verified)
                {
                    throw new InvalidOperationException(
                        $"package-remove for '{checkpoint.PackageId}' survived a domain reload, but the package is still listed in manifest.json.");
                }

                return CreatePackageMutationResponse(
                    operation: "remove",
                    packageId: checkpoint.PackageId,
                    completed: true,
                    restoredAfterDomainReload: true,
                    package: null,
                    manifestChanges: manifestChanges,
                    verification: "manifest");
            }

            throw new InvalidOperationException($"Unknown persisted package operation '{checkpoint.ToolName}'.");
        }

        private static PackageManagerPackageInfo[] ToPackageInfoArray(PackageCollection packages)
        {
            var result = new List<PackageManagerPackageInfo>();
            foreach (PackageManagerPackageInfo package in packages)
            {
                result.Add(package);
            }

            return result.ToArray();
        }

        private static PackageManagerPackageInfo[] ToPackageInfoArray(IEnumerable<PackageManagerPackageInfo> packages)
        {
            return packages.ToArray();
        }

        private static object CreatePackageListResult(
            IEnumerable<PackageManagerPackageInfo> packages,
            PackageSourceFilter sourceFilter,
            string nameFilter,
            bool directDependenciesOnly,
            bool offlineMode)
        {
            var manifestDependencies = ReadManifestDependencies();
            var comparison = StringComparison.OrdinalIgnoreCase;
            var filtered = packages
                .Where(package => sourceFilter == PackageSourceFilter.All || string.Equals(package.source.ToString(), sourceFilter.ToString(), StringComparison.Ordinal))
                .Where(package => !directDependenciesOnly || IsDirectDependency(package, manifestDependencies))
                .Where(package => string.IsNullOrWhiteSpace(nameFilter)
                    || package.name.IndexOf(nameFilter, comparison) >= 0
                    || package.displayName.IndexOf(nameFilter, comparison) >= 0
                    || package.description.IndexOf(nameFilter, comparison) >= 0)
                .OrderByDescending(package => IsDirectDependency(package, manifestDependencies))
                .ThenBy(package => package.name, StringComparer.Ordinal)
                .Select(package => PackageInfoToDto(package, manifestDependencies))
                .ToArray();

            return new
            {
                count = filtered.Length,
                sourceFilter = sourceFilter.ToString(),
                directDependenciesOnly,
                offlineMode,
                packages = filtered
            };
        }

        private static object CreatePackageSearchResult(
            string query,
            int maxResults,
            bool offlineMode,
            IEnumerable<PackageManagerPackageInfo> installedPackages,
            IEnumerable<PackageManagerPackageInfo> registryPackages,
            string? registrySearchError)
        {
            var installedByName = installedPackages
                .GroupBy(package => package.name, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var candidates = registryPackages
                .Concat(installedPackages)
                .Select(package => new PackageSearchCandidate
                {
                    Package = package,
                    Rank = GetPackageSearchRank(package, query),
                    IsInstalled = installedByName.ContainsKey(package.name)
                })
                .Where(candidate => candidate.Rank < int.MaxValue)
                .GroupBy(candidate => candidate.Package.name, StringComparer.Ordinal)
                .Select(group => group
                    .OrderBy(candidate => candidate.Rank)
                    .ThenByDescending(candidate => candidate.IsInstalled)
                    .First())
                .OrderBy(candidate => candidate.Rank)
                .ThenByDescending(candidate => candidate.IsInstalled)
                .ThenBy(candidate => candidate.Package.name, StringComparer.Ordinal)
                .Take(maxResults + 1)
                .ToArray();
            var truncated = candidates.Length > maxResults;
            var results = candidates
                .Take(maxResults)
                .Select(candidate => PackageSearchCandidateToDto(candidate, installedByName))
                .ToArray();

            return new
            {
                query,
                count = results.Length,
                truncated,
                offlineMode,
                registrySearchError,
                results
            };
        }

        private static Dictionary<string, object?> PackageInfoToDto(PackageManagerPackageInfo package, IReadOnlyDictionary<string, string> manifestDependencies)
        {
            var truncated = false;
            return new Dictionary<string, object?>
            {
                ["name"] = package.name,
                ["displayName"] = package.displayName,
                ["version"] = package.version,
                ["source"] = package.source.ToString(),
                ["type"] = package.type,
                ["category"] = package.category,
                ["description"] = TrimText(package.description ?? string.Empty, 500, ref truncated),
                ["descriptionTruncated"] = truncated,
                ["isDirectDependency"] = IsDirectDependency(package, manifestDependencies),
                ["manifestVersion"] = manifestDependencies.TryGetValue(package.name, out var manifestVersion) ? manifestVersion : null,
                ["resolvedPath"] = package.resolvedPath,
                ["assetPath"] = package.assetPath,
                ["dependencyCount"] = package.dependencies?.Length ?? 0
            };
        }

        private static Dictionary<string, object?> PackageSearchCandidateToDto(
            PackageSearchCandidate candidate,
            IReadOnlyDictionary<string, PackageManagerPackageInfo> installedByName)
        {
            installedByName.TryGetValue(candidate.Package.name, out var installedPackage);
            var package = candidate.Package;
            var installed = installedPackage != null;
            var truncated = false;
            return new Dictionary<string, object?>
            {
                ["name"] = package.name,
                ["displayName"] = package.displayName,
                ["latestVersion"] = GetLatestPackageVersion(package),
                ["description"] = TrimText(package.description ?? string.Empty, 500, ref truncated),
                ["descriptionTruncated"] = truncated,
                ["isInstalled"] = installed,
                ["installedVersion"] = installedPackage?.version,
                ["installedSource"] = installedPackage?.source.ToString(),
                ["availableVersions"] = GetAvailablePackageVersions(package, 5),
                ["matchRank"] = candidate.Rank
            };
        }

        private static object CreatePackageMutationResponse(
            string operation,
            string packageId,
            bool completed,
            bool restoredAfterDomainReload,
            object? package,
            object[] manifestChanges,
            string verification)
        {
            return new
            {
                ok = true,
                contentType = "json",
                result = new
                {
                    operation,
                    packageId,
                    completed,
                    restoredAfterDomainReload,
                    verification,
                    package,
                    manifestChanges
                }
            };
        }

        private static void ThrowIfPackageRequestFailed(Request request, string operation)
        {
            if (request.Status == StatusCode.Failure)
            {
                throw new InvalidOperationException($"{operation} failed: {FormatPackageError(request.Error)}");
            }
        }

        private static string FormatPackageError(UnityEditor.PackageManager.Error? error)
        {
            if (error == null)
            {
                return "Unity Package Manager returned an unknown error.";
            }

            return string.IsNullOrWhiteSpace(error.message)
                ? $"Unity Package Manager error {error.errorCode}."
                : $"{error.message} (code {error.errorCode})";
        }

        private static int GetPackageSearchRank(PackageManagerPackageInfo package, string query)
        {
            if (string.Equals(package.name, query, StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            if (string.Equals(package.displayName, query, StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            if (package.name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            {
                return 2;
            }

            if (package.displayName.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            {
                return 3;
            }

            if (package.name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 4;
            }

            if (package.displayName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 5;
            }

            if ((package.description ?? string.Empty).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 6;
            }

            return int.MaxValue;
        }

        private static string GetLatestPackageVersion(PackageManagerPackageInfo package)
        {
            var versions = GetPackageVersionsObject(package);
            foreach (var propertyName in new[] { "latestCompatible", "latest", "recommended", "verified" })
            {
                var value = ReadStringProperty(versions, propertyName);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value!;
                }
            }

            return package.version ?? string.Empty;
        }

        private static string[] GetAvailablePackageVersions(PackageManagerPackageInfo package, int maxResults)
        {
            var versions = GetPackageVersionsObject(package);
            foreach (var propertyName in new[] { "compatible", "all" })
            {
                var values = ReadStringEnumerableProperty(versions, propertyName);
                if (values.Length > 0)
                {
                    return values
                        .Reverse()
                        .Distinct(StringComparer.Ordinal)
                        .Take(maxResults)
                        .ToArray();
                }
            }

            return string.IsNullOrWhiteSpace(package.version)
                ? Array.Empty<string>()
                : new[] { package.version };
        }

        private static object? GetPackageVersionsObject(PackageManagerPackageInfo package)
        {
            return package.GetType().GetProperty("versions", BindingFlags.Instance | BindingFlags.Public)?.GetValue(package);
        }

        private static string? ReadStringProperty(object? source, string propertyName)
        {
            return source?.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)?.GetValue(source) as string;
        }

        private static string[] ReadStringEnumerableProperty(object? source, string propertyName)
        {
            if (source?.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)?.GetValue(source) is IEnumerable<string> values)
            {
                return values.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
            }

            return Array.Empty<string>();
        }

        private static bool IsDirectDependency(PackageManagerPackageInfo package, IReadOnlyDictionary<string, string> manifestDependencies)
        {
            return package.isDirectDependency || manifestDependencies.ContainsKey(package.name);
        }

        private static string ReadRequiredPackageText(JToken args, string parameterName)
        {
            var value = ReadString(args, parameterName);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException($"{parameterName} is required.", parameterName);
            }

            value = value!.Trim();
            if (value.Length > MaxPackageIdChars)
            {
                throw new ArgumentException($"{parameterName} must be {MaxPackageIdChars} characters or fewer.", parameterName);
            }

            if (value.Any(char.IsControl))
            {
                throw new ArgumentException($"{parameterName} cannot contain control characters.", parameterName);
            }

            return value;
        }

        private static void ValidatePackageAddId(string packageId)
        {
            if (IsLocalPackageId(packageId))
            {
                ValidateLocalPackageId(packageId);
                return;
            }

            if (IsGitPackageId(packageId))
            {
                ValidateGitPackageId(packageId);
                return;
            }

            ValidateRegistryPackageReference(packageId);
        }

        private static void ValidatePackageRemoveId(string packageId)
        {
            if (packageId.Contains("@", StringComparison.Ordinal))
            {
                throw new ArgumentException("packageId for package-remove must be a package name without version.", nameof(packageId));
            }

            if (!RuntimeState.RegistryPackageIdPattern.IsMatch(packageId))
            {
                throw new ArgumentException("packageId for package-remove must be a valid Unity package id such as 'com.company.package'.", nameof(packageId));
            }

            if (packageId.StartsWith("com.unity.modules.", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Package '{packageId}' is a built-in Unity module and cannot be removed by this tool.");
            }
        }

        private static void ValidateRegistryPackageReference(string packageId)
        {
            var atIndex = packageId.IndexOf('@');
            var packageName = atIndex >= 0 ? packageId.Substring(0, atIndex) : packageId;
            var version = atIndex >= 0 ? packageId.Substring(atIndex + 1) : string.Empty;
            if (string.IsNullOrWhiteSpace(packageName) || !RuntimeState.RegistryPackageIdPattern.IsMatch(packageName))
            {
                throw new ArgumentException("packageId must be a valid Unity package id, git URL, or file: local package path.", nameof(packageId));
            }

            if (atIndex >= 0 && string.IsNullOrWhiteSpace(version))
            {
                throw new ArgumentException("packageId version cannot be empty after '@'.", nameof(packageId));
            }

            if (atIndex >= 0 && version.Any(char.IsWhiteSpace))
            {
                throw new ArgumentException("packageId version cannot contain whitespace.", nameof(packageId));
            }
        }

        private static bool IsLocalPackageId(string packageId)
        {
            return packageId.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
        }

        private static void ValidateLocalPackageId(string packageId)
        {
            var path = packageId.Substring("file:".Length);
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("file: packageId must include a local package path.", nameof(packageId));
            }

            var fullPath = Path.IsPathRooted(path)
                ? path
                : Path.GetFullPath(Path.Combine(ChievfxMcpToolPolicy.ProjectRoot, path));
            if (!Directory.Exists(fullPath))
            {
                throw new InvalidOperationException($"Local package folder does not exist: '{fullPath}'.");
            }

            if (!File.Exists(Path.Combine(fullPath, "package.json")))
            {
                throw new InvalidOperationException($"Local package folder must contain package.json: '{fullPath}'.");
            }
        }

        private static bool IsGitPackageId(string packageId)
        {
            return packageId.StartsWith("git@", StringComparison.OrdinalIgnoreCase)
                || packageId.StartsWith("git://", StringComparison.OrdinalIgnoreCase)
                || packageId.StartsWith("git+ssh://", StringComparison.OrdinalIgnoreCase)
                || packageId.StartsWith("git+https://", StringComparison.OrdinalIgnoreCase)
                || packageId.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)
                || packageId.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
                || packageId.Contains(".git?", StringComparison.OrdinalIgnoreCase)
                || packageId.Contains(".git#", StringComparison.OrdinalIgnoreCase);
        }

        private static void ValidateGitPackageId(string packageId)
        {
            if (packageId.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
            {
                if (!packageId.Contains(":", StringComparison.Ordinal) || !packageId.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("SCP-style git packageId must look like 'git@host:owner/repo.git'.", nameof(packageId));
                }

                return;
            }

            var uriText = packageId.StartsWith("git+", StringComparison.OrdinalIgnoreCase)
                ? packageId.Substring("git+".Length)
                : packageId;
            if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri))
            {
                throw new ArgumentException("Git packageId must be an absolute git URL.", nameof(packageId));
            }

            if (uri.Scheme != Uri.UriSchemeHttp
                && uri.Scheme != Uri.UriSchemeHttps
                && !string.Equals(uri.Scheme, "ssh", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Scheme, "git", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Git packageId must use https, http, ssh, or git scheme.", nameof(packageId));
            }
        }

        private static string? TryGetRegistryPackageName(string packageId)
        {
            if (IsLocalPackageId(packageId) || IsGitPackageId(packageId))
            {
                return null;
            }

            var atIndex = packageId.IndexOf('@');
            return atIndex >= 0 ? packageId.Substring(0, atIndex) : packageId;
        }

        private static PackageSourceFilter ReadPackageSourceFilter(JToken args)
        {
            var sourceFilter = ReadString(args, "sourceFilter");
            if (string.IsNullOrWhiteSpace(sourceFilter))
            {
                return PackageSourceFilter.All;
            }

            if (Enum.TryParse<PackageSourceFilter>(sourceFilter, true, out var parsed))
            {
                return parsed;
            }

            throw new ArgumentException("sourceFilter must be one of All, Registry, Embedded, Local, Git, BuiltIn, or LocalTarball.", nameof(sourceFilter));
        }

        private static Dictionary<string, string> ReadManifestDependencies()
        {
            var manifestPath = Path.Combine(ChievfxMcpToolPolicy.ProjectRoot, "Packages", "manifest.json");
            if (!File.Exists(manifestPath))
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            var root = JToken.Parse(File.ReadAllText(manifestPath));
            if (root is not JObject rootObj
                || rootObj["dependencies"] is not JObject dependencies)
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var dependency in dependencies.Properties())
            {
                if (dependency.Value.Type == JTokenType.String)
                {
                    result[dependency.Name] = dependency.Value.Value<string>() ?? string.Empty;
                }
            }

            return result;
        }

        private static object[] GetManifestDependencyChanges(
            IReadOnlyDictionary<string, string>? before,
            IReadOnlyDictionary<string, string> after)
        {
            before ??= new Dictionary<string, string>(StringComparer.Ordinal);
            var changes = new List<object>();
            foreach (var dependency in after.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (!before.TryGetValue(dependency.Key, out var previousVersion))
                {
                    changes.Add(new { name = dependency.Key, change = "added", version = dependency.Value });
                    continue;
                }

                if (!string.Equals(previousVersion, dependency.Value, StringComparison.Ordinal))
                {
                    changes.Add(new { name = dependency.Key, change = "updated", previousVersion, version = dependency.Value });
                }
            }

            foreach (var dependency in before.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (!after.ContainsKey(dependency.Key))
                {
                    changes.Add(new { name = dependency.Key, change = "removed", previousVersion = dependency.Value });
                }
            }

            return changes.ToArray();
        }

    }
}
