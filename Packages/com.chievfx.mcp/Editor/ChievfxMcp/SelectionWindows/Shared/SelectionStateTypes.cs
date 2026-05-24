#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
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
    internal enum OptionalState
    {
        RequiredOnly,
        Off,
        Mixed,
        On
    }

    internal enum StatusChipState
    {
        Neutral,
        Good,
        Warning
    }

    internal readonly struct CategoryRows<T>
    {
        public CategoryRows(string category, IReadOnlyList<T> rows)
        {
            Category = category;
            Rows = rows;
        }

        public string Category { get; }

        public IReadOnlyList<T> Rows { get; }
    }
}
