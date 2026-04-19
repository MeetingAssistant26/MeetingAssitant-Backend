# Implementation Plan: Realtime Session Pipeline

**Branch**: `004-realtime-pipeline` | **Date**: 2026-04-18 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `/specs/004-realtime-pipeline/spec.md`

## Summary

Deliver Phase 4 of MeetingAssistant: the realtime pipeline that lets a meeting's participants actually join a live audio/video session, captures live transcription into persistent storage, and keeps backend meeting state in sync with the session. The backend integrates with **LiveKit Cloud** as the managed realtime platform — it issues short-lived join credentials scoped to each participant's meeting role, receives webhook callbacks for room lifecycle and transcription events, persists transcript segments delivered by LiveKit Cloud's built-in STT agent, exposes a transcript retrieval endpoint, and surfaces session events to clients via tenant-scoped SignalR groups. No self-hosted WebRTC or STT infrastructure is introduced — the backend remains the single deployable unit, consistent with the constitution.

The implementation follows the established vertical slice architecture with partial controller pattern, Result-based error handling, EF Core global query filters for tenant isolation, FluentValidation for requests, and MediatR domain events for downstream subscribers (the Phase 6 post-meeting AI pipeline will consume `TranscriptSegmentIngestedEvent` and `SessionEndedEvent`).

## Technical Context

**Language/Version**: C# / .NET 10
**Primary Dependencies**: ASP.NET Core (Controllers + SignalR), EF Core (Npgsql), FluentValidation, MediatR, Mapster, **LiveKit Server SDK for .NET** (`Livekit.Server.Sdk`) for access-token generation and webhook signature verification
**Storage**: PostgreSQL via Npgsql with EF Core — new tables `TranscriptSegments` and `SessionEvents`
**Testing**: xUnit (integration + unit tests). WebApplicationFactory for endpoint integration tests. A thin `ILiveKitTokenIssuer` abstraction is used in tests to avoid calling LiveKit Cloud from the test suite; webhook signature verification is exercised against captured real payloads.
**Target Platform**: Linux server (containerized). The webhook endpoint (`POST /api/webhooks/livekit`) MUST be reachable from LiveKit Cloud's egress IPs.
**Project Type**: Web service (API + SignalR hub; no frontend in this repo)
**Performance Goals** (derived from spec Success Criteria):

- SC-001: Join credential issued in under 1s
- SC-002: Live captions surface in clients within 3s of speech end (caption delivery is handled by LiveKit Cloud data channels; backend target is <500ms for webhook → SignalR relay when applicable)
- SC-003: Meeting status reflects real session state within 5s of the platform event
- SC-004: Transcript retrieval (1 hour of speech) under 2s
- SC-010: All of the above hold at 50 concurrent participants

**Constraints**:

- All queries scoped to the active organization via JWT `organizationId` claim + EF Core global query filters (tenant isolation is structural, not advisory)
- RFC 7807 problem details for every error path, via `Result.ToProblem(correlationIdProvider)`
- SignalR messages sent **only** to `Clients.Group($"org:{organizationId}")`; no `Clients.All` broadcasts
- Webhook signature MUST be verified before any state change (reject on failure)
- Webhook handling MUST be idempotent (de-dup by LiveKit event id / segment sequence id)
- Join credentials valid for 15 minutes from issuance (Clarification)
- Transcription is English only in this phase (Clarification)
- Only Hosts and CoHosts may pause/resume transcription (Clarification)

**Scale/Scope**: Up to 50 concurrent participants per session (FR-021). Organization-level concurrent-session count is bounded by LiveKit Cloud's plan limits, not by backend code.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Status | Notes |
|-----------|--------|-------|
| I. Vertical Slice Architecture | PASS | All code under `Features/LiveSession/` with Models, Services, Validators, Endpoints, Contracts, Infrastructure, Hubs, Mapping, DI. |
| II. Partial Controller Pattern | PASS | 3 partial controllers (Session, Webhook, Transcript). Each action (join-token, pause-transcription, resume-transcription, receive webhook, get transcript) lives in its own endpoint file. |
| III. Tenant Isolation by Default | PASS | `TranscriptSegment` and `SessionEvent` both implement `IHasOrganizationId`. EF Core global query filters auto-applied. `[EnforceOrgAccess]` on all non-webhook controllers. The webhook endpoint does NOT trust org from the caller — it resolves `OrganizationId` by looking up the meeting referenced in the payload after signature verification. |
| IV. Strict Single Membership Rule | PASS | No changes to membership model. Join credentials derive permissions from the user's `MeetingParticipant` row under the user's active organization. |
| V. Standardized Operational Errors | PASS | All endpoints return RFC 7807 via `Result.ToProblem(correlationIdProvider)`. New `LiveSessionErrors` static class mirrors `MeetingErrors` / `OrganizationErrors` patterns. |
| Dev Constraint: .NET 10 + PostgreSQL | PASS | Same stack as Phases 1–3. Adds LiveKit Server SDK as a new dependency. |
| Dev Constraint: Automated tests | PASS | Integration tests for join-token issuance, webhook ingestion (signed + replayed + unsigned), transcript retrieval, and pause-authority enforcement. Unit tests for role→permission mapping and the idempotent webhook processor. |

