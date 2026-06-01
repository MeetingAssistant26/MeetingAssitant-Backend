using FluentAssertions;
using MeetingAssistant.Features.LiveSession.Handlers;
using MeetingAssistant.Features.LiveSession.Jobs;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.LiveSession.Models.Events;
using MeetingAssistant.Features.LiveSession.Services;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Xunit;

namespace tests.Integration.LiveSession
{
    public class PostMeetingPipelineTests
    {
        [Fact]
        public async Task ParticipantAudioReadyPipeline_ShouldWriteTranscriptThenSummary()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);

            var aliceId = db.SeedUser("Alice");
            var bobId = db.SeedUser("Bob");

            db.AddParticipant(meetingId, orgId, aliceId);
            db.AddParticipant(meetingId, orgId, bobId);

            var aliceKey = $"tracks/{meetingId}/{aliceId}.ogg";
            var bobKey = $"tracks/{meetingId}/{bobId}.ogg";

            db.DbContext.ParticipantAudioTracks.AddRange(
                new ParticipantAudioTrack
                {
                    MeetingId = meetingId,
                    OrganizationId = orgId,
                    ParticipantUserId = aliceId,
                    Status = ParticipantAudioTrackStatus.Available,
                    StorageObjectKey = aliceKey
                },
                new ParticipantAudioTrack
                {
                    MeetingId = meetingId,
                    OrganizationId = orgId,
                    ParticipantUserId = bobId,
                    Status = ParticipantAudioTrackStatus.Available,
                    StorageObjectKey = bobKey
                });
            await db.DbContext.SaveChangesAsync();

            var stt = new StubSttService(new Dictionary<string, TrackTranscriptionResult>
            {
                [aliceKey] = new(
                    "whisper-large-v3",
                    new[]
                    {
                        new TranscriptSegment(aliceId, 60_000, 62_000, "action item follow-up", 0.9),
                        new TranscriptSegment(aliceId, 0, 1_500, "project kickoff", 0.95)
                    }),
                [bobKey] = new(
                    "whisper-large-v3",
                    new[]
                    {
                        new TranscriptSegment(bobId, 30_000, 32_000, "timeline update", 0.91)
                    })
            });

            var summarizer = new StubSummarizerService(new SummaryResult(
                "Summary: kickoff, timeline update, action item follow-up.",
                "gpt-4o-mini",
                123,
                45));

            var jobs = new FakeBackgroundJobClient();
            var summaryHandler = new GenerateMeetingSummaryHandler(jobs);
            var publisher = new CollectingPublisher(async (notification, ct) =>
            {
                if (notification is MeetingTranscriptReadyEvent transcriptReadyEvent)
                {
                    await summaryHandler.Handle(transcriptReadyEvent, ct);
                }
            });

            var transcriptHandler = new GenerateMeetingTranscriptHandler(jobs);
            await transcriptHandler.Handle(
                new ParticipantAudioReadyEvent(meetingId, orgId, DateTime.UtcNow),
                CancellationToken.None);

            jobs.CreatedJobs.Should()
                .ContainSingle(x => x.Type == typeof(GenerateMeetingTranscriptJob));

            var transcriptJob = new GenerateMeetingTranscriptJob(
                db.DbContext,
                stt,
                publisher,
                NullLogger<GenerateMeetingTranscriptJob>.Instance);

            await transcriptJob.RunAsync(meetingId, orgId);

            jobs.CreatedJobs.Should()
                .ContainSingle(x => x.Type == typeof(GenerateMeetingSummaryJob));

            var summaryJob = new GenerateMeetingSummaryJob(
                db.DbContext,
                summarizer,
                NullLogger<GenerateMeetingSummaryJob>.Instance);

            await summaryJob.RunAsync(meetingId, orgId);

            var transcript = db.DbContext.MeetingTranscripts.Single(x => x.MeetingId == meetingId);
            var lines = transcript.FullText.Split(Environment.NewLine, StringSplitOptions.None);

            lines.Should().Equal(
                "[00:00:00 Alice] project kickoff",
                "[00:00:30 Bob] timeline update",
                "[00:01:00 Alice] action item follow-up");

            transcript.SttModel.Should().Be("whisper-large-v3");

            var summary = db.DbContext.MeetingSummaries.Single(x => x.MeetingId == meetingId);
            summary.SummaryText.Should().Be("Summary: kickoff, timeline update, action item follow-up.");
            summary.LlmModel.Should().Be("gpt-4o-mini");
            summary.PromptTokens.Should().Be(123);
            summary.CompletionTokens.Should().Be(45);
            summarizer.LastTranscript.Should().Be(transcript.FullText);

