# Builds the PHOSPHOR logo assets from the original PNG (Windows PowerShell 5.1 + System.Drawing).
#
# Outputs in assets/logo:
#   phosphor.png       - copy of the original (only when -Source is given and differs)
#   phosphor-512.png   - high-quality downscale (HighQualityBicubic, alpha kept)
#   phosphor.ico       - multi-size icon: 16, 24, 32, 48, 64, 128, 256 (256 is PNG-compressed)
#
# Sizes 16 and 24 always use the simplified variant (same silhouette, dark fill #0B1F18,
# 1 px bright-green rim #3CFF7A alpha ~0.85 on the mask edge). Size 32 uses -Size32Mode.
# Sizes 48+ use the plain downscale of the original.
#
# -PreviewDir <dir> additionally writes 8x nearest-neighbour previews of both 16/24/32 variants.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts\make-icon.ps1 [-Source <png>] [-Size32Mode scaled|simple] [-PreviewDir <dir>]

param(
    [string]$Source = '',
    [ValidateSet('scaled', 'simple')][string]$Size32Mode = 'scaled',
    [string]$PreviewDir = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$logoDir = Join-Path $root 'assets\logo'
if (-not (Test-Path $logoDir)) { New-Item -ItemType Directory -Path $logoDir | Out-Null }
$logoDir = (Resolve-Path $logoDir).Path
$orig = Join-Path $logoDir 'phosphor.png'

if ($Source -ne '') {
    $srcFull = (Resolve-Path $Source).Path
    if ($srcFull -ne $orig) { Copy-Item -Path $srcFull -Destination $orig -Force }
}
if (-not (Test-Path $orig)) { throw "Original not found: $orig (pass -Source)" }

function New-Scaled([System.Drawing.Image]$src, [int]$size) {
    $bmp = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
        $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $g.DrawImage($src, 0, 0, $size, $size)
    } finally { $g.Dispose() }
    return $bmp
}

