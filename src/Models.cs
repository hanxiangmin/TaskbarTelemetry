using System;
using System.Collections.Generic;

namespace TaskbarTelemetry
{
    internal sealed class SystemMetric
    {
        public double UploadBytesPerSecond { get; set; }
        public double DownloadBytesPerSecond { get; set; }
        public double CpuUsagePercent { get; set; }
        public double MemoryUsagePercent { get; set; }
        public ulong MemoryUsedBytes { get; set; }
        public ulong MemoryTotalBytes { get; set; }
        public string NetworkName { get; set; }
        public string Status { get; set; }

        public SystemMetric()
        {
            NetworkName = string.Empty;
            Status = "Starting";
        }
    }

    internal sealed class TemperatureMetric
    {
        public double? Celsius { get; set; }
        public string SensorName { get; set; }
        public string Status { get; set; }

        public TemperatureMetric()
        {
            SensorName = string.Empty;
            Status = "Not initialized";
        }
    }

    internal sealed class CpuFrequencyMetric
    {
        public double? Megahertz { get; set; }
        public string SensorName { get; set; }
        public string Status { get; set; }

        public CpuFrequencyMetric()
        {
            SensorName = string.Empty;
            Status = "Core clock sensor unavailable";
        }
    }

    internal sealed class GpuMetric
    {
        // Only set after a real NVML identity (UUID or PCI bus address) was read.
        public bool IsNvidiaDevice { get; set; }
        public int DisplayIndex { get; set; }
        public string StableId { get; set; }
        public string Name { get; set; }
        public string PciBusId { get; set; }
        public ulong? MemoryUsedBytes { get; set; }
        public ulong? MemoryTotalBytes { get; set; }
        public double? UsagePercent { get; set; }
        public double? TemperatureCelsius { get; set; }
        public string Status { get; set; }

        public GpuMetric()
        {
            StableId = string.Empty;
            Name = string.Empty;
            PciBusId = string.Empty;
            Status = "Starting";
        }
    }

    internal sealed class QuotaWindowMetric
    {
        public string Name { get; set; }
        public double? UsedPercent { get; set; }
        public int? WindowDurationMinutes { get; set; }
        public DateTime? ResetsAtLocal { get; set; }

        public double? RemainingPercent
        {
            get
            {
                if (!UsedPercent.HasValue || double.IsNaN(UsedPercent.Value) || double.IsInfinity(UsedPercent.Value) || UsedPercent.Value < 0)
                    return null;
                return Math.Max(0.0, Math.Min(100.0, 100.0 - UsedPercent.Value));
            }
        }
    }

    internal sealed class CodexMetric
    {
        // Zero means event-driven local data; positive means active poll cadence.
        public int RefreshIntervalSeconds { get; set; }
        public QuotaWindowMetric Primary { get; set; }
        public QuotaWindowMetric Secondary { get; set; }
        public string PlanType { get; set; }
        public string Status { get; set; }
        public DateTime? UpdatedAtLocal { get; set; }

        public CodexMetric()
        {
            PlanType = string.Empty;
            Status = "Starting Codex quota collector";
        }
    }

    internal sealed class NotificationMetric
    {
        public bool Enabled { get; set; }
        public string Channel { get; set; }
        public string Status { get; set; }
        public DateTime? LastSentAtLocal { get; set; }

        public NotificationMetric()
        {
            Channel = "Server酱";
            Status = "通知未启用";
        }
    }

    internal sealed class TelemetrySnapshot
    {
        public DateTime CapturedAtLocal { get; set; }
        public SystemMetric System { get; set; }
        public TemperatureMetric CpuTemperature { get; set; }
        public CpuFrequencyMetric CpuFrequency { get; set; }
        public IList<GpuMetric> Gpus { get; set; }
        public CodexMetric Codex { get; set; }
        public IList<ProviderQuotaMetric> Quotas { get; set; }
        public NotificationMetric Notification { get; set; }

        public TelemetrySnapshot()
        {
            CapturedAtLocal = DateTime.Now;
            System = new SystemMetric();
            CpuTemperature = new TemperatureMetric();
            CpuFrequency = new CpuFrequencyMetric();
            Gpus = new List<GpuMetric>();
            Codex = new CodexMetric();
            Quotas = new List<ProviderQuotaMetric>();
            Notification = new NotificationMetric();
        }
    }
}
