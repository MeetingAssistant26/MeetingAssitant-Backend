# Feature Specification: Realtime Session Pipeline

**Feature Branch**: `004-realtime-pipeline`
**Created**: 2026-04-18
**Status**: Draft
**Input**: User description: "Phase 4 — Realtime Pipeline: LiveKit Cloud join tokens, live transcription via LiveKit Cloud STT agent, webhook ingestion for room lifecycle events, and transcript retrieval. Includes TranscriptSegment entity, role-based realtime permissions (Host/CoHost/Participant/Observer), tenant-scoped SignalR notifications for session events."

## User Scenarios & Testing

### User Story 1 - Join a Meeting's Live Session (Priority: P1)

A participant of a scheduled meeting requests a credential that lets them join the meeting's live audio/video session. The system verifies the requester is a participant of the meeting, determines their meeting role, and issues a short-lived credential that grants them the correct permissions on the realtime platform (publish audio/video, subscribe only, moderate, etc.).

**Why this priority**: Without the ability to join a live session with correct permissions, the entire realtime meeting experience is unavailable. This is the gate through which every other realtime capability flows.

**Independent Test**: Can be fully tested by creating a meeting, adding participants with different meeting roles, requesting a join credential for each, and verifying each participant receives a credential whose permissions match their role. Non-participants receive an authorization error.

**Acceptance Scenarios**:

1. **Given** an authenticated user is a Host of a Scheduled meeting, **When** they request a join credential for that meeting, **Then** the system issues a credential granting publish, subscribe, and moderation permissions scoped to that meeting's session.
2. **Given** an authenticated user is an Observer of a meeting, **When** they request a join credential, **Then** the system issues a credential granting subscribe-only permissions (no publish, no moderate).
3. **Given** an authenticated user is a CoHost of a meeting, **When** they request a join credential, **Then** the system issues a credential granting publish, subscribe, and moderation permissions.
4. **Given** an authenticated user is a Participant (non-Host/CoHost) of a meeting, **When** they request a join credential, **Then** the system issues a credential granting publish and subscribe permissions, without moderation.
5. **Given** an authenticated user is NOT a participant of a meeting, **When** they request a join credential, **Then** the system rejects the request with an authorization error.
6. **Given** a meeting has status "Cancelled" or "Completed", **When** a participant requests a join credential, **Then** the system rejects the request with a meeting-state error.
7. **Given** a join credential has been issued, **When** 15 minutes have elapsed since its issuance and the participant has not yet joined, **Then** the credential can no longer be used to join the session and the participant must request a new one.

---

### User Story 2 - Capture Live Transcription During a Meeting (Priority: P1)

While a meeting is live, speech from all speaking participants is transcribed in real time. Each transcribed segment is delivered to the system and stored so it can be retrieved after the meeting, and forwarded to the post-meeting processing pipeline. The stored transcript preserves the order of utterances, their approximate start and end times within the session, and the speaking participant when identifiable.

**Why this priority**: Live transcription is the primary data artifact produced during a meeting. All downstream AI processing (summaries, action items, meeting memory in later phases) depends on captured transcript data. If transcripts are not captured, the entire post-meeting value chain collapses.

**Independent Test**: Can be tested by running a meeting with known spoken content, then retrieving the transcript after the meeting ends and verifying segments are present, ordered correctly, and contain the expected text with speaker attribution where available.

**Acceptance Scenarios**:

1. **Given** a meeting is live with at least one participant speaking, **When** transcript segments are delivered to the system, **Then** each segment is persisted with the meeting, speaker (when known), text content, start time, end time, and sequence order.
2. **Given** the same transcript segment is delivered more than once (duplicate delivery), **When** the system receives it, **Then** it is stored only once (idempotent ingestion by sequence identifier).
3. **Given** a speaker cannot be identified for a given segment, **When** the segment is ingested, **Then** the segment is stored with an empty/unknown speaker attribution rather than being discarded.
4. **Given** a meeting ends, **When** all pending transcript segments have been delivered, **Then** all segments for that meeting are available for retrieval in stored order.

---

### User Story 3 - Retrieve a Meeting's Transcript (Priority: P1)

An organization member who is a participant of a meeting retrieves the full transcript of that meeting after (or during) the session. The transcript is returned as an ordered list of segments with timing, speaker (where known), and text.

