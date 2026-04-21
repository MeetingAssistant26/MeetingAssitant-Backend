---
description: "Task list for feature 004-realtime-pipeline"
---

# Tasks: Realtime Session Pipeline

**Input**: Design documents from `/specs/004-realtime-pipeline/`
**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/, quickstart.md
**Tests**: Mandatory (Constitution: "No new features can be implemented without automated Contract/Integration tests covering the Acceptance Scenarios").

> **Revision note (2026-04-21)**: Tasks related to transcript-segment persistence, transcript retrieval, and transcription pause/resume have been removed. The recording-handoff pipeline has been reduced to the MVP minimum: one entity (`Recording`), one job (`DownloadRecordingJob`), one essential webhook (`egress_ended`), **no recording domain events**, **no reconciliation job**. MinIO is the integration boundary with Phase 6. The MVP slice is **US1 (Join) + US3 (Recording handoff)** with **US4 (Lifecycle)** and **US5 (Notifications)** as follow-ons. US2 is a client/platform concern with no backend implementation tasks.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US3)
- Include exact file paths in descriptions

## Path Conventions

- Backend feature code: `MeetingAssistant/Features/LiveSession/`
- Shared errors: `MeetingAssistant/Shared/Errors/`
- Tests: `tests/Integration/LiveSession/`, `tests/Unit/LiveSession/`

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Add the LiveKit SDK + MinIO + Hangfire dependencies, create the feature folder skeleton, and wire configuration.

- [ ] T001 Add `Livekit.Server.Sdk`, `Hangfire.AspNetCore`, `Hangfire.PostgreSql`, and `Minio` NuGet package references to `MeetingAssistant/MeetingAssistant.csproj`
- [ ] T002 [P] Add LiveKit configuration keys (`LiveKit:ApiKey`, `LiveKit:ApiSecret`, `LiveKit:ServerUrl`, `LiveKit:WebhookSecret`) and MinIO keys (`Storage:Endpoint`, `Storage:AccessKey`, `Storage:SecretKey`, `Storage:Bucket`) to `MeetingAssistant/appsettings.json` and `MeetingAssistant/appsettings.Development.json` with placeholder values; document expectation that real values live in `dotnet user-secrets` / env vars
- [ ] T003 [P] Create feature folder skeleton under `MeetingAssistant/Features/LiveSession/` with empty subfolders: `Endpoints/Session/`, `Endpoints/Webhook/`, `Contracts/Requests/`, `Contracts/Responses/`, `Models/`, `Models/Events/`, `Services/`, `Jobs/`, `Hubs/`, `Infrastructure/`, `Infrastructure/Persistence/Configurations/`, `Validators/`, `Mapping/`

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Create infrastructure that every user-story phase depends on (error catalog, settings binding, DI scaffolding, Hangfire registration).

**⚠️ CRITICAL**: No user story work can begin until this phase is complete.

- [ ] T004 Create `MeetingAssistant/Shared/Errors/LiveSessionErrors.cs` with entries: `NotAParticipant`, `MeetingNotJoinable`, `InvalidWebhookSignature`, `LiveKitCallFailed`, `MeetingNotFound`, `RecordingDownloadFailed`
- [ ] T005 [P] Create `MeetingAssistant/Features/LiveSession/Infrastructure/LiveKitOptions.cs` strongly-typed options class (`ApiKey`, `ApiSecret`, `ServerUrl`, `WebhookSecret`) and bind in `Program.cs`
- [ ] T006 Create `MeetingAssistant/Features/LiveSession/LiveSessionDI.cs` with empty `AddLiveSessionFeature(this IServiceCollection)` extension (service registrations will be added in later phases)
- [ ] T007 Register `builder.Services.AddLiveSessionFeature()` and `builder.Services.AddSignalR()` in `MeetingAssistant/Program.cs`; ensure Hangfire is registered (`AddHangfire` / `AddHangfireServer`) against PostgreSQL
- [ ] T008 [P] Create `MeetingAssistant/Features/LiveSession/Models/Events/LiveSessionEvents.cs` with placeholder records for `SessionStartedEvent` and `SessionEndedEvent` only (field sets filled in by US4). **No recording domain events.**

