# Data Model: Realtime Session Pipeline

**Feature**: 004-realtime-pipeline | **Date**: 2026-04-18

## Entities

### TranscriptSegment

A single unit of transcribed speech captured during a live meeting session. One row per finalized segment delivered by the realtime platform's STT.

| Field | Type | Constraints | Notes |
|-------|------|-------------|-------|
| Id | Guid | PK, auto-generated | Inherited from `BaseEntity` |
| MeetingId | Guid | FK to `Meeting`, required, indexed | Cascade not set (soft isolation) |
| OrganizationId | Guid | FK to `Organization`, required | `IHasOrganizationId` — tenant isolation via global query filter |
| SpeakerUserId | Guid? | FK to `ApplicationUser`, nullable | Null when platform cannot identify the speaker (FR-011, edge case) |
| Text | string | Required, max 8000 chars | The final transcribed utterance. Stored as PostgreSQL `text` (no length cap at DB, app-level cap) |
| StartMs | long | Required | Milliseconds from session start (monotonic, supplied by platform) |
| EndMs | long | Required | Milliseconds from session start; MUST be `>= StartMs` |
| SequenceNumber | long | Required | Platform-supplied monotonic sequence within the meeting; dedup key |
| ExternalSegmentId | string | Required, max 100 chars | Platform's stable identifier for the segment; for traceability |
| CreatedAtUtc | DateTime | Auto-set | Inherited from `BaseEntity`; time the backend persisted the row |
| UpdatedAtUtc | DateTime | Auto-set | Inherited from `BaseEntity` |

**Relationships**:

- Belongs to one `Meeting` (via `MeetingId`)
- Belongs to one `Organization` (via `OrganizationId`)
- Optionally belongs to one `ApplicationUser` (via `SpeakerUserId`)

**Indexes**:

- `IX_TranscriptSegments_MeetingId_SequenceNumber` on `(MeetingId, SequenceNumber)` — **UNIQUE**. Primary read path (transcript retrieval ordered by sequence) AND dedup primitive for idempotent ingestion (R-006).
- `IX_TranscriptSegments_OrganizationId` on `OrganizationId` — required by the global query filter.

**Constraints**:

- Unique `(MeetingId, SequenceNumber)` — prevents duplicate storage of the same segment; unique-violation on insert is the idempotency signal per R-006.
- Check constraint: `EndMs >= StartMs`.

**Notes**:

- Only segments with `IsFinal = true` at the platform layer are ever inserted (R-007). Interim segments are discarded by `WebhookService`.
- `Text` is kept as-is (platform-provided casing and punctuation). Normalisation is a Phase 6 concern.

---

### SessionEvent

A verified event delivered by the realtime platform. Persisted for (a) idempotency, (b) deriving participant attendance, (c) operational audit. One row per unique platform-side event id.

| Field | Type | Constraints | Notes |
|-------|------|-------------|-------|
| Id | Guid | PK, auto-generated | Inherited from `BaseEntity` |
| MeetingId | Guid | FK to `Meeting`, required, indexed | Resolved from the room name in the webhook payload |
| OrganizationId | Guid | FK to `Organization`, required | `IHasOrganizationId` — tenant isolation via global query filter |
| ExternalEventId | string | Required, max 100 chars, **UNIQUE** | Platform-supplied event id; idempotency primitive |
| EventType | SessionEventType (enum) | Required | See enum below |
| ParticipantUserId | Guid? | FK to `ApplicationUser`, nullable | Only set for `ParticipantJoined` / `ParticipantLeft` events; resolved from the participant identity (`user:{UserId}`) in the payload (R-004) |
| PayloadJson | string | Required | Raw webhook payload, stored as `jsonb`, for debugging / replay / future schema needs |
| OccurredAtUtc | DateTime | Required | Time the event occurred at the platform (from payload) |
| ProcessedAtUtc | DateTime | Required | Time the backend accepted and persisted the event |
| IsReconciliation | bool | Required, default `false` | `true` when the event was synthesised by the R-011 reconciliation job, not received via webhook |

**Relationships**:

- Belongs to one `Meeting` (via `MeetingId`)
- Belongs to one `Organization` (via `OrganizationId`)
- Optionally belongs to one `ApplicationUser` (via `ParticipantUserId`)

**Indexes**:

- `IX_SessionEvents_ExternalEventId` on `ExternalEventId` — **UNIQUE**. The core idempotency primitive (R-006).
- `IX_SessionEvents_MeetingId_OccurredAtUtc` on `(MeetingId, OccurredAtUtc)` — for attendance and timeline queries.
- `IX_SessionEvents_OrganizationId` on `OrganizationId` — required by the global query filter.

**Constraints**:

- Unique `ExternalEventId` (across the whole table — event ids are globally unique on LiveKit Cloud's side).

---

## Enums

### SessionEventType

```text
RoomStarted              = 0
RoomFinished             = 1
ParticipantJoined        = 2
ParticipantLeft          = 3
TranscriptionStarted     = 4
TranscriptionPaused      = 5
TranscriptionResumed     = 6
TranscriptionFinished    = 7
TranscriptSegmentStored  = 8   // shadow record when a TranscriptSegment was inserted; convenient for timeline queries
Unknown                  = 99  // event types not yet recognised — persisted for forward-compatibility
```

### TranscriptionState

```text
Started  = 0
Paused   = 1
Resumed  = 2
Finished = 3
```

Carried on `TranscriptionStateChangedEvent` (MediatR) and `ILiveSessionNotifier.NotifyTranscriptionStateChangedAsync`. Not persisted as a column — derived from the most recent `TranscriptionPaused` / `TranscriptionResumed` / `TranscriptionStarted` / `TranscriptionFinished` row in `SessionEvents` if ever needed for retrospective queries.

---

## Value Objects (Not Persisted)

### SessionPermissions

A pure value record returned by `SessionPermissions.ForRole(MeetingRole)`. Lives under `Features/LiveSession/Models/SessionPermissions.cs`.

| Field | Type | Notes |
|-------|------|-------|
| CanPublish | bool | Audio/video publish rights |
| CanSubscribe | bool | Always `true` in this phase (no "cannot hear" role) |
| CanModerate | bool | Host/CoHost only |
| CanPublishData | bool | Follows `CanPublish` in this phase |

**Role mapping** (per R-003):

| MeetingRole | CanPublish | CanSubscribe | CanModerate | CanPublishData |
|-------------|-----------:|-------------:|------------:|---------------:|
| Host        | yes        | yes          | yes         | yes            |
| CoHost      | yes        | yes          | yes         | yes            |
| Participant | yes        | yes          | no          | yes            |
| Observer    | no         | yes          | no          | no             |

### TranscriptSegmentIngestRequest

Internal DTO handed from `WebhookService` to `ITranscriptService.IngestAsync`. Not exposed over HTTP. Lives under `Features/LiveSession/Contracts/Internal/TranscriptSegmentIngestRequest.cs`.

| Field | Type | Notes |
|-------|------|-------|
| MeetingId | Guid | Resolved from `room.name` by stripping the `mtg:` prefix |
| ExternalSegmentId | string | Platform-supplied stable id (copied to `TranscriptSegment.ExternalSegmentId`) |
| SpeakerUserId | Guid? | Resolved from the payload's `participant.identity = user:{UserId}`; null when platform cannot attribute |
| Text | string | Final transcribed utterance |
| StartMs | long | From payload |
| EndMs | long | From payload; MUST be `>= StartMs` |
| SequenceNumber | long | Platform-supplied monotonic sequence within the meeting; dedup key |
| IsFinal | bool | Only `true` values are persisted; `false` is discarded by `WebhookService` before this DTO is constructed (R-007) |

### JoinCredential

A transient DTO returned by `POST /api/meetings/{id}/session/join-token`. Not stored; lives only in the response.

| Field | Type | Notes |
|-------|------|-------|
| AccessToken | string | The signed LiveKit access-token JWT |
| RoomName | string | `mtg:{MeetingId}` — the LiveKit room name |
| ServerUrl | string | LiveKit Cloud WebSocket URL (from config) |
| ExpiresAtUtc | DateTime | 15 minutes from issuance (FR-005, R-004) |
| Permissions | SessionPermissions | Echoed for client UI (e.g., hide "mute" button for Observers) |

---

## State Transitions

### Meeting Status (continued from Phase 3)

```text
                    ┌──────────────┐
                    │  Scheduled   │
                    └──────┬───────┘
                           │
              ┌────────────┼────────────┐
              │            │            │
              v            v            │
       ┌────────────┐  ┌──────────┐    │
       │ InProgress │  │Cancelled │    │
       └──────┬─────┘  └──────────┘    │
              │                         │
         ┌────┼────┐                    │
         │         │                    │
         v         v                    │
   ┌──────────┐ ┌────────┐             │
   │Completed │ │ Failed │             │
   └──────────┘ └────────┘             │
```

**Phase 3 implemented**: `Scheduled → Cancelled` (Host action).
**Phase 4 implements**: `Scheduled → InProgress` (on verified `RoomStarted`), `InProgress → Completed` (on verified `RoomFinished` OR reconciliation sweep per R-011). `InProgress → Failed` is reserved for later phases.

**Sole authority**: `WebhookService` (R-010). No user-facing endpoint in this phase transitions `MeetingStatus`.

### Session Transcription State (not stored in our DB)

Transcription "is running" / "is paused" lives entirely on the realtime platform side. The backend does NOT persist a boolean for this — webhooks of type `TranscriptionPaused` / `TranscriptionResumed` simply drive client notifications via `ILiveSessionNotifier` (FR-024).

---

## Entity Relationship Diagram

```text
Organization (Phase 2)
    │ 1
    │
    │ *
Meeting (Phase 3) ───────── MeetingParticipant (Phase 3) ──── ApplicationUser (Phase 1)
    │ 1                              │ *                           │ 1
    │                                                              │
    │ *                                                            │
TranscriptSegment ──────────────────────────────────────────────── │ 0..1  (SpeakerUserId)
    │ *
    │
Meeting 1 ──── * SessionEvent ──────────────────────────────────── │ 0..1  (ParticipantUserId)
```

Both `TranscriptSegment` and `SessionEvent` belong to the same tenant (`Organization`) as their parent `Meeting` — this is enforced by setting `OrganizationId` at insert time from the resolved `Meeting`, and by the global query filter on reads.

---

## Validation Rules Derived from Functional Requirements

| FR | Enforcement Site | Mechanism |
|----|------------------|-----------|
| FR-001 | `SessionController.GetJoinToken` | `[Authorize]` + `[EnforceOrgAccess]` |
| FR-002 | `SessionPermissions.ForRole(MeetingRole)` | Pure function, unit-tested exhaustively |
| FR-003 | `SessionService.IssueJoinTokenAsync` | Lookup `MeetingParticipant` by `(MeetingId, UserId, OrganizationId)`; return `LiveSessionErrors.NotAParticipant` if absent |
| FR-004 | `SessionService.IssueJoinTokenAsync` | Reject if `Meeting.Status ∈ {Cancelled, Completed}` → `LiveSessionErrors.MeetingNotJoinable` |
| FR-005 | `LiveKitTokenIssuer.Issue` | `ttl = TimeSpan.FromMinutes(15)` |
| FR-006 | `WebhookController.Receive` | Reads raw body, invokes validator, dispatches to `WebhookService` |
| FR-007 | `LiveKitWebhookValidator.Validate` | `WebhookReceiver.Receive(body, authHeader)`; on failure → 401 |
| FR-008 | `WebhookService.ProcessAsync` | Insert-and-catch-unique on `SessionEvents.ExternalEventId` |
| FR-009 | `WebhookService.HandleRoomStarted` | Transition within transaction; no-op if already `InProgress` or beyond |
| FR-010 | `WebhookService.HandleRoomFinished` | Transition within transaction; no-op if already `Completed` |
| FR-011 | `TranscriptService.IngestAsync` | Insert segment with all required fields |
| FR-012 | DB unique constraint | `IX_TranscriptSegments_MeetingId_SequenceNumber` |
| FR-013 | `TranscriptController.Get` → `TranscriptService.GetAsync` | Query ordered by `SequenceNumber` |
| FR-014 | `TranscriptService.GetAsync` | Enforce participant check before returning; non-participant → `LiveSessionErrors.NotAParticipant` |
| FR-015 | EF Core global query filter on `TranscriptSegment` and `SessionEvent` | `EntityTypeBuilder.HasQueryFilter(x => x.OrganizationId == _orgContext.OrganizationId)` |
| FR-016 | `LiveSessionNotifier` | Only exposes group-scoped sends; no `Clients.All` method |
| FR-017 | `WebhookService.ProcessAsync` | `IPublisher.Publish(new SessionStartedEvent(...))` etc. inside the tx |
| FR-018 | EF Core conventions + entity fields | All `DateTime` fields typed as `timestamp with time zone`, defaulted to UTC |
| FR-019 | `TranscriptService.IngestAsync` | No status check on ingest — a `Completed` meeting can still accept late segments |
| FR-020 | `WebhookService.HandleParticipantJoined` / `HandleParticipantLeft` | Persist `SessionEvent` rows; attendance is derived by query (no separate attendance table) |
| FR-021 | Not enforced in code (platform-imposed) | Documented limit; load tests assert latency targets hold at 50 participants |
| FR-022 | `LiveKitTokenIssuer.Issue` | Access token does not request non-English STT; LiveKit Cloud STT configured to English only |
| FR-023 | `SessionService.PauseTranscriptionAsync` / `ResumeTranscriptionAsync` | Authorisation check: caller's `MeetingRole ∈ {Host, CoHost}`; else `LiveSessionErrors.ForbiddenForRole` |
| FR-024 | `WebhookService.HandleTranscriptionPaused` / `HandleTranscriptionResumed` | Emit via `ILiveSessionNotifier.NotifyTranscriptionStateChangedAsync(orgId, meetingId, state, actingUserId)` |
