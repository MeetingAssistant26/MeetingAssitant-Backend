# Tasks: Organizations, Membership & Invitations

**Input**: Design documents from `/specs/002-organizations/`
**Prerequisites**: plan.md, spec.md, data-model.md, contracts/api.md

**Validation Convention**: FluentValidation is the single source of truth. ASP.NET built-in model validation is disabled. Every request model MUST have a corresponding validator with `Cascade(CascadeMode.Stop)` on multi-rule fields. Endpoints MUST NOT check `ModelState.IsValid` — SharpGrip auto-validation handles this before the action runs. All errors use `StandardErrorResponse` via `result.ToProblem(correlationIdProvider)` for business errors and `ValidationResultFactory` for validation errors. Reference existing Identity endpoints for the correct pattern.

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Project initialization and basic structure

- [x] T001 Create feature directory structure in `src/Features/Organizations/` (Endpoints, Models, Services, Validators).

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core infrastructure that MUST be complete before ANY user story can be implemented

- [x] T002 [P] Create `OrganizationRole` enum (Admin=0, Member=1, Guest=2) in `src/Features/Organizations/Models/OrganizationRole.cs`.
- [x] T003 Create `Organization` entity (Id, Name, Slug, CreatedAtUtc) in `src/Features/Organizations/Models/Organization.cs`.
- [x] T004 Create `UserOrgMembership` entity with EF partial unique index `UNIQUE(UserId) WHERE IsEnabled = true` in `src/Features/Organizations/Models/UserOrgMembership.cs`.
- [x] T005 Create `Invitation` entity with `EmailWhitelist` (JSONB) and `RevokedAtUtc` in `src/Features/Organizations/Models/Invitation.cs`.
- [x] T006 [P] Create domain events (`OrganizationCreatedEvent`, `MemberJoinedEvent`, `RoleChangedEvent`, `MemberContextUpdatedEvent`, `MemberLeftEvent`, `InvitationRevokedEvent`) in `src/Features/Organizations/Models/Events/`.
- [x] T007 Register EF Core DB Context changes and run migration for Organizations, Memberships, and Invitations in `src/Infrastructure/Data/AppDbContext.cs`.
- [x] T008 [P] Implement organization-scoped authorization policies (`RequireOrgAdmin`, `RequireOrgMember`, `RequireOrgAccess`) in `src/Infrastructure/Security/AuthorizationSetup.cs`.
- [x] T008a [P] Implement Domain Event Dispatcher (e.g., MediatR publishing overriding `SaveChangesAsync`) in `src/Infrastructure/Data/AppDbContext.cs`.
- [x] T008b [P] Register `ICorrelationIdProvider` service and middleware in DI container for standard problem details.

---

## Phase 3: User Story 1 - Admin Creates an Organization (Priority: P1) 🎯 MVP

**Goal**: Allow users with no active membership to create an organization and become its first Admin.

**Independent Test**: Can be fully tested by authenticating a user with no active membership, calling the create organization endpoint with a valid name, and verifying the organization is created, a membership record exists with Admin role, and the slug is unique and URL-safe.

### Implementation for User Story 1

- [x] T009 [P] [US1] Create API request/response contracts for Create Organization in `src/Features/Organizations/Contracts/OrganizationContracts.cs`.
- [x] T009a [P] [US1] Create `CreateOrganizationRequestValidator` with `Cascade(CascadeMode.Stop)` — Name: NotEmpty, Length(1, 200) — in `src/Features/Organizations/Validators/CreateOrganizationRequestValidator.cs`.
- [x] T010 [P] [US1] Implement `OrganizationController.cs` base partial controller class in `src/Features/Organizations/Endpoints/Organization/OrganizationController.cs`.
- [x] T011 [P] [US1] Implement slug generation component logic (URL-safe, lowercase, collision retry) in `src/Features/Organizations/Services/SlugGenerator.cs`.
- [x] T012 [US1] Implement `CreateOrganizationCommand` and handler in `src/Features/Organizations/Services/CreateOrganizationHandler.cs`.
- [x] T013 [US1] Implement `CreateOrganizationEndpoint.cs` in `src/Features/Organizations/Endpoints/Organization/CreateOrganizationEndpoint.cs`.
- [ ] T013a [P] [US1] Implement integration tests verifying organization creation and tenant isolation constraint (SC-001) in `tests/Features.IntegrationTests/Organizations/CreateOrganizationTests.cs`.

