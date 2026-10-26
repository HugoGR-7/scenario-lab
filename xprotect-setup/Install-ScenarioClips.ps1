<#
Install-ScenarioClips.ps1 - load generated clips into XProtect as StableFPS cameras (one camera per clip).

Run on the XProtect machine in PowerShell as Administrator, from the repository folder:
    powershell -NoProfile -ExecutionPolicy Bypass -File .\xprotect-setup\Install-ScenarioClips.ps1
    ... -Step Files      only copy the clips and restart the Recording Server
    ... -Step Cameras    only add the cameras (after Files)
    ... -Step Inspect    change nothing: print the StableFPS settings of a Milestone camera and of ours

Input (default: the repository's demo\stablefps): the ready clips as raw H.264 files (832x480_<camera name>) and
clips.json, which lists camera name -> file. To install clips of your own, convert them with scripts/convert_clips.py
and pass that folder with -ClipFolder.

What it does (guides/stablefps-custom-clips.md, confirmed by Milestone support on 22 Oct 2026):
  Files:   wait until the Recording Server (RS) has run 90 s -> stop it -> copy the clips into StableFPS_Data\Video\H264
           -> back up StableFPS.xml and change its revision number (that makes the driver re-read its file list)
           -> start the RS -> wait 2.5 minutes.
  Cameras: for each clip, add one StableFPS hardware device at 127.0.0.1:<port> (ports 200 and up; 100-199 belong to
           Milestone), named after the camera; copy the settings Milestone uses on its own clip camera (10 fps, H264)
           and point the file setting at our clip; name the camera the same; put it in the camera group "Scenario Lab".

Never touches Milestone's 11 cameras: their hardware is only READ, to copy its settings. Safe to run again: clips
already copied are replaced, cameras that already exist (same name) are skipped.
#>
param(
    [string]$ClipFolder = (Join-Path (Split-Path $PSScriptRoot -Parent) 'demo\stablefps'),
    [ValidateSet('All', 'Files', 'Cameras', 'Inspect')][string]$Step = 'All',
    [int]$FirstPort = 200,
    # A Milestone StableFPS camera whose settings we copy (read only).
    [string]$ReferenceHardware = 'bin_pushed_over_onto_sidewalk',
    [string]$CameraGroup = 'Scenario Lab'
)
$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run PowerShell as Administrator.'
}
if ($FirstPort -lt 200) { throw 'Ports below 200 belong to Milestone. Use 200 or higher.' }

# ---------------------------------------------------------------- clips.json
$manifestPath = Join-Path $ClipFolder 'clips.json'
if ($Step -ne 'Inspect') {
    if (-not (Test-Path $manifestPath)) { throw "Not found: $manifestPath (the repository's demo\stablefps, or a folder made by scripts/convert_clips.py)." }
    # Windows PowerShell 5.1's ConvertFrom-Json returns a JSON list as ONE object; piping it through ForEach-Object
    # unrolls it into one item per clip.
    $clips = @((Get-Content $manifestPath -Raw | ConvertFrom-Json) | ForEach-Object { $_ })
    foreach ($clip in $clips) {
        if ($clip.camera -notmatch '^[A-Za-z0-9_-]+$') { throw "Bad camera name '$($clip.camera)'." }
        if (-not (Test-Path (Join-Path $ClipFolder $clip.file))) { throw "Clip file missing: $($clip.file)" }
    }
    Write-Host "$($clips.Count) clip(s) in $ClipFolder" -ForegroundColor Cyan
    $clips | ForEach-Object { Write-Host "  $($_.camera)  <-  $($_.file)" }
}

# ---------------------------------------------------------------- Files
function Find-NativeDrivers {
    foreach ($root in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        $path = Join-Path $root 'Milestone\XProtect Recording Server\Drivers\NativeDrivers'
        if (Test-Path (Join-Path $path 'StableFPS_Data\Video\H264')) { return $path }
    }
    throw 'StableFPS_Data\Video\H264 not found under Program Files or Program Files (x86).'
}

function Get-RecordingServerService {
    $svc = Get-CimInstance Win32_Service -Filter "DisplayName='Milestone XProtect Recording Server'"
    if (-not $svc) { throw "Service 'Milestone XProtect Recording Server' not found." }
    return $svc
}