**Why this priority**: Retrieval completes the transcript value loop. A captured transcript with no way to read it has no value. This is required for the end user to see meeting output and is a prerequisite for any client-side transcript review UI.

**Independent Test**: Can be tested by running a meeting that captures several transcript segments, then calling the transcript retrieval endpoint as a participant and verifying all segments are returned in the correct order with expected fields.

**Acceptance Scenarios**:

1. **Given** a meeting has captured transcript segments, **When** a participant requests the transcript, **Then** the system returns all segments for that meeting ordered by sequence/time.
2. **Given** a meeting has no transcript segments yet, **When** a participant requests the transcript, **Then** the system returns an empty transcript (not an error).
3. **Given** a user is not a participant of a meeting, **When** they request the transcript, **Then** the system rejects the request with an authorization error.
4. **Given** a user requests the transcript of a meeting in another organization, **When** tenant scoping is applied, **Then** the meeting is not found (tenant isolation).

---

### User Story 4 - Automatic Session Lifecycle Tracking (Priority: P2)

When the realtime platform reports that a meeting's session has started, a participant has joined or left, or the session has ended, the system consumes those events and keeps meeting state in sync. A meeting transitions to "InProgress" when its session starts and to "Completed" when its session ends. Participant presence is tracked for audit purposes.

**Why this priority**: Automatic lifecycle tracking removes the need for manual meeting status management and produces the data needed for analytics and attendance. However, it is not strictly required for a first-demo MVP — a meeting could run without automatic state transitions as long as transcripts and joins work.

**Independent Test**: Can be tested by simulating the realtime platform's lifecycle events (session started, participant joined, participant left, session ended) against the system's ingestion endpoint, and verifying the corresponding meeting's status and participant presence reflect the events.

**Acceptance Scenarios**:

1. **Given** a Scheduled meeting, **When** the realtime platform reports its session has started, **Then** the meeting's status transitions to "InProgress".
2. **Given** an InProgress meeting, **When** the realtime platform reports its session has ended, **Then** the meeting's status transitions to "Completed".
3. **Given** a duplicate lifecycle event is delivered (replay), **When** it is processed, **Then** the meeting's status is not changed a second time and no duplicate side effects occur.
4. **Given** a lifecycle event arrives with an unverifiable source signature, **When** the system inspects it, **Then** the event is rejected and not processed.
5. **Given** a participant join event is received, **When** it is processed, **Then** the event is recorded (for attendance tracking) and the participant is considered "present" in the session.

---

### User Story 5 - Tenant-Scoped Realtime Notifications (Priority: P2)

When a meeting session starts, ends, or changes state (e.g., transcription paused, participant joined), the system pushes notifications to connected clients of the same organization so they can update UI (e.g., a "Meeting is live" badge, a participant-count indicator). Notifications are strictly scoped to the meeting's organization; clients of other organizations never receive them.

**Why this priority**: Realtime UI updates improve user experience for members watching the meeting list, but they are not required for the core joining/transcription workflow. Clients can also poll as a fallback.

**Independent Test**: Can be tested by connecting two clients in different organizations, triggering a session event in one organization, and verifying only clients of that organization receive the notification.

**Acceptance Scenarios**:

1. **Given** a client is connected and subscribed to its organization's channel, **When** a meeting in that organization's session starts, **Then** the client receives a "session started" notification for that meeting.
2. **Given** a client is connected for organization A, **When** a meeting in organization B changes state, **Then** the client does NOT receive any notification for that event.
3. **Given** the transcription service reports "paused" or "error" during a live session, **When** the event is received, **Then** clients in the meeting's organization receive a transcription status notification.
4. **Given** a Host or CoHost pauses live transcription mid-session, **When** the pause takes effect, **Then** no further transcript segments are ingested until transcription is resumed, and clients in the meeting's organization receive a "transcription paused" notification identifying the acting user.
5. **Given** a Participant or Observer attempts to pause live transcription, **When** the request reaches the system, **Then** the system rejects it with an authorization error.

---

### Edge Cases

