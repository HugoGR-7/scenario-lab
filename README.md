# Scenario Lab

Hafnia Hackathon 2026 project (Milestone Systems × NVIDIA).

A portal for customers who already run Milestone XProtect cameras: see how the system reacts, **on their own
cameras**, to rare situations specific to their domain, before those situations happen.

1. List cameras from XProtect and take a snapshot through XProtect.
2. An LLM (large language model) proposes domain-specific edge-case scenarios for each view; the user can add their own.
3. A video generation model renders each scenario on that exact view.
4. The clips play in XProtect as a camera, Hafnia VLM (vision-language model) analyses them.
5. Results come back into XProtect as events and alarms in Smart Client.

## Repository layout

| Folder | Contents |
|---|---|
| `plugin/` | C# MIP (Milestone Integration Platform) Smart Client plug-in: the "Scenario Lab" workspace tab |
| `xprotect-setup/` | MilestonePSTools scripts: load generated clips as StableFPS cameras (`Install-ScenarioClips.ps1`); later events, rules and alarms |
| `hafnia/` | Exported Hafnia prompts, the system prompt text, gateway rule definitions |
| `scripts/` | Build, deploy and environment-check scripts; `convert_clips.py` (MP4 → StableFPS clip) |
| `docs/` | Architecture notes and setup guide |

## Build requirements (Windows, on the XProtect machine)

- Git for Windows
- Build Tools for Visual Studio 2022, workload ".NET desktop build tools", with the .NET Framework 4.8 targeting pack
- Target: .NET Framework 4.8; NuGet package `MilestoneSystems.VideoOS.Platform` 26.1.* (matches XProtect 2026 R1)

No passwords, tokens or API keys are ever committed to this repository.
