using System.Collections.Concurrent;
using FluentAssertions;
using MeetingAssistant.Features.ActionItems.Jobs;
using MeetingAssistant.Features.ActionItems.Models.Entities;
using MeetingAssistant.Features.ActionItems.Models.Enums;
using MeetingAssistant.Features.LiveSession.Infrastructure;
using MeetingAssistant.Features.LiveSession.Jobs;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.LiveSession.Models.Events;
using MeetingAssistant.Features.LiveSession.Models.PostProcessing;
using MeetingAssistant.Features.LiveSession.Services;
using MeetingAssistant.Features.LiveSession.Services.PostProcessing;
using MeetingAssistant.Features.Rag.Jobs;
using MeetingAssistant.Features.Rag.Services;
using MeetingAssistant.Infrastructure.AI;
using MeetingAssistant.Infrastructure.AI.DTOs;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using tests.Integration.LiveSession;
using Xunit;

namespace tests.Unit.LiveSession
{
    public class PartialFailureTests
    {
        [Fact]
        public async Task SttFailure_ShouldNotMarkAudioFragmentFailed_AndShouldRecordRetryableSttMetadata()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);

            var user1 = db.SeedUser("Alice");
            var user2 = db.SeedUser("Bob");

            db.AddParticipant(meetingId, orgId, user1);
            db.AddParticipant(meetingId, orgId, user2);

            var key1 = $"tracks/{meetingId}/{user1}.ogg";
            var key2 = $"tracks/{meetingId}/{user2}.ogg";

            db.AddAvailableAudioFragment(meetingId, orgId, user1, key1, "TR_USER_1");
            var failedFragmentId = db.AddAvailableAudioFragment(meetingId, orgId, user2, key2, "TR_USER_2");

            var stt = new StubSttService(
                new Dictionary<string, TrackTranscriptionResult>
                {
                    [key1] = new(
                        "whisper-large-v3",
                        [new TranscriptSegment(user1, 0, 1_000, "alice update", 0.95)])
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
            transcript.FullText.Should().NotContain("Bob");
            transcript.CompletenessStatus.Should().Be(MeetingTranscriptCompletenessStatus.CompletedWithWarnings);
            transcript.RetryableFailedAudioFragmentCount.Should().Be(1);
            transcript.MissingAudioFragmentIdsJson.Should().Contain(failedFragmentId.ToString());

            var failedFragment = db.DbContext.ParticipantAudioFragments.Single(x => x.Id == failedFragmentId);
            failedFragment.Status.Should().Be(ParticipantAudioFragmentStatus.Available);
            failedFragment.StorageObjectKey.Should().Be(key2);
            failedFragment.SttStatus.Should().Be(ParticipantAudioFragmentSttStatus.FailedRetryable);
            failedFragment.SttAttemptCount.Should().Be(1);
            failedFragment.SttFailureCode.Should().Be("stt_failed");
            failedFragment.SttFailureMessage.Should().Contain("simulated stt failure");
            failedFragment.FailureCode.Should().BeNull();
            failedFragment.FailedAtUtc.Should().BeNull();

            publisher.Notifications
                .OfType<MeetingTranscriptReadyEvent>()
                .Should()
                .BeEmpty();
        }

        [Fact]
        public async Task SecondTranscriptRun_ShouldRetryPreviouslySttFailedFragment_AndPublishCompleteTranscript()
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
            var bobFragmentId = db.AddAvailableAudioFragment(meetingId, orgId, bobId, bobKey, "TR_BOB");
            db.AddAvailableAudioFragment(meetingId, orgId, aliceId, aliceKey, "TR_ALICE");

            var stt = new FailOnceThenSucceedSttService(
                bobKey,
                new InvalidOperationException("simulated first-pass stt failure"),
                new Dictionary<string, TrackTranscriptionResult>
                {
                    [aliceKey] = new(
                        "whisper-large-v3",
                        [new TranscriptSegment(aliceId, 0, 1_000, "alice spoke", 0.95)]),
                    [bobKey] = new(
                        "whisper-large-v3",
                        [new TranscriptSegment(bobId, 2_000, 3_000, "bob spoke", 0.9)])
                });

            var publisher = new CollectingPublisher();
            var transcriptJob = new GenerateMeetingTranscriptJob(
                db.DbContext,
                stt,
                publisher,
                NullLogger<GenerateMeetingTranscriptJob>.Instance);

            await transcriptJob.RunAsync(meetingId, orgId);
            publisher.Notifications.OfType<MeetingTranscriptReadyEvent>().Should().BeEmpty();

            await transcriptJob.RunAsync(meetingId, orgId);

            var transcript = db.DbContext.MeetingTranscripts.Single(x => x.MeetingId == meetingId);
            transcript.FullText.Should().Contain("alice spoke");
            transcript.FullText.Should().Contain("bob spoke");
            transcript.CompletenessStatus.Should().Be(MeetingTranscriptCompletenessStatus.Complete);
            transcript.WarningsJson.Should().NotContain("Degraded transcript");

            var bobFragment = db.DbContext.ParticipantAudioFragments.Single(x => x.Id == bobFragmentId);
            bobFragment.Status.Should().Be(ParticipantAudioFragmentStatus.Available);
            bobFragment.SttStatus.Should().Be(ParticipantAudioFragmentSttStatus.Succeeded);
            bobFragment.SttAttemptCount.Should().Be(2);

            publisher.Notifications
                .OfType<MeetingTranscriptReadyEvent>()
                .Should()
                .ContainSingle(x => x.MeetingId == meetingId);
        }

