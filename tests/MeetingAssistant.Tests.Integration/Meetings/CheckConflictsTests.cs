using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using MeetingAssistant.Api.Shared;
using MeetingAssistant.Features.Identity.Entites;
using MeetingAssistant.Features.Meetings.Contracts.Requests;
using MeetingAssistant.Features.Meetings.Contracts.Responses;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeetingAssistant.Tests.Integration.Meetings;

public class CheckConflictsTests : IntegrationTestBase
{
    public CheckConflictsTests(MeetingAssistantWebFactory factory) : base(factory)
    {
    }

    private HttpClient CreateAuthenticatedClient(Guid userId, Guid orgId)
    {
        var client = Factory.CreateClient();
        var token = TestJwtTokenHelper.GenerateToken(userId, orgId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<ApplicationUser> SeedUserAndMembershipAsync(Guid userId, Guid orgId, bool isEnabled = true, OrganizationRole orgRole = OrganizationRole.Member)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var user = new ApplicationUser
        {
            Id = userId,
            Email = $"test-{userId}@test.com",
            UserName = $"test-{userId}@test.com"
        };

        var membership = new UserOrgMembership
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            OrganizationId = orgId,
            IsEnabled = isEnabled,
            OrgRole = orgRole
        };

        if (!await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Id == userId))
            db.Users.Add(user);

        var orgExists = await db.Organizations.IgnoreQueryFilters().AnyAsync(o => o.Id == orgId);
        if (!orgExists)
        {
            db.Organizations.Add(new Organization { Id = orgId, Name = "Test Org" });
        }

        if (!await db.UserOrgMemberships.IgnoreQueryFilters().AnyAsync(m => m.UserId == userId && m.OrganizationId == orgId))
            db.UserOrgMemberships.Add(membership);

