# Tasks: Reminders — User-Facing

**Input**: Design documents from `/specs/006-reminders-user-facing/`
**Prerequisites**: plan.md, spec.md, data-model.md, contracts/api-contracts.md, research.md

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (US1, US2, US3, US4)

---

## Phase 1: Setup (Feature Slice Initialization)

**Purpose**: Scaffold the Reminder sub-feature within the existing `Tasks` vertical slice

- [ ] T001 [P] Create Reminder entity enums in `src/Features/Tasks/Models/Entities/ReminderEnums.cs` (`ReminderScope`, `ReminderChannel`, `ReminderStatus`)
- [ ] T002 [P] Create Reminder entity in `src/Features/Tasks/Models/Entities/Reminder.cs`
- [ ] T003 [P] Create `CreateMyReminderRequest.cs` and `ReminderResponse.cs` DTOs in `src/Features/Tasks/Models/Requests/` and `src/Features/Tasks/Models/Responses/`
- [ ] T004 [P] Create `CreateMyReminderRequestValidator.cs` in `src/Features/Tasks/Validators/`
- [ ] T005 [P] Create `ReminderController.cs` definition in `src/Features/Tasks/Endpoints/Reminder/`

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core infrastructure that MUST be complete before ANY user story endpoint can be implemented

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

- [ ] T006 Configure EF Core entity mapping for `Reminder` in `src/Infrastructure/Persistence/Configurations/ReminderConfiguration.cs` (depends on T001, T002)
- [ ] T007 Add `DbSet<Reminder>` to `AppDbContext` and wire `ReminderConfiguration` (depends on T006)
- [ ] T008 [P] Create `IReminderService.cs` interface in `src/Features/Tasks/Services/` (depends on T003)
- [ ] T009 Generate and apply EF Core migration `AddRemindersTable` (depends on T007)
- [ ] T010 Create `ReminderService.cs` implementation skeleton in `src/Features/Tasks/Services/` (depends on T008)

**Checkpoint**: Foundation ready — database schema exists, service interface is defined, controller definition is in place. User story implementation can now begin.

---

## Phase 3: User Story 1 — Create a Personal Reminder (Priority: P1) 🎯 MVP

**Goal**: Users can create personal reminders via `POST /api/me/reminders`

**Independent Test**: Call `POST /api/me/reminders` with valid JWT → receive `201 Created` with `Scope=Personal`, `Channel=User`, `MeetingId=null`

### Implementation for User Story 1

- [ ] T011 [US1] Implement `ReminderService.CreateReminderAsync` in `src/Features/Tasks/Services/ReminderService.cs` (depends on T010)
- [ ] T012 [US1] Implement `CreateMyReminderEndpoint.cs` in `src/Features/Tasks/Endpoints/Reminder/` (depends on T005, T011)
- [ ] T013 [US1] Add MediatR domain event dispatch (`ReminderCreatedEvent`) in `ReminderService.CreateReminderAsync` (depends on T011)

**Checkpoint**: `POST /api/me/reminders` is fully functional. Creating a reminder returns the correct entity with all user-facing fields.

---

## Phase 4: User Story 2 — View My Reminders (Priority: P1)

**Goal**: Users can fetch paginated reminders affecting them via `GET /api/me/reminders`

**Independent Test**: Create reminders → call `GET /api/me/reminders?page=1&pageSize=20` → receive paginated list with correct `totalCount`, `totalPages`, and `items` ordered by `ReminderAtUtc` ascending

### Implementation for User Story 2

- [ ] T014 [US2] Implement `ReminderService.GetMyRemindersAsync` with pagination, tenant isolation, and `ReminderAtUtc` filter in `src/Features/Tasks/Services/ReminderService.cs` (depends on T010)
- [ ] T015 [US2] Create `PaginatedList<T>` response wrapper (if not already existing) in `src/Shared/Models/PaginatedList.cs`
- [ ] T016 [US2] Implement `ListMyRemindersEndpoint.cs` in `src/Features/Tasks/Endpoints/Reminder/` (depends on T005, T014, T015)

