using System.Text.Json;
using FluentAssertions;
using MeetingAssistant.Features.LiveSession.Infrastructure;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.LiveSession.Models.PostProcessing;
using MeetingAssistant.Features.LiveSession.Services.PostProcessing;
using MeetingAssistant.Features.Meetings.Jobs;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Features.Meetings.Services.TagSuggestions;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Infrastructure.AI;
using MeetingAssistant.Infrastructure.AI.DTOs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using tests.Integration.LiveSession;
using Xunit;

namespace MeetingAssistant.Tests.Integration.Meetings;

public sealed class MeetingTagSuggestionJobTests
{
    [Fact]
    public async Task RunAsync_SendsTranscriptSummaryAndOrgTagsToLlmAndPersistsPendingSuggestionsOnly()
    {
        await using var db = await LiveSessionTestDb.CreateAsync();
        var organizationId = db.SeedOrganization();
        var meetingId = SeedCompletedMeetingArtifacts(db, organizationId);
        var roadmapTag = SeedTag(db, organizationId, "Roadmap", "#336699");
        var deploymentTag = SeedTag(db, organizationId, "Deployment", "#663399");
        await db.DbContext.SaveChangesAsync();

        var llm = new FakeLlmService($$"""
            {"suggested_tags":[
              {"id":"{{roadmapTag.Id}}","name":"Roadmap","confidence":0.92,"reason":"Roadmap milestones were discussed."},
              {"id":"{{deploymentTag.Id}}","name":"Deployment","confidence":0.65,"reason":"Deployment readiness came up."}
            ]}
            """);
        var tracker = new PostMeetingProcessingTracker(db.DbContext);
        var job = CreateJob(db, llm, tracker);

        await job.RunAsync(meetingId, organizationId, CancellationToken.None);

        llm.CallCount.Should().Be(1);
        llm.LastRequest.Should().NotBeNull();
        llm.LastRequest!.Model.Should().Be("openai-compatible-local");
        llm.LastRequest.ResponseFormat?.Type.Should().Be("json_object");
        var prompt = llm.LastRequest.Messages.Should().ContainSingle().Subject.Content;
        prompt.Should().Contain("Only suggest tags from the provided organization tag list");
        prompt.Should().Contain(roadmapTag.Id.ToString());
        prompt.Should().Contain(deploymentTag.Id.ToString());
        prompt.Should().Contain("Generated summary about roadmap and deployment readiness.");
        prompt.Should().Contain("[00:00:01 Alice] We discussed roadmap milestones and deployment readiness.");

        var suggestions = await db.DbContext.MeetingTagSuggestions
            .IgnoreQueryFilters()
            .Where(x => x.MeetingId == meetingId)
            .OrderBy(x => x.TagNameSnapshot)
            .ToListAsync();
        suggestions.Should().HaveCount(2);
        suggestions.Should().OnlyContain(x => x.OrganizationId == organizationId);
        suggestions.Should().OnlyContain(x => x.Status == MeetingTagSuggestionStatus.PendingReview);
        suggestions.Select(x => x.MeetingTagId).Should().BeEquivalentTo([roadmapTag.Id, deploymentTag.Id]);
        suggestions.Select(x => x.TagNameSnapshot).Should().Equal("Deployment", "Roadmap");
        suggestions.Should().OnlyContain(x => x.LlmModel == "local-tag-model");
        suggestions.Should().OnlyContain(x => x.TranscriptId.HasValue && x.SummaryId.HasValue);

        var metadata = JsonDocument.Parse(suggestions[0].MetadataJson).RootElement;
        metadata.GetProperty("knowledgeRefreshPending").GetBoolean().Should().BeTrue();
        metadata.GetProperty("usesOnlyConfirmedTagsForRag").GetBoolean().Should().BeTrue();

        db.DbContext.MeetingMeetingTags.IgnoreQueryFilters().Where(x => x.MeetingId == meetingId)
            .Should()
            .BeEmpty("AI suggestions must stay separate from confirmed meeting tags until user confirmation");

        var step = await db.DbContext.PostMeetingProcessingSteps
            .IgnoreQueryFilters()
            .SingleAsync(x => x.MeetingId == meetingId && x.StepType == PostMeetingProcessingStepType.TagSuggestion);
        step.Status.Should().Be(PostMeetingProcessingStatus.Completed);
        step.ArtifactType.Should().Be("meeting_tag_suggestion");
        step.ArtifactIdsJson.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task SuggestTagsAsync_IgnoresUnknownForeignAndIdNameMismatchedTags()
    {
        await using var db = await LiveSessionTestDb.CreateAsync();
        var organizationId = db.SeedOrganization();
        var meetingId = SeedCompletedMeetingArtifacts(db, organizationId);
        var roadmapTag = SeedTag(db, organizationId, "Roadmap", "#336699");
        var otherOrgId = Guid.NewGuid();
        db.SeedOrganization("other-org", otherOrgId);
        var foreignRoadmap = SeedTag(db, otherOrgId, "Roadmap", "#000000");
        await db.DbContext.SaveChangesAsync();

        var llm = new FakeLlmService($$"""
            {"suggested_tags":[
              {"id":"{{roadmapTag.Id}}","name":"Roadmap","confidence":0.88,"reason":"valid"},
              {"id":"{{foreignRoadmap.Id}}","name":"Roadmap","confidence":0.99,"reason":"foreign org id"},
              {"id":"{{roadmapTag.Id}}","name":"Deployment","confidence":0.99,"reason":"mismatched name"},
              {"id":"{{Guid.NewGuid()}}","name":"Unknown","confidence":0.5,"reason":"invented"}
            ]}
            """);
        var service = CreateService(db, llm);

        var result = await service.SuggestTagsAsync(organizationId, meetingId, CancellationToken.None);

        result.PersistedSuggestionCount.Should().Be(1);
        result.IgnoredSuggestionCount.Should().Be(3);
        var suggestion = db.DbContext.MeetingTagSuggestions.Should().ContainSingle().Subject;
        suggestion.MeetingTagId.Should().Be(roadmapTag.Id);
        suggestion.OrganizationId.Should().Be(organizationId);
        suggestion.MetadataJson.Should().Contain("unknown_tag");
        suggestion.MetadataJson.Should().Contain("id_name_mismatch");
    }

    [Fact]
    public async Task SuggestTagsAsync_DeduplicatesSuggestionsForSameOrgTag()
    {
        await using var db = await LiveSessionTestDb.CreateAsync();
        var organizationId = db.SeedOrganization();
        var meetingId = SeedCompletedMeetingArtifacts(db, organizationId);
        var roadmapTag = SeedTag(db, organizationId, "Roadmap", "#336699");
        await db.DbContext.SaveChangesAsync();

        var llm = new FakeLlmService($$"""
            {"suggested_tags":[
              {"id":"{{roadmapTag.Id}}","name":"Roadmap","confidence":0.91,"reason":"first"},
              {"name":"Roadmap","confidence":0.75,"reason":"duplicate by name"}
            ]}
            """);
        var service = CreateService(db, llm);

        var result = await service.SuggestTagsAsync(organizationId, meetingId, CancellationToken.None);

        result.PersistedSuggestionCount.Should().Be(1);
        result.IgnoredSuggestionCount.Should().Be(1);
        db.DbContext.MeetingTagSuggestions.Should().ContainSingle(x => x.MeetingTagId == roadmapTag.Id);
        db.DbContext.MeetingTagSuggestions.Single().MetadataJson.Should().Contain("duplicate_tag");
    }

    [Fact]
    public async Task SuggestTagsAsync_WithNoOrgTagsSkipsLlmAndSupersedesPendingSuggestions()
    {
        await using var db = await LiveSessionTestDb.CreateAsync();
        var organizationId = db.SeedOrganization();
        var meetingId = SeedCompletedMeetingArtifacts(db, organizationId);
        var oldTag = SeedTag(db, organizationId, "Old", "#999999", isActive: false);
        db.DbContext.MeetingTagSuggestions.Add(new MeetingTagSuggestion
        {
            OrganizationId = organizationId,
            MeetingId = meetingId,
            MeetingTagId = oldTag.Id,
            TagNameSnapshot = oldTag.Name,
            Status = MeetingTagSuggestionStatus.PendingReview,
            LlmModel = "old",
            SuggestedAtUtc = DateTime.UtcNow.AddHours(-1)
        });
        await db.DbContext.SaveChangesAsync();

        var llm = new FakeLlmService("SHOULD_NOT_BE_USED");
        var service = CreateService(db, llm);

        var result = await service.SuggestTagsAsync(organizationId, meetingId, CancellationToken.None);

        result.SkippedBecauseNoOrgTags.Should().BeTrue();
        result.PersistedSuggestionCount.Should().Be(0);
        llm.CallCount.Should().Be(0);
        db.DbContext.MeetingTagSuggestions.Single().Status.Should().Be(MeetingTagSuggestionStatus.Superseded);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"suggested_tags\":null}")]
    public async Task RunAsync_MalformedResponseFailsPipelineStep(string llmContent)
    {
        await using var db = await LiveSessionTestDb.CreateAsync();
        var organizationId = db.SeedOrganization();
        var meetingId = SeedCompletedMeetingArtifacts(db, organizationId);
        SeedTag(db, organizationId, "Roadmap", "#336699");
        await db.DbContext.SaveChangesAsync();

        var tracker = new PostMeetingProcessingTracker(db.DbContext);
        var job = CreateJob(db, new FakeLlmService(llmContent), tracker);

        await job.Invoking(x => x.RunAsync(meetingId, organizationId, CancellationToken.None))
            .Should()
            .ThrowAsync<InvalidOperationException>();

        db.DbContext.MeetingTagSuggestions.Should().BeEmpty();
        var step = await db.DbContext.PostMeetingProcessingSteps
            .IgnoreQueryFilters()
            .SingleAsync(x => x.MeetingId == meetingId && x.StepType == PostMeetingProcessingStepType.TagSuggestion);
        step.Status.Should().Be(PostMeetingProcessingStatus.Failed);
        step.ErrorCode.Should().Be("tag_suggestion_failed");
    }

