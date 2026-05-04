# Implementation Plan: Action Items & External Task Provider Integration

**Branch**: `008-action-items-integration` | **Date**: 2026-05-04 | **Spec**: [spec.md](spec.md)  
**Input**: Feature specification from `/specs/008-action-items-integration/spec.md`

## Summary

Implement automated action item extraction from meeting transcripts using an outsourced LLM with participant roster matching, a human review and approval workflow, and one-way sync to external task providers via a pluggable `ITaskProvider` abstraction. The feature spans background jobs (Hangfire), REST API endpoints (Partial Controller Pattern), and encrypted credential storage. It preserves existing tenant isolation and follows the project's vertical slice architecture.

## Technical Context

**Language/Version**: C# / .NET 10  
**Primary Dependencies**: ASP.NET Core, EF Core 9 (Npgsql), Hangfire + PostgreSQL, MediatR, FluentValidation, Mapster, ASP.NET Core Data Protection  
**Storage**: PostgreSQL (existing `ApplicationDbContext`)  
**Testing**: xUnit + Integration tests using `WebApplicationFactory`  
**Target Platform**: ASP.NET Core Web API (backend only)  
**Project Type**: Web service  
**Performance Goals**: <300ms p95 for API endpoints; action item extraction completes within 5 minutes of transcript readiness; external sync completes within 30 seconds per item  
**Constraints**: All AI processing async via Hangfire; no SignalR notifications; one-way sync only; API Key + Token auth for first provider (Trello); OAuth deferred  
**Scale/Scope**: Per-organization single active provider integration; up to ~50 action items per meeting; provider rate limits handled per-implementation

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Rule | Status | Notes |
|------|--------|-------|
| Vertical Slice Architecture | ✅ Pass | Feature placed under `Features/ActionItems/` with its own Models, Services, Validators, Endpoints, Jobs |
| Partial Controller Pattern | ✅ Pass | 3 controllers (ActionItemReview, UserIntegration, IntegrationAdmin), each split into definition + endpoint files |
| Tenant Isolation by Default | ✅ Pass | All new entities (`ActionItem`, `OrganizationIntegration`, `OrganizationIntegrationConfig`, `ExternalAccountLink`, `ExternalMemberMapping`) implement `IHasOrganizationId` |
| Strict Single Membership Rule | ✅ Pass | No impact; feature does not modify membership model |
| Standardized Operational Errors | ✅ Pass | All endpoints use `result.ToProblem(correlationIdProvider)` |
| Automated Tests Required | ✅ Pass | Acceptance scenarios cover all user stories; integration tests required for extraction, review, sync, and admin settings |

## Project Structure

### Documentation (this feature)

```text
specs/008-action-items-integration/
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
│   │   │   ├── UserIntegration/
│   │   │       ├── UserIntegrationController.cs         # definition
│   │   │       ├── GetMyConnectionsEndpoint.cs
│   │   │       ├── ConnectProviderEndpoint.cs
│   │   │       └── DisconnectProviderEndpoint.cs
│   │   │   └── Admin/                    # Integration admin settings (org-level)
│   │   │       ├── IntegrationAdminController.cs
│   │   │       ├── GetIntegrationConfigEndpoint.cs
│   │   │       ├── SaveIntegrationConfigEndpoint.cs
│   │   │       ├── ListProviderProjectsEndpoint.cs
│   │   │       ├── ListProviderListsEndpoint.cs
│   │   │       ├── GetMemberProviderStatusEndpoint.cs
│   │   │       └── SetMemberMappingEndpoint.cs
│   │   ├── Models/
│   │   │   ├── Entities/
│   │   │   │   ├── ActionItem.cs
│   │   │   │   ├── OrganizationIntegration.cs
│   │   │   │   ├── OrganizationIntegrationConfig.cs
│   │   │   │   ├── ExternalAccountLink.cs
│   │   │   │   └── ExternalMemberMapping.cs
│   │   │   ├── Enums/
│   │   │   │   ├── ActionItemStatus.cs
│   │   │   │   ├── IntegrationStatus.cs
│   │   │   │   └── ExternalProvider.cs
│   │   │   ├── Requests/
│   │   │   │   ├── UpdateActionItemRequest.cs
│   │   │   │   ├── ApproveActionItemRequest.cs
│   │   │   │   ├── RejectActionItemRequest.cs
│   │   │   │   ├── BulkSyncRequest.cs
│   │   │   │   └── ConnectProviderRequest.cs
│   │   │   └── Responses/
│   │   │       ├── ActionItemResponse.cs
│   │   │       ├── ActionItemListResponse.cs
│   │   │       ├── SyncResultResponse.cs
│   │   │       └── BulkSyncResponse.cs
│   │   ├── Services/
│   │   │   ├── Abstractions/
│   │   │   │   ├── ITaskProvider.cs
│   │   │   │   ├── ITaskProviderFactory.cs
│   │   │   │   ├── ProviderProject.cs
│   │   │   │   ├── ProviderList.cs
│   │   │   │   ├── ProviderTaskResult.cs
│   │   │   │   └── ProviderTaskRequest.cs
│   │   │   ├── Providers/
│   │   │   │   └── Trello/
│   │   │   │       ├── TrelloTaskProvider.cs
│   │   │   │       ├── TrelloAuthValidator.cs
│   │   │   │       └── TrelloDtoMapper.cs
│   │   │   ├── IActionItemService.cs
│   │   │   ├── ActionItemService.cs
│   │   │   ├── IIntegrationAdminService.cs
│   │   │   ├── IntegrationAdminService.cs
│   │   │   ├── IUserIntegrationService.cs
│   │   │   └── UserIntegrationService.cs
│   │   ├── Jobs/
│   │   │   ├── ExtractActionItemsJob.cs
│   │   │   └── SyncActionItemsToProviderJob.cs
│   │   └── Validators/
│   │       ├── UpdateActionItemRequestValidator.cs
│   │       ├── ApproveActionItemRequestValidator.cs
│   │       └── ConnectProviderRequestValidator.cs
├── Infrastructure/
│   └── Persistence/
│       └── DbContext/
│           └── ApplicationDbContext.cs   # add DbSets
│       └── Configurations/
│           ├── ActionItemConfiguration.cs
│           ├── OrganizationIntegrationConfiguration.cs
│           ├── OrganizationIntegrationConfigConfiguration.cs
│           ├── ExternalAccountLinkConfiguration.cs
│           └── ExternalMemberMappingConfiguration.cs
└── tests/
    └── MeetingAssistant.Tests.Integration/
        └── ActionItems/
            ├── ExtractActionItemsJobTests.cs
            ├── ActionItemReviewTests.cs
            ├── ProviderSyncTests.cs
            └── IntegrationConnectionTests.cs
```

