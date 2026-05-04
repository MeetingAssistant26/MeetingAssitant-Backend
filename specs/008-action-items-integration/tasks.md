# Tasks: Action Items & External Task Provider Integration

**Feature**: 008-action-items-integration  
**Branch**: `008-action-items-integration`  
**Date**: 2026-05-04  
**Spec**: [spec.md](spec.md) | **Plan**: [plan.md](plan.md)

---

## Implementation Strategy

**MVP Scope**: User Stories 1 + 2 (extraction + review). These deliver standalone value without requiring external provider connectivity.  
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
    ├──► Phase 5 [US3] Connect Provider Org (P2)
    │       │
    │       ▼
    ├──► Phase 6 [US4] Sync to Provider (P2)
    │       │
    │       ▼
    └──► Phase 7 [US5] Connect Personal Provider (P3)
            │
            ▼
    Phase 8 (Polish)
```

**Parallel Opportunities**:
- Phase 3 [US1] and Phase 5 [US3] can be developed in parallel (extraction logic is independent of provider connection setup).
- Phase 7 [US5] can be developed in parallel with Phase 6 [US4] (personal connection is independent of sync engine internals).

---

## Phase 1: Setup

*Project initialization and infrastructure wiring.*

- [X] T001 Create feature directory structure under `MeetingAssistant/Features/ActionItems/`
- [X] T002 Create `ActionItemsDI.cs` dependency injection module registering all services (`IActionItemService`, `IIntegrationAdminService`, `IUserIntegrationService`, `ITaskProviderFactory`, `TrelloTaskProvider`), validators, and DbContext configurations; register module in `Program.cs`
- [X] T003 Add `ActionItems` DbSets to `ApplicationDbContext.cs`

---

## Phase 2: Foundational

*Entities, migration, provider abstraction, and shared infrastructure. Must complete before any user story.*

### Data Model

- [X] T004 [P] Create `ActionItem` entity in `MeetingAssistant/Features/ActionItems/Models/Entities/ActionItem.cs`
- [X] T005 [P] Create `OrganizationIntegration` entity in `MeetingAssistant/Features/ActionItems/Models/Entities/OrganizationIntegration.cs`
- [X] T006 [P] Create `OrganizationIntegrationConfig` entity in `MeetingAssistant/Features/ActionItems/Models/Entities/OrganizationIntegrationConfig.cs`
- [X] T007 [P] Create `ExternalAccountLink` entity in `MeetingAssistant/Features/ActionItems/Models/Entities/ExternalAccountLink.cs`
- [X] T008 [P] Create `ExternalMemberMapping` entity in `MeetingAssistant/Features/ActionItems/Models/Entities/ExternalMemberMapping.cs`
- [X] T009 [P] Create `ActionItemStatus` enum in `MeetingAssistant/Features/ActionItems/Models/Enums/ActionItemStatus.cs`
- [X] T010 [P] Create `IntegrationStatus` enum in `MeetingAssistant/Features/ActionItems/Models/Enums/IntegrationStatus.cs`
- [X] T011 [P] Create `ExternalProvider` enum in `MeetingAssistant/Features/ActionItems/Models/Enums/ExternalProvider.cs`
- [X] T012 [P] Create EF Core configurations for all 5 entities in `MeetingAssistant/Infrastructure/Persistence/Configurations/`
- [X] T013 Generate EF Core migration: `AddActionItemsAndIntegration`
- [X] T014 Apply migration to development database

### Provider Abstraction

- [X] T015 [P] Create `ITaskProvider` interface in `MeetingAssistant/Features/ActionItems/Services/Abstractions/ITaskProvider.cs`
- [X] T015a [P] Create `ITaskProviderFactory` interface in `MeetingAssistant/Features/ActionItems/Services/Abstractions/ITaskProviderFactory.cs`
- [X] T015b [P] Create generic provider DTOs (`ProviderProject`, `ProviderList`, `ProviderTaskResult`, `ProviderTaskRequest`) in `MeetingAssistant/Features/ActionItems/Services/Abstractions/`
- [X] T015c [P] Implement `TaskProviderFactory` in `MeetingAssistant/Features/ActionItems/Services/TaskProviderFactory.cs`

### Trello Provider Implementation (First Provider)

- [X] T016 [P] Implement `TrelloTaskProvider : ITaskProvider` with `HttpClient` in `MeetingAssistant/Features/ActionItems/Services/Providers/Trello/TrelloTaskProvider.cs`
  - **Reference**: See `research.md` for official Trello API details.
  - Use `GET /1/boards/{id}/members` for member lookup (avoid `GET /1/members/` which has a strict 100 req/15min limit).
  - Pass `idMembers` as an **array** on `POST /1/cards` (not a single string).
- [X] T016a [P] Add Polly retry policy (3× exponential backoff) to `TrelloTaskProvider` for transient errors
  - Account for **dual rate limits**: 300 req/10s per API key + 100 req/10s per token.
- [X] T016b [P] Create `TrelloAuthValidator` for credential validation
- [X] T016c [P] Create `TrelloDtoMapper` for mapping Trello API responses to generic DTOs

### Shared Services

- [X] T017 [P] Create `IIntegrationAdminService` interface in `MeetingAssistant/Features/ActionItems/Services/IIntegrationAdminService.cs`
- [X] T018 [P] Implement `IntegrationAdminService` with `IDataProtector` encryption in `MeetingAssistant/Features/ActionItems/Services/IntegrationAdminService.cs`
- [X] T019 [P] Create `IUserIntegrationService` interface in `MeetingAssistant/Features/ActionItems/Services/IUserIntegrationService.cs`
- [X] T019a [P] Implement `UserIntegrationService` with `IDataProtector` encryption in `MeetingAssistant/Features/ActionItems/Services/UserIntegrationService.cs`

---

## Phase 3: [US1] Extract Action Items from Meeting Transcripts (P1)

**Goal**: Automatically extract action items from meeting transcripts using LLM with participant roster matching.  
**Independent Test Criteria**: End a meeting with transcript → verify action items appear with correct assignees.

### Background Job

- [X] T020 Create `ExtractActionItemsJob` in `MeetingAssistant/Features/ActionItems/Jobs/ExtractActionItemsJob.cs`
- [X] T021 Implement idempotency guard: abort if action items already exist for meeting
- [X] T022 Build LLM prompt with participant roster injection (include participant IDs + user IDs)
- [X] T023 Parse LLM JSON response into action item DTOs with error handling for malformed JSON
- [X] T024 Persist `ActionItem` rows with `Status = PendingReview` and resolved assignees
- [X] T025 Add retry logic: Hangfire `[AutomaticRetry(Attempts = 3)]` with exponential backoff

### Event Wiring

- [X] T026 Create `MeetingTranscriptReadyEvent` handler `EnqueueActionItemExtractionHandler` in `MeetingAssistant/Features/ActionItems/Handlers/`
- [X] T027 Register handler in MediatR pipeline
- [X] T028 Wire handler to enqueue `ExtractActionItemsJob` with `meetingId` and `organizationId`

### Manual Re-Extraction Endpoint

- [X] T029 Create `ReExtractActionItemsEndpoint` in `MeetingAssistant/Features/ActionItems/Endpoints/Review/`
- [X] T030 Validate re-extraction only allowed when no action items exist for meeting
- [X] T031 Enqueue `ExtractActionItemsJob` on manual trigger

---

## Phase 4: [US2] Review and Approve Action Items (P1)

**Goal**: Hosts/CoHosts/Admins review, edit, approve, reject, and bulk-sync action items.  
**Independent Test Criteria**: Create action items → verify host can approve, reject, edit, and trigger bulk sync with 207 response.  
**Depends on**: Phase 3 (needs extracted action items).

### DTOs

- [X] T032 [P] Create `UpdateActionItemRequest` in `MeetingAssistant/Features/ActionItems/Models/Requests/UpdateActionItemRequest.cs`
- [X] T033 [P] Create `ApproveActionItemRequest` in `MeetingAssistant/Features/ActionItems/Models/Requests/ApproveActionItemRequest.cs`
- [X] T034 [P] Create `RejectActionItemRequest` in `MeetingAssistant/Features/ActionItems/Models/Requests/RejectActionItemRequest.cs`
- [X] T035 [P] Create `BulkSyncRequest` in `MeetingAssistant/Features/ActionItems/Models/Requests/BulkSyncRequest.cs`
- [X] T036 [P] Create `ActionItemResponse` in `MeetingAssistant/Features/ActionItems/Models/Responses/ActionItemResponse.cs`
- [X] T037 [P] Create `ActionItemListResponse` in `MeetingAssistant/Features/ActionItems/Models/Responses/ActionItemListResponse.cs`
- [X] T038 [P] Create `SyncResultResponse` in `MeetingAssistant/Features/ActionItems/Models/Responses/SyncResultResponse.cs`
- [X] T039 [P] Create `BulkSyncResponse` in `MeetingAssistant/Features/ActionItems/Models/Responses/BulkSyncResponse.cs`

### Validators

- [X] T040 [P] Create `UpdateActionItemRequestValidator` in `MeetingAssistant/Features/ActionItems/Validators/UpdateActionItemRequestValidator.cs`
- [X] T041 [P] Create `ApproveActionItemRequestValidator` in `MeetingAssistant/Features/ActionItems/Validators/ApproveActionItemRequestValidator.cs`
- [X] T042 [P] Create `ConnectProviderRequestValidator` in `MeetingAssistant/Features/ActionItems/Validators/ConnectProviderRequestValidator.cs`

### Service Layer

- [X] T043 Create `IActionItemService` interface in `MeetingAssistant/Features/ActionItems/Services/IActionItemService.cs`
- [X] T044 Implement `ActionItemService` in `MeetingAssistant/Features/ActionItems/Services/ActionItemService.cs`
- [X] T045 Implement optimistic concurrency check (ETag/RowVersion) in `ActionItemService`
- [X] T046 Implement state transition validation (`PendingReview ↔ Approved/Rejected`, terminal `Synced`/`SyncedNoAssignee`)
- [X] T047 Implement bulk sync orchestration with 207 Multi-Status result building

### Review Endpoints

- [X] T048 Create `ActionItemReviewController` definition in `MeetingAssistant/Features/ActionItems/Endpoints/Review/ActionItemReviewController.cs`
- [X] T049 Implement `ListMeetingActionItemsEndpoint` in `MeetingAssistant/Features/ActionItems/Endpoints/Review/ListMeetingActionItemsEndpoint.cs`
- [X] T050 Implement `UpdateActionItemEndpoint` with `If-Match` header support in `MeetingAssistant/Features/ActionItems/Endpoints/Review/UpdateActionItemEndpoint.cs`
- [X] T051 Implement `ApproveActionItemEndpoint` in `MeetingAssistant/Features/ActionItems/Endpoints/Review/ApproveActionItemEndpoint.cs`
- [X] T052 Implement `RejectActionItemEndpoint` in `MeetingAssistant/Features/ActionItems/Endpoints/Review/RejectActionItemEndpoint.cs`
- [X] T053 Implement `SyncActionItemEndpoint` in `MeetingAssistant/Features/ActionItems/Endpoints/Review/SyncActionItemEndpoint.cs`
- [X] T054 Implement `BulkSyncActionItemsEndpoint` returning 207 Multi-Status in `MeetingAssistant/Features/ActionItems/Endpoints/Review/BulkSyncActionItemsEndpoint.cs`
- [X] T054a Implement `DeleteActionItemEndpoint` in `MeetingAssistant/Features/ActionItems/Endpoints/Review/DeleteActionItemEndpoint.cs`

---

## Phase 5: [US3] Connect External Provider at Organization Level (P2)

**Goal**: Org Admins configure external provider integration (credentials, project/list selection).  
**Independent Test Criteria**: Save credentials → verify projects/lists fetched → verify selection persisted.  
**Parallel with**: Phase 3 (does not depend on extraction).

### Admin Settings Endpoints

- [X] T055 Create `IntegrationAdminController` definition in `MeetingAssistant/Features/ActionItems/Endpoints/Admin/IntegrationAdminController.cs`
- [X] T056 Implement `GetIntegrationConfigEndpoint` in `MeetingAssistant/Features/ActionItems/Endpoints/Admin/GetIntegrationConfigEndpoint.cs`
- [X] T057 Implement `SaveIntegrationConfigEndpoint` with credential validation and encryption in `MeetingAssistant/Features/ActionItems/Endpoints/Admin/SaveIntegrationConfigEndpoint.cs`
- [X] T058 Implement `ListProviderProjectsEndpoint` in `MeetingAssistant/Features/ActionItems/Endpoints/Admin/ListProviderProjectsEndpoint.cs`
- [X] T059 Implement `ListProviderListsEndpoint` in `MeetingAssistant/Features/ActionItems/Endpoints/Admin/ListProviderListsEndpoint.cs`
- [X] T060 Implement `GetMemberProviderStatusEndpoint` in `MeetingAssistant/Features/ActionItems/Endpoints/Admin/GetMemberProviderStatusEndpoint.cs`
- [X] T061 Implement `SetMemberMappingEndpoint` in `MeetingAssistant/Features/ActionItems/Endpoints/Admin/SetMemberMappingEndpoint.cs`

---

## Phase 6: [US4] Sync Approved Action Items to External Provider (P2)

**Goal**: Hosts/CoHosts manually sync approved action items to external provider tasks with assignee resolution.  
**Independent Test Criteria**: Create approved action item with mapped assignee → verify external task created with correct fields and assignee.  
**Depends on**: Phase 5 (needs org provider config).

### Sync Job

- [X] T062 Create `SyncActionItemsToProviderJob` in `MeetingAssistant/Features/ActionItems/Jobs/SyncActionItemsToProviderJob.cs`
- [X] T063 Implement idempotency guard: skip items where `ExternalTaskId != null`
- [X] T064 Implement project member list fetching with 5-minute in-memory caching
- [X] T065 Implement assignee resolution: `ExternalAccountLink` → `ExternalMemberMapping` → no assignee
- [X] T066 Implement external task creation via `ITaskProvider.CreateTaskAsync`
- [X] T067 Implement status mapping: `Synced` (with assignee) vs `SyncedNoAssignee` (without)
- [X] T068 Implement error handling: 401 → `NeedsReconnect`, 404 → `InvalidConfig`, transients → retry
- [X] T069 Update `ActionItem` with `ExternalTaskId`, `ExternalTaskUrl`, `ExternalProvider`, and `SyncedAtUtc` on success

---

## Phase 7: [US5] Connect Personal External Provider Account (P3)

**Goal**: Users connect personal external provider account from profile settings for automatic task assignment.  
**Independent Test Criteria**: User connects provider → verify subsequent syncs assign them to tasks.  
**Parallel with**: Phase 6 (personal connection is independent of sync engine internals).

### User Connection Endpoints

- [X] T070 Create `UserIntegrationController` definition in `MeetingAssistant/Features/ActionItems/Endpoints/UserIntegration/UserIntegrationController.cs`
- [X] T071 Implement `GetMyConnectionsEndpoint` in `MeetingAssistant/Features/ActionItems/Endpoints/UserIntegration/GetMyConnectionsEndpoint.cs`
- [X] T072 Implement `ConnectProviderEndpoint` with token validation and secure storage in `MeetingAssistant/Features/ActionItems/Endpoints/UserIntegration/ConnectProviderEndpoint.cs`
- [X] T073 Implement `DisconnectProviderEndpoint` in `MeetingAssistant/Features/ActionItems/Endpoints/UserIntegration/DisconnectProviderEndpoint.cs`

---

## Phase 8: Polish & Cross-Cutting Concerns

*Testing, logging, error handling, and final validation.*

### Integration Tests

- [X] T074 Create `ExtractActionItemsJobTests` in `tests/MeetingAssistant.Tests.Integration/ActionItems/ExtractActionItemsJobTests.cs`
- [X] T075 Create `ActionItemReviewTests` in `tests/MeetingAssistant.Tests.Integration/ActionItems/ActionItemReviewTests.cs`
- [X] T076 Create `ProviderSyncTests` in `tests/MeetingAssistant.Tests.Integration/ActionItems/ProviderSyncTests.cs`
- [X] T077 Create `IntegrationConnectionTests` in `tests/MeetingAssistant.Tests.Integration/ActionItems/IntegrationConnectionTests.cs`
- [X] T078 Add tenant isolation test: verify org A cannot access org B's integration config or action items

### Observability & Error Handling

- [X] T079 Add structured logging to `ExtractActionItemsJob` (start, complete, fail, LLM raw response on error)
- [X] T080 Add structured logging to `SyncActionItemsToProviderJob` (task creation, assignee resolution, API errors)
- [X] T081 Add correlation ID propagation through provider API calls
- [X] T082 Verify all endpoints return `StandardErrorResponse` via `result.ToProblem(correlationIdProvider)`
- [X] T082a Add extraction metrics: log extraction latency per meeting and assignee resolution accuracy (resolved vs total named participants) to enable SC-001 and SC-002 monitoring

### Final Validation

- [X] T083 Run full integration test suite and verify all acceptance scenarios pass
- [X] T084 Verify constitution compliance: Partial Controller Pattern, tenant isolation, error response standard
- [X] T085 Update `AGENTS.md` with new feature technologies (Provider Strategy Pattern, Data Protection)

---

## Task Summary

| Phase | Tasks | Story | Priority |
|-------|-------|-------|----------|
| Phase 1: Setup | 3 | — | — |
| Phase 2: Foundational | 21 | — | — |
| Phase 3: [US1] Extract | 12 | US1 | P1 |
| Phase 4: [US2] Review | 17 | US2 | P1 |
| Phase 5: [US3] Org Connect | 7 | US3 | P2 |
| Phase 6: [US4] Sync | 8 | US4 | P2 |
| Phase 7: [US5] Personal Connect | 4 | US5 | P3 |
| Phase 8: Polish | 13 | — | — |
| **Total** | **85** | | |

**MVP Tasks** (US1 + US2 + Foundational): 53 tasks  
**Parallel Opportunities**:
- Phase 3 [US1] ↔ Phase 5 [US3] (extraction and org connection are independent)
- Phase 6 [US4] ↔ Phase 7 [US5] (sync engine and personal connection are independent)