---

## Phase 4: User Story 2 - Admin Lists Organization Members (Priority: P1)

**Goal**: Admins (and members) view the list of all members in their organization.

**Independent Test**: Can be fully tested by creating an organization with multiple members, authenticating as an admin, calling the list members endpoint, and verifying all members are returned with their correct roles and metadata.

### Implementation for User Story 2

- [x] T014 [P] [US2] Create API response contracts for List Members (`MemberResponse`) in `src/Features/Organizations/Contracts/MemberContracts.cs`.
- [x] T015 [P] [US2] Implement `MemberController.cs` base partial controller class in `src/Features/Organizations/Endpoints/Member/MemberController.cs`.
- [x] T016 [US2] Implement `ListMembersQuery` and handler to return isolated tenant members in `src/Features/Organizations/Services/ListMembersHandler.cs`.
- [x] T017 [US2] Implement `ListMembersEndpoint.cs` in `src/Features/Organizations/Endpoints/Member/ListMembersEndpoint.cs`.
- [ ] T017a [P] [US2] Implement integration tests verifying cross-tenant isolation where Org A query returns zero results from Org B (SC-001) in `tests/Features.IntegrationTests/Organizations/ListMembersTests.cs`.

---

## Phase 5: User Story 3 - Admin Updates a Member's Role (Priority: P1)

**Goal**: Administrators change a member's organization role (e.g., promoting/demoting) ensuring at least one admin remains.

**Independent Test**: Can be fully tested by creating an organization with an admin and a member, updating the member's role via the endpoint, and verifying the role change is persisted.

### Implementation for User Story 3

- [x] T018 [P] [US3] Create `UpdateMemberRoleRequest` contract in `src/Features/Organizations/Contracts/MemberContracts.cs`.
- [x] T018a [P] [US3] Create `UpdateMemberRoleRequestValidator` with `Cascade(CascadeMode.Stop)` — OrgRole: NotEmpty, IsInEnum — in `src/Features/Organizations/Validators/UpdateMemberRoleRequestValidator.cs`.
- [x] T019 [US3] Implement `UpdateMemberRoleCommand` and handler with "last admin" transaction invariant check in `src/Features/Organizations/Services/UpdateMemberRoleHandler.cs`.
- [x] T020 [US3] Implement `UpdateMemberRoleEndpoint.cs` (Requires RequireOrgAdmin) in `src/Features/Organizations/Endpoints/Member/UpdateMemberRoleEndpoint.cs`.
- [ ] T020a [P] [US3] Implement integration tests attempting to remove/demote the last admin to verify 100% enforcement of the invariant (SC-006) in `tests/Features.IntegrationTests/Organizations/UpdateMemberRoleTests.cs`.

---

## Phase 6: User Story 5 - Admin Creates & Revokes Invitations (Priority: P1)

**Goal**: Admin creates an invitation to bring new members into the org, or revokes it prior to expiration.

**Independent Test**: Can be fully tested by authenticating as an admin, creating an invitation with an email whitelist, verifying it gets a token, and independently testing the revocation prevents further use.

### Implementation for User Story 5