**Gate Result**: ALL PASS. No violations to justify.

**Post-Design Re-Evaluation (after Phase 1 artifacts)**: Re-checked 2026-04-18 after writing `research.md`, `data-model.md`, and `contracts/`. Still ALL PASS. The Phase 1 design introduced one attention-worthy item — the webhook endpoint deliberately has no `[Authorize]` attribute (R-015) — which was identified up front in the table above and is mitigated by mandatory signature verification (FR-007, validator seam). No new principles are violated.

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
│   ├── webhook-endpoints.md
│   └── transcript-endpoints.md
├── checklists/
│   └── requirements.md  # From /speckit.specify
└── tasks.md             # Phase 2 output (/speckit.tasks)
```

### Source Code (repository root)

```text
MeetingAssistant/Features/
└── LiveSession/
    ├── Endpoints/
    │   ├── Session/
    │   │   ├── SessionController.cs
    │   │   ├── GetJoinTokenEndpoint.cs
    │   │   ├── PauseTranscriptionEndpoint.cs
    │   │   └── ResumeTranscriptionEndpoint.cs
    │   ├── Webhook/
    │   │   ├── WebhookController.cs
    │   │   └── LiveKitWebhookEndpoint.cs
    │   └── Transcript/
    │       ├── TranscriptController.cs
    │       └── GetTranscriptEndpoint.cs
    ├── Contracts/
    │   ├── Requests/
    │   │   └── JoinTokenRequest.cs
    │   └── Responses/
    │       ├── JoinTokenResponse.cs
    │       ├── TranscriptResponse.cs
    │       └── TranscriptSegmentResponse.cs
    ├── Models/
    │   ├── TranscriptSegment.cs
    │   ├── SessionEvent.cs
    │   ├── SessionEventType.cs
    │   ├── SessionPermissions.cs          # value record (CanPublish, CanSubscribe, CanModerate)
    │   └── Events/
    │       └── LiveSessionEvents.cs       # MediatR domain events
    ├── Services/
    │   ├── ISessionService.cs             # join-token issuance + pause/resume
    │   ├── SessionService.cs
    │   ├── ILiveKitTokenIssuer.cs         # test seam around LiveKit SDK
    │   ├── LiveKitTokenIssuer.cs
    │   ├── ILiveKitWebhookValidator.cs    # test seam for signature verification
    │   ├── LiveKitWebhookValidator.cs
    │   ├── IWebhookService.cs             # idempotent dispatch of validated events
    │   ├── WebhookService.cs
    │   ├── ITranscriptService.cs          # read-path + ingest-path
    │   └── TranscriptService.cs
    ├── Hubs/
    │   ├── LiveSessionHub.cs              # SignalR hub; group subscription on connect
    │   ├── ILiveSessionNotifier.cs
    │   └── LiveSessionNotifier.cs         # wraps IHubContext<LiveSessionHub>; sends to Group("org:{id}")
    ├── Infrastructure/
    │   └── Persistence/
    │       └── Configurations/
    │           ├── TranscriptSegmentConfiguration.cs
    │           └── SessionEventConfiguration.cs
    ├── Validators/
    │   └── JoinTokenRequestValidator.cs
    ├── Mapping/
    │   └── LiveSessionMappingConfig.cs
    └── LiveSessionDI.cs

MeetingAssistant/Shared/
└── Errors/
    └── LiveSessionErrors.cs               # Result error catalog for this feature

tests/
├── Integration/
│   └── LiveSession/
│       ├── JoinTokenTests.cs
│       ├── WebhookIngestionTests.cs
│       ├── TranscriptRetrievalTests.cs
│       └── TranscriptionControlTests.cs
└── Unit/
    └── LiveSession/
        ├── RolePermissionMappingTests.cs
        ├── WebhookIdempotencyTests.cs
        └── TranscriptDeduplicationTests.cs
```

**Structure Decision**: Follow the established vertical slice pattern (`Features/Meetings/`, `Features/Organizations/`). The LiveSession slice introduces one new cross-cutting component that previous slices did not have — a SignalR hub (`LiveSessionHub`) with a notifier abstraction (`ILiveSessionNotifier`) so services can push events without holding a direct reference to `IHubContext<T>` and so unit tests can assert on "what was notified" without spinning up a hub. Contracts live under `Contracts/Requests` and `Contracts/Responses` (matching Phase 3). Endpoint files are partial extensions of their controller (one action per file, per Constitution Principle II). `ILiveKitTokenIssuer` and `ILiveKitWebhookValidator` isolate the LiveKit Server SDK behind seams so unit tests do not need network access or LiveKit credentials.

## Complexity Tracking

> No constitution violations. No complexity justifications needed.

The LiveKit Server SDK and the SignalR hub are additions, but they do not violate any constitution principle — both are introduced because the feature's requirements genuinely demand them (realtime access tokens and tenant-scoped push notifications, respectively). They are wrapped in thin feature-local abstractions (`ILiveKitTokenIssuer`, `ILiveKitWebhookValidator`, `ILiveSessionNotifier`) so the rest of the slice stays testable without external dependencies.
