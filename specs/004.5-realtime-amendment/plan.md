# Implementation Plan: Opus Realtime Amendment (Phase 4.5)

**Branch**: Not created; artifacts live under `specs/005-opus-realtime-amendment/`  
**Date**: 2026-04-25  
**Spec**: [spec.md](./spec.md)  
**Input**: Feature specification from `specs/005-opus-realtime-amendment/spec.md`

## Summary

Phase 4.5 is a **scope-reduction amendment** to the realtime pipeline. Three things change:

1. **Live transcription is removed from the product surface.** The existing [LiveSessionNotifier](../../MeetingAssistant/Features/LiveSession/Hubs/LiveSessionNotifier.cs) already emits only lifecycle events (`session.started`, `session.ended`, `participant.joined`, `participant.left`) — the amendment forbids re-adding a transcription-status event. No code change is required in the notifier; the constraint is captured as an architectural decision + test coverage.
2. **A new administrator-only transcript debugging endpoint** is introduced under the existing `LiveSession` slice at [MeetingAssistant/Features/LiveSession/Endpoints/Transcript/](../../MeetingAssistant/Features/LiveSession/Endpoints/Transcript/). The endpoint returns `(segments, pipelineStatus)` where `pipelineStatus ∈ {NotStarted, Processing, Completed, Failed}`. Live meetings are rejected with `409 Conflict`.
3. **A forward-looking constraint on the post-meeting summarization step** is documented: when `SummarizeTranscriptJob` is introduced in Phase 6, it MUST be wired to `MeetingTranscriptReadyEvent` (Phase 5.5) and NOT to `MeetingEndedEvent`. No job or event is created in this phase.

Because no `TranscriptSegment` entity exists yet (it is a Phase 5.5 deliverable) and because FR-009 forbids adding new stored business records in this phase, this phase ships the endpoint and contract behind an `ITranscriptReadService` seam. Today the implementation is a stub that returns `NotStarted` + empty for every ended meeting; Phase 5.5 replaces the stub with a real reader backed by the forthcoming `TranscriptSegment` table. This is explicitly acknowledged as a deliberate design choice, not a violation: the seam is internal, not a new stored record.

## Technical Context

**Language/Version**: C# / .NET 10  
**Primary Dependencies**: ASP.NET Core (Controllers + SignalR), EF Core 9 (Npgsql), FluentValidation, MediatR, Mapster, `Livekit.Server.Sdk.Dotnet`  
**Storage**: PostgreSQL via Npgsql + EF Core. This phase adds **no new entities, tables, or migrations.**  
**Testing**: xUnit (`MeetingAssistant.Tests.Integration`, `MeetingAssistant.Tests.Unit`) + `WebApplicationFactory` + Testcontainers pattern used by sibling suites under [tests/Integration/LiveSession/](../../tests/Integration/LiveSession/).  
**Target Platform**: Linux containers (docker-compose) for the backend API + Postgres + MinIO; Windows/Linux dev machines.  
**Project Type**: Web service (single backend project; no frontend in this repo).  
**Performance Goals**: No performance delta is targeted. The new endpoint is an admin-only debug surface expected at single-digit RPS from internal tooling; it MUST NOT regress existing lifecycle and join-token paths.  
**Constraints**:
- FR-009 forbids new stored business records, participant-facing workflows, new background jobs, and participant-facing endpoints in this phase.
- Q3 clarification forbids introducing any audit-record entity or write path.
- FR-007 requires `(segments, pipelineStatus)` on every successful response; empty list without a status is not a legal response shape.
- FR-012 requires rejecting requests for `InProgress` meetings with an explicit signal — implemented as `409 Conflict` with an RFC 7807 problem response per constitution §V.
- Tenant isolation per constitution §III: the endpoint route MUST be nested under `{orgId}` and respect EF Core Global Query Filters.
- Partial controller pattern per constitution §II: one endpoint per file.

