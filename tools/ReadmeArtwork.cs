using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;

namespace TaskbarTelemetry
{
    // Documentation artwork, not a runtime test or a screenshot of the desktop.
    internal static class ReadmeArtwork
    {
        private static readonly Color Ink = Color.FromArgb(235, 242, 251);
        private static readonly Color Muted = Color.FromArgb(143, 162, 186);
        private static readonly Color Cyan = Color.FromArgb(79, 225, 214);
        private static readonly Color Violet = Color.FromArgb(180, 157, 255);

        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length < 2 || args.Length > 4) return 2;
            // Optional physical-DPI strips support local spacing reviews. They
            // still only draw demonstration values, never run the diagnostic probe.
            float dpi = args.Length >= 3 ? float.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 192;
            bool stress = args.Length == 4 && args[3] == "--stress";
            string output = Path.GetFullPath(args[0]);
            Directory.CreateDirectory(output);
            AppSettings settings = AppSettings.Load(args[1]);
            using (Bitmap dual = Strip(settings, true, false, dpi, stress))
            using (Bitmap single = Strip(settings, false, false, dpi, stress))
            using (Bitmap dualLight = Strip(settings, true, true, dpi, stress))
            using (Bitmap singleLight = Strip(settings, false, true, dpi, stress))
            {
                dual.Save(Path.Combine(output, "dual-dark.png"), ImageFormat.Png);
                single.Save(Path.Combine(output, "single-dark.png"), ImageFormat.Png);
                dualLight.Save(Path.Combine(output, "dual-light.png"), ImageFormat.Png);
                singleLight.Save(Path.Combine(output, "single-light.png"), ImageFormat.Png);
                if (args.Length == 2)
                {
                    using (Bitmap hero = Hero(dual, single)) hero.Save(Path.Combine(output, "hero.png"), ImageFormat.Png);
                    using (Bitmap overview = Overview(dual, single)) overview.Save(Path.Combine(output, "layout-overview.png"), ImageFormat.Png);
                }
            }
            Console.WriteLine("Generated {0} documentation PNGs with production TaskbarRenderer and synthetic values. No runtime tests performed.", args.Length == 2 ? 6 : 4);
            return 0;
        }

        private static Bitmap Strip(AppSettings settings, bool dual, bool light, float dpi, bool stress)
        {
            int width = (int)Math.Round(TaskbarRenderer.WindowWidth(settings.TaskbarWidth, dual) * (settings.ScaleWidthWithDpi ? dpi / 96 : 1));
            Bitmap bitmap = new Bitmap(width, (int)Math.Round(40 * dpi / 96), PixelFormat.Format32bppArgb);
            bitmap.SetResolution(dpi, dpi);
            TelemetrySnapshot snapshot = new TelemetrySnapshot();
            snapshot.CapturedAtLocal = new DateTime(2026, 1, 1, 12, 0, 0);
            snapshot.System.UploadBytesPerSecond = .16 * 125000;
            snapshot.System.DownloadBytesPerSecond = 12.16 * 125000;
            snapshot.System.CpuUsagePercent = 24;
            snapshot.System.MemoryUsagePercent = 46;
            snapshot.CpuTemperature.Celsius = 52;
            snapshot.CpuFrequency.Megahertz = 3900;
            for (int i = 0; i < (dual ? 2 : 1); i++)
                snapshot.Gpus.Add(new GpuMetric { IsNvidiaDevice = true, StableId = "DEMO-GPU-" + i,
                    DisplayIndex = i, Name = "Demonstration NVIDIA GPU", MemoryUsedBytes = (ulong)(i == 0 ? 6 : 18) * 1024 * 1024 * 1024,
                    MemoryTotalBytes = 24UL * 1024 * 1024 * 1024, UsagePercent = i == 0 ? 37 : 94, TemperatureCelsius = i == 0 ? 48 : 67 });
            snapshot.Codex.Primary = new QuotaWindowMetric { UsedPercent = 12, WindowDurationMinutes = 300,
                ResetsAtLocal = snapshot.CapturedAtLocal.AddHours(5) };
            snapshot.Codex.UpdatedAtLocal = snapshot.CapturedAtLocal;
            if (stress)
            {
                snapshot.System.UploadBytesPerSecond = 999.9 * 125000;
                snapshot.System.DownloadBytesPerSecond = 999.9 * 125000;
                snapshot.System.CpuUsagePercent = 100;
                snapshot.System.MemoryUsagePercent = 100;
                snapshot.CpuTemperature.Celsius = 100;
                snapshot.Codex.Primary.UsedPercent = 0;
                foreach (GpuMetric gpu in snapshot.Gpus)
                {
                    gpu.MemoryUsedBytes = 999UL * 1024 * 1024 * 1024;
                    gpu.UsagePercent = 100;
                    gpu.TemperatureCelsius = 100;
                }
            }
            ProviderQuotaMetric quota = ProviderQuotaMetric.FromCodex(snapshot.Codex);
            using (Graphics g = Graphics.FromImage(bitmap))
            using (Font font = new Font(settings.FontFamily, settings.FontSizePoints, FontStyle.Regular, GraphicsUnit.Point))
            {
                g.Clear(light ? Color.FromArgb(243, 245, 248) : Color.FromArgb(29, 33, 40));
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                TaskbarRenderer.Draw(g, bitmap.Size, font, light ? Color.FromArgb(30, 39, 52) : Ink, settings, snapshot, quota);
            }
            return bitmap;
        }

        private static Bitmap Hero(Bitmap dual, Bitmap single)
        {
            Bitmap bitmap = new Bitmap(1600, 960, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                Setup(g, bitmap.Size);
                using (Pen grid = new Pen(Color.FromArgb(20, 235, 242, 251)))
                {
                    for (int x = 0; x < 1600; x += 64) g.DrawLine(grid, x, 0, x, 960);
                    for (int y = 0; y < 960; y += 64) g.DrawLine(grid, 0, y, 1600, y);
                }
                Label(g, "TASKBAR / HARDWARE + AI", 72, 46, 22, Cyan, true);
                Label(g, "TaskbarTelemetry", 66, 86, 80, Ink, true);
                Label(g, "算力与 AI 额度，一眼看见。", 72, 191, 39, Ink, true);
                Label(g, "CPU  /  GPU  /  RAM  /  NETWORK  /  CODEX", 76, 259, 23, Muted, false);
                Card(g, new Rectangle(72, 336, 1456, 222), "02  /  DUAL GPU", "两张显卡，各看各的。", dual, Cyan);
                Card(g, new Rectangle(72, 582, 1456, 222), "01  /  SINGLE GPU", "自动收窄，依然一眼看全。", single, Violet);
                Label(g, "硬件 1 秒刷新", 76, 846, 25, Ink, true);
                Label(g, "自动识别单 / 双卡", 330, 846, 25, Ink, true);
                Label(g, "任务栏子窗口", 718, 846, 25, Ink, true);
                Label(g, "MIT 开源", 1110, 846, 25, Ink, true);
                Label(g, "WINDOWS x64 / MAIN 源码预览   ·   生产界面绘制 / 演示数值   ·   Kimi 接入待验证", 76, 908, 18, Muted, false);
            }
            return bitmap;
        }

        private static Bitmap Overview(Bitmap dual, Bitmap single)
        {
            Bitmap bitmap = new Bitmap(1200, 390, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                Setup(g, bitmap.Size);
                Label(g, "双 GPU / 520", 48, 23, 22, Cyan, true);
                Place(g, dual, 72, 68);
                Label(g, "单 GPU / 476", 48, 191, 22, Violet, true);
                Place(g, single, 176, 236);
                Label(g, "生产绘制代码 · 固定演示数值 · 2× 展示", 48, 350, 16, Muted, false);
            }
            return bitmap;
        }

        private static void Setup(Graphics g, Size size)
        {
            using (LinearGradientBrush bg = new LinearGradientBrush(new Rectangle(Point.Empty, size),
                Color.FromArgb(9, 18, 31), Color.FromArgb(24, 22, 48), 25F))
                g.FillRectangle(bg, new Rectangle(Point.Empty, size));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        }

        private static void Card(Graphics g, Rectangle r, string tag, string description, Bitmap strip, Color accent)
        {
            using (Brush fill = new SolidBrush(Color.FromArgb(17, 27, 43)))
            using (Pen border = new Pen(Color.FromArgb(59, 75, 100)))
            using (Brush bar = new SolidBrush(accent))
            {
                g.FillRectangle(fill, r); g.DrawRectangle(border, r);
                g.FillRectangle(bar, r.X, r.Y, 5, r.Height);
            }
            Label(g, tag, r.X + 28, r.Y + 22, 22, accent, true);
            Label(g, description, r.X + 28, r.Y + 66, 22, Ink, false);
            int imageX = r.Right - 36 - strip.Width;
            Place(g, strip, imageX, r.Y + 103);
            Label(g, "任务栏 / 两行看全", r.X + 34, r.Y + 142, 17, Muted, false);
        }

        private static void Place(Graphics g, Bitmap bitmap, int x, int y)
        {
            // Explicit pixel units retain all pixels when the strip has 192-DPI metadata.
            g.DrawImage(bitmap, new Rectangle(x, y, bitmap.Width, bitmap.Height),
                0, 0, bitmap.Width, bitmap.Height, GraphicsUnit.Pixel);
        }

        private static void Label(Graphics g, string text, float x, float y, float pixels, Color color, bool bold)
        {
            using (Font font = new Font("Microsoft YaHei UI", pixels, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel))
            using (Brush brush = new SolidBrush(color))
                g.DrawString(text, font, brush, x, y, StringFormat.GenericTypographic);
        }
    }
}