function Wait-RecordingServerUptime([int]$Seconds = 90) {
    # Kit rule: stopping the RS less than 90 s after it started can hang it.
    $svc = Get-RecordingServerService
    if ($svc.State -ne 'Running' -or -not $svc.ProcessId) { return }
    $started = (Get-Process -Id $svc.ProcessId).StartTime
    $wait = [int]($Seconds - ((Get-Date) - $started).TotalSeconds)
    if ($wait -gt 0) {
        Write-Host "The Recording Server started $([int]((Get-Date) - $started).TotalSeconds) s ago; waiting $wait s before stopping it ..."
        Start-Sleep -Seconds $wait
    }
}

function Update-StableFpsRevision([string]$NativeDrivers) {
    # READ ME (FAQ Q1): any change to the revision number makes the driver re-read the clip list. The file is in
    # NativeDrivers or NativeDrivers\resources depending on the document: change every copy we find.
    $files = @(Get-ChildItem $NativeDrivers -Filter 'StableFPS.xml' -Recurse -Depth 2 -ErrorAction SilentlyContinue)
    if ($files.Count -eq 0) { throw "StableFPS.xml not found under $NativeDrivers" }
    $backupFolder = 'C:\ScenarioLab\backup'
    New-Item -ItemType Directory -Force -Path $backupFolder | Out-Null
    foreach ($file in $files) {
        $bytes = [IO.File]::ReadAllBytes($file.FullName)
        $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
        $text = [IO.File]::ReadAllText($file.FullName)
        $match = [regex]::Match($text, '\$Revision:\s*(\d+)')
        if (-not $match.Success) { throw "No `$Revision: number in $($file.FullName). Change it by hand (guide, step 4)." }
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        Copy-Item $file.FullName (Join-Path $backupFolder "StableFPS.xml.$stamp.$($file.Directory.Name).bak")
        $old = [int64]$match.Groups[1].Value
        $new = $old + 1
        $text = $text.Substring(0, $match.Groups[1].Index) + $new + $text.Substring($match.Groups[1].Index + $match.Groups[1].Length)
        [IO.File]::WriteAllText($file.FullName, $text, (New-Object Text.UTF8Encoding($hasBom)))
        Write-Host "  $($file.FullName): revision $old -> $new (backup in $backupFolder)"
    }
}

if ($Step -in 'All', 'Files') {
    $native = Find-NativeDrivers
    $h264 = Join-Path $native 'StableFPS_Data\Video\H264'
    Write-Host "StableFPS clip folder: $h264" -ForegroundColor Cyan

    Wait-RecordingServerUptime
    Write-Host 'Stopping the Recording Server (all cameras pause recording for a few minutes) ...'
    $svc = Get-RecordingServerService
    Stop-Service -Name $svc.Name
    try {
        foreach ($clip in $clips) {
            Copy-Item (Join-Path $ClipFolder $clip.file) (Join-Path $h264 $clip.file) -Force
            Write-Host "  copied $($clip.file)"
        }
        Update-StableFpsRevision $native
    }
    finally {
        Write-Host 'Starting the Recording Server ...'
        Start-Service -Name $svc.Name
    }
    Write-Host 'Waiting 150 s for the driver to load the clips ...'
    Start-Sleep -Seconds 150
    Write-Host 'Files done.' -ForegroundColor Green
}

# ---------------------------------------------------------------- Cameras
function Connect-Xprotect {
    Import-Module MilestonePSTools
    Connect-Vms -ServerAddress 'http://localhost' -AcceptEula | Out-Null
    Clear-VmsCache
}

function ConvertTo-Hashtable($settings) {
    $table = @{}
    if ($settings -is [Collections.IDictionary]) {
        foreach ($key in $settings.Keys) { $table[$key] = $settings[$key] }
    }
    else {
        foreach ($p in $settings.PSObject.Properties) { $table[$p.Name] = $p.Value }
    }
    return $table
}

