using FluentAssertions;
using MeetingAssistant.Features.ActionItems.Jobs;
using MeetingAssistant.Features.ActionItems.Models;
using MeetingAssistant.Features.ActionItems.Models.Enums;
using MeetingAssistant.Features.LiveSession.Infrastructure;
using MeetingAssistant.Features.LiveSession.Jobs;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.LiveSession.Models.PostProcessing;
using MeetingAssistant.Features.LiveSession.Services;
using MeetingAssistant.Features.LiveSession.Services.PostProcessing;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Infrastructure.AI;
using MeetingAssistant.Infrastructure.AI.DTOs;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using tests.Integration.LiveSession;
using Xunit;

namespace MeetingAssistant.Tests.Integration.ActionItems;

public sealed class ExtractActionItemsJobPromptTests
{
    [Fact]
    public async Task RunAsync_UsesAiWorkTaskPromptAndMapsTaskSchemaToActionItem()
    {
        await using var db = await LiveSessionTestDb.CreateAsync();
        var organizationId = db.SeedOrganization();
        var aliceId = db.SeedUser("Alice");
        var meetingId = db.SeedMeeting(organizationId, MeetingStatus.Completed);
        db.AddParticipant(meetingId, organizationId, aliceId, MeetingRole.Participant);
        db.DbContext.MeetingTranscripts.Add(new MeetingTranscript
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            MeetingId = meetingId,
            FullText = "[00:00:01 Alice] I will review the dataset by 2026-06-10T15:30:00+02:00.",
            GeneratedAtUtc = DateTime.UtcNow,
            SttModel = "test"
        });
        await db.DbContext.SaveChangesAsync();

        var llm = new FakeLlmService("""
            [{"task":"review the dataset","responsible_person":"Alice","deadline":"2026-06-10T15:30:00+02:00"}]
            """);
        var job = CreateJob(db, llm);

        await job.RunAsync(meetingId, organizationId, CancellationToken.None);

        llm.LastRequest.Should().NotBeNull();
        llm.LastRequest!.Model.Should().Be("openai-compatible-local");
        var message = llm.LastRequest!.Messages.Should().ContainSingle().Subject;
        message.Content.Should().Contain("You are an AI meeting assistant specialized in extracting action items from meeting transcripts.");
        message.Content.Should().Contain($"Transcript:{Environment.NewLine}[00:00:01 Alice] I will review the dataset by 2026-06-10T15:30:00+02:00.");
        message.Content.Should().NotContain("{transcript}");
        llm.LastRequest.ResponseFormat.Should().BeNull();

        var item = db.DbContext.ActionItems.Should().ContainSingle().Subject;
        item.Title.Should().Be("review the dataset");
        item.Description.Should().Contain("AI assignee: Alice");
        item.AssignedToUserId.Should().Be(aliceId);
        item.DueDateUtc.Should().Be(new DateTime(2026, 6, 10, 13, 30, 0, DateTimeKind.Utc));
        item.Status.Should().Be(ActionItemStatus.PendingReview);
        item.SyncMissingAssigneeReason.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_EnqueuesPersonalizedSummaryGenerationAfterActionExtractionCompletes()
    {
        await using var db = await LiveSessionTestDb.CreateAsync();
        var organizationId = db.SeedOrganization();
        var aliceId = db.SeedUser("Alice");
        var meetingId = db.SeedMeeting(organizationId, MeetingStatus.Completed);
        db.AddParticipant(meetingId, organizationId, aliceId, MeetingRole.Participant);
        db.DbContext.MeetingTranscripts.Add(new MeetingTranscript
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            MeetingId = meetingId,
            FullText = "[00:00:01 Alice] I will review the dataset.",
            GeneratedAtUtc = DateTime.UtcNow,
            SttModel = "test"
        });
        await db.DbContext.SaveChangesAsync();
        var jobs = new FakeBackgroundJobClient();
        var tracker = new PostMeetingProcessingTracker(db.DbContext);
        var job = CreateJob(
            db,
            new FakeLlmService("""
                [{"task":"review the dataset","responsible_person":"Alice","deadline":null}]
                """),
            tracker,
            jobs);

