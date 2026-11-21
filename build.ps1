# Builds PrayerTray.exe with the C# compiler that ships with Windows (.NET Framework 4.x)
# and installs it to %LOCALAPPDATA%\PrayerTray. No downloads needed.
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$fw   = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$out  = Join-Path $env:LOCALAPPDATA 'PrayerTray'
New-Item -ItemType Directory -Force $out | Out-Null

Get-Process PrayerTray -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

& "$fw\csc.exe" /nologo /target:winexe /optimize+ /codepage:65001 `
    /out:"$out\PrayerTray.exe" `
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:"$fw\System.Web.Extensions.dll" `
    "$here\PrayerTray.cs"
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

if (Test-Path "$here\adhan.wav") { Copy-Item "$here\adhan.wav" $out -Force }
Write-Host "Built $out\PrayerTray.exe"
