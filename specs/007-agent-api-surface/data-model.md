# Data Model: Agent-Callable API Surface

**Feature**: Agent-Callable API Surface (Phase 5.7)  
**Date**: 2026-05-03  
**Status**: Complete

## Overview

Phase 5.7 introduces **no new persistent entities**. All endpoints operate over existing entities from prior phases:

- `Organization` (Phase 2)
- `UserOrgMembership` (Phase 2)
- `ApplicationUser` (Phase 1)
- `Meeting` (Phase 3)
- `MeetingParticipant` (Phase 3)
- `MeetingTag` (Phase 2)
- `Reminder` (Phase 5.6)

## Entity Relationships

```
Organization
├── UserOrgMembership (1:N)
│   └── ApplicationUser (1:1)
├── Meeting (1:N)
│   ├── MeetingParticipant (1:N)
│   │   └── ApplicationUser (N:1)
│   └── Reminder (1:N) [for public/agent reminders]
└── MeetingTag (1:N)
```

## Data Flow

### Agent Context Queries (Read-Only)

1. **GetOrganization**: Query `Organization` by `OrganizationId` from agent token. Count active members via `UserOrgMembership.Where(u => u.IsEnabled)`.

2. **GetMeetingMembers**: Query `MeetingParticipant` filtered by `MeetingId` and `OrganizationId`. Join `ApplicationUser` + `UserOrgMembership` to get `displayName`, `jobRole`, `context`.

3. **ListMeetings**: Query `Meeting` filtered by `OrganizationId` + status. Include `MeetingMeetingTag` for tag IDs.

4. **GetMeetingDetail**: Query `Meeting` by `MeetingId` + `OrganizationId`. Include `MeetingParticipant` roster + `RecurrenceConfig`.

5. **ListRecurringMeetings**: Query `Meeting` where `RecurrenceConfig != null` and `OrganizationId = token.orgId`.

6. **ListMeetingTags**: Query `MeetingTag` where `IsActive = true` and `OrganizationId = token.orgId`.

### Agent Reminder Operations (Read/Write)

**CreateReminder**:
```
Input: CreateAgentReminderRequest
  → Validate meetingId matches token claim
  → Set Channel = Agent
  → Set CreatedByUserId from request body (nullable)
  → Set MeetingId from route
  → Set OrganizationId from token
  → Persist Reminder
```

**ListPublicMeetingReminders**:
```
Input: meetingId
  → Verify meetingId matches token claim
  → Query Reminder WHERE
      OrganizationId = token.orgId
      AND MeetingId = meetingId
      AND Scope = Public
      AND Status = Active
      AND ReminderAtUtc <= meeting.ScheduledStartUtc
  → Return ordered by ReminderAtUtc
```

**MarkReminderDelivered**:
```
Input: reminderId
  → Verify reminder exists and belongs to token.orgId
  → Verify reminder.Scope = Public (defence-in-depth)
  → Set Status = Delivered
  → Set DeliveredAtUtc = UtcNow
```

**CancelReminder**:
```
Input: reminderId
  → Verify reminder exists and belongs to token.orgId
  → Verify reminder.Channel = Agent (optional: allow cancelling any public reminder)
  → Set Status = Cancelled
```

## Validation Rules

| Rule | Enforcement | Failure Response |
|------|-------------|------------------|
| MeetingId route param matches token claim | Action filter or service check | 403 Forbidden |
| OrganizationId scoped to token | Global query filter + explicit check | 403 Forbidden |
| Personal reminders never returned in public list | Service layer hard filter | N/A (silently excluded) |
| Agent cannot mark personal reminders delivered | Service layer check | 403 Forbidden |
| Rate limit: 100 req/min per meetingId | Rate limiting middleware | 429 Too Many Requests |

## State Transitions (Reminder)

```
Active → Delivered   (via agent mark-delivered or user mark-delivered)
Active → Cancelled   (via agent cancel or user cancel)
```

No other transitions are valid for agent operations.
