using System;

namespace TaskbarTelemetry
{
    internal sealed class QuotaNotificationState
    {
        public int SchemaVersion { get; set; }
        public bool Initialized { get; set; }
        public int ThresholdPercent { get; set; }
        public int Generation { get; set; }
        public double LastObservedUsedPercent { get; set; }
        public long WindowResetsAtUtcTicks { get; set; }
        public int WindowDurationMinutes { get; set; }
        public int HandledBucket { get; set; }
        public int PendingFromBucket { get; set; }
        public int PendingToBucket { get; set; }
        public double PendingObservedUsedPercent { get; set; }
        public long PendingWindowResetsAtUtcTicks { get; set; }
        public int PendingWindowDurationMinutes { get; set; }
        public int FailedAttempts { get; set; }
        public long NextAttemptUtcTicks { get; set; }
        public long LastSentAtLocalTicks { get; set; }
        public int DailyAttemptDateKey { get; set; }
        public int DailyAttemptCount { get; set; }

        public QuotaNotificationState()
        {
            SchemaVersion = 1;
            LastObservedUsedPercent = -1.0;
        }
    }

    internal sealed class QuotaNotificationObservation
    {
        public bool HasData { get; set; }
        public bool StateChanged { get; set; }
        public bool PendingChanged { get; set; }
        public bool BaselineEstablished { get; set; }
        public bool WindowReset { get; set; }
        public int NextThresholdPercent { get; set; }
        public double UsedPercent { get; set; }
    }

    internal sealed class PendingQuotaNotification
    {
        public int Generation { get; set; }
        public int FromBucket { get; set; }
        public int ToBucket { get; set; }
        public int ThresholdPercent { get; set; }
        public double UsedPercent { get; set; }
        public DateTime? ResetsAtLocal { get; set; }
        public int? WindowDurationMinutes { get; set; }

        public int FromThresholdPercent
        {
            get { return FromBucket * ThresholdPercent; }
        }

        public int ToThresholdPercent
        {
            get { return Math.Min(100, ToBucket * ThresholdPercent); }
        }

        public double RemainingPercent
        {
            get { return Math.Max(0.0, Math.Min(100.0, 100.0 - UsedPercent)); }
        }
    }

    internal sealed class QuotaNotificationTracker
    {
        private const double PercentageEpsilon = 0.000001;
        private readonly int thresholdPercent;
        private readonly QuotaNotificationState state;

        internal QuotaNotificationTracker(int thresholdPercent, QuotaNotificationState state)
        {
            this.thresholdPercent = Math.Max(1, Math.Min(100, thresholdPercent));
            this.state = state ?? new QuotaNotificationState();
            NormalizeState();
        }

        internal QuotaNotificationState State
        {
            get { return state; }
        }

        internal QuotaNotificationObservation Observe(QuotaWindowMetric window, DateTime nowLocal)
        {
            QuotaNotificationObservation observation = new QuotaNotificationObservation();
            if (window == null || !window.UsedPercent.HasValue ||
                double.IsNaN(window.UsedPercent.Value) || double.IsInfinity(window.UsedPercent.Value))
                return observation;

            double usedPercent = Math.Max(0.0, Math.Min(100.0, window.UsedPercent.Value));
            int currentBucket = GetBucket(usedPercent);
            long resetTicks = ToUtcTicks(window.ResetsAtLocal);
            int durationMinutes = window.WindowDurationMinutes.HasValue
                ? Math.Max(0, window.WindowDurationMinutes.Value)
                : 0;

            observation.HasData = true;
            observation.UsedPercent = usedPercent;

            if (!state.Initialized || state.ThresholdPercent != thresholdPercent)
            {
                EstablishBaseline(currentBucket, usedPercent, resetTicks, durationMinutes, false);
                observation.StateChanged = true;
                observation.BaselineEstablished = true;
                observation.NextThresholdPercent = GetNextThresholdPercent(currentBucket);
                return observation;
            }

            if (IsNewWindow(usedPercent, resetTicks, nowLocal))
            {
                EstablishBaseline(currentBucket, usedPercent, resetTicks, durationMinutes, true);
                observation.StateChanged = true;
                observation.WindowReset = true;
                observation.NextThresholdPercent = GetNextThresholdPercent(currentBucket);
                return observation;
            }

            bool metadataChanged = state.LastObservedUsedPercent != usedPercent ||
                state.WindowResetsAtUtcTicks != resetTicks ||
                state.WindowDurationMinutes != durationMinutes;
            state.LastObservedUsedPercent = usedPercent;
            state.WindowResetsAtUtcTicks = resetTicks;
            state.WindowDurationMinutes = durationMinutes;

            int coveredBucket = Math.Max(state.HandledBucket, state.PendingToBucket);
            if (currentBucket > coveredBucket)
            {
                if (state.PendingToBucket <= state.HandledBucket)
                    state.PendingFromBucket = state.HandledBucket + 1;
                state.PendingToBucket = currentBucket;
                state.PendingObservedUsedPercent = usedPercent;
                state.PendingWindowResetsAtUtcTicks = resetTicks;
                state.PendingWindowDurationMinutes = durationMinutes;
                state.FailedAttempts = 0;
                state.NextAttemptUtcTicks = 0;
                observation.PendingChanged = true;
                metadataChanged = true;
            }

            observation.StateChanged = metadataChanged;
            observation.NextThresholdPercent = GetNextThresholdPercent(
                Math.Max(currentBucket, Math.Max(state.HandledBucket, state.PendingToBucket)));
            return observation;
        }

