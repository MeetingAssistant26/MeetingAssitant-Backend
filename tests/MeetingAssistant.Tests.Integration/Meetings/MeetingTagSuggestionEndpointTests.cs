using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MeetingAssistant.Features.Identity.Entites;
using MeetingAssistant.Features.LiveSession.Models.PostProcessing;
using MeetingAssistant.Features.Meetings.Contracts.Responses;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeetingAssistant.Tests.Integration.Meetings;

public sealed class MeetingTagSuggestionEndpointTests(MeetingAssistantWebFactory factory) : IntegrationTestBase(factory)
{
    [Fact]
    public async Task ListMeetingTagSuggestions_WhenParticipant_ShouldReturnReviewSuggestionsWithoutApplyingThem()
    {
        var meetingId = await SeedMeetingAsync(TestOrganizationId, TestUserId, MeetingRole.Participant);
        var roadmapTag = await SeedTagAsync(TestOrganizationId, "Roadmap", "#336699");
        var deploymentTag = await SeedTagAsync(TestOrganizationId, "Deployment", "#663399");
        await SeedSuggestionAsync(TestOrganizationId, meetingId, roadmapTag, MeetingTagSuggestionStatus.PendingReview);
        await SeedSuggestionAsync(TestOrganizationId, meetingId, deploymentTag, MeetingTagSuggestionStatus.Rejected);

        var response = await Client.GetAsync(SuggestionsUrl(TestOrganizationId, meetingId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<MeetingTagSuggestionListResponse>();
        body.Should().NotBeNull();
        body!.Items.Should().HaveCount(2);
        body.Items.Select(x => x.Status).Should().Contain(["PendingReview", "Rejected"]);
        body.Items.Should().OnlyContain(x => x.UsesOnlyConfirmedTagsForRag);
        body.ConfirmedTagIds.Should().BeEmpty("suggestions remain review-only until an apply endpoint confirms final tags");
    }

    [Fact]
    public async Task ApplyMeetingTagSuggestions_WhenHostSelectsFinalTags_ShouldApplyValidOrgTagsConfirmSelectedAndEnqueueReindex()
    {
        var meetingId = await SeedMeetingAsync(TestOrganizationId, TestUserId, MeetingRole.Host);
        var roadmapTag = await SeedTagAsync(TestOrganizationId, "Roadmap", "#336699");
        var deploymentTag = await SeedTagAsync(TestOrganizationId, "Deployment", "#663399");
        var customerTag = await SeedTagAsync(TestOrganizationId, "Customer", "#119944");
        var roadmapSuggestionId = await SeedSuggestionAsync(TestOrganizationId, meetingId, roadmapTag, MeetingTagSuggestionStatus.PendingReview);
        await SeedSuggestionAsync(TestOrganizationId, meetingId, deploymentTag, MeetingTagSuggestionStatus.PendingReview);

        var response = await Client.PostAsJsonAsync(
            $"{SuggestionsUrl(TestOrganizationId, meetingId)}/apply",
            new
            {
                tagIds = new[] { roadmapTag.Id, customerTag.Id },
                suggestionIds = new[] { roadmapSuggestionId },
                rejectUnselectedPending = true
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<MeetingTagSuggestionApplyResponse>();
        body.Should().NotBeNull();
        body!.ConfirmedTagIds.Should().BeEquivalentTo([roadmapTag.Id, customerTag.Id]);
        body.ReindexEnqueued.Should().BeTrue();
        body.ReindexJobId.Should().NotBeNullOrWhiteSpace();
        body.Suggestions.Should().ContainSingle(x => x.Id == roadmapSuggestionId && x.Status == "Confirmed");
        body.Suggestions.Should().ContainSingle(x => x.MeetingTagId == deploymentTag.Id && x.Status == "Rejected");

        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var confirmedMeetingTags = await db.MeetingMeetingTags
            .IgnoreQueryFilters()
            .Where(x => x.MeetingId == meetingId)
            .Select(x => x.MeetingTagId)
            .ToListAsync();
        confirmedMeetingTags.Should().BeEquivalentTo([roadmapTag.Id, customerTag.Id]);

        var storedSuggestions = await db.MeetingTagSuggestions
            .IgnoreQueryFilters()
            .Where(x => x.MeetingId == meetingId)
            .ToDictionaryAsync(x => x.Id);
        storedSuggestions[roadmapSuggestionId].Status.Should().Be(MeetingTagSuggestionStatus.Confirmed);
        JsonDocument.Parse(storedSuggestions[roadmapSuggestionId].MetadataJson)
            .RootElement.GetProperty("review").GetProperty("reviewedByUserId").GetString()
            .Should().Be(TestUserId.ToString());
        storedSuggestions[roadmapSuggestionId].MetadataJson.Should().Contain(body.ReindexJobId!);

        var pendingStep = await db.PostMeetingProcessingSteps
            .IgnoreQueryFilters()
            .SingleAsync(x => x.MeetingId == meetingId && x.StepType == PostMeetingProcessingStepType.KnowledgeIndexing);
        pendingStep.Status.Should().Be(PostMeetingProcessingStatus.Pending);
        pendingStep.RelatedHangfireJobId.Should().Be(body.ReindexJobId);
    }

    [Fact]
    public async Task ApplyMeetingTagSuggestions_WithForeignTag_ShouldRejectWithoutChangingConfirmedTagsOrSuggestions()
    {
        var meetingId = await SeedMeetingAsync(TestOrganizationId, TestUserId, MeetingRole.Host);
        var roadmapTag = await SeedTagAsync(TestOrganizationId, "Roadmap", "#336699");
        var suggestionId = await SeedSuggestionAsync(TestOrganizationId, meetingId, roadmapTag, MeetingTagSuggestionStatus.PendingReview);
        var otherOrgId = Guid.NewGuid();
        await SeedOrganizationAsync(otherOrgId, "other-org");
        var foreignTag = await SeedTagAsync(otherOrgId, "Foreign", "#000000");

        var response = await Client.PostAsJsonAsync(
            $"{SuggestionsUrl(TestOrganizationId, meetingId)}/apply",
            new { tagIds = new[] { foreignTag.Id }, suggestionIds = Array.Empty<Guid>() });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.MeetingMeetingTags.IgnoreQueryFilters().CountAsync(x => x.MeetingId == meetingId)).Should().Be(0);
        var suggestion = await db.MeetingTagSuggestions.IgnoreQueryFilters().SingleAsync(x => x.Id == suggestionId);
        suggestion.Status.Should().Be(MeetingTagSuggestionStatus.PendingReview);
    }

    [Fact]
    public async Task UpdateAndRejectMeetingTagSuggestions_ShouldRequireReviewerAndKeepUnconfirmedTagsOutOfMeetingTags()
    {
        var meetingId = await SeedMeetingAsync(TestOrganizationId, TestUserId, MeetingRole.Host);
        var reviewerId = await SeedMemberAsync(TestOrganizationId, OrganizationRole.Member, MeetingRole.Participant, meetingId);
        var roadmapTag = await SeedTagAsync(TestOrganizationId, "Roadmap", "#336699");
        var deploymentTag = await SeedTagAsync(TestOrganizationId, "Deployment", "#663399");
        var suggestionId = await SeedSuggestionAsync(TestOrganizationId, meetingId, roadmapTag, MeetingTagSuggestionStatus.PendingReview);

        using var participantClient = AuthenticatedClient(reviewerId, TestOrganizationId, "Member");
        var forbidden = await participantClient.PatchAsJsonAsync(
            $"{SuggestionsUrl(TestOrganizationId, meetingId)}/{suggestionId}",
            new { meetingTagId = deploymentTag.Id, confidence = 0.25m, reason = "Reviewer retag" });
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var updated = await Client.PatchAsJsonAsync(
            $"{SuggestionsUrl(TestOrganizationId, meetingId)}/{suggestionId}",
            new { meetingTagId = deploymentTag.Id, confidence = 1.2m, reason = "Reviewer retag" });
        updated.StatusCode.Should().Be(HttpStatusCode.OK);
        var updatedBody = await updated.Content.ReadFromJsonAsync<MeetingTagSuggestionResponse>();
        updatedBody.Should().NotBeNull();
        updatedBody!.MeetingTagId.Should().Be(deploymentTag.Id);
        updatedBody.Confidence.Should().Be(1m);
        updatedBody.Status.Should().Be("PendingReview");

        var rejected = await Client.PostAsJsonAsync(
            $"{SuggestionsUrl(TestOrganizationId, meetingId)}/{suggestionId}/reject",
            new { reason = "Not relevant" });
        rejected.StatusCode.Should().Be(HttpStatusCode.OK);
        var rejectedBody = await rejected.Content.ReadFromJsonAsync<MeetingTagSuggestionResponse>();
        rejectedBody.Should().NotBeNull();
        rejectedBody!.Status.Should().Be("Rejected");
        rejectedBody.KnowledgeRefreshPending.Should().BeFalse();

        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.MeetingMeetingTags.IgnoreQueryFilters().CountAsync(x => x.MeetingId == meetingId))
            .Should().Be(0, "updating or rejecting a suggestion must not publish it as confirmed RAG tag metadata");
    }

    [Fact]
    public async Task RefreshMeetingTagMetadata_WhenAdmin_ShouldEnqueueIdempotentReindex()
    {
        var meetingId = await SeedMeetingAsync(TestOrganizationId, TestUserId, MeetingRole.Host);

        var response = await Client.PostAsync($"{SuggestionsUrl(TestOrganizationId, meetingId)}/reindex", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = await response.Content.ReadFromJsonAsync<MeetingTagMetadataRefreshResponse>();
        body.Should().NotBeNull();
        body!.ReindexEnqueued.Should().BeTrue();
        body.ReindexJobId.Should().NotBeNullOrWhiteSpace();
    }

    private static string SuggestionsUrl(Guid organizationId, Guid meetingId)
        => $"/api/organizations/{organizationId}/meetings/{meetingId}/tag-suggestions";

    private async Task<Guid> SeedMeetingAsync(Guid organizationId, Guid userId, MeetingRole role)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var start = DateTime.UtcNow.AddHours(-2);
        var meeting = new Meeting
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            Title = "Post meeting tag review",
            ScheduledStartUtc = start,
            ScheduledEndUtc = start.AddHours(1),
            Status = MeetingStatus.Completed
        };
        db.Meetings.Add(meeting);
        db.MeetingParticipants.Add(new MeetingParticipant
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            MeetingId = meeting.Id,
            UserId = userId,
            MeetingRole = role
        });
        await db.SaveChangesAsync();
        return meeting.Id;
    }

