[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ExePath
)

$ErrorActionPreference = 'Stop'
$TaskName = 'AutoDefaultToHeadset.NativeAOT'

if (-not (Test-Path -LiteralPath $ExePath -PathType Leaf)) {
    throw "Application executable not found: $ExePath"
}

function Get-AudioDevices {
    param([ValidateSet('Output', 'Input')][string]$Kind)

    $lines = & $ExePath --list-devices 2>&1 | ForEach-Object { $_.ToString() }
    $devices = [System.Collections.Generic.List[object]]::new()
    $inKind = $false
    $state = $null

    foreach ($line in $lines) {
        if ($line -eq "$Kind devices:") { $inKind = $true; continue }
        if ($inKind -and $line -like 'Current defaults*') { break }
        if (-not $inKind) { continue }

        if ($line -match '^\s*State:\s*(?<State>\w+)') {
            $state = $Matches.State
            continue
        }

        if ($state -and $line -match '^\s*Name:\s*(?<Name>.+)$') {
            $devices.Add([pscustomobject]@{ Name = $Matches.Name.Trim(); State = $state })
            $state = $null
        }
    }

    if ($devices.Count -eq 0) {
        throw "Could not read $Kind devices from '$ExePath --list-devices'."
    }

    return @($devices | Sort-Object @{ Expression = { if ($_.State -eq 'Active') { 0 } elseif ($_.State -eq 'Unplugged') { 1 } else { 2 } } }, @{ Expression = { $_.Name.IndexOf('headset', [StringComparison]::OrdinalIgnoreCase) -lt 0 } }, @{ Expression = { $_.Name.IndexOf('headphones', [StringComparison]::OrdinalIgnoreCase) -lt 0 } }, Name)
}

function Select-Device {
    param([string]$Label, [object[]]$Devices)

    Write-Host "`nSelect $Label device:"
    for ($i = 0; $i -lt $Devices.Count; $i++) {
        Write-Host ('  {0}. [{1}] {2}' -f ($i + 1), $Devices[$i].State, $Devices[$i].Name)
    }

    while ($true) {
        $selection = Read-Host "$Label number"
        $number = 0
        if ([int]::TryParse($selection, [ref]$number) -and $number -ge 1 -and $number -le $Devices.Count) {
            return $Devices[$number - 1].Name
        }
        Write-Warning "Enter a number from 1 to $($Devices.Count)."
    }
}

$renderDevices = Get-AudioDevices Output
$captureDevices = Get-AudioDevices Input
$renderMatch = Select-Device 'output' $renderDevices
$captureMatch = Select-Device 'input' $captureDevices
$fallbackRenderMatch = Select-Device 'fallback output' $renderDevices
$fallbackCaptureMatch = Select-Device 'fallback input' $captureDevices
$disconnectRenderMatch = Select-Device 'Virtual Desktop output to watch for disconnect' $renderDevices

$arguments = @(
    '--background', '--render-match', $renderMatch,
    '--capture-match', $captureMatch,
    '--fallback-render-match', $fallbackRenderMatch,
    '--fallback-capture-match', $fallbackCaptureMatch,
    '--disconnect-render-match', $disconnectRenderMatch
)
$quotedArguments = ($arguments | ForEach-Object { '"' + $_.Replace('"', '\"') + '"' }) -join ' '

$userId = if ($env:USERDOMAIN) { "$env:USERDOMAIN\$env:USERNAME" } else { $env:USERNAME }
$taskAction = New-ScheduledTaskAction -Execute $ExePath -Argument $quotedArguments -WorkingDirectory (Split-Path -Parent $ExePath)
$taskTrigger = New-ScheduledTaskTrigger -AtLogOn -User $userId
$taskPrincipal = New-ScheduledTaskPrincipal -UserId $userId -LogonType Interactive -RunLevel Highest
Register-ScheduledTask -TaskName $TaskName -Action $taskAction -Trigger $taskTrigger -Principal $taskPrincipal -Description 'Switch default audio endpoints when configured devices connect or disconnect.' -Force | Out-Null

$uninstallPath = Join-Path (Split-Path -Parent $ExePath) 'uninstall.bat'
$registryPath = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$TaskName"
New-Item -Path $registryPath -Force | Out-Null
New-ItemProperty -Path $registryPath -Name DisplayName -Value 'AutoDefaultToHeadset (NativeAOT)' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $registryPath -Name DisplayIcon -Value $ExePath -PropertyType String -Force | Out-Null
New-ItemProperty -Path $registryPath -Name UninstallString -Value ('cmd.exe /c ""{0}""' -f $uninstallPath) -PropertyType String -Force | Out-Null
New-ItemProperty -Path $registryPath -Name QuietUninstallString -Value ('cmd.exe /c ""{0}""' -f $uninstallPath) -PropertyType String -Force | Out-Null
New-ItemProperty -Path $registryPath -Name Publisher -Value 'MLG-SERBUR' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $registryPath -Name DisplayVersion -Value '1.0' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $registryPath -Name NoModify -Value 1 -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $registryPath -Name NoRepair -Value 1 -PropertyType DWord -Force | Out-Null

Start-ScheduledTask -TaskName $TaskName
Write-Host "`nInstalled and launched AutoDefaultToHeadset."
Write-Host "Arguments: $quotedArguments"
