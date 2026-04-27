# Implementation Plan: Participant Audio Egress & Storage

**Branch**: `005-participant-audio-egress` | **Date**: 2026-04-26 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `specs/005-participant-audio-egress/spec.md`

## Summary

Phase 5 is an **integrity and completeness amendment** to the existing per-participant audio pipeline. The core infrastructure (webhook ingestion, `ParticipantAudioTrack` rows, `IngestParticipantAudioJob`, MinIO storage, `ParticipantAudioReadyEvent`) is already in place and operational. This phase hardens three gaps discovered during specification:

1. **Join barrier accuracy**: The current barrier counts remaining non-terminal tracks, which fires early if a participant never produces an egress event. The spec requires the expected count to be derived from `participant_joined` lifecycle events.
2. **Transfer retry resilience**: The current job has no retry ceiling. The spec requires 3 retries + an 8-minute hard ceiling to guarantee the 10-minute SLA.
3. **Operational visibility**: No queryable pipeline state exists. The spec requires administrators to inspect track statuses and readiness event state without reading raw job logs.

This phase introduces **zero new entities** and **zero new Hangfire jobs** — it amends existing code paths only.

## Technical Context

**Language/Version**: C# / .NET 10
**Primary Dependencies**: ASP.NET Core, EF Core 9 (Npgsql), Hangfire, MediatR, MinIO .NET SDK, LiveKit Server SDK for .NET (`Livekit.Server.Sdk`)
**Storage**: PostgreSQL via Npgsql with EF Core global query filters. MinIO (S3-compatible) for audio object storage.
**Testing**: xUnit + WebApplicationFactory + `FakeBackgroundJobClient` / `FakeStorageService` / `CollectingPublisher` test harnesses already in `tests/Integration/LiveSession/`.
**Target Platform**: Linux containers (docker-compose) for backend API + Postgres + MinIO.
**Project Type**: Web service (API + background jobs; no frontend).
**Performance Goals**:
- SC-001: 100% of tracks terminal within 10 minutes of webhook receipt
- SC-007: Pipeline state query returns in <500ms
**Constraints**:
- Constitution §III: EF Core Global Query Filters on all tenant-scoped entities.
- Constitution §V: RFC 7807 problem details via `Result.ToProblem(correlationIdProvider)`.
- No participant-facing endpoints (pipeline is internal only).
- Zero new migrations (all entities already exist).
**Scale/Scope**: Up to 50 concurrent participants per session (FR-019 from Phase 4).

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Status | Notes |
|---|---|---|
| I. Vertical Slice | **Pass** | All new/amended files land inside existing `Features/LiveSession/` slice. |
| II. Partial Controller Pattern | **N/A** | Phase 5 is internal pipeline only — no new controllers or endpoints. |
| III. Tenant Isolation by Default | **Pass** | `ParticipantAudioTrack` already implements `IHasOrganizationId`; global query filter applies. All new queries MUST include `OrganizationId` scoping. |
| IV. Strict Single Membership | **N/A** | Not exercised by this feature. |
| V. Standardized Operational Errors | **Pass** | No new error surfaces for participants; internal logging uses structured logging with correlation IDs. |
| Dev Constraint: .NET 10 + PostgreSQL | **Pass** | Existing stack. |
| Dev Constraint: Contract/Integration tests | **Pass** | Integration tests via `ParticipantAudioHandoffTests.cs` already cover the happy path. New tests must cover retry ceiling, late-arriving tracks, and query surface. |

**Gate Result**: ALL PASS.

## Project Structure

### Documentation (this feature)

```text
specs/005-participant-audio-egress/
├── plan.md              # This file
├── research.md          # Phase 0 output
├── data-model.md        # Phase 1 output — documents zero new entities
├── quickstart.md        # Phase 1 output — manual verification walkthrough
└── contracts/           # Phase 1 output — internal job contract only
```

### Source Code (repository root)

Amendments to existing files only — **no new files in this phase**.

```text
MeetingAssistant/Features/LiveSession/
├── Jobs/
│   └── IngestParticipantAudioJob.cs              # UPDATED — 3 retries + 8-min ceiling + auto-Fail
├── Services/
│   └── WebhookService.cs                         # NO CHANGE — expected count derivation is for `PipelineStateView` only; barrier gate remains in `IngestParticipantAudioJob`
├── Models/
│   └── ParticipantAudioTrack.cs                  # NO CHANGE (schema sufficient)
└── Infrastructure/Persistence/Configurations/
    └── ParticipantAudioTrackConfiguration.cs     # NO CHANGE
```

### Test Code

```text
tests/Integration/LiveSession/
└── ParticipantAudioHandoffTests.cs                 # UPDATED — retry ceiling, late-arriving track, missing participant
```

**Structure Decision**: Phase 5 is an amendment phase. It hardens existing paths rather than introducing new surfaces. All work is surgical edits to the 2 production files and 1 test file listed above.

## Complexity Tracking

> Not applicable — Constitution Check has zero violations and no new structural patterns are introduced.

---

## Phase 0: Research

### Unknowns Resolved

