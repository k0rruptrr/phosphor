# package.ps1 - build the hand-off package for a user without .NET:
#   dist\PHOSPHOR-<version>\        folder: Phosphor.exe, phosphor.json, fonts\, install/uninstall scripts, Установить.bat, Удалить.bat, ПРОЧТИ.txt
#   dist\PHOSPHOR-<version>.zip     the same folder, zipped (root folder inside the archive)
# <version> = ProductVersion of Phosphor.exe with '+' replaced by '-', e.g. 0.7.0+70ba44e -> 0.7.0-70ba44e
# Usage: powershell -ExecutionPolicy Bypass -File scripts\package.ps1 [-AllowDirty]
#   Refuses to build while src/, tests/, scripts/ or phosphor.json have uncommitted changes.
#   -AllowDirty: build anyway (debugging only, the package is then not for handing out).
# The package holds only Phosphor.exe; install.ps1 makes Phosphor.scr from it.
[CmdletBinding()]
param(
    [switch]$AllowDirty
)

$ErrorActionPreference = 'Stop'

$root    = Split-Path -Parent $PSScriptRoot
$distDir = Join-Path $root 'dist'
$phosDir = Join-Path $distDir 'Phosphor'
$exe     = Join-Path $phosDir 'Phosphor.exe'
$cfgRepo = Join-Path $root 'phosphor.json'
$tplDir  = Join-Path $PSScriptRoot 'package'

# 0. Refuse uncommitted changes in the paths that go into the build
$gitPaths = @('src', 'tests', 'scripts', 'phosphor.json')
$dirty = @()
$prevEap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'   # git may write warnings to stderr; that must not abort
$dirty = @(& git -C $root status --porcelain -- $gitPaths 2>$null)
$gitExit = $LASTEXITCODE
$ErrorActionPreference = $prevEap
if ($gitExit -ne 0) {
    throw "Cannot check git status (git exit $gitExit). Build refused; use -AllowDirty to build anyway."
}
if ($dirty.Count -gt 0) {
    if (-not $AllowDirty) {
        Write-Host 'Uncommitted changes in src/, tests/, scripts/ or phosphor.json:'
        $dirty | ForEach-Object { '  ' + $_ }
        throw 'Package NOT built: commit these changes first (or use -AllowDirty for a debug build).'
    }
    Write-Host ("WARNING: -AllowDirty: building from a dirty tree ({0} changed paths). Not for handing out." -f $dirty.Count)
}

# 1. Build (self-contained by default). publish.ps1 also copies phosphor.json and runs the smoke test.
Write-Host '== 1/5 publish.ps1 (self-contained) =='
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'publish.ps1')
if ($LASTEXITCODE -ne 0) {
    throw "publish.ps1 failed (exit code $LASTEXITCODE). The package was NOT built. Fix the build error above and run package.ps1 again."
}
if (-not (Test-Path -LiteralPath $exe)) { throw "Phosphor.exe not found after publish: $exe" }

# Version from the built exe
$pv = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
if ([string]::IsNullOrWhiteSpace($pv)) { throw "ProductVersion is empty in $exe" }
$ver  = ($pv.Trim() -replace '\+', '-') -replace '[\\/:*?"<>|\s]', '-'
$name = "PHOSPHOR-$ver"
$pkgDir = Join-Path $distDir $name
$zip    = Join-Path $distDir "$name.zip"
Write-Host "Version: $pv  ->  $name"

# 2. Assemble the folder
Write-Host "== 2/5 assemble $pkgDir =="
# Clear the contents, not the folder itself (it may be held open by Explorer or a sync tool)
if (Test-Path -LiteralPath $pkgDir) {
    Get-ChildItem -LiteralPath $pkgDir -Force | Remove-Item -Recurse -Force
} else {
    New-Item -ItemType Directory -Path $pkgDir | Out-Null
}

# Program files only (no logs, caches, .bak or .pdb from dist\Phosphor)
$skipped = @()
foreach ($f in (Get-ChildItem -LiteralPath $phosDir -File)) {
    if ($f.Name -eq 'Phosphor.exe') {
        Copy-Item -LiteralPath $f.FullName -Destination $pkgDir
    } elseif ($f.Name -notin 'phosphor.json', 'Phosphor.scr') {
        # phosphor.json: repo copy below. Phosphor.scr: not shipped, install.ps1 makes it from the exe.
        $skipped += $f.Name
    }
}

