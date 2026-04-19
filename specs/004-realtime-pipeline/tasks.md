---
description: "Task list for feature 004-realtime-pipeline"
---

# Tasks: Realtime Session Pipeline

**Input**: Design documents from `/specs/004-realtime-pipeline/`
**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/, quickstart.md
**Tests**: Mandatory (Constitution: "No new features can be implemented without automated Contract/Integration tests covering the Acceptance Scenarios").

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3)
- Include exact file paths in descriptions

## Path Conventions

- Backend feature code: `MeetingAssistant/Features/LiveSession/`
- Shared errors: `MeetingAssistant/Shared/Errors/`
- Tests: `tests/Integration/LiveSession/`, `tests/Unit/LiveSession/`

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Add the LiveKit SDK dependency, create the feature folder skeleton, and wire configuration.

- [ ] T001 Add `Livekit.Server.Sdk` NuGet package reference to `MeetingAssistant/MeetingAssistant.csproj`
- [ ] T002 [P] Add LiveKit configuration keys (`LiveKit:ApiKey`, `LiveKit:ApiSecret`, `LiveKit:ServerUrl`, `LiveKit:WebhookSecret`) to `MeetingAssistant/appsettings.json` and `MeetingAssistant/appsettings.Development.json` with placeholder values; document expectation that real values live in `dotnet user-secrets` / env vars
- [ ] T003 [P] Create feature folder skeleton under `MeetingAssistant/Features/LiveSession/` with empty subfolders: `Endpoints/Session/`, `Endpoints/Webhook/`, `Endpoints/Transcript/`, `Contracts/Requests/`, `Contracts/Responses/`, `Models/`, `Models/Events/`, `Services/`, `Hubs/`, `Infrastructure/Persistence/Configurations/`, `Validators/`, `Mapping/`

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Create infrastructure that every user-story phase depends on (error catalog, settings binding, DI scaffolding).

**⚠️ CRITICAL**: No user story work can begin until this phase is complete.

- [ ] T004 Create `MeetingAssistant/Shared/Errors/LiveSessionErrors.cs` with entries: `NotAParticipant`, `MeetingNotJoinable`, `ForbiddenForRole`, `TranscriptionNotPausable`, `TranscriptionNotResumable`, `InvalidWebhookSignature`, `LiveKitCallFailed`, `MeetingNotFound`
- [ ] T005 [P] Create `MeetingAssistant/Features/LiveSession/Infrastructure/LiveKitOptions.cs` strongly-typed options class (`ApiKey`, `ApiSecret`, `ServerUrl`, `WebhookSecret`) and bind in `Program.cs` via `builder.Services.Configure<LiveKitOptions>(builder.Configuration.GetSection("LiveKit"))`
- [ ] T006 Create `MeetingAssistant/Features/LiveSession/LiveSessionDI.cs` with empty `AddLiveSessionFeature(this IServiceCollection)` extension (service registrations will be added in later phases)
- [ ] T007 Register `builder.Services.AddLiveSessionFeature()` and `builder.Services.AddSignalR()` in `MeetingAssistant/Program.cs`
- [ ] T008 [P] Create `MeetingAssistant/Features/LiveSession/Models/Events/LiveSessionEvents.cs` containing empty placeholder records for `SessionStartedEvent`, `SessionEndedEvent`, `TranscriptSegmentIngestedEvent`, `TranscriptionStateChangedEvent` (full field sets filled in by the story that needs them)

**Checkpoint**: Feature skeleton compiles; no user-facing behavior yet. User-story phases can now proceed.

---

## Phase 3: User Story 1 - Join a Meeting's Live Session (Priority: P1) 🎯 MVP

**Goal**: An authenticated meeting participant can request a short-lived join credential scoped to the meeting's live session with permissions that match their meeting role.

**Independent Test**: Seed a meeting with participants of each role (Host / CoHost / Participant / Observer), call `POST /api/meetings/{id}/session/join-token` as each, and verify each response carries the permissions required by FR-002 and the spec's acceptance scenarios 1–4. Non-participants get 403; Cancelled/Completed meetings get 409; credentials decode with a 15-minute TTL (FR-005).

### Tests for User Story 1 ⚠️

> Write these tests FIRST and confirm they fail before implementing.

- [ ] T009 [P] [US1] Unit test `tests/Unit/LiveSession/RolePermissionMappingTests.cs` asserting the full `SessionPermissions.ForRole` matrix (Host, CoHost, Participant, Observer) per R-003
- [ ] T010 [P] [US1] Integration test `tests/Integration/LiveSession/JoinTokenTests.cs` covering spec User Story 1 acceptance scenarios 1–7: each role gets matching grants; non-participant → 403; Cancelled/Completed meeting → 409; issued token's `exp` claim is 15 min in the future; token's `video.room` = `mtg:{MeetingId}`; token metadata carries `organizationId` and `meetingRole` (R-004); also assert the `organizationId` embedded in the token is the caller's active org at issuance time, so that a later active-org switch does not retroactively broaden or narrow the credential (spec edge case L106)

