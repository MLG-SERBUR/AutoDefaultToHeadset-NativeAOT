# AutoDefaultToHeadset.NativeAOT

NativeAOT low-memory fork of AutoDefaultToHeadset. WinExe (no console window), exact friendly-name only, no endpoint IDs, ~8MB disk / ~10MB RAM vs ~35MB / ~30MB for self-contained.

## Why

- Endpoint IDs (`{0.0.0.00000000}.{guid}`) rotate on Bluetooth re-pair / Xbox controller reconnect → stale `exists(all)=False` → does nothing.
- Friendly name (`Headphones (Xbox Controller)` / `Headset Microphone (Xbox Controller)`) stable → exact `Name.Equals(match, OrdinalIgnoreCase)` only.
- NativeAOT `PublishAot` + `WinExe` + `InvariantGlobalization` + `TrimMode=partial` → no `conhost.exe`, no JIT, hidden by default.

## Build

Requires .NET 8 SDK + Windows SDK 10.0.19041+.

```cmd
build.bat
:: or
dotnet publish -c Release -r win-x64 --self-contained true
```

Output:

```
bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\AutoDefaultToHeadset.NativeAOT.exe  ~8 MB
```

## Install / Uninstall

Must run `install.bat` as Administrator.

```cmd
install.bat
:: terminates running instances, copies files to W:\_programs\AutoDefaultToHeadset
:: installs Scheduled Task (on logon, highest privileges), and registers in Add/Remove Programs
```

To uninstall, use **Add/Remove Programs** in Windows Settings, or run:
```cmd
W:\_programs\AutoDefaultToHeadset\uninstall.bat
```

Manual:

```cmd
AutoDefaultToHeadset.NativeAOT.exe --render-match "Headphones (Xbox Controller)" --capture-match "Headset Microphone (Xbox Controller)"
AutoDefaultToHeadset.NativeAOT.exe --verbose --list-devices  :: diagnostics with console
AutoDefaultToHeadset.NativeAOT.exe --background --render-match "Headphones (Xbox Controller)" --capture-match "Headset Microphone (Xbox Controller)"
```

Diagnostics (verbose allocates console):

```
[INFO] Match render: Headphones (Xbox Controller) ...
[INFO] Set eConsole/eMultimedia/eCommunications via IPolicyConfig verify=OK
```

## Admin & Background Execution

On newer Windows 11 builds (24H2 / build 26100+), the undocumented `IPolicyConfig::SetDefaultEndpoint` COM interface requires an elevated token. Without it, switching fails with `0x80070005 (Access Denied)`.

**Do not use a Windows Service.** Services run in Session 0, which isolates audio routing. Core Audio (`IMMDeviceEnumerator` callbacks and default endpoints) are bound to the active user's session (Session 1+). 

**Solution:** Use Windows Task Scheduler. The installer automatically creates a task to run the app at logon with `/RL HIGHEST` inside the user session, bypassing UAC while retaining audio device access.

## Memory

| Variant | Disk | WorkingSet |
|---------|------|------------|
| Self-contained (console+WinForms) | ~40MB | ~30MB + conhost 5MB |
| NativeAOT WinExe (this) | ~8MB | ~10MB, no window |

