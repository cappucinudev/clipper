# Builds Clipper.exe with the C# compiler that ships with Windows (.NET Framework 4).
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$out = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $out | Out-Null
Get-Process Clipper -ErrorAction SilentlyContinue | Stop-Process -Force

# App icon: assets\icon.png packed into a multi-size .ico (PNG entries, Vista+).
$ico = Join-Path $out 'app.ico'
Add-Type -AssemblyName System.Drawing
$src = [System.Drawing.Image]::FromFile((Join-Path $root 'assets\icon.png'))
$sizes = 256, 64, 48, 32, 16
$pngs = foreach ($n in $sizes) {
  $b = New-Object System.Drawing.Bitmap $n, $n
  $g = [System.Drawing.Graphics]::FromImage($b)
  $g.InterpolationMode = 'HighQualityBicubic'
  $g.DrawImage($src, 0, 0, $n, $n)
  $m = New-Object System.IO.MemoryStream
  $b.Save($m, [System.Drawing.Imaging.ImageFormat]::Png)
  ,$m.ToArray()
}
$src.Dispose()
$w = New-Object System.IO.BinaryWriter ([System.IO.File]::Create($ico))
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $n = $sizes[$i] % 256
  $w.Write([byte]$n); $w.Write([byte]$n); $w.Write([byte]0); $w.Write([byte]0)
  $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
  $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()

& $csc /nologo /target:winexe /optimize+ /out:"$out\Clipper.exe" /win32icon:"$ico" `
  /resource:"$root\ui\index.html,index.html" /resource:"$root\assets\icon.png,icon.png" `
  /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll `
  "$root\src\Clipper.cs"
if ($LASTEXITCODE) { exit $LASTEXITCODE }
Remove-Item $ico
Write-Host "Built $out\Clipper.exe"
