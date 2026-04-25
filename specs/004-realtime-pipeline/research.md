# Research: Realtime Session Pipeline (Phase 0)

**Feature**: 004-realtime-pipeline | **Date**: 2026-04-18 | **Last Updated**: 2026-04-21

## Research Summary

No `NEEDS CLARIFICATION` markers remain. The `/speckit.clarify` session on 2026-04-18 resolved participant cap, credential TTL, and language scope. The 2026-04-21 scope revision removed transcript persistence, transcript retrieval, and transcription pause/resume from Phase 4. The 2026-04-21 MVP-simplification revision further reduced the recording pipeline to its minimum viable shape — one entity, one job, one essential webhook, no domain events for recording, and no reconciliation. This document captures the remaining design decisions that shape how Phase 4 plugs into the existing codebase, LiveKit Cloud, and Phase 6.

---

## R-001: LiveKit Cloud Integration Shape

**Decision**: Use the official `Livekit.Server.Sdk` NuGet package for two things: (1) signing `AccessToken` JWTs for participant joins, (2) verifying `Authorization` headers on inbound webhooks. All other interaction with LiveKit Cloud (creating rooms, enabling Egress recording) is configured by **project policy** on the LiveKit Cloud dashboard — not by the backend. Rooms are created on-demand by LiveKit Cloud when the first signed participant joins with that room name.

**Rationale**:
- Keeps the backend deployable as a single unit (Constitution constraint) — no extra .NET worker, no self-hosted SFU, no self-hosted STT.
- The SDK is the only officially-supported way to mint a permission-scoped access token; rolling our own JWT signer here would be an easy place to introduce subtle security bugs.
- Lazy room creation (no explicit `CreateRoom` RPC) is LiveKit's documented pattern for on-demand meetings.

---

## R-002: Room Naming Convention

**Decision**: The LiveKit room name for a meeting is the string `mtg:{MeetingId}`. This value is embedded as the `video.room` grant in every access token issued for that meeting and is the key we expect back in every webhook payload.

**Rationale**:
- Room names are the join key between our backend and LiveKit Cloud — they MUST be deterministic, reversible (`mtg:` prefix → Meeting), and globally unique.
- The same room name is echoed in Egress webhook payloads, so recording-lifecycle events map to meetings by the same convention.

---

## R-003: Role → LiveKit Permissions Mapping

**Decision**: Encode the permission matrix in a single pure function `SessionPermissions.ForRole(MeetingRole)`, returning `(CanPublish, CanSubscribe, CanModerate, CanPublishData)`.

| MeetingRole | CanPublish | CanSubscribe | CanModerate | CanPublishData |
|-------------|-----------:|-------------:|------------:|---------------:|
| Host        | yes        | yes          | yes         | yes            |
| CoHost      | yes        | yes          | yes         | yes            |
| Participant | yes        | yes          | no          | yes            |
| Observer    | no         | yes          | no          | no             |

**Rationale**: Centralising the mapping in one pure function makes it trivial to unit-test and audit. Observers subscribe but cannot publish, matching FR-002.

---

## R-004: Access Token TTL and Contents

**Decision**: Access tokens are signed with:
- `iss` = LiveKit API key (from config)
- `sub` (identity) = `"user:{UserId}"` — stable across rejoin
- `name` = participant's display name
- `ttl` = **15 minutes** (per Clarification)
- `video.room` = `mtg:{MeetingId}`
- `video.canPublish`, `video.canSubscribe`, `video.canPublishData`, `video.roomAdmin` = from `SessionPermissions.ForRole`
- `metadata` (JSON) = `{ "organizationId": "...", "meetingRole": "Host|CoHost|Participant|Observer" }`

**Rationale**: The `metadata` field is echoed in LiveKit webhooks on `participant_joined`/`participant_left`, letting `WebhookService` resolve the tenant from the payload itself — critical for tenant isolation on an unauthenticated endpoint.

---

## R-005: Webhook Signature Verification

