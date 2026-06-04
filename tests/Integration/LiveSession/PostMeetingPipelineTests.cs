using FluentAssertions;
using Livekit.Server.Sdk.Dotnet;
using MeetingAssistant.Features.ActionItems.Models.Entities;
using MeetingAssistant.Features.ActionItems.Models.Enums;
using MeetingAssistant.Features.LiveSession.Infrastructure;
using MeetingAssistant.Features.LiveSession.Handlers;
using MeetingAssistant.Features.LiveSession.Jobs;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.LiveSession.Models.Events;
using MeetingAssistant.Features.LiveSession.Models.PostProcessing;
using MeetingAssistant.Features.LiveSession.Services;
using MeetingAssistant.Features.LiveSession.Services.PostProcessing;
using static MeetingAssistant.Features.LiveSession.Services.TranscriptSourceIdentity;
using MeetingAssistant.Features.Meetings.Jobs;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Features.Rag.Jobs;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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
            var tracker = new PostMeetingProcessingTracker(db.DbContext);
            var summaryHandler = new GenerateMeetingSummaryHandler(jobs, tracker);
            var publisher = new CollectingPublisher(async (notification, ct) =>
            {
                if (notification is MeetingTranscriptReadyEvent transcriptReadyEvent)
                {
                    await summaryHandler.Handle(transcriptReadyEvent, ct);
                }
            });

            var transcriptHandler = new GenerateMeetingTranscriptHandler(jobs, tracker);
            await transcriptHandler.Handle(
                new ParticipantAudioReadyEvent(meetingId, orgId, DateTime.UtcNow),
                CancellationToken.None);

            jobs.CreatedJobs.Should()
                .ContainSingle(x => x.Type == typeof(GenerateMeetingTranscriptJob));

            var transcriptJob = new GenerateMeetingTranscriptJob(
                db.DbContext,
                stt,
                publisher,
                NullLogger<GenerateMeetingTranscriptJob>.Instance,
                tracker);

            await transcriptJob.RunAsync(meetingId, orgId);

            jobs.CreatedJobs.Should()
                .ContainSingle(x => x.Type == typeof(GenerateMeetingSummaryJob));

            var summaryJob = new GenerateMeetingSummaryJob(
                db.DbContext,
                summarizer,
                NullLogger<GenerateMeetingSummaryJob>.Instance,
                tracker,
                hangfireJobContextAccessor: null,
                jobs);

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
            DeserializePersistedSegments(transcript.SegmentsJson)
                .Select(x => x.TimestampOffsetSource)
                .Should()
                .AllBeEquivalentTo("legacy_unknown", "meetings without an actual room start anchor should preserve track-relative legacy timing");

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

            var transcriptGenerationId = jobs.CreatedJobs
                .Single(x => x.Type == typeof(GenerateMeetingTranscriptJob))
                .Args[2] as Guid?;
            transcriptGenerationId.Should().NotBeNull();

            var summaryGenerationId = jobs.CreatedJobs
                .Single(x => x.Type == typeof(GenerateMeetingSummaryJob))
                .Args[2] as Guid?;
            summaryGenerationId.Should().Be(transcriptGenerationId);

            var tagSuggestionGenerationId = jobs.CreatedJobs
                .Single(x => x.Type == typeof(SuggestMeetingTagsJob))
                .Args[2] as Guid?;
            var knowledgeGenerationId = jobs.CreatedJobs
                .Single(x => x.Type == typeof(ReindexMeetingKnowledgeJob))
                .Args[2] as Guid?;
            tagSuggestionGenerationId.Should().Be(transcriptGenerationId);
            knowledgeGenerationId.Should().Be(transcriptGenerationId);

            var snapshot = await tracker.GetLatestByMeetingAsync(orgId, meetingId);
            snapshot.Run!.Status.Should().Be(PostMeetingProcessingStatus.Completed);
            snapshot.Run.CompletedAtUtc.Should().NotBeNull();
            snapshot.Steps.Should().Contain(x =>
                x.StepType == PostMeetingProcessingStepType.TranscriptPersistence
                && x.Status == PostMeetingProcessingStatus.Completed);
            snapshot.Steps.Should().Contain(x =>
                x.StepType == PostMeetingProcessingStepType.SummaryGeneration
                && x.Status == PostMeetingProcessingStatus.Completed);
            snapshot.Steps.Should().Contain(x =>
                x.StepType == PostMeetingProcessingStepType.TagSuggestion
                && x.Status == PostMeetingProcessingStatus.Pending);
            snapshot.Steps.Should().Contain(x =>
                x.StepType == PostMeetingProcessingStepType.KnowledgeIndexing
                && x.Status == PostMeetingProcessingStatus.Pending);
        }

        [Fact]
        public async Task TranscriptGeneration_ShouldMergeAssistantSpeechTracesUsingRoomRelativeMeetingStart()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);
            var roomStartedAtUtc = new DateTime(2026, 06, 02, 12, 00, 00, DateTimeKind.Utc);

            var meeting = db.DbContext.Meetings.Single(x => x.Id == meetingId);
            meeting.RoomActivatedAtUtc = roomStartedAtUtc;

            var aliceId = db.SeedUser("Alice");
            db.AddParticipant(meetingId, orgId, aliceId);

            const string aliceTrackSid = "TR_ALICE";
            var aliceKey = $"tracks/mtg-{meetingId}/user:{aliceId}/track-{aliceTrackSid}.ogg";
            db.AddAvailableAudioFragment(
                meetingId,
                orgId,
                aliceId,
                aliceKey,
                aliceTrackSid,
                trackPublishedAtUtc: roomStartedAtUtc.AddSeconds(3));

            db.DbContext.AiAssistantTraceEvents.AddRange(
                new AiAssistantTraceEvent
                {
                    OrganizationId = orgId,
                    MeetingId = meetingId,
                    SessionId = "agent-session-1",
                    TurnId = "agent-turn-1",
                    Sequence = 50,
                    EventType = AiAssistantTraceEventTypes.AssistantSpeechCompleted,
                    OccurredAtUtc = roomStartedAtUtc.AddSeconds(8),
                    State = "speaking",
                    StepType = "tts",
                    StepVoice = "alloy",
                    DurationMs = 1_500,
                    Text = "Hello Alice, I can help with that.",
                    CreatedAtUtc = roomStartedAtUtc.AddSeconds(8)
                },
                new AiAssistantTraceEvent
                {
                    OrganizationId = orgId,
                    MeetingId = meetingId,
                    SessionId = "agent-session-1",
                    TurnId = "agent-turn-1",
                    Sequence = 50,
                    EventType = AiAssistantTraceEventTypes.AssistantSpeechCompleted,
                    OccurredAtUtc = roomStartedAtUtc.AddSeconds(8),
                    State = "speaking",
                    StepType = "tts",
                    StepVoice = "alloy",
                    DurationMs = 1_500,
                    Text = "Hello Alice, I can help with that.",
                    CreatedAtUtc = roomStartedAtUtc.AddSeconds(9)
                });
            await db.DbContext.SaveChangesAsync();

            var stt = new StubSttService(new Dictionary<string, TrackTranscriptionResult>
            {
                [aliceKey] = new(
                    "whisper-large-v3",
                    [
                        new TranscriptSegment(aliceId, 2_000, 3_000, "Hello assistant", 0.95),
                        new TranscriptSegment(aliceId, 9_000, 10_000, "Thanks for helping", 0.93)
                    ])
            });

            var publisher = new CollectingPublisher();
            var transcriptJob = new GenerateMeetingTranscriptJob(
                db.DbContext,
                stt,
                publisher,
                NullLogger<GenerateMeetingTranscriptJob>.Instance);

            await transcriptJob.RunAsync(meetingId, orgId);
            await transcriptJob.RunAsync(meetingId, orgId);

            var transcript = db.DbContext.MeetingTranscripts.Single(x => x.MeetingId == meetingId);
            transcript.FullText.Split(Environment.NewLine, StringSplitOptions.None).Should().Equal(
                "[00:00:05 Alice] Hello assistant",
                "[00:00:08 AI Assistant] Hello Alice, I can help with that.",
                "[00:00:12 Alice] Thanks for helping");

            publisher.Notifications
                .OfType<MeetingTranscriptReadyEvent>()
                .Should()
                .ContainSingle("the second retry-safe transcript generation should not duplicate or republish unchanged assistant speech");

            var segments = DeserializePersistedSegments(transcript.SegmentsJson);
            segments.Should().HaveCount(3);
            var assistantSegment = segments.Single(x => x.SpeakerRole == "assistant");
            assistantSegment.ParticipantUserId.Should().BeNull();
            assistantSegment.SpeakerDisplayName.Should().Be("AI Assistant");
            assistantSegment.Source.Should().Be("ai_assistant_trace");
            assistantSegment.TraceEventId.Should().NotBeNullOrWhiteSpace();
            assistantSegment.SessionId.Should().Be("agent-session-1");
            assistantSegment.TurnId.Should().Be("agent-turn-1");
            assistantSegment.StartMs.Should().Be(8_000);
            assistantSegment.RoomRelativeStartMs.Should().Be(8_000);
            assistantSegment.AbsoluteStartUtc.Should().Be(roomStartedAtUtc.AddSeconds(8));
            assistantSegment.TimestampOffsetSource.Should().Be(AiAssistantTraceEventTypes.AssistantSpeechCompleted);

            var summarizer = new StubSummarizerService(new SummaryResult(
                "Summary includes assistant response.",
                "gpt-4o-mini",
                10,
                5));
            var summaryJob = new GenerateMeetingSummaryJob(
                db.DbContext,
                summarizer,
                NullLogger<GenerateMeetingSummaryJob>.Instance);

            await summaryJob.RunAsync(meetingId, orgId);

            summarizer.LastTranscript.Should().Be(transcript.FullText);
            summarizer.LastTranscript.Should().Contain("[00:00:08 AI Assistant] Hello Alice, I can help with that.");
        }

        [Fact]
        public async Task TranscriptGeneration_ShouldPersistAssistantOnlyTranscript_WhenNoParticipantFragmentsExist()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);
            var roomStartedAtUtc = new DateTime(2026, 06, 02, 12, 30, 00, DateTimeKind.Utc);

            var meeting = db.DbContext.Meetings.Single(x => x.Id == meetingId);
            meeting.RoomActivatedAtUtc = roomStartedAtUtc;
            db.DbContext.AiAssistantTraceEvents.Add(new AiAssistantTraceEvent
            {
                OrganizationId = orgId,
                MeetingId = meetingId,
                SessionId = "agent-session-only",
                TurnId = "agent-turn-only",
                Sequence = 50,
                EventType = AiAssistantTraceEventTypes.AssistantSpeechCompleted,
                OccurredAtUtc = roomStartedAtUtc.AddSeconds(6),
                StepType = "tts",
                Text = "I am ready to help when participants join."
            });
            await db.DbContext.SaveChangesAsync();

            var stt = new StubSttService(new Dictionary<string, TrackTranscriptionResult>());
            var publisher = new CollectingPublisher();
            var transcriptJob = new GenerateMeetingTranscriptJob(
                db.DbContext,
                stt,
                publisher,
                NullLogger<GenerateMeetingTranscriptJob>.Instance);

            await transcriptJob.RunAsync(meetingId, orgId);
            await transcriptJob.RunAsync(meetingId, orgId);

            stt.Calls.Should().BeEmpty();
            var transcript = db.DbContext.MeetingTranscripts.Single(x => x.MeetingId == meetingId);
            transcript.FullText.Should().Be("[00:00:06 AI Assistant] I am ready to help when participants join.");
            transcript.SttModel.Should().BeEmpty();

            var segment = DeserializePersistedSegments(transcript.SegmentsJson).Should().ContainSingle().Subject;
            segment.SpeakerRole.Should().Be("assistant");
            segment.ParticipantUserId.Should().BeNull();
            segment.RoomRelativeStartMs.Should().Be(6_000);
            segment.AbsoluteStartUtc.Should().Be(roomStartedAtUtc.AddSeconds(6));

            publisher.Notifications
                .OfType<MeetingTranscriptReadyEvent>()
                .Should()
                .ContainSingle();
        }

        [Fact]
        public async Task TranscriptGeneration_ShouldRecoverParticipantAndAssistantTranscriptFromRuntimeAiTraceEvents_WhenAudioFragmentsAreMissing()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);
            var roomStartedAtUtc = new DateTime(2026, 06, 02, 13, 00, 00, DateTimeKind.Utc);

            var meeting = db.DbContext.Meetings.Single(x => x.Id == meetingId);
            meeting.RoomActivatedAtUtc = roomStartedAtUtc;

            var aliceId = db.SeedUser("Alice");
            db.AddParticipant(meetingId, orgId, aliceId);

            db.DbContext.AiAssistantTraceEvents.AddRange(
                new AiAssistantTraceEvent
                {
                    OrganizationId = orgId,
                    MeetingId = meetingId,
                    SessionId = "trace-session-1",
                    TurnId = "trace-turn-1",
                    Sequence = 1,
                    EventType = AiAssistantTraceEventTypes.TurnStarted,
                    ParticipantIdentity = $"user:{aliceId}",
                    OccurredAtUtc = roomStartedAtUtc.AddSeconds(4),
                    State = "listening",
                    StepType = "turn"
                },
                new AiAssistantTraceEvent
                {
                    OrganizationId = orgId,
                    MeetingId = meetingId,
                    SessionId = "trace-session-1",
                    TurnId = "trace-turn-1",
                    Sequence = 20,
                    EventType = AiAssistantTraceEventTypes.SttCompleted,
                    OccurredAtUtc = roomStartedAtUtc.AddSeconds(9),
                    StepType = "stt",
                    StepProvider = "OpenAICompatible",
                    Text = "Please recover this transcript from traces."
                },
                new AiAssistantTraceEvent
                {
                    OrganizationId = orgId,
                    MeetingId = meetingId,
                    SessionId = "trace-session-1",
                    TurnId = "trace-turn-1",
                    Sequence = 30,
                    EventType = AiAssistantTraceEventTypes.LlmCompleted,
                    OccurredAtUtc = roomStartedAtUtc.AddSeconds(12),
                    StepType = "llm",
                    StepProvider = "OpenAI-compatible",
                    Text = "Recovered from the LLM trace."
                },
                new AiAssistantTraceEvent
                {
                    OrganizationId = orgId,
                    MeetingId = meetingId,
                    SessionId = "trace-session-1",
                    TurnId = "trace-turn-1",
                    Sequence = 40,
                    EventType = AiAssistantTraceEventTypes.TtsCompleted,
                    OccurredAtUtc = roomStartedAtUtc.AddSeconds(13),
                    StepType = "tts",
                    StepProvider = "ElevenLabs",
                    Text = "Recovered"
                },
                new AiAssistantTraceEvent
                {
                    OrganizationId = orgId,
                    MeetingId = meetingId,
                    SessionId = "trace-session-1",
                    TurnId = "trace-turn-1",
                    Sequence = 40,
                    EventType = AiAssistantTraceEventTypes.TtsCompleted,
                    OccurredAtUtc = roomStartedAtUtc.AddSeconds(14),
                    StepType = "tts",
                    StepProvider = "ElevenLabs",
                    Text = "from the TTS trace."
                });
            await db.DbContext.SaveChangesAsync();

            var stt = new StubSttService(new Dictionary<string, TrackTranscriptionResult>());
            var publisher = new CollectingPublisher();
            var transcriptJob = new GenerateMeetingTranscriptJob(
                db.DbContext,
                stt,
                publisher,
                NullLogger<GenerateMeetingTranscriptJob>.Instance);

            await transcriptJob.RunAsync(meetingId, orgId);
            await transcriptJob.RunAsync(meetingId, orgId);

            stt.Calls.Should().BeEmpty();
            var transcript = db.DbContext.MeetingTranscripts.Single(x => x.MeetingId == meetingId);
            transcript.FullText.Split(Environment.NewLine, StringSplitOptions.None).Should().Equal(
                "[00:00:09 Alice] Please recover this transcript from traces.",
                "[00:00:12 AI Assistant] Recovered from the LLM trace.");
            transcript.SttModel.Should().Be("ai-debug-trace");

            var segments = DeserializePersistedSegments(transcript.SegmentsJson);
            segments.Should().HaveCount(2);
            var participantSegment = segments.Single(x => x.SpeakerRole == "participant");
            participantSegment.ParticipantUserId.Should().Be(aliceId);
            participantSegment.Source.Should().Be("ai_assistant_trace");
            participantSegment.TimestampOffsetSource.Should().Be(AiAssistantTraceEventTypes.SttCompleted);

            var assistantSegment = segments.Single(x => x.SpeakerRole == "assistant");
            assistantSegment.Source.Should().Be("ai_assistant_trace");
            assistantSegment.TimestampOffsetSource.Should().Be(AiAssistantTraceEventTypes.LlmCompleted);

            publisher.Notifications
                .OfType<MeetingTranscriptReadyEvent>()
                .Should()
                .ContainSingle("the second retry-safe transcript generation should not duplicate or republish unchanged trace recovery output");
        }

        [Fact]
        public async Task PersonalizedSummaryGeneration_ShouldGenerateOnlyEligibleParticipantsAndPersistSkipAudit()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);

            var aliceId = db.SeedUser("Alice");
            var bobId = db.SeedUser("Bob");
            var charlieId = db.SeedUser("Charlie");
            var daveId = db.SeedUser("Dave");
            var eveId = db.SeedUser("Eve");
            var aliceParticipantId = db.AddParticipant(meetingId, orgId, aliceId);
            db.AddParticipant(meetingId, orgId, bobId);
            var charlieParticipantId = db.AddParticipant(meetingId, orgId, charlieId);
            db.AddParticipant(meetingId, orgId, daveId);
            db.AddParticipant(meetingId, orgId, eveId);

            db.DbContext.UserOrgMemberships.AddRange(
                new UserOrgMembership
                {
                    OrganizationId = orgId,
                    UserId = aliceId,
                    OrgRole = OrganizationRole.Member,
                    JobRole = "Backend Lead",
                    Context = "Owns API launch readiness.",
                    IsEnabled = true
                },
                new UserOrgMembership
                {
                    OrganizationId = orgId,
                    UserId = bobId,
                    OrgRole = OrganizationRole.Member,
                    IsEnabled = true
                },
                new UserOrgMembership
                {
                    OrganizationId = orgId,
                    UserId = charlieId,
                    OrgRole = OrganizationRole.Member,
                    IsEnabled = true
                },
                new UserOrgMembership
                {
                    OrganizationId = orgId,
                    UserId = daveId,
                    OrgRole = OrganizationRole.Member,
                    IsEnabled = true
                },
                new UserOrgMembership
                {
                    OrganizationId = orgId,
                    UserId = eveId,
                    OrgRole = OrganizationRole.Member,
                    IsEnabled = true
                });
            db.DbContext.ActionItems.AddRange(
                new ActionItem
                {
                    OrganizationId = orgId,
                    MeetingId = meetingId,
                    Title = "Review API launch checklist",
                    AssignedToUserId = aliceId,
                    Status = ActionItemStatus.PendingReview,
                    ExtractedAtUtc = DateTime.UtcNow
                },
                new ActionItem
                {
                    OrganizationId = orgId,
                    MeetingId = meetingId,
                    Title = "Read the meeting recap",
                    AssignedToParticipantId = charlieParticipantId,
                    Status = ActionItemStatus.PendingReview,
                    ExtractedAtUtc = DateTime.UtcNow
                });
            db.DbContext.MeetingTranscripts.Add(new MeetingTranscript
            {
                OrganizationId = orgId,
                MeetingId = meetingId,
                FullText = "[00:00:01 Alice] Launch readiness is green. Dave should be kept in the release notes.\n[00:00:05 Bob] Timeline remains unchanged.",
                SegmentsJson = SerializePersistedSegments(
                    CreatePersistedSegment(aliceId, "Alice", "Launch readiness is green. Dave should be kept in the release notes."),
                    CreatePersistedSegment(bobId, "Bob", "Timeline remains unchanged.")),
                SttModel = "test-stt",
                GeneratedAtUtc = DateTime.UtcNow
            });
            await db.DbContext.SaveChangesAsync();

            var summarizer = new StubSummarizerService(new SummaryResult(
                "fallback personalized summary",
                "personalized-model",
                1,
                1,
                "PersonalizedMeetingSummarizer",
                "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
            summarizer.EnqueuePersonalizedResult(new SummaryResult(
                "Alice personalized summary v1",
                "personalized-model",
                11,
                5,
                "PersonalizedMeetingSummarizer",
                "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
            summarizer.EnqueuePersonalizedResult(new SummaryResult(
                "Bob personalized summary v1",
                "personalized-model",
                12,
                6,
                "PersonalizedMeetingSummarizer",
                "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
            summarizer.EnqueuePersonalizedResult(new SummaryResult(
                "Charlie personalized summary v1",
                "personalized-model",
                13,
                7,
                "PersonalizedMeetingSummarizer",
                "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
            summarizer.EnqueuePersonalizedResult(new SummaryResult(
                "Dave personalized summary v1",
                "personalized-model",
                14,
                8,
                "PersonalizedMeetingSummarizer",
                "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
            var tracker = new PostMeetingProcessingTracker(db.DbContext);
            var job = new GeneratePersonalizedMeetingSummariesJob(
                db.DbContext,
                summarizer,
                NullLogger<GeneratePersonalizedMeetingSummariesJob>.Instance,
                tracker);

            await job.RunAsync(meetingId, orgId);

            db.DbContext.PersonalizedMeetingSummaries.Should().HaveCount(5);
            db.DbContext.PersonalizedMeetingSummaries.Count(x => x.Status == PersonalizedMeetingSummaryStatus.Generated).Should().Be(4);
            db.DbContext.PersonalizedMeetingSummaries.Should().ContainSingle(x => x.Status == PersonalizedMeetingSummaryStatus.Skipped && x.UserId == eveId);
            var aliceSummary = db.DbContext.PersonalizedMeetingSummaries.Single(x => x.UserId == aliceId);
            var aliceSummaryIdAfterFirstRun = aliceSummary.Id;
            var eveSummaryIdAfterFirstRun = db.DbContext.PersonalizedMeetingSummaries.Single(x => x.UserId == eveId).Id;
            aliceSummary.MeetingParticipantId.Should().Be(aliceParticipantId);
            aliceSummary.Status.Should().Be(PersonalizedMeetingSummaryStatus.Generated);
            aliceSummary.SummaryText.Should().Be("Alice personalized summary v1");
            aliceSummary.TargetDisplayName.Should().Be("Alice");
            aliceSummary.PromptName.Should().Be("PersonalizedMeetingSummarizer");
            aliceSummary.PromptVersion.Should().MatchRegex("^sha256:[0-9a-f]{64}$");
            aliceSummary.PersonalizationContextJson.Should().Contain("Backend Lead");
            aliceSummary.PersonalizationContextJson.Should().Contain("Owns API launch readiness.");
            aliceSummary.PersonalizationContextJson.Should().Contain("Review API launch checklist");
            aliceSummary.EligibilityReason.Should().Be("personalization_signal");
            aliceSummary.EligibilityContextJson.Should().Contain("\"hasJobRole\":true");
            aliceSummary.EligibilityContextJson.Should().Contain("\"hasOrganizationContext\":true");
            aliceSummary.EligibilityContextJson.Should().Contain("\"hasStrongPersonalizationSignal\":true");
            summarizer.PersonalizedCalls.Should().ContainSingle(x =>
                x.Participant == "Alice"
                && x.PersonalizationContext != null
                && x.PersonalizationContext.Contains("Job role: Backend Lead")
                && x.PersonalizationContext.Contains("Context: Owns API launch readiness.")
                && x.PersonalizationContext.Contains("Review API launch checklist"));
            summarizer.PersonalizedCalls.Should().ContainSingle(x =>
                x.Participant == "Bob" && x.PersonalizationContext == null);
            summarizer.PersonalizedCalls.Should().ContainSingle(x =>
                x.Participant == "Charlie"
                && x.PersonalizationContext != null
                && x.PersonalizationContext.Contains("Read the meeting recap"));
            summarizer.PersonalizedCalls.Should().ContainSingle(x =>
                x.Participant == "Dave" && x.PersonalizationContext == null);
            summarizer.PersonalizedCalls.Should().NotContain(x => x.Participant == "Eve");

            var bobSummary = db.DbContext.PersonalizedMeetingSummaries.Single(x => x.UserId == bobId);
            bobSummary.Status.Should().Be(PersonalizedMeetingSummaryStatus.Generated);
            bobSummary.EligibilityReason.Should().Be("participant_spoke");
            bobSummary.PersonalizationContextJson.Should().BeNull();

            var daveSummary = db.DbContext.PersonalizedMeetingSummaries.Single(x => x.UserId == daveId);
            daveSummary.Status.Should().Be(PersonalizedMeetingSummaryStatus.Generated);
            daveSummary.EligibilityReason.Should().Be("participant_mentioned");
            daveSummary.EligibilityContextJson.Should().Contain("Dave");
            daveSummary.PersonalizationContextJson.Should().BeNull();

            var eveSummary = db.DbContext.PersonalizedMeetingSummaries.Single(x => x.UserId == eveId);
            eveSummary.Status.Should().Be(PersonalizedMeetingSummaryStatus.Skipped);
            eveSummary.SummaryText.Should().BeNull();
            eveSummary.LlmModel.Should().BeNull();
            eveSummary.GeneratedAtUtc.Should().BeNull();
            eveSummary.EligibilityReason.Should().Be("no_personalization_signal_or_transcript_relevance");
            eveSummary.EligibilityContextJson.Should().Contain("\"decision\":\"skip\"");

            summarizer.EnqueuePersonalizedResult(new SummaryResult(
                "Alice personalized summary v2",
                "personalized-model",
                21,
                15,
                "PersonalizedMeetingSummarizer",
                "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
            summarizer.EnqueuePersonalizedResult(new SummaryResult(
                "Bob personalized summary v2",
                "personalized-model",
                22,
                16,
                "PersonalizedMeetingSummarizer",
                "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
            summarizer.EnqueuePersonalizedResult(new SummaryResult(
                "Charlie personalized summary v2",
                "personalized-model",
                23,
                17,
                "PersonalizedMeetingSummarizer",
                "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
            summarizer.EnqueuePersonalizedResult(new SummaryResult(
                "Dave personalized summary v2",
                "personalized-model",
                24,
                18,
                "PersonalizedMeetingSummarizer",
                "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));

            await job.RunAsync(meetingId, orgId);

            db.DbContext.PersonalizedMeetingSummaries.Should().HaveCount(5);
            db.DbContext.PersonalizedMeetingSummaries.Single(x => x.UserId == aliceId).Id.Should().Be(aliceSummaryIdAfterFirstRun);
            db.DbContext.PersonalizedMeetingSummaries.Single(x => x.UserId == aliceId).SummaryText.Should().Be("Alice personalized summary v2");
            db.DbContext.PersonalizedMeetingSummaries.Single(x => x.UserId == eveId).Id.Should().Be(eveSummaryIdAfterFirstRun);
            db.DbContext.PersonalizedMeetingSummaries.Single(x => x.UserId == eveId).Status.Should().Be(PersonalizedMeetingSummaryStatus.Skipped);
            db.DbContext.PersonalizedMeetingSummaries.Single(x => x.UserId == eveId).SummaryText.Should().BeNull();
            summarizer.PersonalizedCalls.Should().HaveCount(8);
            var snapshot = await tracker.GetLatestByMeetingAsync(orgId, meetingId);
            snapshot.Steps.Should().Contain(x =>
                x.StepType == PostMeetingProcessingStepType.PersonalizedSummaryGeneration
                && x.Status == PostMeetingProcessingStatus.Completed
                && x.ArtifactType == "personalized_meeting_summary");
        }

        [Fact]
        public async Task PersonalizedSummaryGeneration_WhenDuplicateInsertWinsRace_ShouldRetryAndPersistOneSummaryPerParticipant()
        {
            Guid? raceAliceUserId = null;
            Guid? raceAliceParticipantId = null;
            Guid? raceMeetingId = null;
            Guid? raceOrganizationId = null;

            await using var db = await LiveSessionTestDb.CreateAsync(interceptors:
            [
                new PersonalizedSummaryRaceSaveChangesInterceptor(async (context, pendingSummaries, cancellationToken) =>
                {
                    if (raceAliceUserId is null
                        || raceMeetingId is null
                        || raceOrganizationId is null
                        || raceAliceParticipantId is null)
                    {
                        return;
                    }

                    var alicePending = pendingSummaries.FirstOrDefault(x => x.UserId == raceAliceUserId.Value);
                    if (alicePending == null)
                    {
                        return;
                    }

                    await InsertConflictingPersonalizedSummaryAsync(
                        context,
                        raceMeetingId.Value,
                        raceOrganizationId.Value,
                        raceAliceParticipantId.Value,
                        raceAliceUserId.Value,
                        "race placeholder summary",
                        cancellationToken);
                })
            ]);
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);
            raceOrganizationId = orgId;
            raceMeetingId = meetingId;

            raceAliceUserId = db.SeedUser("Alice");
            var raceBobUserId = db.SeedUser("Bob");
            raceAliceParticipantId = db.AddParticipant(meetingId, orgId, raceAliceUserId.Value);
            db.AddParticipant(meetingId, orgId, raceBobUserId);

            db.DbContext.UserOrgMemberships.Add(new UserOrgMembership
            {
                OrganizationId = orgId,
                UserId = raceAliceUserId.Value,
                OrgRole = OrganizationRole.Member,
                JobRole = "Backend Lead",
                IsEnabled = true
            });
            var transcript = new MeetingTranscript
            {
                OrganizationId = orgId,
                MeetingId = meetingId,
                FullText = "[00:00:01 Alice] Launch readiness is green.\n[00:00:05 Bob] Timeline remains unchanged.",
                SegmentsJson = SerializePersistedSegments(
                    CreatePersistedSegment(raceAliceUserId.Value, "Alice", "Launch readiness is green."),
                    CreatePersistedSegment(raceBobUserId, "Bob", "Timeline remains unchanged.")),
                SttModel = "test-stt",
                GeneratedAtUtc = DateTime.UtcNow
            };
            InitializeNew(transcript, transcript.FullText);
            db.DbContext.MeetingTranscripts.Add(transcript);
            await db.DbContext.SaveChangesAsync();

            var summarizer = new StubSummarizerService(new SummaryResult(
                "fallback personalized summary",
                "personalized-model",
                1,
                1,
                "PersonalizedMeetingSummarizer",
                "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
            summarizer.EnqueuePersonalizedResult(new SummaryResult(
                "Alice personalized summary",
                "personalized-model",
                11,
                5,
                "PersonalizedMeetingSummarizer",
                "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
            summarizer.EnqueuePersonalizedResult(new SummaryResult(
                "Bob personalized summary",
                "personalized-model",
                12,
                6,
                "PersonalizedMeetingSummarizer",
                "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
            var tracker = new PostMeetingProcessingTracker(db.DbContext);
            var job = new GeneratePersonalizedMeetingSummariesJob(
                db.DbContext,
                summarizer,
                NullLogger<GeneratePersonalizedMeetingSummariesJob>.Instance,
                tracker);

            var run = () => job.RunAsync(meetingId, orgId);
            await run.Should().NotThrowAsync();

            db.DbContext.ChangeTracker.Clear();
            var summaries = await db.DbContext.PersonalizedMeetingSummaries
                .IgnoreQueryFilters()
                .Where(x => x.MeetingId == meetingId && x.OrganizationId == orgId)
                .ToListAsync();
            summaries.Should().HaveCount(2);
            summaries.Select(x => x.UserId).Should().BeEquivalentTo([raceAliceUserId.Value, raceBobUserId]);

            var aliceSummary = summaries.Single(x => x.UserId == raceAliceUserId.Value);
            aliceSummary.Status.Should().Be(PersonalizedMeetingSummaryStatus.Generated);
            aliceSummary.SummaryText.Should().Be("Alice personalized summary");
            aliceSummary.SummaryText.Should().NotBe("race placeholder summary");
            aliceSummary.SourceTranscriptId.Should().Be(transcript.Id);
            aliceSummary.SourceTranscriptHash.Should().Be(transcript.TranscriptHash);
            aliceSummary.SourceTranscriptRevision.Should().Be(transcript.TranscriptRevision);

            var bobSummary = summaries.Single(x => x.UserId == raceBobUserId);
            bobSummary.Status.Should().Be(PersonalizedMeetingSummaryStatus.Generated);
            bobSummary.SummaryText.Should().Be("Bob personalized summary");
            bobSummary.SourceTranscriptId.Should().Be(transcript.Id);
            bobSummary.SourceTranscriptHash.Should().Be(transcript.TranscriptHash);
            bobSummary.SourceTranscriptRevision.Should().Be(transcript.TranscriptRevision);

            var snapshot = await tracker.GetLatestByMeetingAsync(orgId, meetingId);
            snapshot.Steps.Should().Contain(x =>
                x.StepType == PostMeetingProcessingStepType.PersonalizedSummaryGeneration
                && x.Status == PostMeetingProcessingStatus.Completed);
        }

        private static async Task InsertConflictingPersonalizedSummaryAsync(
            DbContext context,
            Guid meetingId,
            Guid organizationId,
            Guid meetingParticipantId,
            Guid userId,
            string summaryText,
            CancellationToken cancellationToken)
        {
            var connection = (SqliteConnection)context.Database.GetDbConnection();
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connection)
                .Options;

            await using var raceContext = new ApplicationDbContext(
                options,
                new Microsoft.AspNetCore.Http.HttpContextAccessor(),
                new StaticTenantProvider(organizationId),
                new NoopPublisher());
            raceContext.PersonalizedMeetingSummaries.Add(new PersonalizedMeetingSummary
            {
                MeetingId = meetingId,
                OrganizationId = organizationId,
                MeetingParticipantId = meetingParticipantId,
                UserId = userId,
                Status = PersonalizedMeetingSummaryStatus.Generated,
                SummaryText = summaryText,
                TargetDisplayName = "Alice",
                GeneratedAtUtc = DateTime.UtcNow.AddMinutes(-5)
            });
            await raceContext.SaveChangesAsync(cancellationToken);
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
            segments.Select(x => x.ParticipantAudioFragmentId).Should().Equal(fragments.Select(x => (Guid?)x.Id));
            segments.Select(x => x.TimestampOffsetSource).Should().AllBeEquivalentTo("fragment_track_published");
        }

        private static string SerializePersistedSegments(params PersistedSegmentForTest[] segments)
            => JsonSerializer.Serialize(segments);

        private static PersistedSegmentForTest CreatePersistedSegment(Guid participantUserId, string speakerDisplayName, string text)
            => new(
                Version: 3,
                SpeakerRole: "participant",
                ParticipantUserId: participantUserId,
                ParticipantAudioTrackId: null,
                ParticipantAudioFragmentId: null,
                TrackRelativeStartMs: 0,
                TrackRelativeEndMs: 1_000,
                RoomRelativeStartMs: 0,
                RoomRelativeEndMs: 1_000,
                AbsoluteStartUtc: null,
                AbsoluteEndUtc: null,
                StartMs: 0,
                EndMs: 1_000,
                Text: text,
                AvgLogProb: null,
                TimestampOffsetSource: "test",
                SpeakerDisplayName: speakerDisplayName,
                Source: "test",
                TraceEventId: null,
                SessionId: null,
                TurnId: null);

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

        [Fact]
        public async Task GenerateMeetingSummaryJob_ShouldUpdateSourceFieldsWhenTranscriptChanges()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId, MeetingStatus.Completed);
            const string initialText = "[00:00:01 Alice] Initial transcript.";
            var transcript = new MeetingTranscript
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgId,
                MeetingId = meetingId,
                FullText = initialText,
                GeneratedAtUtc = DateTime.UtcNow,
                SttModel = "test"
            };
            InitializeNew(transcript, initialText);
            db.DbContext.MeetingTranscripts.Add(transcript);
            var summary = new MeetingSummary
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgId,
                MeetingId = meetingId,
                SummaryText = "Old summary",
                LlmModel = "old",
                GeneratedAtUtc = DateTime.UtcNow.AddMinutes(-5),
                SourceTranscriptHash = transcript.TranscriptHash,
                SourceTranscriptRevision = transcript.TranscriptRevision
            };
            db.DbContext.MeetingSummaries.Add(summary);
            await db.DbContext.SaveChangesAsync();

            transcript.FullText = "[00:00:01 Alice] Revised transcript.";
            ApplyContentRevision(transcript, transcript.FullText);
            await db.DbContext.SaveChangesAsync();

            var summarizer = new StubSummarizerService(new SummaryResult("New summary", "gpt-test", 1, 1));
            var job = new GenerateMeetingSummaryJob(
                db.DbContext,
                summarizer,
                NullLogger<GenerateMeetingSummaryJob>.Instance);
            await job.RunAsync(meetingId, orgId, CancellationToken.None);

            var updated = await db.DbContext.MeetingSummaries.SingleAsync(x => x.MeetingId == meetingId);
            updated.SummaryText.Should().Be("New summary");
            updated.SourceTranscriptHash.Should().Be(transcript.TranscriptHash);
            updated.SourceTranscriptRevision.Should().Be(transcript.TranscriptRevision);
        }

        [Fact]
        public async Task GenerateMeetingSummaryJob_ShouldLeaveExistingSummaryWhenLlmFails()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId, MeetingStatus.Completed);
            const string fullText = "[00:00:01 Alice] Transcript.";
            var transcript = new MeetingTranscript
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgId,
                MeetingId = meetingId,
                FullText = fullText,
                GeneratedAtUtc = DateTime.UtcNow,
                SttModel = "test"
            };
            InitializeNew(transcript, fullText);
            db.DbContext.MeetingTranscripts.Add(transcript);
            var summary = new MeetingSummary
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgId,
                MeetingId = meetingId,
                SummaryText = "Keep this summary",
                LlmModel = "old",
                GeneratedAtUtc = DateTime.UtcNow,
                SourceTranscriptHash = "stale-hash",
                SourceTranscriptRevision = 1
            };
            db.DbContext.MeetingSummaries.Add(summary);
            await db.DbContext.SaveChangesAsync();

            var job = new GenerateMeetingSummaryJob(
                db.DbContext,
                new ThrowingSummarizerService(),
                NullLogger<GenerateMeetingSummaryJob>.Instance);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                job.RunAsync(meetingId, orgId, CancellationToken.None));

            var unchanged = await db.DbContext.MeetingSummaries.SingleAsync(x => x.MeetingId == meetingId);
            unchanged.SummaryText.Should().Be("Keep this summary");
            unchanged.SourceTranscriptHash.Should().Be("stale-hash");
        }

        private sealed class ThrowingSummarizerService : ISummarizerService
        {
            public Task<SummaryResult> SummarizeAsync(string fullTranscript, CancellationToken ct = default)
                => throw new InvalidOperationException("summary failed");

            public Task<SummaryResult> SummarizePersonalizedAsync(
                string fullTranscript,
                string participant,
                string? personalizationContext = null,
                CancellationToken ct = default)
                => throw new InvalidOperationException("summary failed");
        }

        private sealed record PersistedSegmentForTest(
            int Version,
            string? SpeakerRole,
            Guid? ParticipantUserId,
            Guid? ParticipantAudioTrackId,
            Guid? ParticipantAudioFragmentId,
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
            string TimestampOffsetSource,
            string? SpeakerDisplayName,
            string? Source,
            string? TraceEventId,
            string? SessionId,
            string? TurnId);
    }
}
