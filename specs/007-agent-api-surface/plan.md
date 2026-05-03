# Implementation Plan: Agent-Callable API Surface

**Branch**: `007-agent-api-surface` | **Date**: 2026-05-03 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `/specks/007-agent-api-surface/spec.md`

## Summary

Build an agent-facing REST API surface under `/api/agent/*` that allows an external LiveKit AI agent to query meeting context, manage reminders, and navigate the organization's meeting catalog during live meetings. This feature introduces a dedicated agent service-identity JWT auth scheme separate from human users, and exposes 10 endpoints (3 reminder + 7 context) over existing domain data from Phases 2, 3, and 5.6.

## Technical Context

**Language/Version**: C# / .NET 10  
**Primary Dependencies**: ASP.NET Core, EF Core (Npgsql), FluentValidation, Mapster, MediatR, LiveKit Server SDK (`Livekit.Server.Sdk`)  
**Storage**: PostgreSQL (pgvector for Meeting Memory in future phases), Redis (caching), MinIO (storage)  
**Testing**: xUnit + FluentAssertions + WebApplicationFactory for integration tests  
**Target Platform**: ASP.NET Core Web API (Linux Docker container)  
**Project Type**: web-service  
**Performance Goals**: <300ms p95 for all agent endpoints; <1s for meeting member queries (SC-001)  
**Constraints**: Partial Controller Pattern (one endpoint per file); tenant isolation via `OrganizationId` global query filters; no synchronous AI in request pipeline (constitution §9.2); agent JWT distinct from user JWT  
**Scale/Scope**: Multi-tenant; one active org per user; agent bound to single meeting per token  

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Gate | Status | Notes |
|------|--------|-------|
| Vertical Slice Architecture | ✅ Pass | Feature `AgentApi` with its own Endpoints, Models, Services, Validators under `Features/` |
| Partial Controller Pattern | ✅ Pass | 2 controllers (AgentReminder, AgentContext) split into 10 endpoint files + definition files |
| Tenant Isolation by Default | ✅ Pass | All queries filtered by `OrganizationId` from agent token claim; global query filter enforced |
| Strict Single Membership Rule | ✅ Pass | No changes to membership model; read-only usage of existing data |
| Standardized Operational Errors | ✅ Pass | All endpoints return `Result.ToProblem(correlationIdProvider)` on failure |
| Automated Contract/Integration Tests | ✅ Pass | Acceptance scenarios defined for every endpoint; will add integration tests |

**Re-check after Phase 1**: All gates remain ✅ — no violations introduced.

## Project Structure

### Documentation (this feature)

```text
specs/007-agent-api-surface/
├── plan.md              # This file
├── research.md          # Phase 0 output
├── data-model.md        # Phase 1 output
├── quickstart.md        # Phase 1 output
├── contracts/           # Phase 1 output
└── tasks.md             # Phase 2 output (speckit.tasks)
```

### Source Code (repository root)

