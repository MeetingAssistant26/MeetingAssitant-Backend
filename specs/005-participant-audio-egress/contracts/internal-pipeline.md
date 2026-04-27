# Contract: Internal Pipeline — `IngestParticipantAudioJob`

**Scope**: Internal background job (Hangfire). Not exposed to external callers.  
**Source**: [plan.md](../plan.md) · [data-model.md](../data-model.md)

## Job Signature

```csharp
[AutomaticRetry(Attempts = 3)]
public async Task RunAsync(
    Guid trackId,
    string egressSourceUrl,
    CancellationToken cancellationToken = default)
```

## Preconditions

- `trackId` references an existing `ParticipantAudioTrack` row.
- The track's `Status` is not already `Available` or `Failed` (idempotent early-exit).

## Post-conditions

### Happy path

- Track `Status` transitions `Pending` → `Downloading` → `Available`.
- `StorageObjectKey` is set to `tracks/{MeetingId}/{ParticipantUserId}.ogg`.
- `SizeBytes` is populated from the upload response.
- If all tracks for the meeting are now terminal, a `ParticipantAudioReadyEvent` is published exactly once.

### Time-ceiling path (new in Phase 5)

- If `DateTime.UtcNow - track.CreatedAtUtc > 8 minutes`, the job skips download/upload.
- Track `Status` is immediately set to `Failed`.
- If all tracks for the meeting are now terminal, a `ParticipantAudioReadyEvent` is published exactly once.

### Failure path

- On download/upload failure, Hangfire retries up to 3 times with exponential backoff.
- If all retries exhaust OR the 8-minute ceiling is reached, track `Status` is set to `Failed`.
- If all tracks for the meeting are now terminal, a `ParticipantAudioReadyEvent` is published exactly once.

## Idempotency

- Re-running the job for an `Available` or `Failed` track is a no-op (early return).
- Re-running for a `Pending` or `Downloading` track after the 8-minute ceiling is a fast-path to `Failed`.
- The readiness event is emitted via `SessionEvents` unique constraint on `ExternalEventId = "participant-audio-ready:{meetingId}"`.

---

# Contract: Internal Service — `IPipelineStateService`

**Scope**: Internal service seam. No public HTTP contract in this phase.  
**Source**: [plan.md](../plan.md) · [data-model.md](../data-model.md)

## Method

```csharp
Task<PipelineStateView?> GetStateAsync(Guid meetingId, CancellationToken cancellationToken);
```

## Response — `PipelineStateView`

```
{
  "meetingId": "...",
  "expectedParticipantCount": 3,
  "tracks": [
    {
      "participantUserId": "...",
      "status": "Available",
      "createdAtUtc": "...",
      "terminalAtUtc": "...",
      "storageObjectKey": "tracks/{MeetingId}/{ParticipantUserId}.ogg",
      "sizeBytes": 1024567
    }
  ],
  "readyEventFired": true,
  "allTracksTerminal": true
}
```

### Invariants

- `ExpectedParticipantCount` = count of `SessionEvent` rows for this meeting with `EventType = ParticipantJoined`. Falls back to `Tracks.Count` if no `ParticipantJoined` events exist.
- `AllTracksTerminal` is true iff every track has `Status` = `Available` or `Failed`.
- `ReadyEventFired` is true iff a `SessionEvent` row exists with `EventType = ParticipantAudioReady`.
- Returns `null` if the meeting has zero tracks AND zero `ParticipantJoined` events.
