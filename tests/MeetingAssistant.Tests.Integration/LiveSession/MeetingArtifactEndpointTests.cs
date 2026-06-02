using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MeetingAssistant.Features.Identity.Entites;
using MeetingAssistant.Features.LiveSession.Contracts.Responses;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Tests.Integration.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeetingAssistant.Tests.Integration.LiveSession;

public class MeetingArtifactEndpointTests : IntegrationTestBase
{
    public MeetingArtifactEndpointTests(MeetingAssistantWebFactory factory) : base(factory)
    {
    }

    private HttpClient CreateAuthenticatedClient(Guid userId, Guid orgId)
    {
        var client = Factory.CreateClient();
        var token = TestJwtTokenHelper.GenerateToken(userId, orgId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<Guid> SeedMeetingAsync(MeetingStatus status = MeetingStatus.Completed, Guid? orgId = null)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var meeting = new Meeting
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId ?? TestOrganizationId,
            Title = "Artifact Test Meeting",
            ScheduledStartUtc = DateTime.UtcNow.AddHours(-2),
            ScheduledEndUtc = DateTime.UtcNow.AddHours(-1),
            Status = status
        };
        db.Meetings.Add(meeting);
        await db.SaveChangesAsync();
        return meeting.Id;
    }

    [Fact]
    public async Task GetTranscript_WithPersistedArtifact_ShouldReturnAvailableTranscript()
    {
        var meetingId = await SeedMeetingAsync();
        var generatedAt = DateTime.UtcNow.AddMinutes(-5);
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.MeetingTranscripts.Add(new MeetingTranscript
            {
                MeetingId = meetingId,
                OrganizationId = TestOrganizationId,
                FullText = "[00:00:01 Test User] hello transcript",
                SegmentsJson = JsonSerializer.Serialize(new[]
                {
                    new MeetingTranscriptSegmentResponse(TestUserId, 1_000, 2_000, "hello transcript", 0.98)
                }),
                SttModel = "whisper-test",
                GeneratedAtUtc = generatedAt
            });
            await db.SaveChangesAsync();
        }

