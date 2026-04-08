# Participant Endpoints Contract

**Controller**: `ParticipantController`
**Base Route**: `api/meetings/{meetingId:guid}/participants`
**Auth**: `[Authorize]`

---

## POST /api/meetings/{meetingId}/participants

**Action**: AddParticipant
**Authorization**: Must be meeting Host or CoHost

**Request Body** (`AddParticipantRequest`):
```json
{
  "userId": "guid",
  "meetingRole": "Participant"
}
```

| Field | Type | Required | Validation |
|-------|------|----------|------------|
| userId | Guid | Yes | Must be an active member of the meeting's organization |
| meetingRole | string | Yes | One of: Host, CoHost, Participant, Observer |

**Success Response**: `200 OK`
```json
{
  "id": "guid",
  "meetingId": "guid",
  "userId": "guid",
  "displayName": "Jane Smith",
  "email": "jane@example.com",
  "meetingRole": "Participant",
  "createdAtUtc": "2026-04-08T12:00:00Z"
}
```

**Error Responses**:
- `400 Bad Request` — Invalid role value
- `403 Forbidden` — Caller is not Host or CoHost
- `404 Not Found` — Meeting not found, or user not an org member
- `409 Conflict` — User is already a participant in this meeting

---

## DELETE /api/meetings/{meetingId}/participants/{userId:guid}

**Action**: RemoveParticipant
**Authorization**: Host can remove anyone. CoHost can remove Participants and Observers only.

**Request Body**: None

**Success Response**: `204 No Content`

**Error Responses**:
- `403 Forbidden` — Insufficient role to remove this participant, or removing the last Host
- `404 Not Found` — Meeting not found, or participant not found

**Business Rules**:
- Host can remove any participant (Host, CoHost, Participant, Observer)
- CoHost can only remove Participants and Observers
- The last remaining Host cannot be removed (FR-021)
- Participants and Observers cannot remove anyone

---

## GET /api/meetings/{meetingId}/participants/conflicts

**Action**: CheckConflicts
**Authorization**: Must be meeting Host or CoHost

**Request Body**: None

**Success Response**: `200 OK`
```json
{
  "conflicts": [
    {
      "userId": "guid",
      "displayName": "Jane Smith",
      "conflictingMeetings": [
        {
          "meetingId": "guid",
          "title": "Design Review",
          "scheduledStartUtc": "2026-04-15T09:30:00Z",
          "scheduledEndUtc": "2026-04-15T10:30:00Z"
        }
      ]
    }
  ]
}
```

**Conflict Detection Logic**:
- Checks all participants of the current meeting
- Finds other meetings where each participant is also a participant
- Overlap condition: `otherMeeting.Start < thisMeeting.End AND otherMeeting.End > thisMeeting.Start`
- Excludes meetings with status Cancelled, Completed, or Failed
- Scoped to the active organization only
