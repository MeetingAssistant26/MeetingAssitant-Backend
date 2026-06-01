using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using MeetingAssistant.Features.Identity.Entites;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Features.Tasks.Contracts.Responses;
using ReminderEntity = MeetingAssistant.Features.Tasks.Models.Entities.Reminder;
using MeetingAssistant.Features.Tasks.Models.Enums;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeetingAssistant.Tests.Integration.Tasks.Endpoints.Reminder;

public class ListMyRemindersTests : IntegrationTestBase
{
    public ListMyRemindersTests(MeetingAssistantWebFactory factory) : base(factory)
    {
    }

    private HttpClient CreateAuthenticatedClient(Guid userId, Guid orgId)
    {
        var client = Factory.CreateClient();
        var token = TestJwtTokenHelper.GenerateToken(userId, orgId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<ReminderEntity> SeedReminderAsync(
        Guid orgId,
        Guid createdByUserId,
        Guid? targetUserId = null,
        ReminderScope scope = ReminderScope.Personal,
        ReminderStatus status = ReminderStatus.Active,
        DateTime? reminderAt = null,
        Guid? meetingId = null)
    {
        using var scope1 = Factory.Services.CreateScope();
        var db = scope1.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var reminder = new ReminderEntity
        {
            OrganizationId = orgId,
            Text = "Test reminder",
            Scope = scope,
            Channel = ReminderChannel.User,
            CreatedByUserId = createdByUserId,
            TargetUserId = targetUserId ?? createdByUserId,
            MeetingId = meetingId,
            ReminderAtUtc = reminderAt ?? DateTime.UtcNow.AddMinutes(-1),
            Status = status
        };

        db.Reminders.Add(reminder);
        await db.SaveChangesAsync();
        return reminder;
    }

    private async Task<Meeting> SeedMeetingWithParticipantAsync(
        Guid orgId,
        Guid meetingId,
        Guid participantUserId)
    {
        using var scope1 = Factory.Services.CreateScope();
        var db = scope1.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var meeting = new Meeting
        {
            Id = meetingId,
            OrganizationId = orgId,
            Title = "Test Meeting",
            ScheduledStartUtc = DateTime.UtcNow.AddDays(1),
            ScheduledEndUtc = DateTime.UtcNow.AddDays(1).AddHours(1),
            Status = MeetingStatus.Scheduled
        };

        db.Meetings.Add(meeting);
        db.MeetingParticipants.Add(new MeetingParticipant
        {
            MeetingId = meetingId,
            OrganizationId = orgId,
            UserId = participantUserId,
            MeetingRole = MeetingRole.Participant
        });
        await db.SaveChangesAsync();
        return meeting;
    }

    [Fact]
    public async Task ListMyReminders_WithPersonalReminder_ShouldReturnReminder()
    {
        // Arrange
        var reminder = await SeedReminderAsync(TestOrganizationId, TestUserId, TestUserId);

        // Act
        var response = await Client.GetAsync("/api/me/reminders?page=1&pageSize=20");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ReminderListResponse>();
        result.Should().NotBeNull();
        result!.Items.Should().ContainSingle(r => r.Id == reminder.Id);
        result.TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task ListMyReminders_WithFutureReminder_ShouldNotReturnReminder()
    {
        // Arrange
        await SeedReminderAsync(TestOrganizationId, TestUserId, TestUserId, reminderAt: DateTime.UtcNow.AddDays(1));

        // Act
        var response = await Client.GetAsync("/api/me/reminders?page=1&pageSize=20");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ReminderListResponse>();
        result.Should().NotBeNull();
        result!.Items.Should().BeEmpty();
        result.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task ListMyReminders_WithCancelledReminder_ShouldNotReturnReminder()
    {
        // Arrange
        await SeedReminderAsync(TestOrganizationId, TestUserId, TestUserId, status: ReminderStatus.Cancelled);

        // Act
        var response = await Client.GetAsync("/api/me/reminders?page=1&pageSize=20");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ReminderListResponse>();
        result.Should().NotBeNull();
        result!.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task ListMyReminders_WithPublicMeetingReminder_ShouldReturnReminderForParticipant()
    {
        // Arrange
        var meetingId = Guid.NewGuid();
        await SeedMeetingWithParticipantAsync(TestOrganizationId, meetingId, TestUserId);
        await SeedReminderAsync(
            TestOrganizationId,
            TestUserId,
            null,
            ReminderScope.Public,
            ReminderStatus.Active,
            DateTime.UtcNow.AddMinutes(-1),
            meetingId);

        // Act
        var response = await Client.GetAsync("/api/me/reminders?page=1&pageSize=20");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ReminderListResponse>();
        result.Should().NotBeNull();
        result!.Items.Should().HaveCount(1);
        result.Items[0].Scope.Should().Be(ReminderScope.Public);
    }

    [Fact]
    public async Task ListMyReminders_WithPublicMeetingReminder_NonParticipant_ShouldNotReturnReminder()
    {
        // Arrange
        var otherUserId = Guid.NewGuid();
        var meetingId = Guid.NewGuid();
        await SeedMeetingWithParticipantAsync(TestOrganizationId, meetingId, TestUserId);
        await SeedReminderAsync(
            TestOrganizationId,
            TestUserId,
            null,
            ReminderScope.Public,
            ReminderStatus.Active,
            DateTime.UtcNow.AddMinutes(-1),
            meetingId);

        var client = CreateAuthenticatedClient(otherUserId, TestOrganizationId);
        using (var scope1 = Factory.Services.CreateScope())
        {
            var db = scope1.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = new ApplicationUser
            {
                Id = otherUserId,
                Email = $"test-{otherUserId}@test.com",
                UserName = $"test-{otherUserId}@test.com"
            };
            db.Users.Add(user);
            db.UserOrgMemberships.Add(new UserOrgMembership
            {
                UserId = otherUserId,
                OrganizationId = TestOrganizationId,
                IsEnabled = true,
                OrgRole = OrganizationRole.Member
            });
            await db.SaveChangesAsync();
        }

        // Act
        var response = await client.GetAsync("/api/me/reminders?page=1&pageSize=20");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ReminderListResponse>();
        result.Should().NotBeNull();
        result!.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task ListMyReminders_WithPublicMeetingReminder_CancelledMeeting_ShouldNotReturnReminder()
    {
        // Arrange
        var meetingId = Guid.NewGuid();
        var meeting = await SeedMeetingWithParticipantAsync(TestOrganizationId, meetingId, TestUserId);
        meeting.Status = MeetingStatus.Cancelled;
        using (var scope1 = Factory.Services.CreateScope())
        {
            var db = scope1.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Meetings.Update(meeting);
            await db.SaveChangesAsync();
        }

        await SeedReminderAsync(
            TestOrganizationId,
            TestUserId,
            null,
            ReminderScope.Public,
            ReminderStatus.Active,
            DateTime.UtcNow.AddMinutes(-1),
            meetingId);

        // Act
        var response = await Client.GetAsync("/api/me/reminders?page=1&pageSize=20");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ReminderListResponse>();
        result.Should().NotBeNull();
        result!.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task ListMyReminders_Pagination_ShouldReturnCorrectPage()
    {
        // Arrange
        for (int i = 0; i < 5; i++)
        {
            await SeedReminderAsync(
                TestOrganizationId,
                TestUserId,
                TestUserId,
                reminderAt: DateTime.UtcNow.AddMinutes(-i - 1));
        }

        // Act
        var response = await Client.GetAsync("/api/me/reminders?page=1&pageSize=2");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ReminderListResponse>();
        result.Should().NotBeNull();
        result!.Items.Should().HaveCount(2);
        result.TotalCount.Should().Be(5);
        result.Page.Should().Be(1);
        result.PageSize.Should().Be(2);
    }

    [Fact]
    public async Task ListMyReminders_InvalidPagination_ShouldClampLowerBounds()
    {
        // Arrange
        await SeedReminderAsync(TestOrganizationId, TestUserId, TestUserId);

        // Act
        var response = await Client.GetAsync("/api/me/reminders?page=0&pageSize=0");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ReminderListResponse>();
        result.Should().NotBeNull();
        result!.Items.Should().HaveCount(1);
        result.Page.Should().Be(1);
        result.PageSize.Should().Be(1);
    }

    [Fact]
    public async Task ListMyReminders_WithIncludeFutureTrue_ShouldReturnFutureActivePersonalReminder()
    {
        // Arrange
        var reminder = await SeedReminderAsync(
            TestOrganizationId,
            TestUserId,
            TestUserId,
            reminderAt: DateTime.UtcNow.AddDays(1));

        // Act
        var response = await Client.GetAsync("/api/me/reminders?includeFuture=true&page=1&pageSize=20");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ReminderListResponse>();
        result.Should().NotBeNull();
        result!.Items.Should().ContainSingle(r => r.Id == reminder.Id);
        result.TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task ListMyReminders_Unauthenticated_ShouldReturnUnauthorized()
    {
        // Arrange
        var unauthClient = Factory.CreateClient();

        // Act
        var response = await unauthClient.GetAsync("/api/me/reminders");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
