# End-to-End Flow: Agent-Callable API Surface

**Feature**: Phase 5.7 — Agent-Callable API Surface  
**Purpose**: Walk through complete agent-backend interactions during a live meeting lifecycle  
**Audience**: Technical reviewers before implementation  
**Date**: 2026-05-03

---

## Scenario: Weekly Engineering Standup with AI Agent

### Cast

- **Organization**: "TechCorp" (`org-id: techcorp-uuid`)
- **Meeting**: "Weekly Engineering Standup" (`meeting-id: standup-uuid`)
- **Participants**:
  - Ahmed (`user-id: ahmed-uuid`) — Backend Engineer, context: "Works on payment gateway and API infrastructure"
  - Sara (`user-id: sara-uuid`) — UI/UX Lead, context: "Responsible for design system and mobile app UX"
  - Ali (`user-id: ali-uuid`) — DevOps Engineer, context: "Manages CI/CD pipelines and cloud infrastructure"
- **AI Agent**: External LiveKit agent, connected via LiveKit Cloud

---

## Flow 1: Meeting Start — Agent Authentication & Context Discovery

### Step 1.1: LiveKit Room Started

When the LiveKit Cloud room starts for the standup, the backend webhook handler (Phase 4) receives the `room_started` event. The backend mints an agent token:

```http
POST /api/internal/agent-token (internal endpoint, not part of this spec)
```

**Backend generates**:
```json
{
  "agent": "true",
  "organizationId": "techcorp-uuid",
  "meetingId": "standup-uuid",
  "assistedUserId": null,
  "exp": 1715431200
}
```

The agent receives this token via the same channel it uses to join the LiveKit room.

---

### Step 1.2: Agent Queries Organization Context

The agent makes its first API call to understand the organization:

```http
GET /api/agent/organization
Authorization: Bearer <agent-jwt>
```

**Response**:
```json
{
  "id": "techcorp-uuid",
  "name": "TechCorp",
  "slug": "techcorp",
  "memberCount": 12
}
```

The agent now knows it's serving "TechCorp" with 12 total members.

---

### Step 1.3: Agent Queries Meeting Participants

The agent needs to know who's in the room:

```http
GET /api/agent/meetings/standup-uuid/members
Authorization: Bearer <agent-jwt>
```

**Response**:
```json
[
  {
    "userId": "ahmed-uuid",
    "displayName": "Ahmed Hassan",
    "jobRole": "Backend Engineer",
    "context": "Works on payment gateway and API infrastructure"
  },
  {
    "userId": "sara-uuid",
    "displayName": "Sara Kim",
    "jobRole": "UI/UX Lead",
    "context": "Responsible for design system and mobile app UX"
  },
  {
    "userId": "ali-uuid",
    "displayName": "Ali Rahman",
    "jobRole": "DevOps Engineer",
    "context": "Manages CI/CD pipelines and cloud infrastructure"
  }
]
```

The agent now has a "who's who" for the room and can personalize responses.

---

### Step 1.4: Agent Checks for Pending Public Reminders

At meeting start, the agent checks if there are any public reminders to surface:

```http
GET /api/agent/meetings/standup-uuid/reminders
Authorization: Bearer <agent-jwt>
```

**Query logic** (from spec FR-008):
```sql
SELECT * FROM Reminders
WHERE OrganizationId = 'techcorp-uuid'
  AND MeetingId = 'standup-uuid'
  AND Scope = 'Public'
  AND Status = 'Active'
  AND ReminderAtUtc <= (SELECT ScheduledStartUtc FROM Meetings WHERE Id = 'standup-uuid')
ORDER BY ReminderAtUtc
```

**Response** (two pending reminders from last week):
```json
[
  {
    "id": "reminder-1-uuid",
    "text": "Review Q3 budget proposal",
    "scope": "Public",
    "targetUserId": null,
    "reminderAtUtc": "2026-04-26T09:00:00Z",
    "status": "Active"
  },
  {
    "id": "reminder-2-uuid",
    "text": "Discuss new hiring plan for Q3",
    "scope": "Public",
    "targetUserId": null,
    "reminderAtUtc": "2026-04-26T09:00:00Z",
    "status": "Active"
  }
]
```