- [x] T021 [P] [US5] Create `CreateInvitationRequest` and `CreateInvitationResponse` contracts in `src/Features/Organizations/Contracts/InvitationContracts.cs`.
- [x] T021a [P] [US5] Create `CreateInvitationRequestValidator` with `Cascade(CascadeMode.Stop)` — EmailWhitelist: NotEmpty, ForEach(email: NotEmpty, EmailAddress) — in `src/Features/Organizations/Validators/CreateInvitationRequestValidator.cs`.
- [x] T022 [P] [US5] Implement `InvitationController.cs` base partial controller class in `src/Features/Organizations/Endpoints/Invitation/InvitationController.cs`.
- [x] T023 [US5] Implement `CreateInvitationCommand` and handler (generates random URL-safe token, JSONB whitelist mapping) in `src/Features/Organizations/Services/CreateInvitationHandler.cs`.
- [x] T024 [US5] Implement `CreateInvitationEndpoint.cs` in `src/Features/Organizations/Endpoints/Invitation/CreateInvitationEndpoint.cs`.
- [x] T025 [US5] Implement `RevokeInvitationCommand` and handler (sets `RevokedAtUtc`) in `src/Features/Organizations/Services/RevokeInvitationHandler.cs`.
- [x] T026 [US5] Implement `RevokeInvitationEndpoint.cs` in `src/Features/Organizations/Endpoints/Invitation/RevokeInvitationEndpoint.cs`.
- [ ] T026a [P] [US5] Implement contract and integration tests for Invitation creation and revocation checking token constraints (SC-005) in `tests/Features.IntegrationTests/Organizations/InvitationTests.cs`.

---

## Phase 7: User Story 6 - User Joins an Organization via Invitation (Priority: P1)

**Goal**: Registered users accept invitations via non-expired, unrevoked tokens specifically allowing their email.

**Independent Test**: Can be fully tested by explicitly invoking the join endpoint with a token, and successfully getting an active `UserOrgMembership`.

### Implementation for User Story 6

- [x] T027 [P] [US6] Create response contracts for Join Invitation in `src/Features/Organizations/Contracts/InvitationContracts.cs`.
- [x] T028 [US6] Implement `JoinInvitationCommand` and handler (validates token, 7-day expiry/revocation, whitelist email, verify no active org) in `src/Features/Organizations/Services/JoinInvitationHandler.cs`.
- [x] T029 [US6] Implement `JoinInvitationEndpoint.cs` in `src/Features/Organizations/Endpoints/Invitation/JoinInvitationEndpoint.cs`.
- [x] T029a [P] [US6] Implement integration tests proving single-membership DB constraint (SC-002) correctly blocks multiple active memberships during join in `tests/Features.IntegrationTests/Organizations/JoinInvitationTests.cs`.

---

## Phase 8: User Story 4 - Admin or Member Updates Member Context (Priority: P2)

**Goal**: Allowing human-managed AI context field input for LLM pipelines down the line.

**Independent Test**: Member updates their context text, admin verifies it's persisted by listing members.

### Implementation for User Story 4

- [x] T030 [P] [US4] Create `UpdateMemberContextRequest` contract in `src/Features/Organizations/Contracts/MemberContracts.cs`.
- [x] T030a [P] [US4] Create `UpdateMemberContextRequestValidator` with `Cascade(CascadeMode.Stop)` — Context: MaximumLength(2000), JobRole: MaximumLength(100) — in `src/Features/Organizations/Validators/UpdateMemberContextRequestValidator.cs`.
- [x] T031 [US4] Implement `UpdateMemberContextCommand` and handler with specific self-or-admin authorization check in `src/Features/Organizations/Services/UpdateMemberContextHandler.cs`.
- [x] T032 [US4] Implement `UpdateMemberContextEndpoint.cs` in `src/Features/Organizations/Endpoints/Member/UpdateMemberContextEndpoint.cs`.
- [x] T032a [P] [US4] Implement integration tests for Member Context updates evaluating both Admin and Member authorization rules in `tests/Features.IntegrationTests/Organizations/UpdateMemberContextTests.cs`.

---

## Phase 9: User Story 7 - Member Leaves an Organization (Priority: P2)

**Goal**: Allowing voluntary exit by deactivating membership (`is_enabled = false`).

**Independent Test**: Verified by authenticating as a member, executing the leave endpoint, and ensuring their membership flag toggles and token refresh fails afterward.

### Implementation for User Story 7

