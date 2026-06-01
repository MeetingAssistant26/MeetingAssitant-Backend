# API Contracts: Action Items & External Task Provider Integration

## Base Path

All endpoints are prefixed under `/api` and require authentication unless noted.

---

## Action Item Review Endpoints

### List Meeting Action Items

```
GET /api/organizations/{orgId:guid}/meetings/{meetingId:guid}/action-items
```

**Auth**: Any participant of the meeting can view.  
**Query Params**: `?status=PendingReview|Approved|Rejected|Synced|SyncedNoAssignee` (optional filter)

**Response 200 OK**:
```json
{
  "items": [
    {
      "id": "guid",
      "meetingId": "guid",
      "title": "Prepare Q3 roadmap",
      "description": "Compile department priorities into a presentation",
      "assignedToUserId": "guid|null",
      "assignedToUserName": "Ahmed Hassan|null",
      "dueDateUtc": "2026-05-10T00:00:00Z|null",
      "status": "PendingReview",
      "externalTaskId": "string|null",
      "externalTaskUrl": "string|null",
      "externalProvider": "Trello|null",
      "syncMissingAssigneeReason": "string|null",
      "extractedAtUtc": "2026-05-04T10:00:00Z",
      "syncedAtUtc": "2026-05-04T11:00:00Z|null",
      "rowVersionEtag": "base64-row-version"
    }
  ],
  "totalCount": 1,
  "page": 1,
  "pageSize": 1
}
```

### List Organization Action Items

```
GET /api/organizations/{orgId:guid}/action-items?page=&pageSize=&assignee=&meetingId=&status=&provider=&fromUtc=&toUtc=
```

**Auth**: Any active organization member.  
**Query Params**:
- `assignee=all|me` (optional, defaults to `all`)
- `meetingId=guid` (optional)
- `status=PendingReview|Approved|Rejected|Synced|SyncedNoAssignee` (optional)
- `provider=Trello|ClickUp` (optional)
- `fromUtc` / `toUtc` filter by `dueDateUtc` when present
- `page` / `pageSize` paginate results; page is one-based

**Response 200 OK**: Same paginated `ActionItemListResponse` as the meeting-scoped list. Every item includes `rowVersionEtag` for use as `If-Match` on mutations.

---

### Update Action Item

```
PATCH /api/organizations/{orgId:guid}/meetings/{meetingId:guid}/action-items/{id:guid}
```

**Auth**: Host, CoHost, or Org Admin.  
**Headers**: `If-Match: <etag>` (required for optimistic concurrency)

**Request Body**:
```json
{
  "title": "Updated title",
  "description": "Updated description",
  "assignedToParticipantId": "guid|null",
  "dueDateUtc": "2026-05-15T00:00:00Z|null",
  "status": "Approved|Rejected|PendingReview"
}
```

**Response 200 OK**: Updated action item.  
**Response 409 Conflict**: Stale ETag — client must refresh.  
**Response 422 UnprocessableEntity**: Invalid status transition (e.g., editing `Synced` item).

---

### Sync Single Action Item

```
POST /api/organizations/{orgId:guid}/meetings/{meetingId:guid}/action-items/{id:guid}/sync
```

**Auth**: Host, CoHost, or Org Admin.

**Response 200 OK**:
```json
{
  "actionItemId": "guid",
  "status": "Synced|SyncedNoAssignee",
  "externalTaskId": "string|null",
  "externalTaskUrl": "string|null",
  "syncMissingAssigneeReason": "string|null",
  "rowVersionEtag": "base64-row-version"
}
```

**Response 400 BadRequest**: Item not `Approved` or integration not configured.  
**Response 422 UnprocessableEntity**: Integration status is `NeedsReconnect` or `InvalidConfig`.

---

### Bulk Sync Action Items

```
POST /api/organizations/{orgId:guid}/meetings/{meetingId:guid}/action-items/sync-all
```

**Auth**: Host, CoHost, or Org Admin.  
**Request Body**:
```json
{
  "actionItemIds": ["guid", "guid", "guid"]
}
```

**Response 207 Multi-Status**:
```json
{
  "integrationStatus": "Active|NeedsReconnect|InvalidConfig",
  "results": [
    {
      "actionItemId": "guid",
      "status": "Synced|SyncedNoAssignee|PendingRetry|Failed",
      "externalTaskId": "string|null",
      "externalTaskUrl": "string|null",
      "syncMissingAssigneeReason": "string|null",
      "errorMessage": "string|null"
    }
  ]
}
```

> **Note**: `PendingRetry` indicates transient provider failure; the system will auto-retry. `Failed` indicates non-retryable error (integration marked unhealthy).

---

## User Integration Endpoints

### Get My Connections

```
GET /api/users/me/integrations
```

**Auth**: Any authenticated user.

