# Third-party notices for the personal-match Store package

TaskbarTelemetry itself is original MIT-licensed code.

This package redistributes the unmodified LibreHardwareMonitor 0.9.6 runtime
library under MPL-2.0, together with the four Microsoft support assemblies
needed by the CPU-only path. Source and license:

- https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/v0.9.6
- https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LICENSE

The package contains no PawnIO file, driver, driver installer, Windows service,
or installation action. CPU temperature is optional and is available only when
the user's device already has a compatible, working hardware-access component.
TaskbarTelemetry does not download, install, update, or remove that component.

NVIDIA NVML is loaded only from the NVIDIA display driver already installed in
Windows System32; no NVIDIA binary is redistributed.

Optional quota notifications are sent only after explicit user consent through
the user's own ServerChan Turbo account. TaskbarTelemetry and its developer are
independent of and are not endorsed by OpenAI, LibreHardwareMonitor, PawnIO,
NVIDIA, or the ServerChan operator.
