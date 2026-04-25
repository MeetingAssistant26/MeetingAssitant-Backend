# Feature Specification: Opus Realtime Amendment (Phase 4.5)

**Feature Branch**: Not created; artifacts live under `specs/005-opus-realtime-amendment/`  
**Created**: 2026-04-25  
**Status**: Draft  
**Input**: User description: "create a spec for phase 4.5 in docs/implementation-plan.md with name 005-opus-realtime-amendment"

## Overview

Phase 4.5 is a scope-reduction amendment to the realtime pipeline. It removes every live speech-to-text capability from the active-meeting experience and re-classifies the transcript as an internal post-meeting artifact. Phase 4's realtime join, permission, and lifecycle behavior is preserved; only the transcription delivery path is changed.

No new user-facing features, storage, jobs, or endpoints are introduced by this phase. The single surviving transcript surface becomes an administrator-only debugging affordance.

## Clarifications

### Session 2026-04-25

- Q: How should an administrator distinguish between "post-meeting transcription hasn't started", "in flight", "completed", and "failed" when the transcript surface returns an empty or incomplete list? → A: The transcript response MUST include a coarse pipeline status indicator (`NotStarted` / `Processing` / `Completed` / `Failed`) alongside the segment list, so a single surface unambiguously conveys state.
- Q: How should the transcript surface respond to an administrator request for a meeting that has not yet ended? → A: Reject the request with an explicit "meeting has not ended" signal for any pre-ended status (both `Scheduled` and `InProgress`); the pipeline-status enum stays scoped to post-meeting pipeline states, and the administrator is told plainly to retry after the meeting ends.
- Q: Are administrator transcript reads recorded in a persistent audit trail in this phase? → A: No. A per-read audit trail is explicitly out of scope for this phase; infrastructure-level access logs (if any) are considered sufficient, and the plan phase MUST NOT introduce an audit-record entity or write path for this surface.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Live Sessions Run Without Any Transcription Promise (Priority: P1)

Participants and organizers use a realtime meeting room with no expectation — in product behavior, in the UI, or in client notifications — that speech is being transcribed or captioned during the session. The room lifecycle, audio/video, and permissions continue to work exactly as before.

**Why this priority**: This is the single direction change that defines the phase. Everything downstream (summarization trigger, transcript visibility, documentation, QA coverage) depends on this expectation being removed cleanly. If this is not enforced, dependent teams will keep building against a capability the product no longer offers.

**Independent Test**: Run a complete meeting (join → speech → leave → room finished) and audit every user-facing contact point (notifications, client events, UI affordances, stored rows) for transcription output produced *during* the meeting. The story passes when none exists and the meeting itself behaves normally.

**Acceptance Scenarios**:

1. **Given** a meeting is active and participants are speaking, **When** the system is observed during the live session, **Then** no transcript text is surfaced to any client, no caption stream is produced, and no transcript row is written for those live utterances.
2. **Given** a client integration was built expecting live-transcription status updates, **When** the new notification contract is reviewed, **Then** no active / paused / error transcription status event is part of the supported contract.
3. **Given** realtime join tokens, room permissions, and room lifecycle are exercised, **When** behavior is compared to the previous phase, **Then** those behaviors are unchanged by this amendment.

---

### User Story 2 - Room Lifecycle Visibility Is Preserved (Priority: P2)

Organizers and participants continue to see the realtime meeting itself as observable: the room starts, the room ends, and participants are visibly joining and leaving through the same notification channel that existed before the amendment.

**Why this priority**: Removing live transcription must not degrade the confidence organizers have that a meeting is "live and happening." Lifecycle visibility is independent of transcription and must survive the amendment without regression.

**Independent Test**: Drive a meeting through its full lifecycle with at least two participants joining and leaving. The story passes when each of the four lifecycle moments (room started, participant joined, participant left, room finished) is still surfaced and no transcription-related surface appears alongside them.

**Acceptance Scenarios**:

1. **Given** a meeting room is created and the first participant connects, **When** the lifecycle signal fires, **Then** a "room started" notification is delivered and no transcription-status signal is emitted.
2. **Given** participants join and leave during a meeting, **When** each lifecycle signal fires, **Then** the join and leave notifications are delivered to clients as before.
3. **Given** a meeting reaches its natural end, **When** the "room finished" lifecycle signal fires, **Then** the meeting transitions to a completed state without any attempt to finalize a live transcript.

---

### User Story 3 - Transcript Is Treated As Post-Meeting Internal Data (Priority: P3)

