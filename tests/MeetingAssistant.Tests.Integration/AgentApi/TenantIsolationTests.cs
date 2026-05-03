using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using MeetingAssistant.Features.AgentApi.Models.Responses;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Features.Organizations.Models;
using ReminderEntity = MeetingAssistant.Features.Tasks.Models.Entities.Reminder;
using MeetingAssistant.Features.Tasks.Models.Enums;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Tests.Integration.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeetingAssistant.Tests.Integration.AgentApi;

public class TenantIsolationTests : IntegrationTestBase
{
    public TenantIsolationTests(MeetingAssistantWebFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task GetOrganization_ShouldReturnOnlyBoundOrganization()
    {
        // Arrange
        var boundMeetingId = await SeedMeetingAsync(TestOrganizationId, "Bound", MeetingStatus.InProgress);
        var foreignOrgId = await SeedForeignOrganizationAsync();
        _ = await SeedMeetingAsync(foreignOrgId, "Foreign", MeetingStatus.InProgress);

        SetAgentAuthorization(TestOrganizationId, boundMeetingId);

        // Act
        var response = await Client.GetAsync("/api/agent/organization");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AgentOrganizationResponse>();
        body.Should().NotBeNull();
        body!.Id.Should().Be(TestOrganizationId);
        body.Id.Should().NotBe(foreignOrgId);
    }

    [Fact]
    public async Task GetMeetingDetail_ForForeignMeeting_ShouldReturnNotFound()
    {
        // Arrange
        var boundMeetingId = await SeedMeetingAsync(TestOrganizationId, "Bound", MeetingStatus.InProgress);
        var foreignOrgId = await SeedForeignOrganizationAsync();
        var foreignMeetingId = await SeedMeetingAsync(foreignOrgId, "Foreign", MeetingStatus.Scheduled);

        SetAgentAuthorization(TestOrganizationId, boundMeetingId);

        // Act
        var response = await Client.GetAsync($"/api/agent/meetings/{foreignMeetingId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetMeetingMembers_ForDifferentMeetingBinding_ShouldReturnForbidden()
    {
        // Arrange
        var boundMeetingId = await SeedMeetingAsync(TestOrganizationId, "Bound", MeetingStatus.InProgress);
        var otherMeetingId = await SeedMeetingAsync(TestOrganizationId, "Other", MeetingStatus.Scheduled);

        SetAgentAuthorization(TestOrganizationId, boundMeetingId);

        // Act
        var response = await Client.GetAsync($"/api/agent/meetings/{otherMeetingId}/members");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task MarkReminderDelivered_ForForeignReminder_ShouldReturnNotFound()
    {
        // Arrange
        var boundMeetingId = await SeedMeetingAsync(TestOrganizationId, "Bound", MeetingStatus.InProgress);
        var foreignOrgId = await SeedForeignOrganizationAsync();
        var foreignMeetingId = await SeedMeetingAsync(foreignOrgId, "Foreign", MeetingStatus.InProgress);

        var foreignReminderId = await SeedReminderAsync(foreignOrgId, foreignMeetingId);

        SetAgentAuthorization(TestOrganizationId, boundMeetingId);

        // Act
        var response = await Client.PostAsync($"/api/agent/reminders/{foreignReminderId}/mark-delivered", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private void SetAgentAuthorization(Guid organizationId, Guid meetingId)
    {
        var token = TestJwtTokenHelper.GenerateAgentToken(organizationId, meetingId);
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private async Task<Guid> SeedForeignOrganizationAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var foreignOrgId = Guid.NewGuid();
        db.Organizations.Add(new Organization
        {
            Id = foreignOrgId,
            Name = "Foreign Tenant",
            Slug = $"foreign-tenant-{foreignOrgId:N}".ToLowerInvariant()
        });

        await db.SaveChangesAsync();
        return foreignOrgId;
    }

    private async Task<Guid> SeedMeetingAsync(Guid organizationId, string title, MeetingStatus status)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var meetingId = Guid.NewGuid();
        var start = DateTime.UtcNow;
        db.Meetings.Add(new Meeting
        {
            Id = meetingId,
            OrganizationId = organizationId,
            Title = title,
            ScheduledStartUtc = start,
            ScheduledEndUtc = start.AddHours(1),
            Status = status
        });

        await db.SaveChangesAsync();
        return meetingId;
    }

    private async Task<Guid> SeedReminderAsync(Guid organizationId, Guid meetingId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var reminder = new ReminderEntity
        {
            OrganizationId = organizationId,
            Text = "Foreign reminder",
            Scope = ReminderScope.Public,
            Channel = ReminderChannel.Agent,
            CreatedByUserId = Guid.NewGuid(),
            MeetingId = meetingId,
            ReminderAtUtc = DateTime.UtcNow.AddMinutes(-1),
            Status = ReminderStatus.Active
        };

        db.Reminders.Add(reminder);
        await db.SaveChangesAsync();
        return reminder.Id;
    }
}