```text
src/
├── Features/
│   └── AgentApi/
│       ├── Endpoints/
│       │   ├── Reminder/
│       │   │   ├── AgentReminderController.cs       # definition: route, ctor, deps
│       │   │   ├── CreateReminderEndpoint.cs        # POST /api/agent/meetings/{meetingId}/reminders
│       │   │   ├── ListMeetingRemindersEndpoint.cs  # GET  /api/agent/meetings/{meetingId}/reminders
│       │   │   ├── MarkReminderDeliveredEndpoint.cs # POST /api/agent/reminders/{id}/mark-delivered
│       │   │   └── CancelReminderEndpoint.cs        # DELETE /api/agent/reminders/{id}
│       │   │
│       │   └── Context/
│       │       ├── AgentContextController.cs        # definition: route, ctor, deps
│       │       ├── GetOrganizationEndpoint.cs       # GET /api/agent/organization
│       │       ├── GetMeetingMembersEndpoint.cs     # GET /api/agent/meetings/{meetingId}/members
│       │       ├── ListMeetingsEndpoint.cs          # GET /api/agent/meetings
│       │       ├── GetMeetingDetailEndpoint.cs      # GET /api/agent/meetings/{meetingId}
│       │       ├── ListRecurringMeetingsEndpoint.cs # GET /api/agent/meetings/recurring
│       │       └── ListMeetingTagsEndpoint.cs       # GET /api/agent/meeting-tags
│       │
│       ├── Models/
│       │   ├── Requests/
│       │   │   └── CreateAgentReminderRequest.cs
│       │   └── Responses/
│       │       ├── AgentMemberResponse.cs
│       │       ├── AgentMeetingResponse.cs
│       │       ├── AgentMeetingDetailResponse.cs
│       │       ├── AgentReminderResponse.cs
│       │       └── AgentOrganizationResponse.cs
│       │
│       ├── Services/
│       │   ├── IAgentAuthService.cs
│       │   ├── AgentAuthService.cs
│       │   ├── IAgentContextService.cs
│       │   └── AgentContextService.cs
│       │
│       └── Validators/
│           └── CreateAgentReminderRequestValidator.cs
│
├── Infrastructure/
│   ├── LLM/
│   ├── Embedding/
│   ├── Persistence/
│   ├── Caching/
│   └── Storage/
│
├── Shared/
│   └── ResultExtensions.cs
│
└── Program.cs
```

**Structure Decision**: Single ASP.NET Core backend with vertical slice features. Phase 5.7 adds a new `AgentApi` feature slice. No new infrastructure projects required — all dependencies (EF Core, JWT, FluentValidation, Mapster) already scaffolded in Phases 0–3.

## Complexity Tracking

> No constitution violations. All patterns align with existing project conventions.

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| N/A | N/A | N/A |

---

## Phase 0: Outline & Research

### Unknowns & Research Tasks

For this feature, all technical dependencies are already established in prior phases. No external research required. The only "unknowns" are internal design decisions:

1. **Agent JWT Issuer Configuration**: How to configure a second JWT bearer scheme in ASP.NET Core alongside the existing user-JWT scheme. → **Decision**: Use `AddAuthentication().AddJwtBearer("AgentScheme", ...)` with a separate validation key and `[Authorize(AuthenticationSchemes = "AgentScheme")]` or a custom policy that checks the `agent=true` claim.

2. **Agent Token Refresh Endpoint Design**: Where should the refresh endpoint live? → **Decision**: Add `POST /api/agent/refresh` as an additional endpoint on `AgentContextController` (or a dedicated `AgentAuthController` if cleaner). Accepts the current agent token, validates it, and issues a new one with the same claims.

3. **Rate Limiting Implementation**: Per-meeting rate limiting (100 req/min per `meetingId`). → **Decision**: Use ASP.NET Core Rate Limiting middleware with a custom `PartitionedRateLimiter` keyed on `meetingId` extracted from the agent token claim.

### Research Findings

No external research required. All technology choices are project-established:

| Decision | Rationale | Alternatives Considered |
|----------|-----------|------------------------|
| Separate JWT scheme for agents | Clean separation of user and service identity; prevents token confusion | Single scheme with claim discrimination (rejected — too risky for privilege escalation) |
| `AddJwtBearer` for agent auth | Standard ASP.NET Core pattern; no external dependencies | Custom middleware (rejected — unnecessary complexity) |
| In-memory rate limiting | Sufficient for single-instance deployment; can be replaced with Redis in future | Redis-backed from start (rejected — premature optimization for Phase 5.7) |

---

## Phase 1: Design & Contracts

### Data Model

No new entities are introduced in Phase 5.7. All data is read from or written to existing entities from prior phases:

**Read-Only (Context Endpoints)**:
- `Organization` → `AgentOrganizationResponse` (name, slug, member count)
- `Meeting` → `AgentMeetingResponse` / `AgentMeetingDetailResponse`
- `MeetingParticipant` + `ApplicationUser` + `UserOrgMembership` → `AgentMemberResponse`
- `MeetingTag` → tag catalog

**Read/Write (Reminder Endpoints)**:
- `Reminder` → created by agent, read by agent (public only), updated (mark delivered, cancel)

