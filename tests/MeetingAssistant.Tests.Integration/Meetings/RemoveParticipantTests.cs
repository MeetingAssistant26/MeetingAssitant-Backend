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

public class RemoveParticipantTests : IntegrationTestBase
{
    public RemoveParticipantTests(MeetingAssistantWebFactory factory) : base(factory)
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

    // ───────────────────────────────────────────────────────────────
    // Success cases
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task RemoveParticipant_HostRemovesParticipant_ShouldReturnNoContent()
    {
        // Arrange
        var targetUserId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(targetUserId, TestOrganizationId);

        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, targetUserId, MeetingRole.Participant);

        // Act
        var response = await Client.DeleteAsync($"/api/meetings/{meeting.Id}/participants/{targetUserId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var dbParticipant = await db.MeetingParticipants.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.MeetingId == meeting.Id && p.UserId == targetUserId);
        dbParticipant.Should().BeNull();
    }

    [Fact]
    public async Task RemoveParticipant_HostRemovesObserver_ShouldReturnNoContent()
    {
        // Arrange
        var targetUserId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(targetUserId, TestOrganizationId);

        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, targetUserId, MeetingRole.Observer);

        // Act
        var response = await Client.DeleteAsync($"/api/meetings/{meeting.Id}/participants/{targetUserId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var dbParticipant = await db.MeetingParticipants.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.MeetingId == meeting.Id && p.UserId == targetUserId);
        dbParticipant.Should().BeNull();
    }

