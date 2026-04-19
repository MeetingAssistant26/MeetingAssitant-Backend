# Webhook Endpoints Contract

**Controller**: `WebhookController`
**Base Route**: `api/webhooks`
**Auth**: **No `[Authorize]`.** Signature-verified by `ILiveKitWebhookValidator` (see R-005). The controller is unauthenticated at the ASP.NET pipeline level because its caller is LiveKit Cloud, not a backend user. Authorization is enforced by cryptographic signature validation performed inside the endpoint.

This endpoint is the single ingress point for realtime platform events (room lifecycle + transcription). Tenant resolution (`OrganizationId`) is derived from the meeting identified by the payload's `room.name` AFTER signature verification.

---

## POST /api/webhooks/livekit

**Action**: LiveKitWebhook
**Endpoint File**: `LiveKitWebhookEndpoint.cs`

**Request Headers**:

| Header | Required | Description |
|--------|----------|-------------|
| `Authorization` | Yes | JWT signed with the shared webhook secret; contains a `sha256` claim over the raw body. LiveKit's `WebhookReceiver.Receive` verifies both the signature and the body hash. |
| `Content-Type` | Yes | `application/webhook+json` (or `application/json` — accept either) |

**Request Body**: LiveKit's webhook event envelope. The following event types are recognised; any others are persisted as `SessionEventType.Unknown` (forward-compatibility).

| `event` | Handler | Backend effect |
|---------|---------|----------------|
| `room_started` | `HandleRoomStarted` | Transition referenced meeting `Scheduled → InProgress`. Emit `SessionStartedEvent`. Notify org group (`session.started`). |
| `room_finished` | `HandleRoomFinished` | Transition referenced meeting `InProgress → Completed`. Emit `SessionEndedEvent`. Notify org group (`session.ended`). |
| `participant_joined` | `HandleParticipantJoined` | Persist `SessionEvent` with `EventType = ParticipantJoined` and `ParticipantUserId` resolved from the participant's `user:{UserId}` identity. Notify org group (`participant.joined`). |
| `participant_left` | `HandleParticipantLeft` | Persist `SessionEvent` with `EventType = ParticipantLeft`. Notify org group (`participant.left`). |
| `transcription_started` | `HandleTranscriptionStarted` | Persist `SessionEvent`. Notify org group (`transcription.started`). |
| `transcription_segment` (or equivalent LiveKit payload per R-007) | `HandleTranscriptionSegment` | If `isFinal = true`, insert `TranscriptSegment`; else discard. Emit `TranscriptSegmentIngestedEvent`. No SignalR broadcast for individual segments (R-008). |
| `transcription_paused` | `HandleTranscriptionPaused` | Persist `SessionEvent`. Notify org group (`transcription.paused`, `{ actingUserId }`) per FR-024. |
| `transcription_resumed` | `HandleTranscriptionResumed` | Persist `SessionEvent`. Notify org group (`transcription.resumed`). |
| `transcription_finished` | `HandleTranscriptionFinished` | Persist `SessionEvent`. No further action — `room_finished` still governs meeting completion. |
| _anything else_ | `HandleUnknown` | Persist `SessionEvent` with `EventType = Unknown`. Log a Warning. Return 200 so LiveKit does not retry indefinitely. |

**Body example** (`room_started`):

```json
{
  "event": "room_started",
  "id": "EV_01HXYZABC",
  "createdAt": 1713445200,
  "room": {
    "name": "mtg:b3e4c1a0-0000-0000-0000-000000000001",
    "sid": "RM_...",
    "creationTime": 1713445200
  }
}
```

**Success Response**: `200 OK` (empty body). Returned for:

- Successfully processed new events (first-time delivery).
- Duplicate events (`ExternalEventId` already present) — treated as idempotent no-op per R-006.
- Events referencing an unknown meeting / orphaned room — logged at `Warning` and returned 200 to prevent retries (edge case in spec: "The event is acknowledged to prevent retries but not processed").
- Unknown event types — persisted with `EventType = Unknown`, returned 200.

**Error Responses**:

- `401 Unauthorized` — Signature validation failed (missing `Authorization` header, invalid JWT, body hash mismatch). RFC 7807 `LiveSessionErrors.InvalidWebhookSignature`.
- `400 Bad Request` — Payload is well-signed but structurally un-parseable (malformed JSON after signature check). Should essentially never happen in practice — kept as a diagnostic fallback.

**Error responses from the endpoint NEVER leak internal state** — even when rejecting, the response body is a generic RFC 7807 problem detail; no hint of whether the referenced room exists.

---

## Notes on Idempotency

All handlers wrap persistence in a single DB transaction that also inserts the `SessionEvent` row with unique `ExternalEventId`. On `23505` unique-violation, the transaction is rolled back and the outer handler returns `200 OK` without performing the side effect (no duplicate status transition, no duplicate SignalR notification, no duplicate domain event).

## Notes on Ordering

LiveKit does NOT guarantee webhook delivery order. Handlers are written to tolerate out-of-order delivery:

- `room_finished` arriving before `room_started`: still transitions meeting to `Completed`; a later `room_started` is a no-op because the status is no longer `Scheduled`.
- `participant_left` before `participant_joined`: both rows still persist; attendance queries already tolerate this by pairing (join, left) on `ParticipantUserId` and taking the earliest/latest respectively.
- `transcription_segment` ordering by arrival is NOT trusted — the `SequenceNumber` field determines stored order on read.
