using System;
using System.Collections.Generic;

namespace TaskbarTelemetry
{
    internal static class GpuTopology
    {
        internal static List<GpuMetric> Devices(IList<GpuMetric> metrics)
        {
            List<GpuMetric> result = new List<GpuMetric>();
            HashSet<string> identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (metrics != null)
                foreach (GpuMetric gpu in metrics)
                {
                    if (gpu == null || !gpu.IsNvidiaDevice) continue;
                    string identity = !string.IsNullOrWhiteSpace(gpu.StableId) ? gpu.StableId : gpu.PciBusId;
                    if (!string.IsNullOrWhiteSpace(identity) && identities.Add(identity)) result.Add(gpu);
                }
            result.Sort(delegate(GpuMetric a, GpuMetric b)
            {
                int rank = a.DisplayIndex.CompareTo(b.DisplayIndex);
                if (rank != 0) return rank;
                rank = StringComparer.OrdinalIgnoreCase.Compare(a.PciBusId, b.PciBusId);
                return rank != 0 ? rank : StringComparer.OrdinalIgnoreCase.Compare(a.StableId, b.StableId);
            });
            return result;
        }

        internal static GpuMetric MissingSensors(GpuMetric identity, string status)
        {
            return new GpuMetric { IsNvidiaDevice = identity.IsNvidiaDevice,
                DisplayIndex = identity.DisplayIndex, StableId = identity.StableId,
                PciBusId = identity.PciBusId, Name = identity.Name, Status = status };
        }
    }
}