- What happens when a participant requests a join credential for a meeting whose scheduled start time is far in the future? The system still issues the credential — time-of-day gating is not enforced; meeting status gating is the authority.
- What happens when the last Host leaves the live session mid-meeting? The session continues; the meeting does not automatically end, and remaining CoHosts retain their moderation permissions.
- What happens when a webhook event references a meeting that does not exist in the system (e.g., stale or orphaned event)? The event is acknowledged to prevent retries but not processed; no meeting state is created from an unrecognized event.
- What happens when transcript segments arrive after a meeting is already marked "Completed"? Late segments are still ingested and appended to the meeting's transcript in correct sequence order.
- What happens when a user's active organization has changed since they requested the join credential? The credential was issued against a specific organization context; if the user has switched active organizations, their subsequent actions (e.g., viewing transcript) are scoped to the new active organization and may not see the meeting.
- What happens when two transcript segments have the same sequence number or overlapping timestamps? The system accepts the first occurrence and treats subsequent ones as duplicates (no overwrite).

## Requirements

### Functional Requirements

- **FR-001**: System MUST allow a meeting participant to request a short-lived join credential for the meeting's live session.
- **FR-002**: System MUST map each meeting role to realtime session permissions as follows: Host → publish + subscribe + moderate; CoHost → publish + subscribe + moderate; Participant → publish + subscribe; Observer → subscribe only.
- **FR-003**: System MUST reject join credential requests from users who are not participants of the target meeting.
- **FR-004**: System MUST reject join credential requests when the meeting's status is "Cancelled" or "Completed".
- **FR-005**: System MUST issue join credentials that are valid for 15 minutes from issuance and scoped to a single meeting's session. After the validity window elapses, the credential cannot be used to join; a participant who has already joined the session remains connected under the realtime platform's own session lifetime.
- **FR-006**: System MUST accept realtime platform webhook callbacks for session lifecycle events (session started, session ended, participant joined, participant left) and for transcription status events.
- **FR-007**: System MUST verify the authenticity of webhook callbacks (e.g., signature validation) and reject unverified events.
- **FR-008**: System MUST process webhook events idempotently so that duplicate or replayed events do not cause duplicate state changes.
- **FR-009**: System MUST transition a meeting's status from "Scheduled" to "InProgress" upon receiving a verified session-started event.
- **FR-010**: System MUST transition a meeting's status from "InProgress" to "Completed" upon receiving a verified session-ended event.
- **FR-011**: System MUST ingest transcript segments delivered during a live session and persist each segment with its meeting identifier, organization identifier, speaker identifier (when known), text content, start time, end time, and sequence number.
- **FR-012**: System MUST de-duplicate transcript segments by sequence identifier within a given meeting.
- **FR-013**: System MUST provide an endpoint that returns the full stored transcript for a meeting as an ordered list of segments.
- **FR-014**: System MUST restrict transcript retrieval to participants of the meeting.
- **FR-015**: System MUST enforce tenant isolation on all transcript reads and session operations — a user cannot access sessions or transcripts outside their active organization.
- **FR-016**: System MUST push realtime notifications for session lifecycle and transcription status changes to clients of the meeting's organization only, never broadcast to all connected clients.
- **FR-017**: System MUST emit domain events for key session lifecycle changes (session started, session ended, transcript segment ingested) so downstream features can subscribe.
- **FR-018**: System MUST store all session and transcript timestamps in UTC.
- **FR-019**: System MUST continue to persist and retrieve transcript segments that arrive after the associated meeting has already transitioned to "Completed".
- **FR-020**: System MUST record participant presence events (joined/left) so attendance data can be derived for downstream reporting.
- **FR-021**: System MUST support up to 50 concurrent participants per live meeting session, consistent with the participant cap established in Phase 3.
- **FR-022**: System MUST transcribe spoken English in live sessions. Non-English speech is out of scope for this phase; any output produced by the underlying platform for non-English audio is not guaranteed, not supported, and not validated by tests in this phase.
- **FR-023**: System MUST allow only Hosts and CoHosts of a meeting to pause or resume live transcription during the session. Participants and Observers MUST NOT have this capability. When transcription is paused, no new transcript segments are ingested until it is resumed.
- **FR-024**: System MUST emit a tenant-scoped notification when live transcription is paused or resumed, including the acting user so clients can reflect the state change in the UI.

### Key Entities

