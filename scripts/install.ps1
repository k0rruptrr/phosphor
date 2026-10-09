# install.ps1 - install PHOSPHOR as the current user's screensaver (no admin rights needed).
# Usage: powershell -ExecutionPolicy Bypass -File scripts\install.ps1 [-TimeoutMinutes N] [-WhatIf]
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateRange(0, 1440)]
    [int]$TimeoutMinutes = 0
)

$ErrorActionPreference = 'Stop'

$root     = Split-Path -Parent $PSScriptRoot
$srcDir   = Join-Path $root 'dist\Phosphor'
$dstDir   = Join-Path $env:LOCALAPPDATA 'Programs\Phosphor'
$scrSrc   = Join-Path $srcDir 'Phosphor.scr'
$scrDst   = Join-Path $dstDir 'Phosphor.scr'
$cfgDst   = Join-Path $dstDir 'phosphor.json'
$prevFile = Join-Path $dstDir 'previous-settings.json'
$desk     = 'HKCU:\Control Panel\Desktop'
$names    = @('SCRNSAVE.EXE', 'ScreenSaveActive', 'ScreenSaveTimeOut')

if (-not (Test-Path -LiteralPath $scrSrc)) {
    throw "Not found: $scrSrc. Run scripts\publish.ps1 first."
}

if ($WhatIfPreference) { Write-Host "[WhatIf] dry run: nothing will be copied or written" }

# 1. Copy files
Write-Host "Install folder: $dstDir"
if ($PSCmdlet.ShouldProcess($dstDir, 'Create folder')) {
    New-Item -ItemType Directory -Force -Path $dstDir | Out-Null
}

Get-ChildItem -LiteralPath $srcDir -File | ForEach-Object {
    if ($_.Name -eq 'phosphor.json') {
        if (Test-Path -LiteralPath $cfgDst) {
            $defDst = Join-Path $dstDir 'phosphor.default.json'
            if ($PSCmdlet.ShouldProcess($defDst, 'Copy new default config (user phosphor.json kept)')) {
                Copy-Item -LiteralPath $_.FullName -Destination $defDst -Force
            }
        } else {
            if ($PSCmdlet.ShouldProcess($cfgDst, 'Copy default config')) {
                Copy-Item -LiteralPath $_.FullName -Destination $cfgDst
            }
        }
    } else {
        if ($PSCmdlet.ShouldProcess((Join-Path $dstDir $_.Name), 'Copy file')) {
            Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $dstDir $_.Name) -Force
        }
    }
}

# 2. Save previous screensaver settings (only once, so re-install does not overwrite the backup)
if (Test-Path -LiteralPath $prevFile) {
    Write-Host "Backup of previous settings already exists, keeping it: $prevFile"
} else {
    $cur = Get-ItemProperty -LiteralPath $desk
    $backup = [ordered]@{}
    foreach ($n in $names) {
        $has = $cur.PSObject.Properties.Name -contains $n
        $backup[$n] = [ordered]@{ exists = $has; value = if ($has) { [string]$cur.$n } else { $null } }
    }
    $json = $backup | ConvertTo-Json -Depth 4
    if ($PSCmdlet.ShouldProcess($prevFile, 'Save previous screensaver settings')) {
        Set-Content -LiteralPath $prevFile -Value $json -Encoding utf8
    }
    if (-not $WhatIfPreference) { Write-Host "Previous settings saved to: $prevFile" }
}

# 3. Registry: screensaver = installed Phosphor.scr (short 8.3 path)
if (Test-Path -LiteralPath $scrDst) {
    $fso = New-Object -ComObject Scripting.FileSystemObject
    $short = $fso.GetFile($scrDst).ShortPath
} else {
    $short = '<8.3 path of ' + $scrDst + ' - computed at real install>'
}
if ((Test-Path -LiteralPath $scrDst) -and ($short -match ' ')) { Write-Host "WARNING: short path still contains a space: $short" }

if ($PSCmdlet.ShouldProcess("$desk\SCRNSAVE.EXE", "Set to $short")) {
    Set-ItemProperty -LiteralPath $desk -Name 'SCRNSAVE.EXE' -Value $short
}
if ($PSCmdlet.ShouldProcess("$desk\ScreenSaveActive", 'Set to 1')) {
    Set-ItemProperty -LiteralPath $desk -Name 'ScreenSaveActive' -Value '1'
}
if ($TimeoutMinutes -gt 0) {
    $secs = [string]($TimeoutMinutes * 60)
    if ($PSCmdlet.ShouldProcess("$desk\ScreenSaveTimeOut", "Set to $secs s ($TimeoutMinutes min)")) {
        Set-ItemProperty -LiteralPath $desk -Name 'ScreenSaveTimeOut' -Value $secs
    }
} else {
    Write-Host "ScreenSaveTimeOut not changed (no -TimeoutMinutes given)"
}

# 4. Apply without re-logon
if ($PSCmdlet.ShouldProcess('user32.dll', 'UpdatePerUserSystemParameters')) {
    & rundll32.exe user32.dll,UpdatePerUserSystemParameters
}

# 5. Summary
Write-Host ""
Write-Host "=== PHOSPHOR installed (or would be) ==="
Write-Host "Location     : $dstDir"
Write-Host "Screensaver  : $scrDst  (registry SCRNSAVE.EXE = $short)"
Write-Host "Config       : $cfgDst  (edit this file)"
Write-Host "Settings UI  : control desk.cpl,,@screensaver"
Write-Host "Fort Firewall: allow network for this exe: $scrDst"
Write-Host "               (if network news/weather fail with error 10013, this is the cause)"
exit 0
