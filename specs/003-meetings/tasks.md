# Tasks: Meetings

**Input**: Design documents from `/specs/003-meetings/`
**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/

**Tests**: Required by constitution — "No new features can be implemented without automated Contract/Integration tests covering the Acceptance Scenarios."

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3)
- Include exact file paths in descriptions

## Path Conventions

- **Main project**: `MeetingAssistant/Features/Meetings/`
- **Tests**: `tests/Integration/Meetings/`, `tests/Unit/Meetings/`
- **Shared**: `MeetingAssistant/Shared/`

---

## Phase 1: Setup

**Purpose**: Feature folder structure and shared infrastructure for Meetings

- [X] T001 Create Meetings feature folder structure per plan.md: `MeetingAssistant/Features/Meetings/{Endpoints/Meeting, Endpoints/Recurring, Endpoints/Participant, Endpoints/Calendar, Contracts/Requests, Contracts/Responses, Models/Events, Services, Validators, Infrastructure/Persistence/Configurations, Mapping}`
- [X] T002 [P] Create MeetingStatus enum (Scheduled=0, InProgress=1, Completed=2, Cancelled=3, Failed=4) in `MeetingAssistant/Features/Meetings/Models/MeetingStatus.cs`
- [X] T003 [P] Create MeetingRole enum (Host=0, CoHost=1, Participant=2, Observer=3) in `MeetingAssistant/Features/Meetings/Models/MeetingRole.cs`
- [X] T004 [P] Create RecurrenceFrequency enum (Daily=0, Weekly=1, Monthly=2) in `MeetingAssistant/Features/Meetings/Models/RecurrenceFrequency.cs`
- [X] T005 [P] Create MeetingErrors static class with error definitions (NotFound, InvalidStatus, NotHost, AlreadyParticipant, NotOrgMember, LastHost, InvalidRecurrence, ConflictDetected) in `MeetingAssistant/Shared/Errors/MeetingErrors.cs`

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Entities, EF configurations, DbContext registration, and DI — MUST be complete before ANY user story

**CRITICAL**: No user story work can begin until this phase is complete

