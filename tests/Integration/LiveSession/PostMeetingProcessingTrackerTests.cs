using System.Text.Json;
using FluentAssertions;
using MeetingAssistant.Features.LiveSession.Models.PostProcessing;
using MeetingAssistant.Features.LiveSession.Services.PostProcessing;
using Xunit;

namespace tests.Integration.LiveSession
{
    public class PostMeetingProcessingTrackerTests
    {
        [Fact]
        public async Task MarkStepPendingAsync_ShouldCreateQueryableRunStepAndEventWithHangfireJobId()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);
            var tracker = new PostMeetingProcessingTracker(db.DbContext);

            var step = await tracker.MarkStepPendingAsync(
                orgId,
                meetingId,
                PostMeetingProcessingStepType.Stt,
                message: "STT job enqueued.",
                relatedHangfireJobId: "hf-stt-1");

            step.Status.Should().Be(PostMeetingProcessingStatus.Pending);
            step.RelatedHangfireJobId.Should().Be("hf-stt-1");

            var snapshot = await tracker.GetLatestByMeetingAsync(orgId, meetingId);
            snapshot.Run.Should().NotBeNull();
            snapshot.Run!.OrganizationId.Should().Be(orgId);
            snapshot.Run.MeetingId.Should().Be(meetingId);
            snapshot.Run.Status.Should().Be(PostMeetingProcessingStatus.Pending);
            snapshot.Run.RelatedHangfireJobId.Should().Be("hf-stt-1");

