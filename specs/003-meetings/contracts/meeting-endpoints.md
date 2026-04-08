# Meeting Endpoints Contract

**Controller**: `MeetingController`
**Base Route**: `api/organizations/{orgId:guid}/meetings`
**Auth**: `[Authorize]` + `[EnforceOrgAccess]`

---

## POST /api/organizations/{orgId}/meetings

**Action**: CreateMeeting
**Authorization**: Org Admin or Member (Guests cannot create)

**Request Body** (`CreateMeetingRequest`):
```json
{
  "title": "Sprint Planning",
  "description": "Weekly sprint planning session",
  "scheduledStartUtc": "2026-04-15T09:00:00Z",
  "scheduledEndUtc": "2026-04-15T10:00:00Z",
  "tagIds": ["guid-1", "guid-2"]
}
```

| Field | Type | Required | Validation |
|-------|------|----------|------------|
| title | string | Yes | 1-200 chars |
| description | string | No | Max 2000 chars |
| scheduledStartUtc | DateTime | Yes | Must be in the future |
| scheduledEndUtc | DateTime | Yes | Must be after scheduledStartUtc |
| tagIds | Guid[] | No | Each must reference an active MeetingTag in the same org |

**Success Response**: `200 OK`
```json
{
  "id": "guid",
  "organizationId": "guid",
  "title": "Sprint Planning",
  "description": "Weekly sprint planning session",
  "scheduledStartUtc": "2026-04-15T09:00:00Z",
  "scheduledEndUtc": "2026-04-15T10:00:00Z",
  "status": "Scheduled",
  "participantCount": 1,
  "tags": [
    { "id": "guid-1", "name": "Engineering", "color": "#FF0000" }
  ],
  "createdAtUtc": "2026-04-08T12:00:00Z"
}
```

**Error Responses**:
- `400 Bad Request` — Validation failure (end before start, title empty, etc.)
- `403 Forbidden` — Guest role or wrong organization
- `404 Not Found` — Tag not found or inactive

**Domain Events**: `MeetingCreatedEvent(MeetingId, OrganizationId, CreatedByUserId)`

---

## PUT /api/organizations/{orgId}/meetings/{id:guid}

**Action**: UpdateMeeting
**Authorization**: Must be meeting Host

**Request Body** (`UpdateMeetingRequest`):
```json
{
  "title": "Updated Sprint Planning",
  "description": "Updated description",
  "scheduledStartUtc": "2026-04-15T09:30:00Z",
  "scheduledEndUtc": "2026-04-15T10:30:00Z",
  "tagIds": ["guid-1"]
}
```

| Field | Type | Required | Validation |
|-------|------|----------|------------|
| title | string | No | 1-200 chars if provided |
| description | string | No | Max 2000 chars |
| scheduledStartUtc | DateTime | No | Must be in the future if provided |
| scheduledEndUtc | DateTime | No | Must be after start if provided |
| tagIds | Guid[] | No | Replaces all tag associations if provided |

**Success Response**: `200 OK` — Updated `MeetingResponse`

**Error Responses**:
- `400 Bad Request` — Validation failure
- `403 Forbidden` — Not the Host, or meeting not in Scheduled status
- `404 Not Found` — Meeting not found

**Domain Events**: `MeetingUpdatedEvent(MeetingId, OrganizationId)`

---

## DELETE /api/organizations/{orgId}/meetings/{id:guid}

**Action**: CancelMeeting
**Authorization**: Must be meeting Host

**Request Body**: None

**Success Response**: `200 OK`
```json
{
  "id": "guid",
  "status": "Cancelled"
}
```

**Error Responses**:
- `403 Forbidden` — Not the Host, or meeting not in Scheduled status
- `404 Not Found` — Meeting not found

**Domain Events**: `MeetingCancelledEvent(MeetingId, OrganizationId)`

---

## GET /api/organizations/{orgId}/meetings?filter={upcoming|past}&page={n}&pageSize={n}

**Action**: ListMeetings
**Authorization**: Any org member (including Guests)

**Query Parameters**:

| Param | Type | Required | Default | Notes |
|-------|------|----------|---------|-------|
| filter | string | No | (all) | "upcoming" or "past" |
| page | int | No | 1 | Page number |
| pageSize | int | No | 20 | Max 100 |

**Success Response**: `200 OK`
```json
{
  "items": [
    {
      "id": "guid",
      "title": "Sprint Planning",
      "scheduledStartUtc": "2026-04-15T09:00:00Z",
      "scheduledEndUtc": "2026-04-15T10:00:00Z",
      "status": "Scheduled",
      "participantCount": 5,
      "tags": [
        { "id": "guid", "name": "Engineering", "color": "#FF0000" }
      ],
      "createdAtUtc": "2026-04-08T12:00:00Z"
    }
  ],
  "totalCount": 42,
  "page": 1,
  "pageSize": 20
}
```

**Filtering Logic**:
- `upcoming`: ScheduledStartUtc > now AND Status IN (Scheduled, InProgress). Ordered by ScheduledStartUtc ASC.
- `past`: Status IN (Completed, Cancelled) OR ScheduledEndUtc < now. Ordered by ScheduledStartUtc DESC.
- No filter: All meetings, ordered by ScheduledStartUtc DESC.
