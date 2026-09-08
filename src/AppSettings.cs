using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace TaskbarTelemetry
{
    internal sealed class AppSettings
    {
        public int RefreshIntervalMilliseconds { get; private set; }
        public int CodexRefreshSeconds { get; private set; }
        public int TaskbarWidth { get; private set; }
        public bool ScaleWidthWithDpi { get; private set; }
        public int HorizontalOffset { get; private set; }
        public int VerticalOffset { get; private set; }
        public float FontSizePoints { get; private set; }
        public string FontFamily { get; private set; }
        public string NetworkInterfaceId { get; private set; }
        public string CodexMode { get; private set; }
        public string CodexCommand { get; private set; }
        public string CodexHome { get; private set; }
        public bool CpuTemperatureEnabled { get; private set; }
        public string CpuTemperatureLibrary { get; private set; }
        public string CpuAlias { get; private set; }
        public string CpuFullName { get; private set; }
        public string Gpu0Alias { get; private set; }
        public string Gpu1Alias { get; private set; }
        public string ForegroundColor { get; private set; }
        public string BackgroundColor { get; private set; }
        public bool NotificationEnabled { get; private set; }
        public int NotificationThresholdPercent { get; private set; }
        public int NotificationDailyRequestLimit { get; private set; }

        private AppSettings()
        {
            RefreshIntervalMilliseconds = 1000;
            CodexRefreshSeconds = 1;
            TaskbarWidth = 528;
            ScaleWidthWithDpi = true;
            HorizontalOffset = 0;
            VerticalOffset = 0;
            FontSizePoints = 8.5f;
            FontFamily = "Microsoft YaHei UI";
            NetworkInterfaceId = string.Empty;
            CodexMode = "auto";
            CodexCommand = "codex";
            CodexHome = string.Empty;
#if STORE_BUILD
            CpuTemperatureEnabled = false;
            CpuTemperatureLibrary = string.Empty;
#else
            CpuTemperatureEnabled = true;
            CpuTemperatureLibrary = Path.Combine("lib", "LibreHardwareMonitorLib.dll");
#endif
            CpuFullName = CpuModelDetector.DetectFullName();
            CpuAlias = CpuModelDetector.ExtractShortLabel(CpuFullName);
            Gpu0Alias = "GPU0";
            Gpu1Alias = "GPU1";
            ForegroundColor = "Auto";
            BackgroundColor = "Auto";
            NotificationEnabled = false;
            NotificationThresholdPercent = 10;
            NotificationDailyRequestLimit = 5;
        }

        public static AppSettings Load(string path)
        {
            AppSettings settings = new AppSettings();
            if (!File.Exists(path))
                return settings;

            Dictionary<string, string> values = ParseIni(path);
            settings.RefreshIntervalMilliseconds = GetInt(values, "monitor.refreshMilliseconds", settings.RefreshIntervalMilliseconds, 500, 10000);
            settings.CodexRefreshSeconds = GetInt(values, "codex.refreshSeconds", settings.CodexRefreshSeconds, 1, 3600);
            settings.TaskbarWidth = GetInt(values, "ui.width", settings.TaskbarWidth, 360, 900);
            // Old INIs used physical pixels. Preserve their on-screen footprint.
            settings.ScaleWidthWithDpi = GetBool(values, "ui.scaleWidthWithDpi", false);
            settings.HorizontalOffset = GetInt(values, "ui.horizontalOffset", settings.HorizontalOffset, -1000, 1000);
            settings.VerticalOffset = GetInt(values, "ui.verticalOffset", settings.VerticalOffset, -100, 100);
            settings.FontSizePoints = GetFloat(values, "ui.fontSize", settings.FontSizePoints, 6.0f, 18.0f);
            settings.FontFamily = GetString(values, "ui.fontFamily", settings.FontFamily);
            settings.NetworkInterfaceId = GetString(values, "network.interfaceId", settings.NetworkInterfaceId);
            settings.CodexMode = GetString(values, "codex.mode", settings.CodexMode).ToLowerInvariant();
            settings.CodexCommand = GetString(values, "codex.command", settings.CodexCommand);
            settings.CodexHome = GetString(values, "codex.home", settings.CodexHome);
            settings.CpuTemperatureEnabled = GetBool(
                values, "temperature.enabled", settings.CpuTemperatureEnabled);
#if !STORE_BUILD
            settings.CpuTemperatureLibrary = GetString(values, "temperature.libreHardwareMonitorLibrary", settings.CpuTemperatureLibrary);
#else
            settings.CpuTemperatureEnabled = false;
            settings.CpuTemperatureLibrary = string.Empty;
#endif
            settings.CpuAlias = CpuModelDetector.ValidateAlias(GetString(values, "cpu.alias", settings.CpuAlias));
            settings.Gpu0Alias = GetString(values, "gpu.alias0", settings.Gpu0Alias);
            settings.Gpu1Alias = GetString(values, "gpu.alias1", settings.Gpu1Alias);
            settings.ForegroundColor = GetString(values, "ui.foreground", settings.ForegroundColor);
            settings.BackgroundColor = GetString(values, "ui.background", settings.BackgroundColor);
            settings.NotificationEnabled = GetBool(values, "notification.enabled", settings.NotificationEnabled);
            settings.NotificationThresholdPercent = GetInt(values, "notification.thresholdPercent",
                settings.NotificationThresholdPercent, 1, 100);
            settings.NotificationDailyRequestLimit = GetInt(values, "notification.dailyRequestLimit",
                settings.NotificationDailyRequestLimit, 0, 1000);
            if (IsEnabledValue(Environment.GetEnvironmentVariable("TASKBARTELEMETRY_DISABLE_NOTIFICATIONS")))
                settings.NotificationEnabled = false;
            return settings;
        }

        public string ResolvePath(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value))
                return value;
            return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, value));
        }

        internal void DisableNotifications()
        {
            NotificationEnabled = false;
        }

        private static Dictionary<string, string> ParseIni(string path)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string section = string.Empty;
            foreach (string rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith(";", StringComparison.Ordinal) || line.StartsWith("#", StringComparison.Ordinal))
                    continue;
                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                {
                    section = line.Substring(1, line.Length - 2).Trim();
                    continue;
                }
                int equals = line.IndexOf('=');
                if (equals <= 0)
                    continue;
                string key = line.Substring(0, equals).Trim();
                string value = line.Substring(equals + 1).Trim();
                result[section + "." + key] = value;
            }
            return result;
        }

        private static string GetString(Dictionary<string, string> values, string key, string fallback)
        {
            string value;
            return values.TryGetValue(key, out value) && value.Length > 0 ? value : fallback;
        }

        private static int GetInt(Dictionary<string, string> values, string key, int fallback, int minimum, int maximum)
        {
            string text;
            int value;
            if (!values.TryGetValue(key, out text) || !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                return fallback;
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private static float GetFloat(Dictionary<string, string> values, string key, float fallback, float minimum, float maximum)
        {
            string text;
            float value;
            if (!values.TryGetValue(key, out text) || !float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return fallback;
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private static bool GetBool(Dictionary<string, string> values, string key, bool fallback)
        {
            string text;
            if (!values.TryGetValue(key, out text))
                return fallback;

            bool value;
            if (bool.TryParse(text, out value))
                return value;
            if (string.Equals(text, "1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "on", StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(text, "0", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "no", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "off", StringComparison.OrdinalIgnoreCase))
                return false;
            return fallback;
        }

        private static bool IsEnabledValue(string text)
        {
            return string.Equals(text, "1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "on", StringComparison.OrdinalIgnoreCase);
        }
    }
}