Ordinary users never see a raw transcript; after the meeting they consume the post-meeting summary. Only organization administrators can reach transcript segments, and they do so through a narrowly-scoped debugging surface that simply reflects whatever the post-meeting pipeline has persisted so far.

**Why this priority**: The transcript still exists as a pipeline artifact and is useful for diagnosing STT quality and pipeline failures, but it is no longer a participant-facing deliverable. Narrowing access preserves the debugging utility while preventing the transcript from re-entering the product surface through the back door.

**Independent Test**: Ask for the transcript as three different callers — a regular participant, a meeting host who is not an org admin, and an organization administrator — at three different pipeline states (before any post-meeting work, while segments exist partially, and after completion). The story passes when the non-admin callers are rejected, the administrator always gets a response shaped by the current pipeline state, and the administrator's own view transitions naturally from empty to chronologically ordered as segments appear.

**Acceptance Scenarios**:

1. **Given** a meeting has ended but post-meeting transcript processing has not yet started, **When** an organization administrator requests the transcript, **Then** the response is returned successfully with an empty list of segments and a pipeline status of `NotStarted`.
2. **Given** post-meeting transcript processing is currently in flight, **When** an organization administrator requests the transcript, **Then** the response is returned successfully with whatever segments are persisted so far and a pipeline status of `Processing`.
3. **Given** post-meeting transcript processing has completed, **When** an organization administrator requests the transcript, **Then** the segments are returned in chronological order of their start time with a pipeline status of `Completed`.
4. **Given** the post-meeting transcription pipeline has exhausted its retries and will not produce more segments, **When** an organization administrator requests the transcript, **Then** the response is returned successfully with whatever segments exist (possibly empty) and a pipeline status of `Failed`.
5. **Given** a non-admin caller (including a meeting host or participant) requests the transcript, **When** access is evaluated, **Then** access is denied.
6. **Given** an ordinary user wants to know what was decided in a meeting, **When** they look for the post-meeting result, **Then** they are directed to the meeting summary rather than the raw transcript.
7. **Given** a meeting has not yet ended (it is either scheduled for the future or currently in progress), **When** an organization administrator requests the transcript, **Then** the request is rejected with an explicit "meeting has not ended" signal and no segment list or pipeline status is returned.

---

### Edge Cases

- A meeting finishes with no one having spoken: the transcript is treated as legitimately empty rather than as a failure.
- Post-meeting transcript processing is still running when an administrator asks for the transcript: the response is shaped by whatever is persisted at that instant and carries a pipeline status of `Processing`; the caller is not blocked and is not promised eventual delivery through the transcript surface.
- The post-meeting transcription pipeline has exhausted its retries for a meeting: the administrator's transcript response carries a pipeline status of `Failed`, which lets an administrator distinguish pipeline failure from a meeting with no speech or from an as-yet-unstarted run.
- A legacy or mis-configured realtime provider still emits a live-transcription status signal: the system does not re-expose it as a product capability.
- A meeting host who is not an organization administrator attempts to read the transcript for a meeting they themselves created: access is denied; the meeting role does not confer transcript access.
- Downstream summarization must not start before the post-meeting transcript is complete: the amendment must signal that the summarization step waits for a "transcript-ready" signal rather than a "meeting-ended" signal.
- An administrator queries the transcript surface for a meeting that has not yet ended (scheduled for the future or currently in progress): the request is rejected with an explicit "meeting has not ended" signal rather than returning an empty-looking `NotStarted` response that would be indistinguishable from an ended-but-not-yet-processed meeting.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST NOT produce live transcription output (text, captions, or segments) during an active meeting.
- **FR-002**: System MUST NOT emit a live-transcription status signal (active, paused, error, progress, or equivalent) as part of the supported realtime notification contract.
- **FR-003**: System MUST preserve the existing realtime room lifecycle behavior (room started, participant joined, participant left, room finished), including the notifications that deliver those lifecycle changes to clients.
- **FR-004**: System MUST preserve the existing realtime meeting join, permission, and token-issuance behavior without modification by this amendment.
- **FR-005**: System MUST treat any transcript content as output of the post-meeting pipeline rather than output of the live session.
- **FR-006**: System MUST expose transcript data only through an administrator-scoped debugging surface; access MUST be limited to organization administrators and denied to all other callers regardless of their meeting role.
- **FR-007**: System MUST return every transcript response as a successful response carrying two elements: a list of transcript segments (possibly empty) and a coarse pipeline status indicator with exactly one of the values `NotStarted`, `Processing`, `Completed`, or `Failed`. An empty segment list MUST never be returned without an accompanying pipeline status so that administrators can distinguish "no speech / not started / in flight / failed" at a glance.
- **FR-008**: System MUST return transcript segments in chronological order of their start time when segments exist, regardless of the pipeline status indicator value.
- **FR-009**: System MUST NOT introduce, as part of this amendment, (a) new participant-facing workflows, (b) new participant-facing endpoints, (c) new participant-facing background jobs, or (d) new stored business records of any kind (participant-facing or internal). An administrator-scoped debugging surface is allowed under FR-006 because it is not participant-facing; no persistent record is introduced for it.
- **FR-010**: System MUST direct ordinary users to the post-meeting summary as the normal signal that post-meeting information is ready, rather than to the raw transcript.
- **FR-011**: System MUST arrange the downstream summarization step so that it begins only after the post-meeting transcript is complete, rather than immediately when the meeting ends.
- **FR-012**: System MUST reject administrator transcript requests for meetings that have not yet ended (session-lifecycle status `Scheduled` or `InProgress`) with an explicit "meeting has not ended" signal; such requests MUST NOT return the normal segment-plus-status response shape, because a not-yet-ended meeting is a session-lifecycle state and not a post-meeting pipeline state.

