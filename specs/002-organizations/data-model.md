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

## State Transitions
*   **UserOrgMembership.IsEnabled**: `true` -> `false` via Leave Organization flow. (Non-reversible through this flow; user must be invited again).
*   **UserOrgMembership.OrgRole**: `Member` <-> `Admin`. Condition: Cannot set from `Admin` -> `Member` if this record is the *last* admin for the specific `OrganizationId`.

## Validation Rules
*   **Organization.Slug**: Regex validation `^[a-z0-9-]+$`, max 100 chars. Auto-generated appending `-N` if duplicate.
*   **UserOrgMembership.Context**: Max length 2000 characters. Admin or target User permitted to edit.
*   **Invitation.EmailWhitelist**: At least 1 item; all items must pass valid email regex.
*   **Invitation.ExpiresAtUtc**: Must be >= UtcNow.
