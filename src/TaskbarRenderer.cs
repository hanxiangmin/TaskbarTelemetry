using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace TaskbarTelemetry
{
    internal sealed class RenderSlot
    {
        internal string Id;
        internal string Text;
        internal RectangleF Bounds;
        internal bool CpuFont;
        internal StringAlignment Alignment;
    }

    /// <summary>
    /// Production renderer shared by the real child window and deterministic tests.
    /// Column dividers stay fixed as readings and quota providers change.
    /// </summary>
    internal static class TaskbarRenderer
    {
        internal static float WindowWidth(float configuredDualWidth, bool dual)
        {
            return configuredDualWidth * (dual ? 1F : 476F / 520F);
        }

        internal static float[] ColumnEdges(float width, bool dual)
        {
            float referenceWidth = dual ? 520F : 476F;
            return new float[] { 0, width * (dual ? 200 : 140) / referenceWidth,
                width * (dual ? 402 : 356) / referenceWidth, width };
        }

        internal static List<RenderSlot> BuildSlots(Graphics g, Size size, Font font, Font cpuFont,
            AppSettings settings, TelemetrySnapshot snapshot, ProviderQuotaMetric quota)
        {
            List<RenderSlot> slots = new List<RenderSlot>();
            List<GpuMetric> devices = GpuTopology.Devices(snapshot == null ? null : snapshot.Gpus);
            bool dual = devices.Count >= 2;
            float[] edges = ColumnEdges(size.Width, dual);
            float rowHeight = size.Height / 2F;
            float layoutScale = size.Width / (dual ? 520F : 476F);
            float gap = Math.Max(3, 4 * layoutScale);
            float percentWidth = Measure(g, font, dual ? "99%" : "100%", 0);
            float tempWidth = Math.Max(Measure(g, font, "888°C", 0), Measure(g, font, "-88°C", 0));
            // Reserve three digits if either card has at least 100 GB or its capacity is unknown.
            const ulong TwoDigitMemoryLimit = 99UL * 1024 * 1024 * 1024 + 512 * 1024 * 1024;
            bool twoDigitMemory = dual && devices.GetRange(0, 2).TrueForAll(gpu =>
                gpu.MemoryTotalBytes.GetValueOrDefault() > 0 &&
                gpu.MemoryTotalBytes.Value < TwoDigitMemoryLimit &&
                gpu.MemoryUsedBytes.GetValueOrDefault() < TwoDigitMemoryLimit);
            float numberWidth = dual ? Measure(g, font, twoDigitMemory ? "88" : "888", 0) :
                Math.Max(Measure(g, font, "999"), Math.Max(Measure(g, font, "9.9"), Measure(g, font, ">99")));
            if (dual && !twoDigitMemory)
                numberWidth = Math.Max(numberWidth, Measure(g, font, ">99", 0));
            float memoryUnitWidth = Math.Max(Measure(g, font, "GB", 0), Measure(g, font, "TB", 0));
            float unitWidth = dual ? memoryUnitWidth : Math.Max(Measure(g, font, "GHz"), memoryUnitWidth + 2);
            float unitGap = dual ? 0 : layoutScale;
            float memoryWidth = numberWidth + unitGap + unitWidth;
            float cpuWidth = Measure(g, cpuFont, "WW-WWWWWWW", 0);
            float hardwareLabelWidth = dual ? Math.Max(Measure(g, font, settings.Gpu0Alias, 0), Measure(g, font, settings.Gpu1Alias, 0))
                : Math.Max(Measure(g, font, "CPU"), Measure(g, font, "GPU"));
            float leftInset = (dual ? 4 : 6) * layoutScale;
            float rightInset = (dual ? 7 : 5) * layoutScale;
            float hardwareWidth = edges[2] - edges[1] - leftInset - rightInset;
            float extraHardwareSpace = hardwareWidth - hardwareLabelWidth - memoryWidth - percentWidth - tempWidth;
            float hardwareGap = Math.Max(0, Math.Min((dual ? 4 : 2) * layoutScale, extraHardwareSpace / 3));
            float labelWidth = Math.Max(1, Math.Min(hardwareLabelWidth,
                hardwareWidth - memoryWidth - percentWidth - tempWidth - 3 * hardwareGap));
            float labelX = edges[1] + leftInset;
            if (dual)
                labelX += Math.Min(5 * layoutScale, Math.Max(0, extraHardwareSpace - 3 * hardwareGap));
            float memoryX = labelX + labelWidth + hardwareGap;
            float percentX = memoryX + memoryWidth + hardwareGap;
            float tempX = percentX + percentWidth + hardwareGap;
            string cpu = CpuModelDetector.ValidateAlias(settings.CpuAlias).PadRight(CpuModelDetector.LabelCharacters);
            string cpuPercent = snapshot == null || snapshot.System == null ? "--%" : TaskbarForm.FormatPercent(snapshot.System.CpuUsagePercent);
            string cpuTemp = Temperature(snapshot == null || snapshot.CpuTemperature == null
                ? null : snapshot.CpuTemperature.Celsius);
            // Pack the metrics next to the label and share their positions across rows.
            for (int row = 0; row < 2; row++)
            {
                float y = row * rowHeight;
                bool cpuHere = !dual && row == 0;
                if (cpuHere)
                {
                    Add(slots, "cpu.label", "CPU", labelX, y, labelWidth, rowHeight, false, StringAlignment.Near);
                    Add(slots, "cpu.frequency.value", FrequencyNumber(snapshot == null || snapshot.CpuFrequency == null ? null : snapshot.CpuFrequency.Megahertz),
                        memoryX, y, numberWidth, rowHeight, false, StringAlignment.Far);
                    Add(slots, "cpu.frequency.unit", "GHz", memoryX + numberWidth + unitGap, y, unitWidth, rowHeight, false, StringAlignment.Near);
                }
                else
                {
                    int index = dual ? row : 0;
                    GpuMetric gpu = devices.Count > index ? devices[index] : null;
                    string alias = index == 0 ? settings.Gpu0Alias : settings.Gpu1Alias;
                    // At legacy physical-pixel widths, missing numeric readings and
                    // the tooltip still explain unavailability without clipping text.
                    string gpuLabel = !dual ? "GPU" : gpu == null ? (Measure(g, font, "GPU不可用") <= labelWidth ? "GPU不可用" : "GPU") : alias;
                    Add(slots, "gpu" + index + ".label", gpuLabel,
                        labelX, y, labelWidth, rowHeight, false, StringAlignment.Near);
                    string value, unit;
                    MemoryParts(gpu == null ? null : gpu.MemoryUsedBytes, out value, out unit);
                    Add(slots, "gpu" + index + ".memory.value", value, memoryX, y, numberWidth, rowHeight, false, StringAlignment.Far);
                    Add(slots, "gpu" + index + ".memory.unit", unit,
                        memoryX + numberWidth + unitGap, y, unitWidth, rowHeight, false, StringAlignment.Near);
                }
                GpuMetric rowGpu = cpuHere ? null : devices.Count > (dual ? row : 0) ? devices[dual ? row : 0] : null;
                string prefix = cpuHere ? "cpu" : "gpu" + (dual ? row : 0);
                double? gpuUsage = rowGpu == null ? null : rowGpu.UsagePercent;
                string usage = cpuHere ? cpuPercent : Percent(gpuUsage.HasValue ? Math.Min(99, gpuUsage.Value) : gpuUsage);
                string temperature = cpuHere ? (settings.CpuTemperatureEnabled ? cpuTemp : "") :
                    Temperature(rowGpu == null ? null : rowGpu.TemperatureCelsius);
                Add(slots, prefix + ".percent", usage, percentX, y, percentWidth, rowHeight, false, StringAlignment.Far);
                Add(slots, prefix + ".temperature", temperature, tempX, y, tempWidth, rowHeight, false, StringAlignment.Far);
            }
            double? up = snapshot == null || snapshot.System == null ? (double?)null : snapshot.System.UploadBytesPerSecond;
            double? down = snapshot == null || snapshot.System == null ? (double?)null : snapshot.System.DownloadBytesPerSecond;
            bool rateInBytes = settings.NetworkRateInBytes;
            if (dual)
            {
                float half = (edges[1] - 2 * gap) / 2;
                AddNetwork(slots, g, font, "network.upload", "↑", up, rateInBytes, gap, 0, half, rowHeight, gap);
                float cpuInset = 3 * layoutScale;
                float downloadRight = edges[1] - cpuInset;
                AddNetwork(slots, g, font, "network.download", "↓", down, rateInBytes,
                    gap + half, 0, half, rowHeight, gap, downloadRight);
                float cpuMetricsX = cpuInset + cpuWidth;
                float cpuMetricsWidth = Math.Max(1, downloadRight - cpuMetricsX);
                string cpuMetrics = cpuPercent;
                if (settings.CpuTemperatureEnabled)
                {
                    cpuMetrics = cpuPercent + "\u2002" + cpuTemp;
                    if (Measure(g, font, cpuMetrics, 0) > cpuMetricsWidth)
                        cpuMetrics = cpuPercent + "\u2009" + cpuTemp;
                }
                Add(slots, "cpu.label", cpu, cpuInset, rowHeight, cpuWidth, rowHeight, true, StringAlignment.Near);
                Add(slots, "cpu.metrics", cpuMetrics, cpuMetricsX, rowHeight, cpuMetricsWidth, rowHeight, false, StringAlignment.Far);
            }
            else
            {
                AddNetwork(slots, g, font, "network.upload", "↑", up, rateInBytes, gap, 0, edges[1] - 2 * gap, rowHeight, gap);
                AddNetwork(slots, g, font, "network.download", "↓", down, rateInBytes, gap, rowHeight, edges[1] - 2 * gap, rowHeight, gap);
            }
            float quotaNameGap = 6 * layoutScale;
            // Put the stale dot next to the value so it shares the percentage slot.
            float quotaPercentWidth = Measure(g, font, "100%·", 0);
            float nameWidth = Math.Max(Measure(g, font, "未连接"),
                Math.Max(Measure(g, font, "Codex"), Measure(g, font, "Kimi")));
            float quotaWidth = edges[3] - edges[2];
            float quotaInset = 2 * layoutScale;
            nameWidth = Math.Max(1, Math.Min(nameWidth, quotaWidth - 2 * quotaInset - quotaNameGap - quotaPercentWidth));
            float extraQuotaSpace = quotaWidth - nameWidth - quotaNameGap - quotaPercentWidth;
            float quotaLabelX = edges[2] + extraQuotaSpace / 2;
            float quotaPercentX = quotaLabelX + nameWidth + quotaNameGap;
            Add(slots, "ram.label", "内存", quotaLabelX, 0, nameWidth, rowHeight, false, StringAlignment.Far);
            Add(slots, "ram.percent", snapshot == null || snapshot.System == null ? "--%" : TaskbarForm.FormatPercent(snapshot.System.MemoryUsagePercent),
                quotaPercentX, 0, quotaPercentWidth, rowHeight, false, StringAlignment.Near);
            Add(slots, "quota.label", quota == null ? "未连接" : quota.Name, quotaLabelX, rowHeight, nameWidth, rowHeight, false, StringAlignment.Far);
            string quotaPercent = quota == null ? "" : Percent(quota.RemainingPercent);
            if (quota != null && (quota.State == QuotaConnectionState.Stale || quota.State == QuotaConnectionState.Expired))
                quotaPercent += "·";
            Add(slots, "quota.percent", quotaPercent, quotaPercentX, rowHeight, quotaPercentWidth, rowHeight, false, StringAlignment.Near);
            return slots;
        }

        internal static void Draw(Graphics g, Size size, Font font, Color foreground, AppSettings settings,
            TelemetrySnapshot snapshot, ProviderQuotaMetric quota)
        {
            using (Font cpuFont = new Font("Consolas", font.SizeInPoints, FontStyle.Regular, GraphicsUnit.Point))
            using (Brush brush = new SolidBrush(foreground))
            using (Pen pen = new Pen(Color.FromArgb(130, foreground)))
            using (StringFormat format = new StringFormat(StringFormat.GenericTypographic))
            {
                format.LineAlignment = StringAlignment.Center;
                format.Trimming = StringTrimming.None;
                format.FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
                foreach (RenderSlot slot in BuildSlots(g, size, font, cpuFont, settings, snapshot, quota))
                {
                    format.Alignment = slot.Alignment;
                    GraphicsState state = g.Save();
                    g.SetClip(slot.Bounds, CombineMode.Intersect);
                    g.DrawString(slot.Text, slot.CpuFont ? cpuFont : font, brush, slot.Bounds, format);
                    g.Restore(state);
                }
                bool dual = GpuTopology.Devices(snapshot == null ? null : snapshot.Gpus).Count >= 2;
                float[] edges = ColumnEdges(size.Width, dual);
                g.DrawLine(pen, 0, size.Height / 2F, size.Width, size.Height / 2F);
                for (int i = 1; i < 3; i++) g.DrawLine(pen, edges[i], 3, edges[i], size.Height - 3);
            }
        }

        private static float Measure(Graphics g, Font font, string text, float padding = 2)
        {
            using (StringFormat format = new StringFormat(StringFormat.GenericTypographic))
            { format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces; return (float)Math.Ceiling(g.MeasureString(text, font, 1000, format).Width) + padding; }
        }
        private static void AddNetwork(List<RenderSlot> slots, Graphics g, Font font, string id,
            string arrow, double? bytes, bool rateInBytes, float x, float y, float width, float height, float gap,
            float? rightEdge = null)
        {
            const string arrowGap = "\u2009";
            float numberWidth = Math.Max(Measure(g, font, arrow + arrowGap + "888.8"),
                Measure(g, font, arrow + arrowGap + "88.88"));
            string suffix = rateInBytes ? "B" : "b";
            float unitWidth = Math.Max(Measure(g, font, "M" + suffix),
                Math.Max(Measure(g, font, "G" + suffix), Measure(g, font, "T" + suffix)));
            // Keep the arrow with the number and reserve a pixel on each end of the cell.
            gap = Math.Max(0, Math.Min(gap / 2, width - numberWidth - unitWidth - 2));
            float contentWidth = numberWidth + unitWidth + gap;
            float start = rightEdge.HasValue ? rightEdge.Value - contentWidth : x + (width - contentWidth) / 2;
            string rate = Rate(bytes, rateInBytes);
            int separator = rate.LastIndexOf(' ');
            Add(slots, id + ".value", arrow + arrowGap + rate.Substring(0, separator),
                start, y, numberWidth, height, false, StringAlignment.Far);
            float unitX = start + numberWidth + gap;
            Add(slots, id + ".unit", rate.Substring(separator + 1), unitX,
                y, unitWidth, height, false, rightEdge.HasValue ? StringAlignment.Far : StringAlignment.Near);
        }
        private static void Add(List<RenderSlot> slots, string id, string text, float x, float y, float w, float h, bool cpu, StringAlignment alignment)
        {
            slots.Add(new RenderSlot { Id = id, Text = text, Bounds = new RectangleF(x, y, w, h), CpuFont = cpu, Alignment = alignment });
        }
        private static string Percent(double? value) { return value.HasValue ? TaskbarForm.FormatPercent(value.Value) : "--%"; }
        private static string Temperature(double? value)
        {
            return !value.HasValue || value.Value < -99 || value.Value > 999 ? "--°C" : TaskbarForm.FormatTemperature(value.Value);
        }
        private static string Memory(ulong? bytes)
        {
            if (!bytes.HasValue) return "--GB";
            double gb = bytes.Value / (1024.0 * 1024 * 1024);
            if (gb < 999.5) return gb.ToString("0", CultureInfo.InvariantCulture) + "GB";
            double tb = gb / 1024;
            return tb < 999.5 ? tb.ToString("0", CultureInfo.InvariantCulture) + "TB" : ">999T";
        }

        internal static string FrequencyNumber(double? megahertz)
        {
            if (!megahertz.HasValue || double.IsNaN(megahertz.Value) || double.IsInfinity(megahertz.Value) ||
                megahertz.Value <= 0 || megahertz.Value >= 100000) return "--";
            double ghz = megahertz.Value / 1000.0;
            return ghz.ToString(ghz < 9.95 ? "0.0" : "0", CultureInfo.InvariantCulture);
        }

        private static void MemoryParts(ulong? bytes, out string value, out string unit)
        {
            string combined = Memory(bytes);
            if (combined == ">999T") { value = ">99"; unit = "TB"; return; }
            value = combined.Substring(0, combined.Length - 2);
            unit = combined.Substring(combined.Length - 2);
        }
        internal static string Rate(double? bytes, bool rateInBytes = false)
        {
            string suffix = rateInBytes ? "B" : "b";
            if (!bytes.HasValue || double.IsNaN(bytes.Value) || double.IsInfinity(bytes.Value) || bytes.Value < 0)
                return "--.-- M" + suffix;
            // Divide first to avoid overflowing even for a finite double.MaxValue.
            double value = bytes.Value / (rateInBytes ? 1000000.0 : 125000.0);
            string unit = "M";
            if (value >= 999.95) { value /= 1000; unit = "G"; }
            if (value >= 999.95) { value /= 1000; unit = "T"; }
            if (value >= 999.95) return ">999 T" + suffix;
            return value.ToString(value < 99.995 ? "0.00" : "0.0", CultureInfo.InvariantCulture) + " " + unit + suffix;
        }
    }
}