        [Fact]
        public async Task PartialTranscript_ShouldPersistCompletedWithWarningsMetadata()
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
            var bobFragmentId = db.AddAvailableAudioFragment(meetingId, orgId, bobId, bobKey, "TR_BOB");
            db.AddAvailableAudioFragment(meetingId, orgId, aliceId, aliceKey, "TR_ALICE");

            var stt = new StubSttService(
                new Dictionary<string, TrackTranscriptionResult>
                {
                    [aliceKey] = new(
                        "whisper-large-v3",
                        [new TranscriptSegment(aliceId, 0, 1_000, "alice only", 0.95)])
                },
                new Dictionary<string, Exception>
                {
                    [bobKey] = new InvalidOperationException("bob stt failed")
                });

            var tracker = new PostMeetingProcessingTracker(db.DbContext);
            var transcriptJob = new GenerateMeetingTranscriptJob(
                db.DbContext,
                stt,
                new CollectingPublisher(),
                NullLogger<GenerateMeetingTranscriptJob>.Instance,
                tracker);

            await transcriptJob.RunAsync(meetingId, orgId);

            var transcript = db.DbContext.MeetingTranscripts.Single(x => x.MeetingId == meetingId);
            transcript.CompletenessStatus.Should().Be(MeetingTranscriptCompletenessStatus.CompletedWithWarnings);
            transcript.ExpectedAudioFragmentCount.Should().Be(2);
            transcript.TranscribedAudioFragmentCount.Should().Be(1);
            transcript.RetryableFailedAudioFragmentCount.Should().Be(1);
            transcript.MissingAudioFragmentIdsJson.Should().Contain(bobFragmentId.ToString());
            transcript.WarningsJson.Should().Contain("Degraded transcript");