    private async Task<MeetingTag> SeedTagAsync(Guid organizationId, string name, string color)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var tag = new MeetingTag
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            Name = name,
            Color = color,
            IsActive = true
        };
        db.MeetingTags.Add(tag);
        await db.SaveChangesAsync();
        return tag;
    }

    private async Task<Guid> SeedSuggestionAsync(
        Guid organizationId,
        Guid meetingId,
        MeetingTag tag,
        MeetingTagSuggestionStatus status)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var suggestion = new MeetingTagSuggestion
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            MeetingId = meetingId,
            MeetingTagId = tag.Id,
            TagNameSnapshot = tag.Name,
            TagColorSnapshot = tag.Color,
            Confidence = 0.8m,
            Reason = $"Suggested {tag.Name}",
            Status = status,
            LlmModel = "test-llm",
            SuggestedAtUtc = DateTime.UtcNow,
            MetadataJson = "{\"knowledgeRefreshPending\":true,\"usesOnlyConfirmedTagsForRag\":true}"
        };
        db.MeetingTagSuggestions.Add(suggestion);
        await db.SaveChangesAsync();
        return suggestion.Id;
    }

    private async Task<Guid> SeedMemberAsync(
        Guid organizationId,
        OrganizationRole organizationRole,
        MeetingRole meetingRole,
        Guid meetingId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var userId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser
        {
            Id = userId,
            UserName = $"member-{userId:N}@test.com",
            NormalizedUserName = $"MEMBER-{userId:N}@TEST.COM",
            Email = $"member-{userId:N}@test.com",
            NormalizedEmail = $"MEMBER-{userId:N}@TEST.COM",
            EmailConfirmed = true,
            DisplayName = "Review Member",
            SecurityStamp = Guid.NewGuid().ToString()
        });
        db.UserOrgMemberships.Add(new UserOrgMembership
        {
            UserId = userId,
            OrganizationId = organizationId,
            OrgRole = organizationRole,
            IsEnabled = true
        });
        db.MeetingParticipants.Add(new MeetingParticipant
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            MeetingId = meetingId,
            UserId = userId,
            MeetingRole = meetingRole
        });
        await db.SaveChangesAsync();
        return userId;
    }

    private async Task SeedOrganizationAsync(Guid organizationId, string slug)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Organizations.Add(new Organization
        {
            Id = organizationId,
            Name = slug,
            Slug = slug
        });
        await db.SaveChangesAsync();
    }

    private HttpClient AuthenticatedClient(Guid userId, Guid organizationId, string organizationRole)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            TestJwtTokenHelper.GenerateToken(userId, organizationId, organizationRole));
        return client;
    }
}
