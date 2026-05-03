# Feature Specification: Agent-Callable API Surface

**Feature Branch**: `007-agent-api-surface`  
**Created**: 2026-05-03  
**Status**: Draft  
**Input**: User description: "create a spec for phase 5.7 in implmentation plan"

## User Scenarios & Testing *(mandatory)*

### User Story 1 — Agent Retrieves Meeting Context (Priority: P1)

During a live meeting, the AI assistant needs to understand who is present, what organization it is serving, and what meetings are scheduled so it can respond intelligently to participant requests. The assistant must be able to look up the current meeting's participants, their roles, and organizational context in real time.

**Why this priority**: Without organizational and participant context, the agent cannot personalize responses, attribute reminders correctly, or understand meeting dynamics. This is foundational for all other agent capabilities.

**Independent Test**: Can be fully tested by verifying the agent receives accurate participant roster and organization details when joining a meeting room, and delivers relevant contextual responses.

**Acceptance Scenarios**:

1. **Given** a meeting is active with three participants, **When** the agent queries for meeting members, **Then** it receives exactly those three participants with their `userId`, `displayName`, `jobRole`, and human-managed `context` descriptions.
2. **Given** an agent is bound to Organization A, **When** it requests organization details, **Then** it receives Organization A's name, slug, and member count — never data from Organization B.
3. **Given** an agent is bound to Meeting X, **When** it queries meeting members, **Then** the returned list is filtered to Meeting X participants only, excluding other organization members who are not in the meeting.

---

### User Story 2 — Agent Creates and Surfaces Reminders (Priority: P1)

Participants ask the AI assistant to create reminders during meetings — either for the whole group ("remind us to review the budget next week") or for a specific individual ("remind me to follow up with the vendor"). The assistant must create these reminders and, at the start of each meeting, surface any pending public reminders that are due.

**Why this priority**: Reminder creation and retrieval is the primary user-facing value of the agent during meetings. It directly replaces manual note-taking and ensures commitments made in meetings are not forgotten.

**Independent Test**: Can be fully tested by having the agent create a public reminder, starting a subsequent meeting, and verifying the agent surfaces and speaks the reminder at the appropriate time.

**Acceptance Scenarios**:

1. **Given** a participant asks the agent to "remind us to review Q3 metrics at the next standup", **When** the agent creates a public reminder linked to the recurring standup series, **Then** the reminder is stored and will surface at the first standup occurrence on or after the target date.
2. **Given** public reminders exist for a meeting, **When** the agent queries for meeting reminders at meeting start, **Then** it receives only active public reminders whose due date is on or before the meeting's scheduled start time.
3. **Given** the agent speaks a public reminder during a meeting, **When** it marks the reminder as delivered, **Then** the reminder is closed and will never surface again at future occurrences.
4. **Given** a participant requests a personal reminder, **When** the agent creates it, **Then** the reminder targets that individual and is never exposed to other participants or returned in public reminder queries.

---

### User Story 3 — Agent Navigates Meeting Catalog (Priority: P2)

Participants reference past or future meetings during conversation ("like we discussed last month" or "let's table this for next week's planning"). The agent needs to browse the organization's meeting catalog to understand schedules, recurrence patterns, and tag categorizations without human intervention.

**Why this priority**: While not required for basic operation, meeting catalog access enables the agent to handle time-based references, schedule reminders against correct meetings, and provide organizational awareness that improves user trust.

**Independent Test**: Can be fully tested by asking the agent to find the next occurrence of a recurring meeting and verifying it computes the correct date from the meeting's recurrence configuration.

**Acceptance Scenarios**:

1. **Given** the agent needs to schedule a reminder for "next week's planning meeting", **When** it queries upcoming meetings, **Then** it receives a paginated list from which it can identify the correct meeting by title and scheduled time.
2. **Given** the agent is asked whether the current meeting is recurring, **When** it retrieves the meeting detail, **Then** it receives the recurrence configuration (if any) enabling it to compute future occurrence dates.
3. **Given** the agent is categorizing a reminder, **When** it queries the organization's meeting tags, **Then** it receives the active tag catalog for potential classification use.

---

### Edge Cases

