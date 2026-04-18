using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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

public class AddParticipantTests : IntegrationTestBase
{
    public AddParticipantTests(MeetingAssistantWebFactory factory) : base(factory)
    {
    }

    private async Task<ApplicationUser> SeedUserAndMembershipAsync(Guid userId, Guid orgId, bool isEnabled = true)
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
            OrgRole = OrganizationRole.Member
        };

        if (!await db.Users.AnyAsync(u => u.Id == userId))
            db.Users.Add(user);

        if (!await db.UserOrgMemberships.IgnoreQueryFilters().AnyAsync(m => m.UserId == userId && m.OrganizationId == orgId))
            db.UserOrgMemberships.Add(membership);

        var orgExists = await db.Organizations.IgnoreQueryFilters().AnyAsync(o => o.Id == orgId);
        if (!orgExists)
        {
            db.Organizations.Add(new Organization { Id = orgId, Name = "Test Org" });
        }

        await db.SaveChangesAsync();
        return user;
    }

    private async Task<Meeting> SeedMeetingAsync(Guid orgId, MeetingStatus status = MeetingStatus.Scheduled)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var meeting = new Meeting
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            Title = "Integration Test Meeting",
            ScheduledStartUtc = DateTime.UtcNow.AddDays(1),
            ScheduledEndUtc = DateTime.UtcNow.AddDays(1).AddHours(1),
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

        var participant = new MeetingParticipant
        {
            Id = Guid.NewGuid(),
            MeetingId = meetingId,
            OrganizationId = orgId,
            UserId = userId,
            MeetingRole = role
        };

        db.MeetingParticipants.Add(participant);
        await db.SaveChangesAsync();
    }

    private HttpClient CreateAuthenticatedClient(Guid userId, Guid orgId)
    {
        var client = Factory.CreateClient();
        var token = TestJwtTokenHelper.GenerateToken(userId, orgId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task AddParticipant_HostAddsValidUser_ShouldSucceed()
    {
        // Arrange
        // TestUserId and TestOrganizationId are already seeded in IntegrationTestBase
        var targetUserId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(targetUserId, TestOrganizationId);

        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);

        var request = new AddParticipantRequest(targetUserId, MeetingRole.Participant);

        // Act
        var response = await Client.PostAsJsonAsync($"/api/meetings/{meeting.Id}/participants", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ParticipantResponse>();
        result.Should().NotBeNull();
        result!.UserId.Should().Be(targetUserId);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var dbParticipant = await db.MeetingParticipants.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.MeetingId == meeting.Id && p.UserId == targetUserId);
        dbParticipant.Should().NotBeNull();
        dbParticipant!.MeetingRole.Should().Be(MeetingRole.Participant);
    }

    [Fact]
    public async Task AddParticipant_CoHostAddsValidUser_ShouldSucceed()
    {
        // Arrange
        var targetUserId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(targetUserId, TestOrganizationId);

        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.CoHost);

        var request = new AddParticipantRequest(targetUserId, MeetingRole.Observer);

        // Act
        var response = await Client.PostAsJsonAsync($"/api/meetings/{meeting.Id}/participants", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var dbParticipant = await db.MeetingParticipants.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.MeetingId == meeting.Id && p.UserId == targetUserId);
        dbParticipant.Should().NotBeNull();
        dbParticipant!.MeetingRole.Should().Be(MeetingRole.Observer);
    }

    [Fact]
    public async Task AddParticipant_ByRegularParticipant_ShouldReturnForbidden()
    {
        // Arrange
        var targetUserId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(targetUserId, TestOrganizationId);

        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Participant);

        var request = new AddParticipantRequest(targetUserId, MeetingRole.Participant);

        // Act
        var response = await Client.PostAsJsonAsync($"/api/meetings/{meeting.Id}/participants", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.Forbidden);
        error.Title.Should().Be("Caller is not a host of the meeting.");
    }

    [Fact]
    public async Task AddParticipant_CallerNotPartOfMeeting_ShouldReturnForbidden()
    {
        // Arrange
        var targetUserId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(targetUserId, TestOrganizationId);

        var meeting = await SeedMeetingAsync(TestOrganizationId);
        // TestUserId NOT added to MeetingParticipants

        var request = new AddParticipantRequest(targetUserId, MeetingRole.Participant);

        // Act
        var response = await Client.PostAsJsonAsync($"/api/meetings/{meeting.Id}/participants", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.Forbidden);
        error.Title.Should().Be("You are not a participant of this meeting.");
    }

    [Fact]
    public async Task AddParticipant_UserAlreadyParticipant_ShouldReturnConflict()
    {
        // Arrange
        var targetUserId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(targetUserId, TestOrganizationId);

        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, targetUserId, MeetingRole.Participant);

        var request = new AddParticipantRequest(targetUserId, MeetingRole.Participant);

        // Act
        var response = await Client.PostAsJsonAsync($"/api/meetings/{meeting.Id}/participants", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.Conflict);
        error.Title.Should().Be("User is already a participant of this meeting.");
    }

    [Fact]
    public async Task AddParticipant_MeetingDoesNotExist_ShouldReturnNotFound()
    {
        // Arrange
        var targetUserId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(targetUserId, TestOrganizationId);

        var nonExistingMeetingId = Guid.NewGuid();
        var request = new AddParticipantRequest(targetUserId, MeetingRole.Participant);

        // Act
        var response = await Client.PostAsJsonAsync($"/api/meetings/{nonExistingMeetingId}/participants", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.NotFound);
        error.Title.Should().Be("The specified meeting was not found.");
    }

    [Fact]
    public async Task AddParticipant_TargetUserNotOrgMember_ShouldReturnForbidden()
    {
        // Arrange
        var targetUserId = Guid.NewGuid(); // User exists, but NO membership

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Users.Add(new ApplicationUser
            {
                Id = targetUserId,
                Email = $"test-{targetUserId}@test.com",
                UserName = $"test-{targetUserId}@test.com"
            });
            await db.SaveChangesAsync();
        }

        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);

        var request = new AddParticipantRequest(targetUserId, MeetingRole.Participant);

        // Act
        var response = await Client.PostAsJsonAsync($"/api/meetings/{meeting.Id}/participants", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.Forbidden);
        error.Title.Should().Be("User is not a member of the organization.");
    }

    [Fact]
    public async Task AddParticipant_TargetUserDoesNotExist_ShouldReturnForbidden()
    {
        // Arrange
        var missingUserId = Guid.NewGuid(); // Not seeded at all

        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);

        var request = new AddParticipantRequest(missingUserId, MeetingRole.Participant);

        // Act
        var response = await Client.PostAsJsonAsync($"/api/meetings/{meeting.Id}/participants", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.Forbidden);
        error.Title.Should().Be("User is not a member of the organization.");
    }

    [Fact]
    public async Task AddParticipant_TargetUserInDifferentOrganization_ShouldReturnForbidden()
    {
        // Arrange
        var otherOrgId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(targetUserId, otherOrgId);

        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);

        var request = new AddParticipantRequest(targetUserId, MeetingRole.Participant);

        // Act
        var response = await Client.PostAsJsonAsync($"/api/meetings/{meeting.Id}/participants", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.Forbidden);
        error.Title.Should().Be("User is not a member of the organization.");
    }

    [Fact]
    public async Task AddParticipant_Unauthenticated_ShouldReturnUnauthorized()
    {
        // Arrange
        var meetingId = Guid.NewGuid();
        var request = new AddParticipantRequest(Guid.NewGuid(), MeetingRole.Participant);

        var unauthenticatedClient = Factory.CreateClient();
        unauthenticatedClient.DefaultRequestHeaders.Authorization = null;

        // Act
        var response = await unauthenticatedClient.PostAsJsonAsync($"/api/meetings/{meetingId}/participants", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AddParticipant_OrgIdMismatch_ShouldReturnNotFoundOrForbidden()
    {
        // Arrange
        // We create a completely different organization, user, and membership A
        var customUserId = Guid.NewGuid();
        var customOrgId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(customUserId, customOrgId);
        var alternateClient = CreateAuthenticatedClient(customUserId, customOrgId);

        // A Meeting is created under TestOrganizationId (Org B)
        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);

        var request = new AddParticipantRequest(Guid.NewGuid(), MeetingRole.Participant);

        // Act
        // Caller acts on a meeting belonging to TestOrganizationId while their token's OrgId is customOrgId
        var response = await alternateClient.PostAsJsonAsync($"/api/meetings/{meeting.Id}/participants", request);

        // Assert
        // In EF Global Query Filters, cross-tenant lookups usually return 404 NotFound
        // If it leaks through the query filter, ParticipantService returns 403.
        response.StatusCode.Should().Match(s => s == HttpStatusCode.NotFound || s == HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AddParticipant_InvalidRequestBody_ShouldReturnBadRequest()
    {
        // Arrange
        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);

        // Invalid request with Empty Guid for UserId
        var request = new AddParticipantRequest(Guid.Empty, MeetingRole.Participant);

        // Act
        var response = await Client.PostAsJsonAsync($"/api/meetings/{meeting.Id}/participants", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.BadRequest);
        error.Errors.Should().NotBeNull();
        // Validation check on the AddParticipantRequest usually targets the "UserId" property
        error.Errors.Should().ContainKey("UserId");
    }

    [Fact]
    public async Task AddParticipant_TargetUserAlreadyParticipantWithDifferentRole_ShouldReturnConflict()
    {
        // Arrange
        var targetUserId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(targetUserId, TestOrganizationId);

        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);
        
        // Target is already an Observer
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, targetUserId, MeetingRole.Observer);

        // Try to add as Participant (Different role)
        var request = new AddParticipantRequest(targetUserId, MeetingRole.Participant);

        // Act
        var response = await Client.PostAsJsonAsync($"/api/meetings/{meeting.Id}/participants", request);

        // Assert
        // Verified logic: check.AlreadyParticipant returns Conflict regardless of whether the role is different
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.Conflict);
        error.Title.Should().Be("User is already a participant of this meeting.");
        
        // Assert Database State didn't change the role
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var dbParticipant = await db.MeetingParticipants.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.MeetingId == meeting.Id && p.UserId == targetUserId);
        dbParticipant.Should().NotBeNull();
        dbParticipant!.MeetingRole.Should().Be(MeetingRole.Observer); // Remains unchanged
    }
}

