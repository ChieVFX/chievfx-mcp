#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Chievfx.Mcp.Editor;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using PackageManagerPackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Chievfx.Mcp.Extensions.Cameras
{
    internal static partial class ChievfxMcpCamerasExtension
    {
        private readonly struct ShotSpec
        {
            public ShotSpec(string name, double start, double duration, float fieldOfView, float distance, int priority)
            {
                Name = name;
                Start = start;
                Duration = duration;
                FieldOfView = fieldOfView;
                Distance = distance;
                Priority = priority;
            }

            public string Name { get; }

            public double Start { get; }

            public double Duration { get; }

            public float FieldOfView { get; }

            public float Distance { get; }

            public int Priority { get; }
        }

        private readonly struct SequencerShotSpec
        {
            public SequencerShotSpec(string name, double hold, float fieldOfView, float distance, int priority, string blendStyle, float blendTime)
            {
                Name = name;
                Hold = hold;
                FieldOfView = fieldOfView;
                Distance = distance;
                Priority = priority;
                BlendStyle = blendStyle;
                BlendTime = blendTime;
            }

            public string Name { get; }

            public double Hold { get; }

            public float FieldOfView { get; }

            public float Distance { get; }

            public int Priority { get; }

            public string BlendStyle { get; }

            public float BlendTime { get; }
        }

        private readonly struct BlenderBlendSpec
        {
            public BlenderBlendSpec(string from, string to, string style, float time)
            {
                From = from;
                To = to;
                Style = style;
                Time = time;
            }

            public string From { get; }

            public string To { get; }

            public string Style { get; }

            public float Time { get; }
        }

        private readonly struct OptionalPackageStatus
        {
            public OptionalPackageStatus(string packageName, bool packageInstalled, string? packageVersion, Type? primaryType, string primaryTypeName, Type? secondaryType, string secondaryTypeName)
            {
                PackageName = packageName;
                PackageInstalled = packageInstalled;
                PackageVersion = packageVersion;
                PrimaryType = primaryType;
                PrimaryTypeName = primaryTypeName;
                SecondaryType = secondaryType;
                SecondaryTypeName = secondaryTypeName;
            }

            public string PackageName { get; }

            public bool PackageInstalled { get; }

            public string? PackageVersion { get; }

            public Type? PrimaryType { get; }

            public string PrimaryTypeName { get; }

            public Type? SecondaryType { get; }

            public string SecondaryTypeName { get; }

            public bool PrimaryTypeLoaded => PrimaryType != null;

            public bool SecondaryTypeLoaded => SecondaryType != null;

            public bool TypesLoaded => PrimaryTypeLoaded && SecondaryTypeLoaded;

            public bool Available => PackageInstalled && TypesLoaded;

            public string Reason
            {
                get
                {
                    if (Available)
                    {
                        return "available";
                    }

                    if (!PackageInstalled)
                    {
                        return $"{PackageName} package is not installed.";
                    }

                    return $"{PackageName} package detected, but {PrimaryTypeName} and/or {SecondaryTypeName} types are not loaded.";
                }
            }

            public Dictionary<string, object?> ToDictionary()
            {
                return new Dictionary<string, object?>
                {
                    ["available"] = Available,
                    ["packageName"] = PackageName,
                    ["packageVersion"] = PackageVersion,
                    ["packageInstalled"] = PackageInstalled,
                    ["typesLoaded"] = TypesLoaded,
                    ["primaryTypeName"] = PrimaryTypeName,
                    ["primaryTypeLoaded"] = PrimaryTypeLoaded,
                    ["secondaryTypeName"] = SecondaryTypeName,
                    ["secondaryTypeLoaded"] = SecondaryTypeLoaded,
                    ["reason"] = Reason,
                };
            }
        }

        private readonly struct AdvancedHelperStatus
        {
            public AdvancedHelperStatus(
                string key,
                string displayName,
                bool packageInstalled,
                string? packageVersion,
                string? apiFamily,
                bool versionDefineActive,
                Type? helperType,
                string helperTypeName,
                string? optionalPackageName,
                bool optionalPackageInstalled,
                string? optionalPackageVersion,
                Type? optionalType,
                string? optionalTypeName,
                Type? secondaryHelperType = null,
                string? secondaryHelperTypeName = null,
                Type? tertiaryHelperType = null,
                string? tertiaryHelperTypeName = null,
                bool optionalVersionDefineActive = true)
            {
                Key = key;
                DisplayName = displayName;
                PackageInstalled = packageInstalled;
                PackageVersion = packageVersion;
                ApiFamily = apiFamily;
                VersionDefineActive = versionDefineActive;
                HelperType = helperType;
                HelperTypeName = helperTypeName;
                OptionalPackageName = optionalPackageName;
                OptionalPackageInstalled = optionalPackageInstalled;
                OptionalPackageVersion = optionalPackageVersion;
                OptionalType = optionalType;
                OptionalTypeName = optionalTypeName;
                SecondaryHelperType = secondaryHelperType;
                SecondaryHelperTypeName = secondaryHelperTypeName;
                TertiaryHelperType = tertiaryHelperType;
                TertiaryHelperTypeName = tertiaryHelperTypeName;
                OptionalVersionDefineActive = optionalVersionDefineActive;
            }

            public string Key { get; }

            public string DisplayName { get; }

            public bool PackageInstalled { get; }

            public string? PackageVersion { get; }

            public string? ApiFamily { get; }

            public bool VersionDefineActive { get; }

            public Type? HelperType { get; }

            public string HelperTypeName { get; }

            public string? OptionalPackageName { get; }

            public bool OptionalPackageInstalled { get; }

            public string? OptionalPackageVersion { get; }

            public Type? OptionalType { get; }

            public string? OptionalTypeName { get; }

            public Type? SecondaryHelperType { get; }

            public string? SecondaryHelperTypeName { get; }

            public Type? TertiaryHelperType { get; }

            public string? TertiaryHelperTypeName { get; }

            public bool OptionalVersionDefineActive { get; }

            public bool HelperTypeLoaded => HelperType != null;

            public bool SecondaryHelperTypeLoaded => SecondaryHelperTypeName == null || SecondaryHelperType != null;

            public bool TertiaryHelperTypeLoaded => TertiaryHelperTypeName == null || TertiaryHelperType != null;

            public bool OptionalGateRequired => OptionalPackageName != null;

            public bool OptionalGateAvailable => !OptionalGateRequired || (OptionalPackageInstalled && OptionalType != null && OptionalVersionDefineActive);

            public bool TypesLoaded => HelperTypeLoaded && SecondaryHelperTypeLoaded && TertiaryHelperTypeLoaded && OptionalGateAvailable;

            public bool Available => PackageInstalled
                && VersionDefineActive
                && ApiFamily == CinemachineApiFamilyCm3
                && TypesLoaded;

            public string Reason
            {
                get
                {
                    if (Available)
                    {
                        return "available";
                    }

                    if (!PackageInstalled)
                    {
                        return $"{CinemachinePackageName} package is not installed.";
                    }

                    if (ApiFamily == CinemachineApiFamilyCm2)
                    {
                        return $"Cinemachine 2.x API detected; {DisplayName} requires Cinemachine 3.x. No package upgrade or package mutation was attempted.";
                    }

                    if (ApiFamily == CinemachineApiFamilyCm3LegacyObsolete)
                    {
                        return $"Cinemachine 3 legacy obsolete Unity.Cinemachine.CinemachineVirtualCamera API detected; {DisplayName} requires CM3 helper types. No package mutation was attempted.";
                    }

                    if (ApiFamily != CinemachineApiFamilyCm3)
                    {
                        return $"Cinemachine apiFamily is '{ApiFamily ?? "unknown"}', not cm3.";
                    }

                    if (!VersionDefineActive)
                    {
                        return "CHIEVFX_MCP_HAS_CINEMACHINE is not active yet; wait for Unity compile or refresh assembly definitions.";
                    }

                    if (!HelperTypeLoaded)
                    {
                        return $"CM3 package detected, but required type {HelperTypeName} is not loaded.";
                    }

                    if (!SecondaryHelperTypeLoaded)
                    {
                        return $"CM3 package detected, but required type {SecondaryHelperTypeName} is not loaded.";
                    }

                    if (!TertiaryHelperTypeLoaded)
                    {
                        return $"CM3 package detected, but required type {TertiaryHelperTypeName} is not loaded.";
                    }

                    if (OptionalGateRequired && !OptionalPackageInstalled)
                    {
                        return $"{OptionalPackageName} package is not installed.";
                    }

                    if (OptionalGateRequired && !OptionalVersionDefineActive)
                    {
                        return $"CHIEVFX_MCP_HAS_SPLINES is not active yet; wait for Unity compile or refresh assembly definitions.";
                    }

                    if (OptionalGateRequired && OptionalType == null)
                    {
                        return $"{OptionalPackageName} package detected, but required type {OptionalTypeName} is not loaded.";
                    }

                    return $"{DisplayName} unavailable.";
                }
            }

            public Dictionary<string, object?> ToDictionary()
            {
                var result = new Dictionary<string, object?>
                {
                    ["available"] = Available,
                    ["key"] = Key,
                    ["displayName"] = DisplayName,
                    ["packageName"] = CinemachinePackageName,
                    ["packageVersion"] = PackageVersion,
                    ["packageInstalled"] = PackageInstalled,
                    ["apiFamily"] = ApiFamily,
                    ["cinemachineApiFamily"] = ApiFamily,
                    ["versionDefineActive"] = VersionDefineActive,
                    ["typesLoaded"] = TypesLoaded,
                    ["helperTypeName"] = HelperTypeName,
                    ["helperTypeLoaded"] = HelperTypeLoaded,
                    ["secondaryHelperTypeName"] = SecondaryHelperTypeName,
                    ["secondaryHelperTypeLoaded"] = SecondaryHelperTypeLoaded,
                    ["tertiaryHelperTypeName"] = TertiaryHelperTypeName,
                    ["tertiaryHelperTypeLoaded"] = TertiaryHelperTypeLoaded,
                    ["reason"] = Reason,
                };

                if (OptionalGateRequired)
                {
                    result["optionalPackageName"] = OptionalPackageName;
                    result["optionalPackageVersion"] = OptionalPackageVersion;
                    result["optionalPackageInstalled"] = OptionalPackageInstalled;
                    result["optionalTypeName"] = OptionalTypeName;
                    result["optionalTypeLoaded"] = OptionalType != null;
                    result["optionalVersionDefineActive"] = OptionalVersionDefineActive;
                    result["optionalGateAvailable"] = OptionalGateAvailable;
                }

                return result;
            }
        }

        private readonly struct SequencerCameraStatus
        {
            public SequencerCameraStatus(
                bool packageInstalled,
                string? packageVersion,
                string? apiFamily,
                bool versionDefineActive,
                Type? sequencerCameraType,
                Type? instructionType,
                Type? cameraType,
                Type? virtualCameraBaseType,
                Type? blendDefinitionType,
                Type? brainType)
            {
                PackageInstalled = packageInstalled;
                PackageVersion = packageVersion;
                ApiFamily = apiFamily;
                VersionDefineActive = versionDefineActive;
                SequencerCameraType = sequencerCameraType;
                InstructionType = instructionType;
                CameraType = cameraType;
                VirtualCameraBaseType = virtualCameraBaseType;
                BlendDefinitionType = blendDefinitionType;
                BrainType = brainType;
            }

            public bool PackageInstalled { get; }

            public string? PackageVersion { get; }

            public string? ApiFamily { get; }

            public bool VersionDefineActive { get; }

            public Type? SequencerCameraType { get; }

            public Type? InstructionType { get; }

            public Type? CameraType { get; }

            public Type? VirtualCameraBaseType { get; }

            public Type? BlendDefinitionType { get; }

            public Type? BrainType { get; }

            public bool SequencerCameraTypeLoaded => SequencerCameraType != null;

            public bool TypesLoaded => SequencerCameraType != null
                && InstructionType != null
                && CameraType != null
                && VirtualCameraBaseType != null
                && BlendDefinitionType != null
                && BrainType != null;

            public bool Available => PackageInstalled
                && VersionDefineActive
                && ApiFamily == CinemachineApiFamilyCm3
                && TypesLoaded;

            public string Reason
            {
                get
                {
                    if (Available)
                    {
                        return "available";
                    }

                    if (!PackageInstalled)
                    {
                        return $"{CinemachinePackageName} package is not installed.";
                    }

                    if (ApiFamily == CinemachineApiFamilyCm2)
                    {
                        return "Cinemachine 2.x API detected; Sequencer Camera requires Cinemachine 3.x. No package upgrade or package mutation was attempted.";
                    }

                    if (ApiFamily == CinemachineApiFamilyCm3LegacyObsolete)
                    {
                        return "Cinemachine 3 legacy obsolete Unity.Cinemachine.CinemachineVirtualCamera API detected; Sequencer Camera requires CM3 CinemachineSequencerCamera and CinemachineCamera. No package mutation was attempted.";
                    }

                    if (ApiFamily != CinemachineApiFamilyCm3)
                    {
                        return $"Cinemachine apiFamily is '{ApiFamily ?? "unknown"}', not cm3.";
                    }

                    if (!VersionDefineActive)
                    {
                        return "CHIEVFX_MCP_HAS_CINEMACHINE is not active yet; wait for Unity compile or refresh assembly definitions.";
                    }

                    if (!TypesLoaded)
                    {
                        return "CM3 package detected, but one or more Sequencer Camera types are not loaded.";
                    }

                    return "Sequencer Camera unavailable.";
                }
            }

            public Dictionary<string, object?> ToDictionary()
            {
                return new Dictionary<string, object?>
                {
                    ["available"] = Available,
                    ["packageName"] = CinemachinePackageName,
                    ["packageVersion"] = PackageVersion,
                    ["packageInstalled"] = PackageInstalled,
                    ["apiFamily"] = ApiFamily,
                    ["cinemachineApiFamily"] = ApiFamily,
                    ["versionDefineActive"] = VersionDefineActive,
                    ["typesLoaded"] = TypesLoaded,
                    ["sequencerCameraAvailable"] = Available,
                    ["sequencerCameraTypeLoaded"] = SequencerCameraTypeLoaded,
                    ["instructionTypeLoaded"] = InstructionType != null,
                    ["cinemachineCameraTypeLoaded"] = CameraType != null,
                    ["virtualCameraBaseTypeLoaded"] = VirtualCameraBaseType != null,
                    ["blendDefinitionTypeLoaded"] = BlendDefinitionType != null,
                    ["brainTypeLoaded"] = BrainType != null,
                    ["reason"] = Reason,
                };
            }
        }

        private readonly struct PackageStatus
        {
            public PackageStatus(string packageName, bool packageInstalled, string? packageVersion, string? apiFamily, bool typesLoaded, bool versionDefineActive, Type? cameraOrAssetType, Type? brainOrTrackType, Type? trackOrClipType, Type? shotOrDirectorType)
            {
                PackageName = packageName;
                PackageInstalled = packageInstalled;
                PackageVersion = packageVersion;
                ApiFamily = apiFamily;
                TypesLoaded = typesLoaded;
                VersionDefineActive = versionDefineActive;
                if (packageName == CinemachinePackageName)
                {
                    CameraType = cameraOrAssetType;
                    BrainType = brainOrTrackType;
                    TrackType = trackOrClipType;
                    ShotType = shotOrDirectorType;
                    AssetType = null;
                    TimelineTrackType = null;
                    ClipType = null;
                    DirectorType = null;
                }
                else
                {
                    AssetType = cameraOrAssetType;
                    TimelineTrackType = brainOrTrackType;
                    ClipType = trackOrClipType;
                    DirectorType = shotOrDirectorType;
                    CameraType = null;
                    BrainType = null;
                    TrackType = null;
                    ShotType = null;
                }
            }

            public string PackageName { get; }

            public bool PackageInstalled { get; }

            public string? PackageVersion { get; }

            public string? ApiFamily { get; }

            public bool TypesLoaded { get; }

            public bool VersionDefineActive { get; }

            public bool Available => PackageInstalled && TypesLoaded && VersionDefineActive;

            public Type? CameraType { get; }

            public Type? BrainType { get; }

            public Type? TrackType { get; }

            public Type? ShotType { get; }

            public Type? AssetType { get; }

            public Type? TimelineTrackType { get; }

            public Type? ClipType { get; }

            public Type? DirectorType { get; }

            public string Reason
            {
                get
                {
                    if (Available)
                    {
                        return "available";
                    }

                    if (PackageName == CinemachinePackageName)
                    {
                        if (ApiFamily == CinemachineApiFamilyCm2)
                        {
                            return "Cinemachine 2.x API detected; chievfx.cameras authoring tools are deferred until Cinemachine 3.x Unity.Cinemachine.CinemachineCamera is available. No package upgrade or package mutation was attempted.";
                        }

                        if (ApiFamily == CinemachineApiFamilyCm3LegacyObsolete)
                        {
                            return "Cinemachine 3 legacy obsolete Unity.Cinemachine.CinemachineVirtualCamera API detected; chievfx.cameras authoring tools currently require Unity.Cinemachine.CinemachineCamera. No package mutation was attempted.";
                        }
                    }

                    if (!PackageInstalled)
                    {
                        return $"{PackageName} package is not installed.";
                    }

                    if (PackageName == CinemachinePackageName && ApiFamily == CinemachineApiFamilyCm3 && !TypesLoaded)
                    {
                        return "Cinemachine 3.x package detected, but Unity.Cinemachine.CinemachineCamera and Unity.Cinemachine.CinemachineBrain types are not loaded.";
                    }

                    if (!TypesLoaded)
                    {
                        return $"{PackageName} runtime/editor types are not loaded.";
                    }

                    return "asmdef versionDefine is not active yet; wait for Unity compile or refresh assembly definitions.";
                }
            }

            public Dictionary<string, object?> ToDictionary()
            {
                var result = new Dictionary<string, object?>
                {
                    ["available"] = Available,
                    ["packageName"] = PackageName,
                    ["packageVersion"] = PackageVersion,
                    ["packageInstalled"] = PackageInstalled,
                    ["typesLoaded"] = TypesLoaded,
                    ["versionDefineActive"] = VersionDefineActive,
                    ["reason"] = Reason,
                };

                if (PackageName == CinemachinePackageName)
                {
                    result["apiFamily"] = ApiFamily;
                    result["cinemachineApiFamily"] = ApiFamily;
                }

                return result;
            }
        }

        private readonly struct DependencyStatus
        {
            public DependencyStatus(PackageStatus cinemachine, SequencerCameraStatus sequencer, AdvancedHelperStatus splinesDolly, AdvancedHelperStatus inputAxisController, OptionalPackageStatus inputSystem, AdvancedHelperStatus blenderSettings, AdvancedHelperStatus impulse, Type? collisionImpulseSourceType, Type? externalImpulseListenerType, AdvancedHelperStatus confiner2D, AdvancedHelperStatus confiner3D, Type? obsoleteConfinerType, PackageStatus timeline)
            {
                Cinemachine = cinemachine;
                Sequencer = sequencer;
                SplinesDolly = splinesDolly;
                InputAxisController = inputAxisController;
                InputSystem = inputSystem;
                BlenderSettings = blenderSettings;
                Impulse = impulse;
                CollisionImpulseSourceType = collisionImpulseSourceType;
                ExternalImpulseListenerType = externalImpulseListenerType;
                Confiner2D = confiner2D;
                Confiner3D = confiner3D;
                ObsoleteConfinerType = obsoleteConfinerType;
                Timeline = timeline;
            }

            public PackageStatus Cinemachine { get; }

            public SequencerCameraStatus Sequencer { get; }

            public AdvancedHelperStatus SplinesDolly { get; }

            public AdvancedHelperStatus InputAxisController { get; }

            public OptionalPackageStatus InputSystem { get; }

            public AdvancedHelperStatus BlenderSettings { get; }

            public AdvancedHelperStatus Impulse { get; }

            public Type? CollisionImpulseSourceType { get; }

            public Type? ExternalImpulseListenerType { get; }

            public AdvancedHelperStatus Confiner2D { get; }

            public AdvancedHelperStatus Confiner3D { get; }

            public Type? ObsoleteConfinerType { get; }

            public PackageStatus Timeline { get; }

            public bool AnyAvailable => Cinemachine.Available || Sequencer.Available || SplinesDolly.Available || InputAxisController.Available || BlenderSettings.Available || Impulse.Available || Confiner2D.Available || Confiner3D.Available || Timeline.Available;

            public string Reason => AnyAvailable ? "one-or-more-dependencies-available" : "Cinemachine and Timeline extension dependencies are unavailable.";

            public Dictionary<string, object?> ToDictionary()
            {
                return new Dictionary<string, object?>
                {
                    ["available"] = AnyAvailable,
                    ["cinemachine"] = Cinemachine.ToDictionary(),
                    ["sequencerCamera"] = Sequencer.ToDictionary(),
                    ["splinesDolly"] = SplinesDolly.ToDictionary(),
                    ["inputAxisController"] = InputAxisController.ToDictionary(),
                    ["inputSystem"] = InputSystem.ToDictionary(),
                    ["blenderSettings"] = BlenderSettings.ToDictionary(),
                    ["impulse"] = Impulse.ToDictionary(),
                    ["collisionImpulseSourceTypeLoaded"] = CollisionImpulseSourceType != null,
                    ["externalImpulseListenerTypeLoaded"] = ExternalImpulseListenerType != null,
                    ["confiner2D"] = Confiner2D.ToDictionary(),
                    ["confiner3D"] = Confiner3D.ToDictionary(),
                    ["obsoleteConfinerTypeLoaded"] = ObsoleteConfinerType != null,
                    ["obsoleteConfinerTypeName"] = "Unity.Cinemachine.CinemachineConfiner",
                    ["timeline"] = Timeline.ToDictionary(),
                };
            }
        }
    }
}
