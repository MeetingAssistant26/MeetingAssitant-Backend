# Tasks: Action Items & Trello Integration

**Feature**: 008-action-items-trello  
**Branch**: `008-action-items-trello`  
**Date**: 2026-05-04  
**Spec**: [spec.md](spec.md) | **Plan**: [plan.md](plan.md)

---

## Implementation Strategy

**MVP Scope**: User Stories 1 + 2 (extraction + review). These deliver standalone value without requiring Trello connectivity.  
**Incremental Delivery**: Add US3 (org connection), then US4 (sync), then US5 (personal connection). Each story is independently testable.

---

## Dependency Graph

```
Phase 1 (Setup)
    │
    ▼
Phase 2 (Foundational)
    │
    ├──► Phase 3 [US1] Extract Action Items (P1)
    │       │
    │       ▼
    ├──► Phase 4 [US2] Review & Approve (P1)
    │       │
    │       ▼
    ├──► Phase 5 [US3] Connect Trello Org (P2)
    │       │
    │       ▼
    ├──► Phase 6 [US4] Sync to Trello (P2)
    │       │
    │       ▼
    └──► Phase 7 [US5] Connect Personal Trello (P3)
            │
            ▼
    Phase 8 (Polish)
```

**Parallel Opportunities**:
- Phase 3 [US1] and Phase 5 [US3] can be developed in parallel (extraction logic is independent of Trello connection setup).
- Phase 7 [US5] can be developed in parallel with Phase 6 [US4] (personal connection is independent of sync engine internals).

---

## Phase 1: Setup

*Project initialization and infrastructure wiring.*

- [ ] T001 Create feature directory structure under `MeetingAssistant/Features/ActionItems/`
- [ ] T002 Create `ActionItemsDI.cs` dependency injection module registering all services (`IActionItemService`, `ITrelloConnectionService`, `ITrelloClient`), validators, and DbContext configurations; register module in `Program.cs`
- [ ] T003 Add `ActionItems` DbSets to `ApplicationDbContext.cs`

---

## Phase 2: Foundational

*Entities, migration, and shared infrastructure. Must complete before any user story.*

### Data Model

- [ ] T004 [P] Create `ActionItem` entity in `MeetingAssistant/Features/ActionItems/Models/Entities/ActionItem.cs`
- [ ] T005 [P] Create `OrganizationIntegration` entity in `MeetingAssistant/Features/ActionItems/Models/Entities/OrganizationIntegration.cs`
- [ ] T006 [P] Create `TrelloWorkspaceConfig` entity in `MeetingAssistant/Features/ActionItems/Models/Entities/TrelloWorkspaceConfig.cs`
- [ ] T007 [P] Create `ExternalAccountLink` entity in `MeetingAssistant/Features/ActionItems/Models/Entities/ExternalAccountLink.cs`
- [ ] T008 [P] Create `TrelloMemberMapping` entity in `MeetingAssistant/Features/ActionItems/Models/Entities/TrelloMemberMapping.cs`
- [ ] T009 [P] Create `ActionItemStatus` enum in `MeetingAssistant/Features/ActionItems/Models/Enums/ActionItemStatus.cs`
- [ ] T010 [P] Create `IntegrationStatus` enum in `MeetingAssistant/Features/ActionItems/Models/Enums/IntegrationStatus.cs`
- [ ] T011 [P] Create `ExternalProvider` enum in `MeetingAssistant/Features/ActionItems/Models/Enums/ExternalProvider.cs`
- [ ] T012 [P] Create EF Core configurations for all 5 entities in `MeetingAssistant/Infrastructure/Persistence/Configurations/`
- [ ] T013 Generate EF Core migration: `AddActionItemsAndTrelloIntegration`
- [ ] T014 Apply migration to development database

### Shared Services

- [ ] T015 Create `ITrelloClient` interface in `MeetingAssistant/Features/ActionItems/Services/ITrelloClient.cs`
- [ ] T016 Implement `TrelloClient` with `HttpClient` in `MeetingAssistant/Features/ActionItems/Services/TrelloClient.cs`
- [ ] T017 Add Polly retry policy (3× exponential backoff) to `TrelloClient` for transient errors
- [ ] T018 Create `ITrelloConnectionService` interface in `MeetingAssistant/Features/ActionItems/Services/ITrelloConnectionService.cs`
- [ ] T019 Implement `TrelloConnectionService` with `IDataProtector` encryption in `MeetingAssistant/Features/ActionItems/Services/TrelloConnectionService.cs`

