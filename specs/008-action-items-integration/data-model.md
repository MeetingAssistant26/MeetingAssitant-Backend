# Data Model: Action Items & External Task Provider Integration

## Entity Relationship Diagram

```
┌─────────────────────┐     ┌──────────────────────┐     ┌─────────────────────────────┐
│   Organization      │◄────┤ OrganizationIntegration│     │ OrganizationIntegrationConfig│
│   (existing)        │     │   (new)              │◄────┤   (new)                     │
└─────────────────────┘     └──────────────────────┘     └─────────────────────────────┘
         ▲                           ▲
         │                           │
         │                    ┌──────┴──────┐
         │                    │             │
┌────────┴────────┐    ┌─────┴──────┐  ┌───┴──────────┐
│   Meeting       │    │ ActionItem │  │ ExternalAccountLink│
│   (existing)    │◄───┤   (new)    │  │   (new)            │
└─────────────────┘    └────────────┘  └────────────────────┘
         ▲
         │
┌────────┴────────┐
│ MeetingParticipant│
│   (existing)    │
└─────────────────┘
```

## New Entities

### ActionItem

Stores tasks extracted from meeting transcripts.

| Field | Type | Constraints | Description |
|-------|------|-------------|-------------|
| Id | Guid | PK | Unique identifier |
| OrganizationId | Guid | FK → Organization, NOT NULL, index | Tenant isolation |
| MeetingId | Guid | FK → Meeting, NOT NULL, index | Source meeting |
| Title | string (200) | NOT NULL | Task title |
| Description | string (2000) | NULL | Task details |
| AssignedToParticipantId | Guid | FK → MeetingParticipant, NULL | Matched participant from roster |
| AssignedToUserId | Guid | FK → ApplicationUser, NULL | Platform user (null for guests) |
| DueDateUtc | DateTime | NULL | Optional due date |
| Status | ActionItemStatus | NOT NULL, default PendingReview | Lifecycle state |
| ExternalTaskId | string (50) | NULL | External provider task ID |
| ExternalTaskUrl | string (500) | NULL | Deep link to external task |
| ExternalProvider | ExternalProvider | NULL | Provider that created the task |
| SyncMissingAssigneeReason | string (50) | NULL | Why assignee was skipped |
| ExtractedAtUtc | DateTime | NOT NULL | When extraction completed |
| SyncedAtUtc | DateTime | NULL | When sync completed |
| RowVersion | byte[] | RowVersion (concurrency token) | Optimistic concurrency |
| CreatedAtUtc | DateTime | NOT NULL | Audit timestamp |
| UpdatedAtUtc | DateTime | NOT NULL | Audit timestamp |

**Indexes**:
- `(MeetingId, Status)` — for review panel queries
- `(OrganizationId, Status)` — for tenant-scoped listing
- `(AssignedToUserId, Status)` — for "my action items" queries

**State Transitions**:
```
PendingReview ──[approve]──► Approved ──[sync]──► Synced
PendingReview ──[reject]──► Rejected
Approved ──[undo]──► PendingReview
Rejected ──[undo]──► PendingReview
Approved ──[sync, no assignee]──► SyncedNoAssignee
```

> **Rule**: `Synced` and `SyncedNoAssignee` are terminal. No further edits allowed.

---

### OrganizationIntegration

Tracks enabled external integrations per organization.

| Field | Type | Constraints | Description |
|-------|------|-------------|-------------|
| Id | Guid | PK | Unique identifier |
| OrganizationId | Guid | FK → Organization, NOT NULL, UNIQUE | One integration record per org per type |
| Type | ExternalProvider | NOT NULL, default Trello | Integration provider |
| Status | IntegrationStatus | NOT NULL, default Disabled | Connection health |
| CreatedAtUtc | DateTime | NOT NULL | Audit timestamp |
| UpdatedAtUtc | DateTime | NOT NULL | Audit timestamp |

**IntegrationStatus enum**:
- `Active` — working normally
- `NeedsReconnect` — credentials invalid (401)
- `InvalidConfig` — project/list not found (404)
- `Disabled` — not configured or explicitly turned off

---

### OrganizationIntegrationConfig

Organization-level external provider destination settings. Uses an encrypted JSON payload for provider-specific configuration, enabling new providers without schema changes.

