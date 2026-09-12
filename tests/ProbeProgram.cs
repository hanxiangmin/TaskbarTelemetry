using System;
using System.Globalization;
using System.IO;
using System.Threading;

namespace TaskbarTelemetry
{
    internal static class ProbeProgram
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int GetClassName(IntPtr window, System.Text.StringBuilder text, int capacity);
        [STAThread]
        public static int Main(string[] args)
        {
            NativeMethods.TryEnablePerMonitorDpiAwareness();
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length == 1 && args[0] == "--notifications-only")
                return CheckServerChanProtocol() & CheckProtectedSendKeyStorage() & CheckNotificationDialog() ? 0 : 1;
            string settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TaskbarTelemetry.ini");
            AppSettings settings = AppSettings.Load(settingsPath);
            Console.WriteLine("CPU display label: {0}", settings.CpuAlias);
            using (TelemetryEngine engine = new TelemetryEngine(settings))
            {
                engine.Start();
                TelemetrySnapshot snapshot = WaitForSnapshot(engine);
                PrintSnapshot(snapshot);

                bool systemOk = snapshot != null && snapshot.System != null && snapshot.System.MemoryTotalBytes > 0;
                bool cpuBackendOk = snapshot != null && snapshot.CpuTemperature != null &&
                    !string.IsNullOrWhiteSpace(snapshot.CpuTemperature.Status);
                bool gpuOk = snapshot != null && snapshot.Gpus != null;
                bool codexOk = snapshot != null && snapshot.Codex != null && snapshot.Quotas.Count == 2;
                bool cpuLabelOk = !string.IsNullOrWhiteSpace(settings.CpuAlias);
                bool refreshOk = settings.RefreshIntervalMilliseconds == 1000 && settings.CodexRefreshSeconds == 60 && settings.CodexLocalRefreshSeconds == 1;
                bool formattingOk = CheckDisplayFormatting();
                formattingOk &= AdaptiveLayoutTests.Run();
                bool codexSelectionOk = CheckCodexQuotaSelection();
                bool notificationsOk = CheckQuotaNotificationThresholds();
                notificationsOk &= CheckServerChanProtocol();
                notificationsOk &= CheckNotificationDialog();
                bool packagedSettingsOk = CheckPackagedSettingsPath();
                bool sendKeyStorageOk = CheckProtectedSendKeyStorage();
                bool taskbarEmbeddingOk = CheckTaskbarEmbedding(settings, engine, settingsPath);

                Console.WriteLine("CHECK system={0} cpuStatus={1} cpuLabel={2} gpuSnapshot={3} quotaStates={4} refresh1s={5} formatting={6} codexSelection={7} notifications={8} packagedSettings={9} protectedSendKey={10} taskbarEmbedding={11}",
                    systemOk, cpuBackendOk, cpuLabelOk, gpuOk, codexOk, refreshOk, formattingOk,
                    codexSelectionOk, notificationsOk, packagedSettingsOk, sendKeyStorageOk, taskbarEmbeddingOk);
                return systemOk && cpuBackendOk && cpuLabelOk && gpuOk && codexOk && refreshOk &&
                    formattingOk && codexSelectionOk && notificationsOk && packagedSettingsOk && sendKeyStorageOk &&
                    taskbarEmbeddingOk ? 0 : 1;
            }
        }

        private static bool CheckServerChanProtocol()
        {
            const string sc3Key = "sctp123tTEST_TOKEN_456";
            const string turboKey = "SCT123TEST_TOKEN_456";
            Uri endpoint;
            string channel;
            bool ok = CheckNotification("SC3 official endpoint", ServerChanEndpoint.TryCreate(sc3Key, out endpoint, out channel) &&
                endpoint.AbsoluteUri == "https://123.push.ft07.com/send/sctp123tTEST_TOKEN_456.send" && channel.Contains("App"));
            ok &= CheckNotification("SC3 different numeric UID", ServerChanEndpoint.TryCreate("sctp45678tTEST_TOKEN", out endpoint, out channel) &&
                endpoint.Host == "45678.push.ft07.com");
            ok &= CheckNotification("Turbo compatibility", ServerChanEndpoint.TryCreate(turboKey, out endpoint, out channel) &&
                endpoint.AbsoluteUri == "https://sctapi.ftqq.com/SCT123TEST_TOKEN_456.send" && channel.Contains("微信"));
            ok &= CheckNotification("pasted whitespace trimmed", ServerChanEndpoint.TryCreate(" \r\n" + sc3Key + "\r\n ", out endpoint, out channel) &&
                endpoint.AbsolutePath == "/send/" + sc3Key + ".send");
            string[] invalid = new string[] {
                null, "", "sctp123t", "sctpNOT_A_UIDtTOKEN", "sctp123TOKEN", "sctp123tTOKEN/other",
                "sctp123tTOKEN?query", "sctp123tTOKEN#fragment", "sctp123tTOKEN@example.com",
                "sctp123tTOKEN\nINJECT", "sctp１２３tTOKEN", "sct123testvalue", "SCTTOKEN/path",
                "sctp" + new string('1', 64) + "tTOKEN", "SCT" + new string('A', 510)
            };
            bool invalidRejected = true;
            foreach (string value in invalid)
                invalidRejected &= !ServerChanEndpoint.TryCreate(value, out endpoint, out channel) && endpoint == null;
            ok &= CheckNotification("reject malformed keys and URL injection", invalidRejected);

            AppSettings disabledSettings = AppSettings.Load(string.Empty);
            disabledSettings.DisableNotifications();
            using (ServerChanNotificationService service = new ServerChanNotificationService(disabledSettings))
            {
                ok &= CheckNotification("SC3 code zero accepted", service.ParseResponse("{\"code\":0,\"message\":\"SUCCESS\"}", sc3Key).Success);
                ok &= CheckNotification("Turbo code zero accepted", service.ParseResponse("{\"code\":0,\"data\":{\"errno\":0}}", turboKey).Success);
                ServerChanNotificationService.SendResult rejected = service.ParseResponse(
                    "{\"code\":40001,\"message\":\"invalid " + sc3Key + "\"}", sc3Key);
                ok &= CheckNotification("provider rejection redacts secret", !rejected.Success && rejected.ProviderRejected &&
                    !rejected.Error.Contains(sc3Key) && rejected.Error.Contains("<SendKey>"));
                ok &= CheckNotification("malformed responses never succeed",
                    !service.ParseResponse("{}", sc3Key).Success &&
                    !service.ParseResponse("{\"code\":null}", sc3Key).Success &&
                    !service.ParseResponse("{\"code\":0.1}", sc3Key).Success &&
                    !service.ParseResponse("{\"code\":false}", sc3Key).Success &&
                    !service.ParseResponse("<html>error</html>", sc3Key).Success);
            }
            return ok;
        }

        private static bool CheckNotificationDialog()
        {
            bool ok = true;
            foreach (float scale in new float[] { 1F, 1.5F, 2F })
            {
                using (NotificationSettingsForm form = new NotificationSettingsForm(string.Empty, string.Empty, null))
                {
                    form.StartPosition = System.Windows.Forms.FormStartPosition.Manual;
                    form.Location = new System.Drawing.Point(-32000, -32000);
                    form.Show();
                    System.Windows.Forms.Application.DoEvents();
                    form.Scale(new System.Drawing.SizeF(scale, scale));
                    form.PerformLayout();
                    using (System.Drawing.Bitmap bitmap = new System.Drawing.Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(System.Drawing.Point.Empty, form.Size));
                        string name = "notification-settings-" + (scale * 100).ToString("0", CultureInfo.InvariantCulture) + ".png";
                        bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name));
                    }
                    bool fits = CheckControlsFit(form);
                    ok &= CheckNotification("settings layout scale " + scale.ToString(CultureInfo.InvariantCulture), fits);
                }
            }
            return ok;
        }

        private static bool CheckControlsFit(System.Windows.Forms.Control parent)
        {
            bool ok = true;
            foreach (System.Windows.Forms.Control control in parent.Controls)
            {
                if (control.Right > parent.ClientSize.Width || control.Bottom > parent.ClientSize.Height || control.Left < 0 || control.Top < 0)
                {
                    Console.WriteLine("LAYOUT overflow: {0} {1} parent {2}", control.GetType().Name, control.Bounds, parent.ClientSize);
                    ok = false;
                }
                System.Windows.Forms.Label label = control as System.Windows.Forms.Label;
                if (label != null && label.GetPreferredSize(new System.Drawing.Size(label.Width, 0)).Height > label.Height)
                {
                    Console.WriteLine("LAYOUT clipped label: {0} preferred={1}", label.Bounds, label.GetPreferredSize(new System.Drawing.Size(label.Width, 0)));
                    ok = false;
                }
                ok &= CheckControlsFit(control);
            }
            return ok;
        }

        private static bool CheckTaskbarEmbedding(
            AppSettings settings,
            TelemetryEngine engine,
            string settingsPath)
        {
            IntPtr taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
            IntPtr tray = taskbar == IntPtr.Zero
                ? IntPtr.Zero
                : NativeMethods.FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
            if (taskbar == IntPtr.Zero || tray == IntPtr.Zero)
            {
                Console.WriteLine("TASKBAR embedding: SKIPPED (Windows taskbar was not found)");
                return true;
            }

            try
            {
                using (TaskbarForm form = new TaskbarForm(settings, engine, settingsPath, false))
                {
                    // This probe moves the window deliberately. Freeze its position timer
                    // so a normal refresh cannot undo the test move between observations.
                    System.Windows.Forms.Timer positionTimer = (System.Windows.Forms.Timer)typeof(TaskbarForm)
                        .GetField("uiTimer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(form);
                    positionTimer.Stop();
                    IntPtr foregroundBefore = NativeMethods.GetForegroundWindow();
                    form.Show();
                    System.Windows.Forms.Application.DoEvents();
                    form.AttachAndPosition(true);
                    System.Windows.Forms.Application.DoEvents();

                    long style = NativeMethods.GetWindowLongPointer(
                        form.Handle, NativeMethods.GWL_STYLE).ToInt64();
                    long extendedStyle = NativeMethods.GetWindowLongPointer(
                        form.Handle, NativeMethods.GWL_EXSTYLE).ToInt64();
                    bool parentCorrect = NativeMethods.GetParent(form.Handle) == taskbar;
                    bool childStyle = (style & NativeMethods.WS_CHILD) != 0 &&
                        (style & NativeMethods.WS_POPUP) == 0;
                    bool notTopmost = (extendedStyle & NativeMethods.WS_EX_TOPMOST) == 0;
                    bool layered = (extendedStyle & NativeMethods.WS_EX_LAYERED) != 0;

                    NativeMethods.RECT taskbarBounds = new NativeMethods.RECT();
                    NativeMethods.RECT trayBounds = new NativeMethods.RECT();
                    NativeMethods.RECT windowBounds = new NativeMethods.RECT();
                    bool boundsRead = NativeMethods.GetWindowRect(taskbar, out taskbarBounds) &&
                        NativeMethods.GetWindowRect(tray, out trayBounds) &&
                        NativeMethods.GetWindowRect(form.Handle, out windowBounds);
                    bool boundsInsideTaskbar = boundsRead &&
                        windowBounds.Left >= taskbarBounds.Left &&
                        windowBounds.Top >= taskbarBounds.Top &&
                        windowBounds.Right <= taskbarBounds.Right &&
                        windowBounds.Bottom <= taskbarBounds.Bottom &&
                        windowBounds.Right <= trayBounds.Left + Math.Abs(settings.HorizontalOffset);
                    int expectedWidth = Math.Max(160, Math.Min(
                        (int)Math.Round(TaskbarRenderer.WindowWidth(settings.TaskbarWidth, GpuTopology.Devices(engine.Latest.Gpus).Count >= 2) *
                            (settings.ScaleWidthWithDpi ? NativeMethods.GetDpiScale(taskbar) : 1.0)),
                        Math.Max(160, trayBounds.Left - taskbarBounds.Left - 2)));
                    bool widthMatches = boundsRead && windowBounds.Width == expectedWidth;

                    bool hideCalled = NativeMethods.ShowWindow(form.Handle, NativeMethods.SW_HIDE);
                    bool hidden = !NativeMethods.IsWindowVisible(form.Handle);
                    form.AttachAndPosition(false);
                    bool visibilityRecovered = NativeMethods.IsWindowVisible(form.Handle);

                    int probeWidth = Math.Max(1, windowBounds.Width);
                    int probeHeight = Math.Max(1, windowBounds.Height);
                    int probeX = Math.Max(0, (taskbarBounds.Width - probeWidth) / 3);
                    int probeY = Math.Max(0, (taskbarBounds.Height - probeHeight) / 2);
                    bool probeMoved = NativeMethods.SetWindowPos(
                        form.Handle,
                        IntPtr.Zero,
                        probeX,
                        probeY,
                        probeWidth,
                        probeHeight,
                        NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                    form.Update();
                    Thread.Sleep(100); // Let the desktop compositor present the moved child.
                    System.Windows.Forms.Application.DoEvents();
                    NativeMethods.RECT probeBounds = new NativeMethods.RECT();
                    bool probeBoundsRead = NativeMethods.GetWindowRect(form.Handle, out probeBounds);
                    NativeMethods.POINT probePoint = new NativeMethods.POINT
                    {
                        X = probeBounds.Left + probeBounds.Width / 2,
                        Y = probeBounds.Top + probeBounds.Height / 2
                    };
                    IntPtr hitWindow = NativeMethods.WindowFromPoint(probePoint);
                    bool childIsHitTarget = probeMoved && probeBoundsRead && hitWindow == form.Handle;
                    if (!childIsHitTarget)
                    {
                        System.Text.StringBuilder hitClass = new System.Text.StringBuilder(256);
                        GetClassName(hitWindow, hitClass, hitClass.Capacity);
                        Console.WriteLine("TASKBAR hit diagnostic: class={0} own={1} hit={2} hitParent={3} point={4},{5} bounds={6},{7},{8},{9}",
                            hitClass, form.Handle, hitWindow, NativeMethods.GetParent(hitWindow), probePoint.X, probePoint.Y,
                            probeBounds.Left, probeBounds.Top, probeBounds.Right, probeBounds.Bottom);
                    }

                    NativeMethods.ApplyTaskbarFallbackStyles(form.Handle);
                    bool detachedForRecoveryTest = NativeMethods.GetParent(form.Handle) != taskbar &&
                        !NativeMethods.HasTaskbarChildStyle(form.Handle);
                    form.AttachAndPosition(true);
                    bool reattachedAfterParentLoss = detachedForRecoveryTest &&
                        NativeMethods.GetParent(form.Handle) == taskbar &&
                        NativeMethods.HasTaskbarChildStyle(form.Handle);

                    bool focusPreserved = NativeMethods.GetForegroundWindow() == foregroundBefore;
                    bool visible = NativeMethods.IsWindowVisible(form.Handle);
                    bool ok = parentCorrect && childStyle && notTopmost && layered &&
                        boundsInsideTaskbar && widthMatches &&
                        hideCalled && hidden && visibilityRecovered && childIsHitTarget &&
                        reattachedAfterParentLoss && focusPreserved && visible;
                    Console.WriteLine(
                        "TASKBAR embedding: {0} (parentCorrect={1} childStyle={2} notTopmost={3} layered={4} boundsInsideTaskbar={5} widthMatches={6} actualWidth={7} expectedWidth={8} hidden={9} visibilityRecovered={10} childIsHitTarget={11} reattachedAfterParentLoss={12} focusPreserved={13} visible={14})",
                        ok ? "OK" : "FAILED",
                        parentCorrect,
                        childStyle,
                        notTopmost,
                        layered,
                        boundsInsideTaskbar,
                        widthMatches,
                        windowBounds.Width,
                        expectedWidth,
                        hideCalled && hidden,
                        visibilityRecovered,
                        childIsHitTarget,
                        reattachedAfterParentLoss,
                        focusPreserved,
                        visible);
                    return ok;
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine("TASKBAR embedding: FAILED ({0})", exception.Message);
                return false;
            }
        }

        private static bool CheckPackagedSettingsPath()
        {
            string root = Path.Combine(Path.GetTempPath(), "TaskbarTelemetry-Probe-" + Guid.NewGuid().ToString("N"));
            string baseDirectory = Path.Combine(root, "package");
            string localAppData = Path.Combine(root, "local");
            string packageFamilyName = "TaskbarTelemetry.Probe_1234567890abc";
            try
            {
                Directory.CreateDirectory(baseDirectory);
                string templatePath = Path.Combine(baseDirectory, "TaskbarTelemetry.ini");
                File.WriteAllText(templatePath, "[monitor]\r\nrefreshMilliseconds=1000\r\n");
                string legacyDirectory = Path.Combine(localAppData, "TaskbarTelemetry");
                Directory.CreateDirectory(legacyDirectory);
                File.WriteAllText(
                    Path.Combine(legacyDirectory, "TaskbarTelemetry.ini"),
                    "[notification]\r\nenabled=true\r\n[ui]\r\nwidth=500\r\n" +
                    "[notification]\r\nenabled=true\r\n");
                string legacyBefore = File.ReadAllText(
                    Path.Combine(legacyDirectory, "TaskbarTelemetry.ini"));

                string unpackaged = PackageRuntime.ResolveSettingsPath(
                    false, baseDirectory, localAppData, string.Empty);
                string packaged = PackageRuntime.ResolveSettingsPath(
                    true, baseDirectory, localAppData, packageFamilyName);
                string expectedPackaged = Path.Combine(
                    localAppData, "Packages", packageFamilyName, "LocalState",
                    "TaskbarTelemetry", "TaskbarTelemetry.ini");
                bool ok = string.Equals(unpackaged, templatePath, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(packaged, expectedPackaged, StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(packaged) &&
                    string.Equals(
                        File.ReadAllText(packaged),
                        File.ReadAllText(templatePath),
                        StringComparison.Ordinal) &&
                    string.Equals(
                        File.ReadAllText(Path.Combine(legacyDirectory, "TaskbarTelemetry.ini")),
                        legacyBefore,
                        StringComparison.Ordinal);
                File.WriteAllText(
                    packaged,
                    "[notification]\r\nenabled=false\r\n[ui]\r\nwidth=500\r\n" +
                    "[notification]\r\nenabled=false\r\n");
                IniSettingsWriter.SetNotificationEnabled(packaged, true);
                string enabledText = File.ReadAllText(packaged);
                string overrideName = "TASKBARTELEMETRY_DISABLE_NOTIFICATIONS";
                string oldOverride = Environment.GetEnvironmentVariable(overrideName);
                bool parsedEnabled;
                try
                {
                    Environment.SetEnvironmentVariable(overrideName, null);
                    parsedEnabled = AppSettings.Load(packaged).NotificationEnabled;
                }
                finally
                {
                    Environment.SetEnvironmentVariable(overrideName, oldOverride);
                }
                IniSettingsWriter.SetNotificationEnabled(packaged, false);
                string disabledText = File.ReadAllText(packaged);
                ok &= parsedEnabled &&
                    CountToken(enabledText, "enabled=") == 1 &&
                    enabledText.IndexOf("enabled=true", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    CountToken(disabledText, "enabled=") == 1 &&
                    disabledText.IndexOf("enabled=false", StringComparison.OrdinalIgnoreCase) >= 0;
                Console.WriteLine("PACKAGE settings isolation: {0}", ok ? "OK" : "FAILED");
                return ok;
            }
            catch (Exception exception)
            {
                Console.WriteLine("PACKAGE settings isolation: FAILED ({0})", exception.Message);
                return false;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(root))
                        Directory.Delete(root, true);
                }
                catch
                {
                }
            }
        }

        private static bool CheckProtectedSendKeyStorage()
        {
            string root = Path.Combine(
                Path.GetTempPath(), "TaskbarTelemetry-Dpapi-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(root, "sendkey.dat");
            const string testValue = "SCT_TEST_VALUE_123456";
            try
            {
                SendKeyStore.SaveProtected(testValue, path);
                byte[] raw = File.ReadAllBytes(path);
                bool ok = string.Equals(
                    SendKeyStore.ReadProtected(path), testValue, StringComparison.Ordinal) &&
                    !ContainsBytes(raw, System.Text.Encoding.UTF8.GetBytes(testValue));
                File.WriteAllText(path + ".tmp", "temporary");
                SendKeyStore.DeleteProtected(path);
                ok &= !File.Exists(path) && !File.Exists(path + ".tmp");
                Console.WriteLine("DPAPI SendKey storage: {0}", ok ? "OK" : "FAILED");
                return ok;
            }
            catch (Exception exception)
            {
                Console.WriteLine("DPAPI SendKey storage: FAILED ({0})", exception.Message);
                return false;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(root))
                        Directory.Delete(root, true);
                }
                catch
                {
                }
            }
        }

        private static int CountToken(string text, string token)
        {
            int count = 0;
            int index = 0;
            while (!string.IsNullOrEmpty(text) && !string.IsNullOrEmpty(token) &&
                (index = text.IndexOf(token, index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                count++;
                index += token.Length;
            }
            return count;
        }

        private static bool ContainsBytes(byte[] haystack, byte[] needle)
        {
            if (haystack == null || needle == null || needle.Length == 0 || haystack.Length < needle.Length)
                return false;
            for (int start = 0; start <= haystack.Length - needle.Length; start++)
            {
                bool matches = true;
                for (int index = 0; index < needle.Length; index++)
                {
                    if (haystack[start + index] == needle[index])
                        continue;
                    matches = false;
                    break;
                }
                if (matches)
                    return true;
            }
            return false;
        }

        private static bool CheckDisplayFormatting()
        {
            bool ok = true;
            ok &= CheckFormat("network placeholder", TaskbarForm.BuildNetworkText(null), "↑ --.-- Mb  ↓ --.-- Mb");
            ok &= CheckFormat("rate zero", TaskbarForm.FormatRate(0.0), "0.00");
            ok &= CheckFormat("rate below one without padding", TaskbarForm.FormatRate(20000.0), "0.16");
            ok &= CheckFormat("rate two digits retained", TaskbarForm.FormatRate(1520000.0), "12.16");
            ok &= CheckFormat("rate decimal megabits", TaskbarForm.FormatRate(93.57 * 125000.0), "93.57");
            ok &= CheckFormat("rate invalid", TaskbarForm.FormatRate(double.NaN), "--.--");
            ok &= CheckFormat("percent without leading zero", TaskbarForm.FormatPercent(7.0), "7%");
            ok &= CheckFormat("percent one hundred", TaskbarForm.FormatPercent(100.0), "100%");
            ok &= CheckFormat("temperature without leading zero", TaskbarForm.FormatTemperature(8.0), "8°");
            ok &= CheckFormat("temperature three digits", TaskbarForm.FormatTemperature(100.0), "100°");
            ok &= CheckFormat("GPU memory placeholder", TaskbarForm.FormatGpuMemory(null), "--GB");
            ok &= CheckFormat("GPU memory zero", TaskbarForm.FormatGpuMemory(0), "0GB");
            ok &= CheckFormat("GPU memory whole GiB", TaskbarForm.FormatGpuMemory(
                8UL * 1024UL * 1024UL * 1024UL), "8GB");
            ok &= CheckFormat("CPU model Intel Core", CpuModelDetector.ExtractShortLabel(
                "Intel(R) Core(TM) i9-10980XE CPU @ 3.00GHz"), "i9-10980XE");
            ok &= CheckFormat("CPU model Core Ultra", CpuModelDetector.ExtractShortLabel(
                "Intel(R) Core(TM) Ultra 9 285K"), "U9-285K");
            ok &= CheckFormat("CPU model AMD Ryzen", CpuModelDetector.ExtractShortLabel(
                "AMD Ryzen 9 7950X 16-Core Processor"), "R9-7950X");
            ok &= CheckFormat("CPU model Threadripper", CpuModelDetector.ExtractShortLabel(
                "AMD Ryzen Threadripper PRO 7995WX 96-Cores"), "TR-7995WX");
            ok &= CheckFormat("CPU model Xeon", CpuModelDetector.ExtractShortLabel(
                "Intel(R) Xeon(R) W-2295 CPU @ 3.00GHz"), "CPU");
            ok &= CheckFormat("CPU model fallback", CpuModelDetector.ExtractShortLabel(null), "CPU");

            TelemetrySnapshot snapshot = new TelemetrySnapshot();
            GpuMetric gpu = new GpuMetric();
            gpu.DisplayIndex = 0;
            gpu.IsNvidiaDevice = true;
            gpu.StableId = "TEST-GPU-0";
            gpu.MemoryUsedBytes = 8UL * 1024UL * 1024UL * 1024UL;
            gpu.MemoryTotalBytes = 24UL * 1024UL * 1024UL * 1024UL;
            gpu.UsagePercent = 7.0;
            gpu.TemperatureCelsius = 34.0;
            snapshot.Gpus.Add(gpu);
            snapshot.Codex.Primary = new QuotaWindowMetric { UsedPercent = 8.0 };
            snapshot.Codex.Secondary = new QuotaWindowMetric { UsedPercent = 45.0 };
            snapshot.System.CpuUsagePercent = 7.0;
            snapshot.CpuTemperature.Celsius = 34.0;
            ok &= CheckFormat("CPU alias", TaskbarForm.BuildCpuText(snapshot, "i9-10980XE"), "i9-10980XE 7% 34°");
            ok &= CheckFormat("GPU three metrics", TaskbarForm.BuildGpuText(snapshot, 0, "GPU0"), "GPU0 8GB 7% 34°");
            ok &= CheckFormat("Codex primary only", TaskbarForm.BuildCodexText(snapshot), "Codex 92%");
            return ok;
        }

        private static bool CheckFormat(string name, string actual, string expected)
        {
            bool matches = string.Equals(actual, expected, StringComparison.Ordinal);
            Console.WriteLine("FORMAT {0}: {1} ({2})", name, actual, matches ? "OK" : "expected " + expected);
            return matches;
        }

        private static bool CheckCodexQuotaSelection()
        {
            string temporaryRoot = Path.Combine(Path.GetTempPath(),
                "TaskbarTelemetry-quota-" + Guid.NewGuid().ToString("N"));
            string sessions = Path.Combine(temporaryRoot, "sessions");
            Directory.CreateDirectory(sessions);
            try
            {
                DateTime nowUtc = DateTime.UtcNow;
                long resetsAt = (long)(nowUtc.AddDays(7) -
                    new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                string generalPath = Path.Combine(sessions, "general.jsonl");
                string newerGeneralEventPath = Path.Combine(sessions, "newer-general-event.jsonl");
                string staleNewestEventPath = Path.Combine(sessions, "stale-newest-event.jsonl");
                string sparkPath = Path.Combine(sessions, "spark.jsonl");
                string legacyPath = Path.Combine(sessions, "legacy.jsonl");
                string general = "{\"timestamp\":\"" + nowUtc.AddMinutes(-2).ToString("o", CultureInfo.InvariantCulture) +
                    "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"rate_limits\":" +
                    "{\"limit_id\":\"codex\",\"primary\":{\"used_percent\":17,\"window_minutes\":10080," +
                    "\"resets_at\":" + resetsAt.ToString(CultureInfo.InvariantCulture) + "},\"secondary\":null," +
                    "\"plan_type\":\"pro\"}}}";
                string staleNewestEvent = "{\"timestamp\":\"" + nowUtc.AddSeconds(-10).ToString("o", CultureInfo.InvariantCulture) +
                    "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"rate_limits\":" +
                    "{\"limit_id\":\"codex\",\"primary\":{\"used_percent\":15,\"window_minutes\":10080," +
                    "\"resets_at\":" + resetsAt.ToString(CultureInfo.InvariantCulture) + "},\"secondary\":null," +
                    "\"plan_type\":\"pro\"}}}";
                string newerGeneralEvent = "{\"timestamp\":\"" + nowUtc.AddSeconds(-30).ToString("o", CultureInfo.InvariantCulture) +
                    "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"rate_limits\":" +
                    "{\"limit_id\":\"codex\",\"primary\":{\"used_percent\":17,\"window_minutes\":10080," +
                    "\"resets_at\":" + resetsAt.ToString(CultureInfo.InvariantCulture) + "},\"secondary\":null," +
                    "\"plan_type\":\"pro\"}}}";
                string spark = "{\"timestamp\":\"" + nowUtc.AddMinutes(-1).ToString("o", CultureInfo.InvariantCulture) +
                    "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"rate_limits\":" +
                    "{\"limit_id\":\"codex_bengalfox\",\"limit_name\":\"GPT-5.3-Codex-Spark\"," +
                    "\"primary\":{\"used_percent\":0,\"window_minutes\":300,\"resets_at\":" +
                    resetsAt.ToString(CultureInfo.InvariantCulture) + "},\"secondary\":{\"used_percent\":0," +
                    "\"window_minutes\":10080,\"resets_at\":" + resetsAt.ToString(CultureInfo.InvariantCulture) +
                    "},\"plan_type\":\"pro\"}}}";
                string legacy = "{\"timestamp\":\"" + nowUtc.ToString("o", CultureInfo.InvariantCulture) +
                    "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"rate_limits\":" +
                    "{\"primary\":{\"used_percent\":50,\"window_minutes\":300,\"resets_at\":" +
                    resetsAt.ToString(CultureInfo.InvariantCulture) + "}}}}";
                File.WriteAllText(generalPath, general);
                File.WriteAllText(newerGeneralEventPath, newerGeneralEvent);
                File.WriteAllText(staleNewestEventPath, newerGeneralEvent + Environment.NewLine + staleNewestEvent);
                File.WriteAllText(sparkPath, spark);
                File.WriteAllText(legacyPath, legacy);
                File.SetLastWriteTimeUtc(generalPath, nowUtc.AddMinutes(-2));
                File.SetLastWriteTimeUtc(newerGeneralEventPath, nowUtc.AddMinutes(-3));
                File.SetLastWriteTimeUtc(staleNewestEventPath, nowUtc.AddMinutes(-4));
                File.SetLastWriteTimeUtc(sparkPath, nowUtc.AddMinutes(-1));
                File.SetLastWriteTimeUtc(legacyPath, nowUtc);

                CodexSessionQuotaCollector collector = new CodexSessionQuotaCollector(
                    temporaryRoot, TimeSpan.FromSeconds(1));
                CodexMetric selected = collector.Collect();
                bool ok = selected != null && selected.Primary != null &&
                    selected.Primary.WindowDurationMinutes == 10080 &&
                    selected.Primary.RemainingPercent.HasValue &&
                    Math.Abs(selected.Primary.RemainingPercent.Value - 83.0) < 0.001 &&
                    string.Equals(selected.PlanType, "pro", StringComparison.OrdinalIgnoreCase) &&
                    selected.UpdatedAtLocal.HasValue &&
                    selected.UpdatedAtLocal.Value.ToUniversalTime() > nowUtc.AddMinutes(-1) &&
                    selected.Status.IndexOf("general quota", StringComparison.OrdinalIgnoreCase) >= 0;

                string specializedOnlyRoot = Path.Combine(temporaryRoot, "specialized-only");
                string specializedOnlySessions = Path.Combine(specializedOnlyRoot, "sessions");
                Directory.CreateDirectory(specializedOnlySessions);
                File.WriteAllText(Path.Combine(specializedOnlySessions, "spark.jsonl"), spark);
                CodexMetric specializedOnly = new CodexSessionQuotaCollector(
                    specializedOnlyRoot, TimeSpan.FromSeconds(1)).Collect();
                bool specializedRejected = specializedOnly != null &&
                    specializedOnly.Primary == null && specializedOnly.Secondary == null;
                ok &= specializedRejected;

                string legacyOnlyRoot = Path.Combine(temporaryRoot, "legacy-only");
                string legacyOnlySessions = Path.Combine(legacyOnlyRoot, "sessions");
                Directory.CreateDirectory(legacyOnlySessions);
                File.WriteAllText(Path.Combine(legacyOnlySessions, "legacy.jsonl"), legacy);
                CodexMetric legacyOnly = new CodexSessionQuotaCollector(
                    legacyOnlyRoot, TimeSpan.FromSeconds(1)).Collect();
                bool legacyAccepted = legacyOnly != null && legacyOnly.Primary != null &&
                    legacyOnly.Primary.RemainingPercent.HasValue &&
                    Math.Abs(legacyOnly.Primary.RemainingPercent.Value - 50.0) < 0.001 &&
                    legacyOnly.Status.IndexOf("legacy untyped", StringComparison.OrdinalIgnoreCase) >= 0;
                ok &= legacyAccepted;

                string expiryRoot = Path.Combine(temporaryRoot, "expiry-reselection");
                string expirySessions = Path.Combine(expiryRoot, "sessions");
                Directory.CreateDirectory(expirySessions);
                long expiresSoon = (long)(nowUtc.AddSeconds(3) -
                    new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                string expiring = "{\"timestamp\":\"" + nowUtc.ToString("o", CultureInfo.InvariantCulture) +
                    "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"rate_limits\":" +
                    "{\"limit_id\":\"codex\",\"primary\":{\"used_percent\":10,\"window_minutes\":300," +
                    "\"resets_at\":" + expiresSoon.ToString(CultureInfo.InvariantCulture) +
                    "},\"plan_type\":\"pro\"}}}";
                string durableWithoutReset = "{\"timestamp\":\"" + nowUtc.AddMinutes(-2).ToString("o", CultureInfo.InvariantCulture) +
                    "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"rate_limits\":" +
                    "{\"limit_id\":\"codex\",\"primary\":{\"used_percent\":17,\"window_minutes\":10080}," +
                    "\"plan_type\":\"pro\"}}}";
                string durablePath = Path.Combine(expirySessions, "durable.jsonl");
                string expiringPath = Path.Combine(expirySessions, "expiring.jsonl");
                File.WriteAllText(durablePath, durableWithoutReset);
                File.WriteAllText(expiringPath, expiring);
                File.SetLastWriteTimeUtc(durablePath, nowUtc.AddMinutes(-2));
                File.SetLastWriteTimeUtc(expiringPath, nowUtc);
                CodexSessionQuotaCollector expiryCollector = new CodexSessionQuotaCollector(
                    expiryRoot, TimeSpan.FromSeconds(1));
                CodexMetric beforeExpiry = expiryCollector.Collect();
                Thread.Sleep(3500);
                CodexMetric afterExpiry = expiryCollector.Collect();
                bool expiryReselected = beforeExpiry != null && beforeExpiry.Primary != null &&
                    beforeExpiry.Primary.RemainingPercent.HasValue &&
                    Math.Abs(beforeExpiry.Primary.RemainingPercent.Value - 90.0) < 0.001 &&
                    afterExpiry != null && afterExpiry.Primary != null &&
                    afterExpiry.Primary.RemainingPercent.HasValue &&
                    Math.Abs(afterExpiry.Primary.RemainingPercent.Value - 83.0) < 0.001;
                ok &= expiryReselected;
                Console.WriteLine("CODEX selection: remaining={0} window={1} plan={2} ({3})",
                    selected != null && selected.Primary != null && selected.Primary.RemainingPercent.HasValue
                        ? selected.Primary.RemainingPercent.Value.ToString("0", CultureInfo.InvariantCulture) + "%"
                        : "N/A",
                    selected != null && selected.Primary != null && selected.Primary.WindowDurationMinutes.HasValue
                        ? selected.Primary.WindowDurationMinutes.Value.ToString(CultureInfo.InvariantCulture)
                        : "N/A",
                    selected == null ? string.Empty : selected.PlanType,
                    ok ? "OK" : "expected monotonic general Pro quota, model-specific rejection, legacy fallback, and expiry reselection");
                return ok;
            }
            finally
            {
                try
                {
                    Directory.Delete(temporaryRoot, true);
                }
                catch
                {
                }
            }
        }

        private static bool CheckQuotaNotificationThresholds()
        {
            bool ok = true;
            DateTime now = new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Local);
            DateTime resetA = now.AddHours(5);
            DateTime resetB = resetA.AddHours(5);

            QuotaNotificationState baselineState = new QuotaNotificationState();
            QuotaNotificationTracker baselineTracker = new QuotaNotificationTracker(10, baselineState);
            QuotaNotificationObservation baseline = baselineTracker.Observe(CreateQuota(37.2, resetA), now);
            ok &= CheckNotification("first baseline", baseline.BaselineEstablished && !baselineTracker.HasPendingNotification &&
                baselineState.HandledBucket == 3 && baseline.NextThresholdPercent == 40);
            baselineTracker.Observe(CreateQuota(39.9, resetA), now.AddMinutes(1));
            ok &= CheckNotification("below threshold", !baselineTracker.HasPendingNotification);
            baselineTracker.Observe(CreateQuota(40.0, resetA), now.AddMinutes(2));
            PendingQuotaNotification forty = baselineTracker.GetPendingNotification();
            ok &= CheckNotification("cross exact threshold", forty != null && forty.FromThresholdPercent == 40 &&
                forty.ToThresholdPercent == 40);
            baselineTracker.Observe(CreateQuota(41.0, resetA), now.AddMinutes(3));
            PendingQuotaNotification repeatedForty = baselineTracker.GetPendingNotification();
            ok &= CheckNotification("same bucket dedupe", repeatedForty != null && repeatedForty.ToThresholdPercent == 40);
            baselineTracker.MarkSent(repeatedForty, now.AddMinutes(3));
            ok &= CheckNotification("successful ack", !baselineTracker.HasPendingNotification && baselineState.HandledBucket == 4);

            QuotaNotificationState mergeState = new QuotaNotificationState();
            QuotaNotificationTracker mergeTracker = new QuotaNotificationTracker(10, mergeState);
            mergeTracker.Observe(CreateQuota(19.0, resetA), now);
            mergeTracker.Observe(CreateQuota(31.2, resetA), now.AddMinutes(1));
            PendingQuotaNotification merged = mergeTracker.GetPendingNotification();
            ok &= CheckNotification("merge 19 to 31", merged != null && merged.FromThresholdPercent == 20 &&
                merged.ToThresholdPercent == 30 && Math.Abs(merged.UsedPercent - 31.2) < 0.001);

            QuotaNotificationState restartState = new QuotaNotificationState();
            QuotaNotificationTracker beforeRestart = new QuotaNotificationTracker(10, restartState);
            beforeRestart.Observe(CreateQuota(8.0, resetA), now);
            beforeRestart.Observe(CreateQuota(10.2, resetA), now.AddMinutes(1));
            beforeRestart.MarkSent(beforeRestart.GetPendingNotification(), now.AddMinutes(1));
            QuotaNotificationTracker afterRestart = new QuotaNotificationTracker(10, restartState);
            afterRestart.Observe(CreateQuota(12.0, resetA), now.AddMinutes(2));
            ok &= CheckNotification("restart dedupe", !afterRestart.HasPendingNotification);
            afterRestart.Observe(CreateQuota(20.0, resetA), now.AddMinutes(3));
            PendingQuotaNotification twenty = afterRestart.GetPendingNotification();
            ok &= CheckNotification("restart next bucket", twenty != null && twenty.FromThresholdPercent == 20 &&
                twenty.ToThresholdPercent == 20);

            QuotaNotificationState resetState = new QuotaNotificationState();
            QuotaNotificationTracker resetTracker = new QuotaNotificationTracker(10, resetState);
            resetTracker.Observe(CreateQuota(49.0, resetA), now);
            resetTracker.Observe(CreateQuota(51.0, resetA), now.AddMinutes(1));
            resetTracker.MarkSent(resetTracker.GetPendingNotification(), now.AddMinutes(1));
            QuotaNotificationObservation reset = resetTracker.Observe(CreateQuota(3.0, resetB), now.AddMinutes(2));
            ok &= CheckNotification("new window baseline", reset.WindowReset && !resetTracker.HasPendingNotification &&
                resetState.HandledBucket == 0);
            resetTracker.Observe(CreateQuota(10.0, resetB), now.AddMinutes(3));
            ok &= CheckNotification("new window first bucket", resetTracker.HasPendingNotification &&
                resetTracker.GetPendingNotification().ToThresholdPercent == 10);

            QuotaNotificationState lowResetState = new QuotaNotificationState();
            QuotaNotificationTracker lowResetTracker = new QuotaNotificationTracker(10, lowResetState);
            lowResetTracker.Observe(CreateQuota(12.0, resetA), now);
            QuotaNotificationObservation lowReset = lowResetTracker.Observe(CreateQuota(8.0, resetB), now.AddMinutes(1));
            ok &= CheckNotification("small drop with new reset", lowReset.WindowReset &&
                lowResetState.HandledBucket == 0 && !lowResetTracker.HasPendingNotification);

            QuotaNotificationState dipState = new QuotaNotificationState();
            QuotaNotificationTracker dipTracker = new QuotaNotificationTracker(10, dipState);
            dipTracker.Observe(CreateQuota(12.0, resetA), now);
            QuotaNotificationObservation dip = dipTracker.Observe(CreateQuota(8.0, resetA), now.AddMinutes(1));
            ok &= CheckNotification("same window dip", !dip.WindowReset && !dipTracker.HasPendingNotification);
            dipTracker.Observe(CreateQuota(20.0, resetA), now.AddMinutes(2));
            ok &= CheckNotification("dip does not duplicate", dipTracker.HasPendingNotification &&
                dipTracker.GetPendingNotification().FromThresholdPercent == 20);

            QuotaNotificationTracker invalidTracker = new QuotaNotificationTracker(10, new QuotaNotificationState());
            ok &= CheckNotification("null quota", !invalidTracker.Observe(null, now).HasData);
            ok &= CheckNotification("NaN quota", !invalidTracker.Observe(
                new QuotaWindowMetric { UsedPercent = double.NaN }, now).HasData);
            invalidTracker.Observe(CreateQuota(99.9, resetA), now);
            invalidTracker.Observe(CreateQuota(100.0, resetA), now.AddMinutes(1));
            ok &= CheckNotification("one hundred percent", invalidTracker.HasPendingNotification &&
                invalidTracker.GetPendingNotification().ToThresholdPercent == 100);

            PendingQuotaNotification message = new PendingQuotaNotification
            {
                FromBucket = 2,
                ToBucket = 3,
                ThresholdPercent = 10,
                UsedPercent = 31.2,
                WindowDurationMinutes = 300,
                ResetsAtLocal = resetA
            };
            string title = ServerChanNotificationService.BuildQuotaTitle(message);
            string description = ServerChanNotificationService.BuildQuotaDescription(message);
            ok &= CheckNotification("message title", title.IndexOf("已用 31%", StringComparison.Ordinal) >= 0 &&
                title.IndexOf("剩余 69%", StringComparison.Ordinal) >= 0 && title.IndexOf('\n') < 0);
            ok &= CheckNotification("message merged range", description.IndexOf("20%–30%", StringComparison.Ordinal) >= 0 &&
                description.IndexOf("重置时间", StringComparison.Ordinal) >= 0);
            ok &= CheckNotification("free daily limit", !ServerChanNotificationService.IsDailyRequestLimitReached(4, 5) &&
                ServerChanNotificationService.IsDailyRequestLimitReached(5, 5) &&
                !ServerChanNotificationService.IsDailyRequestLimitReached(100, 0));
            ok &= CheckNotification("daily budget rollover key",
                ServerChanNotificationService.GetLocalDateKey(now) !=
                ServerChanNotificationService.GetLocalDateKey(now.AddDays(1)));
            ok &= CheckNotification("expired pending suppressed",
                ServerChanNotificationService.IsPendingExpired(
                    new PendingQuotaNotification { ResetsAtLocal = now.AddMinutes(-1) }, now) &&
                !ServerChanNotificationService.IsPendingExpired(
                    new PendingQuotaNotification { ResetsAtLocal = now.AddMinutes(1) }, now) &&
                !ServerChanNotificationService.IsPendingExpired(
                    new PendingQuotaNotification(), now));

            QuotaNotificationState testIsolationState = new QuotaNotificationState();
            QuotaNotificationTracker testIsolationTracker = new QuotaNotificationTracker(10, testIsolationState);
            testIsolationTracker.Observe(CreateQuota(8.0, resetA), now);
            testIsolationTracker.Observe(CreateQuota(10.0, resetA), now.AddMinutes(1));
            DateTime retryAt = now.ToUniversalTime().AddMinutes(2);
            testIsolationTracker.MarkFailed(retryAt);
            testIsolationTracker.MarkTestSent(now.AddMinutes(1));
            ok &= CheckNotification("test message preserves quota retry",
                testIsolationState.FailedAttempts == 1 &&
                testIsolationTracker.GetNextAttemptUtc() == retryAt &&
                testIsolationTracker.HasPendingNotification);

            PendingQuotaNotification oldGeneration = testIsolationTracker.GetPendingNotification();
            testIsolationTracker.Observe(CreateQuota(2.0, resetB), now.AddMinutes(3));
            int newGeneration = testIsolationState.Generation;
            bool oldResultAccepted = testIsolationTracker.MarkSent(oldGeneration, now.AddMinutes(4));
            ok &= CheckNotification("old generation result ignored",
                !oldResultAccepted && testIsolationState.Generation == newGeneration &&
                testIsolationState.HandledBucket == 0);

            return ok;
        }

        private static QuotaWindowMetric CreateQuota(double usedPercent, DateTime resetsAtLocal)
        {
            return new QuotaWindowMetric
            {
                UsedPercent = usedPercent,
                WindowDurationMinutes = 300,
                ResetsAtLocal = resetsAtLocal
            };
        }

        private static bool CheckNotification(string name, bool condition)
        {
            Console.WriteLine("NOTIFY {0}: {1}", name, condition ? "OK" : "FAILED");
            return condition;
        }

        private static bool HasCompleteGpuMetrics(GpuMetric gpu)
        {
            return gpu != null &&
                gpu.MemoryUsedBytes.HasValue && gpu.MemoryTotalBytes.HasValue &&
                gpu.MemoryTotalBytes.Value > 0 && gpu.MemoryUsedBytes.Value <= gpu.MemoryTotalBytes.Value &&
                gpu.UsagePercent.HasValue && gpu.TemperatureCelsius.HasValue;
        }

        private static TelemetrySnapshot WaitForSnapshot(TelemetryEngine engine)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(25);
            DateTime readyAfter = DateTime.MaxValue;
            TelemetrySnapshot snapshot = null;
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(1000);
                snapshot = engine.Latest;
                if (snapshot != null && snapshot.System != null && snapshot.System.MemoryTotalBytes > 0 &&
                    snapshot.Quotas != null && snapshot.Quotas.Count == 2)
                {
                    if (readyAfter == DateTime.MaxValue)
                        readyAfter = DateTime.UtcNow.AddSeconds(2);
                    else if (DateTime.UtcNow >= readyAfter)
                        break;
                }
                else
                {
                    readyAfter = DateTime.MaxValue;
                }
            }
            return snapshot;
        }

        private static void PrintSnapshot(TelemetrySnapshot snapshot)
        {
            if (snapshot == null)
            {
                Console.WriteLine("No telemetry snapshot was produced.");
                return;
            }

            Console.WriteLine("Captured: {0:yyyy-MM-dd HH:mm:ss}", snapshot.CapturedAtLocal);
            if (snapshot.System != null)
            {
                Console.WriteLine("Network: up={0:0} B/s down={1:0} B/s adapter={2}",
                    snapshot.System.UploadBytesPerSecond,
                    snapshot.System.DownloadBytesPerSecond,
                    snapshot.System.NetworkName);
                Console.WriteLine("CPU: {0:0.0}%  RAM: {1:0.0}%", snapshot.System.CpuUsagePercent, snapshot.System.MemoryUsagePercent);
            }

            if (snapshot.CpuTemperature != null)
            {
                Console.WriteLine("CPU temp: {0}  sensor={1}  status={2}",
                    snapshot.CpuTemperature.Celsius.HasValue
                        ? snapshot.CpuTemperature.Celsius.Value.ToString("0", CultureInfo.InvariantCulture) + " C"
                        : "N/A",
                    snapshot.CpuTemperature.SensorName,
                    snapshot.CpuTemperature.Status);
            }

            if (snapshot.Gpus != null)
            {
                foreach (GpuMetric gpu in snapshot.Gpus)
                {
                    Console.WriteLine("GPU{0}: {1} bus={2} memory={3}/{4} usage={5} temp={6} uuid={7}",
                        gpu.DisplayIndex,
                        gpu.Name,
                        gpu.PciBusId,
                        TaskbarForm.FormatGpuMemory(gpu.MemoryUsedBytes),
                        TaskbarForm.FormatGpuMemory(gpu.MemoryTotalBytes),
                        FormatNullable(gpu.UsagePercent, "%"),
                        FormatNullable(gpu.TemperatureCelsius, " C"),
                        gpu.StableId);
                }
            }

            if (snapshot.Codex != null)
            {
                Console.WriteLine("Codex: primary remaining={0} secondary remaining={1} plan={2} status={3}",
                    FormatRemaining(snapshot.Codex.Primary),
                    FormatRemaining(snapshot.Codex.Secondary),
                    snapshot.Codex.PlanType,
                    snapshot.Codex.Status);
            }
        }

        private static string FormatNullable(double? value, string suffix)
        {
            return value.HasValue ? value.Value.ToString("0.0", CultureInfo.InvariantCulture) + suffix : "N/A";
        }

        private static string FormatRemaining(QuotaWindowMetric window)
        {
            return window != null && window.RemainingPercent.HasValue
                ? window.RemainingPercent.Value.ToString("0.0", CultureInfo.InvariantCulture) + "%"
                : "N/A";
        }
    }
}
