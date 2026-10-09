# uninstall.ps1 - restore the previous screensaver settings and remove the PHOSPHOR install folder.
# Usage: powershell -ExecutionPolicy Bypass -File scripts\uninstall.ps1 [-KeepConfig] [-WhatIf] [-Confirm]
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [switch]$KeepConfig
)

$ErrorActionPreference = 'Stop'

$dstDir   = Join-Path $env:LOCALAPPDATA 'Programs\Phosphor'
$scrDst   = Join-Path $dstDir 'Phosphor.scr'
$cfgDst   = Join-Path $dstDir 'phosphor.json'
$prevFile = Join-Path $dstDir 'previous-settings.json'
$desk     = 'HKCU:\Control Panel\Desktop'
$names    = @('SCRNSAVE.EXE', 'ScreenSaveActive', 'ScreenSaveTimeOut')

if ($WhatIfPreference) { Write-Host "[WhatIf] dry run: nothing will be changed or deleted" }

# 1. Restore registry values
if (Test-Path -LiteralPath $prevFile) {
    $prev = Get-Content -LiteralPath $prevFile -Encoding utf8 -Raw | ConvertFrom-Json
    foreach ($n in $names) {
        $entry = $prev.$n
        if ($null -ne $entry -and $entry.exists) {
            if ($PSCmdlet.ShouldProcess("$desk\$n", "Restore value '$($entry.value)'")) {
                Set-ItemProperty -LiteralPath $desk -Name $n -Value ([string]$entry.value)
            }
        } else {
            $cur = Get-ItemProperty -LiteralPath $desk
            if ($cur.PSObject.Properties.Name -contains $n) {
                if ($PSCmdlet.ShouldProcess("$desk\$n", 'Remove value (did not exist before install)')) {
                    Remove-ItemProperty -LiteralPath $desk -Name $n
                }
            } else {
                Write-Host "$n already absent, nothing to restore"
            }
        }
    }
    if ($PSCmdlet.ShouldProcess('user32.dll', 'UpdatePerUserSystemParameters')) {
        & rundll32.exe user32.dll,UpdatePerUserSystemParameters
    }
} else {
    Write-Host "WARNING: $prevFile not found - registry NOT restored automatically."
    $cur = Get-ItemProperty -LiteralPath $desk
    if ($cur.PSObject.Properties.Name -contains 'SCRNSAVE.EXE') {
        Write-Host "Current SCRNSAVE.EXE = $($cur.'SCRNSAVE.EXE')  (check it manually in control desk.cpl,,@screensaver)"
    }
}

# 2. Remove install folder
if (-not (Test-Path -LiteralPath $dstDir)) {
    Write-Host "Install folder not found: $dstDir (nothing to delete)"
    exit 0
}

if ($KeepConfig) {
    Write-Host "-KeepConfig: phosphor.json will be kept"
    Get-ChildItem -LiteralPath $dstDir -Force | Where-Object { $_.FullName -ne $cfgDst } | ForEach-Object {
        if ($PSCmdlet.ShouldProcess($_.FullName, 'Delete')) {
            Remove-Item -LiteralPath $_.FullName -Recurse -Force
        }
    }
} else {
    if ($PSCmdlet.ShouldProcess($dstDir, 'Delete entire install folder INCLUDING phosphor.json (user settings)')) {
        Remove-Item -LiteralPath $dstDir -Recurse -Force
    }
}

Write-Host ""
Write-Host "=== PHOSPHOR uninstall (or would be) ==="
Write-Host "Removed folder: $dstDir $(if ($KeepConfig) { '(except phosphor.json)' })"
if ($KeepConfig) { Write-Host "Kept config   : $cfgDst" }
exit 0