| Field | Type | Constraints | Description |
|-------|------|-------------|-------------|
| Id | Guid | PK | Unique identifier |
| OrganizationId | Guid | FK → Organization, NOT NULL, UNIQUE | Tenant isolation |
| Provider | ExternalProvider | NOT NULL | Provider type (Trello, ClickUp, etc.) |
| SelectedProjectId | string (50) | NOT NULL | External project ID (board, space, etc.) |
| SelectedListId | string (50) | NOT NULL | External list ID (list, status, etc.) |
| EncryptedProviderPayload | string (2000) | NOT NULL | Encrypted JSON containing provider-specific credentials and settings |
| CreatedAtUtc | DateTime | NOT NULL | Audit timestamp |
| UpdatedAtUtc | DateTime | NOT NULL | Audit timestamp |

**Unique Constraint**: `(OrganizationId, Provider)` — one config per org per provider.

> **Encryption**: `EncryptedProviderPayload` is encrypted using ASP.NET Core Data Protection (`IDataProtector`). The plaintext JSON structure is provider-specific. For Trello, it contains `{ apiKey, apiToken }`. For future ClickUp, it might contain `{ apiToken, teamId }`.

---

### ExternalAccountLink

Per-user, per-organization connection to external providers.

| Field | Type | Constraints | Description |
|-------|------|-------------|-------------|
| Id | Guid | PK | Unique identifier |
| UserId | Guid | FK → ApplicationUser, NOT NULL | Platform user |
| OrganizationId | Guid | FK → Organization, NOT NULL | Tenant scope |
| Provider | ExternalProvider | NOT NULL | Provider type |
| ExternalUserId | string (50) | NOT NULL | External member ID |
| ExternalUsername | string (100) | NULL | External username (for display) |
| AccessTokenProtected | string (500) | NOT NULL | Encrypted personal token |
| CreatedAtUtc | DateTime | NOT NULL | Audit timestamp |
| UpdatedAtUtc | DateTime | NOT NULL | Audit timestamp |

**Unique Constraint**: `(UserId, OrganizationId, Provider)` — one link per user per org per provider.

---

### ExternalMemberMapping

Explicit admin-defined mapping between a platform user and an external member ID for a given organization and provider.

| Field | Type | Constraints | Description |
|-------|------|-------------|-------------|
| Id | Guid | PK | Unique identifier |
| OrganizationId | Guid | FK → Organization, NOT NULL | Tenant scope |
| UserId | Guid | FK → ApplicationUser, NOT NULL | Platform user |
| Provider | ExternalProvider | NOT NULL | Provider type |
| ExternalMemberId | string (50) | NOT NULL | External member ID |
| CreatedAtUtc | DateTime | NOT NULL | Audit timestamp |
| UpdatedAtUtc | DateTime | NOT NULL | Audit timestamp |

**Unique Constraint**: `(OrganizationId, UserId, Provider)` — one mapping per user per org per provider.

> **Resolution priority during sync**: `ExternalAccountLink` (self-connected) is checked first. If absent, fallback to `ExternalMemberMapping` (admin-defined). If both absent, task created without assignee.

## Entity Relationships

| From | To | Cardinality | Notes |
|------|-----|-------------|-------|
| ActionItem | Organization | N:1 | Tenant isolation |
| ActionItem | Meeting | N:1 | Source meeting |
| ActionItem | MeetingParticipant | N:0..1 | Optional assignee participant |
| ActionItem | ApplicationUser | N:0..1 | Optional platform user assignee |
| OrganizationIntegration | Organization | 1:1 per type | One integration config per org |
| OrganizationIntegrationConfig | Organization | 1:1 per provider | One config per org per provider |
| ExternalAccountLink | ApplicationUser | N:1 | User can have links to multiple providers/orgs |
| ExternalAccountLink | Organization | N:1 | Scoped to org |
| ExternalMemberMapping | Organization | N:1 | Many mappings per org |
| ExternalMemberMapping | ApplicationUser | N:1 | One mapping per user per org per provider |

## Validation Rules

1. **ActionItem.Title** — required, max 200 characters.
2. **ActionItem.Status** — must be valid enum value; terminal states (`Synced`, `SyncedNoAssignee`) block updates.
3. **OrganizationIntegrationConfig.SelectedProjectId/SelectedListId** — required when integration is Active.
4. **ExternalAccountLink.AccessTokenProtected** — must be non-empty encrypted value.
5. **ExternalMemberMapping.ExternalMemberId** — required, format validated by provider-specific rules.
