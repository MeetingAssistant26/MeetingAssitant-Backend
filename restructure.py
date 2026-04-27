import re

FILE_PATH = r'C:\Users\DEll\source\repos\MeetingAssitant-Backend\docs\implementation-plan.md'

with open(FILE_PATH, 'r', encoding='utf-8') as f:
    text = f.read()

# --- Step 1: Extract Phase 7.5 ---
phase_7_5_pattern = r'(^# Phase 7\.5 — Agent-Callable API Surface.*?)(?=^# Phase 8 —|\Z)'
phase_7_5_match = re.search(phase_7_5_pattern, text, re.MULTILINE | re.DOTALL)
if not phase_7_5_match:
    raise ValueError("Could not find Phase 7.5")
phase_7_5_block = phase_7_5_match.group(1)

# Remove Phase 7.5 from its current location
text = text.replace(phase_7_5_block, '', 1)

# --- Step 2: Extract Reminder content from Phase 7 ---
# Phase 7 starts with its header and ends just before Phase 7.5 (which we just removed)
# So now Phase 7 is followed directly by Phase 8.
phase_7_pattern = r'(^# Phase 7 — Task Review Queue & Management.*?)(?=^# Phase 8 —|\Z)'
phase_7_match = re.search(phase_7_pattern, text, re.MULTILINE | re.DOTALL)
if not phase_7_match:
    raise ValueError("Could not find Phase 7")
phase_7_block = phase_7_match.group(1)

# We need to extract these subsections from Phase 7:
# A. Folder structure lines containing Reminder/
# B. v3.7 changes note mentioning reminders
# C. ReminderController definition + endpoints
# D. Reminder entity
# E. Reminder System
# F. Reminder Fetch Semantics
# G. Reminder domain events
# H. Reminder tests
# I. Reminder deliverable line

# It's safer to rebuild Phase 7 by removing known blocks.
phase_7_trimmed = phase_7_block

# Remove the Reminder folder structure from the Phase 7 tree
# The tree has:
#     │   └── Reminder/
#     │       ├── ReminderController.cs
#     ...
reminder_folder_tree = """    │   └── Reminder/
    │       ├── ReminderController.cs
    │       ├── CreateMyReminderEndpoint.cs        ← POST /api/me/reminders
    │       ├── ListMyRemindersEndpoint.cs         ← GET  /api/me/reminders
    │       ├── MarkMyReminderDeliveredEndpoint.cs ← POST /api/me/reminders/{id}/mark-delivered
    │       └── CancelMyReminderEndpoint.cs        ← DELETE /api/me/reminders/{id}"""
phase_7_trimmed = phase_7_trimmed.replace(reminder_folder_tree, '', 1)

# Remove reminder models/services/validators from Phase 7 tree
reminder_models_tree = """    │   │   └── CreateMyReminderRequest.cs
    │   │   └── ReminderResponse.cs"""
phase_7_trimmed = phase_7_trimmed.replace(reminder_models_tree, '', 1)

reminder_services_tree = """    │   ├── IReminderService.cs
    │   └── ReminderService.cs"""
phase_7_trimmed = phase_7_trimmed.replace(reminder_services_tree, '', 1)

reminder_validators_tree = """        └── CreateMyReminderRequestValidator.cs"""
phase_7_trimmed = phase_7_trimmed.replace(reminder_validators_tree, '', 1)

# Remove the v3.7 changes note block
v37_note = """> **v3.7 changes**: `TriggerReminderJob` removed (no Hangfire firing — reminders
> are pure data, fetched via endpoints). `IReminderService` is shared by user-
> facing endpoints here and the agent-facing endpoints in Phase 7.5."""
phase_7_trimmed = phase_7_trimmed.replace(v37_note, '', 1)

