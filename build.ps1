<#
.SYNOPSIS
    Zero-dependency build script for LumeDesk (uses built-in Windows .NET Framework 4.8 csc.exe).
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\build.ps1
    powershell -ExecutionPolicy Bypass -File .\build.ps1 -Strict
    powershell -ExecutionPolicy Bypass -File .\build.ps1 -Run
#>
param(
    [switch]$Run,
    [switch]$Strict
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$fw = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
$csc = Join-Path $fw "csc.exe"
$wpf = Join-Path $fw "WPF"
$distDir = Join-Path $root "dist"
$outExe = Join-Path $distDir "LumeDesk.exe"
$outCfg = Join-Path $distDir "LumeDesk.exe.config"

if (-not (Test-Path $csc)) {
    throw "Built-in .NET Framework 4.8 compiler not found at: $csc"
}

if (-not (Test-Path $distDir)) {
    New-Item -ItemType Directory -Path $distDir | Out-Null
}

# Stop running LumeDesk (or legacy VertiHome) if active so we can overwrite exe cleanly
foreach ($procName in @("LumeDesk", "VertiHome")) {
    Get-Process -Name $procName -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}
Start-Sleep -Milliseconds 250

$sources = Get-ChildItem -Path (Join-Path $root "src") -Filter "*.cs" |
    Sort-Object Name |
    Select-Object -ExpandProperty FullName

if (-not $sources -or $sources.Count -eq 0) {
    throw "No C# source files found under $(Join-Path $root 'src')"
}

$argsList = @(
    "/nologo",
    "/target:winexe",
    "/platform:x64",
    "/optimize+",
    "/utf8output",
    "/win32manifest:`"$(Join-Path $root 'LumeDesk.manifest')`"",
    "/out:`"$outExe`"",
    "/r:`"$wpf\PresentationFramework.dll`"",
    "/r:`"$wpf\PresentationCore.dll`"",
    "/r:`"$wpf\WindowsBase.dll`"",
    "/r:`"$fw\System.Xaml.dll`"",
    "/r:`"$fw\System.Drawing.dll`"",
    "/r:`"$fw\System.Windows.Forms.dll`"",
    "/r:`"$fw\System.Web.Extensions.dll`"",
    "/r:`"$fw\System.Security.dll`"",
    "/r:`"$fw\System.Runtime.dll`""
)

if ($Strict) {
    $argsList += "/warn:4"
    $argsList += "/warnaserror+"
}

$winmdDir = "C:\Windows\System32\WinMetadata"
foreach ($md in @("Windows.Foundation.winmd", "Windows.Media.winmd", "Windows.Storage.winmd")) {
    $mdPath = Join-Path $winmdDir $md
    if (-not (Test-Path $mdPath)) {
        throw "Required Windows 10/11 WinMD metadata not found: $mdPath"
    }
    $argsList += "/r:`"$mdPath`""
}

$argsList += ($sources | ForEach-Object { "`"$_`"" })

Write-Host "Compiling LumeDesk.exe ($($sources.Count) source files)..."
$proc = Start-Process -FilePath $csc -ArgumentList ($argsList -join " ") -NoNewWindow -Wait -PassThru
if ($proc.ExitCode -ne 0) {
    throw "csc.exe failed with exit code $($proc.ExitCode)"
}

# Write .NET 4.8 Per-Monitor DPI v2 opt-in app.config alongside exe (critical for mixed-DPI multi-monitor setups)
$appConfig = @'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <startup>
    <supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8" />
  </startup>
  <runtime>
    <AppContextSwitchOverrides value="Switch.System.Windows.DoNotScaleForDpiChanges=false;Switch.System.Windows.Media.EnableHardwareAccelerationInRdp=true" />
  </runtime>
</configuration>
'@
[System.IO.File]::WriteAllText($outCfg, $appConfig, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "Build succeeded: $outExe (+ Per-Monitor DPI v2 app.config)"

if ($Run) {
    Start-Process -FilePath $outExe -WorkingDirectory $root
}
