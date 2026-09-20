# AutoDefaultToHeadset.NativeAOT

Low-memory fork of AutoDefaultToHeadset. WinExe (no console window), exact friendly-name only, no endpoint IDs. The project emits a trimmed NativeAOT executable.

## Why

- Endpoint IDs (`{0.0.0.00000000}.{guid}`) rotate on Bluetooth re-pair / Xbox controller reconnect → stale `exists(all)=False` → does nothing.
- Friendly name (`Headphones (Xbox Controller)` / `Headset Microphone (Xbox Controller)`) stable → exact `Name.Equals(match, OrdinalIgnoreCase)` only.
- `WinExe` + `InvariantGlobalization` + trimming keeps the app hidden and minimizes deployment size. NativeAOT removes JIT/runtime overhead.

## Build

Requires .NET 8 SDK + Visual Studio 2022 or later with Desktop development with C++ + Windows SDK 10.0.19041+.

```cmd
build.bat
:: or
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishAot=true -p:PublishSingleFile=false -p:OptimizationPreference=Size
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
:: install.ps1 prompts for devices, installs Scheduled Task (on logon, highest privileges), and registers Add/Remove Programs
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

## Xbox / VR disconnect fallback

Use exact endpoint names from `--verbose --list-devices`. The configured headset output/input are watched automatically. Add Virtual Desktop output as an extra watched endpoint. When Xbox disconnects, or Windows changes away from Virtual Desktop output, the app sets both fallback endpoints.

`install.bat` copies files; `install.ps1` prompts for Xbox output/input, fallback output/input, then the Virtual Desktop output endpoint. Re-run `install.bat` after building to replace the Scheduled Task arguments. The resident executable has no installer mode.

```cmd
AutoDefaultToHeadset.NativeAOT.exe --background ^
  --render-match "Headphones (Xbox Controller)" ^
  --capture-match "Headset Microphone (Xbox Controller)" ^
  --disconnect-render-match "Virtual Desktop Audio" ^
  --fallback-render-match "PIXIO" ^
  --fallback-capture-match "Jouvino"
```

This uses existing Core Audio endpoint callbacks and cached endpoint IDs. Virtual Desktop default changes alone cause no action. When Virtual Desktop output becomes inactive/disabled, it checks `vrserver.exe` once. If SteamVR remains active, it waits 11 minutes and checks again; it only changes to PIXIO/Jouvino after `vrserver.exe` exits. Virtual Desktop recovery cancels the pending fallback. No polling, WMI watcher, or persistent process scan. `--disconnect-capture-match` exists for VR setups where input also has a reliable endpoint-state change; Virtual Desktop output state changes alone trigger both fallback output and input.

Diagnostics (verbose allocates console):

```
[INFO] Match render: Headphones (Xbox Controller) ...
[INFO] Set eConsole/eMultimedia/eCommunications via IPolicyConfig verify=OK
```

## Admin & Background Execution

On newer Windows 11 builds (24H2 / build 26100+), the undocumented `IPolicyConfig::SetDefaultEndpoint` COM interface requires an elevated token. Without it, switching fails with `0x80070005 (Access Denied)`.

**Do not use a Windows Service.** Services run in Session 0, which isolates audio routing. Core Audio (`IMMDeviceEnumerator` callbacks and default endpoints) are bound to the active user's session (Session 1+). 

**Solution:** Use Windows Task Scheduler. The installer automatically creates a task to run the app at logon with `/RL HIGHEST` inside the user session, bypassing UAC while retaining audio device access.

## Measured memory

Measured on this machine after startup, using Task Manager process values. MiB values use 1,048,576 bytes.

| Build | Executable | Working Set | Private / commit |
|---|---:|---:|---:|
| Before: trimmed single-file CoreCLR | 12.4 MB | 24.9 MiB | 6.9 MiB |
| After: NativeAOT WinExe | 2.58 MB | 19.54 MiB | 8.93 MiB |

NativeAOT reduced executable size and startup/runtime overhead. Private commit remains below 10 MiB. Working Set did not reach the <10 MiB target; Windows audio and COM pages account for much of the resident memory.