        internal bool HasPendingNotification
        {
            get
            {
                return state.Initialized && state.PendingFromBucket > 0 &&
                    state.PendingToBucket >= state.PendingFromBucket;
            }
        }

        internal PendingQuotaNotification GetPendingNotification()
        {
            if (!HasPendingNotification)
                return null;

            PendingQuotaNotification pending = new PendingQuotaNotification();
            pending.Generation = state.Generation;
            pending.FromBucket = state.PendingFromBucket;
            pending.ToBucket = state.PendingToBucket;
            pending.ThresholdPercent = thresholdPercent;
            pending.UsedPercent = state.PendingObservedUsedPercent;
            pending.ResetsAtLocal = FromUtcTicks(state.PendingWindowResetsAtUtcTicks);
            pending.WindowDurationMinutes = state.PendingWindowDurationMinutes > 0
                ? (int?)state.PendingWindowDurationMinutes
                : null;
            return pending;
        }

        internal bool MarkSent(PendingQuotaNotification sent, DateTime sentAtLocal)
        {
            if (!MarkHandled(sent))
                return false;
            state.FailedAttempts = 0;
            state.NextAttemptUtcTicks = 0;
            state.LastSentAtLocalTicks = sentAtLocal.Ticks;
            return true;
        }

        internal bool SuppressPending(PendingQuotaNotification pending)
        {
            return MarkHandled(pending);
        }

        internal void MarkTestSent(DateTime sentAtLocal)
        {
            state.LastSentAtLocalTicks = sentAtLocal.Ticks;
        }

        internal void MarkFailed(DateTime nextAttemptUtc)
        {
            if (!HasPendingNotification)
                return;
            state.FailedAttempts = Math.Min(1000, state.FailedAttempts + 1);
            state.NextAttemptUtcTicks = nextAttemptUtc.Ticks;
        }

        internal DateTime GetNextAttemptUtc()
        {
            if (state.NextAttemptUtcTicks <= 0)
                return DateTime.MinValue;
            try
            {
                return new DateTime(state.NextAttemptUtcTicks, DateTimeKind.Utc);
            }
            catch (ArgumentOutOfRangeException)
            {
                return DateTime.MinValue;
            }
        }

