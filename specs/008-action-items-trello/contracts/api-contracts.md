# API Contracts: Action Items & Trello Integration

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
      "trelloCardId": "string|null",
      "trelloCardUrl": "string|null",
      "trelloAssigneeMissingReason": "string|null",
      "extractedAtUtc": "2026-05-04T10:00:00Z",
      "syncedAtUtc": "2026-05-04T11:00:00Z|null"
    }
  ]
}
```

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
  "trelloCardId": "string|null",
  "trelloCardUrl": "string|null",
  "trelloAssigneeMissingReason": "string|null"
}
```

**Response 400 BadRequest**: Item not `Approved` or integration not configured.  
**Response 422 UnprocessableEntity**: Trello integration status is `NeedsReconnect` or `InvalidConfig`.

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
      "trelloCardId": "string|null",
      "trelloCardUrl": "string|null",
      "trelloAssigneeMissingReason": "string|null",
      "errorMessage": "string|null"
    }
  ]
}
```

> **Note**: `PendingRetry` indicates transient Trello failure; the system will auto-retry. `Failed` indicates non-retryable error (integration marked unhealthy).

---

## User Connection Endpoints

### Get My Connections

```
GET /api/users/me/connections
```

**Auth**: Any authenticated user.

**Response 200 OK**:
```json
{
  "trello": {
    "isConnected": true,
    "externalUsername": "ahmedtrello"
  }
}
```

---

### Connect Trello (User)

```
POST /api/users/me/connections/trello
```

**Auth**: Any authenticated user.

**Request Body**:
```json
{
  "token": "trello-personal-token-string"
}
```

**Response 204 NoContent**: Connection saved.  
**Response 401 Unauthorized**: Invalid Trello token.

---

### Disconnect Trello (User)

```
DELETE /api/users/me/connections/trello
```

**Auth**: Any authenticated user.

**Response 204 NoContent**: Connection removed.

---

## Organization Admin Settings Endpoints

### Get Trello Integration Settings

```
GET /api/organizations/{orgId:guid}/integrations/trello
```

**Auth**: Org Admin.

**Response 200 OK**:
```json
{
  "isConfigured": true,
  "integrationStatus": "Active|NeedsReconnect|InvalidConfig|Disabled",
  "boardId": "string|null",
  "boardName": "string|null",
  "listId": "string|null",
  "listName": "string|null"
}
```

> **Security**: Does NOT return encrypted tokens.

---

### Save Trello Integration Settings

```
PUT /api/organizations/{orgId:guid}/integrations/trello
```

**Auth**: Org Admin.

**Request Body**:
```json
{
  "apiKey": "trello-api-key",
  "apiToken": "trello-api-token",
  "boardId": "board-id",
  "listId": "list-id"
}
```

**Response 204 NoContent**: Settings saved and validated.  
**Response 401 Unauthorized**: Invalid API key or token.  
**Response 404 NotFound**: Board or list not found / not accessible.

---

### List Trello Boards

```
GET /api/organizations/{orgId:guid}/integrations/trello/boards
```

**Auth**: Org Admin (requires saved credentials).

**Response 200 OK**:
```json
{
  "boards": [
    {
      "id": "board-id",
      "name": "Engineering Board"
    }
  ]
}
```

---

### List Trello Lists

```
GET /api/organizations/{orgId:guid}/integrations/trello/boards/{boardId}/lists
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

### Get Member Trello Status

```
GET /api/organizations/{orgId:guid}/members/trello-status
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
      "trelloUsername": "ahmedtrello",
      "adminMappedMemberId": "string|null"
    }
  ]
}
```

---

### Set Member Trello Mapping (Admin Override)

```
PUT /api/organizations/{orgId:guid}/members/{userId:guid}/trello-mapping
```

**Auth**: Org Admin.

**Request Body**:
```json
{
  "trelloMemberId": "trello-member-id"
}
```

**Response 204 NoContent**: Mapping saved.  
**Response 404 NotFound**: Trello member ID not found on configured board.

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

### SyncActionItemsToTrelloJob

**Trigger**: Enqueued by sync endpoints.  
**Input**: `meetingId`, `organizationId`, `actionItemIds[]`  
**Behavior**:
1. Load `TrelloWorkspaceConfig`.
2. Fetch board members (cached for 5 min).
3. For each action item:
   - Resolve assignee via `ExternalAccountLink` → `TrelloMemberMapping`.
   - Validate assignee is in board member list.
   - Call Trello `POST /1/cards`.
   - Update `ActionItem` status and Trello references.
4. On 401/404: mark integration unhealthy, stop job.
5. On transient errors: retry up to 3× with exponential backoff.

**Retry**: Hangfire automatic retry for unhandled exceptions.  
**Idempotency**: Skip items where `TrelloCardId` is already set.
