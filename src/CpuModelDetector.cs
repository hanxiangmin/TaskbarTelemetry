using System;
using System.Globalization;
using System.Management;
using System.Text.RegularExpressions;

namespace TaskbarTelemetry
{
    internal static class CpuModelDetector
    {
        internal const int LabelCharacters = 10;
        private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

        public static string DetectFullName()
        {
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    @"root\CIMV2", "SELECT Name FROM Win32_Processor"))
                using (ManagementObjectCollection processors = searcher.Get())
                {
                    foreach (ManagementObject processor in processors)
                    using (processor)
                    {
                        string name = Convert.ToString(processor["Name"], CultureInfo.InvariantCulture);
                        if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
                    }
                }
            }
            catch { }
            return "CPU (型号不可用)";
        }

        public static string DetectShortLabel() { return ExtractShortLabel(DetectFullName()); }

        internal static string ExtractShortLabel(string fullName)
        {
            string name = Regex.Replace(fullName ?? "", @"\((?:R|TM)\)|[™®]", "", Options);
            name = Regex.Replace(name, @"\s+", " ").Trim();
            Match m = Regex.Match(name, @"\b(i[3579])\s*-\s*([0-9][A-Z0-9-]*)\b", Options);
            if (m.Success) return Compact(m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value);
            m = Regex.Match(name, @"\bCore\s+Ultra\s+([579])\s+([0-9][A-Z0-9-]*)\b", Options);
            if (m.Success) return Compact("U" + m.Groups[1].Value, m.Groups[2].Value);
            m = Regex.Match(name, @"\bCore\s+([357])\s+([0-9][A-Z0-9-]*)\b", Options);
            if (m.Success) return Compact("C" + m.Groups[1].Value, m.Groups[2].Value);
            m = Regex.Match(name, @"\b(?:Ryzen\s+)?Threadripper\s+(?:PRO\s+)?([0-9][A-Z0-9-]*)\b", Options);
            if (m.Success) return Compact("TR", m.Groups[1].Value);
            m = Regex.Match(name, @"\bRyzen\s+AI\s+([579])\s+(?:PRO\s+)?((?:HX\s*)?[0-9][A-Z0-9-]*)\b", Options);
            if (m.Success) return Compact("A" + m.Groups[1].Value, m.Groups[2].Value.Replace(" ", ""));
            m = Regex.Match(name, @"\bRyzen\s+([3579])\s+(?:PRO\s+)?([0-9][A-Z0-9-]*)\b", Options);
            if (m.Success) return Compact("R" + m.Groups[1].Value, m.Groups[2].Value);
            return "CPU";
        }

        private static string Compact(string family, string model)
        {
            // Never cut a suffix: that could misidentify a processor.
            return model.Length <= 7 && Regex.IsMatch(model, @"\A[A-Z0-9]+\z", Options) ? family + "-" + model.ToUpperInvariant() : "CPU";
        }

        internal static string ValidateAlias(string alias)
        {
            string value = (alias ?? "").Trim();
            return Regex.IsMatch(value, @"\A(?:i[3579]|U[579]|C[357]|R[3579]|A[579]|TR)-[A-Za-z0-9]{1,7}\z")
                ? value : "CPU";
        }
    }
}
