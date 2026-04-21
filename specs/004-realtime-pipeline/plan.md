# Implementation Plan: Realtime Session Pipeline

**Branch**: `004-realtime-pipeline` | **Date**: 2026-04-18 | **Last Updated**: 2026-04-21 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `/specs/004-realtime-pipeline/spec.md`

## Summary

Deliver Phase 4 of MeetingAssistant: the realtime pipeline that lets a meeting's participants join a live audio/video session and lands the session's recording in MinIO for Phase 6 to pick up. The backend integrates with **LiveKit Cloud** as the managed realtime platform — it issues short-lived join credentials scoped to each participant's meeting role, receives webhook callbacks for room and recording lifecycle, tracks meeting state, and surfaces session events to clients via tenant-scoped SignalR groups. No self-hosted WebRTC or STT infrastructure is introduced — the backend remains the single deployable unit, consistent with the constitution.

> **MVP simplification (2026-04-21)**: Phase 4's recording pipeline is deliberately minimal.
>
> ```
> LiveKit Egress → egress_ended webhook → DownloadRecordingJob → MinIO → Phase 6 (WhisperX)
> ```
>
> - One entity: `Recording` (`Id`, `MeetingId`, `FilePath`, `Status`).
> - One background job: `DownloadRecordingJob`.
> - One essential webhook for recording: `egress_ended` (`recording_started` is optional).
> - **No domain events** for recording lifecycle. **No reconciliation job.** **No event bus between Phase 4 and Phase 6.**
> - **MinIO is the integration boundary**: Phase 6 reads files from MinIO using `Recording.FilePath`.
>
> The authoritative transcript is produced in Phase 6 by WhisperX (or equivalent offline STT) applied to the MinIO object. Live captions during the meeting are delivered by the realtime platform directly to clients; the backend is never on the caption path.

The implementation follows the established vertical slice architecture with partial controller pattern, Result-based error handling, EF Core global query filters for tenant isolation, and FluentValidation for requests. MediatR domain events are still used for **session lifecycle** notifications (`SessionStartedEvent`, `SessionEndedEvent`) where they already fit the existing pattern; they are **not** used for recording hand-off.

## Technical Context

**Language/Version**: C# / .NET 10
**Primary Dependencies**: ASP.NET Core (Controllers + SignalR), EF Core (Npgsql), FluentValidation, MediatR, Mapster, **LiveKit Server SDK for .NET** (`Livekit.Server.Sdk`) for access-token generation and webhook signature verification, **Hangfire** for the recording download job, **MinIO .NET client** for object storage
**Storage**: PostgreSQL via Npgsql with EF Core — new tables `SessionEvents` and `Recordings`. MinIO (S3-compatible) for the recording binary; the backend persists only the object key in `Recording.FilePath`.
**Testing**: xUnit (integration + unit tests). WebApplicationFactory for endpoint integration tests. A thin `ILiveKitTokenIssuer` abstraction is used in tests to avoid calling LiveKit Cloud from the test suite. The download job is tested against a fake HTTP handler for the cloud URL and a test MinIO instance (or mocked `IStorageService`).
**Target Platform**: Linux server (containerized). The webhook endpoint (`POST /api/webhooks/livekit`) MUST be reachable from LiveKit Cloud's egress IPs.
**Project Type**: Web service (API + SignalR hub; no frontend in this repo)
**Performance Goals** (derived from spec Success Criteria):

- SC-001: Join credential issued in under 1s
- SC-003: Meeting status reflects real session state within 5s of the platform event
- SC-004: Recording lands in MinIO with `Status = Completed` within 10 min of `egress_ended` receipt
- SC-010: All of the above hold at 50 concurrent participants

**Constraints**:

- All queries scoped to the active organization via JWT `organizationId` claim + EF Core global query filters (tenant isolation is structural, not advisory)
- RFC 7807 problem details for every error path, via `Result.ToProblem(correlationIdProvider)`
- SignalR messages sent **only** to `Clients.Group($"org:{organizationId}")`; no `Clients.All` broadcasts
- Webhook signature MUST be verified before any state change (reject on failure)
- Webhook handling MUST be idempotent (de-dup by LiveKit event id for `SessionEvents`; the download job additionally checks `Recording.Status` to avoid re-downloading)
- Join credentials valid for 15 minutes from issuance (Clarification)
- **Backend MUST NOT be on the live-caption path** — no endpoint, service, or background task in this phase ingests, relays, or stores caption text
- **Phase 4 MUST NOT publish recording-related domain events.** Phase 6 reads from MinIO, not from an event bus.

**Scale/Scope**: Up to 50 concurrent participants per session (FR-019). Organization-level concurrent-session count is bounded by LiveKit Cloud's plan limits, not by backend code.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Status | Notes |
|-----------|--------|-------|
| I. Vertical Slice Architecture | PASS | All code under `Features/LiveSession/` with Models, Services, Validators, Endpoints, Contracts, Infrastructure, Hubs, Jobs, Mapping, DI. |
| II. Partial Controller Pattern | PASS | 2 partial controllers (Session, Webhook). Each action lives in its own endpoint file. |
| III. Tenant Isolation by Default | PASS | `SessionEvent` and `Recording` both implement `IHasOrganizationId`. EF Core global query filters auto-applied. `[EnforceOrgAccess]` on all non-webhook controllers. The webhook endpoint does NOT trust org from the caller — it resolves `OrganizationId` by looking up the meeting referenced in the payload after signature verification. |
| IV. Strict Single Membership Rule | PASS | No changes to membership model. Join credentials derive permissions from the user's `MeetingParticipant` row under the user's active organization. |
| V. Standardized Operational Errors | PASS | All endpoints return RFC 7807 via `Result.ToProblem(correlationIdProvider)`. `LiveSessionErrors` mirrors existing patterns. |
| Dev Constraint: .NET 10 + PostgreSQL | PASS | Same stack as Phases 1–3. Adds LiveKit Server SDK, MinIO client, and Hangfire. |
| Dev Constraint: Automated tests | PASS | Integration tests for join-token issuance, webhook ingestion (signed + replayed + unsigned), and recording handoff. Unit tests for role→permission mapping, webhook idempotency, and the download job. |