The agent speaks: *"Good morning team. I have two reminders from last week's standup. First, review the Q3 budget proposal. Second, discuss the new hiring plan for Q3."*

---

### Step 1.5: Agent Marks Reminders as Delivered

After speaking each reminder, the agent marks them delivered:

```http
POST /api/agent/reminders/reminder-1-uuid/mark-delivered
Authorization: Bearer <agent-jwt>
```

```http
POST /api/agent/reminders/reminder-2-uuid/mark-delivered
Authorization: Bearer <agent-jwt>
```

**Result**: Both reminders' `Status` is updated to `Delivered`, `DeliveredAtUtc` set to current time. They will never surface again.

---

## Flow 2: During Meeting — Live Interactions

### Step 2.1: Participant Creates a Public Reminder

Ahmed says: *"Remind us to review the API performance metrics next week."*

The agent's LLM parses this and calls:

```http
POST /api/agent/meetings/standup-uuid/reminders
Authorization: Bearer <agent-jwt>
Content-Type: application/json

{
  "text": "Review API performance metrics",
  "scope": "Public",
  "targetUserId": null,
  "reminderAtUtc": "2026-05-10T09:00:00Z",
  "createdByUserId": "ahmed-uuid"
}
```

**Validation**:
- `meetingId` route param (`standup-uuid`) matches token's `meetingId` claim ✅
- `scope` is `"Public"` ✅
- `targetUserId` is null (correct for Public scope) ✅
- `text` is under 500 chars ✅

**Backend persists**:
```sql
INSERT INTO Reminders (Id, OrganizationId, Text, Scope, Channel, CreatedByUserId, MeetingId, ReminderAtUtc, Status, CreatedAtUtc)
VALUES ('new-reminder-uuid', 'techcorp-uuid', 'Review API performance metrics', 'Public', 'Agent', 'ahmed-uuid', 'standup-uuid', '2026-05-10T09:00:00Z', 'Active', NOW())
```

**Response**: `201 Created` with `AgentReminderResponse`

The agent confirms: *"I'll remind the team to review API performance metrics at next week's standup."*

---

### Step 2.2: Participant Creates a Personal Reminder

Sara says: *"Remind me to update the design system documentation before the next meeting."*

The agent's LLM identifies "me" as Sara and calls:

```http
POST /api/agent/meetings/standup-uuid/reminders
Authorization: Bearer <agent-jwt>
Content-Type: application/json

{
  "text": "Update design system documentation",
  "scope": "Personal",
  "targetUserId": "sara-uuid",
  "reminderAtUtc": "2026-05-09T18:00:00Z",
  "createdByUserId": "sara-uuid"
}
```

**Validation**:
- `scope` is `"Personal"` ✅
- `targetUserId` is provided (required for Personal) ✅
- `targetUserId` = `createdByUserId` = Sara ✅

**Backend persists**:
```sql
INSERT INTO Reminders (Id, OrganizationId, Text, Scope, Channel, CreatedByUserId, TargetUserId, MeetingId, ReminderAtUtc, Status, CreatedAtUtc)
VALUES ('personal-reminder-uuid', 'techcorp-uuid', 'Update design system documentation', 'Personal', 'Agent', 'sara-uuid', 'sara-uuid', 'standup-uuid', '2026-05-09T18:00:00Z', 'Active', NOW())
```

**Agent confirms**: *"I'll remind you to update the design system documentation on Friday at 6 PM, Sara."*

> **Important**: This personal reminder will NEVER be returned by `GET /api/agent/meetings/{id}/reminders` because that endpoint hard-filters `Scope = Public`. Only Sara can see it via `GET /api/me/reminders` (Phase 5.6).

---

### Step 2.3: Participant Cancels a Reminder

Ali says: *"Actually, never mind about the API metrics reminder — we covered that in Slack."*

The agent's LLM identifies which reminder Ali means (most recently created public reminder for this meeting) and calls:

```http
DELETE /api/agent/reminders/new-reminder-uuid
Authorization: Bearer <agent-jwt>
```

**Backend validates**:
- Reminder exists ✅
- Reminder belongs to `techcorp-uuid` ✅
- Soft-cancels: `Status = Cancelled`

**Agent confirms**: *"Got it, Ali. I've cancelled the API performance metrics reminder."*

---

### Step 2.4: Agent Navigates Meeting Catalog

Ahmed says: *"When is the next architecture review? I need to prepare."*

The agent doesn't know, so it queries:

```http
GET /api/agent/meetings?status=upcoming&limit=20&offset=0
Authorization: Bearer <agent-jwt>
```

**Response** (paginated):
```json
{
  "items": [
    {
      "id": "standup-uuid",
      "title": "Weekly Engineering Standup",
      "scheduledStartUtc": "2026-05-03T09:00:00Z",
      "scheduledEndUtc": "2026-05-03T09:30:00Z",
      "status": "InProgress",
      "recurrenceConfig": { "frequency": "weekly", "interval": 1, "daysOfWeek": ["Monday"] },
      "tagIds": ["eng-tag-uuid"]
    },
    {
      "id": "arch-review-uuid",
      "title": "Architecture Review Q2",
      "scheduledStartUtc": "2026-05-08T14:00:00Z",
      "scheduledEndUtc": "2026-05-08T15:30:00Z",
      "status": "Scheduled",
      "recurrenceConfig": null,
      "tagIds": ["arch-tag-uuid"]
    }
  ],
  "totalCount": 2,
  "limit": 20,
  "offset": 0
}
```

The agent finds the architecture review and gets details:

```http
GET /api/agent/meetings/arch-review-uuid
Authorization: Bearer <agent-jwt>
```

**Response**:
```json
{
  "id": "arch-review-uuid",
  "title": "Architecture Review Q2",
  "scheduledStartUtc": "2026-05-08T14:00:00Z",
  "scheduledEndUtc": "2026-05-08T15:30:00Z",
  "status": "Scheduled",
  "recurrenceConfig": null,
  "tagIds": ["arch-tag-uuid"],
  "participants": [
    { "userId": "ahmed-uuid", "displayName": "Ahmed Hassan", "jobRole": "Backend Engineer", "context": "Works on payment gateway and API infrastructure" },
    { "userId": "ali-uuid", "displayName": "Ali Rahman", "jobRole": "DevOps Engineer", "context": "Manages CI/CD pipelines and cloud infrastructure" }
  ]
}
```

**Agent responds**: *"The next architecture review is on Friday, May 8th at 2 PM. You'll be there with Ali, Ahmed."*

---

### Step 2.5: Agent Checks Recurring Meetings

Sara asks: *"Is this standup the only recurring meeting we have?"*

The agent queries:

```http
GET /api/agent/meetings/recurring
Authorization: Bearer <agent-jwt>
```

**Response**:
```json
[
  {
    "id": "standup-uuid",
    "title": "Weekly Engineering Standup",
    "scheduledStartUtc": "2026-05-03T09:00:00Z",
    "scheduledEndUtc": "2026-05-03T09:30:00Z",
    "status": "InProgress",
    "recurrenceConfig": { "frequency": "weekly", "interval": 1, "daysOfWeek": ["Monday"] },
    "tagIds": ["eng-tag-uuid"]
  }
]
```

**Agent responds**: *"Yes, this weekly standup is the only recurring meeting in the catalog right now, Sara."*

---

### Step 2.6: Agent Queries Meeting Tags

Ahmed says: *"Can you tag that architecture review reminder with 'backend'?"*

The agent first needs to know available tags:

```http
GET /api/agent/meeting-tags
Authorization: Bearer <agent-jwt>
```

**Response**:
```json
[
  { "id": "eng-tag-uuid", "name": "Engineering", "color": "#4CAF50" },
  { "id": "arch-tag-uuid", "name": "Architecture", "color": "#2196F3" },
  { "id": "backend-tag-uuid", "name": "Backend", "color": "#FF9800" }
]
```

