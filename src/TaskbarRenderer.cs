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
    /// No rectangle depends on a measured live value or selected quota provider.
    /// </summary>
    internal static class TaskbarRenderer
    {
        internal static float WindowWidth(float configuredDualWidth, bool dual)
        {
            return configuredDualWidth * (dual ? 1F : 476F / 528F);
        }

        internal static float[] ColumnEdges(float width, bool dual)
        {
            float referenceWidth = dual ? 528F : 476F;
            return new float[] { 0, width * (dual ? 208 : 140) / referenceWidth,
                width * (dual ? 408 : 356) / referenceWidth, width };
        }

        internal static List<RenderSlot> BuildSlots(Graphics g, Size size, Font font, Font cpuFont,
            AppSettings settings, TelemetrySnapshot snapshot, ProviderQuotaMetric quota)
        {
            List<RenderSlot> slots = new List<RenderSlot>();
            List<GpuMetric> devices = GpuTopology.Devices(snapshot == null ? null : snapshot.Gpus);
            bool dual = devices.Count >= 2;
            float[] edges = ColumnEdges(size.Width, dual);
            float rowHeight = size.Height / 2F;
            float layoutScale = size.Width / (dual ? 528F : 476F);
            float gap = Math.Max(3, 4 * layoutScale);
            float percentWidth = Measure(g, font, "100%");
            float tempWidth = Math.Max(Measure(g, font, "100°"), Measure(g, font, "-99°"));
            float numberWidth = Math.Max(Measure(g, font, "999"), Math.Max(Measure(g, font, "9.9"), Measure(g, font, ">99")));
            float unitWidth = Math.Max(Measure(g, font, "GHz"), Math.Max(Measure(g, font, "GB"), Measure(g, font, "TB")));
            float unitGap = layoutScale;
            float memoryWidth = dual ? Measure(g, font, "999GB") : numberWidth + unitGap + unitWidth;
            float cpuWidth = Measure(g, cpuFont, "WW-WWWWWWW");
            float hardwareLabelWidth = dual ? Math.Max(Measure(g, font, settings.Gpu0Alias), Measure(g, font, settings.Gpu1Alias))
                : Math.Max(Measure(g, font, "CPU"), Measure(g, font, "GPU"));
            // Reserve a real gutter by the separator before distributing the rest.
            // Label rectangles no longer center short names against that separator.
            float leftInset = (dual ? 5 : 6) * layoutScale;
            float rightInset = 2 * layoutScale;
            float hardwareGap = Math.Max(0, Math.Min(2 * gap,
                (edges[2] - edges[1] - leftInset - rightInset - hardwareLabelWidth - memoryWidth - percentWidth - tempWidth) / 3F));
            string cpu = CpuModelDetector.ValidateAlias(settings.CpuAlias).PadRight(CpuModelDetector.LabelCharacters);
            string cpuPercent = snapshot == null || snapshot.System == null ? "--%" : TaskbarForm.FormatPercent(snapshot.System.CpuUsagePercent);
            string cpuTemp = snapshot == null || snapshot.CpuTemperature == null || !snapshot.CpuTemperature.Celsius.HasValue
                ? "--°" : Temperature(snapshot.CpuTemperature.Celsius);
            // Right-side numeric slots share exactly the same x coordinates in both rows.
            for (int row = 0; row < 2; row++)
            {
                float y = row * rowHeight;
                bool cpuHere = !dual && row == 0;
                float right = edges[2] - rightInset;
                float tempX = right - tempWidth;
                float percentX = tempX - hardwareGap - percentWidth;
                float memoryX = percentX - hardwareGap - memoryWidth;
                float labelX = edges[1] + leftInset;
                // Single-card labels need only three letters. Anchor the middle
                // value/unit pair next to them, not to a stretched GPU0 label slot.
                if (!dual) memoryX = Math.Min(memoryX, labelX + hardwareLabelWidth + 2 * layoutScale);
                float labelWidth = dual ? Math.Max(1, memoryX - hardwareGap - labelX) : hardwareLabelWidth;
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
                    if (dual)
                        Add(slots, "gpu" + index + ".memory", Memory(gpu == null ? null : gpu.MemoryUsedBytes),
                            memoryX, y, memoryWidth, rowHeight, false, StringAlignment.Far);
                    else
                    {
                        string value, unit;
                        MemoryParts(gpu == null ? null : gpu.MemoryUsedBytes, out value, out unit);
                        Add(slots, "gpu0.memory.value", value, memoryX, y, numberWidth, rowHeight, false, StringAlignment.Far);
                        Add(slots, "gpu0.memory.unit", unit, memoryX + numberWidth + unitGap, y, unitWidth, rowHeight, false, StringAlignment.Near);
                    }
                }
                GpuMetric rowGpu = cpuHere ? null : devices.Count > (dual ? row : 0) ? devices[dual ? row : 0] : null;
                string prefix = cpuHere ? "cpu" : "gpu" + (dual ? row : 0);
                Add(slots, prefix + ".percent", cpuHere ? cpuPercent : Percent(rowGpu == null ? null : rowGpu.UsagePercent),
                    percentX, y, percentWidth, rowHeight, false, StringAlignment.Far);
                Add(slots, prefix + ".temperature", cpuHere ? (settings.CpuTemperatureEnabled ? cpuTemp : "") : Temperature(rowGpu == null ? null : rowGpu.TemperatureCelsius),
                    tempX, y, tempWidth, rowHeight, false, StringAlignment.Far);
            }
            if (dual)
            {
                float tempX = edges[1] - 2 * gap - tempWidth;
                float percentX = tempX - 2 * gap - percentWidth;
                Add(slots, "cpu.label", cpu, 2 * gap, rowHeight, cpuWidth, rowHeight, true, StringAlignment.Near);
                Add(slots, "cpu.percent", cpuPercent, percentX, rowHeight, percentWidth, rowHeight, false, StringAlignment.Far);
                Add(slots, "cpu.temperature", settings.CpuTemperatureEnabled ? cpuTemp : "", tempX, rowHeight, tempWidth, rowHeight, false, StringAlignment.Far);
            }
            double? up = snapshot == null || snapshot.System == null ? (double?)null : snapshot.System.UploadBytesPerSecond;
            double? down = snapshot == null || snapshot.System == null ? (double?)null : snapshot.System.DownloadBytesPerSecond;
            if (dual)
            {
                float half = (edges[1] - 2 * gap) / 2;
                AddNetwork(slots, g, font, "network.upload", "↑", Rate(up), gap, 0, half, rowHeight, gap);
                AddNetwork(slots, g, font, "network.download", "↓", Rate(down), gap + half, 0, half, rowHeight, gap);
            }
            else
            {
                AddNetwork(slots, g, font, "network.upload", "↑", Rate(up), gap, 0, edges[1] - 2 * gap, rowHeight, gap);
                AddNetwork(slots, g, font, "network.download", "↓", Rate(down), gap, rowHeight, edges[1] - 2 * gap, rowHeight, gap);
            }
            float quotaPercentX = edges[3] - 2 * gap - percentWidth;
            float nameWidth = quotaPercentX - edges[2] - 3 * gap;
            Add(slots, "ram.label", "内存", edges[2] + gap, 0, nameWidth, rowHeight, false, StringAlignment.Center);
            Add(slots, "ram.percent", snapshot == null || snapshot.System == null ? "--%" : TaskbarForm.FormatPercent(snapshot.System.MemoryUsagePercent),
                quotaPercentX, 0, percentWidth, rowHeight, false, StringAlignment.Far);
            Add(slots, "quota.label", quota == null ? "未连接" : quota.Name, edges[2] + gap, rowHeight, nameWidth, rowHeight, false, StringAlignment.Center);
            Add(slots, "quota.percent", quota == null ? "" : Percent(quota.RemainingPercent), quotaPercentX, rowHeight, percentWidth, rowHeight, false, StringAlignment.Far);
            // Separate stale marker, so even stale 100% fits the percent slot.
            Add(slots, "quota.stale", quota != null && (quota.State == QuotaConnectionState.Stale || quota.State == QuotaConnectionState.Expired) ? "·" : "",
                edges[3] - gap, rowHeight, gap, rowHeight, false, StringAlignment.Center);
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

        private static float Measure(Graphics g, Font font, string text)
        {
            using (StringFormat format = new StringFormat(StringFormat.GenericTypographic))
            { format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces; return (float)Math.Ceiling(g.MeasureString(text, font, 1000, format).Width) + 2; }
        }
        private static void AddNetwork(List<RenderSlot> slots, Graphics g, Font font, string id,
            string arrow, string rate, float x, float y, float width, float height, float gap)
        {
            float arrowWidth = Measure(g, font, "↑");
            float numberWidth = Math.Max(Measure(g, font, "888.8"), Measure(g, font, "88.88"));
            float unitWidth = Measure(g, font, "M");
            float start = x + (width - arrowWidth - numberWidth - unitWidth - 2 * gap) / 2;
            int separator = rate.LastIndexOf(' ');
            Add(slots, id + ".arrow", arrow, start, y, arrowWidth, height, false, StringAlignment.Center);
            Add(slots, id + ".value", rate.Substring(0, separator), start + arrowWidth + gap, y, numberWidth, height, false, StringAlignment.Far);
            Add(slots, id + ".unit", rate.Substring(separator + 1), start + arrowWidth + numberWidth + 2 * gap,
                y, unitWidth, height, false, StringAlignment.Near);
        }
        private static void Add(List<RenderSlot> slots, string id, string text, float x, float y, float w, float h, bool cpu, StringAlignment alignment)
        {
            slots.Add(new RenderSlot { Id = id, Text = text, Bounds = new RectangleF(x, y, w, h), CpuFont = cpu, Alignment = alignment });
        }
        private static string Percent(double? value) { return value.HasValue ? TaskbarForm.FormatPercent(value.Value) : "--%"; }
        private static string Temperature(double? value)
        {
            return !value.HasValue || value.Value < -99 || value.Value > 999 ? "--°" : TaskbarForm.FormatTemperature(value.Value);
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
        internal static string Rate(double? bytes)
        {
            if (!bytes.HasValue || double.IsNaN(bytes.Value) || double.IsInfinity(bytes.Value) || bytes.Value < 0) return "--.-- M";
            double value = bytes.Value / (1024 * 1024);
            string unit = "M";
            if (value >= 999.95) { value /= 1024; unit = "G"; }
            if (value >= 999.95) { value /= 1024; unit = "T"; }
            if (value >= 999.95) return ">999 T";
            return value.ToString(value < 99.995 ? "00.00" : "000.0", CultureInfo.InvariantCulture) + " " + unit;
        }
    }
}
