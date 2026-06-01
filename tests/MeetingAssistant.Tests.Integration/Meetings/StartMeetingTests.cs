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

public class StartMeetingTests : IntegrationTestBase
{
    public StartMeetingTests(MeetingAssistantWebFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task StartMeeting_HostStartsScheduledMeeting_ShouldReturnOkSetInProgressAndActivationTime()
    {
        await SetMembershipRoleAsync(TestUserId, TestOrganizationId, OrganizationRole.Member);
        var meetingId = await SeedMeetingAsync(MeetingStatus.Scheduled, TestUserId, MeetingRole.Host);

        var response = await Client.PostAsync($"/api/organizations/{TestOrganizationId}/meetings/{meetingId}/start", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<MeetingResponse>();
        body!.Id.Should().Be(meetingId);
        body.Status.Should().Be(MeetingStatus.InProgress);

        var meeting = await LoadMeetingAsync(meetingId);
        meeting.Status.Should().Be(MeetingStatus.InProgress);
        meeting.RoomActivatedAtUtc.Should().NotBeNull();
        meeting.RoomActivatedAtUtc!.Value.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task StartMeeting_CoHostStartsScheduledMeeting_ShouldReturnOk()
    {
        await SetMembershipRoleAsync(TestUserId, TestOrganizationId, OrganizationRole.Member);
        var meetingId = await SeedMeetingAsync(MeetingStatus.Scheduled, TestUserId, MeetingRole.CoHost);

        var response = await Client.PostAsync($"/api/organizations/{TestOrganizationId}/meetings/{meetingId}/start", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<MeetingResponse>();
        body!.Status.Should().Be(MeetingStatus.InProgress);
    }

    [Fact]
    public async Task StartMeeting_OrgAdminNotParticipant_ShouldReturnOk()
    {
        var meetingId = await SeedMeetingAsync(MeetingStatus.Scheduled);

        var response = await Client.PostAsync($"/api/organizations/{TestOrganizationId}/meetings/{meetingId}/start", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<MeetingResponse>();
        body!.Status.Should().Be(MeetingStatus.InProgress);
    }

    [Fact]
    public async Task StartMeeting_ParticipantNotHostCoHostOrAdmin_ShouldReturnForbidden()
    {
        await SetMembershipRoleAsync(TestUserId, TestOrganizationId, OrganizationRole.Member);
        var meetingId = await SeedMeetingAsync(MeetingStatus.Scheduled, TestUserId, MeetingRole.Participant);

        var response = await Client.PostAsync($"/api/organizations/{TestOrganizationId}/meetings/{meetingId}/start", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error!.Type.Should().Be("Meetings.LifecycleControlForbidden");
    }

    [Fact]
    public async Task StartMeeting_AlreadyInProgress_ShouldReturnCurrentStateAndNotMoveActivationTime()
    {
        await SetMembershipRoleAsync(TestUserId, TestOrganizationId, OrganizationRole.Member);
        var activatedAt = DateTime.UtcNow.AddMinutes(-15);
        var meetingId = await SeedMeetingAsync(MeetingStatus.InProgress, TestUserId, MeetingRole.Host, activatedAt);

        var response = await Client.PostAsync($"/api/organizations/{TestOrganizationId}/meetings/{meetingId}/start", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<MeetingResponse>();
        body!.Status.Should().Be(MeetingStatus.InProgress);

        var meeting = await LoadMeetingAsync(meetingId);
        meeting.RoomActivatedAtUtc.Should().Be(activatedAt);
    }

    [Theory]
    [InlineData(MeetingStatus.Completed)]
    [InlineData(MeetingStatus.Cancelled)]
    [InlineData(MeetingStatus.Failed)]
    public async Task StartMeeting_ClosedMeeting_ShouldReturnConflict(MeetingStatus status)
    {
        var meetingId = await SeedMeetingAsync(status);

        var response = await Client.PostAsync($"/api/organizations/{TestOrganizationId}/meetings/{meetingId}/start", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error!.Type.Should().Be("Meetings.InvalidLifecycleTransition");
    }

    [Fact]
    public async Task StartMeeting_RouteOrgIdDiffersFromJwtOrgId_ShouldReturnForbidden()
    {
        var response = await Client.PostAsync($"/api/organizations/{Guid.NewGuid()}/meetings/{Guid.NewGuid()}/start", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private async Task<Guid> SeedMeetingAsync(
        MeetingStatus status,
        Guid? participantUserId = null,
        MeetingRole? participantRole = null,
        DateTime? roomActivatedAtUtc = null)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var start = DateTime.UtcNow.AddMinutes(30);
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
