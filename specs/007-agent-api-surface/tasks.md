# Tasks: Agent-Callable API Surface

**Input**: Design documents from `/specs/007-agent-api-surface/`  
**Prerequisites**: plan.md (required), spec.md (required for user stories), data-model.md, contracts/

**Tests**: Integration tests are included per the implementation plan (Phase 1E).

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3)
- Include exact file paths in descriptions

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Scaffold the `AgentApi` feature slice under `src/Features/`

- [x] T001 Create `src/Features/AgentApi/` folder structure: `Endpoints/`, `Models/`, `Services/`, `Validators/`
- [x] T002 Create `Endpoints/Reminder/` and `Endpoints/Context/` subdirectories
- [x] T003 Create `Models/Requests/` and `Models/Responses/` subdirectories
- [x] T004 [P] Add `AgentApi` namespace and feature registration in `Program.cs`

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core auth infrastructure that MUST be complete before ANY user story can be implemented

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

- [x] T005 Register `AgentJwt` authentication scheme in `Program.cs` with separate signing key
- [x] T006 Define `AgentOnly` authorization policy in `Program.cs`
- [x] T007 Create `IAgentAuthService` interface in `src/Features/AgentApi/Services/IAgentAuthService.cs`
- [x] T008 Implement `AgentAuthService` in `src/Features/AgentApi/Services/AgentAuthService.cs`
- [x] T009 Create `IAgentContextProvider` interface in `src/Shared/` (or `Infrastructure/`) to resolve `organizationId` and `meetingId` from agent claims
- [x] T010 Implement `AgentContextProvider` to extract claims from `HttpContext.User`
- [x] T011 Configure per-meeting rate limiter (`AgentPerMeeting` policy) in `Program.cs`
- [x] T012 Create `CreateAgentReminderRequest` sealed record in `src/Features/AgentApi/Models/Requests/CreateAgentReminderRequest.cs`
- [x] T013 Create `CreateAgentReminderRequestValidator` in `src/Features/AgentApi/Validators/CreateAgentReminderRequestValidator.cs`

**Checkpoint**: Foundation ready — agent auth, context provider, rate limiting, and request model are all in place. User story implementation can now begin in parallel.

---

## Phase 3: User Story 1 — Agent Retrieves Meeting Context (Priority: P1) 🎯 MVP

**Goal**: Allow the agent to query organization profile and meeting participant roster in real time.

**Independent Test**: Send an agent-authenticated request to `GET /api/agent/organization` and `GET /api/agent/meetings/{id}/members`. Verify correct data is returned and cross-tenant/mismatched-meeting requests return 403.

### Implementation for User Story 1

- [x] T014 [P] [US1] Create `AgentOrganizationResponse` sealed record in `src/Features/AgentApi/Models/Responses/AgentOrganizationResponse.cs`
- [x] T015 [P] [US1] Create `AgentMemberResponse` sealed record in `src/Features/AgentApi/Models/Responses/AgentMemberResponse.cs`
- [x] T016 [US1] Create `IAgentContextService` interface in `src/Features/AgentApi/Services/IAgentContextService.cs`
- [x] T017 [US1] Implement `AgentContextService` in `src/Features/AgentApi/Services/AgentContextService.cs`
- [x] T018 [US1] Create `AgentContextController.cs` definition file in `src/Features/AgentApi/Endpoints/Context/AgentContextController.cs`
- [x] T019 [P] [US1] Implement `GetOrganizationEndpoint.cs` in `src/Features/AgentApi/Endpoints/Context/GetOrganizationEndpoint.cs`
- [x] T020 [P] [US1] Implement `GetMeetingMembersEndpoint.cs` in `src/Features/AgentApi/Endpoints/Context/GetMeetingMembersEndpoint.cs`
- [x] T021 [US1] Add Mapster configuration for `AgentOrganizationResponse` and `AgentMemberResponse` mappings
- [x] T022 [US1] Add integration tests: `AgentContextTests.cs` in `tests/Integration/AgentApi/` — test org query, member query, meeting mismatch → 403, cross-tenant → 403

**Checkpoint**: At this point, User Story 1 should be fully functional and testable independently.

---

## Phase 4: User Story 2 — Agent Creates and Surfaces Reminders (Priority: P1)

**Goal**: Allow the agent to create public/personal reminders, list public reminders for a meeting, mark them delivered, and cancel them.