    [Fact]
    public async Task SuggestTagsAsync_UsesOnlyCurrentOrganizationTagsForNameOnlySuggestions()
    {
        await using var db = await LiveSessionTestDb.CreateAsync();
        var organizationId = db.SeedOrganization();
        var meetingId = SeedCompletedMeetingArtifacts(db, organizationId);
        var currentOrgTag = SeedTag(db, organizationId, "Roadmap", "#336699");
        var otherOrgId = Guid.NewGuid();
        db.SeedOrganization("other-org", otherOrgId);
        SeedTag(db, otherOrgId, "Deployment", "#000000");
        await db.DbContext.SaveChangesAsync();

        var service = CreateService(db, new FakeLlmService("""
            {"suggested_tags":[
              {"name":"Roadmap","confidence":0.8,"reason":"current org name"},
              {"name":"Deployment","confidence":0.8,"reason":"foreign org only"}
            ]}
            """));

        var result = await service.SuggestTagsAsync(organizationId, meetingId, CancellationToken.None);

        result.PersistedSuggestionCount.Should().Be(1);
        result.IgnoredSuggestionCount.Should().Be(1);
        db.DbContext.MeetingTagSuggestions.Should().ContainSingle(x => x.MeetingTagId == currentOrgTag.Id);
    }

    private static IMeetingTagSuggestionService CreateService(
        LiveSessionTestDb db,
        ILLMService llm,
        IPostMeetingProcessingTracker? tracker = null)
    {
        return new MeetingTagSuggestionService(
            db.DbContext,
            llm,
            Options.Create(new OpenAiCompatibleOptions
            {
                Llm = new OpenAiCompatibleOptions.ProviderConfig
                {
                    BaseUrl = "http://llm.test/v1",
                    ApiKey = "test-key",
                    Model = "openai-compatible-local"
                }
            }),
            NullLogger<MeetingTagSuggestionService>.Instance,
            tracker);
    }

