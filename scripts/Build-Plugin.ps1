<#
Build-Plugin.ps1 - build the Scenario Lab plug-in and deploy it to Smart Client.

Run in PowerShell as Administrator (writing to Program Files needs it), from the repository folder:
    powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build-Plugin.ps1            # build + deploy
    powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build-Plugin.ps1 -NoDeploy  # build only

Deploy copies only our own files (ScenarioLab.dll, its .pdb for readable error traces, plugin.def) to
C:\Program Files\Milestone\MIPPlugins\ScenarioLab. Never the VideoOS.* DLLs: Smart Client has its own.
It refuses to deploy while Smart Client runs, because Smart Client locks the DLL it has loaded.
#>
param(
    [switch]$NoDeploy,
    [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'

$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo 'plugin\ScenarioLab\ScenarioLab.csproj'
$output = Join-Path $repo "plugin\ScenarioLab\bin\$Configuration"
$deployFolder = 'C:\Program Files\Milestone\MIPPlugins\ScenarioLab'

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw 'Build Tools for Visual Studio not found. See README, "Build requirements".' }
$msbuild = & $vswhere -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild.exe not found.' }

Write-Host "Building $Configuration ..." -ForegroundColor Cyan
# -restore: download NuGet packages first.  -nr:false: no MSBuild helper processes left in memory afterwards.
& $msbuild $project -restore "-p:Configuration=$Configuration" -nr:false -v:minimal -nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed (MSBuild exit code $LASTEXITCODE)." }
Write-Host 'Build OK.' -ForegroundColor Green

if ($NoDeploy) { return }

if (Get-Process -Name 'Client' -ErrorAction SilentlyContinue) {
    throw 'Smart Client is running. Close it, then run this script again.'
}

New-Item -ItemType Directory -Force -Path $deployFolder | Out-Null
$files = 'ScenarioLab.dll', 'ScenarioLab.pdb', 'plugin.def' |
    ForEach-Object { Join-Path $output $_ } |
    Where-Object { Test-Path $_ }
Copy-Item $files $deployFolder -Force

Write-Host "Deployed to $deployFolder" -ForegroundColor Green
Get-ChildItem $deployFolder | Format-Table Name, Length, LastWriteTime -AutoSize
Write-Host 'Start Smart Client and open the "Scenario Lab" tab.'
