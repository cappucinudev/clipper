# Builds Clipper.exe with the C# compiler that ships with Windows (.NET Framework 4).
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$out = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $out | Out-Null
Get-Process Clipper -ErrorAction SilentlyContinue | Stop-Process -Force

# App icon: purple rounded square with a white play triangle.
$ico = Join-Path $out 'app.ico'
Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap 64, 64
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$r = 16
$path.AddArc(0, 0, $r, $r, 180, 90); $path.AddArc(63 - $r, 0, $r, $r, 270, 90)
$path.AddArc(63 - $r, 63 - $r, $r, $r, 0, 90); $path.AddArc(0, 63 - $r, $r, $r, 90, 90)
$g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(109, 93, 252))), $path)
$g.FillPolygon([System.Drawing.Brushes]::White, [System.Drawing.Point[]]@((New-Object System.Drawing.Point 24, 18), (New-Object System.Drawing.Point 48, 32), (New-Object System.Drawing.Point 24, 46)))
$fs = [System.IO.File]::Create($ico)
[System.Drawing.Icon]::FromHandle($bmp.GetHicon()).Save($fs)
$fs.Close()

& $csc /nologo /target:winexe /optimize+ /out:"$out\Clipper.exe" /win32icon:"$ico" `
  /resource:"$root\ui\index.html,index.html" `
  /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll `
  "$root\src\Clipper.cs"
if ($LASTEXITCODE) { exit $LASTEXITCODE }
Remove-Item $ico
Write-Host "Built $out\Clipper.exe"