- [X] T006 Create Meeting entity inheriting BaseEntity, implementing IHasOrganizationId with fields: OrganizationId, Title, Description, ScheduledStartUtc, ScheduledEndUtc, Status (MeetingStatus), RecurrenceConfig (owned nullable). Add navigation properties for Participants and Tags collections in `MeetingAssistant/Features/Meetings/Models/Meeting.cs`
- [X] T007 [P] Create MeetingParticipant entity inheriting BaseEntity, implementing IHasOrganizationId with fields: MeetingId, OrganizationId, UserId, MeetingRole. Add navigation properties for Meeting and User in `MeetingAssistant/Features/Meetings/Models/MeetingParticipant.cs`
- [X] T008 [P] Create MeetingMeetingTag junction entity (no BaseEntity) with MeetingId and MeetingTagId. Add navigation properties for Meeting and MeetingTag in `MeetingAssistant/Features/Meetings/Models/MeetingMeetingTag.cs`
- [X] T009 Create MeetingConfiguration with table mapping, property constraints (Title max 200, Description max 2000), indexes (IX_Meetings_OrganizationId, IX_Meetings_OrgId_ScheduledStartUtc, IX_Meetings_OrgId_Status), and owned entity mapping for RecurrenceConfig as JSONB in `MeetingAssistant/Features/Meetings/Infrastructure/Persistence/Configurations/MeetingConfiguration.cs`
- [X] T010 [P] Create MeetingParticipantConfiguration with unique constraint on (MeetingId, UserId), indexes (IX_MeetingParticipants_MeetingId_UserId unique, IX_MeetingParticipants_UserId, IX_MeetingParticipants_OrganizationId) in `MeetingAssistant/Features/Meetings/Infrastructure/Persistence/Configurations/MeetingParticipantConfiguration.cs`
- [X] T011 [P] Create MeetingMeetingTagConfiguration with composite PK (MeetingId, MeetingTagId), foreign keys to Meeting and MeetingTag in `MeetingAssistant/Features/Meetings/Infrastructure/Persistence/Configurations/MeetingMeetingTagConfiguration.cs`
- [X] T012 Register DbSet properties (Meetings, MeetingParticipants, MeetingMeetingTags) in `MeetingAssistant/Infrastructure/Persistence/DbContext/ApplicationDbContext.cs`
- [X] T013 Generate EF Core migration by running `dotnet ef migrations add AddMeetings`
- [X] T014 [P] Create domain event records (MeetingCreatedEvent, MeetingUpdatedEvent, MeetingCancelledEvent, MeetingStartedEvent, MeetingEndedEvent) implementing IDomainEvent in `MeetingAssistant/Features/Meetings/Models/Events/MeetingEvents.cs`
- [X] T015 [P] Create CreateMeetingRequest sealed record (Title, Description, ScheduledStartUtc, ScheduledEndUtc, TagIds) in `MeetingAssistant/Features/Meetings/Contracts/Requests/CreateMeetingRequest.cs`
- [X] T016 [P] Create UpdateMeetingRequest sealed record (Title, Description, ScheduledStartUtc, ScheduledEndUtc, TagIds) in `MeetingAssistant/Features/Meetings/Contracts/Requests/UpdateMeetingRequest.cs`
- [X] T017 [P] Create AddParticipantRequest sealed record (UserId, MeetingRole) in `MeetingAssistant/Features/Meetings/Contracts/Requests/AddParticipantRequest.cs`
- [X] T018a [P] Create all response DTOs as sealed records: MeetingResponse, MeetingListResponse (with pagination), ParticipantResponse, ConflictResponse, CalendarDataResponse per contracts/ specs in `MeetingAssistant/Features/Meetings/Contracts/Responses/`
- [X] T018b [P] Create CreateMeetingRequestValidator (Title required 1-200 chars, ScheduledStartUtc required and in future, ScheduledEndUtc required and after start, Description max 2000) in `MeetingAssistant/Features/Meetings/Validators/CreateMeetingRequestValidator.cs`
- [X] T018c [P] Create UpdateMeetingRequestValidator (Title 1-200 if provided, ScheduledEndUtc after start if both provided, Description max 2000) in `MeetingAssistant/Features/Meetings/Validators/UpdateMeetingRequestValidator.cs`
- [X] T019 [P] Create AddParticipantRequestValidator (UserId required, MeetingRole required and valid enum value) in `MeetingAssistant/Features/Meetings/Validators/AddParticipantRequestValidator.cs`
- [X] T020 [P] Create MeetingMappingConfig implementing IRegister for Mapster mappings (Meeting to MeetingResponse, MeetingParticipant to ParticipantResponse) in `MeetingAssistant/Features/Meetings/Mapping/MeetingMappingConfig.cs`
- [X] T021 Create MeetingsDI extension class with empty `AddMeetingsFeature()` method (services will be registered incrementally per story phase) in `MeetingAssistant/Features/Meetings/MeetingsDI.cs`
- [X] T022 Register MeetingsDI in Program.cs by adding `builder.Services.AddMeetingsFeature()` in `MeetingAssistant/Program.cs`

**Checkpoint**: Foundation ready — entities, database, contracts, validators, and DI all in place. User story implementation can begin.

---

## Phase 3: User Story 1 — Create and Manage a Meeting (Priority: P1) MVP

**Goal**: Organization Admins/Members can create meetings with title, description, times, and optional tags. Host can update and cancel meetings. Creator auto-assigned as Host.

**Independent Test**: Create a meeting, verify it appears in the list, update its title, cancel it.

### Tests for User Story 1