- **TranscriptSegment**: A single unit of transcribed speech captured during a live meeting session. Belongs to one meeting and one organization. Includes the speaking user (when known), the transcribed text, start and end times within the session, a sequence number for ordering and de-duplication, and the time it was recorded.
- **SessionJoinCredential**: A transient, short-lived credential issued to a specific user for a specific meeting's live session. Carries the participant's role-derived permissions. Not persisted long-term; its issuance is logged for audit.
- **SessionLifecycleEvent**: A verified event delivered by the realtime platform describing a change in session or participant state (session started, session ended, participant joined, participant left, transcription paused/resumed). Used to update meeting status and participant attendance. Processed idempotently.

## Success Criteria

### Measurable Outcomes

- **SC-001**: A participant receives a valid join credential within 1 second of requesting one for a Scheduled or InProgress meeting.
- **SC-002**: Live captions for a spoken utterance appear to session participants within 3 seconds of the speech ending.
- **SC-003**: Meeting status reflects the real session state (InProgress / Completed) within 5 seconds of the corresponding lifecycle event.
- **SC-004**: A meeting's full transcript of up to 1 hour of speech can be retrieved in under 2 seconds.
- **SC-005**: 100% of join credentials carry permissions that match the requester's meeting role — no role mismatch is ever observed.
- **SC-006**: 100% of session operations and transcript reads are scoped to the requester's active organization — no cross-organization leakage is observed in automated tenant-isolation tests.
- **SC-007**: 100% of webhook events with invalid or missing source signatures are rejected.
- **SC-008**: Duplicate or replayed webhook events produce zero duplicate state transitions and zero duplicate stored segments.
- **SC-009**: Realtime notifications for session events are delivered only to clients of the meeting's organization — zero cross-organization notifications are observed in automated tests.
- **SC-010**: A live session with 50 concurrent participants operates within the latency targets of SC-001 through SC-004 with no degradation.

## Clarifications

### Session 2026-04-18

- Q: What is the maximum number of concurrent participants per live meeting session? → A: Up to 50 participants per session (aligns with Phase 3 SC-002).
- Q: What language(s) must live transcription support in this phase? → A: English only; multi-language support is deferred to a later phase.
- Q: How long should a join credential remain valid before a participant must request a new one? → A: 15 minutes from issuance; once joined, the realtime platform's own session lifetime takes over.
- Q: Who is authorized to pause or resume live transcription during an active session? → A: Hosts and CoHosts only; Participants and Observers cannot pause or resume.

## Assumptions

- Users are authenticated, belong to at least one organization, and have an active organization selected (Phase 1 and Phase 2 complete). All session and transcript operations are scoped via the active organization on the request identity.
- The Meeting and MeetingParticipant entities exist and support the roles Host, CoHost, Participant, and Observer (Phase 3 complete).
- A managed realtime platform (LiveKit Cloud) provides the underlying audio/video infrastructure, speech-to-text transcription, and webhook delivery. The backend does not host WebRTC or transcription services itself — this satisfies the "single deployable unit" constitution constraint.
- Live captions are delivered to session clients via the realtime platform's native data channels during the session; the backend persists transcript segments for later retrieval rather than relaying captions in real time.
- The transcript retrieval endpoint returns the transcript as stored so far. During an active session, partial transcripts may be returned; clients that need "live" captions rely on the realtime platform's native delivery.
- A meeting's in-progress state updates (e.g., extending end time, pausing transcription for privacy) are driven primarily by the realtime platform's events. In-session meeting-metadata edits remain out of scope here.
- Meeting-memory semantic search, summarization, and action item extraction are explicitly out of scope for this phase; they are handled in Phase 6 and 6.5 by a post-meeting pipeline that consumes the transcript segments stored here.
- Transcription is limited to spoken English in this phase. Multi-language support (including Arabic, per-org language selection, or auto-detection) is deferred to a later phase and will require STT configuration and downstream AI pipeline changes.
- Recording and storage of audio/video files are out of scope for this phase; they are handled in Phase 5.
- The system relies on the realtime platform's speaker identification to attribute segments to users. When the platform cannot identify a speaker, segments are stored with an unknown speaker attribution rather than dropped.
- Realtime notifications to clients use the existing tenant-scoped channel convention established in earlier phases (group-per-organization); client subscription and authentication on those channels is already in place.
