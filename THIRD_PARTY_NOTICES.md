# Third-party notices

TaskbarTelemetry itself is original MIT-licensed code. It does not copy the
TrafficMonitor or Token Monitor source trees.

The optional CPU-temperature backend loads the unmodified
LibreHardwareMonitor 0.9.6 release at runtime. LibreHardwareMonitor is licensed
under MPL-2.0. Its source and license are available at:

- https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/v0.9.6
- https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LICENSE

The published runtime contains only LibreHardwareMonitorLib.dll and the four
Microsoft support assemblies needed by the CPU-only path. Unused GUI, storage,
HID, RAM/SPD, task-scheduler, and plotting assemblies from the upstream release
are deliberately not redistributed.

LibreHardwareMonitor 0.9.6 can use PawnIO. PawnIO is not bundled or silently
installed by this project. Install only the official restricted, signed PawnIO
release from https://pawnio.eu/ after reviewing its GPL-2.0 license and special
exception.

NVIDIA NVML is loaded from the NVIDIA display driver already installed in the
Windows System32 directory; no NVIDIA binary is redistributed here.

Codex quota data can be read through the documented OpenAI Codex app-server
protocol or from numeric rate-limit fields in recent local Codex session event
lines. TaskbarTelemetry never opens auth.json or stores Codex access tokens.