**Checkpoint**: Feature skeleton compiles; no user-facing behavior yet.

---

## Phase 3: User Story 1 - Join a Meeting's Live Session (Priority: P1) 🎯 MVP

**Goal**: An authenticated meeting participant can request a short-lived join credential scoped to the meeting's live session with permissions that match their meeting role.

**Independent Test**: Seed a meeting with participants of each role (Host / CoHost / Participant / Observer), call `POST /api/meetings/{id}/session/join-token` as each, verify role-matched permissions.

### Tests for User Story 1 ⚠️

- [ ] T009 [P] [US1] Unit test `tests/Unit/LiveSession/RolePermissionMappingTests.cs` — full `SessionPermissions.ForRole` matrix
- [ ] T010 [P] [US1] Integration test `tests/Integration/LiveSession/JoinTokenTests.cs` — acceptance scenarios 1–7 from spec US1

### Implementation for User Story 1

- [ ] T011 [P] [US1] Create `MeetingAssistant/Features/LiveSession/Models/SessionPermissions.cs` as a `readonly record struct` with static `ForRole(MeetingRole)`
- [ ] T012 [P] [US1] Create `MeetingAssistant/Features/LiveSession/Services/ILiveKitTokenIssuer.cs`
- [ ] T013 [US1] Implement `MeetingAssistant/Features/LiveSession/Services/LiveKitTokenIssuer.cs` (depends on T012)
- [ ] T014 [P] [US1] Create `MeetingAssistant/Features/LiveSession/Contracts/Requests/JoinTokenRequest.cs`
- [ ] T015 [P] [US1] Create `MeetingAssistant/Features/LiveSession/Contracts/Responses/JoinTokenResponse.cs`
- [ ] T016 [P] [US1] Create `MeetingAssistant/Features/LiveSession/Validators/JoinTokenRequestValidator.cs`
- [ ] T017 [US1] Create `MeetingAssistant/Features/LiveSession/Services/ISessionService.cs`
- [ ] T018 [US1] Implement `MeetingAssistant/Features/LiveSession/Services/SessionService.cs`
- [ ] T019 [US1] Create partial controller `MeetingAssistant/Features/LiveSession/Endpoints/Session/SessionController.cs` with `[Authorize]` + `[EnforceOrgAccess]`
- [ ] T020 [US1] Create endpoint `MeetingAssistant/Features/LiveSession/Endpoints/Session/GetJoinTokenEndpoint.cs` (depends on T018, T019)
- [ ] T021 [US1] Register `ISessionService`, `ILiveKitTokenIssuer` in `LiveSessionDI.cs`

**Checkpoint**: Participants can request role-scoped join credentials.

---

## Phase 4: User Story 3 - Record the Meeting for Post-Processing (Priority: P1)

> User Story 2 (live captions) has no backend implementation tasks — it is fulfilled entirely by the realtime platform's native data channels and verified by a client-side integration test outside the backend test suite.

**Goal**: When a meeting session ends, the finalised recording is downloaded from LiveKit Cloud storage into MinIO and the `Recording` row is marked `Completed` with its `FilePath`. Phase 6 discovers the recording by reading the row + the MinIO object — no event is published.

**Independent Test**: Post a signed `egress_ended` webhook; assert a `Recording(Pending)` row is created, a download is enqueued, the file lands in MinIO at `recordings/{MeetingId}.mp4`, and `Recording.Status` flips to `Completed` with `FilePath` populated. Replay the webhook and assert no second row and no duplicate download.

### Tests for User Story 3 ⚠️