# Remove ReminderController block
reminder_controller_block = """### ReminderController — `api/me/reminders` (v3.7)

```csharp
namespace MeetingAssistant.Features.Tasks.Endpoints.Reminder;

[ApiController]
[Route("api/me/reminders")]
[Authorize]
public partial class ReminderController : ControllerBase
{
    private readonly IReminderService _reminderService;

    public ReminderController(IReminderService reminderService)
    {
        _reminderService = reminderService;
    }
}
```

> **v3.7 change**: route changed from `api/tasks/{taskId}/reminders` to
> `api/me/reminders`. Reminders are no longer linked to `TaskItem`. The thing
> to be reminded about is captured in the `Text` field on the `Reminder`
> entity (free string).

**Endpoints (all user-JWT scoped):**
- `POST   /api/me/reminders`                      → `CreateMyReminderEndpoint.cs` — creates `Scope=Personal, Channel=User, TargetUserId=me, MeetingId=null`
- `GET    /api/me/reminders`                      → `ListMyRemindersEndpoint.cs` — returns reminders affecting me: `(TargetUserId=me) OR (Scope=Public AND MeetingId IN <meetings I participate in>)`, filtered by `Status=Active`
- `POST   /api/me/reminders/{id}/mark-delivered`  → `MarkMyReminderDeliveredEndpoint.cs` — sets `Status=Delivered` (only the user's own Personal reminders)
- `DELETE /api/me/reminders/{id}`                 → `CancelMyReminderEndpoint.cs` — soft-cancel (`Status=Cancelled`), only the user's own Personal reminders
"""
phase_7_trimmed = phase_7_trimmed.replace(reminder_controller_block, '', 1)

# Remove Reminder entity block
reminder_entity_block = """## Entities

- **`Reminder`** (refactored in v3.7):
  - `Id`, `OrganizationId`
  - `Text` (free string — the thing to be reminded about; no `TaskItem` linkage)
  - `Scope` (enum: `Personal` | `Public`)
  - `Channel` (enum: `User` | `Agent` — provenance of the reminder)
  - `CreatedByUserId` (who originated the intent)
  - `TargetUserId` (nullable — required when `Scope=Personal`; null when `Scope=Public`. Public reminders target all participants of `MeetingId`, derived at fetch time from `MeetingParticipant`.)
  - `MeetingId` (nullable — required when `Scope=Public` or when `Channel=Agent`. Always null when `Channel=User` because user-created personal reminders are standalone.)
  - `ReminderAtUtc` (when the reminder is "due"; used by fetch endpoints as a filter — see Reminder Fetch Semantics below. No firing.)
  - `Status` (enum: `Active` | `Delivered` | `Cancelled`)
  - `DeliveredAtUtc` (nullable)
  - `OriginalText` (nullable — raw agent input, audit only)
  - `CreatedAtUtc`, `UpdatedAtUtc`
  - **DB Indexes**: `(TargetUserId, Status)` for the user's "my reminders" query; `(MeetingId, Scope, Status)` for the agent's "this meeting's public reminders" query.

> **v3.7 changes**: removed `TaskItemId` linkage, removed `Pending/Sent/Failed`
> states, removed Hangfire `TriggerReminderJob`, removed SignalR push. Reminders
> are pure data, fetched via endpoints. Status lifecycle is now `Active →
> Delivered` (after fetch + acknowledgement) or `Active → Cancelled` (soft delete).

## Reminder System (v3.7)

- **No Hangfire firing**, **no SignalR push**. Reminders are pure data.
- Personal reminders are surfaced to the user via the `GET /api/me/reminders` poll endpoint. The user marks delivery via `POST /api/me/reminders/{id}/mark-delivered`.
- Public reminders are surfaced to the agent via `GET /api/agent/meetings/{meetingId}/reminders` (Phase 7.5) at meeting start. The agent speaks them and marks delivery via `POST /api/agent/reminders/{id}/mark-delivered`.

## Reminder Fetch Semantics (v3.7)

`ReminderAtUtc` acts as a **timing gate** in fetch queries:

| Endpoint | Filter |
|---|---|
| `GET /api/me/reminders` (user) | `Status=Active AND ReminderAtUtc <= now AND ((TargetUserId=me) OR (Scope=Public AND MeetingId IN <my meetings>))` |
| `GET /api/agent/meetings/{meetingId}/reminders` (agent — Phase 7.5) | `Status=Active AND Scope=Public AND MeetingId=X AND ReminderAtUtc <= meeting.scheduledStartUtc` |

For Public reminders bound to a recurring meeting (one row per series, Model A), the agent query `ReminderAtUtc <= meeting.scheduledStartUtc` ensures the reminder fires at the first occurrence whose start time is on or after `ReminderAtUtc`. This lets agents express "remind us in 2 weeks at the standup" by setting `ReminderAtUtc` to the target date — earlier occurrences will not return the reminder. After the agent speaks it and marks `Delivered`, it never returns again.

> **No per-occurrence targeting** (`OccurrenceDateUtc` is intentionally not modeled).
> Each reminder fires at exactly one occurrence: the first one whose start time
> is on or after `ReminderAtUtc`. After delivery, the reminder is closed.
"""
phase_7_trimmed = phase_7_trimmed.replace(reminder_entity_block, '', 1)

