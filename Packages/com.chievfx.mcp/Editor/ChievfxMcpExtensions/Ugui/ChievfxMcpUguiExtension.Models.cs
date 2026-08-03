#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Chievfx.Mcp.Editor;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using static Chievfx.Mcp.Extensions.Ugui.ChievfxMcpUguiExtension;
using static Chievfx.Mcp.Extensions.Ugui.UguiDesignTools;
using static Chievfx.Mcp.Extensions.Ugui.UguiElementHelpers;
using static Chievfx.Mcp.Extensions.Ugui.UguiLayoutHelpers;
using static Chievfx.Mcp.Extensions.Ugui.UguiResourcesAndRows;
using static Chievfx.Mcp.Extensions.Ugui.UguiRuntimeControlHelpers;
using static Chievfx.Mcp.Extensions.Ugui.UguiRuntimeHelpers;
using static Chievfx.Mcp.Extensions.Ugui.UguiRuntimeTools;
using static Chievfx.Mcp.Extensions.Ugui.UguiSchemas;
using static Chievfx.Mcp.Extensions.Ugui.UguiSharedHelpers;

namespace Chievfx.Mcp.Extensions.Ugui
{
    internal readonly struct RuntimeScreenPosition
    {
        public RuntimeScreenPosition(Vector2 screenPosition, Vector2 screenSize, Vector2 normalizedPosition, bool normalizedInputSupplied)
        {
            ScreenPosition = screenPosition;
            ScreenSize = screenSize;
            NormalizedPosition = normalizedPosition;
            NormalizedInputSupplied = normalizedInputSupplied;
        }

        public Vector2 ScreenPosition { get; }
        public Vector2 ScreenSize { get; }
        public Vector2 NormalizedPosition { get; }
        public bool NormalizedInputSupplied { get; }

        public static RuntimeScreenPosition FromScreenPosition(Vector2 screenPosition)
        {
            var screenSize = ChievfxMcpRuntimeScreenSize.Resolve();
            return new RuntimeScreenPosition(
                screenPosition,
                screenSize,
                new Vector2(screenPosition.x / screenSize.x, screenPosition.y / screenSize.y),
                normalizedInputSupplied: false);
        }
    }

    internal readonly struct RectArgs
    {
        public RectArgs(
            string preset,
            string? dock,
            Vector2 size,
            Vector2 position,
            float margin,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 pivot,
            Vector2? anchoredPosition,
            Vector2? sizeDelta,
            Vector2? offsetMin,
            Vector2? offsetMax,
            bool usesRawAnchors)
        {
            Preset = preset;
            Dock = dock;
            Size = size;
            Position = position;
            Margin = margin;
            AnchorMin = anchorMin;
            AnchorMax = anchorMax;
            Pivot = pivot;
            AnchoredPosition = anchoredPosition;
            SizeDelta = sizeDelta;
            OffsetMin = offsetMin;
            OffsetMax = offsetMax;
            UsesRawAnchors = usesRawAnchors;
        }

        public string Preset { get; }
        public string? Dock { get; }
        public Vector2 Size { get; }
        public Vector2 Position { get; }
        public float Margin { get; }
        public Vector2 AnchorMin { get; }
        public Vector2 AnchorMax { get; }
        public Vector2 Pivot { get; }
        public Vector2? AnchoredPosition { get; }
        public Vector2? SizeDelta { get; }
        public Vector2? OffsetMin { get; }
        public Vector2? OffsetMax { get; }
        public bool UsesRawAnchors { get; }

        public RectArgs WithPreset(string preset)
        {
            return new RectArgs(preset, Dock, Size, Position, Margin, AnchorMin, AnchorMax, Pivot, AnchoredPosition, SizeDelta, OffsetMin, OffsetMax, UsesRawAnchors);
        }
    }

    internal sealed class UguiDependencyStatus
    {
        public UguiDependencyStatus(
            bool available,
            string reason,
            string packageName,
            string packageVersion,
            string packageSource,
            bool defaultControlsLoaded,
            bool versionDefineActive,
            Type? defaultControlsType,
            Type? canvasType,
            Type? canvasScalerType,
            Type? graphicRaycasterType,
            Type? eventSystemType,
            Type? baseInputModuleType,
            Type? standaloneInputModuleType,
            Type? inputSystemUiInputModuleType,
            Type? imageType,
            Type? buttonType,
            Type? sliderType,
            Type? toggleType,
            Type? scrollbarType,
            Type? scrollRectType,
            Type? dropdownType,
            Type? tmpDropdownType,
            Type? inputFieldType,
            Type? selectableType,
            Type? graphicType,
            Type? pointerEventDataType,
            Type? raycastResultType,
            Type? pointerClickHandlerType,
            string tmpPackageVersion,
            string tmpPackageSource,
            Type? tmpTextType)
        {
            Available = available;
            Reason = reason;
            PackageName = packageName;
            PackageVersion = packageVersion;
            PackageSource = packageSource;
            DefaultControlsLoaded = defaultControlsLoaded;
            VersionDefineActive = versionDefineActive;
            DefaultControlsType = defaultControlsType;
            CanvasType = canvasType;
            CanvasScalerType = canvasScalerType;
            GraphicRaycasterType = graphicRaycasterType;
            EventSystemType = eventSystemType;
            BaseInputModuleType = baseInputModuleType;
            StandaloneInputModuleType = standaloneInputModuleType;
            InputSystemUiInputModuleType = inputSystemUiInputModuleType;
            ImageType = imageType;
            ButtonType = buttonType;
            SliderType = sliderType;
            ToggleType = toggleType;
            ScrollbarType = scrollbarType;
            ScrollRectType = scrollRectType;
            DropdownType = dropdownType;
            TmpDropdownType = tmpDropdownType;
            InputFieldType = inputFieldType;
            SelectableType = selectableType;
            GraphicType = graphicType;
            PointerEventDataType = pointerEventDataType;
            RaycastResultType = raycastResultType;
            PointerClickHandlerType = pointerClickHandlerType;
            TmpPackageVersion = tmpPackageVersion;
            TmpPackageSource = tmpPackageSource;
            TmpTextType = tmpTextType;
        }

