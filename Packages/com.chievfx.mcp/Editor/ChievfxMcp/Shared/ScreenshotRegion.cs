#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Chievfx.Mcp.Editor
{
    // A sub-rectangle of the Game View expressed the way a caller reads it off a screenshot PNG:
    // normalized 0..1 with a TOP-LEFT origin (left/top = 0,0; right/bottom = 1,1). screenshot-game-view
    // crops to it, and ui-runtime-click/drag/probe accept the same four values so a 0..1 position read
    // off a cropped PNG can be mapped back onto the full view. Keeping one parser for both sides is the
    // point: the crop the screenshot applied and the crop the click undoes must agree to the bit.
    internal readonly struct ScreenshotRegion : IEquatable<ScreenshotRegion>
    {
        public const string LeftKey = "left";
        public const string TopKey = "top";
        public const string RightKey = "right";
        public const string BottomKey = "bottom";

        public static readonly string[] ArgumentKeys = { LeftKey, TopKey, RightKey, BottomKey };

        public static readonly ScreenshotRegion Full = new(0f, 0f, 1f, 1f);

        public ScreenshotRegion(float left, float top, float right, float bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

        public float Left { get; }

        public float Top { get; }

        public float Right { get; }

        public float Bottom { get; }

        public float Width => Right - Left;

        public float Height => Bottom - Top;

        public bool IsFull =>
            Mathf.Approximately(Left, 0f) && Mathf.Approximately(Top, 0f)
            && Mathf.Approximately(Right, 1f) && Mathf.Approximately(Bottom, 1f);

        public static bool HasAnyArgument(JToken? args)
        {
            if (args is not JObject)
            {
                return false;
            }

            foreach (var key in ArgumentKeys)
            {
                if (args[key] != null && args[key]!.Type != JTokenType.Null)
                {
                    return true;
                }
            }

            return false;
        }

        // Missing keys fall back to the full-view edge, so a caller can pass only the sides it wants to
        // trim. Values are validated rather than clamped: a swapped or out-of-range edge is a bug in the
        // caller's arithmetic, and silently "fixing" it would capture (or click) somewhere else.
        public static ScreenshotRegion Read(JToken? args)
        {
            if (!HasAnyArgument(args))
            {
                return Full;
            }

            var left = ReadEdge(args!, LeftKey, 0f);
            var top = ReadEdge(args!, TopKey, 0f);
            var right = ReadEdge(args!, RightKey, 1f);
            var bottom = ReadEdge(args!, BottomKey, 1f);
            if (right <= left)
            {
                throw new ArgumentException($"Screenshot region right ({Format(right)}) must be greater than left ({Format(left)}). All four are 0..1 fractions of the full view with a top-left origin.");
            }

            if (bottom <= top)
            {
                throw new ArgumentException($"Screenshot region bottom ({Format(bottom)}) must be greater than top ({Format(top)}). All four are 0..1 fractions of the full view with a top-left origin (top is the smaller value).");
            }

            return new ScreenshotRegion(left, top, right, bottom);
        }

        // Maps a 0..1 position read off a PNG that was cropped to this region back onto the full view
        // (still top-left origin). The identity for the full region.
        public Vector2 ToFullViewNormalized(float x, float y)
        {
            return new Vector2(Left + Mathf.Clamp01(x) * Width, Top + Mathf.Clamp01(y) * Height);
        }

        // Source pixel rect in top-left texture terms; at least one pixel on each side so a hairline
        // region never produces a zero-sized capture.
        public RectInt ToPixelRect(int sourceWidth, int sourceHeight)
        {
            var x0 = Mathf.Clamp(Mathf.FloorToInt(Left * sourceWidth), 0, Math.Max(0, sourceWidth - 1));
            var y0 = Mathf.Clamp(Mathf.FloorToInt(Top * sourceHeight), 0, Math.Max(0, sourceHeight - 1));
            var x1 = Mathf.Clamp(Mathf.CeilToInt(Right * sourceWidth), x0 + 1, sourceWidth);
            var y1 = Mathf.Clamp(Mathf.CeilToInt(Bottom * sourceHeight), y0 + 1, sourceHeight);
            return new RectInt(x0, y0, x1 - x0, y1 - y0);
        }

        public Dictionary<string, object?> ToMetadata()
        {
            return new Dictionary<string, object?>
            {
                [LeftKey] = Round(Left),
                [TopKey] = Round(Top),
                [RightKey] = Round(Right),
                [BottomKey] = Round(Bottom),
            };
        }

        public override string ToString()
        {
            return $"left:{Format(Left)} top:{Format(Top)} right:{Format(Right)} bottom:{Format(Bottom)}";
        }

        public bool Equals(ScreenshotRegion other)
        {
            return Mathf.Approximately(Left, other.Left) && Mathf.Approximately(Top, other.Top)
                && Mathf.Approximately(Right, other.Right) && Mathf.Approximately(Bottom, other.Bottom);
        }

        public override bool Equals(object? obj) => obj is ScreenshotRegion other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = Round(Left).GetHashCode();
                hash = (hash * 397) ^ Round(Top).GetHashCode();
                hash = (hash * 397) ^ Round(Right).GetHashCode();
                return (hash * 397) ^ Round(Bottom).GetHashCode();
            }
        }

        private static float ReadEdge(JToken args, string key, float defaultValue)
        {
            var token = args[key];
            if (token == null || token.Type == JTokenType.Null)
            {
                return defaultValue;
            }

            float value;
            switch (token.Type)
            {
                case JTokenType.Integer:
                case JTokenType.Float:
                    value = token.Value<float>();
                    break;
                case JTokenType.String when float.TryParse(token.Value<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                    value = parsed;
                    break;
                default:
                    throw new ArgumentException($"Screenshot region '{key}' must be a number between 0 and 1.");
            }

            if (float.IsNaN(value) || value < -0.0001f || value > 1.0001f)
            {
                throw new ArgumentException($"Screenshot region '{key}' ({Format(value)}) must be a 0..1 fraction of the full view (top-left origin), not a pixel count.");
            }

            return Mathf.Clamp01(value);
        }

        private static double Round(float value) => Math.Round(value, 4);

        private static string Format(float value) => value.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
