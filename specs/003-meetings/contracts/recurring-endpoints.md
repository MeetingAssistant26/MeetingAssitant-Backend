# Recurring Meeting Endpoints Contract

**Controller**: `RecurringMeetingController`
**Base Route**: `api/organizations/{orgId:guid}/meetings/recurring`
**Auth**: `[Authorize]` + `[EnforceOrgAccess]`

---

## POST /api/organizations/{orgId}/meetings/recurring

**Action**: CreateRecurringMeeting
**Authorization**: Org Admin or Member (Guests cannot create)

**Request Body** (`CreateRecurringMeetingRequest`):
```json
{
  "title": "Weekly Standup",
  "description": "Daily standup meeting",
  "scheduledStartTimeUtc": "09:00:00",
  "scheduledEndTimeUtc": "09:15:00",
  "recurrence": {
    "frequency": "Weekly",
    "interval": 1,
    "daysOfWeek": ["Monday", "Wednesday", "Friday"],
    "endsAtUtc": "2026-07-01T00:00:00Z"
  },
  "tagIds": ["guid-1"]
}
```

| Field | Type | Required | Validation |
|-------|------|----------|------------|
| title | string | Yes | 1-200 chars |
| description | string | No | Max 2000 chars |
| scheduledStartTimeUtc | TimeSpan | Yes | Time of day for each instance |
| scheduledEndTimeUtc | TimeSpan | Yes | Must be after start time |
| recurrence.frequency | string | Yes | Daily, Weekly, Monthly |
| recurrence.interval | int | Yes | Min 1, max 12 |
| recurrence.daysOfWeek | string[] | Conditional | Required for Weekly frequency |
| recurrence.endsAtUtc | DateTime | No | If null, defaults to 12 weeks from now |
| tagIds | Guid[] | No | Active MeetingTags in same org |

**Success Response**: `200 OK`
```json
{
  "generatedCount": 12,
  "meetings": [
    {
      "id": "guid",
      "title": "Weekly Standup",
      "scheduledStartUtc": "2026-04-13T09:00:00Z",
      "scheduledEndUtc": "2026-04-13T09:15:00Z",
      "status": "Scheduled"
    }
  ]
}
```

**Error Responses**:
- `400 Bad Request` — Invalid recurrence config (zero interval, empty daysOfWeek for Weekly, end before start, produces zero instances)
- `403 Forbidden` — Guest role or wrong organization

**Business Rules**:
- Each generated instance is an independent Meeting entity
- Creator is added as Host to every instance
- RecurrenceConfig is stored on each instance for reference
- Tags are associated with all generated instances
- Maximum generation horizon: 52 weeks (even if endsAtUtc is further)
- If no endsAtUtc, default to 12 weeks from the first instance

**Domain Events**: `MeetingCreatedEvent` emitted for each generated instance
