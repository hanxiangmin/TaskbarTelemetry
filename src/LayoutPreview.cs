using System.Collections.Generic;

namespace TaskbarTelemetry
{
    // Display-only filtering: the collectors and their original snapshot stay intact.
    // No synthetic adapter is added if the requested number is not available.
    internal static class LayoutPreview
    {
        internal static TelemetrySnapshot Filter(TelemetrySnapshot source, int gpuLimit)
        {
            if (source == null || gpuLimit <= 0) return source;
            List<GpuMetric> devices = GpuTopology.Devices(source.Gpus);
            if (devices.Count > gpuLimit) devices.RemoveRange(gpuLimit, devices.Count - gpuLimit);
            return new TelemetrySnapshot {
                CapturedAtLocal = source.CapturedAtLocal,
                System = source.System,
                CpuTemperature = source.CpuTemperature,
                CpuFrequency = source.CpuFrequency,
                Gpus = devices,
                Codex = source.Codex,
                Quotas = source.Quotas,
                Notification = source.Notification
            };
        }
    }
}
