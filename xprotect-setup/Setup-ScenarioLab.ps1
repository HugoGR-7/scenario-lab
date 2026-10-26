<#
Setup-ScenarioLab.ps1 - everything Scenario Lab needs in XProtect and the Hafnia gateway, except the clip cameras
(Install-ScenarioClips.ps1) and the plug-in itself (scripts\Build-Plugin.ps1).

Run on the XProtect machine in PowerShell as Administrator, from the repository folder:
    powershell -NoProfile -ExecutionPolicy Bypass -File .\xprotect-setup\Setup-ScenarioLab.ps1
    ... -Step Folders         C:\ScenarioLab folders + the demo's scenario suggestions
    ... -Step AnalyticsEvent  the analytics event "Scenario Lab detection"
    ... -Step Alarm           the alarm definition "Scenario Lab: scenario detected" and its sources
    ... -Step Hafnia          the gateway's saved prompt "Scenario Lab" and gateway rule "Scenario Lab" (asks for the
                              gateway password = the Windows Administrator password; it is never stored)

Every step checks by name first: run it as often as you like, it only creates what is missing. The names are the ones
the plug-in uses (hafnia/README.md).

One thing has no command and must be done by hand first: Management Client > Tools > Options > Analytics Events tab:
Enable, port 9090, All network addresses, OK.
#>
param(
    [ValidateSet('All', 'Folders', 'AnalyticsEvent', 'Alarm', 'Hafnia')][string]$Step = 'All',
    # AllCameras: every camera can raise the alarm, so clips added later need nothing more.
    # ScenarioLab: only the snapshot camera (vandalism) and the clip cameras listed in demo\stablefps\clips.json.
    [ValidateSet('AllCameras', 'ScenarioLab')][string]$AlarmSources = 'AllCameras'
)
$ErrorActionPreference = 'Stop'

$repo = Split-Path $PSScriptRoot -Parent
$dataFolder = 'C:\ScenarioLab'
$analyticsEventName = 'Scenario Lab detection'
$alarmName = 'Scenario Lab: scenario detected'
$promptTitle = 'Scenario Lab'
$ruleName = 'Scenario Lab'
$triggerText = 'SCENARIO_DETECTED'
$snapshotCamera = 'vandalism'
$allCamerasPath = 'CameraGroup[0e1b0ad3-f67c-4d5f-b792-4bd6c3cf52f8]'   # "All Cameras": the same on every XProtect
# Used only when someone runs the saved prompt by hand; the plug-in sends its own question (hafnia/README.md).
$promptText = "Is anyone climbing over a fence or wall in this video?`nFirst describe in one or two sentences what happens in the clip. Then, on the last line, write exactly one word: SCENARIO_DETECTED if this happens in the clip, SCENARIO_NOT_DETECTED if it does not."

function Say($text, $color = 'Gray') { Write-Host $text -ForegroundColor $color }
function Manual($text) { Write-Host "  BY HAND: $text" -ForegroundColor Yellow }

# ------------------------------------------------------------------------------------------------ Folders
if ($Step -in 'All', 'Folders') {
    Say 'Folders and demo suggestions' Cyan
    foreach ($sub in 'snapshots', 'suggestions') { New-Item -ItemType Directory -Force -Path (Join-Path $dataFolder $sub) | Out-Null }
    foreach ($file in Get-ChildItem (Join-Path $repo 'demo\suggestions') -Filter '*_scenarios.json') {
        Copy-Item $file.FullName (Join-Path $dataFolder "suggestions\$($file.Name)") -Force
        Say "  copied $($file.Name) -> $dataFolder\suggestions"
    }
}