- [ ] T023 [P] [US1] Integration test: create meeting with valid data, verify 200 response with MeetingResponse, verify creator is Host participant in `tests/Integration/Meetings/CreateMeetingTests.cs`
- [ ] T024 [P] [US1] Integration test: create meeting as Guest role, verify 403 Forbidden in `tests/Integration/Meetings/CreateMeetingTests.cs`
- [ ] T025 [P] [US1] Integration test: create meeting with end time before start time, verify 400 validation error in `tests/Integration/Meetings/CreateMeetingTests.cs`
- [ ] T026 [P] [US1] Integration test: update meeting as Host while Scheduled, verify updated fields returned in `tests/Integration/Meetings/UpdateMeetingTests.cs`
- [ ] T026b [P] [US1] Integration test: update meeting tags — create meeting with tags, update with new tagIds list, verify old tags replaced with new tags in response in `tests/Integration/Meetings/UpdateMeetingTests.cs`
- [ ] T027 [P] [US1] Integration test: update meeting as non-Host, verify 403 in `tests/Integration/Meetings/UpdateMeetingTests.cs`
- [ ] T028 [P] [US1] Integration test: cancel meeting as Host, verify status changes to Cancelled in `tests/Integration/Meetings/CancelMeetingTests.cs`
- [ ] T029 [P] [US1] Integration test: cancel meeting as CoHost, verify 403 in `tests/Integration/Meetings/CancelMeetingTests.cs`
- [ ] T030 [P] [US1] Integration test: update Cancelled meeting, verify 403 rejection in `tests/Integration/Meetings/UpdateMeetingTests.cs`
- [ ] T031 [P] [US1] Integration test: create meeting with tag association, verify tags in response in `tests/Integration/Meetings/CreateMeetingTests.cs`

### Implementation for User Story 1

- [X] T032 [US1] Register IMeetingService/MeetingService as scoped in `MeetingsDI.AddMeetingsFeature()` in `MeetingAssistant/Features/Meetings/MeetingsDI.cs`
- [X] T033 [US1] Create IMeetingService interface with methods: CreateMeetingAsync, UpdateMeetingAsync, CancelMeetingAsync in `MeetingAssistant/Features/Meetings/Services/IMeetingService.cs`
- [X] T033 [US1] Implement MeetingService.CreateMeetingAsync: validate org role (Admin/Member only, reject Guest), create Meeting entity, auto-add creator as Host MeetingParticipant, associate tags (validate active and same org), raise MeetingCreatedEvent, save and return MeetingResponse in `MeetingAssistant/Features/Meetings/Services/MeetingService.cs`
- [X] T034 [US1] Implement MeetingService.UpdateMeetingAsync: find meeting, verify caller is Host, verify status is Scheduled (reject InProgress/Completed/Cancelled — partial InProgress updates deferred to Phase 4), update fields, replace tag associations if tagIds provided, raise MeetingUpdatedEvent, save and return MeetingResponse in `MeetingAssistant/Features/Meetings/Services/MeetingService.cs`
- [X] T035 [US1] Implement MeetingService.CancelMeetingAsync: find meeting, verify caller is Host (not CoHost), verify status is Scheduled, set status to Cancelled, raise MeetingCancelledEvent, save in `MeetingAssistant/Features/Meetings/Services/MeetingService.cs`
- [X] T036 [US1] Create MeetingController partial class definition with route `api/organizations/{orgId:guid}/meetings`, inject IMeetingService and ICorrelationIdProvider, apply [EnforceOrgAccess] in `MeetingAssistant/Features/Meetings/Endpoints/Meeting/MeetingController.cs`
- [X] T037 [P] [US1] Create CreateMeetingEndpoint with [HttpPost], extract userId from claims, check org role authorization (reject Guest), call CreateMeetingAsync, return Ok or ToProblem in `MeetingAssistant/Features/Meetings/Endpoints/Meeting/CreateMeetingEndpoint.cs`
- [X] T038 [P] [US1] Create UpdateMeetingEndpoint with [HttpPut("{id:guid}")], extract userId, call UpdateMeetingAsync, return Ok or ToProblem in `MeetingAssistant/Features/Meetings/Endpoints/Meeting/UpdateMeetingEndpoint.cs`
- [X] T039 [P] [US1] Create CancelMeetingEndpoint with [HttpDelete("{id:guid}")], extract userId, call CancelMeetingAsync, return Ok or ToProblem in `MeetingAssistant/Features/Meetings/Endpoints/Meeting/CancelMeetingEndpoint.cs`

