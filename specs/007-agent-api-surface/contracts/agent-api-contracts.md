# Agent API Contracts

**Feature**: Agent-Callable API Surface (Phase 5.7)  
**Base URL**: `/api/agent`  
**Auth**: `Authorization: Bearer <agent-jwt>` (AgentOnly policy)

---

## Authentication

### Agent JWT Claims

| Claim | Type | Required | Description |
|-------|------|----------|-------------|
| `agent` | string | Yes | Must be `"true"` |
| `organizationId` | guid | Yes | Tenant binding |
| `meetingId` | guid | Yes | Bound meeting |
| `assistedUserId` | guid | No | The user the agent is primarily assisting |
| `exp` | unix timestamp | Yes | Token expiry |

### Token Refresh

```http
POST /api/agent/refresh
Authorization: Bearer <current-agent-token>
```

**Response**: `200 OK` with new agent JWT body, or `403 Forbidden` if meeting no longer `InProgress`.

---

## Reminder Endpoints

### Create Reminder

```http
POST /api/agent/meetings/{meetingId}/reminders
Authorization: Bearer <agent-jwt>
Content-Type: application/json

{
  "text": "Review Q3 metrics",
  "scope": "Public",
  "targetUserId": null,
  "reminderAtUtc": "2026-05-10T09:00:00Z",
  "createdByUserId": "550e8400-e29b-41d4-a716-446655440000"
}
```

**Validation**:
- `meetingId` route param MUST match token `meetingId` claim
- `scope` ∈ {`Personal`, `Public`}
- `targetUserId` required when `scope = Personal`
- `text` max 500 characters
- `createdByUserId` nullable

**Response**: `201 Created` → `AgentReminderResponse`

### List Public Meeting Reminders

```http
GET /api/agent/meetings/{meetingId}/reminders
Authorization: Bearer <agent-jwt>
```

**Query Logic**:
```sql
SELECT * FROM Reminders
WHERE OrganizationId = <token.orgId>
  AND MeetingId = <meetingId>
  AND Scope = 'Public'
  AND Status = 'Active'
  AND ReminderAtUtc <= (
    SELECT ScheduledStartUtc FROM Meetings WHERE Id = <meetingId>
  )
ORDER BY ReminderAtUtc
```

**Response**: `200 OK` → `List<AgentReminderResponse>`

### Mark Reminder Delivered

```http
POST /api/agent/reminders/{id}/mark-delivered
Authorization: Bearer <agent-jwt>
```

**Validation**:
- Reminder MUST exist and belong to token's organization
- Reminder.Scope MUST be `Public` (defence-in-depth)

**Response**: `204 NoContent` or `403 Forbidden`

### Cancel Reminder

```http
DELETE /api/agent/reminders/{id}
Authorization: Bearer <agent-jwt>
```

**Validation**:
- Reminder MUST exist and belong to token's organization
- Soft delete: sets `Status = Cancelled`

**Response**: `204 NoContent` or `403 Forbidden`

---

## Context Endpoints

### Get Organization

```http
GET /api/agent/organization
Authorization: Bearer <agent-jwt>
```

**Response**: `200 OK` → `AgentOrganizationResponse`

```json
{
  "id": "550e8400-e29b-41d4-a716-446655440000",
  "name": "Acme Corp",
  "slug": "acme-corp",
  "memberCount": 12
}
```

### Get Meeting Members

```http
GET /api/agent/meetings/{meetingId}/members
Authorization: Bearer <agent-jwt>
```

**Validation**: `meetingId` MUST match token `meetingId` claim.

**Response**: `200 OK` → `List<AgentMemberResponse>`

```json
[
  {
    "userId": "550e8400-e29b-41d4-a716-446655440001",
    "displayName": "Ahmed Hassan",
    "jobRole": "Backend Engineer",
    "context": "Ahmed works on the payment gateway and API infrastructure"
  }
]
```

### List Meetings

```http
GET /api/agent/meetings?status=upcoming&limit=20&offset=0
Authorization: Bearer <agent-jwt>
```

**Parameters**:
- `status`: `upcoming` | `past` (optional, default `upcoming`)
- `limit`: 1–100 (optional, default 20)
- `offset`: ≥0 (optional, default 0)

