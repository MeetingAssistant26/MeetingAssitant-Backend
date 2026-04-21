# Feature Specification: Realtime Session Pipeline

**Feature Branch**: `004-realtime-pipeline`
**Created**: 2026-04-18
**Last Updated**: 2026-04-21
**Status**: Draft (revised — MVP simplification)
**Input**: User description: "Phase 4 — Realtime Pipeline: LiveKit Cloud join tokens, webhook ingestion for room lifecycle events, recording download to MinIO for Phase 6 to pick up. Live captions are delivered directly by the realtime platform. Role-based realtime permissions (Host/CoHost/Participant/Observer), tenant-scoped SignalR notifications for session events."

> **Revision notes**:
>
> - **2026-04-21 (scope revision)**: Realtime transcript persistence, transcript retrieval, and transcription pause/resume are no longer part of Phase 4. Live captions are delivered directly from the realtime platform to clients over its native data channels. The authoritative transcript is produced in **Phase 6 (Post-Meeting AI Pipeline)** using WhisperX (or an equivalent offline STT) applied to the recording.
> - **2026-04-21 (MVP simplification)**: The recording pipeline is intentionally minimal. No domain events, no reconciliation job, no multi-step orchestration. One entity (`Recording`), one background job (`DownloadRecordingJob`), one essential webhook (`egress_ended`). Phase 6 integration is a **storage boundary**: Phase 6 reads files from MinIO, not from an event bus or queue.

## The Recording Pipeline (at a glance)

```
LiveKit Egress → egress_ended webhook → DownloadRecordingJob → MinIO → Phase 6 (WhisperX)
```

That is the entire flow. No orchestrator, no event bus, no reconciliation layer.

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

### User Story 2 - View Live Captions During a Meeting (Priority: P1)

While a meeting is live, speech from speaking participants is transcribed in real time by the realtime platform and delivered as captions directly to connected clients over the platform's native data channels. The backend does NOT receive, relay, store, or process these realtime captions — they are a client/platform concern for the duration of the session.

**Why this priority**: Users expect live captions during a meeting for accessibility and clarity. Delegating this to the realtime platform reduces backend complexity and latency, and aligns with deferring the authoritative transcription to Phase 6.

**Independent Test**: Can be tested by joining a live session as any participant role with a client that subscribes to the realtime platform's caption data channel and verifying caption messages are received from the platform directly, without any round-trip to the backend.

**Acceptance Scenarios**:

1. **Given** a meeting is live and a participant is subscribed via the realtime platform, **When** another participant speaks, **Then** caption text for that speech is delivered to the subscribing client via the platform's data channel within the platform's own latency budget.
2. **Given** a client is connected to the live session, **When** the realtime platform delivers caption messages, **Then** the backend is not involved in that delivery path and no caption text is stored on the backend.
3. **Given** a meeting has ended, **When** clients query the backend for the meeting's transcript, **Then** Phase 4 does not expose any transcript endpoint; the authoritative transcript becomes available only after Phase 6's post-meeting pipeline completes.

---

### User Story 3 - Record the Meeting for Post-Processing (Priority: P1)

When a meeting's live session ends, LiveKit Cloud Egress finalises a recording to its managed cloud storage and notifies the backend via the `egress_ended` webhook. The backend enqueues a single background job that downloads the file into MinIO. Once the file is in MinIO, it is considered ready for Phase 6 to process.

**Why this priority**: The recording is the single input for the entire Phase 6 post-meeting AI pipeline. Without the recording landing in MinIO, every downstream value stream (transcripts, summaries, action items, meeting memory) is blocked.

**Independent Test**: Can be tested by posting a signed `egress_ended` webhook and verifying (a) a `Recording` row exists with `Status = Completed` and a populated `FilePath`, (b) the corresponding object is present in the MinIO bucket at that path, (c) no transcript text is produced or stored by Phase 4, and (d) Phase 6 can read the object from MinIO without any coordination message from Phase 4.

**Acceptance Scenarios**:

1. **Given** a meeting session has ended and LiveKit Egress has finalised a recording, **When** the verified `egress_ended` webhook arrives at the backend, **Then** the system enqueues a single `DownloadRecordingJob` for the meeting's recording.
2. **Given** the download job runs successfully, **When** it finishes, **Then** a `Recording` row exists for the meeting with `Status = Completed` and `FilePath` pointing at the object's location in MinIO.
3. **Given** the download job fails, **When** its retry budget is exhausted, **Then** the `Recording` row is left with `Status = Failed`; no further automatic recovery is attempted (manual retry is acceptable for MVP).
4. **Given** a duplicate `egress_ended` webhook is delivered for the same recording, **When** it is processed, **Then** no duplicate job is enqueued and no duplicate object is uploaded to MinIO.
5. **Given** the recording has landed in MinIO with `Status = Completed`, **When** Phase 6 is ready to process it, **Then** Phase 6 reads the file directly from MinIO using the stored `FilePath`; no event, message, or queue hand-off from Phase 4 is required.

