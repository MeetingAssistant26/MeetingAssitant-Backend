# Feature Specification: Organizations, Membership & Invitations

**Feature Branch**: `002-organizations`  
**Created**: 2026-03-14  
**Status**: Draft  
**Input**: User description: "create the spec for phase 2 in implementation-plan.md"

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Admin Creates an Organization (Priority: P1)

An authenticated user who registered via Scenario A (register + create org) already has an organization created during registration. However, this story covers the standalone organization creation endpoint for cases where an existing user with no active membership creates a new organization. The user provides an organization name, the system generates a URL-safe slug, creates the organization, creates a `UserOrgMembership` (org_role = Admin, is_enabled = true), and the user becomes the organization's administrator.

**Why this priority**: Organizations are the foundational tenant unit. Every org-scoped feature (meetings, tasks, recordings, AI pipelines) depends on an organization existing. Without this, no multi-tenant functionality works.

**Independent Test**: Can be fully tested by authenticating a user with no active membership, calling the create organization endpoint with a valid name, and verifying the organization is created, a membership record exists with Admin role, and the slug is unique and URL-safe.

**Acceptance Scenarios**:

1. **Given** an authenticated user with no active `UserOrgMembership`, **When** they submit a create organization request with a valid name, **Then** the organization is created with a generated slug, a `UserOrgMembership` is created with `org_role = Admin` and `is_enabled = true`, and the organization details are returned.
2. **Given** an authenticated user with an existing active `UserOrgMembership`, **When** they attempt to create a new organization, **Then** the system rejects the request with error: "You already belong to an organization."
3. **Given** an organization name that would produce a slug collision with an existing organization, **When** the creation request is submitted, **Then** the system generates a unique slug by appending a numeric suffix (e.g., `my-org-2`).
4. **Given** an organization name with special characters, **When** the creation request is submitted, **Then** the system generates a URL-safe slug by replacing non-alphanumeric characters with hyphens and lowercasing.

---

### User Story 2 - Admin Lists Organization Members (Priority: P1)

An organization administrator views the list of all members in their organization. The list displays each member's name, email, organization role, job role, and context summary. This enables the admin to understand who is in the organization and what roles they hold.

**Why this priority**: Member visibility is required for role management, invitation decisions, and understanding who participates in meetings. It is a prerequisite for all member management operations.

**Independent Test**: Can be fully tested by creating an organization with multiple members, authenticating as an admin, calling the list members endpoint, and verifying all members are returned with their correct roles and metadata.

**Acceptance Scenarios**:

1. **Given** an organization with 3 members (1 Admin, 2 Members), **When** the admin requests the member list, **Then** all 3 members are returned with their `org_role`, `job_role`, `context`, and `is_enabled` status.
2. **Given** two organizations (Org A and Org B), **When** an admin of Org A requests the member list, **Then** only Org A's members are returned — zero members from Org B are included (tenant isolation).
3. **Given** a member requests the member list (not an admin), **When** they call the endpoint, **Then** the list is returned (read access is available to all members of the organization).
4. **Given** a user who is not a member of the organization, **When** they request the member list, **Then** the system returns a 403 Forbidden response.

---

### User Story 3 - Admin Updates a Member's Role (Priority: P1)

An organization administrator changes a member's organization role (e.g., promoting a Member to Admin, or demoting an Admin to Member). The role change takes effect immediately and is reflected in subsequent JWT tokens issued during login or token refresh.

**Why this priority**: Role management is essential for organizational governance. Admins must be able to delegate administrative responsibilities and control access levels.

**Independent Test**: Can be fully tested by creating an organization with an admin and a member, updating the member's role via the endpoint, and verifying the role change is persisted and reflected in the member list.

**Acceptance Scenarios**:

