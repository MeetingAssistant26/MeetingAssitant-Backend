using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using MeetingAssistant.Features.AgentApi.Models.Responses;
using MeetingAssistant.Features.Identity.Entites;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Features.Organizations.Contracts.Responses;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Tests.Integration.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeetingAssistant.Tests.Integration.AgentApi;

public class AgentCatalogTests : IntegrationTestBase
{
    public AgentCatalogTests(MeetingAssistantWebFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task ListMeetings_ShouldSupportStatusFiltering_AndPagination()
    {
        // Arrange
        var upcoming1 = await SeedMeetingAsync(TestOrganizationId, "Upcoming 1", MeetingStatus.Scheduled, DateTime.UtcNow.AddHours(1));
        var upcoming2 = await SeedMeetingAsync(TestOrganizationId, "Upcoming 2", MeetingStatus.InProgress, DateTime.UtcNow.AddMinutes(-5));
        _ = await SeedMeetingAsync(TestOrganizationId, "Past", MeetingStatus.Completed, DateTime.UtcNow.AddDays(-1));

        SetAgentAuthorization(TestOrganizationId, upcoming1);

        // Act
        var response = await Client.GetAsync("/api/agent/meetings?status=upcoming&limit=1&offset=0");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AgentMeetingListResponse>();
        body.Should().NotBeNull();
        body!.Items.Should().HaveCount(1);
        body.TotalCount.Should().Be(2);
        body.Limit.Should().Be(1);
        body.Offset.Should().Be(0);

        var pastResponse = await Client.GetAsync("/api/agent/meetings?status=past&limit=20&offset=0");
        pastResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var pastBody = await pastResponse.Content.ReadFromJsonAsync<AgentMeetingListResponse>();
        pastBody.Should().NotBeNull();
        pastBody!.Items.Should().ContainSingle(m => m.Status == nameof(MeetingStatus.Completed));
    }

    [Fact]
    public async Task GetMeetingDetail_ShouldReturnParticipantsAndRecurrence()
    {
        // Arrange
        var seedMeetingId = await SeedMeetingAsync(TestOrganizationId, "Seed", MeetingStatus.InProgress, DateTime.UtcNow);
        SetAgentAuthorization(TestOrganizationId, seedMeetingId);

        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var collaboratorId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser
        {
            Id = collaboratorId,
            UserName = $"catalog-collab-{collaboratorId:N}@test.com",
            NormalizedUserName = $"CATALOG-COLLAB-{collaboratorId:N}@TEST.COM",
            Email = $"catalog-collab-{collaboratorId:N}@test.com",
            NormalizedEmail = $"CATALOG-COLLAB-{collaboratorId:N}@TEST.COM",
            EmailConfirmed = true,
            DisplayName = "Catalog Collaborator",
            SecurityStamp = Guid.NewGuid().ToString()
        });

        db.UserOrgMemberships.Add(new UserOrgMembership
        {
            UserId = collaboratorId,
            OrganizationId = TestOrganizationId,
            OrgRole = OrganizationRole.Member,
            JobRole = "Platform Engineer",
            Context = "Supports data pipelines",
            IsEnabled = true
        });

        var detailMeetingId = Guid.NewGuid();
        var tagId = Guid.NewGuid();

        db.MeetingTags.Add(new MeetingTag
        {
            Id = tagId,
            OrganizationId = TestOrganizationId,
            Name = "Architecture",
            Color = "#2196F3",
            IsActive = true
        });

        db.Meetings.Add(new Meeting
        {
            Id = detailMeetingId,
            OrganizationId = TestOrganizationId,
            Title = "Architecture Review",
            ScheduledStartUtc = DateTime.UtcNow.AddDays(2),
            ScheduledEndUtc = DateTime.UtcNow.AddDays(2).AddHours(1),
            Status = MeetingStatus.Scheduled,
            RecurrenceConfig = new RecurrenceConfig
            {
                Frequency = RecurrenceFrequency.Weekly,
                Interval = 1,
                DaysOfWeek = "Monday"
            }
        });

        db.MeetingParticipants.AddRange(
            new MeetingParticipant
            {
                MeetingId = detailMeetingId,
                OrganizationId = TestOrganizationId,
                UserId = TestUserId,
                MeetingRole = MeetingRole.Host
            },
            new MeetingParticipant
            {
                MeetingId = detailMeetingId,
                OrganizationId = TestOrganizationId,
                UserId = collaboratorId,
                MeetingRole = MeetingRole.Participant
            });

        db.MeetingMeetingTags.Add(new MeetingMeetingTag
        {
            MeetingId = detailMeetingId,
            MeetingTagId = tagId
        });

        await db.SaveChangesAsync();

        // Act
        var response = await Client.GetAsync($"/api/agent/meetings/{detailMeetingId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AgentMeetingDetailResponse>();
        body.Should().NotBeNull();
        body!.Id.Should().Be(detailMeetingId);
        body.RecurrenceConfig.Should().NotBeNull();
        body.TagIds.Should().Contain(tagId);
        body.Participants.Should().Contain(p => p.UserId == collaboratorId && p.JobRole == "Platform Engineer");
    }

    [Fact]
    public async Task ListRecurringMeetings_ShouldReturnOnlyRecurringSeries()
    {
        // Arrange
        var boundMeetingId = await SeedMeetingAsync(TestOrganizationId, "Bound", MeetingStatus.InProgress, DateTime.UtcNow);
        var recurringId = await SeedMeetingAsync(TestOrganizationId, "Recurring", MeetingStatus.Scheduled, DateTime.UtcNow.AddDays(7), isRecurring: true);
        _ = await SeedMeetingAsync(TestOrganizationId, "One-off", MeetingStatus.Scheduled, DateTime.UtcNow.AddDays(3));

        SetAgentAuthorization(TestOrganizationId, boundMeetingId);

        // Act
        var response = await Client.GetAsync("/api/agent/meetings/recurring");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<List<AgentMeetingResponse>>();
        body.Should().NotBeNull();
        body!.Should().ContainSingle(m => m.Id == recurringId);
    }

    [Fact]
    public async Task ListMeetingTags_ShouldReturnOnlyActiveTagsForCurrentOrganization()
    {
        // Arrange
        var boundMeetingId = await SeedMeetingAsync(TestOrganizationId, "Bound", MeetingStatus.InProgress, DateTime.UtcNow);
        var foreignOrgId = Guid.NewGuid();

        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.Organizations.Add(new Organization
        {
            Id = foreignOrgId,
            Name = "Foreign Org",
            Slug = $"foreign-org-{foreignOrgId:N}".ToLowerInvariant()
        });

        var activeOwnTagId = Guid.NewGuid();
        db.MeetingTags.AddRange(
            new MeetingTag
            {
                Id = activeOwnTagId,
                OrganizationId = TestOrganizationId,
                Name = "Backend",
                Color = "#FF9800",
                IsActive = true
            },
            new MeetingTag
            {
                Id = Guid.NewGuid(),
                OrganizationId = TestOrganizationId,
                Name = "Inactive",
                IsActive = false
            },
            new MeetingTag
            {
                Id = Guid.NewGuid(),
                OrganizationId = foreignOrgId,
                Name = "Foreign",
                IsActive = true
            });

        await db.SaveChangesAsync();
        SetAgentAuthorization(TestOrganizationId, boundMeetingId);

        // Act
        var response = await Client.GetAsync("/api/agent/meeting-tags");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<List<MeetingTagResponse>>();
        body.Should().NotBeNull();
        body!.Should().ContainSingle(t => t.Id == activeOwnTagId);
    }

    [Fact]
    public async Task RefreshToken_WhenMeetingInProgress_ShouldReturnNewToken()
    {
        // Arrange
        var meetingId = await SeedMeetingAsync(TestOrganizationId, "In Progress", MeetingStatus.InProgress, DateTime.UtcNow);
        SetAgentAuthorization(TestOrganizationId, meetingId);

        // Act
        var response = await Client.PostAsync("/api/agent/refresh", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AgentTokenResponse>();
        body.Should().NotBeNull();
        body!.Token.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task RefreshToken_WhenMeetingCompleted_ShouldReturnForbidden()
    {
        // Arrange
        var meetingId = await SeedMeetingAsync(TestOrganizationId, "Completed", MeetingStatus.Completed, DateTime.UtcNow.AddDays(-1));
        SetAgentAuthorization(TestOrganizationId, meetingId);

        // Act
        var response = await Client.PostAsync("/api/agent/refresh", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private void SetAgentAuthorization(Guid organizationId, Guid meetingId)
    {
        var token = TestJwtTokenHelper.GenerateAgentToken(organizationId, meetingId);
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private async Task<Guid> SeedMeetingAsync(
        Guid organizationId,
        string title,
        MeetingStatus status,
        DateTime scheduledStartUtc,
        bool isRecurring = false)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var meetingId = Guid.NewGuid();
        db.Meetings.Add(new Meeting
        {
            Id = meetingId,
            OrganizationId = organizationId,
            Title = title,
            ScheduledStartUtc = scheduledStartUtc,
            ScheduledEndUtc = scheduledStartUtc.AddHours(1),
            Status = status,
            RecurrenceConfig = isRecurring
                ? new RecurrenceConfig { Frequency = RecurrenceFrequency.Weekly, Interval = 1, DaysOfWeek = "Monday" }
                : null
        });

        await db.SaveChangesAsync();
        return meetingId;
    }
}
