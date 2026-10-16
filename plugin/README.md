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
| `Client/ScenarioLabViewItemWpfUserControl.xaml.cs` | The panel's code: cameras, live view, frame picker, snapshot, clips | the view's event handlers |
| `Client/RecordedFrameFetcher.cs` | Reads recorded frames from XProtect on a background thread | a worker thread with a "latest request wins" queue |

Life cycle: Smart Client calls `Init()` after login (or when the panel appears) and `Close()` at logout (or when
it disappears). Everything acquired in `Init()` is released in `Close()`.

## What this build does

1. **Cameras:** lists every camera in XProtect's configuration (`Configuration.Instance.GetItemsByKind(Kind.Camera)`).
2. **Live view and frame picker:** shows the selected camera live with XProtect's own video control
   (`ImageViewerWpfControl`). Below it, a slider (last 2 minutes to 24 hours) and frame-step buttons pick a frame
   from XProtect's recordings (`BitmapVideoSource`, at the camera's original resolution). **Save this frame as
   snapshot** writes it as a PNG to `C:\ScenarioLab\snapshots`, named after the camera and the frame's own time.
   The image comes through XProtect, as the hackathon requires.
3. **Scenario:** saves the text as `<snapshot>.scenario.txt` next to the snapshot: the hand-off to video generation.
4. **Generated clips:** lists and plays video files placed in `C:\ScenarioLab\clips` (until clips can be loaded
   into XProtect as recordings).
5. **Hafnia results:** placeholder.

**It sends no Hafnia requests and raises no XProtect events.**

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
