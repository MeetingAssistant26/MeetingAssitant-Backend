# Phase 1 Data Model: Participant Audio Egress & Storage

**Source spec**: [spec.md](./spec.md)  
**Source plan**: [plan.md](./plan.md)  
**Source research**: [research.md](./research.md)

## Entities added by this phase

**None.**

All entities required by Phase 5 already exist from prior phases. This phase makes **zero schema changes, zero migrations, zero new tables**.

## Entities referenced (read-only or amended behavior)

### `ParticipantAudioTrack` (pre-existing)

| Field | Notes |
|---|---|
| `Id` (Guid, PK) | Identity. |
| `MeetingId` (Guid, FK) | Lookup key; indexed with `ParticipantUserId` as unique composite. |
| `OrganizationId` (Guid) | Tenant isolation — global query filter applies. |
| `ParticipantUserId` (Guid) | The speaker this track belongs to. |
| `Status` (enum → int) | `Pending` / `Downloading` / `Available` / `Failed`. |
| `StorageObjectKey` (string, 500, nullable) | MinIO path: `tracks/{MeetingId}/{ParticipantUserId}.ogg`. |
| `DurationSeconds` (double?, nullable) | Not used in this phase. |
| `SizeBytes` (long?, nullable) | Populated on successful upload. |
| `CreatedAtUtc` (DateTime) | **Used by this phase** as the baseline for the 8-minute ceiling guard. |
| `UpdatedAtUtc` (DateTime) | Inherited from `BaseEntity`. |

**No schema changes.** The 8-minute ceiling reads `CreatedAtUtc` (already present). No new columns.

### `SessionEvent` (pre-existing)

| Field | Purpose in this phase |
|---|---|
| `MeetingId` | Lookup key. |
| `EventType` | `ParticipantJoined` rows are counted to derive the expected participant count for operational queries. `ParticipantAudioReady` row existence signals that the readiness event has already fired (idempotency guard). |
| `ExternalEventId` | `participant-audio-ready:{meetingId}` is the deduplication key for the readiness event (unique constraint enforced). |

**No schema changes.** Existing columns are sufficient.

### `Meeting` (pre-existing)

Read-only reference for `OrganizationId` lookup. No changes.

## New response-only types (not entities)

### `PipelineStateView` (record, response-only)

```
public sealed record PipelineStateView(
    Guid MeetingId,
    int ExpectedParticipantCount,
    IReadOnlyList<ParticipantAudioTrackView> Tracks,
    bool ReadyEventFired,
    bool AllTracksTerminal);

public sealed record ParticipantAudioTrackView(
    Guid ParticipantUserId,
    ParticipantAudioTrackStatus Status,
    DateTime CreatedAtUtc,
    DateTime? TerminalAtUtc,     // null if not terminal
    string? StorageObjectKey,
    long? SizeBytes);
```

**Invariants**:
- `ExpectedParticipantCount` = count of `SessionEvent` rows for this meeting with `EventType = ParticipantJoined`. If no `ParticipantJoined` events exist (e.g., tests that don't seed them), falls back to `Tracks.Count`.
- `AllTracksTerminal` is true iff every track has `Status = Available` or `Failed`.
- `ReadyEventFired` is true iff a `SessionEvent` row exists with `EventType = ParticipantAudioReady` for this meeting.

## Service contract

### `IPipelineStateService`

```csharp
public interface IPipelineStateService
{
    Task<PipelineStateView?> GetStateAsync(Guid meetingId, CancellationToken cancellationToken);
}
```

**Behavior**:
- Queries `SessionEvents` for `ParticipantJoined` count → `ExpectedParticipantCount`.
- Queries `ParticipantAudioTrack` rows for the meeting.
- Queries `SessionEvents` for `ParticipantAudioReady` existence → `ReadyEventFired`.
- Returns `null` if the meeting has zero tracks and zero `ParticipantJoined` events.

## State transitions

### `ParticipantAudioTrack` status lifecycle (amended)

```
Pending ──[IngestParticipantAudioJob starts]──> Downloading
    │                                              │
    │  [success]                                    │  [success]
    └──────────────> Available <───────────────────┘
    │
    │  [failure after 3 retries OR 8-min ceiling]
    └──────────────> Failed
```

**Key amendment**: The `Failed` transition now has **two triggers**:
1. Transfer fails after 3 Hangfire retry attempts (existing behavior).
2. Transfer is abandoned because `CreatedAtUtc + 8 minutes < UtcNow` (new behavior in this phase).

### Readiness event (`ParticipantAudioReady`) emission (amended)

**Old behavior**: Fired when `COUNT(ParticipantAudioTrack WHERE MeetingId=X AND Status NOT IN (Available, Failed)) == 0`.

**New behavior**: Fires when `COUNT(ParticipantAudioTrack WHERE MeetingId=X AND Status NOT IN (Available, Failed)) == 0` (same condition). The expected participant count (from `SessionEvents`) is tracked for operational visibility but is **not** a gate condition — the barrier fires when all tracks that exist are terminal. The `ExpectedParticipantCount` in `PipelineStateView` surfaces any discrepancy (e.g., "2 of 3 expected tracks present") for administrator diagnosis.

**Rationale**: The spec's edge case explicitly states that a participant who joined but never unmuted "does not block the pipeline for other participants." If the expected count were a hard gate, a missing track would permanently stall the barrier. The expected count is informational for FR-010 operational visibility; the actual gate is the terminal status of all existing tracks.
