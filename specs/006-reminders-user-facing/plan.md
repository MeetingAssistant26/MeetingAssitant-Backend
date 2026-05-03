# Implementation Plan: Reminders — User-Facing

**Branch**: `006-reminders-user-facing` | **Date**: 2026-04-27 | **Spec**: [spec.md](../spec.md)
**Input**: Feature specification from `/specs/006-reminders-user-facing/spec.md`

## Summary

Implement user-scoped reminder CRUD endpoints under the existing `Tasks` feature slice. Users can create personal reminders, view reminders affecting them (personal + public for meetings they participate in), mark personal reminders as delivered, and soft-cancel personal reminders. All endpoints use tenant-scoped queries with `OrganizationId`, enforce that `POST /api/me/reminders` only creates `Scope=Personal` reminders, and return standard problem details on errors. No background jobs or SignalR — reminders are pure data fetched on demand with pagination support.

## Technical Context

**Language/Version**: C# 13 / .NET 10
**Primary Dependencies**: ASP.NET Core, EF Core (Npgsql), FluentValidation, MediatR, Mapster
**Storage**: PostgreSQL (with pgvector extension available but unused for this feature)
**Testing**: xUnit + FluentAssertions + WebApplicationFactory (integration tests), Moq (unit tests)
**Target Platform**: Linux server (Docker)
**Project Type**: Web API service
**Performance Goals**: <200ms p95 for paginated list queries, <100ms for single-item mutations
**Constraints**: Must use Partial Controller Pattern (one endpoint per file), must use `result.ToProblem(correlationIdProvider)` for errors, must use `CancellationToken` on all async methods
**Scale/Scope**: Up to ~100 active reminders per user, page size 1–50, default 20

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Gate | Status | Notes |
|------|--------|-------|
| I. Vertical Slice Architecture | ✅ Pass | Reminders live under `Features/Tasks/` slice alongside existing Task/ReviewQueue endpoints |
| II. Partial Controller Pattern | ✅ Pass | 4 endpoint files (CreateMyReminder, ListMyReminders, MarkMyReminderDelivered, CancelMyReminder) + 1 controller definition |
| III. Tenant Isolation by Default | ✅ Pass | `OrganizationId` on `Reminder` entity + EF Core global query filter |
| IV. Strict Single Membership Rule | ✅ Pass | No impact; membership is already enforced by Phase 1/2 |
| V. Standardized Operational Errors | ✅ Pass | All endpoints use `result.ToProblem(correlationIdProvider)` for RFC 7807 responses |

*Re-checked after Phase 1: all gates still pass. No violations or exceptions required.*

## Project Structure

### Documentation (this feature)

```text
specs/006-reminders-user-facing/
├── plan.md              # This file
├── research.md          # Phase 0 output (see below)
├── data-model.md        # Phase 1 output (see below)
├── quickstart.md        # Phase 1 output (see below)
├── contracts/           # Phase 1 output (see below)
└── tasks.md             # Phase 2 output (/speckit.tasks command)
```

### Source Code (repository root)

```text
backend/src/Features/Tasks/
├── Endpoints/
│   └── Reminder/
│       ├── ReminderController.cs          # definition only (route, ctor, deps)
│       ├── CreateMyReminderEndpoint.cs    # POST /api/me/reminders
│       ├── ListMyRemindersEndpoint.cs     # GET  /api/me/reminders
│       ├── MarkMyReminderDeliveredEndpoint.cs  # POST /api/me/reminders/{id}/mark-delivered
│       └── CancelMyReminderEndpoint.cs    # DELETE /api/me/reminders/{id}
│
├── Models/
│   ├── Requests/
│   │   └── CreateMyReminderRequest.cs
│   └── Responses/
│       └── ReminderResponse.cs
│
├── Services/
│   ├── IReminderService.cs
│   └── ReminderService.cs
│
└── Validators/
    └── CreateMyReminderRequestValidator.cs
```

**Structure Decision**: Follows the existing Partial Controller Pattern established in Phase 0.3. The `ReminderController` uses `[Route("api/me/reminders")]` and is isolated in its own `Endpoints/Reminder/` subfolder. All business logic is delegated to `IReminderService`.

## Complexity Tracking

No violations. All patterns align with existing architecture.

---

## Phase 0: Outline & Research

### Research Findings

This feature is a standard CRUD + list + state-transition endpoint set within an existing vertical slice. No external API integrations or novel technology choices are required. The following decisions are confirmed from existing project patterns:

**Decision: Reuse existing Result/Error pattern**
- **Rationale**: Phase 0.3 introduced `Result<T>` and `ResultExtensions.ToProblem()`. All service methods return `Result<T>` or `Result`, and endpoints use the extension method.
- **Alternatives considered**: None — deviation would break consistency.

**Decision: Use existing EF Core + PostgreSQL stack**
- **Rationale**: The `AppDbContext` with global `OrganizationId` query filter is already configured. Adding a new `DbSet<Reminder>` requires only an entity class + migration.
- **Alternatives considered**: None — deviation would break tenant isolation.

**Decision: Use existing MediatR domain event dispatch**
- **Rationale**: Domain events (`ReminderCreatedEvent`, `ReminderDeliveredEvent`, `ReminderCancelledEvent`) are already dispatched via `IPublisher` in other features. Reminder events follow the same pattern.
- **Alternatives considered**: None.

**Decision: Pagination via Skip/Take with Count query for metadata**
- **Rationale**: Simplest, most efficient for expected scale (<100 active reminders per user). Offset pagination is sufficient; cursor pagination is unnecessary for this volume.
- **Alternatives considered**: Cursor pagination (rejected — overkill for <100 items per user, adds complexity without benefit).

**Decision: Sort order for reminders**
- **Rationale**: Ascending by `ReminderAtUtc` (soonest first) is the natural mental model for a todo/reminder list.
- **Alternatives considered**: Descending (most recent first) — rejected, as users typically want to see what's due next.

---

## Phase 1: Design & Contracts

### Data Model

See `data-model.md` for full entity definition with EF Core configuration. Summary:

| Entity | Table | Key Fields | Relationships |
|--------|-------|------------|---------------|
| Reminder | `Reminders` | `Id` (PK), `OrganizationId`, `Text`, `Scope`, `Channel`, `CreatedByUserId`, `TargetUserId`, `MeetingId`, `ReminderAtUtc`, `Status`, `DeliveredAtUtc`, `OriginalText`, `CreatedAtUtc`, `UpdatedAtUtc` | Implicit: `MeetingParticipant` (used in list query only) |

**Indexes**: 
- `(TargetUserId, Status)` — for user's "my reminders" query
- `(MeetingId, Scope, Status)` — for agent's "this meeting's public reminders" query (Phase 5.7)
- `(OrganizationId)` — covered by global query filter

**State Transitions**:
```
Active → Delivered (via mark-delivered)
Active → Cancelled (via cancel)
No transitions out of Delivered or Cancelled
```

### API Contracts

See `contracts/` directory for full OpenAPI-style endpoint definitions. Summary:

| Method | Route | Auth | Request | Response | Status Codes |
|--------|-------|------|---------|----------|--------------|
| POST | `/api/me/reminders` | User JWT | `CreateMyReminderRequest` | `ReminderResponse` | 201, 400, 422, 401 |
| GET | `/api/me/reminders` | User JWT | `page`, `pageSize` query | `PaginatedList<ReminderResponse>` | 200, 401 |
| POST | `/api/me/reminders/{id}/mark-delivered` | User JWT | — | `ReminderResponse` | 200, 403, 404, 401 |
| DELETE | `/api/me/reminders/{id}` | User JWT | — | 204 No Content | 204, 403, 409, 404, 401 |

**Key contract rules**:
- `POST /api/me/reminders` ignores any `scope` field in body and always sets `Personal`
- `GET /api/me/reminders` returns `Status=Active AND ReminderAtUtc <= now` with pagination
- `mark-delivered` idempotent: 200 OK even if already delivered
- `DELETE` returns 409 if reminder is already `Delivered` or `Cancelled`
- `403` returned for cross-user personal reminders OR any public reminder mutation attempt

### Quick Start

See `quickstart.md` for Postman/curl examples.

### Agent Context Update

Updated `.specify/memory/constitution.md` — no changes needed. All technologies (EF Core, PostgreSQL, MediatR, Mapster, FluentValidation) are already listed. Partial Controller Pattern and tenant isolation rules remain unchanged.

---

## Artifacts Generated

| Artifact | Path | Status |
|----------|------|--------|
| Research | `specs/006-reminders-user-facing/research.md` | ✅ Complete |
| Data Model | `specs/006-reminders-user-facing/data-model.md` | ✅ Complete |
| API Contracts | `specs/006-reminders-user-facing/contracts/` | ✅ Complete |
| Quick Start | `specs/006-reminders-user-facing/quickstart.md` | ✅ Complete |
| Implementation Plan | `specs/006-reminders-user-facing/plan.md` | ✅ Complete |

**Suggested next command**: `/speckit.tasks` — break the plan into actionable, dependency-ordered tasks.
