# Feature Specification: Reminders — User-Facing

**Feature Branch**: `006-reminders-user-facing`  
**Created**: 2026-04-27  
**Status**: Draft  
**Input**: User description: "Phase 5.6 Reminders - User-Facing: user-scoped reminder endpoints for creating, listing, marking delivered, and cancelling personal reminders"

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Create a Personal Reminder (Priority: P1)

As a meeting participant, I want to create personal reminders so that I can track follow-up items after a meeting without relying on external tools.

**Why this priority**: Creating reminders is the core value proposition of this feature. Without the ability to create reminders, the entire feature is non-functional.

**Independent Test**: A user can authenticate, call the create-reminder endpoint with text and a due date, and receive a persisted reminder that appears in their list.

**Acceptance Scenarios**:

1. **Given** an authenticated user with a valid JWT, **When** they POST to `/api/me/reminders` with `{ text: "Follow up with client", reminderAtUtc: "2026-05-01T09:00:00Z" }`, **Then** the system creates an `Active` reminder with `Scope=Personal`, `Channel=User`, `TargetUserId=me`, `MeetingId=null`, and returns the created reminder.
2. **Given** an authenticated user, **When** they create a reminder without required fields (missing text or reminderAtUtc), **Then** the system returns a validation error (422) and no reminder is created.
3. **Given** an authenticated user, **When** they POST to `/api/me/reminders` with an explicit `Scope=Public` in the body, **Then** the system returns a validation error (422) and no reminder is created.

---

### User Story 2 - View My Reminders (Priority: P1)

As a user, I want to see all reminders that affect me (personal reminders I created and public reminders for meetings I participate in) so that I can stay informed about upcoming actions.

**Why this priority**: Viewing reminders is essential for users to act on them. This is the primary consumption surface.

**Independent Test**: A user with existing personal and public reminders can fetch their list and see only Active reminders with `ReminderAtUtc <= now`.

**Acceptance Scenarios**:

1. **Given** a user with 2 active personal reminders and 1 active public reminder for a meeting they participate in, **When** they GET `/api/me/reminders`, **Then** they receive all 3 reminders ordered by `ReminderAtUtc`.
2. **Given** a user with 1 delivered reminder and 1 cancelled reminder, **When** they GET `/api/me/reminders`, **Then** neither appears in the response (only `Active` status is returned).
3. **Given** a user with a public reminder for a meeting they do NOT participate in, **When** they GET `/api/me/reminders`, **Then** that public reminder is excluded.
4. **Given** a reminder exists in Organization A and a user from Organization B is authenticated, **When** the Organization B user calls any reminder endpoint (`GET`, `POST mark-delivered`, or `DELETE`) with the Organization A reminder ID, **Then** the system returns 404 Not Found and no data from Organization A is leaked.

---

### User Story 3 - Mark Reminder as Delivered (Priority: P2)

As a user, I want to mark a reminder as delivered so that I can track which items I have completed and prevent them from appearing in future fetches.

**Why this priority**: Marking delivered is part of the reminder lifecycle. Without it, reminders would remain active indefinitely, cluttering the user's view.

**Independent Test**: A user can mark their own personal reminder as delivered, and it no longer appears in subsequent `GET /api/me/reminders` calls.

**Acceptance Scenarios**:

1. **Given** a user owns an active personal reminder, **When** they POST `/api/me/reminders/{id}/mark-delivered`, **Then** the reminder's status changes to `Delivered` and `DeliveredAtUtc` is set to the current time.
2. **Given** a user attempts to mark another user's personal reminder as delivered, **When** they POST `/api/me/reminders/{id}/mark-delivered`, **Then** the system returns a 403 forbidden error.
3. **Given** a user attempts to mark a Public reminder (which they can see but do not own) as delivered, **When** they POST `/api/me/reminders/{id}/mark-delivered`, **Then** the system returns a 403 forbidden error.
4. **Given** a user attempts to mark an already-delivered reminder as delivered, **When** they POST the endpoint, **Then** the system returns 200 OK idempotently with the unchanged reminder state.

---

### User Story 4 - Cancel a Reminder (Priority: P2)

As a user, I want to cancel a personal reminder I created so that I can remove items I no longer need to act on.

