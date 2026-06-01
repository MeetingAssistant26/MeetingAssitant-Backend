using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using MeetingAssistant.Api.Shared;
using MeetingAssistant.Features.Meetings.Contracts.Responses;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeetingAssistant.Tests.Integration.Meetings;

public class EndMeetingTests : IntegrationTestBase
{
    public EndMeetingTests(MeetingAssistantWebFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task EndMeeting_HostEndsInProgressMeeting_ShouldReturnOkAndSetCompleted()
    {
        await SetMembershipRoleAsync(TestUserId, TestOrganizationId, OrganizationRole.Member);
        var activatedAt = DateTime.UtcNow.AddMinutes(-20);
        var meetingId = await SeedMeetingAsync(MeetingStatus.InProgress, TestUserId, MeetingRole.Host, activatedAt);

        var response = await Client.PostAsync($"/api/organizations/{TestOrganizationId}/meetings/{meetingId}/end", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<MeetingResponse>();
        body!.Status.Should().Be(MeetingStatus.Completed);

        var meeting = await LoadMeetingAsync(meetingId);
        meeting.Status.Should().Be(MeetingStatus.Completed);
        meeting.RoomActivatedAtUtc.Should().Be(activatedAt);
    }

    [Fact]
    public async Task EndMeeting_CoHostEndsInProgressMeeting_ShouldReturnOk()
    {
        await SetMembershipRoleAsync(TestUserId, TestOrganizationId, OrganizationRole.Member);
        var meetingId = await SeedMeetingAsync(MeetingStatus.InProgress, TestUserId, MeetingRole.CoHost, DateTime.UtcNow.AddMinutes(-5));

        var response = await Client.PostAsync($"/api/organizations/{TestOrganizationId}/meetings/{meetingId}/end", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<MeetingResponse>();
        body!.Status.Should().Be(MeetingStatus.Completed);
    }

    [Fact]
    public async Task EndMeeting_OrgAdminNotParticipant_ShouldReturnOk()
    {
        var meetingId = await SeedMeetingAsync(MeetingStatus.InProgress, roomActivatedAtUtc: DateTime.UtcNow.AddMinutes(-5));

        var response = await Client.PostAsync($"/api/organizations/{TestOrganizationId}/meetings/{meetingId}/end", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<MeetingResponse>();
        body!.Status.Should().Be(MeetingStatus.Completed);
    }

    [Fact]
    public async Task EndMeeting_ParticipantNotHostCoHostOrAdmin_ShouldReturnForbidden()
    {
        await SetMembershipRoleAsync(TestUserId, TestOrganizationId, OrganizationRole.Member);
        var meetingId = await SeedMeetingAsync(MeetingStatus.InProgress, TestUserId, MeetingRole.Participant, DateTime.UtcNow.AddMinutes(-5));

        var response = await Client.PostAsync($"/api/organizations/{TestOrganizationId}/meetings/{meetingId}/end", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error!.Type.Should().Be("Meetings.LifecycleControlForbidden");
    }

    [Fact]
    public async Task EndMeeting_AlreadyCompleted_ShouldReturnCurrentState()
    {
        var meetingId = await SeedMeetingAsync(MeetingStatus.Completed, roomActivatedAtUtc: DateTime.UtcNow.AddMinutes(-30));

        var response = await Client.PostAsync($"/api/organizations/{TestOrganizationId}/meetings/{meetingId}/end", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<MeetingResponse>();
        body!.Status.Should().Be(MeetingStatus.Completed);
    }

    [Theory]
    [InlineData(MeetingStatus.Scheduled)]
    [InlineData(MeetingStatus.Cancelled)]
    [InlineData(MeetingStatus.Failed)]
    public async Task EndMeeting_NotActiveMeeting_ShouldReturnConflict(MeetingStatus status)
    {
        var meetingId = await SeedMeetingAsync(status);

        var response = await Client.PostAsync($"/api/organizations/{TestOrganizationId}/meetings/{meetingId}/end", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error!.Type.Should().Be("Meetings.InvalidLifecycleTransition");
    }

    [Fact]
    public async Task EndMeeting_MissingMeeting_ShouldReturnNotFound()
    {
        var response = await Client.PostAsync($"/api/organizations/{TestOrganizationId}/meetings/{Guid.NewGuid()}/end", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private async Task<Guid> SeedMeetingAsync(
        MeetingStatus status,
        Guid? participantUserId = null,
        MeetingRole? participantRole = null,
        DateTime? roomActivatedAtUtc = null)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var start = DateTime.UtcNow.AddMinutes(-30);
        var meeting = new Meeting
        {
            Id = Guid.NewGuid(),
            OrganizationId = TestOrganizationId,
            Title = "Lifecycle meeting",
            ScheduledStartUtc = start,
            ScheduledEndUtc = start.AddHours(1),
            Status = status,
            RoomActivatedAtUtc = roomActivatedAtUtc
        };

        db.Meetings.Add(meeting);

        if (participantUserId.HasValue && participantRole.HasValue)
        {
            db.MeetingParticipants.Add(new MeetingParticipant
            {
                MeetingId = meeting.Id,
                OrganizationId = TestOrganizationId,
                UserId = participantUserId.Value,
                MeetingRole = participantRole.Value
            });
        }

        await db.SaveChangesAsync();
        return meeting.Id;
    }

    private async Task SetMembershipRoleAsync(Guid userId, Guid orgId, OrganizationRole role)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var membership = await db.UserOrgMemberships.IgnoreQueryFilters()
            .FirstAsync(m => m.UserId == userId && m.OrganizationId == orgId);
        membership.OrgRole = role;
        await db.SaveChangesAsync();
    }

    private async Task<Meeting> LoadMeetingAsync(Guid meetingId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Meetings.IgnoreQueryFilters().SingleAsync(m => m.Id == meetingId);
    }
}
