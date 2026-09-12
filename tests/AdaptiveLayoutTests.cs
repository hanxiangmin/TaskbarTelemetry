using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace TaskbarTelemetry
{
    internal static class AdaptiveLayoutTests
    {
        private static int assertions;
        private static int failures;
        internal static bool Run()
        {
            CheckCpuNames();
            CheckCompactDefaults();
            CheckCoreClocks();
            CheckNetworkRates();
            CheckTopology();
            CheckQuotas();
            CheckActiveQuotaPolling();
            CheckRendering();
            CheckQuotaRendering();
            CreateOverview();
            Console.WriteLine("ADAPTIVE assertions={0} failures={1}", assertions, failures);
            return failures == 0;
        }

        private static void CheckNetworkRates()
        {
            Check("Mbps zero", TaskbarRenderer.Rate(0) == "0.00 Mb");
            Check("Mbps below one without padding", TaskbarRenderer.Rate(0.16 * 125000) == "0.16 Mb");
            Check("Mbps single digit without padding", TaskbarRenderer.Rate(1.16 * 125000) == "1.16 Mb");
            Check("Mbps two digits retained", TaskbarRenderer.Rate(12.16 * 125000) == "12.16 Mb");
            Check("Mbps decimal not binary", TaskbarRenderer.Rate(1000000) == "8.00 Mb");
            Check("speed test upload", TaskbarRenderer.Rate(93.57 * 125000) == "93.57 Mb");
            Check("speed test download", TaskbarRenderer.Rate(92.88 * 125000) == "92.88 Mb");
            Check("old MiB reading becomes Mbps", TaskbarRenderer.Rate(11.74 * 1048576) == "98.48 Mb");
            Check("three digit Mbps", TaskbarRenderer.Rate(100 * 125000) == "100.0 Mb");
            Check("before Gbps boundary", TaskbarRenderer.Rate(999.94 * 125000) == "999.9 Mb");
            Check("rounded Gbps boundary", TaskbarRenderer.Rate(999.95 * 125000) == "1.00 Gb");
            Check("Gbps decimal boundary", TaskbarRenderer.Rate(125000000) == "1.00 Gb");
            Check("Tbps decimal boundary", TaskbarRenderer.Rate(125000000000) == "1.00 Tb");
            Check("finite extreme capped", TaskbarRenderer.Rate(double.MaxValue) == ">999 Tb");
            foreach (double? invalid in new double?[] { null, -1, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
                Check("invalid network placeholder", TaskbarRenderer.Rate(invalid) == "--.-- Mb");
        }

        private static void CheckCompactDefaults()
        {
            AppSettings defaults = AppSettings.Load(string.Empty);
            Check("clean defaults use compact physical width", defaults.TaskbarWidth == 520 &&
                !defaults.ScaleWidthWithDpi && defaults.FontSizePoints == 8.5f);
            Check("dual has separator gutters while single footprint is unchanged", TaskbarRenderer.WindowWidth(defaults.TaskbarWidth, true) == 520 &&
                TaskbarRenderer.WindowWidth(defaults.TaskbarWidth, false) == 476);
            string templatePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TaskbarTelemetry.ini");
            Check("release template is present for layout regression", File.Exists(templatePath));
            AppSettings releaseDefaults = AppSettings.Load(templatePath);
            Check("release INI and no-file defaults agree on compact layout", releaseDefaults.TaskbarWidth == defaults.TaskbarWidth &&
                releaseDefaults.ScaleWidthWithDpi == defaults.ScaleWidthWithDpi && releaseDefaults.FontSizePoints == defaults.FontSizePoints);
        }

        private static void CheckCpuNames()
        {
            string[,] examples = {
                { "Intel Core i3-14100F", "i3-14100F" }, { "Intel Core i5-14600K", "i5-14600K" },
                { "Intel Core i7-14700KF", "i7-14700KF" }, { "Intel Core i9-10980XE", "i9-10980XE" },
                { "Intel Core Ultra 9 285K", "U9-285K" }, { "Intel Core Ultra 7 265H", "U7-265H" },
                { "Intel Core 5 120U", "C5-120U" }, { "AMD Ryzen 9 9950X3D", "R9-9950X3D" },
                { "AMD Ryzen 7 9800X3D", "R7-9800X3D" }, { "AMD Ryzen AI 9 HX 370", "A9-HX370" },
                { "AMD Ryzen AI 7 350", "A7-350" }, { "AMD Ryzen Threadripper PRO 7995WX", "TR-7995WX" },
                { "AMD Ryzen AI Max+ 395", "CPU" }, { "Intel Xeon W-2295", "CPU" },
                { "AMD Ryzen 9 1234567ABC", "CPU" }, { "Unknown Processor", "CPU" }
                , { "Intel Core i9-12345-ABC", "CPU" }
            };
            for (int i = 0; i < examples.GetLength(0); i++)
                Check("CPU " + examples[i, 0], CpuModelDetector.ExtractShortLabel(examples[i, 0]) == examples[i, 1]);
            Check("CPU alias rejects long/unknown", CpuModelDetector.ValidateAlias("i9-123456789") == "CPU" &&
                CpuModelDetector.ValidateAlias("My CPU") == "CPU");
        }

        private static void CheckCoreClocks()
        {
            CpuFrequencyMetric frequency = new CpuFrequencyMetric();
            CpuClockSelector.Observe(frequency, "Bus Speed", 100);
            CpuClockSelector.Observe(frequency, "Cores (Average)", 9000);
            CpuClockSelector.Observe(frequency, "Core #1 (Effective)", 9000);
            Check("bus/average/effective clocks excluded", !frequency.Megahertz.HasValue);
            CpuClockSelector.Observe(frequency, "CPU Core #1", 3800);
            CpuClockSelector.Observe(frequency, "P-Core #2", 4200);
            CpuClockSelector.Observe(frequency, "E-Core #3", 2900);
            CpuClockSelector.Observe(frequency, "Core #4", 4100);
            CpuClockSelector.Observe(frequency, "Core #5", double.NaN);
            CpuClockSelector.Observe(frequency, "Core #6", double.PositiveInfinity);
            CpuClockSelector.Observe(frequency, "Core #7", -1);
            Check("highest valid Intel/AMD core clock", frequency.Megahertz == 4200 && frequency.SensorName == "P-Core #2");
            Check("frequency units and compact precision", TaskbarRenderer.FrequencyNumber(3800) == "3.8" &&
                TaskbarRenderer.FrequencyNumber(800) == "0.8" && TaskbarRenderer.FrequencyNumber(10000) == "10");
            Check("missing clock not substituted with model/base speed", TaskbarRenderer.FrequencyNumber(null) == "--" &&
                TaskbarRenderer.FrequencyNumber(double.NaN) == "--" && TaskbarRenderer.FrequencyNumber(0) == "--");
            TelemetrySnapshot original = Fixture(2, false);
            original.CpuFrequency = frequency;
            TelemetrySnapshot single = LayoutPreview.Filter(original, 1);
            Check("preview preserves frequency and original devices", single.CpuFrequency == frequency && single.Gpus.Count == 1 && original.Gpus.Count == 2);
        }

        private static void CheckTopology()
        {
            List<GpuMetric> gpus = new List<GpuMetric>();
            Check("zero NVIDIA", GpuTopology.Devices(gpus).Count == 0);
            gpus.Add(new GpuMetric { Name = "NVIDIA GPU unavailable" });
            gpus.Add(new GpuMetric { Name = "Intel Integrated", StableId = "igpu" });
            gpus.Add(new GpuMetric { Name = "Virtual Display", StableId = "virtual" });
            Check("placeholders/iGPU/virtual excluded", GpuTopology.Devices(gpus).Count == 0);
            GpuMetric first = Gpu(0);
            gpus.Add(first);
            Check("single NVIDIA", GpuTopology.Devices(gpus).Count == 1);
            gpus.Add(GpuTopology.MissingSensors(Gpu(1), "temperature unavailable"));
            Check("sensor failure keeps dual", GpuTopology.Devices(gpus).Count == 2);
            gpus.Add(first);
            Check("same identity deduplicated", GpuTopology.Devices(gpus).Count == 2);
            gpus.Insert(0, Gpu(2));
            List<GpuMetric> sorted = GpuTopology.Devices(gpus);
            Check("more than two sorted", sorted.Count == 3 && sorted[0].DisplayIndex == 0 && sorted[1].DisplayIndex == 1);
        }

        private static void CheckQuotas()
        {
            DateTime now = new DateTime(2026, 9, 8, 12, 0, 0);
            QuotaHistory history = new QuotaHistory();
            ProviderQuotaMetric absent = new ProviderQuotaMetric("codex", "Codex");
            ProviderQuotaMetric fresh = history.Observe(Quota("codex", 91, now), now);
            Check("remaining not used", fresh.RemainingPercent == 9 && fresh.State == QuotaConnectionState.Connected);
            ProviderQuotaMetric stale = history.Observe(absent, now.AddSeconds(10));
            Check("failure retains provider and value", stale.HasConnected && stale.RemainingPercent == 9 && stale.State == QuotaConnectionState.Stale);
            ProviderQuotaMetric expired = history.Observe(absent, now.AddMinutes(5));
            Check("five minute hard TTL", expired.HasConnected && !expired.RemainingPercent.HasValue && expired.State == QuotaConnectionState.Expired);
            Check("cache reread cannot extend TTL", !history.Observe(Quota("codex", 91, now), now.AddMinutes(6)).RemainingPercent.HasValue);
            Check("old cache cannot establish new connection", !new QuotaHistory().Observe(Quota("codex", 91, now), now.AddMinutes(6)).HasConnected);
            Check("fresh recovery", history.Observe(Quota("codex", 0, now.AddMinutes(7)), now.AddMinutes(7)).RemainingPercent == 100);
            ProviderQuotaMetric invalid = Quota("codex", double.NaN, now);
            Check("invalid numbers not connected", !new QuotaHistory().Observe(invalid, now).HasConnected);
            invalid = Quota("codex", 10, now.AddHours(1));
            Check("future timestamp rejected", !new QuotaHistory().Observe(invalid, now).HasConnected);
            invalid = Quota("codex", 10, now);
            invalid.Primary.ResetsAtLocal = now;
            Check("reset boundary invalidates old percent", !new QuotaHistory().Observe(invalid, now).RemainingPercent.HasValue);
            Check("installed client is not quota", !new QuotaHistory().Observe(KimiQuotaCollector.DiscoverReadOnlySource(), DateTime.Now).HasConnected);
            QuotaRotation rotation = new QuotaRotation();
            List<ProviderQuotaMetric> sources = new List<ProviderQuotaMetric>();
            Check("no source -> unconnected", rotation.Select(sources, TimeSpan.Zero) == null);
            sources.Add(fresh);
            Check("one source fixed", rotation.Select(sources, TimeSpan.Zero).Name == "Codex" && rotation.Select(sources, TimeSpan.FromSeconds(8)).Name == "Codex");
            ProviderQuotaMetric kimi = new QuotaHistory().Observe(Quota("kimi", 25, now), now);
            sources.Add(kimi);
            rotation = new QuotaRotation();
            Check("dual starts Codex", rotation.Select(sources, TimeSpan.Zero).Name == "Codex");
            Check("before five seconds stable", rotation.Select(sources, TimeSpan.FromSeconds(4.99)).Name == "Codex");
            Check("five seconds atomic switch", rotation.Select(sources, TimeSpan.FromSeconds(5)).Name == "Kimi");
            rotation.SetHovered(true, TimeSpan.FromSeconds(6));
            Check("hover pauses", rotation.Select(sources, TimeSpan.FromSeconds(100)).Name == "Kimi");
            rotation.SetHovered(false, TimeSpan.FromSeconds(100));
            Check("leave preserves remaining interval", rotation.Select(sources, TimeSpan.FromSeconds(103)).Name == "Kimi");
            Check("rotation resumes", rotation.Select(sources, TimeSpan.FromSeconds(104)).Name == "Codex");
            sources[0] = expired;
            Check("expired source keeps slot", rotation.Select(sources, TimeSpan.FromSeconds(105)).Name == "Codex");
            sources.RemoveAt(0);
            Check("Kimi alone fixed", rotation.Select(sources, TimeSpan.FromSeconds(106)).Name == "Kimi");
        }

        private static void CheckActiveQuotaPolling()
        {
            AppSettings settings = AppSettings.Load(string.Empty);
            Check("separate active and local cadence", settings.CodexRefreshSeconds == 60 && settings.CodexLocalRefreshSeconds == 1);
            DateTime now = new DateTime(2026, 9, 9, 10, 0, 0);
            CodexMetric live = new CodexMetric { UpdatedAtLocal = now, RefreshIntervalSeconds = 60,
                Primary = new QuotaWindowMetric { UsedPercent = 46, WindowDurationMinutes = 10080, ResetsAtLocal = now.AddDays(6) } };
            QuotaHistory history = new QuotaHistory();
            ProviderQuotaMetric sample = ProviderQuotaMetric.FromCodex(live);
            history.Observe(sample, now);
            Check("minute polling stays fresh between reads", history.Observe(sample, now.AddSeconds(60)).State == QuotaConnectionState.Connected);
            Check("missed poll becomes stale after grace", history.Observe(sample, now.AddSeconds(76)).State == QuotaConnectionState.Stale);
            Check("live failures retain five-minute hard expiry", history.Observe(sample, now.AddSeconds(300)).State == QuotaConnectionState.Expired);
            // A successful unchanged value is still a newly verified snapshot.
            live.UpdatedAtLocal = now.AddSeconds(301);
            Check("unchanged live percent recovers", history.Observe(ProviderQuotaMetric.FromCodex(live), now.AddSeconds(301)).RemainingPercent == 54);

            CodexMetric local = new CodexMetric { UpdatedAtLocal = now.AddSeconds(302), Primary = live.Primary, Status = "local" };
            Check("newer matching local event updates immediately", CodexQuotaSourceSelector.Select(live, local, now.AddSeconds(302)) == local);
            live.UpdatedAtLocal = now.AddSeconds(303);
            Check("fresh live query beats older logs", CodexQuotaSourceSelector.Select(live, local, now.AddSeconds(303)) == live);
            live.UpdatedAtLocal = now.AddMinutes(-10);
            Check("stale live snapshot cannot mask fresh logs", CodexQuotaSourceSelector.Select(live, local, now.AddSeconds(303)) == local);
            CodexMetric failed = new CodexMetric { Status = "CLI not found" };
            Check("fallback preserves active error", CodexQuotaSourceSelector.Select(failed, local, now.AddSeconds(303)).Status.Contains("CLI not found"));

            // Explicit invalid overrides must fail, not silently launch another CLI.
            typeof(AppSettings).GetProperty("CodexCommand").SetValue(settings, "missing-cli-for-layout-test.exe", null);
            bool rejected = false;
            try { CodexExecutableLocator.Resolve(settings); }
            catch (FileNotFoundException) { rejected = true; }
            Check("missing explicit native CLI is reported", rejected);
        }

        private static void CheckRendering()
        {
            string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "layouts");
            Directory.CreateDirectory(directory);
            AppSettings settings = AppSettings.Load(string.Empty);
            typeof(AppSettings).GetProperty("CpuAlias").SetValue(settings, "R9-9950X3D", null);
            foreach (int dpi in new int[] { 96, 120, 144, 192 })
            foreach (int gpuCount in new int[] { 0, 1, 2, 3 })
            foreach (bool dark in new bool[] { true, false })
            {
                float scale = dpi / 96F;
                Size size = new Size((int)Math.Round(TaskbarRenderer.WindowWidth(520 * scale, gpuCount >= 2)), (int)(40 * scale));
                using (Bitmap bitmap = new Bitmap(size.Width, size.Height))
                {
                    bitmap.SetResolution(dpi, dpi);
                    using (Graphics g = Graphics.FromImage(bitmap))
                    using (Font font = new Font(settings.FontFamily, settings.FontSizePoints))
                    using (Font cpuFont = new Font("Consolas", settings.FontSizePoints))
                    {
                        TelemetrySnapshot small = Fixture(gpuCount, false);
                        TelemetrySnapshot large = Fixture(gpuCount, true);
                        List<RenderSlot> a = TaskbarRenderer.BuildSlots(g, size, font, cpuFont, settings, small, small.Quotas[0]);
                        List<RenderSlot> b = TaskbarRenderer.BuildSlots(g, size, font, cpuFont, settings, large, large.Quotas[1]);
                        bool stable = a.Count == b.Count;
                        for (int i = 0; stable && i < a.Count; i++) stable &= a[i].Id == b[i].Id && a[i].Bounds == b[i].Bounds;
                        Check("fixed slots " + gpuCount + " GPU / DPI " + dpi, stable);
                        Check("text fits " + gpuCount + " GPU / DPI " + dpi, Fits(g, b, font, cpuFont, size));
                        float[] edges = TaskbarRenderer.ColumnEdges(size.Width, gpuCount >= 2);
                        Check("column contract", Math.Abs(edges[1] / scale - (gpuCount >= 2 ? 200 : 140)) < 0.01 &&
                            Math.Abs(edges[2] / scale - (gpuCount >= 2 ? 408 : 356)) < 0.01 &&
                            Math.Abs(edges[3] / scale - (gpuCount >= 2 ? 520 : 476)) < 0.01);
                        foreach (RenderSlot slot in b)
                        {
                            bool hardware = slot.Id.StartsWith("gpu", StringComparison.Ordinal) ||
                                (gpuCount < 2 && slot.Id.StartsWith("cpu", StringComparison.Ordinal));
                            if (hardware)
                                Check("hardware separator clearance " + slot.Id,
                                    slot.Bounds.Left >= edges[1] + 8 * scale - 0.1F &&
                                    slot.Bounds.Right <= edges[2] - 7 * scale + 0.1F);
                        }
                        Check("CPU label matches layout", gpuCount >= 2 ?
                            Slot(b, "cpu.label").Text.TrimEnd() == "R9-9950X3D" && Slot(b, "cpu.label").CpuFont :
                            Slot(b, "cpu.label").Text == "CPU" && !Slot(b, "cpu.label").CpuFont &&
                            Slot(b, "cpu.label").Alignment == StringAlignment.Near);
                        if (gpuCount < 2)
                        {
                            Check("CPU/GPU labels left aligned", Slot(b, "cpu.label").Bounds.X == Slot(b, "gpu0.label").Bounds.X &&
                                Slot(b, "gpu0.label").Alignment == StringAlignment.Near && Slot(b, "gpu0.label").Text == "GPU");
                            Check("GHz/GB unit slots align", Slot(b, "cpu.frequency.unit").Bounds.X == Slot(b, "gpu0.memory.unit").Bounds.X &&
                                Slot(b, "cpu.frequency.unit").Bounds.Width == Slot(b, "gpu0.memory.unit").Bounds.Width &&
                                Slot(b, "cpu.frequency.unit").Text == "GHz" &&
                                Slot(b, "cpu.frequency.unit").Alignment == StringAlignment.Near &&
                                Slot(b, "gpu0.memory.unit").Alignment == StringAlignment.Near);
                            Check("frequency/VRAM numeric slots align", Slot(b, "cpu.frequency.value").Bounds.X == Slot(b, "gpu0.memory.value").Bounds.X);
                            float nameGap = Slot(b, "cpu.frequency.value").Bounds.Left - Slot(b, "cpu.label").Bounds.Right;
                            Check("single name/value gap compact", nameGap >= -0.1F && nameGap <= 2 * scale + 0.1F);
                        }
                        if (gpuCount >= 2)
                        {
                            Check("dual usable hardware width preserved", Math.Abs((edges[2] - edges[1]) / scale - 15 - 193) < 0.01);
                            Check("CPU percent-temperature gap tightened", Math.Abs((Slot(b, "cpu.temperature").Bounds.Left -
                                Slot(b, "cpu.percent").Bounds.Right) / scale - 4) < 0.01);
                            Check("GPU label separator gutter", Slot(b, "gpu0.label").Bounds.X - edges[1] >= 8 * scale - 0.1F &&
                                Slot(b, "gpu0.label").Bounds.X == Slot(b, "gpu1.label").Bounds.X);
                            Check("dual GPU labels unchanged", Slot(b, "gpu0.label").Text == settings.Gpu0Alias && Slot(b, "gpu1.label").Text == settings.Gpu1Alias);
                        }
                        if (gpuCount == 1)
                            Check("CPU/GPU metrics align", Slot(b, "cpu.percent").Bounds.X == Slot(b, "gpu0.percent").Bounds.X &&
                                Slot(b, "cpu.temperature").Bounds.X == Slot(b, "gpu0.temperature").Bounds.X);
                        g.Clear(dark ? Color.FromArgb(31, 34, 39) : Color.FromArgb(243, 243, 243));
                        TaskbarRenderer.Draw(g, size, font, dark ? Color.White : Color.FromArgb(26, 26, 26), settings, large, large.Quotas[1]);
                    }
                    bitmap.Save(Path.Combine(directory, string.Format(CultureInfo.InvariantCulture, "gpu{0}-{1}-{2}dpi.png", gpuCount, dark ? "dark" : "light", dpi)), ImageFormat.Png);
                }
            }
        }

        private static RenderSlot Slot(List<RenderSlot> slots, string id) { return slots.Find(delegate(RenderSlot s) { return s.Id == id; }); }
        private static void CreateOverview()
        {
            string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "layouts");
            AppSettings settings = AppSettings.Load(string.Empty);
            typeof(AppSettings).GetProperty("CpuAlias").SetValue(settings, "i9-10980XE", null);
            using (Bitmap canvas = new Bitmap(1120, 400))
            using (Graphics page = Graphics.FromImage(canvas))
            using (Font title = new Font("Microsoft YaHei UI", 16))
            using (Font caption = new Font("Microsoft YaHei UI", 10))
            {
                page.Clear(Color.FromArgb(245, 247, 250));
                page.DrawString("TaskbarTelemetry · 单 / 双 NVIDIA 自适应布局", title, Brushes.Black, 26, 18);
                for (int row = 0; row < 4; row++)
                {
                    bool dual = row % 2 == 0;
                    bool dark = row < 2;
                    TelemetrySnapshot sample = Fixture(dual ? 2 : 1, false);
                    sample.System.CpuUsagePercent = 24;
                    sample.System.MemoryUsagePercent = 54;
                    sample.CpuTemperature.Celsius = 44;
                    sample.Gpus[0].MemoryUsedBytes = 8UL * 1024 * 1024 * 1024;
                    sample.Gpus[0].UsagePercent = 37;
                    if (dual) { sample.Gpus[1].MemoryUsedBytes = 3UL * 1024 * 1024 * 1024; sample.Gpus[1].UsagePercent = 12; }
                    ProviderQuotaMetric quota = new QuotaHistory().Observe(Quota(dual ? "codex" : "kimi", dual ? 8 : 36, DateTime.Now), DateTime.Now);
                    page.DrawString((dual ? "双卡" : "单卡") + (dark ? " · 深色" : " · 浅色"), caption, Brushes.DimGray, 26, 79 + row * 68);
                    using (Bitmap strip = new Bitmap((int)Math.Round(TaskbarRenderer.WindowWidth(780, dual)), 60))
                    {
                        strip.SetResolution(144, 144);
                        using (Graphics g = Graphics.FromImage(strip))
                        using (Font font = new Font(settings.FontFamily, settings.FontSizePoints))
                        {
                            g.Clear(dark ? Color.FromArgb(31, 34, 39) : Color.FromArgb(238, 238, 238));
                            TaskbarRenderer.Draw(g, strip.Size, font, dark ? Color.White : Color.FromArgb(26, 26, 26), settings, sample, quota);
                        }
                        page.DrawImageUnscaled(strip, 160 + 780 - strip.Width, 60 + row * 68);
                        strip.Save(Path.Combine(directory, (dual ? "dual" : "single") + "-" + (dark ? "dark" : "light") + ".png"));
                    }
                }
                page.DrawString("生产绘制代码 · 演示数据；Kimi 仅为轮换示例，尚未通过真实账户核对。", caption, Brushes.DimGray, 26, 346);
                page.DrawString("双卡 520（200/208/112） · 单卡 476（140/216/120）；Mb = Mbps。", caption, Brushes.DimGray, 26, 371);
                canvas.Save(Path.Combine(directory, "layout-overview.png"));
            }
        }
        private static void CheckQuotaRendering()
        {
            AppSettings settings = AppSettings.Load(string.Empty);
            DateTime now = DateTime.Now;
            QuotaHistory history = new QuotaHistory();
            history.Observe(Quota("codex", 91, now), now);
            ProviderQuotaMetric[] choices = { null,
                new QuotaHistory().Observe(Quota("codex", 91, now), now),
                new QuotaHistory().Observe(Quota("kimi", 0, now), now),
                history.Observe(new ProviderQuotaMetric("codex", "Codex"), now.AddMinutes(2)),
                history.Observe(new ProviderQuotaMetric("codex", "Codex"), now.AddMinutes(6)) };
            foreach (int gpuCount in new int[] { 0, 1, 2 })
            using (Bitmap bitmap = BitmapAtDpi((int)Math.Round(TaskbarRenderer.WindowWidth(520, gpuCount >= 2)), 40, 96))
            using (Graphics g = Graphics.FromImage(bitmap))
            using (Font font = new Font(settings.FontFamily, settings.FontSizePoints))
            using (Font cpuFont = new Font("Consolas", settings.FontSizePoints))
            {
                TelemetrySnapshot snapshot = Fixture(gpuCount, false);
                if (gpuCount > 0)
                {
                    snapshot.Gpus[0].TemperatureCelsius = null;
                    snapshot.Gpus[0].MemoryUsedBytes = ulong.MaxValue;
                }
                snapshot.System.UploadBytesPerSecond = double.MaxValue;
                snapshot.System.DownloadBytesPerSecond = double.NaN;
                snapshot.CpuFrequency = new CpuFrequencyMetric();
                RectangleF? label = null;
                RectangleF? percent = null;
                foreach (ProviderQuotaMetric choice in choices)
                {
                    List<RenderSlot> slots = TaskbarRenderer.BuildSlots(g, bitmap.Size, font, cpuFont, settings, snapshot, choice);
                    Check("missing sensors/source/stale fits", Fits(g, slots, font, cpuFont, bitmap.Size));
                    RenderSlot labelSlot = Slot(slots, "quota.label"), percentSlot = Slot(slots, "quota.percent");
                    if (label.HasValue) Check("quota names/numbers remain in place", labelSlot.Bounds == label.Value && percentSlot.Bounds == percent.Value);
                    label = labelSlot.Bounds; percent = percentSlot.Bounds;
                    if (choice == null) Check("disconnected visible text", labelSlot.Text == "未连接" && percentSlot.Text == "");
                    if (choice != null && choice.State == QuotaConnectionState.Expired) Check("expired visible text", percentSlot.Text == "--%");
                }
            }
            foreach (int gpuCount in new int[] { 0, 1, 2 })
            using (Bitmap bitmap = BitmapAtDpi((int)Math.Round(TaskbarRenderer.WindowWidth(520, gpuCount >= 2)), 60, 144))
            using (Graphics g = Graphics.FromImage(bitmap))
            using (Font font = new Font(settings.FontFamily, settings.FontSizePoints))
            using (Font cpuFont = new Font("Consolas", settings.FontSizePoints))
            {
                TelemetrySnapshot snapshot = Fixture(gpuCount, true);
                Check("legacy physical width at 150% DPI / " + gpuCount + " GPU", Fits(g,
                    TaskbarRenderer.BuildSlots(g, bitmap.Size, font, cpuFont, settings, snapshot, snapshot.Quotas[0]), font, cpuFont, bitmap.Size));
            }
        }
        private static Bitmap BitmapAtDpi(int width, int height, int dpi)
        {
            Bitmap bitmap = new Bitmap(width, height);
            bitmap.SetResolution(dpi, dpi);
            return bitmap;
        }
        private static bool Fits(Graphics g, List<RenderSlot> slots, Font font, Font cpuFont, Size size)
        {
            bool ok = true;
            using (StringFormat format = new StringFormat(StringFormat.GenericTypographic))
            {
                format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                foreach (RenderSlot slot in slots)
                {
                    SizeF measured = g.MeasureString(slot.Text, slot.CpuFont ? cpuFont : font, 1000, format);
                    if (measured.Width > slot.Bounds.Width + 0.2 || measured.Height > slot.Bounds.Height + 0.2 ||
                        slot.Bounds.Left < 0 || slot.Bounds.Right > size.Width + 0.2)
                    { Console.WriteLine("CLIP {0}: measured={1} bounds={2}", slot.Id, measured, slot.Bounds); ok = false; }
                    foreach (RenderSlot other in slots)
                    {
                        if (slot == other || slot.Text.Trim().Length == 0 || other.Text.Trim().Length == 0) continue;
                        RectangleF intersection = RectangleF.Intersect(slot.Bounds, other.Bounds);
                        if (intersection.Width > 0.2 && intersection.Height > 0.2)
                        { Console.WriteLine("OVERLAP {0} / {1}", slot.Id, other.Id); ok = false; }
                    }
                }
            }
            return ok;
        }

        internal static TelemetrySnapshot Fixture(int gpuCount, bool large)
        {
            TelemetrySnapshot s = new TelemetrySnapshot();
            s.System.UploadBytesPerSecond = (large ? 1234 : 0.25) * 1024 * 1024;
            s.System.DownloadBytesPerSecond = (large ? 999.9 : 3.42) * 1024 * 1024;
            s.System.CpuUsagePercent = large ? 100 : 9;
            s.System.MemoryUsagePercent = large ? 100 : 9;
            s.CpuTemperature.Celsius = large ? 100 : 49;
            s.CpuFrequency.Megahertz = large ? 9900 : 3800;
            for (int i = 0; i < gpuCount; i++)
            {
                GpuMetric gpu = Gpu(i);
                gpu.UsagePercent = large ? 100 : 9;
                gpu.TemperatureCelsius = large ? 100 : 49;
                gpu.MemoryUsedBytes = (large ? 999UL : 9UL) * 1024 * 1024 * 1024;
                s.Gpus.Add(gpu);
            }
            s.Quotas.Add(new QuotaHistory().Observe(Quota("codex", large ? 0 : 91, DateTime.Now), DateTime.Now));
            s.Quotas.Add(new QuotaHistory().Observe(Quota("kimi", large ? 0 : 91, DateTime.Now), DateTime.Now));
            return s;
        }
        internal static GpuMetric Gpu(int index)
        {
            return new GpuMetric { IsNvidiaDevice = true, StableId = "TEST-GPU-" + index, DisplayIndex = index,
                Name = "NVIDIA demonstration device", PciBusId = "TEST-PCI-" + index, Status = "fixture" };
        }
        internal static ProviderQuotaMetric Quota(string id, double used, DateTime updated)
        {
            ProviderQuotaMetric q = new ProviderQuotaMetric(id, id == "kimi" ? "Kimi" : "Codex");
            q.Primary = new QuotaWindowMetric { UsedPercent = used, WindowDurationMinutes = 300, ResetsAtLocal = updated.AddHours(2) };
            q.Windows.Add(q.Primary);
            q.UpdatedAtLocal = updated;
            q.Status = "fixture (not an account measurement)";
            return q;
        }
        private static void Check(string name, bool passed)
        {
            assertions++;
            if (!passed) { failures++; Console.WriteLine("ADAPTIVE FAIL " + name); }
        }
    }
}