The agent sees there's already a "Backend" tag and can inform Ahmed. (Note: Tag assignment to meetings is a user-facing feature; the agent is read-only for tags in Phase 5.7.)

---

## Flow 3: Token Refresh (Long Meeting)

### Step 3.1: Token Nears Expiry

The standup runs long (2 hours). The agent's token (4-hour lifetime) is at 3.5 hours. The agent proactively refreshes:

```http
POST /api/agent/refresh
Authorization: Bearer <current-agent-jwt>
```

**Backend validates**:
- Current token is valid ✅
- Meeting `standup-uuid` still has `Status = InProgress` ✅

**Backend mints new token** with same claims but extended expiry.

**Response**: `200 OK` with new JWT

---

### Step 3.2: Meeting Ends — Refresh Rejected

The host ends the meeting. Meeting status transitions to `Completed`.

The agent tries to refresh:

```http
POST /api/agent/refresh
Authorization: Bearer <current-agent-jwt>
```

**Backend validates**:
- Meeting `standup-uuid` has `Status = Completed` ❌

**Response**: `403 Forbidden`

The agent gracefully disconnects from the LiveKit room.

---

## Flow 4: Next Week — Reminder Surfaces

### Step 4.1: Next Standup Starts

One week later, the next standup starts (same recurring series, `standup-uuid`). A new agent instance joins with a new token bound to this meeting.

### Step 4.2: Agent Checks Reminders

```http
GET /api/agent/meetings/standup-uuid/reminders
Authorization: Bearer <new-agent-jwt>
```

**Query**:
```sql
SELECT * FROM Reminders
WHERE OrganizationId = 'techcorp-uuid'
  AND MeetingId = 'standup-uuid'
  AND Scope = 'Public'
  AND Status = 'Active'
  AND ReminderAtUtc <= '2026-05-10T09:00:00Z'  -- this meeting's scheduled start
ORDER BY ReminderAtUtc
```

**Response** (the API metrics reminder was cancelled, so nothing returns):
```json
[]
```

> **Note**: If the reminder had NOT been cancelled, it would surface here and the agent would speak it.

---

## Flow 5: Security & Edge Cases

### Case 5.1: Mismatched Meeting ID

An attacker tries to read members from a different meeting:

```http
GET /api/agent/meetings/other-meeting-uuid/members
Authorization: Bearer <agent-jwt-bound-to-standup-uuid>
```

**Backend validation**:
- Route `meetingId` (`other-meeting-uuid`) ≠ token `meetingId` (`standup-uuid`) ❌

**Response**: `403 Forbidden`

```json
{
  "type": "AccessDenied",
  "title": "Meeting ID does not match agent token",
  "status": 403,
  "correlationId": "abc-123-def"
}
```

---

### Case 5.2: Cross-Tenant Access

An agent token for "TechCorp" tries to read "RivalCorp" data:

The backend's global query filter ensures `WHERE OrganizationId = 'techcorp-uuid'` is applied to ALL queries. Even if the agent crafted a valid request, no RivalCorp data would be returned.

**Response**: `200 OK` with empty array (or `404 NotFound` for single-resource endpoints)

> **Defence-in-depth**: The `OrganizationId` claim is embedded in the token at minting time. The database query filter uses this claim, not any user-provided parameter.

---

### Case 5.3: Personal Reminder Leakage Prevention

Even if an attacker adds `?scope=Personal` to the query string:

```http
GET /api/agent/meetings/standup-uuid/reminders?scope=Personal
Authorization: Bearer <agent-jwt>
```

**Backend service layer**:
```csharp
// Hard-coded filter — query string is IGNORED
var reminders = await _dbContext.Reminders
    .Where(r => r.OrganizationId == orgId)
    .Where(r => r.MeetingId == meetingId)
    .Where(r => r.Scope == ReminderScope.Public)  // ← never Personal
    .Where(r => r.Status == ReminderStatus.Active)
    .Where(r => r.ReminderAtUtc <= meeting.ScheduledStartUtc)
    .ToListAsync();
```

**Response**: Only public reminders (or empty array). Personal reminders are never returned.