# Remove reminder domain events
reminder_events = """- `ReminderCreatedEvent`
- `ReminderDeliveredEvent` (v3.7 — replaces `ReminderTriggeredEvent`)
- `ReminderCancelledEvent` (v3.7)
"""
phase_7_trimmed = phase_7_trimmed.replace(reminder_events, '', 1)

# Remove reminder tests
reminder_tests = """- Unit: reminder fetch semantics — `ReminderAtUtc` timing gate for Personal and Public
- Unit: `mark-delivered` and `cancel` state transitions
- Integration: user creates personal reminder → polls → marks delivered → no longer returned
- Integration: tenant isolation — user cannot see/mark/cancel another user's or another org's reminders
"""
phase_7_trimmed = phase_7_trimmed.replace(reminder_tests, '', 1)

# Update Phase 7 deliverable
phase_7_trimmed = phase_7_trimmed.replace(
    "- 3 controllers, 14 endpoint files (was 11; +4 user reminder endpoints, –1 task reminder endpoint)",
    "- 2 controllers, 10 endpoint files"
)

# Update Phase 7 title week range
phase_7_trimmed = phase_7_trimmed.replace(
    "# Phase 7 — Task Review Queue & Management (Weeks 11.5–13)",
    "# Phase 7 — Task Review Queue & Task Management (Weeks 14–15.5)"
)

# Update intro paragraph to reflect trimmed scope
phase_7_trimmed = phase_7_trimmed.replace(
    "## Folder Structure",
    "## Folder Structure"
)  # no change needed

# Replace old Phase 7 block with trimmed block in text
text = text.replace(phase_7_block, phase_7_trimmed, 1)

