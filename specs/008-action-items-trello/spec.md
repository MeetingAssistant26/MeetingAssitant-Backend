# Feature Specification: Action Items & Trello Integration

**Feature Branch**: `008-action-items-trello`  
**Created**: 2026-05-04  
**Status**: Draft  
**Input**: User description: "create a spec for the new phase 6 in implmentation plan"

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Extract Action Items from Meeting Transcripts (Priority: P1)

After a meeting ends, the system automatically analyzes the meeting transcript using an outsourced LLM to extract clear action items. Each action item includes a task title, description, responsible person, and optional due date. The LLM is given the full participant roster (with IDs) to resolve assignees deterministically, avoiding fuzzy name matching.

**Why this priority**: This is the foundational capability of the feature. Without automated extraction, action items would be lost or manually transcribed, defeating the purpose of reducing the gap between meetings and execution.

**Independent Test**: Can be tested by ending a meeting, waiting for the transcript to be ready, and verifying that action items appear in the review panel with correctly matched assignees.

**Acceptance Scenarios**:

1. **Given** a meeting has ended and the transcript is complete, **When** the extraction job runs, **Then** action items are created with status `PendingReview` and linked to the meeting.
2. **Given** the LLM identifies a responsible person from the participant roster, **When** the action item is persisted, **Then** the `AssignedToParticipantId` and `AssignedToUserId` (if applicable) are correctly populated.
3. **Given** the LLM returns malformed JSON or hallucinates, **When** the extraction job processes the response, **Then** the job logs the error and aborts without crashing, leaving no partial data.

---

### User Story 2 - Review and Approve Action Items (Priority: P1)

All meeting participants can view extracted action items from each meeting. Hosts, CoHosts, and Org Admins can additionally review, edit, approve, reject, or delete items before they are synced to Trello. Only approved items are eligible for Trello sync.

**Why this priority**: Human review ensures accuracy and prevents incorrect or sensitive tasks from being pushed to external systems. This is critical for user trust and adoption.

**Independent Test**: Can be tested by creating action items and verifying that a host can approve, reject, edit, and bulk-sync them independently of the extraction or Trello sync processes.

**Acceptance Scenarios**:

1. **Given** action items exist with status `PendingReview`, **When** a host views them, **Then** all items are listed with their current status, assignee, and due date.
2. **Given** a host edits an action item and marks it `Approved`, **When** the update is saved, **Then** the item's status changes to `Approved` and it becomes eligible for sync.
3. **Given** a host marks an action item `Rejected`, **When** the update is saved, **Then** the item is excluded from sync and retained for audit.
4. **Given** multiple approved action items exist, **When** a host triggers bulk sync, **Then** the response is 207 Multi-Status with per-item results showing which items synced successfully, which are pending retry, and which failed.

---

### User Story 3 - Connect Trello at Organization Level (Priority: P2)

An Org Admin can connect the organization's Trello account using an API Key and Token. They can browse available boards and lists and select the default destination for action items. Credentials are encrypted at rest.

**Why this priority**: Trello connection is required before any sync can occur, but it is a one-time setup step. It is less urgent than extraction and review, which deliver immediate value even without sync.

**Independent Test**: Can be tested by navigating to organization settings, entering Trello credentials, and verifying that boards and lists are fetched and the selection is persisted.

**Acceptance Scenarios**:

1. **Given** an Org Admin provides a valid Trello API Key and Token, **When** they save the integration settings, **Then** the system validates the token and stores encrypted credentials.
2. **Given** Trello credentials are saved, **When** an admin requests board and list options, **Then** the system returns accessible boards and their lists from the Trello API.
3. **Given** an admin selects a board and list, **When** they save the configuration, **Then** the selected board and list IDs are stored as the sync destination.

---

### User Story 4 - Sync Approved Action Items to Trello (Priority: P2)

Approved action items are manually synced to the configured Trello board and list as cards by hosts or cohosts. If the assigned user has connected their personal Trello account and is a member of the selected board, they are assigned to the card. Otherwise, the card is created without an assignee, and the reason is recorded.

**Why this priority**: This is the final delivery step that bridges the meeting platform with the organization's task management tool. It depends on the review and connection steps but delivers the core value proposition.

**Independent Test**: Can be tested by creating approved action items and triggering sync, then verifying card creation and assignment in Trello.

**Acceptance Scenarios**:

1. **Given** an approved action item with a mapped Trello member, **When** sync runs, **Then** a Trello card is created with the correct title, description, due date, and assignee.
2. **Given** an approved action item with an unmapped assignee, **When** sync runs, **Then** the card is created without an assignee and the action item status is set to `SyncedNoAssignee` with reason `UserNotConnected`.
3. **Given** an approved action item where the assignee is not a member of the target board, **When** sync runs, **Then** the card is created without an assignee and the status is set to `SyncedNoAssignee` with reason `NotBoardMember`.
4. **Given** a sync job encounters a Trello 401 error, **When** the error is handled, **Then** the integration status is marked `NeedsReconnect` and the job stops gracefully.

---

### User Story 5 - Connect Personal Trello Account (Priority: P3)

