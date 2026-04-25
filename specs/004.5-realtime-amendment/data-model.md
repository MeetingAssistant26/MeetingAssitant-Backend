# Phase 1 Data Model: Opus Realtime Amendment

**Source spec**: [spec.md](./spec.md)  
**Source plan**: [plan.md](./plan.md)

## Entities added by this phase

**None.**

FR-009 forbids new stored business records, and Q3 forbids an audit-record entity. Every persisted entity this phase touches is read-only and pre-existing.

## Entities referenced (read-only)

### `Meeting` (pre-existing — [Features/Meetings/Models/Meeting.cs](../../MeetingAssistant/Features/Meetings/Models/Meeting.cs))

Used to enforce the FR-012 precondition and to scope transcript lookups.

| Field | Purpose in this phase |
|---|---|
| `Id` | Lookup key (from route). |
| `OrganizationId` | Tenant isolation — matched against caller's `[EnforceOrgAccess]` claim. |
| `Status` (`MeetingStatus`) | If `InProgress`, the endpoint returns 409 (FR-012). Otherwise the read proceeds. |

**`MeetingStatus`** values (see [Features/Meetings/Models/MeetingStatus.cs](../../MeetingAssistant/Features/Meetings/Models/MeetingStatus.cs)) mapped to endpoint behavior:

| `MeetingStatus` | Endpoint behavior |
|---|---|
| `Scheduled` | 409 Conflict — "Meeting has not started." (Same problem family as `InProgress` — the transcript has no source material yet.) |
| `InProgress` | 409 Conflict — "Meeting is still in progress." (FR-012) |
| `Completed` | 200 OK with `(segments, pipelineStatus)`. |
| `Cancelled` | 200 OK with `(segments=[], pipelineStatus=NotStarted)`. The meeting ended; no STT will ever run; the admin sees an honest empty response. |
| `Failed` | 200 OK with `(segments=[], pipelineStatus=NotStarted)`. Same treatment as `Cancelled`. |

> Rationale: the spec explicitly addresses `InProgress`. `Scheduled` is analogous (meeting has produced no post-meeting state) and deserves the same clear rejection rather than a misleading `NotStarted` response. The alternative of folding `Scheduled` into `NotStarted` would mean an administrator hitting a future meeting gets an empty body that looks identical to an already-ended meeting whose pipeline hasn't fired — that reintroduces the exact ambiguity Q1 fixed.

### `MeetingParticipant` (pre-existing)

Not read by this phase. The FR-006 OrgAdmin check deliberately ignores meeting-level roles.

## Forward reference (Phase 5.5 — NOT added here)

### `TranscriptSegment` (Phase 5.5)

| Field | Notes |
|---|---|
| `Id` (Guid) | Identity. |
| `MeetingId` (Guid, FK) | Tenant-scoped lookup key. |
| `OrganizationId` (Guid) | Set so the global query filter applies. |
| `SpeakerUserId` (Guid, FK) | Speaker attribution from the participant-audio track that produced the segment. |
| `StartTime` / `EndTime` (TimeSpan) | STT timing output. |
| `Text` (string) | STT text output. |
| `SequenceNumber` (int) | Ordering within a participant's track. |

Added and migrated by Phase 5.5. The endpoint this phase ships reads this entity only through `ITranscriptReadService`, so Phase 5.5 can land the entity + a real reader behind the same interface without changing the controller.

## New response-only types (Contracts/Responses)

### `TranscriptPipelineStatus` (enum, response-only)

```
enum TranscriptPipelineStatus
{
    NotStarted = 0,
    Processing = 1,
    Completed  = 2,
    Failed     = 3,
}
```

- Not persisted. Derived at read time from domain state by whichever implementation of `ITranscriptReadService` is registered.
- Serialized as the string name (`"NotStarted"`, …) in JSON for stable contract semantics — never as the integer. This is enforced by the System.Text.Json default or an explicit `JsonStringEnumConverter` if not already applied globally.

### `TranscriptSegmentView` (record, response-only)

```
public sealed record TranscriptSegmentView(
    Guid   Id,
    Guid   SpeakerUserId,
    TimeSpan StartTime,
    TimeSpan EndTime,
    string Text,
    int    SequenceNumber);
```

- Projected from `TranscriptSegment` by `ITranscriptReadService` once Phase 5.5 lands.
- Today the projection returns an empty list from `StubTranscriptReadService`.

### `TranscriptDebugResponse` (record, response-only)

```
public sealed record TranscriptDebugResponse(
    IReadOnlyList<TranscriptSegmentView> Segments,
    TranscriptPipelineStatus              PipelineStatus);
```

**Invariants enforced in code (contract test territory):**

- `Segments` is non-null in every response (use `Array.Empty<TranscriptSegmentView>()` rather than `null`).
- `PipelineStatus` is always one of the four enum values; no sentinel.
- Segments MUST be ordered by `StartTime` ascending (FR-008) regardless of `PipelineStatus`.

## Service contract

### `ITranscriptReadService`

```
public interface ITranscriptReadService
{
    Task<Result<TranscriptDebugResponse>> ReadAsync(
        Guid organizationId,
        Guid meetingId,
        CancellationToken cancellationToken);
}
```

Contract-level guarantees:

- If the meeting does not exist inside `organizationId` → returns `Result.NotFound(...)` (will serialize to 404 via `ToProblem`).
- If the meeting status is `Scheduled` or `InProgress` → returns `Result.Conflict(...)` (409). The service is the *only* layer that inspects `MeetingStatus`; the controller stays dumb.
- Otherwise returns `Result.Success(new TranscriptDebugResponse(segments, status))` with `segments` ordered by `StartTime` ascending.

### `StubTranscriptReadService` (this phase's implementation)

- Loads the meeting; applies the `NotFound` / `Conflict` rules above.
- For `Completed` / `Cancelled` / `Failed` meetings returns `(segments: [], pipelineStatus: NotStarted)` unconditionally.
- Zero state of its own. Zero persistence writes. Zero Hangfire interaction.

## State transitions

None added. The amendment only reads `Meeting.Status` and (eventually, via Phase 5.5) `TranscriptSegment`. It introduces no lifecycle of its own.

## Validation rules

No new request validation — the endpoint has no request body and only two route parameters, both Guid-constrained by ASP.NET routing (`{orgId:guid}`, `{meetingId:guid}`).
