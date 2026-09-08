# Microsoft Store package notices

The Microsoft Store package does not redistribute a hardware driver, Windows
service, LibreHardwareMonitor, PawnIO, an NVIDIA binary, or an OpenAI binary.

NVIDIA GPU metrics are read through NVML only when that interface is already
provided by the NVIDIA display driver installed on the user's device. The app
continues to run when no compatible NVIDIA GPU or NVML interface is available.

Optional quota notifications are sent only after explicit user consent through
the user's own ServerChan Turbo account. TaskbarTelemetry and its developer are
independent of and are not endorsed by OpenAI or the ServerChan operator.