**Independent Test**: Use agent token to create a public reminder, query it via `GET /api/agent/meetings/{id}/reminders`, mark it delivered, verify it no longer surfaces. Verify personal reminders are never returned in public lists.

### Implementation for User Story 2

- [x] T023 [P] [US2] Create `AgentReminderResponse` sealed record in `src/Features/AgentApi/Models/Responses/AgentReminderResponse.cs`
- [x] T024 [US2] Extend `IReminderService` (or create `IAgentReminderService`) with agent-specific methods in `src/Features/AgentApi/Services/IAgentReminderService.cs`
- [x] T025 [US2] Implement `AgentReminderService` in `src/Features/AgentApi/Services/AgentReminderService.cs`
- [x] T026 [US2] Create `AgentReminderController.cs` definition file in `src/Features/AgentApi/Endpoints/Reminder/AgentReminderController.cs`
- [x] T027 [P] [US2] Implement `CreateReminderEndpoint.cs` in `src/Features/AgentApi/Endpoints/Reminder/CreateReminderEndpoint.cs`
- [x] T028 [P] [US2] Implement `ListMeetingRemindersEndpoint.cs` in `src/Features/AgentApi/Endpoints/Reminder/ListMeetingRemindersEndpoint.cs`
- [x] T029 [P] [US2] Implement `MarkReminderDeliveredEndpoint.cs` in `src/Features/AgentApi/Endpoints/Reminder/MarkReminderDeliveredEndpoint.cs`
- [x] T030 [P] [US2] Implement `CancelReminderEndpoint.cs` in `src/Features/AgentApi/Endpoints/Reminder/CancelReminderEndpoint.cs`
- [x] T031 [US2] Add Mapster configuration for `AgentReminderResponse` mappings
- [x] T032 [US2] Add integration tests: `AgentReminderTests.cs` in `tests/Integration/AgentApi/` — test create, list (public only), mark-delivered, cancel, personal reminder leakage prevention

**Checkpoint**: At this point, User Stories 1 AND 2 should both work independently.

---

## Phase 5: User Story 3 — Agent Navigates Meeting Catalog (Priority: P2)

**Goal**: Allow the agent to browse meetings, view details, list recurring series, and view meeting tags.

**Independent Test**: Use agent token to query `GET /api/agent/meetings`, `GET /api/agent/meetings/{id}`, `GET /api/agent/meetings/recurring`, and `GET /api/agent/meeting-tags`. Verify pagination, status filtering, and recurrence data.

### Implementation for User Story 3

- [x] T033 [P] [US3] Create `AgentMeetingResponse` sealed record in `src/Features/AgentApi/Models/Responses/AgentMeetingResponse.cs`
- [x] T034 [P] [US3] Create `AgentMeetingDetailResponse` sealed record in `src/Features/AgentApi/Models/Responses/AgentMeetingDetailResponse.cs`
- [x] T035 [US3] Extend `IAgentContextService` with catalog methods (ListMeetings, GetMeetingDetail, ListRecurringMeetings, ListMeetingTags)
- [x] T036 [US3] Update `AgentContextService` implementation with catalog queries
- [x] T037 [P] [US3] Implement `ListMeetingsEndpoint.cs` in `src/Features/AgentApi/Endpoints/Context/ListMeetingsEndpoint.cs`
- [x] T038 [P] [US3] Implement `GetMeetingDetailEndpoint.cs` in `src/Features/AgentApi/Endpoints/Context/GetMeetingDetailEndpoint.cs`
- [x] T039 [P] [US3] Implement `ListRecurringMeetingsEndpoint.cs` in `src/Features/AgentApi/Endpoints/Context/ListRecurringMeetingsEndpoint.cs`
- [x] T040 [P] [US3] Implement `ListMeetingTagsEndpoint.cs` in `src/Features/AgentApi/Endpoints/Context/ListMeetingTagsEndpoint.cs`
- [x] T041 [US3] Implement `RefreshTokenEndpoint.cs` in `src/Features/AgentApi/Endpoints/Context/RefreshTokenEndpoint.cs`
- [x] T042 [US3] Add Mapster configuration for `AgentMeetingResponse` and `AgentMeetingDetailResponse` mappings
- [x] T043 [US3] Add integration tests: `AgentCatalogTests.cs` in `tests/Integration/AgentApi/` — test list, detail, recurring, tags, pagination, token refresh

**Checkpoint**: All user stories should now be independently functional.

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: Rate limiting validation, security hardening, and documentation

