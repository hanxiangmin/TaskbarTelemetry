using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace TaskbarTelemetry
{
#if STORE_BUILD
    /// <summary>
    /// Microsoft Store builds intentionally omit the optional CPU-temperature
    /// backend so the package has no dependency on a non-Microsoft driver.
    /// </summary>
    internal sealed class CpuTemperatureCollector : IDisposable
    {
        public CpuTemperatureCollector(AppSettings settings)
        {
        }

        public TemperatureMetric Collect(out CpuFrequencyMetric frequency)
        {
            frequency = new CpuFrequencyMetric();
            frequency.Status = "CPU hardware clocks are not included in the Microsoft Store build";
            TemperatureMetric result = new TemperatureMetric();
            result.Status = "CPU temperature is not included in the Microsoft Store build";
            return result;
        }

        public void Dispose()
        {
        }
    }
#else
    /// <summary>
    /// Optional CPU-temperature provider. LibreHardwareMonitor is loaded by
    /// reflection so this application can still run when the library or its
    /// separately installed PawnIO prerequisite is unavailable.
    /// </summary>
    internal sealed class CpuTemperatureCollector : IDisposable
    {
        private static readonly Version MinimumSupportedVersion = new Version(0, 9, 6, 0);

        private readonly object syncRoot = new object();
        private readonly string libraryPath;
        private object computer;
        private MethodInfo closeMethod;
        private PropertyInfo hardwareProperty;
        private Version libraryVersion;
        private string initializationError;
        private bool initialized;
        private bool disposed;

        public CpuTemperatureCollector(AppSettings settings)
        {
            try
            {
                string configuredPath = settings == null
                    ? Path.Combine("lib", "LibreHardwareMonitorLib.dll")
                    : settings.CpuTemperatureLibrary;
                libraryPath = settings == null
                    ? Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, configuredPath))
                    : settings.ResolvePath(configuredPath);
            }
            catch (Exception ex)
            {
                libraryPath = string.Empty;
                initializationError = "CPU temperature library path is invalid: " + GetExceptionMessage(ex);
            }
        }

        public TemperatureMetric Collect(out CpuFrequencyMetric frequency)
        {
            lock (syncRoot)
            {
                frequency = new CpuFrequencyMetric();
                TemperatureMetric result = new TemperatureMetric();
                if (disposed)
                {
                    result.Status = "CPU temperature collector is disposed";
                    frequency.Status = result.Status;
                    return result;
                }

                try
                {
                    EnsureInitialized();
                    if (!initialized)
                    {
                        result.Status = initializationError;
                        frequency.Status = initializationError;
                        return result;
                    }

                    return ReadTemperature(frequency);
                }
                catch (Exception ex)
                {
                    result.Status = "CPU temperature unavailable: " + GetExceptionMessage(ex);
                    frequency = new CpuFrequencyMetric { Status = "CPU clock unavailable: " + GetExceptionMessage(ex) };
                    return result;
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

                if (computer != null && closeMethod != null)
                {
                    try
                    {
                        closeMethod.Invoke(computer, null);
                    }
                    catch
                    {
                    }
                }

                IDisposable disposableComputer = computer as IDisposable;
                if (disposableComputer != null)
                {
                    try
                    {
                        disposableComputer.Dispose();
                    }
                    catch
                    {
                    }
                }

                computer = null;
                closeMethod = null;
                hardwareProperty = null;
                initialized = false;
            }
        }

        private void EnsureInitialized()
        {
            if (initialized || initializationError != null)
                return;
            if (string.IsNullOrWhiteSpace(libraryPath))
            {
                initializationError = "CPU temperature unavailable: no LibreHardwareMonitor library path is configured";
                return;
            }
            if (!File.Exists(libraryPath))
            {
                initializationError = "CPU temperature unavailable: LibreHardwareMonitorLib.dll was not found at " + libraryPath;
                return;
            }

            try
            {
                AssemblyName assemblyName = AssemblyName.GetAssemblyName(libraryPath);
                libraryVersion = assemblyName.Version;
                if (libraryVersion == null || libraryVersion.CompareTo(MinimumSupportedVersion) < 0)
                {
                    string foundVersion = libraryVersion == null ? "unknown" : libraryVersion.ToString();
                    initializationError = "CPU temperature unavailable: LibreHardwareMonitor 0.9.6 or later is required; found " + foundVersion;
                    return;
                }

                Assembly assembly = Assembly.LoadFrom(libraryPath);
                Type computerType = assembly.GetType("LibreHardwareMonitor.Hardware.Computer", false, false);
                if (computerType == null)
                    throw new TypeLoadException("LibreHardwareMonitor.Hardware.Computer was not found");

                computer = Activator.CreateInstance(computerType);
                DisableAllHardwareGroups(computerType, computer);
                SetRequiredBooleanProperty(computerType, computer, "IsCpuEnabled", true);

                hardwareProperty = computerType.GetProperty("Hardware", BindingFlags.Instance | BindingFlags.Public);
                if (hardwareProperty == null || !hardwareProperty.CanRead)
                    throw new MissingMemberException(computerType.FullName, "Hardware");

                MethodInfo openMethod = computerType.GetMethod("Open", BindingFlags.Instance | BindingFlags.Public,
                    null, Type.EmptyTypes, null);
                closeMethod = computerType.GetMethod("Close", BindingFlags.Instance | BindingFlags.Public,
                    null, Type.EmptyTypes, null);
                if (openMethod == null || closeMethod == null)
                    throw new MissingMethodException("LibreHardwareMonitor Computer.Open/Close was not found");

                // This call only opens the configured library backend. This
                // application never runs a PawnIO installer or installs a driver.
                openMethod.Invoke(computer, null);
                initialized = true;
            }
            catch (Exception ex)
            {
                initializationError = "CPU temperature unavailable: " + GetExceptionMessage(ex) +
                    ". LibreHardwareMonitor 0.9.6 and an already installed/working PawnIO setup may be required";
                CloseAfterFailedInitialization();
            }
        }

        private TemperatureMetric ReadTemperature(CpuFrequencyMetric frequency)
        {
            TemperatureMetric result = new TemperatureMetric();
            object hardwareValue = hardwareProperty.GetValue(computer, null);
            IEnumerable hardwareItems = hardwareValue as IEnumerable;
            if (hardwareItems == null)
            {
                result.Status = "CPU temperature unavailable: LibreHardwareMonitor returned no hardware collection";
                return result;
            }

            bool foundCpu = false;
            List<TemperatureCandidate> candidates = new List<TemperatureCandidate>();
            foreach (object hardware in hardwareItems)
            {
                if (hardware == null || !IsCpuHardware(hardware))
                    continue;
                foundCpu = true;
                UpdateHardware(hardware);
                // Reuse the same hardware update, not a second poll or driver instance.
                // An unreadable temperature must not discard readable core clocks.
                AddCoreClocks(hardware, frequency);
                AddTemperatureCandidates(hardware, candidates);
            }

            if (!foundCpu)
            {
                result.Status = "CPU temperature unavailable: LibreHardwareMonitor did not enumerate a CPU";
                return result;
            }
            if (candidates.Count == 0)
            {
                result.Status = "CPU temperature unavailable: CPU Package, Package, and Core Max have no readable value (PawnIO may not be installed or available)";
                return result;
            }

            candidates.Sort(delegate(TemperatureCandidate left, TemperatureCandidate right)
            {
                int priorityComparison = left.Priority.CompareTo(right.Priority);
                if (priorityComparison != 0)
                    return priorityComparison;
                return right.Celsius.CompareTo(left.Celsius);
            });

            TemperatureCandidate selected = candidates[0];
            result.Celsius = selected.Celsius;
            result.SensorName = selected.Name;
            result.Status = "OK (LibreHardwareMonitor " + libraryVersion + ")";
            return result;
        }

        private static void AddCoreClocks(object hardware, CpuFrequencyMetric frequency)
        {
            PropertyInfo sensorsProperty = hardware.GetType().GetProperty("Sensors", BindingFlags.Instance | BindingFlags.Public);
            IEnumerable sensors = sensorsProperty == null ? null : sensorsProperty.GetValue(hardware, null) as IEnumerable;
            if (sensors == null) return;
            foreach (object sensor in sensors)
            {
                if (sensor == null) continue;
                try
                {
                    Type type = sensor.GetType();
                    PropertyInfo kindProperty = type.GetProperty("SensorType");
                    object kind = kindProperty == null ? null : kindProperty.GetValue(sensor, null);
                    if (kind == null || !string.Equals(kind.ToString(), "Clock", StringComparison.OrdinalIgnoreCase)) continue;
                    PropertyInfo nameProperty = type.GetProperty("Name"), valueProperty = type.GetProperty("Value");
                    object value = valueProperty == null ? null : valueProperty.GetValue(sensor, null);
                    if (nameProperty == null || value == null) continue;
                    CpuClockSelector.Observe(frequency, Convert.ToString(nameProperty.GetValue(sensor, null)),
                        Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture));
                }
                catch
                {
                    // One unsupported sensor must not break the temperature sample.
                }
            }
        }

        private static bool IsCpuHardware(object hardware)
        {
            PropertyInfo hardwareTypeProperty = hardware.GetType().GetProperty("HardwareType",
                BindingFlags.Instance | BindingFlags.Public);
            object hardwareType = hardwareTypeProperty == null
                ? null
                : hardwareTypeProperty.GetValue(hardware, null);
            return hardwareType != null && string.Equals(hardwareType.ToString(), "Cpu",
                StringComparison.OrdinalIgnoreCase);
        }

        private static void UpdateHardware(object hardware)
        {
            MethodInfo updateMethod = hardware.GetType().GetMethod("Update", BindingFlags.Instance | BindingFlags.Public,
                null, Type.EmptyTypes, null);
            if (updateMethod == null)
                throw new MissingMethodException(hardware.GetType().FullName, "Update");
            updateMethod.Invoke(hardware, null);
        }

        private static void AddTemperatureCandidates(object hardware, List<TemperatureCandidate> candidates)
        {
            PropertyInfo sensorsProperty = hardware.GetType().GetProperty("Sensors",
                BindingFlags.Instance | BindingFlags.Public);
            if (sensorsProperty == null || !sensorsProperty.CanRead)
                return;
            IEnumerable sensors = sensorsProperty.GetValue(hardware, null) as IEnumerable;
            if (sensors == null)
                return;

            foreach (object sensor in sensors)
            {
                if (sensor == null || !IsTemperatureSensor(sensor))
                    continue;

                PropertyInfo nameProperty = sensor.GetType().GetProperty("Name",
                    BindingFlags.Instance | BindingFlags.Public);
                PropertyInfo valueProperty = sensor.GetType().GetProperty("Value",
                    BindingFlags.Instance | BindingFlags.Public);
                string name = nameProperty == null ? string.Empty : Convert.ToString(nameProperty.GetValue(sensor, null));
                int priority = GetSensorPriority(name);
                if (priority == int.MaxValue || valueProperty == null || !valueProperty.CanRead)
                    continue;

                object value = valueProperty.GetValue(sensor, null);
                if (value == null)
                    continue;
                double celsius = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
                if (double.IsNaN(celsius) || double.IsInfinity(celsius))
                    continue;

                TemperatureCandidate candidate = new TemperatureCandidate();
                candidate.Name = name;
                candidate.Celsius = celsius;
                candidate.Priority = priority;
                candidates.Add(candidate);
            }
        }

        private static bool IsTemperatureSensor(object sensor)
        {
            PropertyInfo sensorTypeProperty = sensor.GetType().GetProperty("SensorType",
                BindingFlags.Instance | BindingFlags.Public);
            object sensorType = sensorTypeProperty == null
                ? null
                : sensorTypeProperty.GetValue(sensor, null);
            return sensorType != null && string.Equals(sensorType.ToString(), "Temperature",
                StringComparison.OrdinalIgnoreCase);
        }

        private static int GetSensorPriority(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return int.MaxValue;
            if (name.IndexOf("CPU Package", StringComparison.OrdinalIgnoreCase) >= 0)
                return 0;
            if (string.Equals(name.Trim(), "Package", StringComparison.OrdinalIgnoreCase) ||
                name.IndexOf("Package", StringComparison.OrdinalIgnoreCase) >= 0)
                return 1;
            if (name.IndexOf("Core Max", StringComparison.OrdinalIgnoreCase) >= 0)
                return 2;
            return int.MaxValue;
        }

        private static void DisableAllHardwareGroups(Type computerType, object computerInstance)
        {
            string[] propertyNames =
            {
                "IsBatteryEnabled",
                "IsControllerEnabled",
                "IsCpuEnabled",
                "IsGpuEnabled",
                "IsMemoryEnabled",
                "IsMotherboardEnabled",
                "IsNetworkEnabled",
                "IsPsuEnabled",
                "IsStorageEnabled"
            };
            for (int i = 0; i < propertyNames.Length; i++)
                SetBooleanPropertyIfAvailable(computerType, computerInstance, propertyNames[i], false);
        }

        private static void SetBooleanPropertyIfAvailable(Type type, object instance, string name, bool value)
        {
            PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (property != null && property.CanWrite && property.PropertyType == typeof(bool))
                property.SetValue(instance, value, null);
        }

        private static void SetRequiredBooleanProperty(Type type, object instance, string name, bool value)
        {
            PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (property == null || !property.CanWrite || property.PropertyType != typeof(bool))
                throw new MissingMemberException(type.FullName, name);
            property.SetValue(instance, value, null);
        }

        private void CloseAfterFailedInitialization()
        {
            if (computer != null && closeMethod != null)
            {
                try
                {
                    closeMethod.Invoke(computer, null);
                }
                catch
                {
                }
            }
            computer = null;
            closeMethod = null;
            hardwareProperty = null;
            initialized = false;
        }

        private static string GetExceptionMessage(Exception exception)
        {
            Exception current = exception;
            while (current is TargetInvocationException && current.InnerException != null)
                current = current.InnerException;
            string message = current.Message ?? current.GetType().Name;
            return message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        private sealed class TemperatureCandidate
        {
            public string Name;
            public double Celsius;
            public int Priority;
        }
    }
#endif
}
