using System;
using System.Collections.Generic;
using System.IO;

namespace TaskbarTelemetry
{
    // CreateProcess cannot execute npm's codex.cmd shim. Elevated launches also
    // lack PATH entries injected into a Codex terminal. Resolve a native EXE
    // before starting; never retry another binary after an OS execution denial.
    internal static class CodexExecutableLocator
    {
        internal static string Resolve(AppSettings settings)
        {
            string command = (settings.CodexCommand ?? string.Empty).Trim().Trim('"');
            if (command.Length == 0) throw new FileNotFoundException("codex.command is empty");
            if (!string.Equals(command, "codex", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(command, "codex.exe", StringComparison.OrdinalIgnoreCase))
            {
                string explicitPath = settings.ResolvePath(command);
                if (!string.Equals(Path.GetExtension(explicitPath), ".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(explicitPath))
                    throw new FileNotFoundException("codex.command must point to an installed native codex.exe, not a shell shim");
                return explicitPath;
            }

            List<string> directories = new List<string>();
            AddPath(directories, Environment.GetEnvironmentVariable("PATH"));
            AddPath(directories, Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User));
            AddPath(directories, Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine));
            foreach (string directory in directories)
            {
                // WindowsApps aliases can launch the GUI instead of a CLI server.
                if (directory.IndexOf("WindowsApps", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                string native = NativeAt(directory);
                if (native != null) return native;
            }

            string desktopBin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
            if (Directory.Exists(desktopBin))
            {
                List<FileInfo> desktop = new List<FileInfo>();
                foreach (string version in Directory.GetDirectories(desktopBin))
                {
                    string native = NativeAt(version);
                    if (native != null) desktop.Add(new FileInfo(native));
                }
                desktop.Sort(delegate(FileInfo a, FileInfo b) { return b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc); });
                if (desktop.Count > 0) return desktop[0].FullName;
            }

            foreach (string directory in directories)
            {
                string package = Path.Combine(directory, "node_modules", "@openai", "codex");
                foreach (string vendor in new string[] {
                    Path.Combine(package, "node_modules", "@openai", "codex-win32-x64", "vendor", "x86_64-pc-windows-msvc"),
                    Path.Combine(package, "vendor", "x86_64-pc-windows-msvc") })
                {
                    string native = NativeAt(Path.Combine(vendor, "bin")) ?? NativeAt(Path.Combine(vendor, "codex"));
                    if (native != null) return native;
                }
            }
            throw new FileNotFoundException("Native Codex CLI not found; set codex.command to its full codex.exe path");
        }

        private static void AddPath(List<string> directories, string path)
        {
            foreach (string entry in (path ?? string.Empty).Split(';'))
            {
                string directory = Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"'));
                if (directory.Length == 0 || !Path.IsPathRooted(directory)) continue;
                if (!directories.Exists(delegate(string value) { return string.Equals(value, directory, StringComparison.OrdinalIgnoreCase); }))
                    directories.Add(directory);
            }
        }

        private static string NativeAt(string directory)
        {
            try
            {
                string candidate = Path.Combine(directory, "codex.exe");
                return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
            }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
        }
    }
}