### Implementation for User Story 1

- [ ] T011 [P] [US1] Create `MeetingAssistant/Features/LiveSession/Models/SessionPermissions.cs` as a `readonly record struct` with `CanPublish`, `CanSubscribe`, `CanModerate`, `CanPublishData` and a static `ForRole(MeetingRole)` method implementing R-003's matrix
- [ ] T012 [P] [US1] Create `MeetingAssistant/Features/LiveSession/Services/ILiveKitTokenIssuer.cs` with method signature `Result<JoinCredential> Issue(Guid meetingId, Guid organizationId, Guid userId, string displayName, SessionPermissions permissions)`
- [ ] T013 [US1] Implement `MeetingAssistant/Features/LiveSession/Services/LiveKitTokenIssuer.cs` wrapping `AccessToken` from `Livekit.Server.Sdk`; sets `identity = user:{UserId}`, `ttl = 15 min`, `video.room = mtg:{MeetingId}`, grant flags from `SessionPermissions`, and `metadata = { organizationId, meetingRole }` (R-004); returns a `JoinCredential` DTO including `AccessToken`, `RoomName`, `ServerUrl` (from `LiveKitOptions`), `ExpiresAtUtc`, and `Permissions` (depends on T012)
- [ ] T014 [P] [US1] Create request DTO `MeetingAssistant/Features/LiveSession/Contracts/Requests/JoinTokenRequest.cs` with nullable `DisplayName`
- [ ] T015 [P] [US1] Create response DTO `MeetingAssistant/Features/LiveSession/Contracts/Responses/JoinTokenResponse.cs` matching `contracts/session-endpoints.md` example (`AccessToken`, `RoomName`, `ServerUrl`, `ExpiresAtUtc`, `Permissions`)
- [ ] T016 [P] [US1] Create `MeetingAssistant/Features/LiveSession/Validators/JoinTokenRequestValidator.cs` (FluentValidation) — `DisplayName` length 1–120 when present
- [ ] T017 [US1] Create `MeetingAssistant/Features/LiveSession/Services/ISessionService.cs` with `IssueJoinTokenAsync(Guid meetingId, Guid callerUserId, string? displayName, CancellationToken ct)` returning `Result<JoinTokenResponse>`
- [ ] T018 [US1] Implement `MeetingAssistant/Features/LiveSession/Services/SessionService.cs`: loads `MeetingParticipant` by `(MeetingId, UserId)` under the active org (tenant-isolated via global query filter); returns `LiveSessionErrors.NotAParticipant` / `MeetingNotJoinable` as appropriate; calls `ILiveKitTokenIssuer` and maps to `JoinTokenResponse` (depends on T011, T012, T014, T015, T017)
- [ ] T019 [US1] Create partial controller `MeetingAssistant/Features/LiveSession/Endpoints/Session/SessionController.cs` with route `api/meetings/{meetingId:guid}/session`, `[Authorize]` + `[EnforceOrgAccess]`, and an injected `ISessionService` field
- [ ] T020 [US1] Create endpoint file `MeetingAssistant/Features/LiveSession/Endpoints/Session/GetJoinTokenEndpoint.cs` as a partial extension of `SessionController` exposing `POST join-token` → calls `_sessionService.IssueJoinTokenAsync` and returns `Result.ToProblem(correlationIdProvider)` on failure per Constitution Principle V (depends on T018, T019)
- [ ] T021 [US1] Register `ISessionService → SessionService` and `ILiveKitTokenIssuer → LiveKitTokenIssuer` in `LiveSessionDI.cs` (extend T006)

**Checkpoint**: User Story 1 fully functional. A participant can request a join credential and receive correct role-scoped grants. Non-participants and non-joinable meetings are rejected.

---

## Phase 4: User Story 2 - Capture Live Transcription During a Meeting (Priority: P1)

**Goal**: Final transcript segments delivered by the realtime platform's STT are ingested via signed webhook, persisted with their meeting, speaker, timing, and sequence, and made available to downstream consumers via a MediatR domain event. Duplicate deliveries are de-duplicated.

**Independent Test**: Send a signed `transcription_segment` webhook payload twice against the backend; assert (a) first delivery persists one `TranscriptSegment` row, (b) second delivery returns 200 with no duplicate row, (c) unsigned payload returns 401, (d) the resulting row carries the correct `MeetingId`, `OrganizationId`, `SpeakerUserId`, `Text`, `StartMs`, `EndMs`, `SequenceNumber`, `ExternalSegmentId`.

