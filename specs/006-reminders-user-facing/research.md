# Phase 0 Research: Reminders — User-Facing

**Date**: 2026-04-27
**Feature**: Phase 5.6 Reminders — User-Facing

## Research Decisions

### Pagination Strategy

**Decision**: Offset pagination (Skip/Take) with count query for total metadata.

**Rationale**: 
- Expected scale is <100 active reminders per user (from SC-003)
- Page sizes are bounded (max 50, default 20)
- Offset pagination is the simplest to implement with EF Core and requires no cursor state management
- Performance target (<200ms) is easily achievable with a small dataset and proper indexing

**Alternatives considered**:
- Cursor pagination (keyset): Rejected as overkill for this volume. Adds complexity without benefit.
- No pagination: Rejected — unbounded payloads violate API best practices.

### Sort Order

**Decision**: Ascending by `ReminderAtUtc` (soonest first), then by `CreatedAtUtc` as tiebreaker.

**Rationale**: Users viewing a reminder list naturally expect to see what's due next at the top. This matches calendar/task app conventions.

**Alternatives considered**:
- Descending by `ReminderAtUtc`: Rejected — would show newest-created or furthest-future first, which is counterintuitive for a todo/reminder list.

### Idempotency for Mark-Delivered

**Decision**: Idempotent 200 OK on repeated calls.

**Rationale**:
- REST best practice: safe, repeatable operations should not error on re-invocation
- Client simplicity: no need to track "already delivered" state before calling
- Race condition handling: concurrent identical requests from the same user should not produce errors

**Alternatives considered**:
- 409 Conflict: Rejected — creates unnecessary client error handling for a harmless condition.

### Scope Enforcement at Endpoint Level

**Decision**: `POST /api/me/reminders` unconditionally sets `Scope=Personal` regardless of body input.

**Rationale**:
- Clean separation of concerns: user surface = Personal only, agent surface = can create Public (Phase 5.7)
- Prevents any possibility of users creating Public reminders accidentally or maliciously
- Simpler than body validation rejecting `Scope=Public` (though we do both for defense-in-depth)

### Public Reminder Visibility in User List

**Decision**: Users can see Public reminders for meetings they participate in, but cannot mutate them.

**Rationale**:
- Users need awareness of public reminders (e.g., "Team standup notes due tomorrow") even though they can't control them
- Mutation restricted to agent-only preserves agent authority over public state
- 403 Forbidden on mutation attempts provides clear API semantics

## No External Dependencies

This feature has no external API integrations, no third-party services, and no novel technology choices. All dependencies (EF Core, MediatR, FluentValidation, Mapster) are already established in the codebase.

## Risk Assessment

| Risk | Likelihood | Impact | Mitigation |
|------|------------|--------|------------|
| Tenant isolation bypass in list query | Low | High | Global query filter + explicit `OrganizationId` filter in service layer |
| Race condition on concurrent mark-delivered | Low | Medium | Idempotent design + database row-level locking if needed |
| Performance degradation with large reminder volumes | Low | Medium | Pagination limits + composite indexes on `(TargetUserId, Status)` and `(MeetingId, Scope, Status)` |
| Public reminder appearing after user leaves meeting | Low | Low | Query dynamically checks `MeetingParticipant` at fetch time |
