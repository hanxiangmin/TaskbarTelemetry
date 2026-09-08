using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace TaskbarTelemetry
{
    /// <summary>
    /// Small, read-only fallback for machines that only have the packaged
    /// Codex desktop app. It scans recent JSONL event lines for rate-limit
    /// snapshots, retains only the parsed quota fields, and never opens an
    /// authentication file.
    /// </summary>
    internal sealed class CodexSessionQuotaCollector
    {
        private const int MaximumFilesToInspect = 20;
        private const int MaximumTailBytes = 4 * 1024 * 1024;
        private readonly object syncRoot;
        private readonly JavaScriptSerializer serializer;
        private readonly string codexHome;
        private readonly TimeSpan scanInterval;
        private readonly Dictionary<string, FileCandidateCacheEntry> candidateCache;
        private CodexMetric cached;
        private DateTime nextScanUtc;
        private string lastSessionFingerprint;

        private sealed class RateLimitCandidate
        {
            public CodexMetric Metric { get; set; }
            public CodexRateLimitKind Kind { get; set; }
        }

        private sealed class FileCandidateCacheEntry
        {
            public long Length { get; set; }
            public long LastWriteTimeUtcTicks { get; set; }
            public RateLimitCandidate Candidate { get; set; }
        }

        public CodexSessionQuotaCollector(AppSettings settings)
            : this(
                ResolveCodexHome(settings),
                TimeSpan.FromSeconds(settings == null ? 60 : settings.CodexRefreshSeconds))
        {
        }

        internal CodexSessionQuotaCollector(string codexHome, TimeSpan scanInterval)
        {
            syncRoot = new object();
            serializer = new JavaScriptSerializer();
            this.codexHome = codexHome;
            this.scanInterval = scanInterval;
            candidateCache = new Dictionary<string, FileCandidateCacheEntry>(StringComparer.OrdinalIgnoreCase);
            cached = new CodexMetric();
            cached.Status = "Waiting for a Codex rate-limit snapshot";
            nextScanUtc = DateTime.MinValue;
            lastSessionFingerprint = null;
        }

        public CodexMetric Collect()
        {
            lock (syncRoot)
            {
                if (DateTime.UtcNow < nextScanUtc)
                    return CloneMetricForCurrentTime(cached);

                nextScanUtc = DateTime.UtcNow.Add(scanInterval);
                try
                {
                    cached = ScanRecentSessions();
                }
                catch (Exception ex)
                {
                    CodexMetric failed = new CodexMetric();
                    failed.Status = "Codex local quota unavailable: " + SafeMessage(ex);
                    cached = failed;
                    lastSessionFingerprint = null;
                }
                return CloneMetric(cached);
            }
        }

        private CodexMetric ScanRecentSessions()
        {
            CodexMetric unavailable = new CodexMetric();
            if (string.IsNullOrWhiteSpace(codexHome) || !Directory.Exists(codexHome))
            {
                unavailable.Status = "Codex home was not found: " + codexHome;
                return unavailable;
            }

            List<FileInfo> files = new List<FileInfo>();
            AddSessionFiles(files, Path.Combine(codexHome, "sessions"));
            AddSessionFiles(files, Path.Combine(codexHome, "archived_sessions"));
            files.Sort(delegate(FileInfo left, FileInfo right)
            {
                return right.LastWriteTimeUtc.CompareTo(left.LastWriteTimeUtc);
            });

            int count = Math.Min(MaximumFilesToInspect, files.Count);
            string fingerprint = BuildSessionFingerprint(files, count);
            if (string.Equals(fingerprint, lastSessionFingerprint, StringComparison.Ordinal))
            {
                CodexMetric current = CloneMetricForCurrentTime(cached);
                bool cachedHadWindow = cached.Primary != null || cached.Secondary != null;
                if (!cachedHadWindow || current.Primary != null || current.Secondary != null)
                    return current;
            }

            PruneCandidateCache(files, count);

            RateLimitCandidate bestGeneral = null;
            RateLimitCandidate bestLegacy = null;
            int index;
            for (index = 0; index < count; index++)
            {
                RateLimitCandidate found = ReadLatestRateLimitCached(files[index]);
                if (found == null)
                    continue;

                if (found.Kind == CodexRateLimitKind.General)
                {
                    if (IsPreferredGeneralCandidate(found, bestGeneral))
                        bestGeneral = found;
                }
                else if (IsNewer(found, bestLegacy))
                    bestLegacy = found;
            }

            RateLimitCandidate selected = bestGeneral ?? bestLegacy;
            if (selected != null)
            {
                lastSessionFingerprint = fingerprint;
                return selected.Metric;
            }

            unavailable.Status = files.Count == 0
                ? "No Codex session files were found"
                : "No rate-limit snapshot was found in recent Codex sessions";
            lastSessionFingerprint = fingerprint;
            return unavailable;
        }

        private void PruneCandidateCache(IList<FileInfo> files, int count)
        {
            HashSet<string> retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int index;
            for (index = 0; index < count; index++)
                retained.Add(files[index].FullName);

            List<string> stale = new List<string>();
            foreach (string path in candidateCache.Keys)
            {
                if (!retained.Contains(path))
                    stale.Add(path);
            }
            for (index = 0; index < stale.Count; index++)
                candidateCache.Remove(stale[index]);
        }

        private RateLimitCandidate ReadLatestRateLimitCached(FileInfo file)
        {
            FileCandidateCacheEntry entry;
            if (candidateCache.TryGetValue(file.FullName, out entry) &&
                entry.Length == file.Length &&
                entry.LastWriteTimeUtcTicks == file.LastWriteTimeUtc.Ticks)
                return CloneCandidateForCurrentTime(entry.Candidate);

            RateLimitCandidate candidate = ReadLatestRateLimit(file);
            entry = new FileCandidateCacheEntry();
            entry.Length = file.Length;
            entry.LastWriteTimeUtcTicks = file.LastWriteTimeUtc.Ticks;
            entry.Candidate = candidate;
            candidateCache[file.FullName] = entry;
            return CloneCandidateForCurrentTime(candidate);
        }

        private static RateLimitCandidate CloneCandidateForCurrentTime(RateLimitCandidate source)
        {
            if (source == null || source.Metric == null)
                return null;

            CodexMetric metric = CloneMetric(source.Metric);
            DateTime now = DateTime.Now;
            metric.Primary = DiscardExpiredWindow(metric.Primary, now);
            metric.Secondary = DiscardExpiredWindow(metric.Secondary, now);
            if (metric.Primary == null && metric.Secondary == null)
                return null;

            RateLimitCandidate result = new RateLimitCandidate();
            result.Metric = metric;
            result.Kind = source.Kind;
            return result;
        }

        private static string BuildSessionFingerprint(IList<FileInfo> files, int count)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append(files == null ? 0 : files.Count);
            builder.Append('|');
            builder.Append(count);

            for (int index = 0; index < count; index++)
            {
                FileInfo file = files[index];
                builder.Append('|');
                builder.Append(file.FullName.ToUpperInvariant());
                builder.Append('|');
                builder.Append(file.Length.ToString(CultureInfo.InvariantCulture));
                builder.Append('|');
                builder.Append(file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
            }
            return builder.ToString();
        }

        private void AddSessionFiles(List<FileInfo> destination, string directory)
        {
            if (!Directory.Exists(directory))
                return;

            try
            {
                string[] paths = Directory.GetFiles(directory, "*.jsonl", SearchOption.AllDirectories);
                int index;
                for (index = 0; index < paths.Length; index++)
                {
                    try
                    {
                        destination.Add(new FileInfo(paths[index]));
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
                // A partially inaccessible archive must not hide live files.
            }
        }

        private RateLimitCandidate ReadLatestRateLimit(FileInfo file)
        {
            List<string> lines = ReadCandidateLines(file.FullName);
            RateLimitCandidate bestGeneral = null;
            RateLimitCandidate legacyFallback = null;
            int index;
            for (index = lines.Count - 1; index >= 0; index--)
            {
                string line = lines[index];
                Dictionary<string, object> root;
                try
                {
                    root = serializer.DeserializeObject(line.Trim()) as Dictionary<string, object>;
                }
                catch
                {
                    continue;
                }
                if (root == null || !StringValueEquals(root, "type", "event_msg"))
                    continue;

                Dictionary<string, object> payload = GetDictionary(root, "payload");
                if (payload == null || !StringValueEquals(payload, "type", "token_count"))
                    continue;

                Dictionary<string, object> limits = GetDictionary(payload, "rate_limits");
                if (limits == null)
                    limits = GetDictionary(payload, "rateLimits");
                if (limits == null)
                    continue;

                CodexRateLimitKind kind = CodexRateLimitSelector.Classify(limits);
                if (kind == CodexRateLimitKind.ModelSpecific)
                    continue;

                QuotaWindowMetric primary = ParseWindow(GetDictionary(limits, "primary"));
                QuotaWindowMetric secondary = ParseWindow(GetDictionary(limits, "secondary"));
                DateTime now = DateTime.Now;
                primary = DiscardExpiredWindow(primary, now);
                secondary = DiscardExpiredWindow(secondary, now);
                if (primary == null && secondary == null)
                    continue;

                DateTime updated = ParseTimestamp(root, file.LastWriteTime);
                CodexMetric metric = new CodexMetric();
                metric.Primary = primary;
                metric.Secondary = secondary;
                metric.PlanType = GetString(limits, "plan_type", "planType");
                metric.UpdatedAtLocal = updated;
                metric.Status = kind == CodexRateLimitKind.General
                    ? "OK (Codex general quota from local session cache, updated " +
                        updated.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture) + ")"
                    : "OK (legacy untyped Codex quota from local session cache, updated " +
                    updated.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture) + ")";
                RateLimitCandidate candidate = new RateLimitCandidate();
                candidate.Metric = metric;
                candidate.Kind = kind;
                if (kind == CodexRateLimitKind.General)
                {
                    if (IsPreferredGeneralCandidate(candidate, bestGeneral))
                        bestGeneral = candidate;
                }
                else if (legacyFallback == null)
                    legacyFallback = candidate;
            }
            return bestGeneral ?? legacyFallback;
        }

        private static bool IsNewer(RateLimitCandidate candidate, RateLimitCandidate current)
        {
            if (candidate == null || candidate.Metric == null || !candidate.Metric.UpdatedAtLocal.HasValue)
                return false;
            return current == null || current.Metric == null || !current.Metric.UpdatedAtLocal.HasValue ||
                candidate.Metric.UpdatedAtLocal.Value > current.Metric.UpdatedAtLocal.Value;
        }

        private static bool IsPreferredGeneralCandidate(
            RateLimitCandidate candidate,
            RateLimitCandidate current)
        {
            if (candidate == null || candidate.Metric == null)
                return false;
            if (current == null || current.Metric == null)
                return true;

            QuotaWindowMetric candidateWindow = candidate.Metric.Primary ?? candidate.Metric.Secondary;
            QuotaWindowMetric currentWindow = current.Metric.Primary ?? current.Metric.Secondary;
            if (candidateWindow == null || currentWindow == null ||
                !candidateWindow.ResetsAtLocal.HasValue || !currentWindow.ResetsAtLocal.HasValue)
                return IsNewer(candidate, current);

            TimeSpan resetDifference = candidateWindow.ResetsAtLocal.Value - currentWindow.ResetsAtLocal.Value;
            if (Math.Abs(resetDifference.TotalMinutes) > 1.0)
                return resetDifference > TimeSpan.Zero;

            // Concurrent Codex sessions can briefly publish stale snapshots
            // after a fresher one. Usage is monotonic within one reset window,
            // so a later event with a lower used percentage must not make the
            // displayed remaining allowance increase again.
            if (candidateWindow.UsedPercent.HasValue && currentWindow.UsedPercent.HasValue &&
                Math.Abs(candidateWindow.UsedPercent.Value - currentWindow.UsedPercent.Value) > 0.0001)
                return candidateWindow.UsedPercent.Value > currentWindow.UsedPercent.Value;
            if (candidateWindow.UsedPercent.HasValue != currentWindow.UsedPercent.HasValue)
                return candidateWindow.UsedPercent.HasValue;

            return IsNewer(candidate, current);
        }

        private static List<string> ReadCandidateLines(string path)
        {
            List<string> candidates = new List<string>();
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                long count = Math.Min((long)MaximumTailBytes, stream.Length);
                stream.Seek(-count, SeekOrigin.End);
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true, 4096, false))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.IndexOf("\"rate_limits\"", StringComparison.Ordinal) >= 0 ||
                            line.IndexOf("\"rateLimits\"", StringComparison.Ordinal) >= 0)
                            candidates.Add(line);
                    }
                }
            }
            return candidates;
        }

        private static QuotaWindowMetric DiscardExpiredWindow(QuotaWindowMetric window, DateTime now)
        {
            if (window == null)
                return null;
            if (window.ResetsAtLocal.HasValue && window.ResetsAtLocal.Value <= now)
                return null;
            return window;
        }

        private static QuotaWindowMetric ParseWindow(Dictionary<string, object> source)
        {
            if (source == null)
                return null;

            QuotaWindowMetric result = new QuotaWindowMetric();
            double used;
            int minutes;
            long reset;
            if (TryGetDouble(source, "used_percent", "usedPercent", out used))
                result.UsedPercent = used;
            if (TryGetInt32(source, "window_minutes", "windowDurationMins", out minutes))
                result.WindowDurationMinutes = minutes;
            if (TryGetInt64(source, "resets_at", "resetsAt", out reset))
                result.ResetsAtLocal = UnixSecondsToLocal(reset);

            return result.UsedPercent.HasValue || result.WindowDurationMinutes.HasValue || result.ResetsAtLocal.HasValue
                ? result
                : null;
        }

        private static DateTime ParseTimestamp(Dictionary<string, object> root, DateTime fallback)
        {
            object value;
            DateTime parsed;
            if (TryGetValue(root, "timestamp", out value) && value != null &&
                DateTime.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed))
                return parsed.ToLocalTime();
            return fallback;
        }

        private static DateTime? UnixSecondsToLocal(long seconds)
        {
            try
            {
                return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds).ToLocalTime();
            }
            catch
            {
                return null;
            }
        }

        private static string ResolveCodexHome(AppSettings settings)
        {
            string configured = settings == null ? string.Empty : settings.CodexHome;
            if (!string.IsNullOrWhiteSpace(configured))
                return settings.ResolvePath(configured);

            string environmentHome = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (!string.IsNullOrWhiteSpace(environmentHome))
                return Path.GetFullPath(Environment.ExpandEnvironmentVariables(environmentHome));

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        }

        private static Dictionary<string, object> GetDictionary(Dictionary<string, object> source, string key)
        {
            object value;
            return TryGetValue(source, key, out value) ? value as Dictionary<string, object> : null;
        }

        private static bool StringValueEquals(Dictionary<string, object> source, string key, string expected)
        {
            object value;
            return TryGetValue(source, key, out value) && value != null &&
                string.Equals(Convert.ToString(value, CultureInfo.InvariantCulture), expected, StringComparison.Ordinal);
        }

        private static string GetString(Dictionary<string, object> source, string first, string second)
        {
            object value;
            if ((!TryGetValue(source, first, out value) && !TryGetValue(source, second, out value)) || value == null)
                return string.Empty;
            return Convert.ToString(value, CultureInfo.InvariantCulture).Trim();
        }

        private static bool TryGetValue(Dictionary<string, object> source, string key, out object value)
        {
            if (source != null && source.TryGetValue(key, out value))
                return true;
            value = null;
            return false;
        }

        private static bool TryGetDouble(Dictionary<string, object> source, string first, string second, out double result)
        {
            object value;
            if ((!TryGetValue(source, first, out value) && !TryGetValue(source, second, out value)) || value == null)
            {
                result = 0;
                return false;
            }
            try
            {
                result = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return !double.IsNaN(result) && !double.IsInfinity(result);
            }
            catch
            {
                result = 0;
                return false;
            }
        }

        private static bool TryGetInt32(Dictionary<string, object> source, string first, string second, out int result)
        {
            long value;
            if (TryGetInt64(source, first, second, out value) && value >= int.MinValue && value <= int.MaxValue)
            {
                result = (int)value;
                return true;
            }
            result = 0;
            return false;
        }

        private static bool TryGetInt64(Dictionary<string, object> source, string first, string second, out long result)
        {
            object value;
            if ((!TryGetValue(source, first, out value) && !TryGetValue(source, second, out value)) || value == null)
            {
                result = 0;
                return false;
            }
            try
            {
                result = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                result = 0;
                return false;
            }
        }

        private static string SafeMessage(Exception exception)
        {
            if (exception == null || string.IsNullOrWhiteSpace(exception.Message))
                return "unknown error";
            return exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        private static CodexMetric CloneMetric(CodexMetric source)
        {
            CodexMetric result = new CodexMetric();
            result.Primary = CloneWindow(source.Primary);
            result.Secondary = CloneWindow(source.Secondary);
            result.PlanType = source.PlanType;
            result.Status = source.Status;
            result.UpdatedAtLocal = source.UpdatedAtLocal;
            return result;
        }

        private static CodexMetric CloneMetricForCurrentTime(CodexMetric source)
        {
            CodexMetric result = CloneMetric(source);
            bool hadWindow = result.Primary != null || result.Secondary != null;
            DateTime now = DateTime.Now;
            result.Primary = DiscardExpiredWindow(result.Primary, now);
            result.Secondary = DiscardExpiredWindow(result.Secondary, now);
            if (hadWindow && result.Primary == null && result.Secondary == null)
                result.Status = "Last known Codex quota snapshot has expired";
            return result;
        }

        private static QuotaWindowMetric CloneWindow(QuotaWindowMetric source)
        {
            if (source == null)
                return null;
            QuotaWindowMetric result = new QuotaWindowMetric();
            result.UsedPercent = source.UsedPercent;
            result.WindowDurationMinutes = source.WindowDurationMinutes;
            result.ResetsAtLocal = source.ResetsAtLocal;
            return result;
        }
    }
}
