using System.Text.Json;
using FluentAssertions;
using MeetingAssistant.Features.LiveSession.Jobs;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.LiveSession.Models.PostProcessing;
using MeetingAssistant.Features.LiveSession.Services;
using MeetingAssistant.Features.LiveSession.Services.PostProcessing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
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
        public async Task OptionalStepFailure_ShouldNotFailOrReopenCompletedRun()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);
            var tracker = new PostMeetingProcessingTracker(db.DbContext);

            await tracker.CompleteStepAsync(
                orgId,
                meetingId,
                PostMeetingProcessingStepType.TranscriptPersistence,
                artifact: new PostMeetingArtifactLink("meeting_transcript", Guid.NewGuid()));
            await tracker.CompleteStepAsync(
                orgId,
                meetingId,
                PostMeetingProcessingStepType.SummaryGeneration,
                artifact: new PostMeetingArtifactLink("meeting_summary", Guid.NewGuid()));
            await tracker.CompleteRunAsync(orgId, meetingId, message: "Core artifacts complete.");

            await tracker.StartStepAsync(
                orgId,
                meetingId,
                PostMeetingProcessingStepType.TagSuggestion,
                relatedHangfireJobId: "hf-tags-1");
            await tracker.FailStepAsync(
                orgId,
                meetingId,
                PostMeetingProcessingStepType.TagSuggestion,
                "tag_suggestion_failed",
                "LLM returned malformed JSON.",
                relatedHangfireJobId: "hf-tags-1");

            var snapshot = await tracker.GetLatestByMeetingAsync(orgId, meetingId);
            snapshot.Run!.Status.Should().Be(PostMeetingProcessingStatus.Completed);
            snapshot.Run.CompletedAtUtc.Should().NotBeNull();
            snapshot.Run.ErrorCode.Should().BeNull();
            snapshot.Steps.Should().ContainSingle(x =>
                x.StepType == PostMeetingProcessingStepType.TagSuggestion
                && x.Status == PostMeetingProcessingStatus.Failed
                && x.ErrorCode == "tag_suggestion_failed");
            snapshot.Events.Should().NotContain(x =>
                x.EventType == PostMeetingProcessingEventType.RunStatusChanged
                && x.Status == PostMeetingProcessingStatus.Failed);
        }

        [Fact]
        public async Task OptionalStepFailure_BeforeCoreCompletion_ShouldKeepRunInProgress()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);
            var tracker = new PostMeetingProcessingTracker(db.DbContext);

            await tracker.StartStepAsync(orgId, meetingId, PostMeetingProcessingStepType.ActionExtraction);
            await tracker.FailStepAsync(
                orgId,
                meetingId,
                PostMeetingProcessingStepType.ActionExtraction,
                "action_extraction_failed",
                "Provider timed out.");

            var snapshot = await tracker.GetLatestByMeetingAsync(orgId, meetingId);
            snapshot.Run!.Status.Should().Be(PostMeetingProcessingStatus.InProgress);
            snapshot.Run.ErrorCode.Should().BeNull();
            snapshot.Steps.Should().ContainSingle(x =>
                x.StepType == PostMeetingProcessingStepType.ActionExtraction
                && x.Status == PostMeetingProcessingStatus.Failed);
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

        [Fact]
        public async Task Reconciliation_ShouldFinalizeStaleCoreStepsFromDurableTranscriptAndSummaryArtifacts()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);
            var staleAtUtc = DateTime.UtcNow.AddHours(-2);
            var transcriptId = Guid.NewGuid();
            var summaryId = Guid.NewGuid();
            var run = SeedStaleRun(db, orgId, meetingId, staleAtUtc);
            db.DbContext.PostMeetingProcessingSteps.AddRange(
                CreateStep(run, PostMeetingProcessingStepType.TranscriptPersistence, PostMeetingProcessingStatus.InProgress, staleAtUtc),
                CreateStep(run, PostMeetingProcessingStepType.SummaryGeneration, PostMeetingProcessingStatus.InProgress, staleAtUtc));
            db.DbContext.MeetingTranscripts.Add(new MeetingTranscript
            {
                Id = transcriptId,
                OrganizationId = orgId,
                MeetingId = meetingId,
                FullText = "[00:00:01 Alice] stale transcript exists.",
                SegmentsJson = "[]",
                SttModel = "test",
                GeneratedAtUtc = DateTime.UtcNow.AddMinutes(-30)
            });
            db.DbContext.MeetingSummaries.Add(new MeetingSummary
            {
                Id = summaryId,
                OrganizationId = orgId,
                MeetingId = meetingId,
                SummaryText = "Stale summary exists.",
                LlmModel = "test",
                GeneratedAtUtc = DateTime.UtcNow.AddMinutes(-25)
            });
            await db.DbContext.SaveChangesAsync();

            var job = CreateReconciliationJob(db);
            await job.RunAsync(CancellationToken.None);

            var snapshot = await new PostMeetingProcessingTracker(db.DbContext).GetLatestByMeetingAsync(orgId, meetingId);
            snapshot.Run!.Status.Should().Be(PostMeetingProcessingStatus.Completed);
            snapshot.Run.CompletedAtUtc.Should().NotBeNull();
            snapshot.Steps.Should().Contain(x =>
                x.StepType == PostMeetingProcessingStepType.TranscriptPersistence
                && x.Status == PostMeetingProcessingStatus.Completed
                && x.ArtifactId == transcriptId);
            snapshot.Steps.Should().Contain(x =>
                x.StepType == PostMeetingProcessingStepType.SummaryGeneration
                && x.Status == PostMeetingProcessingStatus.Completed
                && x.ArtifactId == summaryId);
            var eventCountAfterFirstRun = snapshot.Events.Count;

            await job.RunAsync(CancellationToken.None);

            var secondSnapshot = await new PostMeetingProcessingTracker(db.DbContext).GetLatestByMeetingAsync(orgId, meetingId);
            secondSnapshot.Events.Should().HaveCount(eventCountAfterFirstRun);
        }

        [Fact]
        public async Task Reconciliation_ShouldSkipStaleOptionalStepWithoutReopeningCompletedCoreRun()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);
            var staleAtUtc = DateTime.UtcNow.AddHours(-2);
            var run = SeedStaleRun(db, orgId, meetingId, staleAtUtc);
            db.DbContext.PostMeetingProcessingSteps.Add(
                CreateStep(run, PostMeetingProcessingStepType.KnowledgeIndexing, PostMeetingProcessingStatus.InProgress, staleAtUtc));
            db.DbContext.MeetingTranscripts.Add(new MeetingTranscript
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgId,
                MeetingId = meetingId,
                FullText = "[00:00:01 Alice] transcript exists.",
                SegmentsJson = "[]",
                SttModel = "test",
                GeneratedAtUtc = DateTime.UtcNow.AddMinutes(-30)
            });
            db.DbContext.MeetingSummaries.Add(new MeetingSummary
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgId,
                MeetingId = meetingId,
                SummaryText = "Summary exists.",
                LlmModel = "test",
                GeneratedAtUtc = DateTime.UtcNow.AddMinutes(-25)
            });
            await db.DbContext.SaveChangesAsync();

            await CreateReconciliationJob(db).RunAsync(CancellationToken.None);

            var snapshot = await new PostMeetingProcessingTracker(db.DbContext).GetLatestByMeetingAsync(orgId, meetingId);
            snapshot.Run!.Status.Should().Be(PostMeetingProcessingStatus.Completed);
            snapshot.Steps.Should().ContainSingle(x =>
                x.StepType == PostMeetingProcessingStepType.KnowledgeIndexing
                && x.Status == PostMeetingProcessingStatus.Skipped);
            snapshot.Events.Should().Contain(x =>
                x.EventType == PostMeetingProcessingEventType.StepSkipped
                && x.StepType == PostMeetingProcessingStepType.KnowledgeIndexing);
        }

        [Fact]
        public async Task Reconciliation_ShouldCompleteStalePersonalizedSummaryStepFromDurableArtifacts()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);
            var userId = db.SeedUser("Alice");
            var participantId = db.AddParticipant(meetingId, orgId, userId);
            var staleAtUtc = DateTime.UtcNow.AddHours(-2);
            var run = SeedStaleRun(db, orgId, meetingId, staleAtUtc);
            db.DbContext.PostMeetingProcessingSteps.Add(
                CreateStep(run, PostMeetingProcessingStepType.PersonalizedSummaryGeneration, PostMeetingProcessingStatus.InProgress, staleAtUtc));
            var summaryId = Guid.NewGuid();
            db.DbContext.PersonalizedMeetingSummaries.Add(new PersonalizedMeetingSummary
            {
                Id = summaryId,
                OrganizationId = orgId,
                MeetingId = meetingId,
                MeetingParticipantId = participantId,
                UserId = userId,
                SummaryText = "Personalized summary exists.",
                LlmModel = "test",
                GeneratedAtUtc = DateTime.UtcNow.AddMinutes(-25)
            });
            await db.DbContext.SaveChangesAsync();

            await CreateReconciliationJob(db).RunAsync(CancellationToken.None);

            var snapshot = await new PostMeetingProcessingTracker(db.DbContext).GetLatestByMeetingAsync(orgId, meetingId);
            snapshot.Steps.Should().ContainSingle(x =>
                x.StepType == PostMeetingProcessingStepType.PersonalizedSummaryGeneration
                && x.Status == PostMeetingProcessingStatus.Completed
                && x.ArtifactType == "personalized_meeting_summary"
                && JsonSerializer.Deserialize<Guid[]>(x.ArtifactIdsJson!)!.Single() == summaryId);
        }

        [Fact]
        public async Task Reconciliation_ShouldLeaveFreshInProgressStepsUntouched()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);
            var freshAtUtc = DateTime.UtcNow;
            var run = SeedStaleRun(db, orgId, meetingId, freshAtUtc);
            db.DbContext.PostMeetingProcessingSteps.Add(
                CreateStep(run, PostMeetingProcessingStepType.SummaryGeneration, PostMeetingProcessingStatus.InProgress, freshAtUtc));
            db.DbContext.MeetingSummaries.Add(new MeetingSummary
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgId,
                MeetingId = meetingId,
                SummaryText = "Fresh summary exists.",
                LlmModel = "test",
                GeneratedAtUtc = DateTime.UtcNow
            });
            await db.DbContext.SaveChangesAsync();

            await CreateReconciliationJob(db).RunAsync(CancellationToken.None);

            var snapshot = await new PostMeetingProcessingTracker(db.DbContext).GetLatestByMeetingAsync(orgId, meetingId);
            snapshot.Run!.Status.Should().Be(PostMeetingProcessingStatus.InProgress);
            snapshot.Steps.Should().ContainSingle(x =>
                x.StepType == PostMeetingProcessingStepType.SummaryGeneration
                && x.Status == PostMeetingProcessingStatus.InProgress);
        }

        private static PostMeetingProcessingReconciliationJob CreateReconciliationJob(LiveSessionTestDb db)
        {
            return new PostMeetingProcessingReconciliationJob(
                db.DbContext,
                new PostMeetingProcessingTracker(db.DbContext),
                NullLogger<PostMeetingProcessingReconciliationJob>.Instance);
        }

        private static PostMeetingProcessingRun SeedStaleRun(
            LiveSessionTestDb db,
            Guid orgId,
            Guid meetingId,
            DateTime startedAtUtc)
        {
            var run = new PostMeetingProcessingRun
            {
                OrganizationId = orgId,
                MeetingId = meetingId,
                Status = PostMeetingProcessingStatus.InProgress,
                StartedAtUtc = startedAtUtc,
                AttemptCount = 1
            };
            db.DbContext.PostMeetingProcessingRuns.Add(run);
            return run;
        }

        [Fact]
        public async Task DistinctPipelineGenerations_ShouldCreateSeparateRunsAndSteps()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);
            var tracker = new PostMeetingProcessingTracker(db.DbContext);
            var generationA = Guid.NewGuid();
            var generationB = Guid.NewGuid();

            await tracker.CompleteStepAsync(
                orgId,
                meetingId,
                PostMeetingProcessingStepType.Stt,
                generationA,
                relatedHangfireJobId: "hf-generation-a",
                message: "Generation A STT completed.");
            await tracker.StartStepAsync(
                orgId,
                meetingId,
                PostMeetingProcessingStepType.Stt,
                generationB,
                relatedHangfireJobId: "hf-generation-b",
                message: "Generation B STT started.");

            var runs = await db.DbContext.PostMeetingProcessingRuns
                .IgnoreQueryFilters()
                .Where(x => x.OrganizationId == orgId && x.MeetingId == meetingId)
                .ToListAsync();

            runs.Should().HaveCount(2);
            runs.Select(x => x.PipelineGenerationId).Should().BeEquivalentTo([generationA, generationB]);

            var steps = await db.DbContext.PostMeetingProcessingSteps
                .IgnoreQueryFilters()
                .Where(x => x.OrganizationId == orgId && x.MeetingId == meetingId && x.StepType == PostMeetingProcessingStepType.Stt)
                .ToListAsync();

            steps.Should().HaveCount(2);
            steps.Should().ContainSingle(x =>
                x.RunId == runs.Single(r => r.PipelineGenerationId == generationA).Id
                && x.Status == PostMeetingProcessingStatus.Completed
                && x.RelatedHangfireJobId == "hf-generation-a");
            steps.Should().ContainSingle(x =>
                x.RunId == runs.Single(r => r.PipelineGenerationId == generationB).Id
                && x.Status == PostMeetingProcessingStatus.InProgress
                && x.RelatedHangfireJobId == "hf-generation-b");
        }

        [Fact]
        public async Task StartStepAsync_WithDifferentHangfireJobId_ShouldNotShortCircuitCompletedStep()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);
            var tracker = new PostMeetingProcessingTracker(db.DbContext);

            await tracker.CompleteStepAsync(
                orgId,
                meetingId,
                PostMeetingProcessingStepType.Stt,
                relatedHangfireJobId: "old-job");
            var restarted = await tracker.StartStepAsync(
                orgId,
                meetingId,
                PostMeetingProcessingStepType.Stt,
                relatedHangfireJobId: "new-job",
                message: "STT rerun started.");

            restarted.Status.Should().Be(PostMeetingProcessingStatus.InProgress);
            restarted.RelatedHangfireJobId.Should().Be("new-job");
            restarted.AttemptCount.Should().Be(1);

            var snapshot = await tracker.GetLatestByMeetingAsync(orgId, meetingId);
            snapshot.Events.Should().Contain(x =>
                x.EventType == PostMeetingProcessingEventType.StepRetried
                && x.RelatedHangfireJobId == "new-job");
        }

        [Fact]
        public async Task RecordEventAsync_ShouldUseExplicitJobIdWithoutStaleFallback()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);
            var tracker = new PostMeetingProcessingTracker(db.DbContext);
            var generationId = Guid.NewGuid();

            await tracker.CompleteStepAsync(
                orgId,
                meetingId,
                PostMeetingProcessingStepType.Stt,
                generationId,
                relatedHangfireJobId: "stale-job");
            var processingEvent = await tracker.RecordEventAsync(
                orgId,
                meetingId,
                PostMeetingProcessingEventType.Info,
                generationId,
                PostMeetingProcessingStepType.Stt,
                PostMeetingProcessingStatus.InProgress,
                message: "Inline progress event without explicit job id.");

            processingEvent.RelatedHangfireJobId.Should().BeNull();
        }

        [Fact]
        public async Task GenerateMeetingTranscriptJob_ShouldAttributeTrackerEventsToSeededHangfireJobId()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);
            var roomStartedAtUtc = new DateTime(2026, 06, 04, 10, 00, 00, DateTimeKind.Utc);
            var meeting = db.DbContext.Meetings.Single(x => x.Id == meetingId);
            meeting.RoomActivatedAtUtc = roomStartedAtUtc;

            var aliceId = db.SeedUser("Alice");
            db.AddParticipant(meetingId, orgId, aliceId);
            db.DbContext.AiAssistantTraceEvents.Add(new AiAssistantTraceEvent
            {
                OrganizationId = orgId,
                MeetingId = meetingId,
                SessionId = "trace-session-attribution",
                TurnId = "trace-turn-attribution",
                Sequence = 20,
                EventType = AiAssistantTraceEventTypes.SttCompleted,
                OccurredAtUtc = roomStartedAtUtc.AddSeconds(5),
                StepType = "stt",
                Text = "Attribution test transcript from traces."
            });
            await db.DbContext.SaveChangesAsync();

            var hangfireContext = new TestHangfireJobContextAccessor { CurrentJobId = "hf-transcript-current" };
            var tracker = new PostMeetingProcessingTracker(db.DbContext);
            var pipelineGenerationId = Guid.NewGuid();
            var transcriptJob = new GenerateMeetingTranscriptJob(
                db.DbContext,
                new StubSttService(new Dictionary<string, TrackTranscriptionResult>()),
                new CollectingPublisher(),
                NullLogger<GenerateMeetingTranscriptJob>.Instance,
                tracker,
                hangfireJobContextAccessor: hangfireContext);

            await transcriptJob.RunAsync(meetingId, orgId, pipelineGenerationId);

            var run = await db.DbContext.PostMeetingProcessingRuns
                .IgnoreQueryFilters()
                .SingleAsync(x => x.PipelineGenerationId == pipelineGenerationId);
            run.RelatedHangfireJobId.Should().Be("hf-transcript-current");

            var sttStep = await db.DbContext.PostMeetingProcessingSteps
                .IgnoreQueryFilters()
                .SingleAsync(x => x.RunId == run.Id && x.StepType == PostMeetingProcessingStepType.Stt);
            sttStep.RelatedHangfireJobId.Should().Be("hf-transcript-current");

            var sttEvents = await db.DbContext.PostMeetingProcessingEvents
                .IgnoreQueryFilters()
                .Where(x => x.RunId == run.Id && x.StepType == PostMeetingProcessingStepType.Stt)
                .ToListAsync();
            sttEvents.Should().NotBeEmpty();
            sttEvents.Where(x => x.EventType is PostMeetingProcessingEventType.StepStarted or PostMeetingProcessingEventType.StepCompleted)
                .Should()
                .OnlyContain(x => x.RelatedHangfireJobId == "hf-transcript-current");
        }

        [Fact]
        public async Task BeginManualRerunAsync_ShouldCreateFreshGenerationAndPendingStepForActionExtraction()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);
            var tracker = new PostMeetingProcessingTracker(db.DbContext);
            var staleGeneration = Guid.NewGuid();

            await tracker.CompleteStepAsync(
                orgId,
                meetingId,
                PostMeetingProcessingStepType.ActionExtraction,
                staleGeneration,
                relatedHangfireJobId: "old-action-job");

            var pipelineGenerationId = await PostMeetingProcessingPipeline.BeginManualRerunAsync(
                tracker,
                orgId,
                meetingId,
                PostMeetingProcessingStepType.ActionExtraction,
                message: "Manual action item re-extract pending.",
                relatedHangfireJobId: "new-action-job");

            pipelineGenerationId.Should().NotBe(staleGeneration);

            var runs = await db.DbContext.PostMeetingProcessingRuns
                .IgnoreQueryFilters()
                .Where(x => x.OrganizationId == orgId && x.MeetingId == meetingId)
                .ToListAsync();
            runs.Should().HaveCount(2);

            var pendingStep = await db.DbContext.PostMeetingProcessingSteps
                .IgnoreQueryFilters()
                .SingleAsync(x =>
                    x.OrganizationId == orgId
                    && x.MeetingId == meetingId
                    && x.Run!.PipelineGenerationId == pipelineGenerationId
                    && x.StepType == PostMeetingProcessingStepType.ActionExtraction);
            pendingStep.Status.Should().Be(PostMeetingProcessingStatus.Pending);
            pendingStep.RelatedHangfireJobId.Should().Be("new-action-job");
        }

        [Fact]
        public async Task BeginManualRerunAsync_ShouldCreateFreshGenerationAndPendingStepForKnowledgeIndexing()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);
            var tracker = new PostMeetingProcessingTracker(db.DbContext);
            var staleGeneration = Guid.NewGuid();

            await tracker.CompleteStepAsync(
                orgId,
                meetingId,
                PostMeetingProcessingStepType.KnowledgeIndexing,
                staleGeneration,
                relatedHangfireJobId: "old-knowledge-job");

            var pipelineGenerationId = await PostMeetingProcessingPipeline.BeginManualRerunAsync(
                tracker,
                orgId,
                meetingId,
                PostMeetingProcessingStepType.KnowledgeIndexing,
                message: "Manual knowledge reindex pending.",
                relatedHangfireJobId: "new-knowledge-job");

            pipelineGenerationId.Should().NotBe(staleGeneration);

            var pendingStep = await db.DbContext.PostMeetingProcessingSteps
                .IgnoreQueryFilters()
                .SingleAsync(x =>
                    x.OrganizationId == orgId
                    && x.MeetingId == meetingId
                    && x.Run!.PipelineGenerationId == pipelineGenerationId
                    && x.StepType == PostMeetingProcessingStepType.KnowledgeIndexing);
            pendingStep.Status.Should().Be(PostMeetingProcessingStatus.Pending);
            pendingStep.RelatedHangfireJobId.Should().Be("new-knowledge-job");
        }

        private static PostMeetingProcessingStep CreateStep(
            PostMeetingProcessingRun run,
            PostMeetingProcessingStepType stepType,
            PostMeetingProcessingStatus status,
            DateTime lastAttemptAtUtc)
        {
            return new PostMeetingProcessingStep
            {
                OrganizationId = run.OrganizationId,
                MeetingId = run.MeetingId,
                Run = run,
                StepType = stepType,
                Status = status,
                StartedAtUtc = lastAttemptAtUtc,
                LastAttemptAtUtc = lastAttemptAtUtc,
                AttemptCount = 1
            };
        }
    }
}