- [x] T033 [US7] Implement `LeaveOrganizationCommand` and handler (checks not last admin, sets `is_enabled = false`, emits `MemberLeftEvent`) in `src/Features/Organizations/Services/LeaveOrganizationHandler.cs`.
- [x] T034 [US7] Implement `LeaveOrganizationEndpoint.cs` in `src/Features/Organizations/Endpoints/Organization/LeaveOrganizationEndpoint.cs`.
- [x] T034a [P] [US7] Implement integration tests validating leave deactivation and resulting access failures (SC-007) in `tests/Features.IntegrationTests/Organizations/LeaveOrganizationTests.cs`.

---

## Phase 10: Meeting Tags (User Stories 8-11)

**Purpose**: Organization-scoped labels for categorizing meetings. Admins manage the tag pool (CRUD); any member can list tags.

**Goal**: Allow admins to create, update, and soft-delete meeting tags. Any organization member (including Guests) can list active tags. Tags are referenced by meetings via a many-to-many junction table (Phase 3).

**Independent Test**: Can be fully tested by authenticating as an admin, creating tags with names and optional colors, listing them as various roles, updating name/color, and soft-deleting — verifying uniqueness, ordering, authorization, and soft-delete behavior.

### Implementation for Meeting Tags

- [x] T038 [P] [US8/US11] Create `MeetingTag` entity (Id, OrganizationId, Name max 50, Color max 7 nullable, IsActive default true, CreatedAtUtc, UpdatedAtUtc) implementing `IHasOrganizationId` in `MeetingAssistant/Features/Organizations/Models/MeetingTag.cs`.
- [x] T039 [P] Add `MeetingTagCreatedEvent`, `MeetingTagUpdatedEvent`, `MeetingTagDeletedEvent` domain event records implementing `IDomainEvent` in `MeetingAssistant/Features/Organizations/Models/Events/OrganizationEvents.cs`.
- [x] T040 [P] Add EF Core `MeetingTagConfiguration` with partial unique index `(OrganizationId, LOWER(Name)) WHERE IsActive = true`, index on `OrganizationId`, and global query filter for `OrganizationId`. Run migration. In `MeetingAssistant/Features/Organizations/Infrastructure/Persistence/Configurations/MeetingTagConfiguration.cs`.
- [x] T041 [P] [US8/US9] Create API request/response contracts: `CreateMeetingTagRequest`, `UpdateMeetingTagRequest` in `Contracts/Requests/`, `MeetingTagResponse` in `Contracts/Responses/`.
- [x] T042 [P] [US8] Create `CreateMeetingTagRequestValidator` with `Cascade(CascadeMode.Stop)` — Name: NotEmpty, MaximumLength(50); Color: optional, Matches(`^#[0-9A-Fa-f]{6}$`) when provided — in `MeetingAssistant/Features/Organizations/Validators/CreateMeetingTagRequestValidator.cs`.
- [x] T043 [P] [US9] Create `UpdateMeetingTagRequestValidator` with `Cascade(CascadeMode.Stop)` — Name: when provided, NotEmpty, MaximumLength(50); Color: when provided, Matches hex regex or null — in `MeetingAssistant/Features/Organizations/Validators/UpdateMeetingTagRequestValidator.cs`.
- [x] T044 [P] Implement `MeetingTagController.cs` base partial controller class with route `api/organizations/{organizationId:guid}/meeting-tags`, injecting `IMeetingTagService`, in `MeetingAssistant/Features/Organizations/Endpoints/MeetingTag/MeetingTagController.cs`.
- [x] T045 [US8-US11] Implement `IMeetingTagService` interface and `MeetingTagService` — Create (duplicate name check case-insensitive, color normalization to uppercase, emit `MeetingTagCreatedEvent`), Update (partial update, duplicate name check excluding self, emit `MeetingTagUpdatedEvent`), Delete (set `IsActive = false`, emit `MeetingTagDeletedEvent`), List (active only, ordered by `CreatedAtUtc ASC`). Register `IMeetingTagService`/`MeetingTagService` in `OrganizationsDI.cs`.
- [x] T046 [US11] Implement `ListMeetingTagsEndpoint.cs` — `GET`, requires `RequireOrgAccess`, returns active tags ordered by `CreatedAtUtc ASC` — in `MeetingAssistant/Features/Organizations/Endpoints/MeetingTag/ListMeetingTagsEndpoint.cs`.
- [x] T047 [US8] Implement `CreateMeetingTagEndpoint.cs` — `POST`, requires `RequireOrgAdmin`, returns `201 Created` with `MeetingTagResponse`, returns 409 on duplicate name — in `MeetingAssistant/Features/Organizations/Endpoints/MeetingTag/CreateMeetingTagEndpoint.cs`.
- [x] T048 [US9] Implement `UpdateMeetingTagEndpoint.cs` — `PUT /{tagId}`, requires `RequireOrgAdmin`, partial update, returns `200 OK` with `MeetingTagResponse`, returns 404 for inactive/missing tags, 409 on duplicate name — in `MeetingAssistant/Features/Organizations/Endpoints/MeetingTag/UpdateMeetingTagEndpoint.cs`.
- [x] T049 [US10] Implement `DeleteMeetingTagEndpoint.cs` — `DELETE /{tagId}`, requires `RequireOrgAdmin`, sets `IsActive = false`, returns `204 No Content`, returns 404 for already-inactive tags — in `MeetingAssistant/Features/Organizations/Endpoints/MeetingTag/DeleteMeetingTagEndpoint.cs`.
- [ ] T050 [P] [US8-US11] Implement integration tests for MeetingTag CRUD covering SC-011 (duplicate name uniqueness at DB level), SC-012 (soft delete excludes from list), SC-013 (deleted name reuse), SC-014 (non-admin gets 403 on create/update/delete), SC-015 (list ordered by `CreatedAtUtc ASC`), and tenant isolation (Org A tags not visible to Org B) — in `tests/Features.IntegrationTests/Organizations/MeetingTagTests.cs`.