- What happens when an agent requests meeting members for a meeting it is not bound to? The system must reject the request to prevent cross-meeting data exposure.
- How does the system handle a personal reminder creation request where the target user is not a participant in the current meeting? The system should still create the reminder but ensure it remains private to the target user.
- What happens when `createdByUserId` is null in an agent reminder creation request? The system MUST accept the reminder with `CreatedByUserId` set to null, preserving the agent channel provenance but leaving the originating user unattributed.
- What happens when the same reminder text is submitted multiple times in one meeting? The system MUST create each reminder independently without deduplication; the agent or LLM handles conversational suppression if desired.
- What happens when an agent creates a reminder with `reminderAtUtc` in the past or present? The system MUST accept the reminder and surface it immediately in the next list query, since "remind me" often implies immediacy in natural conversation.
- What happens when an agent token expires mid-meeting? The system MUST provide a token refresh endpoint that allows the agent to obtain a new token for the same meeting, provided the meeting is still active.
- What happens when a participant asks the agent to cancel a reminder it just created? The system MUST allow the agent to soft-cancel (set `Status = Cancelled`) any reminder it previously created, regardless of delivery state.
- What happens when the meeting ends while the agent is still connected? The system MUST reject token refresh requests once the meeting status transitions to `Completed` or `Cancelled`, gracefully ending the agent's session.
- What happens when an agent exceeds the rate limit? The system MUST return a `429 Too Many Requests` response with a `Retry-After` header, allowing the agent to back off and retry.
- What happens if an agent attempts to mark a personal reminder as delivered? The system must reject this action since personal reminders are owned by individual users, not the agent.
- How does the system prevent an agent token issued for Organization A from reading Organization B's meetings or members? Tenant isolation must be enforced at every boundary.

## Clarifications

### Session 2026-05-03

- **Q1**: What should the default and maximum `limit` be for agent meeting list queries? → **A**: Default 20, maximum 100.
- **Q2**: How should the system determine which user the agent is assisting when it creates a reminder? → **A**: `createdByUserId` provided in the request body, determined by the LLM/agent based on conversational context. The field is nullable for cases where user attribution is unclear.
- **Q3**: Which user fields should be exposed to the agent in the meeting members response? → **A**: `userId`, `displayName`, `jobRole`, and `context`.
- **Q4**: Should the system prevent duplicate reminders from being created within the same meeting? → **A**: Allow duplicates without deduplication. No deduplication logic at the API layer; the agent/LLM handles suppression conversationally if needed.
- **Q5**: How should the system handle reminder creation requests where `reminderAtUtc` is in the past or present? → **A**: Accept past-due reminders and surface them immediately in the next list query.
- **Q6**: What happens when an agent token expires during a live meeting? → **A**: Support token renewal via a refresh endpoint. The agent can renew its token indefinitely while the meeting is active.
- **Q7**: Should the agent be able to cancel reminders it created? → **A**: Yes — add a soft-cancel endpoint for agent-created reminders. Participants can naturally say "never mind, cancel that reminder" and the agent acts on it immediately.
- **Q8**: At which meeting status transition should the agent token refresh be rejected? → **A**: Token refresh is allowed while meeting `Status = InProgress`; rejected once the meeting transitions to `Completed` or `Cancelled`.
- **Q9**: Should agent endpoints enforce rate limiting? → **A**: Yes — apply a generous per-meeting rate limit (e.g., 100 requests per minute per `meetingId`) to prevent runaway loops while accommodating normal parallel tool-use patterns.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The system MUST provide the agent with read-only access to the organization's basic profile (name, slug, member count) scoped to the organization identified in the agent's identity token.
- **FR-002**: The system MUST allow the agent to retrieve a filtered list of participants for a specific meeting, including their `userId`, `displayName`, `jobRole`, and human-managed `context`, but only when the requested meeting matches the agent's bound meeting identity.
- **FR-003**: The system MUST allow the agent to browse the organization's meeting catalog with pagination and status filtering (upcoming or past), returning meeting identifiers, titles, scheduled times, statuses, recurrence configurations, and associated tags. The default page size MUST be 20 and the maximum allowed page size MUST be 100.
- **FR-004**: The system MUST allow the agent to retrieve detailed information for a single meeting, including its full participant list and recurrence configuration, to support scheduling and reminder decisions.
- **FR-005**: The system MUST provide a shortcut endpoint for the agent to list only recurring meeting series, reducing query complexity when the agent needs to compute future occurrences.
- **FR-006**: The system MUST expose the organization's active meeting tag catalog to the agent for contextual awareness.
- **FR-007**: The system MUST allow the agent to create reminders during a meeting, capturing the reminder text, scope (public or personal), optional target user, intended delivery timing, and the user on whose behalf the reminder is being created (`createdByUserId` from the request body, nullable when attribution is unclear). The creating meeting must be recorded for public and agent-channel reminders.
- **FR-008**: The system MUST allow the agent to retrieve active public reminders for a specific meeting at meeting start time, filtered so that only reminders due on or before the meeting's scheduled start are returned. Past-due reminders MUST also be surfaced immediately. Personal reminders MUST never be included in this response.
- **FR-009**: The system MUST allow the agent to mark a public reminder as delivered after speaking it, closing the reminder so it does not surface again.
- **FR-010**: Every agent request that includes a meeting identifier in the route MUST verify that the identifier matches the meeting bound in the agent's identity token. Mismatches MUST be rejected with an access-denied response.
- **FR-011**: Every read query performed on behalf of an agent MUST be scoped to the organization identified in the agent's identity token, preventing cross-tenant data access.
- **FR-012**: The system MUST allow the agent to soft-cancel (set `Status = Cancelled`) a reminder it previously created, enabling natural conversational correction. The cancellation MUST be recorded for audit purposes.
- **FR-013**: The system MUST enforce a per-meeting rate limit on agent endpoints (e.g., 100 requests per minute per `meetingId`), preventing abuse while accommodating parallel tool-use patterns. The limit MUST be separate from and higher than user-facing endpoint rate limits.

