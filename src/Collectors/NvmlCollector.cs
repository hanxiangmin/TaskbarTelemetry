using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TaskbarTelemetry
{
    /// <summary>
    /// Reads NVIDIA metrics through the driver-provided NVML library. The DLL
    /// is loaded only by its absolute Windows System32 path, never by a bare
    /// filename or from the application directory.
    /// </summary>
    internal sealed class NvmlCollector : IDisposable
    {
        private const int NvmlSuccess = 0;
        private const uint NvmlTemperatureGpu = 0;
        private const uint LoadLibrarySearchDllLoadDir = 0x00000100;
        private const uint LoadLibrarySearchSystem32 = 0x00000800;

        private readonly object syncRoot = new object();
        private IntPtr libraryHandle;
        private bool nvmlInitialized;
        private bool disposed;
        private string status;
        private WindowsGpuIndexResolver windowsGpuIndexResolver;
        private readonly Dictionary<uint, GpuMetric> knownDevices = new Dictionary<uint, GpuMetric>();

        private NvmlInitDelegate nvmlInit;
        private NvmlShutdownDelegate nvmlShutdown;
        private NvmlDeviceGetCountDelegate nvmlDeviceGetCount;
        private NvmlDeviceGetHandleByIndexDelegate nvmlDeviceGetHandleByIndex;
        private NvmlDeviceGetStringDelegate nvmlDeviceGetName;
        private NvmlDeviceGetStringDelegate nvmlDeviceGetUuid;
        private NvmlDeviceGetPciInfoDelegate nvmlDeviceGetPciInfo;
        private NvmlDeviceGetMemoryInfoDelegate nvmlDeviceGetMemoryInfo;
        private NvmlDeviceGetUtilizationDelegate nvmlDeviceGetUtilization;
        private NvmlDeviceGetTemperatureDelegate nvmlDeviceGetTemperature;
        private NvmlErrorStringDelegate nvmlErrorString;

        public NvmlCollector()
        {
            status = "NVML not initialized";
            try
            {
                Initialize();
            }
            catch (Exception ex)
            {
                status = "NVML unavailable: " + GetExceptionMessage(ex);
                ReleaseNativeResources();
            }
        }

        public string Status
        {
            get
            {
                lock (syncRoot)
                {
                    return status;
                }
            }
        }

        public IList<GpuMetric> Collect()
        {
            lock (syncRoot)
            {
                List<GpuMetric> result = new List<GpuMetric>();
                if (disposed)
                {
                    status = "NVML collector is disposed";
                    result.Add(CreateUnavailableMetric(status));
                    return result;
                }

                if (!nvmlInitialized)
                {
                    result.Add(CreateUnavailableMetric(status));
                    return result;
                }

                try
                {
                    uint count = 0;
                    int countResult = nvmlDeviceGetCount(ref count);
                    if (countResult != NvmlSuccess)
                    {
                        status = "NVML device enumeration failed: " + DescribeResult(countResult);
                        return LastKnownDevices(status);
                    }

                    foreach (uint oldIndex in new List<uint>(knownDevices.Keys))
                        if (oldIndex >= count) knownDevices.Remove(oldIndex);

                    for (uint index = 0; index < count; index++)
                        result.Add(CollectDevice(index));

                    bool windowsOrderApplied = windowsGpuIndexResolver != null &&
                        windowsGpuIndexResolver.TrySort(result);
                    if (!windowsOrderApplied)
                    {
                        result.Sort(delegate(GpuMetric left, GpuMetric right)
                        {
                            int pciComparison = StringComparer.OrdinalIgnoreCase.Compare(left.PciBusId, right.PciBusId);
                            if (pciComparison != 0)
                                return pciComparison;
                            return StringComparer.OrdinalIgnoreCase.Compare(left.StableId, right.StableId);
                        });
                    }

                    for (int displayIndex = 0; displayIndex < result.Count; displayIndex++)
                        result[displayIndex].DisplayIndex = displayIndex;

                    status = result.Count == 0 ? "NVML found no NVIDIA GPUs" : "OK";
                    if (result.Count == 0)
                        result.Add(CreateUnavailableMetric(status));
                    return result;
                }
                catch (Exception ex)
                {
                    status = "NVML collection failed: " + GetExceptionMessage(ex);
                    return LastKnownDevices(status);
                }
            }
        }

        public void Dispose()
        {
            lock (syncRoot)
            {
                if (disposed)
                    return;
                disposed = true;
                ReleaseNativeResources();
                status = "NVML collector is disposed";
            }
        }

        private void Initialize()
        {
            string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (string.IsNullOrWhiteSpace(windowsDirectory))
                throw new InvalidOperationException("Windows directory could not be resolved");

            string system32Directory = Path.GetFullPath(Path.Combine(windowsDirectory, "System32"));
            string nvmlPath = Path.GetFullPath(Path.Combine(system32Directory, "nvml.dll"));
            string expectedPrefix = system32Directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!nvmlPath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Resolved NVML path is outside Windows System32");
            if (!File.Exists(nvmlPath))
                throw new FileNotFoundException("The NVIDIA DCH driver NVML library was not found in Windows System32", nvmlPath);

            libraryHandle = LoadLibraryEx(nvmlPath, IntPtr.Zero,
                LoadLibrarySearchDllLoadDir | LoadLibrarySearchSystem32);
            if (libraryHandle == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not load NVML from Windows System32");

            nvmlInit = GetRequiredDelegate<NvmlInitDelegate>("nvmlInit_v2");
            nvmlShutdown = GetRequiredDelegate<NvmlShutdownDelegate>("nvmlShutdown");
            nvmlDeviceGetCount = GetRequiredDelegate<NvmlDeviceGetCountDelegate>("nvmlDeviceGetCount_v2");
            nvmlDeviceGetHandleByIndex = GetRequiredDelegate<NvmlDeviceGetHandleByIndexDelegate>("nvmlDeviceGetHandleByIndex_v2");
            nvmlDeviceGetName = GetRequiredDelegate<NvmlDeviceGetStringDelegate>("nvmlDeviceGetName");
            nvmlDeviceGetUuid = GetRequiredDelegate<NvmlDeviceGetStringDelegate>("nvmlDeviceGetUUID");
            nvmlDeviceGetPciInfo = GetRequiredDelegate<NvmlDeviceGetPciInfoDelegate>("nvmlDeviceGetPciInfo_v3");
            nvmlDeviceGetMemoryInfo = GetOptionalDelegate<NvmlDeviceGetMemoryInfoDelegate>("nvmlDeviceGetMemoryInfo");
            nvmlDeviceGetUtilization = GetRequiredDelegate<NvmlDeviceGetUtilizationDelegate>("nvmlDeviceGetUtilizationRates");
            nvmlDeviceGetTemperature = GetRequiredDelegate<NvmlDeviceGetTemperatureDelegate>("nvmlDeviceGetTemperature");
            nvmlErrorString = GetOptionalDelegate<NvmlErrorStringDelegate>("nvmlErrorString");

            int initResult = nvmlInit();
            if (initResult != NvmlSuccess)
                throw new InvalidOperationException("nvmlInit_v2 failed with code " + initResult);

            nvmlInitialized = true;
            windowsGpuIndexResolver = new WindowsGpuIndexResolver();
            status = "OK";
        }

        private GpuMetric CollectDevice(uint nativeIndex)
        {
            GpuMetric metric = new GpuMetric();
            metric.DisplayIndex = (int)nativeIndex;
            List<string> issues = new List<string>();

            IntPtr device = IntPtr.Zero;
            int handleResult = nvmlDeviceGetHandleByIndex(nativeIndex, ref device);
            if (handleResult != NvmlSuccess || device == IntPtr.Zero)
            {
                metric.Name = "NVIDIA GPU " + nativeIndex;
                metric.Status = "NVML handle failed: " + DescribeResult(handleResult);
                GpuMetric known;
                return knownDevices.TryGetValue(nativeIndex, out known)
                    ? GpuTopology.MissingSensors(known, metric.Status) : metric;
            }

            metric.Name = ReadString(device, nvmlDeviceGetName, "name", issues);
            metric.StableId = ReadString(device, nvmlDeviceGetUuid, "UUID", issues);
            metric.PciBusId = ReadPciBusId(device, issues);
            metric.IsNvidiaDevice = metric.StableId.Length > 0 || metric.PciBusId.Length > 0;
            if (!metric.IsNvidiaDevice)
            {
                GpuMetric known;
                if (knownDevices.TryGetValue(nativeIndex, out known))
                    metric = GpuTopology.MissingSensors(known, "Identity temporarily unavailable");
            }
            if (metric.IsNvidiaDevice) knownDevices[nativeIndex] = metric;

            if (nvmlDeviceGetMemoryInfo != null)
            {
                NvmlMemoryV1 memory = new NvmlMemoryV1();
                int memoryResult = nvmlDeviceGetMemoryInfo(device, ref memory);
                if (memoryResult == NvmlSuccess)
                {
                    if (memory.Total > 0 && memory.Used <= memory.Total)
                    {
                        metric.MemoryUsedBytes = memory.Used;
                        metric.MemoryTotalBytes = memory.Total;
                    }
                    else
                    {
                        issues.Add("memory returned invalid values");
                    }
                }
                else
                {
                    issues.Add("memory " + DescribeResult(memoryResult));
                }
            }
            else
            {
                issues.Add("memory API unavailable");
            }

            NvmlUtilization utilization = new NvmlUtilization();
            int utilizationResult = nvmlDeviceGetUtilization(device, ref utilization);
            if (utilizationResult == NvmlSuccess)
                metric.UsagePercent = Math.Max(0.0, Math.Min(100.0, utilization.Gpu));
            else
                issues.Add("usage " + DescribeResult(utilizationResult));

            uint temperature = 0;
            int temperatureResult = nvmlDeviceGetTemperature(device, NvmlTemperatureGpu, ref temperature);
            if (temperatureResult == NvmlSuccess)
                metric.TemperatureCelsius = temperature;
            else
                issues.Add("temperature " + DescribeResult(temperatureResult));

            if (metric.Name.Length == 0)
                metric.Name = "NVIDIA GPU " + nativeIndex;
            metric.Status = issues.Count == 0 ? "OK" : string.Join(", ", issues.ToArray());
            return metric;
        }

        private IList<GpuMetric> LastKnownDevices(string failure)
        {
            List<GpuMetric> result = new List<GpuMetric>();
            foreach (GpuMetric known in knownDevices.Values)
                result.Add(GpuTopology.MissingSensors(known, failure));
            if (result.Count == 0) result.Add(CreateUnavailableMetric(failure));
            return result;
        }

        private string ReadString(IntPtr device, NvmlDeviceGetStringDelegate getter,
            string fieldName, List<string> issues)
        {
            StringBuilder buffer = new StringBuilder(256);
            int nativeResult = getter(device, buffer, (uint)buffer.Capacity);
            if (nativeResult != NvmlSuccess)
            {
                issues.Add(fieldName + " " + DescribeResult(nativeResult));
                return string.Empty;
            }
            return buffer.ToString().Trim();
        }

        private string ReadPciBusId(IntPtr device, List<string> issues)
        {
            NvmlPciInfo pciInfo = new NvmlPciInfo();
            pciInfo.BusIdLegacy = new byte[16];
            pciInfo.BusId = new byte[32];
            int nativeResult = nvmlDeviceGetPciInfo(device, ref pciInfo);
            if (nativeResult != NvmlSuccess)
            {
                issues.Add("PCI bus ID " + DescribeResult(nativeResult));
                return string.Empty;
            }

            string busId = ReadNullTerminatedAscii(pciInfo.BusId);
            if (busId.Length > 0)
                return busId;
            return pciInfo.Domain.ToString("X8") + ":" +
                pciInfo.Bus.ToString("X2") + ":" +
                pciInfo.Device.ToString("X2") + ".0";
        }

        private T GetRequiredDelegate<T>(string exportName) where T : class
        {
            T result = GetOptionalDelegate<T>(exportName);
            if (result == null)
                throw new EntryPointNotFoundException("NVML export was not found: " + exportName);
            return result;
        }

        private T GetOptionalDelegate<T>(string exportName) where T : class
        {
            IntPtr address = GetProcAddress(libraryHandle, exportName);
            if (address == IntPtr.Zero)
                return null;
            return Marshal.GetDelegateForFunctionPointer(address, typeof(T)) as T;
        }

        private string DescribeResult(int result)
        {
            if (result == NvmlSuccess)
                return "success";
            try
            {
                if (nvmlErrorString != null)
                {
                    IntPtr pointer = nvmlErrorString(result);
                    if (pointer != IntPtr.Zero)
                    {
                        string text = Marshal.PtrToStringAnsi(pointer);
                        if (!string.IsNullOrWhiteSpace(text))
                            return text + " (" + result + ")";
                    }
                }
            }
            catch
            {
            }
            return "NVML error " + result;
        }

        private void ReleaseNativeResources()
        {
            if (windowsGpuIndexResolver != null)
            {
                try
                {
                    windowsGpuIndexResolver.Dispose();
                }
                catch
                {
                }
                windowsGpuIndexResolver = null;
            }

            if (nvmlInitialized && nvmlShutdown != null)
            {
                try
                {
                    nvmlShutdown();
                }
                catch
                {
                }
            }
            nvmlInitialized = false;

            if (libraryHandle != IntPtr.Zero)
            {
                try
                {
                    FreeLibrary(libraryHandle);
                }
                catch
                {
                }
                libraryHandle = IntPtr.Zero;
            }

            nvmlInit = null;
            nvmlShutdown = null;
            nvmlDeviceGetCount = null;
            nvmlDeviceGetHandleByIndex = null;
            nvmlDeviceGetName = null;
            nvmlDeviceGetUuid = null;
            nvmlDeviceGetPciInfo = null;
            nvmlDeviceGetMemoryInfo = null;
            nvmlDeviceGetUtilization = null;
            nvmlDeviceGetTemperature = null;
            nvmlErrorString = null;
        }

        private static GpuMetric CreateUnavailableMetric(string message)
        {
            GpuMetric metric = new GpuMetric();
            metric.DisplayIndex = 0;
            metric.Name = "NVIDIA GPU";
            metric.Status = message;
            return metric;
        }

        private static string ReadNullTerminatedAscii(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return string.Empty;
            int length = 0;
            while (length < bytes.Length && bytes[length] != 0)
                length++;
            return Encoding.ASCII.GetString(bytes, 0, length).Trim();
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
        private struct NvmlUtilization
        {
            public uint Gpu;
            public uint Memory;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        private struct NvmlMemoryV1
        {
            public ulong Total;
            public ulong Free;
            public ulong Used;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NvmlPciInfo
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] BusIdLegacy;
            public uint Domain;
            public uint Bus;
            public uint Device;
            public uint PciDeviceId;
            public uint PciSubSystemId;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] BusId;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvmlInitDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvmlShutdownDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvmlDeviceGetCountDelegate(ref uint count);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvmlDeviceGetHandleByIndexDelegate(uint index, ref IntPtr device);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private delegate int NvmlDeviceGetStringDelegate(IntPtr device, StringBuilder value, uint length);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvmlDeviceGetPciInfoDelegate(IntPtr device, ref NvmlPciInfo pciInfo);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvmlDeviceGetMemoryInfoDelegate(IntPtr device, ref NvmlMemoryV1 memory);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvmlDeviceGetUtilizationDelegate(IntPtr device, ref NvmlUtilization utilization);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvmlDeviceGetTemperatureDelegate(IntPtr device, uint sensorType, ref uint temperature);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr NvmlErrorStringDelegate(int result);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string fileName, IntPtr file, uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string procedureName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeLibrary(IntPtr module);
    }
}
