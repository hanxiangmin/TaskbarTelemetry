using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace TaskbarTelemetry
{
    internal static class StoreScreenshotProgram
    {
        private const int CanvasWidth = 1366;
        private const int CanvasHeight = 768;
        private const int TaskbarHeight = 52;

        [STAThread]
        public static int Main(string[] args)
        {
            if (args == null || args.Length != 2)
                return 2;

            string outputDirectory = Path.GetFullPath(args[0]);
            string settingsPath = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(outputDirectory);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            AppSettings settings = AppSettings.Load(settingsPath);
            using (TelemetryEngine engine = new TelemetryEngine(settings))
            {
                engine.SetPreviewSnapshot(CreateDemonstrationSnapshot());
                using (TaskbarForm form = new TaskbarForm(settings, engine, settingsPath, true))
                using (Bitmap overlay = RenderControl(form))
                using (Bitmap screenshot = CreateDesktopCanvas())
                {
                    using (Graphics graphics = Graphics.FromImage(screenshot))
                    {
                        int taskbarTop = CanvasHeight - TaskbarHeight;
                        int x = CanvasWidth - overlay.Width - 184;
                        int y = taskbarTop + (TaskbarHeight - overlay.Height) / 2;
                        graphics.DrawImageUnscaled(overlay, x, y);
                        DrawNotificationArea(graphics, x + overlay.Width, taskbarTop);
                    }
                    screenshot.Save(
                        Path.Combine(outputDirectory, "Screenshot-01-Taskbar.png"),
                        ImageFormat.Png);
                }

                using (NotificationSettingsForm form = new NotificationSettingsForm(
                    PackageRuntime.GetPrivacyPolicyPath(), settingsPath, engine.DisableNotifications))
                using (Bitmap dialog = RenderDialog(form))
                using (Bitmap screenshot = CreateDesktopCanvas())
                {
                    using (Graphics graphics = Graphics.FromImage(screenshot))
                    {
                        int x = (CanvasWidth - dialog.Width) / 2;
                        int y = Math.Max(24, (CanvasHeight - TaskbarHeight - dialog.Height) / 2);
                        graphics.DrawImageUnscaled(dialog, x, y);
                    }
                    screenshot.Save(
                        Path.Combine(outputDirectory, "Screenshot-02-Privacy.png"),
                        ImageFormat.Png);
                }
            }
            return 0;
        }

        private static Bitmap RenderControl(Control control)
        {
            control.CreateControl();
            foreach (Control child in control.Controls)
                child.CreateControl();
            Bitmap bitmap = new Bitmap(
                Math.Max(1, control.Width),
                Math.Max(1, control.Height),
                PixelFormat.Format32bppArgb);
            control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            return bitmap;
        }

        private static Bitmap RenderDialog(Form form)
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-32000, -32000);
            form.ShowInTaskbar = false;
            form.Show();
            Application.DoEvents();
            try
            {
                return RenderControl(form);
            }
            finally
            {
                form.Hide();
            }
        }

        private static Bitmap CreateDesktopCanvas()
        {
            Bitmap bitmap = new Bitmap(CanvasWidth, CanvasHeight, PixelFormat.Format24bppRgb);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                Rectangle desktop = new Rectangle(0, 0, CanvasWidth, CanvasHeight - TaskbarHeight);
                using (LinearGradientBrush wallpaper = new LinearGradientBrush(
                    desktop,
                    Color.FromArgb(224, 234, 246),
                    Color.FromArgb(245, 247, 251),
                    LinearGradientMode.ForwardDiagonal))
                {
                    graphics.FillRectangle(wallpaper, desktop);
                }
                using (SolidBrush taskbar = new SolidBrush(Color.FromArgb(31, 34, 39)))
                    graphics.FillRectangle(taskbar, 0, CanvasHeight - TaskbarHeight, CanvasWidth, TaskbarHeight);
            }
            return bitmap;
        }

        private static void DrawNotificationArea(Graphics graphics, int left, int taskbarTop)
        {
            using (Pen separator = new Pen(Color.FromArgb(80, 255, 255, 255)))
                graphics.DrawLine(separator, left + 4, taskbarTop + 8, left + 4, CanvasHeight - 8);
            using (SolidBrush iconBrush = new SolidBrush(Color.FromArgb(215, 225, 235)))
            {
                graphics.FillEllipse(iconBrush, left + 20, taskbarTop + 20, 12, 12);
                graphics.FillEllipse(iconBrush, left + 43, taskbarTop + 20, 12, 12);
                graphics.FillEllipse(iconBrush, left + 66, taskbarTop + 20, 12, 12);
            }
            using (Font font = new Font("Segoe UI", 9.0f, FontStyle.Regular, GraphicsUnit.Point))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            {
                graphics.DrawString("12:00", font, textBrush, left + 92, taskbarTop + 7);
                graphics.DrawString("2026/8/25", font, textBrush, left + 84, taskbarTop + 26);
            }
        }

        private static TelemetrySnapshot CreateDemonstrationSnapshot()
        {
            TelemetrySnapshot snapshot = new TelemetrySnapshot();
            snapshot.CapturedAtLocal = new DateTime(2026, 8, 25, 12, 0, 0);
            snapshot.System.UploadBytesPerSecond = 0.28 * 1024.0 * 1024.0;
            snapshot.System.DownloadBytesPerSecond = 3.42 * 1024.0 * 1024.0;
            snapshot.System.CpuUsagePercent = 24.0;
            snapshot.System.MemoryUsagePercent = 46.0;
            snapshot.System.MemoryTotalBytes = 64UL * 1024UL * 1024UL * 1024UL;
            snapshot.System.MemoryUsedBytes = 29UL * 1024UL * 1024UL * 1024UL;
            snapshot.System.NetworkName = "Ethernet";
            snapshot.CpuTemperature.Celsius = 44.0;
            snapshot.CpuTemperature.SensorName = "CPU Package";
            snapshot.CpuTemperature.Status = "Demo value for Store screenshot";

            snapshot.Gpus.Add(CreateGpu(0, 3, 27.0, 44.0));
            snapshot.Gpus.Add(CreateGpu(1, 2, 16.0, 51.0));
            snapshot.Codex.Primary = new QuotaWindowMetric
            {
                UsedPercent = 42.0,
                WindowDurationMinutes = 300,
                ResetsAtLocal = new DateTime(2026, 8, 25, 17, 0, 0)
            };
            snapshot.Codex.UpdatedAtLocal = snapshot.CapturedAtLocal;
            snapshot.Quotas.Add(new QuotaHistory().Observe(ProviderQuotaMetric.FromCodex(snapshot.Codex), snapshot.CapturedAtLocal));
            return snapshot;
        }

        private static GpuMetric CreateGpu(
            int index, ulong usedGiB, double usagePercent, double temperature)
        {
            GpuMetric gpu = new GpuMetric();
            gpu.DisplayIndex = index;
            gpu.IsNvidiaDevice = true;
            gpu.StableId = "DEMO-GPU-" + index;
            gpu.Name = "Compatible NVIDIA GPU";
            gpu.MemoryUsedBytes = usedGiB * 1024UL * 1024UL * 1024UL;
            gpu.MemoryTotalBytes = 24UL * 1024UL * 1024UL * 1024UL;
            gpu.UsagePercent = usagePercent;
            gpu.TemperatureCelsius = temperature;
            return gpu;
        }
    }
}