Individual users can connect their personal Trello account from their profile settings. This enables them to be automatically assigned to Trello cards when they are the responsible person for an action item.

**Why this priority**: Personal connection improves the user experience by enabling automatic assignment, but it is not required for the core flow. Cards can still be created without assignees.

**Independent Test**: Can be tested by a user connecting their Trello account and verifying that subsequent action items where they are the assignee result in correctly assigned Trello cards.

**Acceptance Scenarios**:

1. **Given** a user provides a valid personal Trello token, **When** they save the connection, **Then** their Trello member ID and username are stored securely.
2. **Given** an Org Admin manually sets a `TrelloMemberMapping` for a member who has not self-connected, **When** an action item where that member is the assignee is synced, **Then** the resulting Trello card includes them as an assignee (if they are a board member).
3. **Given** a user is connected to Trello, **When** an action item where they are the assignee is synced, **Then** the resulting Trello card includes them as an assignee (if they are a board member).

---

### Edge Cases

- **Duplicate extraction on job retry**: The extraction job checks if action items already exist for the meeting before inserting to prevent duplicates.
- **LLM service unavailable**: If the outsourced LLM is unreachable after 3 retries with exponential backoff, the extraction job marks the meeting's extraction status as `Failed` and creates no action items. Hosts can trigger manual re-extraction later via the review panel.
- **Trello board or list deleted after setup**: On sync, a 404 response marks the integration config as `InvalidConfig` and notifies the admin to reconfigure.
- **Two users have similar names**: The LLM uses the full roster context and transcript context to disambiguate. If still ambiguous, it may return null; the host fixes during review.
- **Rate limiting / transient failures**: The sync engine retries transient errors (429, 5xx, timeouts) up to 3 times with exponential backoff. If retries exhaust, the action item remains `Approved` and the integration status is unchanged. Users can retry manually once the transient issue resolves.
- **Plain-text assignee fallback**: If no platform user match is found, the action item is still created and can be manually assigned during review.
- **Synced items are immutable**: Once an action item reaches `Synced` or `SyncedNoAssignee`, it cannot be edited or re-synced. If the Trello card is deleted externally, the platform retains the action item as a historical record but does not recreate it.
- **Sync failure on non-retryable error**: If a 401 or 404 occurs during sync, the job stops, the integration status is updated (`NeedsReconnect` or `InvalidConfig`), and the action item stays `Approved`. The host must fix the integration and manually retry the sync.
- **Partial bulk sync failure**: During bulk sync, some items may succeed while others fail. The 207 Multi-Status response includes individual results for each item. Successfully synced items transition to `Synced`; failed items remain `Approved` and can be retried individually or in a new bulk sync.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The system MUST automatically extract action items from meeting transcripts after transcript completion.
- **FR-002**: The extraction process MUST provide the LLM with the full participant roster (including participant IDs and user IDs) to enable deterministic assignee resolution.
- **FR-003**: Extracted action items MUST include: title, description, assigned participant, due date (if available), and initial status `PendingReview`.
- **FR-004**: The extraction job MUST be idempotent — if action items already exist for a meeting, no duplicates are created.
- **FR-004a**: The extraction job MUST retry LLM calls with exponential backoff up to 3 times over 15 minutes on transient failures. If the LLM remains unavailable, the job MUST mark extraction as `Failed` and leave no action items for that meeting.
- **FR-005**: All meeting participants MUST be able to view action items from meetings they attended. Hosts, CoHosts, and Org Admins MUST additionally be able to edit, approve, reject, sync, and delete action items.
- **FR-005a**: Hosts and CoHosts MUST be able to trigger manual re-extraction for a meeting, but ONLY when no action items exist for that meeting. If action items exist, they MUST be deleted first before re-extraction is allowed.
- **FR-006**: Only action items with status `Approved` MUST be eligible for Trello sync.
- **FR-007**: Org Admins MUST be able to configure Trello integration by providing an API Key and Token, selecting a board, and selecting a list.
- **FR-008**: Trello credentials and user tokens MUST be encrypted at rest using data protection mechanisms.
- **FR-009**: Users MUST be able to connect their personal Trello account from their profile settings.
- **FR-010**: The sync engine MUST create Trello cards for approved action items with title, description, due date, and list destination.
- **FR-011**: If the assignee is connected to Trello and is a member of the target board, the sync engine MUST assign them to the card.
- **FR-012**: If the assignee cannot be mapped to a valid Trello board member, the card MUST still be created without an assignee, and the reason MUST be recorded.
- **FR-013**: All Trello-related data (settings, connections, mappings, sync operations) MUST be isolated per Organization with no cross-tenant leakage.
- **FR-014**: The sync engine MUST handle Trello API errors gracefully: 401 triggers `NeedsReconnect`, 404 triggers `InvalidConfig`. Transient errors (429, 5xx, timeouts) MUST be retried up to 3 times with exponential backoff before failing.
- **FR-015**: Action items with status `Synced` or `SyncedNoAssignee` MUST be read-only and block any further edits, approvals, or re-sync attempts.
- **FR-016**: Action item updates MUST use optimistic concurrency control (ETag/row version). Concurrent modifications from multiple reviewers MUST be detected and rejected with 409 Conflict, requiring the client to refresh before retrying.
- **FR-017**: Bulk sync endpoints MUST return 207 Multi-Status with per-item results (`Synced`, `SyncedNoAssignee`, `PendingRetry`, `Failed`) and overall integration health, so hosts can see exactly which items succeeded and which require attention.

