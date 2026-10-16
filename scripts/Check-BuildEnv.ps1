<#
Check-BuildEnv.ps1 - read-only check that this machine can build the Scenario Lab plug-in.
Changes nothing. Run in PowerShell on the XProtect machine:
    powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Check-BuildEnv.ps1
#>
$ErrorActionPreference = 'Continue'

function Show([string]$Label, $Value) { Write-Host ('  {0,-28} {1}' -f $Label, $Value) }

Write-Host 'Machine' -ForegroundColor Cyan
$os = Get-CimInstance Win32_OperatingSystem
Show 'Name' $env:COMPUTERNAME
Show 'RAM free' ('{0:N1} GB of {1:N1} GB' -f ($os.FreePhysicalMemory / 1MB), ($os.TotalVisibleMemorySize / 1MB))
$c = Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='C:'"
Show 'Disk C: free' ('{0:N0} GB' -f ($c.FreeSpace / 1GB))

Write-Host 'Git' -ForegroundColor Cyan
$git = Get-Command git -ErrorAction SilentlyContinue
Show 'git' $(if ($git) { (& git --version) } else { 'NOT FOUND' })

Write-Host 'MSBuild (Build Tools for Visual Studio)' -ForegroundColor Cyan
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (Test-Path $vswhere) {
    $msbuild = & $vswhere -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    Show 'MSBuild.exe' $(if ($msbuild) { $msbuild } else { 'NOT FOUND' })
    if ($msbuild) { Show 'MSBuild version' (& $msbuild -version -nologo | Select-Object -Last 1) }
} else {
    Show 'vswhere.exe' 'NOT FOUND (Build Tools not installed)'
}

Write-Host '.NET Framework targeting packs' -ForegroundColor Cyan
foreach ($v in 'v4.7', 'v4.8') {
    $p = "${env:ProgramFiles(x86)}\Reference Assemblies\Microsoft\Framework\.NETFramework\$v"
    Show $v $(if (Test-Path $p) { 'present' } else { 'missing' })
}

Write-Host 'Plug-in folder' -ForegroundColor Cyan
Show 'Milestone\MIPPlugins' $(if (Test-Path 'C:\Program Files\Milestone\MIPPlugins') { 'present' } else { 'missing' })