            var snapshot = await tracker.GetLatestByMeetingAsync(orgId, meetingId);
            snapshot.Steps.Should().Contain(x =>
                x.StepType == PostMeetingProcessingStepType.Stt
                && x.Status == PostMeetingProcessingStatus.CompletedWithWarnings);
            snapshot.Run!.Status.Should().Be(PostMeetingProcessingStatus.CompletedWithWarnings);
        }

        [Fact]
        public async Task DownstreamJobs_ShouldSkipWhenTranscriptIsIncomplete()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);

            db.DbContext.MeetingTranscripts.Add(new MeetingTranscript
            {
                MeetingId = meetingId,
                OrganizationId = orgId,
                FullText = "partial transcript only",
                SegmentsJson = "[]",
                SttModel = "whisper-large-v3",
                GeneratedAtUtc = DateTime.UtcNow,
                CompletenessStatus = MeetingTranscriptCompletenessStatus.CompletedWithWarnings,
                ExpectedAudioFragmentCount = 2,
                TranscribedAudioFragmentCount = 1,
                RetryableFailedAudioFragmentCount = 1
            });
            await db.DbContext.SaveChangesAsync();

            var tracker = new PostMeetingProcessingTracker(db.DbContext);
            var summarizer = new StubSummarizerService(new SummaryResult("should not run", "gpt-4o-mini", 1, 1));

            var summaryJob = new GenerateMeetingSummaryJob(
                db.DbContext,
                summarizer,
                NullLogger<GenerateMeetingSummaryJob>.Instance,
                tracker);
            await summaryJob.RunAsync(meetingId, orgId);

            db.DbContext.MeetingSummaries.Should().BeEmpty();
            summarizer.LastTranscript.Should().BeNull();

            var ragJob = new ReindexMeetingKnowledgeJob(
                db.DbContext,
                new ReindexMeetingKnowledgeService(
                    db.DbContext,
                    new NoopEmbeddingService(),
                    NullLogger<ReindexMeetingKnowledgeService>.Instance),
                NullLogger<ReindexMeetingKnowledgeJob>.Instance,
                tracker);
            await ragJob.RunAsync(meetingId, orgId);
            db.DbContext.KnowledgeDocuments.Where(x => x.MeetingId == meetingId).Should().BeEmpty();

            var snapshot = await tracker.GetLatestByMeetingAsync(orgId, meetingId);
            snapshot.Steps.Should().Contain(x =>
                x.StepType == PostMeetingProcessingStepType.SummaryGeneration
                && x.Status == PostMeetingProcessingStatus.Skipped);
            snapshot.Steps.Should().Contain(x =>
                x.StepType == PostMeetingProcessingStepType.KnowledgeIndexing
                && x.Status == PostMeetingProcessingStatus.Skipped);
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
        public async Task SttNoSegments_ShouldRecordRetryableSttMetadataWithoutMarkingAudioFragmentFailed()
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
            failedFragment.Status.Should().Be(ParticipantAudioFragmentStatus.Available);
            failedFragment.SttStatus.Should().Be(ParticipantAudioFragmentSttStatus.FailedRetryable);
            failedFragment.SttFailureCode.Should().Be("stt_no_segments");
            failedFragment.SttFailureMessage.Should().Contain("no transcript segments");
            failedFragment.FailureCode.Should().BeNull();
            failedFragment.FailedAtUtc.Should().BeNull();

            var snapshot = await tracker.GetLatestByMeetingAsync(orgId, meetingId);
            snapshot.Steps.Should().ContainSingle(x =>
                x.StepType == PostMeetingProcessingStepType.Stt
                && x.Status == PostMeetingProcessingStatus.Failed
                && x.ErrorCode == "all_fragments_failed");
        }

        [Fact]
        public async Task TerminalSttFailure_ShouldRemainIncompleteAcrossLaterTranscriptRuns_AndBlockDownstreamPublish()
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
            var bobFragmentId = db.AddAvailableAudioFragment(meetingId, orgId, bobId, bobKey, "TR_BOB");

            var bobFragment = db.DbContext.ParticipantAudioFragments.Single(x => x.Id == bobFragmentId);
            bobFragment.SttStatus = ParticipantAudioFragmentSttStatus.FailedTerminal;
            bobFragment.SttAttemptCount = 5;
            bobFragment.SttFailureCode = "stt_failed";
            bobFragment.SttFailureMessage = "terminal STT failure from prior attempts";
            await db.DbContext.SaveChangesAsync();

            var stt = new StubSttService(new Dictionary<string, TrackTranscriptionResult>
            {
                [aliceKey] = new(
                    "whisper-large-v3",
                    [new TranscriptSegment(aliceId, 0, 1_000, "alice spoke", 0.95)])
            });
            var publisher = new CollectingPublisher();
            var transcriptJob = new GenerateMeetingTranscriptJob(
                db.DbContext,
                stt,
                publisher,
                NullLogger<GenerateMeetingTranscriptJob>.Instance);

            await transcriptJob.RunAsync(meetingId, orgId);
            await transcriptJob.RunAsync(meetingId, orgId);

            stt.Calls.Should().OnlyContain(key => key == aliceKey);
            stt.Calls.Should().HaveCount(2);

            var transcript = db.DbContext.MeetingTranscripts.Single(x => x.MeetingId == meetingId);
            transcript.CompletenessStatus.Should().Be(MeetingTranscriptCompletenessStatus.CompletedWithWarnings);
            transcript.ExpectedAudioFragmentCount.Should().Be(2);
            transcript.TranscribedAudioFragmentCount.Should().Be(1);
            transcript.TerminalFailedAudioFragmentCount.Should().Be(1);
            transcript.MissingAudioFragmentIdsJson.Should().NotContain(bobFragmentId.ToString());

            publisher.Notifications
                .OfType<MeetingTranscriptReadyEvent>()
                .Should()
                .BeEmpty();
        }

        [Fact]
        public async Task ExtractActionItemsJob_WithExistingItemsAndIncompleteTranscript_ShouldSkipWithoutEnqueueingDownstream()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);

            db.DbContext.MeetingTranscripts.Add(new MeetingTranscript
            {
                MeetingId = meetingId,
                OrganizationId = orgId,
                FullText = "partial transcript only",
                SegmentsJson = "[]",
                SttModel = "whisper-large-v3",
                GeneratedAtUtc = DateTime.UtcNow,
                CompletenessStatus = MeetingTranscriptCompletenessStatus.CompletedWithWarnings,
                ExpectedAudioFragmentCount = 2,
                TranscribedAudioFragmentCount = 1,
                RetryableFailedAudioFragmentCount = 1
            });
            db.DbContext.ActionItems.Add(new ActionItem
            {
                OrganizationId = orgId,
                MeetingId = meetingId,
                Title = "existing action item",
                Status = ActionItemStatus.PendingReview,
                ExtractedAtUtc = DateTime.UtcNow
            });
            await db.DbContext.SaveChangesAsync();

            var jobs = new FakeBackgroundJobClient();
            var tracker = new PostMeetingProcessingTracker(db.DbContext);
            var job = new ExtractActionItemsJob(
                db.DbContext,
                new UnreachableLlmService(),
                new PromptProvider(new TestHostEnvironment(AppContext.BaseDirectory)),
                Options.Create(new OpenAiCompatibleOptions
                {
                    Llm = new OpenAiCompatibleOptions.ProviderConfig
                    {
                        BaseUrl = "http://llm.test/v1",
                        ApiKey = "test-key",
                        Model = "openai-compatible-local"
                    }
                }),
                NullLogger<ExtractActionItemsJob>.Instance,
                tracker,
                jobs);

            await job.RunAsync(meetingId, orgId, CancellationToken.None);

            jobs.CreatedJobs.Should().BeEmpty();

            var snapshot = await tracker.GetLatestByMeetingAsync(orgId, meetingId);
            snapshot.Steps.Should().Contain(x =>
                x.StepType == PostMeetingProcessingStepType.ActionExtraction
                && x.Status == PostMeetingProcessingStatus.Skipped);
            snapshot.Events.Should().Contain(x =>
                x.StepType == PostMeetingProcessingStepType.ActionExtraction
                && x.ErrorCode == MeetingTranscriptCompletenessGuard.IncompleteErrorCode);
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

        private sealed class NoopEmbeddingService : IEmbeddingService
        {
            public EmbeddingMetadata Metadata { get; } = new("noop", "noop", 1);

            public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
                => Task.FromResult(Array.Empty<float>());

            public Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
                => Task.FromResult(texts.Select(_ => Array.Empty<float>()).ToArray());
        }

        private sealed class UnreachableLlmService : ILLMService
        {
            public Task<LLMResponse> CompleteAsync(LLMRequest request, CancellationToken cancellationToken)
                => throw new InvalidOperationException("LLM should not run when transcript is incomplete.");

            public Task<T> CompleteWithJsonAsync<T>(LLMRequest request, CancellationToken cancellationToken)
                => throw new InvalidOperationException("LLM should not run when transcript is incomplete.");
        }

        private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
        {
            public string EnvironmentName { get; set; } = Environments.Development;
            public string ApplicationName { get; set; } = "MeetingAssistant.Tests";
            public string ContentRootPath { get; set; } = contentRootPath;
            public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        }

        private sealed class FailOnceThenSucceedSttService(
            string failObjectKey,
            Exception firstFailure,
            IReadOnlyDictionary<string, TrackTranscriptionResult> resultsByObjectKey) : ISttService
        {
            private readonly ConcurrentDictionary<string, int> _attemptCounts = new(StringComparer.Ordinal);

            public Task<TrackTranscriptionResult> TranscribeTrackAsync(
                Guid participantUserId,
                string storageObjectKey,
                CancellationToken ct = default)
            {
                var attempt = _attemptCounts.AddOrUpdate(storageObjectKey, 1, (_, current) => current + 1);
                if (string.Equals(storageObjectKey, failObjectKey, StringComparison.Ordinal) && attempt == 1)
                {
                    throw firstFailure;
                }

                if (resultsByObjectKey.TryGetValue(storageObjectKey, out var result))
                {
                    return Task.FromResult(result);
                }

                throw new KeyNotFoundException($"No STT result configured for object key '{storageObjectKey}'.");
            }
        }
    }
}