---

## Phase 11: Polish & Cross-Cutting Concerns

**Purpose**: Improvements that affect multiple user stories and system cleanliness

- [x] T035 Review all endpoints (including MeetingTag) to ensure they use `result.ToProblem(correlationIdProvider)` for explicit ProblemDetails formatting.
- [x] T036 Verify all request models (including MeetingTag) have a corresponding FluentValidation validator with `Cascade(CascadeMode.Stop)`, and that SharpGrip auto-validation returns `StandardErrorResponse` via `ValidationResultFactory` for all endpoints.
- [x] T037 Ensure the EF Core DbContext explicitly cascades or restricts deletes to avoid orphan `UserOrgMembership` issues if an admin were hypothetically removed from system in a manual data patch.

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies - can start immediately
- **Foundational (Phase 2)**: Depends on Setup completion - BLOCKS all user stories
- **User Stories P1 (Phases 3-7)**: All depend on Foundational phase completion
  - Can proceed sequentially according to numerical ID (Wait for US1 to be complete to test anything organization-related easily, though US5/US6 can be built cleanly against the models).
- **User Stories P2 (Phases 8-9)**: Wait until all standard capabilities (US1-US6) are implemented.
- **Meeting Tags (Phase 10)**: Depends on Foundational (Phase 2) completion. Independent of Phases 3-9 and can be implemented in parallel with them.
- **Polish (Phase 11)**: Depends on all desired user stories and Meeting Tags being complete.

### Parallel Opportunities

- All Foundational tasks marked `[P]` (enums, domain events, auth requirements) can rapidly run in parallel alongside EF entity mapping setup.
- Models and Contract definitions marked `[P]` under each User story can be distributed to different team members since they define the basic I/O shapes without containing handler logic.
- Both P2 User Stories (Updates to Context and Leaving an Org) operate on completely independent endpoints and handlers. They can be split cleanly to separate implementers once Foundational elements resolve.
- Meeting Tag tasks (Phase 10) are fully independent of Phases 3-9 and can run in parallel once Phase 2 is complete. T038-T043 (models, events, config, contracts, validators) are parallelizable [P] tasks.