# --- Step 3: Build Phase 5.6 block from extracted content ---
phase_5_6_block = f"""---

# Phase 5.6 — Reminders — User-Facing (Weeks 9.5–10.5)

> Extracted from Phase 7 (v3.7). Reminders are now built before the post-meeting AI pipeline so that both user-facing and agent-facing surfaces are available for the Task Review Queue and Agent API phases.

## Folder Structure

```text
Features/
└── Tasks/
    ├── Endpoints/
    │   └── Reminder/
    │       ├── ReminderController.cs
    │       ├── CreateMyReminderEndpoint.cs        ← POST /api/me/reminders
    │       ├── ListMyRemindersEndpoint.cs         ← GET  /api/me/reminders
    │       ├── MarkMyReminderDeliveredEndpoint.cs ← POST /api/me/reminders/{id}/mark-delivered
    │       └── CancelMyReminderEndpoint.cs        ← DELETE /api/me/reminders/{id}
    │
    ├── Models/
    │   ├── Requests/
    │   │   └── CreateMyReminderRequest.cs
    │   └── Responses/
    │       └── ReminderResponse.cs
    │
    ├── Services/
    │   ├── IReminderService.cs
    │   └── ReminderService.cs
    │
    └── Validators/
        └── CreateMyReminderRequestValidator.cs
```

## Controller Definitions

### ReminderController — `api/me/reminders` (v3.7)

```csharp
namespace MeetingAssistant.Features.Tasks.Endpoints.Reminder;

[ApiController]
[Route("api/me/reminders")]
[Authorize]
public partial class ReminderController : ControllerBase
{{
    private readonly IReminderService _reminderService;

    public ReminderController(IReminderService reminderService)
    {{
        _reminderService = reminderService;
    }}
}}
```

> **v3.7 change**: route changed from `api/tasks/{{taskId}}/reminders` to
> `api/me/reminders`. Reminders are no longer linked to `TaskItem`. The thing
> to be reminded about is captured in the `Text` field on the `Reminder`
> entity (free string).

**Endpoints (all user-JWT scoped):**
- `POST   /api/me/reminders`                      → `CreateMyReminderEndpoint.cs` — creates `Scope=Personal, Channel=User, TargetUserId=me, MeetingId=null`
- `GET    /api/me/reminders`                      → `ListMyRemindersEndpoint.cs` — returns reminders affecting me: `(TargetUserId=me) OR (Scope=Public AND MeetingId IN <meetings I participate in>)`, filtered by `Status=Active`
- `POST   /api/me/reminders/{{id}}/mark-delivered`  → `MarkMyReminderDeliveredEndpoint.cs` — sets `Status=Delivered` (only the user's own Personal reminders)
- `DELETE /api/me/reminders/{{id}}`                 → `CancelMyReminderEndpoint.cs` — soft-cancel (`Status=Cancelled`), only the user's own Personal reminders

## Entities

- **`Reminder`** (refactored in v3.7):
  - `Id`, `OrganizationId`
  - `Text` (free string — the thing to be reminded about; no `TaskItem` linkage)
  - `Scope` (enum: `Personal` | `Public`)
  - `Channel` (enum: `User` | `Agent` — provenance of the reminder)
  - `CreatedByUserId` (who originated the intent)
  - `TargetUserId` (nullable — required when `Scope=Personal`; null when `Scope=Public`. Public reminders target all participants of `MeetingId`, derived at fetch time from `MeetingParticipant`.)
  - `MeetingId` (nullable — required when `Scope=Public` or when `Channel=Agent`. Always null when `Channel=User` because user-created personal reminders are standalone.)
  - `ReminderAtUtc` (when the reminder is "due"; used by fetch endpoints as a filter — see Reminder Fetch Semantics below. No firing.)
  - `Status` (enum: `Active` | `Delivered` | `Cancelled`)
  - `DeliveredAtUtc` (nullable)
  - `OriginalText` (nullable — raw agent input, audit only)
  - `CreatedAtUtc`, `UpdatedAtUtc`
  - **DB Indexes**: `(TargetUserId, Status)` for the user's "my reminders" query; `(MeetingId, Scope, Status)` for the agent's "this meeting's public reminders" query.

> **v3.7 changes**: removed `TaskItemId` linkage, removed `Pending/Sent/Failed`
> states, removed Hangfire `TriggerReminderJob`, removed SignalR push. Reminders
> are pure data, fetched via endpoints. Status lifecycle is now `Active →
> Delivered` (after fetch + acknowledgement) or `Active → Cancelled` (soft delete).

## Reminder System (v3.7)

- **No Hangfire firing**, **no SignalR push**. Reminders are pure data.
- Personal reminders are surfaced to the user via the `GET /api/me/reminders` poll endpoint. The user marks delivery via `POST /api/me/reminders/{{id}}/mark-delivered`.
- Public reminders are surfaced to the agent via `GET /api/agent/meetings/{{meetingId}}/reminders` (Phase 5.7) at meeting start. The agent speaks them and marks delivery via `POST /api/agent/reminders/{{id}}/mark-delivered`.

## Reminder Fetch Semantics (v3.7)

`ReminderAtUtc` acts as a **timing gate** in fetch queries:

| Endpoint | Filter |
|---|---|
| `GET /api/me/reminders` (user) | `Status=Active AND ReminderAtUtc <= now AND ((TargetUserId=me) OR (Scope=Public AND MeetingId IN <my meetings>))` |
| `GET /api/agent/meetings/{{meetingId}}/reminders` (agent — Phase 5.7) | `Status=Active AND Scope=Public AND MeetingId=X AND ReminderAtUtc <= meeting.scheduledStartUtc` |

For Public reminders bound to a recurring meeting (one row per series, Model A), the agent query `ReminderAtUtc <= meeting.scheduledStartUtc` ensures the reminder fires at the first occurrence whose start time is on or after `ReminderAtUtc`. This lets agents express "remind us in 2 weeks at the standup" by setting `ReminderAtUtc` to the target date — earlier occurrences will not return the reminder. After the agent speaks it and marks `Delivered`, it never returns again.

> **No per-occurrence targeting** (`OccurrenceDateUtc` is intentionally not modeled).
> Each reminder fires at exactly one occurrence: the first one whose start time
> is on or after `ReminderAtUtc`. After delivery, the reminder is closed.

## Domain Events

- `ReminderCreatedEvent`
- `ReminderDeliveredEvent` (v3.7 — replaces `ReminderTriggeredEvent`)
- `ReminderCancelledEvent` (v3.7)

## Tests (Phase 5.6)

- Unit: reminder fetch semantics — `ReminderAtUtc` timing gate for Personal and Public
- Unit: `mark-delivered` and `cancel` state transitions
- Integration: user creates personal reminder → polls → marks delivered → no longer returned
- Integration: tenant isolation — user cannot see/mark/cancel another user's or another org's reminders

Deliverable:
- User-facing reminder lifecycle works (create / list / mark-delivered / cancel)
- 1 controller, 4 endpoint files
"""

