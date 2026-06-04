using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using MeetingAssistant.Features.AgentApi.Models.Responses;
using MeetingAssistant.Features.Meetings.Models;
using ReminderEntity = MeetingAssistant.Features.Tasks.Models.Entities.Reminder;
using MeetingAssistant.Features.Tasks.Models.Enums;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeetingAssistant.Tests.Integration.AgentApi;

public class AgentReminderTests : IntegrationTestBase
{
    public AgentReminderTests(MeetingAssistantWebFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task CreateReminder_Public_ShouldReturnCreated()
    {
        // Arrange
        var meetingId = await SeedMeetingAsync(TestOrganizationId, MeetingStatus.InProgress);
        SetAgentAuthorization(TestOrganizationId, meetingId);

        var request = new
        {
            text = "Review API metrics",
            scope = "Public",
            targetUserId = (Guid?)null,
            reminderAtUtc = DateTime.UtcNow.AddMinutes(15),
            createdByUserId = TestUserId
        };

        // Act
        var response = await Client.PostAsJsonAsync($"/api/agent/meetings/{meetingId}/reminders", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<AgentReminderResponse>();
        body.Should().NotBeNull();
        body!.Text.Should().Be("Review API metrics");
        body.Scope.Should().Be("Public");
        body.Status.Should().Be("Active");

        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var persisted = await db.Reminders
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == body.Id);
        persisted.Should().NotBeNull();
        persisted!.Channel.Should().Be(ReminderChannel.Agent);
        persisted.MeetingId.Should().Be(meetingId);
    }

    [Fact]
    public async Task ListMeetingReminders_ShouldReturnPublicDueOnly()
    {
        // Arrange
        var scheduledStart = DateTime.UtcNow;
        var meetingId = await SeedMeetingAsync(TestOrganizationId, MeetingStatus.InProgress, scheduledStart);

        var publicDueId = await SeedReminderAsync(
            TestOrganizationId,
            meetingId,
            ReminderScope.Public,
            ReminderStatus.Active,
            ReminderChannel.Agent,
            scheduledStart.AddMinutes(-1));

        await SeedReminderAsync(
            TestOrganizationId,
            meetingId,
            ReminderScope.Personal,
            ReminderStatus.Active,
            ReminderChannel.Agent,
            scheduledStart.AddMinutes(-1),
            targetUserId: TestUserId);

        await SeedReminderAsync(
            TestOrganizationId,
            meetingId,
            ReminderScope.Public,
            ReminderStatus.Active,
            ReminderChannel.Agent,
            scheduledStart.AddMinutes(20));

        SetAgentAuthorization(TestOrganizationId, meetingId);

        // Act
        var response = await Client.GetAsync($"/api/agent/meetings/{meetingId}/reminders");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<List<AgentReminderResponse>>();
        body.Should().NotBeNull();
        body!.Should().ContainSingle(r => r.Id == publicDueId && r.Scope == "Public");
    }

    [Fact]
    public async Task ListMeetingReminders_ShouldCarryForwardPublicDueReminderFromPriorSeriesMeeting()
    {
        // Arrange
        var m1Start = DateTime.UtcNow.AddHours(1);
        var (_, meetings) = await SeedRecurringMeetingsAsync(TestOrganizationId, m1Start, m1Start.AddDays(7));
        var m1Id = meetings[0];
        var m2Id = meetings[1];

        var reminderId = await SeedReminderAsync(
            TestOrganizationId,
            m1Id,
            ReminderScope.Public,
            ReminderStatus.Active,
            ReminderChannel.Agent,
            m1Start.AddDays(7).AddMinutes(-5));

        SetAgentAuthorization(TestOrganizationId, m2Id);

        // Act
        var response = await Client.GetAsync($"/api/agent/meetings/{m2Id}/reminders");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<List<AgentReminderResponse>>();
        body.Should().NotBeNull();
        body!.Should().ContainSingle(r => r.Id == reminderId);
    }

    [Fact]
    public async Task ListMeetingReminders_ShouldFilterInvalidCarryForwardCandidates()
    {
        // Arrange
        var m1Start = DateTime.UtcNow.AddHours(1);
        var m2Start = m1Start.AddDays(7);
        var (_, meetings) = await SeedRecurringMeetingsAsync(TestOrganizationId, m1Start, m2Start, m2Start.AddDays(7));
        var m1Id = meetings[0];
        var m2Id = meetings[1];
        var m3Id = meetings[2];

        var (_, differentSeriesMeetings) = await SeedRecurringMeetingsAsync(
            TestOrganizationId,
            m1Start.AddDays(30));
        var differentSeriesMeetingId = differentSeriesMeetings[0];

        await SeedReminderAsync(
            TestOrganizationId,
            m1Id,
            ReminderScope.Personal,
            ReminderStatus.Active,
            ReminderChannel.Agent,
            m2Start.AddMinutes(-10),
            targetUserId: TestUserId);
        await SeedReminderAsync(
            TestOrganizationId,
            m1Id,
            ReminderScope.Public,
            ReminderStatus.Cancelled,
            ReminderChannel.Agent,
            m2Start.AddMinutes(-9));
        await SeedReminderAsync(
            TestOrganizationId,
            m1Id,
            ReminderScope.Public,
            ReminderStatus.Delivered,
            ReminderChannel.Agent,
            m2Start.AddMinutes(-8));
        await SeedReminderAsync(
            TestOrganizationId,
            differentSeriesMeetingId,
            ReminderScope.Public,
            ReminderStatus.Active,
            ReminderChannel.Agent,
            m2Start.AddMinutes(-7));
        await SeedReminderAsync(
            TestOrganizationId,
            m3Id,
            ReminderScope.Public,
            ReminderStatus.Active,
            ReminderChannel.Agent,
            m2Start.AddMinutes(-6));
        await SeedReminderAsync(
            TestOrganizationId,
            m1Id,
            ReminderScope.Public,
            ReminderStatus.Active,
            ReminderChannel.Agent,
            m2Start.AddMinutes(1));

        var validReminderId = await SeedReminderAsync(
            TestOrganizationId,
            m1Id,
            ReminderScope.Public,
            ReminderStatus.Active,
            ReminderChannel.Agent,
            m2Start.AddMinutes(-5));

        SetAgentAuthorization(TestOrganizationId, m2Id);

        // Act
        var response = await Client.GetAsync($"/api/agent/meetings/{m2Id}/reminders");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<List<AgentReminderResponse>>();
        body.Should().NotBeNull();
        body!.Select(r => r.Id).Should().BeEquivalentTo([validReminderId]);
    }

    [Fact]
    public async Task MarkReminderDelivered_ShouldReturnNoContent_AndCloseReminder()
    {
        // Arrange
        var meetingId = await SeedMeetingAsync(TestOrganizationId, MeetingStatus.InProgress);
        var reminderId = await SeedReminderAsync(
            TestOrganizationId,
            meetingId,
            ReminderScope.Public,
            ReminderStatus.Active,
            ReminderChannel.Agent,
            DateTime.UtcNow.AddMinutes(-5));

        SetAgentAuthorization(TestOrganizationId, meetingId);

        // Act
        var response = await Client.PostAsync($"/api/agent/reminders/{reminderId}/mark-delivered", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var reminder = await db.Reminders
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == reminderId);
        reminder.Should().NotBeNull();
        reminder!.Status.Should().Be(ReminderStatus.Delivered);
        reminder.DeliveredAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task MarkReminderDelivered_ShouldAllowCurrentSeriesMeetingToClosePriorReminder()
    {
        // Arrange
        var m1Start = DateTime.UtcNow.AddHours(1);
        var m2Start = m1Start.AddDays(7);
        var (_, meetings) = await SeedRecurringMeetingsAsync(TestOrganizationId, m1Start, m2Start);
        var m1Id = meetings[0];
        var m2Id = meetings[1];
        var reminderId = await SeedReminderAsync(
            TestOrganizationId,
            m1Id,
            ReminderScope.Public,
            ReminderStatus.Active,
            ReminderChannel.Agent,
            m2Start.AddMinutes(-5));

        SetAgentAuthorization(TestOrganizationId, m2Id);

        // Act
        var response = await Client.PostAsync($"/api/agent/reminders/{reminderId}/mark-delivered", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var reminder = await db.Reminders
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == reminderId);
        reminder.Should().NotBeNull();
        reminder!.Status.Should().Be(ReminderStatus.Delivered);
        reminder.DeliveredAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task ListMeetingReminders_ShouldExcludeDeliveredCarryForwardReminderFromLaterMeeting()
    {
        // Arrange
        var m1Start = DateTime.UtcNow.AddHours(1);
        var m2Start = m1Start.AddDays(7);
        var m3Start = m2Start.AddDays(7);
        var (_, meetings) = await SeedRecurringMeetingsAsync(TestOrganizationId, m1Start, m2Start, m3Start);
        var m1Id = meetings[0];
        var m2Id = meetings[1];
        var m3Id = meetings[2];
        var reminderId = await SeedReminderAsync(
            TestOrganizationId,
            m1Id,
            ReminderScope.Public,
            ReminderStatus.Active,
            ReminderChannel.Agent,
            m2Start.AddMinutes(-5));

        SetAgentAuthorization(TestOrganizationId, m2Id);
        var markDeliveredResponse = await Client.PostAsync($"/api/agent/reminders/{reminderId}/mark-delivered", null);
        markDeliveredResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        SetAgentAuthorization(TestOrganizationId, m3Id);

        // Act
        var response = await Client.GetAsync($"/api/agent/meetings/{m3Id}/reminders");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<List<AgentReminderResponse>>();
        body.Should().NotBeNull();
        body!.Should().NotContain(r => r.Id == reminderId);
    }

    [Fact]
    public async Task MarkReminderDelivered_FromDifferentSeries_ShouldReturnNotFoundAndLeaveActive()
    {
        // Arrange
        var m1Start = DateTime.UtcNow.AddHours(1);
        var (_, seriesAMeetings) = await SeedRecurringMeetingsAsync(TestOrganizationId, m1Start);
        var (_, seriesBMeetings) = await SeedRecurringMeetingsAsync(TestOrganizationId, m1Start.AddDays(7));
        var m1Id = seriesAMeetings[0];
        var currentMeetingId = seriesBMeetings[0];
        var reminderId = await SeedReminderAsync(
            TestOrganizationId,
            m1Id,
            ReminderScope.Public,
            ReminderStatus.Active,
            ReminderChannel.Agent,
            m1Start.AddMinutes(-5));

        SetAgentAuthorization(TestOrganizationId, currentMeetingId);

        // Act
        var response = await Client.PostAsync($"/api/agent/reminders/{reminderId}/mark-delivered", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var reminder = await db.Reminders
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == reminderId);
        reminder.Should().NotBeNull();
        reminder!.Status.Should().Be(ReminderStatus.Active);
        reminder.DeliveredAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task CancelReminder_ShouldReturnNoContent_AndSoftCancelReminder()
    {
        // Arrange
        var meetingId = await SeedMeetingAsync(TestOrganizationId, MeetingStatus.InProgress);
        var reminderId = await SeedReminderAsync(
            TestOrganizationId,
            meetingId,
            ReminderScope.Public,
            ReminderStatus.Active,
            ReminderChannel.Agent,
            DateTime.UtcNow.AddMinutes(-5));

        SetAgentAuthorization(TestOrganizationId, meetingId);

        // Act
        var response = await Client.DeleteAsync($"/api/agent/reminders/{reminderId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var reminder = await db.Reminders
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == reminderId);
        reminder.Should().NotBeNull();
        reminder!.Status.Should().Be(ReminderStatus.Cancelled);
    }

    [Fact]
    public async Task ListMeetingReminders_ShouldNotLeakPersonalReminders()
    {
        // Arrange
        var scheduledStart = DateTime.UtcNow;
        var meetingId = await SeedMeetingAsync(TestOrganizationId, MeetingStatus.InProgress, scheduledStart);

        await SeedReminderAsync(
            TestOrganizationId,
            meetingId,
            ReminderScope.Personal,
            ReminderStatus.Active,
            ReminderChannel.Agent,
            scheduledStart.AddMinutes(-2),
            targetUserId: TestUserId);

        await SeedReminderAsync(
            TestOrganizationId,
            meetingId,
            ReminderScope.Public,
            ReminderStatus.Active,
            ReminderChannel.Agent,
            scheduledStart.AddMinutes(-1));

        SetAgentAuthorization(TestOrganizationId, meetingId);

        // Act
        var response = await Client.GetAsync($"/api/agent/meetings/{meetingId}/reminders");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<List<AgentReminderResponse>>();
        body.Should().NotBeNull();
        body!.Should().NotContain(r => r.Scope == "Personal");
        body.Should().OnlyContain(r => r.Scope == "Public");
    }

    private void SetAgentAuthorization(Guid organizationId, Guid meetingId)
    {
        var token = TestJwtTokenHelper.GenerateAgentToken(organizationId, meetingId);
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private async Task<Guid> SeedMeetingAsync(Guid organizationId, MeetingStatus status, DateTime? scheduledStartUtc = null)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var meetingId = Guid.NewGuid();
        var start = scheduledStartUtc ?? DateTime.UtcNow;

        db.Meetings.Add(new Meeting
        {
            Id = meetingId,
            OrganizationId = organizationId,
            Title = "Agent Reminder Meeting",
            ScheduledStartUtc = start,
            ScheduledEndUtc = start.AddHours(1),
            Status = status
        });

        await db.SaveChangesAsync();
        return meetingId;
    }

    private async Task<(Guid SeriesId, List<Guid> MeetingIds)> SeedRecurringMeetingsAsync(
        Guid organizationId,
        params DateTime[] scheduledStartsUtc)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var firstStart = scheduledStartsUtc[0];
        var series = new RecurringMeetingSeries
        {
            OrganizationId = organizationId,
            Title = "Agent Reminder Series",
            ScheduledStartTimeUtc = firstStart.TimeOfDay,
            ScheduledEndTimeUtc = firstStart.AddHours(1).TimeOfDay,
            Frequency = RecurrenceFrequency.Weekly,
            Interval = 1,
            DaysOfWeek = firstStart.DayOfWeek.ToString(),
            Status = RecurringMeetingSeriesStatus.Active,
            CreatedByUserId = TestUserId
        };

        db.RecurringMeetingSeries.Add(series);

        var meetingIds = new List<Guid>();
        for (var index = 0; index < scheduledStartsUtc.Length; index++)
        {
            var meetingId = Guid.NewGuid();
            var start = scheduledStartsUtc[index];
            meetingIds.Add(meetingId);
            db.Meetings.Add(new Meeting
            {
                Id = meetingId,
                OrganizationId = organizationId,
                Title = $"Agent Reminder Series Occurrence {index}",
                ScheduledStartUtc = start,
                ScheduledEndUtc = start.AddHours(1),
                Status = MeetingStatus.InProgress,
                RecurringSeriesId = series.Id,
                RecurringOccurrenceIndex = index
            });
        }

        await db.SaveChangesAsync();
        return (series.Id, meetingIds);
    }

    private async Task<Guid> SeedReminderAsync(
        Guid organizationId,
        Guid meetingId,
        ReminderScope scope,
        ReminderStatus status,
        ReminderChannel channel,
        DateTime reminderAtUtc,
        Guid? targetUserId = null)
    {
        await using var scopeProvider = Factory.Services.CreateAsyncScope();
        var db = scopeProvider.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var reminder = new ReminderEntity
        {
            OrganizationId = organizationId,
            Text = "Seeded reminder",
            Scope = scope,
            Channel = channel,
            CreatedByUserId = TestUserId,
            TargetUserId = targetUserId,
            MeetingId = meetingId,
            ReminderAtUtc = reminderAtUtc,
            Status = status
        };

        db.Reminders.Add(reminder);
        await db.SaveChangesAsync();
        return reminder.Id;
    }
}
