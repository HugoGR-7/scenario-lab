# After the hackathon: an agentic redesign

**Status: design, not implemented.** The plug-in in this repository is the version presented at the Hafnia
Hackathon 2026, where a person drives every step. This page is the follow-up: what changed after presenting it, and
how the same loop would be run by an AI agent across all the cameras of a site.

## Where this comes from

Scenario Lab was presented in Copenhagen as one of the 8 finalists, at the Milestone Developer Summit. A theme that
kept coming up in the feedback and in the conversations with people from Milestone, NVIDIA and the partner ecosystem
was **agentic AI**: systems that do not just answer one request but carry out a whole task on their own, calling tools
along the way.

Looking at Scenario Lab from that angle, its weak point is clear. It works well for one camera, but a real site has
tens or hundreds of cameras, each with its own view and its own edge cases. Doing page ① and page ② by hand for every
camera is the slow part, and in practice it often does not get done: detection questions go live without ever being
tested on the site's own cameras.

The good news is that the hackathon version was already built as separate steps that read and write plain files (the
snapshot, `<camera>_scenarios.json`, the clips, `runs.jsonl`). Nothing in the loop needs a person except two
decisions: what should count as an alarm, and whether the result is good enough to go live. So the repetitive part
can be handed to an agent.

## What changes from the hackathon version

| | Hackathon version | Agentic version |
|---|---|---|
| Scope | One camera at a time | Every camera of a site, in one run |
| Choosing the snapshot | The user scrubs the recordings with a slider | The agent picks frames at different times and asks the VLM (vision-language model) for the emptiest one |
| What should trigger | The user ticks *Should trigger* / *Should NOT trigger* per scenario | The agent labels each scenario from a **site policy** written once; unclear cases go to the person |
| Bad generated clips | Not detected: a broken clip shows up as a Hafnia FAIL | A local VLM checks every clip before it is tested, and regenerates it if needed |
| Tuning the question | The user edits the question and re-runs failing rows by hand | The agent rewrites the question from Hafnia's answers and re-runs only the failing rows, within a budget |
| Output | A PASS/FAIL table in the tab | A report per camera, with a proposed final question for the person to approve |
| Who runs it | The plug-in, inside Smart Client | A Python runner on the local GPU server; the plug-in becomes the review and approval screen |

## The operator's role

**What the operator still does**

1. **Writes the site policy once**, in plain language: what counts as an alarm on this site and what does not. For
   example: *"Anyone climbing the fence is an alarm. People in the loading bay after 22:00 are an alarm, except staff in
   high-visibility vests. Cleaning and deliveries are never an alarm."*
2. **Answers the cases the policy does not cover**, which the agent flags instead of guessing.
3. **Approves before anything changes on the system:** before the clip cameras are installed (it restarts the
   Recording Server), and before the tuned question and gateway rule go live on a real camera.

**What the operator no longer does**

- Pick a frame for every camera.
- Read and tick 8 suggested scenarios for every camera.
- Watch every generated clip to see whether the video model got it right.
- Rewrite the question and re-run rows by trial and error.
- Keep track of which questions were tried and why they failed: the report does it.

The agent never changes the live configuration on its own.

## The loop, per camera

```
 site policy ──────────────┐
                           ▼
 snapshot ─▶ propose ─▶ label expected ─▶ generate ─▶ check clip ─▶ install as
             scenarios   (from policy)     clips      (local VLM)    cameras
                                                                        │
                                                                        ▼
 report ◀── stop? ◀── rewrite the question ◀── test with Hafnia ◀───────┘
   │                  (failing rows only)
   ▼
 person approves ─▶ question + gateway rule go live on the real camera
```

1. **Snapshot.** The agent fetches a few frames from the recordings at different times (day and night if available)
   and asks the VLM which one has the fewest people and vehicles.
2. **Propose scenarios.** Stage A (`comfyui/stage_a_scenarios_from_snapshot.json`), unchanged. Output:
   `<camera>_scenarios.json`, same format as today.
3. **Label expected.** An LLM (large language model) reads the site policy and each scenario and writes *detect* or
   *no detect*, with a one-line reason. Scenarios the policy does not clearly cover are **flagged for the person**,
   not guessed. Output: `<camera>_scenarios_user.json`, same format the plug-in writes today, with `source: agent`.
4. **Generate clips.** Stage B (`comfyui/stage_b_video_from_scenario.json`) for each scenario, then
   `scripts/convert_clips.py`.