# The hardware's driver settings ("Settings" tab in Management Client), read and written through XProtect's
# Configuration API objects that MilestonePSTools returns: Hardware > HardwareDriverSettingsFolder >
# HardwareDriverSettings > HardwareDriverSettingsChildItems[0].Properties. Values are raw (the stored value, not
# the text Management Client shows). If that path is missing, fall back to MilestonePSTools' own cmdlets.
function Get-DriverSettingsItem($hardware) {
    $settings = @($hardware.HardwareDriverSettingsFolder.HardwareDriverSettings)[0]
    if (-not $settings) { return $null }
    $child = @($settings.HardwareDriverSettingsChildItems)[0]
    if (-not $child -or -not $child.Properties) { return $null }
    return @{ Settings = $settings; Child = $child }
}

function Get-RawSettings($hardware) {
    $item = Get-DriverSettingsItem $hardware
    if ($item) {
        $table = @{}
        foreach ($key in $item.Child.Properties.Keys) { $table[$key] = $item.Child.Properties.GetValue($key) }
        return $table
    }
    return ConvertTo-Hashtable (Get-VmsDeviceGeneralSetting -Device $hardware -RawValues)
}

# Keys look like "hardware/FileName/<GUID>"; the GUID (globally unique identifier) may differ between two hardware
# devices, so settings are matched between devices by the part before it ("hardware/FileName").
function Get-ShortKey([string]$key) {
    return ($key -replace '/[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$', '')
}

function Show-Settings([hashtable]$settings) {
    # Out-String + Write-Host: a table printed inside a script with Write-Host lines around it can otherwise be lost.
    $settings.GetEnumerator() | Sort-Object Name |
        Select-Object @{ n = 'Setting'; e = { Get-ShortKey $_.Name } }, Value, @{ n = 'Full key'; e = { $_.Name } } |
        Format-Table -AutoSize | Out-String -Width 300 | Write-Host
}

function Set-RawSettings($hardware, [hashtable]$changes) {
    $item = Get-DriverSettingsItem $hardware
    if ($item) {
        foreach ($key in $changes.Keys) { $item.Child.Properties.SetValue($key, [string]$changes[$key]) }
        $item.Settings.Save()
        return
    }
    Set-VmsDeviceGeneralSetting -Device $hardware -Settings $changes
}

if ($Step -in 'All', 'Cameras', 'Inspect') {
    Connect-Xprotect
    $reference = Get-VmsHardware | Where-Object Name -eq $ReferenceHardware | Select-Object -First 1
    if (-not $reference) { throw "Reference hardware '$ReferenceHardware' not found (Get-VmsHardware | Select Name, Address)." }
    $refSettings = Get-RawSettings $reference
    # The setting that holds the clip file is the one whose value is the reference camera's clip name.
    # Settings that hold the clip file: every one whose value is the reference camera's clip name. On p043 that is
    # hardware/VideoH264Files (Management Client's "102 VideoH264Files") and hardware/FileName.
    $fileKeys = @($refSettings.Keys | Where-Object { "$($refSettings[$_])" -like "*$ReferenceHardware*" } | ForEach-Object { Get-ShortKey $_ })
    if ($fileKeys.Count -eq 0) { throw "Could not tell which setting holds the clip file. Run -Step Inspect and set it by hand." }
    # Only these are copied from Milestone's camera; everything else stays at the new device's defaults. Never copy
    # identity settings such as MacAddress or SerialNumber.
    $copyKeys = @('hardware/FPS', 'hardware/VideoCodec', 'hardware/Codec')
}

if ($Step -eq 'Inspect') {
    Write-Host "Settings of '$ReferenceHardware' (read only). Clip file setting(s): $($fileKeys -join ', ')" -ForegroundColor Cyan
    Show-Settings $refSettings
    $ours = Get-VmsHardware | Where-Object { $_.Address -match ':(\d+)/?$' -and [int]$Matches[1] -ge 200 }
    foreach ($hw in $ours) {
        Write-Host "Our hardware '$($hw.Name)' at $($hw.Address)" -ForegroundColor Cyan
        Show-Settings (Get-RawSettings $hw)
    }
    return
}