---

### Case 5.4: Agent Tries to Mark Personal Reminder Delivered

An agent tries to mark Sara's personal reminder as delivered:

```http
POST /api/agent/reminders/personal-reminder-uuid/mark-delivered
Authorization: Bearer <agent-jwt>
```

**Backend service layer**:
```csharp
var reminder = await _dbContext.Reminders.FindAsync(reminderId);
if (reminder.Scope == ReminderScope.Personal)
    return Result.Failure(AgentErrors.CannotMarkPersonalReminder);
```

**Response**: `403 Forbidden`

---

### Case 5.5: Rate Limiting

A misbehaving agent makes 150 requests in one minute:

**Backend** (rate limiter):
```csharp
// Partition key: "agent:standup-uuid"
// Limit: 100 requests per 1-minute window
```

After 100 requests, the 101st request returns:

**Response**: `429 Too Many Requests`

```http
Retry-After: 45
```

The agent's HTTP client should back off and retry after 45 seconds.

---

### Case 5.6: Null CreatedByUserId

Ahmed says: *"Remind us about the deployment."* The LLM isn't sure who said it (voice overlap).

The agent calls:

```http
POST /api/agent/meetings/standup-uuid/reminders
Content-Type: application/json

{
  "text": "Remind us about the deployment",
  "scope": "Public",
  "targetUserId": null,
  "reminderAtUtc": "2026-05-10T09:00:00Z",
  "createdByUserId": null
}
```

**Backend accepts** the reminder with `CreatedByUserId = null`. The reminder is still valid and will surface correctly, but the originating user is unattributed.

---

## Flow 6: Summary of State Transitions

### Reminder Lifecycle (Agent-Created)

```
[Agent creates reminder]
    │
    ▼
Status: Active
    │
    ├──→ [Agent marks delivered] ──→ Status: Delivered (closed)
    │
    └──→ [Agent cancels] ──────────→ Status: Cancelled (closed)
```

### Token Lifecycle

```
[Meeting starts]
    │
    ▼
Token minted (4h lifetime)
    │
    ├──→ [Agent calls refresh] ──→ New token issued (if meeting InProgress)
    │
    └──→ [Meeting ends] ─────────→ Refresh rejected (403)
                                   Token expires naturally
```

---

## Key Invariants Verified

| Invariant | How Verified |
|-----------|--------------|
| Agent never sees personal reminders | Service layer hard-filters `Scope = Public` |
| Agent cannot access other meetings | Route `meetingId` must match token claim |
| Agent cannot access other orgs | Global query filter on `OrganizationId` |
| Agent cannot mark personal reminders delivered | Service layer rejects `Scope = Personal` |
| Reminder timing gate works | `ReminderAtUtc <= meeting.ScheduledStartUtc` filter |
| Token refresh is meeting-bound | Refresh rejected when meeting `Status != InProgress` |
| Rate limiting is per-meeting | Partitioned rate limiter keyed on `meetingId` |

---

## Review Checklist

Before implementation, verify this flow document covers:

- [x] Token issuance at meeting start
- [x] Organization context retrieval
- [x] Meeting member retrieval with context
- [x] Public reminder creation and surfacing
- [x] Personal reminder creation and privacy
- [x] Reminder delivery and cancellation
- [x] Meeting catalog navigation (list, detail, recurring, tags)
- [x] Token refresh during long meetings
- [x] Token rejection after meeting ends
- [x] All security edge cases (mismatch, cross-tenant, leakage, rate limiting)
- [x] Null `createdByUserId` handling
- [x] State transitions for reminders and tokens

---

## Questions for Reviewers

1. **Should the agent be allowed to update a reminder's text after creation?** Currently not in spec — participant would cancel and recreate.
2. **Should the agent receive a SignalR notification when a new public reminder is created by another agent instance?** Currently not in spec — the next `GET` query will pick it up.
3. **Should cancelled reminders be returned in the list with `Status = Cancelled` for transparency?** Currently no — only `Active` reminders are returned.

If any of these need to change, update the spec before proceeding to implementation.