        await job.RunAsync(meetingId, organizationId, CancellationToken.None);

        jobs.CreatedJobs.Should().ContainSingle(x => x.Type == typeof(GeneratePersonalizedMeetingSummariesJob));
        var snapshot = await tracker.GetLatestByMeetingAsync(organizationId, meetingId);
        snapshot.Steps.Should().Contain(x =>
            x.StepType == PostMeetingProcessingStepType.PersonalizedSummaryGeneration
            && x.Status == PostMeetingProcessingStatus.Pending
            && x.RelatedHangfireJobId != null);
    }

    [Fact]
    public async Task RunAsync_WithNoAmbientTenant_ShouldNotCrashAndShouldEnqueuePersonalizedSummaryGeneration()
    {
        await using var db = await LiveSessionTestDb.CreateWithAmbientTenantAsync(ambientTenantOrganizationId: null);
        var organizationId = db.SeedOrganization();
        var aliceId = db.SeedUser("Alice");
        var meetingId = db.SeedMeeting(organizationId, MeetingStatus.Completed);
        db.AddParticipant(meetingId, organizationId, aliceId, MeetingRole.Participant);
        db.DbContext.MeetingTranscripts.Add(new MeetingTranscript
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            MeetingId = meetingId,
            FullText = "[00:00:01 Alice] I will review the dataset.",
            GeneratedAtUtc = DateTime.UtcNow,
            SttModel = "test"
        });
        await db.DbContext.SaveChangesAsync();
        var jobs = new FakeBackgroundJobClient();
        var tracker = new PostMeetingProcessingTracker(db.DbContext);
        var job = CreateJob(
            db,
            new FakeLlmService("""
                [{"task":"review the dataset","responsible_person":"Alice","deadline":null}]
                """),
            tracker,
            jobs);

        await job.Invoking(x => x.RunAsync(meetingId, organizationId, CancellationToken.None))
            .Should()
            .NotThrowAsync();

        var item = await db.DbContext.ActionItems
            .IgnoreQueryFilters()
            .SingleAsync(x => x.OrganizationId == organizationId && x.MeetingId == meetingId);
        item.Title.Should().Be("review the dataset");
        jobs.CreatedJobs.Should().ContainSingle(x => x.Type == typeof(GeneratePersonalizedMeetingSummariesJob));

        var snapshot = await tracker.GetLatestByMeetingAsync(organizationId, meetingId);
        snapshot.Steps.Should().Contain(x =>
            x.StepType == PostMeetingProcessingStepType.ActionExtraction
            && x.Status == PostMeetingProcessingStatus.Completed);
        snapshot.Steps.Should().Contain(x =>
            x.StepType == PostMeetingProcessingStepType.PersonalizedSummaryGeneration
            && x.Status == PostMeetingProcessingStatus.Pending
            && x.RelatedHangfireJobId != null);
    }

    [Fact]
    public async Task RunAsync_WithMismatchedAmbientTenant_ShouldUseExplicitOrganizationScope()
    {
        var organizationId = Guid.NewGuid();
        var unrelatedAmbientTenantId = Guid.NewGuid();
        await using var db = await LiveSessionTestDb.CreateWithAmbientTenantAsync(unrelatedAmbientTenantId, organizationId);
        db.SeedOrganization("target", organizationId);
        db.SeedOrganization("ambient", unrelatedAmbientTenantId);
        var aliceId = db.SeedUser("Alice");
        var meetingId = db.SeedMeeting(organizationId, MeetingStatus.Completed);
        db.AddParticipant(meetingId, organizationId, aliceId, MeetingRole.Participant);
        db.DbContext.MeetingTranscripts.Add(new MeetingTranscript
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            MeetingId = meetingId,
            FullText = "[00:00:01 Alice] We discussed the dataset.",
            GeneratedAtUtc = DateTime.UtcNow,
            SttModel = "test"
        });
        await db.DbContext.SaveChangesAsync();
        var llm = new FakeLlmService("[]");
        var jobs = new FakeBackgroundJobClient();
        var tracker = new PostMeetingProcessingTracker(db.DbContext);
        var job = CreateJob(db, llm, tracker, jobs);

        await job.RunAsync(meetingId, organizationId, CancellationToken.None);

        llm.LastRequest.Should().NotBeNull();
        jobs.CreatedJobs.Should().ContainSingle(x => x.Type == typeof(GeneratePersonalizedMeetingSummariesJob));
        var snapshot = await tracker.GetLatestByMeetingAsync(organizationId, meetingId);
        snapshot.Steps.Should().Contain(x =>
            x.StepType == PostMeetingProcessingStepType.ActionExtraction
            && x.Status == PostMeetingProcessingStatus.Completed
            && x.ErrorCode == null);
        snapshot.Steps.Should().Contain(x =>
            x.StepType == PostMeetingProcessingStepType.PersonalizedSummaryGeneration
            && x.Status == PostMeetingProcessingStatus.Pending);
    }

    [Fact]
    public async Task RunAsync_WhenLlmFails_ShouldMarkActionExtractionFailedAndEnqueuePersonalizedSummaryFallback()
    {
        await using var db = await LiveSessionTestDb.CreateAsync();
        var organizationId = db.SeedOrganization();
        var aliceId = db.SeedUser("Alice");
        var meetingId = db.SeedMeeting(organizationId, MeetingStatus.Completed);
        db.AddParticipant(meetingId, organizationId, aliceId, MeetingRole.Participant);
        db.DbContext.MeetingTranscripts.Add(new MeetingTranscript
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            MeetingId = meetingId,
            FullText = "[00:00:01 Alice] We discussed the dataset.",
            GeneratedAtUtc = DateTime.UtcNow,
            SttModel = "test"
        });
        await db.DbContext.SaveChangesAsync();
        var jobs = new FakeBackgroundJobClient();
        var tracker = new PostMeetingProcessingTracker(db.DbContext);
        var job = CreateJob(db, new ThrowingLlmService(new InvalidOperationException("LLM unavailable")), tracker, jobs);

        await job.Invoking(x => x.RunAsync(meetingId, organizationId, CancellationToken.None))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("LLM unavailable");

        jobs.CreatedJobs.Should().ContainSingle(x => x.Type == typeof(GeneratePersonalizedMeetingSummariesJob));
        var snapshot = await tracker.GetLatestByMeetingAsync(organizationId, meetingId);
        snapshot.Steps.Should().Contain(x =>
            x.StepType == PostMeetingProcessingStepType.ActionExtraction
            && x.Status == PostMeetingProcessingStatus.Failed
            && x.ErrorCode == "action_extraction_failed"
            && x.ErrorMessage == "LLM unavailable");
        snapshot.Steps.Should().Contain(x =>
            x.StepType == PostMeetingProcessingStepType.PersonalizedSummaryGeneration
            && x.Status == PostMeetingProcessingStatus.Pending
            && x.RelatedHangfireJobId != null);
    }

    [Fact]
    public async Task RunAsync_EnqueuesPersonalizedSummaryGenerationWhenExtractedTasksAreUnusable()
    {
        await using var db = await LiveSessionTestDb.CreateAsync();
        var organizationId = db.SeedOrganization();
        var aliceId = db.SeedUser("Alice");
        var meetingId = db.SeedMeeting(organizationId, MeetingStatus.Completed);
        db.AddParticipant(meetingId, organizationId, aliceId, MeetingRole.Participant);
        db.DbContext.MeetingTranscripts.Add(new MeetingTranscript
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            MeetingId = meetingId,
            FullText = "[00:00:01 Alice] We did not identify a concrete task.",
            GeneratedAtUtc = DateTime.UtcNow,
            SttModel = "test"
        });
        await db.DbContext.SaveChangesAsync();
        var jobs = new FakeBackgroundJobClient();
        var tracker = new PostMeetingProcessingTracker(db.DbContext);
        var job = CreateJob(
            db,
            new FakeLlmService("""
                [{"task":"   ","responsible_person":"Alice","deadline":null}]
                """),
            tracker,
            jobs);

        await job.RunAsync(meetingId, organizationId, CancellationToken.None);

        db.DbContext.ActionItems.Should().BeEmpty();
        jobs.CreatedJobs.Should().ContainSingle(x => x.Type == typeof(GeneratePersonalizedMeetingSummariesJob));
        var snapshot = await tracker.GetLatestByMeetingAsync(organizationId, meetingId);
        snapshot.Steps.Should().Contain(x =>
            x.StepType == PostMeetingProcessingStepType.PersonalizedSummaryGeneration
            && x.Status == PostMeetingProcessingStatus.Pending);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"tasks\":null}")]
    public async Task RunAsync_MalformedJsonFailsJobInsteadOfBeingTreatedAsNoTasks(string llmContent)
    {
        await using var db = await LiveSessionTestDb.CreateAsync();
        var organizationId = db.SeedOrganization();
        var userId = db.SeedUser("Alice");
        var meetingId = db.SeedMeeting(organizationId, MeetingStatus.Completed);
        db.AddParticipant(meetingId, organizationId, userId, MeetingRole.Participant);
        db.DbContext.MeetingTranscripts.Add(new MeetingTranscript
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            MeetingId = meetingId,
            FullText = "[00:00:01 Alice] We discussed the dataset.",
            GeneratedAtUtc = DateTime.UtcNow,
            SttModel = "test"
        });
        await db.DbContext.SaveChangesAsync();

        var job = CreateJob(db, new FakeLlmService(llmContent));

        await job.Invoking(x => x.RunAsync(meetingId, organizationId, CancellationToken.None))
            .Should()
            .ThrowAsync<InvalidOperationException>();

        db.DbContext.ActionItems.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_ValidEmptyTasksDoesNotFailOrPersistRows()
    {
        await using var db = await LiveSessionTestDb.CreateAsync();
        var organizationId = db.SeedOrganization();
        var userId = db.SeedUser("Alice");
        var meetingId = db.SeedMeeting(organizationId, MeetingStatus.Completed);
        db.AddParticipant(meetingId, organizationId, userId, MeetingRole.Participant);
        db.DbContext.MeetingTranscripts.Add(new MeetingTranscript
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            MeetingId = meetingId,
            FullText = "[00:00:01 Alice] We discussed the dataset.",
            GeneratedAtUtc = DateTime.UtcNow,
            SttModel = "test"
        });
        await db.DbContext.SaveChangesAsync();

        var job = CreateJob(db, new FakeLlmService("[]"));

        await job.Invoking(x => x.RunAsync(meetingId, organizationId, CancellationToken.None))
            .Should()
            .NotThrowAsync();

        db.DbContext.ActionItems.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_PersistsMissingAssigneeAsReviewRequiredAndSuppressesAutoSyncEligibility()
    {
        await using var db = await LiveSessionTestDb.CreateAsync();
        var organizationId = db.SeedOrganization();
        var userId = db.SeedUser("Alice");
        var meetingId = db.SeedMeeting(organizationId, MeetingStatus.Completed);
        db.AddParticipant(meetingId, organizationId, userId, MeetingRole.Participant);
        db.DbContext.MeetingTranscripts.Add(new MeetingTranscript
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            MeetingId = meetingId,
            FullText = "[00:00:01 Alice] Someone should prepare the release notes.",
            GeneratedAtUtc = DateTime.UtcNow,
            SttModel = "test"
        });
        await db.DbContext.SaveChangesAsync();

        var job = CreateJob(db, new FakeLlmService("""
            [{"task":"prepare the release notes","responsible_person":null,"deadline":null}]
            """));

        await job.RunAsync(meetingId, organizationId, CancellationToken.None);

        var item = db.DbContext.ActionItems.Should().ContainSingle().Subject;
        item.AssignedToParticipantId.Should().BeNull();
        item.AssignedToUserId.Should().BeNull();
        item.Status.Should().Be(ActionItemStatus.PendingReview);
        item.SyncMissingAssigneeReason.Should().Be(ActionItemReviewReasons.NeedsAssignee);
    }

    [Fact]
    public async Task RunAsync_InvalidDueDateWithKnownAssigneePersistsExplicitReviewBlocker()
    {
        await using var db = await LiveSessionTestDb.CreateAsync();
        var organizationId = db.SeedOrganization();
        var aliceId = db.SeedUser("Alice");
        var meetingId = db.SeedMeeting(organizationId, MeetingStatus.Completed);
        db.AddParticipant(meetingId, organizationId, aliceId, MeetingRole.Participant);
        db.DbContext.MeetingTranscripts.Add(new MeetingTranscript
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            MeetingId = meetingId,
            FullText = "[00:00:01 Alice] Alice will prepare the deployment plan by not-a-date.",
            GeneratedAtUtc = DateTime.UtcNow,
            SttModel = "test"
        });
        await db.DbContext.SaveChangesAsync();

        var job = CreateJob(db, new FakeLlmService("""
            [{"task":"prepare the deployment plan","responsible_person":"Alice","deadline":"not-a-date"}]
            """));

        await job.RunAsync(meetingId, organizationId, CancellationToken.None);

        var item = db.DbContext.ActionItems.Should().ContainSingle().Subject;
        item.AssignedToUserId.Should().Be(aliceId);
        item.DueDateUtc.Should().BeNull();
        item.SyncMissingAssigneeReason.Should().Be(ActionItemReviewReasons.InvalidDueDate);
        item.Description.Should().Contain("AI due date: not-a-date (could not parse to UTC)");
    }

    [Fact]
    public async Task RunAsync_UnknownAssigneeAndInvalidDueDateRemainReviewableWithRawLlmContext()
    {
        await using var db = await LiveSessionTestDb.CreateAsync();
        var organizationId = db.SeedOrganization();
        var aliceId = db.SeedUser("Alice");
        var meetingId = db.SeedMeeting(organizationId, MeetingStatus.Completed);
        db.AddParticipant(meetingId, organizationId, aliceId, MeetingRole.Participant);
        db.DbContext.MeetingTranscripts.Add(new MeetingTranscript
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            MeetingId = meetingId,
            FullText = "[00:00:01 Alice] Layla will prepare the deployment plan by not-a-date.",
            GeneratedAtUtc = DateTime.UtcNow,
            SttModel = "test"
        });
        await db.DbContext.SaveChangesAsync();

        var job = CreateJob(db, new FakeLlmService("""
            [{"task":"prepare the deployment plan","responsible_person":"Layla","deadline":"not-a-date"}]
            """));

        await job.RunAsync(meetingId, organizationId, CancellationToken.None);

        var item = db.DbContext.ActionItems.Should().ContainSingle().Subject;
        item.AssignedToUserId.Should().BeNull();
        item.DueDateUtc.Should().BeNull();
        item.SyncMissingAssigneeReason.Should().Be(
            $"{ActionItemReviewReasons.NeedsAssignee};{ActionItemReviewReasons.InvalidDueDate}");
        item.Description.Should().Contain("AI assignee: Layla");
        item.Description.Should().Contain("AI due date: not-a-date (could not parse to UTC)");
    }

    private static ExtractActionItemsJob CreateJob(
        LiveSessionTestDb db,
        ILLMService llmService,
        IPostMeetingProcessingTracker? tracker = null,
        FakeBackgroundJobClient? backgroundJobClient = null)
    {
        return new ExtractActionItemsJob(
            db.DbContext,
            llmService,
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
            hangfireJobContextAccessor: null,
            backgroundJobClient);
    }

    private sealed class FakeLlmService(string content) : ILLMService
    {
        public LLMRequest? LastRequest { get; private set; }

        public Task<LLMResponse> CompleteAsync(LLMRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new LLMResponse
            {
                Choices =
                [
                    new Choice
                    {
                        Message = new ChoiceMessage
                        {
                            Role = "assistant",
                            Content = content
                        }
                    }
                ]
            });
        }

        public Task<T> CompleteWithJsonAsync<T>(LLMRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException("ExtractActionItemsJob should parse the ai_work tasks schema itself.");
    }

    private sealed class ThrowingLlmService(Exception exception) : ILLMService
    {
        public Task<LLMResponse> CompleteAsync(LLMRequest request, CancellationToken cancellationToken)
            => Task.FromException<LLMResponse>(exception);

        public Task<T> CompleteWithJsonAsync<T>(LLMRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException("ExtractActionItemsJob should parse the ai_work tasks schema itself.");
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "MeetingAssistant.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