**Checkpoint**: Meeting CRUD is functional. Can create, update, cancel meetings. Creator is auto-assigned as Host. Tags can be associated.

---

## Phase 4: User Story 2 — List and Filter Meetings (Priority: P1)

**Goal**: Organization members view meetings filtered by upcoming or past, with pagination, participant count, and tags.

**Independent Test**: Create meetings with different statuses/dates, filter by "upcoming" and "past", verify correct results.

### Tests for User Story 2

- [ ] T040 [P] [US2] Integration test: list meetings with filter=upcoming, verify only future Scheduled/InProgress meetings returned sorted by start ASC in `tests/Integration/Meetings/ListMeetingsTests.cs`
- [ ] T041 [P] [US2] Integration test: list meetings with filter=past, verify only Completed/Cancelled/past-end meetings returned sorted by start DESC in `tests/Integration/Meetings/ListMeetingsTests.cs`
- [ ] T042 [P] [US2] Integration test: list meetings with no filter, verify all meetings returned in `tests/Integration/Meetings/ListMeetingsTests.cs`
- [ ] T043 [P] [US2] Integration test: list meetings with pagination (page=1, pageSize=2), verify paged response with totalCount in `tests/Integration/Meetings/ListMeetingsTests.cs`

### Implementation for User Story 2

- [X] T044 [US2] Add ListMeetingsAsync method to IMeetingService accepting filter (upcoming/past/null), page, pageSize parameters in `MeetingAssistant/Features/Meetings/Services/IMeetingService.cs`
- [X] T045 [US2] Implement MeetingService.ListMeetingsAsync: query meetings by org (auto-filtered), apply filter logic — upcoming: ScheduledStartUtc > now AND Status IN (Scheduled, InProgress); past: Status IN (Completed, Cancelled) OR ScheduledEndUtc < now; no filter: all meetings — include participant count and tags, apply pagination (upcoming: order by start ASC, past/all: order by start DESC), return MeetingListResponse in `MeetingAssistant/Features/Meetings/Services/MeetingService.cs`
- [X] T046 [US2] Create ListMeetingsEndpoint with [HttpGet], accept filter/page/pageSize query params, call ListMeetingsAsync, return Ok in `MeetingAssistant/Features/Meetings/Endpoints/Meeting/ListMeetingsEndpoint.cs`

**Checkpoint**: Meeting listing with filtering and pagination is functional. All P1 stories complete — MVP achieved.

---

## Phase 5: User Story 3 — Add and Manage Participants (Priority: P2)

**Goal**: Hosts/CoHosts add participants with roles. Hosts can remove anyone; CoHosts remove Participants/Observers only. Duplicate prevention. Org membership validation. Last Host protection.

**Independent Test**: Create meeting, add participants with roles, attempt duplicate, remove participant, verify role hierarchy enforcement.

### Tests for User Story 3

