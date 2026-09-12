using System;
using System.Collections.Generic;

namespace TaskbarTelemetry
{
    internal enum QuotaConnectionState { NotConnected, Connected, Stale, Expired }

    internal sealed class ProviderQuotaMetric
    {
        public string ProviderId { get; set; }
        public string Name { get; set; }
        public string Status { get; set; }
        public QuotaWindowMetric Primary { get; set; }
        public IList<QuotaWindowMetric> Windows { get; set; }
        public DateTime? UpdatedAtLocal { get; set; }
        public QuotaConnectionState State { get; set; }
        public bool HasConnected { get; set; }
        public int FreshForSeconds { get; set; }
        public double? RemainingPercent
        {
            get { return State == QuotaConnectionState.Expired || Primary == null ? null : Primary.RemainingPercent; }
        }
        public ProviderQuotaMetric(string id, string name)
        {
            ProviderId = id; Name = name; Status = "未连接";
            FreshForSeconds = id == "kimi" ? 75 : 15;
            Windows = new List<QuotaWindowMetric>();
        }

        public static ProviderQuotaMetric FromCodex(CodexMetric metric)
        {
            ProviderQuotaMetric result = new ProviderQuotaMetric("codex", "Codex");
            if (metric == null) return result;
            result.Primary = metric.Primary; // Never substitute a different quota bucket.
            if (metric.Primary != null) result.Windows.Add(metric.Primary);
            if (metric.Secondary != null) result.Windows.Add(metric.Secondary);
            result.UpdatedAtLocal = metric.UpdatedAtLocal;
            result.Status = metric.Status;
            result.FreshForSeconds = Math.Min(285, Math.Max(15, metric.RefreshIntervalSeconds + 15));
            return result;
        }
    }

    // One tracker per provider. Re-reading the same cached event must not refresh its age.
    internal sealed class QuotaHistory
    {
        private ProviderQuotaMetric lastGood;
        internal ProviderQuotaMetric Observe(ProviderQuotaMetric sample, DateTime now)
        {
            bool valid = sample.Primary != null && sample.Primary.RemainingPercent.HasValue &&
                sample.UpdatedAtLocal.HasValue && sample.UpdatedAtLocal.Value <= now.AddSeconds(5);
            bool currentWindow = valid && (now - sample.UpdatedAtLocal.Value).TotalSeconds < 300 &&
                (!sample.Primary.ResetsAtLocal.HasValue || sample.Primary.ResetsAtLocal.Value > now);
            if (currentWindow && (lastGood == null || sample.UpdatedAtLocal >= lastGood.UpdatedAtLocal))
                lastGood = sample;
            ProviderQuotaMetric result = new ProviderQuotaMetric(sample.ProviderId, sample.Name);
            result.Status = sample.Status;
            if (lastGood == null) return result;
            result.HasConnected = true;
            result.UpdatedAtLocal = lastGood.UpdatedAtLocal;
            result.FreshForSeconds = lastGood.FreshForSeconds;
            result.Primary = lastGood.Primary;
            result.Windows = new List<QuotaWindowMetric>(lastGood.Windows);
            double age = (now - lastGood.UpdatedAtLocal.Value).TotalSeconds;
            // Active queries get one poll interval plus 15 s grace. Local event
            // timestamps remain unchanged; rereading logs never extends the TTL.
            double freshSeconds = lastGood.FreshForSeconds;
            bool resetPassed = lastGood.Primary.ResetsAtLocal.HasValue && lastGood.Primary.ResetsAtLocal.Value <= now;
            result.State = age >= 300 || resetPassed ? QuotaConnectionState.Expired :
                (!currentWindow || age > freshSeconds ? QuotaConnectionState.Stale : QuotaConnectionState.Connected);
            return result;
        }
    }

    // The UI owns this clock; sampling and network latency cannot advance the display.
    internal sealed class QuotaRotation
    {
        private string selectedId;
        private TimeSpan nextSwitch;
        private TimeSpan? pauseStarted;
        internal void SetHovered(bool hovered, TimeSpan now)
        {
            if (hovered && !pauseStarted.HasValue) pauseStarted = now;
            else if (!hovered && pauseStarted.HasValue)
            {
                nextSwitch += now - pauseStarted.Value;
                pauseStarted = null;
            }
        }
        internal ProviderQuotaMetric Select(IList<ProviderQuotaMetric> sources, TimeSpan now)
        {
            List<ProviderQuotaMetric> available = new List<ProviderQuotaMetric>();
            if (sources != null)
                foreach (ProviderQuotaMetric source in sources)
                    if (source != null && source.HasConnected) available.Add(source);
            if (available.Count == 0) { selectedId = null; return null; }
            int index = available.FindIndex(delegate(ProviderQuotaMetric item) { return item.ProviderId == selectedId; });
            if (index < 0) { index = 0; nextSwitch = (pauseStarted ?? now).Add(TimeSpan.FromSeconds(5)); }
            else if (!pauseStarted.HasValue && now >= nextSwitch)
            {
                if (available.Count > 1) index = (index + 1) % available.Count;
                nextSwitch = now.Add(TimeSpan.FromSeconds(5));
            }
            selectedId = available[index].ProviderId;
            return available[index];
        }
    }
}
