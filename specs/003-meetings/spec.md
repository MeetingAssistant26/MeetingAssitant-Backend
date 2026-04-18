# Feature Specification: Meetings

**Feature Branch**: `003-meetings`
**Created**: 2026-04-08
**Status**: Draft
**Input**: User description: "Phase 3 — Meetings (Weeks 4–6) — create, schedule, manage meetings with participants, recurrence, calendar, and tag association"

## User Scenarios & Testing

### User Story 1 - Create and Manage a Meeting (Priority: P1)

An organization member creates a new meeting by providing a title, optional description, scheduled start and end times, and optionally assigns tags from the organization's tag pool. After creation, the creator is automatically added as the Host. The creator can later update meeting details or cancel the meeting.

**Why this priority**: Meeting creation is the foundational action for the entire feature. Without it, no other meeting functionality (participants, recurrence, calendar) is possible.

**Independent Test**: Can be fully tested by creating a meeting, verifying it appears in the list, updating its title, and then cancelling it. Delivers the core value of organizing meetings within an organization.

**Acceptance Scenarios**:

1. **Given** an authenticated user who belongs to an organization, **When** they create a meeting with a title, start time, and end time, **Then** the meeting is created with status "Scheduled", the user is added as Host, and the meeting appears in the organization's meeting list.
2. **Given** a meeting exists with status "Scheduled", **When** the Host updates the title or times, **Then** the meeting details are updated and a meeting-updated event is emitted.
3. **Given** a meeting exists with status "Scheduled", **When** the Host cancels it, **Then** the meeting status changes to "Cancelled" and a meeting-cancelled event is emitted.
4. **Given** a meeting with status "Cancelled" or "Completed", **When** a user tries to update it, **Then** the system rejects the update with an appropriate error.
5. **Given** a meeting exists, **When** the Host assigns organization tags to it, **Then** the tags are associated with the meeting and returned in meeting details.

---

### User Story 2 - List and Filter Meetings (Priority: P1)

An organization member views meetings within their organization, filtered by upcoming or past meetings. The list includes meeting details, participant count, assigned tags, and status.

**Why this priority**: Viewing meetings is essential for all users to find and access their meetings. Tied with creation as the minimum viable product.

**Independent Test**: Can be tested by creating several meetings with different statuses and dates, then filtering by "upcoming" and "past" to verify correct results are returned.

**Acceptance Scenarios**:

1. **Given** an organization has multiple meetings, **When** a member requests the meeting list with filter "upcoming", **Then** only meetings with scheduled start time in the future and status "Scheduled" or "InProgress" are returned, ordered by start time ascending.
2. **Given** an organization has past meetings, **When** a member requests the meeting list with filter "past", **Then** only meetings with status "Completed" or "Cancelled" or with scheduled end time in the past are returned, ordered by start time descending.
3. **Given** no filter is specified, **When** a member requests the meeting list, **Then** all meetings for the organization are returned with a default order.

---

### User Story 3 - Add and Manage Participants (Priority: P2)

A meeting Host or CoHost adds participants to a meeting by specifying a user (who must be an organization member) and their meeting role (Host, CoHost, Participant, or Observer). The system prevents adding the same user twice. Participants can also be removed, respecting role hierarchy: Hosts can remove anyone, CoHosts can only remove Participants and Observers. The last Host cannot be removed.

**Why this priority**: Meetings need participants to be useful, but a single-user meeting (Host only) already provides value for personal scheduling. Participant management extends the collaborative value.

**Independent Test**: Can be tested by creating a meeting, adding participants with different roles, attempting to add a duplicate, removing a participant, and verifying the participant list reflects correct data.

**Acceptance Scenarios**:

1. **Given** a meeting exists with status "Scheduled", **When** the Host adds a user as a Participant, **Then** the user is added to the meeting with the specified role.
2. **Given** a user is already a participant in a meeting, **When** someone tries to add the same user again, **Then** the system rejects the request with a conflict error.
3. **Given** a meeting exists, **When** a participant is added, **Then** the participant count in meeting listings reflects the updated total.
4. **Given** a user is not a member of the organization, **When** someone tries to add them as a participant, **Then** the system rejects the request.
5. **Given** a meeting with multiple participants, **When** the Host removes a Participant, **Then** the user is removed from the meeting.
6. **Given** a meeting with a Host and a CoHost, **When** the CoHost tries to remove the Host, **Then** the system rejects the request.
7. **Given** a meeting with only one Host, **When** someone tries to remove that Host, **Then** the system rejects the request to preserve the minimum Host requirement.

---

### User Story 4 - Check Scheduling Conflicts (Priority: P2)

