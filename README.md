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

## Install (exact name, persistent)

```cmd
install.bat
:: picks output/input from active list, stores --render-match "Headphones (Xbox Controller)" --capture-match "Headset Microphone (Xbox Controller)"
```

Manual:

```cmd
AutoDefaultToHeadset.NativeAOT.exe --render-match "Headphones (Xbox Controller)" --capture-match "Headset Microphone (Xbox Controller)"
AutoDefaultToHeadset.NativeAOT.exe --verbose --list-devices  :: diagnostics with console
AutoDefaultToHeadset.NativeAOT.exe --background --render-match "Headphones (Xbox Controller)" --capture-match "Headset Microphone (Xbox Controller)"  :: hidden background (Task Scheduler /RL HIGHEST for admin)
```

Diagnostics (verbose allocates console):

```
[INFO] Match render: Headphones (Xbox Controller) ...
[INFO] Set eConsole/eMultimedia/eCommunications via IPolicyConfig verify=OK
```

## Admin

`IPolicyConfig::SetDefaultEndpoint` needs elevated token on Win11 25H2 (26200) → `Admin: False` → `0x80070005`. Run as Administrator or:

```cmd
schtasks /Create /SC ONLOGON /TN "AutoDefaultToHeadset.NativeAOT" /TR "\"C:\path\AutoDefaultToHeadset.NativeAOT.exe\" --background --render-match \"Headphones (Xbox Controller)\" --capture-match \"Headset Microphone (Xbox Controller)\"" /RL HIGHEST /F
```

## Memory

| Variant | Disk | WorkingSet |
|---------|------|------------|
| Self-contained (console+WinForms) | ~40MB | ~30MB + conhost 5MB |
| NativeAOT WinExe (this) | ~8MB | ~10MB, no window |

