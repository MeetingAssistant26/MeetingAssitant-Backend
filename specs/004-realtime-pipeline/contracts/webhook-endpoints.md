# Webhook Endpoints Contract

**Controller**: `WebhookController`
**Base Route**: `api/webhooks`
**Auth**: **No `[Authorize]`.** Signature-verified by `ILiveKitWebhookValidator` (see R-005). The controller is unauthenticated at the ASP.NET pipeline level because its caller is LiveKit Cloud, not a backend user. Authorization is enforced by cryptographic signature validation performed inside the endpoint.

This endpoint is the single ingress point for realtime platform events (room lifecycle + recording). Tenant resolution (`OrganizationId`) is derived from the meeting identified by the payload's `room.name` AFTER signature verification.

> **Revision note (2026-04-21)**: The recording pipeline has been simplified to the MVP minimum. The essential recording webhook is **`egress_ended`**. `recording_started` is optional (may be handled to pre-create a `Pending` row; safe to ignore otherwise). All transcription-related handlers remain removed.

---

## POST /api/webhooks/livekit

**Action**: LiveKitWebhook
**Endpoint File**: `LiveKitWebhookEndpoint.cs`

**Request Headers**:

| Header | Required | Description |
|--------|----------|-------------|
| `Authorization` | Yes | JWT signed with the shared webhook secret; contains a `sha256` claim over the raw body. |
| `Content-Type` | Yes | `application/webhook+json` (or `application/json`) |

**Request Body**: LiveKit's webhook event envelope. The following event types are recognised.

| `event` | Handler | Backend effect |
|---------|---------|----------------|
| `room_started` | `HandleRoomStarted` | Transition referenced meeting `Scheduled → InProgress`. Publish `SessionStartedEvent` (MediatR). Notify org group (`session.started`). |
| `room_finished` | `HandleRoomFinished` | Transition referenced meeting `InProgress → Completed`. Publish `SessionEndedEvent` (MediatR). Notify org group (`session.ended`). |
| `participant_joined` | `HandleParticipantJoined` | Persist `SessionEvent` with `ParticipantUserId` resolved from `user:{UserId}`. Notify org group (`participant.joined`). |
| `participant_left` | `HandleParticipantLeft` | Persist `SessionEvent`. Notify org group (`participant.left`). |
| `egress_ended` (success variant) | `HandleEgressEnded` | **Primary recording trigger.** Persist `SessionEvent`. Upsert `Recording(MeetingId, Status=Pending)`. Enqueue `DownloadRecordingJob` with the MinIO target key and the source cloud URL from the payload. |
| `egress_ended` (failure variant) | `HandleEgressEnded` | Persist `SessionEvent`. Upsert `Recording(MeetingId, Status=Failed)`. No download job enqueued. |
| `recording_started` *(optional)* | `HandleRecordingStarted` | Optional. If enabled, upsert `Recording(MeetingId, Status=Pending)` for audit. If not implemented, the event is persisted as `Unknown` and ignored — the primary flow still works from `egress_ended`. |
| _anything else_ | `HandleUnknown` | Persist `SessionEvent` with `EventType = Unknown`. Log a Warning. Return 200. |

> **Removed handlers**: `transcription_segment`, `transcription_started`, `transcription_paused`, `transcription_resumed`, `transcription_finished`, `recording_failed` (absorbed into the failure variant of `egress_ended`). The backend does not participate in realtime transcription, and it does not subscribe to events that do not feed the single `egress_ended → DownloadRecordingJob → MinIO` flow.

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

**Body example** (`egress_ended`, success):

```json
{
  "event": "egress_ended",
  "id": "EV_01HXYZDEF",
  "createdAt": 1713449000,
  "egressInfo": {
    "egressId": "EG_01HXYZGHI",
    "roomName": "mtg:b3e4c1a0-0000-0000-0000-000000000001",
    "status": "EGRESS_COMPLETE",
    "fileResults": [
      {
        "filename": "mtg-b3e4c1a0-.../composite.mp4",
        "location": "https://livekit-cloud-recordings.example/.../composite.mp4"
      }
    ]
  }
}
```

**Success Response**: `200 OK` (empty body). Returned for:

- Successfully processed new events (first-time delivery).
- Duplicate events (`ExternalEventId` already present, or `Recording.MeetingId` already created) — treated as idempotent no-ops.
- Events referencing an unknown meeting / orphaned room — logged at `Warning` and returned 200 to prevent retries (FR-020).
- Unknown event types — persisted with `EventType = Unknown`, returned 200.

**Error Responses**:

- `401 Unauthorized` — Signature validation failed. RFC 7807 `LiveSessionErrors.InvalidWebhookSignature`.
- `400 Bad Request` — Payload is well-signed but structurally un-parseable. Diagnostic fallback; should essentially never happen.

---

## Idempotency (MVP shape)

Two primitives together keep the pipeline exactly-once in practice:

1. **`SessionEvents.ExternalEventId` unique** — duplicate webhook deliveries produce a unique-violation, the handler treats it as a no-op.
2. **`Recordings.MeetingId` unique** — duplicate `egress_ended` deliveries for the same meeting cannot create a second `Recording` row. The download job additionally checks `Recording.Status` at start and exits early if the row is already `Completed` or `Failed`.

No cross-phase event bus, no reconciliation sweep. If either primitive is somehow bypassed (e.g., MinIO upload ran twice within one job attempt), the object key is deterministic (`recordings/{MeetingId}.mp4` — see the download job notes in `quickstart.md`), so the later write wins and replaces the earlier one — still one object in MinIO.

## Ordering

LiveKit does NOT guarantee webhook delivery order. Handlers are tolerant:

- `room_finished` before `room_started`: transitions straight to `Completed`; later `room_started` is a no-op.
- `participant_left` before `participant_joined`: both rows persist; attendance queries pair them by `ParticipantUserId`.
- `egress_ended` before `room_finished`: recording download proceeds regardless of meeting status.
