# Phase 0 Research: Participant Audio Egress & Storage

**Date**: 2026-04-26
**Source**: [spec.md](./spec.md) · [plan.md](./plan.md)

## Research Questions & Answers

### R-1: How should the join barrier determine the expected number of tracks?

**Context**: The spec requires the readiness event to fire when "all expected participant tracks" are terminal. The existing code counts remaining non-terminal `ParticipantAudioTrack` rows, which fires early if a participant never produces an egress event.

**Decision**: Derive the expected count from `SessionEvents` rows where `EventType = ParticipantJoined` for the meeting.

**Rationale**: 
- `SessionEvents` already captures every `participant_joined` lifecycle event per the Phase 4 webhook handler.
- No new table or write path needed.
- The count is deterministic: every participant who joined the live session is expected to have one audio track.
- Unrecognized participants in `egress_ended` webhooks do not affect this count (per FR-005 clarification).

**Alternatives considered**:
- Add `ExpectedAudioTrackCount` column to `Meeting` — rejected: duplicates information, requires new write path during session lifecycle, adds migration.
- Count `MeetingParticipants` rows — rejected: not all invited participants join the session; only those who actually joined (tracked by `participant_joined` events) are expected to have tracks.

---

### R-2: How to enforce the 8-minute hard ceiling on track transfers?

**Context**: The spec requires tracks to reach terminal state within 10 minutes (SC-001). An 8-minute ceiling gives 2 minutes of margin for the join barrier to evaluate and emit the readiness event.

**Decision**: Implement the ceiling as an in-job time guard: at the start of `IngestParticipantAudioJob.RunAsync`, compare `DateTime.UtcNow` against `track.CreatedAt + 8 minutes`. If exceeded, immediately mark `Failed` and skip the download/upload attempt.

**Rationale**:
- Hangfire does not provide a native "max job wall-clock time" feature.
- The guard is deterministic and testable: set `CreatedAt` to 9 minutes ago in a test, run the job, assert immediate failure.
- Transient failures (network, MinIO unavailable) are still retried via Hangfire's `[AutomaticRetry(Attempts = 3)]` attribute.
- The ceiling is a safety valve, not the primary success path.

**Alternatives considered**:
- External watchdog job scanning for `Downloading` tracks older than 8 minutes — rejected: adds a second job type and scheduling complexity for a simple time comparison.
- Hangfire `JobExpirationTimeout` — rejected: this controls how long Hangfire keeps job metadata, not how long the job method runs.

---

### R-3: How to expose queryable pipeline state without a public endpoint?

**Context**: FR-010 requires administrators to query pipeline state. This phase has no participant-facing surfaces.

**Decision**: Introduce an internal service seam `IPipelineStateService` with a single method `GetStateAsync(Guid meetingId, ct)` returning a `PipelineStateView` DTO. No controller or HTTP endpoint in this phase.

**Rationale**:
- The spec says "queryable" not "REST endpoint." A service method is queryable by tests and by a future admin/debug controller.
- Keeps Phase 5 focused on pipeline integrity, not UI/API surface.
- Phase 4.5 established the pattern of admin-only debug surfaces (`TranscriptController`). A future Phase 5.x or 6 admin endpoint can consume `IPipelineStateService`.

**Alternatives considered**:
- `GET /api/admin/pipeline-state/{meetingId}` endpoint — rejected: no admin role or auth policy exists for pipeline admin access; better deferred to a future phase that defines admin surfaces holistically.

---

### R-4: Retry policy integration with Hangfire

**Context**: FR-004 requires 3 retries with exponential backoff.

**Decision**: Use Hangfire's `[AutomaticRetry(Attempts = 3)]` on `IngestParticipantAudioJob.RunAsync`. Do not use `DelayInSecondsByAttemptFunc` because the default exponential backoff is sufficient for transient network/MinIO failures.

**Rationale**:
- Default Hangfire retry delays are roughly: attempt 1 (immediate), attempt 2 (~1 min), attempt 3 (~2 min). Total span ~3 minutes, well within the 8-minute ceiling.
- Custom delay functions add complexity without meaningful benefit for this domain.
- The 8-minute time guard (R-2) is the ultimate backstop; retry configuration is an optimization for transient failures.

---

## Research Artifacts

- **Existing code analyzed**: `IngestParticipantAudioJob.cs`, `WebhookService.cs`, `ParticipantAudioTrackConfiguration.cs`, `ParticipantAudioHandoffTests.cs`
- **Existing patterns verified**: `FakeBackgroundJobClient`, `FakeStorageService`, `CollectingPublisher`, `LiveSessionTestDb` test harnesses all reusable.
- **No new libraries or technologies needed**: All capabilities exist in the existing stack (Hangfire, EF Core, MinIO SDK).