- [ ] T022 [P] [US3] Unit test `tests/Unit/LiveSession/WebhookIdempotencyTests.cs` — duplicate `ExternalEventId` yields exactly one `SessionEvent` row and no duplicate side effects; duplicate `egress_ended` for the same meeting yields exactly one `Recording` row and one enqueued download
- [ ] T023 [P] [US3] Unit test `tests/Unit/LiveSession/DownloadRecordingJobTests.cs` — early-exit on `Completed`/`Failed`; retries transient errors; marks `Failed` after budget; deterministic target key `recordings/{MeetingId}.mp4`
- [ ] T024 [P] [US3] Integration test `tests/Integration/LiveSession/RecordingHandoffTests.cs` — `egress_ended` success creates `Recording(Pending)`, enqueues download, job completion populates `FilePath` + `Status=Completed`; duplicate `egress_ended` is idempotent; `egress_ended` failure variant creates `Recording(Failed)` and does not enqueue a download; unsigned payload → 401; orphan room (no matching meeting) → 200 without creating a `Recording` (FR-020)

### Implementation for User Story 3

- [ ] T025 [P] [US3] Create `MeetingAssistant/Features/LiveSession/Models/SessionEventType.cs` enum per data-model.md (`RoomStarted`, `RoomFinished`, `ParticipantJoined`, `ParticipantLeft`, `RecordingStarted`, `EgressEnded`, `Unknown`)
- [ ] T026 [P] [US3] Create `MeetingAssistant/Features/LiveSession/Models/RecordingStatus.cs` enum (`Pending`, `Completed`, `Failed`)
- [ ] T027 [P] [US3] Create entity `MeetingAssistant/Features/LiveSession/Models/SessionEvent.cs` per data-model.md
- [ ] T028 [P] [US3] Create entity `MeetingAssistant/Features/LiveSession/Models/Recording.cs` per data-model.md — fields: `MeetingId`, `OrganizationId`, `FilePath?`, `Status`
- [ ] T029 [US3] EF config `Features/LiveSession/Infrastructure/Persistence/Configurations/SessionEventConfiguration.cs` — unique `ExternalEventId`, `jsonb` for `PayloadJson`, tz-aware timestamps, global query filter
- [ ] T030 [US3] EF config `Features/LiveSession/Infrastructure/Persistence/Configurations/RecordingConfiguration.cs` — unique `MeetingId`, `FilePath` nullable max 500, global query filter
- [ ] T031 [US3] Add `DbSet<SessionEvent>` and `DbSet<Recording>` to `ApplicationDbContext`
- [ ] T032 [US3] Generate EF Core migration `AddLiveSession` covering both tables
- [ ] T033 [US3] Create `ILiveKitWebhookValidator` + `LiveKitWebhookValidator` wrapping `WebhookReceiver.Receive(rawBody, authHeader)`
- [ ] T034 [US3] Create `IStorageService` + `StorageService` abstraction wrapping the MinIO client (upload from source URL to target key; optional `GetObjectStream(key)` for future readers)
- [ ] T035 [US3] Create `MeetingAssistant/Features/LiveSession/Jobs/DownloadRecordingJob.cs` — Hangfire job `RunAsync(Guid meetingId, string sourceCloudUrl, CancellationToken ct)`; re-reads `Recording` by `MeetingId`; exits early on `Completed`/`Failed`; uploads to `recordings/{MeetingId}.mp4`; sets `FilePath` + `Status=Completed` on success, `Status=Failed` on terminal failure; **publishes no domain events**
- [ ] T036 [US3] Create `IWebhookService` + `WebhookService.ProcessAsync` dispatching `egress_ended` (success + failure variants) and unknown events; `HandleEgressEnded` success upserts `Recording(Pending)` and enqueues `DownloadRecordingJob` via `IBackgroundJobClient`; failure variant upserts `Recording(Failed)` and does not enqueue; orphan room → log Warning + 200 without creating a `Recording`. (Handlers for `room_*` / `participant_*` land in US4.) An optional `HandleRecordingStarted` may upsert `Recording(Pending)` for audit; if absent, the event is persisted as `Unknown`
- [ ] T037 [US3] Create partial controller `Features/LiveSession/Endpoints/Webhook/WebhookController.cs` — **no `[Authorize]`** (R-015)
- [ ] T038 [US3] Create `Features/LiveSession/Endpoints/Webhook/LiveKitWebhookEndpoint.cs` — read raw body via `EnableBuffering`, invoke validator, hand parsed event to `IWebhookService.ProcessAsync`
- [ ] T039 [US3] Register `ILiveKitWebhookValidator`, `IWebhookService`, `IStorageService` in `LiveSessionDI.cs`