### Key Entities *(include if feature involves data)*

- **ActionItem**: Represents a task extracted from a meeting. Attributes: title, description, assigned participant/user, due date, status (PendingReview/Approved/Rejected/Synced/SyncedNoAssignee), Trello card reference, missing assignee reason.
- **OrganizationIntegration**: Tracks which external integrations are enabled for an organization. Attributes: integration type, status (Active/NeedsReconnect/InvalidConfig/Disabled).
- **TrelloWorkspaceConfig**: Stores organization-level Trello settings. Attributes: selected board ID, selected list ID, encrypted API key and token.
- **ExternalAccountLink**: Stores per-user, per-organization connections to external providers. Attributes: provider type, external user ID, external username, encrypted access token.
- **TrelloMemberMapping**: Explicit mapping between a platform user and a Trello member ID for a given organization/board. Can be created by the user via self-connection or by an Org Admin on behalf of a member.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: 95% of meetings with transcripts produce at least one actionable item within 5 minutes of transcript completion.
- **SC-002**: Action item assignee resolution accuracy (via LLM roster matching) is at least 90% for named participants.
- **SC-003**: 100% of approved action items are synced to Trello within 30 seconds of approval (excluding API errors).
- **SC-004**: Zero cross-organization data leakage incidents — all Trello settings, member mappings, and sync operations are strictly tenant-isolated.
- **SC-005**: Users can complete the Trello connection setup (org-level and personal) in under 3 minutes.
- **SC-006**: When a Trello assignee is missing, the reason is clearly displayed to users (e.g., "Not connected to Trello" or "Not a board member") with a prompt to resolve.

## Clarifications

### Session 2026-05-04

- **Q**: Which action item state transitions should be permitted, and which should be blocked? → **A**: Option B — Hosts can undo `Approved/Rejected → PendingReview` before sync. `Synced` and `SyncedNoAssignee` are terminal and read-only. No re-sync or edit after external card creation.
- **Q**: How should the system handle concurrent modifications to the same action item by multiple reviewers? → **A**: Option B — Optimistic concurrency control using ETag/row version. Stale writes are rejected with 409 Conflict; client must refresh before retrying.
- **Q**: Should approved action items sync automatically or require manual trigger? → **A**: Option B — Manual sync only. Hosts must explicitly trigger sync for single items or in bulk. No automatic sync on approval.
- **Q**: How should the sync engine handle transient failures and recovery? → **A**: Option C — Automatic retry with exponential backoff for transient errors (429, 5xx, timeouts) up to 3 attempts. Non-retryable errors (401/404) update integration status. Action items remain `Approved` and can be manually retried once the integration is fixed.
- **Q**: Should changing the Trello board/list destination apply retroactively to existing synced action items? → **A**: Option B — Destination changes apply only to future syncs. Existing synced cards remain on their original board/list. No migration of existing cards.
- **Q**: How should bulk sync report partial successes and failures? → **A**: Option B — Return 207 Multi-Status with per-item result breakdown (`Synced`, `SyncedNoAssignee`, `PendingRetry`, `Failed`) plus overall integration health.
- **Q**: Should regular meeting participants be able to view action items? → **A**: Option B — All meeting participants can view action items read-only. Only Hosts, CoHosts, and Org Admins can edit, approve, reject, or sync.
- **Q**: Can Org Admins manually set or override Trello member mappings for organization members? → **A**: Option B — Users can self-connect their Trello account, but Org Admins can also manually set `TrelloMemberMapping` for any member who hasn't connected. This enables admin-assisted mapping without requiring the user's personal token.
- **Q**: What should happen when the outsourced LLM is unavailable during action item extraction? → **A**: Option B — The extraction job retries with exponential backoff up to 3 times over 15 minutes. If the LLM remains unavailable, the job marks extraction as `Failed` for that meeting and leaves no action items. A manual re-extraction endpoint allows hosts to retry later.
- **Q**: Should manual re-extraction replace existing action items or require a clean slate? → **A**: Option A — Re-extraction only works when no action items exist for the meeting. If action items already exist, the host must delete them first before re-extracting.

## Assumptions

- The platform already has a working meeting transcript pipeline (`MeetingTranscriptReadyEvent` is emitted after post-meeting STT completes).
- The outsourced LLM supports structured JSON output for action item extraction.
- Trello's API Key + Token authentication model is sufficient for the MVP; OAuth 1.0a is deferred to a future phase.
- Sync is one-way only (Platform → Trello). Bi-directional status updates from Trello back to the platform are out of scope.
- No real-time notifications (SignalR) are required; status updates are poll-based via API endpoints.
- Users who are assigned action items but have not connected Trello will still have cards created without them as assignees — the system does not block sync for missing mappings.
- The platform's existing multi-tenancy model (`OrganizationId` global query filters) will be extended to all new entities.