**Why this priority**: Cancellation provides users with control over their reminder inventory and supports the natural lifecycle of tasks that become irrelevant.

**Independent Test**: A user can cancel their own personal reminder, and it is soft-deleted (status = `Cancelled`) and no longer appears in fetches.

**Acceptance Scenarios**:

1. **Given** a user owns an active personal reminder, **When** they DELETE `/api/me/reminders/{id}`, **Then** the reminder's status changes to `Cancelled`.
2. **Given** a user attempts to cancel another user's reminder, **When** they DELETE `/api/me/reminders/{id}`, **Then** the system returns a 403 forbidden error.
3. **Given** a user attempts to cancel an already-delivered reminder, **When** they DELETE the endpoint, **Then** the system returns a 409 conflict error.

---

### Edge Cases

- What happens when a user creates a reminder with `ReminderAtUtc` in the past? (Allowed — it will be immediately visible in fetches.)
- How does the system handle a fetch when the user has no reminders? (Returns an empty array with 200 OK.)
- What happens if a public reminder's associated meeting is cancelled? (The public reminder is excluded because the join query filters `Meeting.Status != Cancelled`. The `MeetingParticipant` row may still exist, but the meeting itself is no longer active.)
- How does the system prevent users from seeing reminders from other organizations? (Tenant isolation enforced via `OrganizationId` and global query filters.)
- What happens when `ReminderAtUtc` is exactly equal to `now`? (Included in fetch results, since filter is `<= now`.)
- What happens when a user requests `pageSize` above the server maximum or `page` beyond the total pages? (Return a validation error for oversized `pageSize`; return an empty array with pagination metadata for out-of-range `page`.)
- What happens if a user tries to create a Public reminder via `POST /api/me/reminders`? (Rejected with 422 validation error — Public reminders are agent-only.)
- What happens if a user attempts to mark a Public reminder as delivered? (Return 403 Forbidden — only the agent can mark Public reminders delivered.)
- What happens if a user attempts to cancel a Public reminder? (Return 403 Forbidden — users may only cancel their own Personal reminders.)
- **Cross-tenant isolation (Critical)**: What happens when a user from Organization B attempts to access, mark-delivered, or cancel a reminder in Organization A? (Return 404 Not Found for all endpoints — the global `OrganizationId` query filter makes the reminder invisible to users outside its organization. No cross-tenant leakage.)

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: Authenticated users MUST be able to create personal reminders via `POST /api/me/reminders` with `text` and `reminderAtUtc` fields. Reminders created through this endpoint MUST have `Scope=Personal`, `Channel=User`, `TargetUserId=me`, and `MeetingId=null`. Any `scope` value present in the request body MUST be ignored; the server unconditionally sets `Scope=Personal` and returns 422 if the body explicitly attempts to override this behavior with validation rules.
- **FR-002**: The system MUST validate that `text` is non-empty and `reminderAtUtc` is a valid future or present UTC timestamp.
- **FR-003**: Authenticated users MUST be able to fetch all reminders affecting them via `GET /api/me/reminders`, returning only reminders with `Status=Active` and `ReminderAtUtc <= now`.
- **FR-003a**: The list endpoint MUST support pagination via `page` (1-based) and `pageSize` query parameters, with a server-enforced maximum `pageSize` of 50 and a default of 20.
- **FR-004**: The fetch query MUST return reminders where `(TargetUserId = me AND Scope = Personal)` OR `(Scope = Public AND MeetingId IN <meetings the user participates in that are NOT Cancelled>)`.
- **FR-005**: Authenticated users MUST be able to mark their own Personal reminders as delivered via `POST /api/me/reminders/{id}/mark-delivered`. Attempts to mark a Public reminder as delivered MUST return 403 Forbidden.
- **FR-006**: Authenticated users MUST be able to soft-cancel their own personal reminders via `DELETE /api/me/reminders/{id}`.
- **FR-007**: The system MUST enforce tenant isolation so users cannot see, mark, or cancel reminders belonging to another organization.
- **FR-008**: The system MUST emit domain events (`ReminderCreatedEvent`, `ReminderDeliveredEvent`, `ReminderCancelledEvent`) for downstream observability.
- **FR-009**: Reminders MUST support a `Status` lifecycle: `Active → Delivered` or `Active → Cancelled`. No transitions from `Delivered` or `Cancelled` are permitted.
- **FR-010**: The reminder entity MUST include `Scope` (`Personal` | `Public`) and `Channel` (`User` | `Agent`) to support future agent-created reminders (agent surface defined in Phase 5.7).

