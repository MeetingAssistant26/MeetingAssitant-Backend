using FluentAssertions;
using MeetingAssistant.Features.ActionItems.Jobs;
using MeetingAssistant.Features.ActionItems.Models;
using MeetingAssistant.Features.ActionItems.Models.Enums;
using MeetingAssistant.Features.LiveSession.Infrastructure;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.LiveSession.Services;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Infrastructure.AI;
using MeetingAssistant.Infrastructure.AI.DTOs;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
            {"tasks":[{"assignee":"Alice","task":"review the dataset","due_date":"2026-06-10T15:30:00+02:00","status":"pending"}]}
            """);
        var job = CreateJob(db, llm);

        await job.RunAsync(meetingId, organizationId, CancellationToken.None);

        llm.LastRequest.Should().NotBeNull();
        llm.LastRequest!.Model.Should().Be("openai-compatible-local");
        var message = llm.LastRequest!.Messages.Should().ContainSingle().Subject;
        message.Content.Should().Contain("You are an AI meeting assistant specialized in extracting action items.");
        message.Content.Should().Contain($"Transcript:{Environment.NewLine}[00:00:01 Alice] I will review the dataset by 2026-06-10T15:30:00+02:00.");
        message.Content.Should().NotContain("{transcript}");
        llm.LastRequest.ResponseFormat?.Type.Should().Be("json_object");

        var item = db.DbContext.ActionItems.Should().ContainSingle().Subject;
        item.Title.Should().Be("review the dataset");
        item.Description.Should().Contain("AI assignee: Alice");
        item.AssignedToUserId.Should().Be(aliceId);
        item.DueDateUtc.Should().Be(new DateTime(2026, 6, 10, 13, 30, 0, DateTimeKind.Utc));
        item.Status.Should().Be(ActionItemStatus.PendingReview);
        item.SyncMissingAssigneeReason.Should().BeNull();
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

        var job = CreateJob(db, new FakeLlmService("{\"tasks\":[]}"));

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
            {"tasks":[{"assignee":null,"task":"prepare the release notes","due_date":null,"status":"pending"}]}
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
            {"tasks":[{"assignee":"Alice","task":"prepare the deployment plan","due_date":"not-a-date","status":"pending"}]}
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
            {"tasks":[{"assignee":"Layla","task":"prepare the deployment plan","due_date":"not-a-date","status":"pending"}]}
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

    private static ExtractActionItemsJob CreateJob(LiveSessionTestDb db, ILLMService llmService)
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
            NullLogger<ExtractActionItemsJob>.Instance);
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

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "MeetingAssistant.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