**Checkpoint**: A session end produces a downloaded MinIO object + `Recording(Completed)` row for Phase 6 to read.

---

## Phase 5: User Story 4 - Automatic Session Lifecycle Tracking (Priority: P2)

**Goal**: Meeting status transitions automatically based on verified platform lifecycle webhooks. Participant presence events are recorded.

> **Removed in 2026-04-21 MVP**: the reconciliation sweep. A meeting whose `room_finished` never arrives stays `InProgress` until an operator intervenes; acceptable MVP behaviour.

### Tests for User Story 4 ⚠️

- [ ] T040 [P] [US4] Integration test `tests/Integration/LiveSession/LifecycleWebhookTests.cs` — `room_started` → `InProgress` + `SessionStartedEvent`; `room_finished` → `Completed` + `SessionEndedEvent`; `participant_joined`/`participant_left` persist `SessionEvent` with resolved `ParticipantUserId`; duplicate is idempotent; SC-003 timing (< 5s)

### Implementation for User Story 4

- [ ] T041 [US4] Flesh out `SessionStartedEvent`, `SessionEndedEvent` records in `LiveSessionEvents.cs` with `(Guid MeetingId, Guid OrganizationId, DateTime OccurredAtUtc)`
- [ ] T042 [US4] Extend `WebhookService` with `HandleRoomStarted`, `HandleRoomFinished`, `HandleParticipantJoined`, `HandleParticipantLeft` — transitions inside the `SessionEvent` insert transaction; publish MediatR domain events on success

**Checkpoint**: Meeting state tracks the realtime platform automatically.

---

## Phase 6: User Story 5 - Tenant-Scoped Realtime Notifications (Priority: P2)

**Goal**: Connected SignalR clients receive **session lifecycle** changes scoped to their organization.

> Recording availability is **not** broadcast over SignalR in the MVP. Phase 6 consumes recordings by reading the `Recording` row + the MinIO object directly. If a client UI later needs live recording status, it will come from a Phase 6 read API.

### Tests for User Story 5 ⚠️

- [ ] T043 [P] [US5] Integration test `tests/Integration/LiveSession/TenantScopedNotificationTests.cs` — two orgs connected; `session.started` / `session.ended` / `participant.joined` / `participant.left` notifications reach only the owning org; p95 relay latency < 500 ms (backend target for SignalR fan-out)

### Implementation for User Story 5

- [ ] T044 [P] [US5] Create `Features/LiveSession/Hubs/LiveSessionHub.cs` — `OnConnectedAsync` adds connection to `$"org:{orgId}"`
- [ ] T045 [P] [US5] Create `Features/LiveSession/Hubs/ILiveSessionNotifier.cs` exposing only group-scoped session-lifecycle methods (`NotifySessionStartedAsync`, `NotifySessionEndedAsync`, `NotifyParticipantJoinedAsync`, `NotifyParticipantLeftAsync`). **No** `NotifyRecording*` methods
- [ ] T046 [US5] Implement `Features/LiveSession/Hubs/LiveSessionNotifier.cs` wrapping `IHubContext<LiveSessionHub>` (depends on T044, T045)
- [ ] T047 [US5] Wire notifier into existing US4 handlers: `HandleRoomStarted` → `NotifySessionStartedAsync`; `HandleRoomFinished` → `NotifySessionEndedAsync`; `HandleParticipantJoined` / `HandleParticipantLeft` → participant notifiers. Fan-out runs fire-and-forget after the DB commit
- [ ] T048 [US5] Map `LiveSessionHub` endpoint in `Program.cs`: `app.MapHub<LiveSessionHub>("/hubs/live-session").RequireAuthorization()`
- [ ] T049 [US5] Register `ILiveSessionNotifier → LiveSessionNotifier` in `LiveSessionDI.cs`