# --- Step 4: Update Phase 7.5 to become Phase 5.7 ---
phase_5_7_block = phase_7_5_block

# Update title and weeks
phase_5_7_block = phase_5_7_block.replace(
    "# Phase 7.5 — Agent-Callable API Surface (Week 13–14) (new in v3.7)",
    "# Phase 5.7 — Agent-Callable API Surface (Weeks 10.5–11.5) (new in v3.7)"
)

# Update cross-references within the block
phase_5_7_block = phase_5_7_block.replace(
    "// shared with Phase 7",
    "// shared with Phase 5.6"
)
phase_5_7_block = phase_5_7_block.replace(
    "`Reminder` (Phase 7)",
    "`Reminder` (Phase 5.6)"
)
phase_5_7_block = phase_5_7_block.replace(
    "(Phase 7.5)",
    "(Phase 5.7)"
)
phase_5_7_block = phase_5_7_block.replace(
    "Phase 7.5",
    "Phase 5.7"
)
# But careful: the title already changed, and we don't want to over-replace inside the block if there are other uses.
# The specific replacements above are targeted enough.

# --- Step 5: Insert new phases before Phase 6 ---
phase_6_header = "# Phase 6 — Post-Meeting AI Pipeline + Meeting Memory (Weeks 9.5–12)"
new_phase_6_header = "# Phase 6 — Post-Meeting AI Pipeline + Meeting Memory (Weeks 11.5–14)"

text = text.replace(phase_6_header, new_phase_6_header, 1)

insert_marker = new_phase_6_header
insert_text = phase_5_6_block + "\n\n---\n\n" + phase_5_7_block + "\n\n---\n\n"

text = text.replace(insert_marker, insert_text + insert_marker, 1)

# --- Step 6: Update other phase week ranges ---
text = text.replace(
    "# Phase 6.5 — Meeting Memory Query: RAG Endpoint (Week 11–11.5) (refactor require by chat gpt)",
    "# Phase 6.5 — Meeting Memory Query: RAG Endpoint (Week 14–14.5) (refactor require by chat gpt)"
)

text = text.replace(
    "# Phase 8 — Trello Integration (Weeks 13–14)",
    "# Phase 8 — Trello Integration (Weeks 15.5–16.5)"
)

