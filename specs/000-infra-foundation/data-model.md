# Data Model: Infrastructure & Foundation

**Feature**: `000-infra-foundation` | **Date**: 2026-03-07

> Infrastructure features define shared base types and enums rather than
> domain-specific entities. These types are consumed by all downstream features.

---

## BaseEntity (abstract class)

The shared base class for all domain entities across the system.

### Fields

| Field | Type | Constraints | Description |
|-------|------|-------------|-------------|
| `Id` | `Guid` | PK, auto-generated (`Guid.NewGuid()`) | Unique identifier |
| `CreatedAtUtc` | `DateTime` | Required, UTC, auto-set on insert | Record creation timestamp |
| `UpdatedAtUtc` | `DateTime` | Required, UTC, auto-set on insert and update | Last modification timestamp |

### Behaviors

- `CreatedAtUtc` is set automatically in `SaveChangesAsync` when `EntityState == Added`.
- `UpdatedAtUtc` is set automatically in `SaveChangesAsync` when `EntityState == Added` or `Modified`.
- Values are always `DateTime.UtcNow` — no local/offset timestamps permitted.
- All domain entities inherit from `BaseEntity`.

### Notes

- `OrganizationId` is NOT on `BaseEntity` — it belongs to the `IHasOrganizationId` interface (see below).
- Entities like `ApplicationUser` (which extends `IdentityUser<Guid>`) inherit timestamps but not tenant scoping.

---

## IHasOrganizationId (interface)

Marker interface for entities that are scoped to an organization (tenant).

### Fields

| Field | Type | Constraints | Description |
|-------|------|-------------|-------------|
| `OrganizationId` | `Guid` | Required, FK to Organization, indexed | Tenant identifier |

### Behaviors

- Any entity implementing this interface gets an automatic EF Core global query filter: `WHERE OrganizationId = @currentTenantId`.
- The filter is registered via reflection in `OnModelCreating` — no manual registration needed when adding new entities.
- `SaveChangesAsync` auto-sets `OrganizationId` from `ITenantProvider.CurrentOrganizationId` on insert if not already set (defense-in-depth).
- Entities that exist outside organizational context (e.g., `ApplicationUser`, `RefreshToken`) do NOT implement this interface.

---

## EntityStatus (enum)

Shared enumeration for entities that undergo background processing via Hangfire.

### Values

| Value | Integer | Description |
|-------|---------|-------------|
| `Pending` | 0 | Initial state, awaiting processing |
| `Processing` | 1 | Background job is actively working on this entity |
| `Completed` | 2 | Processing finished successfully |
| `Failed` | 3 | All retries exhausted, marked by Hangfire retry filter |

### Usage

- Used by `Summary`, `Recording`, `TaskItem`, `Reminder`, `MeetingEmbedding` and any future entity with background processing.
- The Hangfire retry filter automatically transitions entities to `Failed` when 3 retries are exhausted.
- `Processing` → `Completed`: set by the job on successful completion.
- `Processing` → `Failed`: set by the `IElectStateFilter` on retry exhaustion.

### State Transitions

```
Pending ──► Processing ──► Completed
                │
                └──► Failed (after 3 retries)
```

---

## StandardErrorResponse (DTO)

Standardized error response returned by the global error handling middleware.

### Fields

| Field | Type | Nullable | Description |
|-------|------|----------|-------------|
| `Type` | `string` | No | Error category (e.g., `ValidationError`, `AuthenticationError`, `InternalError`) |
| `Title` | `string` | No | Human-readable summary |
| `Status` | `int` | No | HTTP status code |
| `Errors` | `Dictionary<string, string[]>` | Yes | Field-level validation errors (null for non-validation errors) |
| `CorrelationId` | `string` | No | Request correlation ID for tracing |

### Notes

- This DTO is used by the global error handler and consumed by all downstream features.
- Endpoints MUST NOT construct `StandardErrorResponse` directly — use `ResultExtensions.ToProblem()` instead (see below).
- `Errors` is populated only for `400 Bad Request` responses (validation failures).
- Internal exception details are NEVER exposed — `Title` provides a generic message for 500 errors.

---

## ResultExtensions (static class)

Helper extension method that converts a failed `Result` into an `ObjectResult` containing `StandardErrorResponse`. This is the standard pattern for endpoint-level error responses.

### Location

`Shared/ResultExtensions.cs`

### Method

| Method | Signature | Returns |
|--------|-----------|----------|
| `ToProblem` | `public static ObjectResult ToProblem(this Result result, ICorrelationIdProvider correlationIdProvider)` | `ObjectResult` containing `StandardErrorResponse` |

### Behavior

- **Guard**: Throws `InvalidOperationException` if called on a successful result (`result.IsSuccess == true`).
- **Mapping**: Maps `Result.Error` fields to `StandardErrorResponse`:
  - `Error.Code` → `StandardErrorResponse.Type`
  - `Error.Description` → `StandardErrorResponse.Title`
  - `Error.StatusCode` → `StandardErrorResponse.Status`
- **CorrelationId**: Sets `StandardErrorResponse.CorrelationId` from `correlationIdProvider.CorrelationId` (generated by `CorrelationIdMiddleware`).
- **HTTP Status**: Sets `ObjectResult.StatusCode` from `result.Error.StatusCode`.

### Usage

```csharp
var result = await service.DoSomethingAsync(..., cancellationToken);

if (result.IsSuccess)
    return Ok(result.Value);

return result.ToProblem(correlationIdProvider);
```

### Notes

- This helper handles **expected business failures** (validation errors, not found, unauthorized, etc.).
- The global `ExceptionHandlingMiddleware` continues to handle **unexpected exceptions**.
- Endpoints MUST use `result.ToProblem(correlationIdProvider)` and MUST NOT manually construct `StandardErrorResponse`.
- `ICorrelationIdProvider` is injected into controller definitions and passed to `ToProblem()` at endpoint call sites.

---

## Relationships

```
BaseEntity (abstract)
    ├── ApplicationUser (via IdentityUser<Guid>) — does NOT implement IHasOrganizationId
    ├── RefreshToken — does NOT implement IHasOrganizationId
    ├── Organization — does NOT implement IHasOrganizationId (it IS the tenant)
    └── [All org-scoped entities] — implement IHasOrganizationId
         ├── UserOrgMembership
         ├── Meeting
         ├── MeetingParticipant
         ├── TranscriptSegment
         ├── Summary
         ├── TaskItem
         ├── Recording
         ├── MeetingEmbedding
         ├── PlatformIntegration
         ├── IntegrationMapping
         └── Reminder

ITenantProvider (scoped service)
    └── Resolved from JWT `organizationId` claim
    └── Injected into AppDbContext constructor
    └── Provides CurrentOrganizationId for query filters and auto-set on insert

ICorrelationIdProvider (scoped service)
    └── Set by CorrelationIdMiddleware (HTTP) or Hangfire IServerFilter (background)
    └── Consumed by any service needing the correlation ID outside LogContext
```

---

## Domain Events (infrastructure-level)

No domain events are defined in Phase 0. The infrastructure provides the MediatR-based event dispatcher that downstream features use to define their own events. All events must implement `INotification`.

---

## Validation Rules

| Rule | Applies To | Detail |
|------|------------|--------|
| `OrganizationId` required | All `IHasOrganizationId` entities | Cannot be `Guid.Empty`; auto-set from tenant provider |
| UTC timestamps | `BaseEntity` | All timestamps are `DateTime` with `Kind = Utc` |
| Correlation ID format | `ICorrelationIdProvider` | Non-empty string; typically a GUID |
| Status enum range | `EntityStatus` | Must be one of the 4 defined values |
