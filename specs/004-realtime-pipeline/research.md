# Research: Realtime Session Pipeline (Phase 0)

**Feature**: 004-realtime-pipeline | **Date**: 2026-04-18

## Research Summary

No `NEEDS CLARIFICATION` markers carry over from the spec — the `/speckit.clarify` session on 2026-04-18 resolved the four open questions (participant cap, transcription language, credential TTL, pause authority). This document captures the remaining design decisions and best-practice choices that shape how Phase 4 plugs into the existing codebase and into LiveKit Cloud.

---

## R-001: LiveKit Cloud Integration Shape

**Decision**: Use the official `Livekit.Server.Sdk` NuGet package for two things only: (1) signing `AccessToken` JWTs for participant joins, (2) verifying `Authorization` headers on inbound webhooks. All other interaction with LiveKit Cloud (creating rooms, enabling STT, routing data channels) is configured by **project policy** on the LiveKit Cloud dashboard — not by the backend. Rooms are created on-demand by LiveKit Cloud when the first signed participant joins with that room name.

**Rationale**:
- Keeps the backend deployable as a single unit (Constitution constraint) — no extra .NET worker, no self-hosted SFU.
- The SDK is the only officially-supported way to mint a permission-scoped access token; rolling our own JWT signer here would be an easy place to introduce subtle security bugs.
- Lazy room creation (no explicit `CreateRoom` RPC) is LiveKit's documented pattern for on-demand meetings and avoids a second round-trip when issuing a token.

**Alternatives considered**:
- **Hand-rolled JWT signer.** Rejected: the access-token grant structure (`VideoGrant`, `permissions`, metadata encoding) is LiveKit-specific and changes with SDK versions.
- **Explicit `RoomServiceClient.CreateRoom` before every join.** Rejected: unnecessary round-trip, and rooms auto-expire if no one joins; eager creation gains nothing.

---

## R-002: Room Naming Convention

**Decision**: The LiveKit room name for a meeting is the string `mtg:{MeetingId}`. This value is embedded as the `video.room` grant in every access token issued for that meeting and is the key we expect back in every webhook payload.

**Rationale**:
- Room names are the join key between our backend and LiveKit Cloud — they MUST be deterministic, reversible (`mtg:` prefix → Meeting), and globally unique. A `Guid` alone is enough for uniqueness; the prefix helps operators grep logs and future-proofs us if we ever need a second, non-meeting room type (e.g., `review:{Id}`).
- Using the MeetingId directly avoids a separate `LiveKitRoomId` column on the Meeting entity.

**Alternatives considered**:
- **Use a dedicated `LiveKitRoomId` field on Meeting.** Rejected: extra DB column with no independent lifecycle; anything we could encode there, we can derive from MeetingId.
- **Use the organization's slug as a prefix.** Rejected: exposes org identity in room names that may appear in LiveKit Cloud's logs; tenant metadata belongs in the access-token metadata field, not the room name.

---

## R-003: Role → LiveKit Permissions Mapping

**Decision**: Encode the permission matrix in a single pure function `SessionPermissions.ForRole(MeetingRole)`, which returns a value record of `(CanPublish, CanSubscribe, CanModerate, CanPublishData)`. `ISessionService` calls this function and passes the result to `ILiveKitTokenIssuer`.

| MeetingRole | CanPublish | CanSubscribe | CanModerate | CanPublishData |
|-------------|-----------:|-------------:|------------:|---------------:|
| Host        | yes        | yes          | yes         | yes            |
| CoHost      | yes        | yes          | yes         | yes            |
| Participant | yes        | yes          | no          | yes            |
| Observer    | no         | yes          | no          | no             |

**Rationale**:
- Centralising the mapping in one pure function makes it trivial to unit-test and to audit. `RolePermissionMappingTests` asserts the full matrix.
- `CanPublishData` follows `CanPublish` — no separate knob in this phase — because data-channel publication is used today only for reactions/ad-hoc events, which are aligned with speech-publication rights.
- Observers explicitly get `CanSubscribe = true, CanPublish = false` — this is the subscribe-only contract from FR-002.

