using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TaskbarTelemetry
{
    internal static class SendKeyStore
    {
        internal const string LegacyEnvironmentName = "SERVERCHAN_SENDKEY";
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes(
            "TaskbarTelemetry.ServerChan.SendKey.v1");

        internal static string Read()
        {
#if STORE_SCREENSHOT
            return string.Empty;
#else
            string protectedKey = ReadProtected(GetProtectedPath());
            if (!string.IsNullOrWhiteSpace(protectedKey))
                return protectedKey.Trim();

            // The packaged app never falls back to unpackaged data or a global
            // environment variable. Its LocalState is an independent consent boundary.
            if (PackageRuntime.IsPackaged())
                return string.Empty;

            try
            {
                string legacy = Environment.GetEnvironmentVariable(LegacyEnvironmentName);
                if (string.IsNullOrWhiteSpace(legacy))
                    legacy = Environment.GetEnvironmentVariable(
                        LegacyEnvironmentName, EnvironmentVariableTarget.User);
                return legacy == null ? string.Empty : legacy.Trim();
            }
            catch
            {
                return string.Empty;
            }
#endif
        }

        internal static void Save(string sendKey)
        {
            string currentPath = GetProtectedPath();
            SaveProtected(sendKey, currentPath);
            if (!PackageRuntime.IsPackaged())
            {
                List<string> errors = new List<string>();
                DeleteLegacyEnvironmentVariable(errors);
                ThrowIfCleanupFailed(errors);
            }
        }

        internal static void Delete()
        {
            List<string> errors = new List<string>();
            TryDeleteProtected(GetProtectedPath(), errors);
            if (!PackageRuntime.IsPackaged())
                DeleteLegacyEnvironmentVariable(errors);
            ThrowIfCleanupFailed(errors);
        }

        internal static void SaveProtected(string sendKey, string path)
        {
            if (string.IsNullOrWhiteSpace(sendKey))
                throw new ArgumentException("SendKey 不能为空。", "sendKey");
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("加密存储路径不能为空。", "path");

            byte[] clearBytes = Encoding.UTF8.GetBytes(sendKey.Trim());
            byte[] protectedBytes = null;
            string temporaryPath = path + ".tmp";
            try
            {
                protectedBytes = ProtectedData.Protect(
                    clearBytes, Entropy, DataProtectionScope.CurrentUser);
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);
                File.WriteAllBytes(temporaryPath, protectedBytes);
                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(temporaryPath, path, null, true);
                    }
                    catch
                    {
                        File.Copy(temporaryPath, path, true);
                        File.Delete(temporaryPath);
                    }
                }
                else
                {
                    File.Move(temporaryPath, path);
                }
            }
            catch
            {
                try
                {
                    if (File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                }
                catch
                {
                }
                throw;
            }
            finally
            {
                Array.Clear(clearBytes, 0, clearBytes.Length);
                if (protectedBytes != null)
                    Array.Clear(protectedBytes, 0, protectedBytes.Length);
            }
        }

        internal static string ReadProtected(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return string.Empty;
            byte[] protectedBytes = null;
            byte[] clearBytes = null;
            try
            {
                protectedBytes = File.ReadAllBytes(path);
                clearBytes = ProtectedData.Unprotect(
                    protectedBytes, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(clearBytes).Trim();
            }
            catch
            {
                return string.Empty;
            }
            finally
            {
                if (protectedBytes != null)
                    Array.Clear(protectedBytes, 0, protectedBytes.Length);
                if (clearBytes != null)
                    Array.Clear(clearBytes, 0, clearBytes.Length);
            }
        }

        internal static void DeleteProtected(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            List<string> errors = new List<string>();
            TryDeleteProtected(path, errors);
            ThrowIfCleanupFailed(errors);
        }

        private static string GetProtectedPath()
        {
            return Path.Combine(PackageRuntime.GetDataDirectory(), "serverchan-sendkey.dat");
        }

        private static void DeleteLegacyEnvironmentVariable(IList<string> errors)
        {
            try
            {
                Environment.SetEnvironmentVariable(
                    LegacyEnvironmentName, null, EnvironmentVariableTarget.User);
            }
            catch (Exception exception)
            {
                errors.Add("旧版用户环境变量：" + exception.Message);
            }
            try
            {
                Environment.SetEnvironmentVariable(
                    LegacyEnvironmentName, null, EnvironmentVariableTarget.Process);
            }
            catch (Exception exception)
            {
                errors.Add("旧版进程环境变量：" + exception.Message);
            }
        }

        private static void TryDeleteProtected(string path, IList<string> errors)
        {
            TryDeleteFile(path, errors);
            TryDeleteFile(path + ".tmp", errors);
        }

        private static void TryDeleteFile(string path, IList<string> errors)
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

        private static void ThrowIfCleanupFailed(IList<string> errors)
        {
            if (errors.Count > 0)
                throw new IOException(string.Join("; ", new List<string>(errors).ToArray()));
        }

    }
}