**Scale/Scope**: Single endpoint, one service seam, one response DTO, one status enum. Zero new entities/migrations/jobs. Integration tests covering 4 admin states + non-admin denial + in-progress rejection = ~6 scenarios.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Status | Notes |
|---|---|---|
| I. Vertical Slice | Pass | All new files land inside the existing `Features/LiveSession/` slice (`Endpoints/Transcript/`, `Contracts/Responses/`, `Services/`). No technical grouping introduced. |
| II. Partial Controller Pattern | Pass | `TranscriptController.cs` defines base route + DI; `GetTranscriptEndpoint.cs` contains the single action method. |
| III. Tenant Isolation by Default | Pass | Route is `api/organizations/{orgId:guid}/meetings/{meetingId:guid}/transcript`; `[EnforceOrgAccess]` filter applied as on [SessionController](../../MeetingAssistant/Features/LiveSession/Endpoints/Session/SessionController.cs); Meeting lookup goes through EF Core Global Query Filters. |
| IV. Strict Single Membership | Pass | Not exercised by this feature (no membership state changes). |
| V. Standardized Operational Errors | Pass | All failure paths return `Result.ToProblem(_correlationIdProvider)` → RFC 7807 problem details. The `409 Conflict` for in-progress meetings is returned via the same `Result` → `ToProblem` pipeline. |
| Dev Constraint: .NET 10 + Postgres/Npgsql | Pass | Uses existing stack. |
| Dev Constraint: Contract/Integration tests | Pass | Integration tests are the acceptance surface (see Phase 1 Quickstart + the tests enumerated in Summary). |

No violations. Complexity Tracking table omitted.

## Project Structure

### Documentation (this feature)

```text
specs/005-opus-realtime-amendment/
├── plan.md              # This file
├── research.md          # Phase 0 output
├── data-model.md        # Phase 1 output — documents zero new entities + one response-only enum
├── quickstart.md        # Phase 1 output — manual verification walkthrough
├── contracts/
│   └── transcript-endpoint.md   # HTTP contract for GET /transcript
├── checklists/
│   └── requirements.md  # From /speckit.specify
└── tasks.md             # Created by /speckit.tasks (not by this command)
```

### Source Code (repository root)

```text
MeetingAssistant/
└── Features/
    └── LiveSession/
        ├── Contracts/
        │   └── Responses/
        │       ├── TranscriptDebugResponse.cs          # NEW — { segments, pipelineStatus }
        │       ├── TranscriptSegmentView.cs            # NEW — read-only view record; NOT an entity
        │       └── TranscriptPipelineStatus.cs         # NEW — response-only enum
        ├── Endpoints/
        │   └── Transcript/                             # NEW folder
        │       ├── TranscriptController.cs             # NEW — partial base: route + DI
        │       └── GetTranscriptEndpoint.cs            # NEW — single action method
        ├── Services/
        │   ├── ITranscriptReadService.cs               # NEW — seam; ReadAsync(orgId, meetingId, ct)
        │   └── StubTranscriptReadService.cs            # NEW — returns NotStarted + empty; Phase 5.5 replaces
        └── LiveSessionDI.cs                            # UPDATED — register ITranscriptReadService

tests/
└── MeetingAssistant.Tests.Integration/
    └── LiveSession/
        └── TranscriptDebugEndpointTests.cs             # NEW — 6 integration scenarios
```

**Structure Decision**: Keep everything inside the existing `LiveSession` slice. A new slice for "Transcript" would violate the spec's "no new participant-facing workflow" boundary (it would imply the transcript is its own domain) and split the transcript read path from the realtime domain it belongs to. When Phase 5.5 adds the real `TranscriptSegment` entity + repository, those can also live inside `LiveSession/` or move to a dedicated `Transcription/` slice if that phase prefers — that decision is deferred.

## Complexity Tracking

> Not applicable — Constitution Check has zero violations.
