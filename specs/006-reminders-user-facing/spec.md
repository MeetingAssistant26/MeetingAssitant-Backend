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

1. **Given** an authenticated user with a valid JWT, **When** they POST to `/api/me/reminders` with `{ text: "Follow up with client", reminderAtUtc: "2026-05-01T09:00:00Z" }`, **Then** the system creates an `Active` reminder with `Scope=Personal`, `Channel=User`, `TargetUserId=me`, and returns the created reminder.
2. **Given** an authenticated user, **When** they create a reminder without required fields (missing text or reminderAtUtc), **Then** the system returns a validation error (422) and no reminder is created.

---

### User Story 2 - View My Reminders (Priority: P1)

As a user, I want to see all reminders that affect me (personal reminders I created and public reminders for meetings I participate in) so that I can stay informed about upcoming actions.

**Why this priority**: Viewing reminders is essential for users to act on them. This is the primary consumption surface.

**Independent Test**: A user with existing personal and public reminders can fetch their list and see only Active reminders with `ReminderAtUtc <= now`.

**Acceptance Scenarios**:

1. **Given** a user with 2 active personal reminders and 1 active public reminder for a meeting they participate in, **When** they GET `/api/me/reminders`, **Then** they receive all 3 reminders ordered by `ReminderAtUtc`.
2. **Given** a user with 1 delivered reminder and 1 cancelled reminder, **When** they GET `/api/me/reminders`, **Then** neither appears in the response (only `Active` status is returned).
3. **Given** a user with a public reminder for a meeting they do NOT participate in, **When** they GET `/api/me/reminders`, **Then** that public reminder is excluded.

---

### User Story 3 - Mark Reminder as Delivered (Priority: P2)

As a user, I want to mark a reminder as delivered so that I can track which items I have completed and prevent them from appearing in future fetches.

**Why this priority**: Marking delivered is part of the reminder lifecycle. Without it, reminders would remain active indefinitely, cluttering the user's view.

**Independent Test**: A user can mark their own personal reminder as delivered, and it no longer appears in subsequent `GET /api/me/reminders` calls.

**Acceptance Scenarios**:

1. **Given** a user owns an active personal reminder, **When** they POST `/api/me/reminders/{id}/mark-delivered`, **Then** the reminder's status changes to `Delivered` and `DeliveredAtUtc` is set to the current time.
2. **Given** a user attempts to mark another user's personal reminder as delivered, **When** they POST `/api/me/reminders/{id}/mark-delivered`, **Then** the system returns a 403 forbidden error.
3. **Given** a user attempts to mark an already-delivered reminder as delivered, **When** they POST the endpoint, **Then** the system returns a 409 conflict or idempotent success.

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
- What happens if a public reminder's associated meeting is deleted or the user leaves the meeting? (The public reminder is still returned if the user participates at fetch time; if they no longer participate, it is excluded via `MeetingId IN <my meetings>` filter.)
- How does the system prevent users from seeing reminders from other organizations? (Tenant isolation enforced via `OrganizationId` and global query filters.)
- What happens when `ReminderAtUtc` is exactly equal to `now`? (Included in fetch results, since filter is `<= now`.)

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: Authenticated users MUST be able to create personal reminders via `POST /api/me/reminders` with `text` and `reminderAtUtc` fields.
- **FR-002**: The system MUST validate that `text` is non-empty and `reminderAtUtc` is a valid future or present UTC timestamp.
- **FR-003**: Authenticated users MUST be able to fetch all reminders affecting them via `GET /api/me/reminders`, returning only reminders with `Status=Active` and `ReminderAtUtc <= now`.
- **FR-004**: The fetch query MUST return reminders where `(TargetUserId = me AND Scope = Personal)` OR `(Scope = Public AND MeetingId IN <meetings the user participates in>)`.
- **FR-005**: Authenticated users MUST be able to mark their own personal reminders as delivered via `POST /api/me/reminders/{id}/mark-delivered`.
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

- **SC-001**: Users can create a personal reminder in under 30 seconds via the API endpoint.
- **SC-002**: 100% of fetched reminders are scoped to the requesting user's organization (zero cross-tenant leakage).
- **SC-003**: Reminder fetch queries return results in under 200 milliseconds for users with up to 100 active reminders.
- **SC-004**: Users can mark reminders as delivered or cancel them with 100% consistency — no reminder remains in `Active` state after a successful mark-delivered or cancel operation.
- **SC-005**: All reminder lifecycle transitions emit the correct domain event with 100% reliability for audit and downstream processing.

## Assumptions

- Users are already authenticated via JWT (implemented in Phase 1 — Identity).
- The `Meeting` and `MeetingParticipant` entities exist (implemented in Phase 3 — Meetings).
- Tenant isolation via `OrganizationId` global query filters is active (implemented in Phase 0.3).
- Agent-facing reminder endpoints (Phase 5.7) will reuse the same `Reminder` entity and `IReminderService`.
- No Hangfire jobs or SignalR notifications are required — reminders are pure data fetched on demand.
- Soft delete via `Status=Cancelled` is preferred over hard deletion for audit purposes.
- Mobile and desktop clients will poll `GET /api/me/reminders` at appropriate intervals rather than relying on push notifications.
- The system does not need to support recurring reminders; each reminder row targets exactly one occurrence.
