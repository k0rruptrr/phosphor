# publish.ps1 - build PHOSPHOR into dist\Phosphor (Phosphor.exe + Phosphor.scr).
# Usage: powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 [-FrameworkDependent]
# Default is self-contained (.NET bundled, ~70 MB): the .scr runs on any PC.
[CmdletBinding()]
param(
    [switch]$FrameworkDependent
)

$ErrorActionPreference = 'Stop'

$root    = Split-Path -Parent $PSScriptRoot
$proj    = Join-Path $root 'src\Phosphor\Phosphor.csproj'
$outDir  = Join-Path $root 'dist\Phosphor'
$cfgSrc  = Join-Path $root 'phosphor.json'
$exe     = Join-Path $outDir 'Phosphor.exe'
$scr     = Join-Path $outDir 'Phosphor.scr'
$shotPng = Join-Path $root 'dist\check.png'
$selfVal = if ($FrameworkDependent) { 'false' } else { 'true' }

Write-Host "Project : $proj"
Write-Host "Output  : $outDir"
Write-Host "SelfContained: $selfVal"

# Clean output so no stale files stay in dist
if (Test-Path -LiteralPath $outDir) { Remove-Item -LiteralPath $outDir -Recurse -Force }
if (Test-Path -LiteralPath $shotPng) { Remove-Item -LiteralPath $shotPng -Force }

# Version info of the exe: commit hash (+ -dirty when the tree has uncommitted changes). No git: build without it, do not fail.
$revArg = @()
try {
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'SilentlyContinue'
    $rev = (& git -C $root rev-parse --short HEAD 2>$null)
    $revExit = $LASTEXITCODE
    $status = (& git -C $root status --porcelain 2>$null)
    $ErrorActionPreference = $prevEap
    if ($revExit -eq 0 -and $rev) {
        $rev = ("$rev").Trim()
        if (-not [string]::IsNullOrWhiteSpace(($status | Out-String))) { $rev += '-dirty' }
        $revArg = @("-p:SourceRevisionId=$rev")
        Write-Host "Revision: $rev"
    }
} catch {
    $ErrorActionPreference = 'Stop'
    $revArg = @()
}

& dotnet publish $proj -c Release -r win-x64 --self-contained $selfVal `
    -p:AgentId=publish -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=$selfVal -p:DebugType=none `
    @revArg -o $outDir
if ($LASTEXITCODE -ne 0) { Write-Host "dotnet publish failed (exit $LASTEXITCODE)"; exit 1 }

# phosphor.json must sit next to the exe (Config is read from AppContext.BaseDirectory)
if (-not (Test-Path -LiteralPath (Join-Path $outDir 'phosphor.json'))) {
    Copy-Item -LiteralPath $cfgSrc -Destination (Join-Path $outDir 'phosphor.json')
    Write-Host "Copied phosphor.json next to the exe"
}

if (-not (Test-Path -LiteralPath $exe)) { Write-Host "Phosphor.exe not found after publish"; exit 1 }
Copy-Item -LiteralPath $exe -Destination $scr -Force

# Smoke test: offscreen screenshot render
Write-Host "Smoke test: --shot ..."
$shotArgs = '--shot "' + $shotPng + '" --scene matrix --preset clean --frames 60 --size 640x360'
# Run the .exe, not the .scr: ShellExecute on .scr uses the "open" verb ("%1 /S") and drops our arguments,
# starting the real fullscreen screensaver instead of the shot. Same binary, so this is the same check.
$p = Start-Process -FilePath $exe -ArgumentList $shotArgs -PassThru -WindowStyle Hidden
if (-not $p.WaitForExit(120000)) {
    try { $p.Kill() } catch { }
    Write-Host "Smoke test timed out after 120 s (process killed). A MessageBox may have been shown."
}
$shotOk = (Test-Path -LiteralPath $shotPng) -and ($p.HasExited) -and ($p.ExitCode -eq 0)

if (-not $shotOk) {
    Write-Host "SMOKE TEST FAILED: check.png missing or exit code $($p.ExitCode)"
    $logs = Get-ChildItem -LiteralPath $env:TEMP -Filter 'phosphor*.log' -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending
    if ($logs) {
        Write-Host "--- tail of $($logs[0].FullName) ---"
        Get-Content -LiteralPath $logs[0].FullName -Encoding utf8 -Tail 30
    } else {
        Write-Host "No phosphor*.log found in $env:TEMP"
    }
    exit 1
}
Write-Host "Smoke test OK: $shotPng"

# Listing
Write-Host ""
Write-Host "Files in dist\Phosphor:"
$total = 0
Get-ChildItem -LiteralPath $outDir -File | Sort-Object Name | ForEach-Object {
    $total += $_.Length
    '{0,-22} {1,12:N0} bytes  ({2:N2} MB)' -f $_.Name, $_.Length, ($_.Length / 1MB)
}
'Total: {0:N2} MB' -f ($total / 1MB)
exit 0
