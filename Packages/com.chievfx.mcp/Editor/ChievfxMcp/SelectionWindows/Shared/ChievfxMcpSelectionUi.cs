#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using static Chievfx.Mcp.Editor.ChievfxMcpSelectionUi;

namespace Chievfx.Mcp.Editor
{
    internal static class ChievfxMcpSelectionUi
    {
        public static ScrollView CreateRootScroll(VisualElement root)
        {
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            scroll.style.paddingLeft = 12;
            scroll.style.paddingRight = 12;
            scroll.style.paddingTop = 12;
            scroll.style.paddingBottom = 12;
            root.Add(scroll);
            return scroll;
        }

        public static Button CreateButton(string text, Action clicked)
        {
            var button = new Button(clicked)
            {
                text = text
            };
            button.style.minWidth = 96;
            button.style.marginRight = 4;
            button.style.marginBottom = 4;
            return button;
        }

        public static Button CreateCopyNameButton(string value, string kind)
        {
            var button = new Button(() => GUIUtility.systemCopyBuffer = value)
            {
                text = "⧉",
                tooltip = $"Copy {kind} name: {value}"
            };
            button.style.width = 24;
            button.style.height = 20;
            button.style.minWidth = 24;
            button.style.marginLeft = 6;
            button.style.marginRight = 0;
            button.style.marginBottom = 2;
            button.style.paddingLeft = 0;
            button.style.paddingRight = 0;
            button.style.paddingTop = 0;
            button.style.paddingBottom = 0;
            button.style.unityTextAlign = TextAnchor.MiddleCenter;
            button.RegisterCallback<ClickEvent>(evt => evt.StopPropagation());
            return button;
        }

        public static VisualElement CreateTitleRow(string title, bool allInfo, Action<bool> setAllInfo)
        {
            var row = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems = Align.Center,
                    marginBottom = 2
                }
            };