        internal DateTime? GetLastSentAtLocal()
        {
            if (state.LastSentAtLocalTicks <= 0)
                return null;
            try
            {
                return new DateTime(state.LastSentAtLocalTicks, DateTimeKind.Local);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        private void EstablishBaseline(int currentBucket, double usedPercent,
            long resetTicks, int durationMinutes, bool incrementGeneration)
        {
            state.SchemaVersion = 1;
            state.Initialized = true;
            state.ThresholdPercent = thresholdPercent;
            if (incrementGeneration)
                state.Generation = state.Generation == int.MaxValue ? 1 : state.Generation + 1;
            else if (state.Generation <= 0)
                state.Generation = 1;
            state.LastObservedUsedPercent = usedPercent;
            state.WindowResetsAtUtcTicks = resetTicks;
            state.WindowDurationMinutes = durationMinutes;
            state.HandledBucket = currentBucket;
            ClearPending();
        }

        private bool MarkHandled(PendingQuotaNotification handled)
        {
            if (handled == null || handled.Generation != state.Generation || !HasPendingNotification)
                return false;

            int handledToBucket = Math.Min(handled.ToBucket, state.PendingToBucket);
            if (handledToBucket < state.PendingFromBucket)
                return false;

            state.HandledBucket = Math.Max(state.HandledBucket, handledToBucket);
            if (state.PendingToBucket <= handledToBucket)
                ClearPending();
            else
                state.PendingFromBucket = handledToBucket + 1;
            state.FailedAttempts = 0;
            state.NextAttemptUtcTicks = 0;
            return true;
        }

        private bool IsNewWindow(double usedPercent, long resetTicks, DateTime nowLocal)
        {
            double minimumDrop = Math.Max(2.0, thresholdPercent / 2.0);
            bool significantDrop = state.LastObservedUsedPercent >= 0.0 &&
                usedPercent <= state.LastObservedUsedPercent - minimumDrop;
            long oneMinuteTicks = TimeSpan.FromMinutes(1).Ticks;
            bool resetAdvanced = resetTicks > 0 && state.WindowResetsAtUtcTicks > 0 &&
                resetTicks > state.WindowResetsAtUtcTicks + oneMinuteTicks;
            bool previousWindowExpired = state.WindowResetsAtUtcTicks > 0 &&
                state.WindowResetsAtUtcTicks <= nowLocal.ToUniversalTime().Ticks + oneMinuteTicks;
            bool anyDropWithAdvancedReset = state.LastObservedUsedPercent >= 0.0 &&
                usedPercent + PercentageEpsilon < state.LastObservedUsedPercent && resetAdvanced;
            bool noResetMetadataAndNearStart = resetTicks <= 0 && state.WindowResetsAtUtcTicks <= 0 &&
                usedPercent < thresholdPercent;
            return (resetAdvanced && previousWindowExpired) || anyDropWithAdvancedReset ||
                (significantDrop && (previousWindowExpired || noResetMetadataAndNearStart));
        }

        private int GetBucket(double usedPercent)
        {
            int bucket = (int)Math.Floor((usedPercent + PercentageEpsilon) / thresholdPercent);
            int maximumBucket = (int)Math.Ceiling(100.0 / thresholdPercent);
            return Math.Max(0, Math.Min(maximumBucket, bucket));
        }

        private int GetNextThresholdPercent(int bucket)
        {
            int next = (bucket + 1) * thresholdPercent;
            return next <= 100 ? next : 0;
        }

        private void ClearPending()
        {
            state.PendingFromBucket = 0;
            state.PendingToBucket = 0;
            state.PendingObservedUsedPercent = 0.0;
            state.PendingWindowResetsAtUtcTicks = 0;
            state.PendingWindowDurationMinutes = 0;
            state.FailedAttempts = 0;
            state.NextAttemptUtcTicks = 0;
        }

        private void NormalizeState()
        {
            if (state.SchemaVersion != 1 || state.ThresholdPercent < 0 ||
                state.HandledBucket < 0 || state.PendingFromBucket < 0 || state.PendingToBucket < 0)
            {
                ResetState();
                return;
            }

            if (!state.Initialized)
            {
                state.LastObservedUsedPercent = -1.0;
                ClearPending();
                return;
            }

            if (state.PendingToBucket < state.PendingFromBucket)
                ClearPending();
            if (state.Generation <= 0)
                state.Generation = 1;
        }

        private void ResetState()
        {
            state.SchemaVersion = 1;
            state.Initialized = false;
            state.ThresholdPercent = thresholdPercent;
            state.Generation = 0;
            state.LastObservedUsedPercent = -1.0;
            state.WindowResetsAtUtcTicks = 0;
            state.WindowDurationMinutes = 0;
            state.HandledBucket = 0;
            state.LastSentAtLocalTicks = 0;
            state.DailyAttemptDateKey = 0;
            state.DailyAttemptCount = 0;
            ClearPending();
        }

        private static long ToUtcTicks(DateTime? localTime)
        {
            if (!localTime.HasValue)
                return 0;
            try
            {
                return localTime.Value.ToUniversalTime().Ticks;
            }
            catch (ArgumentException)
            {
                return 0;
            }
        }

        private static DateTime? FromUtcTicks(long ticks)
        {
            if (ticks <= 0)
                return null;
            try
            {
                return new DateTime(ticks, DateTimeKind.Utc).ToLocalTime();
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }
    }
}
