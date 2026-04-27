# Known Bugs & Deferred Issues — LiveSession Pipeline

**Last updated**: 2026-04-27
**Branch**: `main`
**Scope**: Phase 5 (Participant Audio Egress & Storage) + Phase 5.5 (Post-Meeting STT)

---

## Deferred Bugs

### Bug #1: Fire-and-Forget Egress Start — Silent Failures

| | |
|---|---|
| **Location** | `WebhookService.cs:165` — `TrackPublished` handler |
| **Code** | `_ = _egressService.StartTrackEgressAsync(...)` (discarded task) |
| **Impact** | If `StartTrackEgressAsync` throws (LiveKit Egress service down, invalid API key, network failure), the exception is unobserved. The meeting proceeds with no audio recording, and the backend has no record of the failure. |
| **Reproduction** | Stop LiveKit Egress service; start a meeting; unmute microphone. Check logs — no error. Check MinIO — no files. |
| **Workaround** | Manually verify MinIO has files after meeting ends. |
| **Planned Fix** | Unspecified. Needs design: fire-and-forget is intentional for webhook handler responsiveness. Alternative: enqueue a background job that retries egress start with backoff, and marks the track `Failed` if retries exhaust. |
| **MVP Risk** | **Medium** — meetings may silently produce no audio. Mitigated by manual testing before production. |

---

### Bug #2: Orphan Audio Files on Mute/Unmute/Reconnect

| | |
|---|---|
| **Location** | `EgressService.cs` — `StartTrackEgressAsync` |
| **Root Cause** | No idempotency check before calling `StartTrackEgressAsync`. Every `TrackPublished` event (reconnection, mute→unmute cycle) starts a new egress job, creating a new file in MinIO. Old files from previous egresses are orphaned. |
| **Impact** | Storage bloat. Multiple `.ogg` files per participant per meeting in MinIO. The backend only tracks the *latest* successful egress (via the last `egress_ended` webhook), but earlier files remain in storage forever. |
| **Reproduction** | Join meeting; mute; unmute; mute; unmute; leave. Check MinIO: 3+ files for one participant. |
| **Workaround** | Periodic MinIO lifecycle policy to delete files older than N days with no DB reference. |
| **Planned Fix** | Unspecified. Requires: (a) query MinIO for existing file before starting egress, or (b) add a `TrackEgressSession` table to track active egresses per `(meetingId, trackSid)` and deduplicate. |
| **MVP Risk** | **Low** — storage cost is negligible for MVP scale. |

---

### Bug #3: STT Timestamp Misalignment Across Per-Track Recordings

| | |
|---|---|
| **Location** | `GenerateMeetingTranscriptJob.cs:90-93` |
| **Code** | `allSegments.OrderBy(x => x.StartMs).ThenBy(x => x.EndMs)` |
| **Root Cause** | `StartMs` is relative to the **beginning of each participant's individual audio file**, not the meeting start time. Participant A's `StartMs=0` = when A unmuted. Participant B's `StartMs=0` = when B unmuted. These are different absolute times. Sorting by `StartMs` alone produces incorrect cross-participant ordering. |
| **Impact** | Transcript may show Speaker B's first words appearing **before** Speaker A's first words, even though A spoke first in real-time. The merged transcript is semantically wrong for multi-speaker meetings where participants join/unmute at different times. |
| **Reproduction** | Meeting with 2 participants. Participant A joins at T+0, unmutes at T+5. Participant B joins at T+2, unmutes at T+10. Both speak. Transcript will show both starting at `[00:00:00]`, with B potentially sorted before A if B's segment count is lower. |
| **Workaround** | None. The transcript is wrong but the LLM summary may still be coherent because it reads the full text holistically. |
| **Planned Fix** | Compute per-participant time offset using `SessionEvent` rows: `ParticipantJoined.OccurredAtUtc - RoomStarted.OccurredAtUtc`. Add this offset to each segment's `StartMs`/`EndMs` before the cross-participant sort. **Blocked on Bug #6** ( SQLite/Postgres unique violation handling) being fixed first for reliable concurrent webhook processing. |
| **MVP Risk** | **High** for accurate transcripts; **Low** for summary quality. |

---

### Bug #4: SQLite/Postgres Unique Violation Handling Gap

| | |
|---|---|
| **Location** | `WebhookService.cs:381-385` and `IngestParticipantAudioJob.cs:195-199` |
| **Code** | `IsUniqueViolation` only checks for `PostgresException` with SQL state `23505`. |
| **Root Cause** | SQLite (used in integration tests and local dev) throws `Microsoft.Data.Sqlite.SqliteException` for unique constraint violations, not `PostgresException`. The catch block doesn't handle this. |
| **Impact** | In production (PostgreSQL), duplicate concurrent webhooks are gracefully handled. In SQLite tests, a true race condition (two `egress_ended` webhooks for the same participant arriving simultaneously before `SaveChanges`) would throw an **unhandled exception** instead of returning `Result.Success()`. |
| **Reproduction** | Extremely difficult — requires true concurrent `egress_ended` webhooks within the same `ProcessAsync` DB transaction window. The `alreadyProcessed` check prevents most duplicates, but not all. |
| **Workaround** | None needed for production (PostgreSQL). For SQLite dev, avoid concurrent webhook testing. |
| **Planned Fix** | User will fix manually. Requires adding a check for `Microsoft.Data.Sqlite.SqliteException` with error code `19` (SQLITE_CONSTRAINT) in `IsUniqueViolation`. |
| **MVP Risk** | **Low** — only affects dev/test environment. Production uses PostgreSQL. |

---

## Accepted Design Deviations (Not Bugs)

### Deviation #A: Direct S3 Write vs. Download-Upload Pipeline

