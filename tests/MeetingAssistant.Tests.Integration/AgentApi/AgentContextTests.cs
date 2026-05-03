using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using MeetingAssistant.Features.AgentApi.Models.Responses;
using MeetingAssistant.Features.Identity.Entites;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Tests.Integration.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeetingAssistant.Tests.Integration.AgentApi;

public class AgentContextTests : IntegrationTestBase
{
    public AgentContextTests(MeetingAssistantWebFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task GetOrganization_WithAgentToken_ShouldReturnOrganizationProfile()
    {
        // Arrange
        var meetingId = await SeedMeetingAsync(TestOrganizationId);
        SetAgentAuthorization(TestOrganizationId, meetingId);

        // Act
        var response = await Client.GetAsync("/api/agent/organization");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AgentOrganizationResponse>();
        body.Should().NotBeNull();
        body!.Id.Should().Be(TestOrganizationId);
        body.Name.Should().Be("Test Organization");
        body.Slug.Should().Be("test-org");
        body.MemberCount.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task GetMeetingMembers_WithAgentToken_ShouldReturnMeetingRoster()
    {
        // Arrange
        var meetingId = await SeedMeetingWithParticipantsAsync(TestOrganizationId);
        SetAgentAuthorization(TestOrganizationId, meetingId);

        // Act
        var response = await Client.GetAsync($"/api/agent/meetings/{meetingId}/members");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<List<AgentMemberResponse>>();
        body.Should().NotBeNull();
        body!.Should().HaveCount(2);
        body.Should().Contain(m => m.UserId == TestUserId && m.DisplayName == "Test User");
        body.Should().Contain(m => m.JobRole == "Backend Engineer" && m.Context == "Owns API integrations");
    }

    [Fact]
    public async Task GetMeetingMembers_MismatchedMeetingId_ShouldReturnForbidden()
    {
        // Arrange
        var tokenMeetingId = await SeedMeetingAsync(TestOrganizationId);
        var otherMeetingId = await SeedMeetingAsync(TestOrganizationId);
        SetAgentAuthorization(TestOrganizationId, tokenMeetingId);

        // Act
        var response = await Client.GetAsync($"/api/agent/meetings/{otherMeetingId}/members");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetMeetingMembers_CrossTenantMeeting_ShouldReturnForbidden()
    {
        // Arrange
        var tokenMeetingId = await SeedMeetingAsync(TestOrganizationId);
        var foreignOrgId = Guid.NewGuid();
        var foreignMeetingId = await SeedMeetingAsync(foreignOrgId);
        SetAgentAuthorization(TestOrganizationId, tokenMeetingId);

        // Act
        var response = await Client.GetAsync($"/api/agent/meetings/{foreignMeetingId}/members");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private void SetAgentAuthorization(Guid organizationId, Guid meetingId)
    {
        var token = TestJwtTokenHelper.GenerateAgentToken(organizationId, meetingId);
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private async Task<Guid> SeedMeetingWithParticipantsAsync(Guid organizationId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var collaboratorId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser
        {
            Id = collaboratorId,
            UserName = $"collab-{collaboratorId:N}@test.com",
            NormalizedUserName = $"COLLAB-{collaboratorId:N}@TEST.COM",
            Email = $"collab-{collaboratorId:N}@test.com",
            NormalizedEmail = $"COLLAB-{collaboratorId:N}@TEST.COM",
            EmailConfirmed = true,
            DisplayName = "Collaborator",
            SecurityStamp = Guid.NewGuid().ToString()
        });

        db.UserOrgMemberships.Add(new UserOrgMembership
        {
            UserId = collaboratorId,
            OrganizationId = organizationId,
            OrgRole = OrganizationRole.Member,
            JobRole = "Backend Engineer",
            Context = "Owns API integrations",
            IsEnabled = true
        });

        var meetingId = Guid.NewGuid();
        db.Meetings.Add(new Meeting
        {
            Id = meetingId,
            OrganizationId = organizationId,
            Title = "Agent Context Test Meeting",
            ScheduledStartUtc = DateTime.UtcNow.AddMinutes(-5),
            ScheduledEndUtc = DateTime.UtcNow.AddMinutes(25),
            Status = MeetingStatus.InProgress
        });

        db.MeetingParticipants.AddRange(
            new MeetingParticipant
            {
                MeetingId = meetingId,
                OrganizationId = organizationId,
                UserId = TestUserId,
                MeetingRole = MeetingRole.Host
            },
            new MeetingParticipant
            {
                MeetingId = meetingId,
                OrganizationId = organizationId,
                UserId = collaboratorId,
                MeetingRole = MeetingRole.Participant
            });

        await db.SaveChangesAsync();
        return meetingId;
    }

    private async Task<Guid> SeedMeetingAsync(Guid organizationId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        if (organizationId != TestOrganizationId)
        {
            db.Organizations.Add(new Organization
            {
                Id = organizationId,
                Name = "Foreign Organization",
                Slug = $"foreign-org-{organizationId:N}".ToLowerInvariant()
            });
        }

        var meetingId = Guid.NewGuid();
        db.Meetings.Add(new Meeting
        {
            Id = meetingId,
            OrganizationId = organizationId,
            Title = "Seeded Agent Meeting",
            ScheduledStartUtc = DateTime.UtcNow.AddMinutes(-10),
            ScheduledEndUtc = DateTime.UtcNow.AddMinutes(20),
            Status = MeetingStatus.InProgress
        });

        await db.SaveChangesAsync();
        return meetingId;
    }
}
