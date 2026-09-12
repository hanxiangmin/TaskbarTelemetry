using System;

namespace TaskbarTelemetry
{
    internal static class CodexQuotaSourceSelector
    {
        internal static CodexMetric Select(CodexMetric live, CodexMetric local, DateTime now)
        {
            bool liveUsable = IsUsable(live, now);
            bool localUsable = IsUsable(local, now);
            if (liveUsable && (!localUsable || local.UpdatedAtLocal <= live.UpdatedAtLocal || !SameWindow(live, local)))
                return live;
            if (localUsable) return WithLiveStatus(local, live);
            // Retain the actual timestamps on old readings. QuotaHistory, not
            // repeatedly scanning a file, owns the five-minute expiry boundary.
            if (live != null && live.UpdatedAtLocal.HasValue &&
                (local == null || !local.UpdatedAtLocal.HasValue || live.UpdatedAtLocal >= local.UpdatedAtLocal))
                return live;
            if (local != null) return WithLiveStatus(local, live);
            return live ?? new CodexMetric();
        }

        private static bool IsUsable(CodexMetric value, DateTime now)
        {
            return value != null && value.Primary != null && value.Primary.RemainingPercent.HasValue &&
                value.UpdatedAtLocal.HasValue && value.UpdatedAtLocal <= now.AddSeconds(5) &&
                (now - value.UpdatedAtLocal.Value).TotalSeconds < 300 &&
                (!value.Primary.ResetsAtLocal.HasValue || value.Primary.ResetsAtLocal > now);
        }

        private static bool SameWindow(CodexMetric a, CodexMetric b)
        {
            return a.Primary.WindowDurationMinutes == b.Primary.WindowDurationMinutes &&
                a.Primary.ResetsAtLocal.HasValue && b.Primary.ResetsAtLocal.HasValue &&
                Math.Abs((a.Primary.ResetsAtLocal.Value - b.Primary.ResetsAtLocal.Value).TotalSeconds) <= 60;
        }

        private static CodexMetric WithLiveStatus(CodexMetric local, CodexMetric live)
        {
            if (live != null && !string.IsNullOrWhiteSpace(live.Status))
                local.Status += " / 主动查询: " + live.Status;
            return local;
        }
    }
}
