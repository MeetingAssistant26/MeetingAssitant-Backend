# Implementation Plan: Meetings

**Branch**: `003-meetings` | **Date**: 2026-04-08 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `/specs/003-meetings/spec.md`

## Summary

Implement the full meeting lifecycle for organizations: create, update, cancel, and list meetings with filtering. Add participant management with role-based permissions (Host, CoHost, Participant, Observer), scheduling conflict detection scoped to the active organization, recurring meeting generation from recurrence configurations, calendar data retrieval by week, and many-to-many tag association via a junction table linking to the existing MeetingTag entity from Phase 2.

The implementation follows the established vertical slice architecture with partial controller pattern, Result-based error handling, EF Core global query filters for tenant isolation, and MediatR domain events.

## Technical Context

**Language/Version**: C# / .NET 10
**Primary Dependencies**: ASP.NET Core, EF Core (Npgsql), FluentValidation, MediatR, Mapster
**Storage**: PostgreSQL via Npgsql with EF Core
**Testing**: xUnit (integration and unit tests)
**Target Platform**: Linux server (containerized)
**Project Type**: Web service (API-first, no frontend)
**Performance Goals**: Meeting CRUD < 2s, conflict checks < 3s for 50 participants, recurring generation < 5s for 52 weeks
**Constraints**: All queries scoped to active organization via JWT `organizationId` claim and EF Core global query filters. RFC 7807 problem details for all errors.
**Scale/Scope**: Single organization context per request, up to 50 participants per meeting

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Status | Notes |
|-----------|--------|-------|
| I. Vertical Slice Architecture | PASS | All Meetings code under `Features/Meetings/` with Models, Services, Validators, Endpoints |
| II. Partial Controller Pattern | PASS | 4 controllers (Meeting, Recurring, Participant, Calendar), each with separate endpoint files. One action per file. |
| III. Tenant Isolation by Default | PASS | `Meeting` and `MeetingParticipant` implement `IHasOrganizationId`. Global query filters auto-applied. `[EnforceOrgAccess]` on org-scoped controllers. |
| IV. Strict Single Membership Rule | PASS | No changes to membership model. Meeting operations rely on existing active membership enforcement. |
| V. Standardized Operational Errors | PASS | All endpoints use `Result.ToProblem(correlationIdProvider)` for RFC 7807 responses. `MeetingErrors` static class follows `OrganizationErrors` pattern. |
| Dev Constraint: .NET 10 + PostgreSQL | PASS | Same stack as Phase 1 & 2. |
| Dev Constraint: Automated tests | PASS | Integration tests for meeting lifecycle, unit tests for conflict detection and recurrence logic. |

**Gate Result**: ALL PASS. No violations to justify.

## Project Structure

### Documentation (this feature)

```text
specs/003-meetings/
├── plan.md              # This file
├── research.md          # Phase 0 output
├── data-model.md        # Phase 1 output
├── quickstart.md        # Phase 1 output
├── contracts/           # Phase 1 output
│   ├── meeting-endpoints.md
│   ├── participant-endpoints.md
│   ├── recurring-endpoints.md
│   └── calendar-endpoints.md
└── tasks.md             # Phase 2 output (/speckit.tasks)
```

### Source Code (repository root)

```text
MeetingAssistant/Features/
└── Meetings/
    ├── Endpoints/
    │   ├── Meeting/
    │   │   ├── MeetingController.cs
    │   │   ├── CreateMeetingEndpoint.cs
    │   │   ├── UpdateMeetingEndpoint.cs
    │   │   ├── CancelMeetingEndpoint.cs
    │   │   └── ListMeetingsEndpoint.cs
    │   ├── Recurring/
    │   │   ├── RecurringMeetingController.cs
    │   │   └── CreateRecurringMeetingEndpoint.cs
    │   ├── Participant/
    │   │   ├── ParticipantController.cs
    │   │   ├── AddParticipantEndpoint.cs
    │   │   ├── RemoveParticipantEndpoint.cs
    │   │   └── CheckConflictsEndpoint.cs
    │   └── Calendar/
    │       ├── CalendarController.cs
    │       └── GetCalendarDataEndpoint.cs
    ├── Contracts/
    │   ├── Requests/
    │   │   ├── CreateMeetingRequest.cs
    │   │   ├── UpdateMeetingRequest.cs
    │   │   ├── CreateRecurringMeetingRequest.cs
    │   │   └── AddParticipantRequest.cs
    │   └── Responses/
    │       ├── MeetingResponse.cs
    │       ├── MeetingListResponse.cs
    │       ├── ParticipantResponse.cs
    │       ├── ConflictResponse.cs
    │       └── CalendarDataResponse.cs
    ├── Models/
    │   ├── Meeting.cs
    │   ├── MeetingParticipant.cs
    │   ├── MeetingMeetingTag.cs
    │   ├── MeetingStatus.cs
    │   ├── MeetingRole.cs
    │   ├── RecurrenceFrequency.cs
    │   └── Events/
    │       └── MeetingEvents.cs
    ├── Services/
    │   ├── IMeetingService.cs
    │   ├── MeetingService.cs
    │   ├── IRecurrenceService.cs
    │   ├── RecurrenceService.cs
    │   ├── IParticipantService.cs
    │   ├── ParticipantService.cs
    │   ├── ICalendarService.cs
    │   └── CalendarService.cs
    ├── Infrastructure/
    │   └── Persistence/
    │       └── Configurations/
    │           ├── MeetingConfiguration.cs
    │           ├── MeetingParticipantConfiguration.cs
    │           └── MeetingMeetingTagConfiguration.cs
    ├── Validators/
    │   ├── CreateMeetingRequestValidator.cs
    │   ├── UpdateMeetingRequestValidator.cs
    │   ├── CreateRecurringMeetingRequestValidator.cs
    │   └── AddParticipantRequestValidator.cs
    ├── Mapping/
    │   └── MeetingMappingConfig.cs
    └── MeetingsDI.cs

tests/
├── Integration/
│   └── Meetings/
│       ├── MeetingLifecycleTests.cs
│       ├── ParticipantManagementTests.cs
│       ├── ConflictDetectionTests.cs
│       └── RecurringMeetingTests.cs
└── Unit/
    └── Meetings/
        ├── RecurrenceServiceTests.cs
        ├── ConflictDetectionTests.cs
        └── MeetingStatusTransitionTests.cs
```

**Structure Decision**: Follows the established vertical slice pattern under `Features/Meetings/` matching the `Features/Organizations/` structure from Phase 2. Contracts use the `Contracts/Requests` and `Contracts/Responses` subfolders (matching the existing pattern). Added `RemoveParticipantEndpoint.cs` per clarification that participant removal is required. Infrastructure/Persistence/Configurations holds EF Core entity configurations.

## Complexity Tracking

> No constitution violations. No complexity justifications needed.