---

## Phase 3: [US1] Extract Action Items from Meeting Transcripts (P1)

**Goal**: Automatically extract action items from meeting transcripts using LLM with participant roster matching.  
**Independent Test Criteria**: End a meeting with transcript → verify action items appear with correct assignees.

### Background Job

- [ ] T020 Create `ExtractActionItemsJob` in `MeetingAssistant/Features/ActionItems/Jobs/ExtractActionItemsJob.cs`
- [ ] T021 Implement idempotency guard: abort if action items already exist for meeting
- [ ] T022 Build LLM prompt with participant roster injection (include participant IDs + user IDs)
- [ ] T023 Parse LLM JSON response into action item DTOs with error handling for malformed JSON
- [ ] T024 Persist `ActionItem` rows with `Status = PendingReview` and resolved assignees
- [ ] T025 Add retry logic: Hangfire `[AutomaticRetry(Attempts = 3)]` with exponential backoff

### Event Wiring

- [ ] T026 Create `MeetingTranscriptReadyEvent` handler `EnqueueActionItemExtractionHandler` in `MeetingAssistant/Features/ActionItems/Handlers/`
- [ ] T027 Register handler in MediatR pipeline
- [ ] T028 Wire handler to enqueue `ExtractActionItemsJob` with `meetingId` and `organizationId`

### Manual Re-Extraction Endpoint

- [ ] T029 Create `ReExtractActionItemsEndpoint` in `MeetingAssistant/Features/ActionItems/Endpoints/Review/`
- [ ] T030 Validate re-extraction only allowed when no action items exist for meeting
- [ ] T031 Enqueue `ExtractActionItemsJob` on manual trigger

---

## Phase 4: [US2] Review and Approve Action Items (P1)

**Goal**: Hosts/CoHosts/Admins review, edit, approve, reject, and bulk-sync action items.  
**Independent Test Criteria**: Create action items → verify host can approve, reject, edit, and trigger bulk sync with 207 response.  
**Depends on**: Phase 3 (needs extracted action items).

### DTOs

- [ ] T032 [P] Create `UpdateActionItemRequest` in `MeetingAssistant/Features/ActionItems/Models/Requests/UpdateActionItemRequest.cs`
- [ ] T033 [P] Create `ApproveActionItemRequest` in `MeetingAssistant/Features/ActionItems/Models/Requests/ApproveActionItemRequest.cs`
- [ ] T034 [P] Create `RejectActionItemRequest` in `MeetingAssistant/Features/ActionItems/Models/Requests/RejectActionItemRequest.cs`
- [ ] T035 [P] Create `BulkSyncRequest` in `MeetingAssistant/Features/ActionItems/Models/Requests/BulkSyncRequest.cs`
- [ ] T036 [P] Create `ActionItemResponse` in `MeetingAssistant/Features/ActionItems/Models/Responses/ActionItemResponse.cs`
- [ ] T037 [P] Create `ActionItemListResponse` in `MeetingAssistant/Features/ActionItems/Models/Responses/ActionItemListResponse.cs`
- [ ] T038 [P] Create `SyncResultResponse` in `MeetingAssistant/Features/ActionItems/Models/Responses/SyncResultResponse.cs`
- [ ] T039 [P] Create `BulkSyncResponse` in `MeetingAssistant/Features/ActionItems/Models/Responses/BulkSyncResponse.cs`

### Validators

- [ ] T040 [P] Create `UpdateActionItemRequestValidator` in `MeetingAssistant/Features/ActionItems/Validators/UpdateActionItemRequestValidator.cs`
- [ ] T041 [P] Create `ApproveActionItemRequestValidator` in `MeetingAssistant/Features/ActionItems/Validators/ApproveActionItemRequestValidator.cs`
- [ ] T042 [P] Create `ConnectTrelloRequestValidator` in `MeetingAssistant/Features/ActionItems/Validators/ConnectTrelloRequestValidator.cs`

### Service Layer

- [ ] T043 Create `IActionItemService` interface in `MeetingAssistant/Features/ActionItems/Services/IActionItemService.cs`
- [ ] T044 Implement `ActionItemService` in `MeetingAssistant/Features/ActionItems/Services/ActionItemService.cs`
- [ ] T045 Implement optimistic concurrency check (ETag/RowVersion) in `ActionItemService`
- [ ] T046 Implement state transition validation (`PendingReview ↔ Approved/Rejected`, terminal `Synced`/`SyncedNoAssignee`)
- [ ] T047 Implement bulk sync orchestration with 207 Multi-Status result building