**Gate Result**: ALL PASS.

## Project Structure

### Documentation (this feature)

```text
specs/004-realtime-pipeline/
├── plan.md              # This file
├── research.md          # Phase 0 output
├── data-model.md        # Phase 1 output
├── quickstart.md        # Phase 1 output
├── contracts/           # Phase 1 output
│   ├── session-endpoints.md
│   └── webhook-endpoints.md
├── checklists/
│   └── requirements.md
└── tasks.md             # Phase 2 output
```

> `contracts/transcript-endpoints.md` remains only as a forwarding marker — Phase 4 does not expose any transcript endpoint.

### Source Code (repository root)

```text
MeetingAssistant/Features/
└── LiveSession/
    ├── Endpoints/
    │   ├── Session/
    │   │   ├── SessionController.cs
    │   │   └── GetJoinTokenEndpoint.cs
    │   └── Webhook/
    │       ├── WebhookController.cs
    │       └── LiveKitWebhookEndpoint.cs
    ├── Contracts/
    │   ├── Requests/
    │   │   └── JoinTokenRequest.cs
    │   └── Responses/
    │       └── JoinTokenResponse.cs
    ├── Models/
    │   ├── SessionEvent.cs
    │   ├── SessionEventType.cs
    │   ├── Recording.cs
    │   ├── RecordingStatus.cs
    │   ├── SessionPermissions.cs
    │   └── Events/
    │       └── LiveSessionEvents.cs       # SessionStartedEvent, SessionEndedEvent only
    ├── Services/
    │   ├── ISessionService.cs             # join-token issuance
    │   ├── SessionService.cs
    │   ├── ILiveKitTokenIssuer.cs
    │   ├── LiveKitTokenIssuer.cs
    │   ├── ILiveKitWebhookValidator.cs
    │   ├── LiveKitWebhookValidator.cs
    │   ├── IWebhookService.cs             # idempotent dispatch of validated events
    │   ├── WebhookService.cs
    │   ├── IStorageService.cs             # MinIO wrapper
    │   └── StorageService.cs
    ├── Jobs/
    │   └── DownloadRecordingJob.cs        # the ONE recording job
    ├── Hubs/
    │   ├── LiveSessionHub.cs
    │   ├── ILiveSessionNotifier.cs
    │   └── LiveSessionNotifier.cs
    ├── Infrastructure/
    │   ├── LiveKitOptions.cs
    │   └── Persistence/
    │       └── Configurations/
    │           ├── SessionEventConfiguration.cs
    │           └── RecordingConfiguration.cs
    ├── Validators/
    │   └── JoinTokenRequestValidator.cs
    ├── Mapping/
    │   └── LiveSessionMappingConfig.cs
    └── LiveSessionDI.cs

MeetingAssistant/Shared/
└── Errors/
    └── LiveSessionErrors.cs

tests/
├── Integration/
│   └── LiveSession/
│       ├── JoinTokenTests.cs
│       ├── WebhookIngestionTests.cs
│       ├── RecordingHandoffTests.cs
│       ├── LifecycleWebhookTests.cs
│       └── TenantScopedNotificationTests.cs
└── Unit/
    └── LiveSession/
        ├── RolePermissionMappingTests.cs
        ├── WebhookIdempotencyTests.cs
        └── DownloadRecordingJobTests.cs
```

**Structure Decision**: Follow the established vertical slice pattern (`Features/Meetings/`, `Features/Organizations/`). Compared with the previous revision, the `Jobs/` folder now contains a single file (`DownloadRecordingJob.cs`); there is no `SessionReconciliationJob` and no `RecordingService` — the download job is simple enough to manage the `Recording` row directly via the DbContext. If row-manipulation grows, it can be extracted to a service later without changing the public surface.

> **Phase 6 integration**: Phase 6's `SummarizeTranscriptJob` (or equivalent) reads the `Recording` row for a meeting, finds the `FilePath` object key, fetches the bytes from MinIO via the same `IStorageService` abstraction, and runs WhisperX. Phase 4 does not signal Phase 6; Phase 6 is triggered by its own mechanism (e.g., a scheduled scan or a Phase 6-owned trigger from the meeting-completion flow). The surface between phases is the MinIO object plus the `Recording.FilePath` row — nothing more.

## Complexity Tracking

> No constitution violations. No complexity justifications needed.

The LiveKit Server SDK, Hangfire, and MinIO are additions, but each is introduced because the feature's requirements genuinely demand it. The recording pipeline was deliberately reduced to the minimum shape that still satisfies FR-013 / FR-014 / SC-004; every piece of complexity that could be pushed to Phase 6 or out of scope was removed.