# ------------------------------------------------------------------------------------------------ Analytics event
if ($Step -in 'All', 'AnalyticsEvent') {
    Say "Analytics event '$analyticsEventName'" Cyan
    try {
        # A token as the current Windows user (as in the kit's tour script), then the Configuration REST API.
        $token = Invoke-RestMethod -Method Post -Uri 'http://localhost/IDP/connect/token' -UseDefaultCredentials `
            -Body @{ grant_type = 'windows_credentials'; client_id = 'GrantValidatorClient' }
        $headers = @{ Authorization = 'Bearer ' + $token.access_token }
        $existing = @((Invoke-RestMethod -Uri 'http://localhost/api/rest/v1/analyticsEvents' -Headers $headers).array)
        if ($existing | Where-Object { $_.name -eq $analyticsEventName -or $_.displayName -eq $analyticsEventName }) {
            Say "  present: $analyticsEventName"
        }
        else {
            $body = @{ name = $analyticsEventName; description = 'Raised by the Hafnia gateway rule "Scenario Lab" when Hafnia VLM answers SCENARIO_DETECTED' } | ConvertTo-Json
            Invoke-RestMethod -Method Post -Uri 'http://localhost/api/rest/v1/analyticsEvents' -Headers $headers `
                -ContentType 'application/json' -Body $body | Out-Null
            Say "  created: $analyticsEventName" Green
        }
    }
    catch {
        Write-Warning "  Could not check or create it through the REST API: $($_.Exception.Message)"
        Manual "Management Client > Rules and Events > Analytics Events > right-click > Add New > name '$analyticsEventName' > Save."
    }
    Say '  Analytics events must be switched on (no command for it):'
    Manual 'Management Client > Tools > Options > Analytics Events: Enable, port 9090, All network addresses, OK.'
}