            snapshot.Steps.Should().ContainSingle(x =>
                x.StepType == PostMeetingProcessingStepType.Stt
                && x.Status == PostMeetingProcessingStatus.Pending
                && x.RelatedHangfireJobId == "hf-stt-1");
            snapshot.Events.Should().Contain(x => x.EventType == PostMeetingProcessingEventType.RunCreated);
            snapshot.Events.Should().Contain(x =>
                x.EventType == PostMeetingProcessingEventType.StepPending
                && x.StepType == PostMeetingProcessingStepType.Stt
                && x.RelatedHangfireJobId == "hf-stt-1");
        }

        [Fact]
        public async Task StepLifecycle_ShouldTrackAttemptsFailuresRetriesAndArtifactsIdempotently()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);
            var tracker = new PostMeetingProcessingTracker(db.DbContext);
            var transcriptId = Guid.NewGuid();

            var firstStart = await tracker.StartStepAsync(
                orgId,
                meetingId,
                PostMeetingProcessingStepType.TranscriptPersistence,
                relatedHangfireJobId: "hf-transcript-1");
            var firstAttemptCount = firstStart.AttemptCount;
            var duplicateStart = await tracker.StartStepAsync(
                orgId,
                meetingId,
                PostMeetingProcessingStepType.TranscriptPersistence,
                relatedHangfireJobId: "hf-transcript-1");
            var duplicateAttemptCount = duplicateStart.AttemptCount;
            var failed = await tracker.FailStepAsync(
                orgId,
                meetingId,
                PostMeetingProcessingStepType.TranscriptPersistence,
                "transcript_write_failed",
                "database unavailable",
                relatedHangfireJobId: "hf-transcript-1");
            var failedStatus = failed.Status;
            var failedErrorCode = failed.ErrorCode;
            var retry = await tracker.StartStepAsync(
                orgId,
                meetingId,
                PostMeetingProcessingStepType.TranscriptPersistence,
                relatedHangfireJobId: "hf-transcript-2");
            var retryAttemptCount = retry.AttemptCount;
            var retryStatus = retry.Status;
            var completed = await tracker.CompleteStepAsync(
                orgId,
                meetingId,
                PostMeetingProcessingStepType.TranscriptPersistence,
                relatedHangfireJobId: "hf-transcript-2",
                artifact: new PostMeetingArtifactLink("meeting_transcript", transcriptId));
            var duplicateComplete = await tracker.CompleteStepAsync(
                orgId,
                meetingId,
                PostMeetingProcessingStepType.TranscriptPersistence,
                relatedHangfireJobId: "hf-transcript-2",
                artifact: new PostMeetingArtifactLink("meeting_transcript", transcriptId));

            firstAttemptCount.Should().Be(1);
            duplicateAttemptCount.Should().Be(1);
            failedStatus.Should().Be(PostMeetingProcessingStatus.Failed);
            failedErrorCode.Should().Be("transcript_write_failed");
            retryAttemptCount.Should().Be(2);
            retryStatus.Should().Be(PostMeetingProcessingStatus.InProgress);
            completed.Status.Should().Be(PostMeetingProcessingStatus.Completed);
            completed.ArtifactType.Should().Be("meeting_transcript");
            completed.ArtifactId.Should().Be(transcriptId);
            duplicateComplete.Id.Should().Be(completed.Id);

            var snapshot = await tracker.GetLatestByMeetingAsync(orgId, meetingId);
            snapshot.Run!.Status.Should().Be(PostMeetingProcessingStatus.InProgress);
            snapshot.Run.AttemptCount.Should().Be(2);
            snapshot.Steps.Should().ContainSingle(x =>
                x.StepType == PostMeetingProcessingStepType.TranscriptPersistence
                && x.AttemptCount == 2
                && x.Status == PostMeetingProcessingStatus.Completed
                && x.ArtifactId == transcriptId);
            snapshot.Events.Count(x => x.EventType == PostMeetingProcessingEventType.StepStarted).Should().Be(1);
            snapshot.Events.Count(x => x.EventType == PostMeetingProcessingEventType.StepRetried).Should().Be(1);
            snapshot.Events.Count(x => x.EventType == PostMeetingProcessingEventType.StepFailed).Should().Be(1);
            snapshot.Events.Count(x => x.EventType == PostMeetingProcessingEventType.StepCompleted).Should().Be(1);
            snapshot.Events.Count(x => x.EventType == PostMeetingProcessingEventType.ArtifactLinked).Should().Be(1);
        }

        [Fact]
        public async Task Tracker_ShouldSupportTagKnowledgeAndProviderStepsWithMultipleArtifactIdsAndCompletedRun()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);
            var tracker = new PostMeetingProcessingTracker(db.DbContext);
            var suggestedTagIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
            var chunkIds = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
            var syncedActionItemIds = new[] { Guid.NewGuid() };

            await tracker.CompleteStepAsync(
                orgId,
                meetingId,
                PostMeetingProcessingStepType.TagSuggestion,
                artifact: new PostMeetingArtifactLink("meeting_tag_suggestion", ArtifactIds: suggestedTagIds));
            await tracker.CompleteStepAsync(
                orgId,
                meetingId,
                PostMeetingProcessingStepType.KnowledgeIndexing,
                artifact: new PostMeetingArtifactLink("knowledge_chunk", ArtifactIds: chunkIds));
            await tracker.CompleteStepAsync(
                orgId,
                meetingId,
                PostMeetingProcessingStepType.ProviderSync,
                relatedHangfireJobId: "hf-provider-sync",
                artifact: new PostMeetingArtifactLink("action_item", ArtifactIds: syncedActionItemIds));
            await tracker.CompleteRunAsync(orgId, meetingId, message: "All post-meeting processing steps completed.");

            var snapshot = await tracker.GetLatestByMeetingAsync(orgId, meetingId);

            snapshot.Run!.Status.Should().Be(PostMeetingProcessingStatus.Completed);
            snapshot.Run.CompletedAtUtc.Should().NotBeNull();
            snapshot.Steps.Should().Contain(x =>
                x.StepType == PostMeetingProcessingStepType.TagSuggestion
                && x.ArtifactType == "meeting_tag_suggestion"
                && JsonSerializer.Deserialize<Guid[]>(x.ArtifactIdsJson!)!.OrderBy(id => id).SequenceEqual(suggestedTagIds.OrderBy(id => id)));
            snapshot.Steps.Should().Contain(x =>
                x.StepType == PostMeetingProcessingStepType.KnowledgeIndexing
                && x.ArtifactType == "knowledge_chunk"
                && JsonSerializer.Deserialize<Guid[]>(x.ArtifactIdsJson!)!.Length == 3);
            snapshot.Steps.Should().Contain(x =>
                x.StepType == PostMeetingProcessingStepType.ProviderSync
                && x.RelatedHangfireJobId == "hf-provider-sync"
                && x.ArtifactType == "action_item");
            snapshot.Events.Should().Contain(x =>
                x.EventType == PostMeetingProcessingEventType.RunStatusChanged
                && x.Status == PostMeetingProcessingStatus.Completed);
        }
    }
}