### Tests for User Story 2 ⚠️

- [ ] T022 [P] [US2] Unit test `tests/Unit/LiveSession/WebhookIdempotencyTests.cs` — same `ExternalEventId` processed twice yields exactly one `SessionEvent` row and no duplicate domain-event publication (R-006)
- [ ] T023 [P] [US2] Unit test `tests/Unit/LiveSession/TranscriptDeduplicationTests.cs` — duplicate `(MeetingId, SequenceNumber)` ingest returns success without inserting a second row; interim (`IsFinal = false`) segments are discarded (R-007)
- [ ] T024 [P] [US2] Integration test `tests/Integration/LiveSession/WebhookIngestionTests.cs` covering: signed `transcription_segment` payload persists one row; unsigned payload → 401; replayed signed payload → 200 with no duplicate; `transcription_segment` with `isFinal=false` is discarded; segment with unknown speaker persists with `SpeakerUserId = null`; **late-segment case (FR-019 / spec edge case L105)**: a `transcription_segment` for a meeting already in `Completed` status is still ingested and the row is visible via `GET /api/meetings/{id}/transcript` in correct `SequenceNumber` order alongside earlier segments (covers spec User Story 2 acceptance scenario 4's "after meeting ends" variant)

### Implementation for User Story 2

- [ ] T025 [P] [US2] Create `MeetingAssistant/Features/LiveSession/Models/SessionEventType.cs` enum matching `data-model.md` (RoomStarted=0 … Unknown=99)
- [ ] T026 [P] [US2] Create entity `MeetingAssistant/Features/LiveSession/Models/TranscriptSegment.cs` inheriting `BaseEntity`, implementing `IHasOrganizationId`, with all fields from `data-model.md`
- [ ] T027 [P] [US2] Create entity `MeetingAssistant/Features/LiveSession/Models/SessionEvent.cs` inheriting `BaseEntity`, implementing `IHasOrganizationId`, with all fields from `data-model.md` (depends on T025 for the `EventType` column)
- [ ] T028 [US2] Create EF Core config `MeetingAssistant/Features/LiveSession/Infrastructure/Persistence/Configurations/TranscriptSegmentConfiguration.cs`: unique index on `(MeetingId, SequenceNumber)`, check constraint `EndMs >= StartMs`, global query filter by `OrganizationId`, `text` column type for `Text`, **`CreatedAtUtc` and `UpdatedAtUtc` mapped to `timestamp with time zone` and default-UTC per FR-018**
- [ ] T029 [US2] Create EF Core config `MeetingAssistant/Features/LiveSession/Infrastructure/Persistence/Configurations/SessionEventConfiguration.cs`: unique index on `ExternalEventId`, `PayloadJson` mapped to `jsonb`, global query filter by `OrganizationId`, **`OccurredAtUtc` and `ProcessedAtUtc` mapped to `timestamp with time zone` per FR-018**
- [ ] T030 [US2] Add `DbSet<TranscriptSegment> TranscriptSegments` and `DbSet<SessionEvent> SessionEvents` to `ApplicationDbContext` (depends on T026, T027)
- [ ] T031 [US2] Generate EF Core migration `dotnet ef migrations add AddLiveSession -p MeetingAssistant -s MeetingAssistant` and commit generated files under `MeetingAssistant/Infrastructure/Persistence/Migrations/` (or project-equivalent path) (depends on T028, T029, T030)
- [ ] T032 [P] [US2] Flesh out `TranscriptSegmentIngestedEvent` record in `Models/Events/LiveSessionEvents.cs` with fields `(Guid MeetingId, Guid OrganizationId, Guid? SpeakerUserId, long SequenceNumber)` (extends T008)
- [ ] T033 [US2] Create `MeetingAssistant/Features/LiveSession/Services/ILiveKitWebhookValidator.cs` + `LiveKitWebhookValidator.cs` wrapping `WebhookReceiver.Receive(rawBody, authHeader)` (R-005); returns `Result<WebhookEvent>` with `LiveSessionErrors.InvalidWebhookSignature` on failure
- [ ] T034 [US2] Create `MeetingAssistant/Features/LiveSession/Services/ITranscriptService.cs` with `IngestAsync(TranscriptSegmentIngestRequest request, CancellationToken ct)` method; `GetAsync` added in US3. Also create the internal DTO `MeetingAssistant/Features/LiveSession/Contracts/Internal/TranscriptSegmentIngestRequest.cs` per data-model.md § Value Objects with fields `(Guid MeetingId, string ExternalSegmentId, Guid? SpeakerUserId, string Text, long StartMs, long EndMs, long SequenceNumber, bool IsFinal)`
- [ ] T035 [US2] Implement `IngestAsync` in `MeetingAssistant/Features/LiveSession/Services/TranscriptService.cs`: resolve `OrganizationId` from the meeting; insert `TranscriptSegment`; catch unique-violation on `(MeetingId, SequenceNumber)` as success-no-op (FR-012); publish `TranscriptSegmentIngestedEvent` via MediatR on successful insert (FR-017)
- [ ] T036 [US2] Create `MeetingAssistant/Features/LiveSession/Services/IWebhookService.cs` with `ProcessAsync(WebhookEvent evt, string rawPayload, CancellationToken ct)` returning `Result`
- [ ] T037 [US2] Implement `WebhookService.ProcessAsync` in `MeetingAssistant/Features/LiveSession/Services/WebhookService.cs`: resolve `MeetingId` from `room.name` (strip `mtg:` prefix); reject with 200-no-op if meeting not found (edge case per spec); wrap all side effects in a single transaction that also inserts a `SessionEvent` row with unique `ExternalEventId`; catch unique-violation as idempotent no-op (R-006). For this phase, implement only the `transcription_segment` handler — discard interim segments (R-007) and delegate final segments to `ITranscriptService.IngestAsync` (other handlers added in US4 / US5)
- [ ] T038 [US2] Create partial controller `MeetingAssistant/Features/LiveSession/Endpoints/Webhook/WebhookController.cs` with route `api/webhooks`, **no `[Authorize]`**, and an injected `ILiveKitWebhookValidator` + `IWebhookService` (R-015 — document the missing `[Authorize]` with an XML comment referencing R-015)
- [ ] T039 [US2] Create endpoint file `MeetingAssistant/Features/LiveSession/Endpoints/Webhook/LiveKitWebhookEndpoint.cs` as a partial extension exposing `POST /livekit`: read raw body once via `Request.EnableBuffering()` + `StreamReader`, invoke validator, on success hand parsed event to `IWebhookService.ProcessAsync`; on signature failure return `Result.ToProblem(correlationIdProvider)` producing an RFC 7807 401 per Constitution Principle V (depends on T033, T037, T038)
- [ ] T040 [US2] Register `ILiveKitWebhookValidator → LiveKitWebhookValidator`, `ITranscriptService → TranscriptService`, `IWebhookService → WebhookService` in `LiveSessionDI.cs` (extend T021)

**Checkpoint**: Transcript segments are captured, de-duplicated, and stored. Webhook signature verification blocks unsigned traffic. Downstream consumers can subscribe to `TranscriptSegmentIngestedEvent`.

---

## Phase 5: User Story 3 - Retrieve a Meeting's Transcript (Priority: P1)

**Goal**: A meeting participant retrieves the stored transcript via HTTP as an ordered list of segments. Non-participants and cross-org callers are rejected.

**Independent Test**: Seed a meeting with 10 `TranscriptSegment` rows at varying sequence numbers; `GET /api/meetings/{id}/transcript` as a participant returns all 10 ordered by sequence; same call as a non-participant returns 403; call against a different-org meeting returns 404; call against a meeting with zero segments returns `{ segments: [], totalCount: 0 }` with 200.

### Tests for User Story 3 ⚠️

- [ ] T041 [P] [US3] Integration test `tests/Integration/LiveSession/TranscriptRetrievalTests.cs` covering spec User Story 3 acceptance scenarios 1–4: participant gets ordered segments; empty meeting returns empty list; non-participant gets 403; cross-org access returns 404

### Implementation for User Story 3

- [ ] T042 [P] [US3] Create response DTO `MeetingAssistant/Features/LiveSession/Contracts/Responses/TranscriptSegmentResponse.cs` with fields `Id, SequenceNumber, SpeakerUserId, SpeakerDisplayName, Text, StartMs, EndMs` per `contracts/transcript-endpoints.md`
- [ ] T043 [P] [US3] Create response DTO `MeetingAssistant/Features/LiveSession/Contracts/Responses/TranscriptResponse.cs` with `MeetingId`, `Segments` (list), `TotalCount`
- [ ] T044 [P] [US3] Create `MeetingAssistant/Features/LiveSession/Mapping/LiveSessionMappingConfig.cs` — Mapster `TypeAdapterConfig` for `TranscriptSegment → TranscriptSegmentResponse`
- [ ] T045 [US3] Extend `ITranscriptService` (T034) and `TranscriptService` (T035) with `GetAsync(Guid meetingId, Guid callerUserId, CancellationToken ct)` returning `Result<TranscriptResponse>`; enforce participant check per R-013 (any role including Observer); project segments ordered by `SequenceNumber` ascending; batched lookup of speaker display names
- [ ] T046 [US3] Create partial controller `MeetingAssistant/Features/LiveSession/Endpoints/Transcript/TranscriptController.cs` with route `api/meetings/{meetingId:guid}/transcript`, `[Authorize]` + `[EnforceOrgAccess]`, injected `ITranscriptService`
- [ ] T047 [US3] Create endpoint file `MeetingAssistant/Features/LiveSession/Endpoints/Transcript/GetTranscriptEndpoint.cs` as a partial extension exposing `GET /` → calls `_transcriptService.GetAsync` and returns `Result.ToProblem(correlationIdProvider)` on failure per Constitution Principle V (depends on T045, T046)

**Checkpoint**: Transcripts can be read. Combined with US1 + US2, a participant can now join a session, have their speech transcribed, and later retrieve the transcript — the MVP slice of Phase 4.

---

## Phase 6: User Story 4 - Automatic Session Lifecycle Tracking (Priority: P2)

**Goal**: Meeting status transitions automatically based on verified platform lifecycle webhooks. Participant presence events are recorded. A reconciliation sweep catches meetings whose `room_finished` webhook was never delivered.

**Independent Test**: Send a signed `room_started` webhook for a Scheduled meeting; assert its status transitions to `InProgress` and a `SessionStartedEvent` domain event is published. Send a signed `room_finished` for an InProgress meeting; assert `Completed` + `SessionEndedEvent`. Send `participant_joined` and `participant_left` webhooks; assert `SessionEvent` rows with correct `ParticipantUserId`. A duplicate lifecycle webhook produces no additional transition. Schedule reconciliation: a meeting stuck InProgress past its scheduled end is moved to Completed.

### Tests for User Story 4 ⚠️

- [ ] T048 [P] [US4] Integration test `tests/Integration/LiveSession/LifecycleWebhookTests.cs` covering: `room_started` → meeting InProgress + domain event; `room_finished` → Completed + domain event; `participant_joined`/`participant_left` persist SessionEvent with resolved `ParticipantUserId`; duplicate delivery is idempotent; `participant_left` arriving before `participant_joined` (out-of-order, R-006) still persists both rows; **SC-003 timing assertion**: measure elapsed wall-clock from `POST /api/webhooks/livekit` returning 200 to `GET /api/meetings/{id}` exposing the new `Status` — assert under 5 seconds (use `Stopwatch` in the test)
- [ ] T049 [P] [US4] Integration test `tests/Integration/LiveSession/ReconciliationTests.cs` — a meeting in `InProgress` with `ScheduledEndUtc` > 2 hours ago and no `RoomFinished` event is transitioned to `Completed` by the reconciliation job and a synthetic `SessionEvent` with `IsReconciliation = true` is inserted (R-011)

### Implementation for User Story 4

- [ ] T050 [US4] Flesh out `SessionStartedEvent`, `SessionEndedEvent` records in `Models/Events/LiveSessionEvents.cs` with `(Guid MeetingId, Guid OrganizationId, DateTime OccurredAtUtc)` fields (extends T008)
- [ ] T051 [US4] Extend `WebhookService` (T037) with `HandleRoomStarted`, `HandleRoomFinished`, `HandleParticipantJoined`, `HandleParticipantLeft` in the same file. `HandleRoomStarted` transitions `Meeting.Status` `Scheduled → InProgress` (no-op if already past); `HandleRoomFinished` transitions `InProgress → Completed`; both publish the corresponding domain events inside the SessionEvent insert transaction (R-010). Participant handlers resolve `ParticipantUserId` from `user:{UserId}` identity and persist `SessionEvent` only (no presence-column update)
- [ ] T052 [US4] Dispatch to new handlers inside `WebhookService.ProcessAsync` (extend T037) — cases for `room_started`, `room_finished`, `participant_joined`, `participant_left`
- [ ] T053 [US4] Create background reconciliation job `MeetingAssistant/Features/LiveSession/Services/SessionReconciliationJob.cs` implementing R-011: query meetings in `InProgress` with `ScheduledEndUtc < UtcNow.AddHours(-2)` AND no `RoomFinished` SessionEvent; transition to `Completed`; insert synthetic SessionEvent with `IsReconciliation = true`. Implement as a **Hangfire recurring job** (Hangfire is already registered in `Infrastructure/DependencyInjection/HangfireDI.cs` from Phase 0); schedule hourly via `Cron.Hourly()`
- [ ] T054 [US4] Register the reconciliation job via `RecurringJob.AddOrUpdate<SessionReconciliationJob>("session-reconciliation", j => j.RunAsync(CancellationToken.None), Cron.Hourly())` during app startup in `Program.cs`

**Checkpoint**: Meeting state tracks the realtime platform automatically. Stuck `InProgress` meetings are cleaned up. Downstream Phase 6 AI pipeline has reliable `SessionEndedEvent` signal.

---

## Phase 7: User Story 5 - Tenant-Scoped Realtime Notifications + Transcription Control (Priority: P2)

**Goal**: Connected SignalR clients receive session and transcription state changes scoped to their organization. Hosts and CoHosts can pause/resume live transcription mid-session.

**Independent Test**: Connect two SignalR clients in different organizations; trigger a `room_started` webhook for an org-A meeting; assert only the org-A client receives `session.started` (FR-016, User Story 5 scenarios 1–2). As a Host or CoHost, call `POST /api/meetings/{id}/session/transcription/pause`; assert 204. As a Participant or Observer, the same call returns 403. After a `transcription_paused` webhook arrives, subscribed org clients receive a `transcription.paused` notification with `actingUserId` (FR-024, User Story 5 scenarios 4–5).

### Tests for User Story 5 ⚠️

- [ ] T055 [P] [US5] Integration test `tests/Integration/LiveSession/TranscriptionControlTests.cs` covering spec User Story 5 acceptance scenarios 4–5: Host can pause; CoHost can pause; Participant → 403; Observer → 403; pause on a non-InProgress meeting → 409; `transcription_paused` webhook produces a SignalR notification to the org group
- [ ] T056 [P] [US5] Integration test `tests/Integration/LiveSession/TenantScopedNotificationTests.cs` — two connected clients in different organizations; `room_started` / `room_finished` / `participant_joined` webhooks deliver notifications only to the owning org's group; no notification reaches the other org (FR-016, SC-009); **backend relay latency assertion (plan.md SC-002 backend target)**: measure p95 elapsed from webhook POST returning 200 to the org-A client's `OnSessionStarted` callback firing — assert under 500 ms (test harness uses in-process SignalR client)

### Implementation for User Story 5

- [ ] T057 [P] [US5] Create SignalR hub `MeetingAssistant/Features/LiveSession/Hubs/LiveSessionHub.cs`: on `OnConnectedAsync`, read `organizationId` from JWT claims and call `Groups.AddToGroupAsync(Context.ConnectionId, $"org:{organizationId}")` (R-009)
- [ ] T058 [P] [US5] Create `MeetingAssistant/Features/LiveSession/Hubs/ILiveSessionNotifier.cs` with ONLY group-scoped send methods: `NotifySessionStartedAsync`, `NotifySessionEndedAsync`, `NotifyParticipantJoinedAsync`, `NotifyParticipantLeftAsync`, `NotifyTranscriptionStateChangedAsync(Guid orgId, Guid meetingId, TranscriptionState state, Guid? actingUserId)` — **no `NotifyAll`** method
- [ ] T059 [US5] Implement `MeetingAssistant/Features/LiveSession/Hubs/LiveSessionNotifier.cs` wrapping `IHubContext<LiveSessionHub>`; every method targets `Clients.Group($"org:{orgId}")` (R-009, FR-016) (depends on T057, T058)
- [ ] T060 [US5] Flesh out `TranscriptionStateChangedEvent` record in `Models/Events/LiveSessionEvents.cs` with `(Guid MeetingId, Guid OrganizationId, TranscriptionState State, Guid? ActingUserId)` (extends T008). Also create enum `MeetingAssistant/Features/LiveSession/Models/TranscriptionState.cs` with values `Started=0, Paused=1, Resumed=2, Finished=3` per data-model.md § Enums
- [ ] T061 [US5] Create `MeetingAssistant/Features/LiveSession/Services/ILiveKitRoomAdmin.cs` + `LiveKitRoomAdmin.cs` wrapping LiveKit admin calls to pause/resume transcription on a room; returns `Result` with `LiveSessionErrors.LiveKitCallFailed` on SDK exception (R-014)
- [ ] T062 [US5] Extend `ISessionService` (T017) and `SessionService` (T018) with `PauseTranscriptionAsync(Guid meetingId, Guid callerUserId)` and `ResumeTranscriptionAsync(...)` — both enforce `MeetingRole ∈ {Host, CoHost}` and return `LiveSessionErrors.ForbiddenForRole` otherwise; reject with `LiveSessionErrors.TranscriptionNotPausable` / `TranscriptionNotResumable` when meeting is not `InProgress`; call `ILiveKitRoomAdmin` for the platform-side action (FR-023)
- [ ] T063 [P] [US5] Create endpoint file `MeetingAssistant/Features/LiveSession/Endpoints/Session/PauseTranscriptionEndpoint.cs` as a partial extension of `SessionController` exposing `POST transcription/pause` → `_sessionService.PauseTranscriptionAsync`; 204 on success, `Result.ToProblem(correlationIdProvider)` on failure per Constitution Principle V
- [ ] T064 [P] [US5] Create endpoint file `MeetingAssistant/Features/LiveSession/Endpoints/Session/ResumeTranscriptionEndpoint.cs` mirroring T063 for `POST transcription/resume`; 204 on success, `Result.ToProblem(correlationIdProvider)` on failure per Constitution Principle V
- [ ] T065 [US5] Extend `WebhookService` (T037) with `HandleTranscriptionStarted`, `HandleTranscriptionPaused`, `HandleTranscriptionResumed`, `HandleTranscriptionFinished`; each persists a `SessionEvent` and calls `ILiveSessionNotifier.NotifyTranscriptionStateChangedAsync(orgId, meetingId, state, actingUserId)` (FR-024); dispatch cases added to `ProcessAsync`
- [ ] T066 [US5] Wire notifier into existing US4 handlers (T051): `HandleRoomStarted` → `NotifySessionStartedAsync`; `HandleRoomFinished` → `NotifySessionEndedAsync`; `HandleParticipantJoined` → `NotifyParticipantJoinedAsync`; `HandleParticipantLeft` → `NotifyParticipantLeftAsync`. All notifier calls are fire-and-forget relative to the DB transaction (commit first, notify after) to avoid rolling back notifications on transient hub failures
- [ ] T067 [US5] Map `LiveSessionHub` endpoint in `Program.cs`: `app.MapHub<LiveSessionHub>("/hubs/live-session").RequireAuthorization();`
- [ ] T068 [US5] Register `ILiveSessionNotifier → LiveSessionNotifier` and `ILiveKitRoomAdmin → LiveKitRoomAdmin` in `LiveSessionDI.cs` (extend T040)

**Checkpoint**: All five user stories fully functional. Feature complete — Phase 4 deliverable ready.

---

## Phase 8: Polish & Cross-Cutting Concerns

**Purpose**: Finalise the slice — verify performance targets, sharpen operational posture, and execute the quickstart end-to-end.

- [ ] T069 [P] Load test harness in `tests/Integration/LiveSession/PerformanceTests.cs` verifying SC-001 (token under 1s), SC-004 (transcript retrieval under 2s for 10k segments), and SC-010 (targets hold at 50 participants) against a WebApplicationFactory-hosted instance
- [ ] T070 [P] Structured-logging audit — every `ILiveKitTokenIssuer.Issue` call logs `(meetingId, userId, roleAtIssuance, expiresAtUtc)`; every rejected webhook logs `(reason, externalEventId?, meetingRoom?)`; no PII in logs (`Text` field of transcripts is NEVER logged at Information or above)
- [ ] T071 Run `quickstart.md` end-to-end against a fresh LiveKit Cloud sandbox project — seed a meeting, issue a token, join via the LiveKit web tester, speak, observe a transcript segment lands in the DB, retrieve via the endpoint, end the session, verify meeting transitions to Completed and `SessionEndedEvent` fires

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies — can start immediately
- **Foundational (Phase 2)**: Depends on Setup — BLOCKS all user stories
- **User Story 1 (Phase 3)**: Depends on Foundational. Independent of US2–US5
- **User Story 2 (Phase 4)**: Depends on Foundational. Independent of US1, US3, US4, US5 at the test level (shares `WebhookService` file with US4/US5 — sequence the US2 handler before US4/US5 extensions or merge carefully)
- **User Story 3 (Phase 5)**: Depends on Foundational. Tests can run against directly-seeded `TranscriptSegment` rows, so technically independent of US2; in practice ship after US2 for realistic end-to-end
- **User Story 4 (Phase 6)**: Depends on US2 (uses `WebhookService`, `SessionEvent` entity, signature validator) — **this is the one cross-story dependency**
- **User Story 5 (Phase 7)**: Depends on US2 (extends `WebhookService`) and US1 (extends `SessionService`)
- **Polish (Phase 8)**: Depends on all user stories

### User Story Dependencies

- **US1**: Foundational only
- **US2**: Foundational only
- **US3**: Foundational only (entity shared with US2, but US3 can test against seeded data)
- **US4**: US2 (reuses `WebhookService`, `ILiveKitWebhookValidator`, `SessionEvent` entity)
- **US5**: US2 + US1 (extends both service classes)

### Within Each User Story

- Tests MUST fail first, then implementation makes them pass (Constitution + template guidance)
- Models → Configurations → DbContext/migration → Services → Endpoints → DI registration
- `[P]` tasks within a story target different files and can run in parallel

### Parallel Opportunities

- All `[P]` Setup tasks (T002, T003) can run in parallel after T001
- All `[P]` Foundational tasks (T005, T008) can run in parallel after T004
- Within each user story, `[P]` tasks (mostly DTOs, entity classes, test files) can run in parallel
- US1 and US2 can be implemented by different developers in parallel after Foundational completes
- US3 can be implemented in parallel with US2 by seeding test data, or sequentially after US2 for realistic integration

---

## Parallel Example: User Story 1

```bash
# Write tests in parallel (different test files):
Task: "Unit test RolePermissionMappingTests in tests/Unit/LiveSession/RolePermissionMappingTests.cs"
Task: "Integration test JoinTokenTests in tests/Integration/LiveSession/JoinTokenTests.cs"

# Implementation: run these in parallel (different files, no interdependencies):
Task: "Create SessionPermissions value type in Features/LiveSession/Models/SessionPermissions.cs"
Task: "Create ILiveKitTokenIssuer in Features/LiveSession/Services/ILiveKitTokenIssuer.cs"
Task: "Create JoinTokenRequest in Features/LiveSession/Contracts/Requests/JoinTokenRequest.cs"
Task: "Create JoinTokenResponse in Features/LiveSession/Contracts/Responses/JoinTokenResponse.cs"
Task: "Create JoinTokenRequestValidator in Features/LiveSession/Validators/JoinTokenRequestValidator.cs"

# Then sequentially (each depends on the previous):
# SessionService (T018) → SessionController (T019) → GetJoinTokenEndpoint (T020) → DI (T021)
```

---

## Parallel Example: User Story 2

```bash
# Test files (parallel, different files):
Task: "WebhookIdempotencyTests in tests/Unit/LiveSession/WebhookIdempotencyTests.cs"
Task: "TranscriptDeduplicationTests in tests/Unit/LiveSession/TranscriptDeduplicationTests.cs"
Task: "WebhookIngestionTests in tests/Integration/LiveSession/WebhookIngestionTests.cs"

# Entity + enum scaffolding in parallel:
Task: "SessionEventType enum in Features/LiveSession/Models/SessionEventType.cs"
Task: "TranscriptSegment entity in Features/LiveSession/Models/TranscriptSegment.cs"
Task: "SessionEvent entity in Features/LiveSession/Models/SessionEvent.cs"
Task: "TranscriptSegmentIngestedEvent record fleshed out"

# Then EF configs (sequential per-file, but both configs can be parallel with each other):
# TranscriptSegmentConfiguration || SessionEventConfiguration → DbContext update → migration
```

---

## Implementation Strategy

### MVP First — Ship US1 + US2 + US3 together (all P1)

Phase 4's MVP is the full "join → transcribe → retrieve" loop. Ship:

1. Complete Phase 1: Setup
2. Complete Phase 2: Foundational (CRITICAL — blocks everything)
3. Complete Phase 3 (US1): participants can join live sessions
4. Complete Phase 4 (US2): transcription is captured and persisted
5. Complete Phase 5 (US3): transcripts can be retrieved
6. **STOP and VALIDATE**: run the quickstart end-to-end against a LiveKit Cloud sandbox
7. Ship MVP. Phase 6 AI pipeline (later phase) can already begin consuming `TranscriptSegmentIngestedEvent` and `SessionEndedEvent` as they fire.

### Incremental Delivery — Ship P2 stories next

8. Add US4 (lifecycle tracking) — meetings auto-transition status and reconciliation catches stuck meetings
9. Add US5 (notifications + transcription control) — clients get live UI updates; Hosts/CoHosts can pause for privacy asides

### Parallel Team Strategy

With two or three developers after Foundational completes:

- Developer A: US1 (Phase 3)
- Developer B: US2 (Phase 4) → then US4 (Phase 6)
- Developer C: US3 (Phase 5) — can start in parallel with seeded test data
- US5 (Phase 7) waits for US1 + US2 to land; whoever freed up takes it

---

## Notes

- [P] tasks = different files, no dependencies on other incomplete tasks in the same phase
- [Story] label maps every story-phase task to its user story for traceability
- Tests are mandatory per the project constitution, not optional
- Every task names an absolute file path (or project-relative path) so the next developer / agent has no ambiguity
- Commit after each task or logical group; keep commits focused per Constitution's Governance note
- Stop at any checkpoint (end of a Phase) to validate the story independently before starting the next
- When a later story extends a file created by an earlier story (`WebhookService`, `ISessionService`, `LiveSessionDI`), sequence the work — do not try to parallelize edits to the same file
- Spec edge case L103 ("last Host leaves mid-session → session continues, CoHosts retain moderation") is **platform-side behaviour**: LiveKit Cloud does not revoke CoHost grants when the Host disconnects, and our backend performs no role-demotion on `participant_left`. No backend code or test is required — documented here so future reviewers do not treat the absence of a test as a gap