function Get-Bgra([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $bytes = [byte[]]::new($w * $h * 4)
    $rect = [System.Drawing.Rectangle]::new(0, 0, $w, $h)
    $d = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try { [System.Runtime.InteropServices.Marshal]::Copy($d.Scan0, $bytes, 0, $bytes.Length) }
    finally { $bmp.UnlockBits($d) }
    return , $bytes
}

function New-FromBgra([byte[]]$bytes, [int]$w, [int]$h) {
    $bmp = [System.Drawing.Bitmap]::new($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $rect = [System.Drawing.Rectangle]::new(0, 0, $w, $h)
    $d = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::WriteOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try { [System.Runtime.InteropServices.Marshal]::Copy($bytes, 0, $d.Scan0, $bytes.Length) }
    finally { $bmp.UnlockBits($d) }
    return $bmp
}

# Simplified variant: silhouette = alpha of the downscaled original.
function New-Simple([System.Drawing.Image]$src, [int]$size) {
    $base = New-Scaled $src $size
    $px = Get-Bgra $base
    $base.Dispose()
    $n = $size * $size
    $a = [byte[]]::new($n)
    for ($i = 0; $i -lt $n; $i++) { $a[$i] = $px[$i * 4 + 3] }
    $out = [byte[]]::new($n * 4)
    for ($y = 0; $y -lt $size; $y++) {
        for ($x = 0; $x -lt $size; $x++) {
            $i = $y * $size + $x
            $av = $a[$i]
            if ($av -eq 0) { continue }
            $o = $i * 4
            $edge = $false
            if ($av -ge 128) {
                if ($x -eq 0 -or $y -eq 0 -or $x -eq ($size - 1) -or $y -eq ($size - 1)) { $edge = $true }
                elseif ($a[$i - 1] -lt 128 -or $a[$i + 1] -lt 128 -or $a[$i - $size] -lt 128 -or $a[$i + $size] -lt 128) { $edge = $true }
            }
            if ($edge) {
                # rim #3CFF7A, alpha 0.85 (BGRA order)
                $out[$o] = 0x7A; $out[$o + 1] = 0xFF; $out[$o + 2] = 0x3C; $out[$o + 3] = 217
            } else {
                # fill #0B1F18, alpha from silhouette (BGRA order)
                $out[$o] = 0x18; $out[$o + 1] = 0x1F; $out[$o + 2] = 0x0B; $out[$o + 3] = $av
            }
        }
    }
    return New-FromBgra $out $size $size
}

function Save-Preview([System.Drawing.Bitmap]$bmp, [string]$path) {
    $k = 8
    $big = [System.Drawing.Bitmap]::new($bmp.Width * $k, $bmp.Height * $k, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($big)
    try {
        $g.Clear([System.Drawing.Color]::FromArgb(255, 48, 48, 48))
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
        $g.DrawImage($bmp, 0, 0, $big.Width, $big.Height)
    } finally { $g.Dispose() }
    $big.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $big.Dispose()
}

function Get-IcoImageData([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $ms = New-Object System.IO.MemoryStream
    if ($w -ge 256) {
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        return , $ms.ToArray()
    }
    $px = Get-Bgra $bmp
    $row = $w * 4
    $xor = [byte[]]::new($px.Length)
    for ($y = 0; $y -lt $h; $y++) { [Array]::Copy($px, ($h - 1 - $y) * $row, $xor, $y * $row, $row) }  # DIB is bottom-up
    $andStride = [int]([math]::Floor(($w + 31) / 32) * 4)
    $and = [byte[]]::new($andStride * $h)   # all zero: transparency comes from the alpha channel
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([int32]40)
    $bw.Write([int32]$w)
    $bw.Write([int32]($h * 2))
    $bw.Write([int16]1)
    $bw.Write([int16]32)
    $bw.Write([int32]0)
    $bw.Write([int32]($xor.Length + $and.Length))
    $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([int32]0)
    $bw.Write($xor)
    $bw.Write($and)
    $bw.Flush()
    return , $ms.ToArray()
}

function Write-Ico([System.Collections.Generic.List[object]]$frames, [string]$path) {
    $datas = New-Object 'System.Collections.Generic.List[byte[]]'
    foreach ($f in $frames) { $datas.Add([byte[]](Get-IcoImageData $f.Bitmap)) }
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([uint16]0)
    $bw.Write([uint16]1)
    $bw.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    for ($i = 0; $i -lt $frames.Count; $i++) {
        $s = [int]$frames[$i].Size
        $bw.Write([byte]($s % 256))
        $bw.Write([byte]($s % 256))
        $bw.Write([byte]0)
        $bw.Write([byte]0)
        $bw.Write([uint16]1)
        $bw.Write([uint16]32)
        $bw.Write([uint32]$datas[$i].Length)
        $bw.Write([uint32]$offset)
        $offset += $datas[$i].Length
    }
    foreach ($d in $datas) { $bw.Write($d) }
    $bw.Flush()
    [System.IO.File]::WriteAllBytes($path, $ms.ToArray())
    $bw.Dispose()
    $ms.Dispose()
}

$bytes = [System.IO.File]::ReadAllBytes($orig)
$srcStream = New-Object System.IO.MemoryStream(, $bytes)
$img = [System.Drawing.Image]::FromStream($srcStream)
try {
    # 512 px preview/asset
    $b512 = New-Scaled $img 512
    $b512.Save((Join-Path $logoDir 'phosphor-512.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    $b512.Dispose()

    $frames = New-Object 'System.Collections.Generic.List[object]'
    $toDispose = New-Object 'System.Collections.Generic.List[object]'
    foreach ($s in 16, 24) {
        $b = New-Simple $img $s
        $frames.Add([pscustomobject]@{ Size = $s; Bitmap = $b }); $toDispose.Add($b)
    }
    if ($Size32Mode -eq 'simple') { $b = New-Simple $img 32 } else { $b = New-Scaled $img 32 }
    $frames.Add([pscustomobject]@{ Size = 32; Bitmap = $b }); $toDispose.Add($b)
    foreach ($s in 48, 64, 128, 256) {
        $b = New-Scaled $img $s
        $frames.Add([pscustomobject]@{ Size = $s; Bitmap = $b }); $toDispose.Add($b)
    }
    Write-Ico $frames (Join-Path $logoDir 'phosphor.ico')

    if ($PreviewDir -ne '') {
        if (-not (Test-Path $PreviewDir)) { New-Item -ItemType Directory -Path $PreviewDir | Out-Null }
        foreach ($s in 16, 24, 32) {
            $sc = New-Scaled $img $s
            Save-Preview $sc (Join-Path $PreviewDir "icon-scaled-$s.png"); $sc.Dispose()
            $si = New-Simple $img $s
            Save-Preview $si (Join-Path $PreviewDir "icon-simple-$s.png"); $si.Dispose()
        }
    }
    foreach ($d in $toDispose) { $d.Dispose() }
} finally {
    $img.Dispose()
    $srcStream.Dispose()
}

Write-Output "Wrote: $orig, $(Join-Path $logoDir 'phosphor-512.png'), $(Join-Path $logoDir 'phosphor.ico') (32 mode: $Size32Mode)"