Before or after adding participants to a meeting, the Host can check whether any participants have scheduling conflicts with other meetings that overlap in time. The conflict check returns a list of conflicting meetings for each affected participant.

**Why this priority**: Conflict detection prevents double-booking and is a key quality-of-life feature. It builds on participant management and meeting scheduling.

**Independent Test**: Can be tested by creating two overlapping meetings, adding the same user to both, and running a conflict check to verify the overlap is detected.

**Acceptance Scenarios**:

1. **Given** a participant has another meeting that overlaps with the current meeting's time window, **When** a conflict check is run, **Then** the conflicting meeting details are returned for that participant.
2. **Given** no participants have overlapping meetings, **When** a conflict check is run, **Then** an empty conflict list is returned.
3. **Given** a meeting is cancelled, **When** a conflict check is run against its time window, **Then** the cancelled meeting is not considered a conflict.

---

### User Story 5 - Create Recurring Meetings (Priority: P3)

A user creates a recurring meeting by specifying a recurrence pattern (frequency, interval, days of week, optional end date). The system generates individual meeting instances based on the pattern. Each instance is a standalone meeting that can be independently updated or cancelled.

**Why this priority**: Recurring meetings add significant convenience but are not required for the core meeting workflow. The recurrence engine adds complexity that can be deferred after basic meetings work.

**Independent Test**: Can be tested by creating a weekly recurring meeting for 4 weeks, verifying 4 individual meeting instances are created, then cancelling one instance to confirm independence.

**Acceptance Scenarios**:

1. **Given** a user provides a recurrence configuration (e.g., weekly on Mondays for 4 weeks), **When** they create a recurring meeting, **Then** individual meeting instances are generated for each occurrence with status "Scheduled".
2. **Given** a recurring meeting series exists, **When** the user cancels one instance, **Then** only that instance is cancelled; other instances remain "Scheduled".
3. **Given** a recurrence configuration with no end date, **When** the system generates instances, **Then** a reasonable default horizon is used (e.g., 12 weeks ahead).
4. **Given** invalid recurrence parameters (e.g., zero interval, empty days of week), **When** the user submits the request, **Then** the system rejects it with validation errors.

---

### User Story 6 - View Calendar Data (Priority: P3)

An organization member views meeting data for a specified week. The calendar data includes all meetings within that time range with their times, titles, statuses, and participant counts.

**Why this priority**: Calendar view is a presentation-oriented retrieval that enhances usability. The underlying data is already available through meeting listings; this provides a time-range-oriented query optimized for calendar display.

**Independent Test**: Can be tested by creating meetings across different days of a week, then requesting calendar data for that week and verifying all meetings appear with correct day assignments.

**Acceptance Scenarios**:

1. **Given** an organization has meetings spread across a week, **When** a member requests calendar data for that week, **Then** all meetings within the week's date range are returned ordered by day and time.
2. **Given** a week with no meetings, **When** a member requests calendar data, **Then** an empty result is returned.
3. **Given** a meeting spans midnight (crosses day boundaries), **When** calendar data is retrieved, **Then** the meeting appears based on its start time.

---

### Edge Cases

- What happens when a meeting's scheduled end time is before the start time? The system rejects this with a validation error.
- What happens when a meeting is updated while it is "InProgress"? Only certain fields (e.g., description, end time extension) are modifiable. **Phase 3 scope**: updates are restricted to Scheduled status only. Partial InProgress updates are deferred to Phase 4 (LiveKit integration).
- What happens when the last Host is removed from a meeting? The system prevents this — a meeting must always have at least one Host.
- What happens when a recurring meeting's recurrence configuration produces zero instances (e.g., end date is before start date)? The system rejects this with a validation error.
- How does the system handle timezone differences? All times are stored and processed in UTC. Clients are responsible for timezone conversion.

## Requirements

### Functional Requirements