---

### User Story 4 - Automatic Session Lifecycle Tracking (Priority: P2)

When the realtime platform reports that a meeting's session has started, a participant has joined or left, or the session has ended, the system consumes those events and keeps meeting state in sync. A meeting transitions to "InProgress" when its session starts and to "Completed" when its session ends. Participant presence is tracked for audit purposes.

**Why this priority**: Automatic lifecycle tracking removes the need for manual meeting status management. It is not strictly required for a first-demo MVP — a meeting could run without automatic state transitions as long as joins and the recording download work.

**Independent Test**: Can be tested by simulating the realtime platform's lifecycle events (session started, participant joined, participant left, session ended) against the system's ingestion endpoint, and verifying the corresponding meeting's status and participant presence reflect the events.

**Acceptance Scenarios**:

1. **Given** a Scheduled meeting, **When** the realtime platform reports its session has started, **Then** the meeting's status transitions to "InProgress".
2. **Given** an InProgress meeting, **When** the realtime platform reports its session has ended, **Then** the meeting's status transitions to "Completed".
3. **Given** a duplicate lifecycle event is delivered (replay), **When** it is processed, **Then** the meeting's status is not changed a second time and no duplicate side effects occur.
4. **Given** a lifecycle event arrives with an unverifiable source signature, **When** the system inspects it, **Then** the event is rejected and not processed.
5. **Given** a participant join event is received, **When** it is processed, **Then** the event is recorded (for attendance tracking) and the participant is considered "present" in the session.

---

### User Story 5 - Tenant-Scoped Realtime Notifications (Priority: P2)

When a meeting session starts, ends, or a participant joins or leaves, the system pushes notifications to connected clients of the same organization so they can update UI (e.g., a "Meeting is live" badge, a participant-count indicator). Notifications are strictly scoped to the meeting's organization; clients of other organizations never receive them.

**Why this priority**: Realtime UI updates improve user experience for members watching the meeting list, but they are not required for the core joining/recording workflow. Clients can also poll as a fallback.

**Independent Test**: Can be tested by connecting two clients in different organizations, triggering a session event in one organization, and verifying only clients of that organization receive the notification.

**Acceptance Scenarios**:

1. **Given** a client is connected and subscribed to its organization's channel, **When** a meeting in that organization's session starts, **Then** the client receives a "session started" notification for that meeting.
2. **Given** a client is connected for organization A, **When** a meeting in organization B changes state, **Then** the client does NOT receive any notification for that event.

---

### Edge Cases

- What happens when a participant requests a join credential for a meeting whose scheduled start time is far in the future? The system still issues the credential — time-of-day gating is not enforced; meeting status gating is the authority.
- What happens when the last Host leaves the live session mid-meeting? The session continues; the meeting does not automatically end, and remaining CoHosts retain their moderation permissions.
- What happens when a webhook event references a meeting that does not exist in the system (e.g., stale or orphaned event)? The event is acknowledged to prevent retries but not processed; no meeting state or `Recording` row is created from an unrecognized event.
- What happens when the `DownloadRecordingJob` fails after its retry budget? The `Recording` row is left with `Status = Failed`. The MVP does not automatically recover; an operator can re-enqueue the job manually (or delete the row and replay the webhook).
- What happens when LiveKit Cloud never delivers `egress_ended` (e.g., recording disabled for the room, or Egress dropped)? No `Recording` row is created; Phase 6 simply has no file to read for that meeting. This is accepted MVP behaviour — no reconciliation sweep.
- What happens when a user's active organization has changed since they requested the join credential? The credential was issued against a specific organization context; subsequent actions are scoped to the new active organization.

## Requirements

### Functional Requirements

