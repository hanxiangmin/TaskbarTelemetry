using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TaskbarTelemetry
{
    internal sealed class TaskbarForm : Form
    {
        private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupValueName = "TaskbarTelemetry";
        private readonly AppSettings settings;
        private readonly TelemetryEngine engine;
        private readonly string settingsPath;
        private readonly bool isPackaged;
        private readonly string privacyPolicyPath;
        private readonly Timer uiTimer;
        private readonly ToolTip statusToolTip;
        private readonly ContextMenuStrip menu;
        private readonly ToolStripMenuItem startupMenuItem;
        private readonly Font displayFont;
        private readonly int taskbarCreatedMessage;

        private IntPtr attachedTaskbar;
        private bool isTaskbarChild;
        private Rectangle lastPositionBounds;
        private Rectangle pendingPositionBounds;
        private int pendingPositionObservations;
        private bool hasPositioned;
        private bool? positionedDualLayout;
        private string lastToolTipText;
        private bool exitRequested;
        private readonly QuotaRotation quotaRotation = new QuotaRotation();
        private readonly Stopwatch rotationClock = Stopwatch.StartNew();
        private long nextTelemetryPaint;
        private string paintedProvider;
        private bool layoutPreviewEnabled;
        private bool cyclePreviewLayouts;
        private int previewGpuLimit;
        private long previewClockSample;
        private long previewRemainingMilliseconds;
        private ToolStripMenuItem previewMenuItem;
        private ToolStripMenuItem previewSingleItem;
        private ToolStripMenuItem previewDualItem;
        private ToolStripMenuItem previewCycleItem;
        private ToolStripMenuItem previewAutomaticItem;

        internal TaskbarForm(AppSettings settings, TelemetryEngine engine, string settingsPath, bool isPackaged)
        {
            if (settings == null)
                throw new ArgumentNullException("settings");
            if (engine == null)
                throw new ArgumentNullException("engine");

            this.settings = settings;
            this.engine = engine;
            this.settingsPath = settingsPath;
            this.isPackaged = isPackaged;
            privacyPolicyPath = PackageRuntime.GetPrivacyPolicyPath();
            attachedTaskbar = IntPtr.Zero;
            lastPositionBounds = Rectangle.Empty;
            pendingPositionBounds = Rectangle.Empty;
            pendingPositionObservations = 0;
            hasPositioned = false;
            lastToolTipText = string.Empty;

            Text = "TaskbarTelemetry";
            Name = "TaskbarTelemetryWindow";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            ControlBox = false;
            MaximizeBox = false;
            MinimizeBox = false;
            // Match TrafficMonitor's normal window model: this form is
            // reparented into Shell_TrayWnd instead of competing in the
            // desktop-wide topmost band.
            TopMost = false;
            Size = new Size(settings.TaskbarWidth, 36);
            Location = new Point(-32000, -32000);

            SetStyle(ControlStyles.UserPaint |
                     ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            UpdateStyles();

            displayFont = CreateDisplayFont(settings.FontFamily, settings.FontSizePoints);
            Font = displayFont;
            ApplyConfiguredColors();
            // WinForms implements TransparencyKey with the same layered-window
            // path used by TrafficMonitor's GDI renderer. Fuchsia is only a
            // sentinel key and is never painted, so the configured background
            // remains fully opaque while Windows 11 composes the child above
            // the XAML taskbar surface.
            TransparencyKey = Color.Fuchsia;
            menu = new ContextMenuStrip();
            ToolStripMenuItem refreshPositionItem = new ToolStripMenuItem("刷新任务栏位置");
            refreshPositionItem.Click += RefreshPositionClick;
            menu.Items.Add(refreshPositionItem);

            ToolStripMenuItem openSettingsItem = new ToolStripMenuItem("打开配置文件");
            openSettingsItem.Click += OpenSettingsClick;
            menu.Items.Add(openSettingsItem);

            ToolStripMenuItem testNotificationItem = new ToolStripMenuItem("发送通知测试消息");
            testNotificationItem.Click += TestNotificationClick;
            menu.Items.Add(testNotificationItem);

            ToolStripMenuItem notificationSettingsItem = new ToolStripMenuItem("通知与隐私设置...");
            notificationSettingsItem.Click += NotificationSettingsClick;
            menu.Items.Add(notificationSettingsItem);

            ToolStripMenuItem privacyPolicyItem = new ToolStripMenuItem("隐私政策");
            privacyPolicyItem.Click += PrivacyPolicyClick;
            menu.Items.Add(privacyPolicyItem);

            startupMenuItem = new ToolStripMenuItem(isPackaged ? "开机启动（打开系统设置）" : "开机启动");
            startupMenuItem.Click += StartupClick;
            menu.Items.Add(startupMenuItem);
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem exitItem = new ToolStripMenuItem("退出");
            exitItem.Click += ExitClick;
            menu.Items.Add(exitItem);
            menu.Opening += MenuOpening;
            ContextMenuStrip = menu;

            statusToolTip = new ToolTip();
            statusToolTip.InitialDelay = 350;
            statusToolTip.ReshowDelay = 150;
            statusToolTip.AutoPopDelay = 30000;
            statusToolTip.ShowAlways = true;
            statusToolTip.SetToolTip(this, "正在读取状态...");

            taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");

            uiTimer = new Timer();
            uiTimer.Interval = 250;
            uiTimer.Tick += UiTimerTick;
            uiTimer.Start();
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= (int)(NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE);
                parameters.ExStyle &= ~(int)NativeMethods.WS_EX_APPWINDOW;
                return parameters;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            AttachAndPosition(true);
            UpdateToolTip();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            attachedTaskbar = IntPtr.Zero;
            isTaskbarChild = false;
            hasPositioned = false;
            pendingPositionObservations = 0;
            base.OnHandleDestroyed(e);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            TelemetrySnapshot snapshot = GetLatestSnapshot();

            ProviderQuotaMetric quota = quotaRotation.Select(snapshot == null ? null : snapshot.Quotas, rotationClock.Elapsed);
            TaskbarRenderer.Draw(e.Graphics, ClientSize, displayFont, ForeColor, settings, snapshot, quota);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            quotaRotation.SetHovered(true, rotationClock.Elapsed);
            UpdateToolTip();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            quotaRotation.SetHovered(false, rotationClock.Elapsed);
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == NativeMethods.WM_MOUSEACTIVATE)
            {
                message.Result = new IntPtr(NativeMethods.MA_NOACTIVATE);
                return;
            }

            if (taskbarCreatedMessage != 0 && message.Msg == taskbarCreatedMessage)
            {
                attachedTaskbar = IntPtr.Zero;
                isTaskbarChild = false;
                hasPositioned = false;
                BeginInvoke(new MethodInvoker(delegate { AttachAndPosition(true); }));
            }

            base.WndProc(ref message);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!exitRequested && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (uiTimer != null)
                {
                    uiTimer.Stop();
                    uiTimer.Dispose();
                }
                if (statusToolTip != null)
                    statusToolTip.Dispose();
                if (menu != null)
                    menu.Dispose();
                if (displayFont != null)
                    displayFont.Dispose();
            }
            base.Dispose(disposing);
        }

        private void UiTimerTick(object sender, EventArgs e)
        {
            AdvanceLayoutPreview();
            TelemetrySnapshot snapshot = GetLatestSnapshot();
            ProviderQuotaMetric quota = quotaRotation.Select(snapshot == null ? null : snapshot.Quotas, rotationClock.Elapsed);
            string provider = quota == null ? null : quota.ProviderId;
            bool changedProvider = provider != paintedProvider;
            paintedProvider = provider;
            if (rotationClock.ElapsedMilliseconds < nextTelemetryPaint)
            {
                if (changedProvider) Invalidate();
                return;
            }
            nextTelemetryPaint = rotationClock.ElapsedMilliseconds + settings.RefreshIntervalMilliseconds;
            AttachAndPosition(false);
            // Replacing a visible tooltip every second dismisses/reopens it on Windows.
            if (!ClientRectangle.Contains(PointToClient(Cursor.Position))) UpdateToolTip();
            Invalidate();
        }

        internal void AttachAndPosition(bool force)
        {
            if (IsDisposed)
                return;

            IntPtr ownHandle;
            try
            {
                ownHandle = Handle;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            IntPtr taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
            if (taskbar == IntPtr.Zero || !NativeMethods.IsWindow(taskbar))
            {
                attachedTaskbar = IntPtr.Zero;
                isTaskbarChild = false;
                hasPositioned = false;
                return;
            }

            bool parentMatches = NativeMethods.GetParent(ownHandle) == taskbar;
            if (attachedTaskbar != taskbar || !parentMatches || !NativeMethods.HasTaskbarChildStyle(ownHandle))
            {
                isTaskbarChild = NativeMethods.TryAttachToTaskbar(ownHandle, taskbar);
                attachedTaskbar = taskbar;
                hasPositioned = false;
                force = true;
            }

            IntPtr tray = NativeMethods.FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
            if (tray == IntPtr.Zero || !NativeMethods.IsWindow(tray))
            {
                if (!hasPositioned)
                    NativeMethods.ShowWindow(ownHandle, NativeMethods.SW_HIDE);
                return;
            }

            NativeMethods.RECT taskbarRectangle;
            NativeMethods.RECT trayRectangle;
            if (!NativeMethods.GetWindowRect(taskbar, out taskbarRectangle) ||
                !NativeMethods.GetWindowRect(tray, out trayRectangle))
            {
                if (!hasPositioned)
                    NativeMethods.ShowWindow(ownHandle, NativeMethods.SW_HIDE);
                return;
            }

            bool horizontal = taskbarRectangle.Width >= taskbarRectangle.Height;
            TelemetrySnapshot layoutSnapshot = GetLatestSnapshot();
            bool dualLayout = GpuTopology.Devices(layoutSnapshot == null ? null : layoutSnapshot.Gpus).Count >= 2;
            // An actual layout change is intentional, unlike transient tray-arrow
            // geometry. Apply its width immediately; keep normal tray debouncing.
            if (!positionedDualLayout.HasValue || positionedDualLayout.Value != dualLayout) force = true;
            int x;
            int y;
            int width;
            int height;

            if (horizontal)
            {
                float taskbarDpi = (float)(96 * NativeMethods.GetDpiScale(taskbar));
                int idealHeight = (int)Math.Ceiling(displayFont.GetHeight(taskbarDpi) * 2.0f + 6.0f * taskbarDpi / 96F);
                height = Math.Max(24, Math.Min(taskbarRectangle.Height, idealHeight));
                int referenceWidth = (int)Math.Round(TaskbarRenderer.WindowWidth(settings.TaskbarWidth, dualLayout) *
                    (settings.ScaleWidthWithDpi ? NativeMethods.GetDpiScale(taskbar) : 1.0));
                width = Math.Max(160, Math.Min(referenceWidth,
                    Math.Max(160, trayRectangle.Left - taskbarRectangle.Left - 2)));
                x = trayRectangle.Left - width + settings.HorizontalOffset;
                x = Math.Max(taskbarRectangle.Left,
                    Math.Min(Math.Max(taskbarRectangle.Left, taskbarRectangle.Right - width), x));
                y = taskbarRectangle.Top + (taskbarRectangle.Height - height) / 2 + settings.VerticalOffset;
                y = Math.Max(taskbarRectangle.Top,
                    Math.Min(Math.Max(taskbarRectangle.Top, taskbarRectangle.Bottom - height), y));
            }
            else
            {
                width = taskbarRectangle.Width;
                height = Math.Max(48, Math.Min(settings.TaskbarWidth,
                    Math.Max(48, trayRectangle.Top - taskbarRectangle.Top - 2)));
                x = taskbarRectangle.Left + settings.HorizontalOffset;
                x = Math.Max(taskbarRectangle.Left,
                    Math.Min(Math.Max(taskbarRectangle.Left, taskbarRectangle.Right - width), x));
                y = trayRectangle.Top - height + settings.VerticalOffset;
                y = Math.Max(taskbarRectangle.Top,
                    Math.Min(Math.Max(taskbarRectangle.Top, taskbarRectangle.Bottom - height), y));
            }

            if (isTaskbarChild)
            {
                NativeMethods.POINT parentPoint = new NativeMethods.POINT { X = x, Y = y };
                if (!NativeMethods.ScreenToClient(taskbar, ref parentPoint))
                    return;
                x = parentPoint.X;
                y = parentPoint.Y;
            }

            Rectangle desiredBounds = new Rectangle(x, y, width, height);
            bool needsVisibilityRestore = !NativeMethods.IsWindowVisible(ownHandle);
            if (!force && hasPositioned && desiredBounds == lastPositionBounds)
            {
                pendingPositionObservations = 0;
                if (needsVisibilityRestore)
                    NativeMethods.ShowWindow(ownHandle, NativeMethods.SW_SHOWNOACTIVATE);
                return;
            }

            if (!force && hasPositioned)
            {
                if (desiredBounds == pendingPositionBounds)
                    pendingPositionObservations++;
                else
                {
                    pendingPositionBounds = desiredBounds;
                    pendingPositionObservations = 1;
                }
                if (pendingPositionObservations < 2)
                    return;
            }

            uint flags = NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW;
            IntPtr insertAfter;
            if (isTaskbarChild)
            {
                // MoveWindow in TrafficMonitor preserves the child/sibling
                // Z-order. SWP_NOZORDER provides the same behavior here.
                flags |= NativeMethods.SWP_NOZORDER;
                insertAfter = IntPtr.Zero;
            }
            else
            {
                // This path is only a compatibility fallback when cross-process
                // SetParent fails. Keep TrafficMonitor's limited behavior: use
                // topmost only while the taskbar itself owns the foreground.
                insertAfter = NativeMethods.GetForegroundWindow() == taskbar
                    ? NativeMethods.HWND_TOPMOST
                    : NativeMethods.HWND_NOTOPMOST;
            }

            if (!NativeMethods.SetWindowPos(
                ownHandle, insertAfter, x, y, width, height, flags))
                return;

            lastPositionBounds = desiredBounds;
            hasPositioned = true;
            positionedDualLayout = dualLayout;
            pendingPositionBounds = Rectangle.Empty;
            pendingPositionObservations = 0;
        }

        private TelemetrySnapshot GetLatestSnapshot()
        {
            try
            {
                return LayoutPreview.Filter(engine.Latest, layoutPreviewEnabled ? previewGpuLimit : 0);
            }
            catch
            {
                return null;
            }
        }

        internal static string BuildNetworkText(TelemetrySnapshot snapshot)
        {
            if (snapshot == null || snapshot.System == null)
                return "↑ --.-- Mb  ↓ --.-- Mb";
            return "↑ " + TaskbarRenderer.Rate(snapshot.System.UploadBytesPerSecond) +
                   "  ↓ " + TaskbarRenderer.Rate(snapshot.System.DownloadBytesPerSecond);
        }

        internal static string BuildCpuText(TelemetrySnapshot snapshot, string alias)
        {
            string temperature = snapshot != null && snapshot.CpuTemperature != null && snapshot.CpuTemperature.Celsius.HasValue
                ? FormatTemperature(snapshot.CpuTemperature.Celsius.Value) : "--°";
            return CpuModelDetector.ValidateAlias(alias) + " " +
                (snapshot == null || snapshot.System == null ? "--%" : FormatPercent(snapshot.System.CpuUsagePercent)) + " " + temperature;
        }

        internal static string BuildGpuText(TelemetrySnapshot snapshot, int displayIndex, string alias)
        {
            List<GpuMetric> devices = GpuTopology.Devices(snapshot == null ? null : snapshot.Gpus);
            GpuMetric gpu = displayIndex >= 0 && displayIndex < devices.Count ? devices[displayIndex] : null;
            return alias + " " + FormatGpuMemory(gpu == null ? null : gpu.MemoryUsedBytes) + " " +
                (gpu == null || !gpu.UsagePercent.HasValue ? "--%" : FormatPercent(gpu.UsagePercent.Value)) + " " +
                (gpu == null || !gpu.TemperatureCelsius.HasValue ? "--°" : FormatTemperature(gpu.TemperatureCelsius.Value));
        }

        internal static string BuildCodexText(TelemetrySnapshot snapshot)
        {
            return "Codex " + FormatRemaining(snapshot == null || snapshot.Codex == null ? null : snapshot.Codex.Primary);
        }

        internal static string FormatRate(double bytesPerSecond)
        {
            if (double.IsNaN(bytesPerSecond) || double.IsInfinity(bytesPerSecond) || bytesPerSecond < 0.0)
                return "--.--";

            double megabitsPerSecond = bytesPerSecond / 125000.0;
            return megabitsPerSecond.ToString("0.00", CultureInfo.InvariantCulture);
        }

        internal static string FormatPercent(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.0)
                return "--%";
            value = Math.Max(0.0, Math.Min(100.0, value));
            return FormatInteger(value) + "%";
        }

        internal static string FormatTemperature(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return "--°";
            return FormatInteger(value) + "°";
        }

        internal static string FormatGpuMemory(ulong? bytes)
        {
            if (!bytes.HasValue)
                return "--GB";

            double gibibytes = bytes.Value / (1024.0 * 1024.0 * 1024.0);
            double rounded = Math.Round(gibibytes, 0, MidpointRounding.AwayFromZero);
            return rounded.ToString("0", CultureInfo.InvariantCulture) + "GB";
        }

        private static string FormatInteger(double value)
        {
            return value.ToString("0", CultureInfo.InvariantCulture);
        }

        private static string FormatRemaining(QuotaWindowMetric quota)
        {
            if (quota == null || !quota.RemainingPercent.HasValue)
                return "--%";
            return FormatPercent(quota.RemainingPercent.Value);
        }

        private void UpdateToolTip()
        {
            TelemetrySnapshot snapshot = GetLatestSnapshot();
            string text = BuildToolTipText(snapshot);
            if (string.Equals(text, lastToolTipText, StringComparison.Ordinal))
                return;

            lastToolTipText = text;
            statusToolTip.SetToolTip(this, text);
        }

        private string BuildToolTipText(TelemetrySnapshot snapshot)
        {
            if (snapshot == null)
                return "TaskbarTelemetry\r\n等待第一份遥测数据...";

            StringBuilder builder = new StringBuilder();
            builder.Append("TaskbarTelemetry  更新 ");
            builder.Append(snapshot.CapturedAtLocal.ToString("HH:mm:ss", CultureInfo.CurrentCulture));
            if (layoutPreviewEnabled)
            {
                builder.Append("\r\n布局预览：" + PreviewLayoutName());
                builder.Append(cyclePreviewLayouts ? "（每 20 秒切换，悬停暂停）" : "（固定显示）");
                builder.Append("\r\n只筛选显示，不禁用显卡；本次运行不发送通知。");
                builder.Append("\r\n实际检测到 NVIDIA 设备：" + GpuTopology.Devices(engine.Latest.Gpus).Count);
                builder.Append("；右键 → 布局预览可分别固定查看。");
            }
            builder.Append("\r\nCPU: " + settings.CpuFullName);
            builder.Append("\r\nCPU 核心最高频率（硬件库报告，非平均有效频率）: ");
            builder.Append(snapshot.CpuFrequency != null && snapshot.CpuFrequency.Megahertz.HasValue
                ? (snapshot.CpuFrequency.Megahertz.Value / 1000.0).ToString("0.00", CultureInfo.InvariantCulture) + " GHz"
                : "不可用");
            if (snapshot.CpuFrequency != null)
            {
                builder.Append("  " + snapshot.CpuFrequency.SensorName);
                AppendStatus(builder, snapshot.CpuFrequency.Status);
            }
            builder.Append("\r\n网速单位: Mb / Gb / Tb = Mbps / Gbps / Tbps（比特/秒，与测速网页一致；1000 进位）");
            builder.Append("\r\n额度右侧 · 表示数据过期；悬停期间暂停轮换");
            int gpuCount = GpuTopology.Devices(snapshot.Gpus).Count;
            builder.Append("\r\nNVIDIA 设备: " + gpuCount.ToString(CultureInfo.InvariantCulture));
            if (gpuCount == 0) builder.Append("（GPU 不可用；采用单卡结构）");
            if (gpuCount > 2) builder.Append("（仅显示排序后的前两块）");

            if (snapshot.System != null)
            {
                builder.Append("\r\n网络: ");
                builder.Append(string.IsNullOrWhiteSpace(snapshot.System.NetworkName) ? "自动选择" : snapshot.System.NetworkName);
                AppendStatus(builder, snapshot.System.Status);
                if (snapshot.System.MemoryTotalBytes > 0)
                {
                    builder.Append("\r\n内存: ");
                    builder.Append(FormatBytes(snapshot.System.MemoryUsedBytes));
                    builder.Append(" / ");
                    builder.Append(FormatBytes(snapshot.System.MemoryTotalBytes));
                }
            }

            if (settings.CpuTemperatureEnabled && snapshot.CpuTemperature != null)
            {
                builder.Append("\r\nCPU 温度传感器: ");
                builder.Append(string.IsNullOrWhiteSpace(snapshot.CpuTemperature.SensorName) ? "未找到" : snapshot.CpuTemperature.SensorName);
                AppendStatus(builder, snapshot.CpuTemperature.Status);
            }

            if (snapshot.Gpus != null)
            {
                foreach (GpuMetric gpu in snapshot.Gpus)
                {
                    if (gpu == null)
                        continue;
                    builder.Append("\r\nGPU");
                    builder.Append(gpu.DisplayIndex.ToString(CultureInfo.InvariantCulture));
                    builder.Append(": ");
                    builder.Append(string.IsNullOrWhiteSpace(gpu.Name) ? "未知设备" : gpu.Name);
                    if (!string.IsNullOrWhiteSpace(gpu.PciBusId))
                    {
                        builder.Append(" [");
                        builder.Append(gpu.PciBusId);
                        builder.Append("]");
                    }
                    if (gpu.MemoryUsedBytes.HasValue && gpu.MemoryTotalBytes.HasValue &&
                        gpu.MemoryTotalBytes.Value > 0)
                    {
                        builder.Append(" / 显存 ");
                        builder.Append(FormatBytes(gpu.MemoryUsedBytes.Value));
                        builder.Append(" / ");
                        builder.Append(FormatBytes(gpu.MemoryTotalBytes.Value));
                    }
                    AppendStatus(builder, gpu.Status);
                }
            }

            builder.Append("\r\nCodex 刷新：主动查询 " + settings.CodexRefreshSeconds + " 秒 / 本地记录 " + settings.CodexLocalRefreshSeconds + " 秒");
            if (snapshot.Quotas != null)
                foreach (ProviderQuotaMetric quota in snapshot.Quotas)
                {
                    builder.Append("\r\n" + quota.Name + ": ");
                    builder.Append(quota.State == QuotaConnectionState.Connected ? "已连接" :
                        quota.State == QuotaConnectionState.Stale ? "旧值（过期，最多保留5分钟）" :
                        quota.State == QuotaConnectionState.Expired ? "过期（--%，等待新数据）" : "未连接");
                    AppendStatus(builder, quota.Status);
                    foreach (QuotaWindowMetric window in quota.Windows)
                    {
                        // Never present a time-expired historical percentage as current.
                        QuotaWindowMetric detail = quota.State == QuotaConnectionState.Expired
                            ? new QuotaWindowMetric { WindowDurationMinutes = window.WindowDurationMinutes,
                                ResetsAtLocal = window.ResetsAtLocal } : window;
                        AppendQuotaToolTip(builder, string.IsNullOrWhiteSpace(window.Name) ? "额度" : window.Name, detail);
                    }
                    if (quota.UpdatedAtLocal.HasValue)
                        builder.Append("\r\n额度更新: " + quota.UpdatedAtLocal.Value.ToString("yyyy-MM-dd HH:mm:ss"));
                }

            if (snapshot.Notification != null)
            {
                builder.Append("\r\n通知");
                if (!string.IsNullOrWhiteSpace(snapshot.Notification.Channel))
                {
                    builder.Append(" (");
                    builder.Append(snapshot.Notification.Channel);
                    builder.Append(")");
                }
                AppendStatus(builder, snapshot.Notification.Status);
                if (snapshot.Notification.LastSentAtLocal.HasValue)
                {
                    builder.Append("\r\n最近通知推送: ");
                    builder.Append(snapshot.Notification.LastSentAtLocal.Value.ToString(
                        "yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture));
                }
            }

            return builder.ToString();
        }

        private static void AppendQuotaToolTip(StringBuilder builder, string label, QuotaWindowMetric quota)
        {
            builder.Append("\r\n");
            builder.Append(label);
            builder.Append(": 剩余 ");
            builder.Append(FormatRemaining(quota));
            if (quota != null && quota.WindowDurationMinutes.HasValue)
            {
                builder.Append(" / 窗口 ");
                builder.Append(FormatWindowDuration(quota.WindowDurationMinutes.Value));
            }
            if (quota != null && quota.ResetsAtLocal.HasValue)
            {
                builder.Append(" / 重置 ");
                builder.Append(quota.ResetsAtLocal.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture));
            }
        }

        private static string FormatWindowDuration(int minutes)
        {
            if (minutes >= 1440 && minutes % 1440 == 0)
                return (minutes / 1440).ToString(CultureInfo.InvariantCulture) + "天";
            if (minutes >= 60 && minutes % 60 == 0)
                return (minutes / 60).ToString(CultureInfo.InvariantCulture) + "小时";
            return minutes.ToString(CultureInfo.InvariantCulture) + "分钟";
        }

        private static void AppendStatus(StringBuilder builder, string status)
        {
            if (string.IsNullOrWhiteSpace(status))
                return;
            builder.Append(" — ");
            builder.Append(status);
        }

        private static string FormatBytes(ulong bytes)
        {
            double value = bytes;
            string[] units = new string[] { "B", "KiB", "MiB", "GiB", "TiB" };
            int unit = 0;
            while (value >= 1024.0 && unit < units.Length - 1)
            {
                value /= 1024.0;
                unit++;
            }
            return value.ToString(value >= 10.0 ? "0.0" : "0.00", CultureInfo.InvariantCulture) + " " + units[unit];
        }

        private void RefreshPositionClick(object sender, EventArgs e)
        {
            attachedTaskbar = IntPtr.Zero;
            isTaskbarChild = false;
            hasPositioned = false;
            AttachAndPosition(true);
        }

        private void OpenSettingsClick(object sender, EventArgs e)
        {
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = settingsPath;
                startInfo.UseShellExecute = true;
                Process.Start(startInfo);
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, "无法打开配置文件：\r\n" + exception.Message,
                    "TaskbarTelemetry", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void TestNotificationClick(object sender, EventArgs e)
        {
            try
            {
                string result = engine.QueueNotificationTest();
                MessageBox.Show(this, result, "TaskbarTelemetry 通知",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, "无法发送通知测试消息：\r\n" + exception.Message,
                    "TaskbarTelemetry 通知", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void NotificationSettingsClick(object sender, EventArgs e)
        {
            using (NotificationSettingsForm form = new NotificationSettingsForm(
                privacyPolicyPath, settingsPath, engine.DisableNotifications))
            {
                form.ShowDialog(this);
                if (form.Action == NotificationSettingsAction.Enabled)
                {
                    MessageBox.Show(this,
                        "设置已保存。请退出并重新打开 TaskbarTelemetry，使通知生效。",
                        "TaskbarTelemetry 通知",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
                else if (form.Action == NotificationSettingsAction.Disabled)
                {
                    MessageBox.Show(this,
                        form.CleanupComplete
                            ? "通知已立即停用，SendKey 与额度提醒状态已删除。"
                            : "通知已停止，但部分本地清理未完成：\r\n\r\n" + form.CleanupError,
                        "TaskbarTelemetry 通知",
                        MessageBoxButtons.OK,
                        form.CleanupComplete ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                }
            }
        }

        private void PrivacyPolicyClick(object sender, EventArgs e)
        {
            OpenShellTarget(privacyPolicyPath, "无法打开隐私政策");
        }

        private void StartupClick(object sender, EventArgs e)
        {
            try
            {
                if (isPackaged)
                {
                    OpenShellTarget("ms-settings:startupapps", "无法打开 Windows 开机启动设置");
                    return;
                }
                SetStartupEnabled(!IsStartupEnabled());
                startupMenuItem.Checked = IsStartupEnabled();
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, "无法修改开机启动设置：\r\n" + exception.Message,
                    "TaskbarTelemetry", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void MenuOpening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            startupMenuItem.Checked = !isPackaged && IsStartupEnabled();
        }

        internal void EnableLayoutPreview()
        {
            if (layoutPreviewEnabled) return;
            layoutPreviewEnabled = true;
            previewMenuItem = new ToolStripMenuItem("布局预览");
            previewSingleItem = new ToolStripMenuItem("固定单 GPU（第一张真实显卡）");
            previewDualItem = new ToolStripMenuItem("固定双 GPU（前两张真实显卡）");
            previewCycleItem = new ToolStripMenuItem("每 20 秒交替预览");
            previewAutomaticItem = new ToolStripMenuItem("恢复按实际设备自动布局");
            previewSingleItem.Click += delegate { SetPreviewLayout(1, false); };
            previewDualItem.Click += delegate { SetPreviewLayout(2, false); };
            previewCycleItem.Click += delegate { SetPreviewLayout(1, true); };
            previewAutomaticItem.Click += delegate { SetPreviewLayout(0, false); };
            previewMenuItem.DropDownItems.AddRange(new ToolStripItem[] {
                previewSingleItem, previewDualItem, previewCycleItem, previewAutomaticItem });
            menu.Items.Insert(0, previewMenuItem);
            // Preview mode is opt-in at process startup; notification settings were
            // disabled before constructing the engine. Never change persisted consent.
            foreach (ToolStripItem item in menu.Items)
                if (item.Text == "发送通知测试消息" || item.Text == "通知与隐私设置...")
                    item.Enabled = false;
            SetPreviewLayout(1, true);
        }

        private string PreviewLayoutName()
        {
            return previewGpuLimit == 1 ? "单 GPU" : previewGpuLimit == 2 ? "双 GPU" : "自动";
        }

        private void SetPreviewLayout(int gpuLimit, bool cycle)
        {
            previewGpuLimit = gpuLimit;
            cyclePreviewLayouts = cycle;
            previewClockSample = rotationClock.ElapsedMilliseconds;
            previewRemainingMilliseconds = 20000;
            previewSingleItem.Checked = !cycle && gpuLimit == 1;
            previewDualItem.Checked = !cycle && gpuLimit == 2;
            previewCycleItem.Checked = cycle;
            previewAutomaticItem.Checked = !cycle && gpuLimit == 0;
            previewMenuItem.Text = "布局预览：" + PreviewLayoutName() + (cycle ? "（交替中）" : string.Empty);
            Text = "TaskbarTelemetry — " + PreviewLayoutName() + "布局预览";
            nextTelemetryPaint = 0;
            if (IsHandleCreated && hasPositioned) AttachAndPosition(true);
            UpdateToolTip();
            Invalidate();
        }

        private void AdvanceLayoutPreview()
        {
            if (!layoutPreviewEnabled || !cyclePreviewLayouts) return;
            long now = rotationClock.ElapsedMilliseconds;
            long elapsed = now - previewClockSample;
            previewClockSample = now;
            // Keep both layouts on screen long enough to inspect; opening a menu
            // or tooltip must not allow the next layout to move under the pointer.
            if (menu.Visible || ClientRectangle.Contains(PointToClient(Cursor.Position))) return;
            if (GpuTopology.Devices(engine.Latest.Gpus).Count < 2) return;
            previewRemainingMilliseconds -= elapsed;
            if (previewRemainingMilliseconds <= 0)
                SetPreviewLayout(previewGpuLimit == 1 ? 2 : 1, true);
        }

        private void OpenShellTarget(string target, string errorTitle)
        {
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = target;
                startInfo.UseShellExecute = true;
                Process.Start(startInfo);
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, errorTitle + "：\r\n" + exception.Message,
                    "TaskbarTelemetry", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void ExitClick(object sender, EventArgs e)
        {
            exitRequested = true;
            Close();
        }

        private static bool IsStartupEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(StartupRegistryPath, false))
                {
                    if (key == null)
                        return false;
                    object value = key.GetValue(StartupValueName, null);
                    return value != null && !string.IsNullOrWhiteSpace(Convert.ToString(value, CultureInfo.InvariantCulture));
                }
            }
            catch
            {
                return false;
            }
        }

        private static void SetStartupEnabled(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(StartupRegistryPath))
            {
                if (key == null)
                    throw new InvalidOperationException("无法写入当前用户的 Run 注册表项。");

                if (enabled)
                    key.SetValue(StartupValueName, "\"" + Application.ExecutablePath + "\"", RegistryValueKind.String);
                else
                    key.DeleteValue(StartupValueName, false);
            }
        }

        private void ApplyConfiguredColors()
        {
            if (SystemInformation.HighContrast)
            {
                BackColor = SystemColors.Window;
                ForeColor = SystemColors.WindowText;
                return;
            }

            bool lightTaskbar = IsLightSystemTheme();
            Color automaticBackground = lightTaskbar ? Color.FromArgb(243, 243, 243) : Color.FromArgb(32, 32, 32);
            Color automaticForeground = lightTaskbar ? Color.FromArgb(24, 24, 24) : Color.FromArgb(245, 245, 245);
            BackColor = ParseColor(settings.BackgroundColor, automaticBackground);
            ForeColor = ParseColor(settings.ForegroundColor, automaticForeground);
        }

        private static bool IsLightSystemTheme()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", false))
                {
                    if (key == null)
                        return false;
                    object value = key.GetValue("SystemUsesLightTheme", 0);
                    return Convert.ToInt32(value, CultureInfo.InvariantCulture) != 0;
                }
            }
            catch
            {
                return false;
            }
        }

        private static Color ParseColor(string text, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(text) || string.Equals(text, "Auto", StringComparison.OrdinalIgnoreCase))
                return fallback;
            try
            {
                Color color = ColorTranslator.FromHtml(text.Trim());
                return color.A == 0 ? fallback : color;
            }
            catch
            {
                Color namedColor = Color.FromName(text.Trim());
                return namedColor.IsKnownColor || namedColor.IsNamedColor ? namedColor : fallback;
            }
        }

        private static Font CreateDisplayFont(string familyName, float sizeInPoints)
        {
            try
            {
                return new Font(familyName, sizeInPoints, FontStyle.Regular, GraphicsUnit.Point);
            }
            catch
            {
                return new Font(SystemFonts.MessageBoxFont.FontFamily, sizeInPoints, FontStyle.Regular, GraphicsUnit.Point);
            }
        }
    }
}