### Review Endpoints

- [ ] T048 Create `ActionItemReviewController` definition in `MeetingAssistant/Features/ActionItems/Endpoints/Review/ActionItemReviewController.cs`
- [ ] T049 Implement `ListMeetingActionItemsEndpoint` in `MeetingAssistant/Features/ActionItems/Endpoints/Review/ListMeetingActionItemsEndpoint.cs`
- [ ] T050 Implement `UpdateActionItemEndpoint` with `If-Match` header support in `MeetingAssistant/Features/ActionItems/Endpoints/Review/UpdateActionItemEndpoint.cs`
- [ ] T051 Implement `ApproveActionItemEndpoint` in `MeetingAssistant/Features/ActionItems/Endpoints/Review/ApproveActionItemEndpoint.cs`
- [ ] T052 Implement `RejectActionItemEndpoint` in `MeetingAssistant/Features/ActionItems/Endpoints/Review/RejectActionItemEndpoint.cs`
- [ ] T053 Implement `SyncActionItemEndpoint` in `MeetingAssistant/Features/ActionItems/Endpoints/Review/SyncActionItemEndpoint.cs`
- [ ] T054 Implement `BulkSyncActionItemsEndpoint` returning 207 Multi-Status in `MeetingAssistant/Features/ActionItems/Endpoints/Review/BulkSyncActionItemsEndpoint.cs`
- [ ] T054a Implement `DeleteActionItemEndpoint` in `MeetingAssistant/Features/ActionItems/Endpoints/Review/DeleteActionItemEndpoint.cs`

---

## Phase 5: [US3] Connect Trello at Organization Level (P2)

**Goal**: Org Admins configure Trello integration (API Key + Token, board/list selection).  
**Independent Test Criteria**: Save credentials → verify boards/lists fetched → verify selection persisted.  
**Parallel with**: Phase 3 (does not depend on extraction).

### Admin Settings Endpoints

- [ ] T055 Create `TrelloAdminController` definition in `MeetingAssistant/Features/ActionItems/Endpoints/Admin/TrelloAdminController.cs`
- [ ] T056 Implement `GetTrelloSettingsEndpoint` in `MeetingAssistant/Features/ActionItems/Endpoints/Admin/GetTrelloSettingsEndpoint.cs`
- [ ] T057 Implement `SaveTrelloSettingsEndpoint` with credential validation and encryption in `MeetingAssistant/Features/ActionItems/Endpoints/Admin/SaveTrelloSettingsEndpoint.cs`
- [ ] T058 Implement `ListTrelloBoardsEndpoint` in `MeetingAssistant/Features/ActionItems/Endpoints/Admin/ListTrelloBoardsEndpoint.cs`
- [ ] T059 Implement `ListTrelloListsEndpoint` in `MeetingAssistant/Features/ActionItems/Endpoints/Admin/ListTrelloListsEndpoint.cs`
- [ ] T060 Implement `GetMemberTrelloStatusEndpoint` in `MeetingAssistant/Features/ActionItems/Endpoints/Admin/GetMemberTrelloStatusEndpoint.cs`
- [ ] T061 Implement `SetMemberTrelloMappingEndpoint` in `MeetingAssistant/Features/ActionItems/Endpoints/Admin/SetMemberTrelloMappingEndpoint.cs`

---

## Phase 6: [US4] Sync Approved Action Items to Trello (P2)

**Goal**: Hosts/CoHosts manually sync approved action items to Trello cards with assignee resolution.  
**Independent Test Criteria**: Create approved action item with mapped assignee → verify Trello card created with correct fields and assignee.  
**Depends on**: Phase 5 (needs org Trello config).

### Sync Job

