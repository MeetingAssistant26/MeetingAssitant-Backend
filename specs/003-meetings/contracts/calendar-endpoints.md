# Calendar Endpoints Contract

**Controller**: `CalendarController`
**Base Route**: `api/organizations/{orgId:guid}/calendar`
**Auth**: `[Authorize]` + `[EnforceOrgAccess]`

---

## GET /api/organizations/{orgId}/calendar?week={date}

**Action**: GetCalendarData
**Authorization**: Any org member (including Guests)

**Query Parameters**:

| Param | Type | Required | Default | Notes |
|-------|------|----------|---------|-------|
| week | string (date) | No | Current week's Monday | ISO 8601 date (YYYY-MM-DD), snapped to Monday |

**Success Response**: `200 OK`
```json
{
  "weekStart": "2026-04-13",
  "weekEnd": "2026-04-19",
  "meetings": [
    {
      "id": "guid",
      "title": "Sprint Planning",
      "scheduledStartUtc": "2026-04-15T09:00:00Z",
      "scheduledEndUtc": "2026-04-15T10:00:00Z",
      "status": "Scheduled",
      "participantCount": 5,
      "tags": [
        { "id": "guid", "name": "Engineering", "color": "#FF0000" }
      ]
    }
  ]
}
```

**Query Logic**:
- Date range: `[weekStart 00:00:00 UTC, weekStart + 7 days 00:00:00 UTC)`
- Filter: `ScheduledStartUtc >= rangeStart AND ScheduledStartUtc < rangeEnd`
- Includes all statuses (Scheduled, InProgress, Completed, Cancelled, Failed)
- Ordered by ScheduledStartUtc ASC
- No pagination — returns all meetings in the week (expected to be a manageable number)

**Error Responses**:
- `400 Bad Request` — Invalid date format
- `403 Forbidden` — Wrong organization
