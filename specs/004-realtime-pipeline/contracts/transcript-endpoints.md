# Transcript Endpoints Contract

**Controller**: `TranscriptController`
**Base Route**: `api/meetings/{meetingId:guid}/transcript`
**Auth**: `[Authorize]` + `[EnforceOrgAccess]`

---

## GET /api/meetings/{meetingId}/transcript

**Action**: GetTranscript
**Endpoint File**: `GetTranscriptEndpoint.cs`
**Authorization**: Caller MUST be a participant of the meeting (any role, including Observer — see R-013).

**Request**: No body. No query parameters in this phase (R-012 — paging deferred).

**Success Response**: `200 OK` (`TranscriptResponse`)

```json
{
  "meetingId": "b3e4c1a0-0000-0000-0000-000000000001",
  "segments": [
    {
      "id": "...",
      "sequenceNumber": 1,
      "speakerUserId": "u-001",
      "speakerDisplayName": "Alice Doe",
      "text": "Thanks everyone for joining today.",
      "startMs": 1200,
      "endMs": 3500
    },
    {
      "id": "...",
      "sequenceNumber": 2,
      "speakerUserId": null,
      "speakerDisplayName": null,
      "text": "Let's start with the sprint review.",
      "startMs": 3800,
      "endMs": 6200
    }
  ],
  "totalCount": 2
}
```

**Field notes**:

- `segments` is ordered by `sequenceNumber` ascending.
- `speakerUserId` / `speakerDisplayName` are `null` when the platform could not identify the speaker (FR-011, edge case). Clients display these as "Unknown speaker".
- `startMs` / `endMs` are milliseconds from the start of the LiveKit session, not wall-clock time. Wall-clock mapping is a client-side concern and can be computed from the meeting's `InProgress`-transition `SessionEvent.OccurredAtUtc` if needed later.
- If the meeting has no segments yet (e.g., has not started, or the session has started but no speech has been captured), `segments` is `[]` and `totalCount` is `0` — this is **not** an error (FR-013 acceptance scenario 2).
- During an active session, a partial transcript is returned (FR-019 / assumption in spec). Clients expecting "live" captions receive those directly from LiveKit's data channels, not from this endpoint.

**Error Responses**:

- `403 Forbidden` — Caller is not a participant of the meeting (`NotAParticipant`).
- `404 Not Found` — Meeting does not exist in the caller's active organization (tenant isolation; no distinction between "other-org meeting" and "no-such-meeting" to avoid leakage).

**Performance Target**: SC-004 — p95 under 2 seconds for a 1-hour meeting (~10k segments). Query plan: single index scan on `IX_TranscriptSegments_MeetingId_SequenceNumber`; no joins required on the hot path (speaker display names are fetched in a single batched lookup after the primary read).