**Key Constraints**:
- `Reminder.Scope` must be `Public` for agent list queries (defence-in-depth)
- `Reminder.Channel` set to `Agent` when created by agent
- `Reminder.MeetingId` required for public/agent reminders
- `MeetingId` route param must match agent token `meetingId` claim

### Interface Contracts

**Agent Auth Contract**:
```csharp
// IAgentAuthService
Task<string> MintTokenAsync(Guid organizationId, Guid meetingId, TimeSpan lifetime);
Task<AgentTokenClaims> ValidateTokenAsync(string token);
Task<string> RefreshTokenAsync(string currentToken); // only if meeting still InProgress
```

**Agent Context Contract**:
```csharp
// IAgentContextService
Task<AgentOrganizationResponse> GetOrganizationAsync(Guid orgId);
Task<List<AgentMemberResponse>> GetMeetingMembersAsync(Guid orgId, Guid meetingId);
Task<PaginatedList<AgentMeetingResponse>> ListMeetingsAsync(Guid orgId, string status, int limit, int offset);
Task<AgentMeetingDetailResponse> GetMeetingDetailAsync(Guid orgId, Guid meetingId);
Task<List<AgentMeetingResponse>> ListRecurringMeetingsAsync(Guid orgId);
Task<List<MeetingTagResponse>> ListMeetingTagsAsync(Guid orgId);
```

**Agent Reminder Contract**:
```csharp
// IReminderService (extends Phase 5.6 interface)
Task<Reminder> CreateAgentReminderAsync(CreateAgentReminderRequest request, Guid orgId, Guid meetingId);
Task<List<AgentReminderResponse>> ListPublicMeetingRemindersAsync(Guid orgId, Guid meetingId, DateTime scheduledStartUtc);
Task MarkReminderDeliveredAsync(Guid reminderId, Guid orgId);
Task CancelReminderAsync(Guid reminderId, Guid orgId);
```

### API Contracts (`contracts/`)

See generated files in `contracts/agent-api-contracts.md` with full endpoint specifications.

---

## Implementation Phases

### Phase 1A: Agent Auth Infrastructure
- Add `AddJwtBearer("AgentScheme")` in `Program.cs`
- Define `AgentOnly` authorization policy
- Implement `IAgentAuthService` / `AgentAuthService`
- Add `IAgentContextProvider` to resolve `organizationId` and `meetingId` from agent token claims
- Add `POST /api/agent/refresh` endpoint

### Phase 1B: Context Endpoints (6 endpoints)
- Implement `AgentContextController` + endpoint files
- Implement `IAgentContextService` / `AgentContextService`
- Add response models and Mapster mappings
- Add meeting ID claim verification middleware/filter

### Phase 1C: Reminder Endpoints (4 endpoints)
- Extend `IReminderService` with agent-specific methods
- Implement `AgentReminderController` + endpoint files
- Add `CreateAgentReminderRequest` + FluentValidation validator
- Enforce `Scope=Public` hard filter in service layer

### Phase 1D: Rate Limiting
- Configure per-meeting rate limiter (100 req/min)
- Add `Retry-After` header on 429 responses
- Integration test rate limiting behavior

### Phase 1E: Integration Tests
- Agent token issuance/validation
- Cross-tenant isolation (agent for org A cannot read org B)
- Meeting ID mismatch → 403
- Personal reminder leakage prevention
- Reminder lifecycle: create → list → mark-delivered → cancel
- Rate limiting enforcement

---

## Deliverables

| Artifact | Path | Status |
|----------|------|--------|
| Plan | `specs/007-agent-api-surface/plan.md` | ✅ Complete |
| Research | `specs/007-agent-api-surface/research.md` | ✅ Complete (no external research needed) |
| Data Model | `specs/007-agent-api-surface/data-model.md` | ✅ Complete (no new entities) |
| Contracts | `specs/007-agent-api-surface/contracts/` | ✅ Complete |
| Quickstart | `specs/007-agent-api-surface/quickstart.md` | ⏳ To be generated |