- [ ] T047 [P] [US3] Integration test: add participant as Host, verify 200 with ParticipantResponse in `tests/Integration/Meetings/ParticipantManagementTests.cs`
- [ ] T048 [P] [US3] Integration test: add duplicate participant, verify 409 Conflict in `tests/Integration/Meetings/ParticipantManagementTests.cs`
- [ ] T049 [P] [US3] Integration test: add non-org-member as participant, verify 404 in `tests/Integration/Meetings/ParticipantManagementTests.cs`
- [ ] T050 [P] [US3] Integration test: add participant as Participant role (not Host/CoHost), verify 403 in `tests/Integration/Meetings/ParticipantManagementTests.cs`
- [ ] T051 [P] [US3] Integration test: Host removes a Participant, verify 204 in `tests/Integration/Meetings/ParticipantManagementTests.cs`
- [ ] T052 [P] [US3] Integration test: CoHost tries to remove Host, verify 403 in `tests/Integration/Meetings/ParticipantManagementTests.cs`
- [ ] T053 [P] [US3] Integration test: attempt to remove last Host, verify 403 in `tests/Integration/Meetings/ParticipantManagementTests.cs`

### Implementation for User Story 3

- [X] T054 [US3] Create IParticipantService interface with methods: AddParticipantAsync, RemoveParticipantAsync in `MeetingAssistant/Features/Meetings/Services/IParticipantService.cs`
- [X] T055 [US3] Implement ParticipantService.AddParticipantAsync: find meeting, verify caller is Host or CoHost, verify target user is active org member, check for duplicate, create MeetingParticipant, save and return ParticipantResponse in `MeetingAssistant/Features/Meetings/Services/ParticipantService.cs`
- [X] T056 [US3] Implement ParticipantService.RemoveParticipantAsync: find meeting, verify caller role hierarchy (Host removes anyone, CoHost removes Participant/Observer only), verify not removing last Host, delete MeetingParticipant, save in `MeetingAssistant/Features/Meetings/Services/ParticipantService.cs`
- [X] T057 [US3] Create ParticipantController partial class definition with route `api/meetings/{meetingId:guid}/participants`, inject IParticipantService and ICorrelationIdProvider in `MeetingAssistant/Features/Meetings/Endpoints/Participant/ParticipantController.cs` — NOTE: no `{orgId}` in route, so `[EnforceOrgAccess]` is not applied. Tenant isolation is enforced via EF Core global query filter on Meeting lookup (meeting belongs to org, query filter scopes by active org)
- [X] T058 [P] [US3] Create AddParticipantEndpoint with [HttpPost], extract userId, call AddParticipantAsync, return Ok or ToProblem in `MeetingAssistant/Features/Meetings/Endpoints/Participant/AddParticipantEndpoint.cs`
- [X] T059 [P] [US3] Create RemoveParticipantEndpoint with [HttpDelete("{userId:guid}")], extract caller userId, call RemoveParticipantAsync, return NoContent or ToProblem in `MeetingAssistant/Features/Meetings/Endpoints/Participant/RemoveParticipantEndpoint.cs`

**Checkpoint**: Full participant management with role hierarchy enforcement is functional.

---

## Phase 6: User Story 4 — Check Scheduling Conflicts (Priority: P2)

**Goal**: Hosts/CoHosts check participant scheduling conflicts within the active organization. Returns conflicting meetings per participant.

**Independent Test**: Create two overlapping meetings, add same user to both, run conflict check, verify overlap detected.

### Tests for User Story 4

- [ ] T060 [P] [US4] Integration test: check conflicts with overlapping meetings, verify ConflictResponse lists affected participants and conflicting meetings in `tests/Integration/Meetings/ConflictDetectionTests.cs`
- [ ] T061 [P] [US4] Integration test: check conflicts with no overlaps, verify empty conflicts list in `tests/Integration/Meetings/ConflictDetectionTests.cs`
- [ ] T062 [P] [US4] Integration test: cancelled meeting not considered a conflict in `tests/Integration/Meetings/ConflictDetectionTests.cs`
- [ ] T063 [P] [US4] Unit test: conflict detection overlap logic (A.Start < B.End AND A.End > B.Start) with edge cases (adjacent, exact overlap, partial overlap) in `tests/Unit/Meetings/ConflictDetectionTests.cs`

### Implementation for User Story 4