1. **Given** an admin of the organization, **When** they update a member's role from `Member` to `Admin`, **Then** the member's `org_role` is changed to `Admin` and a `RoleChangedEvent` is emitted.
2. **Given** an admin of the organization, **When** they attempt to change their own role, **Then** the system rejects the request — an admin cannot demote themselves (at least one admin must exist).
3. **Given** a non-admin member of the organization, **When** they attempt to update another member's role, **Then** the system returns a 403 Forbidden response.
4. **Given** a role change from `Admin` to `Member`, **When** the organization would be left with zero admins, **Then** the system rejects the request with error: "Organization must have at least one admin."

---

### User Story 4 - Admin or Member Updates Member Context (Priority: P2)

A member (or an admin on their behalf) updates the **Member Context** field — a human-managed description of the member's organizational role, expertise, and responsibilities. This context is injected into LLM prompts during AI task extraction (Phase 6) so the model can suggest appropriate assignees.

**Why this priority**: Member Context is required for the AI pipeline's assignee suggestion feature. Without it, the LLM has no organizational knowledge to base assignment decisions on. However, the system is functional without it — tasks are still extracted, just without assignee suggestions.

**Independent Test**: Can be fully tested by authenticating as a member, updating the context field with descriptive text, and verifying it is persisted and retrievable via the member list endpoint.

**Acceptance Scenarios**:

1. **Given** an authenticated member, **When** they update their own context to "Backend engineer specializing in .NET and PostgreSQL", **Then** the context is saved and a `MemberContextUpdatedEvent` is emitted.
2. **Given** an admin, **When** they update another member's context, **Then** the context is saved (admins can manage all member contexts).
3. **Given** a non-admin member, **When** they attempt to update another member's context, **Then** the system returns a 403 Forbidden response.
4. **Given** a context field that exceeds the maximum length (2000 characters), **When** the update is submitted, **Then** the system rejects the request with a validation error.

---

### User Story 5 - Admin Creates an Invitation (Priority: P1)

An organization administrator creates an invitation to bring new members into the organization. The invitation includes a JSONB email whitelist (an array of allowed email addresses) and generates a unique token. The invitation has an expiration date (default 7 days). Only users whose email appears in the whitelist can accept the invitation.

**Why this priority**: Invitations are the only way to add members to an existing organization (registration Scenario B). Without invitations, organizations are limited to a single member (the creator).

**Independent Test**: Can be fully tested by authenticating as an admin, creating an invitation with an email whitelist, and verifying the invitation is created with a valid token and expiration date.

**Acceptance Scenarios**:

1. **Given** an admin of the organization, **When** they create an invitation with an email whitelist `["alice@example.com", "bob@example.com"]`, **Then** the invitation is created with a unique token, default 7-day expiration, and the whitelist is stored as JSONB.
2. **Given** a non-admin member, **When** they attempt to create an invitation, **Then** the system returns a 403 Forbidden response (only admins can invite).
3. **Given** an empty email whitelist, **When** the admin submits the invitation, **Then** the system rejects the request with a validation error: "Email whitelist must contain at least one address."
4. **Given** an email whitelist with malformed emails, **When** the admin submits the invitation, **Then** the system rejects the request with validation errors for each malformed address.
5. **Given** an active invitation, **When** an admin revokes it, **Then** the invitation `RevokedAtUtc` is set to the current time, preventing any further use.

---

### User Story 6 - User Joins an Organization via Invitation (Priority: P1)

A registered user (or a user registering via Scenario B) accepts an invitation by providing the invitation token. The system validates the token, checks the email whitelist, and — if valid — creates a `UserOrgMembership` with `org_role = Member`. Users who already belong to an organization are rejected.

**Why this priority**: This is the counterpart to invitation creation. Without the join flow, invitations are useless and organizations cannot grow beyond the initial admin.

**Independent Test**: Can be fully tested by creating an invitation, registering a user with a whitelisted email, submitting the join request with the invitation token, and verifying the membership is created.

**Acceptance Scenarios**:

1. **Given** a valid invitation token and a user whose email is in the whitelist, **When** the user submits the join request, **Then** a `UserOrgMembership` is created with `org_role = Member` and `is_enabled = true`, and a `MemberJoinedEvent` is emitted.
2. **Given** a valid invitation token but a user whose email is NOT in the whitelist, **When** the user submits the join request, **Then** the system rejects the request with error: "Your email is not authorized for this invitation."
3. **Given** a user who already has an active `UserOrgMembership`, **When** they attempt to join via invitation, **Then** the system rejects the request with error: "You already belong to an organization."
4. **Given** an expired invitation token (older than 7 days), **When** the user submits the join request, **Then** the system rejects the request with error: "This invitation has expired."
5. **Given** an invitation token that does not exist, **When** the user submits the join request, **Then** the system rejects the request with error: "Invalid invitation token."
6. **Given** an invitation token that has been revoked (`RevokedAtUtc` is populated), **When** the user submits the join request, **Then** the system rejects the request with error: "This invitation has been revoked."

---

### User Story 7 - Member Leaves an Organization (Priority: P2)

A member voluntarily leaves their organization. The system deactivates their `UserOrgMembership` by setting `is_enabled = false`. The user's JWT remains valid until its natural expiry (15 minutes) but cannot access org-scoped resources. Token refresh will fail after leaving. The last remaining admin cannot leave the organization.

**Why this priority**: Leave flow is necessary for organizational lifecycle management but is not required for core meeting functionality. Users must be able to leave before joining a different organization (single-membership constraint).

**Independent Test**: Can be fully tested by authenticating as a member, calling the leave endpoint, and verifying the membership is deactivated and token refresh fails.

**Acceptance Scenarios**:

1. **Given** a member of the organization, **When** they submit a leave request, **Then** their `UserOrgMembership` is set to `is_enabled = false` and a `MemberLeftEvent` is emitted.
2. **Given** the only admin of the organization, **When** they attempt to leave, **Then** the system rejects the request with error: "Cannot leave — you are the last admin. Transfer admin role first."
3. **Given** a member who has just left, **When** they attempt to refresh their JWT, **Then** token refresh fails because the active membership check fails.
4. **Given** a member who has left Org A, **When** they join Org B via invitation, **Then** the membership is created successfully (the single-membership constraint is satisfied because the Org A membership is deactivated).

---

### User Story 8 - Admin Creates a Meeting Tag (Priority: P1)

An organization administrator creates a reusable tag for categorizing meetings within the organization. Tags are defined at the organization level and used when creating meetings (Phase 3). Tags support optional color customization for visual identification.

**Why this priority**: Meeting tags are required for the meeting creation and categorization features. Without a tag pool defined by admins, meetings cannot be tagged, and AI features (context retrieval by tag) cannot function properly.

**Independent Test**: Can be fully tested by authenticating as an admin, calling the create tag endpoint with name and optional color, and verifying the tag appears in the organization's tag list.

**Acceptance Scenarios**:

1. **Given** an admin, **When** they create a tag "Sprint Planning" with color "#4CAF50", **Then** the tag is created with the provided color and appears in the organization's tag list.
2. **Given** an admin, **When** they create a tag "Standup" without providing a color, **Then** the tag is created with `color = null` and appears in the list (UI shows default color).
3. **Given** an admin, **When** they attempt to create a duplicate tag name (case-insensitive: "sprint planning" vs "Sprint Planning"), **Then** the system rejects with 409 Conflict.
4. **Given** an admin, **When** they attempt to create a tag with an invalid color format (e.g., "red" instead of "#FF0000"), **Then** the system returns 400 Validation error.
5. **Given** a member (non-admin), **When** they attempt to create a tag, **Then** the system returns 403 Forbidden.

---

### User Story 9 - Admin Updates a Meeting Tag (Priority: P1)

An admin updates an existing tag's name and/or color. The update is partial — only provided fields are modified.

**Why this priority**: Admins need the ability to rename tags (fix typos, improve clarity) and adjust colors for better visual organization.

**Independent Test**: Can be tested by creating a tag, then calling update with only name, only color, or both, and verifying the changes.

**Acceptance Scenarios**:

1. **Given** an admin and existing tag "Standup" with color "#FF0000", **When** they update only the name to "Daily Standup", **Then** the name changes but color remains "#FF0000".
2. **Given** an admin and existing tag "Planning", **When** they update only the color to "#4CAF50", **Then** the color changes but name remains unchanged.
3. **Given** an admin and existing tag with color, **When** they send `color: null`, **Then** the color is cleared (set to null) and UI shows default color.
4. **Given** an admin, **When** they update to a duplicate name (excluding the current tag), **Then** 409 Conflict is returned.
5. **Given** a member, **When** they attempt to update a tag, **Then** 403 Forbidden.
6. **Given** an admin, **When** they update a soft-deleted tag (by ID), **Then** 404 Not Found is returned (cannot modify deleted tags).

---

### User Story 10 - Admin Deletes a Meeting Tag (Priority: P2)

An admin soft-deletes a tag, removing it from the pool of available tags for future meetings. Existing meetings referencing this tag retain the reference for historical purposes.

**Why this priority**: Organization needs change over time — obsolete tags need to be removable without breaking historical meeting data. Soft delete preserves referential integrity.

**Independent Test**: Can be tested by creating a tag, deleting it (soft delete), verifying it disappears from the list, then attempting to create a new tag with the same name (should succeed).

**Acceptance Scenarios**:

1. **Given** an admin and existing tag "Deprecated", **When** they delete the tag, **Then** `IsActive` is set to false, the tag no longer appears in `GET /meeting-tags`, and the tag name becomes available for reuse.
2. **Given** a meeting was created with tag "Deprecated", **When** the tag is soft-deleted, **Then** the meeting still shows "Deprecated" tag in its history (referential integrity preserved via junction table).
3. **Given** an admin, **When** they delete a tag and then create a new tag with the same name, **Then** the creation succeeds (unique constraint is `WHERE IsActive = true`).
4. **Given** a member, **When** they attempt to delete a tag, **Then** 403 Forbidden.
5. **Given** an admin, **When** they attempt to delete an already-deleted tag, **Then** 404 Not Found is returned.

---

### User Story 11 - Member Lists Meeting Tags (Priority: P1)

Any organization member can view the list of active meeting tags. This is needed when creating meetings (to select tags from the pool) and for understanding organization topic categories.

**Why this priority**: Members need to see available tags when creating meetings (Phase 3). Read access should be available to all organization members.

**Independent Test**: Can be tested by authenticating as any member (Admin, Member, or Guest) and calling the list tags endpoint.

**Acceptance Scenarios**:

1. **Given** an organization with 5 active tags ("Engineering", "Design", "Planning", "Review", "Retro"), **When** any member requests the tag list, **Then** all 5 active tags are returned ordered by `CreatedAtUtc ASC` (oldest first).
2. **Given** an organization with 3 active tags and 2 soft-deleted tags, **When** a member requests the tag list, **Then** only the 3 active tags are returned (deleted tags excluded).
3. **Given** a user who is not a member of the organization, **When** they request the tag list, **Then** the system returns 403 Forbidden (tenant isolation).
4. **Given** a newly created tag, **When** the list is retrieved, **Then** the new tag appears at the end of the list (chronological ordering).

---

### Edge Cases

