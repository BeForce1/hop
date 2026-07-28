# csc.exe, not the dotnet SDK: one command, and hop.exe then runs on any Windows
# box with no runtime to install. .NET Framework 4.x ships with the OS.
$ErrorActionPreference = 'Stop'
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$wpf = Join-Path $fw 'WPF'

$refs = @(
    'System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll'
) | ForEach-Object { "/r:$(Join-Path $fw $_)" }

$refs += @('UIAutomationClient.dll', 'UIAutomationTypes.dll', 'WindowsBase.dll') |
    ForEach-Object { "/r:$(Join-Path $wpf $_)" }

$out = Join-Path $PSScriptRoot 'hop.exe'
$src = Join-Path $PSScriptRoot 'hop.cs'

# /target:winexe = no console window when double-clicked. --dump still prints if
# launched from a terminal that already has one attached.
& (Join-Path $fw 'csc.exe') /nologo /target:winexe /optimize+ /out:$out $refs $src

if (Test-Path $out) { "built $out ($([int]((Get-Item $out).Length / 1024)) KB)" }