| | |
|---|---|
| **Spec Assumption** | LiveKit Cloud Egress → temporary cloud storage → backend downloads via presigned URL → uploads to MinIO. |
| **Reality** | Self-hosted LiveKit + Egress writes directly to MinIO via `DirectFileOutput`. `IngestParticipantAudioJob` never downloads or uploads — it only parses the S3 object key from the webhook URL. |
| **Impact** | `IStorageService.UploadFromUrlAsync` exists but is **unused** in the pipeline. The `Downloading` status was dead code (removed). Breaks LiveKit Cloud compatibility. |
| **Acceptance** | Approved for local-dev MVP. Self-hosted stack is the target deployment. |

### Deviation #B: No `PipelineStateService` / Operational Visibility

| | |
|---|---|
| **Spec Requirement** | FR-010: Queryable pipeline state for administrators (track count, statuses, barrier fired, expected vs. actual participants). |
| **Reality** | Not implemented. No API endpoint, no `PipelineStateView` DTO, no `IPipelineStateService`. |
| **Impact** | Operators must query the database directly to diagnose stuck transfers or missing readiness events. |
| **Acceptance** | Explicitly deferred. Purely diagnostic; no user-facing feature depends on it. |

### Deviation #C: No SignalR Notifications

| | |
|---|---|
| **Spec Requirement** | Phase 4/5.5 listed SignalR notifications for transcription status (`TranscriptionStarted`, `TranscriptionProgress`, `TranscriptionCompleted`, `TranscriptionFailed`). |
| **Reality** | Entire SignalR subsystem (`LiveSessionHub`, `LiveSessionNotifier`, `ILiveSessionNotifier`) was deleted from `LiveSession`. The pipeline is silent. |
| **Impact** | No real-time UI updates for transcription progress. Clients must poll or wait for Phase 6 summary completion. |
| **Acceptance** | Explicitly deleted. SignalR was only used for live transcription status, which is no longer applicable in the post-meeting STT architecture. |

### Deviation #D: Monolithic Transcript Job vs. Orchestrator + Per-Track Jobs

| | |
|---|---|
| **Spec Requirement** | Phase 5.5: `SttOrchestratorJob` fans out to N `TranscribeParticipantAudioJob` with a join barrier. |
| **Reality** | Single `GenerateMeetingTranscriptJob` with `Parallel.ForEachAsync` internally. No separate per-track jobs, no `ParticipantTranscriptReadyEvent`. |
| **Impact** | Simpler, fewer moving parts. Functionally equivalent. Less granular progress visibility. |
| **Acceptance** | Approved. The end result (complete transcript) is identical. |

---

## Risk Matrix

| # | Issue | Severity | Likelihood | Production Impact | Fix Effort |
|---|---|---|---|---|---|
| 1 | Fire-and-forget egress silent failure | **High** | Medium | Meeting has no audio | Medium |
| 2 | Orphan files on mute/unmute | Low | High | Storage bloat | Medium |
| 3 | STT timestamp misalignment | **High** | **High** | Wrong transcript order | Medium |
| 4 | SQLite unique violation gap | Low | Very Low | Test flakiness only | Low |
| A | Direct S3 write (no LiveKit Cloud) | Medium | N/A | Can't use LiveKit Cloud | High |
| B | No `PipelineStateService` | Low | N/A | Manual DB queries | Medium |
| C | No SignalR notifications | Low | N/A | No real-time progress UI | High |
| D | Monolithic STT job | Low | N/A | None (same output) | Low |

---

## Next Actions

| Priority | Action | Owner |
|---|---|---|
| P1 | Fix Bug #3 (STT timestamp misalignment) — compute per-participant offset from `SessionEvent` timestamps | TBD |
| P2 | Fix Bug #1 (fire-and-forget egress) — add retry-enqueue or observability | TBD |
| P2 | Fix Bug #2 (orphan files) — deduplicate egress starts per `(meetingId, trackSid)` | TBD |
| P3 | Implement `PipelineStateService` + admin/debug endpoint (FR-010) | TBD |
| P3 | Make STT pipeline swappable to LiveKit Cloud (download-upload architecture) | TBD |
| P4 | Fix Bug #4 (SQLite exception handling) | User |

---

## How to Detect These in Production

### Bug #1 (Silent Egress Failures)
```sql
-- After a meeting ends, verify expected track count matches actual
SELECT m.id, m.title,
  (SELECT COUNT(*) FROM "MeetingParticipants" WHERE "MeetingId" = m.id) as expected,
  (SELECT COUNT(*) FROM "ParticipantAudioTracks" WHERE "MeetingId" = m.id AND status = 2) as actual_available
FROM "Meetings" m
WHERE m.status = 2  -- Completed
  AND m."ScheduledEndUtc" < NOW() - INTERVAL '1 hour'
HAVING expected > actual_available;
```

### Bug #2 (Orphan Files)
```bash
# Compare MinIO file count vs. DB track count per meeting
mc ls --recursive local/meeting-recordings/tracks/ | grep "mtg:" | wc -l
# vs.
SELECT COUNT(*) FROM "ParticipantAudioTracks" WHERE status = 2;
# If MinIO count >> DB count, you have orphans.
```

### Bug #3 (Timestamp Misalignment)
```sql
-- Check if transcript segments for a meeting have overlapping StartMs from different speakers
-- (a symptom of incorrect ordering)
WITH segments AS (
  SELECT jsonb_array_elements(segments_json::jsonb) as seg
  FROM "MeetingTranscripts"
  WHERE "MeetingId" = 'guid'::uuid
)
SELECT (seg->>'StartMs')::bigint as start_ms,
       (seg->>'ParticipantUserId')::uuid as speaker
FROM segments
ORDER BY start_ms;
-- Look for: two different speakers both starting at ~0ms (should be offset)
```
