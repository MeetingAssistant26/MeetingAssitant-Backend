# 004-realtime-pipeline — Additional Fixes (Revised)

> **Supersedes the prior version of this file.** Naming corrected to match the
> actual flow (LiveKit → MinIO transfer, no local disk), Phase 5.5 (post-meeting
> STT + summary) build-out specified, and persistence model finalized.
>
> Decisions locked in conversation 2026-04-25:
> - **Egress mode:** track-based (one audio file per participant — **no MP4/video**).
> - **Egress destination:** indirect — LiveKit Cloud bucket → backend transfer job → MinIO.
> - **STT:** OpenAI-compatible provider (Whisper API or compatible — Groq, etc.).
> - **LLM summarizer:** any model exposing the OpenAI-compatible API.
> - **Per-track STT** (each track is one speaker — no diarization needed).
> - **Persistence:** `MeetingTranscript` (full pre-summary text + segments JSON) + `MeetingSummary` (LLM output) — both 1:1 with `Meeting`, overwrite on regenerate. **No `TranscriptSegments` table.**
> - **Migration path:** new migration on top of `20260421222726_AddLiveSession`.
>
> Source of truth (plan): [docs/implementation-plan.md](../../docs/implementation-plan.md) — Phase 4.5, Phase 5, Phase 5.5.

---

## Target end-to-end flow

```
Meeting ends
   │
   ▼
LiveKit Track Egress → LiveKit Cloud bucket
   (one OGG/Opus audio file per participant; no video)
   │
   ▼  one webhook per track (or one webhook with FileResults[])
WebhookService
   - per FileResult: parse `participantIdentity` → ParticipantUserId
   - upsert ParticipantAudioTrack row (Status=Pending)
   - enqueue IngestParticipantAudioJob(trackId)
   │
   ▼  Hangfire (parallel — N tracks fan out)
IngestParticipantAudioJob(trackId)
   - load ParticipantAudioTrack
   - Status: Pending → Downloading
   - StorageService.UploadFromUrlAsync(egressUrl, "tracks/{meetingId}/{participantUserId}.ogg")
   - persist StorageObjectKey, DurationSeconds, SizeBytes
   - Status: Downloading → Available
   - join-barrier: if all tracks for this MeetingId are now Available
       → fire ParticipantAudioReadyEvent EXACTLY ONCE
   │
   ▼
GenerateMeetingTranscriptHandler  (Phase 5.5 — NEW)
   - per track: stream from MinIO → OpenAI-compatible STT → segments
       (chunk to ≤25 MB if needed)
   - tag each segment with the track's ParticipantUserId
   - merge segments chronologically by start time
   - render FullText with [HH:MM:SS Speaker] prefixes
   - upsert MeetingTranscript (overwrite)
   - publish MeetingTranscriptReadyEvent
   │
   ▼
GenerateMeetingSummaryHandler  (Phase 5.5 — NEW)
   - load MeetingTranscript.FullText
   - call LLM (OpenAI-compatible) with summarization prompt
   - upsert MeetingSummary (overwrite)
```

---

## Naming corrections vs. prior version of this file

| Prior name (misleading)         | New name                       | Why                                                              |
|---------------------------------|--------------------------------|------------------------------------------------------------------|
| `LocalFilePath`                 | `StorageObjectKey`             | File never lands on local disk — `StorageService` streams LiveKit URL → MinIO directly. |
| `DownloadParticipantAudioJob`   | `IngestParticipantAudioJob`    | The job *transfers* (LiveKit → MinIO); "Download" implies local fs. |
| `Recording`                     | `ParticipantAudioTrack`        | One row per participant track, not per meeting.                  |
| `TranscriptSegments` table      | *(dropped — now `SegmentsJson` jsonb on `MeetingTranscript`)* | Single meeting-scoped read, no cross-segment queries. |

---

## Section A — Phase 4 / Phase 5 fixes (drift correction)

### A1. Entity rename — `Recording` → `ParticipantAudioTrack`

File: [MeetingAssistant/Features/LiveSession/Models/Recording.cs](../../MeetingAssistant/Features/LiveSession/Models/Recording.cs)