**Structure Decision**: All endpoints consolidated under **`Features/ActionItems/`** as a single vertical slice:
- `Endpoints/Review/` — action item review and sync (host/cohost)
- `Endpoints/UserIntegration/` — personal external provider connection (any user)
- `Endpoints/Admin/` — org-level integration settings and member mapping (Org Admin only)

This keeps all integration-related code in one domain slice, aligning with the Vertical Slice Architecture principle.

## Complexity Tracking

> No constitution violations detected. No complexity justifications required.

## Research Summary

See [research.md](research.md) for full details. Key decisions:
- **Provider Strategy Pattern**: `ITaskProvider` abstraction allows pluggable providers. Trello is the first implementation.
- **Trello API**: Direct `HttpClient` wrapper (no SDK) inside `TrelloTaskProvider`
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
   - `SyncActionItemsToProviderJob` skips items where `ExternalTaskId != null`.

3. **Retry Policy**:
   - LLM calls: 3 retries with exponential backoff over 15 minutes (Hangfire automatic retry).
   - Provider transient errors: 3 retries with exponential backoff (Polly policy on `ITaskProvider` implementations).
   - Provider non-retryable errors (401/404): mark integration unhealthy, stop job.

4. **Member Resolution Priority**:
   ```
   ExternalAccountLink (self-connected) → ExternalMemberMapping (admin-defined) → no assignee
   ```

5. **Project Member Caching**: `ITaskProvider.ListProjectMembersAsync` result cached in-memory for 5 minutes via `IMemoryCache`.

6. **Encryption**: `IDataProtector` injected into services. Protect on write, unprotect on read. No plaintext tokens in memory longer than necessary.

7. **Provider Abstraction**:
   - All provider-specific logic lives in `ITaskProvider` implementations.
   - Controllers and endpoints are completely provider-agnostic.
   - Adding a new provider (e.g., ClickUp) requires only:
     1. New `ClickUpTaskProvider : ITaskProvider`
     2. DI registration `services.AddScoped<ITaskProvider, ClickUpTaskProvider>()`
     3. New JSON payload shape in `OrganizationIntegrationConfig.EncryptedProviderPayload`

## Implementation Phases

### Phase 1: Entities & Migration
- Create all 5 entities with EF Core configurations
- Add `DbSet`s to `ApplicationDbContext`
- Generate and apply migration

### Phase 2: Provider Abstraction & Trello Implementation
- Create `ITaskProvider`, `ITaskProviderFactory`, and generic DTOs
- Implement `TrelloTaskProvider` with `HttpClient`
- Implement `IntegrationAdminService` and `UserIntegrationService` with `IDataProtector`

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
- Implement `SyncActionItemsToProviderJob`
- Implement member resolution logic
- Add transient retry logic
- Implement single-item and bulk sync endpoints

### Phase 6: Admin Settings Endpoints
- Implement integration admin settings under `Features/ActionItems/Endpoints/Admin/`
- Add project/list fetching endpoints
- Add member mapping endpoints

### Phase 7: Integration Tests
- Test extraction job with mocked LLM
- Test review flow (approve, reject, edit, concurrent modification)
- Test sync with mocked `ITaskProvider`
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
