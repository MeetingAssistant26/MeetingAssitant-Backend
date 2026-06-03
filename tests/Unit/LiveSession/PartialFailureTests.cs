using FluentAssertions;
using MeetingAssistant.Features.LiveSession.Jobs;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.LiveSession.Models.Events;
using MeetingAssistant.Features.LiveSession.Models.PostProcessing;
using MeetingAssistant.Features.LiveSession.Services;
using MeetingAssistant.Features.LiveSession.Services.PostProcessing;
using Microsoft.Extensions.Logging.Abstractions;
using tests.Integration.LiveSession;
using Xunit;

namespace tests.Unit.LiveSession
{
    public class PartialFailureTests
    {
        [Fact]
        public async Task TranscriptAndSummary_ShouldStillBeGenerated_WhenOneTrackTranscriptionFails()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);

            var user1 = db.SeedUser("Alice");
            var user2 = db.SeedUser("Bob");
            var user3 = db.SeedUser("Carol");

            db.AddParticipant(meetingId, orgId, user1);
            db.AddParticipant(meetingId, orgId, user2);
            db.AddParticipant(meetingId, orgId, user3);

            var key1 = $"tracks/{meetingId}/{user1}.ogg";
            var key2 = $"tracks/{meetingId}/{user2}.ogg";
            var key3 = $"tracks/{meetingId}/{user3}.ogg";

            db.AddAvailableAudioFragment(meetingId, orgId, user1, key1, "TR_USER_1");
            var failedFragmentId = db.AddAvailableAudioFragment(meetingId, orgId, user2, key2, "TR_USER_2");
            db.AddAvailableAudioFragment(meetingId, orgId, user3, key3, "TR_USER_3");

            var stt = new StubSttService(
                new Dictionary<string, TrackTranscriptionResult>
                {
                    [key1] = new(
                        "whisper-large-v3",
                        [new TranscriptSegment(user1, 0, 1_000, "alice update", 0.95)]),
                    [key3] = new(
                        "whisper-large-v3",
                        [new TranscriptSegment(user3, 2_000, 3_000, "carol decision", 0.9)])
                },
                new Dictionary<string, Exception>
                {
                    [key2] = new InvalidOperationException("simulated stt failure")
                });

            var publisher = new CollectingPublisher();
            var transcriptJob = new GenerateMeetingTranscriptJob(
                db.DbContext,
                stt,
                publisher,
                NullLogger<GenerateMeetingTranscriptJob>.Instance);

            await transcriptJob.RunAsync(meetingId, orgId);

            var transcript = db.DbContext.MeetingTranscripts.Single(x => x.MeetingId == meetingId);
            transcript.FullText.Should().Contain("alice update");
            transcript.FullText.Should().Contain("carol decision");
            transcript.FullText.Should().NotContain("Bob");

            var failedFragment = db.DbContext.ParticipantAudioFragments.Single(x => x.Id == failedFragmentId);
            failedFragment.Status.Should().Be(ParticipantAudioFragmentStatus.Failed);
            failedFragment.FailureCode.Should().Be("stt_failed");
            failedFragment.FailureMessage.Should().Contain("simulated stt failure");
            failedFragment.FailedAtUtc.Should().NotBeNull();

            db.DbContext.ParticipantAudioFragments
                .Where(x => x.Id != failedFragmentId)
                .Select(x => x.Status)
                .Should()
                .OnlyContain(x => x == ParticipantAudioFragmentStatus.Available);

            publisher.Notifications
                .OfType<MeetingTranscriptReadyEvent>()
                .Should()
                .ContainSingle(x => x.MeetingId == meetingId);

            var summarizer = new StubSummarizerService(new SummaryResult(
                "Partial summary generated.",
                "gpt-4o-mini",
                50,
                20));

            var summaryJob = new GenerateMeetingSummaryJob(
                db.DbContext,
                summarizer,
                NullLogger<GenerateMeetingSummaryJob>.Instance);

            await summaryJob.RunAsync(meetingId, orgId);