**Decision**: Every `POST /api/webhooks/livekit` request is run through `ILiveKitWebhookValidator.Validate(rawBody, authorizationHeader)` **before any deserialisation side-effect is taken**. The validator wraps the SDK's `WebhookReceiver.Receive(body, authToken)`. Validation failure → `401 Unauthorized` (RFC 7807).

**Rationale**: The webhook body must be hashed before model binding consumes the request stream; signature verification is the system boundary for this unauthenticated endpoint.

---

## R-006: Idempotent Webhook Processing (REVISED 2026-04-21 MVP)

**Decision**: Two DB uniqueness primitives carry the idempotency guarantee; no in-memory dedup, no cross-phase event bus.

1. **`SessionEvents.ExternalEventId` unique** — duplicate webhook deliveries hit a `23505` unique-violation on insert. `WebhookService` catches it and treats the event as already processed (200 OK, no side effects).
2. **`Recordings.MeetingId` unique** — duplicate `egress_ended` deliveries for the same meeting cannot create a second `Recording` row. The download job additionally guards by reading `Recording.Status` at start and exiting early if it is already `Completed` or `Failed`.

**Event payload processing policies** (MVP shape):

- `room_started` → transition `Meeting.Status` `Scheduled → InProgress`; publish `SessionStartedEvent` (MediatR); notify org group (`session.started`).
- `room_finished` → transition `InProgress → Completed`; publish `SessionEndedEvent`; notify org group (`session.ended`).
- `participant_joined` / `participant_left` → persist `SessionEvent`; notify org group.
- `egress_ended` (success variant) — **the primary recording trigger**: upsert `Recording(MeetingId, Status=Pending)`; enqueue `DownloadRecordingJob` with the MinIO target key and the source cloud URL from the payload.
- `egress_ended` (failure variant): upsert `Recording(MeetingId, Status=Failed)`; no download job enqueued.
- `recording_started` *(optional)*: if the handler is enabled, upsert `Recording(MeetingId, Status=Pending)` for audit. If not, the event is persisted as `Unknown` and ignored — the primary flow still works from `egress_ended`.
- Unknown event types → log at `Warning`, persist with `EventType = Unknown`, return 200.

> **Removed from policy** (2026-04-21): `transcription_*` handlers (backend is not on the caption path); a separate `recording_failed` handler (folded into the `egress_ended` failure variant).

**Rationale**: Two DB uniques + one status guard is the simplest shape that still produces exactly-once-in-practice behaviour across webhook retries, job retries, and process restarts. No extra state machine to maintain.

---

## R-007: Recording Handoff to Phase 6 (REVISED 2026-04-21 MVP)

**Decision**: Phase 4 hands the finished recording to Phase 6 through **MinIO and nothing else**. The pipeline is:

```
LiveKit Cloud Egress records the session
  → sends egress_ended webhook to /api/webhooks/livekit
  → WebhookService upserts Recording(MeetingId, Status=Pending)
  → WebhookService enqueues DownloadRecordingJob(meetingId, sourceCloudUrl)
  → DownloadRecordingJob downloads from cloud URL, uploads to MinIO at recordings/{MeetingId}.mp4
  → DownloadRecordingJob sets Recording.FilePath + Status=Completed
Phase 6 (independent trigger) reads Recording.FilePath, fetches bytes from MinIO, runs WhisperX.
```

**Rationale**:
- Aligns with the implementation plan's explicit split: Phase 4 = realtime, Phase 6 = post-meeting AI.
- **MinIO is the integration boundary.** Phase 6 does not subscribe to a domain event; it reads the `Recording` row and the object. No event bus spans the two phases.
- The deterministic object key `recordings/{MeetingId}.mp4` means any rare double-write by a retried job produces one object, not two.
- `Recordings.MeetingId` unique + `Status` guard make the handoff exactly-once in practice; no domain event is needed to communicate completion.