text = text.replace(
    "# Phase 9 — Integration Testing & Hardening (Weeks 14–15)",
    "# Phase 9 — Integration Testing & Hardening (Weeks 16.5–17.5)"
)

text = text.replace(
    "# Phase 10 — Demo Preparation (Weeks 15–16)",
    "# Phase 10 — Demo Preparation (Weeks 17.5–18.5)"
)

# --- Step 7: Update cross-references in other sections ---
# Phase 0.3 SignalR note mentions Phase 7 reminders
# Actually it says: "All background job notifications (AI status updates, task events, RAG answers, reminders) MUST be sent..."
# This is fine, no phase number there.

# Phase 7.5 comment in Phase 5.7: "shared with Phase 5.6" already done.
# Phase 7.5 note: "`Reminder` (Phase 5.6)" already done.

# Update Phase 9 end-to-end flow step order
# Step 15 currently: "Reminder scheduled → reminder triggers (SignalR)"
# We should move it earlier or adjust. Actually the flow is just a demo narrative.
# Let's keep it but update the numbering if needed. The text doesn't have explicit numbers in the list items, they are just a numbered list.
# We can leave the order as is since it's a demo flow; the pipeline will still generate reminders before RAG.
# But let's adjust the step text to remove the SignalR mention since reminders don't use SignalR anymore.
text = text.replace(
    "15. Reminder scheduled → reminder triggers (SignalR)",
    "15. User creates personal reminder → polls → marks delivered"
)

# --- Step 8: Update Endpoint Summary table ---
old_summary_line = "| 7 | Tasks | 3 (ReviewQueue, Task, Reminder) | 14 | 14 |"
new_summary_line = "| 5.6 | Reminders | 1 (Reminder) | 4 | 4 |\n| 7 | Tasks | 2 (ReviewQueue, Task) | 10 | 10 |"
text = text.replace(old_summary_line, new_summary_line, 1)

old_summary_line_2 = "| 7.5 | Agent API Surface | 2 (AgentReminder, AgentContext) | 9 | 9 |"
new_summary_line_2 = "| 5.7 | Agent API Surface | 2 (AgentReminder, AgentContext) | 9 | 9 |"
text = text.replace(old_summary_line_2, new_summary_line_2, 1)

old_total_line = "| **Total** | | **21 controllers** | **57 endpoint files** | **57 actions** |"
new_total_line = "| **Total** | | **21 controllers** | **57 endpoint files** | **57 actions** |"
# totals unchanged: 21 controllers, 57 endpoints. Good.

# --- Step 9: Update Timeline table ---
old_timeline_lines = """| 6. Post-Meeting AI Pipeline + Meeting Memory | 2.5 weeks | 9–11.5 |
| 6.5. Meeting Memory Query (RAG) | 0.5 weeks | 11.5–12 |
| 7. Task Review Queue & Management | 1.5 weeks | 12–13.5 |
| 7.5. Agent-Callable API Surface (new in v3.7) | 1 week | 13.5–14.5 |
| 8. Trello Integration | 1 week | 14.5–15.5 |
| 9. Testing & Hardening | 1 week | 15.5–16.5 |
| 10. Demo Preparation | 1 week | 16.5–17.5 |"""

new_timeline_lines = """| 5.6. Reminders — User-Facing | 1 week | 9.5–10.5 |
| 5.7. Agent-Callable API Surface | 1 week | 10.5–11.5 |
| 6. Post-Meeting AI Pipeline + Meeting Memory | 2.5 weeks | 11.5–14 |
| 6.5. Meeting Memory Query (RAG) | 0.5 weeks | 14–14.5 |
| 7. Task Review Queue & Management | 1.5 weeks | 14.5–16 |
| 8. Trello Integration | 1 week | 16–17 |
| 9. Testing & Hardening | 1 week | 17–18 |
| 10. Demo Preparation | 1 week | 18–19 |"""

text = text.replace(old_timeline_lines, new_timeline_lines, 1)

