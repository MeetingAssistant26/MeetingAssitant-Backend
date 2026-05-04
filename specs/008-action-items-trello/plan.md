# Implementation Plan: Action Items & Trello Integration

**Branch**: `008-action-items-trello` | **Date**: 2026-05-04 | **Spec**: [spec.md](spec.md)  
**Input**: Feature specification from `/specs/008-action-items-trello/spec.md`

## Summary

Implement automated action item extraction from meeting transcripts using an outsourced LLM with participant roster matching, a human review and approval workflow, and one-way sync to Trello cards. The feature spans background jobs (Hangfire), REST API endpoints (Partial Controller Pattern), and encrypted credential storage. It preserves existing tenant isolation and follows the project's vertical slice architecture.

## Technical Context

**Language/Version**: C# / .NET 10  
**Primary Dependencies**: ASP.NET Core, EF Core 9 (Npgsql), Hangfire + PostgreSQL, MediatR, FluentValidation, Mapster, ASP.NET Core Data Protection  
**Storage**: PostgreSQL (existing `ApplicationDbContext`)  
**Testing**: xUnit + Integration tests using `WebApplicationFactory`  
**Target Platform**: ASP.NET Core Web API (backend only)  
**Project Type**: Web service  
**Performance Goals**: <300ms p95 for API endpoints; action item extraction completes within 5 minutes of transcript readiness; Trello sync completes within 30 seconds per item  
**Constraints**: All AI processing async via Hangfire; no SignalR notifications; one-way sync only; API Key + Token auth (no OAuth 1.0a)  
**Scale/Scope**: Per-organization Trello integration; up to ~50 action items per meeting; Trello rate limit 300 req/10s

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Rule | Status | Notes |
|------|--------|-------|
| Vertical Slice Architecture | ✅ Pass | Feature placed under `Features/ActionItems/` with its own Models, Services, Validators, Endpoints, Jobs |
| Partial Controller Pattern | ✅ Pass | 3 controllers (ActionItemReview, UserConnection, TrelloAdmin), each split into definition + endpoint files |
| Tenant Isolation by Default | ✅ Pass | All new entities (`ActionItem`, `OrganizationIntegration`, `TrelloWorkspaceConfig`, `ExternalAccountLink`, `TrelloMemberMapping`) implement `IHasOrganizationId` |
| Strict Single Membership Rule | ✅ Pass | No impact; feature does not modify membership model |
| Standardized Operational Errors | ✅ Pass | All endpoints use `result.ToProblem(correlationIdProvider)` |
| Automated Tests Required | ✅ Pass | Acceptance scenarios cover all user stories; integration tests required for extraction, review, sync, and admin settings |

## Project Structure

### Documentation (this feature)

```text
specs/008-action-items-trello/
├── plan.md              # This file
├── research.md          # Phase 0 output
├── data-model.md        # Phase 1 output
├── quickstart.md        # Phase 1 output
├── contracts/           # Phase 1 output
│   └── api-contracts.md
├── spec.md              # From /speckit.specify
└── checklists/          # From /speckit.specify
    └── requirements.md
```

### Source Code (repository root)

```text
MeetingAssistant/
├── Features/
│   ├── ActionItems/
│   │   ├── Endpoints/
│   │   │   ├── Review/
│   │   │   │   ├── ActionItemReviewController.cs      # definition
│   │   │   │   ├── ListMeetingActionItemsEndpoint.cs
│   │   │   │   ├── UpdateActionItemEndpoint.cs
│   │   │   │   ├── ApproveActionItemEndpoint.cs
│   │   │   │   ├── RejectActionItemEndpoint.cs
│   │   │   │   ├── SyncActionItemEndpoint.cs
│   │   │   │   ├── BulkSyncActionItemsEndpoint.cs
│   │   │   │   └── DeleteActionItemEndpoint.cs
│   │   │   ├── UserConnection/
│   │   │       ├── UserConnectionController.cs         # definition
│   │   │       ├── GetMyConnectionsEndpoint.cs
│   │   │       ├── ConnectTrelloEndpoint.cs
│   │   │       └── DisconnectTrelloEndpoint.cs
│   │   │   └── Admin/                    # Trello admin settings (org-level)
│   │   │       ├── TrelloAdminController.cs
│   │   │       ├── GetTrelloSettingsEndpoint.cs
│   │   │       ├── SaveTrelloSettingsEndpoint.cs
│   │   │       ├── ListTrelloBoardsEndpoint.cs
│   │   │       ├── ListTrelloListsEndpoint.cs
│   │   │       ├── GetMemberTrelloStatusEndpoint.cs
│   │   │       └── SetMemberTrelloMappingEndpoint.cs
│   │   ├── Models/
│   │   │   ├── Entities/
│   │   │   │   ├── ActionItem.cs
│   │   │   │   ├── OrganizationIntegration.cs
│   │   │   │   ├── TrelloWorkspaceConfig.cs
│   │   │   │   ├── ExternalAccountLink.cs
│   │   │   │   └── TrelloMemberMapping.cs
│   │   │   ├── Enums/
│   │   │   │   ├── ActionItemStatus.cs
│   │   │   │   └── IntegrationStatus.cs
│   │   │   ├── Requests/
│   │   │   │   ├── UpdateActionItemRequest.cs
│   │   │   │   ├── ApproveActionItemRequest.cs
│   │   │   │   ├── RejectActionItemRequest.cs
│   │   │   │   ├── BulkSyncRequest.cs
│   │   │   │   └── ConnectTrelloRequest.cs
│   │   │   └── Responses/
│   │   │       ├── ActionItemResponse.cs
│   │   │       ├── ActionItemListResponse.cs
│   │   │       ├── SyncResultResponse.cs
│   │   │       └── BulkSyncResponse.cs
│   │   ├── Services/
│   │   │   ├── IActionItemService.cs
│   │   │   ├── ActionItemService.cs
│   │   │   ├── ITrelloConnectionService.cs
│   │   │   ├── TrelloConnectionService.cs
│   │   │   ├── ITrelloClient.cs
│   │   │   └── TrelloClient.cs
│   │   ├── Jobs/
│   │   │   ├── ExtractActionItemsJob.cs
│   │   │   └── SyncActionItemsToTrelloJob.cs
│   │   └── Validators/
│   │       ├── UpdateActionItemRequestValidator.cs
│   │       ├── ApproveActionItemRequestValidator.cs
│   │       └── ConnectTrelloRequestValidator.cs
├── Infrastructure/
│   └── Persistence/
│       └── DbContext/
│           └── ApplicationDbContext.cs   # add DbSets
│       └── Configurations/
│           ├── ActionItemConfiguration.cs
│           ├── OrganizationIntegrationConfiguration.cs
│           ├── TrelloWorkspaceConfigConfiguration.cs
│           ├── ExternalAccountLinkConfiguration.cs
│           └── TrelloMemberMappingConfiguration.cs
└── tests/
    └── MeetingAssistant.Tests.Integration/
        └── ActionItems/
            ├── ExtractActionItemsJobTests.cs
            ├── ActionItemReviewTests.cs
            ├── TrelloSyncTests.cs
            └── TrelloConnectionTests.cs
```