        await db.SaveChangesAsync();
        return user;
    }

    private async Task<Meeting> SeedMeetingAsync(
        Guid orgId,
        MeetingStatus status = MeetingStatus.Scheduled,
        string title = "Integration Test Meeting",
        DateTime? scheduledStart = null,
        DateTime? scheduledEnd = null)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var orgExists = await db.Organizations.IgnoreQueryFilters().AnyAsync(o => o.Id == orgId);
        if (!orgExists)
        {
            db.Organizations.Add(new Organization { Id = orgId, Name = "Test Org" });
            await db.SaveChangesAsync();
        }

        var start = scheduledStart ?? DateTime.UtcNow.AddDays(1);
        var end = scheduledEnd ?? start.AddHours(1);

        var meeting = new Meeting
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            Title = title,
            ScheduledStartUtc = start,
            ScheduledEndUtc = end,
            Status = status
        };

        db.Meetings.Add(meeting);
        await db.SaveChangesAsync();
        return meeting;
    }

    private async Task SeedMeetingParticipantAsync(Guid meetingId, Guid orgId, Guid userId, MeetingRole role)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        if (!await db.MeetingParticipants.IgnoreQueryFilters().AnyAsync(p => p.MeetingId == meetingId && p.UserId == userId))
        {
            db.MeetingParticipants.Add(new MeetingParticipant
            {
                Id = Guid.NewGuid(),
                MeetingId = meetingId,
                OrganizationId = orgId,
                UserId = userId,
                MeetingRole = role
            });
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task CheckSchedulingConflicts_OverlappingHostMeetingExists_ShouldReturnConflict()
    {
        // Arrange
        var start = DateTime.UtcNow.AddDays(1).Date.AddHours(10);
        var existingMeeting = await SeedMeetingAsync(
            TestOrganizationId,
            scheduledStart: start,
            scheduledEnd: start.AddHours(1),
            title: "Existing Host Meeting");
        await SeedMeetingParticipantAsync(existingMeeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);

        var request = new MeetingConflictCheckRequest(
            start.AddMinutes(15),
            start.AddMinutes(45),
            new List<Guid>(),
            null);

        // Act
        var response = await Client.PostAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings/conflict-check", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ConflictCheckResponse>();
        result.Should().NotBeNull();
        result!.Conflicts.Should().ContainSingle(c => c.UserId == TestUserId);
        result.Conflicts.Single().ConflictingMeetings.Should().ContainSingle(m => m.Id == existingMeeting.Id);
    }

    [Fact]
    public async Task CheckSchedulingConflicts_SelectedParticipantHasOverlap_ShouldReturnConflict()
    {
        // Arrange
        var participantId = Guid.NewGuid();
        var otherHostId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(participantId, TestOrganizationId);
        await SeedUserAndMembershipAsync(otherHostId, TestOrganizationId);

        var start = DateTime.UtcNow.AddDays(1).Date.AddHours(10);
        var existingMeeting = await SeedMeetingAsync(
            TestOrganizationId,
            scheduledStart: start,
            scheduledEnd: start.AddHours(1),
            title: "Participant Conflict Meeting");
        await SeedMeetingParticipantAsync(existingMeeting.Id, TestOrganizationId, otherHostId, MeetingRole.Host);
        await SeedMeetingParticipantAsync(existingMeeting.Id, TestOrganizationId, participantId, MeetingRole.Participant);

        var request = new MeetingConflictCheckRequest(
            start.AddMinutes(15),
            start.AddMinutes(45),
            new List<Guid> { participantId },
            null);

        // Act
        var response = await Client.PostAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings/conflict-check", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ConflictCheckResponse>();
        result.Should().NotBeNull();
        result!.Conflicts.Should().ContainSingle(c => c.UserId == participantId);
        result.Conflicts.Single().ConflictingMeetings.Should().ContainSingle(m => m.Id == existingMeeting.Id);
    }

    [Fact]
    public async Task CheckSchedulingConflicts_ExcludedMeetingUsesCurrentParticipants_ShouldReturnEmptyForSelfOverlap()
    {
        // Arrange
        var start = DateTime.UtcNow.AddDays(1).Date.AddHours(10);
        var currentMeeting = await SeedMeetingAsync(
            TestOrganizationId,
            scheduledStart: start,
            scheduledEnd: start.AddHours(1),
            title: "Current Meeting");
        await SeedMeetingParticipantAsync(currentMeeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);

        var request = new MeetingConflictCheckRequest(
            start.AddMinutes(15),
            start.AddMinutes(45),
            null,
            currentMeeting.Id);

        // Act
        var response = await Client.PostAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings/conflict-check", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ConflictCheckResponse>();
        result.Should().NotBeNull();
        result!.Conflicts.Should().BeEmpty();
    }

    [Fact]
    public async Task CheckConflicts_OverlappingMeetingsExist_ShouldReturnConflicts()
    {
        // Arrange
        var participantId = Guid.NewGuid();
        var otherHostId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(participantId, TestOrganizationId);
        await SeedUserAndMembershipAsync(otherHostId, TestOrganizationId);

        var currentStart = DateTime.UtcNow.AddDays(1).Date.AddHours(10);
        var currentEnd = currentStart.AddHours(1);
        var currentMeeting = await SeedMeetingAsync(TestOrganizationId, scheduledStart: currentStart, scheduledEnd: currentEnd);

        var otherStart = currentStart.AddMinutes(30);
        var otherEnd = otherStart.AddHours(1);
        var otherMeeting = await SeedMeetingAsync(TestOrganizationId, scheduledStart: otherStart, scheduledEnd: otherEnd, title: "Overlapping Meeting");

        await SeedMeetingParticipantAsync(currentMeeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);
        await SeedMeetingParticipantAsync(currentMeeting.Id, TestOrganizationId, participantId, MeetingRole.Participant);
        await SeedMeetingParticipantAsync(otherMeeting.Id, TestOrganizationId, otherHostId, MeetingRole.Host);
        await SeedMeetingParticipantAsync(otherMeeting.Id, TestOrganizationId, participantId, MeetingRole.Participant);

        // Act
        var response = await Client.GetAsync($"/api/meetings/{currentMeeting.Id}/participants/conflicts");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ConflictCheckResponse>();
        result.Should().NotBeNull();
        result!.Conflicts.Should().ContainSingle(c => c.UserId == participantId);
        var conflict = result.Conflicts.Single(c => c.UserId == participantId);
        conflict.ConflictingMeetings.Should().ContainSingle(m => m.Id == otherMeeting.Id);
        conflict.ConflictingMeetings.Should().NotContain(m => m.Id == currentMeeting.Id);
    }

    [Fact]
    public async Task CheckConflicts_NoOverlaps_ShouldReturnEmptyConflicts()
    {
        // Arrange
        var participantId = Guid.NewGuid();
        var otherHostId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(participantId, TestOrganizationId);
        await SeedUserAndMembershipAsync(otherHostId, TestOrganizationId);

        var currentStart = DateTime.UtcNow.AddDays(1).Date.AddHours(10);
        var currentMeeting = await SeedMeetingAsync(TestOrganizationId, scheduledStart: currentStart, scheduledEnd: currentStart.AddHours(1));
        var otherMeeting = await SeedMeetingAsync(TestOrganizationId, scheduledStart: currentStart.AddHours(3), scheduledEnd: currentStart.AddHours(4));

        await SeedMeetingParticipantAsync(currentMeeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);
        await SeedMeetingParticipantAsync(currentMeeting.Id, TestOrganizationId, participantId, MeetingRole.Participant);
        await SeedMeetingParticipantAsync(otherMeeting.Id, TestOrganizationId, otherHostId, MeetingRole.Host);
        await SeedMeetingParticipantAsync(otherMeeting.Id, TestOrganizationId, participantId, MeetingRole.Participant);

        // Act
        var response = await Client.GetAsync($"/api/meetings/{currentMeeting.Id}/participants/conflicts");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ConflictCheckResponse>();
        result.Should().NotBeNull();
        result!.Conflicts.Should().BeEmpty();
    }

    [Fact]
    public async Task CheckConflicts_ExcludedStatusesOnly_ShouldReturnEmptyConflicts()
    {
        // Arrange
        var participantId = Guid.NewGuid();
        var otherHostId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(participantId, TestOrganizationId);
        await SeedUserAndMembershipAsync(otherHostId, TestOrganizationId);

        var start = DateTime.UtcNow.AddDays(1).Date.AddHours(10);
        var currentMeeting = await SeedMeetingAsync(TestOrganizationId, scheduledStart: start, scheduledEnd: start.AddHours(1));

        var cancelled = await SeedMeetingAsync(TestOrganizationId, MeetingStatus.Cancelled, "Cancelled", start.AddMinutes(10), start.AddHours(1));
        var completed = await SeedMeetingAsync(TestOrganizationId, MeetingStatus.Completed, "Completed", start.AddMinutes(10), start.AddHours(1));
        var failed = await SeedMeetingAsync(TestOrganizationId, MeetingStatus.Failed, "Failed", start.AddMinutes(10), start.AddHours(1));

        await SeedMeetingParticipantAsync(currentMeeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);
        await SeedMeetingParticipantAsync(currentMeeting.Id, TestOrganizationId, participantId, MeetingRole.Participant);

        await SeedMeetingParticipantAsync(cancelled.Id, TestOrganizationId, otherHostId, MeetingRole.Host);
        await SeedMeetingParticipantAsync(cancelled.Id, TestOrganizationId, participantId, MeetingRole.Participant);

        await SeedMeetingParticipantAsync(completed.Id, TestOrganizationId, otherHostId, MeetingRole.Host);
        await SeedMeetingParticipantAsync(completed.Id, TestOrganizationId, participantId, MeetingRole.Participant);

        await SeedMeetingParticipantAsync(failed.Id, TestOrganizationId, otherHostId, MeetingRole.Host);
        await SeedMeetingParticipantAsync(failed.Id, TestOrganizationId, participantId, MeetingRole.Participant);

        // Act
        var response = await Client.GetAsync($"/api/meetings/{currentMeeting.Id}/participants/conflicts");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ConflictCheckResponse>();
        result.Should().NotBeNull();
        result!.Conflicts.Should().BeEmpty();
    }

    [Fact]
    public async Task CheckConflicts_CoHostCaller_ShouldReturnOk()
    {
        // Arrange
        var coHostId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var otherHostId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(coHostId, TestOrganizationId);
        await SeedUserAndMembershipAsync(participantId, TestOrganizationId);
        await SeedUserAndMembershipAsync(otherHostId, TestOrganizationId);

        var coHostClient = CreateAuthenticatedClient(coHostId, TestOrganizationId);

        var start = DateTime.UtcNow.AddDays(1).Date.AddHours(10);
        var currentMeeting = await SeedMeetingAsync(TestOrganizationId, scheduledStart: start, scheduledEnd: start.AddHours(1));
        var overlapMeeting = await SeedMeetingAsync(TestOrganizationId, scheduledStart: start.AddMinutes(20), scheduledEnd: start.AddHours(1).AddMinutes(20));

        await SeedMeetingParticipantAsync(currentMeeting.Id, TestOrganizationId, coHostId, MeetingRole.CoHost);
        await SeedMeetingParticipantAsync(currentMeeting.Id, TestOrganizationId, participantId, MeetingRole.Participant);
        await SeedMeetingParticipantAsync(overlapMeeting.Id, TestOrganizationId, otherHostId, MeetingRole.Host);
        await SeedMeetingParticipantAsync(overlapMeeting.Id, TestOrganizationId, participantId, MeetingRole.Participant);

        // Act
        var response = await coHostClient.GetAsync($"/api/meetings/{currentMeeting.Id}/participants/conflicts");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ConflictCheckResponse>();
        result.Should().NotBeNull();
        result!.Conflicts.Should().ContainSingle(c => c.UserId == participantId);
    }

    [Fact]
    public async Task CheckConflicts_RegularParticipantCaller_ShouldReturnForbidden()
    {
        // Arrange
        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Participant);

        // Act
        var response = await Client.GetAsync($"/api/meetings/{meeting.Id}/participants/conflicts");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.Forbidden);
        error.Title.Should().Be("Caller is not a host of the meeting.");
    }

    [Fact]
    public async Task CheckConflicts_CallerNotParticipant_ShouldReturnForbidden()
    {
        // Arrange
        var otherUserId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(otherUserId, TestOrganizationId);

        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, otherUserId, MeetingRole.Host);

        // Act
        var response = await Client.GetAsync($"/api/meetings/{meeting.Id}/participants/conflicts");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.Forbidden);
        error.Title.Should().Be("You are not a participant of this meeting.");
    }

    [Fact]
    public async Task CheckConflicts_MeetingDoesNotExist_ShouldReturnNotFound()
    {
        // Arrange
        var meetingId = Guid.NewGuid();

        // Act
        var response = await Client.GetAsync($"/api/meetings/{meetingId}/participants/conflicts");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.NotFound);
        error.Title.Should().Be("The specified meeting was not found.");
    }

    [Fact]
    public async Task CheckConflicts_Unauthenticated_ShouldReturnUnauthorized()
    {
        // Arrange
        var meetingId = Guid.NewGuid();
        var unauthenticatedClient = Factory.CreateClient();

        // Act
        var response = await unauthenticatedClient.GetAsync($"/api/meetings/{meetingId}/participants/conflicts");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CheckConflicts_MeetingFromDifferentOrganization_ShouldReturnNotFound()
    {
        // Arrange
        var otherOrgId = Guid.NewGuid();
        var otherOrgHostId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(otherOrgHostId, otherOrgId);

        var meeting = await SeedMeetingAsync(otherOrgId);
        await SeedMeetingParticipantAsync(meeting.Id, otherOrgId, otherOrgHostId, MeetingRole.Host);

        // Act
        var response = await Client.GetAsync($"/api/meetings/{meeting.Id}/participants/conflicts");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.NotFound);
        error.Title.Should().Be("The specified meeting was not found.");
    }
}
