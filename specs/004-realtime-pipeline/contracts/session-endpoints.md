# Session Endpoints Contract

**Controller**: `SessionController`
**Base Route**: `api/meetings/{meetingId:guid}/session`
**Auth**: `[Authorize]` + `[EnforceOrgAccess]`

All routes on this controller require the caller to be authenticated AND to be a participant of the meeting identified by `{meetingId}`. Non-participants receive `403 Forbidden` regardless of action (RFC 7807 `LiveSessionErrors.NotAParticipant`).

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

---

## POST /api/meetings/{meetingId}/session/transcription/pause

**Action**: PauseTranscription
**Endpoint File**: `PauseTranscriptionEndpoint.cs`
**Authorization**: Caller MUST be a participant AND have `MeetingRole ∈ {Host, CoHost}` for this meeting (FR-023).

**Request Body**: None

**Success Response**: `204 No Content`

**Error Responses**:

- `403 Forbidden` — Caller is a Participant or Observer (`ForbiddenForRole`), or not a participant of the meeting.
- `404 Not Found` — Meeting does not exist in the caller's active organization.
- `409 Conflict` — Meeting is not currently `InProgress` (`TranscriptionNotPausable`), or transcription is already paused on the platform side.
- `502 Bad Gateway` — LiveKit admin call failed; caller may retry.

**Side Effects**:

- Calls LiveKit admin API to pause the transcription agent in room `mtg:{meetingId}`.
- The corresponding `TranscriptionPaused` webhook arrives asynchronously; that webhook is what triggers the `ILiveSessionNotifier.NotifyTranscriptionStateChangedAsync` call to connected clients (NOT this endpoint — keeps the source of truth single).

---

## POST /api/meetings/{meetingId}/session/transcription/resume

**Action**: ResumeTranscription
**Endpoint File**: `ResumeTranscriptionEndpoint.cs`
**Authorization**: Same as `pause` — Host or CoHost only.

**Request Body**: None

**Success Response**: `204 No Content`

**Error Responses**:

- `403 Forbidden` — Same conditions as `pause`.
- `404 Not Found` — Meeting does not exist in the caller's active organization.
- `409 Conflict` — Meeting is not currently `InProgress`, or transcription is not currently paused (`TranscriptionNotResumable`).
- `502 Bad Gateway` — LiveKit admin call failed.

**Side Effects**: Mirror of `pause`. Resumes the LiveKit transcription agent. Client notification flows via the `TranscriptionResumed` webhook.
