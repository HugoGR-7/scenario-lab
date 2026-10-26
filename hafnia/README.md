# Hafnia setup for Scenario Lab

How a Scenario Lab test turns into an XProtect alarm, and the exact names the plug-in and the setup rely on.
Keep these names in sync with `plugin/` and `xprotect-setup/`.

## The chain

1. The user writes a detection question in the Scenario Lab panel, e.g. *"Is anyone climbing over the fence?"*
2. The plug-in sends `POST /api/Summaries` to the Hafnia gateway with:
   - `Prompt` = the user's question + the **keyword instruction** below;
   - `PromptId` = the ID of the saved prompt **Scenario Lab** (looked up by title), so the gateway rule checks
     the answer. The gateway does not look the text up: it uses the text we send.
   - `VideoUris` = one clip from one camera.
3. Hafnia VLM answers with a short description and, on the last line, `SCENARIO_DETECTED` or
   `SCENARIO_NOT_DETECTED`.
4. The gateway rule **Scenario Lab** sees `SCENARIO_DETECTED` and raises the XProtect analytics event
   **Scenario Lab detection** on the clip's camera.
5. The alarm definition **Scenario Lab: scenario detected** shows it in Smart Client's Alarm Manager.

`SCENARIO_NOT_DETECTED` never fires the rule: the gateway matches whole words, and `_` is part of a word.

**Safety:** nothing may use the analytics event *Scenario Lab detection* to start a Hafnia request (that loops until
the 500 daily requests are gone). Each test is one button press, one request per clip.

## Verified on p043 (17 Oct 2026)

One request through the gateway API page (Swagger) on the `vandalism` camera, with the saved prompt's `PromptId` but a
**different question** (window/throwing) from the saved text (fence):

- Hafnia answered the question we sent, not the saved one: the gateway uses the sent `Prompt` text.
- The answer ended with `SCENARIO_DETECTED`; the gateway rule raised *Scenario Lab detection*, and the alarm
  *Scenario Lab: scenario detected* appeared in Alarm Manager. It took a while (minutes) to show up.
- Smart Client's History adds "This summary is AI-generated:" in front of the answer when copying; the API's
  `Response` field does not contain it.

The exact request and answer (camera ID is specific to this machine):

```json
{
  "Id": "bf2b54bf-c230-4045-b930-6d483ec19a81",
  "CreationTimeUtc": "2026-10-17T15:37:09.852+00:00",
  "LastUpdatedTimeUtc": "2026-10-17T15:37:15.493+00:00",
  "Request": {
    "PromptId": "41626fc5-b9b7-40ea-a2b5-9bd57550ddea",
    "Prompt": "Is someone throwing an object at a building or breaking a window in this video?\nFirst describe in one or two sentences what happens in the clip. Then, on the last line, write exactly one word: SCENARIO_DETECTED if this happens in the clip, SCENARIO_NOT_DETECTED if it does not.",
    "VideoUris": [
      "xprotect://localhost/cameras/6B2A2E48-4054-42A7-A660-F5A3EC9B63B8?StartTime=2026-10-17T15:33:15Z&EndTime=2026-10-17T15:33:45Z"
    ],
    "AiModelId": "traffic-general"
  },
  "Source": { "Trigger": "Manual" },
  "Progress": "Completed",
  "Response": "A man in black clothing repeatedly throws a small object toward a building, striking the glass of a window and causing it to shatter. The broken window remains visibly damaged as he continues his actions. SCENARIO_DETECTED"
}
```

## Setup

`xprotect-setup/Setup-ScenarioLab.ps1` creates everything in the table below that is missing (the saved prompt and
gateway rule through the gateway API, the analytics event through the Configuration REST API, the alarm definition with
MilestonePSTools). Analytics events must be switched on by hand first: Management Client > Tools > Options > Analytics
Events.

## Names

| Thing | Where | Name / value |
|---|---|---|
| Saved prompt | Hafnia gateway, Prompts | Title `Scenario Lab` |
| Gateway rule | Hafnia gateway, Rules | Name `Scenario Lab`, prompt *Scenario Lab*, trigger text `SCENARIO_DETECTED` |
| Analytics event | Management Client, Rules and Events, Analytics Events | `Scenario Lab detection` |
| Alarm definition | Management Client, Alarms, Alarm Definitions | `Scenario Lab: scenario detected`, sources *All Cameras* (an alarm only appears for cameras in its sources) |

## Keyword instruction (appended to every question)

```
First describe in one or two sentences what happens in the clip. Then, on the last line, write exactly one word: SCENARIO_DETECTED if this happens in the clip, SCENARIO_NOT_DETECTED if it does not.
```

## Saved prompt text

The plug-in replaces the question with the user's own; this text is only used when someone runs the prompt by hand
in Smart Client.

```
Is anyone climbing over a fence or wall in this video?
First describe in one or two sentences what happens in the clip. Then, on the last line, write exactly one word: SCENARIO_DETECTED if this happens in the clip, SCENARIO_NOT_DETECTED if it does not.
```

## Gateway rule as JSON (for `POST /api/Rules`, used later by the setup script)

```json
{
  "Name": "Scenario Lab",
  "Description": "Raise an XProtect alarm when Hafnia confirms the tested scenario",
  "PromptId": "<id of the saved prompt 'Scenario Lab'>",
  "Triggers": [
    { "TriggerText": "SCENARIO_DETECTED", "RuleActionId": "TriggerAnalyticsEvent", "RuleActionData": "Scenario Lab detection" }
  ]
}
```

## System prompt

Not used for now (gateway Settings, *System Prompt* stays empty).