- **FR-001**: System MUST allow organization Admins and Members to create meetings with a title, optional description, scheduled start time, and scheduled end time. Guests are view-only and cannot create meetings.
- **FR-002**: System MUST automatically assign the meeting creator as Host upon meeting creation.
- **FR-003**: System MUST validate that scheduled end time is after scheduled start time.
- **FR-004**: System MUST support meeting status transitions: Scheduled to InProgress to Completed, and Scheduled to Cancelled. The status "Failed" is reserved for system-level failures.
- **FR-005**: System MUST allow the Host to update meeting details (title, description, times) while the meeting is in "Scheduled" status.
- **FR-006**: System MUST allow only the Host to cancel a meeting, changing its status to "Cancelled". CoHosts cannot cancel.
- **FR-007**: System MUST provide a list of meetings for an organization, filterable by "upcoming" or "past".
- **FR-008**: System MUST allow Hosts and CoHosts to add participants to a meeting by specifying a user and their meeting role (Host, CoHost, Participant, Observer).
- **FR-009**: System MUST prevent adding a user who is already a participant in the same meeting.
- **FR-010**: System MUST only allow adding users who are members of the meeting's organization as participants.
- **FR-011**: System MUST provide a scheduling conflict check that identifies overlapping meetings for participants within the user's active organization only. Cross-organization conflicts are not checked, as all queries are scoped to the active organization via JWT claims and global query filters.
- **FR-012**: System MUST support recurring meeting creation with configurable frequency (daily, weekly, monthly), interval, days of week, and optional end date.
- **FR-013**: System MUST generate individual meeting instances from a recurrence configuration, each as an independent meeting.
- **FR-014**: System MUST provide calendar data retrieval for a specified week, returning all meetings within that date range.
- **FR-015**: System MUST support associating organization-scoped tags (MeetingTag) with meetings via a many-to-many relationship.
- **FR-016**: System MUST emit domain events for key meeting lifecycle changes: created, updated, cancelled, started, ended.
- **FR-017**: System MUST store all timestamps in UTC.
- **FR-018**: System MUST enforce that a meeting always has at least one Host participant.
- **FR-019**: System MUST allow Hosts to remove any participant from a meeting.
- **FR-020**: System MUST allow CoHosts to remove only Participants and Observers from a meeting. CoHosts cannot remove Hosts.
- **FR-021**: System MUST prevent removal of the last remaining Host from a meeting.

### Key Entities

- **Meeting**: Represents a scheduled event within an organization. Contains title, description, time window (start/end in UTC), status, and optional recurrence configuration. Belongs to one organization.
- **MeetingParticipant**: Represents a user's involvement in a specific meeting. Links a user to a meeting with a designated role (Host, CoHost, Participant, Observer). Belongs to the meeting's organization for tenant isolation.
- **RecurrenceConfig**: A structured data object stored on the Meeting entity that defines repetition rules — frequency, interval, days of week, and optional end date.
- **MeetingMeetingTag**: A junction record linking a Meeting to a MeetingTag, enabling many-to-many tag assignment. Uses a composite key of meeting and tag identifiers.

## Success Criteria

### Measurable Outcomes

- **SC-001**: Users can create a meeting and see it in their organization's meeting list within 2 seconds of submission.
- **SC-002**: Users can add up to 50 participants to a single meeting without noticeable delay.
- **SC-003**: Scheduling conflict checks return results within 3 seconds for meetings with up to 50 participants.
- **SC-004**: Recurring meeting creation generates all instances within 5 seconds for recurrence spans of up to 52 weeks.
- **SC-005**: Calendar data for a given week loads within 2 seconds regardless of the number of meetings in the organization.
- **SC-006**: All meeting lifecycle actions (create, update, cancel) produce corresponding domain events that can be consumed by downstream features.
- **SC-007**: 100% of meeting operations enforce organization-level tenant isolation — no user can access meetings outside their organization.

## Clarifications

### Session 2026-04-08

- Q: Which organization roles can create meetings, and can CoHosts cancel? → A: Admins and Members can create meetings. Guests are view-only. Only Hosts can cancel; CoHosts can manage participants but not cancel.
- Q: Should conflict detection check across all organizations or same org only? → A: Same (active) organization only. The system enforces one active org per user via JWT organizationId claim, and all queries are scoped by global query filters. Cross-org conflicts are not applicable at runtime.
- Q: Can participants be removed from a meeting, and by whom? → A: Yes. Hosts can remove any participant. CoHosts can remove Participants and Observers only (not Hosts). The last Host cannot be removed — at least one Host must always remain.

## Assumptions

- Users are authenticated and belong to at least one organization before interacting with meetings (Phase 1 and Phase 2 are complete). A user has one active organization membership at a time (IsActive = true), which determines the JWT organizationId claim. All meeting queries are scoped to the active organization via global query filters.
- The MeetingTag entity and CRUD operations are already implemented in Phase 2 (Organizations). This phase only adds the junction table for many-to-many association.
- Meeting status transitions to "InProgress", "Completed", and "Failed" will be triggered by downstream features (Phase 4 — LiveKit integration), not directly by user action in this phase.
- The recurrence configuration uses a simple JSON structure. Complex recurrence rules (e.g., "third Thursday of every month") are out of scope for the initial implementation.
- Calendar data is returned as a flat list of meetings within the requested date range. Calendar-specific grouping (by day, week view formatting) is a client-side concern.
- Pagination for meeting lists follows the same patterns established in Phase 2 (Organizations).
- Meeting deletion is not supported; meetings are cancelled (soft status change) rather than removed from the system.