        public bool Available { get; }
        public string Reason { get; }
        public string PackageName { get; }
        public string PackageVersion { get; }
        public string PackageSource { get; }
        public bool DefaultControlsLoaded { get; }
        public bool VersionDefineActive { get; }
        public Type? DefaultControlsType { get; }
        public Type? CanvasType { get; }
        public Type? CanvasScalerType { get; }
        public Type? GraphicRaycasterType { get; }
        public Type? EventSystemType { get; }
        public Type? BaseInputModuleType { get; }
        public Type? StandaloneInputModuleType { get; }
        public Type? InputSystemUiInputModuleType { get; }
        public Type? ImageType { get; }
        public Type? ButtonType { get; }
        public Type? SliderType { get; }
        public Type? ToggleType { get; }
        public Type? ScrollbarType { get; }
        public Type? ScrollRectType { get; }
        public Type? DropdownType { get; }
        public Type? TmpDropdownType { get; }
        public Type? InputFieldType { get; }
        public Type? SelectableType { get; }
        public Type? GraphicType { get; }
        public Type? PointerEventDataType { get; }
        public Type? RaycastResultType { get; }
        public Type? PointerClickHandlerType { get; }
        public string TmpPackageVersion { get; }
        public string TmpPackageSource { get; }
        public Type? TmpTextType { get; }
        public bool TmpConfigured => TmpTextType != null;

        public Dictionary<string, object?> ToDictionary()
        {
            return new Dictionary<string, object?>
            {
                ["packageName"] = PackageName,
                ["packageVersion"] = PackageVersion,
                ["packageSource"] = PackageSource,
                ["canvasLoaded"] = CanvasType != null,
                ["defaultControlsLoaded"] = DefaultControlsLoaded,
                ["versionDefineActive"] = VersionDefineActive,
                ["baseInputModuleLoaded"] = BaseInputModuleType != null,
                ["inputSystemUiInputModuleLoaded"] = InputSystemUiInputModuleType != null,
                ["prefersInputSystemUiInputModule"] = ShouldPreferInputSystemUiModule(),
                ["runtimeControlTypesLoaded"] = new[]
                {
                    ButtonType,
                    ToggleType,
                    SliderType,
                    ScrollbarType,
                    ScrollRectType,
                    DropdownType,
                    TmpDropdownType,
                    InputFieldType,
                }.Where(type => type != null).Select(type => type!.FullName).ToArray(),
                ["textMeshProPackageVersion"] = TmpPackageVersion,
                ["textMeshProPackageSource"] = TmpPackageSource,
                ["textMeshProUGUILoaded"] = TmpTextType != null,
                ["textMeshProConfigured"] = TmpConfigured,
                ["available"] = Available,
                ["reason"] = Reason,
            };
        }
    }

    internal sealed class UguiRuntimeUiAdapter : IChievfxMcpRuntimeUiAdapter, IChievfxMcpRuntimeUiTextInputAdapter, IChievfxMcpRuntimeUiControlFindAdapter, IChievfxMcpRuntimeUiClickAdapter, IChievfxMcpRuntimeUiDragAdapter, IChievfxMcpRuntimeUiSetControlValueAdapter, IChievfxMcpRuntimeUiFocusAdapter
    {
        public string FrameworkId => "ugui";

        public string FrameworkName => "uGUI";

        public int Priority => 100;

        public bool Available => GetDependencyStatus().Available;

        public object? Status => GetDependencyStatus().ToDictionary();

        public IEnumerable<string> Resources => new[] { RuntimeStatusUri, RuntimeCanvasesUri, RuntimeVisibleTreeUri };

        public object? ProbeScreenPosition(JToken request)
        {
            return ProbeRuntimeScreenPosition(request, GetDependencyStatus());
        }

        public object? TypeIntoFocusedTextField(JToken request, bool requireTarget)
        {
            return TypeTextIntoFocusedTextField(request, GetDependencyStatus(), requireTarget);
        }

        public object? FindControls(JToken request)
        {
            return ControlFind(request, GetDependencyStatus());
        }

        public object? ClickAtPosition(JToken request)
        {
            return RuntimeClickAtPosition(request, GetDependencyStatus());
        }

        public object? DragAtPosition(JToken request)
        {
            return RuntimeDragAtPosition(request, GetDependencyStatus());
        }

        public object? SetControlValue(JToken request, bool requireTarget)
        {
            return RuntimeSetControlValueAt(request, GetDependencyStatus(), requireTarget);
        }

        public object? Focus(JToken request, bool requireTarget)
        {
            return RuntimeFocusAt(request, GetDependencyStatus(), requireTarget);
        }

        public object? ClearFocus(JToken request)
        {
            return RuntimeClearFocus(GetDependencyStatus());
        }
    }
}
