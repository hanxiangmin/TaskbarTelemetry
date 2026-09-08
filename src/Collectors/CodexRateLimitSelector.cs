using System;
using System.Collections.Generic;
using System.Globalization;

namespace TaskbarTelemetry
{
    internal enum CodexRateLimitKind
    {
        Legacy,
        General,
        ModelSpecific
    }

    /// <summary>
    /// Distinguishes the shared Codex allowance from model-specific limits
    /// such as GPT-5.3-Codex-Spark. Older Codex builds did not include an
    /// identifier, so identifier-free snapshots remain a legacy fallback.
    /// </summary>
    internal static class CodexRateLimitSelector
    {
        internal static CodexRateLimitKind Classify(IDictionary<string, object> limits)
        {
            if (limits == null)
                return CodexRateLimitKind.ModelSpecific;

            string limitId = GetString(limits, "limitId", "limit_id");
            if (limitId.Length == 0)
                return CodexRateLimitKind.Legacy;

            return string.Equals(limitId, "codex", StringComparison.OrdinalIgnoreCase)
                ? CodexRateLimitKind.General
                : CodexRateLimitKind.ModelSpecific;
        }

        internal static bool IsGeneralOrLegacy(IDictionary<string, object> limits)
        {
            return Classify(limits) != CodexRateLimitKind.ModelSpecific;
        }

        private static string GetString(IDictionary<string, object> source, string first, string second)
        {
            object value;
            if ((!source.TryGetValue(first, out value) && !source.TryGetValue(second, out value)) || value == null)
                return string.Empty;
            return Convert.ToString(value, CultureInfo.InvariantCulture).Trim();
        }
    }
}