    [Fact]
    public async Task RemoveParticipant_CoHostRemovesParticipant_ShouldReturnNoContent()
    {
        // Arrange
        var coHostId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(coHostId, TestOrganizationId);
        var coHostClient = CreateAuthenticatedClient(coHostId, TestOrganizationId);

        var targetUserId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(targetUserId, TestOrganizationId);

        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, coHostId, MeetingRole.CoHost);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, targetUserId, MeetingRole.Participant);

        // Act
        var response = await coHostClient.DeleteAsync($"/api/meetings/{meeting.Id}/participants/{targetUserId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var dbParticipant = await db.MeetingParticipants.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.MeetingId == meeting.Id && p.UserId == targetUserId);
        dbParticipant.Should().BeNull();
    }

    [Fact]
    public async Task RemoveParticipant_CoHostRemovesObserver_ShouldReturnNoContent()
    {
        // Arrange
        var coHostId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(coHostId, TestOrganizationId);
        var coHostClient = CreateAuthenticatedClient(coHostId, TestOrganizationId);

        var targetUserId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(targetUserId, TestOrganizationId);

        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, coHostId, MeetingRole.CoHost);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, targetUserId, MeetingRole.Observer);

        // Act
        var response = await coHostClient.DeleteAsync($"/api/meetings/{meeting.Id}/participants/{targetUserId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var dbParticipant = await db.MeetingParticipants.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.MeetingId == meeting.Id && p.UserId == targetUserId);
        dbParticipant.Should().BeNull();
    }

    [Fact]
    public async Task RemoveParticipant_HostRemovesAnotherHostWhenMultipleHostsExist_ShouldReturnNoContent()
    {
        // Arrange
        var targetHostId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(targetHostId, TestOrganizationId);

        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, targetHostId, MeetingRole.Host);

        // Act
        var response = await Client.DeleteAsync($"/api/meetings/{meeting.Id}/participants/{targetHostId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var dbParticipant = await db.MeetingParticipants.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.MeetingId == meeting.Id && p.UserId == targetHostId);
        dbParticipant.Should().BeNull();
    }

    // ───────────────────────────────────────────────────────────────
    // Authorization failures
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task RemoveParticipant_RegularParticipantTriesToRemove_ShouldReturnForbidden()
    {
        // Arrange
        var targetUserId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(targetUserId, TestOrganizationId);

        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Participant);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, targetUserId, MeetingRole.Observer);

        // Act
        var response = await Client.DeleteAsync($"/api/meetings/{meeting.Id}/participants/{targetUserId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.Forbidden);
        error.Title.Should().Be("Caller is not a host of the meeting.");

        // Verify participant still exists
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var dbParticipant = await db.MeetingParticipants.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.MeetingId == meeting.Id && p.UserId == targetUserId);
        dbParticipant.Should().NotBeNull();
    }

    [Fact]
    public async Task RemoveParticipant_ObserverTriesToRemove_ShouldReturnForbidden()
    {
        // Arrange
        var targetUserId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(targetUserId, TestOrganizationId);

        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Observer);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, targetUserId, MeetingRole.Participant);

        // Act
        var response = await Client.DeleteAsync($"/api/meetings/{meeting.Id}/participants/{targetUserId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.Forbidden);
        error.Title.Should().Be("Caller is not a host of the meeting.");

        // Verify participant still exists
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var dbParticipant = await db.MeetingParticipants.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.MeetingId == meeting.Id && p.UserId == targetUserId);
        dbParticipant.Should().NotBeNull();
    }

    [Fact]
    public async Task RemoveParticipant_CallerNotPartOfMeeting_ShouldReturnForbidden()
    {
        // Arrange
        var targetUserId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(targetUserId, TestOrganizationId);

        var meeting = await SeedMeetingAsync(TestOrganizationId);
        // TestUserId is NOT added to MeetingParticipants
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, targetUserId, MeetingRole.Participant);

        // Act
        var response = await Client.DeleteAsync($"/api/meetings/{meeting.Id}/participants/{targetUserId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.Forbidden);
        error.Title.Should().Be("You are not a participant of this meeting.");

        // Verify participant still exists
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var dbParticipant = await db.MeetingParticipants.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.MeetingId == meeting.Id && p.UserId == targetUserId);
        dbParticipant.Should().NotBeNull();
    }

    [Fact]
    public async Task RemoveParticipant_CoHostTriesToRemoveHost_ShouldReturnForbidden()
    {
        // Arrange
        var coHostId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(coHostId, TestOrganizationId);
        var coHostClient = CreateAuthenticatedClient(coHostId, TestOrganizationId);

        var hostId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(hostId, TestOrganizationId);

        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, hostId, MeetingRole.Host);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, coHostId, MeetingRole.CoHost);

        // Act
        var response = await coHostClient.DeleteAsync($"/api/meetings/{meeting.Id}/participants/{hostId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.Forbidden);
        error.Title.Should().Be("CoHosts can only remove Participants and Observers.");

        // Verify host still exists
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var dbParticipant = await db.MeetingParticipants.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.MeetingId == meeting.Id && p.UserId == hostId);
        dbParticipant.Should().NotBeNull();
    }

    [Fact]
    public async Task RemoveParticipant_CoHostTriesToRemoveAnotherCoHost_ShouldReturnForbidden()
    {
        // Arrange
        var coHostCallerId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(coHostCallerId, TestOrganizationId);
        var coHostClient = CreateAuthenticatedClient(coHostCallerId, TestOrganizationId);

        var coHostTargetId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(coHostTargetId, TestOrganizationId);

        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, coHostCallerId, MeetingRole.CoHost);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, coHostTargetId, MeetingRole.CoHost);

        // Act
        var response = await coHostClient.DeleteAsync($"/api/meetings/{meeting.Id}/participants/{coHostTargetId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.Forbidden);
        error.Title.Should().Be("CoHosts can only remove Participants and Observers.");

        // Verify target CoHost still exists
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var dbParticipant = await db.MeetingParticipants.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.MeetingId == meeting.Id && p.UserId == coHostTargetId);
        dbParticipant.Should().NotBeNull();
    }

    // ───────────────────────────────────────────────────────────────
    // Business rule failures
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task RemoveParticipant_LastHostRemoval_ShouldReturnForbidden()
    {
        // Arrange
        var targetHostId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(targetHostId, TestOrganizationId);

        var meeting = await SeedMeetingAsync(TestOrganizationId);
        // Only one Host exists — TestUserId is Host, targetHostId is also Host but we want to test removing the LAST host
        // Setup: TestUserId is Host (caller), targetHostId is the only OTHER host
        // Actually, to test "last host", we need ONE host total. The caller must be host to attempt removal,
        // and the target must also be a host. With 2 hosts, removing one is allowed.
        // To test "cannot remove the LAST host", the target must be the sole host.
        // But caller must also be Host or CoHost per earlier checks.
        // Scenario: caller is Host, target is also Host, but they are the SAME host — no, target != caller in the route.
        // Correct scenario: caller (Host) tries to remove the only other Host when there are exactly 2, making it the "last" — no, that leaves 1.
        // Reconsidering: "last Host (HostCount <= 1)" means if the target is a Host and after removal there'd be 0 hosts.
        // So: only 1 Host total (the target), but caller must be Host or CoHost.
        // If caller is CoHost, they can't remove Host (RemovalForbidden triggers first).
        // If caller is Host AND target is Host AND there's only 1 host... caller IS the host, so target == caller? No, route has separate userId.
        // The scenario must be: 2 hosts exist, caller is one, target is the other — but then HostCount=2, removing one leaves 1, which is fine.
        // Actually wait — the check is "If target is Host and HostCount <= 1". This means if there's only 1 host total and that host is the target.
        // But if there's only 1 host and that's the target, the caller can't be that host (they'd be removing themselves via a different userId).
        // The caller must still be Host or CoHost. If caller is a SECOND Host, then HostCount >= 2 and the check won't trigger.
        // So the only way: caller is CoHost, target is the only Host. But then check 5 (RemovalForbidden) triggers first.
        // Unless... caller is Host AND target is Host AND HostCount == 1: impossible because if caller is Host, HostCount >= 1, and if target is also Host and different person, HostCount >= 2.
        // Let me re-read: "If target is Host and they are the last Host (HostCount <= 1)".
        // Hmm, maybe the scenario is that the Host tries to remove THEMSELVES? But that would work only if the route allows self-removal.
        // Most likely: a Host can remove themselves, and the check prevents it if they're the last host.
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);
        // TestUserId is the ONLY host — attempt to remove self

        // Act
        var response = await Client.DeleteAsync($"/api/meetings/{meeting.Id}/participants/{TestUserId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.Forbidden);
        error.Title.Should().Be("Cannot remove the last host of the meeting.");

        // Verify host still exists
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var dbParticipant = await db.MeetingParticipants.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.MeetingId == meeting.Id && p.UserId == TestUserId);
        dbParticipant.Should().NotBeNull();
    }

    [Fact]
    public async Task RemoveParticipant_TargetNotInMeeting_ShouldReturnNotFound()
    {
        // Arrange
        var nonParticipantId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(nonParticipantId, TestOrganizationId);

        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);
        // nonParticipantId is NOT added to MeetingParticipants

        // Act
        var response = await Client.DeleteAsync($"/api/meetings/{meeting.Id}/participants/{nonParticipantId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.NotFound);
        error.Title.Should().Be("The specified participant was not found in the meeting.");
    }

    // ───────────────────────────────────────────────────────────────
    // Not found / auth
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task RemoveParticipant_MeetingDoesNotExist_ShouldReturnNotFound()
    {
        // Arrange
        var nonExistingMeetingId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();

        // Act
        var response = await Client.DeleteAsync($"/api/meetings/{nonExistingMeetingId}/participants/{targetUserId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.NotFound);
        error.Title.Should().Be("The specified meeting was not found.");
    }

    [Fact]
    public async Task RemoveParticipant_Unauthenticated_ShouldReturnUnauthorized()
    {
        // Arrange
        var meetingId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();

        var unauthenticatedClient = Factory.CreateClient();
        unauthenticatedClient.DefaultRequestHeaders.Authorization = null;

        // Act
        var response = await unauthenticatedClient.DeleteAsync($"/api/meetings/{meetingId}/participants/{targetUserId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ───────────────────────────────────────────────────────────────
    // Cross-tenant
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task RemoveParticipant_CrossTenantCaller_ShouldReturnNotFoundOrForbidden()
    {
        // Arrange
        var customUserId = Guid.NewGuid();
        var customOrgId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(customUserId, customOrgId);
        var alternateClient = CreateAuthenticatedClient(customUserId, customOrgId);

        // Meeting belongs to TestOrganizationId
        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);

        var targetUserId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(targetUserId, TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, targetUserId, MeetingRole.Participant);

        // Act
        // Caller's JWT org (customOrgId) differs from meeting's org (TestOrganizationId)
        var response = await alternateClient.DeleteAsync($"/api/meetings/{meeting.Id}/participants/{targetUserId}");

        // Assert
        response.StatusCode.Should().Match(s => s == HttpStatusCode.NotFound || s == HttpStatusCode.Forbidden);
    }

    // ───────────────────────────────────────────────────────────────
    // Idempotency
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task RemoveParticipant_DeleteSameParticipantTwice_SecondCallShouldReturnNotFound()
    {
        // Arrange
        var targetUserId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(targetUserId, TestOrganizationId);

        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, targetUserId, MeetingRole.Participant);

        var url = $"/api/meetings/{meeting.Id}/participants/{targetUserId}";

        // Act — first DELETE
        var firstResponse = await Client.DeleteAsync(url);

        // Assert — first DELETE succeeds
        firstResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Act — second DELETE (same URL)
        var secondResponse = await Client.DeleteAsync(url);

        // Assert — second DELETE returns 404
        secondResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var error = await secondResponse.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.NotFound);
        error.Title.Should().Be("The specified participant was not found in the meeting.");

        // Verify participant is still absent from the DB
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var dbParticipant = await db.MeetingParticipants.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.MeetingId == meeting.Id && p.UserId == targetUserId);
        dbParticipant.Should().BeNull();
    }
}