# ------------------------------------------------------------------------------------------------ Alarm definition
if ($Step -in 'All', 'Alarm') {
    Say "Alarm definition '$alarmName'" Cyan
    Import-Module MilestonePSTools
    Connect-Vms -ServerAddress 'http://localhost' -AcceptEula | Out-Null
    Clear-VmsCache

    if ($AlarmSources -eq 'AllCameras') {
        $sourcePaths = @($allCamerasPath)
        $sourceLabel = 'All Cameras'
    }
    else {
        $manifest = Join-Path $repo 'demo\stablefps\clips.json'
        $names = @($snapshotCamera) + @((Get-Content $manifest -Raw | ConvertFrom-Json) | ForEach-Object { $_ } | ForEach-Object { $_.camera })
        $cameras = @(Get-VmsCamera | Where-Object { $names -contains $_.Name })
        $missing = @($names | Where-Object { $cameras.Name -notcontains $_ })
        if ($missing.Count) { Write-Warning "  Cameras not found (install the clips first): $($missing -join ', ')" }
        $sourcePaths = @($cameras | ForEach-Object { $_.Path })
        $sourceLabel = ($cameras.Name -join ', ')
    }

    $def = Get-VmsAlarmDefinition | Where-Object Name -eq $alarmName | Select-Object -First 1
    if (-not $def) {
        try {
            # The triggering event: group "Analytics Events", event = our analytics event (names as Management
            # Client shows them; see the kit's scripting.md, "Alarm definition").
            New-VmsAlarmDefinition -Name $alarmName -EventTypeGroup 'Analytics Events' -EventType $analyticsEventName `
                -Source $(if ($AlarmSources -eq 'AllCameras') { 'AllCameras' } else { $sourcePaths }) -Description 'Hafnia VLM confirmed the tested scenario (Scenario Lab).' | Out-Null
            Say "  created: $alarmName, sources: $sourceLabel" Green
        }
        catch {
            Write-Warning "  Not created: $($_.Exception.Message)"
            Manual "Management Client > Alarms > Alarm Definitions > right-click > Add New: name '$alarmName', Triggering event 'Analytics Events' > '$analyticsEventName', Sources: $sourceLabel. Save."
        }
    }
    else {
        $group = $def.EventTypeGroupValues.Keys | Where-Object { $def.EventTypeGroupValues[$_] -eq $def.EventTypeGroup }
        $type = $def.EventTypeValues.Keys | Where-Object { $def.EventTypeValues[$_] -eq $def.EventType }
        Say "  present: $alarmName (triggering event: $group > $type)"
        $current = @("$($def.SourceList)".Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        $wanted = @($sourcePaths | Sort-Object)
        if ((@($current | Sort-Object) -join ',') -eq ($wanted -join ',')) {
            Say "  sources already: $sourceLabel"
        }
        else {
            Say "  sources now: $($current -join ', ')"
            try {
                $def.SourceList = ($sourcePaths -join ',')
                $def.Save()
                Say "  sources set to: $sourceLabel" Green
            }
            catch {
                Write-Warning "  Sources not changed: $($_.Exception.Message)"
                Manual "Management Client > Alarms > Alarm Definitions > '$alarmName' > Sources: $sourceLabel. Save."
            }
        }
    }
}

# ------------------------------------------------------------------------------------------------ Hafnia gateway
if ($Step -in 'All', 'Hafnia') {
    Say "Hafnia gateway: saved prompt '$promptTitle' and rule '$ruleName'" Cyan
    # Windows PowerShell 5.1 cannot connect to the gateway with Invoke-RestMethod (kit, gateway-api.md): use curl.exe.
    # JSON goes in through stdin, so quotes survive and the password never appears on a command line.
    $OutputEncoding = New-Object Text.UTF8Encoding $false
    $script:session = $null

    function Invoke-Gateway([string]$Method, [string]$Path, $Body = $null) {
        $curlArgs = @('-sk', '--max-time', '60', '-X', $Method, '-H', 'Content-Type: application/json', '-w', "`n%{http_code}")
        if ($script:session) { $curlArgs += @('-H', "X-Session-Id: $($script:session)") }
        $url = "https://localhost:15443$Path"
        if ($null -ne $Body) {
            $json = if ($Body -is [string]) { $Body } else { $Body | ConvertTo-Json -Depth 6 -Compress }
            $out = @($json | & curl.exe @curlArgs --data-binary '@-' $url)
        }
        else {
            $out = @(& curl.exe @curlArgs $url)
        }
        if ($LASTEXITCODE -ne 0 -or $out.Count -eq 0) { throw "$Method $Path`: no answer from the gateway (curl exit code $LASTEXITCODE)" }
        $code = [int]$out[-1]
        $text = if ($out.Count -gt 1) { ($out[0..($out.Count - 2)] -join "`n") } else { '' }
        if ($code -ge 300) { throw "$Method $Path`: HTTP $code $text" }
        if ($text.Trim()) { return ($text | ConvertFrom-Json) }
    }

    $secure = Read-Host -Prompt 'Gateway password (the Windows Administrator password)' -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try {
        $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
        $login = @{ Name = 'administrator'; Password = $plain } | ConvertTo-Json -Compress
        $plain = $null
        $script:session = (Invoke-Gateway POST '/api/Sessions/Login' $login).Id
        $login = $null
    }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
    Say '  logged in'

    try {
        $found = Invoke-Gateway GET ('/api/Prompts?SearchString=' + [Uri]::EscapeDataString($promptTitle))
        $prompt = @($found.PageContent) | Where-Object Title -eq $promptTitle | Select-Object -First 1
        if ($prompt) { Say "  present: prompt '$promptTitle'" }
        else {
            $prompt = Invoke-Gateway POST '/api/Prompts' @{ Title = $promptTitle; Text = $promptText }
            Say "  created: prompt '$promptTitle'" Green
        }

        $rulesAnswer = Invoke-Gateway GET '/api/Rules'
        # A plain JSON list comes back from ConvertFrom-Json (PowerShell 5.1) as one object: unroll it.
        $rules = @($rulesAnswer | ForEach-Object { $_ })
        if ($rules.Count -eq 1 -and $rules[0].PSObject.Properties['PageContent']) { $rules = @($rules[0].PageContent) }
        $rule = $rules | Where-Object Name -eq $ruleName | Select-Object -First 1
        if ($rule) {
            Say "  present: rule '$ruleName'"
            if ($rule.PromptId -ne $prompt.Id) { Write-Warning "  The rule points at another prompt ID than '$promptTitle': delete it on the gateway page and run this step again." }
        }
        else {
            $options = Invoke-Gateway GET '/api/Rules/Actions/TriggerAnalyticsEvent/Options'
            if (($options | ConvertTo-Json -Depth 6) -notmatch [regex]::Escape($analyticsEventName)) {
                Write-Warning "  The gateway does not list the analytics event '$analyticsEventName' yet (step AnalyticsEvent, and switch analytics events on). Creating the rule anyway."
            }
            Invoke-Gateway POST '/api/Rules' @{
                Name        = $ruleName
                Description = 'Raise an XProtect alarm when Hafnia confirms the tested scenario'
                PromptId    = $prompt.Id
                Triggers    = @(@{ TriggerText = $triggerText; RuleActionId = 'TriggerAnalyticsEvent'; RuleActionData = $analyticsEventName })
            } | Out-Null
            Say "  created: rule '$ruleName' ($triggerText -> '$analyticsEventName')" Green
        }
    }
    finally {
        try { Invoke-Gateway POST '/api/Sessions/Logout' @{ Id = $script:session } | Out-Null } catch { }
    }
}

Say ''
Say 'Done. Next: build the plug-in (scripts\Build-Plugin.ps1), then follow "Run the demo" in README.md.' Green