- [ ] T062 Create `SyncActionItemsToTrelloJob` in `MeetingAssistant/Features/ActionItems/Jobs/SyncActionItemsToTrelloJob.cs`
- [ ] T063 Implement idempotency guard: skip items where `TrelloCardId != null`
- [ ] T064 Implement board member list fetching with 5-minute in-memory caching
- [ ] T065 Implement assignee resolution: `ExternalAccountLink` → `TrelloMemberMapping` → no assignee
- [ ] T066 Implement Trello card creation via `ITrelloClient.CreateCardAsync`
- [ ] T067 Implement status mapping: `Synced` (with assignee) vs `SyncedNoAssignee` (without)
- [ ] T068 Implement error handling: 401 → `NeedsReconnect`, 404 → `InvalidConfig`, transients → retry
- [ ] T069 Update `ActionItem` with `TrelloCardId`, `TrelloCardUrl`, and `SyncedAtUtc` on success

---

## Phase 7: [US5] Connect Personal Trello Account (P3)

**Goal**: Users connect personal Trello account from profile settings for automatic card assignment.  
**Independent Test Criteria**: User connects Trello → verify subsequent syncs assign them to cards.  
**Parallel with**: Phase 6 (personal connection is independent of sync engine internals).

### User Connection Endpoints

- [ ] T070 Create `UserConnectionController` definition in `MeetingAssistant/Features/ActionItems/Endpoints/UserConnection/UserConnectionController.cs`
- [ ] T071 Implement `GetMyConnectionsEndpoint` in `MeetingAssistant/Features/ActionItems/Endpoints/UserConnection/GetMyConnectionsEndpoint.cs`
- [ ] T072 Implement `ConnectTrelloEndpoint` with token validation and secure storage in `MeetingAssistant/Features/ActionItems/Endpoints/UserConnection/ConnectTrelloEndpoint.cs`
- [ ] T073 Implement `DisconnectTrelloEndpoint` in `MeetingAssistant/Features/ActionItems/Endpoints/UserConnection/DisconnectTrelloEndpoint.cs`

---

## Phase 8: Polish & Cross-Cutting Concerns

*Testing, logging, error handling, and final validation.*

### Integration Tests

- [ ] T074 Create `ExtractActionItemsJobTests` in `tests/MeetingAssistant.Tests.Integration/ActionItems/ExtractActionItemsJobTests.cs`
- [ ] T075 Create `ActionItemReviewTests` in `tests/MeetingAssistant.Tests.Integration/ActionItems/ActionItemReviewTests.cs`
- [ ] T076 Create `TrelloSyncTests` in `tests/MeetingAssistant.Tests.Integration/ActionItems/TrelloSyncTests.cs`
- [ ] T077 Create `TrelloConnectionTests` in `tests/MeetingAssistant.Tests.Integration/ActionItems/TrelloConnectionTests.cs`
- [ ] T078 Add tenant isolation test: verify org A cannot access org B's Trello config or action items

### Observability & Error Handling

- [ ] T079 Add structured logging to `ExtractActionItemsJob` (start, complete, fail, LLM raw response on error)
- [ ] T080 Add structured logging to `SyncActionItemsToTrelloJob` (card creation, assignee resolution, API errors)
- [ ] T081 Add correlation ID propagation through Trello API calls
- [ ] T082 Verify all endpoints return `StandardErrorResponse` via `result.ToProblem(correlationIdProvider)`
- [ ] T082a Add extraction metrics: log extraction latency per meeting and assignee resolution accuracy (resolved vs total named participants) to enable SC-001 and SC-002 monitoring

### Final Validation

- [ ] T083 Run full integration test suite and verify all acceptance scenarios pass
- [ ] T084 Verify constitution compliance: Partial Controller Pattern, tenant isolation, error response standard
- [ ] T085 Update `AGENTS.md` with new feature technologies (Trello integration, Data Protection)

---

## Task Summary

| Phase | Tasks | Story | Priority |
|-------|-------|-------|----------|
| Phase 1: Setup | 3 | — | — |
| Phase 2: Foundational | 15 | — | — |
| Phase 3: [US1] Extract | 12 | US1 | P1 |
| Phase 4: [US2] Review | 17 | US2 | P1 |
| Phase 5: [US3] Org Connect | 7 | US3 | P2 |
| Phase 6: [US4] Sync | 8 | US4 | P2 |
| Phase 7: [US5] Personal Connect | 4 | US5 | P3 |
| Phase 8: Polish | 13 | — | — |
| **Total** | **79** | | |

**MVP Tasks** (US1 + US2 + Foundational): 47 tasks  
**Parallel Opportunities**:
- Phase 3 [US1] ↔ Phase 5 [US3] (extraction and org connection are independent)
- Phase 6 [US4] ↔ Phase 7 [US5] (sync engine and personal connection are independent)
