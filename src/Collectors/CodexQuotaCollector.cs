using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace TaskbarTelemetry
{
    /// <summary>
    /// Reads ChatGPT Codex quota through the documented Codex app-server
    /// stdio protocol.  Authentication remains owned by Codex; this class
    /// never reads or stores credentials.
    /// </summary>
    internal sealed class CodexQuotaCollector : IDisposable
    {
        private const int StartRetrySeconds = 15;
        private const int HandshakeTimeoutSeconds = 20;

        private readonly AppSettings _settings;
        private readonly object _stateLock;
        private readonly object _writeLock;
        private readonly object _serializerLock;
        private readonly JavaScriptSerializer _serializer;

        private CodexMetric _snapshot;
        private Process _process;
        private Thread _stdoutThread;
        private Thread _stderrThread;
        private Timer _refreshTimer;
        private long _nextRequestId;
        private long _initializeRequestId;
        private long _accountRequestId;
        private long _rateLimitsRequestId;
        private DateTime _handshakeStartedUtc;
        private DateTime _accountRequestSentUtc;
        private DateTime _rateLimitsRequestSentUtc;
        private DateTime _nextQuotaRefreshUtc;
        private DateTime _nextStartAttemptUtc;
        private bool _started;
        private bool _processStarting;
        private bool _initialized;
        private bool _disposed;

        public CodexQuotaCollector(AppSettings settings)
        {
            if (settings == null)
                throw new ArgumentNullException("settings");

            _settings = settings;
            _stateLock = new object();
            _writeLock = new object();
            _serializerLock = new object();
            _serializer = new JavaScriptSerializer();
            _snapshot = new CodexMetric();
            _nextQuotaRefreshUtc = DateTime.MinValue;
            _nextStartAttemptUtc = DateTime.MinValue;
        }

        public void Start()
        {
            lock (_stateLock)
            {
                if (_disposed)
                {
                    SetStatusLocked("Codex quota collector is stopped");
                    return;
                }

                if (_started)
                    return;

                _started = true;
            }

            EnsureProcessStarted(true);

            lock (_stateLock)
            {
                if (!_disposed && _refreshTimer == null)
                    _refreshTimer = new Timer(OnRefreshTimer, null, 1000, 1000);
            }
        }

        /// <summary>
        /// Safe to call from the one-second UI loop.  Network-backed quota
        /// requests are throttled to AppSettings.CodexRefreshSeconds.
        /// </summary>
        public void Refresh()
        {
            Process process;
            bool shouldStart;
            bool shouldCloseInput;
            bool shouldReadQuota;
            DateTime nowUtc = DateTime.UtcNow;

            lock (_stateLock)
            {
                if (_disposed)
                    return;

                if (!_started)
                    _started = true;

                process = _process;
                shouldStart = !IsProcessRunning(process) && !_processStarting && nowUtc >= _nextStartAttemptUtc;
                shouldCloseInput = IsProcessRunning(process) && !_initialized &&
                    _handshakeStartedUtc != DateTime.MinValue &&
                    nowUtc - _handshakeStartedUtc >= TimeSpan.FromSeconds(HandshakeTimeoutSeconds);

                if (shouldCloseInput)
                {
                    _handshakeStartedUtc = DateTime.MinValue;
                    _nextStartAttemptUtc = nowUtc.AddSeconds(StartRetrySeconds);
                    SetStatusLocked("Codex app-server initialization timed out");
                }

                if (_rateLimitsRequestId != 0 &&
                    nowUtc - _rateLimitsRequestSentUtc >= GetRequestTimeout())
                {
                    _rateLimitsRequestId = 0;
                    SetStatusLocked("Codex quota request timed out; retrying");
                }

                if (_accountRequestId != 0 &&
                    nowUtc - _accountRequestSentUtc >= GetRequestTimeout())
                {
                    _accountRequestId = 0;
                    SetStatusLocked("Codex account request timed out; reading quota directly");
                }

                shouldReadQuota = IsProcessRunning(process) && _initialized &&
                    _rateLimitsRequestId == 0 && nowUtc >= _nextQuotaRefreshUtc;
            }

            if (shouldCloseInput)
                CloseStandardInput(process);

            if (shouldStart)
            {
                EnsureProcessStarted(false);
                return;
            }

            if (shouldReadQuota)
                SendRateLimitsRequest();
        }

        public CodexMetric GetSnapshot()
        {
            lock (_stateLock)
            {
                CodexMetric result = CloneMetric(_snapshot);
                DateTime now = DateTime.Now;
                bool hadWindow = result.Primary != null || result.Secondary != null;
                result.Primary = DiscardExpiredWindow(result.Primary, now);
                result.Secondary = DiscardExpiredWindow(result.Secondary, now);
                if (hadWindow && result.Primary == null && result.Secondary == null)
                    result.Status = "Last known Codex quota snapshot has expired";
                return result;
            }
        }

        public void Dispose()
        {
            Timer timer;
            Process process;
            Thread stdoutThread;
            Thread stderrThread;

            lock (_stateLock)
            {
                if (_disposed)
                    return;

                _disposed = true;
                _started = false;
                timer = _refreshTimer;
                _refreshTimer = null;
                process = _process;
                _process = null;
                stdoutThread = _stdoutThread;
                stderrThread = _stderrThread;
                _stdoutThread = null;
                _stderrThread = null;
                ResetProtocolStateLocked();
            }

            if (timer != null)
                timer.Dispose();

            // Closing stdin sends EOF to app-server and lets it terminate on
            // its own.  Deliberately do not Kill or taskkill the process.
            CloseStandardInput(process);

            if (process != null)
            {
                try
                {
                    if (!process.HasExited)
                        process.WaitForExit(2000);
                }
                catch
                {
                    // Disposal is best effort and must not break app shutdown.
                }
            }

            JoinThread(stdoutThread);
            JoinThread(stderrThread);

            if (process != null)
            {
                try
                {
                    process.Dispose();
                }
                catch
                {
                }
            }
        }

        private void EnsureProcessStarted(bool force)
        {
            string command;
            Process process = null;
            DateTime nowUtc = DateTime.UtcNow;

            lock (_stateLock)
            {
                if (_disposed || _processStarting || IsProcessRunning(_process))
                    return;
                if (!force && nowUtc < _nextStartAttemptUtc)
                    return;

                _processStarting = true;
                _nextStartAttemptUtc = nowUtc.AddSeconds(StartRetrySeconds);
                SetStatusLocked("Starting Codex app-server");
                command = NormalizeCommand(_settings.CodexCommand);
            }

            try
            {
                if (command.Length == 0)
                    throw new InvalidOperationException("codex.command is empty");

                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = command;
                startInfo.Arguments = "app-server --listen stdio://";
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = true;
                startInfo.WindowStyle = ProcessWindowStyle.Hidden;
                startInfo.RedirectStandardInput = true;
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;

                process = new Process();
                process.StartInfo = startInfo;
                process.EnableRaisingEvents = true;
                process.Exited += OnProcessExited;

                if (!process.Start())
                    throw new InvalidOperationException("the Codex process did not start");

                Thread stdoutThread = new Thread(ReadStandardOutput);
                stdoutThread.IsBackground = true;
                stdoutThread.Name = "Codex app-server output";

                Thread stderrThread = new Thread(DrainStandardError);
                stderrThread.IsBackground = true;
                stderrThread.Name = "Codex app-server errors";

                lock (_stateLock)
                {
                    if (_disposed)
                    {
                        _processStarting = false;
                    }
                    else
                    {
                        _process = process;
                        _stdoutThread = stdoutThread;
                        _stderrThread = stderrThread;
                        _processStarting = false;
                        ResetProtocolStateLocked();
                        _handshakeStartedUtc = DateTime.UtcNow;
                    }
                }

                if (IsDisposed())
                {
                    CloseStandardInput(process);
                    return;
                }

                stdoutThread.Start(process);
                stderrThread.Start(process);
                SendInitializeRequest();
            }
            catch (Exception ex)
            {
                lock (_stateLock)
                {
                    _processStarting = false;
                    if (object.ReferenceEquals(_process, process))
                        _process = null;
                    SetStatusLocked("Codex app-server unavailable: " + SafeMessage(ex));
                }

                if (process != null)
                {
                    CloseStandardInput(process);
                    try
                    {
                        process.Dispose();
                    }
                    catch
                    {
                    }
                }
            }
        }

        private void SendInitializeRequest()
        {
            long requestId = Interlocked.Increment(ref _nextRequestId);
            Dictionary<string, object> clientInfo = new Dictionary<string, object>();
            clientInfo["name"] = "taskbar_telemetry";
            clientInfo["title"] = "Taskbar Telemetry";
            clientInfo["version"] = "0.1.0";

            Dictionary<string, object> parameters = new Dictionary<string, object>();
            parameters["clientInfo"] = clientInfo;

            Dictionary<string, object> request = new Dictionary<string, object>();
            request["method"] = "initialize";
            request["id"] = requestId;
            request["params"] = parameters;

            lock (_stateLock)
            {
                _initializeRequestId = requestId;
            }

            if (!WriteMessage(request))
            {
                lock (_stateLock)
                {
                    _initializeRequestId = 0;
                }
            }
        }

        private void SendInitializedNotification()
        {
            Dictionary<string, object> notification = new Dictionary<string, object>();
            notification["method"] = "initialized";
            WriteMessage(notification);
        }

        private void SendAccountRequest()
        {
            long requestId = Interlocked.Increment(ref _nextRequestId);
            Dictionary<string, object> parameters = new Dictionary<string, object>();
            parameters["refreshToken"] = false;

            Dictionary<string, object> request = new Dictionary<string, object>();
            request["method"] = "account/read";
            request["id"] = requestId;
            request["params"] = parameters;

            lock (_stateLock)
            {
                _accountRequestId = requestId;
                _accountRequestSentUtc = DateTime.UtcNow;
                SetStatusLocked("Reading Codex account");
            }

            if (!WriteMessage(request))
            {
                lock (_stateLock)
                {
                    _accountRequestId = 0;
                    _accountRequestSentUtc = DateTime.MinValue;
                }
                SendRateLimitsRequest();
            }
        }

        private void SendRateLimitsRequest()
        {
            long requestId;
            Dictionary<string, object> request;

            lock (_stateLock)
            {
                if (_disposed || !_initialized || _rateLimitsRequestId != 0 || !IsProcessRunning(_process))
                    return;

                requestId = Interlocked.Increment(ref _nextRequestId);
                _rateLimitsRequestId = requestId;
                _rateLimitsRequestSentUtc = DateTime.UtcNow;
                _nextQuotaRefreshUtc = DateTime.UtcNow.AddSeconds(GetRefreshSeconds());
                SetStatusLocked("Reading Codex quota");
            }

            request = new Dictionary<string, object>();
            request["method"] = "account/rateLimits/read";
            request["id"] = requestId;

            if (!WriteMessage(request))
            {
                lock (_stateLock)
                {
                    if (_rateLimitsRequestId == requestId)
                        _rateLimitsRequestId = 0;
                }
            }
        }

        private bool WriteMessage(Dictionary<string, object> message)
        {
            Process process;
            string json;

            try
            {
                lock (_serializerLock)
                {
                    json = _serializer.Serialize(message);
                }
            }
            catch (Exception ex)
            {
                SetStatus("Cannot encode Codex request: " + SafeMessage(ex));
                return false;
            }

            lock (_stateLock)
            {
                if (_disposed || !IsProcessRunning(_process))
                    return false;
                process = _process;
            }

            try
            {
                lock (_writeLock)
                {
                    process.StandardInput.WriteLine(json);
                    process.StandardInput.Flush();
                }
                return true;
            }
            catch (Exception ex)
            {
                SetStatus("Cannot communicate with Codex app-server: " + SafeMessage(ex));
                return false;
            }
        }

        private void ReadStandardOutput(object state)
        {
            Process process = state as Process;
            if (process == null)
                return;

            try
            {
                string line;
                while (!IsDisposed() && (line = process.StandardOutput.ReadLine()) != null)
                {
                    if (line.Length != 0)
                        HandleMessage(line);
                }
            }
            catch (Exception ex)
            {
                if (!IsDisposed() && IsCurrentProcess(process))
                    SetStatus("Codex app-server output failed: " + SafeMessage(ex));
            }
        }

        private void DrainStandardError(object state)
        {
            Process process = state as Process;
            if (process == null)
                return;

            try
            {
                // Always drain stderr to prevent child-process backpressure.
                // Do not retain or display its contents because diagnostics can
                // contain local paths or other sensitive context.
                while (!IsDisposed() && process.StandardError.ReadLine() != null)
                {
                }
            }
            catch
            {
                // Stderr is diagnostic only; quota collection continues.
            }
        }

        private void HandleMessage(string json)
        {
            Dictionary<string, object> message;

            try
            {
                lock (_serializerLock)
                {
                    message = _serializer.DeserializeObject(json) as Dictionary<string, object>;
                }
                if (message == null)
                    return;
            }
            catch (Exception ex)
            {
                SetStatus("Codex returned invalid JSON: " + SafeMessage(ex));
                return;
            }

            object methodValue;
            if (TryGetValue(message, "method", out methodValue))
            {
                string method = Convert.ToString(methodValue, CultureInfo.InvariantCulture);
                if (string.Equals(method, "account/rateLimits/updated", StringComparison.Ordinal))
                {
                    Dictionary<string, object> parameters = GetDictionary(message, "params");
                    if (parameters != null)
                        ApplyRateLimits(parameters, true);
                }
                else if (string.Equals(method, "account/updated", StringComparison.Ordinal))
                {
                    ApplyAccountUpdated(GetDictionary(message, "params"));
                }
                return;
            }

            long responseId;
            if (!TryGetInt64(message, "id", out responseId))
                return;

            long initializeId;
            long accountId;
            long rateLimitsId;
            lock (_stateLock)
            {
                initializeId = _initializeRequestId;
                accountId = _accountRequestId;
                rateLimitsId = _rateLimitsRequestId;
            }

            if (responseId == initializeId && initializeId != 0)
                HandleInitializeResponse(message, responseId);
            else if (responseId == accountId && accountId != 0)
                HandleAccountResponse(message, responseId);
            else if (responseId == rateLimitsId && rateLimitsId != 0)
                HandleRateLimitsResponse(message, responseId);
        }

        private void HandleInitializeResponse(Dictionary<string, object> message, long responseId)
        {
            string error = GetProtocolError(message);
            Process processToClose = null;

            lock (_stateLock)
            {
                if (_initializeRequestId != responseId)
                    return;
                _initializeRequestId = 0;
                _handshakeStartedUtc = DateTime.MinValue;

                if (error.Length == 0)
                    _initialized = true;
                else
                {
                    SetStatusLocked("Codex initialization failed: " + error);
                    processToClose = _process;
                    _nextStartAttemptUtc = DateTime.UtcNow.AddSeconds(StartRetrySeconds);
                }
            }

            if (error.Length != 0)
            {
                CloseStandardInput(processToClose);
                return;
            }

            SendInitializedNotification();
            SendAccountRequest();
        }

        private void HandleAccountResponse(Dictionary<string, object> message, long responseId)
        {
            string error = GetProtocolError(message);
            Dictionary<string, object> result = GetDictionary(message, "result");

            lock (_stateLock)
            {
                if (_accountRequestId != responseId)
                    return;
                _accountRequestId = 0;
                _accountRequestSentUtc = DateTime.MinValue;
            }

            if (error.Length != 0)
            {
                SetStatus("Cannot read Codex account: " + error);
            }
            else if (result != null)
            {
                Dictionary<string, object> account = GetDictionary(result, "account");
                if (account != null)
                {
                    string planType = GetString(account, "planType");
                    lock (_stateLock)
                    {
                        if (planType.Length != 0)
                            _snapshot.PlanType = planType;
                    }
                }
                else
                {
                    bool requiresOpenAiAuth;
                    if (TryGetBoolean(result, "requiresOpenaiAuth", out requiresOpenAiAuth) && requiresOpenAiAuth)
                        SetStatus("Codex is not signed in");
                }
            }

            // Reading quota remains useful even when account metadata is not
            // available (for example, across app-server versions).
            SendRateLimitsRequest();
        }

        private void HandleRateLimitsResponse(Dictionary<string, object> message, long responseId)
        {
            string error = GetProtocolError(message);
            Dictionary<string, object> result = GetDictionary(message, "result");

            lock (_stateLock)
            {
                if (_rateLimitsRequestId != responseId)
                    return;
                _rateLimitsRequestId = 0;
                _nextQuotaRefreshUtc = DateTime.UtcNow.AddSeconds(GetRefreshSeconds());
            }

            if (error.Length != 0)
            {
                SetStatus("Cannot read Codex quota: " + error);
                return;
            }

            if (result == null || !ApplyRateLimits(result, false))
                SetStatus("Codex quota is unavailable for this account");
        }

        private bool ApplyRateLimits(Dictionary<string, object> container, bool sparseUpdate)
        {
            Dictionary<string, object> limits = SelectCodexRateLimits(container);
            if (limits == null)
                return false;

            object primaryValue;
            object secondaryValue;
            bool hasPrimary = TryGetValue(limits, "primary", out primaryValue);
            bool hasSecondary = TryGetValue(limits, "secondary", out secondaryValue);
            QuotaWindowMetric primary = ParseWindow(primaryValue);
            QuotaWindowMetric secondary = ParseWindow(secondaryValue);
            string planType = GetString(limits, "planType");
            string reachedType = GetString(limits, "rateLimitReachedType");

            lock (_stateLock)
            {
                if (sparseUpdate)
                {
                    if (hasPrimary)
                        _snapshot.Primary = primary;
                    if (hasSecondary)
                        _snapshot.Secondary = secondary;
                }
                else
                {
                    _snapshot.Primary = hasPrimary ? primary : null;
                    _snapshot.Secondary = hasSecondary ? secondary : null;
                }

                if (planType.Length != 0)
                    _snapshot.PlanType = planType;

                _snapshot.UpdatedAtLocal = DateTime.Now;
                if (reachedType.Length != 0)
                    _snapshot.Status = "Codex rate limit reached: " + reachedType;
                else if (_snapshot.Primary != null || _snapshot.Secondary != null)
                    _snapshot.Status = "OK (Codex app-server)";
                else
                    _snapshot.Status = "Codex quota is unavailable for this account";
            }

            return true;
        }

        private void ApplyAccountUpdated(Dictionary<string, object> parameters)
        {
            if (parameters == null)
                return;

            string planType = GetString(parameters, "planType");
            if (planType.Length == 0)
                return;

            lock (_stateLock)
            {
                _snapshot.PlanType = planType;
            }
        }

        private static Dictionary<string, object> SelectCodexRateLimits(Dictionary<string, object> container)
        {
            Dictionary<string, object> byLimitId = GetDictionary(container, "rateLimitsByLimitId");
            if (byLimitId != null)
            {
                Dictionary<string, object> codex = GetDictionary(byLimitId, "codex");
                if (codex != null)
                    return codex;
            }

            Dictionary<string, object> rateLimits = GetDictionary(container, "rateLimits");
            if (rateLimits != null && CodexRateLimitSelector.IsGeneralOrLegacy(rateLimits))
                return rateLimits;

            object ignored;
            if ((TryGetValue(container, "primary", out ignored) || TryGetValue(container, "secondary", out ignored)) &&
                CodexRateLimitSelector.IsGeneralOrLegacy(container))
                return container;

            return null;
        }

        private static QuotaWindowMetric ParseWindow(object value)
        {
            Dictionary<string, object> window = value as Dictionary<string, object>;
            if (window == null)
                return null;

            double usedPercent;
            int durationMinutes;
            long resetsAt;
            QuotaWindowMetric result = new QuotaWindowMetric();

            if (TryGetDouble(window, "usedPercent", out usedPercent))
                result.UsedPercent = usedPercent;
            if (TryGetInt32(window, "windowDurationMins", out durationMinutes))
                result.WindowDurationMinutes = durationMinutes;
            if (TryGetInt64(window, "resetsAt", out resetsAt))
                result.ResetsAtLocal = UnixSecondsToLocalTime(resetsAt);

            if (!result.UsedPercent.HasValue && !result.WindowDurationMinutes.HasValue && !result.ResetsAtLocal.HasValue)
                return null;

            return result;
        }

        private static DateTime? UnixSecondsToLocalTime(long seconds)
        {
            try
            {
                DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                return epoch.AddSeconds(seconds).ToLocalTime();
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        private static QuotaWindowMetric DiscardExpiredWindow(QuotaWindowMetric window, DateTime now)
        {
            if (window == null)
                return null;
            if (window.ResetsAtLocal.HasValue && window.ResetsAtLocal.Value <= now)
                return null;
            return window;
        }

        private void OnRefreshTimer(object state)
        {
            try
            {
                Refresh();
            }
            catch (Exception ex)
            {
                SetStatus("Codex quota refresh failed: " + SafeMessage(ex));
            }
        }

        private void OnProcessExited(object sender, EventArgs eventArgs)
        {
            Process process = sender as Process;
            if (process == null)
                return;

            int exitCode = -1;
            try
            {
                exitCode = process.ExitCode;
            }
            catch
            {
            }

            lock (_stateLock)
            {
                if (_disposed || !object.ReferenceEquals(_process, process))
                    return;

                _process = null;
                ResetProtocolStateLocked();
                _snapshot.Primary = null;
                _snapshot.Secondary = null;
                _snapshot.UpdatedAtLocal = null;
                _nextStartAttemptUtc = DateTime.UtcNow.AddSeconds(StartRetrySeconds);
                SetStatusLocked("Codex app-server exited (code " + exitCode.ToString(CultureInfo.InvariantCulture) + ")");
            }

            try
            {
                process.Dispose();
            }
            catch
            {
            }
        }

        private void ResetProtocolStateLocked()
        {
            _initialized = false;
            _initializeRequestId = 0;
            _accountRequestId = 0;
            _accountRequestSentUtc = DateTime.MinValue;
            _rateLimitsRequestId = 0;
            _handshakeStartedUtc = DateTime.MinValue;
            _rateLimitsRequestSentUtc = DateTime.MinValue;
            _nextQuotaRefreshUtc = DateTime.MinValue;
        }

        private void SetStatus(string status)
        {
            lock (_stateLock)
            {
                SetStatusLocked(status);
            }
        }

        private void SetStatusLocked(string status)
        {
            _snapshot.Status = status ?? string.Empty;
        }

        private bool IsDisposed()
        {
            lock (_stateLock)
            {
                return _disposed;
            }
        }

        private bool IsCurrentProcess(Process process)
        {
            lock (_stateLock)
            {
                return object.ReferenceEquals(_process, process);
            }
        }

        private static bool IsProcessRunning(Process process)
        {
            if (process == null)
                return false;
            try
            {
                return !process.HasExited;
            }
            catch
            {
                return false;
            }
        }

        private static void CloseStandardInput(Process process)
        {
            if (process == null)
                return;
            try
            {
                process.StandardInput.Close();
            }
            catch
            {
            }
        }

        private static void JoinThread(Thread thread)
        {
            if (thread == null || object.ReferenceEquals(thread, Thread.CurrentThread))
                return;
            try
            {
                thread.Join(500);
            }
            catch
            {
            }
        }

        private int GetRefreshSeconds()
        {
            return Math.Max(1, _settings.CodexRefreshSeconds);
        }

        private TimeSpan GetRequestTimeout()
        {
            int seconds = Math.Max(15, Math.Min(120, GetRefreshSeconds() * 2));
            return TimeSpan.FromSeconds(seconds);
        }

        private static string NormalizeCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
                return string.Empty;

            string result = command.Trim();
            if (result.Length >= 2 && result[0] == '"' && result[result.Length - 1] == '"')
                result = result.Substring(1, result.Length - 2);
            return result;
        }

        private static string GetProtocolError(Dictionary<string, object> message)
        {
            Dictionary<string, object> error = GetDictionary(message, "error");
            if (error == null)
                return string.Empty;

            string text = GetString(error, "message");
            return text.Length == 0 ? "unknown protocol error" : SafeText(text);
        }

        private static Dictionary<string, object> GetDictionary(Dictionary<string, object> source, string key)
        {
            object value;
            if (source == null || !TryGetValue(source, key, out value))
                return null;
            return value as Dictionary<string, object>;
        }

        private static string GetString(Dictionary<string, object> source, string key)
        {
            object value;
            if (source == null || !TryGetValue(source, key, out value) || value == null)
                return string.Empty;
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private static bool TryGetValue(Dictionary<string, object> source, string key, out object value)
        {
            if (source != null && source.TryGetValue(key, out value))
                return true;

            if (source != null)
            {
                foreach (KeyValuePair<string, object> pair in source)
                {
                    if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                    {
                        value = pair.Value;
                        return true;
                    }
                }
            }

            value = null;
            return false;
        }

        private static bool TryGetDouble(Dictionary<string, object> source, string key, out double result)
        {
            object value;
            if (!TryGetValue(source, key, out value) || value == null)
            {
                result = 0.0;
                return false;
            }

            try
            {
                result = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return !double.IsNaN(result) && !double.IsInfinity(result);
            }
            catch
            {
                result = 0.0;
                return false;
            }
        }

        private static bool TryGetInt32(Dictionary<string, object> source, string key, out int result)
        {
            long value;
            if (TryGetInt64(source, key, out value) && value >= int.MinValue && value <= int.MaxValue)
            {
                result = (int)value;
                return true;
            }

            result = 0;
            return false;
        }

        private static bool TryGetInt64(Dictionary<string, object> source, string key, out long result)
        {
            object value;
            if (!TryGetValue(source, key, out value) || value == null)
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

        private static bool TryGetBoolean(Dictionary<string, object> source, string key, out bool result)
        {
            object value;
            if (!TryGetValue(source, key, out value) || value == null)
            {
                result = false;
                return false;
            }

            try
            {
                result = Convert.ToBoolean(value, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                result = false;
                return false;
            }
        }

        private static string SafeMessage(Exception exception)
        {
            if (exception == null)
                return "unknown error";
            return SafeText(exception.Message);
        }

        private static string SafeText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "unknown error";

            StringBuilder builder = new StringBuilder(Math.Min(text.Length, 160));
            int index;
            for (index = 0; index < text.Length && builder.Length < 160; index++)
            {
                char character = text[index];
                if (character == '\r' || character == '\n' || character == '\t')
                    builder.Append(' ');
                else if (!char.IsControl(character))
                    builder.Append(character);
            }
            return builder.ToString().Trim();
        }

        private static CodexMetric CloneMetric(CodexMetric source)
        {
            CodexMetric clone = new CodexMetric();
            clone.Primary = CloneWindow(source.Primary);
            clone.Secondary = CloneWindow(source.Secondary);
            clone.PlanType = source.PlanType;
            clone.Status = source.Status;
            clone.UpdatedAtLocal = source.UpdatedAtLocal;
            return clone;
        }

        private static QuotaWindowMetric CloneWindow(QuotaWindowMetric source)
        {
            if (source == null)
                return null;

            QuotaWindowMetric clone = new QuotaWindowMetric();
            clone.UsedPercent = source.UsedPercent;
            clone.WindowDurationMinutes = source.WindowDurationMinutes;
            clone.ResetsAtLocal = source.ResetsAtLocal;
            return clone;
        }
    }
}