### Key Entities *(include if feature involves data)*

- **Reminder**: Represents a timed note to be surfaced later. Contains text, scope (Personal or Public), channel provenance (User or Agent), creator, optional target user, optional meeting linkage, due timing, and delivery status. Public reminders are spoken by the agent; personal reminders are private to the target user.
- **Meeting**: A scheduled gathering with title, timing, status, and optional recurrence configuration. The agent uses meetings as anchor points for reminders and scheduling references.
- **Meeting Participant**: A junction linking users to meetings with assigned meeting roles (Host, CoHost, Participant, Observer). The agent uses this to build the "who's who" for a live room.
- **User Organization Membership**: Stores a user's relationship to an organization, including their job role and human-managed context description. The agent uses context descriptions to personalize interactions and suggest appropriate task assignees.
- **Meeting Tag**: An organization-scoped label for categorizing meetings. Provides contextual metadata the agent may reference.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: The agent can retrieve meeting participant context within 1 second of request, ensuring real-time responsiveness during live meetings.
- **SC-002**: 100% of agent requests with a mismatched meeting identifier are rejected before any data is returned, ensuring meeting-bound security.
- **SC-003**: Public reminders created by the agent surface at the correct meeting occurrence 100% of the time, with zero leakage of personal reminders into public channels.
- **SC-004**: Agent-driven reminder creation to delivery completion can be demonstrated end-to-end in under 5 minutes during a live meeting session.
- **SC-005**: Cross-tenant data access attempts by an agent are blocked in 100% of test cases, with no organization data visible outside its boundary.

## Assumptions

- The AI agent itself is external to this system; this specification defines only the backend API surface the agent consumes.
- The agent receives its identity token at meeting-room creation time, with a lifetime matching the expected meeting duration plus a safety buffer. The agent can renew its token via a refresh endpoint while the meeting `Status = InProgress`; refresh is rejected once the meeting transitions to `Completed` or `Cancelled`.
- Agent identity uses a separate authentication scheme from human users, ensuring clean separation of privileges.
- The existing reminder, meeting, membership, and tag data models from prior phases are sufficient; no new persistent entities are required.
- Speaker attribution and real-time transcription are handled by the external LiveKit Cloud infrastructure, not by this API surface.
- The agent is trusted to compute recurrence-based dates locally using the recurrence configuration returned by the system; the system does not pre-compute occurrence instances.