**Current** (one row per meeting):
```csharp
public class Recording : BaseEntity, IHasOrganizationId
{
    public Guid MeetingId { get; set; }
    public Meeting Meeting { get; set; } = default!;
    public Guid OrganizationId { get; set; }
    public string? FilePath { get; set; }
    public RecordingStatus Status { get; set; } = RecordingStatus.Pending;
}
```

**Replace with** (one row per participant track):
```csharp
public class ParticipantAudioTrack : BaseEntity, IHasOrganizationId
{
    public Guid MeetingId { get; set; }
    public Meeting Meeting { get; set; } = default!;

    public Guid OrganizationId { get; set; }

    public Guid ParticipantUserId { get; set; }

    public ParticipantAudioTrackStatus Status { get; set; } = ParticipantAudioTrackStatus.Pending;

    /// <summary>MinIO object key — what STT reads. Null until upload completes.</summary>
    public string? StorageObjectKey { get; set; }

    public double? DurationSeconds { get; set; }
    public long?   SizeBytes { get; set; }
}
```

**Indexes** (in `OnModelCreating`):
- Unique: `(MeetingId, ParticipantUserId)` — one track per participant per meeting.
- Query: `(MeetingId, Status)` — drives the join-barrier check.

**Notes:**
- The LiveKit egress URL is **transient** — passed into the job and not persisted on the entity (the URL becomes useless once the file is in MinIO).
- File: rename `Recording.cs` → `ParticipantAudioTrack.cs`.

### A2. Status enum — rename + extend

File: [MeetingAssistant/Features/LiveSession/Models/RecordingStatus.cs](../../MeetingAssistant/Features/LiveSession/Models/RecordingStatus.cs)

**Replace with:**
```csharp
public enum ParticipantAudioTrackStatus
{
    Pending     = 0,  // row created, awaiting transfer
    Downloading = 1,  // job is transferring LiveKit → MinIO
    Available   = 2,  // file in MinIO, ready for STT
    Failed      = 3
}
```

Rename file to `ParticipantAudioTrackStatus.cs`.

### A3. Job rewrite — `DownloadRecordingJob` → `IngestParticipantAudioJob`

File: [MeetingAssistant/Features/LiveSession/Jobs/DownloadRecordingJob.cs](../../MeetingAssistant/Features/LiveSession/Jobs/DownloadRecordingJob.cs)

**Current signature:**
```csharp
public async Task RunAsync(Guid meetingId, string sourceCloudUrl, CancellationToken ct = default)
```
Assumes one row per meeting and a hardcoded `recordings/{meetingId}.mp4` key.

**Replace with:**
```csharp
public async Task RunAsync(Guid trackId, string egressSourceUrl, CancellationToken ct = default)
```

Behaviour:
1. Load `ParticipantAudioTrack` by id. If missing → log + return.
2. If status already `Available` or `Failed` → log + return (idempotent).
3. Set status `Pending` → `Downloading`. Save.
4. Compute object key: `tracks/{MeetingId}/{ParticipantUserId}.ogg` (extension determined by LiveKit egress audio container — confirm OGG/Opus in egress config; if MP4/AAC, use `.m4a`).
5. Call `IStorageService.UploadFromUrlAsync(egressSourceUrl, objectKey, ct)`.
6. On success: persist `StorageObjectKey`, `SizeBytes`, set status `Available`.
7. On failure: set status `Failed`, save, log, **do not throw** (per-track failures must not poison the meeting — partial summaries are still useful).
8. **Join-barrier check** (idempotent — see A4):
   - `SELECT COUNT(*) FROM ParticipantAudioTracks WHERE MeetingId = @id AND Status NOT IN (Available, Failed)` → if 0:
     - check `SessionEvents` for an existing `ParticipantAudioReady` event for this `MeetingId` (idempotency).
     - if none: insert one (unique-constraint protected) and dispatch `ParticipantAudioReadyEvent` via MediatR.

Rename file to `IngestParticipantAudioJob.cs`.

**Failure semantics:** the barrier fires when all tracks are in a terminal state (`Available` OR `Failed`), not just `Available`. Phase 5.5 then summarizes whatever is `Available` and logs the missing speakers. Pure-failure case (zero `Available`) is allowed to fire the event — handler short-circuits gracefully.

