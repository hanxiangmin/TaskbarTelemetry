using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace TaskbarTelemetry
{
    /// <summary>
    /// Resolves NVIDIA PCI bus IDs to the Windows graphics-adapter order.
    /// CUDA supplies the bridge from PCI identity to the Windows adapter LUID;
    /// D3DKMT supplies the Windows adapter enumeration order used for display.
    /// </summary>
    internal sealed class WindowsGpuIndexResolver : IDisposable
    {
        private const int CudaSuccess = 0;
        private const int StatusSuccess = 0;
        private const uint LoadLibrarySearchDllLoadDir = 0x00000100;
        private const uint LoadLibrarySearchSystem32 = 0x00000800;
        private const uint MaximumReasonableAdapterCount = 256;

        private readonly object syncRoot = new object();
        private readonly List<long> windowsAdapterOrder = new List<long>();
        private readonly Dictionary<string, int> cachedRanks =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private IntPtr cudaLibraryHandle;
        private CuDeviceGetByPciBusIdDelegate cuDeviceGetByPciBusId;
        private CuDeviceGetLuidDelegate cuDeviceGetLuid;
        private bool available;
        private bool disposed;

        public WindowsGpuIndexResolver()
        {
            try
            {
                Initialize();
            }
            catch
            {
                ReleaseNativeResources();
            }
        }

        public bool TrySort(List<GpuMetric> metrics)
        {
            lock (syncRoot)
            {
                if (disposed || !available || metrics == null || metrics.Count == 0)
                    return false;

                Dictionary<GpuMetric, int> ranks = new Dictionary<GpuMetric, int>();
                HashSet<int> usedRanks = new HashSet<int>();
                for (int index = 0; index < metrics.Count; index++)
                {
                    GpuMetric metric = metrics[index];
                    int rank;
                    if (metric == null || !TryResolveRank(metric.PciBusId, out rank) ||
                        !usedRanks.Add(rank))
                        return false;
                    ranks.Add(metric, rank);
                }

                metrics.Sort(delegate(GpuMetric left, GpuMetric right)
                {
                    return ranks[left].CompareTo(ranks[right]);
                });
                return true;
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
            }
        }

        private void Initialize()
        {
            string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (string.IsNullOrWhiteSpace(windowsDirectory))
                return;

            string system32Directory = Path.GetFullPath(Path.Combine(windowsDirectory, "System32"));
            string cudaPath = Path.GetFullPath(Path.Combine(system32Directory, "nvcuda.dll"));
            string expectedPrefix = system32Directory.TrimEnd(Path.DirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            if (!cudaPath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(cudaPath))
                return;

            cudaLibraryHandle = LoadLibraryEx(cudaPath, IntPtr.Zero,
                LoadLibrarySearchDllLoadDir | LoadLibrarySearchSystem32);
            if (cudaLibraryHandle == IntPtr.Zero)
                return;

            CuInitDelegate cuInit = GetRequiredDelegate<CuInitDelegate>("cuInit");
            cuDeviceGetByPciBusId =
                GetRequiredDelegate<CuDeviceGetByPciBusIdDelegate>("cuDeviceGetByPCIBusId");
            cuDeviceGetLuid = GetRequiredDelegate<CuDeviceGetLuidDelegate>("cuDeviceGetLuid");
            if (cuInit(0) != CudaSuccess)
                return;

            ReadWindowsAdapterOrder();
            available = windowsAdapterOrder.Count > 0;
        }

        private void ReadWindowsAdapterOrder()
        {
            D3dkmtEnumAdapters2 enumeration = new D3dkmtEnumAdapters2();
            int firstResult = D3DKMTEnumAdapters2(ref enumeration);
            if (firstResult != StatusSuccess || enumeration.NumAdapters == 0 ||
                enumeration.NumAdapters > MaximumReasonableAdapterCount)
                return;

            int adapterSize = Marshal.SizeOf(typeof(D3dkmtAdapterInfo));
            int bufferSize = checked(adapterSize * (int)enumeration.NumAdapters);
            enumeration.Adapters = Marshal.AllocHGlobal(bufferSize);
            uint allocatedCount = enumeration.NumAdapters;
            bool handlesNeedClosing = false;
            try
            {
                int secondResult = D3DKMTEnumAdapters2(ref enumeration);
                if (secondResult != StatusSuccess || enumeration.NumAdapters > allocatedCount)
                    return;

                handlesNeedClosing = true;
                for (uint index = 0; index < enumeration.NumAdapters; index++)
                {
                    IntPtr itemPointer = IntPtr.Add(enumeration.Adapters,
                        checked((int)index * adapterSize));
                    D3dkmtAdapterInfo adapter = (D3dkmtAdapterInfo)Marshal.PtrToStructure(
                        itemPointer, typeof(D3dkmtAdapterInfo));
                    long luid = ToLuidKey(adapter.AdapterLuid);
                    if (!windowsAdapterOrder.Contains(luid))
                        windowsAdapterOrder.Add(luid);
                }
            }
            finally
            {
                if (handlesNeedClosing)
                {
                    for (uint index = 0; index < enumeration.NumAdapters; index++)
                    {
                        IntPtr itemPointer = IntPtr.Add(enumeration.Adapters,
                            checked((int)index * adapterSize));
                        D3dkmtAdapterInfo adapter = (D3dkmtAdapterInfo)Marshal.PtrToStructure(
                            itemPointer, typeof(D3dkmtAdapterInfo));
                        if (adapter.AdapterHandle != 0)
                        {
                            D3dkmtCloseAdapter close = new D3dkmtCloseAdapter();
                            close.AdapterHandle = adapter.AdapterHandle;
                            D3DKMTCloseAdapter(ref close);
                        }
                    }
                }
                Marshal.FreeHGlobal(enumeration.Adapters);
                enumeration.Adapters = IntPtr.Zero;
            }
        }

        private bool TryResolveRank(string pciBusId, out int rank)
        {
            rank = -1;
            if (string.IsNullOrWhiteSpace(pciBusId))
                return false;

            string normalizedPciBusId = pciBusId.Trim();
            if (cachedRanks.TryGetValue(normalizedPciBusId, out rank))
                return true;

            int cudaDevice = 0;
            int lookupResult = cuDeviceGetByPciBusId(ref cudaDevice, normalizedPciBusId);
            if (lookupResult != CudaSuccess)
                return false;

            byte[] luidBytes = new byte[8];
            uint nodeMask = 0;
            int luidResult = cuDeviceGetLuid(luidBytes, ref nodeMask, cudaDevice);
            if (luidResult != CudaSuccess)
                return false;

            WindowsLuid luid = new WindowsLuid();
            luid.LowPart = BitConverter.ToUInt32(luidBytes, 0);
            luid.HighPart = BitConverter.ToInt32(luidBytes, 4);
            rank = windowsAdapterOrder.IndexOf(ToLuidKey(luid));
            if (rank < 0)
                return false;

            cachedRanks[normalizedPciBusId] = rank;
            return true;
        }

        private T GetRequiredDelegate<T>(string exportName) where T : class
        {
            IntPtr address = GetProcAddress(cudaLibraryHandle, exportName);
            if (address == IntPtr.Zero)
                throw new EntryPointNotFoundException("CUDA export was not found: " + exportName);
            return Marshal.GetDelegateForFunctionPointer(address, typeof(T)) as T;
        }

        private void ReleaseNativeResources()
        {
            available = false;
            cachedRanks.Clear();
            windowsAdapterOrder.Clear();
            cuDeviceGetByPciBusId = null;
            cuDeviceGetLuid = null;

            if (cudaLibraryHandle != IntPtr.Zero)
            {
                try
                {
                    FreeLibrary(cudaLibraryHandle);
                }
                catch
                {
                }
                cudaLibraryHandle = IntPtr.Zero;
            }
        }

        private static long ToLuidKey(WindowsLuid luid)
        {
            return unchecked(((long)luid.HighPart << 32) | ((long)luid.LowPart & 0xFFFFFFFFL));
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowsLuid
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct D3dkmtAdapterInfo
        {
            public uint AdapterHandle;
            public WindowsLuid AdapterLuid;
            public uint NumOfSources;
            [MarshalAs(UnmanagedType.Bool)]
            public bool PrecisePresentRegionsPreferred;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct D3dkmtEnumAdapters2
        {
            public uint NumAdapters;
            public IntPtr Adapters;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct D3dkmtCloseAdapter
        {
            public uint AdapterHandle;
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int CuInitDelegate(uint flags);

        [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Ansi)]
        private delegate int CuDeviceGetByPciBusIdDelegate(ref int device,
            [MarshalAs(UnmanagedType.LPStr)] string pciBusId);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int CuDeviceGetLuidDelegate([Out] byte[] luid,
            ref uint deviceNodeMask, int device);

        [DllImport("gdi32.dll")]
        private static extern int D3DKMTEnumAdapters2(ref D3dkmtEnumAdapters2 enumeration);

        [DllImport("gdi32.dll")]
        private static extern int D3DKMTCloseAdapter(ref D3dkmtCloseAdapter closeAdapter);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string fileName, IntPtr file, uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string procedureName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeLibrary(IntPtr module);
    }
}