### Key Entities

- **Realtime Meeting Session**: A live room with participants and a lifecycle; its behavior is preserved across this amendment. It no longer carries any transcription responsibility.
- **Lifecycle Event**: A room- or participant-level state change (room started, participant joined, participant left, room finished) that remains a first-class notification after this amendment.
- **Transcript Segment**: A post-meeting internal unit of transcribed speech with a speaker, a start/end time, and text. Its schema is unchanged; what changes is that it is now written only by the post-meeting pipeline and read only by an administrator-scoped debugging surface.
- **Meeting Summary**: The post-meeting artifact that ordinary users consume; after this amendment, summary readiness — not transcript readiness — is the user-facing end-of-meeting signal.
- **Transcript Pipeline Status** (response-only): A coarse enumeration — `NotStarted`, `Processing`, `Completed`, `Failed` — that accompanies every administrator transcript response. Derived from the state of the post-meeting pipeline at read time; not stored independently.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Across all realtime-session test scenarios, zero live-transcription outputs (text, captions, or status signals) are produced during an active meeting.
- **SC-002**: 100% of the pre-existing room and participant lifecycle scenarios continue to pass after the amendment with no regression in the lifecycle notifications delivered to clients.
- **SC-003**: Zero new participant-facing workflows, endpoints, background jobs, or stored business records are added by this amendment.
- **SC-004**: Transcript access by non-administrator callers is denied in 100% of test cases, including when the caller is the host of the meeting being requested.
- **SC-005**: Every administrator transcript response across test scenarios carries a pipeline status of exactly one of `NotStarted`, `Processing`, `Completed`, or `Failed`, and the status matches the actual pipeline state for that meeting in 100% of cases; zero responses collapse a `Failed` state into a state that looks indistinguishable from `NotStarted` or `Processing`.
- **SC-006**: In 100% of end-to-end post-meeting runs, the summarization step does not begin until after the post-meeting transcript is complete.
- **SC-007**: Administrator transcript requests made for meetings that have not yet ended (status `Scheduled` or `InProgress`) are rejected with an explicit "meeting has not ended" signal in 100% of test cases, and none of those requests return a segment list or pipeline-status value.

## Assumptions

- Phase 4.5 is an amendment to an existing phase rather than a new end-user feature; its primary deliverable is a documented scope change and the enforcement of that change in the supported product surface.
- The post-meeting transcription pipeline that actually produces segments is defined and delivered by a later phase; this specification only depends on that pipeline existing and eventually persisting segments.
- The realtime infrastructure established in Phase 4 (join tokens, webhook handling, permissions) is considered frozen and is not altered by this amendment.
- "Organization administrator" is treated as a pre-existing role on the platform; this amendment does not define how that role is established, only that it is the sole role permitted to read the transcript surface.
- The post-meeting summary is the user-facing end-of-meeting deliverable; this amendment treats its existence as a dependency rather than redefining it.
- A per-read audit trail for the administrator transcript surface is explicitly out of scope for this phase. No audit-record entity, no per-read audit write path, and no audit-review surface are introduced by this phase. If a future phase decides a forensic trail is required, it re-opens the decision rather than inheriting one from here.

## Dependencies

- The Phase 4 realtime pipeline (join, permissions, room lifecycle, webhook handling) must be in place; this amendment modifies its scope but does not rebuild it.
- A post-meeting transcription pipeline must be planned or delivered to produce the transcript segments that the administrator-scoped debugging surface reads.
- A post-meeting summarization step must exist and must be the artifact ordinary users consume after a meeting.
