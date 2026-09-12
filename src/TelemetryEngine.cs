using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace TaskbarTelemetry
{
    internal sealed class TelemetryEngine : IDisposable
    {
        private readonly AppSettings settings;
        private readonly SystemMetricsCollector systemCollector;
        private readonly NvmlCollector nvmlCollector;
        private readonly CpuTemperatureCollector cpuTemperatureCollector;
        private readonly CodexQuotaCollector codexCollector;
        private readonly CodexSessionQuotaCollector codexSessionCollector;
        private readonly ServerChanNotificationService notificationService;
        private readonly KimiQuotaCollector kimiCollector;
        private readonly QuotaHistory codexHistory = new QuotaHistory();
        private readonly QuotaHistory kimiHistory = new QuotaHistory();
        private readonly string codexMode;
        private readonly object stateLock;
        private readonly ManualResetEvent stopEvent;

        private TelemetrySnapshot latest;
        private Thread workerThread;
        private bool started;
        private bool disposed;

        public TelemetryEngine(AppSettings settings)
        {
            if (settings == null)
                throw new ArgumentNullException("settings");

            this.settings = settings;
            stateLock = new object();
            stopEvent = new ManualResetEvent(false);
            latest = new TelemetrySnapshot();
            systemCollector = new SystemMetricsCollector(settings);
            nvmlCollector = new NvmlCollector();
            cpuTemperatureCollector = new CpuTemperatureCollector(settings);
            codexMode = NormalizeCodexMode(settings.CodexMode);
            codexSessionCollector = codexMode == "local" || codexMode == "auto"
                ? new CodexSessionQuotaCollector(settings)
                : null;
            codexCollector = codexMode == "appserver" || codexMode == "auto"
                ? new CodexQuotaCollector(settings)
                : null;
            notificationService = new ServerChanNotificationService(settings);
            kimiCollector = new KimiQuotaCollector();
        }

        public TelemetrySnapshot Latest
        {
            get
            {
                lock (stateLock)
                {
                    return latest;
                }
            }
        }

#if STORE_SCREENSHOT
        internal void SetPreviewSnapshot(TelemetrySnapshot snapshot)
        {
            if (snapshot == null)
                throw new ArgumentNullException("snapshot");
            lock (stateLock)
            {
                latest = snapshot;
            }
        }
#endif

        public void Start()
        {
            lock (stateLock)
            {
                if (disposed || started)
                    return;
                started = true;
                workerThread = new Thread(WorkerLoop);
                workerThread.IsBackground = true;
                workerThread.Name = "Taskbar telemetry sampler";
            }

            if (codexCollector != null)
                codexCollector.Start();
            kimiCollector.Start();
            workerThread.Start();
        }

        public string QueueNotificationTest()
        {
            return notificationService.QueueTest();
        }

        public void DisableNotifications()
        {
            notificationService.Disable();
        }

        public void Dispose()
        {
            Thread thread;
            lock (stateLock)
            {
                if (disposed)
                    return;
                disposed = true;
                thread = workerThread;
            }

            stopEvent.Set();
            if (thread != null && !object.ReferenceEquals(thread, Thread.CurrentThread))
            {
                try
                {
                    thread.Join(5000);
                }
                catch
                {
                }
            }

            if (codexCollector != null)
                codexCollector.Dispose();
            notificationService.Dispose();
            kimiCollector.Dispose();
            cpuTemperatureCollector.Dispose();
            nvmlCollector.Dispose();
            systemCollector.Dispose();
            stopEvent.Dispose();
        }

        private void WorkerLoop()
        {
            int interval = Math.Max(500, settings.RefreshIntervalMilliseconds);
            Stopwatch clock = Stopwatch.StartNew();
            long nextSampleAtMilliseconds = 0;
            while (!stopEvent.WaitOne(0))
            {
                TelemetrySnapshot snapshot = CollectSnapshot();
                lock (stateLock)
                {
                    if (!disposed)
                        latest = snapshot;
                }

                // Keep sample starts on the configured cadence. Waiting for a
                // full interval after collection makes the real period equal
                // to collection time plus the interval, which is noticeable
                // when the hardware or Codex backends take longer to respond.
                nextSampleAtMilliseconds += interval;
                long remainingMilliseconds = nextSampleAtMilliseconds - clock.ElapsedMilliseconds;
                if (remainingMilliseconds <= 0)
                {
                    // Do not run a burst of catch-up samples after an overrun.
                    nextSampleAtMilliseconds = clock.ElapsedMilliseconds;
                    continue;
                }

                int waitMilliseconds = (int)Math.Min(int.MaxValue, remainingMilliseconds);
                if (stopEvent.WaitOne(waitMilliseconds))
                    break;
            }
        }

        private TelemetrySnapshot CollectSnapshot()
        {
            TelemetrySnapshot snapshot = new TelemetrySnapshot();
            snapshot.CapturedAtLocal = DateTime.Now;

            try
            {
                snapshot.System = systemCollector.Collect();
            }
            catch (Exception ex)
            {
                snapshot.System = new SystemMetric();
                snapshot.System.Status = "System collection failed: " + SafeMessage(ex);
            }

            try
            {
                CpuFrequencyMetric frequency;
                snapshot.CpuTemperature = cpuTemperatureCollector.Collect(out frequency);
                snapshot.CpuFrequency = frequency;
            }
            catch (Exception ex)
            {
                snapshot.CpuTemperature = new TemperatureMetric();
                snapshot.CpuTemperature.Status = "CPU temperature failed: " + SafeMessage(ex);
            }

            try
            {
                IList<GpuMetric> collected = nvmlCollector.Collect();
                snapshot.Gpus = collected == null ? new List<GpuMetric>() : new List<GpuMetric>(collected);
            }
            catch (Exception ex)
            {
                GpuMetric unavailable = new GpuMetric();
                unavailable.Status = "GPU collection failed: " + SafeMessage(ex);
                snapshot.Gpus = new List<GpuMetric>(new GpuMetric[] { unavailable });
            }

            try
            {
                if (codexMode == "off")
                {
                    snapshot.Codex = new CodexMetric();
                    snapshot.Codex.Status = "Codex quota collection is disabled";
                }
                else
                {
                    CodexMetric appServerMetric = null;
                    if (codexCollector != null)
                    {
                        codexCollector.Refresh();
                        appServerMetric = codexCollector.GetSnapshot();
                    }

                    CodexMetric localMetric = codexSessionCollector == null ? null : codexSessionCollector.Collect();
                    snapshot.Codex = CodexQuotaSourceSelector.Select(appServerMetric, localMetric, DateTime.Now);
                }
            }
            catch (Exception ex)
            {
                snapshot.Codex = new CodexMetric();
                snapshot.Codex.Status = "Codex quota failed: " + SafeMessage(ex);
            }

            DateTime quotaNow = DateTime.Now;
            snapshot.Quotas.Add(codexHistory.Observe(ProviderQuotaMetric.FromCodex(snapshot.Codex), quotaNow));
            snapshot.Quotas.Add(kimiHistory.Observe(kimiCollector.GetSnapshot(), quotaNow));

            try
            {
                // Notifications deliberately remain Codex-only.
                notificationService.Observe(snapshot.Codex);
                snapshot.Notification = notificationService.GetMetric();
            }
            catch (Exception ex)
            {
                snapshot.Notification = new NotificationMetric();
                snapshot.Notification.Enabled = settings.NotificationEnabled;
                snapshot.Notification.Status = "通知状态失败: " + SafeMessage(ex);
            }

            return snapshot;
        }

        private static string NormalizeCodexMode(string value)
        {
            string normalized = string.IsNullOrWhiteSpace(value)
                ? "local"
                : value.Trim().ToLowerInvariant();
            return normalized == "appserver" || normalized == "auto" || normalized == "off"
                ? normalized
                : "local";
        }

        private static string SafeMessage(Exception exception)
        {
            if (exception == null || string.IsNullOrWhiteSpace(exception.Message))
                return "unknown error";
            return exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        }
    }
}