### Key Entities

- **Reminder**: Represents a thing to be reminded about. Key attributes:
  - `Id` (unique identifier)
  - `OrganizationId` (tenant isolation)
  - `Text` (free string describing what to remember)
  - `Scope` (`Personal` | `Public`)
  - `Channel` (`User` | `Agent` — provenance)
  - `CreatedByUserId` (who originated the reminder)
  - `TargetUserId` (required for Personal; null for Public)
  - `MeetingId` (required for Public or Agent-created; null for user-created Personal)
  - `ReminderAtUtc` (timing gate for fetch queries)
  - `Status` (`Active` | `Delivered` | `Cancelled`)
  - `DeliveredAtUtc` (nullable, set on delivery)
  - `OriginalText` (nullable, raw agent input for audit)
  - `CreatedAtUtc`, `UpdatedAtUtc` (timestamps)

- **MeetingParticipant**: Referenced implicitly in fetch queries to determine which public reminders affect a user. Represents the many-to-many relationship between users and meetings.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: The create-reminder endpoint responds in under 300ms p95 under normal load with a valid JWT and proper request body.
- **SC-002**: 100% of fetched reminders are scoped to the requesting user's organization (zero cross-tenant leakage).
- **SC-003**: Reminder fetch queries return paginated results in under 200 milliseconds for users with up to 100 active reminders, with page sizes between 1 and 50.
- **SC-004**: Users can mark reminders as delivered or cancel them with 100% consistency — no reminder remains in `Active` state after a successful mark-delivered or cancel operation.
- **SC-005**: All reminder lifecycle transitions emit the correct domain event with 100% reliability for audit and downstream processing.

## Clarifications

### Session 2026-04-27

- **Q**: Should the system enforce a maximum number of active reminders per user, and should the list endpoint support pagination? → **A**: Pagination with max limit (e.g., 20–50 per page), no hard cap on total reminders. Return all active reminders in one response only if count is below the page size; otherwise require page/cursor parameters.
- **Q**: Should `POST /api/me/reminders` be strictly Personal-only, or should users also be able to create Public reminders? → **A**: Strictly Personal-only. The user-facing endpoint only creates Personal reminders. Public reminders are created exclusively by the agent surface (Phase 5.7) or admin functions.
- **Q**: When a user attempts to mark a Public reminder as delivered, what should the API response be? → **A**: Return 403 Forbidden with a clear error message. Users may only mark their own Personal reminders as delivered. Public reminders are managed by the agent.
- **Q**: Should `POST /api/me/reminders/{id}/mark-delivered` return 409 Conflict or idempotent 200 OK when called on an already-delivered reminder? → **A**: Idempotent 200 OK with the reminder state unchanged. Repeated identical requests produce the same result without side effects. 409 Conflict is reserved for true race conditions (e.g., concurrent conflicting updates from different users).

## Assumptions

- Users are already authenticated via JWT (implemented in Phase 1 — Identity).
- The `Meeting` and `MeetingParticipant` entities exist (implemented in Phase 3 — Meetings).
- Tenant isolation via `OrganizationId` global query filters is active (implemented in Phase 0.3).
- Agent-facing reminder endpoints (Phase 5.7) will reuse the same `Reminder` entity and `IReminderService`.
- No Hangfire jobs or SignalR notifications are required — reminders are pure data fetched on demand.
- Soft delete via `Status=Cancelled` is preferred over hard deletion for audit purposes.
- Mobile and desktop clients will poll `GET /api/me/reminders` at appropriate intervals rather than relying on push notifications.
- The system does not need to support recurring reminders; each reminder row targets exactly one occurrence.
- Domain event reliability (100% emission) is inherited from the existing MediatR + EF Core Unit of Work pattern established in Phase 0.3: events are published in-process after the database transaction commits. Out-of-process event bus failure does not rollback the transaction, but event loss is mitigated by the in-process dispatch within the same request/background job scope.
