# End-to-End Flow: Action Items & Trello Integration

**Feature**: Phase 6 Action Items & Trello Integration  
**Date**: 2026-05-04  
**Scope**: Complete lifecycle from meeting end through action item extraction, review, Trello connection, and sync

---

## Table of Contents

1. [System Context](#1-system-context)
2. [Flow 1: Extract Action Items from Meeting Transcript](#2-flow-1-extract-action-items-from-meeting-transcript)
3. [Flow 2: Review and Approve Action Items](#3-flow-2-review-and-approve-action-items)
4. [Flow 3: Connect Trello at Organization Level](#4-flow-3-connect-trello-at-organization-level)
5. [Flow 4: Sync Approved Action Items to Trello](#5-flow-4-sync-approved-action-items-to-trello)
6. [Flow 5: Connect Personal Trello Account](#6-flow-5-connect-personal-trello-account)
7. [Cross-Cutting Concerns](#7-cross-cutting-concerns)
8. [State Machine Summary](#8-state-machine-summary)

---

## 1. System Context

### Architecture Layers

```
┌─────────────────────────────────────────────┐
│  Client (Web/Mobile)                        │
│  • Bearer Token (User JWT)                  │
│  • Polls GET /api/meetings/{id}/action-items│
└──────────────────┬──────────────────────────┘
                   │ HTTPS
┌──────────────────▼──────────────────────────┐
│  ASP.NET Core Pipeline                      │
│  • JWT Authentication Middleware            │
│  • CorrelationId Middleware                 │
│  • Tenant Resolution (orgId from JWT)       │
└──────────────────┬──────────────────────────┘
                   │
┌──────────────────▼──────────────────────────┐
│  Controllers (partial classes)              │
│  • ActionItemsController (Review)           │
│  • TrelloAdminController (Org settings)     │
│  • TrelloUserController (Personal conn)     │
└──────────────────┬──────────────────────────┘
                   │
┌──────────────────▼──────────────────────────┐
│  Hangfire Background Jobs                   │
│  • ExtractActionItemsJob (triggered by      │
│    MeetingTranscriptReadyEvent)             │
│  • SyncActionItemsToTrelloJob (triggered    │
│    by manual sync endpoint)                 │
└──────────────────┬──────────────────────────┘
                   │
┌──────────────────▼──────────────────────────┐
│  AppDbContext (EF Core + Npgsql)            │
│  • Global Query Filter: OrganizationId      │
│  • DbSet<ActionItem>                        │
│  • DbSet<OrganizationIntegration>           │
│  • DbSet<TrelloWorkspaceConfig>             │
│  • DbSet<ExternalAccountLink>               │
│  • DbSet<TrelloMemberMapping>               │
└──────────────────┬──────────────────────────┘
                   │
┌──────────────────▼──────────────────────────┐
│  PostgreSQL                                 │
│  • ActionItems, OrganizationIntegrations    │
│  • TrelloWorkspaceConfigs                   │
│  • ExternalAccountLinks                     │
│  • TrelloMemberMappings                     │
└─────────────────────────────────────────────┘
```

### JWT Claims Required

| Claim | Source | Usage |
|-------|--------|-------|
| `userId` | `sub` or custom | Ownership checks, action item assignee |
| `organizationId` | custom | Tenant isolation (global query filter) |
| `roles` | custom | Org Admin check for Trello settings |

---

## 2. Flow 1: Extract Action Items from Meeting Transcript

### 2.1 Happy Path (Automatic)

```
Meeting ends → transcript pipeline completes
  │
  ▼
[MeetingTranscriptReadyEvent published]
  │
  ├── Handler A: GenerateMeetingSummaryJob (existing)
  └── Handler B: ExtractActionItemsJob (new, parallel)
        │
        ▼
[ExtractActionItemsJob.Execute(meetingId)]
  1. Idempotency check:
     • SELECT COUNT(*) FROM ActionItems WHERE MeetingId = @meetingId
     • If count > 0 → log "Already extracted" and return (no-op)
  2. Load meeting context:
     • Meeting transcript text
     • Participant roster with ParticipantId + UserId + display names
  3. Call outsourced LLM with prompt:
     • "Extract action items from this transcript."
     • "Use ONLY the following participant IDs for assignees..."
     • Roster JSON provided as structured context
  4. Parse LLM response (expected structured JSON):
     • title, description, assignedParticipantId, dueDate
  5. For each extracted item:
     • Resolve AssignedToParticipantId → AssignedToUserId via roster
     • Build ActionItem entity:
       - Id = Guid.NewGuid()
       - OrganizationId = meeting.OrganizationId
       - MeetingId = meetingId
       - Title = item.title
       - Description = item.description
       - AssignedToParticipantId = item.assignedParticipantId (or null)
       - AssignedToUserId = resolved user ID (or null)
       - DueDateUtc = parsed dueDate (or null)
       - Status = PendingReview
       - TrelloCardId = null
       - MissingAssigneeReason = null
       - RowVersion = generated
       - CreatedAtUtc = NowUtc
       - UpdatedAtUtc = NowUtc
     • _dbContext.ActionItems.Add(actionItem)
  6. await _dbContext.SaveChangesAsync()
  7. Log extraction metrics (count, duration) for SC-001/SC-002
```

### 2.2 LLM Unavailable / Retry Path

```
[ExtractActionItemsJob.Execute(meetingId)]
  1. LLM call fails (timeout, 5xx)
  2. Polly retry policy kicks in:
     • Retry up to 3 times with exponential backoff
     • Delays: ~2s, ~4s, ~8s (total ~15 minutes)
  3. If all retries exhaust:
     • Log failure with CorrelationId
     • Update Meeting.ExtractionStatus = Failed (or use a Hangfire job state)
     • Do NOT create any ActionItems
     • Host can trigger manual re-extraction later
```

### 2.3 Error Paths

| Scenario | Trigger | Layer | Response / Behavior |
|----------|---------|-------|---------------------|
| Duplicate extraction | ActionItems already exist for meeting | Job idempotency check | Logged as no-op; no duplicates created |
| LLM returns malformed JSON | Invalid structure in response | Job parser | Log error; abort job; no partial data persisted |
| LLM hallucinates assignee | Participant ID not in roster | Job resolver | Assignee fields left null; item still created with `MissingAssigneeReason = Unresolved` (host fixes during review) |
| LLM unreachable after retries | 3 failed attempts | Polly + Job | Meeting extraction marked `Failed`; no action items created |
| DB failure | PostgreSQL unavailable | SaveChanges | Exception logged; job fails; Hangfire will retry per its own policy |

---

## 3. Flow 2: Review and Approve Action Items

### 3.1 Happy Path — List Action Items

```
User opens meeting review panel
  │
  ▼
Client GET /api/meetings/{meetingId}/action-items
  Headers: Authorization: Bearer <user-jwt>
  │
  ▼
[ListActionItemsEndpoint.cs]
  1. [FromRoute] Guid meetingId
  2. Calls _actionItemService.GetByMeetingAsync(meetingId, userId, orgId, ct)
  │
  ▼
[ActionItemService.GetByMeetingAsync]
  1. Verify user is a participant of the meeting:
     • Check MeetingParticipants for (MeetingId, UserId)
     • If not found → return Result.Failure(Error.Forbidden)
  2. Query ActionItems:
     • MeetingId == meetingId
     • OrganizationId == orgId (global filter)
     • Order by CreatedAtUtc ASC
  3. Map each entity → ActionItemResponse
  4. Return Result<List<ActionItemResponse>>.Success(list)
  │
  ▼
[ListActionItemsEndpoint.cs]
  Result.IsSuccess → 200 OK
  Response Body: [ { id, title, description, status, assignedToUserId, dueDateUtc, ... } ]
```

### 3.2 Happy Path — Approve a Single Action Item

```
Host reviews an item and taps "Approve"
  │
  ▼
Client PATCH /api/action-items/{id}/approve
  Headers: Authorization: Bearer <user-jwt>
  Headers: If-Match: "abc123" (ETag / RowVersion)
  │
  ▼
[ApproveActionItemEndpoint.cs]
  1. [FromRoute] Guid id
  2. [FromHeader(Name = "If-Match")] string etag
  3. Calls _actionItemService.ApproveAsync(id, userId, orgId, etag, ct)
  │
  ▼
[ActionItemService.ApproveAsync]
  1. Find action item by id + orgId (global filter)
  2. If not found → 404 Not Found
  3. Verify role:
     • Load meeting participants for this item's MeetingId
     • Check if user is Host, CoHost, or Org Admin
     • If not → 403 Forbidden
  4. Optimistic concurrency check:
     • Compare provided ETag with entity.RowVersion
     • If mismatch → 409 Conflict
  5. State transition guard:
     • If Status == Synced or SyncedNoAssignee → 409 Conflict (terminal)
     • If Status == Approved → idempotent 200 OK
     • If Status == Rejected → allow transition to Approved (per clarification)
     • If Status == PendingReview → transition to Approved
  6. Update:
     • Status = Approved
     • UpdatedAtUtc = NowUtc
  7. await _dbContext.SaveChangesAsync()
     • If DbUpdateConcurrencyException → 409 Conflict
  8. Map → ActionItemResponse
  9. Return Result<ActionItemResponse>.Success(response)
  │
  ▼
[ApproveActionItemEndpoint.cs]
  Result.IsSuccess → 200 OK
```

### 3.3 Happy Path — Reject an Action Item

```
Host taps "Reject"
  │
  ▼
Client PATCH /api/action-items/{id}/reject
  Headers: Authorization: Bearer <user-jwt>
  Headers: If-Match: "abc123"
  │
  ▼
[RejectActionItemEndpoint.cs]
  Same flow as approve, but:
  • Status = Rejected
  • Rejected items are excluded from sync
  • Rejected items are retained for audit
```

### 3.4 Happy Path — Edit an Action Item

```
Host edits title / description / assignee / due date
  │
  ▼
Client PUT /api/action-items/{id}
  Headers: Authorization: Bearer <user-jwt>
  Headers: If-Match: "abc123"
  Body: { "title": "...", "description": "...", "assignedToParticipantId": "...", "dueDateUtc": "..." }
  │
  ▼
[UpdateActionItemEndpoint.cs]
  1. Validation via FluentValidation
  2. Calls _actionItemService.UpdateAsync(id, request, userId, orgId, etag, ct)
  │
  ▼
[ActionItemService.UpdateAsync]
  1. Find + auth checks (same as approve)
  2. Optimistic concurrency check (same as approve)
  3. Terminal state guard: Synced / SyncedNoAssignee → 409
  4. Apply updates:
     • Title = request.Title
     • Description = request.Description
     • AssignedToParticipantId = request.AssignedToParticipantId
     • AssignedToUserId = resolved from roster (or null)
     • DueDateUtc = request.DueDateUtc
     • UpdatedAtUtc = NowUtc
  5. SaveChangesAsync
  6. Return updated response
```

### 3.5 Happy Path — Delete an Action Item

```
Host taps "Delete"
  │
  ▼
Client DELETE /api/action-items/{id}
  Headers: Authorization: Bearer <user-jwt>
  Headers: If-Match: "abc123"
  │
  ▼
[DeleteActionItemEndpoint.cs]
  1. Auth checks (Host / CoHost / Org Admin only)
  2. Optimistic concurrency check
  3. Terminal state guard: Synced / SyncedNoAssignee → 409 (immutable)
  4. _dbContext.ActionItems.Remove(entity)
  5. SaveChangesAsync
  6. Return 204 No Content
```

### 3.6 Happy Path — Manual Re-Extraction

```
Host taps "Re-extract" (only visible when 0 action items exist)
  │
  ▼
Client POST /api/meetings/{meetingId}/action-items/re-extract
  Headers: Authorization: Bearer <user-jwt>
  │
  ▼
[ReExtractActionItemsEndpoint.cs]
  1. Verify user is Host / CoHost / Org Admin for the meeting
  2. Check if any ActionItems exist for meetingId:
     • If count > 0 → 409 Conflict "Delete existing action items first"
  3. Enqueue ExtractActionItemsJob for meetingId
  4. Return 202 Accepted
```

### 3.7 Error Paths

| Scenario | Trigger | Layer | Response |
|----------|---------|-------|----------|
| Missing JWT | No `Authorization` header | Auth Middleware | 401 Unauthorized |
| Not a meeting participant | User not in MeetingParticipants | Service | 403 Forbidden |
| Regular participant tries to edit | User is Participant/Observer | Service | 403 Forbidden |
| Action item not found | Invalid / deleted ID | Service | 404 Not Found |
| Stale ETag | Concurrent edit by another host | Service | 409 Conflict |
| Edit synced item | Status == Synced | Service | 409 Conflict |
| Re-extract with existing items | ActionItems count > 0 | Service | 409 Conflict |
| Cross-tenant access | Org B user, Org A meeting | Global filter | 404 Not Found |

---

## 4. Flow 3: Connect Trello at Organization Level

### 4.1 Happy Path — Save Integration Settings

```
Org Admin opens Organization Settings → Integrations → Trello
  │
  ▼
Client POST /api/admin/trello/settings
  Headers: Authorization: Bearer <user-jwt>
  Body: { "apiKey": "trello-key-xxx", "apiToken": "trello-token-yyy",
          "selectedBoardId": "board-id", "selectedListId": "list-id" }
  │
  ▼
[SaveTrelloSettingsEndpoint.cs]
  1. [Authorize(Policy = "OrgAdmin")]
  2. FluentValidation:
     • apiKey: not empty
     • apiToken: not empty
     • selectedBoardId: not empty
     • selectedListId: not empty
  3. Calls _trelloAdminService.SaveSettingsAsync(request, orgId, ct)
  │
  ▼
[TrelloAdminService.SaveSettingsAsync]
  1. Encrypt credentials:
     • protectedApiKey = _dataProtector.Protect(request.ApiKey)
     • protectedApiToken = _dataProtector.Protect(request.ApiToken)
  2. Validate token against Trello API:
     • GET /1/members/me with key + token
     • If 401 → return Result.Failure(Error.BadRequest("Invalid Trello credentials"))
  3. Verify board and list exist and are accessible:
     • GET /1/boards/{boardId} with key + token
     • GET /1/boards/{boardId}/lists
     • If 404 → return Result.Failure(Error.BadRequest("Board or list not found"))
  4. Upsert TrelloWorkspaceConfig:
     • OrganizationId = orgId
     • SelectedBoardId = request.SelectedBoardId
     • SelectedListId = request.SelectedListId
     • EncryptedApiKey = protectedApiKey
     • EncryptedApiToken = protectedApiToken
     • UpdatedAtUtc = NowUtc
  5. Upsert OrganizationIntegration:
     • IntegrationType = Trello
     • Status = Active
     • UpdatedAtUtc = NowUtc
  6. SaveChangesAsync
  7. Return Result.Success
  │
  ▼
[SaveTrelloSettingsEndpoint.cs]
  200 OK
```

### 4.2 Happy Path — Fetch Boards and Lists

```
Org Admin opens board/list selector
  │
  ▼
Client GET /api/admin/trello/boards
  Headers: Authorization: Bearer <user-jwt>
  │
  ▼
[ListTrelloBoardsEndpoint.cs]
  1. [Authorize(Policy = "OrgAdmin")]
  2. Calls _trelloAdminService.GetBoardsAsync(orgId, ct)
  │
  ▼
[TrelloAdminService.GetBoardsAsync]
  1. Load TrelloWorkspaceConfig for orgId
  2. Decrypt credentials:
     • apiKey = _dataProtector.Unprotect(config.EncryptedApiKey)
     • apiToken = _dataProtector.Unprotect(config.EncryptedApiToken)
  3. Call Trello API:
     • GET /1/members/me/boards?key={apiKey}&token={apiToken}
  4. For each board, fetch lists:
     • GET /1/boards/{boardId}/lists?key={apiKey}&token={apiToken}
  5. Return Result<List<TrelloBoardDto>>.Success(boards)
  │
  ▼
[ListTrelloBoardsEndpoint.cs]
  200 OK
  Response: [ { "id": "b1", "name": "Team Board", "lists": [ { "id": "l1", "name": "To Do" } ] } ]
```

### 4.3 Error Paths

| Scenario | Trigger | Layer | Response |
|----------|---------|-------|----------|
| Non-admin tries to save settings | User without OrgAdmin role | AuthZ Policy | 403 Forbidden |
| Invalid Trello credentials | 401 from Trello API | Service | 400 Bad Request |
| Board deleted after setup | 404 from Trello API | Service | 400 Bad Request |
| Trello API rate limit | 429 from Trello API | ITrelloClient + Polly | Retried 3×; if still failing → 502 Bad Gateway |
| Encryption failure | Data Protection provider unavailable | Service | 500 Internal Server Error |

---

## 5. Flow 4: Sync Approved Action Items to Trello

### 5.1 Happy Path — Single Item Sync

```
Host taps "Sync to Trello" on an approved action item
  │
  ▼
Client POST /api/action-items/{id}/sync-to-trello
  Headers: Authorization: Bearer <user-jwt>
  │
  ▼
[SyncActionItemToTrelloEndpoint.cs]
  1. Auth check: Host / CoHost / Org Admin
  2. Calls _actionItemService.SyncToTrelloAsync(id, userId, orgId, ct)
  │
  ▼
[ActionItemService.SyncToTrelloAsync]
  1. Find action item by id + orgId
  2. State guard: Status MUST be Approved
     • If PendingReview / Rejected → 409 Conflict
     • If Synced / SyncedNoAssignee → 409 Conflict (already synced)
  3. Load TrelloWorkspaceConfig for orgId
  4. Decrypt credentials
  5. Resolve Trello assignee:
     a. Check ExternalAccountLink for (UserId=AssignedToUserId, Provider=Trello)
        • If found → trelloMemberId = link.ExternalUserId
     b. Else check TrelloMemberMapping for (UserId=AssignedToUserId)
        • If found → trelloMemberId = mapping.TrelloMemberId
     c. Else → no assignee
  6. If trelloMemberId exists, verify board membership:
     • GET /1/boards/{boardId}/members (cached for 5 min)
     • If member not in list → no assignee; reason = NotBoardMember
  7. Create Trello card:
     • POST /1/cards
       - idList = config.SelectedListId
       - name = actionItem.Title
       - desc = actionItem.Description
       - due = actionItem.DueDateUtc?.ToString("o")
       - idMembers = trelloMemberId (or omitted)
  8. On success:
     • actionItem.TrelloCardId = response.id
     • actionItem.Status = trelloMemberId != null ? Synced : SyncedNoAssignee
     • actionItem.MissingAssigneeReason = reason (or null)
     • actionItem.UpdatedAtUtc = NowUtc
     • SaveChangesAsync
     • Return Result<ActionItemResponse>.Success(response)
  │
  ▼
[SyncActionItemToTrelloEndpoint.cs]
  200 OK
```

### 5.2 Happy Path — Bulk Sync

```
Host taps "Sync All Approved to Trello"
  │
  ▼
Client POST /api/meetings/{meetingId}/action-items/sync-approved
  Headers: Authorization: Bearer <user-jwt>
  │
  ▼
[BulkSyncActionItemsEndpoint.cs]
  1. Auth check: Host / CoHost / Org Admin for meeting
  2. Calls _actionItemService.BulkSyncAsync(meetingId, userId, orgId, ct)
  │
  ▼
[ActionItemService.BulkSyncAsync]
  1. Load all ActionItems for meeting where Status == Approved
  2. Load TrelloWorkspaceConfig + decrypt credentials
  3. Fetch board members (cached for duration of batch)
  4. For each approved item:
     a. Resolve assignee (same logic as single sync)
     b. Attempt create card with retry for transients
     c. Record result per item:
        - Success → Status = Synced / SyncedNoAssignee
        - Transient failure (429/5xx after retries) → Status stays Approved; result = PendingRetry
        - Non-retryable failure (401/404) → Status stays Approved; result = Failed; stop batch
  5. If 401 encountered → update OrganizationIntegration.Status = NeedsReconnect
  6. If 404 encountered → update OrganizationIntegration.Status = InvalidConfig
  7. SaveChangesAsync for all updated items
  8. Build BulkSyncResult:
     • Items: [ { actionItemId, status, trelloCardId, error? } ]
     • IntegrationHealth: Healthy / NeedsReconnect / InvalidConfig
  9. Return Result<BulkSyncResult>.Success(result)
  │
  ▼
[BulkSyncActionItemsEndpoint.cs]
  207 Multi-Status
  Response Body:
  {
    "items": [
      { "actionItemId": "a1", "result": "Synced", "trelloCardId": "c1" },
      { "actionItemId": "a2", "result": "SyncedNoAssignee", "reason": "UserNotConnected", "trelloCardId": "c2" },
      { "actionItemId": "a3", "result": "PendingRetry", "error": "Trello rate limit exceeded" }
    ],
    "integrationHealth": "Healthy"
  }
```

### 5.3 Error Paths

| Scenario | Trigger | Layer | Response |
|----------|---------|-------|----------|
| Sync non-approved item | Status == PendingReview | Service | 409 Conflict |
| Sync already synced item | Status == Synced | Service | 409 Conflict |
| Trello not configured | No TrelloWorkspaceConfig for org | Service | 400 Bad Request "Trello not configured" |
| Invalid Trello credentials | 401 from Trello during sync | Service | Item stays Approved; integration → NeedsReconnect; 207 with Failed result |
| Board/list deleted | 404 from Trello during sync | Service | Item stays Approved; integration → InvalidConfig; 207 with Failed result |
| Trello rate limit | 429 from Trello | ITrelloClient + Polly | Retried 3×; if still failing → result = PendingRetry in 207 |
| Assignee not connected | No ExternalAccountLink or mapping | Service | Card created without assignee; status = SyncedNoAssignee; reason = UserNotConnected |
| Assignee not board member | Member ID not in board members | Service | Card created without assignee; status = SyncedNoAssignee; reason = NotBoardMember |
| Cross-tenant sync | Org B user, Org A action item | Global filter | 404 Not Found |

---

## 6. Flow 5: Connect Personal Trello Account

### 6.1 Happy Path — Self-Connection

```
User opens Profile → Connected Accounts → Trello
  │
  ▼
Client POST /api/me/trello/connect
  Headers: Authorization: Bearer <user-jwt>
  Body: { "apiToken": "personal-trello-token" }
  │
  ▼
[ConnectPersonalTrelloEndpoint.cs]
  1. FluentValidation: apiToken not empty
  2. Calls _trelloUserService.ConnectAsync(request, userId, orgId, ct)
  │
  ▼
[TrelloUserService.ConnectAsync]
  1. Encrypt token:
     • protectedToken = _dataProtector.Protect(request.ApiToken)
  2. Validate against Trello:
     • GET /1/members/me?key={orgApiKey}&token={request.ApiToken}
     • If 401 → return Result.Failure(Error.BadRequest("Invalid token"))
  3. Extract member info:
     • trelloMemberId = response.id
     • trelloUsername = response.username
  4. Upsert ExternalAccountLink:
     • OrganizationId = orgId
     • UserId = userId
     • Provider = Trello
     • ExternalUserId = trelloMemberId
     • ExternalUsername = trelloUsername
     • EncryptedAccessToken = protectedToken
     • UpdatedAtUtc = NowUtc
  5. Upsert TrelloMemberMapping:
     • OrganizationId = orgId
     • UserId = userId
     • TrelloMemberId = trelloMemberId
     • Source = SelfConnected
     • UpdatedAtUtc = NowUtc
  6. SaveChangesAsync
  7. Return Result.Success
  │
  ▼
[ConnectPersonalTrelloEndpoint.cs]
  200 OK
```

### 6.2 Happy Path — Admin Sets Mapping for Member

```
Org Admin opens Member Management → selects member → "Set Trello Member ID"
  │
  ▼
Client POST /api/admin/trello/member-mappings
  Headers: Authorization: Bearer <user-jwt>
  Body: { "userId": "user-id-123", "trelloMemberId": "trello-id-456" }
  │
  ▼
[CreateTrelloMemberMappingEndpoint.cs]
  1. [Authorize(Policy = "OrgAdmin")]
  2. Calls _trelloAdminService.SetMemberMappingAsync(request, orgId, ct)
  │
  ▼
[TrelloAdminService.SetMemberMappingAsync]
  1. Upsert TrelloMemberMapping:
     • OrganizationId = orgId
     • UserId = request.UserId
     • TrelloMemberId = request.TrelloMemberId
     • Source = AdminDefined
     • UpdatedAtUtc = NowUtc
  2. SaveChangesAsync
  3. Return Result.Success
```

### 6.3 Error Paths

| Scenario | Trigger | Layer | Response |
|----------|---------|-------|----------|
| Invalid personal token | 401 from Trello | Service | 400 Bad Request |
| Non-admin sets mapping for another user | Regular user calls admin endpoint | AuthZ Policy | 403 Forbidden |
| Admin sets mapping for user in other org | UserId belongs to Org B | Global filter + validation | 404 or 400 |

---

## 7. Cross-Cutting Concerns

### 7.1 Tenant Isolation (Zero Leakage Guarantee)

All new entities implement `IHasOrganizationId` and are protected by the EF Core Global Query Filter:

- **ActionItems**: Filtered by `OrganizationId` from the parent `Meeting`
- **TrelloWorkspaceConfig**: One record per org; global filter prevents cross-org access
- **ExternalAccountLink**: Scoped to `(OrganizationId, UserId)`
- **TrelloMemberMapping**: Scoped to `(OrganizationId, UserId)`
- **OrganizationIntegration**: Scoped to `OrganizationId`

Defense-in-depth: Service layer validates roles (Host/CoHost/Org Admin) before mutations, but the database-level global filter is the first line of defense.

### 7.2 Authentication & Authorization Matrix

| Endpoint | Auth Required | Additional Authorization |
|----------|-------------|------------------------|
| GET /api/meetings/{id}/action-items | User JWT | Must be meeting participant |
| PATCH /api/action-items/{id}/approve | User JWT | Must be Host, CoHost, or Org Admin for the meeting |
| PATCH /api/action-items/{id}/reject | User JWT | Must be Host, CoHost, or Org Admin |
| PUT /api/action-items/{id} | User JWT | Must be Host, CoHost, or Org Admin |
| DELETE /api/action-items/{id} | User JWT | Must be Host, CoHost, or Org Admin |
| POST /api/meetings/{id}/action-items/re-extract | User JWT | Must be Host, CoHost, or Org Admin |
| POST /api/action-items/{id}/sync-to-trello | User JWT | Must be Host, CoHost, or Org Admin |
| POST /api/meetings/{id}/action-items/sync-approved | User JWT | Must be Host, CoHost, or Org Admin |
| POST /api/admin/trello/settings | User JWT | OrgAdmin policy |
| GET /api/admin/trello/boards | User JWT | OrgAdmin policy |
| POST /api/admin/trello/member-mappings | User JWT | OrgAdmin policy |
| POST /api/me/trello/connect | User JWT | None (self only) |

### 7.3 Optimistic Concurrency Control

All action item mutations (approve, reject, edit, delete) require an `If-Match` header containing the current `RowVersion` (ETag):

```
Client → If-Match: "abc123"
Service → Compare with entity.RowVersion
If mismatch → 409 Conflict
If match → Proceed with update; RowVersion auto-incremented by EF
```

Clients should re-fetch the action item after a 409 to get the latest state before retrying.

### 7.4 Retry Policies

| Layer | What | Policy | Max Attempts | Backoff |
|-------|------|--------|--------------|---------|
| LLM extraction | Transient failures (timeout, 5xx) | Exponential backoff | 3 | ~2s, ~4s, ~8s (~15 min total) |
| Trello API | 429, 5xx, timeouts | Exponential backoff | 3 | ~1s, ~2s, ~4s |
| Hangfire | Job execution failure | Hangfire own retry | 10 (default) | progressive |

### 7.5 Data Protection & Encryption

| Data | Protection | Method |
|------|-----------|--------|
| Org Trello API Key | Encrypted at rest | `IDataProtector.Protect()` |
| Org Trello API Token | Encrypted at rest | `IDataProtector.Protect()` |
| Personal Trello Token | Encrypted at rest | `IDataProtector.Protect()` |

Decryption happens in-memory at runtime only. Raw tokens never touch logs or responses.

### 7.6 Error Response Format (RFC 7807 Problem Details)

All errors flow through `result.ToProblem(correlationIdProvider)`:

```json
{
  "type": "Conflict",
  "title": "Action item has already been synced and is immutable.",
  "status": 409,
  "correlationId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
}
```

```json
{
  "type": "ValidationError",
  "title": "One or more validation errors occurred.",
  "status": 422,
  "correlationId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "errors": {
    "ApiToken": ["API Token is required."]
  }
}
```

---

## 8. State Machine Summary

### Action Item Lifecycle

```
                    ┌──────────────────┐
        ┌──────────►│  PendingReview   │◄────────────────┐
        │ extract   └────────┬─────────┘                 │
        │                    │                           │
        │     approve        │    reject                 │   delete
        │         ┌──────────▼──────────┐               │
        │         │      Approved       │               │
        │         └──────────┬──────────┘               │
        │                    │ sync                      │
        │         ┌──────────▼──────────┐               │
        │         │       Synced        │               │
        │         └─────────────────────┘               │
        │                                               │
        │                    ┌──────────────────────────┘
        │                    │
        │         ┌──────────▼──────────┐
        │         │   SyncedNoAssignee  │
        │         └─────────────────────┘
        │
        │    undo (Host/Admin)
        └───────────────────────────────────────────────┘

        ┌──────────────┐
        │   Rejected   │
        └──────────────┘
```

### Transitions

| From | To | Trigger | Who |
|------|-----|---------|-----|
| (none) | PendingReview | Meeting transcript ready; extraction job runs | System |
| PendingReview | Approved | Host/CoHost/Admin approves | Authorized user |
| PendingReview | Rejected | Host/CoHost/Admin rejects | Authorized user |
| Approved | PendingReview | Host/CoHost/Admin undoes approval | Authorized user |
| Rejected | PendingReview | Host/CoHost/Admin undoes rejection | Authorized user |
| Approved | Synced | Sync succeeds with assignee | Authorized user |
| Approved | SyncedNoAssignee | Sync succeeds but no valid assignee | Authorized user |
| Any (non-terminal) | (deleted) | Host/CoHost/Admin deletes | Authorized user |
| Synced | (any) | ❌ Blocked — terminal state | — |
| SyncedNoAssignee | (any) | ❌ Blocked — terminal state | — |

### Permission Matrix by State

| Action | PendingReview | Approved | Rejected | Synced | SyncedNoAssignee |
|--------|-------------|----------|----------|--------|-----------------|
| **View** | ✅ All participants | ✅ All participants | ✅ All participants | ✅ All participants | ✅ All participants |
| **Edit** | ✅ Host/CoHost/Admin | ✅ Host/CoHost/Admin | ✅ Host/CoHost/Admin | ❌ 409 | ❌ 409 |
| **Approve** | ✅ → Approved | ✅ 200 (idempotent) | ✅ → Approved | ❌ 409 | ❌ 409 |
| **Reject** | ✅ → Rejected | ✅ → Rejected | ✅ 200 (idempotent) | ❌ 409 | ❌ 409 |
| **Delete** | ✅ | ✅ | ✅ | ❌ 409 | ❌ 409 |
| **Sync** | ❌ 409 | ✅ → Synced/SyncedNoAssignee | ❌ 409 | ❌ 409 | ❌ 409 |

---

## 9. Complete Request/Response Walkthrough

### Example: Full Meeting Lifecycle with Action Items

**14:00 UTC** — Meeting ends, transcript completes:
```
[SYSTEM] MeetingTranscriptReadyEvent published
[SYSTEM] ExtractActionItemsJob enqueued and executes
[SYSTEM] LLM extracts 3 action items
[SYSTEM] ActionItems created with Status = PendingReview
```

**14:05 UTC** — Host views action items:
```http
GET /api/meetings/{meeting-id}/action-items
Authorization: Bearer <host-jwt>

→ 200 OK
[
  { "id": "a1", "title": "Prepare Q2 report", "status": "PendingReview", "assignedToUserId": "u1", ... },
  { "id": "a2", "title": "Schedule follow-up", "status": "PendingReview", "assignedToUserId": null, ... },
  { "id": "a3", "title": "Review budget", "status": "PendingReview", "assignedToUserId": "u2", ... }
]
```

**14:10 UTC** — Host approves a1 and a3, rejects a2:
```http
PATCH /api/action-items/a1/approve
Authorization: Bearer <host-jwt>
If-Match: "etag-a1"

→ 200 OK
{ "id": "a1", "status": "Approved", ... }

PATCH /api/action-items/a3/approve
Authorization: Bearer <host-jwt>
If-Match: "etag-a3"

→ 200 OK
{ "id": "a3", "status": "Approved", ... }

PATCH /api/action-items/a2/reject
Authorization: Bearer <host-jwt>
If-Match: "etag-a2"

→ 200 OK
{ "id": "a2", "status": "Rejected", ... }
```

**14:15 UTC** — Org Admin connects Trello (one-time setup):
```http
POST /api/admin/trello/settings
Authorization: Bearer <admin-jwt>

{
  "apiKey": "trello-key-xxx",
  "apiToken": "trello-token-yyy",
  "selectedBoardId": "board-123",
  "selectedListId": "list-456"
}

→ 200 OK
```

**14:20 UTC** — User u1 connects personal Trello:
```http
POST /api/me/trello/connect
Authorization: Bearer <u1-jwt>

{ "apiToken": "personal-token-u1" }

→ 200 OK
```

**14:25 UTC** — Host bulk-syncs approved items:
```http
POST /api/meetings/{meeting-id}/action-items/sync-approved
Authorization: Bearer <host-jwt>

→ 207 Multi-Status
{
  "items": [
    { "actionItemId": "a1", "result": "Synced", "trelloCardId": "card-111" },
    { "actionItemId": "a3", "result": "SyncedNoAssignee", "reason": "UserNotConnected", "trelloCardId": "card-222" }
  ],
  "integrationHealth": "Healthy"
}
```

**14:30 UTC** — Host tries to edit synced item (blocked):
```http
PUT /api/action-items/a1
Authorization: Bearer <host-jwt>
If-Match: "etag-a1-new"

{ "title": "Updated title" }

→ 409 Conflict
{
  "type": "Conflict",
  "title": "Action item has already been synced and is immutable.",
  "status": 409
}
```

**14:35 UTC** — Concurrent edit attempt (another host):
```http
PATCH /api/action-items/a2/approve
Authorization: Bearer <other-host-jwt>
If-Match: "stale-etag"

→ 409 Conflict
{
  "type": "Conflict",
  "title": "The action item was modified by another user. Please refresh and try again.",
  "status": 409
}
```

---

*End of End-to-End Flow Document*
