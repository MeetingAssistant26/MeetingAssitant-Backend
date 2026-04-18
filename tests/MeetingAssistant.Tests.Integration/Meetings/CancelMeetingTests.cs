using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MeetingAssistant.Features.Identity.Entites;
using MeetingAssistant.Features.Meetings.Contracts.Requests;
using MeetingAssistant.Features.Meetings.Contracts.Responses;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Api.Shared;
using MeetingAssistant.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeetingAssistant.Tests.Integration.Meetings;

public class CancelMeetingTests : IntegrationTestBase
{
    public CancelMeetingTests(MeetingAssistantWebFactory factory) : base(factory)
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

    private async Task<Meeting> SeedMeetingAndParticipantAsync(Guid meetingId, Guid orgId, MeetingStatus status, Guid? participantUserId = null, MeetingRole? participantRole = null)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var start = DateTime.UtcNow.AddDays(1);
        var meeting = new Meeting
        {
            Id = meetingId,
            OrganizationId = orgId,
            Title = "Test Meeting",
            ScheduledStartUtc = start,
            ScheduledEndUtc = start.AddHours(1),
            Status = status
        };

        db.Meetings.Add(meeting);

        if (participantUserId.HasValue && participantRole.HasValue)
        {
            db.MeetingParticipants.Add(new MeetingParticipant
            {
                MeetingId = meetingId,
                OrganizationId = orgId,
                UserId = participantUserId.Value,
                MeetingRole = participantRole.Value
            });
        }

        await db.SaveChangesAsync();
        return meeting;
    }

    [Fact]
    public async Task CancelMeeting_HostCancelsScheduledMeeting_ShouldReturnOkAndSetStatus()
    {
        // Arrange
        var meetingId = Guid.NewGuid();
        await SeedMeetingAndParticipantAsync(meetingId, TestOrganizationId, MeetingStatus.Scheduled, TestUserId, MeetingRole.Host);

        // Act
        var response = await Client.DeleteAsync($"/api/organizations/{TestOrganizationId}/meetings/{meetingId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("status").GetString().Should().Be("Cancelled");
        json.GetProperty("id").GetGuid().Should().Be(meetingId);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var dbMeeting = await db.Meetings.IgnoreQueryFilters().FirstOrDefaultAsync(m => m.Id == meetingId);
        dbMeeting.Should().NotBeNull();
        dbMeeting!.Status.Should().Be(MeetingStatus.Cancelled);
    }

    [Fact]
    public async Task CancelMeeting_Unauthenticated_ShouldReturnUnauthorized()
    {
        // Arrange
        var meetingId = Guid.NewGuid();
        var unauthClient = Factory.CreateClient();

        // Act
        var response = await unauthClient.DeleteAsync($"/api/organizations/{TestOrganizationId}/meetings/{meetingId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CancelMeeting_RouteOrgIdDiffersFromJwtOrgId_ShouldReturnForbidden()
    {
        // Arrange
        var meetingId = Guid.NewGuid();
        var otherOrgId = Guid.NewGuid();

        // Act
        var response = await Client.DeleteAsync($"/api/organizations/{otherOrgId}/meetings/{meetingId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CancelMeeting_CoHostTriesToCancel_ShouldReturnForbidden()
    {
        // Arrange
        var meetingId = Guid.NewGuid();
        await SeedMeetingAndParticipantAsync(meetingId, TestOrganizationId, MeetingStatus.Scheduled, TestUserId, MeetingRole.CoHost);

        // Act
        var response = await Client.DeleteAsync($"/api/organizations/{TestOrganizationId}/meetings/{meetingId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.Forbidden);
        error.Title.Should().Be("Caller is not a host of the meeting.");
    }

    [Fact]
    public async Task CancelMeeting_ParticipantTriesToCancel_ShouldReturnForbidden()
    {
        // Arrange
        var meetingId = Guid.NewGuid();
        await SeedMeetingAndParticipantAsync(meetingId, TestOrganizationId, MeetingStatus.Scheduled, TestUserId, MeetingRole.Participant);

        // Act
        var response = await Client.DeleteAsync($"/api/organizations/{TestOrganizationId}/meetings/{meetingId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.Forbidden);
        error.Title.Should().Be("Caller is not a host of the meeting.");
    }

    [Fact]
    public async Task CancelMeeting_CallerNotAParticipant_ShouldReturnForbidden()
    {
        // Arrange
        var meetingId = Guid.NewGuid();
        await SeedMeetingAndParticipantAsync(meetingId, TestOrganizationId, MeetingStatus.Scheduled);

        // Act
        var response = await Client.DeleteAsync($"/api/organizations/{TestOrganizationId}/meetings/{meetingId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.Forbidden);
        error.Title.Should().Be("Caller is not a host of the meeting.");
    }

    [Fact]
    public async Task CancelMeeting_MeetingDoesNotExist_ShouldReturnNotFound()
    {
        // Arrange
        var nonExistentMeetingId = Guid.NewGuid();

        // Act
        var response = await Client.DeleteAsync($"/api/organizations/{TestOrganizationId}/meetings/{nonExistentMeetingId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.NotFound);
        error.Title.Should().Be("The specified meeting was not found.");
    }

    [Fact]
    public async Task CancelMeeting_MeetingIsAlreadyCancelled_ShouldReturnBadRequest()
    {
        // Arrange
        var meetingId = Guid.NewGuid();
        await SeedMeetingAndParticipantAsync(meetingId, TestOrganizationId, MeetingStatus.Cancelled, TestUserId, MeetingRole.Host);

        // Act
        var response = await Client.DeleteAsync($"/api/organizations/{TestOrganizationId}/meetings/{meetingId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.BadRequest);
        error.Title.Should().Be("The operation is invalid for the current meeting status.");
    }

    [Fact]
    public async Task CancelMeeting_MeetingIsCompleted_ShouldReturnBadRequest()
    {
        // Arrange
        var meetingId = Guid.NewGuid();
        await SeedMeetingAndParticipantAsync(meetingId, TestOrganizationId, MeetingStatus.Completed, TestUserId, MeetingRole.Host);

        // Act
        var response = await Client.DeleteAsync($"/api/organizations/{TestOrganizationId}/meetings/{meetingId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.BadRequest);
        error.Title.Should().Be("The operation is invalid for the current meeting status.");
    }

    [Fact]
    public async Task CancelMeeting_CallerFromDifferentOrg_ShouldReturnNotFoundOrForbidden()
    {
        // Arrange
        var otherOrgId = Guid.NewGuid();
        var meetingId = Guid.NewGuid();
        await SeedMeetingAndParticipantAsync(meetingId, otherOrgId, MeetingStatus.Scheduled, TestUserId, MeetingRole.Host);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var orgExists = await db.Organizations.IgnoreQueryFilters().AnyAsync(o => o.Id == otherOrgId);
            if (!orgExists)
            {
                db.Organizations.Add(new Organization { Id = otherOrgId, Name = "Other Org" });
                await db.SaveChangesAsync();
            }
        }

        // Act
        var response = await Client.DeleteAsync($"/api/organizations/{TestOrganizationId}/meetings/{meetingId}");

        // Assert
        response.StatusCode.Should().Match(s => s == HttpStatusCode.NotFound || s == HttpStatusCode.Forbidden);
    }
}