# Data Model: Meetings

**Feature**: 003-meetings | **Date**: 2026-04-08

## Entities

### Meeting

Represents a scheduled event within an organization.

| Field | Type | Constraints | Notes |
|-------|------|-------------|-------|
| Id | Guid | PK, auto-generated | Inherited from BaseEntity |
| OrganizationId | Guid | FK, required, indexed | IHasOrganizationId — tenant isolation via global query filter |
| Title | string | Required, max 200 chars | |
| Description | string? | Optional, max 2000 chars | |
| ScheduledStartUtc | DateTime | Required | UTC only |
| ScheduledEndUtc | DateTime | Required | Must be after ScheduledStartUtc |
| Status | MeetingStatus (enum) | Required, default: Scheduled | Stored as int |
| RecurrenceConfig | RecurrenceConfig? | Optional, JSONB | Owned entity mapped to JSON column |
| CreatedAtUtc | DateTime | Auto-set | Inherited from BaseEntity |
| UpdatedAtUtc | DateTime | Auto-set | Inherited from BaseEntity |

**Relationships**:
- Belongs to one `Organization` (via OrganizationId)
- Has many `MeetingParticipant`
- Has many `MeetingMeetingTag`

**Indexes**:
- `IX_Meetings_OrganizationId` on OrganizationId
- `IX_Meetings_OrgId_ScheduledStartUtc` on (OrganizationId, ScheduledStartUtc) — for calendar/list queries
- `IX_Meetings_OrgId_Status` on (OrganizationId, Status) — for filtering

---

### MeetingParticipant

Represents a user's involvement in a specific meeting with a designated role.

| Field | Type | Constraints | Notes |
|-------|------|-------------|-------|
| Id | Guid | PK, auto-generated | Inherited from BaseEntity |
| MeetingId | Guid | FK to Meeting, required | |
| OrganizationId | Guid | FK, required | IHasOrganizationId — tenant isolation |
| UserId | Guid | FK to ApplicationUser, required | |
| MeetingRole | MeetingRole (enum) | Required | Host, CoHost, Participant, Observer |
| CreatedAtUtc | DateTime | Auto-set | Inherited from BaseEntity |
| UpdatedAtUtc | DateTime | Auto-set | Inherited from BaseEntity |

**Relationships**:
- Belongs to one `Meeting` (via MeetingId)
- Belongs to one `ApplicationUser` (via UserId)

**Indexes**:
- `IX_MeetingParticipants_MeetingId_UserId` on (MeetingId, UserId) — UNIQUE, prevents duplicate participants
- `IX_MeetingParticipants_UserId` on UserId — for conflict detection queries
- `IX_MeetingParticipants_OrganizationId` on OrganizationId

**Constraints**:
- Unique constraint on (MeetingId, UserId) — a user can only appear once per meeting

---

### MeetingMeetingTag (Junction Table)

Links meetings to organization-scoped tags (many-to-many).

| Field | Type | Constraints | Notes |
|-------|------|-------------|-------|
| MeetingId | Guid | Composite PK, FK to Meeting | |
| MeetingTagId | Guid | Composite PK, FK to MeetingTag | |

**Relationships**:
- Belongs to one `Meeting` (via MeetingId)
- Belongs to one `MeetingTag` (via MeetingTagId, defined in Phase 2)

**Note**: No BaseEntity inheritance — this is a pure junction table with composite PK only. No Id, CreatedAtUtc, or UpdatedAtUtc.

---

### RecurrenceConfig (Owned Entity / JSONB)

Stored as a JSON column on the Meeting entity. Not a standalone table.

| Field | Type | Constraints | Notes |
|-------|------|-------------|-------|
| Frequency | RecurrenceFrequency (enum) | Required | Daily, Weekly, Monthly |
| Interval | int | Required, min 1 | e.g., every 2 weeks = Weekly + interval 2 |
| DaysOfWeek | List\<DayOfWeek\>? | Optional | For weekly recurrence — which days |
| EndsAtUtc | DateTime? | Optional | If null, default horizon of 12 weeks |

---

## Enums

### MeetingStatus

```
Scheduled = 0
InProgress = 1
Completed = 2
Cancelled = 3
Failed = 4
```

### MeetingRole

```
Host = 0
CoHost = 1
Participant = 2
Observer = 3
```

### RecurrenceFrequency

```
Daily = 0
Weekly = 1
Monthly = 2
```

---

## State Transitions

```
                    ┌──────────────┐
                    │  Scheduled   │
                    └──────┬───────┘
                           │
              ┌────────────┼────────────┐
              │            │            │
              v            v            │
       ┌────────────┐  ┌──────────┐    │
       │ InProgress │  │Cancelled │    │
       └──────┬─────┘  └──────────┘    │
              │                         │
         ┌────┼────┐                    │
         │         │                    │
         v         v                    │
   ┌──────────┐ ┌────────┐             │
   │Completed │ │ Failed │             │
   └──────────┘ └────────┘             │
```

**Phase 3 implements**: Scheduled -> Cancelled (via Host action)
**Phase 4 implements**: Scheduled -> InProgress, InProgress -> Completed, InProgress -> Failed

---

## Entity Relationship Diagram

```
Organization (Phase 2)
    │ 1
    │
    │ *
Meeting ─────────── MeetingMeetingTag ─────────── MeetingTag (Phase 2)
    │ 1                  * : *                         1
    │
    │ *
MeetingParticipant
    │ *
    │
    │ 1
ApplicationUser (Phase 1)
```
