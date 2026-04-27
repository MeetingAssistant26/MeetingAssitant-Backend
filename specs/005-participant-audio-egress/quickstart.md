# Quickstart: Participant Audio Egress & Storage

This walkthrough verifies the Phase 5 hardened pipeline end-to-end. Phase 5 is an amendment to existing infrastructure — it hardens the join barrier, adds an 8-minute transfer ceiling, and introduces queryable pipeline state.

## Prerequisites

- `dotnet build` succeeds at the repo root.
- PostgreSQL + MinIO running (via `docker compose up -d postgres minio`).
- Backend running (`dotnet run --project MeetingAssistant/MeetingAssistant.csproj`).

## 1. Run the test suite

```bash
dotnet test tests/MeetingAssistant.Tests.Integration --filter "FullyQualifiedName~ParticipantAudioHandoffTests"
```

Expected: all tests pass, including:
- Happy path: 3 participants → 3 tracks → all Available → readiness event fires once.
- Retry ceiling: track older than 8 minutes auto-Fails.
- Late-arriving track: processed but does not re-trigger readiness event.
- Missing participant: barrier fires when existing tracks are all terminal (expected count is informational).

## 2. Verify the 8-minute ceiling (manual simulation)

Use the integration test harness to simulate a stale track:

```csharp
// In a test or REPL context with LiveSessionTestDb:
var db = await LiveSessionTestDb.CreateAsync();
var orgId = db.SeedOrganization();
var meetingId = db.SeedMeeting(orgId);
var participantId = db.SeedUser("stale-participant");
db.AddParticipant(meetingId, orgId, participantId);

// Manually create a track with CreatedAt 9 minutes ago
var track = new ParticipantAudioTrack
{
    MeetingId = meetingId,
    OrganizationId = orgId,
    ParticipantUserId = participantId,
    Status = ParticipantAudioTrackStatus.Pending,
    CreatedAtUtc = DateTime.UtcNow.AddMinutes(-9)
};
db.DbContext.ParticipantAudioTracks.Add(track);
db.DbContext.SaveChanges();

// Run the job
var job = new IngestParticipantAudioJob(
    db.DbContext,
    new FakeStorageService(),
    new CollectingPublisher(),
    NullLogger<IngestParticipantAudioJob>.Instance);

await job.RunAsync(track.Id, "https://example.com/fake.ogg");

// Assert: track is Failed without attempting download
var updated = db.DbContext.ParticipantAudioTracks.Find(track.Id);
updated.Status.Should().Be(ParticipantAudioTrackStatus.Failed);
```

## 3. Verify queryable pipeline state

```csharp
// After seeding a meeting with tracks in mixed states:
var service = new PipelineStateService(db.DbContext);
var state = await service.GetStateAsync(meetingId);

// Assert shape
state.Should().NotBeNull();
state.MeetingId.Should().Be(meetingId);
state.Tracks.Should().HaveCount(3);  // or however many tracks exist
state.AllTracksTerminal.Should().BeFalse();  // if any track is Pending/Downloading
state.ReadyEventFired.Should().BeFalse();    // if barrier hasn't fired yet
```

## 4. Manual verification — join barrier with missing participant

1. Create a meeting with 3 participants.
2. Simulate the live session: 2 participants join (`participant_joined` webhooks), 1 never joins.
3. Process `egress_ended` for only the 2 joined participants.
4. Assert:
   - 2 `ParticipantAudioTrack` rows created (both Pending).
   - `PipelineStateView.ExpectedParticipantCount == 2` (from `ParticipantJoined` events).
   - After ingest completes, `AllTracksTerminal == true`.
   - Readiness event fires (barrier is based on existing tracks, not expected count).

## 5. Verify idempotency — duplicate `egress_ended`

1. Process the same `egress_ended` webhook twice (same `ExternalEventId`).
2. Assert:
   - Only one `ParticipantAudioTrack` row per participant.
   - Only one `IngestParticipantAudioJob` enqueued per participant (Hangfire deduplication or webhook idempotency).
   - No duplicate MinIO uploads.

## 6. Post-conditions to confirm before declaring the phase done

- [ ] `IngestParticipantAudioJob` has the 8-minute time guard (search for `CreatedAtUtc + 8 minutes` or equivalent).
- [ ] `IngestParticipantAudioJob` has `[AutomaticRetry(Attempts = 3)]` attribute.
- [ ] `TryCreateParticipantAudioReadyEventAsync` uses `SessionEvents` for `ExpectedParticipantCount` in `PipelineStateView`.
- [ ] `PipelineStateService` is registered in `LiveSessionDI.cs`.
- [ ] Zero new migrations under `MeetingAssistant/Migrations/`.
- [ ] All `ParticipantAudioHandoffTests` pass, including new test cases for ceiling and late-arriving tracks.
