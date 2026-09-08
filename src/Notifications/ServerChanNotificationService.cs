using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace TaskbarTelemetry
{
    internal sealed class ServerChanNotificationService : IDisposable
    {
        private const int RequestTimeoutMilliseconds = 10000;

        private bool enabled;
        private readonly int thresholdPercent;
        private readonly int dailyRequestLimit;
        private readonly string statePath;
        private readonly object syncRoot;
        private readonly object requestLock;
        private readonly object serializerLock;
        private readonly AutoResetEvent workEvent;
        private readonly ManualResetEvent stopEvent;
        private readonly JavaScriptSerializer serializer;
        private readonly QuotaNotificationTracker tracker;

        private Thread workerThread;
        private HttpWebRequest activeRequest;
        private bool testPending;
        private bool quotaObserved;
        private bool disposed;
        private string status;

        internal ServerChanNotificationService(AppSettings settings)
        {
            if (settings == null)
                throw new ArgumentNullException("settings");

            enabled = settings.NotificationEnabled;
            thresholdPercent = Math.Max(1, Math.Min(100, settings.NotificationThresholdPercent));
            dailyRequestLimit = Math.Max(0, settings.NotificationDailyRequestLimit);
            statePath = BuildStatePath();
            syncRoot = new object();
            requestLock = new object();
            serializerLock = new object();
            workEvent = new AutoResetEvent(false);
            stopEvent = new ManualResetEvent(false);
            serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = 1024 * 1024;

            QuotaNotificationState persistedState = enabled
                ? LoadState()
                : new QuotaNotificationState();
            tracker = new QuotaNotificationTracker(thresholdPercent, persistedState);
            status = enabled
                ? "已启用，等待 Codex 主额度数据"
                : "通知未启用";

            if (enabled)
            {
                workerThread = new Thread(WorkerLoop);
                workerThread.IsBackground = true;
                workerThread.Name = "TaskbarTelemetry ServerChan sender";
                workerThread.Start();
            }
        }

        internal void Observe(CodexMetric codex)
        {
            if (!enabled || disposed)
                return;

            bool wakeWorker = false;
            lock (syncRoot)
            {
                QuotaWindowMetric window = codex == null ? null : codex.Primary;
                QuotaNotificationObservation observation = tracker.Observe(window, DateTime.Now);
                if (!observation.HasData)
                {
                    if (!tracker.HasPendingNotification && !testPending)
                        status = "已启用，等待 Codex 主额度数据";
                    return;
                }
                quotaObserved = true;

                if (observation.StateChanged)
                    SaveStateLocked();

                string key;
                string keyError;
                bool hasKey = TryGetSendKey(out key, out keyError);
                if (!hasKey)
                {
                    status = keyError;
                    if (observation.PendingChanged)
                    {
                        tracker.SuppressPending(tracker.GetPendingNotification());
                        SaveStateLocked();
                    }
                }
                else if (observation.BaselineEstablished)
                {
                    status = BuildBaselineStatus(observation, false);
                }
                else if (observation.WindowReset)
                {
                    status = BuildBaselineStatus(observation, true);
                }
                else if (observation.PendingChanged)
                {
                    PendingQuotaNotification pending = tracker.GetPendingNotification();
                    status = pending == null
                        ? "额度提醒已布防"
                        : "已跨过 " + pending.ToThresholdPercent.ToString(CultureInfo.InvariantCulture) +
                          "% 档位，等待通知推送";
                }

                wakeWorker = observation.PendingChanged ||
                    (tracker.HasPendingNotification && tracker.GetNextAttemptUtc() <= DateTime.UtcNow);
            }

            if (wakeWorker)
                workEvent.Set();
        }

        internal NotificationMetric GetMetric()
        {
            lock (syncRoot)
            {
                if (enabled)
                    ResetDailyBudgetIfNeededLocked(DateTime.Now);
                NotificationMetric metric = new NotificationMetric();
                metric.Enabled = enabled;
                metric.Channel = ServerChanEndpoint.GetChannel(SendKeyStore.Read());
                metric.Status = status;
                metric.LastSentAtLocal = tracker.GetLastSentAtLocal();
                return metric;
            }
        }

        internal string QueueTest()
        {
            if (!enabled)
                return "通知尚未启用。请把 notification.enabled 设置为 true 后重启程序。";

            string key;
            string keyError;
            if (!TryGetSendKey(out key, out keyError))
            {
                lock (syncRoot)
                {
                    status = keyError;
                }
                return keyError + "。请打开“通知与隐私设置”重新保存。";
            }

            lock (syncRoot)
            {
                ResetDailyBudgetIfNeededLocked(DateTime.Now);
                if (IsDailyBudgetExhaustedLocked())
                {
                    status = BuildDailyLimitStatusLocked();
                    return status;
                }

                testPending = true;
                status = "通知测试消息已排队";
            }
            workEvent.Set();
            return "测试消息已排队；发送结果会显示在鼠标悬停提示中。";
        }

        internal void Disable()
        {
            HttpWebRequest request;
            Thread thread;
            lock (syncRoot)
            {
                if (!enabled)
                    return;
                enabled = false;
                testPending = false;
                status = "通知已停用";
                thread = workerThread;
            }

            stopEvent.Set();
            workEvent.Set();
            lock (requestLock)
            {
                request = activeRequest;
            }
            if (request != null)
            {
                try
                {
                    request.Abort();
                }
                catch
                {
                }
            }

            if (thread != null && !object.ReferenceEquals(thread, Thread.CurrentThread))
            {
                try
                {
                    thread.Join(5000);
                }
                catch
                {
                }
            }
        }

        public void Dispose()
        {
            Thread thread;
            lock (syncRoot)
            {
                if (disposed)
                    return;
                disposed = true;
                thread = workerThread;
            }

            stopEvent.Set();
            workEvent.Set();
            lock (requestLock)
            {
                if (activeRequest != null)
                {
                    try
                    {
                        activeRequest.Abort();
                    }
                    catch
                    {
                    }
                }
            }

            if (thread != null && !object.ReferenceEquals(thread, Thread.CurrentThread))
            {
                try
                {
                    thread.Join(5000);
                }
                catch
                {
                }
            }

            if (thread == null || !thread.IsAlive)
            {
                workEvent.Dispose();
                stopEvent.Dispose();
            }
        }

        private void WorkerLoop()
        {
            WaitHandle[] handles = new WaitHandle[] { stopEvent, workEvent };
            while (!stopEvent.WaitOne(0))
            {
                int waitMilliseconds = GetWaitMilliseconds();
                int signaled = WaitHandle.WaitAny(handles, waitMilliseconds);
                if (signaled == 0 || stopEvent.WaitOne(0))
                    break;

                WorkItem item;
                while (TryTakeWork(out item))
                {
                    SendResult result;
                    try
                    {
                        result = Send(item);
                    }
                    catch (Exception exception)
                    {
                        result = SendResult.TransientFailure(
                            "通知模块异常：" + SafeError(exception, string.Empty));
                    }
                    HandleSendResult(item, result);
                    if (stopEvent.WaitOne(0))
                        return;
                }
            }
        }

        private int GetWaitMilliseconds()
        {
            lock (syncRoot)
            {
                if (!enabled || disposed)
                    return Timeout.Infinite;
                ResetDailyBudgetIfNeededLocked(DateTime.Now);
                if (testPending)
                    return 0;
                if (!quotaObserved)
                    return Timeout.Infinite;
                if (!tracker.HasPendingNotification)
                    return Timeout.Infinite;
                if (IsDailyBudgetExhaustedLocked())
                {
                    SuppressPendingForDailyLimitLocked();
                    return Timeout.Infinite;
                }

                DateTime nextAttemptUtc = tracker.GetNextAttemptUtc();
                if (nextAttemptUtc == DateTime.MinValue || nextAttemptUtc <= DateTime.UtcNow)
                    return 0;
                return ToWaitMilliseconds(nextAttemptUtc - DateTime.UtcNow);
            }
        }

        private bool TryTakeWork(out WorkItem item)
        {
            item = null;
            lock (syncRoot)
            {
                if (!enabled || disposed)
                    return false;
                ResetDailyBudgetIfNeededLocked(DateTime.Now);
                string key;
                string keyError;
                if (!TryGetSendKey(out key, out keyError))
                {
                    testPending = false;
                    tracker.SuppressPending(tracker.GetPendingNotification());
                    status = keyError;
                    SaveStateLocked();
                    return false;
                }
                if (IsDailyBudgetExhaustedLocked())
                {
                    testPending = false;
                    SuppressPendingForDailyLimitLocked();
                    return false;
                }

                if (testPending)
                {
                    testPending = false;
                    item = WorkItem.CreateTest();
                    RegisterAttemptLocked();
                    return true;
                }

                if (!quotaObserved)
                    return false;
                if (!tracker.HasPendingNotification)
                    return false;
                DateTime nextAttemptUtc = tracker.GetNextAttemptUtc();
                if (nextAttemptUtc != DateTime.MinValue && nextAttemptUtc > DateTime.UtcNow)
                    return false;

                PendingQuotaNotification pending = tracker.GetPendingNotification();
                if (pending == null)
                    return false;
                if (IsPendingExpired(pending, DateTime.Now))
                {
                    tracker.SuppressPending(pending);
                    status = "已丢弃过期额度档位；等待当前窗口的新档位";
                    SaveStateLocked();
                    return false;
                }
                item = WorkItem.CreateQuota(pending);
                RegisterAttemptLocked();
                return true;
            }
        }

        private SendResult Send(WorkItem item)
        {
            string key;
            string keyError;
            if (!TryGetSendKey(out key, out keyError))
                return SendResult.ConfigurationFailure(keyError);

            string title;
            string description;
            if (item.IsTest)
            {
                title = "TaskbarTelemetry 通知测试";
                description = ServerChanEndpoint.GetChannel(key) + " 接口测试。\n\n以后 Codex 主额度每跨过 " +
                    thresholdPercent.ToString(CultureInfo.InvariantCulture) + "% 档位时会自动推送。";
            }
            else
            {
                title = BuildQuotaTitle(item.Pending);
                description = BuildQuotaDescription(item.Pending);
            }

            return SendRequest(key, title, description);
        }

        private SendResult SendRequest(string sendKey, string title, string description)
        {
            HttpWebRequest request = null;

            try
            {
                Uri endpoint;
                string channel;
                if (!ServerChanEndpoint.TryCreate(sendKey, out endpoint, out channel))
                    return SendResult.ConfigurationFailure("SendKey 格式无效，请重新保存通知设置");
                string form = "title=" + Uri.EscapeDataString(title) +
                    "&desp=" + Uri.EscapeDataString(description);
                byte[] body = Encoding.UTF8.GetBytes(form);
                request = (HttpWebRequest)WebRequest.Create(endpoint);
                request.Method = "POST";
                request.ContentType = "application/x-www-form-urlencoded; charset=utf-8";
                request.ContentLength = body.Length;
                request.Timeout = RequestTimeoutMilliseconds;
                request.ReadWriteTimeout = RequestTimeoutMilliseconds;
                request.AllowAutoRedirect = false;
                request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                request.UserAgent = "TaskbarTelemetry/1.0";

                lock (requestLock)
                {
                    if (!enabled || disposed)
                        return SendResult.TransientFailure("程序正在退出");
                    activeRequest = request;
                }

                using (Stream requestStream = request.GetRequestStream())
                {
                    requestStream.Write(body, 0, body.Length);
                }

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    if (response.StatusCode != HttpStatusCode.OK)
                        return SendResult.ProviderFailure("HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));

                    string responseText;
                    using (Stream responseStream = response.GetResponseStream())
                    using (StreamReader reader = new StreamReader(responseStream ?? Stream.Null, Encoding.UTF8, true, 1024, false))
                    {
                        responseText = reader.ReadToEnd();
                    }
                    return ParseResponse(responseText, sendKey);
                }
            }
            catch (WebException exception)
            {
                HttpWebResponse response = exception.Response as HttpWebResponse;
                if (response != null)
                {
                    using (response)
                    {
                        int statusCode = (int)response.StatusCode;
                        bool providerFailure = statusCode >= 400 && statusCode < 500;
                        return providerFailure
                            ? SendResult.ProviderFailure("HTTP " + statusCode.ToString(CultureInfo.InvariantCulture))
                            : SendResult.TransientFailure("HTTP " + statusCode.ToString(CultureInfo.InvariantCulture));
                    }
                }
                return SendResult.TransientFailure(SafeError(exception, sendKey));
            }
            catch (Exception exception)
            {
                return SendResult.TransientFailure(SafeError(exception, sendKey));
            }
            finally
            {
                lock (requestLock)
                {
                    if (object.ReferenceEquals(activeRequest, request))
                        activeRequest = null;
                }
            }
        }

        internal SendResult ParseResponse(string responseText, string sendKey)
        {
            try
            {
                Dictionary<string, object> response;
                lock (serializerLock)
                {
                    response = serializer.DeserializeObject(responseText) as Dictionary<string, object>;
                }
                if (response == null || !response.ContainsKey("code"))
                    return SendResult.TransientFailure("响应中缺少 code");

                int code;
                if (response["code"] == null || !int.TryParse(
                    Convert.ToString(response["code"], CultureInfo.InvariantCulture),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out code))
                    return SendResult.TransientFailure("响应中的 code 无效");
                if (code == 0)
                    return SendResult.Successful();

                string message = response.ContainsKey("message")
                    ? Convert.ToString(response["message"], CultureInfo.InvariantCulture)
                    : "Server酱返回 code " + code.ToString(CultureInfo.InvariantCulture);
                return SendResult.ProviderFailure(Sanitize(message, sendKey));
            }
            catch (Exception exception)
            {
                return SendResult.TransientFailure("无法解析 Server酱响应：" + SafeError(exception, sendKey));
            }
        }

        private void HandleSendResult(WorkItem item, SendResult result)
        {
            lock (syncRoot)
            {
                if (!enabled)
                {
                    status = "通知已停用";
                    return;
                }
                if (!item.IsTest && (item.Pending == null ||
                    item.Pending.Generation != tracker.State.Generation))
                {
                    status = "额度窗口已更新；已忽略旧窗口的推送结果";
                    if (tracker.HasPendingNotification)
                        workEvent.Set();
                    return;
                }

                if (result.Success)
                {
                    DateTime sentAt = DateTime.Now;
                    if (!item.IsTest)
                    {
                        if (!tracker.MarkSent(item.Pending, sentAt))
                        {
                            status = "额度状态已更新；已忽略过时的推送结果";
                            return;
                        }
                    }
                    else
                        tracker.MarkTestSent(sentAt);

                    status = item.IsTest
                        ? "测试消息已被服务接收（" + sentAt.ToString("HH:mm:ss", CultureInfo.CurrentCulture) + "）"
                        : "已推送 " + item.Pending.ToThresholdPercent.ToString(CultureInfo.InvariantCulture) +
                          "% 档位（" + sentAt.ToString("HH:mm:ss", CultureInfo.CurrentCulture) + "）";
                    SaveStateLocked();
                    if (tracker.HasPendingNotification)
                        workEvent.Set();
                    return;
                }

                if (item.IsTest)
                {
                    status = "通知测试发送失败：" + result.Error;
                    SaveStateLocked();
                    return;
                }

                if (result.ConfigurationError || result.ProviderRejected || tracker.State.FailedAttempts >= 1)
                {
                    tracker.SuppressPending(item.Pending);
                    status = "通知推送失败，本档位不再重试：" + result.Error;
                    SaveStateLocked();
                    return;
                }

                DateTime nextAttemptUtc = DateTime.UtcNow.Add(GetRetryDelay(1));
                tracker.MarkFailed(nextAttemptUtc);
                status = "通知推送暂时失败，稍后重试一次：" + result.Error;
                SaveStateLocked();
            }
        }

        private void RegisterAttemptLocked()
        {
            ResetDailyBudgetIfNeededLocked(DateTime.Now);
            tracker.State.DailyAttemptCount = Math.Min(1000000, tracker.State.DailyAttemptCount + 1);
            SaveStateLocked();
        }

        private void ResetDailyBudgetIfNeededLocked(DateTime nowLocal)
        {
            int dateKey = GetLocalDateKey(nowLocal);
            if (tracker.State.DailyAttemptDateKey == dateKey && tracker.State.DailyAttemptCount >= 0)
                return;
            tracker.State.DailyAttemptDateKey = dateKey;
            tracker.State.DailyAttemptCount = 0;
            if (!string.IsNullOrWhiteSpace(status) &&
                status.StartsWith("今日本地推送请求已达", StringComparison.Ordinal))
                status = "今日本地推送额度已重置；等待下一个额度档位";
            SaveStateLocked();
        }

        private bool IsDailyBudgetExhaustedLocked()
        {
            return IsDailyRequestLimitReached(tracker.State.DailyAttemptCount, dailyRequestLimit);
        }

        private string BuildDailyLimitStatusLocked()
        {
            return "今日本地推送请求已达 " + tracker.State.DailyAttemptCount.ToString(CultureInfo.InvariantCulture) +
                "/" + dailyRequestLimit.ToString(CultureInfo.InvariantCulture) + "；本日后续档位仅记录不发送";
        }

        private void SuppressPendingForDailyLimitLocked()
        {
            PendingQuotaNotification pending = tracker.GetPendingNotification();
            if (pending != null)
                tracker.SuppressPending(pending);
            status = BuildDailyLimitStatusLocked();
            SaveStateLocked();
        }

        private bool TryGetSendKey(out string key, out string error)
        {
            key = string.Empty;
            error = string.Empty;
            try
            {
                key = SendKeyStore.Read();
            }
            catch
            {
                key = string.Empty;
            }

            key = key == null ? string.Empty : key.Trim();
            if (key.Length == 0)
            {
                error = "未找到已保存的 Server酱 SendKey";
                return false;
            }
            if (!ServerChanEndpoint.IsValid(key))
            {
                error = "SendKey 格式无效：Server酱³ 为 sctp数字t密钥，Turbo 为 SCT 开头";
                key = string.Empty;
                return false;
            }
            return true;
        }

        private QuotaNotificationState LoadState()
        {
            try
            {
                if (!File.Exists(statePath))
                    return new QuotaNotificationState();
                string json = File.ReadAllText(statePath, Encoding.UTF8);
                lock (serializerLock)
                {
                    return serializer.Deserialize<QuotaNotificationState>(json) ?? new QuotaNotificationState();
                }
            }
            catch
            {
                return new QuotaNotificationState();
            }
        }

        private void SaveStateLocked()
        {
            if (!enabled)
                return;
            try
            {
                string directory = Path.GetDirectoryName(statePath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                string temporaryPath = statePath + ".tmp";
                string json;
                lock (serializerLock)
                {
                    json = serializer.Serialize(tracker.State);
                }
                File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
                if (File.Exists(statePath))
                {
                    try
                    {
                        File.Replace(temporaryPath, statePath, null, true);
                    }
                    catch
                    {
                        File.Copy(temporaryPath, statePath, true);
                        File.Delete(temporaryPath);
                    }
                }
                else
                {
                    File.Move(temporaryPath, statePath);
                }
            }
            catch
            {
                // Notification state is best-effort; quota sampling must continue.
            }
        }

        internal static string BuildQuotaTitle(PendingQuotaNotification pending)
        {
            return "Codex 已用 " + FormatPercent(pending.UsedPercent) +
                "，剩余 " + FormatPercent(pending.RemainingPercent);
        }

        internal static string BuildQuotaDescription(PendingQuotaNotification pending)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("Codex 主额度已跨过 ");
            if (pending.FromBucket == pending.ToBucket)
            {
                builder.Append(pending.ToThresholdPercent.ToString(CultureInfo.InvariantCulture));
                builder.Append("%");
            }
            else
            {
                builder.Append(pending.FromThresholdPercent.ToString(CultureInfo.InvariantCulture));
                builder.Append("%–");
                builder.Append(pending.ToThresholdPercent.ToString(CultureInfo.InvariantCulture));
                builder.Append("%");
            }
            builder.Append(" 档位。\n\n");
            builder.Append("- 当前已用：");
            builder.Append(FormatPercent(pending.UsedPercent));
            builder.Append("\n- 当前剩余：");
            builder.Append(FormatPercent(pending.RemainingPercent));
            if (pending.WindowDurationMinutes.HasValue)
            {
                builder.Append("\n- 额度窗口：");
                builder.Append(FormatDuration(pending.WindowDurationMinutes.Value));
            }
            if (pending.ResetsAtLocal.HasValue)
            {
                builder.Append("\n- 重置时间：");
                builder.Append(pending.ResetsAtLocal.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture));
            }
            return builder.ToString();
        }

        private static string BuildBaselineStatus(QuotaNotificationObservation observation, bool reset)
        {
            string prefix = reset ? "额度窗口已重置" : "已建立当前额度基线";
            if (observation.NextThresholdPercent <= 0)
                return prefix + "；当前窗口已到 100%";
            return prefix + "；跨过 " + observation.NextThresholdPercent.ToString(CultureInfo.InvariantCulture) +
                "% 时推送";
        }

        private static string FormatPercent(double value)
        {
            value = Math.Max(0.0, Math.Min(100.0, value));
            return value.ToString("0", CultureInfo.InvariantCulture) + "%";
        }

        private static string FormatDuration(int minutes)
        {
            if (minutes >= 1440 && minutes % 1440 == 0)
                return (minutes / 1440).ToString(CultureInfo.InvariantCulture) + " 天";
            if (minutes >= 60 && minutes % 60 == 0)
                return (minutes / 60).ToString(CultureInfo.InvariantCulture) + " 小时";
            return minutes.ToString(CultureInfo.InvariantCulture) + " 分钟";
        }

        private static TimeSpan GetRetryDelay(int attempt)
        {
            return attempt <= 1 ? TimeSpan.FromMinutes(2) : TimeSpan.FromHours(1);
        }

        internal static bool IsDailyRequestLimitReached(int attemptCount, int limit)
        {
            return limit > 0 && attemptCount >= limit;
        }

        internal static int GetLocalDateKey(DateTime localTime)
        {
            return localTime.Year * 10000 + localTime.Month * 100 + localTime.Day;
        }

        internal static bool IsPendingExpired(PendingQuotaNotification pending, DateTime nowLocal)
        {
            return pending != null && pending.ResetsAtLocal.HasValue &&
                pending.ResetsAtLocal.Value <= nowLocal;
        }

        private static int ToWaitMilliseconds(TimeSpan delay)
        {
            if (delay <= TimeSpan.Zero)
                return 0;
            return (int)Math.Min(60000.0, Math.Max(1.0, delay.TotalMilliseconds));
        }

        private static string BuildStatePath()
        {
            return NotificationConsent.GetQuotaStatePath();
        }

        private static string SafeError(Exception exception, string sendKey)
        {
            if (exception == null)
                return "未知错误";
            string message = string.IsNullOrWhiteSpace(exception.Message)
                ? exception.GetType().Name
                : exception.Message;
            return Sanitize(message, sendKey);
        }

        private static string Sanitize(string value, string sendKey)
        {
            string result = value ?? string.Empty;
            if (!string.IsNullOrEmpty(sendKey))
            {
                result = result.Replace(sendKey, "<SendKey>");
                try
                {
                    result = result.Replace(Uri.EscapeDataString(sendKey), "<SendKey>");
                }
                catch
                {
                }
            }
            result = result.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return result.Length > 180 ? result.Substring(0, 180) : result;
        }

        private sealed class WorkItem
        {
            internal bool IsTest { get; private set; }
            internal PendingQuotaNotification Pending { get; private set; }

            internal static WorkItem CreateTest()
            {
                WorkItem item = new WorkItem();
                item.IsTest = true;
                return item;
            }

            internal static WorkItem CreateQuota(PendingQuotaNotification pending)
            {
                WorkItem item = new WorkItem();
                item.Pending = pending;
                return item;
            }
        }

        internal sealed class SendResult
        {
            internal bool Success { get; private set; }
            internal bool ProviderRejected { get; private set; }
            internal bool ConfigurationError { get; private set; }
            internal string Error { get; private set; }

            private SendResult()
            {
                Error = string.Empty;
            }

            internal static SendResult Successful()
            {
                SendResult result = new SendResult();
                result.Success = true;
                return result;
            }

            internal static SendResult ProviderFailure(string error)
            {
                SendResult result = new SendResult();
                result.ProviderRejected = true;
                result.Error = string.IsNullOrWhiteSpace(error) ? "Server酱拒绝了请求" : error;
                return result;
            }

            internal static SendResult ConfigurationFailure(string error)
            {
                SendResult result = new SendResult();
                result.ConfigurationError = true;
                result.Error = error ?? "通知配置错误";
                return result;
            }

            internal static SendResult TransientFailure(string error)
            {
                SendResult result = new SendResult();
                result.Error = string.IsNullOrWhiteSpace(error) ? "网络请求失败" : error;
                return result;
            }
        }
    }
}