**Alternatives considered**:
- **Store a permission bitmask on each MeetingParticipant row.** Rejected: couples persistence to a LiveKit-specific concept; we would have to re-compute anyway if LiveKit adds new grant flags.

---

## R-004: Access Token TTL and Contents

**Decision**: Access tokens are signed with:
- `iss` = LiveKit API key (from config)
- `sub` (identity) = `"user:{UserId}"` — stable across rejoin
- `name` = participant's display name
- `ttl` = **15 minutes** (per Clarification; covers the window between issuance and actually joining; LiveKit Cloud governs the lifetime of an already-joined session)
- `video.room` = `mtg:{MeetingId}`
- `video.canPublish`, `video.canSubscribe`, `video.canPublishData`, `video.roomAdmin` = from `SessionPermissions.ForRole`
- `metadata` (JSON) = `{ "organizationId": "...", "meetingRole": "Host|CoHost|Participant|Observer" }`

**Rationale**:
- The `metadata` field is echoed back in LiveKit's webhooks on `participant_joined` / `participant_left`, which lets `WebhookService` resolve the tenant (`organizationId`) **from the webhook payload itself** rather than trusting the caller — critical for tenant isolation on an unauthenticated endpoint.
- A stable `identity` (`user:{UserId}`) means a participant who briefly disconnects and rejoins shows up as the same LiveKit participant — important for attribution in transcript segments.
- `roomAdmin` is only granted when `CanModerate = true`, so only Hosts / CoHosts can call LiveKit admin RPCs (mute, remove, etc.) from the client.

**Alternatives considered**:
- **Use email as identity.** Rejected: emails are PII and mutable.
- **Encode meeting role in a custom claim outside `metadata`.** Rejected: LiveKit does not echo unknown claims in webhooks; `metadata` is the documented way.

---

## R-005: Webhook Signature Verification

**Decision**: Every `POST /api/webhooks/livekit` request is run through `ILiveKitWebhookValidator.Validate(rawBody, authorizationHeader)` **before any deserialisation side-effect is taken**. The validator uses the LiveKit SDK's `WebhookReceiver.Receive(body, authToken)`, which verifies the JWT in the `Authorization` header using the same API secret used to sign access tokens. Validation failure → `401 Unauthorized` (RFC 7807).