**Checkpoint**: `GET /api/me/reminders` returns paginated active reminders. Personal + public reminders for meetings the user participates in are both returned. Delivered/cancelled reminders are excluded.

---

## Phase 5: User Story 3 — Mark Reminder as Delivered (Priority: P2)

**Goal**: Users can mark their own personal reminders as delivered via `POST /api/me/reminders/{id}/mark-delivered`

**Independent Test**: Create a reminder → call `POST /api/me/reminders/{id}/mark-delivered` → status changes to `Delivered` with `DeliveredAtUtc` set. Re-calling returns `200 OK` idempotently.

### Implementation for User Story 3

- [ ] T017 [US3] Implement `ReminderService.MarkDeliveredAsync` with ownership/scope checks and idempotency in `src/Features/Tasks/Services/ReminderService.cs` (depends on T010)
- [ ] T018 [US3] Implement `MarkMyReminderDeliveredEndpoint.cs` in `src/Features/Tasks/Endpoints/Reminder/` (depends on T005, T017)
- [ ] T019 [US3] Add MediatR domain event dispatch (`ReminderDeliveredEvent`) in `ReminderService.MarkDeliveredAsync` (depends on T017)

**Checkpoint**: `POST /api/me/reminders/{id}/mark-delivered` marks personal reminders as delivered. Idempotent on re-invocation. Returns 403 for public reminders or other users' reminders.

---

## Phase 6: User Story 4 — Cancel a Reminder (Priority: P2)

**Goal**: Users can soft-cancel their own personal reminders via `DELETE /api/me/reminders/{id}`

**Independent Test**: Create a reminder → call `DELETE /api/me/reminders/{id}` → status changes to `Cancelled`. Attempting to cancel an already-delivered reminder returns `409 Conflict`.

### Implementation for User Story 4

- [ ] T020 [US4] Implement `ReminderService.CancelReminderAsync` with ownership checks and terminal-state guard in `src/Features/Tasks/Services/ReminderService.cs` (depends on T010)
- [ ] T021 [US4] Implement `CancelMyReminderEndpoint.cs` in `src/Features/Tasks/Endpoints/Reminder/` (depends on T005, T020)
- [ ] T022 [US4] Add MediatR domain event dispatch (`ReminderCancelledEvent`) in `ReminderService.CancelReminderAsync` (depends on T020)

**Checkpoint**: `DELETE /api/me/reminders/{id}` soft-cancels personal reminders. Returns 403 for public/other-user reminders. Returns 409 for already-delivered/cancelled reminders.

---

## Phase 7: Polish & Cross-Cutting Concerns

**Purpose**: Integration tests, tenant isolation validation, and documentation alignment

- [ ] T023 [P] Add unit tests for `ReminderService` business rules (tenant isolation, pagination logic, state transitions) in `tests/Features/Tasks/Services/ReminderServiceTests.cs`
- [ ] T024 [P] Add integration tests for all 4 endpoints covering success + error scenarios in `tests/Features/Tasks/Endpoints/Reminder/`
- [ ] T025 [P] Add contract tests verifying response shapes match `contracts/api-contracts.md` in `tests/Features/Tasks/Contracts/`
- [ ] T026 Verify database indexes `(TargetUserId, Status)` and `(MeetingId, Scope, Status)` are present in migration
- [ ] T027 Validate `quickstart.md` curl examples against running API

---

## Dependencies & Execution Order

### Phase Dependencies

| Phase | Depends On | Notes |
|-------|-----------|-------|
| Phase 1: Setup | Nothing | Can start immediately; all tasks parallel |
| Phase 2: Foundational | Phase 1 | Database schema required before endpoints |
| Phase 3: US1 (P1) | Phase 2 | MVP story — create reminders |
| Phase 4: US2 (P1) | Phase 2 | View reminders — can run in parallel with US1 after foundation |
| Phase 5: US3 (P2) | Phase 2 | Mark delivered — depends on foundation only |
| Phase 6: US4 (P2) | Phase 2 | Cancel — depends on foundation only |
| Phase 7: Polish | All user stories | Integration tests require all endpoints |

### User Story Dependencies