**Response**: `200 OK` → `PaginatedList<AgentMeetingResponse>`

```json
{
  "items": [
    {
      "id": "...",
      "title": "Weekly Standup",
      "scheduledStartUtc": "2026-05-10T09:00:00Z",
      "scheduledEndUtc": "2026-05-10T09:30:00Z",
      "status": "Scheduled",
      "recurrenceConfig": { "frequency": "weekly", ... },
      "tagIds": ["..."]
    }
  ],
  "totalCount": 45,
  "limit": 20,
  "offset": 0
}
```

### Get Meeting Detail

```http
GET /api/agent/meetings/{meetingId}
Authorization: Bearer <agent-jwt>
```

**Response**: `200 OK` → `AgentMeetingDetailResponse`

```json
{
  "id": "...",
  "title": "Weekly Standup",
  "scheduledStartUtc": "2026-05-10T09:00:00Z",
  "scheduledEndUtc": "2026-05-10T09:30:00Z",
  "status": "Scheduled",
  "recurrenceConfig": { "frequency": "weekly", ... },
  "tagIds": ["..."],
  "participants": [
    { "userId": "...", "displayName": "...", "jobRole": "...", "context": "..." }
  ]
}
```

### List Recurring Meetings

```http
GET /api/agent/meetings/recurring
Authorization: Bearer <agent-jwt>
```

**Response**: `200 OK` → `List<AgentMeetingResponse>` (filtered to `RecurrenceConfig IS NOT NULL`)

### List Meeting Tags

```http
GET /api/agent/meeting-tags
Authorization: Bearer <agent-jwt>
```

**Response**: `200 OK` → `List<MeetingTagResponse>`

```json
[
  { "id": "...", "name": "Engineering", "color": "#4CAF50" }
]
```

---

## Response Models

### AgentOrganizationResponse

```csharp
public sealed record AgentOrganizationResponse(
    Guid Id,
    string Name,
    string Slug,
    int MemberCount
);
```

### AgentMemberResponse

```csharp
public sealed record AgentMemberResponse(
    Guid UserId,
    string DisplayName,
    string? JobRole,
    string? Context
);
```

### AgentMeetingResponse

```csharp
public sealed record AgentMeetingResponse(
    Guid Id,
    string Title,
    DateTime ScheduledStartUtc,
    DateTime ScheduledEndUtc,
    string Status,
    RecurrenceConfig? RecurrenceConfig,
    List<Guid> TagIds
);
```

### AgentMeetingDetailResponse

```csharp
public sealed record AgentMeetingDetailResponse(
    Guid Id,
    string Title,
    DateTime ScheduledStartUtc,
    DateTime ScheduledEndUtc,
    string Status,
    RecurrenceConfig? RecurrenceConfig,
    List<Guid> TagIds,
    List<AgentMemberResponse> Participants
);
```

### AgentReminderResponse

```csharp
public sealed record AgentReminderResponse(
    Guid Id,
    string Text,
    string Scope,
    Guid? TargetUserId,
    DateTime ReminderAtUtc,
    string Status
);
```

### CreateAgentReminderRequest

```csharp
public sealed record CreateAgentReminderRequest(
    string Text,
    string Scope,           // "Personal" | "Public"
    Guid? TargetUserId,     // required when Scope = "Personal"
    DateTime ReminderAtUtc,
    Guid? CreatedByUserId   // nullable when attribution unclear
);
```

---

## Error Responses

All errors follow RFC 7807 Problem Details format via `Result.ToProblem()`:

| Status | Code | When |
|--------|------|------|
| 400 | ValidationError | Request body fails FluentValidation |
| 403 | AccessDenied | MeetingId mismatch, cross-tenant access, or agent accessing personal reminder |
| 404 | NotFound | Reminder or meeting not found |
| 429 | RateLimitExceeded | >100 req/min per meetingId |
| 500 | InternalError | Unexpected server error (with CorrelationId) |

Example 403:
```json
{
  "type": "AccessDenied",
  "title": "Meeting ID does not match agent token",
  "status": 403,
  "correlationId": "abc-123-def"
}
```
