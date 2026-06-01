using FluentAssertions;
using Livekit.Server.Sdk.Dotnet;
using MeetingAssistant.Features.LiveSession.Infrastructure;
using MeetingAssistant.Features.LiveSession.Handlers;
using MeetingAssistant.Features.LiveSession.Jobs;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.LiveSession.Models.Events;
using MeetingAssistant.Features.LiveSession.Services;
using MeetingAssistant.Features.Meetings.Jobs;
using MeetingAssistant.Features.Rag.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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

            db.AddAvailableAudioFragment(meetingId, orgId, aliceId, aliceKey, "TR_ALICE");
            db.AddAvailableAudioFragment(meetingId, orgId, bobId, bobKey, "TR_BOB");

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
                NullLogger<GenerateMeetingSummaryJob>.Instance,
                backgroundJobClient: jobs);

            await summaryJob.RunAsync(meetingId, orgId);

            jobs.CreatedJobs.Should()
                .Contain(x => x.Type == typeof(SuggestMeetingTagsJob),
                    "post-meeting tag suggestion should run after summary generation has both transcript and summary context");
            jobs.CreatedJobs.Should()
                .Contain(x => x.Type == typeof(ReindexMeetingKnowledgeJob),
                    "knowledge indexing remains separate and uses only confirmed tags until suggestions are confirmed");

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

            var aliceFragmentId = db.AddAvailableAudioFragment(
                meetingId,
                orgId,
                aliceId,
                aliceKey,
                aliceTrackSid);
            var bobFragmentId = db.AddAvailableAudioFragment(
                meetingId,
                orgId,
                bobId,
                bobKey,
                bobTrackSid);

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
            alice.ParticipantAudioFragmentId.Should().Be(aliceFragmentId);
            alice.TrackRelativeStartMs.Should().Be(1_000);
            alice.TrackRelativeEndMs.Should().Be(4_000);
            alice.RoomRelativeStartMs.Should().Be(1_000);
            alice.RoomRelativeEndMs.Should().Be(4_000);
            alice.AbsoluteStartUtc.Should().Be(roomStartedAtUtc.AddSeconds(1));
            alice.AbsoluteEndUtc.Should().Be(roomStartedAtUtc.AddSeconds(4));

            var bob = segments.Single(x => x.ParticipantUserId == bobId);
            bob.ParticipantAudioFragmentId.Should().Be(bobFragmentId);
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

        [Fact]
        public async Task TranscriptGeneration_ShouldUseAllAvailableFragmentsInRoomRelativeOrder()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);
            var roomStartedAtUtc = new DateTime(2026, 06, 01, 14, 00, 00, DateTimeKind.Utc);

            var meeting = db.DbContext.Meetings.Single(x => x.Id == meetingId);
            meeting.RoomActivatedAtUtc = roomStartedAtUtc;

            var aliceId = db.SeedUser("Alice");
            var bobId = db.SeedUser("Bob");

            db.AddParticipant(meetingId, orgId, aliceId);
            db.AddParticipant(meetingId, orgId, bobId);

            var aggregate = new ParticipantAudioTrack
            {
                MeetingId = meetingId,
                OrganizationId = orgId,
                ParticipantUserId = aliceId,
                Status = ParticipantAudioTrackStatus.Available,
                StorageObjectKey = $"tracks/mtg-{meetingId}/user:{aliceId}/track-TR_ALICE_LATE.ogg"
            };
            db.DbContext.ParticipantAudioTracks.Add(aggregate);
            await db.DbContext.SaveChangesAsync();

            var aliceEarlyKey = $"tracks/mtg-{meetingId}/user:{aliceId}/track-TR_ALICE_EARLY.ogg";
            var bobKey = $"tracks/mtg-{meetingId}/user:{bobId}/track-TR_BOB.ogg";
            var aliceLateKey = $"tracks/mtg-{meetingId}/user:{aliceId}/track-TR_ALICE_LATE.ogg";

            // Insert out of chronological order to prove room-relative ordering comes from
            // fragment timing, not database/aggregate ordering.
            var aliceLateFragmentId = db.AddAvailableAudioFragment(
                meetingId,
                orgId,
                aliceId,
                aliceLateKey,
                "TR_ALICE_LATE",
                roomStartedAtUtc.AddSeconds(20),
                aggregate.Id);
            var aliceEarlyFragmentId = db.AddAvailableAudioFragment(
                meetingId,
                orgId,
                aliceId,
                aliceEarlyKey,
                "TR_ALICE_EARLY",
                roomStartedAtUtc.AddSeconds(5),
                aggregate.Id);
            var bobFragmentId = db.AddAvailableAudioFragment(
                meetingId,
                orgId,
                bobId,
                bobKey,
                "TR_BOB",
                roomStartedAtUtc.AddSeconds(10));

            var stt = new StubSttService(new Dictionary<string, TrackTranscriptionResult>
            {
                [aliceEarlyKey] = new(
                    "whisper-large-v3",
                    [new TranscriptSegment(aliceId, 0, 1_000, "alice first fragment", 0.95)]),
                [bobKey] = new(
                    "whisper-large-v3",
                    [new TranscriptSegment(bobId, 0, 1_000, "bob middle fragment", 0.91)]),
                [aliceLateKey] = new(
                    "whisper-large-v3",
                    [new TranscriptSegment(aliceId, 0, 1_000, "alice rejoined fragment", 0.9)])
            });

            var transcriptJob = new GenerateMeetingTranscriptJob(
                db.DbContext,
                stt,
                new CollectingPublisher(),
                NullLogger<GenerateMeetingTranscriptJob>.Instance);

            await transcriptJob.RunAsync(meetingId, orgId);

            stt.Calls.Should().BeEquivalentTo([aliceEarlyKey, bobKey, aliceLateKey]);

            var transcript = db.DbContext.MeetingTranscripts.Single(x => x.MeetingId == meetingId);
            transcript.FullText.Split(Environment.NewLine, StringSplitOptions.None).Should().Equal(
                "[00:00:05 Alice] alice first fragment",
                "[00:00:10 Bob] bob middle fragment",
                "[00:00:20 Alice] alice rejoined fragment");

            var segments = DeserializePersistedSegments(transcript.SegmentsJson);
            segments.Select(x => x.ParticipantAudioFragmentId).Should().Equal(
                aliceEarlyFragmentId,
                bobFragmentId,
                aliceLateFragmentId);
            segments.Select(x => x.TimestampOffsetSource).Should().AllBeEquivalentTo("fragment_track_published");
        }

        [Fact]
        public async Task TranscriptGeneration_ShouldUseFragmentsCreatedFromWebhookAndEgressInputs()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);
            var roomStartedAtUtc = new DateTime(2026, 06, 01, 15, 30, 00, DateTimeKind.Utc);

            var aliceId = db.SeedUser("Alice");
            db.AddParticipant(meetingId, orgId, aliceId);

            var jobs = new FakeBackgroundJobClient();
            var webhookService = new WebhookService(
                db.DbContext,
                jobs,
                Options.Create(new LiveKitOptions { EgressHost = "http://egress" }),
                new FakeEgressService(),
                NullLogger<WebhookService>.Instance);

            var roomStarted = WithCreatedAt(
                WebhookEventFactory.RoomStarted(meetingId, "evt-room-start-webhook-transcript"),
                roomStartedAtUtc);
            (await webhookService.ProcessAsync(roomStarted, "{}")).IsSuccess.Should().BeTrue();

            const string earlyTrackSid = "TR_WEBHOOK_EARLY";
            const string lateTrackSid = "TR_WEBHOOK_LATE";
            var earlyTrack = WithCreatedAt(
                WebhookEventFactory.TrackPublished(meetingId, "evt-track-early", aliceId, earlyTrackSid),
                roomStartedAtUtc.AddSeconds(4));
            var lateTrack = WithCreatedAt(
                WebhookEventFactory.TrackPublished(meetingId, "evt-track-late", aliceId, lateTrackSid),
                roomStartedAtUtc.AddSeconds(18));

            (await webhookService.ProcessAsync(earlyTrack, "{}")).IsSuccess.Should().BeTrue();
            (await webhookService.ProcessAsync(lateTrack, "{}")).IsSuccess.Should().BeTrue();

            var earlySourceUrl = BuildEgressSourceUrl(meetingId, aliceId, earlyTrackSid);
            var lateSourceUrl = BuildEgressSourceUrl(meetingId, aliceId, lateTrackSid);

            var lateEgress = CreateEgressEndedWebhook(
                meetingId,
                "evt-egress-late",
                lateTrackSid,
                lateSourceUrl,
                roomStartedAtUtc.AddSeconds(18),
                roomStartedAtUtc.AddSeconds(26));
            var earlyEgress = CreateEgressEndedWebhook(
                meetingId,
                "evt-egress-early",
                earlyTrackSid,
                earlySourceUrl,
                roomStartedAtUtc.AddSeconds(4),
                roomStartedAtUtc.AddSeconds(12));

            // Deliver egress-ended webhooks out of chronological order to prove transcript order
            // comes from persisted fragment timing, not webhook delivery order.
            (await webhookService.ProcessAsync(lateEgress.Event, lateEgress.RawPayload)).IsSuccess.Should().BeTrue();
            (await webhookService.ProcessAsync(earlyEgress.Event, earlyEgress.RawPayload)).IsSuccess.Should().BeTrue();

            jobs.CreatedJobs.Where(x => x.Type == typeof(IngestParticipantAudioJob)).Should().HaveCount(2);

            var ingestPublisher = new CollectingPublisher();
            var ingestJob = new IngestParticipantAudioJob(
                db.DbContext,
                ingestPublisher,
                NullLogger<IngestParticipantAudioJob>.Instance);

            foreach (var job in jobs.CreatedJobs.Where(x => x.Type == typeof(IngestParticipantAudioJob)))
            {
                await ingestJob.RunFragmentAsync(
                    (Guid)job.Args[0],
                    (string)job.Args[1],
                    (long?)job.Args[2]);
            }

            var fragments = db.DbContext.ParticipantAudioFragments
                .Where(x => x.MeetingId == meetingId)
                .OrderBy(x => x.TrackPublishedAtUtc)
                .ToList();

            fragments.Should().HaveCount(2);
            fragments.Select(x => x.TrackSid).Should().Equal(earlyTrackSid, lateTrackSid);
            fragments.Should().OnlyContain(x => x.Status == ParticipantAudioFragmentStatus.Available);
            fragments.Select(x => x.StorageObjectKey).Should().Equal(
                ExtractExpectedObjectKey(earlySourceUrl),
                ExtractExpectedObjectKey(lateSourceUrl));

            var stt = new StubSttService(new Dictionary<string, TrackTranscriptionResult>
            {
                [ExtractExpectedObjectKey(earlySourceUrl)] = new(
                    "whisper-large-v3",
                    [new TranscriptSegment(aliceId, 0, 900, "alice before mute", 0.96)]),
                [ExtractExpectedObjectKey(lateSourceUrl)] = new(
                    "whisper-large-v3",
                    [new TranscriptSegment(aliceId, 0, 900, "alice after rejoin", 0.94)])
            });

            var transcriptJob = new GenerateMeetingTranscriptJob(
                db.DbContext,
                stt,
                new CollectingPublisher(),
                NullLogger<GenerateMeetingTranscriptJob>.Instance);

            await transcriptJob.RunAsync(meetingId, orgId);

            stt.Calls.Should().BeEquivalentTo([
                ExtractExpectedObjectKey(earlySourceUrl),
                ExtractExpectedObjectKey(lateSourceUrl)]);

            var transcript = db.DbContext.MeetingTranscripts.Single(x => x.MeetingId == meetingId);
            transcript.FullText.Split(Environment.NewLine, StringSplitOptions.None).Should().Equal(
                "[00:00:04 Alice] alice before mute",
                "[00:00:18 Alice] alice after rejoin");

            var segments = DeserializePersistedSegments(transcript.SegmentsJson);
            segments.Select(x => x.ParticipantAudioFragmentId).Should().Equal(fragments.Select(x => x.Id));
            segments.Select(x => x.TimestampOffsetSource).Should().AllBeEquivalentTo("fragment_track_published");
        }

        private static IReadOnlyList<PersistedSegmentForTest> DeserializePersistedSegments(string json)
            => JsonSerializer.Deserialize<List<PersistedSegmentForTest>>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];

        private static WebhookEvent WithCreatedAt(WebhookEvent webhookEvent, DateTime occurredAtUtc)
        {
            webhookEvent.CreatedAt = new DateTimeOffset(occurredAtUtc).ToUnixTimeSeconds();
            return webhookEvent;
        }

        private static string BuildEgressSourceUrl(Guid meetingId, Guid participantUserId, string trackSid)
            => $"http://minio:9000/recordings/tracks/mtg:{meetingId}/user:{participantUserId}/track-{trackSid}.wav";

        private static string ExtractExpectedObjectKey(string sourceUrl)
        {
            var uri = new Uri(sourceUrl);
            var segments = uri.AbsolutePath.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            var tracksIndex = Array.FindIndex(segments, x => x.Equals("tracks", StringComparison.OrdinalIgnoreCase));
            tracksIndex.Should().BeGreaterThanOrEqualTo(0);
            return string.Join('/', segments.Skip(tracksIndex));
        }

        private static (WebhookEvent Event, string RawPayload) CreateEgressEndedWebhook(
            Guid meetingId,
            string eventId,
            string trackSid,
            string sourceUrl,
            DateTime startedAtUtc,
            DateTime endedAtUtc)
        {
            var evt = new WebhookEvent
            {
                Event = "egress_ended",
                Id = eventId,
                CreatedAt = new DateTimeOffset(endedAtUtc).ToUnixTimeSeconds(),
                EgressInfo = new EgressInfo
                {
                    RoomName = $"mtg:{meetingId}",
                    Status = EgressStatus.EgressComplete
                }
            };

            var objectKey = ExtractExpectedObjectKey(sourceUrl);
            evt.EgressInfo.FileResults.Add(new Livekit.Server.Sdk.Dotnet.FileInfo
            {
                Filename = objectKey,
                Location = sourceUrl,
                Size = 12_345L
            });

            var rawPayload = JsonSerializer.Serialize(new
            {
                egressInfo = new
                {
                    egressId = $"EG_{trackSid}",
                    trackId = trackSid,
                    startedAt = new DateTimeOffset(startedAtUtc).ToUnixTimeSeconds(),
                    endedAt = new DateTimeOffset(endedAtUtc).ToUnixTimeSeconds(),
                    fileResults = new[]
                    {
                        new
                        {
                            filename = objectKey,
                            location = sourceUrl,
                            size = 12_345L
                        }
                    }
                }
            });

            return (evt, rawPayload);
        }

        private sealed record PersistedSegmentForTest(
            int Version,
            Guid ParticipantUserId,
            Guid? ParticipantAudioTrackId,
            Guid ParticipantAudioFragmentId,
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