# phosphor.json: always the repo copy (the one in dist may be re-saved by the smoke-test run)
Copy-Item -LiteralPath $cfgRepo -Destination (Join-Path $pkgDir 'phosphor.json') -Force
if ((Get-FileHash -LiteralPath $cfgRepo).Hash -ne (Get-FileHash -LiteralPath (Join-Path $pkgDir 'phosphor.json')).Hash) {
    throw 'phosphor.json in the package differs from the repo copy'
}

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install.ps1') -Destination $pkgDir
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'uninstall.ps1') -Destination $pkgDir

# Built-in fonts: the whole fonts\ folder (TTF + OFL licence texts; the OFL requires the licence to travel with the font)
$fontsSrc = Join-Path $phosDir 'fonts'
if (-not (Test-Path -LiteralPath $fontsSrc -PathType Container)) { throw "fonts folder not found: $fontsSrc" }
$fontsDst = Join-Path $pkgDir 'fonts'
New-Item -ItemType Directory -Path $fontsDst | Out-Null
Copy-Item -Path (Join-Path $fontsSrc '*') -Destination $fontsDst -Recurse

# Logo for the badge (BadgeLayer, source=phosphor): logo\phosphor.png next to the exe, same way as fonts\
$logoSrc = Join-Path $phosDir 'logo'
if (-not (Test-Path -LiteralPath $logoSrc -PathType Container)) { throw "logo folder not found: $logoSrc" }
$logoDst = Join-Path $pkgDir 'logo'
New-Item -ItemType Directory -Path $logoDst | Out-Null
Copy-Item -Path (Join-Path $logoSrc '*') -Destination $logoDst -Recurse

# Templates -> user-facing files. CRLF line endings. bat: UTF-8 without BOM (chcp 65001 inside). txt: UTF-8 with BOM (Notepad).
function Write-Template([string]$src, [string]$dst, [bool]$withBom) {
    $text = [IO.File]::ReadAllText($src, [Text.Encoding]::UTF8)
    $text = ($text -replace "`r`n", "`n") -replace "`n", "`r`n"
    [IO.File]::WriteAllText($dst, $text, (New-Object Text.UTF8Encoding($withBom)))
}
Write-Template (Join-Path $tplDir 'pkg-install.bat')   (Join-Path $pkgDir 'Установить.bat') $false
Write-Template (Join-Path $tplDir 'pkg-uninstall.bat') (Join-Path $pkgDir 'Удалить.bat')     $false
Write-Template (Join-Path $tplDir 'pkg-readme.txt')    (Join-Path $pkgDir 'ПРОЧТИ.txt')      $true

Write-Host 'Files in package:'
Get-ChildItem -LiteralPath $pkgDir -File | Sort-Object Name | ForEach-Object {
    '  {0,-22} {1,12:N0} bytes' -f $_.Name, $_.Length
}
if ($skipped.Count -gt 0) { Write-Host ("Not included from dist\Phosphor: " + ($skipped -join ', ')) }

# 3. Zip
Write-Host '== 3/5 zip =='
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -LiteralPath $pkgDir -DestinationPath $zip

# 4. Check the archive
Write-Host '== 4/5 check archive =='
Add-Type -AssemblyName System.IO.Compression.FileSystem
$arc = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    $arc.Entries | ForEach-Object { '  ' + $_.FullName }
    $count = $arc.Entries.Count
} finally {
    $arc.Dispose()
}

$zipSize = (Get-Item -LiteralPath $zip).Length

# 5. One-click installer (Inno Setup 6): dist\PHOSPHOR-Setup-<x.y.z>.exe from the same folder.
#    Skipped with a warning when ISCC is not installed (winget install JRSoftware.InnoSetup --scope user).
Write-Host '== 5/5 installer (Inno Setup) =='
$iscc = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
$setup = $null
if ($iscc) {
    $appVer = ($pv.Trim() -split '\+')[0]
    $setupName = "PHOSPHOR-Setup-$appVer"
    & $iscc /Q "/DPkgDir=$pkgDir" "/DAppVer=$appVer" "/DOutName=$setupName" "/DOutDir=$distDir" (Join-Path $tplDir 'phosphor.iss')
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed (exit code $LASTEXITCODE)" }
    $setup = Join-Path $distDir "$setupName.exe"
} else {
    Write-Host 'WARNING: Inno Setup 6 not found - installer NOT built (zip is ready). winget install JRSoftware.InnoSetup --scope user'
}

Write-Host ''
Write-Host "Package folder : $pkgDir"
Write-Host ("Package zip    : {0}" -f $zip)
Write-Host ("Size           : {0:N2} MB ({1:N0} bytes), {2} entries" -f ($zipSize / 1MB), $zipSize, $count)
if ($setup) { Write-Host ("Installer      : {0} ({1:N2} MB)" -f $setup, ((Get-Item -LiteralPath $setup).Length / 1MB)) }
exit 0
