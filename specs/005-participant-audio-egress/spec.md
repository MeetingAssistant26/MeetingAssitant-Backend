# Feature Specification: Participant Audio Egress & Storage

**Feature Branch**: `005-participant-audio-egress`  
**Created**: 2026-04-26  
**Status**: Draft  
**Input**: User description: "create a spec for phase 5 in docs/implementation-plan.md"

## Clarifications

### Session 2026-04-26

- **Q**: How does the system determine the expected number of participant audio tracks for a meeting?  
  **A**: The expected count is derived from the participants who actually joined the live session (tracked via `participant_joined` lifecycle webhooks). The join barrier knows the target N before any egress completes. Unrecognized participants in egress webhooks do not count toward the expected total.
- **Q**: What is the concrete retry policy for track transfers, and how does the join barrier handle tracks stuck retrying?  
  **A**: Track transfers are retried up to 3 times with exponential backoff. An 8-minute hard ceiling applies: if a track has not succeeded after 8 minutes, it is automatically marked `Failed` so the join barrier can fire before the 10-minute SLA expires.
- **Q**: What operational visibility must the system provide for the audio transfer pipeline?  
  **A**: The system exposes queryable pipeline state for any meeting: count and status of all participant tracks, time spent in each state, and whether the readiness event has fired. This enables proactive diagnosis of stuck transfers or missing readiness events without reading raw job logs.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Each Participant's Audio Is Captured Individually (Priority: P1)

When a meeting ends, the system must have a separate, high-quality audio recording for every participant who spoke. This enables accurate speaker attribution in the post-meeting transcript — each voice is processed independently so the transcript can say exactly who said what, without complex diarization.

**Why this priority**: Without per-participant audio tracks, the downstream speech-to-text pipeline cannot trivially attribute text to speakers. Diarization (figuring out who spoke when from a mixed audio file) is error-prone and expensive. Per-track capture eliminates this problem entirely.

**Independent Test**: Configure a meeting with two participants, start a live session, have both speak, end the session. Verify that the system produces two distinct audio artifacts, one per participant, both stored in the organization's object storage.

**Acceptance Scenarios**:

1. **Given** a meeting with three participants and an active live session, **When** the session ends and the realtime platform completes track egress, **Then** the backend receives webhook notifications for each participant's audio track.
2. **Given** an `egress_ended` webhook containing three file results for a single meeting, **When** the backend processes the webhook, **Then** three separate track records are created or updated, one per participant.
3. **Given** a participant who joined the session but never unmuted, **When** track egress completes, **Then** the participant's track is handled gracefully (may be absent from egress or have zero duration) without blocking the pipeline for other participants.

---

### User Story 2 - Audio Tracks Land in Organization Storage Reliably (Priority: P1)

Participant audio tracks originate in the realtime platform's cloud storage. The system must reliably transfer each track into the organization's own object storage, making it available for downstream processing (speech-to-text, summarization) without depending on external cloud storage retention policies.

**Why this priority**: LiveKit Cloud storage is temporary. If the backend fails to transfer audio before the platform's retention window expires, the recording is lost permanently and the post-meeting pipeline has no input. Reliable transfer is the bridge between the live session and every downstream AI feature.

**Independent Test**: Simulate an `egress_ended` webhook with a presigned URL pointing to a test audio file. Verify the backend downloads the file, uploads it to the organization's object storage bucket under a deterministic path, and updates the track record to reflect success.

**Acceptance Scenarios**:

1. **Given** a valid `egress_ended` webhook with a source URL for a participant track, **When** the backend processes the event, **Then** the audio file is transferred to organization storage within a bounded time and the track record reflects success.
2. **Given** a transient network failure during track download, **When** the download is retried, **Then** the transfer eventually succeeds or exhausts retries and marks the track as failed, without corrupting the stored file.
3. **Given** a track that was already successfully transferred, **When** a duplicate `egress_ended` webhook arrives, **Then** the system does not re-download or overwrite the stored file (idempotent handling).

---

### User Story 3 - Downstream Pipeline Knows When All Audio Is Ready (Priority: P2)

The speech-to-text pipeline cannot begin until every participant's audio track has either been successfully stored or definitively failed. The system must signal the downstream pipeline exactly once when this "all tracks terminal" condition is reached, so transcript generation does not start prematurely or repeatedly.

**Why this priority**: Starting STT before all tracks are available would produce an incomplete transcript. Starting it multiple times would waste compute and potentially create duplicate transcript artifacts. A single, reliable readiness signal is the coordination mechanism between storage and AI processing.

**Independent Test**: Seed a meeting with N participant tracks in various states. Transition the last non-terminal track to a terminal state. Verify that exactly one readiness event is emitted and that re-transitioning tracks does not emit additional events.

**Acceptance Scenarios**:

1. **Given** a meeting with three participants who joined the live session and three corresponding track records where two are terminal and one is still pending, **When** the pending track completes transfer, **Then** a single readiness event is emitted for that meeting because all expected tracks are now terminal.
2. **Given** a meeting where all tracks have reached terminal states (some Available, some Failed), **When** the last track transitions, **Then** a single readiness event is emitted, even if multiple tracks finish simultaneously.
3. **Given** a meeting whose tracks are all terminal and a readiness event was already emitted, **When** a duplicate webhook causes a track to be re-evaluated, **Then** no additional readiness event is emitted (idempotent join barrier).

---

### Edge Cases

