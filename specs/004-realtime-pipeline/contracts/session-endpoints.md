# Session Endpoints Contract

**Controller**: `SessionController`
**Base Route**: `api/meetings/{meetingId:guid}/session`
**Auth**: `[Authorize]` + `[EnforceOrgAccess]`

All routes on this controller require the caller to be authenticated AND to be a participant of the meeting identified by `{meetingId}`. Non-participants receive `403 Forbidden` regardless of action (RFC 7807 `LiveSessionErrors.NotAParticipant`).

> **Revision note (2026-04-21)**: The `POST /transcription/pause` and `POST /transcription/resume` endpoints have been **removed** from Phase 4. Realtime transcription is no longer a backend concern — live captions are delivered directly from the realtime platform to clients, and the authoritative transcript is produced in Phase 6. Pause/resume of client-side caption rendering is a client/platform concern.

---

## POST /api/meetings/{meetingId}/session/join-token

**Action**: GetJoinToken
**Endpoint File**: `GetJoinTokenEndpoint.cs`
**Authorization**: Caller MUST be a participant of the meeting. Meeting status MUST NOT be `Cancelled` or `Completed`.

**Request Body** (`JoinTokenRequest`):

```json
{
  "displayName": "Alice Doe"
}
```

| Field | Type | Required | Validation |
|-------|------|----------|------------|
| displayName | string | No | 1–120 chars if provided. If omitted, the backend uses the caller's profile display name. |

**Success Response**: `200 OK` (`JoinTokenResponse`)

```json
{
  "accessToken": "<signed LiveKit JWT>",
  "roomName": "mtg:b3e4c1a0-0000-0000-0000-000000000001",
  "serverUrl": "wss://meetingassistant.livekit.cloud",
  "expiresAtUtc": "2026-04-18T14:15:00Z",
  "permissions": {
    "canPublish": true,
    "canSubscribe": true,
    "canModerate": false,
    "canPublishData": true
  }
}
```

**Error Responses**:

- `400 Bad Request` — `displayName` validation failure.
- `403 Forbidden` — Caller is not a participant (`NotAParticipant`), or caller's active organization does not match the meeting's organization.
- `404 Not Found` — Meeting does not exist in the caller's active organization (tenant isolation).
- `409 Conflict` — Meeting is `Cancelled` or `Completed` (`MeetingNotJoinable`).

**Side Effects**: None persisted. Token issuance is logged (structured log) with `(meetingId, userId, roleAtIssuance, expiresAtUtc)` for audit.

**Performance Target**: SC-001 — p95 under 1 second.
