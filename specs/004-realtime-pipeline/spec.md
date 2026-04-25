# Feature Specification: Realtime Session Pipeline

**Feature Branch**: `004-realtime-pipeline`
**Created**: 2026-04-18
**Last Updated**: 2026-04-25
**Status**: Draft (Phase 4/5/5.5 amended)
**Input**: User description: "Phase 4 — Realtime Pipeline: LiveKit Cloud join tokens, webhook ingestion for room lifecycle events, participant-track audio ingestion to MinIO, and post-meeting transcript + summary generation. Live captions are not provided in Phase 4/4.5. Role-based realtime permissions (Host/CoHost/Participant/Observer), tenant-scoped SignalR notifications for session events."

> **Revision notes**:
>
> - **2026-04-25 (Phase 4/5 correction + Phase 5.5 build-out)**: Egress mode is track-based and audio-only (no video). One `ParticipantAudioTrack` is stored per participant and ingested to MinIO under `tracks/{MeetingId}/{ParticipantUserId}.ogg`. The post-meeting persistence model is `MeetingTranscript` + `MeetingSummary` (1:1 with `Meeting`, overwrite on regenerate). `ParticipantAudioReadyEvent` and `MeetingTranscriptReadyEvent` orchestrate Hangfire transcript/summary jobs.
> - **2026-04-21 (scope revision)**: Realtime transcript persistence, transcript retrieval, and transcription pause/resume are no longer part of Phase 4. The authoritative transcript is produced in **Phase 6 (Post-Meeting AI Pipeline)** using WhisperX (or an equivalent offline STT) applied to the recording.
> - **2026-04-24 (Phase 4.5 amendment)**: Live transcription and live captions are not provided. The backend must not promise, relay, store, or expose live caption text during the meeting.
> - **2026-04-21 (MVP simplification)**: The recording pipeline is intentionally minimal. No reconciliation job and no synchronous external API calls in webhook processing. Phase 6 integration remains a **storage boundary** through MinIO.

## The Post-Meeting Pipeline (at a glance)

```
LiveKit track egress (audio-only) -> egress_ended webhook -> IngestParticipantAudioJob (per track)
-> ParticipantAudioReadyEvent (exactly once) -> GenerateMeetingTranscriptJob
-> MeetingTranscriptReadyEvent -> GenerateMeetingSummaryJob
```

Primary persistence artifacts are `ParticipantAudioTracks`, `MeetingTranscripts`, and `MeetingSummaries`.

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

### User Story 2 - Live Captions Removed From Realtime Scope (Priority: P1)

While a meeting is live, no Phase 4/4.5 capability provides live transcription or live captions. The backend does NOT receive, relay, store, process, or promise realtime caption text.

**Why this priority**: Phase 4.5 removes live transcription and live captions from the realtime scope. This prevents downstream implementation and tests from depending on an unsupported caption path.

**Independent Test**: Can be tested by joining a live session and verifying no backend-supported live transcript/caption endpoint, event, notification, or persisted caption data is produced.

**Acceptance Scenarios**:

1. **Given** a meeting is live, **When** a participant speaks, **Then** the backend does not produce live transcript or caption text.
2. **Given** a client is connected to the live session, **When** session notifications are reviewed, **Then** no live transcription active, paused, or error notification is expected.
3. **Given** a meeting has ended, **When** transcript data is needed for debugging, **Then** transcript data is produced only by post-meeting processing and may be inspected only by OrgAdmins through the Phase 4.5 TranscriptController.

---

### User Story 3 - Capture Participant Audio for Post-Meeting AI (Priority: P1)

When a meeting ends, LiveKit track egress (audio-only) writes one file per participant and notifies the backend via `egress_ended`. The backend fans out one `ParticipantAudioTrack` per file result and enqueues one ingest job per pending track. Once all tracks are terminal (`Available` or `Failed`), exactly one `ParticipantAudioReadyEvent` is emitted to begin transcript generation.

**Why this priority**: Participant-track audio is the required input for post-meeting transcript and summary generation. If track ingestion never completes, the post-meeting pipeline cannot produce artifacts.

**Independent Test**: Can be tested by posting a signed `egress_ended` webhook with multiple `FileResults[]` and verifying (a) one `ParticipantAudioTrack` row per participant, (b) one ingest enqueue per pending track, (c) MinIO objects at `tracks/{MeetingId}/{ParticipantUserId}.ogg` for successful tracks, and (d) exactly one `ParticipantAudioReadyEvent` row in `SessionEvents`.