- [x] T044 [P] Add rate limiting integration tests in `tests/Integration/AgentApi/RateLimitTests.cs` — verify 429 response and `Retry-After` header
- [x] T045 [P] Add cross-tenant isolation audit tests in `tests/Integration/AgentApi/TenantIsolationTests.cs`
- [x] T046 Verify `Result.ToProblem()` is used in all agent endpoints (no manual error construction)
- [x] T047 Verify `CancellationToken` is accepted as the last parameter in all async endpoint methods
- [x] T048 Update `CLAUDE.md` / `AGENTS.md` with AgentApi feature documentation
- [x] T049 Run full `AgentApi` integration test suite and ensure all tests pass
- [x] T050 Validate `quickstart.md` steps against actual implementation

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies — can start immediately
- **Foundational (Phase 2)**: Depends on Setup completion — BLOCKS all user stories
- **User Stories (Phase 3–5)**: All depend on Foundational phase completion
  - User stories can proceed in parallel (if staffed)
  - Or sequentially in priority order (US1 → US2 → US3)
- **Polish (Final Phase)**: Depends on all desired user stories being complete

### User Story Dependencies

- **User Story 1 (P1)**: Can start after Foundational (Phase 2) — No dependencies on other stories
- **User Story 2 (P1)**: Can start after Foundational (Phase 2) — Independent of US1; shares no mutable state
- **User Story 3 (P2)**: Can start after Foundational (Phase 2) — Extends `IAgentContextService` from US1 but can be developed in parallel if interface is established in Phase 2

### Within Each User Story

- Response models before services
- Services before controllers/endpoints
- Core implementation before integration tests
- Story complete before moving to next priority

### Parallel Opportunities

- All Setup tasks (T001–T004) can run in parallel
- All Foundational tasks (T005–T013) marked [P] can run in parallel
- Once Foundational phase completes, all three user stories can start in parallel
- Response models within a story (marked [P]) can run in parallel
- Endpoint files within a story (marked [P]) can run in parallel
- Integration tests for different stories can run in parallel

---

## Parallel Example: User Story 1

```bash
# Launch all models for User Story 1 together:
Task: "Create AgentOrganizationResponse in src/Features/AgentApi/Models/Responses/AgentOrganizationResponse.cs"
Task: "Create AgentMemberResponse in src/Features/AgentApi/Models/Responses/AgentMemberResponse.cs"

# Launch all endpoints for User Story 1 together (after service is ready):
Task: "Implement GetOrganizationEndpoint in src/Features/AgentApi/Endpoints/Context/GetOrganizationEndpoint.cs"
Task: "Implement GetMeetingMembersEndpoint in src/Features/AgentApi/Endpoints/Context/GetMeetingMembersEndpoint.cs"
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup
2. Complete Phase 2: Foundational (CRITICAL — blocks all stories)
3. Complete Phase 3: User Story 1
4. **STOP and VALIDATE**: Test US1 independently (org query + meeting members)
5. Deploy/demo if ready

### Incremental Delivery

1. Complete Setup + Foundational → Foundation ready
2. Add User Story 1 → Test independently → Deploy/Demo (MVP!)
3. Add User Story 2 → Test independently → Deploy/Demo
4. Add User Story 3 → Test independently → Deploy/Demo
5. Each story adds value without breaking previous stories

### Parallel Team Strategy

With multiple developers:

1. Team completes Setup + Foundational together
2. Once Foundational is done:
   - Developer A: User Story 1 (Context endpoints)
   - Developer B: User Story 2 (Reminder endpoints)
   - Developer C: User Story 3 (Catalog endpoints + token refresh)
3. Stories complete and integrate independently
4. Final polish phase: shared test and security review

---

## Task Summary

| Phase | Tasks | Story |
|-------|-------|-------|
| Setup | T001–T004 | — |
| Foundational | T005–T013 | — |
| US1 — Meeting Context | T014–T022 | US1 |
| US2 — Reminders | T023–T032 | US2 |
| US3 — Meeting Catalog | T033–T043 | US3 |
| Polish | T044–T050 | — |
| **Total** | **50 tasks** | |

---

## Notes

- [P] tasks = different files, no dependencies
- [Story] label maps task to specific user story for traceability
- Each user story should be independently completable and testable
- Verify tests fail before implementing (TDD approach)
- Commit after each task or logical group
- Stop at any checkpoint to validate story independently
- Avoid: vague tasks, same file conflicts, cross-story dependencies that break independence


