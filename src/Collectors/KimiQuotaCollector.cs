using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace TaskbarTelemetry
{
    /// <summary>
    /// Discovery gate, not a reverse-engineered desktop account API. The installed
    /// Kimi desktop does not expose a verified read-only plan-quota source. Never
    /// interpret chat token counters, wallet balances or presence of a client as quota.
    /// </summary>
    internal sealed class KimiQuotaCollector : IDisposable
    {
        internal const int RefreshSeconds = 60;
        private readonly object sync = new object();
        private readonly ManualResetEvent stop = new ManualResetEvent(false);
        private readonly Func<ProviderQuotaMetric> readSource;
        private Thread worker;
        private ProviderQuotaMetric latest = new ProviderQuotaMetric("kimi", "Kimi");
        internal KimiQuotaCollector() : this(DiscoverReadOnlySource) { }
        internal KimiQuotaCollector(Func<ProviderQuotaMetric> source) { readSource = source; }
        internal void Start()
        {
            if (worker != null) return;
            worker = new Thread(delegate()
            {
                do
                {
                    ProviderQuotaMetric sample;
                    try { sample = readSource(); }
                    catch { sample = new ProviderQuotaMetric("kimi", "Kimi") { Status = "本地额度来源读取失败" }; }
                    lock (sync) latest = sample;
                } while (!stop.WaitOne(RefreshSeconds * 1000));
            });
            worker.IsBackground = true;
            worker.Name = "Kimi quota discovery (read only)";
            worker.Start();
        }
        internal ProviderQuotaMetric GetSnapshot() { lock (sync) return latest; }
        public void Dispose()
        {
            stop.Set();
            if (worker == null || worker.Join(3000)) stop.Dispose();
        }
        internal static ProviderQuotaMetric DiscoverReadOnlySource()
        {
            bool desktop = File.Exists(Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), @"Programs\kimi-desktop\Kimi.exe"));
            foreach (Process process in Process.GetProcessesByName("Kimi"))
                using (process) desktop = true;
            string home = Environment.GetEnvironmentVariable("KIMI_CODE_HOME");
            if (string.IsNullOrWhiteSpace(home)) home = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kimi-code");
            bool hasInstances = Directory.Exists(Path.Combine(home, @"server\instances"));
            // Official REST /api/v1/oauth/usage is documented, but automatic discovery
            // and bearer-token access must be verified against the actual local service.
            // Do not read OAuth credentials, browser cookies, session content or logs.
            return new ProviderQuotaMetric("kimi", "Kimi") { Status = hasInstances
                ? "发现 Kimi Code 服务目录；本地服务身份及只读授权尚未验证，未连接"
                : desktop ? "已检测到 Kimi 桌面版；未发现已验证的套餐额度接口，未连接"
                : "未发现可用的 Kimi 套餐额度来源，未连接" };
        }
    }
}