- What happens when an `egress_ended` webhook arrives for a participant who is not recognized in the meeting roster? The event is acknowledged to prevent retries, but the unrecognized track is not stored and does not block the join barrier.
- What happens when a participant re-joins the session multiple times (causing multiple egress events for the same participant)? The system upserts the participant's track record, keeping the latest successful transfer as the canonical audio for that participant.
- What happens when the source URL in the webhook has expired before the backend attempts download? The transfer job fails after retries and the track is marked Failed, which still counts as terminal for the join barrier.
- What happens when the object storage bucket is temporarily unavailable? Transfer jobs retry up to 3 times with exponential backoff; if all retries exhaust or the 8-minute ceiling is reached, the track is marked Failed.
- What happens when a meeting has zero participants (no one joined)? No tracks are created, no readiness event is emitted, and the post-meeting pipeline has no work to do.
- What happens when an `egress_ended` webhook arrives for a participant *after* the readiness event was already emitted (late-arriving track)? The late track is processed (stored or marked Failed) but does not re-trigger the readiness event; the event is emitted exactly once based on the expected participant count at emission time.
- What happens when video is included in the egress (misconfiguration)? Video streams are ignored; only audio tracks are processed. The system does not fail but may log a warning.
- What happens when a track transfer is still retrying after 8 minutes? The system automatically marks the track as `Failed` so the readiness event can fire within the 10-minute SLA. The failed track may still be re-processed manually by an operator, but it no longer blocks the pipeline.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST receive and validate webhook notifications from the realtime platform when per-participant audio track egress completes.
- **FR-002**: System MUST create or update one track record per participant per meeting, preserving the latest successful transfer as the canonical audio for that participant.
- **FR-003**: System MUST transfer each audio track from the realtime platform's cloud storage into the organization's own object storage under a deterministic, meeting-scoped path.
- **FR-004**: System MUST mark each track record with one of the following statuses: `Pending` (awaiting transfer), `Downloading` (transfer in progress), `Available` (transfer succeeded, storage path populated), or `Failed` (transfer exhausted retries, source URL invalid, or 8-minute ceiling exceeded). Track transfers are retried up to 3 times with exponential backoff; if the 8-minute hard ceiling is reached without success, the track is automatically marked `Failed` so the join barrier can proceed within the 10-minute SLA.
- **FR-005**: System MUST emit a readiness event exactly once per meeting when all expected participant tracks have reached a terminal state (`Available` or `Failed`). The expected count of tracks is derived from the set of participants who joined the live session (tracked via `participant_joined` lifecycle events). Unrecognized participants in egress webhooks do not count toward the expected total.
- **FR-006**: System MUST process duplicate webhook deliveries idempotently — the same external event identifier must not cause duplicate track records, duplicate transfers, or duplicate readiness events.
- **FR-007**: System MUST tenant-scope all track records and storage paths by the meeting's organization, ensuring one organization's audio tracks are never visible to another organization.
- **FR-008**: System MUST handle unrecognized participants in egress webhooks gracefully (acknowledge webhook, do not create track, do not fail the pipeline).
- **FR-009**: System MUST process only audio tracks from the egress; video or mixed-composite streams, if present, MUST be ignored.
- **FR-010**: System MUST expose queryable pipeline state for any meeting, showing the count and current status of all participant tracks, the time each track spent in each state, and whether the readiness event has fired. This enables administrators to diagnose stuck transfers or missing readiness events without inspecting raw job logs.

### Key Entities

- **ParticipantAudioTrack**: A per-participant, per-meeting record representing one audio recording. It tracks the lifecycle from receipt of the egress webhook through transfer completion. It carries the participant identity, meeting identity, organization identity, current status, source URL from the realtime platform, and the local storage path once available. It is the hand-off artifact between the live session pipeline and the post-meeting AI pipeline.
- **Meeting**: The existing meeting entity that owns the session. Its status transitions (Scheduled → InProgress → Completed) are driven by lifecycle webhooks from the realtime platform. The meeting is the parent context under which all participant tracks are grouped.
- **Organization**: The tenant boundary. All tracks and storage paths are scoped to an organization. The organization identity is derived from the meeting that owns the session.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: For 100% of completed meetings with delivered `egress_ended` webhooks, every recognized participant track reaches a terminal state (`Available` or `Failed`) within 10 minutes of webhook receipt.
- **SC-002**: The readiness event for a meeting fires exactly once when all expected participant tracks (derived from session join events) have reached a terminal state, regardless of how many duplicate webhooks or retry attempts occur.
- **SC-003**: Zero cross-organization audio track leakage — a query for one organization's tracks never returns another organization's tracks.
- **SC-004**: Duplicate `egress_ended` webhooks (same external event identifier) produce zero additional storage objects and zero additional track record writes.
- **SC-005**: Audio tracks are stored in organization object storage at deterministic, meeting-scoped paths that the downstream speech-to-text pipeline can resolve without external lookups.
- **SC-006**: The system handles meetings with up to 50 concurrent participants without degradation in transfer throughput or readiness event latency.
- **SC-007**: Pipeline state for any meeting can be queried and returns the status of all participant tracks and the readiness event flag within 500 milliseconds.

## Assumptions

- The realtime platform (LiveKit Cloud) is configured for track-based egress in audio-only mode. Composite recording (single mixed file) is not supported by this pipeline.
- The realtime platform's cloud storage URLs are time-limited presigned URLs. The backend must initiate transfer promptly; if the URL expires, the track becomes Failed.
- Object storage (MinIO or S3-compatible) is provisioned and reachable by the backend. Multi-region or cross-cloud replication is out of scope for this phase.
- The backend runs background job infrastructure (Hangfire) with at least one worker capable of downloading and uploading audio files.
- The downstream speech-to-text pipeline (Phase 5.5) is triggered by the readiness event emitted by this phase. No other mechanism starts transcript generation.
- Video recording is explicitly out of scope. Only audio tracks are processed; any video content in egress is ignored.
- There are no participant-facing screens or APIs for audio track management in this phase. Track records are internal pipeline artifacts accessible only through future admin/debug surfaces.