# Update total duration text
text = text.replace(
    "**Total estimated duration: 17–18 weeks** (v3.7: +1 week for Phase 7.5 agent surface; +1 from v3.6 STT.)",
    "**Total estimated duration: 19–20 weeks** (v3.7: +1 week for Phase 5.6 Reminders, +1 week for Phase 5.7 agent surface; +1 from v3.6 STT.)"
)

# --- Step 10: Update Decisions & Changes log (v3.7) ---
# Change 114: New Phase 7.5 → New Phase 5.7
# Change 121: New user-facing reminder endpoints (Phase 7) → (Phase 5.6)
# Change 122: New agent-facing reminder endpoints (Phase 7.5) → (Phase 5.7)
# Change 123: Reminder fetch semantics (already references Phase 7.5)
# Change 124: (no phase number)
# Change 132: Timeline +1 week → +2 weeks
# Change 133: Endpoint summary updated

text = text.replace(
    "| 114 | New Phase 7.5 — Agent-Callable API Surface",
    "| 114 | New Phase 5.7 — Agent-Callable API Surface"
)
text = text.replace(
    "| 121 | New user-facing reminder endpoints (Phase 7)",
    "| 121 | New user-facing reminder endpoints (Phase 5.6)"
)
text = text.replace(
    "| 122 | New agent-facing reminder endpoints (Phase 7.5)",
    "| 122 | New agent-facing reminder endpoints (Phase 5.7)"
)
text = text.replace(
    "| 123 | Reminder fetch semantics — `ReminderAtUtc` timing gate | User query: ... Agent query: `Status=Active AND Scope=Public AND MeetingId=X AND ReminderAtUtc <= meeting.scheduledStartUtc`. Lets agent express \"remind us in 2 weeks at the standup\" without per-occurrence targeting. |",
    "| 123 | Reminder fetch semantics — `ReminderAtUtc` timing gate | User query: `Status=Active AND ReminderAtUtc <= now`. Agent query: `Status=Active AND Scope=Public AND MeetingId=X AND ReminderAtUtc <= meeting.scheduledStartUtc`. Lets agent express \"remind us in 2 weeks at the standup\" without per-occurrence targeting. |"
)
# Actually the exact text for 123 is very long and might be hard to match. Let me do a simpler replace.
text = text.replace(
    "Agent query: `Status=Active AND Scope=Public AND MeetingId=X AND ReminderAtUtc <= meeting.scheduledStartUtc`.",
    "Agent query: `Status=Active AND Scope=Public AND MeetingId=X AND ReminderAtUtc <= meeting.scheduledStartUtc`."
)  # no change needed there.

text = text.replace(
    "| 132 | Timeline +1 week | Phase 7.5 adds 1 week. Total: 17–18 weeks (was 16–17). |",
    "| 132 | Timeline +2 weeks | Phase 5.6 (+1 week) and Phase 5.7 (+1 week) inserted before Phase 6. Total: 19–20 weeks (was 17–18). |"
)

# Update decision 113 text
# "The backend's only contract is the endpoint surface defined in Phase 7.5 + the agent service-identity auth scheme."
text = text.replace(
    "The backend's only contract is the endpoint surface defined in Phase 7.5 + the agent service-identity auth scheme.",
    "The backend's only contract is the endpoint surface defined in Phase 5.7 + the agent service-identity auth scheme."
)

# Update v3.6 decisions that mention Phase 7
# Actually v3.6 decisions don't mention Phase 7 much.

# --- Step 11: Update Constitution Compliance references ---
# "Phase 7 reminders" in the SignalR row
text = text.replace(
    "(incl. Phase 7 reminders, Phase 8 sync status)",
    "(incl. Phase 5.6 reminders, Phase 8 sync status)"
)

# --- Step 12: Clean up double separators ---
text = text.replace("\n\n---\n\n---\n\n", "\n\n---\n\n", 1)

# Write back
with open(FILE_PATH, 'w', encoding='utf-8') as f:
    f.write(text)

print("Done. File updated.")