- [X] T064 [US4] Add CheckConflictsAsync method to IParticipantService in `MeetingAssistant/Features/Meetings/Services/IParticipantService.cs`
- [X] T065 [US4] Implement ParticipantService.CheckConflictsAsync: get all participants of current meeting, for each participant query other meetings where they are participants and time overlaps (Start < End AND End > Start), exclude Cancelled/Completed/Failed meetings, scope to active org, return ConflictResponse in `MeetingAssistant/Features/Meetings/Services/ParticipantService.cs`
- [X] T066 [US4] Create CheckConflictsEndpoint with [HttpGet("conflicts")], verify caller is Host or CoHost, call CheckConflictsAsync, return Ok in `MeetingAssistant/Features/Meetings/Endpoints/Participant/CheckConflictsEndpoint.cs`

**Checkpoint**: Conflict detection is functional. All P2 stories complete.

---

## Phase 7: User Story 5 — Create Recurring Meetings (Priority: P3)

**Goal**: Users create recurring meetings from a recurrence config. System generates independent meeting instances eagerly. Default 12-week horizon for open-ended recurrences.

**Independent Test**: Create weekly recurring meeting for 4 weeks, verify 4 instances created, cancel one, verify others unaffected.

### Tests for User Story 5

- [ ] T067 [P] [US5] Unit test: RecurrenceService generates correct dates for weekly recurrence with specific days in `tests/Unit/Meetings/RecurrenceServiceTests.cs`
- [ ] T068 [P] [US5] Unit test: RecurrenceService generates correct dates for daily recurrence with interval in `tests/Unit/Meetings/RecurrenceServiceTests.cs`
- [ ] T069 [P] [US5] Unit test: RecurrenceService generates correct dates for monthly recurrence in `tests/Unit/Meetings/RecurrenceServiceTests.cs`
- [ ] T070 [P] [US5] Unit test: RecurrenceService applies default 12-week horizon when no endsAtUtc in `tests/Unit/Meetings/RecurrenceServiceTests.cs`
- [ ] T071 [P] [US5] Unit test: RecurrenceService rejects invalid config (zero interval, empty daysOfWeek for weekly) in `tests/Unit/Meetings/RecurrenceServiceTests.cs`
- [ ] T072 [P] [US5] Integration test: create recurring meeting, verify correct number of instances generated with status Scheduled in `tests/Integration/Meetings/RecurringMeetingTests.cs`
- [ ] T073 [P] [US5] Integration test: cancel one recurring instance, verify others remain Scheduled in `tests/Integration/Meetings/RecurringMeetingTests.cs`

### Implementation for User Story 5

- [ ] T074 [US5] Create CreateRecurringMeetingRequest record with Title, Description, ScheduledStartTimeUtc (TimeSpan), ScheduledEndTimeUtc (TimeSpan), Recurrence (frequency, interval, daysOfWeek, endsAtUtc), TagIds in `MeetingAssistant/Features/Meetings/Contracts/Requests/CreateRecurringMeetingRequest.cs`
- [ ] T075 [US5] Create CreateRecurringMeetingRequestValidator (Title 1-200, times valid, recurrence.frequency valid, interval 1-12, daysOfWeek required for Weekly, endsAtUtc after now if provided) in `MeetingAssistant/Features/Meetings/Validators/CreateRecurringMeetingRequestValidator.cs`
- [ ] T076 [US5] Create IRecurrenceService interface with GenerateMeetingInstancesAsync method in `MeetingAssistant/Features/Meetings/Services/IRecurrenceService.cs`
- [ ] T077 [US5] Implement RecurrenceService.GenerateMeetingInstancesAsync: compute occurrence dates from recurrence config (Daily/Weekly/Monthly with interval), apply default 12-week horizon if no endsAtUtc, cap at 52 weeks max, create independent Meeting entities for each occurrence with RecurrenceConfig stored, add creator as Host to each, associate tags, raise MeetingCreatedEvent for each, save all and return list in `MeetingAssistant/Features/Meetings/Services/RecurrenceService.cs`
- [ ] T078 [US5] Create RecurringMeetingController partial class definition with route `api/organizations/{orgId:guid}/meetings/recurring`, inject IRecurrenceService and ICorrelationIdProvider, apply [EnforceOrgAccess] in `MeetingAssistant/Features/Meetings/Endpoints/Recurring/RecurringMeetingController.cs`
- [ ] T079 [US5] Create CreateRecurringMeetingEndpoint with [HttpPost], check org role (reject Guest), extract userId, call GenerateMeetingInstancesAsync, return Ok with generated count and meeting list in `MeetingAssistant/Features/Meetings/Endpoints/Recurring/CreateRecurringMeetingEndpoint.cs`

