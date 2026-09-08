# Certification notes — personal-match package

TaskbarTelemetry is a .NET Framework 4.8 WinForms desktop utility packaged as MSIX for Windows Desktop x64. This package is intended for a private Microsoft Store audience and preserves the CPU-temperature display used on the owner's existing device.

## Restricted capability justification: runFullTrust

TaskbarTelemetry requires medium-integrity full trust to host its own non-activating WinForms taskbar child window; read local CPU, GPU, memory and network telemetry; read compatible quota fields from the current user's local session cache; maintain isolated per-user package settings; and send an optional HTTPS quota notification after explicit user consent. The app does not request elevation, inject into or hook Explorer, install services or drivers, modify Windows security settings, read authentication tokens, or access other users' data.

The app uses User32 window-discovery, rectangle and `SetParent` APIs to host its own window as a child of `Shell_TrayWnd` immediately to the left of the notification area. It does not inject code into or hook the Explorer process. The taskbar window can always be closed from its context menu.

## CPU-temperature component disclosure

The package redistributes the unmodified LibreHardwareMonitor 0.9.6 runtime library and four required support assemblies under their respective licenses. The package contains no PawnIO binary, `.sys`, `.inf`, `.cat`, driver installer, Windows service, or custom install action. The application never downloads, installs, updates, recommends, or removes a driver.

CPU temperature is an optional local-only metric. It appears only on a device that already has a compatible, functioning hardware-access component. On a clean certification device without that component, the CPU temperature slot displays `--°`; network, CPU usage, memory, NVIDIA GPU metrics, quota display, settings, and exit remain functional. No temperature value is transmitted by TaskbarTelemetry.

## Test guidance

1. Launch the app. The two-row overlay appears immediately to the left of the Windows notification area.
2. Right-click the overlay for refresh position, editable settings, notification/privacy settings, privacy policy, Windows startup settings, and Exit.
3. ServerChan notifications are disabled by default and do not need to be enabled for certification. Enabling requires explicit consent and a tester-owned SendKey; the key is DPAPI-protected in package LocalState.
4. The startup task is declared disabled and the app runs as `asInvoker` without UAC elevation.
5. Missing optional GPU, quota, or CPU-temperature data is represented by `--` and does not prevent the app from running.

The app is independent of and is not endorsed by OpenAI, LibreHardwareMonitor, PawnIO, NVIDIA, or the ServerChan operator.
