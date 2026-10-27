<#
List-UiControls.ps1 - READ ONLY. Lists Milestone's design-system WPF controls (namespace VideoOS.Platform.UI.Controls)
from the VideoOS.Platform.dll that Smart Client itself uses, so the plug-in can use the exact class names and members.
Milestone's online reference truncates that list, which is why this exists.

    powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\List-UiControls.ps1 > C:\ScenarioLab\ui-controls.txt
#>
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Xaml

$candidates = @(
    'C:\Program Files\Milestone\XProtect Smart Client\VideoOS.Platform.dll'
) + @(Get-ChildItem "$env:USERPROFILE\.nuget\packages\milestonesystems.videoos.platform" -Recurse -Filter 'VideoOS.Platform.dll' -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | ForEach-Object { $_.FullName })
$dll = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $dll) { throw 'VideoOS.Platform.dll not found.' }
"DLL: $dll  version $((Get-Item $dll).VersionInfo.FileVersion)"

$asm = [Reflection.Assembly]::LoadFrom($dll)
try { $types = $asm.GetTypes() } catch [Reflection.ReflectionTypeLoadException] { $types = $_.Exception.Types | Where-Object { $_ } }

function Chain($t) {
    $names = @(); $b = $t.BaseType
    while ($b -and $b.FullName -ne 'System.Object') {
        $names += $(if ($b.Namespace -like 'VideoOS*') { $b.Name } else { $b.FullName })
        if ($b.Namespace -like 'System.Windows*') { break }
        $b = $b.BaseType
    }
    $names -join ' > '
}

foreach ($ns in 'VideoOS.Platform.UI.Controls', 'VideoOS.Platform.UI.Controls.Tables') {
    "`n================ $ns ================"
    foreach ($t in ($types | Where-Object { $_.IsPublic -and $_.Namespace -eq $ns } | Sort-Object Name)) {
        if ($t.IsEnum) { "ENUM  $($t.Name): $([Enum]::GetNames($t) -join ', ')"; continue }
        $kind = if ($t.IsAbstract) { 'abstract' } else { 'class' }
        "$kind  $($t.Name)  :  $(Chain $t)"
        # Members added by Milestone (declared on VideoOS.* types in the chain), not WPF's own.
        $own = $t.GetProperties([Reflection.BindingFlags]'Public,Instance') | Where-Object { $_.DeclaringType.Namespace -like 'VideoOS*' }
        if ($own) { '      props: ' + (($own | ForEach-Object { "$($_.Name):$($_.PropertyType.Name)" } | Sort-Object -Unique) -join ', ') }
        $ev = $t.GetEvents() | Where-Object { $_.DeclaringType.Namespace -like 'VideoOS*' }
        if ($ev) { '      events: ' + (($ev | ForEach-Object Name | Sort-Object -Unique) -join ', ') }
    }
}

"`n================ VideoOS.Platform.Themes (public types) ================"
$types | Where-Object { $_.IsPublic -and $_.Namespace -like 'VideoOS.Platform.Themes*' } | Sort-Object FullName | ForEach-Object {
    $static = $_.GetProperties([Reflection.BindingFlags]'Public,Static') | ForEach-Object Name
    $fields = $_.GetFields([Reflection.BindingFlags]'Public,Static') | ForEach-Object Name
    "$($_.FullName)$(if ($static -or $fields) { '  static: ' + ((@($static) + @($fields)) -join ', ') })"
}

"`n================ Theme resource dictionaries in the DLL ================"
$res = $asm.GetManifestResourceNames()
$res
foreach ($r in $res | Where-Object { $_ -like '*.g.resources' }) {
    $stream = $asm.GetManifestResourceStream($r)
    $reader = New-Object Resources.ResourceReader $stream
    foreach ($entry in $reader) { if ($entry.Key -match 'theme|style|control|brush|color' ) { "  $($entry.Key)" } }
    $reader.Close()
}
