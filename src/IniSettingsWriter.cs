using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TaskbarTelemetry
{
    internal static class IniSettingsWriter
    {
        internal static void SetNotificationEnabled(string path, bool enabled)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("配置文件路径不能为空。", "path");

            List<string> lines = File.Exists(path)
                ? new List<string>(File.ReadAllLines(path, Encoding.UTF8))
                : new List<string>();
            List<string> normalized = new List<string>();
            bool inNotificationSection = false;
            int lastNotificationHeader = -1;

            for (int index = 0; index < lines.Count; index++)
            {
                string trimmed = lines[index].Trim();
                if (trimmed.StartsWith("[", StringComparison.Ordinal) &&
                    trimmed.EndsWith("]", StringComparison.Ordinal))
                {
                    inNotificationSection = string.Equals(
                        trimmed.Substring(1, trimmed.Length - 2).Trim(),
                        "notification",
                        StringComparison.OrdinalIgnoreCase);
                    if (inNotificationSection)
                        lastNotificationHeader = normalized.Count;
                    normalized.Add(lines[index]);
                    continue;
                }

                int equals = trimmed.IndexOf('=');
                bool isEnabledValue = inNotificationSection && equals > 0 && string.Equals(
                    trimmed.Substring(0, equals).Trim(), "enabled", StringComparison.OrdinalIgnoreCase);
                if (!isEnabledValue)
                    normalized.Add(lines[index]);
            }

            string enabledLine = "enabled=" + (enabled ? "true" : "false");
            if (lastNotificationHeader < 0)
            {
                if (normalized.Count > 0 && normalized[normalized.Count - 1].Length > 0)
                    normalized.Add(string.Empty);
                normalized.Add("[notification]");
                normalized.Add(enabledLine);
            }
            else
            {
                int insertAt = normalized.Count;
                for (int index = lastNotificationHeader + 1; index < normalized.Count; index++)
                {
                    string trimmed = normalized[index].Trim();
                    if (trimmed.StartsWith("[", StringComparison.Ordinal) &&
                        trimmed.EndsWith("]", StringComparison.Ordinal))
                    {
                        insertAt = index;
                        break;
                    }
                }
                normalized.Insert(insertAt, enabledLine);
            }

            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllLines(path, normalized.ToArray(), new UTF8Encoding(false));
        }
    }
}