if ($Step -in 'All', 'Cameras') {
    $rs = Get-VmsRecordingServer | Select-Object -First 1
    $allHardware = @(Get-VmsHardware)
    $usedPorts = @($allHardware | ForEach-Object { if ($_.Address -match ':(\d+)/?$') { [int]$Matches[1] } })
    # StableFPS ignores the user name and password; Add-VmsHardware just requires some. Not a secret.
    $dummy = New-Object Management.Automation.PSCredential('stablefps', (ConvertTo-SecureString 'ignored-by-driver' -AsPlainText -Force))

    $group = $null
    try {
        $group = Get-VmsDeviceGroup -Type Camera -Name $CameraGroup -ErrorAction SilentlyContinue
        if (-not $group) { $group = New-VmsDeviceGroup -Type Camera -Name $CameraGroup }
    }
    catch { Write-Warning "Camera group '$CameraGroup' not made ($($_.Exception.Message)). Add the cameras to a view by hand." }

    $port = $FirstPort
    foreach ($clip in $clips) {
        $name = $clip.camera
        $existing = $allHardware | Where-Object Name -eq $name | Select-Object -First 1
        if ($existing) {
            Write-Host "$name already exists at $($existing.Address): skipped." -ForegroundColor Yellow
            continue
        }
        while ($usedPorts -contains $port) { $port++ }

        Write-Host "Adding $name at http://127.0.0.1:$port/ ..." -ForegroundColor Cyan
        $address = "http://127.0.0.1:$port/"
        try {
            # Same driver as Milestone's clip camera, taken from that hardware.
            $hw = Add-VmsHardware -RecordingServer $rs -HardwareAddress $address `
                -HardwareDriverPath $reference.HardwareDriverPath -Name $name -Credential $dummy -Force
        }
        catch {
            Write-Host "  (by driver path failed: $($_.Exception.Message); trying by driver number)"
            $driver = Get-VmsHardwareDriver -RecordingServer $rs | Where-Object Name -like '*StableFPS*' | Select-Object -First 1
            if (-not $driver) { throw 'StableFPS driver not found. Add the hardware by hand (xprotect-setup\README.md).' }
            $hw = Add-VmsHardware -RecordingServer $rs -HardwareAddress $address `
                -DriverNumber $driver.Number -Name $name -Credential $dummy -Force
        }
        $usedPorts += $port

        # Copy only the settings where Milestone's clip camera differs from a fresh device (fps, codec, file),
        # with the file setting pointing at our clip.
        $fresh = Get-RawSettings $hw
        $freshKeys = @{}   # short key -> this device's full key
        foreach ($key in $fresh.Keys) { $freshKeys[(Get-ShortKey $key)] = $key }
        $changes = @{}
        foreach ($key in $refSettings.Keys) {
            $short = Get-ShortKey $key
            if (-not $freshKeys.ContainsKey($short)) { continue }
            $target = $freshKeys[$short]
            if ($fileKeys -contains $short) { $changes[$target] = [IO.Path]::GetFileName($clip.file) }
            elseif ($copyKeys -contains $short -and "$($fresh[$target])" -ne "$($refSettings[$key])") { $changes[$target] = $refSettings[$key] }
        }
        foreach ($short in $fileKeys) {
            if (-not $freshKeys.ContainsKey($short)) { Write-Warning "  The new device has no '$short' setting: set the clip file by hand." }
        }
        try {
            Set-RawSettings $hw $changes
            Write-Host ('  settings: ' + (($changes.GetEnumerator() | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ', '))
        }
        catch {
            Write-Warning "  Settings not applied ($($_.Exception.Message)). Set them by hand: Management Client, Recording Servers, $name, Settings: Frames per second 10, VideoCodec H264, VideoH264Files $($clip.file)."
        }

        # Configuration API objects: the hardware's first camera channel, renamed and enabled.
        $camera = @($hw.CameraFolder.Cameras) | Select-Object -First 1
        if ($camera) {
            $camera.Name = $name
            $camera.Enabled = $true
            $camera.Save()
            if ($group) {
                try { Add-VmsDeviceGroupMember -Group $group -Device $camera } catch { Write-Warning "  Not added to group: $($_.Exception.Message)" }
            }
            Write-Host "  camera '$name' ready." -ForegroundColor Green
        }
        $port++
    }
    Clear-VmsCache
    Write-Host ''
    Write-Host 'Cameras done. Next: xprotect-setup\Setup-ScenarioLab.ps1 (alarm, Hafnia prompt and rule, demo suggestions).' -ForegroundColor Green
    Write-Host 'They record on motion like the others; give them a minute before testing with Hafnia.'
}
