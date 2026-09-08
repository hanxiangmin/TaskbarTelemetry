using System;
using System.IO;
using System.Text;

namespace TaskbarTelemetry
{
    internal static class NotificationConsent
    {
        private const string ConsentMarker = "TaskbarTelemetry ServerChan consent v1";

        internal static bool IsGranted()
        {
            try
            {
                string path = GetStatePath();
                if (!File.Exists(path))
                    return false;
                string text = File.ReadAllText(path, Encoding.UTF8);
                return text.IndexOf(ConsentMarker, StringComparison.Ordinal) >= 0 &&
                    text.IndexOf("granted=true", StringComparison.Ordinal) >= 0;
            }
            catch
            {
                return false;
            }
        }

        internal static void Grant()
        {
            string path = GetStatePath();
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(
                path,
                ConsentMarker + Environment.NewLine +
                "granted=true" + Environment.NewLine +
                "updatedUtc=" + DateTime.UtcNow.ToString("o") + Environment.NewLine,
                new UTF8Encoding(false));
        }

        internal static void Revoke()
        {
            DeleteDataFileEverywhere("notification-consent.txt");
        }

        internal static void DeleteQuotaState()
        {
            DeleteDataFileEverywhere("quota-notification-state.json");
        }

        internal static string GetStatePath()
        {
            return Path.Combine(PackageRuntime.GetDataDirectory(), "notification-consent.txt");
        }

        internal static string GetQuotaStatePath()
        {
            return Path.Combine(PackageRuntime.GetDataDirectory(), "quota-notification-state.json");
        }

        private static void DeleteDataFileEverywhere(string fileName)
        {
            string path = Path.Combine(PackageRuntime.GetDataDirectory(), fileName);
            System.Collections.Generic.List<string> errors =
                new System.Collections.Generic.List<string>();
            DeleteIfPresent(path, errors);
            DeleteIfPresent(path + ".tmp", errors);
            if (errors.Count > 0)
                throw new IOException(string.Join("; ", errors.ToArray()));
        }

        private static void DeleteIfPresent(
            string path, System.Collections.Generic.IList<string> errors)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception exception)
            {
                errors.Add(Path.GetFileName(path) + ": " + exception.Message);
            }
        }

    }
}