**Alternatives considered**:
- **Publish `RecordingAvailableEvent` / `RecordingFailedEvent` MediatR notifications for Phase 6 to subscribe to.** Rejected in the MVP revision — Phase 6's trigger should be owned by Phase 6 (scheduled scan, meeting-completion flow, or similar). An event published from Phase 4 is coupling we don't need.
- **Have Phase 6 poll LiveKit Cloud for completed recordings.** Rejected: the platform already delivers `egress_ended`; we should not poll.
- **Have LiveKit Egress write directly to MinIO.** Rejected (as in Phase 5 research): LiveKit Cloud cannot reach a Docker-local MinIO instance without public exposure.

---

## R-008: Live Caption Delivery to Clients (REVISED 2026-04-21)

**Decision**: Clients receive live captions **directly from LiveKit data channels**. The backend is not on the caption path at any point — it does not receive, relay, buffer, store, or process realtime caption text. There is no backend endpoint, service, or background task in Phase 4 that participates in live caption delivery.

**Rationale**:
- Shortest possible latency — clients render captions as the realtime platform produces them, no backend round-trip.
- Eliminates the `TranscriptSegment` entity, the transcription-segment webhook handler, dedup logic, transcript retrieval, and pause/resume endpoints.
- The authoritative / archived transcript is produced in Phase 6 from the downloaded recording.

**Alternatives considered**:
- **Backend ingests `transcription_segment` webhooks and persists them.** Rejected — duplicates work the realtime platform already does, adds storage and latency cost, overlaps with Phase 6.
- **Backend re-broadcasts captions over SignalR.** Rejected — puts the backend on a high-throughput hot path for no added value.

---

## R-009: SignalR Hub & Tenant-Scoped Groups (REVISED 2026-04-21 MVP)

**Decision**: Introduce a single `LiveSessionHub` under `Features/LiveSession/Hubs/`. On connect, the hub reads the user's active-organization claim from the JWT and calls `Groups.AddToGroupAsync(ConnectionId, $"org:{organizationId}")`. All notifications flow through `ILiveSessionNotifier`, which wraps `IHubContext<LiveSessionHub>` and only exposes group-scoped send methods — there is no way to call `Clients.All` through the notifier.

**Notifier methods** in this phase (MVP):

- `NotifySessionStartedAsync`
- `NotifySessionEndedAsync`
- `NotifyParticipantJoinedAsync`
- `NotifyParticipantLeftAsync`

> No `NotifyRecording*` methods. Recording progress is not broadcast to clients in this phase — Phase 6 is the consumer and it reads MinIO directly. Clients that need recording availability in UI can query Phase 6's read API when it exists.

**Rationale**: Wrapping the hub context removes the temptation to write `Clients.All` in a rush; the type does not expose it. Restricting the notifier surface to session lifecycle keeps the MVP aligned with the "MinIO is the integration boundary" principle.

---

## R-010: Meeting Status State-Transition Authority

**Decision**: Only `WebhookService` (in response to verified LiveKit events) transitions meetings from `Scheduled` → `InProgress` and `InProgress` → `Completed`. No user-facing endpoint transitions meeting status in this phase.

**Rationale**: Matches FR-009 / FR-010 and removes an entire class of race conditions. Keeps the state machine driven by a single authority.

---

## R-011: [REMOVED 2026-04-21 MVP]

Reconciliation for missed `room_finished` / `egress_ended` events is **not** a Phase 4 responsibility in the MVP. A meeting whose `room_finished` never arrives will remain `InProgress` until an operator intervenes; a meeting whose `egress_ended` never arrives will have no `Recording` row and Phase 6 will skip it. Both are acceptable MVP behaviours. This entry is retained as a forwarding marker in case reconciliation returns as a post-MVP hardening item.

---

## R-012: [REMOVED 2026-04-21]

Transcript retrieval query shape is no longer a Phase 4 concern. Any transcript read API belongs to Phase 6. This entry is retained as a forwarding marker.

---

## R-013: [REMOVED 2026-04-21]

Authorization on transcript read is no longer a Phase 4 concern. Any transcript read API belongs to Phase 6. This entry is retained as a forwarding marker.

---

## R-014: [REMOVED 2026-04-21]