            var label = new Label(title);
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.fontSize = 18;
            label.style.flexGrow = 1;
            row.Add(label);
            row.Add(CreateAllInfoButton(allInfo, setAllInfo));
            return row;
        }

        public static Button CreateAllInfoButton(bool allInfo, Action<bool> setAllInfo)
        {
            Button? button = null;
            button = new Button(() =>
            {
                setAllInfo(!allInfo);
            })
            {
                text = "i",
                tooltip = allInfo ? "All info on" : "All info off"
            };
            button.style.width = 24;
            button.style.height = 20;
            button.style.minWidth = 24;
            button.style.marginLeft = 6;
            button.style.marginRight = 0;
            button.style.marginBottom = 2;
            button.style.paddingLeft = 0;
            button.style.paddingRight = 0;
            button.style.paddingTop = 0;
            button.style.paddingBottom = 0;
            button.style.unityFontStyleAndWeight = FontStyle.Bold;
            button.style.unityTextAlign = TextAnchor.MiddleCenter;
            button.style.backgroundColor = allInfo
                ? new StyleColor(new Color(0.17f, 0.24f, 0.31f))
                : new StyleColor(new Color(0.16f, 0.16f, 0.16f));
            button.style.color = allInfo
                ? new StyleColor(new Color(0.78f, 0.90f, 1f))
                : new StyleColor(new Color(0.70f, 0.70f, 0.70f));
            return button;
        }

        public static VisualElement CreateSectionCard(string title)
        {
            var card = new VisualElement
            {
                style =
                {
                    borderTopWidth = 1,
                    borderBottomWidth = 1,
                    borderLeftWidth = 1,
                    borderRightWidth = 1,
                    borderTopColor = new Color(0.25f, 0.25f, 0.25f),
                    borderBottomColor = new Color(0.25f, 0.25f, 0.25f),
                    borderLeftColor = new Color(0.25f, 0.25f, 0.25f),
                    borderRightColor = new Color(0.25f, 0.25f, 0.25f),
                    paddingLeft = 8,
                    paddingRight = 8,
                    paddingTop = 6,
                    paddingBottom = 6,
                    marginTop = 8,
                    marginBottom = 8,
                    backgroundColor = new StyleColor(new Color(0.12f, 0.12f, 0.12f))
                }
            };

            var header = new Label(title);
            header.style.unityFontStyleAndWeight = FontStyle.Bold;
            header.style.marginBottom = 4;
            card.Add(header);
            return card;
        }

        public static Label CreateMutedLabel(string text)
        {
            var label = new Label(text);
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.color = new StyleColor(new Color(0.68f, 0.68f, 0.68f));
            label.style.marginTop = 2;
            label.style.marginBottom = 4;
            return label;
        }

        public static TextField CreateQuickFilterField(string value, Action<string> changed)
        {
            var field = new TextField("Quick filter")
            {
                value = value,
                tooltip = "Space-separated wildcard terms are ANDed. Prefix with c: to match categories."
            };
            field.style.marginTop = 2;
            field.style.marginBottom = 8;
            field.RegisterValueChangedCallback(evt => changed(evt.newValue ?? string.Empty));
            return field;
        }

        public static IEnumerable<CategoryRows<T>> ApplyQuickFilter<T>(
            IEnumerable<CategoryRows<T>> groups,
            string query,
            Func<T, string> rowSearchText)
        {
            var filter = QuickFilter.Parse(query);
            foreach (var group in groups)
            {
                if (filter.IsEmpty)
                {
                    yield return group;
                    continue;
                }

                if (filter.MatchesCategory(group.Category))
                {
                    if (!filter.HasRowTerms)
                    {
                        yield return group;
                        continue;
                    }
                }
                else if (filter.HasCategoryTerms)
                {
                    continue;
                }

                var rows = group.Rows.Where(row => filter.MatchesRow(rowSearchText(row))).ToList();
                if (rows.Count > 0)
                {
                    yield return new CategoryRows<T>(group.Category, rows);
                }
            }
        }

        public static Label CreateStateChip(string text, StatusChipState state)
        {
            var chip = new Label(text);
            chip.style.marginRight = 4;
            chip.style.marginBottom = 4;
            chip.style.paddingLeft = 8;
            chip.style.paddingRight = 8;
            chip.style.paddingTop = 2;
            chip.style.paddingBottom = 2;
            chip.style.unityFontStyleAndWeight = FontStyle.Bold;
            ApplyStateChipStyle(chip, state);
            return chip;
        }

        public static void ApplyStateChipStyle(Label chip, StatusChipState state)
        {
            switch (state)
            {
                case StatusChipState.Good:
                    chip.style.backgroundColor = new StyleColor(new Color(0.18f, 0.34f, 0.22f));
                    chip.style.color = new StyleColor(new Color(0.72f, 1f, 0.78f));
                    break;
                case StatusChipState.Warning:
                    chip.style.backgroundColor = new StyleColor(new Color(0.34f, 0.28f, 0.16f));
                    chip.style.color = new StyleColor(new Color(1f, 0.88f, 0.58f));
                    break;
                default:
                    chip.style.backgroundColor = new StyleColor(new Color(0.20f, 0.20f, 0.20f));
                    chip.style.color = new StyleColor(new Color(0.78f, 0.78f, 0.78f));
                    break;
            }
        }

        public static void ApplyCategoryStateStyle(Button button, OptionalState state)
        {
            switch (state)
            {
                case OptionalState.RequiredOnly:
                    button.text = "Locked";
                    button.style.backgroundColor = new StyleColor(new Color(0.18f, 0.24f, 0.30f));
                    button.style.color = new StyleColor(new Color(0.68f, 0.86f, 1f));
                    break;
                case OptionalState.On:
                    button.text = "On";
                    button.style.backgroundColor = new StyleColor(new Color(0.18f, 0.34f, 0.22f));
                    button.style.color = new StyleColor(new Color(0.72f, 1f, 0.78f));
                    break;
                case OptionalState.Mixed:
                    button.text = "Mixed";
                    button.style.backgroundColor = new StyleColor(new Color(0.34f, 0.34f, 0.34f));
                    button.style.color = new StyleColor(new Color(0.94f, 0.94f, 0.94f));
                    break;
                default:
                    button.text = "Off";
                    button.style.backgroundColor = new StyleColor(new Color(0.20f, 0.20f, 0.20f));
                    button.style.color = new StyleColor(new Color(0.78f, 0.78f, 0.78f));
                    break;
            }
        }

        public static VisualElement CreateMetaRow()
        {
            return new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    flexWrap = Wrap.Wrap,
                    marginLeft = 28,
                    marginTop = 2,
                    marginBottom = 2,
                    color = new StyleColor(new Color(0.72f, 0.72f, 0.72f))
                }
            };
        }

        public static VisualElement CreateEmptyState(string title, string guidance, Action retry)
        {
            var container = new VisualElement();
            container.Add(new HelpBox($"{title}\n\n{guidance}", HelpBoxMessageType.Warning));
            var actions = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    flexWrap = Wrap.Wrap,
                    marginTop = 4
                }
            };
            actions.Add(CreateButton("Reload Metadata", retry));
            actions.Add(CreateButton("Open MCP Config", ChievfxMcpWindow.Open));
            container.Add(actions);
            return container;
        }

        public static VisualElement CreateLoadErrorState(string title, string error, string savedSelectionMessage, Action retry)
        {
            var container = new VisualElement();
            container.Add(new HelpBox(
                $"{title}\n\nWhat failed: {error}\n\n{savedSelectionMessage}\n\nRetry with Reload Metadata. If this keeps failing, open MCP config and confirm the server script path.",
                HelpBoxMessageType.Error));
            var actions = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    flexWrap = Wrap.Wrap,
                    marginTop = 4
                }
            };
            actions.Add(CreateButton("Reload Metadata", retry));
            actions.Add(CreateButton("Open MCP Config", ChievfxMcpWindow.Open));
            container.Add(actions);
            return container;
        }

        public static Foldout CreatePreviewFoldout(string title, string preview, int minHeight = 96, string tooltip = "")
        {
            var foldout = new Foldout
            {
                text = title,
                value = false,
                tooltip = tooltip
            };
            foldout.style.marginLeft = 28;

            var previewText = new TextField
            {
                value = preview,
                multiline = true,
                isReadOnly = true
            };
            previewText.style.minHeight = minHeight;
            previewText.style.whiteSpace = WhiteSpace.Normal;
            foldout.Add(previewText);
            return foldout;
        }

        public static string QuoteArg(string value)
        {
            return $"\"{value.Replace("\"", "\\\"")}\"";
        }

        public static string FormatJson(JToken element)
        {
            return element.ToString(Formatting.Indented);
        }

        public static string ReadString(JToken element, string propertyName, string defaultValue = "")
        {
            if (element[propertyName] is JToken value && value.Type == JTokenType.String)
            {
                return value.Value<string>() ?? defaultValue;
            }

            return defaultValue;
        }

        public static int ReadInt(JToken element, string propertyName, int defaultValue = 0)
        {
            if (element[propertyName] is JToken value
                && (value.Type == JTokenType.Integer || value.Type == JTokenType.Float))
            {
                try
                {
                    return value.Value<int>();
                }
                catch (Exception)
                {
                    return defaultValue;
                }
            }

            return defaultValue;
        }

        public static string ReadResponseEstimateLabel(JToken element)
        {
            if (element["responseEstimate"] is JObject responseEstimate)
            {
                var label = ReadString(responseEstimate, "label");
                if (!string.IsNullOrWhiteSpace(label))
                {
                    return label;
                }
            }

            return "rough wrapped-result size depends on output";
        }

        public static string ShortHash(string hash)
        {
            if (hash.Length <= 12)
            {
                return hash;
            }

            return hash.Substring(0, 12);
        }

        private sealed class QuickFilter
        {
            private readonly List<Regex> rowTerms = new();
            private readonly List<Regex> categoryTerms = new();

            public bool IsEmpty => rowTerms.Count == 0 && categoryTerms.Count == 0;

            public bool HasCategoryTerms => categoryTerms.Count > 0;

            public bool HasRowTerms => rowTerms.Count > 0;

            public static QuickFilter Parse(string query)
            {
                var filter = new QuickFilter();
                foreach (var rawTerm in query.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var isCategory = rawTerm.StartsWith("c:", StringComparison.OrdinalIgnoreCase);
                    var term = isCategory ? rawTerm.Substring(2) : rawTerm;
                    if (string.IsNullOrWhiteSpace(term))
                    {
                        continue;
                    }

                    (isCategory ? filter.categoryTerms : filter.rowTerms).Add(BuildWildcardRegex(term));
                }

                return filter;
            }

            public bool MatchesCategory(string category)
            {
                return categoryTerms.Count > 0 && categoryTerms.All(term => term.IsMatch(category));
            }

            public bool MatchesRow(string searchText)
            {
                return rowTerms.Count == 0 || rowTerms.All(term => term.IsMatch(searchText));
            }

            private static Regex BuildWildcardRegex(string term)
            {
                var pattern = Regex.Escape(term)
                    .Replace("\\*", ".*")
                    .Replace("\\?", ".");
                if (!term.Contains("*", StringComparison.Ordinal) && !term.Contains("?", StringComparison.Ordinal))
                {
                    pattern = $".*{pattern}.*";
                }

                return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
        }
    }
}