            var summary = db.DbContext.MeetingSummaries.Single(x => x.MeetingId == meetingId);
            summary.SummaryText.Should().Be("Partial summary generated.");
            summarizer.LastTranscript.Should().Be(transcript.FullText);
        }

        [Fact]
        public async Task TranscriptGeneration_ShouldWaitForPendingFragmentsBeforeSttAndRetrySuccessfully()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);
            var userId = db.SeedUser("Alice");
            db.AddParticipant(meetingId, orgId, userId);

            var pendingFragment = new ParticipantAudioFragment
            {
                MeetingId = meetingId,
                OrganizationId = orgId,
                ParticipantUserId = userId,
                TrackSid = "TR_PENDING_STT_WAIT",
                Status = ParticipantAudioFragmentStatus.Pending
            };
            db.DbContext.ParticipantAudioFragments.Add(pendingFragment);
            await db.DbContext.SaveChangesAsync();

            var objectKey = $"tracks/{meetingId}/{userId}.ogg";
            var stt = new StubSttService(new Dictionary<string, TrackTranscriptionResult>
            {
                [objectKey] = new(
                    "whisper-large-v3",
                    [new TranscriptSegment(userId, 0, 1_000, "late fragment is ready", 0.94)])
            });
            var publisher = new CollectingPublisher();
            var tracker = new PostMeetingProcessingTracker(db.DbContext);
            var transcriptJob = new GenerateMeetingTranscriptJob(
                db.DbContext,
                stt,
                publisher,
                NullLogger<GenerateMeetingTranscriptJob>.Instance,
                tracker);

            await transcriptJob.RunAsync(meetingId, orgId);

            stt.Calls.Should().BeEmpty();
            db.DbContext.MeetingTranscripts.Should().BeEmpty();
            publisher.Notifications.OfType<MeetingTranscriptReadyEvent>().Should().BeEmpty();
            var waitingSnapshot = await tracker.GetLatestByMeetingAsync(orgId, meetingId);
            waitingSnapshot.Steps.Should().ContainSingle(x =>
                x.StepType == PostMeetingProcessingStepType.Stt
                && x.Status == PostMeetingProcessingStatus.InProgress);
            waitingSnapshot.Events.Should().Contain(x =>
                x.EventType == PostMeetingProcessingEventType.Info
                && x.StepType == PostMeetingProcessingStepType.Stt
                && x.Message!.Contains("waiting for 1 participant audio fragment"));

            pendingFragment.Status = ParticipantAudioFragmentStatus.Available;
            pendingFragment.StorageObjectKey = objectKey;
            pendingFragment.StorageLocation = $"s3://recordings/{objectKey}";
            pendingFragment.StorageAvailableAtUtc = DateTime.UtcNow;
            await db.DbContext.SaveChangesAsync();

            await transcriptJob.RunAsync(meetingId, orgId);

            var transcript = db.DbContext.MeetingTranscripts.Single(x => x.MeetingId == meetingId);
            transcript.FullText.Should().Contain("late fragment is ready");
            publisher.Notifications
                .OfType<MeetingTranscriptReadyEvent>()
                .Should()
                .ContainSingle(x => x.MeetingId == meetingId);
            var completedSnapshot = await tracker.GetLatestByMeetingAsync(orgId, meetingId);
            completedSnapshot.Steps.Should().Contain(x =>
                x.StepType == PostMeetingProcessingStepType.Stt
                && x.Status == PostMeetingProcessingStatus.Completed);
        }

        [Fact]
        public async Task TranscriptGeneration_ShouldMarkFragmentFailed_WhenSttReturnsNoSegments()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);
            var userId = db.SeedUser("Alice");
            db.AddParticipant(meetingId, orgId, userId);

            var objectKey = $"tracks/{meetingId}/{userId}.ogg";
            var fragmentId = db.AddAvailableAudioFragment(meetingId, orgId, userId, objectKey, "TR_EMPTY_STT");

            var stt = new StubSttService(new Dictionary<string, TrackTranscriptionResult>
            {
                [objectKey] = new("whisper-large-v3", [])
            });
            var publisher = new CollectingPublisher();
            var tracker = new PostMeetingProcessingTracker(db.DbContext);
            var transcriptJob = new GenerateMeetingTranscriptJob(
                db.DbContext,
                stt,
                publisher,
                NullLogger<GenerateMeetingTranscriptJob>.Instance,
                tracker);

            await transcriptJob.RunAsync(meetingId, orgId);

            db.DbContext.MeetingTranscripts.Should().BeEmpty();
            publisher.Notifications.OfType<MeetingTranscriptReadyEvent>().Should().BeEmpty();

            var failedFragment = db.DbContext.ParticipantAudioFragments.Single(x => x.Id == fragmentId);
            failedFragment.Status.Should().Be(ParticipantAudioFragmentStatus.Failed);
            failedFragment.FailureCode.Should().Be("stt_failed");
            failedFragment.FailureMessage.Should().Contain("no transcript segments");
            failedFragment.FailedAtUtc.Should().NotBeNull();

            var snapshot = await tracker.GetLatestByMeetingAsync(orgId, meetingId);
            snapshot.Steps.Should().ContainSingle(x =>
                x.StepType == PostMeetingProcessingStepType.Stt
                && x.Status == PostMeetingProcessingStatus.Failed
                && x.ErrorCode == "all_fragments_failed");
        }

        [Fact]
        public async Task TranscriptGeneration_ShouldNotRepublishTranscriptReady_WhenTranscriptContentIsUnchanged()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);
            var userId = db.SeedUser("Alice");
            db.AddParticipant(meetingId, orgId, userId);

            var objectKey = $"tracks/{meetingId}/{userId}.ogg";
            db.AddAvailableAudioFragment(meetingId, orgId, userId, objectKey, "TR_IDEMPOTENT_STT");

            var stt = new StubSttService(new Dictionary<string, TrackTranscriptionResult>
            {
                [objectKey] = new(
                    "whisper-large-v3",
                    [new TranscriptSegment(userId, 0, 1_000, "same transcript content", 0.94)])
            });
            var publisher = new CollectingPublisher();
            var transcriptJob = new GenerateMeetingTranscriptJob(
                db.DbContext,
                stt,
                publisher,
                NullLogger<GenerateMeetingTranscriptJob>.Instance);

            await transcriptJob.RunAsync(meetingId, orgId);
            await transcriptJob.RunAsync(meetingId, orgId);

            db.DbContext.MeetingTranscripts.Where(x => x.MeetingId == meetingId).Should().ContainSingle();
            publisher.Notifications
                .OfType<MeetingTranscriptReadyEvent>()
                .Should()
                .ContainSingle(x => x.MeetingId == meetingId);
        }
    }
}
