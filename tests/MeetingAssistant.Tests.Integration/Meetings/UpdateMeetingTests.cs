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

public class UpdateMeetingTests : IntegrationTestBase
{
    public UpdateMeetingTests(MeetingAssistantWebFactory factory) : base(factory)
    {
    }

    // ───────────────────────────────────────────────────────────────
    // Seed helpers
    // ───────────────────────────────────────────────────────────────

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

    private async Task<Meeting> SeedMeetingAsync(
        Guid orgId,
        MeetingStatus status = MeetingStatus.Scheduled,
        string title = "Integration Test Meeting",
        string? description = "Test description",
        DateTime? scheduledStart = null,
        DateTime? scheduledEnd = null)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var start = scheduledStart ?? DateTime.UtcNow.AddDays(1);
        var end = scheduledEnd ?? start.AddHours(1);

        var meeting = new Meeting
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            Title = title,
            Description = description,
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

    private async Task<MeetingTag> SeedMeetingTagAsync(Guid orgId, string name = "Test Tag", bool isActive = true)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var tag = new MeetingTag
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            Name = name,
            IsActive = isActive
        };

        db.MeetingTags.Add(tag);
        await db.SaveChangesAsync();
        return tag;
    }

    private async Task SeedMeetingMeetingTagAsync(Guid meetingId, Guid tagId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var junction = new MeetingMeetingTag
        {
            MeetingId = meetingId,
            MeetingTagId = tagId
        };

        db.MeetingMeetingTags.Add(junction);
        await db.SaveChangesAsync();
    }

    private HttpClient CreateAuthenticatedClient(Guid userId, Guid orgId)
    {
        var client = Factory.CreateClient();
        var token = TestJwtTokenHelper.GenerateToken(userId, orgId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    // ───────────────────────────────────────────────────────────────
    // Success cases (partial updates)
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateMeeting_HostUpdatesTitleOnly_ShouldReturnOkWithUpdatedTitle()
    {
        // Arrange
        var meeting = await SeedMeetingAsync(TestOrganizationId, title: "Original Title", description: "Original Description");
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);

        var request = new UpdateMeetingRequest("Updated Title", null, null, null, null);

        // Act
        var response = await Client.PutAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings/{meeting.Id}", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<MeetingResponse>();
        result.Should().NotBeNull();
        result!.Title.Should().Be("Updated Title");
        result.Description.Should().Be("Original Description");

        // Verify DB
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var dbMeeting = await db.Meetings.IgnoreQueryFilters().FirstOrDefaultAsync(m => m.Id == meeting.Id);
        dbMeeting.Should().NotBeNull();
        dbMeeting!.Title.Should().Be("Updated Title");
        dbMeeting.Description.Should().Be("Original Description");
    }

    [Fact]
    public async Task UpdateMeeting_HostUpdatesDescriptionOnly_ShouldReturnOkWithUpdatedDescription()
    {
        // Arrange
        var meeting = await SeedMeetingAsync(TestOrganizationId, title: "Test Meeting", description: "Old Description");
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);

        var request = new UpdateMeetingRequest(null, "New Description", null, null, null);

        // Act
        var response = await Client.PutAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings/{meeting.Id}", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<MeetingResponse>();
        result.Should().NotBeNull();
        result!.Title.Should().Be("Test Meeting");
        result.Description.Should().Be("New Description");

        // Verify DB
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var dbMeeting = await db.Meetings.IgnoreQueryFilters().FirstOrDefaultAsync(m => m.Id == meeting.Id);
        dbMeeting.Should().NotBeNull();
        dbMeeting!.Description.Should().Be("New Description");
    }

    [Fact]
    public async Task UpdateMeeting_HostUpdatesScheduleTimes_ShouldReturnOkWithUpdatedTimes()
    {
        // Arrange
        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);

        var newStart = DateTime.UtcNow.AddDays(5);
        var newEnd = newStart.AddHours(2);
        var request = new UpdateMeetingRequest(null, null, newStart, newEnd, null);

        // Act
        var response = await Client.PutAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings/{meeting.Id}", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<MeetingResponse>();
        result.Should().NotBeNull();
        result!.ScheduledStartUtc.Should().BeCloseTo(newStart, TimeSpan.FromSeconds(1));
        result.ScheduledEndUtc.Should().BeCloseTo(newEnd, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task UpdateMeeting_HostUpdatesAllFields_ShouldReturnOkWithAllFieldsUpdated()
    {
        // Arrange
        var meeting = await SeedMeetingAsync(TestOrganizationId, title: "Old Title", description: "Old Desc");
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);

        var newStart = DateTime.UtcNow.AddDays(10);
        var newEnd = newStart.AddHours(3);
        var request = new UpdateMeetingRequest("New Title", "New Desc", newStart, newEnd, null);

        // Act
        var response = await Client.PutAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings/{meeting.Id}", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<MeetingResponse>();
        result.Should().NotBeNull();
        result!.Title.Should().Be("New Title");
        result.Description.Should().Be("New Desc");
        result.ScheduledStartUtc.Should().BeCloseTo(newStart, TimeSpan.FromSeconds(1));
        result.ScheduledEndUtc.Should().BeCloseTo(newEnd, TimeSpan.FromSeconds(1));
        result.Status.Should().Be(MeetingStatus.Scheduled);
    }

    // ───────────────────────────────────────────────────────────────
    // Tag update cases
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateMeeting_HostReplacesTagsWithNewTags_ShouldReturnOkWithNewTags()
    {
        // Arrange
        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);

        var oldTag = await SeedMeetingTagAsync(TestOrganizationId, "Old Tag");
        await SeedMeetingMeetingTagAsync(meeting.Id, oldTag.Id);

        var newTag = await SeedMeetingTagAsync(TestOrganizationId, "New Tag");
        var request = new UpdateMeetingRequest(null, null, null, null, new List<Guid> { newTag.Id });

        // Act
        var response = await Client.PutAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings/{meeting.Id}", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<MeetingResponse>();
        result.Should().NotBeNull();
        result!.TagIds.Should().ContainSingle().Which.Should().Be(newTag.Id);
        result.TagIds.Should().NotContain(oldTag.Id);
    }

    [Fact]
    public async Task UpdateMeeting_HostUpdatesWithEmptyTagIds_ShouldClearAllTags()
    {
        // Arrange
        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);

        var tag = await SeedMeetingTagAsync(TestOrganizationId, "Existing Tag");
        await SeedMeetingMeetingTagAsync(meeting.Id, tag.Id);

        var request = new UpdateMeetingRequest(null, null, null, null, new List<Guid>());

        // Act
        var response = await Client.PutAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings/{meeting.Id}", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<MeetingResponse>();
        result.Should().NotBeNull();
        result!.TagIds.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateMeeting_HostUpdatesWithNullTagIds_ShouldPreserveExistingTags()
    {
        // Arrange
        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);

        var tag = await SeedMeetingTagAsync(TestOrganizationId, "Preserved Tag");
        await SeedMeetingMeetingTagAsync(meeting.Id, tag.Id);

        // TagIds is null — should not touch existing tags
        var request = new UpdateMeetingRequest("Updated Title", null, null, null, null);

        // Act
        var response = await Client.PutAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings/{meeting.Id}", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<MeetingResponse>();
        result.Should().NotBeNull();
        result!.TagIds.Should().ContainSingle().Which.Should().Be(tag.Id);
    }

    [Fact]
    public async Task UpdateMeeting_TagIdsContainsNonExistentTag_ShouldReturnNotFound()
    {
        // Arrange
        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);

        var nonExistentTagId = Guid.NewGuid();
        var request = new UpdateMeetingRequest(null, null, null, null, new List<Guid> { nonExistentTagId });

        // Act
        var response = await Client.PutAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings/{meeting.Id}", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.NotFound);
        error.Title.Should().Be("One or more specified tags were not found or are inactive.");
    }

    // ───────────────────────────────────────────────────────────────
    // Authorization / access failures
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateMeeting_Unauthenticated_ShouldReturnUnauthorized()
    {
        // Arrange
        var meetingId = Guid.NewGuid();
        var request = new UpdateMeetingRequest("Title", null, null, null, null);

        var unauthenticatedClient = Factory.CreateClient();
        unauthenticatedClient.DefaultRequestHeaders.Authorization = null;

        // Act
        var response = await unauthenticatedClient.PutAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings/{meetingId}", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UpdateMeeting_RouteOrgIdDiffersFromJwtOrgId_ShouldReturnForbidden()
    {
        // Arrange
        var otherUserId = Guid.NewGuid();
        var otherOrgId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(otherUserId, otherOrgId);
        var otherClient = CreateAuthenticatedClient(otherUserId, otherOrgId);

        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);

        var request = new UpdateMeetingRequest("Title", null, null, null, null);

        // Act — otherClient's JWT has otherOrgId, but route has TestOrganizationId
        var response = await otherClient.PutAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings/{meeting.Id}", request);

        // Assert — EnforceOrgAccess returns bare 403 ForbidResult, no body
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task UpdateMeeting_CallerNotParticipant_ShouldReturnForbidden()
    {
        // Arrange
        var hostId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(hostId, TestOrganizationId);

        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, hostId, MeetingRole.Host);
        // TestUserId is NOT added as a participant

        var request = new UpdateMeetingRequest("Updated Title", null, null, null, null);

        // Act
        var response = await Client.PutAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings/{meeting.Id}", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.Forbidden);
        error.Title.Should().Be("Caller is not a host of the meeting.");
    }

    [Fact]
    public async Task UpdateMeeting_CallerIsCoHost_ShouldReturnForbidden()
    {
        // Arrange
        var hostId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(hostId, TestOrganizationId);

        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, hostId, MeetingRole.Host);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.CoHost);

        var request = new UpdateMeetingRequest("Updated Title", null, null, null, null);

        // Act
        var response = await Client.PutAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings/{meeting.Id}", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.Forbidden);
        error.Title.Should().Be("Caller is not a host of the meeting.");
    }

    [Fact]
    public async Task UpdateMeeting_CallerIsParticipantRole_ShouldReturnForbidden()
    {
        // Arrange
        var hostId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(hostId, TestOrganizationId);

        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, hostId, MeetingRole.Host);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Participant);

        var request = new UpdateMeetingRequest("Updated Title", null, null, null, null);

        // Act
        var response = await Client.PutAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings/{meeting.Id}", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.Forbidden);
        error.Title.Should().Be("Caller is not a host of the meeting.");
    }

    [Fact]
    public async Task UpdateMeeting_CallerIsObserver_ShouldReturnForbidden()
    {
        // Arrange
        var hostId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(hostId, TestOrganizationId);

        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, hostId, MeetingRole.Host);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Observer);

        var request = new UpdateMeetingRequest("Updated Title", null, null, null, null);

        // Act
        var response = await Client.PutAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings/{meeting.Id}", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.Forbidden);
        error.Title.Should().Be("Caller is not a host of the meeting.");
    }

    // ───────────────────────────────────────────────────────────────
    // Business rule failures
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateMeeting_MeetingDoesNotExist_ShouldReturnNotFound()
    {
        // Arrange
        var nonExistentMeetingId = Guid.NewGuid();
        var request = new UpdateMeetingRequest("Title", null, null, null, null);

        // Act
        var response = await Client.PutAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings/{nonExistentMeetingId}", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.NotFound);
        error.Title.Should().Be("The specified meeting was not found.");
    }

    [Fact]
    public async Task UpdateMeeting_MeetingIsCancelled_ShouldReturnBadRequest()
    {
        // Arrange
        var meeting = await SeedMeetingAsync(TestOrganizationId, status: MeetingStatus.Cancelled);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);

        var request = new UpdateMeetingRequest("Updated Title", null, null, null, null);

        // Act
        var response = await Client.PutAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings/{meeting.Id}", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.BadRequest);
        error.Title.Should().Be("The operation is invalid for the current meeting status.");
    }

    [Fact]
    public async Task UpdateMeeting_MeetingIsCompleted_ShouldReturnBadRequest()
    {
        // Arrange
        var meeting = await SeedMeetingAsync(TestOrganizationId, status: MeetingStatus.Completed);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);

        var request = new UpdateMeetingRequest("Updated Title", null, null, null, null);

        // Act
        var response = await Client.PutAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings/{meeting.Id}", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.BadRequest);
        error.Title.Should().Be("The operation is invalid for the current meeting status.");
    }

    [Fact]
    public async Task UpdateMeeting_MeetingIsInProgress_ShouldReturnBadRequest()
    {
        // Arrange
        var meeting = await SeedMeetingAsync(TestOrganizationId, status: MeetingStatus.InProgress);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);

        var request = new UpdateMeetingRequest("Updated Title", null, null, null, null);

        // Act
        var response = await Client.PutAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings/{meeting.Id}", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.BadRequest);
        error.Title.Should().Be("The operation is invalid for the current meeting status.");
    }

    // ───────────────────────────────────────────────────────────────
    // Validation failures
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateMeeting_EmptyTitle_ShouldReturnBadRequest()
    {
        // Arrange
        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);

        var request = new UpdateMeetingRequest("", null, null, null, null);

        // Act
        var response = await Client.PutAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings/{meeting.Id}", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.BadRequest);
        error.Errors.Should().NotBeNull();
        error.Errors.Should().ContainKey("Title");
    }

    [Fact]
    public async Task UpdateMeeting_TitleExceeds200Chars_ShouldReturnBadRequest()
    {
        // Arrange
        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);

        var longTitle = new string('A', 201);
        var request = new UpdateMeetingRequest(longTitle, null, null, null, null);

        // Act
        var response = await Client.PutAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings/{meeting.Id}", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.BadRequest);
        error.Errors.Should().NotBeNull();
        error.Errors.Should().ContainKey("Title");
    }

    [Fact]
    public async Task UpdateMeeting_DescriptionExceeds2000Chars_ShouldReturnBadRequest()
    {
        // Arrange
        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);

        var longDescription = new string('A', 2001);
        var request = new UpdateMeetingRequest(null, longDescription, null, null, null);

        // Act
        var response = await Client.PutAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings/{meeting.Id}", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.BadRequest);
        error.Errors.Should().NotBeNull();
        error.Errors.Should().ContainKey("Description");
    }

    [Fact]
    public async Task UpdateMeeting_ScheduledStartUtcInThePast_ShouldReturnBadRequest()
    {
        // Arrange
        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);

        var pastStart = DateTime.UtcNow.AddDays(-1);
        var futureEnd = DateTime.UtcNow.AddDays(2);
        var request = new UpdateMeetingRequest(null, null, pastStart, futureEnd, null);

        // Act
        var response = await Client.PutAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings/{meeting.Id}", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.BadRequest);
        error.Errors.Should().NotBeNull();
        error.Errors.Should().ContainKey("ScheduledStartUtc");
    }

    [Fact]
    public async Task UpdateMeeting_ScheduledEndUtcBeforeStartUtc_ShouldReturnBadRequest()
    {
        // Arrange
        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);

        var start = DateTime.UtcNow.AddDays(5);
        var endBeforeStart = start.AddHours(-1);
        var request = new UpdateMeetingRequest(null, null, start, endBeforeStart, null);

        // Act
        var response = await Client.PutAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings/{meeting.Id}", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.BadRequest);
        error.Errors.Should().NotBeNull();
        error.Errors.Should().ContainKey("ScheduledEndUtc");
    }

    // ───────────────────────────────────────────────────────────────
    // Cross-tenant
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateMeeting_CrossTenantCaller_ShouldReturnNotFoundOrForbidden()
    {
        // Arrange
        var otherUserId = Guid.NewGuid();
        var otherOrgId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(otherUserId, otherOrgId);
        var otherClient = CreateAuthenticatedClient(otherUserId, otherOrgId);

        // Meeting belongs to TestOrganizationId
        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);

        var request = new UpdateMeetingRequest("Hijacked Title", null, null, null, null);

        // Act — caller's JWT org (otherOrgId) matches route org (otherOrgId), but meeting belongs to TestOrganizationId
        var response = await otherClient.PutAsJsonAsync($"/api/organizations/{otherOrgId}/meetings/{meeting.Id}", request);

        // Assert — global query filter won't find the meeting, so 404; or 403 if it leaks
        response.StatusCode.Should().Match(s => s == HttpStatusCode.NotFound || s == HttpStatusCode.Forbidden);
    }
}