### A4. Idempotency — exactly-one `ParticipantAudioReadyEvent`

Hangfire runs jobs in parallel, so two tracks finishing concurrently must not both fire the event.

**Mechanism:** add a new `SessionEventType.ParticipantAudioReady` and use the existing `SessionEvent` table's idempotency key. Insert with `(MeetingId, EventType=ParticipantAudioReady)` as a unique tuple inside the same DB transaction that flips the last track's status. The unique constraint guarantees exactly one writer wins; the loser swallows the unique-violation and skips the dispatch.

If the existing `SessionEvent` schema doesn't have a unique index that covers this, add it in the same migration as the entity rename.

### A5. Webhook handler — per-track fan-out

File: [MeetingAssistant/Features/LiveSession/Services/WebhookService.cs](../../MeetingAssistant/Features/LiveSession/Services/WebhookService.cs) — lines 138–152, plus `UpsertRecordingAsync` at lines 190–216 and `ResolveSourceCloudUrl` at lines 218–226.

**Current `EgressEnded` handler** reads only `FileResults[0].Location` and upserts a single `Recording` row.

**Replace with — iterate `FileResults`:**

```csharp
case SessionEventType.EgressEnded:
{
    var fileResults = webhookEvent.EgressInfo?.FileResults ?? [];
    var egressOk    = webhookEvent.EgressInfo?.Status == EgressStatus.EgressComplete;

    foreach (var file in fileResults)
    {
        var participantUserId = await ResolveParticipantUserIdAsync(
            meeting.Id, file.ParticipantIdentity, cancellationToken);
        if (participantUserId is null)
        {
            _logger.LogWarning("Skipping track — could not resolve participant. MeetingId={MeetingId} Identity={Identity}",
                meeting.Id, file.ParticipantIdentity);
            continue;
        }

        var status = egressOk && !string.IsNullOrWhiteSpace(file.Location)
            ? ParticipantAudioTrackStatus.Pending
            : ParticipantAudioTrackStatus.Failed;

        var trackId = await UpsertParticipantAudioTrackAsync(
            meeting.Id, meeting.OrganizationId, participantUserId.Value, status, cancellationToken);

        if (status == ParticipantAudioTrackStatus.Pending)
        {
            ingestEnqueues.Add((trackId, file.Location!));
        }
    }
    break;
}
```

Then after `SaveChangesAsync`, dispatch:
```csharp
foreach (var (trackId, sourceUrl) in ingestEnqueues)
{
    _backgroundJobClient.Enqueue<IngestParticipantAudioJob>(
        job => job.RunAsync(trackId, sourceUrl, CancellationToken.None));
}
```

Rename `UpsertRecordingAsync` → `UpsertParticipantAudioTrackAsync` and have it return the row id (`Guid`) so the caller can enqueue the job.

