# Data Model: Realtime Session Pipeline

**Feature**: 004-realtime-pipeline | **Date**: 2026-04-18 | **Last Updated**: 2026-04-21

> **Revision note (2026-04-21)**: `TranscriptSegment` is out of scope — the authoritative transcript is produced in Phase 6. The recording pipeline has been simplified to the MVP minimum: one entity (`Recording`), one job (`DownloadRecordingJob`), no domain events for recording, no reconciliation.

## Entities

### SessionEvent

A verified event delivered by the realtime platform. Persisted for (a) idempotency, (b) deriving participant attendance, (c) operational audit. One row per unique platform-side event id.

| Field | Type | Constraints | Notes |
|-------|------|-------------|-------|
| Id | Guid | PK, auto-generated | Inherited from `BaseEntity` |
| MeetingId | Guid | FK to `Meeting`, required, indexed | Resolved from the room name in the webhook payload |
| OrganizationId | Guid | FK to `Organization`, required | `IHasOrganizationId` — tenant isolation via global query filter |
| ExternalEventId | string | Required, max 100 chars, **UNIQUE** | Platform-supplied event id; idempotency primitive |
| EventType | SessionEventType (enum) | Required | See enum below |
| ParticipantUserId | Guid? | FK to `ApplicationUser`, nullable | Only set for `ParticipantJoined` / `ParticipantLeft` events; resolved from `user:{UserId}` identity in the payload |
| PayloadJson | string | Required | Raw webhook payload, stored as `jsonb`, for debugging / audit |
| OccurredAtUtc | DateTime | Required | Time the event occurred at the platform (from payload) |
| ProcessedAtUtc | DateTime | Required | Time the backend persisted the event |

**Relationships**:

- Belongs to one `Meeting` (via `MeetingId`)
- Belongs to one `Organization` (via `OrganizationId`)
- Optionally belongs to one `ApplicationUser` (via `ParticipantUserId`)

**Indexes**:

- `IX_SessionEvents_ExternalEventId` on `ExternalEventId` — **UNIQUE**. The core idempotency primitive.
- `IX_SessionEvents_MeetingId_OccurredAtUtc` on `(MeetingId, OccurredAtUtc)` — for attendance and timeline queries.
- `IX_SessionEvents_OrganizationId` on `OrganizationId` — required by the global query filter.

**Constraints**:

- Unique `ExternalEventId` (event ids are globally unique on LiveKit Cloud's side).

> **Removed**: the `IsReconciliation` flag. With no reconciliation job, there is no synthesised event source.

---

### Recording

Minimal metadata record pointing at a recording's location in MinIO. One row per meeting.

| Field | Type | Constraints | Notes |
|-------|------|-------------|-------|
| Id | Guid | PK, auto-generated | Inherited from `BaseEntity` |
| MeetingId | Guid | FK to `Meeting`, required, **UNIQUE** | One recording per meeting in this phase |
| OrganizationId | Guid | FK to `Organization`, required | `IHasOrganizationId` — tenant isolation via global query filter. Carried so Phase 6 queries stay tenant-scoped. |
| FilePath | string? | Nullable until download completes, max 500 chars | MinIO object key. `null` while `Status = Pending`; set when `Status = Completed`. |
| Status | RecordingStatus (enum) | Required, default `Pending` | `Pending`, `Completed`, or `Failed` |
| CreatedAtUtc | DateTime | Auto-set | Inherited from `BaseEntity` |
| UpdatedAtUtc | DateTime | Auto-set | Inherited from `BaseEntity` |

**Relationships**:

- Belongs to one `Meeting` (via `MeetingId`) — at most one `Recording` per meeting.
- Belongs to one `Organization` (via `OrganizationId`).

**Indexes**:

- `IX_Recordings_MeetingId` on `MeetingId` — **UNIQUE**. One recording per meeting; also serves as the idempotency primitive for the handoff (duplicate `egress_ended` deliveries that try to create a second row fail the uniqueness check and are treated as no-ops).
- `IX_Recordings_OrganizationId` on `OrganizationId` — required by the global query filter.

**Constraints**:

- Unique `MeetingId`.

**Notes on shape**:

- No `CloudStorageUrl`, `SizeBytes`, `DurationMs`, `ExternalEgressId`, `FailureReason`, or `CompletedAtUtc` columns in the MVP. Implementations are free to add columns that are strictly helpful for operations (for example, a stable external egress id as a second unique index to harden against LiveKit retries), but the spec commits only to the four fields above.
- The binary itself is never stored in the database — only the `FilePath` pointing into MinIO.

---

## Enums

### SessionEventType

```text
RoomStarted         = 0
RoomFinished        = 1
ParticipantJoined   = 2
ParticipantLeft     = 3
RecordingStarted    = 4   // optional — recorded only if the handler is enabled
EgressEnded         = 5   // the one essential recording event
Unknown             = 99
```

> Any event type not listed above is persisted as `Unknown` for forward-compatibility and then ignored.

### RecordingStatus

```text
Pending    = 0   // download not yet finished (row may exist from recording_started, or be created at egress_ended)
Completed  = 1   // MinIO object is readable; FilePath populated
Failed     = 2   // terminal failure after the job's retry budget
```

---

## Value Objects (Not Persisted)

### SessionPermissions

A pure value record returned by `SessionPermissions.ForRole(MeetingRole)`.

| Field | Type |
|-------|------|
| CanPublish | bool |
| CanSubscribe | bool |
| CanModerate | bool |
| CanPublishData | bool |

| MeetingRole | CanPublish | CanSubscribe | CanModerate | CanPublishData |
|-------------|-----------:|-------------:|------------:|---------------:|
| Host        | yes        | yes          | yes         | yes            |
| CoHost      | yes        | yes          | yes         | yes            |
| Participant | yes        | yes          | no          | yes            |
| Observer    | no         | yes          | no          | no             |

### JoinCredential

Transient DTO returned by `POST /api/meetings/{id}/session/join-token`. Not stored.

| Field | Type | Notes |
|-------|------|-------|
| AccessToken | string | The signed LiveKit access-token JWT |
| RoomName | string | `mtg:{MeetingId}` |
| ServerUrl | string | LiveKit Cloud WebSocket URL (from config) |
| ExpiresAtUtc | DateTime | 15 minutes from issuance |
| Permissions | SessionPermissions | Echoed for client UI |

---

## Domain Events (MediatR)

Only **session lifecycle** events. No recording events.

```text
SessionStartedEvent(Guid MeetingId, Guid OrganizationId, DateTime OccurredAtUtc)
SessionEndedEvent(Guid MeetingId, Guid OrganizationId, DateTime OccurredAtUtc)
```

> **Removed**: `RecordingAvailableEvent`, `RecordingFailedEvent`, `TranscriptSegmentIngestedEvent`, `TranscriptionStateChangedEvent`. Phase 6 reads recordings from MinIO directly using `Recording.FilePath`; no event bridges the two phases.

---

## State Transitions

### Meeting Status

`Scheduled → InProgress` on verified `RoomStarted`.
`InProgress → Completed` on verified `RoomFinished`.
`Scheduled → Cancelled` is Phase 3's concern (Host action).

No reconciliation job in this phase. A meeting whose `RoomFinished` is never delivered will remain `InProgress` until an operator intervenes — acceptable MVP behaviour.

### Recording Status

```text
  (egress_ended webhook, or recording_started if the optional handler is enabled)
                    │
                    v
               ┌──────────┐
               │ Pending  │───── DownloadRecordingJob enqueued
               └────┬─────┘
                    │ (job runs, downloads from cloud URL, uploads to MinIO)
         ┌──────────┼──────────┐
         │                     │
         v                     v
    ┌───────────┐         ┌──────────┐
    │ Completed │         │  Failed  │
    └───────────┘         └──────────┘
```

Once `Status = Completed` and `FilePath` is populated, Phase 6 can read the object from MinIO.

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
    │ 1                                                            │
    ├── 0..1 Recording                                             │
    │                                                              │
    │ *                                                            │
    └── SessionEvent ──────────────────────────────────────────── │ 0..1  (ParticipantUserId)
```

`SessionEvent` and `Recording` both carry `OrganizationId`, enforced by the global query filter on reads.

---

## Validation Rules Derived from Functional Requirements

| FR | Enforcement Site | Mechanism |
|----|------------------|-----------|
| FR-001 | `SessionController.GetJoinToken` | `[Authorize]` + `[EnforceOrgAccess]` |
| FR-002 | `SessionPermissions.ForRole(MeetingRole)` | Pure function, unit-tested exhaustively |
| FR-003 | `SessionService.IssueJoinTokenAsync` | Lookup `MeetingParticipant`; return `NotAParticipant` if absent |
| FR-004 | `SessionService.IssueJoinTokenAsync` | Reject if `Meeting.Status ∈ {Cancelled, Completed}` → `MeetingNotJoinable` |
| FR-005 | `LiveKitTokenIssuer.Issue` | `ttl = TimeSpan.FromMinutes(15)` |
| FR-006 | `WebhookController.Receive` | Reads raw body, invokes validator, dispatches to `WebhookService` |
| FR-007 | `LiveKitWebhookValidator.Validate` | `WebhookReceiver.Receive(body, authHeader)`; on failure → 401 |
| FR-008 | `WebhookService.ProcessAsync` | Insert-and-catch-unique on `SessionEvents.ExternalEventId`; uniqueness on `Recordings.MeetingId` covers duplicate `egress_ended` |
| FR-009 | `WebhookService.HandleRoomStarted` | Transition within transaction; no-op if already past |
| FR-010 | `WebhookService.HandleRoomFinished` | Transition within transaction; no-op if already `Completed` |
| FR-011 | N/A — negative requirement | Enforced by *absence* of any caption-ingest code in the slice |
| FR-012 | N/A — negative requirement | No `TranscriptController`; no transcript endpoint |
| FR-013 | `WebhookService.HandleEgressEnded` → `DownloadRecordingJob` enqueue | Upsert `Recording (Status=Pending)`; `BackgroundJobClient.Enqueue` |
| FR-014 | `RecordingConfiguration` + `DownloadRecordingJob` | Row shape enforced at the EF config; job sets `FilePath` + `Status=Completed` on success, `Status=Failed` on terminal error |
| FR-015 | EF Core global query filter on `SessionEvent` and `Recording` | `HasQueryFilter(x => x.OrganizationId == _orgContext.OrganizationId)` |
| FR-016 | `LiveSessionNotifier` | Only exposes group-scoped sends; no `Clients.All` method |
| FR-017 | EF Core conventions + entity fields | All `DateTime` fields typed as `timestamp with time zone`, defaulted to UTC |
| FR-018 | `WebhookService.HandleParticipantJoined` / `HandleParticipantLeft` | Persist `SessionEvent` rows; attendance derived by query |
| FR-019 | Not enforced in code (platform-imposed) | Documented limit; load tests assert latency targets hold at 50 participants |
| FR-020 | `WebhookService.HandleEgressEnded` | If meeting not found, log Warning and return 200 without creating a `Recording` |
| FR-021 | N/A — negative requirement | Enforced by absence of any `Recording*Event` record in `LiveSessionEvents.cs` |
