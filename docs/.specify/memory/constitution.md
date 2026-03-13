<!--
  Sync Impact Report
  ==================
  Version change: 0.0.0 (unfilled template) → 1.0.0
  Modified principles:
    - [PRINCIPLE_1_NAME] → I. Foundational Principles (NEW)
    - [PRINCIPLE_2_NAME] → II. Domain Design Rules (NEW)f
    - [PRINCIPLE_3_NAME] → III. Event-Driven Architecture (NEW)
    - [PRINCIPLE_4_NAME] → IV. Realtime Architecture (NEW)
    - [PRINCIPLE_5_NAME] → V. Security Rules (NEW)
  Added sections:
    - Operational, Quality & Coding Standards (from user section 9.1–9.3)
    - Deployment & Evolution Strategy (from user section 9.4–9.5)
    - Governance (new)
  Removed sections: none
  Templates requiring updates:
    - .specify/templates/plan-template.md      ✅ no update needed (generic constitution ref)
    - .specify/templates/spec-template.md      ✅ no update needed (no constitution refs)
    - .specify/templates/tasks-template.md     ✅ no update needed (no constitution refs)
    - .specify/templates/checklist-template.md ✅ no update needed (no constitution refs)
    - .github/agents/*.md                      ✅ no update needed (generic constitution path refs)
    - .github/prompts/*.md                     ✅ no update needed (generic constitution path refs)
  Follow-up TODOs: none
-->

# AI-Powered Meeting Assistant Constitution

**.NET 10 — Feature-Based Modular Monolith**

## Core Principles

### I. Foundational Principles

The system MUST be implemented as a **Feature-Based Modular Monolith**:

- Single deployable unit.
- Single PostgreSQL database.
- Event-driven internal design.
- Microservices are explicitly OUT OF SCOPE.

The codebase MUST follow feature-based modular structure:

```text
src/
├── Features/
├── Infrastructure/
├── Shared/
└── Program.cs
```

Each feature owns its own Endpoints, Services, DTOs, Validators, and
Domain Events. Cross-feature calls MUST NOT invoke services directly.
All cross-feature communication MUST occur through domain events or
explicit integration contracts.

**Endpoint Architecture (Partial Controller Pattern)**:

All API endpoints MUST use ASP.NET Controllers with the Partial
Controller Pattern. Minimal APIs MUST NOT be used.

- Each controller is split into a **definition file** and **endpoint files**.
- The definition file (`{Name}Controller.cs`) contains: namespace,
  `[ApiController]`, `[Route]`, base class (`ControllerBase`),
  constructor, and shared dependencies. It contains NO action methods.
- Each endpoint file (`{Action}Endpoint.cs`) contains exactly **one**
  action method in a `partial class`. It MUST NOT contain constructor,
  `[ApiController]`, `[Route]`, base class declaration, or field
  declarations.
- Namespace MUST match folder path:
  `MeetingAssistant.Features.{FeatureName}.Endpoints.{Group}`
- Controllers MUST be grouped by domain responsibility (max 4–5
  endpoints per controller).
- All async actions MUST accept `CancellationToken` as last parameter.
- Controllers MUST stay thin — delegate to Services immediately.
- **Error responses**: Endpoints MUST use `result.ToProblem(correlationIdProvider)` 
  (`Shared/ResultExtensions.cs`) to convert failed `Result` objects
  into `ObjectResult` containing `StandardErrorResponse` with
  `CorrelationId` from `ICorrelationIdProvider`. Endpoints
  MUST NOT manually construct `StandardErrorResponse`. The global
  exception middleware handles unexpected exceptions; `ToProblem()`
  handles expected business failures.
- Object mapping uses **Mapster**.
- Input validation uses **FluentValidation**.

**Technology Stack (Non-Negotiable)**:

| Layer | Technology |
|-------|-----------|
| Backend | .NET 10, ASP.NET Controllers (Partial Controller Pattern), ASP.NET Identity + JWT, Hangfire, SignalR, Mapster |
| Database | PostgreSQL (primary), JSONB, pgvector (active — Meeting Memory RAG) |
| Cache | Redis |
| Storage | MinIO (meeting recordings) |
| Realtime Media | LiveKit Cloud (managed WebRTC — no self-hosted server) |
| AI | External AI services via `ILLMService` + `IEmbeddingService` abstractions (OpenAI API standard); backend is orchestrator only — no AI model logic inside .NET |

### II. Domain Design Rules

**Multi-Tenancy**: The system uses shared-database multi-tenancy. All
organization-scoped entities MUST include `OrganizationId` (Guid).
Data MUST always be filtered by `OrganizationId`. No
cross-organization data leakage is permitted.

**Simplified Membership Model**: Each user may have **only one active
organization membership** at a time. The `UserOrgMembership` table
MUST be preserved — it stores membership metadata required by
downstream features (org role, job role, context for AI, enabled
status). The database MUST enforce a uniqueness constraint:
`UNIQUE(user_id) WHERE is_enabled = true` on `UserOrgMembership`.
A user may leave one organization and join another, but MUST NOT
belong to multiple organizations simultaneously.

**Registration Model**: Registration without an organization is NOT
allowed. Two registration flows are supported:

- *Scenario A — Create Organization*: User registers with name,
  email, password, and `organization_name`. The system creates the
  user, creates the organization, creates a `UserOrgMembership`
  (org_role = Admin, is_enabled = true), and issues a JWT with
  `userId` + `organizationId`.
- *Scenario B — Join via Invitation*: User opens an invite link,
  registers with name, email, and password. The system creates the
  user, creates a `UserOrgMembership` (org_role = Member,
  is_enabled = true), and issues a JWT with `userId` +
  `organizationId`.

If a user already has an active membership, the system MUST reject
any attempt to join another organization with a clear error.

**Role Model**: A two-level role system is mandatory.

- *Organization Roles (persistent)*: OrganizationAdmin, Member, Guest.
- *Meeting Roles (per-meeting)*: Host, CoHost, Participant, Observer.

Meeting roles control LiveKit permissions. Organization roles control
system-level permissions.

**State Separation**: The system MUST distinguish between:

- *Persistent Business State* (PostgreSQL): Users, Organizations,
  Meetings, Meeting Roles, Tasks, Summaries, Reminders.
- *Ephemeral Realtime State* (LiveKit): IsAudioMuted, IsVideoMuted,
  IsHandRaised, Active Speaker.

Ephemeral state MUST NOT be stored in PostgreSQL or Redis. LiveKit is
responsible for media session state.

### III. Event-Driven Architecture

Feature interactions MUST occur through domain events (e.g.,
`MeetingEndedEvent`, `SummaryGeneratedEvent`, `TasksExtractedEvent`,
`ReminderCreatedEvent`). Services MUST NOT tightly couple across
features.

All long-running operations MUST use Hangfire:

- AI summarization, task extraction, task syncing, reminder
  triggering, retry logic.
- Retry policy: automatic retry with exponential backoff, maximum 3
  retries. After failure the entity state MUST be marked as `FAILED`.

### IV. Realtime Architecture

**LiveKit Cloud Responsibilities**: Audio/video transport, participant
state, DataChannels, room presence, live transcription, recording
egress. Backend MUST NOT proxy media streams. All WebRTC
infrastructure (TURN/STUN, UDP, TLS, NAT traversal) is managed by
LiveKit Cloud.

**LiveKit Token Issuance**: Backend generates LiveKit Cloud access
tokens based on meeting role and organization permissions. Permissions
MUST be enforced server-side via token grants.

**SignalR Usage**: SignalR is used for notifications, reminder
triggers, AI status updates, and meeting lifecycle signals. SignalR
MUST NOT be used for media transport.

**SignalR Tenant Safety**: All SignalR messages MUST be scoped to
organization-specific Groups (`org:{OrganizationId}`). On connection,
the hub MUST authenticate the user via JWT, then read the
`organizationId` claim from the token and add the connection to
exactly **one** group: `org:{organizationId}`. Background
jobs MUST send notifications via `Clients.Group(...)` only.
`Clients.All` MUST NOT be used anywhere in the codebase. This
prevents cross-tenant data leakage in the shared multi-tenant hub.

### V. Security Rules

**Authentication**:

- ASP.NET Identity for user management.
- JWT Access Tokens (15-minute expiry). MUST include `userId` and
  `organizationId` claims. The `organizationId` is resolved from
  the user's active `UserOrgMembership` record during login.
- Refresh Tokens (7-day expiry), stored hashed, with rotation
  required.

**Authorization** MUST be policy-based, role-based,
organization-scoped, and meeting-role aware.

**Secrets Management**:

- JWT signing keys MUST NOT be hardcoded.
- LiveKit keys MUST be stored in configuration.
- MinIO credentials MUST be stored securely.
- Redis connection strings MUST be secured.

## Operational, Quality & Coding Standards

**Logging & Observability**:

- Structured logging is mandatory.
- Every request MUST include a correlation ID.
- All domain events MUST be logged.
- AI job lifecycle MUST be logged.
- External integration failures MUST be logged.
- Logs MUST NOT expose sensitive information.

**Performance Constraints**:

- API responses MUST complete in under 300 ms (excluding background
  jobs).
- No synchronous AI calls inside request pipelines.
- All AI-related processing MUST be executed asynchronously via
  Hangfire.

  > **Exception — Hybrid Sync/Async Pattern**: Lightweight AI read
  > operations (e.g., RAG queries involving a single embedding lookup +
  > short completion) MAY use a hybrid approach: attempt synchronous
  > execution with a strict timeout (≤3 seconds). If the timeout is
  > exceeded, the operation MUST fall back to a Hangfire job with
  > SignalR delivery. Heavy processing (summarization, task extraction,
  > batch embedding generation) remains strictly asynchronous.
- Database queries MUST be optimized and scoped by `OrganizationId`.
- No blocking calls inside async methods.

**Coding Standards**:

- Dependency Injection is mandatory.
- No static service classes; no service locator pattern.
- Explicit DTOs for all API responses.
- Input validation MUST use FluentValidation.
- All async methods MUST accept `CancellationToken`.
- All timestamps MUST be stored in UTC.

## Deployment & Evolution Strategy

**Deployment Assumptions** (initial scope):

- Local demo deployment.
- Docker-based infrastructure orchestration.
- LiveKit Cloud (external managed service — not self-hosted).
- Local MinIO instance.
- PostgreSQL + pgvector via Docker.
- Redis via Docker.
- Cloud deployment is out of scope for the initial version.

**Evolution & Future Refactoring Rule**: The architecture MUST remain
cleanly modular, extractable into microservices if required, and
compatible with future Clean Architecture refactoring. All
cross-feature interactions MUST occur through domain events or
explicit integration contracts.

## Governance

This constitution supersedes all other development practices for the
AI-Powered Meeting Assistant project. All code reviews and pull
requests MUST verify compliance with these principles.

**Amendment Procedure**:

1. Propose change with rationale in a dedicated PR or discussion.
2. Document impact on existing features and dependent artifacts.
3. Update constitution version per semantic versioning (see below).
4. Propagate changes to dependent templates and guidance files.

**Versioning Policy**: Constitution versions follow MAJOR.MINOR.PATCH:

- MAJOR: Backward-incompatible governance or principle changes.
- MINOR: New principle/section added or materially expanded.
- PATCH: Clarifications, wording, or non-semantic refinements.

**Compliance Review**: Every implementation plan MUST include a
Constitution Check gate (see plan template). Violations MUST be
justified in a Complexity Tracking table.

**Version**: 1.4.0 | **Ratified**: 2026-03-03 | **Last Amended**: 2026-03-13
