# Research: Meetings (Phase 0)

**Feature**: 003-meetings | **Date**: 2026-04-08

## Research Summary

No critical unknowns were identified — the tech stack, patterns, and dependencies are fully established from Phase 1 (Identity) and Phase 2 (Organizations). This document records design decisions and best practices for Phase 3-specific concerns.

---

## R-001: Recurrence Configuration Storage

**Decision**: Store recurrence config as a JSONB column on the Meeting entity (nullable).

**Rationale**: The implementation plan specifies `RecurrenceConfig` as JSONB. This avoids a separate table for a simple structured value. EF Core supports JSONB via Npgsql with owned entity mapping (`OwnsOne` with `ToJson()`). The recurrence config is read-only after meeting creation — it captures the original pattern used to generate instances.

**Alternatives considered**:
- Separate `RecurrenceRule` table: Overhead for a 1:1 relationship with no independent lifecycle.
- iCalendar RRULE string: More expressive but harder to query and validate. Overkill for simple daily/weekly/monthly patterns.

---

## R-002: Recurring Meeting Instance Generation Strategy

**Decision**: Generate all instances eagerly at creation time. Each instance is an independent Meeting entity with no foreign key back to a "parent" recurrence.

**Rationale**: The spec requires that "each instance is a standalone meeting that can be independently updated or cancelled." Eager generation keeps the model simple — no lazy expansion logic, no parent-child relationship to maintain. The `RecurrenceConfig` is stored on each generated instance for audit/display purposes only.

**Alternatives considered**:
- Lazy generation (expand on query): Complex, requires expansion logic on every list/calendar query. Not justified for the simple recurrence patterns in scope.
- Parent meeting with linked children: Adds complexity for "update all future instances" which is out of scope.

**Default horizon**: 12 weeks for open-ended recurrences (no end date specified).

---

## R-003: Conflict Detection Algorithm

**Decision**: Time overlap query using standard interval overlap logic: `A.Start < B.End AND A.End > B.Start`. Only check meetings with status Scheduled or InProgress within the active organization.

**Rationale**: Simple SQL-based overlap detection via EF Core LINQ. The scope is limited to the active organization (per clarification) and excludes Cancelled/Completed/Failed meetings. For 50 participants, this means at most 50 subqueries — well within the 3-second target.

**Alternatives considered**:
- In-memory interval tree: Premature optimization. SQL overlap is sufficient at this scale.
- Background pre-computation: Adds complexity with no user-facing benefit at current scale.

---

## R-004: Meeting Status State Machine

**Decision**: Enforce valid transitions in the service layer. The Meeting entity stores the status as an enum column.

**Valid transitions**:
- `Scheduled` -> `InProgress` (triggered by Phase 4 LiveKit integration, not this phase)
- `Scheduled` -> `Cancelled` (Host action via CancelMeetingEndpoint)
- `InProgress` -> `Completed` (triggered by Phase 4)
- `InProgress` -> `Failed` (system-level, Phase 4)

**Rationale**: Phase 3 only implements `Scheduled -> Cancelled`. Other transitions are documented for forward compatibility but not enforced until Phase 4. The service layer validates that cancellation is only allowed from `Scheduled` status.

---

## R-005: Participant Role Hierarchy Enforcement

**Decision**: Enforce role-based permissions in the ParticipantService. No separate authorization policy needed — the service checks the caller's MeetingRole directly.

**Permission matrix**:

| Action | Host | CoHost | Participant | Observer |
|--------|------|--------|-------------|----------|
| Add participant | Yes | Yes | No | No |
| Remove any participant | Yes | No | No | No |
| Remove Participant/Observer | Yes | Yes | No | No |
| Cancel meeting | Yes | No | No | No |
| Update meeting | Yes | No | No | No |

**Rationale**: Meeting-level roles (MeetingRole) are distinct from org-level roles (OrganizationRole). Org-level roles gate meeting creation (Admin/Member only, Guest view-only). Meeting-level roles gate actions within a meeting.

---

## R-006: Tag Association Pattern

**Decision**: Use a junction entity `MeetingMeetingTag` with composite PK `(MeetingId, MeetingTagId)`. Tags are assigned during meeting creation (optional) and can be updated via the UpdateMeetingEndpoint.

**Rationale**: The `MeetingTag` entity already exists in Phase 2 with full CRUD. This phase only adds the junction table and the association logic. Tags are validated to ensure they belong to the same organization and are active (`IsActive = true`).

---

## R-007: Pagination for Meeting Lists

**Decision**: Use cursor-based or offset pagination consistent with the existing codebase pattern. Since the current codebase does not yet implement pagination, introduce a simple offset-based `PagedRequest`/`PagedResponse` pattern that can be reused across features.

**Rationale**: Meeting lists need pagination for organizations with many meetings. Offset-based pagination is simpler to implement and sufficient for the expected scale. Cursor-based can be adopted later if needed.

**Default page size**: 20 items.

---

## R-008: Calendar Data Query

**Decision**: Calendar endpoint accepts a `week` parameter (ISO 8601 date of the week's Monday) and returns all meetings where `ScheduledStartUtc` falls within the 7-day range `[Monday 00:00 UTC, next Monday 00:00 UTC)`.

**Rationale**: Simple date-range query. The spec states meetings appear based on start time for cross-midnight cases. Grouping by day is a client-side concern.
