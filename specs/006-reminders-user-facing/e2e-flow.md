# End-to-End Flow: Reminders — User-Facing

**Feature**: Phase 5.6 Reminders — User-Facing  
**Date**: 2026-04-27  
**Scope**: Complete lifecycle of every user-facing reminder operation

---

## Table of Contents

1. [System Context](#1-system-context)
2. [Flow 1: Create a Personal Reminder](#2-flow-1-create-a-personal-reminder)
3. [Flow 2: List My Reminders](#3-flow-2-list-my-reminders)
4. [Flow 3: Mark Reminder as Delivered](#4-flow-3-mark-reminder-as-delivered)
5. [Flow 4: Cancel a Reminder](#5-flow-4-cancel-a-reminder)
6. [Cross-Cutting Concerns](#6-cross-cutting-concerns)
7. [State Machine Summary](#7-state-machine-summary)

---

## 1. System Context

### Architecture Layers

```
┌─────────────────────────────────────────┐
│  Client (Web/Mobile/Postman)            │
│  • Bearer Token (User JWT)              │
│  • Polls GET /api/me/reminders          │
└──────────────────┬──────────────────────┘
                   │ HTTPS
┌──────────────────▼──────────────────────┐
│  ASP.NET Core Pipeline                │
│  • JWT Authentication Middleware      │
│  • CorrelationId Middleware           │
│  • Tenant Resolution (orgId from JWT) │
└──────────────────┬──────────────────────┘
                   │
┌──────────────────▼──────────────────────┐
│  ReminderController (partial class)     │
│  • Route: api/me/reminders              │
│  • Dependencies: IReminderService       │
│  • No business logic here               │
└──────────────────┬──────────────────────┘
                   │
┌──────────────────▼──────────────────────┐
│  ReminderService                        │
│  • Business rules + tenant isolation    │
│  • EF Core queries via AppDbContext     │
│  • Returns Result<T> / Result           │
│  • Emits domain events via IPublisher   │
└──────────────────┬──────────────────────┘
                   │
┌──────────────────▼──────────────────────┐
│  AppDbContext (EF Core + Npgsql)      │
│  • Global Query Filter: OrganizationId│
│  • DbSet<Reminder>                    │
│  • DbSet<MeetingParticipant>          │
└──────────────────┬──────────────────────┘
                   │
┌──────────────────▼──────────────────────┐
│  PostgreSQL                             │
│  • Reminders table                    │
│  • MeetingParticipants table (Phase 3)  │
│  • Indexes: (TargetUserId, Status)    │
│            (MeetingId, Scope, Status) │
└─────────────────────────────────────────┘
```

### JWT Claims Required

| Claim | Source | Usage |
|-------|--------|-------|
| `userId` | `sub` or custom | `CreatedByUserId`, `TargetUserId` |
| `organizationId` | custom | Tenant isolation (global query filter) |

---

## 2. Flow 1: Create a Personal Reminder

### 2.1 Happy Path

```
User opens app → taps "New Reminder"
  │
  ▼
Client POST /api/me/reminders
  Headers: Authorization: Bearer <user-jwt>
  Body: { "text": "Follow up with client", "reminderAtUtc": "2026-05-01T09:00:00Z" }
  │
  ▼
[Middleware Pipeline]
  ├── JWT Authentication → extracts userId, organizationId
  ├── CorrelationId → generates trace ID
  └── Tenant Resolution → orgId available for downstream
  │
  ▼
[CreateMyReminderEndpoint.cs]
  1. [FromBody] CreateMyReminderRequest request
  2. FluentValidation runs (T004):
     • text: not empty, max 500 chars
     • reminderAtUtc: valid DateTime, UTC
  3. If validation fails → 422 Unprocessable Entity
  4. Calls _reminderService.CreateReminderAsync(request, userId, orgId, ct)
  │
  ▼
[ReminderService.CreateReminderAsync]
  1. Build Reminder entity:
     • Id = Guid.NewGuid()
     • OrganizationId = orgId (from JWT)
     • Text = request.Text
     • Scope = Personal (unconditionally set — FR-001)
     • Channel = User (unconditionally set)
     • CreatedByUserId = userId
     • TargetUserId = userId
     • MeetingId = null
     • ReminderAtUtc = request.ReminderAtUtc
     • Status = Active
     • DeliveredAtUtc = null
     • OriginalText = null
     • CreatedAtUtc = NowUtc
     • UpdatedAtUtc = NowUtc
  2. _dbContext.Reminders.Add(reminder)
  3. await _dbContext.SaveChangesAsync(ct)
  4. Emit ReminderCreatedEvent via IPublisher:
     • Event: { ReminderId, OrganizationId, CreatedByUserId, ReminderAtUtc }
  5. Map entity → ReminderResponse (Mapster)
  6. Return Result<ReminderResponse>.Success(response)
  │
  ▼
[CreateMyReminderEndpoint.cs]
  Result.IsSuccess → 201 Created
  Response Body: ReminderResponse JSON
```

### 2.2 Error Paths

| Scenario | Trigger | Layer | Response |
|----------|---------|-------|----------|
| Missing JWT | No `Authorization` header | Auth Middleware | 401 Unauthorized |
| Invalid JWT | Expired / bad signature | Auth Middleware | 401 Unauthorized |
| Empty `text` | `{ "text": "" }` | FluentValidation (T004) | 422 with `"Text": ["Text is required."]` |
| Missing `text` | Body has no `text` field | FluentValidation (T004) | 422 with `"Text": ["Text is required."]` |
| Missing `reminderAtUtc` | Body has no `reminderAtUtc` | FluentValidation (T004) | 422 with `"ReminderAtUtc": ["ReminderAtUtc is required."]` |
| `Scope=Public` in body | `{ "scope": "Public", ... }` | FluentValidation or Service | 422 (rejected at validation or service layer) |
| DB failure | PostgreSQL unavailable | SaveChanges | 500 via Global Exception Middleware (logged with CorrelationId) |

---

## 3. Flow 2: List My Reminders

### 3.1 Happy Path

```
User opens "My Reminders" screen
  │
  ▼
Client GET /api/me/reminders?page=1&pageSize=20
  Headers: Authorization: Bearer <user-jwt>
  │
  ▼
[ListMyRemindersEndpoint.cs]
  1. [FromQuery] int page = 1
  2. [FromQuery] int pageSize = 20
  3. Clamp pageSize: if > 50 → error; if < 1 → default 20
  4. Calls _reminderService.GetMyRemindersAsync(userId, orgId, page, pageSize, ct)
  │
  ▼
[ReminderService.GetMyRemindersAsync]
1. Build query against _dbContext.Reminders:
     Base filter (enforced by global query filter):
     • OrganizationId == orgId
     
     Additional filters:
     • Status == Active
     • ReminderAtUtc <= DateTime.UtcNow
     • AND (
         (TargetUserId == userId AND Scope == Personal)
         OR
         (Scope == Public AND MeetingId IN (
            SELECT mp.MeetingId 
            FROM MeetingParticipants mp
            JOIN Meetings m ON mp.MeetingId = m.Id
            WHERE mp.UserId == userId
              AND m.Status != Cancelled
         ))
       )
  2. Order by: ReminderAtUtc ASC, then CreatedAtUtc ASC (tiebreaker)
  3. Pagination:
     • totalCount = await query.CountAsync(ct)
     • items = await query.Skip((page-1)*pageSize).Take(pageSize).ToListAsync(ct)
  4. Map each entity → ReminderResponse
  5. Build PaginatedList<ReminderResponse>:
     • Items, Page, PageSize, TotalCount, TotalPages
  6. Return Result<PaginatedList<ReminderResponse>>.Success(paginatedList)
  │
  ▼
[ListMyRemindersEndpoint.cs]
  Result.IsSuccess → 200 OK
  Response Body:
  {
    "items": [ ...ReminderResponse objects... ],
    "page": 1,
    "pageSize": 20,
    "totalCount": 47,
    "totalPages": 3
  }
```

### 3.2 Edge Cases

| Scenario | Trigger | Service Behavior | Response |
|----------|---------|-----------------|----------|
| User has no reminders | Empty result set | Returns `items: []`, `totalCount: 0` | 200 OK with empty array |
| `pageSize=100` | Query param too large | Validation error before service | 422 `"pageSize must be <= 50"` |
| `page=999` | Beyond total pages | Returns `items: []`, `totalCount: actual` | 200 OK with empty array |
| Meeting cancelled | Meeting.Status changed to Cancelled | Public reminders for that meeting excluded by `Meeting.Status != Cancelled` join in the subquery | Not returned in list |
| User "leaves" meeting | Disconnects from LiveKit room | `MeetingParticipant` row is NOT deleted — user is still a participant in the DB | Public reminders still returned (user is still in `MeetingParticipants`) |
| Delivered reminder exists | Status=Delivered | Excluded by `Status == Active` filter | Not returned in list |
| Cancelled reminder exists | Status=Cancelled | Excluded by `Status == Active` filter | Not returned in list |
| Future reminder | ReminderAtUtc > now | Excluded by `ReminderAtUtc <= now` filter | Not returned in list |
| Cross-tenant reminder | Org A user queries Org B reminder | Global query filter excludes it (OrgId mismatch) | 404 if accessed by ID; invisible in list |

---

## 4. Flow 3: Mark Reminder as Delivered

### 4.1 Happy Path (First Call)

```
User taps "Done" on a reminder
  │
  ▼
Client POST /api/me/reminders/{id}/mark-delivered
  Headers: Authorization: Bearer <user-jwt>
  │
  ▼
[MarkMyReminderDeliveredEndpoint.cs]
  1. [FromRoute] Guid id
  2. Calls _reminderService.MarkDeliveredAsync(id, userId, orgId, ct)
  │
  ▼
[ReminderService.MarkDeliveredAsync]
  1. Find reminder:
     • _dbContext.Reminders
     • Id == id
     • OrganizationId == orgId (global filter enforces)
  2. If not found → return Result.Failure(Error.NotFound)
  3. Verify ownership/scope:
     • IF Scope == Public → return Result.Failure(Error.Forbidden("Only personal reminders can be marked delivered by users."))
     • IF TargetUserId != userId → return Result.Failure(Error.Forbidden)
  4. Idempotency check:
     • IF Status == Delivered → return Result.Success(response) immediately (no state change)
  5. Update state:
     • Status = Delivered
     • DeliveredAtUtc = DateTime.UtcNow
     • UpdatedAtUtc = DateTime.UtcNow
  6. await _dbContext.SaveChangesAsync(ct)
  7. Emit ReminderDeliveredEvent via IPublisher:
     • Event: { ReminderId, OrganizationId, TargetUserId, DeliveredAtUtc }
  8. Map entity → ReminderResponse
  9. Return Result<ReminderResponse>.Success(response)
  │
  ▼
[MarkMyReminderDeliveredEndpoint.cs]
  Result.IsSuccess → 200 OK
  Response Body: ReminderResponse with Status="Delivered"
```

### 4.2 Idempotent Re-Call

```
User accidentally taps "Done" again
  │
  ▼
Client POST /api/me/reminders/{id}/mark-delivered (same ID)
  │
  ▼
[ReminderService.MarkDeliveredAsync]
  1. Find reminder → exists, Status == Delivered
  2. Ownership/scope checks pass
  3. Idempotency: Status == Delivered → return Result.Success immediately
     • No DB update
     • No domain event re-emitted
  4. Return existing ReminderResponse
  │
  ▼
200 OK (same response as first call)
```

### 4.3 Error Paths

| Scenario | Trigger | Service Check | Response |
|----------|---------|--------------|----------|
| Reminder not found | Invalid / deleted ID | `FindAsync` returns null | 404 Not Found |
| Public reminder | `Scope == Public` | Ownership check fails | 403 Forbidden |
| Another user's reminder | `TargetUserId != userId` | Ownership check fails | 403 Forbidden |
| Cross-tenant access | User from Org B, reminder in Org A | Global query filter excludes | 404 Not Found (no leakage) |
| Already cancelled | `Status == Cancelled` | State transition check | 409 Conflict (cannot mark cancelled reminder delivered) |

---

## 5. Flow 4: Cancel a Reminder

### 5.1 Happy Path

```
User taps "Delete" (soft-cancel) on a reminder
  │
  ▼
Client DELETE /api/me/reminders/{id}
  Headers: Authorization: Bearer <user-jwt>
  │
  ▼
[CancelMyReminderEndpoint.cs]
  1. [FromRoute] Guid id
  2. Calls _reminderService.CancelReminderAsync(id, userId, orgId, ct)
  │
  ▼
[ReminderService.CancelReminderAsync]
  1. Find reminder:
     • _dbContext.Reminders
     • Id == id
     • OrganizationId == orgId (global filter enforces)
  2. If not found → return Result.Failure(Error.NotFound)
  3. Verify ownership/scope:
     • IF Scope == Public → return Result.Failure(Error.Forbidden)
     • IF TargetUserId != userId → return Result.Failure(Error.Forbidden)
  4. Terminal state guard:
     • IF Status == Delivered → return Result.Failure(Error.Conflict("Cannot cancel a delivered reminder."))
     • IF Status == Cancelled → return Result.Failure(Error.Conflict("Reminder is already cancelled."))
  5. Update state:
     • Status = Cancelled
     • UpdatedAtUtc = DateTime.UtcNow
  6. await _dbContext.SaveChangesAsync(ct)
  7. Emit ReminderCancelledEvent via IPublisher:
     • Event: { ReminderId, OrganizationId, TargetUserId }
  8. Return Result.Success
  │
  ▼
[CancelMyReminderEndpoint.cs]
  Result.IsSuccess → 204 No Content
```

### 5.2 Error Paths

| Scenario | Trigger | Service Check | Response |
|----------|---------|--------------|----------|
| Reminder not found | Invalid ID | `FindAsync` returns null | 404 Not Found |
| Public reminder | `Scope == Public` | Scope check fails | 403 Forbidden |
| Another user's reminder | `TargetUserId != userId` | Ownership check fails | 403 Forbidden |
| Already delivered | `Status == Delivered` | Terminal state guard | 409 Conflict |
| Already cancelled | `Status == Cancelled` | Terminal state guard | 409 Conflict |
| Cross-tenant access | Org B user, Org A reminder | Global query filter | 404 Not Found |

---

## 6. Cross-Cutting Concerns

### 6.1 Tenant Isolation (Zero Leakage Guarantee)

Every database query is automatically filtered by `OrganizationId` through the EF Core Global Query Filter configured in Phase 0.3. This means:

- **List query**: A user from Org B physically cannot see reminders from Org A, even if they guess the reminder ID.
- **Single-item operations** (`mark-delivered`, `cancel`): If the ID belongs to another org, the global filter makes it invisible → `FindAsync` returns null → 404.
- **No manual WHERE clauses needed** in every service method for tenant isolation.

**Defense-in-depth**: Service layer still validates `TargetUserId` and `Scope` for authorization, but the first line of defense is the database-level global filter.

### 6.2 Authentication & Authorization Matrix

| Endpoint | Auth Required | Additional Authorization |
|----------|-------------|----------------------|
| POST /api/me/reminders | User JWT | None (creates for self) |
| GET /api/me/reminders | User JWT | None (returns own + public for my meetings) |
| POST /api/me/reminders/{id}/mark-delivered | User JWT | Must own the reminder (TargetUserId == me) AND Scope == Personal |
| DELETE /api/me/reminders/{id} | User JWT | Must own the reminder (TargetUserId == me) AND Scope == Personal |

### 6.3 Pagination Behavior

| Input | Behavior |
|-------|----------|
| `page=1, pageSize=20` (default) | Returns first 20 items, `totalPages` computed from `totalCount` |
| `pageSize=50` (max) | Returns up to 50 items |
| `pageSize=51` | 422 Validation Error |
| `page=0` or negative | Clamped to 1 or rejected by validator |
| `page > totalPages` | Returns `items: []`, `totalCount: actual`, `totalPages: actual` |
| No reminders match | Returns `items: []`, `totalCount: 0`, `totalPages: 0` |

### 6.4 Domain Events Lifecycle

| Operation | Event Emitted | When | Consumer (Phase 5.7+) |
|-----------|--------------|------|----------------------|
| Create reminder | `ReminderCreatedEvent` | After `SaveChangesAsync` | Agent API may log or audit |
| Mark delivered | `ReminderDeliveredEvent` | After `SaveChangesAsync` (first time only — idempotent) | Agent API may log |
| Cancel reminder | `ReminderCancelledEvent` | After `SaveChangesAsync` | Agent API may log |

All events are dispatched **in-process** via MediatR `IPublisher`. They run in the same DI scope as the HTTP request. If an event handler throws, it does NOT rollback the database transaction (the reminder is still created/delivered/cancelled), but the exception propagates to the global exception middleware and returns 500. This is consistent with other features in the codebase.

### 6.5 Error Response Format (RFC 7807 Problem Details)

All errors flow through `result.ToProblem(correlationIdProvider)`:

```json
{
  "type": "ValidationError",
  "title": "One or more validation errors occurred.",
  "status": 422,
  "correlationId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "errors": {
    "Text": ["Text is required."]
  }
}
```

```json
{
  "type": "Forbidden",
  "title": "You can only mark your own personal reminders as delivered.",
  "status": 403,
  "correlationId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
}
```

---

## 7. State Machine Summary

```
                    ┌─────────────┐
       ┌───────────►│   Active    │◄────────────┐
       │            └──────┬──────┘             │
       │                   │                    │
       │  create           │  cancel            │  create
       │                   ▼                    │
┌──────┴──────┐      ┌─────────────┐    ┌────┴────────┐
│   (start)   │      │  Cancelled  │    │  Delivered  │
└─────────────┘      └─────────────┘    └─────────────┘
                            ▲                  ▲
                            │                  │
                            └────  idempotent ─┘
                                   (200 OK, no change)

Transitions:
  ANY state ──create──► Active          (only possible via POST /api/me/reminders)
  Active ──mark-delivered──► Delivered  (only if owner + Personal)
  Active ──cancel──► Cancelled          (only if owner + Personal)
  Delivered ──mark-delivered──► Delivered (idempotent, 200 OK)
  Delivered ──cancel──► X (409 Conflict)
  Cancelled ──cancel──► X (409 Conflict)
  Cancelled ──mark-delivered──► X (409 Conflict)
```

### Permission Matrix by State

| Action | Active (own, Personal) | Active (Public) | Delivered (own) | Cancelled (own) | Any (other user) |
|--------|----------------------|-----------------|-----------------|-----------------|-----------------|
| **View in list** | ✅ Yes | ✅ Yes (if in meeting) | ❌ No (Active filter) | ❌ No (Active filter) | ❌ No (403/404) |
| **Mark delivered** | ✅ Yes → Delivered | ❌ 403 | ✅ 200 (idempotent) | ❌ 409 | ❌ 403 |
| **Cancel** | ✅ Yes → Cancelled | ❌ 403 | ❌ 409 | ❌ 409 | ❌ 403 |

---

## 8. Complete Request/Response Walkthrough

### Example: Full Day in the Life

**08:00 UTC** — User creates a reminder:
```http
POST /api/me/reminders
Authorization: Bearer eyJhbGciOiJIUzI1NiIs...

{ "text": "Review Q2 budget", "reminderAtUtc": "2026-04-28T10:00:00Z" }

→ 201 Created
{ "id": "r1", "text": "Review Q2 budget", "status": "Active", ... }
```

**09:30 UTC** — User checks reminders (nothing due yet):
```http
GET /api/me/reminders?page=1&pageSize=20

→ 200 OK
{ "items": [], "totalCount": 0, ... }
```

*(ReminderAtUtc is 10:00, which is > 09:30, so not returned)*

**10:15 UTC** — User checks again:
```http
GET /api/me/reminders?page=1&pageSize=20

→ 200 OK
{ "items": [
    { "id": "r1", "text": "Review Q2 budget", "status": "Active", ... }
  ], "totalCount": 1, ... }
```

*(ReminderAtUtc 10:00 <= now 10:15, so returned)*

**10:20 UTC** — User marks it done:
```http
POST /api/me/reminders/r1/mark-delivered

→ 200 OK
{ "id": "r1", "status": "Delivered", "deliveredAtUtc": "2026-04-28T10:20:00Z", ... }
```

**10:25 UTC** — User accidentally marks again:
```http
POST /api/me/reminders/r1/mark-delivered

→ 200 OK (idempotent)
{ "id": "r1", "status": "Delivered", "deliveredAtUtc": "2026-04-28T10:20:00Z", ... }
```

*(Same response; no DB update, no new event)*

**10:30 UTC** — User checks list again:
```http
GET /api/me/reminders?page=1&pageSize=20

→ 200 OK
{ "items": [], "totalCount": 0, ... }
```

*(Status=Delivered excluded by Active filter)*

---

*End of End-to-End Flow Document*
