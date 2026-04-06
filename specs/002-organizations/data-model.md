# Phase 1: Data Model

## Conceptual Entities

### Organization
The root tenant entity.
*   `Id` (Guid, PK)
*   `Name` (string, max 200)
*   `Slug` (string, max 100, unique index, URL safe)
*   `CreatedAtUtc` (timestampz)
*   `UpdatedAtUtc` (timestampz - from BaseEntity)

### UserOrgMembership
Represents a user's role and status within an organization.
*   `Id` (Guid, PK)
*   `UserId` (Guid, FK to User)
*   `OrganizationId` (Guid, FK to Organization)
*   `OrgRole` (enum: Admin=0, Member=1, Guest=2)
*   `JobRole` (string, nullable - title like 'Software Engineer')
*   `Context` (string, max 2000 nullable - AI context on what the user does)
*   `ContextStatus` (string, nullable)
*   `IsEnabled` (bool, default true)
*   `CreatedAtUtc` (timestampz)
*   `UpdatedAtUtc` (timestampz)

**Indexes/Constraints:**
*   Partial Unique Index: `(UserId) WHERE IsEnabled = true` - Enforces a single active organization per user.
*   Index on `OrganizationId` for quick membership/tenant retrieval.

### Invitation
A time-bound token to add members.
*   `Id` (Guid, PK)
*   `OrganizationId` (Guid, FK to Organization)
*   `InvitedByUserId` (Guid, FK to User)
*   `Token` (string, unique index constraint, e.g., URL safe cryptographically secure token)
*   `EmailWhitelist` (JSONB string array)
*   `ExpiresAtUtc` (timestampz)
*   `CreatedAtUtc` (timestampz)
*   `UpdatedAtUtc` (timestampz)

### MeetingTag
Organization-defined labels for categorizing meetings. Belongs to an organization, referenced by meetings (many-to-many relationship via MeetingTags junction table in Phase 3).

*   `Id` (Guid, PK)
*   `OrganizationId` (Guid, FK to Organization)
*   `Name` (string, max 50) — display name of the tag
*   `Color` (string, max 7, nullable) — hex color code (e.g., "#FF5733")
*   `IsActive` (bool, default true) — soft delete flag
*   `CreatedAtUtc` (timestampz)
*   `UpdatedAtUtc` (timestampz)

**Indexes/Constraints:**
*   Partial Unique Index: `(OrganizationId, LOWER(Name)) WHERE IsActive = true` — case-insensitive uniqueness per org
*   Index on `OrganizationId` for quick tag retrieval

**Notes:**
*   Color is optional (nullable). When not provided, UI shows default color.
*   Tags are ordered by `CreatedAtUtc ASC` (oldest first) when listed.
*   Soft delete via `IsActive = false` — deleted tags remain in DB for referential integrity with existing meetings.
*   Reactivation not supported; create new tag instead.

## State Transitions
*   **UserOrgMembership.IsEnabled**: `true` -> `false` via Leave Organization flow. (Non-reversible through this flow; user must be invited again).
*   **UserOrgMembership.OrgRole**: `Member` <-> `Admin`. Condition: Cannot set from `Admin` -> `Member` if this record is the *last* admin for the specific `OrganizationId`.
*   **MeetingTag.IsActive**: `true` -> `false` via soft delete (admin action). Non-reversible; create new tag if needed.

## Validation Rules
*   **Organization.Slug**: Regex validation `^[a-z0-9-]+$`, max 100 chars. Auto-generated appending `-N` if duplicate.
*   **UserOrgMembership.Context**: Max length 2000 characters. Admin or target User permitted to edit.
*   **Invitation.EmailWhitelist**: At least 1 item; all items must pass valid email regex.
*   **Invitation.ExpiresAtUtc**: Must be >= UtcNow.
*   **MeetingTag.Name**: Required, 1-50 chars. Unique per organization (case-insensitive comparison).
*   **MeetingTag.Color**: Optional. If provided, must match hex format `^#[0-9A-Fa-f]{6}$`.
