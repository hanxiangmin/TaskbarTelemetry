using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace TaskbarTelemetry
{
    internal static class Program
    {
        private const string SingleInstanceMutexName = @"Local\TaskbarTelemetry.SingleInstance";

        [STAThread]
        private static void Main(string[] args)
        {
            NativeMethods.TryEnablePerMonitorDpiAwareness();
            bool previewLayouts = Array.Exists(args, delegate(string arg)
            {
                return string.Equals(arg, "--preview-layouts", StringComparison.OrdinalIgnoreCase);
            });

            bool ownsMutex;
            using (Mutex mutex = new Mutex(true, SingleInstanceMutexName, out ownsMutex))
            {
                if (!ownsMutex)
                    return;

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                bool isPackaged = PackageRuntime.IsPackaged();
                string settingsPath = PackageRuntime.ResolveSettingsPath(isPackaged);
                TelemetryEngine engine = null;
                try
                {
                    AppSettings settings = AppSettings.Load(settingsPath);
                    // A preview is a read-only display session, never a notification test.
                    if (previewLayouts || !NotificationConsent.IsGranted())
                        settings.DisableNotifications();
                    engine = new TelemetryEngine(settings);
                    engine.Start();

                    using (TaskbarForm form = new TaskbarForm(settings, engine, settingsPath, isPackaged))
                    {
                        if (previewLayouts) form.EnableLayoutPreview();
                        Application.Run(form);
                    }
                }
                catch (Exception exception)
                {
                    MessageBox.Show(
                        "TaskbarTelemetry 无法启动：\r\n" + exception.Message,
                        "TaskbarTelemetry",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
                finally
                {
                    if (engine != null)
                        engine.Dispose();

                    try
                    {
                        mutex.ReleaseMutex();
                    }
                    catch (ApplicationException)
                    {
                    }
                }
            }
        }
    }
}