**Rationale**:
- LiveKit Cloud signs webhooks by putting a short-lived JWT in the `Authorization` header whose `sha256` claim matches the raw body. We MUST compute the body hash BEFORE routing logic runs; otherwise ASP.NET model binding can consume the request stream and leave the hash unverifiable.
- The endpoint action therefore reads the raw body once, hands it to the validator, and only after success passes the parsed `WebhookEvent` into `IWebhookService`.
- The endpoint is registered without `[Authorize]` (it's called by LiveKit Cloud, not by our users) but is protected by this signature check instead — the system boundary is the signature validator.

**Alternatives considered**:
- **Authenticate webhooks via mutual TLS.** Rejected: not offered by LiveKit Cloud.
- **IP allowlisting only.** Rejected: not a substitute for a cryptographic signature; LiveKit's egress IP list can change.

---

## R-006: Idempotent Webhook Processing

**Decision**: Every verified webhook event is persisted as a `SessionEvent` row inside the same transaction as any state change it triggers. The row has a unique constraint on `(ExternalEventId)`. Duplicate delivery produces a `23505` (unique_violation) on insert, which `WebhookService` catches and treats as "already processed — return 200 OK with no side effects."

**Event payload processing policies**:
- `room_started` → transition `Meeting.Status` from `Scheduled` to `InProgress`; emit `SessionStartedEvent`; notify org group.
- `room_finished` → transition `Meeting.Status` from `InProgress` to `Completed`; emit `SessionEndedEvent`; notify org group.
- `participant_joined` / `participant_left` → persist event only (attendance derived from query; no denormalised "presence" column in this phase).
- `transcription_segment` (or whatever LiveKit's STT agent emits — see R-007) → insert `TranscriptSegment`, unique on `(MeetingId, SequenceNumber)`; emit `TranscriptSegmentIngestedEvent`; no SignalR broadcast (clients receive live captions directly from LiveKit data channels per R-008).
- `transcription_started` / `transcription_paused` / `transcription_resumed` / `transcription_finished` → persist as SessionEvent; notify org group (FR-024).
- Unknown event types → log at `Warning`, persist as SessionEvent with `EventType = Unknown`, return 200 so LiveKit does not retry forever.

**Rationale**:
- Using the DB unique constraint as the idempotency primitive is both simple and robust — it survives process restarts, concurrent webhook delivery, and retries with no in-memory state.
- Persisting even unknown event types gives us a trail for debugging new LiveKit features without code changes.

**Alternatives considered**:
- **In-memory dedup cache (e.g., `IMemoryCache`).** Rejected: does not survive process restart; LiveKit retries on 5xx and we would double-process.
- **Redis-based dedup set.** Rejected: adds a new infrastructure dependency; the DB already gives us exactly-once semantics at the tx boundary.

---

## R-007: Transcript Segment Source & Shape

**Decision**: Transcript segments arrive via **one of two paths**, both handled by `IWebhookService`:

1. **LiveKit Cloud native transcription events** — If the project enables LiveKit Cloud's built-in transcription (Whisper-based STT agent), transcription segments are delivered as webhook payloads alongside room lifecycle events. This is the preferred path for this phase.
2. **Backend-consumed data-channel relay** — If a native webhook payload is not yet available for transcription at implementation time, the fallback is a small sidecar LiveKit Agent running inside LiveKit Cloud's agent framework that publishes transcription segments as data-channel messages; the backend subscribes via LiveKit egress-to-webhook forwarding.

Either path produces a canonical internal shape:

```json
{
  "meetingRoomName": "mtg:<MeetingId>",
  "segmentId": "<LiveKit-supplied stable id>",
  "sequenceNumber": 42,
  "participantIdentity": "user:<UserId>" | null,
  "text": "…",
  "startMsFromSessionStart": 12345,
  "endMsFromSessionStart": 13500,
  "isFinal": true
}
```

**Rationale**:
- Path 1 keeps the backend the single deployable unit. Path 2 exists only as a contingency if LiveKit's native transcription-webhook payload is not available for the cloud plan / region in use — it still does not require a self-hosted agent (the agent runs inside LiveKit Cloud's Agents Framework).
- Only segments with `isFinal = true` are persisted. Interim/partial segments are discarded — clients receive interim captions directly from LiveKit, and we do not want partials polluting the stored transcript consumed by the Phase 6 AI pipeline.
- `sequenceNumber` is the primary dedup key; `segmentId` is stored alongside it for traceability back to LiveKit.

**Alternatives considered**:
- **Self-hosted .NET transcription agent.** Rejected: violates "single deployable unit" per the updated Phase 4 design.
- **Write-through interim segments (then overwrite on final).** Rejected: doubles write volume and complicates downstream consumers; clients already have the interim caption via data channel.

---

## R-008: Live Caption Delivery to Clients

**Decision**: Clients receive live captions **directly from LiveKit data channels**, not via our backend. The backend does NOT re-broadcast transcript segments over SignalR. The backend's SignalR responsibility is limited to meta-events (session started/ended, transcription paused/resumed, participant joined/left — see R-009). The transcript retrieval endpoint (`GET /api/meetings/{id}/transcript`) is the canonical read path for the stored transcript, usable both post-meeting and during an active session (for late joiners / scrollback).

**Rationale**:
- Relaying each transcript segment through the backend's SignalR adds 100–500ms of round-trip latency and makes the backend a hot path during the meeting — neither matches the performance targets in SC-002.
- LiveKit's data-channel delivery is the natural path; our job is persistence and retrieval, not low-latency fan-out.

**Alternatives considered**:
- **Backend re-broadcasts each segment over SignalR.** Rejected: latency, load, and duplicates the work LiveKit already does.

---

## R-009: SignalR Hub & Tenant-Scoped Groups

**Decision**: Introduce a single `LiveSessionHub` under `Features/LiveSession/Hubs/`. On connect, the hub reads the user's active-organization claim from the JWT and calls `Groups.AddToGroupAsync(ConnectionId, $"org:{organizationId}")`. All notifications flow through `ILiveSessionNotifier`, which wraps `IHubContext<LiveSessionHub>` and only exposes group-scoped send methods — there is no way to call `Clients.All` through the notifier. This enforces the spec's "never broadcast to all clients" constraint at the type level.

**Rationale**:
- The constitution does not specifically mandate a notifier abstraction, but previous phases did not have a SignalR hub at all. Wrapping the hub context in `ILiveSessionNotifier` means services never touch `IHubContext<T>` directly, which (a) makes unit tests trivial (assert on a fake notifier), and (b) removes the temptation to write `Clients.All` in a rush.
- Group membership is derived from the JWT, not from a client-supplied subscription message — clients cannot subscribe themselves to another org's group.

**Alternatives considered**:
- **Per-meeting groups (`mtg:{meetingId}`).** Rejected for this phase: the spec requires org-scoped notifications (User Story 5) — meeting-scoped fan-out can be layered on later if product needs it.

---

## R-010: Meeting Status State-Transition Authority

**Decision**: Only `WebhookService` (in response to verified LiveKit events) transitions meetings from `Scheduled` → `InProgress` and `InProgress` → `Completed`. No user-facing endpoint transitions meeting status in this phase. This closes the state machine that Phase 3 opened.

**Rationale**:
- Matches FR-009 / FR-010 and removes an entire class of race conditions (user manually marking a meeting complete while LiveKit is still sending events).
- Keeps the state machine driven by a single authority (the realtime platform), which is the most conservative design.

**Alternatives considered**:
- **Expose a "force-end meeting" endpoint for Hosts.** Rejected for this phase — not in scope; can be added later if operational need arises.

---

## R-011: Reconciliation for Missed `room_finished` Events

**Decision**: A lightweight reconciliation pass runs hourly via Hangfire: for every meeting in `InProgress` whose `ScheduledEndUtc` is more than 2 hours in the past and which has no `room_finished` SessionEvent, query the LiveKit Cloud room-service API. If the room no longer exists, transition the meeting to `Completed` and synthesize a `SessionEndedEvent` with a `reconciliation = true` flag on the source SessionEvent.

**Rationale**:
- Webhooks are best-effort; LiveKit may deliver a `room_finished` late or not at all in rare failure modes (e.g., extended network partition during room teardown). Without reconciliation, meetings could sit in `InProgress` forever, polluting "upcoming" lists and blocking the Phase 6 post-meeting pipeline.
- 2 hours is a generous buffer above the 1-hour typical meeting cap; keeps reconciliation low-traffic.

**Alternatives considered**:
- **Rely entirely on webhooks (no reconciliation).** Rejected: operationally fragile; one missed event is unrecoverable without manual DB surgery.
- **Reconcile on every transcript-retrieval request.** Rejected: latency-coupling an admin concern to a user-facing read is bad ergonomics.

> Hangfire is already in the dependency list for the Phase 6 post-meeting AI pipeline; using it here does not add a new runtime dependency. If Hangfire is not yet registered at the time this phase ships, the reconciliation job is added as a `BackgroundService` and migrated later.

---

## R-012: Transcript Retrieval Query Shape

**Decision**: `GET /api/meetings/{meetingId}/transcript` returns all stored segments ordered by `(SequenceNumber)` ascending. Paging is NOT applied in this phase — a 1-hour meeting at ~3 segments per second yields ~10,000 rows, well under a 2-second p95 return (SC-004) at the single-index scan the query requires.

**Rationale**:
- Paging adds client complexity and breaks the simplest client (just show the whole transcript) for no real benefit at the scale in scope.
- If a future need arises (e.g., 8-hour training webinars), a cursor parameter can be added without breaking existing clients.

**Alternatives considered**:
- **Offset paging from the start.** Rejected: premature; page 20+ of a transcript is an unusual user action for current meeting lengths.

---

## R-013: Authorization on Transcript Read

**Decision**: `GET /api/meetings/{meetingId}/transcript` allows the request if **(a)** the caller is authenticated, **(b)** tenant isolation (global query filter) locates the meeting in the caller's active organization, AND **(c)** the caller has a `MeetingParticipant` row for the meeting (any role, including Observer).

**Rationale**:
- FR-014 restricts transcript retrieval to participants of the meeting — Observers (who subscribed but did not speak) still count as participants.
- Non-participant org members cannot read, even if they technically have access to the organization — "in the org" is not "in the meeting."

**Alternatives considered**:
- **Allow any org member to read any meeting's transcript.** Rejected: contradicts FR-014 and leaks information from private meetings.

---

## R-014: Pause / Resume Transcription Implementation

**Decision**: `POST /api/meetings/{meetingId}/session/transcription/pause` and `...resume` are thin pass-throughs to the LiveKit admin API (specifically, starting/stopping the transcription agent for the room) via `ILiveKitTokenIssuer` (or a peer `ILiveKitRoomAdmin` service — see below). Authorization is enforced by the backend BEFORE the call: only Hosts and CoHosts of the meeting are allowed. The backend does not store a "transcription is paused" flag — the LiveKit room is the source of truth; the corresponding webhook event is what updates our clients.

**Rationale**:
- Keeping LiveKit Cloud as the source of truth for "is transcription active" avoids a dual-ownership problem where our DB says "paused" but LiveKit is still transcribing (or vice versa).
- Authorization is enforced in our backend (tighter and auditable) even though LiveKit would also reject an invalid admin call — defence in depth.

**Decision refinement**: Introduce a small `ILiveKitRoomAdmin` abstraction alongside `ILiveKitTokenIssuer` so the two responsibilities (token minting vs. room admin) stay separated.

**Alternatives considered**:
- **Store a `TranscriptionPaused` flag on Meeting.** Rejected: dual-ownership with LiveKit; webhooks already give us the state we need.

---

## R-015: `[EnforceOrgAccess]` on the Webhook Endpoint

**Decision**: The webhook controller (`WebhookController`) is NOT decorated with `[Authorize]` or `[EnforceOrgAccess]`. Authorization on this endpoint is provided by `ILiveKitWebhookValidator`. `OrganizationId` is derived inside `WebhookService` by loading the meeting referenced by the webhook's `room.name` (after the `mtg:` prefix strip) and reading its `OrganizationId`.

**Rationale**:
- The webhook's caller is LiveKit Cloud, not an authenticated user. `[Authorize]` would reject every legitimate call. The validator and the server-side meeting lookup together provide the equivalent guarantees: "this event is genuinely from LiveKit" + "this event is for a meeting we know, and here is its org."
- This is explicitly noted in the Constitution Check above so no future reviewer misreads the missing attribute as an oversight.

**Alternatives considered**:
- **Decorate with `[Authorize]` and have LiveKit send a bearer token.** Rejected: LiveKit's webhook auth scheme is a signed JWT in `Authorization`, but that JWT is signed with our webhook secret — not a user identity. Treating it as a user would be a category error.

---

## R-016: Migrations Plan

**Decision**: One EF Core migration `AddLiveSession` creates `TranscriptSegments` and `SessionEvents` tables with their indexes and global query filter registrations. No changes to existing Phase 3 tables.

**Rationale**: Phase 4 is additive — the state-machine transitions it enables were already reserved on `MeetingStatus` in Phase 3. No schema breakage, no data backfill needed.