Pause / resume transcription endpoints have been removed from Phase 4. Live captions are delivered by the realtime platform directly to clients; the backend does not participate.

---

## R-015: `[EnforceOrgAccess]` on the Webhook Endpoint

**Decision**: The webhook controller (`WebhookController`) is NOT decorated with `[Authorize]` or `[EnforceOrgAccess]`. Authorization on this endpoint is provided by `ILiveKitWebhookValidator`. `OrganizationId` is derived inside `WebhookService` by loading the meeting referenced by the webhook's `room.name` (after the `mtg:` prefix strip) and reading its `OrganizationId`.

**Rationale**: The webhook's caller is LiveKit Cloud, not an authenticated user. The validator and the server-side meeting lookup together provide the equivalent guarantees: "this event is genuinely from LiveKit" + "this event is for a meeting we know, and here is its org."

---

## R-016: Migrations Plan (REVISED 2026-04-21 MVP)

**Decision**: One EF Core migration `AddLiveSession` creates `SessionEvents` and `Recordings` tables with their indexes and global query filter registrations. No changes to existing Phase 3 tables.

`Recordings` columns (MVP): `Id`, `MeetingId` (unique), `OrganizationId`, `FilePath` (nullable until download completes), `Status`, `CreatedAtUtc`, `UpdatedAtUtc`.

> **Not in the MVP migration**: `TranscriptSegments` (removed in the scope revision); a wider `RecordingAsset` shape with `CloudStorageUrl`, `SizeBytes`, `DurationMs`, `ExternalEgressId`, `FailureReason`, `CompletedAtUtc` (collapsed to the minimum shape above).

**Rationale**: Phase 4 is additive — the state-machine transitions it enables were already reserved on `MeetingStatus` in Phase 3. No schema breakage, no data backfill needed. Implementations may add helpful columns later (e.g., a second unique index on an external egress id) without spec changes.

---

## R-017: Recording Download Job — Resiliency & Idempotency (REVISED 2026-04-21 MVP)

**Decision**: `DownloadRecordingJob` is a single Hangfire background job with the following properties:

- **Input**: `(MeetingId, sourceCloudUrl)`. The job re-reads the `Recording` row keyed by `MeetingId` on each attempt.
- **Exit-early guard**: if `Recording.Status ∈ {Completed, Failed}`, return immediately (idempotent re-entry).
- **Target key**: deterministic — `recordings/{MeetingId}.mp4`. On double-write, the later PUT replaces the earlier — still one object in MinIO.
- **Transitions**: stays at `Pending` while downloading; on success sets `FilePath = recordings/{MeetingId}.mp4`, `Status = Completed`; on terminal failure sets `Status = Failed`.
- **Retries**: Hangfire's default exponential backoff with a bounded max attempts (default 5). Transient HTTP/network failures are retried; permanent failures (404 on cloud URL, signature mismatch) go straight to `Failed`.
- **Storage**: Uploads to MinIO via an `IStorageService` abstraction (PutObject from URL or stream). Multipart upload for files > 64 MB is an implementation detail of the storage wrapper.
- **Side-effect ordering**: MinIO write first, then DB update to `Completed` + `FilePath`. On failure between steps, the deterministic key means idempotent re-entry produces exactly one object + one `Completed` row.
- **No MediatR publication on success or failure.** Phase 6 discovers completed recordings through the `Recording` row + the MinIO object, not through an event.

**Rationale**: The handoff MUST be exactly-once in practice from Phase 6's perspective. Two DB uniques (`SessionEvents.ExternalEventId`, `Recordings.MeetingId`), the `Status` guard, and the deterministic object key together cover webhook retries, job retries, and process restarts — without an event bus.

**Alternatives considered**:
- **Download synchronously inside the webhook handler.** Rejected: webhooks must return quickly (< 5s); downloading a 1-hour recording does not fit.
- **Publish `RecordingAvailableEvent` so Phase 6 can subscribe.** Rejected in the MVP — Phase 6 owns its own trigger. A Phase 4 → Phase 6 event adds coupling and an extra failure mode.