**Checkpoint**: Feature complete — Phase 4 deliverable ready.

---

## Phase 7: Polish & Cross-Cutting Concerns

- [ ] T050 [P] Load test harness in `tests/Integration/LiveSession/PerformanceTests.cs` verifying SC-001 (token under 1s) and SC-010 (targets hold at 50 participants) against a WebApplicationFactory-hosted instance
- [ ] T051 [P] Structured-logging audit — every `ILiveKitTokenIssuer.Issue` logs `(meetingId, userId, roleAtIssuance, expiresAtUtc)`; every rejected webhook logs `(reason, externalEventId?, meetingRoom?)`; every download job logs `(meetingId, status transitions, targetKey, sizeBytes?)`; no PII in logs
- [ ] T052 Run `quickstart.md` end-to-end against a fresh LiveKit Cloud sandbox — seed a meeting, issue a token, join via LiveKit web tester, end the session, observe `RoomFinished` → `egress_ended` → MinIO object at `recordings/{MeetingId}.mp4` → `Recording.Status = Completed`

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies
- **Foundational (Phase 2)**: Depends on Setup — BLOCKS all user stories
- **User Story 1 (Phase 3)**: Depends on Foundational; independent of US3–US5
- **User Story 3 (Phase 4)**: Depends on Foundational
- **User Story 4 (Phase 5)**: Depends on US3 (shares `WebhookService`, `SessionEvent` entity, validator)
- **User Story 5 (Phase 6)**: Depends on US4 (wires notifier into lifecycle handlers)
- **Polish (Phase 7)**: Depends on all user stories

### User Story Dependencies

- **US1**: Foundational only
- **US2**: No backend tasks (client/platform)
- **US3**: Foundational only
- **US4**: US3
- **US5**: US4

### Within Each User Story

- Tests MUST fail first, then implementation makes them pass
- Models → Configurations → DbContext/migration → Services → Jobs → Endpoints → DI registration

---

## Implementation Strategy

### MVP First — Ship US1 + US3 together (both P1)

Phase 4's MVP is the "join → record → handoff" loop. Ship:

1. Setup + Foundational (Phases 1, 2)
2. US1 (Phase 3): participants can join live sessions
3. US3 (Phase 4): recording lands in MinIO with `Recording.Status = Completed`
4. **STOP and VALIDATE**: run the quickstart end-to-end against a LiveKit Cloud sandbox
5. Ship MVP. Phase 6 can already begin consuming the `Recording` row + MinIO object.

### Incremental Delivery — Ship P2 stories next

6. US4: lifecycle tracking (status transitions; no reconciliation in MVP)
7. US5: tenant-scoped SignalR notifications for session lifecycle

---

## Notes

- [P] tasks = different files, no dependencies on other incomplete tasks in the same phase
- [Story] label maps every story-phase task to its user story for traceability
- Tests are mandatory per the project constitution
- Every task names a project-relative file path
- Commit after each task or logical group
- Spec edge case "last Host leaves mid-session" is platform-side behaviour; no backend code required
- Spec edge case "orphan room with no matching meeting" is enforced in `WebhookService` by a log-and-200 path (FR-020)
- Removed in the 2026-04-21 MVP revision: `IRecordingService` + `RecordingService`, `SessionReconciliationJob`, `RecordingAvailableEvent` / `RecordingFailedEvent`, `NotifyRecording*` notifier methods, recurring-job registration. Simpler shape; fewer moving parts
- The previous revision's transcript-ingest (US2 old), transcript-retrieval (US3 old), and pause/resume (US5 old) tasks remain **removed** — those responsibilities moved to Phase 6 or out of scope entirely