| Unknown | Resolution |
|---|---|
| How does the current join barrier determine "all tracks terminal"? | It counts `ParticipantAudioTrack` rows with non-terminal status. This is wrong per spec — it fires early if a participant never produces an egress. |
| How should the expected participant count be tracked? | `SessionEvents` table already records `participant_joined` events per meeting. The barrier should count expected participants from `SessionEvents WHERE EventType = ParticipantJoined` rather than counting `ParticipantAudioTrack` rows. |
| How to implement the 8-minute ceiling in a Hangfire job? | Hangfire `AutomaticRetry` attribute supports `Attempts = 3` and `DelayInSecondsByAttemptFunc`. The 8-minute ceiling is a custom guard: at job start, check `CreatedAt` on the `ParticipantAudioTrack` row; if `UtcNow - CreatedAt > 8 minutes`, skip the download and immediately mark `Failed`. |
| How to expose queryable pipeline state without a public endpoint? | An admin/debug service method `IPipelineStateService.GetStateAsync(meetingId)` that returns a plain DTO. No controller in this phase — the surface is internal-only, consumed by tests and (in a future phase) by an admin endpoint. |

### Decisions

- **Decision**: Join barrier expected count derived from `SessionEvents` (ParticipantJoined count), not from `ParticipantAudioTrack` rows.
  - **Rationale**: The spec explicitly states the expected count is derived from participants who joined the live session. `SessionEvents` already captures these events. No new table needed.
  - **Alternatives considered**: Adding a `ExpectedTrackCount` column to `Meeting` — rejected because it duplicates information already in `SessionEvents` and introduces a new write path.

- **Decision**: Retry ceiling implemented as an in-job time guard (`track.CreatedAt + 8 min`), not as a Hangfire job-level timeout.
  - **Rationale**: Hangfire does not have a native "max job duration" feature. The time guard is deterministic and testable. The job still retries on transient failures (network, MinIO unavailable) up to 3 times via `[AutomaticRetry]`.
  - **Alternatives considered**: External watchdog job that scans `Downloading` tracks older than 8 minutes — rejected as overly complex for this phase.

- **Decision**: Queryable pipeline state delivered as an internal service DTO, not a public REST endpoint.
  - **Rationale**: FR-010 requires "administrator" visibility, but this phase has no participant-facing surfaces. The queryable state is needed for tests and for a future admin/debug endpoint (Phase 4.5-style). A service seam is the right granularity.

## Phase 1: Design & Contracts

### Data Model — Zero New Entities

All entities required by this phase already exist. No migrations needed.

| Entity | Status | Changes |
|---|---|---|
| `ParticipantAudioTrack` | **Exists** | None. Schema is sufficient. |
| `SessionEvent` | **Exists** | Used to derive expected participant count (`EventType = ParticipantJoined`). |
| `Meeting` | **Exists** | Read-only reference for organization scoping. |

**New read-only DTO** (response-only, not an entity):

```
PipelineStateView
├── MeetingId: Guid
├── ExpectedParticipantCount: int          ← from SessionEvents ParticipantJoined count
├── Tracks: List<ParticipantAudioTrackView>
│   └── Each: { ParticipantUserId, Status, CreatedAt, TerminalAt?, StorageObjectKey?, SizeBytes? }
├── ReadyEventFired: bool                  ← from SessionEvents ParticipantAudioReady existence
└── AllTracksTerminal: bool                ← computed: Tracks.All(t => Available or Failed)
```

### Internal Contracts

#### Job Contract: `IngestParticipantAudioJob.RunAsync`

```csharp
// Already exists. Behavior amendments only:
// 1. On entry: if (DateTime.UtcNow - track.CreatedAt > 8 minutes) → mark Failed, return.
// 2. On transient failure: allow Hangfire AutomaticRetry (Attempts=3).
// 3. On terminal status (Available or Failed): call TryCreateParticipantAudioReadyEventAsync
//    with expected-count logic (SessionEvents ParticipantJoined count).
```

#### Service Contract: `IPipelineStateService`

```csharp
public interface IPipelineStateService
{
    Task<PipelineStateView?> GetStateAsync(Guid meetingId, CancellationToken ct);
}
```

No public HTTP contract in this phase.

### Quickstart Verification Steps

1. **Verify existing happy path still works**
   ```bash
   dotnet test --filter "FullyQualifiedName~ParticipantAudioHandoffTests"
   ```
   Expected: existing test passes unchanged.

2. **Verify join barrier with missing participant**
   - Seed a meeting with 3 participants who joined the session.
   - Process `egress_ended` for only 2 of them.
   - Assert: readiness event does **not** fire.
   - Process a late `egress_ended` (or manually set the 3rd track to Failed).
   - Assert: readiness event fires exactly once.

3. **Verify 8-minute ceiling**
   - Seed a track with `CreatedAt = UtcNow - 9 minutes`.
   - Run `IngestParticipantAudioJob`.
   - Assert: track is immediately marked `Failed` without attempting download.
   - Assert: if this was the last non-terminal track, readiness event fires.

4. **Verify queryable pipeline state**
   - Seed a meeting with 3 participants and 2 tracks (1 Available, 1 Pending).
   - Call `PipelineStateService.GetStateAsync`.
   - Assert: `ExpectedParticipantCount == 3`, `AllTracksTerminal == false`, `ReadyEventFired == false`.
   - Mark the pending track Failed, re-query.
   - Assert: `AllTracksTerminal == true`, `ReadyEventFired == true`.

## Phase 2: Tasks

Tasks will be generated by `/speckit.tasks` based on this plan. Expected task count: ~8 tasks (tests + implementation + verification).
