using System;
using System.Text.RegularExpressions;

namespace TaskbarTelemetry
{
    internal static class CpuClockSelector
    {
        // LHM 0.9.6 Intel (CPU/P-/E-Core #n) and AMD (Core #n) names.
        // Explicitly exclude bus clocks, averages and effective-clock sensors.
        private static readonly Regex CoreName = new Regex(@"^(?:CPU )?(?:[PE]-)?Core #\d+$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal static void Observe(CpuFrequencyMetric result, string name, double? megahertz)
        {
            if (result == null || string.IsNullOrWhiteSpace(name) || !CoreName.IsMatch(name.Trim()) ||
                !megahertz.HasValue || double.IsNaN(megahertz.Value) || double.IsInfinity(megahertz.Value) ||
                megahertz.Value <= 0 || megahertz.Value >= 100000) return;
            if (!result.Megahertz.HasValue || megahertz.Value > result.Megahertz.Value)
            {
                result.Megahertz = megahertz;
                result.SensorName = name;
                result.Status = "LibreHardwareMonitor reported core clock (highest); not average effective clock";
            }
        }
    }
}
