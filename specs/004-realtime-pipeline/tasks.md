---
description: "Task list for feature 004-realtime-pipeline"
---

# Tasks: Realtime Session Pipeline

**Input**: Design documents from `/specs/004-realtime-pipeline/`
**Prerequisites**: `plan.md`, `spec.md`, `research.md`, `data-model.md`, `contracts/`, `quickstart.md`

## Phase 1: Setup

- [X] T001 Add LiveKit, Hangfire, and MinIO package dependencies.
- [X] T002 Add LiveKit and Storage configuration keys in app settings.
- [X] T003 Create `Features/LiveSession` folder structure.

## Phase 2: Foundational

- [X] T004 Add `LiveSessionErrors` entries.
- [X] T005 Add `LiveKitOptions` binding.
- [X] T006 Add `AddLiveSessionFeature` DI extension.
- [X] T007 Wire LiveSession DI, SignalR, and Hangfire registration.
- [X] T008 Add base live-session domain event definitions.

## Phase 3: User Story 1 - Join Token (P1)

### Tests

- [X] T009 `tests/Unit/LiveSession/RolePermissionMappingTests.cs`
- [X] T010 `tests/Integration/LiveSession/JoinTokenTests.cs`

### Implementation

- [X] T011 `SessionPermissions` role mapping.
- [X] T012 `ILiveKitTokenIssuer`.
- [X] T013 `LiveKitTokenIssuer`.
- [X] T014 `JoinTokenRequest`.
- [X] T015 `JoinTokenResponse`.
- [X] T016 `JoinTokenRequestValidator`.
- [X] T017 `ISessionService`.
- [X] T018 `SessionService`.
- [X] T019 `SessionController`.
- [X] T020 `GetJoinTokenEndpoint`.
- [X] T021 Register join-token services in `LiveSessionDI`.

## Phase 4: User Story 3 - Participant Audio Handoff (P1)

**Goal**: LiveKit `egress_ended` fan-out creates one `ParticipantAudioTrack` per participant file, ingests each source URL into MinIO, and emits `ParticipantAudioReadyEvent` exactly once when all tracks are terminal (`Available` or `Failed`).

### Tests

- [X] T022 `tests/Unit/LiveSession/WebhookIdempotencyTests.cs` - duplicate webhook idempotency.
- [X] T023 `tests/Unit/LiveSession/IngestParticipantAudioJobTests.cs` - ingest status transitions, idempotent rerun, failure semantics.
- [X] T024 `tests/Integration/LiveSession/ParticipantAudioHandoffTests.cs` - N participants -> N tracks -> N ingest jobs -> single ready event.
- [X] T053 `tests/Unit/LiveSession/JoinBarrierIdempotencyTests.cs` - concurrent terminal transitions dispatch one ready event.

### Implementation

- [X] T025 Extend `SessionEventType` with `ParticipantAudioReady`.
- [X] T026 Replace `RecordingStatus` with `ParticipantAudioTrackStatus` (`Pending`, `Downloading`, `Available`, `Failed`).
- [X] T027 Keep `SessionEvent` idempotency gate and enforce unique `(MeetingId, EventType)`.
- [X] T028 Replace `Recording` with `ParticipantAudioTrack` model.
- [X] T029 Add `ParticipantAudioTrack` persistence config and indexes.
- [X] T030 Register `DbSet<ParticipantAudioTrack>`.
- [X] T031 Add migration for `ParticipantAudioTracks` and session-event uniqueness.
- [X] T032 Keep webhook signature validation seam.
- [X] T033 Reuse `IStorageService.UploadFromUrlAsync` for ingestion.
- [X] T034 Replace `DownloadRecordingJob` with `IngestParticipantAudioJob(Guid trackId, string egressSourceUrl, CancellationToken ct)`.
- [X] T035 Update `WebhookService` egress processing to iterate all `FileResults[]` and enqueue per-track ingest.
- [X] T036 Keep webhook endpoint and persistence idempotency behavior.

## Phase 5: User Story 4 - Session Lifecycle Tracking (P2)

### Tests

- [X] T040 `tests/Integration/LiveSession/LifecycleWebhookTests.cs`.

### Implementation

- [X] T041 Keep `SessionStartedEvent` and `SessionEndedEvent` definitions.
- [X] T042 Keep lifecycle webhook transitions for room and participant events.

## Phase 5.5: Post-Meeting Transcript + Summary (P1)

**Goal**: After participant audio readiness, run per-track STT and meeting summarization in Hangfire jobs; persist output as meeting-scoped `MeetingTranscript` and `MeetingSummary` records.

### Tests

- [X] T054 `tests/Integration/LiveSession/PostMeetingPipelineTests.cs` - ready event -> transcript -> summary pipeline.
- [X] T055 `tests/Unit/LiveSession/SttChunkingTests.cs` - chunk offset timestamps stay absolute.
- [X] T056 `tests/Unit/LiveSession/PartialFailureTests.cs` - partial STT failure still yields transcript + summary.

### Implementation

- [X] T057 Add `MeetingTranscript` entity (unique `MeetingId`, `FullText`, `SegmentsJson`, `SttModel`, `GeneratedAtUtc`).
- [X] T058 Add `MeetingSummary` entity (unique `MeetingId`, `SummaryText`, `LlmModel`, token usage, `GeneratedAtUtc`).
- [X] T059 Add migration for `MeetingTranscripts` and `MeetingSummaries`.
- [X] T060 Add and bind `OpenAiCompatibleOptions` (`Stt`, `Llm`).
- [X] T061 Implement `ISttService` / `SttService` with MinIO streaming and chunk fallback.
- [X] T062 Implement `ISummarizerService` / `SummarizerService` using OpenAI-compatible chat completions.
- [X] T063 Add `ParticipantAudioReadyEvent` and `MeetingTranscriptReadyEvent` enqueue handlers.
- [X] T064 Implement `GenerateMeetingTranscriptJob` with bounded parallel STT and transcript upsert.
- [X] T065 Implement `GenerateMeetingSummaryJob` with summary upsert.
- [X] T066 Register new services, options, clients, and jobs in `LiveSessionDI`.

## Phase 6: User Story 5 - Tenant-Scoped Notifications (P2)

### Tests

- [X] T043 `tests/Integration/LiveSession/TenantScopedNotificationTests.cs`.

### Implementation

- [X] T044 `LiveSessionHub` organization group behavior.
- [X] T045 `ILiveSessionNotifier` lifecycle-only notification surface.
- [X] T046 `LiveSessionNotifier` implementation.
- [X] T047 Webhook lifecycle events dispatch tenant-scoped notifications.
- [X] T048 Map `/hubs/live-session` endpoint.
- [X] T049 Register notifier in DI.

## Phase 7: Polish

- [X] T050 `tests/Integration/LiveSession/PerformanceTests.cs` load/perf checks.
- [X] T051 Logging audit for join token, webhook validation, and ingest jobs.
- [ ] T052 Run `quickstart.md` against a fresh LiveKit Cloud sandbox and collect evidence.

## Notes

- Live transcription and live captions are out of scope for Phase 4/4.5.
- Egress mode is track-based and audio-only (no video).
- Persistence model for post-meeting artifacts is `MeetingTranscript` + `MeetingSummary` (1:1 with `Meeting`, overwrite-on-regenerate).