**Response 200 OK**:
```json
{
  "integrations": [
    {
      "provider": "Trello",
      "isConnected": true,
      "externalUsername": "ahmedtrello"
    }
  ]
}
```

---

### Connect Provider (User)

```
POST /api/users/me/integrations/{provider}/connect
```

**Auth**: Any authenticated user.

**Request Body** (provider-specific; Trello example):
```json
{
  "token": "trello-personal-token-string"
}
```

**Response 204 NoContent**: Connection saved.  
**Response 401 Unauthorized**: Invalid provider token.

---

### Disconnect Provider (User)

```
DELETE /api/users/me/integrations/{provider}/connect
```

**Auth**: Any authenticated user.

**Response 204 NoContent**: Connection removed.

---

## Organization Admin Settings Endpoints

### Get Integration Config

```
GET /api/organizations/{orgId:guid}/integrations/{provider}
```

**Auth**: Org Admin.

**Response 200 OK**:
```json
{
  "isConfigured": true,
  "integrationStatus": "Active|NeedsReconnect|InvalidConfig|Disabled",
  "projectId": "string|null",
  "projectName": "string|null",
  "listId": "string|null",
  "listName": "string|null"
}
```

> **Security**: Does NOT return encrypted tokens.

---

### Save Integration Config

```
PUT /api/organizations/{orgId:guid}/integrations/{provider}
```

**Auth**: Org Admin.

**Request Body** (provider-specific; Trello example):
```json
{
  "apiKey": "trello-api-key",
  "apiToken": "trello-api-token",
  "projectId": "board-id",
  "listId": "list-id"
}
```

**Response 204 NoContent**: Settings saved and validated.  
**Response 401 Unauthorized**: Invalid credentials.  
**Response 404 NotFound**: Project or list not found / not accessible.

---

### List Provider Projects

```
GET /api/organizations/{orgId:guid}/integrations/{provider}/projects
```

**Auth**: Org Admin (requires saved credentials).

**Response 200 OK**:
```json
{
  "projects": [
    {
      "id": "project-id",
      "name": "Engineering Board"
    }
  ]
}
```

---

### List Provider Lists

```
GET /api/organizations/{orgId:guid}/integrations/{provider}/projects/{projectId}/lists
```

**Auth**: Org Admin (requires saved credentials).

**Response 200 OK**:
```json
{
  "lists": [
    {
      "id": "list-id",
      "name": "To Do"
    }
  ]
}
```

---

### Get Member Provider Status

```
GET /api/organizations/{orgId:guid}/integrations/{provider}/members-status
```

**Auth**: Org Admin.

**Response 200 OK**:
```json
{
  "members": [
    {
      "userId": "guid",
      "displayName": "Ahmed Hassan",
      "isConnected": true,
      "externalUsername": "ahmedtrello",
      "adminMappedMemberId": "string|null"
    }
  ]
}
```

---

### Set Member Mapping (Admin Override)

```
PUT /api/organizations/{orgId:guid}/integrations/{provider}/members/{userId:guid}/mapping
```

**Auth**: Org Admin.

**Request Body**:
```json
{
  "externalMemberId": "external-member-id"
}
```

**Response 204 NoContent**: Mapping saved.  
**Response 404 NotFound**: External member ID not found on configured project.

---

## Internal Jobs (Hangfire)

These are not HTTP endpoints but are part of the contract surface.

### ExtractActionItemsJob

**Trigger**: Enqueued by `MeetingTranscriptReadyEvent` handler.  
**Input**: `meetingId`, `organizationId`  
**Behavior**:
1. Check if action items already exist for meeting → abort if yes.
2. Fetch transcript and participant roster.
3. Call `ILLMService.CompleteWithJsonAsync` with roster-injected prompt.
4. Parse JSON, validate structure.
5. Persist `ActionItem` rows with `Status = PendingReview`.

**Retry**: Hangfire automatic retry (3 attempts).  
**Idempotency**: Guarded by existence check.

---

### SyncActionItemsToProviderJob

**Trigger**: Enqueued by sync endpoints.  
**Input**: `meetingId`, `organizationId`, `actionItemIds[]`  
**Behavior**:
1. Load `OrganizationIntegrationConfig` for the org's active provider.
2. Fetch provider-specific project members (cached for 5 min).
3. For each action item:
   - Resolve assignee via `ExternalAccountLink` → `ExternalMemberMapping`.
   - Validate assignee is in project member list.
   - Call `ITaskProvider.CreateTaskAsync`.
   - Update `ActionItem` status and external references.
4. On 401/404: mark integration unhealthy, stop job.
5. On transient errors: retry up to 3× with exponential backoff.

**Retry**: Hangfire automatic retry for unhandled exceptions.  
**Idempotency**: Skip items where `ExternalTaskId` is already set.