**Checkpoint**: Recurring meeting creation is functional with all recurrence patterns.

---

## Phase 8: User Story 6 — View Calendar Data (Priority: P3)

**Goal**: Organization members view meetings for a specific week, ordered by day and time.

**Independent Test**: Create meetings across different days of a week, request calendar data, verify all appear.

### Tests for User Story 6

- [ ] T080 [P] [US6] Integration test: get calendar data for a week with meetings, verify all meetings in range returned ordered by start time in `tests/Integration/Meetings/CalendarDataTests.cs`
- [ ] T081 [P] [US6] Integration test: get calendar data for empty week, verify empty result in `tests/Integration/Meetings/CalendarDataTests.cs`
- [ ] T082 [P] [US6] Integration test: get calendar data with default week (no param), verify current week used in `tests/Integration/Meetings/CalendarDataTests.cs`

### Implementation for User Story 6

- [ ] T083 [US6] Create ICalendarService interface with GetCalendarDataAsync method accepting week date parameter in `MeetingAssistant/Features/Meetings/Services/ICalendarService.cs`
- [ ] T084 [US6] Implement CalendarService.GetCalendarDataAsync: snap input date to Monday, compute 7-day range, query meetings where ScheduledStartUtc within range (auto-scoped to org), include participant count and tags, order by ScheduledStartUtc ASC, return CalendarDataResponse in `MeetingAssistant/Features/Meetings/Services/CalendarService.cs`
- [ ] T085 [US6] Create CalendarController partial class definition with route `api/organizations/{orgId:guid}/calendar`, inject ICalendarService and ICorrelationIdProvider, apply [EnforceOrgAccess] in `MeetingAssistant/Features/Meetings/Endpoints/Calendar/CalendarController.cs`
- [ ] T086 [US6] Create GetCalendarDataEndpoint with [HttpGet], accept optional week query param, call GetCalendarDataAsync, return Ok in `MeetingAssistant/Features/Meetings/Endpoints/Calendar/GetCalendarDataEndpoint.cs`

**Checkpoint**: Calendar data retrieval is functional. All P3 stories complete.

---

## Phase 9: Polish & Cross-Cutting Concerns

**Purpose**: Final validation, status transition tests, and cleanup

- [ ] T087 [P] Unit test: meeting status transition validation (Scheduled->Cancelled allowed, Cancelled->anything rejected, Completed->anything rejected) in `tests/Unit/Meetings/MeetingStatusTransitionTests.cs`
- [ ] T088 [P] Integration test: full meeting lifecycle — create, add participants, check conflicts, update, cancel — end-to-end in `tests/Integration/Meetings/MeetingLifecycleTests.cs`
- [ ] T089 [P] Integration test: tenant isolation — verify meeting from org A is not visible to org B member in `tests/Integration/Meetings/MeetingLifecycleTests.cs`
- [ ] T090 Integration test: verify domain events are raised — create a meeting and assert MeetingCreatedEvent is published, update it and assert MeetingUpdatedEvent is published, cancel it and assert MeetingCancelledEvent is published. Use MediatR notification handler spy/mock to capture events. File: `tests/Integration/Meetings/MeetingDomainEventTests.cs`
- [ ] T091 Run full test suite and verify all tests pass

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies — can start immediately
- **Foundational (Phase 2)**: Depends on Setup — BLOCKS all user stories
- **User Stories (Phase 3–8)**: All depend on Foundational phase completion
  - US1 and US2 (both P1) can proceed in parallel after Phase 2
  - US3 and US4 (both P2) can proceed in parallel after Phase 2
  - US5 and US6 (both P3) can proceed in parallel after Phase 2