**Acceptance Scenarios**:

1. **Given** `egress_ended` contains N participant files, **When** the webhook is processed, **Then** N `ParticipantAudioTrack` rows are upserted (one per participant) and N ingest jobs are enqueued for valid source URLs.
2. **Given** an ingest job succeeds, **When** it finishes, **Then** the track is `Available` and `StorageObjectKey` points to `tracks/{MeetingId}/{ParticipantUserId}.ogg` in MinIO.
3. **Given** an ingest job fails, **When** processing ends, **Then** that track is marked `Failed` and processing continues for other tracks.
4. **Given** all tracks for a meeting are terminal (`Available` or `Failed`), **When** the last terminal transition is committed, **Then** exactly one `ParticipantAudioReadyEvent` is emitted.
5. **Given** at least one track is `Available`, **When** transcript generation runs, **Then** it produces a merged transcript from available tracks and can continue to summary generation even if some tracks failed.

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
- **FR-011**: System MUST NOT persist, store, relay, promise, or expose live transcript/caption text during a session. Live transcription and live captions are not provided in Phase 4/4.5.
- **FR-012**: System MUST NOT expose participant-facing transcript retrieval in this phase. Phase 4.5 defines an OrgAdmin-only TranscriptController for post-meeting transcript debugging only.
- **FR-013**: System MUST, upon a verified `egress_ended` webhook, iterate all participant `FileResults[]`, resolve participant identities, upsert one `ParticipantAudioTrack` per participant, and enqueue one `IngestParticipantAudioJob` per pending track source URL.
- **FR-014**: System MUST persist `ParticipantAudioTrack` rows with at least: `MeetingId`, `OrganizationId`, `ParticipantUserId`, `Status`, and `StorageObjectKey`. System MUST treat `Available` and `Failed` as terminal states for the meeting-level join barrier.
- **FR-015**: System MUST enforce tenant isolation on all session operations — a user cannot request join credentials for or receive notifications about meetings outside their active organization. `ParticipantAudioTrack`, `MeetingTranscript`, and `MeetingSummary` rows MUST carry the meeting's organization id for tenant-scoped reads.
- **FR-016**: System MUST push realtime notifications for session lifecycle changes (`room_started`, `room_finished`, `participant_joined`, `participant_left`) to clients of the meeting's organization only, never broadcast to all connected clients.
- **FR-017**: System MUST store all session-event and recording timestamps in UTC.
- **FR-018**: System MUST record participant presence events (joined/left) so attendance data can be derived for downstream reporting.
- **FR-019**: System MUST support up to 50 concurrent participants per live meeting session, consistent with the participant cap established in Phase 3.
- **FR-020**: System MUST, when an `egress_ended` webhook arrives for a meeting that does not exist in the system, acknowledge the webhook (200 OK) without creating `ParticipantAudioTrack` rows and without enqueuing ingest jobs.
- **FR-021**: System MUST emit `ParticipantAudioReadyEvent` exactly once per meeting when all participant tracks are terminal, guarded by unique `(MeetingId, EventType)` idempotency in `SessionEvents`.
- **FR-022**: System MUST persist post-meeting transcript output in `MeetingTranscript` (merged `FullText`, ordered `SegmentsJson`, STT model and timestamp), unique by `MeetingId`, overwrite on regenerate.
- **FR-023**: System MUST persist post-meeting summary output in `MeetingSummary` (summary text, model, token usage, timestamp), unique by `MeetingId`, overwrite on regenerate.
- **FR-024**: System MUST use LiveKit track egress in audio-only mode (video disabled) for this pipeline.

### Key Entities

- **SessionJoinCredential**: A transient, short-lived credential issued to a specific user for a specific meeting's live session. Carries the participant's role-derived permissions. Not persisted long-term; its issuance is logged for audit.
- **SessionLifecycleEvent**: A verified event delivered by the realtime platform describing a change in session, participant, or recording state (`room_started`, `room_finished`, `participant_joined`, `participant_left`, `egress_ended`). Used to update meeting status, participant attendance, and to trigger the recording download. Processed idempotently by a stable external event id.
- **ParticipantAudioTrack**: One row per participant per meeting. Stores ingest lifecycle (`Pending`, `Downloading`, `Available`, `Failed`), participant identity, and MinIO `StorageObjectKey` once available.
- **MeetingTranscript**: One row per meeting containing merged transcript text (`FullText`) and ordered segment payload (`SegmentsJson`) generated from available participant tracks.
- **MeetingSummary**: One row per meeting containing the summarizer output and token usage metadata.