**LiveKit identity parsing already exists** at [WebhookService.cs:310-325](../../MeetingAssistant/Features/LiveSession/Services/WebhookService.cs#L310-L325) — `user:{guid}` convention. Reuse it via the existing `ResolveParticipantUserIdAsync` helper.

`SessionEventType.RecordingStarted` handler currently upserts a `Recording` row — drop it. With per-track egress there's nothing to upsert until `FileResults` arrive on `EgressEnded`. Keep the lifecycle event itself (logging only).

### A6. DbContext

File: [MeetingAssistant/Infrastructure/Persistence/DbContext/ApplicationDbContext.cs](../../MeetingAssistant/Infrastructure/Persistence/DbContext/ApplicationDbContext.cs) — line 38.

- `DbSet<Recording> Recordings` → `DbSet<ParticipantAudioTrack> ParticipantAudioTracks`.
- `DbSet<MeetingTranscript> MeetingTranscripts` (new — see B1).
- `DbSet<MeetingSummary> MeetingSummaries` (new — see B2).
- Update the global `OrganizationId` query filter registration for the renamed entity.
- Register indexes from A1, B1, B2 in `OnModelCreating` (or in fluent config classes — match repo convention).

### A7. Domain event

File: [MeetingAssistant/Features/LiveSession/Models/Events/LiveSessionEvents.cs](../../MeetingAssistant/Features/LiveSession/Models/Events/LiveSessionEvents.cs)

Add:
```csharp
public record ParticipantAudioReadyEvent(
    Guid MeetingId,
    Guid OrganizationId,
    DateTime OccurredAtUtc) : IDomainEvent;

public record MeetingTranscriptReadyEvent(
    Guid MeetingId,
    Guid OrganizationId,
    DateTime OccurredAtUtc) : IDomainEvent;
```

`MeetingTranscriptReadyEvent` chains transcript → summary so each handler stays single-purpose.

### A8. LiveKit egress config — track mode

File: [MeetingAssistant/Features/LiveSession/LiveSessionDI.cs](../../MeetingAssistant/Features/LiveSession/LiveSessionDI.cs) (and any code that initiates egress via the LiveKit Server SDK).

- If egress is initiated **from code** → switch the request from Room Composite to Track Egress (audio only — explicitly disable video). One file per `participantIdentity`. Confirm container/codec is OGG/Opus (default) — used for the file extension in A3.
- If egress is initiated **from LiveKit Cloud dashboard** (no code-initiated egress today) → it's a config change there. Document it in [livekit-sandbox-runbook.md](livekit-sandbox-runbook.md).
- Either way: **video is disabled** — confirms "no MP4" requirement.

---

## Section B — Phase 5.5 build-out (NEW — post-meeting STT + summary)

### B1. New entity — `MeetingTranscript`

File: `MeetingAssistant/Features/LiveSession/Models/MeetingTranscript.cs` (new)

```csharp
public class MeetingTranscript : BaseEntity, IHasOrganizationId
{
    public Guid MeetingId { get; set; }
    public Meeting Meeting { get; set; } = default!;

    public Guid OrganizationId { get; set; }

    /// <summary>Chronologically merged transcript with [HH:MM:SS Speaker] prefixes — fed to the LLM.</summary>
    public string FullText { get; set; } = string.Empty;

    /// <summary>jsonb. Array of { ParticipantUserId, StartMs, EndMs, Text, Confidence? } ordered by StartMs.</summary>
    public string SegmentsJson { get; set; } = "[]";

    public string SttModel { get; set; } = string.Empty;

    public DateTime GeneratedAtUtc { get; set; }
}
```

**Indexes:** unique on `MeetingId` (1:1 with `Meeting`, overwrite on regenerate).

**Why JSON, not a `TranscriptSegments` table:** the only consumers are (a) the LLM (reads `FullText`) and (b) the FE (loads the full transcript per meeting). No cross-segment queries are needed. A jsonb column avoids N rows × M tracks of relational overhead and keeps the read path single-row.

### B2. New entity — `MeetingSummary`

File: `MeetingAssistant/Features/LiveSession/Models/MeetingSummary.cs` (new)

```csharp
public class MeetingSummary : BaseEntity, IHasOrganizationId
{
    public Guid MeetingId { get; set; }
    public Meeting Meeting { get; set; } = default!;

    public Guid OrganizationId { get; set; }

    public string SummaryText { get; set; } = string.Empty;

    public string LlmModel { get; set; } = string.Empty;

    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }

    public DateTime GeneratedAtUtc { get; set; }
}
```

**Indexes:** unique on `MeetingId` (1:1, overwrite on regenerate).

**Action items deferred** — out of scope for this iteration. If/when needed, add a `MeetingActionItem` child entity rather than embedding in `SummaryText`.

### B3. OpenAI-compatible provider config

File: `MeetingAssistant/Features/LiveSession/Infrastructure/OpenAiCompatibleOptions.cs` (new)

```csharp
public class OpenAiCompatibleOptions
{
    public ProviderConfig Stt { get; set; } = new();
    public ProviderConfig Llm { get; set; } = new();

    public class ProviderConfig
    {
        public string BaseUrl { get; set; } = string.Empty;   // e.g. https://api.openai.com/v1
        public string ApiKey  { get; set; } = string.Empty;
        public string Model   { get; set; } = string.Empty;   // e.g. "whisper-large-v3" / "gpt-4o-mini"
    }
}
```

Bind in `Program.cs` from `OpenAiCompatible:Stt:*` / `OpenAiCompatible:Llm:*` config keys. Same provider (OpenAI) or split (Groq Whisper + OpenAI GPT) — both work without code change.

### B4. STT service — `ISttService`

File: `MeetingAssistant/Features/LiveSession/Services/ISttService.cs` + `SttService.cs` (new)

```csharp
public interface ISttService
{
    Task<TrackTranscriptionResult> TranscribeTrackAsync(
        Guid participantUserId,
        string storageObjectKey,
        CancellationToken ct = default);
}

public record TrackTranscriptionResult(
    string Model,
    IReadOnlyList<TranscriptSegment> Segments);

public record TranscriptSegment(
    Guid ParticipantUserId,
    long StartMs,
    long EndMs,
    string Text,
    double? Confidence);
```

**Implementation notes:**
- HTTP `POST {BaseUrl}/audio/transcriptions` with `multipart/form-data`: `file`, `model`, `response_format=verbose_json`, `timestamp_granularities[]=segment`.
- `verbose_json` returns `segments[]` with `start`, `end`, `text`, `avg_logprob` (use as confidence proxy).
- Stream the file from MinIO (`IMinioClient.GetObjectAsync` with a stream callback) into the multipart payload — do not buffer to memory or local disk.
- **Chunking for >25 MB tracks:** OpenAI's transcriptions endpoint caps at 25 MB. Strategy:
  1. If `SizeBytes <= 24_000_000` → single request.
  2. Otherwise: split using ffmpeg by time (e.g., 10-minute chunks at silence boundaries via `silencedetect` filter, fall back to fixed-time split). Submit chunks sequentially. Add the chunk's offset to each returned segment's `StartMs`/`EndMs` to keep timestamps absolute against the original track.
  3. ffmpeg dependency: add to the Docker image used to run Hangfire workers — note in [docker-compose.yml](../../docker-compose.yml).
- Tag every returned segment with the supplied `participantUserId` (per-track STT means the speaker is known).

### B5. LLM service — `ISummarizerService`

File: `MeetingAssistant/Features/LiveSession/Services/ISummarizerService.cs` + `SummarizerService.cs` (new)

```csharp
public interface ISummarizerService
{
    Task<SummaryResult> SummarizeAsync(string fullTranscript, CancellationToken ct = default);
}

public record SummaryResult(
    string SummaryText,
    string Model,
    int? PromptTokens,
    int? CompletionTokens);
```

**Implementation notes:**
- HTTP `POST {BaseUrl}/chat/completions` with system + user messages.
- Prompt template: `Resources/Prompts/MeetingSummarizer.md` (new) — keeps the prompt out of code so it can be tuned without recompiling. Loaded once at startup.
- Capture `usage.prompt_tokens` / `usage.completion_tokens` from the response for cost tracking.

### B6. Transcript handler — `GenerateMeetingTranscriptHandler`

File: `MeetingAssistant/Features/LiveSession/Handlers/GenerateMeetingTranscriptHandler.cs` (new)

`INotificationHandler<ParticipantAudioReadyEvent>`:
1. Load all `ParticipantAudioTrack` rows for the meeting where `Status = Available` and `StorageObjectKey IS NOT NULL`. (Tracks with `Failed` status are skipped — partial summary is acceptable; logged.)
2. For each track: `_sttService.TranscribeTrackAsync(track.ParticipantUserId, track.StorageObjectKey)`. Run **in parallel** with a bounded concurrency (e.g., `Parallel.ForEachAsync` with `MaxDegreeOfParallelism = 4`) to avoid hammering the STT provider.
3. Collect segments from all tracks; sort by `StartMs` ascending.
4. Render `FullText`: each segment as `[HH:MM:SS Display Name] Text` — resolve `ParticipantUserId` → display name from `MeetingParticipant` / `User`. Cache the lookup outside the per-segment loop.
5. Upsert `MeetingTranscript` (delete-existing-then-insert OR `ON CONFLICT (MeetingId) DO UPDATE` via `ExecuteUpdate` — pick whichever matches repo convention) with `FullText`, `SegmentsJson`, `SttModel`, `GeneratedAtUtc=UtcNow`.
6. Publish `MeetingTranscriptReadyEvent`.

**Failure handling:** if zero tracks are `Available`, log and exit without writing a transcript or publishing `MeetingTranscriptReadyEvent`. If STT fails for a subset of tracks, write the transcript with whatever succeeded — log the failures. If STT fails for all, log and exit (no transcript, no summary).

### B7. Summary handler — `GenerateMeetingSummaryHandler`

File: `MeetingAssistant/Features/LiveSession/Handlers/GenerateMeetingSummaryHandler.cs` (new)

`INotificationHandler<MeetingTranscriptReadyEvent>`:
1. Load `MeetingTranscript.FullText` for the meeting.
2. `_summarizerService.SummarizeAsync(fullText)`.
3. Upsert `MeetingSummary` with `SummaryText`, `LlmModel`, token counts, `GeneratedAtUtc=UtcNow`.

**Why two events instead of inlining:** isolates failure domains (STT failure ≠ LLM failure), lets us re-run summary with a different prompt without re-running STT, and keeps each handler's responsibility small.

### B8. Hangfire orchestration

The two handlers above should run on Hangfire (not synchronously inside the MediatR dispatch from the ingest job) — STT and LLM calls are slow and external. Pattern:
- `ParticipantAudioReadyEvent` MediatR handler → `_backgroundJobClient.Enqueue<GenerateMeetingTranscriptJob>(j => j.RunAsync(meetingId, ct))`.
- That Hangfire job calls into `GenerateMeetingTranscriptHandler` logic (or move the logic directly into the job — same code, different shell).
- Same pattern for `MeetingTranscriptReadyEvent` → `GenerateMeetingSummaryJob`.

### B9. DI registration

File: [MeetingAssistant/Features/LiveSession/LiveSessionDI.cs](../../MeetingAssistant/Features/LiveSession/LiveSessionDI.cs)

Add:
```csharp
services.Configure<OpenAiCompatibleOptions>(configuration.GetSection("OpenAiCompatible"));
services.AddScoped<ISttService, SttService>();
services.AddScoped<ISummarizerService, SummarizerService>();
services.AddHttpClient("openai-stt");
services.AddHttpClient("openai-llm");
```

---

## Section C — Migration

**Path:** new migration on top of the committed `20260421222726_AddLiveSession`. Do **not** edit that migration in place.

Create `dotnet ef migrations add Phase5_AudioTracksAndTranscripts` covering:

1. **Drop** old `Recordings` table (branch is WIP, no production rows; if any local dev rows exist they're disposable).
2. **Create** `ParticipantAudioTracks` (A1):
   - Columns per A1.
   - Unique index on `(MeetingId, ParticipantUserId)`.
   - Index on `(MeetingId, Status)`.
   - FK `MeetingId → Meetings(Id)` cascade-on-delete.
3. **Create** `MeetingTranscripts` (B1):
   - Columns per B1, `SegmentsJson` as `jsonb`.
   - Unique index on `MeetingId`.
   - FK `MeetingId → Meetings(Id)` cascade-on-delete.
4. **Create** `MeetingSummaries` (B2):
   - Columns per B2.
   - Unique index on `MeetingId`.
   - FK `MeetingId → Meetings(Id)` cascade-on-delete.
5. **Add** `SessionEventType.ParticipantAudioReady` enum value — no schema change if `SessionEvents.EventType` is stored as `int`/`smallint` (it is). Confirm the existing unique constraint on `SessionEvents` covers `(MeetingId, EventType)` — if not, add it (required by A4 for exactly-once event dispatch).

After running `dotnet ef migrations add`, hand-edit `Up`/`Down` if EF emits unwanted artifacts (e.g., legacy `Recordings` references in the snapshot).

---

## Section D — Tests

### D1. Replace existing track-of-one tests

- `tests/Unit/LiveSession/DownloadRecordingJobTests.cs` → rewrite as `IngestParticipantAudioJobTests.cs`:
  - Loads one track, calls `UploadFromUrlAsync`, persists key, flips status `Pending → Downloading → Available`.
  - Idempotency: second invocation on an already-`Available` track is a no-op.
  - Failure: `UploadFromUrlAsync` throws → status `Failed`, no exception bubbles.
- `tests/Integration/LiveSession/RecordingHandoffTests.cs` → rewrite as `ParticipantAudioHandoffTests.cs`:
  - N participants → N webhook `FileResults` → N tracks created in `Pending` → N jobs enqueued → N transfers → all tracks `Available` → exactly one `ParticipantAudioReadyEvent` fires.

### D2. New tests

- `tests/Unit/LiveSession/JoinBarrierIdempotencyTests.cs`:
  - Two tracks, both finish concurrently (simulate via `Task.WhenAll`) → exactly one `ParticipantAudioReadyEvent` is dispatched. Use the `SessionEvent` unique constraint as the gate.
- `tests/Integration/LiveSession/PostMeetingPipelineTests.cs`:
  - Stub `ISttService` returning canned segments per track.
  - Stub `ISummarizerService` returning canned summary.
  - End-to-end: `ParticipantAudioReadyEvent` → `MeetingTranscript` row exists with merged + ordered `FullText` → `MeetingTranscriptReadyEvent` → `MeetingSummary` row exists.
- `tests/Unit/LiveSession/SttChunkingTests.cs`:
  - Track > 25 MB → chunked into N requests → segment timestamps offset correctly so the merged track is contiguous.
- `tests/Unit/LiveSession/PartialFailureTests.cs`:
  - 3 tracks; track 2's STT throws → transcript still written from tracks 1 + 3, summary still produced, failure logged.

### D3. Stop-using tests

Anything referencing `Recording`, `RecordingStatus.Completed`, `DownloadRecordingJob` directly — delete after the rewrites land.

---

## Section E — Spec / docs alignment

- [specs/004-realtime-pipeline/tasks.md](tasks.md) — currently dirty in `git status`. Rewrite the Phase 4/5 sections to match this document; add a Phase 5.5 section enumerating B1–B9.
- [specs/004-realtime-pipeline/spec.md](spec.md) — confirm "audio-only egress, no video" is explicit. Confirm persistence model (`MeetingTranscript` + `MeetingSummary`) is documented.
- [specs/004-realtime-pipeline/quickstart.md](quickstart.md) — add OpenAI-compatible provider env vars: `OpenAiCompatible__Stt__BaseUrl`, `__ApiKey`, `__Model`, same for `Llm`.
- [livekit-sandbox-runbook.md](livekit-sandbox-runbook.md) — document Track Egress (audio-only) configuration.
- [CLAUDE.md](../../CLAUDE.md) — update the "PostgreSQL via Npgsql with EF Core — new tables" line to drop `TranscriptSegments` and add `ParticipantAudioTracks`, `MeetingTranscripts`, `MeetingSummaries`.

---

## Suggested execution order

1. **Section A1–A2 + A6 + Section C migration** (entity rename + status enum + DbContext + new migration). One commit. Build must pass.
2. **A7** (new domain events). One commit.
3. **A3 + A4** (job rewrite + idempotency). One commit.
4. **A5** (webhook fan-out). One commit.
5. **A8** (LiveKit egress mode — config or code, see section).
6. **B1 + B2** (new entities — extend the migration created in step 1 OR add a follow-up migration, whichever is cleaner).
7. **B3 + B4 + B5 + B9** (provider config, STT service, summarizer service, DI). One commit.
8. **B6 + B7 + B8** (handlers + Hangfire wiring). One commit.
9. **D1 + D2** (test rewrites + new tests). One commit (or split per phase).
10. **Section E** (spec / docs / runbook updates). One commit.

Estimated effort: 3–4 days focused. Phase 4/5 corrections are surgical (~1 day); Phase 5.5 build-out is greenfield (~2–3 days including ffmpeg chunking and prompt tuning).

---

## Out of scope

- Action items extraction (deferred per 2026-04-25 decision).
- Summary regeneration history (`MeetingSummary` is overwrite-only).
- Live STT / live captions (v3.6 is post-meeting only).
- Phase 7 / Phase 7.5 work (reminders, agent-callable surface) — fresh-start territory, no drift to fix.
- Multi-tenant isolation hardening for MinIO — covered by the existing `OrganizationId` query filter on `ParticipantAudioTrack` / `MeetingTranscript` / `MeetingSummary`.