        var response = await Client.GetAsync($"/api/organizations/{TestOrganizationId}/meetings/{meetingId}/transcript");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<MeetingTranscriptResponse>();
        body.Should().NotBeNull();
        body!.Status.Should().Be("available");
        body.FullText.Should().Contain("hello transcript");
        body.SttModel.Should().Be("whisper-test");
        body.GeneratedAtUtc.Should().NotBeNull();
        body.Segments.Should().ContainSingle(s => s.ParticipantUserId == TestUserId && s.StartMs == 1_000 && s.EndMs == 2_000);
    }

    [Fact]
    public async Task GetSummary_WithPersistedArtifact_ShouldReturnAvailableSummary()
    {
        var meetingId = await SeedMeetingAsync();
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.MeetingSummaries.Add(new MeetingSummary
            {
                MeetingId = meetingId,
                OrganizationId = TestOrganizationId,
                SummaryText = "Summary: decisions and action items.",
                LlmModel = "gpt-test",
                PromptTokens = 11,
                CompletionTokens = 22,
                GeneratedAtUtc = DateTime.UtcNow.AddMinutes(-2)
            });
            await db.SaveChangesAsync();
        }

        var response = await Client.GetAsync($"/api/organizations/{TestOrganizationId}/meetings/{meetingId}/summary");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<MeetingSummaryResponse>();
        body.Should().NotBeNull();
        body!.Status.Should().Be("available");
        body.SummaryText.Should().Contain("decisions");
        body.LlmModel.Should().Be("gpt-test");
        body.PromptTokens.Should().Be(11);
        body.CompletionTokens.Should().Be(22);
    }

    [Fact]
    public async Task GetPersonalizedSummary_WithPersistedArtifacts_ShouldReturnOnlyCurrentUsersSummary()
    {
        var meetingId = await SeedMeetingAsync();
        var otherUserId = Guid.NewGuid();
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var currentParticipant = new MeetingParticipant
            {
                OrganizationId = TestOrganizationId,
                MeetingId = meetingId,
                UserId = TestUserId
            };
            var otherUser = new ApplicationUser
            {
                Id = otherUserId,
                Email = $"other-{otherUserId:N}@test.com",
                UserName = $"other-{otherUserId:N}@test.com",
                DisplayName = "Other User",
                EmailConfirmed = true,
                SecurityStamp = Guid.NewGuid().ToString()
            };
            var otherParticipant = new MeetingParticipant
            {
                OrganizationId = TestOrganizationId,
                MeetingId = meetingId,
                UserId = otherUserId
            };
            db.Users.Add(otherUser);
            db.MeetingParticipants.AddRange(currentParticipant, otherParticipant);
            await db.SaveChangesAsync();
            db.PersonalizedMeetingSummaries.AddRange(
                new PersonalizedMeetingSummary
                {
                    MeetingId = meetingId,
                    OrganizationId = TestOrganizationId,
                    MeetingParticipantId = currentParticipant.Id,
                    UserId = TestUserId,
                    SummaryText = "Current user's personalized summary.",
                    LlmModel = "gpt-personalized",
                    PromptTokens = 31,
                    CompletionTokens = 9,
                    GeneratedAtUtc = DateTime.UtcNow.AddMinutes(-2),
                    TargetDisplayName = "Test User",
                    PromptName = "PersonalizedMeetingSummarizer",
                    PromptVersion = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    PersonalizationContextJson = "{\"jobRole\":\"Backend Lead\"}"
                },
                new PersonalizedMeetingSummary
                {
                    MeetingId = meetingId,
                    OrganizationId = TestOrganizationId,
                    MeetingParticipantId = otherParticipant.Id,
                    UserId = otherUserId,
                    SummaryText = "Other user's personalized summary.",
                    LlmModel = "gpt-personalized",
                    GeneratedAtUtc = DateTime.UtcNow.AddMinutes(-2)
                });
            await db.SaveChangesAsync();
        }

        var response = await Client.GetAsync($"/api/organizations/{TestOrganizationId}/meetings/{meetingId}/summary/personalized");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PersonalizedMeetingSummaryResponse>();
        body.Should().NotBeNull();
        body!.Status.Should().Be("available");
        body.UserId.Should().Be(TestUserId);
        body.SummaryText.Should().Be("Current user's personalized summary.");
        body.SummaryText.Should().NotContain("Other user");
        body.LlmModel.Should().Be("gpt-personalized");
        body.PromptTokens.Should().Be(31);
        body.CompletionTokens.Should().Be(9);
        body.TargetDisplayName.Should().Be("Test User");
        body.PromptName.Should().Be("PersonalizedMeetingSummarizer");
        body.PromptVersion.Should().MatchRegex("^sha256:[0-9a-f]{64}$");
    }

    [Fact]
    public async Task GetPersonalizedSummary_WithSkippedArtifact_ShouldReturnNotRelevantState()
    {
        var meetingId = await SeedMeetingAsync(MeetingStatus.Completed);
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var participant = new MeetingParticipant
            {
                OrganizationId = TestOrganizationId,
                MeetingId = meetingId,
                UserId = TestUserId
            };
            db.MeetingParticipants.Add(participant);
            await db.SaveChangesAsync();
            db.PersonalizedMeetingSummaries.Add(new PersonalizedMeetingSummary
            {
                MeetingId = meetingId,
                OrganizationId = TestOrganizationId,
                MeetingParticipantId = participant.Id,
                UserId = TestUserId,
                Status = PersonalizedMeetingSummaryStatus.Skipped,
                TargetDisplayName = "Test User",
                EligibilityReason = "no_personalization_signal_or_transcript_relevance",
                EligibilityContextJson = "{\"decision\":\"skip\"}"
            });
            await db.SaveChangesAsync();
        }

        var response = await Client.GetAsync($"/api/organizations/{TestOrganizationId}/meetings/{meetingId}/summary/personalized");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PersonalizedMeetingSummaryResponse>();
        body.Should().NotBeNull();
        body!.Status.Should().Be("not_relevant");
        body.SummaryText.Should().BeNull();
        body.GeneratedAtUtc.Should().BeNull();
        body.TargetDisplayName.Should().Be("Test User");
    }

    [Fact]
    public async Task GetPersonalizedSummary_WithoutArtifact_ForInProgressParticipantMeeting_ShouldReturnProcessingState()
    {
        var meetingId = await SeedMeetingAsync(MeetingStatus.InProgress);
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.MeetingParticipants.Add(new MeetingParticipant
            {
                OrganizationId = TestOrganizationId,
                MeetingId = meetingId,
                UserId = TestUserId
            });
            await db.SaveChangesAsync();
        }

        var response = await Client.GetAsync($"/api/organizations/{TestOrganizationId}/meetings/{meetingId}/summary/personalized");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PersonalizedMeetingSummaryResponse>();
        body.Should().NotBeNull();
        body!.Status.Should().Be("processing");
        body.SummaryText.Should().BeNull();
        body.UserId.Should().Be(TestUserId);
    }

    [Fact]
    public async Task GetPersonalizedSummary_WithoutArtifact_ForCompletedParticipantMeeting_ShouldReturnNotAvailableState()
    {
        var meetingId = await SeedMeetingAsync(MeetingStatus.Completed);
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.MeetingParticipants.Add(new MeetingParticipant
            {
                OrganizationId = TestOrganizationId,
                MeetingId = meetingId,
                UserId = TestUserId
            });
            await db.SaveChangesAsync();
        }

        var response = await Client.GetAsync($"/api/organizations/{TestOrganizationId}/meetings/{meetingId}/summary/personalized");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PersonalizedMeetingSummaryResponse>();
        body.Should().NotBeNull();
        body!.Status.Should().Be("not_available");
        body.SummaryText.Should().BeNull();
        body.GeneratedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task GetPersonalizedSummary_ForNonParticipant_ShouldReturnForbidden()
    {
        var meetingId = await SeedMeetingAsync(MeetingStatus.Completed);

        var response = await Client.GetAsync($"/api/organizations/{TestOrganizationId}/meetings/{meetingId}/summary/personalized");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetTranscript_WithoutArtifact_ForInProgressMeeting_ShouldReturnProcessingState()
    {
        var meetingId = await SeedMeetingAsync(MeetingStatus.InProgress);

        var response = await Client.GetAsync($"/api/organizations/{TestOrganizationId}/meetings/{meetingId}/transcript");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<MeetingTranscriptResponse>();
        body.Should().NotBeNull();
        body!.Status.Should().Be("processing");
        body.FullText.Should().BeNull();
        body.Segments.Should().BeEmpty();
    }

    [Fact]
    public async Task GetSummary_WithoutArtifact_ForCompletedMeeting_ShouldReturnNotAvailableState()
    {
        var meetingId = await SeedMeetingAsync(MeetingStatus.Completed);

        var response = await Client.GetAsync($"/api/organizations/{TestOrganizationId}/meetings/{meetingId}/summary");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<MeetingSummaryResponse>();
        body.Should().NotBeNull();
        body!.Status.Should().Be("not_available");
        body.SummaryText.Should().BeNull();
        body.GeneratedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task GetTranscript_ForMissingMeeting_ShouldReturnNotFound()
    {
        var response = await Client.GetAsync($"/api/organizations/{TestOrganizationId}/meetings/{Guid.NewGuid()}/transcript");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetSummary_WithCrossOrgToken_ShouldReturnForbidden()
    {
        var meetingId = await SeedMeetingAsync();
        var otherOrgId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Users.Add(new ApplicationUser
            {
                Id = otherUserId,
                Email = $"test-{otherUserId:N}@test.com",
                UserName = $"test-{otherUserId:N}@test.com",
                EmailConfirmed = true,
                SecurityStamp = Guid.NewGuid().ToString()
            });
            db.Organizations.Add(new Organization { Id = otherOrgId, Name = "Other Org", Slug = $"other-{otherOrgId:N}" });
            db.UserOrgMemberships.Add(new UserOrgMembership
            {
                UserId = otherUserId,
                OrganizationId = otherOrgId,
                IsEnabled = true,
                OrgRole = OrganizationRole.Member
            });
            await db.SaveChangesAsync();
        }

        using var otherClient = CreateAuthenticatedClient(otherUserId, otherOrgId);
        var response = await otherClient.GetAsync($"/api/organizations/{TestOrganizationId}/meetings/{meetingId}/summary");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