5. **Check the clip.** A video model can fail to show what it was asked for (the rock never hits the window, the
   person vanishes). Then a FAIL would be the generator's fault, not Hafnia's. Before spending a Hafnia request, a local
   VLM (for example Qwen3-VL, the same model as Stage A) answers *"Does this clip show: <scenario>?"*. If not, the clip
   is regenerated with a new seed, up to two times, and otherwise dropped and listed in the report.
6. **Install as cameras.** `xprotect-setup/Install-ScenarioClips.ps1`. This restarts the Recording Server and pauses
   recording for about 4 minutes, so the agent **batches the clips of all cameras into one install** and only runs it
   after the person approves, ideally in a maintenance window. A dedicated test Recording Server would remove this
   problem entirely.
7. **Test with Hafnia.** One `POST /api/Summaries` per clip, exactly like *Run all* on page ②, plus the row with the
   camera's real recording.
8. **Rewrite the question.** If rows fail, an LLM gets the question, the expected results and Hafnia's answers for the
   failing rows, and proposes a new question. Only the failing rows are run again. When everything passes, all rows
   are run once more with the final question, to check that nothing that passed before now fails.
9. **Stop** when all rows pass, after 3 rewrite rounds, or when the camera's request budget is used up. Rows that still
   fail go in the report: that is useful too. It may mean Hafnia cannot see that event from this camera angle, which
   is better to know before going live than after.

The **report** per camera has the chosen snapshot, the scenarios with their expected result and reason, the clips,
every question tried with its PASS/FAIL table, and the proposed final question. The person reviews it in the
Scenario Lab tab and approves or edits it.

## How each part would be implemented

| Tool | What it does | Exists today? |
|---|---|---|
| `get_snapshot(camera, times)` | Frames from the recordings | In the plug-in (MIP SDK, Milestone Integration Platform). Needs a version that runs without Smart Client |
| `propose_scenarios(snapshot)` | Runs Stage A | Yes, as a ComfyUI workflow. Export it in API format and call it through ComfyUI's HTTP API |
| `label_expected(scenarios, policy)` | Expected result per scenario, or a flag | New |
| `generate_clip(snapshot, scenario, seed)` | Runs Stage B and converts the clip | Yes (Stage B + `convert_clips.py`) |
| `check_clip(clip, scenario)` | Local VLM: does the clip show the scenario? | New |
| `install_clips(folder)` | Loads clips as cameras | Yes (`Install-ScenarioClips.ps1`), behind an approval step |
| `run_test(camera, window, question)` | One Hafnia request | Yes (`HafniaGatewayClient.cs`); the same REST call works from Python |
| `write_report(camera)` | Builds the report from `runs.jsonl` and the JSON files | New |

The agent itself is a small Python runner around these tools and an LLM with tool calling, run on the same local GPU
server as the ComfyUI workflows. The plug-in stays as the place where the person reads reports and approves.

## Guardrails

- **Hafnia budget.** The gateway allows 500 requests a day and the plug-in refuses past 450. With 8 scenarios plus the
  real row (9 rows), the worst case per camera is 9 (first run) + 3 × 9 (rewrites) + 9 (final check) = **45
  requests**, so about 10 cameras a day under the 450 cap. Usually far fewer: rewrites only re-run failing rows, and
  step 5 means no request is spent on a broken clip. The agent gets a fixed budget per camera and per day and stops when
  it runs out.
- **No loops from alarms.** Same rule as today (`hafnia/README.md`): nothing uses the analytics event
  *Scenario Lab detection* to start a Hafnia request. The agent only sends requests from its own loop, one per clip.
- **Not grading its own homework.** A question tuned only on generated clips can end up fitting those clips and not
  reality. So: the camera's real recording is always one of the rows; one or two scenarios per camera are held back
  from the rewrite loop and only tested at the end; and the model that proposes scenarios (Qwen3-VL) is not the model
  being tested (Hafnia VLM).
- **Footage stays on site.** Snapshots, generation, clip checks and the agent's LLM all run on the local GPU server.
  The agent adds no new place where video is sent.
- **A person approves** the install and the final question, and can always see why each scenario was labelled as it was.

## Order to build it in

1. Export Stage A and Stage B in ComfyUI's API format and drive them from Python (no XProtect needed).
2. Add `label_expected` and `check_clip`, and run steps 1–5 on saved snapshots. This already produces a reviewed set of
   test clips per camera without any manual ticking.
3. Add the Hafnia test and the rewrite loop, with the budget guard, using the same REST calls as the plug-in.
4. Add the report and the approval step to the Scenario Lab tab.
