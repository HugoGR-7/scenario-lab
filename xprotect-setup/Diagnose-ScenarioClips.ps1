<#
Diagnose-ScenarioClips.ps1 - READ ONLY. Why does a Scenario Lab StableFPS camera show no video?

Run on p043 in PowerShell as Administrator, from the repository folder:
    powershell -NoProfile -ExecutionPolicy Bypass -File .\xprotect-setup\Diagnose-ScenarioClips.ps1 > C:\ScenarioLab\diagnose.txt
then send C:\ScenarioLab\diagnose.txt.

Compares our hardware (ports 200+) with one of Milestone's working clip cameras: the clip files on disk, the hardware
and camera objects, their driver settings, and the Recording Server's device log. Changes nothing.
#>
param([string]$ReferenceHardware = 'bin_pushed_over_onto_sidewalk')
$ErrorActionPreference = 'Continue'

function Section($title) { "`n==================== $title ====================" }

function Get-RawSettings($hardware) {
    $table = [ordered]@{}
    try {
        $settings = @($hardware.HardwareDriverSettingsFolder.HardwareDriverSettings)[0]
        $child = @($settings.HardwareDriverSettingsChildItems)[0]
        foreach ($key in ($child.Properties.Keys | Sort-Object)) {
            $short = $key -replace '/[0-9A-Fa-f-]{36}$', ''
            $table[$short] = $child.Properties.GetValue($key)
        }
    }
    catch { $table['ERROR'] = $_.Exception.Message }
    return $table
}

# ---------------------------------------------------------------- files
Section 'Clip files in StableFPS_Data\Video\H264'
$native = $null
foreach ($root in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
    $p = Join-Path $root 'Milestone\XProtect Recording Server\Drivers\NativeDrivers'
    if (Test-Path (Join-Path $p 'StableFPS_Data\Video\H264')) { $native = $p }
}
$h264 = Join-Path $native 'StableFPS_Data\Video\H264'
Get-ChildItem $h264 | Sort-Object LastWriteTime | Format-Table Name, Length, LastWriteTime -AutoSize | Out-String -Width 200
foreach ($f in Get-ChildItem $h264 | Where-Object { $_.Name -like "*vandalism_s1*" -or $_.Name -like "*$ReferenceHardware*" }) {
    $bytes = [IO.File]::ReadAllBytes($f.FullName)
    "{0}: first bytes {1}" -f $f.Name, (($bytes[0..7] | ForEach-Object { $_.ToString('x2') }) -join ' ')
}

Section 'StableFPS.xml files'
Get-ChildItem $native -Filter 'StableFPS.xml' -Recurse -Depth 2 | ForEach-Object {
    $text = Get-Content $_.FullName -Raw
    $rev = [regex]::Match($text, '\$Revision:\s*\d+').Value
    $mentions = ([regex]::Matches($text, 'vandalism_[su]\d')).Count
    "{0}  {1}  LastWrite {2}  mentions of our clips: {3}" -f $_.FullName, $rev, $_.LastWriteTime, $mentions
}

# ---------------------------------------------------------------- XProtect objects
Import-Module MilestonePSTools
Connect-Vms -ServerAddress 'http://localhost' -AcceptEula | Out-Null
Clear-VmsCache

$reference = Get-VmsHardware | Where-Object Name -eq $ReferenceHardware | Select-Object -First 1
$ours = @(Get-VmsHardware | Where-Object { $_.Address -match ':(\d+)/?$' -and [int]$Matches[1] -ge 200 })

foreach ($hw in @($reference) + $ours) {
    if (-not $hw) { continue }
    Section "Hardware $($hw.Name)"
    "Address: $($hw.Address)   Enabled: $($hw.Enabled)   Model: $($hw.Model)   Driver: $($hw.HardwareDriverPath)"
    foreach ($cam in @($hw.CameraFolder.Cameras)) {
        "Camera: '$($cam.Name)'  Enabled: $($cam.Enabled)  Channel: $($cam.Channel)  Id: $($cam.Id)  RecordingEnabled: $($cam.RecordingEnabled)"
    }
    $s = Get-RawSettings $hw
    foreach ($k in 'hardware/FPS', 'hardware/VideoCodec', 'hardware/Codec', 'hardware/VideoH264Files', 'hardware/FileName',
                   'hardware/MacAddress', 'hardware/DetectedModelName', 'hardware/FirmwareVersion', 'hardware/SerialNumber') {
        "  {0,-28} {1}" -f $k, $s[$k]
    }
}

Section 'Cameras state (from the Event Server)'
try {
    Get-VmsCameraStatus -ErrorAction Stop | Where-Object { $_.Name -like 'vandalism*' -or $_.Name -like "*$ReferenceHardware*" } |
        Format-Table -AutoSize | Out-String -Width 250
}
catch { "Get-VmsCameraStatus not available: $($_.Exception.Message)" }

# ---------------------------------------------------------------- Recording Server log
Section 'DeviceHandling.log: lines about ports 200-202 or our clips (last 60)'
$log = 'C:\ProgramData\Milestone\XProtect Recording Server\Logs\DeviceHandling.log'
if (Test-Path $log) {
    Get-Content $log -Tail 20000 | Select-String -Pattern '127\.0\.0\.1:20[0-9]', 'vandalism_[su]\d', ':20[0-9]/' | Select-Object -Last 60 |
        ForEach-Object { $_.Line }
    Section 'DeviceHandling.log: last 25 lines'
    Get-Content $log -Tail 25
}
else { "No log at $log" }