- **Polish (Phase 9)**: Depends on all user stories being complete

### User Story Dependencies

- **US1 (P1) — Create & Manage**: After Phase 2 — no dependencies on other stories
- **US2 (P1) — List & Filter**: After Phase 2 — no dependencies on other stories (uses same Meeting entity)
- **US3 (P2) — Participants**: After Phase 2 — no dependencies on other stories (uses MeetingParticipant entity from Phase 2)
- **US4 (P2) — Conflicts**: After Phase 2 — benefits from US3 (participant data) but can test independently
- **US5 (P3) — Recurring**: After Phase 2 — creates meetings, no dependency on other stories
- **US6 (P3) — Calendar**: After Phase 2 — queries meetings, no dependency on other stories

### Within Each User Story

- Tests written first (fail before implementation)
- Services before endpoints
- Core implementation before integration
- Story complete before moving to next priority

### Parallel Opportunities

- T002, T003, T004, T005 (Setup enums/errors) — all parallel
- T007, T008 (entities), T010, T011 (configs), T014–T020 (contracts/validators/mapping) — all parallel
- All test tasks marked [P] within a story — parallel
- Endpoint files within a story marked [P] — parallel
- US1 and US2 can run in parallel after Phase 2
- US3 and US4 can run in parallel
- US5 and US6 can run in parallel

---

## Parallel Example: User Story 1

```text
# All US1 tests can be written in parallel:
T023: Integration test - create meeting valid data
T024: Integration test - create meeting as Guest
T025: Integration test - create meeting invalid times
T026: Integration test - update meeting as Host
T027: Integration test - update meeting as non-Host
T028: Integration test - cancel meeting
T029: Integration test - cancel as CoHost
T030: Integration test - update Cancelled meeting
T031: Integration test - create meeting with tags

# After MeetingService is implemented, endpoints can be written in parallel:
T037: CreateMeetingEndpoint
T038: UpdateMeetingEndpoint
T039: CancelMeetingEndpoint
```

---

## Implementation Strategy

### MVP First (User Stories 1 + 2)

1. Complete Phase 1: Setup (T001–T005)
2. Complete Phase 2: Foundational (T006–T022)
3. Complete Phase 3: US1 — Create & Manage (T023–T039)
4. Complete Phase 4: US2 — List & Filter (T040–T046)
5. **STOP and VALIDATE**: Full meeting CRUD with listing is functional
6. Deploy/demo if ready

### Incremental Delivery

1. Setup + Foundational → Foundation ready
2. US1 + US2 → Test independently → Deploy (MVP — meeting CRUD + listing)
3. US3 + US4 → Test independently → Deploy (participant management + conflicts)
4. US5 + US6 → Test independently → Deploy (recurring + calendar)
5. Each pair adds value without breaking previous stories

### Solo Developer Strategy (Recommended)

1. Complete Setup + Foundational sequentially
2. US1 → US2 (both P1, sequential — same MeetingService file)
3. US3 → US4 (both P2, sequential — same ParticipantService file)
4. US5 → US6 (both P3, sequential — different service files, could parallel)
5. Polish phase last

---

## Notes

- [P] tasks = different files, no dependencies
- [Story] label maps task to specific user story for traceability
- Constitution requires tests — all acceptance scenarios must have automated coverage
- Commit after each task or logical group
- Stop at any checkpoint to validate story independently