    private static SuggestMeetingTagsJob CreateJob(
        LiveSessionTestDb db,
        ILLMService llm,
        IPostMeetingProcessingTracker tracker)
    {
        return new SuggestMeetingTagsJob(
            CreateService(db, llm, tracker),
            NullLogger<SuggestMeetingTagsJob>.Instance,
            tracker);
    }

    private static Guid SeedCompletedMeetingArtifacts(LiveSessionTestDb db, Guid organizationId)
    {
        var meetingId = db.SeedMeeting(organizationId, MeetingStatus.Completed);
        db.DbContext.MeetingTranscripts.Add(new MeetingTranscript
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            MeetingId = meetingId,
            FullText = "[00:00:01 Alice] We discussed roadmap milestones and deployment readiness.",
            SegmentsJson = "[]",
            SttModel = "test-stt",
            GeneratedAtUtc = DateTime.UtcNow.AddMinutes(-5)
        });
        db.DbContext.MeetingSummaries.Add(new MeetingSummary
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            MeetingId = meetingId,
            SummaryText = "Generated summary about roadmap and deployment readiness.",
            LlmModel = "summary-model",
            GeneratedAtUtc = DateTime.UtcNow.AddMinutes(-1)
        });
        db.DbContext.SaveChanges();
        return meetingId;
    }

    private static MeetingTag SeedTag(
        LiveSessionTestDb db,
        Guid organizationId,
        string name,
        string color,
        bool isActive = true)
    {
        var tag = new MeetingTag
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            Name = name,
            Color = color,
            IsActive = isActive
        };
        db.DbContext.MeetingTags.Add(tag);
        return tag;
    }

    private sealed class FakeLlmService(string content) : ILLMService
    {
        public LLMRequest? LastRequest { get; private set; }
        public int CallCount { get; private set; }

        public Task<LLMResponse> CompleteAsync(LLMRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            CallCount++;
            return Task.FromResult(new LLMResponse
            {
                Model = "local-tag-model",
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
            => throw new NotSupportedException();
    }
}
