using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TaskbarTelemetry
{
    internal static class PackageRuntime
    {
        private const int ErrorSuccess = 0;
        private const int ErrorInsufficientBuffer = 122;
        private const int AppModelErrorNoPackage = 15700;
        private const string DataDirectoryName = "TaskbarTelemetry";
        private const string DataBoundaryMarkerName = ".store-data-boundary-v1";

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetCurrentPackageFullName(
            ref int packageFullNameLength,
            StringBuilder packageFullName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetCurrentPackageFamilyName(
            ref int packageFamilyNameLength,
            StringBuilder packageFamilyName);

        internal static bool IsPackaged()
        {
            try
            {
                int length = 0;
                int result = GetCurrentPackageFullName(ref length, null);
                if (result == AppModelErrorNoPackage)
                    return false;
                if (result == ErrorInsufficientBuffer || result == ErrorSuccess)
                    return true;
                throw new InvalidOperationException(
                    "Unable to determine MSIX package identity (error " + result + ").");
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    "Unable to determine whether the process has MSIX package identity.",
                    exception);
            }
        }

        internal static string ResolveSettingsPath(bool isPackaged)
        {
            string packageFamilyName = isPackaged ? ReadCurrentPackageFamilyName() : string.Empty;
            return ResolveSettingsPath(
                isPackaged,
                AppDomain.CurrentDomain.BaseDirectory,
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                packageFamilyName);
        }

        internal static string ResolveSettingsPath(
            bool isPackaged,
            string baseDirectory,
            string localApplicationData,
            string packageFamilyName)
        {
            string templatePath = Path.Combine(baseDirectory, "TaskbarTelemetry.ini");
            if (!isPackaged)
                return templatePath;

            string settingsDirectory = ResolveDataDirectory(true, localApplicationData, packageFamilyName);
            EnsurePackagedDataBoundary(settingsDirectory);
            string settingsPath = Path.Combine(settingsDirectory, "TaskbarTelemetry.ini");
            if (!File.Exists(settingsPath))
            {
                Directory.CreateDirectory(settingsDirectory);
                File.Copy(templatePath, settingsPath, false);
            }
            return settingsPath;
        }

        internal static string GetDataDirectory()
        {
            bool isPackaged = IsPackaged();
            string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!isPackaged)
                return GetLegacyDataDirectory(localApplicationData);

            string directory = ResolveDataDirectory(
                true, localApplicationData, ReadCurrentPackageFamilyName());
            EnsurePackagedDataBoundary(directory);
            return directory;
        }

        internal static string GetLegacyDataDirectory()
        {
            return GetLegacyDataDirectory(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        }

        internal static string GetLegacyDataDirectory(string localApplicationData)
        {
            if (string.IsNullOrWhiteSpace(localApplicationData))
                throw new ArgumentException("Local application data path is required.", "localApplicationData");
            return Path.Combine(localApplicationData, DataDirectoryName);
        }

        internal static string ResolveDataDirectory(
            bool isPackaged,
            string localApplicationData,
            string packageFamilyName)
        {
            if (!isPackaged)
                return GetLegacyDataDirectory(localApplicationData);
            if (string.IsNullOrWhiteSpace(packageFamilyName) ||
                packageFamilyName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                packageFamilyName.IndexOf(Path.DirectorySeparatorChar) >= 0 ||
                packageFamilyName.IndexOf(Path.AltDirectorySeparatorChar) >= 0)
                throw new InvalidOperationException("Windows did not provide a valid package family name.");

            return Path.Combine(
                localApplicationData,
                "Packages",
                packageFamilyName,
                "LocalState",
                DataDirectoryName);
        }

        internal static void EnsurePackagedDataBoundary(string packagedDataDirectory)
        {
            if (string.IsNullOrWhiteSpace(packagedDataDirectory))
                throw new ArgumentException("Packaged data directory is required.", "packagedDataDirectory");

            Directory.CreateDirectory(packagedDataDirectory);
            string markerPath = Path.Combine(packagedDataDirectory, DataBoundaryMarkerName);
            if (File.Exists(markerPath))
                return;

            File.WriteAllText(
                markerPath,
                "Legacy settings, consent, keys, and notification state were not imported." +
                Environment.NewLine,
                new UTF8Encoding(false));
        }

        internal static string GetPrivacyPolicyPath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PRIVACY.html");
        }

        private static string ReadCurrentPackageFamilyName()
        {
            int length = 0;
            int result = GetCurrentPackageFamilyName(ref length, null);
            if (result == AppModelErrorNoPackage)
                throw new InvalidOperationException("The process does not have MSIX package identity.");
            if (result != ErrorInsufficientBuffer || length <= 1)
                throw new InvalidOperationException(
                    "Unable to read the MSIX package family name (error " + result + ").");

            StringBuilder builder = new StringBuilder(length);
            result = GetCurrentPackageFamilyName(ref length, builder);
            if (result != ErrorSuccess || builder.Length == 0)
                throw new InvalidOperationException(
                    "Unable to read the MSIX package family name (error " + result + ").");
            return builder.ToString();
        }
    }
}