- What happens when two admins try to create invitations with overlapping email whitelists simultaneously? → **Resolved**: Each invitation is independent. A user can only accept one invitation (single-membership constraint enforced at join time by DB unique constraint).
- What happens when the last admin demotes themselves via a race condition (two admins demoting each other simultaneously)? → **Resolved**: Transaction-level check — the role update query verifies at least one admin remains in the same transaction. If violated, the transaction rolls back with an error.
- What happens when a deactivated member's email appears in a new invitation whitelist? → **Resolved**: The deactivated member can accept the new invitation because `UNIQUE(user_id) WHERE is_enabled = true` only constrains active memberships. Their old membership remains as `is_enabled = false` for audit purposes.
- What happens if a user tries to join an organization via invitation while their JWT still contains the old `organizationId`? → **Resolved**: The join endpoint verifies the user has no active `UserOrgMembership` in the database (not the JWT). After joining, the user must re-authenticate to get a JWT with the new `organizationId`.
- What happens when an admin removes themselves and simultaneously another admin is leaving? → **Resolved**: Same transaction-level check as role demotion — at least one active admin must remain. Both operations cannot succeed if they would leave zero admins.
- How does the slug generation handle very long organization names? → **Resolved**: Slug is truncated to 100 characters maximum, with any trailing hyphens removed.
- Are revoked/expired invitation tokens physically deleted? → **Resolved**: No background cleanup job is required for this phase. Expired and revoked tokens remain in DB and are simply rejected via on-the-fly validation.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST allow authenticated users with no active `UserOrgMembership` to create an organization by providing a name. The system MUST generate a unique URL-safe slug from the name.
- **FR-002**: System MUST create a `UserOrgMembership` with `org_role = Admin` and `is_enabled = true` when a user creates an organization.
- **FR-003**: System MUST reject organization creation if the user already has an active `UserOrgMembership`. Error: "You already belong to an organization."
- **FR-004**: System MUST allow any member of an organization to list all members of their own organization, including `org_role`, `job_role`, `context`, `context_status`, and `is_enabled` fields.
- **FR-005**: System MUST enforce tenant isolation on all organization-scoped queries — a user MUST NOT be able to see, modify, or interact with members, invitations, or data belonging to another organization.
- **FR-006**: System MUST allow admins to update a member's organization role (`Admin`, `Member`, `Guest`). The system MUST reject role changes that would leave the organization with zero admins.
- **FR-007**: System MUST allow members to update their own Member Context field. Admins MAY update any member's context. Non-admin members MUST NOT update other members' context.
- **FR-008**: System MUST validate the Member Context field does not exceed 2000 characters.
- **FR-009**: System MUST allow admins to create invitations with a JSONB email whitelist (array of allowed emails) and a unique token. Default expiration: 7 days from creation.
- **FR-010**: System MUST validate that the invitation email whitelist contains at least one well-formed email address.
- **FR-011**: System MUST allow users to join an organization by providing a valid, non-expired invitation token. The user's email MUST appear in the invitation's email whitelist.
- **FR-012**: System MUST reject join requests from users who already have an active `UserOrgMembership`. Error: "You already belong to an organization."
- **FR-013**: System MUST create a `UserOrgMembership` with `org_role = Member` and `is_enabled = true` when a user successfully joins via invitation.
- **FR-014**: System MUST reject join requests with expired or revoked invitation tokens. Error: "This invitation has expired or been revoked."
- **FR-015**: System MUST reject join requests where the user's email is not in the invitation's email whitelist. Error: "Your email is not authorized for this invitation."
- **FR-016**: System MUST allow members to leave their organization by deactivating their `UserOrgMembership` (`is_enabled = false`). The system MUST reject leave requests from the last remaining admin.
- **FR-016b**: System MUST allow Admin users to explicitly revoke/cancel an active invitation before its intrinsic expiration.
- **FR-017**: System MUST emit domain events for all state changes: `OrganizationCreatedEvent`, `MemberJoinedEvent`, `RoleChangedEvent`, `MemberContextUpdatedEvent`, `MemberLeftEvent`, `InvitationRevokedEvent`, `MeetingTagCreatedEvent`, `MeetingTagUpdatedEvent`, `MeetingTagDeletedEvent`.
- **FR-018**: System MUST enforce the single-membership constraint via a database unique constraint: `UNIQUE(user_id) WHERE is_enabled = true` on the `UserOrgMembership` table.
- **FR-019**: System MUST scope all endpoints in this feature to the authenticated user's organization. Org ID from route parameters MUST match the JWT `organizationId` claim — mismatches MUST return 403 Forbidden.
- **FR-020**: System MUST implement organization-scoped authorization policies: `RequireOrgAdmin` (only Admin role), `RequireOrgMember` (Admin or Member), `RequireOrgAccess` (Admin, Member, or Guest).
- **FR-021**: System MUST use the Partial Controller Pattern for all endpoints — one endpoint per file via partial classes. Controller definition files contain `[ApiController]`, `[Route]`, base class, constructor, and shared dependencies. Endpoint files contain exactly one action method.
- **FR-022**: System MUST use `result.ToProblem(correlationIdProvider)` for all endpoint error responses. Endpoints MUST NOT manually construct `StandardErrorResponse`.
- **FR-023**: System MUST allow Organization Admins to create meeting tags with a name (1-50 chars) and optional color (hex format #RGB or #RRGGBB, with or without a leading #). Name MUST be unique per organization (case-insensitive, unique constraint `WHERE IsActive = true`).
- **FR-024**: System MUST reject duplicate tag names within the same organization with 409 Conflict. Error code: `MeetingTag.DuplicateName`.
- **FR-025**: System MUST allow Organization Admins to update meeting tags via partial update (PUT) — only provided fields (name and/or color) are modified. Name uniqueness constraint applies excluding the current tag.
- **FR-026**: System MUST allow Organization Admins to soft-delete tags (set `IsActive = false`). Deleted tags MUST NOT appear in list endpoints. Deleted tag names MUST be available for reuse (unique constraint excludes inactive tags).
- **FR-027**: System MUST allow any organization member (Admin, Member, or Guest) to list active meeting tags. List MUST be ordered by `CreatedAtUtc ASC` (oldest first).
- **FR-028**: System MUST validate color format using regex `^#?(?:[0-9A-Fa-f]{3}|[0-9A-Fa-f]{6})$` when color is provided. Setting color to `null` MUST clear the color.
- **FR-029**: System MUST emit `MeetingTagCreatedEvent`, `MeetingTagUpdatedEvent`, and `MeetingTagDeletedEvent` domain events for tag state changes.

### Key Entities

- **Organization**: Represents a tenant/workspace. Key attributes: unique identifier (`Id`), display name (`Name`), URL-safe identifier (`Slug`, unique), creation timestamp (`CreatedAtUtc`). Does NOT implement `IHasOrganizationId` — it IS the tenant.
- **UserOrgMembership**: Represents a user's membership in an organization. Key attributes: `UserId`, `OrganizationId`, `OrgRole` (Admin/Member/Guest enum), `JobRole` (text, nullable — job title), `Context` (text — Member Context for AI), `ContextStatus` (text, nullable), `IsEnabled` (bool, default true). Implements `IHasOrganizationId`. DB constraint: `UNIQUE(user_id) WHERE is_enabled = true`.
- **Invitation**: Represents a pending invitation to join an organization. Key attributes: `Id`, `OrganizationId`, `InvitedByUserId`, `EmailWhitelist` (JSONB array of strings), `Token` (unique string), `ExpiresAtUtc`, `CreatedAtUtc`, `RevokedAtUtc` (timestampz, nullable). Implements `IHasOrganizationId`.
- **MeetingTag**: Organization-level label for categorizing meetings. Key attributes: `Id`, `OrganizationId`, `Name` (text 1-50 chars), `Color` (hex string, nullable), `IsActive` (bool, default true), `CreatedAtUtc`, `UpdatedAtUtc`. DB constraint: unique `(organization_id, lower(name)) WHERE is_active = true`. Soft delete via `IsActive = false`. Referenced by meetings via many-to-many junction table (defined in Phase 3). Implements `IHasOrganizationId`.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Tenant isolation verified — queries for Org A data MUST return zero results from Org B, confirmed by integration tests with two seeded organizations.
- **SC-002**: Single-membership constraint enforced — attempting to create a second active `UserOrgMembership` for the same user MUST fail at the database level, confirmed by constraint violation test.
- **SC-003**: Organization creation completes in under 2 seconds (including slug generation and membership creation).
- **SC-004**: Member list endpoint returns results in under 300ms for organizations with up to 100 members.
- **SC-005**: Invitation token generation is cryptographically random and unique — zero collisions across 10,000 generated tokens in automated tests.
- **SC-006**: 100% of role change operations enforce the "at least one admin" invariant, verified by tests that attempt to remove/demote the last admin.
- **SC-007**: Leave organization flow correctly deactivates membership and causes subsequent token refresh to fail — confirmed by integration test.
- **SC-008**: All domain events (`OrganizationCreatedEvent`, `MemberJoinedEvent`, `RoleChangedEvent`, `MemberContextUpdatedEvent`, `MemberLeftEvent`, `InvitationRevokedEvent`) are emitted and logged with correlation IDs.
- **SC-009**: All endpoint error responses include `CorrelationId` and use `StandardErrorResponse` format via `result.ToProblem(correlationIdProvider)`.
- **SC-010**: All 3 controllers and 8 endpoint files follow the Partial Controller Pattern — verified by code review (no constructor/attributes/fields in endpoint files).
- **SC-011**: Meeting tag creation enforces name uniqueness at the database level — attempting to create a duplicate active tag name MUST fail at the DB level, confirmed by integration test with concurrent creation attempts.
- **SC-012**: Meeting tag soft delete correctly sets `IsActive = false` and excluded from list queries — verified by creating, deleting, and listing tags.
- **SC-013**: Deleted tag name becomes available for reuse — after soft delete, creating a tag with the same name MUST succeed, confirmed by integration test.
- **SC-014**: All MeetingTag endpoints enforce authorization — non-admins receive 403 Forbidden on create/update/delete, verified by integration tests with Member and Guest roles.
- **SC-015**: MeetingTag list endpoint returns results, ordered chronologically.

## Clarifications

### Session 2026-03-15
- Q: Should organization administrators be able to explicitly revoke or cancel an active invitation before it naturally expires? → A: Yes, allow admins to explicitly revoke/cancel an active invitation before it expires.
- Q: Are revoked/expired invitation tokens physically deleted from the database? → A: No. They remain in the database and are rejected via on-the-fly validation. No background cleanup job is required for this phase.

### Session 2026-03-14

- Q: Can a Member-role user view the organization's member list, or is it restricted to Admins? → A: All organization members can view the member list (read access). Only role changes and invitation creation are admin-restricted.
- Q: When a member leaves an organization, should their historical data (meeting participations, task assignments, context) be deleted? → A: No. The `UserOrgMembership` record is preserved with `is_enabled = false` for audit purposes. Historical references remain intact.
- Q: Should the `Organization.Slug` be editable after creation? → A: No. Slugs are immutable once created. If needed, the admin creates a new organization.
- Q: What authorization is required for the `JoinOrganization` endpoint? → A: The user must be authenticated (valid JWT) but does NOT need to belong to any organization. The endpoint validates the invitation token and email whitelist.

### Session 2026-04-06 (MeetingTag)
- Q: What happens when a tag is soft-deleted but meetings still reference it? → **Resolved**: Meetings retain the reference via the junction table. The tag name is preserved for historical context. If the same name is recreated, it's a new tag with a new ID — existing meetings keep the old tag reference.
- Q: Can a soft-deleted tag be reactivated? → **Resolved**: No. Reactivation is not supported. Admins should create a new tag if needed. This prevents confusion between old and new tag contexts.
- Q: What color format should be stored and returned? → **Resolved**: Always uppercase six-character hex with hash prefix (e.g., "#4CAF50"). API accepts any case, accepts optional leading hash, and expands shorthand (e.g., "#ccc" or "ccc" normalize to "#CCCCCC").
- Q: Should color be validated as a "real" color or just format? → **Resolved**: Only format validation (regex `^#?(?:[0-9A-Fa-f]{3}|[0-9A-Fa-f]{6})$`). No semantic validation (e.g., "#000000" is valid even if black).

## Assumptions

- Organization names must be non-empty and have a maximum length of 200 characters.
- Slug generation uses standard URL-safe rules: lowercase, hyphens for separators, no special characters, max 100 characters.
- The `OrganizationRole` enum has three values: `Admin` (0), `Member` (1), `Guest` (2). Guest role has read-only access.
- Invitation tokens are generated using a cryptographically secure random generator (e.g., `RandomNumberGenerator`) and are 32 characters long.
- The email whitelist comparison is case-insensitive (per RFC 5321 — local parts are technically case-sensitive, but practically treated as case-insensitive).
- Member Context (`UserOrgMembership.Context`) is plain text, not structured data. It is intended for human-readable descriptions like "Backend engineer" or "Responsible for UI/UX design."
- `ContextStatus` field is reserved for future use (e.g., tracking AI processing state of context). Not actively used in this phase.
- When a user leaves an organization and their JWT has not yet expired, org-scoped endpoints MUST still reject requests because the `AppDbContext` global query filter will no longer match (the membership is deactivated).
- `MemberLeftEvent` and `InvitationRevokedEvent` are additions to the implementation plan's domain event list — the plan lists `OrganizationCreatedEvent`, `MemberJoinedEvent`, `RoleChangedEvent`, `MemberContextUpdatedEvent` only. These two new events are required by the leave and revocation flows added in this spec.
- `RevokedAtUtc` on the `Invitation` entity is an addition to the implementation plan's entity definition. The plan defines `Id`, `OrganizationId`, `InvitedByUserId`, `EmailWhitelist`, `Token`, `ExpiresAtUtc`, `CreatedAtUtc` only. This field is required by the invitation revocation flow (FR-016b).
- **MeetingTag additions**: `MeetingTagCreatedEvent`, `MeetingTagUpdatedEvent`, and `MeetingTagDeletedEvent` are additions to the domain event list. The `Color` field is nullable and stores hex color codes (e.g., "#4CAF50"). Soft delete via `IsActive` follows the same pattern as `UserOrgMembership.IsEnabled`.
- **MeetingTag ordering**: Tags are always returned in chronological order by `CreatedAtUtc ASC` (oldest first). This provides a stable, predictable ordering without requiring an explicit "sort order" field.

## Dependencies

- **Phase 0 (Infrastructure & Foundation)**: Requires the completed foundation — `AppDbContext` with global query filter for `OrganizationId`, `BaseEntity`, `IHasOrganizationId`, domain event dispatcher (MediatR), correlation ID middleware, global error handling, `ResultExtensions.ToProblem()`, Hangfire configuration, JWT configuration, Mapster configuration, and Partial Controller Pattern conventions. **Validation architecture**: FluentValidation is the single source of truth (ASP.NET built-in model validation is disabled via `DisableBuiltInModelValidation`). SharpGrip auto-validation intercepts requests before controller actions. Validation errors are returned as `StandardErrorResponse` via a custom `ValidationResultFactory`. All validators must use `Cascade(CascadeMode.Stop)` to prevent redundant error messages per field. No reliance on ModelState.
- **Phase 1 (User Identity & Authentication)**: Requires `ApplicationUser` entity, JWT access/refresh tokens with `userId` and `organizationId` claims, authentication middleware, and the two registration flows (Scenario A creates org + membership in Phase 1; Scenario B creates membership via invitation which depends on this phase's invitation flow).
- **Constitution v1.4.0**: All multi-tenancy rules (§II) must be followed — `OrganizationId` on all org-scoped entities, global query filters, simplified membership model (one active org per user), DB unique constraint on `user_id WHERE is_enabled = true`. Endpoint architecture must follow the Partial Controller Pattern (§I). Error responses must use `result.ToProblem(correlationIdProvider)` (§I).

## Out of Scope

- Organization settings or configuration management (display preferences, feature flags)
- Organization deletion or archival
- Bulk member import
- Member removal by admin (deferred — only self-service leave is implemented)
- Organization transfer (transferring ownership to another user)
- Multi-organization membership (explicitly prohibited by architecture — single active membership constraint)
- Meeting-role authorization policies (handled in Phase 3/4)
- Email notifications for invitations (no email service in scope — invitations are token-based, shared out-of-band)
- Member profile pictures or avatars
- Organization billing, subscription, or tier management
- Deep analytics on member behavior or meeting metrics
- Fine-grained permission systems (custom roles beyond Admin/Member/Guest)