            publisher.Notifications
                .OfType<MeetingTranscriptReadyEvent>()
                .Should()
                .ContainSingle(x => x.MeetingId == meetingId);
        }

        [Fact]
        public async Task TranscriptGeneration_ShouldNormalizeSttOffsetsAgainstRoomActivationTime()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);
            var roomStartedAtUtc = new DateTime(2026, 05, 15, 12, 00, 00, DateTimeKind.Utc);

            var meeting = db.DbContext.Meetings.Single(x => x.Id == meetingId);
            meeting.RoomActivatedAtUtc = roomStartedAtUtc;

            var aliceId = db.SeedUser("Alice");
            var bobId = db.SeedUser("Bob");

            db.AddParticipant(meetingId, orgId, aliceId);
            db.AddParticipant(meetingId, orgId, bobId);

            const string aliceTrackSid = "TR_ALICE";
            const string bobTrackSid = "TR_BOB";
            var aliceKey = $"tracks/mtg-{meetingId}/user:{aliceId}/track-{aliceTrackSid}.ogg";
            var bobKey = $"tracks/mtg-{meetingId}/user:{bobId}/track-{bobTrackSid}.ogg";

            db.DbContext.SessionEvents.AddRange(
                new SessionEvent
                {
                    MeetingId = meetingId,
                    OrganizationId = orgId,
                    ExternalEventId = $"room-started:{meetingId}",
                    EventType = SessionEventType.RoomStarted,
                    PayloadJson = "{}",
                    OccurredAtUtc = roomStartedAtUtc,
                    ProcessedAtUtc = roomStartedAtUtc
                },
                new SessionEvent
                {
                    MeetingId = meetingId,
                    OrganizationId = orgId,
                    ExternalEventId = $"track-published:{meetingId}:{aliceTrackSid}",
                    EventType = SessionEventType.TrackPublished,
                    ParticipantUserId = aliceId,
                    PayloadJson = "{}",
                    OccurredAtUtc = roomStartedAtUtc,
                    ProcessedAtUtc = roomStartedAtUtc
                },
                new SessionEvent
                {
                    MeetingId = meetingId,
                    OrganizationId = orgId,
                    ExternalEventId = $"participant-joined:{meetingId}:{bobId}",
                    EventType = SessionEventType.ParticipantJoined,
                    ParticipantUserId = bobId,
                    PayloadJson = "{}",
                    OccurredAtUtc = roomStartedAtUtc.AddSeconds(63),
                    ProcessedAtUtc = roomStartedAtUtc.AddSeconds(63)
                },
                new SessionEvent
                {
                    MeetingId = meetingId,
                    OrganizationId = orgId,
                    ExternalEventId = $"track-published:{meetingId}:{bobTrackSid}",
                    EventType = SessionEventType.TrackPublished,
                    ParticipantUserId = bobId,
                    PayloadJson = "{}",
                    OccurredAtUtc = roomStartedAtUtc.AddSeconds(64),
                    ProcessedAtUtc = roomStartedAtUtc.AddSeconds(64)
                });

            db.DbContext.ParticipantAudioTracks.AddRange(
                new ParticipantAudioTrack
                {
                    MeetingId = meetingId,
                    OrganizationId = orgId,
                    ParticipantUserId = aliceId,
                    Status = ParticipantAudioTrackStatus.Available,
                    StorageObjectKey = aliceKey
                },
                new ParticipantAudioTrack
                {
                    MeetingId = meetingId,
                    OrganizationId = orgId,
                    ParticipantUserId = bobId,
                    Status = ParticipantAudioTrackStatus.Available,
                    StorageObjectKey = bobKey
                });
            await db.DbContext.SaveChangesAsync();

            var stt = new StubSttService(new Dictionary<string, TrackTranscriptionResult>
            {
                [aliceKey] = new(
                    "whisper-large-v3",
                    [new TranscriptSegment(aliceId, 1_000, 4_000, "hello", 0.95)]),
                [bobKey] = new(
                    "whisper-large-v3",
                    [new TranscriptSegment(bobId, 0, 4_000, "hey", 0.91)])
            });

            var transcriptJob = new GenerateMeetingTranscriptJob(
                db.DbContext,
                stt,
                new CollectingPublisher(),
                NullLogger<GenerateMeetingTranscriptJob>.Instance);

            await transcriptJob.RunAsync(meetingId, orgId);

            var transcript = db.DbContext.MeetingTranscripts.Single(x => x.MeetingId == meetingId);
            var lines = transcript.FullText.Split(Environment.NewLine, StringSplitOptions.None);
            lines.Should().Equal(
                "[00:00:01 Alice] hello",
                "[00:01:04 Bob] hey");

            var segments = DeserializePersistedSegments(transcript.SegmentsJson);
            var alice = segments.Single(x => x.ParticipantUserId == aliceId);
            alice.TrackRelativeStartMs.Should().Be(1_000);
            alice.TrackRelativeEndMs.Should().Be(4_000);
            alice.RoomRelativeStartMs.Should().Be(1_000);
            alice.RoomRelativeEndMs.Should().Be(4_000);
            alice.AbsoluteStartUtc.Should().Be(roomStartedAtUtc.AddSeconds(1));
            alice.AbsoluteEndUtc.Should().Be(roomStartedAtUtc.AddSeconds(4));

            var bob = segments.Single(x => x.ParticipantUserId == bobId);
            bob.StartMs.Should().Be(64_000);
            bob.EndMs.Should().Be(68_000);
            bob.TrackRelativeStartMs.Should().Be(0);
            bob.TrackRelativeEndMs.Should().Be(4_000);
            bob.RoomRelativeStartMs.Should().Be(64_000);
            bob.RoomRelativeEndMs.Should().Be(68_000);
            bob.AbsoluteStartUtc.Should().Be(roomStartedAtUtc.AddSeconds(64));
            bob.AbsoluteEndUtc.Should().Be(roomStartedAtUtc.AddSeconds(68));
            bob.TimestampOffsetSource.Should().Be("track_published");
        }

        private static IReadOnlyList<PersistedSegmentForTest> DeserializePersistedSegments(string json)
            => JsonSerializer.Deserialize<List<PersistedSegmentForTest>>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];

        private sealed record PersistedSegmentForTest(
            int Version,
            Guid ParticipantUserId,
            Guid ParticipantAudioTrackId,
            long TrackRelativeStartMs,
            long TrackRelativeEndMs,
            long RoomRelativeStartMs,
            long RoomRelativeEndMs,
            DateTime? AbsoluteStartUtc,
            DateTime? AbsoluteEndUtc,
            long StartMs,
            long EndMs,
            string Text,
            double? AvgLogProb,
            string TimestampOffsetSource);
    }
}
