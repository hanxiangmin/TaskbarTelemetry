using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace TaskbarTelemetry
{
    /// <summary>
    /// Collects low-cost operating-system metrics without PerformanceCounter,
    /// WMI, a helper process, or an elevated component.
    /// </summary>
    internal sealed class SystemMetricsCollector : IDisposable
    {
        private readonly object syncRoot = new object();
        private readonly string configuredNetworkInterfaceId;

        private bool hasPreviousCpuSample;
        private ulong previousIdleTime;
        private ulong previousKernelTime;
        private ulong previousUserTime;

        private bool hasPreviousNetworkSample;
        private string previousNetworkSelectionKey;
        private long previousNetworkTimestamp;
        private long previousBytesSent;
        private long previousBytesReceived;
        private bool disposed;

        public SystemMetricsCollector(AppSettings settings)
        {
            configuredNetworkInterfaceId = settings == null
                ? string.Empty
                : (settings.NetworkInterfaceId ?? string.Empty).Trim();
            previousNetworkSelectionKey = string.Empty;
        }

        public SystemMetric Collect()
        {
            lock (syncRoot)
            {
                SystemMetric result = new SystemMetric();
                List<string> issues = new List<string>();

                if (disposed)
                {
                    result.Status = "System collector is disposed";
                    return result;
                }

                try
                {
                    result.CpuUsagePercent = CollectCpuUsage();
                }
                catch (Exception ex)
                {
                    issues.Add("CPU usage unavailable: " + GetExceptionMessage(ex));
                }

                try
                {
                    CollectMemory(result);
                }
                catch (Exception ex)
                {
                    issues.Add("Memory usage unavailable: " + GetExceptionMessage(ex));
                }

                try
                {
                    CollectNetwork(result, issues);
                }
                catch (Exception ex)
                {
                    ResetNetworkBaseline();
                    issues.Add("Network rate unavailable: " + GetExceptionMessage(ex));
                }

                result.Status = issues.Count == 0 ? "OK" : string.Join("; ", issues.ToArray());
                return result;
            }
        }

        public void Dispose()
        {
            lock (syncRoot)
            {
                disposed = true;
                ResetNetworkBaseline();
            }
        }

        private double CollectCpuUsage()
        {
            NativeFileTime idle;
            NativeFileTime kernel;
            NativeFileTime user;
            if (!GetSystemTimes(out idle, out kernel, out user))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            ulong idleTime = idle.ToUInt64();
            ulong kernelTime = kernel.ToUInt64();
            ulong userTime = user.ToUInt64();
            double usage = 0.0;

            if (hasPreviousCpuSample &&
                idleTime >= previousIdleTime &&
                kernelTime >= previousKernelTime &&
                userTime >= previousUserTime)
            {
                ulong idleDelta = idleTime - previousIdleTime;
                ulong kernelDelta = kernelTime - previousKernelTime;
                ulong userDelta = userTime - previousUserTime;
                ulong totalDelta = kernelDelta + userDelta;
                if (totalDelta > 0)
                    usage = 100.0 * (1.0 - ((double)idleDelta / totalDelta));
            }

            previousIdleTime = idleTime;
            previousKernelTime = kernelTime;
            previousUserTime = userTime;
            hasPreviousCpuSample = true;
            return ClampPercent(usage);
        }

        private static void CollectMemory(SystemMetric result)
        {
            MemoryStatusEx status = new MemoryStatusEx();
            status.Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
            if (!GlobalMemoryStatusEx(ref status))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            result.MemoryTotalBytes = status.TotalPhysical;
            result.MemoryUsedBytes = status.TotalPhysical >= status.AvailablePhysical
                ? status.TotalPhysical - status.AvailablePhysical
                : 0;
            result.MemoryUsagePercent = status.TotalPhysical == 0
                ? 0.0
                : ClampPercent(100.0 * result.MemoryUsedBytes / status.TotalPhysical);
        }

        private void CollectNetwork(SystemMetric result, List<string> issues)
        {
            List<NetworkInterface> selected = SelectNetworkInterfaces(issues);
            if (selected.Count == 0)
            {
                ResetNetworkBaseline();
                result.NetworkName = string.Empty;
                return;
            }

            selected.Sort(delegate(NetworkInterface left, NetworkInterface right)
            {
                return StringComparer.OrdinalIgnoreCase.Compare(left.Id, right.Id);
            });

            long totalSent = 0;
            long totalReceived = 0;
            List<string> names = new List<string>();
            List<string> successfulIds = new List<string>();
            for (int i = 0; i < selected.Count; i++)
            {
                NetworkInterface networkInterface = selected[i];
                try
                {
                    IPv4InterfaceStatistics statistics = networkInterface.GetIPv4Statistics();
                    totalSent = AddCounterWithoutOverflow(totalSent, statistics.BytesSent);
                    totalReceived = AddCounterWithoutOverflow(totalReceived, statistics.BytesReceived);
                    successfulIds.Add(networkInterface.Id);
                    names.Add(string.IsNullOrWhiteSpace(networkInterface.Name)
                        ? networkInterface.Description
                        : networkInterface.Name);
                }
                catch (Exception ex)
                {
                    issues.Add("Network interface " + SafeInterfaceName(networkInterface) +
                        " unavailable: " + GetExceptionMessage(ex));
                }
            }

            if (successfulIds.Count == 0)
            {
                ResetNetworkBaseline();
                result.NetworkName = string.Empty;
                return;
            }

            string selectionKey = string.Join("|", successfulIds.ToArray());
            long now = Stopwatch.GetTimestamp();
            result.NetworkName = string.Join(" + ", names.ToArray());

            // A changed adapter set has unrelated counters, so it deliberately
            // starts a fresh baseline instead of showing a false traffic spike.
            if (hasPreviousNetworkSample &&
                string.Equals(selectionKey, previousNetworkSelectionKey, StringComparison.OrdinalIgnoreCase))
            {
                double seconds = (now - previousNetworkTimestamp) / (double)Stopwatch.Frequency;
                if (seconds > 0.0)
                {
                    long sentDelta = totalSent >= previousBytesSent ? totalSent - previousBytesSent : 0;
                    long receivedDelta = totalReceived >= previousBytesReceived ? totalReceived - previousBytesReceived : 0;
                    result.UploadBytesPerSecond = sentDelta / seconds;
                    result.DownloadBytesPerSecond = receivedDelta / seconds;
                }
            }

            previousNetworkSelectionKey = selectionKey;
            previousNetworkTimestamp = now;
            previousBytesSent = totalSent;
            previousBytesReceived = totalReceived;
            hasPreviousNetworkSample = true;
        }

        private List<NetworkInterface> SelectNetworkInterfaces(List<string> issues)
        {
            NetworkInterface[] allInterfaces = NetworkInterface.GetAllNetworkInterfaces();
            List<NetworkInterface> selected = new List<NetworkInterface>();

            if (configuredNetworkInterfaceId.Length > 0)
            {
                NetworkInterface configured = null;
                for (int i = 0; i < allInterfaces.Length; i++)
                {
                    if (string.Equals(allInterfaces[i].Id, configuredNetworkInterfaceId,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        configured = allInterfaces[i];
                        break;
                    }
                }

                if (configured == null)
                {
                    issues.Add("Configured network interface was not found: " + configuredNetworkInterfaceId);
                    return selected;
                }

                if (!IsUsableNetworkInterface(configured))
                {
                    issues.Add("Configured network interface is not active: " + SafeInterfaceName(configured));
                    return selected;
                }

                selected.Add(configured);
                return selected;
            }

            for (int i = 0; i < allInterfaces.Length; i++)
            {
                NetworkInterface candidate = allInterfaces[i];
                if (IsUsableNetworkInterface(candidate) && HasDefaultGateway(candidate))
                    selected.Add(candidate);
            }

            if (selected.Count == 0)
                issues.Add("No active network interface with a default gateway");
            return selected;
        }

        private static bool IsUsableNetworkInterface(NetworkInterface networkInterface)
        {
            return networkInterface.OperationalStatus == OperationalStatus.Up &&
                networkInterface.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                networkInterface.NetworkInterfaceType != NetworkInterfaceType.Tunnel;
        }

        private static bool HasDefaultGateway(NetworkInterface networkInterface)
        {
            try
            {
                GatewayIPAddressInformationCollection gateways = networkInterface.GetIPProperties().GatewayAddresses;
                foreach (GatewayIPAddressInformation gateway in gateways)
                {
                    IPAddress address = gateway.Address;
                    if (address != null &&
                        !address.Equals(IPAddress.Any) &&
                        !address.Equals(IPAddress.IPv6Any) &&
                        !address.Equals(IPAddress.None) &&
                        !address.Equals(IPAddress.IPv6None))
                        return true;
                }
            }
            catch
            {
                return false;
            }
            return false;
        }

        private void ResetNetworkBaseline()
        {
            hasPreviousNetworkSample = false;
            previousNetworkSelectionKey = string.Empty;
            previousNetworkTimestamp = 0;
            previousBytesSent = 0;
            previousBytesReceived = 0;
        }

        private static long AddCounterWithoutOverflow(long left, long right)
        {
            if (right < 0)
                return left;
            if (long.MaxValue - left < right)
                return long.MaxValue;
            return left + right;
        }

        private static double ClampPercent(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return 0.0;
            return Math.Max(0.0, Math.Min(100.0, value));
        }

        private static string SafeInterfaceName(NetworkInterface networkInterface)
        {
            if (!string.IsNullOrWhiteSpace(networkInterface.Name))
                return networkInterface.Name;
            if (!string.IsNullOrWhiteSpace(networkInterface.Description))
                return networkInterface.Description;
            return networkInterface.Id;
        }

        private static string GetExceptionMessage(Exception exception)
        {
            Exception current = exception;
            while (current is System.Reflection.TargetInvocationException && current.InnerException != null)
                current = current.InnerException;
            string message = current.Message ?? current.GetType().Name;
            return message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeFileTime
        {
            public uint LowDateTime;
            public uint HighDateTime;

            public ulong ToUInt64()
            {
                return ((ulong)HighDateTime << 32) | LowDateTime;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhysical;
            public ulong AvailablePhysical;
            public ulong TotalPageFile;
            public ulong AvailablePageFile;
            public ulong TotalVirtual;
            public ulong AvailableVirtual;
            public ulong AvailableExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemTimes(
            out NativeFileTime idleTime,
            out NativeFileTime kernelTime,
            out NativeFileTime userTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
    }
}