- **US1 (Create, P1)**: No dependencies on other stories. The most fundamental operation.
- **US2 (View, P1)**: No dependencies on other stories, but is most valuable AFTER US1 (you need reminders to view them). Can be developed in parallel with US1.
- **US3 (Mark Delivered, P2)**: Depends on US1 logically (need a reminder to mark), but service method is independent. Can be developed in parallel after foundation.
- **US4 (Cancel, P2)**: Same pattern as US3 — logically needs existing reminders, but implementation is independent.

### Within Each User Story

1. Service method implementation (depends on interface + entity)
2. Endpoint implementation (depends on service method + controller definition)
3. Domain event dispatch (depends on service method)

### Parallel Opportunities

- **Phase 1**: All 5 tasks (T001–T005) are in different files with no interdependencies — fully parallel
- **Phase 2**: T008 (interface) can run in parallel with T006/T007 (EF config), T009 (migration) requires T007
- **Phase 3–6**: Once foundation is complete, all 4 user stories can be implemented in parallel by different developers:
  - Dev A: US1 (Create) — T011, T012, T013
  - Dev B: US2 (View) — T014, T015, T016
  - Dev C: US3 (Mark Delivered) — T017, T018, T019
  - Dev D: US4 (Cancel) — T020, T021, T022
- **Phase 7**: T023, T024, T025 are all in different test files — parallel

---

## Parallel Example: User Story 1

```bash
# Launch all models + interface work together (Phase 1):
Task: "Create Reminder entity enums in src/Features/Tasks/Models/Entities/ReminderEnums.cs"
Task: "Create Reminder entity in src/Features/Tasks/Models/Entities/Reminder.cs"
Task: "Create CreateMyReminderRequest and ReminderResponse DTOs"
Task: "Create CreateMyReminderRequestValidator"
Task: "Create ReminderController.cs definition"

# After foundation, launch US1 implementation in sequence:
Task: "Implement ReminderService.CreateReminderAsync"
Task: "Implement CreateMyReminderEndpoint.cs"
Task: "Add ReminderCreatedEvent dispatch"
```

---

## Implementation Strategy

### MVP First (User Story 1 + 2)

1. Complete Phase 1: Setup
2. Complete Phase 2: Foundational (CRITICAL — blocks all stories)
3. Complete Phase 3: US1 (Create reminder) → **MVP checkpoint** — users can create reminders
4. Complete Phase 4: US2 (View reminders) → **MVP checkpoint** — users can create AND view reminders
5. **STOP and VALIDATE**: Test both endpoints independently via quickstart.md
6. Deploy/demo if ready

### Incremental Delivery

1. Setup + Foundational → Foundation ready
2. US1 (Create) → Test independently → Deploy/Demo (basic MVP)
3. US2 (View) → Test independently → Deploy/Demo (usable MVP)
4. US3 (Mark Delivered) → Test independently → Deploy/Demo
5. US4 (Cancel) → Test independently → Deploy/Demo
6. Polish phase → Full feature complete

### Parallel Team Strategy

With multiple developers:

1. One developer completes Phase 1 + Phase 2 (setup + foundation)
2. Once Foundational is done:
   - Developer A: US1 (Create) + US2 (View) — closely related, sequential within
   - Developer B: US3 (Mark Delivered) + US4 (Cancel) — closely related, sequential within
3. Stories complete and integrate independently
4. Phase 7 (Polish) runs after all stories merge

---

## Notes

- [P] tasks = different files, no dependencies
- [Story] label maps task to specific user story for traceability
- Each user story is independently completable once Phase 2 (Foundational) is done
- Domain events (`ReminderCreatedEvent`, `ReminderDeliveredEvent`, `ReminderCancelledEvent`) are dispatched via MediatR `IPublisher` — same pattern as other features
- Tenant isolation is enforced in the service layer (`OrganizationId` filter) AND via EF Core global query filter
- `CancellationToken` parameter must be present on all async service methods and endpoint actions
- All endpoints use `result.ToProblem(correlationIdProvider)` for error responses per Constitution §V
- No test tasks are generated by default (TDD was not requested). Phase 7 includes optional integration/contract tests.