**Structure Decision**: All endpoints consolidated under **`Features/ActionItems/`** as a single vertical slice:
- `Endpoints/Review/` — action item review and sync (host/cohost)
- `Endpoints/UserConnection/` — personal Trello connection (any user)
- `Endpoints/Admin/` — org-level Trello settings and member mapping (Org Admin only)

This keeps all Trello-related code in one domain slice, aligning with the Vertical Slice Architecture principle.

## Complexity Tracking

> No constitution violations detected. No complexity justifications required.

## Research Summary

See [research.md](research.md) for full details. Key decisions:
- **Trello API**: Direct `HttpClient` wrapper (no SDK)
- **Encryption**: ASP.NET Core Data Protection
- **Background jobs**: Hangfire (existing)
- **Parallel extraction**: `MeetingTranscriptReadyEvent` drives both summary and extraction jobs independently
- **Bulk sync status**: HTTP 207 Multi-Status (RFC 4918)

## Design Decisions

### Data Model
See [data-model.md](data-model.md) for entity definitions, relationships, and validation rules.

### API Contracts
See [contracts/api-contracts.md](contracts/api-contracts.md) for endpoint definitions, request/response schemas, and auth requirements.

### Key Implementation Patterns

1. **Optimistic Concurrency**: `ActionItem` uses `RowVersion` byte array. PATCH endpoints require `If-Match` header. 409 returned on stale writes.

2. **Idempotency**: 
   - `ExtractActionItemsJob` checks `ActionItems.Any(x => x.MeetingId == meetingId)` before processing.
   - `SyncActionItemsToTrelloJob` skips items where `TrelloCardId != null`.

3. **Retry Policy**:
   - LLM calls: 3 retries with exponential backoff over 15 minutes (Hangfire automatic retry).
   - Trello transient errors: 3 retries with exponential backoff (Polly policy on `ITrelloClient`).
   - Trello non-retryable errors (401/404): mark integration unhealthy, stop job.

4. **Member Resolution Priority**:
   ```
   ExternalAccountLink (self-connected) → TrelloMemberMapping (admin-defined) → no assignee
   ```

5. **Board Member Caching**: `ITrelloClient.GetBoardMembersAsync` result cached in-memory for 5 minutes via `IMemoryCache`.

6. **Encryption**: `IDataProtector` injected into services. Protect on write, unprotect on read. No plaintext tokens in memory longer than necessary.

## Implementation Phases

### Phase 1: Entities & Migration
- Create all 5 entities with EF Core configurations
- Add `DbSet`s to `ApplicationDbContext`
- Generate and apply migration

### Phase 2: Trello Client & Connection Services
- Implement `ITrelloClient` with `HttpClient`
- Implement `TrelloConnectionService` for user/org connections
- Add encryption helpers using `IDataProtector`

### Phase 3: Extraction Job
- Implement `ExtractActionItemsJob`
- Build LLM prompt with participant roster injection
- Wire `MeetingTranscriptReadyEvent` → job enqueue
- Add idempotency guard

### Phase 4: Review Endpoints
- Implement `ActionItemReviewController` + endpoints
- Add optimistic concurrency handling
- Implement state transition validation

### Phase 5: Sync Job & Endpoints
- Implement `SyncActionItemsToTrelloJob`
- Implement member resolution logic
- Add transient retry logic
- Implement single-item and bulk sync endpoints

### Phase 6: Admin Settings Endpoints
- Implement Trello admin settings under `Organizations/Endpoints/Integration/`
- Add board/list fetching endpoints
- Add member mapping endpoints

### Phase 7: Integration Tests
- Test extraction job with mocked LLM
- Test review flow (approve, reject, edit, concurrent modification)
- Test sync with mocked Trello API
- Test tenant isolation across organizations

## Generated Artifacts

| Artifact | Path | Status |
|----------|------|--------|
| Research | [research.md](research.md) | ✅ Complete |
| Data Model | [data-model.md](data-model.md) | ✅ Complete |
| API Contracts | [contracts/api-contracts.md](contracts/api-contracts.md) | ✅ Complete |
| Quickstart | [quickstart.md](quickstart.md) | ✅ Complete |
| Tasks | `tasks.md` | ⏳ Created by `/speckit.tasks` |

## Next Step

Run `/speckit.tasks` to break this plan into actionable, dependency-ordered tasks.