- **FR-001**: System MUST allow a meeting participant to request a short-lived join credential for the meeting's live session.
- **FR-002**: System MUST map each meeting role to realtime session permissions as follows: Host → publish + subscribe + moderate; CoHost → publish + subscribe + moderate; Participant → publish + subscribe; Observer → subscribe only.
- **FR-003**: System MUST reject join credential requests from users who are not participants of the target meeting.
- **FR-004**: System MUST reject join credential requests when the meeting's status is "Cancelled" or "Completed".
- **FR-005**: System MUST issue join credentials that are valid for 15 minutes from issuance and scoped to a single meeting's session. After the validity window elapses, the credential cannot be used to join; a participant who has already joined the session remains connected under the realtime platform's own session lifetime.
- **FR-006**: System MUST accept realtime platform webhook callbacks for session lifecycle events (`room_started`, `room_finished`, `participant_joined`, `participant_left`) and for the `egress_ended` event that signals a finalised recording.
- **FR-007**: System MUST verify the authenticity of webhook callbacks (e.g., signature validation) and reject unverified events.
- **FR-008**: System MUST process webhook events idempotently so that duplicate or replayed events do not cause duplicate state changes or duplicate `DownloadRecordingJob` enqueues.
- **FR-009**: System MUST transition a meeting's status from "Scheduled" to "InProgress" upon receiving a verified session-started event.
- **FR-010**: System MUST transition a meeting's status from "InProgress" to "Completed" upon receiving a verified session-ended event.
- **FR-011**: System MUST NOT persist, store, or relay live transcript/caption text during a session. Realtime captions are delivered directly from the realtime platform to subscribed clients via the platform's native data channels.
- **FR-012**: System MUST NOT expose any transcript retrieval endpoint in this phase. The authoritative transcript for a meeting is produced by the Phase 6 post-meeting AI pipeline from the recording in MinIO; any transcript read API is defined by Phase 6, not by Phase 4.
- **FR-013**: System MUST, upon a verified `egress_ended` webhook, enqueue a single `DownloadRecordingJob` that downloads the recording from LiveKit Cloud storage and stores it in MinIO.
- **FR-014**: System MUST persist a single `Recording` row per meeting that carries at minimum: `Id`, `MeetingId`, `FilePath` (MinIO object key), and `Status` (`Pending`, `Completed`, or `Failed`).
- **FR-015**: System MUST enforce tenant isolation on all session operations — a user cannot request join credentials for or receive notifications about meetings outside their active organization. `Recording` rows MUST carry the meeting's organization id for tenant-scoped queries from Phase 6.
- **FR-016**: System MUST push realtime notifications for session lifecycle changes (`room_started`, `room_finished`, `participant_joined`, `participant_left`) to clients of the meeting's organization only, never broadcast to all connected clients.
- **FR-017**: System MUST store all session-event and recording timestamps in UTC.
- **FR-018**: System MUST record participant presence events (joined/left) so attendance data can be derived for downstream reporting.
- **FR-019**: System MUST support up to 50 concurrent participants per live meeting session, consistent with the participant cap established in Phase 3.
- **FR-020**: System MUST, when an `egress_ended` webhook arrives for a meeting that does not exist in the system, acknowledge the webhook (200 OK) without creating a `Recording` row and without enqueuing a download job.
- **FR-021**: Phase 4 MUST NOT publish any recording-related domain event. Phase 6 reads recordings from MinIO directly, using the `Recording.FilePath` to locate the object. MinIO is the integration boundary between Phase 4 and Phase 6.

### Key Entities

- **SessionJoinCredential**: A transient, short-lived credential issued to a specific user for a specific meeting's live session. Carries the participant's role-derived permissions. Not persisted long-term; its issuance is logged for audit.
- **SessionLifecycleEvent**: A verified event delivered by the realtime platform describing a change in session, participant, or recording state (`room_started`, `room_finished`, `participant_joined`, `participant_left`, `egress_ended`). Used to update meeting status, participant attendance, and to trigger the recording download. Processed idempotently by a stable external event id.
- **Recording**: A minimal metadata record describing a recording produced for a meeting. Carries `Id`, `MeetingId`, `FilePath` (MinIO object key), and `Status` (`Pending` / `Completed` / `Failed`). The binary lives in MinIO; this entity only points at it. Phase 6 reads the row to find the `FilePath` and then reads the object from MinIO.

> **Removed from this phase**: `TranscriptSegment`, `RecordingAsset` (replaced by the simpler `Recording`), `RecordingAvailableEvent`, `RecordingFailedEvent`. Any transcript entity belongs to Phase 6.

## Success Criteria

### Measurable Outcomes

- **SC-001**: A participant receives a valid join credential within 1 second of requesting one for a Scheduled or InProgress meeting.
- **SC-002**: Live captions for a spoken utterance appear to session participants within the realtime platform's own latency budget (typically under 3 seconds). The backend is **not on this path** — no backend latency target applies.
- **SC-003**: Meeting status reflects the real session state (InProgress / Completed) within 5 seconds of the corresponding verified lifecycle event.
- **SC-004**: For 100% of completed meetings that had recording enabled and whose `egress_ended` webhook was delivered, the `Recording` row transitions to `Status = Completed` and the MinIO object is readable within 10 minutes of webhook receipt.
- **SC-005**: 100% of join credentials carry permissions that match the requester's meeting role — no role mismatch is ever observed.
- **SC-006**: 100% of session operations are scoped to the requester's active organization — no cross-organization leakage is observed in automated tenant-isolation tests.
- **SC-007**: 100% of webhook events with invalid or missing source signatures are rejected.
- **SC-008**: Duplicate or replayed webhook events produce zero duplicate state transitions, zero duplicate `DownloadRecordingJob` enqueues, and zero duplicate MinIO objects.
- **SC-009**: Realtime notifications for session events are delivered only to clients of the meeting's organization — zero cross-organization notifications are observed in automated tests.
- **SC-010**: A live session with 50 concurrent participants operates within the latency targets of SC-001 and SC-003 with no degradation.

