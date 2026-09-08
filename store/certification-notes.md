# Certification notes

TaskbarTelemetry is a .NET Framework 4.8 WinForms desktop utility packaged as MSIX for Windows Desktop x64.

## Restricted capability justification: runFullTrust

TaskbarTelemetry requires medium-integrity full trust to host its own non-activating WinForms taskbar child window, read local CPU, GPU, memory and network telemetry through standard Windows/.NET desktop APIs, maintain isolated per-user package settings, and send an optional HTTPS quota notification to a user-configured ServerChan endpoint. The app does not request elevation, inject into or hook Explorer, install services or drivers, modify Windows security settings, or access other users' data.

The app uses standard User32 window discovery, rectangle and `SetParent` APIs to host its own window as a child of `Shell_TrayWnd` immediately to the left of the notification area. It does not inject code into or hook the Explorer process. The taskbar window can always be closed from its context menu.

## Test guidance

1. Launch the app. The two-row telemetry overlay appears immediately to the left of the Windows notification area. If Codex or supported GPU data is unavailable, the corresponding value displays `--`; the app remains functional.
2. Right-click the telemetry overlay to open its menu. The menu provides refresh position, editable settings, notification/privacy settings, privacy policy, Windows startup settings, and Exit.
3. ServerChan notifications are disabled by default. They require the user to open “通知与隐私设置”, review the disclosure, explicitly check consent, and provide their own SendKey. SC3 keys route to the numeric-user-ID host under push.ft07.com (App delivery); SCT keys use sctapi.ftqq.com (WeChat/configured channels). The key is protected for the current Windows user with DPAPI in package LocalState. No key or user credential is included in the package. Certification does not require enabling this optional feature.
4. The Microsoft Store build intentionally excludes the optional CPU-temperature backend and all of its libraries. It does not depend on, download, recommend, or install a third-party driver or NT service. The CPU row shows CPU usage only; supported NVIDIA GPU temperature remains available through the display driver's existing NVML interface.
5. The startup task is declared disabled. Launch the app once, then use “开机启动（打开系统设置）” to open Windows Settings > Apps > Startup if startup behavior needs to be tested.
6. The app runs as `asInvoker` and never requests UAC elevation.

The core taskbar telemetry functionality works without an account or network connection. Compatible local quota data is optional and absent data is handled without an error dialog. TaskbarTelemetry is an independent third-party utility and is not endorsed by OpenAI or the ServerChan operator.