> **Removed from this phase**: the single-row `Recording` model and any `TranscriptSegments` relational table. Post-meeting artifacts are persisted as meeting-scoped transcript/summary rows.

## Success Criteria

### Measurable Outcomes

- **SC-001**: A participant receives a valid join credential within 1 second of requesting one for a Scheduled or InProgress meeting.
- **SC-002**: Live captions for a spoken utterance appear to session participants within the realtime platform's own latency budget (typically under 3 seconds). The backend is **not on this path** — no backend latency target applies.
- **SC-003**: Meeting status reflects the real session state (InProgress / Completed) within 5 seconds of the corresponding verified lifecycle event.
- **SC-004**: For 100% of completed meetings with delivered `egress_ended` webhooks, participant tracks reach terminal states and successful tracks produce readable MinIO objects under `tracks/{MeetingId}/` within 10 minutes of webhook receipt.
- **SC-005**: 100% of join credentials carry permissions that match the requester's meeting role — no role mismatch is ever observed.
- **SC-006**: 100% of session operations are scoped to the requester's active organization — no cross-organization leakage is observed in automated tenant-isolation tests.
- **SC-007**: 100% of webhook events with invalid or missing source signatures are rejected.
- **SC-008**: Duplicate or replayed webhook events produce zero duplicate state transitions, zero duplicate per-track ingest enqueues, and exactly one `ParticipantAudioReadyEvent` per meeting.
- **SC-009**: Realtime notifications for session events are delivered only to clients of the meeting's organization — zero cross-organization notifications are observed in automated tests.
- **SC-010**: A live session with 50 concurrent participants operates within the latency targets of SC-001 and SC-003 with no degradation.

## Clarifications

### Session 2026-04-18

- Q: What is the maximum number of concurrent participants per live meeting session? → A: Up to 50 participants per session (aligns with Phase 3 SC-002).
- Q: How long should a join credential remain valid before a participant must request a new one? → A: 15 minutes from issuance; once joined, the realtime platform's own session lifetime takes over.

### Session 2026-04-21 (scope simplification)

- Q: Does Phase 4 persist realtime transcript text? → **A: No.** Live transcription and live captions are not provided in Phase 4/4.5; the backend does not receive, relay, store, process, promise, or expose realtime caption text. The authoritative transcript is produced after the meeting from recorded audio.
- Q: Does Phase 4 expose transcript retrieval? → **A: Not for participants.** Phase 4.5 defines an OrgAdmin-only TranscriptController for post-meeting transcript debugging only.
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
- A managed realtime platform (LiveKit Cloud) provides the underlying audio/video infrastructure, egress-based recording into its managed cloud storage, and webhook delivery for session and recording lifecycle events. The backend does not host WebRTC, STT, or media processing services itself — this satisfies the "single deployable unit" constitution constraint.
- **Live transcription and live captions are not provided in Phase 4/4.5.** The backend does not relay, buffer, store, process, promise, or expose realtime caption text.
- **The authoritative post-meeting artifacts are generated from participant-track audio** ingested into MinIO. Transcript and summary persistence are part of this feature's Phase 5.5 scope.
- **MinIO is the storage boundary** between ingest and AI processing. Each successful participant track is stored with a deterministic key under `tracks/{MeetingId}/`.
- The ingest pipeline is intentionally simple: `egress_ended` fan-out -> `IngestParticipantAudioJob` per track -> terminal join barrier -> transcript job -> summary job.
- `MeetingTranscript` and `MeetingSummary` are overwrite-only meeting-scoped records (no version history in this phase).
- Realtime notifications to clients use the existing tenant-scoped channel convention established in earlier phases (group-per-organization); client subscription and authentication on those channels is already in place.
- Recording is an infrastructure-level concern configured on the realtime platform (LiveKit Egress); enabling or disabling recording per meeting is a policy / admin matter and is not exposed as a per-request backend API in Phase 4.
