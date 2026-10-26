# Scenario Lab plug-in (Smart Client)

A MIP (Milestone Integration Platform) plug-in: a C# DLL (dynamic-link library) for .NET Framework 4.8 that
XProtect Smart Client loads at start-up. It adds a **Scenario Lab** tab.

## Build and deploy (on the XProtect machine)

PowerShell as Administrator, Smart Client closed:

```powershell
cd C:\src\scenario-lab
git pull
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build-Plugin.ps1
```

Then start Smart Client, log in to `localhost` and open the **Scenario Lab** tab.
Remove the plug-in: close Smart Client and delete `C:\Program Files\Milestone\MIPPlugins\ScenarioLab`.

## How the pieces fit

Smart Client loads `plugin.def`, which names `ScenarioLab.dll` and says "load in Smart Client only". In the DLL:

| File | Role | Python analogy |
|---|---|---|
| `ScenarioLabDefinition.cs` | Entry point (`PluginDefinition`): lists the parts the plug-in adds | the package's `__init__` registering plugins |
| `Client/ScenarioLabWorkSpacePlugin.cs` | The tab (`WorkSpacePlugin`): one view with one full-size rectangle | a page with a single container |
| `Client/ScenarioLabViewItemPlugin.cs` | Describes our panel type (`ViewItemPlugin`): ID, name, icon, factory | a class registered in a registry |
| `Client/ScenarioLabViewItemManager.cs` | One per shown panel: its saved properties; creates the UI control | a factory |
| `Client/ScenarioLabViewItemWpfUserControl.xaml` | The panel's layout (WPF, Windows Presentation Foundation) | an HTML template |
| `Client/ScenarioLabViewItemWpfUserControl.xaml.cs` | The panel's code: both pages (cameras, snapshot, scenarios, clips, Hafnia test) | the view's event handlers |
| `Client/RecordedFrameFetcher.cs` | Reads recorded frames from XProtect on a background thread | a worker thread with a "latest request wins" queue |
| `Client/Scenario.cs` | One scenario: a checklist row on page 1, a test row on page 2 | a dataclass with change notifications |
| `Client/ScenarioFiles.cs` | Reads the suggestions JSON, writes and reads the user's selection JSON | a small `json` module wrapper |
| `Hafnia/HafniaGatewayClient.cs` | Calls the Hafnia gateway REST API (login, allowance, prompt lookup, ask, read answer) | a small `requests` wrapper |

Life cycle: Smart Client calls `Init()` after login (or when the panel appears) and `Close()` at logout (or when
it disappears). Everything acquired in `Init()` is released in `Close()`.

## What this build does

The tab has two pages, switched with the buttons at the top.

**Page 1: Snapshot & scenarios**

1. **Cameras:** every camera in XProtect's configuration (`Configuration.Instance.GetItemsByKind(Kind.Camera)`).
2. **Live view and frame picker:** the selected camera live (`ImageViewerWpfControl`); a slider (last 2 minutes to 24
   hours) and frame-step buttons pick a recorded frame (`BitmapVideoSource`, original resolution). **Save this frame
   as snapshot** writes a PNG to `C:\ScenarioLab\snapshots`. The image comes through XProtect, as the hackathon requires.
3. **Scenarios:** **Load suggestions** reads `C:\ScenarioLab\suggestions\<camera>_scenarios.json`, the edge cases the
   scene-analysis model (a vision-language model in ComfyUI) proposed from that snapshot:
   `[{"title": ..., "category": ..., "where": ...}, ...]`. They appear as a checklist; *harmless look-alike* ones are
   marked "should NOT trigger". The user adds their own scenarios and **Save selection** writes the ticked ones to
   `<camera>_scenarios_user.json` (with `id`, `source`, `expected`): the input for video generation.

**Page 2: Generated clips & Hafnia test**

4. **Generated clips in XProtect:** each saved scenario next to the XProtect camera that plays its clip, found by name
   (`<id>` or `<id>_anything`, see `xprotect-setup/README.md`), plus an **original camera** row: the snapshot camera's
   real recording from the snapshot moment (Expected defaults to *detect*, because the demo camera `vandalism` loops a
   real stone-throwing incident; set it to what really happens on your camera). Clicking a row shows that camera live. *Expected* can be changed per row.
5. **Test with Hafnia:** connect with the gateway password (memory only), write one detection question, then **Run
   selected** or **Run all**. One request per clip: the question plus a fixed keyword instruction, the saved prompt
   *Scenario Lab*'s ID, and 10/20/30 seconds of recording (a generated clip loops, so its latest seconds; the original
   camera from the snapshot moment). Each row shows got vs. expected (PASS / FAIL) and the answer. A "detected" answer also
   raises the XProtect alarm *Scenario Lab: scenario detected* through the gateway rule. Every test is appended to
   `C:\ScenarioLab\runs.jsonl`. Setup and names: `hafnia/README.md`.

**Hafnia requests:** only from the Run buttons, one per clip, never on a timer, and refused once 450 of the 500 daily
requests are used.

## Where the code comes from

Every XProtect call follows a Milestone sample in
[mipsdk-samples-plugin](https://github.com/milestonesys/mipsdk-samples-plugin) and the
[MIP reference](https://doc.developer.milestonesys.com/mipsdk/MIPHelp/index.html):

- tab, layout, view item, camera tree walk: *Smart Client Workspace* (`SCWorkSpace`);
- `ImageViewerWpfControl`: *Video Preview* (`VideoPreview`);
- `BitmapVideoSource` (recorded frames, one dedicated thread): *Video Replay* (`VideoReplay`) and
  *RGB Video Enhancement* (`RGBVideoEnhancement`).

## Troubleshooting

- **No tab:** Smart Client, *Settings and more*, *About* lists the loaded plug-ins. If ours is missing, check that
  `C:\Program Files\Milestone\MIPPlugins\ScenarioLab` holds `ScenarioLab.dll` and `plugin.def`.
- **Errors:** the status line at the bottom of the panel shows them; full details go to the MIP log. Switch logging
  on in Smart Client (*Settings and more*, *Settings*, *Advanced*, *Logging*), then read `MIPLogger.log` in
  `C:\ProgramData\Milestone\XProtect Smart Client\Logs` and search for `ScenarioLab`.
- **Smart Client fails to start after deploying:** delete the `ScenarioLab` folder in `MIPPlugins` and start again.