## Clarifications

### Session 2026-04-18

- Q: What is the maximum number of concurrent participants per live meeting session? → A: Up to 50 participants per session (aligns with Phase 3 SC-002).
- Q: How long should a join credential remain valid before a participant must request a new one? → A: 15 minutes from issuance; once joined, the realtime platform's own session lifetime takes over.

### Session 2026-04-21 (scope simplification)

- Q: Does Phase 4 persist realtime transcript text? → **A: No.** Live captions are delivered directly from the realtime platform to clients over native data channels; the backend does not receive, relay, or store them. The authoritative transcript is produced in Phase 6 from the recording in MinIO.
- Q: Does Phase 4 expose a transcript retrieval endpoint? → **A: No.** Any transcript read API is owned by Phase 6.
- Q: Is realtime transcription pause/resume a Phase 4 capability? → **A: No.**

### Session 2026-04-21 (MVP simplification of the recording pipeline)

- Q: How does Phase 4 hand off the recording to Phase 6? → **A: MinIO is the integration boundary.** Phase 6 reads the file from MinIO using `Recording.FilePath`. No domain events, no queues, no message bus.
- Q: What is the minimum shape of the `Recording` entity? → **A: `Id`, `MeetingId`, `FilePath`, and a simple `Status` (Pending / Completed / Failed).** No size, duration, cloud URL, egress id, or failure-reason columns are required for the MVP. If the implementation finds some of these useful for idempotency (e.g., a stable external egress id), that is acceptable — but the spec commits only to the minimum.
- Q: Is there a reconciliation or self-healing job? → **A: No.** If a download fails after retries, the row is left at `Status = Failed` and manual recovery is acceptable. If an `egress_ended` webhook is never delivered, no recording exists; Phase 6 simply skips that meeting.
- Q: How many background jobs are involved? → **A: One — `DownloadRecordingJob`.** It downloads the recording from LiveKit Cloud storage, uploads it to MinIO, and updates the `Recording` row.
- Q: Which recording-related webhooks are essential? → **A: Only `egress_ended`.** `recording_started` is optional and MAY be handled to pre-create a `Pending` row for auditing, but is not required.

## Assumptions

- Users are authenticated, belong to at least one organization, and have an active organization selected (Phase 1 and Phase 2 complete). All session operations are scoped via the active organization on the request identity.
- The Meeting and MeetingParticipant entities exist and support the roles Host, CoHost, Participant, and Observer (Phase 3 complete).
- A managed realtime platform (LiveKit Cloud) provides the underlying audio/video infrastructure, native live-caption delivery over data channels, egress-based recording into its managed cloud storage, and webhook delivery for session and recording lifecycle events. The backend does not host WebRTC, STT, or media processing services itself — this satisfies the "single deployable unit" constitution constraint.
- **Live captions are delivered directly from the realtime platform to clients via its native data channels.** The backend is not on the caption path at any point; it does not relay, buffer, store, or process realtime caption text.
- **The authoritative transcript is generated in Phase 6** by an offline speech-to-text pipeline (WhisperX or equivalent) running over the recording in MinIO. Phase 4 does not produce, store, or expose any transcript.
- **MinIO is the integration boundary between Phase 4 and Phase 6.** Phase 6 reads recordings from MinIO using `Recording.FilePath` looked up by meeting id. No events, queues, or other coupling mechanisms bridge the two phases.
- The recording pipeline is intentionally minimal: one webhook (`egress_ended`) triggers one background job (`DownloadRecordingJob`), which updates one entity (`Recording`). No reconciliation, no event bus, no self-healing.
- Phase 6 is the single source of truth for transcript, summary, tasks, and meeting memory. Phase 4 explicitly does not duplicate or shadow any of these concerns.
- Realtime notifications to clients use the existing tenant-scoped channel convention established in earlier phases (group-per-organization); client subscription and authentication on those channels is already in place.
- Recording is an infrastructure-level concern configured on the realtime platform (LiveKit Egress); enabling or disabling recording per meeting is a policy / admin matter and is not exposed as a per-request backend API in Phase 4.
